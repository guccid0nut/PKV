// SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)
/*
 * [EXPERIMENTAL - NOT DEPLOYED]
 * Project Kongor: Linux Kernel eBPF / XDP Dedicated Server UDP Packet Scrubber
 * Protects ports 11235-11238 against truncated UDP frames, null reflection probes, and volumetric bursts.
 */

#include <linux/bpf.h>
#include <linux/if_ether.h>
#include <linux/ip.h>
#include <linux/in.h>
#include <linux/udp.h>
#include <bpf/bpf_helpers.h>
#include <bpf/bpf_endian.h>
#include "kongor_xdp_filter.h"

char LICENSE[] SEC("license") = "Dual BSD/GPL";

/*
 * BPF Telemetry Array Map for atomic packet metrics
 */
struct {
    __uint(type, BPF_MAP_TYPE_PERCPU_ARRAY);
    __type(key, __u32);
    __type(value, __u64);
    __uint(max_entries, KONGOR_STAT_MAX);
} kongor_stats_map SEC(".maps");

/*
 * BPF LRU Hash Map for IP rate limiting / DDoS burst filtering
 * Key: __u32 IPv4 address, Value: struct rate_limit_entry
 */
struct {
    __uint(type, BPF_MAP_TYPE_LRU_HASH);
    __type(key, __u32);
    __type(value, struct rate_limit_entry);
    __uint(max_entries, 65536);
} kongor_rate_limit_map SEC(".maps");

static __always_inline void increment_stat(__u32 index, __u64 bytes)
{
    __u64 *val = bpf_map_lookup_elem(&kongor_stats_map, &index);
    if (val) {
        *val += bytes;
    }
}

SEC("xdp")
int kongor_xdp_filter(struct xdp_md *ctx)
{
    void *data = (void *)(long)ctx->data;
    void *data_end = (void *)(long)ctx->data_end;
    __u64 packet_len = (__u64)(data_end - data);

    increment_stat(KONGOR_STAT_PACKETS_TOTAL, 1);
    increment_stat(KONGOR_STAT_BYTES_PROCESSED, packet_len);

    /* 1. Parse Ethernet Header */
    struct ethhdr *eth = data;
    if ((void *)(eth + 1) > data_end) {
        increment_stat(KONGOR_STAT_PACKETS_DROPPED_MALFORMED, 1);
        return XDP_DROP;
    }

    if (bpf_ntohs(eth->h_proto) != ETH_P_IP) {
        // Non-IPv4 traffic (ARP, IPv6) passes through untouched
        increment_stat(KONGOR_STAT_PACKETS_PASSED, 1);
        return XDP_PASS;
    }

    /* 2. Parse IPv4 Header */
    struct iphdr *ip = (void *)(eth + 1);
    if ((void *)(ip + 1) > data_end) {
        increment_stat(KONGOR_STAT_PACKETS_DROPPED_MALFORMED, 1);
        return XDP_DROP;
    }

    // Only inspect UDP traffic
    if (ip->protocol != IPPROTO_UDP) {
        increment_stat(KONGOR_STAT_PACKETS_PASSED, 1);
        return XDP_PASS;
    }

    // Handle variable-length IP options
    __u32 ip_hdr_len = ip->ihl * 4;
    if (ip_hdr_len < sizeof(struct iphdr)) {
        increment_stat(KONGOR_STAT_PACKETS_DROPPED_MALFORMED, 1);
        return XDP_DROP;
    }

    /* 3. Parse UDP Header */
    struct udphdr *udp = (void *)((char *)ip + ip_hdr_len);
    if ((void *)(udp + 1) > data_end) {
        increment_stat(KONGOR_STAT_PACKETS_DROPPED_MALFORMED, 1);
        return XDP_DROP;
    }

    __u16 dest_port = bpf_ntohs(udp->dest);

    // Only scrub packets destined for Project Kongor dedicated server ports (11235-11238)
    if (dest_port < KONGOR_PORT_MIN || dest_port > KONGOR_PORT_MAX) {
        increment_stat(KONGOR_STAT_PACKETS_PASSED, 1);
        return XDP_PASS;
    }

    /* 4. Validate K2 UDP Payload Length (min 3 bytes: 2-byte seq/OOB + 1-byte opcode) */
    void *payload = (void *)(udp + 1);
    __u16 udp_len = bpf_ntohs(udp->len);
    if (udp_len < sizeof(struct udphdr) + KONGOR_MIN_K2_PAYLOAD_LEN) {
        increment_stat(KONGOR_STAT_PACKETS_DROPPED_MALFORMED, 1);
        return XDP_DROP;
    }

    if ((void *)((char *)payload + KONGOR_MIN_K2_PAYLOAD_LEN) > data_end) {
        increment_stat(KONGOR_STAT_PACKETS_DROPPED_MALFORMED, 1);
        return XDP_DROP;
    }

    /* 5. Drop all-zero 4-byte amplification/null probes without rejecting valid K2 frames */
    if (udp_len >= sizeof(struct udphdr) + sizeof(__u32)) {
        __u32 *hdr_prefix = payload;
        if ((void *)(hdr_prefix + 1) <= data_end && *hdr_prefix == KONGOR_NULL_PROBE_HEADER) {
            increment_stat(KONGOR_STAT_PACKETS_DROPPED_MAGIC, 1);
            return XDP_DROP;
        }
    }

    /* 6. Per-IP Token-Bucket Rate Limiter (Optional DDoS mitigation) */
    __u32 src_ip = ip->saddr;
    struct rate_limit_entry *entry = bpf_map_lookup_elem(&kongor_rate_limit_map, &src_ip);
    __u64 now_ns = bpf_ktime_get_ns();

    if (entry) {
        // Replenish tokens: 10,000 packets/sec capacity with 1,000 burst allowance
        __u64 elapsed_ns = now_ns - entry->last_time_ns;
        if (elapsed_ns > 1000000000ULL) { // 1 second
            entry->tokens = 1000;
            entry->last_time_ns = now_ns;
        }

        if (entry->tokens > 0) {
            entry->tokens--;
        } else {
            entry->drop_count++;
            increment_stat(KONGOR_STAT_PACKETS_DROPPED_RATELIMIT, 1);
            return XDP_DROP;
        }
    } else {
        struct rate_limit_entry new_entry = {
            .last_time_ns = now_ns,
            .tokens = 1000,
            .drop_count = 0
        };
        bpf_map_update_elem(&kongor_rate_limit_map, &src_ip, &new_entry, BPF_ANY);
    }

    // Packet validated successfully -> Pass to dedicated server socket buffer
    increment_stat(KONGOR_STAT_PACKETS_PASSED, 1);
    return XDP_PASS;
}
