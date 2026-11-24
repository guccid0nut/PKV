// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard Lifecycle & Proxy Forwarding Orchestrator
// ============================================================================

#include "proxy.h"
#include "native_guard_internal.h"
#include "cursor_confinement.h"
#include "asset_prewarmer.h"
#include "vfs_loose_cache.h"
#include "job_pool.h"
#include "predictive_pathing.h"
#include "modern_archive_hook.h"
#include "runtime_patch_controller.h"
#include "kongor_assert.h"
#include "kongor_overflow.h"
#include "kongor_constpool.h"
#include "kongor_sstream.h"
#include "kongor_deterministic_math.h"
#include "kongor_shared_mem.h"
#include "kongor_lua_shared_mem.h"
#include "concurrentqueue.h"
#include "kongor_stacktrace.h"
#include "kongor_base_cmd.h"
#include "queue_arcade_hooks.h"
#include "vmt_hook_manager.h"
#include "entity_field_container.h"
#include "kongor_pipe_security.h"
#if defined(KONGOR_TRACY_ENABLE) || defined(KONGOR_ENABLE_TRACY)
#define KONGOR_TRACY_DEFINE_MARKER_EXPORT 1
#endif
#include "kongor_tracy.h"

#include <string>
#include <iostream>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <chrono>
#include <filesystem>
#include <cstdlib>
#include <crtdbg.h>
#include <dbghelp.h>
#include <psapi.h>
#include <objbase.h>
#include <cstdint>
#include <atomic>
#include <array>
#include <vector>
#include <algorithm>
#include <memory>
#include <mutex>
#include <mmsystem.h>
#include <intrin.h>

HMODULE hOriginalDll = NULL;
HMODULE g_hSelfModule = NULL;
LPTOP_LEVEL_EXCEPTION_FILTER pPreviousFilter = NULL;
static std::atomic<bool> g_mimallocActive{false};
static std::atomic<int> g_mimallocVersion{0};
static std::atomic<bool> g_hangDumpWrittenThisSession{false};
static DWORD g_mainProcessThreadId = 0;
bool g_isHeadlessProcess = false;
std::atomic<uint64_t> g_lastMainThreadHeartbeatTick{0};

static bool TryBeginSingleFlightShutdown() noexcept {
    return !g_shutdownStarted.exchange(true, std::memory_order_acq_rel);
}

// (#171) COM Apartment RAII Guard: tolerates RPC_E_CHANGED_MODE (0x80010106) and calls
// CoUninitialize() strictly only when CoInitializeEx returned S_OK or S_FALSE.
class KongorScopedComGuard {
public:
    explicit KongorScopedComGuard(DWORD dwCoInit = COINIT_MULTITHREADED) noexcept
        : m_hr(::CoInitializeEx(nullptr, dwCoInit)) {}

    ~KongorScopedComGuard() noexcept {
        if (SUCCEEDED(m_hr)) {
            ::CoUninitialize();
        }
    }

    KongorScopedComGuard(const KongorScopedComGuard&) = delete;
    KongorScopedComGuard& operator=(const KongorScopedComGuard&) = delete;

    bool IsComAvailable() const noexcept {
        return SUCCEEDED(m_hr) || m_hr == RPC_E_CHANGED_MODE;
    }

    bool OwnsApartmentInit() const noexcept {
        return SUCCEEDED(m_hr);
    }

    HRESULT Result() const noexcept {
        return m_hr;
    }

private:
    HRESULT m_hr;
};


extern "C" {
    FARPROC pfnGetFileVersionInfoA = NULL;
    FARPROC pfnGetFileVersionInfoByHandle = NULL;
    FARPROC pfnGetFileVersionInfoExA = NULL;
    FARPROC pfnGetFileVersionInfoExW = NULL;
    FARPROC pfnGetFileVersionInfoSizeA = NULL;
    FARPROC pfnGetFileVersionInfoSizeExA = NULL;
    FARPROC pfnGetFileVersionInfoSizeExW = NULL;
    FARPROC pfnGetFileVersionInfoSizeW = NULL;
    FARPROC pfnGetFileVersionInfoW = NULL;
    FARPROC pfnVerFindFileA = NULL;
    FARPROC pfnVerFindFileW = NULL;
    FARPROC pfnVerInstallFileA = NULL;
    FARPROC pfnVerInstallFileW = NULL;
    FARPROC pfnVerLanguageNameA = NULL;
    FARPROC pfnVerLanguageNameW = NULL;
    FARPROC pfnVerQueryValueA = NULL;
    FARPROC pfnVerQueryValueW = NULL;
}

void InitializeProxies(HMODULE hMod) {
    pfnGetFileVersionInfoA = GetProcAddress(hMod, "GetFileVersionInfoA");
    pfnGetFileVersionInfoByHandle = GetProcAddress(hMod, "GetFileVersionInfoByHandle");
    pfnGetFileVersionInfoExA = GetProcAddress(hMod, "GetFileVersionInfoExA");
    pfnGetFileVersionInfoExW = GetProcAddress(hMod, "GetFileVersionInfoExW");
    pfnGetFileVersionInfoSizeA = GetProcAddress(hMod, "GetFileVersionInfoSizeA");
    pfnGetFileVersionInfoSizeExA = GetProcAddress(hMod, "GetFileVersionInfoSizeExA");
    pfnGetFileVersionInfoSizeExW = GetProcAddress(hMod, "GetFileVersionInfoSizeExW");
    pfnGetFileVersionInfoSizeW = GetProcAddress(hMod, "GetFileVersionInfoSizeW");
    pfnGetFileVersionInfoW = GetProcAddress(hMod, "GetFileVersionInfoW");
    pfnVerFindFileA = GetProcAddress(hMod, "VerFindFileA");
    pfnVerFindFileW = GetProcAddress(hMod, "VerFindFileW");
    pfnVerInstallFileA = GetProcAddress(hMod, "VerInstallFileA");
    pfnVerInstallFileW = GetProcAddress(hMod, "VerInstallFileW");
    pfnVerLanguageNameA = GetProcAddress(hMod, "VerLanguageNameA");
    pfnVerLanguageNameW = GetProcAddress(hMod, "VerLanguageNameW");
    pfnVerQueryValueA = GetProcAddress(hMod, "VerQueryValueA");
    pfnVerQueryValueW = GetProcAddress(hMod, "VerQueryValueW");
}

static std::mutex s_sentinelMutex;
static std::ofstream s_sentinelStreams[2];
static bool s_sentinelInit = false;
static uint64_t s_lastSentinelFlushTick = 0;

void FlushSentinelLogs(bool tryOnly) {
    std::unique_lock<std::mutex> lock(s_sentinelMutex, std::defer_lock);
    if (tryOnly) {
        if (!lock.try_lock()) return;
    } else {
        lock.lock();
    }
    for (auto& ofs : s_sentinelStreams) {
        if (ofs.is_open()) {
            ofs.flush();
        }
    }
}

