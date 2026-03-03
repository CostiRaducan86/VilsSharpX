/******************************************************************************
 * nichia_eth.c — Nichia Frame over Ethernet (NFE)
 *
 * Frame assembly (64 rows → 256×64 buffer) + GETH raw Ethernet TX.
 *
 * Build requirements:
 *   - iLLD Geth/Eth driver (IfxGeth_Eth.h/.c) must be compiled into the project
 *   - RMII pins must match the board (KIT_A2G_TC397_5V_TFT default below)
 *   - PHY must be initialised externally if needed (BCM89811 on A2G adapter)
 *
 * NOTE: The RMII pin assignment below matches the standard KIT_A2G_TC397_5V_TFT
 *       wiring.  If your board uses different pins or a different PHY interface,
 *       adjust the rmiiPins struct and phyInterfaceMode accordingly.
 ******************************************************************************/

#include "nichia_eth.h"
#include "Geth/Eth/IfxGeth_Eth.h"
#include "Geth/Std/IfxGeth.h"
#include "Stm/Std/IfxStm.h"
#include <string.h>  /* memcpy, memset */

/* ==================== MAC addresses ==================== */
static const uint8 s_srcMac[6] = { 0x02, 0xAU, 0xF0, 0x4E, 0x49, 0x01 };  /* locally-administered */
static const uint8 s_dstMac[6] = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };  /* broadcast */

/* ==================== GETH handle & buffers ==================== */
static IfxGeth_Eth s_geth;

/*
 * TX / RX buffers — must be aligned to 4 bytes (iLLD requirement).
 * We allocate NFE_TX_DESCRIPTORS TX buffers of NFE_TX_BUF_SIZE each.
 * For the single TX descriptor chain, iLLD uses txBuffer1StartAddress as
 * the base and txBuffer1Size as the stride to compute per-descriptor addresses.
 */
IFX_ALIGN(32) static uint8 s_txBuf[NFE_TX_DESCRIPTORS * NFE_TX_BUF_SIZE];
IFX_ALIGN(32) static uint8 s_rxBuf[NFE_RX_DESCRIPTORS * NFE_RX_BUF_SIZE];

/* ==================== Frame assembly ==================== */
/*
 * Double-buffer:
 *   s_frameBufA / s_frameBufB — two 16 384-byte frame buffers.
 *   s_assembleIdx — index of the buffer currently being assembled (0 or 1).
 *   When a complete frame is detected, the assembler marks that buffer as
 *   "ready" and switches to the other buffer for the next frame.
 */
static uint8  s_frameBufA[NFE_FRAME_BYTES];
static uint8  s_frameBufB[NFE_FRAME_BYTES];
static uint8 *s_framePtr[2] = { s_frameBufA, s_frameBufB };
static uint8  s_assembleIdx = 0;

/*
 * Row tracking: we use a simple "next expected row" scheme.
 * When row 0 arrives, we start a new frame.  When row 63 arrives (and all
 * previous rows were sequentially received), the frame is complete.
 */
static uint8  s_nextRow       = 0;
static uint8  s_rowCount      = 0;   /* rows received for current frame */
static uint32 s_frameTimestamp = 0;   /* STM at row 0 */
static volatile boolean s_frameReady = FALSE;
static volatile uint8   s_readyIdx   = 0;
static uint16 s_frameSeq = 0;

/* ==================== Telemetry ==================== */
NfeStats g_nfeStats;

/* ==================== RGMII pin configuration ==================== */
/*
 * RGMII pins for KIT_A2G_TC397_5V_TFT (LFBGA292):
 *   TXCLK  : P11.4   (125/25/2.5 MHz from MAC)
 *   TXD0   : P11.3
 *   TXD1   : P11.2
 *   TXD2   : P11.1
 *   TXD3   : P11.0
 *   TXCTL  : P11.6   (TX Enable / Control)
 *   RXCLK  : P11.12  (from PHY)
 *   RXD0   : P11.10
 *   RXD1   : P11.9
 *   RXD2   : P11.8
 *   RXD3   : P11.7
 *   RXCTL  : P11.11  (RX DV / Control)
 *   MDC    : P12.0
 *   MDIO   : P12.1
 *   GREFCLK: P11.5   (125 MHz reference from PHY)
 */
