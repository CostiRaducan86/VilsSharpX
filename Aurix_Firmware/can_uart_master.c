/******************************************************************************
 * \file can_uart_master.c
 * \brief CAN-UART master for Direct Control Mode: replays the ECU diagnostic
 *        conversation towards the LSM on ASCLIN4.
 *
 * State machine per sequence step:
 *
 *   GAP  -> wait the inter-frame idle observed in the original ECU trace
 *   TX   -> queue the request bytes, then wait for their echo on the LSM RX
 *   RSP  -> for reads, collect response bytes until the bus goes idle
 *   next step
 *
 * Everything runs on CPU2.  The state machine advances from
 * can_uart_master_tick(); bytes arrive from can_uart_master_feed_rx(), which is
 * called by the bridge relay pump in the ASCLIN4 RX interrupt.  Shared state is
 * only touched under a short interrupt lock.
 ******************************************************************************/

#include "can_uart_master.h"
#include "can_uart_osram_sequence.h"
#include "can_uart_nichia_sequence.h"
#include "device_mode.h"

#include "IfxCpu.h"
#include "Asclin/Std/IfxAsclin.h"
#include "Stm/Std/IfxStm.h"
#include "can_hw.h"
#include <string.h>

/* ASCLIN4 is the LSM-side channel; see can_uart_bridge.c for the pin mapping. */
#define CUM_LSM_ASCLIN      (&MODULE_ASCLIN4)
#define CUM_TX_FIFO_DEPTH   16u

/* Wire time of one byte at 2 Mbaud, 8 data plus parity plus 2 stop, and the
 * request-to-response turnaround.  Only used to place the gap anchor when the
 * answer is missing; lastRspDelayUs reports the measured value. */
#define CUM_BYTE_US         6u
#define CUM_TURNAROUND_US   100u
#define CUM_NICHIA_BYTE_US  5u

CanUartMasterStats g_canUartMasterStats;

volatile uint8 g_canUartMasterRawFilter = 0u;

typedef enum
{
    CUM_STEP_GAP = 0,
    CUM_STEP_TX,
    CUM_STEP_RSP
} CanUartMasterStepState;

static boolean s_active   = FALSE;
static uint8   s_phase    = (uint8)CUM_PHASE_IDLE;
static uint8   s_stepState = (uint8)CUM_STEP_GAP;
static uint32  s_stepIndex = 0u;

static uint32  s_ticksPerUs = 1u;
static uint32  s_stateEnterStm = 0u;   /* when the current sub-state began   */
static uint32  s_gapTicks = 0u;

/* Raw capture of everything the LSM channel delivers for the current step.
 * Whether the transmit echo appears on the bus is decided per transaction by
 * comparing the head of this buffer with the request, so the master works in
 * both cases instead of assuming one. */
static volatile uint8  s_rawBuf[CAN_UART_MASTER_RAW_MAX];
static volatile uint32 s_rawStm[CAN_UART_MASTER_RAW_MAX];
static volatile uint8  s_rawLen = 0u;
static volatile uint32 s_rawLastStm  = 0u;

/* Last byte seen on the bus.  The trace gap is measured from the end of the
 * previous frame, and a request is only sent once the bus has been quiet. */
static volatile uint32 s_busLastByteStm = 0u;

/* When the transaction in flight should end on the wire.  A lost response must
 * not shorten the gap that follows it, or the whole cadence would speed up
 * whenever reception degrades. */
static uint32 s_frameEndStm = 0u;

/* Bytes the LSM will send for the request in flight, 0 for a write. */
static volatile uint8 s_rspExpected = 0u;

/* Raw length at which the previous read was closed.  Anything captured after
 * that point is answer the assumed frame length did not account for. */
static uint8   s_closedRawLen = 0u;
static boolean s_tailValid    = FALSE;

/* STM of the first byte that is answer rather than transmit echo. */
static uint32  s_rspFirstStm = 0u;
static uint32  s_txFirstStm = 0u;
static uint32  s_txEndStm = 0u;
static uint32  s_prevPublishedEndStm = 0u;

/* Keep the uploaded trace out of vtc:linear; CPU2 owns and services this table. */
typedef struct
{
    uint32 gapUs;
    uint8  len;
    uint8  expectResponse;
    uint8  data[CAN_UART_MASTER_NICHIA_REQ_MAX];
} CanUartMasterRuntimeStep;

