// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard Internal Cross-TU Declarations (Option C2)
// Explicit cross-translation-unit contracts for version.dll subsystems.
// ============================================================================
#pragma once

#include <windows.h>
#include <sddl.h>
#ifndef _WINSOCKAPI_
#include <winsock.h>
#endif
#ifndef PIPE_REJECT_REMOTE_CLIENTS
#define PIPE_REJECT_REMOTE_CLIENTS 0x00000008
#endif
#ifndef SO_EXCLUSIVEADDRUSE
#define SO_EXCLUSIVEADDRUSE ((int)(~SO_REUSEADDR))
#endif
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>
#include <filesystem>
#include "kongor_shared_mem.h"
#include "cursor_confinement.h"
#include "modern_archive_hook.h" 

extern LPTOP_LEVEL_EXCEPTION_FILTER pPreviousFilter;

// Emergency cursor confinement release (crash filter + VEH + background watchdog safe via AttachThreadInput)
void Kongor_RegisterEmergencyCursorReleaseContext(
    BOOL (WINAPI* pfnRealClipCursor)(const RECT*),
    HWND hGameWnd,
    std::atomic<bool>* pCurrentlyConfinedFlag,
    void* pStateMachine = nullptr) noexcept;
void Kongor_ForceReleaseCursorEmergency() noexcept;

// Protected SEH depth tracking across K2 hook callbacks & VEH handler
void Kongor_EnterProtectedSeh() noexcept;
void Kongor_LeaveProtectedSeh() noexcept;
int  Kongor_GetProtectedSehDepth() noexcept;

// MSVC C2712-safe RAII scope guard for Protected SEH: construct in outer C++ function
// and keep __try/__except inside a leaf helper with no unwinding C++ locals.
struct KongorScopedProtectedSeh {
    KongorScopedProtectedSeh() noexcept { Kongor_EnterProtectedSeh(); }
    ~KongorScopedProtectedSeh() noexcept { Kongor_LeaveProtectedSeh(); }
    KongorScopedProtectedSeh(const KongorScopedProtectedSeh&) = delete;
    KongorScopedProtectedSeh& operator=(const KongorScopedProtectedSeh&) = delete;
};

// Shared UTC timestamp helper
std::string GetTimestampString();
std::string GetFileSafeTimestamp();

// VEH / Unhandled Exception / CRT Fault Handlers (veh_crash_shield.cpp)
LONG WINAPI NativeGuardUnhandledExceptionFilter(PEXCEPTION_POINTERS pExceptionInfo);
LONG WINAPI NativeGuardVectoredHandler(PEXCEPTION_POINTERS pExceptionInfo);
void __cdecl NativeGuardPurecallHandler();
void __cdecl NativeGuardInvalidParameterHandler(
    const wchar_t* expression,
    const wchar_t* function,
    const wchar_t* file,
    unsigned int line,
    uintptr_t pReserved);

// D3D9 8,192 Texture Table Reclaimer & VidBlitBitmap Crash Shield (d3d9_texture_healer.cpp)
DWORD WINAPI VidD3D9TexturePatcherWorker(LPVOID lpParam);
extern std::atomic<bool> g_vidBlitHookInstalled;
bool Kongor_VidBlitShouldZeroFillMismatchedMip(
    int width,
    int height,
    int iFormat,
    int iPitch,
    size_t* outZeroBytes,
    int* outDestW = nullptr,
    int* outDestH = nullptr) noexcept;
bool Kongor_IsVidBlitBitmapVehExcluded(uintptr_t relRip, bool vidBlitHookInstalled) noexcept;

// SIMD Ray Bridge & Float Determinism (simd_ray_bridge.cpp)
extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism();

// Direct C++ unit-testable crash shield primitives (#166, #167, #168, #255)
struct _MINIDUMP_CALLBACK_INPUT;
struct _MINIDUMP_CALLBACK_OUTPUT;

struct KongorTier1DumpCallbackCtx {
    DWORD TargetStackThreadId;
    uintptr_t FaultAddress;
    uint32_t ModulesStripped;
    uint32_t ThirdPartyCvCleared;
    uint32_t BackgroundStacksSuppressed;
};

bool Kongor_IsSafeEngineOrSystemModuleW(const wchar_t* rawModulePath, const wchar_t* baseName) noexcept;
BOOL CALLBACK Kongor_Tier1PrivacyMiniDumpCallback(
    PVOID CallbackParam,
    _MINIDUMP_CALLBACK_INPUT* CallbackInput,
    _MINIDUMP_CALLBACK_OUTPUT* CallbackOutput) noexcept;

bool Kongor_IsBenignDebugOrThreadNameException(DWORD code) noexcept;
const char* Kongor_ClassifyAccessViolationKind(ULONG_PTR accessKindCode) noexcept;
bool Kongor_ResolveChainedPdataFunctionEntry(
    uintptr_t imageBase,
    const RUNTIME_FUNCTION* pInitialRf,
    uint32_t* outBeginRva,
    uint32_t* outEndRva,
    uint32_t* outHops = nullptr) noexcept;
bool Kongor_TestSehExtractMsvcCppException(const EXCEPTION_RECORD* pRec, char* outBuf, size_t outCap) noexcept;
bool Kongor_TestTrampolineUnwindLookup(uintptr_t trampPc, uintptr_t* outModBase, uint32_t* outBeginRva) noexcept;

// (#260) AMD64 Non-Canonical #GPF (0xFFFFFFFFFFFFFFFF) Register Inspector & Poison/Bit-Flip Classifier
bool Kongor_IsAmd64NonCanonicalAddress(uintptr_t addr) noexcept;
bool Kongor_InspectAmd64GeneralProtectionFault(
    uintptr_t faultTargetAddr,
    const uint64_t gprValues[16],
    char* outDiag,
    size_t outCap,
    int* outNonCanonicalRegIdx = nullptr,
    uintptr_t* outNonCanonicalVal = nullptr) noexcept;
bool Kongor_ClassifyPointerPoisonOrBitFlip(
    uintptr_t faultAddr,
    uintptr_t canonicalBaseAddr,
    char* outPatternTag,
    size_t tagCap,
    bool* outIsHeuristicBitFlip = nullptr,
    uint32_t* outFlippedBitIndex = nullptr) noexcept;

// (#261) Bounded 40-Qword ([Rsp, Rsp + 320]) Stack-Scan Fallback & Preceding CALL Validator
bool Kongor_ValidatePrecedingCallInstruction(
    const uint8_t* codeBeforeRet,
    size_t lenBeforeRet,
    char* outEncodingTag,
    size_t tagCap) noexcept;
size_t Kongor_ScanBoundedStackForValidCallSites(
    const uintptr_t* stackQwords,
    size_t qwordCount,
    uintptr_t moduleBase,
    uint32_t textStartRva,
    uint32_t textEndRva,
    uintptr_t* outValidRetAddrs,
    size_t maxOut) noexcept;

// [L2-P0-01] x64 Single-Instruction Length Disassembler (control-flow refusal),
// RtlVirtualUnwind Caller Unwinder & Bounded Per-(TID, RIP) Recovery Budget
uint32_t Kongor_DecodeSingleInsnLength64(const uint8_t* code, size_t maxBytes) noexcept;
bool Kongor_TryRecordAndAllowVehRecovery(uint32_t threadId, uintptr_t rip) noexcept;
void Kongor_ResetVehRecoveryBudgetTable() noexcept;
bool Kongor_TrySafeUnwindCallerFrame64(CONTEXT* ioCtx) noexcept;

extern "C" __declspec(dllexport) uint32_t __cdecl Kongor_NativeGuard_DecodeInsnLength64(const uint8_t* code, uint32_t maxBytes);
extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_TestVehRecoveryBudget(uint32_t threadId, uint64_t rip);
extern "C" __declspec(dllexport) void __cdecl Kongor_NativeGuard_ResetVehRecoveryBudget();
extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_VerifyIpcToken(
    const char* candidatePayload,
    uint32_t candidateLen,
    const char* expectedToken,
    uint32_t expectedLen);

// P2P & Offline Practice Nickname Resolution (CHostClient::Connect / CClientAccount::m_sNickname)
enum class KongorOfflineNickSource : uint8_t {
    PreservedOnlineAccount = 0,
    P2pNicknameCvar        = 1,
    LoginNameCvar          = 2,
    TrackedConsoleFallback = 3,
    DefaultPlayerFallback  = 4
};

inline const char* Kongor_OfflineNickSourceToString(KongorOfflineNickSource src) noexcept {
    switch (src) {
        case KongorOfflineNickSource::PreservedOnlineAccount: return "online_account_preserved";
        case KongorOfflineNickSource::P2pNicknameCvar:        return "p2p_nickname";
        case KongorOfflineNickSource::LoginNameCvar:          return "login_name";
        case KongorOfflineNickSource::TrackedConsoleFallback: return "tracked_console";
        case KongorOfflineNickSource::DefaultPlayerFallback:  return "fallback_Player";
        default:                                              return "unknown";
    }
}

inline bool Kongor_SanitizeOfflineNicknameCandidateW(
    const wchar_t* rawCandidate,
    wchar_t* outBuf,
    size_t outCap) noexcept
{
    if (!outBuf || outCap < 2) return false;
    outBuf[0] = L'\0';
    if (!rawCandidate) return false;

    const wchar_t* p = rawCandidate;
    while (*p == L' ' || *p == L'\t' || *p == L'\r' || *p == L'\n' || *p == L'"' || *p == L'\'') {
        ++p;
    }

    constexpr size_t kMaxNickChars = 24;
    const size_t maxCopy = (outCap - 1 < kMaxNickChars) ? (outCap - 1) : kMaxNickChars;

    wchar_t temp[kMaxNickChars + 1] = {0};
    size_t len = 0;
    while (*p != L'\0' && *p != L';' && *p != L'\r' && *p != L'\n' && len < maxCopy) {
        wchar_t ch = *p++;
        if (ch < 0x20 || ch == 0x7F) {
            continue;
        }
        temp[len++] = ch;
    }

    while (len > 0) {
        wchar_t tail = temp[len - 1];
        if (tail == L' ' || tail == L'\t' || tail == L'"' || tail == L'\'') {
            temp[--len] = L'\0';
        } else {
            break;
        }
    }
    temp[len] = L'\0';

    if (len == 0) {
        return false;
    }

    if (_wcsicmp(temp, L"UnnamedNewbie") == 0 ||
        _wcsicmp(temp, L"UnnamedPlayer") == 0 ||
        _wcsicmp(temp, L"client_default_name") == 0) {
        return false;
    }

    wcsncpy_s(outBuf, outCap, temp, _TRUNCATE);
    return true;
}

