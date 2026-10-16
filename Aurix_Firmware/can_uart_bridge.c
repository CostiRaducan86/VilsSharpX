/******************************************************************************
 * can_uart_bridge.c — Active CAN-UART forwarding bridge (Adapter_V2)
 *
 * See can_uart_bridge.h for the full architecture description.
 *
 * Summary:
 *   ECU side = ASCLIN5  RX P00.6  / TX P00.7   (X103 pins 28 / 29)
 *   LSM side = ASCLIN4  RX P00.12 / TX P00.9   (X103 pins 34 / 31)
 *
 *   ECU RX ISR (prio 11): drain RX FIFO -> write each byte to LSM TX FIFO
 *                         (forward request ECU->LSM) + capture for monitoring.
 *   LSM RX ISR (prio 12): drain RX FIFO -> write each byte to ECU TX FIFO
 *                         (forward response LSM->ECU) + capture for monitoring.
 *
 * Forwarding is byte-level (RX FIFO interrupt) to keep latency ~1 byte time,
 * which is mandatory for the request/response diagnostic protocol timing.
 *
 * Hardware: KIT_A2G_TC397_5V_TFT + SmartVisio Adapter_V2.
 *   3.3 V LVCMOS on P00.x matches the adapter transceiver VIO (3v3_LOCAL).
 ******************************************************************************/

#include "can_uart_bridge.h"

#include "Ifx_Types.h"
#include "IfxCpu.h"
#include "Asclin/Asc/IfxAsclin_Asc.h"
#include "Asclin/Std/IfxAsclin.h"
#include "IfxAsclin_PinMap.h"
#include "Stm/Std/IfxStm.h"
#include "Port/Std/IfxPort.h"
#include "IfxSrc.h"
#include <string.h>

#include "can_diag.h"        /* can_diag_bridge_uart_frame(), CAN_DIAG_RAW_MAX */
#include "can_hw.h"          /* DiagUartFrame */
#include "adapter_ctrl.h"    /* adapter_ctrl_set_can_bridge() */
#include "can_uart_fault_inject.h"
#include "defect_inject.h"   /* in-flight ELEDERP/ELEDERS injection (LSM->ECU) */
#include "nichia_defect_inject.h" /* in-flight PIXEL_ID/counter/flag injection (Nichia) */
#include "can_uart_master.h" /* Direct Control Mode master consumes the LSM bytes */

/* ======================== Configuration ======================== */

#define BRIDGE_DEVICE_NICHIA      0u
#define BRIDGE_DEVICE_OSRAM       1u

#define BRIDGE_ECU_RX_ISR_PRIO    11u   /* ASCLIN5 RX (ECU side) */
#define BRIDGE_LSM_RX_ISR_PRIO    12u   /* ASCLIN4 RX (LSM side) */

#define BRIDGE_TX_FIFO_DEPTH      16u   /* ASCLIN TX FIFO depth  */

/* Accumulator capacity per direction (one diagnostic frame fits easily). */
#define BRIDGE_ACC_MAX            CAN_DIAG_RAW_MAX   /* 72 */

#define BRIDGE_OSRAM_SYNC0        0x80u  /* Osram diagnostic frame SYNC0 */

/* ---- Osram diagnostic frame structure (KEWGBXXD1U) ----------------------
 * The merged monitor stream is framed by LENGTH, NOT by an idle gap.  An idle
 * gap that landed mid-transaction (e.g. the ~13 us request->response turnaround
 * or any inter-byte stretch) used to split a frame into a short head record
 * ("Malformed") and a discarded tail.  Length framing derives the exact frame
 * size from the HCTRL byte so a frame is only ever emitted once all of its
 * bytes are present, embedded 0x80 data bytes can never re-trigger a sync, and
 * fragments disappear. */
#define BRIDGE_OSRAM_SYNC1        0xA5u  /* SYNC1 (master address)            */
#define BRIDGE_FRAME_HEADER_LEN   4u     /* SYNC0 SYNC1 HCTRL HADR            */
#define BRIDGE_FRAME_CRC_LEN      2u     /* trailing CRC-16                   */
/* A header whose remaining bytes never arrive (e.g. a read request whose
 * response was lost) is discarded after this much bus-quiet so the parser
 * resynchronises on the next transaction instead of stalling on the partial.
 * Sits above the ~300 us inter-transaction idle yet flushes promptly.        */
#define BRIDGE_STALE_FLUSH_US     500u

/* Bus-quiet timeout after which the half-duplex relay lock is released back to
 * RELAY_IDLE.  This is ONLY a safety net for a desynchronised lock (e.g. a lost
 * echo after an RX-FIFO overflow): it sits far above the ~6 us transceiver echo
 * latency (so genuine echoes are always drained first) yet well below the inter-
 * transaction bus idle, so a stuck lock self-recovers before the next request.
 * Normal request->response and response->request turnarounds are resolved by
 * echo-count arbitration, NOT by this timer. */
#define BRIDGE_RELAY_IDLE_US      300u

/* ======================== Module state ======================== */

CanUartBridgeStats g_canUartBridgeStats;

/* Per-direction state.  One instance for ECU->LSM, one for LSM->ECU.
 * Holds only the byte-level forwarding telemetry; the monitoring capture is a
 * single shared stream (see s_mon* below) so request+response of one logical
 * transaction land in ONE record, preserving the original single-wire view on
 * the shared half-duplex wire. */
typedef struct bridge_dir_s
{
    Ifx_ASCLIN *srcAsclin;        /* this channel (RX source)            */
    Ifx_ASCLIN *peerAsclin;       /* opposite channel (TX destination)   */

    volatile uint32 echoDiscarded;        /* telemetry: TX echoes dropped  */

    /* Counters (mirrored into g_canUartBridgeStats by tick) */
    volatile uint32 rxBytes;
    volatile uint32 txForwarded;
    volatile uint32 txDropped;
} bridge_dir_t;

static IfxAsclin_Asc s_ascEcu;    /* ASCLIN5 */
static IfxAsclin_Asc s_ascLsm;    /* ASCLIN4 */

static bridge_dir_t  s_ecuDir;    /* RX from ECU  -> forward to LSM TX */
static bridge_dir_t  s_lsmDir;    /* RX from LSM  -> forward to ECU TX */

static volatile uint8 s_bridgeActive;  /* 1 = forwarding + capturing       */
static uint8          s_statsActive;   /* CPU2-owned session marker */
static uint8          s_deviceId = BRIDGE_DEVICE_OSRAM;
static uint32         s_ticksPerUs = 100u;  /* STM0 ticks per microsecond  */

/* ---- ECU request capture for defect injection --------------------------
 * The defect filter sees only LSM response bytes (data+CRC, no header).
 * We capture the last ECU request bytes here so HCTRL and HADR can be
 * passed to defect_inject_frame_begin() when the relay locks to RSP. */
static uint8 s_reqBuf[4];   /* [0x80][0xA5][HCTRL][HADR] from ECU */
static uint8 s_reqLen;      /* bytes captured (0..4) */

/* Active response-path injector for the current frame:
 *   0 = none, 1 = OSRAM (ELEDERP/ELEDERS), 2 = Nichia (PIXEL_ID/counter/flag).
 * Selected at RSP lock from the captured request sync byte. */
static uint8 s_injDev;

