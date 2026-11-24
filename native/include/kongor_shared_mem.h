// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file kongor_shared_mem.h
 * @brief Lock-Free Shared-Memory Fast Bus with 64-bit Atomic Seqlock
 * 
 * Project Kongor - Phase 2 Engine Telemetry & Fast-Bus Synchronization
 * Architecture: 192-byte 3-Cache-Line Dedicated Header Layout (Zero False Sharing)
 * 
 * Cache Line 0 (0..63):   V1 Base Header (100% Backwards-Compatible with Legacy Readers)
 * Cache Line 1 (64..127): Seqlock Synchronization, QPC Timestamps, & Re-entrancy Guard
 * Cache Line 2 (128..191): Atomic Latest Snapshot (Single 64-byte Cache-Line Read)
 * Cache Line 3+ (192..):  Circular Ring Buffer Slots (Capacity = 4096 records)
 */

#ifndef KONGOR_SHARED_MEM_H
#define KONGOR_SHARED_MEM_H

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <sddl.h>
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdio.h>
#include <intrin.h>

#ifdef _MSC_VER
#pragma comment(lib, "advapi32.lib")
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// CONSTANTS & BITFLAGS
// ============================================================================

#define KONGOR_SHM_MAGIC                0x4B4F4E47  /* 'KONG' */
#define KONGOR_SHM_VERSION_1            1
#define KONGOR_SHM_VERSION_2            2
#define KONGOR_SHM_CURRENT_VERSION      KONGOR_SHM_VERSION_2

#define KONGOR_SHM_DEFAULT_CAPACITY     4096
#define KONGOR_SHM_HEADER_SIZE_V1       64
#define KONGOR_SHM_HEADER_SIZE_V2       192
#define KONGOR_SHM_RECORD_SIZE          64

/* Voice Subsystem Push-To-Talk Bitflags (ReservedFlags) */
#define KONGOR_VOICE_PTT_TEAM           (1ULL << 0)
#define KONGOR_VOICE_PTT_LANE           (1ULL << 1)
#define KONGOR_VOICE_PTT_PARTY          (1ULL << 2)
#define KONGOR_VOICE_PTT_MENTOR         (1ULL << 3)

/* Frame Telemetry Hitch Sentinel Bitflags (ReservedFlags in FrameTelemetryRecord) */
#define KONGOR_FRAME_FLAG_HITCH         (1U << 0)
#define KONGOR_FRAME_FLAG_CPU_STALL     (1U << 1)
#define KONGOR_FRAME_FLAG_GPU_STALL     (1U << 2)
#define KONGOR_FRAME_FLAG_DWM_WAIT      (1U << 3)
#define KONGOR_FRAME_FLAG_PREEMPTION    (1U << 4)

// ============================================================================
// DATA STRUCTURES
// ============================================================================

#pragma pack(push, 1)

/**
 * @brief Blittable 64-byte frame telemetry record matching an exact CPU cache line.
 */
typedef struct FrameTelemetryRecord {
    uint64_t FrameIndex;
    int64_t  TimestampQpc;
    float    CpuFrameTimeMs;
    float    GpuFrameTimeMs;
    float    InputLatencyMs;
    uint32_t DrawCallCount;
    uint32_t ActiveEntities;
    uint32_t ParticlesAlive;
    uint32_t PingMicroseconds;
    float    PacketLossPercent;
    uint32_t MemoryWorkingSetMb;
    uint32_t AudioVoicesActive;
    uint32_t ServerTick;
    uint32_t ReservedFlags;
} FrameTelemetryRecord;

#ifdef __cplusplus
static_assert(sizeof(FrameTelemetryRecord) == 64, "FrameTelemetryRecord must be exactly 64 bytes");
#endif

/**
 * @brief 192-byte 3-Cache-Line Dedicated Header for Lock-Free Shared-Memory Fast Bus.
 */
