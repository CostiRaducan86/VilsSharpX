# VilsSharpX - Technical Architecture Review

**Date**: February 13, 2026  
**Project**: VilsSharpX - AVTP/LVDS Video Frame Monitoring & Comparison Tool  
**Platform**: WPF (.NET 8 Windows)  
**Status**: Stable Baseline (UI Layout Optimized)

---

## Executive Summary

VilsSharpX is a dual-mode **8-bit grayscale frame visualization** application designed for automotive lighting module testing. It seamlessly switches between **live Ethernet AVTP capture** and **file-based playback** (PGM, AVI, PCAP, Scene, Sequence), displaying frame comparison (LVDS vs AVTP) with real-time diff rendering and performance metrics.

**Key Achievement**: Recent UI redesign spatially separates concerns (60% left pane for main video + controls, 40% right pane for monitoring), improving ergonomics and enabling future CAN/UART integration.

---

## 1. System Overview

### 1.1 Functional Scope

- **Dual Input Sources**:
  - Live Ethernet AVTP packet capture via SharpPcap (protocol: RVF, ethertype 0x22F0)
  - File-based playback (PGM, AVI, PCAP records, procedural scenes/sequences)
  
- **Processing Core**:
  - Packet-to-frame reassembly (line-based, 320×80 Gray8)
  - Real-time pixel delta computation (A − B threshold)
  - Zoom/pan with affine transforms
  - Dark pixel compensation, comparison mode inversion
  
- **Visualization**:
  - 3-pane layout: A (AVTP), B (LVDS fallback), D (Diff/BGR24)
  - Performant WriteableBitmap rendering + overlay (FPS, pixel stats)
  - Status monitoring (frame rate, frame info, AVTP diagnostics)
  
- **Control & TX**:
  - Configurable AVTP packet generation (MAC, VLAN, EtherType, Stream ID)
  - Multi-device type support (Osram, Nichia, with ECU variants)
  - Network Interface selection and monitoring
  
- **Export/Logging**:
  - AVI recording, PNG snapshots, Excel reports
  - File-based diagnostics (diagnostic.log, crash.log)

### 1.2 Resolution / Format Constraints

- **Frame Dimensions**: 320×80 (active), 320×84 (LVDS with 4 metadata rows cropped)
- **Pixel Format**:
  - Source (A, B): Gray8 (8-bit grayscale)
  - Output (D/DIFF): BGR24 (24-bit color for delta visualization)
- **Protocol**: RVF 1-based line numbering, sequential reassembly

---

## 2. Architectural Layers

### 2.1 Data Input Layer

**Responsibility**: Frame source acquisition

**Components**:

- **AvtpLiveCapture** (Ethernet)
  - Background thread running `Task.Run(...)`
  - Uses SharpPcap for NIC-level packet sniffing
  - Filters ethertype 0x22F0 (AVTP)
  - Invokes `AvtpRvfParser.TryParseAvtpRvfEthernet()` on each frame
  - Emits `OnFrameReady(Frame, metadata)` on completion

- **File-Based Loaders** (Disk)
  - `PgmLoader`: Simple "skip 4 newlines" heuristic (not fully robust)
  - `AviSourcePlayer`: Frame extraction from AVI containers
  - `SequencePlayer`: Multi-frame sequence playback
  - `ScenePlayer`: Procedurally-generated frame sequences
  - Delegated to `SourceLoaderHelper` for unified interface

**Concurrency Pattern**: Background loop + `CancellationTokenSource` termination

---

### 2.2 Network & Protocol Layer

**Responsibility**: AVTP packet parsing, RVF protocol semantics

**Components**:

- **AvtpRvfParser**
  - Stateless parser: `TryParseAvtpRvfEthernet(byte[] packet) → RvfChunk?`
  - Extracts payload from Ethernet frame
  - Decodes RVF line index, chunk size, pixel data
  - Validates protocol fields

- **RvfReassembler**
  - **State Machine**: Accumulates RVF chunks into complete frames
  - **Line-based Buffering**: Maps 1-based line numbers → pixel rows
  - **Gap Tracking**: Detects missing sequences, emits copy on incomplete frames
  - **Output**: `OnFrameReady()` event with complete `Frame` buffer
  - **Safety**: Input validation (bounds-checking on chunk size, line numbers within [1, H])

**Protocol Assumptions**:

