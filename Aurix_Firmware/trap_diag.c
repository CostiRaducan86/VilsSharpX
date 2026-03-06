/******************************************************************************
 * trap_diag.c — Bus-error trap diagnostic storage
 ******************************************************************************/

#include "trap_diag.h"

volatile TrapDiag g_trapDiag = {0};
