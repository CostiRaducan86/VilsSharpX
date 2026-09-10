# Direct Control Mode Implementation Tracking

**Last updated:** 2026-09-10
**Design reference:** [Direct_Control_Mode_Architecture.md](Direct_Control_Mode_Architecture.md)
**Active phase:** Phase 2 closed; Phase 4 (CAN-UART master) in progress

## Status legend

- **Complete**: implemented and validated on hardware or against a captured trace.
- **Partial**: implemented, but validation is still pending.
- **In progress**: actively being worked on.
- **Open**: planned, not started.
- **Blocked**: waiting on a decision, hardware or missing information.

## Current status summary

| Area | Status | Notes |
| --- | --- | --- |
| Design decisions D1-D10 | Complete | Closed in the architecture document, section 16. |
| Hardware bring-up measurements | Complete | Phase 0 below. |
| Adapter selector control for Direct Mode | Complete | `adapter_ctrl_set_mode(ADAPTER_MODE_DIRECT)` already sets all selectors. |
| `SET_ADAPTER_MODE` Ethernet command | Complete | `AdapterModeCommand.cs` and `FE_CMD_SET_ADAPTER` handler exist. |
| UI mode constraints | Complete | Direct control forces LVDS Generator and CAN "SmartVisio LSM". |
| `P02.2` as ASCLIN1 TX | Complete | Validated on hardware with a Saleae capture. |
| LVDS frame generation (OSRAM) | Complete | Byte stream and CRC verified against the ECU algorithm on captured frames. |
| LVDS frame generation (NICHIA) | Partial | 256x64 row builder, CRC-16, 12.5 Mbaud 8N1 TX and row timing are implemented; Nichia hardware validation remains pending. |
| AVTP ingest on AURIX | Partial | `avtp_rx.c` implemented; hardware validation pending. |
| CAN-UART master | Complete | CPU2 replays the OSRAM sequence, captures LSM responses and exposes master telemetry. |
| LSM start-up sequence | Complete | Extracted from the ECU trace and validated against the LSM 2.0 start-up conversation. |
| Loopback to pane B | Partial | OSRAM path is validated; Nichia row assembly and `NI` Ethernet output are implemented, but hardware end-to-end validation remains pending. |
| Direct Mode telemetry to PC | Partial | The existing `CD` transaction record path is functional; dedicated `DS` status consumer remains open. |
| CAN replay from `.rply` | Open | `BtnCanReplay_Click` is a stub. |

## Decisions

All design decisions are closed. The full rationale is in the architecture document,
section 16 (decision log); the sections referenced below hold the detail.

| ID | Decision | Outcome | Status |
| --- | --- | --- | --- |
| D1 | AVTP destination MAC seen by the AURIX | Pass-all-multicast enabled while Direct Mode is armed; stream sent unchanged; unicast/broadcast remain a fallback (section 6.4) | Closed |
| D2 | ASCLIN1 direction handling | TX-only while in Direct Mode, RX-only in ECU Mode (section 5.1) | Closed |
| D3 | Source-to-device geometry mapping | 1:1 when equal, centre crop x 32 / y 8 by default, downscale optional (section 6.6) | Closed |
| D4 | LSM start-up sequence source | Firmware built-in table plus PC `.rply` replay override (section 7.4) | Closed |
| D5 | Generator frame rate | Fixed 20 ms first, configurable later, clamped to the device ceiling (section 6.7) | Closed |
| D6 | `LOCAL_RL_DET` default level | Not applicable; `RL_DET` unwired on both ends, level stays LOW (section 4.4) | Closed |
| D7 | Behaviour when the AVTP source stops | Repeat 200 ms, then all-zero frames, stream never stopped (section 6.7) | Closed |
| D8 | New command IDs | `0x08`, `0x09`, `0x0A` and magic `DS` = `0x4453` confirmed free (section 8) | Closed |
| D9 | LVDS transmit completion detection | Polled in the main loop, no transmit ISR (section 5.4) | Closed |
| D10 | LVDS transmit DMA feeding | 8-byte blocks, FIFO level 8, one transaction per frame, primed by a software request (section 5.4) | Closed |

## Phase 0 â€” Preparation and hardware bring-up

Measurement phase, completed. No firmware or application code was changed during it.