- Ethertype: `0x22F0`
- Resolution: Hardcoded 320×80 (per device type, configurable via LSM Device dropdown)
- Chunk Payload: `numLines × width` bytes (Gray8)
- Frame Completion: All lines 1..H received (with tolerance for out-of-order arrival)

---

### 2.3 Frame Processing Layer

**Responsibility**: Frame buffer management, pixel-level computation

**Components**:

- **Frame** (Data Container)
  - Dual Gray8 buffers: `_pixelsA[320×80]`, `_pixelsB[320×80]`
  - Single BGR24 buffer: `_pixelsDiff[320×80×3]`
  - Provides `Byte property` accessor for current-selected pane
  - Clone semantics (safe for concurrent access with `_frameLock`)

- **DiffRenderer**
  - Computes delta: `D[i] = Clamp(|A[i] − B[i]| − threshold)`
  - Applies dark pixel compensation: ±15% neighbor boost (N/S/E/W), ±10% diagonal, ±5% 2-step
  - Inversion mode: `(0,0) → white`, `(255,255) → black` (for visual contrast)
  - Output: BGR24 with color mapping (grayscale or threshold-based).

- **PlaybackStateManager**
  - **FPS Estimation**: Exponential Moving Average (α = 0.30, window = 0.25s)
  - **Timing**: Frame rate scheduler for playback from files
  - **Live Signal Loss**: Detects ~625ms inactivity → flags "Signal not available"
  - **State**: Playing, Paused, Stopped

- **ZoomPanManager**
  - 2D affine transforms (scale + pan offset)
  - Clip/render-bounds computation
  - Transforms applied before WriteableBitmap rendering

---

### 2.4 Rendering & UI Update Layer

**Responsibility**: Pixel-to-screen display, thread marshaling

**Components**:

- **WriteableBitmap** (Direct Pixel API)
  - Format: `PixelFormats.Gray8` (A, B), `PixelFormats.Bgr24` (D)
  - Dimensions: 320×80 (device-configurable)
  - Lock-free pixel writes: `WritePixels(Int32Rect, array, stride, offset)`

- **Blit() Function** (MainWindow.xaml.cs)
  - Core rendering routine: copies processed frame to WriteableBitmap
  - Applies zoom/pan transforms
  - Invoked via `Dispatcher.Invoke()` from background thread
  - Thread-safe: UI thread ownership of bitmap

- **OverlayRenderer**
  - Composites performance metrics (FPS, frame count, timestamp)
  - Renders pixel inspector data on hover
  - Uses Canvas overlay for non-destructive annotation

- **Concurrency Model**:

  ```text
  [Background Thread]
    Frame computation → Blit() scheduled
    ↓ (Dispatcher.Invoke)
  [UI Thread]
    WritePixels() → ImgA/ImgB/ImgD refresh → WPF render pipeline
  ```

---

### 2.5 User Interface Layer

#### Left Pane (60%)

**3 Frame Viewers**:

- **A (Top, full width)**: AVTP Source (Gray8)
- **B (Bottom-left)**: LVDS Source (Gray8)
- **D (Bottom-right)**: Diff/Comparison (BGR24)

**Control Hierarchy** (optimized layout, recent redesign):

1. **Playback Controls**:
   - Load Files, Start, Prev, Next, Record, Stop, Save
   - Loop Playback checkbox (AVI/PCAP mode only)
   - Save feedback label

2. **Configuration Groups** (proportions: 50% / 20% / 30%):

   **Hardware Configuration (50%)**:
   - Mode of Operation: AVTP Live / Generator/Player (Files)
   - LSM Device Type: Osram 2.0, Osram 2.05, Nichia
   - ECU Variant: 15 options (MB PLU-HD, CHLC, GM MLD, MB PLU, MB HLI)

   **Application Settings (20%)**:
   - FPS: playback frame rate
   - Deviation value: B-delta offset
   - Deviation threshold: Diff pixel threshold
   - Force dead pixel ID: simulate pixel failure
   - Dark pixel compensation: enable/disable
   - Comparison option: (0,0→white, 255,255→black) inversion mode

   **Ethernet Configuration (30%)**:
   - NIC: dropdown + refresh button
   - Source MAC, Destination MAC: TX packet addresses
   - VLAN ID, VLAN Priority: 802.1Q fields
   - AVTP EtherType: hex value (default 0x22F0)
   - Stream ID byte: last byte of AVTP stream ID

#### Right Pane (40%)

**Two Monitoring Panels**:

1. **CAN/UART Communication** (Top, spans ~50% of right pane height):
   - Placeholder ListView with columns: Time, Nr, Name, Address, R/W, Value
   - Status text: "Visual area prepared for CAN/UART monitoring. Functional implementation will be added later."
   - Future: Integrate CANdb++, UART HW module data

2. **AVTP Status** (Bottom, remaining height):
   - `LblStatus`: Main status text (ready/no signal/loading)
   - `LblDiffStats`: Frame count, frame info
   - Hidden AVTP metrics (fps in, dropped packets) for future UI

---

### 2.6 Support & Utility Layer

**PixelInspector**:

- Hover callback: tracks mouse position, reads pixel value at coordinates
- Overlay display: shows `(X, Y) = value` annotation

**UiSettingsManager**:

- Persistence: saves/loads UI state (last mode, device type, control values)
- Flag: `IsLoading` to suppress event handlers during startup

**FrameSnapshotSaver**:

- Exports current frame to PNG + metadata Excel (`frameSnapshots/` folder)
- Contains: timestamp, frame count, device type, diff threshold

**RecordingManager**:

- Encodes live frame stream to AVI (SharpAvi library)
- Frame rate: configurable (100 fps default)
- Output: `videoRecords/` folder

**DiagnosticLogger**:

- File append-only: `diagnostic.log`, `crash.log`
- Captures: AVTP frame activity, timing stats, error traces
- No console (WinExe), all output to files

**AvtpTransmitManager** (TX Path):

- Builds AVTP/RVF packets: Ethernet → VLAN → RVF payload
- MAC addresses, EtherType, Stream ID from UI controls
- Sends via NIC (requires admin privileges for raw packet injection)

**LiveNicSelector** & **NetworkInterfaceUtils**:

- Enumerates network interfaces
- Status monitoring (up/down)
- Refresh on demand

---

## 3. Data Flow Diagrams

### 3.1 Live Capture (AVTP) Data Flow

```text
Ethernet NIC
  ├─ Packet sniff (SharpPcap, ethertype 0x22F0)
  │
AvtpLiveCapture (background thread)
  ├─ Dequeue packets
  │
AvtpRvfParser::TryParseAvtpRvfEthernet()
  ├─ Extract payload
  ├─ Validate RVF header
  │
RvfReassembler::Push(RvfChunk)
  ├─ Accumulate line-by-line
  ├─ Detect frame completion
  │
OnFrameReady(Frame, metadata) event
  │
MainWindow_AvtpFrameReady()
  ├─ Update Frame buffer
  ├─ Schedule Blit() on UI thread
  │
[UI Thread] Blit() & WritePixels()
  │
ImgA/ImgB/ImgD WriteableBitmap refresh
  │
WPF Render Pipeline → Screen Display
```

### 3.2 File Playback Data Flow

```text
File picker → Load file (PGM/AVI/PCAP/Scene)
  │
SourceLoaderHelper
  ├─ Route to appropriate loader
  ├─ PgmLoader.Load() → byte[]
  ├─ AviSourcePlayer.GetNextFrame() → byte[]
  ├─ SequencePlayer.NextFrame() → byte[]
  ├─ ScenePlayer.Render() → byte[]
  │
PlaybackStateManager
  ├─ Schedule frame based on FPS
  ├─ Callback: NextFrameCallback(byte[] pixels)
  │
Frame buffer update (A or B)
  │
[Automatic] DiffRenderer
  ├─ Compute D = |A − B|
  │
Schedule Blit() on UI thread (same as Live)
  │
Screen Display
```

### 3.3 AVTP TX Data Flow

```text
User input:
  ├─ Src/Dst MAC (TxtSrcMac, TxtDstMac)
  ├─ VLAN ID/Priority (TxtVlanId, TxtVlanPriority)
  ├─ EtherType, Stream ID
  │
AvtpTransmitManager::BuildAvtpFrame()
  ├─ Assemble Ethernet header + VLAN tag
  ├─ Append RVF payload
  ├─ Compute checksums
  │
Send via selected NIC (raw socket, requires admin)
  │
Ethernet → Receiver (DUT, test bench)
```

---

## 4. Concurrency & Synchronization

### 4.1 Threading Model

- **Background Thread** (Live Capture):
  - Runs `Task.Run(async () => { AvtpLiveCapture.Run(token); })`
  - Blocked on NIC receive (SharpPcap event-driven)
  - Controlled by `CancellationTokenSource`

