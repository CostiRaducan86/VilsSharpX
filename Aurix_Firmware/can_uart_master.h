#ifndef CAN_UART_MASTER_H
#define CAN_UART_MASTER_H

/******************************************************************************
 * can_uart_master.h — CAN-UART master for Direct Control Mode
 *
 * With no ECU in the chain the LSM stays in failsafe until it has seen the
 * diagnostic conversation it expects.  This module replays the ECU side of that
 * conversation on ASCLIN4 (LSM side): first the start-up sequence, then the
 * cyclic keep-alive and status polling, both captured from a real ECU trace and
 * generated into can_uart_osram_sequence.h.
 *
 * Ownership and timing:
 * - ASCLIN4 and ASCLIN5 belong to can_uart_bridge.c, whose RX interrupts run on
 *   CPU2.  The master therefore also runs on CPU2 and never touches the RX FIFO
 *   itself: the bridge relay pump is the single reader and hands bytes over
 *   through can_uart_master_feed_rx().
 * - The bus is half duplex through the transceivers, so every transmitted byte
 *   comes back on the LSM RX as an echo.  The master counts its own echoes and
 *   only treats later bytes as the LSM response, the same discipline the bridge
 *   uses for forwarded traffic.
 * - Transmission uses the same IfxAsclin_writeTxData() path as the bridge.
 *
 * The bridge's byte forwarding must be inactive while the master runs; there is
 * no ECU to forward to and both would otherwise drive the same TX line.
 ******************************************************************************/

#include "Ifx_Types.h"
#include "can_diag.h"

/* Once the answer has started, a gap this long means the frame ended.  It only
 * applies between answer bytes: the LSM turnaround is far longer than one byte
 * time, so this must never be used to decide that no answer is coming. */
#define CAN_UART_MASTER_RSP_IDLE_US     60u

/* OSRAM response timing.  Nichia uses a separate, longer turnaround below. */
#define CAN_UART_MASTER_RSP_START_US    250u

/* Absolute cap for an OSRAM response step. */
#define CAN_UART_MASTER_TIMEOUT_US      600u

/* Nichia response timing captured from the ECU startup sequence. */
#define CAN_UART_MASTER_NICHIA_RSP_START_US  3500u
#define CAN_UART_MASTER_NICHIA_TIMEOUT_US    4500u

/* The bus must be quiet for this long before a new request is transmitted, so
 * a late or trailing byte can never be miscounted as the echo of the next
 * request. */
#define CAN_UART_MASTER_QUIET_US        40u

#define CAN_UART_MASTER_OSRAM_RSP_MAX   34u
#define CAN_UART_MASTER_NICHIA_RSP_MAX  65u
#define CAN_UART_MASTER_NICHIA_REQ_MAX  72u
#define CAN_UART_MASTER_RSP_MAX         CAN_UART_MASTER_NICHIA_RSP_MAX

/* Raw capture window: a transaction may or may not put the transmit echo on the
 * bus, so room is reserved for the longest request plus the longest response. */
#define CAN_UART_MASTER_RAW_MAX         (CAN_UART_MASTER_NICHIA_REQ_MAX + \
                                         CAN_UART_MASTER_NICHIA_RSP_MAX)
#define CAN_UART_MASTER_UPLOADED_MAX    1400u
#define CAN_UART_MASTER_NICHIA_UPLOADED_MAX 300u

typedef enum
{
    CUM_PHASE_IDLE    = 0,   /* not running                                  */
    CUM_PHASE_STARTUP = 1,   /* replaying the start-up sequence              */
    CUM_PHASE_CYCLE   = 2    /* replaying the cyclic keep-alive and polling   */
} CanUartMasterPhase;