| # | Task | Status | Result |
| --- | --- | --- | --- |
| 0.1 | Confirm the LSM LED supply voltage and current limit | Complete | Bench supply set to approximately 4 V on `EXT_PWR_J4`. |
| 0.2 | Confirm the LSM harness scope | Complete | `LSM_MAIN_J5` carries only `LSM_CAN_L`, `LSM_CAN_H`, `LSM_5V_LOGIC`, `LSM_LVDS_OUT_L`, `LSM_LVDS_OUT_H`; LED power arrives on `LSM_PWR_J6`. |
| 0.3 | Check `ECU_RL_DET` and `LSM_RL_DET` | Complete | Both unwired. The floating LSM pin measures approximately 1.8 V against LSM ground, consistent with its 1 kOhm pull-up to 3.3 V plus 5.11 kOhm series network. Out of scope. |
| 0.4 | Determine the `LOCAL_RL_DET` policy | Complete | Not applicable while unwired; the pin stays LOW. D6 closed. |
| 0.5 | Reference CAN-UART traces | Complete | Full power-on, start-up, init, config and cyclic run traces plus Saleae CSV exports are archived in `docs/CAN_UART_Communication/`. |
| 0.6 | Reference LVDS frame format | Complete | Covered by the existing OSRAM and NICHIA protocol documentation and the receive parsers; no new capture required. |
| 0.7 | Verify the `X103-15` to `LOCAL_J3-4` wire | Complete | Present and continuous. |
| 0.8 | Probe `P02.2` idle level | Complete | Stable HIGH at 5 V. The 5 V TC397 port is level-shifted to 3.3 V by the adapter's 74LVC1G17. |
| 0.9 | Verify the adapter selector levels in Direct control | Complete | Selector behaviour matches `adapter_ctrl`; `TTL_SEL` has a 10 kOhm pull-down, so the ECU input is the fail-safe default. |
| 0.10 | Verify `TTL_SEL` HIGH routes the local source to the LSM | Complete | Proven indirectly: injecting the local-idle LVDS fault is seen by the ECU as a communication fault. |
| 0.11 | Power the LSM from the bench supply without the ECU | Complete | Confirmed with `EXT_PWR_J4` and the adapter local 5 V. |
| 0.12 | AVTP stream parameters | Complete | Fixed source MAC `3C:CE:15:00:00:19`, destination MAC `01:00:5E:16:00:12`, VLAN 70; the remaining fields are in the C# Ethernet Configuration window. D1 confirmed: pass-all-multicast is required. |
| 0.13 | AVTP frame geometry per device profile | Open | To be confirmed while implementing the Phase 2 ingest. |
| 0.14 | ECU LVDS frame period | Complete | Existing observation of approximately 47-50 fps for OSRAM; the generator default of 50 fps matches it. |

### Phase 0 outcome

- `RL_DET` is removed from the Direct Control Mode scope.
- `TTL_FROM_LOCAL` is a 0-5 V signal, not 3.3 V; the adapter performs the conversion.
  The architecture document and the terminology table were corrected.
- The LVDS output chain is documented end to end in architecture section 4.5.
- The AVTP addressing is fixed and confirms the pass-all-multicast decision.

## Phase 1 -> LVDS transmit path (OSRAM first)

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 1.1 | Add `lvds_frame_build.c/.h` with the OSRAM byte-stream builder | Complete | Header, 25600 pixels, CRC-32 from `osram_crc32.c`, little-endian on the wire. |
| 1.2 | Add the NICHIA row builder | Complete | 0x5D, row address with parity bits 7:6, 256 pixels and CRC-16 from `rx_crc.c`; 64 rows produce a 16640-byte stream. |
| 1.3 | Add `lvds_tx.c/.h`: ASCLIN1 TX on `P02.2`, DMA channel 2 | Complete | Device-dependent baud/framing; OSRAM uses 20 Mbaud 8O2 and Nichia uses 12.5 Mbaud 8N1. Buffers are in `dsram4`; completion is polled. |
| 1.4 | Implement the safe ASCLIN1 direction switch | Complete | `asclin1_dma_stop()` disarms the receive DMA and service request first. |
| 1.5 | Implement the `P02.2` handover without a LOW glitch | Complete | ASCLIN takes the pin while it idles HIGH; `adapter_ctrl_ttl_local_take_gpio()` reverses it. |
| 1.6 | Add a built-in test pattern source | Complete | Two static patterns: full black and a value-120 grid every 4 pixels and 4 rows, selected from `g_lvdsTxTestPattern`. |
| 1.7 | Add fixed-period transmit scheduling with repeat-on-underrun | Complete | 20 ms default, clamped to a 15 ms minimum. |
| 1.8 | Add TX telemetry counters | Complete | `g_lvdsTxStats`: built, sent, repeated, superseded, late starts, idle periods, stall re-arms, measured frame time, active pattern. |
| 1.9 | Suppress the receive watchdog while transmitting | Complete | `lvds_recovery_tick()` returns early; a device switch reconfigures the transmitter instead of the receiver. |
| 1.10 | Arm the transmitter from `SET_ADAPTER_MODE` | Complete | Interim activation until `FE_CMD_DIRECT_LVDS_CFG` and its PC sender exist. |
| 1.11 | Build and flash the firmware from ADS | Complete | Build succeeded, flashed and run. |
| 1.12 | Validate on Saleae: baud, 8O2 framing, header, CRC, frame period | Complete | See the test evidence below. |
| 1.13 | Verify the buffer placement in the map file | Complete | `g_lvdsTxStream` resolves to `0x30000000` (dsram4). |
| 1.14 | Verify ECU Mode is unaffected after entering and leaving Direct control | Complete | ECU Mode unchanged before and after; `P02.2` returns to a stable HIGH. |
| 1.15 | Replace the bring-up ramp with the two requested test patterns | Complete | Requires a reflash to observe. |

### Phase 1 validation procedure

1. Flash the firmware and keep the UI in ECU control; confirm LVDS reception is unchanged.
1. Switch the UI to Direct control. The transmitter arms itself with the test pattern.
1. Probe `LOCAL_J3` pin 4 with the Saleae and decode as UART, 20 Mbaud, 8 data bits, odd
   parity, **two stop bits** for OSRAM.
1. Confirm the frame header `80 A5 AA 55`, 25600 pixel bytes and a 4-byte CRC, then check
   that the frame repeats every 20 ms.
