// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! Byte-for-byte parity port of `infra/ebpf/xdp_kongor_filter.c` into safe `#![no_std]` Rust.
//!
//! Covers:
//! - Ethernet header validation + IEEE 802.1Q / 802.1ad VLAN tag unwrapping (`<= 2` tags)
//! - IPv4 (with variable IHL 20..=60 bounds verification) and IPv6 + UDP header parsing
//! - Dynamic COMPEL active port map lookup + static HoN server port range fallback
//! - Direct K2 minimum payload enforcement (`>= 3B` game / `>= 2B` voice)
//! - COMPEL proxy 40-byte watermark envelope validation (`>= 43B` game / `>= 41B` voice,
//!   challenge @ offset 28, counter @ offset 32, all-zeros & all-ones flood drops)
//! - All 7 per-CPU telemetry counter indices (`STAT_PASS_GAME` .. `STAT_DROP_UNREGISTERED_PORT`)

pub const ETH_HLEN: usize = 14;
pub const VLAN_HLEN: usize = 4;
pub const IPV4_MIN_HLEN: usize = 20;
pub const IPV4_MAX_HLEN: usize = 60;
pub const IPV6_HLEN: usize = 40;
pub const UDP_HLEN: usize = 8;

pub const ETH_P_IP: u16 = 0x0800;
pub const ETH_P_IPV6: u16 = 0x86DD;
pub const ETH_P_8021Q: u16 = 0x8100;
pub const ETH_P_8021AD: u16 = 0x88A8;

pub const IPPROTO_UDP: u8 = 17;

pub const KONGOR_WATERMARK_PREFIX_LEN: usize = 40;
pub const KONGOR_PROXY_GAME_MIN_LEN: usize = 43;
pub const KONGOR_PROXY_VOICE_MIN_LEN: usize = 41;
pub const KONGOR_DIRECT_GAME_MIN_LEN: usize = 3;
pub const KONGOR_DIRECT_VOICE_MIN_LEN: usize = 2;

pub const PORT_FLAG_VOICE: u32 = 1 << 0;
pub const PORT_FLAG_PROXY: u32 = 1 << 1;

pub const STAT_MAX_ENTRIES: usize = 7;

/// Linux `enum xdp_action` numeric values.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum XdpAction {
    Aborted = 0,
    Drop = 1,
    Pass = 2,
    Tx = 3,
    Redirect = 4,
}

/// Telemetry counter indices matching `infra/ebpf/xdp_kongor_filter.c:37-44`.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StatCounter {
    PassGame = 0,
    PassVoice = 1,
    DropTooShort = 2,
    DropMalformed = 3,
    PassNonGame = 4,
    DropSpoofedCookie = 5,
    DropUnregisteredPort = 6,
}

impl StatCounter {
    #[inline]
    pub const fn index(self) -> usize {
        self as usize
    }
}

/// Pinned BPF map value layout matching `struct kongor_port_entry` in `xdp_kongor_filter.c:47-51`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct KongorPortEntry {
    pub pid: u32,
    /// bit 0: is_voice (1=voice, 0=game), bit 1: is_proxy (1=proxy, 0=direct)
    pub flags: u32,
    pub registered_at_ns: u64,
}

impl KongorPortEntry {
    #[inline]
    pub const fn is_voice(self) -> bool {
        (self.flags & PORT_FLAG_VOICE) != 0
    }

    #[inline]
    pub const fn is_proxy(self) -> bool {
        (self.flags & PORT_FLAG_PROXY) != 0
    }
}

/// Result of classifying one L2 Ethernet frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FilterDecision {
    pub action: XdpAction,
    pub stat: Option<StatCounter>,
}

impl FilterDecision {
    #[inline]
    const fn pass_no_stat() -> Self {
        Self {
            action: XdpAction::Pass,
            stat: None,
        }
    }

    #[inline]
    const fn drop_no_stat() -> Self {
        Self {
            action: XdpAction::Drop,
            stat: None,
        }
    }

