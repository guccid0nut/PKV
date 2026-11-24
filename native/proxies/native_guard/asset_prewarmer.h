// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <string>
#include <vector>
#include <atomic>
#include <cstdint>

namespace Kongor {

struct PrewarmStats {
    uint32_t FilesPrewarmed;
    uint64_t BytesPrewarmed;
    float    DurationMs;
    uint32_t HeroesProcessed;
    uint32_t ItemsProcessed;
    uint32_t MapsProcessed;
};

// Initializes the background asset pre-warming engine
void InitializeAssetPrewarmer();

// Gracefully shuts down the background worker thread
void ShutdownAssetPrewarmer();

// Queues a specific hero and active avatar for background pre-warming
void EnqueueHeroForPrewarming(const std::string& heroName, const std::string& avatarKey = "");

// Queues an inventory item for pre-warming (e.g. "Item_PortalKey")
void EnqueueItemForPrewarming(const std::string& itemCode);

// Queues a target map for background pre-warming (e.g. "midwars" or "caldavar")
void EnqueueMapForPrewarming(const std::string& mapName);

// Pre-warms common shared assets (common UI fonts, textures, base archives)
void PrewarmCommonAssets();

// Triggers pre-warming of the competitive item pool (top active/passive combat items)
void PrewarmCompetitiveItemPool();

// Manually pre-warms a target file path from disk / VFS into OS page cache
bool PrewarmFile(const std::wstring& relativePath);

// Retrieves current live pre-warmer statistics
PrewarmStats GetAssetPrewarmStats();

// Returns true if pre-warming is currently in progress
bool IsPrewarmingActive();

// Pure eligibility predicate (> 8 GiB physical RAM and >= 3 GiB free RAM) exposed for deterministic unit tests
inline constexpr bool IsMemoryEligibleForBackgroundWarmup(uint64_t totalPhysBytes, uint64_t availPhysBytes) noexcept {
    constexpr uint64_t kGiB = 1024ULL * 1024ULL * 1024ULL;
    return (totalPhysBytes >= (10ULL * kGiB)) && (availPhysBytes >= (3ULL * kGiB));
}

// Computes hard cumulative PostIntro byte budget from initial snapshot of ullAvailPhys minus untouched headroom (default 2 GiB)
inline constexpr uint64_t ComputePostIntroWarmupBudgetBytes(
    uint64_t initialAvailPhysBytes,
    uint64_t reservedHeadroomBytes = 2ULL * 1024ULL * 1024ULL * 1024ULL) noexcept {
    return (initialAvailPhysBytes > reservedHeadroomBytes)
        ? (initialAvailPhysBytes - reservedHeadroomBytes)
        : 0ULL;
}

// Returns true when running on DC battery (ACLineStatus == 0) or Windows Battery Saver (SystemStatusFlag == 1)
inline constexpr bool ShouldCapWarmupToTier1OnPower(uint8_t acLineStatus, uint8_t systemStatusFlag) noexcept {
    return (acLineStatus == 0U) || (systemStatusFlag == 1U);
}

// Returns true when storage media is SSD (zero mechanical seek penalty) and KONGOR_FORCE_SSD_PREWARM is not set,
// preserving ~6.9 GiB of OS Standby RAM cache on NVMe/SSD PCs while keeping full warmup automatic on HDD/UNKNOWN.
inline constexpr bool ShouldSkipArchiveWarmupOnSsd(bool isSsdDrive, bool forceSsdPrewarm) noexcept {
    return isSsdDrive && !forceSsdPrewarm;
}

// Pure eligibility guard for queue-concurrent PreloadWorld: disables only on headless test runs,
// explicit KONGOR_DISABLE_BOOT_PRELOAD=1, or critical low free RAM (< 3.5 GiB) so 16 GB PCs with
// a browser open (~4.0-5.8 GiB free) are not silently excluded.
inline constexpr bool ShouldDisableBootWorldPreload(
    uint64_t availPhysBytes,
    bool envDisabled,
    bool isHeadless) noexcept {
    constexpr uint64_t kMinFreeBytes = (7ULL * 1024ULL * 1024ULL * 1024ULL) / 2ULL; // 3.5 GiB
    return isHeadless || envDisabled || (availPhysBytes < kMinFreeBytes);
}

// Writes a milestone line to the DLL-anchored native_guard_prewarm.log (<clientRoot>/userdata/logs/native_guard_prewarm.log)
void KongorWriteWillowmakerLog(const char* tag, const char* message);

// Resolves the DLL-anchored native prewarm telemetry log path (<clientRoot>/userdata/logs/native_guard_prewarm.log)
const wchar_t* KongorResolvePrewarmLogPathW(bool forceRefresh = false);

// Deterministic unit-test overrides for storage media detection ("SSD", "HDD", "UNKNOWN", or nullptr to clear),
// KONGOR_FORCE_SSD_PREWARM (-1 = read env, 0 = force off, 1 = force on), and hermetic scratch game root directory.
void SetPrewarmerDriveTypeForTesting(const char* driveTypeOverride) noexcept;
void SetForceSsdPrewarmForTesting(int forceMode) noexcept;
void SetPrewarmerGameRootForTesting(const wchar_t* rootDirOverride) noexcept;

struct PostIntroTelemetrySnapshot {
    bool        WarmupEnabled;
    const char* WarmupState;
    const char* DriveType;
    bool        OnBatteryOrSaver;
    uint32_t    PostIntroFiles;
    uint64_t    PostIntroFaultedBytes;
    uint64_t    BudgetBytes;
    float       WarmupElapsedMs;
    float       PeakFrameMs;
    uint64_t    HitchCount;
};

// Queries GlobalMemoryStatusEx and returns true when the machine passes the > 8 GiB RAM guard
bool IsSystemEligibleForBackgroundWarmup(uint64_t* outTotalMiB = nullptr, uint64_t* outAvailMiB = nullptr);

// Triggers post-Beauty-Empath background OS page-cache pre-warming (loose .entity/.interface + sharded .s2z + map)
bool TriggerPostIntroBackgroundWarmup(const std::string& mapName = "caldavar");

// Resumable low-I/O file pre-warmer used by PostIntro (pauses at exact 256 KiB offset during foreground busy; aborts on cancel/match/disable)
bool PrewarmFilePostIntro(const std::wstring& fullPath, uint64_t* ioRemainingBudgetBytes = nullptr);

// Lowers the calling thread's NT disk I/O priority (ThreadIoPriority = 22 -> IoPriorityLow = 1) while keeping ThreadPagePriority = Normal (5)
bool ApplyLowDiskIoPriorityToCurrentThread() noexcept;

// Signals that foreground PreloadWorld / match loading is active so background pre-faulting yields disk bandwidth immediately
void SetPrewarmerForegroundBusy(bool busy);
bool IsPrewarmerForegroundBusy();

// Signals whether client is connecting to / inside a match (clientState >= 2 or canonicalPhase >= 2); cancels PostIntro without disabling prewarm_hero
void SetPrewarmerMatchActive(bool matchActive);
bool IsPrewarmerMatchActive();

// Explicitly cancels PostIntro background warmup for the session (e.g. after Play-Now PreloadWorld completes or match starts)
void CancelPostIntroWarmup(const char* reason = "cancelled");
bool IsPostIntroWarmupCancelled();
bool IsPostIntroWarmupCompleted();

// Records per-frame CPU duration (microseconds) exclusively while PostIntro background warmup is actively running
void NotifyPrewarmerFrameTimeUs(uint32_t cpuFrameTimeUs) noexcept;
PostIntroTelemetrySnapshot GetPostIntroTelemetrySnapshot() noexcept;
void LogPlayNowPreloadTelemetry(const char* outcome, uint64_t preloadElapsedMs, const char* mapName);

// Enables or pauses background asset pre-warming ("prewarm" hook group toggle)
void SetAssetPrewarmerEnabled(bool enabled);
bool IsAssetPrewarmerEnabled();

} // namespace Kongor

