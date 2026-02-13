# VilsSharpX – Comprehensive Project Status (Last Updated: 2026-01-16)

**Read this file first after any VS Code restart or session interruption.**

---

## 1. Project Overview & Mission

VilsSharpX is a **pixel-accurate inspection tool** for 8-bit grayscale video frames in automotive ECU development:

**Core Capabilities:**

- Ingest frames from **AVTP/RVF** (Ethernet live capture or PCAP replay)
- Support **Scene mode** (loops through image files for A/B toggle testing)
- Support **AVI playback** as input source (indexed, uncompressed only)
- Visualize **A (AVTP/Generator)**, **B (LVDS)**, and **DIFF (|A−B|)** with pixel-perfect zoom/pan
- Provide diagnostics (FPS, dropped frames, gaps, sequence tracking)
- Record A/B/D video streams (AVI) and generate Excel compare reports (.xlsx)
- Detect and report **dark pixels** (A>0 but ECU output B==0)
- Optional **dark pixel compensation** (Cassandra-style kernel applied to B before render/record)
- Transmit AVTP/RVF frames over Ethernet
- One-click frame snapshot export (PNG + XLSX report)

**Target Users:** ECU validation engineers, test automation, visual regression testing

---

## 2. Current Architecture State

### 2.1 Comprehensive Documentation

Recent architecture analysis produced detailed documentation:

📄 **[ARCHITECTURE_DIAGRAM.md](tehnical_docs/ARCHITECTURE_DIAGRAM.md)**

- Mermaid block diagram with 10 subgraphs
- Color-coded layers (Ingress, Processing, Rendering, UI, Storage, Transmit)
- 3 documented data flow paths:
  1. Live AVTP → Packet Capture → Parser → Reassembler → Frame Ready → UI
  2. File Playback → Scene/AVI/PCAP → Frame Ready → UI
  3. UI → TX Manager → Packet Builder → Ethernet Send

📄 **[ARCHITECTURE_REVIEW.md](tehnical_docs/ARCHITECTURE_REVIEW.md)**

- 11-section technical review (~1200 lines, English)
- Executive summary, system overview, 8 architectural layers
- Concurrency model, design patterns, performance characteristics
- Protocol constraints (RVF/AVTP specifics)
- 8 identified strengths (separation of concerns, event-driven, WriteableBitmap efficiency, etc.)
- 6 improvement opportunities (PGM parser hardening, MVVM migration, unit tests, etc.)
- Short/Medium/Long-term recommendations

### 2.2 Architecture Layers

The application follows a layered, manager-based architecture:

```text
┌─────────────────────────────────────────────────────────────┐
│ DATA SOURCES                                                │
│  • Live Ethernet (SharpPcap)                               │
│  • PCAP files (replay)                                     │
│  • Scene files (.scene) – image sequences                  │
│  • AVI files (uncompressed, indexed)                       │
│  • PGM/BMP single images                                   │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│ NETWORK & PARSING LAYER                                    │
│  • AvtpLiveCapture – live packet sniffing (ethertype 0x22F0)│
│  • AvtpRvfParser – RVF protocol parsing                    │
│  • RvfReassembler – line-based frame reassembly            │
│  • PcapAvtpRvfReplay – PCAP playback                       │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│ FRAME PROCESSING                                           │
│  • Frame cloning (avoid shared-buffer races)               │
│  • Dark pixel detection (A>0 && B==0)                      │
│  • DarkPixelCompensation – Cassandra kernel                │
│  • DiffRenderer – compute |A−B| with threshold             │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│ RENDERING LAYER                                            │
│  • BitmapUtils.Blit() – WriteableBitmap + PixelFormats.Gray8│
│  • OverlayRenderer – numeric overlays, pixel inspector     │
│  • ZoomPanManager – per-pane transforms, letterbox-aware  │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│ UI LAYER (WPF)                                             │
│  • MainWindow.xaml – Grid layout (60% left / 40% right)    │
│  • 3 panes: Cadran A (AVTP), Cadran B (LVDS), Cadran D (DIFF)│
│  • Right panels: CAN/UART monitor (placeholder), AVTP Status│
│  • UiSettingsManager – persist settings to AppData        │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│ STORAGE & OUTPUT                                           │
│  • AviRecorder – 3-stream AVI (SharpAvi)                   │
│  • FrameSnapshotSaver – PNG + XLSX (ClosedXML)            │
│  • RecordingManager – orchestrate recording lifecycle      │
│  • DiagnosticLogger – file-based logging (no console)     │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│ TRANSMIT LAYER                                             │
│  • AvtpTransmitManager – orchestrate TX state              │
│  • AvtpPacketBuilder – construct RVF packets               │
│  • AvtpEthernetSender – send via SharpPcap                 │
└─────────────────────────────────────────────────────────────┘
```