__attribute__((section(".bss.bss_cpu2")))
static CanUartMasterStep s_uploadedOsram[CAN_UART_MASTER_UPLOADED_MAX];
__attribute__((section(".bss.bss_cpu2")))
static CanUartMasterRuntimeStep s_uploadedNichia[CAN_UART_MASTER_NICHIA_UPLOADED_MAX];
static uint16 s_uploadedStartupCount = 0u;
static uint16 s_uploadedExpectedCount = 0u;
static boolean s_uploadedStartupValid = FALSE;
static boolean s_waitingForUploadedStart = FALSE;

/* Completed master transactions are produced on CPU2 and consumed on CPU0,
 * where can_diag_push_record() is single-core owned. */
#define CUM_OUT_RING_LEN 16u
#define CUM_OUT_DRAIN_BUDGET 15u
static volatile DiagUartFrame s_outRing[CUM_OUT_RING_LEN];
static volatile uint16 s_outHead;
static volatile uint16 s_outTail;

/* Request of the step whose bytes are still in s_rawBuf. */
static uint8   s_prevReq[CAN_UART_MASTER_NICHIA_REQ_MAX];

static boolean is_nichia_mode(void)
{
    return (device_mode_get() == FE_DEVICE_NICHIA) ? TRUE : FALSE;
}

/* Answer length implied by a request header:
 *   [2] HCTRL bits 4:1 hold nRegs-1
 *   answer = nRegs * 2 data + 2 CRC
 * The LSM does not repeat the request header, so the four header bytes seen in
 * a bus trace ahead of the data belong to the request, not to the answer.
 * Verified against the captured traces for the 4, 6, 8 and 34 byte answers. */
static uint8 expected_osram_response_len(const CanUartMasterStep *step)
{
    uint8 nRegs;

    if ((step->expectResponse == 0u) || (step->len < 4u))
        return 0u;

    nRegs = (uint8)(((step->data[2] >> 1) & 0x0Fu) + 1u);

    return (uint8)((nRegs * 2u) + 2u);
}

static uint8 nichia_data_length(uint8 dlc)
{
    static const uint8 lengths[8] = { 1u, 2u, 4u, 8u, 16u, 24u, 32u, 64u };
    return lengths[dlc & 0x07u];
}

static uint8 nichia_expected_response_len(const CanUartMasterRuntimeStep *step)
{
    uint8 fun;
    uint8 dataLen;

    if ((step->expectResponse == 0u) || (step->len < 3u))
        return 0u;

    fun = (uint8)(step->data[2] & 0x07u);
    dataLen = nichia_data_length((uint8)((step->data[2] >> 3u) & 0x07u));

    /* FUN 4 writes return a two-byte ACK; FUN 5 returns data plus CRC8;
     * FUN 7 returns EEPROM data without CRC. */
    if (fun == 4u)
        return 2u;
    if (fun == 5u)
        return (uint8)(dataLen + 1u);
    if (fun == 7u)
        return dataLen;
    return 0u;
}

static void get_current_step(uint32 index, CanUartMasterRuntimeStep *step,
                             uint32 *count)
{
    uint8 i;

    if (is_nichia_mode())
    {
        if (s_phase == (uint8)CUM_PHASE_STARTUP)
        {
            if (s_uploadedStartupValid)
            {
                *count = s_uploadedStartupCount;
                step->gapUs = s_uploadedNichia[index].gapUs;
                step->len = s_uploadedNichia[index].len;
                step->expectResponse = s_uploadedNichia[index].expectResponse;
                for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
                    step->data[i] = (i < step->len) ? s_uploadedNichia[index].data[i] : 0u;
            }
            else
            {
                *count = CAN_UART_NICHIA_STARTUP_STEPS;
                *step = *(const CanUartMasterRuntimeStep *)&s_nichiaStartup[index];
            }
        }
        else
        {
            *count = CAN_UART_NICHIA_CYCLE_STEPS;
            *step = *(const CanUartMasterRuntimeStep *)&s_nichiaCycle[index];
        }
        return;
    }

    if (s_phase == (uint8)CUM_PHASE_STARTUP)
    {
        if (s_uploadedStartupValid)
        {
            *count = s_uploadedStartupCount;
            step->gapUs = s_uploadedOsram[index].gapUs;
            step->len = s_uploadedOsram[index].len;
            step->expectResponse = s_uploadedOsram[index].expectResponse;
            for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
                step->data[i] = (i < step->len) ? s_uploadedOsram[index].data[i] : 0u;
            return;
        }
        *count = CAN_UART_OSRAM_STARTUP_STEPS;
        step->gapUs = s_osramStartup[index].gapUs;
        step->len = s_osramStartup[index].len;
        step->expectResponse = s_osramStartup[index].expectResponse;
        for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
            step->data[i] = (i < step->len) ? s_osramStartup[index].data[i] : 0u;
        return;
    }

    *count = CAN_UART_OSRAM_CYCLE_STEPS;
    step->gapUs = s_osramCycle[index].gapUs;
    step->len = s_osramCycle[index].len;
    step->expectResponse = s_osramCycle[index].expectResponse;
    for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
        step->data[i] = (i < step->len) ? s_osramCycle[index].data[i] : 0u;
}

