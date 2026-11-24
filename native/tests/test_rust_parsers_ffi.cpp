// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Wave 4 Track 3 (w4-rust-boundary): Cross-Language C++17 <-> Rust Staticlib FFI Tests
//
// Test Value Bar:
// 1. protects:
//    - C-ABI layout parity (sizeof/offsetof) between native/include/kongor_rust_parsers.h
//      and native/rust/kongor_parsers_ffi/src/lib.rs
//    - All 4 untrusted byte-boundary parsers linked as a real MSVC staticlib (.lib):
//      (a) Pipe 'MW' 12B framing + safe RFC 8259 JSON-RPC 2.0 token extraction
//      (b) S2Z/VFS nasty-path firewall + ZIP EOCD & Central Directory ZIP64 (0x0001)
//      (c) DDS / DX10 mipmap chain & cubemap verification
//      (d) UDP 6-byte K2 ping probe, loopback map: guard & COMPEL 40B watermark
//    - Fail-closed null-pointer & buffer-capacity guards on every FFI entry point
// 2. fails_when:
//    - Struct packing/padding drifts across MSVC C++17 and rustc x86_64-pc-windows-msvc
//    - Any parser accepts malformed/spoofed/truncated/overflowing untrusted bytes
//    - Rejected FFI calls mutate caller output buffers or crash on NULL pointers
// 3. seam:
//    - Real compiled Rust staticlib (`kongor_parsers_ffi.lib` / `libkongor_parsers_ffi.a`)
//      linked directly into this MSVC C++17 binary via `native/cmake/KongorRust.cmake`
// ============================================================================

#include "kongor_rust_parsers.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// Compile-time ABI layout invariants matching kongor_parsers_ffi/src/lib.rs
static_assert(sizeof(KongorMwFrameHeader) == 16, "KongorMwFrameHeader ABI size mismatch");
static_assert(alignof(KongorMwFrameHeader) == 4, "KongorMwFrameHeader ABI align mismatch");
static_assert(offsetof(KongorMwFrameHeader, opcode) == 0, "opcode offset mismatch");
static_assert(offsetof(KongorMwFrameHeader, payload_len) == 4, "payload_len offset mismatch");
static_assert(offsetof(KongorMwFrameHeader, req_id) == 8, "req_id offset mismatch");
static_assert(offsetof(KongorMwFrameHeader, total_frame_len) == 12, "total_frame_len offset mismatch");

static_assert(sizeof(KongorZipEocdInfo) == 48, "KongorZipEocdInfo ABI size mismatch");
static_assert(alignof(KongorZipEocdInfo) == 8, "KongorZipEocdInfo ABI align mismatch");
static_assert(offsetof(KongorZipEocdInfo, is_zip64) == 40, "is_zip64 offset mismatch");
static_assert(offsetof(KongorZipEocdInfo, requires_modern_archive) == 42, "requires_modern_archive offset mismatch");

static_assert(sizeof(KongorZipCdEntryInfo) == 48, "KongorZipCdEntryInfo ABI size mismatch");
static_assert(alignof(KongorZipCdEntryInfo) == 8, "KongorZipCdEntryInfo ABI align mismatch");
static_assert(offsetof(KongorZipCdEntryInfo, is_zip64) == 40, "cd is_zip64 offset mismatch");
static_assert(offsetof(KongorZipCdEntryInfo, path_is_safe) == 41, "cd path_is_safe offset mismatch");

static_assert(sizeof(KongorDdsInfo) == 40, "KongorDdsInfo ABI size mismatch");
static_assert(alignof(KongorDdsInfo) == 8, "KongorDdsInfo ABI align mismatch");
static_assert(offsetof(KongorDdsInfo, expected_min_size) == 0, "dds expected_min_size offset mismatch");
static_assert(offsetof(KongorDdsInfo, is_dx10) == 32, "dds is_dx10 offset mismatch");
static_assert(offsetof(KongorDdsInfo, block_or_pixel_bytes) == 35, "dds block_or_pixel_bytes offset mismatch");

