// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "asset_prewarmer.h"
#include "runtime_patch_controller.h"
#include <windows.h>
#include <winioctl.h>
#include <string>
#include <vector>
#include <deque>
#include <unordered_set>
#include <filesystem>
#include <fstream>
#include <chrono>
#include <atomic>
#include <algorithm>
#include <iostream>

namespace Kongor {

namespace fs = std::filesystem;

enum class PrewarmJobType {
    Hero,
    Item,
    Map,
    Common,
    PostIntro
};

struct PrewarmJob {
    PrewarmJobType Type;
    std::string Name;
    std::string Extra;
};

static std::atomic<bool> g_prewarmerRunning{ false };
static std::atomic<bool> g_prewarmerEnabled{ true };
static std::atomic<bool> g_prewarmerForegroundBusy{ false };
static std::atomic<bool> g_prewarmerMatchActive{ false };
static std::atomic<bool> g_prewarmerGentlePacing{ false };
static std::atomic<bool> g_postIntroTriggered{ false };
static std::atomic<bool> g_postIntroCancelled{ false };
static std::atomic<bool> g_postIntroCompleted{ false };
static std::atomic<bool> g_postIntroBudgetInitialized{ false };
static std::atomic<bool> g_postIntroBudgetExhausted{ false };
static std::atomic<bool> g_postIntroOnBatteryOrSaver{ false };
static std::atomic<bool> g_postIntroSkippedSsd{ false };
static std::atomic<bool> g_isPostIntroActive{ false };
static HANDLE g_hPrewarmThread = NULL;
static HANDLE g_hPrewarmEvent = NULL;
static CRITICAL_SECTION g_prewarmLock;
static std::deque<PrewarmJob> g_jobQueue;
static std::unordered_set<std::wstring> g_postIntroCompletedPaths;

static std::atomic<uint32_t> g_statFiles{ 0 };
static std::atomic<uint64_t> g_statBytes{ 0 };
static std::atomic<uint32_t> g_statHeroes{ 0 };
static std::atomic<uint32_t> g_statItems{ 0 };
static std::atomic<uint32_t> g_statMaps{ 0 };
static std::atomic<float>    g_statDurationMs{ 0.0f };
static std::atomic<bool>     g_isPrewarmingActive{ false };

static std::atomic<uint64_t> g_postIntroBudgetBytes{ 0 };
static std::atomic<uint64_t> g_postIntroFaultedBytes{ 0 };
static std::atomic<uint32_t> g_postIntroFaultedFiles{ 0 };
static std::atomic<float>    g_postIntroElapsedMs{ 0.0f };
static std::atomic<uint32_t> g_warmupWindowPeakFrameUs{ 0 };
static std::atomic<uint64_t> g_warmupWindowHitchCount{ 0 };
static char                  g_cachedDriveType[16] = "UNKNOWN";
static char                  g_driveTypeTestOverride[16] = "";
static std::atomic<int>      g_forceSsdPrewarmTestOverride{ -1 };
static std::wstring          g_gameRootTestOverride;

static bool IsForceSsdPrewarmEnabled() noexcept {
    const int testMode = g_forceSsdPrewarmTestOverride.load(std::memory_order_acquire);
    if (testMode >= 0) {
        return testMode != 0;
    }
    wchar_t envBuf[16] = { 0 };
    const DWORD len = GetEnvironmentVariableW(L"KONGOR_FORCE_SSD_PREWARM", envBuf, 16);
    if (len > 0 && len < 16) {
        return (wcscmp(envBuf, L"1") == 0 || _wcsicmp(envBuf, L"true") == 0);
    }
    return false;
}

static void AddDurationMs(float ms) {
    float current = g_statDurationMs.load(std::memory_order_relaxed);
    while (!g_statDurationMs.compare_exchange_weak(current, current + ms, std::memory_order_relaxed)) {}
}

static void AddPostIntroElapsedMs(float ms) {
    float current = g_postIntroElapsedMs.load(std::memory_order_relaxed);
    while (!g_postIntroElapsedMs.compare_exchange_weak(current, current + ms, std::memory_order_relaxed)) {}
}

void KongorWriteWillowmakerLog(const char* tag, const char* message);

static fs::path g_gameRootDir;

static void LogPrewarmEx(bool isMilestone, const char* format, ...) {
    char buffer[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);

    OutputDebugStringA(buffer);

    static const bool s_enableVerbosePrewarmLogs = []() {
        const char* env = getenv("KONGOR_PREWARM_LOGS");
        return env && (strcmp(env, "1") == 0 || _stricmp(env, "true") == 0);
    }();

    if (isMilestone || s_enableVerbosePrewarmLogs) {
        KongorWriteWillowmakerLog("ASSET_PREWARM", buffer);
    }
}

#define LogPrewarm(...) LogPrewarmEx(false, __VA_ARGS__)
#define LogPrewarmMilestone(...) LogPrewarmEx(true, __VA_ARGS__)

static inline bool ShouldAbortOrYieldPrewarm() noexcept {
    return !g_prewarmerRunning.load(std::memory_order_relaxed) ||
           !g_prewarmerEnabled.load(std::memory_order_relaxed) ||
           g_prewarmerForegroundBusy.load(std::memory_order_relaxed);
}

static inline bool ShouldHardStopPostIntro() noexcept {
    return !g_prewarmerRunning.load(std::memory_order_acquire) ||
           !g_prewarmerEnabled.load(std::memory_order_acquire) ||
           g_postIntroCancelled.load(std::memory_order_acquire) ||
           g_prewarmerMatchActive.load(std::memory_order_acquire);
}

// Waits cleanly if foreground PreloadWorld is active, returning false only if shutdown or disabled
static bool WaitWhileForegroundBusy() noexcept {
    while (g_prewarmerRunning.load(std::memory_order_acquire) &&
           g_prewarmerEnabled.load(std::memory_order_acquire) &&
           g_prewarmerForegroundBusy.load(std::memory_order_acquire)) {
        Sleep(25);
    }
    return g_prewarmerRunning.load(std::memory_order_acquire) &&
           g_prewarmerEnabled.load(std::memory_order_acquire);
}

// Waits cleanly during PostIntro warmup while foreground PreloadWorld is busy; aborts immediately on match/cancel/disable
static bool WaitWhilePostIntroAllowed() noexcept {
    while (!ShouldHardStopPostIntro() &&
           g_prewarmerForegroundBusy.load(std::memory_order_acquire)) {
        Sleep(15);
    }
    return !ShouldHardStopPostIntro();
}

// Lowers thread I/O priority (ThreadIoPriority = 22 -> IoPriorityLow = 1) so Windows CcPerformReadAhead
// and synchronous reads yield to foreground K2 reads while preserving ThreadPagePriority = Normal (5).
bool ApplyLowDiskIoPriorityToCurrentThread() noexcept {
    HMODULE hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (!hNtdll) return false;
    using FnNtSetInformationThread = LONG(NTAPI*)(HANDLE, ULONG, PVOID, ULONG);
    auto pfn = reinterpret_cast<FnNtSetInformationThread>(GetProcAddress(hNtdll, "NtSetInformationThread"));
    if (!pfn) return false;
    ULONG ioPriorityLow = 1U; // IoPriorityLow
    constexpr ULONG kThreadIoPriority = 22U; // THREADINFOCLASS::ThreadIoPriority (0x16)
    return pfn(GetCurrentThread(), kThreadIoPriority, &ioPriorityLow, sizeof(ioPriorityLow)) >= 0;
}

static void ApplyLowDiskIoPriorityToFileHandle(HANDLE hFile) noexcept {
    if (!hFile || hFile == INVALID_HANDLE_VALUE) return;
    FILE_IO_PRIORITY_HINT_INFO hintInfo{};
    hintInfo.PriorityHint = IoPriorityHintLow;
    (void)SetFileInformationByHandle(hFile, FileIoPriorityHintInfo, &hintInfo, sizeof(hintInfo));
}

static bool IsPostIntroPathCompleted(const std::wstring& fullPath) {
    EnterCriticalSection(&g_prewarmLock);
    const bool done = (g_postIntroCompletedPaths.find(fullPath) != g_postIntroCompletedPaths.end());
    LeaveCriticalSection(&g_prewarmLock);
    return done;
}

static void MarkPostIntroPathCompleted(const std::wstring& fullPath) {
    EnterCriticalSection(&g_prewarmLock);
    g_postIntroCompletedPaths.insert(fullPath);
    LeaveCriticalSection(&g_prewarmLock);
}

// Reusable 64-byte cache-line-aligned thread-local 256 KiB prewarm scratch buffer (#131)
// Eliminates per-file 512 KB std::vector<char> heap allocation/deallocation churn across 1,000+ combat files.
static constexpr DWORD kPrewarmScratchBytes = 262144;
static thread_local alignas(64) uint8_t t_prewarmScratch[kPrewarmScratchBytes];

// Faults a file into the operating system page cache using sequential scan with 0 heap allocations (#131)
bool PrewarmFile(const std::wstring& fullPath) {
    if (ShouldAbortOrYieldPrewarm()) {
        return false;
    }

    HANDLE hFile = CreateFileW(
        fullPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_SEQUENTIAL_SCAN,
        NULL
    );

    if (hFile == INVALID_HANDLE_VALUE) {
        return false;
    }

    if (g_prewarmerGentlePacing.load(std::memory_order_relaxed)) {
        ApplyLowDiskIoPriorityToFileHandle(hFile);
    }

    LARGE_INTEGER fileSize;
    if (GetFileSizeEx(hFile, &fileSize) && fileSize.QuadPart > 0) {
        DWORD bytesRead = 0;
        uint64_t totalRead = 0;
        uint32_t chunkCount = 0;
        volatile uint8_t pageTouchAccumulator = 0;

        while (ReadFile(hFile, t_prewarmScratch, kPrewarmScratchBytes, &bytesRead, NULL) && bytesRead > 0) {
            for (DWORD offset = 0; offset < bytesRead; offset += 4096) {
                pageTouchAccumulator ^= t_prewarmScratch[offset];
            }
            totalRead += bytesRead;
            ++chunkCount;
            if (ShouldAbortOrYieldPrewarm()) break;
            if (g_prewarmerGentlePacing.load(std::memory_order_relaxed) && (chunkCount & 31U) == 0U) {
                // Micro-yield 1 ms every 8 MiB on background trickle so SATA/NVMe queues remain 100% responsive
                Sleep(1);
                if (ShouldAbortOrYieldPrewarm()) break;
            }
        }
        (void)pageTouchAccumulator;

        g_statFiles.fetch_add(1, std::memory_order_relaxed);
        g_statBytes.fetch_add(totalRead, std::memory_order_relaxed);
    }

    CloseHandle(hFile);
    return true;
}

// Resumable low-I/O file pre-warmer for PostIntro: pauses at exact 256 KiB file offset while foreground is busy,
// enforces cumulative byte budget, skips already-completed files on Options resume, and aborts on cancel/match/disable.
bool PrewarmFilePostIntro(const std::wstring& fullPath, uint64_t* ioRemainingBudgetBytes) {
    if (!WaitWhilePostIntroAllowed()) {
        return false;
    }
    if (IsPostIntroPathCompleted(fullPath)) {
        return true;
    }
    if (ioRemainingBudgetBytes != nullptr) {
        if (*ioRemainingBudgetBytes == 0ULL) {
            g_postIntroBudgetExhausted.store(true, std::memory_order_release);
            return false;
        }
    } else if (g_postIntroBudgetInitialized.load(std::memory_order_acquire)) {
        if (g_postIntroFaultedBytes.load(std::memory_order_relaxed) >=
            g_postIntroBudgetBytes.load(std::memory_order_relaxed)) {
            g_postIntroBudgetExhausted.store(true, std::memory_order_release);
            return false;
        }
    }

    HANDLE hFile = CreateFileW(
        fullPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_SEQUENTIAL_SCAN,
        NULL
    );
    if (hFile == INVALID_HANDLE_VALUE) {
        return false;
    }

    ApplyLowDiskIoPriorityToFileHandle(hFile);

    bool completedFullFile = false;
    LARGE_INTEGER fileSize{};
    if (GetFileSizeEx(hFile, &fileSize)) {
        if (fileSize.QuadPart <= 0) {
            completedFullFile = true;
        } else {
            DWORD bytesRead = 0;
            uint64_t totalRead = 0;
            uint32_t chunkCount = 0;
            bool abortedOrCapped = false;
            volatile uint8_t pageTouchAccumulator = 0;

            while (true) {
                if (!WaitWhilePostIntroAllowed()) {
                    abortedOrCapped = true;
                    break;
                }

                DWORD toRead = kPrewarmScratchBytes;
                if (ioRemainingBudgetBytes != nullptr) {
                    if (*ioRemainingBudgetBytes == 0ULL) {
                        g_postIntroBudgetExhausted.store(true, std::memory_order_release);
                        abortedOrCapped = true;
                        break;
                    }
                    if (*ioRemainingBudgetBytes < static_cast<uint64_t>(toRead)) {
                        toRead = static_cast<DWORD>(*ioRemainingBudgetBytes);
                    }
                } else if (g_postIntroBudgetInitialized.load(std::memory_order_acquire)) {
                    const uint64_t faultedSoFar = g_postIntroFaultedBytes.load(std::memory_order_relaxed);
                    const uint64_t budgetCap = g_postIntroBudgetBytes.load(std::memory_order_relaxed);
                    if (faultedSoFar >= budgetCap) {
                        g_postIntroBudgetExhausted.store(true, std::memory_order_release);
                        abortedOrCapped = true;
                        break;
                    }
                    const uint64_t rem = budgetCap - faultedSoFar;
                    if (rem < static_cast<uint64_t>(toRead)) {
                        toRead = static_cast<DWORD>(rem);
                    }
                }

                if (!ReadFile(hFile, t_prewarmScratch, toRead, &bytesRead, NULL) || bytesRead == 0) {
                    break;
                }

                for (DWORD offset = 0; offset < bytesRead; offset += 4096) {
                    pageTouchAccumulator ^= t_prewarmScratch[offset];
                }
                totalRead += bytesRead;
                ++chunkCount;
                g_statBytes.fetch_add(bytesRead, std::memory_order_relaxed);
                g_postIntroFaultedBytes.fetch_add(bytesRead, std::memory_order_relaxed);
                if (ioRemainingBudgetBytes != nullptr) {
                    *ioRemainingBudgetBytes = (*ioRemainingBudgetBytes >= bytesRead)
                        ? (*ioRemainingBudgetBytes - bytesRead)
                        : 0ULL;
                }

                if (g_prewarmerGentlePacing.load(std::memory_order_relaxed) && (chunkCount & 31U) == 0U) {
                    Sleep(1);
                }
            }
            (void)pageTouchAccumulator;

            if (!abortedOrCapped && totalRead >= static_cast<uint64_t>(fileSize.QuadPart)) {
                completedFullFile = true;
                g_statFiles.fetch_add(1, std::memory_order_relaxed);
                g_postIntroFaultedFiles.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    CloseHandle(hFile);
    if (completedFullFile) {
        MarkPostIntroPathCompleted(fullPath);
    }
    return completedFullFile;
}

// Faults the final `tailBytes` (e.g. 4 MiB ZIP central directory tail) of large .s2z archives
// into the OS page cache with 0 heap allocations so archive central-directory parsing is instant across all 35 maps.
static bool PrewarmFileTailPostIntro(const std::wstring& fullPath, uint64_t tailBytes = 4ULL * 1024ULL * 1024ULL) {
    if (!WaitWhilePostIntroAllowed()) {
        return false;
    }
    const std::wstring tailKey = fullPath + L"#tail";
    if (IsPostIntroPathCompleted(tailKey)) {
        return true;
    }
    if (g_postIntroBudgetInitialized.load(std::memory_order_acquire) &&
        g_postIntroFaultedBytes.load(std::memory_order_relaxed) >= g_postIntroBudgetBytes.load(std::memory_order_relaxed)) {
        g_postIntroBudgetExhausted.store(true, std::memory_order_release);
        return false;
    }

    HANDLE hFile = CreateFileW(
        fullPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_SEQUENTIAL_SCAN,
        NULL
    );
    if (hFile == INVALID_HANDLE_VALUE) {
        return false;
    }

    ApplyLowDiskIoPriorityToFileHandle(hFile);

    bool completedTail = false;
    LARGE_INTEGER fileSize{};
    if (GetFileSizeEx(hFile, &fileSize) && fileSize.QuadPart > 0) {
        const uint64_t totalBytes = static_cast<uint64_t>(fileSize.QuadPart);
        uint64_t expectedTailBytes = totalBytes;
        if (totalBytes > tailBytes) {
            LARGE_INTEGER seekPos{};
            seekPos.QuadPart = static_cast<LONGLONG>(totalBytes - tailBytes);
            SetFilePointerEx(hFile, seekPos, NULL, FILE_BEGIN);
            expectedTailBytes = tailBytes;
        }

        DWORD bytesRead = 0;
        uint64_t totalRead = 0;
        bool aborted = false;
        volatile uint8_t pageTouchAccumulator = 0;
        while (true) {
            if (!WaitWhilePostIntroAllowed()) {
                aborted = true;
                break;
            }
            if (!ReadFile(hFile, t_prewarmScratch, kPrewarmScratchBytes, &bytesRead, NULL) || bytesRead == 0) {
                break;
            }
            for (DWORD offset = 0; offset < bytesRead; offset += 4096) {
                pageTouchAccumulator ^= t_prewarmScratch[offset];
            }
            totalRead += bytesRead;
            g_statBytes.fetch_add(bytesRead, std::memory_order_relaxed);
            g_postIntroFaultedBytes.fetch_add(bytesRead, std::memory_order_relaxed);
        }
        (void)pageTouchAccumulator;

        if (!aborted && totalRead >= expectedTailBytes) {
            completedTail = true;
            g_statFiles.fetch_add(1, std::memory_order_relaxed);
            g_postIntroFaultedFiles.fetch_add(1, std::memory_order_relaxed);
        }
    }

    CloseHandle(hFile);
    if (completedTail) {
        MarkPostIntroPathCompleted(tailKey);
    }
    return completedTail;
}

// Recursively walks target directory and pre-warms combat & entity/UI definition assets
static uint32_t PrewarmDirectoryTree(const fs::path& dirPath, bool includeDefinitions = false, bool forPostIntro = false) {
    if (!fs::exists(dirPath) || !fs::is_directory(dirPath)) {
        return 0;
    }

    uint32_t count = 0;
    std::error_code ec;
    for (const auto& entry : fs::recursive_directory_iterator(dirPath, fs::directory_options::skip_permission_denied, ec)) {
        if (forPostIntro) {
            if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
        } else if (ShouldAbortOrYieldPrewarm()) {
            if (!WaitWhileForegroundBusy()) break;
        }
        if (entry.is_regular_file(ec)) {
            std::string ext = entry.path().extension().string();
            std::transform(ext.begin(), ext.end(), ext.begin(), ::tolower);

            bool match = (ext == ".effect" || ext == ".material" || ext == ".mdf" ||
                          ext == ".tga" || ext == ".dds" || ext == ".wav" || ext == ".ogg" || ext == ".clip");
            if (!match && includeDefinitions) {
                match = (ext == ".entity" || ext == ".gamemechanics" || ext == ".upgrades" ||
                         ext == ".str" || ext == ".interface" || ext == ".package" || ext == ".lua");
            }
            if (match) {
                const bool ok = forPostIntro
                    ? PrewarmFilePostIntro(entry.path().wstring())
                    : PrewarmFile(entry.path().wstring());
                if (ok) {
                    count++;
                } else if (forPostIntro && g_postIntroBudgetExhausted.load(std::memory_order_acquire)) {
                    break;
                }
            }
        }
    }
    return count;
}

static std::string ExtractHeroFolder(const std::string& heroName) {
    std::string slug = heroName;
    if (slug.rfind("Hero_", 0) == 0) {
        slug = slug.substr(5);
    }
    std::transform(slug.begin(), slug.end(), slug.begin(), ::tolower);
    return slug;
}

static void ProcessPrewarmHero(const std::string& heroName, const std::string& avatarKey) {
    auto startTime = std::chrono::high_resolution_clock::now();
    std::string folder = ExtractHeroFolder(heroName);

    LogPrewarm(" [ASSET_PREWARM] Warming hero assets: %s (folder: %s, avatar: %s)...",
        heroName.c_str(), folder.c_str(), avatarKey.empty() ? "default" : avatarKey.c_str());

    std::vector<fs::path> searchBases = {
        g_gameRootDir / "override" / "heroes" / folder,
        g_gameRootDir / "game" / "heroes" / folder,
        g_gameRootDir / "ClientPackageFiles" / "heroes" / folder
    };

    uint32_t heroFiles = 0;
    for (const auto& basePath : searchBases) {
        if (fs::exists(basePath)) {
            heroFiles += PrewarmDirectoryTree(basePath);
        }
    }

    // If a specific alt avatar is requested, warm its specific subfolder
    if (!avatarKey.empty() && avatarKey != "default" && avatarKey != "Hero") {
        std::string avatarFolder = avatarKey;
        size_t dotPos = avatarFolder.find('.');
        if (dotPos != std::string::npos) {
            avatarFolder = avatarFolder.substr(dotPos + 1);
        }
        std::transform(avatarFolder.begin(), avatarFolder.end(), avatarFolder.begin(), ::tolower);

        for (const auto& basePath : searchBases) {
            fs::path altPath = basePath / avatarFolder;
            if (fs::exists(altPath)) {
                heroFiles += PrewarmDirectoryTree(altPath);
            }
        }
    }

    auto endTime = std::chrono::high_resolution_clock::now();
    float durationMs = std::chrono::duration<float, std::milli>(endTime - startTime).count();

    g_statHeroes.fetch_add(1, std::memory_order_relaxed);
    AddDurationMs(durationMs);

    LogPrewarm(" [ASSET_PREWARM] Pre-warmed %u assets for %s in %.2f ms.",
        heroFiles, heroName.c_str(), durationMs);
}

static void ProcessPrewarmItem(const std::string& itemCode) {
    auto startTime = std::chrono::high_resolution_clock::now();
    std::string itemName = itemCode;
    if (itemName.rfind("Item_", 0) == 0) {
        itemName = itemName.substr(5);
    }
    std::transform(itemName.begin(), itemName.end(), itemName.begin(), ::tolower);

    std::vector<fs::path> searchBases = {
        g_gameRootDir / "override" / "items" / itemName,
        g_gameRootDir / "game" / "items" / itemName,
        g_gameRootDir / "ClientPackageFiles" / "items" / itemName
    };

    uint32_t itemFiles = 0;
    for (const auto& basePath : searchBases) {
        if (fs::exists(basePath)) {
            itemFiles += PrewarmDirectoryTree(basePath);
        }
    }

    auto endTime = std::chrono::high_resolution_clock::now();
    float durationMs = std::chrono::duration<float, std::milli>(endTime - startTime).count();

    g_statItems.fetch_add(1, std::memory_order_relaxed);
    AddDurationMs(durationMs);
}

static void ProcessPrewarmMap(const std::string& mapName) {
    auto startTime = std::chrono::high_resolution_clock::now();
    std::string cleanMap = mapName;
    std::transform(cleanMap.begin(), cleanMap.end(), cleanMap.begin(), ::tolower);
    while (!cleanMap.empty() && (cleanMap.back() == ' ' || cleanMap.back() == '\t' || cleanMap.back() == '\r' || cleanMap.back() == '\n')) {
        cleanMap.pop_back();
    }
    while (!cleanMap.empty() && (cleanMap.front() == ' ' || cleanMap.front() == '\t')) {
        cleanMap.erase(0, 1);
    }
    if (cleanMap.empty()) return;

    LogPrewarm(" [ASSET_PREWARM] Warming map assets: %s...", cleanMap.c_str());

    // Adaptive RAM check via GlobalMemoryStatusEx
    MEMORYSTATUSEX memStatus;
    memStatus.dwLength = sizeof(memStatus);
    bool isAggressive = true;
    if (GlobalMemoryStatusEx(&memStatus)) {
        uint64_t totalRamGb = memStatus.ullTotalPhys / (1024ULL * 1024ULL * 1024ULL);
        if (totalRamGb <= 8) {
            isAggressive = false;
            LogPrewarm(" [ASSET_PREWARM] RAM <= 8GB (%llu GB detected). Conservative pre-warm mode active.", totalRamGb);
        } else {
            LogPrewarm(" [ASSET_PREWARM] RAM > 12GB (%llu GB detected). Aggressive pre-warm mode active.", totalRamGb);
        }
    }

    uint32_t mapFiles = 0;

    // 1. Map .s2z archives (skipped on SSD unless KONGOR_FORCE_SSD_PREWARM=1)
    const bool skipSsdArchives = ShouldSkipArchiveWarmupOnSsd(
        _stricmp(g_cachedDriveType, "SSD") == 0,
        IsForceSsdPrewarmEnabled());
    if (!skipSsdArchives) {
        std::vector<fs::path> mapArchives = {
            g_gameRootDir / "game" / "maps" / (cleanMap + ".s2z"),
            g_gameRootDir / "game" / "maps" / (cleanMap + "_reborn.s2z")
        };
        for (const auto& archivePath : mapArchives) {
            if (fs::exists(archivePath)) {
                if (PrewarmFile(archivePath.wstring())) {
                    mapFiles++;
                }
            }
        }
    }

    // 2. Unpacked map directory tree if present
    std::vector<fs::path> mapDirs = {
        g_gameRootDir / "game" / "maps" / cleanMap,
        g_gameRootDir / "game" / "maps" / (cleanMap + "_reborn"),
        g_gameRootDir / "override" / "maps" / cleanMap
    };
    for (const auto& dir : mapDirs) {
        if (fs::exists(dir)) {
            mapFiles += PrewarmDirectoryTree(dir);
        }
    }

    // 3. Lane Creeps (vital for Frame 0 combat)
    std::vector<fs::path> creepDirs = {
        g_gameRootDir / "game" / "npcs" / "good_melee",
        g_gameRootDir / "game" / "npcs" / "good_range",
        g_gameRootDir / "game" / "npcs" / "good_siege",
        g_gameRootDir / "game" / "npcs" / "bad_melee",
        g_gameRootDir / "game" / "npcs" / "bad_range",
        g_gameRootDir / "game" / "npcs" / "bad_siege"
    };
    for (const auto& creepDir : creepDirs) {
        if (fs::exists(creepDir)) {
            mapFiles += PrewarmDirectoryTree(creepDir);
        }
    }

    // 3b. Caldavar Neutral Creeps & Kongor Pit (vital for Frame 0 combat on Caldavar)
    if (cleanMap.find("caldavar") != std::string::npos) {
        std::vector<fs::path> caldavarNpcDirs = {
            g_gameRootDir / "game" / "npcs" / "kongor",
            g_gameRootDir / "game" / "npcs" / "boss_dragon",
            g_gameRootDir / "game" / "npcs" / "n_wolf",
            g_gameRootDir / "game" / "npcs" / "n_wolf_commander",
            g_gameRootDir / "game" / "npcs" / "n_catman",
            g_gameRootDir / "game" / "npcs" / "n_catman_leader",
            g_gameRootDir / "game" / "npcs" / "n_ogre",
            g_gameRootDir / "game" / "npcs" / "n_ogre_leader",
            g_gameRootDir / "game" / "npcs" / "n_minotaur",
            g_gameRootDir / "game" / "npcs" / "n_dragon",
            g_gameRootDir / "game" / "npcs" / "n_ebula",
            g_gameRootDir / "game" / "npcs" / "n_skeleton",
            g_gameRootDir / "game" / "npcs" / "n_hunter_war"
        };
        for (const auto& npcDir : caldavarNpcDirs) {
            if (fs::exists(npcDir)) {
                mapFiles += PrewarmDirectoryTree(npcDir);
            }
        }
    }

    // 4. In aggressive mode, prewarm competitive combat item pool as well
    if (isAggressive) {
        PrewarmCompetitiveItemPool();
    }

    auto endTime = std::chrono::high_resolution_clock::now();
    float durationMs = std::chrono::duration<float, std::milli>(endTime - startTime).count();

    g_statMaps.fetch_add(1, std::memory_order_relaxed);
    AddDurationMs(durationMs);

    LogPrewarm(" [ASSET_PREWARM] Pre-warmed %u assets for map '%s' in %.2f ms.",
        mapFiles, cleanMap.c_str(), durationMs);
}

static bool HasSufficientFreeRamGiB(uint64_t minTotalGiB, uint64_t minAvailGiB) noexcept {
    MEMORYSTATUSEX memStatus{};
    memStatus.dwLength = sizeof(memStatus);
    if (!GlobalMemoryStatusEx(&memStatus)) {
        return false;
    }
    constexpr uint64_t kGiB = 1024ULL * 1024ULL * 1024ULL;
    return (memStatus.ullTotalPhys >= (minTotalGiB * kGiB)) &&
           (memStatus.ullAvailPhys >= (minAvailGiB * kGiB));
}

static bool QuerySystemOnBatteryOrPowerSaver() noexcept {
    SYSTEM_POWER_STATUS sps{};
    if (!GetSystemPowerStatus(&sps)) {
        return false;
    }
    return ShouldCapWarmupToTier1OnPower(sps.ACLineStatus, sps.SystemStatusFlag);
}

static void DetectDriveMediaType(const fs::path& rootPath) noexcept {
    if (g_driveTypeTestOverride[0] != '\0') {
        strncpy_s(g_cachedDriveType, sizeof(g_cachedDriveType), g_driveTypeTestOverride, _TRUNCATE);
        return;
    }
    strncpy_s(g_cachedDriveType, sizeof(g_cachedDriveType), "UNKNOWN", _TRUNCATE);
    std::error_code ec;
    fs::path absRoot = fs::absolute(rootPath, ec);
    const std::wstring wRoot = absRoot.empty() ? rootPath.wstring() : absRoot.wstring();
    if (wRoot.size() < 2 || wRoot[1] != L':') {
        return;
    }
    wchar_t volumeDevicePath[16] = L"\\\\.\\X:";
    volumeDevicePath[4] = wRoot[0];

    // dwDesiredAccess = 0 queries storage device properties without requiring Administrator elevation
    HANDLE hVol = CreateFileW(
        volumeDevicePath,
        0,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        0,
        NULL);
    if (hVol == INVALID_HANDLE_VALUE) {
        return;
    }

    STORAGE_PROPERTY_QUERY query{};
    query.PropertyId = static_cast<STORAGE_PROPERTY_ID>(7); // StorageDeviceSeekPenaltyProperty
    query.QueryType = PropertyStandardQuery;

    struct SeekPenaltyDescCompat {
        DWORD   Version;
        DWORD   Size;
        BOOLEAN IncursSeekPenalty;
    } desc{};
    DWORD bytesReturned = 0;
    if (DeviceIoControl(hVol, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), &desc, sizeof(desc), &bytesReturned, NULL) &&
        bytesReturned >= sizeof(desc)) {
        strncpy_s(g_cachedDriveType, sizeof(g_cachedDriveType), desc.IncursSeekPenalty ? "HDD" : "SSD", _TRUNCATE);
    }
    CloseHandle(hVol);
}

static uint32_t PrewarmArchiveCentralDirectoryTails(bool forPostIntro = false) {
    const fs::path archiveDirs[] = {
        g_gameRootDir / "game",
        g_gameRootDir / "game" / "maps",
        g_gameRootDir / "KONGOR" / "updates",
        g_gameRootDir / "KONGOR" / "extensions"
    };
    uint32_t tailArchives = 0;
    for (const auto& dir : archiveDirs) {
        if (forPostIntro) {
            if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
        } else if (ShouldAbortOrYieldPrewarm()) {
            if (!WaitWhileForegroundBusy()) break;
        }
        std::error_code ec;
        if (!fs::exists(dir, ec) || !fs::is_directory(dir, ec)) continue;
        for (const auto& entry : fs::directory_iterator(dir, fs::directory_options::skip_permission_denied, ec)) {
            if (forPostIntro) {
                if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
            } else if (ShouldAbortOrYieldPrewarm()) {
                if (!WaitWhileForegroundBusy()) break;
            }
            if (!entry.is_regular_file(ec)) continue;
            std::string ext = entry.path().extension().string();
            std::transform(ext.begin(), ext.end(), ext.begin(), ::tolower);
            if (ext == ".s2z") {
                if (PrewarmFileTailPostIntro(entry.path().wstring(), 4ULL * 1024ULL * 1024ULL)) {
                    ++tailArchives;
                }
            }
        }
    }
    return tailArchives;
}

static void ProcessPrewarmCommon(bool forPostIntro = false) {
    auto startTime = std::chrono::high_resolution_clock::now();
    LogPrewarm(" [ASSET_PREWARM] Warming common shared UI, entity, and sharded texture archives...");

    std::vector<fs::path> commonPaths = {
        g_gameRootDir / "base" / "resources0.s2z",
        g_gameRootDir / "game" / "resources0.s2z",
        g_gameRootDir / "game" / "resources98.s2z",
        g_gameRootDir / "game" / "resources99.s2z",
        g_gameRootDir / "KONGOR" / "updates" / "textures.s2z",
        g_gameRootDir / "KONGOR" / "updates" / "resources0.s2z"
    };

    uint32_t count = 0;
    for (const auto& p : commonPaths) {
        if (forPostIntro) {
            if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
            if (fs::exists(p) && PrewarmFilePostIntro(p.wstring())) {
                count++;
            }
        } else {
            if (ShouldAbortOrYieldPrewarm()) {
                if (!WaitWhileForegroundBusy()) break;
            }
            if (fs::exists(p) && HasSufficientFreeRamGiB(10, 3)) {
                if (PrewarmFile(p.wstring())) {
                    count++;
                }
            }
        }
    }

    const fs::path gameTextures = g_gameRootDir / "game" / "textures.s2z";
    if (forPostIntro) {
        constexpr uint64_t kGiB = 1024ULL * 1024ULL * 1024ULL;
        if (WaitWhilePostIntroAllowed() &&
            !g_postIntroBudgetExhausted.load(std::memory_order_acquire) &&
            HasSufficientFreeRamGiB(15, 4) &&
            g_postIntroBudgetBytes.load(std::memory_order_relaxed) >= (3ULL * kGiB) &&
            fs::exists(gameTextures)) {
            if (PrewarmFilePostIntro(gameTextures.wstring())) {
                count++;
            }
        }
    } else if (HasSufficientFreeRamGiB(15, 6)) {
        if (fs::exists(gameTextures) && WaitWhileForegroundBusy()) {
            if (PrewarmFile(gameTextures.wstring())) {
                count++;
            }
        }
    }

    uint32_t tailArchives = 0;
    if (!forPostIntro && HasSufficientFreeRamGiB(10, 4)) {
        tailArchives = PrewarmArchiveCentralDirectoryTails(false);
    }

    auto endTime = std::chrono::high_resolution_clock::now();
    float durationMs = std::chrono::duration<float, std::milli>(endTime - startTime).count();
    AddDurationMs(durationMs);

    LogPrewarm(" [ASSET_PREWARM] Pre-warmed %u common core archives + %u .s2z central-directory tails in %.2f ms.",
        count, tailArchives, durationMs);
}

static void ProcessPrewarmPostIntro(const std::string& requestedMap) {
    if (g_postIntroCancelled.load(std::memory_order_acquire) ||
        g_prewarmerMatchActive.load(std::memory_order_acquire) ||
        g_postIntroCompleted.load(std::memory_order_acquire)) {
        return;
    }

    if (ShouldSkipArchiveWarmupOnSsd(_stricmp(g_cachedDriveType, "SSD") == 0, IsForceSsdPrewarmEnabled())) {
        g_postIntroSkippedSsd.store(true, std::memory_order_release);
        g_postIntroCompleted.store(true, std::memory_order_release);
        LogPrewarmMilestone(
            " [ASSET_PREWARM] Post-intro archive warmup SKIPPED_SSD (drive=%s, preserving ~6.9 GiB OS RAM cache; set KONGOR_FORCE_SSD_PREWARM=1 to override).",
            g_cachedDriveType);
        return;
    }

    uint64_t totalMiB = 0;
    uint64_t availMiB = 0;
    if (!g_postIntroBudgetInitialized.load(std::memory_order_acquire)) {
        MEMORYSTATUSEX memStatus{};
        memStatus.dwLength = sizeof(memStatus);
        if (!GlobalMemoryStatusEx(&memStatus) ||
            !IsMemoryEligibleForBackgroundWarmup(memStatus.ullTotalPhys, memStatus.ullAvailPhys)) {
            totalMiB = memStatus.ullTotalPhys / (1024ULL * 1024ULL);
            availMiB = memStatus.ullAvailPhys / (1024ULL * 1024ULL);
            LogPrewarmMilestone(
                " [ASSET_PREWARM] Post-intro background warmup SKIPPED due to RAM guard (total=%llu MiB, avail=%llu MiB; requires >= 10240 MiB total & >= 3072 MiB free).",
                static_cast<unsigned long long>(totalMiB),
                static_cast<unsigned long long>(availMiB));
            g_postIntroCompleted.store(true, std::memory_order_release);
            return;
        }
        totalMiB = memStatus.ullTotalPhys / (1024ULL * 1024ULL);
        availMiB = memStatus.ullAvailPhys / (1024ULL * 1024ULL);

        uint64_t computedBudget = ComputePostIntroWarmupBudgetBytes(memStatus.ullTotalPhys > 0 ? memStatus.ullAvailPhys : 0ULL);
        const bool onBattery = QuerySystemOnBatteryOrPowerSaver();
        g_postIntroOnBatteryOrSaver.store(onBattery, std::memory_order_release);
        if (onBattery) {
            constexpr uint64_t kBatteryTier1CapBytes = 512ULL * 1024ULL * 1024ULL;
            if (computedBudget > kBatteryTier1CapBytes) {
                computedBudget = kBatteryTier1CapBytes;
            }
        }
        g_postIntroBudgetBytes.store(computedBudget, std::memory_order_release);
        g_postIntroBudgetExhausted.store(false, std::memory_order_release);
        g_warmupWindowPeakFrameUs.store(0, std::memory_order_relaxed);
        g_warmupWindowHitchCount.store(0, std::memory_order_relaxed);
        g_postIntroBudgetInitialized.store(true, std::memory_order_release);
    } else {
        (void)IsSystemEligibleForBackgroundWarmup(&totalMiB, &availMiB);
    }

    std::string cleanMap = requestedMap.empty() ? "caldavar" : requestedMap;
    std::transform(cleanMap.begin(), cleanMap.end(), cleanMap.begin(), ::tolower);

    const uint64_t budgetMiB = g_postIntroBudgetBytes.load(std::memory_order_relaxed) / (1024ULL * 1024ULL);
    const bool onBattery = g_postIntroOnBatteryOrSaver.load(std::memory_order_relaxed);
    auto startTime = std::chrono::high_resolution_clock::now();

    LogPrewarmMilestone(
        " [ASSET_PREWARM] Post-intro background warmup STARTED (map='%s', root='%ls', drive=%s, onBattery=%d, totalRam=%llu MiB, availRam=%llu MiB, budgetMiB=%llu, pacing=low_io_gentle_8MiB_1ms).",
        cleanMap.c_str(),
        g_gameRootDir.c_str(),
        g_cachedDriveType,
        onBattery ? 1 : 0,
        static_cast<unsigned long long>(totalMiB),
        static_cast<unsigned long long>(availMiB),
        static_cast<unsigned long long>(budgetMiB));

    struct PostIntroActiveScope {
        PostIntroActiveScope() noexcept {
            g_prewarmerGentlePacing.store(true, std::memory_order_release);
            g_isPostIntroActive.store(true, std::memory_order_release);
        }
        ~PostIntroActiveScope() noexcept {
            g_isPostIntroActive.store(false, std::memory_order_release);
            g_prewarmerGentlePacing.store(false, std::memory_order_release);
        }
    } activeScope;

    // Tier 1: Loose VFS override/UI/entity definitions + both core map archives (caldavar.s2z + midwars.s2z) + .s2z central-directory tails
    const fs::path looseDefinitionRoots[] = {
        g_gameRootDir / "override",
        g_gameRootDir / "ClientPackageFiles" / "ui",
        g_gameRootDir / "game" / "ui",
        g_gameRootDir / "shared",
        g_gameRootDir / "KONGOR" / "configuration",
        g_gameRootDir / "KONGOR" / "extensions"
    };
    uint32_t looseFiles = 0;
    for (const auto& root : looseDefinitionRoots) {
        if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
        if (fs::exists(root)) {
            looseFiles += PrewarmDirectoryTree(root, /*includeDefinitions=*/true, /*forPostIntro=*/true);
        }
    }

    if (WaitWhilePostIntroAllowed() && !g_postIntroBudgetExhausted.load(std::memory_order_acquire)) {
        const std::string tier1Maps[] = { "caldavar", "midwars", cleanMap };
        for (const auto& m : tier1Maps) {
            if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
            const fs::path mapS2z = g_gameRootDir / "game" / "maps" / (m + ".s2z");
            if (fs::exists(mapS2z)) {
                (void)PrewarmFilePostIntro(mapS2z.wstring());
            }
        }
        (void)PrewarmArchiveCentralDirectoryTails(/*forPostIntro=*/true);
    }

    auto tier1End = std::chrono::high_resolution_clock::now();
    float tier1Ms = std::chrono::duration<float, std::milli>(tier1End - startTime).count();
    LogPrewarmMilestone(
        " [ASSET_PREWARM] Post-intro Tier 1 (loose .entity/.interface/.lua + caldavar/midwars.s2z + .s2z tails) finished: looseFiles=%u faultedMiB=%llu/%llu elapsed=%.1f ms.",
        looseFiles,
        static_cast<unsigned long long>(g_postIntroFaultedBytes.load(std::memory_order_relaxed) / (1024ULL * 1024ULL)),
        static_cast<unsigned long long>(budgetMiB),
        tier1Ms);

    // On DC battery or Battery Saver, stop cleanly after Tier 1 (~350 MiB) to preserve laptop battery & thermals
    if (!onBattery) {
        // Tier 2: Core entity, UI, and sharded texture archives within snapshot byte budget
        if (WaitWhilePostIntroAllowed() && !g_postIntroBudgetExhausted.load(std::memory_order_acquire)) {
            ProcessPrewarmCommon(/*forPostIntro=*/true);
        }

        // Tier 3: Map creeps/bosses + high-RAM model/sound archives (guarded by >= 20 GiB total & remaining byte budget)
        if (WaitWhilePostIntroAllowed() && !g_postIntroBudgetExhausted.load(std::memory_order_acquire) && HasSufficientFreeRamGiB(20, 4)) {
            const char* extraModelArchives[] = {
                "resources1.s2z",
                "resources2.s2z",
                "resources3.s2z",
                "resources4.s2z",
                "resources5.s2z"
            };
            for (const char* archiveName : extraModelArchives) {
                if (!WaitWhilePostIntroAllowed() || g_postIntroBudgetExhausted.load(std::memory_order_acquire)) break;
                fs::path p = g_gameRootDir / "game" / archiveName;
                if (fs::exists(p)) {
                    (void)PrewarmFilePostIntro(p.wstring());
                }
            }
        }
    }

    auto endTime = std::chrono::high_resolution_clock::now();
    float runMs = std::chrono::duration<float, std::milli>(endTime - startTime).count();
    AddDurationMs(runMs);
    AddPostIntroElapsedMs(runMs);

    const uint32_t totalPostIntroFiles = g_postIntroFaultedFiles.load(std::memory_order_relaxed);
    const uint64_t totalPostIntroMiB = g_postIntroFaultedBytes.load(std::memory_order_relaxed) / (1024ULL * 1024ULL);
    const float peakFrameMs = static_cast<float>(g_warmupWindowPeakFrameUs.load(std::memory_order_relaxed)) / 1000.0f;
    const uint64_t hitches = g_warmupWindowHitchCount.load(std::memory_order_relaxed);

    const char* finalStatus = "COMPLETE";
    if (!g_prewarmerRunning.load(std::memory_order_acquire)) {
        finalStatus = "STOPPED_SHUTDOWN";
    } else if (g_postIntroCancelled.load(std::memory_order_acquire) || g_prewarmerMatchActive.load(std::memory_order_acquire)) {
        finalStatus = "CANCELLED_MATCH_OR_PRELOAD";
        g_postIntroCancelled.store(true, std::memory_order_release);
    } else if (!g_prewarmerEnabled.load(std::memory_order_acquire)) {
        // Paused by player unchecking Options checkbox -> allow re-checking later in session to resume un-warmed files!
        finalStatus = "PAUSED_BY_PLAYER";
        g_postIntroTriggered.store(false, std::memory_order_release);
    } else if (g_postIntroBudgetExhausted.load(std::memory_order_acquire)) {
        finalStatus = "COMPLETE_BUDGET_CAP";
        g_postIntroCompleted.store(true, std::memory_order_release);
    } else if (onBattery) {
        finalStatus = "COMPLETE_BATTERY_TIER1_CAP";
        g_postIntroCompleted.store(true, std::memory_order_release);
    } else {
        finalStatus = "COMPLETE";
        g_postIntroCompleted.store(true, std::memory_order_release);
    }

    LogPrewarmMilestone(
        " [ASSET_PREWARM] Post-intro background warmup %s (map='%s', drive=%s, files=%u, faultedMiB=%llu/%llu, elapsed=%.1f ms, peakMenuFrameMs=%.2f, hitches=%llu).",
        finalStatus,
        cleanMap.c_str(),
        g_cachedDriveType,
        totalPostIntroFiles,
        static_cast<unsigned long long>(totalPostIntroMiB),
        static_cast<unsigned long long>(budgetMiB),
        g_postIntroElapsedMs.load(std::memory_order_relaxed),
        peakFrameMs,
        static_cast<unsigned long long>(hitches));
}

static bool IsValidClientGameRoot(const fs::path& candidate) noexcept {
    std::error_code ec;
    return !candidate.empty() &&
           (fs::exists(candidate / "game" / "resources0.s2z", ec) ||
            fs::exists(candidate / "base" / "resources0.s2z", ec));
}

static DWORD WINAPI PrewarmerWorkerThread(LPVOID lpParam) {
    (void)lpParam;
    // Keep CPU scheduling at THREAD_PRIORITY_LOWEST + ThreadIoPriority = IoPriorityLow (1)
    // while preserving Normal NT Standby Page Priority (Priority 5) so faulted .s2z / .entity pages stay resident.
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_LOWEST);
    const bool lowIoApplied = ApplyLowDiskIoPriorityToCurrentThread();
    LogPrewarmMilestone(
        " [ASSET_PREWARM] Background worker thread ready (ThreadId: %lu, CpuPriority: LOWEST, ThreadIoPriorityLow: %d, awaiting post-intro signal).",
        GetCurrentThreadId(),
        lowIoApplied ? 1 : 0);

    while (g_prewarmerRunning.load(std::memory_order_acquire)) {
        DWORD waitRes = WaitForSingleObject(g_hPrewarmEvent, 500);
        (void)waitRes;
        if (!g_prewarmerRunning.load(std::memory_order_acquire)) break;
        if (!g_prewarmerEnabled.load(std::memory_order_acquire)) continue;
        if (g_prewarmerForegroundBusy.load(std::memory_order_acquire)) {
            if (!WaitWhileForegroundBusy()) continue;
        }

        PrewarmJob currentJob;
        bool hasJob = false;

        EnterCriticalSection(&g_prewarmLock);
        if (!g_jobQueue.empty()) {
            currentJob = g_jobQueue.front();
            g_jobQueue.pop_front();
            hasJob = true;
        }
        LeaveCriticalSection(&g_prewarmLock);

        if (hasJob) {
            g_isPrewarmingActive.store(true, std::memory_order_release);
            switch (currentJob.Type) {
                case PrewarmJobType::Hero:
                    ProcessPrewarmHero(currentJob.Name, currentJob.Extra);
                    break;
                case PrewarmJobType::Item:
                    ProcessPrewarmItem(currentJob.Name);
                    break;
                case PrewarmJobType::Map:
                    ProcessPrewarmMap(currentJob.Name);
                    break;
                case PrewarmJobType::Common:
                    ProcessPrewarmCommon(false);
                    break;
                case PrewarmJobType::PostIntro:
                    ProcessPrewarmPostIntro(currentJob.Name);
                    break;
            }
            g_isPrewarmingActive.store(false, std::memory_order_release);
        }
    }

    LogPrewarm(" [ASSET_PREWARM] Background worker thread shutting down.");
    return 0;
}

void InitializeAssetPrewarmer() {
    Kongor::PatchController::RegisterGroupCallback("prewarm", SetAssetPrewarmerEnabled, true);
    if (g_prewarmerRunning.load(std::memory_order_acquire)) return;

    if (!g_gameRootTestOverride.empty()) {
        g_gameRootDir = fs::path(g_gameRootTestOverride);
    } else {
        wchar_t exePath[MAX_PATH] = { 0 };
        if (GetModuleFileNameW(NULL, exePath, MAX_PATH) > 0) {
            fs::path exeDir = fs::path(exePath).parent_path();
            if (IsValidClientGameRoot(exeDir)) {
                g_gameRootDir = exeDir;
            } else if (IsValidClientGameRoot(exeDir.parent_path())) {
                g_gameRootDir = exeDir.parent_path();
            } else {
                g_gameRootDir = exeDir;
            }
        } else {
            g_gameRootDir = fs::current_path();
        }

        if (!IsValidClientGameRoot(g_gameRootDir)) {
            HMODULE hSelf = NULL;
            if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                (LPCWSTR)&InitializeAssetPrewarmer, &hSelf) && hSelf) {
                wchar_t dllPath[MAX_PATH] = { 0 };
                if (GetModuleFileNameW(hSelf, dllPath, MAX_PATH) > 0) {
                    fs::path dllDir = fs::path(dllPath).parent_path();
                    if (IsValidClientGameRoot(dllDir)) {
                        g_gameRootDir = dllDir;
                    } else if (IsValidClientGameRoot(dllDir.parent_path())) {
                        g_gameRootDir = dllDir.parent_path();
                    }
                }
            }
        }

        if (!IsValidClientGameRoot(g_gameRootDir)) {
            fs::path candidate = fs::current_path() / "games" / "client";
            if (IsValidClientGameRoot(candidate)) {
                g_gameRootDir = candidate;
            }
        }
    }