inline bool Kongor_ResolveOfflinePlayerNickname(
    uint32_t accountId,
    bool hasSessionCookie,
    const wchar_t* p2pNicknameCvar,
    const wchar_t* loginNameCvar,
    const wchar_t* trackedFallbackNick,
    wchar_t* outNick,
    size_t outCap,
    KongorOfflineNickSource* outSource = nullptr) noexcept
{
    if (!outNick || outCap < 8) return false;
    outNick[0] = L'\0';

    // Strict Authenticated Online Guard:
    // Never overwrite a Master Server authenticated account nickname.
    if ((accountId != 0u && accountId != 0xFFFFFFFFu) || hasSessionCookie) {
        if (outSource) *outSource = KongorOfflineNickSource::PreservedOnlineAccount;
        return false;
    }

    if (Kongor_SanitizeOfflineNicknameCandidateW(p2pNicknameCvar, outNick, outCap)) {
        if (outSource) *outSource = KongorOfflineNickSource::P2pNicknameCvar;
        return true;
    }
    if (Kongor_SanitizeOfflineNicknameCandidateW(loginNameCvar, outNick, outCap)) {
        if (outSource) *outSource = KongorOfflineNickSource::LoginNameCvar;
        return true;
    }
    if (Kongor_SanitizeOfflineNicknameCandidateW(trackedFallbackNick, outNick, outCap)) {
        if (outSource) *outSource = KongorOfflineNickSource::TrackedConsoleFallback;
        return true;
    }

    wcsncpy_s(outNick, outCap, L"Player", _TRUNCATE);
    if (outSource) *outSource = KongorOfflineNickSource::DefaultPlayerFallback;
    return true;
}

// ============================================================================
// Dedicated Server CLI Parsing, Telemetry Isolation & K2_SERVER RVA Guard
// ============================================================================
static constexpr uint32_t kKongorK2ClientSizeOfImage         = 0x01394000u; // 19,811,840 B client k2_x64.dll
static constexpr uint32_t kKongorK2ServerSizeOfImage         = 0x01342000u; // 19,467,264 B server k2_x64.dll
static constexpr uint32_t kKongorGameSharedClientSizeOfImage = 0x00802000u; //  8,043,008 B client game_shared_x64.dll
static constexpr uint32_t kKongorGameSharedServerSizeOfImage = 0x00803000u; //  8,051,200 B server game_shared_x64.dll

inline bool Kongor_HasCliFlagToken(const char* cmdLine, const char* flagToken) noexcept {
    if (!cmdLine || !flagToken || flagToken[0] == '\0') return false;
    const size_t flagLen = strlen(flagToken);
    const char* p = cmdLine;
    while ((p = strstr(p, flagToken)) != nullptr) {
        const bool validStart = (p == cmdLine) ||
            (p[-1] == ' ' || p[-1] == '\t' || p[-1] == '"' || p[-1] == '\'');
        const char after = p[flagLen];
        const bool validEnd = (after == '\0' || after == ' ' || after == '\t' ||
            after == '"' || after == '\'' || after == ';' || after == '\r' || after == '\n');
        if (validStart && validEnd) {
            return true;
        }
        p += flagLen;
    }
    return false;
}

inline bool Kongor_IsDedicatedServerCommandLine(const char* cmdLine) noexcept {
    return Kongor_HasCliFlagToken(cmdLine, "-dedicated") ||
           Kongor_HasCliFlagToken(cmdLine, "-server");
}

inline void Kongor_ExtractCmdLineCvarValue(
    const char* cmdLine,
    const char* cvarName,
    char* outBuf,
    size_t maxLen) noexcept
{
    if (!cvarName || !outBuf || maxLen == 0) return;
    outBuf[0] = '\0';
    if (!cmdLine) return;

    std::string cmd(cmdLine);
    std::string pattern = std::string("Set ") + cvarName + " ";
    size_t pos = cmd.find(pattern);
    if (pos == std::string::npos) return;
    pos += pattern.size();
    while (pos < cmd.size() && (cmd[pos] == ' ' || cmd[pos] == '"')) ++pos;

    size_t outIdx = 0;
    while (pos < cmd.size() && outIdx + 1 < maxLen) {
        char c = cmd[pos++];
        if (c == ' ' || c == '"' || c == '\'' || c == '+' || c == ';' || c == '\r' || c == '\n') {
            break;
        }
        outBuf[outIdx++] = c;
    }
    outBuf[outIdx] = '\0';
}

inline int Kongor_ResolveDedicatedServerPortFromCommandLine(const char* cmdLine) noexcept {
    // Client processes (even when launched with +Set svr_port <N> for Practice/P2P)
    // MUST NEVER attach to Global\KongorServerTelemetry_<port>.
    if (!Kongor_IsDedicatedServerCommandLine(cmdLine)) {
        return 0;
    }

    std::string cmd(cmdLine);

    // 1. Explicit -port <N> or port:<N>
    size_t portPos = cmd.find("-port ");
    size_t skipLen = 6;
    if (portPos == std::string::npos) {
        portPos = cmd.find("port:");
        skipLen = 5;
    }
    if (portPos != std::string::npos) {
        int port = atoi(cmd.c_str() + portPos + skipLen);
        if (port > 0 && port <= 65535) {
            return port;
        }
    }

    // 2. +Set svr_port <N> / -execute "Set svr_port <N>;..." / svr_port=<N>
    char cvarPortBuf[32] = {};
    Kongor_ExtractCmdLineCvarValue(cmdLine, "svr_port", cvarPortBuf, sizeof(cvarPortBuf));
    if (cvarPortBuf[0] != '\0') {
        int port = atoi(cvarPortBuf);
        if (port > 0 && port <= 65535) {
            return port;
        }
    }
    size_t svrPortPos = cmd.find("svr_port");
    if (svrPortPos != std::string::npos) {
        size_t p = svrPortPos + 8;
        while (p < cmd.size() && (cmd[p] == ' ' || cmd[p] == '=' || cmd[p] == '"')) ++p;
        int port = atoi(cmd.c_str() + p);
        if (port > 0 && port <= 65535) {
            return port;
        }
    }

    // 3. -pipe hon-server-native-<N> or -pipe hon-server-<N>
    size_t pipeNativePos = cmd.find("hon-server-native-");
    if (pipeNativePos != std::string::npos) {
        int port = atoi(cmd.c_str() + pipeNativePos + 18);
        if (port > 0 && port <= 65535) {
            return port;
        }
    }
    size_t pipePos = cmd.find("hon-server-");
    if (pipePos != std::string::npos) {
        int port = atoi(cmd.c_str() + pipePos + 11);
        if (port > 0 && port <= 65535) {
            return port;
        }
    }

    // 4. Dedicated server launched without explicit port defaults to 11235
    return 11235;
}

inline bool Kongor_AllowClientRvaFallback(bool isDedicatedServer, uint32_t peSizeOfImage) noexcept {
    if (isDedicatedServer) {
        return false;
    }
    return (peSizeOfImage == kKongorK2ClientSizeOfImage) ||
           (peSizeOfImage == kKongorGameSharedClientSizeOfImage);
}

// ============================================================================
// Console Command Helpers
// ============================================================================
inline bool Kongor_ExtractMapNameFromConsoleCmdW(
    const wchar_t* cmdLineW,
    char* outMapStem,
    size_t outCap) noexcept
{
    if (!outMapStem || outCap < 2) return false;
    outMapStem[0] = '\0';
    if (!cmdLineW) return false;

    std::string lower;
    lower.reserve(256);
    for (const wchar_t* p = cmdLineW; *p != L'\0'; ++p) {
        wchar_t wc = *p;
        char c = (wc <= 0x7F) ? static_cast<char>(wc) : '?';
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
        lower.push_back(c);
    }

    size_t pos = lower.find("map:");
    size_t skip = 4;
    if (pos == std::string::npos) {
        pos = lower.find("host_map ");
        skip = 9;
    }
    if (pos == std::string::npos) return false;
    pos += skip;
    while (pos < lower.size() && (lower[pos] == ' ' || lower[pos] == '"' || lower[pos] == '\'')) ++pos;

    size_t idx = 0;
    while (pos < lower.size() && idx + 1 < outCap) {
        char c = lower[pos++];
        if (c == ' ' || c == '\t' || c == '"' || c == '\'' || c == ';' || c == '\r' || c == '\n') {
            break;
        }
        outMapStem[idx++] = c;
    }
    outMapStem[idx] = '\0';
    if (idx > 4 && strcmp(outMapStem + idx - 4, ".s2z") == 0) {
        outMapStem[idx - 4] = '\0';
        idx -= 4;
    }
    return idx > 0;
}

// ============================================================================
// Phase 7: Client Dynamic Ephemeral UDP Port Allocator & Bare-Loopback Rewrite
// ============================================================================
static constexpr uint16_t kKongorPracticePortStart        = 11580u;
static constexpr uint16_t kKongorPracticePortEnd          = 11589u;
static constexpr uint16_t kKongorBotMatchPortStart        = 11590u;
static constexpr uint16_t kKongorBotMatchPortEnd          = 11599u;
static constexpr uint16_t kKongorPracticeVoicePortStart   = 12080u;
static constexpr uint16_t kKongorPracticeVoicePortEnd     = 12089u;
static constexpr uint16_t kKongorBotMatchVoicePortStart   = 12090u;
static constexpr uint16_t kKongorBotMatchVoicePortEnd     = 12099u;
static constexpr uint16_t kKongorPracticeFallbackStart    = 12100u;
static constexpr uint16_t kKongorPracticeFallbackEnd      = 12149u;
static constexpr uint16_t kKongorBotMatchFallbackStart    = 12150u;
static constexpr uint16_t kKongorBotMatchFallbackEnd      = 12199u;
static constexpr uint16_t kKongorClientVoiceFallbackStart = 12200u;
static constexpr uint16_t kKongorClientVoiceFallbackEnd   = 12299u;

inline bool Kongor_EnsureWinsockInitialized() noexcept {
    static std::atomic<int> s_wsaState{0}; // 0 = uninit, 1 = initializing, 2 = ready, -1 = failed
    int state = s_wsaState.load(std::memory_order_acquire);
    if (state == 2) return true;
    if (state == -1) return false;

    int expected = 0;
    if (s_wsaState.compare_exchange_strong(expected, 1, std::memory_order_acq_rel)) {
        WSADATA wsa{};
        int rc = WSAStartup(MAKEWORD(2, 2), &wsa);
        s_wsaState.store((rc == 0) ? 2 : -1, std::memory_order_release);
        return (rc == 0);
    }

    for (int spin = 0; spin < 200; ++spin) {
        state = s_wsaState.load(std::memory_order_acquire);
        if (state == 2) return true;
        if (state == -1) return false;
        Sleep(1);
    }
    return (s_wsaState.load(std::memory_order_acquire) == 2);
}

inline bool Kongor_IsUdpPortAvailableOnHost(uint16_t port) noexcept {
    if (port == 0) return false;
    if (!Kongor_EnsureWinsockInitialized()) return false;

    const BOOL excl = TRUE;

    // 1. Check wildcard INADDR_ANY (0.0.0.0) with SO_EXCLUSIVEADDRUSE
    SOCKET sockAny = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sockAny == INVALID_SOCKET) return false;
    setsockopt(sockAny, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
               reinterpret_cast<const char*>(&excl), sizeof(excl));
    sockaddr_in addrAny{};
    addrAny.sin_family = AF_INET;
    addrAny.sin_addr.s_addr = htonl(INADDR_ANY);
    addrAny.sin_port = htons(port);
    const bool okAny = (bind(sockAny, reinterpret_cast<const sockaddr*>(&addrAny), sizeof(addrAny)) == 0);
    closesocket(sockAny);
    if (!okAny) return false;

    // 2. Check explicit loopback (127.0.0.1) with SO_EXCLUSIVEADDRUSE to catch loopback-only listeners
    SOCKET sockLoop = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sockLoop == INVALID_SOCKET) return false;
    setsockopt(sockLoop, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
               reinterpret_cast<const char*>(&excl), sizeof(excl));
    sockaddr_in addrLoop{};
    addrLoop.sin_family = AF_INET;
    addrLoop.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addrLoop.sin_port = htons(port);
    const bool okLoop = (bind(sockLoop, reinterpret_cast<const sockaddr*>(&addrLoop), sizeof(addrLoop)) == 0);
    closesocket(sockLoop);
    return okLoop;
}

