// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef KONGOR_HON_PING_PROTOCOL_H
#define KONGOR_HON_PING_PROTOCOL_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#ifdef _WIN32
#include <BaseTsd.h>
typedef SSIZE_T hon_ping_ssize_t;
#else
#include <sys/types.h>
typedef ssize_t hon_ping_ssize_t;
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Canonical host-byte-order IPv4 loopback address (127.0.0.1 = 0x7F000001). */
#define HON_PING_LOOPBACK_IPV4_HOST 0x7F000001U

/*
 * [L2-P0-03] Exact 6-byte raw server-browser ping probe predicate:
 * K2 client raw UDP ping probes are strictly 6 bytes:
 *   [0x00, 0x00, 0x01, 0xCA, <seq_lo>, <seq_hi>]
 * Unreliable/sequenced gameplay UDP packets exceed 6 bytes (n > 6) and can
 * legitimately carry 0x01, 0xCA at offsets [2..3]; they MUST NEVER be swallowed.
 */
static inline int hon_ping_is_exact_6byte_probe(const unsigned char* pkt, hon_ping_ssize_t len) {
    if (!pkt || len != 6) {
        return 0;
    }
    return (pkt[0] == 0x00 && pkt[1] == 0x00 && pkt[2] == 0x01 && pkt[3] == 0xCA) ? 1 : 0;
}

/*
 * [L2-P0-03] Loopback-only guard for internal "map:<name>" state notifications.
 * Rejects spoofed external UDP packets attempting to mutate g_map_name / g_is_game_active.
 */
static inline int hon_ping_is_loopback_ipv4_host(uint32_t host_order_ipv4) {
    return (host_order_ipv4 == HON_PING_LOOPBACK_IPV4_HOST) ? 1 : 0;
}

static inline int hon_ping_extract_loopback_map_update(
    const unsigned char* pkt,
    hon_ping_ssize_t len,
    uint32_t sender_ipv4_host_order,
    char* out_map,
    size_t out_map_cap)
{
    if (!pkt || len <= 4 || !out_map || out_map_cap < 2) {
        return 0;
    }
    if (!hon_ping_is_loopback_ipv4_host(sender_ipv4_host_order)) {
        return 0;
    }
    if (memcmp(pkt, "map:", 4) != 0) {
        return 0;
    }

    size_t map_len = (size_t)(len - 4);
    if (map_len >= out_map_cap) {
        map_len = out_map_cap - 1;
    }
    memcpy(out_map, pkt + 4, map_len);
    out_map[map_len] = '\0';

    while (map_len > 0 &&
           (out_map[map_len - 1] == '\n' ||
            out_map[map_len - 1] == '\r' ||
            out_map[map_len - 1] == ' '  ||
            out_map[map_len - 1] == '\t')) {
        out_map[--map_len] = '\0';
    }

    return (map_len > 0) ? 1 : 0;
}

#ifdef __cplusplus
}
#endif

#endif /* KONGOR_HON_PING_PROTOCOL_H */
