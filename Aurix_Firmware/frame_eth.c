/******************************************************************************
 * frame_eth.c — Unified Frame-over-Ethernet TX for Nichia + Osram
 *
 * Replaces nichia_eth.c with a device-agnostic implementation.
 * Same GETH / RGMII / PHY init (KIT_A2G_TC397_5V_TFT with RTL8211F),
 * same ethertype 0x88B5, same fragment protocol —  just configurable
 * magic, width, height, and fragment count per device type.
 *
 * Nichia: rows arrive one-by-one (push_nichia_row), 256×64, magic "NI"
 * Osram:  complete frame arrives   (push_osram_frame), 320×80, magic "OS"
 *
 * Build requirements:
 *   - iLLD Geth/Eth driver compiled into the project
 *   - RGMII pins for KIT_A2G_TC397_5V_TFT
 ******************************************************************************/

#include "frame_eth.h"
#include "Geth/Eth/IfxGeth_Eth.h"
#include "Geth/Std/IfxGeth.h"
#include "Stm/Std/IfxStm.h"
#include <string.h>  /* memcpy, memset */

/* ==================== MAC addresses ==================== */
static const uint8 s_srcMac[6] = { 0x02, 0x0A, 0xF0, 0x4E, 0x49, 0x01 };  /* locally-administered */
static const uint8 s_dstMac[6] = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };  /* broadcast */

/* ==================== GETH handle & buffers ==================== */
static IfxGeth_Eth s_geth;

IFX_ALIGN(32) static uint8 s_txBuf[FE_TX_DESCRIPTORS * FE_TX_BUF_SIZE];
IFX_ALIGN(32) static uint8 s_rxBuf[FE_RX_DESCRIPTORS * FE_RX_BUF_SIZE];

/* ==================== Device parameters ==================== */
static FrameEthDevice s_device     = FE_DEVICE_NICHIA;
static uint16         s_magic      = FE_MAGIC_NICHIA;
static uint16         s_width      = FE_NICHIA_W;
static uint16         s_height     = FE_NICHIA_H;
static uint32         s_frameBytes = FE_NICHIA_FRAME_BYTES;

/* ==================== Frame assembly (double-buffered) ==================== */
/*
 * Worst-case buffer: 25600 bytes (Osram).
 * For Nichia only the first 16384 bytes are used.
 */
static uint8  s_frameBufA[FE_MAX_FRAME_BYTES];
static uint8  s_frameBufB[FE_MAX_FRAME_BYTES];
static uint8 *s_framePtr[2] = { s_frameBufA, s_frameBufB };
static uint8  s_assembleIdx = 0;

/* Nichia row assembly tracking */
static uint8  s_nextRow       = 0;
static uint8  s_rowCount      = 0;
static uint32 s_frameTimestamp = 0;

/* Frame ready signal */
static volatile boolean s_frameReady = FALSE;
static volatile uint8   s_readyIdx   = 0;
static uint16           s_frameSeq   = 0;

/* ==================== Telemetry ==================== */
FeStats g_feStats;

/* ==================== RGMII pin configuration ==================== */
/*
 * RGMII pins for KIT_A2G_TC397_5V_TFT (LFBGA292):
 *   TXCLK  : P11.4    RXCLK  : P11.12
 *   TXD0   : P11.3    RXD0   : P11.10
 *   TXD1   : P11.2    RXD1   : P11.9
 *   TXD2   : P11.1    RXD2   : P11.8
 *   TXD3   : P11.0    RXD3   : P11.7
 *   TXCTL  : P11.6    RXCTL  : P11.11
 *   MDC    : P12.0    MDIO   : P12.1
 *   GREFCLK: P11.5
 */
static const IfxGeth_Eth_RgmiiPins s_rgmiiPins = {
    .txClk   = &IfxGeth_TXCLK_P11_4_OUT,
    .txd0    = &IfxGeth_TXD0_P11_3_OUT,
    .txd1    = &IfxGeth_TXD1_P11_2_OUT,
    .txd2    = &IfxGeth_TXD2_P11_1_OUT,
    .txd3    = &IfxGeth_TXD3_P11_0_OUT,
    .txCtl   = &IfxGeth_TXCTL_P11_6_OUT,
    .rxClk   = &IfxGeth_RXCLKA_P11_12_IN,
    .rxd0    = &IfxGeth_RXD0A_P11_10_IN,
    .rxd1    = &IfxGeth_RXD1A_P11_9_IN,
    .rxd2    = &IfxGeth_RXD2A_P11_8_IN,
    .rxd3    = &IfxGeth_RXD3A_P11_7_IN,
    .rxCtl   = &IfxGeth_RXCTLA_P11_11_IN,
    .mdc     = &IfxGeth_MDC_P12_0_OUT,
    .mdio    = &IfxGeth_MDIO_P12_1_INOUT,
    .grefClk = &IfxGeth_GREFCLK_P11_5_IN,
};

