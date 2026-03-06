#ifndef ASCLIN9_DMA_H
#define ASCLIN9_DMA_H

#include "Ifx_Types.h"
#include "Dma/Dma/IfxDma_Dma.h"
#include "asclin9_rx.h"              /* Asclin9_FrameMode */

/**
 * @file asclin9_dma.h
 * @brief ASCLIN9 RX with DMA + dual buffer (ping-pong) for high-speed acquisition.
 *
 * This module replaces SW FIFO polling with zero-copy DMA transfers:
 * - ASCLIN9 RX (P14.7, 12.5 Mbaud) → HDMA directly to RAM
 * - Dual buffers: while parser consumes buffer A, DMA fills buffer B
 * - DMA completion interrupt signals when each buffer is full
 *
 * DMA reads 8-bit data from ASCLIN RXDATA register. The source address is
 * kept fixed via TC3xx circular buffer mode with CBLS=0 (no address modification).
 *
 * Dual buffer sizing:
 * - Each Nichia frame = 260 bytes (0x5D + row + 256px + CRC16)
 * - BUFFER_SIZE = 2560 bytes = ~10 frames → balances ISR overhead vs latency
 */

/* ==================== Configuration ==================== */

/** DMA buffer size (bytes per ping-pong buffer). Multiple of 260 (frame size). */
#define ASCLIN9_DMA_BUFFER_SIZE   (2560u)  /* 10 frames * 260 bytes */

/** DMA ISR priority for channel completion (below ASCLIN error ISR). */
#define ASCLIN9_DMA_ISR_PRIO      (13u)

/** DMA channel to use. Change if channel 0 is already occupied. */
#define ASCLIN9_DMA_CHANNEL_ID    IfxDma_ChannelId_0

/* ==================== Handle ==================== */

typedef struct
{
    /* Dual ping-pong buffers (aligned for DMA) */
    uint8 bufferA[ASCLIN9_DMA_BUFFER_SIZE];
    uint8 bufferB[ASCLIN9_DMA_BUFFER_SIZE];

    /* Guard zone: absorbs any stray DMA writes past bufferB.
     * With single-shot mode this should never happen, but the padding
     * costs only 32 bytes and protects dmaHandle/dmaChannel from
     * corruption in all edge cases. */
    uint8 _guard[32];

    /* DMA resources */
    IfxDma_Dma          dmaHandle;
    IfxDma_Dma_Channel  dmaChannel;

    /* Ping-pong bookkeeping */
    uint8           *pCurrentDest;      /**< Points to bufferA or bufferB (current DMA target) */
    volatile uint8  *pCompletedBuffer;  /**< Non-NULL when a buffer is ready for the parser */
    volatile uint32  completionCount;   /**< Total DMA buffer completions */
    uint32           timeoutWarnings;   /**< Consumer-lag warnings */
} Asclin9Dma;

extern Asclin9Dma g_asclin9_dma;

/* ==================== API ==================== */

/**
 * @brief Initialize ASCLIN9 + DMA with dual-buffer ping-pong.
 * @param baud_bps  Baud rate (e.g., 12500000 for 12.5 M).
 * @param frameMode Frame layout (8N1 or 8Odd1).
 */
void asclin9_dma_init(uint32 baud_bps, Asclin9_FrameMode frameMode);

/**
 * @brief Check if a DMA buffer is ready for the parser to consume.
 * @return Pointer to completed buffer (ASCLIN9_DMA_BUFFER_SIZE bytes),
 *         or NULL_PTR if no data yet.  Resets the ready flag.
 */
uint8* asclin9_dma_get_completed_buffer(void);

/**
 * @brief Query current DMA diagnostics.
 */
uint32 asclin9_dma_get_completion_count(void);
uint32 asclin9_dma_get_timeout_warnings(void);

#endif /* ASCLIN9_DMA_H */