    DetectDriveMediaType(g_gameRootDir);

    g_prewarmerEnabled.store(true, std::memory_order_relaxed);
    g_postIntroTriggered.store(false, std::memory_order_relaxed);
    g_postIntroCancelled.store(false, std::memory_order_relaxed);
    g_postIntroCompleted.store(false, std::memory_order_relaxed);
    g_postIntroBudgetInitialized.store(false, std::memory_order_relaxed);
    g_postIntroBudgetExhausted.store(false, std::memory_order_relaxed);
    g_postIntroOnBatteryOrSaver.store(false, std::memory_order_relaxed);
    g_postIntroSkippedSsd.store(false, std::memory_order_relaxed);
    g_isPostIntroActive.store(false, std::memory_order_relaxed);
    g_prewarmerForegroundBusy.store(false, std::memory_order_relaxed);
    g_prewarmerMatchActive.store(false, std::memory_order_relaxed);
    g_prewarmerGentlePacing.store(false, std::memory_order_relaxed);
    g_statFiles.store(0, std::memory_order_relaxed);
    g_statBytes.store(0, std::memory_order_relaxed);
    g_statHeroes.store(0, std::memory_order_relaxed);
    g_statItems.store(0, std::memory_order_relaxed);
    g_statMaps.store(0, std::memory_order_relaxed);
    g_statDurationMs.store(0.0f, std::memory_order_relaxed);
    g_postIntroBudgetBytes.store(0, std::memory_order_relaxed);
    g_postIntroFaultedBytes.store(0, std::memory_order_relaxed);
    g_postIntroFaultedFiles.store(0, std::memory_order_relaxed);
    g_postIntroElapsedMs.store(0.0f, std::memory_order_relaxed);
    g_warmupWindowPeakFrameUs.store(0, std::memory_order_relaxed);
    g_warmupWindowHitchCount.store(0, std::memory_order_relaxed);

