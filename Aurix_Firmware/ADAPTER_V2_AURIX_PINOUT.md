# Adapter_V2 <-> AURIX TC397 Pinout and Wiring Guide

## Purpose

This document is the single reference for rewiring the SmartVisio Adapter_V2 to the
AURIX TC397 Application Kit (X103), especially for active CAN-UART bridge mode.

It consolidates:

- X103 connector pin assignment (as used in this project)
- Adapter LOCAL_J3 control and CAN-UART signal mapping
- Verified ASCLIN channel assignment for bidirectional active CAN-UART forwarding
- Rewiring checklist and quick validation steps

## Source Reference

- Infineon Application Kit TC3X7 user manual
- Section 6.1: Connector Pin Assignment (TC387/TC397)
- Figure 6-1: IO connectors pinout (X103/X102)

## X103 Pinout (Relevant Pins)

X103 is a 2x20 connector. The project-relevant pins are:

<!-- markdownlint-disable-next-line MD033 -->
<img src="images/Connector_Pin_Assignment.jpg" alt="X103 connector pin assignment" width="500" align="right">

| X103 pin | AURIX port | Note |
| --- | --- | --- |
| 2 | V_UC | 5V0 |
| 3 | GND | Ground |
| 4 | GND | Ground |
| 7 | P14.8 | ASCLIN1 RX (LVDS existing path) |
| 8 | P14.7 | LOCAL_RL_DET ( GPIO in current adapter_ctrl) |
| 9 | P14.6 | CAN_SEL |
| 10 | P20.0 | TTL_SEL |
| 11 | P21.4 | LED_POWER_SEL |
| 5 | P21.2 | RL_DET_SEL |
| 6 | P21.3 | LOGIC_5V_SEL |
| 15 | P02.2 | ASCLIN1 TX (LVDS existing path) |
| 16 | P02.3 | CAMERA TRIGGER (Basler Camera hw trigger) |
| 28 | P00.6 | ASCLIN5 RX (CAN_RX_ECU) |
| 29 | P00.7 | ASCLIN5 TX (CAN_TX_ECU) |
| 31 | P00.9 | ASCLIN4 TX (CAN_TX_LSM) |
| 34 | P00.12 | ASCLIN4 RX (CAN_RX_LSM) |

<!-- markdownlint-disable-next-line MD033 -->
<div style="clear: both;"></div>

## Adapter Connector Pinouts (From Captures)

The following mappings were extracted from the attached PCB/schematic captures.
Pins marked `X` are shown as not connected (NC) in the captures.

## ECU_MAIN_J1 (16-pin)

| Pin | Signal |
| --- | --- |
| 1 | ECU_LVDS_IN_H |
| 2 | ECU_LVDS_IN_L |
| 3 | ECU_5V_LOGIC |
| 4 | GND |
| 5 | ECU_RL_DET |
| 6 | GND |
| 7 | ECU_CAN_L |
| 8 | ECU_CAN_H |
| 9 | GND |
| 10 | GND |
| 11 | X (NC) |
| 12 | X (NC) |
| 13 | GND |
| 14 | GND |
| 15 | GND |
| 16 | X (NC) |

## LSM_MAIN_J5 (16-pin)

| Pin | Signal |
| --- | --- |
| 1 | GND |
| 2 | LSM_CAN_L |
| 3 | LSM_CAN_H |
| 4 | GND |
| 5 | LSM_5V_LOGIC |
| 6 | GND |
| 7 | LSM_LVDS_OUT_L |
| 8 | LSM_LVDS_OUT_H |
| 9 | X (NC) |
| 10 | X (NC) |
| 11 | X (NC) |
| 12 | X (NC) |
| 13 | LSM_RL_DET |
| 14 | X (NC) |
| 15 | GND |
| 16 | GND |

## LOCAL_J3 (16-pin, full pinout)

| Pin | Signal |
| --- | --- |
| 1 | GND |
| 2 | TTL_SEL |
| 3 | TTL_FROM_ECU_3V3 |
| 4 | TTL_FROM_LOCAL |
| 5 | LOGIC_5V_SEL |
| 6 | LOCAL_RL_DET |
| 7 | RL_DET_SEL |
| 8 | CAN_TX_LSM |
| 9 | GND |
| 10 | CAN_RX_LSM |
| 11 | CAN_RX_ECU |
| 12 | CAN_SEL |
| 13 | CAN_TX_ECU |
| 14 | 3V3_LOCAL |
| 15 | LED_POWER_SEL |
| 16 | AURIX_5V_IN |

## 2-Pin Power Connectors

| Connector | Pin 1 | Pin 2 |
| --- | --- | --- |
| EXT_5V_IN_J1 | EXT_5V_IN | GND |
| ECU_PWR_J2 | ECU_LED_POWER | GND |
| EXT_PWR_J4 | EXT_LED_POWER | GND |
| LSM_PWR_J6 | LSM_LED_POWER | GND |

## Functional Grouping (Quick Reference)