void WriteSentinelLine(const char* line) {
    if (!line) return;
    OutputDebugStringA(line);
    OutputDebugStringA("\n");

    std::lock_guard<std::mutex> lock(s_sentinelMutex);
    if (!s_sentinelInit) {
        s_sentinelInit = true;
        static const char* s_targetDirs[] = { "logs/crash", "userdata/logs/crash" };
        for (size_t i = 0; i < 2; ++i) {
            const char* dirStr = s_targetDirs[i];
            std::error_code ec;
            std::filesystem::create_directories(dirStr, ec);
            std::filesystem::path dir(dirStr);
            std::filesystem::path logPath = dir / "native_guard_sentinel.log";
            std::filesystem::path bakPath = dir / "native_guard_sentinel.log.bak";

            if (std::filesystem::exists(logPath, ec) && std::filesystem::file_size(logPath, ec) > (10ULL * 1024ULL * 1024ULL)) {
                std::filesystem::remove(bakPath, ec);
                std::filesystem::rename(logPath, bakPath, ec);
            }

            s_sentinelStreams[i].open(logPath, std::ios::app);
        }
    }

    std::string ts = GetTimestampString();
    for (auto& ofs : s_sentinelStreams) {
        if (ofs.is_open()) {
            ofs << "[" << ts << "] " << line << "\n";
        }
    }

    uint64_t now = GetTickCount64();
    if (now - s_lastSentinelFlushTick >= 1000) {
        s_lastSentinelFlushTick = now;
        for (auto& ofs : s_sentinelStreams) {
            if (ofs.is_open()) {
                ofs.flush();
            }
        }
    }
}

// Per-PID crash sentinel helpers (InitSessionCrashSentinelDirs, MarkQuitInitiated, MarkCleanUserExit,
// RevokeCleanExitSentinelsNoAlloc, WritePerPidCrashSentinelNoAlloc, FormatStackBackTraceRvasNoAlloc,
// WriteSentinelLineNoAllocBestEffort) live in veh_crash_shield.cpp so both version.dll and
// native_guard_unit_tests exercise the exact same production implementation.

static std::vector<std::filesystem::path> ResolveDeduplicatedCrashDirs(const std::filesystem::path& moduleDir) {
    std::vector<std::filesystem::path> rawCandidates;
    if (!moduleDir.empty()) {
        rawCandidates.push_back(moduleDir / L"logs" / L"crash");
    }
    rawCandidates.push_back(std::filesystem::path(L"logs") / L"crash");

    std::vector<std::filesystem::path> uniqueDirs;
    std::vector<std::wstring> canonicalKeys;
    for (const auto& cand : rawCandidates) {
        std::error_code ec;
        std::filesystem::create_directories(cand, ec);
        std::filesystem::path canon = std::filesystem::weakly_canonical(cand, ec);
        std::wstring key = (ec || canon.empty()) ? cand.lexically_normal().wstring() : canon.wstring();
        bool duplicate = false;
        for (const auto& existing : canonicalKeys) {
            if (_wcsicmp(existing.c_str(), key.c_str()) == 0) {
                duplicate = true;
                break;
            }
        }
        if (!duplicate) {
            canonicalKeys.push_back(key);
            uniqueDirs.push_back((ec || canon.empty()) ? cand : canon);
        }
    }
    return uniqueDirs;
}