    InitializeCriticalSection(&g_prewarmLock);
    g_jobQueue.clear();
    g_postIntroCompletedPaths.clear();
    g_hPrewarmEvent = CreateEvent(NULL, FALSE, FALSE, NULL);
    g_prewarmerRunning.store(true, std::memory_order_release);

    g_hPrewarmThread = CreateThread(NULL, 0, PrewarmerWorkerThread, NULL, 0, NULL);
    if (g_hPrewarmThread) {
        SetThreadPriority(g_hPrewarmThread, THREAD_PRIORITY_LOWEST);
    }

    LogPrewarmMilestone(" [ASSET_PREWARM] Asset Pre-Warming Engine initialized on '%ls' (drive=%s, boot disk I/O deferred until Beauty Empath intro finishes).",
        g_gameRootDir.c_str(),
        g_cachedDriveType);
}

void ShutdownAssetPrewarmer() {
    if (!g_prewarmerRunning.load(std::memory_order_acquire)) return;

    g_prewarmerRunning.store(false, std::memory_order_release);
    if (g_hPrewarmEvent) {
        SetEvent(g_hPrewarmEvent);
    }

    if (g_hPrewarmThread) {
        WaitForSingleObject(g_hPrewarmThread, 1000);
        CloseHandle(g_hPrewarmThread);
        g_hPrewarmThread = NULL;
    }

    if (g_hPrewarmEvent) {
        CloseHandle(g_hPrewarmEvent);
        g_hPrewarmEvent = NULL;
    }

    EnterCriticalSection(&g_prewarmLock);
    g_jobQueue.clear();
    g_postIntroCompletedPaths.clear();
    LeaveCriticalSection(&g_prewarmLock);

    DeleteCriticalSection(&g_prewarmLock);
    LogPrewarm(" [ASSET_PREWARM] Asset Pre-Warming Engine shutdown cleanly.");
}

