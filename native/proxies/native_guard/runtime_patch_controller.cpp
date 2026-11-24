// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "runtime_patch_controller.h"
#include "kongor_assert.h"
#include "kongor_overflow.h"
#include "kongor_constpool.h"
#include "vmt_hook_manager.h"
#include "entity_field_container.h"
#include "../../../src/engine/headers/k2_structs.generated.h"
#include <tlhelp32.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <mutex>
#include <memory>
#include <atomic>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <algorithm>
#include <stdexcept>

extern bool Kongor_TestSehExtractMsvcCppException(const EXCEPTION_RECORD* pRec, char* outBuf, size_t outCap) noexcept;
extern bool Kongor_TestTrampolineUnwindLookup(uintptr_t trampPc, uintptr_t* outModBase, uint32_t* outBeginRva) noexcept;
extern bool Kongor_ProcessPipeStreamLines(const char* rawInput, char* outResp, size_t outCap);
extern void Kongor_EnterProtectedSeh() noexcept;
extern void Kongor_LeaveProtectedSeh() noexcept;

namespace {
struct KongorScopedProtectedSeh {
    KongorScopedProtectedSeh() noexcept { Kongor_EnterProtectedSeh(); }
    ~KongorScopedProtectedSeh() noexcept { Kongor_LeaveProtectedSeh(); }
    KongorScopedProtectedSeh(const KongorScopedProtectedSeh&) = delete;
    KongorScopedProtectedSeh& operator=(const KongorScopedProtectedSeh&) = delete;
};
} // namespace

namespace Kongor {
namespace PatchController {

static constexpr uint64_t FNV_OFFSET_BASIS = 14695981039346656037ULL;
static constexpr uint64_t FNV_PRIME        = 1099511628211ULL;
static constexpr UINT     WM_KONGOR_SAFEPOINT_CMD = WM_USER + 0x1337;

struct ManagedPatchSite {
    const char* internedGroup   = "";
    std::string group;
    std::string name;
    uintptr_t   targetAddr      = 0;
    uintptr_t   trampolineAddr  = 0;
    size_t      trampolineSize  = 0;
    size_t      patchLen        = 0;
    size_t      expectedLen     = 0;
    uint8_t     expectedBytes[MAX_PATCH_BYTES] = {0};
    uint8_t     originalBytes[MAX_PATCH_BYTES] = {0};
    uint8_t     patchedBytes[MAX_PATCH_BYTES]  = {0};
    uintptr_t   regionBase      = 0;
    uint64_t    regionChecksum  = 0;
    bool        isInstalled     = false;
    bool        isEnabled       = false;
    uint32_t    toggleCount     = 0;
    std::string lastStatus      = "UNINITIALIZED";
};

struct ManagedGroupCallback {
    std::string          group;
    GroupStateCallbackFn callback = nullptr;
    bool                 isEnabled = true;
    uint32_t             toggleCount = 0;
};

static std::recursive_mutex g_controllerMutex;
static std::unordered_map<uintptr_t, std::shared_ptr<RegionSnapshot>> g_regionSnapshots;
static std::vector<ManagedPatchSite> g_patchSites;
static std::vector<ManagedGroupCallback> g_groupCallbacks;

static std::atomic<bool>      g_initialized{ false };
static std::atomic<bool>      g_safeModeActive{ false };
static std::string            g_safeModeReason{ "NONE" };
static std::atomic<bool>      g_bootSentinelCleared{ false };
static std::atomic<uint32_t>  g_bootSentinelTrips{ 0 };
static std::vector<std::string> g_bootDisabledGroups;
static std::atomic<DWORD>     g_mainThreadId{ 0 };
static std::atomic<HWND>      g_hMainGameWnd{ NULL };
static std::atomic<uintptr_t> g_simulatedThreadRip{ 0 };
static std::atomic<uint32_t>  g_signatureMismatchCount{ 0 };
static std::atomic<uint32_t>  g_patchOverlapCollisionCount{ 0 };
static std::atomic<uint32_t>  g_ripHazardAbortCount{ 0 };
static std::atomic<uint32_t>  g_totalSafePointSwaps{ 0 };

struct QueuedMainThreadTask {
    MainThreadCallbackFn rawFn = nullptr;
    void* userData = nullptr;
    std::function<void()> taskFn;
};
static std::mutex g_mainThreadCallbackMutex;
static std::vector<QueuedMainThreadTask> g_pendingMainThreadCallbacks;

struct PendingSafePointRequest {
    std::mutex  mutex;
    HANDLE      hDoneEvent = NULL;
    bool        pending    = false;
    std::string group;
    std::string action;
    bool        result     = false;
    std::string jsonOutput;
};
static PendingSafePointRequest g_pendingRequest;

static void LogHookEvent(const char* fmt, ...) {
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);

    OutputDebugStringA(buf);

    static std::mutex s_logMutex;
    std::lock_guard<std::mutex> lock(s_logMutex);
    static FILE* s_fp = nullptr;
    static bool s_opened = false;
    if (!s_opened) {
        s_opened = true;
        CreateDirectoryA("userdata", NULL);
        CreateDirectoryA("userdata/logs", NULL);
        s_fp = fopen("userdata/logs/native_guard_hooks.log", "a");
    }
    if (s_fp) {
        fputs(buf, s_fp);
        fflush(s_fp);
    }
}

void LogPreconditionFailure(const char* file, int line, const char* func, const char* expr) {
    LogHookEvent("[KONGOR ASSERT] [PRECONDITION FAILED] %s:%d in %s(): '%s'\n",
        file ? file : "unknown", line, func ? func : "unknown", expr ? expr : "null");
}

// Option N: Near-module (+/- 1 GB) executable trampoline allocator
static uint8_t* AllocTrampolineNear(uintptr_t targetAddr, size_t allocSize) {
    const uintptr_t gran = 0x10000ULL; // 64 KiB allocation granularity
    const uintptr_t alignedBase = targetAddr & ~(gran - 1ULL);
    const uintptr_t maxRadius = 0x40000000ULL; // 1 GiB radius (well within +/- 2 GiB rel32)

    for (uintptr_t delta = gran; delta <= maxRadius; delta += gran) {
        uintptr_t candidates[2] = {
            (alignedBase > delta) ? (alignedBase - delta) : 0ULL,
            alignedBase + delta
        };
        for (uintptr_t cand : candidates) {
            if (cand < 0x10000ULL) continue;
            MEMORY_BASIC_INFORMATION mbi = {0};
            if (VirtualQuery(reinterpret_cast<void*>(cand), &mbi, sizeof(mbi)) != 0 && mbi.State == MEM_FREE) {
                // [L2-P1-02] Strict W^X: allocate RW first, then seal as RX via VirtualProtect before execution.
                void* pMem = VirtualAlloc(reinterpret_cast<void*>(cand), allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (pMem) return reinterpret_cast<uint8_t*>(pMem);
            }
        }
    }
    return reinterpret_cast<uint8_t*>(VirtualAlloc(NULL, allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
}

static std::string BytesToHex(const uint8_t* data, size_t len) {
    if (!data || len == 0) return "";
    std::string out;
    out.reserve(len * 3);
    char hex[4];
    for (size_t i = 0; i < len; ++i) {
        snprintf(hex, sizeof(hex), i + 1 < len ? "%02X " : "%02X", data[i]);
        out += hex;
    }
    return out;
}

static bool IsPageReadable(void* pageAddr) {
    MEMORY_BASIC_INFORMATION mbi = {0};
    if (VirtualQuery(pageAddr, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT) return false;
    if ((mbi.Protect & PAGE_NOACCESS) || (mbi.Protect & PAGE_GUARD)) return false;
    const DWORD readableFlags = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
                                PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return (mbi.Protect & readableFlags) != 0;
}

static std::string GetBootSentinelPath(bool* outExplicitPath = nullptr) {
    char envPath[MAX_PATH] = {0};
    DWORD len = GetEnvironmentVariableA("KONGOR_BOOT_SENTINEL_PATH", envPath, sizeof(envPath));
    if (len > 0 && len < sizeof(envPath)) {
        if (outExplicitPath) *outExplicitPath = true;
        return std::string(envPath);
    }
    if (outExplicitPath) *outExplicitPath = false;
    return "userdata/logs/native_guard_boot.sentinel";
}

static bool IsGroupBootDisabledLocked(const char* groupName) {
    if (!groupName || !groupName[0]) return false;
    // In Safe Mode, all optional/experimental groups are disarmed; "seh" crash reporting and "inline_legacy" console IPC stay armed
    if (g_safeModeActive.load(std::memory_order_acquire) &&
        _stricmp(groupName, "seh") != 0 &&
        _stricmp(groupName, "inline_legacy") != 0 &&
        _stricmp(groupName, "inline_core") != 0 &&
        _stricmp(groupName, "vfs") != 0 &&
        _stricmp(groupName, "atob") != 0) {
        return true;
    }
    for (const auto& disabled : g_bootDisabledGroups) {
        if (_stricmp(disabled.c_str(), "all") == 0 || _stricmp(disabled.c_str(), groupName) == 0) {
            return true;
        }
    }
    return false;
}

bool IsSafeModeActive() {
    return g_safeModeActive.load(std::memory_order_acquire);
}

const char* GetSafeModeReason() {
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    return g_safeModeReason.c_str();
}

void SetSafeMode(bool active, const char* reason) {
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    g_safeModeActive.store(active, std::memory_order_release);
    g_safeModeReason = (active && reason && reason[0]) ? reason : (active ? "MANUAL" : "NONE");
    if (active) {
        LogHookEvent("[GEODE-SAFE-MODE] Activated (reason=%s) — disarming non-essential hook groups.\n",
            g_safeModeReason.c_str());
    } else {
        LogHookEvent("[GEODE-SAFE-MODE] Deactivated — normal hook group policy restored.\n");
    }
}

// satisfies: OBL-HOOK-BOOT-SENTINEL
static void EvaluateBootSentinelAndGroupSeedLocked() {
    g_bootDisabledGroups.clear();
    char disableEnv[512] = {0};
    DWORD disLen = GetEnvironmentVariableA("KONGOR_PATCH_GROUPS_DISABLE", disableEnv, sizeof(disableEnv));
    if (disLen > 0 && disLen < sizeof(disableEnv)) {
        std::string raw(disableEnv);
        size_t start = 0;
        while (start < raw.size()) {
            size_t end = raw.find_first_of(",;", start);
            if (end == std::string::npos) end = raw.size();
            std::string token = raw.substr(start, end - start);
            while (!token.empty() && (token.front() == ' ' || token.front() == '\t')) token.erase(token.begin());
            while (!token.empty() && (token.back() == ' ' || token.back() == '\t')) token.pop_back();
            if (!token.empty()) {
                g_bootDisabledGroups.push_back(token);
            }
            start = end + 1;
        }
        if (!g_bootDisabledGroups.empty()) {
            LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-BOOT-SENTINEL] Boot group disable seed active: KONGOR_PATCH_GROUPS_DISABLE='%s' (%zu group(s)).\n",
                disableEnv, g_bootDisabledGroups.size());
        }
    }

    bool explicitSentinel = false;
    std::string sentinelPath = GetBootSentinelPath(&explicitSentinel);
    char testModeEnv[16] = {0};
    bool isTestMode = (GetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", testModeEnv, sizeof(testModeEnv)) > 0 &&
                       (testModeEnv[0] == '1' || testModeEnv[0] == 'y' || testModeEnv[0] == 'Y'));

    // (#170) Check explicit KONGOR_SAFE_MODE=1 or ALT+SHIFT chord or last-crashed.sentinel
    char safeModeEnv[16] = {0};
    bool envSafeMode = (GetEnvironmentVariableA("KONGOR_SAFE_MODE", safeModeEnv, sizeof(safeModeEnv)) > 0 &&
                        (safeModeEnv[0] == '1' || safeModeEnv[0] == 't' || safeModeEnv[0] == 'T' || safeModeEnv[0] == 'y' || safeModeEnv[0] == 'Y'));

    char simChordEnv[16] = {0};
    bool simAltShift = (GetEnvironmentVariableA("KONGOR_SIMULATE_ALT_SHIFT_SAFE_MODE", simChordEnv, sizeof(simChordEnv)) > 0 &&
                        simChordEnv[0] == '1');
    bool hwAltShift = (!isTestMode) &&
                      ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) &&
                      ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0);

    if (envSafeMode) {
        SetSafeMode(true, "KONGOR_SAFE_MODE_ENV");
    } else if (simAltShift || hwAltShift) {
        SetSafeMode(true, "ALT_SHIFT_CHORD");
    }

    bool foundCrashSentinel = false;
    if (!isTestMode || explicitSentinel) {
        const char* crashSentinels[2] = {
            "logs/crash/last-crashed.sentinel",
            "userdata/logs/last-crashed.sentinel"
        };
        for (const char* csPath : crashSentinels) {
            FILE* fpCrash = fopen(csPath, "rb");
            if (fpCrash) {
                fclose(fpCrash);
                foundCrashSentinel = true;
                SetSafeMode(true, "LAST_CRASHED_SENTINEL");
                break;
            }
        }

        FILE* fpExisting = fopen(sentinelPath.c_str(), "rb");
        if (fpExisting) {
            char prevBuf[256] = {0};
            size_t nRead = fread(prevBuf, 1, sizeof(prevBuf) - 1, fpExisting);
            prevBuf[nRead] = '\0';
            fclose(fpExisting);

            unsigned long prevPid = 0;
            bool prevPidRunning = false;
            if (sscanf(prevBuf, "{\"pid\":%lu", &prevPid) == 1 && prevPid > 0) {
                HANDLE hPrev = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, prevPid);
                if (hPrev) {
                    DWORD exitCode = 0;
                    if (GetExitCodeProcess(hPrev, &exitCode) && exitCode == STILL_ACTIVE) {
                        prevPidRunning = true;
                    }
                    CloseHandle(hPrev);
                }
            }
            if (foundCrashSentinel || prevPidRunning || (explicitSentinel && prevPid == 0)) {
                SetSafeMode(true, "BOOT_SENTINEL_UNCLEARED");
            } else if (!envSafeMode && !simAltShift && !hwAltShift) {
                g_safeModeActive.store(false, std::memory_order_release);
                g_safeModeReason = "NONE";
            }
            g_bootSentinelCleared.store(false, std::memory_order_release);
            uint32_t trips = g_bootSentinelTrips.fetch_add(1, std::memory_order_relaxed) + 1;
            LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-BOOT-SENTINEL] [SAFE-MODE] Uncleared boot crash sentinel detected at '%s' (prev='%s', trips=%u)! Auto-disarming optional hook groups.\n",
                sentinelPath.c_str(), prevBuf, trips);
        } else {
            if (!envSafeMode && !simAltShift && !hwAltShift && !foundCrashSentinel) {
                g_safeModeActive.store(false, std::memory_order_release);
                g_safeModeReason = "NONE";
            }
            g_bootSentinelCleared.store(false, std::memory_order_release);
            if (!explicitSentinel) {
                CreateDirectoryA("userdata", NULL);
                CreateDirectoryA("userdata/logs", NULL);
            }
            FILE* fpWrite = fopen(sentinelPath.c_str(), "wb");
            if (fpWrite) {
                fprintf(fpWrite, "{\"pid\":%lu,\"state\":\"BOOT_IN_PROGRESS\"}\n", (unsigned long)GetCurrentProcessId());
                fflush(fpWrite);
                fclose(fpWrite);
                LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-BOOT-SENTINEL] Armed boot crash sentinel at '%s' (PID=%lu).\n",
                    sentinelPath.c_str(), (unsigned long)GetCurrentProcessId());
            }
        }
    } else if (!envSafeMode && !simAltShift) {
        g_safeModeActive.store(false, std::memory_order_release);
        g_safeModeReason = "NONE";
    }

    // Apply Safe Mode / boot seed to any already-registered group callbacks
    for (auto& gc : g_groupCallbacks) {
        if (IsGroupBootDisabledLocked(gc.group.c_str()) && gc.isEnabled) {
            gc.isEnabled = false;
            if (gc.callback) gc.callback(false);
            LogHookEvent("[KONGOR PATCH CTRL] Auto-disarmed group callback '%s' due to Safe Mode / boot seed.\n", gc.group.c_str());
        }
    }
}