### 2.3 Manager Classes (Separation of Concerns)

- **PlaybackStateManager** – controls Start/Stop/Pause, coordinates source switching
- **LiveCaptureManager** – owns AvtpLiveCapture, handles frame-ready events
- **RecordingManager** – toggles recording, manages AviRecorder lifecycle
- **AvtpTransmitManager** – toggles TX, manages AvtpRvfTransmitter lifecycle
- **UiSettingsManager** – load/save settings from `%APPDATA%\VilsSharpX\settings.json`
- **ZoomPanManager** – per-pane zoom/pan state, unified transforms

---

## 3. Non-Negotiable Invariants

**Resolution & Geometry:**

- Width: **W = 320 px**
- Active height: **H_ACTIVE = 80 px**
- LVDS height: **H_LVDS = 84 px** (bottom 4 lines are metadata, cropped for display)
- Frame size: **25,600 bytes** (320×80)

**Threading & Concurrency:**

- All WPF control updates must be on the UI thread (`Dispatcher.Invoke/BeginInvoke`)
- Background loops run in `Task.Run(...)` with `CancellationToken` for clean shutdown
- Frames are **cloned** on publish to avoid shared-buffer races

**Protocol Constraints:**

- AVTP ethertype: **0x22F0**
- RVF reassembly: line numbers are **1-based**, chunk payload is `numLines * width` bytes
- Frame-ready publishes a **copy** of the buffer (safe for multi-consumer)

**Rendering & UX:**

- Rendering: `WriteableBitmap`, `PixelFormats.Gray8`, nearest-neighbor scaling
- Pixel inspector: letterbox-aware, pixel_ID is **1..(W*H_ACTIVE)**
- Zoom/pan: per-pane, overlays stay aligned

---

## 4. Recent UI Improvements (2026-01-16 Session)

### 4.1 Layout Redesign

**Goal:** Add real-time monitoring panels without compromising main visualization

**Changes:**

- Grid layout changed from 2 columns to **3 columns** with proportions **3* : 3* : 4*** (60% left, 40% right)
- Row 0: **Cadran A (AVTP)** spanning cols 0-1, **CAN/UART Communication** panel (col 2, RowSpan 2)
- Row 1: **Cadran B (LVDS)** col 0, **Cadran D (DIFF)** col 1
- Row 2: Control buttons + 3 config groups (cols 0-1), **AVTP Status** panel (col 2)

**Result:** Clean visual separation, no overlap, proper alignment

### 4.2 CAN/UART Communication Panel (Right-Top)

**Purpose:** Placeholder for future CAN/UART message monitoring

**Implementation:**

- GroupBox with ListView (currently empty)
- Text translated to English: "CAN/UART Communication"
- Column headers: "Timestamp", "Message Type", "ID", "Data"
- Prepared for future population with CAN/UART frames

### 4.3 AVTP Status Panel (Right-Bottom)

**Purpose:** Real-time AVTP diagnostics

**Metrics:**

- `LblStatus` – current state (e.g., "Running @ 30 fps")
- `LblDiffStats` – diff statistics (e.g., "Diff max=42, avg=12.3")
- `LblAvtpInFps` – AVTP input FPS
- `LblAvtpDropped` – dropped frame count

**Previous Location:** Footer (Row 3, spanning all columns)  
**New Location:** Dedicated panel in col 2, row 2 (aligned with config groups)

### 4.4 Config Group Proportions

**Challenge:** 3 groups (Hardware, App Settings, Ethernet) had equal widths, causing text truncation and cramped controls

**Solution:**

- Changed from `Auto` width to **star-sizing** with proportions **5* : 2* : 3*** (50% / 20% / 30%)
- **Hardware Config** (50%): Needs wide combo (Device type, NIC selection)
- **App Settings** (20%): Small numeric inputs (FPS, threshold, pixel ID)
- **Ethernet Config** (30%): Medium-width text boxes (MAC address, Stream ID)

