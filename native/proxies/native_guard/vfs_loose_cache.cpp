// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "vfs_loose_cache.h"
#include "runtime_patch_controller.h"
#include "kongor_sstream.h"
#include <atomic>
#include <chrono>
#include <cerrno>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <memory>
#include <mutex>
#include <vector>

namespace fs = std::filesystem;

namespace Kongor {

namespace {

static constexpr size_t kMaxNormPathChars = 1024;
static constexpr uint32_t kMinTableCapacity = 4096; // Power of 2

struct HashEntry128 {
    uint64_t h1 = 0; // 0 == empty slot
    uint64_t h2 = 0;
};

struct IndexedRootPrefix {
    wchar_t prefix[kMaxNormPathChars] = {0};
    size_t  len = 0;
    char    displayLabel[64] = {0}; // Privacy-clean label (e.g. "game", "ClientPackageFiles")
};

struct LooseCacheSnapshot {
    wchar_t                       normGameRoot[kMaxNormPathChars] = {0};
    size_t                        normGameRootLen = 0;
    std::vector<IndexedRootPrefix> roots;
    std::vector<HashEntry128>     table;
    uint32_t                      tableMask = 0;
    uint32_t                      entryCount = 0;
    float                         buildDurationMs = 0.0f;
};

static std::atomic<const LooseCacheSnapshot*> g_activeSnapshot{nullptr};
static std::mutex                             g_snapshotLifecycleMutex;
static std::vector<std::unique_ptr<LooseCacheSnapshot>> g_retiredSnapshots;
static std::wstring                           g_configuredGameRoot;
static wchar_t                                g_logFilePath[MAX_PATH] = {0};

static std::atomic<bool>     g_cacheEnabled{true};
static std::atomic<bool>     g_workerRunning{false};
static std::atomic<bool>     g_rebuildPending{false};
static std::atomic<bool>     g_summaryFlushed{false};
static HANDLE                g_hRebuildEvent = NULL;
static HANDLE                g_hWorkerThread = NULL;

static std::atomic<uint64_t> g_statTotalCalls{0};
static std::atomic<uint64_t> g_statFastNegativeMisses{0};
static std::atomic<uint64_t> g_statPositivePassThroughs{0};
static std::atomic<uint64_t> g_statBypassPassThroughs{0};

static inline HashEntry128 HashNormalizedPath128(const wchar_t* s, size_t len) noexcept {
    uint64_t h1 = 14695981039346656037ULL;
    uint64_t h2 = 0x9E3779B97F4A7C15ULL;
    for (size_t i = 0; i < len; ++i) {
        const uint64_t ch = static_cast<uint64_t>(static_cast<uint16_t>(s[i]));
        h1 ^= ch;
        h1 *= 1099511628211ULL;
        h2 ^= (ch + 0x9E3779B97F4A7C15ULL + (h2 << 6) + (h2 >> 2));
    }
    if (h1 == 0) {
        h1 = 1ULL; // Reserve 0 as empty sentinel
    }
    return { h1, h2 };
}

static void InsertIntoSnapshotTable(LooseCacheSnapshot& snap, const wchar_t* normPath, size_t normLen) {
    if (snap.table.empty() || normLen == 0) return;
    const HashEntry128 key = HashNormalizedPath128(normPath, normLen);
    uint32_t idx = static_cast<uint32_t>(key.h1) & snap.tableMask;
    for (size_t probe = 0; probe < snap.table.size(); ++probe) {
        HashEntry128& slot = snap.table[idx];
        if (slot.h1 == 0) {
            slot = key;
            ++snap.entryCount;
            return;
        }
        if (slot.h1 == key.h1 && slot.h2 == key.h2) {
            return;
        }
        idx = (idx + 1U) & snap.tableMask;
    }
}

static inline bool LookupInSnapshotTable(const LooseCacheSnapshot& snap, const wchar_t* normPath, size_t normLen) noexcept {
    if (snap.table.empty() || normLen == 0) return true; // Fail-open to real _waccess if empty
    const HashEntry128 key = HashNormalizedPath128(normPath, normLen);
    uint32_t idx = static_cast<uint32_t>(key.h1) & snap.tableMask;
    for (size_t probe = 0; probe < snap.table.size(); ++probe) {
        const HashEntry128& slot = snap.table[idx];
        if (slot.h1 == 0) {
            return false;
        }
        if (slot.h1 == key.h1 && slot.h2 == key.h2) {
            return true;
        }
        idx = (idx + 1U) & snap.tableMask;
    }
    return true;
}

static void AppendLockFreeLogLine(const char* text, size_t textLen) noexcept {
    if (!text || textLen == 0 || g_logFilePath[0] == L'\0') return;
    HANDLE hFile = ::CreateFileW(
        g_logFilePath,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        ::WriteFile(hFile, text, static_cast<DWORD>(textLen), &written, NULL);
        ::CloseHandle(hFile);
    }
}

static void LogLooseCacheFmt(const char* fmt, ...) noexcept {
    static LogBurstLimiter s_logBurstLimiter(5000ULL, 16U);
    uint64_t suppressedCount = 0;
    uint64_t prevWindowMs = 5000ULL;
    if (!s_logBurstLimiter.TryAcquireNow(&suppressedCount, &prevWindowMs)) {
        return;
    }

    char msg[768] = {0};
    va_list args;
    va_start(args, fmt);
    vsnprintf(msg, sizeof(msg) - 1, fmt, args);
    va_end(args);

    GetGlobalBinaryTelemetryRing().Record(0x56465301U, "vfs_cache", suppressedCount, 0, msg);

    SYSTEMTIME st{};
    ::GetLocalTime(&st);
    if (suppressedCount > 0) {
        char supLine[256] = {0};
        int sn = snprintf(
            supLine, sizeof(supLine),
            "[%02u:%02u:%02u.%03u] [VFS_LOOSE_CACHE] [suppressed %llu events in %.1fs]\r\n",
            static_cast<unsigned>(st.wHour),
            static_cast<unsigned>(st.wMinute),
            static_cast<unsigned>(st.wSecond),
            static_cast<unsigned>(st.wMilliseconds),
            static_cast<unsigned long long>(suppressedCount),
            static_cast<double>(prevWindowMs) / 1000.0);
        if (sn > 0) {
            ::OutputDebugStringA(supLine);
            AppendLockFreeLogLine(supLine, static_cast<size_t>((sn < (int)sizeof(supLine)) ? sn : ((int)sizeof(supLine) - 1)));
        }
    }

    char line[896] = {0};
    int n = snprintf(
        line, sizeof(line),
        "[%02u:%02u:%02u.%03u] [VFS_LOOSE_CACHE] %s\r\n",
        static_cast<unsigned>(st.wHour),
        static_cast<unsigned>(st.wMinute),
        static_cast<unsigned>(st.wSecond),
        static_cast<unsigned>(st.wMilliseconds),
        msg);
    if (n > 0) {
        ::OutputDebugStringA(line);
        AppendLockFreeLogLine(line, static_cast<size_t>((n < (int)sizeof(line)) ? n : ((int)sizeof(line) - 1)));
    }
}

// Enumerates all files and subdirectories under absDirW using Win32 FindFirstFileExW (FindExInfoBasic + LARGE_FETCH).
// Returns false if any directory scan fails unexpectedly, so callers never publish an incomplete root prefix.
static bool CollectRootEntriesWin32(const std::wstring& absDirW, std::vector<std::wstring>& outNormPaths) {
    DWORD rootAttrs = ::GetFileAttributesW(absDirW.c_str());
    if (rootAttrs == INVALID_FILE_ATTRIBUTES || (rootAttrs & FILE_ATTRIBUTE_DIRECTORY) == 0) {
        return false;
    }

    wchar_t normRoot[kMaxNormPathChars] = {0};
    size_t normRootLen = 0;
    if (!NormalizePathForLooseCacheW(absDirW.c_str(), normRoot, kMaxNormPathChars, &normRootLen) || normRootLen == 0) {
        return false;
    }
    outNormPaths.emplace_back(normRoot, normRootLen);

    std::vector<std::wstring> dirStack;
    dirStack.push_back(absDirW);

    while (!dirStack.empty()) {
        std::wstring curDir = std::move(dirStack.back());
        dirStack.pop_back();

        std::wstring pattern = curDir;
        if (!pattern.empty() && pattern.back() != L'\\' && pattern.back() != L'/') {
            pattern.push_back(L'\\');
        }
        pattern.push_back(L'*');

        WIN32_FIND_DATAW fd{};
        HANDLE hFind = ::FindFirstFileExW(
            pattern.c_str(),
            FindExInfoBasic,
            &fd,
            FindExSearchNameMatch,
            NULL,
            FIND_FIRST_EX_LARGE_FETCH);
        if (hFind == INVALID_HANDLE_VALUE) {
            DWORD err = ::GetLastError();
            if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND || err == ERROR_ACCESS_DENIED) {
                continue;
            }
            return false;
        }

        do {
            const wchar_t* name = fd.cFileName;
            if (name[0] == L'.' && (name[1] == L'\0' || (name[1] == L'.' && name[2] == L'\0'))) {
                continue;
            }

            std::wstring childFull = curDir;
            if (!childFull.empty() && childFull.back() != L'\\' && childFull.back() != L'/') {
                childFull.push_back(L'\\');
            }
            childFull.append(name);

            wchar_t normChild[kMaxNormPathChars] = {0};
            size_t normChildLen = 0;
            if (NormalizePathForLooseCacheW(childFull.c_str(), normChild, kMaxNormPathChars, &normChildLen) && normChildLen > 0) {
                outNormPaths.emplace_back(normChild, normChildLen);
            }

            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
                (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0) {
                dirStack.push_back(std::move(childFull));
            }
        } while (::FindNextFileW(hFind, &fd));

        const DWORD endErr = ::GetLastError();
        ::FindClose(hFind);
        if (endErr != ERROR_NO_MORE_FILES) {
            return false;
        }
    }

