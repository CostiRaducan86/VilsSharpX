#ifndef SYS_TICK_H
#define SYS_TICK_H

/**
 * @file sys_tick.h
 * @brief 1 ms system tick counter (OS-style uptime counter).
 *
 * Runs on CPU3 (otherwise idle) using STM3 comparator 0, so it does not add
 * any interrupt load to CPU0 (LVDS/GETH) or CPU2 (CAN-UART bridge) and does
 * not touch STM0 (comparator 0 = LVDS TX row timer, comparator 1 = camera
 * trigger).
 *
 * Add g_sysTickMs to the winIDEA Watch window to observe it live.
 */

#include "Ifx_Types.h"

/** Milliseconds since sys_tick_init(). Wraps after ~49.7 days. */
extern volatile uint32 g_sysTickMs;

/** Compare matches that arrived late (e.g. CPU3 halted by the debugger).
 *  The missed milliseconds are still added to g_sysTickMs. */
extern volatile uint32 g_sysTickLateCount;

/** Start the 1 ms tick. Must be called from CPU3 (ISR is serviced by CPU3). */
void sys_tick_init(void);

/** Current tick value in milliseconds (safe to call from any core). */
uint32 sys_tick_get_ms(void);

#endif /* SYS_TICK_H */
