/*
 * main.c - Frame-aware LVDS-to-USB-CDC bridge for Raspberry Pi Pico 2 (RP2350)
 *
 * Architecture:
 *   LVDS -> NBA3N012C -> TTL -> GPIO2 -> PIO UART RX -> byte-DMA -> ring
 *   -> CPU line parser -> frame assembler -> USB CDC -> PC
 *
 * Instead of blindly forwarding raw UART bytes to USB (which overflows
 * because UART rate > USB CDC throughput via RDP), the firmware parses
 * the LVDS line protocol on-chip, assembles complete frames, and sends
 * cooked frame packets to the host.
 *
 * Frame skipping handles the bandwidth mismatch gracefully:
 *   UART input:  849 KB/s  (260 B/line x 68 lines x 48 FPS, Nichia)
 *   USB output:  ~500 KB/s (USB FS CDC through RDP)
 *   Cooked frame: 16392 B  (8-byte header + 256x64 pixels)
 *   At 24 FPS:  393 KB/s   <- fits comfortably in USB budget
 *
 * Result: ~24 FPS of COMPLETE, CORRECT frames.
 *
 * USB cooked frame protocol:
 *   [0xFE] [0xED]                        - magic bytes
 *   [frame_id_lo] [frame_id_hi]          - 16-bit frame counter (LE)
 *   [width_lo] [width_hi]                - frame width (LE)
 *   [height_lo] [height_hi]              - active height (LE)
 *   [width x height bytes of pixel data] - row-major, grayscale
 *
 * Protocol modes (selected by host command over CDC):
 *   'N' = Nichia:  12,500,000 baud, 8N1, 8x oversampling, 256x64 active
 *   'O' = Osram:   20,000,000 baud, 8O1*, 4x oversampling, 320x80 active
 *
 * Hardware setup:
 *   - Pico 2 on gusmanb LogicAnalyzer level-shifting board
 *   - LVDS receiver (onsemi NBA3N012C) -> TTL -> Channel 1 (GPIO 2)
 *   - USB CDC virtual COM port to PC
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

/* Generated from uart_rx.pio */
#include "uart_rx.pio.h"

/* ----------------------------------------------------------------- */
/*  Configuration                                                     */
/* ----------------------------------------------------------------- */

#define UART_RX_PIN         2
#define LED_PIN             25

#define RING_BITS           15
#define RING_SIZE           (1u << RING_BITS)
#define RING_MASK           (RING_SIZE - 1)

#define SYNC_BYTE           0x5D

#define FRAME_MAGIC_0       0xFE
#define FRAME_MAGIC_1       0xED
#define FRAME_HDR_SIZE      8

typedef struct {
    uint16_t width;
    uint16_t active_height;
    uint16_t lvds_height;
    uint16_t line_size;
    uint32_t baud;
} lvds_proto_t;

#define BAUD_NICHIA  12500000
#define BAUD_OSRAM   20000000

static const lvds_proto_t PROTO_NICHIA = { 256, 64, 68, 260, BAUD_NICHIA };
static const lvds_proto_t PROTO_OSRAM  = { 320, 80, 84, 324, BAUD_OSRAM };
static const lvds_proto_t *proto = &PROTO_NICHIA;

/* ----------------------------------------------------------------- */
/*  Globals                                                           */
/* ----------------------------------------------------------------- */

static PIO  pio     = pio0;
static uint sm      = 0;
static int  dma_chan = -1;

static uint8_t  ring_buf[RING_SIZE] __attribute__((aligned(RING_SIZE)));
static uint32_t ring_rd = 0;

typedef enum { MODE_NICHIA = 0, MODE_OSRAM = 1 } protocol_mode_t;
static protocol_mode_t current_mode = MODE_NICHIA;

/* Double-buffered frame assembly (max: Osram 320x84 = 26880 B) */
#define MAX_FRAME_BYTES  (320 * 84)

static uint8_t  fb_a[MAX_FRAME_BYTES];
static uint8_t  fb_b[MAX_FRAME_BYTES];
static uint8_t *asm_fb  = fb_a;    /* assembling into */
static uint8_t *send_fb = NULL;    /* sending from (NULL = idle) */

