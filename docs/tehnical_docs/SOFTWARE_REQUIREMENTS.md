# VilsSharpX - Software Requirements Specification

**Document Type**: Software Requirements Specification (SRS)

**Date**: February 13, 2026

**Version**: 1.0

**Project**: VilsSharpX - AVTP/LVDS Video Frame Monitoring & Comparison Tool

**Status**: Baseline

**Parent Document**: [SYSTEM_REQUIREMENTS.md](SYSTEM_REQUIREMENTS.md)

---

## 1. Introduction

### 1.1 Purpose

This document refines the system-level requirements into detailed software requirements for VilsSharpX implementation. It specifies functional behavior, interfaces, data structures, quality attributes, and implementation constraints.

### 1.2 Scope

This SRS covers:

- Functional requirements for each software module
- Non-functional requirements (performance, reliability, maintainability)
- Interface specifications (user, hardware, software)
- Data requirements and formats
- Design constraints and implementation guidelines

### 1.3 Document Organization

- **Section 2**: Overall Description
- **Section 3**: Functional Requirements
- **Section 4**: Non-Functional Requirements
- **Section 5**: Interface Requirements
- **Section 6**: Data Requirements
- **Section 7**: Quality Attributes
- **Section 8**: Design Constraints
- **Section 9**: Verification and Validation

### 1.4 References

- System Requirements Specification (SYSTEM_REQUIREMENTS.md)
- Architecture Review (ARCHITECTURE_REVIEW.md)
- Architecture Diagram (ARCHITECTURE_DIAGRAM.md)
- Project Status (PROJECT_STATUS.md)

---

## 2. Overall Description

### 2.1 Product Perspective

VilsSharpX is a standalone WPF application (.NET 8) for Windows that integrates with:

- **SharpPcap** library for network packet capture/transmission
- **SharpAvi** library for AVI recording
- **ClosedXML** library for Excel report generation
- **WriteableBitmapEx** library for bitmap rendering
- **Npcap** driver for packet capture (external dependency)

### 2.2 Product Functions

**Primary Functions:**

1. Live AVTP/RVF frame capture from Ethernet
2. File-based frame playback (PGM, AVI, PCAP, Scene)
3. Dual-pane visualization (A/B/DIFF)
4. Pixel-accurate diff computation
5. Zoom/pan with pixel inspector
6. Frame recording (AVI, PNG snapshots, Excel reports)
7. AVTP packet transmission
8. Dark pixel detection and compensation
9. Diagnostic logging

### 2.3 User Characteristics

**Target Users:**

- ECU validation engineers
- Test automation developers
- Visual regression testing specialists
- Automotive lighting system developers

**Assumed Knowledge:**

- Basic networking concepts (Ethernet, MAC addresses)
- Understanding of AVTP/RVF protocol (training provided)
- Familiarity with image processing concepts
- Windows desktop application usage

### 2.4 Operating Environment

**Platform**: Windows 10 (1809+) or Windows 11

**Runtime**: .NET 8.0 Windows Desktop Runtime

**Dependencies**: Npcap driver 1.70+

**Hardware**: x86-64, 4GB RAM minimum, Gigabit Ethernet adapter

---

## 3. Functional Requirements

### 3.1 Network Capture Module

**Module**: AvtpLiveCapture, LiveCaptureManager

**Traceability**: SYS-NET-001, SYS-NET-006

#### 3.1.1 Live Packet Capture

**SW-NET-001**: The AvtpLiveCapture module SHALL filter Ethernet frames with EtherType `0x22F0`.

**SW-NET-002**: The AvtpLiveCapture module SHALL execute packet capture on a background thread using `Task.Run()`.

**SW-NET-003**: The AvtpLiveCapture module SHALL support cancellation via `CancellationToken`.

**SW-NET-004**: The AvtpLiveCapture module SHALL invoke `AvtpRvfParser.TryParseAvtpRvfEthernet()` for each captured frame.

**SW-NET-005**: The AvtpLiveCapture module SHALL emit `OnFrameReady(Frame, metadata)` event upon complete frame reassembly.

**SW-NET-006**: The LiveCaptureManager module SHALL coordinate Start/Stop operations for AvtpLiveCapture.

**SW-NET-007**: The LiveCaptureManager module SHALL marshal frame-ready events to UI thread using `Dispatcher.Invoke()`.

#### 3.1.2 Network Adapter Management

**SW-NET-008**: The NetworkInterfaceUtils module SHALL enumerate available network adapters using SharpPcap API.

**SW-NET-009**: The NetworkInterfaceUtils module SHALL detect adapter connection status changes.

**SW-NET-010**: The LiveNicSelector module SHALL provide UI dropdown for adapter selection.

**SW-NET-011**: The system SHALL prevent capture start if no adapter is selected.

### 3.2 Protocol Parsing Module

