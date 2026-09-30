/******************************************************************************
 * \file asclin1_dma.c
 * \brief ASCLIN1 RX with HDMA + dual buffer (zero-copy) for LVDS pixel data.
 *
 * Architecture:
 * - ASCLIN1 RX on P14.8 (X103 pin 7) → HDMA transfers bytes to RAM
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

#include "asclin1_dma.h"

/* ===================== Module State ===================== */
/*
 * Place the LVDS DMA ping-pong buffers in CPU3's Data Scratch-Pad RAM (dsram3)
 * instead of the default dsram0.
 *
 * Root cause of the LVDS CRC errors / flicker: the CAN-UART bridge runs on CPU2
 * and accesses its state (s_monAcc, s_ecuDir, s_lsmDir, ...) which the linker
 * placed in dsram0.  CPU2 reaches dsram0 over the SRI crossbar at a high rate,
 * contending with the LVDS HDMA that writes THESE ping-pong buffers in the SAME
 * dsram0 slave port.  When the DMA's writes are delayed the ASCLIN1 RX FIFO
 * overflows and pixel bytes are lost -> frame CRC fails, and the corrupted frame
 * is seen on both the PC monitor and the TFT.
 *
 * CPU3 executes only an empty idle loop, so dsram3's data port is otherwise
 * unused: the DMA gets uncontended, deterministic write bandwidth.  The linker
 * routes the ".bss.bss_cpu3" section to dsram3 at its SRI-global alias
 * (0x40000000), which both CPU0 (the parser / DMA-completion ISR) and the DMA
 * engine can address.  This is purely a memory-placement change; the ASCLIN
 * baud rate, DMA channel, buffer size and protocol handling are unchanged.
 */
IFX_ALIGN(32) __attribute__((section(".bss.bss_cpu3"))) Asclin1Dma g_asclin1_dma;

/* ASCLIN handle (used for baudrate/pin config; RX data path is DMA) */
static IfxAsclin_Asc g_asc1;

/* ===================== DMA Completion ISR ===================== */

/**
 * Fires when DMA has transferred ASCLIN1_DMA_BUFFER_SIZE bytes into a buffer.
 * Actions:
 *  1. Clear DMA channel interrupt.
 *  2. Swap destination buffer (ping-pong).
 *  3. Re-program DMA destination address + transfer count for next buffer.
 *  4. Signal main loop that a buffer is ready.
 */
IFX_INTERRUPT(ASCLIN1_DMA_ISR, 0, ASCLIN1_DMA_ISR_PRIO)
{
    volatile Ifx_DMA_CH *ch = &MODULE_DMA.CH[ASCLIN1_DMA_CHANNEL_ID];

    /* 1. Determine next buffer (before any SFR writes) */
    uint8 *completed;
    uint8 *nextDest;

    if (g_asclin1_dma.pCurrentDest == g_asclin1_dma.bufferA)
    {
        completed = g_asclin1_dma.bufferA;
        nextDest  = g_asclin1_dma.bufferB;
    }
    else
    {
        completed = g_asclin1_dma.bufferB;
        nextDest  = g_asclin1_dma.bufferA;
    }

    /* 2. Write new destination address (channel is idle, TCOUNT=0) */
    ch->DADR.U = (uint32)nextDest;

    /* 3. Re-enable hardware request → loads TCOUNT from TREL, starts DMA. */
    {
        Ifx_DMA_TSR tsr;
        tsr.U     = 0;
        tsr.B.ECH = 1;
        MODULE_DMA.TSR[ASCLIN1_DMA_CHANNEL_ID].U = tsr.U;
    }

    /* 4. Clear channel interrupt flag */
    {
        Ifx_DMA_CH_CHCSR csr;
        csr.U      = 0;
        csr.B.CICH = 1;
        ch->CHCSR.U = csr.U;
    }

    /* 5. Update tracking */
    g_asclin1_dma.pCurrentDest     = nextDest;
    if (g_asclin1_dma.pCompletedBuffer != NULL_PTR)
    {
        g_asclin1_dma.missedBuffers++;
    }
    g_asclin1_dma.pCompletedBuffer = completed;
    g_asclin1_dma.completionCount++;
}

/* ===================== ASCLIN1 Configuration ===================== */

static void asclin1_dma_reset_hardware(void)
{
    volatile Ifx_SRC_SRCR *rxSrc = IfxAsclin_getSrcPointerRx(&MODULE_ASCLIN1);

    IfxSrc_disable(rxSrc);
    MODULE_ASCLIN1.FLAGSENABLE.U = 0u;
    IfxAsclin_flushRxFifo(&MODULE_ASCLIN1);
    IfxAsclin_clearAllFlags(&MODULE_ASCLIN1);

    IfxAsclin_resetModule(&MODULE_ASCLIN1);
}

