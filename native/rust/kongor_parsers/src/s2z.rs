// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! S2Z / VFS nasty-path firewall and 32-bit/ZIP64 EOCD + Central Directory validator.
//!
//! Parity reference: `native/proxies/native_guard/modern_archive_hook.cpp` lines 111-187 & 906-958,
//! plus PKWARE APPNOTE 6.3.x section 4.5.3 (ZIP64 Extended Information Extra Field `0x0001`).

use std::fmt;

/// Maximum permitted VFS path length in bytes (`modern_archive_hook.cpp:139`).
pub const MAX_VFS_PATH_LEN: usize = 1024;

/// Threshold above which K2's 32-bit `CMMapUnzip` must be bypassed (1.5 GiB).
pub const MODERN_ARCHIVE_THRESHOLD_BYTES: u64 = 1_610_612_736;

/// ZIP End of Central Directory signature (`PK\x05\x06`).
pub const ZIP_EOCD_SIG: u32 = 0x0605_4B50;

/// ZIP64 End of Central Directory Locator signature (`PK\x06\x07`).
pub const ZIP64_LOCATOR_SIG: u32 = 0x0706_4B50;

/// ZIP Central Directory File Header signature (`PK\x01\x02`).
pub const ZIP_CD_HEADER_SIG: u32 = 0x0201_4B50;

/// PKWARE APPNOTE ZIP64 Extended Information Extra Field Header ID (`0x0001`).
pub const ZIP64_EXTRA_FIELD_ID: u16 = 0x0001;

pub const ZIP_EOCD_MIN_LEN: usize = 22;
pub const ZIP64_LOCATOR_LEN: usize = 20;
pub const ZIP_CD_FIXED_LEN: usize = 46;
pub const ZIP_EOCD_MAX_TAIL_SCAN: usize = 65_557; // 22 + 65535 max comment

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum S2zError {
    EmptyPath,
    PathTooLong { actual: usize, max: usize },
    InvalidUtf8Path,
    LeadingSlashDisallowed,
    BackslashDisallowed,
    ControlOrIllegalChar(u8),
    DriveLetterDisallowed,
    TraversalSegment,
    EmptyPathSegment,
    TrailingDotOrSpace,
    ReservedDosDevice,
    EocdNotFound,
    InvalidCdSignature,
    TruncatedBuffer { needed: usize, actual: usize },
    CdRangeOutOfBounds,
    MalformedExtraField,
    MissingZip64ExtraField,
    TruncatedZip64ExtraField { needed: usize, actual: usize },
    ArithmeticOverflow,
}

impl fmt::Display for S2zError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::EmptyPath => write!(f, "VFS path is empty"),
            Self::PathTooLong { actual, max } => {
                write!(f, "VFS path length {actual} exceeds {max}")
            }
            Self::InvalidUtf8Path => write!(f, "VFS path is not valid UTF-8"),
            Self::LeadingSlashDisallowed => write!(f, "leading slash is not permitted"),
            Self::BackslashDisallowed => write!(f, "backslash separator is not permitted in VFS path"),
            Self::ControlOrIllegalChar(c) => {
                write!(f, "illegal or control byte 0x{c:02X} in VFS path")
            }
            Self::DriveLetterDisallowed => write!(f, "drive letter / colon is not permitted"),
            Self::TraversalSegment => write!(f, "path traversal segment ('.' or '..') rejected"),
            Self::EmptyPathSegment => write!(f, "empty path segment ('//' or trailing '/') rejected"),
            Self::TrailingDotOrSpace => write!(f, "path segment ends with '.' or space"),
            Self::ReservedDosDevice => write!(f, "reserved Windows DOS device name rejected"),
            Self::EocdNotFound => write!(f, "ZIP End of Central Directory (0x06054b50) not found"),
            Self::InvalidCdSignature => {
                write!(f, "invalid ZIP Central Directory signature (expected 0x02014b50)")
            }
            Self::TruncatedBuffer { needed, actual } => {
                write!(f, "truncated ZIP structure: need {needed} bytes, have {actual}")
            }
            Self::CdRangeOutOfBounds => {
                write!(f, "ZIP Central Directory offset + size exceeds EOCD position")
            }
            Self::MalformedExtraField => write!(f, "malformed/truncated ZIP extra field TLV"),
            Self::MissingZip64ExtraField => {
                write!(f, "ZIP64 sentinel set in CD header without 0x0001 extra field")
            }
            Self::TruncatedZip64ExtraField { needed, actual } => {
                write!(
                    f,
                    "ZIP64 extra field (0x0001) too small: need {needed} bytes, have {actual}"
                )
            }
            Self::ArithmeticOverflow => write!(f, "integer overflow while validating ZIP offsets"),
        }
    }
}

