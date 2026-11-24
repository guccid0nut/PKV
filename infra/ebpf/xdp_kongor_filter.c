// SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)
// ============================================================================
// File: xdp_kongor_filter.c
// System: Heroes of Newerth - Dedicated Game Server Cluster
// Subsystem: Hardware-Level eBPF / XDP Network Ingress Abuse Filter
// Milestone 4.1: Dynamic Linux Kernel UDP Filtering & COMPEL Port Map Sync
// ============================================================================

#include <linux/bpf.h>
#include <linux/if_ether.h>
#include <linux/if_vlan.h>
#include <linux/ip.h>
#include <linux/ipv6.h>
#include <linux/in.h>
#include <linux/udp.h>
#include <bpf/bpf_helpers.h>
#include <bpf/bpf_endian.h>

#ifndef LIBBPF_PIN_BY_NAME
#define LIBBPF_PIN_BY_NAME 1
#endif

// Minimum packet sizes defined by COMPEL ClientPacketReader (proxy ports 21235+/21735+):
// 40 bytes Watermark + 2 bytes Connection + 1 byte Packet Type = 43 bytes
#define KONGOR_WATERMARK_PREFIX_LEN 40
#define KONGOR_PROXY_GAME_MIN_LEN   43
#define KONGOR_PROXY_VOICE_MIN_LEN  41

// Minimum valid K2 UDP payload sizes on direct non-proxy server ports (11235+/11735+):
// 2 bytes Connection ID / Sequence + 1 byte K2 NetCmd/Handshake Opcode = 3 bytes (2 bytes for voice keepalive)
#define KONGOR_DIRECT_GAME_MIN_LEN  3
#define KONGOR_DIRECT_VOICE_MIN_LEN 2

// Telemetry counters index
#define STAT_PASS_GAME              0
#define STAT_PASS_VOICE             1
#define STAT_DROP_TOO_SHORT         2
#define STAT_DROP_MALFORMED         3
#define STAT_PASS_NON_GAME          4
#define STAT_DROP_SPOOFED_COOKIE    5
#define STAT_DROP_UNREGISTERED_PORT 6
#define STAT_MAX_ENTRIES            7

// Port entry stored in the dynamic active ports map
struct kongor_port_entry {
    __u32 pid;
    __u32 flags; // bit 0: is_voice (1=voice, 0=game), bit 1: is_proxy (1=proxy, 0=direct)
    __u64 registered_at_ns;
};

// Pinned BPF hash map for active dedicated server game & voice ports.
// Dynamic lifecycle port map synchronized by COMPEL (/sys/fs/bpf/kongor_active_ports)
struct {
    __uint(type, BPF_MAP_TYPE_HASH);
    __type(key, __u16); // destination port in host byte order
    __type(value, struct kongor_port_entry);
    __uint(max_entries, 1024);
    __uint(pinning, LIBBPF_PIN_BY_NAME);
} kongor_active_ports SEC(".maps");

struct {
    __uint(type, BPF_MAP_TYPE_PERCPU_ARRAY);
    __type(key, __u32);
    __type(value, __u64);
    __uint(max_entries, STAT_MAX_ENTRIES);
} kongor_stats SEC(".maps");

struct kongor_vlan_hdr {
    __be16 h_vlan_TCI;
    __be16 h_vlan_encapsulated_proto;
};

static __always_inline void increment_stat(__u32 key) {
    __u64 *val = bpf_map_lookup_elem(&kongor_stats, &key);
    if (val) {
        *val += 1;
    }
}

static __always_inline struct kongor_port_entry *lookup_kongor_port(__u16 port) {
    return bpf_map_lookup_elem(&kongor_active_ports, &port);
}

static __always_inline int is_kongor_udp_port(__u16 port) {
    // Standard and Proxy Port Ranges for Heroes of Newerth Dedicated Servers:
    // Game UDP:       11235 - 11534
    // Voice UDP:      11735 - 12034
    // Proxy Game UDP: 21235 - 21534
    // Proxy Voice UDP: 21735 - 22034
    if ((port >= 11235 && port <= 11534) ||
        (port >= 11735 && port <= 12034) ||
        (port >= 21235 && port <= 21534) ||
        (port >= 21735 && port <= 22034)) {
        return 1;
    }
    return 0;
}

static __always_inline int is_voice_port(__u16 port) {
    return ((port >= 11735 && port <= 12034) || (port >= 21735 && port <= 22034)) ? 1 : 0;
}

static __always_inline int is_proxy_port(__u16 port) {
    return ((port >= 21235 && port <= 21534) || (port >= 21735 && port <= 22034)) ? 1 : 0;
}