void Initialize() {
    if (g_initialized.exchange(true)) return;
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    if (!g_pendingRequest.hDoneEvent) {
        g_pendingRequest.hDoneEvent = CreateEventA(NULL, FALSE, FALSE, NULL);
    }
    EvaluateBootSentinelAndGroupSeedLocked();
    LogHookEvent("[KONGOR PATCH CTRL] RuntimePatchController initialized (64 KiB RegionSnapshot + Safe-Point Quiescence + Boot Sentinel active, SafeMode=%s, Reason=%s).\n",
        g_safeModeActive.load(std::memory_order_acquire) ? "TRUE" : "FALSE",
        g_safeModeReason.c_str());
}

void RegisterGroupCallback(const char* groupName, GroupStateCallbackFn callback, bool initialEnabled) {
    if (!groupName || !callback) return;
    Initialize();
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    bool effectiveEnabled = initialEnabled && !IsGroupBootDisabledLocked(groupName);
    for (auto& gc : g_groupCallbacks) {
        if (_stricmp(gc.group.c_str(), groupName) == 0) {
            gc.callback = callback;
            gc.isEnabled = effectiveEnabled;
            return;
        }
    }
    ManagedGroupCallback entry;
    entry.group = groupName;
    entry.callback = callback;
    entry.isEnabled = effectiveEnabled;
    entry.toggleCount = 0;
    g_groupCallbacks.push_back(entry);
    LogHookEvent("[KONGOR PATCH CTRL] Registered group callback for '%s' (initial=%s).\n",
        groupName, effectiveEnabled ? "ON" : "OFF");
}

// satisfies: OBL-HOOK-REGION-SNAPSHOT
static std::shared_ptr<RegionSnapshot> GetOrCreateRegionSnapshot(uintptr_t alignedBase) {
    auto it = g_regionSnapshots.find(alignedBase);
    if (it != g_regionSnapshots.end()) {
        return it->second;
    }

    auto snap = std::make_shared<RegionSnapshot>();
    snap->baseAddress = alignedBase;
    snap->capturedPagesCount = 0;
    memset(snap->validPageMask, 0, sizeof(snap->validPageMask));
    memset(snap->bytes, 0, sizeof(snap->bytes));

    uint64_t hash = FNV_OFFSET_BASIS;
    for (size_t pageIdx = 0; pageIdx < PAGES_PER_REGION; ++pageIdx) {
        uintptr_t pageAddr = alignedBase + (pageIdx * PAGE_SIZE_4K);
        if (IsPageReadable(reinterpret_cast<void*>(pageAddr))) {
            memcpy(snap->bytes + (pageIdx * PAGE_SIZE_4K),
                   reinterpret_cast<const void*>(pageAddr),
                   PAGE_SIZE_4K);
            snap->validPageMask[pageIdx] = 1;
            snap->capturedPagesCount++;
            const uint8_t* pPage = snap->bytes + (pageIdx * PAGE_SIZE_4K);
            for (size_t b = 0; b < PAGE_SIZE_4K; ++b) {
                hash = (hash ^ pPage[b]) * FNV_PRIME;
            }
        }
    }
    snap->checksumFnv1a = hash;
    g_regionSnapshots[alignedBase] = snap;

    LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-REGION-SNAPSHOT] Captured 64 KiB RegionSnapshot @ 0x%llX (Pages=%u/16, FNV1a=0x%016llX).\n",
        (unsigned long long)alignedBase, snap->capturedPagesCount, (unsigned long long)snap->checksumFnv1a);
    return snap;
}

bool EnsureRegionSnapshotForRange(void* targetAddr, size_t len, uint64_t* outChecksum) {
    if (!targetAddr || len == 0) return false;
    Initialize();
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);

    uintptr_t startAddr = reinterpret_cast<uintptr_t>(targetAddr);
    uintptr_t endAddr   = startAddr + len - 1;
    uintptr_t base1     = startAddr & ~(REGION_SNAPSHOT_SIZE - 1);
    uintptr_t base2     = endAddr & ~(REGION_SNAPSHOT_SIZE - 1);

    auto snap1 = GetOrCreateRegionSnapshot(base1);
    if (!snap1 || snap1->capturedPagesCount == 0) return false;

    if (base2 != base1) {
        auto snap2 = GetOrCreateRegionSnapshot(base2);
        if (!snap2 || snap2->capturedPagesCount == 0) return false;
    }

    if (outChecksum) *outChecksum = snap1->checksumFnv1a;
    return true;
}

bool ReadOriginalBytesFromSnapshot(void* targetAddr, uint8_t* outBuf, size_t len) {
    if (!targetAddr || !outBuf || len == 0) return false;
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);

    uintptr_t addr = reinterpret_cast<uintptr_t>(targetAddr);
    for (size_t i = 0; i < len; ++i) {
        uintptr_t cur = addr + i;
        uintptr_t regionBase = cur & ~(REGION_SNAPSHOT_SIZE - 1);
        size_t offsetInRegion = cur - regionBase;
        size_t pageIdx = offsetInRegion / PAGE_SIZE_4K;

        auto it = g_regionSnapshots.find(regionBase);
        if (it == g_regionSnapshots.end() || !it->second) return false;
        if (it->second->validPageMask[pageIdx] == 0) return false;

        outBuf[i] = it->second->bytes[offsetInRegion];
    }
    return true;
}

// satisfies: OBL-HOOK-QUIESCE
// satisfies: OBL-HOOK-ICACHE-FLUSH
static bool VerifyThreadsQuiescentAndSwap(ManagedPatchSite& site, bool enablePatch) {
    if (!site.targetAddr || site.patchLen == 0 || site.patchLen > MAX_PATCH_BYTES) {
        site.lastStatus = "INVALID_SITE_PARAMS";
        return false;
    }

    // 1. Check simulated RIP hazard first (deterministic unit-test seam for OBL-HOOK-QUIESCE)
    uintptr_t simRip = g_simulatedThreadRip.load(std::memory_order_acquire);
    if (simRip != 0) {
        bool inTarget = (simRip >= site.targetAddr && simRip < (site.targetAddr + site.patchLen));
        bool inTramp  = (site.trampolineAddr != 0 && simRip >= site.trampolineAddr &&
                         simRip < (site.trampolineAddr + site.trampolineSize));
        if (inTarget || inTramp) {
            g_ripHazardAbortCount.fetch_add(1, std::memory_order_relaxed);
            site.lastStatus = "RIP_HAZARD_ABORT";
            LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-QUIESCE] ABORT toggle on '%s::%s': Simulated thread RIP 0x%llX is inside hazard window [0x%llX..0x%llX)!\n",
                site.group.c_str(), site.name.c_str(),
                (unsigned long long)simRip,
                (unsigned long long)site.targetAddr,
                (unsigned long long)(site.targetAddr + site.patchLen));
            return false;
        }
    }

    // 2. Suspend sibling threads and verify no live thread RIP is inside [targetAddr, targetAddr + patchLen)
    char testModeEnv[16] = {0};
    bool isUnitTestMode = (GetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", testModeEnv, sizeof(testModeEnv)) > 0 &&
                           testModeEnv[0] == '1');

    std::vector<HANDLE> suspendedThreads;
    DWORD currentPid = GetCurrentProcessId();
    DWORD currentTid = GetCurrentThreadId();

    if (!isUnitTestMode) {
        HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (hSnap != INVALID_HANDLE_VALUE) {
            THREADENTRY32 te = {0};
            te.dwSize = sizeof(te);
            if (Thread32First(hSnap, &te)) {
                do {
                    if (te.th32OwnerProcessID == currentPid && te.th32ThreadID != currentTid) {
                        HANDLE hThread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
                                                    FALSE, te.th32ThreadID);
                        if (hThread) {
                            if (SuspendThread(hThread) != (DWORD)-1) {
                                suspendedThreads.push_back(hThread);
                            } else {
                                CloseHandle(hThread);
                            }
                        }
                    }
                } while (Thread32Next(hSnap, &te));
            }
            CloseHandle(hSnap);
        }

        for (HANDLE hThread : suspendedThreads) {
            CONTEXT ctx = {0};
            ctx.ContextFlags = CONTEXT_CONTROL;
            if (GetThreadContext(hThread, &ctx)) {
                uintptr_t rip = static_cast<uintptr_t>(ctx.Rip);
                bool inTarget = (rip >= site.targetAddr && rip < (site.targetAddr + site.patchLen));
                bool inTramp  = (site.trampolineAddr != 0 && rip >= site.trampolineAddr &&
                                 rip < (site.trampolineAddr + site.trampolineSize));
                if (inTarget || inTramp) {
                    for (HANDLE hResume : suspendedThreads) {
                        ResumeThread(hResume);
                        CloseHandle(hResume);
                    }
                    g_ripHazardAbortCount.fetch_add(1, std::memory_order_relaxed);
                    site.lastStatus = "RIP_HAZARD_ABORT";
                    LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-QUIESCE] ABORT toggle on '%s::%s': Live thread RIP 0x%llX is inside hazard window!\n",
                        site.group.c_str(), site.name.c_str(), (unsigned long long)rip);
                    return false;
                }
            }
        }
    }

    // 3. Apply originalBytes (from 64 KiB RegionSnapshot) or patchedBytes under VirtualProtect
    uint8_t desiredBytes[MAX_PATCH_BYTES] = {0};
    if (enablePatch) {
        memcpy(desiredBytes, site.patchedBytes, site.patchLen);
    } else {
        if (!ReadOriginalBytesFromSnapshot(reinterpret_cast<void*>(site.targetAddr), desiredBytes, site.patchLen)) {
            memcpy(desiredBytes, site.originalBytes, site.patchLen);
        }
    }

    void* pTarget = reinterpret_cast<void*>(site.targetAddr);
    DWORD oldProt = 0;
    bool swapped = false;
    if (VirtualProtect(pTarget, site.patchLen, PAGE_EXECUTE_READWRITE, &oldProt)) {
        memcpy(pTarget, desiredBytes, site.patchLen);
        VirtualProtect(pTarget, site.patchLen, oldProt, &oldProt);
        // satisfies: OBL-HOOK-ICACHE-FLUSH
        FlushInstructionCache(GetCurrentProcess(), pTarget, site.patchLen);
        swapped = true;
    }

    for (HANDLE hResume : suspendedThreads) {
        ResumeThread(hResume);
        CloseHandle(hResume);
    }

    if (!swapped) {
        site.lastStatus = "VIRTUAL_PROTECT_FAILED";
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] VirtualProtect failed @ 0x%llX for '%s::%s'.\n",
            (unsigned long long)site.targetAddr, site.group.c_str(), site.name.c_str());
        return false;
    }

    site.isEnabled = enablePatch;
    site.toggleCount++;
    site.lastStatus = enablePatch ? "ACTIVE_HOOKED" : "RESTORED_VANILLA";
    g_totalSafePointSwaps.fetch_add(1, std::memory_order_relaxed);

    LogHookEvent("[KONGOR PATCH CTRL] [SAFE-POINT SWAP] '%s::%s' @ 0x%llX -> %s (%zu bytes: %s, ToggleCount=%u).\n",
        site.group.c_str(), site.name.c_str(),
        (unsigned long long)site.targetAddr,
        site.lastStatus.c_str(),
        site.patchLen,
        BytesToHex(desiredBytes, site.patchLen).c_str(),
        site.toggleCount);
    return true;
}