/* ---- Half-duplex relay arbitration --------------------------------------
 * The diagnostic bus is strictly turn-based: the ECU issues a request, the LSM
 * answers, repeat — never both at once.  AURIX splits that single shared bus
 * into two segments (ECU = ASCLIN5, LSM = ASCLIN4) and must relay exactly ONE
 * direction at a time.  The on-board CAN transceivers echo every transmitted
 * byte back onto the SAME channel's RX, so while AURIX retransmits onto a
 * channel, that channel's RX carries only echoes that must NOT be forwarded
 * back onto the bus they came from (doing so fed the ECU its own request bytes
 * as garbage and tripped its fail-safe).
 *
 * Self-arbitrating mechanism (no fixed turnaround timer):
 *   REQ: forward ECU RX -> LSM TX.  LSM RX bytes are request echoes and are
 *        dropped until exactly as many have arrived as were forwarded
 *        (echoCount == fwdCount).  The first LSM RX byte beyond that is a
 *        genuine response  -> switch to RSP.
 *   RSP: forward LSM RX -> ECU TX.  ECU RX bytes are response echoes, dropped
 *        the same way.  The first genuine ECU RX byte beyond the echoes is the
 *        next request -> switch back to REQ.
 * The switch only fires once ALL of the previous direction's echoes are
 * accounted for, so no echo is ever forwarded back onto its own bus. */
typedef enum
{
    BRIDGE_RELAY_IDLE = 0u,   /* bus quiet / no direction locked        */
    BRIDGE_RELAY_REQ  = 1u,   /* relaying ECU -> LSM (request)          */
    BRIDGE_RELAY_RSP  = 2u    /* relaying LSM -> ECU (response)         */
} bridge_relay_t;

static volatile bridge_relay_t s_relay = BRIDGE_RELAY_IDLE;
static volatile uint32 s_fwdCount;      /* bytes forwarded to active dest TX */
static volatile uint32 s_echoCount;     /* echoes drained on active dest RX  */
static volatile uint32 s_relayLastStm;  /* STM of last byte seen on the bus  */
static volatile uint32 s_relayReqCount; /* telemetry: ECU->LSM locks taken   */
static volatile uint32 s_relayRspCount; /* telemetry: LSM->ECU locks taken   */
static volatile uint32 s_relayResyncs;  /* telemetry: idle/overflow resyncs  */
static volatile uint32 s_lsmLastRequestEchoStm; /* STM of last request echo on LSM RX */

/* ---- Shared monitoring stream -------------------------------------------
 * Genuine (echo-filtered) bytes from BOTH directions are appended here in
 * processing order: the ECU request bytes first, then the LSM response bytes
 * ~6 us later.  This reproduces the original single-wire bus view, so one
 * read transaction = one record "80A5BE00 + <response> + CRC".
 * The buffer is a linear parse buffer drained by bridge_mon_tick(), which
 * extracts complete frames by LENGTH (see BRIDGE_FRAME_* above). */
static volatile uint8  s_monAcc[BRIDGE_ACC_MAX];
static volatile uint16 s_monLen;
static volatile uint32 s_monStartStm;    /* STM of byte[0] (current frame)    */
static volatile uint32 s_monLastStm;     /* STM of last byte appended         */
static volatile uint16 s_monResponseDelayUs; /* turnaround for current frame  */
static uint32          s_monPrevEndStm;  /* for inter-frame delay             */
static volatile uint32 s_monNoiseSkipped;/* sync-hunt / discarded bytes       */
static volatile uint32 s_monOverflow;    /* accumulator overflow              */
static volatile uint32 s_monFramesBridged;

/* ---- Cross-core handoff: CPU2 (bridge) -> CPU0 (can_diag + GETH) ----------
 * The bridge RX ISRs and the monitoring frame extraction run on CPU2 so they
 * never steal CPU0 cycles from the real-time LVDS pipeline.  The can_diag queue
 * is single-core only (shared count, producer drops tail on overflow), so it is
 * NOT safe to push into from CPU2.  Completed monitor frames are instead handed
 * to CPU0 through this lock-free single-producer (CPU2) / single-consumer (CPU0)
 * ring; CPU0 then feeds them into can_diag exactly as before. */
#define BRIDGE_OUT_RING_LEN   16u
static volatile DiagUartFrame s_outRing[BRIDGE_OUT_RING_LEN];
static volatile uint16        s_outHead;     /* advanced only by CPU2 producer */
static volatile uint16        s_outTail;     /* advanced only by CPU0 consumer */
static volatile uint32        s_outDropped;  /* ring full -> frame dropped      */
/* CPU0 drain budget per main-loop iteration.
 * Without a cap, a sustained producer stream can keep the consumer inside
 * can_uart_bridge_poll_out() for too long, starving frame_eth_poll_rx() and
 * making UI commands appear "stuck" until reset. */
#define BRIDGE_OUT_DRAIN_BUDGET  8u

/* Cross-core enable request: set on CPU0 by can_uart_bridge_set_active(TRUE);
 * the actual RX-FIFO drain + relay reset + activation is performed on CPU2 (the
 * core that owns the RX ISRs and the relay/FIFO state) to avoid any race. */
static volatile uint8         s_enableReq;

/* ======================== Low-level forwarding ======================== */

/* Append a genuine (already echo-filtered) byte to the SHARED monitoring parse
 * buffer.  Both directions feed the same linear buffer in processing order
 * (request bytes, then the response that follows); bridge_mon_tick() later
 * extracts complete frames by length.  No sync gating here — the extractor
 * hunts the sync pair so an out-of-phase start self-corrects. */
static void bridge_mon_capture(uint8 b, uint32 stm)
{
    if (s_monLen >= BRIDGE_ACC_MAX)
    {
        /* Parser fell behind on unframable data: drop the buffer rather than
         * dead-lock.  Bounded frames (<=38 B) make this practically unreachable. */
        s_monOverflow++;
        s_monLen = 0u;
    }

    if (s_monLen == 0u)
        s_monStartStm = stm;

    s_monAcc[s_monLen++] = b;
    s_monLastStm = stm;
}

/* Forward one genuine byte to a destination TX FIFO and account the echo that
 * the destination transceiver will produce on its own RX.  s_fwdCount is only
 * advanced when the byte is actually transmitted, so the echo bookkeeping stays
 * exact even if the TX FIFO is momentarily full (which cannot happen at cut-
 * through on a same-baud bus, but is handled defensively). */
static void bridge_forward(bridge_dir_t *srcDir, Ifx_ASCLIN *dst,
                           uint8 b, uint32 stm)
{
    if (dst->TXFIFOCON.B.FILL < BRIDGE_TX_FIFO_DEPTH)
    {
        IfxAsclin_writeTxData(dst, (uint32)b);
        srcDir->txForwarded++;
        s_fwdCount++;
    }
    else
    {
        srcDir->txDropped++;   /* not transmitted -> generates no echo */
    }

    bridge_mon_capture(b, stm);
}

/* Discard any bytes sitting in an RX FIFO (used on overflow recovery only). */
static void bridge_drain_rx(Ifx_ASCLIN *asc)
{
    while (asc->RXFIFOCON.B.FILL > 0u)
        (void)IfxAsclin_readRxData(asc);
}

/* In Direct Control Mode nothing drives the ECU segment, so its RX pin can
 * float and flood CPU2 with noise interrupts that delay the LSM channel. */
