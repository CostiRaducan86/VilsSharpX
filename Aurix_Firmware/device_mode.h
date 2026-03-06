#ifndef DEVICE_MODE_H
#define DEVICE_MODE_H

/******************************************************************************
 * device_mode.h — LSM device type management for Aurix firmware
 *
 * Manages the active device type (Nichia or Osram) and coordinates the
 * reconfiguration of:
 *   - ASCLIN9 (baud rate + parity)
 *   - Frame parser selection (rxmon vs. osram_frame)
 *   - Ethernet TX parameters (magic, dimensions)
 *
 * Initial device type is set at startup via device_mode_init().
 * Runtime switching via device_mode_set() reconfigures all subsystems.
 *
 * Future: Ethernet RX for device mode commands from PC application.
 ******************************************************************************/

#include "Ifx_Types.h"
#include "frame_eth.h"       /* FrameEthDevice */

/* ─── ASCLIN9 parameters per device ─── */

/* Nichia: 12.5 Mbaud, 8N1, oversampling 8 */
#define DM_NICHIA_BAUD        12500000u

/* Osram:  20 Mbaud, 8O1 (odd parity), oversampling 5 */
#define DM_OSRAM_BAUD         20000000u

/* ─── API ─── */

/**
 * Initialize device mode at startup.
 * Configures ASCLIN9, parsers, and Ethernet for the specified device.
 * Call once from core0_main() after watchdog disable.
 *
 * @param device  Initial device type
 */
void device_mode_init(FrameEthDevice device);

/**
 * Switch to a different device type at runtime.
 *   1. Drains pending DMA buffers
 *   2. Reconfigures ASCLIN9 (baud + parity)
 *   3. Resets frame parsers (rxmon + osram_frame)
 *   4. Resets Ethernet frame assembly
 *
 * Safe to call even if already in the requested mode (no-op).
 *
 * @param device  New device type
 */
void device_mode_set(FrameEthDevice device);

/**
 * Query the current device mode.
 */
FrameEthDevice device_mode_get(void);

#endif /* DEVICE_MODE_H */