inline uint16_t Kongor_SelectAvailableUdpPortInBands(
    uint16_t primaryStart,
    uint16_t primaryEnd,
    uint16_t fallbackStart,
    uint16_t fallbackEnd,
    uint16_t avoidPort = 0) noexcept
{
    if (primaryStart > 0 && primaryEnd >= primaryStart) {
        for (uint32_t p = primaryStart; p <= primaryEnd; ++p) {
            const uint16_t candidate = static_cast<uint16_t>(p);
            if (candidate == avoidPort) continue;
            if (Kongor_IsUdpPortAvailableOnHost(candidate)) {
                return candidate;
            }
        }
    }
    if (fallbackStart > 0 && fallbackEnd >= fallbackStart) {
        for (uint32_t p = fallbackStart; p <= fallbackEnd; ++p) {
            const uint16_t candidate = static_cast<uint16_t>(p);
            if (candidate == avoidPort) continue;
            if (Kongor_IsUdpPortAvailableOnHost(candidate)) {
                return candidate;
            }
        }
    }
    return primaryStart;
}

inline bool Kongor_ContainsAsciiSubstringCaseInsensitiveW(
    const wchar_t* haystackW,
    const char* needleLowerA) noexcept
{
    if (!haystackW || !needleLowerA || needleLowerA[0] == '\0') return false;
    const size_t nLen = strlen(needleLowerA);
    for (const wchar_t* p = haystackW; *p != L'\0'; ++p) {
        size_t i = 0;
        for (; i < nLen; ++i) {
            wchar_t wc = p[i];
            if (wc == L'\0' || wc > 0x7F) break;
            char c = static_cast<char>(wc);
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
            if (c != needleLowerA[i]) break;
        }
        if (i == nLen) return true;
    }
    return false;
}

inline bool Kongor_IsPracticeStartGameCommandW(
    const wchar_t* cmdW,
    bool* outIsBotMatch = nullptr,
    size_t* outStartGameOffset = nullptr,
    bool* outIsReplay = nullptr) noexcept
{
    if (outIsBotMatch) *outIsBotMatch = false;
    if (outStartGameOffset) *outStartGameOffset = 0;
    if (outIsReplay) *outIsReplay = false;
    if (!cmdW || cmdW[0] == L'\0') return false;

    constexpr size_t kStartGameLen = 9;    // "StartGame"
    constexpr size_t kStartReplayLen = 11; // "StartReplay"
    const wchar_t* p = cmdW;
    while (*p != L'\0') {
        while (*p == L' ' || *p == L'\t' || *p == L'\r' || *p == L'\n' || *p == L';') {
            ++p;
        }
        if (*p == L'\0') break;

        if (_wcsnicmp(p, L"StartGame", kStartGameLen) == 0 &&
            (p[kStartGameLen] == L' ' || p[kStartGameLen] == L'\t')) {
            const wchar_t* arg = p + kStartGameLen;
            while (*arg == L' ' || *arg == L'\t' || *arg == L'"' || *arg == L'\'') {
                ++arg;
            }

            bool matchedLocalServerType = false;
            if (_wcsnicmp(arg, L"practice", 8) == 0) {
                const wchar_t after = arg[8];
                matchedLocalServerType = (after == L'\0' || after == L' ' || after == L'\t' ||
                                          after == L'"' || after == L'\'' || after == L';');
            } else if (_wcsnicmp(arg, L"local", 5) == 0) {
                const wchar_t after = arg[5];
                matchedLocalServerType = (after == L'\0' || after == L' ' || after == L'\t' ||
                                          after == L'"' || after == L'\'' || after == L';');
            }

            if (matchedLocalServerType) {
                if (outStartGameOffset) {
                    *outStartGameOffset = static_cast<size_t>(p - cmdW);
                }
                if (outIsBotMatch) {
                    *outIsBotMatch =
                        Kongor_ContainsAsciiSubstringCaseInsensitiveW(p, "localbotsgame") ||
                        Kongor_ContainsAsciiSubstringCaseInsensitiveW(p, "mode:botmatch") ||
                        Kongor_ContainsAsciiSubstringCaseInsensitiveW(p, "randombots:");
                }
                return true;
            }
        } else if (_wcsnicmp(p, L"StartReplay", kStartReplayLen) == 0 &&
                   (p[kStartReplayLen] == L' ' || p[kStartReplayLen] == L'\t')) {
            const wchar_t* arg = p + kStartReplayLen;
            while (*arg == L' ' || *arg == L'\t' || *arg == L'"' || *arg == L'\'') {
                ++arg;
            }
            if (*arg != L'\0' && *arg != L';') {
                if (outStartGameOffset) {
                    *outStartGameOffset = static_cast<size_t>(p - cmdW);
                }
                if (outIsReplay) {
                    *outIsReplay = true;
                }
                return true;
            }
        }

        // Advance to next semicolon-delimited command segment
        while (*p != L'\0' && *p != L';') {
            ++p;
        }
    }
    return false;
}

inline bool Kongor_IsClientLocalSessionEndCommandW(const wchar_t* cmdW) noexcept
{
    if (!cmdW || cmdW[0] == L'\0') return false;
    if (Kongor_ContainsAsciiSubstringCaseInsensitiveW(cmdW, "startgame") ||
        Kongor_ContainsAsciiSubstringCaseInsensitiveW(cmdW, "startreplay")) {
        return false;
    }

    const wchar_t* p = cmdW;
    while (*p != L'\0') {
        while (*p == L' ' || *p == L'\t' || *p == L'\r' || *p == L'\n' || *p == L';') {
            ++p;
        }
        if (*p == L'\0') break;

        if ((_wcsnicmp(p, L"Disconnect", 10) == 0 &&
             (p[10] == L'\0' || p[10] == L' ' || p[10] == L'\t' || p[10] == L';')) ||
            (_wcsnicmp(p, L"StopReplay", 10) == 0 &&
             (p[10] == L'\0' || p[10] == L' ' || p[10] == L'\t' || p[10] == L';'))) {
            return true;
        }

        while (*p != L'\0' && *p != L';') {
            ++p;
        }
    }
    return false;
}

inline bool Kongor_RewriteBareLoopbackConnectTargetW(
    const wchar_t* rawAddrW,
    uint16_t activeLocalSvrPort,
    wchar_t* outRewrittenW,
    size_t outCap) noexcept
{
    if (!outRewrittenW || outCap < 24) return false;
    outRewrittenW[0] = L'\0';
    if (!rawAddrW || activeLocalSvrPort == 0) return false;

    const wchar_t* start = rawAddrW;
    while (*start == L' ' || *start == L'\t' || *start == L'"' || *start == L'\'') {
        ++start;
    }
    if (*start == L'\0') return false;

    // Never rewrite any target that already carries an explicit ':port' or IPv6 colon
    if (wcschr(start, L':') != nullptr) {
        return false;
    }

    const wchar_t* end = start + wcslen(start);
    while (end > start && (end[-1] == L' ' || end[-1] == L'\t' || end[-1] == L'"' || end[-1] == L'\'')) {
        --end;
    }
    const size_t hostLen = static_cast<size_t>(end - start);
    const bool isBareLocalhost = (hostLen == 9 && _wcsnicmp(start, L"localhost", 9) == 0);
    const bool isBareLoopback  = (hostLen == 9 && _wcsnicmp(start, L"127.0.0.1", 9) == 0);
    if (!isBareLocalhost && !isBareLoopback) {
        return false;
    }

    int written = swprintf_s(outRewrittenW, outCap, L"127.0.0.1:%u", static_cast<unsigned int>(activeLocalSvrPort));
    return (written > 0);
}

// ============================================================================
// Wave 5 Release Hardening:
// 1. CSoundManager::LoadSample Negative Sample Cache (k2_x64.dll + 0x9E7260)
// 2. D3D_Draw2D Diffuse Sampler OOB Crash Guard Stub (vid_d3d9_x64.dll + 0x29100)
// 3. NETCMD_RECONNECT_STATUS_RESPONSE (0x6f) Trailing-Int Drain (k2_x64.dll + 0x8CD7C0)
// ============================================================================

inline bool Kongor_IsSoundManagerInitialized(const void* pSoundMgr) noexcept {
    // satisfies: OBL-NG-LOADSAMPLE-02
    if (!pSoundMgr) return false;
    return reinterpret_cast<const uint8_t*>(pSoundMgr)[0x20] != 0;
}

inline bool Kongor_NormalizeSoundCachePathW(
    const wchar_t* rawPathW,
    size_t maxRawChars,
    int32_t iFlags,
    wchar_t* outNormW,
    size_t outCap,
    uint64_t* outHash) noexcept
{
    if (!rawPathW || !outNormW || outCap < 4 || !outHash) return false;
    outNormW[0] = L'\0';
    *outHash = 0;
    if (rawPathW[0] == L'\0') return false;

    uint64_t h = 14695981039346656037ULL;
    size_t len = 0;
    for (; len < maxRawChars && rawPathW[len] != L'\0'; ++len) {
        if (len + 1 >= outCap) {
            outNormW[0] = L'\0';
            return false; // Path exceeds fixed slot capacity; skip cache safely
        }
        wchar_t ch = rawPathW[len];
        if (ch >= L'A' && ch <= L'Z') {
            ch = static_cast<wchar_t>(ch + (L'a' - L'A'));
        } else if (ch == L'\\') {
            ch = L'/';
        }
        outNormW[len] = ch;
        h ^= static_cast<uint64_t>(static_cast<uint16_t>(ch));
        h *= 1099511628211ULL;
    }
    if (len == 0 || (len == maxRawChars && rawPathW[len] != L'\0')) {
        outNormW[0] = L'\0';
        return false;
    }
    outNormW[len] = L'\0';
    h ^= (static_cast<uint64_t>(static_cast<uint32_t>(iFlags)) * 0x9E3779B97F4A7C15ULL);
    h *= 1099511628211ULL;
    if (h == 0) h = 1;
    *outHash = h;
    return true;
}

struct Kongor_SoundNegCacheTable {
    static constexpr size_t kSlotCount = 1024;
    static constexpr size_t kSlotMask  = kSlotCount - 1;
    static constexpr size_t kMaxProbe  = 16;
    static constexpr size_t kMaxPathW  = 192;

    struct Slot {
        uint64_t hash;
        int32_t  flags;
        uint32_t hits;
        wchar_t  normPath[kMaxPathW];
    };

    SRWLOCK lock = SRWLOCK_INIT;
    uint32_t entryCount = 0;
    uint64_t totalHits = 0;
    uint64_t totalInserts = 0;
    uint64_t totalClears = 0;
    Slot slots[kSlotCount] = {};