1. Read `g_lvdsTxStats` in the debugger: `framesSent` increments at approximately 50 Hz,
   while `stallRearms`, `lateStarts` and `submitRejected` stay at zero.
1. Switch back to ECU control and confirm `P02.2` returns to a stable HIGH and the LVDS
   receive counters resume.

## Phase 2 -> AVTP ingest on AURIX

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 2.1 | Enable pass-all-multicast in `MAC_PACKET_FILTER` while Direct Mode is armed | Complete | `frame_eth_set_pass_all_multicast()`, restored on exit; per D1. |
| 2.2 | Add `avtp_rx.c/.h`: VLAN skip, RVF decode, 20-chunk reassembly | Complete | Mirrors `AvtpRvfParser.cs`; buffers in `dsram5`. |
| 2.3 | Dispatch ethertype `0x22F0` in `frame_eth_poll_rx` | Complete | VLAN and stacked-VLAN ethertypes also routed; the buffer is still always freed. |
| 2.4 | Take the receive length from the descriptor | Complete | `RDES3.PL` read before the buffer is handed over. |
| 2.5 | Connect `avtp_rx` output to the LVDS transmitter | Complete | `direct_mode_pixel_tick()` in the CPU0 loop. |
| 2.6 | Add AVTP telemetry counters | Complete | `g_avtpRxStats`: accepted, rejected, complete, incomplete, restarted, dropped-busy, duplicate chunks, chunk mask. |
| 2.7 | Implement the starvation policy | Complete | Repeat for 200 ms, then black; `starvationEvents` and `starved`. |
| 2.8 | Measure RX capacity and remove bulk work from the receive path | Complete | Ping-pong publish, build at the transmit rate, extra drain after the handoff. |
| 2.9 | Validate end to end: PC image on the scope-decoded LVDS stream | Open | Hardware validation. |
| 2.10 | Re-measure `framesIncomplete` after the receive-path optimisation | Complete | No improvement; the hand-off was not the cause. |
| 2.11 | Run the loopback isolation experiment | Complete | 18.45 versus 18.44 accepted chunks per frame with the mirror off versus every frame: no effect. |
| 2.12 | Report the RX health counters during an AVTP run | Complete | `rxFifoOverflowPackets` 12751, RBU never set, `rxPollBudgetHits` 0, `rxNullBuffers` 0. |
| 2.13 | Size the MTL receive FIFO for a 20-packet burst | Complete | RX queue raised from 2560 to 8192 bytes. |
| 2.14 | Re-measure after the FIFO change | Complete | 20.01 accepted chunks per 20-chunk frame; `framesIncomplete` 9 out of 15908 frames. |
| 2.15 | Raise the RX descriptor ring if RBU starts setting | Open | Not needed so far. |
| 2.16 | Investigate the periodic pane B dropouts | Complete | Traced to a receive DMA stall: the tail pointer was never published after freeing descriptors. |
| 2.17 | Publish the RX descriptor tail pointer on every drain | Complete | Keeps the ring minus one slot available to the DMA. |
| 2.18 | Re-measure stability after the tail-pointer fix | Complete | `rxRecoveries` and `rxNoProgressEvents` stayed at 0; the pane B dropouts are gone. |
| 2.19 | Raise the RX descriptor ring to hold a full burst | Complete | 32 descriptors through `IFXGETH_MAX_RX_DESCRIPTORS` in `Ifx_Cfg.h`, with a compile-time match check. |
| 2.20 | Re-measure the residual FIFO overflow | Complete | Counters stay flat; Saleae confirms the stream is identical and correctly delayed at all four probe points. |

## Phase 3 -> Loopback and PC integration

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 3.1 | Push the transmitted frame to `frame_eth` for the `OS` loopback | Complete | OSRAM frame loopback is validated; the common path also supports Nichia row assembly and `NI` fragments. |
| 3.2 | Move the camera trigger source to the transmit frame-complete event | Complete | `direct_mode` fires the single-shot trigger on each completed transmission. |
| 3.3 | Add `DirectModeCommand.cs` for the new commands | Open | IDs `0x08`, `0x09`, `0x0A` per D8. |
| 3.4 | Add the Direct Mode status record and its PC-side consumer | Open | Surfaced in the diagnostics log/panel. |
| 3.5 | Route the AVTP transmission to the AURIX NIC when Direct Mode is active | Open | Both AVTP Monitoring and AVTP Generator. |
| 3.6 | Label pane B as loopback in Direct Mode | Open | Avoids misreading the comparison. |
| 3.7 | Add UI interlocks (no fault injection active, AVTP source running) | Open | Blocks unsafe entry. |
| 3.8 | Run `dotnet build` and fix warnings after the C# changes | Open | Required by the project build rules. |