static bool     line_placed[84];
static int      lines_placed = 0;
static int      prev_row = -1;

/* USB send state */
static uint32_t send_total  = 0;
static uint32_t send_offset = 0;
static uint8_t  send_hdr[FRAME_HDR_SIZE];

/* Line parser state machine */
typedef enum { SCAN_SYNC, READ_LINE, SCAN_GAP } parse_state_t;
static parse_state_t ps = SCAN_SYNC;
static uint8_t  line_data[324];   /* max line (Osram) */
static int      line_pos = 0;
static int      gap_budget = 0;       /* bytes left to scan in SCAN_GAP */
static bool     frame_locked = false; /* true after first valid line */

/* Max gap bytes between lines before declaring loss of sync.
 * LVDS inter-line idle periods can insert 0-~20 null bytes. */
#define MAX_GAP_BYTES  64

/* Extract row address from raw row byte.
 *   Nichia: [odd_parity:1][row_addr:7]  row 0 = 0x80
 *   Osram:  raw row number (parity handled by 8O1 UART) */
static inline int extract_row(uint8_t raw) {
    return (current_mode == MODE_NICHIA) ? (raw & 0x7F) : raw;
}

/* Statistics */
static uint32_t fw_frame_id     = 0;
static uint32_t frames_sent     = 0;
static uint32_t frames_dropped  = 0;
static uint32_t crc_errors      = 0;
static uint32_t crc_ok_lines    = 0;  /* lines placed with valid CRC */
static uint32_t gap_bytes_total = 0;  /* total gap/idle bytes skipped */
static uint32_t gap_resyncs     = 0;  /* times gap exceeded budget -> rescan */
static uint32_t total_usb_bytes = 0;
static uint32_t max_fill        = 0;

static uint32_t last_led_time   = 0;
static bool     led_state       = false;

/* ----------------------------------------------------------------- */
/*  Forward declarations                                              */
/* ----------------------------------------------------------------- */

static void start_pio(protocol_mode_t mode);
static void stop_pio(void);
static void start_dma(void);
static void stop_dma(void);
static void restart_capture(protocol_mode_t mode);
static void reset_frame_state(void);
static void process_host_commands(void);
static void parse_ring_data(void);
static bool handle_complete_line(void);   /* returns true if CRC ok */
static void emit_assembled_frame(void);
static void send_frame_chunk(void);
static void update_led(void);
static inline uint32_t get_dma_wr(void);

/* ----------------------------------------------------------------- */
/*  CRC-16/CCITT-FALSE  (poly 0x1021, init 0xFFFF, no reflection)    */
/* ----------------------------------------------------------------- */

static uint16_t crc16_table[256];

static void init_crc16_table(void)
{
    for (int i = 0; i < 256; i++) {
        uint16_t crc = (uint16_t)(i << 8);
        for (int j = 0; j < 8; j++) {
            if (crc & 0x8000) crc = (crc << 1) ^ 0x1021;
            else              crc <<= 1;
        }
        crc16_table[i] = crc;
    }
}

static uint16_t crc16_ccitt(const uint8_t *data, int len)
{
    uint16_t crc = 0xFFFF;
    for (int i = 0; i < len; i++) {
        uint8_t idx = (crc >> 8) ^ data[i];
        crc = (crc << 8) ^ crc16_table[idx];
    }
    return crc;
}

/* ================================================================= */
/*  Main                                                              */
/* ================================================================= */

int main(void)
{
    board_init();
    gpio_init(LED_PIN);
    gpio_set_dir(LED_PIN, GPIO_OUT);
    tusb_init();
    init_crc16_table();

    start_pio(MODE_NICHIA);
    start_dma();

    while (1)
    {
        parse_ring_data();
        send_frame_chunk();
        tud_task();
        send_frame_chunk();
        tud_task();

        if (dma_channel_hw_addr(dma_chan)->transfer_count == 0)
            dma_channel_set_trans_count(dma_chan, 0x0FFFFFFF, true);

        process_host_commands();
        update_led();
    }
    return 0;
}

/* ================================================================= */
/*  Line Parser + Frame Assembler                                     */
/* ================================================================= */

