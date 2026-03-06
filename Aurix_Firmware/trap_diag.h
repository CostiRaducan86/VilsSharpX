#ifndef TRAP_DIAG_H
#define TRAP_DIAG_H

/******************************************************************************
 * trap_diag.h — Universal trap diagnostics (all trap classes)
 *
 * Defines hooks for ALL iLLD trap classes so the trap handler stores
 * diagnostic info in g_trapDiag before halting at __debug().
 * Include this header BEFORE the iLLD Trap files are compiled
 * (e.g., via Ifx_Cfg.h or a forced-include).
 *
 * After ANY trap, inspect g_trapDiag in the debugger:
 *   trapClass = Trap class (0=MMU, 1=IPE, 2=IE, 3=CME, 4=BE, 5=Assert, 7=NMI)
 *   trapId    = TIN value — identifies exact error type within the class
 *   trapAddr  = A[11] = return address / faulting PC
 *   trapCpu   = CPU core ID
 *   hitCount  = increments on each trap (detects repeated traps)
 *
 * Class 1 (IPE) TIN values:
 *   1=PRIV  2=MPR  3=MPW  4=MPX  5=MPP  6=MPN  7=GRWP
 * Class 4 (BE) TIN values:
 *   1=PSE  2=DSE  3=DAE  5=PIE  6=DIE  7=TAE
 ******************************************************************************/

/*
 * Ifx_Cfg.h is also included by toolchain/startup *.src units.
 * Those assembler/preprocessed units cannot parse C declarations.
 */
#if !defined(__ASSEMBLER__) && !defined(__ASSEMBLY__) && !defined(_ASMLANGUAGE)

/*
 * Use plain C types instead of uint32 to avoid a circular include:
 *   Ifx_Types.h -> Ifx_Cfg.h -> trap_diag.h -> Ifx_Types.h (guard blocks)
 * On AURIX/TASKING, uint32 == unsigned int.
 */
typedef struct
{
    unsigned int trapClass;       /**< Trap class (0-7) */
    unsigned int trapId;          /**< TIN value — identifies exact error type */
    unsigned int trapAddr;        /**< A[11] = return address from trap (faulting PC) */
    unsigned int trapCpu;         /**< CPU core ID */
    volatile unsigned int hitCount; /**< Increments on each trap */
} TrapDiag;

extern volatile TrapDiag g_trapDiag;

/* Common recording macro used by all hooks */
#define TRAP_DIAG_RECORD(trapWatch) \
    do { \
        g_trapDiag.trapClass = (trapWatch).tClass; \
        g_trapDiag.trapId    = (trapWatch).tId;    \
        g_trapDiag.trapAddr  = (trapWatch).tAddr;  \
        g_trapDiag.trapCpu   = (trapWatch).tCpu;   \
        g_trapDiag.hitCount++; \
    } while(0)

/* Class 0: Memory Management Error */
#ifndef IFX_CFG_CPU_TRAP_MME_HOOK
#define IFX_CFG_CPU_TRAP_MME_HOOK(trapWatch)    TRAP_DIAG_RECORD(trapWatch)
#endif

/* Class 1: Internal Protection Error (PRIV/MPR/MPW/MPX/MPP/MPN/GRWP) */
#ifndef IFX_CFG_CPU_TRAP_IPE_HOOK
#define IFX_CFG_CPU_TRAP_IPE_HOOK(trapWatch)    TRAP_DIAG_RECORD(trapWatch)
#endif

/* Class 2: Instruction Error */
#ifndef IFX_CFG_CPU_TRAP_IE_HOOK
#define IFX_CFG_CPU_TRAP_IE_HOOK(trapWatch)     TRAP_DIAG_RECORD(trapWatch)
#endif

/* Class 3: Context Management Error */
#ifndef IFX_CFG_CPU_TRAP_CME_HOOK
#define IFX_CFG_CPU_TRAP_CME_HOOK(trapWatch)    TRAP_DIAG_RECORD(trapWatch)
#endif

/* Class 4: Bus Error (DSE/PSE/DAE/PIE/DIE/TAE) */
#ifndef IFX_CFG_CPU_TRAP_BE_HOOK
#define IFX_CFG_CPU_TRAP_BE_HOOK(trapWatch)     TRAP_DIAG_RECORD(trapWatch)
#endif

/* Class 5: Assertion Trap */
#ifndef IFX_CFG_CPU_TRAP_ASSERT_HOOK
#define IFX_CFG_CPU_TRAP_ASSERT_HOOK(trapWatch) TRAP_DIAG_RECORD(trapWatch)
#endif

/* Class 7: Non-Maskable Interrupt */
#ifndef IFX_CFG_CPU_TRAP_NMI_HOOK
#define IFX_CFG_CPU_TRAP_NMI_HOOK(trapWatch)    TRAP_DIAG_RECORD(trapWatch)
#endif

#else

/* No-op in assembler units that include Ifx_Cfg.h. */
#ifndef IFX_CFG_CPU_TRAP_MME_HOOK
#define IFX_CFG_CPU_TRAP_MME_HOOK(trapWatch)
#endif
#ifndef IFX_CFG_CPU_TRAP_IPE_HOOK
#define IFX_CFG_CPU_TRAP_IPE_HOOK(trapWatch)
#endif
#ifndef IFX_CFG_CPU_TRAP_IE_HOOK
#define IFX_CFG_CPU_TRAP_IE_HOOK(trapWatch)
#endif
#ifndef IFX_CFG_CPU_TRAP_CME_HOOK
#define IFX_CFG_CPU_TRAP_CME_HOOK(trapWatch)
#endif
#ifndef IFX_CFG_CPU_TRAP_BE_HOOK
#define IFX_CFG_CPU_TRAP_BE_HOOK(trapWatch)
#endif
#ifndef IFX_CFG_CPU_TRAP_ASSERT_HOOK
#define IFX_CFG_CPU_TRAP_ASSERT_HOOK(trapWatch)
#endif
#ifndef IFX_CFG_CPU_TRAP_NMI_HOOK
#define IFX_CFG_CPU_TRAP_NMI_HOOK(trapWatch)
#endif

#endif

#endif /* TRAP_DIAG_H */
