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
#include "camera_trigger.h"
#include "can_diag.h"
#include "Cpu/Std/IfxCpu_Intrinsics.h"
#include "Geth/Eth/IfxGeth_Eth.h"
#include "Geth/Std/IfxGeth.h"
#include "Stm/Std/IfxStm.h"
#include <string.h>  /* memcpy, memset */

/* The receive buffer array and the tail-pointer wrap logic both assume the ring
 * depth iLLD was compiled with.  A mismatch would corrupt memory silently. */
#if (FE_RX_DESCRIPTORS != IFXGETH_MAX_RX_DESCRIPTORS)
#error "FE_RX_DESCRIPTORS must match IFXGETH_MAX_RX_DESCRIPTORS (see Configurations/Ifx_Cfg.h)"
#endif
#if (FE_TX_DESCRIPTORS != IFXGETH_MAX_TX_DESCRIPTORS)
#error "FE_TX_DESCRIPTORS must match IFXGETH_MAX_TX_DESCRIPTORS"
#endif

/* ==================== MAC addresses ==================== */
static const uint8 s_srcMac[6] = { 0x02, 0x0A, 0xF0, 0x4E, 0x49, 0x01 };  /* locally-administered */
static const uint8 s_dstMac[6] = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };  /* broadcast */

/* ==================== GETH handle & buffers ==================== */
static IfxGeth_Eth s_geth;
static IfxGeth_Eth_TxChannelConfig s_txChannelConfig;
static IfxGeth_Eth_RxChannelConfig s_rxChannelConfig;

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
static uint8  s_txFrameBuf[FE_NICHIA_FRAME_BYTES];
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
static volatile uint32  s_displaySeq = 0;
static uint16           s_diagSeq    = 0;

/* Ethernet TX retry state.
 * A completed LVDS frame is copied here before Ethernet fragmentation starts,
 * so transient TX stalls do not block the live frame assembler/display buffers.
 */
static uint8  s_txActive     = 0u;
static uint16 s_txSeq        = 0u;
static uint8  s_txFragIdx    = 0u;
static uint8  s_txFragCnt    = 0u;
static uint16 s_txOffset     = 0u;
static uint32 s_txRemaining  = 0u;
static uint32 s_txTimestamp  = 0u;
static const uint8 *s_txPixels = NULL_PTR;
static volatile IfxGeth_TxDescr *s_txPendingDescr = NULL_PTR;

/* ==================== Telemetry ==================== */
FeStats g_feStats;

#define FE_TX_RECOVERY_INTERVAL_MS   100u
/* Main-loop fairness guardrails:
 * - TX: do not send an entire frame (12-18 fragments) in one tight burst.
 * - RX: do not stay forever in poll_rx() if RX ring status becomes sticky.
 * These caps keep command polling, LVDS parsing and transport responsive even
 * under transient MAC/DMA glitches. */
#define FE_TX_FRAG_BURST_MAX         6u
#define FE_RX_POLL_BUDGET            64u
/* RX freeze detection via DMA hardware state (traffic-independent).
 * RBU = Receive Buffer Unavailable, DMA_CHx.STATUS bit 7 (write-1-to-clear).
 * It latches when the DMA received a packet but had no CPU-freed descriptor to
 * store it.  We clear it every poll, so a set bit means "a packet arrived
 * since the previous poll".  If packets keep arriving (RBU) but none ever
 * surface (isRxDataAvailable stays FALSE / no buffer processed) for
 * FE_RX_STALL_MS while the link is up, the RX descriptor ring is
 * desynchronised (the command freeze) and a cheap wakeup cannot fix it -> a
 * full ring re-init is required (same effect as a winIDEA reset+run).
 * On a genuinely silent link RBU never re-latches, so this never false-fires. */
#define FE_DMA_STATUS_RBU            (1u << 7)
#define FE_RX_STALL_MS               200u
/* CAN-diag Ethernet TX pacing. In ECU↔SmartVisio↔LSM + Recording the monitor
 * produces a high record rate; unpaced TX floods the single GETH TX channel
 * and keeps CPU0 busy in the TX spin, so the LVDS RX DMA ping-pong buffers are
 * not drained in time -> LVDS CRC errors and pane-B/TFT flicker. Pace the diag
 * TX so LVDS transport keeps priority. ECU mode has almost no monitor traffic,
 * so it is unaffected. */
#define FE_DIAG_TX_INTERVAL_US       100u

#define PHY_BMSR_LINK_STATUS         0x0004u
#define PHY_BMSR_AUTONEG_COMPLETE    0x0020u
#define PHY_AN_10_HALF               0x0020u
#define PHY_AN_10_FULL               0x0040u
#define PHY_AN_100_HALF              0x0080u
#define PHY_AN_100_FULL              0x0100u
#define PHY_GB_CTRL_1000_HALF        0x0100u
#define PHY_GB_CTRL_1000_FULL        0x0200u
#define PHY_GB_STAT_LP_1000_HALF     0x0400u
#define PHY_GB_STAT_LP_1000_FULL     0x0800u

#define FE_CACHE_LINE_SIZE           32u
#define FE_TX_DESC_BYTES             16u
#define FE_TX_DESC_RING_BYTES        (FE_TX_DESC_BYTES * FE_TX_DESCRIPTORS)

#define RTL8211F_MDIO_BMCR           0x00u
#define RTL8211F_MDIO_PAGSR          0x1Fu
#define RTL8211F_MDIO_LCR            0x10u
#define RTL8211F_MDIO_EEELCR         0x11u
#define RTL8211F_PAGE_RGMII          0x0D08u
#define RTL8211F_PAGE_LED_EEE        0x0D04u

/* PHY link tracking.
 * In debugger runs the PHY link is usually already settled by the time CPU0
 * reaches the main loop. In standalone boot the firmware can start much
 * earlier, so TX must wait until runtime MDIO polling confirms link-up. */
static uint8  s_phyFound        = 0u;
static uint8  s_phyAddrRuntime  = 0u;
static uint32 s_lastLinkPollStm = 0u;
static uint32 s_lastTxRecoveryStm = 0u;
static uint32 s_lastRxRecoveryStm = 0u;
static uint32 s_lastLinkUp = 0u;
static uint8  s_macSynced = 0u;
static uint32 s_lastDiagTxStm = 0u;

/* RX freeze watchdog state.  Sporadic PC commands are NOT a valid liveness
 * signal; instead we use the DMA RBU hardware evidence (packets arriving) vs
 * actual buffer progress. */
static uint32 s_lastRxBufStm = 0u;        /* STM ticks at last processed buffer */
static uint8  s_rxRbuSinceProgress = 0u;  /* RBU re-latched since last progress */
/* MAC-level RX FIFO overflow evidence (upstream of the DMA/RBU signal).  A
 * confirmed freeze mode: commands are silently dropped at the MAC RX FIFO
 * and never reach the DMA descriptor ring, so RBU never latches and the
 * watchdog above never fires (rxRecoveries stays 0 while the freeze is
 * live).  Track the overflow counter the same way as RBU so the existing,
 * already-safe frame_eth_recover_rx_ring() also gets a chance to run. */
static uint32 s_lastRxFifoOverflowCount = 0u;
static uint8  s_rxFifoOverflowSinceProgress = 0u;

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

static void frame_eth_cache_writeback_invalidate_line(void *addr)
{
#if defined(__TASKING__)
    __asm__ volatile ("cachea.wi [%0]0" : : "a"(addr) : "memory");
#elif defined(__DCC__)
    __cacheawi(addr);
#else
    __cacheawi(addr);
#endif
}

static void frame_eth_cache_invalidate_line(void *addr)
{
#if defined(__TASKING__)
    __asm__ volatile ("cachea.i [%0]0" : : "a"(addr) : "memory");
#elif defined(__DCC__)
    __cacheawi(addr);
#else
    __cacheai(addr);
#endif
}

static void frame_eth_cache_writeback_invalidate_range(const volatile void *addr, uint32 len)
{
    uint32 start;
    uint32 end;

    if ((addr == NULL_PTR) || (len == 0u))
        return;

    start = ((uint32)addr) & ~(FE_CACHE_LINE_SIZE - 1u);
    end   = ((uint32)addr + len + FE_CACHE_LINE_SIZE - 1u) & ~(FE_CACHE_LINE_SIZE - 1u);

    while (start < end)
    {
        frame_eth_cache_writeback_invalidate_line((void *)start);
        start += FE_CACHE_LINE_SIZE;
    }

    __dsync();
}