**Control Width Optimizations:**

- NIC ComboBox: 160px → **80px** (with HorizontalAlignment="Stretch" for responsiveness)
- MAC TextBoxes: 160px → **110px**
- Label widths increased: Hardware/App Settings **110px**, Ethernet **85px**
- Abbreviated labels: "Deviation threshold" → "Dev. threshold", "Force dead pixel ID" → "Dead pixel ID"

**Result:** All text fully visible, no truncation, proportional scaling works correctly

### 4.5 Git Commit Baseline

**Commit:** `8481453`  
**Message:** `feat(ui): Add CAN/UART and AVTP Status panels with optimized layout`

**Why:** Stable checkpoint before further development; all UI changes validated with `dotnet build`

---

## 5. Data Flow (Detailed)

### 5.1 Live AVTP Capture Flow

```text
Ethernet wire
  → AvtpLiveCapture (SharpPcap, filter ethertype 0x22F0)
    → AvtpRvfParser.TryParseAvtpRvfEthernet(pkt)
      → RvfReassembler.Push(lineNum, lineCount, payload)
        → copies lines into internal 320×80 buffer
        → on EndFrame → OnFrameReady(clonedFrame, metadata)
          → MainWindow subscribes → Dispatcher.Invoke(...)
            → updates _avtpFrame, _avtp_meta
            → RenderAll() → Blit() → WriteableBitmap update
```

**Important:** MainWindow uses `Dispatcher.Invoke` because `OnFrameReady` fires from SharpPcap's background thread.

### 5.2 PCAP Replay Flow

```text
User clicks "Load Files…" → selects .pcap
  → PcapAvtpRvfReplay.Start()
    → background loop reads packets
      → same AvtpRvfParser → RvfReassembler path
        → OnFrameReady → UI update (marshaled)
```

### 5.3 Scene Playback Flow

```text
User clicks "Load Files…" → selects .scene
  → SceneLoader.Load(path) → parses steps + delays
    → ScenePlayer.Start()
      → background loop: load each image (PGM/BMP)
        → wait delayMs
        → loop back to step 0 if loop=true
          → every image load triggers _avtpFrame update → RenderAll()
```

### 5.4 AVI Playback Flow

```text
User clicks "Load Files…" → selects .avi
  → AviUncompressedVideoReader opens AVI (requires idx1 chunk)
    → AviSourcePlayer.Start()
      → background loop: ReadFrame()
        → convert to Gray8 (if 24/32bpp)
        → crop to 320×80
        → emit frame → _avtpFrame update → RenderAll()
```

**Frame duration:** Uses AVI's inherent frame timing (independent of FPS textbox)  
**Pause behavior:** Prev/Next steps through AVI frames, UI updates immediately

### 5.5 Transmit Flow

```text
User clicks "Toggle TX"
  → AvtpTransmitManager.Start()
    → AvtpRvfTransmitter starts background loop
      → reads _latest B frame (or fallback)
        → AvtpPacketBuilder.BuildRvfPackets(frame)
          → splits into chunks (numLines per packet)
            → AvtpEthernetSender.Send(pkt) via SharpPcap
```

---

## 6. Key Features & Semantics

### 6.1 Compare/DIFF Semantics (CRITICAL)

**Deviation Definition:** **B − A** (ECU output minus input)

**Consistency Across:**

- DIFF pane rendering (|A−B| with threshold)
- Numeric overlay labels
- XLSX report columns (`PixelValue_A`, `PixelValue_B`, `Diff_B_A`)

**User-Facing Term:** "Deviation threshold" (formerly "deadband")

**Implementation:** `DiffRenderer.ComputeDiff(a, b, threshold)` → grayscale visualization where diff below threshold is black, above threshold is scaled.

### 6.2 (0=0)→White Mapping

**Purpose:** Optional visualization enhancement

**Behavior:** When both A and B are 0, render pixel as **white** (instead of black) in DIFF pane

**Default:** **OFF** (see `AppSettings` migration)

**Use Case:** Quickly identify areas where both streams are inactive vs. areas with actual signal

### 6.3 Dark Pixel Detection