static void bridge_set_ecu_rx_enabled(boolean on)
{
    volatile Ifx_SRC_SRCR *src = IfxAsclin_getSrcPointerRx(s_ascEcu.asclin);

    if (on)
    {
        IfxSrc_enable(src);
    }
    else
    {
        IfxSrc_disable(src);
        bridge_drain_rx(s_ascEcu.asclin);
        s_ascEcu.asclin->FLAGSCLEAR.U = 0xFFFu;
    }
}

/* Re-entrancy guard so the two RX ISRs never run the pump body concurrently and
 * race on the shared relay state.  It is set/cleared with only a 2-instruction
 * interrupt disable (NOT around the whole pump). */
static volatile uint8 s_pumpBusy = 0u;

/* Half-duplex relay pump.  Called from BOTH RX ISRs.  The two channels never run
 * the pump body concurrently thanks to s_pumpBusy: if the higher-priority LSM
 * ISR preempts the ECU ISR mid-pump it sees the guard set and returns at once
 * (the running pump drains BOTH FIFOs, so nothing is lost).  Crucially the body
 * runs with interrupts ENABLED, so the prio-14 LVDS DMA ISR can preempt it and
 * the LVDS capture / Ethernet TX path is no longer starved by the bridge (this
 * starvation was the root cause of the pane-B glitch that appeared once the two
 * bridge ASCLINs were added).  All work is bounded (FIFOs are 16 deep), no
 * allocation, no blocking. */
static void bridge_relay_pump(void)
{
    Ifx_ASCLIN *ecu = s_ascEcu.asclin;   /* RX from ECU, TX to LSM */
    Ifx_ASCLIN *lsm = s_ascLsm.asclin;   /* RX from LSM, TX to ECU */
    boolean     is;
    boolean     more = TRUE;

    /* Re-entrancy test-and-set (minimal critical section, NOT the whole pump). */
    is = IfxCpu_disableInterrupts();
    if (s_pumpBusy != 0u)
    {
        IfxCpu_restoreInterrupts(is);
        return;
    }
    s_pumpBusy = 1u;
    IfxCpu_restoreInterrupts(is);

    /* Clear the fill-level flags BEFORE draining.  Clearing them afterwards
     * discards the flag that a byte arriving during the drain had just set, so
     * that byte waits for the next one to be serviced and the tail of a burst
     * can sit in the FIFO until it overflows. */
    if (s_bridgeActive != 0u)
        IfxAsclin_clearRxFifoFillLevelFlag(ecu);
    IfxAsclin_clearRxFifoFillLevelFlag(lsm);

    /* RX-FIFO overflow forces a resync: a lost echo would otherwise desync the
     * echo counters and stall the lock until the idle timeout.  Each channel is
     * recovered on its own; dropping the other one's FIFO as well would discard
     * a response that is still arriving on it. */
    {
        uint32  ecuFl  = (s_bridgeActive != 0u) ? ecu->FLAGS.U : 0u;
        uint32  lsmFl  = lsm->FLAGS.U;
        boolean resync = FALSE;

        if ((ecuFl & (1u << 8)) != 0u)   /* RFO on the ECU channel */
        {
            bridge_drain_rx(ecu);
            ecu->FLAGSCLEAR.U = ecuFl & 0xFFFu;
            resync = TRUE;
        }
        if ((lsmFl & (1u << 8)) != 0u)   /* RFO on the LSM channel */
        {
            bridge_drain_rx(lsm);
            lsm->FLAGSCLEAR.U = lsmFl & 0xFFFu;
            resync = TRUE;
            /* In Direct Control Mode the drained bytes belonged to the master's
             * transaction, so the loss must be visible in its telemetry. */
            can_uart_master_note_rx_overflow();
        }
        if (resync)
        {
            s_relay     = BRIDGE_RELAY_IDLE;
            s_fwdCount  = 0u;
            s_echoCount = 0u;
            s_relayResyncs++;
        }
    }

    while (more)
    {
        uint32 stm = IfxStm_getLower(&MODULE_STM0);
        more = FALSE;

        /* Safety: release a stale lock after a long bus-quiet so a desynced
         * (lost-echo) state self-recovers before the next request.  At this
         * point all in-flight echoes (~6 us latency) have long been drained,
         * so nothing genuine is lost. */
        if (s_bridgeActive != 0u && s_relay != BRIDGE_RELAY_IDLE &&
            (uint32)(stm - s_relayLastStm) > (BRIDGE_RELAY_IDLE_US * s_ticksPerUs))
        {
            s_relay     = BRIDGE_RELAY_IDLE;
            s_fwdCount  = 0u;
            s_echoCount = 0u;
            s_relayResyncs++;
        }

        /* ---------------- ECU channel (RX from ECU) ---------------- */
        if (s_bridgeActive != 0u && ecu->RXFIFOCON.B.FILL > 0u)
        {
            uint8 b = (uint8)IfxAsclin_readRxData(ecu);
            uint32 byteStm = IfxStm_getLower(&MODULE_STM0);
            s_ecuDir.rxBytes++;
            more = TRUE;

            if (s_bridgeActive != 0u)
            {
                s_relayLastStm = byteStm;

                if (s_relay == BRIDGE_RELAY_RSP && s_echoCount < s_fwdCount)
                {
                    /* Echo of a response byte we just forwarded onto ECU TX. */
                    s_echoCount++;
                    s_ecuDir.echoDiscarded++;
                }
                else
                {
                    /* Genuine ECU request byte -> lock/keep REQ, forward to LSM. */
                    if (s_relay != BRIDGE_RELAY_REQ)
                    {
                        s_relay     = BRIDGE_RELAY_REQ;
                        s_fwdCount  = 0u;
                        s_echoCount = 0u;
                        s_relayReqCount++;
                        s_reqLen    = 0u;   /* start of new request: reset capture */
                        s_lsmLastRequestEchoStm = 0u;
                        s_monResponseDelayUs = 0u;
                    }
                    /* Capture first 4 bytes of request for HCTRL/HADR extraction. */
                    if (s_reqLen < 4u)
                        s_reqBuf[s_reqLen++] = b;
                    if (can_uart_fault_should_drop(CAN_UART_FAULT_DIR_ECU_TO_LSM))
                        bridge_mon_capture(b, byteStm);
                    else
                        bridge_forward(&s_ecuDir, lsm, b, byteStm);
                }
            }
        }

        /* ---------------- LSM channel (RX from LSM) ---------------- */
        if (lsm->RXFIFOCON.B.FILL > 0u)
        {
            uint8 b = (uint8)IfxAsclin_readRxData(lsm);
            uint32 byteStm = IfxStm_getLower(&MODULE_STM0);
            s_lsmDir.rxBytes++;
            more = TRUE;

            /* Direct Control Mode: no ECU to forward to.  This pump stays the
             * single reader of the RX FIFO and hands the byte to the master,
             * which does its own echo filtering. */
            if (s_bridgeActive == 0u)
            {
                can_uart_master_feed_rx(b, byteStm);
            }

            if (s_bridgeActive != 0u)
            {
                s_relayLastStm = byteStm;

                if (s_relay == BRIDGE_RELAY_REQ && s_echoCount < s_fwdCount)
                {
                    /* Echo of a request byte we just forwarded onto LSM TX. */
                    s_echoCount++;
                    s_lsmDir.echoDiscarded++;
                    s_lsmLastRequestEchoStm = byteStm;
                }
                else
                {
                    /* Genuine LSM response byte -> lock/keep RSP, forward to ECU. */
                    if (s_relay != BRIDGE_RELAY_RSP)
                    {
                        /* Validate sync bytes before trusting the request.
                         * OSRAM requests start with 0x80 0xA5; Nichia/TLD816K
                         * requests start with 0x55. Bus noise at ECU power-on
                         * matches neither -> both filters stay idle. Only the
                         * selected device's frame_begin arms its filter; the
                         * other is reset to idle so no stale state leaks. */
                        uint8 hctrl = 0u;
                        uint8 hadr  = 0u;

                        s_relay     = BRIDGE_RELAY_RSP;
                        s_fwdCount  = 0u;
                        s_echoCount = 0u;
                        s_relayRspCount++;

                        if (s_lsmLastRequestEchoStm != 0u)
                        {
                            /* Measure from the last request echo observed on
                             * the LSM segment to the first genuine LSM byte.
                             * This excludes ECU-side forwarding latency. */
                            uint32 turnaroundUs = (byteStm - s_lsmLastRequestEchoStm) / s_ticksPerUs;
                            s_monResponseDelayUs = (turnaroundUs > 0xFFFFu)
                                                 ? 0xFFFFu : (uint16)turnaroundUs;
                        }
                        else
                        {
                            s_monResponseDelayUs = 0u;
                        }

                        if (s_reqLen >= 4u && s_reqBuf[0] == 0x55u)
                        {
                            s_injDev = 2u;
                            defect_inject_frame_begin(0u, 0u);   /* keep OSRAM idle */
                            nichia_defect_inject_frame_begin(s_reqBuf, s_reqLen);
                        }
                        else
                        {
                            if (s_reqLen >= 4u &&
                                s_reqBuf[0] == 0x80u &&
                                s_reqBuf[1] == 0xA5u)
                            {
                                hctrl = s_reqBuf[2];
                                hadr  = s_reqBuf[3];
                            }
                            s_injDev = 1u;
                            nichia_defect_inject_frame_begin(0, 0); /* keep Nichia idle */
                            defect_inject_frame_begin(hctrl, hadr);
                        }
                    }
                    /* In-flight defect injection on the response path:
                     * substitutes register bytes + overrides the trailing CRC
                     * for defined defects, byte-identical otherwise. The
                     * substituted byte is what both the ECU and the PC monitor
                     * (bridge_mon_capture) receive. */
                    b = (s_injDev == 2u)
                      ? nichia_defect_inject_filter_byte(b)
                      : defect_inject_filter_byte(b);
                                        if (can_uart_fault_should_drop(CAN_UART_FAULT_DIR_LSM_TO_ECU))
                                            bridge_mon_capture(b, byteStm);
                                        else
                                            bridge_forward(&s_lsmDir, ecu, b, byteStm);
                }
            }
        }
    }

    s_pumpBusy = 0u;
}

