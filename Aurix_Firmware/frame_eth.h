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
#include "can_diag.h"

/* ─── Max frame size (Osram worst case) ─── */
#define FE_MAX_FRAME_BYTES        25600u

/* ─── Protocol constants ─── */
#define FE_ETHERTYPE              0x88B5u     /* IEEE 802 local experimental */
#define FE_MAGIC_NICHIA           0x4E49u     /* "NI" */
#define FE_MAGIC_OSRAM            0x4F53u     /* "OS" */
#define FE_MAGIC_COMMAND          0x434Du     /* "CM" — command packet from PC */
#define FE_MAGIC_CAN_DIAG         0x4344u     /* "CD" — CAN diagnostic record */
#define FE_CMD_SET_DEVICE         0x01u       /* Command: set device mode */
#define FE_CMD_DIAG_SNIFF         0x02u       /* Command: start/stop diag sniffing */
#define FE_CMD_SET_ADAPTER        0x03u       /* Command: set adapter mode (control + CAN UART) */
#define FE_CMD_SET_DEFECT_LIST    0x04u       /* Command: set OSRAM defect injection list */
#define FE_CMD_SET_DEFECT_LIST_NICHIA 0x05u   /* Command: set Nichia defect injection list */
#define FE_CMD_CAN_UART_FAULT     0x06u       /* Command: CAN-UART DROP fault */
#define FE_CMD_LVDS_FAULT         0x07u       /* Command: physical LVDS fault */
#define FE_CMD_OSRAM_SEQ_STEP     0x08u       /* Stage one Direct Control step */
#define FE_CMD_OSRAM_SEQ_COMMIT   0x09u       /* Activate staged startup steps */
#define FE_CMD_NICHIA_SEQ_STEP    0x0Au       /* Stage one Nichia Direct Control step */
#define FE_CMD_NICHIA_SEQ_COMMIT  0x0Bu       /* Activate staged Nichia startup steps */
#define FE_CMD_NICHIA_SEQ_HARDCODED 0x0Cu     /* Start built-in Nichia startup table */
#define FE_CMD_AVTP_SOURCE        0x0Du       /* Command: lock AVTP ingest to one source MAC */
#define FE_HDR_LEN                18u
#define FE_DIAG_HDR_LEN           8u
/* v2 payload: 22 fixed bytes + 72 raw UART bytes = 94 bytes
 * Fixed: ts(4)+addr(2)+rspDly(2)+ifDly(2)+val(4)+crc(4)+devId(1)+op(1)+status(1)+rawLen(1) = 22 */
#define FE_DIAG_PAYLOAD_FIXED     22u
#define FE_DIAG_PAYLOAD_RAW_MAX   72u
#define FE_DIAG_PAYLOAD_LEN       (FE_DIAG_PAYLOAD_FIXED + FE_DIAG_PAYLOAD_RAW_MAX)
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
#define FE_TX_BUF_SIZE            2576u       /* match Infineon lwIP examples: 2560 + 14 + 2 */
#define FE_TX_DESCRIPTORS         8u          /* must match IFXGETH_MAX_TX_DESCRIPTORS */
#define FE_RX_DESCRIPTORS         32u         /* must match IFXGETH_MAX_RX_DESCRIPTORS (Ifx_Cfg.h) */
#define FE_RX_BUF_SIZE            1536u       /* must hold a full Ethernet frame (up to 1518 + alignment) */

/* TX diagnostic reason codes exposed through g_feStats.txLastFailReason. */
#define FE_TX_FAIL_NONE           0u
#define FE_TX_FAIL_LINK           1u
#define FE_TX_FAIL_NO_BUFFER      2u
#define FE_TX_FAIL_TIMEOUT        3u

