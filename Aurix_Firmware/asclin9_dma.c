/******************************************************************************
 * \file asclin9_dma.c
 * \brief ASCLIN9 RX with HDMA + dual buffer (zero-copy) for 12.5 Mbaud Nichia.
 *
 * Architecture:
 * - ASCLIN9 RX on P14.7 → HDMA transfers bytes directly to RAM
 * - Dual 2.56 KB buffers (A, B) in ping-pong mode
 * - ASCLIN RX service request routed to DMA (not CPU ISR)
 * - DMA completion ISR atomically swaps buffers & signals parser
 * - Main loop: consume completed buffer while DMA fills next one
 *
 * Source address fix:
 * - TC3xx DMA CBLS=0 with SCBE=1 → source address is never modified
 * - This allows 8-bit moves from the fixed ASCLIN RXDATA register
 ******************************************************************************/

#include "Ifx_Types.h"
#include "IfxCpu.h"
#include "Dma/Dma/IfxDma_Dma.h"
#include "Asclin/Asc/IfxAsclin_Asc.h"
#include "Asclin/Std/IfxAsclin.h"
#include "IfxAsclin_PinMap.h"
#include "IfxPort.h"
#include "IfxSrc.h"

#include "asclin9_dma.h"
#include "rxmon.h"

/* ===================== Module State ===================== */
IFX_ALIGN(32) Asclin9Dma g_asclin9_dma;

/* ASCLIN handle (used for baudrate/pin config; RX data path is DMA) */
static IfxAsclin_Asc g_asc9;

/* ===================== DMA Completion ISR ===================== */

/**
 * Fires when DMA has transferred ASCLIN9_DMA_BUFFER_SIZE bytes into a buffer.
 * Actions:
 *  1. Clear DMA channel interrupt.
 *  2. Swap destination buffer (ping-pong).
 *  3. Re-program DMA destination address + transfer count for next buffer.
 *  4. Signal main loop that a buffer is ready.
 */
IFX_INTERRUPT(ASCLIN9_DMA_ISR, 0, ASCLIN9_DMA_ISR_PRIO)
{
    /* 1. Clear channel interrupt flag */
    IfxDma_Dma_clearChannelInterrupt(&g_asclin9_dma.dmaChannel);

    /* 2. Identify which buffer just completed */
    uint8 *old_dest = g_asclin9_dma.pCurrentDest;

    if (old_dest == g_asclin9_dma.bufferA)
    {
        g_asclin9_dma.pCurrentDest     = g_asclin9_dma.bufferB;
        g_asclin9_dma.pCompletedBuffer = g_asclin9_dma.bufferA;
    }
    else
    {
        g_asclin9_dma.pCurrentDest     = g_asclin9_dma.bufferA;
        g_asclin9_dma.pCompletedBuffer = g_asclin9_dma.bufferB;
    }

    /* 3. Re-program DMA for the next buffer */
    IfxDma_Dma_setChannelDestinationAddress(&g_asclin9_dma.dmaChannel,
                                            (uint32)g_asclin9_dma.pCurrentDest);
    IfxDma_Dma_setChannelTransferCount(&g_asclin9_dma.dmaChannel,
                                       ASCLIN9_DMA_BUFFER_SIZE);

    /* 4. Bump completion counter */
    g_asclin9_dma.completionCount++;
}

/* ===================== ASCLIN9 Configuration ===================== */

/**
 * Configure ASCLIN9 for RX-only, with the RX service request routed to DMA.
 *
 * Key differences from the interrupt-driven asclin9_rx.c version:
 *  - rxPriority = 0, erPriority = 0, tos = cpu0 (let iLLD skip all SRC setup)
 *  - After initModule, we manually enable RFLE + route RX SRC to DMA
 *  - This avoids iLLD also enabling TX SRC for DMA (TX FIFO empty → infinite triggers)
 *  - FIFO interrupt level = 1 (trigger DMA on every received byte)
 *  - Minimal SW buffers (iLLD needs them but data goes via DMA)
 */