static void parse_ring_data(void)
{
    uint32_t wr = get_dma_wr();
    uint32_t rd = ring_rd;
    int budget = 8192;

    while (rd != wr && budget-- > 0)
    {
        uint8_t b = ring_buf[rd];
        rd = (rd + 1) & RING_MASK;

        switch (ps)
        {
        case SCAN_SYNC:
            /* Cold scan: byte-by-byte search for 0x5D.
             * Only used at startup or after total loss of alignment. */
            if (b == SYNC_BYTE) {
                line_data[0] = b;
                line_pos = 1;
                ps = READ_LINE;
                frame_locked = false;
            }
            break;

        case SCAN_GAP:
            /* After a valid line, scan through inter-line gap/idle bytes
             * looking for the next 0x5D.  Gap bytes are typically 0x00
             * (LVDS idle), so there is no risk of false 0x5D matches.
             * This handles protocols with variable inter-line padding. */
            if (b == SYNC_BYTE) {
                line_data[0] = b;
                line_pos = 1;
                ps = READ_LINE;
            } else {
                gap_bytes_total++;
                gap_budget--;
                if (gap_budget <= 0) {
                    gap_resyncs++;
                    frame_locked = false;
                    ps = SCAN_SYNC;
                }
            }
            break;

        case READ_LINE:
            line_data[line_pos++] = b;

            /* Early reject: invalid row after masking */
            if (line_pos == 2) {
                int rowChk = extract_row(b);
                if (rowChk >= proto->lvds_height) {
                    if (frame_locked) {
                        /* Aligned but row byte is bad - scan gap for next sync */
                        gap_budget = MAX_GAP_BYTES + proto->line_size;
                        ps = SCAN_GAP;
                    } else {
                        /* From cold SCAN_SYNC - false sync on pixel 0x5D */
                        if (b == SYNC_BYTE) {
                            line_data[0] = b;
                            line_pos = 1;
                        } else {
                            ps = SCAN_SYNC;
                            line_pos = 0;
                        }
                    }
                    break;
                }
            }

            if (line_pos >= proto->line_size) {
                bool crc_ok = handle_complete_line();
                line_pos = 0;
                if (crc_ok) {
                    /* CRC passed => alignment is correct */
                    frame_locked = true;
                    gap_budget = MAX_GAP_BYTES;
                } else {
                    /* CRC failed => likely false 0x5D match in gap.
                     * Don't trust alignment; scan for real next sync
                     * with extended budget (gap + one full line). */
                    gap_budget = MAX_GAP_BYTES + proto->line_size;
                }
                ps = SCAN_GAP;
            }
            break;
        }
    }

    uint32_t fill = (wr >= rd) ? (wr - rd) : (RING_SIZE - rd + wr);
    if (fill > max_fill) max_fill = fill;
    ring_rd = rd;
}

static bool handle_complete_line(void)
{
    /* Extract row address (Nichia: mask off parity bit) */
    int row = extract_row(line_data[1]);

    /* CRC validation: only place lines with correct CRC.
     * False 0x5D matches in gap data produce misaligned lines
     * whose CRC will almost certainly fail. */
    uint16_t crc_exp = ((uint16_t)line_data[proto->line_size - 2] << 8)
                     | line_data[proto->line_size - 1];
    uint16_t crc_got = crc16_ccitt(line_data + 2, proto->width);

    if (crc_got != crc_exp) {
        crc_errors++;
        return false;   /* don't place; caller will resync */
    }

    crc_ok_lines++;

    /* Frame boundary: row decreased -> new frame */
    if (row <= prev_row && prev_row >= 0 && lines_placed > 0)
        emit_assembled_frame();

    prev_row = row;

    if (row < proto->active_height) {
        memcpy(asm_fb + row * proto->width,
               line_data + 2, proto->width);
        if (!line_placed[row]) {
            line_placed[row] = true;
            lines_placed++;
        }
    }
    return true;
}