- **UI Thread** (Main WPF):
  - WPF dispatcher thread (STA)
  - Handles all UI updates via `Dispatcher.Invoke()`

- **Thread Interaction**:

  ```csharp
  // Background thread
  await Task.Run(() => {
      Frame frame = _liveCapture.GetNextFrame();
      // Schedule UI update
      Dispatcher.Invoke(() => {
          _latestA = frame;
          Blit();
      });
  });
  ```

### 4.2 Synchronization Primitives

- **_frameLock** (object): Protects Frame buffer access

  ```csharp
  lock (_frameLock) {
      _latestA = newFrame;
      _latestD = diffFrame;
  }
  ```

- **Volatile Fields**:

  - `_loopPlayingEnabled`: User toggle
  - `_bValueDelta`: Live adjustment (B offset)
  - `_diffThreshold`: Threshold slider input

- **Events** (not locks):

  - `OnFrameReady(Frame, metadata)`: Async frame notification
  - BackgroundWorker pattern deprecated in favor of async/await

### 4.3 Race Condition Mitigation

- **Frame Buffer Cloning**: `Frame` constructor copies pixel arrays
  - Eliminates shared-buffer issues
  - Trade-off: Copy overhead (~28 KB per frame @ 320×80)
  
- **Dispatcher Marshaling**: All WPF control updates via `Dispatcher.Invoke()`
  - No cross-thread UI access
  
- **Settings Synchronization**:
  - UI controls → internal fields → processing pipeline
  - `IsLoading` flag suppresses event handlers during init

---

## 5. Key Design Patterns

### 5.1 Separation of Concerns

- **Network**: AvtpLiveCapture, AvtpRvfParser (no UI dependency)
- **Processing**: DiffRenderer, ZoomPanManager, PlaybackStateManager (stateless/pure)
- **Rendering**: WriteableBitmap, Blit() (high-speed pixel ops)
- **UI**: MainWindow (presentation + event marshaling only)

### 5.2 Dual-Mode Architecture

- **Abstraction**: `IFrameSource` interface (implicit, not explicit)
  - Live: AvtpLiveCapture (push model, events)
  - File: PlaybackStateManager (pull model, callbacks)
- **Benefit**: Seamless switching, shared rendering pipeline

### 5.3 Manager Pattern

- **Delegation**: MainWindow delegates to specialized managers:
  - `PlaybackStateManager`: Timing, FPS
  - `LiveCaptureManager`: AVTP sniffing
  - `RecordingManager`: AVI output
  - `AvtpTransmitManager`: TX packet assembly
  - `UiSettingsManager`: Persistence
- **Motivation**: Single Responsibility, testability

### 5.4 Observable Pattern (WPF Binding - Future)

- Currently: Direct control event handlers (TextChanged, SelectionChanged)
- Opportunity: Migrate to MVVM (ViewModel, INotifyPropertyChanged) for better testability

---

## 6. Performance Characteristics

### 6.1 Frame Rate

- **Target**: 100 fps (configurable via TxtFps)
- **Measurement**: EMA-based (α=0.30, window=0.25s)
- **Bottlenecks**:
  - WritePixels() bandwidth: ~24 MB/s @ 100fps (320×80×3 bytes)
  - DiffRenderer pixel loop: O(W×H) per frame → 25,600 operations
  - Zoom/pan transform: affine math overhead (negligible if not per-pixel)

### 6.2 Latency

- **Live Capture Path**: Ethernet → Parse → Reassemble → Blit → Screen
  - ~10–30 ms typical (depends on OS scheduler, display refresh rate)
  
- **File Playback**: Load → PlaybackStateManager scheduling → Blit
  - ~5–10 ms (no I/O blocking if file cached)

### 6.3 Memory

- **Frame Buffers**: 3× (320×80 Gray8) + 1× (320×80 BGR24) ≈ 128 KB
- **UI Controls**: ~50 KB (XAML objects, TextBox, ComboBox)
- **Total Runtime**: ~20–30 MB (including .NET Framework overhead)

---

## 7. Protocol & Constraints

### 7.1 AVTP/RVF Protocol

- **Ethertype**: `0x22F0` (proprietary)
- **Payload Structure**:

  ```text
  [Ethernet Header] [VLAN Tag (optional)] [RVF Header] [Chunk Data]
  ```

  - RVF Header: sequence number, frame ID, line index, chunk size
  - Chunk Data: `numLines × width` bytes (Gray8)