static void frame_eth_cache_invalidate_range(const volatile void *addr, uint32 len)
{
    uint32 start;
    uint32 end;

    if ((addr == NULL_PTR) || (len == 0u))
        return;

    start = ((uint32)addr) & ~(FE_CACHE_LINE_SIZE - 1u);
    end   = ((uint32)addr + len + FE_CACHE_LINE_SIZE - 1u) & ~(FE_CACHE_LINE_SIZE - 1u);

    while (start < end)
    {
        frame_eth_cache_invalidate_line((void *)start);
        start += FE_CACHE_LINE_SIZE;
    }

    __dsync();
}

static void frame_eth_snapshot_tx_dma(void)
{
    volatile IfxGeth_TxDescr *descr;

    if (s_geth.gethSFR == NULL_PTR)
        return;

    g_feStats.txDmaStatus = s_geth.gethSFR->DMA_CH[IfxGeth_DmaChannel_0].STATUS.U;
    descr = IfxGeth_Eth_getActualTxDescriptor(&s_geth, IfxGeth_TxDmaChannel_0);
    frame_eth_cache_invalidate_range(descr, FE_TX_DESC_BYTES);
    if (descr != NULL_PTR)
        g_feStats.txDescOwn = descr->TDES3.R.OWN;
}

static void frame_eth_snapshot_rx_dma(void)
{
    if (s_geth.gethSFR == NULL_PTR)
        return;

    g_feStats.rxDmaStatus = s_geth.gethSFR->DMA_CH[IfxGeth_DmaChannel_0].STATUS.U;
}

static boolean frame_eth_tx_pending_complete(void)
{
    volatile uint32 spin;

    if (s_txPendingDescr == NULL_PTR)
        return TRUE;

    /* TX descriptors are in DSPR0 (non-cached, direct-mapped).  CPU reads
     * are always coherent with DMA writes — no cache invalidation needed.
     * Spin until the DMA releases OWN.  At 1 Gbps a max-size Ethernet frame
     * takes ~12 µs on the wire.  10 000 iterations ≈ 250 µs at 200 MHz
     * which generously covers normal TX latency including store-and-forward
     * delay in the MTL TX queue.
     * If DMA doesn't complete within this window, something is wrong —
     * recovery will handle it on the next main-loop iteration. */
    for (spin = 0u; spin < 10000u; spin++)
    {
        if (s_txPendingDescr->TDES3.R.OWN == 0u)
        {
            s_txPendingDescr = NULL_PTR;
            return TRUE;
        }
    }

    /* DMA did not release OWN within ~250 µs.  Abort the burst.
     * frame_eth_recover_tx_ring will fire on the next send attempt
     * (rate-limited to 100 ms). */
    g_feStats.txWakeups++;
    return FALSE;
}

static void frame_eth_clear_tx_status_flags(void)
{
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_transmitInterrupt);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_transmitStopped);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_transmitBufferUnavailable);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_earlyTransmitInterrupt);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_fatalBusError);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_contextDescriptorError);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_abnormalInterruptSummary);
    IfxGeth_dma_clearInterruptFlag(s_geth.gethSFR, IfxGeth_DmaChannel_0,
                                   IfxGeth_DmaInterruptFlag_normalInterruptSummary);
}

static void frame_eth_phy_select_page(uint8 phyAddr, uint32 page)
{
    IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, RTL8211F_MDIO_PAGSR, page);
}

static void frame_eth_configure_rtl8211f(uint8 phyAddr)
{
    uint32 value;
    uint32 timeout;

    IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, RTL8211F_MDIO_BMCR, 0x8000u);

    timeout = 2000000u;
    value   = 0x8000u;
    while (((value & 0x8000u) != 0u) && (timeout-- != 0u))
    {
        IfxGeth_phy_Clause22_readMDIORegister(phyAddr, RTL8211F_MDIO_BMCR, &value);
    }

    frame_eth_phy_select_page(phyAddr, RTL8211F_PAGE_RGMII);
    value = 0u;
    IfxGeth_phy_Clause22_readMDIORegister(phyAddr, 0x11u, &value);
    value |= 0x0100u;
    IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, 0x11u, value);
    frame_eth_phy_select_page(phyAddr, 0x0000u);

    /* Keep the local sequence aligned with Infineon's RTL8211F examples:
     * LED page setup plus EEE-off.  EEE/LPI can leave the standalone link in
     * low-power idle long enough for the polled TX path to time out.
     */
    frame_eth_phy_select_page(phyAddr, RTL8211F_PAGE_LED_EEE);
    IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, RTL8211F_MDIO_LCR, 0x8170u);
    IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, RTL8211F_MDIO_EEELCR, 0x0000u);
    frame_eth_phy_select_page(phyAddr, 0x0000u);

    IfxGeth_Phy_Clause22_writeMDIORegister(phyAddr, RTL8211F_MDIO_BMCR, 0x1200u);
}

static void frame_eth_recover_tx_ring(boolean force)
{
    uint32 now = (uint32)IfxStm_getLower(&MODULE_STM0);
    uint32 minInterval = (uint32)IfxStm_getTicksFromMilliseconds(&MODULE_STM0,
                                                                 FE_TX_RECOVERY_INTERVAL_MS);
    uint32 i;

    if (s_txChannelConfig.txDescrList == NULL_PTR || s_geth.gethSFR == NULL_PTR)
        return;

    if (!force && (uint32)(now - s_lastTxRecoveryStm) < minInterval)
        return;

    s_lastTxRecoveryStm = now;
    frame_eth_snapshot_tx_dma();

    s_txPendingDescr = NULL_PTR;
    IfxGeth_Eth_stopTransmitters(&s_geth, 1u);

    /* iLLD initTransmitDescriptors does not clear TDES3 on a reused ring.
     * Clear it first so stale OWN/IOC/FD/LD bits cannot survive recovery.
     */
    for (i = 0u; i < FE_TX_DESCRIPTORS; i++)
    {
        s_txChannelConfig.txDescrList->descr[i].TDES3.U = 0u;
    }
    frame_eth_cache_writeback_invalidate_range(s_txChannelConfig.txDescrList->descr,
                                               FE_TX_DESC_RING_BYTES);

    IfxGeth_Eth_initTransmitDescriptors(&s_geth, &s_txChannelConfig);
    frame_eth_cache_writeback_invalidate_range(s_txChannelConfig.txDescrList->descr,
                                               FE_TX_DESC_RING_BYTES);
    frame_eth_clear_tx_status_flags();

    /* Set tail pointer = base so DMA knows the ring is empty.  Without this,
     * the stale tail pointer from before the stop may cause the DMA to scan
     * OWN=0 descriptors and enter Suspended state from which a tail-pointer
     * write is needed to recover — creating a chicken-and-egg problem. */
    {
        volatile IfxGeth_TxDescr *base;
        base = s_txChannelConfig.txDescrList->descr;
        IfxGeth_dma_setTxDescriptorTailPointer(s_geth.gethSFR,
                                               IfxGeth_TxDmaChannel_0,
                                               (uint32)base);
    }

    IfxGeth_Eth_startTransmitters(&s_geth, 1u);
    IfxGeth_Eth_wakeupTransmitter(&s_geth, IfxGeth_TxDmaChannel_0);
    g_feStats.txRecoveries++;
    g_feStats.txWakeups++;
    frame_eth_snapshot_tx_dma();

    /* Reset the frame TX state machine so we don't attempt to send a partial
     * frame whose early fragments may have been lost.  The next complete frame
     * from the LVDS assembler will start a fresh burst. */
    s_txActive   = 0u;
    s_txPixels   = NULL_PTR;
    s_txFragIdx  = 0u;
    s_txOffset   = 0u;
    s_txRemaining = 0u;
}

