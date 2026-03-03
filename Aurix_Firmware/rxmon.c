#include "rxmon.h"
#include "nichia_eth.h"

/* ==================== Internal state ==================== */
static uint8  s_buf[RXMON_FRAME_LEN_BYTES];
static uint32 s_fill    = 0;
/* Parser states:
 * HUNT    : s_sync==0 -> căutăm 0x5D + byte[1] valid (row/parity)
 * COLLECT : s_sync==1 -> colectăm fix 260 B; dacă CRC OK, rămânem LOCKED
 */
static uint8  s_sync   = 0; /* 0=HUNT, 1=COLLECT (LOCKED) */
static uint8  s_gotHdr = 0; /* 0=need 0x5D, 1=await row/parity */
static uint8  s_row    = 0; /* row index al cadrului curent */

/* ==================== CRC16 external (rx_crc.c) ==================== */
#if RXMON_USE_EXTERNAL_CRC16
    #define rxmon_crc16(data, n) ioHwAbsTLD816K_Crc16((data), (n))
#else
    #error "This build expects RXMON_USE_EXTERNAL_CRC16==1 and rx_crc.c in target."
#endif

/* ==================== Row parity helper ==================== */
static uint8 parity_bits_for_row(uint8 row)
{
    uint8 x = (uint8)(row & 0x3F);
    x = (uint8)(x - ((x >> 1) & 0x55));
    x = (uint8)((x & 0x33) + ((x >> 2) & 0x33));
    uint8 pop = (uint8)((x + (x >> 4)) & 0x0F);
    return (pop & 1) ? 0x40u : 0x80u;
}

/* ==================== Global telemetry ==================== */
RxMon g_rxmon = {0};

/* Optional reset (used by Cpu0_Main.c) */
void rxmon_reset(void)
{
    /* Reset counter and FSM in a short sequence (called before the main loop) */
    RxMon z = {0};
    g_rxmon = z;
    s_fill = 0; s_sync = 0; s_gotHdr = 0; s_row = 0;
}