static_assert(sizeof(KongorCompelWatermarkInfo) == 16, "KongorCompelWatermarkInfo ABI size mismatch");
static_assert(alignof(KongorCompelWatermarkInfo) == 4, "KongorCompelWatermarkInfo ABI align mismatch");
static_assert(offsetof(KongorCompelWatermarkInfo, challenge) == 0, "watermark challenge offset mismatch");
static_assert(offsetof(KongorCompelWatermarkInfo, counter) == 4, "watermark counter offset mismatch");
static_assert(offsetof(KongorCompelWatermarkInfo, inner_offset) == 12, "watermark inner_offset offset mismatch");

static int g_assertions = 0;
static int g_failures = 0;

#define EXPECT_TRUE(cond, msg)                                                                           \
    do {                                                                                                 \
        ++g_assertions;                                                                                  \
        if (!(cond)) {                                                                                   \
            std::fprintf(stderr,                                                                         \
                "[FAIL] %s:%d: %s (%s)\n"                                                                \
                "  -> Diagnose: powershell -NoProfile -ExecutionPolicy Bypass -File "                    \
                "scripts/Invoke-KongorTestLock.ps1 -Owner test -- "                                      \
                "ctest --test-dir native/build_rust_spike -C Release -R test_rust_parsers_ffi "          \
                "--output-on-failure\n",                                                                 \
                __FILE__, __LINE__, (msg), #cond);                                                       \
            ++g_failures;                                                                                \
        }                                                                                                \
    } while (0)

static void AppendU16Le(std::vector<uint8_t>& buf, uint16_t v) {
    buf.push_back(static_cast<uint8_t>(v & 0xFFu));
    buf.push_back(static_cast<uint8_t>((v >> 8) & 0xFFu));
}

static void AppendU32Le(std::vector<uint8_t>& buf, uint32_t v) {
    buf.push_back(static_cast<uint8_t>(v & 0xFFu));
    buf.push_back(static_cast<uint8_t>((v >> 8) & 0xFFu));
    buf.push_back(static_cast<uint8_t>((v >> 16) & 0xFFu));
    buf.push_back(static_cast<uint8_t>((v >> 24) & 0xFFu));
}

static void AppendU64Le(std::vector<uint8_t>& buf, uint64_t v) {
    for (int i = 0; i < 8; ++i) {
        buf.push_back(static_cast<uint8_t>((v >> (i * 8)) & 0xFFu));
    }
}

static void WriteU32Le(std::vector<uint8_t>& buf, size_t off, uint32_t v) {
    buf[off]     = static_cast<uint8_t>(v & 0xFFu);
    buf[off + 1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
    buf[off + 2] = static_cast<uint8_t>((v >> 16) & 0xFFu);
    buf[off + 3] = static_cast<uint8_t>((v >> 24) & 0xFFu);
}

static void WriteU16Le(std::vector<uint8_t>& buf, size_t off, uint16_t v) {
    buf[off]     = static_cast<uint8_t>(v & 0xFFu);
    buf[off + 1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
}

static void TestAbiAndNullGuards() {
    std::printf("[TEST] [FFI-00] ABI version & null-pointer fail-closed guards...\n");

    EXPECT_TRUE(kongor_rust_parsers_abi_version() == KONGOR_RUST_PARSERS_ABI_VERSION,
        "Rust staticlib ABI version must match C header");

    KongorMwFrameHeader hdr{};
    EXPECT_TRUE(kongor_pipe_parse_mw_frame(nullptr, 12, &hdr) == KONGOR_PARSE_ERR_NULL_PTR,
        "Null input data must return KONGOR_PARSE_ERR_NULL_PTR");
    const uint8_t dummy[4] = { 0x4D, 0x57, 0x01, 0x00 };
    EXPECT_TRUE(kongor_pipe_parse_mw_frame(dummy, sizeof(dummy), nullptr) == KONGOR_PARSE_ERR_NULL_PTR,
        "Null out_header must return KONGOR_PARSE_ERR_NULL_PTR");

    uint8_t isRpc = 0;
    EXPECT_TRUE(kongor_pipe_is_jsonrpc_2_0(nullptr, 2, &isRpc) == KONGOR_PARSE_ERR_NULL_PTR,
        "Null JSON input must fail closed");
    EXPECT_TRUE(kongor_s2z_check_nasty_path(nullptr, 4, 0) == KONGOR_PARSE_ERR_NULL_PTR,
        "Null VFS path must fail closed");

    KongorDdsInfo dds{};
    EXPECT_TRUE(kongor_dds_verify_mipmap_chain(nullptr, 128, &dds) == KONGOR_PARSE_ERR_NULL_PTR,
        "Null DDS buffer must fail closed");

    uint16_t seq = 0;
    EXPECT_TRUE(kongor_udp_parse_ping_probe(nullptr, 6, &seq) == KONGOR_PARSE_ERR_NULL_PTR,
        "Null UDP ping packet must fail closed");
}

static void TestPipeParsers() {
    std::printf("[TEST] [FFI-01] Pipe 'MW' 12B framing & safe JSON-RPC 2.0 extraction...\n");

    // Valid 'MW' frame: opcode=0x0003, payloadLen=4, reqId=0xCAFE0001, body="PING"
    std::vector<uint8_t> frame;
    frame.push_back(0x4D);
    frame.push_back(0x57);
    AppendU16Le(frame, 0x0003u);
    AppendU32Le(frame, 4u);
    AppendU32Le(frame, 0xCAFE0001u);
    frame.insert(frame.end(), { 'P', 'I', 'N', 'G' });

    KongorMwFrameHeader hdr{};
    EXPECT_TRUE(kongor_pipe_parse_mw_frame(frame.data(), frame.size(), &hdr) == KONGOR_PARSE_OK,
        "Valid 16-byte MW frame must parse cleanly");
    EXPECT_TRUE(hdr.opcode == 0x0003u && hdr.payload_len == 4u && hdr.req_id == 0xCAFE0001u && hdr.total_frame_len == 16u,
        "Parsed MW header fields must match exact LE wire values");

    // Truncated body
    EXPECT_TRUE(kongor_pipe_parse_mw_frame(frame.data(), frame.size() - 1, &hdr) == KONGOR_PARSE_ERR_NEED_MORE_DATA,
        "Truncated MW payload must return KONGOR_PARSE_ERR_NEED_MORE_DATA");

    // Negative payload length (-1 = 0xFFFFFFFF)
    std::vector<uint8_t> negFrame = frame;
    WriteU32Le(negFrame, 4, 0xFFFFFFFFu);
    EXPECT_TRUE(kongor_pipe_parse_mw_frame(negFrame.data(), negFrame.size(), &hdr) == KONGOR_PARSE_ERR_LENGTH_OUT_OF_RANGE,
        "Negative MW payload length must be rejected");

    // Oversized payload (> 16 MiB)
    std::vector<uint8_t> hugeFrame = frame;
    WriteU32Le(hugeFrame, 4, (16u * 1024u * 1024u) + 1u);
    EXPECT_TRUE(kongor_pipe_parse_mw_frame(hugeFrame.data(), hugeFrame.size(), &hdr) == KONGOR_PARSE_ERR_LENGTH_OUT_OF_RANGE,
        ">16 MiB MW payload length must be rejected");

    // JSON-RPC 2.0 with escaped quotes, spoofed key in string value, and nested params
    const std::string rpc =
        "{\"jsonrpc\":\"2.0\",\"note\":\"\\\"method\\\":\\\"evil\\\"\",\"id\":\"req-42\","
        "\"method\":\"execute\",\"params\":{\"command\":\"Echo \\\"hi\\\\there\\\"\"}}";
    uint8_t isRpc = 0;
    EXPECT_TRUE(
        kongor_pipe_is_jsonrpc_2_0(reinterpret_cast<const uint8_t*>(rpc.data()), rpc.size(), &isRpc) == KONGOR_PARSE_OK &&
        isRpc == 1,
        "Valid JSON-RPC 2.0 object must be recognized");

    char outBuf[256] = {};
    size_t outLen = 0;
    EXPECT_TRUE(
        kongor_pipe_extract_json_token(
            reinterpret_cast<const uint8_t*>(rpc.data()), rpc.size(),
            "method", 0, 1, outBuf, sizeof(outBuf), &outLen) == KONGOR_PARSE_OK,
        "Extracting 'method' must succeed");
    EXPECT_TRUE(std::strcmp(outBuf, "execute") == 0 && outLen == 7,
        "Real 'method' must ignore spoofed key embedded in string literal");

    EXPECT_TRUE(
        kongor_pipe_extract_json_token(
            reinterpret_cast<const uint8_t*>(rpc.data()), rpc.size(),
            "id", 1, 0, outBuf, sizeof(outBuf), &outLen) == KONGOR_PARSE_OK,
        "Extracting quoted 'id' must succeed");
    EXPECT_TRUE(std::strcmp(outBuf, "\"req-42\"") == 0,
        "preserve_quotes=1 must retain surrounding JSON quotes");

    EXPECT_TRUE(
        kongor_pipe_extract_json_token(
            reinterpret_cast<const uint8_t*>(rpc.data()), rpc.size(),
            "command", 0, 1, outBuf, sizeof(outBuf), &outLen) == KONGOR_PARSE_OK,
        "Extracting nested params.command with escapes must succeed");
    EXPECT_TRUE(std::strcmp(outBuf, "Echo \"hi\\there\"") == 0,
        "Escaped quotes & backslashes must unescape accurately");

    // Buffer too small reports needed length
    char tinyBuf[4] = {};
    size_t neededLen = 0;
    EXPECT_TRUE(
        kongor_pipe_extract_json_token(
            reinterpret_cast<const uint8_t*>(rpc.data()), rpc.size(),
            "command", 0, 1, tinyBuf, sizeof(tinyBuf), &neededLen) == KONGOR_PARSE_ERR_BUFFER_TOO_SMALL,
        "Undersized output buffer must return KONGOR_PARSE_ERR_BUFFER_TOO_SMALL");
    EXPECT_TRUE(neededLen == 15, "out_len must report exact required byte length");

    // Deep nesting bomb (> 64)
    std::string deep = "{\"a\":";
    for (int i = 0; i < 80; ++i) deep.push_back('[');
    for (int i = 0; i < 80; ++i) deep.push_back(']');
    deep.push_back('}');
    EXPECT_TRUE(
        kongor_pipe_extract_json_token(
            reinterpret_cast<const uint8_t*>(deep.data()), deep.size(),
            "a", 0, 0, outBuf, sizeof(outBuf), &outLen) == KONGOR_PARSE_ERR_NESTING_TOO_DEEP,
        "Adversarial >64 JSON nesting must return KONGOR_PARSE_ERR_NESTING_TOO_DEEP");
}

static void TestS2zParsers() {
    std::printf("[TEST] [FFI-02] S2Z VFS nasty-path firewall & ZIP/ZIP64 EOCD + CD 0x0001...\n");

    const char* goodPath = "heroes/scout/model.mdf";
    EXPECT_TRUE(
        kongor_s2z_check_nasty_path(reinterpret_cast<const uint8_t*>(goodPath), std::strlen(goodPath), 0) == KONGOR_PARSE_OK,
        "Clean VFS path must be accepted");

    const char* leadSlash = "/ui/fe2/main.interface";
    EXPECT_TRUE(
        kongor_s2z_check_nasty_path(reinterpret_cast<const uint8_t*>(leadSlash), std::strlen(leadSlash), 0) == KONGOR_PARSE_ERR_NASTY_PATH,
        "Leading slash must be rejected when allow_leading_slash=0");
    EXPECT_TRUE(
        kongor_s2z_check_nasty_path(reinterpret_cast<const uint8_t*>(leadSlash), std::strlen(leadSlash), 1) == KONGOR_PARSE_OK,
        "Leading slash must be accepted when allow_leading_slash=1");

    const char* badPaths[] = {
        "../secret.cfg",
        "heroes/../evil.dll",
        "C:/Windows/win.ini",
        "heroes\\scout\\model.mdf",
        "heroes/CON.txt",
        "heroes/COM1",
        "heroes/trailing.",
        "heroes//double.dds"
    };
    for (const char* bp : badPaths) {
        EXPECT_TRUE(
            kongor_s2z_check_nasty_path(reinterpret_cast<const uint8_t*>(bp), std::strlen(bp), 0) == KONGOR_PARSE_ERR_NASTY_PATH,
            "Adversarial VFS path must be rejected");
    }

    const uint8_t nulPath[] = { 'a', '/', 'b', '\0', 'c' };
    EXPECT_TRUE(
        kongor_s2z_check_nasty_path(nulPath, sizeof(nulPath), 0) == KONGOR_PARSE_ERR_NASTY_PATH,
        "Embedded NUL byte in VFS path must be rejected");

    // ZIP64 EOCD with locator (0x07064b50) + sentinels
    std::vector<uint8_t> z64Tail;
    AppendU32Le(z64Tail, 0x07064B50u); // ZIP64 locator sig
    AppendU32Le(z64Tail, 0u);
    AppendU64Le(z64Tail, 0x0000000112233445ULL);
    AppendU32Le(z64Tail, 1u);
    AppendU32Le(z64Tail, 0x06054B50u); // EOCD sig
    AppendU16Le(z64Tail, 0u);
    AppendU16Le(z64Tail, 0u);
    AppendU16Le(z64Tail, 0xFFFFu);
    AppendU16Le(z64Tail, 0xFFFFu);
    AppendU32Le(z64Tail, 0xFFFFFFFFu);
    AppendU32Le(z64Tail, 0xFFFFFFFFu);
    AppendU16Le(z64Tail, 0u);

    KongorZipEocdInfo eocdInfo{};
    EXPECT_TRUE(
        kongor_s2z_parse_eocd(z64Tail.data(), z64Tail.size(), 5000000000ULL, &eocdInfo) == KONGOR_PARSE_OK,
        "Valid ZIP64 tail with locator must parse");
    EXPECT_TRUE(
        eocdInfo.is_zip64 == 1 && eocdInfo.has_zip64_locator == 1 &&
        eocdInfo.requires_modern_archive == 1 &&
        eocdInfo.zip64_locator_eocd_offset == 0x0000000112233445ULL,
        "ZIP64 locator offset & modern-archive flags must match");

    // ZIP64 Central Directory entry with 0x0001 extra field
    const std::string entryName = "textures/00000000/hero.dds";
    std::vector<uint8_t> extra;
    AppendU16Le(extra, 0x0001u); // ZIP64 extra ID
    AppendU16Le(extra, 24u);     // uncomp(8) + comp(8) + offset(8)
    AppendU64Le(extra, 0x0000000200004000ULL);
    AppendU64Le(extra, 0x0000000100002000ULL);
    AppendU64Le(extra, 0x0000000300000100ULL);

    std::vector<uint8_t> cd(46, 0);
    WriteU32Le(cd, 0, 0x02014B50u);
    WriteU16Le(cd, 10, 8u); // Deflate
    WriteU32Le(cd, 20, 0xFFFFFFFFu);
    WriteU32Le(cd, 24, 0xFFFFFFFFu);
    WriteU16Le(cd, 28, static_cast<uint16_t>(entryName.size()));
    WriteU16Le(cd, 30, static_cast<uint16_t>(extra.size()));
    WriteU32Le(cd, 42, 0xFFFFFFFFu);
    cd.insert(cd.end(), entryName.begin(), entryName.end());
    cd.insert(cd.end(), extra.begin(), extra.end());

    KongorZipCdEntryInfo cdInfo{};
    EXPECT_TRUE(kongor_s2z_parse_cd_entry(cd.data(), cd.size(), &cdInfo) == KONGOR_PARSE_OK,
        "Valid ZIP64 CD entry must parse");
    EXPECT_TRUE(
        cdInfo.is_zip64 == 1 && cdInfo.path_is_safe == 1 &&
        cdInfo.uncompressed_size == 0x0000000200004000ULL &&
        cdInfo.compressed_size == 0x0000000100002000ULL &&
        cdInfo.local_header_offset == 0x0000000300000100ULL,
        "ZIP64 64-bit sizes & offset from 0x0001 extra field must match");

    // Missing ZIP64 extra field when sentinel set -> KONGOR_PARSE_ERR_ZIP64_INVALID
    std::vector<uint8_t> badCd(46, 0);
    WriteU32Le(badCd, 0, 0x02014B50u);
    WriteU32Le(badCd, 20, 0xFFFFFFFFu);
    WriteU16Le(badCd, 28, 5u);
    badCd.insert(badCd.end(), { 'a', '.', 'd', 'd', 's' });
    EXPECT_TRUE(kongor_s2z_parse_cd_entry(badCd.data(), badCd.size(), &cdInfo) == KONGOR_PARSE_ERR_ZIP64_INVALID,
        "ZIP64 CD sentinel without 0x0001 extra field must return KONGOR_PARSE_ERR_ZIP64_INVALID");
}

static void TestDdsParser() {
    std::printf("[TEST] [FFI-03] DDS / DX10 mipmap chain & cubemap verification...\n");

    // 8x8 DXT1, 4 mips -> 128 + 32 + 8 + 8 + 8 = 184 bytes
    std::vector<uint8_t> dxt1(184, 0);
    WriteU32Le(dxt1, 0, 0x20534444u); // "DDS "
    WriteU32Le(dxt1, 4, 124u);
    WriteU32Le(dxt1, 12, 8u);  // height
    WriteU32Le(dxt1, 16, 8u);  // width
    WriteU32Le(dxt1, 28, 4u);  // mipMapCount
    WriteU32Le(dxt1, 80, 0x4u);        // DDPF_FOURCC
    WriteU32Le(dxt1, 84, 0x31545844u); // "DXT1"

    KongorDdsInfo ddsInfo{};
    EXPECT_TRUE(kongor_dds_verify_mipmap_chain(dxt1.data(), dxt1.size(), &ddsInfo) == KONGOR_PARSE_OK,
        "Valid 8x8 DXT1 4-mip DDS must pass");
    EXPECT_TRUE(
        ddsInfo.expected_min_size == 184u && ddsInfo.is_block_compressed == 1 &&
        ddsInfo.block_or_pixel_bytes == 8u && ddsInfo.is_dx10 == 0,
        "DXT1 DDS metadata & expected_min_size must match");

    // Truncated by 1 byte -> KONGOR_PARSE_ERR_NEED_MORE_DATA and expected_min_size still populated
    KongorDdsInfo truncInfo{};
    EXPECT_TRUE(
        kongor_dds_verify_mipmap_chain(dxt1.data(), dxt1.size() - 1, &truncInfo) == KONGOR_PARSE_ERR_NEED_MORE_DATA,
        "183-byte truncated DXT1 DDS must return KONGOR_PARSE_ERR_NEED_MORE_DATA");
    EXPECT_TRUE(truncInfo.expected_min_size == 184u,
        "Truncated DDS must still report expected_min_size=184");

    // 16x16 DX10 BC7 (DXGI 98) cubemap, 2 mips: 148 + (256 + 64) * 6 = 2068 bytes
    std::vector<uint8_t> dx10(2068, 0);
    WriteU32Le(dx10, 0, 0x20534444u);
    WriteU32Le(dx10, 4, 124u);
    WriteU32Le(dx10, 12, 16u);
    WriteU32Le(dx10, 16, 16u);
    WriteU32Le(dx10, 28, 2u);
    WriteU32Le(dx10, 80, 0x4u);
    WriteU32Le(dx10, 84, 0x30315844u); // "DX10"
    WriteU32Le(dx10, 112, 0x00000200u); // DDSCAPS2_CUBEMAP
    WriteU32Le(dx10, 128, 98u); // DXGI_FORMAT_BC7_UNORM
    WriteU32Le(dx10, 136, 0x4u); // DDS_RESOURCE_MISC_TEXTURECUBE
    WriteU32Le(dx10, 140, 1u);   // arraySize

    KongorDdsInfo dx10Info{};
    EXPECT_TRUE(kongor_dds_verify_mipmap_chain(dx10.data(), dx10.size(), &dx10Info) == KONGOR_PARSE_OK,
        "Valid 16x16 DX10 BC7 cubemap must pass");
    EXPECT_TRUE(
        dx10Info.is_dx10 == 1 && dx10Info.is_cubemap == 1 &&
        dx10Info.header_bytes == 148u && dx10Info.dxgi_format == 98u &&
        dx10Info.block_or_pixel_bytes == 16u && dx10Info.expected_min_size == 2068u,
        "DX10 BC7 cubemap metadata must match exact byte count");
}

static void TestUdpParsers() {
    std::printf("[TEST] [FFI-04] UDP 6B K2 ping probe, loopback map: & COMPEL 40B watermark...\n");

    const uint8_t validProbe[6] = { 0x00, 0x00, 0x01, 0xCA, 0x34, 0x12 };
    uint16_t seq = 0;
    EXPECT_TRUE(kongor_udp_parse_ping_probe(validProbe, sizeof(validProbe), &seq) == KONGOR_PARSE_OK,
        "Exact 6-byte K2 ping probe must parse");
    EXPECT_TRUE(seq == 0x1234u, "Ping sequence must decode LE 0x1234");

    const uint8_t gameplay7[7] = { 0x00, 0x00, 0x01, 0xCA, 0x10, 0x20, 0x30 };
    EXPECT_TRUE(kongor_udp_parse_ping_probe(gameplay7, sizeof(gameplay7), &seq) == KONGOR_PARSE_ERR_INVALID_MAGIC,
        "7-byte gameplay UDP packet must NEVER match ping probe");

    // Loopback map: notification
    char mapBuf[64] = "sentinel";
    size_t mapLen = 0;
    const char* mapCmd = "map:caldavar\r\n";
    EXPECT_TRUE(
        kongor_udp_extract_loopback_map(
            reinterpret_cast<const uint8_t*>(mapCmd), std::strlen(mapCmd),
            0x7F000001u, mapBuf, sizeof(mapBuf), &mapLen) == KONGOR_PARSE_OK,
        "Loopback 127.0.0.1 map: update must succeed");
    EXPECT_TRUE(std::strcmp(mapBuf, "caldavar") == 0 && mapLen == 8,
        "Loopback map name must strip CRLF and equal 'caldavar'");

    char spoofBuf[64] = "unchanged";
    EXPECT_TRUE(
        kongor_udp_extract_loopback_map(
            reinterpret_cast<const uint8_t*>("map:pwned"), 9,
            0xCB00712Au, spoofBuf, sizeof(spoofBuf), &mapLen) == KONGOR_PARSE_ERR_UNAUTHORIZED_SENDER,
        "External IPv4 map: spoof must return KONGOR_PARSE_ERR_UNAUTHORIZED_SENDER");
    EXPECT_TRUE(std::strcmp(spoofBuf, "unchanged") == 0,
        "Rejected external map: packet must NOT mutate caller output buffer");

    // COMPEL 40-byte watermark
    std::vector<uint8_t> wm(43, 0x5Au);
    WriteU32Le(wm, 28, 0x12345678u);
    WriteU16Le(wm, 32, 0x00ABu);
    KongorCompelWatermarkInfo wmInfo{};
    EXPECT_TRUE(
        kongor_udp_verify_compel_watermark(wm.data(), wm.size(), 0, &wmInfo) == KONGOR_PARSE_OK,
        "Valid 43-byte game COMPEL watermark must pass");
    EXPECT_TRUE(
        wmInfo.challenge == 0x12345678u && wmInfo.counter == 0x00ABu &&
        wmInfo.is_voice == 0 && wmInfo.payload_len == 43u && wmInfo.inner_offset == 40u,
        "Parsed COMPEL watermark fields must match exact offsets");

    EXPECT_TRUE(
        kongor_udp_verify_compel_watermark(wm.data(), 42, 0, &wmInfo) == KONGOR_PARSE_ERR_NEED_MORE_DATA,
        "42-byte game COMPEL packet must be rejected as too short");
    EXPECT_TRUE(
        kongor_udp_verify_compel_watermark(wm.data(), 41, 1, &wmInfo) == KONGOR_PARSE_OK,
        "41-byte voice COMPEL packet must pass");

    std::vector<uint8_t> zeros(43, 0);
    EXPECT_TRUE(
        kongor_udp_verify_compel_watermark(zeros.data(), zeros.size(), 0, &wmInfo) == KONGOR_PARSE_ERR_MALFORMED,
        "All-zero COMPEL challenge/counter sentinel must be rejected");

    std::vector<uint8_t> ones(43, 0xFFu);
    EXPECT_TRUE(
        kongor_udp_verify_compel_watermark(ones.data(), ones.size(), 0, &wmInfo) == KONGOR_PARSE_ERR_MALFORMED,
        "All-ones COMPEL challenge/counter sentinel must be rejected");
}

int main() {
    std::printf("==================================================================\n");
    std::printf("  Project Kongor: C++17 <-> Rust Staticlib FFI Verification Suite\n");
    std::printf("==================================================================\n");

    TestAbiAndNullGuards();
    TestPipeParsers();
    TestS2zParsers();
    TestDdsParser();
    TestUdpParsers();

    if (g_failures > 0) {
        std::fprintf(stderr, "[SUMMARY] FAILED: %d/%d assertions failed.\n", g_failures, g_assertions);
        return 1;
    }

    std::printf("[SUMMARY] PASSED: %d/%d cross-language FFI assertions passed (0 failures).\n",
        g_assertions, g_assertions);
    return 0;
}
