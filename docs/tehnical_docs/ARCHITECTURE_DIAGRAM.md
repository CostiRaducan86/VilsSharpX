# VilsSharpX - System Architecture Diagram

## Block Diagram

```mermaid
graph TB
    subgraph "Data Sources"
        ETH["Ethernet NIC<br/>AVTP Frames<br/>0x22F0"]
        FILE["File Sources<br/>PGM/AVI/PCAP<br/>Scene/Sequence"]
    end

    subgraph "Network Layer"
        PCAP["SharpPcap<br/>Live Capture"]
        PARSER["AvtpRvfParser<br/>RVF Protocol<br/>320×80 frames"]
    end

    subgraph "Frame Assembly & Processing"
        REASM["RvfReassembler<br/>Line buffering<br/>Gap tracking"]
        PLAYBACK["PlaybackStateManager<br/>FPS estimation<br/>Timing control"]
        LOADERS["SourceLoaderHelper<br/>PgmLoader<br/>AviSourcePlayer<br/>SequencePlayer<br/>ScenePlayer"]
    end

    subgraph "Core Frame Pipeline"
        FRAME["Frame Buffer<br/>Gray8: 320×80<br/>RGB24: DIFF"]
        DIFF["DiffRenderer<br/>Pixel delta<br/>Threshold matching"]
        ZP["ZoomPanManager<br/>Transform<br/>Render bounds"]
    end

    subgraph "Rendering Engine"
        WB["WriteableBitmap<br/>Gray8 + BGR24<br/>PixelFormats"]
        BLIT["Blit() + WritePixels<br/>Direct bitmap update"]
        OVR["OverlayRenderer<br/>Perf metrics<br/>Pixel inspector"]
    end

    subgraph "UI Layer - Left Pane 60%"
        XAML["MainWindow.xaml<br/>3 Frame Viewers<br/>A B D<br/>+ Controls"]
        CTRL["Control Groups<br/>Hardware Config<br/>App Settings<br/>Ethernet Config<br/>Playback Buttons"]
    end

    subgraph "UI Layer - Right Pane 40%"
        CAN["CAN/UART Monitor<br/>Placeholder<br/>ListView<br/>Future impl"]
        STATUS["AVTP Status Panel<br/>LblStatus<br/>LblDiffStats<br/>Frame info"]
    end

    subgraph "Storage & Logging"
        REC["RecordingManager<br/>AVI output<br/>Frame writer"]
        SNAP["FrameSnapshotSaver<br/>PNG/Excel<br/>Capture frames"]
        LOG["DiagnosticLogger<br/>diagnostic.log<br/>crash.log"]
    end

    subgraph "TX/Control"
        TX["AvtpTransmitManager<br/>AVTP TX packets<br/>MAC/VLAN/EtherType"]
        NIC["NetInterface Utils<br/>NIC enumeration<br/>Active monitor"]
    end

    ETH -->|"sniff frames<br/>ethertype 0x22F0"| PCAP
    FILE -->|"Load from disk"| LOADERS
    PCAP -->|"ethernet packets"| PARSER
    PARSER -->|"RVF chunks<br/>line numbers"| REASM
    REASM -->|"Frame ready<br/>OnFrameReady event"| FRAME
    LOADERS -->|"frame bytes"| FRAME
    
    FRAME -->|"A[Gray8]"| DIFF
    FRAME -->|"B[Gray8]"| DIFF
    DIFF -->|"DIFF[BGR24]"| FRAME
    
    PLAYBACK -->|"FPS/timing"| FRAME
    FRAME -->|"WriteableBitmap<br/>data"| WB
    WB -->|"pixel update"| BLIT
    BLIT -->|"UI thread<br/>Dispatcher.Invoke"| XAML
    
    FRAME -->|"transform"| ZP
    ZP -->|"clip/zoom"| BLIT
    OVR -->|"overlay metrics"| BLIT
    
    XAML -->|"Sliders/Combos"| CTRL
    CTRL -->|"FPS/threshold<br/>device type"| PLAYBACK
    CTRL -->|"Src/Dst MAC<br/>VLAN ID"| TX
    CTRL -->|"NIC select"| NIC
    
    FRAME -->|"display data"| CAN
    FRAME -->|"stats + frame<br/>info"| STATUS
    
    FRAME -->|"save frame"| SNAP
    FRAME -->|"record video"| REC
    PLAYBACK -->|"timing/FPS"| LOG
    PARSER -->|"protocol trace"| LOG
    
    TX -->|"send AVTP<br/>frames"| ETH
    NIC -->|"list NICs"| CTRL

    style ETH fill:#e1f5ff
    style PCAP fill:#fff3e0
    style PARSER fill:#f3e5f5
    style REASM fill:#f3e5f5
    style FRAME fill:#c8e6c9
    style WB fill:#bbdefb
    style XAML fill:#f1f8e9
    style STATUS fill:#ffe0b2
    style CAN fill:#f0f4c3
    style LOG fill:#fce4ec
```

## Color Legend

| Color | Layer | Components |
| ----- | ----- | ---------- |
| Light Blue (`#e1f5ff`) | Data Sources | Ethernet, External input |
| Light Orange (`#fff3e0`) | Network | SharpPcap, Protocol parsing |
| Light Purple (`#f3e5f5`) | Processing | Reassembly, Frame assembly |
| Light Green (`#c8e6c9`) | Core Pipeline | Frame buffer, processing |
| Light Blue (`#bbdefb`) | Rendering | WriteableBitmap, output |
| Light Green (`#f1f8e9`) | UI - Content | Main window, viewers |
| Light Yellow (`#ffe0b2`) | UI - Status | Status panels, monitoring |
| Light Yellow (`#f0f4c3`) | UI - Monitoring | CAN/UART monitor |
| Light Pink (`#fce4ec`) | Support | Logging, diagnostics |

## Key Data Paths

### 1. Live AVTP Capture Path

```text
Ethernet NIC 
  → SharpPcap (sniff ethertype 0x22F0)
  → AvtpRvfParser (decode RVF chunks)
  → RvfReassembler (line-based buffering)
  → Frame Buffer (320×80 Gray8)
  → DiffRenderer
  → WriteableBitmap → UI Display
```

### 2. File Playback Path

```text
PGM/AVI/PCAP/Scene File
  → SourceLoaderHelper / Specialized Players
  → PlaybackStateManager (FPS scheduling)
  → Frame Buffer
  → DiffRenderer
  → WriteableBitmap → UI Display
```

### 3. AVTP TX Path

```text
UI Controls (MAC/VLAN/EtherType inputs)
  → AvtpTransmitManager (packet assembly)
  → Ethernet NIC (transmit frames)
```

---

**Diagram Version**: 1.0  
**Generated**: February 13, 2026  
**Project**: VilsSharpX (WPF .NET 8)