/* ==================== Helpers ==================== */

static void put_be16(uint8 *dst, uint16 val)
{
    dst[0] = (uint8)(val >> 8);
    dst[1] = (uint8)(val);
}

static void put_be32(uint8 *dst, uint32 val)
{
    dst[0] = (uint8)(val >> 24);
    dst[1] = (uint8)(val >> 16);
    dst[2] = (uint8)(val >> 8);
    dst[3] = (uint8)(val);
}

/* ==================== Update device parameters ==================== */

static void apply_device_params(FrameEthDevice device)
{
    s_device = device;
    if (device == FE_DEVICE_OSRAM)
    {
        s_magic      = FE_MAGIC_OSRAM;
        s_width      = FE_OSRAM_W;
        s_height     = FE_OSRAM_H;
        s_frameBytes = FE_OSRAM_FRAME_BYTES;
    }
    else
    {
        s_magic      = FE_MAGIC_NICHIA;
        s_width      = FE_NICHIA_W;
        s_height     = FE_NICHIA_H;
        s_frameBytes = FE_NICHIA_FRAME_BYTES;
    }
}

/* ==================== GETH initialisation ==================== */

void frame_eth_init(FrameEthDevice device)
{
    /*
     * IMPORTANT: config must be static — the struct is ~1 KB.
     * Allocating on the stack overflows the CSA → CME trap.
     */
    static IfxGeth_Eth_Config config;

    /* Set device parameters first */
    apply_device_params(device);

    /* Clear stats */
    memset((void *)&g_feStats, 0, sizeof(g_feStats));
    g_feStats.initStep = 1;

    /* Default config */
    IfxGeth_Eth_initModuleConfig(&config, &MODULE_GETH);
    g_feStats.initStep = 2;

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

        config.dma.txChannel[0].channelEnable        = TRUE;
        config.dma.txChannel[0].channelId             = IfxGeth_TxDmaChannel_0;
        config.dma.txChannel[0].txDescrList           = &IfxGeth_Eth_txDescrList[gethInst][0];
        config.dma.txChannel[0].txBuffer1StartAddress = (uint32 *)&s_txBuf[0];
        config.dma.txChannel[0].txBuffer1Size         = FE_TX_BUF_SIZE;

        config.dma.rxChannel[0].channelEnable        = TRUE;
        config.dma.rxChannel[0].channelId             = IfxGeth_RxDmaChannel_0;
        config.dma.rxChannel[0].rxDescrList           = &IfxGeth_Eth_rxDescrList[gethInst][0];
        config.dma.rxChannel[0].rxBuffer1StartAddress = (uint32 *)&s_rxBuf[0];
        config.dma.rxChannel[0].rxBuffer1Size         = FE_RX_BUF_SIZE;

        config.dma.txInterrupt[0].channelId = IfxGeth_DmaChannel_0;
        config.dma.txInterrupt[0].priority  = FE_GETH_TX_ISR_PRIO;
        config.dma.txInterrupt[0].provider  = IfxSrc_Tos_cpu0;

        config.dma.rxInterrupt[0].channelId = IfxGeth_DmaChannel_0;
        config.dma.rxInterrupt[0].priority  = FE_GETH_RX_ISR_PRIO;
        config.dma.rxInterrupt[0].provider  = IfxSrc_Tos_cpu0;
    }
    g_feStats.initStep = 3;

    /* Initialise the module */
    IfxGeth_Eth_initModule(&s_geth, &config);
    g_feStats.initStep = 4;

    /* Enable transmitter only — we use GETH for TX exclusively.
     * DO NOT start receivers: we never read Ethernet frames, and the
     * RX DMA ring would be exhausted by broadcast traffic (ARP, mDNS)
     * without being recycled, leading to DMA abnormal state → bus error. */
    IfxGeth_Eth_startTransmitters(&s_geth, 1);
    g_feStats.initStep = 5;

    /* RX DMA left disabled (descriptors are configured by initModule
     * but the DMA engine never runs). */
    g_feStats.initStep = 6;

    /* Brief delay (~50 ms) for PHY power-up before MDIO scan */
    {
        volatile uint32 d = 10000000u;
        while (d--) {}
    }
    g_feStats.initStep = 7;

    /* ── PHY MDIO: scan for PHY, initialise RTL8211F ── */
    {
        uint8  phyAddr = 0;
        uint32 phyId   = 0;
        uint8  found   = 0;
        uint8  a;

        /* Debug: raw read of addr 0, reg 2 & 3 */
        {
            uint32 raw2 = 0xDEADu, raw3 = 0xDEADu;
            IfxGeth_phy_Clause22_readMDIORegister(0u, 2u, &raw2);
            IfxGeth_phy_Clause22_readMDIORegister(0u, 3u, &raw3);
            g_feStats.mdioRawReg2 = raw2;
            g_feStats.mdioRawReg3 = raw3;
        }
        g_feStats.initStep = 8;

        for (a = 0; a < 32; a++)
        {
            uint32 id = 0;
            IfxGeth_phy_Clause22_readMDIORegister(a, 2u, &id);
            g_feStats.initStep = 10u + a;
            if (id != 0x0000u && id != 0xFFFFu)
            {
                phyAddr = a;
                phyId   = id;
                found   = 1;
                break;
            }
        }

        g_feStats.phyAddr  = phyAddr;
        g_feStats.phyId    = phyId;
        g_feStats.initStep = 50;

        if (found)
        {
            /* Reset PHY (bit 15) */
            IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 0u, 0x8000u);
            g_feStats.initStep = 51;

            /* Wait for reset bit to self-clear */
            {
                uint32 timeout = 2000000u;
                uint32 ctrl    = 0x8000u;
                while ((ctrl & 0x8000u) && timeout--)
                {
                    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 0u, &ctrl);
                }
            }
            g_feStats.initStep = 52;

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
            g_feStats.initStep = 53;

            /* Brief delay for PHY to settle */
            {
                volatile uint32 d = 2000000u;
                while (d--) {}
            }
            g_feStats.initStep = 54;

            /* Poll link status (reg 1, bit 2) — read twice per IEEE spec */
            {
                uint32 status  = 0;
                uint32 timeout = 2000000u;
                do
                {
                    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 1u, &status);
                    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 1u, &status);
                    if ((status & 0x0004u) != 0u)
                    {
                        g_feStats.linkUp = 1u;
                        break;
                    }
                } while (--timeout);
            }
            g_feStats.initStep = 55;
        }
    }

    /* Clear frame buffers */
    memset(s_frameBufA, 0, FE_MAX_FRAME_BYTES);
    memset(s_frameBufB, 0, FE_MAX_FRAME_BYTES);

    /* Reset assembly state */
    frame_eth_reset_frame_state();

    g_feStats.initStep = 99;
    g_feStats.initDone = 1;
}