static const IfxGeth_Eth_RgmiiPins s_rgmiiPins = {
    .txClk  = &IfxGeth_TXCLK_P11_4_OUT,
    .txd0   = &IfxGeth_TXD0_P11_3_OUT,
    .txd1   = &IfxGeth_TXD1_P11_2_OUT,
    .txd2   = &IfxGeth_TXD2_P11_1_OUT,
    .txd3   = &IfxGeth_TXD3_P11_0_OUT,
    .txCtl  = &IfxGeth_TXCTL_P11_6_OUT,
    .rxClk  = &IfxGeth_RXCLKA_P11_12_IN,
    .rxd0   = &IfxGeth_RXD0A_P11_10_IN,
    .rxd1   = &IfxGeth_RXD1A_P11_9_IN,
    .rxd2   = &IfxGeth_RXD2A_P11_8_IN,
    .rxd3   = &IfxGeth_RXD3A_P11_7_IN,
    .rxCtl  = &IfxGeth_RXCTLA_P11_11_IN,
    .mdc    = &IfxGeth_MDC_P12_0_OUT,
    .mdio   = &IfxGeth_MDIO_P12_1_INOUT,
    .grefClk = &IfxGeth_GREFCLK_P11_5_IN,
};

/* ==================== Helpers ==================== */

/* Store uint16 big-endian into buffer */
static void put_be16(uint8 *dst, uint16 val)
{
    dst[0] = (uint8)(val >> 8);
    dst[1] = (uint8)(val);
}

/* Store uint32 big-endian into buffer */
static void put_be32(uint8 *dst, uint32 val)
{
    dst[0] = (uint8)(val >> 24);
    dst[1] = (uint8)(val >> 16);
    dst[2] = (uint8)(val >> 8);
    dst[3] = (uint8)(val);
}

/* ==================== GETH initialisation ==================== */

