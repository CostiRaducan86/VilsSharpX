# LVDS-to-USB Bridge Firmware — Pico 2 (RP2350)

## Overview

This firmware turns a Raspberry Pi Pico 2 (on the gusmanb LogicAnalyzer
level-shifting board) into a high-speed UART-to-USB CDC bridge for
capturing LVDS video data from automotive ECU→LSM links.

```
LVDS signal → NBA3N012C → TTL → Ch1/GPIO2 → PIO UART RX → USB CDC → PC (VilsSharpX)
              (receiver)        (board pin)   (RP2350)       (COM port)
```

## Supported Modes

| Mode    | Baud Rate  | UART Config | PIO Oversampling | Clock Divider |
|---------|------------|-------------|------------------|---------------|
| Nichia  | 12.5 Mbps  | 8N1         | 8×               | 1.5           |
| Osram   | 20 Mbps    | 8O1*        | 4×               | 1.875         |

*Osram line parity is ignored at PIO level — raw 8-bit data is forwarded.

## Hardware Wiring

1. **LVDS Receiver**: Connect the NBA3N012C LVDS receiver output (TTL, 3.3V)
   to **Channel 1** on the LogicAnalyzer board header.
2. **USB**: Connect Pico 2 to PC via USB cable.
3. **Power**: Board is powered via USB (5V from PC, regulated to 3.3V on board).

Channel 1 on the LogicAnalyzer board maps to **GPIO 2** on the Pico 2.

## Host Commands (PC → Pico)

Send a single byte over the CDC serial port:

| Command | Action                                 |
|---------|----------------------------------------|
| `N`     | Switch to Nichia mode (12.5 Mbps)      |
| `O`     | Switch to Osram mode (20 Mbps)         |
| `S`     | Query status (returns mode + byte count) |
| `R`     | Reset statistics                       |

Default mode on power-up: **Nichia** (12.5 Mbps).

## LED Status

| State              | LED Behaviour   |
|--------------------|-----------------|
| USB not connected  | Off             |
| Connected, no data | Blinking (2 Hz) |
| Data flowing       | Solid on        |

## Building

### Prerequisites

- [Pico SDK](https://github.com/raspberrypi/pico-sdk) (v2.0+)
- CMake 3.13+
- ARM cross-compiler (`arm-none-eabi-gcc`)

### Steps

```bash
# Set the SDK path
export PICO_SDK_PATH=/path/to/pico-sdk

# Create build directory
mkdir build && cd build

# Configure (target = Pico 2 / RP2350)
cmake -DPICO_BOARD=pico2 ..

# Build
make -j$(nproc)
```

Output: `pico2_lvds_bridge.uf2`

### Flashing

1. Hold the **BOOTSEL** button on the Pico 2 and connect USB (or press
   BOOTSEL + reset if already connected).
2. The Pico 2 appears as a USB mass storage drive (`RPI-RP2`).
3. Copy `pico2_lvds_bridge.uf2` to the drive.
4. The Pico 2 reboots and the bridge starts automatically.
5. A new COM port appears in Device Manager / `dmesg`.

## Data Format

The firmware forwards **raw UART bytes** with no additional framing.
The VilsSharpX PC application (`LvdsFrameReassembler`) handles:
- Sync detection (`0x5D` per line)
- Row address extraction
- Pixel data accumulation
- CRC verification
- Frame assembly from individual lines

## Performance Notes

- PIO UART RX uses the RP2350's programmable I/O for cycle-exact
  bit sampling, achieving reliable capture at up to 20 Mbps.
- Polling-based FIFO drain (not DMA) keeps code simple and avoids
  the 32→8 bit packing overhead. At ≤2 MB/s effective throughput,
  the 150 MHz dual-core RP2350 handles this easily.
- USB Full Speed bulk endpoint: ~1 MB/s theoretical max. With
  inter-line gaps in the LVDS protocol, effective data rate stays
  well within this limit.
