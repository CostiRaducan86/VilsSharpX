cmake_minimum_required(VERSION 3.13)

# ── Pico SDK import (must come before project()) ────────────────────
# Set PICO_SDK_PATH environment variable or pass -DPICO_SDK_PATH=...
include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(pico2_lvds_bridge C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Target: Raspberry Pi Pico 2 (RP2350)
set(PICO_BOARD pico2)

pico_sdk_init()

# ── Main firmware ────────────────────────────────────────────────────
add_executable(pico2_lvds_bridge
    main.c
    usb_descriptors.c
)

# ── PIO program generation (must be after add_executable) ────────────
pico_generate_pio_header(pico2_lvds_bridge
    ${CMAKE_CURRENT_LIST_DIR}/uart_rx.pio
)

target_include_directories(pico2_lvds_bridge PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}
)

target_link_libraries(pico2_lvds_bridge
    pico_stdlib
    hardware_pio
    hardware_dma
    hardware_clocks
    tinyusb_device
    tinyusb_board
)

# Disable default stdio (we handle USB CDC ourselves)
pico_enable_stdio_usb(pico2_lvds_bridge 0)
pico_enable_stdio_uart(pico2_lvds_bridge 0)

# Generate UF2 output (drag-and-drop to Pico 2 BOOTSEL)
pico_add_extra_outputs(pico2_lvds_bridge)
