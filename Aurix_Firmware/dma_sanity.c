#include "dma_sanity.h"
#include "Dma/Dma/IfxDma_Dma.h"
#include "IfxCpu.h"       /* IFX_ALIGN, IFXCPU_GLB_ADDR_DSPR */
#include "IfxScuWdt.h"    /* safety endinit */
#include <string.h>       /* memset, memcmp */

/* Debug counters for Watch */
volatile uint32 g_dmaSanity_ok  = 0;
volatile uint32 g_dmaSanity_err = 0;

/* Local test buffers (typically end up in DSPR); 32-byte aligned */
IFX_ALIGN(32) static uint8 s_src[64];
IFX_ALIGN(32) static uint8 s_dst[64];

/* Enable DMA kernel clock (clear CLC.DISR under safety ENDINIT) */
void dma_enable_module(void)
{
    uint16 passwd = IfxScuWdt_getSafetyWatchdogPassword();
    IfxScuWdt_clearSafetyEndinit(passwd);

    MODULE_DMA.CLC.B.DISR = 0;            /* request enable */
    (void)MODULE_DMA.CLC.U;               /* sync read */
    while (MODULE_DMA.CLC.B.DISS != 0)    /* wait until enabled */
    {
        /* spin */
    }

    IfxScuWdt_setSafetyEndinit(passwd);
}

boolean dma_sanity_run_once(void)
{
    /* Make sure DMA kernel is enabled before any register access */
    dma_enable_module();

    /* Fill source with a simple pattern; clear destination */
    for (uint32 i = 0; i < sizeof(s_src); ++i) s_src[i] = (uint8)(0xA5u ^ i);
    memset(s_dst, 0x00, sizeof(s_dst));

    /* DMA module & channel handles */
    IfxDma_Dma dma;
    IfxDma_Dma_Config dcfg;
    IfxDma_Dma_initModuleConfig(&dcfg, &MODULE_DMA);
    IfxDma_Dma_initModule(&dma, &dcfg);

    IfxDma_Dma_Channel ch;
    IfxDma_Dma_ChannelConfig ccfg;
    IfxDma_Dma_initChannelConfig(&ccfg, &dma);

    /* Use a free channel (adjust if used elsewhere) */
    ccfg.channelId                     = IfxDma_ChannelId_5;
    ccfg.hardwareRequestEnabled        = FALSE; /* SW request only */
    ccfg.channelInterruptEnabled       = TRUE;
    ccfg.channelInterruptPriority      = 5;     /* CPU0 prio */
    ccfg.channelInterruptTypeOfService = IfxSrc_Tos_cpu0;

    /* Map source/destination to GLOBAL DSPR addresses (DMA-accessible) */
    uint32 dsrc = IFXCPU_GLB_ADDR_DSPR(IfxCpu_getCoreId(), s_src);
    uint32 ddst = IFXCPU_GLB_ADDR_DSPR(IfxCpu_getCoreId(), s_dst);

    /* Source & destination settings */
    ccfg.sourceAddress                   = dsrc;
    ccfg.sourceCircularBufferEnabled     = FALSE;
    ccfg.sourceAddressCircularRange      = IfxDma_ChannelIncrementCircular_none;
    ccfg.sourceAddressIncrementStep      = IfxDma_ChannelIncrementStep_1;
    ccfg.sourceAddressIncrementDirection = IfxDma_ChannelIncrementDirection_positive;

    ccfg.destinationAddress                 = ddst;
    ccfg.destinationCircularBufferEnabled   = FALSE;
    ccfg.destinationAddressCircularRange    = IfxDma_ChannelIncrementCircular_none;
    ccfg.destinationAddressIncrementStep    = IfxDma_ChannelIncrementStep_1;
    ccfg.destinationAddressIncrementDirection= IfxDma_ChannelIncrementDirection_positive;

    /* One transaction of 64 x 8-bit moves, started by SW request */
    ccfg.moveSize      = IfxDma_ChannelMoveSize_8bit;
    ccfg.transferCount = (uint16)sizeof(s_src);
    ccfg.requestMode   = IfxDma_ChannelRequestMode_completeTransactionPerRequest; /* 1 SW req → full transaction */
    ccfg.operationMode = IfxDma_ChannelOperationMode_single;

    IfxDma_Dma_initChannel(&ch, &ccfg);

    /* Start by SW and wait for completion */
    IfxDma_Dma_startChannelTransaction(&ch);

    /* Busy wait for demo; production: use ISR or a proper timeout/tick */
    uint32 guard = 1000000;
    while ((IfxDma_Dma_getAndClearChannelInterrupt(&ch) == FALSE) && (--guard)) { }

    /* Verify content */
    boolean ok = (guard != 0) && (memcmp(s_src, s_dst, sizeof(s_src)) == 0);
    if (ok) { g_dmaSanity_ok++; return TRUE; }
    g_dmaSanity_err++; return FALSE;
}