/* ======================== Interrupt handlers ======================== */

/* Both RX ISRs are installed in CPU2's vector table (vectab index 2) and the
 * RX service requests are routed to CPU2 (IfxSrc_Tos_cpu2 in the ASCLIN cfg).
 * Running the forwarding on the otherwise-idle CPU2 keeps CPU0 free for the
 * real-time LVDS capture/parse/Ethernet pipeline, which the high-rate Direct-
 * mode bridge traffic was previously starving (corrupting LVDS frames). */
IFX_INTERRUPT(BRIDGE_ECU_RX_ISR, 2, BRIDGE_ECU_RX_ISR_PRIO)
{
    bridge_relay_pump();
}

IFX_INTERRUPT(BRIDGE_LSM_RX_ISR, 2, BRIDGE_LSM_RX_ISR_PRIO)
{
    bridge_relay_pump();
}

/* ======================== ASCLIN configuration ======================== */

static void bridge_asclin_configure(IfxAsclin_Asc *asc,
                                    Ifx_ASCLIN    *module,
                                    const IfxAsclin_Asc_Pins *pins,
                                    uint8          deviceId,
                                    uint16         rxPriority,
                                    uint8         *rxBufMem,
                                    uint8         *txBufMem)
{
    IfxAsclin_Asc_Config cfg;
    IfxAsclin_Asc_initModuleConfig(&cfg, module);

    cfg.clockSource = IfxAsclin_ClockSource_ascFastClock;

    /* 2 Mbaud — identical to the diagnostic bus (see can_hw.c). */
    cfg.baudrate.baudrate     = 2000000.0f;
    cfg.baudrate.prescaler    = 0;
    cfg.baudrate.oversampling = IfxAsclin_OversamplingFactor_16;

    cfg.bitTiming.samplePointPosition = IfxAsclin_SamplePointPosition_8;
    cfg.bitTiming.medianFilter        = IfxAsclin_SamplesPerBit_three;

    cfg.frame.dataLength = IfxAsclin_DataLength_8;
    cfg.frame.frameMode  = IfxAsclin_FrameMode_asc;
    cfg.frame.shiftDir   = IfxAsclin_ShiftDirection_lsbFirst;

    if (deviceId == BRIDGE_DEVICE_NICHIA)
    {
        /* Nichia/TLD816K: 2 Mbaud, 8N1. */
        cfg.frame.stopBit    = IfxAsclin_StopBit_1;
        cfg.frame.parityBit  = FALSE;
        cfg.frame.parityType = IfxAsclin_ParityType_even;
    }
    else
    {
        /* Osram/KEWGBXXD1U: 2 Mbaud, 8O2. */
        cfg.frame.stopBit    = IfxAsclin_StopBit_2;
        cfg.frame.parityBit  = TRUE;
        cfg.frame.parityType = IfxAsclin_ParityType_odd;
    }

    cfg.fifo.inWidth              = IfxAsclin_TxFifoInletWidth_1;
    cfg.fifo.outWidth             = IfxAsclin_RxFifoOutletWidth_1;
    cfg.fifo.rxFifoInterruptLevel = IfxAsclin_RxFifoInterruptLevel_1;
    cfg.fifo.txFifoInterruptLevel = IfxAsclin_TxFifoInterruptLevel_0;
    cfg.fifo.buffMode             = IfxAsclin_ReceiveBufferMode_rxFifo;

    /* RX serviced by CPU2 at the given priority (forwarding runs off the CPU0
     * critical path); TX/ER unused (we write the TX FIFO directly from the
     * forwarding ISR, no driver TX ring). */
    cfg.interrupt.txPriority    = 0;
    cfg.interrupt.rxPriority    = rxPriority;
    cfg.interrupt.erPriority    = 0;
    cfg.interrupt.typeOfService = IfxSrc_Tos_cpu2;

    cfg.pins = pins;

    cfg.rxBuffer     = rxBufMem;
    cfg.rxBufferSize = 64;
    cfg.txBuffer     = txBufMem;
    cfg.txBufferSize = 64;

    IfxAsclin_Asc_initModule(asc, &cfg);
    IfxAsclin_setFilterDepth(asc->asclin, 2);

    /* Make sure the RX FIFO fill-level interrupt is enabled (CPU service). */
    IfxAsclin_enableRxFifoFillLevelFlag(asc->asclin, TRUE);
}

/* ======================== Public API ======================== */

