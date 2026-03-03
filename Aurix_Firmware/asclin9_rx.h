#ifndef ASCLIN9_RX_H
#define ASCLIN9_RX_H

#include "Ifx_Types.h"

/**
 * @brief Frame mode selector for parity configuration.
 */
typedef enum
{
    Frame_8N1 = 0,  /* 8 data bits, No parity, 1 stop bit */
    Frame_8Odd1     /* 8 data bits, Odd parity, 1 stop bit */
} Asclin9_FrameMode;

/**
 * @brief Initialize ASCLIN9 in RX-only mode on P14.7 (X103 pin 8).
 * @param baud_bps  Baud rate in bit/s.
 * @param frameMode Frame layout (8N1 / 8Odd1).
 */
void asclin9_init(uint32 baud_bps, Asclin9_FrameMode frameMode);

/**
 * @brief Reconfigure baudrate/frame-mode while receiving.
 */
void asclin9_set_baudrate(uint32 baud_bps, Asclin9_FrameMode frameMode);

/**
 * @brief Drain all bytes available in the ASCLIN SW FIFO and pass to callback.
 */
void asclin9_consume_ready_buffers(void (*consume)(const uint8 *data, uint32 len));

#endif /* ASCLIN9_RX_H */
