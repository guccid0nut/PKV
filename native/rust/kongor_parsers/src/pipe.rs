// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! Named-pipe `'MW'` 12-byte binary frame parser and safe RFC 8259 JSON-RPC 2.0 token extractor.
//!
//! Parity reference: `native/proxies/native_guard/pipe_server.cpp` lines 70-105 & 998-1024.

use std::fmt;

/// Magic 2-byte prefix for Project Kongor binary named-pipe frames (`"MW"` = `0x4D 0x57`).
pub const MW_MAGIC_BYTES: [u8; 2] = [0x4D, 0x57];

/// Fixed header length in bytes: 2B magic + 2B opcode + 4B signed payloadLen + 4B reqId.
pub const MW_HEADER_LEN: usize = 12;

/// Maximum permitted binary pipe payload length (16 MiB), matching `pipe_server.cpp:88`.
pub const MW_MAX_PAYLOAD_LEN: usize = 16 * 1024 * 1024;

/// Maximum permitted unframed text / JSON-RPC line buffer (64 KiB), matching `pipe_server.cpp:70`.
pub const MAX_TEXT_STREAM_BUFFER_BYTES: usize = 65_536;

/// Maximum JSON container nesting depth (`{}` / `[]`) to defend against recursion/DoS bombs.
pub const MAX_JSON_DEPTH: usize = 64;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MwFrameHeader {
    pub opcode: u16,
    pub payload_len: u32,
    pub req_id: u32,
    pub total_frame_len: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MwFrame<'a> {
    pub header: MwFrameHeader,
    pub payload: &'a [u8],
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PipeError {
    NeedMoreData { needed: usize },
    InvalidMagic,
    NegativePayloadLength(i32),
    PayloadTooLarge { payload_len: u32, max: u32 },
    TextLineTooLarge { actual: usize, max: usize },
    InvalidUtf8,
    JsonNotAnObject,
    JsonUnterminatedString,
    JsonInvalidEscape,
    JsonNestingTooDeep { max: usize },
    JsonMalformed,
}

impl fmt::Display for PipeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NeedMoreData { needed } => {
                write!(f, "incomplete frame: need at least {needed} bytes")
            }
            Self::InvalidMagic => write!(f, "invalid MW pipe frame magic (expected 0x4D 0x57)"),
            Self::NegativePayloadLength(n) => write!(f, "negative MW payload length: {n}"),
            Self::PayloadTooLarge { payload_len, max } => {
                write!(f, "MW payload length {payload_len} exceeds limit {max}")
            }
            Self::TextLineTooLarge { actual, max } => {
                write!(f, "text stream length {actual} exceeds {max} byte cap")
            }
            Self::InvalidUtf8 => write!(f, "JSON-RPC payload is not valid UTF-8"),
            Self::JsonNotAnObject => write!(f, "JSON-RPC payload root is not a JSON object"),
            Self::JsonUnterminatedString => write!(f, "unterminated JSON string literal"),
            Self::JsonInvalidEscape => write!(f, "invalid JSON string escape sequence"),
            Self::JsonNestingTooDeep { max } => {
                write!(f, "JSON nesting exceeds maximum depth of {max}")
            }
            Self::JsonMalformed => write!(f, "malformed JSON syntax"),
        }
    }
}

impl std::error::Error for PipeError {}

