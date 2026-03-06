/*******************************************************************************
 * RETIRED: This ISR-based ASCLIN9 driver is replaced by asclin9_dma.c
 * (DMA-based dual-buffer).  Kept for reference.  Excluded from build via #if 0.
 *
 * If this file is compiled, its ISR handlers (priorities 14, 15) register in
 * the IVT and reference an uninitialised g_asc9 handle.  Any spurious
 * interrupt at those priorities would dereference NULL → bus error trap.
 ******************************************************************************/
#if 0 /* ── replaced by asclin9_dma.c ── */

#include "IfxAsclin_Asc.h"
#include "IfxAsclin_PinMap.h"
#include "IfxAsclin.h"              /* pentru IfxAsclin_enableAscErrorFlags */
#include "IfxPort.h"
#include "IfxCpu.h"
#include "IfxCpu_Irq.h"
#include "asclin9_rx.h"
#include "rxmon.h"                  /* contorizez erorile HW în ISR */

/* ===================== TUNING =====================
 * Pentru 12.5 MBaud:
 * - Oversampling 8×
 * - Sample-point ușor mai devreme (SP=3) față de 4
 * - Median filter = 3 sample
 * - Glitch filter = 2 ticks
 * - RxFifo interrupt level = 8 bytes (reduce sarcina ISR)
 */
#define ASCLIN9_OVERSAMPLING   IfxAsclin_OversamplingFactor_8
#define ASCLIN9_SAMPLE_POINT   IfxAsclin_SamplePointPosition_3
#define ASCLIN9_MEDIAN_FILTER  IfxAsclin_SamplesPerBit_three

/* SW FIFO payload areas */
#define RX_SWBUF_DATA_SZ   (8 * 1024)
#define TX_SWBUF_DATA_SZ   (64)

/* ISR priorities */
#define ISR_PRIO_ASCLIN9_RX  (14)
#define ISR_PRIO_ASCLIN9_ERR (15)

/* ASCLIN handle */
static IfxAsclin_Asc g_asc9;

/* ===================== INTERRUPTS ===================== */
IFX_INTERRUPT(ASCLIN9_RX_ISR, 0, ISR_PRIO_ASCLIN9_RX)
{
    /* Mută octeții din HW FIFO în SW FIFO (iLLD) */
    IfxAsclin_Asc_isrReceive(&g_asc9);
}

IFX_INTERRUPT(ASCLIN9_ERR_ISR, 0, ISR_PRIO_ASCLIN9_ERR)
{
    /* Citește snapshot erori și contorizează (parity/frame/overflow) */
    IfxAsclin_Asc_ErrorFlagsUnion ef;
    ef.ALL = (unsigned char)g_asc9.asclin->FLAGS.U;

    if (ef.flags.parityError)    { g_rxmon.hwParityErr++;    }
    if (ef.flags.frameError)     { g_rxmon.hwFrameErr++;     }
    if (ef.flags.rxFifoOverflow) { g_rxmon.hwOverrunErr++;   }

    IfxAsclin_Asc_isrError(&g_asc9);  /* curăță flag-urile */
}

