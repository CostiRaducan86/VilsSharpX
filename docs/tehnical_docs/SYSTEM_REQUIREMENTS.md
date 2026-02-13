# VilsSharpX - System Requirements Specification

**Document Type**: System Requirements Specification (SyRS)

**Date**: February 13, 2026

**Version**: 1.0

**Project**: VilsSharpX - AVTP/LVDS Video Frame Monitoring & Comparison Tool

**Status**: Baseline

---

## 1. Introduction

### 1.1 Purpose

This document specifies the system-level requirements for VilsSharpX, a pixel-accurate inspection tool for 8-bit grayscale video frames in automotive ECU development and validation.

### 1.2 Scope

VilsSharpX enables real-time and file-based frame acquisition, comparison, and analysis for automotive lighting module testing. The system supports:

- Live Ethernet AVTP/RVF packet capture
- File-based playback (PGM, AVI, PCAP, Scene sequences)
- Dual-pane visualization with pixel-accurate diff rendering
- Frame recording, snapshot export, and diagnostic logging
- AVTP packet transmission for testing ECU receivers

### 1.3 Definitions and Acronyms

- **AVTP**: Audio Video Transport Protocol (IEEE 1722)
- **RVF**: Raw Video Format (proprietary frame reassembly protocol)
- **ECU**: Electronic Control Unit
- **LVDS**: Low-Voltage Differential Signaling
- **FPS**: Frames Per Second
- **LSM**: Lighting System Module
- **PGM**: Portable Gray Map (image format)
- **PCAP**: Packet Capture (network trace format)
- **WPF**: Windows Presentation Foundation

---

## 2. System Overview

### 2.1 System Context

VilsSharpX operates as a standalone desktop application on Windows systems, interfacing with:

- Ethernet network adapters (for AVTP capture/transmission)
- Local file system (for playback and recording)
- Display devices (for visualization)
- User input devices (keyboard, mouse)

### 2.2 System Architecture

The system follows a layered architecture:

```text
┌─────────────────────────────────────────────────┐
│           User Interface Layer (WPF)           │
├─────────────────────────────────────────────────┤
│        Processing & Rendering Layer            │
├─────────────────────────────────────────────────┤
│    Network & Protocol Layer (AVTP/RVF)        │
├─────────────────────────────────────────────────┤
│   Data Sources (Live Capture / File Replay)    │
└─────────────────────────────────────────────────┘
```

---

## 3. Hardware Requirements

### 3.1 Minimum Hardware Configuration

**SYS-HW-001**: The system SHALL operate on x86-64 compatible processors with a minimum of 2 cores.

**SYS-HW-002**: The system SHALL require at least 4 GB of system RAM.

**SYS-HW-003**: The system SHALL require at least 500 MB of available disk space for application installation.

**SYS-HW-004**: The system SHALL require at least 5 GB of available disk space for frame recording and diagnostic logs.

**SYS-HW-005**: The system SHALL support display resolutions of at least 1280×720 pixels.

### 3.2 Network Hardware Requirements

**SYS-HW-006**: The system SHALL support Ethernet network adapters compatible with SharpPcap library (WinPcap/Npcap backend).

**SYS-HW-007**: The system SHALL support Gigabit Ethernet (1000BASE-T) adapters for live AVTP capture.

**SYS-HW-008**: The system SHALL support promiscuous mode on network adapters for packet capture.

### 3.3 Recommended Hardware Configuration

**SYS-HW-009**: The system SHOULD operate on Intel Core i5 or AMD Ryzen 5 equivalent or better processors.

**SYS-HW-010**: The system SHOULD have at least 8 GB of system RAM for optimal performance.

**SYS-HW-011**: The system SHOULD support display resolutions of 1920×1080 or higher.

---

## 4. Software Requirements

### 4.1 Operating System Requirements

**SYS-SW-001**: The system SHALL run on Microsoft Windows 10 (version 1809 or later) or Windows 11.

**SYS-SW-002**: The system SHALL require .NET 8.0 Runtime (Windows Desktop) or later.

**SYS-SW-003**: The system SHALL require Npcap driver (version 1.70 or later) for packet capture functionality.

### 4.2 Software Dependencies

**SYS-SW-004**: The system SHALL use SharpPcap library for network packet capture and transmission.

**SYS-SW-005**: The system SHALL use SharpAvi library for AVI file recording.

**SYS-SW-006**: The system SHALL use ClosedXML library for Excel report generation.

**SYS-SW-007**: The system SHALL use WriteableBitmapEx library for bitmap manipulation.

### 4.3 Runtime Environment

**SYS-SW-008**: The system SHALL execute as a Windows desktop application (WinExe).

**SYS-SW-009**: The system SHALL NOT require elevated (administrator) privileges for core functionality EXCEPT for network adapter access.