/// Parses and validates the 12-byte `'MW'` binary pipe header without requiring the full payload.
pub fn parse_mw_frame_header(buf: &[u8]) -> Result<MwFrameHeader, PipeError> {
    if buf.is_empty() {
        return Err(PipeError::NeedMoreData {
            needed: MW_HEADER_LEN,
        });
    }
    if buf[0] != MW_MAGIC_BYTES[0] {
        return Err(PipeError::InvalidMagic);
    }
    if buf.len() < 2 {
        return Err(PipeError::NeedMoreData {
            needed: MW_HEADER_LEN,
        });
    }
    if buf[1] != MW_MAGIC_BYTES[1] {
        return Err(PipeError::InvalidMagic);
    }
    if buf.len() < MW_HEADER_LEN {
        return Err(PipeError::NeedMoreData {
            needed: MW_HEADER_LEN,
        });
    }

    let opcode = u16::from_le_bytes([buf[2], buf[3]]);
    let payload_len_i32 = i32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]);
    let req_id = u32::from_le_bytes([buf[8], buf[9], buf[10], buf[11]]);

    if payload_len_i32 < 0 {
        return Err(PipeError::NegativePayloadLength(payload_len_i32));
    }

    let payload_len = payload_len_i32 as u32;
    if (payload_len as usize) > MW_MAX_PAYLOAD_LEN {
        return Err(PipeError::PayloadTooLarge {
            payload_len,
            max: MW_MAX_PAYLOAD_LEN as u32,
        });
    }

    let total_frame_len = MW_HEADER_LEN
        .checked_add(payload_len as usize)
        .ok_or(PipeError::PayloadTooLarge {
            payload_len,
            max: MW_MAX_PAYLOAD_LEN as u32,
        })?;

    Ok(MwFrameHeader {
        opcode,
        payload_len,
        req_id,
        total_frame_len,
    })
}

/// Parses a complete `'MW'` binary pipe frame (`12 + payload_len` bytes) from `buf`.
pub fn parse_mw_frame(buf: &[u8]) -> Result<MwFrame<'_>, PipeError> {
    let header = parse_mw_frame_header(buf)?;
    if buf.len() < header.total_frame_len {
        return Err(PipeError::NeedMoreData {
            needed: header.total_frame_len,
        });
    }
    Ok(MwFrame {
        header,
        payload: &buf[MW_HEADER_LEN..header.total_frame_len],
    })
}

/// Validates whether `input` is a syntactically valid JSON-RPC 2.0 object (`"jsonrpc": "2.0"`).
pub fn is_json_rpc_2_0(input: &[u8]) -> Result<bool, PipeError> {
    match extract_json_token(input, "jsonrpc", false, false)? {
        Some(ver) => Ok(ver == "2.0"),
        None => Ok(false),
    }
}

/// Extracts a field value from a JSON-RPC 2.0 object safely.
///
/// - Enforces UTF-8, `<= 64 KiB` text cap, RFC 8259 string escaping (including `\uXXXX` surrogate
///   pairs), and bounded container depth (`<= 64`).
/// - Distinguishes object keys from string values so spoofed keys embedded inside string values
///   never shadow authentic fields.
/// - Searches direct top-level object members first; when `search_params` is true and the key is
///   absent at top level, searches direct members of a top-level `"params"` object.
/// - For string values: `preserve_quotes == true` returns the exact JSON quoted literal (e.g.
///   `"\"abc\""`, matching `pipe_server.cpp`'s `"id"` passthrough); `preserve_quotes == false`
///   returns the unescaped string content.
pub fn extract_json_token(
    input: &[u8],
    key: &str,
    preserve_quotes: bool,
    search_params: bool,
) -> Result<Option<String>, PipeError> {
    if input.len() > MAX_TEXT_STREAM_BUFFER_BYTES {
        return Err(PipeError::TextLineTooLarge {
            actual: input.len(),
            max: MAX_TEXT_STREAM_BUFFER_BYTES,
        });
    }

    let text = std::str::from_utf8(input).map_err(|_| PipeError::InvalidUtf8)?;
    let mut scanner = JsonScanner::new(text);
    scanner.skip_ws();
    if scanner.peek() != Some(b'{') {
        return Err(PipeError::JsonNotAnObject);
    }

    let mut top_match: Option<String> = None;
    let mut params_match: Option<String> = None;

    scanner.parse_root_object(
        key,
        preserve_quotes,
        search_params,
        &mut top_match,
        &mut params_match,
    )?;

    scanner.skip_ws();
    if !scanner.is_eof() {
        return Err(PipeError::JsonMalformed);
    }

    Ok(top_match.or(params_match))
}

