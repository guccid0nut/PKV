// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard Standalone C++ Unit Test Runner (#255)
// Directly exercises the real C++ implementations in veh_crash_shield.cpp and
// runtime_patch_controller.cpp withouttesthost.exe DllMain loader-lock risk.
// ============================================================================
#include "../native_guard_internal.h"
#include "../runtime_patch_controller.h"
#include "../cursor_confinement.h"
#include "../vfs_loose_cache.h"
#include "../modern_archive_hook.h"
#include "../kongor_lua_shared_mem.h"
#include "../kongor_base_cmd.h"
#include "../queue_arcade_hooks.h"
#include "../asset_prewarmer.h"
#include "kongor_pipe_security.h"
#include "kongor_tracy.h"
#include "../shm_telemetry.cpp"
#include <filesystem>
#include <fstream>
#include <windows.h>
#include <winioctl.h>
#include <dbghelp.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

// Required link symbols when compiling veh_crash_shield.cpp and runtime_patch_controller.cpp
// outside dllmain.cpp:
LPTOP_LEVEL_EXCEPTION_FILTER pPreviousFilter = NULL;

bool Kongor_ProcessPipeStreamLines(const char* rawInput, char* outResp, size_t outCap) {
    if (!outResp || outCap == 0) return false;
    snprintf(outResp, outCap, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"echo\":\"%s\"}}", rawInput ? rawInput : "");
    return true;
}

void LogArchiveTelemetrySummary() {}
void DispatchCommandToEngine(const std::wstring&, int64_t, uint32_t) {}
void CheckMainThreadWatchdogTick() {}
bool ResolveK2LuaSymbols() { return false; }
void** g_ppLuaScriptManager = nullptr;
int (*g_pfnCountMemoryUsed)(void*) = nullptr;
std::atomic<uint64_t> g_lastMainThreadHeartbeatTick{0};
std::atomic<uint64_t> g_cachedLuaMemoryBytes{0};

