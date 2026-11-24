// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - VEH Crash Shield, Zero-Alloc Minidump Writer & Dynamic AOB
// Extracted Translation Unit (Option C2 & Option S)
// ============================================================================
#include "native_guard_internal.h"
#include "runtime_patch_controller.h"
#include "kongor_sstream.h"
#include "kongor_stacktrace.h"
#include <dbghelp.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <vector>

static thread_local int g_inProtectedSehDepth = 0;

// ============================================================================
// PER-PID FOREGROUND SESSION HANDSHAKE & LOCK-FREE EXIT DIAGNOSTICS (WS3A/WS3B)
// ============================================================================
wchar_t g_crashSentinelDirsW[2][MAX_PATH] = { {0}, {0} };
size_t g_crashSentinelDirCount = 0;
static std::atomic<bool> g_sessionSentinelsActive{ false };
std::atomic<bool> g_userInitiatedCleanExit{ false };
std::atomic<bool> g_quitInitiated{ false };
std::atomic<uint64_t> g_quitInitiatedTickMs{ 0 };
std::atomic<bool> g_shutdownStarted{ false };
static std::atomic<uintptr_t> g_pfnRtlDllShutdownInProgressCache{ 0 };

void Kongor_WarmRtlDllShutdownInProgressCache() noexcept {
    if (g_pfnRtlDllShutdownInProgressCache.load(std::memory_order_acquire) != 0u) {
        return;
    }
    uintptr_t resolved = 1u;
    if (HMODULE hNtdll = ::GetModuleHandleW(L"ntdll.dll")) {
        if (FARPROC proc = ::GetProcAddress(hNtdll, "RtlDllShutdownInProgress")) {
            resolved = reinterpret_cast<uintptr_t>(proc);
        }
    }
    g_pfnRtlDllShutdownInProgressCache.store(resolved, std::memory_order_release);
}

bool Kongor_IsRtlDllShutdownInProgress() noexcept {
    uintptr_t cached = g_pfnRtlDllShutdownInProgressCache.load(std::memory_order_acquire);
    if (cached == 0u) {
        Kongor_WarmRtlDllShutdownInProgressCache();
        cached = g_pfnRtlDllShutdownInProgressCache.load(std::memory_order_acquire);
    }
    if (cached <= 1u) {
        return false;
    }
    using PfnRtlDllShutdownInProgress = BOOLEAN (NTAPI*)(VOID);
    return reinterpret_cast<PfnRtlDllShutdownInProgress>(cached)() != FALSE;
}

static void AppendSentinelLineDirectWin32W(const wchar_t* fullPathW, const char* payload, size_t payloadLen) noexcept {
    if (!fullPathW || !*fullPathW || !payload || payloadLen == 0) return;
    HANDLE hFile = ::CreateFileW(
        fullPathW,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        ::WriteFile(hFile, payload, static_cast<DWORD>(payloadLen), &written, nullptr);
        ::FlushFileBuffers(hFile);
        ::CloseHandle(hFile);
    }
}

void WriteSentinelLineNoAllocBestEffort(const char* line) noexcept {
    if (!line || !*line) return;
    OutputDebugStringA(line);
    OutputDebugStringA("\n");

    SYSTEMTIME st = {};
    ::GetLocalTime(&st);
    char formatted[512] = {0};
    int len = snprintf(
        formatted,
        sizeof(formatted),
        "[%04u-%02u-%02u %02u:%02u:%02u.%03u] %s\r\n",
        static_cast<unsigned>(st.wYear),
        static_cast<unsigned>(st.wMonth),
        static_cast<unsigned>(st.wDay),
        static_cast<unsigned>(st.wHour),
        static_cast<unsigned>(st.wMinute),
        static_cast<unsigned>(st.wSecond),
        static_cast<unsigned>(st.wMilliseconds),
        line);
    if (len <= 0) return;
    const size_t writeLen = (static_cast<size_t>(len) >= sizeof(formatted))
        ? (sizeof(formatted) - 1)
        : static_cast<size_t>(len);

    if (g_crashSentinelDirCount > 0) {
        for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
            wchar_t logPath[MAX_PATH] = {0};
            swprintf_s(logPath, _countof(logPath), L"%ls\\native_guard_sentinel.log", g_crashSentinelDirsW[i]);
            AppendSentinelLineDirectWin32W(logPath, formatted, writeLen);
        }
    } else {
        AppendSentinelLineDirectWin32W(L"logs\\crash\\native_guard_sentinel.log", formatted, writeLen);
        AppendSentinelLineDirectWin32W(L"userdata\\logs\\crash\\native_guard_sentinel.log", formatted, writeLen);
    }
}

static void WriteSentinelFileNoAllocW(const wchar_t* fullPathW, const char* payload, size_t payloadLen) noexcept {
    if (!fullPathW || !*fullPathW || !payload || payloadLen == 0) return;
    HANDLE hFile = ::CreateFileW(
        fullPathW,
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        ::WriteFile(hFile, payload, static_cast<DWORD>(payloadLen), &written, nullptr);
        ::FlushFileBuffers(hFile);
        ::CloseHandle(hFile);
    }
}

void InitSessionCrashSentinelDirs(const std::vector<std::filesystem::path>& uniqueCrashDirs, bool trackForegroundSession) noexcept {
    g_crashSentinelDirCount = 0;
    for (const auto& dir : uniqueCrashDirs) {
        if (g_crashSentinelDirCount >= 2) break;
        const std::wstring wdir = dir.wstring();
        if (!wdir.empty() && wdir.size() + 48 < MAX_PATH) {
            wcsncpy_s(g_crashSentinelDirsW[g_crashSentinelDirCount], MAX_PATH, wdir.c_str(), _TRUNCATE);
            ++g_crashSentinelDirCount;
        }
    }

    if (!trackForegroundSession || g_crashSentinelDirCount == 0) {
        g_sessionSentinelsActive.store(false, std::memory_order_release);
        return;
    }

    const DWORD pid = ::GetCurrentProcessId();
    SYSTEMTIME stLocal = {};
    ::GetLocalTime(&stLocal);
    char payload[192] = {0};
    int len = snprintf(
        payload,
        sizeof(payload),
        "PID=%lu\nStartLocal=%04u-%02u-%02u %02u:%02u:%02u.%03u\n",
        static_cast<unsigned long>(pid),
        static_cast<unsigned>(stLocal.wYear),
        static_cast<unsigned>(stLocal.wMonth),
        static_cast<unsigned>(stLocal.wDay),
        static_cast<unsigned>(stLocal.wHour),
        static_cast<unsigned>(stLocal.wMinute),
        static_cast<unsigned>(stLocal.wSecond),
        static_cast<unsigned>(stLocal.wMilliseconds));
    if (len <= 0) return;

    for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
        wchar_t activePath[MAX_PATH] = {0};
        swprintf_s(activePath, _countof(activePath), L"%ls\\session-active.%lu.sentinel",
                   g_crashSentinelDirsW[i], static_cast<unsigned long>(pid));
        WriteSentinelFileNoAllocW(activePath, payload, static_cast<size_t>(len));
    }
    g_sessionSentinelsActive.store(true, std::memory_order_release);
}

void MarkQuitInitiated(const char* reason) noexcept {
    const uint64_t nowTick = ::GetTickCount64();
    uint64_t expectedZero = 0;
    g_quitInitiatedTickMs.compare_exchange_strong(expectedZero, nowTick, std::memory_order_acq_rel);
    g_quitInitiated.store(true, std::memory_order_release);

    if (!g_sessionSentinelsActive.load(std::memory_order_acquire) || g_crashSentinelDirCount == 0) {
        return;
    }

    const DWORD pid = ::GetCurrentProcessId();
    SYSTEMTIME stUtc = {};
    ::GetSystemTime(&stUtc);
    char payload[320] = {0};
    int len = snprintf(
        payload,
        sizeof(payload),
        "PID=%lu\nStartUtc=%04u-%02u-%02uT%02u:%02u:%02u.%03uZ\nTickMs=%llu\nReason=%s\n",
        static_cast<unsigned long>(pid),
        static_cast<unsigned>(stUtc.wYear),
        static_cast<unsigned>(stUtc.wMonth),
        static_cast<unsigned>(stUtc.wDay),
        static_cast<unsigned>(stUtc.wHour),
        static_cast<unsigned>(stUtc.wMinute),
        static_cast<unsigned>(stUtc.wSecond),
        static_cast<unsigned>(stUtc.wMilliseconds),
        static_cast<unsigned long long>(nowTick),
        reason ? reason : "user_quit");
    if (len <= 0) return;
    if (static_cast<size_t>(len) >= sizeof(payload)) {
        len = static_cast<int>(sizeof(payload) - 1);
    }

    // Write quit-initiated.<pid>.sentinel while KEEPING session-active.<pid>.sentinel on disk
    // until CHost::Shutdown finishes cleanly and calls MarkCleanUserExit().
    for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
        wchar_t quitPath[MAX_PATH] = {0};
        swprintf_s(quitPath, _countof(quitPath), L"%ls\\quit-initiated.%lu.sentinel",
                   g_crashSentinelDirsW[i], static_cast<unsigned long>(pid));
        WriteSentinelFileNoAllocW(quitPath, payload, static_cast<size_t>(len));
    }
}

void MarkCleanUserExit(const char* reason) noexcept {
    g_userInitiatedCleanExit.store(true, std::memory_order_release);
    if (!g_sessionSentinelsActive.load(std::memory_order_acquire)) {
        return;
    }

    const DWORD pid = ::GetCurrentProcessId();
    char payload[192] = {0};
    int len = snprintf(payload, sizeof(payload), "PID=%lu\nReason=%s\n",
                       static_cast<unsigned long>(pid), reason ? reason : "user_quit");
    if (len <= 0) return;

    // Write clean-exit.<pid>.sentinel and delete session-active.<pid>.sentinel.
    // Intentionally leave quit-initiated.<pid>.sentinel on disk until WILLOWMAKER observes exit so
    // the out-of-process shutdown-hang supervisor can still catch a post-CHost::Shutdown LdrShutdownProcess hang.
    for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
        wchar_t cleanPath[MAX_PATH] = {0};
        wchar_t activePath[MAX_PATH] = {0};
        swprintf_s(cleanPath, _countof(cleanPath), L"%ls\\clean-exit.%lu.sentinel",
                   g_crashSentinelDirsW[i], static_cast<unsigned long>(pid));
        swprintf_s(activePath, _countof(activePath), L"%ls\\session-active.%lu.sentinel",
                   g_crashSentinelDirsW[i], static_cast<unsigned long>(pid));
        WriteSentinelFileNoAllocW(cleanPath, payload, static_cast<size_t>(len));
        ::DeleteFileW(activePath);
    }
}

void RevokeCleanExitSentinelsNoAlloc() noexcept {
    g_userInitiatedCleanExit.store(false, std::memory_order_release);
    const DWORD pid = ::GetCurrentProcessId();
    if (g_crashSentinelDirCount > 0) {
        for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
            wchar_t cleanPath[MAX_PATH] = {0};
            wchar_t legacyCleanPath[MAX_PATH] = {0};
            swprintf_s(cleanPath, _countof(cleanPath), L"%ls\\clean-exit.%lu.sentinel",
                       g_crashSentinelDirsW[i], static_cast<unsigned long>(pid));
            swprintf_s(legacyCleanPath, _countof(legacyCleanPath), L"%ls\\clean-exit.sentinel",
                       g_crashSentinelDirsW[i]);
            ::DeleteFileW(cleanPath);
            ::DeleteFileW(legacyCleanPath);
        }
    } else {
        wchar_t relCleanPath[MAX_PATH] = {0};
        swprintf_s(relCleanPath, _countof(relCleanPath), L"logs\\crash\\clean-exit.%lu.sentinel",
                   static_cast<unsigned long>(pid));
        ::DeleteFileW(relCleanPath);
        ::DeleteFileW(L"logs\\crash\\clean-exit.sentinel");
    }
}

void WritePerPidCrashSentinelNoAlloc(const char* reason, int exitCode, const char* detail, const char* stackRvas) noexcept {
    const DWORD pid = ::GetCurrentProcessId();
    char payload[1024] = {0};
    int len = snprintf(
        payload,
        sizeof(payload),
        "PID=%lu\nReason=%s\nExitCode=%d\nDetail=%s\nStack=%s\n",
        static_cast<unsigned long>(pid),
        reason ? reason : "unexpected_exit",
        exitCode,
        detail ? detail : "",
        stackRvas ? stackRvas : "");
    if (len <= 0) return;
    if (static_cast<size_t>(len) >= sizeof(payload)) {
        len = static_cast<int>(sizeof(payload) - 1);
    }

    if (g_crashSentinelDirCount > 0) {
        for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
            wchar_t crashPath[MAX_PATH] = {0};
            swprintf_s(crashPath, _countof(crashPath), L"%ls\\last-crashed.%lu.sentinel",
                       g_crashSentinelDirsW[i], static_cast<unsigned long>(pid));
            WriteSentinelFileNoAllocW(crashPath, payload, static_cast<size_t>(len));
        }
    } else {
        wchar_t relCrashPath[MAX_PATH] = {0};
        swprintf_s(relCrashPath, _countof(relCrashPath), L"logs\\crash\\last-crashed.%lu.sentinel",
                   static_cast<unsigned long>(pid));
        WriteSentinelFileNoAllocW(relCrashPath, payload, static_cast<size_t>(len));
    }
}

void FormatStackBackTraceRvasNoAlloc(char* outBuf, size_t outCap, ULONG skipFrames) noexcept {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = '\0';
    PVOID frames[10] = {0};
    USHORT count = ::RtlCaptureStackBackTrace(skipFrames + 1, 10, frames, nullptr);
    size_t pos = 0;
    for (USHORT i = 0; i < count && pos + 32 < outCap; ++i) {
        HMODULE hMod = nullptr;
        char modName[MAX_PATH] = {0};
        const char* shortName = "unknown";
        uintptr_t rva = reinterpret_cast<uintptr_t>(frames[i]);
        if (::GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(frames[i]),
                &hMod) && hMod != nullptr) {
            rva -= reinterpret_cast<uintptr_t>(hMod);
            if (::GetModuleFileNameA(hMod, modName, MAX_PATH) > 0) {
                const char* slash = strrchr(modName, '\\');
                shortName = slash ? (slash + 1) : modName;
            }
        }
        int written = snprintf(
            outBuf + pos,
            outCap - pos,
            "%s%s+0x%llX",
            (i == 0 ? "" : " <- "),
            shortName,
            static_cast<unsigned long long>(rva));
        if (written <= 0) break;
        size_t adv = static_cast<size_t>(written);
        pos = (pos + adv < outCap) ? (pos + adv) : (outCap - 1);
    }
}

bool Kongor_IsUnrecoverableFatalExceptionCode(DWORD code) noexcept {
    switch (code) {
        case 0xC0000374UL: // STATUS_HEAP_CORRUPTION
        case 0xC0000409UL: // STATUS_STACK_BUFFER_OVERRUN
        case 0xC0000025UL: // EXCEPTION_NONCONTINUABLE_EXCEPTION
        case 0xC0000194UL: // STATUS_POSSIBLE_DEADLOCK
        case EXCEPTION_ILLEGAL_INSTRUCTION:      // 0xC000001D
        case EXCEPTION_PRIV_INSTRUCTION:         // 0xC0000096
        case 0xE0000001UL: // CRT_PURE_VIRTUAL_CALL
        case 0xE0000002UL: // CRT_INVALID_PARAMETER
            return true;
        default:
            return false;
    }
}

bool Kongor_IsShutdownFatalExceptionCode(DWORD code) noexcept {
    if (Kongor_IsUnrecoverableFatalExceptionCode(code)) {
        return true;
    }
    switch (code) {
        case EXCEPTION_ACCESS_VIOLATION:         // 0xC0000005
        case EXCEPTION_IN_PAGE_ERROR:            // 0xC0000006
        case EXCEPTION_INT_DIVIDE_BY_ZERO:       // 0xC0000094
        case EXCEPTION_STACK_OVERFLOW:           // 0xC00000FD
        case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:    // 0xC000008C
        case EXCEPTION_DATATYPE_MISALIGNMENT:    // 0x80000002
            return true;
        default:
            return false;
    }
}

void Kongor_EnterProtectedSeh() noexcept {
    ++g_inProtectedSehDepth;
}

void Kongor_LeaveProtectedSeh() noexcept {
    --g_inProtectedSehDepth;
}

int Kongor_GetProtectedSehDepth() noexcept {
    return g_inProtectedSehDepth;
}

// ============================================================================
// (#166) MSVC x64 C++ Exception (0xE06D7363) _ThrowInfo / RTTI ABI Structures
// ============================================================================
#pragma pack(push, 4)
struct _MSVC_PMD {
    int32_t mdisp; // Member displacement
    int32_t pdisp; // Vbtable displacement (-1 if not virtual base)
    int32_t vdisp; // Displacement inside vbtable
};

