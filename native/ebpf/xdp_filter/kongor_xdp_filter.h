// SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

#ifndef KONGOR_XDP_FILTER_H
#define KONGOR_XDP_FILTER_H

#ifdef __KERNEL__
#include <linux/types.h>
#else
#include <stdint.h>
typedef uint8_t __u8;
typedef uint16_t __u16;
typedef uint32_t __u32;
typedef uint64_t __u64;
#endif

/*
 * [EXPERIMENTAL - NOT DEPLOYED]
 * Project Kongor Dedicated Server XDP UDP Scrubber Constants.
 * NOTE: Real K2 engine UDP frames on ports 11235-11238 begin with a 2-byte sequence/OOB header
 * followed by a 1-byte packet opcode (NOT an ASCII magic string). Enforcing a bogus 4-byte ASCII
 * magic would drop 100% of legitimate K2 client traffic; therefore we enforce minimum K2 frame
 * length (>= 3 payload bytes) + zero-amplification probe rejection + per-IP token-bucket rate limiting.
 */
#define KONGOR_MIN_K2_PAYLOAD_LEN    3U
#define KONGOR_NULL_PROBE_HEADER     0x00000000U
#define KONGOR_PORT_MIN              11235U
#define KONGOR_PORT_MAX              11238U

/* XDP Filter Telemetry Metric Indexes */
enum kongor_stat_index {
    KONGOR_STAT_PACKETS_TOTAL = 0,
    KONGOR_STAT_PACKETS_PASSED = 1,
    KONGOR_STAT_PACKETS_DROPPED_MALFORMED = 2,
    KONGOR_STAT_PACKETS_DROPPED_MAGIC = 3,
    KONGOR_STAT_PACKETS_DROPPED_RATELIMIT = 4,
    KONGOR_STAT_BYTES_PROCESSED = 5,
    KONGOR_STAT_MAX = 6
};

/* Rate Limiter Entry (Token Bucket) */
struct rate_limit_entry {
    __u64 last_time_ns;
    __u32 tokens;
    __u32 drop_count;
};

#endif /* KONGOR_XDP_FILTER_H */