**Definition:** **A > 0 && B == 0** (input has signal but ECU output is black)

**Detection Points:**

- During compare computation
- During recording (tracked per frame)
- During one-click Save

**Reporting:**

- Highlighted rows in XLSX report
- Dedicated **`DarkPixels`** worksheet with pixel_ID, coordinates, A value

### 6.4 Dark Pixel Compensation

**Purpose:** Simulate ECU correction by boosting neighbors around dark pixels

**Cassandra-Style Kernel (applied to B before render/record):**

- **+15%:** N/S/E/W neighbors at distance 1
- **+10%:** Diagonal neighbors at distance 1
- **+5%:** N/S/E/W at distance 2

**Default:** **OFF**

**Implementation:** `DarkPixelCompensation.Apply(frame, darkPixelMask)` → modifies B in-place

**Effect Visibility:** Visible in DIFF pane, recorded in AVI, reflected in XLSX report

### 6.5 Force Dead Pixel ID

**Purpose:** Simulate a specific pixel failure for testing

**Behavior:** When set to a valid pixel_ID (1..25600), forces `B[pixel_ID] = 0` before compare/render

**Default:** **0** (disabled)

**Use Case:** Validate dark pixel detection + compensation logic without needing real defective hardware

---

## 7. Recording & Reporting

### 7.1 AVI Recording

**Output Location:** `docs/outputs/videoRecords/`

**Files Generated:**

- `<timestamp>_A.avi` (AVTP/Generator stream)
- `<timestamp>_B.avi` (LVDS stream with compensation applied if enabled)
- `<timestamp>_D.avi` (DIFF stream)

**Codec:** Uncompressed (Gray8), indexed (`EmitIndex1=true`)

**Frame Rate:** User-configurable (FPS textbox in App Settings group)

**Recording Lifecycle:**

1. User clicks "Record" → `RecordingManager.StartRecording()`
2. Background loop: every frame ready → `AviRecorder.WriteFrame(a, b, d)`
3. User clicks "Stop" → `AviRecorder.Finish()` → flushes + closes files

### 7.2 Compare Report (XLSX)

**Output Location:** `docs/outputs/videoRecords/` (during recording) or `docs/outputs/frameSnapshots/` (one-click Save)

**Report Structure:**

- **Main Sheet:** `FrameNr_XX`
  - Columns: `PixelID`, `LinNr`, `ColNr`, `PixelValue_A`, `PixelValue_B`, `Diff_B_A`
  - All 25,600 pixels listed
- **DarkPixels Sheet:** Only rows where A>0 && B==0
  - Same columns as main sheet
  - Strong highlighting (yellow fill + bold font)

**Generation:** `AviRecorder.GenerateCompareReport()` using ClosedXML

### 7.3 One-Click Frame Snapshot (Save Button)

**Purpose:** Export currently displayed frame without starting full recording

**Workflow:**

1. User pauses playback
2. Navigate with Prev/Next to desired frame
3. Click **Save**
4. Generates:
   - `<timestamp>_AVTP.png` (pane A, 320×80, 1:1 pixels)
   - `<timestamp>_LVDS.png` (pane B, 320×80, 1:1 pixels)
   - `<timestamp>_Compare.png` (pane D, 320×80, 1:1 pixels)
   - `<timestamp>_Compare.xlsx` (same format as recording report)

**Output Location:** `docs/outputs/frameSnapshots/`

**Implementation:** `FrameSnapshotSaver.SaveSnapshot(a, b, d, meta)`

---

## 8. Settings Persistence

**File Location:** `%APPDATA%\VilsSharpX\settings.json`

**Managed By:** `UiSettingsManager` (singleton)

**Persisted Settings:**

- Device type (Osram_1Chip / Nichia_1Chip)
- NIC name (network interface for live capture)
- FPS (recording frame rate)
- Deviation threshold (compare sensitivity)
- Force dead pixel ID (simulation)
- Dark pixel compensation enabled (checkbox)
- (0=0)→white mapping enabled (checkbox)
- MAC address, Stream ID (Ethernet config)

**Migration Logic:**

- `AppSettings.Migrate()` ensures old defaults are updated:
  - Dark pixel compensation: **true → false**
  - (0=0)→white: **true → false**

**Load on Startup:** `UiSettingsManager.Load()` called in `MainWindow` constructor

