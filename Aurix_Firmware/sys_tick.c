/**
 * @file sys_tick.c
 * @brief 1 ms system tick counter on CPU3 / STM3 comparator 0.
 *
 * The compare register is advanced by a fixed number of ticks per period
 * (absolute schedule), so the counter does not drift with ISR latency.  If
 * the ISR is delayed by more than one period (debugger halt on CPU3), the
 * missed milliseconds are caught up instead of waiting for a 32-bit STM wrap.
 */

#include "sys_tick.h"

#include "IfxCpu_Irq.h"
#include "Stm/Std/IfxStm.h"

#define SYS_TICK_STM          (&MODULE_STM3)
#define SYS_TICK_COMPARATOR   (IfxStm_Comparator_0)
#define SYS_TICK_PERIOD_MS    (1u)

/* Unique priority; keep literal in IFX_INTERRUPT (TASKING, see camera_trigger.c) */
#define SYS_TICK_ISR_PRIO     (30)

volatile uint32 g_sysTickMs;
volatile uint32 g_sysTickLateCount;

static uint32 s_ticksPerPeriod;

IFX_INTERRUPT(sys_tick_isr, 3, 30);

void sys_tick_isr(void)
{
    uint32 next = IfxStm_getCompare(SYS_TICK_STM, SYS_TICK_COMPARATOR) + s_ticksPerPeriod;
    uint32 ms   = g_sysTickMs + SYS_TICK_PERIOD_MS;

    /* Catch up if the next match is already in the past. */
    if ((sint32)(next - IfxStm_getLower(SYS_TICK_STM)) <= 0)
    {
        g_sysTickLateCount++;
        do
        {
            next += s_ticksPerPeriod;
            ms   += SYS_TICK_PERIOD_MS;
        } while ((sint32)(next - IfxStm_getLower(SYS_TICK_STM)) <= 0);
    }

    IfxStm_updateCompare(SYS_TICK_STM, SYS_TICK_COMPARATOR, next);
    g_sysTickMs = ms;
}

void sys_tick_init(void)
{
    IfxStm_CompareConfig cfg;

    g_sysTickMs        = 0u;
    g_sysTickLateCount = 0u;
    s_ticksPerPeriod   = (uint32)IfxStm_getTicksFromMilliseconds(SYS_TICK_STM, SYS_TICK_PERIOD_MS);

    IfxStm_initCompareConfig(&cfg);
    cfg.comparator          = SYS_TICK_COMPARATOR;
    cfg.comparatorInterrupt = IfxStm_ComparatorInterrupt_ir0;
    cfg.ticks               = s_ticksPerPeriod;
    cfg.triggerPriority     = (uint16)SYS_TICK_ISR_PRIO;
    cfg.typeOfService       = IfxSrc_Tos_cpu3;

    IfxStm_initCompare(SYS_TICK_STM, &cfg);
}

uint32 sys_tick_get_ms(void)
{
    return g_sysTickMs;
}
