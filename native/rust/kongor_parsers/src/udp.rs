// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! UDP untrusted boundary parsers:
//! - 6-byte K2 server-browser ping probe (`native/proxies/hon_ping_shim/hon_ping_protocol.h`)
//! - Loopback-only `"map:<name>"` notification parser (`hon_ping_protocol.h`)
//! - COMPEL 40-byte watermark envelope validator (`infra/ebpf/xdp_kongor_filter.c`)

use std::fmt;

/// Canonical host-byte-order IPv4 loopback address (`127.0.0.1`).
pub const HON_PING_LOOPBACK_IPV4_HOST: u32 = 0x7F00_0001;

/// Exact byte length of K2 client raw UDP server-browser ping probes.
pub const K2_PING_PROBE_LEN: usize = 6;

/// Exact 4-byte prefix of K2 client raw UDP ping probes (`[0x00, 0x00, 0x01, 0xCA]`).
pub const K2_PING_PROBE_PREFIX: [u8; 4] = [0x00, 0x00, 0x01, 0xCA];

/// COMPEL 40-byte watermark header length.
pub const KONGOR_WATERMARK_PREFIX_LEN: usize = 40;
/// COMPEL proxy game UDP minimum length: 40B watermark + 2B connection + 1B packet type.
pub const KONGOR_PROXY_GAME_MIN_LEN: usize = 43;
/// COMPEL proxy voice UDP minimum length: 40B watermark + 1B packet type.
pub const KONGOR_PROXY_VOICE_MIN_LEN: usize = 41;
/// Direct K2 server game UDP minimum length.
pub const KONGOR_DIRECT_GAME_MIN_LEN: usize = 3;
/// Direct K2 server voice UDP minimum length.
pub const KONGOR_DIRECT_VOICE_MIN_LEN: usize = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CompelWatermark<'a> {
    pub challenge: u32,
    pub counter: u16,
    pub is_voice: bool,
    pub payload_len: usize,
    pub inner_payload: &'a [u8],
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UdpError {
    NotAPingProbe,
    UnauthorizedSender(u32),
    NotAMapUpdate,
    InvalidMapName,
    TooShort { actual: usize, required: usize },
    MalformedSentinelAllOnes,
    MalformedSentinelAllZeros,
}

impl fmt::Display for UdpError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotAPingProbe => {
                write!(f, "packet is not an exact 6-byte K2 ping probe (00 00 01 CA)")
            }
            Self::UnauthorizedSender(ip) => {
                write!(f, "non-loopback sender 0x{ip:08X} rejected for map: control packet")
            }
            Self::NotAMapUpdate => write!(f, "packet does not start with 'map:' prefix"),
            Self::InvalidMapName => write!(f, "map name is empty or contains invalid/control bytes"),
            Self::TooShort { actual, required } => {
                write!(f, "UDP payload too short: have {actual} bytes, require {required}")
            }
            Self::MalformedSentinelAllOnes => {
                write!(f, "COMPEL watermark rejected: challenge=0xFFFFFFFF & counter=0xFFFF")
            }
            Self::MalformedSentinelAllZeros => {
                write!(f, "COMPEL watermark rejected: challenge=0 & counter=0")
            }
        }
    }
}

impl std::error::Error for UdpError {}

/// Validates an exact 6-byte K2 server-browser ping probe `[0x00, 0x00, 0x01, 0xCA, seq_lo, seq_hi]`
/// and returns its little-endian sequence number.
///
/// Gameplay UDP packets (`len > 6`) that happen to carry `0x01 0xCA` at offsets `2..4` are
/// guaranteed to be rejected (`Err(UdpError::NotAPingProbe)`) so they are never swallowed.
pub fn parse_k2_ping_probe(pkt: &[u8]) -> Result<u16, UdpError> {
    if pkt.len() != K2_PING_PROBE_LEN || pkt[0..4] != K2_PING_PROBE_PREFIX {
        return Err(UdpError::NotAPingProbe);
    }
    Ok(u16::from_le_bytes([pkt[4], pkt[5]]))
}

/// Returns `true` iff `host_order_ipv4` equals canonical loopback `127.0.0.1` (`0x7F000001`).
pub const fn is_loopback_ipv4_host(host_order_ipv4: u32) -> bool {
    host_order_ipv4 == HON_PING_LOOPBACK_IPV4_HOST
}

/// Validates and extracts a loopback-only `"map:<name>"` notification, stripping trailing
/// `\n`, `\r`, `' '`, and `'\t'` bytes (exact parity with `hon_ping_extract_loopback_map_update`).
pub fn parse_loopback_map_update(
    pkt: &[u8],
    sender_ipv4_host_order: u32,
) -> Result<&str, UdpError> {
    if !is_loopback_ipv4_host(sender_ipv4_host_order) {
        return Err(UdpError::UnauthorizedSender(sender_ipv4_host_order));
    }
    if pkt.len() <= 4 || &pkt[..4] != b"map:" {
        return Err(UdpError::NotAMapUpdate);
    }

    let mut raw = &pkt[4..];
    while let Some(&last) = raw.last() {
        if matches!(last, b'\n' | b'\r' | b' ' | b'\t') {
            raw = &raw[..raw.len() - 1];
        } else {
            break;
        }
    }

    if raw.is_empty() {
        return Err(UdpError::InvalidMapName);
    }
    if raw.iter().any(|&b| b < 0x20 || b == 0x7F) {
        return Err(UdpError::InvalidMapName);
    }

    std::str::from_utf8(raw).map_err(|_| UdpError::InvalidMapName)
}