void can_uart_bridge_init(uint8 deviceId)
{
    /* ECU side = ASCLIN5: RX P00.6, TX P00.7 */
    static const IfxAsclin_Asc_Pins ecuPins = {
        .cts       = NULL_PTR,
        .ctsMode   = IfxPort_InputMode_noPullDevice,
        .rx        = &IfxAsclin5_RXA_P00_6_IN,
        .rxMode    = IfxPort_InputMode_pullUp,
        .rts       = NULL_PTR,
        .rtsMode   = IfxPort_OutputMode_pushPull,
        .tx        = &IfxAsclin5_TX_P00_7_OUT,
        .txMode    = IfxPort_OutputMode_pushPull,
        .pinDriver = IfxPort_PadDriver_cmosAutomotiveSpeed1
    };

    /* LSM side = ASCLIN4: RX P00.12, TX P00.9 */
    static const IfxAsclin_Asc_Pins lsmPins = {
        .cts       = NULL_PTR,
        .ctsMode   = IfxPort_InputMode_noPullDevice,
        .rx        = &IfxAsclin4_RXA_P00_12_IN,
        .rxMode    = IfxPort_InputMode_pullUp,
        .rts       = NULL_PTR,
        .rtsMode   = IfxPort_OutputMode_pushPull,
        .tx        = &IfxAsclin4_TX_P00_9_OUT,
        .txMode    = IfxPort_OutputMode_pushPull,
        .pinDriver = IfxPort_PadDriver_cmosAutomotiveSpeed1
    };

    static uint8 ecuRxBuf[64 + sizeof(Ifx_Fifo) + 8];
    static uint8 ecuTxBuf[64 + sizeof(Ifx_Fifo) + 8];
    static uint8 lsmRxBuf[64 + sizeof(Ifx_Fifo) + 8];
    static uint8 lsmTxBuf[64 + sizeof(Ifx_Fifo) + 8];

    s_deviceId = (deviceId == BRIDGE_DEVICE_NICHIA) ? BRIDGE_DEVICE_NICHIA
                                                    : BRIDGE_DEVICE_OSRAM;

    can_uart_fault_clear();

    /* Keep forwarding OFF while we (re)configure the UART hardware. */
    s_bridgeActive = 0u;

    /* Reset directional state. */
    memset((void *)&s_ecuDir, 0, sizeof(s_ecuDir));
    memset((void *)&s_lsmDir, 0, sizeof(s_lsmDir));

    /* Configure both channels.  Configuring the ASCLIN TX pin drives the line
     * to the UART idle level (HIGH), which satisfies the Adapter_V2 rule that
     * CAN_TX_ECU / CAN_TX_LSM must idle HIGH before CAN_SEL is activated. */
    bridge_asclin_configure(&s_ascEcu, &MODULE_ASCLIN5, &ecuPins,
                            s_deviceId, BRIDGE_ECU_RX_ISR_PRIO,
                            ecuRxBuf, ecuTxBuf);
    bridge_asclin_configure(&s_ascLsm, &MODULE_ASCLIN4, &lsmPins,
                            s_deviceId, BRIDGE_LSM_RX_ISR_PRIO,
                            lsmRxBuf, lsmTxBuf);

    /* Wire directional forwarding targets. */
    s_ecuDir.srcAsclin  = s_ascEcu.asclin;   /* RX from ECU  */
    s_ecuDir.peerAsclin = s_ascLsm.asclin;   /* TX to LSM    */
    s_lsmDir.srcAsclin  = s_ascLsm.asclin;   /* RX from LSM  */
    s_lsmDir.peerAsclin = s_ascEcu.asclin;   /* TX to ECU    */

    /* Start the half-duplex relay unlocked. */
    s_relay        = BRIDGE_RELAY_IDLE;
    s_fwdCount     = 0u;
    s_echoCount    = 0u;
    s_relayLastStm = 0u;
    s_lsmLastRequestEchoStm = 0u;
    s_monResponseDelayUs = 0u;

    /* STM0 ticks per microsecond for idle-gap timing. */
    {
        uint32 freq = (uint32)IfxStm_getFrequency(&MODULE_STM0);
        s_ticksPerUs = (freq >= 1000000u) ? (freq / 1000000u) : 1u;
    }

    g_canUartBridgeStats.initOk        = 1u;
    g_canUartBridgeStats.active        = 0u;
    g_canUartBridgeStats.deviceId      = s_deviceId;
    g_canUartBridgeStats.stmTicksPerUs = s_ticksPerUs;
}

void can_uart_bridge_set_active(boolean enable)
{
    /* Forwarding state only.  The CAN_SEL routing pin is owned
     * by adapter_ctrl (adapter_ctrl_set_can_uart), driven from the UI adapter
     * command and at boot.  The caller MUST have already routed the bus through
     * AURIX (ECU↔SmartVisio↔LSM CAN-UART mode, CAN_SEL HIGH) before enabling forwarding.
     *
     * This function is called from CPU0 (the GETH command handler), but the RX
     * ISRs and relay/FIFO state live on CPU2.  To avoid a cross-core race we do
     * NOT touch the FIFOs/relay here: enabling is requested via s_enableReq and
     * the clean-start (FIFO drain + relay reset + activate) is performed on CPU2
     * in can_uart_bridge_tick().  Disabling is a single volatile write the CPU2
     * RX ISR observes per byte, so it is safe to do directly from CPU0. */
    if (enable)
    {
        s_enableReq = 1u;
    }
    else
    {
        s_bridgeActive = 0u;
        s_enableReq    = 0u;
        g_canUartBridgeStats.active = 0u;
    }
}

boolean can_uart_bridge_is_active(void)
{
    return (s_bridgeActive != 0u) ? TRUE : FALSE;
}

/* Total Osram frame length (header + data + CRC) decoded from the HCTRL byte,
 * matching the historical full-length formula.  The HCTRL nRegs
 * field describes both write-data and read-response register counts, so this is
 * the full merged length for either operation. */
static uint8 bridge_mon_full_len(uint8 hctrl)
{
    uint8 nRegs = (uint8)(((hctrl >> 1u) & 0x0Fu) + 1u);
    return (uint8)(BRIDGE_FRAME_HEADER_LEN + nRegs * 2u + BRIDGE_FRAME_CRC_LEN);
}

/* Remove n bytes from the front of the parse buffer (caller holds the int-lock). */
static void bridge_mon_compact(uint16 n)
{
    if (n >= s_monLen)
    {
        s_monLen = 0u;
        return;
    }
    memmove((void *)s_monAcc, (const void *)&s_monAcc[n], (size_t)(s_monLen - n));
    s_monLen = (uint16)(s_monLen - n);
    /* Remaining bytes belong to the next transaction; approximate its start. */
    s_monStartStm = s_monLastStm;
}

/* One length-framing step over the shared parse buffer (caller holds the
 * int-lock).  Returns TRUE when it made progress (a frame was produced into
 * *out, or noise/a bare request was discarded) so the caller loops again;
 * FALSE when the buffer holds only an incomplete frame and must wait for more
 * bytes. */