boolean can_uart_master_stage_step(uint16 index, uint16 total, uint32 gapUs,
                                   uint8 len, uint8 expectResponse,
                                   const uint8 *data)
{
    uint8 i;

    if ((data == NULL_PTR) || (total == 0u) ||
        (total > (is_nichia_mode() ? CAN_UART_MASTER_NICHIA_UPLOADED_MAX
                       : CAN_UART_MASTER_UPLOADED_MAX)) || (index >= total) ||
        (len == 0u) || (len > CAN_UART_MASTER_NICHIA_REQ_MAX))
    {
        g_canUartMasterStats.uploadStepRejects++;
        return FALSE;
    }

    if ((index == 0u) || (s_uploadedExpectedCount != total))
    {
        s_uploadedExpectedCount = total;
        s_uploadedStartupCount = 0u;
        s_uploadedStartupValid = FALSE;
    }

    if (is_nichia_mode())
    {
        s_uploadedNichia[index].gapUs = gapUs;
        s_uploadedNichia[index].len = len;
        s_uploadedNichia[index].expectResponse = (expectResponse != 0u) ? 1u : 0u;
        for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
            s_uploadedNichia[index].data[i] = (i < len) ? data[i] : 0u;
    }
    else
    {
        s_uploadedOsram[index].gapUs = gapUs;
        s_uploadedOsram[index].len = len;
        s_uploadedOsram[index].expectResponse = (expectResponse != 0u) ? 1u : 0u;
        for (i = 0u; i < CAN_UART_MASTER_MAX_REQUEST_LEN; i++)
            s_uploadedOsram[index].data[i] = (i < len) ? data[i] : 0u;
    }
    if ((uint16)(index + 1u) > s_uploadedStartupCount)
        s_uploadedStartupCount = (uint16)(index + 1u);
    g_canUartMasterStats.uploadStepsReceived++;
    return TRUE;
}

boolean can_uart_master_commit_staged(uint16 total)
{
    g_canUartMasterStats.uploadCommitsReceived++;
    if ((total == 0u) || (total != s_uploadedExpectedCount) ||
        (s_uploadedStartupCount != total))
    {
        g_canUartMasterStats.uploadCommitRejects++;
        return FALSE;
    }

    s_uploadedStartupValid = TRUE;
    if (s_waitingForUploadedStart)
    {
        s_waitingForUploadedStart = FALSE;
        can_uart_master_start();
        return TRUE;
    }
    if (s_active)
    {
        can_uart_master_stop();
        can_uart_master_start();
    }
    return TRUE;
}

static uint32 stm_now(void)
{
    return (uint32)IfxStm_getLower(&MODULE_STM0);
}

static uint32 us_to_ticks(uint32 us)
{
    return us * s_ticksPerUs;
}

static void publish_step(void)
{
    g_canUartMasterStats.phase     = s_phase;
    g_canUartMasterStats.stepIndex = s_stepIndex;
}

static void begin_step(void)
{
    uint32 count;
    CanUartMasterRuntimeStep step;

    get_current_step(s_stepIndex, &step, &count);

    s_gapTicks      = us_to_ticks(step.gapUs);
    s_stepState     = (uint8)CUM_STEP_GAP;
    /* Saleae gap values are measured after the previous request burst.  Anchor
     * at its wire end, not at the response end, so the response duration is
     * not added to every inter-request delay.  The quiet-bus check below still
     * prevents overlap with a late response. */
    s_stateEnterStm = (s_txEndStm != 0u) ? s_txEndStm : s_busLastByteStm;
    publish_step();
}