## Phase 4 -> CAN-UART master

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 4.1 | Extract the ECU start-up register sequence from the OSRAM trace | Complete | `scripts/analyze_can_uart_trace.py`; 1290 transactions, 338 writes to 27 registers. |
| 4.2 | Extract the cyclic run pattern and its period | Complete | 32 transactions per super-cycle, about 33.7 ms; keep-alive `W 0x0006 = 0x3100` per group. |
| 4.3 | Generate the replay table | Complete | `Aurix_Firmware/can_uart_osram_sequence.h`, about 12 KB of request payload. |
| 4.4 | Add `can_uart_master.c/.h` with request scheduling on ASCLIN4 | Complete | Runs on CPU2, fed by the bridge relay pump, own echo filtering. |
| 4.5 | Add response capture, timeout and retry handling | Complete | Response length is protocol-derived, with idle fallback for truncated answers, echo classification and response timeout telemetry in `g_canUartMasterStats`. |
| 4.6 | Feed master transactions into the existing `CD` record path | Complete | Direct Control Record is visible in the CAN/UART monitor; requests and LSM responses are generated locally and published through Ethernet. |
| 4.7 | Keep the defect-injection filters working on master responses | Partial | OSRAM filter path is existing; Nichia response filtering and CRC8 recomputation are implemented, but Nichia hardware validation remains pending. |
| 4.8 | Implement OSRAM startup upload and `.rply` replay in the UI | Complete | `BtnCanReplay_Click` sends `CM` step `0x08` packets and commit `0x09`; NormalRun-only traces are rejected. Nichia has separate step/commit commands and a built-in table. |
| 4.9 | Validate the start-up sequence against the ECU trace on Saleae | Open | Byte and timing comparison. |
| 4.10 | Re-capture an LSM 2.0 trace and diff it against the OSRAM 2.05 table | Complete | Functionally identical: 1289 versus 1290 start-up steps, the only difference being one extra initial `W 0x0001 = 0x0001` poll, and the same 32-step cycle. |
| 4.11 | Fix the echo desynchronisation and timing fidelity | Complete | Gap measured from the last bus byte, quiet-bus gate before transmitting, response timeout cut from 5 ms to 600 us, sync validation counter. |
| 4.12 | Re-test whether the LSM leaves failsafe | Partial | The LSM lit the grid for about two seconds after start-up, then fell back to failsafe. |
| 4.13 | Derive the response length from the request header | Complete | `nRegs * 2 + 2` from HCTRL bits 4:1, because the LSM response contains register data plus CRC and does not repeat the four-byte request header; validated against all 6554 reads in the trace. |
| 4.14 | Classify response framing without assuming a response sync header | Complete | The request header is excluded from the LSM response; exact protocol length counts as valid, shorter data as truncated, empty data as timeout and excess data as a framing slip. |
| 4.15 | Stop assuming the transmit echo is present | Complete | The raw stream is captured per step and the echo detected by comparing with the request. |
| 4.16 | Re-test the cyclic keep-alive cadence | Complete | Fix6 trace has 274/274 complete `HWSTAT W` blocks with 7 reads; `responseTimeouts`, `shortResponses`, `tailBytes` and output-ring drops remained zero in the corresponding Watch capture. |
| 4.17 | Hold the default startup during PC-driven Direct entry | Complete | AURIX waits in Direct Control until a valid uploaded trace is committed; the built-in table remains reserved for future standalone TFT operation. |
| 4.18 | Validate startup detection for loaded traces | Complete | Requires the initial OSRAM polling/configuration signature and a complete 1291-step startup boundary; NormalRun-only traces are rejected. |

## Phase 5 -> System integration and mode transitions

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 5.1 | Add `direct_mode.c/.h` with the documented entry/exit ordering | Open | Single owner of the transition. |
| 5.2 | Make `device_mode_set()` reconfigure generator and master, not only parsers | Open | OSRAM/Nichia switch in Direct Mode. |
| 5.3 | Make `lvds_fault_inject` Direct-Mode aware | Open | Fault = stop generator, not selector toggle. |
| 5.4 | Add the arming-failure fallback to ECU Mode | Open | Fail-safe requirement. |
| 5.5 | Show Direct Mode state on the TFT UI | Open | Preserve uppercase `OSRAM` / `NICHIA`. |
| 5.6 | Keep `Cpu0_Main.c` as a list of API calls | Open | Project firmware rule. |

## Phase 6 -> Nichia support

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 6.1 | Implement the geometry rule (1:1, centre crop, optional downscale) | Complete | Shared device mapping supports 1:1 matching geometry and the documented 320x80-to-256x64 centre crop; hardware validation remains part of Phase 7. |
| 6.2 | Add the Nichia row builder (0x5D, parity, CRC-16) | Complete | 260-byte rows, 64 rows, with the existing Nichia CRC implementation. |
| 6.3 | Add the 12.5 Mbaud 8N1 TX configuration | Complete | Implemented in the shared `lvds_tx` module with a Nichia row timer and per-device framing. |
| 6.4 | Add the Nichia CAN-UART master sequence | Complete | Device-aware master supports the captured Nichia startup/cycle tables, uploaded Nichia steps and the built-in Nichia table. |
| 6.5 | Validate the Nichia Direct Mode on hardware | Open | Requires the Nichia module. |

## Phase 7 -> Validation and hardening