// satisfies: OBL-HOOK-NO-OVERLAP
static bool CheckPatchIntervalCollisionLocked(
    const char* groupName,
    const char* siteName,
    uintptr_t startAddr,
    size_t patchLen,
    ManagedPatchSite** outExactMatch = nullptr)
{
    if (outExactMatch) *outExactMatch = nullptr;
    if (patchLen == 0) return false;

    uintptr_t thisStart = startAddr;
    uintptr_t thisEnd   = startAddr + patchLen - 1;

    for (auto& existing : g_patchSites) {
        if (existing.patchLen == 0) continue;
        // Allow idempotent re-registration of the exact same hook site at the exact same address
        if (existing.targetAddr == thisStart &&
            existing.patchLen == patchLen &&
            _stricmp(existing.group.c_str(), groupName ? groupName : "") == 0 &&
            _stricmp(existing.name.c_str(), siteName ? siteName : "") == 0) {
            if (outExactMatch) *outExactMatch = &existing;
            return false;
        }

        uintptr_t otherStart = existing.targetAddr;
        uintptr_t otherEnd   = existing.targetAddr + existing.patchLen - 1;
        // Closed-interval overlap check: [thisStart, thisEnd] intersects [otherStart, otherEnd]
        if (thisStart <= otherEnd && thisEnd >= otherStart) {
            g_patchOverlapCollisionCount.fetch_add(1, std::memory_order_relaxed);
            LogHookEvent(
                "[KONGOR PATCH CTRL] [OBL-HOOK-NO-OVERLAP] COLLISION: Candidate '%s::%s' [0x%llX..0x%llX] (%zu bytes) overlaps existing '%s::%s' [0x%llX..0x%llX] (%zu bytes)! Refusing patch.\n",
                groupName ? groupName : "null",
                siteName ? siteName : "null",
                (unsigned long long)thisStart,
                (unsigned long long)thisEnd,
                patchLen,
                existing.group.c_str(),
                existing.name.c_str(),
                (unsigned long long)otherStart,
                (unsigned long long)otherEnd,
                existing.patchLen);
            return true;
        }
    }
    return false;
}

// #268: HDE64 Prologue Validator & Instruction-Boundary Length Calculator
// Inspired by TsudaKageyu/minhook (src/trampoline.c & src/hde/hde64.c)
namespace {

struct DecodedInst64 {
    uint8_t length          = 0;
    uint8_t opcode          = 0;
    uint8_t opcode2         = 0;
    bool    isTwoByteOpcode = false;
    bool    hasModRm        = false;
    uint8_t modrm           = 0;
    bool    isRipRelative   = false;
    bool    isRelBranch     = false;
    bool    isRel32CallOrJmp= false;
    bool    isRetOrInt3     = false;
    bool    decodeError     = false;
};

DecodedInst64 DecodeOneX64Instruction(const uint8_t* code, size_t maxBytes) noexcept {
    DecodedInst64 inst{};
    if (!code || maxBytes == 0) {
        inst.decodeError = true;
        return inst;
    }

    size_t pos = 0;
    bool operandSize16 = false;
    bool addressSize32 = false;
    bool rexW = false;

    while (pos < maxBytes && pos < 15) {
        uint8_t b = code[pos];
        if (b == 0x66) {
            operandSize16 = true;
            ++pos;
        } else if (b == 0x67) {
            addressSize32 = true;
            ++pos;
        } else if (b == 0xF0 || b == 0xF2 || b == 0xF3 ||
                   b == 0x26 || b == 0x2E || b == 0x36 || b == 0x3E || b == 0x64 || b == 0x65) {
            ++pos;
        } else if ((b & 0xF0) == 0x40) {
            rexW = ((b & 0x08) != 0);
            ++pos;
            break;
        } else {
            break;
        }
    }

    if (pos >= maxBytes) {
        inst.decodeError = true;
        return inst;
    }

    uint8_t op = code[pos++];
    inst.opcode = op;

    if ((op >= 0x50 && op <= 0x5F) || op == 0x90 || op == 0x98 || op == 0x99 ||
        op == 0xC9 || op == 0xF8 || op == 0xF9 || op == 0xFC || op == 0xFD) {
        inst.length = static_cast<uint8_t>(pos);
        return inst;
    }
    if (op == 0xC3 || op == 0xCB || op == 0xCC) {
        inst.isRetOrInt3 = true;
        inst.length = static_cast<uint8_t>(pos);
        return inst;
    }
    if (op == 0xC2 || op == 0xCA) {
        if (pos + 2 > maxBytes) { inst.decodeError = true; return inst; }
        inst.isRetOrInt3 = true;
        inst.length = static_cast<uint8_t>(pos + 2);
        return inst;
    }
    if ((op >= 0x70 && op <= 0x7F) || (op >= 0xE0 && op <= 0xE3) || op == 0xEB) {
        if (pos + 1 > maxBytes) { inst.decodeError = true; return inst; }
        inst.isRelBranch = true;
        inst.length = static_cast<uint8_t>(pos + 1);
        return inst;
    }
    if (op == 0xE8 || op == 0xE9) {
        if (pos + 4 > maxBytes) { inst.decodeError = true; return inst; }
        inst.isRelBranch = true;
        inst.isRel32CallOrJmp = true;
        inst.length = static_cast<uint8_t>(pos + 4);
        return inst;
    }
    if (op == 0x04 || op == 0x0C || op == 0x14 || op == 0x1C ||
        op == 0x24 || op == 0x2C || op == 0x34 || op == 0x3C || op == 0xA8 || op == 0x6A) {
        if (pos + 1 > maxBytes) { inst.decodeError = true; return inst; }
        inst.length = static_cast<uint8_t>(pos + 1);
        return inst;
    }
    if (op == 0x05 || op == 0x0D || op == 0x15 || op == 0x1D ||
        op == 0x25 || op == 0x2D || op == 0x35 || op == 0x3D || op == 0xA9 || op == 0x68) {
        size_t immLen = (operandSize16 && !rexW) ? 2 : 4;
        if (pos + immLen > maxBytes) { inst.decodeError = true; return inst; }
        inst.length = static_cast<uint8_t>(pos + immLen);
        return inst;
    }
    if (op >= 0xB0 && op <= 0xB7) {
        if (pos + 1 > maxBytes) { inst.decodeError = true; return inst; }
        inst.length = static_cast<uint8_t>(pos + 1);
        return inst;
    }
    if (op >= 0xB8 && op <= 0xBF) {
        size_t immLen = rexW ? 8 : (operandSize16 ? 2 : 4);
        if (pos + immLen > maxBytes) { inst.decodeError = true; return inst; }
        inst.length = static_cast<uint8_t>(pos + immLen);
        return inst;
    }

    size_t immBytesAfterModRm = 0;
    if (op == 0x0F) {
        if (pos >= maxBytes) { inst.decodeError = true; return inst; }
        uint8_t op2 = code[pos++];
        inst.isTwoByteOpcode = true;
        inst.opcode2 = op2;

        if (op2 == 0x05 || op2 == 0x31 || op2 == 0xA2) {
            inst.length = static_cast<uint8_t>(pos);
            return inst;
        }
        if (op2 >= 0x80 && op2 <= 0x8F) {
            if (pos + 4 > maxBytes) { inst.decodeError = true; return inst; }
            inst.isRelBranch = true;
            inst.length = static_cast<uint8_t>(pos + 4);
            return inst;
        }
        if (op2 == 0xBA || (op2 >= 0x70 && op2 <= 0x73) || op2 == 0xA4 || op2 == 0xAC ||
            op2 == 0xC2 || op2 == 0xC4 || op2 == 0xC5 || op2 == 0xC6) {
            immBytesAfterModRm = 1;
        }
        inst.hasModRm = true;
    } else {
        inst.hasModRm = true;
        if (op == 0x80 || op == 0x82 || op == 0x83 || op == 0xC0 || op == 0xC1 || op == 0xC6 || op == 0x6B) {
            immBytesAfterModRm = 1;
        } else if (op == 0x81 || op == 0xC7 || op == 0x69) {
            immBytesAfterModRm = (operandSize16 && !rexW) ? 2 : 4;
        } else if (op == 0xF6 || op == 0xF7) {
            if (pos >= maxBytes) { inst.decodeError = true; return inst; }
            uint8_t peekModRm = code[pos];
            uint8_t regField = (peekModRm >> 3) & 0x07;
            if (regField == 0 || regField == 1) {
                immBytesAfterModRm = (op == 0xF6) ? 1 : ((operandSize16 && !rexW) ? 2 : 4);
            }
        }
    }

    if (inst.hasModRm) {
        if (pos >= maxBytes) { inst.decodeError = true; return inst; }
        uint8_t modrm = code[pos++];
        inst.modrm = modrm;
        uint8_t mod = (modrm >> 6) & 0x03;
        uint8_t rm  = modrm & 0x07;

        if (mod != 3) {
            if (rm == 4 && !addressSize32) {
                if (pos >= maxBytes) { inst.decodeError = true; return inst; }
                uint8_t sib = code[pos++];
                uint8_t base = sib & 0x07;
                if (mod == 0 && base == 5) {
                    if (pos + 4 > maxBytes) { inst.decodeError = true; return inst; }
                    pos += 4;
                }
            }
            if (mod == 0 && rm == 5) {
                if (pos + 4 > maxBytes) { inst.decodeError = true; return inst; }
                inst.isRipRelative = true;
                pos += 4;
            } else if (mod == 1) {
                if (pos + 1 > maxBytes) { inst.decodeError = true; return inst; }
                pos += 1;
            } else if (mod == 2) {
                if (pos + 4 > maxBytes) { inst.decodeError = true; return inst; }
                pos += 4;
            }
        }
    }

    if (pos + immBytesAfterModRm > maxBytes || pos + immBytesAfterModRm > 15) {
        inst.decodeError = true;
        return inst;
    }
    pos += immBytesAfterModRm;
    inst.length = static_cast<uint8_t>(pos);
    return inst;
}

} // anonymous namespace