static void advance_step(void)
{
    uint32 count;

    CanUartMasterRuntimeStep step;

    get_current_step(s_stepIndex, &step, &count);

    s_stepIndex++;
    if (s_stepIndex >= count)
    {
        s_stepIndex = 0u;
        if (s_phase == (uint8)CUM_PHASE_STARTUP)
        {
            s_phase = (uint8)CUM_PHASE_CYCLE;
            /* An uploaded trace applies to one replay session only. */
            s_uploadedStartupValid = FALSE;
            g_canUartMasterStats.startupDone++;
        }
        else
        {
            g_canUartMasterStats.cyclesDone++;
        }
    }

    begin_step();
}

static boolean transmit_request(const CanUartMasterRuntimeStep *step)
{
    Ifx_ASCLIN *asc = CUM_LSM_ASCLIN;
    uint8   i;
    boolean is;
    uint32  txStm = stm_now();
    uint32 byteUs;
    uint32 turnaroundUs;

    if ((asc->TXFIFOCON.B.FILL > CUM_TX_FIFO_DEPTH) ||
        ((step->len <= CUM_TX_FIFO_DEPTH) &&
         (step->len > (CUM_TX_FIFO_DEPTH - asc->TXFIFOCON.B.FILL))))
    {
        g_canUartMasterStats.txFull++;
        return FALSE;
    }

    if (s_tailValid && (s_rawLen > s_closedRawLen))
    {
        uint8 tail = (uint8)(s_rawLen - s_closedRawLen);
        g_canUartMasterStats.lastTailLen = tail;
        g_canUartMasterStats.tailBytes  += tail;
    }

    if (s_tailValid && (g_canUartMasterStats.lastFullRawLen == 0u) &&
        ((g_canUartMasterRawFilter == 0u) || (s_prevReq[2] == g_canUartMasterRawFilter)))
    {
        for (i = 0u; i < CAN_UART_MASTER_RAW_MAX; i++)
            g_canUartMasterStats.lastFullRaw[i] = (i < s_rawLen) ? s_rawBuf[i] : 0u;
        for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
            g_canUartMasterStats.lastFullRawRequest[i] = s_prevReq[i];

        g_canUartMasterStats.lastFullRawLen = s_rawLen;
    }
    s_tailValid = FALSE;

    /* Arm the capture before the first byte can come back. */
    is = IfxCpu_disableInterrupts();
    s_rawLen       = 0u;
    s_closedRawLen = 0u;
    s_rspFirstStm  = 0u;
    s_rspExpected  = is_nichia_mode()
        ? nichia_expected_response_len(step)
        : expected_osram_response_len((const CanUartMasterStep *)step);
    s_txFirstStm   = txStm;
    byteUs = is_nichia_mode() ? CUM_NICHIA_BYTE_US : CUM_BYTE_US;
    turnaroundUs = is_nichia_mode() ? 0u : CUM_TURNAROUND_US;
    s_txEndStm     = txStm + us_to_ticks((uint32)step->len * byteUs);
    IfxCpu_restoreInterrupts(is);

    s_frameEndStm = txStm + us_to_ticks(
        ((uint32)step->len + (uint32)s_rspExpected) * byteUs +
        ((s_rspExpected != 0u) ? turnaroundUs : 0u));

    for (i = 0u; i < step->len; i++)
    {
        /* Nichia cycle writes can be longer than the 16-byte hardware FIFO.
         * Keep the request contiguous on the wire by feeding the FIFO as it
         * drains instead of rejecting the whole request forever. */
        while (asc->TXFIFOCON.B.FILL >= CUM_TX_FIFO_DEPTH)
        {
            /* CPU2 interrupts remain enabled, so RX service can continue while
             * the current byte is being shifted out. */
        }
        IfxAsclin_writeTxData(asc, (uint32)step->data[i]);
        g_canUartMasterStats.lastRequest[i] = step->data[i];
    }

    /* Clear the tail so a shorter request does not show a longer one's bytes. */
    for (; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
        g_canUartMasterStats.lastRequest[i] = 0u;

    for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
        s_prevReq[i] = (i < step->len) ? step->data[i] : 0u;

    g_canUartMasterStats.requestsSent++;
    return TRUE;
}

void can_uart_master_init(void)
{
    CanUartMasterStats zero = {0};

    g_canUartMasterStats = zero;

    s_ticksPerUs = (uint32)IfxStm_getTicksFromMicroseconds(&MODULE_STM0, 1);
    if (s_ticksPerUs == 0u)
        s_ticksPerUs = 1u;

    s_active    = FALSE;
    s_phase     = (uint8)CUM_PHASE_IDLE;
    s_stepIndex = 0u;
    s_stepState = (uint8)CUM_STEP_GAP;
    s_txFirstStm = 0u;
    s_outHead   = 0u;
    s_outTail   = 0u;
    s_prevPublishedEndStm = 0u;
}

void can_uart_master_start(void)
{
    if (s_active)
        return;

    s_waitingForUploadedStart = FALSE;

    s_phase     = (uint8)CUM_PHASE_STARTUP;
    s_stepIndex = 0u;
    s_active    = TRUE;
    g_canUartMasterStats.active = 1u;

    s_rawLen         = 0u;
    s_rspExpected    = 0u;
    s_txFirstStm     = 0u;
    s_closedRawLen   = 0u;
    s_rspFirstStm    = 0u;
    s_tailValid      = FALSE;
    s_prevPublishedEndStm = 0u;
    g_canUartMasterStats.lastFullRawLen = 0u;
    s_busLastByteStm = stm_now();
    s_frameEndStm    = s_busLastByteStm;

    begin_step();
}

void can_uart_master_start_hardcoded_nichia(void)
{
    if (!is_nichia_mode())
        return;

    can_uart_master_stop();
    s_uploadedStartupCount = 0u;
    s_uploadedExpectedCount = 0u;
    s_uploadedStartupValid = FALSE;
    can_uart_master_start();
}

void can_uart_master_wait_for_uploaded_start(void)
{
    if (s_active)
        can_uart_master_stop();

    s_waitingForUploadedStart = TRUE;
    s_phase = (uint8)CUM_PHASE_STARTUP;
    s_stepIndex = 0u;
    g_canUartMasterStats.active = 0u;
    g_canUartMasterStats.startupDone = 0u;
    publish_step();
}

void can_uart_master_stop(void)
{
    s_active = FALSE;
    s_waitingForUploadedStart = FALSE;
    s_phase  = (uint8)CUM_PHASE_IDLE;
    g_canUartMasterStats.active = 0u;
    publish_step();
}

boolean can_uart_master_is_active(void)
{
    return s_active;
}

boolean can_uart_master_startup_done(void)
{
    if (s_waitingForUploadedStart)
        return FALSE;

    if (!s_active)
        return TRUE;

    return (s_phase == (uint8)CUM_PHASE_CYCLE) ? TRUE : FALSE;
}

void can_uart_master_note_rx_overflow(void)
{
    if (s_active)
        g_canUartMasterStats.rxOverflows++;
}

void can_uart_master_feed_rx(uint8 b, uint32 stm)
{
    if (!s_active)
        return;

    if (s_rawLen < CAN_UART_MASTER_RAW_MAX)
    {
        s_rawStm[s_rawLen] = stm;
        s_rawBuf[s_rawLen++] = b;
    }
    else
    {
        g_canUartMasterStats.strayBytes++;
    }

    s_rawLastStm     = stm;
    s_busLastByteStm = stm;
}

void can_uart_master_poll_out(void)
{
    uint8 drained = 0u;

    while (s_outTail != s_outHead && drained < CUM_OUT_DRAIN_BUDGET)
    {
        DiagUartFrame frame;
        uint16 tail = s_outTail;

        memcpy(&frame, (const void *)&s_outRing[tail], sizeof(frame));
        __dsync();
        s_outTail = (uint16)((tail + 1u) % CUM_OUT_RING_LEN);
        (void)can_diag_bridge_uart_frame(&frame,
            is_nichia_mode() ? CAN_DIAG_DEVICE_NICHIA : CAN_DIAG_DEVICE_OSRAM);
        drained++;
    }
}

/* Number of leading bytes that are this step's transmit echo.
 * The adapter transceiver may or may not loop the transmission back, so it is
 * decided per transaction by comparing the captured head with the request.
 * A still-incomplete echo returns a shorter prefix, so its bytes are never
 * mistaken for the answer while the transmission is still coming back. */
static uint8 echo_offset(const CanUartMasterRuntimeStep *step, uint8 rawLen)
{
    uint8 n = (rawLen < step->len) ? rawLen : step->len;
    uint8 i;

    for (i = 0u; i < n; i++)
    {
        if (s_rawBuf[i] != step->data[i])
            return i;
    }

    return n;
}

static void publish_transaction(const CanUartMasterRuntimeStep *step, uint8 offset, uint8 responseLen)
{
    DiagUartFrame frame;
    uint8 frameLen = 0u;
    uint8 i;
    uint16 next;
    uint32 firstStm = s_txFirstStm;
    uint32 responseDelayUs = 0u;
    uint32 interFrameDelayUs = 0u;
    uint32 transactionEndStm;

    if (g_diagSniffEnabled == 0u)
        return;

    for (i = 0u; i < step->len && frameLen < CAN_DIAG_RAW_MAX; i++)
        frame.data[frameLen++] = step->data[i];
    for (i = 0u; i < responseLen && frameLen < CAN_DIAG_RAW_MAX; i++)
        frame.data[frameLen++] = s_rawBuf[offset + i];

    frame.len = frameLen;
    frame.timestampUs = firstStm / s_ticksPerUs;
    if (responseLen != 0u)
    {
        if (offset >= step->len)
            responseDelayUs = (uint32)(s_rawStm[offset] - s_rawStm[offset - 1u]);
        else
            responseDelayUs = (uint32)(s_rawStm[offset] - s_txEndStm);
        responseDelayUs /= s_ticksPerUs;
    }
    if (s_prevPublishedEndStm != 0u)
    {
        interFrameDelayUs = (uint32)(s_txFirstStm - s_prevPublishedEndStm);
        interFrameDelayUs /= s_ticksPerUs;
    }
    frame.responseDelayUs = (responseDelayUs > 0xFFFFu) ? 0xFFFFu : (uint16)responseDelayUs;
    frame.interFrameDelayUs = (interFrameDelayUs > 0xFFFFu)
        ? 0xFFFFu : (uint16)interFrameDelayUs;

    next = (uint16)((s_outHead + 1u) % CUM_OUT_RING_LEN);
    if (next == s_outTail)
    {
        g_canUartMasterStats.outRingDrops++;
        return;
    }

    memcpy((void *)&s_outRing[s_outHead], &frame, sizeof(frame));
    __dsync();
    s_outHead = next;
    {
        uint16 depth = (s_outHead >= s_outTail)
                     ? (uint16)(s_outHead - s_outTail)
                     : (uint16)(CUM_OUT_RING_LEN - s_outTail + s_outHead);
        if ((uint32)depth > g_canUartMasterStats.outRingHighWater)
            g_canUartMasterStats.outRingHighWater = depth;
    }
    transactionEndStm = (responseLen != 0u)
        ? s_rawStm[offset + responseLen - 1u]
        : ((s_rawLen != 0u) ? s_rawLastStm : s_txEndStm);
    s_prevPublishedEndStm = transactionEndStm;
}

static void finish_response(const CanUartMasterRuntimeStep *step)
{
    boolean is;
    uint8 rawLen;
    uint8 offset;
    uint8 len;
    uint8 i;

    is = IfxCpu_disableInterrupts();
    rawLen = s_rawLen;
    offset = echo_offset(step, rawLen);
    len    = (uint8)(rawLen - offset);
    IfxCpu_restoreInterrupts(is);

    if ((s_rspExpected == 0u) || (len == s_rspExpected))
        publish_transaction(step, offset, (s_rspExpected != 0u) ? len : 0u);

    if (offset == step->len)
        g_canUartMasterStats.echoSeenCount++;
    else if (s_rawLen != 0u)
        g_canUartMasterStats.echoAbsentCount++;

    g_canUartMasterStats.lastRawLen      = s_rawLen;
    g_canUartMasterStats.lastEchoLen     = offset;
    g_canUartMasterStats.lastRspLen      = len;
    g_canUartMasterStats.lastRspExpected = s_rspExpected;

    if ((s_rspExpected != 0u) && (len != 0u))
    {
        s_rspFirstStm = s_rawStm[offset];
        g_canUartMasterStats.lastRspDelayUs =
            (uint32)(s_rspFirstStm - ((offset >= step->len)
                ? s_rawStm[offset - 1u] : s_txEndStm)) / s_ticksPerUs;
    }

    for (i = 0u; i < CAN_UART_MASTER_NICHIA_REQ_MAX; i++)
        g_canUartMasterStats.lastRspRequest[i] = (i < step->len) ? step->data[i] : 0u;

    for (i = 0u; i < CAN_UART_MASTER_NICHIA_RSP_MAX; i++)
        g_canUartMasterStats.lastResponse[i] = (i < len) ? s_rawBuf[offset + i] : 0u;

    g_canUartMasterStats.lastRspSerial++;

    s_closedRawLen = rawLen;
    s_tailValid    = (s_rspExpected != 0u) ? TRUE : FALSE;

    if (s_rspExpected == 0u)
        return;

    if (len == s_rspExpected)
    {
        g_canUartMasterStats.responsesOk++;
    }
    else if (len < s_rspExpected)
    {
        g_canUartMasterStats.shortResponses++;
        if (len == 0u)
            g_canUartMasterStats.responseTimeouts++;
    }
    else
    {
        /* More bytes than the frame holds: the window caught something else. */
        g_canUartMasterStats.badSyncResponses++;
    }
}

void can_uart_master_tick(void)
{
    uint32 count;
    CanUartMasterRuntimeStep step;
    uint32 now;

    if (!s_active)
        return;

    get_current_step(s_stepIndex, &step, &count);
    now  = stm_now();

    switch (s_stepState)
    {
        case (uint8)CUM_STEP_GAP:
            /* Signed: the anchor can sit slightly ahead of now when the frame
             * ended a little earlier on the wire than its computed length. */
            if ((sint32)(now - s_stateEnterStm) < (sint32)s_gapTicks)
                return;

            /* Never start transmitting while the bus is still busy: a trailing
             * byte would be counted as part of this request's echo and shift
             * every following response by one. */
            if ((uint32)(now - s_busLastByteStm) < us_to_ticks(CAN_UART_MASTER_QUIET_US))
            {
                g_canUartMasterStats.quietWaits++;
                return;
            }

            if (!transmit_request(&step))
                return;

            s_stepState     = (uint8)CUM_STEP_TX;
            s_stateEnterStm = now;
            break;

        case (uint8)CUM_STEP_TX:
            /* The capture window covers the request echo, when the transceiver
             * produces one, plus the response.  Both possibilities are accepted
             * so the step ends as soon as enough bytes are on the bus. */
            if (step.expectResponse == 0u)
            {
                /* A write is only echoed, if at all.  Give the bus a short
                 * moment, then move on. */
                if ((uint32)(now - s_stateEnterStm) > us_to_ticks(CAN_UART_MASTER_QUIET_US * 2u))
                {
                    publish_transaction(&step, 0u, 0u);
                    advance_step();
                }
            }
            else
            {
                s_stepState     = (uint8)CUM_STEP_RSP;
                s_stateEnterStm = now;
            }
            break;

        case (uint8)CUM_STEP_RSP:
        {
            /* Only bytes past the transmit echo count towards the answer, whose
             * length follows from the request header. */
            boolean is;
            uint32 responseStartUs;
            uint32 responseTimeoutUs;
            uint8 rawLen;
            uint8 offset;
            uint8 got;

            responseStartUs = is_nichia_mode()
                ? CAN_UART_MASTER_NICHIA_RSP_START_US
                : CAN_UART_MASTER_RSP_START_US;
            responseTimeoutUs = is_nichia_mode()
                ? CAN_UART_MASTER_NICHIA_TIMEOUT_US
                : CAN_UART_MASTER_TIMEOUT_US;

            is = IfxCpu_disableInterrupts();
            rawLen = s_rawLen;
            offset = echo_offset(&step, rawLen);
            got    = (uint8)(rawLen - offset);

            if ((got > 0u) && (s_rspFirstStm == 0u))
                s_rspFirstStm = s_rawStm[offset];
            IfxCpu_restoreInterrupts(is);

            if (got >= s_rspExpected)
            {
                finish_response(&step);
                advance_step();
            }
            else if (got > 0u)
            {
                /* Keep the transaction open across byte gaps until the
                 * protocol-defined response length has arrived. */
            }
            else if ((uint32)(now - s_stateEnterStm) >
                     us_to_ticks(responseStartUs))
            {
                /* The ECU cadence must continue when a response is absent. */
                finish_response(&step);
                advance_step();
            }

            if ((s_stepState == (uint8)CUM_STEP_RSP) &&
                ((uint32)(now - s_stateEnterStm) > us_to_ticks(responseTimeoutUs)))
            {
                finish_response(&step);
                advance_step();
            }
            break;
        }

        default:
            s_stepState = (uint8)CUM_STEP_GAP;
            break;
    }
}
