// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard Shared Memory Fast Bus & Telemetry Sampler
// ============================================================================

#include "native_guard_internal.h"
#include "kongor_shared_mem.h"
#include "kongor_deterministic_math.h"
#include "runtime_patch_controller.h"
#include "asset_prewarmer.h"
#include "modern_archive_hook.h"
#include "kongor_tracy.h"
#include <windows.h>
#include <psapi.h>
#include <cstdint>
#include <atomic>
#include <string>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cwchar>
#include <algorithm>
#include <unordered_map>
#include <mmsystem.h>

typedef KongorTelemetryRingHeaderV2 TelemetryRingHeader;

KongorSharedMemHandle g_shmHandle = { 0 };
KongorTelemetryRingHeaderV2* g_pTelemetryHeader = NULL;
FrameTelemetryRecord* g_pTelemetryRecords = NULL;
static std::atomic<uint64_t> g_telemetryFrameCounter{0};
LARGE_INTEGER g_qpcFrequency = {0};
static LARGE_INTEGER g_qpcLastFrame = {0};
LARGE_INTEGER g_qpcProcessStart = {0};
static HANDLE g_hTelemetryWorkerThread = NULL;
static volatile bool g_telemetryWorkerRunning = false;
void** g_ppHost = nullptr;
static void** g_ppGame = nullptr;

std::atomic<uint32_t> g_dynamicDrawCalls{ 180 };
std::atomic<uint32_t> g_dynamicActiveEntities{ 0 };
std::atomic<uint32_t> g_dynamicParticlesAlive{ 350 };
std::atomic<uint32_t> g_dynamicPingMicroseconds{ 12000 };
std::atomic<float>    g_dynamicPacketLossPercent{ 0.0f };
std::atomic<uint32_t> g_dynamicServerTick{ 0 };
std::atomic<uint32_t> g_dynamicAudioVoices{ 16 };
std::atomic<float>    g_dynamicInputLatencyMs{ 1.2f };
std::atomic<uint64_t> g_telemetryHitchCount{ 0 };
std::atomic<uint64_t> g_telemetryCpuStallCount{ 0 };
std::atomic<uint32_t> g_telemetryPeakFrameTimeUs{ 0 };

static SRWLOCK g_hostFrameTrackLock = SRWLOCK_INIT;
static int32_t g_lastTrackedHostFrame = 0;
static uint64_t g_lastHostFrameAdvanceTick = 0;
static uint64_t g_nativeGuardAttachTick = GetTickCount64();

int g_resolvedDedicatedServerPort = 0;
wchar_t g_wszTrackedOfflineNick[32] = {0};
std::atomic<uint64_t> g_fullGcCount{ 0 };
std::atomic<int> g_lastKnownGamePhase{ -1 };

static void SampleLiveEngineStateForTelemetry(int32_t* ioLastHostFrame, double deltaSeconds, float* outRealFrameTimeMs);

uint64_t UpdateAndGetMsSinceHostFrameAdvance(int32_t currentHostFrame) {
    uint64_t nowTick = GetTickCount64();
    AcquireSRWLockExclusive(&g_hostFrameTrackLock);
    if (currentHostFrame > 0) {
        if (g_lastTrackedHostFrame <= 0) {
            g_lastTrackedHostFrame = currentHostFrame;
        } else if (currentHostFrame != g_lastTrackedHostFrame) {
            g_lastTrackedHostFrame = currentHostFrame;
            g_lastHostFrameAdvanceTick = nowTick;
        }
    }
    uint64_t lastAdvTick = g_lastHostFrameAdvanceTick;
    uint64_t attachTick = g_nativeGuardAttachTick;
    ReleaseSRWLockExclusive(&g_hostFrameTrackLock);

    if (currentHostFrame <= 0 || lastAdvTick == 0) {
        uint64_t sinceAttachMs = (nowTick >= attachTick) ? (nowTick - attachTick) : 0ULL;
        return 1000ULL + sinceAttachMs;
    }
    return (nowTick >= lastAdvTick) ? (nowTick - lastAdvTick) : 0ULL;
}


static void SampleLiveEngineStateForTelemetry(int32_t* ioLastHostFrame, double deltaSeconds, float* outRealFrameTimeMs);

// Voice Subsystem Fast-Bus Push-To-Talk Flags (using definitions from kongor_shared_mem.h)
static std::atomic<uint64_t> g_voicePttFlags{ 0 };