bool ValidateHookPrologue64(
    const uint8_t* codeBytes,
    size_t stolenBytes,
    bool requiresCallableTrampoline,
    PrologueValidationResult* outResult) noexcept
{
    PrologueValidationResult localRes{};
    if (!codeBytes || stolenBytes < 14 || stolenBytes > MAX_PATCH_BYTES) {
        localRes.isValid = false;
        localRes.refusalReason = "INVALID_STOLEN_BYTE_LENGTH";
        if (outResult) *outResult = localRes;
        return false;
    }

    if (!requiresCallableTrampoline) {
        localRes.isValid = true;
        localRes.decodedBytes = stolenBytes;
        localRes.refusalReason = "NONE_FULL_REPLACEMENT_OPTOUT";
        if (outResult) *outResult = localRes;
        return true;
    }

    size_t offset = 0;
    while (offset < stolenBytes) {
        DecodedInst64 inst = DecodeOneX64Instruction(codeBytes + offset, stolenBytes - offset);
        if (inst.decodeError || inst.length == 0) {
            localRes.isValid = false;
            localRes.decodedBytes = offset;
            localRes.faultOffset = offset;
            localRes.faultOpcode = codeBytes[offset];
            localRes.refusalReason = "REFUSED_MID_INSTRUCTION_SPLIT";
            if (outResult) *outResult = localRes;
            return false;
        }
        if (inst.isRipRelative) {
            localRes.isValid = false;
            localRes.decodedBytes = offset;
            localRes.faultOffset = offset;
            localRes.faultOpcode = inst.opcode;
            localRes.refusalReason = "REFUSED_RIP_RELATIVE_OPERAND";
            if (outResult) *outResult = localRes;
            return false;
        }
        if (inst.isRetOrInt3) {
            localRes.isValid = false;
            localRes.decodedBytes = offset;
            localRes.faultOffset = offset;
            localRes.faultOpcode = inst.opcode;
            localRes.refusalReason = "REFUSED_EARLY_RET_OR_INT3";
            if (outResult) *outResult = localRes;
            return false;
        }
        if (inst.isRelBranch) {
            if (!(offset == 0 && inst.isRel32CallOrJmp)) {
                localRes.isValid = false;
                localRes.decodedBytes = offset;
                localRes.faultOffset = offset;
                localRes.faultOpcode = inst.isTwoByteOpcode ? inst.opcode2 : inst.opcode;
                localRes.refusalReason = "REFUSED_RELATIVE_BRANCH_IN_PROLOGUE";
                if (outResult) *outResult = localRes;
                return false;
            }
        }
        offset += inst.length;
    }

    if (offset != stolenBytes) {
        localRes.isValid = false;
        localRes.decodedBytes = offset;
        localRes.faultOffset = offset;
        localRes.refusalReason = "REFUSED_MID_INSTRUCTION_SPLIT";
        if (outResult) *outResult = localRes;
        return false;
    }

    localRes.isValid = true;
    localRes.decodedBytes = offset;
    localRes.refusalReason = "NONE";
    if (outResult) *outResult = localRes;
    return true;
}

size_t ComputeSafeHookLength64(
    const uint8_t* codeBytes,
    size_t maxScanBytes,
    size_t minRequiredBytes) noexcept
{
    if (!codeBytes || minRequiredBytes < 14 || maxScanBytes < minRequiredBytes) {
        return 0;
    }
    size_t limit = (std::min)(maxScanBytes, MAX_PATCH_BYTES);
    size_t offset = 0;
    while (offset < minRequiredBytes && offset < limit) {
        DecodedInst64 inst = DecodeOneX64Instruction(codeBytes + offset, limit - offset);
        if (inst.decodeError || inst.length == 0 || inst.isRipRelative || inst.isRetOrInt3) {
            return 0;
        }
        if (inst.isRelBranch && !(offset == 0 && inst.isRel32CallOrJmp)) {
            return 0;
        }
        offset += inst.length;
    }
    return (offset >= minRequiredBytes && offset <= limit) ? offset : 0;
}

// satisfies: OBL-HOOK-PROLOGUE-SIG
bool InstallManagedInlineHook64(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    void* pHook,
    void** ppOrigTrampoline,
    size_t stolenBytes,
    const uint8_t* expectedPrologue,
    size_t expectedPrologueLen)
{
    Initialize();
    if (!groupName || !siteName || !pTarget || !pHook || stolenBytes < 14 || stolenBytes > MAX_PATCH_BYTES) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] Invalid parameters for InstallManagedInlineHook64('%s', '%s', stolen=%zu).\n",
            groupName ? groupName : "null", siteName ? siteName : "null", stolenBytes);
    }
    KONGOR_RETURN_VAL_IF_FAIL(groupName != nullptr && siteName != nullptr && pTarget != nullptr && pHook != nullptr && stolenBytes >= 14 && stolenBytes <= MAX_PATCH_BYTES, false);

    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    const char* internedGroup = Kongor::ConstStringPool::Instance().Intern(groupName);
    uintptr_t targetAddr = reinterpret_cast<uintptr_t>(pTarget);

    ManagedPatchSite* exactExisting = nullptr;
    if (CheckPatchIntervalCollisionLocked(groupName, siteName, targetAddr, stolenBytes, &exactExisting)) {
        return false;
    }
    if (exactExisting != nullptr) {
        if (ppOrigTrampoline && exactExisting->trampolineAddr != 0) {
            *ppOrigTrampoline = reinterpret_cast<void*>(exactExisting->trampolineAddr);
        }
        return exactExisting->isInstalled;
    }

    // 1. Verify expected prologue bytes before touching memory (satisfies: OBL-HOOK-PROLOGUE-SIG)
    if (expectedPrologue && expectedPrologueLen > 0) {
        size_t checkLen = (std::min)(expectedPrologueLen, stolenBytes);
        if (memcmp(pTarget, expectedPrologue, checkLen) != 0) {
            bool autoHealed = false;
            char autoHealEnv[16] = {0};
            bool allowAutoHeal = (strstr(groupName, "autoheal") != nullptr) ||
                (GetEnvironmentVariableA("KONGOR_ENABLE_SIG_AUTOHEAL", autoHealEnv, sizeof(autoHealEnv)) > 0 && autoHealEnv[0] == '1');
            if (allowAutoHeal) {
                // Option B: rz-sign style +/- 256 byte signature auto-healing scan
                for (intptr_t delta = -256; delta <= 256; ++delta) {
                    if (delta == 0) continue;
                    uintptr_t candAddr = static_cast<uintptr_t>(static_cast<intptr_t>(targetAddr) + delta);
                    void* candPtr = reinterpret_cast<void*>(candAddr);
                    if (IsPageReadable(candPtr) && IsPageReadable(reinterpret_cast<uint8_t*>(candPtr) + stolenBytes - 1)) {
                        if (memcmp(candPtr, expectedPrologue, checkLen) == 0) {
                            LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-AUTOHEAL] Relocated '%s::%s' from 0x%llX to 0x%llX (delta=%lld bytes) via prologue signature scan.\n",
                                groupName, siteName, (unsigned long long)targetAddr, (unsigned long long)candAddr, (long long)delta);
                            pTarget = candPtr;
                            targetAddr = candAddr;
                            autoHealed = true;
                            break;
                        }
                    }
                }
            }
            if (!autoHealed) {
                g_signatureMismatchCount.fetch_add(1, std::memory_order_relaxed);
                std::string actualHex = BytesToHex(reinterpret_cast<const uint8_t*>(pTarget), checkLen);
                std::string expectHex = BytesToHex(expectedPrologue, checkLen);
                LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-PROLOGUE-SIG] MISMATCH for '%s::%s' @ 0x%llX! Expected [%s], Actual [%s]. Refusing detour.\n",
                    groupName, siteName, (unsigned long long)targetAddr, expectHex.c_str(), actualHex.c_str());
                return false;
            }
            // Re-verify interval collision if auto-healed to a relocated address
            if (CheckPatchIntervalCollisionLocked(groupName, siteName, targetAddr, stolenBytes, &exactExisting)) {
                return false;
            }
        }
    }

    // 1b. (#268) HDE64 Prologue Validator when a callable trampoline is requested (ppOrigTrampoline != nullptr)
    bool requiresCallableTrampoline = (ppOrigTrampoline != nullptr);
    PrologueValidationResult valRes{};
    if (!ValidateHookPrologue64(reinterpret_cast<const uint8_t*>(pTarget), stolenBytes, requiresCallableTrampoline, &valRes)) {
        LogHookEvent("[KONGOR PATCH CTRL] [HDE64-PROLOGUE-VALIDATOR] REFUSED trampoline hook '%s::%s' @ 0x%llX (Stolen=%zu): %s at offset %zu (opcode=0x%02X).\n",
            groupName, siteName, (unsigned long long)targetAddr, stolenBytes,
            valRes.refusalReason ? valRes.refusalReason : "UNKNOWN", valRes.faultOffset, valRes.faultOpcode);
        if (ppOrigTrampoline) *ppOrigTrampoline = nullptr;
        return false;
    }

    // 2. Capture 64 KiB RegionSnapshot before modifying target page (satisfies: OBL-HOOK-REGION-SNAPSHOT)
    uint64_t regionChecksum = 0;
    if (!EnsureRegionSnapshotForRange(pTarget, stolenBytes, &regionChecksum)) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] Failed to capture 64 KiB RegionSnapshot for '%s::%s' @ 0x%llX.\n",
            groupName, siteName, (unsigned long long)targetAddr);
        return false;
    }

    // 3. Allocate executable trampoline near target module (Option N & Option T)
    size_t trampSize = requiresCallableTrampoline ? 64 : 0;
    uint8_t* pTrampoline = requiresCallableTrampoline ? AllocTrampolineNear(targetAddr, trampSize) : nullptr;
    if (requiresCallableTrampoline && !pTrampoline) {
        return false;
    }

    ManagedPatchSite site;
    site.internedGroup  = internedGroup;
    site.group          = groupName;
    site.name           = siteName;
    site.targetAddr     = targetAddr;
    site.trampolineAddr = reinterpret_cast<uintptr_t>(pTrampoline);
    site.trampolineSize = trampSize;
    site.patchLen       = stolenBytes;
    site.regionBase     = targetAddr & ~(REGION_SNAPSHOT_SIZE - 1);
    site.regionChecksum = regionChecksum;

    memcpy(site.originalBytes, pTarget, stolenBytes);
    auto snapIt = g_regionSnapshots.find(site.regionBase);
    if (snapIt != g_regionSnapshots.end() && snapIt->second) {
        size_t offInRegion = targetAddr - site.regionBase;
        if (offInRegion + stolenBytes <= REGION_SNAPSHOT_SIZE) {
            memcpy(snapIt->second->bytes + offInRegion, site.originalBytes, stolenBytes);
        }
    }
    if (expectedPrologue && expectedPrologueLen > 0) {
        site.expectedLen = (std::min)(expectedPrologueLen, MAX_PATCH_BYTES);
        memcpy(site.expectedBytes, expectedPrologue, site.expectedLen);
    } else {
        site.expectedLen = stolenBytes;
        memcpy(site.expectedBytes, site.originalBytes, stolenBytes);
    }

    // Option N + Option T: Populate trampoline with relocated/widened relative branches + 14-byte jump back
    size_t trampWriteOff = 0;
    if (pTrampoline != nullptr) {
    if ((site.originalBytes[0] == 0xE8 || site.originalBytes[0] == 0xE9) && stolenBytes >= 5) {
        uint8_t opcode = site.originalBytes[0];
        int32_t origDisp = *reinterpret_cast<const int32_t*>(site.originalBytes + 1);
        uintptr_t branchTarget = static_cast<uintptr_t>(static_cast<intptr_t>(targetAddr + 5) + static_cast<intptr_t>(origDisp));
        uintptr_t trampInstEnd = reinterpret_cast<uintptr_t>(pTrampoline) + 5;
        int64_t newDelta = static_cast<int64_t>(branchTarget) - static_cast<int64_t>(trampInstEnd);
        bool forceWiden = (strstr(siteName, "widen") != nullptr);

        if (!forceWiden && Kongor::MathExtras::IsInt32(newDelta)) {
            pTrampoline[0] = opcode;
            *reinterpret_cast<int32_t*>(pTrampoline + 1) = static_cast<int32_t>(newDelta);
            trampWriteOff = 5;
            LogHookEvent("[KONGOR PATCH CTRL] [TRAMPOLINE-RELOC] Relocated 0x%02X rel32 for '%s::%s' -> target 0x%llX (newDelta=%d, IsInt32=true).\n",
                opcode, groupName, siteName, (unsigned long long)branchTarget, static_cast<int32_t>(newDelta));
        } else {
            // Option T: Widen out-of-range rel32 branch into 64-bit indirect stub
            if (opcode == 0xE8) {
                // CALL QWORD PTR [RIP+2] (FF 15 02 00 00 00) + JMP +8 (EB 08) + 8-byte target
                pTrampoline[0] = 0xFF;
                pTrampoline[1] = 0x15;
                *reinterpret_cast<int32_t*>(pTrampoline + 2) = 2;
                pTrampoline[6] = 0xEB;
                pTrampoline[7] = 0x08;
                *reinterpret_cast<uint64_t*>(pTrampoline + 8) = static_cast<uint64_t>(branchTarget);
                trampWriteOff = 16;
            } else {
                // JMP QWORD PTR [RIP+0] (FF 25 00 00 00 00) + 8-byte target
                pTrampoline[0] = 0xFF;
                pTrampoline[1] = 0x25;
                *reinterpret_cast<int32_t*>(pTrampoline + 2) = 0;
                *reinterpret_cast<uint64_t*>(pTrampoline + 6) = static_cast<uint64_t>(branchTarget);
                trampWriteOff = 14;
            }
            LogHookEvent("[KONGOR PATCH CTRL] [TRAMPOLINE-WIDEN] Widened 0x%02X rel32 for '%s::%s' -> 64-bit indirect stub targeting 0x%llX (delta=%lld, forceWiden=%d).\n",
                opcode, groupName, siteName, (unsigned long long)branchTarget, (long long)newDelta, forceWiden ? 1 : 0);
        }
        if (stolenBytes > 5) {
            memcpy(pTrampoline + trampWriteOff, site.originalBytes + 5, stolenBytes - 5);
            trampWriteOff += (stolenBytes - 5);
        }
    } else {
        memcpy(pTrampoline, site.originalBytes, stolenBytes);
        trampWriteOff = stolenBytes;
    }

    pTrampoline[trampWriteOff + 0] = 0xFF;
    pTrampoline[trampWriteOff + 1] = 0x25;
    *reinterpret_cast<int32_t*>(pTrampoline + trampWriteOff + 2) = 0;
    *reinterpret_cast<void**>(pTrampoline + trampWriteOff + 6) = reinterpret_cast<uint8_t*>(pTarget) + stolenBytes;
        // [L2-P1-02] Seal trampoline page to PAGE_EXECUTE_READ (W^X invariant) and flush I-cache.
        DWORD oldTrampProt = 0;
        if (!VirtualProtect(pTrampoline, trampSize, PAGE_EXECUTE_READ, &oldTrampProt)) {
            LogHookEvent("[KONGOR PATCH CTRL] [ERROR] W^X VirtualProtect(PAGE_EXECUTE_READ) failed on trampoline for '%s::%s' (GLE=%lu).\n",
                groupName, siteName, GetLastError());
            VirtualFree(pTrampoline, 0, MEM_RELEASE);
            if (ppOrigTrampoline) *ppOrigTrampoline = nullptr;
            return false;
        }
        FlushInstructionCache(GetCurrentProcess(), pTrampoline, trampWriteOff + 14);
    } else {
        LogHookEvent("[KONGOR PATCH CTRL] [FULL-REPLACEMENT-OPTOUT] Skipping trampoline generation for '%s::%s' @ 0x%llX (ppOrigTrampoline == nullptr).\n",
            groupName, siteName, (unsigned long long)targetAddr);
    }

    // Build 14-byte absolute indirect jump + NOP padding
    site.patchedBytes[0] = 0xFF;
    site.patchedBytes[1] = 0x25;
    *reinterpret_cast<int32_t*>(site.patchedBytes + 2) = 0;
    *reinterpret_cast<void**>(site.patchedBytes + 6) = pHook;
    for (size_t i = 14; i < stolenBytes; ++i) {
        site.patchedBytes[i] = 0x90;
    }

    if (ppOrigTrampoline) {
        *ppOrigTrampoline = pTrampoline;
    }

    if (IsGroupBootDisabledLocked(groupName)) {
        site.isInstalled = true;
        site.isEnabled   = false;
        site.lastStatus  = g_safeModeActive.load(std::memory_order_acquire) ? "DISARMED_SAFE_MODE" : "DISARMED_BOOT_SEED";
        g_patchSites.push_back(site);
        LogHookEvent("[KONGOR PATCH CTRL] Registered inline hook '%s::%s' @ 0x%llX in DISARMED state (%s).\n",
            groupName, siteName, (unsigned long long)targetAddr, site.lastStatus.c_str());
        return true;
    }

    // Write detour
    DWORD oldProt = 0;
    if (!VirtualProtect(pTarget, stolenBytes, PAGE_EXECUTE_READWRITE, &oldProt)) {
        if (pTrampoline) VirtualFree(pTrampoline, 0, MEM_RELEASE);
        if (ppOrigTrampoline) *ppOrigTrampoline = nullptr;
        return false;
    }

    memcpy(pTarget, site.patchedBytes, stolenBytes);
    VirtualProtect(pTarget, stolenBytes, oldProt, &oldProt);
    FlushInstructionCache(GetCurrentProcess(), pTarget, stolenBytes);

    site.isInstalled = true;
    site.isEnabled   = true;
    site.lastStatus  = "ACTIVE_HOOKED";
    g_patchSites.push_back(site);

    LogHookEvent("[KONGOR PATCH CTRL] Installed reversible inline hook '%s::%s' @ 0x%llX (Stolen=%zu, Region=0x%llX, Orig=[%s]).\n",
        groupName, siteName, (unsigned long long)targetAddr, stolenBytes,
        (unsigned long long)site.regionBase, BytesToHex(site.originalBytes, stolenBytes).c_str());
    return true;
}

