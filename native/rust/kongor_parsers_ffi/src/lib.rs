// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! Thin C-ABI staticlib bridge for [`kongor_parsers`].
//!
//! Every `extern "C"` entry point enforces:
//! 1. Strict null-pointer and length bounds checks before forming any Rust slice.
//! 2. `std::panic::catch_unwind` isolation so Rust panics can never unwind across FFI into C++.
//! 3. Compile-time `#[repr(C)]` struct size assertions matching `native/include/kongor_rust_parsers.h`.

use std::ffi::{c_char, CStr};
use std::mem::{align_of, size_of};
use std::panic::{catch_unwind, AssertUnwindSafe};

use kongor_parsers::dds::{verify_dds_mipmap_chain, DdsError, DdsInfo};
use kongor_parsers::pipe::{extract_json_token, is_json_rpc_2_0, parse_mw_frame, PipeError};
use kongor_parsers::s2z::{check_nasty_path, parse_zip_cd_entry, parse_zip_eocd, S2zError};
use kongor_parsers::udp::{
    parse_compel_watermark, parse_k2_ping_probe, parse_loopback_map_update, UdpError,
    KONGOR_WATERMARK_PREFIX_LEN,
};

pub const KONGOR_RUST_PARSERS_ABI_VERSION: u32 = 1;

