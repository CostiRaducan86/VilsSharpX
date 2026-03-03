# Code Status - DMA Step 1

## Current State

### Implementation Complete, Awaiting Build + Hardware Validation

---

## Scope Delivered

- DMA-based ASCLIN9 RX path
- Dual-buffer ping-pong buffer model
- Main loop integration in `Cpu0_Main.c`
- Documentation package for build and validation

## Files

### New

- `asclin9_dma.h`
- `asclin9_dma.c`
- `BUILD_INSTRUCTIONS.md`
- `DMA_DUAL_BUFFER_DESIGN.md`
- `STEP1_BUILD_VALIDATE.md`
- `FILE_MANIFEST.md`
- `HANDOFF_SUMMARY.md`
- `COMPLETION_CHECKLIST.md`

### Modified

- `Cpu0_Main.c`

---

## Quality Checklist

### Compilation Safety

- [ ] No syntax errors in `*.c` / `*.h`
- [ ] iLLD include paths resolved
- [ ] DMA symbols linked correctly

### Runtime Safety

- [ ] ISR/consumer ownership is race-safe
- [ ] `timeoutWarnings` remains stable in nominal load
- [ ] Parser receives complete buffer flow

### Integration

- [ ] Existing timing/fps update path unaffected
- [ ] Main loop remains non-blocking
- [ ] Legacy module kept only as reference

---

## Design Parameters

| Parameter | Value | Note |
| --- | --- | --- |
| RX baud | 12.5 Mbaud | Nichia stream |
| Buffer size | 2560 bytes | ~10 lines batch |
| Buffers | 2 | ping-pong |
| DMA completion cadence | ~1.6 ms | at nominal baud |
| ISR priority | 13 | below ASCLIN RX/ERR |

---

## Common Build Issues

### `IfxDma.h` not found

- **Cause:** iLLD include path missing
- **Fix:** add iLLD include directories in ADS settings

### `undefined reference to IfxDma_DmaChannel_init`

- **Cause:** iLLD DMA module not linked
- **Fix:** ensure iLLD sources/libraries are compiled in current config

### `asclin9_dma.h: No such file or directory`

- **Cause:** index/build state stale
- **Fix:** rebuild project index + clean build

---

## Validation Strategy

### Pre-Hardware

1. Build with `TriCore Debug (TASKING)`
2. Confirm `VilsSharpX.elf` output
3. Check map/link symbols for DMA references

### Hardware

1. Flash firmware
2. Run debugger watch on completion counters
3. Confirm parser counters increase and CRC stays healthy

### Success Targets

- No ISR faults
- Stable completion cadence
- No sustained timeout warnings

---

## Deferred to Phase 2

- Ethernet TX packaging from DMA-fed parser output
- Host protocol adaptation and end-to-end transport validation

---

## Recommendation

Build in ADS first, then execute `STEP1_BUILD_VALIDATE.md` checklist for runtime confirmation.