    #[inline]
    const fn with_stat(action: XdpAction, stat: StatCounter) -> Self {
        Self {
            action,
            stat: Some(stat),
        }
    }
}

/// Host-side / test accumulator mirroring the 7-entry `kongor_stats` BPF per-CPU array.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct KongorXdpStats {
    pub counters: [u64; STAT_MAX_ENTRIES],
}

impl KongorXdpStats {
    #[inline]
    pub const fn new() -> Self {
        Self {
            counters: [0; STAT_MAX_ENTRIES],
        }
    }

    #[inline]
    pub fn record(&mut self, stat: StatCounter) {
        self.counters[stat.index()] = self.counters[stat.index()].saturating_add(1);
    }

    #[inline]
    pub const fn get(&self, stat: StatCounter) -> u64 {
        self.counters[stat.index()]
    }
}

#[inline]
pub const fn is_kongor_udp_port(port: u16) -> bool {
    (port >= 11235 && port <= 11534)
        || (port >= 11735 && port <= 12034)
        || (port >= 21235 && port <= 21534)
        || (port >= 21735 && port <= 22034)
}

#[inline]
pub const fn is_voice_port(port: u16) -> bool {
    (port >= 11735 && port <= 12034) || (port >= 21735 && port <= 22034)
}

#[inline]
pub const fn is_proxy_port(port: u16) -> bool {
    (port >= 21235 && port <= 21534) || (port >= 21735 && port <= 22034)
}

