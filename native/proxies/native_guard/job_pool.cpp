// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "job_pool.h"
#include "kongor_fast_divmod.h"
#include <windows.h>
#include <process.h>
#include <intrin.h>
#include <cstdint>
#include <vector>
#include <deque>
#include <atomic>
#include <chrono>
#include <string>
#include <fstream>
#include <iostream>
#include <algorithm>
#include <filesystem>
#include <mutex>

namespace Kongor {

struct InternalJob {
    JobTaskFn Task;
    void* Context;
    std::atomic<uint32_t>* CompletionCounter;
};

struct alignas(64) PersistentTileBatch {
    ParallelForFn Task;
    void* UserContext;
    uint32_t StartIndex;
    uint32_t EndIndex;
    uint32_t TileSize;
    uint32_t NumTiles;
    std::atomic<uint32_t> NextTile{ 0 };
    std::atomic<uint32_t> CompletedTiles{ 0 };
};

static std::vector<HANDLE> g_workerThreads;
static std::atomic<bool>   g_poolRunning{ false };
static SRWLOCK             g_queueLock = SRWLOCK_INIT;
static CONDITION_VARIABLE  g_queueCv = CONDITION_VARIABLE_INIT;
static CONDITION_VARIABLE  g_waitAllCv = CONDITION_VARIABLE_INIT;
static std::deque<InternalJob> g_jobQueue;

static std::atomic<uint32_t> g_activeWorkers{ 0 };
static std::atomic<uint32_t> g_pendingJobs{ 0 };
static std::atomic<uint64_t> g_totalJobsExecuted{ 0 };
static std::atomic<uint64_t> g_totalBatchesExecuted{ 0 };
static std::atomic<uint64_t> g_totalBatchDurationUs{ 0 };
static uint32_t            g_configuredWorkerCount{ 0 };

static bool IsJobPoolClientRoot(const std::filesystem::path& candidate) noexcept {
    std::error_code ec;
    return !candidate.empty() &&
           (std::filesystem::exists(candidate / "game" / "resources0.s2z", ec) ||
            std::filesystem::exists(candidate / "base" / "resources0.s2z", ec) ||
            std::filesystem::exists(candidate / "userdata", ec));
}

const wchar_t* KongorResolvePrewarmLogPathW(bool forceRefresh) {
    static SRWLOCK s_pathLock = SRWLOCK_INIT;
    static wchar_t s_resolvedLogPath[1024] = { 0 };
    static bool s_initialized = false;

    AcquireSRWLockExclusive(&s_pathLock);
    if (!s_initialized || forceRefresh) {
        s_resolvedLogPath[0] = L'\0';

        wchar_t customLog[1024] = { 0 };
        DWORD customLen = GetEnvironmentVariableW(L"KONGOR_NATIVE_GUARD_PREWARM_LOG", customLog, 1024);
        if (customLen > 0 && customLen < 1024) {
            wcscpy_s(s_resolvedLogPath, customLog);
        } else {
            wchar_t testMode[16] = { 0 };
            DWORD testLen = GetEnvironmentVariableW(L"KONGOR_NATIVE_GUARD_TEST_MODE", testMode, 16);
            if (testLen > 0 && wcscmp(testMode, L"1") == 0) {
                wchar_t tempDir[MAX_PATH] = { 0 };
                DWORD tempLen = GetTempPathW(MAX_PATH, tempDir);
                std::filesystem::path testLog = (tempLen > 0 && tempLen < MAX_PATH)
                    ? (std::filesystem::path(tempDir) / "kongor_native_guard_prewarm_test.log")
                    : std::filesystem::path("kongor_native_guard_prewarm_test.log");
                wcscpy_s(s_resolvedLogPath, testLog.wstring().c_str());
            } else {
                std::filesystem::path rootDir;
                HMODULE hSelf = NULL;
                if (GetModuleHandleExW(
                        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                        reinterpret_cast<LPCWSTR>(&KongorResolvePrewarmLogPathW),
                        &hSelf) && hSelf) {
                    wchar_t dllPath[MAX_PATH * 2] = { 0 };
                    if (GetModuleFileNameW(hSelf, dllPath, MAX_PATH * 2) > 0) {
                        std::filesystem::path dllDir = std::filesystem::path(dllPath).parent_path();
                        if (IsJobPoolClientRoot(dllDir)) {
                            rootDir = dllDir;
                        } else if (IsJobPoolClientRoot(dllDir.parent_path())) {
                            rootDir = dllDir.parent_path();
                        } else {
                            rootDir = dllDir;
                        }
                    }
                }
                if (rootDir.empty()) {
                    wchar_t exePath[MAX_PATH * 2] = { 0 };
                    if (GetModuleFileNameW(NULL, exePath, MAX_PATH * 2) > 0) {
                        std::filesystem::path exeDir = std::filesystem::path(exePath).parent_path();
                        rootDir = IsJobPoolClientRoot(exeDir.parent_path()) && !IsJobPoolClientRoot(exeDir)
                            ? exeDir.parent_path()
                            : exeDir;
                    }
                }
                std::filesystem::path logDir = rootDir / "userdata" / "logs";
                std::error_code ec;
                std::filesystem::create_directories(logDir, ec);
                std::filesystem::path finalPath = std::filesystem::exists(logDir, ec)
                    ? (logDir / "native_guard_prewarm.log")
                    : (rootDir / "native_guard_prewarm.log");
                wcscpy_s(s_resolvedLogPath, finalPath.wstring().c_str());
            }
        }
        s_initialized = true;
    }
    const wchar_t* result = s_resolvedLogPath;
    ReleaseSRWLockExclusive(&s_pathLock);
    return result;
}

void KongorWriteWillowmakerLog(const char* tag, const char* message) {
    static std::mutex s_wmMutex;
    std::lock_guard<std::mutex> lock(s_wmMutex);

    SYSTEMTIME st{};
    GetLocalTime(&st);

    char lineBuf[1400];
    int len = snprintf(
        lineBuf,
        sizeof(lineBuf),
        "[%04d-%02d-%02d %02d:%02d:%02d.%03d]%s%s%s%s\r\n",
        st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
        (tag && tag[0]) ? " [" : "",
        (tag && tag[0]) ? tag : "",
        (tag && tag[0]) ? "] " : " ",
        message ? message : "");
    if (len <= 0) {
        return;
    }
    const DWORD bytesToWrite = static_cast<DWORD>((std::min)(static_cast<size_t>(len), sizeof(lineBuf) - 1));

    const wchar_t* logPathW = KongorResolvePrewarmLogPathW(false);
    if (!logPathW || !logPathW[0]) {
        return;
    }

    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (GetFileAttributesExW(logPathW, GetFileExInfoStandard, &fad)) {
        const ULONGLONG fileSize = (static_cast<ULONGLONG>(fad.nFileSizeHigh) << 32) | fad.nFileSizeLow;
        if (fileSize > (10ULL * 1024ULL * 1024ULL)) {
            std::wstring bakPathW = std::wstring(logPathW) + L".bak";
            MoveFileExW(logPathW, bakPathW.c_str(), MOVEFILE_REPLACE_EXISTING);
        }
    }

    HANDLE hFile = CreateFileW(
        logPathW,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        (void)WriteFile(hFile, lineBuf, bytesToWrite, &written, NULL);
        CloseHandle(hFile);
    }
}

static void LogJobPool(const char* format, ...) {
    char buffer[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);

    OutputDebugStringA(buffer);

    KongorWriteWillowmakerLog("JOB_POOL", buffer);
}

static void SetKongorThreadName(const wchar_t* threadName) {
    if (!threadName) return;
    HMODULE hKernel32 = GetModuleHandleW(L"kernel32.dll");
    if (!hKernel32) return;

    typedef HRESULT(WINAPI* PFN_SetThreadDescription)(HANDLE hThread, PCWSTR lpThreadDescription);
    PFN_SetThreadDescription pfnSetThreadDesc =
        reinterpret_cast<PFN_SetThreadDescription>(GetProcAddress(hKernel32, "SetThreadDescription"));
    if (pfnSetThreadDesc) {
        pfnSetThreadDesc(GetCurrentThread(), threadName);
    }
}

static DWORD WINAPI WorkerThreadProc(LPVOID lpParam) {
    uint32_t workerIndex = (uint32_t)(uintptr_t)lpParam;

    wchar_t workerName[64];
    swprintf_s(workerName, _countof(workerName), L"Kongor_JobWorker_%u", workerIndex);
    SetKongorThreadName(workerName);

    // Signal mimalloc allocator that this thread is part of a dedicated threadpool
    HMODULE hMimalloc = GetModuleHandleW(L"mimalloc-override.dll");
    if (!hMimalloc) hMimalloc = GetModuleHandleW(L"mimalloc.dll");
    if (hMimalloc) {
        typedef void (*PFN_mi_thread_set_in_threadpool)(void);
        PFN_mi_thread_set_in_threadpool pfn = (PFN_mi_thread_set_in_threadpool)GetProcAddress(hMimalloc, "mi_thread_set_in_threadpool");
        if (pfn) pfn();
    }

    LogJobPool("Worker thread #%u online (ThreadId: %lu, Name: Kongor_JobWorker_%u).", workerIndex, GetCurrentThreadId(), workerIndex);

    while (g_poolRunning.load(std::memory_order_acquire)) {
        InternalJob job = {};
        bool hasJob = false;

        AcquireSRWLockExclusive(&g_queueLock);
        while (g_jobQueue.empty() && g_poolRunning.load(std::memory_order_acquire)) {
            SleepConditionVariableSRW(&g_queueCv, &g_queueLock, INFINITE, 0);
        }

        if (!g_jobQueue.empty()) {
            job = g_jobQueue.front();
            g_jobQueue.pop_front();
            hasJob = true;
        }
        ReleaseSRWLockExclusive(&g_queueLock);

        if (hasJob && job.Task) {
            g_activeWorkers.fetch_add(1, std::memory_order_relaxed);

            __try {
                job.Task(job.Context);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                LogJobPool("[ERROR] Worker #%u caught structured exception during job execution!", workerIndex);
            }

            g_activeWorkers.fetch_sub(1, std::memory_order_relaxed);
            g_totalJobsExecuted.fetch_add(1, std::memory_order_relaxed);

            if (job.CompletionCounter) {
                job.CompletionCounter->fetch_sub(1, std::memory_order_release);
            }

            uint32_t remaining = g_pendingJobs.fetch_sub(1, std::memory_order_relaxed);
            if (remaining <= 1) {
                WakeAllConditionVariable(&g_waitAllCv);
            }
        }
    }

    LogJobPool("Worker thread #%u exiting.", workerIndex);
    return 0;
}

static void EnsureWorkersStarted() {
    if (!g_workerThreads.empty()) return;
    AcquireSRWLockExclusive(&g_queueLock);
    if (g_workerThreads.empty() && g_configuredWorkerCount > 0) {
        g_workerThreads.resize(g_configuredWorkerCount);
        for (uint32_t i = 0; i < g_configuredWorkerCount; ++i) {
            HANDLE hThread = CreateThread(NULL, 0, WorkerThreadProc, (LPVOID)(uintptr_t)i, 0, NULL);
            if (hThread) {
                SetThreadPriority(hThread, THREAD_PRIORITY_NORMAL);
                g_workerThreads[i] = hThread;
            }
        }
        LogJobPool("Lazy Worker Job Pool started successfully (%u workers active, THREAD_PRIORITY_NORMAL).", g_configuredWorkerCount);
    }
    ReleaseSRWLockExclusive(&g_queueLock);
}

void JobPool::Initialize(uint32_t workerCount) {
    if (g_poolRunning.load(std::memory_order_acquire)) {
        return;
    }

    SYSTEM_INFO sysInfo;
    GetSystemInfo(&sysInfo);
    DWORD totalLogicalCores = sysInfo.dwNumberOfProcessors;

    if (workerCount == 0) {
        // Reserve Core 0 for main simulation loop, input, and OS scheduling
        workerCount = (totalLogicalCores > 1) ? (totalLogicalCores - 1) : 1;
        // Cap default worker pool at 16 threads for optimal cache locality
        if (workerCount > 16) workerCount = 16;
    }

    g_configuredWorkerCount = workerCount;
    g_poolRunning.store(true, std::memory_order_release);

    LogJobPool("Configured Multithreaded Worker Job Pool: %u workers planned on %u CPU cores (lazy startup).",
        workerCount, totalLogicalCores);
}

void JobPool::Shutdown() {
    if (!g_poolRunning.load(std::memory_order_acquire)) {
        return;
    }

    LogJobPool("Shutting down Worker Job Pool...");
    g_poolRunning.store(false, std::memory_order_release);

    // Wake all sleeping workers
    WakeAllConditionVariable(&g_queueCv);

    // Wait for all worker threads to cleanly exit
    for (HANDLE hThread : g_workerThreads) {
        if (hThread) {
            WaitForSingleObject(hThread, 1000);
            CloseHandle(hThread);
        }
    }
    g_workerThreads.clear();

    AcquireSRWLockExclusive(&g_queueLock);
    g_jobQueue.clear();
    ReleaseSRWLockExclusive(&g_queueLock);

    LogJobPool("Worker Job Pool shutdown cleanly. Total jobs executed: %llu.",
        (unsigned long long)g_totalJobsExecuted.load());
}

bool JobPool::IsInitialized() {
    return g_poolRunning.load(std::memory_order_acquire);
}

void JobPool::Dispatch(JobTaskFn task, void* context) {
    if (!task) return;

    if (!g_poolRunning.load(std::memory_order_acquire)) {
        task(context);
        return;
    }

    EnsureWorkersStarted();
    g_pendingJobs.fetch_add(1, std::memory_order_relaxed);

    AcquireSRWLockExclusive(&g_queueLock);
    g_jobQueue.push_back({ task, context, nullptr });
    ReleaseSRWLockExclusive(&g_queueLock);

    WakeConditionVariable(&g_queueCv);
}

static void ConsumePersistentTiles(PersistentTileBatch* batch) {
    if (!batch || !batch->Task) return;

    while (true) {
        uint32_t tileIdx = batch->NextTile.fetch_add(1, std::memory_order_relaxed);
        if (tileIdx >= batch->NumTiles) {
            break;
        }

        uint32_t tileStart = batch->StartIndex + (tileIdx * batch->TileSize);
        uint32_t tileEnd = (std::min)(batch->EndIndex, tileStart + batch->TileSize);

        for (uint32_t i = tileStart; i < tileEnd; ++i) {
            batch->Task(i, batch->UserContext);
        }

        batch->CompletedTiles.fetch_add(1, std::memory_order_release);
    }
}

static void PersistentTileWorkerRunner(void* ctx) {
    ConsumePersistentTiles(static_cast<PersistentTileBatch*>(ctx));
}

void JobPool::ParallelFor(uint32_t start, uint32_t end, uint32_t minBatchSize, ParallelForFn task, void* context) {
    if (!task || start >= end) {
        return;
    }

    uint32_t totalItems = end - start;

    // Single item or uninitialized pool executes immediately on caller thread
    if (totalItems == 1 || !g_poolRunning.load(std::memory_order_acquire) || g_configuredWorkerCount == 0) {
        for (uint32_t i = start; i < end; ++i) {
            task(i, context);
        }
        return;
    }

    auto startTime = std::chrono::high_resolution_clock::now();

    if (minBatchSize == 0) minBatchSize = 1;
    // Partition into up to 4x worker count fine-grained tiles for dynamic load balancing (#129, #130)
    uint32_t maxTiles = g_configuredWorkerCount * 4;
    FastDivmod maxTilesDivmod(maxTiles);
    uint32_t tileSize = (std::max)(minBatchSize, maxTilesDivmod.Div(totalItems + maxTiles - 1));
    FastDivmod tileSizeDivmod(tileSize);
    uint32_t numTiles = tileSizeDivmod.Div(totalItems + tileSize - 1);

    if (numTiles <= 1) {
        for (uint32_t i = start; i < end; ++i) {
            task(i, context);
        }
        return;
    }

    PersistentTileBatch batch;
    batch.Task = task;
    batch.UserContext = context;
    batch.StartIndex = start;
    batch.EndIndex = end;
    batch.TileSize = tileSize;
    batch.NumTiles = numTiles;
    batch.NextTile.store(0, std::memory_order_relaxed);
    batch.CompletedTiles.store(0, std::memory_order_relaxed);

    uint32_t workerSlots = (std::min)(g_configuredWorkerCount, numTiles - 1);
    if (workerSlots > 0) {
        EnsureWorkersStarted();
    }
    std::atomic<uint32_t> pendingWorkerJobs{ workerSlots };

    // Enqueue persistent tile consumers to worker threads (stack-allocated, 0 heap allocations)
    AcquireSRWLockExclusive(&g_queueLock);
    for (uint32_t w = 0; w < workerSlots; ++w) {
        g_pendingJobs.fetch_add(1, std::memory_order_relaxed);
        g_jobQueue.push_back({ PersistentTileWorkerRunner, &batch, &pendingWorkerJobs });
    }
    ReleaseSRWLockExclusive(&g_queueLock);

    // Wake workers to begin stealing tiles
    WakeAllConditionVariable(&g_queueCv);

    // Active caller-thread cooperative work-stealing: caller claims and executes tiles alongside workers (#130)
    ConsumePersistentTiles(&batch);

    // Once all tiles are claimed, reclaim any still-unpopped worker jobs for this batch so we never wait on sleeping threads
    if (pendingWorkerJobs.load(std::memory_order_acquire) > 0) {
        AcquireSRWLockExclusive(&g_queueLock);
        for (auto it = g_jobQueue.begin(); it != g_jobQueue.end(); ) {
            if (it->Context == &batch) {
                it = g_jobQueue.erase(it);
                pendingWorkerJobs.fetch_sub(1, std::memory_order_release);
                uint32_t rem = g_pendingJobs.fetch_sub(1, std::memory_order_relaxed);
                if (rem <= 1) {
                    WakeAllConditionVariable(&g_waitAllCv);
                }
            } else {
                ++it;
            }
        }
        ReleaseSRWLockExclusive(&g_queueLock);
    }

    // Spin-wait barrier until any worker currently inside PersistentTileWorkerRunner(&batch) finishes its last tile
    uint32_t spinCount = 0;
    while (pendingWorkerJobs.load(std::memory_order_acquire) > 0 ||
           batch.CompletedTiles.load(std::memory_order_acquire) < numTiles) {
        _mm_pause();
        spinCount++;
        if (spinCount > 1000) {
            SwitchToThread();
            spinCount = 0;
        }
    }

    auto endTime = std::chrono::high_resolution_clock::now();
    uint64_t durationUs = (uint64_t)std::chrono::duration_cast<std::chrono::microseconds>(endTime - startTime).count();

    g_totalBatchesExecuted.fetch_add(1, std::memory_order_relaxed);
    g_totalBatchDurationUs.fetch_add(durationUs, std::memory_order_relaxed);
}

void JobPool::WaitAll() {
    if (!g_poolRunning.load(std::memory_order_acquire)) {
        return;
    }

    AcquireSRWLockExclusive(&g_queueLock);
    while (g_pendingJobs.load(std::memory_order_relaxed) > 0 && g_poolRunning.load(std::memory_order_relaxed)) {
        SleepConditionVariableSRW(&g_waitAllCv, &g_queueLock, 100, 0);
    }
    ReleaseSRWLockExclusive(&g_queueLock);
}

JobPoolStats JobPool::GetStats() {
    uint64_t batches = g_totalBatchesExecuted.load(std::memory_order_relaxed);
    uint64_t totalUs = g_totalBatchDurationUs.load(std::memory_order_relaxed);
    float avgUs = (batches > 0) ? ((float)totalUs / (float)batches) : 0.0f;

    return {
        g_configuredWorkerCount,
        g_totalJobsExecuted.load(std::memory_order_relaxed),
        batches,
        avgUs,
        g_activeWorkers.load(std::memory_order_relaxed),
        g_poolRunning.load(std::memory_order_relaxed)
    };
}

void JobPool::ResetStats() {
    g_totalJobsExecuted.store(0, std::memory_order_relaxed);
    g_totalBatchesExecuted.store(0, std::memory_order_relaxed);
    g_totalBatchDurationUs.store(0, std::memory_order_relaxed);
}

} // namespace Kongor