| Group | ECU side | Adapter control | LSM side |
| --- | --- | --- | --- |
| LVDS | ECU_LVDS_IN_H/L | TTL_SEL, TTL_FROM_ECU_3V3, TTL_FROM_LOCAL | LSM_LVDS_OUT_H/L |
| CAN-UART | ECU_CAN_H/L | CAN_SEL, CAN_TX_ECU, CAN_RX_ECU, CAN_TX_LSM, CAN_RX_LSM | LSM_CAN_H/L |
| RL detect | ECU_RL_DET | RL_DET_SEL, LOCAL_RL_DET | LSM_RL_DET |
| 5V logic | ECU_5V_LOGIC | LOGIC_5V_SEL, AURIX_5V_IN, 3V3_LOCAL | LSM_5V_LOGIC |
| LED power | ECU_LED_POWER | LED_POWER_SEL, EXT_LED_POWER | LSM_LED_POWER |

## Final Wiring Table (X103 <-> LOCAL_J3)

Use this as the 1:1 cabling checklist.

| Function | AURIX port | X103 pin | LOCAL_J3 pin | Direction |
| --- | --- | --- | --- | --- |
| GND | GND | 3 | 1 | Common reference |
| TTL_SEL | P20.0 (GPIO) | 10 | 2 | AURIX -> Adapter |
| TTL_FROM_ECU_3V3 | P14.8 (ASCLIN1 RX LVDS) | 7 | 3 | Adapter -> AURIX |
| TTL_FROM_LOCAL | P02.2 (ASCLIN1 TX LVDS) | 15 | 4 | AURIX -> Adapter |
| LOGIC_5V_SEL | P21.3 (GPIO) | 6 | 5 | AURIX -> Adapter |
| LOCAL_RL_DET | P14.7 (GPIO) | 8 | 6 | AURIX -> Adapter |
| RL_DET_SEL | P21.2 (GPIO) | 5 | 7 | AURIX -> Adapter |
| CAN_TX_LSM | P00.9 (ASCLIN4 TX) | 31 | 8 | AURIX -> Adapter |
| GND | GND | 4 | 9 | Common reference |
| CAN_RX_LSM | P00.12 (ASCLIN4 RXA) | 34 | 10 | Adapter -> AURIX |
| CAN_RX_ECU | P00.6 (ASCLIN5 RXA) | 28 | 11 | Adapter -> AURIX |
| CAN_SEL | P14.6 (GPIO) | 9 | 12 | AURIX -> Adapter |
| CAN_TX_ECU | P00.7 (ASCLIN5 TX) | 29 | 13 | AURIX -> Adapter |
| 3v3_LOCAL | ------------- | -- | 14 | Adapter -> OUT |
| LED_POWER_SEL | P21.4 (GPIO) | 11 | 15 | AURIX -> Adapter |
| AURIX_5V_IN | V_UC (5V0) | 2 | 16 | AURIX -> Adapter |
| AURIX_CAM_TRIGGER | P02.3 (GPIO) | 16 | 3 (Camera) | AURIX -> Camera |

## ASCLIN Assignment for Active CAN-UART Bridge

Selected channels:

- ECU side transceiver path: ASCLIN5
  - RX: IfxAsclin5_RXA_P00_6_IN (X103 pin 28)
  - TX: IfxAsclin5_TX_P00_7_OUT (X103 pin 29)
- LSM side transceiver path: ASCLIN4
  - RX: IfxAsclin4_RXA_P00_12_IN (X103 pin 34)
  - TX: IfxAsclin4_TX_P00_9_OUT (X103 pin 31)

UART parameters per device mode:

- Osram KEWGBXXD1U: 2 Mbaud, 8O2
- Nichia TLD816K: 2 Mbaud, 8N1

## Control Logic Summary (Adapter_V2)

- CAN_SEL LOW: fail-safe direct bridge ECU_CAN_H/L <-> LSM_CAN_H/L
- CAN_SEL HIGH: active mode through AURIX (U3 ECU-side + U10 LSM-side transceivers)

Startup safety rule:

1. Initialize control GPIOs to safe defaults (CAN_SEL LOW)
2. Configure UART TX pins so they idle HIGH
3. Initialize UART RX/TX bridge channels
4. Set CAN_SEL HIGH only after software is ready

## Quick Rewiring Checklist

1. Power off hardware.
2. Wire GND first (X103 pin 3 or 4 -> LOCAL_J3 pin 1 or 9).
3. Wire the four CAN-UART lines exactly as in the table above.
4. Wire CAN_SEL (X103 pin 9 -> LOCAL_J3 pin 12).
5. Verify continuity and no shorts between adjacent X103 pins.
6. Power on with CAN_SEL forced LOW first (fail-safe check).
7. Enable active bridge in firmware and verify diagnostic traffic.

## Bring-Up Validation

After flashing firmware:

1. Confirm active bridge status in debugger/watch (`g_canUartBridgeStats.active == 1`).
2. Check counters increasing:
   - `ecuRxBytes`, `lsmRxBytes`
   - `ecuTxForwarded`, `lsmTxForwarded`
3. Ensure drops stay zero during normal traffic:
   - `ecuTxDropped == 0`
   - `lsmTxDropped == 0`
4. Confirm CAN diagnostic records are visible in PC UI.

## Notes

- This mapping corrects earlier temporary table offsets.
- P00.6/P00.7/P00.9/P00.12 are intentionally chosen because they are exposed on X103 and do not conflict with LVDS (ASCLIN1).
- Keep this file updated if adapter harness or AURIX pin usage changes.