/* ==================== Device switching ==================== */

void frame_eth_set_device(FrameEthDevice device)
{
    apply_device_params(device);
    frame_eth_reset_frame_state();
}

void frame_eth_reset_frame_state(void)
{
    s_assembleIdx  = 0;
    s_nextRow      = 0;
    s_rowCount     = 0;
    s_frameReady   = FALSE;
    s_readyIdx     = 0;
    s_frameTimestamp = 0;
}

/* ==================== Zero-copy Osram API ==================== */

uint8 *frame_eth_get_assembly_buffer(void)
{
    return s_framePtr[s_assembleIdx];
}

void frame_eth_mark_osram_ready(void)
{
    s_frameTimestamp = (uint32)IfxStm_getLower(&MODULE_STM0);
    s_readyIdx   = s_assembleIdx;
    s_frameReady = TRUE;
    g_feStats.osramFramesPushed++;
    s_assembleIdx = (uint8)(1u - s_assembleIdx);
}

/* ==================== Nichia row assembly ==================== */

void frame_eth_push_nichia_row(uint8 row, const uint8 *pixels)
{
    g_feStats.nichiaRowsReceived++;

    /* Detect new frame: row 0 → finalise previous frame if complete */
    if (row == 0)
    {
        if (s_rowCount == FE_NICHIA_H)
        {
            s_readyIdx   = s_assembleIdx;
            s_frameReady = TRUE;
            g_feStats.nichiaFramesAssembled++;
            s_assembleIdx = (uint8)(1u - s_assembleIdx);
        }

        s_rowCount      = 0;
        s_nextRow       = 0;
        s_frameTimestamp = (uint32)IfxStm_getLower(&MODULE_STM0);
    }

    /* Accept sequential rows only */
    if (row == s_nextRow && row < FE_NICHIA_H)
    {
        uint8 *dst = s_framePtr[s_assembleIdx] + (uint32)row * FE_NICHIA_W;
        memcpy(dst, pixels, FE_NICHIA_W);
        s_rowCount++;
        s_nextRow = row + 1u;
    }

    /* Immediate send when last row arrives */
    if (s_rowCount == FE_NICHIA_H && !s_frameReady)
    {
        s_readyIdx   = s_assembleIdx;
        s_frameReady = TRUE;
        g_feStats.nichiaFramesAssembled++;
        s_assembleIdx = (uint8)(1u - s_assembleIdx);
        s_rowCount    = 0;
        s_nextRow     = 0;
    }
}