typedef struct KongorTelemetryRingHeaderV2 {
    /* ------------------------------------------------------------------------
     * Cache Line 0 (Bytes 0..63): V1 Base Header (Backwards-Compatible)
     * ------------------------------------------------------------------------ */
    uint32_t Magic;              /* 0x4B4F4E47 ('KONG') */
    uint32_t Version;            /* 2 */
    uint32_t Capacity;           /* 4096 */
    uint32_t RecordSizeBytes;    /* 64 */
    uint64_t Head;               /* Monotonic write index */
    uint64_t Tail;               /* Monotonic read index */
    uint64_t ProducerProcessId;  /* PID of producer process */
    int64_t  StartTimestampQpc;  /* QPC base at startup */
    int64_t  FrequencyQpc;       /* QPC frequency */
    uint64_t ReservedFlags;      /* Global engine bitflags (PTT, status) */

    /* ------------------------------------------------------------------------
     * Cache Line 1 (Bytes 64..127): Seqlock Synchronization & Live Engine State
     * ------------------------------------------------------------------------ */
    uint64_t LatestSequence;     /* [64..71]  64-bit Seqlock: odd = write in progress, even = committed */
    int64_t  SnapshotTimestamp;  /* [72..79]  Timestamp of latest snapshot write */
    uint32_t ProducerThreadId;   /* [80..83]  Thread ID of producer for re-entrancy deadlock avoidance */
    uint32_t GamePhase;          /* [84..87]  Canonical live engine phase emitted by shm_telemetry.cpp (0=Menu/Lobby, 1=Loading, 2=HeroSelect, 3=InGame, 4=PostGame) */
    uint32_t ActiveMatchId;      /* [88..91]  Active Match ID (0 when in menu/lobby) */
    uint32_t ConnectedPlayers;   /* [92..95]  Connected player count (1 in lobby/menu) */
    uint32_t LuaMemoryKb;        /* [96..99]  Live Lua 5.1 VM heap usage in KB */
    char     CurrentMap[16];     /* [100..115] Null-terminated active map name ("" when in lobby/login) */
    char     PlayerName[12];     /* [116..127] Null-terminated active player nickname */

    /* ------------------------------------------------------------------------
     * Cache Line 2 (Bytes 128..191): Atomic Latest Snapshot (Single Cache Line)
     * ------------------------------------------------------------------------ */
    FrameTelemetryRecord LatestRecord; /* Exactly 64 bytes */

} KongorTelemetryRingHeaderV2;

#ifdef __cplusplus
static_assert(sizeof(KongorTelemetryRingHeaderV2) == 192, "KongorTelemetryRingHeaderV2 must be exactly 192 bytes (3 cache lines)");
static_assert(offsetof(KongorTelemetryRingHeaderV2, GamePhase) == 84, "GamePhase must be at byte 84");
static_assert(offsetof(KongorTelemetryRingHeaderV2, ActiveMatchId) == 88, "ActiveMatchId must be at byte 88");
static_assert(offsetof(KongorTelemetryRingHeaderV2, ConnectedPlayers) == 92, "ConnectedPlayers must be at byte 92");
static_assert(offsetof(KongorTelemetryRingHeaderV2, LuaMemoryKb) == 96, "LuaMemoryKb must be at byte 96");
static_assert(offsetof(KongorTelemetryRingHeaderV2, CurrentMap) == 100, "CurrentMap must be at byte 100");
static_assert(offsetof(KongorTelemetryRingHeaderV2, PlayerName) == 116, "PlayerName must be at byte 116");
static_assert(offsetof(KongorTelemetryRingHeaderV2, LatestRecord) == 128, "LatestRecord must be at byte 128 (Cache line 2)");
#endif

#pragma pack(pop)

/**
 * @brief Handle to an initialized or opened shared memory ring buffer.
 */
typedef struct KongorSharedMemHandle {
    HANDLE  hMap;
    void   *pView;
    KongorTelemetryRingHeaderV2 *pHeader;
    FrameTelemetryRecord        *pRecords;
    uint32_t Capacity;
    uint32_t CapacityMask;
    bool     IsProducer;
} KongorSharedMemHandle;

// ============================================================================
// SECURITY ATTRIBUTES HELPER (OWNER-RESTRICTED DACL — NEVER NULL DACL)
// ============================================================================

