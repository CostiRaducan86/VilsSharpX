/*
 * main.c — LVDS-to-USB-CDC bridge firmware for Raspberry Pi Pico 2 (RP2350)
 *
 * Hardware setup:
 *   - Pico 2 mounted on gusmanb LogicAnalyzer level-shifting board
 *   - LVDS receiver (onsemi NBA3N012C) converts LVDS → TTL (3.3V)
 *   - TTL signal connected to Channel 1 of the LogicAnalyzer board (= GPIO 2)
 *   - Pico 2 connected to PC via USB (appears as virtual COM port)
 *
 * Data flow:
 *   LVDS → NBA3N012C → TTL → Ch1/GPIO2 → PIO UART RX → DMA → USB CDC → PC
 *
 * Protocol modes (selected by host command over CDC):
 *   'N' = Nichia:  12,500,000 baud, 8N1, 8× oversampling
 *   'O' = Osram:   20,000,000 baud, 8O1*, 4× oversampling
 *
 *   *Note: Osram uses 8O1 (odd parity) at the electrical level, but the
 *    PIO captures raw 8-bit data without parity checking. The parity bit
 *    is handled at the UART framing level and is transparent to pixel data.
 *    The VilsSharpX PC software handles parity verification if needed.
 *
 * LED status:
 *   - Solid ON:  bridge running, data flowing
 *   - Blinking:  bridge running, no data (idle)
 *   - Off:       USB not connected
 *
 * Build:
 *   mkdir build && cd build
 *   cmake -DPICO_SDK_PATH=/path/to/pico-sdk ..
 *   make -j4
 *   # Copy pico2_lvds_bridge.uf2 to Pico 2 in BOOTSEL mode
 */

#include <stdio.h>
#include <string.h>
#include "pico/stdlib.h"
#include "hardware/pio.h"
#include "hardware/dma.h"
#include "hardware/clocks.h"
#include "hardware/gpio.h"
#include "tusb.h"
#include "bsp/board_api.h"
#include "pico/bootrom.h"

// Generated from uart_rx.pio
#include "uart_rx.pio.h"

/* ── Configuration ───────────────────────────────────────────────── */

#define UART_RX_PIN         2           /* GPIO 2 = Channel 1 on LogicAnalyzer board */
#define LED_PIN             25          /* Onboard LED (active high on Pico 2) */

/* Baud rates */
#define BAUD_NICHIA         12500000    /* 12.5 Mbps */
#define BAUD_OSRAM          20000000    /* 20 Mbps */

/* DMA double-buffer: 2 × BUF_SIZE bytes.
 * While one buffer is being transmitted over USB, the other collects PIO data.
 * At 20 Mbps raw UART = ~2 MB/s, but actual pixel data is ~1.8 MB/s
 * (gaps between lines). USB FS bulk can handle ~1 MB/s sustained.
 * With line gaps, effective throughput is well within USB FS limits.
 */
#define BUF_SIZE            2048
#define BUF_COUNT           2

/* ── Globals ─────────────────────────────────────────────────────── */

static PIO           pio        = pio0;
static uint          sm         = 0;
static int           dma_chan    = -1;
static volatile uint buf_idx    = 0;

/* Double buffer for DMA → USB transfer */
static uint8_t dma_buf[BUF_COUNT][BUF_SIZE];

/* Current protocol mode */
typedef enum { MODE_NICHIA = 0, MODE_OSRAM = 1 } protocol_mode_t;
static protocol_mode_t current_mode = MODE_NICHIA;

/* Statistics */
static uint32_t total_bytes   = 0;
static uint32_t last_led_time = 0;
static bool     led_state     = false;

/* ── Forward declarations ────────────────────────────────────────── */

static void start_capture(protocol_mode_t mode);
static void stop_capture(void);
static void restart_capture(protocol_mode_t mode);
static void configure_dma(void);
static void process_host_commands(void);
static void flush_pio_to_usb(void);
static void update_led(void);

/* ═══════════════════════════════════════════════════════════════════ */
/*  Main entry point                                                  */
/* ═══════════════════════════════════════════════════════════════════ */

int main(void)
{
    /* Board init (clocks, GPIO) */
    board_init();

    /* LED */
    gpio_init(LED_PIN);
    gpio_set_dir(LED_PIN, GPIO_OUT);

    /* TinyUSB */
    tusb_init();

    /* Start in Nichia mode by default */
    start_capture(MODE_NICHIA);

    /* ── Main loop ───────────────────────────────────────────────── */
    while (1)
    {
        tud_task();               /* USB device task (non-blocking) */
        process_host_commands();  /* Check for mode-change commands */
        flush_pio_to_usb();       /* Drain PIO RX FIFO → USB CDC TX */
        update_led();             /* Visual feedback */
    }

    return 0; /* never reached */
}

/* ═══════════════════════════════════════════════════════════════════ */
/*  Capture control                                                   */
/* ═══════════════════════════════════════════════════════════════════ */

static void start_capture(protocol_mode_t mode)
{
    current_mode = mode;

    uint offset;

    if (mode == MODE_NICHIA)
    {
        /* 12.5 Mbps, 8× oversampling → PIO clock = 100 MHz, div = 1.5 */
        offset = pio_add_program(pio, &uart_rx_8x_program);
        uart_rx_8x_program_init(pio, sm, offset, UART_RX_PIN, BAUD_NICHIA);
    }
    else
    {
        /* 20 Mbps, 4× oversampling → PIO clock = 80 MHz, div = 1.875 */
        offset = pio_add_program(pio, &uart_rx_4x_program);
        uart_rx_4x_program_init(pio, sm, offset, UART_RX_PIN, BAUD_OSRAM);
    }
}

