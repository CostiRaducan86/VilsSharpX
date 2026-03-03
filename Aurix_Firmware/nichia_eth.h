#ifndef NICHIA_ETH_H
#define NICHIA_ETH_H

/******************************************************************************
 * nichia_eth.h — Nichia Frame over Ethernet (NFE)
 *
 * Assembles 64 rows (256 px each) into a 256×64 frame buffer, then
 * fragments and transmits the frame as raw Ethernet packets using GETH.
 *
 * Protocol:
 *   Ethertype 0x88B5 (IEEE 802 Local Experimental 1)
 *   18-byte header + up to 1482 bytes pixel data per fragment
 *   12 fragments per 16 384-byte frame
 *
 * Called from rxmon.c on each CRC-valid row.
 ******************************************************************************/

#include "Ifx_Types.h"

/* ─── Nichia image geometry ─── */
#define NFE_WIDTH               256u
#define NFE_HEIGHT              64u
#define NFE_FRAME_BYTES         (NFE_WIDTH * NFE_HEIGHT)   /* 16 384 */

/* ─── NFE protocol constants ─── */
#define NFE_ETHERTYPE           0x88B5u     /* IEEE 802 local experimental */
#define NFE_MAGIC               0x4E49u     /* "NI" */
#define NFE_HDR_LEN             18u         /* protocol header size */
#define NFE_MTU                 1500u       /* standard Ethernet MTU */
#define NFE_MAX_PAYLOAD         (NFE_MTU - NFE_HDR_LEN)  /* 1482 */
#define NFE_FRAG_COUNT          12u         /* ceil(16384/1482) */

/* ─── GETH / DMA sizing ─── */
#define NFE_TX_BUF_SIZE         1536u       /* >= NFE_MTU, 32-byte aligned */
#define NFE_TX_DESCRIPTORS      16u         /* at least NFE_FRAG_COUNT + 2 */
#define NFE_RX_DESCRIPTORS      4u          /* we only TX, but iLLD needs >= 1 */
#define NFE_RX_BUF_SIZE         64u         /* minimal, not used */

/* ─── ISR priority for GETH (0 = disabled, we poll for TX complete) ─── */
#define NFE_GETH_TX_ISR_PRIO    0u
#define NFE_GETH_RX_ISR_PRIO    0u

/* ─── NFE packet header (18 bytes, big-endian on wire) ─── */
typedef struct __packed
{
    uint16 magic;           /* 0x4E49 */
    uint16 frameSeq;        /* frame sequence number */
    uint8  fragIdx;         /* fragment index (0..11) */
    uint8  fragCnt;         /* total fragments (12) */
    uint16 dataOffset;      /* byte offset of this fragment within frame */
    uint16 dataLen;         /* pixel bytes in this fragment */
    uint16 width;           /* 256 */
    uint16 height;          /* 64 */
    uint32 timestamp;       /* STM ticks at frame-start (32-bit) */
} NfeHeader;

/* ─── Telemetry ─── */
typedef struct
{
    volatile uint32 framesSent;         /* complete frames sent */
    volatile uint32 fragmentsSent;      /* individual ETH packets sent */
    volatile uint32 txErrors;           /* buffer-not-available errors */
    volatile uint32 rowsReceived;       /* rows pushed via push_row */
    volatile uint32 framesAssembled;    /* complete frames detected */
    volatile uint32 phyAddr;            /* PHY MDIO address found (0..31) */
    volatile uint32 phyId;              /* PHY Identifier (reg 2) */
    volatile uint32 linkUp;             /* 1 = link up (PHY reg 1, bit 2) */
    volatile uint32 initDone;           /* 1 = nichia_eth_init completed */
    volatile uint32 initStep;           /* diagnostic: last completed init step */
    volatile uint32 mdioRawReg2;        /* raw MDIO read of reg 2 at addr 0 (debug) */
    volatile uint32 mdioRawReg3;        /* raw MDIO read of reg 3 at addr 0 (debug) */
} NfeStats;

extern NfeStats g_nfeStats;

/* ─── API ─── */

/**
 * Initialise the GETH peripheral (RGMII, 100 Mbps full-duplex).
 * Configures PHY via MDIO for forced 100 Mbps (no auto-negotiation).
 * Call once from core0_main() after watchdog disable + interrupt enable.
 */
void nichia_eth_init(void);

/**
 * Push a single decoded row into the frame assembly buffer.
 * Called from rxmon.c after CRC validation.
 *
 * @param row    Row index 0..63
 * @param pixels Pointer to 256 bytes of Gray8 pixel data
 */
void nichia_eth_push_row(uint8 row, const uint8 *pixels);

/**
 * If a complete frame has been assembled, fragment and send it over Ethernet.
 * Non-blocking: returns immediately if nothing to send or TX busy.
 * Call from the main loop after rxmon_feed().
 *
 * @return TRUE if a frame was sent, FALSE otherwise.
 */
boolean nichia_eth_send_pending(void);

#endif /* NICHIA_ETH_H */
