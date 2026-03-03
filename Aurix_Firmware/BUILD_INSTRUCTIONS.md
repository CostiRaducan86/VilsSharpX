# Building the Aurix TC397 Firmware (DMA + Dual Buffer)

## Project Configuration

This is an **Aurix Development Studio (Eclipse CDT)** project for the TC397 TriCore microcontroller with TASKING compiler.

**Build System:** Eclipse Managed Builder + Infineon Aurix plugins  
**Toolchain:** TASKING TriCore compiler (comes with Aurix Development Studio)  
**Target Configuration:** TriCore Debug (TASKING) configuration

---

## Build Method 1: Aurix Development Studio GUI (Recommended)

### Prerequisites

- **Aurix Development Studio** (ADS) installed (includes TASKING compiler)
  - Download: <https://www.infineon.com/cms/en/tools/aurix-development-studio/>
- Project already imported in ADS

### Steps

1. **Open Aurix Development Studio**
2. **Import or open the project:**

   ```powershell
   File → Open Projects from File System
   → C:\...\VilsSharpX\VilsSharpX\Aurix_Firmware
   ```

3. **Select Build Configuration:**

   - Right-click project → Build Configurations → Set Active → "TriCore Debug (TASKING)"

4. **Clean the project:**

   - Right-click project → Clean Project

5. **Build the project:**

   - Right-click project → Build Project
   - Or: Project → Build All (Ctrl+B)

6. **Expected Success Output:**

   ```powershell
   Building: asclin9_dma.c
   Building: asclin9_rx.c
   Building: Cpu0_Main.c
   Building: rxmon.c
   ...
   13:20:45 Build Finished
   [RESULT] VilsSharpX.elf (xxx bytes)
   [RESULT] VilsSharpX.hex
   ```

7. **Output Artifacts:**

   - **Main ELF:** `TriCore Debug (TASKING)/VilsSharpX.elf`
   - **Hex File:** `TriCore Debug (TASKING)/VilsSharpX.hex`
   - **Map:** `TriCore Debug (TASKING)/VilsSharpX.map`

### Build Output Location

```text
Aurix_Firmware/
├── TriCore Debug (TASKING)/
│   ├── VilsSharpX.elf          ← Main firmware image
│   ├── VilsSharpX.hex          ← Hex format (for programmers)
│   ├── asclin9_dma.o           ← New DMA object file
│   ├── Cpu0_Main.o
│   ├── rxmon.o
│   └── ...
```

---

## Build Method 2: Command-Line (Eclipse CDT Headless)

If you have Aurix Development Studio installed, you can build from command line:

```powershell
# On Windows (requires Eclipse CDT + TASKING installed)
# Navigate to project root
cd "C:\...\VilsSharpX\VilsSharpX"

# Use Eclipse headless builder (if installed)
"C:\Program Files (x86)\Infineon\AurixDevelopmentStudio_*\eclipse\eclipse.exe" `
  -noSplash `
  -application org.eclipse.cdt.managedbuilder.core.headlessbuild `
  -import "Aurix_Firmware" `
  -projects "VilsSharpX" `
  -build "TriCore Debug (TASKING)"
```

**Note:** This requires a full ADS installation with paths set correctly.

---

## Build Method 3: Manual TASKING Compiler (Advanced)

If you have the TASKING compiler but not the full ADS IDE:

```powershell
cd "Aurix_Firmware\TriCore Debug (TASKING)"

