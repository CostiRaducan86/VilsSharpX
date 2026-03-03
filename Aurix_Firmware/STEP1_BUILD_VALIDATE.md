# Step 1 Build and Validate (DMA + Dual Buffer)

## Goal

Build the TC397 firmware with DMA receive path enabled and validate runtime behavior on hardware.

---

## Prerequisites

- Aurix Development Studio (ADS) installed
- Project imports successfully
- Active configuration: `TriCore Debug (TASKING)`

---

## Build Procedure

1. Open project in ADS
2. Clean project
3. Build project

Expected key artifacts:

- `TriCore Debug (TASKING)/VilsSharpX.elf`
- `TriCore Debug (TASKING)/VilsSharpX.hex`
- `TriCore Debug (TASKING)/VilsSharpX.map`

Quick checks (optional):

```powershell
Test-Path "Aurix_Firmware/TriCore Debug (TASKING)/VilsSharpX.elf"
Test-Path "Aurix_Firmware/TriCore Debug (TASKING)/asclin9_dma.o"
```

---

## Debug Validation Procedure

1. Start debug session
2. Let target run for at least 30-60 seconds
3. Observe DMA and parser counters

Recommended watch variables:

- `g_asclin9_dma.completionCount`
- `g_asclin9_dma.timeoutWarnings`
- `g_rxmon.framesOk`
- `g_rxmon.framesCrcBad`

---

## Runtime Expectations

| Signal | Expected |
| --- | --- |
| `completionCount` | Increases continuously |
| `timeoutWarnings` | Stays near zero in nominal load |
| `framesOk` | Increases steadily |
| `framesCrcBad` | Remains low/zero for good signal integrity |

---

## Troubleshooting

### ISR not firing

- Check DMA channel init path
- Check interrupt routing/priority
- Confirm ASCLIN RX source is active

### `completionCount` increments but parser is idle

- Verify main loop calls `asclin9_dma_get_completed_buffer()`
- Verify consumer callback path to parser

### `timeoutWarnings` keeps increasing

- Main loop cannot keep up with consume rate
- Reduce load, optimize parser path, or revisit buffer sizing

### CRC errors increase

- Check LVDS signal quality and electrical setup
- Check framing assumptions and row synchronization

---

## Pass/Fail Criteria

### Pass

- Build succeeds
- DMA completion runs stably
- Parser receives frames continuously
- No critical runtime faults

### Fail

- Build/link failure
- DMA never completes
- Parser starves despite completions
- Persistent fault conditions

---

## Next Step

If Step 1 passes, proceed to transport-layer work (Phase 2) and define Ethernet payload strategy from parser output.