**Module**: AvtpRvfParser, RvfReassembler

**Traceability**: SYS-NET-002, SYS-DATA-001, SYS-DATA-002

#### 3.2.1 AVTP/RVF Parsing

**SW-PROTO-001**: The AvtpRvfParser module SHALL extract RVF payload from Ethernet frame bytes.

**SW-PROTO-002**: The AvtpRvfParser module SHALL decode RVF header fields:

- Sequence number (uint16)
- Frame ID (uint16)
- Line index (uint8, 1-based)
- Number of lines (uint8)
- Chunk size (bytes)

**SW-PROTO-003**: The AvtpRvfParser module SHALL validate chunk size against expected dimensions (numLines × width).

**SW-PROTO-004**: The AvtpRvfParser module SHALL return `RvfChunk?` (nullable) for invalid frames.

**SW-PROTO-005**: The AvtpRvfParser module SHALL support optional VLAN tag (802.1Q) in Ethernet header.

#### 3.2.2 Frame Reassembly

**SW-PROTO-006**: The RvfReassembler module SHALL accumulate RVF chunks into a frame buffer sized by configured device resolution (320×80 or 256×64).

**SW-PROTO-007**: The RvfReassembler module SHALL map 1-based line numbers to 0-based buffer offsets.

**SW-PROTO-008**: The RvfReassembler module SHALL track missing line sequences as "gaps".

**SW-PROTO-009**: The RvfReassembler module SHALL emit frame copy when reassembly completes or gaps detected.

**SW-PROTO-010**: The RvfReassembler module SHALL validate line index range [1, H_ACTIVE] before copying data.

**SW-PROTO-011**: The RvfReassembler module SHALL reject chunks with line index + numLines exceeding configured frame height.

### 3.3 File Playback Module

**Module**: PgmLoader, AviSourcePlayer, SequencePlayer, ScenePlayer, PcapAvtpRvfReplay

**Traceability**: SYS-DATA-008, SYS-DATA-009, SYS-DATA-010, SYS-DATA-011

#### 3.3.1 PGM Image Loading

**SW-FILE-001**: The PgmLoader module SHALL support PGM P2 (ASCII) format.

**SW-FILE-002**: The PgmLoader module SHALL support PGM P5 (binary) format.

**SW-FILE-003**: The PgmLoader module SHALL parse PGM header to extract width, height, maxval.

**SW-FILE-004**: The PgmLoader module SHALL validate frame dimensions (320×80, 320×84, 256×64, or 256×68).

**SW-FILE-005**: The PgmLoader module SHALL crop 320×84 frames to 320×80 and 256×68 frames to 256×64 by removing bottom 4 lines; 320×80 and 256×64 frames are used as-is.

**SW-FILE-006**: The PgmLoader module SHALL return `Frame` object with pixel buffer.

#### 3.3.2 AVI Video Playback

**SW-FILE-007**: The AviSourcePlayer module SHALL use SharpAvi to read uncompressed AVI containers.

**SW-FILE-008**: The AviSourcePlayer module SHALL extract frames sequentially with indexing support.

**SW-FILE-009**: The AviSourcePlayer module SHALL validate AVI format (uncompressed, Gray8 or BGR24).

**SW-FILE-010**: The AviSourcePlayer module SHALL implement loop playback mode.

**SW-FILE-011**: The AviSourcePlayer module SHALL respect configured frame rate (via TxtFps control).

#### 3.3.3 PCAP Replay

**SW-FILE-012**: The PcapAvtpRvfReplay module SHALL read PCAP files using SharpPcap.

**SW-FILE-013**: The PcapAvtpRvfReplay module SHALL filter AVTP frames (EtherType 0x22F0).

**SW-FILE-014**: The PcapAvtpRvfReplay module SHALL parse RVF chunks using AvtpRvfParser.

**SW-FILE-015**: The PcapAvtpRvfReplay module SHALL reassemble frames using RvfReassembler.

**SW-FILE-016**: The PcapAvtpRvfReplay module SHALL support pause/resume during playback.

#### 3.3.4 Scene and Sequence Playback

**SW-FILE-017**: The ScenePlayer module SHALL load .scene files defining frame sequences.

**SW-FILE-018**: The ScenePlayer module SHALL parse scene file format (file paths, frame counts, transitions).

**SW-FILE-019**: The SceneLoader module SHALL load referenced image files (PGM, BMP).

**SW-FILE-020**: The SequencePlayer module SHALL play multi-frame sequences with configurable timing.

### 3.4 Frame Processing Module

**Module**: DiffRenderer, DarkPixelCompensation, OverlayRenderer

**Traceability**: SYS-PERF-002, SYS-DATA-003, SYS-DATA-004

#### 3.4.1 Diff Rendering

**SW-PROC-001**: The DiffRenderer module SHALL compute pixel-wise absolute difference between frame A and frame B.