static boolean bridge_mon_parse_step(DiagUartFrame *out, boolean *haveFrame)
{
    uint8  hctrl;
    uint8  fullLen;
    uint8  copyLen;
    uint16 i;

    *haveFrame = FALSE;

    if (s_monLen < 2u)
        return FALSE;

    /* Hunt the SYNC0 (0x80) at the front of the buffer. */
    if (s_monAcc[0] != BRIDGE_OSRAM_SYNC0)
    {
        for (i = 1u; i < s_monLen; i++)
        {
            if (s_monAcc[i] == BRIDGE_OSRAM_SYNC0)
                break;
        }
        s_monNoiseSkipped += i;
        bridge_mon_compact(i);
        return (s_monLen >= 2u) ? TRUE : FALSE;
    }

    /* Verify SYNC1 (0xA5); if absent, this was a stray 0x80 — skip it. */
    if (s_monAcc[1] != BRIDGE_OSRAM_SYNC1)
    {
        s_monNoiseSkipped++;
        bridge_mon_compact(1u);
        return TRUE;
    }

    if (s_monLen < BRIDGE_FRAME_HEADER_LEN)
        return FALSE;          /* wait for HCTRL + HADR */

    hctrl = (uint8)s_monAcc[2];

    /* Read frame: distinguish a bare 4-byte read request (no response captured)
     * from a full read response by peeking at bytes [4..5].  If they are the
     * next sync pair the response is missing, so drop the request header instead
     * of letting it swallow the following frame. */
    if ((hctrl & 0x80u) != 0u)
    {
        if (s_monLen < 6u)
            return FALSE;      /* need [4..5] before deciding */

        if (s_monAcc[4] == BRIDGE_OSRAM_SYNC0 &&
            s_monAcc[5] == BRIDGE_OSRAM_SYNC1)
        {
            s_monNoiseSkipped += BRIDGE_FRAME_HEADER_LEN;
            bridge_mon_compact(BRIDGE_FRAME_HEADER_LEN);
            return TRUE;
        }
    }

    fullLen = bridge_mon_full_len(hctrl);

    if (s_monLen < fullLen)
        return FALSE;          /* wait for the rest of the frame */

    /* Complete frame — emit exactly fullLen bytes. */
    copyLen = (fullLen <= (uint8)sizeof(out->data)) ? fullLen : (uint8)sizeof(out->data);
    memcpy(out->data, (const void *)s_monAcc, copyLen);
    out->len             = copyLen;
    out->timestampUs     = s_monStartStm / s_ticksPerUs;
    out->responseDelayUs = ((hctrl & 0x80u) != 0u) ? s_monResponseDelayUs : 0u;

    if (s_monPrevEndStm != 0u)
    {
        uint32 gap = s_monStartStm - s_monPrevEndStm;
        uint32 us  = gap / s_ticksPerUs;
        out->interFrameDelayUs = (us > 0xFFFFu) ? 0xFFFFu : (uint16)us;
    }
    else
    {
        out->interFrameDelayUs = 0u;
    }
    s_monPrevEndStm = s_monLastStm;

    bridge_mon_compact(fullLen);
    *haveFrame = TRUE;
    return TRUE;
}

/* ---- Nichia TLD816K parse step (length-framing via DLC_FUN) ---------------
 * Mirrors the logic of diag_uart_try_receive_nichia() from can_hw.c but
 * operates on the bridge's shared linear parse buffer (s_monAcc).
 *
 * Nichia frame on the half-duplex bus:
 *   [0] SYNC = 0x55
 *   [1] MasterRequest byte (device addr / direction marker)
 *   [2] DLC_FUN: bits[2:0]=FUN, bits[5:3]=DLC, bits[7:6]=reserved(0)
 *   [3..] address (1 B for REG, 2 B for EEP), data, CRC-8
 *   For writes: 2 ACK bytes may follow the CRC.
 *
 * Read requests (ECU→LSM) are header+address only — they are SKIPPED so the
 * response that follows becomes the emitted record.
 */
#define BRIDGE_NICHIA_SYNC         0x55u
#define BRIDGE_NICHIA_HDR_LEN      3u
#define BRIDGE_NICHIA_REG_ADDR_LEN 1u
#define BRIDGE_NICHIA_EEP_ADDR_LEN 2u
#define BRIDGE_NICHIA_CRC_LEN      1u
#define BRIDGE_NICHIA_ACK_LEN      2u
#define BRIDGE_NICHIA_FUN_WRITE_REG  4u
#define BRIDGE_NICHIA_FUN_READ_REG   5u
#define BRIDGE_NICHIA_FUN_WRITE_EEP  6u
#define BRIDGE_NICHIA_FUN_READ_EEP   7u
#define BRIDGE_NICHIA_FUN_MASK       0x07u
#define BRIDGE_NICHIA_DLC_MASK       0x38u
#define BRIDGE_NICHIA_DLC_FUN_RES_MASK 0xC0u

static uint8 bridge_nichia_data_length(uint8 dlc)
{
    static const uint8 s_len[8] = { 1u, 2u, 4u, 8u, 16u, 24u, 32u, 64u };
    return s_len[dlc & 0x07u];
}

static uint8 bridge_nichia_addr_length(uint8 fun)
{
    return ((fun == BRIDGE_NICHIA_FUN_WRITE_EEP) || (fun == BRIDGE_NICHIA_FUN_READ_EEP))
        ? BRIDGE_NICHIA_EEP_ADDR_LEN : BRIDGE_NICHIA_REG_ADDR_LEN;
}

static boolean bridge_nichia_fun_valid(uint8 fun)
{
    return ((fun >= BRIDGE_NICHIA_FUN_WRITE_REG) && (fun <= BRIDGE_NICHIA_FUN_READ_EEP))
        ? TRUE : FALSE;
}

/* Check if a valid Nichia header starts at offset 'off' in s_monAcc. */
static boolean bridge_nichia_header_valid_at(uint16 off)
{
    uint8 dlcFun;
    uint8 fun;

    if ((uint16)(off + BRIDGE_NICHIA_HDR_LEN) > s_monLen)
        return FALSE;

    if (s_monAcc[off] != BRIDGE_NICHIA_SYNC)
        return FALSE;

    dlcFun = (uint8)s_monAcc[off + 2u];
    fun    = (uint8)(dlcFun & BRIDGE_NICHIA_FUN_MASK);

    if ((dlcFun & BRIDGE_NICHIA_DLC_FUN_RES_MASK) != 0u)
        return FALSE;

    return bridge_nichia_fun_valid(fun);
}