- **Reassembly**:

  - Line 1-based indexing
  - Out-of-order tolerance (frame completed when all lines received)
  - Gap detection: missing line → frame copy + emit

### 7.2 Resolution Constraints

- **Fixed Resolution**: 320×80 (active) or 320×84 (LVDS with metadata)
- **Hardcoded** in `RvfProtocol.W/H`, enforced by RvfReassembler
- **Future**: Device type dropdown allows config, but current binary uses single resolution

### 7.3 Pixel Format Guarantees

- **Input (A, B)**: Always Gray8 (1 byte per pixel)
- **Output (D)**: Always BGR24 (3 bytes per pixel, color space)
- **Conversion**: DiffRenderer handles Gray8 → BGR24 mapping

---

## 8. Current Strengths

✅ **Clean Separation**: Network, Protocol, Rendering, UI layers are isolated  
✅ **Dual-Mode Input**: Seamless switching between live and playback  
✅ **Responsive FPS**: EMA-based estimation + live signal loss detection  
✅ **Fast Rendering**: WriteableBitmap + GPU-accelerated at OS level  
✅ **Modular Managers**: Each concern (playback, recording, capture) has dedicated class  
✅ **File Diagnostics**: No console; diagnostic.log + crash.log capture all events  
✅ **Recent UI Improvements**: 60/40 layout split, optimized control proportions  

---

## 9. Improvement Opportunities

⚠️ **CAN/UART Monitor** (Placeholder)  

- Currently: Empty ListView template
- Next: Integrate CAN database decoder, UART HW module interface
- Impact: Enable real-time system diagnostics alongside video

⚠️ **PGM Loader** (Fragile)  

- Current: "Skip 4 newlines" heuristic (not robust for all PGM variants)
- Improvement: Full P2/P5 parser with error handling
- Impact: Support broader image library, better error reporting

⚠️ **No Unit Tests**  

- Validation currently: manual runtime observation
- Opportunity: Add integration tests for:  
  - RvfReassembler (line sequencing, gap detection)
  - DiffRenderer (threshold logic, compensation)
  - Playback timing (FPS estimation)
- Impact: Regression prevention, easier refactoring

⚠️ **AVTP TX Simplicity**  

- Current: Basic packet builder (no AVB/TSN features)
- Future: Add scheduling, bandwidth reservation, audio sync
- Impact: Automotive-grade compliance (SOME/IP, TSN integration)

⚠️ **Transform State Exposure**  

- Zoom/pan currently: private to ZoomPanManager
- Opportunity: Export transform matrix for plugins/external visualization
- Impact: Extensibility for research/analysis tools

⚠️ **Settings Persistence**  

- Currently: Partial (UiSettingsManager)
- Gap: No persistent window geometry, no workspace layouts
- Improvement: Full app state save/restore

---

## 10. Recommendations

### Short Term (1–2 weeks)

1. **Document CAN/UART Integration**: Define API/interface for future HW modules
2. **Enhance Error Handling**: Add try-catch blocks in DiffRenderer, RvfReassembler
3. **Performance Profiling**: Measure CPU/memory under sustained 100fps load

### Medium Term (1–3 months)

1. **Unit Test Suite**: Target PlaybackStateManager, DiffRenderer, RvfReassembler
2. **PGM Loader Hardening**: Robust P2/P5 parser with validation
3. **MVVM Migration** (Phase 1): MainWindow → ViewModel layer (properties, commands)

### Long Term (3–6 months)

1. **CAN/UART Implementation**: Full monitor integration with CANdb++
2. **Plugin Architecture**: Expose transform matrix, frame hooks for custom renderers
3. **AVB/TSN Support**: Upgrade AVTP TX for automotive networking standards

---

## 11. Conclusion

VilsSharpX demonstrates **solid architectural foundations** with clean separation of concerns, responsive rendering, and robust dual-mode operation. The recent UI redesign (60/40 split, optimized control layout) significantly improves usability and extensibility.

**Key strengths** lie in protocol isolation, async/await patterns, and modular manager design. **Improvement opportunities** are clearly identified (CAN/UART, testing, PGM robustness), and the roadmap is actionable.

The system is well-positioned for **near-term feature expansion** (CAN/UART full impl) and **longer-term evolution** (AVB/TSN, plugin architecture) while maintaining code stability.

---

**Document Version**: 1.0  
**Last Updated**: February 13, 2026  
**Next Review**: Recommended Q1 2026 (post CAN/UART MVP)