/**
 * @brief Initializes a SECURITY_ATTRIBUTES struct with an explicit Owner + Administrators + System DACL.
 * Replaces legacy NULL DACL so untrusted/cross-user local processes cannot corrupt the seqlock ring.
 * Caller must release the heap-allocated security descriptor via kongor_shm_free_security_attributes().
 */
static inline bool kongor_shm_init_security_attributes(
    SECURITY_ATTRIBUTES *pSa,
    SECURITY_DESCRIPTOR *pSd)
{
    (void)pSd;
    if (!pSa) return false;
    pSa->nLength = sizeof(SECURITY_ATTRIBUTES);
    pSa->lpSecurityDescriptor = NULL;
    pSa->bInheritHandle = FALSE;

    char sddlBuf[256];
    snprintf(sddlBuf, sizeof(sddlBuf), "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GA;;;CO)");

    HANDLE hToken = NULL;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken) && hToken != NULL) {
        DWORD needed = 0;
        GetTokenInformation(hToken, TokenUser, NULL, 0, &needed);
        if (needed > 0 && needed <= 1024) {
            uint8_t tokenBuf[1024];
            if (GetTokenInformation(hToken, TokenUser, tokenBuf, needed, &needed)) {
                TOKEN_USER *pTokenUser = (TOKEN_USER *)tokenBuf;
                LPSTR sidStr = NULL;
                if (pTokenUser->User.Sid && ConvertSidToStringSidA(pTokenUser->User.Sid, &sidStr) && sidStr != NULL) {
                    snprintf(sddlBuf, sizeof(sddlBuf), "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;%s)", sidStr);
                    LocalFree(sidStr);
                }
            }
        }
        CloseHandle(hToken);
    }

    PSECURITY_DESCRIPTOR pHeapSd = NULL;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorA(sddlBuf, SDDL_REVISION_1, &pHeapSd, NULL) && pHeapSd != NULL) {
        pSa->lpSecurityDescriptor = pHeapSd;
        return true;
    }

    return false;
}

static inline void kongor_shm_free_security_attributes(SECURITY_ATTRIBUTES *pSa)
{
    if (pSa && pSa->lpSecurityDescriptor) {
        LocalFree(pSa->lpSecurityDescriptor);
        pSa->lpSecurityDescriptor = NULL;
    }
}

// ============================================================================
// PRODUCER / CONSUMER LIFECYCLE API
// ============================================================================

/**
 * @brief Creates a shared memory telemetry buffer as a producer.
 * 
 * @param mapName Named mapping identifier (e.g., "Local\\KongorClientTelemetry_1234").
 * @param capacity Number of circular slots (must be a power of 2, default 4096).
 * @param outHandle Pointer to handle struct receiving mappings.
 * @return true on success, false on failure (check GetLastError()).
 */