static void asclin9_dma_configure(uint32 baud, Asclin9_FrameMode frameMode)
{
    IfxAsclin_Asc_Config cfg;
    IfxAsclin_Asc_initModuleConfig(&cfg, &MODULE_ASCLIN9);

    /* Clock */
    cfg.clockSource = IfxAsclin_ClockSource_ascFastClock;

    /* Baud rate */
    cfg.baudrate.baudrate     = (float32)baud;
    cfg.baudrate.prescaler    = 1;
    cfg.baudrate.oversampling = IfxAsclin_OversamplingFactor_8;

    /* Bit timing */
    cfg.bitTiming.samplePointPosition = IfxAsclin_SamplePointPosition_3;
    cfg.bitTiming.medianFilter        = IfxAsclin_SamplesPerBit_three;

    /* Frame format */
    cfg.frame.dataLength = IfxAsclin_DataLength_8;
    cfg.frame.stopBit    = IfxAsclin_StopBit_1;
    cfg.frame.frameMode  = IfxAsclin_FrameMode_asc;
    cfg.frame.shiftDir   = IfxAsclin_ShiftDirection_lsbFirst;

    if (frameMode == Frame_8Odd1)
    {
        cfg.frame.parityBit  = TRUE;
        cfg.frame.parityType = IfxAsclin_ParityType_odd;
    }
    else
    {
        cfg.frame.parityBit  = FALSE;
        cfg.frame.parityType = IfxAsclin_ParityType_even;
    }

    /* FIFO: use HW FIFO, trigger SRC every byte for DMA */
    cfg.fifo.inWidth              = IfxAsclin_TxFifoInletWidth_1;
    cfg.fifo.outWidth             = IfxAsclin_RxFifoOutletWidth_1;
    cfg.fifo.rxFifoInterruptLevel = IfxAsclin_RxFifoInterruptLevel_1;
    cfg.fifo.txFifoInterruptLevel = IfxAsclin_TxFifoInterruptLevel_0;
    cfg.fifo.buffMode             = IfxAsclin_ReceiveBufferMode_rxFifo;

    /* Interrupts: all priorities = 0, tos = cpu0.
     * iLLD will NOT set up any SRC automatically.
     * We manually configure only the RX SRC for DMA after initModule.
     * This prevents iLLD from also enabling TX SRC for DMA
     * (TX FIFO empty → TFLE fires continuously → CME trap). */
    cfg.interrupt.txPriority    = 0;
    cfg.interrupt.rxPriority    = 0;
    cfg.interrupt.erPriority    = 0;
    cfg.interrupt.typeOfService = IfxSrc_Tos_cpu0;

    /* Pins: RX-only on P14.7 (official iLLD pin-map) */
    static const IfxAsclin_Asc_Pins pins = {
        .cts       = NULL_PTR,
        .ctsMode   = IfxPort_InputMode_noPullDevice,
        .rx        = &IfxAsclin9_RXC_P14_7_IN,
        .rxMode    = IfxPort_InputMode_pullUp,
        .rts       = NULL_PTR,
        .rtsMode   = IfxPort_OutputMode_pushPull,
        .tx        = NULL_PTR,
        .txMode    = IfxPort_OutputMode_pushPull,
        .pinDriver = IfxPort_PadDriver_cmosAutomotiveSpeed1
    };
    cfg.pins = &pins;                                  /* pointer, not copy */

    /* SW buffers: iLLD needs something, but DMA does the actual work */
    static uint8 rxBufMem[64 + sizeof(Ifx_Fifo) + 8];
    static uint8 txBufMem[64 + sizeof(Ifx_Fifo) + 8];
    cfg.rxBuffer     = rxBufMem;
    cfg.rxBufferSize = 64;
    cfg.txBuffer     = txBufMem;
    cfg.txBufferSize = 64;

    /* Initialise ASCLIN9 module (sets up baudrate, pins, FIFOs, ISRs) */
    IfxAsclin_Asc_initModule(&g_asc9, &cfg);

    /* Glitch filter on RX input (2 clock ticks) */
    IfxAsclin_setFilterDepth(g_asc9.asclin, 2);

    /* --- Manual RX SRC → DMA routing (iLLD skipped because rxPriority=0) ---
     * 1. Enable RFLE: ASCLIN sets RFL flag when RX FIFO fill ≥ level (=1 byte)
     * 2. Route RX SRC to DMA channel (SRPN = channel ID, TOS = dma)
     * 3. Enable the SRC node
     * Only RX is routed; TX SRC stays disabled (avoids infinite TFLE triggers). */
    IfxAsclin_enableRxFifoFillLevelFlag(g_asc9.asclin, TRUE);
    {
        volatile Ifx_SRC_SRCR *rxSrc = IfxAsclin_getSrcPointerRx(g_asc9.asclin);
        IfxSrc_init(rxSrc, IfxSrc_Tos_dma, (Ifx_Priority)ASCLIN9_DMA_CHANNEL_ID);
        IfxSrc_enable(rxSrc);
    }
}

/* ===================== DMA Channel Configuration ===================== */

/**
 * Configure one DMA channel for ASCLIN9 peripheral-to-memory transfers.
 *
 * - Source: ASCLIN9.RXDATA (fixed address via SCBE=1, CBLS=0)
 * - Dest: ping-pong bufferA / bufferB (auto-increment)
 * - 8-bit moves, 1 move per transfer, 1 transfer per HW request
 * - Channel completion interrupt → ASCLIN9_DMA_ISR (swaps buffers)
 */