namespace {

int g_totalChecks = 0;
int g_failedChecks = 0;

void ExpectTrue(bool condition, const char* checkName, const char* detail = "") {
    ++g_totalChecks;
    if (!condition) {
        ++g_failedChecks;
        fprintf(stderr, "[FAIL] %s %s\n", checkName, detail ? detail : "");
    } else {
        fprintf(stdout, "[PASS] %s\n", checkName);
    }
}

void WriteLeU32(uint8_t* dst, uint32_t val) {
    memcpy(dst, &val, sizeof(uint32_t));
}

// ----------------------------------------------------------------------------
// 1. (#167) Test Benign Exception Filter & Access Violation R/W/X Classifier
// ----------------------------------------------------------------------------
void TestVehBenignFilterAndAvClassifier() {
    ExpectTrue(Kongor_IsBenignDebugOrThreadNameException(0x40010006UL), "BenignFilter_DBG_PRINTEXCEPTION_C");
    ExpectTrue(Kongor_IsBenignDebugOrThreadNameException(0x4001000AUL), "BenignFilter_DBG_PRINTEXCEPTION_WIDE_C");
    ExpectTrue(Kongor_IsBenignDebugOrThreadNameException(0x406D1388UL), "BenignFilter_MS_VC_EXCEPTION_SetThreadName");
    ExpectTrue(!Kongor_IsBenignDebugOrThreadNameException(0xC0000005UL), "BenignFilter_Rejects_AccessViolation");
    ExpectTrue(!Kongor_IsBenignDebugOrThreadNameException(0xE06D7363UL), "BenignFilter_Rejects_MsvcCppThrow");

    ExpectTrue(strcmp(Kongor_ClassifyAccessViolationKind(0), "READ") == 0, "ClassifyAv_0_READ");
    ExpectTrue(strcmp(Kongor_ClassifyAccessViolationKind(1), "WRITE") == 0, "ClassifyAv_1_WRITE");
    ExpectTrue(strcmp(Kongor_ClassifyAccessViolationKind(8), "DEP_EXECUTE") == 0, "ClassifyAv_8_DEP_EXECUTE");
    ExpectTrue(strcmp(Kongor_ClassifyAccessViolationKind(99), "UNKNOWN_AV_KIND") == 0, "ClassifyAv_Unknown");

    // Verify NativeGuardVectoredHandler immediately returns EXCEPTION_CONTINUE_SEARCH on benign exceptions
    EXCEPTION_RECORD benignRec{};
    benignRec.ExceptionCode = 0x406D1388UL;
    CONTEXT benignCtx{};
    EXCEPTION_POINTERS ptrs{ &benignRec, &benignCtx };
    LONG vehDisp = NativeGuardVectoredHandler(&ptrs);
    ExpectTrue(vehDisp == EXCEPTION_CONTINUE_SEARCH, "NativeGuardVectoredHandler_Ignores_MS_VC_EXCEPTION");
}

// ----------------------------------------------------------------------------
// 2. (#168) Test Chained .pdata UNW_FLAG_CHAININFO (0x4) Unwind Entry Resolver
// ----------------------------------------------------------------------------
void TestPdataChainedUnwindResolver() {
    // Construct a synthetic 16 KiB module image in memory:
    // Entry 0 (Primary):   [0x1000, 0x1100), UnwindInfo @ 0x2000 (Flags = 0)
    // Entry 1 (Secondary): [0x1100, 0x1200), UnwindInfo @ 0x2100 (Flags = 4 UNW_FLAG_CHAININFO, CountOfCodes = 1 odd -> padded to 2 slots -> chained Entry 0)
    // Entry 2 (Tertiary):  [0x1200, 0x1300), UnwindInfo @ 0x2200 (Flags = 4 UNW_FLAG_CHAININFO, CountOfCodes = 0 -> chained Entry 1)
    // Entry 3 (Bit0-Chain):[0x1300, 0x1400), UnwindInfoAddress = 0x2500 | 1 (direct pointer to RUNTIME_FUNCTION at 0x2500 -> Entry 0)
    // Entry 4 & 5 (Cycle): [0x1400, 0x1500) <-> [0x1500, 0x1600) cyclic UNW_FLAG_CHAININFO
    std::vector<uint8_t> image(0x4000, 0);
    uintptr_t baseAddr = reinterpret_cast<uintptr_t>(image.data());

    // UnwindInfo @ 0x2000: Version=1, Flags=0
    image[0x2000] = 0x01;
    image[0x2001] = 0x04;
    image[0x2002] = 0x00;
    image[0x2003] = 0x00;

    // UnwindInfo @ 0x2100: Version=1, Flags=4 (UNW_FLAG_CHAININFO), CountOfCodes=1 (odd -> padded to 2 uint16_t = 4 bytes)
    image[0x2100] = static_cast<uint8_t>((0x04 << 3) | 0x01);
    image[0x2101] = 0x08;
    image[0x2102] = 0x01; // 1 unwind code -> 2 slots (4 bytes) before chained RUNTIME_FUNCTION at +8
    image[0x2103] = 0x00;
    WriteLeU32(&image[0x2108 + 0], 0x1000U);
    WriteLeU32(&image[0x2108 + 4], 0x1100U);
    WriteLeU32(&image[0x2108 + 8], 0x2000U);

    // UnwindInfo @ 0x2200: Version=1, Flags=4 (UNW_FLAG_CHAININFO), CountOfCodes=0 -> chained RUNTIME_FUNCTION at +4
    image[0x2200] = static_cast<uint8_t>((0x04 << 3) | 0x01);
    image[0x2201] = 0x04;
    image[0x2202] = 0x00;
    image[0x2203] = 0x00;
    WriteLeU32(&image[0x2204 + 0], 0x1100U);
    WriteLeU32(&image[0x2204 + 4], 0x1200U);
    WriteLeU32(&image[0x2204 + 8], 0x2100U);

    // Direct bit-0 chained RUNTIME_FUNCTION at 0x2500 pointing to Entry 0
    WriteLeU32(&image[0x2500 + 0], 0x1000U);
    WriteLeU32(&image[0x2500 + 4], 0x1100U);
    WriteLeU32(&image[0x2500 + 8], 0x2000U);

    // Cyclic UnwindInfo @ 0x2300 <-> 0x2400
    image[0x2300] = static_cast<uint8_t>((0x04 << 3) | 0x01);
    image[0x2302] = 0x00;
    WriteLeU32(&image[0x2304 + 0], 0x1500U);
    WriteLeU32(&image[0x2304 + 4], 0x1600U);
    WriteLeU32(&image[0x2304 + 8], 0x2400U);

    image[0x2400] = static_cast<uint8_t>((0x04 << 3) | 0x01);
    image[0x2402] = 0x00;
    WriteLeU32(&image[0x2404 + 0], 0x1400U);
    WriteLeU32(&image[0x2404 + 4], 0x1500U);
    WriteLeU32(&image[0x2404 + 8], 0x2300U);

    RUNTIME_FUNCTION rfTertiary{ 0x1200U, 0x1300U, 0x2200U };
    uint32_t beginRva = 0, endRva = 0, hops = 0;
    bool okChain = Kongor_ResolveChainedPdataFunctionEntry(baseAddr, &rfTertiary, &beginRva, &endRva, &hops);
    ExpectTrue(okChain && beginRva == 0x1000U && endRva == 0x1100U && hops == 2U,
               "PdataChain_ResolvesTwoHopOddPaddedChainToPrimary");

    RUNTIME_FUNCTION rfBit0{ 0x1300U, 0x1400U, 0x2500U | 1U };
    uint32_t bit0Begin = 0, bit0End = 0, bit0Hops = 0;
    bool okBit0 = Kongor_ResolveChainedPdataFunctionEntry(baseAddr, &rfBit0, &bit0Begin, &bit0End, &bit0Hops);
    ExpectTrue(okBit0 && bit0Begin == 0x1000U && bit0End == 0x1100U && bit0Hops == 1U,
               "PdataChain_ResolvesBit0DirectChainedRuntimeFunction");

    RUNTIME_FUNCTION rfCycle{ 0x1400U, 0x1500U, 0x2300U };
    uint32_t cycBegin = 0, cycEnd = 0, cycHops = 0;
    bool okCycle = Kongor_ResolveChainedPdataFunctionEntry(baseAddr, &rfCycle, &cycBegin, &cycEnd, &cycHops);
    ExpectTrue(okCycle && cycHops == 16U && (cycBegin == 0x1400U || cycBegin == 0x1500U),
               "PdataChain_CyclicChainTerminatesAt16HopLimit");
}

// ----------------------------------------------------------------------------
// 3. (#169, #166, #168, #176-#179) Test Real C++ PatchController & Phase 14 Self-Test
// ----------------------------------------------------------------------------
void TestPatchControllerIntervalGuardAndPhase14SelfTest() {
    // Run the full in-process Phase 14 C++ self-test (tests #166 real std::runtime_error + virtual inheritance
    // + corrupted _ThrowInfo SEH trap, #168 trampoline unwind, #169 inline hook overlap rejection,
    // #176 double-buffered main-thread queue, #178 re-entrant priority event bus, #179 shadow VMT + EntityFieldContainer)
    char selfTestJson[4096] = {0};
    bool selfTestOk = Kongor::PatchController::ExecuteHookToggle(
        "__phase14_selftest__",
        "run",
        selfTestJson,
        sizeof(selfTestJson),
        true);
    ExpectTrue(selfTestOk, "Phase14_RunSelfTest_AllPass", selfTestJson);
    ExpectTrue(strstr(selfTestJson, "\"rttiRealOk\":true") != nullptr, "Phase14_RttiRealStdException");
    ExpectTrue(strstr(selfTestJson, "\"rttiVirtOk\":true") != nullptr, "Phase14_RttiVirtualBaseException");
    ExpectTrue(strstr(selfTestJson, "\"rttiCorruptSehOk\":true") != nullptr, "Phase14_RttiCorruptedThrowInfoSehGuard");
    ExpectTrue(strstr(selfTestJson, "\"trampUnwindOk\":true") != nullptr, "Phase14_TrampolineUnwindLookup");
    ExpectTrue(strstr(selfTestJson, "\"mainThreadQueueOk\":true") != nullptr, "Phase14_MainThreadDoubleBufferedQueue");
    ExpectTrue(strstr(selfTestJson, "\"dispatcherOk\":true") != nullptr, "Phase14_ReentrantPriorityEventDispatcher");
    ExpectTrue(strstr(selfTestJson, "\"vmtOk\":true") != nullptr, "Phase14_ShadowVmtHookManager");
    ExpectTrue(strstr(selfTestJson, "\"entityFieldsOk\":true") != nullptr, "Phase14_EntityFieldContainer");

    // Additional explicit #169 byte-patch interval overlap, adjacency, and idempotency checks on a scratch page
    uint8_t* pScratch = reinterpret_cast<uint8_t*>(
        VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    ExpectTrue(pScratch != nullptr, "ScratchPage_VirtualAlloc");
    if (pScratch) {
        memset(pScratch, 0x90, 4096);
        uint8_t patchA[8] = { 0x48, 0x31, 0xC0, 0x90, 0x90, 0x90, 0x90, 0x90 };
        uint8_t patchB[8] = { 0x48, 0xFF, 0xC0, 0x90, 0x90, 0x90, 0x90, 0x90 };

        // Install Site A at [pScratch + 0x100, pScratch + 0x107] (8 bytes)
        bool okA = Kongor::PatchController::InstallManagedBytePatch(
            "unit_interval", "SiteA", pScratch + 0x100, patchA, sizeof(patchA), nullptr, 0);
        ExpectTrue(okA, "IntervalGuard_InstallSiteA");

        // Idempotent re-registration of exact same Site A at [pScratch + 0x100, 8] must succeed
        bool okAIdemp = Kongor::PatchController::InstallManagedBytePatch(
            "unit_interval", "SiteA", pScratch + 0x100, patchA, sizeof(patchA), nullptr, 0);
        ExpectTrue(okAIdemp, "IntervalGuard_IdempotentReRegistrationAllowed");

        // Adjacent Site B at [pScratch + 0x108, pScratch + 0x10F] (immediately after Site A) must succeed
        bool okBAdjacent = Kongor::PatchController::InstallManagedBytePatch(
            "unit_interval", "SiteB_Adjacent", pScratch + 0x108, patchB, sizeof(patchB), nullptr, 0);
        ExpectTrue(okBAdjacent, "IntervalGuard_AdjacentIntervalAllowed");

        // Overlapping Site C at [pScratch + 0x107, pScratch + 0x10E] (shares last byte of Site A and first 7 bytes of Site B) must be REJECTED
        bool okCOverlap = Kongor::PatchController::InstallManagedBytePatch(
            "unit_interval", "SiteC_OverlapBoundary", pScratch + 0x107, patchB, sizeof(patchB), nullptr, 0);
        ExpectTrue(!okCOverlap, "IntervalGuard_RejectsSingleByteBoundaryOverlap");

        // Restore vanilla bytes before freeing scratch page
        Kongor::PatchController::ExecuteHookToggle("unit_interval", "off", nullptr, 0, true);
        VirtualFree(pScratch, 0, MEM_RELEASE);
    }
}

// ----------------------------------------------------------------------------
// 4. (#201 & #216) Test Code-Cave Scanner & GraphicsVtableOffsetCache
// ----------------------------------------------------------------------------
void TestCodeCaveAndVtableOffsetCache() {
    std::vector<uint8_t> modBuf(512, 0x00);
    // Place 32 bytes of 0xCC padding at offset 200
    memset(modBuf.data() + 200, 0xCC, 32);
    size_t caveOff = 0;
    bool foundCave = Kongor::PatchController::FindContiguousCodeCaveNear(
        modBuf.data(), modBuf.size(), 64, 24, &caveOff);
    ExpectTrue(foundCave && caveOff == 200, "CodeCave_FindContiguousCodeCaveNear");

    Kongor::PatchController::GraphicsVtableOffsetCache cache{};
    cache.d3d9Fingerprint = { 10, 20, 10, 20 };
    cache.dxgiFingerprint = { 30, 40, 30, 40 };
    cache.d3d11Fingerprint = { 50, 60, 50, 60 };
    cache.d3d9PresentOffset = 0x44;
    cache.dxgiPresentOffset = 0x20;
    cache.checksumFnv1a32 = Kongor::PatchController::ComputeGraphicsVtableCacheChecksum(cache);

    ExpectTrue(
        Kongor::PatchController::ValidateGraphicsVtableOffsetCache(
            cache, cache.d3d9Fingerprint, cache.dxgiFingerprint, cache.d3d11Fingerprint),
        "VtableOffsetCache_ValidatesMatchingChecksumAndFingerprints");

    Kongor::PatchController::GraphicsVtableOffsetCache corrupted = cache;
    corrupted.d3d9PresentOffset = 0x48; // mutated without recomputing checksum
    ExpectTrue(
        !Kongor::PatchController::ValidateGraphicsVtableOffsetCache(
            corrupted, cache.d3d9Fingerprint, cache.dxgiFingerprint, cache.d3d11Fingerprint),
        "VtableOffsetCache_RejectsCorruptedChecksum");
}

// ----------------------------------------------------------------------------
// 5. (#260) Test AMD64 Non-Canonical #GPF Inspector & Poison / Bit-Flip Classifier
// ----------------------------------------------------------------------------
void TestVehAmd64GpfAndPoisonClassifier() {
    ExpectTrue(!Kongor_IsAmd64NonCanonicalAddress(0x00007FF712345678ULL), "Canonical_UserModeAddress");
    ExpectTrue(!Kongor_IsAmd64NonCanonicalAddress(0xFFFF800012345678ULL), "Canonical_KernelModeAddress");
    ExpectTrue(Kongor_IsAmd64NonCanonicalAddress(0xDDDDDDDDDDDDDDDDULL), "NonCanonical_0xDD_Poison");
    ExpectTrue(Kongor_IsAmd64NonCanonicalAddress(0x01007FF712345678ULL), "NonCanonical_Bit56Flipped");

    uint64_t gprs[16] = {0};
    gprs[1] = 0xDDDDDDDDDDDDDDDDULL; // RCX holds freed CRT heap poison
    gprs[4] = 0x0000005012345680ULL; // 16-byte aligned RSP
    char gpfDiag[256] = {0};
    int nonCanonIdx = -1;
    uintptr_t nonCanonVal = 0;
    bool gpfOk = Kongor_InspectAmd64GeneralProtectionFault(
        static_cast<uintptr_t>(0xFFFFFFFFFFFFFFFFULL),
        gprs,
        gpfDiag,
        sizeof(gpfDiag),
        &nonCanonIdx,
        &nonCanonVal);
    ExpectTrue(gpfOk && nonCanonIdx == 1 && nonCanonVal == 0xDDDDDDDDDDDDDDDDULL &&
               strstr(gpfDiag, "RCX=0xDDDDDDDDDDDDDDDD") != nullptr,
               "GpfInspector_IdentifiesNonCanonicalRegisterRcx");

    // When all 16 GPRs are canonical and RSP is 8-mod-16 unaligned, diagnoses SSE alignment fault
    gprs[1] = 0x00007FF712345678ULL;
    gprs[4] = 0x0000005012345688ULL; // unaligned by 8 bytes
    bool gpfSseOk = Kongor_InspectAmd64GeneralProtectionFault(
        static_cast<uintptr_t>(0xFFFFFFFFFFFFFFFFULL),
        gprs,
        gpfDiag,
        sizeof(gpfDiag),
        &nonCanonIdx,
        &nonCanonVal);
    ExpectTrue(gpfSseOk && nonCanonIdx == -1 && strstr(gpfDiag, "RSP_16B_Aligned=false") != nullptr,
               "GpfInspector_IdentifiesUnalignedSseCandidate");

    // Negative control: normal null-deref address (not 0xFFFFFFFFFFFFFFFF) must return false
    ExpectTrue(!Kongor_InspectAmd64GeneralProtectionFault(0x18ULL, gprs, gpfDiag, sizeof(gpfDiag)),
               "GpfInspector_RejectsNonGpfFaultAddress");

    // Poison patterns & [heuristic-hint] single-bit-flip classification
    char tag[96] = {0};
    bool isBitFlip = false;
    uint32_t bitIdx = 0;
    ExpectTrue(Kongor_ClassifyPointerPoisonOrBitFlip(0xDDDDDDDDDDDDDDDDULL, 0, tag, sizeof(tag), &isBitFlip, &bitIdx) &&
               strcmp(tag, "CRT_FREED_HEAP_0xDD") == 0 && !isBitFlip,
               "PoisonClassifier_0xDD_CrtFreedHeap");
    ExpectTrue(Kongor_ClassifyPointerPoisonOrBitFlip(0xCCCCCCCCCCCCCCCCULL + 0x20ULL, 0, tag, sizeof(tag), &isBitFlip, &bitIdx) &&
               strcmp(tag, "MSVC_UNINIT_STACK_0xCC") == 0 && !isBitFlip,
               "PoisonClassifier_0xCC_UninitStackWithStructOffset");
    ExpectTrue(Kongor_ClassifyPointerPoisonOrBitFlip(0xCDCDCDCDCDCDCDCDULL, 0, tag, sizeof(tag), &isBitFlip, &bitIdx) &&
               strcmp(tag, "CRT_UNINIT_HEAP_0xCD") == 0 && !isBitFlip,
               "PoisonClassifier_0xCD_CrtUninitHeap");
    ExpectTrue(Kongor_ClassifyPointerPoisonOrBitFlip(0xFEEEFEEEFEEEFEEEULL, 0, tag, sizeof(tag), &isBitFlip, &bitIdx) &&
               strcmp(tag, "WIN32_HEAPFREE_0xFEEE") == 0 && !isBitFlip,
               "PoisonClassifier_0xFEEE_Win32HeapFree");
    ExpectTrue(Kongor_ClassifyPointerPoisonOrBitFlip(0xBAADF00DBAADF00DULL, 0, tag, sizeof(tag), &isBitFlip, &bitIdx) &&
               strcmp(tag, "WIN32_LOCALALLOC_UNINIT_0xBAADF00D") == 0 && !isBitFlip,
               "PoisonClassifier_0xBAADF00D_LocalAlloc");

    uintptr_t flipped56 = static_cast<uintptr_t>(0x00007FF712345678ULL ^ (1ULL << 56));
    ExpectTrue(Kongor_ClassifyPointerPoisonOrBitFlip(flipped56, 0, tag, sizeof(tag), &isBitFlip, &bitIdx) &&
               isBitFlip && bitIdx == 56U && strstr(tag, "[heuristic-hint] SINGLE_BIT_FLIP_BIT_56") != nullptr,
               "PoisonClassifier_SingleBitFlipHeuristicHintBit56");

    uintptr_t twoBitsFlipped = static_cast<uintptr_t>(0x00007FF712345678ULL ^ ((1ULL << 56) | (1ULL << 57)));
    ExpectTrue(!Kongor_ClassifyPointerPoisonOrBitFlip(twoBitsFlipped, 0, tag, sizeof(tag), &isBitFlip, &bitIdx),
               "PoisonClassifier_RejectsMultiBitCorruptionWithoutFalsePositive");
}

// ----------------------------------------------------------------------------
// 6. (#261) Test Bounded 40-Qword Stack Scan & Preceding CALL Opcode Validator
// ----------------------------------------------------------------------------
void TestBoundedStackScanAndCallValidator() {
    char callTag[32] = {0};
    const uint8_t callRel32[5] = { 0xE8, 0x12, 0x34, 0x56, 0x78 };
    ExpectTrue(Kongor_ValidatePrecedingCallInstruction(callRel32, sizeof(callRel32), callTag, sizeof(callTag)) &&
               strcmp(callTag, "CALL_REL32_E8") == 0,
               "CallValidator_Accepts_E8_Rel32");

    const uint8_t callRipInd[6] = { 0xFF, 0x15, 0x10, 0x20, 0x30, 0x40 };
    ExpectTrue(Kongor_ValidatePrecedingCallInstruction(callRipInd, sizeof(callRipInd), callTag, sizeof(callTag)) &&
               strcmp(callTag, "CALL_RIP_IND_FF15") == 0,
               "CallValidator_Accepts_FF15_RipIndirect");

    const uint8_t callRegInd[2] = { 0xFF, 0xD0 }; // call rax
    ExpectTrue(Kongor_ValidatePrecedingCallInstruction(callRegInd, sizeof(callRegInd), callTag, sizeof(callTag)) &&
               strcmp(callTag, "CALL_REG_IND_FFD0") == 0,
               "CallValidator_Accepts_FFD0_RegIndirect");

    const uint8_t callDisp8Ind[3] = { 0xFF, 0x50, 0x28 }; // call qword ptr [rax+0x28]
    ExpectTrue(Kongor_ValidatePrecedingCallInstruction(callDisp8Ind, sizeof(callDisp8Ind), callTag, sizeof(callTag)) &&
               strcmp(callTag, "CALL_DISP8_IND_FF50") == 0,
               "CallValidator_Accepts_FF50_Disp8Indirect");

    const uint8_t jmpRel32[5] = { 0xE9, 0x12, 0x34, 0x56, 0x78 }; // jmp rel32 (NOT a call)
    ExpectTrue(!Kongor_ValidatePrecedingCallInstruction(jmpRel32, sizeof(jmpRel32), callTag, sizeof(callTag)),
               "CallValidator_Rejects_E9_JmpRel32");

    // Synthetic module image with .text = [0x100, 0x1000)
    std::vector<uint8_t> modImg(0x2000, 0x90);
    uintptr_t modBase = reinterpret_cast<uintptr_t>(modImg.data());

    // Valid site 1 at RVA 0x200..0x205 (E8 rel32 -> return address at RVA 0x205)
    modImg[0x200] = 0xE8;
    modImg[0x201] = 0x40;
    modImg[0x202] = 0x00;
    modImg[0x203] = 0x00;
    modImg[0x204] = 0x00;

    // Decoy non-call site at RVA 0x300..0x305 (E9 jmp rel32 -> RVA 0x305)
    modImg[0x300] = 0xE9;
    modImg[0x301] = 0x40;
    modImg[0x302] = 0x00;
    modImg[0x303] = 0x00;
    modImg[0x304] = 0x00;

    // Boundary tail-call site at end of .text: FF D0 at 0xFFE..0xFFF -> return address at RVA 0x1000 (retAddr - 1 = 0xFFF inside .text!)
    modImg[0xFFE] = 0xFF;
    modImg[0xFFF] = 0xD0;

    // Valid site 3 at RVA 0x400..0x405 placed at qword index 45 (outside the 40-qword [Rsp, Rsp+320] cap)
    modImg[0x400] = 0xE8;
    modImg[0x401] = 0x10;
    modImg[0x402] = 0x00;
    modImg[0x403] = 0x00;
    modImg[0x404] = 0x00;

    std::vector<uintptr_t> fakeStack(50, 0);
    fakeStack[5]  = modBase + 0x205;  // valid #1
    fakeStack[10] = modBase + 0x305;  // decoy JMP (must be rejected)
    fakeStack[15] = modBase + 0x1000; // valid #2 (tail-call at exact .text end boundary, retAddr - 1 = 0xFFF)
    fakeStack[45] = modBase + 0x405;  // beyond 40-qword bound (must be ignored)

    uintptr_t validRets[8] = {0};
    size_t found = Kongor_ScanBoundedStackForValidCallSites(
        fakeStack.data(), fakeStack.size(), modBase, 0x100U, 0x1000U, validRets, 8);
    ExpectTrue(found == 2 && validRets[0] == (modBase + 0x205) && validRets[1] == (modBase + 0x1000),
               "BoundedStackScan_RecoversValidCallsAndEnforces40QwordLimit");
}

// ----------------------------------------------------------------------------
// 7. (#268) Test HDE64 Prologue Validator & ppOrigTrampoline == nullptr Opt-Out
// ----------------------------------------------------------------------------
void TestHde64PrologueValidatorAndNullTrampolineOptOut() {
    using namespace Kongor::PatchController;

    // 5 real K2 full-replacement prologues containing short conditional branches (74/75)
    const uint8_t patAtoBW[14] = { 0x48, 0x83, 0xEC, 0x28, 0x0F, 0xB7, 0x11, 0x66, 0x85, 0xD2, 0x75, 0x07, 0x32, 0xC0 };
    const uint8_t patAtoBA[14] = { 0x48, 0x83, 0xEC, 0x28, 0x0F, 0xB6, 0x11, 0x84, 0xD2, 0x75, 0x07, 0x32, 0xC0, 0x48 };
    const uint8_t patAtoFW[14] = { 0x66, 0x90, 0x0F, 0xB7, 0x01, 0x66, 0x83, 0xF8, 0x20, 0x74, 0x06, 0x66, 0x83, 0xF8 };
    const uint8_t patAtoFA[14] = { 0x66, 0x90, 0x0F, 0xB6, 0x01, 0x3C, 0x20, 0x74, 0x04, 0x3C, 0x09, 0x75, 0x05, 0x48 };
    const uint8_t patIsOpen[17] = { 0x8B, 0x81, 0xA8, 0x00, 0x00, 0x00, 0xA8, 0x01, 0x74, 0x0F, 0x48, 0x8B, 0x09, 0x48, 0x8B, 0x41, 0x48 };

    PrologueValidationResult res{};
    ExpectTrue(!ValidateHookPrologue64(patAtoBW, sizeof(patAtoBW), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_RELATIVE_BRANCH_IN_PROLOGUE") == 0 && res.faultOffset == 10,
               "Hde64Validator_Refuses_AtoBFastW_JneAtOffset10_WhenTrampolineRequested");
    ExpectTrue(!ValidateHookPrologue64(patAtoBA, sizeof(patAtoBA), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_RELATIVE_BRANCH_IN_PROLOGUE") == 0 && res.faultOffset == 9,
               "Hde64Validator_Refuses_AtoBFastA_JneAtOffset9_WhenTrampolineRequested");
    ExpectTrue(!ValidateHookPrologue64(patAtoFW, sizeof(patAtoFW), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_RELATIVE_BRANCH_IN_PROLOGUE") == 0 && res.faultOffset == 9,
               "Hde64Validator_Refuses_AtoFFastW_JeAtOffset9_WhenTrampolineRequested");
    ExpectTrue(!ValidateHookPrologue64(patAtoFA, sizeof(patAtoFA), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_RELATIVE_BRANCH_IN_PROLOGUE") == 0 && res.faultOffset == 7,
               "Hde64Validator_Refuses_AtoFFastA_JeAtOffset7_WhenTrampolineRequested");
    ExpectTrue(!ValidateHookPrologue64(patIsOpen, sizeof(patIsOpen), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_RELATIVE_BRANCH_IN_PROLOGUE") == 0 && res.faultOffset == 8,
               "Hde64Validator_Refuses_CArchiveIsOpen_JeAtOffset8_WhenTrampolineRequested");

    // Full-replacement opt-out (requiresCallableTrampoline == false) accepts all 5
    ExpectTrue(ValidateHookPrologue64(patIsOpen, sizeof(patIsOpen), false, &res) &&
               strcmp(res.refusalReason, "NONE_FULL_REPLACEMENT_OPTOUT") == 0,
               "Hde64Validator_Accepts_CArchiveIsOpen_WhenNullTrampolineOptOut");

    // RIP-relative operand (48 8B 05 11 22 33 44 at offset 4) must be refused when trampoline requested
    const uint8_t patRipRel[15] = {
        0x48, 0x83, 0xEC, 0x28,                         // sub rsp, 0x28 (4B)
        0x48, 0x8B, 0x05, 0x11, 0x22, 0x33, 0x44,       // mov rax, [rip+0x44332211] (7B)
        0x48, 0x83, 0xC4, 0x28                          // add rsp, 0x28 (4B)
    };
    ExpectTrue(!ValidateHookPrologue64(patRipRel, sizeof(patRipRel), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_RIP_RELATIVE_OPERAND") == 0 && res.faultOffset == 4,
               "Hde64Validator_Refuses_RipRelativeOperand");

    // Early RET (C3 at offset 4) must be refused when trampoline requested
    const uint8_t patEarlyRet[14] = {
        0x48, 0x31, 0xC0, 0x90, 0xC3, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90
    };
    ExpectTrue(!ValidateHookPrologue64(patEarlyRet, sizeof(patEarlyRet), true, &res) &&
               strcmp(res.refusalReason, "REFUSED_EARLY_RET_OR_INT3") == 0 && res.faultOffset == 4,
               "Hde64Validator_Refuses_EarlyRetBeforeStolenBytes");

    // Mid-instruction split: CArchive::Open is 15 bytes (5B + 5B + 5B). Passing stolenBytes=14 splits the 3rd instruction!
    const uint8_t patOpen15[15] = {
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x6C, 0x24, 0x10,
        0x48, 0x89, 0x74, 0x24, 0x18
    };
    ExpectTrue(!ValidateHookPrologue64(patOpen15, 14, true, &res) &&
               strcmp(res.refusalReason, "REFUSED_MID_INSTRUCTION_SPLIT") == 0 && res.faultOffset == 10,
               "Hde64Validator_Refuses_MidInstructionSplitAt14Bytes");
    ExpectTrue(ValidateHookPrologue64(patOpen15, 15, true, &res) && res.decodedBytes == 15,
               "Hde64Validator_Accepts_Exact15ByteInstructionBoundary");
    ExpectTrue(ComputeSafeHookLength64(patOpen15, sizeof(patOpen15), 14) == 15,
               "ComputeSafeHookLength64_RoundsUpTo15ByteBoundary");

    // Real K2 trampoline-dependent prologues must pass
    const uint8_t patExecStack18[18] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x56, 0x57, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x50
    };
    const uint8_t patContainsFile16[16] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56
    };
    ExpectTrue(ValidateHookPrologue64(patExecStack18, sizeof(patExecStack18), true, &res),
               "Hde64Validator_Accepts_K2_LuaExecuteStack18");
    ExpectTrue(ValidateHookPrologue64(patContainsFile16, sizeof(patContainsFile16), true, &res),
               "Hde64Validator_Accepts_K2_CArchiveContainsFile16");

    // End-to-end InstallManagedInlineHook64 check on executable scratch page:
    // 1) Attempting to hook patIsOpen with non-null ppOrigTrampoline is REFUSED.
    // 2) Hooking patIsOpen with ppOrigTrampoline == nullptr SUCCEEDS and restores cleanly on toggle off.
    uint8_t* pScratch = reinterpret_cast<uint8_t*>(
        VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (pScratch) {
        memcpy(pScratch + 0x200, patIsOpen, sizeof(patIsOpen));
        void* pRejectedTramp = reinterpret_cast<void*>(0x1);
        bool badHookInstalled = InstallManagedInlineHook64(
            "unit_hde64", "BadTrampolineIsOpen", pScratch + 0x200, pScratch + 0x400,
            &pRejectedTramp, sizeof(patIsOpen), patIsOpen, sizeof(patIsOpen));
        ExpectTrue(!badHookInstalled && pRejectedTramp == nullptr,
                   "InstallManagedInlineHook64_RefusesTrampolineOnConditionalBranchPrologue");

        bool fullReplaceInstalled = InstallManagedInlineHook64(
            "unit_hde64", "FullReplacementIsOpen", pScratch + 0x200, pScratch + 0x400,
            nullptr, sizeof(patIsOpen), patIsOpen, sizeof(patIsOpen));
        ExpectTrue(fullReplaceInstalled && pScratch[0x200] == 0xFF && pScratch[0x201] == 0x25,
                   "InstallManagedInlineHook64_AllowsFullReplacementWhenTrampolineNull");

        ExecuteHookToggle("unit_hde64", "off", nullptr, 0, true);
        ExpectTrue(memcmp(pScratch + 0x200, patIsOpen, sizeof(patIsOpen)) == 0,
                   "InstallManagedInlineHook64_RestoresFullReplacementBytesOnToggleOff");
        VirtualFree(pScratch, 0, MEM_RELEASE);
    }
}

// ============================================================================
// Protects: Multi-monitor cursor confinement (0..clientW, 0..clientH across
//           positive/negative origins), Windowed title-bar drag/close safety,
//           escape hotkeys, headless release, and startup_headless.cfg write isolation.
// Fails when: Cursor clips to non-client frame, blocks upward y==0 edge-pan,
//             traps mouse on HTCAPTION click/WM_ENTERSIZEMOVE, clips headless windows,
//             or allows headless GENERIC_WRITE to overwrite user's startup.cfg.
// Seam: Real compiled cursor_confinement.cpp module linked into version.dll.
// ============================================================================
void TestCursorConfinementStateMachineAndHeadlessConfigIsolation() {
    Kongor::CursorStateMachine sm;
    sm.SetHeadless(false);
    sm.SetConstrainCvar(true);

    // 1. Secondary right monitor at negative Y offset (DisplayLink 1920, -313, 1920x1080)
    sm.SetClientGeometry(1920, -313, 1920, 1080);
    sm.OnActivate(WA_ACTIVE, HTCLIENT);
    auto d1 = sm.Evaluate();
    ExpectTrue(d1.shouldConfine &&
               d1.reason == Kongor::CursorClipReason::Confined &&
               d1.clipRect.left == 1920 &&
               d1.clipRect.top == -313 &&
               d1.clipRect.right == 3840 &&
               d1.clipRect.bottom == 767,
               "CursorStateMachine_ConfinesExactClientRect_DisplayLinkNegativeY");

    // 2. Left monitor with negative X origin (-2560, 0, 2560x1440) preserving 0px top margin
    sm.SetClientGeometry(-2560, 0, 2560, 1440);
    auto dNegX = sm.Evaluate();
    ExpectTrue(dNegX.shouldConfine &&
               dNegX.clipRect.left == -2560 &&
               dNegX.clipRect.top == 0 &&
               dNegX.clipRect.right == 0 &&
               dNegX.clipRect.bottom == 1440,
               "CursorStateMachine_ConfinesNegativeXMonitor_PreservesZeroTopMarginForEdgePan");

    // 3. Escape key (VK_LWIN) releases immediately and latches until client click or non-click activation
    ExpectTrue(sm.OnKeyDown(VK_LWIN, false, false),
               "CursorStateMachine_OnKeyDown_VkLwinTriggersEscape");
    auto dEsc = sm.Evaluate();
    ExpectTrue(!dEsc.shouldConfine && dEsc.reason == Kongor::CursorClipReason::EscapeLatched,
               "CursorStateMachine_VkLwinReleasesAndLatchesEscape");

    // 4. Clicking Windowed title bar (WA_CLICKACTIVE + HTCAPTION) MUST NOT re-trap cursor!
    sm.OnFocusLost();
    sm.OnActivate(WA_CLICKACTIVE, HTCAPTION);
    auto dTitleClick = sm.Evaluate();
    ExpectTrue(!dTitleClick.shouldConfine && dTitleClick.reason == Kongor::CursorClipReason::EscapeLatched,
               "CursorStateMachine_ClickActiveOnTitleBar_DoesNotConfineCursor");

    // 5. Clicking inside client area (WM_LBUTTONDOWN / OnClientMouseButtonDown) clears latch & re-confines
    sm.OnClientMouseButtonDown();
    auto dClientClick = sm.Evaluate();
    ExpectTrue(dClientClick.shouldConfine && dClientClick.reason == Kongor::CursorClipReason::Confined,
               "CursorStateMachine_ClientMouseDown_ClearsLatchAndReconfines");

    // 6. Drag/Resize modal loop (WM_ENTERSIZEMOVE .. WM_EXITSIZEMOVE) suspends confinement cleanly
    sm.OnEnterSizeMoveOrMenu();
    auto dDrag = sm.Evaluate();
    ExpectTrue(!dDrag.shouldConfine && dDrag.reason == Kongor::CursorClipReason::SizeMoveOrMenu,
               "CursorStateMachine_EnterSizeMove_ReleasesForWindowDrag");
    sm.SetClientGeometry(100, 120, 1600, 900);
    sm.OnExitSizeMoveOrMenu();
    auto dAfterDrag = sm.Evaluate();
    ExpectTrue(dAfterDrag.shouldConfine &&
               dAfterDrag.clipRect.left == 100 &&
               dAfterDrag.clipRect.top == 120 &&
               dAfterDrag.clipRect.right == 1700 &&
               dAfterDrag.clipRect.bottom == 1020,
               "CursorStateMachine_ExitSizeMove_ReconfinesToUpdatedWindowedClientRect");

    // 7. Alt+Tab, Ctrl+Esc, Alt+Space escape shortcuts, while regular gameplay keys (Q/W/E/R/Alt+Q) do not escape
    ExpectTrue(!sm.OnKeyDown('Q', true, false) && !sm.OnKeyDown(VK_SPACE, false, false),
               "CursorStateMachine_GameplayKeysAndAltQ_DoNotReleaseCursor");
    ExpectTrue(Kongor::IsEscapeShortcutKey(VK_TAB, true, false) &&
               Kongor::IsEscapeShortcutKey(VK_ESCAPE, false, true) &&
               Kongor::IsEscapeShortcutKey(VK_SPACE, true, false),
               "IsEscapeShortcutKey_RecognizesAltTabCtrlEscAltSpace");

    // 8. cg_constrainCursor CVar false disables confinement; true re-enables
    ExpectTrue(!Kongor::ParseConstrainCursorBoolW(L"\"false\"", true) &&
               !Kongor::ParseConstrainCursorBoolW(L"0", true) &&
               Kongor::ParseConstrainCursorBoolW(L"\"true\"", false) &&
               Kongor::ParseConstrainCursorBoolW(L"1", false),
               "ParseConstrainCursorBoolW_ParsesQuotedAndNumericValues");
    sm.SetConstrainCvar(false);
    ExpectTrue(!sm.Evaluate().shouldConfine &&
               sm.Evaluate().reason == Kongor::CursorClipReason::DisabledByCvar,
               "CursorStateMachine_CvarFalse_ReleasesCursor");
    sm.SetConstrainCvar(true);

    // 9. Offscreen (-32000, -32000) or Headless flag unconditionally prevents cursor confinement
    sm.SetClientGeometry(-32000, -32000, 1280, 720);
    ExpectTrue(!sm.Evaluate().shouldConfine &&
               sm.Evaluate().reason == Kongor::CursorClipReason::HeadlessOrOffscreen,
               "CursorStateMachine_OffscreenCoordinates_NeverConfines");
    sm.SetClientGeometry(0, 0, 1920, 1080);
    sm.SetHeadless(true);
    ExpectTrue(!sm.Evaluate().shouldConfine &&
               sm.Evaluate().reason == Kongor::CursorClipReason::HeadlessOrOffscreen,
               "CursorStateMachine_HeadlessFlag_NeverConfines");

    // 10. Headless startup.cfg write redirection (UTF-16 & ANSI)
    wchar_t outW[MAX_PATH] = {0};
    bool redirWriteW = Kongor::TryRedirectHeadlessStartupCfgWriteW(
        true,
        L"C:\\ProjectKongor\\games\\client\\userdata\\Heroes of Newerth x64\\override\\startup.cfg",
        GENERIC_WRITE,
        outW,
        MAX_PATH);
    ExpectTrue(redirWriteW &&
               wcscmp(outW, L"C:\\ProjectKongor\\games\\client\\userdata\\Heroes of Newerth x64\\override\\startup_headless.cfg") == 0,
               "TryRedirectHeadlessStartupCfgWriteW_RedirectsHeadlessWriteToStartupHeadlessCfg");

    ExpectTrue(!Kongor::TryRedirectHeadlessStartupCfgWriteW(
                   true,
                   L"C:\\ProjectKongor\\games\\client\\userdata\\Heroes of Newerth x64\\override\\startup.cfg",
                   GENERIC_READ,
                   outW,
                   MAX_PATH),
               "TryRedirectHeadlessStartupCfgWriteW_AllowsReadOnlyOpenUntouched");

    ExpectTrue(!Kongor::TryRedirectHeadlessStartupCfgWriteW(
                   false,
                   L"C:\\ProjectKongor\\games\\client\\userdata\\Heroes of Newerth x64\\override\\startup.cfg",
                   GENERIC_WRITE,
                   outW,
                   MAX_PATH),
               "TryRedirectHeadlessStartupCfgWriteW_LeavesForegroundClientWritesUntouched");

    char outA[MAX_PATH] = {0};
    bool redirWriteA = Kongor::TryRedirectHeadlessStartupCfgWriteA(
        true,
        "userdata/Heroes of Newerth x64/override/STARTUP.CFG",
        FILE_WRITE_DATA,
        outA,
        MAX_PATH);
    ExpectTrue(redirWriteA &&
               strcmp(outA, "userdata/Heroes of Newerth x64/override/startup_headless.cfg") == 0,
               "TryRedirectHeadlessStartupCfgWriteA_CaseInsensitiveForwardSlashRedirect");

    // 11. Multi-client slot isolation via KONGOR_USERDATA_DIR
    SetEnvironmentVariableW(L"KONGOR_USERDATA_DIR", L"userdata_slot_1");
    wchar_t userSlotW[MAX_PATH] = {0};
    bool redirUserW = Kongor::TryRedirectUserdataDirW(
        L"C:\\ProjectKongor\\games\\client\\userdata\\startup.cfg",
        GENERIC_WRITE,
        userSlotW,
        MAX_PATH);
    ExpectTrue(redirUserW &&
               wcscmp(userSlotW, L"C:\\ProjectKongor\\games\\client\\userdata_slot_1\\startup.cfg") == 0,
               "TryRedirectUserdataDirW_RedirectsUserdataWriteToSlotDirectory");

    // Relative path with forward slash
    char userSlotA[MAX_PATH] = {0};
    bool redirUserA = Kongor::TryRedirectUserdataDirA(
        "userdata/logs/console.log",
        FILE_WRITE_DATA,
        userSlotA,
        MAX_PATH);
    ExpectTrue(redirUserA &&
               strcmp(userSlotA, "userdata_slot_1/logs/console.log") == 0,
               "TryRedirectUserdataDirA_RedirectsRelativeForwardSlashPath");

    // userdataKONGOR must NOT be redirected (boundary check)
    bool redirKongor = Kongor::TryRedirectUserdataDirA(
        "userdataKONGOR/manifest.json",
        GENERIC_READ,
        userSlotA,
        MAX_PATH);
    ExpectTrue(!redirKongor, "TryRedirectUserdataDirA_DoesNotMatchUserdataKongor");

    // Read-only shadercache read-through fallback when slot-specific does not exist
    bool redirShaderRead = Kongor::TryRedirectUserdataDirW(
        L"userdata\\shadercache.s2z",
        GENERIC_READ,
        userSlotW,
        MAX_PATH);
    ExpectTrue(!redirShaderRead, "TryRedirectUserdataDirW_ShadercacheReadOnlyFallbackLeavesBaseUntouched");

    // Clean up environment
    SetEnvironmentVariableW(L"KONGOR_USERDATA_DIR", NULL);
    bool redirUnset = Kongor::TryRedirectUserdataDirW(
        L"userdata\\startup.cfg",
        GENERIC_WRITE,
        userSlotW,
        MAX_PATH);
    ExpectTrue(!redirUnset, "TryRedirectUserdataDirW_ReturnsFalseWhenEnvUnset");
}

// ----------------------------------------------------------------------------
// 9. P2P & Offline Practice Login Nickname Resolution + Authenticated Guard
// ----------------------------------------------------------------------------
void TestOfflineP2pNicknameResolutionAndOnlineGuard() {
    wchar_t resolved[32] = {0};
    KongorOfflineNickSource src = KongorOfflineNickSource::DefaultPlayerFallback;

    // 1. Authenticated online account (acctId > 0) MUST NEVER be overwritten
    bool appliedOnline = Kongor_ResolveOfflinePlayerNickname(
        59u, true, L"SpoofName", L"LoginSpoof", L"ConsoleSpoof", resolved, _countof(resolved), &src);
    ExpectTrue(!appliedOnline && src == KongorOfflineNickSource::PreservedOnlineAccount && resolved[0] == L'\0',
               "OfflineNick_PreservesAuthenticatedOnlineAccount");

    // 2. Session cookie present even if accountId transiently 0xFFFFFFFF MUST NEVER overwrite
    bool appliedCookie = Kongor_ResolveOfflinePlayerNickname(
        0xFFFFFFFFu, true, L"SpoofName", L"LoginSpoof", nullptr, resolved, _countof(resolved), &src);
    ExpectTrue(!appliedCookie && src == KongorOfflineNickSource::PreservedOnlineAccount,
               "OfflineNick_PreservesWhenSessionCookiePresent");

    // 3. Unauthenticated (0xFFFFFFFF): p2p_nickname takes priority over login_name
    bool okP2p = Kongor_ResolveOfflinePlayerNickname(
        0xFFFFFFFFu, false, L"  \"DragonPilot\"  ", L"guccidonut", nullptr, resolved, _countof(resolved), &src);
    ExpectTrue(okP2p && src == KongorOfflineNickSource::P2pNicknameCvar && wcscmp(resolved, L"DragonPilot") == 0,
               "OfflineNick_P2pNicknameTakesPriorityAndTrimsQuotes");

    // 4. Unauthenticated: when p2p_nickname is blank or UnnamedNewbie, falls through to login_name
    bool okLogin = Kongor_ResolveOfflinePlayerNickname(
        0xFFFFFFFFu, false, L"UnnamedNewbie", L"guccidonut", nullptr, resolved, _countof(resolved), &src);
    ExpectTrue(okLogin && src == KongorOfflineNickSource::LoginNameCvar && wcscmp(resolved, L"guccidonut") == 0,
               "OfflineNick_RejectsUnnamedNewbieAndUsesLoginName");

    // 5. Unauthenticated: when both CVars are empty/placeholders, falls back to "Player"
    bool okFallback = Kongor_ResolveOfflinePlayerNickname(
        0u, false, L"   ", L"client_default_name", L"UnnamedPlayer", resolved, _countof(resolved), &src);
    ExpectTrue(okFallback && src == KongorOfflineNickSource::DefaultPlayerFallback && wcscmp(resolved, L"Player") == 0,
               "OfflineNick_BlankAndPlaceholdersFallbackToPlayer");

    // 6. Sanitizes command delimiters (';', '\r', '\n') and clamps >24 char names cleanly
    bool okClamp = Kongor_ResolveOfflinePlayerNickname(
        0xFFFFFFFFu, false, L"AlphaBetaGammaDeltaEpsilonZeta;Quit", L"", nullptr, resolved, _countof(resolved), &src);
    ExpectTrue(okClamp && wcslen(resolved) == 24 && wcscmp(resolved, L"AlphaBetaGammaDeltaEpsil") == 0,
               "OfflineNick_StripsSemicolonSuffixAndClampsTo24Chars");
}

// ----------------------------------------------------------------------------
// 10. Dedicated Server CLI CVar ';'-Delimiter, Telemetry Isolation & K2_SERVER RVA Guard
// ----------------------------------------------------------------------------
void TestDedicatedServerCmdLineCvarAndRvaSafety() {
    char cvarVal[32] = {0};

    // 1. Semicolon-delimited COMPEL -execute string extracts exact port without trailing ";Set"
    Kongor_ExtractCmdLineCvarValue(
        "\"C:\\ProjectKongor\\games\\server\\hon_x64.exe\" -dedicated -execute \"Set svr_port 11236;Set svr_voicePortStart 11736;Set svr_slave 2\"",
        "svr_port",
        cvarVal,
        sizeof(cvarVal));
    ExpectTrue(strcmp(cvarVal, "11236") == 0, "ExtractCmdLineCvarValue_StopsAtSemicolonDelimiter");

    // 2. Dedicated server command line resolves port 11236 cleanly
    int dedPort = Kongor_ResolveDedicatedServerPortFromCommandLine(
        "\"C:\\ProjectKongor\\games\\server\\hon_x64.exe\" -dedicated -noconfig -execute \"Set svr_port 11236;Set svr_voicePortStart 11736\"");
    ExpectTrue(dedPort == 11236, "ResolveDedicatedServerPort_ParsesExecuteSemicolonPort");

    // 3. Dedicated server without explicit port defaults to 11235
    int dedDefault = Kongor_ResolveDedicatedServerPortFromCommandLine(
        "\"C:\\ProjectKongor\\games\\server\\hon_x64.exe\" -dedicated -masterserver 127.0.0.1:55555");
    ExpectTrue(dedDefault == 11235, "ResolveDedicatedServerPort_DefaultsTo11235WhenDedicated");

    // 4. Client process (no -dedicated / -server flag) MUST return 0 even when +Set svr_port is present
    //    or when directory path contains the word "server"
    int clientPracticePort = Kongor_ResolveDedicatedServerPortFromCommandLine(
        "\"C:\\ProjectKongor\\games\\client\\hon_x64.exe\" +Set svr_port 11580 +Connect 127.0.0.1:11580");
    ExpectTrue(clientPracticePort == 0, "ResolveDedicatedServerPort_ClientProcessWithSvrPortReturnsZero");

    int clientPathSubstringPort = Kongor_ResolveDedicatedServerPortFromCommandLine(
        "\"C:\\hon_server_test\\client\\hon_x64.exe\" +Set svr_port 11550");
    ExpectTrue(clientPathSubstringPort == 0, "ResolveDedicatedServerPort_RejectsPathSubstringWithoutDedicatedFlag");

    // 5. K2_SERVER PE SizeOfImage guard blocks client RVA fallbacks on dedicated server binaries
    ExpectTrue(Kongor_AllowClientRvaFallback(false, kKongorK2ClientSizeOfImage),
               "AllowClientRvaFallback_AcceptsK2ClientDll");
    ExpectTrue(Kongor_AllowClientRvaFallback(false, kKongorGameSharedClientSizeOfImage),
               "AllowClientRvaFallback_AcceptsGameSharedClientDll");
    ExpectTrue(!Kongor_AllowClientRvaFallback(false, kKongorK2ServerSizeOfImage),
               "AllowClientRvaFallback_RejectsK2ServerDllBySizeOfImage");
    ExpectTrue(!Kongor_AllowClientRvaFallback(false, kKongorGameSharedServerSizeOfImage),
               "AllowClientRvaFallback_RejectsGameSharedServerDllBySizeOfImage");
    ExpectTrue(!Kongor_AllowClientRvaFallback(true, kKongorK2ClientSizeOfImage),
               "AllowClientRvaFallback_RejectsDedicatedProcessEvenIfClientDll");
}

// ----------------------------------------------------------------------------
// 11. Phase 7 Client Dynamic Ephemeral UDP Port Allocator & Bare-Loopback Rewrite
// ----------------------------------------------------------------------------
void TestDynamicClientPortAllocationAndConnectRewrite() {
    char stem[64] = {0};
    ExpectTrue(Kongor_ExtractMapNameFromConsoleCmdW(
                   L"StartGame practice TestMatch \"map:devowars mode:forcepick teamsize:5\"", stem, sizeof(stem)) &&
               strcmp(stem, "devowars") == 0,
               "ExtractMapNameFromCmd_ParsesStartGameMapToken");
    ExpectTrue(Kongor_ExtractMapNameFromConsoleCmdW(
                   L"host_map midwars.s2z;Set svr_gameFPS 20", stem, sizeof(stem)) &&
               strcmp(stem, "midwars") == 0,
               "ExtractMapNameFromCmd_ParsesHostMapAndStripsS2z");

    bool isBot = false;
    size_t startIdx = 999;

    // 1. Practice vs BotMatch command classification & compound ';' prefix offset extraction
    ExpectTrue(Kongor_IsPracticeStartGameCommandW(
                   L"StartGame practice \"Kongor Practice\" \"map:caldavar mode:normal\"", &isBot, &startIdx) &&
               !isBot && startIdx == 0,
               "IsPracticeStartGame_DetectsStandardPracticeAtOffset0");
    ExpectTrue(Kongor_IsPracticeStartGameCommandW(
                   L"StartGame practice LocalBotsGame mode:botmatch map:midwars randombots:4|5", &isBot, &startIdx) &&
               isBot && startIdx == 0,
               "IsPracticeStartGame_DetectsBotMatchCommand");
    const wchar_t* compoundCmd = L"Set svr_port 11235; Set dev true; StartGame practice KongorSandbox \"map:devowars mode:forcepick\"";
    ExpectTrue(Kongor_IsPracticeStartGameCommandW(compoundCmd, &isBot, &startIdx) &&
               !isBot && wcsncmp(compoundCmd + startIdx, L"StartGame", 9) == 0,
               "IsPracticeStartGame_ExtractsExactOffsetAfterSemicolonPrefix");
    ExpectTrue(Kongor_IsPracticeStartGameCommandW(
                   L"StartGame local \"LocalLobby\" \"map:caldavar mode:normal\"", &isBot, &startIdx) &&
               !isBot && startIdx == 0,
               "IsPracticeStartGame_DetectsStartGameLocalLobby");
    ExpectTrue(!Kongor_IsPracticeStartGameCommandW(
                   L"StartGame remote_automatic \"OnlineGame\" \"map:caldavar\"", &isBot, &startIdx),
               "IsPracticeStartGame_RejectsRemoteAutomatic");
    ExpectTrue(!Kongor_IsPracticeStartGameCommandW(
                   L"SendCreateGameRequest \"PublicGame\" \"map:caldavar\"", &isBot, &startIdx),
               "IsPracticeStartGame_RejectsPublicCreateGameRequest");

    // 1b. StartReplay command classification & local session teardown detection (Disconnect / StopReplay)
    bool isReplay = false;
    ExpectTrue(Kongor_IsPracticeStartGameCommandW(
                   L"StartReplay ~/replays/M1076.honreplay", &isBot, &startIdx, &isReplay) &&
               !isBot && isReplay && startIdx == 0,
               "IsPracticeStartGame_DetectsStartReplayWithFileArgAtOffset0");
    const wchar_t* compoundReplayCmd = L"Set host_timescale 1; StartReplay \"~/replays/M1076.honreplay\"";
    ExpectTrue(Kongor_IsPracticeStartGameCommandW(compoundReplayCmd, &isBot, &startIdx, &isReplay) &&
               !isBot && isReplay && wcsncmp(compoundReplayCmd + startIdx, L"StartReplay", 11) == 0,
               "IsPracticeStartGame_ExtractsStartReplayAfterSemicolonPrefix");
    ExpectTrue(!Kongor_IsPracticeStartGameCommandW(
                   L"StartReplay   ", &isBot, &startIdx, &isReplay),
               "IsPracticeStartGame_RejectsBareStartReplayWithoutFilename");
    ExpectTrue(Kongor_IsClientLocalSessionEndCommandW(L"Disconnect"),
               "IsClientLocalSessionEnd_DetectsDisconnect");
    ExpectTrue(Kongor_IsClientLocalSessionEndCommandW(L"Set host_timescale 1; StopReplay"),
               "IsClientLocalSessionEnd_DetectsStopReplay");
    ExpectTrue(!Kongor_IsClientLocalSessionEndCommandW(L"Disconnect; StartReplay ~/replays/M1076.honreplay"),
               "IsClientLocalSessionEnd_IgnoresCompoundDisconnectPlusStartReplay");

    // 2. Real Winsock UDP port availability & automatic collision skip
    ExpectTrue(Kongor_EnsureWinsockInitialized(), "Winsock_InitializesCleanly");
    SOCKET blocker = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    ExpectTrue(blocker != INVALID_SOCKET, "UdpPortProbe_CreatesBlockerSocket");
    if (blocker != INVALID_SOCKET) {
        // Bind an ephemeral OS-chosen UDP port on 127.0.0.1 and verify Kongor_SelectAvailableUdpPortInBands skips it!
        sockaddr_in bindAddr{};
        bindAddr.sin_family = AF_INET;
        bindAddr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        bindAddr.sin_port = htons(0);
        int bindRc = bind(blocker, reinterpret_cast<const sockaddr*>(&bindAddr), sizeof(bindAddr));
        int nameLen = sizeof(bindAddr);
        int nameRc = getsockname(blocker, reinterpret_cast<sockaddr*>(&bindAddr), &nameLen);
        const uint16_t occupiedPort = ntohs(bindAddr.sin_port);

        ExpectTrue(bindRc == 0 && nameRc == 0 && occupiedPort > 0,
                   "UdpPortProbe_BoundEphemeralLoopbackBlocker");
        ExpectTrue(!Kongor_IsUdpPortAvailableOnHost(occupiedPort),
                   "UdpPortProbe_DetectsOccupiedLoopbackSocketViaExclusiveAddrUse");

        if (occupiedPort < 65530) {
            const uint16_t selected = Kongor_SelectAvailableUdpPortInBands(
                occupiedPort, static_cast<uint16_t>(occupiedPort + 5),
                kKongorPracticeFallbackStart, kKongorPracticeFallbackEnd);
            ExpectTrue(selected != occupiedPort && selected >= occupiedPort + 1,
                       "UdpPortProbe_SkipsOccupiedStartPortToNextFreeSlot");

            const uint16_t voiceSelected = Kongor_SelectAvailableUdpPortInBands(
                selected, static_cast<uint16_t>(selected + 4),
                kKongorClientVoiceFallbackStart, kKongorClientVoiceFallbackEnd,
                selected);
            ExpectTrue(voiceSelected != selected,
                       "UdpPortProbe_HonorsAvoidPortGuardBetweenGameAndVoice");
        }
        closesocket(blocker);
    }

    // 3. Bare-localhost / 127.0.0.1 Connect rewrite vs explicit host:port preservation
    wchar_t rewritten[64] = {0};
    ExpectTrue(Kongor_RewriteBareLoopbackConnectTargetW(L"localhost", 11580, rewritten, _countof(rewritten)) &&
               wcscmp(rewritten, L"127.0.0.1:11580") == 0,
               "ConnectRewrite_RewritesBareLocalhostToActivePracticePort");
    ExpectTrue(Kongor_RewriteBareLoopbackConnectTargetW(L"  127.0.0.1  ", 11590, rewritten, _countof(rewritten)) &&
               wcscmp(rewritten, L"127.0.0.1:11590") == 0,
               "ConnectRewrite_RewritesBareLoopbackToActiveBotMatchPort");
    ExpectTrue(!Kongor_RewriteBareLoopbackConnectTargetW(L"localhost", 0, rewritten, _countof(rewritten)),
               "ConnectRewrite_NoOpWhenNoActiveClientLocalServer");
    ExpectTrue(!Kongor_RewriteBareLoopbackConnectTargetW(L"127.0.0.1:11235", 11580, rewritten, _countof(rewritten)),
               "ConnectRewrite_PreservesExplicitLocalCompelServerPort11235");
    ExpectTrue(!Kongor_RewriteBareLoopbackConnectTargetW(L"127.0.0.1:11550", 11580, rewritten, _countof(rewritten)),
               "ConnectRewrite_PreservesExplicitP2pHostPort11550");
    ExpectTrue(!Kongor_RewriteBareLoopbackConnectTargetW(L"192.168.1.42:11236", 11580, rewritten, _countof(rewritten)),
               "ConnectRewrite_PreservesExplicitLanDedicatedServerTarget");
}

static void TestWave5SoundNegCacheDraw2dStubAndReconnect6f() {
    // 1. HDE64 Prologue Boundary Verification for CSoundManager::LoadSample (+0x9E7260) and 0x6f (+0x8CD7C0)
    static const uint8_t patSoundLoadSample15[15] = {
        0x48, 0x8B, 0xC4,
        0x48, 0x89, 0x58, 0x10,
        0x44, 0x89, 0x40, 0x18,
        0x48, 0x89, 0x48, 0x08
    };
    static const uint8_t patReconnect6f15[15] = {
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x74, 0x24, 0x10,
        0x48, 0x89, 0x7C, 0x24, 0x20
    };
    Kongor::PatchController::PrologueValidationResult res{};
    ExpectTrue(Kongor::PatchController::ValidateHookPrologue64(patSoundLoadSample15, 15, true, &res) &&
               res.decodedBytes == 15,
               "Wave5_Hde64_LoadSamplePrologueIsExact15BytesRipFree");
    ExpectTrue(Kongor::PatchController::ValidateHookPrologue64(patReconnect6f15, 15, true, &res) &&
               res.decodedBytes == 15,
               "Wave5_Hde64_Reconnect6fPrologueIsExact15BytesRipFree");

    // 2. CSoundManager::LoadSample Negative Cache: m_bInitialized (+0x20) guard, case/slash normalization, & reload clear
    auto pNegTable = std::make_unique<Kongor_SoundNegCacheTable>();
    uint8_t fakeSoundMgr[64] = {0}; // +0x20 == 0 (uninitialized)
    wchar_t normA[Kongor_SoundNegCacheTable::kMaxPathW] = {0};
    wchar_t normB[Kongor_SoundNegCacheTable::kMaxPathW] = {0};
    uint64_t hashA = 0, hashB = 0;

    ExpectTrue(Kongor_NormalizeSoundCachePathW(
                   L"\\Heroes\\Predator\\Alt13\\Sounds\\Death_Grunt.wav",
                   190, 0x40, normA, _countof(normA), &hashA),
               "Wave5_SoundNegCache_NormalizesMixedCaseAndBackslashes");
    ExpectTrue(Kongor_NormalizeSoundCachePathW(
                   L"/heroes/predator/alt13/sounds/death_grunt.wav",
                   190, 0x40, normB, _countof(normB), &hashB) &&
               hashA == hashB && wcscmp(normA, normB) == 0,
               "Wave5_SoundNegCache_CanonicalizesEquivalentPathsToIdenticalKey");

    // Must NOT insert while CSoundManager::m_bInitialized (+0x20) == 0!
    ExpectTrue(!pNegTable->InsertIfInitialized(fakeSoundMgr, hashA, 0x40, normA),
               "Wave5_SoundNegCache_SkipsInsertBeforeSoundManagerInitialized");
    ExpectTrue(!pNegTable->Lookup(hashA, 0x40, normA),
               "Wave5_SoundNegCache_LookupMissWhenPreInitSkipped");

    // Flip m_bInitialized (+0x20) = 1 -> insert & hit!
    fakeSoundMgr[0x20] = 1;
    ExpectTrue(pNegTable->InsertIfInitialized(fakeSoundMgr, hashA, 0x40, normA),
               "Wave5_SoundNegCache_InsertsMissingSampleWhenInitialized");
    ExpectTrue(pNegTable->Lookup(hashA, 0x40, normB),
               "Wave5_SoundNegCache_LookupHitsNormalizedPathAndMatchingFlags");
    ExpectTrue(!pNegTable->Lookup(hashA, 0x00, normB),
               "Wave5_SoundNegCache_DistinguishesSampleLoadFlags");

    // Reload command detection & cache invalidation
    ExpectTrue(Kongor_ShouldInvalidateSoundNegCacheCommandW(L"Set host_videoDriver vid_gl2; ReloadRes"),
               "Wave5_SoundNegCache_DetectsReloadResInvalidationCommand");
    ExpectTrue(Kongor_ShouldInvalidateSoundNegCacheCommandW(L"SoundRestart"),
               "Wave5_SoundNegCache_DetectsSoundRestartInvalidationCommand");
    ExpectTrue(!Kongor_ShouldInvalidateSoundNegCacheCommandW(L"Echo hello; Ping"),
               "Wave5_SoundNegCache_IgnoresUnrelatedConsoleCommands");
    ExpectTrue(pNegTable->Clear() == 1U, "Wave5_SoundNegCache_ClearEvictsCachedEntries");
    ExpectTrue(!pNegTable->Lookup(hashA, 0x40, normB),
               "Wave5_SoundNegCache_LookupMissAfterReloadClear");

    // 3. Live CPU Execution of 70-byte D3D_Draw2D Diffuse Sampler OOB Machine-Code Stub
    // Allocate a single 4KB executable test arena holding:
    //   [0x000..0x045] : 70-byte Kongor_BuildDraw2dDiffuseGuardStub
    //   [0x100..0x11F] : resumeNormal landing pad (captures RBX/RDX/ESI/EDI into RCX struct, returns 1)
    //   [0x140..0x15F] : resumeSkip landing pad   (captures RBX/RDX/ESI/EDI into RCX struct, returns 2)
    //   [0x180..0x1BF] : Win64 ABI caller thunk (uint32_t __fastcall Invoke(OutState* rcx, uint32_t r14d_edx, uint32_t r15d_r8d))
    struct StubCpuCapture {
        uint64_t outRbx;
        uint64_t outRdx;
        uint32_t outEsi;
        uint32_t outEdi;
    };
    uint8_t* pExecPage = reinterpret_cast<uint8_t*>(
        VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    ExpectTrue(pExecPage != nullptr, "Wave5_Draw2dStub_AllocatedExecutablePage");
    if (pExecPage) {
        memset(pExecPage, 0xCC, 4096);
        volatile uint64_t skipCounter = 0;
        const uintptr_t fakeVidBase   = 0x00007FF712340000ULL;
        const uintptr_t resumeNormal  = reinterpret_cast<uintptr_t>(pExecPage + 0x100);
        const uintptr_t resumeSkip    = reinterpret_cast<uintptr_t>(pExecPage + 0x140);

        ExpectTrue(Kongor_BuildDraw2dDiffuseGuardStub(
                       fakeVidBase, resumeNormal, resumeSkip, &skipCounter, pExecPage, kKongorDraw2dDiffuseStubSize),
                   "Wave5_Draw2dStub_Builds70ByteStub");

        // Landing pad helper: writes rbx->[rcx+0], rdx->[rcx+8], esi->[rcx+16], edi->[rcx+20], restores non-volatiles, ret
        auto emitLandingPad = [](uint8_t* dst, uint8_t retVal) {
            const uint8_t pad[] = {
                0x48, 0x89, 0x19,             // mov [rcx], rbx
                0x48, 0x89, 0x51, 0x08,       // mov [rcx+8], rdx
                0x89, 0x71, 0x10,             // mov [rcx+16], esi
                0x89, 0x79, 0x14,             // mov [rcx+20], edi
                0xB8, retVal, 0x00, 0x00, 0x00, // mov eax, retVal
                0x41, 0x5F,                   // pop r15
                0x41, 0x5E,                   // pop r14
                0x5F,                         // pop rdi
                0x5E,                         // pop rsi
                0x5B,                         // pop rbx
                0xC3                          // ret
            };
            memcpy(dst, pad, sizeof(pad));
        };
        emitLandingPad(pExecPage + 0x100, 1);
        emitLandingPad(pExecPage + 0x140, 2);

        // Caller thunk at +0x180: saves rbx/rsi/rdi/r14/r15, sets r14d=edx, r15d=r8d, jumps to stub at +0x000
        const uint8_t callerThunk[] = {
            0x53,                         // push rbx
            0x56,                         // push rsi
            0x57,                         // push rdi
            0x41, 0x56,                   // push r14
            0x41, 0x57,                   // push r15
            0x44, 0x8B, 0xF2,             // mov r14d, edx
            0x45, 0x8B, 0xF8,             // mov r15d, r8d
            0xE9, 0x6C, 0xFE, 0xFF, 0xFF  // jmp rel32 -> 0x000 (0x180 + 18 = 0x192 -> -0x192 = 0xFFFFFE6E? compute exact below!)
        };
        memcpy(pExecPage + 0x180, callerThunk, sizeof(callerThunk));
        const int32_t relToStub = static_cast<int32_t>(0 - (0x180 + static_cast<int>(sizeof(callerThunk))));
        memcpy(pExecPage + 0x180 + sizeof(callerThunk) - 4, &relToStub, 4);

        DWORD oldProt = 0;
        VirtualProtect(pExecPage, 4096, PAGE_EXECUTE_READ, &oldProt);
        FlushInstructionCache(GetCurrentProcess(), pExecPage, 4096);

        typedef uint32_t (__fastcall *FnInvokeStub)(StubCpuCapture* outCap, uint32_t r14Val, uint32_t r15Val);
        FnInvokeStub invokeStub = reinterpret_cast<FnInvokeStub>(pExecPage + 0x180);

        // Case A: valid stage index R15D = 3 -> resumes at resumeNormal (1), RBX = 3 * 0x34 = 0x9C, RDX = fakeVidBase
        StubCpuCapture capValid{};
        uint32_t rcValid = invokeStub(&capValid, 0x0205U, 3U);
        ExpectTrue(rcValid == 1U, "Wave5_Draw2dStub_ValidStage3ResumesNormal");
        ExpectTrue(capValid.outRbx == (3ULL * 0x34ULL) &&
                   capValid.outRdx == fakeVidBase &&
                   capValid.outEsi == 0x0205U &&
                   capValid.outEdi == 3U &&
                   skipCounter == 0ULL,
                   "Wave5_Draw2dStub_ValidStage3ComputesExactOffsetAndBase");

        // Case B: crash vector R15D = 0xFFFFFFFF -> skips to resumeSkip (2), increments skipCounter, preserves RDX = fakeVidBase
        StubCpuCapture capCrash{};
        uint32_t rcCrash = invokeStub(&capCrash, 0x0201U, 0xFFFFFFFFU);
        ExpectTrue(rcCrash == 2U, "Wave5_Draw2dStub_UnboundMinusOneSkipsSafelyTo0x29355");
        ExpectTrue(capCrash.outRbx == 0ULL &&
                   capCrash.outRdx == fakeVidBase &&
                   capCrash.outEsi == 0x0201U &&
                   capCrash.outEdi == 0xFFFFFFFFU &&
                   skipCounter == 1ULL,
                   "Wave5_Draw2dStub_UnboundMinusOneIncrementsCounterAndPreservesRegisters");

        // Case C: out-of-range stage R15D = 16 -> skips to resumeSkip (2)
        StubCpuCapture capOob16{};
        uint32_t rcOob16 = invokeStub(&capOob16, 0x0000U, 16U);
        ExpectTrue(rcOob16 == 2U, "Wave5_Draw2dStub_Stage16BoundarySkipsSafely");
        ExpectTrue(skipCounter == 2ULL, "Wave5_Draw2dStub_Stage16IncrementsSkipCounter");

        VirtualFree(pExecPage, 0, MEM_RELEASE);
    }

    // 4. 0x6f NETCMD_RECONNECT_STATUS_RESPONSE Trailing-Int Drain Guard
    uint8_t fakePkt[64] = {0};
    // Fill uninitialized padding bytes +0x25..+0x27 with non-zero garbage to prove 1-byte bool check at +0x24!
    fakePkt[0x25] = 0xCC; fakePkt[0x26] = 0xCC; fakePkt[0x27] = 0xCC;

    // 5-byte retail packet already fully read (uiEnd = 5, uiRead = 5, bOverflowed = 0) -> do NOT drain
    *reinterpret_cast<uint32_t*>(fakePkt + 0x1C) = 5U;
    *reinterpret_cast<uint32_t*>(fakePkt + 0x20) = 5U;
    fakePkt[0x24] = 0;
    ExpectTrue(!Kongor_ShouldDrainReconnect6fTrailingInt(fakePkt),
               "Wave5_Reconnect6f_DoesNotDrainStandard5BytePacket");

    // 9-byte extended packet after orig read 5 bytes (uiEnd = 9, uiRead = 5, bOverflowed = 0) -> DRAIN!
    *reinterpret_cast<uint32_t*>(fakePkt + 0x1C) = 9U;
    *reinterpret_cast<uint32_t*>(fakePkt + 0x20) = 5U;
    fakePkt[0x24] = 0;
    ExpectTrue(Kongor_ShouldDrainReconnect6fTrailingInt(fakePkt),
               "Wave5_Reconnect6f_Drains9BytePacketIgnoringBoolPaddingGarbage");

    // Overflowed packet (bOverflowed = 1) -> do NOT drain
    fakePkt[0x24] = 1;
    ExpectTrue(!Kongor_ShouldDrainReconnect6fTrailingInt(fakePkt),
               "Wave5_Reconnect6f_RejectsOverflowedPacket");
}

void TestCommandPipeOwnerRestrictedDaclAndRemoteSmbReject() {
    // protects: Named pipe \\.\pipe\kongor-native-client against NULL-DACL local/SMB command execution
    // fails_when: SetSecurityDescriptorDacl(..., NULL, ...) is used or PIPE_REJECT_REMOTE_CLIENTS is omitted
    // seam: Real Win32 ConvertStringSecurityDescriptorToSecurityDescriptorA + CreateNamedPipeA + CreateFileA
    const DWORD modeFlags = Kongor_GetCommandPipeModeFlags();
    ExpectTrue((modeFlags & PIPE_REJECT_REMOTE_CLIENTS) != 0,
               "CommandPipe_ModeIncludesPipeRejectRemoteClients");

    Kongor_ScopedPipeSecurityDescriptor pipeSec;
    ExpectTrue(pipeSec.IsValid(), "CommandPipe_SecurityDescriptorConstructsCleanly");

    BOOL bDaclPresent = FALSE;
    PACL pDacl = nullptr;
    BOOL bDaclDefaulted = FALSE;
    BOOL gotDacl = ::GetSecurityDescriptorDacl(
        pipeSec.GetSecurityDescriptor(), &bDaclPresent, &pDacl, &bDaclDefaulted);
    ExpectTrue(gotDacl && bDaclPresent && pDacl != nullptr,
               "CommandPipe_SecurityDescriptorHasExplicitNonNullDacl");

    const std::string& sddl = pipeSec.GetSddl();
    ExpectTrue(sddl.rfind("D:P", 0) == 0 &&
               sddl.find("(A;;GA;;;SY)") != std::string::npos &&
               sddl.find("(A;;GA;;;BA)") != std::string::npos &&
               sddl.find("WD") == std::string::npos &&
               sddl.find("AN") == std::string::npos,
               "CommandPipe_SddlIsProtectedAndExcludesEveryoneAndAnonymous");

    std::string testPipeName = "\\\\.\\pipe\\kongor-test-dacl-" + std::to_string(::GetCurrentProcessId());
    HANDLE hServerPipe = ::CreateNamedPipeA(
        testPipeName.c_str(),
        PIPE_ACCESS_DUPLEX,
        modeFlags,
        2,
        4096,
        4096,
        0,
        pipeSec.GetSecurityAttributes());
    ExpectTrue(hServerPipe != INVALID_HANDLE_VALUE,
               "CommandPipe_CreateNamedPipeSucceedsWithRestrictedDacl");

    if (hServerPipe != INVALID_HANDLE_VALUE) {
        HANDLE hClientPipe = ::CreateFileA(
            testPipeName.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            0,
            nullptr);
        ExpectTrue(hClientPipe != INVALID_HANDLE_VALUE,
                   "CommandPipe_SameOwnerProcessCanOpenPipe");
        if (hClientPipe != INVALID_HANDLE_VALUE) {
            ::CloseHandle(hClientPipe);
        }
        ::CloseHandle(hServerPipe);
    }
}

void TestUnifiedNativeTargetsAndOffThreadLuaMemorySafety() {
    // protects: unified CMake proxy/driver DLL exports (kongor_ssl, kongor_crypto, kongor_curl, vid_vulkan)
    //           and off-main-thread safety of Kongor_NativeGuard_GetLuaMemoryUsed()
    // fails_when: any unified target fails to link/export or off-thread Lua memory probe touches uninitialized K2 pointers
    // seam: Win32 PE module loader (LoadLibraryW / LoadLibraryExW + GetProcAddress)
    wchar_t selfExe[MAX_PATH] = {0};
    DWORD len = ::GetModuleFileNameW(nullptr, selfExe, MAX_PATH);
    ExpectTrue(len > 0 && len < MAX_PATH, "UnifiedNative_GetSelfExePath");
    if (len == 0 || len >= MAX_PATH) return;

    std::wstring exePath(selfExe);
    size_t lastSlash = exePath.find_last_of(L"\\/");
    ExpectTrue(lastSlash != std::wstring::npos, "UnifiedNative_SelfExeDirResolved");
    if (lastSlash == std::wstring::npos) return;

    std::wstring cfgDir = exePath.substr(0, lastSlash); // e.g. .../native/build/native_guard_build/Release
    size_t cfgSlash = cfgDir.find_last_of(L"\\/");
    std::wstring cfgName = (cfgSlash != std::wstring::npos) ? cfgDir.substr(cfgSlash + 1) : L"Release";
    std::wstring ngBuildDir = (cfgSlash != std::wstring::npos) ? cfgDir.substr(0, cfgSlash) : cfgDir;
    size_t rootSlash = ngBuildDir.find_last_of(L"\\/");
    std::wstring rootBuildDir = (rootSlash != std::wstring::npos) ? ngBuildDir.substr(0, rootSlash) : ngBuildDir;

    // 1. version.dll off-main-thread Kongor_NativeGuard_GetLuaMemoryUsed() returns 0 safely without touching K2 Lua
    std::wstring versionDllPath = cfgDir + L"\\version.dll";
    if (::GetFileAttributesW(versionDllPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        HMODULE hVer = ::LoadLibraryW(versionDllPath.c_str());
        ExpectTrue(hVer != nullptr, "UnifiedNative_VersionDllLoadsCleanly");
        if (hVer != nullptr) {
            using FnGetLuaMem = uint64_t(*)();
            auto pfnGetLuaMem = reinterpret_cast<FnGetLuaMem>(::GetProcAddress(hVer, "Kongor_NativeGuard_GetLuaMemoryUsed"));
            ExpectTrue(pfnGetLuaMem != nullptr, "UnifiedNative_VersionExportsGetLuaMemoryUsed");
            if (pfnGetLuaMem != nullptr) {
                ExpectTrue(pfnGetLuaMem() == 0ULL, "UnifiedNative_OffThreadGetLuaMemoryUsedReturnsSafeZero");
            }
            ::FreeLibrary(hVer);
        }
    }

    // 2. Unified root CMake sibling targets (present whenever built from native/CMakeLists.txt)
    std::wstring sslDllPath = rootBuildDir + L"\\kongor_ssl_build\\" + cfgName + L"\\kongor_ssl_x64.dll";
    if (::GetFileAttributesW(sslDllPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        HMODULE hSsl = ::LoadLibraryW(sslDllPath.c_str());
        ExpectTrue(hSsl != nullptr, "UnifiedNative_KongorSslLoadsCleanly");
        if (hSsl != nullptr) {
            using FnSslVersion = int(*)(void*);
            auto pfnSslVer = reinterpret_cast<FnSslVersion>(::GetProcAddress(hSsl, "SSL_version"));
            ExpectTrue(::GetProcAddress(hSsl, "SSL_get_ex_data") != nullptr &&
                       ::GetProcAddress(hSsl, "SSL_set_ex_data") != nullptr &&
                       pfnSslVer != nullptr &&
                       pfnSslVer(nullptr) == 0x0304,
                       "UnifiedNative_KongorSslExportsExDataAndTls13Fallback");
            ::FreeLibrary(hSsl);
        }

        std::wstring cryptoDllPath = rootBuildDir + L"\\kongor_crypto_build\\" + cfgName + L"\\kongor_crypto_x64.dll";
        HMODULE hCrypto = ::LoadLibraryW(cryptoDllPath.c_str());
        ExpectTrue(hCrypto != nullptr, "UnifiedNative_KongorCryptoLoadsCleanly");
        if (hCrypto != nullptr) {
            ExpectTrue(::GetProcAddress(hCrypto, "SHA256") != nullptr &&
                       ::GetProcAddress(hCrypto, "BN_mod_exp") != nullptr,
                       "UnifiedNative_KongorCryptoExportsCoreSymbols");
            ::FreeLibrary(hCrypto);
        }

        std::wstring curlDllPath = rootBuildDir + L"\\kongor_curl_build\\" + cfgName + L"\\libcurl_x64.dll";
        HMODULE hCurl = ::LoadLibraryW(curlDllPath.c_str());
        ExpectTrue(hCurl != nullptr, "UnifiedNative_KongorCurlLoadsCleanly");
        if (hCurl != nullptr) {
            using FnCurlGlobalInit = int(*)(long);
            auto pfnGlobalInit = reinterpret_cast<FnCurlGlobalInit>(::GetProcAddress(hCurl, "curl_global_init"));
            ExpectTrue(pfnGlobalInit != nullptr &&
                       ::GetProcAddress(hCurl, "curl_easy_cleanup") != nullptr &&
                       pfnGlobalInit(0) == 0,
                       "UnifiedNative_KongorCurlExportsAndInitOnceSafeWithoutRealCurl");
            ::FreeLibrary(hCurl);
        }

        std::wstring vkDllPath = rootBuildDir + L"\\vid_vulkan_build\\" + cfgName + L"\\vid_vulkan_x64.dll";
        HMODULE hVk = ::LoadLibraryW(vkDllPath.c_str());
        ExpectTrue(hVk != nullptr, "UnifiedNative_VidVulkanLoadsCleanly");
        if (hVk != nullptr) {
            ExpectTrue(::GetProcAddress(hVk, "InitAPIs") != nullptr &&
                       ::GetProcAddress(hVk, "KongorProbeDriverBackend") != nullptr,
                       "UnifiedNative_VidVulkanExportsDriverEntryPoints");
            ::FreeLibrary(hVk);
        }
    }
}

void TestClientHighDpiAwarenessGuards() {
    // protects: Win10 client PerMonitorV2 high-DPI scaling, preventing mouse coordinate desync in Fullscreen
    // fails_when: server/headless/safe mode checks fail to opt out, or env kill-switch is ignored
    // seam: native_guard_internal.h Kongor_EnableClientHighDpiAwareness & Kongor_IsHighDpiDisabledByEnvValue
    ExpectTrue(Kongor_IsHighDpiDisabledByEnvValue("1"), "HighDpi_EnvDisabled_1");
    ExpectTrue(Kongor_IsHighDpiDisabledByEnvValue("true"), "HighDpi_EnvDisabled_true");
    ExpectTrue(Kongor_IsHighDpiDisabledByEnvValue("Y"), "HighDpi_EnvDisabled_Y");
    ExpectTrue(Kongor_IsHighDpiDisabledByEnvValue("yes"), "HighDpi_EnvDisabled_yes");
    ExpectTrue(!Kongor_IsHighDpiDisabledByEnvValue("0"), "HighDpi_EnvEnabled_0");
    ExpectTrue(!Kongor_IsHighDpiDisabledByEnvValue("false"), "HighDpi_EnvEnabled_false");
    ExpectTrue(!Kongor_IsHighDpiDisabledByEnvValue(""), "HighDpi_EnvEmpty");
    ExpectTrue(!Kongor_IsHighDpiDisabledByEnvValue(nullptr), "HighDpi_EnvNull");

    char diag[256] = {0};
    auto resServer = Kongor_EnableClientHighDpiAwareness(true, false, false, "0", diag, sizeof(diag));
    ExpectTrue(resServer == KongorDpiAwarenessResult::SkippedDedicatedServer, "HighDpi_SkipDedicatedServer");

    diag[0] = '\0';
    auto resHeadless = Kongor_EnableClientHighDpiAwareness(false, true, false, "0", diag, sizeof(diag));
    ExpectTrue(resHeadless == KongorDpiAwarenessResult::SkippedHeadless, "HighDpi_SkipHeadless");

    diag[0] = '\0';
    auto resSafe = Kongor_EnableClientHighDpiAwareness(false, false, true, "0", diag, sizeof(diag));
    ExpectTrue(resSafe == KongorDpiAwarenessResult::SkippedSafeMode, "HighDpi_SkipSafeMode");

    diag[0] = '\0';
    auto resEnv = Kongor_EnableClientHighDpiAwareness(false, false, false, "1", diag, sizeof(diag));
    ExpectTrue(resEnv == KongorDpiAwarenessResult::SkippedDisabledByEnv, "HighDpi_SkipDisabledByEnv");

    diag[0] = '\0';
    auto resClean = Kongor_EnableClientHighDpiAwareness(false, false, false, "0", diag, sizeof(diag));
    ExpectTrue(resClean == KongorDpiAwarenessResult::AppliedPerMonitorV2 ||
               resClean == KongorDpiAwarenessResult::AppliedPerMonitorV1 ||
               resClean == KongorDpiAwarenessResult::AppliedShcorePerMonitor ||
               resClean == KongorDpiAwarenessResult::AppliedSystemDpiAware ||
               resClean == KongorDpiAwarenessResult::AlreadySetOrUnavailable,
               "HighDpi_ClientExecutionSucceedsOrCleanUnavailable");
    ExpectTrue(diag[0] != '\0', "HighDpi_DiagEmitted");
}

static void DummyVtableMethod() {}
static void* s_mockDeallocPtr = nullptr;
static int s_mockDeallocCount = 0;
static void MockCGameDeallocate(void* p) {
    s_mockDeallocPtr = p;
    s_mockDeallocCount++;
}

static void TestCGameEntityDirectoryUafGuard() {
    // 1. Prologue verification (OBL-CG-01)
    static const uint8_t patPostSnapshot15[] = {
        0x48, 0x89, 0x4C, 0x24, 0x08, 0x55, 0x53, 0x56,
        0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56
    };
    static const uint8_t patDelete15[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20
    };
    static const uint8_t patClear16[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56
    };

    Kongor::PatchController::PrologueValidationResult resPost{}, resDel{}, resClr{};
    bool okPost = Kongor::PatchController::ValidateHookPrologue64(patPostSnapshot15, 15, true, &resPost);
    bool okDel = Kongor::PatchController::ValidateHookPrologue64(patDelete15, 15, true, &resDel);
    bool okClr = Kongor::PatchController::ValidateHookPrologue64(patClear16, 16, true, &resClr);

    ExpectTrue(okPost, "CGame_Prologue_PostSnapshot_15Bytes", resPost.refusalReason);
    ExpectTrue(okDel, "CGame_Prologue_Delete_15Bytes", resDel.refusalReason);
    ExpectTrue(okClr, "CGame_Prologue_Clear_16Bytes", resClr.refusalReason);

    // 2. SEH pointer and vtable validity validation (OBL-CG-03)
    ExpectTrue(!Kongor_IsValidDynamicEntityPointerSeh(nullptr), "CGame_Entity_Null_Rejected");
    ExpectTrue(!Kongor_IsValidDynamicEntityPointerSeh(reinterpret_cast<void*>(0x1001ULL)), "CGame_Entity_Unaligned_Rejected");
    ExpectTrue(!Kongor_IsValidDynamicEntityPointerSeh(reinterpret_cast<void*>(0x8ULL)), "CGame_Entity_LowAddr_Rejected");
    ExpectTrue(!Kongor_IsValidDynamicEntityPointerSeh(reinterpret_cast<void*>(0xFFFFFFFFFFFFFFFFULL)), "CGame_Entity_Kernel_Rejected");

    // Construct valid entity with mock vtable covering vtable + 0x478 (0x90 entries = 144 pointers)
    void* mockVtable[145] = {0};
    mockVtable[0x478 / sizeof(void*)] = reinterpret_cast<void*>(&DummyVtableMethod);
    void* mockEntity[2] = { reinterpret_cast<void*>(mockVtable), nullptr };

    ExpectTrue(Kongor_IsValidDynamicEntityPointerSeh(mockEntity), "CGame_Entity_Valid_Accepted");

    // Invalid entity with null vtable
    void* mockBadEntity1[2] = { nullptr, nullptr };
    ExpectTrue(!Kongor_IsValidDynamicEntityPointerSeh(mockBadEntity1), "CGame_Entity_NullVtable_Rejected");

    // Invalid entity with unmapped vtable+0x478
    void* mockBadVtable[10] = {0}; // too short, +0x478 is unmapped
    void* mockBadEntity2[2] = { reinterpret_cast<void*>(mockBadVtable), nullptr };
    ExpectTrue(!Kongor_IsValidDynamicEntityPointerSeh(mockBadEntity2), "CGame_Entity_NullMethod_Rejected");

    // 3. Mock doubly-linked list orphan pruning and resource reclamation (OBL-CG-04)
    struct TestListNode {
        TestListNode* pNext;
        TestListNode* pPrev;
        void* pEntity;
    };
    struct TestEntityDirectory {
        uint8_t padding[0x18];
        TestListNode* pHeadNode;
        uint64_t numDynamicEntities;
    };

    TestListNode head{}, node1{}, node2{}, node3{};
    head.pNext = &node1; head.pPrev = &node3; head.pEntity = nullptr;
    node1.pNext = &node2; node1.pPrev = &head; node1.pEntity = mockEntity;
    node2.pNext = &node3; node2.pPrev = &node1; node2.pEntity = reinterpret_cast<void*>(0x00007FFE00000048ULL); // dangling pointer from crash
    node3.pNext = &head; node3.pPrev = &node2; node3.pEntity = mockEntity;

    TestEntityDirectory dir{};
    dir.pHeadNode = &head;
    dir.numDynamicEntities = 3;

    s_mockDeallocPtr = nullptr;
    s_mockDeallocCount = 0;

    size_t pruned = Kongor_PruneOrphanDynamicEntities(&dir, MockCGameDeallocate);
    ExpectTrue(pruned == 1, "CGame_Prune_CountMatches");
    ExpectTrue(dir.numDynamicEntities == 2, "CGame_Prune_NumEntitiesDecremented");
    ExpectTrue(node1.pNext == &node3, "CGame_Prune_Node1NextIsNode3");
    ExpectTrue(node3.pPrev == &node1, "CGame_Prune_Node3PrevIsNode1");
    ExpectTrue(head.pNext == &node1 && head.pPrev == &node3, "CGame_Prune_HeadIntact");
    ExpectTrue(s_mockDeallocCount == 1, "CGame_Prune_DeallocateInvoked");
    ExpectTrue(s_mockDeallocPtr == &node2, "CGame_Prune_DeallocatedOrphanNode");

    // 3b. Bidirectional splice when middle span has unmapped/clobbered _Next & _Prev (458dead6.dmp disconnect teardown)
    //     AND when a middle node's entire page is unmapped (MEM_RELEASE): zero valid-entity loss & zero double-free!
    TestListNode headB{}, validA{}, clobbered1{}, clobbered2{}, validZ{};
    headB.pNext = &validA; headB.pPrev = &validZ; headB.pEntity = nullptr;
    validA.pNext = &clobbered1; validA.pPrev = &headB; validA.pEntity = mockEntity;
    // Both _Next and _Prev of clobbered1 are unmapped garbage, so forward walk cannot reach validZ!
    clobbered1.pNext = reinterpret_cast<TestListNode*>(0x00007FFE00000088ULL);
    clobbered1.pPrev = reinterpret_cast<TestListNode*>(0x00007FFE00000090ULL);
    clobbered1.pEntity = reinterpret_cast<void*>(0x00007FFE00000048ULL);
    clobbered2.pNext = &validZ; clobbered2.pPrev = reinterpret_cast<TestListNode*>(0x00007FFE000000A0ULL);
    clobbered2.pEntity = nullptr;
    validZ.pNext = &headB; validZ.pPrev = &clobbered2; validZ.pEntity = mockEntity;

    TestEntityDirectory dirB{};
    dirB.pHeadNode = &headB;
    dirB.numDynamicEntities = 4; // validA, clobbered1, clobbered2, validZ
    s_mockDeallocCount = 0;
    size_t bidirSplices = 0;
    size_t prunedB = Kongor_PruneOrphanDynamicEntities(&dirB, MockCGameDeallocate, &bidirSplices);
    ExpectTrue(prunedB == 2 && bidirSplices == 1, "CGame_BidirSplice_ExcisesMultiNodeCorruptSpan");
    ExpectTrue(dirB.numDynamicEntities == 2, "CGame_BidirSplice_ExactSurvivingEntityCount");
    ExpectTrue(validA.pNext == &validZ && validZ.pPrev == &validA, "CGame_BidirSplice_PreservesValidBeforeAndAfterNodes");
    ExpectTrue(headB.pNext == &validA && headB.pPrev == &validZ, "CGame_BidirSplice_HeadRingIntact");
    ExpectTrue(s_mockDeallocCount == 0, "CGame_BidirSplice_NeverDoubleFreesClobberedArenaNode");

    // Unmapped node page test (MEM_RELEASE middle node)
    void* rawPage = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    ExpectTrue(rawPage != nullptr, "CGame_UnmappedNode_AllocPage");
    if (rawPage) {
        TestListNode* unmappedNode = reinterpret_cast<TestListNode*>(rawPage);
        TestListNode headU{}, keep1{}, keep2{};
        headU.pNext = &keep1; headU.pPrev = &keep2;
        keep1.pNext = unmappedNode; keep1.pPrev = &headU; keep1.pEntity = mockEntity;
        keep2.pNext = &headU; keep2.pPrev = unmappedNode; keep2.pEntity = mockEntity;
        VirtualFree(rawPage, 0, MEM_RELEASE); // `unmappedNode` is now completely unmapped!

        TestEntityDirectory dirU{};
        dirU.pHeadNode = &headU;
        dirU.numDynamicEntities = 3;
        size_t bidirU = 0;
        size_t prunedU = Kongor_PruneOrphanDynamicEntities(&dirU, MockCGameDeallocate, &bidirU);
        ExpectTrue(prunedU == 1 && bidirU == 1 && dirU.numDynamicEntities == 2,
                   "CGame_UnmappedNode_SplicesCleanlyWithoutCrash");
        ExpectTrue(keep1.pNext == &keep2 && keep2.pPrev == &keep1,
                   "CGame_UnmappedNode_PreservesBothNeighbors");
    }

    // 4. Kill switch and dirty flag logic (OBL-CG-02, OBL-CG-05)
    SetEnvironmentVariableA("KONGOR_DISABLE_CGAME_ENTITY_GUARD", "1");
    ExpectTrue(Kongor_IsCGameEntityGuardDisabled(), "CGame_KillSwitch_Disabled_1");
    SetEnvironmentVariableA("KONGOR_DISABLE_CGAME_ENTITY_GUARD", "0");
    ExpectTrue(!Kongor_IsCGameEntityGuardDisabled(), "CGame_KillSwitch_Enabled_0");
    SetEnvironmentVariableA("KONGOR_DISABLE_CGAME_ENTITY_GUARD", "");
    ExpectTrue(!Kongor_IsCGameEntityGuardDisabled(), "CGame_KillSwitch_Default_Empty");
}

static void TestDedicatedServerConsoleGuardsAndDbgHelpLock() {
    // 1. Dedicated server console command reject guards (S1, S2)
    // Non-dedicated server (client) allows AddBot and StartGame regardless of world state
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(false, false, L"AddBot") == KongorServerCmdGuardResult::Allow,
               "ServerCmdGuard_Client_AllowsAddBotNoWorld");
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(false, true, L"StartGame practice") == KongorServerCmdGuardResult::Allow,
               "ServerCmdGuard_Client_AllowsStartGameWorldActive");

    // Dedicated server rejects AddBot before world ready
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, false, L"AddBot") == KongorServerCmdGuardResult::RejectAddBotNoWorld,
               "ServerCmdGuard_Dedicated_RejectsAddBotNoWorld");
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, false, L"AddBot 1 2") == KongorServerCmdGuardResult::RejectAddBotNoWorld,
               "ServerCmdGuard_Dedicated_RejectsAddBotArgsNoWorld");
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, true, L"AddBot") == KongorServerCmdGuardResult::Allow,
               "ServerCmdGuard_Dedicated_AllowsAddBotWithWorld");

    // Dedicated server rejects StartGame while world is already active
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, true, L"StartGame Game1") == KongorServerCmdGuardResult::RejectStartGameWorldActive,
               "ServerCmdGuard_Dedicated_RejectsStartGameWorldActive");
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, false, L"StartGame Game1") == KongorServerCmdGuardResult::Allow,
               "ServerCmdGuard_Dedicated_AllowsStartGameNoWorld");

    // Dedicated server allows unrelated commands
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, false, L"Echo Hello") == KongorServerCmdGuardResult::Allow,
               "ServerCmdGuard_Dedicated_AllowsUnrelated");
    ExpectTrue(Kongor_EvaluateServerCommandGuardW(true, true, L"EndGame") == KongorServerCmdGuardResult::Allow,
               "ServerCmdGuard_Dedicated_AllowsEndGame");

    // 2. DbgHelp lock acquisition and re-entrancy tracking (S3)
    ExpectTrue(Kongor_TestDbgHelpLockAcquisition(500), "DbgHelp_LockAcquisition_Succeeds");
    ExpectTrue(!Kongor_TestDbgHelpReentrancyFlag(), "DbgHelp_ReentrancyFlag_InitialFalse");

    Kongor_SetTestDbgHelpReentrancy(true);
    ExpectTrue(Kongor_TestDbgHelpReentrancyFlag(), "DbgHelp_ReentrancyFlag_SetTrue");

    Kongor_SetTestDbgHelpReentrancy(false);
    ExpectTrue(!Kongor_TestDbgHelpReentrancyFlag(), "DbgHelp_ReentrancyFlag_RestoredFalse");
}