    bool Lookup(uint64_t hash, int32_t iFlags, const wchar_t* normPathW) noexcept {
        if (hash == 0 || !normPathW || normPathW[0] == L'\0') return false;
        bool matched = false;
        AcquireSRWLockShared(&lock);
        const size_t baseIdx = static_cast<size_t>(hash) & kSlotMask;
        for (size_t probe = 0; probe < kMaxProbe; ++probe) {
            Slot& s = slots[(baseIdx + probe) & kSlotMask];
            if (s.hash == 0) {
                break;
            }
            if (s.hash == hash && s.flags == iFlags && wcscmp(s.normPath, normPathW) == 0) {
                matched = true;
                break;
            }
        }
        ReleaseSRWLockShared(&lock);
        if (matched) {
            InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&totalHits));
        }
        return matched;
    }

    // Returns true if newly inserted (false if already present or sound manager not initialized)
    bool InsertIfInitialized(const void* pSoundMgr, uint64_t hash, int32_t iFlags, const wchar_t* normPathW) noexcept {
        // satisfies: OBL-NG-LOADSAMPLE-02
        if (!Kongor_IsSoundManagerInitialized(pSoundMgr)) return false;
        if (hash == 0 || !normPathW || normPathW[0] == L'\0') return false;

        bool insertedNew = false;
        AcquireSRWLockExclusive(&lock);
        const size_t baseIdx = static_cast<size_t>(hash) & kSlotMask;
        size_t targetIdx = (baseIdx + kMaxProbe - 1) & kSlotMask;
        bool foundExisting = false;
        for (size_t probe = 0; probe < kMaxProbe; ++probe) {
            const size_t idx = (baseIdx + probe) & kSlotMask;
            Slot& s = slots[idx];
            if (s.hash == 0) {
                targetIdx = idx;
                break;
            }
            if (s.hash == hash && s.flags == iFlags && wcscmp(s.normPath, normPathW) == 0) {
                s.hits++;
                foundExisting = true;
                break;
            }
        }
        if (!foundExisting) {
            Slot& dst = slots[targetIdx];
            if (dst.hash == 0) {
                entryCount++;
            }
            dst.hash = hash;
            dst.flags = iFlags;
            dst.hits = 1;
            wcsncpy_s(dst.normPath, kMaxPathW, normPathW, _TRUNCATE);
            totalInserts++;
            insertedNew = true;
        }
        ReleaseSRWLockExclusive(&lock);
        return insertedNew;
    }

    uint32_t Clear() noexcept {
        AcquireSRWLockExclusive(&lock);
        const uint32_t cleared = entryCount;
        memset(slots, 0, sizeof(slots));
        entryCount = 0;
        totalClears++;
        ReleaseSRWLockExclusive(&lock);
        return cleared;
    }
};

inline bool Kongor_ShouldInvalidateSoundNegCacheCommandW(const wchar_t* cmdW) noexcept {
    if (!cmdW || cmdW[0] == L'\0') return false;
    const wchar_t* p = cmdW;
    while (*p != L'\0') {
        while (*p == L' ' || *p == L'\t' || *p == L';' || *p == L'\r' || *p == L'\n') {
            ++p;
        }
        if (*p == L'\0') break;

        struct PrefixEntry { const wchar_t* name; size_t len; };
        static constexpr PrefixEntry kReloadCmds[] = {
            { L"ReloadRes",        9 },
            { L"SoundRestart",    12 },
            { L"SetMod",           6 },
            { L"ReloadInterfaces",16 },
            { L"ResourceCmd",     11 }
        };
        for (const auto& entry : kReloadCmds) {
            if (_wcsnicmp(p, entry.name, entry.len) == 0) {
                const wchar_t after = p[entry.len];
                if (after == L'\0' || after == L' ' || after == L'\t' || after == L';' || after == L'\r' || after == L'\n') {
                    return true;
                }
            }
        }

        while (*p != L'\0' && *p != L';' && *p != L'\n') {
            ++p;
        }
    }
    return false;
}

static constexpr size_t kKongorDraw2dDiffuseStubSize = 70;

inline bool Kongor_BuildDraw2dDiffuseGuardStub(
    uintptr_t vidBaseAddr,
    uintptr_t resumeNormalAddr,
    uintptr_t resumeSkipAddr,
    volatile uint64_t* pSkipCounter,
    uint8_t* outStub,
    size_t stubCap) noexcept
{
    // satisfies: OBL-NG-DRAW2D-02
    if (!outStub || stubCap < kKongorDraw2dDiffuseStubSize ||
        vidBaseAddr == 0 || resumeNormalAddr == 0 || resumeSkipAddr == 0 || !pSkipCounter) {
        return false;
    }

    const uint64_t uVidBase      = static_cast<uint64_t>(vidBaseAddr);
    const uint64_t uResumeNormal = static_cast<uint64_t>(resumeNormalAddr);
    const uint64_t uResumeSkip   = static_cast<uint64_t>(resumeSkipAddr);
    const uint64_t uCounterPtr   = reinterpret_cast<uint64_t>(pSkipCounter);

    size_t i = 0;
    // [0..2] mov esi, r14d
    outStub[i++] = 0x41; outStub[i++] = 0x8B; outStub[i++] = 0xF6;
    // [3..5] mov edi, r15d
    outStub[i++] = 0x41; outStub[i++] = 0x8B; outStub[i++] = 0xFF;
    // [6..15] movabs rdx, uVidBase (relocates RIP-relative lea rdx, [vid_d3d9_base])
    outStub[i++] = 0x48; outStub[i++] = 0xBA;
    memcpy(outStub + i, &uVidBase, 8); i += 8;
    // [16..18] cmp edi, 0x10 (unsigned stage < 16 check; catches 0xFFFFFFFF and >= 16)
    outStub[i++] = 0x83; outStub[i++] = 0xFF; outStub[i++] = 0x10;
    // [19..20] jae +0x12 (jumps to offset 39)
    outStub[i++] = 0x73; outStub[i++] = 0x12;
    // [21..24] imul rbx, rdi, 0x34
    outStub[i++] = 0x48; outStub[i++] = 0x6B; outStub[i++] = 0xDF; outStub[i++] = 0x34;
    // [25..38] jmp qword ptr [rip + 0] -> resumeNormalAddr (vidBase + 0x29111)
    outStub[i++] = 0xFF; outStub[i++] = 0x25;
    outStub[i++] = 0x00; outStub[i++] = 0x00; outStub[i++] = 0x00; outStub[i++] = 0x00;
    memcpy(outStub + i, &uResumeNormal, 8); i += 8;
    // [39..48] movabs rbx, pSkipCounter
    outStub[i++] = 0x48; outStub[i++] = 0xBB;
    memcpy(outStub + i, &uCounterPtr, 8); i += 8;
    // [49..52] lock inc qword ptr [rbx]
    outStub[i++] = 0xF0; outStub[i++] = 0x48; outStub[i++] = 0xFF; outStub[i++] = 0x03;
    // [53..55] xor rbx, rbx
    outStub[i++] = 0x48; outStub[i++] = 0x31; outStub[i++] = 0xDB;
    // [56..69] jmp qword ptr [rip + 0] -> resumeSkipAddr (vidBase + 0x29355)
    outStub[i++] = 0xFF; outStub[i++] = 0x25;
    outStub[i++] = 0x00; outStub[i++] = 0x00; outStub[i++] = 0x00; outStub[i++] = 0x00;
    memcpy(outStub + i, &uResumeSkip, 8); i += 8;

    return (i == kKongorDraw2dDiffuseStubSize);
}

inline bool Kongor_ShouldDrainReconnect6fTrailingInt(const void* pPacket) noexcept {
    if (!pPacket) return false;
    const uint8_t* pkt = reinterpret_cast<const uint8_t*>(pPacket);
    const uint32_t uiEnd       = *reinterpret_cast<const uint32_t*>(pkt + 0x1C);
    const uint32_t uiRead      = *reinterpret_cast<const uint32_t*>(pkt + 0x20);
    const uint8_t  bOverflowed = *reinterpret_cast<const uint8_t*>(pkt + 0x24); // 1-byte bool!
    return (bOverflowed == 0 && uiEnd >= uiRead && (uiEnd - uiRead) == 4 && uiEnd <= 65536U);
}

// ============================================================================
// Owner-Restricted Command Pipe Security Descriptor & Remote SMB Rejection
// ============================================================================
inline constexpr DWORD Kongor_GetCommandPipeModeFlags() noexcept {
    return PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
}

class Kongor_ScopedPipeSecurityDescriptor {
public:
    Kongor_ScopedPipeSecurityDescriptor() noexcept {
        m_sa.nLength = sizeof(SECURITY_ATTRIBUTES);
        m_sa.lpSecurityDescriptor = nullptr;
        m_sa.bInheritHandle = FALSE;

        std::string userSid;
        HANDLE hToken = nullptr;
        if (::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &hToken) && hToken != nullptr) {
            DWORD needed = 0;
            ::GetTokenInformation(hToken, TokenUser, nullptr, 0, &needed);
            if (needed > 0 && needed <= 4096) {
                uint8_t tokenBuf[4096] = {};
                if (::GetTokenInformation(hToken, TokenUser, tokenBuf, needed, &needed)) {
                    const TOKEN_USER* pTokenUser = reinterpret_cast<const TOKEN_USER*>(tokenBuf);
                    LPSTR sidStr = nullptr;
                    if (pTokenUser->User.Sid != nullptr && ::ConvertSidToStringSidA(pTokenUser->User.Sid, &sidStr)) {
                        if (sidStr != nullptr) {
                            userSid.assign(sidStr);
                            ::LocalFree(sidStr);
                        }
                    }
                }
            }
            ::CloseHandle(hToken);
        }

        // Protected DACL (D:P): grant Generic All strictly to LocalSystem (SY),
        // Built-in Administrators (BA), and the current process owner SID (or OW fallback).
        // Never emit a NULL DACL or Everyone (WD) / Anonymous (AN) ACE.
        m_sddl = userSid.empty()
            ? std::string("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)")
            : (std::string("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;") + userSid + ")");

        if (::ConvertStringSecurityDescriptorToSecurityDescriptorA(
                m_sddl.c_str(),
                SDDL_REVISION_1,
                &m_pSd,
                nullptr)) {
            m_sa.lpSecurityDescriptor = m_pSd;
        } else {
            m_pSd = nullptr;
        }
    }

    ~Kongor_ScopedPipeSecurityDescriptor() noexcept {
        if (m_pSd != nullptr) {
            ::LocalFree(m_pSd);
            m_pSd = nullptr;
        }
    }

    Kongor_ScopedPipeSecurityDescriptor(const Kongor_ScopedPipeSecurityDescriptor&) = delete;
    Kongor_ScopedPipeSecurityDescriptor& operator=(const Kongor_ScopedPipeSecurityDescriptor&) = delete;

    bool IsValid() const noexcept { return m_pSd != nullptr; }
    PSECURITY_DESCRIPTOR GetSecurityDescriptor() const noexcept { return m_pSd; }
    SECURITY_ATTRIBUTES* GetSecurityAttributes() noexcept { return &m_sa; }
    const std::string& GetSddl() const noexcept { return m_sddl; }

private:
    PSECURITY_DESCRIPTOR m_pSd = nullptr;
    SECURITY_ATTRIBUTES m_sa{};
    std::string m_sddl;
};