pub const KONGOR_PARSE_OK: i32 = 0;
pub const KONGOR_PARSE_ERR_NULL_PTR: i32 = -1;
pub const KONGOR_PARSE_ERR_NEED_MORE_DATA: i32 = -2;
pub const KONGOR_PARSE_ERR_INVALID_MAGIC: i32 = -3;
pub const KONGOR_PARSE_ERR_LENGTH_OUT_OF_RANGE: i32 = -4;
pub const KONGOR_PARSE_ERR_MALFORMED: i32 = -5;
pub const KONGOR_PARSE_ERR_BUFFER_TOO_SMALL: i32 = -6;
pub const KONGOR_PARSE_ERR_NASTY_PATH: i32 = -7;
pub const KONGOR_PARSE_ERR_ZIP64_INVALID: i32 = -8;
pub const KONGOR_PARSE_ERR_OVERFLOW: i32 = -9;
pub const KONGOR_PARSE_ERR_UNAUTHORIZED_SENDER: i32 = -10;
pub const KONGOR_PARSE_ERR_NESTING_TOO_DEEP: i32 = -11;
pub const KONGOR_PARSE_ERR_NOT_FOUND: i32 = -12;
pub const KONGOR_PARSE_ERR_PANIC: i32 = -99;

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct KongorMwFrameHeader {
    pub opcode: u16,
    pub reserved: u16,
    pub payload_len: u32,
    pub req_id: u32,
    pub total_frame_len: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct KongorZipEocdInfo {
    pub eocd_offset: u64,
    pub cd_offset: u64,
    pub cd_size: u64,
    pub zip64_locator_eocd_offset: u64,
    pub entries_on_disk: u32,
    pub entries_total: u32,
    pub is_zip64: u8,
    pub has_zip64_locator: u8,
    pub requires_modern_archive: u8,
    pub reserved: [u8; 5],
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct KongorZipCdEntryInfo {
    pub compressed_size: u64,
    pub uncompressed_size: u64,
    pub local_header_offset: u64,
    pub crc32: u32,
    pub disk_start: u32,
    pub total_entry_len: u32,
    pub compression_method: u16,
    pub filename_len: u16,
    pub is_zip64: u8,
    pub path_is_safe: u8,
    pub reserved: [u8; 6],
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct KongorDdsInfo {
    pub expected_min_size: u64,
    pub width: u32,
    pub height: u32,
    pub mip_count: u32,
    pub header_bytes: u32,
    pub dxgi_format: u32,
    pub fourcc: u32,
    pub is_dx10: u8,
    pub is_cubemap: u8,
    pub is_block_compressed: u8,
    pub block_or_pixel_bytes: u8,
    pub reserved: [u8; 4],
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct KongorCompelWatermarkInfo {
    pub challenge: u32,
    pub counter: u16,
    pub is_voice: u8,
    pub reserved: u8,
    pub payload_len: u32,
    pub inner_offset: u32,
}

// Compile-time ABI layout invariants mirrored in test_rust_parsers_ffi.cpp.
const _: () = {
    assert!(size_of::<KongorMwFrameHeader>() == 16);
    assert!(align_of::<KongorMwFrameHeader>() == 4);
    assert!(size_of::<KongorZipEocdInfo>() == 48);
    assert!(align_of::<KongorZipEocdInfo>() == 8);
    assert!(size_of::<KongorZipCdEntryInfo>() == 48);
    assert!(align_of::<KongorZipCdEntryInfo>() == 8);
    assert!(size_of::<KongorDdsInfo>() == 40);
    assert!(align_of::<KongorDdsInfo>() == 8);
    assert!(size_of::<KongorCompelWatermarkInfo>() == 16);
    assert!(align_of::<KongorCompelWatermarkInfo>() == 4);
};

fn guard_ffi<F: FnOnce() -> i32>(f: F) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(KONGOR_PARSE_ERR_PANIC)
}

unsafe fn slice_from_raw<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], i32> {
    if ptr.is_null() {
        return Err(KONGOR_PARSE_ERR_NULL_PTR);
    }
    if len > isize::MAX as usize {
        return Err(KONGOR_PARSE_ERR_LENGTH_OUT_OF_RANGE);
    }
    Ok(std::slice::from_raw_parts(ptr, len))
}

unsafe fn copy_cstr_out(
    src: &[u8],
    out_buf: *mut c_char,
    out_cap: usize,
    out_len: *mut usize,
) -> i32 {
    if out_buf.is_null() {
        return KONGOR_PARSE_ERR_NULL_PTR;
    }
    if !out_len.is_null() {
        *out_len = src.len();
    }
    if out_cap <= src.len() {
        return KONGOR_PARSE_ERR_BUFFER_TOO_SMALL;
    }
    let dst = std::slice::from_raw_parts_mut(out_buf as *mut u8, out_cap);
    dst[..src.len()].copy_from_slice(src);
    dst[src.len()] = 0;
    KONGOR_PARSE_OK
}

fn fill_dds_info(info: &DdsInfo) -> KongorDdsInfo {
    KongorDdsInfo {
        expected_min_size: info.expected_min_size,
        width: info.width,
        height: info.height,
        mip_count: info.mip_count,
        header_bytes: info.header_bytes,
        dxgi_format: info.dxgi_format,
        fourcc: info.fourcc,
        is_dx10: u8::from(info.is_dx10),
        is_cubemap: u8::from(info.is_cubemap),
        is_block_compressed: u8::from(info.is_block_compressed),
        block_or_pixel_bytes: info.block_or_pixel_bytes,
        reserved: [0; 4],
    }
}

fn map_pipe_error(err: PipeError) -> i32 {
    match err {
        PipeError::NeedMoreData { .. } => KONGOR_PARSE_ERR_NEED_MORE_DATA,
        PipeError::InvalidMagic => KONGOR_PARSE_ERR_INVALID_MAGIC,
        PipeError::NegativePayloadLength(_)
        | PipeError::PayloadTooLarge { .. }
        | PipeError::TextLineTooLarge { .. } => KONGOR_PARSE_ERR_LENGTH_OUT_OF_RANGE,
        PipeError::JsonNestingTooDeep { .. } => KONGOR_PARSE_ERR_NESTING_TOO_DEEP,
        PipeError::InvalidUtf8
        | PipeError::JsonNotAnObject
        | PipeError::JsonUnterminatedString
        | PipeError::JsonInvalidEscape
        | PipeError::JsonMalformed => KONGOR_PARSE_ERR_MALFORMED,
    }
}

#[no_mangle]
pub extern "C" fn kongor_rust_parsers_abi_version() -> u32 {
    KONGOR_RUST_PARSERS_ABI_VERSION
}

#[no_mangle]
pub unsafe extern "C" fn kongor_pipe_parse_mw_frame(
    data: *const u8,
    len: usize,
    out_header: *mut KongorMwFrameHeader,
) -> i32 {
    guard_ffi(|| {
        if out_header.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(data, len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match parse_mw_frame(slice) {
            Ok(frame) => {
                *out_header = KongorMwFrameHeader {
                    opcode: frame.header.opcode,
                    reserved: 0,
                    payload_len: frame.header.payload_len,
                    req_id: frame.header.req_id,
                    total_frame_len: frame.header.total_frame_len as u32,
                };
                KONGOR_PARSE_OK
            }
            Err(e) => map_pipe_error(e),
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_pipe_is_jsonrpc_2_0(
    json_utf8: *const u8,
    json_len: usize,
    out_is_jsonrpc: *mut u8,
) -> i32 {
    guard_ffi(|| {
        if out_is_jsonrpc.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(json_utf8, json_len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match is_json_rpc_2_0(slice) {
            Ok(flag) => {
                *out_is_jsonrpc = u8::from(flag);
                KONGOR_PARSE_OK
            }
            Err(e) => {
                *out_is_jsonrpc = 0;
                map_pipe_error(e)
            }
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_pipe_extract_json_token(
    json_utf8: *const u8,
    json_len: usize,
    key_cstr: *const c_char,
    preserve_quotes: u8,
    search_params: u8,
    out_buf: *mut c_char,
    out_cap: usize,
    out_len: *mut usize,
) -> i32 {
    guard_ffi(|| {
        if key_cstr.is_null() || out_buf.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(json_utf8, json_len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        let key = match CStr::from_ptr(key_cstr).to_str() {
            Ok(k) => k,
            Err(_) => return KONGOR_PARSE_ERR_MALFORMED,
        };
        match extract_json_token(slice, key, preserve_quotes != 0, search_params != 0) {
            Ok(Some(val)) => copy_cstr_out(val.as_bytes(), out_buf, out_cap, out_len),
            Ok(None) => {
                if !out_len.is_null() {
                    *out_len = 0;
                }
                KONGOR_PARSE_ERR_NOT_FOUND
            }
            Err(e) => map_pipe_error(e),
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_s2z_check_nasty_path(
    path_bytes: *const u8,
    path_len: usize,
    allow_leading_slash: u8,
) -> i32 {
    guard_ffi(|| {
        let slice = match slice_from_raw(path_bytes, path_len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match check_nasty_path(slice, allow_leading_slash != 0) {
            Ok(()) => KONGOR_PARSE_OK,
            Err(_) => KONGOR_PARSE_ERR_NASTY_PATH,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_s2z_parse_eocd(
    tail_bytes: *const u8,
    tail_len: usize,
    total_archive_size: u64,
    out_info: *mut KongorZipEocdInfo,
) -> i32 {
    guard_ffi(|| {
        if out_info.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(tail_bytes, tail_len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match parse_zip_eocd(slice, total_archive_size) {
            Ok(info) => {
                *out_info = KongorZipEocdInfo {
                    eocd_offset: info.eocd_offset,
                    cd_offset: info.cd_offset as u64,
                    cd_size: info.cd_size as u64,
                    zip64_locator_eocd_offset: info.zip64_locator_eocd_offset,
                    entries_on_disk: info.entries_on_disk as u32,
                    entries_total: info.entries_total as u32,
                    is_zip64: u8::from(info.is_zip64),
                    has_zip64_locator: u8::from(info.has_zip64_locator),
                    requires_modern_archive: u8::from(info.requires_modern_archive),
                    reserved: [0; 5],
                };
                KONGOR_PARSE_OK
            }
            Err(S2zError::TruncatedBuffer { .. }) => KONGOR_PARSE_ERR_NEED_MORE_DATA,
            Err(S2zError::EocdNotFound) => KONGOR_PARSE_ERR_INVALID_MAGIC,
            Err(S2zError::ArithmeticOverflow) => KONGOR_PARSE_ERR_OVERFLOW,
            Err(_) => KONGOR_PARSE_ERR_MALFORMED,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_s2z_parse_cd_entry(
    cd_bytes: *const u8,
    cd_len: usize,
    out_info: *mut KongorZipCdEntryInfo,
) -> i32 {
    guard_ffi(|| {
        if out_info.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(cd_bytes, cd_len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match parse_zip_cd_entry(slice) {
            Ok(entry) => {
                *out_info = KongorZipCdEntryInfo {
                    compressed_size: entry.compressed_size,
                    uncompressed_size: entry.uncompressed_size,
                    local_header_offset: entry.local_header_offset,
                    crc32: entry.crc32,
                    disk_start: entry.disk_start,
                    total_entry_len: entry.total_entry_len as u32,
                    compression_method: entry.compression_method,
                    filename_len: entry.filename.len() as u16,
                    is_zip64: u8::from(entry.is_zip64),
                    path_is_safe: u8::from(entry.path_is_safe),
                    reserved: [0; 6],
                };
                KONGOR_PARSE_OK
            }
            Err(S2zError::TruncatedBuffer { .. }) => KONGOR_PARSE_ERR_NEED_MORE_DATA,
            Err(S2zError::InvalidCdSignature) => KONGOR_PARSE_ERR_INVALID_MAGIC,
            Err(S2zError::MissingZip64ExtraField | S2zError::TruncatedZip64ExtraField { .. }) => {
                KONGOR_PARSE_ERR_ZIP64_INVALID
            }
            Err(S2zError::ArithmeticOverflow) => KONGOR_PARSE_ERR_OVERFLOW,
            Err(_) => KONGOR_PARSE_ERR_MALFORMED,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_dds_verify_mipmap_chain(
    data: *const u8,
    len: usize,
    out_info: *mut KongorDdsInfo,
) -> i32 {
    guard_ffi(|| {
        if out_info.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(data, len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match verify_dds_mipmap_chain(slice) {
            Ok(info) => {
                *out_info = fill_dds_info(&info);
                KONGOR_PARSE_OK
            }
            Err(DdsError::TruncatedPayload { info, .. }) => {
                *out_info = fill_dds_info(&info);
                KONGOR_PARSE_ERR_NEED_MORE_DATA
            }
            Err(DdsError::TruncatedHeader { required, .. }) => {
                *out_info = KongorDdsInfo {
                    expected_min_size: required as u64,
                    ..KongorDdsInfo::default()
                };
                KONGOR_PARSE_ERR_NEED_MORE_DATA
            }
            Err(DdsError::InvalidMagic(_)) => KONGOR_PARSE_ERR_INVALID_MAGIC,
            Err(DdsError::SizeOverflow) => KONGOR_PARSE_ERR_OVERFLOW,
            Err(DdsError::InvalidHeaderSize(_) | DdsError::InvalidDimensions { .. }) => {
                KONGOR_PARSE_ERR_MALFORMED
            }
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_udp_parse_ping_probe(
    pkt: *const u8,
    len: usize,
    out_seq: *mut u16,
) -> i32 {
    guard_ffi(|| {
        if out_seq.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(pkt, len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match parse_k2_ping_probe(slice) {
            Ok(seq) => {
                *out_seq = seq;
                KONGOR_PARSE_OK
            }
            Err(_) => KONGOR_PARSE_ERR_INVALID_MAGIC,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_udp_extract_loopback_map(
    pkt: *const u8,
    len: usize,
    sender_ipv4_host_order: u32,
    out_map: *mut c_char,
    out_cap: usize,
    out_len: *mut usize,
) -> i32 {
    guard_ffi(|| {
        if out_map.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(pkt, len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match parse_loopback_map_update(slice, sender_ipv4_host_order) {
            Ok(map_name) => copy_cstr_out(map_name.as_bytes(), out_map, out_cap, out_len),
            Err(UdpError::UnauthorizedSender(_)) => KONGOR_PARSE_ERR_UNAUTHORIZED_SENDER,
            Err(UdpError::NotAMapUpdate) => KONGOR_PARSE_ERR_INVALID_MAGIC,
            Err(_) => KONGOR_PARSE_ERR_MALFORMED,
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn kongor_udp_verify_compel_watermark(
    pkt: *const u8,
    len: usize,
    is_voice: u8,
    out_info: *mut KongorCompelWatermarkInfo,
) -> i32 {
    guard_ffi(|| {
        if out_info.is_null() {
            return KONGOR_PARSE_ERR_NULL_PTR;
        }
        let slice = match slice_from_raw(pkt, len) {
            Ok(s) => s,
            Err(code) => return code,
        };
        match parse_compel_watermark(slice, is_voice != 0) {
            Ok(wm) => {
                *out_info = KongorCompelWatermarkInfo {
                    challenge: wm.challenge,
                    counter: wm.counter,
                    is_voice: u8::from(wm.is_voice),
                    reserved: 0,
                    payload_len: wm.payload_len as u32,
                    inner_offset: KONGOR_WATERMARK_PREFIX_LEN as u32,
                };
                KONGOR_PARSE_OK
            }
            Err(UdpError::TooShort { .. }) => KONGOR_PARSE_ERR_NEED_MORE_DATA,
            Err(UdpError::MalformedSentinelAllOnes | UdpError::MalformedSentinelAllZeros) => {
                KONGOR_PARSE_ERR_MALFORMED
            }
            Err(_) => KONGOR_PARSE_ERR_MALFORMED,
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr;

    #[test]
    fn ffi_null_guards_fail_closed() {
        unsafe {
            assert_eq!(kongor_rust_parsers_abi_version(), KONGOR_RUST_PARSERS_ABI_VERSION);
            let mut hdr = KongorMwFrameHeader::default();
            assert_eq!(
                kongor_pipe_parse_mw_frame(ptr::null(), 0, &mut hdr),
                KONGOR_PARSE_ERR_NULL_PTR
            );
            assert_eq!(
                kongor_pipe_parse_mw_frame(b"MW".as_ptr(), 2, ptr::null_mut()),
                KONGOR_PARSE_ERR_NULL_PTR
            );
            assert_eq!(
                kongor_s2z_check_nasty_path(ptr::null(), 5, 0),
                KONGOR_PARSE_ERR_NULL_PTR
            );
            assert_eq!(
                kongor_dds_verify_mipmap_chain(ptr::null(), 128, ptr::null_mut()),
                KONGOR_PARSE_ERR_NULL_PTR
            );
            assert_eq!(
                kongor_udp_parse_ping_probe(ptr::null(), 6, ptr::null_mut()),
                KONGOR_PARSE_ERR_NULL_PTR
            );
        }
    }
}
