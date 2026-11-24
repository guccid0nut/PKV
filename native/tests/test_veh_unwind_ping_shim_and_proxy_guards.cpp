// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Wave 4 Part B Native Hardening Unit & Seam Tests
//
// Test Value Bar:
// 1. protects:
//    - [L2-P0-01] VEH x64 instruction length decoder, control-flow refusal, and per-RIP retry cap
//    - [L2-P0-03] hon_ping_shim exact 6-byte probe filter & 127.0.0.1 loopback map: guard
//    - [L2-P0-06] FrameCapturer TwoLevelPageBitmaskTracker per-slot dirty page range clearing
//    - [L2-P1-01] Fail-closed kongor_ssl / kongor_crypto stubs, binary-safe curl realloc, zlib uncompress2 *sourceLen
//    - [L2-P1-04] Constant-time KONGOR_IPC_TOKEN verification
// 2. fails_when:
//    - VEH guesses wrong REX instruction length, skips RET/CALL/JMP, or allows >3 fault loops at same RIP
//    - hon_ping_shim swallows >6-byte gameplay UDP packets or accepts external spoofed map: updates
//    - Draining CaptureSlot 0 wipes CaptureSlot 1's dirty page bits
//    - SSL/SHA proxies fail open (returning 0 / uninitialized digest), curl realloc truncates at '\0', or uncompress2 leaves *sourceLen unupdated
//    - IPC AUTH accepts mismatched/empty token when KONGOR_IPC_TOKEN is configured
// 3. seam:
//    - Real compiled Release DLL exports (version.dll, zlibwapi_x64.dll, kongor_ssl_x64.dll,
//      kongor_crypto_x64.dll, libcurl_x64.dll) + shared production headers (hon_ping_protocol.h, page_bitmask_tracker.h)
// ============================================================================

#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "../proxies/hon_ping_shim/hon_ping_protocol.h"
#include "../vulkan-layer/page_bitmask_tracker.h"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include "../proxies/native_guard/kongor_sstream.h"
#endif

static int g_failures = 0;