bool IsSystemEligibleForBackgroundWarmup(uint64_t* outTotalMiB, uint64_t* outAvailMiB) {
    MEMORYSTATUSEX memStatus{};
    memStatus.dwLength = sizeof(memStatus);
    if (!GlobalMemoryStatusEx(&memStatus)) {
        if (outTotalMiB) *outTotalMiB = 0;
        if (outAvailMiB) *outAvailMiB = 0;
        return false;
    }
    if (outTotalMiB) *outTotalMiB = memStatus.ullTotalPhys / (1024ULL * 1024ULL);
    if (outAvailMiB) *outAvailMiB = memStatus.ullAvailPhys / (1024ULL * 1024ULL);
    return IsMemoryEligibleForBackgroundWarmup(memStatus.ullTotalPhys, memStatus.ullAvailPhys);
}

bool TriggerPostIntroBackgroundWarmup(const std::string& mapName) {
    if (!g_prewarmerRunning.load(std::memory_order_acquire)) {
        LogPrewarmMilestone(" [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') ignored: worker not running.", mapName.c_str());
        return false;
    }
    if (!g_prewarmerEnabled.load(std::memory_order_acquire)) {
        LogPrewarmMilestone(" [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') skipped: prewarm disabled by player setting.", mapName.c_str());
        return false;
    }
    if (g_postIntroCancelled.load(std::memory_order_acquire) || g_prewarmerMatchActive.load(std::memory_order_acquire)) {
        LogPrewarm(" [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') ignored: match/preload already cancelled session warmup.", mapName.c_str());
        return false;
    }
    if (g_postIntroCompleted.load(std::memory_order_acquire)) {
        LogPrewarm(" [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') ignored: already completed this session.", mapName.c_str());
        return false;
    }

    if (g_driveTypeTestOverride[0] != '\0') {
        strncpy_s(g_cachedDriveType, sizeof(g_cachedDriveType), g_driveTypeTestOverride, _TRUNCATE);
    }

    if (ShouldSkipArchiveWarmupOnSsd(_stricmp(g_cachedDriveType, "SSD") == 0, IsForceSsdPrewarmEnabled())) {
        g_postIntroSkippedSsd.store(true, std::memory_order_release);
        g_postIntroCompleted.store(true, std::memory_order_release);
        LogPrewarmMilestone(
            " [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') SKIPPED_SSD (drive=%s, zero mechanical seek penalty; preserving ~6.9 GiB OS RAM cache).",
            mapName.empty() ? "caldavar" : mapName.c_str(),
            g_cachedDriveType);
        return false;
    }

    uint64_t totalMiB = 0;
    uint64_t availMiB = 0;
    if (!g_postIntroBudgetInitialized.load(std::memory_order_acquire) &&
        !IsSystemEligibleForBackgroundWarmup(&totalMiB, &availMiB)) {
        LogPrewarmMilestone(
            " [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') skipped by RAM guard (total=%llu MiB, avail=%llu MiB).",
            mapName.c_str(),
            static_cast<unsigned long long>(totalMiB),
            static_cast<unsigned long long>(availMiB));
        return false;
    }
    if (totalMiB == 0) {
        (void)IsSystemEligibleForBackgroundWarmup(&totalMiB, &availMiB);
    }

    bool expected = false;
    if (!g_postIntroTriggered.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
        LogPrewarm(" [ASSET_PREWARM] TriggerPostIntroBackgroundWarmup('%s') ignored: already in progress.", mapName.c_str());
        return false;
    }

    EnterCriticalSection(&g_prewarmLock);
    g_jobQueue.push_front({ PrewarmJobType::PostIntro, mapName.empty() ? "caldavar" : mapName, "" });
    LeaveCriticalSection(&g_prewarmLock);

    if (g_hPrewarmEvent) {
        SetEvent(g_hPrewarmEvent);
    }
    LogPrewarmMilestone(" [ASSET_PREWARM] Enqueued post-intro background warmup for map='%s' (totalRam=%llu MiB, availRam=%llu MiB).",
        mapName.empty() ? "caldavar" : mapName.c_str(),
        static_cast<unsigned long long>(totalMiB),
        static_cast<unsigned long long>(availMiB));
    return true;
}