/**
 * Configure ASCLIN1 for RX-only on P14.8, with the RX SRC routed to DMA.
 */
static void asclin1_dma_configure(uint32 baud, LvdsFrameMode frameMode)
{
    IfxAsclin_Asc_Config cfg;
    IfxAsclin_Asc_initModuleConfig(&cfg, &MODULE_ASCLIN1);

    /* Clock */
    cfg.clockSource = IfxAsclin_ClockSource_ascFastClock;

    /* Baud rate */
    cfg.baudrate.baudrate     = (float32)baud;
    cfg.baudrate.prescaler    = 1;

    /* Bit timing */
    if (frameMode == Frame_8Odd1)
    {
        /* Osram 20 Mbaud: 10x gives fOVS = fASCLINF (200 MHz, no fractional
         * jitter); median-of-3 at ticks 4..6 = 20..30 ns, the bit centre.
         * Long FTB harnesses delay P14.8 edges by up to ~24 ns, which the
         * former early sample point (ticks 1..3 of 8) turned into bit errors. */
        cfg.baudrate.oversampling         = IfxAsclin_OversamplingFactor_10;
        cfg.bitTiming.samplePointPosition = IfxAsclin_SamplePointPosition_6;
    }
    else
    {
        cfg.baudrate.oversampling         = IfxAsclin_OversamplingFactor_8;
        cfg.bitTiming.samplePointPosition = IfxAsclin_SamplePointPosition_3;
    }
    cfg.bitTiming.medianFilter = IfxAsclin_SamplesPerBit_three;

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

    /* Request the DMA once 4 bytes are queued; one request then drains 4.
     * At one byte per request the DMA can only match the arrival rate, so any
     * backlog in the 16-entry FIFO is permanent and ends in an overflow. */
    cfg.fifo.inWidth              = IfxAsclin_TxFifoInletWidth_1;
    cfg.fifo.outWidth             = IfxAsclin_RxFifoOutletWidth_1;
    cfg.fifo.rxFifoInterruptLevel = IfxAsclin_RxFifoInterruptLevel_4;
    cfg.fifo.txFifoInterruptLevel = IfxAsclin_TxFifoInterruptLevel_0;
    cfg.fifo.buffMode             = IfxAsclin_ReceiveBufferMode_rxFifo;

    /* Interrupts: all priorities = 0 (iLLD skips SRC setup).
     * We manually route only RX SRC to DMA after initModule. */
    cfg.interrupt.txPriority    = 0;
    cfg.interrupt.rxPriority    = 0;
    cfg.interrupt.erPriority    = 0;
    cfg.interrupt.typeOfService = IfxSrc_Tos_cpu0;

    /* Pins: RX-only on P14.8 (ASCLIN1 RXD, X103 pin 7) */
    static const IfxAsclin_Asc_Pins pins = {
        .cts       = NULL_PTR,
        .ctsMode   = IfxPort_InputMode_noPullDevice,
        .rx        = &IfxAsclin1_RXD_P14_8_IN,
        .rxMode    = IfxPort_InputMode_pullUp,
        .rts       = NULL_PTR,
        .rtsMode   = IfxPort_OutputMode_pushPull,
        .tx        = NULL_PTR,
        .txMode    = IfxPort_OutputMode_pushPull,
        .pinDriver = IfxPort_PadDriver_cmosAutomotiveSpeed1
    };
    cfg.pins = &pins;

    /* SW buffers: iLLD needs something, but DMA does the actual work */
    static uint8 rxBufMem[64 + sizeof(Ifx_Fifo) + 8];
    static uint8 txBufMem[64 + sizeof(Ifx_Fifo) + 8];
    cfg.rxBuffer     = rxBufMem;
    cfg.rxBufferSize = 64;
    cfg.txBuffer     = txBufMem;
    cfg.txBufferSize = 64;

    /* Initialise ASCLIN1 module */
    IfxAsclin_Asc_initModule(&g_asc1, &cfg);

    /* Glitch filter on RX input (2 clock ticks) */
    IfxAsclin_setFilterDepth(g_asc1.asclin, 2);

    /* Manual RX SRC → DMA routing */
    IfxAsclin_enableRxFifoFillLevelFlag(g_asc1.asclin, TRUE);
    {
        volatile Ifx_SRC_SRCR *rxSrc = IfxAsclin_getSrcPointerRx(g_asc1.asclin);
        IfxSrc_init(rxSrc, IfxSrc_Tos_dma, (Ifx_Priority)ASCLIN1_DMA_CHANNEL_ID);
        IfxSrc_enable(rxSrc);
    }
}

/* ===================== DMA Channel Configuration ===================== */

/**
 * Configure DMA channel 1 for ASCLIN1 peripheral-to-memory transfers.
 */