static boolean bridge_mon_parse_step_nichia(DiagUartFrame *out, boolean *haveFrame)
{
    uint8  dlcFun;
    uint8  fun;
    uint8  dlc;
    uint8  addrLen;
    uint8  dataLen;
    uint8  hasCrc;
    uint8  reqLen;
    uint8  dataFrameLen;
    uint8  fullLen;
    uint8  copyLen;
    uint16 i;

    *haveFrame = FALSE;

    if (s_monLen < BRIDGE_NICHIA_HDR_LEN)
        return FALSE;

    /* Hunt the 0x55 sync at the front of the buffer. */
    if (s_monAcc[0] != BRIDGE_NICHIA_SYNC)
    {
        for (i = 1u; i < s_monLen; i++)
        {
            if (s_monAcc[i] == BRIDGE_NICHIA_SYNC)
                break;
        }
        s_monNoiseSkipped += i;
        bridge_mon_compact(i);
        return (s_monLen >= BRIDGE_NICHIA_HDR_LEN) ? TRUE : FALSE;
    }

    /* Validate header (DLC_FUN reserved bits + function range). */
    dlcFun = (uint8)s_monAcc[2];
    fun    = (uint8)(dlcFun & BRIDGE_NICHIA_FUN_MASK);

    if ((dlcFun & BRIDGE_NICHIA_DLC_FUN_RES_MASK) != 0u || !bridge_nichia_fun_valid(fun))
    {
        /* Invalid header at position 0 — skip this 0x55 and re-hunt. */
        s_monNoiseSkipped++;
        bridge_mon_compact(1u);
        return TRUE;
    }

    dlc     = (uint8)((dlcFun & BRIDGE_NICHIA_DLC_MASK) >> 3u);
    dataLen = bridge_nichia_data_length(dlc);
    addrLen = bridge_nichia_addr_length(fun);
    hasCrc  = (fun == BRIDGE_NICHIA_FUN_READ_EEP) ? 0u : 1u;
    reqLen  = (uint8)(BRIDGE_NICHIA_HDR_LEN + addrLen);
    dataFrameLen = (uint8)(reqLen + dataLen + (hasCrc ? BRIDGE_NICHIA_CRC_LEN : 0u));

    /* --- Read request (ECU→LSM): header+address only, no data/CRC.
     * Skip it so the following response frame becomes the emitted record. */
    if (fun == BRIDGE_NICHIA_FUN_READ_REG || fun == BRIDGE_NICHIA_FUN_READ_EEP)
    {
        if (s_monLen < reqLen)
            return FALSE;   /* wait for the rest of the short request */

        /* If the next bytes form a valid header → this was just a short request. */
        if (bridge_nichia_header_valid_at(reqLen))
        {
            /* Skip the request and let the response be emitted next loop. */
            s_monNoiseSkipped += reqLen;
            bridge_mon_compact(reqLen);
            return TRUE;
        }

        /* No next header yet: maybe it IS a response (data follows), or we
         * need more bytes to decide.  Fall through to emit the data frame. */
        if (s_monLen < dataFrameLen)
            return FALSE;   /* wait for full response frame */
    }
    else
    {
        /* --- Write request/response: needs full dataFrameLen bytes. */
        if (s_monLen < dataFrameLen)
            return FALSE;

        /* Writes may have 2 ACK bytes after CRC.  Include them if present
         * and the byte after them looks like a new sync. */
        fullLen = dataFrameLen;
        if (s_monLen >= (uint16)(dataFrameLen + BRIDGE_NICHIA_ACK_LEN) &&
            !bridge_nichia_header_valid_at(dataFrameLen))
        {
            fullLen = (uint8)(dataFrameLen + BRIDGE_NICHIA_ACK_LEN);
        }

        copyLen = (fullLen <= (uint8)sizeof(out->data)) ? fullLen : (uint8)sizeof(out->data);
        memcpy(out->data, (const void *)s_monAcc, copyLen);
        out->len             = copyLen;
        out->timestampUs     = s_monStartStm / s_ticksPerUs;
        out->responseDelayUs = 0u;

        if (s_monPrevEndStm != 0u)
        {
            uint32 gap = s_monStartStm - s_monPrevEndStm;
            uint32 us  = gap / s_ticksPerUs;
            out->interFrameDelayUs = (us > 0xFFFFu) ? 0xFFFFu : (uint16)us;
        }
        else
        {
            out->interFrameDelayUs = 0u;
        }
        s_monPrevEndStm = s_monLastStm;

        bridge_mon_compact(fullLen);
        *haveFrame = TRUE;
        return TRUE;
    }

    /* Emit the read response (dataFrameLen bytes). */
    copyLen = (dataFrameLen <= (uint8)sizeof(out->data)) ? dataFrameLen : (uint8)sizeof(out->data);
    memcpy(out->data, (const void *)s_monAcc, copyLen);
    out->len             = copyLen;
    out->timestampUs     = s_monStartStm / s_ticksPerUs;
    out->responseDelayUs = s_monResponseDelayUs;

    if (s_monPrevEndStm != 0u)
    {
        uint32 gap = s_monStartStm - s_monPrevEndStm;
        uint32 us  = gap / s_ticksPerUs;
        out->interFrameDelayUs = (us > 0xFFFFu) ? 0xFFFFu : (uint16)us;
    }
    else
    {
        out->interFrameDelayUs = 0u;
    }
    s_monPrevEndStm = s_monLastStm;

    bridge_mon_compact(dataFrameLen);
    *haveFrame = TRUE;
    return TRUE;
}

/* ---- Cross-core SPSC handoff (CPU2 producer) ---------------------------- */

/* Publish one completed monitor frame to the CPU0 consumer ring.  Single
 * producer (CPU2 bridge_mon_tick).  The __dsync() guarantees the frame payload
 * is globally visible before the head index advances so the CPU0 consumer never
 * reads a half-written slot. */
static boolean bridge_out_push(const DiagUartFrame *f)
{
    uint16 head = s_outHead;
    uint16 next = (uint16)((head + 1u) % BRIDGE_OUT_RING_LEN);

    if (next == s_outTail)
        return FALSE;                       /* ring full — caller counts drop */

    memcpy((void *)&s_outRing[head], f, sizeof(*f));
    __dsync();
    s_outHead = next;
    return TRUE;
}

/* Drain the shared parse buffer, pushing every complete length-framed
 * transaction to the cross-core handoff ring (drained on CPU0 into can_diag).
 * A header whose remainder never arrives is discarded after a long bus-quiet so
 * the parser resynchronises.  Runs on CPU2. */
static void bridge_mon_tick(void)
{
    boolean progress = TRUE;

    while (progress)
    {
        DiagUartFrame frame;
        boolean       haveFrame = FALSE;
        boolean       intState  = IfxCpu_disableInterrupts();

        if (s_deviceId == BRIDGE_DEVICE_NICHIA)
            progress = bridge_mon_parse_step_nichia(&frame, &haveFrame);
        else
            progress = bridge_mon_parse_step(&frame, &haveFrame);

        IfxCpu_restoreInterrupts(intState);

        if (haveFrame)
        {
            if (bridge_out_push(&frame))
                s_monFramesBridged++;
            else
                s_outDropped++;
        }
    }

    /* Stale-partial flush: discard an incomplete frame whose missing bytes
     * never showed up, so a lost response cannot block the buffer forever. */
    {
        boolean intState = IfxCpu_disableInterrupts();
        if (s_monLen > 0u)
        {
            uint32 now    = IfxStm_getLower(&MODULE_STM0);
            uint32 idleUs = (now - s_monLastStm) / s_ticksPerUs;
            if (idleUs >= BRIDGE_STALE_FLUSH_US)
            {
                s_monNoiseSkipped += s_monLen;
                s_monLen = 0u;
            }
        }
        IfxCpu_restoreInterrupts(intState);
    }
}