void SetPrewarmerForegroundBusy(bool busy) {
    const bool prev = g_prewarmerForegroundBusy.exchange(busy, std::memory_order_acq_rel);
    if (!busy && g_hPrewarmEvent) {
        SetEvent(g_hPrewarmEvent);
    }
    if (prev != busy) {
        LogPrewarmMilestone(" [ASSET_PREWARM] Foreground load busy state -> %s (background prewarmer %s).",
            busy ? "BUSY" : "IDLE",
            busy ? "yielding disk I/O immediately" : "allowed to resume");
    }
}

bool IsPrewarmerForegroundBusy() {
    return g_prewarmerForegroundBusy.load(std::memory_order_acquire);
}

void SetPrewarmerMatchActive(bool matchActive) {
    const bool prev = g_prewarmerMatchActive.exchange(matchActive, std::memory_order_acq_rel);
    if (matchActive) {
        const bool wasCancelled = g_postIntroCancelled.exchange(true, std::memory_order_acq_rel);
        if (g_hPrewarmEvent) {
            SetEvent(g_hPrewarmEvent);
        }
        if (!prev && !wasCancelled && !g_postIntroCompleted.load(std::memory_order_acquire)) {
            LogPrewarmMilestone(" [ASSET_PREWARM] Match/server connection active -> PostIntro background warmup permanently cancelled for session (prewarm_hero remains ready).");
        }
    }
}