static void stop_capture(void)
{
    pio_sm_set_enabled(pio, sm, false);
    pio_clear_instruction_memory(pio);

    /* Drain any remaining data in PIO FIFO */
    while (!pio_sm_is_rx_fifo_empty(pio, sm))
        (void)pio_sm_get(pio, sm);
}

static void restart_capture(protocol_mode_t mode)
{
    stop_capture();
    start_capture(mode);
}

/* ═══════════════════════════════════════════════════════════════════ */
/*  Host command processing                                           */
/* ═══════════════════════════════════════════════════════════════════ */
/*
 * Simple single-byte command protocol (host → device):
 *   'N' — switch to Nichia mode (12.5 Mbps)
 *   'O' — switch to Osram mode (20 Mbps)
 *   'S' — query status (device responds with mode + stats)
 *   'R' — reset statistics
 *   'B' — reboot into USB bootloader (BOOTSEL mode) for firmware update
 */

static void process_host_commands(void)
{
    if (!tud_cdc_available()) return;

    uint8_t cmd;
    if (tud_cdc_read(&cmd, 1) != 1) return;

    switch (cmd)
    {
        case 'N': case 'n':
            restart_capture(MODE_NICHIA);
            break;

        case 'O': case 'o':
            restart_capture(MODE_OSRAM);
            break;

        case 'S': case 's':
        {
            char status[80];
            int len = snprintf(status, sizeof(status),
                "MODE=%s BAUD=%u BYTES=%u\n",
                current_mode == MODE_NICHIA ? "NICHIA" : "OSRAM",
                current_mode == MODE_NICHIA ? BAUD_NICHIA : BAUD_OSRAM,
                total_bytes);
            tud_cdc_write(status, len);
            tud_cdc_write_flush();
            break;
        }

        case 'R': case 'r':
            total_bytes = 0;
            break;

        case 'B': case 'b':
            /* Reboot into USB bootloader (BOOTSEL mode).
             * The COM port will disconnect, and the Pico 2 will appear
             * as a USB mass-storage drive (RPI-RP2) for UF2 flashing.
             * Parameters: (0, 0) = no activity LED, enable both USB and PICOBOOT. */
            tud_cdc_write_str("BOOT\n");
            tud_cdc_write_flush();
            sleep_ms(50);  /* Give USB time to flush */
            reset_usb_boot(0, 0);
            /* Never returns */
            break;

        default:
            break;
    }
}

/* ═══════════════════════════════════════════════════════════════════ */
/*  PIO → USB data pump                                               */
/* ═══════════════════════════════════════════════════════════════════ */
/*
 * Polling approach: read PIO RX FIFO in tight loop, batch into a
 * local buffer, then write to USB CDC.
 *
 * Why polling (not DMA)?
 * - PIO UART RX pushes one 32-bit word per byte (only bottom 8 bits valid)
 * - DMA would transfer 32-bit words, wasting 3x bandwidth on USB
 * - At ≤2 MB/s, polling is fast enough for the 150 MHz dual-core RP2350
 * - Simpler code, easier to debug
 *
 * A DMA approach with byte packing could be added later if needed.
 */

static void flush_pio_to_usb(void)
{
    if (!tud_cdc_connected()) return;

    uint8_t  batch[512];
    uint32_t count = 0;

    /* Drain PIO FIFO into local batch buffer */
    while (!pio_sm_is_rx_fifo_empty(pio, sm) && count < sizeof(batch))
    {
        uint32_t word = pio_sm_get(pio, sm);
        batch[count++] = (uint8_t)(word & 0xFF);
    }

    if (count == 0) return;

    total_bytes += count;

    /* Write to USB CDC TX buffer.
     * If the USB buffer is full, we drop the oldest data to avoid
     * blocking the PIO read loop (FIFO overflow is worse than USB stall).
     */
    uint32_t avail = tud_cdc_write_available();
    if (avail > 0)
    {
        uint32_t to_write = (count < avail) ? count : avail;
        tud_cdc_write(batch, to_write);
        tud_cdc_write_flush();
    }
}

/* ═══════════════════════════════════════════════════════════════════ */
/*  LED status indicator                                              */
/* ═══════════════════════════════════════════════════════════════════ */

static void update_led(void)
{
    uint32_t now = to_ms_since_boot(get_absolute_time());

    if (!tud_cdc_connected())
    {
        /* USB not connected → LED off */
        gpio_put(LED_PIN, 0);
        return;
    }

    if (total_bytes > 0)
    {
        /* Data flowing → LED solid on */
        gpio_put(LED_PIN, 1);
    }
    else
    {
        /* Connected but no data → blink 2 Hz */
        if (now - last_led_time >= 250)
        {
            led_state = !led_state;
            gpio_put(LED_PIN, led_state);
            last_led_time = now;
        }
    }
}

/* ═══════════════════════════════════════════════════════════════════ */
/*  TinyUSB callbacks                                                 */
/* ═══════════════════════════════════════════════════════════════════ */

/* Invoked when CDC line state changes (DTR/RTS) */
void tud_cdc_line_state_cb(uint8_t itf, bool dtr, bool rts)
{
    (void)itf;
    (void)rts;

    if (dtr)
    {
        /* Host opened the port — reset stats */
        total_bytes = 0;
    }
}

/* Invoked when CDC line coding changes (baud rate, parity, etc.)
 * We ignore this — baud rate is controlled by our 'N'/'O' commands,
 * not by the host's serial port settings.
 */
void tud_cdc_line_coding_cb(uint8_t itf, cdc_line_coding_t const *p_line_coding)
{
    (void)itf;
    (void)p_line_coding;
}