/// Validates a COMPEL proxy UDP packet's 40-byte watermark envelope (`>= 43B` game, `>= 41B` voice),
/// reads `challenge` at offset 28 (u32 LE) and `counter` at offset 32 (u16 LE), and rejects both
/// all-ones and all-zeros flood sentinels (exact parity with `infra/ebpf/xdp_kongor_filter.c:212-239`).
pub fn parse_compel_watermark(pkt: &[u8], is_voice: bool) -> Result<CompelWatermark<'_>, UdpError> {
    let min_len = if is_voice {
        KONGOR_PROXY_VOICE_MIN_LEN
    } else {
        KONGOR_PROXY_GAME_MIN_LEN
    };
    if pkt.len() < min_len {
        return Err(UdpError::TooShort {
            actual: pkt.len(),
            required: min_len,
        });
    }

    let challenge = u32::from_le_bytes([pkt[28], pkt[29], pkt[30], pkt[31]]);
    let counter = u16::from_le_bytes([pkt[32], pkt[33]]);

    if challenge == 0xFFFF_FFFF && counter == 0xFFFF {
        return Err(UdpError::MalformedSentinelAllOnes);
    }
    if challenge == 0 && counter == 0 {
        return Err(UdpError::MalformedSentinelAllZeros);
    }

    Ok(CompelWatermark {
        challenge,
        counter,
        is_voice,
        payload_len: pkt.len(),
        inner_payload: &pkt[KONGOR_WATERMARK_PREFIX_LEN..],
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn k2_ping_probe_matches_strictly_6_bytes() {
        let probe = [0x00, 0x00, 0x01, 0xCA, 0x34, 0x12];
        assert_eq!(parse_k2_ping_probe(&probe), Ok(0x1234));

        // 5 bytes (truncated) or 7 bytes / 48 bytes (gameplay) must NOT match.
        assert_eq!(parse_k2_ping_probe(&probe[..5]), Err(UdpError::NotAPingProbe));
        let gameplay7 = [0x00, 0x00, 0x01, 0xCA, 0x10, 0x20, 0x30];
        assert_eq!(parse_k2_ping_probe(&gameplay7), Err(UdpError::NotAPingProbe));
        let mut gameplay48 = [0u8; 48];
        gameplay48[2] = 0x01;
        gameplay48[3] = 0xCA;
        assert_eq!(parse_k2_ping_probe(&gameplay48), Err(UdpError::NotAPingProbe));
    }

    #[test]
    fn loopback_map_update_strips_whitespace_and_blocks_external_spoofing() {
        assert_eq!(
            parse_loopback_map_update(b"map:caldavar\r\n", HON_PING_LOOPBACK_IPV4_HOST),
            Ok("caldavar")
        );
        assert_eq!(
            parse_loopback_map_update(b"map:midwars \t\n", HON_PING_LOOPBACK_IPV4_HOST),
            Ok("midwars")
        );
        assert_eq!(
            parse_loopback_map_update(b"map:pwned", 0xCB00_712A),
            Err(UdpError::UnauthorizedSender(0xCB00_712A))
        );
        assert_eq!(
            parse_loopback_map_update(b"map:   \r\n", HON_PING_LOOPBACK_IPV4_HOST),
            Err(UdpError::InvalidMapName)
        );
        assert_eq!(
            parse_loopback_map_update(b"map:bad\0name", HON_PING_LOOPBACK_IPV4_HOST),
            Err(UdpError::InvalidMapName)
        );
    }

    #[test]
    fn proxy_watermark_validates_lengths_and_sentinels() {
        let mut game_pkt = vec![0u8; 43];
        game_pkt[28..32].copy_from_slice(&0x1122_3344u32.to_le_bytes());
        game_pkt[32..34].copy_from_slice(&0x0055u16.to_le_bytes());
        game_pkt[40..43].copy_from_slice(&[0xAA, 0xBB, 0xCC]);

        let wm = parse_compel_watermark(&game_pkt, false).expect("valid 43B game watermark");
        assert_eq!(wm.challenge, 0x1122_3344);
        assert_eq!(wm.counter, 0x0055);
        assert_eq!(wm.inner_payload, &[0xAA, 0xBB, 0xCC]);

        // 42B game packet rejected, 41B voice packet accepted.
        assert_eq!(
            parse_compel_watermark(&game_pkt[..42], false),
            Err(UdpError::TooShort {
                actual: 42,
                required: 43,
            })
        );
        let voice_wm =
            parse_compel_watermark(&game_pkt[..41], true).expect("valid 41B voice watermark");
        assert_eq!(voice_wm.inner_payload, &[0xAA]);

        // 40B voice packet rejected.
        assert_eq!(
            parse_compel_watermark(&game_pkt[..40], true),
            Err(UdpError::TooShort {
                actual: 40,
                required: 41,
            })
        );

        // All-zeros sentinel rejected.
        let zeros = vec![0u8; 43];
        assert_eq!(
            parse_compel_watermark(&zeros, false),
            Err(UdpError::MalformedSentinelAllZeros)
        );

        // All-ones sentinel rejected.
        let mut ones = vec![0u8; 43];
        ones[28..32].copy_from_slice(&0xFFFF_FFFFu32.to_le_bytes());
        ones[32..34].copy_from_slice(&0xFFFFu16.to_le_bytes());
        assert_eq!(
            parse_compel_watermark(&ones, false),
            Err(UdpError::MalformedSentinelAllOnes)
        );
    }
}