**SYS-SW-010**: The system SHALL support execution from any directory path with read/write access.

---

## 5. Network Requirements

### 5.1 Protocol Requirements

**SYS-NET-001**: The system SHALL support AVTP packet capture with EtherType `0x22F0`.

**SYS-NET-002**: The system SHALL support RVF protocol frame reassembly with 1-based line numbering.

**SYS-NET-003**: The system SHALL support optional VLAN tagging (802.1Q) on AVTP frames.

**SYS-NET-004**: The system SHALL support configurable MAC addresses for AVTP transmission.

**SYS-NET-005**: The system SHALL support configurable Stream ID for AVTP transmission.

### 5.2 Network Performance

**SYS-NET-006**: The system SHALL capture AVTP packets at rates up to 100 frames per second.

**SYS-NET-007**: The system SHALL transmit AVTP packets at configurable frame rates (1-100 fps).

**SYS-NET-008**: The system SHALL support network adapter selection from available interfaces.

**SYS-NET-009**: The system SHALL detect network adapter disconnection within 5 seconds.

### 5.3 Network Compatibility

**SYS-NET-010**: The system SHALL operate on standard Ethernet networks (IEEE 802.3).

**SYS-NET-011**: The system SHALL tolerate packet loss and out-of-order delivery.

**SYS-NET-012**: The system SHALL support playback of PCAP files captured by standard tools (Wireshark, tcpdump).

---

## 6. Performance Requirements

### 6.1 Frame Processing Performance

**SYS-PERF-001**: The system SHALL process incoming AVTP frames at rates up to 100 fps without frame drops on recommended hardware.

**SYS-PERF-002**: The system SHALL compute pixel-accurate diff rendering (|A−B|) at rates up to 100 fps.

**SYS-PERF-003**: The system SHALL update frame rate estimation using Exponential Moving Average (EMA) with α=0.30, window=0.25s.

**SYS-PERF-004**: The system SHALL support zoom and pan operations with less than 50ms latency.

### 6.2 Latency Requirements

**SYS-PERF-005**: The system SHALL display live AVTP frames within 30ms of packet reception (on recommended hardware).

**SYS-PERF-006**: The system SHALL respond to user input (Start/Stop/Pause) within 100ms.

**SYS-PERF-007**: The system SHALL complete frame snapshot export within 2 seconds for standard resolution (320×80).

### 6.3 Resource Utilization

**SYS-PERF-008**: The system SHALL consume no more than 30 MB of RAM during idle state.

**SYS-PERF-009**: The system SHALL consume no more than 150 MB of RAM during continuous operation at 100 fps.

**SYS-PERF-010**: The system SHALL write diagnostic logs at a rate not exceeding 10 MB/hour during normal operation.

**SYS-PERF-011**: The system SHALL record AVI streams with bandwidth not exceeding 25 MB/s for triple-stream recording (A+B+D).

---

## 7. Data Requirements

### 7.1 Video Frame Specifications

**SYS-DATA-001**: The system SHALL support frame dimensions of 320×80 pixels (active area).

**SYS-DATA-002**: The system SHALL support frame dimensions of 256×64 pixels (active area) for Nichia LSM devices.

**SYS-DATA-003**: The system SHALL support frame dimensions of 320×84 pixels (Osram LVDS with metadata, cropped to 320×80 for display).

**SYS-DATA-004**: The system SHALL support frame dimensions of 256×68 pixels (Nichia LVDS with metadata, cropped to 256×64 for display).

**SYS-DATA-005**: The system SHALL process frames in Gray8 pixel format (8-bit grayscale, 1 byte per pixel).

**SYS-DATA-006**: The system SHALL render diff frames in BGR24 pixel format (24-bit color, 3 bytes per pixel).

**SYS-DATA-007**: The system SHALL process frame buffer sizes of 25,600 bytes (320×80×1), 16,384 bytes (256×64×1), and 17,408 bytes (256×68×1) before LVDS cropping.

### 7.2 File Format Support

**SYS-DATA-008**: The system SHALL load PGM image files in P2 (ASCII) and P5 (binary) formats.

**SYS-DATA-009**: The system SHALL load uncompressed, indexed AVI files.

**SYS-DATA-010**: The system SHALL load PCAP files containing Ethernet frames.

**SYS-DATA-011**: The system SHALL load Scene files (.scene) defining multi-frame sequences.

**SYS-DATA-012**: The system SHALL export snapshots in PNG format.

**SYS-DATA-013**: The system SHALL export comparison reports in Excel format (.xlsx).

**SYS-DATA-014**: The system SHALL record AVI files in uncompressed format with indexing.

### 7.3 Configuration Data

**SYS-DATA-015**: The system SHALL persist user settings in JSON format to `%APPDATA%\VilsSharpX\settings.json`.