static void frame_eth_recover_rx_ring(boolean force)
{
    uint32 now = (uint32)IfxStm_getLower(&MODULE_STM0);
    uint32 minInterval = (uint32)IfxStm_getTicksFromMilliseconds(&MODULE_STM0,
                                                                 FE_TX_RECOVERY_INTERVAL_MS);

    if (s_rxChannelConfig.rxDescrList == NULL_PTR || s_geth.gethSFR == NULL_PTR)
        return;

    if (!force && (uint32)(now - s_lastRxRecoveryStm) < minInterval)
        return;

    s_lastRxRecoveryStm = now;
    frame_eth_snapshot_rx_dma();

    /* Re-prime RX descriptors/buffers with APIs available in this iLLD build.
     * Receiver wakeup handles the suspended/RBU path if needed. */
    IfxGeth_Eth_initReceiveDescriptors(&s_geth, &s_rxChannelConfig);
    IfxGeth_Eth_wakeupReceiver(&s_geth, IfxGeth_RxDmaChannel_0);
    IfxGeth_Eth_startTransmitters(&s_geth, 1u);
    IfxGeth_Eth_startReceivers(&s_geth, 1u);

    g_feStats.rxRecoveries++;
    frame_eth_snapshot_rx_dma();
}

static boolean frame_eth_sync_mac_with_phy(void)
{
    Ifx_GETH_MAC_PHYIF_CONTROL_STATUS phyIf;
    uint32 bmsr = 0u;
    uint32 anar = 0u;
    uint32 anlpar = 0u;
    uint32 gbCtrl = 0u;
    uint32 gbStatus = 0u;
    IfxGeth_LineSpeed speed = IfxGeth_LineSpeed_1000Mbps;
    IfxGeth_DuplexMode duplex = IfxGeth_DuplexMode_fullDuplex;
    uint32 speedMbps = 1000u;
    uint32 duplexFull = 1u;

    if (s_phyFound == 0u || s_geth.gethSFR == NULL_PTR)
        return FALSE;

    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 1u, &bmsr);
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 1u, &bmsr);
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 4u, &anar);
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 5u, &anlpar);
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 9u, &gbCtrl);
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 10u, &gbStatus);

    g_feStats.phyBmsr = bmsr;
    g_feStats.phyAnar = anar;
    g_feStats.phyAnlpar = anlpar;
    g_feStats.phyGbCtrl = gbCtrl;
    g_feStats.phyGbStatus = gbStatus;

    if ((bmsr & PHY_BMSR_LINK_STATUS) == 0u)
        return FALSE;

    if ((bmsr & PHY_BMSR_AUTONEG_COMPLETE) == 0u)
        return FALSE;

    phyIf.U = s_geth.gethSFR->MAC_PHYIF_CONTROL_STATUS.U;
    if (phyIf.B.LNKSTS == 0u)
        return FALSE;

    duplex = (phyIf.B.LNKMOD != 0u) ? IfxGeth_DuplexMode_fullDuplex
                                    : IfxGeth_DuplexMode_halfDuplex;
    duplexFull = (phyIf.B.LNKMOD != 0u) ? 1u : 0u;

    if (phyIf.B.LNKSPEED == 0u)
    {
        speed = IfxGeth_LineSpeed_10Mbps;
        speedMbps = 10u;
    }
    else if (phyIf.B.LNKSPEED == 1u)
    {
        speed = IfxGeth_LineSpeed_100Mbps;
        speedMbps = 100u;
    }
    else
    {
        speed = IfxGeth_LineSpeed_1000Mbps;
        speedMbps = 1000u;
    }


    IfxGeth_mac_setLineSpeed(s_geth.gethSFR, speed);
    IfxGeth_mac_setDuplexMode(s_geth.gethSFR, duplex);

    s_macSynced = 1u;
    g_feStats.macSynced = 1u;
    g_feStats.phyLineSpeedMbps = speedMbps;
    g_feStats.phyDuplexFull = duplexFull;
    g_feStats.macCfg = s_geth.gethSFR->MAC_CONFIGURATION.U;

    return TRUE;
}

static void frame_eth_update_link_status(boolean force)
{
    uint32 status = 0u;
    uint32 newLink;

    if (s_phyFound == 0u)
    {
        g_feStats.linkUp = 0u;
        s_lastLinkUp = 0u;
        s_macSynced = 0u;
        g_feStats.macSynced = 0u;
        return;
    }

    if (!force)
    {
        uint32 now      = (uint32)IfxStm_getLower(&MODULE_STM0);
        uint32 interval = (uint32)IfxStm_getTicksFromMilliseconds(&MODULE_STM0, 100u);

        if ((uint32)(now - s_lastLinkPollStm) < interval)
            return;

        s_lastLinkPollStm = now;
    }
    else
    {
        s_lastLinkPollStm = (uint32)IfxStm_getLower(&MODULE_STM0);
    }

    /* Read BMSR twice; latch-low bits are cleared by the first read. */
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 1u, &status);
    IfxGeth_phy_Clause22_readMDIORegister(s_phyAddrRuntime, 1u, &status);

    g_feStats.phyBmsr = status;

    newLink = (((status & PHY_BMSR_LINK_STATUS) != 0u)
               && (s_geth.gethSFR->MAC_PHYIF_CONTROL_STATUS.B.LNKSTS != 0u)) ? 1u : 0u;
    g_feStats.linkUp = newLink;

    if (newLink != s_lastLinkUp)
    {
        g_feStats.linkTransitions++;
        s_lastLinkUp = newLink;

        if (newLink != 0u)
        {
            if (frame_eth_sync_mac_with_phy())
                frame_eth_recover_tx_ring(TRUE);
        }
        else
        {
            s_macSynced = 0u;
            g_feStats.macSynced = 0u;
        }
    }

    if (newLink != 0u && s_macSynced == 0u)
    {
        if (frame_eth_sync_mac_with_phy())
            frame_eth_recover_tx_ring(TRUE);
    }

    /* A debug-disconnect transient or OCDS event could clear MAC_CONFIGURATION.TE.
     * Re-enabling is harmless if it's already set. */
    if (newLink != 0u && s_macSynced != 0u)
    {
        if (s_geth.gethSFR->MAC_CONFIGURATION.B.TE == 0u)
        {
            IfxGeth_mac_enableTransmitter(s_geth.gethSFR);
            g_feStats.txRecoveries++;
        }
    }
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

/* ==================== TX ISR — required for standalone operation ==================== */