bool InstallManagedBytePatch(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    const uint8_t* patchBytes,
    size_t patchLen,
    const uint8_t* expectedPrologue,
    size_t expectedPrologueLen)
{
    Initialize();
    if (!groupName || !siteName || !pTarget || !patchBytes || patchLen == 0 || patchLen > MAX_PATCH_BYTES) {
        return false;
    }

    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    uintptr_t targetAddr = reinterpret_cast<uintptr_t>(pTarget);

    ManagedPatchSite* exactExisting = nullptr;
    if (CheckPatchIntervalCollisionLocked(groupName, siteName, targetAddr, patchLen, &exactExisting)) {
        return false;
    }
    if (exactExisting != nullptr) {
        return exactExisting->isInstalled;
    }

    // 1. Verify expected prologue signature (satisfies: OBL-HOOK-PROLOGUE-SIG)
    if (expectedPrologue && expectedPrologueLen > 0) {
        if (memcmp(pTarget, expectedPrologue, expectedPrologueLen) != 0) {
            g_signatureMismatchCount.fetch_add(1, std::memory_order_relaxed);
            std::string actualHex = BytesToHex(reinterpret_cast<const uint8_t*>(pTarget), expectedPrologueLen);
            std::string expectHex = BytesToHex(expectedPrologue, expectedPrologueLen);
            LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-PROLOGUE-SIG] MISMATCH for byte-patch '%s::%s' @ 0x%llX! Expected [%s], Actual [%s].\n",
                groupName, siteName, (unsigned long long)targetAddr, expectHex.c_str(), actualHex.c_str());
            return false;
        }
    }

    // 2. Capture 64 KiB RegionSnapshot before patching (satisfies: OBL-HOOK-REGION-SNAPSHOT)
    uint64_t regionChecksum = 0;
    if (!EnsureRegionSnapshotForRange(pTarget, patchLen, &regionChecksum)) {
        return false;
    }

    ManagedPatchSite site;
    site.group          = groupName;
    site.name           = siteName;
    site.targetAddr     = targetAddr;
    site.trampolineAddr = 0;
    site.trampolineSize = 0;
    site.patchLen       = patchLen;
    site.regionBase     = targetAddr & ~(REGION_SNAPSHOT_SIZE - 1);
    site.regionChecksum = regionChecksum;

    if (!ReadOriginalBytesFromSnapshot(pTarget, site.originalBytes, patchLen)) {
        memcpy(site.originalBytes, pTarget, patchLen);
    }
    memcpy(site.patchedBytes, patchBytes, patchLen);
    if (expectedPrologue && expectedPrologueLen > 0) {
        site.expectedLen = (std::min)(expectedPrologueLen, MAX_PATCH_BYTES);
        memcpy(site.expectedBytes, expectedPrologue, site.expectedLen);
    } else {
        site.expectedLen = patchLen;
        memcpy(site.expectedBytes, site.originalBytes, patchLen);
    }

    if (IsGroupBootDisabledLocked(groupName)) {
        site.isInstalled = true;
        site.isEnabled   = false;
        site.lastStatus  = g_safeModeActive.load(std::memory_order_acquire) ? "DISARMED_SAFE_MODE" : "DISARMED_BOOT_SEED";
        g_patchSites.push_back(site);
        LogHookEvent("[KONGOR PATCH CTRL] Registered byte-patch '%s::%s' @ 0x%llX in DISARMED state (%s).\n",
            groupName, siteName, (unsigned long long)targetAddr, site.lastStatus.c_str());
        return true;
    }

    DWORD oldProt = 0;
    if (!VirtualProtect(pTarget, patchLen, PAGE_EXECUTE_READWRITE, &oldProt)) {
        return false;
    }
    memcpy(pTarget, site.patchedBytes, patchLen);
    VirtualProtect(pTarget, patchLen, oldProt, &oldProt);
    FlushInstructionCache(GetCurrentProcess(), pTarget, patchLen);

    site.isInstalled = true;
    site.isEnabled   = true;
    site.lastStatus  = "ACTIVE_PATCHED";
    g_patchSites.push_back(site);

    LogHookEvent("[KONGOR PATCH CTRL] Installed reversible byte-patch '%s::%s' @ 0x%llX (Len=%zu, Orig=[%s] -> Patch=[%s]).\n",
        groupName, siteName, (unsigned long long)targetAddr, patchLen,
        BytesToHex(site.originalBytes, patchLen).c_str(),
        BytesToHex(site.patchedBytes, patchLen).c_str());
    return true;
}

bool FindContiguousCodeCaveNear(
    const uint8_t* moduleBytes,
    size_t moduleSize,
    size_t targetOffset,
    size_t requiredCaveBytes,
    size_t* outCaveOffset)
{
    if (outCaveOffset) *outCaveOffset = 0;
    if (!moduleBytes || requiredCaveBytes == 0 || requiredCaveBytes > moduleSize || targetOffset > moduleSize) {
        return false;
    }

    auto isHomogeneousPaddingAt = [&](size_t startIdx) -> bool {
        if (startIdx + requiredCaveBytes > moduleSize) return false;
        uint8_t first = moduleBytes[startIdx];
        if (first != 0xCC && first != 0x90) return false;
        for (size_t i = 1; i < requiredCaveBytes; ++i) {
            if (moduleBytes[startIdx + i] != first) return false;
        }
        return true;
    };

    for (size_t i = targetOffset; i + requiredCaveBytes <= moduleSize; ++i) {
        if (isHomogeneousPaddingAt(i)) {
            int64_t relFwd = static_cast<int64_t>(i) - static_cast<int64_t>(targetOffset + 5);
            int64_t relRet = static_cast<int64_t>(targetOffset + 5) - static_cast<int64_t>(i + requiredCaveBytes);
            if (Kongor::MathExtras::IsInt32(relFwd) && Kongor::MathExtras::IsInt32(relRet)) {
                if (outCaveOffset) *outCaveOffset = i;
                return true;
            }
        }
    }

    if (targetOffset >= requiredCaveBytes) {
        for (size_t i = targetOffset - requiredCaveBytes; ; --i) {
            if (isHomogeneousPaddingAt(i)) {
                int64_t relBwd = static_cast<int64_t>(i) - static_cast<int64_t>(targetOffset + 5);
                int64_t relRet = static_cast<int64_t>(targetOffset + 5) - static_cast<int64_t>(i + requiredCaveBytes);
                if (Kongor::MathExtras::IsInt32(relBwd) && Kongor::MathExtras::IsInt32(relRet)) {
                    if (outCaveOffset) *outCaveOffset = i;
                    return true;
                }
            }
            if (i == 0) break;
        }
    }

    return false;
}