**SYS-DATA-016**: The system SHALL store diagnostic logs in `diagnostic.log` in the application directory.

**SYS-DATA-017**: The system SHALL store crash logs in `crash.log` in the application directory.

---

## 8. User Interface Requirements

### 8.1 Display Layout

**SYS-UI-001**: The system SHALL display three video panes: Cadran A (AVTP), Cadran B (LVDS), Cadran D (DIFF).

**SYS-UI-002**: The system SHALL allocate 60% of window width to video panes and controls, 40% to monitoring panels.

**SYS-UI-003**: The system SHALL display real-time FPS counter in each video pane.

**SYS-UI-004**: The system SHALL display pixel inspector values on mouse hover in video panes.

**SYS-UI-005**: The system SHALL support zoom levels from 1× to 10× in each video pane independently.

**SYS-UI-006**: The system SHALL support pan operations in zoomed video panes.

### 8.2 Control Interface

**SYS-UI-007**: The system SHALL provide Start/Stop/Pause buttons for playback control.

**SYS-UI-008**: The system SHALL provide mode selection (Live Capture / File Playback / Scene / Sequence).

**SYS-UI-009**: The system SHALL provide network adapter selection dropdown.

**SYS-UI-010**: The system SHALL provide LSM device type selection (Osram, Nichia variants).

**SYS-UI-011**: The system SHALL provide recording toggle buttons for snapshot and AVI recording.

**SYS-UI-012**: The system SHALL provide AVTP transmission toggle and configuration controls.

### 8.3 Status Monitoring

**SYS-UI-013**: The system SHALL display frame count, timestamp, and sequence number for AVTP frames.

**SYS-UI-014**: The system SHALL display network adapter status (connected/disconnected).

**SYS-UI-015**: The system SHALL display recording status (active/inactive, file path).

**SYS-UI-016**: The system SHALL display transmission status (active/inactive, packet count).

**SYS-UI-017**: The system SHALL display error messages in status bar or dedicated panel.

---

## 9. Reliability Requirements

### 9.1 Fault Tolerance

**SYS-REL-001**: The system SHALL continue operation when network adapter is disconnected during live capture.

**SYS-REL-002**: The system SHALL recover from corrupt or incomplete AVTP frames without crashing.

**SYS-REL-003**: The system SHALL log unhandled exceptions to crash.log and attempt graceful shutdown.

**SYS-REL-004**: The system SHALL validate frame dimensions and reject frames exceeding buffer limits.

### 9.2 Data Integrity

**SYS-REL-005**: The system SHALL clone frame buffers to prevent shared-buffer race conditions.

**SYS-REL-006**: The system SHALL use thread-safe locks (_frameLock) for frame buffer access.

**SYS-REL-007**: The system SHALL marshal UI updates to the UI thread using Dispatcher.Invoke.

**SYS-REL-008**: The system SHALL validate PCAP file integrity before playback.

### 9.3 Availability

**SYS-REL-009**: The system SHALL start successfully within 5 seconds of launch.

**SYS-REL-010**: The system SHALL shut down cleanly within 2 seconds of exit command.

**SYS-REL-011**: The system SHALL persist settings on exit to prevent data loss.

---

## 10. Maintainability Requirements

### 10.1 Logging and Diagnostics

**SYS-MAINT-001**: The system SHALL write diagnostic events to diagnostic.log with timestamps.

**SYS-MAINT-002**: The system SHALL write crash events to crash.log with stack traces.

**SYS-MAINT-003**: The system SHALL log AVTP capture events (frame received, gaps detected, reassembly errors).

**SYS-MAINT-004**: The system SHALL log file I/O events (file loaded, recording started/stopped).

### 10.2 Code Structure

**SYS-MAINT-005**: The system SHALL follow layered architecture with separation of concerns.

**SYS-MAINT-006**: The system SHALL use manager classes for specialized concerns (playback, capture, recording, transmission).

**SYS-MAINT-007**: The system SHALL use event-driven patterns for asynchronous frame processing.

---

## 11. Security Requirements

### 11.1 Network Security

**SYS-SEC-001**: The system SHALL NOT transmit sensitive data over the network except configured AVTP frames.

**SYS-SEC-002**: The system SHALL validate Ethernet frame headers before parsing.

**SYS-SEC-003**: The system SHALL NOT accept remote commands or control connections.

### 11.2 File System Security

**SYS-SEC-004**: The system SHALL only write to application directory and %APPDATA% folder.

**SYS-SEC-005**: The system SHALL validate file paths before reading or writing.

**SYS-SEC-006**: The system SHALL NOT execute external binaries or scripts.

---

## 12. Extensibility Requirements

### 12.1 Future Enhancements

**SYS-EXT-001**: The system SHOULD support plugin architecture for custom frame processors.

**SYS-EXT-002**: The system SHOULD support CAN/UART monitoring integration (placeholder currently implemented).