// [L2-P1-04] Allocation-free, constant-time KONGOR_IPC_TOKEN validator.
// Supports raw token payloads, "AUTH <token>" text lines, and {"token":"<token>"} JSON envelopes.
// When expectedLen == 0 (KONGOR_IPC_TOKEN unset/empty), returns true for backward compatibility.
inline bool Kongor_VerifyIpcAuthToken(
    const char* candidatePayload,
    size_t candidateLen,
    const char* expectedToken,
    size_t expectedLen) noexcept
{
    if (!expectedToken || expectedLen == 0) {
        return true;
    }
    if (!candidatePayload || candidateLen == 0) {
        return false;
    }

    size_t start = 0;
    size_t end = candidateLen;
    while (start < end) {
        char c = candidatePayload[start];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\0') {
            ++start;
        } else {
            break;
        }
    }
    while (end > start) {
        char c = candidatePayload[end - 1];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\0') {
            --end;
        } else {
            break;
        }
    }

    const char* tokPtr = nullptr;
    size_t tokLen = 0;

    if (start < end && candidatePayload[start] == '{') {
        // Minimal zero-alloc scan for "token" : "<value>" inside [start, end)
        const char* p = candidatePayload + start;
        const char* lim = candidatePayload + end;
        const char kKey[] = "\"token\"";
        const size_t kKeyLen = sizeof(kKey) - 1;
        const char* foundKey = nullptr;
        for (const char* cur = p; cur + kKeyLen <= lim; ++cur) {
            if (memcmp(cur, kKey, kKeyLen) == 0) {
                foundKey = cur + kKeyLen;
                break;
            }
        }
        if (foundKey != nullptr) {
            const char* colon = foundKey;
            while (colon < lim && (*colon == ' ' || *colon == '\t' || *colon == '\r' || *colon == '\n')) {
                ++colon;
            }
            if (colon < lim && *colon == ':') {
                ++colon;
                while (colon < lim && (*colon == ' ' || *colon == '\t' || *colon == '\r' || *colon == '\n')) {
                    ++colon;
                }
                if (colon < lim && *colon == '"') {
                    ++colon;
                    const char* closeQuote = colon;
                    while (closeQuote < lim && *closeQuote != '"') {
                        if (*closeQuote == '\\' && (closeQuote + 1) < lim) {
                            ++closeQuote;
                        }
                        ++closeQuote;
                    }
                    if (closeQuote < lim && *closeQuote == '"') {
                        tokPtr = colon;
                        tokLen = static_cast<size_t>(closeQuote - colon);
                    }
                }
            }
        }
    } else if ((end - start) >= 5 &&
               (candidatePayload[start] == 'A' || candidatePayload[start] == 'a') &&
               (candidatePayload[start + 1] == 'U' || candidatePayload[start + 1] == 'u') &&
               (candidatePayload[start + 2] == 'T' || candidatePayload[start + 2] == 't') &&
               (candidatePayload[start + 3] == 'H' || candidatePayload[start + 3] == 'h') &&
               (candidatePayload[start + 4] == ' ' || candidatePayload[start + 4] == '\t')) {
        size_t s = start + 5;
        while (s < end && (candidatePayload[s] == ' ' || candidatePayload[s] == '\t')) {
            ++s;
        }
        tokPtr = candidatePayload + s;
        tokLen = end - s;
    } else if (start < end) {
        tokPtr = candidatePayload + start;
        tokLen = end - start;
    }

    volatile uint32_t diff = static_cast<uint32_t>(tokLen ^ expectedLen);
    for (size_t i = 0; i < expectedLen; ++i) {
        unsigned char c = (tokPtr != nullptr && i < tokLen) ? static_cast<unsigned char>(tokPtr[i]) : 0u;
        unsigned char e = static_cast<unsigned char>(expectedToken[i]);
        diff |= static_cast<uint32_t>(c ^ e);
    }
    return (diff == 0u) && (tokPtr != nullptr);
}

// ============================================================================
// Playtest Round 2 Issue #4B: Loader-Lock-Safe Client High-DPI Awareness
// ============================================================================

enum class KongorDpiAwarenessResult : uint8_t {
    SkippedDedicatedServer = 0,
    SkippedHeadless        = 1,
    SkippedSafeMode        = 2,
    SkippedDisabledByEnv   = 3,
    AppliedPerMonitorV2    = 4,
    AppliedPerMonitorV1    = 5,
    AppliedShcorePerMonitor = 6,
    AppliedSystemDpiAware  = 7,
    AlreadySetOrUnavailable = 8
};

inline const char* Kongor_DpiAwarenessResultToString(KongorDpiAwarenessResult res) noexcept {
    switch (res) {
        case KongorDpiAwarenessResult::SkippedDedicatedServer: return "SkippedDedicatedServer";
        case KongorDpiAwarenessResult::SkippedHeadless:        return "SkippedHeadless";
        case KongorDpiAwarenessResult::SkippedSafeMode:        return "SkippedSafeMode";
        case KongorDpiAwarenessResult::SkippedDisabledByEnv:   return "SkippedDisabledByEnv";
        case KongorDpiAwarenessResult::AppliedPerMonitorV2:    return "AppliedPerMonitorV2";
        case KongorDpiAwarenessResult::AppliedPerMonitorV1:    return "AppliedPerMonitorV1";
        case KongorDpiAwarenessResult::AppliedShcorePerMonitor: return "AppliedShcorePerMonitor";
        case KongorDpiAwarenessResult::AppliedSystemDpiAware:  return "AppliedSystemDpiAware";
        case KongorDpiAwarenessResult::AlreadySetOrUnavailable: return "AlreadySetOrUnavailable";
        default: return "Unknown";
    }
}

inline bool Kongor_IsHighDpiDisabledByEnvValue(const char* envVal) noexcept {
    if (!envVal || !*envVal) return false;
    return (_stricmp(envVal, "1") == 0 ||
            _stricmp(envVal, "true") == 0 ||
            _stricmp(envVal, "y") == 0 ||
            _stricmp(envVal, "yes") == 0);
}

inline KongorDpiAwarenessResult Kongor_EnableClientHighDpiAwareness(
    bool isDedicatedServer,
    bool isHeadless,
    bool isSafeMode,
    const char* disableEnvOverride = nullptr,
    char* outDiag = nullptr,
    size_t outDiagCap = 0) noexcept
{
    if (isDedicatedServer) {
        if (outDiag && outDiagCap > 0) {
            snprintf(outDiag, outDiagCap, "[KONGOR NATIVE GUARD] Client DPI awareness: SkippedDedicatedServer");
        }
        return KongorDpiAwarenessResult::SkippedDedicatedServer;
    }
    if (isHeadless) {
        if (outDiag && outDiagCap > 0) {
            snprintf(outDiag, outDiagCap, "[KONGOR NATIVE GUARD] Client DPI awareness: SkippedHeadless");
        }
        return KongorDpiAwarenessResult::SkippedHeadless;
    }
    if (isSafeMode) {
        if (outDiag && outDiagCap > 0) {
            snprintf(outDiag, outDiagCap, "[KONGOR NATIVE GUARD] Client DPI awareness: SkippedSafeMode");
        }
        return KongorDpiAwarenessResult::SkippedSafeMode;
    }

    char envBuf[32] = {0};
    const char* envVal = disableEnvOverride;
    if (!envVal) {
        if (GetEnvironmentVariableA("KONGOR_DISABLE_HIGHDPI", envBuf, sizeof(envBuf)) > 0) {
            envVal = envBuf;
        }
    }
    if (envVal && Kongor_IsHighDpiDisabledByEnvValue(envVal)) {
        if (outDiag && outDiagCap > 0) {
            snprintf(outDiag, outDiagCap, "[KONGOR NATIVE GUARD] Client DPI awareness: SkippedDisabledByEnv");
        }
        return KongorDpiAwarenessResult::SkippedDisabledByEnv;
    }

    KongorDpiAwarenessResult result = KongorDpiAwarenessResult::AlreadySetOrUnavailable;
    const char* modeStr = "AlreadySetOrUnavailable";

    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    if (hUser32) {
        typedef BOOL (WINAPI* PFN_SetProcessDpiAwarenessContext)(HANDLE);
        auto pfnSetContext = reinterpret_cast<PFN_SetProcessDpiAwarenessContext>(
            GetProcAddress(hUser32, "SetProcessDpiAwarenessContext"));
        if (pfnSetContext) {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ((DPI_AWARENESS_CONTEXT)-4)
            HANDLE ctxV2 = reinterpret_cast<HANDLE>(static_cast<intptr_t>(-4));
            if (pfnSetContext(ctxV2)) {
                result = KongorDpiAwarenessResult::AppliedPerMonitorV2;
                modeStr = "AppliedPerMonitorV2";
            } else {
                DWORD err = GetLastError();
                if (err == ERROR_ACCESS_DENIED) {
                    result = KongorDpiAwarenessResult::AlreadySetOrUnavailable;
                    modeStr = "AlreadySetOrUnavailable";
                } else {
                    // Fallback DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = ((DPI_AWARENESS_CONTEXT)-3)
                    HANDLE ctxV1 = reinterpret_cast<HANDLE>(static_cast<intptr_t>(-3));
                    if (pfnSetContext(ctxV1)) {
                        result = KongorDpiAwarenessResult::AppliedPerMonitorV1;
                        modeStr = "AppliedPerMonitorV1";
                    }
                }
            }
        }
    }

    if (result == KongorDpiAwarenessResult::AlreadySetOrUnavailable && GetLastError() != ERROR_ACCESS_DENIED) {
        // Fallback: Check Shcore.dll (NO LoadLibrary! Only GetModuleHandleW)
        HMODULE hShcore = GetModuleHandleW(L"Shcore.dll");
        if (hShcore) {
            typedef HRESULT (WINAPI* PFN_SetProcessDpiAwareness)(int);
            auto pfnSetDpiAwareness = reinterpret_cast<PFN_SetProcessDpiAwareness>(
                GetProcAddress(hShcore, "SetProcessDpiAwareness"));
            if (pfnSetDpiAwareness) {
                // PROCESS_PER_MONITOR_DPI_AWARE = 2
                HRESULT hr = pfnSetDpiAwareness(2);
                if (SUCCEEDED(hr)) {
                    result = KongorDpiAwarenessResult::AppliedShcorePerMonitor;
                    modeStr = "AppliedShcorePerMonitor";
                }
            }
        }
    }

    if (result == KongorDpiAwarenessResult::AlreadySetOrUnavailable && GetLastError() != ERROR_ACCESS_DENIED) {
        // Fallback: SetProcessDPIAware in user32.dll
        if (hUser32) {
            typedef BOOL (WINAPI* PFN_SetProcessDPIAware)(VOID);
            auto pfnSetDPIAware = reinterpret_cast<PFN_SetProcessDPIAware>(
                GetProcAddress(hUser32, "SetProcessDPIAware"));
            if (pfnSetDPIAware && pfnSetDPIAware()) {
                result = KongorDpiAwarenessResult::AppliedSystemDpiAware;
                modeStr = "AppliedSystemDpiAware";
            }
        }
    }

    int cx = GetSystemMetrics(SM_CXSCREEN);
    int cy = GetSystemMetrics(SM_CYSCREEN);
    if (outDiag && outDiagCap > 0) {
        snprintf(outDiag, outDiagCap,
            "[KONGOR NATIVE GUARD] Client DPI awareness: %s (primaryMetrics=%dx%d)",
            modeStr, cx, cy);
    }

    return result;
}