static void WriteFrameTelemetry(
    float cpuTimeMs,
    float gpuTimeMs,
    float inputLatencyMs,
    uint32_t drawCalls,
    uint32_t activeEntities,
    uint32_t particlesAlive,
    uint32_t pingMicroseconds,
    float packetLossPercent,
    uint32_t memoryWorkingSetMb,
    uint32_t audioVoicesActive,
    uint32_t serverTick,
    uint32_t flags)
{
    if (!g_shmHandle.pHeader || !g_shmHandle.pRecords) return;

    LARGE_INTEGER nowQpc;
    QueryPerformanceCounter(&nowQpc);

    uint64_t frameIdx = ++g_telemetryFrameCounter;

    if (cpuTimeMs > 5.0f) {
        flags |= KONGOR_FRAME_FLAG_HITCH;
        g_telemetryHitchCount.fetch_add(1, std::memory_order_relaxed);
    }
    if (cpuTimeMs > 16.6f) {
        flags |= KONGOR_FRAME_FLAG_CPU_STALL;
        g_telemetryCpuStallCount.fetch_add(1, std::memory_order_relaxed);
    }
    uint32_t frameTimeUs = cpuTimeMs > 0.0f ? (uint32_t)(cpuTimeMs * 1000.0f) : 0;
    uint32_t prevPeakUs = g_telemetryPeakFrameTimeUs.load(std::memory_order_relaxed);
    while (frameTimeUs > prevPeakUs &&
           !g_telemetryPeakFrameTimeUs.compare_exchange_weak(prevPeakUs, frameTimeUs, std::memory_order_relaxed)) {
    }
    Kongor::NotifyPrewarmerFrameTimeUs(frameTimeUs);

    FrameTelemetryRecord rec;
    rec.FrameIndex = frameIdx;
    rec.TimestampQpc = nowQpc.QuadPart;
    rec.CpuFrameTimeMs = cpuTimeMs;
    rec.GpuFrameTimeMs = gpuTimeMs;
    rec.InputLatencyMs = inputLatencyMs;
    rec.DrawCallCount = drawCalls;
    rec.ActiveEntities = activeEntities;
    rec.ParticlesAlive = particlesAlive;
    rec.PingMicroseconds = pingMicroseconds;
    rec.PacketLossPercent = packetLossPercent;
    rec.MemoryWorkingSetMb = memoryWorkingSetMb;
    rec.AudioVoicesActive = audioVoicesActive;
    rec.ServerTick = serverTick;
    rec.ReservedFlags = flags;

    if (g_shmHandle.pHeader) {
        g_shmHandle.pHeader->ReservedFlags = g_voicePttFlags.load(std::memory_order_relaxed);
    }

    kongor_shm_write_record(&g_shmHandle, &rec);
}