void nichia_eth_init(void)
{
    /*
     * IMPORTANT: config must be static — the struct is ~1 KB
     * (4 TX/RX channels, 4 queues, 4 interrupt configs each).
     * Allocating it on the stack overflows the CSA → CME trap.
     */
    static IfxGeth_Eth_Config config;

    /* Clear stats first so initStep is visible immediately */
    memset((void *)&g_nfeStats, 0, sizeof(g_nfeStats));
    g_nfeStats.initStep = 1; /* step 1: entering init */

    /* Get default config */
    IfxGeth_Eth_initModuleConfig(&config, &MODULE_GETH);
    g_nfeStats.initStep = 2; /* step 2: default config loaded */

    /* PHY interface: RGMII */
    config.phyInterfaceMode = IfxGeth_PhyInterfaceMode_rgmii;

    /* MAC configuration */
    config.mac.lineSpeed    = IfxGeth_LineSpeed_1000Mbps;
    config.mac.duplexMode   = IfxGeth_DuplexMode_fullDuplex;
    config.mac.loopbackMode = IfxGeth_LoopbackMode_disable;
    memcpy(config.mac.macAddress, s_srcMac, 6);

    /* Pin assignment */
    config.pins.rmiiPins  = NULL_PTR;
    config.pins.rgmiiPins = &s_rgmiiPins;
    config.pins.miiPins   = NULL_PTR;

    /* MTL: 1 TX queue, 1 RX queue */
    config.mtl.numOfTxQueues = 1;
    config.mtl.numOfRxQueues = 1;
    config.mtl.txQueue[0].queueEnable    = TRUE;
    config.mtl.txQueue[0].storeAndForward = TRUE;
    config.mtl.txQueue[0].txQueueSize    = IfxGeth_QueueSize_2560Bytes;
    config.mtl.rxQueue[0].queueEnable    = TRUE;
    config.mtl.rxQueue[0].storeAndForward = TRUE;
    config.mtl.rxQueue[0].rxQueueSize    = IfxGeth_QueueSize_256Bytes;
    config.mtl.rxQueue[0].rxDmaChannelMap = IfxGeth_RxDmaChannel_0;

    /* DMA: 1 TX channel, 1 RX channel */
    config.dma.numOfTxChannels = 1;
    config.dma.numOfRxChannels = 1;

    {
        IfxGeth_Index gethInst = IfxGeth_getIndex(&MODULE_GETH);

        config.dma.txChannel[0].channelEnable       = TRUE;
        config.dma.txChannel[0].channelId            = IfxGeth_TxDmaChannel_0;
        config.dma.txChannel[0].txDescrList          = &IfxGeth_Eth_txDescrList[gethInst][0];
        config.dma.txChannel[0].txBuffer1StartAddress = (uint32 *)&s_txBuf[0];
        config.dma.txChannel[0].txBuffer1Size        = NFE_TX_BUF_SIZE;

        config.dma.rxChannel[0].channelEnable       = TRUE;
        config.dma.rxChannel[0].channelId            = IfxGeth_RxDmaChannel_0;
        config.dma.rxChannel[0].rxDescrList          = &IfxGeth_Eth_rxDescrList[gethInst][0];
        config.dma.rxChannel[0].rxBuffer1StartAddress = (uint32 *)&s_rxBuf[0];
        config.dma.rxChannel[0].rxBuffer1Size        = NFE_RX_BUF_SIZE;

        config.dma.txInterrupt[0].channelId = IfxGeth_DmaChannel_0;
        config.dma.txInterrupt[0].priority  = NFE_GETH_TX_ISR_PRIO;
        config.dma.txInterrupt[0].provider  = IfxSrc_Tos_cpu0;

        config.dma.rxInterrupt[0].channelId = IfxGeth_DmaChannel_0;
        config.dma.rxInterrupt[0].priority  = NFE_GETH_RX_ISR_PRIO;
        config.dma.rxInterrupt[0].provider  = IfxSrc_Tos_cpu0;
    }
    g_nfeStats.initStep = 3; /* step 3: config prepared */

    /* Initialise the module */
    IfxGeth_Eth_initModule(&s_geth, &config);
    g_nfeStats.initStep = 4; /* step 4: initModule done */

    /* Enable transmitter and receiver */
    IfxGeth_Eth_startTransmitters(&s_geth, 1);
    g_nfeStats.initStep = 5; /* step 5: TX started */

    IfxGeth_Eth_startReceivers(&s_geth, 1);
    g_nfeStats.initStep = 6; /* step 6: RX started */

    /* Brief delay (~50 ms) for PHY power-up before MDIO scan */
    {
        volatile uint32 d = 10000000u;
        while (d--) {}
    }
    g_nfeStats.initStep = 7; /* step 7: PHY power-up delay done */

    /* ── PHY MDIO: scan for PHY, initialise RTL8211-style ── */
    {
        uint8  phyAddr = 0;
        uint32 phyId   = 0;
        uint8  found   = 0;
        uint8  a;

        /* Debug: raw read of addr 0, reg 2 & 3 (capture even if 0x0000) */
        {
            uint32 raw2 = 0xDEADu, raw3 = 0xDEADu;
            IfxGeth_phy_Clause22_readMDIORegister(0u, 2u, &raw2);
            IfxGeth_phy_Clause22_readMDIORegister(0u, 3u, &raw3);
            g_nfeStats.mdioRawReg2 = raw2;
            g_nfeStats.mdioRawReg3 = raw3;
        }
        g_nfeStats.initStep = 8; /* step 8: debug MDIO reads done */

        for (a = 0; a < 32; a++)  /* full scan 0..31 */
        {
            uint32 id = 0;
            IfxGeth_phy_Clause22_readMDIORegister(a, 2u, &id);
            g_nfeStats.initStep = 10u + a;  /* 10..41: scanning PHY addr a */
            if (id != 0x0000u && id != 0xFFFFu)
            {
                phyAddr = a;
                phyId   = id;
                found   = 1;
                break;
            }
        }

        g_nfeStats.phyAddr = phyAddr;
        g_nfeStats.phyId   = phyId;
        g_nfeStats.initStep = 50; /* step 50: PHY scan done */

        if (found)
        {
            /* Reset PHY (bit 15) */
            IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 0u, 0x8000u);
            g_nfeStats.initStep = 51;

            /* Wait for reset bit to self-clear with timeout */
            {
                uint32 timeout = 2000000u;
                uint32 ctrl = 0x8000u;
                while ((ctrl & 0x8000u) && timeout--)
                {
                    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 0u, &ctrl);
                }
            }
            g_nfeStats.initStep = 52; /* PHY reset done */

            /* RTL8211F: enable TX delay (page 0x0d08, reg 0x11 bit 8) */
            IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 31u, 0x0d08u);
            {
                uint32 value = 0;
                IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 0x11u, &value);
                value |= 0x0100u;
                IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 0x11u, value);
            }
            IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 31u, 0x0000u);

            /* Restart auto-negotiation (BMCR = 0x1200) */
            IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 0u, 0x1200u);
            g_nfeStats.initStep = 53;

            /* Brief delay for PHY to settle */
            {
                volatile uint32 d = 2000000u;
                while (d--) {}
            }
            g_nfeStats.initStep = 54;

            /* Poll link status (reg 1, bit 2) — read twice per IEEE spec */
            {
                uint32 status = 0;
                uint32 timeout = 2000000u;
                do
                {
                    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 1u, &status);
                    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 1u, &status);
                    if ((status & 0x0004u) != 0u)
                    {
                        g_nfeStats.linkUp = 1u;
                        break;
                    }
                } while (--timeout);
            }
            g_nfeStats.initStep = 55;
        }
    }

    /* Clear frame buffers */
    memset(s_frameBufA, 0, NFE_FRAME_BYTES);
    memset(s_frameBufB, 0, NFE_FRAME_BYTES);

    g_nfeStats.initStep = 99; /* step 99: fully complete */
    g_nfeStats.initDone = 1;
}

