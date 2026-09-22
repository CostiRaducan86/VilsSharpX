#ifndef ADAPTER_CTRL_H
#define ADAPTER_CTRL_H

#include "Ifx_Types.h"

/*
 * SmartVisio Adapter control — GPIO-driven selectors.
 *
 * Adapter sits between ECU (ECU_MAIN_J1) and LSM device (LSM_MAIN_J5).
 * Aurix TFT kit controls the adapter via LOCAL_J3 (X103 connector).
 *
 * ECU_MAIN_J1 pinout:
 *   Pin 1: ECU_LVDS_IN_H   Pin 2: ECU_LVDS_IN_L    Pin 3: ECU_5V_LOGIC
 *   Pin 5: ECU_RL_DET      Pin 7: ECU_CAN_L        Pin 8: ECU_CAN_H
 *
 * LSM_MAIN_J5 pinout:
 *   Pin 2: LSM_CAN_L       Pin 3: LSM_CAN_H        Pin 5: LSM_5V_LOGIC
 *   Pin 7: LSM_LVDS_OUT_L  Pin 8: LSM_LVDS_OUT_H   Pin 13: LSM_RL_DET
 *
 * LOCAL_J3  pinout (adapter  ←→  X103 Aurix connector):
 *   Pin 1:  GND              ←→  GND    (X103-3)
 *   Pin 2:  TTL_SEL          ←→  P20.0  (X103-10) [GPIO]
 *   Pin 3:  TTL_FROM_ECU_3V3 ←→  P14.8  (X103-7)  [ASCLIN1 RX LVDS]
 *   Pin 4:  TTL_FROM_LOCAL   ←→  P02.2  (X103-15) [ASCLIN1 TX LVDS]
 *   Pin 5:  LOGIC_5V_SEL     ←→  P21.3  (X103-6)  [GPIO]
 *   Pin 6:  LOCAL_RL_DET     ←→  P14.7  (X103-8)  [GPIO]
 *   Pin 7:  RL_DET_SEL       ←→  P21.2  (X103-5)  [GPIO]
 *   Pin 8:  CAN_TX_LSM       ←→  P00.9  (X103-31) [ASCLIN4 TX CAN_UART]
 *   Pin 9:  GND              ←→  GND    (X103-4)
 *   Pin 10: CAN_RX_LSM       ←→  P00.12 (X103-34) [ASCLIN4 RX CAN_UART]
 *   Pin 11: CAN_RX_ECU       ←→  P00.6  (X103-28) [ASCLIN5 RX CAN_UART]
 *   Pin 12: CAN_SEL          ←→  P14.6  (X103-9)  [GPIO]
 *   Pin 13: CAN_TX_ECU       ←→  P00.7  (X103-29) [ASCLIN5 TX CAN_UART]
 *   Pin 14: 3v3_LOCAL        ←→  Output 3.3V (not connected to X103)
 *   Pin 15: LED_POWER_SEL    ←→  P21.4  (X103-11) [GPIO]
 *   Pin 16: AURIX_5V_IN      ←→  V_UC   (X103-2)
 *
 * Logic levels:
 *   TTL_SEL:       LOW/default = ECU passthrough, HIGH = local TTL injection through 5V-tolerant buffer U8
 *   TTL_FROM_LOCAL: AURIX P02.2 is held HIGH while no local LVDS stream is generated (UART idle).
 *   RL_DET_SEL:    LOW/default = ECU_RL_DET to LSM_RL_DET, HIGH = LOCAL_RL_DET to LSM_RL_DET.
 *   LOCAL_RL_DET:  LOW/default = GND (low resolution) [default LOW], HIGH = 5.0V (high resolution)
 *   LOGIC_5V_SEL:  LOW/default = ECU_5V_LOGIC powers LSM_5V_LOGIC, HIGH = LOCAL_5V powers LSM_5V_LOGIC.
 *   LED_POWER_SEL: LOW/default = ECU_LED_POWER to LSM, HIGH = EXT_LED_POWER to LSM.
 *   CAN_SEL:       LOW = direct ECU_CAN_H/L to LSM_CAN_H/L bridge, HIGH/default = active ECU↔SmartVisio CAN_UART mode
 *                  using two TJA1051T-3 transceivers.
 *
 * CAN_UART active mode:
 *   ECU side transceiver U3: CAN_TX_ECU / CAN_RX_ECU connected to ECU_CAN_H/L when CAN_SEL is HIGH.
 *   LSM side transceiver U10: CAN_TX_LSM / CAN_RX_LSM connected to LSM_CAN_H/L when CAN_SEL is HIGH.
 *   Firmware must forward ECU→LSM and LSM→ECU traffic, with optional processing/injection on the LSM→ECU 
 *   direction.
 *   CAN_TX_ECU and CAN_TX_LSM should idle HIGH before enabling CAN_SEL
 */