DWORD WINAPI NativeGuardTelemetrySamplerWorker(LPVOID lpParam) {
    timeBeginPeriod(1);

    HANDLE hProc = GetCurrentProcess();
    PROCESS_MEMORY_COUNTERS_EX pmc;
    ZeroMemory(&pmc, sizeof(pmc));

    LARGE_INTEGER lastQpc;
    QueryPerformanceCounter(&lastQpc);
    LARGE_INTEGER lastMemSampleQpc = lastQpc;
    uint32_t cachedWorkingSetMb = 0;
    if (K32GetProcessMemoryInfo(hProc, (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof(pmc))) {
        cachedWorkingSetMb = (uint32_t)(pmc.WorkingSetSize / (1024 * 1024));
    }
    int32_t lastHostFrame = 0;
    float lastKnownFrameTimeMs = 6.94f; // 144 FPS initial baseline before first frame delta

    while (g_telemetryWorkerRunning && g_pTelemetryHeader != NULL) {
        Sleep(50);
        KONGOR_TRACY_ZONE_SCOPED_N("NativeGuard_TelemetryTick");

        LARGE_INTEGER nowQpc;
        QueryPerformanceCounter(&nowQpc);

        double deltaSeconds = 0.050;
        if (g_qpcFrequency.QuadPart > 0) {
            deltaSeconds = (double)(nowQpc.QuadPart - lastQpc.QuadPart) / (double)g_qpcFrequency.QuadPart;
        }
        lastQpc = nowQpc;

        float sampledFrameTimeMs = 0.0f;
        SampleLiveEngineStateForTelemetry(&lastHostFrame, deltaSeconds, &sampledFrameTimeMs);
        if (sampledFrameTimeMs > 0.1f && sampledFrameTimeMs < 500.0f) {
            lastKnownFrameTimeMs = sampledFrameTimeMs;
        }
        float frameTimeMs = lastKnownFrameTimeMs;

        // Query kernel working set only once every 10 seconds to eliminate NT kernel lock contention
        if (g_qpcFrequency.QuadPart > 0 && (nowQpc.QuadPart - lastMemSampleQpc.QuadPart) >= (g_qpcFrequency.QuadPart * 10)) {
            lastMemSampleQpc = nowQpc;
            if (K32GetProcessMemoryInfo(hProc, (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof(pmc))) {
                cachedWorkingSetMb = (uint32_t)(pmc.WorkingSetSize / (1024 * 1024));
            }
        }
        uint32_t workingSetMb = cachedWorkingSetMb;

        uint32_t tick = g_dynamicServerTick.load(std::memory_order_relaxed);
        if (tick == 0) {
            tick = (lastHostFrame > 0) ? (uint32_t)lastHostFrame : (uint32_t)(g_telemetryFrameCounter.load(std::memory_order_relaxed) / 20);
        }

        WriteFrameTelemetry(
            frameTimeMs > 0.0f ? frameTimeMs : 1.0f,
            frameTimeMs > 0.0f ? (frameTimeMs * 0.9f) : 0.9f,
            g_dynamicInputLatencyMs.load(std::memory_order_relaxed),
            g_dynamicDrawCalls.load(std::memory_order_relaxed),
            g_dynamicActiveEntities.load(std::memory_order_relaxed),
            g_dynamicParticlesAlive.load(std::memory_order_relaxed),
            g_dynamicPingMicroseconds.load(std::memory_order_relaxed),
            g_dynamicPacketLossPercent.load(std::memory_order_relaxed),
            workingSetMb,
            g_dynamicAudioVoices.load(std::memory_order_relaxed),
            tick,
            (uint32_t)g_voicePttFlags.load(std::memory_order_relaxed)
        );

        // Periodically flush VFS archive telemetry summary every 5 seconds (100 * 50ms ticks)
        static uint32_t s_summaryFlushCounter = 0;
        if (++s_summaryFlushCounter % 100 == 0) {
            LogArchiveTelemetrySummary();
        }

        // Two-tier main-thread freeze watchdog (Tier 1: 500ms+key / 2.5s cursor rescue; Tier 2: 15s/30s compact hang dump)
        CheckMainThreadWatchdogTick();

        // Periodically refresh dedicated server state keyframe every 1 second (20 * 50ms ticks)
        if (IsDedicatedServerProcessCached()) {
            static uint32_t s_keyframeTickCounter = 0;
            if ((s_keyframeTickCounter + 1) % 20 == 0 && (GetTickCount64() - GetLastHeroKeyframeSampleTickMs() >= 900)) {
                DispatchCommandToEngine(L"__kongor_keyframe_sample", 0, 0);
            }
            if (++s_keyframeTickCounter % 20 == 0) {
                int sPort = g_resolvedDedicatedServerPort > 0 ? g_resolvedDedicatedServerPort : ResolveDedicatedServerPortFromCommandLine(GetCommandLineA());
                if (sPort > 0) {
                    WriteServerKeyframe(sPort);
                }
            }
        }
    }

    timeEndPeriod(1);
    return 0;
}

static void ExtractCmdLineCvarValue(const std::string& cmdLine, const char* cvarName, char* outBuf, size_t maxLen) {
    Kongor_ExtractCmdLineCvarValue(cmdLine.c_str(), cvarName, outBuf, maxLen);
}

#pragma pack(push, 1)
struct ServerKeyframeHeaderNative {
    uint32_t Magic;              // 0x4B474B46 ("KGKF")
    uint32_t Version;            // 1
    uint32_t Sequence;
    uint32_t ServerTick;
    uint64_t MatchId;
    int64_t  TimestampUtcTicks;
    uint32_t PayloadSizeBytes;
    uint32_t Crc32;
};
#pragma pack(pop)
static_assert(sizeof(ServerKeyframeHeaderNative) == 40, "ServerKeyframeHeaderNative must be exactly 40 bytes");

uint32_t ComputeCrc32Native(const uint8_t* data, size_t length) {
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < length; i++) {
        uint8_t b = data[i];
        crc ^= b;
        for (int j = 0; j < 8; j++) {
            uint32_t mask = (uint32_t)-(int32_t)(crc & 1);
            crc = (crc >> 1) ^ (0xEDB88320 & mask);
        }
    }
    return ~crc;
}

static std::atomic<uint32_t> g_keyframeSequence{ 0 };
static std::mutex g_keyframeMapMutex;
static HANDLE g_hKeyframeMap = NULL;
static int g_hKeyframePort = 0;

struct NativeKeyframePlayer {
    int slot = -1;
    int team = 1;
    char hero[32] = {0};
    char name[32] = {0};
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
    float health = 1000.0f;
    float maxHealth = 1000.0f;
    float mana = 500.0f;
    float maxMana = 500.0f;
    int level = 1;
    float heading = 0.0f;
    bool isAlive = true;
    bool valid = false;
};
static SRWLOCK g_keyframePlayersLock = SRWLOCK_INIT;
static NativeKeyframePlayer g_keyframePlayers[10];

static void CopyJsonSafeToken(char* dst, size_t dstCap, const char* src) noexcept {
    if (!dst || dstCap == 0) return;
    dst[0] = '\0';
    if (!src) return;
    size_t outIdx = 0;
    for (size_t i = 0; src[i] != '\0' && outIdx + 1 < dstCap; ++i) {
        unsigned char ch = static_cast<unsigned char>(src[i]);
        if (ch < 0x20 || ch == '"' || ch == '\\') {
            dst[outIdx++] = '_';
        } else {
            dst[outIdx++] = static_cast<char>(ch);
        }
    }
    dst[outIdx] = '\0';
}

void SetKeyframePlayerCoordinate(
    int slot, int team, const char* hero, const char* name,
    float x, float y, float z, float health, float maxHealth,
    float mana, float maxMana, int level, float heading, bool isAlive)
{
    if (slot < 0 || slot >= 10) return;
    AcquireSRWLockExclusive(&g_keyframePlayersLock);
    NativeKeyframePlayer& p = g_keyframePlayers[slot];
    p.slot = slot;
    p.team = team;
    if (hero) CopyJsonSafeToken(p.hero, sizeof(p.hero), hero);
    if (name) CopyJsonSafeToken(p.name, sizeof(p.name), name);
    p.x = x; p.y = y; p.z = z;
    p.health = health; p.maxHealth = maxHealth;
    p.mana = mana; p.maxMana = maxMana;
    p.level = level; p.heading = heading;
    p.isAlive = isAlive;
    p.valid = true;
    ReleaseSRWLockExclusive(&g_keyframePlayersLock);
}

void ClearKeyframePlayerCoordinates()
{
    AcquireSRWLockExclusive(&g_keyframePlayersLock);
    for (int i = 0; i < 10; ++i) {
        g_keyframePlayers[i].valid = false;
    }
    ReleaseSRWLockExclusive(&g_keyframePlayersLock);
}

void ClearKeyframePlayerSlot(int slot)
{
    if (slot < 0 || slot >= 10) return;
    AcquireSRWLockExclusive(&g_keyframePlayersLock);
    g_keyframePlayers[slot].valid = false;
    ReleaseSRWLockExclusive(&g_keyframePlayersLock);
}

void CleanupServerKeyframeMap() {
    std::lock_guard<std::mutex> lock(g_keyframeMapMutex);
    if (g_hKeyframeMap != NULL && g_hKeyframeMap != INVALID_HANDLE_VALUE) {
        CloseHandle(g_hKeyframeMap);
        g_hKeyframeMap = NULL;
        g_hKeyframePort = 0;
    }
}

void WriteServerKeyframe(int port) {
    KONGOR_TRACY_ZONE_SCOPED_N("NativeGuard_WriteServerKeyframe");
    if (port <= 0) return;
    std::string mapName = "Local\\KongorServerKeyframe_" + std::to_string(port);
    std::lock_guard<std::mutex> lock(g_keyframeMapMutex);
    if (g_hKeyframeMap == NULL || g_hKeyframePort != port) {
        if (g_hKeyframeMap != NULL && g_hKeyframeMap != INVALID_HANDLE_VALUE) {
            CloseHandle(g_hKeyframeMap);
            g_hKeyframeMap = NULL;
        }
        g_hKeyframeMap = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 65536, mapName.c_str());
        g_hKeyframePort = port;
    }
    HANDLE hMap = g_hKeyframeMap;
    if (hMap != NULL && hMap != INVALID_HANDLE_VALUE) {
        LPVOID pBuf = MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, 65536);

        uint32_t seq = g_keyframeSequence.fetch_add(1, std::memory_order_relaxed) + 1;
        uint32_t tick = g_dynamicServerTick.load(std::memory_order_relaxed);
        uint64_t matchId = 0;
        uint32_t gamePhase = 0;
        uint32_t clients = 0;
        uint64_t uptimeMs = 0;

        if (g_pTelemetryHeader) {
            gamePhase = g_pTelemetryHeader->GamePhase;
            matchId = g_pTelemetryHeader->ActiveMatchId;
            clients = g_pTelemetryHeader->ConnectedPlayers;
            if (g_pTelemetryHeader->LatestRecord.ServerTick > 0) {
                tick = g_pTelemetryHeader->LatestRecord.ServerTick;
            }
            LARGE_INTEGER qpcNow;
            QueryPerformanceCounter(&qpcNow);
            if (g_qpcFrequency.QuadPart > 0 && qpcNow.QuadPart > g_pTelemetryHeader->StartTimestampQpc) {
                uptimeMs = (uint64_t)((qpcNow.QuadPart - g_pTelemetryHeader->StartTimestampQpc) * 1000 / g_qpcFrequency.QuadPart);
            }
        }

        FILETIME ft;
        GetSystemTimePreciseAsFileTime(&ft);
        ULARGE_INTEGER uli;
        uli.LowPart = ft.dwLowDateTime;
        uli.HighPart = ft.dwHighDateTime;
        int64_t timestampUtcTicks = (int64_t)(uli.QuadPart + 504911232000000000ULL);

        char payloadJson[8192];
        int payloadLen = 0;
        std::string playersJson = "[]";
        {
            AcquireSRWLockShared(&g_keyframePlayersLock);
            bool hasPlayers = false;
            std::string buf = "[";
            for (int i = 0; i < 10; ++i) {
                if (g_keyframePlayers[i].valid) {
                    if (hasPlayers) buf += ",";
                    const NativeKeyframePlayer& p = g_keyframePlayers[i];
                    char itemBuf[256];
                    snprintf(itemBuf, sizeof(itemBuf),
                        "{\"slot\":%d,\"team\":%d,\"hero\":\"%s\",\"name\":\"%s\",\"x\":%.2f,\"y\":%.2f,\"z\":%.2f,"
                        "\"health\":%.1f,\"maxHealth\":%.1f,\"mana\":%.1f,\"maxMana\":%.1f,\"level\":%d,\"heading\":%.1f,\"isAlive\":%s}",
                        p.slot, p.team, p.hero, p.name, p.x, p.y, p.z,
                        p.health, p.maxHealth, p.mana, p.maxMana, p.level, p.heading, p.isAlive ? "true" : "false");
                    buf += itemBuf;
                    hasPlayers = true;
                }
            }
            buf += "]";
            if (hasPlayers) {
                playersJson = buf;
            }
            ReleaseSRWLockShared(&g_keyframePlayersLock);
        }
        payloadLen = snprintf(payloadJson, sizeof(payloadJson),
            "{\"gamePhase\":%u,\"serverTick\":%u,\"matchId\":%llu,\"uptimeMs\":%llu,\"connectedClients\":%u,\"port\":%d,\"players\":%s}",
            gamePhase, tick, (unsigned long long)matchId, (unsigned long long)uptimeMs, clients, port,
            playersJson.c_str());

        if (payloadLen < 0) payloadLen = 0;
        if (payloadLen >= (int)sizeof(payloadJson)) payloadLen = (int)sizeof(payloadJson) - 1;

        uint32_t crc = ComputeCrc32Native((const uint8_t*)payloadJson, (size_t)payloadLen);

        ServerKeyframeHeaderNative header;
        header.Magic = 0x4B474B46;
        header.Version = 1;
        header.Sequence = seq;
        header.ServerTick = tick;
        header.MatchId = matchId;
        header.TimestampUtcTicks = timestampUtcTicks;
        header.PayloadSizeBytes = (uint32_t)payloadLen;
        header.Crc32 = crc;

        if (pBuf != NULL) {
            uint8_t* dest = (uint8_t*)pBuf;
            *(uint32_t*)dest = 0;
            if (payloadLen > 0) {
                memcpy(dest + 40, payloadJson, (size_t)payloadLen);
            }
            memcpy(dest + 4, ((const uint8_t*)&header) + 4, sizeof(header) - 4);
            MemoryBarrier();
            *(uint32_t*)dest = header.Magic;
            UnmapViewOfFile(pBuf);
        }
        // Retain hMap open for process lifetime; do not CloseHandle(hMap) here!

        char tempPath[MAX_PATH];
        if (GetTempPathA(MAX_PATH, tempPath) > 0) {
            std::string fbPath = std::string(tempPath);
            if (!fbPath.empty() && fbPath.back() != '\\' && fbPath.back() != '/') {
                fbPath += "\\";
            }
            fbPath += "KongorServerKeyframe_" + std::to_string(port) + ".bin";
            HANDLE hFile = CreateFileA(fbPath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (hFile != INVALID_HANDLE_VALUE) {
                DWORD written = 0;
                WriteFile(hFile, &header, sizeof(header), &written, NULL);
                if (payloadLen > 0) {
                    WriteFile(hFile, payloadJson, (DWORD)payloadLen, &written, NULL);
                }
                CloseHandle(hFile);
            }
        }
    }
}

