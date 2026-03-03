# Step 1 Build & Test Guide

## Overview

You now have a **DMA + dual-buffer implementation** for ASCLIN9 LVDS acquisition on the Aurix TC397. This guide walks through validation.

---

## Step 1a: Build the Firmware

### Option A: Eclipse CDT (Recommended for Infineon targets)

1. **Import project into Aurix Development Studio (ADS) or Eclipse CDT:**
   ```powershell
   # If using VS Code + Makefile:
   cd C:\_GitProj\AE\VS_Code_AE_Workspace\VilsSharpX\VilsSharpX\Aurix_Firmware
   make clean
   make all
   ```

2. **Expected output:**
   ```
   Building: Cpu0_Main.c
   Building: asclin9_dma.c
   Building: rxmon.c
   ...
   [LD] main.elf → main.elf (size X bytes)
   [HexTool] → main.hex
   ```

3. **Potential errors & fixes:**
   | Error | Cause | Fix |
   |---|---|---|
   | `asclin9_dma.h: No such file` | Include path issue | Check `.cproject` for `Aurix_Firmware` in include path |
   | `IfxDma.h not found` | iLLD library not linked | Verify iLLD configured in project |
   | `undefined reference to 'IfxDma_DmaChannel_init'` | Library not compiled | Ensure iLLD DMA module is built & linked |

### Option B: Manual GCC + Make

```bash
# If you have arm-none-eabi-gcc installed:
arm-none-eabi-gcc -mcpu=tc37x -mdsp=tc37x \
  -I./Libraries/iLLD -I./Configurations \
  -c Cpu0_Main.c -o Cpu0_Main.o
arm-none-eabi-gcc -mcpu=tc37x -mdsp=tc37x \
  -I./Libraries/iLLD -I./Configurations \
  -c asclin9_dma.c -o asclin9_dma.o
# ... link all .o files
```

---

## Step 1b: Flash & Run

### Flash UF2 (Pico method, if applicable)

If using picotool (Raspberry Pi method):
```powershell
# Windows
picotool load -x main.elf
```

### Flash via JTAG (Standard Aurix method)

1. **Connect debugger** to the TC397 kit (usually via Segger J-Link or similar)
2. **Open Debug Configuration** in ADS:
   - Right-click project → Debug As → Embedded C/C++ Application
   - Or use `pyocd`, `j-link gdb-server`, etc.

3. **Expected output in Console:**
   ```
   Breakpoint 0 at core0_main (0x...)
   (gdb) continue
   [CPU0] Running...
   ```

---

## Step 1c: Validation

### Watch Variables (Debugger)

In **Debug View → Variables Tab**, add these watches:

| Variable | Expected | What it means |
|---|---|---|
| `g_asclin9_dma.completionCount` | Increments every 1.6 ms | DMA ISR is firing correctly |
| `g_rxmon.framesOk` | Increments (48 per second at 48 FPS) | Parser is accepting frames |
| `g_rxmon.framesCrcBad` | **0** | No corrupted frames (good signal!) |
| `g_rxmon.totalBytes` | Growing | Data is flowing through parser |
| `g_asclin9_dma.pCompletedBuffer` | Sometimes non-NULL | Buffers are being produced & consumed |

### Run a ~5 second capture

1. **Let firmware run** (continuous streaming):
   ```
   g_rxmon.framesOk should reach ≈ 48 × 5 = 240
   g_rxmon.completionCount should reach ≈ (1.6ms interval) / 5s ≈ 3,125 completions
   g_rxmon.framesCrcBad should remain 0
   ```

2. **Check diagnostic telemetry** (via serial console if available):
   ```c
   // If you add this to main loop:
   if (g_asclin9_dma.completionCount % 500 == 0) {
       printf("Frames: %u, Errors: %u, DMA Completions: %u\n",
              g_rxmon.framesOk,
              g_rxmon.framesCrcBad,
              g_asclin9_dma.completionCount);
   }
   ```

### Common Issues & Diagnostics

| Symptom | Root Cause | Action |
|---|---|---|
| `completionCount` stuck at 0 | DMA not running | → Check ISR priority (13), SRC enable, DMA channel config |
| `framesOk` not incrementing | Parser failing | → Check `g_rxmon.falseStarts`, `rowParityErrors` |
| `framesCrcBad` > 0 | Signal integrity issue | → Check pin 14.7 wiring, termination, EMI |
| `pCompletedBuffer` always NULL | Main loop consuming too slow | → Check if 1.6 ms budget exceeded |
| Crashes on DMA ISR | Memory corruption | → Verify buffer alignment (IFX_ALIGN(8)) |