static bool IsRealHardwareDebuggerAttachedSeh() noexcept {
    __try {
#if defined(_M_X64) || defined(__x86_64__)
        const uint8_t* pPeb = reinterpret_cast<const uint8_t*>(__readgsqword(0x60));
        return pPeb && (pPeb[2] != 0);
#else
        return false;
#endif
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool IsRealHardwareDebuggerAttached() noexcept {
    KongorScopedProtectedSeh sehGuard;
    return IsRealHardwareDebuggerAttachedSeh();
}

void CheckMainThreadWatchdogTick() {
    if (g_isHeadlessProcess || g_shutdownStarted.load(std::memory_order_relaxed)) {
        return;
    }

    uint64_t hb = g_lastMainThreadHeartbeatTick.load(std::memory_order_relaxed);
    if (hb == 0) {
        return;
    }
    uint64_t nowTick = GetTickCount64();
    if (nowTick <= hb) {
        return;
    }
    uint64_t stallMs = nowTick - hb;
    const bool isDedicated = IsDedicatedServerProcessCached();
    const int phase = g_lastKnownGamePhase.load(std::memory_order_relaxed);

    // Tier 1: Fast Emergency Cursor Rescue (500ms if escape shortcut held, or 2500ms stall while confined)
    // Skip during dedicated server or pre-window cold boot (phase < 0) to prevent false cursor-release spam.
    if (!isDedicated && phase >= 0 && g_currentlyConfined.load(std::memory_order_acquire)) {
        bool escKeyHeld =
            ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) ||
            ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) ||
            (((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) &&
             (((GetAsyncKeyState(VK_TAB) & 0x8000) != 0) || ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0))) ||
            (((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) && ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0));

        if ((stallMs >= 500 && escKeyHeld) || (stallMs >= 2500)) {
            Kongor_ForceReleaseCursorEmergency();
            if (TryEnterCriticalSection(&g_cursorLock)) {
                g_cursorStateMachine.OnKeyDown(VK_LWIN, false, false);
                LeaveCriticalSection(&g_cursorLock);
            }
            char msg[256];
            snprintf(msg, sizeof(msg),
                "[KONGOR CURSOR] WATCHDOG EMERGENCY RELEASE after %llums main-thread stall (escKeyHeld=%d)",
                static_cast<unsigned long long>(stallMs), escKeyHeld ? 1 : 0);
            WriteSentinelLine(msg);
        }
    }

    // Tier 2: Compact Non-Fatal Hang Diagnostic Minidump
    // (90s during ServerPrecache / cold-boot phase < 0, 30s during map load phase == 1, 15s active responsiveness)
    if (IsRealHardwareDebuggerAttached()) {
        return;
    }
    uint64_t dumpThresholdMs = 15000ULL;
    if (isDedicated || phase < 0) {
        dumpThresholdMs = 90000ULL;
    } else if (phase == 1) {
        dumpThresholdMs = 30000ULL;
    }
    if (stallMs < dumpThresholdMs) {
        return;
    }
    if (g_hangDumpWrittenThisSession.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    // Ensure cursor is released before writing hang diagnostic dump (client post-boot only)
    if (!isDedicated && phase >= 0) {
        Kongor_ForceReleaseCursorEmergency();
    }

    DWORD winTid = 0;
    uintptr_t sampledRip = 0;
    char modNameBuf[128] = "unknown";
    uintptr_t modRva = 0;
    HWND hWnd = g_hGameWnd;
    if (hWnd && IsWindow(hWnd)) {
        winTid = GetWindowThreadProcessId(hWnd, nullptr);
    }
    if (winTid == 0) {
        winTid = g_mainProcessThreadId;
    }
    if (winTid != 0 && winTid != GetCurrentThreadId()) {
        HANDLE hMain = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, winTid);
        if (hMain) {
            if (SuspendThread(hMain) != (DWORD)-1) {
                CONTEXT ctx{};
                ctx.ContextFlags = CONTEXT_CONTROL;
                if (GetThreadContext(hMain, &ctx)) {
                    sampledRip = static_cast<uintptr_t>(ctx.Rip);
                }
                ResumeThread(hMain);
            }
            CloseHandle(hMain);
        }
    }
    if (sampledRip != 0) {
        HMODULE hFaultMod = nullptr;
        if (GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(sampledRip),
                &hFaultMod) && hFaultMod) {
            modRva = sampledRip - reinterpret_cast<uintptr_t>(hFaultMod);
            char fullModPath[MAX_PATH] = {0};
            if (GetModuleFileNameA(hFaultMod, fullModPath, sizeof(fullModPath)) > 0) {
                const char* base = strrchr(fullModPath, '\\');
                strncpy_s(modNameBuf, sizeof(modNameBuf), base ? (base + 1) : fullModPath, _TRUNCATE);
            }
        }
    }

    wchar_t exePath[MAX_PATH] = {0};
    std::filesystem::path baseDir;
    if (g_hSelfModule && GetModuleFileNameW(g_hSelfModule, exePath, MAX_PATH) > 0) {
        baseDir = std::filesystem::path(exePath).parent_path();
    } else if (GetModuleFileNameW(NULL, exePath, MAX_PATH) > 0) {
        baseDir = std::filesystem::path(exePath).parent_path();
    } else {
        std::error_code cwdEc;
        baseDir = std::filesystem::current_path(cwdEc);
    }

    std::error_code ec;
    std::filesystem::path crashDir = baseDir / L"userdata" / L"logs" / L"crash";
    std::filesystem::create_directories(crashDir, ec);

    SYSTEMTIME st{};
    GetSystemTime(&st);
    wchar_t dumpFileName[128] = {0};
    swprintf_s(dumpFileName,
        L"hang_%04u%02u%02u_%02u%02u%02u_pid%lu.dmp",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
        GetCurrentProcessId());
    std::filesystem::path dumpPath = crashDir / dumpFileName;

    bool dumpOk = false;
    KongorTier1DumpCallbackCtx hangCbCtx{};
    hangCbCtx.TargetStackThreadId = winTid;
    hangCbCtx.FaultAddress = static_cast<uintptr_t>(sampledRip);
    typedef BOOL (WINAPI *PFN_MiniDumpWriteDump)(
        HANDLE, DWORD, HANDLE, MINIDUMP_TYPE,
        PMINIDUMP_EXCEPTION_INFORMATION, PMINIDUMP_USER_STREAM_INFORMATION, PMINIDUMP_CALLBACK_INFORMATION);
    HMODULE hDbgHelp = LoadLibraryW(L"dbghelp.dll");
    PFN_MiniDumpWriteDump pfnMiniDump = hDbgHelp
        ? reinterpret_cast<PFN_MiniDumpWriteDump>(GetProcAddress(hDbgHelp, "MiniDumpWriteDump"))
        : nullptr;
    if (pfnMiniDump) {
        HANDLE hDumpFile = ::CreateFileW(
            dumpPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hDumpFile != INVALID_HANDLE_VALUE) {
            MINIDUMP_CALLBACK_INFORMATION cbInfo{};
            cbInfo.CallbackRoutine = Kongor_Tier1PrivacyMiniDumpCallback;
            cbInfo.CallbackParam = &hangCbCtx;
            MINIDUMP_TYPE dumpType = static_cast<MINIDUMP_TYPE>(
                MiniDumpNormal | MiniDumpFilterModulePaths | MiniDumpWithoutAuxiliaryState | MiniDumpWithThreadInfo);
            dumpOk = (pfnMiniDump(GetCurrentProcess(), GetCurrentProcessId(), hDumpFile, dumpType, nullptr, nullptr, &cbInfo) != FALSE);
            CloseHandle(hDumpFile);
        }
    }

    // Retain newest 5 hang_*.dmp files
    std::vector<std::pair<std::filesystem::file_time_type, std::filesystem::path>> hangDumps;
    for (const auto& entry : std::filesystem::directory_iterator(crashDir, ec)) {
        if (ec) break;
        if (!entry.is_regular_file(ec)) continue;
        std::wstring fname = entry.path().filename().wstring();
        if (fname.rfind(L"hang_", 0) == 0 && entry.path().extension() == L".dmp") {
            auto ftime = entry.last_write_time(ec);
            if (!ec) {
                hangDumps.emplace_back(ftime, entry.path());
            }
        }
    }
    if (hangDumps.size() > 5) {
        std::sort(hangDumps.begin(), hangDumps.end(),
            [](const auto& a, const auto& b) { return a.first > b.first; });
        for (size_t i = 5; i < hangDumps.size(); ++i) {
            std::filesystem::remove(hangDumps[i].second, ec);
        }
    }

    char hangLog[512];
    snprintf(hangLog, sizeof(hangLog),
        "[KONGOR HANG WATCHDOG] Main thread (TID=%lu) stalled for %llums (phase=%d). RIP=0x%llX (%s+0x%llX) dump=userdata/logs/crash/%ls ok=%d stripped=%u bgSuppressed=%u",
        winTid,
        static_cast<unsigned long long>(stallMs),
        phase,
        static_cast<unsigned long long>(sampledRip),
        modNameBuf,
        static_cast<unsigned long long>(modRva),
        dumpFileName,
        dumpOk ? 1 : 0,
        hangCbCtx.ModulesStripped,
        hangCbCtx.BackgroundStacksSuppressed);
    WriteSentinelLine(hangLog);
}


    __declspec(dllexport) bool Kongor_NativeGuard_IsMimallocActive() {
        return g_mimallocActive.load(std::memory_order_relaxed);
    }

    __declspec(dllexport) int Kongor_NativeGuard_GetMimallocVersion() {
        return g_mimallocVersion.load(std::memory_order_relaxed);
    }



static std::atomic<bool> g_guardInstalled{false};
static std::atomic<bool> g_testModeActive{false};