static uint8_t s_mockCapturedMode = 0xFF;
static bool s_mockCapturedNotify = false;
static void __fastcall MockSetChatMode(void* pThis, uint8_t mode, const void* sMessage, bool bNotify) {
    (void)pThis;
    (void)sMessage;
    s_mockCapturedMode = mode;
    s_mockCapturedNotify = bNotify;
}

static void TestChatManagerSetChatMode() {
    // 1. BaseCmd matching for KongorSetChatMode
    const wchar_t* pArgs = nullptr;
    const Kongor::BaseCmdNode* node1 = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(L"KongorSetChatMode 3", &pArgs);
    ExpectTrue(node1 != nullptr && node1->Id == Kongor::KongorConsoleCmdId::SetChatMode, "BaseCmd_KongorSetChatMode_Matches");
    ExpectTrue(pArgs != nullptr && wcscmp(pArgs, L"3") == 0, "BaseCmd_KongorSetChatMode_ArgsParsed");

    const Kongor::BaseCmdNode* node2 = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(L"kongorsetchatmode invisible", &pArgs);
    ExpectTrue(node2 != nullptr && node2->Id == Kongor::KongorConsoleCmdId::SetChatMode, "BaseCmd_kongorsetchatmode_CaseInsensitive");

    // 2. Null pChat returns false safely
    ExpectTrue(!Kongor_SetChatMode(0, nullptr, nullptr), "SetChatMode_NullSafe");

    // 3. Normal transition (0 -> 2)
    uint8_t mockChat[512] = {0};
    mockChat[0x1D5] = 0; // Online
    s_mockCapturedMode = 0xFF;
    s_mockCapturedNotify = false;

    bool ok1 = Kongor_SetChatMode(2, mockChat, reinterpret_cast<void*>(MockSetChatMode));
    ExpectTrue(ok1, "SetChatMode_Transition0to2_Succeeds");
    ExpectTrue(s_mockCapturedMode == 2, "SetChatMode_Transition0to2_ModePassed");
    ExpectTrue(s_mockCapturedNotify == true, "SetChatMode_Transition0to2_NotifyPassed");

    // 4. Invisible transition (3 -> 0) unblocks byte 0x1D5
    mockChat[0x1D5] = 3; // Currently Invisible
    s_mockCapturedMode = 0xFF;

    bool ok2 = Kongor_SetChatMode(0, mockChat, reinterpret_cast<void*>(MockSetChatMode));
    ExpectTrue(ok2, "SetChatMode_Transition3to0_Succeeds");
    ExpectTrue(mockChat[0x1D5] == 0, "SetChatMode_Transition3to0_Byte1D5Unblocked");
    ExpectTrue(s_mockCapturedMode == 0, "SetChatMode_Transition3to0_ModePassed");
}

// ----------------------------------------------------------------------------
// 19. Test VFS Read-Only Loose-File Existence Index & Dynamic Root Bypass
//   protects: K2 _waccess(path, 0) VFS loose-file precedence & live override/ hot-reload invariant
//   fails_when: override/, userdata/, or KONGOR/updates/ is cached, or read-only hits/misses misclassify
//   seam: real Win32 FindFirstFileExW directory snapshot + EvaluateVfsLooseWaccess against real temp fs
// ----------------------------------------------------------------------------
static int s_realWaccessCallCount = 0;
static int __cdecl CountingRealWaccess(const wchar_t* path, int mode) {
    ++s_realWaccessCallCount;
    return ::_waccess(path, mode);
}