    return true;
}

static bool IsHardExcludedDynamicSubpath(const wchar_t* normPath, size_t normLen, const wchar_t* normGameRoot, size_t normGameRootLen) noexcept {
    if (normGameRootLen == 0 || normLen <= normGameRootLen) return false;
    if (wcsncmp(normPath, normGameRoot, normGameRootLen) != 0 || normPath[normGameRootLen] != L'/') {
        return false;
    }
    const wchar_t* rel = normPath + normGameRootLen + 1;
    const size_t relLen = normLen - (normGameRootLen + 1);
    auto startsWithDir = [&](const wchar_t* dirLit, size_t dirLen) noexcept -> bool {
        return relLen >= dirLen &&
               wcsncmp(rel, dirLit, dirLen) == 0 &&
               (rel[dirLen] == L'/' || rel[dirLen] == L'\0');
    };
    return startsWithDir(L"override", 8) ||
           startsWithDir(L"userdata", 8) ||
           startsWithDir(L"kongor/updates", 14) ||
           startsWithDir(L"kongor/configuration", 20) ||
           startsWithDir(L"logs", 4);
}

static void BuildAndPublishSnapshot(const std::wstring& gameRootInput) {
    const auto t0 = std::chrono::high_resolution_clock::now();

    wchar_t fullRootBuf[kMaxNormPathChars] = {0};
    DWORD fullLen = ::GetFullPathNameW(gameRootInput.c_str(), kMaxNormPathChars, fullRootBuf, NULL);
    if (fullLen == 0 || fullLen >= kMaxNormPathChars) {
        LogLooseCacheFmt("Skipped index build: failed to resolve game root directory.");
        return;
    }

    auto snap = std::make_unique<LooseCacheSnapshot>();
    if (!NormalizePathForLooseCacheW(fullRootBuf, snap->normGameRoot, kMaxNormPathChars, &snap->normGameRootLen) ||
        snap->normGameRootLen == 0) {
        LogLooseCacheFmt("Skipped index build: game root path contains unsupported characters.");
        return;
    }

    const fs::path rootFs(fullRootBuf);
    const fs::path parentFs = rootFs.parent_path();

    struct CandidateRoot {
        fs::path    absPath;
        const char* label;
    };

    const CandidateRoot candidates[] = {
        { rootFs / L"game",                  "game/" },
        { rootFs / L"base",                  "base/" },
        { rootFs / L"ClientPackageFiles",    "ClientPackageFiles/" },
        { parentFs / L"shared",              "../shared/" },
        { rootFs / L"KONGOR" / L"extensions","KONGOR/extensions/" }
    };

    std::vector<std::wstring> allEntries;
    allEntries.reserve(49152);

    char indexedSummary[256] = {0};
    size_t summaryPos = 0;

    for (const auto& cand : candidates) {
        std::error_code ec;
        if (!fs::exists(cand.absPath, ec) || !fs::is_directory(cand.absPath, ec)) {
            continue;
        }

        const size_t beforeCount = allEntries.size();
        if (!CollectRootEntriesWin32(cand.absPath.wstring(), allEntries)) {
            allEntries.resize(beforeCount);
            LogLooseCacheFmt("WARNING: Partial scan aborted for '%s'; root left on live Win32 pass-through.", cand.label);
            continue;
        }

        IndexedRootPrefix rp{};
        if (NormalizePathForLooseCacheW(cand.absPath.c_str(), rp.prefix, kMaxNormPathChars, &rp.len) && rp.len > 0) {
            strncpy_s(rp.displayLabel, sizeof(rp.displayLabel), cand.label, _TRUNCATE);
            snap->roots.push_back(rp);

            const size_t rootEntries = allEntries.size() - beforeCount;
            int w = snprintf(
                indexedSummary + summaryPos,
                sizeof(indexedSummary) - summaryPos,
                "%s%s(%zu)",
                (summaryPos > 0) ? ", " : "",
                cand.label,
                rootEntries);
            if (w > 0 && summaryPos + static_cast<size_t>(w) < sizeof(indexedSummary)) {
                summaryPos += static_cast<size_t>(w);
            }
        }
    }

    // Size open-addressed table to >= 3x entryCount (load factor <= 0.33 for ~1.1 average probes)
    uint32_t cap = kMinTableCapacity;
    const size_t targetMinCap = (allEntries.size() * 3U) + 64U;
    while (cap < targetMinCap && cap < (1U << 22)) {
        cap <<= 1U;
    }
    snap->table.resize(cap);
    snap->tableMask = cap - 1U;

    for (const std::wstring& normEntry : allEntries) {
        InsertIntoSnapshotTable(*snap, normEntry.c_str(), normEntry.size());
    }

    const auto t1 = std::chrono::high_resolution_clock::now();
    snap->buildDurationMs = std::chrono::duration<float, std::milli>(t1 - t0).count();

    const uint32_t publishedEntries = snap->entryCount;
    const uint32_t publishedRoots   = static_cast<uint32_t>(snap->roots.size());
    const float    buildMs          = snap->buildDurationMs;

    {
        std::lock_guard<std::mutex> lock(g_snapshotLifecycleMutex);
        const LooseCacheSnapshot* rawPtr = snap.get();
        g_retiredSnapshots.push_back(std::move(snap));
        g_activeSnapshot.store(rawPtr, std::memory_order_release);
    }

    LogLooseCacheFmt(
        "Published read-only VFS loose-file snapshot: roots=%u [%s], entries=%u, tableSlots=%u, buildMs=%.2f (bypassed: override/, userdata/, KONGOR/updates/).",
        publishedRoots,
        indexedSummary[0] ? indexedSummary : "none",
        publishedEntries,
        cap,
        buildMs);
}

static DWORD WINAPI LooseCacheWorkerProc(LPVOID) {
    while (g_workerRunning.load(std::memory_order_acquire)) {
        if (g_rebuildPending.exchange(false, std::memory_order_acq_rel)) {
            std::wstring rootCopy;
            {
                std::lock_guard<std::mutex> lock(g_snapshotLifecycleMutex);
                rootCopy = g_configuredGameRoot;
            }
            if (!rootCopy.empty()) {
                BuildAndPublishSnapshot(rootCopy);
            }
        }
        if (!g_workerRunning.load(std::memory_order_acquire)) break;
        if (g_hRebuildEvent) {
            ::WaitForSingleObject(g_hRebuildEvent, 1000);
        } else {
            ::Sleep(250);
        }
    }
    return 0;
}

static std::wstring DetectClientGameRoot() {
    wchar_t exePath[MAX_PATH] = {0};
    if (::GetModuleFileNameW(NULL, exePath, MAX_PATH) > 0) {
        fs::path exeDir = fs::path(exePath).parent_path();
        std::error_code ec;
        if (fs::exists(exeDir / L"game", ec)) {
            return exeDir.wstring();
        }
        if (fs::exists(exeDir.parent_path() / L"game", ec)) {
            return exeDir.parent_path().wstring();
        }
    }
    std::error_code ec;
    fs::path cwd = fs::current_path(ec);
    if (!cwd.empty() && fs::exists(cwd / L"game", ec)) {
        return cwd.wstring();
    }
    return L"";
}

} // anonymous namespace