/// Evaluates an L2 Ethernet frame with exact control-flow parity to
/// `xdp_kongor_ingress_filter` in `infra/ebpf/xdp_kongor_filter.c`.
#[inline]
pub fn filter_packet<F>(packet: &[u8], lookup_port: F) -> FilterDecision
where
    F: FnOnce(u16) -> Option<KongorPortEntry>,
{
    // 1. Validate Ethernet Header + Unwrap up to 2 IEEE 802.1Q / 802.1ad VLAN tags
    if packet.len() < ETH_HLEN {
        return FilterDecision::pass_no_stat();
    }

    let mut h_proto = u16::from_be_bytes([packet[12], packet[13]]);
    let mut offset = ETH_HLEN;

    let mut vlan_idx = 0usize;
    while vlan_idx < 2 {
        if h_proto == ETH_P_8021Q || h_proto == ETH_P_8021AD {
            if offset + VLAN_HLEN > packet.len() {
                return FilterDecision::drop_no_stat();
            }
            h_proto = u16::from_be_bytes([packet[offset + 2], packet[offset + 3]]);
            offset += VLAN_HLEN;
        }
        vlan_idx += 1;
    }

    // 2. Validate IPv4 or IPv6 Header & Locate UDP Header
    let udp_offset = if h_proto == ETH_P_IP {
        if offset + IPV4_MIN_HLEN > packet.len() {
            return FilterDecision::pass_no_stat();
        }
        let protocol = packet[offset + 9];
        if protocol != IPPROTO_UDP {
            return FilterDecision::pass_no_stat();
        }
        let ihl = (packet[offset] & 0x0F) as usize;
        let ip_hdr_len = ihl * 4;
        if !(IPV4_MIN_HLEN..=IPV4_MAX_HLEN).contains(&ip_hdr_len) {
            return FilterDecision::with_stat(XdpAction::Drop, StatCounter::DropMalformed);
        }
        if offset + ip_hdr_len > packet.len() {
            return FilterDecision::with_stat(XdpAction::Drop, StatCounter::DropMalformed);
        }
        offset + ip_hdr_len
    } else if h_proto == ETH_P_IPV6 {
        if offset + IPV6_HLEN > packet.len() {
            return FilterDecision::pass_no_stat();
        }
        let nexthdr = packet[offset + 6];
        if nexthdr != IPPROTO_UDP {
            return FilterDecision::pass_no_stat();
        }
        offset + IPV6_HLEN
    } else {
        return FilterDecision::pass_no_stat();
    };

    // 3. Validate UDP Header
    if udp_offset + UDP_HLEN > packet.len() {
        return FilterDecision::pass_no_stat();
    }

    let dst_port = u16::from_be_bytes([packet[udp_offset + 2], packet[udp_offset + 3]]);

    let (is_active, voice, proxy_mode) = if let Some(entry) = lookup_port(dst_port) {
        (true, entry.is_voice(), entry.is_proxy())
    } else if is_kongor_udp_port(dst_port) {
        (true, is_voice_port(dst_port), is_proxy_port(dst_port))
    } else {
        (false, false, false)
    };

    if !is_active {
        return FilterDecision::with_stat(XdpAction::Pass, StatCounter::PassNonGame);
    }

    // 4. Inspect Game Server UDP Payload
    let payload_offset = udp_offset + UDP_HLEN;
    let payload = &packet[payload_offset..];
    let payload_len = payload.len();

    if !proxy_mode {
        let direct_min = if voice {
            KONGOR_DIRECT_VOICE_MIN_LEN
        } else {
            KONGOR_DIRECT_GAME_MIN_LEN
        };
        if payload_len < direct_min {
            return FilterDecision::with_stat(XdpAction::Drop, StatCounter::DropTooShort);
        }
        return FilterDecision::with_stat(
            XdpAction::Pass,
            if voice {
                StatCounter::PassVoice
            } else {
                StatCounter::PassGame
            },
        );
    }

    // 5. COMPEL Proxy Ports (21235-21534 / 21735-22034): Enforce 40-byte Watermark Envelope
    let min_len = if voice {
        KONGOR_PROXY_VOICE_MIN_LEN
    } else {
        KONGOR_PROXY_GAME_MIN_LEN
    };
    if payload_len < min_len || payload_len < KONGOR_WATERMARK_PREFIX_LEN {
        return FilterDecision::with_stat(XdpAction::Drop, StatCounter::DropTooShort);
    }

    let challenge = u32::from_le_bytes([payload[28], payload[29], payload[30], payload[31]]);
    let counter = u16::from_le_bytes([payload[32], payload[33]]);

    if challenge == 0xFFFF_FFFF && counter == 0xFFFF {
        return FilterDecision::with_stat(XdpAction::Drop, StatCounter::DropMalformed);
    }
    if challenge == 0 && counter == 0 {
        return FilterDecision::with_stat(XdpAction::Drop, StatCounter::DropMalformed);
    }

    FilterDecision::with_stat(
        XdpAction::Pass,
        if voice {
            StatCounter::PassVoice
        } else {
            StatCounter::PassGame
        },
    )
}