void UpdateTrackedOfflineNickFromNarrow(const char* narrowVal) noexcept {
    if (!narrowVal) return;
    wchar_t wideBuf[64] = {0};
    size_t i = 0;
    while (narrowVal[i] != '\0' && i + 1 < _countof(wideBuf)) {
        wideBuf[i] = static_cast<wchar_t>(static_cast<unsigned char>(narrowVal[i]));
        ++i;
    }
    wideBuf[i] = L'\0';
    wchar_t sanitized[32] = {0};
    if (Kongor_SanitizeOfflineNicknameCandidateW(wideBuf, sanitized, _countof(sanitized))) {
        wcsncpy_s(g_wszTrackedOfflineNick, _countof(g_wszTrackedOfflineNick), sanitized, _TRUNCATE);
    }
}

void InitializeTelemetrySharedMemory() {
    DWORD currentPid = GetCurrentProcessId();
    std::string mapName = "Local\\KongorClientTelemetry_" + std::to_string(currentPid);

    std::string cmdLine = GetCommandLineA();
    int serverPort = ResolveDedicatedServerPortFromCommandLine(cmdLine);
    g_resolvedDedicatedServerPort = serverPort;
    if (serverPort > 0) {
        mapName = "Global\\KongorServerTelemetry_" + std::to_string(serverPort);
    }

    if (!kongor_shm_create_producer(mapName.c_str(), TELEMETRY_RING_CAPACITY, &g_shmHandle)) {
        // Fallback to Local\ namespace when Global\ requires SeCreateGlobalPrivilege (ERROR_ACCESS_DENIED)
        if (serverPort > 0) {
            std::string localMapName = "Local\\KongorServerTelemetry_" + std::to_string(serverPort);
            if (kongor_shm_create_producer(localMapName.c_str(), TELEMETRY_RING_CAPACITY, &g_shmHandle)) {
                mapName = localMapName;
                std::string okMsg = "[KONGOR NATIVE GUARD] Created dedicated server shared memory in Local namespace: " + mapName + "\n";
                OutputDebugStringA(okMsg.c_str());
                goto shm_initialized;
            }
        }
        char errBuf[256];
        snprintf(errBuf, sizeof(errBuf), "[KONGOR NATIVE GUARD] Failed to create shared memory '%s' (Error %lu)\n",
                 mapName.c_str(), GetLastError());
        OutputDebugStringA(errBuf);
        return;
    }

shm_initialized:

    g_pTelemetryHeader = g_shmHandle.pHeader;
    g_pTelemetryRecords = g_shmHandle.pRecords;

    // Initialize live engine state fields in Cache Line 1
    if (g_pTelemetryHeader) {
        g_pTelemetryHeader->GamePhase = 0;
        g_pTelemetryHeader->ActiveMatchId = 0;
        g_pTelemetryHeader->ConnectedPlayers = 0;
        g_pTelemetryHeader->LuaMemoryKb = 0;
        memset(g_pTelemetryHeader->CurrentMap, 0, sizeof(g_pTelemetryHeader->CurrentMap));
        memset(g_pTelemetryHeader->PlayerName, 0, sizeof(g_pTelemetryHeader->PlayerName));
        strncpy_s(g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName), "Kongor", _TRUNCATE);
        ExtractCmdLineCvarValue(cmdLine, "login_name", g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName));
        ExtractCmdLineCvarValue(cmdLine, "p2p_nickname", g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName));
        char cmdNickBuf[32] = {0};
        ExtractCmdLineCvarValue(cmdLine, "login_name", cmdNickBuf, sizeof(cmdNickBuf));
        UpdateTrackedOfflineNickFromNarrow(cmdNickBuf);
        cmdNickBuf[0] = '\0';
        ExtractCmdLineCvarValue(cmdLine, "p2p_nickname", cmdNickBuf, sizeof(cmdNickBuf));
        UpdateTrackedOfflineNickFromNarrow(cmdNickBuf);
    }

    QueryPerformanceFrequency(&g_qpcFrequency);
    QueryPerformanceCounter(&g_qpcLastFrame);
    g_qpcProcessStart = g_qpcLastFrame;

    g_telemetryWorkerRunning = true;
    g_hTelemetryWorkerThread = CreateThread(NULL, 0, NativeGuardTelemetrySamplerWorker, NULL, 0, NULL);

    size_t totalBytes = sizeof(KongorTelemetryRingHeaderV2) + (TELEMETRY_RING_CAPACITY * sizeof(FrameTelemetryRecord));
    char initLog[256];
    snprintf(initLog, sizeof(initLog),
             "[KONGOR NATIVE GUARD] Shared-memory Fast Bus V2 initialized successfully ('%s', %u slots, %zu bytes, Seqlock enabled).\n",
             mapName.c_str(), TELEMETRY_RING_CAPACITY, totalBytes);
    OutputDebugStringA(initLog);
}