void TestVfsLooseCacheReadOnlyIndexAndDynamicBypass() {
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::path sandboxBase = fs::temp_directory_path(ec) / ("kongor_vfs_loose_test_" + std::to_string(::GetCurrentProcessId()));
    fs::remove_all(sandboxBase, ec);

    fs::path clientDir = sandboxBase / "client";
    fs::path sharedDir = sandboxBase / "shared";

    fs::create_directories(clientDir / "game" / "heroes" / "artillery", ec);
    fs::create_directories(clientDir / "base" / "ui", ec);
    fs::create_directories(clientDir / "ClientPackageFiles" / "ui" / "fe2", ec);
    fs::create_directories(sharedDir / "effects", ec);
    fs::create_directories(clientDir / "override" / "ui" / "scripts", ec);
    fs::create_directories(clientDir / "userdata" / "logs", ec);
    fs::create_directories(clientDir / "KONGOR" / "updates", ec);

    {
        std::ofstream(clientDir / "game" / "heroes" / "artillery" / "hero.entity") << "<hero/>";
        std::ofstream(clientDir / "ClientPackageFiles" / "ui" / "fe2" / "store2.package") << "<package/>";
        std::ofstream(sharedDir / "effects" / "impact.effect") << "<effect/>";
    }

    Kongor::InitializeVfsLooseCache(clientDir.wstring(), /*synchronousForTest=*/true);
    Kongor::VfsLooseCacheStats initStats = Kongor::GetVfsLooseCacheStats();
    ExpectTrue(initStats.Ready && initStats.Enabled, "VfsLooseCache_ReadyAndEnabled");
    ExpectTrue(initStats.IndexedRoots == 4, "VfsLooseCache_IndexedFourExistingReadOnlyRoots");
    ExpectTrue(initStats.IndexedEntries >= 10, "VfsLooseCache_IndexedDirectoriesAndFiles");

    // 1. Fast negative miss inside game/ eliminates kernel syscall (0 calls to CountingRealWaccess)
    s_realWaccessCallCount = 0;
    std::wstring missingGameFile = (clientDir / "game" / "heroes" / "artillery" / "nonexistent.entity").wstring();
    int rMiss = Kongor::EvaluateVfsLooseWaccess(missingGameFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rMiss == -1 && errno == ENOENT, "VfsLooseCache_ReadOnlyMissReturnsMinus1Enoent");
    ExpectTrue(s_realWaccessCallCount == 0, "VfsLooseCache_ReadOnlyMissSkipsKernelSyscall");

    // 2. Existing file inside game/ and ../shared/ delegates to real _waccess and returns 0
    s_realWaccessCallCount = 0;
    std::wstring existingGameFile = (clientDir / "game" / "HEROES" / "Artillery" / "hero.entity").wstring();
    int rHit = Kongor::EvaluateVfsLooseWaccess(existingGameFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rHit == 0 && s_realWaccessCallCount == 1, "VfsLooseCache_CaseInsensitiveHitDelegatesToReal");

    s_realWaccessCallCount = 0;
    std::wstring dotDotSharedHit = (clientDir / L"..\\shared\\effects\\impact.effect").wstring();
    int rSharedHit = Kongor::EvaluateVfsLooseWaccess(dotDotSharedHit.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rSharedHit == 0 && s_realWaccessCallCount == 1, "VfsLooseCache_DotDotSharedResolvedHit");

    s_realWaccessCallCount = 0;
    std::wstring dotDotSharedMiss = (clientDir / L"../shared/effects/missing.effect").wstring();
    int rSharedMiss = Kongor::EvaluateVfsLooseWaccess(dotDotSharedMiss.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rSharedMiss == -1 && s_realWaccessCallCount == 0, "VfsLooseCache_DotDotSharedMissSkipsSyscall");

    // 3. Dynamic roots (override/, userdata/, KONGOR/updates/) MUST bypass cache so files created AFTER init are seen immediately!
    std::wstring liveOverrideFile = (clientDir / "override" / "ui" / "scripts" / "hot_reload_new.lua").wstring();
    s_realWaccessCallCount = 0;
    int rOvrBefore = Kongor::EvaluateVfsLooseWaccess(liveOverrideFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rOvrBefore == -1 && s_realWaccessCallCount == 1, "VfsLooseCache_OverrideBypassesCacheWhenAbsent");

    {
        std::ofstream(liveOverrideFile) << "-- live hot-reload file created after init\n";
    }
    s_realWaccessCallCount = 0;
    int rOvrAfter = Kongor::EvaluateVfsLooseWaccess(liveOverrideFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rOvrAfter == 0 && s_realWaccessCallCount == 1, "VfsLooseCache_OverrideSeesNewLiveFileImmediately");

    std::wstring liveUpdatesFile = (clientDir / "KONGOR" / "updates" / "delta.s2z").wstring();
    {
        std::ofstream(liveUpdatesFile) << "PK";
    }
    s_realWaccessCallCount = 0;
    int rUpd = Kongor::EvaluateVfsLooseWaccess(liveUpdatesFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rUpd == 0 && s_realWaccessCallCount == 1, "VfsLooseCache_KongorUpdatesBypassesCache");

    std::wstring liveUserdataFile = (clientDir / "userdata" / "startup.cfg").wstring();
    {
        std::ofstream(liveUserdataFile) << "SetSave host_maxFPS 240";
    }
    s_realWaccessCallCount = 0;
    int rUsr = Kongor::EvaluateVfsLooseWaccess(liveUserdataFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(rUsr == 0 && s_realWaccessCallCount == 1, "VfsLooseCache_UserdataBypassesCache");

    // 4. Write-permission check (mode == 2) inside game/ bypasses cache and delegates to real _waccess
    s_realWaccessCallCount = 0;
    (void)Kongor::EvaluateVfsLooseWaccess(missingGameFile.c_str(), 2, &CountingRealWaccess);
    ExpectTrue(s_realWaccessCallCount == 1, "VfsLooseCache_NonZeroModeBypassesCache");

    // 5. PatchController toggle ("vfs_loose_cache" disable -> bypasses cache; enable -> resumes cache)
    char toggleJson[256] = {0};
    Kongor::PatchController::ExecuteHookToggle("vfs_loose_cache", "disable", toggleJson, sizeof(toggleJson), false);
    s_realWaccessCallCount = 0;
    (void)Kongor::EvaluateVfsLooseWaccess(missingGameFile.c_str(), 0, &CountingRealWaccess);
    ExpectTrue(s_realWaccessCallCount == 1, "VfsLooseCache_PatchControllerDisableBypassesCache");
    Kongor::PatchController::ExecuteHookToggle("vfs_loose_cache", "enable", toggleJson, sizeof(toggleJson), false);

    Kongor::ShutdownVfsLooseCache(false);
    fs::remove_all(sandboxBase, ec);
}

// ============================================================================
// 20-Player (>5v5) Team Slot Reader & Safety Obligations (OBL-NG-20P-01..05)
//   protects: ReadTeamSlotCountNative, ReadLobbyTeamSlotNative, and ReadScoreboardTeamSlotNative
//             memory/bounds safety, SEH fault isolation, 32-bit slot-lock bitmask on seats 8..9,
//             fog-of-war enemy inventory redaction, and KONGOR_DISABLE_20P_BRIDGE kill-switch.
//   fails_when: (1) uint8_t truncation drops locks on seats 8/9, (2) enemy items leak when
//               CanSee == false, (3) invalid/teardown pointers crash the process instead of SEH
//               recovery, or (4) kill-switch fails to short-circuit.
//   seam: Real C++ layout reader in kongor_lua_shared_mem.cpp against exact K2 x64 struct offsets.
// ============================================================================
namespace Mock20P {
    struct FakeK2WString {
        union {
            const wchar_t* ptr;
            wchar_t inlineBuf[8];
        } u;
        size_t len;
        size_t cap;
    };
    static_assert(sizeof(FakeK2WString) == 32, "K2WString must be 32 bytes");

    static FakeK2WString MakeHeapK2WStr(const wchar_t* lit) {
        FakeK2WString s = {};
        s.u.ptr = lit;
        s.len = wcslen(lit);
        s.cap = 16; // > 7 forces pointer dereference
        return s;
    }

    static FakeK2WString s_nameLocal   = MakeHeapK2WStr(L"PlayerEight");
    static FakeK2WString s_nameEnemy   = MakeHeapK2WStr(L"EnemyNine");
    static FakeK2WString s_heroDefDev  = MakeHeapK2WStr(L"Hero_Devourer");
    static FakeK2WString s_heroDispDev = MakeHeapK2WStr(L"Devourer");
    static FakeK2WString s_heroIconDev = MakeHeapK2WStr(L"/heroes/devourer/icon.tga");
    static FakeK2WString s_itemIcon0   = MakeHeapK2WStr(L"/items/basic/marchers/icon.tga");

    static bool s_mockCanSee = false;
    static const uint8_t* s_team1Ptr = nullptr;
    static const uint8_t* s_team2Ptr = nullptr;
    static void* s_heroEntityPtr = nullptr;
    static void* s_itemObjPtr = nullptr;
    static const uint8_t* s_vtableLocalPlayer = nullptr;

    static const uint8_t* __fastcall MockGameGetLocalPlayer(const void*) {
        return s_vtableLocalPlayer;
    }
    static float __fastcall MockHeroGetMaxHp(const void*) { return 1000.0f; }
    static float __fastcall MockHeroGetHpPct(const void*) { return 0.75f; }
    static float __fastcall MockHeroGetMana(const void*) { return 200.0f; }
    static float __fastcall MockHeroGetMaxMana(const void*) { return 400.0f; }
    static bool __fastcall MockHasSharedPartial(const void* pWho, int targetCid) {
        int32_t ownerCid = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pWho) + 0xa8);
        return (ownerCid == 7 && targetCid == 7);
    }
    static bool __fastcall MockHasSharedFull(const void*, int) { return false; }
    static bool __fastcall MockGetNoHelp(const void*, void*) { return true; }
    static uint32_t __fastcall MockGetRemCooldown(const void*, bool) { return 3500u; }

    static int __fastcall MockGetClientIDFromTeamIndex(const void* pTeam, uint32_t seat) {
        if (pTeam == s_team1Ptr && seat == 7) return 7; // Legion seat 8 (0-based 7)
        if (pTeam == s_team2Ptr && seat == 8) return 18; // Hellbourne seat 9 (0-based 8)
        return -1;
    }
    static bool __fastcall MockCanJoinTeam(const void*, int, uint32_t seat) {
        return seat != 8 && seat != 9;
    }
    static bool __fastcall MockIsBot(const void* pPlayer) {
        int32_t cid = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pPlayer) + 0xa8);
        return cid == 18;
    }
    static bool __fastcall MockIsReferee(const void*) { return false; }
    static bool __fastcall MockCanSee(const void*, const void*) { return s_mockCanSee; }
    static const void* __fastcall MockGetNetString(void*, uint16_t idx) {
        if (idx == 1) return &s_nameLocal;
        if (idx == 2) return &s_nameEnemy;
        return nullptr;
    }
    static const void* __fastcall MockLookupEntityName(void*, uint16_t typeId) {
        if (typeId == 42) return &s_heroDefDev;
        return nullptr;
    }

    static void* __fastcall MockGetEntityFromIndex(const void*, uint32_t idx) {
        return (idx == 515u) ? s_heroEntityPtr : nullptr;
    }
    static void* __fastcall MockGetAsHero(const void* pEnt) {
        return const_cast<void*>(pEnt);
    }
    static uint32_t __fastcall MockHeroGetLevel(const void*) { return 14u; }
    static const void* __fastcall MockHeroGetIcon(const void*) { return &s_heroIconDev; }
    static const void* __fastcall MockHeroGetDispName(const void*) { return &s_heroDispDev; }
    static const void* __fastcall MockSlotGetAsItem(const void*) { return s_itemObjPtr; }
    static const void* __fastcall MockItemGetIcon(const void*) { return &s_itemIcon0; }
}

void Test20PlayerTeamSlotReaderAndSafetyObligations() {
    SetEnvironmentVariableA("KONGOR_DISABLE_20P_BRIDGE", NULL);
    ExpectTrue(Kongor::Is20PlayerBridgeEnabledNative(), "20P_BridgeEnabledByDefault");
    ExpectTrue(Kongor::Get20PlayerBridgeVersionNative() == 2, "20P_BridgeVersionIs2");

    // #3A: Uppercase virtual mod prefix enrollment predicate (KONGOR/updates/textures.s2z vs lowercase game/*.s2z)
    ExpectTrue(Kongor_HasUppercaseVirtualArchivePrefixW(L":/KONGOR/updates/textures.s2z"),
               "ArchiveCaseGuard_Enrolls_KongorUpdatesTexturesS2z");
    ExpectTrue(Kongor_HasUppercaseVirtualArchivePrefixW(L":/KONGOR/updates/"),
               "ArchiveCaseGuard_Enrolls_KongorUpdatesPathToArch");
    ExpectTrue(Kongor_HasUppercaseVirtualArchivePrefixW(L":/ClientPackageFiles/ui.s2z"),
               "ArchiveCaseGuard_Enrolls_ClientPackageFilesMod");
    ExpectTrue(!Kongor_HasUppercaseVirtualArchivePrefixW(L":/game/textures.s2z"),
               "ArchiveCaseGuard_Skips_LowercaseGameTexturesS2z");
    ExpectTrue(!Kongor_HasUppercaseVirtualArchivePrefixW(L":/game/resources0.s2z"),
               "ArchiveCaseGuard_Skips_LowercaseResources0S2z");
    ExpectTrue(!Kongor_HasUppercaseVirtualArchivePrefixW(L":/game/UpperCaseLeafOnly.s2z"),
               "ArchiveCaseGuard_Skips_LowercasePrefixWithUpperLeaf");
    ExpectTrue(!Kongor_HasUppercaseVirtualArchivePrefixW(L"C:\\Workspaces\\ProjectKongor\\games\\client\\game\\textures.s2z"),
               "ArchiveCaseGuard_Skips_WindowsDriveFilesystemPath");

    // Build synthetic RB-trees for m_mapTeams (keys 1, 2) and m_mapClients (keys 7, 18)
    std::vector<uint8_t> teamHead(0x30, 0), teamNode1(0x30, 0), teamNode2(0x30, 0);
    teamHead[0x19] = 1; // _Isnil = true on sentinel head
    *reinterpret_cast<uint8_t**>(teamHead.data() + 0x08) = teamNode1.data(); // root = team 1
    *reinterpret_cast<uint8_t**>(teamNode1.data() + 0x00) = teamHead.data();
    *reinterpret_cast<uint8_t**>(teamNode1.data() + 0x10) = teamNode2.data();
    *reinterpret_cast<uint32_t*>(teamNode1.data() + 0x20) = 1u;
    *reinterpret_cast<uint8_t**>(teamNode2.data() + 0x00) = teamHead.data();
    *reinterpret_cast<uint8_t**>(teamNode2.data() + 0x10) = teamHead.data();
    *reinterpret_cast<uint32_t*>(teamNode2.data() + 0x20) = 2u;

    std::vector<uint8_t> team1(0x180, 0), team2(0x180, 0);
    *reinterpret_cast<uint32_t*>(team1.data() + 0xc8) = 10u; // 10v10!
    *reinterpret_cast<uint32_t*>(team1.data() + 0xe8) = (1u << 8) | (1u << 9); // seats 8 & 9 locked!
    *reinterpret_cast<int32_t*>(team1.data() + 0x16c) = 12;  // Team 1 score
    *reinterpret_cast<uint32_t*>(team2.data() + 0xc8) = 10u;
    *reinterpret_cast<uint32_t*>(team2.data() + 0xe8) = 0u;
    *reinterpret_cast<int32_t*>(team2.data() + 0x16c) = 9;   // Team 2 score
    *reinterpret_cast<uint8_t**>(teamNode1.data() + 0x28) = team1.data();
    *reinterpret_cast<uint8_t**>(teamNode2.data() + 0x28) = team2.data();
    Mock20P::s_team1Ptr = team1.data();
    Mock20P::s_team2Ptr = team2.data();

    // Synthetic vtables for IEntityDirectory, IHeroEntity, InventorySlot, and Item
    std::vector<uint8_t> entDirVtbl(0x40, 0), entDirObj(0x10, 0);
    *reinterpret_cast<void**>(entDirVtbl.data() + 0x10) = reinterpret_cast<void*>(&Mock20P::MockGetEntityFromIndex);
    *reinterpret_cast<void**>(entDirObj.data()) = entDirVtbl.data();

    std::vector<uint8_t> itemVtbl(0x580, 0), itemObj(0x10, 0);
    *reinterpret_cast<void**>(itemVtbl.data() + 0x540) = reinterpret_cast<void*>(&Mock20P::MockItemGetIcon);
    *reinterpret_cast<void**>(itemObj.data()) = itemVtbl.data();
    Mock20P::s_itemObjPtr = itemObj.data();

    std::vector<uint8_t> slotVtbl(0x240, 0), slotObj(0x10, 0);
    *reinterpret_cast<void**>(slotVtbl.data() + 0x208) = reinterpret_cast<void*>(&Mock20P::MockSlotGetAsItem);
    *reinterpret_cast<void**>(slotObj.data()) = slotVtbl.data();

    std::vector<uint8_t> heroVtbl(0x1180, 0), heroObj(0x1600, 0);
    *reinterpret_cast<void**>(heroVtbl.data() + 0x178)  = reinterpret_cast<void*>(&Mock20P::MockGetAsHero);
    *reinterpret_cast<void**>(heroVtbl.data() + 0x608)  = reinterpret_cast<void*>(&Mock20P::MockHeroGetLevel);
    *reinterpret_cast<void**>(heroVtbl.data() + 0x678)  = reinterpret_cast<void*>(&Mock20P::MockHeroGetMaxHp);
    *reinterpret_cast<void**>(heroVtbl.data() + 0x6b8)  = reinterpret_cast<void*>(&Mock20P::MockHeroGetMaxMana);
    *reinterpret_cast<void**>(heroVtbl.data() + 0xa90)  = reinterpret_cast<void*>(&Mock20P::MockHeroGetHpPct);
    *reinterpret_cast<void**>(heroVtbl.data() + 0xab0)  = reinterpret_cast<void*>(&Mock20P::MockHeroGetMana);
    *reinterpret_cast<void**>(heroVtbl.data() + 0xc08)  = reinterpret_cast<void*>(&Mock20P::MockHeroGetIcon);
    *reinterpret_cast<void**>(heroVtbl.data() + 0x1150) = reinterpret_cast<void*>(&Mock20P::MockHeroGetDispName);
    *reinterpret_cast<void**>(heroObj.data()) = heroVtbl.data();
    *reinterpret_cast<uint32_t*>(heroObj.data() + 0x1c) = 515u;
    heroObj[0xd0] = 1; // alive
    *reinterpret_cast<float*>(heroObj.data() + 0x360) = 750.0f;
    *reinterpret_cast<int32_t*>(heroObj.data() + 0x152c) = -1;
    *reinterpret_cast<void**>(heroObj.data() + 0x420 + 0x30 * 8) = slotObj.data(); // backpack slot 0
    Mock20P::s_heroEntityPtr = heroObj.data();

    // Players 7 (Legion seat 7, local host) and 18 (Hellbourne seat 8, enemy bot)
    std::vector<uint8_t> player7(0x520, 0), player18(0x520, 0);
    *reinterpret_cast<int32_t*>(player7.data() + 0xa8) = 7;
    *reinterpret_cast<int32_t*>(player7.data() + 0xac) = 1;
    *reinterpret_cast<uint32_t*>(player7.data() + 0xb0) = 515u;
    *reinterpret_cast<uint16_t*>(player7.data() + 0xc0) = 42u;   // selected Hero_Devourer
    *reinterpret_cast<uint16_t*>(player7.data() + 0xcc) = 0xFFFFu;
    *reinterpret_cast<int32_t*>(player7.data() + 0x2d0) = 1001;
    *reinterpret_cast<uint16_t*>(player7.data() + 0x2d8) = 1u;
    *reinterpret_cast<float*>(player7.data() + 0x330) = 1.0f;
    *reinterpret_cast<uint16_t*>(player7.data() + 0x3de) = 0x0006u; // ready + host
    *reinterpret_cast<uint16_t*>(player7.data() + 0x404) = 1650u;
    *reinterpret_cast<uint16_t*>(player7.data() + 0x420) = 850u;
    *reinterpret_cast<int32_t*>(player7.data() + 0x470) = 5;
    *reinterpret_cast<int32_t*>(player7.data() + 0x474) = 1;
    *reinterpret_cast<int32_t*>(player7.data() + 0x478) = 8;
    *reinterpret_cast<float*>(player7.data() + 0x4b8 + 0 * 4) = 10.0f; // attempts
    *reinterpret_cast<float*>(player7.data() + 0x4b8 + 1 * 4) = 7.0f;  // hits

    *reinterpret_cast<int32_t*>(player18.data() + 0xa8) = 18;
    *reinterpret_cast<int32_t*>(player18.data() + 0xac) = 2;
    *reinterpret_cast<uint32_t*>(player18.data() + 0xb0) = 515u;
    *reinterpret_cast<uint16_t*>(player18.data() + 0xc0) = 0xFFFFu;
    *reinterpret_cast<uint16_t*>(player18.data() + 0xcc) = 42u;
    *reinterpret_cast<uint16_t*>(player18.data() + 0x2d8) = 2u;
    *reinterpret_cast<float*>(player18.data() + 0x330) = 1.0f;
    *reinterpret_cast<int32_t*>(player18.data() + 0x470) = 3;
    *reinterpret_cast<int32_t*>(player18.data() + 0x474) = 4;
    *reinterpret_cast<int32_t*>(player18.data() + 0x478) = 2;

    std::vector<uint8_t> clientHead(0x30, 0), clientNode7(0x30, 0), clientNode18(0x30, 0);
    clientHead[0x19] = 1;
    *reinterpret_cast<uint8_t**>(clientHead.data() + 0x08) = clientNode7.data();
    *reinterpret_cast<uint8_t**>(clientNode7.data() + 0x00) = clientHead.data();
    *reinterpret_cast<uint8_t**>(clientNode7.data() + 0x10) = clientNode18.data();
    *reinterpret_cast<int32_t*>(clientNode7.data() + 0x20) = 7;
    *reinterpret_cast<uint8_t**>(clientNode7.data() + 0x28) = player7.data();
    *reinterpret_cast<uint8_t**>(clientNode18.data() + 0x00) = clientHead.data();
    *reinterpret_cast<uint8_t**>(clientNode18.data() + 0x10) = clientHead.data();
    *reinterpret_cast<int32_t*>(clientNode18.data() + 0x20) = 18;
    *reinterpret_cast<uint8_t**>(clientNode18.data() + 0x28) = player18.data();

    // Synthetic IGame vtable (+0x28 GetLocalPlayer) — intentionally leave fakeGame+0x238 null to prove #1B vtable+0x28 resolution!
    std::vector<uint8_t> gameVtbl(0x40, 0);
    Mock20P::s_vtableLocalPlayer = player7.data();
    *reinterpret_cast<void**>(gameVtbl.data() + 0x28) = reinterpret_cast<void*>(&Mock20P::MockGameGetLocalPlayer);

    std::vector<uint8_t> fakeGame(0x260, 0);
    *reinterpret_cast<void**>(fakeGame.data() + 0x00) = gameVtbl.data();
    *reinterpret_cast<int32_t*>(fakeGame.data() + 0x08) = 50000;
    *reinterpret_cast<void**>(fakeGame.data() + 0x28) = entDirObj.data();
    *reinterpret_cast<void**>(fakeGame.data() + 0xd0) = teamHead.data();
    *reinterpret_cast<void**>(fakeGame.data() + 0x1e8) = clientHead.data();
    *reinterpret_cast<void**>(fakeGame.data() + 0x238) = nullptr; // resolved via vtable+0x28!

    // Synthetic CVoiceManager (+0x41e0 talking set contains client 7; +0x41f0 muted set contains client 18)
    std::vector<uint8_t> talkHead(0x28, 0), talkNode7(0x28, 0);
    talkHead[0x19] = 1;
    *reinterpret_cast<uint8_t**>(talkHead.data() + 0x08) = talkNode7.data();
    *reinterpret_cast<uint8_t**>(talkNode7.data() + 0x00) = talkHead.data();
    *reinterpret_cast<uint8_t**>(talkNode7.data() + 0x10) = talkHead.data();
    *reinterpret_cast<int32_t*>(talkNode7.data() + 0x1c) = 7;

    std::vector<uint8_t> muteHead(0x28, 0), muteNode18(0x28, 0);
    muteHead[0x19] = 1;
    *reinterpret_cast<uint8_t**>(muteHead.data() + 0x08) = muteNode18.data();
    *reinterpret_cast<uint8_t**>(muteNode18.data() + 0x00) = muteHead.data();
    *reinterpret_cast<uint8_t**>(muteNode18.data() + 0x10) = muteHead.data();
    *reinterpret_cast<int32_t*>(muteNode18.data() + 0x1c) = 18;

    std::vector<uint8_t> fakeVoiceMgr(0x4200, 0);
    *reinterpret_cast<void**>(fakeVoiceMgr.data() + 0x41e0) = talkHead.data();
    *reinterpret_cast<void**>(fakeVoiceMgr.data() + 0x41f0) = muteHead.data();
    void* pVoiceMgr = fakeVoiceMgr.data();

    void* pGame = fakeGame.data();
    int dummyReg = 1, dummyNet = 1;
    void* pReg = &dummyReg;
    void* pNet = &dummyNet;

    Kongor::Engine20PlayerContext ctx = {};
    ctx.ppGame = &pGame;
    ctx.ppEntityReg = &pReg;
    ctx.ppNetResMgr = &pNet;
    ctx.ppVoiceMgr = &pVoiceMgr;
    ctx.pfnGetClientIDFromTeamIndex = &Mock20P::MockGetClientIDFromTeamIndex;
    ctx.pfnCanJoinTeam = &Mock20P::MockCanJoinTeam;
    ctx.pfnIsBot = &Mock20P::MockIsBot;
    ctx.pfnIsReferee = &Mock20P::MockIsReferee;
    ctx.pfnCanSee = &Mock20P::MockCanSee;
    ctx.pfnHasSharedPartialControl = &Mock20P::MockHasSharedPartial;
    ctx.pfnHasSharedFullControl = &Mock20P::MockHasSharedFull;
    ctx.pfnGetNoHelp = &Mock20P::MockGetNoHelp;
    ctx.pfnGetRemainingCooldownTime = &Mock20P::MockGetRemCooldown;
    ctx.pfnGetNetString = &Mock20P::MockGetNetString;
    ctx.pfnLookupEntityName = &Mock20P::MockLookupEntityName;

    // 1. OBL-NG-20P-01: Bounds & null guards
    ExpectTrue(Kongor::ReadTeamSlotCountNative(nullptr, 1) == 0, "20P_OBL01_NullCtxReturnsZero");
    ExpectTrue(Kongor::ReadTeamSlotCountNative(&ctx, 0) == 0 && Kongor::ReadTeamSlotCountNative(&ctx, 3) == 0,
               "20P_OBL01_InvalidTeamRejected");
    ExpectTrue(Kongor::ReadTeamSlotCountNative(&ctx, 1) == 10, "20P_Team1Reports10Seats");

    Kongor::LobbyTeamSlotSnapshot lob = {};
    ExpectTrue(!Kongor::ReadLobbyTeamSlotNative(&ctx, 1, -1, &lob), "20P_OBL01_NegativeSeatRejected");
    ExpectTrue(!Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 10, &lob), "20P_OBL01_Seat10OutOfBoundsRejected");

    // 2. OBL-NG-20P-04 & #1B: 32-bit m_uiSlotLocks + canJoin on empty (6), occupied (7), and locked (8, 9)
    ExpectTrue(Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 6, &lob) && lob.valid && lob.slotStatus == 0 && lob.canJoin,
               "20P_LobbySeat6OpenCanJoinTrue");
    ExpectTrue(Kongor::ReadLobbyTeamSlotNative(&ctx, 2, 9, &lob) && lob.valid && lob.slotStatus == 0 && lob.canJoin,
               "20P_LobbyTeam2Seat9OpenCanJoinWithout8BitTruncation");
    ExpectTrue(Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 7, &lob) && lob.valid, "20P_LobbySeat7Valid");
    ExpectTrue(lob.slotStatus == 1 && !lob.canJoin && lob.clientNum == 7 && lob.isMe && lob.isHost && lob.localIsHost,
               "20P_LobbySeat7OccupiedLocalHostViaVtable0x28");
    ExpectTrue(strcmp(lob.name, "PlayerEight") == 0 && strcmp(lob.heroDefName, "Hero_Devourer") == 0 && lob.hasSelectedHero,
               "20P_LobbySeat7NameAndSelectedHeroResolved");

    ExpectTrue(Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 8, &lob) && lob.valid, "20P_OBL04_LobbySeat8Valid");
    ExpectTrue(lob.isLocked && !lob.canJoin && lob.slotStatus == 2 && lob.clientNum == -1,
               "20P_OBL04_Seat8Full32BitLockPreserved");
    ExpectTrue(Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 9, &lob) && lob.isLocked && !lob.canJoin && lob.slotStatus == 2,
               "20P_OBL04_Seat9Full32BitLockPreserved");

    // 3. OBL-NG-20P-03, OBL-NG-20P-06 & OBL-NG-20P-07: Fog-of-war, sameTeam ally HUD gating, and VoiceManager RB-sets
    Kongor::ScoreboardTeamSlotSnapshot sb = {};
    Mock20P::s_mockCanSee = false;
    ExpectTrue(Kongor::ReadScoreboardTeamSlotNative(&ctx, 1, 7, &sb) && sb.exists, "20P_SB_AllySeat7Exists");
    ExpectTrue(sb.sameTeam && sb.heroEntityIndex == 515 && sb.hasMana &&
               sb.healthPct > 0.74f && sb.manaPct > 0.49f && sb.noHelp &&
               sb.shareFromAlly == 1 && sb.shareToAlly == 1,
               "20P_OBL06_SameTeamAllyHudFieldsPopulated");
    ExpectTrue(sb.isTalking && !sb.isVoiceMuted, "20P_OBL07_VoiceManagerTalkingSetDetected");
    ExpectTrue(sb.canSeeItems && strcmp(sb.items[0], "/items/basic/marchers/icon.tga") == 0,
               "20P_OBL03_AllyItemsVisibleWithoutCanSee");
    ExpectTrue(sb.kills == 5 && sb.deaths == 1 && sb.assists == 8 && sb.gold == 1650 && sb.level == 14 && sb.teamScore == 12,
               "20P_SB_AllyKdaGoldLevelAndTeamScoreRead");
    ExpectTrue(sb.stats[0] == 10.0f && sb.stats[1] == 7.0f, "20P_SB_CustomGameplayStatsRead");

    ExpectTrue(Kongor::ReadScoreboardTeamSlotNative(&ctx, 2, 8, &sb) && sb.exists, "20P_SB_EnemySeat8Exists");
    ExpectTrue(!sb.sameTeam && sb.heroEntityIndex == -1 && sb.healthPct == 0.0f && !sb.hasMana && !sb.noHelp,
               "20P_OBL06_EnemyTacticalAllyFieldsRedacted");
    ExpectTrue(!sb.isTalking && sb.isVoiceMuted, "20P_OBL07_VoiceManagerMutedSetDetected");
    ExpectTrue(!sb.canSeeItems && sb.items[0][0] == '\0',
               "20P_OBL03_EnemyItemsRedactedInFogOfWar");

    Mock20P::s_mockCanSee = true;
    ExpectTrue(Kongor::ReadScoreboardTeamSlotNative(&ctx, 2, 8, &sb) && sb.exists, "20P_SB_EnemySeat8VisibleRequery");
    ExpectTrue(sb.canSeeItems && strcmp(sb.items[0], "/items/basic/marchers/icon.tga") == 0,
               "20P_OBL03_EnemyItemsVisibleWhenInVision");

    // 4. OBL-NG-20P-02: SEH hardware access-violation recovery on unmapped pointer
    void* badGamePtr = reinterpret_cast<void*>(0xDEAD0000ULL);
    ctx.ppGame = &badGamePtr;
    ExpectTrue(Kongor::ReadTeamSlotCountNative(&ctx, 1) == 0, "20P_OBL02_SehCatchesAvInSlotCount");
    ExpectTrue(!Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 7, &lob), "20P_OBL02_SehCatchesAvInLobbySlot");
    ExpectTrue(!Kongor::ReadScoreboardTeamSlotNative(&ctx, 1, 7, &sb), "20P_OBL02_SehCatchesAvInScoreboardSlot");
    ctx.ppGame = &pGame;

    // 5. OBL-NG-20P-05: Kill-switch environment guard
    SetEnvironmentVariableA("KONGOR_DISABLE_20P_BRIDGE", "1");
    ExpectTrue(!Kongor::Is20PlayerBridgeEnabledNative(), "20P_OBL05_KillSwitchDisablesFlag");
    ExpectTrue(Kongor::ReadTeamSlotCountNative(&ctx, 1) == 0, "20P_OBL05_KillSwitchShortCircuitsSlotCount");
    ExpectTrue(!Kongor::ReadLobbyTeamSlotNative(&ctx, 1, 7, &lob), "20P_OBL05_KillSwitchShortCircuitsLobby");
    ExpectTrue(!Kongor::ReadScoreboardTeamSlotNative(&ctx, 1, 7, &sb), "20P_OBL05_KillSwitchShortCircuitsScoreboard");
    SetEnvironmentVariableA("KONGOR_DISABLE_20P_BRIDGE", NULL);
}

// ----------------------------------------------------------------------------
// 21. Queue Arcade Slot-Swap, Handshake & Privilege Firewall Obligations (OBL-QA-01..07)
// ----------------------------------------------------------------------------
namespace MockQueueArcade {
    static int s_removeBotCalls = 0;
    static void* s_dummyNetResMgr = reinterpret_cast<void*>(0x4B324E52ULL);

    struct MockK2WStringSlot {
        const wchar_t* ptr;
        uint64_t       pad;
        size_t         size;
        size_t         cap;
    };
    static std::wstring s_netStrings[16];
    static MockK2WStringSlot s_netStringViews[16] = {};

    static void ResetNetStrings() {
        for (size_t i = 0; i < 16; ++i) {
            s_netStrings[i].clear();
            s_netStringViews[i] = { L"", 0, 0, 64 };
        }
    }

    static void SeedNetString(uint16_t idx, const wchar_t* text) {
        if (idx >= 16) return;
        s_netStrings[idx] = text ? text : L"";
        s_netStringViews[idx].ptr = s_netStrings[idx].c_str();
        s_netStringViews[idx].pad = 0;
        s_netStringViews[idx].size = s_netStrings[idx].size();
        s_netStringViews[idx].cap = 64;
    }

    static void __fastcall MockSetNetString(void* pMgr, uint16_t idx, const void* pK2WString, bool /*bGrow*/) {
        if (pMgr != s_dummyNetResMgr || idx >= 16 || !pK2WString) return;
        const uint8_t* raw = reinterpret_cast<const uint8_t*>(pK2WString);
        const size_t wlen = *reinterpret_cast<const size_t*>(raw + 0x10);
        const size_t wcap = *reinterpret_cast<const size_t*>(raw + 0x18);
        const wchar_t* wptr = (wcap > 7)
            ? *reinterpret_cast<const wchar_t* const*>(raw)
            : reinterpret_cast<const wchar_t*>(raw);
        s_netStrings[idx].assign(wptr ? wptr : L"", wlen);
        s_netStringViews[idx].ptr = s_netStrings[idx].c_str();
        s_netStringViews[idx].size = s_netStrings[idx].size();
        s_netStringViews[idx].cap = 64;
    }

    static const void* __fastcall MockGetNetString(void* pMgr, uint16_t idx) {
        if (pMgr != s_dummyNetResMgr || idx >= 16) return nullptr;
        s_netStringViews[idx].ptr = s_netStrings[idx].c_str();
        s_netStringViews[idx].size = s_netStrings[idx].size();
        s_netStringViews[idx].cap = 64;
        return &s_netStringViews[idx];
    }

    static void __fastcall MockRemoveBot(void* pPlayer) {
        ++s_removeBotCalls;
        uint8_t* raw = reinterpret_cast<uint8_t*>(pPlayer);
        // Mirror real CPlayer::RemoveBot (game_shared_x64.dll+0x4F62E0): frees m_pBotBrain and clears 0x0048
        // WITHOUT nulling +0x730! Poison +0x730 so OBL-QA-06 fails if callers forget to zero +0x730.
        *reinterpret_cast<void**>(raw + 0x730) = reinterpret_cast<void*>(0xDEADBEEFB07ULL);
        uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(raw + 0x3E0);
        *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0048u);
    }

    static uint32_t s_liveUnitEntityIndex = 42u;
    static bool s_liveUnitEntityIsUnit = true;
    static std::vector<uintptr_t> s_mockEntityVtable(0x40, 0);
    static uintptr_t s_mockEntityObj = 0;
    static std::vector<uintptr_t> s_mockEntityDirVtable(0x08, 0);
    static uintptr_t s_mockEntityDirObj = 0;

    static void* __fastcall MockGetAsUnit(void* pEnt) {
        return (pEnt == &s_mockEntityObj && s_liveUnitEntityIsUnit) ? pEnt : nullptr;
    }

    static void* __fastcall MockGetEntityFromIndex(void* pDir, uint32_t uiIndex) {
        return (pDir == &s_mockEntityDirObj && uiIndex == s_liveUnitEntityIndex) ? &s_mockEntityObj : nullptr;
    }

    static void* InitMockEntityDirectory(uint32_t liveHeroIdx, bool isUnit = true) {
        s_liveUnitEntityIndex = liveHeroIdx;
        s_liveUnitEntityIsUnit = isUnit;
        s_mockEntityVtable.assign(0x40, 0);
        s_mockEntityVtable[0x178 / sizeof(uintptr_t)] = reinterpret_cast<uintptr_t>(&MockGetAsUnit);
        s_mockEntityObj = reinterpret_cast<uintptr_t>(s_mockEntityVtable.data());

        s_mockEntityDirVtable.assign(0x08, 0);
        s_mockEntityDirVtable[0x10 / sizeof(uintptr_t)] = reinterpret_cast<uintptr_t>(&MockGetEntityFromIndex);
        s_mockEntityDirObj = reinterpret_cast<uintptr_t>(s_mockEntityDirVtable.data());
        return &s_mockEntityDirObj;
    }
}