static void ShutdownNativeGuard(bool isProcessTermination = false) {
    // (#171) Single-Flight Shutdown Gate
    if (!TryBeginSingleFlightShutdown()) {
        return;
    }
    // Always signal/cancel the graceful quit watchdog; join with bounded wait on FreeLibrary (!isProcessTermination)
    // so the watchdog thread never executes unmapped DLL code after unload.
    ShutdownGracefulQuitWatchdog(isProcessTermination);
    if (!g_guardInstalled.exchange(false)) {
        return;
    }
    if (g_testModeActive.exchange(false)) {
        return;
    }
    g_pipeServerRunning = false;
    if (isProcessTermination) {
        // Under LdrShutdownProcess (lpReserved != NULL), kernel has already terminated worker threads;
        // skip thread WaitForSingleObject and DeleteCriticalSection under loader lock to prevent deadlock.
        Kongor::ShutdownVfsLooseCache(true);
        LogArchiveTelemetrySummary();
        FlushSentinelLogs(true);
        return;
    }
    // (#179) Restore all concrete shadow VMT instances and clear sidecar entity fields
    kongor::native_guard::ConcreteVmtHookManager::Instance().RestoreAllInstances();
    kongor::native_guard::EntityFieldContainer::Instance().ClearAll();

    timeEndPeriod(1);
    Kongor::ShutdownVfsLooseCache(false);
    Kongor::ShutdownAssetPrewarmer();
    Kongor::JobPool::Shutdown();
    char enablePredEnv[16] = {0};
    if (GetEnvironmentVariableA("KONGOR_ENABLE_PREDICTIVE_PATHING", enablePredEnv, sizeof(enablePredEnv)) > 0 &&
        (enablePredEnv[0] == '1' || enablePredEnv[0] == 'y' || enablePredEnv[0] == 'Y')) {
        Kongor::PredictivePathing::Shutdown();
    }
    if (g_hPipeServerThread) {
        WaitForSingleObject(g_hPipeServerThread, 100);
        CloseHandle(g_hPipeServerThread);
        g_hPipeServerThread = NULL;
    }
    DeleteCriticalSection(&g_cursorLock);
    ShutdownTelemetrySharedMemory();
    CleanupServerKeyframeMap();
    LogArchiveTelemetrySummary();
    FlushSentinelLogs();
}

static DWORD WINAPI K2SafeHooksWatcherWorker(LPVOID) {
    // (#171) Scoped COM Apartment Guard tolerant of RPC_E_CHANGED_MODE
    KongorScopedComGuard comGuard(COINIT_MULTITHREADED);
    char disableMinizEnv[16] = {0};
    char enableMinizEnv[16] = {0};
    bool minizDisabledByEnv =
        (GetEnvironmentVariableA("KONGOR_DISABLE_MINIZ_ARCHIVE", disableMinizEnv, sizeof(disableMinizEnv)) > 0 &&
        (disableMinizEnv[0] == '1' || disableMinizEnv[0] == 'y' || disableMinizEnv[0] == 'Y')) ||
        (GetEnvironmentVariableA("KONGOR_ENABLE_MINIZ_ARCHIVE", enableMinizEnv, sizeof(enableMinizEnv)) > 0 &&
        (enableMinizEnv[0] == '0' || enableMinizEnv[0] == 'n' || enableMinizEnv[0] == 'N'));
    bool minizEnabled = !Kongor::PatchController::IsSafeModeActive() && !minizDisabledByEnv;

    for (int attempts = 0; attempts < 1000; ++attempts) {
        HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
        if (hK2) {
            const bool isClientK2 = IsK2ClientModule(hK2);
            if (minizEnabled && isClientK2) {
                InstallModernArchiveHooks(hK2);
            }
            InstallK2SafeHooks(hK2);
            InstallConsoleHooks(hK2);
            if (isClientK2) {
                InstallCursorAndConfigIATHooks();
                Kongor_RegisterEmergencyCursorReleaseContext(
                    g_pfnRealClipCursor, g_hGameWnd, &g_currentlyConfined, &g_cursorStateMachine);
            }
            break;
        }
        Sleep(5);
    }
    return 0;
}