bool NormalizePathForLooseCacheW(const wchar_t* rawPath, wchar_t* outBuf, size_t outCap, size_t* outLen) noexcept {
    if (outLen) *outLen = 0;
    if (!rawPath || !outBuf || outCap < 4) return false;
    if (rawPath[0] == L'\0') return false;

    // Reject UNC / Win32 device namespaces (\\?\, \\.\, //) and wildcards up front
    if ((rawPath[0] == L'\\' || rawPath[0] == L'/') && (rawPath[1] == L'\\' || rawPath[1] == L'/')) {
        return false;
    }

    size_t segStarts[128];
    size_t segCount = 0;
    size_t writePos = 0;
    bool hasDrivePrefix = false;

    size_t i = 0;
    if (((rawPath[0] >= L'A' && rawPath[0] <= L'Z') || (rawPath[0] >= L'a' && rawPath[0] <= L'z')) &&
        rawPath[1] == L':') {
        wchar_t driveLower = (rawPath[0] >= L'A' && rawPath[0] <= L'Z')
            ? static_cast<wchar_t>(rawPath[0] + (L'a' - L'A'))
            : rawPath[0];
        outBuf[0] = driveLower;
        outBuf[1] = L':';
        writePos = 2;
        i = 2;
        hasDrivePrefix = true;
        if (rawPath[i] == L'\\' || rawPath[i] == L'/') {
            outBuf[writePos++] = L'/';
            while (rawPath[i] == L'\\' || rawPath[i] == L'/') {
                ++i;
            }
        }
    } else if (rawPath[0] == L'\\' || rawPath[0] == L'/') {
        return false; // Ambiguous drive-relative root path; let real _waccess resolve it
    }

    while (rawPath[i] != L'\0') {
        if (i >= kMaxNormPathChars) return false;

        size_t segBegin = i;
        while (rawPath[i] != L'\0' && rawPath[i] != L'\\' && rawPath[i] != L'/') {
            const wchar_t ch = rawPath[i];
            if (ch < 0x20 || ch >= 0x80 || ch == L'*' || ch == L'?' || ch == L'"' || ch == L'<' || ch == L'>' || ch == L'|' || ch == L':') {
                return false;
            }
            ++i;
        }
        const size_t segLen = i - segBegin;
        while (rawPath[i] == L'\\' || rawPath[i] == L'/') {
            ++i;
        }

        if (segLen == 0 || (segLen == 1 && rawPath[segBegin] == L'.')) {
            continue;
        }
        if (segLen == 2 && rawPath[segBegin] == L'.' && rawPath[segBegin + 1] == L'.') {
            if (segCount == 0) {
                return false; // Cannot pop above root; pass through to real _waccess
            }
            writePos = segStarts[--segCount];
            continue;
        }

        if (segCount >= (sizeof(segStarts) / sizeof(segStarts[0]))) {
            return false;
        }
        segStarts[segCount++] = writePos;

        const bool needSlash = (writePos > 0 && outBuf[writePos - 1] != L'/');
        if (writePos + (needSlash ? 1U : 0U) + segLen + 1U >= outCap) {
            return false;
        }
        if (needSlash) {
            outBuf[writePos++] = L'/';
        }
        for (size_t k = 0; k < segLen; ++k) {
            wchar_t c = rawPath[segBegin + k];
            if (c >= L'A' && c <= L'Z') {
                c = static_cast<wchar_t>(c + (L'a' - L'A'));
            }
            outBuf[writePos++] = c;
        }
    }

    if (writePos == 0 || (hasDrivePrefix && writePos <= 3 && segCount == 0)) {
        return false;
    }

    outBuf[writePos] = L'\0';
    if (outLen) *outLen = writePos;
    return true;
}