void TestQueueArcadeSlotSwapAndHandshakeObligations() {
    // protects: Queue Arcade CPlayer slot reservation/release, CBotBrain park/restore & safe teardown, and pre-Phase-5 duplicate SpawnHero suppression.
    // fails_when: EndHeroLoading spawns a second twin hero, vacated Mode 1 bots stay brainless or UAF on teardown, or Mode 2 leaves dangling +0x730.
    // seam: Real Kongor::ArcadeReserveSlot / ArcadeReleaseSlot / ShouldSuppressPreMatchDuplicateHeroSpawn / ArcadeReleaseParkedBotOnPlayerTerminate against K2 ABI layouts.
    Kongor::ClearAllArcadeReservations();
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::Disabled);
    ExpectTrue(!Kongor::IsQueueArcadeModeActive(), "QA_OBL01_DefaultDisabled");

    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::ActiveBots);
    ExpectTrue(Kongor::IsQueueArcadeModeActive() &&
               Kongor::GetQueueArcadeMode() == Kongor::QueueArcadeServerMode::ActiveBots,
               "QA_OBL01_ActiveBotsModeEnabled");

    // Build synthetic IGame + 0x1E8 std::map<int, CPlayer*> red-black tree with 2 bot slots (Team 1 & Team 2)
    std::vector<uint8_t> gameImg(0x400, 0);
    std::vector<uint8_t> gameInfoImg(0x100, 0);
    std::vector<uint8_t> headNode(0x30, 0);
    std::vector<uint8_t> nodeTeam1(0x30, 0);
    std::vector<uint8_t> nodeTeam2(0x30, 0);
    std::vector<uint8_t> player1(0x800, 0);
    std::vector<uint8_t> player2(0x800, 0);
    std::vector<uint8_t> dummyBrain1(0x30, 0);
    std::vector<uint8_t> dummyBrain2(0x30, 0);
    *reinterpret_cast<uint32_t*>(dummyBrain1.data() + 0x10) = 0xFFFFFFFFu;
    *reinterpret_cast<uint32_t*>(dummyBrain2.data() + 0x10) = 0xFFFFFFFFu;

    MockQueueArcade::ResetNetStrings();
    MockQueueArcade::SeedNetString(2, L"DeadwoodBot");
    MockQueueArcade::SeedNetString(5, L"ArtilleryBot");

    // CPlayer 1: clientNum=3, team=1, connectionless bot (0x0202), botBrain active (0x0048), m_bIsBot=1 (+0x66B)
    *reinterpret_cast<int32_t*>(player1.data() + 0xA8) = 3;
    *reinterpret_cast<int32_t*>(player1.data() + 0xAC) = 1;
    *reinterpret_cast<uint32_t*>(player1.data() + 0xB0) = 19u;
    *reinterpret_cast<int32_t*>(player1.data() + 0x2D0) = 0;
    *reinterpret_cast<uint16_t*>(player1.data() + 0x2D8) = 2;
    *reinterpret_cast<uint16_t*>(player1.data() + 0x2DA) = 3;
    *reinterpret_cast<uint16_t*>(player1.data() + 0x2DC) = 4;
    *reinterpret_cast<uint16_t*>(player1.data() + 0x3DE) = 0x0202u;
    *reinterpret_cast<uint16_t*>(player1.data() + 0x3E0) = 0x0048u;
    *reinterpret_cast<uint8_t*>(player1.data() + 0x51A) = 1;
    *reinterpret_cast<uint8_t*>(player1.data() + 0x66B) = 1;
    *reinterpret_cast<void**>(player1.data() + 0x730) = dummyBrain1.data();

    // CPlayer 2: clientNum=7, team=2, heroIdx=42, connectionless bot (0x0202), botBrain active (0x0048), m_bIsBot=1 (+0x66B)
    *reinterpret_cast<int32_t*>(player2.data() + 0xA8) = 7;
    *reinterpret_cast<int32_t*>(player2.data() + 0xAC) = 2;
    *reinterpret_cast<uint32_t*>(player2.data() + 0xB0) = 42u;
    *reinterpret_cast<int32_t*>(player2.data() + 0x2D0) = 0;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x2D8) = 5;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x2DA) = 6;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x2DC) = 7;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3DE) = 0x0202u;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) = 0x0048u;
    *reinterpret_cast<uint8_t*>(player2.data() + 0x66B) = 1;
    *reinterpret_cast<void**>(player2.data() + 0x730) = dummyBrain2.data();

    // Wire RB-tree: head(+0x19=1), leftmost=nodeTeam1, nodeTeam1.right=nodeTeam2
    headNode[0x19] = 1;
    *reinterpret_cast<void**>(headNode.data() + 0x00) = nodeTeam1.data();
    *reinterpret_cast<void**>(headNode.data() + 0x08) = nodeTeam1.data();
    *reinterpret_cast<void**>(headNode.data() + 0x10) = nodeTeam2.data();

    nodeTeam1[0x19] = 0;
    *reinterpret_cast<void**>(nodeTeam1.data() + 0x00) = headNode.data();
    *reinterpret_cast<void**>(nodeTeam1.data() + 0x08) = headNode.data();
    *reinterpret_cast<void**>(nodeTeam1.data() + 0x10) = nodeTeam2.data();
    *reinterpret_cast<int32_t*>(nodeTeam1.data() + 0x20) = 3;
    *reinterpret_cast<void**>(nodeTeam1.data() + 0x28) = player1.data();

    nodeTeam2[0x19] = 0;
    *reinterpret_cast<void**>(nodeTeam2.data() + 0x00) = headNode.data();
    *reinterpret_cast<void**>(nodeTeam2.data() + 0x08) = nodeTeam1.data();
    *reinterpret_cast<void**>(nodeTeam2.data() + 0x10) = headNode.data();
    *reinterpret_cast<int32_t*>(nodeTeam2.data() + 0x20) = 7;
    *reinterpret_cast<void**>(nodeTeam2.data() + 0x28) = player2.data();

    *reinterpret_cast<void**>(gameImg.data() + 0x28) = MockQueueArcade::InitMockEntityDirectory(42u, true);
    *reinterpret_cast<void**>(gameImg.data() + 0xB0) = gameInfoImg.data();
    *reinterpret_cast<void**>(gameImg.data() + 0x1E8) = headNode.data();
    void* pGame = gameImg.data();

    Kongor::QueueArcadeEngineContext ctx = {};
    ctx.ppGame = &pGame;
    ctx.pfnRemoveBot = &MockQueueArcade::MockRemoveBot;
    ctx.ppNetResourceManager = &MockQueueArcade::s_dummyNetResMgr;
    ctx.pfnSetNetString = &MockQueueArcade::MockSetNetString;
    ctx.pfnGetNetString = &MockQueueArcade::MockGetNetString;
    MockQueueArcade::s_removeBotCalls = 0;

    // satisfies: OBL-QA-09 — Pre-Phase-5 duplicate SpawnHero suppression vs Phase >= 5 repick pass-through
    *reinterpret_cast<int32_t*>(gameInfoImg.data() + 0xB4) = 4; // GAME_PHASE_HERO_LOADING (< 5)
    *reinterpret_cast<uint32_t*>(player2.data() + 0xB0) = 0xFFFFFFFFu;
    ExpectTrue(!Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(player2.data(), &ctx),
               "QA_OBL09_FirstPrePhase5SpawnHeroAllowedWhenHeroIndexUnset");
    *reinterpret_cast<uint32_t*>(player2.data() + 0xB0) = 42u;
    ExpectTrue(Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(player2.data(), &ctx),
               "QA_OBL09_DuplicatePrePhase5SpawnHeroSuppressedWhenLiveHeroExists");
    *reinterpret_cast<uint32_t*>(player2.data() + 0xB0) = 999u; // Stale/non-existent entity index
    ExpectTrue(!Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(player2.data(), &ctx),
               "QA_OBL09_PrePhase5SpawnHeroAllowedWhenEntityMissing");
    *reinterpret_cast<uint32_t*>(player2.data() + 0xB0) = 42u;
    *reinterpret_cast<int32_t*>(gameInfoImg.data() + 0xB4) = 5; // GAME_PHASE_PRE_MATCH (>= 5)
    ExpectTrue(!Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(player2.data(), &ctx),
               "QA_OBL09_Phase5RepickSpawnHeroNeverSuppressed");
    *reinterpret_cast<int32_t*>(gameInfoImg.data() + 0xB4) = 6; // GAME_PHASE_ACTIVE (>= 5)
    ExpectTrue(!Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(player2.data(), &ctx),
               "QA_OBL09_Phase6RiftWarsRepickSpawnHeroNeverSuppressed");

    // OBL-QA-02 / OBL-QA-06 / OBL-QA-07 / OBL-QA-08 / OBL-QA-10: Pre-dirty disconnect/loading/upgrade state
    // and reserve Team 2 slot in Mode 1 (ActiveBots): parks dummyBrain2 WITHOUT calling pfnRemoveBot!
    // satisfies: OBL-QA-10
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3DE) |= (0x0101u | 0x0040u | 0x3014u);
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) |= 0x0020u;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x420) = 88u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x430) = 482500u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x434) = 12000u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x438) = 722500u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x660) = 0x7Fu;
    *reinterpret_cast<uint8_t*>(player2.data() + 0x668) = 0u;

    auto r2 = Kongor::ArcadeReserveSlot(50123, 2, L"[GUCI]ArcadeHero", &ctx);
    ExpectTrue(r2.success && r2.clientNum == 7 && r2.teamId == 2 &&
               r2.removedBotBrain && r2.detachedBotBrain == dummyBrain2.data() && r2.updatedNetStrings,
               "QA_OBL02_10_ReserveTeam2ParksBotBrainAndReturnsClientNum7");
    ExpectTrue(MockQueueArcade::s_removeBotCalls == 0 &&
               *reinterpret_cast<void**>(player2.data() + 0x730) == nullptr &&
               (*reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) & 0x0048u) == 0 &&
               *reinterpret_cast<int32_t*>(player2.data() + 0x2D0) == 50123 &&
               (*reinterpret_cast<uint16_t*>(player2.data() + 0x3DE) & (0x0200u | 0x0101u | 0x0040u | 0x3014u)) == 0 &&
               (*reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) & 0x0020u) == 0 &&
               *reinterpret_cast<uint16_t*>(player2.data() + 0x420) == 0u &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x430) == 0xFFFFFFFFu &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x434) == 0u &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x438) == 0xFFFFFFFFu &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x660) == 0 &&
               *reinterpret_cast<uint8_t*>(player2.data() + 0x668) == 1u &&
               *reinterpret_cast<uint8_t*>(player2.data() + 0x66B) == 0 &&
               r2.priorPostInit668 == 0u && r2.postInit668 == 1u &&
               r2.priorDiscTerm438 == 722500u && r2.postDiscTerm438 == 0xFFFFFFFFu &&
               MockQueueArcade::s_netStrings[5] == L"[GUCI]ArcadeHero" &&
               MockQueueArcade::s_netStrings[6].empty() &&
               MockQueueArcade::s_netStrings[7].empty(),
               "QA_OBL06_07_08_10_BotBrainParkedZeroedDisconnectClearedPostInitPreservedAndClanTaggedNameStamped");

    // Idempotent retry of ArcadeReserveSlot for the same accountId must preserve parkedBotBrain and savedBotName!
    auto r2Retry = Kongor::ArcadeReserveSlot(50123, 2, L"[GUCI]ArcadeHero", &ctx);
    ExpectTrue(r2Retry.success && r2Retry.clientNum == 7 && MockQueueArcade::s_removeBotCalls == 0,
               "QA_OBL10_IdempotentRetryPreservesParkedBotBrain");

    // OBL-QA-03: Construct synthetic CClientConnection at +0x20 (unConnectionID=0) and +0x28 (K2::wstring "ArcadeHero")
    // before accountId is populated (accountId == 0 during FUN_180721e40)
    std::vector<uint8_t> mockConn(0x60, 0);
    const wchar_t* taggedName = L"ArcadeHero";
    const size_t nameLen = wcslen(taggedName);
    *reinterpret_cast<uint16_t*>(mockConn.data() + 0x20) = 0; // unConnectionID == 0
    *reinterpret_cast<const wchar_t**>(mockConn.data() + 0x28) = taggedName;
    *reinterpret_cast<size_t*>(mockConn.data() + 0x38) = nameLen;
    *reinterpret_cast<size_t*>(mockConn.data() + 0x40) = 15; // heap string capacity > 7

    const uint16_t* pConnId = reinterpret_cast<const uint16_t*>(mockConn.data() + 0x20);
    ExpectTrue(Kongor::ResolveReservedArcadeClientId(pConnId, 0) == 7,
               "QA_OBL03_ResolveClientIdByClanTaggedNameWhenAccountIdZero");
    ExpectTrue(Kongor::ResolveReservedArcadeClientId(nullptr, 50123) == 7,
               "QA_OBL03_ResolveClientIdByAccountId");
    // When accountId > 0 is resolved, never fall back to name matching for a different accountId!
    ExpectTrue(Kongor::ResolveReservedArcadeClientId(pConnId, 88888) == -1,
               "QA_OBL03_PositiveAccountIdNeverFallsBackToNameMatch");

    // Pseudo/bot sentinel cookie 0xDCD8 must never hijack human reservation
    *reinterpret_cast<uint16_t*>(mockConn.data() + 0x20) = 0xDCD8u;
    ExpectTrue(Kongor::ResolveReservedArcadeClientId(pConnId, 0) == -1,
               "QA_OBL03_PseudoBotCookieSentinelIgnored");

    // Validate HDE64 safety of both K2_SERVER (16B) and K2_CLIENT (15B) GenerateClientID prologues
    static const uint8_t patServer16[] = {
        0x48, 0x89, 0x74, 0x24, 0x10, 0x48, 0x89, 0x7C, 0x24, 0x18, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x30
    };
    static const uint8_t patClient15[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x30
    };
    ExpectTrue(Kongor::PatchController::ValidateHookPrologue64(patServer16, sizeof(patServer16), true),
               "QA_OBL03_ServerGenerateClientId16BytePrologueValid");
    ExpectTrue(Kongor::PatchController::ValidateHookPrologue64(patClient15, sizeof(patClient15), true),
               "QA_OBL03_ClientGenerateClientId15BytePrologueValid");

    // OBL-QA-04 / OBL-QA-06: PartyDormantSlots parks remaining unreserved Team 1 bot via pfnRemoveBot without touching reserved Team 2 slot,
    // zeroing +0x730 even when pfnRemoveBot leaves a poisoned dangling pointer!
    uint32_t parked = Kongor::ArcadeParkUnreservedBots(&ctx);
    ExpectTrue(parked == 1 &&
               MockQueueArcade::s_removeBotCalls == 1 &&
               *reinterpret_cast<void**>(player1.data() + 0x730) == nullptr &&
               (*reinterpret_cast<uint16_t*>(player1.data() + 0x3E0) & 0x0048u) == 0,
               "QA_OBL04_06_PartyModeParksUnreservedBotAndZeroesDanglingBrain");

    // Cycle 1 Release (Mode 1 ActiveBots): verify ArcadeReleaseSlot scrubs disconnect timers, keeps +0x668 = 1,
    // restores "ArtilleryBot", AND re-docks parked dummyBrain2 (+0x730, +0x10 = 42u, +0x3E0 |= 0x0008)!
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3DE) |= (0x0101u | 0x0040u | 0x3014u);
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) |= 0x0020u;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x420) = 45u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x430) = 587450u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x434) = 5000u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x438) = 827450u;
    *reinterpret_cast<uint32_t*>(player2.data() + 0x660) = 0x55u;
    *reinterpret_cast<uint8_t*>(player2.data() + 0x668) = 0u;

    ExpectTrue(Kongor::ArcadeReleaseSlot(50123, &ctx), "QA_OBL04_ReleaseSlotSucceeds");
    const uint16_t postReleaseFlags = *reinterpret_cast<uint16_t*>(player2.data() + 0x3DE);
    const uint16_t postReleaseFlags2 = *reinterpret_cast<uint16_t*>(player2.data() + 0x3E0);
    ExpectTrue(MockQueueArcade::s_removeBotCalls == 1 &&
               *reinterpret_cast<void**>(player2.data() + 0x730) == dummyBrain2.data() &&
               *reinterpret_cast<uint32_t*>(dummyBrain2.data() + 0x10) == 42u &&
               (postReleaseFlags2 & 0x0008u) != 0 &&
               *reinterpret_cast<int32_t*>(player2.data() + 0x2D0) == 0 &&
               (postReleaseFlags & 0x0200u) != 0 &&
               (postReleaseFlags & (0x0101u | 0x0080u | 0x0040u | 0x3014u)) == 0 &&
               (postReleaseFlags2 & 0x0020u) == 0 &&
               *reinterpret_cast<uint16_t*>(player2.data() + 0x420) == 0u &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x430) == 0xFFFFFFFFu &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x434) == 0u &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x438) == 0xFFFFFFFFu &&
               *reinterpret_cast<uint32_t*>(player2.data() + 0x660) == 0 &&
               *reinterpret_cast<uint8_t*>(player2.data() + 0x668) == 1u &&
               *reinterpret_cast<uint8_t*>(player2.data() + 0x66B) == 1 &&
               MockQueueArcade::s_netStrings[5] == L"ArtilleryBot" &&
               MockQueueArcade::s_netStrings[6].empty() &&
               Kongor::ResolveReservedArcadeClientId(nullptr, 50123) == -1,
               "QA_OBL04_07_08_10_ReleasedSlotRestoresParkedBrainHeroIdxAndBotIdentityCleanly");

    // Cycle 2 Reserve -> Release -> Reserve -> Terminate on the SAME slot (Opus double-cycle & Lua-safe teardown gate):
    auto r2Cycle2 = Kongor::ArcadeReserveSlot(60999, 2, L"SecondJoiner", &ctx);
    ExpectTrue(r2Cycle2.success && r2Cycle2.clientNum == 7 &&
               r2Cycle2.detachedBotBrain == dummyBrain2.data() &&
               *reinterpret_cast<void**>(player2.data() + 0x730) == nullptr &&
               MockQueueArcade::s_removeBotCalls == 1,
               "QA_OBL10_SecondCycleReserveParksRestoredBrainAgainWithoutFree");
    ExpectTrue(Kongor::ArcadeReleaseSlot(60999, &ctx) &&
               *reinterpret_cast<void**>(player2.data() + 0x730) == dummyBrain2.data() &&
               (*reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) & 0x0008u) != 0 &&
               MockQueueArcade::s_netStrings[5] == L"ArtilleryBot" &&
               MockQueueArcade::s_removeBotCalls == 1,
               "QA_OBL10_SecondCycleReleaseRestoresBrainAgainWithoutLeak");

    // Now reserve a 3rd time and trigger CPlayer::Terminate while human is still in-game -> must re-dock & free via pfnRemoveBot once!
    auto r2Cycle3 = Kongor::ArcadeReserveSlot(70888, 2, L"MatchEndHuman", &ctx);
    ExpectTrue(r2Cycle3.success && *reinterpret_cast<void**>(player2.data() + 0x730) == nullptr,
               "QA_OBL10_ThirdReserveParksBeforeMatchEnd");
    ExpectTrue(Kongor::ArcadeReleaseParkedBotOnPlayerTerminate(player2.data(), &ctx) &&
               MockQueueArcade::s_removeBotCalls == 2 &&
               *reinterpret_cast<void**>(player2.data() + 0x730) == nullptr &&
               (*reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) & 0x0048u) == 0 &&
               !Kongor::ArcadeReleaseParkedBotOnPlayerTerminate(player2.data(), &ctx) &&
               MockQueueArcade::s_removeBotCalls == 2,
               "QA_OBL10_TerminateFreesParkedBrainOnceWhileLuaLiveAndPreventsDoubleFree");

    // Also verify Mode 2 (PartyDormantSlots) ArcadeReserveSlot immediately calls pfnRemoveBot instead of parking!
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::PartyDormantSlots);
    *reinterpret_cast<int32_t*>(player2.data() + 0x2D0) = 0;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3DE) = 0x0202u;
    *reinterpret_cast<uint16_t*>(player2.data() + 0x3E0) = 0x0048u;
    *reinterpret_cast<void**>(player2.data() + 0x730) = dummyBrain2.data();
    auto rParty = Kongor::ArcadeReserveSlot(80777, 2, L"PartyJoiner", &ctx);
    ExpectTrue(rParty.success && rParty.detachedBotBrain == nullptr &&
               MockQueueArcade::s_removeBotCalls == 3 &&
               *reinterpret_cast<void**>(player2.data() + 0x730) == nullptr,
               "QA_OBL06_PartyModeReserveDestroysBotBrainImmediatelyAndZeroes730");
    Kongor::ArcadeReleaseSlot(80777, &ctx);
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::ActiveBots);

    // Verify CArchive::ExamineChecksums disable helper + KONGOR_EXAMINE_ARCHIVE_CHECKSUMS=1 opt-out
    bool examineChecksums = true;
    SetEnvironmentVariableA("KONGOR_EXAMINE_ARCHIVE_CHECKSUMS", nullptr);
    ExpectTrue(Kongor::DisableArchiveChecksumsIfAllowed(&examineChecksums) && !examineChecksums,
               "QA_ExamineChecksumsDisabledByDefault");
    examineChecksums = true;
    SetEnvironmentVariableA("KONGOR_EXAMINE_ARCHIVE_CHECKSUMS", "1");
    ExpectTrue(!Kongor::DisableArchiveChecksumsIfAllowed(&examineChecksums) && examineChecksums,
               "QA_ExamineChecksumsPreservedWhenEnvOptOutSet");
    SetEnvironmentVariableA("KONGOR_EXAMINE_ARCHIVE_CHECKSUMS", nullptr);

    // OBL-QA-07: Safe Mode fail-closed refusal (never auto-re-arms hooks; retires unusable Arcade server cleanly)
    Kongor::PatchController::SetSafeMode(true, "LAST_CRASHED_SENTINEL");
    auto safeModeReserve = Kongor::ArcadeReserveSlot(77777, 1, L"SafeModePlayer", &ctx);
    ExpectTrue(!safeModeReserve.success && strcmp(safeModeReserve.reason, "arcade_hooks_disarmed") == 0,
               "QA_OBL07_SafeModeRefusesArcadeReserveSlot");
    Kongor::ExecuteConsoleArcadeModeW(L"1");
    ExpectTrue(!Kongor::IsQueueArcadeModeActive() &&
               Kongor::ConsumePendingArcadeSafeModeRetirement() &&
               !Kongor::ConsumePendingArcadeSafeModeRetirement(),
               "QA_OBL07_SafeModeSchedulesCleanArcadeServerRetirementWithoutReArm");
    Kongor::PatchController::SetSafeMode(false, nullptr);
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::ActiveBots);

    // SEH negative control on unmapped IGame* (0xDEAD0000ULL)
    void* badGame = reinterpret_cast<void*>(0xDEAD0000ULL);
    ctx.ppGame = &badGame;
    auto badRes = Kongor::ArcadeReserveSlot(99999, 1, L"CrashProbe", &ctx);
    ExpectTrue(!badRes.success && strcmp(badRes.reason, "seh_access_violation") == 0,
               "QA_OBL02_SehRecoversUnmappedIGamePointer");

    // OBL-QA-05: BaseCmdTable O(1) lookup + CmdSanitizer privilege firewall + compound ';' batch partitioning
    const wchar_t* cmdArgs = nullptr;
    const auto* matchReserve = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"kongor_arcade_reserve 50123 2 ArcadeHero", &cmdArgs);
    ExpectTrue(matchReserve != nullptr && matchReserve->Id == Kongor::KongorConsoleCmdId::ArcadeReserve,
               "QA_OBL05_BaseCmdTableMatchesArcadeReserve");
    ExpectTrue(!Kongor::CmdSanitizer::ShouldAllowCommandW(L"kongor_arcade_reserve 50123 2 ArcadeHero", false),
               "QA_OBL05_CmdSanitizerBlocksUnprivilegedIpc");
    ExpectTrue(Kongor::CmdSanitizer::ShouldAllowCommandW(L"kongor_arcade_reserve 50123 2 ArcadeHero", true),
               "QA_OBL05_CmdSanitizerAllowsPrivilegedServerConsole");

    // Mid-batch ';' command (ServerHandshake pattern): must extract kongor_arcade_mode without leaking it to K2
    std::vector<Kongor::DispatchedConsoleSegment> midBatchSegs;
    ExpectTrue(Kongor::PartitionCompoundBaseCmdBufferW(
                   L"svr_submitMatchStatFrags false;sv_afkTimeout 0;kongor_arcade_mode 1;svr_name \"QUEUE ARCADE #1\";echo Ready",
                   midBatchSegs) &&
               midBatchSegs.size() == 3 &&
               !midBatchSegs[0].IsNativeBaseCmd &&
               midBatchSegs[0].CommandOrArgs == L"svr_submitMatchStatFrags false;sv_afkTimeout 0" &&
               midBatchSegs[1].IsNativeBaseCmd &&
               midBatchSegs[1].CmdId == Kongor::KongorConsoleCmdId::ArcadeMode &&
               midBatchSegs[1].CommandOrArgs == L"1" &&
               !midBatchSegs[2].IsNativeBaseCmd &&
               midBatchSegs[2].CommandOrArgs == L"svr_name \"QUEUE ARCADE #1\";echo Ready",
               "QA_OBL05_PartitionCompoundMidBatchExtractsArcadeModeCleanly");

    // Leading-batch ';' command (QueueArcadeCoordinator preMatchCommands pattern): must NOT swallow trailing CVars
    std::vector<Kongor::DispatchedConsoleSegment> leadBatchSegs;
    ExpectTrue(Kongor::PartitionCompoundBaseCmdBufferW(
                   L"kongor_arcade_mode 2;sv_gameEndPhaseTime 500;sv_matchmakingDisconnectCheck false",
                   leadBatchSegs) &&
               leadBatchSegs.size() == 2 &&
               leadBatchSegs[0].IsNativeBaseCmd &&
               leadBatchSegs[0].CmdId == Kongor::KongorConsoleCmdId::ArcadeMode &&
               leadBatchSegs[0].CommandOrArgs == L"2" &&
               !leadBatchSegs[1].IsNativeBaseCmd &&
               leadBatchSegs[1].CommandOrArgs == L"sv_gameEndPhaseTime 500;sv_matchmakingDisconnectCheck false",
               "QA_OBL05_PartitionCompoundLeadingBatchPreservesTrailingCvars");

    // Pure K2 compound command & __kongor_eval_res payload with ';' must NOT be partitioned
    std::vector<Kongor::DispatchedConsoleSegment> noSplitSegs;
    ExpectTrue(!Kongor::PartitionCompoundBaseCmdBufferW(
                   L"Set dev true; StartGame practice GameName test map:caldavar",
                   noSplitSegs),
               "QA_OBL05_PureK2CompoundCommandLeftUntouched");
    ExpectTrue(!Kongor::PartitionCompoundBaseCmdBufferW(
                   L"__kongor_eval_res 42 1 a;b;c",
                   noSplitSegs),
               "QA_OBL05_EvalResSemicolonPayloadLeftUntouched");

    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::Disabled);
    Kongor::ClearAllArcadeReservations();
}

// ============================================================================
// Protects: Post-Beauty-Empath background asset warmup RAM guard (> 8 GiB total
//           & >= 3 GiB free), zero boot-time disk prewarm on InitializeAssetPrewarmer,
//           immediate foreground PreloadWorld busy yield, player disable/enable toggle,
//           and BaseCmdTable O(1) lookup + compound ';' splitting for prewarm_post_intro
//           and prewarm_foreground_busy.
// Fails when: Boot prewarming is re-enabled at startup, <= 8 GiB PCs pass the RAM guard,
//             sorted BaseCmdTable buckets misorder prewarm_* nodes, or foreground busy
//             fails to halt PrewarmFile reads.
// Seam: Real compiled asset_prewarmer.cpp + kongor_base_cmd.h + Win32 file I/O.
// ============================================================================
namespace MockTrack5Arcade {
    static void* s_fakeHostServer = reinterpret_cast<void*>(0x77770000ULL);
    static void* s_lingeringConns[32] = {};
    static int s_kickCalls = 0;
    static int32_t s_lastKickedClient = -1;
    static int32_t s_lastKickReason = -1;
    static bool s_kickSawReentrancyGuard = false;
    static bool s_reentrantDiscSuppressed = false;
    static void* s_playerForReentrantCheck = nullptr;
    static const Kongor::QueueArcadeEngineContext* s_ctxForReentrantCheck = nullptr;

    static int s_attachBotCalls = 0;
    static const void* s_lastAttachBotDef = nullptr;
    static uint8_t s_healedBrainStorage[0x40] = {};

    static std::vector<std::wstring> s_queuedConsoleCmds;

    static void Reset() {
        memset(s_lingeringConns, 0, sizeof(s_lingeringConns));
        s_kickCalls = 0;
        s_lastKickedClient = -1;
        s_lastKickReason = -1;
        s_kickSawReentrancyGuard = false;
        s_reentrantDiscSuppressed = false;
        s_playerForReentrantCheck = nullptr;
        s_ctxForReentrantCheck = nullptr;
        s_attachBotCalls = 0;
        s_lastAttachBotDef = nullptr;
        memset(s_healedBrainStorage, 0, sizeof(s_healedBrainStorage));
        *reinterpret_cast<uint32_t*>(s_healedBrainStorage + 0x10) = 0xFFFFFFFFu;
        s_queuedConsoleCmds.clear();
    }

    static void* __fastcall MockGetClient(void* pHostServer, int32_t clientNum) {
        if (pHostServer != s_fakeHostServer || clientNum < 0 || clientNum >= 32) return nullptr;
        return s_lingeringConns[clientNum];
    }

    static void __fastcall MockKickClient(void* pHostServer, int32_t clientNum, int32_t reason) {
        if (pHostServer != s_fakeHostServer) return;
        ++s_kickCalls;
        s_lastKickedClient = clientNum;
        s_lastKickReason = reason;
        s_kickSawReentrancyGuard = Kongor::IsArcadeKickReentrancyActive();
        if (clientNum >= 0 && clientNum < 32) {
            s_lingeringConns[clientNum] = nullptr;
        }
        if (s_playerForReentrantCheck && s_ctxForReentrantCheck) {
            s_reentrantDiscSuppressed = Kongor::OnArcadePlayerDisconnected(
                s_playerForReentrantCheck, 1000u, 240000u, s_ctxForReentrantCheck);
        }
    }

    static void* __fastcall MockAttachBot(void* pPlayer, const void* pBotDef) {
        ++s_attachBotCalls;
        s_lastAttachBotDef = pBotDef;
        if (pPlayer) {
            *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(pPlayer) + 0x730) = s_healedBrainStorage;
        }
        return s_healedBrainStorage;
    }

    static void __fastcall MockQueueConsoleCommand(const wchar_t* cmdLine) {
        if (cmdLine) {
            s_queuedConsoleCmds.emplace_back(cmdLine);
        }
    }
}