bool IsPrewarmerMatchActive() {
    return g_prewarmerMatchActive.load(std::memory_order_acquire);
}

void CancelPostIntroWarmup(const char* reason) {
    const bool prev = g_postIntroCancelled.exchange(true, std::memory_order_acq_rel);
    if (g_hPrewarmEvent) {
        SetEvent(g_hPrewarmEvent);
    }
    if (!prev && !g_postIntroCompleted.load(std::memory_order_acquire)) {
        LogPrewarmMilestone(" [ASSET_PREWARM] PostIntro background warmup cancelled (reason='%s').",
            (reason && reason[0]) ? reason : "cancelled");
    }
}

bool IsPostIntroWarmupCancelled() {
    return g_postIntroCancelled.load(std::memory_order_acquire);
}

bool IsPostIntroWarmupCompleted() {
    return g_postIntroCompleted.load(std::memory_order_acquire);
}

void NotifyPrewarmerFrameTimeUs(uint32_t cpuFrameTimeUs) noexcept {
    if (!g_isPostIntroActive.load(std::memory_order_relaxed) ||
        g_prewarmerForegroundBusy.load(std::memory_order_relaxed) ||
        !g_prewarmerEnabled.load(std::memory_order_relaxed)) {
        return;
    }
    if (cpuFrameTimeUs > 5000U) {
        g_warmupWindowHitchCount.fetch_add(1, std::memory_order_relaxed);
    }
    uint32_t prevPeak = g_warmupWindowPeakFrameUs.load(std::memory_order_relaxed);
    while (cpuFrameTimeUs > prevPeak &&
           !g_warmupWindowPeakFrameUs.compare_exchange_weak(prevPeak, cpuFrameTimeUs, std::memory_order_relaxed)) {
    }
}