**SW-PROC-002**: The DiffRenderer module SHALL apply a configurable diff threshold, setting differences below the threshold to zero.

**SW-PROC-003**: The DiffRenderer module SHALL convert Gray8 diff to BGR24 color map for visualization.

**SW-PROC-004**: The DiffRenderer module SHALL support configurable color schemes (red for diff, blue for inversion).

**SW-PROC-005**: The DiffRenderer module SHALL process 25,600 pixels (320×80) per frame.

#### 3.4.2 Dark Pixel Detection and Compensation

**SW-PROC-006**: The DarkPixelCompensation module SHALL detect dark pixels when the source frame has non-zero intensity and the LVDS frame is zero at the same pixel.

**SW-PROC-007**: The DarkPixelCompensation module SHALL apply Cassandra kernel (5×5 neighborhood averaging) to B frame.

**SW-PROC-008**: The DarkPixelCompensation module SHALL toggle compensation via UI checkbox (ChkDarkPixelCompensation).

**SW-PROC-009**: The system SHALL report dark pixel count in status panel.

#### 3.4.3 Overlay Rendering

**SW-PROC-010**: The OverlayRenderer module SHALL render FPS counter on each video pane.

**SW-PROC-011**: The OverlayRenderer module SHALL render pixel inspector values (pixel ID, Gray value, position).

**SW-PROC-012**: The OverlayRenderer module SHALL render zoom level indicator.

**SW-PROC-013**: The OverlayRenderer module SHALL render frame metadata (timestamp, sequence number).

### 3.5 User Interface Module

**Module**: MainWindow, UiSettingsManager, ZoomPanManager, PixelInspector

**Traceability**: SYS-UI-001 through SYS-UI-017

#### 3.5.1 Main Window Layout

**SW-UI-001**: The MainWindow SHALL display 3 video panes in Grid layout: A (AVTP), B (LVDS), D (DIFF).

**SW-UI-002**: The MainWindow SHALL allocate 60% width to left pane (video + controls), 40% to right pane (monitoring).

**SW-UI-003**: The MainWindow SHALL use WriteableBitmap for each video pane with PixelFormats.Gray8 (A, B) or Bgr24 (D).

**SW-UI-004**: The MainWindow SHALL bind video panes to Image controls in XAML.

#### 3.5.2 Control Panel

**SW-UI-005**: The MainWindow SHALL provide Start/Stop/Pause buttons in StackPanel.

**SW-UI-006**: The MainWindow SHALL provide ComboBox for mode selection (Live, File, Scene, Sequence).

**SW-UI-007**: The MainWindow SHALL provide TextBox for FPS configuration (TxtFps).

**SW-UI-008**: The MainWindow SHALL provide ComboBox for LSM device type selection.

**SW-UI-009**: The MainWindow SHALL provide Button for snapshot export (BtnSnapshot).

**SW-UI-010**: The MainWindow SHALL provide CheckBox for recording toggle (ChkRecord).

**SW-UI-011**: The MainWindow SHALL provide CheckBox for dark pixel compensation (ChkDarkPixelCompensation).

#### 3.5.3 Status Panels

**SW-UI-012**: The MainWindow SHALL display AVTP status panel (right top) with:

- Frame count
- Sequence number
- Timestamp
- Stream ID
- EtherType
- VLAN tag

**SW-UI-013**: The MainWindow SHALL display CAN/UART monitor panel (right bottom, placeholder for future).

**SW-UI-014**: The MainWindow SHALL display status bar at bottom with:

- Current mode
- Network adapter status
- Recording status
- Error messages

#### 3.5.4 Zoom and Pan

**SW-UI-015**: The ZoomPanManager module SHALL support independent zoom per pane (1× to 10×).

**SW-UI-016**: The ZoomPanManager module SHALL support pan via mouse drag in zoomed state.

**SW-UI-017**: The ZoomPanManager module SHALL apply letterbox-aware transforms to preserve aspect ratio.

**SW-UI-018**: The ZoomPanManager module SHALL clamp pan offsets to prevent out-of-bounds rendering.

#### 3.5.5 Pixel Inspector

**SW-UI-019**: The PixelInspector module SHALL detect mouse position in video panes.

**SW-UI-020**: The PixelInspector module SHALL convert screen coordinates to pixel coordinates accounting for zoom/pan/letterbox.

**SW-UI-021**: The PixelInspector module SHALL display pixel ID (1-based, row-major), Gray value, and position (x, y).

**SW-UI-022**: The PixelInspector module SHALL update overlay in real-time on mouse move.

#### 3.5.6 Settings Persistence

**SW-UI-023**: The UiSettingsManager module SHALL load settings from `%APPDATA%\VilsSharpX\settings.json` on startup.

**SW-UI-024**: The UiSettingsManager module SHALL save settings on application exit.