struct _MSVC_TypeDescriptor {
    const void* pVFTable;
    void*       spare;
    char        name[1]; // Mangled RTTI name, e.g. ".?AVruntime_error@std@@"
};

struct _MSVC_CatchableType {
    uint32_t  properties;
    int32_t   pType;            // Image-relative RVA -> _MSVC_TypeDescriptor
    _MSVC_PMD thisDisplacement;
    int32_t   sizeOrOffset;
    int32_t   copyFunction;
};

struct _MSVC_CatchableTypeArray {
    int32_t nCatchableTypes;
    int32_t arrayOfCatchableTypes[1]; // Image-relative RVAs -> _MSVC_CatchableType
};

struct _MSVC_ThrowInfo {
    uint32_t attributes;
    int32_t  pmfnUnwind;
    int32_t  pForwardCompat;
    int32_t  pCatchableTypeArray; // Image-relative RVA -> _MSVC_CatchableTypeArray
};
#pragma pack(pop)

// ============================================================================
// (#167) Benign Debugger / Thread-Naming Exception Filter & AV R/W/X Classifier
// ============================================================================
static constexpr DWORD kExceptionDbgPrintC       = 0x40010006UL; // DBG_PRINTEXCEPTION_C
static constexpr DWORD kExceptionDbgPrintWideC   = 0x4001000AUL; // DBG_PRINTEXCEPTION_WIDE_C
static constexpr DWORD kExceptionMsVcThreadName  = 0x406D1388UL; // MS_VC_EXCEPTION (SetThreadName)
static constexpr DWORD kExceptionMsvcCpp         = 0xE06D7363UL; // MSVC C++ throw

bool Kongor_IsBenignDebugOrThreadNameException(DWORD code) noexcept {
    return code == kExceptionDbgPrintC ||
           code == kExceptionDbgPrintWideC ||
           code == kExceptionMsVcThreadName;
}

static bool IsBenignDebugOrThreadNameException(DWORD code) noexcept {
    return Kongor_IsBenignDebugOrThreadNameException(code);
}

const char* Kongor_ClassifyAccessViolationKind(ULONG_PTR accessKindCode) noexcept {
    switch (accessKindCode) {
        case 0:  return "READ";
        case 1:  return "WRITE";
        case 8:  return "DEP_EXECUTE";
        default: return "UNKNOWN_AV_KIND";
    }
}

static const char* ClassifyAccessViolationKind(ULONG_PTR accessKindCode) noexcept {
    return Kongor_ClassifyAccessViolationKind(accessKindCode);
}

static const char* GetExceptionCodeString(DWORD code) noexcept {
    switch (code) {
        case EXCEPTION_ACCESS_VIOLATION:         return "EXCEPTION_ACCESS_VIOLATION (0xC0000005)";
        case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:    return "EXCEPTION_ARRAY_BOUNDS_EXCEEDED (0xC000008C)";
        case EXCEPTION_BREAKPOINT:               return "EXCEPTION_BREAKPOINT (0x80000003)";
        case EXCEPTION_DATATYPE_MISALIGNMENT:    return "EXCEPTION_DATATYPE_MISALIGNMENT (0x80000002)";
        case EXCEPTION_FLT_DENORMAL_OPERAND:     return "EXCEPTION_FLT_DENORMAL_OPERAND (0xC000008D)";
        case EXCEPTION_FLT_DIVIDE_BY_ZERO:       return "EXCEPTION_FLT_DIVIDE_BY_ZERO (0xC000008E)";
        case EXCEPTION_FLT_INEXACT_RESULT:       return "EXCEPTION_FLT_INEXACT_RESULT (0xC000008F)";
        case EXCEPTION_FLT_INVALID_OPERATION:    return "EXCEPTION_FLT_INVALID_OPERATION (0xC0000090)";
        case EXCEPTION_FLT_OVERFLOW:             return "EXCEPTION_FLT_OVERFLOW (0xC0000091)";
        case EXCEPTION_FLT_STACK_CHECK:          return "EXCEPTION_FLT_STACK_CHECK (0xC0000092)";
        case EXCEPTION_FLT_UNDERFLOW:            return "EXCEPTION_FLT_UNDERFLOW (0xC0000093)";
        case EXCEPTION_ILLEGAL_INSTRUCTION:      return "EXCEPTION_ILLEGAL_INSTRUCTION (0xC000001D)";
        case EXCEPTION_IN_PAGE_ERROR:            return "EXCEPTION_IN_PAGE_ERROR (0xC0000006)";
        case EXCEPTION_INT_DIVIDE_BY_ZERO:       return "EXCEPTION_INT_DIVIDE_BY_ZERO (0xC0000094)";
        case EXCEPTION_INT_OVERFLOW:             return "EXCEPTION_INT_OVERFLOW (0xC0000095)";
        case EXCEPTION_INVALID_DISPOSITION:      return "EXCEPTION_INVALID_DISPOSITION (0xC0000026)";
        case EXCEPTION_NONCONTINUABLE_EXCEPTION: return "EXCEPTION_NONCONTINUABLE_EXCEPTION (0xC0000025)";
        case EXCEPTION_PRIV_INSTRUCTION:         return "EXCEPTION_PRIV_INSTRUCTION (0xC0000096)";
        case EXCEPTION_SINGLE_STEP:              return "EXCEPTION_SINGLE_STEP (0x80000004)";
        case EXCEPTION_STACK_OVERFLOW:           return "EXCEPTION_STACK_OVERFLOW (0xC00000FD)";
        case 0xC0000194:                         return "STATUS_POSSIBLE_DEADLOCK (0xC0000194)";
        case 0xC0000374:                         return "STATUS_HEAP_CORRUPTION (0xC0000374)";
        case 0xC0000409:                         return "STATUS_STACK_BUFFER_OVERRUN (0xC0000409)";
        case 0xCFFFFFFF:                         return "STATUS_APPLICATION_HANG (0xCFFFFFFF)";
        case 0xE0000001:                         return "CRT_PURE_VIRTUAL_CALL (0xE0000001)";
        case 0xE0000002:                         return "CRT_INVALID_PARAMETER (0xE0000002)";
        case 0xE06D7363:                         return "MSVC_CPP_EXCEPTION (0xE06D7363)";
        default:                                 return "UNKNOWN_EXCEPTION";
    }
}

// (#166) SEH-guarded MSVC C++ 0xE06D7363 _ThrowInfo RTTI demangler & std::exception::what() extractor
static void SehExtractMsvcCppExceptionInfo(
    const EXCEPTION_RECORD* pRec,
    Kongor::StackSStream<4096>* pBanner) noexcept
{
    if (!pRec || !pBanner || pRec->ExceptionCode != kExceptionMsvcCpp || pRec->NumberParameters < 3) {
        return;
    }

    __try {
        ULONG_PTR magic         = pRec->ExceptionInformation[0];
        uintptr_t excObject     = static_cast<uintptr_t>(pRec->ExceptionInformation[1]);
        uintptr_t throwInfoAddr = static_cast<uintptr_t>(pRec->ExceptionInformation[2]);
        uintptr_t imageBase     = (pRec->NumberParameters >= 4) ? static_cast<uintptr_t>(pRec->ExceptionInformation[3]) : 0;

        if (magic != 0x19930520ULL && magic != 0x19930521ULL && magic != 0x19930522ULL && magic != 0x01994000ULL) {
            return;
        }
        if (excObject == 0 || throwInfoAddr == 0) {
            return;
        }

        const _MSVC_ThrowInfo* pThrowInfo = reinterpret_cast<const _MSVC_ThrowInfo*>(throwInfoAddr);
        if (pThrowInfo->pCatchableTypeArray == 0) {
            return;
        }

        const _MSVC_CatchableTypeArray* pCta = reinterpret_cast<const _MSVC_CatchableTypeArray*>(
            imageBase + static_cast<uint32_t>(pThrowInfo->pCatchableTypeArray));
        int32_t count = pCta->nCatchableTypes;
        if (count <= 0 || count > 32) {
            return;
        }

        char primaryMangled[256]   = "UnknownType";
        char primaryDemangled[256] = "UnknownType";
        char whatMessage[512]      = "<unavailable>";
        bool foundStdException     = false;

        for (int32_t i = 0; i < count; ++i) {
            int32_t ctRva = pCta->arrayOfCatchableTypes[i];
            if (ctRva == 0) continue;

            const _MSVC_CatchableType* pCt = reinterpret_cast<const _MSVC_CatchableType*>(
                imageBase + static_cast<uint32_t>(ctRva));
            if (pCt->pType == 0) continue;

            const _MSVC_TypeDescriptor* pTd = reinterpret_cast<const _MSVC_TypeDescriptor*>(
                imageBase + static_cast<uint32_t>(pCt->pType));
            const char* rawName = pTd->name;
            if (!rawName || rawName[0] == '\0') continue;

            if (i == 0) {
                snprintf(primaryMangled, sizeof(primaryMangled), "%s", rawName);
                const char* symInput = (rawName[0] == '.') ? (rawName + 1) : rawName;
                if (UnDecorateSymbolName(symInput, primaryDemangled, sizeof(primaryDemangled), UNDNAME_NO_ARGUMENTS) == 0) {
                    snprintf(primaryDemangled, sizeof(primaryDemangled), "%s", rawName);
                }
            }

            if (!foundStdException &&
                (strstr(rawName, "exception@std@@") != nullptr ||
                 strstr(rawName, "runtime_error@std@@") != nullptr ||
                 strstr(rawName, "logic_error@std@@") != nullptr)) {
                uintptr_t subObj = excObject;
                if (pCt->thisDisplacement.pdisp >= 0) {
                    uintptr_t vbtable = *reinterpret_cast<const uintptr_t*>(excObject + pCt->thisDisplacement.pdisp);
                    int32_t vbaseDisp = *reinterpret_cast<const int32_t*>(vbtable + pCt->thisDisplacement.vdisp);
                    subObj += static_cast<intptr_t>(pCt->thisDisplacement.pdisp) + static_cast<intptr_t>(vbaseDisp);
                }
                subObj += static_cast<intptr_t>(pCt->thisDisplacement.mdisp);

                const std::exception* pStdEx = reinterpret_cast<const std::exception*>(subObj);
                const char* w = pStdEx ? pStdEx->what() : nullptr;
                if (w && w[0] != '\0') {
                    snprintf(whatMessage, sizeof(whatMessage), "%s", w);
                    foundStdException = true;
                }
            }
        }

        pBanner->AppendFmt(
            "[GEODE-RTTI] Thrown C++ Type: %s (Mangled=%s) | std::exception::what() = \"%s\"\n",
            primaryDemangled,
            primaryMangled,
            whatMessage);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        pBanner->Append("[GEODE-RTTI] <unreadable _ThrowInfo / RTTI descriptor during C++ exception decode>\n");
    }
}

// ============================================================================
// (#168) x64 .pdata RUNTIME_FUNCTION Chained-Unwind Resolver & StackWalk64 Hooks
// ============================================================================
#pragma pack(push, 1)
struct _KONGOR_UNWIND_INFO_HEADER {
    uint8_t VersionAndFlags; // low 3 bits = Version, high 5 bits = Flags (UNW_FLAG_CHAININFO = 0x4)
    uint8_t SizeOfProlog;
    uint8_t CountOfCodes;
    uint8_t FrameRegisterAndOffset;
    uint16_t UnwindCodes[1];
};
#pragma pack(pop)

static constexpr uint8_t kUnwFlagChainInfo = 0x04;

bool Kongor_IsVidBlitBitmapVehExcluded(uintptr_t relRip, bool vidBlitHookInstalled) noexcept {
    return vidBlitHookInstalled && relRip >= 0x982D0u && relRip < 0x99400u;
}