PostIntroTelemetrySnapshot GetPostIntroTelemetrySnapshot() noexcept {
    const char* state = "NOT_STARTED";
    if (!g_prewarmerEnabled.load(std::memory_order_acquire) && !g_postIntroCompleted.load(std::memory_order_acquire)) {
        state = g_postIntroFaultedBytes.load(std::memory_order_relaxed) > 0 ? "PAUSED_BY_PLAYER" : "DISABLED";
    } else if (g_isPostIntroActive.load(std::memory_order_acquire)) {
        state = "IN_PROGRESS";
    } else if (g_postIntroCompleted.load(std::memory_order_acquire)) {
        if (g_postIntroSkippedSsd.load(std::memory_order_acquire)) {
            state = "SKIPPED_SSD";
        } else if (g_postIntroBudgetExhausted.load(std::memory_order_acquire)) {
            state = "COMPLETE_BUDGET_CAP";
        } else if (g_postIntroOnBatteryOrSaver.load(std::memory_order_acquire)) {
            state = "COMPLETE_BATTERY_TIER1_CAP";
        } else if (g_postIntroFaultedFiles.load(std::memory_order_relaxed) == 0 &&
                   g_postIntroBudgetBytes.load(std::memory_order_relaxed) == 0) {
            state = "SKIPPED_RAM_GUARD";
        } else {
            state = "COMPLETE";
        }
    } else if (g_postIntroCancelled.load(std::memory_order_acquire)) {
        state = "CANCELLED_EARLY";
    }

    PostIntroTelemetrySnapshot snap{};
    snap.WarmupEnabled = g_prewarmerEnabled.load(std::memory_order_relaxed);
    snap.WarmupState = state;
    snap.DriveType = g_cachedDriveType;
    snap.OnBatteryOrSaver = g_postIntroOnBatteryOrSaver.load(std::memory_order_relaxed);
    snap.PostIntroFiles = g_postIntroFaultedFiles.load(std::memory_order_relaxed);
    snap.PostIntroFaultedBytes = g_postIntroFaultedBytes.load(std::memory_order_relaxed);
    snap.BudgetBytes = g_postIntroBudgetBytes.load(std::memory_order_relaxed);
    snap.WarmupElapsedMs = g_postIntroElapsedMs.load(std::memory_order_relaxed);
    snap.PeakFrameMs = static_cast<float>(g_warmupWindowPeakFrameUs.load(std::memory_order_relaxed)) / 1000.0f;
    snap.HitchCount = g_warmupWindowHitchCount.load(std::memory_order_relaxed);
    return snap;
}