static void emit_assembled_frame(void)
{
    fw_frame_id++;

    if (send_fb == NULL) {
        /* Swap buffers and start sending */
        send_fb = asm_fb;
        send_offset = 0;
        uint16_t w = proto->width;
        uint16_t h = proto->active_height;
        send_total = FRAME_HDR_SIZE + (uint32_t)w * h;

        send_hdr[0] = FRAME_MAGIC_0;
        send_hdr[1] = FRAME_MAGIC_1;
        send_hdr[2] = fw_frame_id & 0xFF;
        send_hdr[3] = (fw_frame_id >> 8) & 0xFF;
        send_hdr[4] = w & 0xFF;
        send_hdr[5] = (w >> 8) & 0xFF;
        send_hdr[6] = h & 0xFF;
        send_hdr[7] = (h >> 8) & 0xFF;

        frames_sent++;
        asm_fb = (asm_fb == fb_a) ? fb_b : fb_a;
    } else {
        frames_dropped++;
    }

    /* Clear the new assembly buffer */
    memset(line_placed, 0, proto->lvds_height * sizeof(bool));
    memset(asm_fb, 0, (uint32_t)proto->width * proto->active_height);
    lines_placed = 0;
}

/* ================================================================= */
/*  USB Frame Sender (non-blocking)                                   */
/* ================================================================= */

static void send_frame_chunk(void)
{
    if (send_fb == NULL) return;
    if (!tud_cdc_connected()) { send_fb = NULL; return; }

    for (int pass = 0; pass < 4; pass++)
    {
        uint32_t avail = tud_cdc_write_available();
        if (avail == 0) break;

        if (send_offset < FRAME_HDR_SIZE) {
            uint32_t rem = FRAME_HDR_SIZE - send_offset;
            uint32_t chunk = (avail < rem) ? avail : rem;
            uint32_t w = tud_cdc_write(send_hdr + send_offset, chunk);
            send_offset += w;
            total_usb_bytes += w;
            if (w < chunk) break;
            avail -= w;
            if (avail == 0) break;
        }

        uint32_t pix_off   = send_offset - FRAME_HDR_SIZE;
        uint32_t pix_total = (uint32_t)proto->width * proto->active_height;
        if (pix_off < pix_total) {
            uint32_t rem = pix_total - pix_off;
            uint32_t chunk = (avail < rem) ? avail : rem;
            uint32_t w = tud_cdc_write(send_fb + pix_off, chunk);
            send_offset += w;
            total_usb_bytes += w;
            if (w < chunk) break;
        }

        if (send_offset >= send_total) {
            send_fb = NULL;
            break;
        }
    }
    tud_cdc_write_flush();
}

/* ================================================================= */
/*  DMA: byte-width from PIO FIFO byte 3                             */
/* ================================================================= */

static void start_dma(void)
{
    if (dma_chan < 0)
        dma_chan = dma_claim_unused_channel(true);

    dma_channel_config c = dma_channel_get_default_config(dma_chan);
    channel_config_set_transfer_data_size(&c, DMA_SIZE_8);
    channel_config_set_read_increment(&c, false);
    channel_config_set_write_increment(&c, true);
    channel_config_set_dreq(&c, pio_get_dreq(pio, sm, false));
    channel_config_set_ring(&c, true, RING_BITS);

    memset(ring_buf, 0, sizeof(ring_buf));
    ring_rd  = 0;
    max_fill = 0;

    const volatile uint8_t *pio_rxf_byte3 =
        (const volatile uint8_t *)&pio->rxf[sm] + 3;

    dma_channel_configure(dma_chan, &c,
        ring_buf, pio_rxf_byte3,
        0x0FFFFFFF, true);
}

static void stop_dma(void)
{
    if (dma_chan >= 0)
        dma_channel_abort(dma_chan);
}

static inline uint32_t get_dma_wr(void)
{
    uint32_t wa = dma_channel_hw_addr(dma_chan)->write_addr;
    return (wa - (uintptr_t)ring_buf) & RING_MASK;
}

/* ================================================================= */
/*  PIO capture                                                       */
/* ================================================================= */

static void start_pio(protocol_mode_t mode)
{
    current_mode = mode;
    if (mode == MODE_NICHIA) {
        proto = &PROTO_NICHIA;
        uint off = pio_add_program(pio, &uart_rx_8x_program);
        uart_rx_8x_program_init(pio, sm, off, UART_RX_PIN, BAUD_NICHIA);
    } else {
        proto = &PROTO_OSRAM;
        uint off = pio_add_program(pio, &uart_rx_4x_program);
        uart_rx_4x_program_init(pio, sm, off, UART_RX_PIN, BAUD_OSRAM);
    }
}

