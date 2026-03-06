/******************************************************************************
 * osram_frame.c — Osram UART frame parser
 *
 * State machine that hunts for the 4-byte header pattern (0x80,0xA5,0xAA,0x55),
 * collects 25600 pixel bytes, reads 4 CRC bytes, verifies, and pushes the
 * complete frame to frame_eth for Ethernet TX.
 *
 * Uses bulk memcpy for pixel collection (not byte-by-byte) so that 2560-byte
 * DMA chunks are absorbed efficiently.
 *
 * ZERO-COPY: Pixel data is written directly into frame_eth's assembly buffer
 * via frame_eth_get_assembly_buffer(). No local frame buffers are needed.
 * This saves ~51 KB of static RAM.
 *
 * Frames are forwarded to Ethernet regardless of CRC result — the CRC
 * algorithm may not be perfectly validated yet, and pixel data on the short
 * LVDS link is almost certainly correct.
 ******************************************************************************/

#include "osram_frame.h"
#include "osram_crc32.h"
#include "frame_eth.h"
#include <string.h>   /* memcpy */

/* ==================== State machine ==================== */

typedef enum
{
    OSRAM_HUNT_H0 = 0,   /**< Looking for 0x80 */
    OSRAM_HUNT_H1,        /**< Got 0x80, expecting 0xA5 */
    OSRAM_HUNT_H2,        /**< Got 0xA5, expecting 0xAA */
    OSRAM_HUNT_H3,        /**< Got 0xAA, expecting 0x55 */
    OSRAM_READ_PIXELS,    /**< Collecting 25600 pixel bytes */
    OSRAM_READ_CRC        /**< Collecting 4 CRC bytes */
} OsramParserState;

/* ==================== Internal state ==================== */

static OsramParserState s_state = OSRAM_HUNT_H0;

/* No local frame buffers — pixels go directly to frame_eth assembly buffer */

/* Pixel collection position within current frame */
static uint32 s_pixelPos = 0;

/* CRC collection */
static uint8  s_crcBuf[OSRAM_CRC_LEN];
static uint32 s_crcPos = 0;

/* ==================== Telemetry ==================== */

OsramFrameStats g_osramStats;

/* ==================== Implementation ==================== */

void osram_frame_init(void)
{
    osram_crc32_init();
    osram_frame_reset();

    /* CRC self-test: compute CRC for 25600 zero bytes (black frame).
     * Osram CRC-32 of 25600 zeros = 0x66844BF6 (matches Saleae + WinIDEA).
     * Compare with lastCrcReceived (clean frames) to validate algorithm. */
    g_osramStats.selfTestCrc = osram_crc32_selftest_zeros(OSRAM_FRAME_PIXELS);
}

void osram_frame_reset(void)
{
    s_state    = OSRAM_HUNT_H0;
    s_pixelPos = 0;
    s_crcPos   = 0;

    OsramFrameStats z = {0};
    g_osramStats = z;
}

/**
 * Emit a parsed frame to Ethernet.
 * Called after CRC bytes are collected (regardless of CRC result).
 * Pixels are already in frame_eth's assembly buffer (zero-copy).
 */
static void emit_frame(boolean crcOk)
{
    if (crcOk)
    {
        g_osramStats.framesOk++;
    }
    else
    {
        g_osramStats.framesCrcBad++;

        /* Store debug CRC info — pixels are in frame_eth's assembly buffer */
        uint8 *pixels = frame_eth_get_assembly_buffer();
        g_osramStats.lastCrcComputed = osram_crc32_compute(pixels, OSRAM_FRAME_PIXELS);
        g_osramStats.lastCrcReceived = (uint32)s_crcBuf[0]
                                     | ((uint32)s_crcBuf[1] <<  8)
                                     | ((uint32)s_crcBuf[2] << 16)
                                     | ((uint32)s_crcBuf[3] << 24);

        /* Raw CRC bytes for debugger inspection */
        g_osramStats.lastCrcRawBytes[0] = s_crcBuf[0];
        g_osramStats.lastCrcRawBytes[1] = s_crcBuf[1];
        g_osramStats.lastCrcRawBytes[2] = s_crcBuf[2];
        g_osramStats.lastCrcRawBytes[3] = s_crcBuf[3];

        /* Count non-zero pixels to detect data corruption */
        {
            uint32 nz = 0u;
            uint32 firstNz = 0xFFFFFFFFu;
            for (uint32 j = 0u; j < OSRAM_FRAME_PIXELS; j++)
            {
                if (pixels[j] != 0x00u)
                {
                    nz++;
                    if (firstNz == 0xFFFFFFFFu) firstNz = j;
                }
            }
            g_osramStats.nonZeroPixels   = nz;
            g_osramStats.firstNonZeroIdx = firstNz;
        }
    }

    /* Mark frame_eth assembly buffer as ready for TX (swaps double-buffer) */
    frame_eth_mark_osram_ready();
}