impl std::error::Error for S2zError {}

fn is_reserved_dos_stem(stem: &[u8]) -> bool {
    if stem.len() == 3 {
        let lower = [
            stem[0].to_ascii_lowercase(),
            stem[1].to_ascii_lowercase(),
            stem[2].to_ascii_lowercase(),
        ];
        matches!(&lower, b"con" | b"prn" | b"aux" | b"nul")
    } else if stem.len() == 4 && matches!(stem[3], b'1'..=b'9') {
        let lower = [
            stem[0].to_ascii_lowercase(),
            stem[1].to_ascii_lowercase(),
            stem[2].to_ascii_lowercase(),
        ];
        matches!(&lower, b"com" | b"lpt")
    } else {
        false
    }
}

/// Validates an untrusted VFS / S2Z entry path against traversal, NUL/control characters,
/// drive letters, backslashes, Windows illegal characters, and reserved DOS device stems.
pub fn check_nasty_path(path: &[u8], allow_leading_slash: bool) -> Result<(), S2zError> {
    if path.is_empty() {
        return Err(S2zError::EmptyPath);
    }
    if path.len() > MAX_VFS_PATH_LEN {
        return Err(S2zError::PathTooLong {
            actual: path.len(),
            max: MAX_VFS_PATH_LEN,
        });
    }

    // Reject embedded NUL, control chars (< 0x20, 0x7F), backslashes, drive-letter colons,
    // and Windows wildcard/redirect characters before segment splitting.
    for (idx, &b) in path.iter().enumerate() {
        if b == b'\\' {
            return Err(S2zError::BackslashDisallowed);
        }
        if b == b':' {
            if idx == 1 && path[0].is_ascii_alphabetic() {
                return Err(S2zError::DriveLetterDisallowed);
            }
            return Err(S2zError::ControlOrIllegalChar(b));
        }
        if b < 0x20 || b == 0x7F || matches!(b, b'*' | b'?' | b'"' | b'<' | b'>' | b'|') {
            return Err(S2zError::ControlOrIllegalChar(b));
        }
    }

    if std::str::from_utf8(path).is_err() {
        return Err(S2zError::InvalidUtf8Path);
    }

    if path[0] == b'/' && !allow_leading_slash {
        return Err(S2zError::LeadingSlashDisallowed);
    }

    let trimmed = if allow_leading_slash && path[0] == b'/' {
        if path.len() == 1 {
            return Err(S2zError::EmptyPathSegment);
        }
        &path[1..]
    } else {
        path
    };

    for seg in trimmed.split(|&b| b == b'/') {
        if seg.is_empty() {
            return Err(S2zError::EmptyPathSegment);
        }
        if seg == b"." || seg == b".." {
            return Err(S2zError::TraversalSegment);
        }
        if matches!(seg.last(), Some(b'.' | b' ')) {
            return Err(S2zError::TrailingDotOrSpace);
        }
        let stem = match seg.iter().position(|&b| b == b'.') {
            Some(dot_pos) => &seg[..dot_pos],
            None => seg,
        };
        if is_reserved_dos_stem(stem) {
            return Err(S2zError::ReservedDosDevice);
        }
    }

    Ok(())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ZipEocdInfo {
    /// Offset of the EOCD signature within the inspected `tail_bytes` slice.
    pub eocd_offset: u64,
    pub disk_number: u16,
    pub cd_start_disk: u16,
    pub entries_on_disk: u16,
    pub entries_total: u16,
    pub cd_size: u32,
    pub cd_offset: u32,
    pub comment_len: u16,
    pub is_zip64: bool,
    pub has_zip64_locator: bool,
    pub zip64_locator_eocd_offset: u64,
    pub requires_modern_archive: bool,
}

/// Scans a ZIP/S2Z tail buffer (or full archive slice) backwards for the authentic EOCD
/// (`0x06054b50`), checks ZIP64 sentinel values & preceding ZIP64 EOCD Locator (`0x07064b50`),
/// and validates 32-bit Central Directory bounds when non-ZIP64.
pub fn parse_zip_eocd(
    tail_bytes: &[u8],
    total_archive_size: u64,
) -> Result<ZipEocdInfo, S2zError> {
    if tail_bytes.len() < ZIP_EOCD_MIN_LEN {
        return Err(S2zError::TruncatedBuffer {
            needed: ZIP_EOCD_MIN_LEN,
            actual: tail_bytes.len(),
        });
    }

    let scan_start = tail_bytes.len().saturating_sub(ZIP_EOCD_MAX_TAIL_SCAN);
    let mut found_idx: Option<usize> = None;

    let mut idx = tail_bytes.len() - ZIP_EOCD_MIN_LEN;
    loop {
        let sig = u32::from_le_bytes([
            tail_bytes[idx],
            tail_bytes[idx + 1],
            tail_bytes[idx + 2],
            tail_bytes[idx + 3],
        ]);
        if sig == ZIP_EOCD_SIG {
            let comment_len =
                u16::from_le_bytes([tail_bytes[idx + 20], tail_bytes[idx + 21]]) as usize;
            if idx + ZIP_EOCD_MIN_LEN + comment_len == tail_bytes.len() {
                found_idx = Some(idx);
                break;
            }
        }
        if idx == scan_start {
            break;
        }
        idx -= 1;
    }

    let eocd_pos = found_idx.ok_or(S2zError::EocdNotFound)?;
    let disk_number = u16::from_le_bytes([tail_bytes[eocd_pos + 4], tail_bytes[eocd_pos + 5]]);
    let cd_start_disk = u16::from_le_bytes([tail_bytes[eocd_pos + 6], tail_bytes[eocd_pos + 7]]);
    let entries_on_disk = u16::from_le_bytes([tail_bytes[eocd_pos + 8], tail_bytes[eocd_pos + 9]]);
    let entries_total = u16::from_le_bytes([tail_bytes[eocd_pos + 10], tail_bytes[eocd_pos + 11]]);
    let cd_size = u32::from_le_bytes([
        tail_bytes[eocd_pos + 12],
        tail_bytes[eocd_pos + 13],
        tail_bytes[eocd_pos + 14],
        tail_bytes[eocd_pos + 15],
    ]);
    let cd_offset = u32::from_le_bytes([
        tail_bytes[eocd_pos + 16],
        tail_bytes[eocd_pos + 17],
        tail_bytes[eocd_pos + 18],
        tail_bytes[eocd_pos + 19],
    ]);
    let comment_len = u16::from_le_bytes([tail_bytes[eocd_pos + 20], tail_bytes[eocd_pos + 21]]);

    let has_sentinel = disk_number == 0xFFFF
        || cd_start_disk == 0xFFFF
        || entries_on_disk == 0xFFFF
        || entries_total == 0xFFFF
        || cd_size == 0xFFFF_FFFF
        || cd_offset == 0xFFFF_FFFF;

    let mut has_zip64_locator = false;
    let mut zip64_locator_eocd_offset = 0u64;
    if eocd_pos >= ZIP64_LOCATOR_LEN {
        let loc_pos = eocd_pos - ZIP64_LOCATOR_LEN;
        let loc_sig = u32::from_le_bytes([
            tail_bytes[loc_pos],
            tail_bytes[loc_pos + 1],
            tail_bytes[loc_pos + 2],
            tail_bytes[loc_pos + 3],
        ]);
        if loc_sig == ZIP64_LOCATOR_SIG {
            has_zip64_locator = true;
            zip64_locator_eocd_offset = u64::from_le_bytes([
                tail_bytes[loc_pos + 8],
                tail_bytes[loc_pos + 9],
                tail_bytes[loc_pos + 10],
                tail_bytes[loc_pos + 11],
                tail_bytes[loc_pos + 12],
                tail_bytes[loc_pos + 13],
                tail_bytes[loc_pos + 14],
                tail_bytes[loc_pos + 15],
            ]);
        }
    }

    let is_zip64 = has_sentinel || has_zip64_locator;
    let effective_total_size = total_archive_size.max(tail_bytes.len() as u64);
    let eocd_abs_offset = effective_total_size
        .checked_sub((tail_bytes.len() - eocd_pos) as u64)
        .ok_or(S2zError::ArithmeticOverflow)?;

    if !is_zip64 {
        let cd_end = (cd_offset as u64)
            .checked_add(cd_size as u64)
            .ok_or(S2zError::ArithmeticOverflow)?;
        if cd_end > eocd_abs_offset {
            return Err(S2zError::CdRangeOutOfBounds);
        }
    }

    let requires_modern_archive = is_zip64 || effective_total_size >= MODERN_ARCHIVE_THRESHOLD_BYTES;

    Ok(ZipEocdInfo {
        eocd_offset: eocd_abs_offset,
        disk_number,
        cd_start_disk,
        entries_on_disk,
        entries_total,
        cd_size,
        cd_offset,
        comment_len,
        is_zip64,
        has_zip64_locator,
        zip64_locator_eocd_offset,
        requires_modern_archive,
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ZipCdEntry<'a> {
    pub compression_method: u16,
    pub crc32: u32,
    pub compressed_size: u64,
    pub uncompressed_size: u64,
    pub local_header_offset: u64,
    pub disk_start: u32,
    pub filename: &'a [u8],
    pub is_zip64: bool,
    pub path_is_safe: bool,
    pub total_entry_len: usize,
}

/// Parses a single ZIP Central Directory entry (`0x02014b50`), validates all extra-field TLV bounds,
/// resolves ZIP64 sentinels (`0xFFFFFFFF` / `0xFFFF`) strictly from the `0x0001` extra field in
/// PKWARE APPNOTE 4.5.3 order, and evaluates VFS nasty-path safety on the filename.
pub fn parse_zip_cd_entry(buf: &[u8]) -> Result<ZipCdEntry<'_>, S2zError> {
    if buf.len() < ZIP_CD_FIXED_LEN {
        return Err(S2zError::TruncatedBuffer {
            needed: ZIP_CD_FIXED_LEN,
            actual: buf.len(),
        });
    }

    let sig = u32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]);
    if sig != ZIP_CD_HEADER_SIG {
        return Err(S2zError::InvalidCdSignature);
    }

    let compression_method = u16::from_le_bytes([buf[10], buf[11]]);
    let crc32 = u32::from_le_bytes([buf[16], buf[17], buf[18], buf[19]]);
    let comp_32 = u32::from_le_bytes([buf[20], buf[21], buf[22], buf[23]]);
    let uncomp_32 = u32::from_le_bytes([buf[24], buf[25], buf[26], buf[27]]);
    let filename_len = u16::from_le_bytes([buf[28], buf[29]]) as usize;
    let extra_len = u16::from_le_bytes([buf[30], buf[31]]) as usize;
    let comment_len = u16::from_le_bytes([buf[32], buf[33]]) as usize;
    let disk_start_16 = u16::from_le_bytes([buf[34], buf[35]]);
    let local_offset_32 = u32::from_le_bytes([buf[42], buf[43], buf[44], buf[45]]);

    let name_end = ZIP_CD_FIXED_LEN
        .checked_add(filename_len)
        .ok_or(S2zError::ArithmeticOverflow)?;
    let extra_end = name_end
        .checked_add(extra_len)
        .ok_or(S2zError::ArithmeticOverflow)?;
    let total_entry_len = extra_end
        .checked_add(comment_len)
        .ok_or(S2zError::ArithmeticOverflow)?;

    if buf.len() < total_entry_len {
        return Err(S2zError::TruncatedBuffer {
            needed: total_entry_len,
            actual: buf.len(),
        });
    }

    let filename = &buf[ZIP_CD_FIXED_LEN..name_end];
    let extra = &buf[name_end..extra_end];

    let need_uncomp = uncomp_32 == 0xFFFF_FFFF;
    let need_comp = comp_32 == 0xFFFF_FFFF;
    let need_offset = local_offset_32 == 0xFFFF_FFFF;
    let need_disk = disk_start_16 == 0xFFFF;
    let any_zip64_sentinel = need_uncomp || need_comp || need_offset || need_disk;

    // Validate EVERY extra-field TLV record so truncated/oversized extra records are caught.
    let mut zip64_payload: Option<&[u8]> = None;
    let mut cursor = 0usize;
    while cursor < extra.len() {
        if extra.len() - cursor < 4 {
            return Err(S2zError::MalformedExtraField);
        }
        let header_id = u16::from_le_bytes([extra[cursor], extra[cursor + 1]]);
        let data_size = u16::from_le_bytes([extra[cursor + 2], extra[cursor + 3]]) as usize;
        cursor += 4;
        let record_end = cursor
            .checked_add(data_size)
            .ok_or(S2zError::ArithmeticOverflow)?;
        if record_end > extra.len() {
            return Err(S2zError::MalformedExtraField);
        }
        if header_id == ZIP64_EXTRA_FIELD_ID && zip64_payload.is_none() {
            zip64_payload = Some(&extra[cursor..record_end]);
        }
        cursor = record_end;
    }

    let mut uncompressed_size = uncomp_32 as u64;
    let mut compressed_size = comp_32 as u64;
    let mut local_header_offset = local_offset_32 as u64;
    let mut disk_start = disk_start_16 as u32;

    if any_zip64_sentinel {
        let z64 = zip64_payload.ok_or(S2zError::MissingZip64ExtraField)?;
        let required_bytes = (if need_uncomp { 8 } else { 0 })
            + (if need_comp { 8 } else { 0 })
            + (if need_offset { 8 } else { 0 })
            + (if need_disk { 4 } else { 0 });
        if z64.len() < required_bytes {
            return Err(S2zError::TruncatedZip64ExtraField {
                needed: required_bytes,
                actual: z64.len(),
            });
        }

        let mut zpos = 0usize;
        if need_uncomp {
            uncompressed_size = u64::from_le_bytes([
                z64[zpos],
                z64[zpos + 1],
                z64[zpos + 2],
                z64[zpos + 3],
                z64[zpos + 4],
                z64[zpos + 5],
                z64[zpos + 6],
                z64[zpos + 7],
            ]);
            zpos += 8;
        }
        if need_comp {
            compressed_size = u64::from_le_bytes([
                z64[zpos],
                z64[zpos + 1],
                z64[zpos + 2],
                z64[zpos + 3],
                z64[zpos + 4],
                z64[zpos + 5],
                z64[zpos + 6],
                z64[zpos + 7],
            ]);
            zpos += 8;
        }
        if need_offset {
            local_header_offset = u64::from_le_bytes([
                z64[zpos],
                z64[zpos + 1],
                z64[zpos + 2],
                z64[zpos + 3],
                z64[zpos + 4],
                z64[zpos + 5],
                z64[zpos + 6],
                z64[zpos + 7],
            ]);
            zpos += 8;
        }
        if need_disk {
            disk_start = u32::from_le_bytes([z64[zpos], z64[zpos + 1], z64[zpos + 2], z64[zpos + 3]]);
        }
    }

    let path_is_safe = check_nasty_path(filename, false).is_ok();

    Ok(ZipCdEntry {
        compression_method,
        crc32,
        compressed_size,
        uncompressed_size,
        local_header_offset,
        disk_start,
        filename,
        is_zip64: any_zip64_sentinel || zip64_payload.is_some(),
        path_is_safe,
        total_entry_len,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_eocd(
        entries_disk: u16,
        entries_total: u16,
        cd_size: u32,
        cd_offset: u32,
        comment: &[u8],
    ) -> Vec<u8> {
        let mut out = Vec::with_capacity(22 + comment.len());
        out.extend_from_slice(&ZIP_EOCD_SIG.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes()); // disk_number
        out.extend_from_slice(&0u16.to_le_bytes()); // cd_start_disk
        out.extend_from_slice(&entries_disk.to_le_bytes());
        out.extend_from_slice(&entries_total.to_le_bytes());
        out.extend_from_slice(&cd_size.to_le_bytes());
        out.extend_from_slice(&cd_offset.to_le_bytes());
        out.extend_from_slice(&(comment.len() as u16).to_le_bytes());
        out.extend_from_slice(comment);
        out
    }

    fn make_cd_entry(
        filename: &[u8],
        comp_32: u32,
        uncomp_32: u32,
        disk_start: u16,
        local_off_32: u32,
        extra: &[u8],
    ) -> Vec<u8> {
        let mut out = vec![0u8; ZIP_CD_FIXED_LEN];
        out[0..4].copy_from_slice(&ZIP_CD_HEADER_SIG.to_le_bytes());
        out[10..12].copy_from_slice(&8u16.to_le_bytes()); // Deflate
        out[16..20].copy_from_slice(&0xDEADBEEFu32.to_le_bytes());
        out[20..24].copy_from_slice(&comp_32.to_le_bytes());
        out[24..28].copy_from_slice(&uncomp_32.to_le_bytes());
        out[28..30].copy_from_slice(&(filename.len() as u16).to_le_bytes());
        out[30..32].copy_from_slice(&(extra.len() as u16).to_le_bytes());
        out[34..36].copy_from_slice(&disk_start.to_le_bytes());
        out[42..46].copy_from_slice(&local_off_32.to_le_bytes());
        out.extend_from_slice(filename);
        out.extend_from_slice(extra);
        out
    }

    #[test]
    fn nasty_path_accepts_clean_vfs_paths() {
        assert_eq!(check_nasty_path(b"heroes/scout/model.mdf", false), Ok(()));
        assert_eq!(check_nasty_path(b"/ui/fe2/main.interface", true), Ok(()));
        assert_eq!(check_nasty_path(b"textures/00000000/icon_1.dds", false), Ok(()));
    }

    #[test]
    fn nasty_path_rejects_traversal_nul_drives_backslashes_controls_and_dos_names() {
        assert_eq!(check_nasty_path(b"", false), Err(S2zError::EmptyPath));
        assert_eq!(check_nasty_path(b"../secret.cfg", false), Err(S2zError::TraversalSegment));
        assert_eq!(check_nasty_path(b"heroes/../evil.dll", false), Err(S2zError::TraversalSegment));
        assert_eq!(check_nasty_path(b"heroes/./scout.mdf", false), Err(S2zError::TraversalSegment));
        assert_eq!(
            check_nasty_path(b"heroes/scout\0.mdf", false),
            Err(S2zError::ControlOrIllegalChar(0))
        );
        assert_eq!(
            check_nasty_path(b"C:/Windows/System32/cmd.exe", false),
            Err(S2zError::DriveLetterDisallowed)
        );
        assert_eq!(
            check_nasty_path(b"heroes\\scout\\model.mdf", false),
            Err(S2zError::BackslashDisallowed)
        );
        assert_eq!(
            check_nasty_path(b"heroes/bad\x1Fname.dds", false),
            Err(S2zError::ControlOrIllegalChar(0x1F))
        );
        assert_eq!(
            check_nasty_path(b"/leading/denied.lua", false),
            Err(S2zError::LeadingSlashDisallowed)
        );
        assert_eq!(check_nasty_path(b"heroes/CON.txt", false), Err(S2zError::ReservedDosDevice));
        assert_eq!(check_nasty_path(b"heroes/com1", false), Err(S2zError::ReservedDosDevice));
        assert_eq!(check_nasty_path(b"heroes/LPT9.dds", false), Err(S2zError::ReservedDosDevice));
        assert_eq!(check_nasty_path(b"heroes/trailing.", false), Err(S2zError::TrailingDotOrSpace));
        assert_eq!(check_nasty_path(b"heroes/trailing ", false), Err(S2zError::TrailingDotOrSpace));
        assert_eq!(check_nasty_path(b"heroes//double.dds", false), Err(S2zError::EmptyPathSegment));
        let too_long = vec![b'a'; MAX_VFS_PATH_LEN + 1];
        assert_eq!(
            check_nasty_path(&too_long, false),
            Err(S2zError::PathTooLong {
                actual: MAX_VFS_PATH_LEN + 1,
                max: MAX_VFS_PATH_LEN,
            })
        );
    }

    #[test]
    fn zip_eocd_parses_standard_and_detects_zip64_sentinels_and_locator() {
        let mut archive = vec![0u8; 100];
        let eocd = make_eocd(2, 2, 40, 60, b"kongor");
        archive.extend_from_slice(&eocd);

        let info = parse_zip_eocd(&archive, archive.len() as u64).expect("valid standard EOCD");
        assert!(!info.is_zip64);
        assert!(!info.requires_modern_archive);
        assert_eq!(info.eocd_offset, 100);
        assert_eq!(info.cd_offset, 60);
        assert_eq!(info.cd_size, 40);
        assert_eq!(info.comment_len, 6);

        // Out-of-bounds CD offset + size must fail on 32-bit archive.
        let mut bad_archive = vec![0u8; 50];
        bad_archive.extend_from_slice(&make_eocd(1, 1, 100, 10, b""));
        assert_eq!(
            parse_zip_eocd(&bad_archive, bad_archive.len() as u64),
            Err(S2zError::CdRangeOutOfBounds)
        );

        // ZIP64 sentinel + locator (0x07064b50).
        let mut z64_tail = Vec::new();
        z64_tail.extend_from_slice(&ZIP64_LOCATOR_SIG.to_le_bytes());
        z64_tail.extend_from_slice(&0u32.to_le_bytes());
        z64_tail.extend_from_slice(&0x0000_0001_2345_6789u64.to_le_bytes());
        z64_tail.extend_from_slice(&1u32.to_le_bytes());
        z64_tail.extend_from_slice(&make_eocd(0xFFFF, 0xFFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, b""));

        let z64_info = parse_zip_eocd(&z64_tail, 5_000_000_000).expect("valid ZIP64 tail");
        assert!(z64_info.is_zip64);
        assert!(z64_info.has_zip64_locator);
        assert_eq!(z64_info.zip64_locator_eocd_offset, 0x0000_0001_2345_6789);
        assert!(z64_info.requires_modern_archive);
    }

    #[test]
    fn zip_cd_entry_validates_zip64_extra_field_0x0001() {
        let mut extra = Vec::new();
        extra.extend_from_slice(&ZIP64_EXTRA_FIELD_ID.to_le_bytes());
        extra.extend_from_slice(&28u16.to_le_bytes());
        extra.extend_from_slice(&0x0000_0002_0000_1000u64.to_le_bytes()); // uncomp
        extra.extend_from_slice(&0x0000_0001_0000_0500u64.to_le_bytes()); // comp
        extra.extend_from_slice(&0x0000_0003_0000_0040u64.to_le_bytes()); // local offset
        extra.extend_from_slice(&2u32.to_le_bytes()); // disk start

        let raw = make_cd_entry(
            b"textures/00000000/hero.dds",
            0xFFFF_FFFF,
            0xFFFF_FFFF,
            0xFFFF,
            0xFFFF_FFFF,
            &extra,
        );
        let entry = parse_zip_cd_entry(&raw).expect("valid ZIP64 CD entry");
        assert!(entry.is_zip64);
        assert!(entry.path_is_safe);
        assert_eq!(entry.uncompressed_size, 0x0000_0002_0000_1000);
        assert_eq!(entry.compressed_size, 0x0000_0001_0000_0500);
        assert_eq!(entry.local_header_offset, 0x0000_0003_0000_0040);
        assert_eq!(entry.disk_start, 2);

        // Missing 0x0001 extra field when sentinel is present must fail.
        let missing = make_cd_entry(b"a.dds", 0xFFFF_FFFF, 100, 0, 0, &[]);
        assert_eq!(parse_zip_cd_entry(&missing), Err(S2zError::MissingZip64ExtraField));

        // Truncated 0x0001 extra field (only 8 bytes when 16 required) must fail.
        let mut short_extra = Vec::new();
        short_extra.extend_from_slice(&ZIP64_EXTRA_FIELD_ID.to_le_bytes());
        short_extra.extend_from_slice(&8u16.to_le_bytes());
        short_extra.extend_from_slice(&1234u64.to_le_bytes());
        let short_entry = make_cd_entry(b"a.dds", 0xFFFF_FFFF, 0xFFFF_FFFF, 0, 0, &short_extra);
        assert_eq!(
            parse_zip_cd_entry(&short_entry),
            Err(S2zError::TruncatedZip64ExtraField {
                needed: 16,
                actual: 8,
            })
        );

        // Zip-Slip path flagged as unsafe.
        let slip = make_cd_entry(b"../../version.dll", 10, 20, 0, 0, &[]);
        let parsed_slip = parse_zip_cd_entry(&slip).expect("parses structurally");
        assert!(!parsed_slip.path_is_safe);
    }

    #[test]
    fn zip_cd_partial_zip64_offset_only_and_malformed_extra_tlv() {
        // Preceding non-ZIP64 extra field (0x5455 UT timestamp) followed by ZIP64 offset-only extra.
        let mut extra = Vec::new();
        extra.extend_from_slice(&0x5455u16.to_le_bytes());
        extra.extend_from_slice(&5u16.to_le_bytes());
        extra.extend_from_slice(&[1, 2, 3, 4, 5]);
        extra.extend_from_slice(&ZIP64_EXTRA_FIELD_ID.to_le_bytes());
        extra.extend_from_slice(&8u16.to_le_bytes());
        extra.extend_from_slice(&0x0000_0001_0000_0000u64.to_le_bytes());

        let raw = make_cd_entry(b"maps/caldavar.s2z", 1234, 5678, 0, 0xFFFF_FFFF, &extra);
        let parsed = parse_zip_cd_entry(&raw).expect("offset-only ZIP64 CD entry");
        assert!(parsed.is_zip64);
        assert!(parsed.path_is_safe);
        assert_eq!(parsed.compressed_size, 1234);
        assert_eq!(parsed.uncompressed_size, 5678);
        assert_eq!(parsed.local_header_offset, 0x0000_0001_0000_0000);

        // Malformed TLV extra record extending beyond extra_len must be rejected.
        let bad_tlv = vec![0x01, 0x00, 0xFF, 0x00, 0xAA, 0xBB];
        let bad_entry = make_cd_entry(b"ok.dds", 10, 10, 0, 0, &bad_tlv);
        assert_eq!(
            parse_zip_cd_entry(&bad_entry),
            Err(S2zError::MalformedExtraField)
        );
    }
}