**SW-UI-025**: The UiSettingsManager module SHALL persist:

- Last selected mode
- Last selected network adapter
- Last selected LSM device type
- Frame rate (FPS)
- Diff threshold
- Dark pixel compensation state

### 3.6 Recording Module

**Module**: AviRecorder, FrameSnapshotSaver, RecordingManager

**Traceability**: SYS-DATA-012, SYS-DATA-013, SYS-DATA-014

#### 3.6.1 AVI Recording

**SW-REC-001**: The AviRecorder module SHALL use SharpAvi to create uncompressed AVI files.

**SW-REC-002**: The AviRecorder module SHALL record 3 streams simultaneously: A (AVTP), B (LVDS), D (DIFF).

**SW-REC-003**: The AviRecorder module SHALL write frames at configured frame rate (from TxtFps).

**SW-REC-004**: The AviRecorder module SHALL create output files in `docs/outputs/videoRecords/` directory.

**SW-REC-005**: The AviRecorder module SHALL generate filenames with timestamp: `record_YYYYMMDD_HHMMSS.avi`.

**SW-REC-006**: The AviRecorder module SHALL close files properly on Stop or application exit.

**SW-REC-007**: The RecordingManager module SHALL coordinate recording lifecycle (Start/Stop/Status).

#### 3.6.2 Snapshot Export

**SW-REC-008**: The FrameSnapshotSaver module SHALL export PNG images for A, B, D frames.

**SW-REC-009**: The FrameSnapshotSaver module SHALL generate Excel report with pixel comparison data.

**SW-REC-010**: The FrameSnapshotSaver module SHALL use ClosedXML to create .xlsx files.

**SW-REC-011**: The FrameSnapshotSaver module SHALL create output files in `docs/outputs/frameSnapshots/` directory.

**SW-REC-012**: The FrameSnapshotSaver module SHALL generate filenames with timestamp: `snapshot_YYYYMMDD_HHMMSS`.

### 3.7 Transmission Module

**Module**: AvtpTransmitManager, AvtpRvfTransmitter, AvtpPacketBuilder, AvtpEthernetSender

**Traceability**: SYS-NET-004, SYS-NET-005, SYS-NET-007

#### 3.7.1 Packet Building

**SW-TX-001**: The AvtpPacketBuilder module SHALL construct Ethernet frames with configurable:

- Destination MAC address
- Source MAC address
- EtherType (default 0x22F0)
- VLAN tag (optional)

**SW-TX-002**: The AvtpPacketBuilder module SHALL construct RVF header with:

- Sequence number (incrementing)
- Frame ID
- Line index (1-based)
- Number of lines
- Chunk size

**SW-TX-003**: The AvtpPacketBuilder module SHALL append pixel data from source frame.

**SW-TX-004**: The AvtpPacketBuilder module SHALL split frames into configurable chunk sizes (default: 10 lines per chunk).

#### 3.7.2 Ethernet Transmission

**SW-TX-005**: The AvtpEthernetSender module SHALL use SharpPcap to send raw Ethernet frames.

**SW-TX-006**: The AvtpEthernetSender module SHALL transmit at configurable frame rate (1-100 fps).

**SW-TX-007**: The AvtpEthernetSender module SHALL run transmission loop on background thread.

**SW-TX-008**: The AvtpTransmitManager module SHALL coordinate transmission lifecycle (Start/Stop/Status).

**SW-TX-009**: The AvtpTransmitManager module SHALL update transmission status panel with packet count.

### 3.8 Diagnostic Logging Module

**Module**: DiagnosticLogger

**Traceability**: SYS-MAINT-001, SYS-MAINT-003, SYS-MAINT-004

#### 3.8.1 Logging

**SW-LOG-001**: The DiagnosticLogger module SHALL write events to `diagnostic.log` in application directory.

**SW-LOG-002**: The DiagnosticLogger module SHALL format log entries with timestamp, severity, message.

**SW-LOG-003**: The DiagnosticLogger module SHALL support log levels: Info, Warning, Error.

**SW-LOG-004**: The DiagnosticLogger module SHALL log AVTP capture events:

- Frame received (sequence number, timestamp)
- Gap detected (missing lines)
- Reassembly error (invalid chunk size, line index out of range)

**SW-LOG-005**: The DiagnosticLogger module SHALL log file I/O events:

- File loaded (path, dimensions)
- Recording started/stopped (path)
- Snapshot exported (path)

**SW-LOG-006**: The DiagnosticLogger module SHALL append to log file (not overwrite).

**SW-LOG-007**: The system SHALL log unhandled exceptions to `crash.log` with stack traces (via App.xaml.cs).

---

## 4. Non-Functional Requirements

### 4.1 Performance Requirements

**Traceability**: SYS-PERF-001 through SYS-PERF-011

#### 4.1.1 Throughput