bool InstallCodeCaveDetour32(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    size_t patchSize,
    void* pCodeCave,
    const uint8_t* payloadBytes,
    size_t payloadLen,
    const uint8_t* expectedPrologue,
    size_t expectedPrologueLen)
{
    Initialize();
    if (!groupName || !siteName || !pTarget || !pCodeCave || !payloadBytes ||
        patchSize < 5 || patchSize > MAX_PATCH_BYTES || payloadLen == 0) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] Invalid parameters for InstallCodeCaveDetour32('%s', '%s', patchSize=%zu, payloadLen=%zu).\n",
            groupName ? groupName : "null", siteName ? siteName : "null", patchSize, payloadLen);
        return false;
    }

    if (!IsPageReadable(pTarget) || !IsPageReadable(reinterpret_cast<uint8_t*>(pTarget) + patchSize - 1)) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] InstallCodeCaveDetour32 target 0x%llX (%zuB) is not readable.\n",
            (unsigned long long)reinterpret_cast<uintptr_t>(pTarget), patchSize);
        return false;
    }

    if (expectedPrologue && expectedPrologueLen > 0) {
        if (expectedPrologueLen > patchSize || memcmp(pTarget, expectedPrologue, expectedPrologueLen) != 0) {
            LogHookEvent("[KONGOR PATCH CTRL] [ERROR] InstallCodeCaveDetour32 expectedPrologue mismatch on '%s::%s' @ 0x%llX.\n",
                groupName, siteName, (unsigned long long)reinterpret_cast<uintptr_t>(pTarget));
            return false;
        }
    }

    uintptr_t patchAddr = reinterpret_cast<uintptr_t>(pTarget);
    uintptr_t caveAddr  = reinterpret_cast<uintptr_t>(pCodeCave);
    // Code cave stub layout (#201): payloadBytes (payloadLen) + relocated original prologue (patchSize) + JMP rel32 (5)
    size_t caveTotalLen = payloadLen + patchSize + 5;

    if (!IsPageReadable(pCodeCave) || !IsPageReadable(reinterpret_cast<uint8_t*>(pCodeCave) + caveTotalLen - 1)) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] InstallCodeCaveDetour32 code cave 0x%llX (%zuB) is not readable.\n",
            (unsigned long long)caveAddr, caveTotalLen);
        return false;
    }

    // Disallow overlap between patch site and code cave
    if (patchAddr < caveAddr + caveTotalLen && caveAddr < patchAddr + patchSize) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] InstallCodeCaveDetour32 overlap between patch [0x%llX..+0x%zx) and cave [0x%llX..+0x%zx).\n",
            (unsigned long long)patchAddr, patchSize, (unsigned long long)caveAddr, caveTotalLen);
        return false;
    }

    int64_t entryRel64 = static_cast<int64_t>(caveAddr) - static_cast<int64_t>(patchAddr + 5);
    uintptr_t returnTargetAddr = patchAddr + patchSize;
    uintptr_t caveExitEndAddr  = caveAddr + caveTotalLen;
    int64_t exitRel64 = static_cast<int64_t>(returnTargetAddr) - static_cast<int64_t>(caveExitEndAddr);

    if (!Kongor::MathExtras::IsInt32(entryRel64) || !Kongor::MathExtras::IsInt32(exitRel64)) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] InstallCodeCaveDetour32 rel32 out of +/-2GB range (entryRel=%lld, exitRel=%lld).\n",
            (long long)entryRel64, (long long)exitRel64);
        return false;
    }

    std::vector<uint8_t> originalCaveBytes(caveTotalLen);
    memcpy(originalCaveBytes.data(), pCodeCave, caveTotalLen);

    std::vector<uint8_t> caveStub(caveTotalLen);
    memcpy(caveStub.data(), payloadBytes, payloadLen);
    memcpy(caveStub.data() + payloadLen, pTarget, patchSize);
    caveStub[payloadLen + patchSize + 0] = 0xE9;
    *reinterpret_cast<int32_t*>(caveStub.data() + payloadLen + patchSize + 1) = static_cast<int32_t>(exitRel64);

    DWORD oldCaveProt = 0;
    if (!VirtualProtect(pCodeCave, caveTotalLen, PAGE_EXECUTE_READWRITE, &oldCaveProt)) {
        LogHookEvent("[KONGOR PATCH CTRL] [ERROR] InstallCodeCaveDetour32 VirtualProtect failed on code cave @ 0x%llX.\n",
            (unsigned long long)caveAddr);
        return false;
    }
    memcpy(pCodeCave, caveStub.data(), caveTotalLen);
    VirtualProtect(pCodeCave, caveTotalLen, oldCaveProt, &oldCaveProt);
    FlushInstructionCache(GetCurrentProcess(), pCodeCave, caveTotalLen);

    uint8_t entryStub[MAX_PATCH_BYTES];
    memset(entryStub, 0x90, patchSize);
    entryStub[0] = 0xE9;
    *reinterpret_cast<int32_t*>(entryStub + 1) = static_cast<int32_t>(entryRel64);

    bool installed = InstallManagedBytePatch(
        groupName,
        siteName,
        pTarget,
        entryStub,
        patchSize,
        expectedPrologue,
        expectedPrologueLen);

    if (!installed) {
        DWORD restoreProt = 0;
        if (VirtualProtect(pCodeCave, caveTotalLen, PAGE_EXECUTE_READWRITE, &restoreProt)) {
            memcpy(pCodeCave, originalCaveBytes.data(), caveTotalLen);
            VirtualProtect(pCodeCave, caveTotalLen, restoreProt, &restoreProt);
            FlushInstructionCache(GetCurrentProcess(), pCodeCave, caveTotalLen);
        }
        return false;
    }

    LogHookEvent("[KONGOR PATCH CTRL] [MASK_JUMP32] Installed bidirectional code-cave detour '%s::%s' @ 0x%llX -> Cave 0x%llX (Payload=%zuB, Prologue=%zuB, Return=0x%llX).\n",
        groupName, siteName, (unsigned long long)patchAddr, (unsigned long long)caveAddr,
        payloadLen, patchSize, (unsigned long long)returnTargetAddr);
    return true;
}

size_t GetStatusJson(char* outJson, size_t outJsonCapacity, const char* filterGroup) {
    Initialize();
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);

    std::string json;
    json.reserve(2048);

    char hdr[512];
    snprintf(hdr, sizeof(hdr),
        "{\"success\":true,\"safeMode\":%s,\"bootSentinelTrips\":%u,\"regionSnapshots\":%zu,\"registeredSites\":%zu,\"safePointSwaps\":%u,\"signatureMismatches\":%u,\"ripHazardAborts\":%u,\"groups\":{",
        g_safeModeActive.load(std::memory_order_acquire) ? "true" : "false",
        g_bootSentinelTrips.load(std::memory_order_relaxed),
        g_regionSnapshots.size(),
        g_patchSites.size(),
        g_totalSafePointSwaps.load(std::memory_order_relaxed),
        g_signatureMismatchCount.load(std::memory_order_relaxed),
        g_ripHazardAbortCount.load(std::memory_order_relaxed));
    json += hdr;

    std::vector<std::string> allGroups = { "vfs", "prewarm", "seh", "sleep", "atob" };
    auto addUniqueGroup = [&allGroups](const std::string& g) {
        if (g.empty()) return;
        for (const auto& existing : allGroups) {
            if (_stricmp(existing.c_str(), g.c_str()) == 0) return;
        }
        allGroups.push_back(g);
    };
    for (const auto& s : g_patchSites) addUniqueGroup(s.group);
    for (const auto& gc : g_groupCallbacks) addUniqueGroup(gc.group);

    bool firstGroup = true;
    for (const auto& grpStr : allGroups) {
        const char* grp = grpStr.c_str();
        if (filterGroup && filterGroup[0] != '\0' &&
            _stricmp(filterGroup, "all") != 0 &&
            _stricmp(filterGroup, grp) != 0) {
            continue;
        }

        size_t totalInGrp = 0;
        size_t enabledInGrp = 0;
        for (const auto& s : g_patchSites) {
            if (_stricmp(s.group.c_str(), grp) == 0) {
                totalInGrp++;
                if (s.isEnabled) enabledInGrp++;
            }
        }
        for (const auto& gc : g_groupCallbacks) {
            if (_stricmp(gc.group.c_str(), grp) == 0 && totalInGrp == 0) {
                totalInGrp = 1;
                if (gc.isEnabled) enabledInGrp = 1;
            }
        }

        if (!firstGroup) json += ",";
        firstGroup = false;

        char grpBuf[160];
        snprintf(grpBuf, sizeof(grpBuf),
            "\"%s\":{\"enabled\":%s,\"activeSites\":%zu,\"totalSites\":%zu}",
            grp,
            (enabledInGrp > 0 && enabledInGrp == totalInGrp) ? "true" : "false",
            enabledInGrp,
            totalInGrp);
        json += grpBuf;
    }

    json += "},\"sites\":[";
    bool firstSite = true;
    for (const auto& s : g_patchSites) {
        if (filterGroup && filterGroup[0] != '\0' &&
            _stricmp(filterGroup, "all") != 0 &&
            _stricmp(filterGroup, s.group.c_str()) != 0 &&
            _stricmp(filterGroup, s.name.c_str()) != 0) {
            continue;
        }
        if (!firstSite) json += ",";
        firstSite = false;

        // Verify live memory bytes match expected state
        bool bytesMatchState = false;
        if (IsPageReadable(reinterpret_cast<void*>(s.targetAddr))) {
            const uint8_t* expectedLive = s.isEnabled ? s.patchedBytes : s.originalBytes;
            bytesMatchState = (memcmp(reinterpret_cast<const void*>(s.targetAddr), expectedLive, s.patchLen) == 0);
        }

        char siteBuf[512];
        snprintf(siteBuf, sizeof(siteBuf),
            "{\"group\":\"%s\",\"name\":\"%s\",\"target\":\"0x%llX\",\"regionBase\":\"0x%llX\",\"regionChecksum\":\"0x%016llX\",\"patchLen\":%zu,\"enabled\":%s,\"bytesVerified\":%s,\"toggleCount\":%u,\"status\":\"%s\"}",
            s.group.c_str(),
            s.name.c_str(),
            (unsigned long long)s.targetAddr,
            (unsigned long long)s.regionBase,
            (unsigned long long)s.regionChecksum,
            s.patchLen,
            s.isEnabled ? "true" : "false",
            bytesMatchState ? "true" : "false",
            s.toggleCount,
            s.lastStatus.c_str());
        json += siteBuf;
    }
    json += "]}";

    if (outJson && outJsonCapacity > 0) {
        size_t copyLen = (std::min)(json.size(), outJsonCapacity - 1);
        memcpy(outJson, json.data(), copyLen);
        outJson[copyLen] = '\0';
    }
    return json.size();
}

void EnqueueMainThreadCallback(MainThreadCallbackFn fn, void* userData) {
    if (!fn) return;
    std::lock_guard<std::mutex> lock(g_mainThreadCallbackMutex);
    QueuedMainThreadTask item{};
    item.rawFn = fn;
    item.userData = userData;
    g_pendingMainThreadCallbacks.push_back(std::move(item));
}

void EnqueueMainThreadTask(std::function<void()> task) {
    if (!task) return;
    std::lock_guard<std::mutex> lock(g_mainThreadCallbackMutex);
    QueuedMainThreadTask item{};
    item.taskFn = std::move(task);
    g_pendingMainThreadCallbacks.push_back(std::move(item));
}

size_t DrainMainThreadCallbacks() {
    std::vector<QueuedMainThreadTask> drainingCallbacks;
    {
        std::lock_guard<std::mutex> lock(g_mainThreadCallbackMutex);
        if (g_pendingMainThreadCallbacks.empty()) {
            return 0;
        }
        g_pendingMainThreadCallbacks.swap(drainingCallbacks);
    }

    // Execute outside lock so callbacks can re-entrantly call EnqueueMainThreadCallback without deadlock
    size_t executed = 0;
    for (QueuedMainThreadTask& item : drainingCallbacks) {
        if (item.rawFn) {
            item.rawFn(item.userData);
            ++executed;
        }
        if (item.taskFn) {
            item.taskFn();
            ++executed;
        }
    }
    return executed;
}

bool TryLookupTrampolineSite(
    uintptr_t address,
    char* outGroup,
    size_t groupCap,
    char* outName,
    size_t nameCap,
    uintptr_t* outTrampolineBase,
    uintptr_t* outTargetAddr)
{
    if (address == 0) return false;
    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    for (const auto& s : g_patchSites) {
        if (s.trampolineAddr != 0 && s.trampolineSize > 0 &&
            address >= s.trampolineAddr && address < (s.trampolineAddr + s.trampolineSize)) {
            if (outGroup && groupCap > 0) {
                snprintf(outGroup, groupCap, "%s", s.group.c_str());
            }
            if (outName && nameCap > 0) {
                snprintf(outName, nameCap, "%s", s.name.c_str());
            }
            if (outTrampolineBase) {
                *outTrampolineBase = s.trampolineAddr;
            }
            if (outTargetAddr) {
                *outTargetAddr = s.targetAddr;
            }
            return true;
        }
    }
    return false;
}

void OnMainThreadSafePoint(HWND hGameWnd) {
    g_mainThreadId.store(GetCurrentThreadId(), std::memory_order_release);
    if (hGameWnd && IsWindow(hGameWnd)) {
        g_hMainGameWnd.store(hGameWnd, std::memory_order_release);
    }

    if (!g_bootSentinelCleared.exchange(true)) {
        std::string sentinelPath = GetBootSentinelPath();
        if (DeleteFileA(sentinelPath.c_str())) {
            LogHookEvent("[KONGOR PATCH CTRL] [OBL-HOOK-BOOT-SENTINEL] First healthy main-thread safe point reached; cleared boot crash sentinel at '%s'.\n",
                sentinelPath.c_str());
        }
        DeleteFileA("logs/crash/last-crashed.sentinel");
        DeleteFileA("userdata/logs/last-crashed.sentinel");
    }

    // (#176) Drain double-buffered main-thread callbacks outside the queue lock
    DrainMainThreadCallbacks();

    std::unique_lock<std::mutex> reqLock(g_pendingRequest.mutex);
    if (!g_pendingRequest.pending) return;

    std::string grp = g_pendingRequest.group;
    std::string act = g_pendingRequest.action;
    reqLock.unlock();

    char buf[4096] = {0};
    bool ok = ExecuteHookToggle(grp.c_str(), act.c_str(), buf, sizeof(buf), true);

    reqLock.lock();
    g_pendingRequest.result = ok;
    g_pendingRequest.jsonOutput = buf;
    g_pendingRequest.pending = false;
    if (g_pendingRequest.hDoneEvent) {
        SetEvent(g_pendingRequest.hDoneEvent);
    }
}