static void asclin1_dma_configure_channel(void)
{
    /* Initialise DMA module handle */
    IfxDma_Dma_Config dmaCfg;
    IfxDma_Dma_initModuleConfig(&dmaCfg, &MODULE_DMA);
    IfxDma_Dma_initModule(&g_asclin1_dma.dmaHandle, &dmaCfg);

    /* Build channel configuration */
    IfxDma_Dma_ChannelConfig chnCfg;
    IfxDma_Dma_initChannelConfig(&chnCfg, &g_asclin1_dma.dmaHandle);

    chnCfg.channelId = ASCLIN1_DMA_CHANNEL_ID;

    /* Source: ASCLIN1 RXDATA register (fixed peripheral address) */
    chnCfg.sourceAddress                = (uint32)&MODULE_ASCLIN1.RXDATA.U;
    chnCfg.sourceCircularBufferEnabled  = TRUE;
    chnCfg.sourceAddressCircularRange   = IfxDma_ChannelIncrementCircular_none;

    /* Destination: bufferA initially, auto-increment by 1 byte */
    chnCfg.destinationAddress                   = (uint32)g_asclin1_dma.bufferA;
    chnCfg.destinationAddressIncrementStep      = IfxDma_ChannelIncrementStep_1;
    chnCfg.destinationAddressIncrementDirection  = IfxDma_ChannelIncrementDirection_positive;
    chnCfg.destinationCircularBufferEnabled      = FALSE;

    /* 8-bit moves, 4 per transfer: transferCount counts transfers, not bytes. */
    chnCfg.moveSize      = IfxDma_ChannelMoveSize_8bit;
    chnCfg.blockMode     = IfxDma_ChannelMove_4;
    chnCfg.transferCount = ASCLIN1_DMA_BUFFER_SIZE / ASCLIN1_DMA_MOVES_PER_REQUEST;

    /* Hardware request from ASCLIN RX */
    chnCfg.requestMode            = IfxDma_ChannelRequestMode_oneTransferPerRequest;
    chnCfg.operationMode          = IfxDma_ChannelOperationMode_single;
    chnCfg.hardwareRequestEnabled = TRUE;
    chnCfg.requestSource          = IfxDma_ChannelRequestSource_peripheral;

    /* No shadow (single-shot + ISR re-arm) */
    chnCfg.shadowControl = IfxDma_ChannelShadow_none;

    /* Channel completion interrupt */
    chnCfg.channelInterruptEnabled       = TRUE;
    chnCfg.channelInterruptControl       = IfxDma_ChannelInterruptControl_thresholdLimitMatch;
    chnCfg.interruptRaiseThreshold       = 0;
    chnCfg.channelInterruptPriority      = ASCLIN1_DMA_ISR_PRIO;
    chnCfg.channelInterruptTypeOfService = IfxSrc_Tos_cpu0;

    /* Program channel registers */
    IfxDma_Dma_initChannel(&g_asclin1_dma.dmaChannel, &chnCfg);
}

/* ===================== Init Entry Point ===================== */

void asclin1_dma_init(uint32 baud_bps, LvdsFrameMode frameMode)
{
    uint32 nextInitCount = g_asclin1_dma.initCount + 1u;

    IfxCpu_disableInterrupts();

    /* 1. Enable ASCLIN1 clock */
    IfxAsclin_enableModule(&MODULE_ASCLIN1);
    IfxAsclin_setSuspendMode(&MODULE_ASCLIN1, IfxAsclin_SuspendMode_none);
    asclin1_dma_reset_hardware();
    IfxAsclin_enableModule(&MODULE_ASCLIN1);
    IfxAsclin_setSuspendMode(&MODULE_ASCLIN1, IfxAsclin_SuspendMode_none);

    /* 2. Configure ASCLIN1 (baudrate, pins, FIFO) */
    asclin1_dma_configure(baud_bps, frameMode);

    /* 3. Configure DMA channel 1 */
    asclin1_dma_configure_channel();

    /* 4. Initialise dual-buffer state */
    g_asclin1_dma.pCurrentDest     = g_asclin1_dma.bufferA;
    g_asclin1_dma.pCompletedBuffer = NULL_PTR;
    g_asclin1_dma.completionCount  = 0;
    g_asclin1_dma.missedBuffers    = 0;
    g_asclin1_dma.timeoutWarnings  = 0;
    g_asclin1_dma.frameErrors      = 0;
    g_asclin1_dma.parityErrors     = 0;
    g_asclin1_dma.overrunErrors    = 0;
    g_asclin1_dma.fifoFlushes      = 0;
    g_asclin1_dma.rxFifoFill       = 0;
    g_asclin1_dma.regFlags         = MODULE_ASCLIN1.FLAGS.U;
    g_asclin1_dma.regFlagsEn       = MODULE_ASCLIN1.FLAGSENABLE.U;
    g_asclin1_dma.regFrameCon      = MODULE_ASCLIN1.FRAMECON.U;
    g_asclin1_dma.regRxFifoCon     = MODULE_ASCLIN1.RXFIFOCON.U;
    g_asclin1_dma.regBrg           = MODULE_ASCLIN1.BRG.U;
    g_asclin1_dma.regBitCon        = MODULE_ASCLIN1.BITCON.U;
    g_asclin1_dma.regCsr           = MODULE_ASCLIN1.CSR.U;
    g_asclin1_dma.dmaTsr           = MODULE_DMA.TSR[ASCLIN1_DMA_CHANNEL_ID].U;
    g_asclin1_dma.dmaChcsr         = MODULE_DMA.CH[ASCLIN1_DMA_CHANNEL_ID].CHCSR.U;
    g_asclin1_dma.initCount        = nextInitCount;

    IfxCpu_enableInterrupts();
}

