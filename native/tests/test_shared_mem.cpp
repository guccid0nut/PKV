// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file test_shared_mem.cpp
 * @brief Unit tests and multi-threaded stress benchmarks for kongor_shared_mem.h
 */

#include "kongor_shared_mem.h"
#include <aclapi.h>
#include <iostream>
#include <chrono>
#include <vector>
#include <thread>
#include <atomic>
#include <cassert>
#include <sstream>

// ============================================================================
// COMPILE-TIME STRUCT LAYOUT & CACHE LINE ASSERTIONS
// ============================================================================

static_assert(sizeof(FrameTelemetryRecord) == 64, "FrameTelemetryRecord must be exactly 64 bytes");
static_assert(sizeof(KongorTelemetryRingHeaderV2) == 192, "KongorTelemetryRingHeaderV2 must be 192 bytes (3 cache lines)");
static_assert(offsetof(KongorTelemetryRingHeaderV2, Magic) == 0, "Magic must be at byte 0 (Cache line 0)");
static_assert(offsetof(KongorTelemetryRingHeaderV2, LatestSequence) == 64, "LatestSequence must be at byte 64 (Cache line 1)");
static_assert(offsetof(KongorTelemetryRingHeaderV2, LatestRecord) == 128, "LatestRecord must be at byte 128 (Cache line 2)");

// ============================================================================
// TEST HARNESS
// ============================================================================

static void TestStructLayout() {
    std::cout << "[TEST] 1. Validating 192-byte 3-Cache-Line Structure Alignment...\n";
    assert(sizeof(FrameTelemetryRecord) == 64);
    assert(sizeof(KongorTelemetryRingHeaderV2) == 192);
    assert(offsetof(KongorTelemetryRingHeaderV2, LatestSequence) == 64);
    assert(offsetof(KongorTelemetryRingHeaderV2, GamePhase) == 84);
    assert(offsetof(KongorTelemetryRingHeaderV2, ActiveMatchId) == 88);
    assert(offsetof(KongorTelemetryRingHeaderV2, ConnectedPlayers) == 92);
    assert(offsetof(KongorTelemetryRingHeaderV2, LuaMemoryKb) == 96);
    assert(offsetof(KongorTelemetryRingHeaderV2, CurrentMap) == 100);
    assert(offsetof(KongorTelemetryRingHeaderV2, PlayerName) == 116);
    assert(offsetof(KongorTelemetryRingHeaderV2, LatestRecord) == 128);
    std::cout << "  -> Cache Line 0: V1 Header (64 bytes) [OK]\n";
    std::cout << "  -> Cache Line 1: Seqlock & Live Engine State (64 bytes) [OK]\n";
    std::cout << "  -> Cache Line 2: Atomic Latest Snapshot (64 bytes) [OK]\n";
}