| # | Task | Status | Notes |
| --- | --- | --- | --- |
| 7.1 | Bench validation without the LSM (scope only) | Open | Architecture section 15, step 1. |
| 7.2 | Loopback validation (pane B mirrors the generated image) | Partial | OSRAM loopback is validated; repeat the test with Nichia row assembly and `NI` fragments. |
| 7.3 | LSM powered, LVDS only | Open | Step 3. |
| 7.4 | LSM full Direct Control Mode with the CAN-UART master | Complete | Fix6 trace and Watch capture confirm the LSM running with locally generated CAN-UART traffic and visible Direct Control Record output. |
| 7.5 | 30-minute stability run with flat counters | Open | Step 5. |
| 7.6 | Verify ECU Mode is unaffected after repeated mode switching | Open | Regression guard. |
| 7.7 | Verify the LVDS RX/CRC counters are unaffected in ECU Mode | Open | SRI contention regression guard. |
| 7.8 | Verify a power cycle always returns to the fail-safe ECU defaults | Open | Safety requirement. |

## Firmware files

Added or changed so far:

```text
Aurix_Firmware/lvds_tx.c
Aurix_Firmware/lvds_tx.h
Aurix_Firmware/lvds_frame_build.c
Aurix_Firmware/lvds_frame_build.h
Aurix_Firmware/avtp_rx.c
Aurix_Firmware/avtp_rx.h
Aurix_Firmware/adapter_ctrl.c
Aurix_Firmware/adapter_ctrl.h
Aurix_Firmware/asclin1_dma.c
Aurix_Firmware/asclin1_dma.h
Aurix_Firmware/device_mode.c
Aurix_Firmware/frame_eth.c
Aurix_Firmware/frame_eth.h
Aurix_Firmware/Cpu0_Main.c
```

Still expected in later phases:

```text
Aurix_Firmware/direct_mode.c
Aurix_Firmware/direct_mode.h
Aurix_Firmware/can_uart_master.c
Aurix_Firmware/can_uart_master.h
Aurix_Firmware/lvds_fault_inject.c
Aurix_Firmware/camera_trigger.c
Aurix_Firmware/Cpu2_Main.c
Aurix_Firmware/tft_ui.c
```

## PC application files expected to change

```text
DirectModeCommand.cs (new)
MainWindow.xaml.cs
MainWindow.xaml
AppSettings.cs
UiSettingsManager.cs
AvtpTransmitManager.cs
```

## Test evidence

