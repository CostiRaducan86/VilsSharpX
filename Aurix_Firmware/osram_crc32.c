/******************************************************************************
 * osram_crc32.c — CRC-32 for Osram UART frame verification
 *
 * Matches the ECU CRC algorithm (uart_appl_fast_channel_process)
 * which uses TriCore CRC32L.W + __shuffle intrinsics.
 *
 * The ECU's combined __crc32lw + __shuffle(data, 0x1E4) is algebraically
 * equivalent to standard MSB-first CRC-32 on the raw (unshuffled) data,
 * processed byte-by-byte in sequential order (byte 0, 1, 2, …).
 *
 * CRC32L.W processes the 32-bit word from the LSB upward → on LE it sees
 * bytes in memory order (B0, B1, B2, B3).  __shuffle reflects each byte's
 * bits, which cancels COD32L.W's bit reflection → net effect is standard
 * MSB-first CRC on each byte in natural sequential order.
 *
 * Software-equivalent implementation:
 *   - MSB-first CRC table, poly 0x04C11DB7
 *   - Raw seed 0xDEADAFFE
 *   - Bytes processed sequentially (byte 0, 1, 2, …)
 *   - NO byte reflection (raw bytes used directly)
 *   - Final: bswap32 only
 *
 * Verified: 25600×0x00 → 0x66844BF6, 25600×0x0E → 0x01AE790B
 ******************************************************************************/

#include "osram_crc32.h"

/* ==================== Table ==================== */

/** MSB-first CRC-32 lookup table: poly 0x04C11DB7. */
static uint32 s_crc32_table[256];

/* ==================== Init ==================== */

void osram_crc32_init(void)
{
    for (uint32 i = 0u; i < 256u; i++)
    {
        uint32 crc = i << 24;
        for (uint32 bit = 0u; bit < 8u; bit++)
        {
            if (crc & 0x80000000u)
                crc = (crc << 1) ^ 0x04C11DB7u;
            else
                crc = crc << 1;
        }
        s_crc32_table[i] = crc;
    }
}

/* ==================== Helpers ==================== */

static uint32 bswap32(uint32 v)
{
    return ((v >> 24) & 0xFFu)
         | ((v >>  8) & 0xFF00u)
         | ((v <<  8) & 0xFF0000u)
         | ((v << 24) & 0xFF000000u);
}

/* ==================== CRC computation ==================== */

uint32 osram_crc32_compute(const uint8 *data, uint32 len)
{
    uint32 crc = OSRAM_CRC32_RAW_SEED;   /* 0xDEADAFFE */

    /* Standard MSB-first CRC, sequential byte processing.
     * CRC32L.W on LE processes bytes in memory order (B0, B1, B2, B3).
     * Combined with __shuffle byte-reflection, the net effect is
     * standard MSB-first CRC on raw bytes in sequential order. */
    for (uint32 i = 0u; i < len; i++)
    {
        uint8 idx = (uint8)((crc >> 24) ^ data[i]);
        crc = (crc << 8) ^ s_crc32_table[idx];
    }
    return bswap32(crc);
}

/* ==================== CRC verification ==================== */

boolean osram_crc32_verify(const uint8 *pixelData, uint32 pixelLen,
                            const uint8 *crc4)
{
    uint32 computed = osram_crc32_compute(pixelData, pixelLen);

    /* CRC bytes from UART in LE order (TriCore is LE):
     * crc4[0] = LSB, crc4[3] = MSB */
    uint32 received = (uint32)crc4[0]
                    | ((uint32)crc4[1] <<  8)
                    | ((uint32)crc4[2] << 16)
                    | ((uint32)crc4[3] << 24);

    return (boolean)(computed == received);
}

/* ==================== Self-test ==================== */

uint32 osram_crc32_selftest_zeros(uint32 numBytes)
{
    /* For zero data: reflect(0x00) = 0x00 and byte order is irrelevant.
     * Just apply the CRC table numBytes times with zero data bytes. */
    uint32 crc = OSRAM_CRC32_RAW_SEED;

    for (uint32 i = 0u; i < numBytes; i++)
    {
        uint8 idx = (uint8)(crc >> 24);
        crc = (crc << 8) ^ s_crc32_table[idx];
    }

    return bswap32(crc);
}
