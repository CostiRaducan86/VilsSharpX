#ifndef CAN_UART_BRIDGE_H
#define CAN_UART_BRIDGE_H

/******************************************************************************
 * can_uart_bridge.h — Active CAN-UART forwarding bridge (Adapter_V2)
 *
 * On Adapter_V2 the diagnostic "CAN" bus between ECU and LSM is a UART signal
 * (2 Mbaud) carried as a differential PHY through CAN transceivers.  In the
 * fail-safe default (CAN_SEL = LOW) the adapter bridges ECU_CAN_H/L directly
 * to LSM_CAN_H/L, so AURIX sees nothing and there is NO monitoring.
 *
 * When CAN_SEL = HIGH the adapter routes the bus through two on-board CAN
 * transceivers wired to AURIX:
 *   U3  (ECU side)  -> CAN_RX_ECU / CAN_TX_ECU
 *   U10 (LSM side)  -> CAN_RX_LSM / CAN_TX_LSM
 *
 * AURIX then becomes an inline, transparent, bidirectional UART forwarder and
 * must keep the ECU<->LSM diagnostic link alive while tapping the traffic for
 * the PC UI.
 *
 * Pin assignment (all on X103, verified against Infineon Application Kit
 * TC3X7 manual section 6 and the iLLD TC39xB LFBGA292 pin map):
 *   ECU side = ASCLIN5  RX P00.6 (X103 pin 28)   TX P00.7 (X103 pin 29)
 *   LSM side = ASCLIN4  RX P00.12 (X103 pin 34)  TX P00.9 (X103 pin 31)
 *   CAN_SEL  = P14.6 GPIO (X103 pin 9), driven via adapter_ctrl.
 *
 * Latency note: a transparent forwarder MUST have ~byte-level latency or the
 * request/response diagnostic protocol breaks.  Therefore forwarding is done
 * in the RX-FIFO interrupt (read source byte -> write peer TX byte), NOT via
 * large ping-pong DMA buffers (which would add milliseconds of latency).
 * DMA stays reserved for the high-bandwidth LVDS path (asclin1_dma.c).
 *
 * Monitoring: with two separate directional channels, ECU->LSM bytes are
 * requests and LSM->ECU bytes are responses, naturally delimited by per-
 * direction idle gaps.  Each completed directional frame is pushed through
 * can_diag_bridge_uart_frame() -> existing Ethernet 0x4344 path -> PC UI.
 *
 * ISR priorities: ECU RX = 11, LSM RX = 12 (unique vs GETH-TX 10, diag 13,
 * LVDS 14, camera 20).
 ******************************************************************************/

#include "Ifx_Types.h"

typedef struct
{
    /* ECU side (ASCLIN5): traffic FROM ECU, forwarded to LSM TX */
    volatile uint32 ecuRxBytes;
    volatile uint32 ecuTxForwarded;   /* bytes pushed into LSM TX FIFO        */
    volatile uint32 ecuTxDropped;     /* dropped: LSM TX FIFO full            */
    volatile uint32 ecuFramesBridged; /* directional frames sent to UI        */
    volatile uint32 ecuOverflow;      /* accumulator overflow (frame too long)*/
    volatile uint32 ecuEchoDiscarded; /* TX echoes suppressed on ECU RX       */
    volatile uint32 ecuNoiseSkipped;  /* non-sync / short bursts not shown    */

    /* LSM side (ASCLIN4): traffic FROM LSM, forwarded to ECU TX */
    volatile uint32 lsmRxBytes;
    volatile uint32 lsmTxForwarded;   /* bytes pushed into ECU TX FIFO        */
    volatile uint32 lsmTxDropped;     /* dropped: ECU TX FIFO full            */
    volatile uint32 lsmFramesBridged;
    volatile uint32 lsmOverflow;
    volatile uint32 lsmEchoDiscarded; /* TX echoes suppressed on LSM RX       */
    volatile uint32 lsmNoiseSkipped;  /* non-sync / short bursts not shown    */

    /* Half-duplex relay arbitration telemetry */
    volatile uint32 relayState;       /* 0 = IDLE, 1 = REQ (E->L), 2 = RSP    */
    volatile uint32 relayReqCount;    /* ECU->LSM request locks taken         */
    volatile uint32 relayRspCount;    /* LSM->ECU response locks taken        */
    volatile uint32 relayResyncs;     /* idle/overflow lock resyncs           */

    /* Status */
    volatile uint32 initOk;           /* 1 = both ASCLIN channels initialised */
    volatile uint32 active;           /* 1 = CAN_SEL HIGH, forwarding live    */
    volatile uint32 deviceId;         /* 0 = Nichia/TLD816K, 1 = Osram        */
    volatile uint32 stmTicksPerUs;    /* STM0 ticks per microsecond           */
} CanUartBridgeStats;

extern CanUartBridgeStats g_canUartBridgeStats;

/** Initialise both CAN-UART bridge ASCLIN channels (ECU=ASCLIN5, LSM=ASCLIN4)
 *  for the selected LSM diagnostic UART variant.
 *  deviceId: 0 = Nichia/TLD816K (8N1), 1 = Osram/KEWGBXXD1U (8O2).
 *
 *  TX pins idle HIGH as soon as configured.  Forwarding stays OFF and CAN_SEL
 *  stays LOW (fail-safe direct bridge) until can_uart_bridge_set_active(TRUE)
 *  is called. */
void can_uart_bridge_init(uint8 deviceId);

/** Enable/disable active forwarding.
 *  TRUE  -> drives CAN_SEL HIGH (adapter routes bus through AURIX) and starts
 *           transparent forwarding + monitoring.
 *  FALSE -> stops forwarding and drives CAN_SEL LOW (fail-safe direct bridge).
 *
 *  Must only be called after can_uart_bridge_init() so the UART RX/TX is
 *  ready and TX lines already idle HIGH before the bus is routed through. */
void can_uart_bridge_set_active(boolean enable);

/** TRUE if the active bridge is currently forwarding (CAN_SEL HIGH). */
boolean can_uart_bridge_is_active(void);

/** Service the bridge: handle a pending enable request, extract completed
 *  monitor frames and mirror telemetry counters.  Runs the byte forwarding's
 *  monitoring half.  MUST be called from CPU2 (the core that owns the bridge RX
 *  ISRs); the forwarding itself runs in those ISRs.  Call as fast as practical
 *  (tight loop) so the shared parse accumulator never overflows. */
void can_uart_bridge_tick(void);

/** Drain monitor frames produced by the CPU2 bridge into the can_diag queue.
 *  MUST be called from CPU0 (same core as can_diag + the GETH diagnostic TX),
 *  once per main-loop iteration. */
void can_uart_bridge_poll_out(void);

/** Reset soft counters, mirrored debugger stats and parser/accumulator state
 *  (does NOT re-init the ASCLIN channels or change bridge status).  Call from
 *  CPU2, which owns the bridge RX ISRs and counters. */
void can_uart_bridge_reset_state(void);

#endif /* CAN_UART_BRIDGE_H */