struct JsonScanner<'a> {
    src: &'a str,
    bytes: &'a [u8],
    pos: usize,
}

impl<'a> JsonScanner<'a> {
    fn new(src: &'a str) -> Self {
        Self {
            src,
            bytes: src.as_bytes(),
            pos: 0,
        }
    }

    fn is_eof(&self) -> bool {
        self.pos >= self.bytes.len()
    }

    fn peek(&self) -> Option<u8> {
        self.bytes.get(self.pos).copied()
    }

    fn next_byte(&mut self) -> Option<u8> {
        let b = self.bytes.get(self.pos).copied()?;
        self.pos += 1;
        Some(b)
    }

    fn skip_ws(&mut self) {
        while let Some(b) = self.peek() {
            if matches!(b, b' ' | b'\t' | b'\r' | b'\n') {
                self.pos += 1;
            } else {
                break;
            }
        }
    }

    fn parse_root_object(
        &mut self,
        target_key: &str,
        preserve_quotes: bool,
        search_params: bool,
        top_match: &mut Option<String>,
        params_match: &mut Option<String>,
    ) -> Result<(), PipeError> {
        if self.next_byte() != Some(b'{') {
            return Err(PipeError::JsonNotAnObject);
        }
        self.skip_ws();
        if self.peek() == Some(b'}') {
            self.pos += 1;
            return Ok(());
        }

        loop {
            self.skip_ws();
            let member_key = self.parse_string_unescaped()?;
            self.skip_ws();
            if self.next_byte() != Some(b':') {
                return Err(PipeError::JsonMalformed);
            }
            self.skip_ws();

            let is_direct_hit = top_match.is_none() && member_key == target_key;
            let is_params_obj = search_params
                && member_key == "params"
                && self.peek() == Some(b'{')
                && params_match.is_none();

            if is_direct_hit {
                let val = self.parse_value_as_token(1, preserve_quotes)?;
                *top_match = Some(val);
            } else if is_params_obj {
                self.parse_params_object(target_key, preserve_quotes, params_match)?;
            } else {
                self.skip_value(1)?;
            }

            self.skip_ws();
            match self.next_byte() {
                Some(b',') => continue,
                Some(b'}') => return Ok(()),
                _ => return Err(PipeError::JsonMalformed),
            }
        }
    }

    fn parse_params_object(
        &mut self,
        target_key: &str,
        preserve_quotes: bool,
        params_match: &mut Option<String>,
    ) -> Result<(), PipeError> {
        // Depth is 2 here (root object = 1, params object = 2).
        if 2 > MAX_JSON_DEPTH {
            return Err(PipeError::JsonNestingTooDeep {
                max: MAX_JSON_DEPTH,
            });
        }
        if self.next_byte() != Some(b'{') {
            return Err(PipeError::JsonMalformed);
        }
        self.skip_ws();
        if self.peek() == Some(b'}') {
            self.pos += 1;
            return Ok(());
        }

        loop {
            self.skip_ws();
            let member_key = self.parse_string_unescaped()?;
            self.skip_ws();
            if self.next_byte() != Some(b':') {
                return Err(PipeError::JsonMalformed);
            }
            self.skip_ws();

            if params_match.is_none() && member_key == target_key {
                let val = self.parse_value_as_token(2, preserve_quotes)?;
                *params_match = Some(val);
            } else {
                self.skip_value(2)?;
            }

            self.skip_ws();
            match self.next_byte() {
                Some(b',') => continue,
                Some(b'}') => return Ok(()),
                _ => return Err(PipeError::JsonMalformed),
            }
        }
    }