| Date | Scenario | Result | Artefact |
| --- | --- | --- | --- |
| 2026-08-27 | OSRAM LVDS transmit, Direct control, test pattern source | Pass | `docs/CAN_UART_Communication/digital_Osram_TTL_FROM_LOCAL.csv`, decoded with `scripts/analyze_lvds_saleae.py` |
| 2026-08-27 | Black and grid test patterns, Saleae frame start and end | Pass | Black CRC `0x66844BF6` (wire `F6 4B 84 66`), grid CRC `0x18513E52` (wire `52 3E 51 18`), both matching the computed reference |
| 2026-08-27 | Buffer placement | Pass | `g_lvdsTxStream` at `0x30000000` in `VilsSharpX.map` |
| 2026-08-27 | ECU Mode regression after Direct control | Pass | LVDS reception unchanged, `P02.2` stable HIGH |
| 2026-08-27 | First AVTP end-to-end run, source at 100 fps | Fail, fixed | `framesComplete` 7852 against `framesIncomplete` 7289; `lastChunkMask` consistently missing chunk 3 plus one more; `framesSuperseded` 3892 of 7883 built. Receive path optimised, re-test pending. |
| 2026-08-27 | Second AVTP run after the receive-path optimisation | Fail | Packet acceptance about 18.3 of 20 chunks per frame; `framesComplete` 43 against `framesIncomplete` 3791. Wasted work removed (`framesSuperseded` 0) but the loss is unchanged, so it is not caused by the frame hand-off. Mirror pacing and isolation control added. |
| 2026-08-27 | Loopback isolation experiment | Conclusive | Mirror off: 50092 chunks over 2715 frames = 18.45 per frame. Mirror every frame: 150308 over 8152 = 18.44 per frame. CPU0 transmit load has no measurable effect on receive loss. |
| 2026-08-27 | RX health counters | Root cause found | `rxFifoOverflowPackets` 12751 against about 12400 missing chunks, `rxDmaStatus` 0x444 with RBU never set, `rxPollBudgetHits` 0, `rxNullBuffers` 0. Every lost packet is dropped inside the MAC receive FIFO, upstream of the DMA. |
| 2026-08-27 | AVTP run after raising the RX FIFO to 8192 bytes | Pass | 318632 chunks over 15917 frames = 20.01 per frame; `framesIncomplete` 9, `packetsRejected` 0. Pane B reaches the full 50 fps with the mirror set to every frame. |
| 2026-08-27 | Periodic pane B dropouts after tens of seconds | Root cause found | `rxRecoveries` equal to `rxNoProgressEvents` at every sample (6, 13, 45), `rxFifoOverflowPackets` jumping by about 26000 between samples, all transmit counters and `pushSkippedTxBusy` at zero. About 26000 packets at 2000 per second matches roughly 13 seconds of total receive stall, and 7 recoveries at the 2 second watchdog matches 14 seconds. The receive DMA was suspending and never restarting. |
| 2026-08-27 | Run after the tail-pointer fix | Pass, with a residual | `rxRecoveries` and `rxNoProgressEvents` 0 throughout and no more pane B dropouts. `rxFifoOverflowPackets` still grew in bursts (0, 2458, 2479, 4294) together with `framesRestarted` (0, 336, 348, 581), so a burst still outran the 8-descriptor ring while CPU0 was busy. Ring raised to 32. |
| 2026-08-28 | Run with the 32-descriptor ring | Pass | All receive counters stay flat. Saleae confirms `TTL_FROM_LOCAL`, `TTL_FROM_LOCAL_3V3`, `TTL_TO_LSM` and `TTL_ON_LSM` carry the identical byte stream with correct delays. Phase 2 closed. |
| 2026-08-28 | LSM 2.0 versus OSRAM 2.05 trace comparison | Identical | Only difference is one extra `W 0x0001 = 0x0001` poll at the very start, which is the ECU waiting for the LSM; every following request matches and the cyclic pattern is the same. The generated table is therefore valid for both. |
| 2026-08-28 | First master run against the LSM | Partial | `startupDone` 1 and `echoTimeouts` 0, so transmission and bus routing work, and `lastRspDelayUs` 6 us matches the documented turnaround. But `responseTimeouts` reached 3250 of 9210 requests and `lastResponse` started with `0x01` instead of `0x80 0xA5`, showing the echo accounting had slipped. The LSM stayed dark. |
| 2026-08-28 | Second master run after the timing fixes | Breakthrough, then failsafe | The LSM lit the grid on both the module and the camera for about two seconds around `startupDone`, then went dark. `badSyncResponses` reached 1086 of 1279 responses and `lastRspLen` oscillated between 1 and 11 bytes, far below the 8 to 38 a valid answer has: responses were being cut short, and the resulting delays pushed the keep-alive past the cadence the LSM tolerates. |
| 2026-08-28 | Third master run, with deterministic response length | Root cause found | `responsesOk` stayed at 0 across 2970 requests while `strayBytes` reached 74919, about 25 per request. For the read `80A5A0FB` the captured answer was `80 A3`, which is the data word of the full response `80A5A0FB 80A3 9250`, so the first four bytes of the real answer had been consumed as echo and the remaining 34 bytes, containing no `0x80`, were discarded. The transmit echo is not present on the bus in Direct Control Mode. |
| 2026-08-28 | Fourth master run, with per-transaction echo detection | Root cause found, LSM dark | Cadence is correct: 14 cycles in 0.5 s against the 37.0 ms the trace prescribes, with `txFull`, `strayBytes` and `quietWaits` all at zero. But `responsesOk` was still 0 across 4386 requests, with `shortResponses` 3683 and `badSyncResponses` 952. The expected length was wrong by exactly the four header bytes: the trace line `80A5A0FB 80A3 9250` is request plus answer, and the LSM answers with data and CRC only, `nRegs * 2 + 2` bytes. Every answer therefore measured four bytes short of its target, the `80 A5` sync check was being applied to data, and the byte-count exit never fired, so every read ended on the idle fallback. |
| 2026-08-28 | Fifth master run, with the corrected answer length | Root cause found | The LSM now lights for one to two seconds right at `startupDone` and then falls back to failsafe, which places the fault exactly at the switch to the cyclic table. Measuring the reference trace confirms it: over its 887 keep-alive writes past record 1290 the interval is always between 5923 and 6457 us, average 6192. Our cyclic table opened with a 9492 us gap, because `find_cycle()` returned the first occurrence of the repeating pattern, which still sits in the transition where the ECU had not settled. Every 37 ms super-cycle therefore contained one keep-alive that was 60 percent late. |
| 2026-08-28 | Sixth master run, with the settled keep-alive cadence | Root cause found, receive path | Same behaviour: the LSM lights right after `startupDone` and drops out, `responsesOk` 14 of about 16400 reads, `echoAbsentCount` 3268. `lastResponse` was decisive: for `R 0x00F7` the bus carries `80A5B0F7 0100010000000000 80A3 0000 FFFF ...`, and we captured `80 A5 B0 00 00 00 00 00 80 A3 00 00 FF` . Four to five consecutive bytes are missing from the middle of the burst and the stream resumes correctly, so the LSM does answer and the receive path drops blocks of bytes. |
| 2026-08-28 | Seventh master run, after the receive-path fixes | Large improvement, not closed | `echoAbsentCount` fell from 20 percent of reads to 2.3 percent, `responsesOk` went from 14 to 5096, and `lastRawLen` reached the full 38 bytes for a 16-register read, so complete answers are now being captured. About 88 percent of reads are still short. The LSM behaviour is unchanged: it shows the grid for one to two seconds after `startupDone` and then goes dark, and it does so identically with `ECU_MAIN_J1` unplugged, so ECU-segment noise is not what turns it off. |

Decoded results for the two complete frames in the capture:

| Metric | Value | Expected |
| --- | --- | --- |
| UART decode at 20 Mbaud 8O1 | 95389 bytes, 0 stop errors | clean decode |
| Frame header | `80 A5 AA 55` | OSRAM header |
| Serialisation time | 14084.9 us | 14.08 ms theoretical |
| Frame period | 20.003 ms and 20.006 ms | 20 ms |
| CRC-32 | matches the ECU algorithm on both frames | match |
| Inter-byte gaps | 0 | 0, no DMA underrun |
| `framesSent` versus `framesBuilt` | 1044 versus 1044 | equal |
| `framesRepeated`, `lateStarts`, `idlePeriods`, `stallRearms`, `submitRejected` | all 0 | all 0 |