/* ==================== Frame assembly ==================== */

void nichia_eth_push_row(uint8 row, const uint8 *pixels)
{
    g_nfeStats.rowsReceived++;

    /*
     * Detect new frame: when row 0 arrives, finalise the current frame
     * (if enough rows were collected) and start fresh.
     */
    if (row == 0)
    {
        /* If the previous frame had all 64 rows, mark it ready for TX */
        if (s_rowCount == NFE_HEIGHT)
        {
            s_readyIdx  = s_assembleIdx;
            s_frameReady = TRUE;
            g_nfeStats.framesAssembled++;

            /* Switch to the other buffer for the next frame */
            s_assembleIdx = (uint8)(1u - s_assembleIdx);
        }

        /* Start new frame */
        s_rowCount      = 0;
        s_nextRow       = 0;
        s_frameTimestamp = (uint32)IfxStm_getLower(&MODULE_STM0);
    }

    /* Only accept sequential rows (drop if out of order) */
    if (row == s_nextRow && row < NFE_HEIGHT)
    {
        uint8 *dst = s_framePtr[s_assembleIdx] + (uint32)row * NFE_WIDTH;
        memcpy(dst, pixels, NFE_WIDTH);
        s_rowCount++;
        s_nextRow = row + 1u;
    }

    /*
     * Edge case: row 63 arrived and frame is complete but we won't see row 0
     * for ~6 ms.  Mark ready immediately so the main loop can send sooner.
     */
    if (s_rowCount == NFE_HEIGHT && !s_frameReady)
    {
        s_readyIdx   = s_assembleIdx;
        s_frameReady = TRUE;
        g_nfeStats.framesAssembled++;
        s_assembleIdx = (uint8)(1u - s_assembleIdx);
        s_rowCount    = 0;
        s_nextRow     = 0;
    }
}

/* ==================== Ethernet TX ==================== */

/**
 * Build and send a single Ethernet fragment.
 *
 * Ethernet frame layout:
 *   [0..5]   Dest MAC
 *   [6..11]  Src MAC
 *   [12..13] EtherType (0x88B5)
 *   [14..31] NFE header (18 bytes)
 *   [32..]   Pixel data (up to 1482 bytes)
 */