void ShutdownTelemetrySharedMemory() {
    g_telemetryWorkerRunning = false;
    if (g_hTelemetryWorkerThread) {
        WaitForSingleObject(g_hTelemetryWorkerThread, 100);
        CloseHandle(g_hTelemetryWorkerThread);
        g_hTelemetryWorkerThread = NULL;
    }
    kongor_shm_close(&g_shmHandle);
    g_pTelemetryHeader = NULL;
    g_pTelemetryRecords = NULL;
}

extern "C" {
    __declspec(dllexport) void Kongor_NativeGuard_WriteFrameTelemetry(
        float cpuTimeMs, float gpuTimeMs, float inputLatencyMs,
        uint32_t drawCalls, uint32_t activeEntities, uint32_t particlesAlive,
        uint32_t pingMicroseconds, float packetLossPercent,
        uint32_t memoryWorkingSetMb, uint32_t audioVoicesActive,
        uint32_t serverTick, uint32_t flags)
    {
        WriteFrameTelemetry(cpuTimeMs, gpuTimeMs, inputLatencyMs, drawCalls,
                            activeEntities, particlesAlive, pingMicroseconds,
                            packetLossPercent, memoryWorkingSetMb, audioVoicesActive,
                            serverTick, flags);
    }

    __declspec(dllexport) uint64_t Kongor_NativeGuard_GetTelemetryHead() {
        return g_pTelemetryHeader ? g_pTelemetryHeader->Head : 0;
    }

    __declspec(dllexport) uint32_t Kongor_NativeGuard_GetTelemetryCapacity() {
        return TELEMETRY_RING_CAPACITY;
    }

    __declspec(dllexport) uint32_t Kongor_NativeGuard_GetStatus() {
        return (g_pTelemetryHeader != NULL) ? 1 : 0;
    }

    __declspec(dllexport) void Kongor_NativeGuard_UpdateDynamicTelemetry(
        uint32_t drawCalls, uint32_t activeEntities, uint32_t particlesAlive,
        uint32_t pingMicroseconds, uint32_t serverTick, float inputLatencyMs)
    {
        if (drawCalls > 0) g_dynamicDrawCalls.store(drawCalls, std::memory_order_relaxed);
        g_dynamicActiveEntities.store(activeEntities, std::memory_order_relaxed);
        if (particlesAlive > 0) g_dynamicParticlesAlive.store(particlesAlive, std::memory_order_relaxed);
        if (pingMicroseconds > 0) g_dynamicPingMicroseconds.store(pingMicroseconds, std::memory_order_relaxed);
        if (serverTick > 0) g_dynamicServerTick.store(serverTick, std::memory_order_relaxed);
        if (inputLatencyMs >= 0.0f) g_dynamicInputLatencyMs.store(inputLatencyMs, std::memory_order_relaxed);
    }

    __declspec(dllexport) float Kongor_NativeGuard_InjectSubTickInput(
        const wchar_t* action, int64_t hardwareTimestampQpc, uint32_t inputSequence)
    {
        LARGE_INTEGER nowQpc;
        QueryPerformanceCounter(&nowQpc);

        float measuredLatencyMs = 0.0f;
        if (hardwareTimestampQpc > 0 && g_qpcFrequency.QuadPart > 0 && nowQpc.QuadPart >= hardwareTimestampQpc) {
            double deltaSec = (double)(nowQpc.QuadPart - hardwareTimestampQpc) / (double)g_qpcFrequency.QuadPart;
            measuredLatencyMs = (float)(deltaSec * 1000.0);
            g_dynamicInputLatencyMs.store(measuredLatencyMs, std::memory_order_relaxed);
        }

        if (action && *action) {
            DispatchCommandToEngine(action, hardwareTimestampQpc > 0 ? hardwareTimestampQpc : nowQpc.QuadPart, inputSequence);
        }

        return measuredLatencyMs;
    }
}