The third frame in the capture is truncated because the export covers only the visible
Saleae window; its reported CRC mismatch is an artefact of the truncation and is expected.

Observation carried forward: `lastFrameUs` settles at 14085-14089 us against a 14085 us
true serialisation time, so the polled completion tracks the transfer closely. A larger
reading would indicate the main loop spent longer elsewhere in that period, which matters
only if the camera trigger later needs a precise frame-complete event.

The black pattern CRC `0x66844BF6` is also the documented ECU self-test vector for 25600
zero bytes, which independently confirms the generated stream is byte-compatible.

## Change log

| Date | Change |
| --- | --- |
| 2026-08-26 | Initial architecture and tracking documents created. |
| 2026-08-26 | Decisions D1-D8 closed; Phase 0 expanded into a measurement procedure with probe points and exit criteria. |
| 2026-08-26 | Phase 0 completed. `RL_DET` removed from scope, `TTL_FROM_LOCAL` corrected to 0-5 V, LVDS output chain documented, AVTP addressing confirmed. |
| 2026-08-26 | Phase 1 firmware implemented: LVDS stream builders, ASCLIN1 transmit path on `P02.2` with DMA channel 2, test pattern source, pacing and telemetry. Decisions D9 and D10 added. |
| 2026-08-27 | Phase 1 validated on hardware: framing, CRC, period and gap-free serialisation confirmed from a Saleae capture. Test patterns changed to full black and a value-120 grid, selectable from `g_lvdsTxTestPattern`. |
| 2026-08-27 | Phase 2 implemented: AVTP/RVF ingest with pass-all-multicast, descriptor-based receive length, reassembly in `dsram5`, connection to the generator, starvation fallback to black, and the OSRAM loopback for pane B. |
| 2026-08-27 | First AVTP run showed about half the frames incomplete. Root cause: bulk per-frame work on CPU0 exceeded the 85 us receive-ring budget. Reassembly now publishes by index swap, the stream build and loopback run at the transmit rate, and the ring is drained again right after the handoff. |
| 2026-08-27 | Receive loss unchanged after that optimisation, so the hand-off is not the aggressor. Added `direct_mode.c/.h` with an independently paced pane B mirror and a `g_directLoopbackMode` switch, so the mirror's contribution to the loss can be measured directly. |
| 2026-08-27 | Root cause of the AVTP packet loss identified as MTL receive FIFO overflow: 2560 bytes in store-and-forward hold less than two 1330-byte packets, so a 20-packet burst overflows the MAC while descriptors stay free. RX queue raised to 8192 bytes. Recorded why the pixel path stays on CPU0. |
| 2026-08-27 | FIFO fix confirmed: 20.01 of 20 chunks accepted per frame. Closed a loopback buffer race where a new mirror frame could overwrite the buffer still being fragmented, and moved the camera trigger to the transmitted frame for Direct Control Mode. |
| 2026-08-27 | Periodic receive stalls traced to `IfxGeth_Eth_freeReceiveBuffer()` never moving `RXDESC_TAIL_POINTER`: the EQoS receive DMA suspends at the tail and `IfxGeth_Eth_wakeupReceiver()` does not restart an RBU suspend. The tail pointer is now published on every drain, the stall watchdog was shortened to 200 ms, and the duplicate receive poll was removed. |
| 2026-08-27 | Receive descriptor ring raised from 8 to 32 so a full 20-packet AVTP burst fits without depending on CPU0 timing. Set through `IFXGETH_MAX_RX_DESCRIPTORS` in `Configurations/Ifx_Cfg.h`, which `IfxGeth.h` includes before its own `#ifndef` guard, with a compile-time check that `FE_RX_DESCRIPTORS` matches. |
| 2026-08-28 | Phase 2 closed. Phase 4 started: the OSRAM ECU trace was analysed and the replay table generated. Documented the measured start-up and cyclic structure and the integration points with the existing CPU2 bridge. |
| 2026-08-28 | CAN-UART master implemented: state machine on CPU2, byte hand-over from the bridge relay pump, echo filtering, idle-based response detection and timeouts. Armed from `SET_ADAPTER` together with the LVDS generator. |
| 2026-08-28 | Master timing corrected after the first hardware run: the captured gap is now measured from the last byte on the bus, a request is only sent once the bus has been quiet, and the response timeout was reduced from 5 ms to 600 us so a missing answer cannot delay the keep-alive the LSM expects every 8.4 ms. |
| 2026-08-28 | Response framing made deterministic: the length is derived from HCTRL instead of detected by bus idle, and a response must begin with the sync byte so a lost echo cannot shift every following step. This removes the truncation that was starving the keep-alive cadence. |
| 2026-08-28 | Echo handling reworked: the master no longer assumes the transceiver loops the transmission back. It captures the raw stream per step and detects the echo by comparing with the request, which works whether or not the echo is on the bus. Hardware showed it is absent in Direct Control Mode, which is why every response was being discarded. |
| 2026-08-28 | Response length corrected to the bytes the LSM actually sends, `nRegs * 2 + 2`, without the request header. The sync check on `80 A5`, which only ever belonged to the request, was replaced by a length classification: an exact match counts as good, a shorter answer as truncated, an empty one as a timeout and a longer one as a framing slip. |
| 2026-08-28 | Cyclic table taken from the last complete period of the trace instead of the first, so it carries the settled keep-alive cadence. All four keep-alive gaps are now 6168 to 6222 us and the super-cycle is 33.5 ms. |
| 2026-08-28 | Three receive-path defects fixed in `bridge_relay_pump()`. The RX fill-level flags are cleared before the drain rather than after, so a byte arriving during the drain keeps its interrupt. An overflow now recovers only the channel that overflowed, instead of discarding the other one's FIFO while a response is still arriving on it. And the pump is also called from the CPU2 loop, so a lost interrupt cannot strand a burst until the FIFO overflows. |
| 2026-08-28 | The ECU channel is isolated while the bridge is inactive: its RX service request is disabled, its FIFO drained and its flags cleared, and the pump skips it entirely. Nothing drives that segment in Direct Control Mode, so a floating pin can no longer spend CPU2 time ahead of the LSM channel. |
| 2026-08-28 | The inter-frame gap is anchored to the later of the last byte seen and the time the transaction should have ended on the wire. Previously a lost response moved the anchor about 200 us earlier, so the replay ran progressively faster exactly when reception was worst, and the keep-alive cadence tracked the receive quality instead of the trace. |
| 2026-08-31 | Correlated Saleae sessions captured LVDS and CAN-UART together in both modes. On the wire the CAN-UART replay proved correct: 0.6 us turnaround, every read burst matching `4 + nRegs*2 + 2`, no parity or framing errors and payloads identical to the ECU. The earlier response telemetry had been misleading, so the search moved to the video path. |
| 2026-08-31 | Root cause localised through the LSM status block. Register 0x0E is FWC, the Frame Watchdog Counter, and 0x0F is FWCT, its threshold at 120. The datasheet states FWC is incremented by 2 on a frame error and decremented by 1 on a clean frame, and the register latches the maximum reached. FWC rose by exactly 2 per frame at 100 Hz and never fell, so every frame the AURIX sent was rejected. It reached 120 about 1.2 s after the device entered run state, which matches `MASTER_STATE` moving from 0x0003 to 0x0005. |
| 2026-09-01 | Two defects found in the generated start-up table by comparing against the ECU bus. The ECU repeats the `W 0x0001` write nine times, not eight, and pauses 650 ms before the configuration phase; that pause had been clipped to 65.5 ms by the `uint16 gapUs` field. The field is now `uint32`, the ninth write was restored and the generator script no longer truncates long gaps. |
| 2026-09-01 | LVDS frame integrity fixed. 79 of 149 transmitted frames were 25604 bytes instead of 25608, verified as a clean four-byte deletion near the start of the frame. The ASCLIN1 transmit FIFO refill threshold was `TxFifoInterruptLevel_8` on a 16-entry FIFO with an 8-byte DMA block, leaving no headroom, so a block could be written into a FIFO that could not hold it and the surplus was dropped. The threshold is now 4. |
| 2026-09-02 | Start order aligned with the ECU. The ECU begins the video stream about 25 ms after the LSM reaches run state, while Direct Control Mode was transmitting 1.11 s before the device was configured. The generator is now armed with source `IDLE` and `direct_mode_tick()` releases the stream only once `can_uart_master_startup_done()` reports the sequence complete. |
| 2026-09-02 | Final root cause: the OSRAM pixel line is 8O2, not 8O1. The ECU byte period is 600 ns at 20 Mbaud, one bit more than 8O1, so the LSM was reporting `Wrong Stop Bit` and discarding every frame even after the CRC and length were correct. `asclin1_tx_configure()` now selects `IfxAsclin_StopBit_2` for OSRAM; Nichia stays at one stop bit pending its own measurement. |
| 2026-09-03 | Validated on hardware. Byte period 600.0 ns and frame duration 15.3659 ms against the ECU reference of 15.3651 ms, all frames 25608 bytes with a valid CRC, `MASTER_STATE` stable at 0x0003 and FWC flat after the truncated frame at the start of the recording. The LSM stays lit in Direct Control Mode. |
| 2026-09-03 | Direct Control CAN-UART Record validated. Fix6 published every locally generated request and LSM response through the existing `CD` Ethernet path: 274 of 274 complete `HWSTAT W` blocks contained exactly seven reads (100.00%), compared with 181 of 271 blocks (66.79%) in the Fix4 debug trace. The corresponding Watch capture showed `responseTimeouts=0`, `shortResponses=0`, `tailBytes=0`, `outRingDrops=0` and `queueOverruns=0`. |
| 2026-09-04 | Implemented trace-driven OSRAM start-up replay. The C# UI extracts and validates the complete 1291-step start-up sequence from the loaded `.rply` trace, rejects NormalRun-only traces such as Fix6, and uploads the steps to AURIX through Ethernet commands `0x08` and `0x09`. AURIX waits for the committed upload during PC-driven Direct Control, then runs the uploaded start-up once before returning to the built-in cyclic sequence; the firmware default start-up remains available for future standalone TFT operation. |
| 2026-09-10 | Added Nichia Direct Control support: 256x64 row framing with parity and CRC-16, 12.5 Mbaud 8N1 transmission with row pacing, device-aware Ethernet geometry and `NI` fragments, Nichia CAN-UART startup/cycle tables, dedicated upload commands, and runtime defect-response filtering. Nichia hardware validation remains open. |