/* Control Mode */
typedef enum {
    ADAPTER_MODE_ECU     = 0,   /* ECU in the chain (default) */
    ADAPTER_MODE_DIRECT  = 1    /* Direct control (no ECU) */
} adapter_control_mode_t;

/* CAN UART Mode */
typedef enum {
    CAN_UART_ECU_LSM   = 0,   /* K2=0,K3=0 — ECU↔direct passthrough↔LSM (default) */
    CAN_UART_ECU_SMARTVISIO_LSM   = 1,   /* K2=1,K3=1 — ECU↔SmartVisio↔LSM (Firmware forwards ECU→LSM and LSM→ECU 
*                               traffic) */
    CAN_UART_SMARTVISIO_LSM = 2    /* Adapter_V2: mapped to same CAN_SEL=HIGH path as DIRECT
*                               (reserved for protocol compatibility with UI) */
} adapter_can_uart_mode_t;

/* LVDS source selected by the adapter TTL multiplexer. */
typedef enum {
    ADAPTER_TTL_ECU   = 0,    /* U2 receiver output: TTL_FROM_ECU_3V3 */
    ADAPTER_TTL_LOCAL = 1     /* AURIX output: TTL_FROM_LOCAL on P02.2 */
} adapter_ttl_source_t;

/* Initialise all adapter GPIO pins as outputs with ECU-default state. */
void adapter_ctrl_init(void);

/* Apply the Control Mode (ECU vs Direct). */
void adapter_ctrl_set_mode(adapter_control_mode_t mode);
void adapter_ctrl_tick(void);

/* Return the currently selected adapter control mode. */
adapter_control_mode_t adapter_ctrl_get_mode(void);

/* Return the currently selected CAN-UART routing mode. */
adapter_can_uart_mode_t adapter_ctrl_get_can_uart(void);

/* Prepare the local LVDS source in UART idle and select the requested input. */
void adapter_ctrl_prepare_ttl_local_idle(void);
void adapter_ctrl_set_ttl_source(adapter_ttl_source_t source);

/* Re-claim P02.2 as a GPIO driven HIGH (UART idle).
 * Called after the Direct Control Mode LVDS transmitter releases the pin, so
 * the adapter keeps seeing a stable idle level on TTL_FROM_LOCAL. */
void adapter_ctrl_ttl_local_take_gpio(void);

/* Apply the CAN UART Mode (independent of control mode). */
void adapter_ctrl_set_can_uart(adapter_can_uart_mode_t mode);

/* Adapter_V2 active CAN-UART bridge selector (drives CAN_SEL only).
 *   enable = TRUE  -> CAN_SEL HIGH: bus routed through SmartVisio Adapter transceivers
 *                     (active CAN-UART forwarding via can_uart_bridge.c).
 *   enable = FALSE -> CAN_SEL LOW : fail-safe direct bridge ECU<->LSM.*/
void adapter_ctrl_set_can_bridge(boolean enable);

/* Convenience: apply both modes at once. */
void adapter_ctrl_apply(adapter_control_mode_t ctrl, adapter_can_uart_mode_t can);

#endif /* ADAPTER_CTRL_H */