namespace {

struct Phase14VirtualPad {
    uint64_t pad0 = 0x1122334455667788ULL;
    uint64_t pad1 = 0x99AABBCCDDEEFF00ULL;
    virtual ~Phase14VirtualPad() = default;
};

struct Phase14VirtualStdException : public Phase14VirtualPad, public virtual std::exception {
    const char* what() const noexcept override {
        return "kongor-virtual-base-what";
    }
};

__declspec(noinline) void Phase14ThrowStdRuntimeError() {
    throw std::runtime_error("kongor-phase14-rtti-what");
}

__declspec(noinline) void Phase14ThrowVirtualException() {
    throw Phase14VirtualStdException();
}

__declspec(noinline) void Phase14SehInspectStdException(char* outBuf, size_t outCap) {
    __try {
        Phase14ThrowStdRuntimeError();
    } __except (Kongor_TestSehExtractMsvcCppException(GetExceptionInformation()->ExceptionRecord, outBuf, outCap), EXCEPTION_CONTINUE_SEARCH) {
    }
}

__declspec(noinline) bool Phase14CaptureRealStdException(char* outBuf, size_t outCap) noexcept {
    try {
        KongorScopedProtectedSeh sehGuard;
        Phase14SehInspectStdException(outBuf, outCap);
    } catch (const std::exception&) {
        return true;
    }
    return false;
}

__declspec(noinline) void Phase14SehInspectVirtualException(char* outBuf, size_t outCap) {
    __try {
        Phase14ThrowVirtualException();
    } __except (Kongor_TestSehExtractMsvcCppException(GetExceptionInformation()->ExceptionRecord, outBuf, outCap), EXCEPTION_CONTINUE_SEARCH) {
    }
}

__declspec(noinline) bool Phase14CaptureVirtualException(char* outBuf, size_t outCap) noexcept {
    try {
        KongorScopedProtectedSeh sehGuard;
        Phase14SehInspectVirtualException(outBuf, outCap);
    } catch (const std::exception&) {
        return true;
    }
    return false;
}

#pragma pack(push, 4)
struct FakeCorruptedThrowInfo {
    uint32_t attributes;
    int32_t  pmfnUnwind;
    int32_t  pForwardCompat;
    int32_t  pCatchableTypeArray;
};
#pragma pack(pop)

__declspec(noinline) uint64_t Phase14UnwindTargetFunction(uint64_t a, uint64_t b) {
    DWORD pid = GetCurrentProcessId();


    volatile uint64_t acc = a ^ 0x5A5A5A5A5A5A5A5AULL ^ static_cast<uint64_t>(pid);
    acc += (b * 3ULL);
    acc ^= (acc >> 7);
    acc += 0x12345678ULL;
    return acc;
}

__declspec(noinline) uint64_t Phase14UnwindDetourFunction(uint64_t a, uint64_t b) {
    return a + b + 999ULL;
}

class Phase14VmtPolymorphicSample {
public:
    virtual int Compute(int x) { return x + 10; }
    virtual int Secondary(int x) { return x * 2; }
    virtual ~Phase14VmtPolymorphicSample() = default;
};

int __fastcall Phase14DetourCompute(Phase14VmtPolymorphicSample* /*self*/, int x) {
    return x * 100;
}

struct Phase14SidecarField {
    int* pDtorCount = nullptr;
    int  payload    = 0;
    Phase14SidecarField(int* dtorCounter, int val) : pDtorCount(dtorCounter), payload(val) {}
    ~Phase14SidecarField() {
        if (pDtorCount) {
            ++(*pDtorCount);
        }
    }
};

bool RunPhase14SelfTest(char* outJson, size_t outJsonCapacity) {
    // 1. (#166) Real std::runtime_error + Virtual-base std::exception + Corrupted RVA SEH trap
    char realExcBuf[1024] = {0};
    bool caughtReal = Phase14CaptureRealStdException(realExcBuf, sizeof(realExcBuf));
    bool rttiRealOk = caughtReal &&
                      (strstr(realExcBuf, "runtime_error") != nullptr) &&
                      (strstr(realExcBuf, "kongor-phase14-rtti-what") != nullptr);

    char virtExcBuf[1024] = {0};
    bool caughtVirt = Phase14CaptureVirtualException(virtExcBuf, sizeof(virtExcBuf));
    bool rttiVirtOk = caughtVirt &&
                      (strstr(virtExcBuf, "Phase14VirtualStdException") != nullptr) &&
                      (strstr(virtExcBuf, "kongor-virtual-base-what") != nullptr);

    uint64_t dummyExcObj = 0;
    FakeCorruptedThrowInfo badTi{ 0, 0, 0, 0x7EAD0000 }; // unmapped RVA offset
    EXCEPTION_RECORD corruptRec{};
    corruptRec.ExceptionCode = 0xE06D7363U;
    corruptRec.NumberParameters = 4;
    corruptRec.ExceptionInformation[0] = 0x19930520ULL;
    corruptRec.ExceptionInformation[1] = reinterpret_cast<ULONG_PTR>(&dummyExcObj);
    corruptRec.ExceptionInformation[2] = reinterpret_cast<ULONG_PTR>(&badTi);
    corruptRec.ExceptionInformation[3] = reinterpret_cast<ULONG_PTR>(GetModuleHandleA(NULL));
    char corruptExcBuf[512] = {0};
    Kongor_TestSehExtractMsvcCppException(&corruptRec, corruptExcBuf, sizeof(corruptExcBuf));
    bool rttiCorruptSehOk = (strstr(corruptExcBuf, "<unreadable _ThrowInfo / RTTI descriptor during C++ exception decode>") != nullptr);

    // 2. (#168 & #169) .pdata Trampoline-aware KongorFunctionTableAccess64 / KongorGetModuleBase64 + Overlap Rejection
    void* pOrigUnwindTramp = nullptr;
    size_t unwindHookLen = ComputeSafeHookLength64(reinterpret_cast<const uint8_t*>(&Phase14UnwindTargetFunction), MAX_PATCH_BYTES, 14);
    if (unwindHookLen < 14) unwindHookLen = 14;
    InstallManagedInlineHook64(
        "phase14_unwind",
        "Phase14UnwindTargetFunction",
        reinterpret_cast<void*>(&Phase14UnwindTargetFunction),
        reinterpret_cast<void*>(&Phase14UnwindDetourFunction),
        &pOrigUnwindTramp,
        unwindHookLen,
        nullptr,
        0);
    // Verify overlapping patch at +4 bytes into Phase14UnwindTargetFunction is rejected (#169 OBL-HOOK-NO-OVERLAP)
    void* pOverlapTramp = nullptr;
    bool overlapInstalled = InstallManagedInlineHook64(
        "phase14_unwind",
        "Phase14OverlappingSiteAtPlus4",
        reinterpret_cast<uint8_t*>(&Phase14UnwindTargetFunction) + 4,
        reinterpret_cast<void*>(&Phase14UnwindDetourFunction),
        &pOverlapTramp,
        unwindHookLen,
        nullptr,
        0);
    bool overlapRejectedOk = !overlapInstalled;

    uintptr_t unwindModBase = 0;
    uint32_t unwindBeginRva = 0;
    bool trampUnwindOk = false;
    if (pOrigUnwindTramp != nullptr) {
        trampUnwindOk = Kongor_TestTrampolineUnwindLookup(
            reinterpret_cast<uintptr_t>(pOrigUnwindTramp) + 2,
            &unwindModBase,
            &unwindBeginRva) && (unwindModBase != 0) && (unwindBeginRva != 0) && overlapRejectedOk;
        ExecuteHookToggle("phase14_unwind", "off", nullptr, 0, true);
    }

    // 3. (#176) Double-Buffered Main-Thread Callback Queue with re-entrant Enqueue
    DrainMainThreadCallbacks();
    int cbStage = 0;
    EnqueueMainThreadTask([&cbStage]() {
        cbStage = 1;
        EnqueueMainThreadTask([&cbStage]() {
            cbStage = 2;
        });
    });
    size_t drainedFirst = DrainMainThreadCallbacks();
    bool firstDrainOk = (drainedFirst == 1 && cbStage == 1);
    size_t drainedSecond = DrainMainThreadCallbacks();
    bool mainThreadQueueOk = firstDrainOk && (drainedSecond == 1 && cbStage == 2);

    // 4. (#178) ReentrantPriorityEventDispatcher (lower priority integer = earlier + deferred add/remove + stopPropagation)
    ReentrantPriorityEventDispatcher<int> disp;
    std::string orderLog;
    ReentrantPriorityEventDispatcher<int>::ListenerId idLate = 0;
    idLate = disp.Subscribe(10, [&](const int&) {
        orderLog.push_back('C');
        return false;
    });
    disp.Subscribe(-10, [&](const int&) {
        orderLog.push_back('A');
        disp.Unsubscribe(idLate);
        disp.Subscribe(5, [&](const int&) {
            orderLog.push_back('B');
            return false;
        });
        return false;
    });
    disp.Subscribe(0, [&](const int&) {
        orderLog.push_back('M');
        return false;
    });
    disp.Dispatch(1);  // Expect -10 ('A'), then 0 ('M'); 10 ('C') was unsubscribed mid-dispatch, 5 ('B') deferred
    disp.Dispatch(2);  // Expect -10 ('A'), 0 ('M'), 5 ('B')
    disp.Subscribe(-100, [&](const int&) {
        orderLog.push_back('S');
        return true; // stopPropagation
    });
    size_t stopCount = disp.Dispatch(3);
    bool dispatcherOk = (orderLog == "AMAMBS" && stopCount == 1);

    // 5. (#179) ConcreteVmtHookManager (RTTI vptr[-1] preservation) & EntityFieldContainer
    Phase14VmtPolymorphicSample instHooked;
    Phase14VmtPolymorphicSample instUntouched;
    uintptr_t origRttiLocator = reinterpret_cast<uintptr_t**>(&instHooked)[0][-1];
    uintptr_t origSlot0 = 0;
    bool hookedOk = kongor::native_guard::ConcreteVmtHookManager::Instance().HookInstanceVirtualSlot(
        &instHooked, 0, reinterpret_cast<uintptr_t>(&Phase14DetourCompute), &origSlot0, 4);
    uintptr_t shadowRttiLocator = reinterpret_cast<uintptr_t**>(&instHooked)[0][-1];
    Phase14VmtPolymorphicSample* pHookedPoly = &instHooked;
    Phase14VmtPolymorphicSample* pUntouchedPoly = &instUntouched;
    int hookedVal = pHookedPoly->Compute(5);       // Expect 500
    int untouchedVal = pUntouchedPoly->Compute(5); // Expect 15
    bool restoredOk = kongor::native_guard::ConcreteVmtHookManager::Instance().RestoreInstanceVptr(&instHooked);
    int restoredVal = pHookedPoly->Compute(5);     // Expect 15
    bool vmtOk = hookedOk && (origRttiLocator == shadowRttiLocator) &&
                 (hookedVal == 500) && (untouchedVal == 15) &&
                 restoredOk && (restoredVal == 15);

    int sidecarDtors = 0;
    Phase14SidecarField* pField = kongor::native_guard::EntityFieldContainer::Instance()
        .GetOrCreateField<Phase14SidecarField>(&instHooked, "phase14.sidecar", &sidecarDtors, 777);
    bool fieldCreated = (pField != nullptr && pField->payload == 777);
    size_t destroyedFields = kongor::native_guard::EntityFieldContainer::Instance().OnEntityDestroyed(&instHooked);
    Phase14SidecarField* pAfterDestroy = kongor::native_guard::EntityFieldContainer::Instance()
        .TryGetField<Phase14SidecarField>(&instHooked, "phase14.sidecar");
    bool entityFieldsOk = fieldCreated && (destroyedFields == 1) && (sidecarDtors == 1) && (pAfterDestroy == nullptr);

    bool allOk = rttiRealOk && rttiVirtOk && rttiCorruptSehOk && trampUnwindOk && mainThreadQueueOk && dispatcherOk && vmtOk && entityFieldsOk;
    if (outJson && outJsonCapacity > 0) {
        snprintf(
            outJson,
            outJsonCapacity,
            "{\"success\":%s,\"rttiRealOk\":%s,\"rttiVirtOk\":%s,\"rttiCorruptSehOk\":%s,"
            "\"trampUnwindOk\":%s,\"unwindBeginRva\":%u,\"mainThreadQueueOk\":%s,"
            "\"dispatcherOk\":%s,\"dispatcherOrder\":\"%s\",\"vmtOk\":%s,\"entityFieldsOk\":%s}",
            allOk ? "true" : "false",
            rttiRealOk ? "true" : "false",
            rttiVirtOk ? "true" : "false",
            rttiCorruptSehOk ? "true" : "false",
            trampUnwindOk ? "true" : "false",
            unwindBeginRva,
            mainThreadQueueOk ? "true" : "false",
            dispatcherOk ? "true" : "false",
            orderLog.c_str(),
            vmtOk ? "true" : "false",
            entityFieldsOk ? "true" : "false");
    }
    return allOk;
}

} // anonymous namespace