static inline bool kongor_shm_create_producer(
    const char *mapName,
    uint32_t capacity,
    KongorSharedMemHandle *outHandle)
{
    if (!mapName || !outHandle) return false;
    if (capacity == 0 || (capacity & (capacity - 1)) != 0) {
        capacity = KONGOR_SHM_DEFAULT_CAPACITY;
    }

    size_t totalBytes = sizeof(KongorTelemetryRingHeaderV2) + ((size_t)capacity * sizeof(FrameTelemetryRecord));

    SECURITY_ATTRIBUTES sa;
    SECURITY_DESCRIPTOR sd;
    if (!kongor_shm_init_security_attributes(&sa, &sd)) {
        return false;
    }

    HANDLE hMap = CreateFileMappingA(
        INVALID_HANDLE_VALUE,
        &sa,
        PAGE_READWRITE,
        0,
        (DWORD)totalBytes,
        mapName
    );

    if (!hMap && strncmp(mapName, "Global\\", 7) == 0) {
        /* Fall back to Local namespace if Global requires elevated SeCreateGlobalPrivilege */
        char localMap[256];
        snprintf(localMap, sizeof(localMap), "Local\\%s", mapName + 7);
        hMap = CreateFileMappingA(
            INVALID_HANDLE_VALUE,
            &sa,
            PAGE_READWRITE,
            0,
            (DWORD)totalBytes,
            localMap
        );
    }

    kongor_shm_free_security_attributes(&sa);

    if (!hMap) return false;

    void *pView = MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, totalBytes);
    if (!pView) {
        CloseHandle(hMap);
        return false;
    }

    KongorTelemetryRingHeaderV2 *pHeader = (KongorTelemetryRingHeaderV2 *)pView;
    FrameTelemetryRecord *pRecords = (FrameTelemetryRecord *)((uint8_t *)pView + sizeof(KongorTelemetryRingHeaderV2));

    LARGE_INTEGER qpcFreq, qpcNow;
    QueryPerformanceFrequency(&qpcFreq);
    QueryPerformanceCounter(&qpcNow);

    ZeroMemory(pHeader, sizeof(KongorTelemetryRingHeaderV2));
    pHeader->Magic = KONGOR_SHM_MAGIC;
    pHeader->Version = KONGOR_SHM_VERSION_2;
    pHeader->Capacity = capacity;
    pHeader->RecordSizeBytes = sizeof(FrameTelemetryRecord);
    pHeader->Head = 0;
    pHeader->Tail = 0;
    pHeader->ProducerProcessId = GetCurrentProcessId();
    pHeader->StartTimestampQpc = qpcNow.QuadPart;
    pHeader->FrequencyQpc = qpcFreq.QuadPart;
    pHeader->ReservedFlags = 0;

    /* Initialize Cache Line 1 Seqlock */
    pHeader->LatestSequence = 0; /* Even = clean committed initial state */
    pHeader->SnapshotTimestamp = qpcNow.QuadPart;
    pHeader->ProducerThreadId = GetCurrentThreadId();

    /* Flush header to RAM before consumer connects */
    _ReadWriteBarrier();
    MemoryBarrier();

    outHandle->hMap = hMap;
    outHandle->pView = pView;
    outHandle->pHeader = pHeader;
    outHandle->pRecords = pRecords;
    outHandle->Capacity = capacity;
    outHandle->CapacityMask = capacity - 1;
    outHandle->IsProducer = true;

    return true;
}

/**
 * @brief Opens an existing shared memory buffer as a consumer.
 * Automatically inspects the header version (V1 vs V2) and sets record base offsets accordingly.
 */
static inline bool kongor_shm_open_consumer(
    const char *mapName,
    KongorSharedMemHandle *outHandle)
{
    if (!mapName || !outHandle) return false;

    HANDLE hMap = OpenFileMappingA(FILE_MAP_READ, FALSE, mapName);
    if (!hMap) {
        /* Try ReadWrite if ReadOnly was denied */
        hMap = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, mapName);
    }
    if (!hMap) return false;

    void *pView = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
    if (!pView) {
        CloseHandle(hMap);
        return false;
    }

    KongorTelemetryRingHeaderV2 *pHeader = (KongorTelemetryRingHeaderV2 *)pView;
    if (pHeader->Magic != KONGOR_SHM_MAGIC) {
        UnmapViewOfFile(pView);
        CloseHandle(hMap);
        return false;
    }

    /* Dynamic V1 vs V2 record offset: V1 uses 64 bytes, V2 uses 192 bytes */
    size_t headerOffset = (pHeader->Version >= KONGOR_SHM_VERSION_2) 
        ? KONGOR_SHM_HEADER_SIZE_V2 
        : KONGOR_SHM_HEADER_SIZE_V1;

    FrameTelemetryRecord *pRecords = (FrameTelemetryRecord *)((uint8_t *)pView + headerOffset);

    outHandle->hMap = hMap;
    outHandle->pView = pView;
    outHandle->pHeader = pHeader;
    outHandle->pRecords = pRecords;
    outHandle->Capacity = pHeader->Capacity;
    outHandle->CapacityMask = pHeader->Capacity > 0 ? (pHeader->Capacity - 1) : 0;
    outHandle->IsProducer = false;

    return true;
}

/**
 * @brief Closes and releases shared memory mapping.
 */
static inline void kongor_shm_close(KongorSharedMemHandle *handle)
{
    if (!handle) return;
    if (handle->pView) {
        UnmapViewOfFile(handle->pView);
        handle->pView = NULL;
    }
    if (handle->hMap) {
        CloseHandle(handle->hMap);
        handle->hMap = NULL;
    }
    handle->pHeader = NULL;
    handle->pRecords = NULL;
}

