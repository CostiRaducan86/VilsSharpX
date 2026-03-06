/******************************************************************************
 * device_mode.c — LSM device type management
 *
 * Coordinates ASCLIN9, frame parsers, and Ethernet TX when switching
 * between Nichia (12.5 Mbaud, 8N1) and Osram (20 Mbaud, 8O1).
 *
 * At startup, device_mode_init() is called with the desired mode.
 * Runtime switching via device_mode_set() reconfigures all subsystems.
 ******************************************************************************/

#include "device_mode.h"
#include "asclin9_dma.h"
#include "rxmon.h"
#include "osram_frame.h"

/* ==================== Internal state ==================== */

static FrameEthDevice s_currentDevice = FE_DEVICE_NICHIA;

/* ==================== Implementation ==================== */

void device_mode_init(FrameEthDevice device)
{
    s_currentDevice = device;

    /* Initialise parser for the selected device */
    if (device == FE_DEVICE_OSRAM)
    {
        osram_frame_init();   /* also calls osram_crc32_init() */
    }
    else
    {
        rxmon_reset();
    }

    /* Configure ASCLIN9 for the selected device */
    if (device == FE_DEVICE_OSRAM)
    {
        asclin9_dma_init(DM_OSRAM_BAUD, Frame_8Odd1);
    }
    else
    {
        asclin9_dma_init(DM_NICHIA_BAUD, Frame_8N1);
    }

    /* Initialise GETH + PHY for Ethernet TX */
    frame_eth_init(device);
}

void device_mode_set(FrameEthDevice device)
{
    if (device == s_currentDevice)
        return;

    /* 1. Drain any pending DMA buffer (ignore it) */
    g_asclin9_dma.pCompletedBuffer = NULL_PTR;

    /* 2. Reconfigure ASCLIN9 (baudrate + parity).
     *    asclin9_dma_init() disables interrupts, reprograms ASCLIN + DMA,
     *    then re-enables interrupts.  Safe to call again. */
    if (device == FE_DEVICE_OSRAM)
    {
        asclin9_dma_init(DM_OSRAM_BAUD, Frame_8Odd1);
    }
    else
    {
        asclin9_dma_init(DM_NICHIA_BAUD, Frame_8N1);
    }

    /* 3. Reset frame parsers */
    rxmon_reset();
    osram_frame_reset();

    /* 4. Update Ethernet TX parameters + reset frame assembly */
    frame_eth_set_device(device);
    frame_eth_reset_frame_state();

    s_currentDevice = device;
}

FrameEthDevice device_mode_get(void)
{
    return s_currentDevice;
}