**SW-PERF-001**: The system SHALL process AVTP frames at rates up to 100 fps without frame drops on recommended hardware (Intel i5, 8GB RAM).

**SW-PERF-002**: The DiffRenderer SHALL compute |A−B| for 25,600 pixels in less than 10ms per frame.

**SW-PERF-003**: The BitmapUtils.Blit() SHALL write 320×80 Gray8 pixels to WriteableBitmap in less than 5ms.

**SW-PERF-004**: The system SHALL update FPS counter using EMA (α=0.30) with negligible overhead (<1ms per frame).

#### 4.1.2 Latency

**SW-PERF-005**: The system SHALL marshal frame-ready events to UI thread within 10ms using Dispatcher.Invoke().

**SW-PERF-006**: The system SHALL respond to Start/Stop button clicks within 100ms.

**SW-PERF-007**: The ZoomPanManager SHALL apply transforms in less than 50ms per zoom/pan operation.

#### 4.1.3 Resource Utilization

**SW-PERF-008**: The system SHALL consume no more than 150 MB RAM during continuous operation at 100 fps.

**SW-PERF-009**: The system SHALL write diagnostic logs at a rate not exceeding 10 MB/hour.

**SW-PERF-010**: The AviRecorder SHALL write triple-stream AVI (A+B+D) with bandwidth not exceeding 25 MB/s.

### 4.2 Reliability Requirements

**Traceability**: SYS-REL-001 through SYS-REL-011

#### 4.2.1 Fault Tolerance

**SW-REL-001**: The AvtpLiveCapture SHALL gracefully handle network adapter disconnection by stopping capture and emitting status event.

**SW-REL-002**: The RvfReassembler SHALL validate chunk dimensions and reject invalid chunks without crashing.

**SW-REL-003**: The PgmLoader SHALL return null for corrupt PGM files instead of throwing unhandled exceptions.

**SW-REL-004**: The system SHALL catch unhandled exceptions in App.xaml.cs and log to crash.log before shutdown.

#### 4.2.2 Data Integrity

**SW-REL-005**: The Frame constructor SHALL clone pixel buffer arrays to prevent shared-buffer races.

**SW-REL-006**: The MainWindow SHALL use `lock (_frameLock)` for all frame buffer access.

**SW-REL-007**: The LiveCaptureManager SHALL marshal OnFrameReady callbacks to UI thread using Dispatcher.Invoke().

**SW-REL-008**: The AviRecorder SHALL flush buffers on Stop to prevent data loss.

#### 4.2.3 Availability

**SW-REL-009**: The MainWindow SHALL initialize in less than 5 seconds from launch.

**SW-REL-010**: The system SHALL shut down within 2 seconds by cancelling background tasks via CancellationToken.

**SW-REL-011**: The UiSettingsManager SHALL save settings synchronously on Window_Closing event.

### 4.3 Maintainability Requirements

**Traceability**: SYS-MAINT-005, SYS-MAINT-006, SYS-MAINT-007

#### 4.3.1 Code Structure

**SW-MAINT-001**: The codebase SHALL follow layered architecture with clear separation:

- Data Input Layer (AvtpLiveCapture, PgmLoader, etc.)
- Network & Protocol Layer (AvtpRvfParser, RvfReassembler)
- Processing Layer (DiffRenderer, DarkPixelCompensation)
- Rendering Layer (BitmapUtils, OverlayRenderer, ZoomPanManager)
- UI Layer (MainWindow, UiSettingsManager)
- Storage Layer (AviRecorder, FrameSnapshotSaver)
- Transmit Layer (AvtpTransmitManager, AvtpPacketBuilder)

**SW-MAINT-002**: Each module SHALL have single responsibility (manager pattern).

**SW-MAINT-003**: The system SHALL use event-driven patterns for async frame processing (OnFrameReady).

#### 4.3.2 Documentation

**SW-MAINT-004**: Each public class SHALL have XML documentation comments.

**SW-MAINT-005**: Complex algorithms (DiffRenderer, RvfReassembler) SHALL have inline comments explaining logic.

**SW-MAINT-006**: The codebase SHALL maintain architecture documentation (ARCHITECTURE_REVIEW.md, ARCHITECTURE_DIAGRAM.md).

### 4.4 Usability Requirements

**Traceability**: SYS-UI-001 through SYS-UI-017

#### 4.4.1 User Interface

**SW-USE-001**: The UI SHALL use intuitive labels for all controls (English).

**SW-USE-002**: The UI SHALL provide visual feedback for Start/Stop/Pause operations (button colors, status text).

**SW-USE-003**: The UI SHALL display error messages in user-friendly language (no raw exception messages).

**SW-USE-004**: The pixel inspector SHALL update in real-time without flickering.

#### 4.4.2 Accessibility

**SW-USE-005**: The UI SHALL use high-contrast colors for readability.