#if (FE_GETH_TX_ISR_PRIO > 0u)
IFX_INTERRUPT(ISR_frame_eth_geth_tx, 0, FE_GETH_TX_ISR_PRIO)
{
    /* Clear Transmit Interrupt (TI) + Normal Interrupt Summary (NIS) */
    MODULE_GETH.DMA_CH[0].STATUS.U = (1u << 0) | (1u << 15);  /* TI=bit0, NIS=bit15 */
}
#endif

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
    s_phyFound        = 0u;
    s_phyAddrRuntime  = 0u;
    s_lastLinkPollStm = 0u;
    s_lastTxRecoveryStm = 0u;
    s_lastRxRecoveryStm = 0u;
    s_lastLinkUp = 0u;
    s_macSynced = 0u;
    s_lastDiagTxStm = 0u;
    s_lastRxBufStm = (uint32)IfxStm_getLower(&MODULE_STM0);
    s_rxRbuSinceProgress = 0u;
    g_feStats.initStep = 1;

    /* ╔═══════════════════════════════════════════════════════════════════════╗
     * ║  STANDALONE FIX: 3-second boot delay + explicit module enable.        ║
     * ║  Without debugger, CPU boots instantly; PHY CLK125 (GREFCLK on        ║
     * ║  P11.5) needs time to stabilize. Without this, GETH TX hangs.         ║
     * ╚═══════════════════════════════════════════════════════════════════════╝ */
    {
        uint32 bootDelay = (uint32)IfxStm_getTicksFromMilliseconds(&MODULE_STM0, 3000u);
        uint32 bootStart = (uint32)IfxStm_getLower(&MODULE_STM0);
        while (((uint32)IfxStm_getLower(&MODULE_STM0) - bootStart) < bootDelay) {}
    }

    /* Explicit module enable BEFORE initModule (matches official Infineon example) */
    IfxGeth_enableModule(&MODULE_GETH);
    {
        uint32 enDelay = (uint32)IfxStm_getTicksFromMilliseconds(&MODULE_STM0, 100u);
        uint32 enStart = (uint32)IfxStm_getLower(&MODULE_STM0);
        while (((uint32)IfxStm_getLower(&MODULE_STM0) - enStart) < enDelay) {}
    }

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
    /* Full 8 KB to the single RX queue.  In store-and-forward the MAC holds a
     * complete packet in the FIFO before the DMA moves it, so 2560 bytes buffer
     * less than two 1330-byte AVTP packets: a 20-packet burst then overflows the
     * FIFO even though descriptors are still free (RBU never sets, but
     * GETH_RX_FIFO_OVERFLOW_PACKETS climbs).  8192 bytes hold six packets. */
    config.mtl.rxQueue[0].rxQueueSize    = IfxGeth_QueueSize_8192Bytes;
    config.mtl.rxQueue[0].rxDmaChannelMap = IfxGeth_RxDmaChannel_0;

    /* DMA: 1 TX channel, 1 RX channel */
    config.dma.numOfTxChannels = 1;
    config.dma.numOfRxChannels = 1;

    {
        IfxGeth_Index gethInst = IfxGeth_getIndex(&MODULE_GETH);

        memset(&s_txChannelConfig, 0, sizeof(s_txChannelConfig));
        memset(&s_rxChannelConfig, 0, sizeof(s_rxChannelConfig));
        s_txChannelConfig.channelEnable        = TRUE;
        s_txChannelConfig.channelId             = IfxGeth_TxDmaChannel_0;
        s_txChannelConfig.txDescrList           = &IfxGeth_Eth_txDescrList[gethInst][0];
        s_txChannelConfig.txBuffer1StartAddress = (uint32 *)&s_txBuf[0];
        s_txChannelConfig.txBuffer1Size         = FE_TX_BUF_SIZE;

        config.dma.txChannel[0] = s_txChannelConfig;

        s_rxChannelConfig.channelEnable        = TRUE;
        s_rxChannelConfig.channelId             = IfxGeth_RxDmaChannel_0;
        s_rxChannelConfig.rxDescrList           = &IfxGeth_Eth_rxDescrList[gethInst][0];
        s_rxChannelConfig.rxBuffer1StartAddress = (uint32 *)&s_rxBuf[0];
        s_rxChannelConfig.rxBuffer1Size         = FE_RX_BUF_SIZE;

        config.dma.rxChannel[0] = s_rxChannelConfig;

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

    /* Brief delay (~50 ms) for PHY power-up before MDIO scan */
    {
        volatile uint32 d = 10000000u;
        while (d--) {}
    }
    g_feStats.initStep = 5;

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
            s_phyFound       = 1u;
            s_phyAddrRuntime = phyAddr;

            frame_eth_configure_rtl8211f(phyAddr);
            g_feStats.initStep = 51;

            /* Brief delay for PHY to settle */
            {
                volatile uint32 d = 2000000u;
                while (d--) {}
            }
            g_feStats.initStep = 52;

            /* Poll link status (reg 1, bit 2) — read twice per IEEE spec */
            {
                uint32 timeout = 2000000u;
                do
                {
                    frame_eth_update_link_status(TRUE);
                    if (g_feStats.linkUp != 0u)
                        break;
                } while (--timeout);
            }
            g_feStats.initStep = 53;
        }
    }

    /* Enable TX/RX only after the PHY has been reset/configured.  This follows
     * the ordering used by the Infineon Ethernet examples and avoids starting
     * the DMA while the RTL8211F is still re-negotiating.
     */
    IfxGeth_Eth_startTransmitters(&s_geth, 1u);
    g_feStats.initStep = 60;

    /* Enable receiver for command packets from PC (ethertype 0x88B5, magic "CM").
     * We MUST poll frame_eth_poll_rx() in every main-loop iteration to drain
     * all incoming buffers (including broadcast ARP/mDNS) and prevent the 8-deep
     * RX DMA descriptor ring from being exhausted. */
    IfxGeth_Eth_startReceivers(&s_geth, 1u);
    g_feStats.initStep = 61;

    /* STANDALONE FIX: Enable DMA TX interrupt (NIE + TIE) so the ISR can clear
     * the TI flag.  Without this, DMA TX stalls after first TBU in standalone. */
#if (FE_GETH_TX_ISR_PRIO > 0u)
    s_geth.gethSFR->DMA_CH[0].INTERRUPT_ENABLE.B.NIE = 1u;
    s_geth.gethSFR->DMA_CH[0].INTERRUPT_ENABLE.B.TIE = 1u;
#endif
    g_feStats.initStep = 62;

    /* Clear frame buffers */
    memset(s_frameBufA, 0, FE_MAX_FRAME_BYTES);
    memset(s_frameBufB, 0, FE_MAX_FRAME_BYTES);
    memset(s_txFrameBuf, 0, sizeof(s_txFrameBuf));

    /* Reset assembly state */
    frame_eth_reset_frame_state();

    g_feStats.initStep = 99;
    g_feStats.initDone = 1;
}

/* ==================== Device switching ==================== */

void frame_eth_set_pass_all_multicast(boolean enable)
{
    GETH_MAC_PACKET_FILTER.B.PM = enable ? 1u : 0u;
    g_feStats.macPassAllMulticast = GETH_MAC_PACKET_FILTER.B.PM;
}

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
    s_displaySeq   = 0;
    s_txActive     = 0u;
    s_txFragIdx    = 0u;
    s_txFragCnt    = 0u;
    s_txOffset     = 0u;
    s_txRemaining  = 0u;
    s_txTimestamp  = 0u;
    s_txPixels     = NULL_PTR;
    s_txPendingDescr = NULL_PTR;
    g_feStats.txLastFailReason = FE_TX_FAIL_NONE;
    g_feStats.txLastFailFrag   = 0u;
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
    s_displaySeq++;
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
        if (s_rowCount == FE_NICHIA_H && !s_frameReady)
        {
            s_readyIdx   = s_assembleIdx;
            s_displaySeq++;
            s_frameReady = TRUE;
            g_feStats.nichiaFramesAssembled++;
            s_assembleIdx = (uint8)(1u - s_assembleIdx);
            camera_trigger_fire_sync();
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
        s_displaySeq++;
        s_frameReady = TRUE;
        g_feStats.nichiaFramesAssembled++;
        s_assembleIdx = (uint8)(1u - s_assembleIdx);
        s_rowCount    = 0;
        s_nextRow     = 0;
        camera_trigger_fire_sync();
    }
}

/* ==================== Osram complete frame push ==================== */

void frame_eth_push_osram_frame(const uint8 *pixels, uint32 len)
{
    if (len > FE_MAX_FRAME_BYTES)
        len = FE_MAX_FRAME_BYTES;

    /* An OSRAM frame is larger than s_txFrameBuf, so the transmitter fragments
     * straight out of the assembly buffer over several main-loop passes.
     * Writing the buffer it is still reading would put a torn frame on the
     * wire, so skip this push instead. */
    if ((s_txActive != 0u) && (s_txPixels == s_framePtr[s_assembleIdx]))
    {
        g_feStats.pushSkippedTxBusy++;
        return;
    }

    /* Copy into assembly buffer and mark ready immediately */
    memcpy(s_framePtr[s_assembleIdx], pixels, len);
    s_frameTimestamp = (uint32)IfxStm_getLower(&MODULE_STM0);

    s_readyIdx   = s_assembleIdx;
    s_displaySeq++;
    s_frameReady = TRUE;
    g_feStats.osramFramesPushed++;

    s_assembleIdx = (uint8)(1u - s_assembleIdx);
}

/* ==================== Display frame access (CPU1) ==================== */