// ============================================================================
// CEntityDirectory UAF Guard Declarations & Helpers (cgame_x64.dll+0xB1D17)
// DOSSIER-CGAME-ENTITY-DIRECTORY-UAF-0xB1D17
// ============================================================================
inline bool Kongor_IsValidDynamicEntityPointerSeh(void* pEntity) noexcept {
    if (!pEntity) return false;
    const uintptr_t addr = reinterpret_cast<uintptr_t>(pEntity);
    if (addr < 0x10000ULL || addr > 0x00007FFFFFFEFFFFULL || (addr & 7) != 0) {
        return false;
    }

    __try {
        void** pVtable = *reinterpret_cast<void***>(pEntity);
        if (!pVtable) return false;
        const uintptr_t vtAddr = reinterpret_cast<uintptr_t>(pVtable);
        if (vtAddr < 0x10000ULL || vtAddr > 0x00007FFFFFFEFFFFULL || (vtAddr & 7) != 0) {
            return false;
        }

        // In PostSnapshot: calls vtable + 0x478
        void* pfn = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(pVtable) + 0x478);
        if (!pfn) return false;
        const uintptr_t fnAddr = reinterpret_cast<uintptr_t>(pfn);
        if (fnAddr < 0x10000ULL || fnAddr > 0x00007FFFFFFEFFFFULL) {
            return false;
        }

        // Probe read the first byte of code at pfn
        volatile uint8_t probeCode = *reinterpret_cast<volatile uint8_t*>(pfn);
        (void)probeCode;

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

inline bool Kongor_IsWritableDynamicListNode(const void* pNode) noexcept {
    if (!pNode) return false;
    const uintptr_t addr = reinterpret_cast<uintptr_t>(pNode);
    if (addr < 0x10000ULL || addr > 0x00007FFFFFFEFFDFULL || (addr & 7) != 0) {
        return false;
    }

    constexpr DWORD kWritableMask = PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(pNode, &mbi, sizeof(mbi)) != sizeof(mbi)) {
        return false;
    }
    if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0 || (mbi.Protect & kWritableMask) == 0) {
        return false;
    }

    // Check 24-byte node tail if straddling a 4 KB page boundary
    const uintptr_t endByte = addr + 23ULL;
    if ((addr & ~0xFFFULL) != (endByte & ~0xFFFULL)) {
        MEMORY_BASIC_INFORMATION mbiTail{};
        if (VirtualQuery(reinterpret_cast<const void*>(endByte), &mbiTail, sizeof(mbiTail)) != sizeof(mbiTail)) {
            return false;
        }
        if (mbiTail.State != MEM_COMMIT || (mbiTail.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0 || (mbiTail.Protect & kWritableMask) == 0) {
            return false;
        }
    }
    return true;
}