/// Evaluates `packet` and updates `stats` when the decision maps to a telemetry counter.
#[inline]
pub fn filter_packet_with_stats<F>(
    packet: &[u8],
    lookup_port: F,
    stats: &mut KongorXdpStats,
) -> XdpAction
where
    F: FnOnce(u16) -> Option<KongorPortEntry>,
{
    let decision = filter_packet(packet, lookup_port);
    if let Some(counter) = decision.stat {
        stats.record(counter);
    }
    decision.action
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::vec;
    use std::vec::Vec;

    fn build_ipv4_udp_frame(
        vlans: &[(u16, u16)],
        ihl_words: u8,
        proto: u8,
        dst_port: u16,
        payload: &[u8],
    ) -> Vec<u8> {
        let mut frame = Vec::new();
        frame.extend_from_slice(&[0x11, 0x22, 0x33, 0x44, 0x55, 0x66]); // dst mac
        frame.extend_from_slice(&[0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]); // src mac

        if vlans.is_empty() {
            frame.extend_from_slice(&ETH_P_IP.to_be_bytes());
        } else {
            frame.extend_from_slice(&vlans[0].0.to_be_bytes());
            for (i, &(_, tci)) in vlans.iter().enumerate() {
                frame.extend_from_slice(&tci.to_be_bytes());
                let next_proto = if i + 1 < vlans.len() {
                    vlans[i + 1].0
                } else {
                    ETH_P_IP
                };
                frame.extend_from_slice(&next_proto.to_be_bytes());
            }
        }

        let ip_len = ((ihl_words as usize) * 4).max(20);
        let mut ip = vec![0u8; ip_len];
        ip[0] = (4u8 << 4) | (ihl_words & 0x0F);
        ip[9] = proto;
        frame.extend_from_slice(&ip);

        // UDP header (8 bytes)
        frame.extend_from_slice(&50000u16.to_be_bytes());
        frame.extend_from_slice(&dst_port.to_be_bytes());
        frame.extend_from_slice(&((8 + payload.len()) as u16).to_be_bytes());
        frame.extend_from_slice(&0u16.to_be_bytes());
        frame.extend_from_slice(payload);
        frame
    }

    fn build_ipv6_udp_frame(vlans: &[(u16, u16)], nexthdr: u8, dst_port: u16, payload: &[u8]) -> Vec<u8> {
        let mut frame = Vec::new();
        frame.extend_from_slice(&[0u8; 12]);
        if vlans.is_empty() {
            frame.extend_from_slice(&ETH_P_IPV6.to_be_bytes());
        } else {
            frame.extend_from_slice(&vlans[0].0.to_be_bytes());
            for (i, &(_, tci)) in vlans.iter().enumerate() {
                frame.extend_from_slice(&tci.to_be_bytes());
                let next_proto = if i + 1 < vlans.len() {
                    vlans[i + 1].0
                } else {
                    ETH_P_IPV6
                };
                frame.extend_from_slice(&next_proto.to_be_bytes());
            }
        }

        let mut ip6 = vec![0u8; IPV6_HLEN];
        ip6[0] = 0x60;
        ip6[6] = nexthdr;
        frame.extend_from_slice(&ip6);

        frame.extend_from_slice(&50000u16.to_be_bytes());
        frame.extend_from_slice(&dst_port.to_be_bytes());
        frame.extend_from_slice(&((8 + payload.len()) as u16).to_be_bytes());
        frame.extend_from_slice(&0u16.to_be_bytes());
        frame.extend_from_slice(payload);
        frame
    }

    fn valid_watermark_payload(len: usize, challenge: u32, counter: u16) -> Vec<u8> {
        let mut p = vec![0x5Au8; len];
        p[28..32].copy_from_slice(&challenge.to_le_bytes());
        p[32..34].copy_from_slice(&counter.to_le_bytes());
        p
    }

    #[test]
    fn direct_ipv4_and_ipv6_game_and_voice_filtering() {
        let mut stats = KongorXdpStats::new();

        // Direct game port 11235 with 3-byte K2 handshake passes.
        let ok_game = build_ipv4_udp_frame(&[], 5, IPPROTO_UDP, 11235, &[0x01, 0x02, 0x03]);
        assert_eq!(filter_packet_with_stats(&ok_game, |_| None, &mut stats), XdpAction::Pass);
        assert_eq!(stats.get(StatCounter::PassGame), 1);

        // Direct game port 11235 with 2-byte junk drops (too short).
        let short_game = build_ipv4_udp_frame(&[], 5, IPPROTO_UDP, 11235, &[0x01, 0x02]);
        assert_eq!(filter_packet_with_stats(&short_game, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropTooShort), 1);

        // Direct voice port 11735 over IPv6 with 2-byte keepalive passes.
        let ok_voice = build_ipv6_udp_frame(&[], IPPROTO_UDP, 11735, &[0xAA, 0xBB]);
        assert_eq!(filter_packet_with_stats(&ok_voice, |_| None, &mut stats), XdpAction::Pass);
        assert_eq!(stats.get(StatCounter::PassVoice), 1);

        // Direct voice port 11735 with 1-byte scanner junk drops.
        let short_voice = build_ipv6_udp_frame(&[], IPPROTO_UDP, 11735, &[0xAA]);
        assert_eq!(filter_packet_with_stats(&short_voice, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropTooShort), 2);
    }

    #[test]
    fn vlan_double_tagging_and_truncated_vlan_handling() {
        let mut stats = KongorXdpStats::new();

        // QinQ (802.1ad + 802.1Q) -> COMPEL proxy game port 21235 with valid 43B watermark.
        let payload = valid_watermark_payload(43, 0xCAFE_BABE, 7);
        let qinq = build_ipv4_udp_frame(
            &[(ETH_P_8021AD, 100), (ETH_P_8021Q, 200)],
            5,
            IPPROTO_UDP,
            21235,
            &payload,
        );
        assert_eq!(filter_packet_with_stats(&qinq, |_| None, &mut stats), XdpAction::Pass);
        assert_eq!(stats.get(StatCounter::PassGame), 1);

        // Truncated VLAN header must drop immediately with no stat increment (parity line 128).
        let mut trunc_vlan = vec![0u8; 16];
        trunc_vlan[12..14].copy_from_slice(&ETH_P_8021Q.to_be_bytes());
        let dec = filter_packet(&trunc_vlan, |_| None);
        assert_eq!(dec.action, XdpAction::Drop);
        assert_eq!(dec.stat, None);

        // 3 stacked VLAN tags exceed <=2 unwrap depth and pass untouched (non-IP EtherType).
        let triple = build_ipv4_udp_frame(
            &[(ETH_P_8021AD, 1), (ETH_P_8021Q, 2), (ETH_P_8021Q, 3)],
            5,
            IPPROTO_UDP,
            21235,
            &[0x00],
        );
        let dec3 = filter_packet(&triple, |_| None);
        assert_eq!(dec3.action, XdpAction::Pass);
        assert_eq!(dec3.stat, None);
    }

    #[test]
    fn udp_proxy_watermark_sentinels_and_dynamic_port_map() {
        let mut stats = KongorXdpStats::new();

        // Dynamic port map overrides non-standard port 30000 as proxy voice.
        let dyn_entry = KongorPortEntry {
            pid: 4242,
            flags: PORT_FLAG_VOICE | PORT_FLAG_PROXY,
            registered_at_ns: 123_456_789,
        };
        let lookup = |port: u16| if port == 30000 { Some(dyn_entry) } else { None };

        let ok_voice = build_ipv4_udp_frame(
            &[(ETH_P_8021Q, 42)],
            6, // 24-byte IPv4 header with options
            IPPROTO_UDP,
            30000,
            &valid_watermark_payload(41, 0x0000_0001, 0),
        );
        assert_eq!(filter_packet_with_stats(&ok_voice, lookup, &mut stats), XdpAction::Pass);
        assert_eq!(stats.get(StatCounter::PassVoice), 1);

        // All-zero watermark drops as malformed.
        let zero_wm = build_ipv4_udp_frame(&[], 5, IPPROTO_UDP, 21235, &vec![0u8; 43]);
        assert_eq!(filter_packet_with_stats(&zero_wm, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropMalformed), 1);

        // All-ones watermark drops as malformed.
        let ones_wm = build_ipv4_udp_frame(
            &[],
            5,
            IPPROTO_UDP,
            21235,
            &valid_watermark_payload(43, 0xFFFF_FFFF, 0xFFFF),
        );
        assert_eq!(filter_packet_with_stats(&ones_wm, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropMalformed), 2);

        // Short 42B proxy game packet drops as too short.
        let short_proxy = build_ipv4_udp_frame(
            &[],
            5,
            IPPROTO_UDP,
            21235,
            &valid_watermark_payload(42, 0x1234, 1),
        );
        assert_eq!(filter_packet_with_stats(&short_proxy, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropTooShort), 1);

        // Non-game UDP (e.g. DNS port 53) passes and increments PassNonGame.
        let dns = build_ipv4_udp_frame(&[], 5, IPPROTO_UDP, 53, &[0x12, 0x34]);
        assert_eq!(filter_packet_with_stats(&dns, |_| None, &mut stats), XdpAction::Pass);
        assert_eq!(stats.get(StatCounter::PassNonGame), 1);

        // Invalid IPv4 IHL (< 5 -> < 20 bytes) drops as malformed.
        let mut bad_ihl = build_ipv4_udp_frame(&[], 5, IPPROTO_UDP, 11235, &[1, 2, 3]);
        bad_ihl[ETH_HLEN] = 0x43; // version=4, ihl=3 (12 bytes)
        assert_eq!(filter_packet_with_stats(&bad_ihl, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropMalformed), 3);

        // Non-UDP IPv4 (TCP proto=6) passes untouched with no stat increment.
        let tcp = build_ipv4_udp_frame(&[], 5, 6, 11235, &[1, 2, 3]);
        let tcp_dec = filter_packet(&tcp, |_| None);
        assert_eq!(tcp_dec.action, XdpAction::Pass);
        assert_eq!(tcp_dec.stat, None);

        // Verify exact 7-entry counter ABI layout.
        assert_eq!(STAT_MAX_ENTRIES, 7);
        assert_eq!(StatCounter::DropSpoofedCookie.index(), 5);
        assert_eq!(StatCounter::DropUnregisteredPort.index(), 6);
    }

    #[test]
    fn truncated_headers_and_ipv4_ihl_exceeding_packet_len() {
        let mut stats = KongorXdpStats::new();

        // Truncated Ethernet (< 14B) passes without incrementing counters.
        assert_eq!(filter_packet(&[0u8; 10], |_| None), FilterDecision::pass_no_stat());

        // Truncated IPv4 (< 20B) passes without incrementing counters.
        let mut short_ip = vec![0u8; ETH_HLEN + 12];
        short_ip[12..14].copy_from_slice(&ETH_P_IP.to_be_bytes());
        assert_eq!(filter_packet(&short_ip, |_| None), FilterDecision::pass_no_stat());

        // IPv4 IHL=15 (60B) when only 28B remain after Ethernet -> Drop + DropMalformed.
        let mut oob_ihl = vec![0u8; ETH_HLEN + 28];
        oob_ihl[12..14].copy_from_slice(&ETH_P_IP.to_be_bytes());
        oob_ihl[ETH_HLEN] = 0x4F; // version=4, ihl=15 (60 bytes)
        oob_ihl[ETH_HLEN + 9] = IPPROTO_UDP;
        assert_eq!(filter_packet_with_stats(&oob_ihl, |_| None, &mut stats), XdpAction::Drop);
        assert_eq!(stats.get(StatCounter::DropMalformed), 1);

        // Truncated UDP header (< 8B after IPv6) passes without incrementing counters.
        let mut short_udp6 = vec![0u8; ETH_HLEN + IPV6_HLEN + 4];
        short_udp6[12..14].copy_from_slice(&ETH_P_IPV6.to_be_bytes());
        short_udp6[ETH_HLEN + 6] = IPPROTO_UDP;
        assert_eq!(filter_packet(&short_udp6, |_| None), FilterDecision::pass_no_stat());

        // Dynamic port map overrides static game port 11235 to proxy mode (requiring 43B).
        let override_proxy = KongorPortEntry {
            pid: 99,
            flags: PORT_FLAG_PROXY,
            registered_at_ns: 1,
        };
        let short_direct = build_ipv4_udp_frame(&[], 5, IPPROTO_UDP, 11235, &[1, 2, 3]);
        assert_eq!(
            filter_packet_with_stats(&short_direct, |_| Some(override_proxy), &mut stats),
            XdpAction::Drop
        );
        assert_eq!(stats.get(StatCounter::DropTooShort), 1);
    }
}
