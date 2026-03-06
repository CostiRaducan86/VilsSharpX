#ifndef OSRAM_CRC32_H
#define OSRAM_CRC32_H

/******************************************************************************
 * osram_crc32.h — CRC-32 for Osram UART frame verification
 *
 * Matches the ECU algorithm in uart_appl_fast_channel_process:
 *   - TriCore __crc32lw + __shuffle(data, 0x1E4) + post-processing
 *   - ECU seed (HW format): UART_APPL_2_CRC32_SEED_VALUE = 0x800A4A84
 *   - Equivalent raw MSB-first seed: 0xDEADAFFE
 *
 * The ECU's __shuffle + __crc32lw combination is algebraically equivalent
 * to standard MSB-first CRC-32 on the raw (unshuffled) data.
 *
 * Software-equivalent computation:
 *   - MSB-first CRC table, poly 0x04C11DB7, raw seed 0xDEADAFFE
 *   - Bytes processed sequentially (byte 0, 1, 2, …)
 *   - NO byte reflection (raw bytes used directly)
 *   - Final: bswap32 only
 *
 * Verified: 25600×0x00 → 0x66844BF6, 25600×0x0E → 0x01AE790B
 *
 * RAM: 1024 bytes (CRC table).
 ******************************************************************************/

#include "Ifx_Types.h"

/* Raw MSB-first CRC-32 seed = reflect32(0x800A4A84) ^ 0xFFFFFFFF */
#define OSRAM_CRC32_RAW_SEED      0xDEADAFFEu

/* ECU seed in HW format (for reference / documentation) */
#define OSRAM_CRC32_ECU_SEED      0x800A4A84u

/**
 * Initialize the CRC-32 lookup table.  Call once at startup.
 */
void osram_crc32_init(void);

/**
 * Compute Osram CRC-32 over pixel data.
 *
 * @param data  Pixel data buffer
 * @param len   Data length in bytes (25600 for a standard Osram frame)
 * @return CRC-32 value matching ECU/Osram UART transmission
 */
uint32 osram_crc32_compute(const uint8 *data, uint32 len);

/**
 * Verify a received Osram frame CRC.
 *
 * @param pixelData  Pixel data (25600 bytes)
 * @param pixelLen   Data length in bytes
 * @param crc4       Pointer to 4 CRC bytes as received from UART stream (LE)
 * @return TRUE if CRC matches, FALSE otherwise
 */
boolean osram_crc32_verify(const uint8 *pixelData, uint32 pixelLen,
                            const uint8 *crc4);

/**
 * Compute CRC-32 for all-zero data (no buffer needed).
 *
 * @param numBytes  Number of zero bytes (must be multiple of 4)
 * @return CRC-32 value (expected 0x66844BF6 for 25600 zeros)
 */
uint32 osram_crc32_selftest_zeros(uint32 numBytes);

#endif /* OSRAM_CRC32_H */