/* ===================== INTERNAL CONFIG ===================== */
static void asclin9_configure(uint32 baud, Asclin9_FrameMode frameMode)
{
    IfxAsclin_Asc_Config cfg;
    IfxAsclin_Asc_initModuleConfig(&cfg, &MODULE_ASCLIN9);

    /* Clock ASC rapid */
    cfg.clockSource = IfxAsclin_ClockSource_ascFastClock;

    /* Timings */
    cfg.baudrate.baudrate     = (float32)baud;
    cfg.baudrate.prescaler    = 1;
    cfg.baudrate.oversampling = ASCLIN9_OVERSAMPLING;

    cfg.bitTiming.samplePointPosition = ASCLIN9_SAMPLE_POINT;
    cfg.bitTiming.medianFilter        = ASCLIN9_MEDIAN_FILTER;

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

    /* FIFO config -> SW buffer mode; ridic nivelul IRQ RX la 8 bytes */
    cfg.fifo.inWidth              = IfxAsclin_TxFifoInletWidth_1;
    cfg.fifo.outWidth             = IfxAsclin_RxFifoOutletWidth_1;
    cfg.fifo.txFifoInterruptLevel = IfxAsclin_TxFifoInterruptLevel_0;
    cfg.fifo.rxFifoInterruptLevel = IfxAsclin_RxFifoInterruptLevel_8;
    cfg.fifo.buffMode             = IfxAsclin_ReceiveBufferMode_rxBuffer;

    /* Interrupt config */
    cfg.interrupt.txPriority    = 0;
    cfg.interrupt.rxPriority    = ISR_PRIO_ASCLIN9_RX;
    cfg.interrupt.erPriority    = ISR_PRIO_ASCLIN9_ERR;
    cfg.interrupt.typeOfService = IfxSrc_Tos_cpu0;

    /* Pins: RX-only pe P14.7 (pinmap oficial) */
    static const IfxAsclin_Asc_Pins pins = {
        .cts     = NULL_PTR,
        .ctsMode = IfxPort_InputMode_noPullDevice,
        .rx      = &IfxAsclin9_RXC_P14_7_IN,
        .rxMode  = IfxPort_InputMode_pullUp,
        .rts     = NULL_PTR,
        .rtsMode = IfxPort_OutputMode_pushPull,
        .tx      = NULL_PTR,
        .txMode  = IfxPort_OutputMode_pushPull,
        .pinDriver = IfxPort_PadDriver_cmosAutomotiveSpeed1
    };
    cfg.pins = &pins;

    /* SW FIFO buffers */
    static uint8 rxBufMem[RX_SWBUF_DATA_SZ + sizeof(Ifx_Fifo) + 8];
    static uint8 txBufMem[TX_SWBUF_DATA_SZ + sizeof(Ifx_Fifo) + 8];
    cfg.rxBuffer     = rxBufMem;
    cfg.rxBufferSize = RX_SWBUF_DATA_SZ;
    cfg.txBuffer     = txBufMem;
    cfg.txBufferSize = TX_SWBUF_DATA_SZ;

    /* Init module */
    IfxAsclin_Asc_initModule(&g_asc9, &cfg);

    /* Glitch filter pe intrare (2 ticks) */
    IfxAsclin_setFilterDepth(g_asc9.asclin, 2);

    /* Activez întreruperile de eroare ASC (paritate + Rx FIFO overflow) */
    IfxAsclin_enableAscErrorFlags(g_asc9.asclin, TRUE, TRUE); /* iLLD helper */
}

void asclin9_init(uint32 baud_bps, Asclin9_FrameMode frameMode)
{
    IfxCpu_disableInterrupts();
    IfxAsclin_enableModule(&MODULE_ASCLIN9);
    IfxAsclin_setSuspendMode(&MODULE_ASCLIN9, IfxAsclin_SuspendMode_none);
    asclin9_configure(baud_bps, frameMode);
    IfxCpu_enableInterrupts();
}

void asclin9_set_baudrate(uint32 baud_bps, Asclin9_FrameMode frameMode)
{
    IfxCpu_disableInterrupts();
    asclin9_configure(baud_bps, frameMode);
    IfxCpu_enableInterrupts();
}

/* ===================== FIFO DRAIN ===================== */
void asclin9_consume_ready_buffers(void (*consume)(const uint8 *data, uint32 len))
{
    static uint8 chunk[4096]; /* mărire la 4 KB */

    for (;;)
    {
        uint32 avail = IfxAsclin_Asc_getReadCount(&g_asc9);
        if (avail == 0u)
            break;

        uint32   take   = (avail > sizeof(chunk)) ? sizeof(chunk) : avail;
        Ifx_SizeT toRead = (Ifx_SizeT)take;

        IfxAsclin_Asc_read(&g_asc9, chunk, &toRead, 0);
        if (toRead == 0)
            break;

        consume(chunk, (uint32)toRead);
    }
}

#endif /* #if 0 — replaced by asclin9_dma.c */