**Save Triggers:**

- TextBox `LostFocus`
- ComboBox `SelectionChanged`
- CheckBox `Checked/Unchecked`

---

## 9. Logging & Diagnostics

**App Type:** `WinExe` (no console window)

**Log Files:**

- **`diagnostic.log`** – runtime traces for AVTP/capture/reassembly
  - Written by `DiagnosticLogger.Log(msg)`
  - Append mode, timestamped entries
  - Use for debugging packet loss, sequence gaps, frame timing

- **`crash.log`** – unhandled exceptions
  - Configured in `App.xaml.cs` (`DispatcherUnhandledException`, `UnhandledException`)
  - Captures stack trace + exception details

**Best Practice:** Always check `diagnostic.log` when investigating AVTP frame drops or reassembly issues.

---

## 10. Scene Format (Supported Subset)

**Purpose:** Loop through image files for A/B toggle testing

**File Extension:** `.scene`

**Minimal Format:**

```text
loop = true
delayMs = 500

img1 = 320x80_black.bmp
img2 = SLB_BL1_LeftMD_Osram_1Chip_320x84.pgm
img3 = HighBeam_Lane_OS.pgm
```

**Optional Per-Step Delay:**

```text
img1 = black.bmp
delayMs1 = 1000
img2 = white.bmp
delayMs2 = 200
```

**Comment Support:**

- Lines starting with `//`, `#`, `;` are ignored

**Path Resolution:**

- Relative paths resolve against the `.scene` file directory
- Absolute paths are supported

**Legacy Compatibility:**

- Old object-style scenes with `filename = "..."` still work

**Implementation:** `SceneLoader.cs` + `ScenePlayer.cs`

---

## 11. AVI Playback Implementation

**Purpose:** Load pre-recorded AVI files as input source (pane A)

**Requirements:**

- AVI must be **indexed** (`idx1` chunk present)
- Codec: **Uncompressed** only (8bpp Gray or 24/32bpp RGB converted to Gray8)

**Supported Formats:**

- 8bpp grayscale (direct copy)
- 24bpp RGB (converted to Gray8 via `Bgr24→Gray8`)
- 32bpp ARGB (converted to Gray8 via `Bgr32→Gray8`)

**Crop Behavior:**

- Frames are cropped to top-left **320×80** for display
- If AVI dimensions < 320×80, only available pixels are used

**Frame Timing:**

- Uses AVI's inherent frame duration (from `MicroSecPerFrame` in AVI header)
- Independent of the "FPS" textbox (which only affects recording)

**Pause/Step Behavior:**

- Prev/Next buttons step through AVI frames
- UI updates immediately (no delay)

**FPS Display:**

- "Running @" label shows estimated **content change FPS**
- Counts frame-to-frame differences per second (useful for detecting repeated frames)

**Implementation:** `AviUncompressedVideoReader.cs` + `AviSourcePlayer.cs`

**Known Limitations:**

- No codec support (MJPEG, H.264, etc.) – only uncompressed
- PGM loader uses simple heuristic for P5 binary format (skip 4 newlines) – may fail on malformed files

---

## 12. Key Files Reference

### 12.1 UI Layer

- **`MainWindow.xaml`** – WPF layout (Grid, 3 columns, 3 rows), control definitions, default checkbox states
- **`MainWindow.xaml.cs`** – code-behind, render pipeline, event handlers, frame processing orchestration

### 12.2 Network & Protocol

- **`AvtpLiveCapture.cs`** – SharpPcap wrapper, ethertype 0x22F0 filter
- **`AvtpRvfParser.cs`** – RVF packet parsing
- **`RvfReassembler.cs`** – line-based frame reassembly, gap tracking
- **`RvfProtocol.cs`** – constants (W, H, ethertype, etc.)
- **`PcapAvtpRvfReplay.cs`** – PCAP file playback

### 12.3 Frame Processing

- **`DiffRenderer.cs`** – compute |A−B| with threshold
- **`DarkPixelCompensation.cs`** – Cassandra kernel implementation
- **`BitmapUtils.cs`** – WriteableBitmap blitting (Gray8)
- **`ImageUtils.cs`** – image conversion utilities

### 12.4 Rendering & UI

