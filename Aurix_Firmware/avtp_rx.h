#ifndef AVTP_RX_H
#define AVTP_RX_H

/******************************************************************************
 * avtp_rx.h — AVTP/RVF ingest for Direct Control Mode
 *
 * In Direct Control Mode the pixel content comes from the PC as an IEEE 1722
 * Raw Video Format stream on ethertype 0x22F0.  One 320x80 Gray8 frame is
 * carried by 20 packets of 4 lines each:
 *
 *   Ethernet header [0..11]  destination and source MAC
 *   [12..13]                 0x8100 VLAN tag (optional, skipped) then 0x22F0
 *   AVTP payload byte 22     end-of-frame marker in bit 0x10
 *   AVTP payload byte 31     first line number of this packet, 1-based
 *   AVTP payload bytes 32..  4 lines x 320 pixel bytes = 1280 bytes
 *
 * The layout mirrors AvtpRvfParser.cs on the PC side, so the same stream feeds
 * pane A on the PC and the LVDS generator on the AURIX.
 *
 * A frame is complete when the end-of-frame marker arrives and all 20 chunks
 * were received.  Incomplete frames are dropped and counted: the LSM must never
 * be shown a partially updated image.
 ******************************************************************************/

#include "Ifx_Types.h"

/* ─── Protocol constants ─── */
#define AVTP_ETHERTYPE            0x22F0u
#define AVTP_VLAN_TPID            0x8100u
#define AVTP_VLAN_TPID_STACKED    0x88A8u

#define AVTP_RVF_W                320u
#define AVTP_RVF_H                80u
#define AVTP_RVF_LINES_PER_PACKET 4u
#define AVTP_RVF_PAYLOAD_OFFSET   32u
#define AVTP_RVF_CHUNK_BYTES      (AVTP_RVF_W * AVTP_RVF_LINES_PER_PACKET)   /* 1280 */
#define AVTP_RVF_CHUNKS           (AVTP_RVF_H / AVTP_RVF_LINES_PER_PACKET)   /*   20 */
#define AVTP_RVF_FRAME_BYTES      (AVTP_RVF_W * AVTP_RVF_H)                  /* 25600 */

#define AVTP_RVF_EOF_BYTE         22u
#define AVTP_RVF_EOF_MASK         0x10u
#define AVTP_RVF_LINE_BYTE        31u

/* ─── Telemetry ─── */
typedef struct
{
    volatile uint32 packetsAccepted;    /* well-formed RVF chunks consumed     */
    volatile uint32 packetsRejected;    /* bad length, line number or geometry */
    volatile uint32 packetsForeignSource; /* dropped by the source MAC filter  */
    volatile uint32 framesComplete;     /* frames handed to the generator      */
    volatile uint32 framesIncomplete;   /* end-of-frame with missing chunks    */
    volatile uint32 framesRestarted;    /* new frame began before end-of-frame */
    volatile uint32 framesDroppedBusy;  /* completed frame not yet consumed    */
    volatile uint32 duplicateChunks;    /* same chunk received twice per frame */
    volatile uint32 lastChunkMask;      /* chunk bitmask of the last frame     */
    volatile uint32 lastLine;           /* last accepted first-line number     */
    volatile uint32 lastSrcMac;         /* low 4 bytes of the last source MAC  */
    volatile uint32 lastStreamId;       /* low 4 bytes of the last stream id   */
    volatile uint32 sourceChanges;      /* source MAC / stream id alternations */
} AvtpRxStats;

extern AvtpRxStats g_avtpRxStats;

/* ─── API ─── */

/** Reset the reassembly state and telemetry. */
void avtp_rx_init(void);
void avtp_rx_reset(void);

/**
 * Restrict reassembly to one Ethernet source MAC.
 *
 * Chunks are keyed only by line number, so two AVTP sources sending to the same
 * multicast destination (PC generator plus a CANoe stream) merge into a single
 * frame and destroy it.  The PC arms this filter with the MAC it wants the
 * generator to follow.
 *
 * @param mac6  6-byte source MAC, or NULL_PTR to accept every source again
 */
void avtp_rx_set_source_filter(const uint8 *mac6);

/**
 * Feed one received Ethernet packet.
 *
 * @param packet  Ethernet frame starting at the destination MAC
 * @param len     Captured packet length in bytes
 * @return TRUE if the packet was an AVTP/RVF chunk (consumed by this module)
 */
boolean avtp_rx_handle_packet(const uint8 *packet, uint32 len);

/** TRUE while a completed frame is waiting to be consumed. */
boolean avtp_rx_frame_available(void);

/**
 * Retrieve a completed frame, if one is pending.
 * The pointer stays valid until the reassembler completes the frame after the
 * next one, so the caller must consume it within the same main-loop pass.
 *
 * @return Pointer to AVTP_RVF_FRAME_BYTES Gray8 pixels, or NULL_PTR
 */
const uint8 *avtp_rx_take_frame(void);

#endif /* AVTP_RX_H */