**SW-USE-006**: The UI SHALL support keyboard shortcuts for Start (F5), Stop (F6), Snapshot (F12).

**SW-USE-007**: The UI SHALL provide tooltips for all buttons and controls.

---

## 5. Interface Requirements

### 5.1 User Interface

**Traceability**: SYS-UI-001 through SYS-UI-017

**SW-IF-UI-001**: The MainWindow SHALL implement XAML-based WPF UI with code-behind (MainWindow.xaml.cs).

**SW-IF-UI-002**: The UI layout SHALL use Grid, StackPanel, and DockPanel for responsive design.

**SW-IF-UI-003**: The UI SHALL bind controls to event handlers (Click, TextChanged, SelectionChanged).

**SW-IF-UI-004**: The UI SHALL use Dispatcher.Invoke() for cross-thread updates.

### 5.2 Hardware Interface

**Traceability**: SYS-HW-006, SYS-HW-007

**SW-IF-HW-001**: The AvtpLiveCapture SHALL interface with network adapters via SharpPcap API.

**SW-IF-HW-002**: The SharpPcap API SHALL use Npcap driver for packet capture (wpcap.dll).

**SW-IF-HW-003**: The system SHALL enumerate adapters using `CaptureDeviceList.Instance`.

**SW-IF-HW-004**: The system SHALL open adapters in promiscuous mode for capture.

### 5.3 Software Interface

**Traceability**: SYS-SW-004, SYS-SW-005, SYS-SW-006

#### 5.3.1 SharpPcap Library

**SW-IF-SW-001**: The system SHALL use SharpPcap 6.2.5 or later.

**SW-IF-SW-002**: The AvtpLiveCapture SHALL call `device.Open()`, `device.StartCapture()`, `device.StopCapture()`.

**SW-IF-SW-003**: The AvtpEthernetSender SHALL call `device.SendPacket(byte[])`.

#### 5.3.2 SharpAvi Library

**SW-IF-SW-004**: The AviRecorder SHALL use SharpAvi 3.1.0 or later.

**SW-IF-SW-005**: The AviRecorder SHALL create AviWriter with uncompressed codec.

**SW-IF-SW-006**: The AviRecorder SHALL add video streams with `AddVideoStream()` and write frames with `WriteFrame()`.

#### 5.3.3 ClosedXML Library

**SW-IF-SW-007**: The FrameSnapshotSaver SHALL use ClosedXML 0.102.0 or later.

**SW-IF-SW-008**: The FrameSnapshotSaver SHALL create XLWorkbook, add worksheets, populate cells, and call `SaveAs()`.

#### 5.3.4 WriteableBitmapEx Library

**SW-IF-SW-009**: The BitmapUtils SHALL use WriteableBitmapEx for pixel manipulation.

**SW-IF-SW-010**: The system SHALL call `WriteableBitmap.WritePixels()` for rendering.

### 5.4 Communication Interface

**Traceability**: SYS-NET-001, SYS-NET-010

**SW-IF-COMM-001**: The system SHALL communicate via Ethernet (IEEE 802.3) using raw socket access.

**SW-IF-COMM-002**: The system SHALL filter frames by EtherType `0x22F0` at driver level.

**SW-IF-COMM-003**: The system SHALL parse VLAN tags (802.1Q) if present.

**SW-IF-COMM-004**: The system SHALL NOT establish TCP/UDP connections (raw Ethernet only).

---

## 6. Data Requirements

### 6.1 Frame Data Structures

**Traceability**: SYS-DATA-001 through SYS-DATA-007

#### 6.1.1 Frame Class

**SW-DATA-001**: The Frame class SHALL encapsulate:

- `byte[] pixels` (width × height bytes, e.g., 25,600 for 320×80, 16,384 for 256×64, or 17,408 for 256×68 before LVDS cropping)
- `int width` (device-specific)
- `int height` (device-specific)
- `PixelFormat format` (Gray8)

**SW-DATA-002**: The Frame constructor SHALL clone input pixel array to prevent shared references.

**SW-DATA-003**: The Frame class SHALL provide indexer `frame[x, y]` for pixel access.

#### 6.1.2 RvfChunk Structure

**SW-DATA-004**: The RvfChunk structure SHALL contain:

- `ushort sequenceNumber`
- `ushort frameId`
- `byte lineIndex` (1-based)
- `byte numLines`
- `byte[] payload`

**SW-DATA-005**: The RvfChunk SHALL be immutable (readonly fields or init-only properties).

#### 6.1.3 FrameMetadata Class

**SW-DATA-006**: The FrameMetadata class SHALL contain:

- `DateTime timestamp`
- `uint sequenceNumber`
- `uint streamId`
- `ushort vlanTag`
- `ushort etherType`

### 6.2 File Formats