- **`OverlayRenderer.cs`** – numeric overlays, pixel inspector
- **`ZoomPanManager.cs`** – per-pane zoom/pan state
- **`PixelInspector.cs`** – hover tooltips, pixel_ID calculations

### 12.5 Recording & Output

- **`AviRecorder.cs`** – 3-stream AVI writer + XLSX report generation
- **`FrameSnapshotSaver.cs`** – one-click Save (PNG + XLSX)
- **`RecordingManager.cs`** – recording lifecycle orchestration

### 12.6 Playback Sources

- **`SceneLoader.cs`** – parse `.scene` files
- **`ScenePlayer.cs`** – loop through scene steps
- **`AviSourcePlayer.cs`** – AVI playback orchestration
- **`AviUncompressedVideoReader.cs`** – AVI parsing (idx1 required)
- **`PgmLoader.cs`** – P2/P5 PGM file loading

### 12.7 Transmit

- **`AvtpTransmitManager.cs`** – TX lifecycle orchestration
- **`AvtpRvfTransmitter.cs`** – transmit loop
- **`AvtpPacketBuilder.cs`** – construct RVF packets from frame
- **`AvtpEthernetSender.cs`** – SharpPcap send wrapper

### 12.8 Managers & State

- **`PlaybackStateManager.cs`** – Start/Stop/Pause coordination
- **`LiveCaptureManager.cs`** – owns AvtpLiveCapture, frame-ready subscription
- **`UiSettingsManager.cs`** – settings persistence
- **`AppSettings.cs`** – settings model + migration

### 12.9 Utilities

- **`DiagnosticLogger.cs`** – file-based logging
- **`StatusFormatter.cs`** – format status strings for UI
- **`NetworkInterfaceUtils.cs`** – enumerate NICs
- **`SourceLoaderHelper.cs`** – unified file loading (PCAP/Scene/AVI/PGM/BMP)

### 12.10 Types & Enums

- **`RvfTypes.cs`** – Frame, FrameMeta, RvfChunk structures
- **`LsmDeviceType.cs`** – Osram_1Chip / Nichia_1Chip enum

---

## 13. Technical Debt & Improvement Opportunities

### 13.1 Identified Issues

1. **PGM Loader Hardening**
   - Current P5 parser uses "skip 4 newlines" heuristic
   - Fails on malformed PGM files with non-standard comments
   - **Recommendation:** Implement proper header parser that handles arbitrary comments

2. **No Unit Tests**
   - Zero test coverage for protocol parsing, reassembly, rendering
   - **Recommendation:** Add xUnit project with tests for:
     - AvtpRvfParser (valid/invalid packets)
     - RvfReassembler (gap detection, sequence tracking)
     - DiffRenderer (threshold behavior, edge cases)
     - DarkPixelCompensation (kernel correctness)

3. **MVVM Migration**
   - Current code-behind pattern mixes UI logic with business logic
   - **Recommendation:** Migrate to MVVM with ViewModels for testability + separation

4. **CAN/UART Placeholder**
   - Panel added but not functional
   - **Recommendation:** Implement CAN/UART message capture (e.g., via SocketCAN, PCAN API, or serial port)

5. **Error Handling Gaps**
   - Some paths (file I/O, network) lack try/catch
   - **Recommendation:** Add structured exception handling + user-friendly error dialogs

6. **Performance Profiling**
   - No metrics for render pipeline latency, memory usage
   - **Recommendation:** Add performance counters, profile high-frequency paths

### 13.2 Recommendations by Timeline

**Short-Term (1-2 weeks):**

- Harden PGM loader with proper comment parsing
- Add error handling for file I/O operations
- Write unit tests for AvtpRvfParser and RvfReassembler

**Medium-Term (1-2 months):**

- Implement CAN/UART functional logic
- Migrate core logic to ViewModels (MVVM)
- Add performance metrics (FPS actual vs. target, frame drop %)

**Long-Term (3+ months):**

- Full unit test suite (>80% coverage)
- Refactor to async/await where appropriate (reduce Dispatcher.Invoke overhead)
- Add codec support for AVI playback (MJPEG, H.264 via FFmpeg)

---

## 14. How to Validate Quickly

### 14.1 Basic Smoke Test