    fn parse_value_as_token(
        &mut self,
        depth: usize,
        preserve_quotes: bool,
    ) -> Result<String, PipeError> {
        let start = self.pos;
        match self.peek() {
            Some(b'"') => {
                let unescaped = self.parse_string_unescaped()?;
                if preserve_quotes {
                    Ok(self.src[start..self.pos].to_string())
                } else {
                    Ok(unescaped)
                }
            }
            Some(_) => {
                self.skip_value(depth)?;
                Ok(self.src[start..self.pos].to_string())
            }
            None => Err(PipeError::JsonMalformed),
        }
    }

    fn skip_value(&mut self, depth: usize) -> Result<(), PipeError> {
        match self.peek() {
            Some(b'"') => {
                let _ = self.parse_string_unescaped()?;
                Ok(())
            }
            Some(b'{') => self.skip_object(depth + 1),
            Some(b'[') => self.skip_array(depth + 1),
            Some(b't') => self.expect_literal(b"true"),
            Some(b'f') => self.expect_literal(b"false"),
            Some(b'n') => self.expect_literal(b"null"),
            Some(b'-' | b'0'..=b'9') => self.skip_number(),
            _ => Err(PipeError::JsonMalformed),
        }
    }

    fn skip_object(&mut self, depth: usize) -> Result<(), PipeError> {
        if depth > MAX_JSON_DEPTH {
            return Err(PipeError::JsonNestingTooDeep {
                max: MAX_JSON_DEPTH,
            });
        }
        self.pos += 1; // '{'
        self.skip_ws();
        if self.peek() == Some(b'}') {
            self.pos += 1;
            return Ok(());
        }
        loop {
            self.skip_ws();
            let _ = self.parse_string_unescaped()?;
            self.skip_ws();
            if self.next_byte() != Some(b':') {
                return Err(PipeError::JsonMalformed);
            }
            self.skip_ws();
            self.skip_value(depth)?;
            self.skip_ws();
            match self.next_byte() {
                Some(b',') => continue,
                Some(b'}') => return Ok(()),
                _ => return Err(PipeError::JsonMalformed),
            }
        }
    }

    fn skip_array(&mut self, depth: usize) -> Result<(), PipeError> {
        if depth > MAX_JSON_DEPTH {
            return Err(PipeError::JsonNestingTooDeep {
                max: MAX_JSON_DEPTH,
            });
        }
        self.pos += 1; // '['
        self.skip_ws();
        if self.peek() == Some(b']') {
            self.pos += 1;
            return Ok(());
        }
        loop {
            self.skip_ws();
            self.skip_value(depth)?;
            self.skip_ws();
            match self.next_byte() {
                Some(b',') => continue,
                Some(b']') => return Ok(()),
                _ => return Err(PipeError::JsonMalformed),
            }
        }
    }

    fn expect_literal(&mut self, lit: &[u8]) -> Result<(), PipeError> {
        if self.bytes[self.pos..].starts_with(lit) {
            self.pos += lit.len();
            Ok(())
        } else {
            Err(PipeError::JsonMalformed)
        }
    }

    fn skip_number(&mut self) -> Result<(), PipeError> {
        if self.peek() == Some(b'-') {
            self.pos += 1;
        }
        match self.next_byte() {
            Some(b'0') => {
                // Leading zero must not be followed directly by another digit.
                if matches!(self.peek(), Some(b'0'..=b'9')) {
                    return Err(PipeError::JsonMalformed);
                }
            }
            Some(b'1'..=b'9') => {
                while matches!(self.peek(), Some(b'0'..=b'9')) {
                    self.pos += 1;
                }
            }
            _ => return Err(PipeError::JsonMalformed),
        }

        if self.peek() == Some(b'.') {
            self.pos += 1;
            if !matches!(self.peek(), Some(b'0'..=b'9')) {
                return Err(PipeError::JsonMalformed);
            }
            while matches!(self.peek(), Some(b'0'..=b'9')) {
                self.pos += 1;
            }
        }

        if matches!(self.peek(), Some(b'e' | b'E')) {
            self.pos += 1;
            if matches!(self.peek(), Some(b'+' | b'-')) {
                self.pos += 1;
            }
            if !matches!(self.peek(), Some(b'0'..=b'9')) {
                return Err(PipeError::JsonMalformed);
            }
            while matches!(self.peek(), Some(b'0'..=b'9')) {
                self.pos += 1;
            }
        }
        Ok(())
    }