static void asclin9_dma_configure_channel(void)
{
    /* ---------- Initialise DMA module handle ---------- */
    IfxDma_Dma_Config dmaCfg;
    IfxDma_Dma_initModuleConfig(&dmaCfg, &MODULE_DMA);
    IfxDma_Dma_initModule(&g_asclin9_dma.dmaHandle, &dmaCfg);

    /* ---------- Build channel configuration ---------- */
    IfxDma_Dma_ChannelConfig chnCfg;
    IfxDma_Dma_initChannelConfig(&chnCfg, &g_asclin9_dma.dmaHandle);

    chnCfg.channelId = ASCLIN9_DMA_CHANNEL_ID;

    /* Source: ASCLIN9 RXDATA register (fixed peripheral address).
     * SCBE=1, CBLS=0 → "no modification of SADR after each DMA read move"
     * (TC3xx User Manual, DMA.ADICR.CBLS = 0000b).                         */
    chnCfg.sourceAddress                = (uint32)&MODULE_ASCLIN9.RXDATA.U;
    chnCfg.sourceCircularBufferEnabled  = TRUE;
    chnCfg.sourceAddressCircularRange   = IfxDma_ChannelIncrementCircular_none;

    /* Destination: bufferA initially, auto-increment by 1 byte per move */
    chnCfg.destinationAddress                   = (uint32)g_asclin9_dma.bufferA;
    chnCfg.destinationAddressIncrementStep      = IfxDma_ChannelIncrementStep_1;
    chnCfg.destinationAddressIncrementDirection  = IfxDma_ChannelIncrementDirection_positive;
    chnCfg.destinationCircularBufferEnabled      = FALSE;

    /* 8-bit moves: one byte per DMA move (matches ASCLIN 8-bit data) */
    chnCfg.moveSize      = IfxDma_ChannelMoveSize_8bit;
    chnCfg.blockMode     = IfxDma_ChannelMove_1;         /* 1 move per transfer */
    chnCfg.transferCount = ASCLIN9_DMA_BUFFER_SIZE;       /* 2560 byte transfers */

    /* Hardware request: ASCLIN RX triggers one DMA transfer per byte */
    chnCfg.requestMode            = IfxDma_ChannelRequestMode_oneTransferPerRequest;
    chnCfg.operationMode          = IfxDma_ChannelOperationMode_continuous;
    chnCfg.hardwareRequestEnabled = TRUE;
    chnCfg.requestSource          = IfxDma_ChannelRequestSource_peripheral;

    /* Shadow: not used (manual swap in ISR) */
    chnCfg.shadowControl = IfxDma_ChannelShadow_none;

    /* Channel completion interrupt → fires when TCOUNT reaches 0 */
    chnCfg.channelInterruptEnabled       = TRUE;
    chnCfg.channelInterruptControl       = IfxDma_ChannelInterruptControl_thresholdLimitMatch;
    chnCfg.interruptRaiseThreshold       = 0;            /* interrupt at TCOUNT == 0 */
    chnCfg.channelInterruptPriority      = ASCLIN9_DMA_ISR_PRIO;
    chnCfg.channelInterruptTypeOfService = IfxSrc_Tos_cpu0;

    /* ---------- Program channel registers & SRC ---------- */
    IfxDma_Dma_initChannel(&g_asclin9_dma.dmaChannel, &chnCfg);
}

/* ===================== Init Entry Point ===================== */

/**
 * Set up ASCLIN9 + DMA + dual buffers + service-request routing.
 */
void asclin9_dma_init(uint32 baud_bps, Asclin9_FrameMode frameMode)
{
    IfxCpu_disableInterrupts();

    /* 1. Enable ASCLIN9 clock */
    IfxAsclin_enableModule(&MODULE_ASCLIN9);
    IfxAsclin_setSuspendMode(&MODULE_ASCLIN9, IfxAsclin_SuspendMode_none);

    /* 2. Configure ASCLIN9 (baudrate, pins, FIFO, error ISR) */
    asclin9_dma_configure(baud_bps, frameMode);

    /* 3. Configure DMA channel (source, dest, transfer count, completion ISR) */
    asclin9_dma_configure_channel();

    /* 4. RX SRC→DMA routing is done inside asclin9_dma_configure() */

    /* 5. Initialise dual-buffer state */
    g_asclin9_dma.pCurrentDest     = g_asclin9_dma.bufferA;
    g_asclin9_dma.pCompletedBuffer = NULL_PTR;
    g_asclin9_dma.completionCount  = 0;
    g_asclin9_dma.timeoutWarnings  = 0;

    IfxCpu_enableInterrupts();
}

/* ===================== Consumer API ===================== */

/**
 * Non-blocking: return pointer to a completed buffer (2560 bytes)
 * and clear the flag so the ISR can fill the other one.
 */
uint8* asclin9_dma_get_completed_buffer(void)
{
    uint8 *result = NULL_PTR;

    IfxCpu_disableInterrupts();
    {
        if (g_asclin9_dma.pCompletedBuffer != NULL_PTR)
        {
            result = (uint8 *)g_asclin9_dma.pCompletedBuffer;
            g_asclin9_dma.pCompletedBuffer = NULL_PTR;
        }
    }
    IfxCpu_enableInterrupts();

    return result;
}

uint32 asclin9_dma_get_completion_count(void)
{
    return g_asclin9_dma.completionCount;
}

uint32 asclin9_dma_get_timeout_warnings(void)
{
    return g_asclin9_dma.timeoutWarnings;
}