void asclin1_dma_stop(void)
{
    volatile Ifx_SRC_SRCR *rxSrc = IfxAsclin_getSrcPointerRx(&MODULE_ASCLIN1);
    Ifx_DMA_TSR tsr;

    /* Disable the DMA channel before touching the ASCLIN, so no pending
     * request can write into a buffer that is about to be reconfigured. */
    tsr.U     = 0;
    tsr.B.DCH = 1;
    MODULE_DMA.TSR[ASCLIN1_DMA_CHANNEL_ID].U = tsr.U;

    IfxSrc_disable(rxSrc);
    MODULE_ASCLIN1.FLAGSENABLE.U = 0u;
    IfxAsclin_flushRxFifo(&MODULE_ASCLIN1);
    IfxAsclin_clearAllFlags(&MODULE_ASCLIN1);

    g_asclin1_dma.pCompletedBuffer = NULL_PTR;
}

void asclin1_dma_poll_health(void)
{
    Ifx_ASCLIN *asclin = &MODULE_ASCLIN1;
    uint32 flags = asclin->FLAGS.U;

    g_asclin1_dma.regFlags     = flags;
    g_asclin1_dma.regFlagsEn   = asclin->FLAGSENABLE.U;
    g_asclin1_dma.regFrameCon  = asclin->FRAMECON.U;
    g_asclin1_dma.regRxFifoCon = asclin->RXFIFOCON.U;
    g_asclin1_dma.regBrg       = asclin->BRG.U;
    g_asclin1_dma.regBitCon    = asclin->BITCON.U;
    g_asclin1_dma.regCsr       = asclin->CSR.U;
    g_asclin1_dma.rxFifoFill   = asclin->RXFIFOCON.B.FILL;
    g_asclin1_dma.dmaTsr       = MODULE_DMA.TSR[ASCLIN1_DMA_CHANNEL_ID].U;
    g_asclin1_dma.dmaChcsr     = MODULE_DMA.CH[ASCLIN1_DMA_CHANNEL_ID].CHCSR.U;

    if (asclin->FLAGS.B.FE != 0u)
    {
        g_asclin1_dma.frameErrors++;
        IfxAsclin_clearFrameErrorFlag(asclin);
    }

    if (asclin->FLAGS.B.PE != 0u)
    {
        g_asclin1_dma.parityErrors++;
        IfxAsclin_clearParityErrorFlag(asclin);
    }

    if (asclin->FLAGS.B.RFO != 0u)
    {
        g_asclin1_dma.overrunErrors++;
        IfxAsclin_clearRxFifoOverflowFlag(asclin);
        IfxAsclin_flushRxFifo(asclin);
        g_asclin1_dma.fifoFlushes++;
    }

    if (asclin->FLAGS.B.RFU != 0u)
    {
        IfxAsclin_clearRxFifoUnderflowFlag(asclin);
    }
}

/* ===================== Consumer API ===================== */

uint8* asclin1_dma_get_completed_buffer(void)
{
    uint8 *result = NULL_PTR;

    IfxCpu_disableInterrupts();
    {
        if (g_asclin1_dma.pCompletedBuffer != NULL_PTR)
        {
            result = (uint8 *)g_asclin1_dma.pCompletedBuffer;
            g_asclin1_dma.pCompletedBuffer = NULL_PTR;
        }
    }
    IfxCpu_enableInterrupts();

    return result;
}

uint32 asclin1_dma_get_completion_count(void)
{
    return g_asclin1_dma.completionCount;
}

uint32 asclin1_dma_get_timeout_warnings(void)
{
    return g_asclin1_dma.timeoutWarnings;
}

uint32 asclin1_dma_get_missed_buffers(void)
{
    return g_asclin1_dma.missedBuffers;
}