static void TestSingleThreadedLifecycle() {
    std::cout << "[TEST] 2. Testing Single-Threaded Producer & Consumer Lifecycle...\n";

    std::ostringstream ss;
    ss << "Local\\KongorTestShm_" << GetCurrentProcessId();
    std::string mapName = ss.str();

    KongorSharedMemHandle producer;
    bool created = kongor_shm_create_producer(mapName.c_str(), 1024, &producer);
    assert(created && "Failed to create producer shared memory mapping");
    assert(producer.pHeader->Magic == KONGOR_SHM_MAGIC);
    assert(producer.pHeader->Version == KONGOR_SHM_VERSION_2);

    // Security assertion: Shared memory MUST carry a non-NULL owner-restricted DACL (never NULL DACL)
    PACL pDacl = nullptr;
    PSECURITY_DESCRIPTOR pSdCheck = nullptr;
    DWORD secErr = GetSecurityInfo(
        producer.hMap,
        SE_KERNEL_OBJECT,
        DACL_SECURITY_INFORMATION,
        nullptr,
        nullptr,
        &pDacl,
        nullptr,
        &pSdCheck);
    assert(secErr == ERROR_SUCCESS && "GetSecurityInfo must succeed on shared memory handle");
    assert(pDacl != nullptr && "Shared memory mapping MUST NOT use a NULL DACL");
    if (pSdCheck) {
        LocalFree(pSdCheck);
    }

    producer.pHeader->GamePhase = 3;
    producer.pHeader->ActiveMatchId = 777123;
    producer.pHeader->ConnectedPlayers = 10;
    producer.pHeader->LuaMemoryKb = 48250;
    strncpy_s(producer.pHeader->CurrentMap, sizeof(producer.pHeader->CurrentMap), "caldavar", _TRUNCATE);
    strncpy_s(producer.pHeader->PlayerName, sizeof(producer.pHeader->PlayerName), "Kongor", _TRUNCATE);

    KongorSharedMemHandle consumer;
    bool opened = kongor_shm_open_consumer(mapName.c_str(), &consumer);
    assert(opened && "Failed to open consumer shared memory mapping");
    assert(consumer.pHeader->Capacity == 1024);
    assert(consumer.pHeader->GamePhase == 3);
    assert(consumer.pHeader->ActiveMatchId == 777123);
    assert(consumer.pHeader->ConnectedPlayers == 10);
    assert(consumer.pHeader->LuaMemoryKb == 48250);
    assert(std::string(consumer.pHeader->CurrentMap) == "caldavar");
    assert(std::string(consumer.pHeader->PlayerName) == "Kongor");

    // Write a test frame
    FrameTelemetryRecord recWrite = {};
    recWrite.FrameIndex = 42;
    recWrite.TimestampQpc = 12345678;
    recWrite.CpuFrameTimeMs = 4.5f;
    recWrite.GpuFrameTimeMs = 3.8f;
    recWrite.InputLatencyMs = 1.2f;
    recWrite.DrawCallCount = 250;
    recWrite.ActiveEntities = 115;
    recWrite.ParticlesAlive = 420;
    recWrite.PingMicroseconds = 24000;
    recWrite.PacketLossPercent = 0.05f;
    recWrite.MemoryWorkingSetMb = 850;
    recWrite.AudioVoicesActive = 18;
    recWrite.ServerTick = 999;
    recWrite.ReservedFlags = KONGOR_VOICE_PTT_TEAM;

    kongor_shm_write_record(&producer, &recWrite);

    // Consumer reads
    FrameTelemetryRecord recRead = {};
    bool readSuccess = kongor_shm_read_latest(&consumer, &recRead);
    assert(readSuccess && "Consumer failed to read latest snapshot");

    assert(recRead.FrameIndex == 42);
    assert(recRead.TimestampQpc == 12345678);
    assert(recRead.CpuFrameTimeMs == 4.5f);
    assert(recRead.GpuFrameTimeMs == 3.8f);
    assert(recRead.DrawCallCount == 250);
    assert(recRead.ActiveEntities == 115);
    assert(recRead.ParticlesAlive == 420);
    assert(recRead.PingMicroseconds == 24000);
    assert(recRead.ServerTick == 999);
    assert(recRead.ReservedFlags == KONGOR_VOICE_PTT_TEAM);

    std::cout << "  -> Single-threaded read/write verified with exact field equality [OK]\n";

    kongor_shm_close(&consumer);
    kongor_shm_close(&producer);
}

