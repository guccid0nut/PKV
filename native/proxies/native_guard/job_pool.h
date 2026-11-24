// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>
#include <cstdint>
#include <functional>

namespace Kongor {

struct JobPoolStats {
    uint32_t WorkerCount;
    uint64_t TotalJobsExecuted;
    uint64_t TotalBatchesExecuted;
    float    AverageBatchDurationUs;
    uint32_t ActiveWorkers;
    bool     IsRunning;
};

typedef void (*JobTaskFn)(void* context);
typedef void (*ParallelForFn)(uint32_t index, void* context);

class JobPool {
public:
    // Initializes the worker job pool. If workerCount is 0, auto-detects available CPU cores
    // and spawns (CPU_CORES - 1) workers pinned to Cores 1..N-1 (leaving Core 0 for main thread).
    static void Initialize(uint32_t workerCount = 0);

    // Gracefully shuts down all worker threads and joins them.
    static void Shutdown();

    // Returns true if the job pool is currently active and initialized.
    static bool IsInitialized();

    // Enqueues a single fire-and-forget asynchronous task to the pool.
    static void Dispatch(JobTaskFn task, void* context);

    // Executes a parallel range [start, end) across all worker threads.
    // Blocks the calling thread until all parallel chunks in the range complete.
    static void ParallelFor(uint32_t start, uint32_t end, uint32_t minBatchSize, ParallelForFn task, void* context);

    // Waits for all queued jobs to finish execution.
    static void WaitAll();

    // Retrieves live telemetry and performance metrics from the job pool.
    static JobPoolStats GetStats();

    // Resets performance counters.
    static void ResetStats();
};

} // namespace Kongor