/* ==================== Parser ==================== */
void rxmon_feed(const uint8 *data, uint32 len)
{
    g_rxmon.totalBytes += len;
    g_rxmon.lastLen     = len;
    g_rxmon.readBursts++;

    for (uint32 i = 0; i < len; i++)
    {
        uint8 b = data[i];

        /* ---------- HUNT: detect 0x5D then a valid row/parity ---------- */
        if (!s_sync)
        {
            g_rxmon.bytesOutOfFrames++;

            if (!s_gotHdr)
            {
                if (b == RXMON_HDR_BYTE)
                {
                    s_buf[0] = b;
                    s_fill   = 1;
                    s_gotHdr = 1;   /* next must be row/parity */
                }
                continue;
            }
            else /* expecting row/parity byte right after header */
            {
                uint8 raw   = b;
                uint8 row   = (uint8)(raw & 0x3Fu);
                uint8 pbits = (uint8)(raw & 0xC0u);

                if (pbits == parity_bits_for_row(row))
                {
                    /* LOCK: start collecting a full 260B frame */
                    s_buf[1] = raw;
                    s_row    = row;
                    s_fill   = 2;
                    s_sync   = 1;    /* enter COLLECT (locked) */
                    s_gotHdr = 0;
                    g_rxmon.headersSeen++;
                }
                else
                {
                    g_rxmon.falseStarts++;
                    g_rxmon.rowParityErrors++;
                    s_fill = 0; s_gotHdr = 0; s_sync = 0;
                }
                continue;
            }
        }

        /* ---------- COLLECT (LOCKED): read fixed 260B frames ---------- */
        if (s_fill < RXMON_FRAME_LEN_BYTES)
        {
            s_buf[s_fill++] = b;

            /* Early guard in LOCKED mode */
            if (s_fill == 1)
            {
                if (s_buf[0] != RXMON_HDR_BYTE)
                {
                    g_rxmon.badHeaderInside++;
                    /* lost lock -> re-enter HUNT */
                    s_sync = 0; s_fill = 0; s_gotHdr = 0;
                    continue;
                }
            }
            else if (s_fill == 2)
            {
                uint8 raw   = s_buf[1];
                uint8 row   = (uint8)(raw & 0x3Fu);
                uint8 pbits = (uint8)(raw & 0xC0u);
                if (pbits != parity_bits_for_row(row))
                {
                    g_rxmon.rowParityErrors++;
                    /* lost lock -> re-enter HUNT */
                    s_sync = 0; s_fill = 0; s_gotHdr = 0;
                    continue;
                }
                s_row = row; /* update row for this frame */
            }
        }

        if (s_fill == RXMON_FRAME_LEN_BYTES)
        {
            /* CRC over payload [2..257] */
            uint16 crcCalc = rxmon_crc16(&s_buf[2], RXMON_PIXELS_PER_ROW);
            uint16 crcWire = (uint16)(((uint16)s_buf[258] << 8) | (uint16)s_buf[259]); /* MSB|LSB */

            g_rxmon.lastCrcCalc = crcCalc;
            g_rxmon.lastCrcWire = crcWire;

            if (crcCalc == crcWire)
            {
                /* Frame OK */
                g_rxmon.framesOk++;
                g_rxmon.rowsOk++;
                g_rxmon.lastFrameIdx     = g_rxmon.framesOk;

                /* update byte-accum and derived counters atomically here */
                g_rxmon.framesBytesAccum += RXMON_FRAME_LEN_BYTES;

                /* derived: framesOkByBytes & difference */
                {
                    /* calc pe 64-bit, apoi cast */
                    sint64 diff = (sint64)g_rxmon.framesBytesAccum
                                - (sint64)g_rxmon.framesOk * (sint64)RXMON_FRAME_LEN_BYTES;
                    g_rxmon.framesOkByBytes  = (uint32)(g_rxmon.framesBytesAccum / RXMON_FRAME_LEN_BYTES);
                    g_rxmon.bytesVsCountDiff = (sint32)diff;
                }

                /* legacy mapping */
                g_rxmon.lastStreamId = s_row;

                /* continuity check on valid frames only */
                if (g_rxmon.framesOk > 1)
                {
                    uint8 expected = (uint8)((g_rxmon.lastRowIndex + 1u) % RXMON_ROWS);
                    if (s_row == g_rxmon.lastRowIndex)
                    {
                        g_rxmon.streamIdSame++;
                    }
                    else if (s_row != expected)
                    {
                        g_rxmon.rowContinuityErrors++;
                        g_rxmon.streamIdJumps++;
                    }
                }
                g_rxmon.lastRowIndex = s_row;

                /* ── Push validated row to Ethernet frame assembler ── */
                nichia_eth_push_row(s_row, &s_buf[2]);

                /* snapshots (optional) */
                for (uint32 k = 0; k < RXMON_FRAME_LEN_BYTES; k++)
                    ((volatile uint8*)g_rxmon.lastFrame)[k] = s_buf[k];
                for (uint32 k = 0; k < 16; k++)
                    g_rxmon.lastHead[k] = s_buf[k];
                for (uint32 k = 0; k < 16; k++)
                    g_rxmon.lastTail[k] = s_buf[RXMON_FRAME_LEN_BYTES - 16 + k];

                /* KEEP LOCK after a valid frame */
                s_fill = 0; /* s_sync remains 1 (LOCKED) */
            }
            else
            {
                /* CRC failed -> lose lock, re-enter HUNT */
                g_rxmon.framesCrcBad++;
                g_rxmon.lastBadCrcCalc = crcCalc;
                g_rxmon.lastBadCrcWire = crcWire;
                for (uint32 k = 0; k < 16; k++) g_rxmon.lastBadHead[k] = s_buf[k];
                for (uint32 k = 0; k < 16; k++) g_rxmon.lastBadTail[k] = s_buf[RXMON_FRAME_LEN_BYTES - 16 + k];
                s_sync = 0; s_fill = 0; s_gotHdr = 0;
            }
        }
    }
}
