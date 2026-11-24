// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file test_cas_and_metagen.cpp
 * @brief Cross-Platform SHA-256, CAS Zlib/Merkle Interop, & Metagen Seam Tests
 *
 * Test Value Gate:
 * - protects: FIPS 180-4 SHA-256 correctness, RFC 1950 zlib chunk framing for Python
 *   .kpatch compatibility, case-normalized Merkle roots, CAS corruption detection,
 *   and kongor-metagen RFC 8259 \uXXXX decoding + cycle/CLI validation.
 * - fails_when: Raw deflate is emitted without RFC 1950 zlib header, Merkle vpaths are
 *   not lowercased before hashing, corrupt CAS objects pass verify, or \uXXXX / self-cycles
 *   are mishandled in kongor-metagen.
 * - seam: Real compiled kongor-cas & kongor-metagen executables over real temp directories.
 */

#include "../include/kongor_sha256.h"

#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <process.h>
#else
#include <unistd.h>
#include <sys/wait.h>
#endif

namespace fs = std::filesystem;

#define ASSERT_TRUE(cond) do { \
    if (!(cond)) { \
        std::fprintf(stderr, "[FAIL] %s:%d: Assertion failed: %s\n", __FILE__, __LINE__, #cond); \
        std::exit(1); \
    } \
} while(0)

static int RunCliProcess(const fs::path& exePath, const std::vector<std::string>& args) {
#ifdef _WIN32
    std::wstring cmd = L"\"\"" + exePath.wstring() + L"\"";
    for (const auto& a : args) {
        std::wstring wa = fs::u8path(a).wstring();
        cmd += L" \"" + wa + L"\"";
    }
    cmd += L"\"";
    int rc = _wsystem(cmd.c_str());
    return rc;
#else
    std::string cmd = "\"" + exePath.string() + "\"";
    for (const auto& a : args) {
        cmd += " \"" + a + "\"";
    }
    int status = std::system(cmd.c_str());
    if (status == -1) return -1;
    if (WIFEXITED(status)) return WEXITSTATUS(status);
    return status;
#endif
}