**Traceability**: SYS-DATA-008 through SYS-DATA-015

#### 6.2.1 PGM Format

**SW-DATA-007**: The PgmLoader SHALL parse PGM header format:

```text
P2 or P5
<width> <height>
<maxval>
<pixel data>
```

**SW-DATA-008**: The PgmLoader SHALL validate width=320 or 256, height=80, 84, 64, or 68, maxval=255.

#### 6.2.2 Scene Format

**SW-DATA-009**: The SceneLoader SHALL parse .scene file format (custom text format):

```text
<file_path>,<frame_count>,<transition_type>
```

**SW-DATA-010**: The SceneLoader SHALL support absolute and relative file paths.

#### 6.2.3 Settings Format

**SW-DATA-011**: The UiSettingsManager SHALL persist settings in JSON format:

```json
{
  "LastMode": "Live",
  "LastAdapter": "eth0",
  "LastDeviceType": "Osram_1Chip",
  "FrameRate": 100,
  "DiffThreshold": 10,
  "DarkPixelCompensation": false
}
```

**SW-DATA-012**: The UiSettingsManager SHALL validate JSON schema on load and use defaults if invalid.

### 6.3 Log Formats

**Traceability**: SYS-MAINT-001, SYS-MAINT-002

#### 6.3.1 Diagnostic Log Format

**SW-DATA-013**: The DiagnosticLogger SHALL format entries as:

```text
[YYYY-MM-DD HH:MM:SS.fff] [LEVEL] Message
```

**SW-DATA-014**: The DiagnosticLogger SHALL use levels: INFO, WARN, ERROR.

#### 6.3.2 Crash Log Format

**SW-DATA-015**: The crash.log SHALL contain:

```text
[YYYY-MM-DD HH:MM:SS.fff] CRASH
Exception: <exception_type>
Message: <exception_message>
Stack Trace:
<stack_trace>
```

---

## 7. Quality Attributes

### 7.1 Correctness

**SW-QUAL-001**: The DiffRenderer SHALL compute pixel-accurate diff matching manual calculation.

**SW-QUAL-002**: The RvfReassembler SHALL reconstruct frames byte-identical to source.

**SW-QUAL-003**: The PixelInspector SHALL report correct pixel ID and value accounting for letterbox offset.

### 7.2 Robustness

**SW-QUAL-004**: The system SHALL recover from corrupt PCAP files without crashing.

**SW-QUAL-005**: The system SHALL validate all user inputs (FPS range, threshold range, file paths).

**SW-QUAL-006**: The system SHALL handle rapid Start/Stop sequences without race conditions.

### 7.3 Scalability

**SW-QUAL-007**: The system SHALL support a fixed set of resolutions based on device type (320×80, 320×84, 256×64, 256×68) with future expansion capability.

**SW-QUAL-008**: The system SHALL support additional device types via dropdown (extensible enum).

**SW-QUAL-009**: The system SHALL support plugin architecture for custom frame processors (future enhancement).

### 7.4 Testability

**SW-QUAL-010**: Each module SHALL be unit-testable in isolation (dependency injection, interfaces).

**SW-QUAL-011**: The RvfReassembler SHALL provide gap detection metrics for validation.

**SW-QUAL-012**: The system SHALL log sufficient diagnostic data for debugging (sequence numbers, timestamps, file paths).

---

## 8. Design Constraints

### 8.1 Technology Constraints

**Traceability**: SYS-CONST-001, SYS-CONST-002, SYS-CONST-003

**SW-CONST-001**: The system SHALL use .NET 8.0 Windows Desktop Runtime (WPF dependency).

**SW-CONST-002**: The system SHALL target `net8.0-windows` with `UseWPF=true` in project file.

**SW-CONST-003**: The system SHALL use C# 12.0 language features.

**SW-CONST-004**: The system SHALL use WPF for UI rendering (no cross-platform support).

**SW-CONST-005**: The system SHALL use SharpPcap for packet capture (no native .NET sockets).

### 8.2 Resolution and Format Constraints

**Traceability**: SYS-CONST-002, SYS-CONST-003

**SW-CONST-006**: The system SHALL define device-specific resolution constants: Osram `W=320`, `H_ACTIVE=80`, `H_LVDS=84`; Nichia `W=256`, `H_ACTIVE=64`, `H_LVDS=68`.

**SW-CONST-014**: The system SHALL select active resolution based on LSM device type selection.

**SW-CONST-007**: The system SHALL use `PixelFormats.Gray8` for source frames (8-bit grayscale).

**SW-CONST-008**: The system SHALL use `PixelFormats.Bgr24` for diff frames (24-bit color).

**SW-CONST-009**: The system SHALL use row-major pixel ordering (left-to-right, top-to-bottom).

### 8.3 Concurrency Constraints

**Traceability**: Threading Model (ARCHITECTURE_REVIEW.md Section 4)