# Using the generated Makefile (if available)
# E.g., in Windows with TASKING GNU Make
"C:\Program Files (x86)\HighTec\gnumake.exe" -f Makefile clean
"C:\Program Files (x86)\HighTec\gnumake.exe" -f Makefile all
```

---

## Troubleshooting Build Errors

### Error: `asclin9_dma.h: No such file or directory`

- **Cause:** Eclipse hasn't indexed the new files yet
- **Fix:** Right-click project → Index → Rebuild

### Error: `IfxDma.h not found`

- **Cause:** iLLD include paths not configured
- **Fix:**
  1. Right-click project → Properties → C/C++ Build → Settings
  2. Check GCC C Compiler → Include Paths
  3. Ensure iLLD path is included (usually `Libraries/iLLD`)

### Error: `undefined reference to 'IfxDma_DmaChannel_init'`

- **Cause:** iLLD DMA library not linked, or not compiled
- **Fix:**
  1. Check `Libraries/` folder for iLLD source files
  2. Ensure iLLD is configured to build DMA module
  3. Re-build iLLD library first if needed

### Error: `VilsSharpX.elf not created`

- **Cause:** Link phase failed
- **Fix:**
  1. Clean project completely: Right-click → Clean Project
  2. Check build console for linker errors
  3. Verify Linker Script: `Lcf_Tasking_Tricore_Tc.lsl` (should be in project root)

---

## Verifying the Build

### Check Object Files Were Created

After successful build, verify new files exist:

```powershell
# Check if DMA object was compiled
Test-Path "Aurix_Firmware\TriCore Debug (TASKING)\asclin9_dma.o"
# Expected: True

# Check main ELF was linked
Test-Path "Aurix_Firmware\TriCore Debug (TASKING)\VilsSharpX.elf"
# Expected: True
```

### Check Map File for DMA Symbols

```bash
grep -i "asclin9_dma" "TriCore Debug (TASKING)/VilsSharpX.map"
# Should show something like:
# .text.asclin9_dma_init   0x80001200  0x200  asclin9_dma.o
# .text.ASCLIN9_DMA_ISR    0x80001400  0x100  asclin9_dma.o
```

---

## Next Steps After Successful Build

### Option A: Delta Testing (Verify DMA Is Actually Being Used)

1. **Use Eclipse Debugger (Recommended):**

   ```text
   Run → Debug As → Embedded C/C++ Application (TASKING)
   ```

   - Set breakpoint in `ASCLIN9_DMA_ISR`
   - Run and verify ISR fires (should hit every ~1.6 ms)

2. **Watch Variables:**

   - Add to Variables panel: `g_asclin9_dma.completionCount`
   - Observe it increment (should reach ~48/sec)

### Option B: Flash to Hardware

1. **Connect TC397 debugger** (J-Link, Segger, etc.)
2. **In Eclipse:** Run → Debug As → (TASKING configured debug target)
3. **Flash automatically and break at `main()`**

### Option C: Export ELF for External Programmer

If using a standalone programmer:

- Copy `VilsSharpX.elf` to programmer tool
- Or use `.hex` format for universal compatibility

---

## Build Configuration Details

### Clean vs. Rebuild

- **Clean Project:** Deletes all `.o` files and `.elf`

  ```text
  Right-click project → Clean Project
  ```

  - Takes ~5 seconds
  - Next build will recompile everything (Full Build)

- **Incremental Build:** Only recompiles changed files

  ```text
  Ctrl+B or Project → Build Project
  ```

  - Takes ~2-5 seconds
  - Most common during development

### File Dependencies

The Aurix build system tracks:

- **asclin9_dma.c** depends on:
  - `asclin9_dma.h` (header with constants)
  - `IfxDma.h` (from iLLD Libraries/)
  - `IfxAsclin.h` (from iLLD Libraries/)

If any `.h` changes, all depending `.c` files auto-recompile.

---

## Success Criteria

After build completes successfully, you should have:

✅ **VilsSharpX.elf** created (main firmware)  
✅ **VilsSharpX.map** shows asclin9_dma.o linked  
✅ **Compilation log** shows "0 error, 0 warning"  
✅ **asclin9_dma.o** file size > 0 (typically 5-15 KB)

---

## Reporting Build Issues

If build fails, provide:

1. **Full console output** from Eclipse Build panel
2. **Error message** (copy entire error line)
3. **Verification** that files exist:

   ```powershell
   Test-Path "Aurix_Firmware/asclin9_dma.c"          # Should be True
   Test-Path "Aurix_Firmware/asclin9_dma.h"          # Should be True
   Test-Path "Aurix_Firmware/.project"               # Should be True
   ```

4. **TASKING compiler version:**

   ```text
   In Eclipse: Help → About Aurix Development Studio
   ```

---

## Next: Hardware Validation

Once build succeeds, proceed to [STEP1_BUILD_VALIDATE.md](STEP1_BUILD_VALIDATE.md) for hardware testing!