static void stop_pio(void)
{
    pio_sm_set_enabled(pio, sm, false);
    pio_clear_instruction_memory(pio);
    while (!pio_sm_is_rx_fifo_empty(pio, sm))
        (void)pio_sm_get(pio, sm);
}

static void restart_capture(protocol_mode_t mode)
{
    stop_dma();
    stop_pio();
    reset_frame_state();
    start_pio(mode);
    start_dma();
}

static void reset_frame_state(void)
{
    ps = SCAN_SYNC;
    line_pos = 0;
    gap_budget = 0;
    frame_locked = false;
    prev_row = -1;
    lines_placed = 0;
    memset(line_placed, 0, sizeof(line_placed));
    memset(fb_a, 0, sizeof(fb_a));
    memset(fb_b, 0, sizeof(fb_b));
    asm_fb = fb_a;
    send_fb = NULL;
    send_offset = 0;
}

/* ================================================================= */
/*  Host commands                                                     */
/* ================================================================= */

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
        stop_dma();
        pio_sm_set_enabled(pio, sm, false);
        ps = SCAN_SYNC;
        line_pos = 0;
        ring_rd = 0;
        if (send_fb) { send_fb = NULL; send_offset = 0; }

        tud_cdc_write_flush();
        for (int d = 0; d < 100; d++) {
            tud_task();
            if (tud_cdc_write_available() >= 256) break;
            sleep_us(200);
        }

        char status[300];
        int len = snprintf(status, sizeof(status),
            "MODE=%s BAUD=%u USB=%u SENT=%u DROP=%u CRC_OK=%u CRC_ERR=%u GAP=%u RESYNC=%u MAXFILL=%u/%u\n",
            current_mode == MODE_NICHIA ? "NICHIA" : "OSRAM",
            proto->baud,
            total_usb_bytes, frames_sent, frames_dropped,
            crc_ok_lines, crc_errors, gap_bytes_total, gap_resyncs,
            max_fill, RING_SIZE);
        tud_cdc_write(status, len);
        tud_cdc_write_flush();

        for (int d = 0; d < 50; d++) {
            tud_task();
            sleep_us(200);
        }

        while (!pio_sm_is_rx_fifo_empty(pio, sm))
            (void)pio_sm_get(pio, sm);
        pio_sm_set_enabled(pio, sm, true);
        start_dma();
        break;
    }

    case 'R': case 'r':
        total_usb_bytes = 0;
        frames_sent = 0;
        frames_dropped = 0;
        crc_errors = 0;
        crc_ok_lines = 0;
        gap_bytes_total = 0;
        gap_resyncs = 0;
        max_fill = 0;
        break;

    case 'B': case 'b':
        stop_dma();
        tud_cdc_write_str("BOOT\n");
        tud_cdc_write_flush();
        sleep_ms(50);
        reset_usb_boot(0, 0);
        break;

    default: break;
    }
}

/* ================================================================= */
/*  LED                                                               */
/* ================================================================= */

static void update_led(void)
{
    uint32_t now = to_ms_since_boot(get_absolute_time());
    if (!tud_cdc_connected()) { gpio_put(LED_PIN, 0); return; }
    if (frames_sent > 0) {
        gpio_put(LED_PIN, 1);
    } else if (now - last_led_time >= 250) {
        led_state = !led_state;
        gpio_put(LED_PIN, led_state);
        last_led_time = now;
    }
}

/* ================================================================= */
/*  TinyUSB callbacks                                                 */
/* ================================================================= */

void tud_cdc_line_state_cb(uint8_t itf, bool dtr, bool rts)
{
    (void)itf; (void)rts;
    if (dtr) { total_usb_bytes = 0; frames_sent = 0; frames_dropped = 0; }
}

void tud_cdc_line_coding_cb(uint8_t itf, cdc_line_coding_t const *p_line_coding)
{
    (void)itf; (void)p_line_coding;
}