void LogPlayNowPreloadTelemetry(const char* outcome, uint64_t preloadElapsedMs, const char* mapName) {
    const PostIntroTelemetrySnapshot snap = GetPostIntroTelemetrySnapshot();
    const uint64_t faultedMiB = snap.PostIntroFaultedBytes / (1024ULL * 1024ULL);
    const uint64_t budgetMiB = snap.BudgetBytes / (1024ULL * 1024ULL);
    LogPrewarmMilestone(
        " [PRELOAD_TELEMETRY] outcome=%s map='%s' preloadElapsedMs=%llu warmupEnabled=%d warmupState=%s driveType=%s onBattery=%d files=%u faultedMiB=%llu budgetMiB=%llu warmupElapsedMs=%.1f warmupPeakFrameMs=%.2f warmupHitches=%llu",
        (outcome && outcome[0]) ? outcome : "COMPLETE",
        (mapName && mapName[0]) ? mapName : "caldavar",
        static_cast<unsigned long long>(preloadElapsedMs),
        snap.WarmupEnabled ? 1 : 0,
        snap.WarmupState,
        snap.DriveType,
        snap.OnBatteryOrSaver ? 1 : 0,
        snap.PostIntroFiles,
        static_cast<unsigned long long>(faultedMiB),
        static_cast<unsigned long long>(budgetMiB),
        snap.WarmupElapsedMs,
        snap.PeakFrameMs,
        static_cast<unsigned long long>(snap.HitchCount));
}

void EnqueueHeroForPrewarming(const std::string& heroName, const std::string& avatarKey) {
    if (!g_prewarmerRunning.load(std::memory_order_acquire) || !g_prewarmerEnabled.load(std::memory_order_acquire)) return;

    EnterCriticalSection(&g_prewarmLock);
    g_jobQueue.push_back({ PrewarmJobType::Hero, heroName, avatarKey });
    LeaveCriticalSection(&g_prewarmLock);

    SetEvent(g_hPrewarmEvent);
}

void EnqueueItemForPrewarming(const std::string& itemCode) {
    if (!g_prewarmerRunning.load(std::memory_order_acquire) || !g_prewarmerEnabled.load(std::memory_order_acquire)) return;

    EnterCriticalSection(&g_prewarmLock);
    g_jobQueue.push_back({ PrewarmJobType::Item, itemCode, "" });
    LeaveCriticalSection(&g_prewarmLock);

    SetEvent(g_hPrewarmEvent);
}

void EnqueueMapForPrewarming(const std::string& mapName) {
    if (!g_prewarmerRunning.load(std::memory_order_acquire) || !g_prewarmerEnabled.load(std::memory_order_acquire)) return;

    EnterCriticalSection(&g_prewarmLock);
    g_jobQueue.push_back({ PrewarmJobType::Map, mapName, "" });
    LeaveCriticalSection(&g_prewarmLock);

    SetEvent(g_hPrewarmEvent);
}

void PrewarmCommonAssets() {
    if (!g_prewarmerRunning.load(std::memory_order_acquire) || !g_prewarmerEnabled.load(std::memory_order_acquire)) return;

    EnterCriticalSection(&g_prewarmLock);
    g_jobQueue.push_back({ PrewarmJobType::Common, "", "" });
    LeaveCriticalSection(&g_prewarmLock);

    SetEvent(g_hPrewarmEvent);
}

void PrewarmCompetitiveItemPool() {
    static const char* s_commonItems[] = {
        "Item_PortalKey", "Item_Immunity", "Item_Intelligence7", "Item_PushStaff",
        "Item_Stealth", "Item_Nuke", "Item_PostHaste", "Item_BarbedArmor",
        "Item_Shield2", "Item_Silence", "Item_RestorationStone", "Item_SavageMace",
        "Item_Critical1", "Item_Lightning2", "Item_ManaBurn2", "Item_Evasion",
        "Item_FrostfieldPlate", "Item_BehemothsHeart", "Item_ElderParasite", "Item_Astrolabe"
    };

    LogPrewarm(" [ASSET_PREWARM] Enqueuing competitive item pool (%zu items)...",
        sizeof(s_commonItems) / sizeof(s_commonItems[0]));

    for (const char* itemCode : s_commonItems) {
        EnqueueItemForPrewarming(itemCode);
    }
}

PrewarmStats GetAssetPrewarmStats() {
    return {
        g_statFiles.load(std::memory_order_relaxed),
        g_statBytes.load(std::memory_order_relaxed),
        g_statDurationMs.load(std::memory_order_relaxed),
        g_statHeroes.load(std::memory_order_relaxed),
        g_statItems.load(std::memory_order_relaxed),
        g_statMaps.load(std::memory_order_relaxed)
    };
}

bool IsPrewarmingActive() {
    return g_isPrewarmingActive.load(std::memory_order_relaxed);
}

void SetAssetPrewarmerEnabled(bool enabled) {
    const bool prev = g_prewarmerEnabled.exchange(enabled, std::memory_order_acq_rel);
    if (g_hPrewarmEvent) {
        SetEvent(g_hPrewarmEvent);
    }
    if (prev != enabled) {
        LogPrewarmMilestone(" [ASSET_PREWARM] Prewarmer hook group toggled: %s.", enabled ? "ENABLED" : "PAUSED");
    }
}

bool IsAssetPrewarmerEnabled() {
    return g_prewarmerEnabled.load(std::memory_order_acquire);
}

void SetPrewarmerDriveTypeForTesting(const char* driveTypeOverride) noexcept {
    if (driveTypeOverride && driveTypeOverride[0] != '\0') {
        strncpy_s(g_driveTypeTestOverride, sizeof(g_driveTypeTestOverride), driveTypeOverride, _TRUNCATE);
        strncpy_s(g_cachedDriveType, sizeof(g_cachedDriveType), driveTypeOverride, _TRUNCATE);
    } else {
        g_driveTypeTestOverride[0] = '\0';
        if (!g_gameRootDir.empty()) {
            DetectDriveMediaType(g_gameRootDir);
        }
    }
}

void SetForceSsdPrewarmForTesting(int forceMode) noexcept {
    g_forceSsdPrewarmTestOverride.store(forceMode, std::memory_order_release);
}

void SetPrewarmerGameRootForTesting(const wchar_t* rootDirOverride) noexcept {
    if (rootDirOverride && rootDirOverride[0] != L'\0') {
        g_gameRootTestOverride = rootDirOverride;
    } else {
        g_gameRootTestOverride.clear();
    }
}

} // namespace Kongor