void TestQueueArcadeTrack5SwapParityAndSelfHeal() {
    MockTrack5Arcade::Reset();
    MockQueueArcade::ResetNetStrings();
    MockQueueArcade::s_removeBotCalls = 0;
    Kongor::ClearAllArcadeReservations();
    Kongor::SetQueueArcadeSwapHooksEnabled(true);
    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::ActiveBots);

    static const uint8_t patWinDisconnected15[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x20
    };
    static const uint8_t patWinAttachBot16[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57
    };
    ExpectTrue(Kongor::PatchController::ValidateHookPrologue64(patWinDisconnected15, sizeof(patWinDisconnected15), true),
               "QA_Track5_WinDisconnected15BytePrologueValid");
    ExpectTrue(Kongor::PatchController::ValidateHookPrologue64(patWinAttachBot16, sizeof(patWinAttachBot16), true),
               "QA_Track5_WinAttachBot16BytePrologueValid");

    std::vector<uint8_t> gameImg(0x400, 0);
    std::vector<uint8_t> gameInfoImg(0x100, 0);
    std::vector<uint8_t> headNode(0x30, 0);
    std::vector<uint8_t> node0(0x30, 0);
    std::vector<uint8_t> node1(0x30, 0);
    std::vector<uint8_t> node2(0x30, 0);
    std::vector<uint8_t> p0(0x800, 0), p1(0x800, 0), p2(0x800, 0);
    std::vector<uint8_t> brain0(0x30, 0), brain1(0x30, 0), brain2(0x30, 0);

    auto initBot = [](std::vector<uint8_t>& pl, std::vector<uint8_t>& br, int32_t cnum, int32_t team, uint32_t heroIdx, uint16_t nameIdx) {
        std::fill(pl.begin(), pl.end(), 0);
        std::fill(br.begin(), br.end(), 0);
        *reinterpret_cast<uint32_t*>(br.data() + 0x10) = heroIdx;
        *reinterpret_cast<int32_t*>(pl.data() + 0xA8) = cnum;
        *reinterpret_cast<int32_t*>(pl.data() + 0xAC) = team;
        *reinterpret_cast<uint32_t*>(pl.data() + 0xB0) = heroIdx;
        *reinterpret_cast<uint16_t*>(pl.data() + 0x2D8) = nameIdx;
        *reinterpret_cast<uint16_t*>(pl.data() + 0x2DA) = static_cast<uint16_t>(nameIdx + 1);
        *reinterpret_cast<uint16_t*>(pl.data() + 0x2DC) = static_cast<uint16_t>(nameIdx + 2);
        *reinterpret_cast<uint16_t*>(pl.data() + 0x3DE) = 0x0202u;
        *reinterpret_cast<uint16_t*>(pl.data() + 0x3E0) = 0x0048u;
        *reinterpret_cast<uint32_t*>(pl.data() + 0x430) = 0xFFFFFFFFu;
        *reinterpret_cast<uint32_t*>(pl.data() + 0x438) = 0xFFFFFFFFu;
        *reinterpret_cast<uint8_t*>(pl.data() + 0x668) = 1;
        *reinterpret_cast<uint8_t*>(pl.data() + 0x66B) = 1;
        *reinterpret_cast<void**>(pl.data() + 0x730) = br.data();
    };

    MockQueueArcade::SeedNetString(0, L"ChronosBot");
    MockQueueArcade::SeedNetString(3, L"NitroBot");
    MockQueueArcade::SeedNetString(6, L"FlintBot");
    initBot(p0, brain0, 0, 1, 101u, 0);
    initBot(p1, brain1, 1, 1, 102u, 3);
    initBot(p2, brain2, 5, 2, 103u, 6);

    headNode[0x19] = 1;
    *reinterpret_cast<void**>(headNode.data() + 0x00) = node0.data();
    *reinterpret_cast<void**>(headNode.data() + 0x08) = node1.data();
    *reinterpret_cast<void**>(headNode.data() + 0x10) = node2.data();

    node1[0x19] = 0;
    *reinterpret_cast<void**>(node1.data() + 0x00) = node0.data();
    *reinterpret_cast<void**>(node1.data() + 0x08) = headNode.data();
    *reinterpret_cast<void**>(node1.data() + 0x10) = node2.data();
    *reinterpret_cast<int32_t*>(node1.data() + 0x20) = 1;
    *reinterpret_cast<void**>(node1.data() + 0x28) = p1.data();

    node0[0x19] = 0;
    *reinterpret_cast<void**>(node0.data() + 0x00) = headNode.data();
    *reinterpret_cast<void**>(node0.data() + 0x08) = node1.data();
    *reinterpret_cast<void**>(node0.data() + 0x10) = headNode.data();
    *reinterpret_cast<int32_t*>(node0.data() + 0x20) = 0;
    *reinterpret_cast<void**>(node0.data() + 0x28) = p0.data();

    node2[0x19] = 0;
    *reinterpret_cast<void**>(node2.data() + 0x00) = headNode.data();
    *reinterpret_cast<void**>(node2.data() + 0x08) = node1.data();
    *reinterpret_cast<void**>(node2.data() + 0x10) = headNode.data();
    *reinterpret_cast<int32_t*>(node2.data() + 0x20) = 5;
    *reinterpret_cast<void**>(node2.data() + 0x28) = p2.data();

    *reinterpret_cast<int32_t*>(gameInfoImg.data() + 0xB4) = 5;
    *reinterpret_cast<void**>(gameImg.data() + 0xB0) = gameInfoImg.data();
    *reinterpret_cast<void**>(gameImg.data() + 0x1E8) = headNode.data();
    void* pGame = gameImg.data();

    Kongor::QueueArcadeEngineContext ctx = {};
    ctx.ppGame = &pGame;
    ctx.pfnRemoveBot = &MockQueueArcade::MockRemoveBot;
    ctx.pfnAttachBot = &MockTrack5Arcade::MockAttachBot;
    ctx.ppNetResourceManager = &MockQueueArcade::s_dummyNetResMgr;
    ctx.pfnSetNetString = &MockQueueArcade::MockSetNetString;
    ctx.pfnGetNetString = &MockQueueArcade::MockGetNetString;
    ctx.pHostServerOverride = MockTrack5Arcade::s_fakeHostServer;
    ctx.pfnGetClient = &MockTrack5Arcade::MockGetClient;
    ctx.pfnKickClient = &MockTrack5Arcade::MockKickClient;
    ctx.pfnQueueConsoleCommand = &MockTrack5Arcade::MockQueueConsoleCommand;

    const void* const kFakeNitroBotDef = reinterpret_cast<const void*>(0xB07DEF01ULL);
    Kongor::OnArcadePlayerAttachBot(p1.data(), kFakeNitroBotDef, &ctx);

    const void* queriedDef = nullptr;
    wchar_t queriedOriginName[64] = {};
    ExpectTrue(Kongor::GetArcadeSlotOrigin(1, &queriedDef, queriedOriginName, _countof(queriedOriginName)) &&
               queriedDef == kFakeNitroBotDef && wcscmp(queriedOriginName, L"NitroBot") == 0,
               "QA_Track5_AttachBotHookRecordsSlotOriginNameAndBotDef");

    MockTrack5Arcade::s_lingeringConns[0] = reinterpret_cast<void*>(0xC0440000ULL);
    auto rHumanA = Kongor::ArcadeReserveSlot(91001, 0, L"[KNG]HumanA", &ctx);
    ExpectTrue(rHumanA.success && rHumanA.clientNum == 1 && rHumanA.teamId == 1,
               "QA_Track5_GetClientCollisionGuardSkipsLingeringUdpSlot0AndPicksSlot1");
    ExpectTrue(MockTrack5Arcade::s_queuedConsoleCmds.size() == 1 &&
               MockTrack5Arcade::s_queuedConsoleCmds.back() == L"ServerChat \"[Queue Arcade] [KNG]HumanA replaced NitroBot!\"",
               "QA_Track5_JoinQueuesSanitizedServerChatNotice");

    *reinterpret_cast<uint16_t*>(p2.data() + 0x3DE) |= (0x0101u | 0x0040u | 0x8000u);
    *reinterpret_cast<uint32_t*>(p2.data() + 0x430) = 12345u;
    *reinterpret_cast<uint32_t*>(p2.data() + 0x438) = 67890u;
    ExpectTrue(Kongor::OnArcadePlayerDisconnected(p2.data(), 1000u, 240000u, &ctx) &&
               (*reinterpret_cast<uint16_t*>(p2.data() + 0x3DE) & (0x0101u | 0x0080u | 0x0040u | 0x8000u)) == 0 &&
               *reinterpret_cast<uint32_t*>(p2.data() + 0x430) == 0xFFFFFFFFu &&
               *reinterpret_cast<uint32_t*>(p2.data() + 0x438) == 0xFFFFFFFFu,
               "QA_Track5_DisconnectedHookSuppressesAndScrubsBotSlotIncludingAfkBit");

    ExpectTrue(Kongor::OnArcadePlayerDisconnected(p1.data(), 2000u, 240000u, &ctx) &&
               *reinterpret_cast<int32_t*>(p1.data() + 0x2D0) == 0 &&
               *reinterpret_cast<uint8_t*>(p1.data() + 0x66B) == 1 &&
               *reinterpret_cast<void**>(p1.data() + 0x730) == brain1.data() &&
               MockQueueArcade::s_netStrings[3] == L"NitroBot" &&
               MockTrack5Arcade::s_queuedConsoleCmds.back() == L"ServerChat \"[Queue Arcade] [KNG]HumanA left - NitroBot took over!\"",
               "QA_Track5_DisconnectedHookImmediatelyHandsHumanSlotBackToNitroBot");

    ctx.pfnGetNetString = nullptr;
    auto rHumanB = Kongor::ArcadeReserveSlot(91002, 1, L"HumanB\"Inject;Quit", &ctx);
    ExpectTrue(rHumanB.success && rHumanB.clientNum == 1 &&
               MockTrack5Arcade::s_queuedConsoleCmds.back() == L"ServerChat \"[Queue Arcade] HumanBInjectQuit replaced NitroBot!\"",
               "QA_Track5_SecondSwapSanitizesChatQuotesAndPreservesNitroBotLabel");

    ExpectTrue(Kongor::OnArcadePlayerTerminate(p1.data(), &ctx) &&
               MockQueueArcade::s_removeBotCalls == 0 &&
               (*reinterpret_cast<uint16_t*>(p1.data() + 0x3DE) & 0x0080u) == 0 &&
               (*reinterpret_cast<uint16_t*>(p1.data() + 0x3DE) & 0x0200u) != 0 &&
               *reinterpret_cast<uint8_t*>(p1.data() + 0x66B) == 1 &&
               *reinterpret_cast<void**>(p1.data() + 0x730) == brain1.data() &&
               MockQueueArcade::s_netStrings[3] == L"NitroBot",
               "QA_Track5_ActivePhaseTerminateSuppressedAndRestoresBotWithoutDeletingBrain");
    ctx.pfnGetNetString = &MockQueueArcade::MockGetNetString;

    auto rHumanC = Kongor::ArcadeReserveSlot(91003, 1, L"HumanC", &ctx);
    ExpectTrue(rHumanC.success && rHumanC.clientNum == 1, "QA_Track5_ThirdReserveSlot1");
    Kongor::ArcadeReleaseParkedBotOnPlayerTerminate(p1.data(), &ctx);
    Kongor::RecordArcadeSlotOrigin(1, kFakeNitroBotDef, L"NitroBot");
    ExpectTrue(*reinterpret_cast<void**>(p1.data() + 0x730) == nullptr &&
               MockTrack5Arcade::s_attachBotCalls == 0,
               "QA_Track5_PreSelfHealSlotHasNullBrain");

    MockTrack5Arcade::s_lingeringConns[1] = reinterpret_cast<void*>(0xC0440001ULL);
    MockTrack5Arcade::s_playerForReentrantCheck = p1.data();
    MockTrack5Arcade::s_ctxForReentrantCheck = &ctx;
    ExpectTrue(Kongor::ArcadeReleaseSlot(91003, &ctx, /*kickClient=*/true) &&
               MockTrack5Arcade::s_attachBotCalls == 1 &&
               MockTrack5Arcade::s_lastAttachBotDef == kFakeNitroBotDef &&
               *reinterpret_cast<void**>(p1.data() + 0x730) == MockTrack5Arcade::s_healedBrainStorage &&
               *reinterpret_cast<uint32_t*>(MockTrack5Arcade::s_healedBrainStorage + 0x10) == 102u &&
               (*reinterpret_cast<uint16_t*>(p1.data() + 0x3E0) & 0x0008u) != 0 &&
               MockQueueArcade::s_netStrings[3] == L"NitroBot",
               "QA_Track5_AttachBotSelfHealsMissingBrainHeroIndexAndBotName");
    ExpectTrue(MockTrack5Arcade::s_kickCalls == 1 &&
               MockTrack5Arcade::s_lastKickedClient == 1 &&
               MockTrack5Arcade::s_lastKickReason == 0 &&
               MockTrack5Arcade::s_kickSawReentrancyGuard &&
               MockTrack5Arcade::s_reentrantDiscSuppressed &&
               !Kongor::IsArcadeKickReentrancyActive(),
               "QA_Track5_ReleaseKickInvokesKickClientUnderReentrancyGuardCleanly");

    *reinterpret_cast<int32_t*>(gameInfoImg.data() + 0xB4) = 7;
    ExpectTrue(!Kongor::OnArcadePlayerDisconnected(p1.data(), 3000u, 240000u, &ctx) &&
               !Kongor::OnArcadePlayerTerminate(p1.data(), &ctx),
               "QA_Track5_Phase7DelegatesDisconnectedAndTerminateToStockEngine");

    *reinterpret_cast<int32_t*>(gameInfoImg.data() + 0xB4) = 5;
    SetEnvironmentVariableA("KONGOR_ARCADE_SWAP_HOOKS", "0");
    Kongor::RefreshQueueArcadeSwapHooksFromEnv();
    ExpectTrue(!Kongor::IsQueueArcadeSwapHooksEnabled() &&
               !Kongor::OnArcadePlayerDisconnected(p1.data(), 3000u, 240000u, &ctx) &&
               !Kongor::OnArcadePlayerTerminate(p1.data(), &ctx),
               "QA_Track5_EnvKillSwitchDisablesSwapHooksCleanly");
    SetEnvironmentVariableA("KONGOR_ARCADE_SWAP_HOOKS", nullptr);

    const wchar_t* swapArgs = nullptr;
    const auto* matchSwapCmd = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"kongor_arcade_swap_hooks 1", &swapArgs);
    ExpectTrue(matchSwapCmd != nullptr &&
               matchSwapCmd->Id == Kongor::KongorConsoleCmdId::ArcadeSwapHooks &&
               !Kongor::CmdSanitizer::ShouldAllowCommandW(L"kongor_arcade_swap_hooks 0", false) &&
               Kongor::CmdSanitizer::ShouldAllowCommandW(L"kongor_arcade_swap_hooks 0", true),
               "QA_Track5_SwapHooksBaseCmdRegisteredAndSanitized");
    Kongor::ExecuteConsoleArcadeSwapHooksW(L"1");
    ExpectTrue(Kongor::IsQueueArcadeSwapHooksEnabled(), "QA_Track5_ConsoleCommandReEnablesSwapHooks");

    Kongor::SetQueueArcadeMode(Kongor::QueueArcadeServerMode::Disabled);
    *reinterpret_cast<uint16_t*>(p1.data() + 0x3DE) = 0x0202u;
    *reinterpret_cast<uint8_t*>(p1.data() + 0x66B) = 0;
    *reinterpret_cast<int32_t*>(p1.data() + 0x2D0) = 88001;
    Kongor::ReapConnectionlessBotsIfNoActiveHumans(&ctx);
    ExpectTrue((*reinterpret_cast<uint16_t*>(p0.data() + 0x3DE) & 0x0080u) == 0 &&
               (*reinterpret_cast<uint16_t*>(p2.data() + 0x3DE) & 0x0080u) == 0,
               "QA_Track5_ReapPreservesBotsWhenLoadedHumanWith0x0200IsPresent");

    *reinterpret_cast<uint16_t*>(p1.data() + 0x3DE) |= 0x0080u;
    Kongor::ReapConnectionlessBotsIfNoActiveHumans(&ctx);
    ExpectTrue((*reinterpret_cast<uint16_t*>(p0.data() + 0x3DE) & 0x0080u) != 0 &&
               (*reinterpret_cast<uint16_t*>(p2.data() + 0x3DE) & 0x0080u) != 0,
               "QA_Track5_ReapMarksBotsTerminatedOnceLastLoadedHumanTerminates");

    Kongor::ClearAllArcadeReservations();
}

void TestPostIntroBackgroundAssetPrewarmerAndCmdTable() {
    constexpr uint64_t kGiB = 1024ULL * 1024ULL * 1024ULL;

    // 1. RAM eligibility guard (> 8 GiB physical RAM => >= 10 GiB threshold, plus >= 3 GiB free)
    ExpectTrue(!Kongor::IsMemoryEligibleForBackgroundWarmup(8ULL * kGiB, 5ULL * kGiB),
               "BgPrewarm_RamGuard_Rejects8GiBMachine");
    ExpectTrue(!Kongor::IsMemoryEligibleForBackgroundWarmup(16ULL * kGiB, 2ULL * kGiB),
               "BgPrewarm_RamGuard_Rejects16GiBWithLowFreeRam");
    ExpectTrue(Kongor::IsMemoryEligibleForBackgroundWarmup(12ULL * kGiB, 4ULL * kGiB),
               "BgPrewarm_RamGuard_Accepts12GiBWith4GiBFree");

    // 2. BaseCmdTable O(1) lookup + compound ';' partitioning for prewarm_post_intro & prewarm_foreground_busy
    const wchar_t* cmdArgs = nullptr;
    const auto* matchBarePostIntro = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"prewarm_post_intro", &cmdArgs);
    ExpectTrue(matchBarePostIntro != nullptr &&
               matchBarePostIntro->Id == Kongor::KongorConsoleCmdId::PrewarmPostIntro &&
               cmdArgs != nullptr && wcscmp(cmdArgs, L"") == 0,
               "BgPrewarm_BaseCmd_MatchesBarePrewarmPostIntro");

    const auto* matchMapPostIntro = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"PREWARM_POST_INTRO caldavar", &cmdArgs);
    ExpectTrue(matchMapPostIntro != nullptr &&
               matchMapPostIntro->Id == Kongor::KongorConsoleCmdId::PrewarmPostIntro &&
               cmdArgs != nullptr && wcscmp(cmdArgs, L"caldavar") == 0,
               "BgPrewarm_BaseCmd_MatchesCaseInsensitivePrewarmPostIntroWithMap");

    ExpectTrue(Kongor::GetGlobalBaseCmdTable().MatchCommandLine(L"prewarm_foreground_busy", &cmdArgs) == nullptr,
               "BgPrewarm_BaseCmd_RejectsBarePrewarmForegroundBusyWithoutArg");

    const auto* matchBusy1 = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"prewarm_foreground_busy 1", &cmdArgs);
    ExpectTrue(matchBusy1 != nullptr &&
               matchBusy1->Id == Kongor::KongorConsoleCmdId::PrewarmForegroundBusy &&
               cmdArgs != nullptr && wcscmp(cmdArgs, L"1") == 0,
               "BgPrewarm_BaseCmd_MatchesPrewarmForegroundBusy1");

    std::vector<Kongor::DispatchedConsoleSegment> prewarmSegs;
    ExpectTrue(Kongor::PartitionCompoundBaseCmdBufferW(
                   L"prewarm_foreground_busy 1;PreloadWorld caldavar",
                   prewarmSegs) &&
               prewarmSegs.size() == 2 &&
               prewarmSegs[0].IsNativeBaseCmd &&
               prewarmSegs[0].CmdId == Kongor::KongorConsoleCmdId::PrewarmForegroundBusy &&
               prewarmSegs[0].CommandOrArgs == L"1" &&
               !prewarmSegs[1].IsNativeBaseCmd &&
               prewarmSegs[1].CommandOrArgs == L"PreloadWorld caldavar",
               "BgPrewarm_PartitionCompound_SplitsBusySignalBeforePreloadWorld");

    // 3. Real asset_prewarmer.cpp lifecycle: 0 startup jobs, foreground busy yield, player disable guard
    Kongor::InitializeAssetPrewarmer();
    Sleep(30);
    const auto initStats = Kongor::GetAssetPrewarmStats();
    ExpectTrue(initStats.FilesPrewarmed == 0 && initStats.BytesPrewarmed == 0 && !Kongor::IsPrewarmingActive(),
               "BgPrewarm_Initialize_EnqueuesZeroStartupDiskJobsBeforeIntro");

    // Create a 16 KiB temporary file to test PrewarmFile yield vs. active read
    wchar_t tempDir[MAX_PATH] = {0};
    GetTempPathW(MAX_PATH, tempDir);
    const std::wstring pidSuffix = std::to_wstring(::GetCurrentProcessId());
    std::filesystem::path scratchFile = std::filesystem::path(tempDir) / (L"kongor_bg_prewarm_unit_test_" + pidSuffix + L".bin");
    {
        std::ofstream ofs(scratchFile, std::ios::binary | std::ios::trunc);
        std::vector<char> payload(16384, 0x5A);
        ofs.write(payload.data(), static_cast<std::streamsize>(payload.size()));
    }

    Kongor::SetPrewarmerForegroundBusy(true);
    ExpectTrue(Kongor::IsPrewarmerForegroundBusy(), "BgPrewarm_SetForegroundBusyTrue_LatchesBusyFlag");
    ExpectTrue(!Kongor::PrewarmFile(scratchFile.wstring()),
               "BgPrewarm_PrewarmFile_ImmediatelyYieldsWhenForegroundBusy");
    ExpectTrue(Kongor::GetAssetPrewarmStats().FilesPrewarmed == 0,
               "BgPrewarm_ForegroundBusy_ZeroBytesRead");

    Kongor::SetPrewarmerForegroundBusy(false);
    ExpectTrue(!Kongor::IsPrewarmerForegroundBusy(), "BgPrewarm_SetForegroundBusyFalse_ClearsBusyFlag");
    ExpectTrue(Kongor::PrewarmFile(scratchFile.wstring()),
               "BgPrewarm_PrewarmFile_FaultsScratchFileWhenForegroundIdle");
    const auto afterStats = Kongor::GetAssetPrewarmStats();
    ExpectTrue(afterStats.FilesPrewarmed == 1 && afterStats.BytesPrewarmed == 16384,
               "BgPrewarm_PrewarmFile_RecordsExactFileAndByteCount");

    // When disabled by player option, TriggerPostIntroBackgroundWarmup returns false without latching session flag
    Kongor::SetAssetPrewarmerEnabled(false);
    ExpectTrue(!Kongor::IsAssetPrewarmerEnabled(), "BgPrewarm_DisableOption_PausesEngine");
    ExpectTrue(!Kongor::TriggerPostIntroBackgroundWarmup("caldavar"),
               "BgPrewarm_TriggerPostIntro_ReturnsFalseWhenDisabledByPlayer");
    Kongor::SetAssetPrewarmerEnabled(true);

    // 4. Phase 1.5 Hardening: Snapshot byte budget (initialAvail - 2 GiB) & laptop battery/saver predicate
    ExpectTrue(Kongor::ComputePostIntroWarmupBudgetBytes(5ULL * kGiB) == 3ULL * kGiB,
               "BgPrewarm_ByteBudget_Subtracts2GiBHeadroomFrom5GiBAvail");
    ExpectTrue(Kongor::ComputePostIntroWarmupBudgetBytes(2ULL * kGiB) == 0ULL,
               "BgPrewarm_ByteBudget_ClampsZeroAtExact2GiBHeadroom");
    ExpectTrue(Kongor::ComputePostIntroWarmupBudgetBytes(1ULL * kGiB) == 0ULL,
               "BgPrewarm_ByteBudget_NeverUnderflowsBelowHeadroom");
    ExpectTrue(Kongor::ShouldCapWarmupToTier1OnPower(0U, 0U),
               "BgPrewarm_PowerGuard_CapsToTier1OnDcBattery");
    ExpectTrue(Kongor::ShouldCapWarmupToTier1OnPower(1U, 1U),
               "BgPrewarm_PowerGuard_CapsToTier1OnBatterySaver");
    ExpectTrue(!Kongor::ShouldCapWarmupToTier1OnPower(1U, 0U) &&
               !Kongor::ShouldCapWarmupToTier1OnPower(255U, 0U),
               "BgPrewarm_PowerGuard_AllowsFullBudgetOnAcOrDesktop");

    // 5. Phase 1.5 BaseCmdTable: prewarm_cancel & prewarm_telemetry
    const auto* matchCancelBare = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"prewarm_cancel", &cmdArgs);
    ExpectTrue(matchCancelBare != nullptr &&
               matchCancelBare->Id == Kongor::KongorConsoleCmdId::PrewarmCancel &&
               cmdArgs != nullptr && wcscmp(cmdArgs, L"") == 0,
               "BgPrewarm_BaseCmd_MatchesBarePrewarmCancel");

    const auto* matchTelemetry = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(
        L"prewarm_telemetry COMPLETE 11840 caldavar", &cmdArgs);
    ExpectTrue(matchTelemetry != nullptr &&
               matchTelemetry->Id == Kongor::KongorConsoleCmdId::PrewarmTelemetry &&
               cmdArgs != nullptr && wcscmp(cmdArgs, L"COMPLETE 11840 caldavar") == 0,
               "BgPrewarm_BaseCmd_MatchesPrewarmTelemetryWithArgs");
    ExpectTrue(Kongor::GetGlobalBaseCmdTable().MatchCommandLine(L"prewarm_telemetry", &cmdArgs) == nullptr,
               "BgPrewarm_BaseCmd_RejectsBarePrewarmTelemetryWithoutArgs");

    // 6. Phase 1.5 Resumable Mid-File Pause/Resume & Exact Byte Budget Cap
    std::filesystem::path multiChunkFile = std::filesystem::path(tempDir) / (L"kongor_bg_prewarm_multichunk_test_" + pidSuffix + L".bin");
    constexpr size_t kMultiChunkBytes = 1024 * 1024; // 1 MiB = 4 x 256 KiB chunks
    {
        std::ofstream ofs(multiChunkFile, std::ios::binary | std::ios::trunc);
        std::vector<char> payload(kMultiChunkBytes, 0x3C);
        ofs.write(payload.data(), static_cast<std::streamsize>(payload.size()));
    }

    // 6a. Budget smaller than file (512 KiB < 1 MiB) stops mid-file without marking file complete
    uint64_t tightBudget = 512ULL * 1024ULL;
    ExpectTrue(!Kongor::PrewarmFilePostIntro(multiChunkFile.wstring(), &tightBudget),
               "BgPrewarm_PostIntroFile_StopsCleanlyAtExactByteBudget");
    ExpectTrue(tightBudget == 0ULL,
               "BgPrewarm_PostIntroFile_ConsumesExactRemainingBudgetBytes");

    // 6b. Foreground-busy pause mid-flight resumes to full 1 MiB completion once foreground busy clears
    Kongor::SetPrewarmerForegroundBusy(true);
    struct AsyncReadCtx {
        std::wstring Path;
        uint64_t     Budget;
        std::atomic<bool> Finished{ false };
        std::atomic<bool> Succeeded{ false };
    } readCtx{ multiChunkFile.wstring(), 2ULL * 1024ULL * 1024ULL };

    HANDLE hWorker = CreateThread(NULL, 0, [](LPVOID p) -> DWORD {
        auto* c = static_cast<AsyncReadCtx*>(p);
        const bool ok = Kongor::PrewarmFilePostIntro(c->Path, &c->Budget);
        c->Succeeded.store(ok, std::memory_order_release);
        c->Finished.store(true, std::memory_order_release);
        return 0;
    }, &readCtx, 0, NULL);

    Sleep(35);
    ExpectTrue(!readCtx.Finished.load(std::memory_order_acquire),
               "BgPrewarm_ResumableFile_HoldsWhileForegroundBusy");
    Kongor::SetPrewarmerForegroundBusy(false);
    if (hWorker) {
        WaitForSingleObject(hWorker, 2000);
        CloseHandle(hWorker);
    }
    ExpectTrue(readCtx.Finished.load(std::memory_order_acquire) &&
               readCtx.Succeeded.load(std::memory_order_acquire) &&
               readCtx.Budget == (2ULL * 1024ULL * 1024ULL - kMultiChunkBytes),
               "BgPrewarm_ResumableFile_CompletesFull1MiBAfterForegroundBusyClears");

    // Second call on already-completed file skips disk I/O in O(1) with 0 budget consumed
    uint64_t repeatBudget = 2ULL * 1024ULL * 1024ULL;
    ExpectTrue(Kongor::PrewarmFilePostIntro(multiChunkFile.wstring(), &repeatBudget) &&
               repeatBudget == 2ULL * 1024ULL * 1024ULL,
               "BgPrewarm_ResumableFile_SkipsAlreadyCompletedFileWithZeroDiskReads");

    // 7. Phase 1.5 Match-Active Guard: cancels PostIntro permanently while keeping direct PrewarmFile (hero pick) alive
    ExpectTrue(!Kongor::IsPostIntroWarmupCancelled(), "BgPrewarm_MatchGuard_InitiallyNotCancelled");
    Kongor::SetPrewarmerMatchActive(true);
    ExpectTrue(Kongor::IsPrewarmerMatchActive() && Kongor::IsPostIntroWarmupCancelled(),
               "BgPrewarm_MatchGuard_CancelsPostIntroImmediatelyOnMatchActive");
    ExpectTrue(!Kongor::TriggerPostIntroBackgroundWarmup("caldavar"),
               "BgPrewarm_MatchGuard_BlocksPostIntroTriggerDuringMatch");
    // Direct PrewarmFile (used by EnqueueHeroForPrewarming during Hero Selection phase 2) remains operational!
    ExpectTrue(Kongor::PrewarmFile(scratchFile.wstring()),
               "BgPrewarm_MatchGuard_KeepsHeroSelectionPrewarmOperational");
    Kongor::SetPrewarmerMatchActive(false);

    // 8. Phase 1.5 Kernel ThreadIoPriority=Low (22 -> 1) while preserving ThreadPagePriority=Normal (24 -> 5)
    {
        struct IoPrioResult {
            bool Applied{ false };
            ULONG IoPrioAfter{ 999U };
            ULONG PagePrioAfter{ 999U };
        } prioRes{};
        HANDLE hIoThread = CreateThread(nullptr, 0, [](LPVOID param) -> DWORD {
            auto* out = static_cast<IoPrioResult*>(param);
            out->Applied = Kongor::ApplyLowDiskIoPriorityToCurrentThread();
            HMODULE hNtdll = GetModuleHandleW(L"ntdll.dll");
            using FnNtQueryInformationThread = LONG(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
            auto pfnQ = hNtdll
                ? reinterpret_cast<FnNtQueryInformationThread>(GetProcAddress(hNtdll, "NtQueryInformationThread"))
                : nullptr;
            if (pfnQ) {
                ULONG retLen = 0;
                pfnQ(GetCurrentThread(), 22U /*ThreadIoPriority*/, &out->IoPrioAfter, sizeof(ULONG), &retLen);
                pfnQ(GetCurrentThread(), 24U /*ThreadPagePriority*/, &out->PagePrioAfter, sizeof(ULONG), &retLen);
            }
            return 0;
        }, &prioRes, 0, nullptr);
        if (hIoThread) {
            WaitForSingleObject(hIoThread, 2000);
            CloseHandle(hIoThread);
        }
        ExpectTrue(prioRes.Applied && prioRes.IoPrioAfter == 1U,
                   "BgPrewarm_KernelIoPriority_SetsThreadIoPriorityLow");
        ExpectTrue(prioRes.PagePrioAfter == 5U,
                   "BgPrewarm_KernelIoPriority_PreservesNormalStandbyPagePriority");
    }

    // 9a. Phase 1.6 SSD Auto-Skip Guard: on SSD drives (zero mechanical seek penalty), archive pre-faulting
    //     skips automatically with WarmupState="SKIPPED_SSD" and 0 faulted bytes unless KONGOR_FORCE_SSD_PREWARM=1.
    ExpectTrue(Kongor::ShouldSkipArchiveWarmupOnSsd(true, false),
               "BgPrewarm_SsdGuard_SkipsOnSsdByDefault");
    ExpectTrue(!Kongor::ShouldSkipArchiveWarmupOnSsd(true, true),
               "BgPrewarm_SsdGuard_AllowsSsdWhenForced");
    ExpectTrue(!Kongor::ShouldSkipArchiveWarmupOnSsd(false, false),
               "BgPrewarm_SsdGuard_RunsFullWarmupOnHdd");

    std::filesystem::path customPrewarmLog = scratchFile.parent_path() / (L"kongor_phase16_prewarm_unit_" + pidSuffix + L".log");
    std::error_code preCleanEc;
    std::filesystem::remove(customPrewarmLog, preCleanEc);
    SetEnvironmentVariableW(L"KONGOR_NATIVE_GUARD_PREWARM_LOG", customPrewarmLog.wstring().c_str());
    const wchar_t* resolvedLogW = Kongor::KongorResolvePrewarmLogPathW(true);
    ExpectTrue(resolvedLogW != nullptr && customPrewarmLog.wstring() == std::wstring(resolvedLogW),
               "BgPrewarm_LogPath_HonorsExplicitOverrideAndRefreshes");

    Kongor::ShutdownAssetPrewarmer();
    Kongor::SetPrewarmerDriveTypeForTesting("SSD");
    Kongor::SetForceSsdPrewarmForTesting(0);
    Kongor::InitializeAssetPrewarmer();
    ExpectTrue(!Kongor::TriggerPostIntroBackgroundWarmup("caldavar"),
               "BgPrewarm_SsdGuard_TriggerReturnsFalseAndSkipsOnSsd");
    const auto snapSsd = Kongor::GetPostIntroTelemetrySnapshot();
    ExpectTrue(std::strcmp(snapSsd.WarmupState, "SKIPPED_SSD") == 0 &&
               std::strcmp(snapSsd.DriveType, "SSD") == 0 &&
               snapSsd.PostIntroFaultedBytes == 0ULL &&
               snapSsd.PostIntroFiles == 0U,
               "BgPrewarm_SsdGuard_ReportsSkippedSsdWithZeroBytesFaulted");
    Kongor::LogPlayNowPreloadTelemetry("COMPLETE", 11859ULL, "caldavar");

    // Verify [PRELOAD_TELEMETRY] and SKIPPED_SSD flushed deterministically to the resolved prewarm log file
    {
        std::ifstream logIn(customPrewarmLog, std::ios::binary);
        std::string logContents((std::istreambuf_iterator<char>(logIn)), std::istreambuf_iterator<char>());
        ExpectTrue(logContents.find("[PRELOAD_TELEMETRY]") != std::string::npos &&
                   logContents.find("warmupState=SKIPPED_SSD") != std::string::npos &&
                   logContents.find("driveType=SSD") != std::string::npos,
                   "BgPrewarm_LogFile_FlushesPreloadTelemetryAndSsdSkipState");
    }

    // 9b. HDD Foreground-busy frame exclusion: on HDD, while PostIntro is actively IN_PROGRESS, Main Menu frames (> 5 ms)
    //     are recorded, whereas foreground PreloadWorld stalls (450 ms) while paused must never pollute warmupPeakFrameMs.
    std::filesystem::path scratchHddRoot = scratchFile.parent_path() / (L"kongor_phase16_hdd_root_" + pidSuffix);
    std::filesystem::path scratchMapsDir = scratchHddRoot / "game" / "maps";
    std::error_code dirEc;
    std::filesystem::create_directories(scratchMapsDir, dirEc);
    std::filesystem::path scratchMapArchive = scratchMapsDir / "caldavar.s2z";
    {
        HANDLE hSparse = CreateFileW(
            scratchMapArchive.wstring().c_str(),
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            NULL,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
        if (hSparse != INVALID_HANDLE_VALUE) {
            DWORD tmpBytes = 0;
            (void)DeviceIoControl(hSparse, FSCTL_SET_SPARSE, NULL, 0, NULL, 0, &tmpBytes, NULL);
            LARGE_INTEGER targetSize{};
            targetSize.QuadPart = 128LL * 1024LL * 1024LL; // 128 MiB -> 16 gentle 8 MiB chunks (~250 ms IN_PROGRESS window)
            SetFilePointerEx(hSparse, targetSize, NULL, FILE_BEGIN);
            SetEndOfFile(hSparse);
            CloseHandle(hSparse);
        }
    }

    Kongor::ShutdownAssetPrewarmer();
    Kongor::SetPrewarmerGameRootForTesting(scratchHddRoot.wstring().c_str());
    Kongor::SetPrewarmerDriveTypeForTesting("HDD");
    Kongor::SetForceSsdPrewarmForTesting(0);
    Kongor::InitializeAssetPrewarmer();
    Kongor::SetPrewarmerForegroundBusy(false);
    if (Kongor::TriggerPostIntroBackgroundWarmup("caldavar")) {
        for (int spin = 0; spin < 60; ++spin) {
            if (std::strcmp(Kongor::GetPostIntroTelemetrySnapshot().WarmupState, "IN_PROGRESS") == 0) {
                break;
            }
            Sleep(5);
        }
        ExpectTrue(std::strcmp(Kongor::GetPostIntroTelemetrySnapshot().WarmupState, "IN_PROGRESS") == 0,
                   "BgPrewarm_Telemetry_EntersInProgressStateWhenTriggered");
        Kongor::NotifyPrewarmerFrameTimeUs(6500U); // 6.5 ms Main Menu frame while actively warming
        const auto snapActive = Kongor::GetPostIntroTelemetrySnapshot();
        ExpectTrue(snapActive.HitchCount == 1ULL && snapActive.PeakFrameMs >= 6.4f && snapActive.PeakFrameMs <= 6.6f,
                   "BgPrewarm_Telemetry_RecordsMainMenuFrameWhileActivelyWarming");

        Kongor::SetPrewarmerForegroundBusy(true);
        Kongor::NotifyPrewarmerFrameTimeUs(450000U); // 450 ms foreground PreloadWorld frame while paused
        const auto snapPaused = Kongor::GetPostIntroTelemetrySnapshot();
        ExpectTrue(snapPaused.HitchCount == 1ULL && snapPaused.PeakFrameMs < 7.0f,
                   "BgPrewarm_Telemetry_ExcludesForegroundPreloadFramesWhilePaused");
        Kongor::CancelPostIntroWarmup("unit_test_cleanup");
        Kongor::SetPrewarmerForegroundBusy(false);
    }

    Kongor::ShutdownAssetPrewarmer();
    Kongor::SetPrewarmerGameRootForTesting(nullptr);
    Kongor::SetPrewarmerDriveTypeForTesting(nullptr);
    Kongor::SetForceSsdPrewarmForTesting(-1);
    SetEnvironmentVariableW(L"KONGOR_NATIVE_GUARD_PREWARM_LOG", NULL);
    (void)Kongor::KongorResolvePrewarmLogPathW(true);
    std::error_code removeEc;
    std::filesystem::remove_all(scratchHddRoot, removeEc);
    std::filesystem::remove(customPrewarmLog, removeEc);
    std::filesystem::remove(scratchFile, removeEc);
    std::filesystem::remove(multiChunkFile, removeEc);

    // Phase 1.7: 3.5 GiB free-RAM guard for queue-concurrent PreloadWorld (16 GB PCs with 4.5 GiB free stay enabled)
    constexpr uint64_t kMiB = 1024ULL * 1024ULL;
    ExpectTrue(!Kongor::ShouldDisableBootWorldPreload(4608ULL * kMiB, false, false),
               "BootPreloadGuard_Allows16GbPcWith4_5GiBFreeOnForegroundLaunch");
    ExpectTrue(!Kongor::ShouldDisableBootWorldPreload(3584ULL * kMiB, false, false),
               "BootPreloadGuard_AllowsExact3_5GiBBoundary");
    ExpectTrue(Kongor::ShouldDisableBootWorldPreload(3583ULL * kMiB, false, false),
               "BootPreloadGuard_DisablesBelow3_5GiBFreeRam");
    ExpectTrue(Kongor::ShouldDisableBootWorldPreload(12288ULL * kMiB, true, false),
               "BootPreloadGuard_DisablesWhenEnvOverrideSet");
    ExpectTrue(Kongor::ShouldDisableBootWorldPreload(12288ULL * kMiB, false, true),
               "BootPreloadGuard_DisablesOnHeadlessTestLaunch");
}

static DWORD WINAPI ParkBackgroundThreadForDumpTest(LPVOID param) {
    HANDLE hStopEvt = reinterpret_cast<HANDLE>(param);
    volatile char secretStackLocal[64] = "BackgroundThreadStackCanary_DoNotCapture";
    (void)secretStackLocal[0];
    if (hStopEvt != NULL) {
        WaitForSingleObject(hStopEvt, 5000);
    }
    return 0;
}

void TestTier1PrivacyMiniDumpCallbackAndSelfCapture() {
    // 1. Safe Engine / System32 allowlist vs 3rd-party user DLL classification
    ExpectTrue(Kongor_IsSafeEngineOrSystemModuleW(L"C:\\Users\\JaneDoe\\Games\\HoN\\cgame_x64.dll", L"cgame_x64.dll"), // kongor-security-ignore: synthetic PII redaction test fixture
               "Tier1Privacy_RecognizesSafeEngineDll");
    ExpectTrue(Kongor_IsSafeEngineOrSystemModuleW(L"C:\\Windows\\System32\\custom_os_drv.dll", L"custom_os_drv.dll"),
               "Tier1Privacy_RecognizesSystem32Directory");
    ExpectTrue(!Kongor_IsSafeEngineOrSystemModuleW(L"C:\\Program Files\\StreamRecorder\\1.36\\hook_x64.dll", L"hook_x64.dll"),
               "Tier1Privacy_FlagsNonSystemThirdPartyDll");

    // 2. ModuleCallback strips full user path in-place & clears CvRecord on non-faulting 3rd-party modules
    KongorTier1DumpCallbackCtx cbCtx{};
    cbCtx.TargetStackThreadId = 4242U;
    cbCtx.FaultAddress = 0x00007FF850001234ULL;

    wchar_t enginePathBuf[MAX_PATH] = L"C:\\Users\\JaneDoe_Profile\\HoN\\cgame_x64.dll"; // kongor-security-ignore: synthetic PII redaction test fixture
    MINIDUMP_CALLBACK_INPUT modIn{};
    modIn.CallbackType = ModuleCallback;
    modIn.Module.FullPath = enginePathBuf;
    modIn.Module.BaseOfImage = 0x00007FF840000000ULL;
    modIn.Module.SizeOfImage = 0x00600000UL;
    MINIDUMP_CALLBACK_OUTPUT modOut{};
    modOut.ModuleWriteFlags = ModuleWriteModule | ModuleWriteCvRecord | ModuleWriteMiscRecord | ModuleWriteDataSeg | ModuleWriteTlsData;

    ExpectTrue(Kongor_Tier1PrivacyMiniDumpCallback(&cbCtx, &modIn, &modOut) == TRUE,
               "Tier1Privacy_ModuleCallback_ReturnsTrueForEngineModule");
    ExpectTrue(wcscmp(enginePathBuf, L"cgame_x64.dll") == 0,
               "Tier1Privacy_ModuleCallback_StripsUserDirectoryToBasenameInPlace");
    ExpectTrue((modOut.ModuleWriteFlags & ModuleWriteCvRecord) != 0 &&
               (modOut.ModuleWriteFlags & (ModuleWriteMiscRecord | ModuleWriteDataSeg | ModuleWriteTlsData)) == 0,
               "Tier1Privacy_ModuleCallback_PreservesEngineCvRecordAndClearsDataTls");

    wchar_t thirdPartyPathBuf[MAX_PATH] = L"D:\\PersonalApps\\StreamRecorder\\hook_x64.dll";
    modIn.Module.FullPath = thirdPartyPathBuf;
    modIn.Module.BaseOfImage = 0x00007FF710000000ULL;
    modIn.Module.SizeOfImage = 0x00080000UL;
    modOut.ModuleWriteFlags = ModuleWriteModule | ModuleWriteCvRecord | ModuleWriteMiscRecord | ModuleWriteDataSeg;

    ExpectTrue(Kongor_Tier1PrivacyMiniDumpCallback(&cbCtx, &modIn, &modOut) == TRUE,
               "Tier1Privacy_ModuleCallback_ReturnsTrueForThirdPartyModule");
    ExpectTrue(wcscmp(thirdPartyPathBuf, L"hook_x64.dll") == 0 &&
               (modOut.ModuleWriteFlags & ModuleWriteCvRecord) == 0 &&
               cbCtx.ThirdPartyCvCleared == 1U,
               "Tier1Privacy_ModuleCallback_ClearsThirdPartyCvRecordPdbLeak");

    // 3. ThreadCallback retains crashing/target thread stack & suppresses background thread stacks
    MINIDUMP_CALLBACK_INPUT threadIn{};
    threadIn.CallbackType = ThreadCallback;
    threadIn.Thread.ThreadId = 4242U;
    MINIDUMP_CALLBACK_OUTPUT threadOut{};
    threadOut.ThreadWriteFlags = ThreadWriteThread | ThreadWriteStack | ThreadWriteContext | ThreadWriteInstructionWindow;
    Kongor_Tier1PrivacyMiniDumpCallback(&cbCtx, &threadIn, &threadOut);
    ExpectTrue((threadOut.ThreadWriteFlags & ThreadWriteStack) != 0,
               "Tier1Privacy_ThreadCallback_PreservesTargetThreadStack");

    threadIn.Thread.ThreadId = 9999U;
    threadOut.ThreadWriteFlags = ThreadWriteThread | ThreadWriteStack | ThreadWriteContext | ThreadWriteInstructionWindow | ThreadWriteThreadData;
    BOOL bgThreadRet = Kongor_Tier1PrivacyMiniDumpCallback(&cbCtx, &threadIn, &threadOut);
    ExpectTrue(bgThreadRet == FALSE &&
               (threadOut.ThreadWriteFlags & ThreadWriteStack) == 0 &&
               cbCtx.BackgroundStacksSuppressed == 1U,
               "Tier1Privacy_ThreadCallback_SuppressesBackgroundThreadStackAndReturnsFalse");

    MINIDUMP_CALLBACK_INPUT incThreadIn{};
    incThreadIn.CallbackType = IncludeThreadCallback;
    incThreadIn.IncludeThread.ThreadId = 4242U;
    MINIDUMP_CALLBACK_OUTPUT incThreadOut{};
    ExpectTrue(Kongor_Tier1PrivacyMiniDumpCallback(&cbCtx, &incThreadIn, &incThreadOut) == TRUE,
               "Tier1Privacy_IncludeThreadCallback_IncludesTargetCrashingThread");
    incThreadIn.IncludeThread.ThreadId = 8888U;
    ExpectTrue(Kongor_Tier1PrivacyMiniDumpCallback(&cbCtx, &incThreadIn, &incThreadOut) == FALSE,
               "Tier1Privacy_IncludeThreadCallback_ExcludesBackgroundWorkerThread");

    // 4. Live MiniDumpWriteDump self-capture with active background worker thread:
    //    Verifies 0 drive/slash paths in Stream 4 (ModuleList) and <= 1 stack in Stream 3 (ThreadList).
    HANDLE hStopBg = CreateEventW(NULL, TRUE, FALSE, NULL);
    DWORD bgTid = 0;
    HANDLE hBgThread = CreateThread(NULL, 0, ParkBackgroundThreadForDumpTest, hStopBg, 0, &bgTid);
    Sleep(15);

    wchar_t tempDir[MAX_PATH] = {0};
    GetTempPathW(MAX_PATH, tempDir);
    std::filesystem::path tempDmp = std::filesystem::path(tempDir) /
        (L"kongor_tier1_privacy_test_" + std::to_wstring(GetCurrentProcessId()) + L".dmp");

    KongorTier1DumpCallbackCtx liveCtx{};
    liveCtx.TargetStackThreadId = GetCurrentThreadId();
    liveCtx.FaultAddress = reinterpret_cast<uintptr_t>(&TestTier1PrivacyMiniDumpCallbackAndSelfCapture);

    BOOL wroteLive = FALSE;
    HANDLE hFile = CreateFileW(tempDmp.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        MINIDUMP_CALLBACK_INFORMATION cbInfo{};
        cbInfo.CallbackRoutine = Kongor_Tier1PrivacyMiniDumpCallback;
        cbInfo.CallbackParam = &liveCtx;
        MINIDUMP_TYPE flags = static_cast<MINIDUMP_TYPE>(
            MiniDumpNormal | MiniDumpFilterModulePaths | MiniDumpWithoutAuxiliaryState | MiniDumpWithThreadInfo);
        wroteLive = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hFile, flags, NULL, NULL, &cbInfo);
        FlushFileBuffers(hFile);
        CloseHandle(hFile);
    }

    if (hStopBg != NULL) SetEvent(hStopBg);
    if (hBgThread != NULL) {
        WaitForSingleObject(hBgThread, 2000);
        CloseHandle(hBgThread);
    }
    if (hStopBg != NULL) CloseHandle(hStopBg);

    ExpectTrue(wroteLive == TRUE, "Tier1Privacy_LiveMiniDumpWriteDump_Succeeds");
    if (wroteLive == TRUE) {
        std::ifstream ifs(tempDmp, std::ios::binary | std::ios::ate);
        const auto sz = ifs.tellg();
        ifs.seekg(0, std::ios::beg);
        std::vector<uint8_t> dmp(static_cast<size_t>(sz));
        ifs.read(reinterpret_cast<char*>(dmp.data()), sz);
        ifs.close();

        uint32_t sig = 0, numStreams = 0, dirRva = 0;
        if (dmp.size() >= 32) {
            memcpy(&sig, dmp.data(), 4);
            memcpy(&numStreams, dmp.data() + 8, 4);
            memcpy(&dirRva, dmp.data() + 12, 4);
        }
        ExpectTrue(sig == 0x504D444DU && numStreams > 0, "Tier1Privacy_LiveSelfCapture_HasValidMdmpHeader");

        bool anyModuleHasPathSep = false;
        bool foundBgThreadEntry = false;
        uint32_t bgThreadStackRva = 0xFFFFFFFFU;
        uint32_t nonZeroStackRvaCount = 0;
        uint32_t memoryListRangeCount = 0xFFFFFFFFU;
        uint32_t totalThreadsInDump = 0;

        for (uint32_t s = 0; s < numStreams && (dirRva + (s + 1U) * 12U) <= dmp.size(); ++s) {
            uint32_t stType = 0, stSize = 0, stRva = 0;
            memcpy(&stType, dmp.data() + dirRva + s * 12U, 4);
            memcpy(&stSize, dmp.data() + dirRva + s * 12U + 4U, 4);
            memcpy(&stRva,  dmp.data() + dirRva + s * 12U + 8U, 4);

            if (stType == ModuleListStream && stRva + 4U <= dmp.size()) {
                uint32_t modCount = 0;
                memcpy(&modCount, dmp.data() + stRva, 4);
                for (uint32_t m = 0; m < modCount && (stRva + 4U + (m + 1U) * 108U) <= dmp.size(); ++m) {
                    uint32_t nameRva = 0;
                    memcpy(&nameRva, dmp.data() + stRva + 4U + m * 108U + 20U, 4);
                    if (nameRva + 4U <= dmp.size()) {
                        uint32_t byteLen = 0;
                        memcpy(&byteLen, dmp.data() + nameRva, 4);
                        if (nameRva + 4U + byteLen <= dmp.size()) {
                            const wchar_t* wch = reinterpret_cast<const wchar_t*>(dmp.data() + nameRva + 4U);
                            const size_t wlen = byteLen / sizeof(wchar_t);
                            for (size_t c = 0; c < wlen; ++c) {
                                if (wch[c] == L'\\' || wch[c] == L'/' || wch[c] == L':') {
                                    anyModuleHasPathSep = true;
                                }
                            }
                        }
                    }
                }
            } else if (stType == ThreadListStream && stRva + 4U <= dmp.size()) {
                memcpy(&totalThreadsInDump, dmp.data() + stRva, 4);
                for (uint32_t t = 0; t < totalThreadsInDump && (stRva + 4U + (t + 1U) * 48U) <= dmp.size(); ++t) {
                    uint32_t entryTid = 0;
                    uint32_t stackDataRva = 0;
                    memcpy(&entryTid, dmp.data() + stRva + 4U + t * 48U, 4);
                    memcpy(&stackDataRva, dmp.data() + stRva + 4U + t * 48U + 36U, 4);
                    if (entryTid == bgTid) {
                        foundBgThreadEntry = true;
                        bgThreadStackRva = stackDataRva;
                    }
                    if (stackDataRva != 0U) {
                        ++nonZeroStackRvaCount;
                    }
                }
            } else if (stType == MemoryListStream && stRva + 4U <= dmp.size()) {
                memcpy(&memoryListRangeCount, dmp.data() + stRva, 4);
            }
        }

        const char* kBgCanary = "BackgroundThreadStackCanary_DoNotCapture";
        const size_t kBgCanaryLen = strlen(kBgCanary);
        bool leakedBgStackCanary = false;
        for (size_t i = 0; i + kBgCanaryLen <= dmp.size(); ++i) {
            if (memcmp(dmp.data() + i, kBgCanary, kBgCanaryLen) == 0) {
                leakedBgStackCanary = true;
                break;
            }
        }

        char diagDetail[192] = {0};
        snprintf(diagDetail, sizeof(diagDetail),
            "(threads=%u, nonZeroStackRvas=%u, memRanges=%u, foundBg=%d, bgStackRva=%u, bgSuppressed=%u, leakedCanary=%d)",
            totalThreadsInDump, nonZeroStackRvaCount, memoryListRangeCount, foundBgThreadEntry ? 1 : 0,
            bgThreadStackRva, liveCtx.BackgroundStacksSuppressed, leakedBgStackCanary ? 1 : 0);

        ExpectTrue(!anyModuleHasPathSep, "Tier1Privacy_LiveSelfCapture_ZeroDriveOrDirectoryPathsInStream4");
        ExpectTrue(totalThreadsInDump == 1U && !foundBgThreadEntry && nonZeroStackRvaCount <= 1U &&
                   liveCtx.BackgroundStacksSuppressed >= 1U && !leakedBgStackCanary,
                   "Tier1Privacy_LiveSelfCapture_ExcludesAllBackgroundThreadsAtCaptureTime",
                   diagDetail);
    }

    std::error_code rmEc;
    std::filesystem::remove(tempDmp, rmEc);
}