void can_uart_bridge_tick(void)
{
    if (g_canUartBridgeStats.initOk == 0u)
        return;

    /* Clear the previous session once after forwarding has been disabled.
     * This runs on CPU2, alongside the bridge ISRs that own the counters. */
    if ((s_bridgeActive == 0u) && (s_statsActive != 0u))
    {
        can_uart_bridge_reset_state();
        s_statsActive = 0u;
    }

    /* Safety net for a lost RX interrupt: the pump is re-entrancy guarded and
     * returns at once when both FIFOs are empty, and the CPU2 loop is far
     * faster than the 96 us a full 16-byte FIFO takes at 2 Mbaud. */
    bridge_relay_pump();

    /* Keep the ECU channel serviced only while it is actually bridged. */
    {
        static uint8 s_ecuRxOn = 0xFFu;
        uint8        want      = (s_bridgeActive != 0u) ? 1u : 0u;

        if (s_ecuRxOn != want)
        {
            bridge_set_ecu_rx_enabled(want != 0u);
            s_ecuRxOn = want;
        }
    }

    can_uart_fault_tick();

    /* Service a pending enable request from CPU0 (cross-core handshake).  This
     * runs on CPU2 so the RX-FIFO drain + relay reset happen on the same core as
     * the RX ISRs, with no cross-core race.  Start from a clean relay/accumulator
     * state so a stale lock or echo count cannot drop the first real byte, and
     * flush both RX FIFOs so bytes that arrived mid-transaction during the live
     * bus switch do not corrupt the first real transaction. */
    if (s_enableReq != 0u)
    {
        boolean intState;

        can_uart_bridge_reset_state();
        intState = IfxCpu_disableInterrupts();
        s_relay        = BRIDGE_RELAY_IDLE;
        s_fwdCount     = 0u;
        s_echoCount    = 0u;
        s_relayLastStm = IfxStm_getLower(&MODULE_STM0);
        s_monLen       = 0u;
        bridge_drain_rx(s_ascEcu.asclin);
        bridge_drain_rx(s_ascLsm.asclin);
        s_bridgeActive = 1u;
        s_enableReq    = 0u;
        IfxCpu_restoreInterrupts(intState);

        g_canUartBridgeStats.active = 1u;
        s_statsActive = 1u;
    }

    bridge_mon_tick();

    /* Mirror counters for the debugger / telemetry at a BOUNDED rate.  This
     * struct lives in shared SRAM; writing all ~20 fields on every CPU2 loop
     * iteration floods the SRI crossbar and starves the LVDS HDMA, overflowing
     * the ASCLIN1 RX FIFO -> CRC errors and visible flicker on both the PC LVDS
     * monitor and the TFT.  A few refreshes per second is plenty for telemetry,
     * and the live `active` flag is already updated immediately above. */
    {
        static uint32 s_lastStatsStm = 0u;
        uint32        nowStm = IfxStm_getLower(&MODULE_STM0);

        if ((uint32)(nowStm - s_lastStatsStm) >= (5000u * s_ticksPerUs))
        {
            s_lastStatsStm = nowStm;

            g_canUartBridgeStats.ecuRxBytes       = s_ecuDir.rxBytes;
            g_canUartBridgeStats.ecuTxForwarded   = s_ecuDir.txForwarded;
            g_canUartBridgeStats.ecuTxDropped     = s_ecuDir.txDropped;
            g_canUartBridgeStats.ecuFramesBridged = s_monFramesBridged;
            g_canUartBridgeStats.ecuOverflow      = s_monOverflow;
            g_canUartBridgeStats.ecuEchoDiscarded = s_ecuDir.echoDiscarded;
            g_canUartBridgeStats.ecuNoiseSkipped  = s_monNoiseSkipped;

            g_canUartBridgeStats.lsmRxBytes       = s_lsmDir.rxBytes;
            g_canUartBridgeStats.lsmTxForwarded   = s_lsmDir.txForwarded;
            g_canUartBridgeStats.lsmTxDropped     = s_lsmDir.txDropped;
            g_canUartBridgeStats.lsmFramesBridged = s_monFramesBridged;
            g_canUartBridgeStats.lsmOverflow      = s_monOverflow;
            g_canUartBridgeStats.lsmEchoDiscarded = s_lsmDir.echoDiscarded;
            g_canUartBridgeStats.lsmNoiseSkipped  = s_monNoiseSkipped;

            g_canUartBridgeStats.relayState       = (uint32)s_relay;
            g_canUartBridgeStats.relayReqCount    = s_relayReqCount;
            g_canUartBridgeStats.relayRspCount    = s_relayRspCount;
            g_canUartBridgeStats.relayResyncs     = s_relayResyncs;
        }
    }
}

/* Consume monitor frames produced by the CPU2 bridge and feed them into the
 * single-core can_diag queue.  MUST run on CPU0 (the same core as the can_diag
 * pop + the GETH diagnostic TX), so the can_diag ring stays single-producer /
 * single-consumer on one core exactly as before the CPU2 migration. */
void can_uart_bridge_poll_out(void)
{
    uint8 drained = 0u;

    while (s_outTail != s_outHead && drained < BRIDGE_OUT_DRAIN_BUDGET)
    {
        DiagUartFrame f;
        uint16        tail = s_outTail;

        memcpy(&f, (const void *)&s_outRing[tail], sizeof(f));
        __dsync();
        s_outTail = (uint16)((tail + 1u) % BRIDGE_OUT_RING_LEN);

        (void)can_diag_bridge_uart_frame(&f, (uint8)s_deviceId);
        drained++;
    }
}

void can_uart_bridge_reset_state(void)
{
    boolean intState = IfxCpu_disableInterrupts();

    s_ecuDir.rxBytes = 0u;
    s_ecuDir.txForwarded = 0u;
    s_ecuDir.txDropped = 0u;
    s_ecuDir.echoDiscarded = 0u;

    s_lsmDir.rxBytes = 0u;
    s_lsmDir.txForwarded = 0u;
    s_lsmDir.txDropped = 0u;
    s_lsmDir.echoDiscarded = 0u;

    /* Shared monitoring stream. */
    s_monLen          = 0u;
    s_monStartStm     = 0u;
    s_monLastStm      = 0u;
    s_monPrevEndStm   = 0u;
    s_lsmLastRequestEchoStm = 0u;
    s_monResponseDelayUs = 0u;
    s_monNoiseSkipped = 0u;
    s_monOverflow     = 0u;
    s_monFramesBridged = 0u;

    s_relay        = BRIDGE_RELAY_IDLE;
    s_fwdCount     = 0u;
    s_echoCount    = 0u;
    s_relayReqCount = 0u;
    s_relayRspCount = 0u;
    s_relayResyncs  = 0u;

    g_canUartBridgeStats.ecuRxBytes       = 0u;
    g_canUartBridgeStats.ecuTxForwarded   = 0u;
    g_canUartBridgeStats.ecuTxDropped     = 0u;
    g_canUartBridgeStats.ecuFramesBridged = 0u;
    g_canUartBridgeStats.ecuOverflow      = 0u;
    g_canUartBridgeStats.ecuEchoDiscarded = 0u;
    g_canUartBridgeStats.ecuNoiseSkipped  = 0u;

    g_canUartBridgeStats.lsmRxBytes       = 0u;
    g_canUartBridgeStats.lsmTxForwarded   = 0u;
    g_canUartBridgeStats.lsmTxDropped     = 0u;
    g_canUartBridgeStats.lsmFramesBridged = 0u;
    g_canUartBridgeStats.lsmOverflow      = 0u;
    g_canUartBridgeStats.lsmEchoDiscarded = 0u;
    g_canUartBridgeStats.lsmNoiseSkipped  = 0u;

    g_canUartBridgeStats.relayState       = (uint32)BRIDGE_RELAY_IDLE;
    g_canUartBridgeStats.relayReqCount    = 0u;
    g_canUartBridgeStats.relayRspCount    = 0u;
    g_canUartBridgeStats.relayResyncs     = 0u;

    IfxCpu_restoreInterrupts(intState);
}