inline bool Kongor_TryReadDynamicListNodeSeh(const void* pNode, void** outNext, void** outPrev, void** outEntity) noexcept {
    if (!pNode) return false;
    const uintptr_t addr = reinterpret_cast<uintptr_t>(pNode);
    if (addr < 0x10000ULL || addr > 0x00007FFFFFFEFFDFULL || (addr & 7) != 0) {
        return false;
    }
    __try {
        void* const* fields = reinterpret_cast<void* const*>(pNode);
        *outNext = fields[0];
        *outPrev = fields[1];
        *outEntity = fields[2];
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

inline size_t Kongor_PruneOrphanDynamicEntities(
    void* pDir,
    void (*pfnDeallocate)(void*),
    size_t* outBidirectionalSplices = nullptr) noexcept {
    if (outBidirectionalSplices) *outBidirectionalSplices = 0;
    if (!pDir) return 0;
    uint8_t* pDirBytes = reinterpret_cast<uint8_t*>(pDir);
    size_t prunedCount = 0;
    size_t bidirSplices = 0;

    __try {
        void* pHeadNode = *reinterpret_cast<void**>(pDirBytes + 0x18);
        if (!pHeadNode) return 0;
        uint64_t* pNumEntities = reinterpret_cast<uint64_t*>(pDirBytes + 0x20);

        void* lastValidFwd = pHeadNode;
        void* cur = *reinterpret_cast<void**>(pHeadNode); // pHeadNode->_Next
        size_t validCountFwd = 0;
        size_t safetyLoopCap = 100000;

        while (cur && cur != pHeadNode && --safetyLoopCap > 0) {
            void* next = nullptr;
            void* prev = nullptr;
            void* pEntity = nullptr;
            const bool curReadable = Kongor_TryReadDynamicListNodeSeh(cur, &next, &prev, &pEntity);

            if (curReadable && Kongor_IsValidDynamicEntityPointerSeh(pEntity)) {
                lastValidFwd = cur;
                validCountFwd++;
                cur = next;
                continue;
            }

            // Orphan, clobbered, or unmapped node detected at `cur`!
            // Fast single-node unlink when `cur` is readable AND `next` is a writable list node (`pHeadNode` or readable node)
            void* nextNext = nullptr;
            void* nextPrev = nullptr;
            void* nextEnt = nullptr;
            const bool nextUsable = curReadable && next &&
                (next == pHeadNode || Kongor_TryReadDynamicListNodeSeh(next, &nextNext, &nextPrev, &nextEnt)) &&
                Kongor_IsWritableDynamicListNode(lastValidFwd) &&
                Kongor_IsWritableDynamicListNode(next);

            if (nextUsable) {
                *reinterpret_cast<void**>(lastValidFwd) = next;            // lastValidFwd->_Next = next
                *(reinterpret_cast<void**>(next) + 1) = lastValidFwd;      // next->_Prev = lastValidFwd
                if (pNumEntities && *pNumEntities > 0) {
                    *pNumEntities -= 1;
                }
                // Only invoke CMemManager::Deallocate when `cur` itself is a live writable node with intact backlink
                // (never double-free an already released/clobbered sub-heap page!)
                if (pfnDeallocate && prev == lastValidFwd && Kongor_IsWritableDynamicListNode(cur)) {
                    pfnDeallocate(cur);
                }
                prunedCount++;
                cur = next;
                continue;
            }

            // Bidirectional recovery: `cur` or `cur->_Next` is unmapped/corrupt (e.g. 458dead6.dmp disconnect teardown).
            // Walk backward from `pHeadNode->_Prev` to find the earliest surviving valid node (`firstValidBack`)
            // so we excise ONLY the dead span with ZERO valid-entity loss!
            if (!Kongor_IsWritableDynamicListNode(lastValidFwd) || !Kongor_IsWritableDynamicListNode(pHeadNode)) {
                break;
            }

            void* firstValidBack = pHeadNode;
            size_t validCountBack = 0;
            size_t backCap = 100000;
            void* backCand = *(reinterpret_cast<void**>(pHeadNode) + 1); // pHeadNode->_Prev

            while (backCand && backCand != pHeadNode && backCand != lastValidFwd && backCand != cur && --backCap > 0) {
                if (!Kongor_IsWritableDynamicListNode(backCand)) {
                    break;
                }
                void* bNext = nullptr;
                void* bPrev = nullptr;
                void* bEntity = nullptr;
                if (!Kongor_TryReadDynamicListNodeSeh(backCand, &bNext, &bPrev, &bEntity) ||
                    !Kongor_IsValidDynamicEntityPointerSeh(bEntity)) {
                    break;
                }
                firstValidBack = backCand;
                validCountBack++;
                backCand = bPrev;
            }

            *reinterpret_cast<void**>(lastValidFwd) = firstValidBack;
            *(reinterpret_cast<void**>(firstValidBack) + 1) = lastValidFwd;

            const uint64_t survivingTotal = static_cast<uint64_t>(validCountFwd + validCountBack);
            if (pNumEntities) {
                const uint64_t prevTotal = *pNumEntities;
                const size_t removedInSpan = (prevTotal > survivingTotal)
                    ? static_cast<size_t>(prevTotal - survivingTotal)
                    : 1u;
                *pNumEntities = survivingTotal;
                prunedCount += removedInSpan;
            } else {
                prunedCount++;
            }
            bidirSplices++;
            break;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // Exception caught during traversal, return pruned count so far
    }
    if (outBidirectionalSplices) *outBidirectionalSplices = bidirSplices;
    return prunedCount;
}

inline bool Kongor_IsCGameEntityGuardDisabled() noexcept {
    char envVal[16] = {0};
    DWORD len = GetEnvironmentVariableA("KONGOR_DISABLE_CGAME_ENTITY_GUARD", envVal, sizeof(envVal));
    if (len > 0 && (envVal[0] == '1' || envVal[0] == 't' || envVal[0] == 'T' || envVal[0] == 'y' || envVal[0] == 'Y')) {
        return true;
    }
    return false;
}

// ============================================================================
// Dedicated Server Console Reject Guards (S1: AddBot, S2: StartGame)
// ============================================================================
enum class KongorServerCmdGuardResult : uint8_t {
    Allow = 0,
    RejectAddBotNoWorld = 1,
    RejectStartGameWorldActive = 2
};

inline KongorServerCmdGuardResult Kongor_EvaluateServerCommandGuardW(
    bool isDedicatedServer,
    bool hasActiveGameWorld,
    const wchar_t* cmdLine) noexcept
{
    if (!isDedicatedServer || !cmdLine) return KongorServerCmdGuardResult::Allow;
    while (*cmdLine == L' ' || *cmdLine == L'\t') ++cmdLine;

    if (_wcsnicmp(cmdLine, L"AddBot", 6) == 0 && (cmdLine[6] == L'\0' || cmdLine[6] == L' ' || cmdLine[6] == L'\t')) {
        if (!hasActiveGameWorld) {
            return KongorServerCmdGuardResult::RejectAddBotNoWorld;
        }
    }
    else if (_wcsnicmp(cmdLine, L"StartGame", 9) == 0 && (cmdLine[9] == L'\0' || cmdLine[9] == L' ' || cmdLine[9] == L'\t')) {
        if (hasActiveGameWorld) {
            return KongorServerCmdGuardResult::RejectStartGameWorldActive;
        }
    }
    return KongorServerCmdGuardResult::Allow;
}

// ============================================================================
// Wave 3A: Early-Load Graceful IPC Quit Helpers (< 2s deterministic clean exit)
// ============================================================================
inline bool Kongor_IsQuitConsoleCommandW(const wchar_t* cmdLine) noexcept {
    if (!cmdLine) return false;
    while (*cmdLine == L' ' || *cmdLine == L'\t' || *cmdLine == L'\r' || *cmdLine == L'\n') {
        ++cmdLine;
    }
    if (*cmdLine == L'\0') return false;

    const auto isTerminator = [](wchar_t ch) noexcept {
        return ch == L'\0' || ch == L' ' || ch == L'\t' || ch == L';' || ch == L'\r' || ch == L'\n';
    };

    if (_wcsnicmp(cmdLine, L"quit", 4) == 0 && isTerminator(cmdLine[4])) {
        return true;
    }
    if (_wcsnicmp(cmdLine, L"exit", 4) == 0 && isTerminator(cmdLine[4])) {
        return true;
    }
    return false;
}

inline uint32_t Kongor_ResolveGracefulQuitDeadlineMs(
    const char* envOverrideUtf8,
    uint32_t defaultDeadlineMs = 1250) noexcept {
    constexpr uint32_t kMinDeadlineMs = 150;
    constexpr uint32_t kMaxDeadlineMs = 10000;
    const uint32_t safeDefault = (defaultDeadlineMs < kMinDeadlineMs)
        ? kMinDeadlineMs
        : (defaultDeadlineMs > kMaxDeadlineMs ? kMaxDeadlineMs : defaultDeadlineMs);

    if (!envOverrideUtf8 || *envOverrideUtf8 == '\0') {
        return safeDefault;
    }

    const char* p = envOverrideUtf8;
    while (*p == ' ' || *p == '\t') ++p;
    if (*p < '0' || *p > '9') {
        return safeDefault;
    }

    uint64_t parsed = 0;
    while (*p >= '0' && *p <= '9') {
        if (parsed < kMaxDeadlineMs) {
            parsed = (parsed * 10ULL) + static_cast<uint64_t>(*p - '0');
            if (parsed > kMaxDeadlineMs) {
                parsed = kMaxDeadlineMs;
            }
        }
        ++p;
    }
    while (*p == ' ' || *p == '\t') ++p;
    if (*p != '\0') {
        return safeDefault;
    }
    if (parsed == 0) {
        return safeDefault;
    }
    if (parsed < kMinDeadlineMs) {
        return kMinDeadlineMs;
    }
    if (parsed > kMaxDeadlineMs) {
        return kMaxDeadlineMs;
    }
    return static_cast<uint32_t>(parsed);
}

// ============================================================================
// DbgHelp Re-Entrancy & Lock Test Helpers (S3)
// ============================================================================
bool Kongor_TestDbgHelpLockAcquisition(DWORD maxWaitMs) noexcept;
bool Kongor_TestDbgHelpReentrancyFlag() noexcept;
void Kongor_SetTestDbgHelpReentrancy(bool inDbgHelp) noexcept;

// ============================================================================
// CChatManager Mode Transitions (Wave 2B: 3-State Presence & Invisible Unblock)
// ============================================================================
struct K2WString {
    union {
        wchar_t buf[8];
        const wchar_t* ptr;
    };
    size_t size;
    size_t res;

    K2WString() : size(0), res(7) {
        buf[0] = L'\0';
    }

    K2WString(const wchar_t* s) {
        size = s ? wcslen(s) : 0;
        if (size <= 7) {
            if (s && size > 0) wcsncpy_s(buf, 8, s, size);
            buf[size] = L'\0';
            res = 7;
        } else {
            ptr = s;
            res = size;
        }
    }

    const wchar_t* c_str() const {
        return (res <= 7 || size <= 7) ? buf : ptr;
    }
};
static_assert(sizeof(K2WString) == 32, "K2WString must be exactly 32 bytes");

inline bool Kongor_SetChatMode(
    uint8_t targetMode,
    void* pChatOverride = nullptr,
    void* pfnSetChatModeOverride = nullptr) noexcept
{
    void* pChat = pChatOverride;
    typedef void (__fastcall *FnSetChatMode)(void* pThis, uint8_t mode, const void* sMessage, bool bNotify);
    FnSetChatMode pfnSetChatMode = reinterpret_cast<FnSetChatMode>(pfnSetChatModeOverride);

    if (!pChat || !pfnSetChatMode) {
        HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
        if (!hK2) return false;

        if (!pChat) {
            void*** pppChat = reinterpret_cast<void***>(GetProcAddress(hK2, "?pChatManager@@3PEAVCChatManager@@EA"));
            if (!pppChat || !*pppChat) return false;
            pChat = *pppChat;
        }

        if (!pfnSetChatMode) {
            // CChatManager::SetChatMode RVA 0x6A70F0 in k2_x64.dll
            pfnSetChatMode = reinterpret_cast<FnSetChatMode>(reinterpret_cast<uintptr_t>(hK2) + 0x6A70F0);
        }
    }

    if (!pChat || !pfnSetChatMode) return false;

    Kongor_EnterProtectedSeh();
    __try {
        uint8_t* pMode = reinterpret_cast<uint8_t*>(pChat) + 0x1D5;
        uint8_t oldMode = *pMode;

        // Unblock mode transition out of Invisible (3): line 0x1806A7139: cmp byte ptr [rcx + 0x1d5], 3
        if (oldMode == 3 && targetMode != 3) {
            *pMode = 0;
        }

        K2WString emptyMsg(L"");
        pfnSetChatMode(pChat, targetMode, &emptyMsg, true);

        char buf[256];
        snprintf(buf, sizeof(buf), "[KONGOR CHAT] Kongor_SetChatMode: %u -> %u (pChat=%p)\n", oldMode, targetMode, pChat);
        OutputDebugStringA(buf);
        Kongor_LeaveProtectedSeh();
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        Kongor_LeaveProtectedSeh();
        OutputDebugStringA("[KONGOR CHAT] SEH exception during Kongor_SetChatMode!\n");
        return false;
    }
}

// ============================================================================
// Phase P2.3a Modular Split Cross-TU Contracts
// ============================================================================

#ifdef KONGOR_PLAYTEST_BUILD
constexpr bool kIsPlaytestBuild = true;
#else
constexpr bool kIsPlaytestBuild = false;
#endif

constexpr uint32_t TELEMETRY_RING_CAPACITY = KONGOR_SHM_DEFAULT_CAPACITY;
constexpr uint32_t TELEMETRY_RING_MASK = TELEMETRY_RING_CAPACITY - 1;

constexpr UINT WM_KONGOR_DISPATCH_CMD = WM_USER + 0x1337;
constexpr UINT WM_KONGOR_BG_HUMAN_CLICK = WM_USER + 0x1338;

#pragma pack(push, 8)
struct K2StringW {
    union {
        wchar_t buf[8];
        const wchar_t* ptr;
    } bx;
    size_t length;
    size_t capacity;

    K2StringW(const wchar_t* s = nullptr) {
        length = s ? wcslen(s) : 0;
        if (length < 8) {
            if (s && length > 0) {
                wcsncpy_s(bx.buf, 8, s, length);
            }
            bx.buf[length] = L'\0';
            capacity = 7;
        } else {
            bx.ptr = s;
            capacity = length;
        }
    }
};
#pragma pack(pop)
static_assert(sizeof(K2StringW) == 32, "K2StringW must be exactly 32 bytes");

// Forward Declarations & Helpers
void DispatchCommandToEngine(const std::wstring& wcmd, int64_t qpcTimestamp = 0, uint32_t sequence = 0);
void WriteSentinelLine(const char* line);
void WriteSentinelLineNoAllocBestEffort(const char* line) noexcept;
void FlushSentinelLogs(bool tryOnly = false);
void MarkQuitInitiated(const char* reason) noexcept;
void MarkCleanUserExit(const char* reason) noexcept;
void RevokeCleanExitSentinelsNoAlloc() noexcept;
void ShutdownGracefulQuitWatchdog(bool isProcessTermination) noexcept;
void InitSessionCrashSentinelDirs(const std::vector<std::filesystem::path>& uniqueCrashDirs, bool trackForegroundSession) noexcept;
void WritePerPidCrashSentinelNoAlloc(const char* reason, int exitCode, const char* detail, const char* stackRvas) noexcept;
void FormatStackBackTraceRvasNoAlloc(char* outBuf, size_t outCap, ULONG skipFrames) noexcept;
bool Kongor_IsUnrecoverableFatalExceptionCode(DWORD code) noexcept;
bool Kongor_IsShutdownFatalExceptionCode(DWORD code) noexcept;
void Kongor_WarmRtlDllShutdownInProgressCache() noexcept;
bool Kongor_IsRtlDllShutdownInProgress() noexcept;

// Telemetry & Keyframe
void InitializeTelemetrySharedMemory();
void ShutdownTelemetrySharedMemory();
void CleanupServerKeyframeMap();
void WriteServerKeyframe(int port);
void SetKeyframePlayerCoordinate(int slot, int team, const char* hero, const char* name, float x, float y, float z, float health, float maxHealth, float mana, float maxMana, int level, float heading, bool isAlive);
void ClearKeyframePlayerCoordinates();
void ClearKeyframePlayerSlot(int slot);
uint32_t ComputeCrc32Native(const uint8_t* data, size_t length);
void RefreshServerKeyframePlayersFromEngine() noexcept;
uint64_t GetLastHeroKeyframeSampleTickMs() noexcept;
void CheckMainThreadWatchdogTick();
int32_t ReadCurrentHostFrameSafe();
uint64_t UpdateAndGetMsSinceHostFrameAdvance(int32_t currentHostFrame);
const char* GetCanonicalGamePhaseName(uint32_t phase);
void UpdateTrackedOfflineNickFromNarrow(const char* narrowVal) noexcept;

// Threads & Workers
DWORD WINAPI NativeCommandPipeWorker(LPVOID lpParam);
DWORD WINAPI GameSharedPatcherWorker(LPVOID);
DWORD WINAPI NativeGuardWindowHookWatcher(LPVOID);
DWORD WINAPI NativeGuardTelemetrySamplerWorker(LPVOID lpParam);

// Hook & Subsystem Installers
extern "C" bool InstallK2SafeHooks(HMODULE hK2);
extern "C" bool InstallConsoleHooks(HMODULE hK2);
void InstallCursorAndConfigIATHooks();
void InstallRawInputIATHooks();
bool InstallCGameEntityDirectoryHooks(HMODULE hCGame);
LPTOP_LEVEL_EXCEPTION_FILTER WINAPI Hooked_SetUnhandledExceptionFilter(LPTOP_LEVEL_EXCEPTION_FILTER lpTopLevelExceptionFilter);
BOOL WINAPI Hooked_IsDebuggerPresent();
BOOL WINAPI Hooked_CheckRemoteDebuggerPresent(HANDLE hProcess, PBOOL pbDebuggerPresent);
extern void* g_origSetUnhandledExceptionFilter;

// String / Json / Eval Helpers
std::wstring Utf8ToWide(const std::string& str);
std::string WideToUtf8(const std::wstring& wstr);
std::string EscapeJsonString(const std::string& input);
std::string BuildEvalRunnerCommandUtf8(uint64_t evalId, uint32_t nativeGamePhase);
std::string BuildDispatchUiEventCommandUtf8(const std::string& payloadStr, std::string* outIfaceName = nullptr, std::string* outWidgetName = nullptr, std::string* outEventType = nullptr);
std::string ExtractJsonUiField(const std::string& jsonStr, const char* key);
std::string EncodeK2ConsoleSafeLuaStringExpr(const std::string& s);
bool ParseEvalResCommandArgsW(const wchar_t* pCmdArgs, uint64_t* outEvalId, bool* outOk, std::string* outDecodedUtf8);
uint32_t GetCurrentNativeGamePhaseForLuaShim();

// Process / Module Helpers
inline uint32_t GetModuleSizeOfImage(HMODULE hMod) noexcept {
    if (!hMod) return 0;
    const auto* pDos = reinterpret_cast<const IMAGE_DOS_HEADER*>(hMod);
    if (pDos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    const auto* pNt = reinterpret_cast<const IMAGE_NT_HEADERS*>(
        reinterpret_cast<const uint8_t*>(hMod) + pDos->e_lfanew);
    return (pNt->Signature == IMAGE_NT_SIGNATURE) ? pNt->OptionalHeader.SizeOfImage : 0;
}

inline bool IsDedicatedServerProcessCached() noexcept {
    static int s_isDedicated = -1;
    if (s_isDedicated == -1) {
        s_isDedicated = Kongor_IsDedicatedServerCommandLine(GetCommandLineA()) ? 1 : 0;
    }
    return s_isDedicated == 1;
}

inline bool IsK2ClientModule(HMODULE hK2) noexcept {
    return Kongor_AllowClientRvaFallback(IsDedicatedServerProcessCached(), GetModuleSizeOfImage(hK2));
}

inline bool IsGameSharedClientModule(HMODULE hMod) noexcept {
    return Kongor_AllowClientRvaFallback(IsDedicatedServerProcessCached(), GetModuleSizeOfImage(hMod));
}

inline int ResolveDedicatedServerPortFromCommandLine(const std::string& cmdLine) {
    return Kongor_ResolveDedicatedServerPortFromCommandLine(cmdLine.c_str());
}

inline void* ResolveExport(HMODULE hMod, const char* name, DWORD rvaFallback) {
    if (!hMod) return nullptr;
    FARPROC proc = GetProcAddress(hMod, name);
    if (proc) return (void*)proc;

    // Fallback: manual PE export parser
    auto pDos = reinterpret_cast<PIMAGE_DOS_HEADER>(hMod);
    if (pDos->e_magic == IMAGE_DOS_SIGNATURE) {
        auto pNt = reinterpret_cast<PIMAGE_NT_HEADERS>(
            reinterpret_cast<uint8_t*>(hMod) + pDos->e_lfanew);
        if (pNt->Signature == IMAGE_NT_SIGNATURE) {
            IMAGE_DATA_DIRECTORY dir = pNt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
            if (dir.VirtualAddress && dir.Size) {
                auto pExp = reinterpret_cast<PIMAGE_EXPORT_DIRECTORY>(
                    reinterpret_cast<uint8_t*>(hMod) + dir.VirtualAddress);
                auto pNames = reinterpret_cast<const uint32_t*>(
                    reinterpret_cast<uint8_t*>(hMod) + pExp->AddressOfNames);
                auto pOrdinals = reinterpret_cast<const uint16_t*>(
                    reinterpret_cast<uint8_t*>(hMod) + pExp->AddressOfNameOrdinals);
                auto pFuncs = reinterpret_cast<const uint32_t*>(
                    reinterpret_cast<uint8_t*>(hMod) + pExp->AddressOfFunctions);

                for (uint32_t i = 0; i < pExp->NumberOfNames; ++i) {
                    const char* expName = reinterpret_cast<const char*>(
                        reinterpret_cast<uint8_t*>(hMod) + pNames[i]);
                    if (strcmp(expName, name) == 0) {
                        uint16_t ord = pOrdinals[i];
                        return reinterpret_cast<void*>(
                            reinterpret_cast<uint8_t*>(hMod) + pFuncs[ord]);
                    }
                }
            }
        }
    }

    if (rvaFallback && Kongor_AllowClientRvaFallback(IsDedicatedServerProcessCached(), GetModuleSizeOfImage(hMod))) {
        return reinterpret_cast<void*>(reinterpret_cast<uint8_t*>(hMod) + rvaFallback);
    }
    return nullptr;
}

typedef int (*FnCountMemoryUsed)(void* pThis);
extern void** g_ppLuaScriptManager;
extern FnCountMemoryUsed g_pfnCountMemoryUsed;
bool ResolveK2LuaSymbols();

// Cross-TU Shared Globals
extern HWND g_hGameWnd;
extern HMODULE g_hSelfModule;
extern bool g_isHeadlessProcess;
extern volatile bool g_pipeServerRunning;
extern HANDLE g_hPipeServerThread;
extern std::atomic<bool> g_rawInputEnabled;
extern std::atomic<bool> g_rawInputRegistered;
extern std::atomic<uint64_t> g_rawPacketsProcessed;
extern std::atomic<uint64_t> g_mouseMoveSuppressedCount;
extern std::atomic<float> g_mouseSensitivity;
extern std::atomic<uint64_t> g_voicePttFlags;
extern std::atomic<uint64_t> g_lastMainThreadHeartbeatTick;
extern std::atomic<bool> g_mainThreadBusy;
extern std::atomic<uint64_t> g_activeMainThreadOpStartTick;
extern std::atomic<int> g_lastKnownGamePhase;
extern std::atomic<uint64_t> g_cachedLuaMemoryBytes;
extern std::atomic<uint64_t> g_fullGcCount;
extern KongorSharedMemHandle g_shmHandle;
extern KongorTelemetryRingHeaderV2* g_pTelemetryHeader;
extern FrameTelemetryRecord* g_pTelemetryRecords;
extern LARGE_INTEGER g_qpcFrequency;
extern LARGE_INTEGER g_qpcProcessStart;
extern CRITICAL_SECTION g_cursorLock;
extern Kongor::CursorStateMachine g_cursorStateMachine;
extern std::atomic<bool> g_currentlyConfined;
extern BOOL (WINAPI *g_pfnRealClipCursor)(const RECT*);
extern HANDLE (WINAPI *g_pfnRealCreateFileW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
extern int g_resolvedDedicatedServerPort;
extern std::atomic<uint64_t> g_telemetryHitchCount;
extern std::atomic<uint64_t> g_telemetryCpuStallCount;
extern std::atomic<uint32_t> g_telemetryPeakFrameTimeUs;
extern std::atomic<uint32_t> g_dynamicActiveEntities;
extern std::atomic<uint32_t> g_dynamicServerTick;
extern std::atomic<float>    g_dynamicInputLatencyMs;
extern std::atomic<uint32_t> g_dynamicDrawCalls;
extern std::atomic<uint32_t> g_dynamicParticlesAlive;
extern std::atomic<uint32_t> g_dynamicPingMicroseconds;
extern std::atomic<float>    g_dynamicPacketLossPercent;
extern std::atomic<uint32_t> g_dynamicAudioVoices;
extern std::atomic<int32_t> g_rawAccumulatorX;
extern std::atomic<int32_t> g_rawAccumulatorY;
extern wchar_t g_wszTrackedOfflineNick[32];
extern std::atomic<bool> g_userInitiatedCleanExit;
extern std::atomic<bool> g_quitInitiated;
extern std::atomic<uint64_t> g_quitInitiatedTickMs;
extern std::atomic<bool> g_shutdownStarted;
extern std::atomic<bool> g_csystemErrorTriggered;
extern wchar_t g_crashSentinelDirsW[2][MAX_PATH];
extern size_t g_crashSentinelDirCount;
extern std::atomic<uint64_t> g_killPacketClampCount;
extern std::atomic<uint64_t> g_killPacketExpandCount;
extern std::atomic<uint64_t> g_replayKillReadClampCount;
extern std::atomic<uint64_t> g_snapshotDesyncDropCount;
extern void** g_ppHost;

// ============================================================================
// H2-4: K2 CScrollbarSlider::MouseDown VMT Slot 32 Guard & Track Click-to-Jump
// Evidence Dossier: artifacts/evidence/k2_scrollbar_slider_vmt_hook.json
// ============================================================================
namespace Kongor {

typedef void (__fastcall *FnK2SliderMouseDown)(void* thisPtr, int eButton, const void* cursorPos);

constexpr uint32_t kScrollbarSliderVmtSlotRva   = 0x00F52D38u; // CScrollbarSlider vtable (0xF52C38) + slot 32 (+0x100)
constexpr uint32_t kScrollbarSliderMouseDownRva = 0x009A6BB0u; // Retail CScrollbarSlider::MouseDown
constexpr uint32_t kCSliderMouseDownRva         = 0x009C9D80u; // Retail CSlider::MouseDown
constexpr int      kK2ButtonMouseLeft           = 0x137;       // BUTTON_MOUSEL
constexpr size_t   kCSliderHandleFieldOffset    = 0x190u;      // CSlider::m_pHandle (CSliderHandle*)

inline constexpr uint8_t kScrollbarSliderMouseDownPrologue15[15] = {
    0x48, 0x89, 0x5C, 0x24, 0x08, // MOV [RSP+0x08], RBX
    0x48, 0x89, 0x74, 0x24, 0x10, // MOV [RSP+0x10], RSI
    0x57,                         // PUSH RDI
    0x48, 0x83, 0xEC, 0x20        // SUB RSP, 0x20
};

inline constexpr uint8_t kCSliderMouseDownPrologue12[12] = {
    0x81, 0xFA, 0x37, 0x01, 0x00, 0x00, // CMP EDX, 0x137
    0x0F, 0x85, 0x2F, 0x01, 0x00, 0x00  // JNZ +0x12F
};

enum class ScrollbarVmtInstallStatus : uint8_t {
    Installed             = 0,
    AlreadyInstalled      = 1,
    InvalidArguments      = 2,
    SlotTargetMismatch    = 3,
    OrigPrologueMismatch  = 4,
    BasePrologueMismatch  = 5,
    VirtualProtectFailed  = 6
};

inline ScrollbarVmtInstallStatus InstallScrollbarSliderVmtHook(
    void** ppSlot,
    const void* pExpectedOrigFn,
    const void* pCSliderMouseDownFn,
    void* pHookFn,
    FnK2SliderMouseDown* inoutOrigFn) noexcept
{
    if (!ppSlot || !pExpectedOrigFn || !pCSliderMouseDownFn || !pHookFn || !inoutOrigFn) {
        return ScrollbarVmtInstallStatus::InvalidArguments;
    }

    void* currentSlotTarget = *ppSlot;
    if (currentSlotTarget == pHookFn) {
        if (*inoutOrigFn == nullptr) {
            *inoutOrigFn = reinterpret_cast<FnK2SliderMouseDown>(const_cast<void*>(pExpectedOrigFn));
        }
        return ScrollbarVmtInstallStatus::AlreadyInstalled;
    }

    if (currentSlotTarget != pExpectedOrigFn) {
        return ScrollbarVmtInstallStatus::SlotTargetMismatch;
    }

    if (memcmp(pExpectedOrigFn, kScrollbarSliderMouseDownPrologue15, sizeof(kScrollbarSliderMouseDownPrologue15)) != 0) {
        return ScrollbarVmtInstallStatus::OrigPrologueMismatch;
    }

    if (memcmp(pCSliderMouseDownFn, kCSliderMouseDownPrologue12, sizeof(kCSliderMouseDownPrologue12)) != 0) {
        return ScrollbarVmtInstallStatus::BasePrologueMismatch;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(ppSlot, sizeof(void*), PAGE_READWRITE, &oldProtect)) {
        return ScrollbarVmtInstallStatus::VirtualProtectFailed;
    }

    *inoutOrigFn = reinterpret_cast<FnK2SliderMouseDown>(currentSlotTarget);
    *ppSlot = pHookFn;

    DWORD restoreDummy = 0;
    VirtualProtect(ppSlot, sizeof(void*), oldProtect, &restoreDummy);
    return ScrollbarVmtInstallStatus::Installed;
}

inline void DispatchScrollbarSliderMouseDown(
    void* thisPtr,
    int eButton,
    const void* cursorPos,
    FnK2SliderMouseDown pfnCSliderMouseDown,
    FnK2SliderMouseDown pfnOrigScrollbarSliderMouseDown) noexcept
{
    if (!thisPtr) return;
    Kongor_EnterProtectedSeh();
    __try {
        if (eButton == kK2ButtonMouseLeft && pfnCSliderMouseDown && cursorPos) {
            void* const pHandle = *reinterpret_cast<void* const*>(
                static_cast<const uint8_t*>(thisPtr) + kCSliderHandleFieldOffset);
            if (pHandle != nullptr) {
                pfnCSliderMouseDown(thisPtr, eButton, cursorPos);
                Kongor_LeaveProtectedSeh();
                return;
            }
        }

        if (pfnOrigScrollbarSliderMouseDown) {
            pfnOrigScrollbarSliderMouseDown(thisPtr, eButton, cursorPos);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Exception neutralized inside Hooked_CScrollbarSlider_MouseDown.\n");
    }
    Kongor_LeaveProtectedSeh();
}

} // namespace Kongor

