#ifndef FRAME_ETH_H
#define FRAME_ETH_H

/******************************************************************************
 * frame_eth.h — Unified Frame-over-Ethernet TX for Nichia + Osram
 *
 * Replaces nichia_eth.h/.c with a device-agnostic implementation:
 *   - Same GETH/RGMII/PHY initialisation (KIT_A2G_TC397_5V_TFT)
 *   - Same ethertype (0x88B5) and fragment protocol
 *   - Configurable frame dimensions and magic bytes per device type
 *
 * Nichia: 256×64 = 16384 bytes, magic "NI", 12 fragments/frame
 * Osram:  320×80 = 25600 bytes, magic "OS", 18 fragments/frame
 *
 * Nichia frames are assembled row-by-row (64 push_row calls → 1 frame).
 * Osram frames arrive complete from osram_frame.c (1 push_frame call).
 *
 * Fragment protocol (per Ethernet packet):
 *   [0..5]   Dst MAC (broadcast FF:FF:FF:FF:FF:FF)
 *   [6..11]  Src MAC (locally-administered 02:0A:F0:4E:49:01)
 *   [12..13] EtherType 0x88B5
 *   [14..31] Protocol header (18 bytes, big-endian):
 *     [14..15] Magic (0x4E49="NI" or 0x4F53="OS")
 *     [16..17] Frame sequence number
 *     [18]     Fragment index (0..N-1)
 *     [19]     Fragment count (N)
 *     [20..21] Data offset within frame
 *     [22..23] Pixel bytes in this fragment
 *     [24..25] Frame width
 *     [26..27] Frame height
 *     [28..31] Timestamp (STM ticks, 32-bit)
 *   [32..]   Pixel data (up to 1482 bytes)
 ******************************************************************************/

#include "Ifx_Types.h"

/* ─── Max frame size (Osram worst case) ─── */
#define FE_MAX_FRAME_BYTES        25600u

/* ─── Protocol constants ─── */
#define FE_ETHERTYPE              0x88B5u     /* IEEE 802 local experimental */
#define FE_MAGIC_NICHIA           0x4E49u     /* "NI" */
#define FE_MAGIC_OSRAM            0x4F53u     /* "OS" */
#define FE_HDR_LEN                18u
#define FE_MTU                    1500u
#define FE_MAX_PAYLOAD            (FE_MTU - FE_HDR_LEN)  /* 1482 */

/* ─── GETH / DMA sizing ───
 * IMPORTANT: iLLD allocates fixed-size descriptor rings of
 * IFXGETH_MAX_TX_DESCRIPTORS (default 8) and IFXGETH_MAX_RX_DESCRIPTORS (8).
 * Our data-buffer arrays MUST cover all 8 descriptors:
 *   s_txBuf[ FE_TX_DESCRIPTORS × FE_TX_BUF_SIZE ]  ≥ 8 × stride
 *   s_rxBuf[ FE_RX_DESCRIPTORS × FE_RX_BUF_SIZE ]  ≥ 8 × stride
 * If they are smaller, iLLD places buffer pointers past the end of our
 * array → MAC DMA writes to invalid memory → bus error trap.
 */
#define FE_TX_BUF_SIZE            1536u
#define FE_TX_DESCRIPTORS         8u          /* must match IFXGETH_MAX_TX_DESCRIPTORS */
#define FE_RX_DESCRIPTORS         8u          /* must match IFXGETH_MAX_RX_DESCRIPTORS */
#define FE_RX_BUF_SIZE            1536u       /* must hold a full Ethernet frame (up to 1518 + alignment) */

/* ─── ISR priorities ─── */
#define FE_GETH_TX_ISR_PRIO       0u          /* polled for TX */
#define FE_GETH_RX_ISR_PRIO       0u          /* polled for RX */

/* ─── Device type selector ─── */
typedef enum
{
    FE_DEVICE_NICHIA = 0,
    FE_DEVICE_OSRAM  = 1
} FrameEthDevice;