    fn parse_hex4(&mut self) -> Result<u16, PipeError> {
        let mut val: u16 = 0;
        for _ in 0..4 {
            let b = self.next_byte().ok_or(PipeError::JsonUnterminatedString)?;
            let digit = match b {
                b'0'..=b'9' => (b - b'0') as u16,
                b'a'..=b'f' => (b - b'a' + 10) as u16,
                b'A'..=b'F' => (b - b'A' + 10) as u16,
                _ => return Err(PipeError::JsonInvalidEscape),
            };
            val = (val << 4) | digit;
        }
        Ok(val)
    }

    fn parse_string_unescaped(&mut self) -> Result<String, PipeError> {
        if self.next_byte() != Some(b'"') {
            return Err(PipeError::JsonMalformed);
        }
        let mut out = String::new();
        while self.pos < self.bytes.len() {
            let b = self.bytes[self.pos];
            if b == b'"' {
                self.pos += 1;
                return Ok(out);
            }
            if b == b'\\' {
                self.pos += 1;
                let esc = self.next_byte().ok_or(PipeError::JsonUnterminatedString)?;
                match esc {
                    b'"' => out.push('"'),
                    b'\\' => out.push('\\'),
                    b'/' => out.push('/'),
                    b'b' => out.push('\u{0008}'),
                    b'f' => out.push('\u{000C}'),
                    b'n' => out.push('\n'),
                    b'r' => out.push('\r'),
                    b't' => out.push('\t'),
                    b'u' => {
                        let code_unit = self.parse_hex4()?;
                        if (0xD800..=0xDBFF).contains(&code_unit) {
                            // High surrogate must be followed by `\uXXXX` low surrogate.
                            if self.next_byte() != Some(b'\\') || self.next_byte() != Some(b'u') {
                                return Err(PipeError::JsonInvalidEscape);
                            }
                            let low = self.parse_hex4()?;
                            if !(0xDC00..=0xDFFF).contains(&low) {
                                return Err(PipeError::JsonInvalidEscape);
                            }
                            let scalar = 0x10000u32
                                + (((code_unit as u32 - 0xD800) << 10) | (low as u32 - 0xDC00));
                            let ch = char::from_u32(scalar).ok_or(PipeError::JsonInvalidEscape)?;
                            out.push(ch);
                        } else if (0xDC00..=0xDFFF).contains(&code_unit) {
                            return Err(PipeError::JsonInvalidEscape);
                        } else {
                            let ch = char::from_u32(code_unit as u32)
                                .ok_or(PipeError::JsonInvalidEscape)?;
                            out.push(ch);
                        }
                    }
                    _ => return Err(PipeError::JsonInvalidEscape),
                }
            } else if b < 0x20 {
                // RFC 8259 forbids unescaped control characters U+0000..U+001F in strings.
                return Err(PipeError::JsonMalformed);
            } else {
                // Consume a complete valid UTF-8 character from `self.src`.
                let ch = self.src[self.pos..]
                    .chars()
                    .next()
                    .ok_or(PipeError::JsonUnterminatedString)?;
                out.push(ch);
                self.pos += ch.len_utf8();
            }
        }
        Err(PipeError::JsonUnterminatedString)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn build_mw_frame(opcode: u16, payload_len: i32, req_id: u32, body: &[u8]) -> Vec<u8> {
        let mut v = Vec::with_capacity(12 + body.len());
        v.extend_from_slice(&MW_MAGIC_BYTES);
        v.extend_from_slice(&opcode.to_le_bytes());
        v.extend_from_slice(&payload_len.to_le_bytes());
        v.extend_from_slice(&req_id.to_le_bytes());
        v.extend_from_slice(body);
        v
    }

    #[test]
    fn mw_frame_valid_ping_and_payload() {
        let raw = build_mw_frame(0x0001, 4, 42, b"PING");
        let frame = parse_mw_frame(&raw).expect("valid MW ping frame");
        assert_eq!(frame.header.opcode, 0x0001);
        assert_eq!(frame.header.payload_len, 4);
        assert_eq!(frame.header.req_id, 42);
        assert_eq!(frame.header.total_frame_len, 16);
        assert_eq!(frame.payload, b"PING");
    }

    #[test]
    fn mw_frame_zero_length_payload_and_trailing_bytes() {
        let mut raw = build_mw_frame(0x0002, 0, 99, b"");
        raw.extend_from_slice(b"NEXT_FRAME");
        let frame = parse_mw_frame(&raw).expect("zero-length payload frame");
        assert_eq!(frame.header.payload_len, 0);
        assert_eq!(frame.header.total_frame_len, 12);
        assert!(frame.payload.is_empty());
    }

    #[test]
    fn mw_frame_rejects_bad_magic_negative_and_oversized_len() {
        assert_eq!(parse_mw_frame(b"XY0123456789"), Err(PipeError::InvalidMagic));
        assert_eq!(parse_mw_frame(b"M"), Err(PipeError::NeedMoreData { needed: 12 }));
        assert_eq!(parse_mw_frame(b"MW\x01\x00"), Err(PipeError::NeedMoreData { needed: 12 }));

        let neg = build_mw_frame(0x0003, -1, 1, b"");
        assert_eq!(parse_mw_frame(&neg), Err(PipeError::NegativePayloadLength(-1)));

        let too_big = build_mw_frame(0x0003, (MW_MAX_PAYLOAD_LEN as i32) + 1, 1, b"");
        assert_eq!(
            parse_mw_frame(&too_big),
            Err(PipeError::PayloadTooLarge {
                payload_len: (MW_MAX_PAYLOAD_LEN as u32) + 1,
                max: MW_MAX_PAYLOAD_LEN as u32,
            })
        );

        // Exact 16 MiB header succeeds header check, then asks for more data.
        let exact_max = build_mw_frame(0x0003, MW_MAX_PAYLOAD_LEN as i32, 7, b"abc");
        let hdr = parse_mw_frame_header(&exact_max).expect("exact 16 MiB header is valid");
        assert_eq!(hdr.payload_len, MW_MAX_PAYLOAD_LEN as u32);
        assert_eq!(
            parse_mw_frame(&exact_max),
            Err(PipeError::NeedMoreData {
                needed: 12 + MW_MAX_PAYLOAD_LEN,
            })
        );
    }

    #[test]
    fn jsonrpc_extracts_standard_and_nested_params_with_escapes() {
        let msg = br#"{"jsonrpc":"2.0","id":"req-9","method":"execute","params":{"command":"Echo \"hello \\ world\"\nLine2","group":"vfs"}}"#;
        assert_eq!(is_json_rpc_2_0(msg), Ok(true));
        assert_eq!(
            extract_json_token(msg, "id", true, false),
            Ok(Some("\"req-9\"".to_string()))
        );
        assert_eq!(
            extract_json_token(msg, "method", false, false),
            Ok(Some("execute".to_string()))
        );
        assert_eq!(
            extract_json_token(msg, "command", false, true),
            Ok(Some("Echo \"hello \\ world\"\nLine2".to_string()))
        );
        assert_eq!(
            extract_json_token(msg, "group", false, true),
            Ok(Some("vfs".to_string()))
        );
    }

    #[test]
    fn jsonrpc_ignores_spoofed_keys_inside_string_values() {
        let spoof = br#"{"jsonrpc":"2.0","note":"\"method\":\"trigger_match_victory\"","id":77,"method":"ping"}"#;
        assert_eq!(
            extract_json_token(spoof, "method", false, true),
            Ok(Some("ping".to_string()))
        );
        assert_eq!(
            extract_json_token(spoof, "id", true, false),
            Ok(Some("77".to_string()))
        );
    }

    #[test]
    fn jsonrpc_handles_unicode_escapes_and_surrogate_pairs() {
        let msg = br#"{"jsonrpc":"2.0","id":1,"method":"ping","tag":"\u0041\uD83D\uDE80"}"#;
        assert_eq!(
            extract_json_token(msg, "tag", false, false),
            Ok(Some("A\u{1F680}".to_string()))
        );
        let bad_surrogate = br#"{"jsonrpc":"2.0","tag":"\uD83D"}"#;
        assert_eq!(
            extract_json_token(bad_surrogate, "tag", false, false),
            Err(PipeError::JsonInvalidEscape)
        );
    }

    #[test]
    fn jsonrpc_rejects_truncation_deep_nesting_and_oversized_lines() {
        assert_eq!(
            extract_json_token(br#"{"jsonrpc":"2.0","method":"unterminated"#, "method", false, false),
            Err(PipeError::JsonUnterminatedString)
        );

        let mut deep = String::from("{\"a\":");
        for _ in 0..(MAX_JSON_DEPTH + 5) {
            deep.push('[');
        }
        for _ in 0..(MAX_JSON_DEPTH + 5) {
            deep.push(']');
        }
        deep.push('}');
        assert_eq!(
            extract_json_token(deep.as_bytes(), "a", false, false),
            Err(PipeError::JsonNestingTooDeep {
                max: MAX_JSON_DEPTH
            })
        );

        let oversized = vec![b' '; MAX_TEXT_STREAM_BUFFER_BYTES + 1];
        assert_eq!(
            is_json_rpc_2_0(&oversized),
            Err(PipeError::TextLineTooLarge {
                actual: MAX_TEXT_STREAM_BUFFER_BYTES + 1,
                max: MAX_TEXT_STREAM_BUFFER_BYTES,
            })
        );
    }

    #[test]
    fn jsonrpc_handles_escaped_backslash_before_quote_numbers_booleans_and_trailing_garbage() {
        // Escaped backslash right before closing quote must NOT treat closing quote as escaped.
        let escaped_tail = br#"{"jsonrpc":"2.0","id":0,"method":"execute","params":{"command":"C:\\dir\\","flag":true,"empty":null,"num":-12.5e+2}}"#;
        assert_eq!(
            extract_json_token(escaped_tail, "command", false, true),
            Ok(Some("C:\\dir\\".to_string()))
        );
        assert_eq!(
            extract_json_token(escaped_tail, "flag", false, true),
            Ok(Some("true".to_string()))
        );
        assert_eq!(
            extract_json_token(escaped_tail, "empty", false, true),
            Ok(Some("null".to_string()))
        );
        assert_eq!(
            extract_json_token(escaped_tail, "num", false, true),
            Ok(Some("-12.5e+2".to_string()))
        );
        assert_eq!(
            extract_json_token(escaped_tail, "missing_key", false, true),
            Ok(None)
        );

        // Trailing garbage after root object must be rejected.
        assert_eq!(
            is_json_rpc_2_0(br#"{"jsonrpc":"2.0"} trailing"#),
            Err(PipeError::JsonMalformed)
        );
        // Unescaped control char inside string must be rejected.
        assert_eq!(
            extract_json_token(b"{\"jsonrpc\":\"2.0\",\"method\":\"a\x01b\"}", "method", false, false),
            Err(PipeError::JsonMalformed)
        );
        // Non-UTF8 input must be rejected.
        assert_eq!(
            is_json_rpc_2_0(&[0x7B, 0xFF, 0x7D]),
            Err(PipeError::InvalidUtf8)
        );
    }
}