/* ==================== Osram complete frame push ==================== */

void frame_eth_push_osram_frame(const uint8 *pixels, uint32 len)
{
    if (len > FE_MAX_FRAME_BYTES)
        len = FE_MAX_FRAME_BYTES;

    /* Copy into assembly buffer and mark ready immediately */
    memcpy(s_framePtr[s_assembleIdx], pixels, len);
    s_frameTimestamp = (uint32)IfxStm_getLower(&MODULE_STM0);

    s_readyIdx   = s_assembleIdx;
    s_frameReady = TRUE;
    g_feStats.osramFramesPushed++;

    s_assembleIdx = (uint8)(1u - s_assembleIdx);
}

/* ==================== Ethernet TX (fragmented) ==================== */

/**
 * Build and send a single Ethernet fragment.
 *
 * Layout:
 *   [0..5]   Dst MAC
 *   [6..11]  Src MAC
 *   [12..13] EtherType (0x88B5)
 *   [14..31] Protocol header (18 bytes)
 *   [32..]   Pixel data (up to 1482 bytes)
 */
static boolean send_fragment(const uint8 *framePixels, uint16 frameSeq,
                             uint8 fragIdx, uint8 fragCnt,
                             uint16 dataOffset, uint16 dataLen,
                             uint32 timestamp)
{
    uint32 ethPayload = FE_HDR_LEN + dataLen;
    uint32 ethTotal   = 14u + ethPayload;

    /* Minimum Ethernet frame = 60 bytes (excl. FCS) */
    if (ethTotal < 60u) ethTotal = 60u;

    /* Get TX buffer from iLLD */
    uint8 *pTxBuf = (uint8 *)IfxGeth_Eth_getTransmitBuffer(&s_geth, IfxGeth_TxDmaChannel_0);
    if (pTxBuf == NULL_PTR)
    {
        pTxBuf = (uint8 *)IfxGeth_Eth_waitTransmitBuffer(&s_geth, IfxGeth_TxDmaChannel_0);
        if (pTxBuf == NULL_PTR)
        {
            g_feStats.txErrors++;
            return FALSE;
        }
    }

    /* ---- Ethernet header (14 bytes) ---- */
    memcpy(&pTxBuf[0], s_dstMac, 6);
    memcpy(&pTxBuf[6], s_srcMac, 6);
    put_be16(&pTxBuf[12], FE_ETHERTYPE);

    /* ---- Protocol header (18 bytes) ---- */
    uint8 *hdr = &pTxBuf[14];
    put_be16(&hdr[0],  s_magic);
    put_be16(&hdr[2],  frameSeq);
    hdr[4] = fragIdx;
    hdr[5] = fragCnt;
    put_be16(&hdr[6],  dataOffset);
    put_be16(&hdr[8],  dataLen);
    put_be16(&hdr[10], s_width);
    put_be16(&hdr[12], s_height);
    put_be32(&hdr[14], timestamp);

    /* ---- Pixel payload ---- */
    memcpy(&pTxBuf[14 + FE_HDR_LEN], &framePixels[dataOffset], dataLen);

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

    /* Wait for TX complete (polled — acceptable at ~900 pkts/sec max) */
    {
        uint32 timeout = 100000u;
        while (!IfxGeth_dma_isInterruptFlagSet(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                                IfxGeth_DmaInterruptFlag_transmitInterrupt))
        {
            if (--timeout == 0u)
            {
                g_feStats.txErrors++;
                return FALSE;
            }
        }
    }

    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_transmitInterrupt);

    g_feStats.fragmentsSent++;
    return TRUE;
}

boolean frame_eth_send_pending(void)
{
    if (!s_frameReady)
        return FALSE;

    s_frameReady = FALSE;
    const uint8 *pixels = s_framePtr[s_readyIdx];
    uint16 seq = s_frameSeq++;

    /* Compute fragment count dynamically based on current frame size */
    uint8 fragCnt = (uint8)((s_frameBytes + FE_MAX_PAYLOAD - 1u) / FE_MAX_PAYLOAD);

    uint32 remaining = s_frameBytes;
    uint16 offset    = 0;
    uint8  fragIdx   = 0;

    while (remaining > 0)
    {
        uint16 chunkLen = (remaining > FE_MAX_PAYLOAD)
                        ? (uint16)FE_MAX_PAYLOAD
                        : (uint16)remaining;

        if (!send_fragment(pixels, seq, fragIdx, fragCnt,
                           offset, chunkLen, s_frameTimestamp))
        {
            /* TX error — abort remaining fragments for this frame */
            return FALSE;
        }

        offset    += chunkLen;
        remaining -= chunkLen;
        fragIdx++;
    }

    g_feStats.framesSent++;
    return TRUE;
}