static boolean send_fragment(const uint8 *framePixels, uint16 frameSeq,
                             uint8 fragIdx, uint8 fragCnt,
                             uint16 dataOffset, uint16 dataLen,
                             uint32 timestamp)
{
    /* Total Ethernet payload = NFE header + pixel data */
    uint32 ethPayload = NFE_HDR_LEN + dataLen;
    uint32 ethTotal   = 14u + ethPayload;  /* 14 = MAC header */

    /* Minimum Ethernet frame is 60 bytes (excl. FCS) */
    if (ethTotal < 60u) ethTotal = 60u;

    /* Get TX buffer from iLLD */
    uint8 *pTxBuf = (uint8 *)IfxGeth_Eth_getTransmitBuffer(&s_geth, IfxGeth_TxDmaChannel_0);
    if (pTxBuf == NULL_PTR)
    {
        /* Try wait (blocking) as a fallback */
        pTxBuf = (uint8 *)IfxGeth_Eth_waitTransmitBuffer(&s_geth, IfxGeth_TxDmaChannel_0);
        if (pTxBuf == NULL_PTR)
        {
            g_nfeStats.txErrors++;
            return FALSE;
        }
    }

    /* ---- Ethernet header (14 bytes) ---- */
    memcpy(&pTxBuf[0], s_dstMac, 6);
    memcpy(&pTxBuf[6], s_srcMac, 6);
    put_be16(&pTxBuf[12], NFE_ETHERTYPE);

    /* ---- NFE protocol header (18 bytes) ---- */
    uint8 *hdr = &pTxBuf[14];
    put_be16(&hdr[0],  NFE_MAGIC);
    put_be16(&hdr[2],  frameSeq);
    hdr[4] = fragIdx;
    hdr[5] = fragCnt;
    put_be16(&hdr[6],  dataOffset);
    put_be16(&hdr[8],  dataLen);
    put_be16(&hdr[10], NFE_WIDTH);
    put_be16(&hdr[12], NFE_HEIGHT);
    put_be32(&hdr[14], timestamp);

    /* ---- Pixel payload ---- */
    memcpy(&pTxBuf[14 + NFE_HDR_LEN], &framePixels[dataOffset], dataLen);

    /* Zero-pad if frame is below minimum size */
    if (ethTotal > (14u + ethPayload))
    {
        memset(&pTxBuf[14u + ethPayload], 0, ethTotal - (14u + ethPayload));
    }

    /* Clear TX interrupt flag before send */
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_transmitInterrupt);

    /* Send */
    IfxGeth_Eth_sendTransmitBuffer(&s_geth, ethTotal, IfxGeth_TxDmaChannel_0);

    /* Wait for TX complete (poll — acceptable at 600 pkts/sec) */
    {
        uint32 timeout = 100000u;
        while (!IfxGeth_dma_isInterruptFlagSet(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                                IfxGeth_DmaInterruptFlag_transmitInterrupt))
        {
            if (--timeout == 0u)
            {
                g_nfeStats.txErrors++;
                return FALSE;   /* TX timeout — abort */
            }
        }
    }

    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_transmitInterrupt);

    g_nfeStats.fragmentsSent++;
    return TRUE;
}

boolean nichia_eth_send_pending(void)
{
    if (!s_frameReady)
        return FALSE;

    s_frameReady = FALSE;
    const uint8 *pixels = s_framePtr[s_readyIdx];
    uint16 seq = s_frameSeq++;

    uint32 remaining = NFE_FRAME_BYTES;
    uint16 offset    = 0;
    uint8  fragIdx   = 0;

    while (remaining > 0)
    {
        uint16 chunkLen = (remaining > NFE_MAX_PAYLOAD) ? NFE_MAX_PAYLOAD : (uint16)remaining;

        if (!send_fragment(pixels, seq, fragIdx, NFE_FRAG_COUNT,
                           offset, chunkLen, s_frameTimestamp))
        {
            /* TX error — abort remaining fragments for this frame */
            return FALSE;
        }

        offset    += chunkLen;
        remaining -= chunkLen;
        fragIdx++;
    }

    g_nfeStats.framesSent++;
    return TRUE;
}
