#ifndef OSRAM_FRAME_H
#define OSRAM_FRAME_H

/******************************************************************************
 * osram_frame.h — Osram UART frame parser
 *
 * Parses the Osram async serial protocol from a raw UART byte stream
 * (delivered via DMA buffers from ASCLIN9).
 *
 * Protocol per packet (25608 bytes):
 *   [0..3]       Header pattern: 0x80, 0xA5, 0xAA, 0x55
 *   [4..25603]   Pixel data: 25600 bytes (320×80, Gray8, row-major)
 *   [25604..25607] CRC-32 (4 bytes, LE, ECU seed 0x800A4A84)
 *
 * Packets arrive at ~4.7 ms intervals (47–50 fps), with occasional
 * 6.5–8.5 ms gaps every 2–3 packets.
 *
 * State machine:
 *   HUNT_H0 → HUNT_H1 → HUNT_H2 → HUNT_H3 → READ_PIXELS → READ_CRC → emit
 *
 * On valid frame: pushes pixel data to frame_eth for Ethernet TX.
 * Frames with CRC mismatch are ALSO forwarded (CRC algorithm may not be
 * perfectly validated yet; pixel data is almost certainly correct on the
 * short shielded LVDS link).
 ******************************************************************************/

#include "Ifx_Types.h"

/* ─── Osram frame geometry ─── */
#define OSRAM_FRAME_WIDTH        320u
#define OSRAM_FRAME_HEIGHT       80u
#define OSRAM_FRAME_PIXELS       (OSRAM_FRAME_WIDTH * OSRAM_FRAME_HEIGHT)  /* 25600 */
#define OSRAM_CRC_LEN            4u
#define OSRAM_HEADER_LEN         4u
#define OSRAM_PACKET_LEN         (OSRAM_HEADER_LEN + OSRAM_FRAME_PIXELS + OSRAM_CRC_LEN) /* 25608 */

/* ─── Osram protocol header bytes (UART stream order) ─── */
/* ECU writes TXDATA = 0x55AAA580 (u32 LE), UART sends LSB byte first. */
#define OSRAM_HDR_BYTE0          0x80u
#define OSRAM_HDR_BYTE1          0xA5u
#define OSRAM_HDR_BYTE2          0xAAu
#define OSRAM_HDR_BYTE3          0x55u

/* ─── Telemetry ─── */
typedef struct
{
    volatile uint32 framesOk;           /**< Frames with valid CRC */
    volatile uint32 framesCrcBad;       /**< Frames with CRC mismatch */
    volatile uint32 headersSeen;        /**< Header patterns detected */
    volatile uint32 bytesOutOfSync;     /**< Bytes discarded while hunting header */
    volatile uint32 lastCrcComputed;    /**< Last computed CRC (debug) */
    volatile uint32 lastCrcReceived;    /**< Last received CRC (debug) */
    volatile uint32 framesPerSecond;    /**< FPS (updated externally) */
    volatile uint32 lastSecondFrames;   /**< Snapshot for FPS calc */
    volatile uint32 selfTestCrc;        /**< CRC of 25600 zero bytes (expect 0x66844BF6) */
    volatile uint32 nonZeroPixels;      /**< Non-zero byte count in last CRC-bad frame */
    volatile uint32 firstNonZeroIdx;    /**< Index of first non-zero pixel (0xFFFFFFFF if none) */
    volatile uint8  lastCrcRawBytes[4]; /**< Raw CRC bytes from last frame [0..3] */
} OsramFrameStats;

extern OsramFrameStats g_osramStats;

/* ─── API ─── */

/**
 * Initialize the Osram frame parser + CRC table.
 * Call once at startup (or on device mode switch to Osram).
 */
void osram_frame_init(void);

/**
 * Reset the parser state machine and counters.
 * Used on device mode switch.
 */
void osram_frame_reset(void);

/**
 * Feed raw UART bytes from a DMA buffer into the parser.
 * Called from the main loop whenever a DMA buffer is completed.
 *
 * Internally uses bulk memcpy for pixel collection (not byte-by-byte)
 * for efficient processing of 2560-byte DMA chunks.
 *
 * @param data  Byte buffer from DMA
 * @param len   Number of bytes to process
 */
void osram_frame_feed(const uint8 *data, uint32 len);

#endif /* OSRAM_FRAME_H */