void osram_frame_feed(const uint8 *data, uint32 len)
{
    uint32 i = 0;

    while (i < len)
    {
        uint8 b;

        switch (s_state)
        {
        /* ────────── Header hunt: 0x80, 0xA5, 0xAA, 0x55 ────────── */

        case OSRAM_HUNT_H0:
            b = data[i++];
            if (b == OSRAM_HDR_BYTE0)
                s_state = OSRAM_HUNT_H1;
            else
                g_osramStats.bytesOutOfSync++;
            break;

        case OSRAM_HUNT_H1:
            b = data[i++];
            if (b == OSRAM_HDR_BYTE1)
            {
                s_state = OSRAM_HUNT_H2;
            }
            else
            {
                g_osramStats.bytesOutOfSync++;
                s_state = (b == OSRAM_HDR_BYTE0) ? OSRAM_HUNT_H1 : OSRAM_HUNT_H0;
            }
            break;

        case OSRAM_HUNT_H2:
            b = data[i++];
            if (b == OSRAM_HDR_BYTE2)
            {
                s_state = OSRAM_HUNT_H3;
            }
            else
            {
                g_osramStats.bytesOutOfSync++;
                s_state = (b == OSRAM_HDR_BYTE0) ? OSRAM_HUNT_H1 : OSRAM_HUNT_H0;
            }
            break;

        case OSRAM_HUNT_H3:
            b = data[i++];
            if (b == OSRAM_HDR_BYTE3)
            {
                /* Full header matched → start collecting pixels */
                g_osramStats.headersSeen++;
                s_pixelPos = 0;
                s_state    = OSRAM_READ_PIXELS;
            }
            else
            {
                g_osramStats.bytesOutOfSync++;
                s_state = (b == OSRAM_HDR_BYTE0) ? OSRAM_HUNT_H1 : OSRAM_HUNT_H0;
            }
            break;

        /* ────────── Pixel collection (bulk memcpy, zero-copy to frame_eth) ── */

        case OSRAM_READ_PIXELS:
        {
            uint8 *buf        = frame_eth_get_assembly_buffer();
            uint32 remaining  = OSRAM_FRAME_PIXELS - s_pixelPos;
            uint32 available  = len - i;
            uint32 copyLen    = (available < remaining) ? available : remaining;

            memcpy(&buf[s_pixelPos], &data[i], copyLen);
            s_pixelPos += copyLen;
            i          += copyLen;

            if (s_pixelPos >= OSRAM_FRAME_PIXELS)
            {
                s_crcPos = 0;
                s_state  = OSRAM_READ_CRC;
            }
            break;
        }

        /* ────────── CRC collection (4 bytes) ────────── */

        case OSRAM_READ_CRC:
        {
            /* Collect up to 4 CRC bytes from available data */
            uint32 crcRemaining = OSRAM_CRC_LEN - s_crcPos;
            uint32 available    = len - i;
            uint32 copyLen      = (available < crcRemaining) ? available : crcRemaining;

            memcpy(&s_crcBuf[s_crcPos], &data[i], copyLen);
            s_crcPos += copyLen;
            i        += copyLen;

            if (s_crcPos >= OSRAM_CRC_LEN)
            {
                /* Verify CRC against pixels in frame_eth's buffer */
                uint8  *pixels = frame_eth_get_assembly_buffer();
                boolean crcOk  = osram_crc32_verify(pixels, OSRAM_FRAME_PIXELS, s_crcBuf);

                emit_frame(crcOk);

                /* Back to hunting for next header */
                s_state = OSRAM_HUNT_H0;
            }
            break;
        }

        default:
            s_state = OSRAM_HUNT_H0;
            i++;
            break;
        }
    }
}