static bool ResolveChainedPdataFunctionEntrySeh(
    uintptr_t imageBase,
    const RUNTIME_FUNCTION* pInitialRf,
    uint32_t* outBeginRva,
    uint32_t* outEndRva,
    uint32_t* outHops) noexcept
{
    __try {
        const RUNTIME_FUNCTION* pRf = pInitialRf;
        uint32_t hops = 0;
        // Follow UNW_FLAG_CHAININFO (0x4) parent chain (bounded to 16 hops)
        for (; hops < 16 && pRf != nullptr; ++hops) {
            uint32_t unwindRva = pRf->UnwindInfoAddress;
            if ((unwindRva & 1U) != 0U) {
                // Bit 0 set indicates chained RUNTIME_FUNCTION pointer directly at (unwindRva & ~1)
                pRf = reinterpret_cast<const RUNTIME_FUNCTION*>(imageBase + (unwindRva & ~1U));
                continue;
            }
            const _KONGOR_UNWIND_INFO_HEADER* pUw = reinterpret_cast<const _KONGOR_UNWIND_INFO_HEADER*>(
                imageBase + unwindRva);
            uint8_t flags = static_cast<uint8_t>((pUw->VersionAndFlags >> 3) & 0x1F);
            if ((flags & kUnwFlagChainInfo) == 0) {
                break;
            }
            // Chained RUNTIME_FUNCTION follows the 32-bit aligned UnwindCodes array
            uint32_t codeSlots = static_cast<uint32_t>(pUw->CountOfCodes);
            if ((codeSlots & 1U) != 0U) {
                ++codeSlots; // padded to even count of uint16_t slots
            }
            const uint8_t* pAfterCodes = reinterpret_cast<const uint8_t*>(&pUw->UnwindCodes[codeSlots]);
            pRf = reinterpret_cast<const RUNTIME_FUNCTION*>(pAfterCodes);
        }

        if (!pRf) return false;
        if (outBeginRva) *outBeginRva = pRf->BeginAddress;
        if (outEndRva)   *outEndRva   = pRf->EndAddress;
        if (outHops)     *outHops     = hops;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool Kongor_ResolveChainedPdataFunctionEntry(
    uintptr_t imageBase,
    const RUNTIME_FUNCTION* pInitialRf,
    uint32_t* outBeginRva,
    uint32_t* outEndRva,
    uint32_t* outHops) noexcept
{
    if (outHops) *outHops = 0;
    if (imageBase == 0 || pInitialRf == nullptr) return false;
    KongorScopedProtectedSeh sehGuard;
    return ResolveChainedPdataFunctionEntrySeh(imageBase, pInitialRf, outBeginRva, outEndRva, outHops);
}

static bool ResolvePrimaryFunctionEntryRva(
    uintptr_t pc,
    uintptr_t* outImageBase,
    uint32_t* outBeginRva,
    uint32_t* outEndRva) noexcept
{
    if (pc == 0) return false;
    __try {
        DWORD64 lookupPc = static_cast<DWORD64>(pc);
        DWORD64 imageBase = 0;
        PRUNTIME_FUNCTION pRf = RtlLookupFunctionEntry(lookupPc, &imageBase, NULL);
        if (!pRf || imageBase == 0) {
            uintptr_t trampTarget = 0;
            if (Kongor::PatchController::TryLookupTrampolineSite(pc, nullptr, 0, nullptr, 0, nullptr, &trampTarget) && trampTarget != 0) {
                lookupPc = static_cast<DWORD64>(trampTarget);
                pRf = RtlLookupFunctionEntry(lookupPc, &imageBase, NULL);
            }
        }
        if (!pRf || imageBase == 0) {
            return false;
        }

        uint32_t beginRva = 0;
        uint32_t endRva = 0;
        if (!Kongor_ResolveChainedPdataFunctionEntry(static_cast<uintptr_t>(imageBase), pRf, &beginRva, &endRva, nullptr)) {
            return false;
        }
        if (outImageBase) *outImageBase = static_cast<uintptr_t>(imageBase);
        if (outBeginRva)  *outBeginRva  = beginRva;
        if (outEndRva)    *outEndRva    = endRva;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static PVOID CALLBACK KongorFunctionTableAccess64(HANDLE hProcess, DWORD64 AddrBase) {
    DWORD64 imageBase = 0;
    PRUNTIME_FUNCTION pRf = RtlLookupFunctionEntry(AddrBase, &imageBase, NULL);
    if (pRf) {
        return pRf;
    }
    uintptr_t trampTarget = 0;
    if (Kongor::PatchController::TryLookupTrampolineSite(
            static_cast<uintptr_t>(AddrBase), nullptr, 0, nullptr, 0, nullptr, &trampTarget) &&
        trampTarget != 0) {
        pRf = RtlLookupFunctionEntry(static_cast<DWORD64>(trampTarget), &imageBase, NULL);
        if (pRf) {
            return pRf;
        }
    }
    return SymFunctionTableAccess64(hProcess, AddrBase);
}

static DWORD64 CALLBACK KongorGetModuleBase64(HANDLE hProcess, DWORD64 qwAddr) {
    DWORD64 imageBase = 0;
    if (RtlLookupFunctionEntry(qwAddr, &imageBase, NULL) != nullptr && imageBase != 0) {
        return imageBase;
    }
    uintptr_t trampTarget = 0;
    if (Kongor::PatchController::TryLookupTrampolineSite(
            static_cast<uintptr_t>(qwAddr), nullptr, 0, nullptr, 0, nullptr, &trampTarget) &&
        trampTarget != 0) {
        if (RtlLookupFunctionEntry(static_cast<DWORD64>(trampTarget), &imageBase, NULL) != nullptr && imageBase != 0) {
            return imageBase;
        }
        HMODULE hTrampMod = NULL;
        if (GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(trampTarget),
                &hTrampMod) && hTrampMod != NULL) {
            return reinterpret_cast<DWORD64>(hTrampMod);
        }
    }
    HMODULE hMod = NULL;
    if (GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(static_cast<uintptr_t>(qwAddr)),
            &hMod) && hMod != NULL) {
        return reinterpret_cast<DWORD64>(hMod);
    }
    return SymGetModuleBase64(hProcess, qwAddr);
}

bool Kongor_TestSehExtractMsvcCppException(const EXCEPTION_RECORD* pRec, char* outBuf, size_t outCap) noexcept {
    if (!outBuf || outCap == 0) return false;
    outBuf[0] = '\0';
    Kongor::StackSStream<4096> banner;
    {
        KongorScopedProtectedSeh sehGuard;
        SehExtractMsvcCppExceptionInfo(pRec, &banner);
    }
    size_t copyLen = (banner.size() < (outCap - 1)) ? banner.size() : (outCap - 1);
    memcpy(outBuf, banner.c_str(), copyLen);
    outBuf[copyLen] = '\0';
    return copyLen > 0;
}

bool Kongor_TestTrampolineUnwindLookup(uintptr_t trampPc, uintptr_t* outModBase, uint32_t* outBeginRva) noexcept {
    HANDLE hProc = GetCurrentProcess();
    PVOID pFnEntry = KongorFunctionTableAccess64(hProc, static_cast<DWORD64>(trampPc));
    DWORD64 modBase = KongorGetModuleBase64(hProc, static_cast<DWORD64>(trampPc));
    uintptr_t resolvedImgBase = 0;
    uint32_t beginRva = 0;
    uint32_t endRva = 0;
    bool resolved = ResolvePrimaryFunctionEntryRva(trampPc, &resolvedImgBase, &beginRva, &endRva);
    if (outModBase) *outModBase = static_cast<uintptr_t>(modBase);
    if (outBeginRva) *outBeginRva = beginRva;
    return (pFnEntry != nullptr) && (modBase != 0) && resolved;
}

std::string GetTimestampString() {
    SYSTEMTIME st{};
    GetSystemTime(&st);
    char buf[40] = {0};
    snprintf(buf, sizeof(buf), "%04u-%02u-%02u %02u:%02u:%02u UTC",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return std::string(buf);
}

std::string GetFileSafeTimestamp() {
    SYSTEMTIME st{};
    GetSystemTime(&st);
    char buf[32] = {0};
    snprintf(buf, sizeof(buf), "%04u%02u%02u_%02u%02u%02u",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return std::string(buf);
}

// ============================================================================
// (#260) AMD64 Non-Canonical #GPF (0xFFFFFFFFFFFFFFFF) Register Inspector &
//        Deterministic Poison / [heuristic-hint] Single-Bit-Flip Classifier
// ============================================================================
bool Kongor_IsAmd64NonCanonicalAddress(uintptr_t addr) noexcept {
    uint64_t u = static_cast<uint64_t>(addr);
    uint64_t top17 = u >> 47;
    // Canonical 48-bit AMD64 virtual addresses have bits [63:47] either all 0 or all 1 (0x1FFFF)
    return (top17 != 0ULL) && (top17 != 0x1FFFFULL);
}

bool Kongor_InspectAmd64GeneralProtectionFault(
    uintptr_t faultTargetAddr,
    const uint64_t gprValues[16],
    char* outDiag,
    size_t outCap,
    int* outNonCanonicalRegIdx,
    uintptr_t* outNonCanonicalVal) noexcept
{
    if (outNonCanonicalRegIdx) *outNonCanonicalRegIdx = -1;
    if (outNonCanonicalVal)    *outNonCanonicalVal    = 0;
    if (outDiag && outCap > 0) outDiag[0] = '\0';

    // Windows reports AMD64 #GPF (non-canonical memory dereference or unaligned 16-byte SSE fault)
    // as EXCEPTION_ACCESS_VIOLATION with ExceptionInformation[1] == 0xFFFFFFFFFFFFFFFFULL.
    if (static_cast<uint64_t>(faultTargetAddr) != 0xFFFFFFFFFFFFFFFFULL || !gprValues) {
        return false;
    }

    static const char* const kGprNames[16] = {
        "RAX", "RCX", "RDX", "RBX", "RSP", "RBP", "RSI", "RDI",
        "R8",  "R9",  "R10", "R11", "R12", "R13", "R14", "R15"
    };

    for (int i = 0; i < 16; ++i) {
        uintptr_t val = static_cast<uintptr_t>(gprValues[i]);
        if (Kongor_IsAmd64NonCanonicalAddress(val)) {
            if (outNonCanonicalRegIdx) *outNonCanonicalRegIdx = i;
            if (outNonCanonicalVal)    *outNonCanonicalVal    = val;
            if (outDiag && outCap > 0) {
                snprintf(
                    outDiag,
                    outCap,
                    "[RUST-MINIDUMP-GPF] AMD64 #GPF (0xFFFFFFFFFFFFFFFF): Non-canonical pointer in %s=0x%016llX",
                    kGprNames[i],
                    static_cast<unsigned long long>(val));
            }
            return true;
        }
    }

    // All 16 GPRs are canonical -> check unaligned 16-byte SSE/XMM operand candidate (e.g. RSP & 0xF != 0)
    if (outDiag && outCap > 0) {
        bool rspUnaligned = ((gprValues[4] & 0xFULL) != 0ULL);
        snprintf(
            outDiag,
            outCap,
            "[RUST-MINIDUMP-GPF] AMD64 #GPF (0xFFFFFFFFFFFFFFFF): All 16 GPRs canonical; likely unaligned 16-byte SSE/XMM operand (RSP_16B_Aligned=%s)",
            rspUnaligned ? "false" : "true");
    }
    return true;
}

static bool MatchesByteFillPattern(uint64_t val, uint8_t fillByte) noexcept {
    uint64_t expected64 = 0;
    for (int i = 0; i < 8; ++i) {
        expected64 = (expected64 << 8) | static_cast<uint64_t>(fillByte);
    }
    uint64_t expected32 = expected64 & 0xFFFFFFFFULL;
    // Match exact 64-bit poison or poison + small struct field offset [0..0xFF]
    if (val >= expected64 && (val - expected64) <= 0xFFULL) return true;
    if (val >= expected32 && (val - expected32) <= 0xFFULL) return true;
    return false;
}

bool Kongor_ClassifyPointerPoisonOrBitFlip(
    uintptr_t faultAddr,
    uintptr_t canonicalBaseAddr,
    char* outPatternTag,
    size_t tagCap,
    bool* outIsHeuristicBitFlip,
    uint32_t* outFlippedBitIndex) noexcept
{
    if (outIsHeuristicBitFlip) *outIsHeuristicBitFlip = false;
    if (outFlippedBitIndex)    *outFlippedBitIndex    = 0;
    if (outPatternTag && tagCap > 0) outPatternTag[0] = '\0';

    uint64_t u = static_cast<uint64_t>(faultAddr);
    if (u == 0ULL) return false;

    const char* deterministicTag = nullptr;
    if (MatchesByteFillPattern(u, 0xDD)) {
        deterministicTag = "CRT_FREED_HEAP_0xDD";
    } else if (MatchesByteFillPattern(u, 0xCC)) {
        deterministicTag = "MSVC_UNINIT_STACK_0xCC";
    } else if (MatchesByteFillPattern(u, 0xCD)) {
        deterministicTag = "CRT_UNINIT_HEAP_0xCD";
    } else if ((u & 0xFFFFFFFFFFFF0000ULL) == 0xFEEEFEEEFEEE0000ULL ||
               (u & 0xFFFF0000ULL) == 0xFEEE0000ULL) {
        deterministicTag = "WIN32_HEAPFREE_0xFEEE";
    } else if ((u >= 0xBAADF00DBAADF00DULL && (u - 0xBAADF00DBAADF00DULL) <= 0xFFULL) ||
               (u >= 0xBAADF00DULL && (u - 0xBAADF00DULL) <= 0xFFULL)) {
        deterministicTag = "WIN32_LOCALALLOC_UNINIT_0xBAADF00D";
    } else if ((u >= 0xDEADBEEFDEADBEEFULL && (u - 0xDEADBEEFDEADBEEFULL) <= 0xFFULL) ||
               (u >= 0xDEADBEEFULL && (u - 0xDEADBEEFULL) <= 0xFFULL)) {
        deterministicTag = "SENTINEL_0xDEADBEEF";
    }

    if (deterministicTag != nullptr) {
        if (outPatternTag && tagCap > 0) {
            snprintf(outPatternTag, tagCap, "%s", deterministicTag);
        }
        return true;
    }

    // Check single-bit hardware/memory flip ([heuristic-hint]):
    // Either against an explicit canonicalBaseAddr, or in the upper non-canonical bits [63:47] of a user-mode pointer
    uint64_t diff = 0ULL;
    if (canonicalBaseAddr != 0) {
        diff = u ^ static_cast<uint64_t>(canonicalBaseAddr);
    } else if (Kongor_IsAmd64NonCanonicalAddress(faultAddr)) {
        diff = u & 0xFFFF800000000000ULL;
    }

    if (diff != 0ULL && (diff & (diff - 1ULL)) == 0ULL) {
        uint32_t bitIdx = 0;
        uint64_t tmp = diff;
        while ((tmp >>= 1ULL) != 0ULL) {
            ++bitIdx;
        }
        if (outIsHeuristicBitFlip) *outIsHeuristicBitFlip = true;
        if (outFlippedBitIndex)    *outFlippedBitIndex    = bitIdx;
        if (outPatternTag && tagCap > 0) {
            snprintf(outPatternTag, tagCap, "[heuristic-hint] SINGLE_BIT_FLIP_BIT_%u", bitIdx);
        }
        return true;
    }

    return false;
}

// ============================================================================
// (#261) Bounded 40-Qword ([Rsp, Rsp + 320]) Stack-Scan Fallback &
//        Preceding x86-64 CALL Opcode Validator
// ============================================================================
bool Kongor_ValidatePrecedingCallInstruction(
    const uint8_t* codeBeforeRet,
    size_t lenBeforeRet,
    char* outEncodingTag,
    size_t tagCap) noexcept
{
    if (outEncodingTag && tagCap > 0) outEncodingTag[0] = '\0';
    if (!codeBeforeRet || lenBeforeRet < 2) return false;

    const uint8_t* end = codeBeforeRet + lenBeforeRet;
    const char* matchedTag = nullptr;

    // 1. Direct relative near call: E8 rel32 (5 bytes)
    if (lenBeforeRet >= 5 && end[-5] == 0xE8) {
        matchedTag = "CALL_REL32_E8";
    }
    // 2. RIP-relative indirect call: FF 15 disp32 (6 bytes)
    else if (lenBeforeRet >= 6 && end[-6] == 0xFF && end[-5] == 0x15) {
        matchedTag = "CALL_RIP_IND_FF15";
    }
    // 3. Register indirect call: FF D0..D7 (2 bytes, or 3 bytes with REX prefix)
    else if (lenBeforeRet >= 2 && end[-2] == 0xFF && (end[-1] & 0xF8) == 0xD0) {
        matchedTag = "CALL_REG_IND_FFD0";
    }
    // 4. Memory indirect call without displacement: FF 10..17 (excluding SIB=4 and RIP=5)
    else if (lenBeforeRet >= 2 && end[-2] == 0xFF && (end[-1] & 0xF8) == 0x10 &&
             (end[-1] & 0x07) != 0x04 && (end[-1] & 0x07) != 0x05) {
        matchedTag = "CALL_MEM_IND_FF10";
    }
    // 5. Memory indirect call with SIB byte: FF 14 sib (3 bytes)
    else if (lenBeforeRet >= 3 && end[-3] == 0xFF && end[-2] == 0x14) {
        matchedTag = "CALL_SIB_IND_FF14";
    }
    // 6. Memory indirect call with 8-bit disp: FF 50..57 disp8 (3 bytes, excluding SIB=4)
    else if (lenBeforeRet >= 3 && end[-3] == 0xFF && (end[-2] & 0xF8) == 0x50 && (end[-2] & 0x07) != 0x04) {
        matchedTag = "CALL_DISP8_IND_FF50";
    }
    // 7. Memory indirect call with SIB + 8-bit disp: FF 54 sib disp8 (4 bytes)
    else if (lenBeforeRet >= 4 && end[-4] == 0xFF && end[-3] == 0x54) {
        matchedTag = "CALL_SIB_DISP8_IND_FF54";
    }
    // 8. Memory indirect call with 32-bit disp: FF 90..97 disp32 (6 bytes, excluding SIB=4)
    else if (lenBeforeRet >= 6 && end[-6] == 0xFF && (end[-5] & 0xF8) == 0x90 && (end[-5] & 0x07) != 0x04) {
        matchedTag = "CALL_DISP32_IND_FF90";
    }
    // 9. Memory indirect call with SIB + 32-bit disp: FF 94 sib disp32 (7 bytes)
    else if (lenBeforeRet >= 7 && end[-7] == 0xFF && end[-6] == 0x94) {
        matchedTag = "CALL_SIB_DISP32_IND_FF94";
    }

    if (matchedTag != nullptr) {
        if (outEncodingTag && tagCap > 0) {
            snprintf(outEncodingTag, tagCap, "%s", matchedTag);
        }
        return true;
    }
    return false;
}

size_t Kongor_ScanBoundedStackForValidCallSites(
    const uintptr_t* stackQwords,
    size_t qwordCount,
    uintptr_t moduleBase,
    uint32_t textStartRva,
    uint32_t textEndRva,
    uintptr_t* outValidRetAddrs,
    size_t maxOut) noexcept
{
    if (!stackQwords || qwordCount == 0 || moduleBase == 0 || textEndRva <= textStartRva || !outValidRetAddrs || maxOut == 0) {
        return 0;
    }

    // Strictly bounded to 40 qwords (320 bytes: [Rsp, Rsp + 320]) per #261
    size_t maxScan = (qwordCount < 40) ? qwordCount : 40;
    size_t foundCount = 0;

    __try {
        uintptr_t textStartVa = moduleBase + static_cast<uintptr_t>(textStartRva);
        uintptr_t textEndVa   = moduleBase + static_cast<uintptr_t>(textEndRva);

        for (size_t q = 0; q < maxScan && foundCount < maxOut; ++q) {
            uintptr_t candidateRet = stackQwords[q];
            if (candidateRet <= textStartVa + 2 || candidateRet > textEndVa) {
                continue;
            }
            // rust-minidump caller_ip = ret_addr - 1 rule: ensures tail-calls at function end stay inside caller bounds
            uintptr_t callSiteVa = candidateRet - 1;
            if (callSiteVa < textStartVa || callSiteVa >= textEndVa) {
                continue;
            }

            size_t availBefore = static_cast<size_t>(candidateRet - textStartVa);
            if (availBefore > 8) availBefore = 8;
            const uint8_t* pBeforeRet = reinterpret_cast<const uint8_t*>(candidateRet - availBefore);

            if (Kongor_ValidatePrecedingCallInstruction(pBeforeRet, availBefore, nullptr, 0)) {
                outValidRetAddrs[foundCount++] = candidateRet;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Unreadable stack/code page during scan; return valid entries collected so far
    }

    return foundCount;
}

// #139, #168 & #261: SEH-guarded zero-allocation 16-frame x64 RVA + .pdata + Trampoline-aware stack walk
// with bounded 40-qword ([Rsp, Rsp + 320]) CALL-validated stack-scan fallback
static SRWLOCK s_dbgHelpSrwLock = SRWLOCK_INIT;
static thread_local bool t_inDbgHelp = false;

static bool TryAcquireDbgHelpLockBounded(DWORD maxWaitMs = 2000) noexcept {
    const DWORD startTick = GetTickCount();
    while (!TryAcquireSRWLockExclusive(&s_dbgHelpSrwLock)) {
        if (GetTickCount() - startTick >= maxWaitMs) {
            return false;
        }
        Sleep(5);
    }
    return true;
}

bool Kongor_TestDbgHelpLockAcquisition(DWORD maxWaitMs) noexcept {
    if (!TryAcquireDbgHelpLockBounded(maxWaitMs)) return false;
    ReleaseSRWLockExclusive(&s_dbgHelpSrwLock);
    return true;
}

bool Kongor_TestDbgHelpReentrancyFlag() noexcept {
    return t_inDbgHelp;
}

void Kongor_SetTestDbgHelpReentrancy(bool inDbgHelp) noexcept {
    t_inDbgHelp = inDbgHelp;
}

static void AppendSehSafeStackWalk64(const CONTEXT* pOrigCtx, Kongor::StackSStream<4096>* pBanner) noexcept {
    if (!pOrigCtx || !pBanner) return;
    if (t_inDbgHelp) {
        pBanner->Append("  [StackWalk64 skipped: recursive DbgHelp re-entrancy detected]\n");
        return;
    }
    const bool lockAcquired = TryAcquireDbgHelpLockBounded(2000);
    if (!lockAcquired) {
        pBanner->Append("  [StackWalk64 skipped: DbgHelp lock contention timeout (2000ms)]\n");
        return;
    }
    t_inDbgHelp = true;

    __try {
        CONTEXT walkCtx = *pOrigCtx;
        STACKFRAME64 frame{};
        frame.AddrPC.Offset    = walkCtx.Rip;
        frame.AddrPC.Mode      = AddrModeFlat;
        frame.AddrFrame.Offset = walkCtx.Rbp;
        frame.AddrFrame.Mode   = AddrModeFlat;
        frame.AddrStack.Offset = walkCtx.Rsp;
        frame.AddrStack.Mode   = AddrModeFlat;

        HANDLE hProcess = GetCurrentProcess();
        HANDLE hThread  = GetCurrentThread();
        int walkedFrames = 0;

        for (int idx = 0; idx < 16; ++idx) {
            if (frame.AddrPC.Offset == 0) break;
            ++walkedFrames;

            uintptr_t pc = static_cast<uintptr_t>(frame.AddrPC.Offset);
            HMODULE hMod = NULL;
            char modPath[MAX_PATH] = "Unknown";
            const char* modShort = "UnknownModule";
            uintptr_t rva = pc;

            if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCSTR>(pc), &hMod) && hMod != NULL) {
                rva = pc - reinterpret_cast<uintptr_t>(hMod);
                if (GetModuleFileNameA(hMod, modPath, MAX_PATH) > 0) {
                    const char* pSlash = strrchr(modPath, '\\');
                    modShort = pSlash ? (pSlash + 1) : modPath;
                }
            }

            char trampGrp[64]  = {0};
            char trampName[64] = {0};
            uintptr_t trampBase   = 0;
            uintptr_t trampTarget = 0;
            if (Kongor::PatchController::TryLookupTrampolineSite(
                    pc, trampGrp, sizeof(trampGrp), trampName, sizeof(trampName), &trampBase, &trampTarget)) {
                pBanner->AppendFmt("  #%02d: 0x%016llX [Trampoline: %s::%s+0x%llX -> target 0x%016llX]\n",
                    idx,
                    static_cast<unsigned long long>(pc),
                    trampGrp,
                    trampName,
                    static_cast<unsigned long long>(pc - trampBase),
                    static_cast<unsigned long long>(trampTarget));
            } else {
                uintptr_t pdataImgBase = 0;
                uint32_t funcBeginRva  = 0;
                uint32_t funcEndRva    = 0;
                if (ResolvePrimaryFunctionEntryRva(pc, &pdataImgBase, &funcBeginRva, &funcEndRva) && rva >= funcBeginRva) {
                    pBanner->AppendFmt("  #%02d: 0x%016llX %s+0x%llX (pdataFunc=0x%lX+0x%llX)\n",
                        idx,
                        static_cast<unsigned long long>(pc),
                        modShort,
                        static_cast<unsigned long long>(rva),
                        static_cast<unsigned long>(funcBeginRva),
                        static_cast<unsigned long long>(rva - funcBeginRva));
                } else {
                    pBanner->AppendFmt("  #%02d: 0x%016llX %s+0x%llX\n",
                        idx,
                        static_cast<unsigned long long>(pc),
                        modShort,
                        static_cast<unsigned long long>(rva));
                }
            }

            BOOL stepOk = StackWalk64(
                IMAGE_FILE_MACHINE_AMD64,
                hProcess,
                hThread,
                &frame,
                &walkCtx,
                NULL,
                KongorFunctionTableAccess64,
                KongorGetModuleBase64,
                NULL);
            if (!stepOk || frame.AddrPC.Offset == frame.AddrReturn.Offset) {
                break;
            }
        }

        // (#261) If StackWalk64 terminated after <= 1 frame (e.g. stripped leaf function or corrupted RBP/RSP),
        // run bounded 40-qword ([Rsp, Rsp + 320]) stack scan checking retAddr - 1 & preceding CALL opcode.
        if (walkedFrames <= 1 && pOrigCtx->Rsp != 0) {
            const uintptr_t* pStackWords = reinterpret_cast<const uintptr_t*>(static_cast<uintptr_t>(pOrigCtx->Rsp));
            int scanRecovered = 0;
            for (size_t q = 0; q < 40 && scanRecovered < 8; ++q) {
                uintptr_t candRet = pStackWords[q];
                if (candRet < 0x10000ULL || Kongor_IsAmd64NonCanonicalAddress(candRet)) continue;
                uintptr_t probeVa = candRet - 1;
                uintptr_t pdataBase = 0;
                uint32_t fnBegin = 0, fnEnd = 0;
                if (!ResolvePrimaryFunctionEntryRva(probeVa, &pdataBase, &fnBegin, &fnEnd)) continue;
                char callTag[32] = {0};
                if (!Kongor_ValidatePrecedingCallInstruction(
                        reinterpret_cast<const uint8_t*>(candRet - 8), 8, callTag, sizeof(callTag))) {
                    continue;
                }
                uintptr_t candRva = candRet - pdataBase;
                pBanner->AppendFmt("  [stack-scan +0x%03llX] 0x%016llX RVA=0x%llX (pdataFunc=0x%lX, call=%s)\n",
                    static_cast<unsigned long long>(q * 8),
                    static_cast<unsigned long long>(candRet),
                    static_cast<unsigned long long>(candRva),
                    static_cast<unsigned long>(fnBegin),
                    callTag);
                ++scanRecovered;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        pBanner->Append("  [StackWalk64 terminated early due to unreadable stack frame]\n");
    }

    t_inDbgHelp = false;
    ReleaseSRWLockExclusive(&s_dbgHelpSrwLock);
}

static bool AsciiWideEqualsIgnoreCase(const wchar_t* a, const wchar_t* b) noexcept {
    if (!a || !b) return false;
    while (*a != L'\0' && *b != L'\0') {
        wchar_t ca = *a;
        wchar_t cb = *b;
        if (ca >= L'A' && ca <= L'Z') ca = static_cast<wchar_t>(ca + (L'a' - L'A'));
        if (cb >= L'A' && cb <= L'Z') cb = static_cast<wchar_t>(cb + (L'a' - L'A'));
        if (ca != cb) return false;
        ++a;
        ++b;
    }
    return *a == L'\0' && *b == L'\0';
}

static bool WideContainsAsciiSubstringIgnoreCase(const wchar_t* haystack, const wchar_t* needle) noexcept {
    if (!haystack || !needle || *needle == L'\0') return false;
    for (const wchar_t* p = haystack; *p != L'\0'; ++p) {
        const wchar_t* h = p;
        const wchar_t* n = needle;
        while (*h != L'\0' && *n != L'\0') {
            wchar_t ch = (*h == L'/') ? L'\\' : *h;
            wchar_t cn = (*n == L'/') ? L'\\' : *n;
            if (ch >= L'A' && ch <= L'Z') ch = static_cast<wchar_t>(ch + (L'a' - L'A'));
            if (cn >= L'A' && cn <= L'Z') cn = static_cast<wchar_t>(cn + (L'a' - L'A'));
            if (ch != cn) break;
            ++h;
            ++n;
        }
        if (*n == L'\0') return true;
    }
    return false;
}

bool Kongor_IsSafeEngineOrSystemModuleW(const wchar_t* rawModulePath, const wchar_t* baseName) noexcept {
    if (!baseName || *baseName == L'\0') return false;

    static const wchar_t* const kSafeModules[] = {
        L"hon_x64.exe", L"hon.exe", L"willowmaker.exe",
        L"k2_x64.dll", L"k2.dll", L"cgame_x64.dll", L"cgame.dll", L"game_x64.dll", L"game.dll",
        L"vid_d3d9_x64.dll", L"vid_d3d9.dll", L"vid_gl2_x64.dll", L"vid_gl2.dll", L"vid_vulkan_x64.dll",
        L"d3d9.dll", L"dxgi.dll", L"d3d11.dll", L"d3d12.dll", L"vulkan-1.dll", L"opengl32.dll",
        L"hon_vulkan_layer.dll", L"native_guard.dll", L"version.dll", L"winhttp.dll",
        L"fmodex64.dll", L"fmodex.dll", L"openal32.dll", L"soft_oal.dll",
        L"lua5.1.dll", L"lua51.dll", L"zlib1.dll", L"libcurl.dll", L"curl_x64.dll", L"freetype6.dll", L"speex.dll",
        L"ntdll.dll", L"kernel32.dll", L"kernelbase.dll", L"user32.dll", L"win32u.dll", L"gdi32.dll", L"gdi32full.dll",
        L"msvcrt.dll", L"ucrtbase.dll", L"vcruntime140.dll", L"vcruntime140_1.dll", L"msvcp140.dll",
        L"advapi32.dll", L"sechost.dll", L"rpcrt4.dll", L"combase.dll", L"ole32.dll", L"oleaut32.dll",
        L"shell32.dll", L"shcore.dll", L"shlwapi.dll", L"imm32.dll", L"ws2_32.dll", L"mswsock.dll",
        L"iphlpapi.dll", L"dnsapi.dll", L"crypt32.dll", L"bcrypt.dll", L"bcryptprimitives.dll",
        L"setupapi.dll", L"cfgmgr32.dll", L"dwmapi.dll", L"uxtheme.dll", L"winmm.dll", L"mmdevapi.dll", L"audioses.dll",
        L"dbghelp.dll", L"dbgcore.dll", L"powrprof.dll", L"umpdc.dll", L"kernel.appcore.dll",
        L"nvoglv64.dll", L"nvwgf2umx.dll", L"nvldumdx.dll", L"nvgpucomp64.dll", L"nvspcap64.dll",
        L"amdvlk64.dll", L"amdxn64.dll", L"aticfx64.dll", L"amdihk64.dll",
        L"igvk64.dll", L"igd10iumd64.dll", L"igd12umd64.dll", L"igdgmm64.dll"
    };

    for (size_t i = 0; i < sizeof(kSafeModules) / sizeof(kSafeModules[0]); ++i) {
        if (AsciiWideEqualsIgnoreCase(baseName, kSafeModules[i])) {
            return true;
        }
    }

    if (rawModulePath != nullptr) {
        if (WideContainsAsciiSubstringIgnoreCase(rawModulePath, L"\\Windows\\System32\\") ||
            WideContainsAsciiSubstringIgnoreCase(rawModulePath, L"\\Windows\\SysWOW64\\") ||
            WideContainsAsciiSubstringIgnoreCase(rawModulePath, L"\\Windows\\WinSxS\\") ||
            WideContainsAsciiSubstringIgnoreCase(rawModulePath, L"\\SystemRoot\\")) {
            return true;
        }
    }

    return false;
}

BOOL CALLBACK Kongor_Tier1PrivacyMiniDumpCallback(
    PVOID CallbackParam,
    _MINIDUMP_CALLBACK_INPUT* CallbackInput,
    _MINIDUMP_CALLBACK_OUTPUT* CallbackOutput) noexcept
{
    if (!CallbackInput || !CallbackOutput) {
        return TRUE;
    }

    __try {
        KongorTier1DumpCallbackCtx* pCtx = reinterpret_cast<KongorTier1DumpCallbackCtx*>(CallbackParam);

        switch (CallbackInput->CallbackType) {
            case ModuleCallback: {
                wchar_t* fullPath = CallbackInput->Module.FullPath;
                const wchar_t* basePtr = fullPath;
                bool hadDirectory = false;

                if (fullPath != nullptr && fullPath[0] != L'\0') {
                    for (const wchar_t* p = fullPath; *p != L'\0'; ++p) {
                        if (*p == L'\\' || *p == L'/' || *p == L':') {
                            basePtr = p + 1;
                            hadDirectory = true;
                        }
                    }
                }

                const uintptr_t modBase = static_cast<uintptr_t>(CallbackInput->Module.BaseOfImage);
                const uintptr_t modEnd  = modBase + static_cast<uintptr_t>(CallbackInput->Module.SizeOfImage);
                const bool isFaultingModule = (pCtx != nullptr && pCtx->FaultAddress != 0 &&
                                               pCtx->FaultAddress >= modBase && pCtx->FaultAddress < modEnd);
                const bool isSafeSystemOrEngine = Kongor_IsSafeEngineOrSystemModuleW(fullPath, basePtr);

                if (!isFaultingModule && !isSafeSystemOrEngine) {
                    CallbackOutput->ModuleWriteFlags &=
                        ~static_cast<ULONG>(ModuleWriteCvRecord | ModuleWriteMiscRecord | ModuleWriteDataSeg | ModuleWriteTlsData);
                    if (pCtx != nullptr) {
                        ++pCtx->ThirdPartyCvCleared;
                    }
                } else {
                    CallbackOutput->ModuleWriteFlags &= ~static_cast<ULONG>(ModuleWriteMiscRecord | ModuleWriteDataSeg | ModuleWriteTlsData);
                }

                if (hadDirectory && fullPath != nullptr && basePtr != nullptr && *basePtr != L'\0') {
                    size_t baseChars = 0;
                    while (basePtr[baseChars] != L'\0') {
                        ++baseChars;
                    }
                    memmove(fullPath, basePtr, (baseChars + 1) * sizeof(wchar_t));
                    if (pCtx != nullptr) {
                        ++pCtx->ModulesStripped;
                    }
                }
                break;
            }

            case IncludeThreadCallback: {
                if (pCtx != nullptr &&
                    pCtx->TargetStackThreadId != 0 &&
                    CallbackInput->IncludeThread.ThreadId != pCtx->TargetStackThreadId) {
                    ++pCtx->BackgroundStacksSuppressed;
                    return FALSE;
                }
                break;
            }

            case ThreadCallback:
            case ThreadExCallback: {
                const ULONG tid = (CallbackInput->CallbackType == ThreadCallback)
                    ? CallbackInput->Thread.ThreadId
                    : CallbackInput->ThreadEx.ThreadId;

                if (pCtx != nullptr && pCtx->TargetStackThreadId != 0 && tid != pCtx->TargetStackThreadId) {
                    CallbackOutput->ThreadWriteFlags &=
                        ~static_cast<ULONG>(ThreadWriteStack | ThreadWriteBackingStore | ThreadWriteInstructionWindow | ThreadWriteThreadData);
                    ++pCtx->BackgroundStacksSuppressed;
                    return FALSE;
                }
                break;
            }

            case IncludeVmRegionCallback: {
                CallbackOutput->Continue = FALSE;
                break;
            }

            default:
                break;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return TRUE;
    }

    return TRUE;
}

static void WriteTwoTierMinidumpsSehSafe(
    PEXCEPTION_POINTERS pExceptionInfo,
    const char* tsFile,
    Kongor::StackSStream<4096>* pBanner,
    bool skipRichTier2) noexcept
{
    if (!pExceptionInfo || !tsFile || !pBanner) return;

    if (t_inDbgHelp) {
        pBanner->Append("Minidump writing skipped: recursive DbgHelp re-entrancy detected.\n");
        return;
    }

    const bool lockAcquired = TryAcquireDbgHelpLockBounded(2000);
    if (!lockAcquired) {
        pBanner->Append("Minidump writing skipped: DbgHelp lock contention timeout (2000ms).\n");
        return;
    }

    t_inDbgHelp = true;

    const wchar_t* crashDirW = (g_crashSentinelDirCount > 0 && g_crashSentinelDirsW[0][0] != L'\0')
        ? g_crashSentinelDirsW[0]
        : L"logs\\crash";

    __try {
        MINIDUMP_EXCEPTION_INFORMATION dumpExceptionInfo;
        dumpExceptionInfo.ThreadId = GetCurrentThreadId();
        dumpExceptionInfo.ExceptionPointers = pExceptionInfo;
        dumpExceptionInfo.ClientPointers = FALSE;

        // #139: Minimal-First Two-Tier Minidump Writer (satisfies: OBL-W3-05)
        // Tier 1: Fast minimal dump written to pre-resolved absolute crashDirW FIRST so a guaranteed valid .dmp survives
        wchar_t dumpPathW[MAX_PATH] = {0};
        swprintf_s(dumpPathW, _countof(dumpPathW), L"%ls\\minidump_%hs.dmp", crashDirW, tsFile);
        HANDLE hDumpFile = CreateFileW(dumpPathW, GENERIC_WRITE, 0, NULL,
                                       CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (hDumpFile != INVALID_HANDLE_VALUE) {
            KongorTier1DumpCallbackCtx tier1CbCtx{};
            tier1CbCtx.TargetStackThreadId = dumpExceptionInfo.ThreadId;
            tier1CbCtx.FaultAddress = (pExceptionInfo->ExceptionRecord != nullptr)
                ? reinterpret_cast<uintptr_t>(pExceptionInfo->ExceptionRecord->ExceptionAddress)
                : 0;
            MINIDUMP_CALLBACK_INFORMATION tier1CbInfo{};
            tier1CbInfo.CallbackRoutine = Kongor_Tier1PrivacyMiniDumpCallback;
            tier1CbInfo.CallbackParam = &tier1CbCtx;

            MINIDUMP_TYPE tier1Type = static_cast<MINIDUMP_TYPE>(
                MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpFilterModulePaths | MiniDumpWithoutAuxiliaryState);
            BOOL tier1Ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hDumpFile,
                                             tier1Type, &dumpExceptionInfo, NULL, &tier1CbInfo);
            FlushFileBuffers(hDumpFile);
            CloseHandle(hDumpFile);
            if (tier1Ok) {
                pBanner->AppendFmt(
                    "Minidump (Tier-1 Minimal) saved: minidump_%s.dmp (modulesStripped=%u, thirdPartyCvCleared=%u, bgStacksSuppressed=%u)\n",
                    tsFile,
                    tier1CbCtx.ModulesStripped,
                    tier1CbCtx.ThirdPartyCvCleared,
                    tier1CbCtx.BackgroundStacksSuppressed);
            }
        }

        // Tier 2: Rich dump with data segments and indirectly referenced memory
        // Skipped under RtlDllShutdownInProgress() to minimize work while LdrpLoaderLock is held.
        if (skipRichTier2) {
            pBanner->Append("Minidump (Tier-2 Rich) skipped during LdrShutdownProcess; Tier-1 minimal dump preserved.\n");
        } else {
            wchar_t richDumpPathW[MAX_PATH] = {0};
            swprintf_s(richDumpPathW, _countof(richDumpPathW), L"%ls\\minidump_%hs.rich.dmp", crashDirW, tsFile);
            HANDLE hRichDumpFile = CreateFileW(richDumpPathW, GENERIC_WRITE, 0, NULL,
                                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (hRichDumpFile != INVALID_HANDLE_VALUE) {
                MINIDUMP_TYPE tier2Type = static_cast<MINIDUMP_TYPE>(
                    MiniDumpWithDataSegs | MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithThreadInfo);
                BOOL tier2Ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hRichDumpFile,
                                                 tier2Type, &dumpExceptionInfo, NULL, NULL);
                FlushFileBuffers(hRichDumpFile);
                LARGE_INTEGER richSize{};
                BOOL gotSize = GetFileSizeEx(hRichDumpFile, &richSize);
                CloseHandle(hRichDumpFile);
                if (tier2Ok && gotSize && richSize.QuadPart > 0) {
                    pBanner->AppendFmt("Minidump (Tier-2 Rich) saved:    minidump_%s.rich.dmp (%lld bytes)\n",
                        tsFile, static_cast<long long>(richSize.QuadPart));
                } else {
                    DeleteFileW(richDumpPathW);
                    pBanner->Append("Minidump (Tier-2 Rich) skipped/cleaned up; Tier-1 minimal dump preserved.\n");
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        pBanner->Append("Minidump writing encountered exception in DbgHelp.\n");
    }

    t_inDbgHelp = false;
    ReleaseSRWLockExclusive(&s_dbgHelpSrwLock);
}

// [KONGOR_VEH_CRASH_PATH_BEGIN]
static LONG HandleExceptionInternal(PEXCEPTION_POINTERS pExceptionInfo, bool isTerminalFault) noexcept {
    // Release any active OS cursor clip FIRST so the user's mouse is never held hostage during crash/dump writing.
    Kongor_ForceReleaseCursorEmergency();

    static thread_local bool s_bInExceptionHandler = false;
    if (s_bInExceptionHandler) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    s_bInExceptionHandler = true;
    struct ScopeExitGuard {
        ~ScopeExitGuard() { s_bInExceptionHandler = false; }
    } scopeExitGuard;

    if (!pExceptionInfo || !pExceptionInfo->ExceptionRecord) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    DWORD exceptionCode = pExceptionInfo->ExceptionRecord->ExceptionCode;
    // (#167) Ignore benign OutputDebugString / SetThreadName exceptions
    if (IsBenignDebugOrThreadNameException(exceptionCode)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    const bool rtlShutdown = Kongor_IsRtlDllShutdownInProgress();
    const bool shutdownActive = rtlShutdown ||
                                g_quitInitiated.load(std::memory_order_acquire) ||
                                g_shutdownStarted.load(std::memory_order_acquire);

    uintptr_t faultAddress = (uintptr_t)pExceptionInfo->ExceptionRecord->ExceptionAddress;

    HMODULE hFaultModule = NULL;
    char fullModulePath[MAX_PATH] = "Unknown";
    const char* moduleFileName = "UnknownModule";
    uintptr_t relativeOffset = 0;

    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCSTR)faultAddress, &hFaultModule)) {
        if (GetModuleFileNameA(hFaultModule, fullModulePath, MAX_PATH) > 0) {
            const char* pLastSlash = strrchr(fullModulePath, '\\');
            moduleFileName = pLastSlash ? (pLastSlash + 1) : fullModulePath;
            relativeOffset = faultAddress - (uintptr_t)hFaultModule;
        }
    }

    char locationBuf[320] = {0};
    snprintf(locationBuf, sizeof(locationBuf), "%s+0x%llX",
             moduleFileName, static_cast<unsigned long long>(relativeOffset));

    SYSTEMTIME st{};
    GetSystemTime(&st);
    char tsIso[40] = {0};
    char tsFile[32] = {0};
    snprintf(tsIso, sizeof(tsIso), "%04u-%02u-%02u %02u:%02u:%02u UTC",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    snprintf(tsFile, sizeof(tsFile), "%04u%02u%02u_%02u%02u%02u",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    // Option S: Stack-allocated crash banner (safe even under 0xC0000374 STATUS_HEAP_CORRUPTION)
    Kongor::StackSStream<4096> banner;
    banner.Append("\n================================================================================\n");
    banner.Append("[KONGOR NATIVE GUARD] CRASH DETECTED\n");
    banner.AppendFmt("Timestamp:      %s\n", tsIso);
    banner.AppendFmt("Exception:      %s [0x%08lX]\n", GetExceptionCodeString(exceptionCode), (unsigned long)exceptionCode);
    banner.AppendFmt("Fault Address:  0x%016llX\n", (unsigned long long)faultAddress);
    banner.AppendFmt("Fault Location: %s\n", locationBuf);
    banner.AppendFmt("Module Name:    %s\n", moduleFileName);
    if (shutdownActive) {
        banner.AppendFmt("ShutdownState:  quitInitiated=%d shutdownStarted=%d rtlDllShutdown=%d terminal=%d\n",
            g_quitInitiated.load(std::memory_order_acquire) ? 1 : 0,
            g_shutdownStarted.load(std::memory_order_acquire) ? 1 : 0,
            rtlShutdown ? 1 : 0,
            isTerminalFault ? 1 : 0);
    }

    // (#167 & #260) Access Violation / In-Page Error R/W/X, AMD64 #GPF (0xFFFFFFFFFFFFFFFF), & Poison/Bit-Flip Classifier
    if ((exceptionCode == EXCEPTION_ACCESS_VIOLATION || exceptionCode == EXCEPTION_IN_PAGE_ERROR) &&
        pExceptionInfo->ExceptionRecord->NumberParameters >= 2) {
        ULONG_PTR avKind   = pExceptionInfo->ExceptionRecord->ExceptionInformation[0];
        ULONG_PTR avTarget = pExceptionInfo->ExceptionRecord->ExceptionInformation[1];
        banner.AppendFmt("[SHADPS4-VEH] AccessViolationKind=%s (code=%llu) TargetAddr=0x%016llX\n",
            ClassifyAccessViolationKind(avKind),
            static_cast<unsigned long long>(avKind),
            static_cast<unsigned long long>(avTarget));

        if (static_cast<uint64_t>(avTarget) == 0xFFFFFFFFFFFFFFFFULL && pExceptionInfo->ContextRecord) {
            const CONTEXT* c = pExceptionInfo->ContextRecord;
            uint64_t gprs[16] = {
                c->Rax, c->Rcx, c->Rdx, c->Rbx, c->Rsp, c->Rbp, c->Rsi, c->Rdi,
                c->R8,  c->R9,  c->R10, c->R11, c->R12, c->R13, c->R14, c->R15
            };
            char gpfDiag[256] = {0};
            uintptr_t nonCanonVal = 0;
            if (Kongor_InspectAmd64GeneralProtectionFault(
                    static_cast<uintptr_t>(avTarget), gprs, gpfDiag, sizeof(gpfDiag), nullptr, &nonCanonVal)) {
                banner.AppendFmt("%s\n", gpfDiag);
                char poisonTag[96] = {0};
                if (nonCanonVal != 0 && Kongor_ClassifyPointerPoisonOrBitFlip(nonCanonVal, 0, poisonTag, sizeof(poisonTag), nullptr, nullptr)) {
                    banner.AppendFmt("[RUST-MINIDUMP-POISON] Pattern=%s Address=0x%016llX\n",
                        poisonTag, static_cast<unsigned long long>(nonCanonVal));
                }
            }
        } else {
            char poisonTag[96] = {0};
            if (Kongor_ClassifyPointerPoisonOrBitFlip(static_cast<uintptr_t>(avTarget), 0, poisonTag, sizeof(poisonTag), nullptr, nullptr)) {
                banner.AppendFmt("[RUST-MINIDUMP-POISON] Pattern=%s Address=0x%016llX\n",
                    poisonTag, static_cast<unsigned long long>(avTarget));
            }
        }
    }

    // (#166) MSVC C++ Exception (0xE06D7363) RTTI Demangling & std::exception::what() extraction
    if (exceptionCode == kExceptionMsvcCpp) {
        SehExtractMsvcCppExceptionInfo(pExceptionInfo->ExceptionRecord, &banner);
    }

    if (pExceptionInfo->ContextRecord) {
        const CONTEXT* ctx = pExceptionInfo->ContextRecord;
        banner.Append("--------------------------------------------------------------------------------\n");
        banner.Append("Registers (x64):\n");
        banner.AppendFmt("  RIP: 0x%016llX   RSP: 0x%016llX   RBP: 0x%016llX\n",
            (unsigned long long)ctx->Rip, (unsigned long long)ctx->Rsp, (unsigned long long)ctx->Rbp);
        banner.AppendFmt("  RAX: 0x%016llX   RBX: 0x%016llX   RCX: 0x%016llX\n",
            (unsigned long long)ctx->Rax, (unsigned long long)ctx->Rbx, (unsigned long long)ctx->Rcx);
        banner.AppendFmt("  RDX: 0x%016llX   RSI: 0x%016llX   RDI: 0x%016llX\n",
            (unsigned long long)ctx->Rdx, (unsigned long long)ctx->Rsi, (unsigned long long)ctx->Rdi);
        banner.AppendFmt("  R8:  0x%016llX   R9:  0x%016llX   R10: 0x%016llX\n",
            (unsigned long long)ctx->R8, (unsigned long long)ctx->R9, (unsigned long long)ctx->R10);
        banner.AppendFmt("  R11: 0x%016llX   R12: 0x%016llX   R13: 0x%016llX\n",
            (unsigned long long)ctx->R11, (unsigned long long)ctx->R12, (unsigned long long)ctx->R13);
        banner.AppendFmt("  R14: 0x%016llX   R15: 0x%016llX   EFL: 0x%08lX\n",
            (unsigned long long)ctx->R14, (unsigned long long)ctx->R15, (unsigned long)ctx->EFlags);

        banner.Append("--------------------------------------------------------------------------------\n");
        banner.Append("Call Stack (x64 Module+RVA, max 16 frames):\n");
        AppendSehSafeStackWalk64(ctx, &banner);
    }

    // Option R: Lazy-format recent 32 binary telemetry breadcrumbs via static crash buffer
    // (keeps `banner` stack footprint fixed at 4096B for 0xC00000FD stack-overflow safety;
    // never invokes runtime LogBurstLimiter inside crash handler).
    static std::atomic_flag s_crashRingBusy = ATOMIC_FLAG_INIT;
    static Kongor::StackSStream<6144> s_crashRingStream;
    bool capturedExtendedRing = false;
    if (!s_crashRingBusy.test_and_set(std::memory_order_acquire)) {
        s_crashRingStream.Clear();
        s_crashRingStream.Append("--------------------------------------------------------------------------------\n");
        s_crashRingStream.Append("Recent Binary Telemetry Flight-Recorder (up to 32 committed events):\n");
        Kongor::GetGlobalBinaryTelemetryRing().FormatRecentToStream(s_crashRingStream, 32);
        s_crashRingStream.Append("================================================================================\n\n");
        capturedExtendedRing = true;
    } else {
        banner.Append("--------------------------------------------------------------------------------\n");
        banner.Append("Recent Binary Telemetry Ring:\n");
        Kongor::GetGlobalBinaryTelemetryRing().FormatRecentToStream(banner, 4);
    }

    CreateDirectoryA("logs", NULL);
    CreateDirectoryA("logs/crash", NULL);
    CreateDirectoryA("userdata", NULL);
    CreateDirectoryA("userdata/logs", NULL);

    // STEP 1 (BEFORE MiniDumpWriteDump): Revoke any pre-written clean-exit sentinel and flush per-PID +
    // legacy crash sentinels and engine_crashes.log using pre-resolved absolute paths so crash state
    // survives even if DbgHelp hangs or faults under LdrpLoaderLock.
    if (isTerminalFault || shutdownActive) {
        char stackRvas[512] = {0};
        FormatStackBackTraceRvasNoAlloc(stackRvas, sizeof(stackRvas), 0);
        RevokeCleanExitSentinelsNoAlloc();
        WritePerPidCrashSentinelNoAlloc(
            shutdownActive ? "ShutdownUnhandledException" : "UnhandledException",
            static_cast<int>(exceptionCode),
            locationBuf,
            stackRvas);
    }

    // (#170) Write last-crashed.sentinel so subsequent startup automatically activates Safe Mode
    {
        char sentinelPayload[256] = {0};
        int sentinelLen = snprintf(
            sentinelPayload,
            sizeof(sentinelPayload),
            "{\"timestamp\":\"%s\",\"exception\":\"0x%08lX\",\"location\":\"%s\"}\n",
            tsIso,
            static_cast<unsigned long>(exceptionCode),
            locationBuf);
        if (sentinelLen > 0) {
            if (g_crashSentinelDirCount > 0) {
                for (size_t i = 0; i < g_crashSentinelDirCount; ++i) {
                    wchar_t absSentW[MAX_PATH] = {0};
                    swprintf_s(absSentW, _countof(absSentW), L"%ls\\last-crashed.sentinel", g_crashSentinelDirsW[i]);
                    WriteSentinelFileNoAllocW(absSentW, sentinelPayload, static_cast<size_t>(sentinelLen));
                }
            } else {
                WriteSentinelFileNoAllocW(L"logs\\crash\\last-crashed.sentinel", sentinelPayload, static_cast<size_t>(sentinelLen));
            }
            WriteSentinelFileNoAllocW(L"userdata\\logs\\last-crashed.sentinel", sentinelPayload, static_cast<size_t>(sentinelLen));
        }
    }

    // Flush the crash banner + flight-recorder to engine_crashes.log BEFORE invoking MiniDumpWriteDump
    const wchar_t* crashLogDirW = (g_crashSentinelDirCount > 0 && g_crashSentinelDirsW[0][0] != L'\0')
        ? g_crashSentinelDirsW[0]
        : L"logs\\crash";
    wchar_t crashLogPathW[MAX_PATH] = {0};
    swprintf_s(crashLogPathW, _countof(crashLogPathW), L"%ls\\engine_crashes.log", crashLogDirW);

    auto appendEngineCrashLogW = [&](const char* data, size_t dataLen) noexcept {
        if (!data || dataLen == 0) return;
        HANDLE hCrashLog = CreateFileW(crashLogPathW, FILE_APPEND_DATA,
                                       FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                                       OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (hCrashLog != INVALID_HANDLE_VALUE) {
            DWORD written = 0;
            WriteFile(hCrashLog, data, static_cast<DWORD>(dataLen), &written, NULL);
            FlushFileBuffers(hCrashLog);
            CloseHandle(hCrashLog);
        }
    };

    appendEngineCrashLogW(banner.c_str(), banner.size());
    if (capturedExtendedRing && s_crashRingStream.size() > 0) {
        appendEngineCrashLogW(s_crashRingStream.c_str(), s_crashRingStream.size());
    }

    // STEP 2: Minimal-First Two-Tier Minidump Writer (deduplicating same-thread VEH -> UEF pass within 500ms)
    static thread_local uintptr_t s_lastDumpFaultAddr = 0;
    static thread_local DWORD     s_lastDumpCode      = 0;
    static thread_local ULONGLONG s_lastDumpTickMs    = 0;
    const ULONGLONG nowDumpTick = ::GetTickCount64();
    const bool duplicateDumpPass =
        (s_lastDumpFaultAddr == faultAddress &&
         s_lastDumpCode == exceptionCode &&
         (nowDumpTick - s_lastDumpTickMs) <= 500ULL);

    Kongor::StackSStream<4096> dumpStatusBanner;
    if (!duplicateDumpPass) {
        s_lastDumpFaultAddr = faultAddress;
        s_lastDumpCode      = exceptionCode;
        s_lastDumpTickMs    = nowDumpTick;
        WriteTwoTierMinidumpsSehSafe(pExceptionInfo, tsFile, &dumpStatusBanner, /*skipRichTier2=*/rtlShutdown);
        if (!capturedExtendedRing) {
            dumpStatusBanner.Append("================================================================================\n\n");
        }
        appendEngineCrashLogW(dumpStatusBanner.c_str(), dumpStatusBanner.size());
    }

    OutputDebugStringA(banner.c_str());
    if (dumpStatusBanner.size() > 0) {
        OutputDebugStringA(dumpStatusBanner.c_str());
    }
    if (capturedExtendedRing && s_crashRingStream.size() > 0) {
        OutputDebugStringA(s_crashRingStream.c_str());
    }
    if (capturedExtendedRing) {
        s_crashRingBusy.clear(std::memory_order_release);
    }
    if (!rtlShutdown) {
        _flushall();
    }

    // STEP 3: Prevent ntdll!LdrpCallInitRoutine from swallowing shutdown faults and returning ExitCode 0!
    if ((isTerminalFault && shutdownActive) || rtlShutdown) {
        char termLog[256] = {0};
        snprintf(termLog, sizeof(termLog),
                 "[KONGOR NATIVE GUARD] [SHUTDOWN-CRASH-TERMINATE] Forcing non-zero TerminateProcess(0x%08lX) at %s (rtlShutdown=%d).",
                 static_cast<unsigned long>(exceptionCode != 0 ? exceptionCode : 0xC0000005u),
                 locationBuf,
                 rtlShutdown ? 1 : 0);
        WriteSentinelLineNoAllocBestEffort(termLog);
        ::TerminateProcess(::GetCurrentProcess(), exceptionCode != 0 ? exceptionCode : 0xC0000005u);
    }

    if (pPreviousFilter) {
        return pPreviousFilter(pExceptionInfo);
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

LONG WINAPI NativeGuardUnhandledExceptionFilter(PEXCEPTION_POINTERS pExceptionInfo) {
    return HandleExceptionInternal(pExceptionInfo, /*isTerminalFault=*/true);
}
// [KONGOR_VEH_CRASH_PATH_END]

extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_CaptureStackTrace(char* outBuf, uint32_t maxLen) {
    if (!outBuf || maxLen == 0) return 0;
    std::string trace = Kongor::StackTrace::CaptureCurrentStackTrace(1, 32);
    size_t copyLen = (trace.size() < static_cast<size_t>(maxLen - 1)) ? trace.size() : static_cast<size_t>(maxLen - 1);
    std::memcpy(outBuf, trace.data(), copyLen);
    outBuf[copyLen] = '\0';
    return static_cast<uint32_t>(copyLen);
}

// ============================================================================
// Dynamic AOB (Array of Bytes) Pattern Scanner & Offset Cache
// ============================================================================
struct NativeAobSignature {
    std::vector<uint8_t> pattern;
    std::vector<uint8_t> mask; // 1 = exact match, 0 = wildcard
};

static NativeAobSignature ParseAobSignature(const char* idaSig) {
    NativeAobSignature sig;
    if (!idaSig) return sig;
    const char* p = idaSig;
    while (*p) {
        while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
        if (!*p) break;
        if (*p == '?') {
            sig.pattern.push_back(0);
            sig.mask.push_back(0);
            p++;
            if (*p == '?') p++;
        } else {
            char hex[3] = {0};
            hex[0] = *p++;
            if (*p && *p != ' ' && *p != '\t') {
                hex[1] = *p++;
            }
            uint8_t val = (uint8_t)strtoul(hex, nullptr, 16);
            sig.pattern.push_back(val);
            sig.mask.push_back(1);
        }
    }
    return sig;
}

static uintptr_t AobScanBuffer(const uint8_t* pStart, size_t size, const NativeAobSignature& sig) {
    if (!pStart || size < sig.pattern.size() || sig.pattern.empty()) return 0;
    size_t patLen = sig.pattern.size();
    size_t maxOffset = size - patLen;

    size_t firstExact = 0;
    while (firstExact < patLen && !sig.mask[firstExact]) firstExact++;
    if (firstExact == patLen) return (uintptr_t)pStart;

    uint8_t anchor = sig.pattern[firstExact];
    const uint8_t* pPat = sig.pattern.data();
    const uint8_t* pMask = sig.mask.data();

    for (size_t i = 0; i <= maxOffset; ++i) {
        if (pStart[i + firstExact] != anchor) continue;

        bool match = true;
        for (size_t j = 0; j < patLen; ++j) {
            if (pMask[j] && pStart[i + j] != pPat[j]) {
                match = false;
                break;
            }
        }
        if (match) return (uintptr_t)(pStart + i);
    }
    return 0;
}

static uintptr_t AobScanModule(HMODULE hMod, const char* idaSig) {
    if (!hMod || !idaSig) return 0;
    PBYTE pBase = (PBYTE)hMod;
    PIMAGE_DOS_HEADER pDos = (PIMAGE_DOS_HEADER)pBase;
    if (pDos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    PIMAGE_NT_HEADERS pNt = (PIMAGE_NT_HEADERS)(pBase + pDos->e_lfanew);
    if (pNt->Signature != IMAGE_NT_SIGNATURE) return 0;

    size_t imageSize = pNt->OptionalHeader.SizeOfImage;
    NativeAobSignature sig = ParseAobSignature(idaSig);
    return AobScanBuffer(pBase, imageSize, sig);
}

extern "C" __declspec(dllexport) uintptr_t Kongor_NativeGuard_AobScan(const char* moduleName, const char* idaSignature) {
    HMODULE hMod = moduleName ? GetModuleHandleA(moduleName) : GetModuleHandleA(NULL);
    if (!hMod) return 0;
    return AobScanModule(hMod, idaSignature);
}

struct DynamicVidD3D9CrashSites {
    bool scanned = false;
    uintptr_t site1_Start = 0x270C0;
    uintptr_t site1_End   = 0x270F0;
    uintptr_t site1_Recov = 0x272F7;

    uintptr_t site2_Start = 0x28A90;
    uintptr_t site2_End   = 0x28AB5;
    uintptr_t site2_Recov = 0x28CDF;

    uintptr_t site3_Start = 0x29100;
    uintptr_t site3_End   = 0x29125;
    uintptr_t site3_Recov = 0x29355;

    uintptr_t site4_Start = 0x293D0;
    uintptr_t site4_End   = 0x293F0;
    uintptr_t site4_Recov = 0x2961C;
};
static DynamicVidD3D9CrashSites g_vidD3d9Sites;

struct DynamicGameSharedCrashSites {
    bool scanned = false;
    uintptr_t timeOfDay_Start = 0x569CE0;
    uintptr_t timeOfDay_End   = 0x569D70;
    uintptr_t timeOfDay_Recov = 0x569D6F;
};
static DynamicGameSharedCrashSites g_gameSharedSites;

struct DynamicK2CrashSites {
    bool scanned = false;
    uintptr_t stopChannel_Start = 0x9D9CD0;
    uintptr_t stopChannel_End   = 0x9D9CE0;
    uintptr_t stopChannel_Recov = 0x9D9CE5;
    uintptr_t luaBarrier_Start  = 0xCB6820;
    uintptr_t luaBarrier_End    = 0xCB6845;
    uintptr_t luaBarrier_Recov  = 0xCB68E4;
    uintptr_t modelAnimCount_Start = 0x94F1E0;
    uintptr_t modelAnimCount_End   = 0x94F45A;
    uintptr_t modelAnimCount_Recov = 0x94F45B;
    uintptr_t luaPopEnv_Start   = 0x92EE70;
    uintptr_t luaPopEnv_End     = 0x92EEF0;
    uintptr_t luaPopEnv_Recov   = 0x92F006;
};
static DynamicK2CrashSites g_k2Sites;

struct DynamicCGameCrashSites {
    bool scanned = false;
    uintptr_t disconnect_Start = 0x1E5DE0;
    uintptr_t disconnect_End   = 0x1E5DF4;
    uintptr_t disconnect_Recov = 0x1E5DED;
    uintptr_t worldTeardown_Start = 0xB1D10;
    uintptr_t worldTeardown_End   = 0xB1D1D;
    uintptr_t worldTeardown_Recov = 0xB1D1D;
};
static DynamicCGameCrashSites g_cgameSites;

static bool SafeReadPtr(uintptr_t addr, DWORD64* outVal) {
    if (!addr || !outVal) return false;
    __try {
        *outVal = *(DWORD64*)addr;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool SafeReadInsnBytes(const uint8_t* addr, size_t maxBytes, uint8_t outBuf[15], size_t* outRead) noexcept {
    if (!addr || maxBytes == 0 || !outRead) return false;
    *outRead = 0;
    size_t toRead = (maxBytes < 15) ? maxBytes : 15;
    size_t n = 0;
    __try {
        for (; n < toRead; ++n) {
            outBuf[n] = addr[n];
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Page boundary hit after reading n bytes
    }
    *outRead = n;
    return n > 0;
}

static bool IsExecutableCommittedAddress(uintptr_t addr) noexcept {
    if (addr < 0x10000ULL || Kongor_IsAmd64NonCanonicalAddress(addr)) {
        return false;
    }
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(reinterpret_cast<LPCVOID>(addr), &mbi, sizeof(mbi)) == 0) {
        return false;
    }
    if (mbi.State != MEM_COMMIT) {
        return false;
    }
    const DWORD prot = mbi.Protect;
    if ((prot & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
        return false;
    }
    const DWORD execMask = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return (prot & execMask) != 0;
}

// ============================================================================
// [L2-P0-01] x64 Single-Instruction Length Disassembler (Zero-Alloc, Control-Flow Refusal)
// Returns exact instruction byte length [1..15] for non-control-flow instructions,
// or 0 if unreadable, unknown, stack-pointer-mutating, or control-flow (RET/CALL/JMP/Jcc/INT/SYSCALL).
// ============================================================================
uint32_t Kongor_DecodeSingleInsnLength64(const uint8_t* code, size_t maxBytes) noexcept {
    uint8_t buf[15] = {0};
    size_t n = 0;
    if (!SafeReadInsnBytes(code, maxBytes, buf, &n) || n == 0) {
        return 0;
    }

    size_t pos = 0;
    bool has66 = false;
    bool has67 = false;
    uint8_t rex = 0;

    // 1. Legacy prefixes (at most 4)
    while (pos < n && pos < 4) {
        uint8_t b = buf[pos];
        if (b == 0x66) { has66 = true; ++pos; }
        else if (b == 0x67) { has67 = true; ++pos; }
        else if (b == 0xF0 || b == 0xF2 || b == 0xF3 ||
                 b == 0x26 || b == 0x2E || b == 0x36 || b == 0x3E ||
                 b == 0x64 || b == 0x65) {
            ++pos;
        } else {
            break;
        }
    }
    if (pos >= n) return 0;

    // 2. Optional REX prefix (0x40..0x4F) immediately preceding opcode
    if (buf[pos] >= 0x40 && buf[pos] <= 0x4F) {
        rex = buf[pos++];
        if (pos >= n) return 0;
    }

    bool hasModRm = false;
    size_t immBytes = 0;
    uint8_t opcode = 0;
    uint8_t mapId = 0; // 0 = primary, 1 = 0F, 2 = 0F 38, 3 = 0F 3A

    // 3. Check 2-byte (C5) or 3-byte (C4) VEX encoding (invalid if REX was present)
    if (rex == 0 && (buf[pos] == 0xC5 || buf[pos] == 0xC4)) {
        if (buf[pos] == 0xC5) {
            if (pos + 3 > n) return 0; // C5 + vex1 + opcode (+ modrm typically)
            mapId = 1; // implied 0F map
            pos += 2;
            opcode = buf[pos++];
        } else {
            if (pos + 4 > n) return 0; // C4 + vex1 + vex2 + opcode
            mapId = static_cast<uint8_t>(buf[pos + 1] & 0x1F);
            if (mapId < 1 || mapId > 3) return 0;
            pos += 3;
            opcode = buf[pos++];
        }

        if (mapId == 1 && (opcode == 0x77)) {
            // VZEROUPPER / VZEROALL: no ModRM, never memory-faults
            return 0;
        }
        hasModRm = true;
        if (mapId == 3) {
            immBytes = 1;
        } else if (mapId == 1) {
            if ((opcode >= 0x70 && opcode <= 0x73) || opcode == 0xC2 ||
                opcode == 0xC4 || opcode == 0xC5 || opcode == 0xC6) {
                immBytes = 1;
            }
        }
    } else {
        opcode = buf[pos++];
        if (opcode == 0x0F) {
            if (pos >= n) return 0;
            uint8_t op2 = buf[pos++];
            if (op2 == 0x38) {
                if (pos >= n) return 0;
                mapId = 2;
                opcode = buf[pos++];
                hasModRm = true;
                immBytes = 0;
            } else if (op2 == 0x3A) {
                if (pos >= n) return 0;
                mapId = 3;
                opcode = buf[pos++];
                hasModRm = true;
                immBytes = 1;
            } else {
                mapId = 1;
                opcode = op2;
                // Explicitly reject control-flow, privileged, trap, and stack opcodes in 0F map
                if (opcode == 0x05 || opcode == 0x07 || opcode == 0x0B ||
                    opcode == 0x34 || opcode == 0x35 ||
                    (opcode >= 0x80 && opcode <= 0x8F) ||
                    opcode == 0xA0 || opcode == 0xA1 || opcode == 0xA8 || opcode == 0xA9) {
                    return 0;
                }
                if ((opcode >= 0x10 && opcode <= 0x1F) ||
                    (opcode >= 0x28 && opcode <= 0x2F) ||
                    (opcode >= 0x40 && opcode <= 0x6F) ||
                    (opcode >= 0x74 && opcode <= 0x76) ||
                    (opcode >= 0x7C && opcode <= 0x7F) ||
                    (opcode >= 0x90 && opcode <= 0x9F) ||
                    opcode == 0xA3 || opcode == 0xA5 ||
                    opcode == 0xAB || (opcode >= 0xAD && opcode <= 0xAF) ||
                    (opcode >= 0xB0 && opcode <= 0xB9) ||
                    (opcode >= 0xBB && opcode <= 0xBF) ||
                    opcode == 0xC0 || opcode == 0xC1 || opcode == 0xC3 || opcode == 0xC7 ||
                    (opcode >= 0xD0 && opcode <= 0xFE)) {
                    hasModRm = true;
                    immBytes = 0;
                } else if ((opcode >= 0x70 && opcode <= 0x73) ||
                           opcode == 0xA4 || opcode == 0xAC || opcode == 0xBA ||
                           opcode == 0xC2 || opcode == 0xC4 || opcode == 0xC5 || opcode == 0xC6) {
                    hasModRm = true;
                    immBytes = 1;
                } else {
                    return 0;
                }
            }
        } else {
            mapId = 0;
            // Explicitly reject ALL control-flow, stack-pointer-mutating, and trap opcodes in primary map
            if ((opcode >= 0x50 && opcode <= 0x5F) || // PUSH/POP reg
                opcode == 0x68 || opcode == 0x6A ||   // PUSH imm
                (opcode >= 0x70 && opcode <= 0x7F) || // Jcc rel8
                opcode == 0x82 || opcode == 0x8F ||   // invalid / POP r/m
                opcode == 0x9A || opcode == 0x9C || opcode == 0x9D ||
                opcode == 0xC2 || opcode == 0xC3 || opcode == 0xCA || opcode == 0xCB || // RET
                opcode == 0xC8 || opcode == 0xC9 ||   // ENTER / LEAVE
                (opcode >= 0xCC && opcode <= 0xCF) || // INT / IRET
                (opcode >= 0xE0 && opcode <= 0xEB) || // LOOP / CALL / JMP
                opcode == 0xF1 || opcode == 0xF4) {   // ICEBP / HLT
                return 0;
            }

            if ((opcode <= 0x3B && (opcode & 0x07) <= 0x03) ||
                opcode == 0x63 ||
                (opcode >= 0x84 && opcode <= 0x8B) ||
                opcode == 0x8D ||
                (opcode >= 0xD0 && opcode <= 0xD3)) {
                hasModRm = true;
                immBytes = 0;
            } else if (opcode == 0x6B || opcode == 0x80 || opcode == 0x83 ||
                       opcode == 0xC0 || opcode == 0xC1) {
                hasModRm = true;
                immBytes = 1;
            } else if (opcode == 0x69 || opcode == 0x81) {
                hasModRm = true;
                immBytes = has66 ? 2 : 4;
            } else if (opcode == 0xC6 || opcode == 0xC7 ||
                       opcode == 0xF6 || opcode == 0xF7 ||
                       opcode == 0xFE || opcode == 0xFF) {
                hasModRm = true;
                // immBytes & group validity resolved once ModRM.reg is inspected below
            } else if ((opcode >= 0xA4 && opcode <= 0xA7) || (opcode >= 0xAA && opcode <= 0xAF)) {
                hasModRm = false;
                immBytes = 0;
            } else if (opcode >= 0xA0 && opcode <= 0xA3) {
                hasModRm = false;
                immBytes = has67 ? 4 : 8;
            } else if (opcode >= 0xB0 && opcode <= 0xB7) {
                hasModRm = false;
                immBytes = 1;
            } else if (opcode >= 0xB8 && opcode <= 0xBF) {
                hasModRm = false;
                immBytes = ((rex & 0x08) != 0) ? 8 : (has66 ? 2 : 4);
            } else {
                return 0;
            }
        }
    }

    if (hasModRm) {
        if (pos >= n) return 0;
        uint8_t modrm = buf[pos++];
        uint8_t mod = static_cast<uint8_t>((modrm >> 6) & 0x03);
        uint8_t reg = static_cast<uint8_t>((modrm >> 3) & 0x07);
        uint8_t rm  = static_cast<uint8_t>(modrm & 0x07);

        if (mapId == 0) {
            if (opcode == 0xC6) {
                if (reg != 0) return 0;
                immBytes = 1;
            } else if (opcode == 0xC7) {
                if (reg != 0) return 0;
                immBytes = has66 ? 2 : 4;
            } else if (opcode == 0xF6) {
                immBytes = (reg <= 1) ? 1 : 0;
            } else if (opcode == 0xF7) {
                immBytes = (reg <= 1) ? (has66 ? 2 : 4) : 0;
            } else if (opcode == 0xFE) {
                if (reg > 1) return 0;
            } else if (opcode == 0xFF) {
                // FF /2 = CALL r/m64, /3 = CALL m16:64, /4 = JMP r/m64, /5 = JMP m16:64, /6 = PUSH r/m64
                // Refuse all control-flow and stack-mutating sub-opcodes; allow only /0 INC and /1 DEC.
                if (reg > 1) return 0;
            }
        }

        size_t dispBytes = 0;
        if (mod != 3) {
            if (rm == 4) {
                if (pos >= n) return 0;
                uint8_t sib = buf[pos++];
                uint8_t base = static_cast<uint8_t>(sib & 0x07);
                if (mod == 0 && base == 5) dispBytes = 4;
                else if (mod == 1) dispBytes = 1;
                else if (mod == 2) dispBytes = 4;
            } else {
                if (mod == 0 && rm == 5) dispBytes = 4; // RIP-relative disp32
                else if (mod == 1) dispBytes = 1;
                else if (mod == 2) dispBytes = 4;
            }
        }
        pos += dispBytes;
    }

    pos += immBytes;
    if (pos == 0 || pos > n || pos > 15) {
        return 0;
    }
    return static_cast<uint32_t>(pos);
}

// ============================================================================
// [L2-P0-01] Zero-Alloc Static BSS Per-(TID, RIP) Recovery Retry Cap (Max 3 / 5s)
// ============================================================================
struct VehRecoverySlot {
    uint64_t key;       // non-zero hashed/combined (tid, rip)
    uint64_t firstMs;   // GetTickCount64() of window start
    uint32_t count;     // recoveries granted inside current window
};

static constexpr size_t kVehRecoveryTableSize = 64;
static constexpr uint32_t kVehMaxRecoveriesPerWindow = 3;
static constexpr uint64_t kVehRecoveryWindowMs = 5000ULL;
static VehRecoverySlot g_vehRecoveryTable[kVehRecoveryTableSize] = {};
static std::atomic<long> g_vehRecoverySpinLock{0};

void Kongor_ResetVehRecoveryBudgetTable() noexcept {
    while (g_vehRecoverySpinLock.exchange(1L, std::memory_order_acquire) != 0L) {
        YieldProcessor();
    }
    memset(g_vehRecoveryTable, 0, sizeof(g_vehRecoveryTable));
    g_vehRecoverySpinLock.store(0L, std::memory_order_release);
}

bool Kongor_TryRecordAndAllowVehRecovery(uint32_t threadId, uintptr_t rip) noexcept {
    if (rip == 0) return false;
    uint64_t key = (static_cast<uint64_t>(rip) ^ (static_cast<uint64_t>(threadId) * 0x9E3779B97F4A7C15ULL));
    if (key == 0ULL) key = 1ULL;

    const uint64_t nowMs = GetTickCount64();

    while (g_vehRecoverySpinLock.exchange(1L, std::memory_order_acquire) != 0L) {
        YieldProcessor();
    }

    bool allowed = false;
    size_t startIdx = static_cast<size_t>(key % kVehRecoveryTableSize);
    size_t reclaimIdx = kVehRecoveryTableSize;

    for (size_t probe = 0; probe < kVehRecoveryTableSize; ++probe) {
        size_t idx = (startIdx + probe) % kVehRecoveryTableSize;
        VehRecoverySlot& slot = g_vehRecoveryTable[idx];
        if (slot.key == key) {
            if ((nowMs - slot.firstMs) > kVehRecoveryWindowMs) {
                slot.firstMs = nowMs;
                slot.count = 1;
                allowed = true;
            } else if (slot.count < kVehMaxRecoveriesPerWindow) {
                ++slot.count;
                allowed = true;
            } else {
                allowed = false;
            }
            reclaimIdx = kVehRecoveryTableSize;
            break;
        }
        if (reclaimIdx == kVehRecoveryTableSize &&
            (slot.key == 0ULL || (nowMs - slot.firstMs) > kVehRecoveryWindowMs)) {
            reclaimIdx = idx;
        }
        if (slot.key == 0ULL) {
            break;
        }
    }

    if (!allowed && reclaimIdx < kVehRecoveryTableSize) {
        // Key was not present yet; claim empty/expired slot for hit #1
        VehRecoverySlot& slot = g_vehRecoveryTable[reclaimIdx];
        if (slot.key != key) {
            slot.key = key;
            slot.firstMs = nowMs;
            slot.count = 1;
            allowed = true;
        }
    }

    g_vehRecoverySpinLock.store(0L, std::memory_order_release);
    return allowed;
}

static bool VerifyCallerReturnSite(uintptr_t candidateRet, uintptr_t faultingRip) noexcept {
    if (candidateRet == 0 || candidateRet == faultingRip || !IsExecutableCommittedAddress(candidateRet)) {
        return false;
    }
    uint8_t beforeBuf[15] = {0};
    size_t readLen = 0;
    const size_t probeLen = 8;
    if (candidateRet <= probeLen) return false;
    if (!SafeReadInsnBytes(reinterpret_cast<const uint8_t*>(candidateRet - probeLen), probeLen, beforeBuf, &readLen) || readLen < 2) {
        return false;
    }
    return Kongor_ValidatePrecedingCallInstruction(beforeBuf, readLen, nullptr, 0);
}

// [L2-P0-01] Safe x64 caller unwind via RtlLookupFunctionEntry + RtlVirtualUnwind
// (with CALL-validated [Rsp] leaf-function fallback only when pRf == nullptr).
bool Kongor_TrySafeUnwindCallerFrame64(CONTEXT* ioCtx) noexcept {
    if (!ioCtx || ioCtx->Rip == 0 || ioCtx->Rsp == 0) {
        return false;
    }
    const uintptr_t origRip = static_cast<uintptr_t>(ioCtx->Rip);
    __try {
        DWORD64 imageBase = 0;
        PRUNTIME_FUNCTION pRf = RtlLookupFunctionEntry(ioCtx->Rip, &imageBase, nullptr);
        if (pRf != nullptr && imageBase != 0) {
            CONTEXT unwindCtx = *ioCtx;
            PVOID handlerData = nullptr;
            DWORD64 establisherFrame = 0;
            RtlVirtualUnwind(
                UNW_FLAG_NHANDLER,
                imageBase,
                unwindCtx.Rip,
                pRf,
                &unwindCtx,
                &handlerData,
                &establisherFrame,
                nullptr);
            if (VerifyCallerReturnSite(static_cast<uintptr_t>(unwindCtx.Rip), origRip) &&
                unwindCtx.Rsp >= ioCtx->Rsp) {
                *ioCtx = unwindCtx;
                return true;
            }
            return false;
        }

        // True x64 leaf function (no .pdata entry -> zero stack frame allocation): return address is at [Rsp]
        DWORD64 leafRet = 0;
        if (SafeReadPtr(static_cast<uintptr_t>(ioCtx->Rsp), &leafRet) &&
            VerifyCallerReturnSite(static_cast<uintptr_t>(leafRet), origRip)) {
            ioCtx->Rip = leafRet;
            ioCtx->Rsp += 8;
            return true;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return false;
}

static bool CommitSafeVehRecovery(
    PEXCEPTION_POINTERS pExceptionInfo,
    uintptr_t origRip,
    uintptr_t newRip,
    const char* siteLabel) noexcept
{
    if (!pExceptionInfo || !pExceptionInfo->ContextRecord) {
        return false;
    }
    if (newRip == 0 || newRip == origRip || !IsExecutableCommittedAddress(newRip)) {
        char msg[256] = {0};
        snprintf(msg, sizeof(msg),
            "[KONGOR NATIVE GUARD] VEH refused unsafe recovery (%s): origRip=0x%p newRip=0x%p (failing closed).\n",
            siteLabel ? siteLabel : "unknown", (void*)origRip, (void*)newRip);
        OutputDebugStringA(msg);
        return false;
    }
    if (!Kongor_TryRecordAndAllowVehRecovery(GetCurrentThreadId(), origRip)) {
        char msg[256] = {0};
        snprintf(msg, sizeof(msg),
            "[KONGOR NATIVE GUARD] VEH retry budget exhausted (%s) at RIP=0x%p (>%u faults/5s) — failing closed to minidump.\n",
            siteLabel ? siteLabel : "unknown", (void*)origRip, kVehMaxRecoveriesPerWindow);
        OutputDebugStringA(msg);
        return false;
    }
    pExceptionInfo->ContextRecord->Rip = static_cast<DWORD64>(newRip);
    return true;
}

extern "C" __declspec(dllexport) uint32_t __cdecl Kongor_NativeGuard_DecodeInsnLength64(const uint8_t* code, uint32_t maxBytes) {
    return Kongor_DecodeSingleInsnLength64(code, static_cast<size_t>(maxBytes));
}

extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_TestVehRecoveryBudget(uint32_t threadId, uint64_t rip) {
    return Kongor_TryRecordAndAllowVehRecovery(threadId, static_cast<uintptr_t>(rip)) ? 1 : 0;
}

extern "C" __declspec(dllexport) void __cdecl Kongor_NativeGuard_ResetVehRecoveryBudget() {
    Kongor_ResetVehRecoveryBudgetTable();
}

extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_VerifyIpcToken(
    const char* candidatePayload,
    uint32_t candidateLen,
    const char* expectedToken,
    uint32_t expectedLen)
{
    return Kongor_VerifyIpcAuthToken(
        candidatePayload,
        static_cast<size_t>(candidateLen),
        expectedToken,
        static_cast<size_t>(expectedLen)) ? 1 : 0;
}

LONG WINAPI NativeGuardVectoredHandler(PEXCEPTION_POINTERS pExceptionInfo) {
    static thread_local bool s_bInVeh = false;
    if (s_bInVeh || g_inProtectedSehDepth > 0) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    s_bInVeh = true;
    struct VehScopeExitGuard {
        ~VehScopeExitGuard() { s_bInVeh = false; }
    } vehGuard;

    if (!pExceptionInfo || !pExceptionInfo->ExceptionRecord) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    DWORD code = pExceptionInfo->ExceptionRecord->ExceptionCode;
    // (#167) Ignore benign OutputDebugString / SetThreadName exceptions
    if (IsBenignDebugOrThreadNameException(code)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    const bool rtlShutdown = Kongor_IsRtlDllShutdownInProgress();
    const bool shutdownActive = rtlShutdown ||
                                g_quitInitiated.load(std::memory_order_acquire) ||
                                g_shutdownStarted.load(std::memory_order_acquire);
    const bool isUnrecoverable = Kongor_IsUnrecoverableFatalExceptionCode(code);
    const bool isShutdownFatal = shutdownActive && Kongor_IsShutdownFatalExceptionCode(code);

    if (isUnrecoverable || isShutdownFatal || code == EXCEPTION_ACCESS_VIOLATION) {
        uintptr_t faultAddr = (uintptr_t)pExceptionInfo->ExceptionRecord->ExceptionAddress;
        uintptr_t origRip = (pExceptionInfo->ContextRecord)
            ? static_cast<uintptr_t>(pExceptionInfo->ContextRecord->Rip)
            : faultAddr;
        HMODULE hMod = NULL;
        char modPath[MAX_PATH] = {0};
        if (!rtlShutdown &&
            code == EXCEPTION_ACCESS_VIOLATION &&
            pExceptionInfo->ContextRecord != nullptr &&
            GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               (LPCSTR)faultAddr, &hMod) && GetModuleFileNameA(hMod, modPath, MAX_PATH) > 0) {
            char pathLower[MAX_PATH] = {0};
            for (size_t i = 0; i < MAX_PATH - 1 && modPath[i] != '\0'; ++i) {
                char ch = modPath[i];
                pathLower[i] = (ch >= 'A' && ch <= 'Z') ? (char)(ch + ('a' - 'A')) : ch;
            }

            if (strstr(pathLower, "libcef") != nullptr || strstr(pathLower, "cef") != nullptr || strstr(pathLower, "chrome_elf") != nullptr) {
                // [L2-P0-01] Fail closed: returning EXCEPTION_CONTINUE_EXECUTION without a verified Rip advance
                // causes a 100% CPU infinite fault storm. Write minidump/stack trace and let SEH/OS handle it.
                OutputDebugStringA("[KONGOR NATIVE GUARD] CEF access violation has no verified recovery target — failing closed to minidump.\n");
            } else if (strstr(pathLower, "game_shared_x64") != nullptr) {
                if (!g_gameSharedSites.scanned) {
                    g_gameSharedSites.scanned = true;
                    uintptr_t tod = AobScanModule(hMod, "8B 05 ?? ?? ?? ?? 48 83 38 00 75 28 48 8B 05 ?? ?? ?? ?? C6 00 01 B9 01");
                    if (tod) {
                        uintptr_t rva = tod - (uintptr_t)hMod;
                        g_gameSharedSites.timeOfDay_Start = rva - 0x10;
                        g_gameSharedSites.timeOfDay_End   = rva + 0x90;
                        g_gameSharedSites.timeOfDay_Recov = rva + 0x8F;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved game_shared GetTimeOfDay crash site.\n");
                    }
                }

                uintptr_t relOffset = faultAddr - (uintptr_t)hMod;
                if (relOffset >= g_gameSharedSites.timeOfDay_Start && relOffset <= g_gameSharedSites.timeOfDay_End) {
                    uintptr_t recovRip = (uintptr_t)hMod + g_gameSharedSites.timeOfDay_Recov;
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, recovRip, "game_shared::GetTimeOfDay")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized IGame::GetTimeOfDay NULL match-state crash.\n");
                        pExceptionInfo->ContextRecord->Xmm0.Low = 0;
                        pExceptionInfo->ContextRecord->Xmm0.High = 0;
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                }
            } else if (strstr(pathLower, "cgame_x64") != nullptr) {
                if (!g_cgameSites.scanned) {
                    g_cgameSites.scanned = true;
                    uintptr_t dc = AobScanModule(hMod, "48 83 EC 28 48 8B 02 48 3B 42 08 75 07 32 C0 48 83 C4 28 C3");
                    if (dc) {
                        uintptr_t rva = dc - (uintptr_t)hMod;
                        g_cgameSites.disconnect_Start = rva;
                        g_cgameSites.disconnect_End   = rva + 0x14;
                        g_cgameSites.disconnect_Recov = rva + 0x0D; // 0x1E5DED (xor al, al; add rsp, 0x28; ret)
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved cgame disconnect crash site (ISSUE-303).\n");
                    }
                    uintptr_t wt = AobScanModule(hMod, "48 8B 4B 10 48 8B 01 FF 90 78 04 00 00 48 8B 1B");
                    if (wt) {
                        uintptr_t rva = wt - (uintptr_t)hMod;
                        g_cgameSites.worldTeardown_Start = rva;
                        g_cgameSites.worldTeardown_End   = rva + 0x0D;
                        g_cgameSites.worldTeardown_Recov = rva + 0x0D; // 0xB1D1D (mov rbx, [rbx])
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved cgame world teardown crash site.\n");
                    }
                }

                uintptr_t relOffset = faultAddr - (uintptr_t)hMod;
                uintptr_t relRip = ((uintptr_t)pExceptionInfo->ContextRecord->Rip - (uintptr_t)hMod);
                uintptr_t targetAddr = relRip ? relRip : relOffset;

                if (targetAddr >= g_cgameSites.disconnect_Start && targetAddr <= g_cgameSites.disconnect_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_cgameSites.disconnect_Recov, "cgame::DisconnectCheck")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized cgame disconnect null container dereference (ISSUE-303).\n");
                        pExceptionInfo->ContextRecord->Rax = 0;
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (targetAddr >= g_cgameSites.worldTeardown_Start && targetAddr <= g_cgameSites.worldTeardown_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_cgameSites.worldTeardown_Recov, "cgame::WorldTeardownList")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized cgame LoadWorldResources dangling entity vtable.\n");
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                }
            } else if (strstr(pathLower, "k2_x64") != nullptr) {
                if (!g_k2Sites.scanned) {
                    g_k2Sites.scanned = true;
                    uintptr_t sc = AobScanModule(hMod, "01 48 8B 43 08 48 2B 03 48 C1 F8 05 48 83 F8 01");
                    if (sc) {
                        uintptr_t rva = sc - (uintptr_t)hMod;
                        g_k2Sites.stopChannel_Start = rva - 0x10;
                        g_k2Sites.stopChannel_End   = rva + 0x10;
                        g_k2Sites.stopChannel_Recov = rva + 0x15;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved k2 StopChannel crash site.\n");
                    }
                    uintptr_t pe = AobScanModule(hMod, "48 89 74 24 10 57 48 83 EC 30 8B 79 50 48 8B F1 85 FF");
                    if (pe) {
                        uintptr_t rva = pe - (uintptr_t)hMod;
                        g_k2Sites.luaPopEnv_Start = rva;
                        g_k2Sites.luaPopEnv_End   = rva + 0x60;
                        g_k2Sites.luaPopEnv_Recov = rva + 0x196;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved k2 PopGlobalEnvironment crash site.\n");
                    }
                }

                uintptr_t relOffset = faultAddr - (uintptr_t)hMod;
                if (relOffset >= g_k2Sites.stopChannel_Start && relOffset <= g_k2Sites.stopChannel_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_k2Sites.stopChannel_Recov, "k2::StopChannel")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized CSoundManager::StopChannel NULL sound pointer.\n");
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (relOffset >= g_k2Sites.luaBarrier_Start && relOffset <= g_k2Sites.luaBarrier_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_k2Sites.luaBarrier_Recov, "k2::LuaGcBarrier")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized CLuaScriptManager NULL GC closure barrier crash at k2_x64+0xcb6831.\n");
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (relOffset >= g_k2Sites.modelAnimCount_Start && relOffset <= g_k2Sites.modelAnimCount_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_k2Sites.modelAnimCount_Recov, "k2::GetValidAnimCount")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized CModelPanel::GetValidAnimCount NULL model pointer at k2_x64+0x94f1e0.\n");
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (relOffset >= g_k2Sites.luaPopEnv_Start && relOffset <= g_k2Sites.luaPopEnv_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_k2Sites.luaPopEnv_Recov, "k2::PopGlobalEnvironment")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: neutralized CLuaScriptManager::PopGlobalEnvironment NULL stack crash.\n");
                        DWORD64 savedRbx = 0;
                        if (SafeReadPtr(pExceptionInfo->ContextRecord->Rsp + 0x40, &savedRbx)) {
                            pExceptionInfo->ContextRecord->Rbx = savedRbx;
                        }
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                }
            } else if (strstr(pathLower, "vid_d3d9_x64") != nullptr) {
                if (!g_vidD3d9Sites.scanned) {
                    g_vidD3d9Sites.scanned = true;
                    uintptr_t s1 = AobScanModule(hMod, "01 41 B8 07 00 00 00 41 8B D5 FF 90 28 02 00 00");
                    if (s1) {
                        uintptr_t rva = s1 - (uintptr_t)hMod;
                        g_vidD3d9Sites.site1_Start = rva - 0x10;
                        g_vidD3d9Sites.site1_End   = rva + 0x30;
                        g_vidD3d9Sites.site1_Recov = rva + 0x237;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved vid_d3d9 Site 1 sampler hook offset.\n");
                    }
                    uintptr_t s2 = AobScanModule(hMod, "3B EA 8B 74 24 58 44 8B A5 E8 00 00 00");
                    if (s2) {
                        uintptr_t rva = s2 - (uintptr_t)hMod;
                        g_vidD3d9Sites.site2_Start = rva - 0x10;
                        g_vidD3d9Sites.site2_End   = rva + 0x25;
                        g_vidD3d9Sites.site2_Recov = rva + 0x24F;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved vid_d3d9 Site 2 line sampler hook offset.\n");
                    }
                    uintptr_t s3 = AobScanModule(hMod, "48 89 7E 18 0F 10 00 0F 11 06 0F 10 48 10 0F 11 4E 10");
                    if (s3) {
                        uintptr_t rva = s3 - (uintptr_t)hMod;
                        g_vidD3d9Sites.site3_Start = rva - 0x10;
                        g_vidD3d9Sites.site3_End   = rva + 0x25;
                        g_vidD3d9Sites.site3_Recov = rva + 0x255;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved vid_d3d9 Site 3 sampler 0 hook offset.\n");
                    }
                    uintptr_t s4 = AobScanModule(hMod, "00 C6 00 01 8D 4E 01 FF 15 ?? ?? ?? ?? 48 8B 0D");
                    if (s4) {
                        uintptr_t rva = s4 - (uintptr_t)hMod;
                        g_vidD3d9Sites.site4_Start = rva - 0x10;
                        g_vidD3d9Sites.site4_End   = rva + 0x20;
                        g_vidD3d9Sites.site4_Recov = rva + 0x24C;
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Dynamic AOB: Resolved vid_d3d9 Site 4 sampler 1 mask hook offset.\n");
                    }
                }

                uintptr_t relOffset = faultAddr - (uintptr_t)hMod;
                uintptr_t relRip = ((uintptr_t)pExceptionInfo->ContextRecord->Rip - (uintptr_t)hMod);
                uintptr_t targetAddr = relRip ? relRip : relOffset;

                static std::atomic<uint32_t> s_vidD3d9OverflowCount{0};

                if (targetAddr >= g_vidD3d9Sites.site1_Start && targetAddr <= g_vidD3d9Sites.site1_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_vidD3d9Sites.site1_Recov, "vid_d3d9::Site1")) {
                        if (++s_vidD3d9OverflowCount <= 5) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery: neutralized vid_d3d9 mode 0 sampler overflow (Dynamic AOB).\n");
                        }
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (targetAddr >= g_vidD3d9Sites.site2_Start && targetAddr <= g_vidD3d9Sites.site2_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_vidD3d9Sites.site2_Recov, "vid_d3d9::Site2")) {
                        if (++s_vidD3d9OverflowCount <= 5) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery: neutralized vid_d3d9 line sampler overflow (Dynamic AOB).\n");
                        }
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (targetAddr >= g_vidD3d9Sites.site3_Start && targetAddr <= g_vidD3d9Sites.site3_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_vidD3d9Sites.site3_Recov, "vid_d3d9::Site3")) {
                        if (++s_vidD3d9OverflowCount <= 5) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery: neutralized vid_d3d9 sampler 0 overflow (Dynamic AOB).\n");
                        }
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else if (targetAddr >= g_vidD3d9Sites.site4_Start && targetAddr <= g_vidD3d9Sites.site4_End) {
                    if (CommitSafeVehRecovery(pExceptionInfo, origRip, (uintptr_t)hMod + g_vidD3d9Sites.site4_Recov, "vid_d3d9::Site4")) {
                        if (++s_vidD3d9OverflowCount <= 5) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery: neutralized vid_d3d9 sampler 1 mask overflow (Dynamic AOB).\n");
                        }
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                } else {
                    const bool vidBlitExcluded = Kongor_IsVidBlitBitmapVehExcluded(
                        targetAddr,
                        g_vidBlitHookInstalled.load(std::memory_order_acquire));
                    int32_t rdxVal = (int32_t)(pExceptionInfo->ContextRecord->Rdx & 0xFFFFFFFF);
                    int32_t r8Val  = (int32_t)(pExceptionInfo->ContextRecord->R8 & 0xFFFFFFFF);
                    if (vidBlitExcluded) {
                        static std::atomic<uint32_t> s_vidBlitVehSkipLogs{0};
                        if (s_vidBlitVehSkipLogs.fetch_add(1, std::memory_order_relaxed) < 8) {
                            char dbgMsg[256];
                            snprintf(dbgMsg, sizeof(dbgMsg),
                                "[KONGOR NATIVE GUARD] VEH skipped vid_d3d9::SamplerStageDecode inside hooked VidBlitBitmap (relRip=0x%llX RDX=%d R8=%d)\n",
                                static_cast<unsigned long long>(targetAddr), rdxVal, r8Val);
                            OutputDebugStringA(dbgMsg);
                        }
                    } else if (rdxVal < 0 || (uint32_t)rdxVal >= 32 || r8Val < 0 || (uint32_t)r8Val >= 32) {
                        uint32_t instLen = Kongor_DecodeSingleInsnLength64(reinterpret_cast<const uint8_t*>(origRip), 15);
                        if (instLen > 0 && CommitSafeVehRecovery(pExceptionInfo, origRip, origRip + instLen, "vid_d3d9::SamplerStageDecode")) {
                            if (++s_vidD3d9OverflowCount <= 10) {
                                char dbgMsg[256];
                                snprintf(dbgMsg, sizeof(dbgMsg), "[KONGOR NATIVE GUARD] VEH Intercepted negative/overflow sampler stage: RDX=%d, R8=%d at RIP=0x%p (+%uB)\n",
                                         rdxVal, r8Val, (void*)origRip, instLen);
                                OutputDebugStringA(dbgMsg);
                            }
                            if (rdxVal < 0 || (uint32_t)rdxVal >= 32) pExceptionInfo->ContextRecord->Rdx = 0;
                            if (r8Val < 0 || (uint32_t)r8Val >= 32) pExceptionInfo->ContextRecord->R8 = 0;
                            return EXCEPTION_CONTINUE_EXECUTION;
                        }
                    }

                    if (pExceptionInfo->ExceptionRecord->NumberParameters >= 2) {
                        ULONG_PTR accessType = pExceptionInfo->ExceptionRecord->ExceptionInformation[0];
                        ULONG_PTR faultingMem = pExceptionInfo->ExceptionRecord->ExceptionInformation[1];
                        if (accessType == 0 && (faultingMem < 0x10000 || faultingMem >= 0x7FFFFFFF0000ULL)) {
                            uint32_t instLen = Kongor_DecodeSingleInsnLength64(reinterpret_cast<const uint8_t*>(origRip), 15);
                            if (instLen > 0 && CommitSafeVehRecovery(pExceptionInfo, origRip, origRip + instLen, "vid_d3d9::InvalidTexReadDecode")) {
                                if (++s_vidD3d9OverflowCount <= 10) {
                                    char dbgMsg[256];
                                    snprintf(dbgMsg, sizeof(dbgMsg), "[KONGOR NATIVE GUARD] VEH Neutralized invalid texture read fault at mem 0x%p (RIP=0x%p +%uB)\n",
                                             (void*)faultingMem, (void*)origRip, instLen);
                                    OutputDebugStringA(dbgMsg);
                                }
                                pExceptionInfo->ContextRecord->Rax = 0;
                                return EXCEPTION_CONTINUE_EXECUTION;
                            }
                        }
                    }
                }
            } else if (strstr(pathLower, "avcodec") != nullptr) {
                // [L2-P0-01] Unwind caller safely via RtlLookupFunctionEntry + RtlVirtualUnwind;
                // never blindly pop *(uintptr_t*)Rsp when non-leaf stack allocation exists.
                CONTEXT unwindCtx = *pExceptionInfo->ContextRecord;
                if (Kongor_TrySafeUnwindCallerFrame64(&unwindCtx) &&
                    CommitSafeVehRecovery(pExceptionInfo, origRip, static_cast<uintptr_t>(unwindCtx.Rip), "avcodec::RtlVirtualUnwind")) {
                    OutputDebugStringA("[KONGOR NATIVE GUARD] Safe recovery engaged: unwound avcodec access violation via RtlVirtualUnwind.\n");
                    *pExceptionInfo->ContextRecord = unwindCtx;
                    pExceptionInfo->ContextRecord->Rax = (DWORD64)-22; // AVERROR(EINVAL)
                    return EXCEPTION_CONTINUE_EXECUTION;
                }
                OutputDebugStringA("[KONGOR NATIVE GUARD] avcodec fault could not be safely unwound — failing closed to minidump.\n");
            } else {
                char safeRecEnv[16] = {0};
                GetEnvironmentVariableA("KONGOR_SAFE_UI_RECOVERY", safeRecEnv, sizeof(safeRecEnv));
                if (strcmp(safeRecEnv, "1") == 0 &&
                    (strstr(pathLower, "vid_") != nullptr || strstr(pathLower, "vulkan") != nullptr)) {
                    // [L2-P0-01] Never continue execution at unchanged Rip; attempt validated caller unwind or fail closed.
                    CONTEXT unwindCtx = *pExceptionInfo->ContextRecord;
                    if (Kongor_TrySafeUnwindCallerFrame64(&unwindCtx) &&
                        CommitSafeVehRecovery(pExceptionInfo, origRip, static_cast<uintptr_t>(unwindCtx.Rip), "safe_ui_recovery::RtlVirtualUnwind")) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Safe UI/Render recovery engaged via validated caller unwind.\n");
                        *pExceptionInfo->ContextRecord = unwindCtx;
                        pExceptionInfo->ContextRecord->Rax = 0;
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                    OutputDebugStringA("[KONGOR NATIVE GUARD] KONGOR_SAFE_UI_RECOVERY has no verified unwind target — failing closed to minidump.\n");
                }
            }
        }

        // Release OS cursor clip before fatal/unrecovered exception propagates or dumps
        Kongor_ForceReleaseCursorEmergency();

        static std::atomic<uint32_t> s_unhandledAvDumps{0};
        if (g_inProtectedSehDepth <= 0) {
            if (isShutdownFatal || isUnrecoverable) {
                HandleExceptionInternal(pExceptionInfo, /*isTerminalFault=*/true);
            } else if (code == EXCEPTION_ACCESS_VIOLATION && s_unhandledAvDumps.fetch_add(1) < 10) {
                // Mid-session first-chance AV: write Tier-1 diagnostic dump without writing
                // last-crashed.<pid>.sentinel or terminating in case a frame SEH handler recovers it;
                // if genuinely unhandled, SetUnhandledExceptionFilter runs next with isTerminalFault=true.
                HandleExceptionInternal(pExceptionInfo, /*isTerminalFault=*/false);
            }
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

void __cdecl NativeGuardPurecallHandler() {
    OutputDebugStringA("[KONGOR NATIVE GUARD] CRASH: Pure virtual function call detected!\n");
    RaiseException(0xE0000001, EXCEPTION_NONCONTINUABLE, 0, NULL);
}

void __cdecl NativeGuardInvalidParameterHandler(
    const wchar_t* expression,
    const wchar_t* function,
    const wchar_t* file,
    unsigned int line,
    uintptr_t pReserved) {
    (void)expression; (void)function; (void)file; (void)line; (void)pReserved;
    OutputDebugStringA("[KONGOR NATIVE GUARD] CRASH: CRT Invalid Parameter detected!\n");
    RaiseException(0xE0000002, EXCEPTION_NONCONTINUABLE, 0, NULL);
}