#define EXPECT_TRUE(cond, msg)                                                                  \
    do {                                                                                        \
        if (!(cond)) {                                                                          \
            std::fprintf(stderr, "[FAIL] %s:%d: %s (%s)\n"                                      \
                                 "  -> Fix/Diagnose: Run scripts/Invoke-KongorTestLock.ps1 "    \
                                 "-Owner test -- ctest --test-dir native/build -C Release -R "  \
                                 "test_veh_unwind_ping_shim_and_proxy_guards --output-on-failure\n",                 \
                         __FILE__, __LINE__, (msg), #cond);                                     \
            ++g_failures;                                                                       \
        }                                                                                       \
    } while (0)

static void TestHonPingProtocol() {
    std::printf("[TEST] [L2-P0-03] hon_ping_protocol exact 6-byte probe & loopback map: guard...\n");

    const unsigned char validProbe[6] = { 0x00, 0x00, 0x01, 0xCA, 0x34, 0x12 };
    EXPECT_TRUE(hon_ping_is_exact_6byte_probe(validProbe, 6) == 1, "Exact 6-byte 00 00 01 CA probe must match");
    EXPECT_TRUE(hon_ping_is_exact_6byte_probe(validProbe, 5) == 0, "Truncated 5-byte packet must not match");
    EXPECT_TRUE(hon_ping_is_exact_6byte_probe(nullptr, 6) == 0, "Null packet pointer must not match");

    const unsigned char wrongHeader[6] = { 0x01, 0x00, 0x01, 0xCA, 0x34, 0x12 };
    EXPECT_TRUE(hon_ping_is_exact_6byte_probe(wrongHeader, 6) == 0, "Non-zero prefix bytes [0..1] must not match");

    unsigned char gameplayPacket7[7] = { 0x00, 0x00, 0x01, 0xCA, 0x10, 0x20, 0x30 };
    EXPECT_TRUE(hon_ping_is_exact_6byte_probe(gameplayPacket7, 7) == 0, "7-byte gameplay UDP packet must NEVER be swallowed");

    unsigned char gameplayPacket48[48] = {};
    gameplayPacket48[2] = 0x01;
    gameplayPacket48[3] = 0xCA;
    EXPECT_TRUE(hon_ping_is_exact_6byte_probe(gameplayPacket48, 48) == 0, "48-byte gameplay UDP packet with 01 CA at [2..3] must NEVER be swallowed");

    char mapBuf[64] = {};
    const char* mapCmd = "map:caldavar\r\n";
    EXPECT_TRUE(
        hon_ping_extract_loopback_map_update(
            reinterpret_cast<const unsigned char*>(mapCmd),
            static_cast<hon_ping_ssize_t>(std::strlen(mapCmd)),
            HON_PING_LOOPBACK_IPV4_HOST,
            mapBuf,
            sizeof(mapBuf)) == 1,
        "Loopback 127.0.0.1 map: update must succeed");
    EXPECT_TRUE(std::strcmp(mapBuf, "caldavar") == 0, "Extracted map name must strip CRLF and equal 'caldavar'");

    char spoofBuf[64] = "unchanged";
    EXPECT_TRUE(
        hon_ping_extract_loopback_map_update(
            reinterpret_cast<const unsigned char*>("map:pwned"),
            9,
            0xCB00712AU /* 203.0.113.42 */,
            spoofBuf,
            sizeof(spoofBuf)) == 0,
        "External IPv4 map: packet must be rejected");
    EXPECT_TRUE(std::strcmp(spoofBuf, "unchanged") == 0, "Rejected external map: packet must not mutate output buffer");

    EXPECT_TRUE(
        hon_ping_extract_loopback_map_update(
            reinterpret_cast<const unsigned char*>("map:pwned"),
            9,
            0x0A000005U /* 10.0.0.5 */,
            spoofBuf,
            sizeof(spoofBuf)) == 0,
        "LAN non-loopback IPv4 map: packet must be rejected");
}

static void TestPerSlotPageBitmaskTracker() {
    std::printf("[TEST] [L2-P0-06] FrameCapturer TwoLevelPageBitmaskTracker per-slot isolation...\n");

    // Case 1: 1920x1080 BGRA (8,294,400 bytes = 2025 pages)
    {
        const size_t frameBytes = 1920ULL * 1080ULL * 4ULL;
        const size_t slotStride = hon_capture::AlignCaptureSlotStride4KB(frameBytes);
        hon_capture::TwoLevelPageBitmaskTracker tracker;
        tracker.Initialize(slotStride * 2ULL);

        tracker.MarkRegionGpuModified(0, frameBytes);
        tracker.MarkRegionGpuModified(slotStride, frameBytes);

        size_t slot0Pages = tracker.CountDirtyGpuPagesInRangeAndClear(0, frameBytes, true);
        EXPECT_TRUE(slot0Pages == 2025, "Slot 0 must report all 2025 dirty pages on 1080p frame");
        EXPECT_TRUE(!tracker.IsRegionGpuModified(0, frameBytes), "Slot 0 must be clean after range drain");
        EXPECT_TRUE(tracker.IsRegionGpuModified(slotStride, frameBytes), "Slot 1 dirty bits must survive Slot 0 drain");

        size_t slot1Pages = tracker.CountDirtyGpuPagesInRangeAndClear(slotStride, frameBytes, true);
        EXPECT_TRUE(slot1Pages == 2025, "Slot 1 must still report all 2025 dirty pages when its fence completes");
        EXPECT_TRUE(!tracker.IsRegionGpuModified(slotStride, frameBytes), "Slot 1 must be clean after its own range drain");
    }

    // Case 2: 800x600 BGRA (1,920,000 bytes = 468.75 pages -> non-4KB-multiple!)
    {
        const size_t frameBytes = 800ULL * 600ULL * 4ULL;
        const size_t slotStride = hon_capture::AlignCaptureSlotStride4KB(frameBytes);
        EXPECT_TRUE((slotStride % 4096ULL) == 0ULL && slotStride >= frameBytes, "Slot stride must be 4KB page-aligned");

        hon_capture::TwoLevelPageBitmaskTracker tracker;
        tracker.Initialize(slotStride * 2ULL);

        tracker.MarkRegionGpuModified(0, frameBytes);
        tracker.MarkRegionGpuModified(slotStride, frameBytes);

        size_t slot0Pages = tracker.CountDirtyGpuPagesInRangeAndClear(0, frameBytes, true);
        EXPECT_TRUE(slot0Pages == 469, "Slot 0 must report 469 dirty 4KB pages for 800x600 frame");
        EXPECT_TRUE(!tracker.IsRegionGpuModified(0, frameBytes), "Slot 0 must be cleared without touching Slot 1");

        size_t slot1Pages = tracker.CountDirtyGpuPagesInRangeAndClear(slotStride, frameBytes, true);
        EXPECT_TRUE(slot1Pages == 469, "Slot 1 must retain all 469 dirty pages even when frameBytes is not a 4KB multiple");
    }
}

#ifdef _WIN32
static std::string GetExecutableDirectory() {
    char path[MAX_PATH] = {};
    DWORD n = GetModuleFileNameA(nullptr, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return ".";
    std::string s(path, n);
    size_t pos = s.find_last_of("\\/");
    return (pos == std::string::npos) ? "." : s.substr(0, pos);
}

static HMODULE LoadSiblingBuildDll(const char* relativeSubdir, const char* dllName) {
    std::string exeDir = GetExecutableDirectory();
    // In multi-config MSVC builds, test_veh_unwind_ping_shim_and_proxy_guards.exe is at:
    //   native/build/tests/Release/test_veh_unwind_ping_shim_and_proxy_guards.exe
    // while sibling targets are at:
    //   native/build/<relativeSubdir>/Release/<dllName>
    std::vector<std::string> candidates = {
        exeDir + "\\..\\..\\" + relativeSubdir + "\\Release\\" + dllName,
        exeDir + "\\..\\" + relativeSubdir + "\\" + dllName,
        exeDir + "\\" + dllName
    };
    for (const std::string& cand : candidates) {
        HMODULE h = LoadLibraryExA(cand.c_str(), nullptr, 0);
        if (h != nullptr) {
            return h;
        }
    }
    return nullptr;
}

static void TestWindowsNativeGuardAndProxies() {
    SetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", "1");

    // 1. Test version.dll (kongor_native_guard) VEH & IPC exports
    std::printf("[TEST] [L2-P0-01 / L2-P1-04] NativeGuard x64 decoder, VEH retry cap & constant-time IPC auth...\n");
    HMODULE hGuard = LoadSiblingBuildDll("native_guard_build", "version.dll");
    EXPECT_TRUE(hGuard != nullptr, "Failed to load built version.dll from native/build/proxies/native_guard/Release");
    if (hGuard != nullptr) {
        using PFN_DecodeInsn = uint32_t(__cdecl*)(const uint8_t*, uint32_t);
        using PFN_TestBudget = int(__cdecl*)(uint32_t, uint64_t);
        using PFN_ResetBudget = void(__cdecl*)();
        using PFN_VerifyToken = int(__cdecl*)(const char*, uint32_t, const char*, uint32_t);

        auto pfnDecode = reinterpret_cast<PFN_DecodeInsn>(GetProcAddress(hGuard, "Kongor_NativeGuard_DecodeInsnLength64"));
        auto pfnBudget = reinterpret_cast<PFN_TestBudget>(GetProcAddress(hGuard, "Kongor_NativeGuard_TestVehRecoveryBudget"));
        auto pfnResetBudget = reinterpret_cast<PFN_ResetBudget>(GetProcAddress(hGuard, "Kongor_NativeGuard_ResetVehRecoveryBudget"));
        auto pfnVerifyToken = reinterpret_cast<PFN_VerifyToken>(GetProcAddress(hGuard, "Kongor_NativeGuard_VerifyIpcToken"));

        EXPECT_TRUE(pfnDecode != nullptr, "Export Kongor_NativeGuard_DecodeInsnLength64 must exist");
        EXPECT_TRUE(pfnBudget != nullptr && pfnResetBudget != nullptr, "VEH recovery budget exports must exist");
        EXPECT_TRUE(pfnVerifyToken != nullptr, "Export Kongor_NativeGuard_VerifyIpcToken must exist");

        if (pfnDecode) {
            const uint8_t insnMovSib[4] = { 0x48, 0x8B, 0x04, 0xD1 }; // mov rax, [rcx+rdx*8]
            const uint8_t insnMovSibDisp32[8] = { 0x4C, 0x8B, 0x84, 0xD1, 0x80, 0x01, 0x00, 0x00 }; // mov r8, [rcx+rdx*8+180h]
            const uint8_t insnMovups[3] = { 0x0F, 0x10, 0x00 }; // movups xmm0, [rax]
            const uint8_t insnMovss[5] = { 0xF3, 0x0F, 0x10, 0x48, 0x10 }; // movss xmm1, [rax+10h]
            const uint8_t insnVex2[4] = { 0xC5, 0xF8, 0x10, 0x00 }; // vmovups xmm0, [rax]
            const uint8_t insnVex3[5] = { 0xC4, 0xE1, 0x78, 0x10, 0x00 }; // vmovups xmm0, [rax]
            const uint8_t insnRet[1] = { 0xC3 };
            const uint8_t insnCallRel[5] = { 0xE8, 0x11, 0x22, 0x33, 0x44 };
            const uint8_t insnJmpRel[2] = { 0xEB, 0x08 };
            const uint8_t insnCallInd[6] = { 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00 }; // call [rip+0]

            EXPECT_TRUE(pfnDecode(insnMovSib, sizeof(insnMovSib)) == 4, "REX.W mov rax,[rcx+rdx*8] must decode as 4 bytes");
            EXPECT_TRUE(pfnDecode(insnMovSibDisp32, sizeof(insnMovSibDisp32)) == 8, "REX.WR mov r8,[rcx+rdx*8+180h] must decode as 8 bytes");
            EXPECT_TRUE(pfnDecode(insnMovups, sizeof(insnMovups)) == 3, "SSE movups xmm0,[rax] must decode as 3 bytes");
            EXPECT_TRUE(pfnDecode(insnMovss, sizeof(insnMovss)) == 5, "F3 0F 10 48 10 movss must decode as 5 bytes");
            EXPECT_TRUE(pfnDecode(insnVex2, sizeof(insnVex2)) == 4, "2-byte VEX vmovups must decode as 4 bytes");
            EXPECT_TRUE(pfnDecode(insnVex3, sizeof(insnVex3)) == 5, "3-byte VEX vmovups must decode as 5 bytes");
            EXPECT_TRUE(pfnDecode(insnRet, sizeof(insnRet)) == 0, "Control-flow RET must return 0 (never skipped)");
            EXPECT_TRUE(pfnDecode(insnCallRel, sizeof(insnCallRel)) == 0, "Control-flow CALL rel32 must return 0");
            EXPECT_TRUE(pfnDecode(insnJmpRel, sizeof(insnJmpRel)) == 0, "Control-flow JMP rel8 must return 0");
            EXPECT_TRUE(pfnDecode(insnCallInd, sizeof(insnCallInd)) == 0, "Indirect CALL FF /2 must return 0");
        }

        if (pfnBudget && pfnResetBudget) {
            pfnResetBudget();
            const uint64_t kRipA = 0x7FF610002340ULL;
            const uint64_t kRipB = 0x7FF610009990ULL;
            EXPECT_TRUE(pfnBudget(9001, kRipA) == 1, "Retry #1 at RIP A must be allowed");
            EXPECT_TRUE(pfnBudget(9001, kRipA) == 1, "Retry #2 at RIP A must be allowed");
            EXPECT_TRUE(pfnBudget(9001, kRipA) == 1, "Retry #3 at RIP A must be allowed");
            EXPECT_TRUE(pfnBudget(9001, kRipA) == 0, "Retry #4 at same (TID, RIP A) must fail closed (0)");
            EXPECT_TRUE(pfnBudget(9001, kRipB) == 1, "Distinct RIP B must have independent retry budget");
            pfnResetBudget();
        }

        if (pfnVerifyToken) {
            const char* secret = "k0ng0r-wave4b-ipc-secret-99";
            const uint32_t secretLen = static_cast<uint32_t>(std::strlen(secret));

            EXPECT_TRUE(pfnVerifyToken("", 0, "", 0) == 1, "Unset KONGOR_IPC_TOKEN must remain compatible (allow)");
            EXPECT_TRUE(pfnVerifyToken("", 0, secret, secretLen) == 0, "Empty payload must fail when KONGOR_IPC_TOKEN is set");
            EXPECT_TRUE(pfnVerifyToken("k0ng0r", 6, secret, secretLen) == 0, "Prefix token must fail");
            EXPECT_TRUE(pfnVerifyToken("k0ng0r-wave4b-ipc-secret-98", secretLen, secret, secretLen) == 0, "Single-char mismatch must fail");
            EXPECT_TRUE(pfnVerifyToken(secret, secretLen, secret, secretLen) == 1, "Exact raw token must succeed");

            const char* jsonGood = "{\"token\":\"k0ng0r-wave4b-ipc-secret-99\"}";
            const char* jsonBad  = "{\"token\":\"k0ng0r-wave4b-ipc-secret-00\"}";
            EXPECT_TRUE(pfnVerifyToken(jsonGood, static_cast<uint32_t>(std::strlen(jsonGood)), secret, secretLen) == 1, "JSON token envelope must succeed on exact match");
            EXPECT_TRUE(pfnVerifyToken(jsonBad, static_cast<uint32_t>(std::strlen(jsonBad)), secret, secretLen) == 0, "JSON token envelope must fail on mismatch");
        }
    }

    // 2. Test kongor_ssl_x64.dll & kongor_crypto_x64.dll fail-closed behavior
    std::printf("[TEST] [L2-P1-01] kongor_ssl & kongor_crypto fail-closed verification...\n");
    SetEnvironmentVariableA("KONGOR_PROXY_FORCE_NO_BACKEND", "1");
    HMODULE hSsl = LoadSiblingBuildDll("kongor_ssl_build", "kongor_ssl_x64.dll");
    EXPECT_TRUE(hSsl != nullptr, "Failed to load kongor_ssl_x64.dll");
    if (hSsl != nullptr) {
        using PFN_SSL_get_verify_result = long(__cdecl*)(const void*);
        auto pfnVerify = reinterpret_cast<PFN_SSL_get_verify_result>(GetProcAddress(hSsl, "SSL_get_verify_result"));
        EXPECT_TRUE(pfnVerify != nullptr, "SSL_get_verify_result export must exist");
        if (pfnVerify) {
            long verifyRes = pfnVerify(nullptr);
            EXPECT_TRUE(verifyRes != 0L, "SSL_get_verify_result MUST fail closed (non-zero != X509_V_OK) when real OpenSSL is missing");
        }
    }

    HMODULE hCrypto = LoadSiblingBuildDll("kongor_crypto_build", "kongor_crypto_x64.dll");
    EXPECT_TRUE(hCrypto != nullptr, "Failed to load kongor_crypto_x64.dll");
    if (hCrypto != nullptr) {
        using PFN_SHA256 = unsigned char*(__cdecl*)(const unsigned char*, size_t, unsigned char*);
        using PFN_SHA256_Init = int(__cdecl*)(void*);
        auto pfnSha256 = reinterpret_cast<PFN_SHA256>(GetProcAddress(hCrypto, "SHA256"));
        auto pfnSha256Init = reinterpret_cast<PFN_SHA256_Init>(GetProcAddress(hCrypto, "SHA256_Init"));
        EXPECT_TRUE(pfnSha256 != nullptr && pfnSha256Init != nullptr, "SHA256 exports must exist");
        if (pfnSha256) {
            unsigned char md[32];
            std::memset(md, 0xCC, sizeof(md));
            const unsigned char msg[4] = { 't', 'e', 's', 't' };
            unsigned char* ret = pfnSha256(msg, sizeof(msg), md);
            EXPECT_TRUE(ret == nullptr, "SHA256 MUST return nullptr when real libcrypto is missing");
            bool allZero = true;
            for (unsigned char b : md) {
                if (b != 0) allZero = false;
            }
            EXPECT_TRUE(allZero, "SHA256 MUST zero caller output buffer on missing backend");
        }
        if (pfnSha256Init) {
            unsigned char ctx[128];
            std::memset(ctx, 0xAA, sizeof(ctx));
            EXPECT_TRUE(pfnSha256Init(ctx) == 0, "SHA256_Init MUST return 0 (failure) when real libcrypto is missing");
        }
    }

    // 3. Test libcurl_x64.dll binary-safe safe_realloc
    std::printf("[TEST] [L2-P1-01] kongor_curl binary-safe safe_realloc...\n");
    HMODULE hCurl = LoadSiblingBuildDll("kongor_curl_build", "libcurl_x64.dll");
    EXPECT_TRUE(hCurl != nullptr, "Failed to load proxy libcurl_x64.dll");
    if (hCurl != nullptr) {
        using PFN_TestCurlRealloc = int(__cdecl*)();
        auto pfnTestCurl = reinterpret_cast<PFN_TestCurlRealloc>(GetProcAddress(hCurl, "Kongor_CurlGuard_SelfTestBinaryRealloc"));
        EXPECT_TRUE(pfnTestCurl != nullptr, "Export Kongor_CurlGuard_SelfTestBinaryRealloc must exist");
        if (pfnTestCurl) {
            EXPECT_TRUE(pfnTestCurl() == 1, "kongor_curl safe_realloc must preserve embedded null bytes across tagged and alien buffers");
        }
    }

    // 4. Test zlibwapi_x64.dll uncompress2 *sourceLen update
    std::printf("[TEST] [L2-P1-01] kongor_zlib uncompress2 *sourceLen consumed-bytes update...\n");
    HMODULE hZlib = LoadSiblingBuildDll("kongor_zlib_build", "zlibwapi_x64.dll");
    EXPECT_TRUE(hZlib != nullptr, "Failed to load proxy zlibwapi_x64.dll");
    if (hZlib != nullptr) {
        using uLong = unsigned long;
        using uLongf = unsigned long;
        using Bytef = unsigned char;
        using PFN_uncompress2 = int(__cdecl*)(Bytef*, uLongf*, const Bytef*, uLong*);

        auto pfnUncompress2 = reinterpret_cast<PFN_uncompress2>(GetProcAddress(hZlib, "uncompress2"));
        EXPECT_TRUE(pfnUncompress2 != nullptr, "uncompress2 export must exist on proxy zlibwapi_x64.dll");
        if (pfnUncompress2) {
            // Pre-computed valid zlib-wrapped stream for "KONGOR" (6 bytes):
            // 78 9C F3 F6 F7 73 F7 0F 02 00 06 50 01 D1 (14 bytes exact)
            const Bytef compressedWithTrailing[22] = {
                0x78, 0x9C, 0xF3, 0xF6, 0xF7, 0x73, 0xF7, 0x0F, 0x02, 0x00, 0x06, 0x50, 0x01, 0xD1,
                0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22
            };
            Bytef outBuf[32] = {};
            uLongf destLen = sizeof(outBuf);
            uLong srcLen = sizeof(compressedWithTrailing); // 22 bytes passed in (14 valid + 8 trailing)

            int zrc = pfnUncompress2(outBuf, &destLen, compressedWithTrailing, &srcLen);
            EXPECT_TRUE(zrc == 0, "uncompress2 on valid zlib stream + trailing bytes must return Z_OK (0)");
            EXPECT_TRUE(destLen == 6 && std::memcmp(outBuf, "KONGOR", 6) == 0, "uncompress2 must decode exact 'KONGOR' payload");
            EXPECT_TRUE(srcLen == 14, "uncompress2 MUST update *sourceLen from 22 to exact 14 consumed bytes");

            uLong zeroSrc = 0;
            uLongf badDestLen = sizeof(outBuf);
            EXPECT_TRUE(pfnUncompress2(outBuf, &badDestLen, compressedWithTrailing, &zeroSrc) != 0, "uncompress2 with *sourceLen == 0 must fail");
        }
    }

    // 5. Test Plan A: 32-slot Flight-Recorder SeqLock Ring (commitSeq + 47-char detail clamp) & LogBurstLimiter
    std::printf("[TEST] [TARANTOOL-RING-01] BinaryTelemetryRing 32-slot wraparound, torn-slot skip, & LogBurstLimiter...\n");
    {
        Kongor::BinaryTelemetryRing<64> ring;
        const char* overlongLabel = "ui/scripts/newui/store_alt_avatar_preview_controller_extremely_long_tail.lua\r\ninjected";
        for (uint32_t i = 0; i < 40; ++i) {
            char tagLabel[64];
            std::snprintf(tagLabel, sizeof(tagLabel), "evt_%02u", i);
            ring.Record(i, "lua_eval", static_cast<uint64_t>(i), 0, (i == 39) ? overlongLabel : tagLabel);
        }

        // Simulate a thread crashing mid-write on slot #40 (odd uncommitted sequence number)
        ring.SimulateInterruptedWriteForTest("torn_crash_slot", "CORRUPTED_UNCOMMITTED_GARBAGE_PAYLOAD");

        // Record one more committed event after the interrupted slot (#41)
        ring.Record(41U, "post_torn_ok", 0xABCDULL, 0, "final_valid_breadcrumb");

        Kongor::StackSStream<6144> dump;
        ring.FormatRecentToStream(dump, 32);
        std::string rendered(dump.c_str());

        EXPECT_TRUE(rendered.find("final_valid_breadcrumb") != std::string::npos,
            "Most recent committed slot after torn write must appear in dump");
        EXPECT_TRUE(rendered.find("CORRUPTED_UNCOMMITTED_GARBAGE_PAYLOAD") == std::string::npos,
            "Uncommitted odd-commitSeq slot MUST be skipped during crash dump formatting");
        EXPECT_TRUE(rendered.find("skipped_in_flight_slots=1") != std::string::npos,
            "Dump must explicitly account for 1 skipped in-flight slot");
        EXPECT_TRUE(rendered.find("ui/scripts/newui/store_alt_avatar_preview_contr") != std::string::npos,
            "Overlong detail string must be truncated cleanly to 47 characters + null terminator");
        EXPECT_TRUE(rendered.find("injected") == std::string::npos,
            "Characters past byte 47 (including CRLF tail) must never overflow into dump");
        // 32 window starting from head=42 covers indices 41..10 (31 committed + 1 skipped torn); evt_09 must have aged out
        EXPECT_TRUE(rendered.find("detail=\"evt_10\"") != std::string::npos,
            "32-slot window must retain index 10");
        EXPECT_TRUE(rendered.find("detail=\"evt_09\"") == std::string::npos,
            "Events older than 32 slots (evt_09) must be bounded out of recent 32 dump");

        // Verify Tarantool-style LogBurstLimiter (burst=3 per 5000ms window)
        Kongor::LogBurstLimiter limiter(5000ULL, 3U);
        uint64_t flushed = 999;
        uint64_t winMs = 0;
        EXPECT_TRUE(limiter.TryAcquire(1000ULL, &flushed, &winMs) && flushed == 0, "Burst #1 allowed with 0 prior suppressed");
        EXPECT_TRUE(limiter.TryAcquire(1010ULL, &flushed, &winMs) && flushed == 0, "Burst #2 allowed");
        EXPECT_TRUE(limiter.TryAcquire(1020ULL, &flushed, &winMs) && flushed == 0, "Burst #3 allowed");
        for (int s = 0; s < 144; ++s) {
            EXPECT_TRUE(!limiter.TryAcquire(1500ULL + static_cast<uint64_t>(s), &flushed, &winMs),
                "Events exceeding burst within 5000ms window must be suppressed");
        }
        // Advance past 5000ms window (t = 6050ms -> elapsed = 5050ms)
        EXPECT_TRUE(limiter.TryAcquire(6050ULL, &flushed, &winMs),
            "First event in new window must be allowed");
        EXPECT_TRUE(flushed == 144ULL && winMs == 5050ULL,
            "Window reset MUST report exact suppressed count (144) and elapsed window (5050ms)");
    }
}
#endif

int main() {
    TestHonPingProtocol();
    TestPerSlotPageBitmaskTracker();
#ifdef _WIN32
    TestWindowsNativeGuardAndProxies();
#endif

    if (g_failures > 0) {
        std::fprintf(stderr, "[FAILED] %d assertion(s) failed in test_veh_unwind_ping_shim_and_proxy_guards.\n", g_failures);
        return 1;
    }
    std::printf("[PASSED] All Wave 4B native hardening unit & seam assertions passed.\n");
    return 0;
}