static void TestMultiThreadedSeqlockStress() {
    std::cout << "[TEST] 3. Multi-Threaded Concurrent Seqlock Stress Benchmark (1 Producer, 4 Consumers)...\n";

    std::ostringstream ss;
    ss << "Local\\KongorTestShmStress_" << GetCurrentProcessId();
    std::string mapName = ss.str();

    KongorSharedMemHandle producer;
    bool created = kongor_shm_create_producer(mapName.c_str(), 4096, &producer);
    assert(created);

    std::atomic<bool> runThreads{true};
    std::atomic<uint64_t> totalReads{0};
    std::atomic<uint64_t> tornReadsDetected{0};
    std::atomic<uint64_t> successfulReads{0};
    std::atomic<uint64_t> totalReadTimeNs{0};

    const uint64_t TARGET_WRITES = 20000;
    const int NUM_CONSUMERS = 4;

    // Launch Producer thread
    std::thread producerThread([&]() {
        for (uint64_t frame = 1; frame <= TARGET_WRITES; ++frame) {
            FrameTelemetryRecord rec = {};
            rec.FrameIndex = frame;
            rec.TimestampQpc = (int64_t)frame * 1000;
            rec.CpuFrameTimeMs = (float)frame;
            rec.GpuFrameTimeMs = (float)frame * 2.0f;
            rec.DrawCallCount = (uint32_t)(frame % 500);
            rec.ServerTick = (uint32_t)frame;
            rec.ReservedFlags = (uint32_t)(frame & 0xFF);

            kongor_shm_write_record(&producer, &rec);
            // Simulate 1000 FPS write pacing
            std::this_thread::yield();
        }
        runThreads = false;
    });

    // Launch Consumer threads
    std::vector<std::thread> consumerThreads;
    for (int i = 0; i < NUM_CONSUMERS; ++i) {
        consumerThreads.emplace_back([&, i]() {
            KongorSharedMemHandle consumer;
            bool ok = kongor_shm_open_consumer(mapName.c_str(), &consumer);
            assert(ok);

            uint64_t localReads = 0;
            uint64_t localSuccess = 0;
            uint64_t localTorn = 0;
            uint64_t localTimeNs = 0;

            while (runThreads.load(std::memory_order_relaxed) || localReads < 50000) {
                auto t0 = std::chrono::high_resolution_clock::now();
                FrameTelemetryRecord rec = {};
                bool success = kongor_shm_read_latest(&consumer, &rec);
                auto t1 = std::chrono::high_resolution_clock::now();
                localTimeNs += std::chrono::duration_cast<std::chrono::nanoseconds>(t1 - t0).count();

                localReads++;
                if (success) {
                    // Check for torn reads: every field was written based on 'frame'
                    // If a torn read occurred, CpuFrameTimeMs != (float)FrameIndex
                    if (rec.FrameIndex > 0) {
                        float expectedCpu = (float)rec.FrameIndex;
                        float expectedGpu = (float)rec.FrameIndex * 2.0f;
                        uint32_t expectedTick = (uint32_t)rec.FrameIndex;

                        if (rec.CpuFrameTimeMs != expectedCpu ||
                            rec.GpuFrameTimeMs != expectedGpu ||
                            rec.ServerTick != expectedTick) {
                            localTorn++;
                        } else {
                            localSuccess++;
                        }
                    }
                }

                if (!runThreads.load(std::memory_order_relaxed) && localReads >= 100000) {
                    break;
                }
            }

            totalReads += localReads;
            successfulReads += localSuccess;
            tornReadsDetected += localTorn;
            totalReadTimeNs += localTimeNs;

            kongor_shm_close(&consumer);
        });
    }

    producerThread.join();
    for (auto &t : consumerThreads) {
        t.join();
    }

    double avgLatencyNs = (double)totalReadTimeNs.load() / (double)totalReads.load();
    double avgLatencyUs = avgLatencyNs / 1000.0;

    std::cout << "  -> Total Producer Writes:  " << TARGET_WRITES << "\n";
    std::cout << "  -> Total Concurrent Reads: " << totalReads.load() << "\n";
    std::cout << "  -> Successful Reads:       " << successfulReads.load() << "\n";
    std::cout << "  -> Torn Reads Detected:    " << tornReadsDetected.load() << " (MUST BE 0)\n";
    std::cout << "  -> Average Read Latency:   " << avgLatencyUs << " microseconds (" << avgLatencyNs << " ns)\n";

    assert(tornReadsDetected.load() == 0 && "FATAL: Torn reads detected in Seqlock!");
    assert(avgLatencyUs < 5.0 && "FATAL: Read latency exceeds 5 microsecond frame budget!");

    std::cout << "  -> Seqlock multi-threaded verification PASSED with ZERO torn reads!\n";

    kongor_shm_close(&producer);
}

int main() {
    std::cout << "============================================================\n";
    std::cout << "KONGOR FAST-BUS SHARED MEMORY & SEQLOCK TEST SUITE (PHASE 2)\n";
    std::cout << "============================================================\n";

    TestStructLayout();
    TestSingleThreadedLifecycle();
    TestMultiThreadedSeqlockStress();

    std::cout << "\n[ALL SHARED MEMORY & SEQLOCK TESTS PASSED SUCCESSFULLY]\n";
    return 0;
}