---

## Step 2: Ethernet Protocol Design

Once Step 1 validates DMA + dual buffer acquisition:

### 2a: Define Cooked Frame Protocol

**Proposed UDP frame format:**

```c
typedef struct {
    uint32  frameNumber;           /* Incrementing frame ID */
    uint32  timestamp_us;          /* Time since boot */
    uint8   status;                /* Flags: CRC_OK, SYNC_OK, etc. */
    uint8   padding;
    uint16  reserved;
    uint8   payload[PAYLOAD_SIZE]; /* Raw Nichia lines (260×N bytes) */
} EthernetFrame_t;

// Example PAYLOAD_SIZE options:
// 260 × 10 = 2,600 bytes (one DMA buffer)
// 260 × 64 = 16,640 bytes (full frame worth @ 48 FPS)
```

### 2b: Firmware-Side Ethernet TX

**Add to Cpu0_Main.c (main loop variant):**

```c
static uint32 dma_count_at_last_tx = 0;

while (1) {
    uint8 *completed = asclin9_dma_get_completed_buffer();
    if (completed != NULL_PTR) {
        consume_dma_buffer(completed, ASCLIN9_DMA_BUFFER_SIZE);
        
        // Every 4 DMA completions (≈ 10 frames), send Ethernet packet
        if ((++dma_count_since_tx) >= 4) {
            send_ethernet_cooked_frame(completed, ASCLIN9_DMA_BUFFER_SIZE);
            dma_count_since_tx = 0;
        }
    }
    fps_update();
}
```

### 2c: Host-Side (C# Application)

**Update MainWindow.xaml.cs:**

```csharp
// Create UDP listener on port (e.g., 5555)
UdpClient udpClient = new UdpClient(5555);

// In rendering loop:
if (udpClient.Available > 0) {
    IPEndPoint remoteEP = null;
    byte[] receiveData = udpClient.Receive(ref remoteEP);
    
    // Parse cooked frame
    var cookedFrame = CookedFrame.FromBytes(receiveData);
    
    // Update pane A with latest data
    AssembleFrameAndRender(cookedFrame.payload);
}
```

---

## Recommended Validation Timeline

| Phase | Duration | Task |
|---|---|---|
| **Phase 1a** | 30 min | Build firmware, flash to TC397 |
| **Phase 1b** | 20 min | Attach debugger, verify DMA ISR firing (completionCount incrementing) |
| **Phase 1c** | 10 min | Check rxmon telemetry (framesOk ≈ 48/sec, framesCrcBad == 0) |
| **Design Review** | 30 min | Validate DMA architecture, measure CPU/latency (can be post-Phase-1) |
| **Phase 2 Plan** | 1-2 hours | Design Ethernet protocol, start firmware UDP TX implementation |
| **Phase 2 Build** | 2-3 hours | Implement firmware Ethernet, test host RX, integrate into C# app |
| **Full System Test** | 1-2 hours | End-to-end: P14.7 → DMA → Ethernet → C# UI visualization |

---

## Files Changed / Created

| File | Change | Status |
|---|---|---|
| [asclin9_dma.h](asclin9_dma.h) | **NEW:** DMA module header | ✅ Complete |
| [asclin9_dma.c](asclin9_dma.c) | **NEW:** DMA + ISR implementation | ✅ Complete |
| [Cpu0_Main.c](Cpu0_Main.c) | **MODIFIED:** Use DMA instead of interrupt-driven RX | ✅ Complete |
| [DMA_DUAL_BUFFER_DESIGN.md](DMA_DUAL_BUFFER_DESIGN.md) | **NEW:** Architecture doc | ✅ Complete |
| asclin9_rx.h / asclin9_rx.c | **DEPRECATED:** Old interrupt-driven code (keep for reference) | ⚠️ Can remove after validation |

---

## Next: Building

Ready to build? Run this in VS Code terminal:

```powershell
cd "C:\_GitProj\AE\VS_Code_AE_Workspace\VilsSharpX\VilsSharpX\Aurix_Firmware"
make clean
make all -j4
```

If you hit errors, **copy the error log** and we'll diagnose!

---

**Question for you:** Do you want me to:
1. ✅ **Help troubleshoot the build** (if compilation fails)?
2. ✅ **Add Ethernet TX to firmware** (starting Phase 2)?
3. ✅ **Create C# UDP listener skeleton** (host-side)?
4. ✅ **All of the above** (full pipeline today)?