bool ExecuteHookToggle(
    const char* group,
    const char* action,
    char* outJson,
    size_t outJsonCapacity,
    bool isOnMainThreadSafePoint)
{
    Initialize();
    std::string grp = (group && group[0]) ? group : "all";
    std::string act = (action && action[0]) ? action : "status";

    if (_stricmp(grp.c_str(), "__jsonrpc__") == 0) {
        return Kongor_ProcessPipeStreamLines(act.c_str(), outJson, outJsonCapacity);
    }

    if (_stricmp(grp.c_str(), "__phase14_selftest__") == 0) {
        return RunPhase14SelfTest(outJson, outJsonCapacity);
    }

    if (_stricmp(grp.c_str(), "__sentinel__") == 0) {
        if (_stricmp(act.c_str(), "reinit") == 0) {
            std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
            EvaluateBootSentinelAndGroupSeedLocked();
            for (auto& site : g_patchSites) {
                bool shouldDisable = IsGroupBootDisabledLocked(site.group.c_str());
                if (shouldDisable && site.isEnabled) {
                    VerifyThreadsQuiescentAndSwap(site, false);
                    site.lastStatus = g_safeModeActive.load(std::memory_order_acquire) ? "DISARMED_SAFE_MODE" : "DISARMED_BOOT_SEED";
                } else if (!shouldDisable && !site.isEnabled) {
                    VerifyThreadsQuiescentAndSwap(site, true);
                }
            }
            GetStatusJson(outJson, outJsonCapacity, "all");
            return true;
        }
        if (_stricmp(act.c_str(), "safe_point") == 0) {
            OnMainThreadSafePoint(NULL);
            GetStatusJson(outJson, outJsonCapacity, "all");
            return true;
        }
    }

    if (_stricmp(act.c_str(), "status") == 0) {
        GetStatusJson(outJson, outJsonCapacity, grp.c_str());
        return true;
    }

    // Marshal to main window thread safe point if called from a background IPC thread while window is live
    HWND hWnd = g_hMainGameWnd.load(std::memory_order_acquire);
    if (!isOnMainThreadSafePoint && hWnd && IsWindow(hWnd)) {
        DWORD wndTid = GetWindowThreadProcessId(hWnd, NULL);
        if (wndTid != 0 && GetCurrentThreadId() != wndTid) {
            {
                std::lock_guard<std::mutex> reqLock(g_pendingRequest.mutex);
                g_pendingRequest.group = grp;
                g_pendingRequest.action = act;
                g_pendingRequest.pending = true;
                g_pendingRequest.result = false;
                g_pendingRequest.jsonOutput.clear();
                ResetEvent(g_pendingRequest.hDoneEvent);
            }
            PostMessageA(hWnd, WM_KONGOR_SAFEPOINT_CMD, 0, 0);
            DWORD waitRes = WaitForSingleObject(g_pendingRequest.hDoneEvent, 350);
            if (waitRes == WAIT_OBJECT_0) {
                std::lock_guard<std::mutex> reqLock(g_pendingRequest.mutex);
                if (outJson && outJsonCapacity > 0) {
                    size_t copyLen = (std::min)(g_pendingRequest.jsonOutput.size(), outJsonCapacity - 1);
                    memcpy(outJson, g_pendingRequest.jsonOutput.data(), copyLen);
                    outJson[copyLen] = '\0';
                }
                return g_pendingRequest.result;
            }
            LogHookEvent("[KONGOR PATCH CTRL] Safe-point wait timed out after 350ms; executing with thread-suspension quiescence.\n");
        }
    }

    std::lock_guard<std::recursive_mutex> lock(g_controllerMutex);
    bool matchAll = (_stricmp(grp.c_str(), "all") == 0);
    bool anyMatched = false;
    bool allSucceeded = true;

    // [L2-P1-02] Precompute desired state per group callback once so "toggle" never double-flips
    // between Phase 1 (Pre-Unhook OFF) and Phase 2 (Post-Hook ON).
    std::vector<uint8_t> gcDesired(g_groupCallbacks.size(), 0);
    for (size_t i = 0; i < g_groupCallbacks.size(); ++i) {
        const auto& gc = g_groupCallbacks[i];
        bool targetEnable = gc.isEnabled;
        if (_stricmp(act.c_str(), "on") == 0 || _stricmp(act.c_str(), "enable") == 0) {
            targetEnable = true;
        } else if (_stricmp(act.c_str(), "off") == 0 || _stricmp(act.c_str(), "disable") == 0) {
            targetEnable = false;
        } else if (_stricmp(act.c_str(), "toggle") == 0) {
            targetEnable = !gc.isEnabled;
        }
        gcDesired[i] = targetEnable ? 1u : 0u;
    }

    // Phase 1 (Pre-Unhook): Invoke group callbacks transitioning to OFF *before* restoring
    // native code bytes so subsystems like "vfs" restore synthesized CArchive internal vectors first.
    for (size_t i = 0; i < g_groupCallbacks.size(); ++i) {
        auto& gc = g_groupCallbacks[i];
        if ((matchAll || _stricmp(gc.group.c_str(), grp.c_str()) == 0) && gcDesired[i] == 0u) {
            anyMatched = true;
            if (gc.callback) {
                gc.callback(false);
            }
            gc.isEnabled = false;
            gc.toggleCount++;
            LogHookEvent("[KONGOR PATCH CTRL] Group callback '%s' pre-unhook toggled -> OFF (Count=%u).\n",
                gc.group.c_str(), gc.toggleCount);
        }
    }

    for (auto& site : g_patchSites) {
        if (matchAll ||
            _stricmp(site.group.c_str(), grp.c_str()) == 0 ||
            _stricmp(site.name.c_str(), grp.c_str()) == 0) {
            anyMatched = true;
            bool targetEnable = site.isEnabled;
            if (_stricmp(act.c_str(), "on") == 0 || _stricmp(act.c_str(), "enable") == 0) {
                targetEnable = true;
            } else if (_stricmp(act.c_str(), "off") == 0 || _stricmp(act.c_str(), "disable") == 0) {
                targetEnable = false;
            } else if (_stricmp(act.c_str(), "toggle") == 0) {
                targetEnable = !site.isEnabled;
            }

            if (!VerifyThreadsQuiescentAndSwap(site, targetEnable)) {
                allSucceeded = false;
            }
        }
    }

    // Phase 2 (Post-Hook): Invoke group callbacks transitioning to ON *after* patch sites are active.
    for (size_t i = 0; i < g_groupCallbacks.size(); ++i) {
        auto& gc = g_groupCallbacks[i];
        if ((matchAll || _stricmp(gc.group.c_str(), grp.c_str()) == 0) && gcDesired[i] == 1u) {
            anyMatched = true;
            if (gc.callback) {
                gc.callback(true);
            }
            gc.isEnabled = true;
            gc.toggleCount++;
            LogHookEvent("[KONGOR PATCH CTRL] Group callback '%s' toggled -> ON (Count=%u).\n",
                gc.group.c_str(), gc.toggleCount);
        }
    }

    GetStatusJson(outJson, outJsonCapacity, grp.c_str());
    if (!allSucceeded && outJson && outJsonCapacity > 16) {
        // Flip top-level "success":true to "success":false when a RIP hazard or error aborted the toggle
        char* pSucc = strstr(outJson, "\"success\":true");
        if (pSucc) {
            std::string updated = "{\"success\":false" + std::string(pSucc + 14);
            size_t copyLen = (std::min)(updated.size(), outJsonCapacity - 1);
            memcpy(outJson, updated.data(), copyLen);
            outJson[copyLen] = '\0';
        }
    }
    return anyMatched && allSucceeded;
}

void SetSimulatedThreadRip(uintptr_t simulatedRip) {
    g_simulatedThreadRip.store(simulatedRip, std::memory_order_release);
}

} // namespace PatchController
} // namespace Kongor

namespace Kongor {
namespace PatchController {

uint32_t ComputeGraphicsVtableCacheChecksum(const GraphicsVtableOffsetCache& cache) noexcept {
    const uint8_t* raw = reinterpret_cast<const uint8_t*>(&cache);
    const size_t dataLen = sizeof(GraphicsVtableOffsetCache) - sizeof(uint32_t);
    uint32_t hash = 2166136261U;
    for (size_t i = 0; i < dataLen; ++i) {
        hash ^= raw[i];
        hash *= 16777619U;
    }
    return hash;
}

bool ValidateGraphicsVtableOffsetCache(
    const GraphicsVtableOffsetCache& cache,
    const GraphicsModuleVersionFingerprint& expectedD3d9,
    const GraphicsModuleVersionFingerprint& expectedDxgi,
    const GraphicsModuleVersionFingerprint& expectedD3d11) noexcept
{
    if (cache.magic != GraphicsVtableOffsetCache::EXPECTED_MAGIC ||
        cache.version != GraphicsVtableOffsetCache::EXPECTED_VERSION) {
        return false;
    }
    if (cache.checksumFnv1a32 != ComputeGraphicsVtableCacheChecksum(cache)) {
        return false;
    }
    if (!cache.d3d9Fingerprint.Matches(expectedD3d9) ||
        !cache.dxgiFingerprint.Matches(expectedDxgi) ||
        !cache.d3d11Fingerprint.Matches(expectedD3d11)) {
        return false;
    }
    if (cache.d3d9PresentOffset == 0 && cache.dxgiPresentOffset == 0) {
        return false;
    }
    if ((cache.d3d9PresentOffset & 3U) != 0U || (cache.dxgiPresentOffset & 3U) != 0U) {
        return false;
    }
    return true;
}

} // namespace PatchController
} // namespace Kongor

extern "C" {

__declspec(dllexport) int Kongor_NativeGuard_HookToggle(
    const char* groupName,
    const char* action,
    char* outJson,
    uint32_t outJsonCapacity)
{
    bool ok = Kongor::PatchController::ExecuteHookToggle(groupName, action, outJson, outJsonCapacity, false);
    return ok ? 1 : 0;
}

__declspec(dllexport) uint32_t Kongor_NativeGuard_GetHookStatusJson(
    char* outJson,
    uint32_t outJsonCapacity)
{
    return static_cast<uint32_t>(Kongor::PatchController::GetStatusJson(outJson, outJsonCapacity, "all"));
}

__declspec(dllexport) int Kongor_NativeGuard_RegisterTestPatchSite(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    void* pHook,
    void** ppOrigTrampoline,
    uint32_t stolenBytes,
    const uint8_t* expectedPrologue,
    uint32_t expectedPrologueLen)
{
    bool ok = Kongor::PatchController::InstallManagedInlineHook64(
        groupName,
        siteName,
        pTarget,
        pHook,
        ppOrigTrampoline,
        stolenBytes,
        expectedPrologue,
        expectedPrologueLen
    );
    return ok ? 1 : 0;
}

__declspec(dllexport) void Kongor_NativeGuard_SetSimulatedThreadRip(uint64_t simulatedRip) {
    Kongor::PatchController::SetSimulatedThreadRip(static_cast<uintptr_t>(simulatedRip));
}

__declspec(dllexport) int Kongor_NativeGuard_InstallCodeCaveDetour32(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    uint32_t patchSize,
    void* pCodeCave,
    const uint8_t* payloadBytes,
    uint32_t payloadLen,
    const uint8_t* expectedPrologue,
    uint32_t expectedPrologueLen)
{
    bool ok = Kongor::PatchController::InstallCodeCaveDetour32(
        groupName,
        siteName,
        pTarget,
        static_cast<size_t>(patchSize),
        pCodeCave,
        payloadBytes,
        static_cast<size_t>(payloadLen),
        expectedPrologue,
        static_cast<size_t>(expectedPrologueLen)
    );
    return ok ? 1 : 0;
}

__declspec(dllexport) int Kongor_NativeGuard_ValidateGraphicsVtableCache(
    const void* cacheBytes,
    uint32_t cacheByteCount)
{
    if (!cacheBytes || cacheByteCount != sizeof(Kongor::PatchController::GraphicsVtableOffsetCache)) {
        return 0;
    }
    const auto* pCache = reinterpret_cast<const Kongor::PatchController::GraphicsVtableOffsetCache*>(cacheBytes);
    bool valid = Kongor::PatchController::ValidateGraphicsVtableOffsetCache(
        *pCache,
        pCache->d3d9Fingerprint,
        pCache->dxgiFingerprint,
        pCache->d3d11Fingerprint);
    return valid ? 1 : 0;
}

} // extern "C"