const char* GetCanonicalGamePhaseName(uint32_t phase) {
    switch (phase) {
        case 0: return "Main Menu / Lobby (0)";
        case 1: return "Loading Map (1)";
        case 2: return "Hero Selection (2)";
        case 3: return "Match / In-Game (3)";
        case 4: return "Post-Game Summary (4)";
        default: return "Unknown";
    }
}

static bool CopyK2StringToAsciiBuffer(const void* pK2Str, char* outBuf, size_t maxLen) {
    if (!pK2Str || !outBuf || maxLen == 0) return false;
    outBuf[0] = '\0';
    const K2StringW* s = reinterpret_cast<const K2StringW*>(pK2Str);
    if (s->length == 0 || s->length > 256 || s->capacity > 4096) return false;
    const wchar_t* wptr = (s->capacity < 8) ? s->bx.buf : s->bx.ptr;
    if (!wptr) return false;
    size_t copyLen = (s->length < (maxLen - 1)) ? s->length : (maxLen - 1);
    for (size_t i = 0; i < copyLen; ++i) {
        wchar_t wc = wptr[i];
        if (wc == L'\0') {
            outBuf[i] = '\0';
            return i > 0;
        }
        outBuf[i] = (wc >= 0x20 && wc <= 0x7E) ? (char)wc : '?';
    }
    outBuf[copyLen] = '\0';
    return copyLen > 0;
}