**SYS-EXT-003**: The system SHOULD support AVB/TSN protocol extensions for automotive compliance.

**SYS-EXT-004**: The system SHOULD support additional device types beyond Osram and Nichia.

### 12.2 Configuration Extensibility

**SYS-EXT-005**: The system SHALL support configurable frame rates (1-100 fps).

**SYS-EXT-006**: The system SHALL support configurable diff thresholds (0-255).

**SYS-EXT-007**: The system SHALL support configurable dark pixel compensation kernels.

**SYS-EXT-008**: The system SHALL support dynamic resolution selection based on LSM device type (e.g., Osram 320×80/84, Nichia 256×64/68).

---

## 13. Constraints and Assumptions

### 13.1 Constraints

**SYS-CONST-001**: The system is constrained to Windows operating systems due to WPF dependency.

**SYS-CONST-002**: The system is constrained to a fixed set of resolutions (320×80/84 and 256×64/68) due to hardware device constraints.

**SYS-CONST-003**: The system is constrained to Gray8 pixel format for source frames due to ECU output format.

**SYS-CONST-004**: The system requires Npcap driver for packet capture (cannot use native .NET sockets).

### 13.2 Assumptions

**SYS-ASSUMP-001**: It is assumed that network adapters support promiscuous mode for AVTP capture.

**SYS-ASSUMP-002**: It is assumed that ECU transmits AVTP frames with correct RVF protocol structure.

**SYS-ASSUMP-003**: It is assumed that users have basic understanding of Ethernet networking concepts.

**SYS-ASSUMP-004**: It is assumed that file-based inputs (PGM, AVI) are generated by trusted sources.

---

## 14. Compliance and Standards

### 14.1 Protocol Standards

**SYS-COMP-001**: The system SHALL comply with IEEE 802.3 Ethernet standards.

**SYS-COMP-002**: The system SHALL support IEEE 802.1Q VLAN tagging.

**SYS-COMP-003**: The system SHALL implement proprietary RVF protocol as specified in internal documentation.

### 14.2 Software Standards

**SYS-COMP-004**: The system SHALL follow Microsoft .NET Framework coding guidelines.

**SYS-COMP-005**: The system SHALL follow WPF application architecture patterns.

---

## 15. Acceptance Criteria

### 15.1 Functional Acceptance

**SYS-ACCEPT-001**: The system SHALL successfully capture and display live AVTP frames at 100 fps on test hardware.

**SYS-ACCEPT-002**: The system SHALL successfully load and play PGM/AVI/PCAP files without errors.

**SYS-ACCEPT-003**: The system SHALL compute pixel-accurate diff rendering matching reference implementation.

**SYS-ACCEPT-004**: The system SHALL record AVI streams that can be played back in standard media players.

### 15.2 Performance Acceptance

**SYS-ACCEPT-005**: The system SHALL demonstrate frame rate stability (±5% variance) during 1-hour continuous operation.

**SYS-ACCEPT-006**: The system SHALL demonstrate memory footprint below 200 MB during 1-hour operation.

**SYS-ACCEPT-007**: The system SHALL respond to user commands within specified latency limits.

### 15.3 Reliability Acceptance

**SYS-ACCEPT-008**: The system SHALL operate without crash for 8 hours continuous operation on test hardware.

**SYS-ACCEPT-009**: The system SHALL recover from simulated network disconnection within 5 seconds.

**SYS-ACCEPT-010**: The system SHALL validate corrupt PCAP files without crashing.

---

## Appendix A: Traceability Matrix

| Requirement ID | Software Requirement | Verification Method |
| -------------- | -------------------- | ------------------- |
| SYS-HW-001     | SW-ENV-001           | Inspection          |
| SYS-HW-006     | SW-NET-001           | Test                |
| SYS-SW-001     | SW-ENV-002           | Inspection          |
| SYS-NET-001    | SW-PROTO-001         | Test                |
| SYS-PERF-001   | SW-PERF-001          | Test                |
| SYS-DATA-001   | SW-DATA-001          | Test                |
| SYS-UI-001     | SW-UI-001            | Inspection          |

---

## Appendix B: Requirements Summary

**Total Requirements**: 140

| Requirement Category | Count |
| -------------------- | ----- |
| SYS-ACCEPT           | 10    |
| SYS-ASSUMP           | 4     |
| SYS-COMP             | 5     |
| SYS-CONST            | 4     |
| SYS-DATA             | 18    |
| SYS-EXT              | 8     |
| SYS-HW               | 13    |
| SYS-MAINT            | 7     |
| SYS-NET              | 13    |
| SYS-PERF             | 12    |
| SYS-REL              | 11    |
| SYS-SEC              | 6     |
| SYS-SW               | 11    |
| SYS-UI               | 18    |

---

End of System Requirements Specification.
