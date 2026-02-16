/*
 * tusb_config.h — TinyUSB configuration for LVDS-to-USB-CDC bridge
 *
 * Single CDC interface: Pico 2 appears as a virtual COM port.
 * Data is raw LVDS UART bytes (no additional framing at USB level).
 */

#ifndef _TUSB_CONFIG_H_
#define _TUSB_CONFIG_H_

#ifdef __cplusplus
extern "C" {
#endif

/* ── MCU / board ─────────────────────────────────────────────────── */
#define CFG_TUSB_MCU           OPT_MCU_RP2040   /* works for RP2350 too */
#define CFG_TUSB_OS            OPT_OS_NONE
#define CFG_TUSB_RHPORT0_MODE  OPT_MODE_DEVICE

/* ── Device class ────────────────────────────────────────────────── */
#define CFG_TUD_CDC            1
#define CFG_TUD_MSC            0
#define CFG_TUD_HID            0
#define CFG_TUD_MIDI           0
#define CFG_TUD_VENDOR         0

/* ── CDC buffer sizes ────────────────────────────────────────────── */
/*
 * At 20 Mbps UART → ~2 MB/s raw data.  USB FS max throughput is
 * ~1 MB/s.  However the actual LVDS duty cycle is much lower
 * (line gaps + frame gaps), so effective data rate is manageable.
 *
 * Use large TX buffer to avoid blocking PIO read loop.
 */
#define CFG_TUD_CDC_RX_BUFSIZE   512    /* host → device (commands) */
#define CFG_TUD_CDC_TX_BUFSIZE   4096   /* device → host (UART data) */

/* ── Endpoint sizes ──────────────────────────────────────────────── */
#define CFG_TUD_CDC_EP_BUFSIZE   64     /* USB Full Speed bulk EP */

#ifdef __cplusplus
}
#endif

#endif /* _TUSB_CONFIG_H_ */
