#ifndef RXMON_H
#define RXMON_H

#include "Ifx_Types.h"

/* ================= Nichia 256x64 frame profile =================
 * One line packet: 260 bytes total
 * [0] : 0x5D (sync)
 * [1] : rowAddressWithParity = (rowIndex & 0x3F) | parity(bits 7:6)
 * [2..257] : 256 bytes pixel data
 * [258..259] : CRC16 (MSB, LSB) over the 256 pixel bytes only
 */
#define RXMON_HDR_BYTE                (0x5D)
#define RXMON_ROWS                    (64u)
#define RXMON_PIXELS_PER_ROW          (256u)
#define RXMON_FRAME_LEN_BYTES         (2u + RXMON_PIXELS_PER_ROW + 2u) /* 260 */

/* Use the ECU CRC16 provided in rx_crc.c */
#define RXMON_USE_EXTERNAL_CRC16      (1)

#if RXMON_USE_EXTERNAL_CRC16
extern uint16 ioHwAbsTLD816K_Crc16(const uint8* data, const uint32 len);
#endif

typedef struct
{
    volatile uint32 totalBytes;
    volatile uint32 headersSeen;
    volatile uint32 framesOk;
    volatile uint32 framesCrcBad;
    volatile uint32 framesLostSync;
    volatile uint32 bytesOutOfFrames;
    volatile uint32 falseStarts;

    /* Row decoding and continuity */
    volatile uint32 rowsOk;
    volatile uint32 rowParityErrors;
    volatile uint32 rowContinuityErrors;
    volatile uint8  lastRowIndex;

    /* Snapshots */
    volatile uint32 lastLen;
    volatile uint8  lastHead[16];
    volatile uint8  lastTail[16];
    volatile uint16 lastCrcCalc;
    volatile uint16 lastCrcWire;
    volatile uint32 lastFrameIdx;
    volatile uint8  lastFrame[RXMON_FRAME_LEN_BYTES];

    volatile uint8  lastBadHead[16];
    volatile uint8  lastBadTail[16];
    volatile uint16 lastBadCrcCalc;
    volatile uint16 lastBadCrcWire;

    /* Legacy + derived */
    volatile uint32 framesBytesAccum;      /* sum of 260 bytes over framesOk */
    volatile uint32 badHeaderInside;
    volatile uint32 readBursts;
    volatile uint8  lastStreamId;
    volatile uint32 streamIdJumps;
    volatile uint32 streamIdSame;

    /* Derived, calculated atomically when a frame is accepted */
    volatile uint32 framesOkByBytes;       /* = framesBytesAccum / 260 */
    volatile sint32 bytesVsCountDiff;      /* = framesBytesAccum - framesOk*260 */

    /* HW error counters from ASCLIN error ISR */
    volatile uint32 hwParityErr;
    volatile uint32 hwFrameErr;
    volatile uint32 hwOverrunErr;

    /* FPS (updated in main) */
    volatile uint32 framesPerSecond;
    volatile uint32 lastSecondFrames;

} RxMon;

extern RxMon g_rxmon;

/* API */
void rxmon_feed(const uint8 *data, uint32 len);
void rxmon_reset(void);   /* <--- adăugat */

#endif /* RXMON_H */
