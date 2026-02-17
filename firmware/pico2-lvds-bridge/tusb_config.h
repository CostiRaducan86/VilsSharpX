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
#ifndef CFG_TUSB_OS
#define CFG_TUSB_OS            OPT_OS_NONE
#endif
#define CFG_TUSB_RHPORT0_MODE  OPT_MODE_DEVICE

/* ── Device class ────────────────────────────────────────────────── */
#define CFG_TUD_CDC            1
#define CFG_TUD_MSC            0
#define CFG_TUD_HID            0
#define CFG_TUD_MIDI           0
#define CFG_TUD_VENDOR         0

/* ── CDC buffer sizes ────────────────────────────────────────────── */
/*
 * At 12.5 Mbps UART, effective data rate is ~849 KB/s.
 * USB FS bulk max throughput is ~1 MB/s.
 *
 * Large TX buffer is critical: it absorbs USB jitter.  When the host
 * is busy processing a SOF frame, our tud_cdc_write() calls queue
 * data here.  A larger buffer means the DMA ring stays emptier,
 * preventing DMA wrap-around overwrite of unread data.
 *
 * TX buffer = 8192 bytes → ~9.6 ms of buffering at 849 KB/s.
 * Combined with DMA ring (6.5 ms), total buffering ≈ 16 ms.
 */
#define CFG_TUD_CDC_RX_BUFSIZE   512    /* host → device (commands) */
#define CFG_TUD_CDC_TX_BUFSIZE   8192   /* device → host (UART data) */

/* ── Endpoint sizes ──────────────────────────────────────────────── */
#define CFG_TUD_CDC_EP_BUFSIZE   64     /* USB Full Speed bulk EP */

#ifdef __cplusplus
}
#endif

#endif /* _TUSB_CONFIG_H_ */