1. `dotnet build` (ensure no errors)
2. `dotnet run`
3. Load a known PCAP (`docs/inputs/AVTP_Trace_001_Osram.pcap`)
4. Verify:
   - Pane A (AVTP) updates with frames
   - Pane B (LVDS) shows fallback or loaded PGM
   - Pane D (DIFF) shows computed difference
   - Status labels update (FPS, dropped count)

### 14.2 Scene Playback Test

1. Load `docs/inputs/Black_LB_HB_LB.scene`
2. Click "Start"
3. Verify scene loops through images (500ms delay default)

### 14.3 AVI Playback Test

1. Record a short AVI (toggle "Record" → wait 5 seconds → toggle "Stop")
2. Load the generated `<timestamp>_A.avi`
3. Verify:
   - Playback starts automatically
   - Prev/Next buttons step through frames
   - "Running @" FPS displays content change rate

### 14.4 Dark Pixel Test

1. Set "Dead pixel ID" to 100 (force pixel 100 to black in B)
2. Click "Save"
3. Open generated `.xlsx` report
4. Verify:
   - Main sheet has 25,600 rows
   - `DarkPixels` sheet includes row for pixel 100
   - Row is highlighted (yellow + bold)

### 14.5 Compensation Test

1. Enable "Dark pixel compensation" checkbox
2. Force a dead pixel (e.g., pixel 100)
3. Observe DIFF pane:
   - Neighbors of pixel 100 should brighten (compensation kernel applied)

### 14.6 Zoom/Pan Test

1. Mouse wheel on pane A → verify zoom in/out
2. Left-click drag → verify pan
3. Hover over pixel → verify tooltip shows correct pixel_ID + coordinates
4. Verify overlays stay aligned during zoom/pan

---

## 15. Build & Run Commands

**Prerequisites:**

- .NET 8 SDK
- Windows OS (WPF requirement)

**Build:**

```powershell
dotnet build
```

**Run:**

```powershell
dotnet run
```

**Run with specific project file:**

```powershell
dotnet run --project VilsSharpX.csproj
```

**Clean:**

```powershell
dotnet clean
```

**Restore packages:**

```powershell
dotnet restore
```

---

## 16. Recent Session History (2026-01-16)

### 16.1 UI Layout Iteration

**Objective:** Add CAN/UART monitoring and AVTP status panels without disrupting main visualization

**Iterations:**

1. Initial 60/40 split (2 columns) → CAN/UART and Status stacked in col 1
2. Adjusted to 3 columns (3*, 3*, 4*) → separated CAN/UART (rows 0-1) and Status (row 2)
3. Optimized config group proportions from equal widths to **5* : 2* : 3***
4. Reduced control widths: NIC combo 160→80, MAC textboxes 160→110
5. Increased label widths for full text visibility (110px Hardware/App, 85px Ethernet)
6. Abbreviated long labels: "Deviation threshold" → "Dev. threshold", etc.
7. Translated CAN/UART text from Romanian to English

**Final Outcome:** Clean layout, no overlaps, all text visible, proportional scaling works correctly

### 16.2 Git Commit Baseline

**Commit:** `8481453`  
**Message:** `feat(ui): Add CAN/UART and AVTP Status panels with optimized layout`

**Purpose:** Stable checkpoint for future development; all changes validated with `dotnet build`

### 16.3 Documentation Phase

**Generated Files:**

1. `docs/tehnical_docs/ARCHITECTURE_DIAGRAM.md` – Mermaid block diagram with 10 subgraphs
2. `docs/tehnical_docs/ARCHITECTURE_REVIEW.md` – 11-section technical review (~1200 lines, English)
3. `docs/PROJECT_STATUS.md` (this file) – comprehensive project state documentation

**Purpose:** Enable session recovery, onboard new developers, maintain institutional knowledge

---

## 17. Next Steps & Continuation Plan

### 17.1 Immediate Actions

- ✅ **Commit updated PROJECT_STATUS.md** (if not done already)
- **Test UI layout** on different screen resolutions (verify responsiveness)
- **Validate documentation accuracy** (ensure all references are correct)

### 17.2 Short-Term Priorities

1. **Implement CAN/UART functional logic**
   - Choose library (SocketCAN bridge, PCAN API, serial port reader)
   - Populate ListView with real CAN/UART frames
   - Add filtering/search capabilities

2. **Harden PGM loader**
   - Replace "skip 4 newlines" heuristic with proper parser
   - Add error handling for malformed files