typedef struct
{
    volatile uint32 active;            /* 1 while the master drives the bus   */
    volatile uint32 phase;             /* CanUartMasterPhase                  */
    volatile uint32 stepIndex;         /* index inside the active sequence    */
    volatile uint32 startupDone;       /* start-up sequences completed        */
    volatile uint32 cyclesDone;        /* cyclic super-cycles completed       */

    volatile uint32 requestsSent;      /* requests put on the bus             */
    volatile uint32 responsesOk;       /* responses received before timeout   */
    volatile uint32 responseTimeouts;  /* expected response never arrived     */
    volatile uint32 echoTimeouts;      /* own transmission never echoed back  */
    volatile uint32 txFull;            /* TX FIFO full when queuing a request */
    volatile uint32 strayBytes;        /* bytes outside any response window   */
    volatile uint32 badSyncResponses;  /* answer longer than the expected frame */
    volatile uint32 quietWaits;        /* transmissions held back for a quiet bus */
    volatile uint32 echoSeenCount;     /* transactions whose echo was on the bus */
    volatile uint32 echoAbsentCount;   /* transactions with no echo on the bus   */
    volatile uint32 outRingDrops;      /* completed frames dropped before CPU0  */
    volatile uint32 outRingHighWater;  /* highest number of queued output frames */

    volatile uint32 uploadStepsReceived; /* valid upload step packets received */
    volatile uint32 uploadStepRejects;   /* upload step packets rejected       */
    volatile uint32 uploadCommitsReceived; /* upload commit packets received   */
    volatile uint32 uploadCommitRejects;   /* upload commits rejected           */

    volatile uint32 rxOverflows;       /* LSM RX FIFO overflows, bytes lost   */
    volatile uint32 tailBytes;         /* bytes still arriving after a closed answer */
    volatile uint32 lastTailLen;       /* tail of the last completed read      */

    volatile uint32 lastRspLen;        /* answer bytes after the echo         */
    volatile uint32 lastRspExpected;   /* answer length derived from the request */
    volatile uint32 lastRawLen;        /* raw bytes captured for the last step */
    volatile uint32 lastEchoLen;       /* leading bytes identified as own echo */
    volatile uint32 shortResponses;    /* answer ended before its full length */
    volatile uint32 lastRspDelayUs;    /* echo end to first response byte     */
    volatile uint32 lastRspSerial;     /* bumped after the whole snapshot below */
    volatile uint8  lastRequest[CAN_UART_MASTER_NICHIA_REQ_MAX];
    /* Request that produced lastResponse.  lastRequest belongs to the step in
     * flight and is already one step ahead by the time a debugger reads it. */
    volatile uint8  lastRspRequest[CAN_UART_MASTER_NICHIA_REQ_MAX];
    volatile uint8  lastResponse[CAN_UART_MASTER_NICHIA_RSP_MAX];

    /* Everything the LSM channel delivered for one read, captured after the
     * following inter-frame gap so late bytes are included.  Written once and
     * then frozen: lastFullRawLen is set last and acts as the ready flag, so a
     * debugger sees a coherent buffer.  Write 0 to it to arm the next capture. */
    volatile uint32 lastFullRawLen;
    volatile uint8  lastFullRawRequest[CAN_UART_MASTER_NICHIA_REQ_MAX];
    volatile uint8  lastFullRaw[CAN_UART_MASTER_RAW_MAX];
} CanUartMasterStats;

extern CanUartMasterStats g_canUartMasterStats;

/**
 * HCTRL byte the raw capture waits for, 0 to accept any read.
 * Writable from the debugger, e.g. 0xBE to capture a 16-register read.
 */
extern volatile uint8 g_canUartMasterRawFilter;

/** Reset state and telemetry.  Call once at start-up. */
void can_uart_master_init(void);

/**
 * Begin replaying the sequence from the start-up phase.
 * The caller must have stopped the bridge forwarding first.
 */
void can_uart_master_start(void);

/** Start the built-in Nichia startup table and discard any staged upload. */
void can_uart_master_start_hardcoded_nichia(void);

/** Hold Direct Control startup until an uploaded trace is committed. */
void can_uart_master_wait_for_uploaded_start(void);

boolean can_uart_master_stage_step(uint16 index, uint16 total, uint32 gapUs,
                                   uint8 len, uint8 expectResponse,
                                   const uint8 *data);
boolean can_uart_master_commit_staged(uint16 total);

/** Stop driving the bus and leave the TX line idle. */
void can_uart_master_stop(void);

/** TRUE while the master owns the LSM bus. */
boolean can_uart_master_is_active(void);

/**
 * TRUE once the LSM has been taken through the start-up sequence, or when no
 * master is running at all.  The ECU only starts the video stream after this
 * point, so the LVDS generator must wait for it too.
 */
boolean can_uart_master_startup_done(void);

/** Report that the LSM RX FIFO overflowed and its content was discarded. */
void can_uart_master_note_rx_overflow(void);

/**
 * Service the master state machine.  Call from the CPU2 loop.
 */
void can_uart_master_tick(void);

/**
 * Hand over one byte received on the LSM channel.
 * Called from the bridge relay pump, which is the single RX FIFO reader.
 */
void can_uart_master_feed_rx(uint8 b, uint32 stm);

/** Drain completed Direct Control transactions into the CPU0 diagnostic queue. */
void can_uart_master_poll_out(void);

#endif /* CAN_UART_MASTER_H */