/* ─── Nichia geometry (for row-by-row assembly) ─── */
#define FE_NICHIA_W               256u
#define FE_NICHIA_H               64u
#define FE_NICHIA_FRAME_BYTES     (FE_NICHIA_W * FE_NICHIA_H)   /* 16384 */

/* ─── Osram geometry ─── */
#define FE_OSRAM_W                320u
#define FE_OSRAM_H                80u
#define FE_OSRAM_FRAME_BYTES      (FE_OSRAM_W * FE_OSRAM_H)     /* 25600 */

/* ─── Packet header (18 bytes, big-endian on wire) ─── */
typedef struct
{
    uint16 magic;
    uint16 frameSeq;
    uint8  fragIdx;
    uint8  fragCnt;
    uint16 dataOffset;
    uint16 dataLen;
    uint16 width;
    uint16 height;
    uint32 timestamp;
} FeHeader;

/* ─── Telemetry ─── */
typedef struct
{
    volatile uint32 framesSent;
    volatile uint32 fragmentsSent;
    volatile uint32 txErrors;
    volatile uint32 phyAddr;
    volatile uint32 phyId;
    volatile uint32 linkUp;
    volatile uint32 initDone;
    volatile uint32 initStep;
    volatile uint32 mdioRawReg2;
    volatile uint32 mdioRawReg3;

    /* Assembly counters */
    volatile uint32 nichiaRowsReceived;
    volatile uint32 nichiaFramesAssembled;
    volatile uint32 osramFramesPushed;
} FeStats;

extern FeStats g_feStats;

/* ─── API ─── */

/**
 * Initialise GETH + PHY for Ethernet TX.
 * @param device  Initial device type (determines magic/dimensions for TX)
 */
void frame_eth_init(FrameEthDevice device);

/**
 * Set the active device type.  Resets frame assembly state.
 * Does NOT reconfigure ASCLIN — that's handled by device_mode.c.
 */
void frame_eth_set_device(FrameEthDevice device);

/**
 * Push a single Nichia row into the frame assembly buffer.
 * Called from rxmon.c on each CRC-valid line.
 * When all 64 rows are received, the frame is marked ready for TX.
 *
 * @param row    Row index 0..63
 * @param pixels Pointer to 256 bytes of Gray8 pixel data
 */
void frame_eth_push_nichia_row(uint8 row, const uint8 *pixels);

/**
 * Push a complete Osram frame (320×80 pixels) for Ethernet TX.
 * Called from osram_frame.c when a valid/complete frame is parsed.
 *
 * @param pixels  Pointer to 25600 bytes of Gray8 pixel data
 * @param len     Pixel data length (must be 25600)
 */
void frame_eth_push_osram_frame(const uint8 *pixels, uint32 len);

/**
 * If a frame is ready, fragment and send it over Ethernet.
 * Non-blocking: returns immediately if nothing to send or TX busy.
 * Call from the main loop.
 *
 * @return TRUE if a frame was sent, FALSE otherwise
 */
boolean frame_eth_send_pending(void);

/**
 * Reset frame assembly state (for device mode switch).
 */
void frame_eth_reset_frame_state(void);

/**
 * Get a pointer to the current Osram assembly buffer for zero-copy writes.
 * osram_frame.c writes pixels directly here instead of double-buffering.
 * The returned pointer is stable until frame_eth_mark_osram_ready() is called.
 */
uint8 *frame_eth_get_assembly_buffer(void);

/**
 * Mark the current assembly buffer as ready for Ethernet TX (Osram mode).
 * Called after all 25600 pixels have been written via the pointer from
 * frame_eth_get_assembly_buffer().  Swaps the double-buffer index so the
 * next frame writes to the alternate buffer.
 */
void frame_eth_mark_osram_ready(void);

#endif /* FRAME_ETH_H */