static void RotateForegroundConsoleLogIfUnlocked(const std::filesystem::path& moduleDir) noexcept {
    if (g_isHeadlessProcess) {
        return;
    }
    const char* rawCmd = GetCommandLineA();
    if (rawCmd && (strstr(rawCmd, "-dedicated") != nullptr || strstr(rawCmd, "-server") != nullptr)) {
        return;
    }

    std::error_code ec;
    std::filesystem::path baseDir = moduleDir.empty() ? std::filesystem::current_path(ec) : moduleDir;
    if (baseDir.empty()) {
        return;
    }
    std::filesystem::path overrideDir = baseDir / L"userdata" / L"Heroes of Newerth x64" / L"override";
    std::filesystem::path consoleLogPath = overrideDir / L"console.log";
    if (!std::filesystem::exists(consoleLogPath, ec) || ec) {
        return;
    }
    auto fsize = std::filesystem::file_size(consoleLogPath, ec);
    if (ec || fsize == 0) {
        return;
    }

    SYSTEMTIME st{};
    GetSystemTime(&st);
    wchar_t rotatedName[128] = {0};

    // Probe exclusive access (dwShareMode == 0) so we never rename console.log if another instance holds it open
    const auto pfnCreateFile = (g_pfnRealCreateFileW ? g_pfnRealCreateFileW : &::CreateFileW);
    HANDLE hProbe = pfnCreateFile(
        consoleLogPath.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    bool copiedViaSharedFallback = false;
    if (hProbe == INVALID_HANDLE_VALUE) {
        DWORD err = GetLastError();
        // Zombie-safe fallback (WS3A/WS5): if a dead/zombie kernel handle holds console.log open,
        // snapshot its bytes using FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE so crash evidence is preserved.
        swprintf_s(rotatedName,
            L"console_%04u%02u%02u_%02u%02u%02u_shared.log",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        std::filesystem::path sharedSnapshotPath = overrideDir / rotatedName;
        HANDLE hSharedSrc = pfnCreateFile(
            consoleLogPath.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_FLAG_SEQUENTIAL_SCAN,
            nullptr);
        if (hSharedSrc == INVALID_HANDLE_VALUE) {
            char skipMsg[180];
            snprintf(skipMsg, sizeof(skipMsg),
                "[KONGOR LOG ROTATE] Skipped console.log rotation (locked/unreadable, exclErr=%lu, sharedErr=%lu).\n",
                err, GetLastError());
            OutputDebugStringA(skipMsg);
            return;
        }
        HANDLE hSharedDst = pfnCreateFile(
            sharedSnapshotPath.c_str(),
            GENERIC_WRITE,
            FILE_SHARE_READ,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (hSharedDst == INVALID_HANDLE_VALUE) {
            CloseHandle(hSharedSrc);
            return;
        }
        constexpr DWORD kChunkBytes = 65536;
        constexpr uint64_t kMaxCopyBytes = 64ULL * 1024ULL * 1024ULL;
        std::vector<uint8_t> copyBuf(kChunkBytes);
        uint64_t totalCopied = 0;
        DWORD bytesRead = 0;
        while (totalCopied < kMaxCopyBytes &&
               ::ReadFile(hSharedSrc, copyBuf.data(), kChunkBytes, &bytesRead, nullptr) &&
               bytesRead > 0) {
            DWORD bytesWritten = 0;
            if (!::WriteFile(hSharedDst, copyBuf.data(), bytesRead, &bytesWritten, nullptr) || bytesWritten == 0) {
                break;
            }
            totalCopied += bytesWritten;
        }
        ::FlushFileBuffers(hSharedDst);
        CloseHandle(hSharedDst);
        CloseHandle(hSharedSrc);
        fsize = totalCopied;
        copiedViaSharedFallback = true;
    } else {
        CloseHandle(hProbe);
        swprintf_s(rotatedName,
            L"console_%04u%02u%02u_%02u%02u%02u.log",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        std::filesystem::path rotatedPath = overrideDir / rotatedName;
        if (!MoveFileExW(consoleLogPath.c_str(), rotatedPath.c_str(), MOVEFILE_REPLACE_EXISTING)) {
            return;
        }
    }

    // Retain newest 5 console_*.log archives
    std::vector<std::pair<std::filesystem::file_time_type, std::filesystem::path>> archives;
    for (const auto& entry : std::filesystem::directory_iterator(overrideDir, ec)) {
        if (ec) break;
        if (!entry.is_regular_file(ec)) continue;
        std::wstring fname = entry.path().filename().wstring();
        if (fname.rfind(L"console_", 0) == 0 && entry.path().extension() == L".log") {
            auto ftime = entry.last_write_time(ec);
            if (!ec) {
                archives.emplace_back(ftime, entry.path());
            }
        }
    }
    if (archives.size() > 5) {
        std::sort(archives.begin(), archives.end(),
            [](const auto& a, const auto& b) { return a.first > b.first; });
        for (size_t i = 5; i < archives.size(); ++i) {
            std::filesystem::remove(archives[i].second, ec);
        }
    }

    char rotMsg[280];
    snprintf(rotMsg, sizeof(rotMsg),
        copiedViaSharedFallback
            ? "[KONGOR LOG ROTATE] Preserved locked/zombie console.log via shared snapshot -> %ls (%llu bytes)"
            : "[KONGOR LOG ROTATE] Rotated unlocked console.log -> %ls (%llu bytes)",
        rotatedName, static_cast<unsigned long long>(fsize));
    WriteSentinelLine(rotMsg);
}

static void NeutralizeCompetingWillowmakerPipeFlagSeh() noexcept {
    __try {
        const wchar_t* const targetsW[] = { L"-pipe willowmaker-ipc", L"-pipe hon-client-" };
        wchar_t* pCmdW = GetCommandLineW();
        if (pCmdW) {
            for (const wchar_t* target : targetsW) {
                wchar_t* pMatchW = wcsstr(pCmdW, target);
                if (pMatchW && pMatchW[0] == L'-' && wcsncmp(pMatchW, L"-pipe", 5) == 0) {
                    // Neutralize built-in pipe flag to prevent K2 from opening a competing pipe.
                    // Mutate "-pipe" -> "-zpip" (5 chars).
                    // CRITICAL INVARIANT: Never mutate to "-noip" (which disables UDP networking).
                    pMatchW[1] = L'z';
                    pMatchW[2] = L'p';
                    pMatchW[3] = L'i';
                    pMatchW[4] = L'p';
                }
            }
        }
        const char* const targetsA[] = { "-pipe willowmaker-ipc", "-pipe hon-client-" };
        char* pCmdA = GetCommandLineA();
        if (pCmdA) {
            for (const char* target : targetsA) {
                char* pMatchA = strstr(pCmdA, target);
                if (pMatchA && pMatchA[0] == '-' && strncmp(pMatchA, "-pipe", 5) == 0) {
                    pMatchA[1] = 'z';
                    pMatchA[2] = 'p';
                    pMatchA[3] = 'i';
                    pMatchA[4] = 'p';
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

static void NeutralizeCompetingWillowmakerPipeFlag() noexcept {
    KongorScopedProtectedSeh sehGuard;
    NeutralizeCompetingWillowmakerPipeFlagSeh();
}

void InstallNativeGuard() {
    if (g_guardInstalled.exchange(true)) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Already installed in this process. Skipping duplicate installation.\n");
        return;
    }
    Kongor::PatchController::Initialize();
    bool safeModeBoot = Kongor::PatchController::IsSafeModeActive();
    if (safeModeBoot) {
        char smMsg[256] = {0};
        snprintf(smMsg, sizeof(smMsg),
            "[GEODE-SAFE-MODE] Boot Safe Mode is ACTIVE (reason=%s); optional runtime patchers disarmed while SEH/VEH crash shield remains armed.\n",
            Kongor::PatchController::GetSafeModeReason());
        OutputDebugStringA(smMsg);
    }
    char testModeEnv[16] = {0};
    if (GetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", testModeEnv, sizeof(testModeEnv)) > 0 &&
        (testModeEnv[0] == '1' || testModeEnv[0] == 'y' || testModeEnv[0] == 'Y')) {
        g_testModeActive.store(true, std::memory_order_release);
        OutputDebugStringA("[KONGOR NATIVE GUARD] KONGOR_NATIVE_GUARD_TEST_MODE=1 detected; skipping detached background threads for unit testing.\n");
        return;
    }
    timeBeginPeriod(1);
    OutputDebugStringA("[KONGOR NATIVE GUARD] Enabled persistent 1.0ms Windows timer resolution (timeBeginPeriod(1)).\n");
    Kongor_WarmRtlDllShutdownInProgressCache();
    pPreviousFilter = SetUnhandledExceptionFilter(NativeGuardUnhandledExceptionFilter);
    HMODULE hKernelBase = GetModuleHandleW(L"kernelbase.dll");
    if (!hKernelBase) hKernelBase = GetModuleHandleW(L"kernel32.dll");
    if (hKernelBase) {
        void* pfnSetFilter = reinterpret_cast<void*>(GetProcAddress(hKernelBase, "SetUnhandledExceptionFilter"));
        if (pfnSetFilter) {
            InstallInlineHook64(pfnSetFilter, reinterpret_cast<void*>(Hooked_SetUnhandledExceptionFilter),
                reinterpret_cast<void**>(&g_origSetUnhandledExceptionFilter), 15);
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for SetUnhandledExceptionFilter.\n");
        }
        void* pfnIsDebuggerPresent = reinterpret_cast<void*>(GetProcAddress(hKernelBase, "IsDebuggerPresent"));
        if (pfnIsDebuggerPresent) {
            void* pDummy = nullptr;
            InstallInlineHook64(pfnIsDebuggerPresent, reinterpret_cast<void*>(Hooked_IsDebuggerPresent), &pDummy, 15);
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for IsDebuggerPresent.\n");
        }
        void* pfnCheckRemote = reinterpret_cast<void*>(GetProcAddress(hKernelBase, "CheckRemoteDebuggerPresent"));
        if (pfnCheckRemote) {
            void* pDummy = nullptr;
            InstallInlineHook64(pfnCheckRemote, reinterpret_cast<void*>(Hooked_CheckRemoteDebuggerPresent), &pDummy, 15);
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CheckRemoteDebuggerPresent.\n");
        }
    }
    AddVectoredExceptionHandler(1, NativeGuardVectoredHandler);
    _set_purecall_handler(NativeGuardPurecallHandler);
    _set_invalid_parameter_handler(NativeGuardInvalidParameterHandler);
    OutputDebugStringA("[KONGOR NATIVE GUARD] In-engine crash sentinel (SEH + VEH + CRT) installed successfully.\n");

    // Milestone 4.4: Enforce x64 hardware float determinism (FTZ / DAZ / Round-to-Nearest)
    unsigned int activeMxcsr = Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism();
    char mxcsrLog[160];
    snprintf(mxcsrLog, sizeof(mxcsrLog),
             "[KONGOR NATIVE GUARD] x64 MXCSR hardware float determinism active: FTZ=1, DAZ=1, RN=1 (MXCSR: 0x%04X)\n",
             activeMxcsr);
    OutputDebugStringA(mxcsrLog);

    InitializeCriticalSection(&g_cursorLock);

    // Detect headless mode early BEFORE installing IAT hooks or launching worker threads
    char headlessEnv[16] = {0};
    GetEnvironmentVariableA("KONGOR_HEADLESS", headlessEnv, sizeof(headlessEnv));
    const char* rawCmdLine = GetCommandLineA();
    std::string cmdLine = rawCmdLine ? rawCmdLine : "";
    bool isHeadless = (strcmp(headlessEnv, "1") == 0) ||
                      (cmdLine.find("vid_headless true") != std::string::npos) ||
                      (cmdLine.find("-headless") != std::string::npos) ||
                      (cmdLine.find("startup_headless.cfg") != std::string::npos) ||
                      (cmdLine.find("vid_pos -32000") != std::string::npos) ||
                      (cmdLine.find("vid_pos 25") != std::string::npos) ||
                      (cmdLine.find("-x 25") != std::string::npos) ||
                      (cmdLine.find("-x -32000") != std::string::npos);
    g_isHeadlessProcess = isHeadless;
    EnterCriticalSection(&g_cursorLock);
    g_cursorStateMachine.SetHeadless(isHeadless);
    LeaveCriticalSection(&g_cursorLock);
    if (isHeadless) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Headless tool-window taskbar suppression & startup_headless.cfg write isolation enabled.\n");
    }

    wchar_t selfPath[MAX_PATH] = {0};
    std::filesystem::path moduleDir;
    if (g_hSelfModule && GetModuleFileNameW(g_hSelfModule, selfPath, MAX_PATH) > 0) {
        moduleDir = std::filesystem::path(selfPath).parent_path();
    } else {
        wchar_t exePath[MAX_PATH] = {0};
        if (GetModuleFileNameW(NULL, exePath, MAX_PATH) > 0) {
            moduleDir = std::filesystem::path(exePath).parent_path();
        }
    }

    // Rotate unlocked foreground console.log -> console_<ts>.log before k2_x64.dll opens it
    RotateForegroundConsoleLogIfUnlocked(moduleDir);

    static KongorDpiAwarenessResult s_dpiAwarenessResult = KongorDpiAwarenessResult::SkippedDedicatedServer;
    if (!IsDedicatedServerProcessCached()) {
        char dpiDiag[160] = {0};
        s_dpiAwarenessResult = Kongor_EnableClientHighDpiAwareness(
            false, isHeadless, safeModeBoot, nullptr, dpiDiag, sizeof(dpiDiag));
        if (dpiDiag[0] != '\0') {
            OutputDebugStringA(dpiDiag);
            OutputDebugStringA("\n");
        }

        // Always install ClipCursor & headless startup.cfg CreateFile IAT hooks on client processes
        InstallCursorAndConfigIATHooks();
        Kongor_RegisterEmergencyCursorReleaseContext(
            g_pfnRealClipCursor, g_hGameWnd, &g_currentlyConfined, &g_cursorStateMachine);

        // Raw Input kill-switch check
        char disableRawEnv[16] = {0};
        if (safeModeBoot ||
            (GetEnvironmentVariableA("KONGOR_DISABLE_RAW_INPUT", disableRawEnv, sizeof(disableRawEnv)) > 0 &&
            (disableRawEnv[0] == '1' || disableRawEnv[0] == 'y' || disableRawEnv[0] == 'Y'))) {
            g_rawInputEnabled.store(false, std::memory_order_release);
            OutputDebugStringA("[KONGOR NATIVE GUARD] Raw Input disabled via KONGOR_DISABLE_RAW_INPUT or Safe Mode.\n");
        } else {
            InstallRawInputIATHooks();
        }
    } else {
        g_rawInputEnabled.store(false, std::memory_order_release);
    }

    // Early dynamic override via Microsoft mimalloc
    char disableMimallocEnv[16] = {0};
    if (safeModeBoot ||
        (GetEnvironmentVariableA("KONGOR_DISABLE_MIMALLOC", disableMimallocEnv, sizeof(disableMimallocEnv)) > 0 &&
        (disableMimallocEnv[0] == '1' || disableMimallocEnv[0] == 'y' || disableMimallocEnv[0] == 'Y'))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] mimalloc disabled via KONGOR_DISABLE_MIMALLOC or Safe Mode.\n");
    } else {
        if (!moduleDir.empty()) {
            SetDllDirectoryW(moduleDir.c_str());
            // Preload mimalloc-redirect.dll only if explicitly opted-in to avoid CRT double-free collisions
            char enableRedirectEnv[16] = {0};
            if (GetEnvironmentVariableA("KONGOR_MIMALLOC_REDIRECT", enableRedirectEnv, sizeof(enableRedirectEnv)) > 0 &&
                (enableRedirectEnv[0] == '1' || enableRedirectEnv[0] == 'y' || enableRedirectEnv[0] == 'Y')) {
                std::filesystem::path redirectPath = moduleDir / L"mimalloc-redirect.dll";
                LoadLibraryW(redirectPath.c_str());
            }
        }

        std::filesystem::path overridePath = moduleDir.empty() ? L"mimalloc-override.dll" : (moduleDir / L"mimalloc-override.dll");
        std::filesystem::path mimallocPath = moduleDir.empty() ? L"mimalloc.dll" : (moduleDir / L"mimalloc.dll");

        HMODULE hMimalloc = LoadLibraryW(overridePath.c_str());
        if (!hMimalloc) {
            hMimalloc = LoadLibraryW(mimallocPath.c_str());
        }
        if (hMimalloc) {
            typedef int (*PFN_mi_version)();
            PFN_mi_version pfnVersion = (PFN_mi_version)GetProcAddress(hMimalloc, "mi_version");
            int ver = pfnVersion ? pfnVersion() : 0;
            g_mimallocActive.store(true, std::memory_order_release);
            g_mimallocVersion.store(ver, std::memory_order_release);
            char mimallocLog[128];
            snprintf(mimallocLog, sizeof(mimallocLog), "[KONGOR NATIVE GUARD] Microsoft mimalloc dynamic UCRT allocator active. Version: %d\n", ver);
            OutputDebugStringA(mimallocLog);
        } else {
            DWORD err = GetLastError();
            char errLog[128];
            snprintf(errLog, sizeof(errLog), "[KONGOR NATIVE GUARD] mimalloc binaries not loaded (err: %lu). Using stock Windows CRT heap.\n", err);
            OutputDebugStringA(errLog);
        }
    }

    // Legacy Kongor.NativeGuard.dll companion hooks (Precache Bypass, AtoBFast/AtoFFast detours, 1.0ms timer pacing)
    // are now unified directly inside version.dll. Only load the legacy companion if explicitly opted in.
    char loadCompanionEnv[16] = {0};
    if (!safeModeBoot &&
        GetEnvironmentVariableA("KONGOR_LOAD_LEGACY_COMPANION", loadCompanionEnv, sizeof(loadCompanionEnv)) > 0 &&
        (loadCompanionEnv[0] == '1' || loadCompanionEnv[0] == 'y' || loadCompanionEnv[0] == 'Y')) {
        std::filesystem::path nativeGuardPath = moduleDir.empty() ? L"Kongor.NativeGuard.dll" : (moduleDir / L"Kongor.NativeGuard.dll");
        HMODULE hNativeGuard = LoadLibraryW(nativeGuardPath.c_str());
        if (!hNativeGuard) {
            hNativeGuard = LoadLibraryW(L"Kongor.NativeGuard.dll");
        }
        if (hNativeGuard) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Kongor.NativeGuard.dll legacy companion loaded via KONGOR_LOAD_LEGACY_COMPANION=1.\n");
        }
    } else {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Unified single-supervisor mode active in version.dll (skipping redundant Kongor.NativeGuard.dll companion load).\n");
    }

    // Initialize 1000 FPS shared memory fast bus
    InitializeTelemetrySharedMemory();

    // Neutralize '-pipe willowmaker-ipc' in mutable command-line buffers before k2_x64.dll initializes,
    // preventing k2_x64 from opening a competing read-and-discard pipe instance on \\.\pipe\willowmaker-ipc.
    NeutralizeCompetingWillowmakerPipeFlag();

    // Initialize in-process command pipe (lock-free moodycamel::ConcurrentQueue)
    g_pipeServerRunning = true;
    g_hPipeServerThread = CreateThread(NULL, 0, NativeCommandPipeWorker, NULL, 0, NULL);

    // Initialize background sequential asset pre-warmer & read-only VFS loose-file existence index
    if (!safeModeBoot) {
        if (!IsDedicatedServerProcessCached()) {
            Kongor::InitializeVfsLooseCache();
            Kongor::InitializeAssetPrewarmer();
        }
    }

    // Initialize multithreaded worker job pool for 5v5 teamfights
    Kongor::JobPool::Initialize();

    // Client-side predictive hero turn & pathing engine (opt-in via KONGOR_ENABLE_PREDICTIVE_PATHING=1)
    char enablePredEnv[16] = {0};
    if (!safeModeBoot &&
        GetEnvironmentVariableA("KONGOR_ENABLE_PREDICTIVE_PATHING", enablePredEnv, sizeof(enablePredEnv)) > 0 &&
        (enablePredEnv[0] == '1' || enablePredEnv[0] == 'y' || enablePredEnv[0] == 'Y')) {
        Kongor::PredictivePathing::Initialize();
    } else {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Predictive hero turn & pathing engine disabled (YAGNI).\n");
    }

    const bool isDedicatedProc = IsDedicatedServerProcessCached();
    {
        // Proactive in-memory crash hotfix & CPlayer avatar/upgrade unlocks for game_shared_x64
        CreateThread(NULL, 0, GameSharedPatcherWorker, NULL, 0, NULL);

        // VidD3D9 8,192 Texture Table Reclaimer and Match-Time Purge Worker (client only)
        if (!isDedicatedProc) {
            CreateThread(NULL, 0, VidD3D9TexturePatcherWorker, NULL, 0, NULL);
        }
    }

    // K2 Engine & Safe Hooks Installation:
    // Lazy miniz archive hooks default to ENABLED on K2_CLIENT (intercepting >= 1.5 GiB archives, Zip64 archives,
    // and .s2zdir overlays while delegating standard < 1.5 GiB archives to K2 native CMMapUnzip).
    // Can be disabled via KONGOR_DISABLE_MINIZ_ARCHIVE=1 or KONGOR_ENABLE_MINIZ_ARCHIVE=0.
    char disableMinizEnv[16] = {0};
    char enableMinizEnv[16] = {0};
    bool minizDisabledByEnv =
        (GetEnvironmentVariableA("KONGOR_DISABLE_MINIZ_ARCHIVE", disableMinizEnv, sizeof(disableMinizEnv)) > 0 &&
        (disableMinizEnv[0] == '1' || disableMinizEnv[0] == 'y' || disableMinizEnv[0] == 'Y')) ||
        (GetEnvironmentVariableA("KONGOR_ENABLE_MINIZ_ARCHIVE", enableMinizEnv, sizeof(enableMinizEnv)) > 0 &&
        (enableMinizEnv[0] == '0' || enableMinizEnv[0] == 'n' || enableMinizEnv[0] == 'N'));
    bool minizEnabled = !minizDisabledByEnv && !isDedicatedProc;

    HMODULE hK2Early = GetModuleHandleA("k2_x64.dll");
    if (hK2Early) {
        const bool isClientK2 = IsK2ClientModule(hK2Early);
        if (minizEnabled && isClientK2) {
            InstallModernArchiveHooks(hK2Early);
        } else {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Modern miniz archive hooks disabled or non-client K2 module.\n");
        }
        InstallK2SafeHooks(hK2Early);
        InstallConsoleHooks(hK2Early);
        if (isClientK2) {
            InstallCursorAndConfigIATHooks();
        }
    } else {
        CreateThread(NULL, 0, K2SafeHooksWatcherWorker, NULL, 0, NULL);
    }

    if (!isDedicatedProc) {
        CreateThread(NULL, 0, NativeGuardWindowHookWatcher, NULL, 0, NULL);
        OutputDebugStringA("[KONGOR NATIVE GUARD] Window subclassing and Raw Input watcher thread activated.\n");
    }

    // Write deduplicated startup sentinel log & register foreground per-PID session sentinel (WS3A/WS5)
    std::vector<std::filesystem::path> targetDirs = ResolveDeduplicatedCrashDirs(moduleDir);
    InitSessionCrashSentinelDirs(targetDirs, /*trackForegroundSession=*/(!isDedicatedProc && !isHeadless));

    for (const auto& logDir : targetDirs) {
        std::error_code ec;
        std::filesystem::create_directories(logDir, ec);
        std::filesystem::path logFilePath = logDir / "native_guard_sentinel.log";
        std::ofstream logFile(logFilePath, std::ios::app);
        if (logFile.is_open()) {
            logFile << "[" << GetTimestampString() << "] Kongor NativeGuard initialized. PID: "
                    << GetCurrentProcessId() << ", Host Process Base: 0x" << std::hex << (uintptr_t)GetModuleHandleA(NULL);
            if (g_mimallocActive.load(std::memory_order_acquire)) {
                int v = g_mimallocVersion.load(std::memory_order_acquire);
                int major = v / 10000;
                int minor = (v % 10000) / 100;
                int patch = v % 100;
                logFile << ", Allocator: Microsoft mimalloc (v" << std::dec << major << "." << minor << "." << patch << ")";
            } else {
                logFile << ", Allocator: Stock Windows UCRT";
            }
            if (g_rawInputEnabled.load(std::memory_order_acquire)) {
                logFile << ", RawInput: Active (8000Hz Direct)";
            } else {
                logFile << ", RawInput: Disabled";
            }
            logFile << ", DPI: " << Kongor_DpiAwarenessResultToString(s_dpiAwarenessResult);
            logFile << ", MXCSR: 0x" << std::hex << std::uppercase << _mm_getcsr() << " (FTZ+DAZ), Release: v5.0.2";
            logFile << "\n";
            logFile.flush();
        }
    }
    if (!isDedicatedProc && !isHeadless &&
        cmdLine.find("-mod ") == std::string::npos &&
        cmdLine.find("-mod\t") == std::string::npos &&
        cmdLine.find("-mod\"") == std::string::npos) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] [BARE-EXE-WARN] hon_x64.exe launched without '-mod' stack; launch via WILLOWMAKER or pass '-mod override;base;game;ClientPackageFiles;KONGOR_ARENA' for full UI/shader parity.");
    }
    WriteSentinelLine("[KONGOR_HOTFIX] Active patch: v5.0.2 (NativeGuard version.dll)");
}

static bool IsManagedOrToolHostProcess() {
    char forceEnv[16] = {};
    if (GetEnvironmentVariableA("KONGOR_FORCE_NATIVE_GUARD", forceEnv, sizeof(forceEnv)) > 0 &&
        (forceEnv[0] == '1' || forceEnv[0] == 'y' || forceEnv[0] == 'Y')) {
        return false;
    }

    char testModeEnv[16] = {};
    if (GetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", testModeEnv, sizeof(testModeEnv)) > 0 &&
        (testModeEnv[0] == '1' || testModeEnv[0] == 'y' || testModeEnv[0] == 'Y' || testModeEnv[0] == 't' || testModeEnv[0] == 'T')) {
        return true;
    }

    wchar_t hostPath[MAX_PATH] = {};
    if (GetModuleFileNameW(NULL, hostPath, MAX_PATH) == 0) {
        return false;
    }

    const wchar_t* baseName = wcsrchr(hostPath, L'\\');
    baseName = baseName ? (baseName + 1) : hostPath;

    const wchar_t* const blockedPrefixes[] = {
        L"testhost",
        L"dotnet",
        L"vstest",
        L"test_",
        L"ctest",
        L"willowmaker",
        L"kongor",
        L"powershell",
        L"pwsh",
        L"python",
        L"msbuild",
        L"dumpbin",
        L"rundll32"
    };

    for (const wchar_t* prefix : blockedPrefixes) {
        size_t len = wcslen(prefix);
        if (_wcsnicmp(baseName, prefix, len) == 0) {
            return true;
        }
    }

    return false;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH: {
            DisableThreadLibraryCalls(hModule);
            g_hSelfModule = hModule;
            g_mainProcessThreadId = GetCurrentThreadId();

            char sysDir[MAX_PATH];
            std::string sysVersionPath;
            if (GetSystemDirectoryA(sysDir, MAX_PATH) > 0) {
                sysVersionPath = std::string(sysDir) + "\\version.dll";
                hOriginalDll = LoadLibraryA(sysVersionPath.c_str());
            }

            if (!hOriginalDll) {
                hOriginalDll = LoadLibraryA("C:\\Windows\\System32\\version.dll");
            }

            if (!hOriginalDll) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Error: Failed to load system version.dll!\n");
                return FALSE;
            }

            InitializeProxies(hOriginalDll);

            // Guard against accidental in-process loads inside .NET test runners (testhost.exe / dotnet.exe)
            // or managed tooling: forward version.dll exports cleanly without pinning or spawning hooks/threads.
            if (IsManagedOrToolHostProcess()) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Loaded inside managed/tool host process; forwarding version.dll exports only.\n");
                return TRUE;
            }

            // Self-pin proxy DLL in process address space (#96 manimgx-self-pin-dll-lavapipe)
            // Prevents premature unmapping while background threads or VEH trampolines are active.
            HMODULE hPinnedSelf = NULL;
            GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCWSTR>(&DllMain),
                &hPinnedSelf);

            // Module-relative Lavapipe CPU Vulkan ICD fallback (#96) when headless / KONGOR_FORCE_LAVAPIPE=1
            char forceLavapipe[16] = {};
            if (GetEnvironmentVariableA("KONGOR_FORCE_LAVAPIPE", forceLavapipe, sizeof(forceLavapipe)) > 0 &&
                forceLavapipe[0] == '1') {
                char selfPath[MAX_PATH] = {};
                if (GetModuleFileNameA(hModule, selfPath, MAX_PATH) > 0) {
                    std::string modDir(selfPath);
                    size_t lastSlash = modDir.find_last_of("\\/");
                    if (lastSlash != std::string::npos) {
                        std::string lvpIcd = modDir.substr(0, lastSlash + 1) + "lvp_icd.x86_64.json";
                        if (GetFileAttributesA(lvpIcd.c_str()) != INVALID_FILE_ATTRIBUTES) {
                            SetEnvironmentVariableA("VK_ICD_FILENAMES", lvpIcd.c_str());
                            SetEnvironmentVariableA("VK_DRIVER_FILES", lvpIcd.c_str());
                        }
                    }
                }
            }

            InstallNativeGuard();
            break;
        }
        case DLL_PROCESS_DETACH: {
            bool isProcessTermination = (lpReserved != nullptr);
            if (!IsManagedOrToolHostProcess()) {
                ShutdownNativeGuard(isProcessTermination);
            }
            if (!isProcessTermination && hOriginalDll) {
                FreeLibrary(hOriginalDll);
                hOriginalDll = NULL;
            }
            break;
        }
    }
    return TRUE;
}

