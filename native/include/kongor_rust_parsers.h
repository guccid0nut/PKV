// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef KONGOR_RUST_PARSERS_H
#define KONGOR_RUST_PARSERS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Hand-maintained C ABI matching native/rust/kongor_parsers_ffi/src/lib.rs.
 * Struct sizes and field offsets are verified at compile time on both sides
 * (Rust const assertions + C++17 static_assert in test_rust_parsers_ffi.cpp). */
#define KONGOR_RUST_PARSERS_ABI_VERSION 1U

/* Uniform status codes returned by all kongor_* FFI entry points. */
#define KONGOR_PARSE_OK                      0
#define KONGOR_PARSE_ERR_NULL_PTR           -1
#define KONGOR_PARSE_ERR_NEED_MORE_DATA     -2
#define KONGOR_PARSE_ERR_INVALID_MAGIC      -3
#define KONGOR_PARSE_ERR_LENGTH_OUT_OF_RANGE -4
#define KONGOR_PARSE_ERR_MALFORMED          -5
#define KONGOR_PARSE_ERR_BUFFER_TOO_SMALL   -6
#define KONGOR_PARSE_ERR_NASTY_PATH         -7
#define KONGOR_PARSE_ERR_ZIP64_INVALID      -8
#define KONGOR_PARSE_ERR_OVERFLOW           -9
#define KONGOR_PARSE_ERR_UNAUTHORIZED_SENDER -10
#define KONGOR_PARSE_ERR_NESTING_TOO_DEEP   -11
#define KONGOR_PARSE_ERR_NOT_FOUND          -12
#define KONGOR_PARSE_ERR_PANIC              -99

typedef struct KongorMwFrameHeader {
    uint16_t opcode;
    uint16_t reserved;
    uint32_t payload_len;
    uint32_t req_id;
    uint32_t total_frame_len;
} KongorMwFrameHeader;

typedef struct KongorZipEocdInfo {
    uint64_t eocd_offset;
    uint64_t cd_offset;
    uint64_t cd_size;
    uint64_t zip64_locator_eocd_offset;
    uint32_t entries_on_disk;
    uint32_t entries_total;
    uint8_t is_zip64;
    uint8_t has_zip64_locator;
    uint8_t requires_modern_archive;
    uint8_t reserved[5];
} KongorZipEocdInfo;

typedef struct KongorZipCdEntryInfo {
    uint64_t compressed_size;
    uint64_t uncompressed_size;
    uint64_t local_header_offset;
    uint32_t crc32;
    uint32_t disk_start;
    uint32_t total_entry_len;
    uint16_t compression_method;
    uint16_t filename_len;
    uint8_t is_zip64;
    uint8_t path_is_safe;
    uint8_t reserved[6];
} KongorZipCdEntryInfo;

typedef struct KongorDdsInfo {
    uint64_t expected_min_size;
    uint32_t width;
    uint32_t height;
    uint32_t mip_count;
    uint32_t header_bytes;
    uint32_t dxgi_format;
    uint32_t fourcc;
    uint8_t is_dx10;
    uint8_t is_cubemap;
    uint8_t is_block_compressed;
    uint8_t block_or_pixel_bytes;
    uint8_t reserved[4];
} KongorDdsInfo;

typedef struct KongorCompelWatermarkInfo {
    uint32_t challenge;
    uint16_t counter;
    uint8_t is_voice;
    uint8_t reserved;
    uint32_t payload_len;
    uint32_t inner_offset;
} KongorCompelWatermarkInfo;

/* Returns KONGOR_RUST_PARSERS_ABI_VERSION from the linked Rust staticlib. */
uint32_t kongor_rust_parsers_abi_version(void);

/* 1. Named-pipe 'MW' binary framing & safe JSON-RPC 2.0 token extraction */
int32_t kongor_pipe_parse_mw_frame(
    const uint8_t* data,
    size_t len,
    KongorMwFrameHeader* out_header);

int32_t kongor_pipe_is_jsonrpc_2_0(
    const uint8_t* json_utf8,
    size_t json_len,
    uint8_t* out_is_jsonrpc);

int32_t kongor_pipe_extract_json_token(
    const uint8_t* json_utf8,
    size_t json_len,
    const char* key_cstr,
    uint8_t preserve_quotes,
    uint8_t search_params,
    char* out_buf,
    size_t out_cap,
    size_t* out_len);

/* 2. S2Z / VFS nasty-path firewall & ZIP/ZIP64 EOCD + Central Directory validator */
int32_t kongor_s2z_check_nasty_path(
    const uint8_t* path_bytes,
    size_t path_len,
    uint8_t allow_leading_slash);

int32_t kongor_s2z_parse_eocd(
    const uint8_t* tail_bytes,
    size_t tail_len,
    uint64_t total_archive_size,
    KongorZipEocdInfo* out_info);

int32_t kongor_s2z_parse_cd_entry(
    const uint8_t* cd_bytes,
    size_t cd_len,
    KongorZipCdEntryInfo* out_info);

/* 3. DDS / DX10 header & overflow-checked mipmap chain + cubemap validator */
int32_t kongor_dds_verify_mipmap_chain(
    const uint8_t* data,
    size_t len,
    KongorDdsInfo* out_info);

/* 4. UDP K2 6-byte ping probe, loopback map: guard & COMPEL 40-byte watermark */
int32_t kongor_udp_parse_ping_probe(
    const uint8_t* pkt,
    size_t len,
    uint16_t* out_seq);

int32_t kongor_udp_extract_loopback_map(
    const uint8_t* pkt,
    size_t len,
    uint32_t sender_ipv4_host_order,
    char* out_map,
    size_t out_cap,
    size_t* out_len);

int32_t kongor_udp_verify_compel_watermark(
    const uint8_t* pkt,
    size_t len,
    uint8_t is_voice,
    KongorCompelWatermarkInfo* out_info);

#ifdef __cplusplus
}
#endif

#endif /* KONGOR_RUST_PARSERS_H */