/* ─── ISR priorities ─── */
#define FE_GETH_TX_ISR_PRIO       10u         /* non-zero: required for standalone TX! */
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
    volatile uint32 txNoBuffer;
    volatile uint32 txTimeouts;
    volatile uint32 txRecoveries;
    volatile uint32 txWakeups;
    volatile uint32 txFramesQueued;
    volatile uint32 txReadyDropped;
    volatile uint32 txRetries;
    volatile uint32 txLastFailReason;
    volatile uint32 txLastFailFrag;
    volatile uint32 linkTransitions;
    volatile uint32 txDmaStatus;
    volatile uint32 txDescOwn;
    volatile uint32 phyBmsr;
    volatile uint32 phyAnar;
    volatile uint32 phyAnlpar;
    volatile uint32 phyGbCtrl;
    volatile uint32 phyGbStatus;
    volatile uint32 macCfg;
    volatile uint32 macSynced;
    volatile uint32 phyLineSpeedMbps;
    volatile uint32 phyDuplexFull;
    volatile uint32 phyAddr;
    volatile uint32 phyId;
    volatile uint32 linkUp;
    volatile uint32 initDone;
    volatile uint32 initStep;
    volatile uint32 mdioRawReg2;
    volatile uint32 mdioRawReg3;
    volatile uint32 diagRecordsSent;
    volatile uint32 diagTxErrors;

    /* Assembly counters */
    volatile uint32 nichiaRowsReceived;
    volatile uint32 nichiaFramesAssembled;
    volatile uint32 osramFramesPushed;

    /* Command RX telemetry */
    volatile uint32 cmdPacketsReceived;    /* Total command packets (magic "CM") */
    volatile uint32 cmdSetDeviceReceived;  /* SET_DEVICE commands received */
    volatile uint32 cmdSetDeviceApplied;   /* SET_DEVICE successfully applied */
    volatile uint32 cmdSetDeviceIgnoredDuringLvds;
    volatile uint32 cmdSetAdapterIgnoredDuringLvds;
    volatile uint32 cmdSetAdapterIgnoredDuringCanUart;
    volatile uint32 cmdLvdsFaultReceived;  /* LVDS fault packets received */
    volatile uint32 cmdLvdsFaultApplied;   /* LVDS START packets accepted */
    volatile uint32 cmdLvdsFaultRejected;  /* LVDS START packets rejected */
    volatile uint32 cmdLvdsFaultCleared;   /* LVDS CLEAR packets received */

    /* RX poll health (command path robustness) */
    volatile uint32 rxPollBudgetHits;      /* poll loop hit per-call budget */
    volatile uint32 rxNullBuffers;         /* getReceiveBuffer returned NULL */
    volatile uint32 rxRecoveries;          /* RX descriptor ring recoveries  */
    volatile uint32 rxDmaStatus;           /* DMA CH0 STATUS snapshot        */
    volatile uint32 rxNoProgressEvents;    /* prolonged no-progress events   */

    /* MAC/MTL-level RX health (upstream of the DMA descriptor ring).
     * These catch a command freeze that stalls at the MAC/FIFO stage
     * (never reaches the DMA, so rxDmaStatus/RBU never reflects it).
     * MAC_DEBUG.RPESTS/RFCFCSTS: 0 = idle in both fields when healthy.
     * rxFifoOverflowPackets: cumulative HW counter, must stay 0.        */
    volatile uint32 macDebugRpeSts;        /* MAC_DEBUG.RPESTS (0=idle)      */
    volatile uint32 macDebugRfcfcSts;      /* MAC_DEBUG.RFCFCSTS (0=idle)    */
    volatile uint32 rxFifoOverflowPackets; /* GETH_RX_FIFO_OVERFLOW_PACKETS  */
    volatile uint32 macPassAllMulticast;   /* MAC_PACKET_FILTER.PM mirror    */
    volatile uint32 pushSkippedTxBusy;     /* push skipped: buffer in flight */
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
 * Push a complete Nichia frame for Direct Control loopback/display.
 * Does not trigger the camera; Direct Mode synchronizes the camera to LVDS TX.
 *
 * @param pixels 256x64 Gray8 pixels, row-major
 */
void frame_eth_push_nichia_frame(const uint8 *pixels);

/**
 * Push a complete Osram frame (320×80 pixels) for Ethernet TX.
 * Called from osram_frame.c when a valid/complete frame is parsed.
 *
 * @param pixels  Pointer to 25600 bytes of Gray8 pixel data
 * @param len     Pixel data length (must be 25600)
 */
void frame_eth_push_osram_frame(const uint8 *pixels, uint32 len);

/**
 * Decode a completed Direct Control LVDS UART stream and publish its pixels.
 * The stream is copied into the normal Ethernet/display assembly buffer.
 *
 * @param device Target LSM geometry used by the stream
 * @param stream Completed LVDS UART stream from lvds_tx
 * @param len    Number of valid bytes in stream
 */
void frame_eth_push_lvds_stream(FrameEthDevice device,
                                const uint8 *stream,
                                uint32 len);

/**
 * If a frame is ready, fragment and send it over Ethernet.
 * Non-blocking: returns immediately if nothing to send or TX busy.
 * Call from the main loop.
 *
 * @return TRUE if a frame was sent, FALSE otherwise
 */
boolean frame_eth_send_pending(void);

/**
 * If a diagnostic record is pending, send one Ethernet packet.
 * Call from the main loop after frame transport so diagnostics remain secondary.
 */
boolean frame_eth_send_can_diag_pending(void);

/**
 * Reset frame assembly state (for device mode switch).
 */
void frame_eth_reset_frame_state(void);

/**
 * Enable or disable pass-all-multicast in the MAC receive filter.
 *
 * The AVTP pixel stream uses the multicast destination 01:00:5E:16:00:12.
 * With the reset filter value the MAC accepts only its own unicast address and
 * broadcast, so the stream would be dropped.  Direct Control Mode enables this
 * while it is armed and restores the default on exit.  Promiscuous mode is not
 * used: it would also admit unicast traffic of other stations.
 */
void frame_eth_set_pass_all_multicast(boolean enable);

/**
 * Poll for incoming Ethernet command packets.
 * Drains ALL RX buffers to prevent DMA descriptor exhaustion.
 * Recognises magic "CM" (0x434D) on ethertype 0x88B5;
 * handles device mode and physical LVDS fault commands.
 * Call from the main loop.
 */
void frame_eth_poll_rx(void);

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

/**
 * Get a pointer to the last-completed frame for display (read-only).
 * Safe to call from CPU1 while CPU0 is assembling the next frame.
 * Returns NULL if no frame has been completed yet.
 *
 * @param[out] width   Frame width in pixels
 * @param[out] height  Frame height in pixels
 * @param[out] seqNum  Frame sequence number (for change detection)
 * @return Pointer to pixel data (width × height bytes, Gray8), or NULL
 */
const uint8 *frame_eth_get_display_frame(uint16 *width, uint16 *height, uint32 *seqNum);

#endif /* FRAME_ETH_H */