const uint8 *frame_eth_get_display_frame(uint16 *width, uint16 *height, uint32 *seqNum)
{
    if (s_displaySeq == 0)
        return NULL_PTR;   /* no frame ever completed */

    /* Return the last completed frame selected by producer side. */
    uint8 idx = s_readyIdx;
    *width    = s_width;
    *height   = s_height;
    *seqNum   = s_displaySeq;

    return (const uint8 *)s_framePtr[idx];
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

static uint8 *frame_eth_get_tx_buffer(void)
{
    if (!frame_eth_tx_pending_complete())
        return NULL_PTR;

    /* Use the EXACT iLLD function — same as the official Ethernet example.
     * getTransmitBuffer checks OWN==0 on the current descriptor and returns
     * the buffer pointer (TDES0.U). Returns NULL if descriptor is busy. */
    return (uint8 *)IfxGeth_Eth_getTransmitBuffer(&s_geth, IfxGeth_TxDmaChannel_0);
}

/* Use the EXACT iLLD sendTransmitBuffer function for TX submission.
 * This function: sets TDES3 fields (FL, TSE, CIC, SAIC, CPC),
 * sets B1L+IOC on last descriptor, sets OWN=1, shuffles descriptor,
 * sets FD=1 on first, writes tail pointer, calls wakeupTransmitter,
 * advances txDescrPtr.  All proven to work in the official Ethernet example. */
static void frame_eth_send_packet(uint32 packetLength)
{
    volatile IfxGeth_TxDescr *descr;

    /* Remember which descriptor we just submitted so tx_pending_complete
     * can poll its OWN bit before the next send. */
    descr = IfxGeth_Eth_getActualTxDescriptor(&s_geth, IfxGeth_TxDmaChannel_0);

    /* Call the standard iLLD function — identical to what the lwIP netif uses */
    IfxGeth_Eth_sendTransmitBuffer(&s_geth, packetLength, IfxGeth_TxDmaChannel_0);

    s_txPendingDescr = descr;
}

static boolean send_fragment(const uint8 *framePixels, uint16 frameSeq,
                             uint8 fragIdx, uint8 fragCnt,
                             uint16 dataOffset, uint16 dataLen,
                             uint32 timestamp)
{
    uint32 ethPayload;
    uint32 ethTotal;
    uint8 *pTxBuf;
    uint8 *hdr;

    ethPayload = FE_HDR_LEN + dataLen;
    ethTotal   = 14u + ethPayload;

    frame_eth_update_link_status(FALSE);
    if ((g_feStats.linkUp == 0u) || (s_macSynced == 0u))
    {
        g_feStats.txLastFailReason = FE_TX_FAIL_LINK;
        g_feStats.txLastFailFrag   = fragIdx;
        return FALSE;
    }

    if (ethTotal < 60u)
        ethTotal = 60u;

    pTxBuf = frame_eth_get_tx_buffer();
    if (pTxBuf == NULL_PTR)
    {
        IfxGeth_Eth_wakeupTransmitter(&s_geth, IfxGeth_TxDmaChannel_0);
        g_feStats.txWakeups++;
        frame_eth_snapshot_tx_dma();
        g_feStats.txNoBuffer++;
        g_feStats.txLastFailReason = FE_TX_FAIL_NO_BUFFER;
        g_feStats.txLastFailFrag   = fragIdx;
        frame_eth_recover_tx_ring(FALSE);
        return FALSE;
    }

    memcpy(&pTxBuf[0], s_dstMac, 6u);
    memcpy(&pTxBuf[6], s_srcMac, 6u);
    put_be16(&pTxBuf[12], FE_ETHERTYPE);

    hdr = &pTxBuf[14];
    put_be16(&hdr[0],  s_magic);
    put_be16(&hdr[2],  frameSeq);
    hdr[4] = fragIdx;
    hdr[5] = fragCnt;
    put_be16(&hdr[6],  dataOffset);
    put_be16(&hdr[8],  dataLen);
    put_be16(&hdr[10], s_width);
    put_be16(&hdr[12], s_height);
    put_be32(&hdr[14], timestamp);

    memcpy(&pTxBuf[14u + FE_HDR_LEN], &framePixels[dataOffset], dataLen);

    if (ethTotal > (14u + ethPayload))
        memset(&pTxBuf[14u + ethPayload], 0, ethTotal - (14u + ethPayload));

    frame_eth_send_packet(ethTotal);
    g_feStats.fragmentsSent++;
    return TRUE;
}

static boolean send_can_diag_record(const CanDiagRecord *record, uint16 sequence)
{
    uint32 ethPayload;
    uint32 ethTotal;
    uint8 *pTxBuf;
    uint8 *hdr;
    uint8 *payload;

    if (record == NULL_PTR)
    {
        g_feStats.diagTxErrors++;
        return FALSE;
    }

    ethPayload = FE_DIAG_HDR_LEN + FE_DIAG_PAYLOAD_LEN;
    ethTotal   = 14u + ethPayload;

    frame_eth_update_link_status(FALSE);
    if ((g_feStats.linkUp == 0u) || (s_macSynced == 0u))
    {
        g_feStats.diagTxErrors++;
        g_feStats.txLastFailReason = FE_TX_FAIL_LINK;
        g_feStats.txLastFailFrag   = 0u;
        return FALSE;
    }

    if (ethTotal < 60u)
        ethTotal = 60u;

    pTxBuf = frame_eth_get_tx_buffer();
    if (pTxBuf == NULL_PTR)
    {
        IfxGeth_Eth_wakeupTransmitter(&s_geth, IfxGeth_TxDmaChannel_0);
        g_feStats.txWakeups++;
        frame_eth_snapshot_tx_dma();
        g_feStats.txNoBuffer++;
        g_feStats.diagTxErrors++;
        frame_eth_recover_tx_ring(FALSE);
        return FALSE;
    }

    memcpy(&pTxBuf[0], s_dstMac, 6u);
    memcpy(&pTxBuf[6], s_srcMac, 6u);
    put_be16(&pTxBuf[12], FE_ETHERTYPE);

    hdr = &pTxBuf[14];
    put_be16(&hdr[0], FE_MAGIC_CAN_DIAG);
    hdr[2] = CAN_DIAG_PROTOCOL_VERSION;
    hdr[3] = CAN_DIAG_RECORD_TYPE_REG_IO;
    put_be16(&hdr[4], sequence);
    put_be16(&hdr[6], FE_DIAG_PAYLOAD_LEN);

    payload = &pTxBuf[14u + FE_DIAG_HDR_LEN];
    put_be32(&payload[0],  record->sourceTimestamp);
    put_be16(&payload[4],  record->address);
    put_be16(&payload[6],  record->responseDelayUs);
    put_be16(&payload[8],  record->interFrameDelayUs);
    put_be32(&payload[10], record->value);
    put_be32(&payload[14], record->checksum);
    payload[18] = record->deviceId;
    payload[19] = record->operation;
    payload[20] = record->status;
    payload[21] = record->valueLen;

    {
        uint8 rawLen;

        rawLen = (record->valueLen < CAN_DIAG_RAW_MAX) ? record->valueLen : (uint8)CAN_DIAG_RAW_MAX;
        if (rawLen > 0u)
            memcpy(&payload[FE_DIAG_PAYLOAD_FIXED], record->rawPayload, rawLen);
        if (rawLen < FE_DIAG_PAYLOAD_RAW_MAX)
            memset(&payload[FE_DIAG_PAYLOAD_FIXED + rawLen], 0,
                   FE_DIAG_PAYLOAD_RAW_MAX - rawLen);
    }

    if (ethTotal > (14u + ethPayload))
        memset(&pTxBuf[14u + ethPayload], 0, ethTotal - (14u + ethPayload));

    frame_eth_send_packet(ethTotal);
    g_feStats.diagRecordsSent++;
    return TRUE;
}

static void frame_eth_begin_tx_from_ready(void)
{
    uint32 bytes;

    bytes = s_frameBytes;
    if (bytes > FE_MAX_FRAME_BYTES)
        bytes = FE_MAX_FRAME_BYTES;

    if (bytes <= (uint32)sizeof(s_txFrameBuf))
    {
        memcpy(s_txFrameBuf, s_framePtr[s_readyIdx], bytes);
        s_txPixels = s_txFrameBuf;
    }
    else
    {
        s_txPixels = s_framePtr[s_readyIdx];
    }

    s_frameReady  = FALSE;
    s_txActive    = 1u;
    s_txSeq       = s_frameSeq++;
    s_txFragCnt   = (uint8)((bytes + FE_MAX_PAYLOAD - 1u) / FE_MAX_PAYLOAD);
    s_txFragIdx   = 0u;
    s_txOffset    = 0u;
    s_txRemaining = bytes;
    s_txTimestamp = s_frameTimestamp;

    g_feStats.txFramesQueued++;
}

boolean frame_eth_send_pending(void)
{
    uint16 chunkLen;
    uint8  burstCount = 0u;

    if (s_txActive != 0u && s_frameReady)
    {
        s_frameReady = FALSE;
        g_feStats.txReadyDropped++;
    }

    if (s_txActive == 0u && !s_frameReady)
        return FALSE;

    if (s_txActive == 0u)
    {
        frame_eth_update_link_status(FALSE);
        if (g_feStats.linkUp == 0u || s_macSynced == 0u)
        {
            s_frameReady = FALSE;
            g_feStats.txReadyDropped++;
            g_feStats.txLastFailReason = FE_TX_FAIL_LINK;
            g_feStats.txLastFailFrag   = 0u;
            return FALSE;
        }

        frame_eth_begin_tx_from_ready();
    }

    if (s_txRemaining == 0u || s_txFragCnt == 0u || s_txPixels == NULL_PTR)
    {
        s_txActive = 0u;
        s_txPixels = NULL_PTR;
        return FALSE;
    }

    /* Send a bounded burst per call to keep main-loop fairness under control.
     * The next call continues from s_txFragIdx/s_txOffset/s_txRemaining. */
    while (s_txRemaining > 0u && burstCount < FE_TX_FRAG_BURST_MAX)
    {
        chunkLen = (s_txRemaining > FE_MAX_PAYLOAD)
                 ? (uint16)FE_MAX_PAYLOAD
                 : (uint16)s_txRemaining;

        if (!send_fragment(s_txPixels, s_txSeq, s_txFragIdx, s_txFragCnt,
                           s_txOffset, chunkLen, s_txTimestamp))
        {
            g_feStats.txRetries++;
            return FALSE;
        }

        s_txOffset    = (uint16)(s_txOffset + chunkLen);
        s_txRemaining -= chunkLen;
        s_txFragIdx++;
        burstCount++;
    }

    if (s_txRemaining > 0u)
        return FALSE;

    s_txActive = 0u;
    s_txPixels = NULL_PTR;
    g_feStats.framesSent++;
    g_feStats.txLastFailReason = FE_TX_FAIL_NONE;
    g_feStats.txLastFailFrag   = 0u;
    return TRUE;
}

boolean frame_eth_send_can_diag_pending(void)
{
    CanDiagRecord record;
    boolean sent = FALSE;

    if (s_txActive != 0u || s_frameReady)
        return FALSE;

    frame_eth_update_link_status(FALSE);
    if (g_feStats.linkUp == 0u || s_macSynced == 0u)
        return FALSE;

    /* Pace the diag TX: at most one record per FE_DIAG_TX_INTERVAL_US.  This
     * caps GETH TX pressure so the CPU0 loop keeps draining the LVDS RX DMA in
     * time (no LVDS CRC bursts / flicker) while still forwarding the monitor
     * trace fast enough for the UART Monitor.  Excess records stay in the
     * bounded can_diag queue (oldest dropped) — acceptable for telemetry. */
    {
        uint32 now = (uint32)IfxStm_getLower(&MODULE_STM0);
        uint32 minGap = (uint32)IfxStm_getTicksFromMicroseconds(&MODULE_STM0,
                                                                FE_DIAG_TX_INTERVAL_US);
        if ((uint32)(now - s_lastDiagTxStm) < minGap)
            return FALSE;
        s_lastDiagTxStm = now;
    }

    if (can_diag_pop_record(&record))
    {
        if (send_can_diag_record(&record, s_diagSeq++))
            sent = TRUE;
    }

    return sent;
}

/* ==================== Ethernet RX — command processing ==================== */

#include "device_mode.h"   /* device_mode_set() */
#include "adapter_ctrl.h"  /* adapter_ctrl_set_mode/can_uart/apply() */
#include "can_uart_bridge.h" /* can_uart_bridge_set_active() */
#include "can_hw.h"        /* g_diagSniffEnabled */
#include "can_uart_fault_inject.h"
#include "lvds_fault_inject.h"
#include "defect_inject.h" /* defect_inject_set_list() */
#include "nichia_defect_inject.h" /* nichia_defect_inject_set_list() */
#include "lvds_tx.h"        /* lvds_tx_enable/disable() */
#include "avtp_rx.h"        /* avtp_rx_handle_packet() */
#include "can_uart_master.h" /* Direct Control Mode LSM master */

void frame_eth_poll_rx(void)
{
    uint32 budget = FE_RX_POLL_BUDGET;
    uint8  processedThisPass = 0u;
    uint32 now;
    uint32 rxStatus;

    frame_eth_update_link_status(FALSE);

    /* Drain ALL available RX buffers.  Every buffer MUST be freed even if the
     * frame is not for us, otherwise the 8-deep descriptor ring fills up and
     * the DMA enters an abnormal state (bus error trap). */
    while (IfxGeth_Eth_isRxDataAvailable(&s_geth, IfxGeth_RxDmaChannel_0))
    {
        if (budget-- == 0u)
        {
            /* A sticky RX-available status (descriptor/ring transient) must not
             * monopolize the CPU0 main loop forever; bail out and retry next
             * iteration so LVDS parsing and TX keep progressing. */
            g_feStats.rxPollBudgetHits++;
            break;
        }

        processedThisPass = 1u;   /* RX ring is making progress */

        /* The packet length lives in the descriptor write-back format and must
         * be read before the buffer is handed over, so a short packet can never
         * be parsed together with stale bytes left in the ring buffer. */
        uint32 rxLen;
        {
            volatile IfxGeth_RxDescr *descr =
                IfxGeth_Eth_getActualRxDescriptor(&s_geth, IfxGeth_RxDmaChannel_0);
            rxLen = (uint32)descr->RDES3.W.PL;
            if (rxLen > FE_RX_BUF_SIZE)
                rxLen = FE_RX_BUF_SIZE;
        }

        uint8 *pRxBuf = (uint8 *)IfxGeth_Eth_getReceiveBuffer(&s_geth, IfxGeth_RxDmaChannel_0);
        if (pRxBuf == NULL_PTR)
        {
            /* Shouldn't happen when isRxDataAvailable returned TRUE, but be safe */
            g_feStats.rxNullBuffers++;
            IfxGeth_Eth_freeReceiveBuffer(&s_geth, IfxGeth_RxDmaChannel_0);
            continue;
        }

        /* Ethernet header: [12..13] = EtherType */
        uint16 etherType = ((uint16)pRxBuf[12] << 8) | pRxBuf[13];

        /* AVTP pixel stream for the Direct Control Mode generator.  VLAN tags
         * are handled inside the reassembler, so both tagged and untagged
         * frames reach it. */
        if ((etherType == AVTP_ETHERTYPE) ||
            (etherType == AVTP_VLAN_TPID) ||
            (etherType == AVTP_VLAN_TPID_STACKED))
        {
            (void)avtp_rx_handle_packet(pRxBuf, rxLen);
        }
        else if (etherType == FE_ETHERTYPE)
        {
            /* Protocol header: [14..15] = Magic */
            uint16 magic = ((uint16)pRxBuf[14] << 8) | pRxBuf[15];

            if (magic == FE_MAGIC_COMMAND)
            {
                g_feStats.cmdPacketsReceived++;
                uint8 cmdId      = pRxBuf[16];
                uint8 cmdPayload = pRxBuf[17];

                if (cmdId == FE_CMD_SET_DEVICE)
                {
                    g_feStats.cmdSetDeviceReceived++;
                    FrameEthDevice newDev = (cmdPayload == (uint8)FE_DEVICE_OSRAM)
                                          ? FE_DEVICE_OSRAM
                                          : FE_DEVICE_NICHIA;
                    if (lvds_fault_is_active() && newDev == device_mode_get())
                    {
                        /* The application sends CLEAR before an intentional
                         * device change. Ignore stale/in-flight mode packets
                         * while the physical LVDS fault owns the selector. */
                        g_feStats.cmdSetDeviceIgnoredDuringLvds++;
                    }
                    else
                    {
                        device_mode_set(newDev);
                        g_feStats.cmdSetDeviceApplied++;
                    }
                }
                else if (cmdId == FE_CMD_DIAG_SNIFF)
                {
                    /* START (payload != 0): reset the monitor sequence number +
                     * queue for a clean trace.
                     * STOP (payload == 0): pause the Ethernet TX of monitor
                     * records only; the ASCLIN4/ASCLIN5 bridge keeps running. */
                    uint8 newState = (cmdPayload != 0u) ? 1u : 0u;
                    if (newState)
                    {
                        /* START: reset the monitor sequence + queue for a clean
                         * trace.  The CAN-UART data now comes from the ASCLIN4/
                         * ASCLIN5 bridge (CPU2); there is no ASCLIN9 to reinit. */
                        s_diagSeq = 0u;               /* restart Nr from 0     */
                        can_diag_reset();             /* clear send queue + stats     */
                    }
                    g_diagSniffEnabled = newState;
                }
                else if (cmdId == FE_CMD_SET_ADAPTER)
                {
                    /* Payload: [17] = control_mode (0=ECU, 1=Direct)
                     *          [18] = can_uart_mode (0=ECU, 1=Direct, 2=External) */
                    uint8 ctrlMode = cmdPayload;       /* byte [17] */
                    uint8 canMode  = pRxBuf[18];       /* byte [18] */
                    boolean sameAdapterMode;
                    adapter_control_mode_t ctrlEnum = (ctrlMode != 0u)
                        ? ADAPTER_MODE_DIRECT : ADAPTER_MODE_ECU;
                    adapter_can_uart_mode_t canEnum = CAN_UART_ECU_LSM;
                    boolean physicalFaultActive;
                    if (canMode == 1u) canEnum = CAN_UART_ECU_SMARTVISIO_LSM;
                    else if (canMode == 2u) canEnum = CAN_UART_SMARTVISIO_LSM;

                    sameAdapterMode =
                        (ctrlEnum == adapter_ctrl_get_mode()) &&
                        (canEnum == adapter_ctrl_get_can_uart());
                    physicalFaultActive = lvds_fault_is_active() ||
                                          can_uart_fault_is_active();

                    /* A delayed/redundant SET_ADAPTER_MODE matching the
                     * current routing must not cancel a physical LVDS fault. */
                    if (physicalFaultActive && sameAdapterMode)
                    {
                        if (lvds_fault_is_active())
                            g_feStats.cmdSetAdapterIgnoredDuringLvds++;
                        if (can_uart_fault_is_active())
                            g_feStats.cmdSetAdapterIgnoredDuringCanUart++;
                    }
                    else
                    {
                        if (lvds_fault_is_active())
                            lvds_fault_clear();

                        /* Leaving Direct Control Mode: stop the local LVDS
                         * generator and give P02.2 back to the GPIO idle level
                         * BEFORE the TTL selector returns to the ECU source. */
                        if (ctrlEnum != ADAPTER_MODE_DIRECT)
                        {
                            can_uart_master_stop();
                            /* The PC repeats SET_ADAPTER_MODE, so the receive
                             * path is only rebuilt on the actual transition. */
                            if (lvds_tx_is_enabled())
                            {
                                lvds_tx_disable();
                                device_mode_restore_rx_path();
                            }
                            frame_eth_set_pass_all_multicast(FALSE);
                        }

                        /* Route the bus FIRST (set CAN_SEL), then start or
                         * stop the active forwarding bridge. */
                        adapter_ctrl_apply(ctrlEnum, canEnum);
                        can_uart_fault_clear();
                        can_uart_bridge_set_active(
                            (canEnum == CAN_UART_ECU_SMARTVISIO_LSM) ? TRUE : FALSE);

                        /* Entering Direct Control Mode: the selector already
                         * points at the local source and P02.2 still idles
                         * HIGH, so ASCLIN1 can safely take the pin over. */
                        if (ctrlEnum == ADAPTER_MODE_DIRECT)
                        {
                            if (lvds_tx_enable(device_mode_get()))
                            {
                                avtp_rx_reset();
                                /* The AVTP multicast destination is only
                                 * accepted while the generator needs it. */
                                frame_eth_set_pass_all_multicast(TRUE);
                                /* The LSM rejects video while it is still
                                 * booting, so the line stays idle until the
                                 * CAN-UART start-up sequence has finished.
                                 * direct_mode_tick() switches to the stream. */
                                lvds_tx_set_source(LVDS_TX_SOURCE_IDLE);
                            }

                            /* CAN_SEL already routes the bus through the AURIX
                             * transceivers and the forwarding bridge is off, so
                             * the master can drive the LSM side alone.  In the
                             * uploaded-trace test mode, hold the bus idle until
                             * the PC commits the new startup sequence. */
                            if (device_mode_get() == FE_DEVICE_OSRAM ||
                                device_mode_get() == FE_DEVICE_NICHIA)
                                can_uart_master_wait_for_uploaded_start();
                        }
                    }
                }
                else if (cmdId == FE_CMD_OSRAM_SEQ_STEP)
                {
                    /* Payload: index(2), total(2), gapUs(4), len(1), read(1), data(10). */
                    uint16 index;
                    uint16 total;
                    uint32 gapUs;
                    uint8 len;
                    uint8 expectResponse;

                    if (rxLen >= 37u)
                    {
                        index = (uint16)(((uint16)pRxBuf[17] << 8u) | pRxBuf[18]);
                        total = (uint16)(((uint16)pRxBuf[19] << 8u) | pRxBuf[20]);
                        gapUs = ((uint32)pRxBuf[21] << 24u) |
                                ((uint32)pRxBuf[22] << 16u) |
                                ((uint32)pRxBuf[23] << 8u) | pRxBuf[24];
                        len = pRxBuf[25];
                        expectResponse = pRxBuf[26];
                        (void)can_uart_master_stage_step(index, total, gapUs, len,
                                                          expectResponse, &pRxBuf[27]);
                    }
                }
                else if (cmdId == FE_CMD_NICHIA_SEQ_STEP)
                {
                    /* Payload: index(2), total(2), gapUs(4), len(1), read(1), data(72). */
                    uint16 index;
                    uint16 total;
                    uint32 gapUs;
                    uint8 len;
                    uint8 expectResponse;

                    if (rxLen >= 99u)
                    {
                        index = (uint16)(((uint16)pRxBuf[17] << 8u) | pRxBuf[18]);
                        total = (uint16)(((uint16)pRxBuf[19] << 8u) | pRxBuf[20]);
                        gapUs = ((uint32)pRxBuf[21] << 24u) |
                                ((uint32)pRxBuf[22] << 16u) |
                                ((uint32)pRxBuf[23] << 8u) | pRxBuf[24];
                        len = pRxBuf[25];
                        expectResponse = pRxBuf[26];
                        if (index == 0u)
                            can_uart_master_wait_for_uploaded_start();
                        (void)can_uart_master_stage_step(index, total, gapUs, len,
                                                          expectResponse, &pRxBuf[27]);
                    }
                }
                else if (cmdId == FE_CMD_OSRAM_SEQ_COMMIT)
                {
                    uint16 total;

                    if (rxLen >= 19u)
                    {
                        total = (uint16)(((uint16)pRxBuf[17] << 8u) | pRxBuf[18]);
                        (void)can_uart_master_commit_staged(total);
                    }
                }
                else if (cmdId == FE_CMD_NICHIA_SEQ_COMMIT)
                {
                    uint16 total;

                    if (rxLen >= 19u)
                    {
                        total = (uint16)(((uint16)pRxBuf[17] << 8u) | pRxBuf[18]);
                        (void)can_uart_master_commit_staged(total);
                    }
                }
                else if (cmdId == FE_CMD_NICHIA_SEQ_HARDCODED)
                {
                    /* Ignore all previously uploaded trace steps and start
                     * the compiled Nichia ECU-equivalent sequence. */
                    can_uart_master_start_hardcoded_nichia();
                }
                else if (cmdId == FE_CMD_CAN_UART_FAULT)
                {
                    /* Payload: [17] = mode, [18] = direction,
                     * [19..20] = duration in milliseconds, [21] = action.
                     * action 0 clears the current fault; action 1 starts the
                     * requested DROP or RELAY_BYPASS fault. */
                    uint8 mode = cmdPayload;
                    uint8 direction = pRxBuf[18];
                    uint16 duration = (uint16)(((uint16)pRxBuf[19] << 8u) | pRxBuf[20]);
                    uint8 canUartMode = pRxBuf[22];

                    if (pRxBuf[21] == 0u)
                    {
                        boolean ownsCanSel = can_uart_fault_owns_can_sel();
                        can_uart_fault_clear();
                        if (ownsCanSel)
                            adapter_ctrl_set_can_bridge(FALSE);
                        else if (mode == (uint8)CAN_UART_FAULT_RELAY_BYPASS)
                            can_uart_bridge_set_active(TRUE);
                    }
                    else
                    {
                        if (can_uart_fault_set((CanUartFaultMode)mode,
                                               (CanUartFaultDirection)direction,
                                               duration,
                                               canUartMode) &&
                            mode == (uint8)CAN_UART_FAULT_RELAY_BYPASS)
                        {
                            /* Stop forwarding before selecting the AURIX-side
                             * relay path, so no new byte is driven into a
                             * transition between the two hardware routes. */
                            can_uart_bridge_set_active(FALSE);
                            if (canUartMode == 0u)
                                adapter_ctrl_set_can_bridge(TRUE);
                        }
                    }
                }
                else if (cmdId == FE_CMD_LVDS_FAULT)
                {
                    /* Payload: [17] = mode, [18] = profile,
                     * [19..20] = duration in milliseconds, [21] = action.
                     * Phase 1 supports SELECT_LOCAL_IDLE only. */
                    uint8 mode = cmdPayload;
                    uint8 profile = pRxBuf[18];
                    uint16 duration = (uint16)(((uint16)pRxBuf[19] << 8u) | pRxBuf[20]);
                    g_feStats.cmdLvdsFaultReceived++;

                    if (pRxBuf[21] == 0u)
                    {
                        g_feStats.cmdLvdsFaultCleared++;
                        lvds_fault_clear();
                    }
                    else
                    {
                        if (lvds_fault_set((LvdsFaultMode)mode,
                                           duration,
                                           profile))
                            g_feStats.cmdLvdsFaultApplied++;
                        else
                            g_feStats.cmdLvdsFaultRejected++;
                    }
                }
                else if (cmdId == FE_CMD_SET_DEFECT_LIST)
                {
                    /* Payload: [17] = enable (0/1)
                     *          [18] = count  (0..64 defect records)
                     *          [19..] = count x 5 bytes:
                     *              [slot][x_hi][x_lo][y][status]
                     *              status = (pxState << 2) | (pxDiag & 0x03)
                     * The list only DEFINES defects; the actual ELEDERP/ELEDERS
                     * injection is done in-flight by the CPU2 bridge filter. */
                    uint8 enable = cmdPayload;      /* byte [17] */
                    uint8 count  = pRxBuf[18];      /* byte [18] */
                    defect_inject_set_list(enable, &pRxBuf[19], count);
                }
                else if (cmdId == FE_CMD_SET_DEFECT_LIST_NICHIA)
                {
                    /* Payload: [17] = enable (0/1)
                     *          [18] = count  (0..64 defect records)
                     *          [19..] = count x 4 bytes:
                     *              [idx_hi][idx_lo][type][segPair]
                     *              idx = pixel_index (row*256+col), type 0=dark/1=bright.
                     * The list only DEFINES defects; the actual PIXEL_ID/counter/flag
                     * injection is done in-flight by the CPU2 bridge filter. */
                    uint8 enable = cmdPayload;      /* byte [17] */
                    uint8 count  = pRxBuf[18];      /* byte [18] */
                    nichia_defect_inject_set_list(enable, &pRxBuf[19], count);
                }
            }
        }

        /* Always free — even for unrecognised frames */
        IfxGeth_Eth_freeReceiveBuffer(&s_geth, IfxGeth_RxDmaChannel_0);
    }

    /* A RELAY_BYPASS timeout is detected by CPU2, but CAN_SEL is restored by
     * CPU0, which owns the Ethernet command path and adapter state changes. */
    if (can_uart_fault_take_bypass_expired())
    {
        if (can_uart_fault_take_can_sel_expired())
            adapter_ctrl_set_can_bridge(FALSE);
        else
            can_uart_bridge_set_active(TRUE);
    }

    /* ---- Publish the freed descriptors to the DMA ----
     * IfxGeth_Eth_freeReceiveBuffer() only hands the descriptor back (OWN = 1)
     * and advances the software pointer; it never moves RXDESC_TAIL_POINTER.
     * The EQoS receive DMA stops when it reaches the tail, so on a busy ring it
     * suspends and stays suspended: IfxGeth_Eth_wakeupReceiver() only restarts
     * it when the receive-process-stopped flag is set, which an RBU suspend does
     * not set.  The result was a complete receive stall every few seconds until
     * the watchdog re-initialised the ring.
     * Moving the tail to the last descriptor we returned keeps the whole ring
     * minus one slot available to the DMA. */
    if (processedThisPass != 0u)
    {
        volatile IfxGeth_RxDescr *base =
            IfxGeth_Eth_getBaseRxDescriptor(&s_geth, IfxGeth_RxDmaChannel_0);
        volatile IfxGeth_RxDescr *cur =
            IfxGeth_Eth_getActualRxDescriptor(&s_geth, IfxGeth_RxDmaChannel_0);
        volatile IfxGeth_RxDescr *tail =
            (cur == base) ? &base[FE_RX_DESCRIPTORS - 1u] : (cur - 1);

        IfxGeth_dma_setRxDescriptorTailPointer(s_geth.gethSFR,
                                               IfxGeth_RxDmaChannel_0,
                                               (uint32)tail);
    }

    /* ---- RX freeze watchdog (DMA hardware evidenced, traffic-independent) ----
     * Snapshot + clear the RBU latch.  A freshly set RBU means the DMA received
     * a packet since the previous poll but had no free descriptor to store it.
     * Try a cheap wakeup on every fresh RBU.  If packets keep arriving (RBU)
     * yet no buffer surfaces for FE_RX_STALL_MS while the link is up, the ring
     * is desynchronised (the command freeze): escalate to a full in-place
     * re-init, which is the only thing that reliably un-sticks it. */
    now = (uint32)IfxStm_getLower(&MODULE_STM0);
    rxStatus = s_geth.gethSFR->DMA_CH[IfxGeth_DmaChannel_0].STATUS.U;
    g_feStats.rxDmaStatus = rxStatus;

    /* MAC/MTL-level RX health snapshot.  A freeze that happens upstream of
     * the DMA descriptor ring (e.g. RX FIFO stuck/overflowing) never sets
     * FE_DMA_STATUS_RBU and never triggers frame_eth_recover_rx_ring(), so
     * rxDmaStatus alone can look perfectly normal while commands stop
     * arriving.  These read-only registers catch that case for diagnosis. */
    g_feStats.macDebugRpeSts        = GETH_MAC_DEBUG.B.RPESTS;
    g_feStats.macDebugRfcfcSts      = GETH_MAC_DEBUG.B.RFCFCSTS;
    g_feStats.rxFifoOverflowPackets = GETH_RX_FIFO_OVERFLOW_PACKETS.B.RXFIFOOVFL;

    if (g_feStats.rxFifoOverflowPackets != s_lastRxFifoOverflowCount)
    {
        /* FIFO keeps dropping packets since the previous poll: same liveness
         * meaning as a fresh RBU latch, just one stage further upstream. */
        s_lastRxFifoOverflowCount = g_feStats.rxFifoOverflowPackets;
        s_rxFifoOverflowSinceProgress = 1u;
    }

    if ((rxStatus & FE_DMA_STATUS_RBU) != 0u)
    {
        /* Write-1-to-clear the latch so the next poll reflects fresh activity. */
        s_geth.gethSFR->DMA_CH[IfxGeth_DmaChannel_0].STATUS.U = FE_DMA_STATUS_RBU;
        s_rxRbuSinceProgress = 1u;
        /* First-line, non-destructive resume attempt. */
        IfxGeth_Eth_wakeupReceiver(&s_geth, IfxGeth_RxDmaChannel_0);
    }

    if (processedThisPass != 0u)
    {
        s_lastRxBufStm = now;
        s_rxRbuSinceProgress = 0u;
        s_rxFifoOverflowSinceProgress = 0u;
    }
    else if (g_feStats.linkUp != 0u &&
             (s_rxRbuSinceProgress != 0u || s_rxFifoOverflowSinceProgress != 0u) &&
             (uint32)(now - s_lastRxBufStm) >=
                 (uint32)IfxStm_getTicksFromMilliseconds(&MODULE_STM0, FE_RX_STALL_MS))
    {
        /* Packets are arriving but never surface: the freeze.  Reinitialise the
         * RX descriptor ring in place (force = bypass the rate limiter).  This
         * now also covers the MAC-level FIFO-overflow freeze mode confirmed
         * on hardware, not just the DMA/RBU case it was originally written for. */
        g_feStats.rxNoProgressEvents++;
        frame_eth_recover_rx_ring(TRUE);
        s_lastRxBufStm = now;
        s_rxRbuSinceProgress = 0u;
        s_rxFifoOverflowSinceProgress = 0u;
    }
}