static std::string ReadTextFile(const fs::path& p) {
    std::ifstream in(p, std::ios::binary);
    ASSERT_TRUE(in.is_open());
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

static std::vector<uint8_t> ReadBinaryFile(const fs::path& p) {
    std::ifstream in(p, std::ios::binary);
    ASSERT_TRUE(in.is_open());
    return std::vector<uint8_t>((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

static void WriteBinaryFile(const fs::path& p, const void* data, size_t len) {
    if (p.has_parent_path()) {
        fs::create_directories(p.parent_path());
    }
    std::ofstream out(p, std::ios::binary | std::ios::trunc);
    ASSERT_TRUE(out.is_open());
    if (len > 0) {
        out.write(reinterpret_cast<const char*>(data), static_cast<std::streamsize>(len));
    }
    out.flush();
    ASSERT_TRUE(out.good());
}

static void TestSha256NistAndParity() {
    std::printf("[TEST] TestSha256NistAndParity...\n");

    // 1. Empty string ""
    std::string emptyHex = KongorPortableSha256::HashBuffer("", 0);
    ASSERT_TRUE(emptyHex == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    ASSERT_TRUE(KongorSha256::HashBuffer("", 0) == emptyHex);

    // 2. "abc"
    std::string abcHex = KongorPortableSha256::HashBuffer("abc", 3);
    ASSERT_TRUE(abcHex == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    ASSERT_TRUE(KongorSha256::HashBuffer("abc", 3) == abcHex);

    // 3. 56-byte NIST multi-block boundary vector (448 bits -> forces padding into second block)
    const char* vec56 = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
    std::string hex56 = KongorPortableSha256::HashBuffer(vec56, std::strlen(vec56));
    ASSERT_TRUE(hex56 == "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1");
    ASSERT_TRUE(KongorSha256::HashBuffer(vec56, std::strlen(vec56)) == hex56);

    // 4. Streaming 1,000,000 'a's NIST vector across uneven chunk boundaries
    KongorPortableSha256 streamPortable;
    KongorSha256 streamPlatform;
    std::vector<char> chunkA(1000, 'a');
    for (int i = 0; i < 1000; ++i) {
        ASSERT_TRUE(streamPortable.Update(chunkA.data(), chunkA.size()));
        ASSERT_TRUE(streamPlatform.Update(chunkA.data(), chunkA.size()));
    }
    std::string millionHex = streamPortable.FinalizeHex();
    ASSERT_TRUE(millionHex == "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");
    ASSERT_TRUE(streamPlatform.FinalizeHex() == millionHex);

    std::printf("  -> PASSED\n");
}

static void TestKongorCasZlibAndMerkleSeam(const fs::path& casExe) {
    std::printf("[TEST] TestKongorCasZlibAndMerkleSeam (%s)...\n", casExe.string().c_str());
    ASSERT_TRUE(fs::exists(casExe));

    fs::path sandbox = fs::temp_directory_path() / ("kongor_cas_test_" + std::to_string(
#ifdef _WIN32
        _getpid()
#else
        getpid()
#endif
    ));
    std::error_code ec;
    fs::remove_all(sandbox, ec);
    fs::create_directories(sandbox);

    fs::path srcDir = sandbox / "assets";
    fs::path casDir = sandbox / ".cas";
    fs::path manifestJson = sandbox / "manifest_v1.json";

    // Create 3 files:
    // 1) Mixed-case path (<1 MB) to test Merkle lowercase vpath normalization + RFC 1950 zlib wrapper
    const std::string smallText = "Project Kongor cross-platform CAS payload\n";
    WriteBinaryFile(srcDir / "UI" / "Hero_Pyromancer.xml", smallText.data(), smallText.size());

    // 2) Multi-chunk >1 MB file (1,200,000 bytes) to test streaming 1 MB slicer
    std::vector<uint8_t> bigPayload(1200000);
    for (size_t i = 0; i < bigPayload.size(); ++i) {
        bigPayload[i] = static_cast<uint8_t>((i * 131u + 17u) & 0xFFu);
    }
    WriteBinaryFile(srcDir / "textures" / "atlas.bin", bigPayload.data(), bigPayload.size());

    // 3) 0-byte empty file edge case
    WriteBinaryFile(srcDir / "core" / "empty.marker", "", 0);

    // Run `kongor-cas manifest` (ingests all chunks + emits manifest_v1.json)
    int rcManifest = RunCliProcess(casExe, {
        "manifest", "v1.0.0", srcDir.string(),
        "--output", manifestJson.string(),
        "--cas-dir", casDir.string()
    });
    ASSERT_TRUE(rcManifest == 0);
    ASSERT_TRUE(fs::exists(manifestJson));

    // Verify small chunk on disk carries valid RFC 1950 zlib 2-byte header (CMF=0x78, (CMF*256+FLG)%31 == 0)
    std::string smallHash = KongorPortableSha256::HashBuffer(smallText.data(), smallText.size());
    fs::path smallObjPath = casDir / "objects" / smallHash.substr(0, 2) / smallHash.substr(2);
    ASSERT_TRUE(fs::exists(smallObjPath));
    std::vector<uint8_t> compressedSmall = ReadBinaryFile(smallObjPath);
    ASSERT_TRUE(compressedSmall.size() >= 6);
    ASSERT_TRUE(compressedSmall[0] == 0x78u);
    uint16_t zlibHdr = (static_cast<uint16_t>(compressedSmall[0]) << 8) | compressedSmall[1];
    ASSERT_TRUE((zlibHdr % 31u) == 0u);

    // Verify exact canonical Merkle root matches Python cas.py specification:
    // sorted lowercase normalized vpaths:
    //   1. core/empty.marker
    //   2. textures/atlas.bin
    //   3. ui/hero_pyromancer.xml  (NOT UI/Hero_Pyromancer.xml!)
    std::string emptyHash = KongorPortableSha256::HashBuffer("", 0);
    std::string bigHash = KongorPortableSha256::HashBuffer(bigPayload.data(), bigPayload.size());
    std::string merkleStream =
        "core/empty.marker:" + emptyHash + "\n" +
        "textures/atlas.bin:" + bigHash + "\n" +
        "ui/hero_pyromancer.xml:" + smallHash + "\n";
    std::string expectedMerkleRoot = KongorPortableSha256::HashBuffer(merkleStream.data(), merkleStream.size());

    std::string manifestText = ReadTextFile(manifestJson);
    ASSERT_TRUE(manifestText.find("\"merkle_root\": \"" + expectedMerkleRoot + "\"") != std::string::npos);

    // Run `kongor-cas verify` -> must pass (0)
    int rcVerifyOk = RunCliProcess(casExe, { "verify", casDir.string() });
    ASSERT_TRUE(rcVerifyOk == 0);

    // Tamper 1 byte in a stored CAS chunk -> `kongor-cas verify` MUST detect corruption and return non-zero!
    compressedSmall[compressedSmall.size() / 2] ^= 0xFFu;
    WriteBinaryFile(smallObjPath, compressedSmall.data(), compressedSmall.size());
    int rcVerifyTampered = RunCliProcess(casExe, { "verify", casDir.string() });
    ASSERT_TRUE(rcVerifyTampered != 0);

    // Non-ASCII UTF-8 filename (e.g. "É.tga" = \xC3\x89.tga) MUST fail closed during `manifest`
    // rather than mangling via Windows CP_ACP or diverging on Unicode case-folding vs Python!
    fs::path nonAsciiDir = sandbox / "assets_non_ascii";
    fs::path nonAsciiFile = nonAsciiDir / fs::u8path("UI/\xC3\x89.tga");
    WriteBinaryFile(nonAsciiFile, smallText.data(), smallText.size());
    int rcNonAscii = RunCliProcess(casExe, {
        "manifest", "v1.0.1", nonAsciiDir.string(),
        "--output", (sandbox / "manifest_bad.json").string(),
        "--cas-dir", (sandbox / ".cas_bad").string()
    });
    ASSERT_TRUE(rcNonAscii != 0);

    fs::remove_all(sandbox, ec);
    std::printf("  -> PASSED\n");
}

static void TestKongorMetagenUnicodeAndSafetySeam(const fs::path& metagenExe) {
    std::printf("[TEST] TestKongorMetagenUnicodeAndSafetySeam (%s)...\n", metagenExe.string().c_str());
    ASSERT_TRUE(fs::exists(metagenExe));

    fs::path sandbox = fs::temp_directory_path() / ("kongor_metagen_test_" + std::to_string(
#ifdef _WIN32
        _getpid()
#else
        getpid()
#endif
    ));
    std::error_code ec;
    fs::remove_all(sandbox, ec);
    fs::create_directories(sandbox);

    // 1. Valid schema with \u0027 ('), \u00E9 (2-byte UTF-8), \u2603 (3-byte UTF-8), \uD83D\uDE80 (4-byte surrogate pair)
    const std::string validSchema = R"({
  "title": "Unicode Test",
  "version": "1.0.0",
  "constants": [
    { "name": "TEST_MAGIC", "type": "uint32", "value": "0x1234", "description": "ASCII \u0027KONG\u0027 caf\u00E9 \u2603 \uD83D\uDE80" }
  ],
  "enums": [],
  "structs": [
    {
      "name": "TestPacket",
      "expected_size": 8,
      "description": "Packet\u0027s header",
      "fields": [
        { "name": "Id", "type": "uint32", "description": "Field A\/B" },
        { "name": "Crc", "type": "uint32", "description": "CRC32" }
      ]
    }
  ]
})";
    fs::path schemaPath = sandbox / "schema_unicode.json";
    fs::path cppOut = sandbox / "out.h";
    fs::path csOut  = sandbox / "out.cs";
    fs::path luaOut = sandbox / "out.lua";
    WriteBinaryFile(schemaPath, validSchema.data(), validSchema.size());

    int rcOk = RunCliProcess(metagenExe, {
        "--schema", schemaPath.string(),
        "--cpp-out", cppOut.string(),
        "--cs-out", csOut.string(),
        "--lua-out", luaOut.string()
    });
    ASSERT_TRUE(rcOk == 0);

    std::string cppText = ReadTextFile(cppOut);
    // Verify \u0027 decoded to literal apostrophe ' and \/ decoded to /
    ASSERT_TRUE(cppText.find("ASCII 'KONG' caf\xC3\xA9 \xE2\x98\x83 \xF0\x9F\x9A\x80") != std::string::npos);
    ASSERT_TRUE(cppText.find("Packet's header") != std::string::npos);
    ASSERT_TRUE(cppText.find("Field A/B") != std::string::npos);
    ASSERT_TRUE(cppText.find("u0027") == std::string::npos);

    // 2. Self-referential struct cycle (Node -> Node) MUST fail with non-zero exit code
    const std::string selfCycleSchema = R"({
  "title": "Self Cycle",
  "version": "1.0.0",
  "structs": [
    {
      "name": "SelfRefNode",
      "expected_size": 0,
      "fields": [
        { "name": "Next", "type": "SelfRefNode" }
      ]
    }
  ]
})";
    fs::path cyclePath = sandbox / "schema_self_cycle.json";
    WriteBinaryFile(cyclePath, selfCycleSchema.data(), selfCycleSchema.size());
    int rcCycle = RunCliProcess(metagenExe, {
        "--schema", cyclePath.string(),
        "--cpp-out", cppOut.string(),
        "--cs-out", csOut.string(),
        "--lua-out", luaOut.string()
    });
    ASSERT_TRUE(rcCycle != 0);

    // 3. Unknown CLI flag MUST fail with non-zero exit code
    int rcBadFlag = RunCliProcess(metagenExe, { "--unknown-flag" });
    ASSERT_TRUE(rcBadFlag != 0);

    // 4. Truncated / unclosed string JSON MUST fail with non-zero exit code
    const std::string badJson = "{\"title\": \"unterminated";
    fs::path badJsonPath = sandbox / "schema_bad.json";
    WriteBinaryFile(badJsonPath, badJson.data(), badJson.size());
    int rcBadJson = RunCliProcess(metagenExe, {
        "--schema", badJsonPath.string(),
        "--cpp-out", cppOut.string(),
        "--cs-out", csOut.string(),
        "--lua-out", luaOut.string()
    });
    ASSERT_TRUE(rcBadJson != 0);

    fs::remove_all(sandbox, ec);
    std::printf("  -> PASSED\n");
}

int main(int argc, char** argv) {
    std::printf("========================================================\n");
    std::printf("  Kongor Cross-Platform CAS & Metagen Seam Test Suite\n");
    std::printf("========================================================\n");

    TestSha256NistAndParity();

    if (argc >= 3) {
        fs::path casExe = fs::u8path(argv[1]);
        fs::path metagenExe = fs::u8path(argv[2]);
        if (fs::exists(casExe) && fs::exists(metagenExe)) {
            TestKongorCasZlibAndMerkleSeam(casExe);
            TestKongorMetagenUnicodeAndSafetySeam(metagenExe);
        } else {
            std::printf("[SKIP] CLI executables not found at '%s' / '%s' (standalone header-only mode)\n",
                argv[1], argv[2]);
        }
    }

    std::printf("========================================================\n");
    std::printf("  ALL CAS & METAGEN TESTS PASSED!\n");
    std::printf("========================================================\n");
    return 0;
}