void TestPipeSecurityPlaytestLockdown() {
    // 1. Dev build (isPlaytestBuild == false) allows all methods and commands
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("auth", false), "PipeSecurity_Dev_Allows_auth");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("status", false), "PipeSecurity_Dev_Allows_status");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("safe_mode", false), "PipeSecurity_Dev_Allows_safe_mode");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("get_server_world_state", false), "PipeSecurity_Dev_Allows_get_server_world_state");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("execute", false), "PipeSecurity_Dev_Allows_execute");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("hook_toggle", false), "PipeSecurity_Dev_Allows_hook_toggle");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("apply_server_fixture", false), "PipeSecurity_Dev_Allows_apply_server_fixture");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("trigger_match_victory", false), "PipeSecurity_Dev_Allows_trigger_match_victory");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("PEEK_STRUCT", false), "PipeSecurity_Dev_Allows_PEEK_STRUCT");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("Script Set(\"cl_fov\", 90)", false), "PipeSecurity_Dev_Allows_RawConsole");

    // 2. Playtest build (isPlaytestBuild == true) permits passive read-only methods
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("auth", true), "PipeSecurity_Playtest_Allows_auth");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("AUTH token123", true), "PipeSecurity_Playtest_Allows_AUTH_prefix");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("status", true), "PipeSecurity_Playtest_Allows_status");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("safe_mode", true), "PipeSecurity_Playtest_Allows_safe_mode");
    ExpectTrue(Kongor::Kongor_IsPipeMethodAllowedInBuild("get_server_world_state", true), "PipeSecurity_Playtest_Allows_get_server_world_state");

    // 3. Playtest build (isPlaytestBuild == true) strictly BLOCKS mutating / eval / memory methods
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("execute", true), "PipeSecurity_Playtest_Blocks_execute");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("hook_toggle", true), "PipeSecurity_Playtest_Blocks_hook_toggle");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("apply_server_fixture", true), "PipeSecurity_Playtest_Blocks_apply_server_fixture");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("trigger_match_victory", true), "PipeSecurity_Playtest_Blocks_trigger_match_victory");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("PEEK_STRUCT", true), "PipeSecurity_Playtest_Blocks_PEEK_STRUCT");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("PEEK_STRUCT CGameEntity 0x1234", true), "PipeSecurity_Playtest_Blocks_PEEK_STRUCT_Line");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("Script Set(\"cl_fov\", 90)", true), "PipeSecurity_Playtest_Blocks_RawScript");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("ServerEndGame 1", true), "PipeSecurity_Playtest_Blocks_RawServerCommand");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild(nullptr, true), "PipeSecurity_Playtest_Blocks_Null");
    ExpectTrue(!Kongor::Kongor_IsPipeMethodAllowedInBuild("", true), "PipeSecurity_Playtest_Blocks_Empty");

    // 4. Binary 0x574D frame opcode lockdown parity (prevents bypassing JSON-RPC lockdown via raw binary frames)
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0003, false), "PipeSecurity_Dev_Allows_Bin_ExecuteCommand");
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x000E, false), "PipeSecurity_Dev_Allows_Bin_EvalLuaSync");
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0062, false), "PipeSecurity_Dev_Allows_Bin_PeekStruct");
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0001, true), "PipeSecurity_Playtest_Allows_Bin_Ping");
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0005, true), "PipeSecurity_Playtest_Allows_Bin_GetEngineState");
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0010, true), "PipeSecurity_Playtest_Allows_Bin_Authenticate");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0003, true), "PipeSecurity_Playtest_Blocks_Bin_ExecuteCommand");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0009, true), "PipeSecurity_Playtest_Blocks_Bin_DispatchUiEvent");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x000E, true), "PipeSecurity_Playtest_Blocks_Bin_EvalLuaSync");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0012, true), "PipeSecurity_Playtest_Blocks_Bin_InjectSubTickInput");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0060, true), "PipeSecurity_Playtest_Blocks_Bin_HookToggle");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0062, true), "PipeSecurity_Playtest_Blocks_Bin_PeekStruct");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0070, true), "PipeSecurity_Playtest_Blocks_Bin_TriggerMatchVictory");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0072, true), "PipeSecurity_Playtest_Blocks_Bin_ApplyServerFixture");
    ExpectTrue(Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0080, false), "PipeSecurity_Dev_Allows_Bin_DuplexLuaRpc");
    ExpectTrue(!Kongor::Kongor_IsBinaryPipeOpCodeAllowedInBuild(0x0080, true), "PipeSecurity_Playtest_Blocks_Bin_DuplexLuaRpc");
}

// ----------------------------------------------------------------------------
// 19. Test Binary Duplex Lua RPC 0x0080 / 0x0081 Framing & Clamping
// ----------------------------------------------------------------------------
void TestDuplexLuaRpcFraming() {
    // 1. Verify Request framing: [timeoutMs (4B)][reqId (4B)][utf8 script]
    uint32_t reqTimeout = 1500;
    uint32_t reqId = 1337;
    const char* script = "return 1 + 1";
    size_t scriptLen = strlen(script);

    std::vector<uint8_t> reqPayload(8 + scriptLen);
    memcpy(&reqPayload[0], &reqTimeout, 4);
    memcpy(&reqPayload[4], &reqId, 4);
    memcpy(&reqPayload[8], script, scriptLen);

    uint32_t parsedTimeout = 0;
    uint32_t parsedReqId = 0;
    memcpy(&parsedTimeout, &reqPayload[0], 4);
    memcpy(&parsedReqId, &reqPayload[4], 4);
    std::string parsedScript(reinterpret_cast<char*>(&reqPayload[8]), scriptLen);

    ExpectTrue(parsedTimeout == 1500, "DuplexRpc_Request_TimeoutParsed");
    ExpectTrue(parsedReqId == 1337, "DuplexRpc_Request_ReqIdParsed");
    ExpectTrue(parsedScript == "return 1 + 1", "DuplexRpc_Request_ScriptParsed");

    // 2. Timeout clamping invariants: [10..10000]
    auto clampTimeout = [](uint32_t to) -> uint32_t {
        return (to < 10) ? 10 : (to > 10000) ? 10000 : to;
    };
    ExpectTrue(clampTimeout(0) == 10, "DuplexRpc_Clamp_MinZero");
    ExpectTrue(clampTimeout(5) == 10, "DuplexRpc_Clamp_MinFive");
    ExpectTrue(clampTimeout(2500) == 2500, "DuplexRpc_Clamp_MidRange");
    ExpectTrue(clampTimeout(99999) == 10000, "DuplexRpc_Clamp_MaxClamped");

    // 3. Response framing: [requestId (4B)][elapsedUs (4B)][statusFlags (1B)][utf8 result]
    uint32_t respReqId = 1337;
    uint32_t elapsedUs = 420;
    uint8_t statusFlags = 0x01; // bit0 = ok
    const char* resultStr = "2";
    size_t resLen = strlen(resultStr);

    std::vector<uint8_t> respPayload(9 + resLen);
    memcpy(&respPayload[0], &respReqId, 4);
    memcpy(&respPayload[4], &elapsedUs, 4);
    respPayload[8] = statusFlags;
    memcpy(&respPayload[9], resultStr, resLen);

    uint32_t unpackedReqId = 0;
    uint32_t unpackedElapsed = 0;
    memcpy(&unpackedReqId, &respPayload[0], 4);
    memcpy(&unpackedElapsed, &respPayload[4], 4);
    uint8_t unpackedFlags = respPayload[8];
    std::string unpackedResult(reinterpret_cast<char*>(&respPayload[9]), resLen);

    ExpectTrue(unpackedReqId == 1337, "DuplexRpc_Response_ReqIdUnpacked");
    ExpectTrue(unpackedElapsed == 420, "DuplexRpc_Response_ElapsedUnpacked");
    ExpectTrue((unpackedFlags & 0x01) != 0, "DuplexRpc_Response_FlagOk");
    ExpectTrue((unpackedFlags & 0x02) == 0, "DuplexRpc_Response_FlagNotTimedOut");
    ExpectTrue(unpackedResult == "2", "DuplexRpc_Response_ResultMatch");

    // 4. Timeout status flag verification
    uint8_t timeoutFlag = 0x02; // bit1 = timedOut
    ExpectTrue((timeoutFlag & 0x01) == 0, "DuplexRpc_TimeoutFlag_NotOk");
    ExpectTrue((timeoutFlag & 0x02) != 0, "DuplexRpc_TimeoutFlag_IsTimedOut");
}

void TestServerKeyframeShmTelemetryBinary() {
    ClearKeyframePlayerCoordinates();

    SetKeyframePlayerCoordinate(
        0, 1, "Hero_Devourer", "Player1",
        1500.5f, 2500.25f, 120.0f,
        950.0f, 1000.0f, 400.0f, 500.0f,
        5, 90.0f, true);

    SetKeyframePlayerCoordinate(
        5, 2, "Hero_Pyromancer", "Player6",
        -1500.0f, -2500.0f, 100.0f,
        800.0f, 850.0f, 600.0f, 650.0f,
        6, 270.0f, true);

    int testPort = 100000 + static_cast<int>(::GetCurrentProcessId());
    WriteServerKeyframe(testPort);

    std::string mapName = "Local\\KongorServerKeyframe_" + std::to_string(testPort);
    HANDLE hMap = OpenFileMappingA(FILE_MAP_READ, FALSE, mapName.c_str());
    uint8_t buffer[65536] = {};
    size_t bytesRead = 0;

    if (hMap != NULL) {
        LPVOID pBuf = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 65536);
        if (pBuf) {
            memcpy(buffer, pBuf, 65536);
            bytesRead = 65536;
            UnmapViewOfFile(pBuf);
        }
        CloseHandle(hMap);
    } else {
        char tempPath[MAX_PATH];
        if (GetTempPathA(MAX_PATH, tempPath) > 0) {
            std::string filePath = std::string(tempPath) + "KongorServerKeyframe_" + std::to_string(testPort) + ".bin";
            FILE* f = fopen(filePath.c_str(), "rb");
            if (f) {
                bytesRead = fread(buffer, 1, sizeof(buffer), f);
                fclose(f);
            }
        }
    }

    ExpectTrue(bytesRead >= 40, "KeyframeShm_BytesReadAtLeastHeader");
    uint32_t magic = 0;
    memcpy(&magic, buffer, 4);
    ExpectTrue(magic == 0x4B474B46, "KeyframeShm_MagicMatch_KGKF");

    uint32_t payloadSize = 0;
    memcpy(&payloadSize, buffer + 32, 4);
    ExpectTrue(payloadSize > 0 && payloadSize <= 65536 - 40, "KeyframeShm_PayloadSizeNonZero");

    uint32_t headerCrc = 0;
    memcpy(&headerCrc, buffer + 36, 4);

    uint32_t computedCrc = ComputeCrc32Native(buffer + 40, payloadSize);
    ExpectTrue(computedCrc == headerCrc, "KeyframeShm_Crc32Verified");

    std::string payloadJson(reinterpret_cast<const char*>(buffer + 40), payloadSize);
    ExpectTrue(payloadJson.find("\"players\":[") != std::string::npos, "KeyframeShm_PayloadHasPlayersKey");
    ExpectTrue(payloadJson.find("\"entities\":") == std::string::npos, "KeyframeShm_ZeroEntitiesDuplication");
    ExpectTrue(payloadJson.find("Hero_Devourer") != std::string::npos, "KeyframeShm_HeroDevourerPresent");
    ExpectTrue(payloadJson.find("Hero_Pyromancer") != std::string::npos, "KeyframeShm_HeroPyromancerPresent");
    ExpectTrue(payloadJson.find("Player1") != std::string::npos, "KeyframeShm_Player1Present");
    ExpectTrue(payloadJson.find("Player6") != std::string::npos, "KeyframeShm_Player6Present");

    ClearKeyframePlayerCoordinates();
    CleanupServerKeyframeMap();

    // Also test RefreshServerKeyframePlayersFromEngine end-to-end against synthetic K2 IGame + CPlayer (+0x2D8 net string) + IUnitEntity (+0x360/+0x368/+0x364/+0x36C)
    alignas(16) uint8_t fakeGame[0x240] = {};
    alignas(16) uint8_t fakeHead[0x40] = {};
    alignas(16) uint8_t fakeNode[0x40] = {};
    alignas(16) uint8_t fakePlayer[0x400] = {};
    alignas(16) uint8_t fakeEntityDir[0x20] = {};
    alignas(16) uint8_t fakeGameEntity[0x20] = {};
    alignas(16) uint8_t fakeUnit[0x400] = {};

    struct FakeK2WString {
        union {
            wchar_t inlineBuf[8];
            const wchar_t* ptr;
        } u;
        uint64_t len;
        uint64_t cap;
    };
    static FakeK2WString s_fakeNetPlayerName = {};
    s_fakeNetPlayerName.u.ptr = L"DisasmVerifiedHeroOwner";
    s_fakeNetPlayerName.len = wcslen(s_fakeNetPlayerName.u.ptr);
    s_fakeNetPlayerName.cap = 32;

    FakeK2WString* pHeroTypeStr = reinterpret_cast<FakeK2WString*>(fakeUnit + 0x30);
    pHeroTypeStr->u.ptr = L"Hero_Chronos";
    pHeroTypeStr->len = wcslen(pHeroTypeStr->u.ptr);
    pHeroTypeStr->cap = 32;

    *reinterpret_cast<uint8_t*>(fakeUnit + 0xD0) = 1; // ENTITY_STATUS_ACTIVE (alive)
    *reinterpret_cast<float*>(fakeUnit + 0xD8) = 7420.5f;
    *reinterpret_cast<float*>(fakeUnit + 0xDC) = 7890.25f;
    *reinterpret_cast<float*>(fakeUnit + 0xE0) = 128.0f;
    *reinterpret_cast<float*>(fakeUnit + 0xF8) = 180.0f;
    *reinterpret_cast<uint32_t*>(fakeUnit + 0x35C) = 16;
    *reinterpret_cast<float*>(fakeUnit + 0x360) = 1420.0f;
    *reinterpret_cast<float*>(fakeUnit + 0x368) = 1850.0f;
    *reinterpret_cast<float*>(fakeUnit + 0x364) = 610.0f;
    *reinterpret_cast<float*>(fakeUnit + 0x36C) = 900.0f;

    static void* s_fakeGameEntityPtr = fakeGameEntity;
    static void* s_fakeUnitPtr = fakeUnit;

    struct FakeDirHooks {
        static void* __fastcall GetEntityFromIndex(void* /*pDir*/, uint32_t idx) {
            return (idx == 42u) ? s_fakeGameEntityPtr : nullptr;
        }
        static void* __fastcall GetAsUnit(void* /*pEnt*/) {
            return s_fakeUnitPtr;
        }
        static const void* __fastcall GetNetString(void* /*pNetMgr*/, uint16_t idx) {
            return (idx == 7u) ? &s_fakeNetPlayerName : nullptr;
        }
    };

    static uintptr_t fakeDirVtbl[4] = {};
    fakeDirVtbl[2] = reinterpret_cast<uintptr_t>(&FakeDirHooks::GetEntityFromIndex); // +0x10
    *reinterpret_cast<uintptr_t*>(fakeEntityDir) = reinterpret_cast<uintptr_t>(fakeDirVtbl);

    static uintptr_t fakeEntVtbl[64] = {};
    fakeEntVtbl[0x178 / 8] = reinterpret_cast<uintptr_t>(&FakeDirHooks::GetAsUnit); // +0x178
    *reinterpret_cast<uintptr_t*>(fakeGameEntity) = reinterpret_cast<uintptr_t>(fakeEntVtbl);

    *reinterpret_cast<int32_t*>(fakePlayer + 0xA8) = 2;    // slot 2
    *reinterpret_cast<int32_t*>(fakePlayer + 0xAC) = 1;    // team 1
    *reinterpret_cast<uint32_t*>(fakePlayer + 0xB0) = 42u; // hero entity index
    *reinterpret_cast<uint16_t*>(fakePlayer + 0x2D8) = 7u; // m_unNameIndex -> GetNetString(7)

    fakeHead[0x19] = 1; // _Isnil sentinel
    *reinterpret_cast<uintptr_t*>(fakeHead + 0x00) = reinterpret_cast<uintptr_t>(fakeNode); // leftmost
    fakeNode[0x19] = 0;
    *reinterpret_cast<uintptr_t*>(fakeNode + 0x00) = reinterpret_cast<uintptr_t>(fakeHead);
    *reinterpret_cast<uintptr_t*>(fakeNode + 0x08) = reinterpret_cast<uintptr_t>(fakeHead);
    *reinterpret_cast<uintptr_t*>(fakeNode + 0x10) = reinterpret_cast<uintptr_t>(fakeHead);
    *reinterpret_cast<void**>(fakeNode + 0x28) = fakePlayer;

    *reinterpret_cast<void**>(fakeGame + 0x28) = fakeEntityDir;
    *reinterpret_cast<uintptr_t*>(fakeGame + 0x1E8) = reinterpret_cast<uintptr_t>(fakeHead);

    void* pGamePtr = fakeGame;
    uint8_t dummyNetMgr = 1;
    void* pNetMgrPtr = &dummyNetMgr;

    Kongor::QueueArcadeEngineContext mockCtx = {};
    mockCtx.ppGame = &pGamePtr;
    mockCtx.ppNetResourceManager = &pNetMgrPtr;
    mockCtx.pfnGetNetString = &FakeDirHooks::GetNetString;

    Kongor::RefreshServerKeyframePlayersFromEngine(&mockCtx);
    WriteServerKeyframe(testPort);

    memset(buffer, 0, sizeof(buffer));
    bytesRead = 0;
    hMap = OpenFileMappingA(FILE_MAP_READ, FALSE, mapName.c_str());
    if (hMap != NULL) {
        LPVOID pBuf = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 65536);
        if (pBuf) {
            memcpy(buffer, pBuf, 65536);
            bytesRead = 65536;
            UnmapViewOfFile(pBuf);
        }
        CloseHandle(hMap);
    }
    ExpectTrue(bytesRead >= 40, "KeyframeEngineSample_BytesRead");
    memcpy(&payloadSize, buffer + 32, 4);
    std::string sampledJson(reinterpret_cast<const char*>(buffer + 40), payloadSize);
    ExpectTrue(sampledJson.find("DisasmVerifiedHeroOwner") != std::string::npos, "KeyframeEngineSample_NetStringPlayerNameAt2D8");
    ExpectTrue(sampledJson.find("Hero_Chronos") != std::string::npos, "KeyframeEngineSample_HeroChronosAtUnit30");
    ExpectTrue(sampledJson.find("7420.50") != std::string::npos, "KeyframeEngineSample_UnitPosXAtD8");
    ExpectTrue(sampledJson.find("\"maxHealth\":1850.0") != std::string::npos, "KeyframeEngineSample_UnitMaxHealthAt368");
    ExpectTrue(sampledJson.find("\"maxMana\":900.0") != std::string::npos, "KeyframeEngineSample_UnitMaxManaAt36C");
    ExpectTrue(sampledJson.find("\"level\":16") != std::string::npos, "KeyframeEngineSample_UnitLevelAt35C");

    ClearKeyframePlayerCoordinates();
    CleanupServerKeyframeMap();
}

void TestTracyProfilerDevZones() {
    {
        KONGOR_TRACY_ZONE_SCOPED();
        KONGOR_TRACY_ZONE_SCOPED_N("UnitTestScopedZone");
        KONGOR_TRACY_FRAME_MARK();
        KONGOR_TRACY_FRAME_MARK_NAMED("UnitTestFrame");
        KONGOR_TRACY_PLOT("UnitTestPlot", 42.0);
    }
    ExpectTrue(true, "TracyProfilerDevZones_ExecutesCleanly");
#if defined(KONGOR_TRACY_ENABLE)
    ExpectTrue(true, "TracyProfilerDevZones_EnabledBuildFlagActive");
#else
    ExpectTrue(true, "TracyProfilerDevZones_DisabledBuildDefaultNoOp");
#endif
}

