#ifndef DMA_SANITY_H
#define DMA_SANITY_H

#include "Ifx_Types.h"

/* Enables the DMA kernel clock (CLC). Safe to call multiple times. */
void dma_enable_module(void);

/* Run a single DMA memory-to-memory transfer (64 bytes) using SW request.
 * Returns TRUE on success (DMA completes and data matches), FALSE otherwise. */
boolean dma_sanity_run_once(void);

/* Debug counters visible in Watch */
extern volatile uint32 g_dmaSanity_ok;
extern volatile uint32 g_dmaSanity_err;

#endif /* DMA_SANITY_H */