SEC("xdp")
int xdp_kongor_ingress_filter(struct xdp_md *ctx) {
    void *data_end = (void *)(long)ctx->data_end;
    void *data = (void *)(long)ctx->data;

    // 1. Validate Ethernet Header + Unwrap up to 2 IEEE 802.1Q / 802.1ad VLAN tags
    struct ethhdr *eth = data;
    if ((void *)(eth + 1) > data_end) {
        return XDP_PASS;
    }

    __be16 h_proto = eth->h_proto;
    void *nh = (void *)(eth + 1);

#pragma clang loop unroll(full)
    for (int i = 0; i < 2; i++) {
        if (h_proto == bpf_htons(ETH_P_8021Q) || h_proto == bpf_htons(ETH_P_8021AD)) {
            struct kongor_vlan_hdr *vh = nh;
            if ((void *)(vh + 1) > data_end) {
                return XDP_DROP;
            }
            h_proto = vh->h_vlan_encapsulated_proto;
            nh = (void *)(vh + 1);
        }
    }

    // 2. Validate IPv4 or IPv6 Header & Locate UDP Header
    struct udphdr *udph = 0;
    if (h_proto == bpf_htons(ETH_P_IP)) {
        struct iphdr *iph = nh;
        if ((void *)(iph + 1) > data_end) {
            return XDP_PASS;
        }
        if (iph->protocol != IPPROTO_UDP) {
            return XDP_PASS;
        }
        __u32 ip_hdr_len = iph->ihl * 4;
        if (ip_hdr_len < sizeof(struct iphdr) || ip_hdr_len > 60) {
            increment_stat(STAT_DROP_MALFORMED);
            return XDP_DROP;
        }
        if ((void *)((char *)iph + ip_hdr_len) > data_end) {
            increment_stat(STAT_DROP_MALFORMED);
            return XDP_DROP;
        }
        udph = (void *)((char *)iph + ip_hdr_len);
    } else if (h_proto == bpf_htons(ETH_P_IPV6)) {
        struct ipv6hdr *iph6 = nh;
        if ((void *)(iph6 + 1) > data_end) {
            return XDP_PASS;
        }
        if (iph6->nexthdr != IPPROTO_UDP) {
            return XDP_PASS;
        }
        udph = (void *)(iph6 + 1);
    } else {
        return XDP_PASS;
    }

    // 3. Validate UDP Header
    if ((void *)(udph + 1) > data_end) {
        return XDP_PASS;
    }

    __u16 dst_port = bpf_ntohs(udph->dest);
    struct kongor_port_entry *port_entry = lookup_kongor_port(dst_port);
    int is_active = 0;
    int voice = 0;
    int proxy_mode = 0;

    if (port_entry) {
        is_active = 1;
        voice = (port_entry->flags & 1) ? 1 : 0;
        proxy_mode = (port_entry->flags & 2) ? 1 : 0;
    } else if (is_kongor_udp_port(dst_port)) {
        // Fallback to static port ranges if dynamic registration map is empty/unpinned
        is_active = 1;
        voice = is_voice_port(dst_port);
        proxy_mode = is_proxy_port(dst_port);
    }

    if (!is_active) {
        increment_stat(STAT_PASS_NON_GAME);
        return XDP_PASS;
    }

    // 4. Inspect Game Server UDP Payload
    unsigned char *payload = (unsigned char *)(udph + 1);
    __u32 payload_len = (__u32)((char *)data_end - (char *)payload);

    if (!proxy_mode) {
        // Direct non-proxy K2 server ports (11235-11534 / 11735-12034):
        // Preserve short legitimate K2 client handshake/ping/connect datagrams (>= 3B game, >= 2B voice)
        // while dropping 0-byte/1-byte reflection & scanner junk at the NIC driver layer.
        __u32 direct_min = voice ? KONGOR_DIRECT_VOICE_MIN_LEN : KONGOR_DIRECT_GAME_MIN_LEN;
        if (payload_len < direct_min) {
            increment_stat(STAT_DROP_TOO_SHORT);
            return XDP_DROP;
        }
        increment_stat(voice ? STAT_PASS_VOICE : STAT_PASS_GAME);
        return XDP_PASS;
    }

    // 5. COMPEL Proxy Ports (21235-21534 / 21735-22034): Enforce 40-byte Watermark Envelope
    __u32 min_len = voice ? KONGOR_PROXY_VOICE_MIN_LEN : KONGOR_PROXY_GAME_MIN_LEN;
    if (payload_len < min_len) {
        increment_stat(STAT_DROP_TOO_SHORT);
        return XDP_DROP;
    }

    if ((void *)(payload + KONGOR_WATERMARK_PREFIX_LEN) > data_end) {
        increment_stat(STAT_DROP_TOO_SHORT);
        return XDP_DROP;
    }

    // Offset 28: Challenge (32-bit uint), Offset 32: Counter (16-bit uint)
    __u32 challenge = *(__u32 *)(payload + 28);
    __u16 counter = *(__u16 *)(payload + 32);

    if (challenge == 0xFFFFFFFF && counter == 0xFFFF) {
        increment_stat(STAT_DROP_MALFORMED);
        return XDP_DROP;
    }

    if (challenge == 0 && counter == 0) {
        increment_stat(STAT_DROP_MALFORMED);
        return XDP_DROP;
    }

    increment_stat(voice ? STAT_PASS_VOICE : STAT_PASS_GAME);
    return XDP_PASS;
}

char _license[] SEC("license") = "Dual BSD/GPL";