// protects: Stage 1A write-clamp (0x60 in-place byte[11] clamp), Stage 1A client/replay
//           read-bounds guard (truncated 28-byte kill5 packet in historical .honreplay),
//           Stage 1B 128B kill payload expansion (5..9 & up to 29 assists), and Layer 2
//           modular uint8_t state-sequence desync consecutive-frame tracker.
// fails_when: 28-byte kill5 buffer leaks un-clamped numAssists=5, client/replay guard
//             over-reads past unreadBytes, Stage 1B truncates >=5 assists or corrupts
//             header/sorting, or Layer 2 misclassifies uint8_t 255->0 wraparound.
// seam: Direct queue_arcade_hooks.h framing, assist-collector, expansion & tracker APIs.
void TestMidwarsKillPacketGuardsAndDesyncTelemetry() {
    printf("\n--- Suite: Mid Wars 10v10 Kill Packet (0x60) Guards & Snapshot Desync ---\n");

    // 1. Stage 1A Write-Side Clamp: 28-byte 0x60 packet claiming numAssists = 5 -> clamped to 4.
    uint8_t trunc28[28] = {};
    trunc28[0] = Kongor::kGameCmdKillMessage; // 0x60
    const int32_t killer = 3;
    const int32_t victim = 14;
    const uint16_t gold = 325;
    memcpy(trunc28 + 1, &killer, 4);
    memcpy(trunc28 + 5, &victim, 4);
    memcpy(trunc28 + 9, &gold, 2);
    trunc28[11] = 5; // Claims 12 + 4*5 = 32 bytes in a 28-byte buffer!
    for (int i = 0; i < 4; ++i) {
        int32_t a = i + 1;
        memcpy(trunc28 + 12 + i * 4, &a, 4);
    }

    uint8_t oldAssists = 0;
    uint8_t newAssists = 0;
    const bool clamped28 = Kongor::ClampKillMessageBufferInPlace(trunc28, sizeof(trunc28), &oldAssists, &newAssists);
    ExpectTrue(clamped28 && oldAssists == 5 && newAssists == 4 && trunc28[11] == 4,
               "Midwars_Stage1A_WriteClamp_28B_Kill5_To_Kill4");

    // Negative controls: valid 28-byte kill4 must NOT clamp; non-0x60 opcode must NOT touch byte[11].
    ExpectTrue(!Kongor::ClampKillMessageBufferInPlace(trunc28, sizeof(trunc28), &oldAssists, &newAssists),
               "Midwars_Stage1A_WriteClamp_ValidKill4_NoOp");

    uint8_t nonKill[28] = {};
    nonKill[0] = 0x61;
    nonKill[11] = 9;
    ExpectTrue(!Kongor::ClampKillMessageBufferInPlace(nonKill, sizeof(nonKill)) && nonKill[11] == 9,
               "Midwars_Stage1A_WriteClamp_Non0x60_NoOp");

    // Short 12-byte header clamps byte[11] to 0 without OOB write.
    uint8_t shortHdr[12] = {};
    shortHdr[0] = Kongor::kGameCmdKillMessage;
    shortHdr[11] = 4;
    ExpectTrue(Kongor::ClampKillMessageBufferInPlace(shortHdr, 12) && shortHdr[11] == 0,
               "Midwars_Stage1A_WriteClamp_12B_To_0");

    // 2. Stage 1A Client Read-Side / Replay Guard: 28-byte replay packet declaring numAssists=5
    //    must clamp to 4 before CClientGame reads the 5th assist (no 4-byte CPacket over-read).
    uint8_t replay28[28] = {};
    replay28[0] = Kongor::kGameCmdKillMessage;
    replay28[11] = 5;
    uint8_t rOld = 0, rNew = 0;
    ExpectTrue(!Kongor::IsKillMessageHeaderTruncated(replay28, sizeof(replay28)),
               "Midwars_ReplayGuard_28B_HeaderComplete");
    ExpectTrue(Kongor::GuardClientKillMessagePacketRead(replay28, sizeof(replay28), &rOld, &rNew) &&
                   rOld == 5 && rNew == 4 && replay28[11] == 4,
               "Midwars_ReplayGuard_Clamps_28B_Kill5_To_4");

    // Trailing 0x60 command at tail of multi-command CPacket with only 8 unread bytes -> truncated header.
    uint8_t tail8[8] = {Kongor::kGameCmdKillMessage, 1, 0, 0, 0, 2, 0, 0};
    ExpectTrue(Kongor::IsKillMessageHeaderTruncated(tail8, sizeof(tail8)),
               "Midwars_ReplayGuard_Detects_TruncatedHeader_8B");
    ExpectTrue(!Kongor::GuardClientKillMessagePacketRead(tail8, sizeof(tail8)),
               "Midwars_ReplayGuard_Rejects_TruncatedHeader_8B");

    // 3. Stage 1B Assist Collection + 128B Expansion (5..9 assists & 29-assist cap).
    struct FakeUnit {
        uint8_t pad[0x6D4];
        int32_t ownerClientNum;
    };
    FakeUnit uAttacker{}, uAssist0{}, uAssist1{}, uAssist2{}, uAssist3{}, uAssist4{}, uAssist5{}, uDup{}, uNeg{};
    uAttacker.ownerClientNum = 1;
    uAssist0.ownerClientNum = 8;
    uAssist1.ownerClientNum = 2;
    uAssist2.ownerClientNum = 6;
    uAssist3.ownerClientNum = 4;
    uAssist4.ownerClientNum = 9;
    uAssist5.ownerClientNum = 5;
    uDup.ownerClientNum = 6;  // duplicate assister must deduplicate
    uNeg.ownerClientNum = -1; // creep/neutral excluded

    uintptr_t unitPtrs[] = {
        reinterpret_cast<uintptr_t>(&uAttacker),
        reinterpret_cast<uintptr_t>(&uAssist0),
        reinterpret_cast<uintptr_t>(&uAssist1),
        0, // null unit entry skipped safely
        reinterpret_cast<uintptr_t>(&uAssist2),
        reinterpret_cast<uintptr_t>(&uDup),
        reinterpret_cast<uintptr_t>(&uNeg),
        reinterpret_cast<uintptr_t>(&uAssist3),
        reinterpret_cast<uintptr_t>(&uAssist4),
        reinterpret_cast<uintptr_t>(&uAssist5),
    };
    int32_t collected[Kongor::kKillMessageMaxAssists128B] = {};
    size_t collectedCount = 0;
    const bool okCollect = Kongor::CollectUniqueSortedAssistClientNums(
        unitPtrs,
        unitPtrs + sizeof(unitPtrs) / sizeof(unitPtrs[0]),
        reinterpret_cast<uintptr_t>(&uAttacker),
        0x6D4,
        collected,
        Kongor::kKillMessageMaxAssists128B,
        &collectedCount);
    ExpectTrue(okCollect && collectedCount == 6 &&
                   collected[0] == 2 && collected[1] == 4 && collected[2] == 5 &&
                   collected[3] == 6 && collected[4] == 8 && collected[5] == 9,
               "Midwars_Stage1B_CollectUniqueSortedAssists_6Assists");

    uint8_t expanded[Kongor::kKillMessageExpandedCapacity] = {};
    size_t expandedLen = 0;
    const bool okExpand = Kongor::BuildExpandedKillMessagePayload(
        trunc28, 28, collected, collectedCount, expanded, sizeof(expanded), &expandedLen);
    // 12 + 4 * 6 = 36 bytes
    int32_t outAssist5 = -1;
    memcpy(&outAssist5, expanded + 12 + 5 * 4, 4);
    ExpectTrue(okExpand && expandedLen == 36 && expanded[0] == 0x60 && expanded[11] == 6 && outAssist5 == 9,
               "Midwars_Stage1B_BuildExpandedPayload_6Assists_36B");
    ExpectTrue(!Kongor::ClampKillMessageBufferInPlace(expanded, expandedLen),
               "Midwars_Stage1B_Expanded36B_Passes_Stage1AClamp");

    // 9 assists -> 48 bytes (exact fit for kill9 in 10v10).
    int32_t nineAssists[9] = {0, 1, 2, 3, 4, 5, 6, 7, 8};
    size_t len9 = 0;
    ExpectTrue(Kongor::BuildExpandedKillMessagePayload(trunc28, 28, nineAssists, 9, expanded, sizeof(expanded), &len9) &&
                   len9 == 48 && expanded[11] == 9,
               "Midwars_Stage1B_BuildExpandedPayload_9Assists_48B");

    // Cap at 29 assists (128B ceiling) even if caller passes 32 entries.
    int32_t manyAssists[32] = {};
    for (int i = 0; i < 32; ++i) manyAssists[i] = i + 10;
    size_t lenCap = 0;
    ExpectTrue(Kongor::BuildExpandedKillMessagePayload(trunc28, 28, manyAssists, 32, expanded, sizeof(expanded), &lenCap) &&
                   lenCap == 128 && expanded[11] == 29,
               "Midwars_Stage1B_BuildExpandedPayload_CapsAt29Assists_128B");

    // Header validation + killer/victim pet exclusion before Stage 1B expansion:
    uint8_t valHdr[28] = {};
    valHdr[0] = Kongor::kGameCmdKillMessage;
    const int32_t kKillerId = 1;
    const int32_t kVictimId = 7;
    memcpy(valHdr + 1, &kKillerId, 4);
    memcpy(valHdr + 5, &kVictimId, 4);
    valHdr[11] = 5;
    int32_t petCand[7] = {1, 2, 3, 4, 5, 7, 9}; // contains killer pet (1) and victim (7) -> 5 true assists
    int32_t filteredOut[Kongor::kKillMessageMaxAssists128B] = {};
    size_t filteredLen = 0;
    ExpectTrue(Kongor::FilterAndValidateExpandedKillAssists(
                   valHdr, sizeof(valHdr), petCand, 7,
                   filteredOut, Kongor::kKillMessageMaxAssists128B, &filteredLen) &&
                   filteredLen == 5 && filteredOut[0] == 2 && filteredOut[4] == 9,
               "Midwars_Stage1B_FilterAndValidate_StripsKillerAndVictimPets");

    // Vanilla <= 4 assists must decline Stage 1B expansion (no truncation occurred).
    valHdr[11] = 4;
    ExpectTrue(!Kongor::FilterAndValidateExpandedKillAssists(
                   valHdr, sizeof(valHdr), petCand, 7,
                   filteredOut, Kongor::kKillMessageMaxAssists128B, &filteredLen),
               "Midwars_Stage1B_FilterAndValidate_DeclinesWhenDeclaredLe4");

    // Mismatched non-allied/deny damager count (declared 5 != filtered 4) must decline Stage 1B -> Stage 1A clamps.
    valHdr[11] = 5;
    int32_t fourTrue[5] = {1, 2, 3, 4, 5}; // stripping killer (1) leaves 4 != 5
    ExpectTrue(!Kongor::FilterAndValidateExpandedKillAssists(
                   valHdr, sizeof(valHdr), fourTrue, 5,
                   filteredOut, Kongor::kKillMessageMaxAssists128B, &filteredLen),
               "Midwars_Stage1B_FilterAndValidate_DeclinesOnAssistCountMismatch");

    // 4. Layer 2: CHostClient state-sequence desync consecutive-frame tracker.
    Kongor::SnapshotDesyncTrackerState desync{};
    bool shouldLog = false;
    ExpectTrue(!Kongor::UpdateSnapshotDesyncTracker(&desync, 255, 255, 1000, &shouldLog) && !shouldLog,
               "Midwars_Layer2_Seq255_Match_NoAlert");
    ExpectTrue(!Kongor::UpdateSnapshotDesyncTracker(&desync, 0, 0, 1050, &shouldLog) && !shouldLog,
               "Midwars_Layer2_Seq0_WrapMatch_NoAlert");
    ExpectTrue(!Kongor::UpdateSnapshotDesyncTracker(&desync, 1, 0, 1100, &shouldLog) &&
                   desync.consecutiveMismatchFrames == 1 && !shouldLog,
               "Midwars_Layer2_SingleFrameMismatch_NoAlert");
    ExpectTrue(Kongor::UpdateSnapshotDesyncTracker(&desync, 1, 0, 1150, &shouldLog) &&
                   desync.consecutiveMismatchFrames == 2 && shouldLog && desync.totalAlertsEmitted == 1,
               "Midwars_Layer2_TwoConsecutiveFrames_EmitsAlert");
    ExpectTrue(Kongor::UpdateSnapshotDesyncTracker(&desync, 1, 0, 2000, &shouldLog) && !shouldLog,
               "Midwars_Layer2_RateLimitsLog_Within5s");
    ExpectTrue(Kongor::UpdateSnapshotDesyncTracker(&desync, 1, 0, 6200, &shouldLog) &&
                   shouldLog && desync.totalAlertsEmitted == 2,
               "Midwars_Layer2_LogsAgain_After5s");
    ExpectTrue(!Kongor::UpdateSnapshotDesyncTracker(&desync, 1, 1, 6250, &shouldLog) &&
                   desync.consecutiveMismatchFrames == 0 && !shouldLog,
               "Midwars_Layer2_ResetsOnSequenceMatch");
}

// ----------------------------------------------------------------------------
// H2-4: Scrollbar Slider VMT Signature, Page-Protection & Dispatch Guards
// Evidence Dossier: artifacts/evidence/k2_scrollbar_slider_vmt_hook.json
// ----------------------------------------------------------------------------
struct ScrollbarMockTracker {
    static inline int s_csliderCalls = 0;
    static inline int s_origCalls = 0;
    static inline int s_lastButton = 0;
    static inline const void* s_lastCursor = nullptr;

    static void Reset() noexcept {
        s_csliderCalls = 0;
        s_origCalls = 0;
        s_lastButton = 0;
        s_lastCursor = nullptr;
    }

    static void __fastcall MockCSliderMouseDown(void*, int eButton, const void* cursorPos) noexcept {
        ++s_csliderCalls;
        s_lastButton = eButton;
        s_lastCursor = cursorPos;
    }

    static void __fastcall MockOrigScrollbarMouseDown(void*, int eButton, const void* cursorPos) noexcept {
        ++s_origCalls;
        s_lastButton = eButton;
        s_lastCursor = cursorPos;
    }

    static void __fastcall MockHookEntry(void*, int, const void*) noexcept {}
};

void TestScrollbarSliderVmtSignatureAndDispatchGuards() {
    // Allocate a synthetic .rdata page so we can verify real VirtualProtect read-only restoration
    void* rawPage = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    ExpectTrue(rawPage != nullptr, "ScrollbarVmt_AllocPage");
    if (!rawPage) return;

    uint8_t* pageBytes = static_cast<uint8_t*>(rawPage);
    void** ppSlot = reinterpret_cast<void**>(pageBytes + 0x100);
    uint8_t* fakeOrigCode = pageBytes + 0x200;
    uint8_t* fakeCSliderCode = pageBytes + 0x300;

    memcpy(fakeOrigCode, Kongor::kScrollbarSliderMouseDownPrologue15, sizeof(Kongor::kScrollbarSliderMouseDownPrologue15));
    memcpy(fakeCSliderCode, Kongor::kCSliderMouseDownPrologue12, sizeof(Kongor::kCSliderMouseDownPrologue12));

    // 1. Negative controls (OBL-NG-SCROLLBAR-01): mismatched slot pointer or prologue bytes must abort without mutating .rdata
    // satisfies: OBL-NG-SCROLLBAR-01
    *ppSlot = fakeCSliderCode; // wrong slot target (!= fakeOrigCode)
    DWORD oldProt = 0;
    VirtualProtect(rawPage, 4096, PAGE_READONLY, &oldProt);

    Kongor::FnK2SliderMouseDown savedOrig = nullptr;
    auto stWrongSlot = Kongor::InstallScrollbarSliderVmtHook(
        ppSlot, fakeOrigCode, fakeCSliderCode, reinterpret_cast<void*>(&ScrollbarMockTracker::MockHookEntry), &savedOrig);
    ExpectTrue(stWrongSlot == Kongor::ScrollbarVmtInstallStatus::SlotTargetMismatch &&
                   *ppSlot == fakeCSliderCode && savedOrig == nullptr,
               "ScrollbarVmt_OBL01_RejectsMismatchedVtableSlotTarget");

    // Wrong CScrollbarSlider::MouseDown prologue byte
    VirtualProtect(rawPage, 4096, PAGE_READWRITE, &oldProt);
    *ppSlot = fakeOrigCode;
    fakeOrigCode[0] ^= 0xFF;
    VirtualProtect(rawPage, 4096, PAGE_READONLY, &oldProt);
    auto stBadOrigPrologue = Kongor::InstallScrollbarSliderVmtHook(
        ppSlot, fakeOrigCode, fakeCSliderCode, reinterpret_cast<void*>(&ScrollbarMockTracker::MockHookEntry), &savedOrig);
    ExpectTrue(stBadOrigPrologue == Kongor::ScrollbarVmtInstallStatus::OrigPrologueMismatch &&
                   *ppSlot == fakeOrigCode && savedOrig == nullptr,
               "ScrollbarVmt_OBL01_RejectsMismatchedScrollbarPrologue");

    // Restore orig prologue, corrupt CSlider::MouseDown prologue byte
    VirtualProtect(rawPage, 4096, PAGE_READWRITE, &oldProt);
    fakeOrigCode[0] ^= 0xFF;
    fakeCSliderCode[2] ^= 0xFF;
    VirtualProtect(rawPage, 4096, PAGE_READONLY, &oldProt);
    auto stBadBasePrologue = Kongor::InstallScrollbarSliderVmtHook(
        ppSlot, fakeOrigCode, fakeCSliderCode, reinterpret_cast<void*>(&ScrollbarMockTracker::MockHookEntry), &savedOrig);
    ExpectTrue(stBadBasePrologue == Kongor::ScrollbarVmtInstallStatus::BasePrologueMismatch &&
                   *ppSlot == fakeOrigCode && savedOrig == nullptr,
               "ScrollbarVmt_OBL01_RejectsMismatchedCSliderPrologue");

    // 2. Positive install + PAGE_READONLY restoration + idempotent re-entry (OBL-NG-SCROLLBAR-03)
    // satisfies: OBL-NG-SCROLLBAR-03
    VirtualProtect(rawPage, 4096, PAGE_READWRITE, &oldProt);
    fakeCSliderCode[2] ^= 0xFF;
    VirtualProtect(rawPage, 4096, PAGE_READONLY, &oldProt);

    auto stInstalled = Kongor::InstallScrollbarSliderVmtHook(
        ppSlot, fakeOrigCode, fakeCSliderCode, reinterpret_cast<void*>(&ScrollbarMockTracker::MockHookEntry), &savedOrig);
    MEMORY_BASIC_INFORMATION mbi{};
    VirtualQuery(rawPage, &mbi, sizeof(mbi));
    ExpectTrue(stInstalled == Kongor::ScrollbarVmtInstallStatus::Installed &&
                   *ppSlot == reinterpret_cast<void*>(&ScrollbarMockTracker::MockHookEntry) &&
                   reinterpret_cast<void*>(savedOrig) == fakeOrigCode &&
                   mbi.Protect == PAGE_READONLY,
               "ScrollbarVmt_OBL03_InstallsHookPreservesOrigAndRestoresPageReadOnly");

    auto stIdempotent = Kongor::InstallScrollbarSliderVmtHook(
        ppSlot, fakeOrigCode, fakeCSliderCode, reinterpret_cast<void*>(&ScrollbarMockTracker::MockHookEntry), &savedOrig);
    ExpectTrue(stIdempotent == Kongor::ScrollbarVmtInstallStatus::AlreadyInstalled &&
                   reinterpret_cast<void*>(savedOrig) == fakeOrigCode,
               "ScrollbarVmt_OBL03_IdempotentReinstallDoesNotClobberOrigPointer");

    VirtualFree(rawPage, 0, MEM_RELEASE);

    // 3. Button routing + null thisPtr / cursorPos / m_pHandle (+0x190) safety guards (OBL-NG-SCROLLBAR-02)
    // satisfies: OBL-NG-SCROLLBAR-02
    std::vector<uint8_t> fakeWidget(0x270, 0);
    uint64_t dummyHandleObj = 0x12345678ULL;
    float cursorVec2[2] = { 120.0f, 340.0f };

    // Null thisPtr -> no-op
    ScrollbarMockTracker::Reset();
    Kongor::DispatchScrollbarSliderMouseDown(
        nullptr, Kongor::kK2ButtonMouseLeft, cursorVec2,
        &ScrollbarMockTracker::MockCSliderMouseDown, &ScrollbarMockTracker::MockOrigScrollbarMouseDown);
    ExpectTrue(ScrollbarMockTracker::s_csliderCalls == 0 && ScrollbarMockTracker::s_origCalls == 0,
               "ScrollbarVmt_OBL02_NullThisPtrIsSafeNoOp");

    // BUTTON_MOUSEL (0x137) with null m_pHandle at +0x190 -> must NOT call CSlider::MouseDown (avoids +0x9C9E1E AV), falls back to orig
    ScrollbarMockTracker::Reset();
    *reinterpret_cast<void**>(fakeWidget.data() + Kongor::kCSliderHandleFieldOffset) = nullptr;
    Kongor::DispatchScrollbarSliderMouseDown(
        fakeWidget.data(), Kongor::kK2ButtonMouseLeft, cursorVec2,
        &ScrollbarMockTracker::MockCSliderMouseDown, &ScrollbarMockTracker::MockOrigScrollbarMouseDown);
    ExpectTrue(ScrollbarMockTracker::s_csliderCalls == 0 && ScrollbarMockTracker::s_origCalls == 1,
               "ScrollbarVmt_OBL02_NullSliderHandleBypassesCSliderMouseDown");

    // BUTTON_MOUSEL (0x137) with non-null m_pHandle but null cursorPos -> bypasses CSlider::MouseDown (avoids +0x9C9E76 AV)
    ScrollbarMockTracker::Reset();
    *reinterpret_cast<void**>(fakeWidget.data() + Kongor::kCSliderHandleFieldOffset) = &dummyHandleObj;
    Kongor::DispatchScrollbarSliderMouseDown(
        fakeWidget.data(), Kongor::kK2ButtonMouseLeft, nullptr,
        &ScrollbarMockTracker::MockCSliderMouseDown, &ScrollbarMockTracker::MockOrigScrollbarMouseDown);
    ExpectTrue(ScrollbarMockTracker::s_csliderCalls == 0 && ScrollbarMockTracker::s_origCalls == 1,
               "ScrollbarVmt_OBL02_NullCursorPosBypassesCSliderMouseDown");

    // Valid BUTTON_MOUSEL (0x137) with non-null m_pHandle and cursorPos -> invokes CSlider::MouseDown directly
    ScrollbarMockTracker::Reset();
    Kongor::DispatchScrollbarSliderMouseDown(
        fakeWidget.data(), Kongor::kK2ButtonMouseLeft, cursorVec2,
        &ScrollbarMockTracker::MockCSliderMouseDown, &ScrollbarMockTracker::MockOrigScrollbarMouseDown);
    ExpectTrue(ScrollbarMockTracker::s_csliderCalls == 1 && ScrollbarMockTracker::s_origCalls == 0 &&
                   ScrollbarMockTracker::s_lastButton == 0x137 && ScrollbarMockTracker::s_lastCursor == cursorVec2,
               "ScrollbarVmt_OBL02_LeftClickDispatchesCSliderMouseDown");

    // Wheel up (0x13A) and wheel down (0x13B) -> delegate to retail CScrollbarSlider::MouseDown
    ScrollbarMockTracker::Reset();
    Kongor::DispatchScrollbarSliderMouseDown(
        fakeWidget.data(), 0x13A, cursorVec2,
        &ScrollbarMockTracker::MockCSliderMouseDown, &ScrollbarMockTracker::MockOrigScrollbarMouseDown);
    Kongor::DispatchScrollbarSliderMouseDown(
        fakeWidget.data(), 0x13B, cursorVec2,
        &ScrollbarMockTracker::MockCSliderMouseDown, &ScrollbarMockTracker::MockOrigScrollbarMouseDown);
    ExpectTrue(ScrollbarMockTracker::s_csliderCalls == 0 && ScrollbarMockTracker::s_origCalls == 2 &&
                   ScrollbarMockTracker::s_lastButton == 0x13B,
               "ScrollbarVmt_OBL02_WheelEventsDelegateToOrigScrollbarMouseDown");
}

__declspec(noinline) static bool TriggerIntentionalAvInLeafSehHelper(volatile int* volatile targetPtr, int* outObservedDepthInsideSeh) noexcept {
    __try {
        if (outObservedDepthInsideSeh) {
            *outObservedDepthInsideSeh = Kongor_GetProtectedSehDepth();
        }
        volatile int* p = targetPtr;
        *p = 42;
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return true;
    }
}

void TestVidBlitMipPitchGuardVehExclusionAndProtectedSeh() {
    // (a) Kongor_VidBlitShouldZeroFillMismatchedMip: exact mip-level pitch mismatch math
    size_t zeroBytes = 0xDEADBEEF;
    int destW = -1, destH = -1;

    ExpectTrue(
        Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, 16, &zeroBytes, &destW, &destH) &&
            zeroBytes == 64 && destW == 4 && destH == 4,
        "VidBlit_64x64_Fmt0_Pitch16_ZeroFills4x4Mip64B");

    ExpectTrue(
        Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, 8, &zeroBytes, &destW, &destH) &&
            zeroBytes == 16 && destW == 2 && destH == 2,
        "VidBlit_64x64_Fmt0_Pitch8_ZeroFills2x2Mip16B");

    ExpectTrue(
        Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, 4, &zeroBytes, &destW, &destH) &&
            zeroBytes == 4 && destW == 1 && destH == 1,
        "VidBlit_64x64_Fmt0_Pitch4_ZeroFills1x1Mip4B");

    // Formats 0x0E (remapped to 0), 0x0F, 0x10 are all 4 BPP in vid_d3d9_x64.dll+0x982D0
    for (int fmt : { 0x0E, 0x0F, 0x10 }) {
        zeroBytes = 0;
        ExpectTrue(
            Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, fmt, 16, &zeroBytes) && zeroBytes == 64,
            "VidBlit_64x64_4BppAliasFormats_Pitch16_ZeroFills64B");
    }

    // Valid pitches (>= width * 4) must NOT trigger zero-fill fallback
    zeroBytes = 999;
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, 256, &zeroBytes) && zeroBytes == 0,
        "VidBlit_64x64_Fmt0_Pitch256_NormalBlitAllowed");

    zeroBytes = 999;
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, 512, &zeroBytes) && zeroBytes == 0,
        "VidBlit_64x64_Fmt0_Pitch512_PaddedNormalBlitAllowed");

    // Non-positive pitches and invalid dimensions must return false with zeroBytes == 0
    zeroBytes = 999;
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, 0, &zeroBytes) && zeroBytes == 0,
        "VidBlit_PitchZero_ReturnsFalseZeroBytes");

    zeroBytes = 999;
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0, -16, &zeroBytes) && zeroBytes == 0,
        "VidBlit_PitchNegative_ReturnsFalseZeroBytes");

    zeroBytes = 999;
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(0, 64, 0, 16, &zeroBytes) && zeroBytes == 0,
        "VidBlit_ZeroWidth_ReturnsFalseZeroBytes");

    // Non-square mip aspect scaling & height clamping
    ExpectTrue(
        Kongor_VidBlitShouldZeroFillMismatchedMip(128, 64, 0, 16, &zeroBytes, &destW, &destH) &&
            destW == 4 && destH == 2 && zeroBytes == 32,
        "VidBlit_NonSquare128x64_Pitch16_ZeroFills4x2Mip32B");

    ExpectTrue(
        Kongor_VidBlitShouldZeroFillMismatchedMip(128, 16, 0, 4, &zeroBytes, &destW, &destH) &&
            destW == 1 && destH == 1 && zeroBytes == 4,
        "VidBlit_NonSquare128x16_Pitch4_ClampsHeightTo1");

    // 1-BPP format (0x03): pitch 64 is valid for 64x64; pitch 4 is mismatched 4x4 (16 bytes)
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0x03, 64, &zeroBytes) && zeroBytes == 0,
        "VidBlit_Fmt03_1Bpp_Pitch64_NormalBlitAllowed");
    ExpectTrue(
        Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 0x03, 4, &zeroBytes, &destW, &destH) &&
            destW == 4 && destH == 4 && zeroBytes == 16,
        "VidBlit_Fmt03_1Bpp_Pitch4_ZeroFills4x4Mip16B");

    // Unrecognized format (> 0x10) conservatively skips zero-fill
    zeroBytes = 999;
    ExpectTrue(
        !Kongor_VidBlitShouldZeroFillMismatchedMip(64, 64, 99, 16, &zeroBytes) && zeroBytes == 0,
        "VidBlit_UnknownFormat_ReturnsFalse");

    // (b) Kongor_IsVidBlitBitmapVehExcluded: dynamic hook status & exact [0x982D0, 0x99400) bounds
    ExpectTrue(!g_vidBlitHookInstalled.load(std::memory_order_acquire),
               "VidBlit_HookFlagDefaultsFalseInUnitTests");
    ExpectTrue(Kongor_IsVidBlitBitmapVehExcluded(0x98740u, true),
               "VidBlitVeh_Fault0x98740_ExcludedWhenHookInstalled");
    ExpectTrue(!Kongor_IsVidBlitBitmapVehExcluded(0x98740u, false),
               "VidBlitVeh_Fault0x98740_NotExcludedWhenHookAbsent");
    ExpectTrue(Kongor_IsVidBlitBitmapVehExcluded(0x982D0u, true),
               "VidBlitVeh_StartBoundary0x982D0_Excluded");
    ExpectTrue(Kongor_IsVidBlitBitmapVehExcluded(0x993FFu, true),
               "VidBlitVeh_EndMinus1Boundary0x993FF_Excluded");
    ExpectTrue(!Kongor_IsVidBlitBitmapVehExcluded(0x99400u, true),
               "VidBlitVeh_EndBoundary0x99400_NotExcluded");
    ExpectTrue(!Kongor_IsVidBlitBitmapVehExcluded(0x982CFu, true),
               "VidBlitVeh_BeforeStart0x982CF_NotExcluded");
    ExpectTrue(!Kongor_IsVidBlitBitmapVehExcluded(0x29100u, true),
               "VidBlitVeh_Draw2d0x29100_NeverExcludedWhenHookInstalled");
    ExpectTrue(!Kongor_IsVidBlitBitmapVehExcluded(0x29100u, false),
               "VidBlitVeh_Draw2d0x29100_NeverExcludedWhenHookAbsent");

    // (c) Kongor_EnterProtectedSeh / Kongor_LeaveProtectedSeh nesting & restoration across an SEH-caught AV
    ExpectTrue(Kongor_GetProtectedSehDepth() == 0, "ProtectedSeh_InitialDepthZero");
    Kongor_EnterProtectedSeh();
    ExpectTrue(Kongor_GetProtectedSehDepth() == 1, "ProtectedSeh_ManualDepth1");
    Kongor_EnterProtectedSeh();
    ExpectTrue(Kongor_GetProtectedSehDepth() == 2, "ProtectedSeh_ManualDepth2");
    Kongor_LeaveProtectedSeh();
    ExpectTrue(Kongor_GetProtectedSehDepth() == 1, "ProtectedSeh_ManualUnwindTo1");
    Kongor_LeaveProtectedSeh();
    ExpectTrue(Kongor_GetProtectedSehDepth() == 0, "ProtectedSeh_ManualUnwindTo0");

    PVOID vehHandle = AddVectoredExceptionHandler(1, NativeGuardVectoredHandler);
    int observedDepth = -1;
    bool avCaught = false;
    {
        KongorScopedProtectedSeh outerGuard;
        ExpectTrue(Kongor_GetProtectedSehDepth() == 1, "ProtectedSeh_ScopedOuterDepth1");
        {
            KongorScopedProtectedSeh innerGuard;
            ExpectTrue(Kongor_GetProtectedSehDepth() == 2, "ProtectedSeh_ScopedInnerDepth2");
            avCaught = TriggerIntentionalAvInLeafSehHelper(nullptr, &observedDepth);
            ExpectTrue(Kongor_GetProtectedSehDepth() == 2, "ProtectedSeh_DepthPreservedAfterSehCatch");
        }
        ExpectTrue(Kongor_GetProtectedSehDepth() == 1, "ProtectedSeh_ScopedInnerRestoredTo1");
    }
    if (vehHandle) {
        RemoveVectoredExceptionHandler(vehHandle);
    }
    ExpectTrue(avCaught && observedDepth == 2 && Kongor_GetProtectedSehDepth() == 0,
               "ProtectedSeh_NestedFaultCaughtAndDepthRestoredToZeroWithoutVehDump");
}

// ----------------------------------------------------------------------------
// 30. Pre-v5.0.2 Shutdown Sentinel Lifecycle & Fatal Exception Classifiers
// ----------------------------------------------------------------------------
void TestShutdownSentinelTransitionsAndFatalExceptionClassifier() {
    // 1. Verify RtlDllShutdownInProgress() returns false during normal execution
    Kongor_WarmRtlDllShutdownInProgressCache();
    ExpectTrue(!Kongor_IsRtlDllShutdownInProgress(), "RtlDllShutdownInProgress_FalseDuringNormalExecution");

    // 2. Verify unrecoverable vs shutdown-fatal exception code classifiers
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xC0000374UL), "Unrecoverable_HeapCorruption");
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xC0000409UL), "Unrecoverable_StackBufferOverrun");
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xC0000194UL), "Unrecoverable_PossibleDeadlock");
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xC0000025UL), "Unrecoverable_Noncontinuable");
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xC000001DUL), "Unrecoverable_IllegalInstruction");
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xE0000001UL), "Unrecoverable_CrtPureVirtual");
    ExpectTrue(Kongor_IsUnrecoverableFatalExceptionCode(0xE0000002UL), "Unrecoverable_CrtInvalidParam");
    ExpectTrue(!Kongor_IsUnrecoverableFatalExceptionCode(EXCEPTION_ACCESS_VIOLATION), "Unrecoverable_ExcludesFirstChanceAV");
    ExpectTrue(!Kongor_IsUnrecoverableFatalExceptionCode(0x406D1388UL), "Unrecoverable_ExcludesSetThreadName");

    ExpectTrue(Kongor_IsShutdownFatalExceptionCode(EXCEPTION_ACCESS_VIOLATION), "ShutdownFatal_IncludesAV");
    ExpectTrue(Kongor_IsShutdownFatalExceptionCode(EXCEPTION_IN_PAGE_ERROR), "ShutdownFatal_IncludesInPageError");
    ExpectTrue(Kongor_IsShutdownFatalExceptionCode(EXCEPTION_STACK_OVERFLOW), "ShutdownFatal_IncludesStackOverflow");
    ExpectTrue(Kongor_IsShutdownFatalExceptionCode(0xC0000194UL), "ShutdownFatal_IncludesDeadlock");
    ExpectTrue(!Kongor_IsShutdownFatalExceptionCode(0xE06D7363UL), "ShutdownFatal_ExcludesMsvcCppThrow");
    ExpectTrue(!Kongor_IsShutdownFatalExceptionCode(0x406D1388UL), "ShutdownFatal_ExcludesSetThreadName");

    // 3. Verify two-phase shutdown sentinels: session-active -> quit-initiated -> clean-exit -> revoked on crash
    const DWORD pid = ::GetCurrentProcessId();
    std::error_code ec;
    const std::filesystem::path tempDir =
        std::filesystem::temp_directory_path(ec) / ("kongor_shutdown_sentinel_test_" + std::to_string(pid));
    std::filesystem::remove_all(tempDir, ec);
    std::filesystem::create_directories(tempDir, ec);

    const std::filesystem::path activePath = tempDir / ("session-active." + std::to_string(pid) + ".sentinel");
    const std::filesystem::path quitPath   = tempDir / ("quit-initiated." + std::to_string(pid) + ".sentinel");
    const std::filesystem::path cleanPath  = tempDir / ("clean-exit." + std::to_string(pid) + ".sentinel");
    const std::filesystem::path crashPath  = tempDir / ("last-crashed." + std::to_string(pid) + ".sentinel");

    g_quitInitiated.store(false, std::memory_order_release);
    g_quitInitiatedTickMs.store(0, std::memory_order_release);
    g_userInitiatedCleanExit.store(false, std::memory_order_release);

    InitSessionCrashSentinelDirs({ tempDir }, /*trackForegroundSession=*/true);
    ExpectTrue(std::filesystem::exists(activePath, ec), "Sentinel_SessionActiveCreated");

    MarkQuitInitiated("Test_CSystem_Exit");
    ExpectTrue(g_quitInitiated.load(std::memory_order_acquire), "Sentinel_QuitInitiatedFlagSet");
    ExpectTrue(std::filesystem::exists(quitPath, ec), "Sentinel_QuitInitiatedFileCreated");
    ExpectTrue(std::filesystem::exists(activePath, ec), "Sentinel_SessionActivePreservedDuringQuitPhase1");
    ExpectTrue(!std::filesystem::exists(cleanPath, ec), "Sentinel_CleanExitNotWrittenPrematurely");

    MarkCleanUserExit("CHost::Shutdown_Completed");
    ExpectTrue(std::filesystem::exists(cleanPath, ec), "Sentinel_CleanExitWrittenAfterHostShutdown");
    ExpectTrue(!std::filesystem::exists(activePath, ec), "Sentinel_SessionActiveDeletedAfterHostShutdown");
    ExpectTrue(std::filesystem::exists(quitPath, ec), "Sentinel_QuitInitiatedRetainedForWatchdog");

    RevokeCleanExitSentinelsNoAlloc();
    WritePerPidCrashSentinelNoAlloc("ShutdownUnhandledException", static_cast<int>(0xC0000194UL), "nvoglv64.dll+0x1234", "nvoglv64.dll+0x1234");
    ExpectTrue(!std::filesystem::exists(cleanPath, ec), "Sentinel_CleanExitRevokedOnShutdownFault");
    ExpectTrue(!g_userInitiatedCleanExit.load(std::memory_order_acquire), "Sentinel_UserCleanFlagClearedOnRevoke");
    ExpectTrue(std::filesystem::exists(crashPath, ec), "Sentinel_PerPidCrashSentinelWrittenOnShutdownFault");

    // Reset state & clean temp directory
    g_quitInitiated.store(false, std::memory_order_release);
    g_quitInitiatedTickMs.store(0, std::memory_order_release);
    g_userInitiatedCleanExit.store(false, std::memory_order_release);
    InitSessionCrashSentinelDirs({}, false);
    std::filesystem::remove_all(tempDir, ec);
}

} // anonymous namespace

int main() {
    SetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", "1");
    Kongor::PatchController::Initialize();

    TestTracyProfilerDevZones();

    TestVehBenignFilterAndAvClassifier();
    TestPdataChainedUnwindResolver();
    TestPatchControllerIntervalGuardAndPhase14SelfTest();
    TestCodeCaveAndVtableOffsetCache();
    TestVehAmd64GpfAndPoisonClassifier();
    TestBoundedStackScanAndCallValidator();
    TestHde64PrologueValidatorAndNullTrampolineOptOut();
    TestCursorConfinementStateMachineAndHeadlessConfigIsolation();
    TestOfflineP2pNicknameResolutionAndOnlineGuard();
    TestDedicatedServerCmdLineCvarAndRvaSafety();
    TestDynamicClientPortAllocationAndConnectRewrite();
    TestWave5SoundNegCacheDraw2dStubAndReconnect6f();
    TestCommandPipeOwnerRestrictedDaclAndRemoteSmbReject();
    TestUnifiedNativeTargetsAndOffThreadLuaMemorySafety();
    TestClientHighDpiAwarenessGuards();
    TestCGameEntityDirectoryUafGuard();
    TestDedicatedServerConsoleGuardsAndDbgHelpLock();
    TestChatManagerSetChatMode();
    TestVfsLooseCacheReadOnlyIndexAndDynamicBypass();
    Test20PlayerTeamSlotReaderAndSafetyObligations();
    TestQueueArcadeSlotSwapAndHandshakeObligations();
    TestQueueArcadeTrack5SwapParityAndSelfHeal();
    TestPostIntroBackgroundAssetPrewarmerAndCmdTable();
    TestTier1PrivacyMiniDumpCallbackAndSelfCapture();
    TestPipeSecurityPlaytestLockdown();
    TestDuplexLuaRpcFraming();
    TestServerKeyframeShmTelemetryBinary();
    TestMidwarsKillPacketGuardsAndDesyncTelemetry();
    TestScrollbarSliderVmtSignatureAndDispatchGuards();
    TestVidBlitMipPitchGuardVehExclusionAndProtectedSeh();
    TestShutdownSentinelTransitionsAndFatalExceptionClassifier();

    printf("\n[KONGOR NATIVE_GUARD C++ UNIT TESTS] Total=%d Passed=%d Failed=%d\n",
           g_totalChecks, g_totalChecks - g_failedChecks, g_failedChecks);
    printf("[PASS] All %d/%d NativeGuard C++ unit assertions passed.\n",
           g_totalChecks - g_failedChecks, g_totalChecks);
    return (g_failedChecks == 0) ? 0 : 1;
}
