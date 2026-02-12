# VilsSharpX – restart-proof project brief (2026-01-16)

If you (or Copilot) come back after a VS Code restart, read this file first.

## Purpose / “line” of the project
This is a pixel-accurate inspection tool for 8-bit grayscale video frames:
- Ingest frames from AVTP/RVF (Ethernet live or PCAP replay).
- Support a lightweight “Scene” mode that loops through image files (for A/B toggling tests).
- Visualize **A**, **B**, and **DIFF** in a way that is *pixel-correct* under zoom/pan.
- Provide diagnostics (FPS, dropped/gaps).
- Record A/B/D video streams and generate an Excel-friendly compare report.
- Provide tooling for “dark pixels” (A>0 but ECU output B==0), including detection, reporting, and optional compensation.

## Non-negotiable invariants
- Resolution is **W=320**, active height **H_ACTIVE=80**.
- LVDS source is **H_LVDS=84** with **META_LINES=4** cropped.
- UI updates touching WPF controls must be on the UI thread.
- Avoid shared-buffer races: frames are cloned/snapshotted where needed.

## Data flow (high level)
1. Ethernet AVTP frame → RVF parsing (`AvtpRvfParser`) → chunk callback → reassembly (`RvfReassembler`).
2. Frame-ready event publishes a **copy** of the frame.
3. `MainWindow` updates internal `_latest*` frames and calls `RenderAll()`.
4. When paused, `_paused*` snapshots are used so overlays match the frozen image.

## Rendering / UX goals
- Rendering uses `WriteableBitmap` (`PixelFormats.Gray8`) and nearest-neighbor scaling.
- Hover/inspector is letterbox-aware; pixel_ID is **1..(W*H_ACTIVE)**.
- Zoom/pan is per-pane and unified so overlays don’t drift.

## Compare semantics (IMPORTANT)
- Deviation is **B − A** (ECU output minus input) consistently in:
  - DIFF render
  - numeric overlay
  - XLSX report
- “Deviation threshold” is the user-facing term (renamed from deadband).

## (0=0)→white mapping
- Optional visualization tweak that renders the “both zero” case as white.
- Default is **OFF**.

## Dark pixel detection
- Dark pixel definition: **A > 0 && B == 0**.
- Optional “Force dead pixel ID” (pixel_ID 1..W*H, 0 disables) simulates `B[pixel_ID]=0`.

## Dark pixel compensation
- Applied to **B** before render/record (so DIFF + recordings reflect it).
- Default is **OFF**.
- Cassandra-style kernel (boost neighbors around each dark pixel):
  - +15%: N/S/E/W at distance 1
  - +10%: diagonals at distance 1
  - +5%: N/S/E/W at distance 2

## Recording + reporting outputs
- AVI recording writes three files (A/B/D) under `docs/outputs/videoRecords`.
- Compare report is generated as `.xlsx` (ClosedXML):
  - main sheet name: `FrameNr_XX`
  - `DarkPixels` worksheet listing only dark pixels
  - dark pixel rows are strongly highlighted

## One-click XLSX (no recording)
- The **Save** button writes an XLSX report for the **currently displayed frame** (ideal workflow: Pause → Prev/Next → Save).
- Output goes to `docs/outputs/frameSnapshots` and uses the same report format/logic as the recording report.
- Save also exports PNG snapshots for all panes (1:1 pixels):
  - `<ts>_AVTP.png` (pane A)
  - `<ts>_LVDS.png` (pane B)
  - `<ts>_Compare.png` (pane D)
  - `<ts>_Compare.xlsx` (report)

## AVI playback (new)
- The unified **Load Files…** supports loading an `.avi` as an **A source**.
- The AVI reader is intentionally minimal:
  - Requires indexed AVI (`idx1` present; SharpAvi writes this when `EmitIndex1=true`).
  - Supports uncompressed video only:
    - 8bpp (Gray) and 24/32bpp (converted to Gray8)
  - Frames are cropped to top-left 320×80 for display.
- Playback uses the AVI’s own frame duration (independent of the “FPS” textbox).
- While paused, **Prev/Next** steps AVI frames and the displayed image updates immediately.
- The top-left “Running @” FPS for AVI shows an estimate of the *original source cadence* by counting
  **frame content changes per second** (useful when the AVI was recorded at fixed fps but contains repeated frames).

## Settings persistence
- Settings file: `%APPDATA%\VilsSharpX\settings.json`.
- A settings migration exists to flip old defaults:
  - Dark pixel compensation OFF
  - (0=0)→white OFF

## Logs / diagnostics
- App has no console (WinExe). Important traces go to files:
  - `diagnostic.log`
  - `crash.log`

## How to validate quickly
1. `dotnet run` and load a known PCAP.
2. Verify A/B/DIFF update, zoom/pan works, overlays stay aligned.
2b. Load a `.scene` and press Start; verify it loops between images.
3. Toggle “Record” and confirm AVI + XLSX outputs appear.
4. Use “Force dead pixel ID” to verify:
   - DIFF shows the effect
   - `DarkPixels` sheet includes the pixel
5. Enable dark pixel compensation temporarily and confirm the kernel behavior.

## Key files
- `MainWindow.xaml` – UI controls + default checkbox states.
- `MainWindow.xaml.cs` – render pipeline, compare, overlays, recording integration, dark pixel tools.
- `AviRecorder.cs` – AVI writer + XLSX report generation.
- `AppSettings.cs` – persisted settings + migration.
- `AvtpRvfParser.cs`, `RvfReassembler.cs`, `RvfProtocol.cs` – protocol and reassembly.

## Scene format (supported subset)
Preferred (minimal) format:
- Optional globals:
  - `delayMs = 500`
  - `loop = true|false` (default `true`)
- Steps can be provided as either:
  - one image path per line (absolute or relative), OR
  - `imgN = <path>` / `stepN = <path>`
- Optional per-step delays:
  - `delayMsN = <ms>` (overrides global delay for that step)

Example:
```
loop = true
delayMs = 500

img1 = 320x80_black.bmp
img2 = SLB_BL1_LeftMD_Osram_1Chip_320x84.pgm
```

Notes:
- Lines starting with `//`, `#`, `;` are ignored as comments.
- Relative paths resolve against the `.scene` file directory.
- Legacy object-style scenes (with `filename = "..."`) are still accepted for backward compatibility.