int EvaluateVfsLooseWaccess(const wchar_t* path, int mode, PfnWaccess realWaccess) noexcept {
    const PfnWaccess fallbackFn = realWaccess ? realWaccess : &::_waccess;
    g_statTotalCalls.fetch_add(1, std::memory_order_relaxed);

    // Only intercept F_OK (mode == 0) existence probes when enabled and snapshot is published
    if (!path || mode != 0 || !g_cacheEnabled.load(std::memory_order_acquire)) {
        g_statBypassPassThroughs.fetch_add(1, std::memory_order_relaxed);
        return fallbackFn(path, mode);
    }

    const LooseCacheSnapshot* snap = g_activeSnapshot.load(std::memory_order_acquire);
    if (!snap || snap->roots.empty()) {
        g_statBypassPassThroughs.fetch_add(1, std::memory_order_relaxed);
        return fallbackFn(path, mode);
    }

    wchar_t combinedBuf[kMaxNormPathChars] = {0};
    const wchar_t* candidateToNormalize = path;

    const bool hasDrive =
        (((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':');
    if (!hasDrive && path[0] != L'\\' && path[0] != L'/') {
        const size_t relLen = wcsnlen(path, kMaxNormPathChars);
        if (relLen == 0 || snap->normGameRootLen + 1U + relLen >= kMaxNormPathChars) {
            g_statBypassPassThroughs.fetch_add(1, std::memory_order_relaxed);
            return fallbackFn(path, mode);
        }
        wmemcpy(combinedBuf, snap->normGameRoot, snap->normGameRootLen);
        combinedBuf[snap->normGameRootLen] = L'/';
        wmemcpy(combinedBuf + snap->normGameRootLen + 1U, path, relLen);
        combinedBuf[snap->normGameRootLen + 1U + relLen] = L'\0';
        candidateToNormalize = combinedBuf;
    }

    wchar_t normPath[kMaxNormPathChars] = {0};
    size_t normLen = 0;
    if (!NormalizePathForLooseCacheW(candidateToNormalize, normPath, kMaxNormPathChars, &normLen) || normLen == 0) {
        g_statBypassPassThroughs.fetch_add(1, std::memory_order_relaxed);
        return fallbackFn(path, mode);
    }

    // Explicit defense-in-depth bypass for override/, userdata/, KONGOR/updates/, KONGOR/configuration/, logs/
    if (IsHardExcludedDynamicSubpath(normPath, normLen, snap->normGameRoot, snap->normGameRootLen)) {
        g_statBypassPassThroughs.fetch_add(1, std::memory_order_relaxed);
        return fallbackFn(path, mode);
    }

    bool inIndexedReadOnlyRoot = false;
    for (const IndexedRootPrefix& rp : snap->roots) {
        if (normLen >= rp.len &&
            wcsncmp(normPath, rp.prefix, rp.len) == 0 &&
            (normPath[rp.len] == L'/' || normPath[rp.len] == L'\0')) {
            inIndexedReadOnlyRoot = true;
            break;
        }
    }

    if (!inIndexedReadOnlyRoot) {
        g_statBypassPassThroughs.fetch_add(1, std::memory_order_relaxed);
        return fallbackFn(path, mode);
    }

    if (!LookupInSnapshotTable(*snap, normPath, normLen)) {
        g_statFastNegativeMisses.fetch_add(1, std::memory_order_relaxed);
        _set_errno(ENOENT);
        ::SetLastError(ERROR_FILE_NOT_FOUND);
        return -1;
    }

    // Positive match in table: delegate to real _waccess for 0% false-positive guarantee
    g_statPositivePassThroughs.fetch_add(1, std::memory_order_relaxed);
    return fallbackFn(path, mode);
}

void InitializeVfsLooseCache(const std::wstring& gameRootDir, bool synchronousForTest) {
    PatchController::RegisterGroupCallback("vfs_loose_cache", SetVfsLooseCacheEnabled, true);

    char disableEnv[16] = {0};
    if (::GetEnvironmentVariableA("KONGOR_DISABLE_VFS_LOOSE_CACHE", disableEnv, sizeof(disableEnv)) > 0 &&
        (disableEnv[0] == '1' || disableEnv[0] == 'y' || disableEnv[0] == 'Y' || disableEnv[0] == 't' || disableEnv[0] == 'T')) {
        g_cacheEnabled.store(false, std::memory_order_release);
    }

    std::wstring resolvedRoot = gameRootDir.empty() ? DetectClientGameRoot() : gameRootDir;
    if (resolvedRoot.empty()) {
        return;
    }

    {
        std::lock_guard<std::mutex> lock(g_snapshotLifecycleMutex);
        g_configuredGameRoot = resolvedRoot;
        g_summaryFlushed.store(false, std::memory_order_relaxed);
        if (!synchronousForTest && g_logFilePath[0] == L'\0') {
            std::error_code ec;
            fs::path logDir = fs::path(resolvedRoot) / L"userdata" / L"logs";
            fs::create_directories(logDir, ec);
            fs::path logPath = logDir / L"vfs_loose_cache.log";
            wcsncpy_s(g_logFilePath, MAX_PATH, logPath.c_str(), _TRUNCATE);
        }
    }

    if (synchronousForTest) {
        BuildAndPublishSnapshot(resolvedRoot);
        return;
    }

    if (g_workerRunning.exchange(true, std::memory_order_acq_rel)) {
        RebuildVfsLooseCacheAsync();
        return;
    }

    g_hRebuildEvent = ::CreateEventW(NULL, FALSE, FALSE, NULL);
    g_rebuildPending.store(true, std::memory_order_release);
    g_hWorkerThread = ::CreateThread(NULL, 0, LooseCacheWorkerProc, NULL, 0, NULL);
}

void RebuildVfsLooseCacheAsync() {
    g_rebuildPending.store(true, std::memory_order_release);
    if (g_hRebuildEvent) {
        ::SetEvent(g_hRebuildEvent);
    }
}

void LogVfsLooseCacheTelemetrySummary() {
    if (g_summaryFlushed.exchange(true, std::memory_order_acq_rel)) {
        return;
    }
    const uint64_t total  = g_statTotalCalls.load(std::memory_order_relaxed);
    const uint64_t misses = g_statFastNegativeMisses.load(std::memory_order_relaxed);
    const uint64_t pos    = g_statPositivePassThroughs.load(std::memory_order_relaxed);
    const uint64_t bypass = g_statBypassPassThroughs.load(std::memory_order_relaxed);
    const LooseCacheSnapshot* snap = g_activeSnapshot.load(std::memory_order_acquire);

    SYSTEMTIME st{};
    ::GetLocalTime(&st);
    char buf[512] = {0};
    int n = snprintf(
        buf, sizeof(buf),
        "[%02u:%02u:%02u.%03u] [VFS_LOOSE_CACHE] SHUTDOWN SUMMARY: totalCalls=%llu, fastNegativeMisses=%llu, positivePassThroughs=%llu, bypassPassThroughs=%llu, indexedEntries=%u, buildMs=%.2f\r\n",
        static_cast<unsigned>(st.wHour),
        static_cast<unsigned>(st.wMinute),
        static_cast<unsigned>(st.wSecond),
        static_cast<unsigned>(st.wMilliseconds),
        static_cast<unsigned long long>(total),
        static_cast<unsigned long long>(misses),
        static_cast<unsigned long long>(pos),
        static_cast<unsigned long long>(bypass),
        snap ? snap->entryCount : 0U,
        snap ? snap->buildDurationMs : 0.0f);
    if (n > 0) {
        ::OutputDebugStringA(buf);
        AppendLockFreeLogLine(buf, static_cast<size_t>((n < (int)sizeof(buf)) ? n : ((int)sizeof(buf) - 1)));
    }
}

void ShutdownVfsLooseCache(bool isProcessTermination) {
    LogVfsLooseCacheTelemetrySummary();
    g_workerRunning.store(false, std::memory_order_release);
    if (isProcessTermination) {
        return;
    }
    if (g_hRebuildEvent) {
        ::SetEvent(g_hRebuildEvent);
    }
    if (g_hWorkerThread) {
        ::WaitForSingleObject(g_hWorkerThread, 1000);
        ::CloseHandle(g_hWorkerThread);
        g_hWorkerThread = NULL;
    }
    if (g_hRebuildEvent) {
        ::CloseHandle(g_hRebuildEvent);
        g_hRebuildEvent = NULL;
    }
    {
        std::lock_guard<std::mutex> lock(g_snapshotLifecycleMutex);
        g_activeSnapshot.store(nullptr, std::memory_order_release);
        g_retiredSnapshots.clear();
    }
}

void SetVfsLooseCacheEnabled(bool enabled) {
    g_cacheEnabled.store(enabled, std::memory_order_release);
    LogLooseCacheFmt("Hook group 'vfs_loose_cache' toggled: %s.", enabled ? "ENABLED" : "DISABLED");
}

bool IsVfsLooseCacheEnabled() {
    return g_cacheEnabled.load(std::memory_order_acquire);
}

VfsLooseCacheStats GetVfsLooseCacheStats() {
    const LooseCacheSnapshot* snap = g_activeSnapshot.load(std::memory_order_acquire);
    return {
        g_statTotalCalls.load(std::memory_order_relaxed),
        g_statFastNegativeMisses.load(std::memory_order_relaxed),
        g_statPositivePassThroughs.load(std::memory_order_relaxed),
        g_statBypassPassThroughs.load(std::memory_order_relaxed),
        snap ? snap->entryCount : 0U,
        snap ? static_cast<uint32_t>(snap->roots.size()) : 0U,
        snap ? snap->buildDurationMs : 0.0f,
        snap != nullptr,
        g_cacheEnabled.load(std::memory_order_acquire)
    };
}

} // namespace Kongor