// ============================================================================
// SEQLOCK SYNCHRONIZATION PROTOCOL
// ============================================================================

/**
 * @brief Writes a frame telemetry record using the 64-bit atomic Seqlock protocol.
 * 
 * Hardware Protocol:
 * 1. Increment LatestSequence (odd -> indicates write in progress).
 * 2. Store Snapshot in Cache Line 2 and append to circular buffer slot.
 * 3. MemoryBarrier (SFENCE/MFENCE) ensuring all bytes are visible to bus.
 * 4. Increment LatestSequence (even -> indicates write committed).
 * 5. Update circular Head.
 */
static inline void kongor_shm_write_record(
    KongorSharedMemHandle *handle,
    const FrameTelemetryRecord *pRecord)
{
    if (!handle || !handle->pHeader || !pRecord) return;
    KongorTelemetryRingHeaderV2 *hdr = handle->pHeader;

    /* Update thread ID for re-entrancy avoidance */
    hdr->ProducerThreadId = GetCurrentThreadId();

    /* 1. Mark write in progress (Odd sequence) */
    _InterlockedIncrement64((volatile LONG64 *)&hdr->LatestSequence);
    _ReadWriteBarrier();

    /* 2. Write atomic snapshot (Cache Line 2) */
    hdr->LatestRecord = *pRecord;
    hdr->SnapshotTimestamp = pRecord->TimestampQpc;

    /* Also copy to circular ring buffer for historical log/dump ingestion */
    if (handle->pRecords && handle->CapacityMask > 0) {
        uint64_t head = hdr->Head;
        uint32_t slot = (uint32_t)(head & handle->CapacityMask);
        handle->pRecords[slot] = *pRecord;
    }

    /* 3. Hardware commit fence */
    MemoryBarrier();

    /* 4. Mark write complete (Even sequence) */
    _InterlockedIncrement64((volatile LONG64 *)&hdr->LatestSequence);

    /* 5. Publish circular Head */
    _InterlockedIncrement64((volatile LONG64 *)&hdr->Head);
}

/**
 * @brief Reads the latest frame telemetry snapshot in sub-microsecond time with ZERO locks.
 * 
 * Protocol:
 * - Checks re-entrancy: if the caller is on the producer thread itself, returns directly.
 * - Samples seq1. If odd (write in progress), spin-waits via _mm_pause().
 * - Copies 64-byte payload from Cache Line 2.
 * - Samples seq2. If seq1 == seq2, read is 100% consistent with ZERO torn data!
 * 
 * @param handle Consumer or Producer handle.
 * @param outRecord Buffer receiving 64-byte snapshot.
 * @return true on consistent read, false on torn read after retries.
 */
static inline bool kongor_shm_read_latest(
    const KongorSharedMemHandle *handle,
    FrameTelemetryRecord *outRecord)
{
    if (!handle || !handle->pHeader || !outRecord) return false;
    const KongorTelemetryRingHeaderV2 *hdr = handle->pHeader;

    /* Re-entrancy guard: if current thread is the producer thread, read directly */
    if (GetCurrentThreadId() == hdr->ProducerThreadId) {
        *outRecord = hdr->LatestRecord;
        return true;
    }

    /* Retry loop (up to 4 attempts) */
    for (int retry = 0; retry < 4; ++retry) {
        uint64_t seq1 = *(volatile const uint64_t *)&hdr->LatestSequence;
        if ((seq1 & 1) != 0) {
            _mm_pause();
            continue;
        }

        _ReadWriteBarrier();
        FrameTelemetryRecord snapshot = hdr->LatestRecord;
        MemoryBarrier();

        uint64_t seq2 = *(volatile const uint64_t *)&hdr->LatestSequence;
        if (seq1 == seq2) {
            *outRecord = snapshot;
            return true; /* 100% consistent read! */
        }

        _mm_pause();
    }

    return false; /* Torn read detected across all retries */
}

#ifdef __cplusplus
}
#endif

#endif /* KONGOR_SHARED_MEM_H */