**SW-CONST-010**: The system SHALL run background loops in `Task.Run()` with `CancellationToken`.

**SW-CONST-011**: The system SHALL marshal UI updates to UI thread using `Dispatcher.Invoke()` or `Dispatcher.BeginInvoke()`.

**SW-CONST-012**: The system SHALL use object locks (`_frameLock`) for frame buffer access.

**SW-CONST-013**: The system SHALL NOT use volatile fields for complex objects (use locks instead).

---

## 9. Verification and Validation

### 9.1 Verification Methods

**SW-VV-001**: Unit testing SHALL verify individual module behavior (RvfReassembler, DiffRenderer, AvtpRvfParser).

**SW-VV-002**: Integration testing SHALL verify end-to-end data flow (PCAP → Reassembly → Rendering).

**SW-VV-003**: Manual testing SHALL verify UI behavior (button clicks, zoom/pan, pixel inspector).

**SW-VV-004**: Performance testing SHALL measure FPS, latency, memory usage under sustained load.

### 9.2 Validation Criteria

**SW-VV-005**: The system SHALL successfully capture and display AVTP frames from test ECU at 100 fps.

**SW-VV-006**: The system SHALL accurately compute diff rendering matching reference images.

**SW-VV-007**: The system SHALL record AVI files playable in VLC Media Player.

**SW-VV-008**: The system SHALL export snapshots with correct pixel values in Excel report.

### 9.3 Test Coverage

**SW-VV-009**: Unit tests SHALL cover:

- RvfReassembler gap detection logic
- DiffRenderer threshold logic
- AvtpRvfParser header parsing
- Dark pixel compensation kernel

**SW-VV-010**: Integration tests SHALL cover:

- Live capture workflow (Start → Capture → Display → Stop)
- File playback workflow (Load → Play → Pause → Stop)
- Recording workflow (Start Record → Capture → Stop Record → Verify File)
- Transmission workflow (Start TX → Send Packets → Stop TX → Verify via Wireshark)

---

## 10. Traceability Matrix

### 10.1 System-to-Software Traceability

| System Requirement | Software Requirement      | Verification Method |
| ------------------ | ------------------------- | ------------------- |
| SYS-HW-001         | SW-ENV-001                | Inspection          |
| SYS-HW-006         | SW-IF-HW-001              | Test                |
| SYS-SW-001         | SW-CONST-001              | Inspection          |
| SYS-NET-001        | SW-NET-001, SW-PROTO-001  | Test                |
| SYS-NET-006        | SW-PERF-001               | Performance Test    |
| SYS-PERF-001       | SW-PERF-001               | Performance Test    |
| SYS-PERF-002       | SW-PERF-002               | Performance Test    |
| SYS-DATA-001       | SW-DATA-001, SW-CONST-006 | Inspection          |
| SYS-DATA-002       | SW-DATA-001, SW-CONST-006 | Inspection          |
| SYS-DATA-004       | SW-FILE-005               | Inspection          |
| SYS-UI-001         | SW-UI-001                 | Inspection          |
| SYS-REL-001        | SW-REL-001                | Test                |
| SYS-REL-005        | SW-DATA-002, SW-REL-005   | Code Review         |

### 10.2 Functional-to-Software Traceability

| Functional Area    | Software Requirements        | Modules                                |
| ------------------ | ---------------------------- | -------------------------------------- |
| Network Capture    | SW-NET-001 to SW-NET-011     | AvtpLiveCapture, LiveCaptureManager    |
| Protocol Parsing   | SW-PROTO-001 to SW-PROTO-011 | AvtpRvfParser, RvfReassembler          |
| File Playback      | SW-FILE-001 to SW-FILE-020   | PgmLoader, AviSourcePlayer, etc.       |
| Frame Processing   | SW-PROC-001 to SW-PROC-013   | DiffRenderer, DarkPixelCompensation    |
| User Interface     | SW-UI-001 to SW-UI-025       | MainWindow, ZoomPanManager             |
| Recording          | SW-REC-001 to SW-REC-012     | AviRecorder, FrameSnapshotSaver        |
| Transmission       | SW-TX-001 to SW-TX-009       | AvtpTransmitManager, AvtpPacketBuilder |
| Diagnostic Logging | SW-LOG-001 to SW-LOG-007     | DiagnosticLogger                       |

---

## Appendix A: Requirements Summary

**Total System Requirements**: 15 categories, ~120 requirements

**Total Software Requirements**: 9 functional modules, ~130 requirements

**Coverage**: 100% traceability from system to software requirements

---

## Appendix B: Change History

| Version | Date       | Author | Changes                        |
| ------- | ---------- | ------ | ------------------------------ |
| 1.0     | 2026-02-13 | Team   | Initial SRS based on SyRS v1.0 |

---

End of Software Requirements Specification.