static void OnGamePhaseChanged(int oldPhase, int newPhase) {
    if (oldPhase == 3 && newPhase != 3) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Match exit detected (Phase 3 -> Menu). Triggering full Lua garbage collection.\n");
        DispatchCommandToEngine(L"Script \"collectgarbage('collect')\"");
        g_fullGcCount.fetch_add(1, std::memory_order_relaxed);
    } else if (newPhase == 3 && oldPhase != 3) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Match combat entered (Phase -> 3). Setting smooth incremental Lua GC step multiplier.\n");
        DispatchCommandToEngine(L"Script \"collectgarbage('setpause', 100)\"");
        DispatchCommandToEngine(L"Script \"collectgarbage('setstepmul', 200)\"");
    }
}

int32_t ReadCurrentHostFrameSafe() {
    int32_t frame = 0;
    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (hK2 && !g_ppHost) {
        g_ppHost = (void**)GetProcAddress(hK2, "?g_pHost@@3PEAVCHost@@EA");
    }
    Kongor_EnterProtectedSeh();
    __try {
        if (g_ppHost && *g_ppHost) {
            uint8_t* pHost = reinterpret_cast<uint8_t*>(*g_ppHost);
            frame = *(int32_t*)(pHost + 0x14);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        frame = 0;
    }
    Kongor_LeaveProtectedSeh();
    return frame;
}

static void SampleLiveEngineStateForTelemetry(int32_t* ioLastHostFrame, double deltaSeconds, float* outRealFrameTimeMs) {
    if (!g_pTelemetryHeader) return;

    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (hK2 && !g_ppHost) {
        g_ppHost = (void**)GetProcAddress(hK2, "?g_pHost@@3PEAVCHost@@EA");
    }
    ResolveK2LuaSymbols();

    HMODULE hGameShared = GetModuleHandleW(L"game_shared_x64.dll");
    if (hGameShared && !g_ppGame) {
        g_ppGame = (void**)GetProcAddress(hGameShared, "?s_pGame@IGame@@0PEAV1@EA");
    } else if (!hGameShared) {
        g_ppGame = nullptr;
    }

    int prevPhaseTriggered = -1;
    int nextPhaseTriggered = -1;

    Kongor_EnterProtectedSeh();
    __try {
        if (g_ppLuaScriptManager && *g_ppLuaScriptManager && g_pfnCountMemoryUsed) {
            int luaBytes = g_pfnCountMemoryUsed(*g_ppLuaScriptManager);
            if (luaBytes >= 0) {
                g_cachedLuaMemoryBytes.store(static_cast<uint64_t>(luaBytes), std::memory_order_relaxed);
                g_pTelemetryHeader->LuaMemoryKb = (uint32_t)(luaBytes / 1024);
            }
        }

        if (g_ppHost && *g_ppHost) {
            uint8_t* pHost = reinterpret_cast<uint8_t*>(*g_ppHost);
            int32_t curHostFrame = *(int32_t*)(pHost + 0x14);
            static int32_t s_prevHeartbeatHostFrame = -1;
            if (curHostFrame != s_prevHeartbeatHostFrame) {
                s_prevHeartbeatHostFrame = curHostFrame;
                g_lastMainThreadHeartbeatTick.store(GetTickCount64(), std::memory_order_relaxed);
            }
            (void)UpdateAndGetMsSinceHostFrameAdvance(curHostFrame);
            if (curHostFrame > 10) {
                static std::atomic<bool> s_frameSafePointReached{false};
                if (!s_frameSafePointReached.exchange(true)) {
                    Kongor::PatchController::OnMainThreadSafePoint(nullptr);
                }
            }
            if (ioLastHostFrame && outRealFrameTimeMs) {
                int32_t prevFrame = *ioLastHostFrame;
                *ioLastHostFrame = curHostFrame;
                if (prevFrame > 0 && curHostFrame > prevFrame && deltaSeconds > 0.001) {
                    int32_t deltaFrames = curHostFrame - prevFrame;
                    *outRealFrameTimeMs = (float)((deltaSeconds * 1000.0) / (double)deltaFrames);
                } else {
                    uint32_t uiFrameLen = *(uint32_t*)(pHost + 0x10);
                    if (uiFrameLen > 0 && uiFrameLen < 500) {
                        *outRealFrameTimeMs = (float)uiFrameLen;
                    }
                }
            }

            uint8_t* pHostClient = nullptr;
            if (!IsDedicatedServerProcessCached() && IsK2ClientModule(hK2)) {
                uint8_t* pMapHead = *(uint8_t**)(pHost + 0xF0);
                uint64_t mapSize = *(uint64_t*)(pHost + 0xF8);
                if (pMapHead && mapSize > 0 && mapSize <= 64) {
                    uint8_t* pFirstNode = *(uint8_t**)pMapHead;
                    if (pFirstNode && pFirstNode != pMapHead) {
                        pHostClient = *(uint8_t**)(pFirstNode + 0x28);
                    }
                }
            }

            if (pHostClient) {
                int32_t clientState = *(int32_t*)(pHostClient + 0x14);

                char nickBuf[16] = {0};
                if (CopyK2StringToAsciiBuffer(pHostClient + 0x120, nickBuf, sizeof(nickBuf)) && nickBuf[0] != '\0') {
                    if (_stricmp(nickBuf, "client_default_name") != 0 &&
                        _stricmp(nickBuf, "UnnamedPlayer") != 0 &&
                        _stricmp(nickBuf, "UnnamedNewbie") != 0) {
                        strncpy_s(g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName), nickBuf, _TRUNCATE);
                    }
                }

                uint8_t* pWorld = *(uint8_t**)(pHostClient + 0x87C0);
                char worldNameBuf[16] = {0};
                if (pWorld && clientState > 0) {
                    CopyK2StringToAsciiBuffer(pWorld + 0x18, worldNameBuf, sizeof(worldNameBuf));
                }

                uint32_t matchId = 0;
                uint32_t cgamePhase = 0;
                if (g_ppGame && *g_ppGame && clientState > 0) {
                    uint8_t* pGame = reinterpret_cast<uint8_t*>(*g_ppGame);
                    uint8_t* pGameInfo = *(uint8_t**)(pGame + 0xB0);
                    if (pGameInfo) {
                        cgamePhase = *(uint32_t*)(pGameInfo + 0xB4);
                        matchId = *(uint32_t*)(pGameInfo + 0xC0);
                    }
                }

                uint32_t canonicalPhase = 0;
                if (clientState <= 0) {
                    canonicalPhase = 0; // Main Menu / Login / Lobby
                    g_pTelemetryHeader->CurrentMap[0] = '\0';
                    g_pTelemetryHeader->ActiveMatchId = 0;
                    g_pTelemetryHeader->ConnectedPlayers = 0;
                } else if (clientState >= 1 && clientState <= 5) {
                    canonicalPhase = 1; // Loading / Connecting
                    if (worldNameBuf[0] != '\0') {
                        strncpy_s(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap), worldNameBuf, _TRUNCATE);
                    }
                    g_pTelemetryHeader->ActiveMatchId = matchId;
                    g_pTelemetryHeader->ConnectedPlayers = 1;
                } else {
                    // clientState >= 6 (IN_GAME): cgamePhase 0 = loading/waiting, 1..4 = hero pick/setup,
                    // 5 = pre-match countdown in-world, 6 = active match combat, 7 = post-game summary.
                    if (cgamePhase == 0) {
                        canonicalPhase = 1; // Loading Into Match
                    } else if (cgamePhase >= 1 && cgamePhase <= 4) {
                        canonicalPhase = 2; // Hero Selection / Match Setup
                    } else if (cgamePhase == 7) {
                        canonicalPhase = 4; // Post-Game Summary
                    } else {
                        canonicalPhase = 3; // Active Match / Pre-Match Countdown (5, 6)
                    }
                    if (worldNameBuf[0] != '\0') {
                        strncpy_s(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap), worldNameBuf, _TRUNCATE);
                    }
                    g_pTelemetryHeader->ActiveMatchId = matchId;
                    if (g_pTelemetryHeader->ConnectedPlayers == 0) {
                        g_pTelemetryHeader->ConnectedPlayers = 1;
                    }
                }

                g_pTelemetryHeader->GamePhase = canonicalPhase;
                // clientState == 1 is Main Menu PreloadWorld (handled by prewarm_foreground_busy so ESC-skip can resume);
                // clientState >= 2 or canonicalPhase >= 2 is a real server connect / hero pick / match -> permanently cancel PostIntro warmup.
                Kongor::SetPrewarmerMatchActive(clientState >= 2 || canonicalPhase >= 2);
                int prevKnown = g_lastKnownGamePhase.exchange((int)canonicalPhase, std::memory_order_relaxed);
                if (prevKnown >= 0 && prevKnown != (int)canonicalPhase) {
                    prevPhaseTriggered = prevKnown;
                    nextPhaseTriggered = (int)canonicalPhase;
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Safe SEH recovery during transient engine teardown/allocation windows
    }
    Kongor_LeaveProtectedSeh();

    if (prevPhaseTriggered >= 0 && nextPhaseTriggered >= 0) {
        OnGamePhaseChanged(prevPhaseTriggered, nextPhaseTriggered);
    }
}