3. **Add basic unit tests**
   - Test AvtpRvfParser with known-good packets
   - Test RvfReassembler gap detection

### 17.3 Medium-Term Priorities

1. **MVVM migration**
   - Extract ViewModels for PlaybackState, Settings, RecordingState
   - Reduce Dispatcher.Invoke calls
   - Improve testability

2. **Performance metrics**
   - Add FPS actual vs. target counters
   - Track frame drop percentage
   - Profile render pipeline latency

3. **Error handling improvements**
   - Add try/catch for file I/O
   - User-friendly error dialogs
   - Retry logic for network operations

### 17.4 Long-Term Vision

- Full unit test suite (>80% coverage)
- Codec support for AVI (MJPEG, H.264)
- Plugin architecture for custom processing pipelines
- Web-based remote monitoring (SignalR dashboard)

---

## 18. Key Decisions & Lessons Learned

### 18.1 Layout Design Decisions

**Decision:** Use star-sizing (*) for columns instead of Auto  
**Rationale:** Proportional scaling prevents controls from overflowing/truncating at different window sizes  
**Lesson:** Always account for GroupBox padding + margins when calculating proportions

**Decision:** Separate CAN/UART and Status panels into different row spans  
**Rationale:** Prevents visual imbalance and alignment issues  
**Lesson:** RowSpan=2 for top panel allows clean separation without footer

**Decision:** Proportions 50/20/30 for config groups instead of equal widths  
**Rationale:** Reflects actual content density (Hardware has wide combos, App Settings has small textboxes)  
**Lesson:** Group width should match control requirements, not arbitrary equality

### 18.2 Architecture Decisions

**Decision:** Manager-based separation of concerns  
**Rationale:** Avoids monolithic code-behind, easier to test and maintain  
**Lesson:** Managers should own lifecycle of their resources (e.g., LiveCaptureManager owns AvtpLiveCapture)

**Decision:** Frame cloning on publish  
**Rationale:** Prevents shared-buffer race conditions between capture/render/record threads  
**Lesson:** Safety over performance for correctness-critical code (can optimize later if needed)

**Decision:** WriteableBitmap + Gray8 for rendering  
**Rationale:** Fastest path for WPF grayscale display (no conversion overhead)  
**Lesson:** Nearest-neighbor scaling + BackBufferLock for pixel-perfect zoom

### 18.3 Documentation Decisions

**Decision:** Comprehensive PROJECT_STATUS.md instead of scattered README files  
**Rationale:** Single source of truth for session recovery and onboarding  
**Lesson:** Markdown links to architecture docs provide layered detail without overwhelming readers

**Decision:** Mermaid diagram for architecture visualization  
**Rationale:** Text-based, version-controllable, renders in GitHub/VS Code  
**Lesson:** Color-coding layers makes complex diagrams easier to parse

---

## 19. Contact & Contribution

**Project Maintainer:** (Add contact info if public)

**Contributing:**

- Follow C# coding conventions (PascalCase for public members, _camelCase for private fields)
- Run `dotnet build` before committing to ensure no errors
- Update PROJECT_STATUS.md if adding major features or architectural changes
- Write unit tests for new logic (aim for >70% coverage)

**Issue Tracking:** (Add link if using GitHub Issues, Azure DevOps, etc.)

---

## 20. Appendix: External Dependencies

**NuGet Packages:**

- **SharpPcap** – packet capture (libpcap/Npcap wrapper)
- **PacketDotNet** – protocol parsing
- **SharpAvi** – AVI file writer
- **ClosedXML** – Excel file generation (.xlsx)
- **DocumentFormat.OpenXml** – Office Open XML manipulation

**System Requirements:**

- Windows 10/11 (WPF)
- .NET 8 SDK
- Npcap or WinPcap (for live capture)

**Optional:**

- Wireshark (for PCAP analysis)
- CANoe/CANalyzer (for CAN trace generation)

---

*For architecture details, see [ARCHITECTURE_DIAGRAM.md](tehnical_docs/ARCHITECTURE_DIAGRAM.md) and [ARCHITECTURE_REVIEW.md](tehnical_docs/ARCHITECTURE_REVIEW.md).*

*Last updated: 2026-01-16 after UI layout optimization and documentation phase.*
