// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

// ============================================================================
// File: linux_native_guard.cpp
// System: Heroes of Newerth - Dedicated Server Native Guard (Linux Port)
// Subsystem: POSIX Shared Memory Fast Bus V2 (Seqlock), Unix Socket IPC & Crash Sentinel
// Target: libkongor_native_guard.so / libk2_native_guard.so
// Note: Built with -nostdlib++ -fno-exceptions -fno-rtti so LD_PRELOAD into
//       hon-x86_64-server (which links LLVM libc++.so.1 + libc++abi.so.1) never
//       loads GNU libstdc++.so.6 or triggers std::ios_base::Init ABI collisions.
// ============================================================================

#if !defined(_WIN32)

#if !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif

#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/syscall.h>
#include <fcntl.h>
#include <unistd.h>
#include <signal.h>
#include <setjmp.h>
#include <sys/uio.h>
#include <pthread.h>
#include <poll.h>
#include <time.h>
#include <ucontext.h>
#include <dlfcn.h>
#include <link.h>
#include <errno.h>
#include <strings.h>

#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <cmath>
#include <cwchar>
#include <atomic>
#include "linux_queue_arcade.h"

namespace KongorLinuxDisplay {
void OnClientConsoleFrame(void* consolePtr, void (*execFn)(void*, const void*, unsigned int)) noexcept;
bool TryDispatchDisplayConsoleCommandW(const wchar_t* cmdLine, void* consolePtr, void (*execFn)(void*, const void*, unsigned int), unsigned int flags) noexcept;
} // namespace KongorLinuxDisplay

// ============================================================================
// KONGOR HIGH-SPEED SHARED-MEMORY FAST BUS V2 (192-BYTE 3-CACHE-LINE SEQLOCK)
// Matches native/include/kongor_shared_mem.h & NEXUS.Contracts.Telemetry
// ============================================================================

#pragma pack(push, 1)

struct FrameTelemetryRecord {
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
};
static_assert(sizeof(FrameTelemetryRecord) == 64, "FrameTelemetryRecord must be exactly 64 bytes");

struct KongorTelemetryRingHeaderV2 {
    // Cache Line 0 (Bytes 0..63): V1 Base Header (Backwards-Compatible)
    uint32_t Magic;              // 0x4B4F4E47 ('KONG')
    uint32_t Version;            // 2
    uint32_t Capacity;           // 4096
    uint32_t RecordSizeBytes;    // 64
    uint64_t Head;               // Monotonic write index
    uint64_t Tail;               // Monotonic read index
    uint64_t ProducerProcessId;  // PID of producer process
    int64_t  StartTimestampQpc;  // Nanoseconds on Linux
    int64_t  FrequencyQpc;       // 1,000,000,000 (1 GHz) on Linux
    uint64_t ReservedFlags;      // Global engine bitflags (PTT, status)

    // Cache Line 1 (Bytes 64..127): Seqlock Synchronization & Live Engine State
    uint64_t LatestSequence;     // [64..71]  64-bit Seqlock: odd = write in progress, even = committed
    int64_t  SnapshotTimestamp;  // [72..79]  Timestamp of latest snapshot write
    uint32_t ProducerThreadId;   // [80..83]  Thread ID of producer for re-entrancy avoidance
    uint32_t GamePhase;          // [84..87]  Live engine phase
    uint32_t ActiveMatchId;      // [88..91]  Active Match ID
    uint32_t ConnectedPlayers;   // [92..95]  Connected player count
    uint32_t LuaMemoryKb;        // [96..99]  Live Lua 5.1 VM heap usage in KB
    char     CurrentMap[16];     // [100..115] Null-terminated active map name
    char     PlayerName[12];     // [116..127] Null-terminated active player/server name

    // Cache Line 2 (Bytes 128..191): Atomic Latest Snapshot (Single Cache Line)
    FrameTelemetryRecord LatestRecord;
};

using TelemetryRingHeader = KongorTelemetryRingHeaderV2;

static_assert(sizeof(KongorTelemetryRingHeaderV2) == 192, "KongorTelemetryRingHeaderV2 must be 192 bytes");
static_assert(offsetof(KongorTelemetryRingHeaderV2, GamePhase) == 84, "GamePhase must be at byte 84");
static_assert(offsetof(KongorTelemetryRingHeaderV2, ActiveMatchId) == 88, "ActiveMatchId must be at byte 88");
static_assert(offsetof(KongorTelemetryRingHeaderV2, ConnectedPlayers) == 92, "ConnectedPlayers must be at byte 92");
static_assert(offsetof(KongorTelemetryRingHeaderV2, LuaMemoryKb) == 96, "LuaMemoryKb must be at byte 96");
static_assert(offsetof(KongorTelemetryRingHeaderV2, CurrentMap) == 100, "CurrentMap must be at byte 100");
static_assert(offsetof(KongorTelemetryRingHeaderV2, PlayerName) == 116, "PlayerName must be at byte 116");
static_assert(offsetof(KongorTelemetryRingHeaderV2, LatestRecord) == 128, "LatestRecord must be at byte 128");
#pragma pack(pop)

static constexpr uint32_t KONGOR_SHM_MAGIC = 0x4B4F4E47;
static constexpr uint32_t KONGOR_SHM_VERSION_2 = 2;
static constexpr uint32_t TELEMETRY_RING_CAPACITY = 4096;
static constexpr uint32_t TELEMETRY_RING_MASK = TELEMETRY_RING_CAPACITY - 1;

static int g_shmFd = -1;
static int g_serverPort = 0;
static char g_shmName[128] = { 0 };
static void* g_pTelemetryView = nullptr;
static KongorTelemetryRingHeaderV2* g_pTelemetryHeader = nullptr;
static FrameTelemetryRecord* g_pTelemetryRecords = nullptr;
static std::atomic<uint64_t> g_telemetryFrameCounter{ 0 };
static std::atomic<bool> g_killSwitchActive{ false };

static pthread_t g_telemetryWorkerThread = 0;
static std::atomic<bool> g_telemetryWorkerRunning{ false };

static int g_ipcListenSock = -1;
static char g_ipcSocketPath[108] = { 0 };
static char g_ipcCompatSymlinkPath[108] = { 0 };
static pthread_t g_ipcServerThread = 0;
static std::atomic<bool> g_ipcServerRunning{ false };

// Dynamic Telemetry Metrics Stamped by Server Hooks (0 active entities while idle)
static std::atomic<uint32_t> g_dynamicDrawCalls{ 0 };
static std::atomic<uint32_t> g_dynamicActiveEntities{ 0 };
static std::atomic<uint32_t> g_dynamicParticlesAlive{ 0 };
static std::atomic<uint32_t> g_dynamicPingMicroseconds{ 5000 };
static std::atomic<float>    g_dynamicPacketLossPercent{ 0.0f };
static std::atomic<uint32_t> g_dynamicServerTick{ 0 };
static std::atomic<uint32_t> g_dynamicAudioVoices{ 0 };
static std::atomic<float>    g_dynamicInputLatencyMs{ 0.5f };
static std::atomic<uint64_t> g_voicePttFlags{ 0 };
static std::atomic<uint64_t> g_telemetryHitchCount{ 0 };
static std::atomic<uint64_t> g_telemetryCpuStallCount{ 0 };
static std::atomic<uint32_t> g_telemetryPeakFrameTimeUs{ 0 };
static std::atomic<uint64_t> g_linuxKillPacketClampCount{ 0 };
static std::atomic<uint64_t> g_linuxKillPacketExpandCount{ 0 };

// Fixed-capacity POD command ring (zero heap / zero libstdc++ dependency)
static constexpr size_t CMD_QUEUE_CAPACITY = 64;
static constexpr size_t CMD_MAX_LEN = 1024;
static pthread_mutex_t g_commandQueueMutex = PTHREAD_MUTEX_INITIALIZER;
static char g_pendingConsoleCommands[CMD_QUEUE_CAPACITY][CMD_MAX_LEN];
static size_t g_pendingCmdCount = 0;

static bool NativeGuard_IsDisabledByEnv() {
    const char* val = std::getenv("KONGOR_DISABLE_LINUX_NATIVE_GUARD");
    if (!val || val[0] == '\0') return false;
    return (std::strcmp(val, "1") == 0 ||
            strcasecmp(val, "true") == 0 ||
            strcasecmp(val, "yes") == 0);
}

static int64_t GetCurrentNanoseconds() {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000000LL + (int64_t)ts.tv_nsec;
}

static uint32_t GetCurrentLinuxThreadId() {
#ifdef SYS_gettid
    return (uint32_t)syscall(SYS_gettid);
#else
    return (uint32_t)getpid();
#endif
}

static uint32_t GetLinuxWorkingSetMb() {
    FILE* f = fopen("/proc/self/statm", "r");
    if (!f) return 0;

    unsigned long size = 0, resident = 0;
    if (fscanf(f, "%lu %lu", &size, &resident) == 2) {
        fclose(f);
        long pageSize = sysconf(_SC_PAGESIZE);
        return (uint32_t)((resident * (unsigned long)pageSize) / (1024 * 1024));
    }
    fclose(f);
    return 0;
}

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
    if (!g_pTelemetryHeader || !g_pTelemetryRecords) return;

    int64_t nowNs = GetCurrentNanoseconds();
    uint64_t frameIdx = ++g_telemetryFrameCounter;

    if (cpuTimeMs > 5.0f) {
        flags |= (1U << 0); // KONGOR_FRAME_FLAG_HITCH
        g_telemetryHitchCount.fetch_add(1, std::memory_order_relaxed);
    }
    if (cpuTimeMs > 16.6f) {
        flags |= (1U << 1); // KONGOR_FRAME_FLAG_CPU_STALL
        g_telemetryCpuStallCount.fetch_add(1, std::memory_order_relaxed);
    }
    uint32_t frameTimeUs = cpuTimeMs > 0.0f ? (uint32_t)(cpuTimeMs * 1000.0f) : 0;
    uint32_t prevPeakUs = g_telemetryPeakFrameTimeUs.load(std::memory_order_relaxed);
    while (frameTimeUs > prevPeakUs &&
           !g_telemetryPeakFrameTimeUs.compare_exchange_weak(prevPeakUs, frameTimeUs, std::memory_order_relaxed)) {
    }

    FrameTelemetryRecord rec{};
    rec.FrameIndex = frameIdx;
    rec.TimestampQpc = nowNs;
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

    g_pTelemetryHeader->ReservedFlags = g_voicePttFlags.load(std::memory_order_relaxed);
    g_pTelemetryHeader->ProducerThreadId = GetCurrentLinuxThreadId();

    // 1. Mark write in progress (Odd sequence)
    __atomic_add_fetch(&g_pTelemetryHeader->LatestSequence, 1ULL, __ATOMIC_ACQ_REL);

    // 2. Write atomic snapshot to Cache Line 2 and circular ring slot
    g_pTelemetryHeader->LatestRecord = rec;
    g_pTelemetryHeader->SnapshotTimestamp = nowNs;

    uint64_t currentHead = __atomic_load_n(&g_pTelemetryHeader->Head, __ATOMIC_RELAXED);
    uint32_t slot = (uint32_t)(currentHead & TELEMETRY_RING_MASK);
    g_pTelemetryRecords[slot] = rec;

    // 3. Hardware commit fence
    std::atomic_thread_fence(std::memory_order_seq_cst);

    // 4. Mark write committed (Even sequence)
    __atomic_add_fetch(&g_pTelemetryHeader->LatestSequence, 1ULL, __ATOMIC_RELEASE);

    // 5. Publish circular Head
    __atomic_add_fetch(&g_pTelemetryHeader->Head, 1ULL, __ATOMIC_RELEASE);
}

static void* NativeGuardTelemetrySamplerWorker(void*) {
    int64_t lastNs = GetCurrentNanoseconds();

    while (g_telemetryWorkerRunning.load(std::memory_order_relaxed) && g_pTelemetryHeader != nullptr) {
        usleep(10000); // 10 ms sleep (100 Hz server sampling)

        int64_t nowNs = GetCurrentNanoseconds();
        double deltaSeconds = (double)(nowNs - lastNs) / 1000000000.0;
        if (deltaSeconds <= 0.0) deltaSeconds = 0.010;
        float frameTimeMs = (float)(deltaSeconds * 1000.0);
        lastNs = nowNs;

        uint32_t workingSetMb = GetLinuxWorkingSetMb();
        uint32_t tick = g_dynamicServerTick.load(std::memory_order_relaxed);
        if (tick == 0) {
            tick = (uint32_t)(g_telemetryFrameCounter.load(std::memory_order_relaxed) / 2);
        }

        WriteFrameTelemetry(
            frameTimeMs > 0.0f ? frameTimeMs : 1.0f,
            0.0f, // No GPU on headless dedicated server
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
    }

    return nullptr;
}

static size_t ReadLinuxProcessCommandLine(char* outBuf, size_t outCap) {
    if (!outBuf || outCap == 0) return 0;
    outBuf[0] = '\0';
    int fd = open("/proc/self/cmdline", O_RDONLY);
    if (fd < 0) return 0;

    ssize_t n = read(fd, outBuf, outCap - 1);
    close(fd);
    if (n <= 0) {
        outBuf[0] = '\0';
        return 0;
    }

    for (ssize_t i = 0; i < n; ++i) {
        if (outBuf[i] == '\0') outBuf[i] = ' ';
    }
    outBuf[n] = '\0';
    return (size_t)n;
}

static int ResolveDedicatedServerPortFromCommandLine(const char* cmdLine) {
    if (!cmdLine || cmdLine[0] == '\0') return 0;

    // 1. Explicit -port <N> or port:<N>
    const char* portPos = std::strstr(cmdLine, "-port ");
    size_t skipLen = 6;
    if (!portPos) {
        portPos = std::strstr(cmdLine, "port:");
        skipLen = 5;
    }
    if (portPos) {
        int port = std::atoi(portPos + skipLen);
        if (port > 0 && port <= 65535) return port;
    }

    // 2. +Set svr_port <N> / Set svr_port <N> / svr_port=<N>
    const char* svrPortPos = std::strstr(cmdLine, "svr_port");
    if (svrPortPos) {
        const char* p = svrPortPos + 8;
        while (*p == ' ' || *p == '=' || *p == '"') p++;
        int port = std::atoi(p);
        if (port > 0 && port <= 65535) return port;
    }

    // 3. hon-server-native-<N> or hon-server-<N>
    const char* pipeNativePos = std::strstr(cmdLine, "hon-server-native-");
    if (pipeNativePos) {
        int port = std::atoi(pipeNativePos + 18);
        if (port > 0 && port <= 65535) return port;
    }
    const char* pipePos = std::strstr(cmdLine, "hon-server-");
    if (pipePos) {
        int port = std::atoi(pipePos + 11);
        if (port > 0 && port <= 65535) return port;
    }

    // 4. Environment variable fallback (COMPEL_PORT)
    const char* envPort = std::getenv("COMPEL_PORT");
    if (envPort && envPort[0] != '\0') {
        int port = std::atoi(envPort);
        if (port > 0 && port <= 65535) return port;
    }

    // 5. Dedicated server launched without explicit port defaults to 11235
    if (std::strstr(cmdLine, "-dedicated") != nullptr || std::strstr(cmdLine, "-server") != nullptr) {
        return 11235;
    }

    return 0;
}

static void InitializeTelemetrySharedMemory() {
    pid_t currentPid = getpid();
    char cmdLine[4096];
    ReadLinuxProcessCommandLine(cmdLine, sizeof(cmdLine));
    int port = ResolveDedicatedServerPortFromCommandLine(cmdLine);
    g_serverPort = port;

    if (port > 0) {
        std::snprintf(g_shmName, sizeof(g_shmName), "/KongorServerTelemetry_%d", port);
    } else {
        std::snprintf(g_shmName, sizeof(g_shmName), "/KongorClientTelemetry_%d", (int)currentPid);
    }

    size_t totalBytes = sizeof(KongorTelemetryRingHeaderV2) + sizeof(FrameTelemetryRecord) * TELEMETRY_RING_CAPACITY;

    // [L2-P0-04] Owner-only 0600 permissions on POSIX shared memory (never world-writable 0666).
    mode_t prevShmMask = umask(0077);
    g_shmFd = shm_open(g_shmName, O_CREAT | O_RDWR, 0600);
    umask(prevShmMask);
    if (g_shmFd < 0) {
        std::fprintf(stderr, "[NativeGuard:Linux] shm_open failed for %s: %s\n", g_shmName, strerror(errno));
        return;
    }
    (void)fchmod(g_shmFd, 0600);

    if (ftruncate(g_shmFd, static_cast<off_t>(totalBytes)) != 0) {
        std::fprintf(stderr, "[NativeGuard:Linux] ftruncate failed for %s: %s\n", g_shmName, strerror(errno));
        close(g_shmFd);
        g_shmFd = -1;
        return;
    }

    g_pTelemetryView = mmap(nullptr, totalBytes, PROT_READ | PROT_WRITE, MAP_SHARED, g_shmFd, 0);
    if (g_pTelemetryView == MAP_FAILED) {
        std::fprintf(stderr, "[NativeGuard:Linux] mmap failed for %s: %s\n", g_shmName, strerror(errno));
        g_pTelemetryView = nullptr;
        close(g_shmFd);
        g_shmFd = -1;
        return;
    }

    g_pTelemetryHeader = reinterpret_cast<KongorTelemetryRingHeaderV2*>(g_pTelemetryView);
    g_pTelemetryRecords = reinterpret_cast<FrameTelemetryRecord*>(
        reinterpret_cast<uint8_t*>(g_pTelemetryView) + sizeof(KongorTelemetryRingHeaderV2));

    std::memset(g_pTelemetryView, 0, totalBytes);
    int64_t startNs = GetCurrentNanoseconds();

    g_pTelemetryHeader->Magic = KONGOR_SHM_MAGIC;
    g_pTelemetryHeader->Version = KONGOR_SHM_VERSION_2;
    g_pTelemetryHeader->Capacity = TELEMETRY_RING_CAPACITY;
    g_pTelemetryHeader->RecordSizeBytes = sizeof(FrameTelemetryRecord);
    g_pTelemetryHeader->ProducerProcessId = (uint64_t)currentPid;
    g_pTelemetryHeader->StartTimestampQpc = startNs;
    g_pTelemetryHeader->FrequencyQpc = 1000000000LL; // 1 GHz nanosecond frequency
    g_pTelemetryHeader->Head = 0;
    g_pTelemetryHeader->Tail = 0;
    g_pTelemetryHeader->LatestSequence = 0;
    g_pTelemetryHeader->SnapshotTimestamp = startNs;
    g_pTelemetryHeader->ProducerThreadId = GetCurrentLinuxThreadId();
    g_pTelemetryHeader->GamePhase = 0;
    g_pTelemetryHeader->ActiveMatchId = 0;
    g_pTelemetryHeader->ConnectedPlayers = 0;
    g_pTelemetryHeader->LuaMemoryKb = 0;
    std::snprintf(g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName), "%s", "KongorSrv");

    std::atomic_thread_fence(std::memory_order_seq_cst);

    std::fprintf(stdout, "[NativeGuard:Linux] Telemetry Fast Bus V2 mapped at /dev/shm%s (%zu bytes, PID: %d, Port: %d)\n",
                 g_shmName, totalBytes, (int)currentPid, port);
    std::fflush(stdout);

    g_telemetryWorkerRunning.store(true, std::memory_order_relaxed);
    pthread_create(&g_telemetryWorkerThread, nullptr, NativeGuardTelemetrySamplerWorker, nullptr);
}

static void ShutdownTelemetrySharedMemory() {
    g_telemetryWorkerRunning.store(false, std::memory_order_relaxed);
    if (g_telemetryWorkerThread != 0) {
        pthread_join(g_telemetryWorkerThread, nullptr);
        g_telemetryWorkerThread = 0;
    }

    if (g_pTelemetryView && g_pTelemetryView != MAP_FAILED) {
        size_t totalBytes = sizeof(KongorTelemetryRingHeaderV2) + sizeof(FrameTelemetryRecord) * TELEMETRY_RING_CAPACITY;
        munmap(g_pTelemetryView, totalBytes);
        g_pTelemetryView = nullptr;
        g_pTelemetryHeader = nullptr;
        g_pTelemetryRecords = nullptr;
    }

    if (g_shmFd >= 0) {
        close(g_shmFd);
        g_shmFd = -1;
    }

    if (g_shmName[0] != '\0') {
        shm_unlink(g_shmName);
    }
}

// ============================================================================
// UNIX DOMAIN SOCKET IPC SERVER (/tmp/hon-server-native-<port>.sock)
// Binary Frame Format: [0x4D, 0x57, OpCode(u16), PayloadLen(i32), ReqId(u32), Payload]
// ============================================================================

static size_t EscapeJsonCString(const char* input, size_t inputLen, char* outBuf, size_t outCap) {
    if (!outBuf || outCap == 0) return 0;
    size_t pos = 0;
    for (size_t i = 0; i < inputLen && pos + 8 < outCap; ++i) {
        unsigned char c = static_cast<unsigned char>(input[i]);
        switch (c) {
            case '\"': outBuf[pos++] = '\\'; outBuf[pos++] = '\"'; break;
            case '\\': outBuf[pos++] = '\\'; outBuf[pos++] = '\\'; break;
            case '\b': outBuf[pos++] = '\\'; outBuf[pos++] = 'b';  break;
            case '\f': outBuf[pos++] = '\\'; outBuf[pos++] = 'f';  break;
            case '\n': outBuf[pos++] = '\\'; outBuf[pos++] = 'n';  break;
            case '\r': outBuf[pos++] = '\\'; outBuf[pos++] = 'r';  break;
            case '\t': outBuf[pos++] = '\\'; outBuf[pos++] = 't';  break;
            default:
                if (c < 0x20) {
                    pos += (size_t)std::snprintf(outBuf + pos, outCap - pos, "\\u%04x", (unsigned int)c);
                } else {
                    outBuf[pos++] = (char)c;
                }
                break;
        }
    }
    outBuf[pos] = '\0';
    return pos;
}

static void EnqueueLinuxConsoleCommand(const char* cmd, size_t len) {
    if (!cmd || len == 0) return;
    pthread_mutex_lock(&g_commandQueueMutex);
    if (g_pendingCmdCount < CMD_QUEUE_CAPACITY) {
        size_t copyLen = len < (CMD_MAX_LEN - 1) ? len : (CMD_MAX_LEN - 1);
        std::memcpy(g_pendingConsoleCommands[g_pendingCmdCount], cmd, copyLen);
        g_pendingConsoleCommands[g_pendingCmdCount][copyLen] = '\0';
        g_pendingCmdCount++;
    }
    pthread_mutex_unlock(&g_commandQueueMutex);
}

static bool VerifyLinuxIpcAuthToken(
    const char* candidatePayload,
    size_t candidateLen,
    const char* expectedToken,
    size_t expectedLen) noexcept
{
    if (!expectedToken || expectedLen == 0) return true;
    if (!candidatePayload || candidateLen == 0) return false;

    size_t start = 0;
    size_t end = candidateLen;
    while (start < end && (candidatePayload[start] == ' ' || candidatePayload[start] == '\t' ||
                           candidatePayload[start] == '\r' || candidatePayload[start] == '\n' ||
                           candidatePayload[start] == '\0')) {
        ++start;
    }
    while (end > start && (candidatePayload[end - 1] == ' ' || candidatePayload[end - 1] == '\t' ||
                           candidatePayload[end - 1] == '\r' || candidatePayload[end - 1] == '\n' ||
                           candidatePayload[end - 1] == '\0')) {
        --end;
    }

    const char* tokPtr = nullptr;
    size_t tokLen = 0;
    if (start < end && candidatePayload[start] == '{') {
        const char* p = candidatePayload + start;
        const char* lim = candidatePayload + end;
        const char kKey[] = "\"token\"";
        const size_t kKeyLen = sizeof(kKey) - 1;
        const char* foundKey = nullptr;
        for (const char* cur = p; cur + kKeyLen <= lim; ++cur) {
            if (std::memcmp(cur, kKey, kKeyLen) == 0) {
                foundKey = cur + kKeyLen;
                break;
            }
        }
        if (foundKey) {
            const char* colon = foundKey;
            while (colon < lim && (*colon == ' ' || *colon == '\t' || *colon == '\r' || *colon == '\n')) ++colon;
            if (colon < lim && *colon == ':') {
                ++colon;
                while (colon < lim && (*colon == ' ' || *colon == '\t' || *colon == '\r' || *colon == '\n')) ++colon;
                if (colon < lim && *colon == '"') {
                    ++colon;
                    const char* closeQuote = colon;
                    while (closeQuote < lim && *closeQuote != '"') {
                        if (*closeQuote == '\\' && (closeQuote + 1) < lim) ++closeQuote;
                        ++closeQuote;
                    }
                    if (closeQuote < lim && *closeQuote == '"') {
                        tokPtr = colon;
                        tokLen = static_cast<size_t>(closeQuote - colon);
                    }
                }
            }
        }
    } else if (start < end) {
        tokPtr = candidatePayload + start;
        tokLen = end - start;
    }

    volatile uint32_t diff = static_cast<uint32_t>(tokLen ^ expectedLen);
    for (size_t i = 0; i < expectedLen; ++i) {
        unsigned char c = (tokPtr != nullptr && i < tokLen) ? static_cast<unsigned char>(tokPtr[i]) : 0u;
        unsigned char e = static_cast<unsigned char>(expectedToken[i]);
        diff |= static_cast<uint32_t>(c ^ e);
    }
    return (diff == 0u) && (tokPtr != nullptr);
}

static void WriteLinuxServerKeyframe(int port);

static void HandleIpcClientConnection(int clientFd) {
    // [L2-P0-04] Enforce SO_PEERCRED UID check: only same-UID or root may issue native server IPC commands.
#ifdef SO_PEERCRED
    struct ucred peerCred{};
    socklen_t credLen = sizeof(peerCred);
    if (getsockopt(clientFd, SOL_SOCKET, SO_PEERCRED, &peerCred, &credLen) == 0) {
        uid_t myUid = geteuid();
        if (peerCred.uid != myUid && peerCred.uid != 0) {
            std::fprintf(stderr, "[NativeGuard:Linux] SECURITY: Rejected Unix IPC connection from unauthorized UID %u (expected %u)\n",
                         (unsigned)peerCred.uid, (unsigned)myUid);
            return;
        }
    }
#endif

    const char* expectedToken = std::getenv("KONGOR_IPC_TOKEN");
    const size_t expectedTokenLen = expectedToken ? std::strlen(expectedToken) : 0;
    bool clientAuthenticated = (expectedTokenLen == 0);

    static constexpr size_t STREAM_CAP = 65536;
    uint8_t streamBuffer[STREAM_CAP];
    size_t streamLen = 0;
    uint8_t readBuf[4096];

    while (g_ipcServerRunning.load(std::memory_order_relaxed)) {
        struct pollfd pfd{};
        pfd.fd = clientFd;
        pfd.events = POLLIN;
        int pr = poll(&pfd, 1, 250);
        if (pr < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (pr == 0) continue;

        ssize_t bytesRead = recv(clientFd, readBuf, sizeof(readBuf), 0);
        if (bytesRead <= 0) break;

        if (streamLen + (size_t)bytesRead > STREAM_CAP) {
            streamLen = 0;
            continue;
        }
        std::memcpy(streamBuffer + streamLen, readBuf, (size_t)bytesRead);
        streamLen += (size_t)bytesRead;

        while (streamLen >= 12) {
            if (streamBuffer[0] != 0x4D || streamBuffer[1] != 0x57) {
                streamLen = 0;
                break;
            }

            uint16_t opCode = 0;
            int32_t payloadLen = 0;
            uint32_t reqId = 0;
            std::memcpy(&opCode, &streamBuffer[2], sizeof(uint16_t));
            std::memcpy(&payloadLen, &streamBuffer[4], sizeof(int32_t));
            std::memcpy(&reqId, &streamBuffer[8], sizeof(uint32_t));

            if (payloadLen < 0 || (size_t)(12 + payloadLen) > STREAM_CAP) {
                streamLen = 0;
                break;
            }
            if (streamLen < static_cast<size_t>(12 + payloadLen)) {
                break;
            }

            char payloadBuf[CMD_MAX_LEN];
            size_t copyPayloadLen = (size_t)payloadLen < (sizeof(payloadBuf) - 1)
                ? (size_t)payloadLen
                : (sizeof(payloadBuf) - 1);
            if (copyPayloadLen > 0) {
                std::memcpy(payloadBuf, &streamBuffer[12], copyPayloadLen);
            }
            payloadBuf[copyPayloadLen] = '\0';

            size_t consumed = 12 + (size_t)payloadLen;
            if (streamLen > consumed) {
                std::memmove(streamBuffer, streamBuffer + consumed, streamLen - consumed);
            }
            streamLen -= consumed;

            uint16_t respOpCode = 0x0004;
            char respBuf[2048];
            respBuf[0] = '\0';

            if (!clientAuthenticated && opCode != 0x0001 && opCode != 0x0010) {
                respOpCode = 0x0011;
                std::snprintf(respBuf, sizeof(respBuf),
                    "{\"authenticated\":false,\"error\":\"Unauthorized: send opcode 0x0010 with KONGOR_IPC_TOKEN first\"}");
            } else {
            switch (opCode) {
                case 0x0001: // Ping
                    respOpCode = 0x0002;
                    std::snprintf(respBuf, sizeof(respBuf), "PONG");
                    break;
                case 0x0010: { // Authenticate
                    respOpCode = 0x0011;
                    bool ok = VerifyLinuxIpcAuthToken(payloadBuf, copyPayloadLen, expectedToken, expectedTokenLen);
                    clientAuthenticated = ok;
                    if (ok) {
                        std::snprintf(respBuf, sizeof(respBuf), "{\"authenticated\":true,\"message\":\"Authorized\"}");
                    } else {
                        std::fprintf(stderr, "[NativeGuard:Linux] SECURITY: Rejected invalid KONGOR_IPC_TOKEN on opcode 0x0010\n");
                        std::snprintf(respBuf, sizeof(respBuf), "{\"authenticated\":false,\"error\":\"Invalid KONGOR_IPC_TOKEN\"}");
                    }
                    break;
                }
                case 0x0002:
                case 0x0003: { // ExecuteCommand
                    respOpCode = 0x0004;
                    // satisfies: OBL-QA-LINUX-05
                    if (!::Kongor::ShouldAllowUnprivilegedIpcCommandBufferA(payloadBuf, copyPayloadLen)) {
                        std::fprintf(stderr,
                            "[NativeGuard:Linux] SECURITY: Blocked privileged console command over IPC: %s\n",
                            payloadBuf);
                        std::snprintf(respBuf, sizeof(respBuf),
                            "{\"success\":false,\"error\":\"Blocked privileged console command over IPC\"}");
                        break;
                    }
                    EnqueueLinuxConsoleCommand(payloadBuf, copyPayloadLen);
                    char escaped[1536];
                    EscapeJsonCString(payloadBuf, copyPayloadLen, escaped, sizeof(escaped));
                    std::snprintf(respBuf, sizeof(respBuf), "{\"success\":true,\"command\":\"%s\"}", escaped);
                    break;
                }
                case 0x0005:
                case 0x0009: { // GetEngineState
                    respOpCode = 0x0006;
                    double fps = 20.0;
                    uint32_t memMb = GetLinuxWorkingSetMb();
                    uint32_t gamePhase = 0;
                    uint32_t activeMatchId = 0;
                    uint32_t connectedPlayers = 0;
                    uint32_t luaMemKb = 0;
                    uint32_t activeEntities = g_dynamicActiveEntities.load(std::memory_order_relaxed);
                    uint32_t serverTick = g_dynamicServerTick.load(std::memory_order_relaxed);
                    char currentMapStr[32] = { 0 };
                    char playerNameStr[32] = "KongorSrv";
                    uint64_t uptimeSec = 0;

                    if (g_pTelemetryHeader) {
                        float cpuMs = g_pTelemetryHeader->LatestRecord.CpuFrameTimeMs;
                        if (cpuMs > 0.001f) fps = 1000.0 / cpuMs;
                        if (g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb > 0) {
                            memMb = g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb;
                        }
                        if (g_pTelemetryHeader->LatestRecord.ActiveEntities > 0) {
                            activeEntities = g_pTelemetryHeader->LatestRecord.ActiveEntities;
                        }
                        if (g_pTelemetryHeader->LatestRecord.ServerTick > 0) {
                            serverTick = g_pTelemetryHeader->LatestRecord.ServerTick;
                        }
                        gamePhase = g_pTelemetryHeader->GamePhase;
                        activeMatchId = g_pTelemetryHeader->ActiveMatchId;
                        connectedPlayers = g_pTelemetryHeader->ConnectedPlayers;
                        luaMemKb = g_pTelemetryHeader->LuaMemoryKb;
                        size_t mapLen = strnlen(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap));
                        std::memcpy(currentMapStr, g_pTelemetryHeader->CurrentMap, mapLen);
                        currentMapStr[mapLen] = '\0';
                        size_t nameLen = strnlen(g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName));
                        if (nameLen > 0) {
                            std::memcpy(playerNameStr, g_pTelemetryHeader->PlayerName, nameLen);
                            playerNameStr[nameLen] = '\0';
                        }
                        int64_t nowNs = GetCurrentNanoseconds();
                        if (nowNs > g_pTelemetryHeader->StartTimestampQpc && g_pTelemetryHeader->FrequencyQpc > 0) {
                            uptimeSec = (uint64_t)((nowNs - g_pTelemetryHeader->StartTimestampQpc) / g_pTelemetryHeader->FrequencyQpc);
                        }
                    }

                    uint64_t hitchCount = g_telemetryHitchCount.load(std::memory_order_relaxed);
                    uint64_t cpuStallCount = g_telemetryCpuStallCount.load(std::memory_order_relaxed);
                    double peakTickMs = (double)g_telemetryPeakFrameTimeUs.load(std::memory_order_relaxed) / 1000.0;
                    uint64_t killClampCount = g_linuxKillPacketClampCount.load(std::memory_order_relaxed);
                    uint64_t killExpandCount = g_linuxKillPacketExpandCount.load(std::memory_order_relaxed);

                    char escMap[64];
                    char escName[64];
                    EscapeJsonCString(currentMapStr, std::strlen(currentMapStr), escMap, sizeof(escMap));
                    EscapeJsonCString(playerNameStr, std::strlen(playerNameStr), escName, sizeof(escName));

                    std::snprintf(respBuf, sizeof(respBuf),
                        "{\"currentFps\":%.1f,\"memoryWorkingSetBytes\":%llu,\"luaMemoryKb\":%u,\"activeMatchId\":%u,\"currentMap\":\"%s\",\"playerName\":\"%s\",\"connectedPlayers\":%u,\"uptimeSeconds\":%llu,\"gamePhase\":%u,\"isSpectator\":false,\"activeEntities\":%u,\"serverTick\":%u,\"entityProfiler\":{\"activeEntities\":%u,\"luaMemoryKb\":%u,\"hitchCount\":%llu,\"cpuStallCount\":%llu,\"peakTickMs\":%.2f,\"killPacketClampCount\":%llu,\"killPacketExpandCount\":%llu}}",
                        fps,
                        (unsigned long long)memMb * 1024ULL * 1024ULL,
                        luaMemKb,
                        activeMatchId,
                        escMap,
                        escName,
                        connectedPlayers,
                        (unsigned long long)uptimeSec,
                        gamePhase,
                        activeEntities,
                        serverTick,
                        activeEntities,
                        luaMemKb,
                        (unsigned long long)hitchCount,
                        (unsigned long long)cpuStallCount,
                        peakTickMs,
                        (unsigned long long)killClampCount,
                        (unsigned long long)killExpandCount);
                    break;
                }
                case 0x000E: { // EvalLuaSync
                    respOpCode = 0x000F;
                    if (copyPayloadLen == 0) {
                        std::snprintf(respBuf, sizeof(respBuf), "{\"success\":true,\"result\":\"nil\"}");
                        break;
                    }
                    char escaped[896];
                    EscapeJsonCString(payloadBuf, copyPayloadLen, escaped, sizeof(escaped));
                    char wrappedCmd[CMD_MAX_LEN];
                    int wn = std::snprintf(wrappedCmd, sizeof(wrappedCmd), "Script \"%s\"", escaped);
                    if (wn > 0) {
                        EnqueueLinuxConsoleCommand(wrappedCmd, (size_t)wn < sizeof(wrappedCmd) ? (size_t)wn : sizeof(wrappedCmd) - 1);
                    }
                    std::snprintf(respBuf, sizeof(respBuf), "{\"success\":true,\"result\":\"queued\",\"command\":\"%s\"}", escaped);
                    break;
                }
                case 0x0014: { // UpdateTelemetryMetrics
                    const char* entPos = std::strstr(payloadBuf, "\"activeEntities\":");
                    if (entPos) {
                        uint32_t ent = (uint32_t)std::strtoul(entPos + 17, nullptr, 10);
                        g_dynamicActiveEntities.store(ent, std::memory_order_relaxed);
                    }
                    const char* tickPos = std::strstr(payloadBuf, "\"serverTick\":");
                    if (tickPos) {
                        uint32_t tick = (uint32_t)std::strtoul(tickPos + 13, nullptr, 10);
                        g_dynamicServerTick.store(tick, std::memory_order_relaxed);
                    }
                    respOpCode = 0x0015;
                    std::snprintf(respBuf, sizeof(respBuf), "{\"success\":true,\"telemetryUpdated\":true}");
                    break;
                }
                case 0x0070: { // TriggerMatchVictory
                    const char* harnessEnv = std::getenv("KONGOR_E2E_HARNESS");
                    if (!harnessEnv || std::strcmp(harnessEnv, "1") != 0) {
                        respOpCode = 0x0073;
                        std::snprintf(respBuf, sizeof(respBuf),
                            "{\"error\":{\"code\":403,\"message\":\"403 E2E_ONLY: trigger_match_victory requires KONGOR_E2E_HARNESS=1\"}}");
                    } else {
                        const char* masterEnv = std::getenv("MASTER_SERVER_ADDRESS");
                        if (masterEnv && (std::strstr(masterEnv, ":55555") != nullptr || std::strstr(masterEnv, ".newbs.io") != nullptr)) {
                            respOpCode = 0x0073;
                            std::snprintf(respBuf, sizeof(respBuf),
                                "{\"error\":{\"code\":403,\"message\":\"403 FORBIDDEN: trigger_match_victory blocked on production master server target\"}}");
                        } else {
                            WriteLinuxServerKeyframe(g_serverPort > 0 ? g_serverPort : 11235);
                            EnqueueLinuxConsoleCommand("ServerEndGame 1", 15);
                            respOpCode = 0x0074;
                            std::snprintf(respBuf, sizeof(respBuf),
                                "{\"victoryTriggered\":true,\"winningTeam\":1,\"command\":\"ServerEndGame 1\"}");
                        }
                    }
                    break;
                }
                case 0x0071: { // GetServerWorldState
                    WriteLinuxServerKeyframe(g_serverPort > 0 ? g_serverPort : 11235);
                    respOpCode = 0x0075;
                    double fps = 20.0;
                    uint32_t memMb = GetLinuxWorkingSetMb();
                    uint32_t gamePhase = 0;
                    uint32_t activeMatchId = 0;
                    uint32_t connectedPlayers = 0;
                    uint32_t luaMemKb = 0;
                    uint32_t activeEntities = g_dynamicActiveEntities.load(std::memory_order_relaxed);
                    uint32_t serverTick = g_dynamicServerTick.load(std::memory_order_relaxed);
                    char currentMapStr[32] = { 0 };
                    if (g_pTelemetryHeader) {
                        float cpuMs = g_pTelemetryHeader->LatestRecord.CpuFrameTimeMs;
                        if (cpuMs > 0.001f) fps = 1000.0 / cpuMs;
                        if (g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb > 0) memMb = g_pTelemetryHeader->LatestRecord.MemoryWorkingSetMb;
                        if (g_pTelemetryHeader->LatestRecord.ActiveEntities > 0) activeEntities = g_pTelemetryHeader->LatestRecord.ActiveEntities;
                        if (g_pTelemetryHeader->LatestRecord.ServerTick > 0) serverTick = g_pTelemetryHeader->LatestRecord.ServerTick;
                        gamePhase = g_pTelemetryHeader->GamePhase;
                        activeMatchId = g_pTelemetryHeader->ActiveMatchId;
                        connectedPlayers = g_pTelemetryHeader->ConnectedPlayers;
                        luaMemKb = g_pTelemetryHeader->LuaMemoryKb;
                        size_t mapLen = strnlen(g_pTelemetryHeader->CurrentMap, sizeof(g_pTelemetryHeader->CurrentMap));
                        std::memcpy(currentMapStr, g_pTelemetryHeader->CurrentMap, mapLen);
                        currentMapStr[mapLen] = '\0';
                    }
                    char escMap[64];
                    EscapeJsonCString(currentMapStr, std::strlen(currentMapStr), escMap, sizeof(escMap));
                    std::snprintf(respBuf, sizeof(respBuf),
                        "{\"currentFps\":%.1f,\"memoryWorkingSetBytes\":%llu,\"luaMemoryKb\":%u,\"activeMatchId\":%u,\"currentMap\":\"%s\",\"connectedPlayers\":%u,\"gamePhase\":%u,\"activeEntities\":%u,\"serverTick\":%u}",
                        fps, (unsigned long long)memMb * 1024ULL * 1024ULL, luaMemKb, activeMatchId, escMap, connectedPlayers, gamePhase, activeEntities, serverTick);
                    break;
                }
                case 0x0072: { // ApplyServerFixture
                    char escFixture[CMD_MAX_LEN];
                    size_t dstIdx = 0;
                    for (size_t si = 0; payloadBuf[si] != '\0' && dstIdx + 3 < sizeof(escFixture); ++si) {
                        unsigned char ch = static_cast<unsigned char>(payloadBuf[si]);
                        if (ch == '\\' || ch == '\'' || ch == '"') {
                            escFixture[dstIdx++] = '\\';
                            escFixture[dstIdx++] = static_cast<char>(ch);
                        } else if (ch == '\n') {
                            escFixture[dstIdx++] = '\\';
                            escFixture[dstIdx++] = 'n';
                        } else if (ch == '\r') {
                            escFixture[dstIdx++] = '\\';
                            escFixture[dstIdx++] = 'r';
                        } else if (ch >= 0x20) {
                            escFixture[dstIdx++] = static_cast<char>(ch);
                        }
                    }
                    escFixture[dstIdx] = '\0';

                    char wrappedCmd[CMD_MAX_LEN];
                    int wn = std::snprintf(wrappedCmd, sizeof(wrappedCmd),
                        "Script if TAO and TAO.Dispatch then TAO.Dispatch('apply_fixture', '%s') elseif Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot() end",
                        escFixture);
                    if (wn > 0) {
                        EnqueueLinuxConsoleCommand(wrappedCmd, (size_t)wn < sizeof(wrappedCmd) ? (size_t)wn : sizeof(wrappedCmd) - 1);
                    }
                    respOpCode = 0x0076;
                    std::snprintf(respBuf, sizeof(respBuf), "{\"applied\":true}");
                    break;
                }
                default:
                    respOpCode = 0x0004;
                    std::snprintf(respBuf, sizeof(respBuf), "{\"success\":false,\"error\":\"Unsupported opcode\"}");
                    break;
            }
            }

            int32_t respLen = static_cast<int32_t>(std::strlen(respBuf));
            uint8_t outFrame[2064];
            outFrame[0] = 0x4D;
            outFrame[1] = 0x57;
            std::memcpy(&outFrame[2], &respOpCode, sizeof(uint16_t));
            std::memcpy(&outFrame[4], &respLen, sizeof(int32_t));
            std::memcpy(&outFrame[8], &reqId, sizeof(uint32_t));
            if (respLen > 0) {
                std::memcpy(&outFrame[12], respBuf, static_cast<size_t>(respLen));
            }

            (void)send(clientFd, outFrame, 12 + static_cast<size_t>(respLen), MSG_NOSIGNAL);
        }
    }
}

static void* NativeGuardIpcServerWorker(void*) {
    while (g_ipcServerRunning.load(std::memory_order_relaxed) && g_ipcListenSock >= 0) {
        struct pollfd pfd{};
        pfd.fd = g_ipcListenSock;
        pfd.events = POLLIN;
        int pr = poll(&pfd, 1, 250);
        if (pr <= 0) continue;

        int clientFd = accept(g_ipcListenSock, nullptr, nullptr);
        if (clientFd < 0) continue;

        HandleIpcClientConnection(clientFd);
        close(clientFd);
    }
    return nullptr;
}

#pragma pack(push, 1)
struct ServerKeyframeHeaderLinux {
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
static_assert(sizeof(ServerKeyframeHeaderLinux) == 40, "ServerKeyframeHeaderLinux must be exactly 40 bytes");

static uint32_t ComputeLinuxCrc32(const uint8_t* data, size_t length) {
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

static std::atomic<uint32_t> g_linuxKeyframeSequence{ 0 };

static void WriteLinuxServerKeyframe(int port) {
    if (port <= 0) return;
    char shmName[128];
    std::snprintf(shmName, sizeof(shmName), "/KongorServerKeyframe_%d", port);
    int fd = shm_open(shmName, O_RDWR | O_CREAT, 0666);
    if (fd >= 0) {
        if (ftruncate(fd, 65536) == 0) {
            void* pBuf = mmap(nullptr, 65536, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
            if (pBuf != MAP_FAILED) {
                uint32_t seq = g_linuxKeyframeSequence.fetch_add(1, std::memory_order_relaxed) + 1;
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
                    int64_t nowNs = GetCurrentNanoseconds();
                    if (nowNs > g_pTelemetryHeader->StartTimestampQpc && g_pTelemetryHeader->FrequencyQpc > 0) {
                        uptimeMs = (uint64_t)((nowNs - g_pTelemetryHeader->StartTimestampQpc) * 1000 / g_pTelemetryHeader->FrequencyQpc);
                    }
                }

                struct timespec ts{};
                clock_gettime(CLOCK_REALTIME, &ts);
                int64_t timestampUtcTicks = (int64_t)(ts.tv_sec * 10000000LL) + (ts.tv_nsec / 100) + 621355968000000000LL;

                char payloadJson[1024];
                int payloadLen = std::snprintf(payloadJson, sizeof(payloadJson),
                    "{\"gamePhase\":%u,\"serverTick\":%u,\"matchId\":%llu,\"uptimeMs\":%llu,\"connectedClients\":%u,\"port\":%d}",
                    gamePhase, tick, (unsigned long long)matchId, (unsigned long long)uptimeMs, clients, port);
                if (payloadLen < 0) payloadLen = 0;

                uint32_t crc = ComputeLinuxCrc32((const uint8_t*)payloadJson, (size_t)payloadLen);

                ServerKeyframeHeaderLinux header;
                header.Magic = 0x4B474B46;
                header.Version = 1;
                header.Sequence = seq;
                header.ServerTick = tick;
                header.MatchId = matchId;
                header.TimestampUtcTicks = timestampUtcTicks;
                header.PayloadSizeBytes = (uint32_t)payloadLen;
                header.Crc32 = crc;

                uint8_t* dest = (uint8_t*)pBuf;
                *(uint32_t*)dest = 0;
                if (payloadLen > 0) {
                    std::memcpy(dest + 40, payloadJson, (size_t)payloadLen);
                }
                std::memcpy(dest + 4, ((const uint8_t*)&header) + 4, sizeof(header) - 4);
                std::atomic_thread_fence(std::memory_order_seq_cst);
                *(uint32_t*)dest = header.Magic;

                munmap(pBuf, 65536);

                char fbPath[128];
                std::snprintf(fbPath, sizeof(fbPath), "/tmp/KongorServerKeyframe_%d.bin", port);
                int fbfd = open(fbPath, O_WRONLY | O_CREAT | O_TRUNC, 0666);
                if (fbfd >= 0) {
                    if (write(fbfd, &header, sizeof(header)) < 0) {}
                    if (payloadLen > 0) {
                        if (write(fbfd, payloadJson, (size_t)payloadLen) < 0) {}
                    }
                    close(fbfd);
                }
            }
        }
        close(fd);
    }
}

static void InitializeIpcSocketServer() {
    int port = g_serverPort > 0 ? g_serverPort : 11235;
    uid_t euid = geteuid();

    // [L2-P0-04] Bind inside an owner-only 0700 directory with 0600 socket permissions (zero world-writable /tmp TOCTOU).
    char sockDir[72] = { 0 };
    const char* envDir = std::getenv("KONGOR_IPC_SOCKET_DIR");
    if (envDir && envDir[0] != '\0' && std::strlen(envDir) < sizeof(sockDir) - 1) {
        std::snprintf(sockDir, sizeof(sockDir), "%s", envDir);
    } else {
        std::snprintf(sockDir, sizeof(sockDir), "/tmp/kongor-%u", (unsigned)euid);
    }

    mode_t prevMask = umask(0077);
    (void)mkdir(sockDir, 0700);
    struct stat dirSt{};
    if (lstat(sockDir, &dirSt) != 0 || S_ISLNK(dirSt.st_mode) || !S_ISDIR(dirSt.st_mode) || dirSt.st_uid != euid) {
        umask(prevMask);
        std::fprintf(stderr, "[NativeGuard:Linux] SECURITY: Refusing IPC socket bind — '%s' is not an owner-controlled directory (uid=%u)\n",
                     sockDir, (unsigned)euid);
        return;
    }
    (void)chmod(sockDir, 0700);

    const char* envPipe = std::getenv("KONGOR_NATIVE_PIPE");
    if (envPipe && envPipe[0] != '\0') {
        std::snprintf(g_ipcSocketPath, sizeof(g_ipcSocketPath), "%s/%s.sock", sockDir, envPipe);
        std::snprintf(g_ipcCompatSymlinkPath, sizeof(g_ipcCompatSymlinkPath), "/tmp/%s.sock", envPipe);
    } else {
        std::snprintf(g_ipcSocketPath, sizeof(g_ipcSocketPath), "%s/hon-server-native-%d.sock", sockDir, port);
        std::snprintf(g_ipcCompatSymlinkPath, sizeof(g_ipcCompatSymlinkPath), "/tmp/hon-server-native-%d.sock", port);
    }

    unlink(g_ipcSocketPath);

    int sock = socket(AF_UNIX, SOCK_STREAM, 0);
    if (sock < 0) {
        umask(prevMask);
        std::fprintf(stderr, "[NativeGuard:Linux] socket(AF_UNIX) failed: %s\n", strerror(errno));
        return;
    }

    struct sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    const size_t ipcPathLen = ::strnlen(g_ipcSocketPath, sizeof(addr.sun_path) - 1);
    std::memcpy(addr.sun_path, g_ipcSocketPath, ipcPathLen);
    addr.sun_path[ipcPathLen] = '\0';

    if (bind(sock, reinterpret_cast<struct sockaddr*>(&addr), sizeof(addr)) != 0) {
        umask(prevMask);
        std::fprintf(stderr, "[NativeGuard:Linux] bind(%s) failed: %s\n", g_ipcSocketPath, strerror(errno));
        close(sock);
        return;
    }

    (void)chmod(g_ipcSocketPath, 0600);
    umask(prevMask);

    if (listen(sock, 8) != 0) {
        std::fprintf(stderr, "[NativeGuard:Linux] listen(%s) failed: %s\n", g_ipcSocketPath, strerror(errno));
        close(sock);
        unlink(g_ipcSocketPath);
        return;
    }

    // Maintain same-UID compatibility symlink at /tmp/hon-server-native-<port>.sock if safe
    if (std::strcmp(g_ipcCompatSymlinkPath, g_ipcSocketPath) != 0) {
        struct stat symSt{};
        if (lstat(g_ipcCompatSymlinkPath, &symSt) == 0) {
            if (symSt.st_uid == euid) {
                (void)unlink(g_ipcCompatSymlinkPath);
                if (symlink(g_ipcSocketPath, g_ipcCompatSymlinkPath) != 0) {}
            }
        } else if (errno == ENOENT) {
            if (symlink(g_ipcSocketPath, g_ipcCompatSymlinkPath) != 0) {}
        }
    }

    g_ipcListenSock = sock;
    g_ipcServerRunning.store(true, std::memory_order_relaxed);
    pthread_create(&g_ipcServerThread, nullptr, NativeGuardIpcServerWorker, nullptr);

    std::fprintf(stdout, "[NativeGuard:Linux] Unix domain IPC socket listening at %s (0700 dir / 0600 sock)\n", g_ipcSocketPath);
    std::fflush(stdout);
}

static void ShutdownIpcSocketServer() {
    g_ipcServerRunning.store(false, std::memory_order_relaxed);
    if (g_ipcListenSock >= 0) {
        shutdown(g_ipcListenSock, SHUT_RDWR);
        close(g_ipcListenSock);
        g_ipcListenSock = -1;
    }
    if (g_ipcServerThread != 0) {
        pthread_join(g_ipcServerThread, nullptr);
        g_ipcServerThread = 0;
    }
    if (g_ipcCompatSymlinkPath[0] != '\0') {
        struct stat symSt{};
        if (lstat(g_ipcCompatSymlinkPath, &symSt) == 0 && S_ISLNK(symSt.st_mode) && symSt.st_uid == geteuid()) {
            unlink(g_ipcCompatSymlinkPath);
        }
        g_ipcCompatSymlinkPath[0] = '\0';
    }
    if (g_ipcSocketPath[0] != '\0') {
        unlink(g_ipcSocketPath);
        g_ipcSocketPath[0] = '\0';
    }
}

// ============================================================================
// LINUX POSIX SIGNAL SENTINEL & CRASH MINIDUMP EMITTER (STRICT ASYNC-SIGNAL-SAFE)
// [L2-P0-04] Zero snprintf / backtrace / backtrace_symbols_fd / malloc / stdio.
// ============================================================================

static struct sigaction g_oldSigSegv;
static struct sigaction g_oldSigAbrt;
static struct sigaction g_oldSigBus;
static struct sigaction g_oldSigFpe;
static struct sigaction g_oldSigIll;

static size_t AsyncAppendStr(char* buf, size_t pos, size_t cap, const char* s) noexcept {
    if (!buf || !s || pos >= cap) return pos;
    while (*s != '\0' && pos + 1 < cap) {
        buf[pos++] = *s++;
    }
    buf[pos] = '\0';
    return pos;
}

static size_t AsyncAppendUint64Dec(char* buf, size_t pos, size_t cap, uint64_t val) noexcept {
    if (!buf || pos + 1 >= cap) return pos;
    char rev[24];
    size_t n = 0;
    if (val == 0) {
        rev[n++] = '0';
    } else {
        while (val > 0 && n < sizeof(rev)) {
            rev[n++] = static_cast<char>('0' + (val % 10ULL));
            val /= 10ULL;
        }
    }
    while (n > 0 && pos + 1 < cap) {
        buf[pos++] = rev[--n];
    }
    buf[pos] = '\0';
    return pos;
}

static size_t AsyncAppendIntDec(char* buf, size_t pos, size_t cap, int val) noexcept {
    if (val < 0) {
        pos = AsyncAppendStr(buf, pos, cap, "-");
        uint64_t mag = static_cast<uint64_t>(-(static_cast<int64_t>(val)));
        return AsyncAppendUint64Dec(buf, pos, cap, mag);
    }
    return AsyncAppendUint64Dec(buf, pos, cap, static_cast<uint64_t>(val));
}

static size_t AsyncAppendHex64(char* buf, size_t pos, size_t cap, uint64_t val) noexcept {
    static const char kHex[17] = "0123456789abcdef";
    pos = AsyncAppendStr(buf, pos, cap, "0x");
    for (int shift = 60; shift >= 0 && pos + 1 < cap; shift -= 4) {
        buf[pos++] = kHex[(val >> shift) & 0xFULL];
    }
    if (pos < cap) buf[pos] = '\0';
    return pos;
}

static void NativeGuardCrashSignalHandler(int sig, siginfo_t* info, void* context) {
    // satisfies: OBL-QA-LINUX-00
    if ((sig == SIGSEGV || sig == SIGBUS) && ::Kongor::t_callFaultGuard.armed) {
        ::Kongor::t_callFaultGuard.armed = 0;
        siglongjmp(::Kongor::t_callFaultGuard.jmpBuf, 1);
    }
    int savedErrno = errno;
    pid_t pid = getpid();
    (void)mkdir("logs", 0750);
    (void)mkdir("logs/crash", 0750);

    const char* sigName = "UNKNOWN";
    switch (sig) {
        case SIGSEGV: sigName = "SIGSEGV (139)"; break;
        case SIGABRT: sigName = "SIGABRT (134)"; break;
        case SIGBUS:  sigName = "SIGBUS (135)";  break;
        case SIGFPE:  sigName = "SIGFPE (136)";  break;
        case SIGILL:  sigName = "SIGILL (132)";  break;
    }

    char sentinelPath[128];
    size_t pathPos = 0;
    pathPos = AsyncAppendStr(sentinelPath, pathPos, sizeof(sentinelPath), "logs/crash/native_guard_sentinel_");
    pathPos = AsyncAppendIntDec(sentinelPath, pathPos, sizeof(sentinelPath), static_cast<int>(pid));
    pathPos = AsyncAppendStr(sentinelPath, pathPos, sizeof(sentinelPath), ".log");

    uint64_t ripVal = 0;
    uint64_t rspVal = 0;
    uint64_t rbpVal = 0;
#if defined(__x86_64__)
    if (context != nullptr) {
        const ucontext_t* uctx = reinterpret_cast<const ucontext_t*>(context);
        ripVal = static_cast<uint64_t>(uctx->uc_mcontext.gregs[REG_RIP]);
        rspVal = static_cast<uint64_t>(uctx->uc_mcontext.gregs[REG_RSP]);
        rbpVal = static_cast<uint64_t>(uctx->uc_mcontext.gregs[REG_RBP]);
    }
#endif

    char reportBuf[1536];
    size_t pos = 0;
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), "\n[NativeGuard:Linux] FATAL SIGNAL: ");
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), sigName);
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), " addr=");
    pos = AsyncAppendHex64(reportBuf, pos, sizeof(reportBuf), info ? reinterpret_cast<uintptr_t>(info->si_addr) : 0ULL);
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), " rip=");
    pos = AsyncAppendHex64(reportBuf, pos, sizeof(reportBuf), ripVal);
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), " rsp=");
    pos = AsyncAppendHex64(reportBuf, pos, sizeof(reportBuf), rspVal);
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), " rbp=");
    pos = AsyncAppendHex64(reportBuf, pos, sizeof(reportBuf), rbpVal);
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), " pid=");
    pos = AsyncAppendIntDec(reportBuf, pos, sizeof(reportBuf), static_cast<int>(pid));
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), " port=");
    pos = AsyncAppendIntDec(reportBuf, pos, sizeof(reportBuf), g_serverPort);
    pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), "\n");

    // Dump up to 16 stack qwords solely within the already-faulted current 4KB stack page (zero secondary fault risk)
    if (rspVal >= 0x10000ULL && (rspVal & 0x7ULL) == 0ULL) {
        uint64_t pageLimit = (rspVal & ~0xFFFULL) + 0x1000ULL;
        const uint64_t* sp = reinterpret_cast<const uint64_t*>(static_cast<uintptr_t>(rspVal));
        pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), "[NativeGuard:Linux] Stack words (same-page RSP):\n");
        for (size_t i = 0; i < 16; ++i) {
            uint64_t wordAddr = rspVal + (i * 8ULL);
            if (wordAddr + 8ULL > pageLimit) break;
            pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), "  [rsp+");
            pos = AsyncAppendUint64Dec(reportBuf, pos, sizeof(reportBuf), i * 8ULL);
            pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), "] = ");
            pos = AsyncAppendHex64(reportBuf, pos, sizeof(reportBuf), sp[i]);
            pos = AsyncAppendStr(reportBuf, pos, sizeof(reportBuf), "\n");
        }
    }

    if (pos > 0) {
        if (write(STDERR_FILENO, reportBuf, pos) < 0) {}
    }

    int fd = open(sentinelPath, O_CREAT | O_WRONLY | O_APPEND, 0600);
    if (fd >= 0) {
        if (pos > 0) {
            if (write(fd, reportBuf, pos) < 0) {}
        }
        (void)close(fd);
    }

    errno = savedErrno;

    struct sigaction* prevAction = nullptr;
    switch (sig) {
        case SIGSEGV: prevAction = &g_oldSigSegv; break;
        case SIGABRT: prevAction = &g_oldSigAbrt; break;
        case SIGBUS:  prevAction = &g_oldSigBus; break;
        case SIGFPE:  prevAction = &g_oldSigFpe; break;
        case SIGILL:  prevAction = &g_oldSigIll; break;
    }

    if (prevAction && prevAction->sa_handler != SIG_DFL && prevAction->sa_handler != SIG_IGN) {
        if (prevAction->sa_flags & SA_SIGINFO) {
            prevAction->sa_sigaction(sig, info, context);
        } else {
            prevAction->sa_handler(sig);
        }
    } else {
        struct sigaction dfl{};
        dfl.sa_handler = SIG_DFL;
        sigemptyset(&dfl.sa_mask);
        (void)sigaction(sig, &dfl, nullptr);
        (void)kill(pid, sig);
    }
}

static void InstallCrashSignalHandlers() {
    // SIGSEGV and SIGBUS omit SA_RESETHAND so armed t_callFaultGuard recoveries
    // via siglongjmp keep the handler active for subsequent calls; unarmed crashes
    // explicitly chain prevAction or reset SIG_DFL + kill() in NativeGuardCrashSignalHandler.
    struct sigaction saFault;
    std::memset(&saFault, 0, sizeof(saFault));
    saFault.sa_sigaction = NativeGuardCrashSignalHandler;
    saFault.sa_flags = SA_SIGINFO;
    sigemptyset(&saFault.sa_mask);

    struct sigaction saFatal;
    std::memset(&saFatal, 0, sizeof(saFatal));
    saFatal.sa_sigaction = NativeGuardCrashSignalHandler;
    saFatal.sa_flags = SA_SIGINFO | SA_RESETHAND;
    sigemptyset(&saFatal.sa_mask);

    sigaction(SIGSEGV, &saFault, &g_oldSigSegv);
    sigaction(SIGBUS, &saFault, &g_oldSigBus);
    sigaction(SIGABRT, &saFatal, &g_oldSigAbrt);
    sigaction(SIGFPE, &saFatal, &g_oldSigFpe);
    sigaction(SIGILL, &saFatal, &g_oldSigIll);
}

// ============================================================================
// SHARED OBJECT ENTRYPOINTS (CONSTRUCTOR & DESTRUCTOR)
// ============================================================================

static bool NativeGuard_IsHonEngineProcess() {
    const char* forceVal = std::getenv("KONGOR_FORCE_LINUX_NATIVE_GUARD");
    if (forceVal && (std::strcmp(forceVal, "1") == 0 || strcasecmp(forceVal, "true") == 0)) {
        return true;
    }

    char exeBuf[4096];
    ssize_t exeLen = readlink("/proc/self/exe", exeBuf, sizeof(exeBuf) - 1);
    if (exeLen > 0) {
        exeBuf[exeLen] = '\0';
        const char* base = std::strrchr(exeBuf, '/');
        base = base ? (base + 1) : exeBuf;
        if (strncasecmp(base, "hon", 3) == 0) {
            return true;
        }
        return false;
    }

    char cmd[4096];
    ReadLinuxProcessCommandLine(cmd, sizeof(cmd));
    return std::strstr(cmd, "hon-x86_64") != nullptr || std::strstr(cmd, "hon_x64") != nullptr;
}

static void DisableLinuxArchiveChecksumsIfAllowed() {
    static std::atomic<bool> s_disabled{ false };
    if (s_disabled.load(std::memory_order_acquire)) {
        return;
    }

    const char* envVal = std::getenv("KONGOR_EXAMINE_ARCHIVE_CHECKSUMS");
    if (envVal && (std::strcmp(envVal, "1") == 0 ||
                   strcasecmp(envVal, "true") == 0 ||
                   strcasecmp(envVal, "yes") == 0 ||
                   strcasecmp(envVal, "on") == 0)) {
        s_disabled.store(true, std::memory_order_release);
        return;
    }

    void* sym = dlsym(RTLD_DEFAULT, "_ZN8CArchive16ExamineChecksumsE");
    if (!sym) {
        sym = dlsym(RTLD_NEXT, "_ZN8CArchive16ExamineChecksumsE");
    }
    if (sym != nullptr) {
        *static_cast<bool*>(sym) = false;
        s_disabled.store(true, std::memory_order_release);
        std::fprintf(stdout, "[NativeGuard:Linux] Disabled CArchive::ExamineChecksums (_ZN8CArchive16ExamineChecksumsE) prior to .s2z mount.\n");
        std::fflush(stdout);
    }
}

__attribute__((constructor))
void NativeGuard_Init(void) {
    if (NativeGuard_IsDisabledByEnv()) {
        g_killSwitchActive.store(true, std::memory_order_release);
        std::fprintf(stdout, "[NativeGuard:Linux] Disabled via KONGOR_DISABLE_LINUX_NATIVE_GUARD=1 kill-switch.\n");
        std::fflush(stdout);
        return;
    }

    if (!NativeGuard_IsHonEngineProcess()) {
        g_killSwitchActive.store(true, std::memory_order_release);
        return;
    }

    std::fprintf(stdout, "[NativeGuard:Linux] Loading libk2_native_guard.so / libkongor_native_guard.so (libstdc++-free)...\n");
    std::fflush(stdout);
    DisableLinuxArchiveChecksumsIfAllowed();
    InstallCrashSignalHandlers();
    InitializeTelemetrySharedMemory();
    InitializeIpcSocketServer();
    ::Kongor::EnsureKillPacketGuardsApplied();
}

__attribute__((destructor))
void NativeGuard_Fini(void) {
    if (g_killSwitchActive.load(std::memory_order_acquire)) {
        return;
    }
    std::fprintf(stdout, "[NativeGuard:Linux] Unloading libk2_native_guard.so / libkongor_native_guard.so...\n");
    std::fflush(stdout);
    ShutdownIpcSocketServer();
    ShutdownTelemetrySharedMemory();
}

// ============================================================================
// EXPORTED C API & LD_PRELOAD INTERPOSERS FOR LINUX DEDICATED SERVER
// ============================================================================

extern "C" {

bool NativeGuard_IsActive() {
    return g_pTelemetryHeader != nullptr && !g_killSwitchActive.load(std::memory_order_relaxed);
}

bool NativeGuard_IsKillSwitchActive() {
    return g_killSwitchActive.load(std::memory_order_relaxed);
}

KongorTelemetryRingHeaderV2* NativeGuard_GetTelemetryHeader() {
    return g_pTelemetryHeader;
}

void NativeGuard_SetServerTick(uint32_t tick) {
    g_dynamicServerTick.store(tick, std::memory_order_relaxed);
}

void NativeGuard_SetActiveEntities(uint32_t count) {
    g_dynamicActiveEntities.store(count, std::memory_order_relaxed);
}

void NativeGuard_SetPingMicroseconds(uint32_t us) {
    g_dynamicPingMicroseconds.store(us, std::memory_order_relaxed);
}

void NativeGuard_SetPacketLoss(float percent) {
    g_dynamicPacketLossPercent.store(percent, std::memory_order_relaxed);
}

struct LibcxxFakeLongWString {
    uint64_t capWithLongBit; // ((len + 1) << 1) | 1ULL
    uint64_t size;           // len (in wchar_t code units)
    const wchar_t* data;     // pointer to null-terminated wchar_t array
};
static_assert(sizeof(LibcxxFakeLongWString) == 24, "libc++ wstring must be 24 bytes");

static size_t Utf8ToWcharBuf(const char* str, wchar_t* outBuf, size_t outCap) {
    if (!str || !outBuf || outCap == 0) return 0;
    size_t outLen = 0;
    size_t i = 0;
    while (str[i] != '\0' && outLen + 1 < outCap) {
        uint8_t c = static_cast<uint8_t>(str[i]);
        if (c < 0x80) {
            outBuf[outLen++] = static_cast<wchar_t>(c);
            i += 1;
        } else if ((c & 0xE0) == 0xC0 && str[i + 1] != '\0') {
            uint32_t cp = ((c & 0x1F) << 6) | (static_cast<uint8_t>(str[i + 1]) & 0x3F);
            outBuf[outLen++] = static_cast<wchar_t>(cp);
            i += 2;
        } else if ((c & 0xF0) == 0xE0 && str[i + 1] != '\0' && str[i + 2] != '\0') {
            uint32_t cp = ((c & 0x0F) << 12) |
                          ((static_cast<uint8_t>(str[i + 1]) & 0x3F) << 6) |
                          (static_cast<uint8_t>(str[i + 2]) & 0x3F);
            outBuf[outLen++] = static_cast<wchar_t>(cp);
            i += 3;
        } else if ((c & 0xF8) == 0xF0 && str[i + 1] != '\0' && str[i + 2] != '\0' && str[i + 3] != '\0') {
            uint32_t cp = ((c & 0x07) << 18) |
                          ((static_cast<uint8_t>(str[i + 1]) & 0x3F) << 12) |
                          ((static_cast<uint8_t>(str[i + 2]) & 0x3F) << 6) |
                          (static_cast<uint8_t>(str[i + 3]) & 0x3F);
            outBuf[outLen++] = static_cast<wchar_t>(cp);
            i += 4;
        } else {
            outBuf[outLen++] = L'?';
            i += 1;
        }
    }
    outBuf[outLen] = L'\0';
    return outLen;
}

static size_t WcharToUtf8Buf(const wchar_t* wstr, char* outBuf, size_t outCap) {
    if (!wstr || !outBuf || outCap == 0) return 0;
    size_t outLen = 0;
    size_t i = 0;
    while (wstr[i] != L'\0' && outLen + 1 < outCap) {
        uint32_t cp = static_cast<uint32_t>(wstr[i]);
        if (cp < 0x80) {
            outBuf[outLen++] = static_cast<char>(cp);
        } else if (cp < 0x800 && outLen + 2 < outCap) {
            outBuf[outLen++] = static_cast<char>(0xC0 | (cp >> 6));
            outBuf[outLen++] = static_cast<char>(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000 && outLen + 3 < outCap) {
            outBuf[outLen++] = static_cast<char>(0xE0 | (cp >> 12));
            outBuf[outLen++] = static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            outBuf[outLen++] = static_cast<char>(0x80 | (cp & 0x3F));
        } else if (outLen + 4 < outCap) {
            outBuf[outLen++] = static_cast<char>(0xF0 | (cp >> 18));
            outBuf[outLen++] = static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
            outBuf[outLen++] = static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            outBuf[outLen++] = static_cast<char>(0x80 | (cp & 0x3F));
        } else {
            break;
        }
        ++i;
    }
    outBuf[outLen] = '\0';
    return outLen;
}

static void* ResolveLoadedLibrarySymbol(const char* libSuffix, const char* symName) {
    if (!symName || symName[0] == '\0') return nullptr;

    void* sym = dlsym(RTLD_NEXT, symName);
    if (sym != nullptr) {
        return sym;
    }

    if (!libSuffix || libSuffix[0] == '\0') {
        return nullptr;
    }

    // K2 engine loads game/libgame_shared-x86_64-server.so via dlopen(..., RTLD_LOCAL),
    // which is invisible to dlsym(RTLD_NEXT) inside an LD_PRELOAD interposer.
    // Locate the already-mapped shared object via /proc/self/maps and query its handle
    // with RTLD_NOLOAD so we resolve the exact real symbol without loading a second copy.
    std::FILE* mapsFile = std::fopen("/proc/self/maps", "r");
    if (!mapsFile) {
        return nullptr;
    }

    const size_t suffixLen = std::strlen(libSuffix);
    char line[1024];
    char lastTriedPath[512] = { 0 };

    while (std::fgets(line, sizeof(line), mapsFile) != nullptr) {
        size_t len = std::strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) {
            line[--len] = '\0';
        }

        const char* pathStart = std::strchr(line, '/');
        if (!pathStart) continue;

        size_t pathLen = std::strlen(pathStart);
        // Strip optional " (deleted)" suffix if present
        if (pathLen > 10 && std::strcmp(pathStart + pathLen - 10, " (deleted)") == 0) {
            pathLen -= 10;
        }
        if (pathLen < suffixLen) continue;
        if (std::strncmp(pathStart + pathLen - suffixLen, libSuffix, suffixLen) != 0) {
            continue;
        }

        char candidatePath[512];
        if (pathLen >= sizeof(candidatePath)) continue;
        std::memcpy(candidatePath, pathStart, pathLen);
        candidatePath[pathLen] = '\0';

        if (std::strcmp(candidatePath, lastTriedPath) == 0) {
            continue;
        }
        std::strncpy(lastTriedPath, candidatePath, sizeof(lastTriedPath) - 1);
        lastTriedPath[sizeof(lastTriedPath) - 1] = '\0';

        void* handle = dlopen(candidatePath, RTLD_NOLOAD | RTLD_LAZY);
        if (handle != nullptr) {
            sym = dlsym(handle, symName);
            dlclose(handle);
            if (sym != nullptr) {
                break;
            }
        }
    }

    std::fclose(mapsFile);
    return sym;
}

static int FindGameSharedBaseCallback(struct dl_phdr_info* info, size_t /*size*/, void* data) {
    if (info && info->dlpi_name && std::strstr(info->dlpi_name, "libgame_shared-x86_64-server.so") != nullptr) {
        *static_cast<uintptr_t*>(data) = static_cast<uintptr_t>(info->dlpi_addr);
        return 1;
    }
    return 0;
}

static void EnsureGameSharedAbiGuardsApplied() {
    static std::atomic<uintptr_t> s_lastPatchedBase{ 0 };
    static const uint8_t kPatchWebReqCookie[7] = { 0x49, 0x8D, 0xB5, 0x28, 0x02, 0x00, 0x00 };

    uintptr_t base = 0;
    dl_iterate_phdr(FindGameSharedBaseCallback, &base);
    if (base == 0) {
        return;
    }

    // Fast path: skip if libgame_shared has not been dlclosed/reloaded at a fresh mapping.
    // Checking the live Guard 4 byte sequence guarantees re-patching even when dlopen reuses the same base address.
    if (base == s_lastPatchedBase.load(std::memory_order_acquire) &&
        std::memcmp(reinterpret_cast<const void*>(base + 0xFDD83F), kPatchWebReqCookie, sizeof(kPatchWebReqCookie)) == 0) {
        return;
    }

    long pageSize = sysconf(_SC_PAGESIZE);
    if (pageSize <= 0) pageSize = 4096;

    auto applyPatch = [pageSize](uintptr_t target, const uint8_t* expectedOrig, const uint8_t* patchBytes, size_t len, const char* label) {
        if (std::memcmp(reinterpret_cast<const void*>(target), patchBytes, len) == 0) {
            std::fprintf(stdout, "[NativeGuard:Linux] %s already active at %p\n", label, reinterpret_cast<void*>(target));
            return;
        }
        if (std::memcmp(reinterpret_cast<const void*>(target), expectedOrig, len) != 0) {
            std::fprintf(stderr, "[NativeGuard:Linux] WARN: %s unexpected bytes at %p (skipping)\n", label, reinterpret_cast<void*>(target));
            return;
        }
        uintptr_t pageStart = target & ~static_cast<uintptr_t>(pageSize - 1);
        size_t mapLen = ((target + len) > (pageStart + static_cast<uintptr_t>(pageSize)))
            ? static_cast<size_t>(pageSize) * 2
            : static_cast<size_t>(pageSize);
        if (mprotect(reinterpret_cast<void*>(pageStart), mapLen, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
            std::memcpy(reinterpret_cast<void*>(target), patchBytes, len);
            mprotect(reinterpret_cast<void*>(pageStart), mapLen, PROT_READ | PROT_EXEC);
            std::fprintf(stdout, "[NativeGuard:Linux] Applied %s at %p\n", label, reinterpret_cast<void*>(target));
        }
    };

    // Guard 1: CPlayer::AssignAndUpdateUpgrades (0xC94C24: 74 4a -> eb 4a)
    static const uint8_t kOrig7[2] = { 0x74, 0x4a };
    static const uint8_t kPatch7[2] = { 0xeb, 0x4a };
    applyPatch(base + 0xC94C24, kOrig7, kPatch7, 2, "CPlayer::AssignAndUpdateUpgrades ABI guard");

    // Guard 2: CPlayer::ServerFrameMovement path-preview CHostServer -0x50 ABI shift guard (0xC957E3: 74 10 -> eb 10)
    static const uint8_t kOrig8[2] = { 0x74, 0x10 };
    static const uint8_t kPatch8[2] = { 0xeb, 0x10 };
    applyPatch(base + 0xC957E3, kOrig8, kPatch8, 2, "CPlayer::ServerFrameMovement click-to-move ABI guard");

    // Guard 3A & 3B: CPlayer::CanAccessAltAvatar overloads (0xCA2D50 & 0xCA2B90 -> mov eax, 1; ret)
    static const uint8_t kOrig9a[6] = { 0x41, 0x56, 0x53, 0x48, 0x83, 0xec };
    static const uint8_t kOrig9b[6] = { 0x41, 0x57, 0x41, 0x56, 0x41, 0x54 };
    static const uint8_t kPatchTrue[6] = { 0xb8, 0x01, 0x00, 0x00, 0x00, 0xc3 };
    applyPatch(base + 0xCA2D50, kOrig9a, kPatchTrue, 6, "CPlayer::CanAccessAltAvatar(1-arg) unlock");
    applyPatch(base + 0xCA2B90, kOrig9b, kPatchTrue, 6, "CPlayer::CanAccessAltAvatar(2-arg) unlock");

    // Guard 4: IGame::ProcessWebRequests (0xFDD83F) CHostServer::m_sSessionCookie ABI offset fix (+0x208 -> +0x228)
    // libgame_shared-x86_64-server.so reads CHostServer+0x208 (raw host string fields + length bytes)
    // instead of libk2-x86_64-server.so CHostServer::m_sSessionCookie at +0x228.
    static const uint8_t kOrigWebReqCookie[7] = { 0x49, 0x8D, 0xB5, 0x08, 0x02, 0x00, 0x00 };
    applyPatch(
        base + 0xFDD83F,
        kOrigWebReqCookie,
        kPatchWebReqCookie,
        sizeof(kOrigWebReqCookie),
        "IGame::ProcessWebRequests CHostServer::m_sSessionCookie +0x228 ABI guard"
    );

    std::fflush(stdout);
    s_lastPatchedBase.store(base, std::memory_order_release);
}

// 1. Interpose CPlayer::InitializeConnectionless
// Confirmed via disassembly of libgame_shared-x86_64-server.so:
//   m_iClientNumber (int32_t) at offset +0x9c
//   m_yFlags (uint16_t) at offset +0x3ce (bit 7: 0x0080 PLAYER_FLAG_TERMINATED)
//   m_uiDisconnectTime (uint32_t) at offset +0x428 (0xFFFFFFFFU == INVALID_TIME)
__attribute__((visibility("default")))
void _ZN7CPlayer24InitializeConnectionlessEP11CHostServeriRKNSt3__112basic_stringIwNS2_11char_traitsIwEE17K2StringAllocatorIwEEEb(
    void* thisPtr, void* pHostServer, int iClientNum, const void* sName, bool bFakePlayer)
{
    EnsureGameSharedAbiGuardsApplied();
    ::Kongor::EnsureKillPacketGuardsApplied();
    using FnInitConn = void(*)(void*, void*, int, const void*, bool);
    static constexpr const char* kSymInitConn =
        "_ZN7CPlayer24InitializeConnectionlessEP11CHostServeriRKNSt3__112basic_stringIwNS2_11char_traitsIwEE17K2StringAllocatorIwEEEb";

    static std::atomic<FnInitConn> s_origInitConn{ nullptr };
    static std::atomic<bool> s_initConnIsServer{ false };
    FnInitConn orig_InitializeConnectionless = s_origInitConn.load(std::memory_order_acquire);
    bool isServerGameShared = s_initConnIsServer.load(std::memory_order_acquire);
    if (!orig_InitializeConnectionless) {
        orig_InitializeConnectionless = reinterpret_cast<FnInitConn>(
            ResolveLoadedLibrarySymbol("libgame_shared-x86_64-server.so", kSymInitConn));
        if (orig_InitializeConnectionless) {
            isServerGameShared = true;
            s_initConnIsServer.store(true, std::memory_order_release);
            s_origInitConn.store(orig_InitializeConnectionless, std::memory_order_release);
        } else {
            orig_InitializeConnectionless = reinterpret_cast<FnInitConn>(
                ResolveLoadedLibrarySymbol("libgame_shared-x86_64.so", kSymInitConn));
            if (orig_InitializeConnectionless) {
                isServerGameShared = false;
                s_initConnIsServer.store(false, std::memory_order_release);
                s_origInitConn.store(orig_InitializeConnectionless, std::memory_order_release);
            }
        }
    }

    if (!orig_InitializeConnectionless) {
        std::fprintf(stderr,
            "[NativeGuard:Linux] ERROR: Failed to resolve real CPlayer::InitializeConnectionless in libgame_shared-x86_64-server.so or libgame_shared-x86_64.so (client %d)!\n",
            iClientNum);
        return;
    }

    if (pHostServer != nullptr) {
        ::Kongor::NotifyObservedArcadeHostServer(pHostServer);
    }
    if (::Kongor::IsQueueArcadeModeActive()) {
        ::Kongor::EnsureQueueArcadeK2HooksInstalled();
    }

    orig_InitializeConnectionless(thisPtr, pHostServer, iClientNum, sName, bFakePlayer);

    if (isServerGameShared && thisPtr != nullptr && pHostServer != nullptr) {
        auto* pFlags = reinterpret_cast<uint16_t*>(static_cast<char*>(thisPtr) + 0x3ce);
        uint16_t oldFlags = *pFlags;
        *pFlags &= ~static_cast<uint16_t>(0x0101u | 0x0080u | 0x0040u); // Clear DISCONNECTED | WAS_DISCONNECTED | TERMINATED | LOADING

        auto* pDisconnectTime = reinterpret_cast<uint32_t*>(static_cast<char*>(thisPtr) + 0x428);
        uint32_t oldDiscTime = *pDisconnectTime;
        *pDisconnectTime = 0xFFFFFFFFU; // Disarm disconnect grace expiry timer (INVALID_TIME)

        std::fprintf(stdout,
            "[NativeGuard:Linux] CPlayer::InitializeConnectionless patched connectionless player client %d (bFakePlayer=%d, this=%p): flags 0x%x -> 0x%x (cleared 0x01c1), disconnectExpiry 0x%x -> 0xFFFFFFFF\n",
            iClientNum, bFakePlayer ? 1 : 0, thisPtr, (unsigned int)oldFlags, (unsigned int)(*pFlags), oldDiscTime);
        std::fflush(stdout);
    } else if (!isServerGameShared) {
        std::fprintf(stdout,
            "[NativeGuard:Linux] CPlayer::InitializeConnectionless forwarded cleanly to client libgame_shared-x86_64.so (client %d, bFakePlayer=%d, this=%p)\n",
            iClientNum, bFakePlayer ? 1 : 0, thisPtr);
        std::fflush(stdout);
    }
}

// 2. Interpose CConsole::Frame to drain queued IPC console commands on the K2 main thread
__attribute__((visibility("default")))
void _ZN8CConsole5FrameEv(void* thisPtr) {
    DisableLinuxArchiveChecksumsIfAllowed();
    {
        static uint32_t s_abiGuardCheckTick = 0;
        if ((++s_abiGuardCheckTick % 10u) == 1u) {
            EnsureGameSharedAbiGuardsApplied();
            ::Kongor::EnsureKillPacketGuardsApplied();
        }
    }
    using FnFrame = void(*)(void*);
    using FnAddCmdBuffer = void(*)(void*, const void*);

    static std::atomic<FnFrame> s_origFrame{ nullptr };
    static std::atomic<FnAddCmdBuffer> s_origAddCmdBuffer{ nullptr };

    FnFrame origFrame = s_origFrame.load(std::memory_order_acquire);
    if (!origFrame) {
        origFrame = reinterpret_cast<FnFrame>(
            ResolveLoadedLibrarySymbol("libk2-x86_64-server.so", "_ZN8CConsole5FrameEv"));
        if (!origFrame) {
            origFrame = reinterpret_cast<FnFrame>(
                ResolveLoadedLibrarySymbol("libk2-x86_64.so", "_ZN8CConsole5FrameEv"));
        }
        if (origFrame) {
            s_origFrame.store(origFrame, std::memory_order_release);
        }
    }

    FnAddCmdBuffer origAddCmdBuffer = s_origAddCmdBuffer.load(std::memory_order_acquire);
    if (!origAddCmdBuffer) {
        origAddCmdBuffer = reinterpret_cast<FnAddCmdBuffer>(
            ResolveLoadedLibrarySymbol(
                "libk2-x86_64-server.so",
                "_ZN8CConsole12AddCmdBufferERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE"));
        if (!origAddCmdBuffer) {
            origAddCmdBuffer = reinterpret_cast<FnAddCmdBuffer>(
                ResolveLoadedLibrarySymbol(
                    "libk2-x86_64.so",
                    "_ZN8CConsole12AddCmdBufferERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE"));
        }
        if (origAddCmdBuffer) {
            s_origAddCmdBuffer.store(origAddCmdBuffer, std::memory_order_release);
        }
    }

    if (!g_killSwitchActive.load(std::memory_order_relaxed) && thisPtr != nullptr && origAddCmdBuffer != nullptr) {
        char localCmds[CMD_QUEUE_CAPACITY][CMD_MAX_LEN];
        size_t localCount = 0;

        pthread_mutex_lock(&g_commandQueueMutex);
        if (g_pendingCmdCount > 0) {
            localCount = g_pendingCmdCount;
            std::memcpy(localCmds, g_pendingConsoleCommands, localCount * CMD_MAX_LEN);
            g_pendingCmdCount = 0;
        }
        pthread_mutex_unlock(&g_commandQueueMutex);

        for (size_t idx = 0; idx < localCount; ++idx) {
            if (localCmds[idx][0] == '\0') continue;

            wchar_t wcmd[CMD_MAX_LEN];
            size_t wlen = Utf8ToWcharBuf(localCmds[idx], wcmd, CMD_MAX_LEN);
            LibcxxFakeLongWString fakeStr{};
            fakeStr.capWithLongBit = ((wlen + 1) << 1) | 1ULL;
            fakeStr.size = wlen;
            fakeStr.data = wcmd;

            std::fprintf(stdout, "[NativeGuard:Linux] Dispatching queued console command on main thread: %s\n", localCmds[idx]);
            std::fflush(stdout);
            origAddCmdBuffer(thisPtr, &fakeStr);
        }
    }

    if (origFrame) {
        origFrame(thisPtr);
    }

    if (!g_killSwitchActive.load(std::memory_order_relaxed) && g_serverPort == 0 && thisPtr != nullptr) {
        using FnExec = void(*)(void*, const void*, unsigned int);
        static std::atomic<FnExec> s_clientExec{ nullptr };
        FnExec execFn = s_clientExec.load(std::memory_order_acquire);
        if (!execFn) {
            execFn = reinterpret_cast<FnExec>(
                ResolveLoadedLibrarySymbol(
                    "libk2-x86_64.so",
                    "_ZN8CConsole7ExecuteERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEj"));
            if (execFn) {
                s_clientExec.store(execFn, std::memory_order_release);
            }
        }
        if (execFn) {
            ::KongorLinuxDisplay::OnClientConsoleFrame(thisPtr, execFn);
        }
    }
}

// 2b. Interpose CConsole::Execute to dispatch privileged NET_CHAT_GS_REMOTE_COMMAND
// Queue Arcade commands (kongor_arcade_mode / kongor_arcade_reserve / kongor_arcade_release)
// on the dedicated server main thread without leaking unknown commands to K2.
// satisfies: OBL-QA-LINUX-05
__attribute__((visibility("default")))
void _ZN8CConsole7ExecuteERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEj(
    void* thisPtr, const void* sCmdLine, unsigned int flags)
{
    using FnExecute = void(*)(void*, const void*, unsigned int);
    static std::atomic<FnExecute> s_origExecute{ nullptr };

    FnExecute origExecute = s_origExecute.load(std::memory_order_acquire);
    if (!origExecute) {
        origExecute = reinterpret_cast<FnExecute>(
            ResolveLoadedLibrarySymbol(
                "libk2-x86_64-server.so",
                "_ZN8CConsole7ExecuteERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEj"));
        if (!origExecute) {
            origExecute = reinterpret_cast<FnExecute>(
                ResolveLoadedLibrarySymbol(
                    "libk2-x86_64.so",
                    "_ZN8CConsole7ExecuteERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEj"));
        }
        if (origExecute) {
            s_origExecute.store(origExecute, std::memory_order_release);
        }
    }

    if (!g_killSwitchActive.load(std::memory_order_relaxed)) {
        EnsureGameSharedAbiGuardsApplied();
        ::Kongor::EnsureKillPacketGuardsApplied();
    }

    if (!g_killSwitchActive.load(std::memory_order_relaxed) && sCmdLine != nullptr) {
        wchar_t cmdBuf[CMD_MAX_LEN] = {};
        if (::Kongor::ReadLibcxxWString(sCmdLine, cmdBuf, CMD_MAX_LEN) && cmdBuf[0] != L'\0') {
            // Client multi-monitor & frame-pacer commands (__kongor_sync_monitors / __kongor_apply_monitor)
            if (g_serverPort == 0 &&
                (wcsstr(cmdBuf, L"__kongor_") != nullptr || wcsstr(cmdBuf, L"__KONGOR_") != nullptr)) {
                size_t len = wcslen(cmdBuf);
                size_t segStart = 0;
                bool inQuotes = false;
                for (size_t i = 0; i <= len; ++i) {
                    wchar_t ch = cmdBuf[i];
                    if (ch == L'\"') {
                        inQuotes = !inQuotes;
                    }
                    if (ch == L'\0' || (!inQuotes && (ch == L';' || ch == L'\r' || ch == L'\n'))) {
                        size_t segLen = i - segStart;
                        if (segLen > 0 && segLen < CMD_MAX_LEN) {
                            wchar_t seg[CMD_MAX_LEN] = {};
                            wmemcpy(seg, cmdBuf + segStart, segLen);
                            seg[segLen] = L'\0';
                            if (!::KongorLinuxDisplay::TryDispatchDisplayConsoleCommandW(seg, thisPtr, origExecute, flags) && origExecute) {
                                LibcxxFakeLongWString fakeSeg{};
                                fakeSeg.capWithLongBit = ((segLen + 1) << 1) | 1ULL;
                                fakeSeg.size = segLen;
                                fakeSeg.data = seg;
                                origExecute(thisPtr, &fakeSeg, flags);
                            }
                        }
                        segStart = i + 1;
                    }
                }
                return;
            }

            // Quote-balanced compound ';' / '\r' / '\n' splitter when kongor_arcade_* is present
            bool hasArcadeToken = (wcsstr(cmdBuf, L"kongor_arcade_") != nullptr) ||
                                  (wcsstr(cmdBuf, L"KONGOR_ARCADE_") != nullptr);
            if (hasArcadeToken) {
                size_t len = wcslen(cmdBuf);
                size_t segStart = 0;
                bool inQuotes = false;
                for (size_t i = 0; i <= len; ++i) {
                    wchar_t ch = cmdBuf[i];
                    if (ch == L'\"') {
                        inQuotes = !inQuotes;
                    }
                    if (ch == L'\0' || (!inQuotes && (ch == L';' || ch == L'\r' || ch == L'\n'))) {
                        size_t segLen = i - segStart;
                        if (segLen > 0 && segLen < CMD_MAX_LEN) {
                            wchar_t seg[CMD_MAX_LEN] = {};
                            wmemcpy(seg, cmdBuf + segStart, segLen);
                            seg[segLen] = L'\0';
                            if (::Kongor::TryDispatchLinuxArcadeConsoleCmdW(seg)) {
                                if (::Kongor::ConsumePendingArcadeSafeModeRetirement()) {
                                    std::fprintf(stderr, "[NativeGuard:Linux] Retiring unusable Queue Arcade server cleanly via Quit.\n");
                                    std::fflush(stderr);
                                    if (origExecute) {
                                        wchar_t wquit[] = L"Quit";
                                        LibcxxFakeLongWString fakeQuit{};
                                        fakeQuit.capWithLongBit = ((4 + 1) << 1) | 1ULL;
                                        fakeQuit.size = 4;
                                        fakeQuit.data = wquit;
                                        origExecute(thisPtr, &fakeQuit, flags);
                                    }
                                    return;
                                }
                            } else if (origExecute) {
                                LibcxxFakeLongWString fakeSeg{};
                                fakeSeg.capWithLongBit = ((segLen + 1) << 1) | 1ULL;
                                fakeSeg.size = segLen;
                                fakeSeg.data = seg;
                                origExecute(thisPtr, &fakeSeg, flags);
                            }
                        }
                        segStart = i + 1;
                    }
                }
                return;
            }
            if (::Kongor::IsQueueArcadeModeActive()) {
                ::Kongor::EnsureQueueArcadeK2HooksInstalled();
            }
        }
    }

    if (origExecute) {
        origExecute(thisPtr, sCmdLine, flags);
    }
}

// 3. Interpose CReplayManager::StartRecording
// Binary ABI bug in HoN 5.0.0.0 Linux server: libgame_shared-x86_64-server.so (0xfbdc6d)
// compiled CHostServer with a -0x50 byte field offset shift vs libk2-x86_64-server.so,
// writing m_sReplayFilename (24-byte libc++ wstring) to CHostServer+0x46c0 instead of +0x4710.
// That clobbers CHostServer::m_hDiagnosticsFile (+0x46c0) with 0x11, m_uiLastDiagnosticsTime
// (+0x46c8), and m_mapDisconnectReasons (+0x46d0), causing an immediate SIGSEGV (CR2=0x11)
// in CHostServer::UpdateDiagnostics -> CFileHandle::IsOpen right after StartMatch.
__attribute__((visibility("default")))
bool _ZN14CReplayManager14StartRecordingERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE(
    void* thisPtr, const void* sFilename)
{
    (void)thisPtr;
    (void)sFilename;
    std::fprintf(stdout,
        "[NativeGuard:Linux] Guarded CReplayManager::StartRecording (prevented cross-SO CHostServer+0x46c0/+0x4710 ABI corruption)\n");
    std::fflush(stdout);
    return false;
}

// 4. Defense-in-depth pointer sanity guard on CFileHandle::IsOpen
__attribute__((visibility("default")))
bool _ZNK11CFileHandle6IsOpenEv(const void* thisPtr) {
    if (!thisPtr) return false;
    void* pFile = *reinterpret_cast<void* const*>(thisPtr);
    if (reinterpret_cast<uintptr_t>(pFile) < 0x10000ULL) {
        return false;
    }

    using FnIsOpen = bool(*)(const void*);
    static std::atomic<FnIsOpen> s_origIsOpen{ nullptr };
    FnIsOpen orig = s_origIsOpen.load(std::memory_order_acquire);
    if (!orig) {
        orig = reinterpret_cast<FnIsOpen>(
            ResolveLoadedLibrarySymbol("libk2-x86_64-server.so", "_ZNK11CFileHandle6IsOpenEv"));
        if (!orig) {
            orig = reinterpret_cast<FnIsOpen>(
                ResolveLoadedLibrarySymbol("libk2-x86_64.so", "_ZNK11CFileHandle6IsOpenEv"));
        }
        if (orig) {
            s_origIsOpen.store(orig, std::memory_order_release);
        }
    }
    return orig ? orig(thisPtr) : false;
}

// 5. Interpose CPlayer::CanAccessAltAvatar (1-arg and 2-arg overloads)
// Ensures all PLT callers also resolve Alt Avatar entitlements to true on Linux dedicated servers.
__attribute__((visibility("default")))
bool _ZN7CPlayer18CanAccessAltAvatarERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE(
    void* thisPtr, const void* sAltAvatar)
{
    (void)thisPtr;
    (void)sAltAvatar;
    ::Kongor::EnsureKillPacketGuardsApplied();
    static std::atomic<uint32_t> s_logCount{ 0 };
    if (s_logCount.fetch_add(1, std::memory_order_relaxed) < 8) {
        std::fprintf(stdout, "[NativeGuard:Linux] CPlayer::CanAccessAltAvatar(1-arg) -> permitted\n");
        std::fflush(stdout);
    }
    return true;
}

__attribute__((visibility("default")))
bool _ZN7CPlayer18CanAccessAltAvatarERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEES8_(
    void* thisPtr, const void* sHero, const void* sAltAvatar)
{
    (void)thisPtr;
    (void)sHero;
    (void)sAltAvatar;
    ::Kongor::EnsureKillPacketGuardsApplied();
    static std::atomic<uint32_t> s_logCount{ 0 };
    if (s_logCount.fetch_add(1, std::memory_order_relaxed) < 8) {
        std::fprintf(stdout, "[NativeGuard:Linux] CPlayer::CanAccessAltAvatar(2-arg) -> permitted\n");
        std::fflush(stdout);
    }
    return true;
}

// 6. Interpose CVisibilityMap::FillBitmap (Pebbles Duel 128x128 <-> MidWars 256x256 FoW bounds & transition guard)
// satisfies: OBL-FOW-01
// satisfies: OBL-FOW-02
__attribute__((visibility("default")))
void _ZN14CVisibilityMap10FillBitmapER7CBitmaphh(
    void* thisPtr, void* pBitmap, unsigned char u1, unsigned char u2)
{
    if (!thisPtr || !pBitmap) return;

    using FnFillBitmap = void(*)(void*, void*, unsigned char, unsigned char);
    static constexpr const char* kSymFillBitmap = "_ZN14CVisibilityMap10FillBitmapER7CBitmaphh";
    static std::atomic<FnFillBitmap> s_origFillBitmap{ nullptr };
    static std::atomic<uint32_t> s_lastMapWidth{ 0 };
    static std::atomic<uint32_t> s_lastMapHeight{ 0 };

    FnFillBitmap origFillBitmap = s_origFillBitmap.load(std::memory_order_acquire);
    if (!origFillBitmap) {
        origFillBitmap = reinterpret_cast<FnFillBitmap>(
            ResolveLoadedLibrarySymbol("libgame_shared-x86_64.so", kSymFillBitmap));
        if (!origFillBitmap) {
            origFillBitmap = reinterpret_cast<FnFillBitmap>(
                ResolveLoadedLibrarySymbol("libgame_shared-x86_64-server.so", kSymFillBitmap));
        }
        if (!origFillBitmap) {
            origFillBitmap = reinterpret_cast<FnFillBitmap>(
                ResolveLoadedLibrarySymbol("libgame_shared.so", kSymFillBitmap));
        }
        if (origFillBitmap) {
            s_origFillBitmap.store(origFillBitmap, std::memory_order_release);
        }
    }

    if (!origFillBitmap) {
        return;
    }

    const void* const pCells = *reinterpret_cast<const void* const*>(static_cast<const uint8_t*>(thisPtr) + 8);
    const void* const pBmpPixels = *reinterpret_cast<const void* const*>(pBitmap);
    const int32_t mapW = *reinterpret_cast<const int32_t*>(thisPtr);
    const int32_t mapH = *reinterpret_cast<const int32_t*>(static_cast<const uint8_t*>(thisPtr) + 4);
    const int32_t bmpW = *reinterpret_cast<const int32_t*>(static_cast<const uint8_t*>(pBitmap) + 0x0C);
    const int32_t bmpH = *reinterpret_cast<const int32_t*>(static_cast<const uint8_t*>(pBitmap) + 0x10);
    if (!pCells || !pBmpPixels || mapW <= 0 || mapH <= 0 || bmpW != mapW || bmpH != mapH) {
        return;
    }

    const uint32_t curW = static_cast<uint32_t>(mapW);
    const uint32_t curH = static_cast<uint32_t>(mapH);
    const uint32_t prevW = s_lastMapWidth.load(std::memory_order_relaxed);
    const uint32_t prevH = s_lastMapHeight.load(std::memory_order_relaxed);
    if (curW != prevW || curH != prevH) {
        s_lastMapWidth.store(curW, std::memory_order_release);
        s_lastMapHeight.store(curH, std::memory_order_release);
        if (prevW != 0 || prevH != 0) {
            static constexpr const char kRefreshCmd[] =
                "UICmd \"do local v=GetCvarNumber('cg_fogofwarDarkness_min'); if v then Set('cg_fogofwarDarkness_min', v+0.0001); Set('cg_fogofwarDarkness_min', v) end end\"";
            EnqueueLinuxConsoleCommand(kRefreshCmd, sizeof(kRefreshCmd) - 1);
            std::fprintf(
                stdout,
                "[KONGOR LINUX GUARD] [FOW_GUARD] CVisibilityMap::FillBitmap map dimension transition (%ux%u -> %ux%u); queued FoW CVar recomputation\n",
                prevW, prevH, curW, curH);
            std::fflush(stdout);
        }
    }

    origFillBitmap(thisPtr, pBitmap, u1, u2);
}

// 7. Interpose legacy 4.10.1.0 GameMode Token/Pass paywall checks (SHJ-PRE-01)
// Linux hon-x86_64 (4.10.1.0) ships Garena/Frostburn-era game mode token/pass gates in
// libk2-x86_64.so (called via PLT by game-x86_64.so CGameServer::AddClient). When a Linux
// player launches non-"normal" modes (e.g. soccer / Strikers of Newerth) from Offline Hub
// without a MasterServer session cookie (accountType != 3, tokens == 0),
// CClientConnection::CanAccessGameMode returns false and immediately kicks the local client
// with 'disconnect_no_tokens' ("You need tokens or a pass to play this mode.").
// Project Kongor has zero token/pass paywalls (matching Windows 5.0.0.0 parity).
__attribute__((visibility("default")))
bool _ZNK17CClientConnection17CanAccessGameModeERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE(
    const void* thisPtr, const void* sMode)
{
    (void)thisPtr;
    (void)sMode;
    static std::atomic<uint32_t> s_logCount{ 0 };
    if (s_logCount.fetch_add(1, std::memory_order_relaxed) < 8) {
        std::fprintf(stdout,
            "[NativeGuard:Linux] CClientConnection::CanAccessGameMode -> permitted (SHJ-PRE-01 token/pass gate disabled)\n");
        std::fflush(stdout);
    }
    return true;
}

__attribute__((visibility("default")))
bool _ZNK14CClientAccount17CanAccessGameModeERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE(
    const void* thisPtr, const void* sMode)
{
    (void)thisPtr;
    (void)sMode;
    static std::atomic<uint32_t> s_logCount{ 0 };
    if (s_logCount.fetch_add(1, std::memory_order_relaxed) < 8) {
        std::fprintf(stdout,
            "[NativeGuard:Linux] CClientAccount::CanAccessGameMode -> permitted (SHJ-PRE-01 token/pass gate disabled)\n");
        std::fflush(stdout);
    }
    return true;
}

__attribute__((visibility("default")))
bool _ZNK14CClientAccount10NeedsTokenERKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEE(
    const void* thisPtr, const void* sMode)
{
    (void)thisPtr;
    (void)sMode;
    static std::atomic<uint32_t> s_logCount{ 0 };
    if (s_logCount.fetch_add(1, std::memory_order_relaxed) < 8) {
        std::fprintf(stdout,
            "[NativeGuard:Linux] CClientAccount::NeedsToken -> false (SHJ-PRE-01 token/pass gate disabled)\n");
        std::fflush(stdout);
    }
    return false;
}

} // extern "C"

// ============================================================================
// Linux Queue Arcade Mini-Game ("While You Wait") Implementation
// Target: hon-x86_64-server (libk2-x86_64.so, libgame_shared-x86_64-server.so)
// Dossier: artifacts/evidence/queue_arcade_linux_dossier.json
// ============================================================================

namespace Kongor {

// satisfies: OBL-QA-LINUX-00
__thread LinuxCallFaultGuard t_callFaultGuard = {};

struct LinuxArcadeSlotOriginEntry {
    bool        populated;
    const void* botDef;
    wchar_t     originalBotName[64];
};

static pthread_rwlock_t g_linuxArcadeReservationLock = PTHREAD_RWLOCK_INITIALIZER;
static pthread_mutex_t g_linuxArcadeHookInstallLock = PTHREAD_MUTEX_INITIALIZER;
static ArcadeReservationEntry g_linuxArcadeReservations[kMaxArcadeReservations] = {};
static LinuxArcadeSlotOriginEntry g_linuxArcadeSlotOrigins[kMaxArcadeReservations] = {};

static std::atomic<int32_t> g_linuxArcadeMode{ -1 }; // -1 = uninitialized
static std::atomic<int32_t> g_linuxArcadeSwapHooksEnabled{ -1 }; // -1 = uninitialized, 0 = disabled, 1 = enabled
static std::atomic<void*> g_observedLinuxHostServer{ nullptr };
static std::atomic<uint32_t> g_linuxArcadeKickInProgressCount{ 0 };
static __thread uint32_t t_linuxArcadeKickReentrancyDepth = 0;
static std::atomic<bool> g_linuxClientIdHookArmed{ false };
static std::atomic<bool> g_linuxArcadeHooksInstalled{ false };
static std::atomic<bool> g_linuxSafeModeActive{ false };
static char g_linuxSafeModeReason[128] = { 0 };
static std::atomic<bool> g_linuxPendingSafeModeRetirement{ false };

typedef int (*PFN_CHostServer_GenerateClientID)(void* pHostServer, uint16_t* pConnId, int accountId);
static PFN_CHostServer_GenerateClientID g_origGenerateClientID = nullptr;

typedef void (*PFN_CPlayer_SpawnHero)(void* thisPtr);
static PFN_CPlayer_SpawnHero g_origSpawnHero = nullptr;

typedef void (*PFN_CPlayer_Terminate)(void* thisPtr);
static PFN_CPlayer_Terminate g_origTerminate = nullptr;

typedef void (*PFN_CPlayer_Disconnected)(void* thisPtr, uint32_t time, uint32_t maxDiscTime);
static PFN_CPlayer_Disconnected g_origDisconnected = nullptr;

typedef void* (*PFN_CPlayer_AttachBot)(void* thisPtr, const void* pBotDef);
static PFN_CPlayer_AttachBot g_origAttachBot = nullptr;

static LinuxQueueArcadeEngineContext g_liveLinuxArcadeCtx = {};
static std::atomic<bool> g_liveLinuxArcadeCtxResolved{ false };

static void LogArcadeLine(const char* msg) noexcept {
    if (!msg) return;
    std::fprintf(stdout, "%s", msg);
    std::fflush(stdout);
}

static void ResetLinuxArcadeSlotOriginsLocked() noexcept {
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        g_linuxArcadeSlotOrigins[i] = {};
    }
}

static void EnqueueLinuxArcadeConsoleCmdA(const char* cmdLine) noexcept {
    if (!cmdLine || cmdLine[0] == '\0') return;
    EnqueueLinuxConsoleCommand(cmdLine, std::strlen(cmdLine));
}

static void SanitizeLinuxChatTokenA(const wchar_t* rawW, char* outBuf, size_t outCap, const char* fallback) noexcept {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = '\0';
    char rawUtf8[192] = {};
    if (rawW && rawW[0] != L'\0') {
        WcharToUtf8Buf(rawW, rawUtf8, sizeof(rawUtf8));
    }
    const char* src = (rawUtf8[0] != '\0') ? rawUtf8 : (fallback ? fallback : "Player");
    size_t dst = 0;
    for (size_t i = 0; src[i] != '\0' && dst + 1 < outCap; ++i) {
        const unsigned char uch = static_cast<unsigned char>(src[i]);
        if (uch < 0x20u || src[i] == '"' || src[i] == ';' || src[i] == '\\' || src[i] == '\r' || src[i] == '\n') {
            continue;
        }
        outBuf[dst++] = src[i];
    }
    outBuf[dst] = '\0';
    if (dst == 0 && fallback && fallback[0] != '\0') {
        std::snprintf(outBuf, outCap, "%s", fallback);
    }
}

static void QueueLinuxArcadeSwapChatNotice(
    const LinuxQueueArcadeEngineContext* ctx,
    const wchar_t* humanName,
    const wchar_t* botName,
    bool isJoin
) noexcept {
    if (!ctx || !ctx->pfnQueueConsoleCommandA) return;
    char safeHuman[96] = {};
    char safeBot[96] = {};
    SanitizeLinuxChatTokenA(humanName, safeHuman, sizeof(safeHuman), "Player");
    SanitizeLinuxChatTokenA(botName, safeBot, sizeof(safeBot), "ArcadeBot");

    char cmdBuf[256] = {};
    if (isJoin) {
        std::snprintf(cmdBuf, sizeof(cmdBuf),
            "ServerChat \"[Queue Arcade] %s replaced %s!\"", safeHuman, safeBot);
    } else {
        std::snprintf(cmdBuf, sizeof(cmdBuf),
            "ServerChat \"[Queue Arcade] %s left - %s took over!\"", safeHuman, safeBot);
    }
    ctx->pfnQueueConsoleCommandA(cmdBuf);

    char logBuf[320] = {};
    std::snprintf(logBuf, sizeof(logBuf), "[KONGOR QUEUE ARCADE] Queued swap chat notice: %s\n", cmdBuf);
    LogArcadeLine(logBuf);
}

static void* ResolveEffectiveLinuxHostServer(const LinuxQueueArcadeEngineContext* ctx) noexcept {
    if (!ctx) {
        return g_observedLinuxHostServer.load(std::memory_order_acquire);
    }
    if (ctx->pHostServerOverride != nullptr) {
        return ctx->pHostServerOverride;
    }
    void* volatile pResolved = nullptr;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        if (ctx->ppHost && IsValidReadableMemory(ctx->ppHost, sizeof(void*))) {
            void* pHost = *ctx->ppHost;
            if (pHost && IsValidReadableMemory(
                    reinterpret_cast<const uint8_t*>(pHost) + LinuxOffsets::kHostServerPtrOnCHost,
                    sizeof(void*))) {
                pResolved = *reinterpret_cast<void* const*>(
                    reinterpret_cast<const uint8_t*>(pHost) + LinuxOffsets::kHostServerPtrOnCHost);
            }
        }
    } LINUX_CALL_FAULT_GUARD_END({
        pResolved = nullptr;
    });
    if (pResolved != nullptr) {
        return pResolved;
    }
    return g_observedLinuxHostServer.load(std::memory_order_acquire);
}

static int32_t ReadCurrentLinuxGamePhase(const LinuxQueueArcadeEngineContext* ctx) noexcept {
    if (!ctx || !ctx->ppGame) return 0;
    int32_t phase = 0;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        void* pGameVoid = *ctx->ppGame;
        if (pGameVoid && IsValidReadableMemory(pGameVoid, LinuxOffsets::kGameInfo + sizeof(void*))) {
            void* pGameInfo = *reinterpret_cast<void* const*>(
                reinterpret_cast<const uint8_t*>(pGameVoid) + LinuxOffsets::kGameInfo);
            if (pGameInfo && IsValidReadableMemory(pGameInfo, LinuxOffsets::kGameInfoPhase + sizeof(int32_t))) {
                phase = *reinterpret_cast<const int32_t*>(
                    reinterpret_cast<const uint8_t*>(pGameInfo) + LinuxOffsets::kGameInfoPhase);
            }
        }
    } LINUX_CALL_FAULT_GUARD_END({
        phase = 0;
    });
    return phase;
}

bool IsLinuxSafeModeActive() noexcept {
    return g_linuxSafeModeActive.load(std::memory_order_acquire);
}

void SetLinuxSafeMode(bool active, const char* reason) noexcept {
    g_linuxSafeModeActive.store(active, std::memory_order_release);
    if (reason && reason[0] != '\0') {
        std::snprintf(g_linuxSafeModeReason, sizeof(g_linuxSafeModeReason), "%s", reason);
    }
}

QueueArcadeServerMode GetQueueArcadeMode() noexcept {
    int32_t cur = g_linuxArcadeMode.load(std::memory_order_acquire);
    if (cur >= 0) {
        return static_cast<QueueArcadeServerMode>(cur);
    }
    const char* envVal = std::getenv("KONGOR_QUEUE_ARCADE");
    int32_t resolved = 0;
    if (envVal && envVal[0] != '\0') {
        if (envVal[0] == '2') {
            resolved = 2;
        } else if (envVal[0] == '1' || envVal[0] == 'y' || envVal[0] == 'Y' || envVal[0] == 't' || envVal[0] == 'T') {
            resolved = 1;
        }
    }
    int32_t expected = -1;
    if (g_linuxArcadeMode.compare_exchange_strong(expected, resolved, std::memory_order_acq_rel)) {
        return static_cast<QueueArcadeServerMode>(resolved);
    }
    return static_cast<QueueArcadeServerMode>(expected);
}

bool IsQueueArcadeModeActive() noexcept {
    return GetQueueArcadeMode() != QueueArcadeServerMode::Disabled;
}

void SetQueueArcadeMode(QueueArcadeServerMode mode) noexcept {
    const int32_t clamped = (mode == QueueArcadeServerMode::PartyDormantSlots) ? 2
        : (mode == QueueArcadeServerMode::ActiveBots ? 1 : 0);
    const int32_t prev = g_linuxArcadeMode.exchange(clamped, std::memory_order_acq_rel);
    if (clamped == 0 || prev <= 0) {
        pthread_rwlock_wrlock(&g_linuxArcadeReservationLock);
        ResetLinuxArcadeSlotOriginsLocked();
        pthread_rwlock_unlock(&g_linuxArcadeReservationLock);
    }

    char logBuf[192];
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE:Linux] SetQueueArcadeMode -> %d (%s)\n",
        clamped,
        clamped == 2 ? "PartyDormantSlots" : (clamped == 1 ? "ActiveBots" : "Disabled"));
    LogArcadeLine(logBuf);

    if (clamped > 0) {
        EnsureQueueArcadeK2HooksInstalled();
        if (clamped == 2) {
            ArcadeParkUnreservedBots(nullptr);
        }
    }
}

void RefreshQueueArcadeSwapHooksFromEnv() noexcept {
    const char* envVal = std::getenv("KONGOR_ARCADE_SWAP_HOOKS");
    int32_t resolved = 1; // Default enabled
    if (envVal && envVal[0] != '\0') {
        if (std::strcmp(envVal, "0") == 0 ||
            strcasecmp(envVal, "false") == 0 ||
            strcasecmp(envVal, "no") == 0 ||
            strcasecmp(envVal, "off") == 0) {
            resolved = 0;
        }
    }
    g_linuxArcadeSwapHooksEnabled.store(resolved, std::memory_order_release);
    char logBuf[192];
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] KONGOR_ARCADE_SWAP_HOOKS resolved -> %d (%s)\n",
        resolved, resolved ? "enabled" : "disabled_stock_fallback");
    LogArcadeLine(logBuf);
}

bool IsQueueArcadeSwapHooksEnabled() noexcept {
    int32_t cur = g_linuxArcadeSwapHooksEnabled.load(std::memory_order_acquire);
    if (cur >= 0) {
        return cur != 0;
    }
    RefreshQueueArcadeSwapHooksFromEnv();
    return g_linuxArcadeSwapHooksEnabled.load(std::memory_order_acquire) != 0;
}

void SetQueueArcadeSwapHooksEnabled(bool enabled) noexcept {
    const int32_t val = enabled ? 1 : 0;
    g_linuxArcadeSwapHooksEnabled.store(val, std::memory_order_release);
    char logBuf[192];
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] SetQueueArcadeSwapHooksEnabled -> %d (%s)\n",
        val, enabled ? "enabled" : "disabled_stock_fallback");
    LogArcadeLine(logBuf);
}

void NotifyObservedArcadeHostServer(void* pHostServer) noexcept {
    if (pHostServer != nullptr) {
        g_observedLinuxHostServer.store(pHostServer, std::memory_order_release);
    }
}

bool IsArcadeKickReentrancyActive() noexcept {
    return t_linuxArcadeKickReentrancyDepth > 0 ||
           g_linuxArcadeKickInProgressCount.load(std::memory_order_acquire) > 0;
}

void RecordArcadeSlotOrigin(
    int32_t clientNum,
    const void* pBotDef,
    const wchar_t* originalBotName
) noexcept {
    if (clientNum < 0 || static_cast<size_t>(clientNum) >= kMaxArcadeReservations) {
        return;
    }
    pthread_rwlock_wrlock(&g_linuxArcadeReservationLock);
    LinuxArcadeSlotOriginEntry& entry = g_linuxArcadeSlotOrigins[clientNum];
    if (pBotDef != nullptr) {
        entry.botDef = pBotDef;
        entry.populated = true;
    }
    if (originalBotName && *originalBotName && entry.originalBotName[0] == L'\0') {
        wcsncpy(entry.originalBotName, originalBotName, sizeof(entry.originalBotName) / sizeof(wchar_t) - 1);
        entry.originalBotName[sizeof(entry.originalBotName) / sizeof(wchar_t) - 1] = L'\0';
        entry.populated = true;
    }
    pthread_rwlock_unlock(&g_linuxArcadeReservationLock);
}

bool GetArcadeSlotOrigin(
    int32_t clientNum,
    const void** outBotDef,
    wchar_t* outOriginalBotName,
    size_t nameCap
) noexcept {
    if (outBotDef) *outBotDef = nullptr;
    if (outOriginalBotName && nameCap > 0) outOriginalBotName[0] = L'\0';
    if (clientNum < 0 || static_cast<size_t>(clientNum) >= kMaxArcadeReservations) {
        return false;
    }
    pthread_rwlock_rdlock(&g_linuxArcadeReservationLock);
    const LinuxArcadeSlotOriginEntry& entry = g_linuxArcadeSlotOrigins[clientNum];
    const bool populated = entry.populated;
    if (outBotDef) *outBotDef = entry.botDef;
    if (outOriginalBotName && nameCap > 1 && entry.originalBotName[0] != L'\0') {
        wcsncpy(outOriginalBotName, entry.originalBotName, nameCap - 1);
        outOriginalBotName[nameCap - 1] = L'\0';
    }
    pthread_rwlock_unlock(&g_linuxArcadeReservationLock);
    return populated;
}

bool IsQueueArcadeClientIdHookArmed() noexcept {
    if (g_linuxSafeModeActive.load(std::memory_order_acquire)) {
        return false;
    }
    return g_linuxClientIdHookArmed.load(std::memory_order_acquire) && (g_origGenerateClientID != nullptr);
}

bool ConsumePendingArcadeSafeModeRetirement() noexcept {
    return g_linuxPendingSafeModeRetirement.exchange(false, std::memory_order_acq_rel);
}

// Module discovery via dl_iterate_phdr with /proc/self/maps fallback
// satisfies: OBL-QA-LINUX-11
uintptr_t FindModuleBaseByName(const char* libSuffix) noexcept {
    if (!libSuffix || libSuffix[0] == '\0') return 0;

    struct SearchCtx {
        const char* suffix;
        uintptr_t base;
    } sctx = { libSuffix, 0 };

    dl_iterate_phdr([](struct dl_phdr_info* info, size_t, void* data) -> int {
        auto* sc = static_cast<SearchCtx*>(data);
        if (info && info->dlpi_name && std::strstr(info->dlpi_name, sc->suffix) != nullptr) {
            sc->base = static_cast<uintptr_t>(info->dlpi_addr);
            return 1;
        }
        return 0;
    }, &sctx);

    if (sctx.base != 0) {
        return sctx.base;
    }

    std::FILE* maps = std::fopen("/proc/self/maps", "r");
    if (!maps) return 0;

    char line[1024];
    while (std::fgets(line, sizeof(line), maps) != nullptr) {
        if (std::strstr(line, libSuffix) != nullptr) {
            uintptr_t start = 0;
            if (std::sscanf(line, "%lx-", &start) == 1 && start != 0) {
                sctx.base = start;
                break;
            }
        }
    }
    std::fclose(maps);
    return sctx.base;
}

// Instruction decoder checking for branch or RIP-relative opcodes
// satisfies: OBL-QA-LINUX-11
bool VerifyLinuxPrologueNoRipRelative(const uint8_t* code, size_t len) noexcept {
    if (!code || len < 14) return false;
    for (size_t i = 0; i < len; ++i) {
        uint8_t b = code[i];
        if (b == 0xE8 || b == 0xE9 || b == 0xEB) return false;
        if (b >= 0x70 && b <= 0x7F) return false;
        if (b == 0x0F && (i + 1 < len)) {
            uint8_t next = code[i + 1];
            if (next >= 0x80 && next <= 0x8F) return false;
        }
        if (b == 0x8D || b == 0x8B || b == 0x89 || b == 0x3B || b == 0x39) {
            if (i + 1 < len) {
                uint8_t modrm = code[i + 1];
                if ((modrm & 0xC7) == 0x05) return false;
            }
        }
    }
    return true;
}

// Allocates a strict W^X trampoline and installs a 14-byte FF 25 detour
// satisfies: OBL-QA-LINUX-11
void* InstallLinuxInlineTrampoline64(
    void* targetFunc,
    void* hookFunc,
    const uint8_t* expectedPrologue,
    size_t prologueLen
) noexcept {
    if (!targetFunc || !hookFunc || prologueLen < 14 || prologueLen > 32) {
        return nullptr;
    }
    if (!IsValidReadableMemory(targetFunc, prologueLen)) {
        return nullptr;
    }
    if (expectedPrologue && std::memcmp(targetFunc, expectedPrologue, prologueLen) != 0) {
        std::fprintf(stderr,
            "[NativeGuard:Linux] ERROR: Prologue verification mismatch at %p (expected prologue does not match). SafeMode fail-closed.\n",
            targetFunc);
        std::fflush(stderr);
        return nullptr;
    }
    if (!VerifyLinuxPrologueNoRipRelative(static_cast<const uint8_t*>(targetFunc), prologueLen)) {
        std::fprintf(stderr,
            "[NativeGuard:Linux] ERROR: Instruction decoding rejected RIP-relative or branch opcode in prologue at %p. SafeMode fail-closed.\n",
            targetFunc);
        std::fflush(stderr);
        return nullptr;
    }

    // Allocate trampoline page as PROT_READ | PROT_WRITE (never PROT_EXEC on mmap)
    void* tramp = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (tramp == MAP_FAILED) {
        return nullptr;
    }

    // Copy relocated prologue instructions
    std::memcpy(tramp, targetFunc, prologueLen);

    // Append 14-byte absolute indirect return jump: FF 25 00 00 00 00 <uint64_t retAddr>
    uint8_t* jmpPtr = static_cast<uint8_t*>(tramp) + prologueLen;
    jmpPtr[0] = 0xFF;
    jmpPtr[1] = 0x25;
    *reinterpret_cast<uint32_t*>(jmpPtr + 2) = 0; // disp32 = 0 ([RIP+0])
    uintptr_t retAddr = reinterpret_cast<uintptr_t>(targetFunc) + prologueLen;
    *reinterpret_cast<uint64_t*>(jmpPtr + 6) = static_cast<uint64_t>(retAddr);

    // Seal trampoline to PROT_READ | PROT_EXEC and flush instruction cache
    if (mprotect(tramp, 4096, PROT_READ | PROT_EXEC) != 0) {
        munmap(tramp, 4096);
        return nullptr;
    }
    __builtin___clear_cache(static_cast<char*>(tramp), static_cast<char*>(tramp) + 4096);

    // Modify target code page permissions: mprotect RWX, patch jump, restore RX
    long pageSize = sysconf(_SC_PAGESIZE);
    if (pageSize <= 0) pageSize = 4096;
    uintptr_t targetAddr = reinterpret_cast<uintptr_t>(targetFunc);
    uintptr_t pageStart = targetAddr & ~static_cast<uintptr_t>(pageSize - 1);
    size_t mapLen = ((targetAddr + prologueLen) > (pageStart + static_cast<uintptr_t>(pageSize)))
        ? static_cast<size_t>(pageSize) * 2
        : static_cast<size_t>(pageSize);

    if (mprotect(reinterpret_cast<void*>(pageStart), mapLen, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) {
        munmap(tramp, 4096);
        return nullptr;
    }

    uint8_t* p = static_cast<uint8_t*>(targetFunc);
    p[0] = 0xFF;
    p[1] = 0x25;
    *reinterpret_cast<uint32_t*>(p + 2) = 0;
    *reinterpret_cast<uint64_t*>(p + 6) = reinterpret_cast<uint64_t>(hookFunc);
    for (size_t i = 14; i < prologueLen; ++i) {
        p[i] = 0x90; // NOP padding
    }

    mprotect(reinterpret_cast<void*>(pageStart), mapLen, PROT_READ | PROT_EXEC);
    __builtin___clear_cache(static_cast<char*>(targetFunc), static_cast<char*>(targetFunc) + prologueLen);

    return tramp;
}

static const LinuxQueueArcadeEngineContext* ResolveLiveLinuxArcadeEngineContext() noexcept {
    if (g_liveLinuxArcadeCtxResolved.load(std::memory_order_acquire)) {
        return &g_liveLinuxArcadeCtx;
    }

    uintptr_t baseGS = FindModuleBaseByName("libgame_shared-x86_64-server.so");
    uintptr_t baseK2 = FindModuleBaseByName("libk2-x86_64.so");
    if (baseK2 == 0) {
        baseK2 = FindModuleBaseByName("libk2-x86_64-server.so");
    }

    LinuxQueueArcadeEngineContext ctx = {};
    void* sym_s_pGame = ResolveLoadedLibrarySymbol("libgame_shared-x86_64-server.so", "_ZN5IGame7s_pGameE");
    ctx.ppGame = reinterpret_cast<void**>(sym_s_pGame ? sym_s_pGame
        : (baseGS ? reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaIGameInstance) : nullptr));

    void* sym_RemoveBot = ResolveLoadedLibrarySymbol("libgame_shared-x86_64-server.so", "_ZN7CPlayer9RemoveBotEv");
    ctx.pfnRemoveBot = reinterpret_cast<decltype(ctx.pfnRemoveBot)>(sym_RemoveBot ? sym_RemoveBot
        : (baseGS ? reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaRemoveBot) : nullptr));

    void* sym_AttachBot = ResolveLoadedLibrarySymbol("libgame_shared-x86_64-server.so", "_ZN7CPlayer9AttachBotEPK14CBotDefinition");
    ctx.pfnAttachBot = reinterpret_cast<decltype(ctx.pfnAttachBot)>(sym_AttachBot ? sym_AttachBot
        : (baseGS ? reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaAttachBot) : nullptr));

    void* sym_NetMgr = ResolveLoadedLibrarySymbol("libk2-x86_64-server.so", "g_NetworkResourceManager");
    if (!sym_NetMgr) {
        sym_NetMgr = ResolveLoadedLibrarySymbol("libk2-x86_64.so", "g_NetworkResourceManager");
    }
    ctx.ppNetResourceManager = reinterpret_cast<void**>(sym_NetMgr);

    void* sym_SetNetStr = ResolveLoadedLibrarySymbol(
        "libk2-x86_64-server.so",
        "_ZN23CNetworkResourceManager9SetStringEtRKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEb");
    if (!sym_SetNetStr) {
        sym_SetNetStr = ResolveLoadedLibrarySymbol(
            "libk2-x86_64.so",
            "_ZN23CNetworkResourceManager9SetStringEtRKNSt3__112basic_stringIwNS0_11char_traitsIwEE17K2StringAllocatorIwEEEb");
    }
    ctx.pfnSetNetString = reinterpret_cast<decltype(ctx.pfnSetNetString)>(sym_SetNetStr);

    void* sym_GetNetStr = ResolveLoadedLibrarySymbol(
        "libk2-x86_64-server.so",
        "_ZN23CNetworkResourceManager9GetStringEt");
    if (!sym_GetNetStr) {
        sym_GetNetStr = ResolveLoadedLibrarySymbol(
            "libk2-x86_64.so",
            "_ZN23CNetworkResourceManager9GetStringEt");
    }
    ctx.pfnGetNetString = reinterpret_cast<decltype(ctx.pfnGetNetString)>(sym_GetNetStr);

    void* sym_Host = ResolveLoadedLibrarySymbol("libk2-x86_64-server.so", "g_pHost");
    if (!sym_Host) {
        sym_Host = ResolveLoadedLibrarySymbol("libk2-x86_64.so", "g_pHost");
    }
    ctx.ppHost = reinterpret_cast<void**>(sym_Host);

    void* sym_GetClient = ResolveLoadedLibrarySymbol("libk2-x86_64-server.so", "_ZN11CHostServer9GetClientEi");
    if (!sym_GetClient) {
        sym_GetClient = ResolveLoadedLibrarySymbol("libk2-x86_64.so", "_ZN11CHostServer9GetClientEi");
    }
    ctx.pfnGetClient = reinterpret_cast<decltype(ctx.pfnGetClient)>(sym_GetClient ? sym_GetClient
        : (baseK2 ? reinterpret_cast<void*>(baseK2 + LinuxOffsets::kRvaHostServerGetClient) : nullptr));

    void* sym_KickClient = ResolveLoadedLibrarySymbol("libk2-x86_64-server.so", "_ZN11CHostServer10KickClientEi17EDisconnectReason");
    if (!sym_KickClient) {
        sym_KickClient = ResolveLoadedLibrarySymbol("libk2-x86_64.so", "_ZN11CHostServer10KickClientEi17EDisconnectReason");
    }
    ctx.pfnKickClient = reinterpret_cast<decltype(ctx.pfnKickClient)>(sym_KickClient ? sym_KickClient
        : (baseK2 ? reinterpret_cast<void*>(baseK2 + LinuxOffsets::kRvaHostServerKickClient) : nullptr));

    ctx.pfnQueueConsoleCommandA = &EnqueueLinuxArcadeConsoleCmdA;

    if (ctx.ppGame && ctx.pfnRemoveBot) {
        g_liveLinuxArcadeCtx = ctx;
        g_liveLinuxArcadeCtxResolved.store(true, std::memory_order_release);
        LogArcadeLine("[KONGOR QUEUE ARCADE:Linux] Resolved IGame::s_pGame, CPlayer::RemoveBot/AttachBot, and CHostServer GetClient/KickClient\n");
        if (ctx.ppNetResourceManager && *ctx.ppNetResourceManager && ctx.pfnSetNetString && ctx.pfnGetNetString) {
            LogArcadeLine("[KONGOR QUEUE ARCADE:Linux] Resolved g_NetworkResourceManager, SetString, and GetString\n");
        } else {
            LogArcadeLine("[KONGOR QUEUE ARCADE:Linux WARNING] Failed to resolve g_NetworkResourceManager, SetString, or GetString!\n");
        }
    }
    return &g_liveLinuxArcadeCtx;
}

void OnArcadePlayerAttachBot(
    void* pPlayer,
    const void* pBotDef,
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer || !pBotDef) return;
    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();

    int32_t clientNum = -1;
    uint8_t isBot = 0;
    int32_t accId = 0;
    uint16_t nameIdx = 0xFFFFu;
    volatile bool validPlayer = false;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        if (IsValidReadableMemory(pPlayer, LinuxOffsets::kPlayerBotBrain + sizeof(void*))) {
            const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
            clientNum = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerClientNumber);
            isBot = *reinterpret_cast<const uint8_t*>(addr + LinuxOffsets::kPlayerIsBot);
            accId = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerAccountID);
            nameIdx = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerNameIndex);
            validPlayer = (clientNum >= 0);
        }
    } LINUX_CALL_FAULT_GUARD_END({
        validPlayer = false;
    });
    if (!validPlayer || clientNum < 0 || static_cast<size_t>(clientNum) >= kMaxArcadeReservations) {
        return;
    }

    wchar_t botName[64] = {};
    if (accId <= 0 && ctx && ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
        ctx->pfnGetNetString && nameIdx != 0xFFFFu) {
        const void* pStr = ctx->pfnGetNetString(*ctx->ppNetResourceManager, nameIdx);
        ReadLibcxxWString(pStr, botName, sizeof(botName) / sizeof(wchar_t));
    }

    RecordArcadeSlotOrigin(clientNum, pBotDef, (accId <= 0 && botName[0] != L'\0') ? botName : nullptr);
    char botUtf8[64] = {};
    WcharToUtf8Buf(botName, botUtf8, sizeof(botUtf8));
    char logBuf[256] = {};
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] Recorded ArcadeSlotOrigin[clientNum=%d]: botDef=%p initialBotName='%s' isBot=%u\n",
        clientNum, pBotDef, botUtf8, static_cast<unsigned>(isBot));
    LogArcadeLine(logBuf);
}

static void UpsertReservationLocked(
    int32_t accountId,
    int32_t clientNum,
    int32_t teamId,
    const wchar_t* playerName,
    const wchar_t* savedBotName,
    void* detachedBotBrain,
    int32_t* outDisplacedClientNum,
    void** outDisplacedBotBrain
) noexcept {
    if (outDisplacedClientNum) *outDisplacedClientNum = -1;
    if (outDisplacedBotBrain) *outDisplacedBotBrain = nullptr;

    size_t freeIdx = kMaxArcadeReservations;
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_linuxArcadeReservations[i].active) {
            if (g_linuxArcadeReservations[i].accountId == accountId ||
                g_linuxArcadeReservations[i].clientNum == clientNum ||
                (playerName && *playerName && NamesMatchPlayerW(g_linuxArcadeReservations[i].playerName, playerName))) {
                freeIdx = i;
                break;
            }
        } else if (freeIdx == kMaxArcadeReservations) {
            freeIdx = i;
        }
    }
    if (freeIdx == kMaxArcadeReservations) {
        freeIdx = 0; // Overwrite oldest slot if saturated
    }

    ArcadeReservationEntry& dst = g_linuxArcadeReservations[freeIdx];
    if (dst.active && dst.parkedBotBrain != nullptr && dst.clientNum != clientNum) {
        if (outDisplacedClientNum) *outDisplacedClientNum = dst.clientNum;
        if (outDisplacedBotBrain) *outDisplacedBotBrain = dst.parkedBotBrain;
        dst.parkedBotBrain = nullptr;
    }

    const bool keepExistingSavedBot = dst.active && dst.clientNum == clientNum && dst.savedBotName[0] != L'\0';
    const bool keepExistingParkedBrain = dst.active && dst.clientNum == clientNum && dst.parkedBotBrain != nullptr && detachedBotBrain == nullptr;
    dst.active = true;
    dst.accountId = accountId;
    dst.clientNum = clientNum;
    dst.teamId = teamId;
    if (!keepExistingParkedBrain) {
        dst.parkedBotBrain = detachedBotBrain;
    }
    dst.playerName[0] = L'\0';
    if (playerName && *playerName) {
        wcsncpy(dst.playerName, playerName, sizeof(dst.playerName) / sizeof(wchar_t) - 1);
    }
    if (!keepExistingSavedBot) {
        dst.savedBotName[0] = L'\0';
        if (savedBotName && *savedBotName) {
            wcsncpy(dst.savedBotName, savedBotName, sizeof(dst.savedBotName) / sizeof(wchar_t) - 1);
        }
    }
}

static bool RemoveReservationByAccountLocked(
    int32_t accountId,
    int32_t* outClientNum,
    wchar_t* outDepartedPlayerName,
    size_t outDepartedPlayerCap,
    wchar_t* outSavedBotName,
    size_t outSavedBotCap,
    void** outParkedBotBrain
) noexcept {
    if (outDepartedPlayerName && outDepartedPlayerCap > 0) outDepartedPlayerName[0] = L'\0';
    if (outSavedBotName && outSavedBotCap > 0) outSavedBotName[0] = L'\0';
    if (outParkedBotBrain) *outParkedBotBrain = nullptr;
    bool removed = false;
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_linuxArcadeReservations[i].active && g_linuxArcadeReservations[i].accountId == accountId) {
            if (outClientNum) *outClientNum = g_linuxArcadeReservations[i].clientNum;
            if (outDepartedPlayerName && outDepartedPlayerCap > 1 && g_linuxArcadeReservations[i].playerName[0] != L'\0') {
                wcsncpy(outDepartedPlayerName, g_linuxArcadeReservations[i].playerName, outDepartedPlayerCap - 1);
                outDepartedPlayerName[outDepartedPlayerCap - 1] = L'\0';
            }
            if (outSavedBotName && outSavedBotCap > 1 && g_linuxArcadeReservations[i].savedBotName[0] != L'\0') {
                wcsncpy(outSavedBotName, g_linuxArcadeReservations[i].savedBotName, outSavedBotCap - 1);
                outSavedBotName[outSavedBotCap - 1] = L'\0';
            }
            if (outParkedBotBrain && g_linuxArcadeReservations[i].parkedBotBrain != nullptr) {
                *outParkedBotBrain = g_linuxArcadeReservations[i].parkedBotBrain;
            }
            g_linuxArcadeReservations[i] = {};
            removed = true;
        }
    }
    return removed;
}

static bool FlushParkedBotBrainOnPlayer(
    const LinuxQueueArcadeEngineContext* ctx,
    void* pPlayer,
    void* parkedBotBrain
) noexcept {
    // satisfies: OBL-QA-LINUX-10
    if (!ctx || !ctx->pfnRemoveBot || !pPlayer || !parkedBotBrain) {
        return false;
    }
    if (!IsValidReadableMemory(pPlayer, LinuxOffsets::kPlayerBotBrain + sizeof(void*))) {
        return false;
    }
    const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
    if (*reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain) == nullptr) {
        *reinterpret_cast<void**>(addr + LinuxOffsets::kPlayerBotBrain) = parkedBotBrain;
    }
    ctx->pfnRemoveBot(pPlayer);
    *reinterpret_cast<void**>(addr + LinuxOffsets::kPlayerBotBrain) = nullptr;
    uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags2);
    *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0048u);
    return true;
}

static bool FlushParkedBotBrainForClient(
    const LinuxQueueArcadeEngineContext* ctx,
    int32_t clientNum,
    void* parkedBotBrain
) noexcept {
    // satisfies: OBL-QA-LINUX-10
    if (!ctx || !ctx->ppGame || !ctx->pfnRemoveBot || clientNum < 0 || !parkedBotBrain) {
        return false;
    }
    void* pGameVoid = *ctx->ppGame;
    if (!pGameVoid) return false;

    bool flushed = false;
    TraverseClientsMap(pGameVoid, [&](int32_t key, void* pPlayer) -> bool {
        if (key == clientNum || *reinterpret_cast<const int32_t*>(reinterpret_cast<uintptr_t>(pPlayer) + LinuxOffsets::kPlayerClientNumber) == clientNum) {
            flushed = FlushParkedBotBrainOnPlayer(ctx, pPlayer, parkedBotBrain);
            return false;
        }
        return true;
    });
    return flushed;
}

static bool ReadLinuxPlayerSwapState(
    void* pPlayer,
    int32_t* outClientNum,
    int32_t* outTeam,
    int32_t* outAccountId,
    uint8_t* outIsBot,
    uint16_t* outFlags
) noexcept {
    if (outClientNum) *outClientNum = -1;
    if (outTeam) *outTeam = 0;
    if (outAccountId) *outAccountId = 0;
    if (outIsBot) *outIsBot = 0;
    if (outFlags) *outFlags = 0;
    if (!pPlayer) return false;

    bool ok = false;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        if (IsValidReadableMemory(pPlayer, LinuxOffsets::kPlayerBotBrain + sizeof(void*))) {
            const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
            if (outClientNum) *outClientNum = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerClientNumber);
            if (outTeam) *outTeam = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerTeam);
            if (outAccountId) *outAccountId = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerAccountID);
            if (outIsBot) *outIsBot = *reinterpret_cast<const uint8_t*>(addr + LinuxOffsets::kPlayerIsBot);
            if (outFlags) *outFlags = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerFlags);
            ok = true;
        }
    } LINUX_CALL_FAULT_GUARD_END({
        ok = false;
    });
    return ok;
}

static void ScrubLinuxBotDisconnectAndTerminateFlags(
    void* pPlayer,
    bool ensureConnectionlessBit,
    const LinuxQueueArcadeEngineContext* ctx,
    const void* originBotDef
) noexcept {
    if (!pPlayer) return;
    volatile bool needSelfHeal = false;
    uint32_t heroIdx = 0xFFFFFFFFu;
    int32_t clientNum = -1;

    LINUX_CALL_FAULT_GUARD_BEGIN() {
        if (IsValidReadableMemory(pPlayer, LinuxOffsets::kPlayerBotBrain + sizeof(void*))) {
            const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
            uint16_t* pFlags = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags);
            uint16_t nextFlags = static_cast<uint16_t>(*pFlags & ~(0x0101u | 0x0080u | 0x0040u | 0x8000u));
            if (ensureConnectionlessBit) {
                nextFlags = static_cast<uint16_t>(nextFlags | 0x0200u);
                *reinterpret_cast<uint8_t*>(addr + LinuxOffsets::kPlayerMatchmakingPostInit) = 1;
                *reinterpret_cast<uint8_t*>(addr + LinuxOffsets::kPlayerIsBot) = 1;
            }
            *pFlags = nextFlags;
            *reinterpret_cast<uint32_t*>(addr + LinuxOffsets::kPlayerDisconnectTime) = 0xFFFFFFFFu;
            *reinterpret_cast<uint32_t*>(addr + LinuxOffsets::kPlayerDiscTermTime) = 0xFFFFFFFFu;

            if (ensureConnectionlessBit &&
                GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots &&
                *reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain) == nullptr &&
                originBotDef != nullptr &&
                ctx != nullptr &&
                ctx->pfnAttachBot != nullptr) {
                heroIdx = *reinterpret_cast<const uint32_t*>(addr + LinuxOffsets::kPlayerHeroIndex);
                clientNum = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerClientNumber);
                needSelfHeal = true;
            }
        }
    } LINUX_CALL_FAULT_GUARD_END({
        needSelfHeal = false;
    });

    // Execute C++ engine AttachBot strictly OUTSIDE sigsetjmp fault guard
    if (needSelfHeal && ctx && ctx->pfnAttachBot) {
        void* volatile healedBrain = ctx->pfnAttachBot(pPlayer, originBotDef);
        LINUX_CALL_FAULT_GUARD_BEGIN() {
            const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
            if (healedBrain == nullptr) {
                healedBrain = *reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain);
            }
            if (healedBrain != nullptr &&
                IsValidReadableMemory(healedBrain, LinuxOffsets::kBotBrainHeroIndex + sizeof(uint32_t))) {
                *reinterpret_cast<void**>(addr + LinuxOffsets::kPlayerBotBrain) = healedBrain;
                *reinterpret_cast<uint32_t*>(
                    reinterpret_cast<uint8_t*>(healedBrain) + LinuxOffsets::kBotBrainHeroIndex) = heroIdx;
                uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags2);
                *pFlags2 = static_cast<uint16_t>(*pFlags2 | 0x0008u);
            }
        } LINUX_CALL_FAULT_GUARD_END({
            healedBrain = nullptr;
        });
        if (healedBrain != nullptr) {
            char healLog[256] = {};
            std::snprintf(healLog, sizeof(healLog),
                "[KONGOR QUEUE ARCADE] [ATTACH_BOT_SELF_HEAL] Re-attached CBotBrain=%p on active-phase Terminate guard (clientNum=%d, botDef=%p, heroIdx=%u)\n",
                healedBrain, clientNum, originBotDef, heroIdx);
            LogArcadeLine(healLog);
        }
    }
}

bool OnArcadePlayerDisconnected(
    void* pPlayer,
    uint32_t /*time*/,
    uint32_t /*maxDiscTime*/,
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer || !IsQueueArcadeModeActive()) {
        return false;
    }
    if (!IsQueueArcadeSwapHooksEnabled()) {
        LogArcadeLine("[KONGOR QUEUE ARCADE] [SWAP_KILLSWITCH] Bypassing Hooked_CPlayer_Disconnected (kongor_arcade_swap_hooks=0) -> stock K2 fallback.\n");
        return false;
    }

    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    const int32_t gamePhase = ReadCurrentLinuxGamePhase(ctx);
    if (gamePhase >= 7) {
        return false;
    }

    int32_t clientNum = -1;
    int32_t team = 0;
    int32_t accountId = 0;
    uint8_t isBot = 0;
    uint16_t flags = 0;
    if (!ReadLinuxPlayerSwapState(pPlayer, &clientNum, &team, &accountId, &isBot, &flags)) {
        return false;
    }

    // Once a human finishes loading (NETCMD_FINISHED_LOADING_WORLD), K2 sets 0x0200 (PLAYER_FLAG_LOADED) on +0x3CE,
    // so distinguish bots strictly via isBot (+0x653), accountId (+0x2D0), and kick re-entrancy guard.
    if (isBot == 1 || accountId <= 0 || IsArcadeKickReentrancyActive()) {
        ScrubLinuxBotDisconnectAndTerminateFlags(pPlayer, false, nullptr, nullptr);
        char logBuf[256] = {};
        std::snprintf(logBuf, sizeof(logBuf),
            "[KONGOR QUEUE ARCADE] Hooked_CPlayer_Disconnected suppressed on bot/connectionless slot (clientNum=%d, accId=%d, flags=0x%04X, kickGuard=%d)\n",
            clientNum, accountId, static_cast<unsigned>(flags), IsArcadeKickReentrancyActive() ? 1 : 0);
        LogArcadeLine(logBuf);
        return true;
    }

    char logBuf[256] = {};
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] Hooked_CPlayer_Disconnected releasing human slot (clientNum=%d, accId=%d, phase=%d) back to bot\n",
        clientNum, accountId, gamePhase);
    LogArcadeLine(logBuf);
    ArcadeReleaseSlot(accountId, ctx, false);
    ScrubLinuxBotDisconnectAndTerminateFlags(pPlayer, true, nullptr, nullptr);
    return true;
}

bool OnArcadePlayerTerminate(
    void* pPlayer,
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer || !IsQueueArcadeModeActive()) {
        return false;
    }
    if (!IsQueueArcadeSwapHooksEnabled()) {
        LogArcadeLine("[KONGOR QUEUE ARCADE] [SWAP_KILLSWITCH] Bypassing active-phase Hooked_CPlayer_Terminate guard (kongor_arcade_swap_hooks=0) -> stock K2 fallback.\n");
        return false;
    }

    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    const int32_t gamePhase = ReadCurrentLinuxGamePhase(ctx);
    if (gamePhase >= 7) {
        return false;
    }

    int32_t clientNum = -1;
    int32_t team = 0;
    int32_t accountId = 0;
    uint8_t isBot = 0;
    uint16_t flags = 0;
    if (!ReadLinuxPlayerSwapState(pPlayer, &clientNum, &team, &accountId, &isBot, &flags)) {
        return false;
    }
    if (team != 1 && team != 2) {
        return false;
    }

    if (accountId > 0 && !IsArcadeKickReentrancyActive()) {
        ArcadeReleaseSlot(accountId, ctx, false);
    }

    const void* originBotDef = nullptr;
    GetArcadeSlotOrigin(clientNum, &originBotDef, nullptr, 0);
    ScrubLinuxBotDisconnectAndTerminateFlags(pPlayer, true, ctx, originBotDef);

    char logBuf[256] = {};
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] [TERMINATE_GUARD] Suppressed CPlayer::Terminate during active phase (clientNum=%d, accId=%d, team=%d, phase=%d)\n",
        clientNum, accountId, team, gamePhase);
    LogArcadeLine(logBuf);
    return true;
}

static int Hooked_CHostServer_GenerateClientID(void* pHostServer, uint16_t* pConnId, int accountId) {
    // satisfies: OBL-QA-LINUX-03
    if (pHostServer != nullptr) {
        NotifyObservedArcadeHostServer(pHostServer);
    }
    if (IsQueueArcadeModeActive()) {
        const int32_t reservedClientNum = ResolveReservedArcadeClientId(pConnId, accountId);
        if (reservedClientNum >= 0) {
            if (pConnId && *pConnId == 0u) {
                uint16_t synthCookie = static_cast<uint16_t>(0x4B00u | (static_cast<uint16_t>(reservedClientNum) & 0x00FFu));
                if (synthCookie == 0xDCD8u) synthCookie = 0x4B01u;
                *pConnId = synthCookie;
            }
            char logBuf[256];
            std::snprintf(logBuf, sizeof(logBuf),
                "[KONGOR QUEUE ARCADE:Linux] GenerateClientID intercepted -> bound incoming client (accountId=%d, cookie=0x%04X) to pre-reserved CPlayer slot clientNum=%d\n",
                accountId, pConnId ? static_cast<unsigned>(*pConnId) : 0u, reservedClientNum);
            LogArcadeLine(logBuf);
            return reservedClientNum;
        }
    }
    if (g_origGenerateClientID) {
        return g_origGenerateClientID(pHostServer, pConnId, accountId);
    }
    return 0;
}

static void Hooked_CPlayer_SpawnHero(void* thisPtr) {
    // satisfies: OBL-QA-LINUX-09
    if (IsQueueArcadeModeActive()) {
        if (ShouldSuppressPreMatchDuplicateHeroSpawn(thisPtr)) {
            return;
        }
        if (GetQueueArcadeMode() == QueueArcadeServerMode::PartyDormantSlots) {
            ArcadeParkUnreservedBots(nullptr);
        }
    }
    if (g_origSpawnHero) {
        g_origSpawnHero(thisPtr);
    }
}

static void Hooked_CPlayer_Terminate(void* thisPtr) {
    if (OnArcadePlayerTerminate(thisPtr, nullptr)) {
        return;
    }
    // satisfies: OBL-QA-LINUX-10
    if (IsQueueArcadeModeActive()) {
        ArcadeReleaseParkedBotOnPlayerTerminate(thisPtr);
    }
    bool wasHuman = false;
    if (thisPtr != nullptr) {
        LINUX_CALL_FAULT_GUARD_BEGIN() {
            if (IsValidReadableMemory(thisPtr, LinuxOffsets::kPlayerIsBot + sizeof(uint8_t))) {
                const uintptr_t addr = reinterpret_cast<uintptr_t>(thisPtr);
                const uint8_t preIsBot = *reinterpret_cast<const uint8_t*>(addr + LinuxOffsets::kPlayerIsBot);
                const int32_t preAccId = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerAccountID);
                wasHuman = (preIsBot == 0 && preAccId > 0);
            }
        } LINUX_CALL_FAULT_GUARD_END({
            wasHuman = false;
        });
    }
    if (g_origTerminate) {
        g_origTerminate(thisPtr);
    }
    // satisfies: OBL-QA-LINUX-01
    if (wasHuman) {
        ReapConnectionlessBotsIfNoActiveHumans(nullptr);
    }
}

static void Hooked_CPlayer_Disconnected(void* thisPtr, uint32_t time, uint32_t maxDiscTime) {
    if (OnArcadePlayerDisconnected(thisPtr, time, maxDiscTime, nullptr)) {
        return;
    }
    if (g_origDisconnected) {
        g_origDisconnected(thisPtr, time, maxDiscTime);
    }
}

static void* Hooked_CPlayer_AttachBot(void* thisPtr, const void* pBotDef) {
    void* pBrain = g_origAttachBot ? g_origAttachBot(thisPtr, pBotDef) : nullptr;
    if (thisPtr != nullptr && pBotDef != nullptr) {
        OnArcadePlayerAttachBot(thisPtr, pBotDef, nullptr);
    }
    return pBrain;
}

bool EnsureQueueArcadeK2HooksInstalled() noexcept {
    // satisfies: OBL-QA-LINUX-03
    // satisfies: OBL-QA-LINUX-09
    // satisfies: OBL-QA-LINUX-10
    // satisfies: OBL-QA-LINUX-11
    if (!IsQueueArcadeModeActive()) {
        return false;
    }
    if (g_linuxArcadeHooksInstalled.load(std::memory_order_acquire)) {
        return true;
    }

    pthread_mutex_lock(&g_linuxArcadeHookInstallLock);
    if (g_linuxArcadeHooksInstalled.load(std::memory_order_acquire)) {
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return true;
    }

    uintptr_t baseK2 = FindModuleBaseByName("libk2-x86_64.so");
    if (baseK2 == 0) {
        baseK2 = FindModuleBaseByName("libk2-x86_64-server.so");
    }
    if (baseK2 == 0) {
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return false;
    }

    // 1. Hook CHostServer::GenerateClientID @ libk2+0x00CC8AA0 (14-byte prologue)
    // Installed eagerly as soon as libk2 is mapped (even before CreateGame dlopens libgame_shared).
    if (!g_linuxClientIdHookArmed.load(std::memory_order_acquire)) {
        static const uint8_t kPatGenerateClientId14[14] = {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x18
        };
        void* targetGenId = reinterpret_cast<void*>(baseK2 + LinuxOffsets::kRvaGenerateClientID);
        void* trampGenId = InstallLinuxInlineTrampoline64(
            targetGenId,
            reinterpret_cast<void*>(Hooked_CHostServer_GenerateClientID),
            kPatGenerateClientId14,
            sizeof(kPatGenerateClientId14));
        if (!trampGenId) {
            SetLinuxSafeMode(true, "CHostServer::GenerateClientID prologue mismatch or hook failure");
            pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
            return false;
        }
        g_origGenerateClientID = reinterpret_cast<PFN_CHostServer_GenerateClientID>(trampGenId);
        g_linuxClientIdHookArmed.store(true, std::memory_order_release);
        LogArcadeLine("[KONGOR QUEUE ARCADE:Linux] Installed W^X inline trampoline on CHostServer::GenerateClientID\n");
    }

    uintptr_t baseGS = FindModuleBaseByName("libgame_shared-x86_64-server.so");
    if (baseGS == 0) {
        // libgame_shared-x86_64-server.so is dlopened lazily on CreateGame; SpawnHero/Terminate/Disconnected/AttachBot arm on InitializeConnectionless or next CConsole::Execute.
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return false;
    }

    // 2. Hook CPlayer::SpawnHero @ libgame_shared+0x00C99C90 (17-byte prologue)
    static const uint8_t kPatSpawnHero17[17] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x81, 0xEC, 0x58, 0x02, 0x00, 0x00
    };
    void* targetSpawnHero = reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaSpawnHero);
    void* trampSpawnHero = InstallLinuxInlineTrampoline64(
        targetSpawnHero,
        reinterpret_cast<void*>(Hooked_CPlayer_SpawnHero),
        kPatSpawnHero17,
        sizeof(kPatSpawnHero17));
    if (!trampSpawnHero) {
        SetLinuxSafeMode(true, "CPlayer::SpawnHero prologue mismatch or hook failure");
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return false;
    }
    g_origSpawnHero = reinterpret_cast<PFN_CPlayer_SpawnHero>(trampSpawnHero);

    // 3. Hook CPlayer::Terminate @ libgame_shared+0x00C98E90 (17-byte prologue)
    static const uint8_t kPatTerminate17[17] = {
        0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x81, 0xEC, 0xB8, 0x00, 0x00, 0x00
    };
    void* targetTerminate = reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaTerminate);
    void* trampTerminate = InstallLinuxInlineTrampoline64(
        targetTerminate,
        reinterpret_cast<void*>(Hooked_CPlayer_Terminate),
        kPatTerminate17,
        sizeof(kPatTerminate17));
    if (!trampTerminate) {
        SetLinuxSafeMode(true, "CPlayer::Terminate prologue mismatch or hook failure");
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return false;
    }
    g_origTerminate = reinterpret_cast<PFN_CPlayer_Terminate>(trampTerminate);

    // 4. Hook CPlayer::Disconnected @ libgame_shared+0x00C98E00 (16-byte prologue)
    static const uint8_t kPatDisconnected16[16] = {
        0x55, 0x41, 0x56, 0x53, 0x48, 0x89, 0xFB, 0x66, 0x81, 0x8B, 0xCE, 0x03, 0x00, 0x00, 0x01, 0x01
    };
    void* targetDisconnected = reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaDisconnected);
    void* trampDisconnected = InstallLinuxInlineTrampoline64(
        targetDisconnected,
        reinterpret_cast<void*>(Hooked_CPlayer_Disconnected),
        kPatDisconnected16,
        sizeof(kPatDisconnected16));
    if (!trampDisconnected) {
        SetLinuxSafeMode(true, "CPlayer::Disconnected prologue mismatch or hook failure");
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return false;
    }
    g_origDisconnected = reinterpret_cast<PFN_CPlayer_Disconnected>(trampDisconnected);

    // 5. Hook CPlayer::AttachBot @ libgame_shared+0x00C952B0 (15-byte prologue)
    //   41 57 41 56 41 55 41 54   push r15..r12
    //   53                        push rbx
    //   49 89 F7                  mov r15, rsi
    //   49 89 FE                  mov r14, rdi
    static const uint8_t kPatAttachBot15[15] = {
        0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54,
        0x53, 0x49, 0x89, 0xF7, 0x49, 0x89, 0xFE
    };
    void* targetAttachBot = reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaAttachBot);
    void* trampAttachBot = InstallLinuxInlineTrampoline64(
        targetAttachBot,
        reinterpret_cast<void*>(Hooked_CPlayer_AttachBot),
        kPatAttachBot15,
        sizeof(kPatAttachBot15));
    if (!trampAttachBot) {
        SetLinuxSafeMode(true, "CPlayer::AttachBot prologue mismatch or hook failure");
        pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);
        return false;
    }
    g_origAttachBot = reinterpret_cast<PFN_CPlayer_AttachBot>(trampAttachBot);

    g_linuxArcadeHooksInstalled.store(true, std::memory_order_release);
    pthread_mutex_unlock(&g_linuxArcadeHookInstallLock);

    LogArcadeLine("[KONGOR QUEUE ARCADE:Linux] Successfully installed all 5 W^X inline trampolines (GenerateClientID, SpawnHero, Terminate, Disconnected, AttachBot)\n");
    return true;
}

// satisfies: OBL-QA-LINUX-01
void ReapConnectionlessBotsIfNoActiveHumans(const LinuxQueueArcadeEngineContext* overrideCtx) noexcept {
    if (IsQueueArcadeModeActive()) {
        // Queue Arcade servers intentionally keep pre-allocated connectionless slots alive during 0-human intervals.
        return;
    }

    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    if (!ctx || !ctx->ppGame) {
        return;
    }

    void* botPlayers[64] = {};
    uint32_t botCount = 0;
    uint32_t activeHumans = 0;

    LINUX_CALL_FAULT_GUARD_BEGIN() {
        void* pGameVoid = *ctx->ppGame;
        if (pGameVoid) {
            TraverseClientsMap(pGameVoid, [&](int32_t, void* pEntryPlayer) -> bool {
                const uintptr_t entryAddr = reinterpret_cast<uintptr_t>(pEntryPlayer);
                const uint16_t flags = *reinterpret_cast<const uint16_t*>(entryAddr + LinuxOffsets::kPlayerFlags);
                const uint8_t isBot = *reinterpret_cast<const uint8_t*>(entryAddr + LinuxOffsets::kPlayerIsBot);
                const int32_t accId = *reinterpret_cast<const int32_t*>(entryAddr + LinuxOffsets::kPlayerAccountID);
                if ((flags & 0x0080u) == 0) {
                    // Loaded humans also have 0x0200 (PLAYER_FLAG_LOADED) set after NETCMD_FINISHED_LOADING_WORLD;
                    // detect active humans strictly via isBot == 0 && accountId > 0.
                    if (isBot == 0 && accId > 0) {
                        ++activeHumans;
                    } else if (botCount < 64) {
                        botPlayers[botCount++] = pEntryPlayer;
                    }
                }
                return true;
            });

            if (activeHumans == 0 && botCount > 0) {
                for (uint32_t i = 0; i < botCount; ++i) {
                    uint16_t* pBotFlags = reinterpret_cast<uint16_t*>(
                        reinterpret_cast<uintptr_t>(botPlayers[i]) + LinuxOffsets::kPlayerFlags);
                    *pBotFlags = static_cast<uint16_t>(*pBotFlags | 0x0080u);
                }
            }
        }
    } LINUX_CALL_FAULT_GUARD_END({
        return;
    });

    if (activeHumans == 0 && botCount > 0) {
        char logBuf[256];
        std::snprintf(logBuf, sizeof(logBuf),
            "[KONGOR NATIVE GUARD:Linux] [BOT-ECONOMY] Last human terminated; marked %u bot(s) terminated (0x0080 at +0x3CE) for clean CGameServer::Frame shutdown.\n",
            botCount);
        LogArcadeLine(logBuf);
    }
}

// satisfies: OBL-QA-LINUX-02
// satisfies: OBL-QA-LINUX-06
// satisfies: OBL-QA-LINUX-07
// satisfies: OBL-QA-LINUX-08
LinuxQueueArcadeSlotReservationResult ArcadeReserveSlot(
    int32_t accountId,
    int32_t preferredTeam,
    const wchar_t* playerName,
    const LinuxQueueArcadeEngineContext* overrideCtx,
    const wchar_t* explicitClanTag
) noexcept {
    LinuxQueueArcadeSlotReservationResult res = {};
    res.clientNum = -1;
    res.accountId = accountId;

    if (g_linuxSafeModeActive.load(std::memory_order_acquire) ||
        (overrideCtx == nullptr && !IsQueueArcadeClientIdHookArmed())) {
        res.success = false;
        std::snprintf(res.reason, sizeof(res.reason), "arcade_hooks_disarmed");
        char warnBuf[512];
        std::snprintf(warnBuf, sizeof(warnBuf),
            "[KONGOR QUEUE ARCADE:Linux] ERROR: Refusing ArcadeReserveSlot(accountId=%d) because GenerateClientID hook is disarmed (SafeMode=%d, reason='%s')\n",
            accountId, g_linuxSafeModeActive.load() ? 1 : 0, g_linuxSafeModeReason);
        LogArcadeLine(warnBuf);
        return res;
    }

    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    if (!ctx || !ctx->ppGame) {
        std::snprintf(res.reason, sizeof(res.reason), "missing_engine_context");
        return res;
    }
    if (accountId <= 0) {
        std::snprintf(res.reason, sizeof(res.reason), "invalid_account_id");
        return res;
    }

    struct CandidateBotSlot {
        void*   pPlayer;
        int32_t clientNum;
        int32_t team;
    };
    CandidateBotSlot candidates[64] = {};
    size_t candidateCount = 0;
    void* existingSameAccount = nullptr;
    uint32_t activeHumansTeam1 = 0;
    uint32_t activeHumansTeam2 = 0;

    // Step A: Collect POD candidate snapshot inside sigsetjmp fault guard (zero engine C++ calls inside sigsetjmp)
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) {
            std::snprintf(res.reason, sizeof(res.reason), "null_igame_world");
        } else {
            TraverseClientsMap(pGameVoid, [&](int32_t, void* pPlayer) -> bool {
                uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                uint16_t flags = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerFlags);
                int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerAccountID);
                int32_t slotClientNum = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerClientNumber);
                int32_t slotTeam = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerTeam);
                uint8_t slotIsBot = *reinterpret_cast<const uint8_t*>(addr + LinuxOffsets::kPlayerIsBot);

                if ((flags & 0x0080u) == 0) { // Non-terminated
                    if (slotAccId == accountId) {
                        existingSameAccount = pPlayer;
                    } else if (slotIsBot == 0 && slotAccId > 0) {
                        if (slotTeam == 1) ++activeHumansTeam1;
                        else if (slotTeam == 2) ++activeHumansTeam2;
                    } else if ((flags & 0x0200u) != 0 && slotAccId <= 0 && (slotTeam == 1 || slotTeam == 2)) {
                        if (candidateCount < (sizeof(candidates) / sizeof(candidates[0]))) {
                            candidates[candidateCount].pPlayer = pPlayer;
                            candidates[candidateCount].clientNum = slotClientNum;
                            candidates[candidateCount].team = slotTeam;
                            ++candidateCount;
                        }
                    }
                }
                return true;
            });
        }
    } LINUX_CALL_FAULT_GUARD_END({
        res.success = false;
        std::snprintf(res.reason, sizeof(res.reason), "fault_guard_recovery");
        return res;
    });

    // Step B: Filter out candidate slots with lingering UDP CClientConnection strictly OUTSIDE sigsetjmp
    void* chosen = existingSameAccount;
    if (!chosen && candidateCount > 0) {
        void* const pHostServer = ResolveEffectiveLinuxHostServer(ctx);
        const int32_t effectiveTargetTeam = (preferredTeam == 1 || preferredTeam == 2)
            ? preferredTeam
            : ((activeHumansTeam1 <= activeHumansTeam2) ? 1 : 2);
        void* bestPreferredSlot = nullptr;
        void* bestFallbackSlot = nullptr;

        for (size_t i = 0; i < candidateCount; ++i) {
            const CandidateBotSlot& cand = candidates[i];
            const bool hasLingeringUdp = (pHostServer != nullptr && ctx->pfnGetClient != nullptr &&
                cand.clientNum >= 0 && ctx->pfnGetClient(pHostServer, cand.clientNum) != nullptr);
            if (hasLingeringUdp) {
                char skipLog[192] = {};
                std::snprintf(skipLog, sizeof(skipLog),
                    "[KONGOR QUEUE ARCADE] [SLOT_GUARD] Skipping slot with lingering UDP CClientConnection (clientNum=%d, team=%d)\n",
                    cand.clientNum, cand.team);
                LogArcadeLine(skipLog);
                continue;
            }
            if (cand.team == effectiveTargetTeam && !bestPreferredSlot) {
                bestPreferredSlot = cand.pPlayer;
            }
            if (!bestFallbackSlot) {
                bestFallbackSlot = cand.pPlayer;
            }
        }
        chosen = bestPreferredSlot ? bestPreferredSlot : bestFallbackSlot;
    }

    if (!chosen) {
        if (res.reason[0] == '\0') {
            std::snprintf(res.reason, sizeof(res.reason), "no_available_connectionless_slot");
        }
        return res;
    }

    uintptr_t chosenAddr = reinterpret_cast<uintptr_t>(chosen);
    uint16_t priorFlags = *reinterpret_cast<const uint16_t*>(chosenAddr + LinuxOffsets::kPlayerFlags);
    uint8_t priorIsBot = *reinterpret_cast<const uint8_t*>(chosenAddr + LinuxOffsets::kPlayerIsBot);
    int32_t priorAccId = *reinterpret_cast<const int32_t*>(chosenAddr + LinuxOffsets::kPlayerAccountID);
    res.priorFlags3CE = priorFlags;
    res.priorPostInit650 = *reinterpret_cast<const uint8_t*>(chosenAddr + LinuxOffsets::kPlayerMatchmakingPostInit);
    res.priorDiscTerm428 = *reinterpret_cast<const uint32_t*>(chosenAddr + LinuxOffsets::kPlayerDiscTermTime);

    void* pBotBrain = *reinterpret_cast<void* const*>(chosenAddr + LinuxOffsets::kPlayerBotBrain);
    if (pBotBrain != nullptr) {
        const bool parkForReuse = (GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots);
        if (parkForReuse) {
            // Mode 1: park bot brain
            // satisfies: OBL-QA-LINUX-10
            res.detachedBotBrain = pBotBrain;
        } else {
            if (ctx->pfnRemoveBot != nullptr) {
                ctx->pfnRemoveBot(chosen);
            }
            res.detachedBotBrain = nullptr;
        }
        // satisfies: OBL-QA-LINUX-06
        // Unconditionally zero +0x730 and clear 0x0048 on +0x3D0
        *reinterpret_cast<void**>(chosenAddr + LinuxOffsets::kPlayerBotBrain) = nullptr;
        uint16_t* pBotFlags = reinterpret_cast<uint16_t*>(chosenAddr + LinuxOffsets::kPlayerFlags2);
        *pBotFlags = static_cast<uint16_t>(*pBotFlags & ~0x0048u);
        res.removedBotBrain = true;
    }

    const uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(chosenAddr + LinuxOffsets::kPlayerNameIndex);
    const uint16_t clanIdx = *reinterpret_cast<const uint16_t*>(chosenAddr + LinuxOffsets::kPlayerClanTagIndex);
    const uint16_t rankIdx = *reinterpret_cast<const uint16_t*>(chosenAddr + LinuxOffsets::kPlayerClanNameIndex);
    if (((priorFlags & 0x0200u) != 0 || priorIsBot != 0) && priorAccId <= 0 &&
        ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
        ctx->pfnGetNetString && nameIdx != 0xFFFFu) {
        const void* pExistingName = ctx->pfnGetNetString(*ctx->ppNetResourceManager, nameIdx);
        ReadLibcxxWString(pExistingName, res.previousBotName, sizeof(res.previousBotName) / sizeof(wchar_t));
    }

    // satisfies: OBL-QA-LINUX-08
    uint16_t* pFlags = reinterpret_cast<uint16_t*>(chosenAddr + LinuxOffsets::kPlayerFlags);
    *pFlags = static_cast<uint16_t>(*pFlags & ~(0x0200u | 0x0101u | 0x0080u | 0x0040u | 0x8000u | 0x3014u));
    uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(chosenAddr + LinuxOffsets::kPlayerFlags2);
    *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0020u);

    // satisfies: OBL-QA-LINUX-08
    *reinterpret_cast<uint16_t*>(chosenAddr + LinuxOffsets::kPlayerPing) = 0u;
    *reinterpret_cast<uint32_t*>(chosenAddr + LinuxOffsets::kPlayerDisconnectTime) = 0xFFFFFFFFu;
    *reinterpret_cast<uint32_t*>(chosenAddr + LinuxOffsets::kPlayerTotalDiscTime) = 0u;
    *reinterpret_cast<uint32_t*>(chosenAddr + LinuxOffsets::kPlayerDiscTermTime) = 0xFFFFFFFFu;

    // satisfies: OBL-QA-LINUX-07
    // satisfies: OBL-QA-LINUX-08
    *reinterpret_cast<uint8_t*>(chosenAddr + LinuxOffsets::kPlayerIsBot) = 0;
    *reinterpret_cast<uint8_t*>(chosenAddr + LinuxOffsets::kPlayerOverrideByte) = 0;
    *reinterpret_cast<uint32_t*>(chosenAddr + LinuxOffsets::kPlayerClientFlags) = 0;
    *reinterpret_cast<uint8_t*>(chosenAddr + LinuxOffsets::kPlayerMatchmakingPostInit) = 1;
    *reinterpret_cast<int32_t*>(chosenAddr + LinuxOffsets::kPlayerAccountID) = accountId;

    res.postFlags3CE = *pFlags;
    res.postInit650 = *reinterpret_cast<const uint8_t*>(chosenAddr + LinuxOffsets::kPlayerMatchmakingPostInit);
    res.postDiscTerm428 = *reinterpret_cast<const uint32_t*>(chosenAddr + LinuxOffsets::kPlayerDiscTermTime);

    wchar_t barePlayerName[64] = {};
    wchar_t bareClanTag[32] = {};
    wchar_t fullDisplayName[96] = {};
    SplitDisplayNameAndClanTagW(playerName, explicitClanTag,
        barePlayerName, sizeof(barePlayerName) / sizeof(wchar_t),
        bareClanTag, sizeof(bareClanTag) / sizeof(wchar_t));
    if (barePlayerName[0] != L'\0') {
        if (bareClanTag[0] != L'\0') {
            swprintf(fullDisplayName, sizeof(fullDisplayName) / sizeof(wchar_t), L"[%ls]%ls", bareClanTag, barePlayerName);
        } else {
            wcsncpy(fullDisplayName, barePlayerName, sizeof(fullDisplayName) / sizeof(wchar_t) - 1);
            fullDisplayName[sizeof(fullDisplayName) / sizeof(wchar_t) - 1] = L'\0';
        }
        bool setPrimary = SetPlayerNetStringSlot(ctx, nameIdx, fullDisplayName);
        SetPlayerNetStringSlot(ctx, clanIdx, L"");
        SetPlayerNetStringSlot(ctx, rankIdx, L"");
        res.updatedNetStrings = setPrimary;
    }

    res.clientNum = *reinterpret_cast<const int32_t*>(chosenAddr + LinuxOffsets::kPlayerClientNumber);
    res.teamId = *reinterpret_cast<const int32_t*>(chosenAddr + LinuxOffsets::kPlayerTeam);
    res.success = true;
    std::snprintf(res.reason, sizeof(res.reason), existingSameAccount ? "reused_existing_slot" : "reserved_bot_slot");

    int32_t displacedClientNum = -1;
    void* displacedBrain = nullptr;
    if (res.success && res.clientNum >= 0) {
        pthread_rwlock_wrlock(&g_linuxArcadeReservationLock);
        if (static_cast<size_t>(res.clientNum) < kMaxArcadeReservations) {
            LinuxArcadeSlotOriginEntry& origin = g_linuxArcadeSlotOrigins[res.clientNum];
            if (origin.originalBotName[0] == L'\0' && res.previousBotName[0] != L'\0') {
                wcsncpy(origin.originalBotName, res.previousBotName, sizeof(origin.originalBotName) / sizeof(wchar_t) - 1);
                origin.originalBotName[sizeof(origin.originalBotName) / sizeof(wchar_t) - 1] = L'\0';
                origin.populated = true;
            } else if (origin.originalBotName[0] != L'\0') {
                wcsncpy(res.previousBotName, origin.originalBotName, sizeof(res.previousBotName) / sizeof(wchar_t) - 1);
                res.previousBotName[sizeof(res.previousBotName) / sizeof(wchar_t) - 1] = L'\0';
            }
        }
        UpsertReservationLocked(
            accountId, res.clientNum, res.teamId, playerName, res.previousBotName,
            res.detachedBotBrain, &displacedClientNum, &displacedBrain);
        pthread_rwlock_unlock(&g_linuxArcadeReservationLock);
    } else if (res.detachedBotBrain != nullptr) {
        displacedClientNum = res.clientNum;
        displacedBrain = res.detachedBotBrain;
    }

    if (displacedBrain != nullptr && displacedClientNum >= 0) {
        FlushParkedBotBrainForClient(ctx, displacedClientNum, displacedBrain);
    }

    if (res.success && std::strcmp(res.reason, "reserved_bot_slot") == 0) {
        const wchar_t* humanNoticeName = (fullDisplayName[0] != L'\0') ? fullDisplayName : playerName;
        const wchar_t* botNoticeName = (res.previousBotName[0] != L'\0') ? res.previousBotName : L"ArcadeBot";
        QueueLinuxArcadeSwapChatNotice(ctx, humanNoticeName, botNoticeName, true);
    }

    char prevBotA[64] = {};
    WcharToUtf8Buf(res.previousBotName, prevBotA, sizeof(prevBotA));

    char logBuf[512];
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE:Linux] ArcadeReserveSlot(accountId=%d, preferredTeam=%d) -> success=%d clientNum=%d team=%d removedBot=%d parkedBrain=%d netNameUpdated=%d prevBot='%s' flags3CE=0x%04X->0x%04X postInit650=%u->%u discTerm428=0x%08X->0x%08X reason='%s'\n",
        accountId, preferredTeam, res.success ? 1 : 0, res.clientNum, res.teamId,
        res.removedBotBrain ? 1 : 0, res.detachedBotBrain ? 1 : 0,
        res.updatedNetStrings ? 1 : 0, prevBotA,
        static_cast<unsigned>(res.priorFlags3CE), static_cast<unsigned>(res.postFlags3CE),
        static_cast<unsigned>(res.priorPostInit650), static_cast<unsigned>(res.postInit650),
        res.priorDiscTerm428, res.postDiscTerm428, res.reason);
    LogArcadeLine(logBuf);
    return res;
}

static bool KickLinuxReleasedClientIfConnected(
    const LinuxQueueArcadeEngineContext* ctx,
    int32_t accountId,
    int32_t releasedClientNum
) noexcept {
    if (!ctx || releasedClientNum < 0 || !ctx->pfnGetClient || !ctx->pfnKickClient) {
        return false;
    }
    void* pHostServer = ResolveEffectiveLinuxHostServer(ctx);
    if (!pHostServer) {
        return false;
    }

    void* pLiveConn = ctx->pfnGetClient(pHostServer, releasedClientNum);
    if (pLiveConn == nullptr) {
        return false;
    }

    ++t_linuxArcadeKickReentrancyDepth;
    g_linuxArcadeKickInProgressCount.fetch_add(1, std::memory_order_acq_rel);
    ctx->pfnKickClient(pHostServer, releasedClientNum, 0);
    g_linuxArcadeKickInProgressCount.fetch_sub(1, std::memory_order_acq_rel);
    --t_linuxArcadeKickReentrancyDepth;

    char kickLog[256] = {};
    std::snprintf(kickLog, sizeof(kickLog),
        "[KONGOR QUEUE ARCADE] Kicked lingering UDP CClientConnection on ArcadeReleaseSlot(accountId=%d, clientNum=%d, conn=%p)\n",
        accountId, releasedClientNum, pLiveConn);
    LogArcadeLine(kickLog);
    return true;
}

// satisfies: OBL-QA-LINUX-04
// satisfies: OBL-QA-LINUX-07
// satisfies: OBL-QA-LINUX-08
// satisfies: OBL-QA-LINUX-10
bool ArcadeReleaseSlot(
    int32_t accountId,
    const LinuxQueueArcadeEngineContext* overrideCtx,
    bool kickClient
) noexcept {
    int32_t reservedClientNum = -1;
    wchar_t departedHumanName[96] = {};
    wchar_t savedBotName[64] = {};
    void* parkedBotBrain = nullptr;
    LinuxArcadeSlotOriginEntry originsSnapshot[kMaxArcadeReservations] = {};

    pthread_rwlock_wrlock(&g_linuxArcadeReservationLock);
    bool hadReservation = RemoveReservationByAccountLocked(
        accountId, &reservedClientNum,
        departedHumanName, sizeof(departedHumanName) / sizeof(wchar_t),
        savedBotName, sizeof(savedBotName) / sizeof(wchar_t),
        &parkedBotBrain);
    std::memcpy(originsSnapshot, g_linuxArcadeSlotOrigins, sizeof(originsSnapshot));
    pthread_rwlock_unlock(&g_linuxArcadeReservationLock);

    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    if (!ctx || !ctx->ppGame) {
        return false;
    }

    bool updatedPlayer = false;
    uint16_t priorFlags3CE = 0;
    uint16_t postFlags3CE = 0;
    bool restoredBotBrain = false;
    bool needAttachBotSelfHeal = false;
    void* matchedPlayer = nullptr;
    void* brainToFlushOutsideGuard = nullptr;
    int32_t matchedClientNum = -1;
    uint32_t matchedHeroIdx = 0xFFFFFFFFu;
    uint16_t matchedNameIdx = 0xFFFFu;
    uint16_t matchedClanIdx = 0xFFFFu;
    uint16_t matchedRankIdx = 0xFFFFu;

    // Pure POD field reads/writes inside sigsetjmp fault guard; engine C++ calls
    // (pfnRemoveBot / pfnAttachBot / pfnGetNetString / pfnSetNetString / pfnKickClient) execute strictly OUTSIDE sigsetjmp below.
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        void* pGameVoid = *ctx->ppGame;
        if (pGameVoid) {
            TraverseClientsMap(pGameVoid, [&](int32_t, void* pPlayer) -> bool {
                uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerAccountID);
                int32_t slotClientNum = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerClientNumber);

                if ((accountId > 0 && slotAccId == accountId) ||
                    (reservedClientNum >= 0 && slotClientNum == reservedClientNum)) {
                    // satisfies: OBL-QA-LINUX-08
                    *reinterpret_cast<int32_t*>(addr + LinuxOffsets::kPlayerAccountID) = 0;
                    *reinterpret_cast<uint8_t*>(addr + LinuxOffsets::kPlayerOverrideByte) = 0;
                    *reinterpret_cast<uint32_t*>(addr + LinuxOffsets::kPlayerClientFlags) = 0;
                    *reinterpret_cast<uint8_t*>(addr + LinuxOffsets::kPlayerMatchmakingPostInit) = 1;
                    *reinterpret_cast<uint8_t*>(addr + LinuxOffsets::kPlayerIsBot) = 1;
                    *reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerPing) = 0u;
                    *reinterpret_cast<uint32_t*>(addr + LinuxOffsets::kPlayerDisconnectTime) = 0xFFFFFFFFu;
                    *reinterpret_cast<uint32_t*>(addr + LinuxOffsets::kPlayerTotalDiscTime) = 0u;
                    *reinterpret_cast<uint32_t*>(addr + LinuxOffsets::kPlayerDiscTermTime) = 0xFFFFFFFFu;

                    uint16_t* pFlags = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags);
                    priorFlags3CE = *pFlags;
                    *pFlags = static_cast<uint16_t>((priorFlags3CE | 0x0200u) & ~(0x0101u | 0x0080u | 0x0040u | 0x8000u | 0x3014u));
                    postFlags3CE = *pFlags;

                    uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags2);
                    *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0020u);

                    matchedHeroIdx = *reinterpret_cast<const uint32_t*>(addr + LinuxOffsets::kPlayerHeroIndex);
                    matchedClientNum = slotClientNum;

                    if (parkedBotBrain != nullptr) {
                        if (GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots &&
                            *reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain) == nullptr) {
                            *reinterpret_cast<void**>(addr + LinuxOffsets::kPlayerBotBrain) = parkedBotBrain;
                            *reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(parkedBotBrain) + LinuxOffsets::kBotBrainHeroIndex) =
                                matchedHeroIdx;
                            *pFlags2 = static_cast<uint16_t>(*pFlags2 | 0x0008u);
                            restoredBotBrain = true;
                        } else {
                            brainToFlushOutsideGuard = parkedBotBrain;
                        }
                        parkedBotBrain = nullptr;
                    } else if (GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots &&
                               *reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain) == nullptr) {
                        needAttachBotSelfHeal = true;
                    }

                    matchedNameIdx = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerNameIndex);
                    matchedClanIdx = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerClanTagIndex);
                    matchedRankIdx = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerClanNameIndex);
                    matchedPlayer = pPlayer;
                    updatedPlayer = true;
                    return false;
                }
                return true;
            });
        }
    } LINUX_CALL_FAULT_GUARD_END({
        return false;
    });

    const bool validOriginIdx = (matchedClientNum >= 0 && static_cast<size_t>(matchedClientNum) < kMaxArcadeReservations);
    const wchar_t* originBotLabel = (validOriginIdx && originsSnapshot[matchedClientNum].originalBotName[0] != L'\0')
        ? originsSnapshot[matchedClientNum].originalBotName
        : nullptr;
    const void* originBotDef = validOriginIdx ? originsSnapshot[matchedClientNum].botDef : nullptr;
    const wchar_t* botLabel = originBotLabel ? originBotLabel
        : ((savedBotName[0] != L'\0') ? savedBotName : L"ArcadeBot");

    if (updatedPlayer && matchedPlayer != nullptr) {
        if (departedHumanName[0] == L'\0' && ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
            ctx->pfnGetNetString && matchedNameIdx != 0xFFFFu) {
            const void* pExistingHuman = ctx->pfnGetNetString(*ctx->ppNetResourceManager, matchedNameIdx);
            ReadLibcxxWString(pExistingHuman, departedHumanName, sizeof(departedHumanName) / sizeof(wchar_t));
        }

        if (brainToFlushOutsideGuard != nullptr) {
            FlushParkedBotBrainOnPlayer(ctx, matchedPlayer, brainToFlushOutsideGuard);
        } else if (needAttachBotSelfHeal && originBotDef != nullptr && ctx->pfnAttachBot != nullptr) {
            void* volatile healedBrain = ctx->pfnAttachBot(matchedPlayer, originBotDef);
            LINUX_CALL_FAULT_GUARD_BEGIN() {
                const uintptr_t addr = reinterpret_cast<uintptr_t>(matchedPlayer);
                if (healedBrain == nullptr) {
                    healedBrain = *reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain);
                }
                if (healedBrain != nullptr &&
                    IsValidReadableMemory(healedBrain, LinuxOffsets::kBotBrainHeroIndex + sizeof(uint32_t))) {
                    *reinterpret_cast<void**>(addr + LinuxOffsets::kPlayerBotBrain) = healedBrain;
                    *reinterpret_cast<uint32_t*>(
                        reinterpret_cast<uint8_t*>(healedBrain) + LinuxOffsets::kBotBrainHeroIndex) = matchedHeroIdx;
                    uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags2);
                    *pFlags2 = static_cast<uint16_t>(*pFlags2 | 0x0008u);
                    restoredBotBrain = true;
                }
            } LINUX_CALL_FAULT_GUARD_END({
                healedBrain = nullptr;
            });
            if (healedBrain != nullptr) {
                char healLog[256] = {};
                std::snprintf(healLog, sizeof(healLog),
                    "[KONGOR QUEUE ARCADE] [ATTACH_BOT_SELF_HEAL] Re-attached CBotBrain=%p via CPlayer::AttachBot(clientNum=%d, botDef=%p, heroIdx=%u)\n",
                    healedBrain, matchedClientNum, originBotDef, matchedHeroIdx);
                LogArcadeLine(healLog);
            }
        }

        SetPlayerNetStringSlot(ctx, matchedNameIdx, botLabel);
        SetPlayerNetStringSlot(ctx, matchedClanIdx, L"");
        SetPlayerNetStringSlot(ctx, matchedRankIdx, L"");
        QueueLinuxArcadeSwapChatNotice(ctx, departedHumanName, botLabel, false);
    } else if (!updatedPlayer && parkedBotBrain != nullptr && reservedClientNum >= 0) {
        FlushParkedBotBrainForClient(ctx, reservedClientNum, parkedBotBrain);
    }

    bool kickedLingering = false;
    const int32_t targetKickClientNum = (matchedClientNum >= 0) ? matchedClientNum : reservedClientNum;
    if (kickClient && targetKickClientNum >= 0) {
        kickedLingering = KickLinuxReleasedClientIfConnected(ctx, accountId, targetKickClientNum);
    }

    char restoredBotA[64] = {};
    WcharToUtf8Buf(botLabel, restoredBotA, sizeof(restoredBotA));

    char logBuf[448];
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE:Linux] ArcadeReleaseSlot(accountId=%d, kick=%d) -> hadReservation=%d updatedPlayer=%d restoredBrain=%d kicked=%d clientNum=%d restoredBot='%s' flags3CE=0x%04X->0x%04X\n",
        accountId, kickClient ? 1 : 0, hadReservation ? 1 : 0, updatedPlayer ? 1 : 0,
        restoredBotBrain ? 1 : 0, kickedLingering ? 1 : 0, targetKickClientNum,
        restoredBotA[0] ? restoredBotA : "ArcadeBot",
        static_cast<unsigned>(priorFlags3CE), static_cast<unsigned>(postFlags3CE));
    LogArcadeLine(logBuf);
    return hadReservation || updatedPlayer;
}

// satisfies: OBL-QA-LINUX-04
// satisfies: OBL-QA-LINUX-06
uint32_t ArcadeParkUnreservedBots(
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    if (!ctx || !ctx->ppGame) return 0;

    void* playersToPark[64] = {};
    uint32_t candidateCount = 0;

    // Traverse std::__tree inside fault guard; invoke pfnRemoveBot strictly outside sigsetjmp.
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        void* pGameVoid = *ctx->ppGame;
        if (pGameVoid) {
            TraverseClientsMap(pGameVoid, [&](int32_t, void* pPlayer) -> bool {
                uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                uint16_t flags = *reinterpret_cast<const uint16_t*>(addr + LinuxOffsets::kPlayerFlags);
                int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + LinuxOffsets::kPlayerAccountID);
                void* pBotBrain = *reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain);

                if ((flags & 0x0200u) != 0 && slotAccId <= 0 && pBotBrain != nullptr && candidateCount < 64) {
                    playersToPark[candidateCount++] = pPlayer;
                }
                return true;
            });
        }
    } LINUX_CALL_FAULT_GUARD_END({
        return 0;
    });

    uint32_t parkedCount = 0;
    for (uint32_t i = 0; i < candidateCount; ++i) {
        void* pPlayer = playersToPark[i];
        if (!IsValidReadableMemory(pPlayer, LinuxOffsets::kPlayerBotBrain + sizeof(void*))) {
            continue;
        }
        uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
        if (*reinterpret_cast<void* const*>(addr + LinuxOffsets::kPlayerBotBrain) == nullptr) {
            continue;
        }
        if (ctx->pfnRemoveBot) {
            ctx->pfnRemoveBot(pPlayer);
        }
        // satisfies: OBL-QA-LINUX-06
        *reinterpret_cast<void**>(addr + LinuxOffsets::kPlayerBotBrain) = nullptr;
        uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + LinuxOffsets::kPlayerFlags2);
        *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0048u);
        ++parkedCount;
    }

    if (parkedCount > 0) {
        char logBuf[192];
        std::snprintf(logBuf, sizeof(logBuf),
            "[KONGOR QUEUE ARCADE:Linux] ArcadeParkUnreservedBots -> parked %u unreserved connectionless slot(s) at fountain\n",
            parkedCount);
        LogArcadeLine(logBuf);
    }
    return parkedCount;
}

// satisfies: OBL-QA-LINUX-03
int32_t ResolveReservedArcadeClientId(
    const uint16_t* pConnId,
    int32_t accountId
) noexcept {
    wchar_t connName[64] = {};
    const bool hasConnName = (accountId <= 0) && ReadLibcxxWStringAtConnName(pConnId, connName, sizeof(connName) / sizeof(wchar_t));

    int32_t matchedClientNum = -1;
    pthread_rwlock_rdlock(&g_linuxArcadeReservationLock);
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        const ArcadeReservationEntry& entry = g_linuxArcadeReservations[i];
        if (!entry.active) continue;
        if (accountId > 0) {
            if (entry.accountId == accountId) {
                matchedClientNum = entry.clientNum;
                break;
            }
        } else if (hasConnName && entry.playerName[0] != L'\0' && NamesMatchPlayerW(entry.playerName, connName)) {
            matchedClientNum = entry.clientNum;
            break;
        }
    }
    pthread_rwlock_unlock(&g_linuxArcadeReservationLock);
    return matchedClientNum;
}

// satisfies: OBL-QA-LINUX-10
void ClearAllArcadeReservations(
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    struct PendingParkedDrain {
        int32_t clientNum;
        void*   parkedBotBrain;
    } pending[kMaxArcadeReservations] = {};
    size_t pendingCount = 0;

    pthread_rwlock_wrlock(&g_linuxArcadeReservationLock);
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_linuxArcadeReservations[i].active &&
            g_linuxArcadeReservations[i].parkedBotBrain != nullptr &&
            g_linuxArcadeReservations[i].clientNum >= 0) {
            pending[pendingCount].clientNum = g_linuxArcadeReservations[i].clientNum;
            pending[pendingCount].parkedBotBrain = g_linuxArcadeReservations[i].parkedBotBrain;
            ++pendingCount;
        }
        g_linuxArcadeReservations[i] = {};
    }
    ResetLinuxArcadeSlotOriginsLocked();
    pthread_rwlock_unlock(&g_linuxArcadeReservationLock);

    if (pendingCount > 0) {
        const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
        for (size_t i = 0; i < pendingCount; ++i) {
            FlushParkedBotBrainForClient(ctx, pending[i].clientNum, pending[i].parkedBotBrain);
        }
    }
}

// satisfies: OBL-QA-LINUX-09
bool ShouldSuppressPreMatchDuplicateHeroSpawn(
    void* pPlayer,
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer) return false;
    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    if (!ctx || !ctx->ppGame) return false;

    int32_t clientNum = -1;
    uint32_t heroIdx = 0xFFFFFFFFu;
    int32_t gamePhase = -1;
    bool suppress = false;

    LINUX_CALL_FAULT_GUARD_BEGIN() {
        void* pGameVoid = *ctx->ppGame;
        if (pGameVoid) {
            uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
            void* pGameInfo = *reinterpret_cast<void* const*>(pGame + LinuxOffsets::kGameInfo);
            if (pGameInfo && IsValidReadableMemory(pGameInfo, LinuxOffsets::kGameInfoPhase + sizeof(int32_t))) {
                gamePhase = *reinterpret_cast<const int32_t*>(reinterpret_cast<uintptr_t>(pGameInfo) + LinuxOffsets::kGameInfoPhase);
                if (gamePhase < 5) {
                    uintptr_t playerAddr = reinterpret_cast<uintptr_t>(pPlayer);
                    heroIdx = *reinterpret_cast<const uint32_t*>(playerAddr + LinuxOffsets::kPlayerHeroIndex);
                    if (heroIdx != 0xFFFFFFFFu) {
                        void* pEntityDir = *reinterpret_cast<void* const*>(pGame + LinuxOffsets::kGameEntityDirectory);
                        if (pEntityDir && IsValidReadableMemory(pEntityDir, sizeof(void*))) {
                            using PFN_GetEntity = void* (*)(void*, uint32_t);
                            using PFN_GetAsUnit = void* (*)(void*);

                            uintptr_t dirVtbl = *reinterpret_cast<const uintptr_t*>(pEntityDir);
                            if (dirVtbl && IsValidReadableMemory(reinterpret_cast<void*>(dirVtbl + LinuxOffsets::kVtableEntityDirGetEntity), sizeof(void*))) {
                                auto pfnGetEnt = *reinterpret_cast<PFN_GetEntity const*>(dirVtbl + LinuxOffsets::kVtableEntityDirGetEntity);
                                if (pfnGetEnt) {
                                    void* pGameEntity = pfnGetEnt(pEntityDir, heroIdx);
                                    if (pGameEntity && IsValidReadableMemory(pGameEntity, sizeof(void*))) {
                                        uintptr_t entVtbl = *reinterpret_cast<const uintptr_t*>(pGameEntity);
                                        if (entVtbl && IsValidReadableMemory(reinterpret_cast<void*>(entVtbl + LinuxOffsets::kVtableUnitGetAsUnit), sizeof(void*))) {
                                            auto pfnGetAsUnit = *reinterpret_cast<PFN_GetAsUnit const*>(entVtbl + LinuxOffsets::kVtableUnitGetAsUnit);
                                            if (pfnGetAsUnit && pfnGetAsUnit(pGameEntity) != nullptr) {
                                                clientNum = *reinterpret_cast<const int32_t*>(playerAddr + LinuxOffsets::kPlayerClientNumber);
                                                suppress = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    } LINUX_CALL_FAULT_GUARD_END({
        return false;
    });

    if (suppress) {
        char logBuf[256];
        std::snprintf(logBuf, sizeof(logBuf),
            "[KONGOR NATIVE GUARD:Linux] Suppressed duplicate pre-Phase-5 CPlayer::SpawnHero(clientNum=%d, existingHeroIdx=%u, gamePhase=%d)\n",
            clientNum, heroIdx, gamePhase);
        LogArcadeLine(logBuf);
    }
    return suppress;
}

// satisfies: OBL-QA-LINUX-10
bool ArcadeReleaseParkedBotOnPlayerTerminate(
    void* pPlayer,
    const LinuxQueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer) return false;
    int32_t clientNum = -1;
    LINUX_CALL_FAULT_GUARD_BEGIN() {
        clientNum = *reinterpret_cast<const int32_t*>(reinterpret_cast<uintptr_t>(pPlayer) + LinuxOffsets::kPlayerClientNumber);
    } LINUX_CALL_FAULT_GUARD_END({
        return false;
    });
    if (clientNum < 0) return false;

    void* parkedBrain = nullptr;
    pthread_rwlock_wrlock(&g_linuxArcadeReservationLock);
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_linuxArcadeReservations[i].active && g_linuxArcadeReservations[i].clientNum == clientNum) {
            parkedBrain = g_linuxArcadeReservations[i].parkedBotBrain;
            g_linuxArcadeReservations[i] = {};
            break;
        }
    }
    pthread_rwlock_unlock(&g_linuxArcadeReservationLock);

    if (!parkedBrain) return false;

    const LinuxQueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveLinuxArcadeEngineContext();
    bool freed = FlushParkedBotBrainOnPlayer(ctx, pPlayer, parkedBrain);
    char logBuf[256];
    std::snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE:Linux] ArcadeReleaseParkedBotOnPlayerTerminate(clientNum=%d) -> freed=%d while CLuaScriptManager is live\n",
        clientNum, freed ? 1 : 0);
    LogArcadeLine(logBuf);
    return freed;
}

// satisfies: OBL-QA-LINUX-05
void ExecuteConsoleArcadeModeA(const char* args) noexcept {
    if (!args) return;
    while (*args == ' ' || *args == '\t') ++args;
    int32_t modeVal = std::atoi(args);
    ClearAllArcadeReservations(nullptr);
    if (modeVal >= 2) {
        SetQueueArcadeMode(QueueArcadeServerMode::PartyDormantSlots);
    } else if (modeVal == 1) {
        SetQueueArcadeMode(QueueArcadeServerMode::ActiveBots);
    } else {
        SetQueueArcadeMode(QueueArcadeServerMode::Disabled);
        return;
    }
    if (g_linuxSafeModeActive.load(std::memory_order_acquire) || !IsQueueArcadeClientIdHookArmed()) {
        char errBuf[512];
        std::snprintf(errBuf, sizeof(errBuf),
            "[KONGOR QUEUE ARCADE:Linux] ERROR: GenerateClientID hook disarmed on kongor_arcade_mode=%d (SafeMode=%d, reason='%s') - retiring unusable Queue Arcade server cleanly\n",
            modeVal, g_linuxSafeModeActive.load() ? 1 : 0, g_linuxSafeModeReason);
        LogArcadeLine(errBuf);
        g_linuxArcadeMode.store(0, std::memory_order_release);
        ClearAllArcadeReservations();
        g_linuxPendingSafeModeRetirement.store(true, std::memory_order_release);
    }
}

// satisfies: OBL-QA-LINUX-05
void ExecuteConsoleArcadeReserveA(const char* args) noexcept {
    if (!args || !*args) return;
    if (!IsQueueArcadeModeActive()) {
        SetQueueArcadeMode(QueueArcadeServerMode::ActiveBots);
    }
    const char* p = args;
    while (*p == ' ' || *p == '\t') ++p;
    char* endPtr = nullptr;
    int32_t accountId = static_cast<int32_t>(std::strtol(p, &endPtr, 10));
    if (endPtr == p || accountId <= 0) return;
    p = endPtr;
    while (*p == ' ' || *p == '\t') ++p;
    int32_t preferredTeam = 0;
    if (*p >= '0' && *p <= '9') {
        preferredTeam = static_cast<int32_t>(std::strtol(p, &endPtr, 10));
        p = endPtr;
    }
    auto parseToken = [](const char*& cursor, char* outBuf, size_t outCap) noexcept {
        if (!outBuf || outCap == 0) return;
        outBuf[0] = '\0';
        while (*cursor == ' ' || *cursor == '\t') ++cursor;
        if (*cursor == '\0' || *cursor == ';' || *cursor == '\r' || *cursor == '\n') return;
        size_t idx = 0;
        const bool quoted = (*cursor == '\"');
        if (quoted) ++cursor;
        while (*cursor != '\0' && *cursor != '\r' && *cursor != '\n' && idx + 1 < outCap) {
            if (quoted && *cursor == '\"') { ++cursor; break; }
            if (!quoted && (*cursor == ';' || *cursor == ' ' || *cursor == '\t')) break;
            outBuf[idx++] = *cursor++;
        }
        outBuf[idx] = '\0';
    };
    char nameA[64] = {};
    char clanA[32] = {};
    parseToken(p, nameA, sizeof(nameA));
    parseToken(p, clanA, sizeof(clanA));

    wchar_t nameW[64] = {};
    wchar_t clanW[32] = {};
    Utf8ToWcharBuf(nameA, nameW, sizeof(nameW) / sizeof(wchar_t));
    Utf8ToWcharBuf(clanA, clanW, sizeof(clanW) / sizeof(wchar_t));

    ArcadeReserveSlot(accountId, preferredTeam, nameW, nullptr, clanW[0] != L'\0' ? clanW : nullptr);
}

// satisfies: OBL-QA-LINUX-05
void ExecuteConsoleArcadeReleaseA(const char* args) noexcept {
    if (!args || !*args) return;
    const char* p = args;
    while (*p == ' ' || *p == '\t') ++p;
    char* endPtr = nullptr;
    const int32_t accountId = static_cast<int32_t>(std::strtol(p, &endPtr, 10));
    if (endPtr == p || accountId <= 0) return;

    p = endPtr;
    while (*p == ' ' || *p == '\t' || *p == '\"') ++p;
    bool kickClient = false;
    if (strncasecmp(p, "kick", 4) == 0 &&
        (p[4] == '\0' || p[4] == ' ' || p[4] == '\t' || p[4] == ';' || p[4] == '\"' || p[4] == '\r' || p[4] == '\n')) {
        kickClient = true;
    } else if (*p == '1' &&
               (p[1] == '\0' || p[1] == ' ' || p[1] == '\t' || p[1] == ';' || p[1] == '\"' || p[1] == '\r' || p[1] == '\n')) {
        kickClient = true;
    }
    ArcadeReleaseSlot(accountId, nullptr, kickClient);
}

void ExecuteConsoleArcadeSwapHooksA(const char* args) noexcept {
    if (!args) return;
    while (*args == ' ' || *args == '\t' || *args == '\"') ++args;
    bool enable = true;
    if (*args == '0' ||
        strncasecmp(args, "false", 5) == 0 ||
        strncasecmp(args, "off", 3) == 0 ||
        strncasecmp(args, "no", 2) == 0) {
        enable = false;
    }
    SetQueueArcadeSwapHooksEnabled(enable);
}

// satisfies: OBL-QA-LINUX-05
void ExecuteConsoleArcadeModeW(const wchar_t* args) noexcept {
    if (!args) return;
    while (*args == L' ' || *args == L'\t') ++args;
    int32_t modeVal = static_cast<int32_t>(wcstol(args, nullptr, 10));
    ClearAllArcadeReservations(nullptr);
    if (modeVal >= 2) {
        SetQueueArcadeMode(QueueArcadeServerMode::PartyDormantSlots);
    } else if (modeVal == 1) {
        SetQueueArcadeMode(QueueArcadeServerMode::ActiveBots);
    } else {
        SetQueueArcadeMode(QueueArcadeServerMode::Disabled);
        return;
    }
    if (g_linuxSafeModeActive.load(std::memory_order_acquire) || !IsQueueArcadeClientIdHookArmed()) {
        char errBuf[512];
        std::snprintf(errBuf, sizeof(errBuf),
            "[KONGOR QUEUE ARCADE:Linux] ERROR: GenerateClientID hook disarmed on kongor_arcade_mode=%d (SafeMode=%d, reason='%s') - retiring unusable Queue Arcade server cleanly\n",
            modeVal, g_linuxSafeModeActive.load() ? 1 : 0, g_linuxSafeModeReason);
        LogArcadeLine(errBuf);
        g_linuxArcadeMode.store(0, std::memory_order_release);
        ClearAllArcadeReservations();
        g_linuxPendingSafeModeRetirement.store(true, std::memory_order_release);
    }
}

// satisfies: OBL-QA-LINUX-05
void ExecuteConsoleArcadeReserveW(const wchar_t* args) noexcept {
    if (!args || !*args) return;
    if (!IsQueueArcadeModeActive()) {
        SetQueueArcadeMode(QueueArcadeServerMode::ActiveBots);
    }
    const wchar_t* p = args;
    while (*p == L' ' || *p == L'\t') ++p;
    wchar_t* endPtr = nullptr;
    int32_t accountId = static_cast<int32_t>(wcstol(p, &endPtr, 10));
    if (endPtr == p || accountId <= 0) return;
    p = endPtr;
    while (*p == L' ' || *p == L'\t') ++p;
    int32_t preferredTeam = 0;
    if (*p >= L'0' && *p <= L'9') {
        preferredTeam = static_cast<int32_t>(wcstol(p, &endPtr, 10));
        p = endPtr;
    }
    auto parseToken = [](const wchar_t*& cursor, wchar_t* outBuf, size_t outCap) noexcept {
        if (!outBuf || outCap == 0) return;
        outBuf[0] = L'\0';
        while (*cursor == L' ' || *cursor == L'\t') ++cursor;
        if (*cursor == L'\0' || *cursor == L';' || *cursor == L'\r' || *cursor == L'\n') return;
        size_t idx = 0;
        const bool quoted = (*cursor == L'\"');
        if (quoted) ++cursor;
        while (*cursor != L'\0' && *cursor != L'\r' && *cursor != L'\n' && idx + 1 < outCap) {
            if (quoted && *cursor == L'\"') { ++cursor; break; }
            if (!quoted && (*cursor == L';' || *cursor == L' ' || *cursor == L'\t')) break;
            outBuf[idx++] = *cursor++;
        }
        outBuf[idx] = L'\0';
    };
    wchar_t nameW[64] = {};
    wchar_t clanW[32] = {};
    parseToken(p, nameW, sizeof(nameW) / sizeof(wchar_t));
    parseToken(p, clanW, sizeof(clanW) / sizeof(wchar_t));

    ArcadeReserveSlot(accountId, preferredTeam, nameW, nullptr, clanW[0] != L'\0' ? clanW : nullptr);
}

// satisfies: OBL-QA-LINUX-05
void ExecuteConsoleArcadeReleaseW(const wchar_t* args) noexcept {
    if (!args || !*args) return;
    const wchar_t* p = args;
    while (*p == L' ' || *p == L'\t') ++p;
    wchar_t* endPtr = nullptr;
    const int32_t accountId = static_cast<int32_t>(wcstol(p, &endPtr, 10));
    if (endPtr == p || accountId <= 0) return;

    p = endPtr;
    while (*p == L' ' || *p == L'\t' || *p == L'\"') ++p;
    bool kickClient = false;
    if (wcsncasecmp(p, L"kick", 4) == 0 &&
        (p[4] == L'\0' || p[4] == L' ' || p[4] == L'\t' || p[4] == L';' || p[4] == L'\"' || p[4] == L'\r' || p[4] == L'\n')) {
        kickClient = true;
    } else if (*p == L'1' &&
               (p[1] == L'\0' || p[1] == L' ' || p[1] == L'\t' || p[1] == L';' || p[1] == L'\"' || p[1] == L'\r' || p[1] == L'\n')) {
        kickClient = true;
    }
    ArcadeReleaseSlot(accountId, nullptr, kickClient);
}

void ExecuteConsoleArcadeSwapHooksW(const wchar_t* args) noexcept {
    if (!args) return;
    while (*args == L' ' || *args == L'\t' || *args == L'\"') ++args;
    bool enable = true;
    if (*args == L'0' ||
        wcsncasecmp(args, L"false", 5) == 0 ||
        wcsncasecmp(args, L"off", 3) == 0 ||
        wcsncasecmp(args, L"no", 2) == 0) {
        enable = false;
    }
    SetQueueArcadeSwapHooksEnabled(enable);
}

// Quote-balanced IPC command sanitizer blocking unprivileged kongor_ / __kongor_ / quit / crash / @
// commands over Unix IPC opcode 0x0002 / 0x0003 while keeping zero libstdc++ dependency.
// satisfies: OBL-QA-LINUX-05
bool ShouldAllowUnprivilegedIpcCommandBufferA(const char* buffer, size_t len) noexcept {
    if (!buffer || len == 0) return false;
    bool inQuotes = false;
    size_t segStart = 0;
    bool sawAnySegment = false;

    for (size_t i = 0; i <= len; ++i) {
        char ch = (i < len) ? buffer[i] : '\0';
        if (ch == '\"') {
            inQuotes = !inQuotes;
        }
        if (ch == '\0' || (!inQuotes && (ch == ';' || ch == '\r' || ch == '\n'))) {
            size_t s = segStart;
            while (s < i && (buffer[s] == ' ' || buffer[s] == '\t')) ++s;
            if (s < i) {
                sawAnySegment = true;
                size_t tokEnd = s;
                while (tokEnd < i && buffer[tokEnd] != ' ' && buffer[tokEnd] != '\t') ++tokEnd;
                size_t tokLen = tokEnd - s;
                const char* tok = buffer + s;
                if (tok[0] == '@' ||
                    (tokLen >= 9 && strncasecmp(tok, "__kongor_", 9) == 0) ||
                    (tokLen >= 7 && strncasecmp(tok, "kongor_", 7) == 0) ||
                    (tokLen == 4 && strncasecmp(tok, "quit", 4) == 0) ||
                    (tokLen == 5 && strncasecmp(tok, "crash", 5) == 0)) {
                    return false;
                }
            }
            segStart = i + 1;
        }
    }
    return sawAnySegment;
}

// Dispatches kongor_arcade_mode / kongor_arcade_reserve / kongor_arcade_release / kongor_arcade_swap_hooks from CConsole::Execute.
// satisfies: OBL-QA-LINUX-05
bool TryDispatchLinuxArcadeConsoleCmdW(const wchar_t* cmdLine) noexcept {
    if (!cmdLine) return false;
    const wchar_t* p = cmdLine;
    while (*p == L' ' || *p == L'\t') ++p;
    if (*p == L'\0') return false;

    const wchar_t* tokStart = p;
    while (*p != L'\0' && *p != L' ' && *p != L'\t' && *p != L'\r' && *p != L'\n') ++p;
    size_t tokLen = static_cast<size_t>(p - tokStart);
    while (*p == L' ' || *p == L'\t') ++p;
    if (*p == L'\0') return false; // All Queue Arcade commands require arguments

    if (tokLen == 18 && wcsncasecmp(tokStart, L"kongor_arcade_mode", 18) == 0) {
        ExecuteConsoleArcadeModeW(p);
        return true;
    }
    if (tokLen == 21 && wcsncasecmp(tokStart, L"kongor_arcade_reserve", 21) == 0) {
        ExecuteConsoleArcadeReserveW(p);
        return true;
    }
    if (tokLen == 21 && wcsncasecmp(tokStart, L"kongor_arcade_release", 21) == 0) {
        ExecuteConsoleArcadeReleaseW(p);
        return true;
    }
    if (tokLen == 24 && wcsncasecmp(tokStart, L"kongor_arcade_swap_hooks", 24) == 0) {
        ExecuteConsoleArcadeSwapHooksW(p);
        return true;
    }
    return false;
}

// ============================================================================
// 10v10 Mid Wars Kill Packet (0x60) Framing Guard & 128B Expansion (Linux ELF)
// Dossier: artifacts/evidence/midwars_10v10_kill_packet_truncation.json
// ============================================================================

bool ClampKillMessageBufferInPlace(
    uint8_t* buffer,
    size_t bufferLen,
    uint8_t* outOldAssists,
    uint8_t* outNewAssists
) noexcept {
    // satisfies: OBL-MIDWARS-01
    // satisfies: OBL-MIDWARS-02
    if (!buffer || bufferLen < kKillMessageHeaderBytes) {
        return false;
    }
    if (buffer[0] != kGameCmdKillMessage) {
        return false;
    }
    const uint8_t declaredAssists = buffer[11];
    const size_t requiredLen = kKillMessageHeaderBytes + (4u * static_cast<size_t>(declaredAssists));
    if (bufferLen >= requiredLen) {
        return false;
    }
    const uint8_t maxSafeAssists = static_cast<uint8_t>((bufferLen - kKillMessageHeaderBytes) / 4u);
    if (outOldAssists) *outOldAssists = declaredAssists;
    if (outNewAssists) *outNewAssists = maxSafeAssists;
    buffer[11] = maxSafeAssists;
    return true;
}

bool CollectUniqueSortedAssistClientNums(
    const uintptr_t* unitPtrBegin,
    const uintptr_t* unitPtrEnd,
    uintptr_t attackerUnit,
    ptrdiff_t ownerClientOffset,
    int32_t* outAssists,
    size_t maxAssists,
    size_t* outCount
) noexcept {
    // satisfies: OBL-MIDWARS-04
    if (outCount) *outCount = 0;
    if (!unitPtrBegin || !unitPtrEnd || unitPtrEnd < unitPtrBegin || !outAssists || maxAssists == 0 || !outCount) {
        return false;
    }
    const size_t ptrCount = static_cast<size_t>(unitPtrEnd - unitPtrBegin);
    if (ptrCount > 256) {
        return false;
    }
    size_t count = 0;
    for (size_t i = 0; i < ptrCount; ++i) {
        const uintptr_t unitAddr = unitPtrBegin[i];
        if (unitAddr == 0 || unitAddr == attackerUnit) {
            continue;
        }
        const int32_t clientNum = *reinterpret_cast<const int32_t*>(unitAddr + ownerClientOffset);
        if (clientNum < 0) {
            continue;
        }
        bool duplicate = false;
        for (size_t j = 0; j < count; ++j) {
            if (outAssists[j] == clientNum) {
                duplicate = true;
                break;
            }
        }
        if (duplicate) {
            continue;
        }
        if (count >= maxAssists) {
            break;
        }
        size_t pos = count;
        while (pos > 0 && outAssists[pos - 1] > clientNum) {
            outAssists[pos] = outAssists[pos - 1];
            --pos;
        }
        outAssists[pos] = clientNum;
        ++count;
    }
    *outCount = count;
    return true;
}

bool BuildExpandedKillMessagePayload(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* sortedAssistClientNums,
    size_t assistCount,
    uint8_t* outBuf,
    size_t outCap,
    size_t* outLen
) noexcept {
    // satisfies: OBL-MIDWARS-04
    if (!origBufHeader || origLen < kKillMessageHeaderBytes || origBufHeader[0] != kGameCmdKillMessage) {
        return false;
    }
    if (!sortedAssistClientNums || !outBuf || outCap < kKillMessageHeaderBytes || !outLen) {
        return false;
    }
    const size_t maxByCap = (outCap - kKillMessageHeaderBytes) / 4u;
    const size_t effectiveAssists = (assistCount < maxByCap) ? assistCount : maxByCap;
    if (effectiveAssists > 255u) {
        return false;
    }

    std::memcpy(outBuf, origBufHeader, kKillMessageHeaderBytes);
    outBuf[11] = static_cast<uint8_t>(effectiveAssists);
    for (size_t i = 0; i < effectiveAssists; ++i) {
        std::memcpy(outBuf + kKillMessageHeaderBytes + (i * 4u), &sortedAssistClientNums[i], sizeof(int32_t));
    }
    *outLen = kKillMessageHeaderBytes + (effectiveAssists * 4u);
    return true;
}

bool FilterAndValidateExpandedKillAssists(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* candidateAssists,
    size_t candidateCount,
    int32_t* outFilteredAssists,
    size_t outCap,
    size_t* outFilteredCount
) noexcept {
    // satisfies: OBL-MIDWARS-04
    if (outFilteredCount) *outFilteredCount = 0;
    if (!origBufHeader || origLen < kKillMessageHeaderBytes || origBufHeader[0] != kGameCmdKillMessage) {
        return false;
    }
    if (!candidateAssists || !outFilteredAssists || !outFilteredCount) {
        return false;
    }
    const uint8_t declaredAssists = origBufHeader[11];
    if (declaredAssists < 5u || declaredAssists > 10u) {
        return false;
    }
    int32_t headerKiller = -1;
    int32_t headerVictim = -1;
    std::memcpy(&headerKiller, origBufHeader + 1, sizeof(int32_t));
    std::memcpy(&headerVictim, origBufHeader + 5, sizeof(int32_t));

    size_t filteredCount = 0;
    for (size_t i = 0; i < candidateCount; ++i) {
        const int32_t clientNum = candidateAssists[i];
        if (clientNum == headerKiller || clientNum == headerVictim) {
            continue;
        }
        if (filteredCount >= outCap) {
            return false;
        }
        outFilteredAssists[filteredCount++] = clientNum;
    }
    if (filteredCount != static_cast<size_t>(declaredAssists)) {
        return false;
    }
    *outFilteredCount = filteredCount;
    return true;
}

namespace {

struct LinuxThreadLocalExpandedAssists {
    size_t  count;
    int32_t clientNums[kKillMessageMaxAssists128B];
};

static __thread LinuxThreadLocalExpandedAssists t_linuxExpandedAssists = { 0, {} };

typedef void (*PFN_Linux_CGameServer_BroadcastGameData)(
    void* pGameServer, void* pBuffer, bool bReliable, int32_t iExcludeClient, uint32_t uiDelay);
typedef void (*PFN_Linux_CGameServer_SendGameData)(
    void* pGameServer, int32_t iClientNum, void* pBuffer, bool bReliable, uint32_t uiDelay, uint32_t uiUnused);
typedef void (*PFN_Linux_CHostServer_BroadcastGameData)(
    void* pHostServer, void* pBuffer, bool bReliable, int32_t iExcludeClient);
typedef void (*PFN_Linux_CHostServer_SendGameData)(
    void* pHostServer, int32_t iClientNum, void* pBuffer, bool bReliable);
typedef void (*PFN_Linux_KillNotifyHelper)(
    void* pVictimUnit, void* pAttackerUnit, void* pKillerPlayer, void* pDamagerVector, uint16_t unGold);

static PFN_Linux_CGameServer_BroadcastGameData g_origLinuxGameServerBroadcastGameData = nullptr;
static PFN_Linux_CGameServer_SendGameData      g_origLinuxGameServerSendGameData = nullptr;
static PFN_Linux_CHostServer_BroadcastGameData g_origLinuxHostServerBroadcastGameData = nullptr;
static PFN_Linux_CHostServer_SendGameData      g_origLinuxHostServerSendGameData = nullptr;
static PFN_Linux_KillNotifyHelper              g_origLinuxKillNotifyHelper = nullptr;

static pthread_mutex_t g_linuxKillGuardLock = PTHREAD_MUTEX_INITIALIZER;
static std::atomic<uintptr_t> g_hookedLinuxK2Base{ 0 };
static std::atomic<uintptr_t> g_hookedLinuxGameBase{ 0 };
static std::atomic<uintptr_t> g_hookedLinuxGameSharedBase{ 0 };
static std::atomic<uintptr_t> g_failedLinuxK2Base{ 0 };
static std::atomic<uintptr_t> g_failedLinuxGameBase{ 0 };
static std::atomic<uintptr_t> g_failedLinuxGameSharedBase{ 0 };
static std::atomic<bool>      g_loggedNonServerKillGuardSkip{ false };

static bool NativeGuard_IsLinuxDedicatedServerProcess() noexcept {
    char exeBuf[4096];
    ssize_t exeLen = readlink("/proc/self/exe", exeBuf, sizeof(exeBuf) - 1);
    if (exeLen > 0) {
        exeBuf[exeLen] = '\0';
        const char* base = std::strrchr(exeBuf, '/');
        base = base ? (base + 1) : exeBuf;
        if (strcasestr(base, "server") != nullptr) {
            return true;
        }
        return false;
    }

    char cmd[4096];
    ReadLinuxProcessCommandLine(cmd, sizeof(cmd));
    return strcasestr(cmd, "hon-x86_64-server") != nullptr;
}

static void ClampLinuxIBufferKillPacket(void* pIBuffer, const char* pathTag) noexcept {
    if (!pIBuffer || !IsValidReadableMemory(pIBuffer, sizeof(K2IBufferLayout))) return;
    auto* buf = reinterpret_cast<K2IBufferLayout*>(pIBuffer);
    if (!buf->m_pBuffer || buf->m_uiEnd < kKillMessageHeaderBytes || buf->m_uiEnd > buf->m_uiSize) {
        return;
    }
    if (!IsValidReadableMemory(buf->m_pBuffer, buf->m_uiEnd)) {
        return;
    }
    uint8_t oldAssists = 0;
    uint8_t newAssists = 0;
    if (ClampKillMessageBufferInPlace(buf->m_pBuffer, buf->m_uiEnd, &oldAssists, &newAssists)) {
        g_linuxKillPacketClampCount.fetch_add(1, std::memory_order_relaxed);
        char logBuf[192];
        std::snprintf(logBuf, sizeof(logBuf),
            "[NATIVE-GUARD][KILL-PACKET-CLAMP] Clamped 0x60 numAssists %u -> %u on %s (len=%u)\n",
            static_cast<unsigned>(oldAssists), static_cast<unsigned>(newAssists),
            pathTag ? pathTag : "LinuxBroadcastGameData", static_cast<unsigned>(buf->m_uiEnd));
        LogArcadeLine(logBuf);
    }
}

static void Hooked_Linux_CGameServer_BroadcastGameData(
    void* pGameServer, void* pBuffer, bool bReliable, int32_t iExcludeClient, uint32_t uiDelay
) {
    K2IBufferLayout expandedIBuf{};
    uint8_t expandedStorage[kKillMessageExpandedCapacity] = {};
    void* effectiveBuffer = pBuffer;

    if (!g_killSwitchActive.load(std::memory_order_relaxed) &&
        t_linuxExpandedAssists.count >= 5 &&
        pBuffer != nullptr &&
        IsValidReadableMemory(pBuffer, sizeof(K2IBufferLayout))) {
        const auto* orig = reinterpret_cast<const K2IBufferLayout*>(pBuffer);
        if (orig->m_pBuffer && orig->m_uiEnd >= kKillMessageHeaderBytes &&
            IsValidReadableMemory(orig->m_pBuffer, kKillMessageHeaderBytes) &&
            orig->m_pBuffer[0] == kGameCmdKillMessage) {
            int32_t filteredAssists[kKillMessageMaxAssists128B] = {};
            size_t filteredCount = 0;
            size_t expandedLen = 0;
            if (FilterAndValidateExpandedKillAssists(
                    orig->m_pBuffer, orig->m_uiEnd,
                    t_linuxExpandedAssists.clientNums, t_linuxExpandedAssists.count,
                    filteredAssists, kKillMessageMaxAssists128B, &filteredCount) &&
                BuildExpandedKillMessagePayload(
                    orig->m_pBuffer, orig->m_uiEnd,
                    filteredAssists, filteredCount,
                    expandedStorage, sizeof(expandedStorage), &expandedLen)) {
                expandedIBuf.vptr = orig->vptr;
                expandedIBuf.m_pBuffer = expandedStorage;
                expandedIBuf.m_uiSize = static_cast<uint32_t>(sizeof(expandedStorage));
                expandedIBuf.m_uiEnd = static_cast<uint32_t>(expandedLen);
                expandedIBuf.m_uiRead = 0;
                expandedIBuf.m_iFaults = 0;
                effectiveBuffer = &expandedIBuf;
                t_linuxExpandedAssists.count = 0;
                g_linuxKillPacketExpandCount.fetch_add(1, std::memory_order_relaxed);
                char logBuf[192];
                std::snprintf(logBuf, sizeof(logBuf),
                    "[NATIVE-GUARD][KILL-PACKET-EXPAND] Expanded Linux 0x60 kill packet to %u bytes (%zu assists).\n",
                    static_cast<unsigned>(expandedIBuf.m_uiEnd), filteredCount);
                LogArcadeLine(logBuf);
            }
        }
    }

    if (!g_killSwitchActive.load(std::memory_order_relaxed)) {
        ClampLinuxIBufferKillPacket(effectiveBuffer, "CGameServer::BroadcastGameData");
    }
    if (g_origLinuxGameServerBroadcastGameData) {
        g_origLinuxGameServerBroadcastGameData(pGameServer, effectiveBuffer, bReliable, iExcludeClient, uiDelay);
    }
}

static void Hooked_Linux_CGameServer_SendGameData(
    void* pGameServer, int32_t iClientNum, void* pBuffer, bool bReliable, uint32_t uiDelay, uint32_t uiUnused
) {
    if (!g_killSwitchActive.load(std::memory_order_relaxed)) {
        ClampLinuxIBufferKillPacket(pBuffer, "CGameServer::SendGameData");
    }
    if (g_origLinuxGameServerSendGameData) {
        g_origLinuxGameServerSendGameData(pGameServer, iClientNum, pBuffer, bReliable, uiDelay, uiUnused);
    }
}

static void Hooked_Linux_CHostServer_BroadcastGameData(
    void* pHostServer, void* pBuffer, bool bReliable, int32_t iExcludeClient
) {
    if (!g_killSwitchActive.load(std::memory_order_relaxed)) {
        ClampLinuxIBufferKillPacket(pBuffer, "CHostServer::BroadcastGameData");
    }
    if (g_origLinuxHostServerBroadcastGameData) {
        g_origLinuxHostServerBroadcastGameData(pHostServer, pBuffer, bReliable, iExcludeClient);
    }
}

static void Hooked_Linux_CHostServer_SendGameData(
    void* pHostServer, int32_t iClientNum, void* pBuffer, bool bReliable
) {
    if (!g_killSwitchActive.load(std::memory_order_relaxed)) {
        ClampLinuxIBufferKillPacket(pBuffer, "CHostServer::SendGameData");
    }
    if (g_origLinuxHostServerSendGameData) {
        g_origLinuxHostServerSendGameData(pHostServer, iClientNum, pBuffer, bReliable);
    }
}

static void Hooked_Linux_KillNotifyHelper(
    void* pVictimUnit, void* pAttackerUnit, void* pKillerPlayer, void* pDamagerVector, uint16_t unGold
) {
    t_linuxExpandedAssists.count = 0;
    if (!g_killSwitchActive.load(std::memory_order_relaxed) &&
        pDamagerVector != nullptr &&
        IsValidReadableMemory(pDamagerVector, 16)) {
        const uintptr_t beginPtr = *reinterpret_cast<const uintptr_t*>(pDamagerVector);
        const uintptr_t endPtr   = *reinterpret_cast<const uintptr_t*>(reinterpret_cast<const uint8_t*>(pDamagerVector) + 8);
        if (beginPtr != 0 && endPtr >= beginPtr &&
            ((beginPtr | endPtr) & (sizeof(uintptr_t) - 1)) == 0) {
            const size_t byteSpan = static_cast<size_t>(endPtr - beginPtr);
            if (byteSpan > 0 && byteSpan <= (256u * sizeof(uintptr_t)) &&
                IsValidReadableMemory(reinterpret_cast<const void*>(beginPtr), byteSpan)) {
                bool ptrsReadable = true;
                const uintptr_t* ptrs = reinterpret_cast<const uintptr_t*>(beginPtr);
                const size_t ptrCount = byteSpan / sizeof(uintptr_t);
                for (size_t i = 0; i < ptrCount; ++i) {
                    if (ptrs[i] != 0 && ptrs[i] != reinterpret_cast<uintptr_t>(pAttackerUnit)) {
                        if (!IsValidReadableMemory(
                                reinterpret_cast<const void*>(ptrs[i] + LinuxOffsets::kUnitOwnerClientNumber),
                                sizeof(int32_t))) {
                            ptrsReadable = false;
                            break;
                        }
                    }
                }
                if (ptrsReadable) {
                    size_t assistCount = 0;
                    if (CollectUniqueSortedAssistClientNums(
                            ptrs, ptrs + ptrCount,
                            reinterpret_cast<uintptr_t>(pAttackerUnit),
                            LinuxOffsets::kUnitOwnerClientNumber,
                            t_linuxExpandedAssists.clientNums,
                            kKillMessageMaxAssists128B,
                            &assistCount)) {
                        t_linuxExpandedAssists.count = (assistCount >= 5) ? assistCount : 0;
                    }
                }
            }
        }
    }
    if (g_origLinuxKillNotifyHelper) {
        g_origLinuxKillNotifyHelper(pVictimUnit, pAttackerUnit, pKillerPlayer, pDamagerVector, unGold);
    }
    t_linuxExpandedAssists.count = 0;
}

} // anonymous namespace

bool EnsureKillPacketGuardsApplied() noexcept {
    // satisfies: OBL-MIDWARS-01
    // satisfies: OBL-MIDWARS-02
    // satisfies: OBL-MIDWARS-04
    if (g_killSwitchActive.load(std::memory_order_relaxed) || NativeGuard_IsDisabledByEnv()) {
        return false;
    }

    // Server-only CHostServer/CGameServer/KillNotify guards must NEVER run against the Linux
    // client's libk2-x86_64.so (which shares the .so filename with hon-x86_64-server but has
    // client-only prologues). Gate strictly on dedicated-server process or server modules.
    const bool isServerProc = NativeGuard_IsLinuxDedicatedServerProcess()
        || FindModuleBaseByName("libk2-x86_64-server.so") != 0
        || FindModuleBaseByName("game-x86_64-server.so") != 0;
    if (!isServerProc) {
        if (!g_loggedNonServerKillGuardSkip.exchange(true, std::memory_order_acq_rel)) {
            LogArcadeLine("[NATIVE-GUARD][KILL-PACKET] Skipping server-only CHostServer/KillNotify hooks in non-server Linux process.\n");
        }
        return false;
    }

    pthread_mutex_lock(&g_linuxKillGuardLock);

    uintptr_t baseK2 = FindModuleBaseByName("libk2-x86_64-server.so");
    if (baseK2 == 0) {
        baseK2 = FindModuleBaseByName("libk2-x86_64.so");
    }
    if (baseK2 != 0
        && baseK2 != g_hookedLinuxK2Base.load(std::memory_order_acquire)
        && baseK2 != g_failedLinuxK2Base.load(std::memory_order_acquire)) {
        static const uint8_t kPatHostBcast14[14] = {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x54, 0x53, 0x41, 0x89, 0xCC, 0x49, 0x89, 0xF6
        };
        static const uint8_t kPatHostSend17[17] = {
            0x55, 0x41, 0x56, 0x53, 0x41, 0x89, 0xCE, 0x48, 0x89, 0xD3, 0x48, 0x8B, 0x8F, 0x90, 0x02, 0x00, 0x00
        };
        void* pHostBcast = reinterpret_cast<void*>(baseK2 + LinuxOffsets::kRvaHostServerBroadcastGameData);
        void* pHostSend  = reinterpret_cast<void*>(baseK2 + LinuxOffsets::kRvaHostServerSendGameData);
        void* trampBcast = InstallLinuxInlineTrampoline64(
            pHostBcast, reinterpret_cast<void*>(Hooked_Linux_CHostServer_BroadcastGameData),
            kPatHostBcast14, sizeof(kPatHostBcast14));
        void* trampSend = InstallLinuxInlineTrampoline64(
            pHostSend, reinterpret_cast<void*>(Hooked_Linux_CHostServer_SendGameData),
            kPatHostSend17, sizeof(kPatHostSend17));
        if (trampBcast) g_origLinuxHostServerBroadcastGameData = reinterpret_cast<PFN_Linux_CHostServer_BroadcastGameData>(trampBcast);
        if (trampSend)  g_origLinuxHostServerSendGameData      = reinterpret_cast<PFN_Linux_CHostServer_SendGameData>(trampSend);
        if (trampBcast && trampSend) {
            g_hookedLinuxK2Base.store(baseK2, std::memory_order_release);
            LogArcadeLine("[NATIVE-GUARD][KILL-PACKET] Installed Linux CHostServer BroadcastGameData/SendGameData framing guards.\n");
        } else {
            g_failedLinuxK2Base.store(baseK2, std::memory_order_release);
            char errBuf[192];
            std::snprintf(errBuf, sizeof(errBuf),
                "[NATIVE-GUARD][KILL-PACKET] ERROR: Failed installing CHostServer hooks (base=%p, bcast=%p, send=%p); latched fail-once for this module.\n",
                reinterpret_cast<void*>(baseK2), trampBcast, trampSend);
            LogArcadeLine(errBuf);
        }
    }

    uintptr_t baseGame = FindModuleBaseByName("game-x86_64-server.so");
    if (baseGame != 0
        && baseGame != g_hookedLinuxGameBase.load(std::memory_order_acquire)
        && baseGame != g_failedLinuxGameBase.load(std::memory_order_acquire)) {
        static const uint8_t kPatGameBcast14[14] = {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x38
        };
        static const uint8_t kPatGameSend14[14] = {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x48
        };
        void* pGameBcast = reinterpret_cast<void*>(baseGame + LinuxOffsets::kRvaGameServerBroadcastGameData);
        void* pGameSend  = reinterpret_cast<void*>(baseGame + LinuxOffsets::kRvaGameServerSendGameData);
        void* trampGameBcast = InstallLinuxInlineTrampoline64(
            pGameBcast, reinterpret_cast<void*>(Hooked_Linux_CGameServer_BroadcastGameData),
            kPatGameBcast14, sizeof(kPatGameBcast14));
        void* trampGameSend = InstallLinuxInlineTrampoline64(
            pGameSend, reinterpret_cast<void*>(Hooked_Linux_CGameServer_SendGameData),
            kPatGameSend14, sizeof(kPatGameSend14));
        if (trampGameBcast) g_origLinuxGameServerBroadcastGameData = reinterpret_cast<PFN_Linux_CGameServer_BroadcastGameData>(trampGameBcast);
        if (trampGameSend)  g_origLinuxGameServerSendGameData      = reinterpret_cast<PFN_Linux_CGameServer_SendGameData>(trampGameSend);
        if (trampGameBcast && trampGameSend) {
            g_hookedLinuxGameBase.store(baseGame, std::memory_order_release);
            LogArcadeLine("[NATIVE-GUARD][KILL-PACKET] Installed Linux CGameServer BroadcastGameData/SendGameData (replay + network) guards.\n");
        } else {
            g_failedLinuxGameBase.store(baseGame, std::memory_order_release);
            char errBuf[192];
            std::snprintf(errBuf, sizeof(errBuf),
                "[NATIVE-GUARD][KILL-PACKET] ERROR: Failed installing CGameServer hooks (base=%p, bcast=%p, send=%p); latched fail-once for this module.\n",
                reinterpret_cast<void*>(baseGame), trampGameBcast, trampGameSend);
            LogArcadeLine(errBuf);
        }
    }

    uintptr_t baseGS = FindModuleBaseByName("libgame_shared-x86_64-server.so");
    if (baseGS == 0) {
        baseGS = FindModuleBaseByName("libgame_shared-x86_64.so");
    }
    if (baseGS != 0
        && baseGS != g_hookedLinuxGameSharedBase.load(std::memory_order_acquire)
        && baseGS != g_failedLinuxGameSharedBase.load(std::memory_order_acquire)) {
        static const uint8_t kPatKillHelper17[17] = {
            0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x48, 0x81, 0xEC, 0xF8, 0x01, 0x00, 0x00
        };
        void* pKillHelper = reinterpret_cast<void*>(baseGS + LinuxOffsets::kRvaKillNotifyHelper);
        void* trampKill = InstallLinuxInlineTrampoline64(
            pKillHelper, reinterpret_cast<void*>(Hooked_Linux_KillNotifyHelper),
            kPatKillHelper17, sizeof(kPatKillHelper17));
        if (trampKill) {
            g_origLinuxKillNotifyHelper = reinterpret_cast<PFN_Linux_KillNotifyHelper>(trampKill);
            g_hookedLinuxGameSharedBase.store(baseGS, std::memory_order_release);
            LogArcadeLine("[NATIVE-GUARD][KILL-PACKET] Installed Linux Stage 1B 128B KillNotifyHelper assist expansion hook.\n");
        } else {
            g_failedLinuxGameSharedBase.store(baseGS, std::memory_order_release);
            char errBuf[192];
            std::snprintf(errBuf, sizeof(errBuf),
                "[NATIVE-GUARD][KILL-PACKET] ERROR: Failed installing KillNotifyHelper hook (base=%p); latched fail-once for this module.\n",
                reinterpret_cast<void*>(baseGS));
            LogArcadeLine(errBuf);
        }
    }

    const bool allReady = (g_hookedLinuxK2Base.load(std::memory_order_acquire) != 0);
    pthread_mutex_unlock(&g_linuxKillGuardLock);
    return allReady;
}

uint64_t GetLinuxKillPacketClampCount() noexcept {
    return g_linuxKillPacketClampCount.load(std::memory_order_relaxed);
}

uint64_t GetLinuxKillPacketExpandCount() noexcept {
    return g_linuxKillPacketExpandCount.load(std::memory_order_relaxed);
}

} // namespace Kongor

#include "../../include/kongor_deterministic_math.h"

extern "C" {

float Kongor_NativeGuard_FmaRoundToOdd_f32(float a, float b, float c) {
    return Kongor_FmaRoundToOdd_f32(a, b, c);
}

int Kongor_NativeGuard_VerifyFmaLockstepOracle(uint32_t* outFailedMask) {
    return Kongor_VerifyFmaRoundToOddGoldenVectors(outFailedMask);
}

} // extern "C"

// ============================================================================
// LINUX CLIENT MULTI-MONITOR X11/XRANDR DISCOVERY, WINDOW MOVER & GLX PACER
// Strictly libstdc++-free, -fno-exceptions -fno-rtti, runtime dlopen-only so
// headless dedicated servers (hon-x86_64-server) never depend on X11 libraries.
// ============================================================================

namespace KongorLinuxDisplay {

using X11XID = unsigned long;
using X11Window = X11XID;
using X11Atom = unsigned long;
using X11Time = unsigned long;
using X11RROutput = X11XID;
using X11RRCrtc = X11XID;
using X11RRMode = X11XID;

struct X11DisplayOpaque;

struct X11RRModeInfo {
    X11RRMode id;
    unsigned int width;
    unsigned int height;
    unsigned long dotClock;
    unsigned int hSyncStart;
    unsigned int hSyncEnd;
    unsigned int hTotal;
    unsigned int hSkew;
    unsigned int vSyncStart;
    unsigned int vSyncEnd;
    unsigned int vTotal;
    char* name;
    unsigned int nameLength;
    unsigned long modeFlags;
};

struct X11RRScreenResources {
    X11Time timestamp;
    X11Time configTimestamp;
    int ncrtc;
    X11RRCrtc* crtcs;
    int noutput;
    X11RROutput* outputs;
    int nmode;
    X11RRModeInfo* modes;
};

struct X11RROutputInfo {
    X11Time timestamp;
    X11RRCrtc crtc;
    char* name;
    int nameLen;
    unsigned long mm_width;
    unsigned long mm_height;
    unsigned short connection; // 0 == RR_Connected
    unsigned short subpixel_order;
    int ncrtc;
    X11RRCrtc* crtcs;
    int nclone;
    X11RROutput* clones;
    int nmode;
    int npreferred;
    X11RRMode* modes;
};

struct X11RRCrtcInfo {
    X11Time timestamp;
    int x;
    int y;
    unsigned int width;
    unsigned int height;
    X11RRMode mode;
    unsigned short rotation;
    int noutput;
    X11RROutput* outputs;
    unsigned short rotations;
    int npossible;
    X11RROutput* possible;
};

struct X11RRMonitorInfo {
    X11Atom name;
    int primary;
    int automatic;
    int noutput;
    int x;
    int y;
    int width;
    int height;
    int mwidth;
    int mheight;
    X11RROutput* outputs;
};

struct X11ClientMessageEvent {
    int type; // 33 == ClientMessage
    unsigned long serial;
    int send_event;
    X11DisplayOpaque* display;
    X11Window window;
    X11Atom message_type;
    int format;
    union {
        char b[20];
        short s[10];
        long l[5];
    } data;
};

struct X11WindowAttributes {
    int x, y;
    int width, height;
    int border_width;
    int depth;
    void* visual;
    X11Window root;
    int c_class;
    int bit_gravity;
    int win_gravity;
    int backing_store;
    unsigned long backing_planes;
    unsigned long backing_pixel;
    int save_under;
    X11XID colormap;
    int map_installed;
    int map_state;
    long all_event_masks;
    long your_event_mask;
    long do_not_propagate_mask;
    int override_redirect;
    void* screen;
    unsigned char pad[64];
};

struct LinuxX11Api {
    bool initialized;
    bool available;
    void* libX11;
    void* libXrandr;

    X11DisplayOpaque* (*XOpenDisplay)(const char*);
    int (*XCloseDisplay)(X11DisplayOpaque*);
    X11Window (*XDefaultRootWindow)(X11DisplayOpaque*);
    X11Atom (*XInternAtom)(X11DisplayOpaque*, const char*, int);
    char* (*XGetAtomName)(X11DisplayOpaque*, X11Atom);
    int (*XFree)(void*);
    int (*XFlush)(X11DisplayOpaque*);
    int (*XSync)(X11DisplayOpaque*, int);
    int (*XMoveResizeWindow)(X11DisplayOpaque*, X11Window, int, int, unsigned int, unsigned int);
    int (*XRaiseWindow)(X11DisplayOpaque*, X11Window);
    int (*XSendEvent)(X11DisplayOpaque*, X11Window, int, long, void*);
    int (*XTranslateCoordinates)(X11DisplayOpaque*, X11Window, X11Window, int, int, int*, int*, X11Window*);
    int (*XGetWindowAttributes)(X11DisplayOpaque*, X11Window, X11WindowAttributes*);
    void* (*XSetErrorHandler)(int (*)(X11DisplayOpaque*, void*));

    X11RRScreenResources* (*XRRGetScreenResourcesCurrent)(X11DisplayOpaque*, X11Window);
    void (*XRRFreeScreenResources)(X11RRScreenResources*);
    X11RROutput (*XRRGetOutputPrimary)(X11DisplayOpaque*, X11Window);
    X11RROutputInfo* (*XRRGetOutputInfo)(X11DisplayOpaque*, X11RRScreenResources*, X11RROutput);
    void (*XRRFreeOutputInfo)(X11RROutputInfo*);
    X11RRCrtcInfo* (*XRRGetCrtcInfo)(X11DisplayOpaque*, X11RRScreenResources*, X11RRCrtc);
    void (*XRRFreeCrtcInfo)(X11RRCrtcInfo*);
    int (*XRRGetOutputProperty)(X11DisplayOpaque*, X11RROutput, X11Atom, long, long, int, int, X11Atom, X11Atom*, int*, unsigned long*, unsigned long*, unsigned char**);
    X11RRMonitorInfo* (*XRRGetMonitors)(X11DisplayOpaque*, X11Window, int, int*);
    void (*XRRFreeMonitors)(X11RRMonitorInfo*);
};

struct LinuxMonitorEntry {
    int index;
    char token[32];
    char friendlyName[96];
    bool isPrimary;
    int boundsX;
    int boundsY;
    int nativeWidth;
    int nativeHeight;
    int maxHz;
    char modesSpec[512];
};

static constexpr int kMaxLinuxMonitors = 16;
static constexpr unsigned long kRRInterlaceFlag = 0x00000010UL;

static pthread_mutex_t g_linuxDispMutex = PTHREAD_MUTEX_INITIALIZER;
static LinuxX11Api g_x11 = {};
static X11DisplayOpaque* g_auxDpy = nullptr;
static LinuxMonitorEntry g_linuxMonitors[kMaxLinuxMonitors] = {};
static int g_linuxMonitorCount = 0;

static std::atomic<unsigned long> g_linuxClientWindow{ 0 };
static std::atomic<int> g_linuxTargetMaxFps{ 0 };
static std::atomic<int> g_linuxManualFpsOverride{ 0 };
static std::atomic<int> g_linuxActiveMonitorIndex{ 1 };
static std::atomic<int> g_linuxPendingAutoMoveIdx{ 0 };
static std::atomic<uint64_t> g_linuxSuppressMoveUntilNs{ 0 };

static uint64_t GetMonotonicNs() noexcept {
    struct timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1000000000ULL + static_cast<uint64_t>(ts.tv_nsec);
}

static int IgnoreX11NonFatalError(X11DisplayOpaque* /*dpy*/, void* /*errEvt*/) {
    return 0;
}

static void NormalizeTokenBuf(const char* src, char* dst, size_t dstCap) noexcept {
    if (!dst || dstCap == 0) return;
    dst[0] = '\0';
    if (!src) return;
    while (*src == ' ' || *src == '\t' || *src == '\"' || *src == '\'' || *src == '\\' || *src == '/' || *src == '.') {
        ++src;
    }
    size_t pos = 0;
    for (size_t i = 0; src[i] != '\0' && pos + 1 < dstCap; ++i) {
        char ch = src[i];
        if (ch == ' ' || ch == '\t' || ch == '\"' || ch == '\'' || ch == '\r' || ch == '\n') break;
        if (ch >= 'a' && ch <= 'z') ch = static_cast<char>(ch - ('a' - 'A'));
        dst[pos++] = ch;
    }
    dst[pos] = '\0';
}

static void SanitizeDisplayLabel(const char* src, char* dst, size_t dstCap, const char* fallback) noexcept {
    if (!dst || dstCap == 0) return;
    dst[0] = '\0';
    const char* active = (src && src[0] != '\0') ? src : fallback;
    if (!active) return;

    size_t pos = 0;
    for (size_t i = 0; active[i] != '\0' && pos + 1 < dstCap; ++i) {
        unsigned char c = static_cast<unsigned char>(active[i]);
        if (c == '|' || c == '/' || c == ';' || c == '\"' || c == '\'' || c == '\\' || c == '\r' || c == '\n') {
            dst[pos++] = ' ';
        } else if (c >= 32 && c <= 126) {
            dst[pos++] = static_cast<char>(c);
        }
    }
    while (pos > 0 && dst[pos - 1] == ' ') --pos;
    dst[pos] = '\0';
    if (pos == 0 && fallback) {
        std::snprintf(dst, dstCap, "%s", fallback);
    }
}

int ParseEdidModelNameBytes(const uint8_t* edid, size_t len, char* outName, size_t outCap) noexcept {
    if (!outName || outCap == 0) return 0;
    outName[0] = '\0';
    if (!edid || len < 128) return 0;

    char fallbackText[16] = {};
    for (size_t off = 54; off + 18 <= 126; off += 18) {
        if (edid[off] == 0 && edid[off + 1] == 0 && edid[off + 2] == 0 &&
            (edid[off + 3] == 0xFC || edid[off + 3] == 0xFE)) {
            char buf[16] = {};
            size_t bpos = 0;
            for (size_t j = off + 5; j < off + 18 && bpos + 1 < sizeof(buf); ++j) {
                uint8_t b = edid[j];
                if (b == 0x0A || b == 0x00) break;
                if (b >= 32 && b <= 126) buf[bpos++] = static_cast<char>(b);
            }
            while (bpos > 0 && buf[bpos - 1] == ' ') buf[--bpos] = '\0';
            const char* start = buf;
            while (*start == ' ') ++start;
            if (*start != '\0') {
                if (edid[off + 3] == 0xFC) {
                    SanitizeDisplayLabel(start, outName, outCap, nullptr);
                    return outName[0] != '\0' ? 1 : 0;
                }
                if (fallbackText[0] == '\0') {
                    std::snprintf(fallbackText, sizeof(fallbackText), "%s", start);
                }
            }
        }
    }

    if (fallbackText[0] != '\0') {
        SanitizeDisplayLabel(fallbackText, outName, outCap, nullptr);
        return outName[0] != '\0' ? 1 : 0;
    }
    return 0;
}

static bool EnsureX11LoadedLocked() noexcept {
    const char* dispEnv = std::getenv("DISPLAY");
    if (!dispEnv || dispEnv[0] == '\0') {
        return false;
    }
    if (g_x11.initialized) {
        return g_x11.available;
    }
    g_x11.initialized = true;

    g_x11.libX11 = dlopen("libX11.so.6", RTLD_LAZY | RTLD_LOCAL);
    g_x11.libXrandr = dlopen("libXrandr.so.2", RTLD_LAZY | RTLD_LOCAL);
    if (!g_x11.libX11 || !g_x11.libXrandr) {
        return false;
    }

    g_x11.XOpenDisplay = reinterpret_cast<decltype(g_x11.XOpenDisplay)>(dlsym(g_x11.libX11, "XOpenDisplay"));
    g_x11.XCloseDisplay = reinterpret_cast<decltype(g_x11.XCloseDisplay)>(dlsym(g_x11.libX11, "XCloseDisplay"));
    g_x11.XDefaultRootWindow = reinterpret_cast<decltype(g_x11.XDefaultRootWindow)>(dlsym(g_x11.libX11, "XDefaultRootWindow"));
    g_x11.XInternAtom = reinterpret_cast<decltype(g_x11.XInternAtom)>(dlsym(g_x11.libX11, "XInternAtom"));
    g_x11.XGetAtomName = reinterpret_cast<decltype(g_x11.XGetAtomName)>(dlsym(g_x11.libX11, "XGetAtomName"));
    g_x11.XFree = reinterpret_cast<decltype(g_x11.XFree)>(dlsym(g_x11.libX11, "XFree"));
    g_x11.XFlush = reinterpret_cast<decltype(g_x11.XFlush)>(dlsym(g_x11.libX11, "XFlush"));
    g_x11.XSync = reinterpret_cast<decltype(g_x11.XSync)>(dlsym(g_x11.libX11, "XSync"));
    g_x11.XMoveResizeWindow = reinterpret_cast<decltype(g_x11.XMoveResizeWindow)>(dlsym(g_x11.libX11, "XMoveResizeWindow"));
    g_x11.XRaiseWindow = reinterpret_cast<decltype(g_x11.XRaiseWindow)>(dlsym(g_x11.libX11, "XRaiseWindow"));
    g_x11.XSendEvent = reinterpret_cast<decltype(g_x11.XSendEvent)>(dlsym(g_x11.libX11, "XSendEvent"));
    g_x11.XTranslateCoordinates = reinterpret_cast<decltype(g_x11.XTranslateCoordinates)>(dlsym(g_x11.libX11, "XTranslateCoordinates"));
    g_x11.XGetWindowAttributes = reinterpret_cast<decltype(g_x11.XGetWindowAttributes)>(dlsym(g_x11.libX11, "XGetWindowAttributes"));
    g_x11.XSetErrorHandler = reinterpret_cast<decltype(g_x11.XSetErrorHandler)>(dlsym(g_x11.libX11, "XSetErrorHandler"));

    g_x11.XRRGetScreenResourcesCurrent = reinterpret_cast<decltype(g_x11.XRRGetScreenResourcesCurrent)>(dlsym(g_x11.libXrandr, "XRRGetScreenResourcesCurrent"));
    g_x11.XRRFreeScreenResources = reinterpret_cast<decltype(g_x11.XRRFreeScreenResources)>(dlsym(g_x11.libXrandr, "XRRFreeScreenResources"));
    g_x11.XRRGetOutputPrimary = reinterpret_cast<decltype(g_x11.XRRGetOutputPrimary)>(dlsym(g_x11.libXrandr, "XRRGetOutputPrimary"));
    g_x11.XRRGetOutputInfo = reinterpret_cast<decltype(g_x11.XRRGetOutputInfo)>(dlsym(g_x11.libXrandr, "XRRGetOutputInfo"));
    g_x11.XRRFreeOutputInfo = reinterpret_cast<decltype(g_x11.XRRFreeOutputInfo)>(dlsym(g_x11.libXrandr, "XRRFreeOutputInfo"));
    g_x11.XRRGetCrtcInfo = reinterpret_cast<decltype(g_x11.XRRGetCrtcInfo)>(dlsym(g_x11.libXrandr, "XRRGetCrtcInfo"));
    g_x11.XRRFreeCrtcInfo = reinterpret_cast<decltype(g_x11.XRRFreeCrtcInfo)>(dlsym(g_x11.libXrandr, "XRRFreeCrtcInfo"));
    g_x11.XRRGetOutputProperty = reinterpret_cast<decltype(g_x11.XRRGetOutputProperty)>(dlsym(g_x11.libXrandr, "XRRGetOutputProperty"));
    g_x11.XRRGetMonitors = reinterpret_cast<decltype(g_x11.XRRGetMonitors)>(dlsym(g_x11.libXrandr, "XRRGetMonitors"));
    g_x11.XRRFreeMonitors = reinterpret_cast<decltype(g_x11.XRRFreeMonitors)>(dlsym(g_x11.libXrandr, "XRRFreeMonitors"));

    if (!g_x11.XOpenDisplay || !g_x11.XDefaultRootWindow || !g_x11.XRRGetScreenResourcesCurrent ||
        !g_x11.XRRGetOutputInfo || !g_x11.XRRGetCrtcInfo || !g_x11.XMoveResizeWindow) {
        return false;
    }

    if (g_x11.XSetErrorHandler) {
        g_x11.XSetErrorHandler(&IgnoreX11NonFatalError);
    }

    g_auxDpy = g_x11.XOpenDisplay(nullptr);
    g_x11.available = (g_auxDpy != nullptr);
    return g_x11.available;
}

static bool ParseKongorEnvFallbackMonitor(
    const char* envPayload,
    const char* targetToken,
    char* outName,
    size_t outNameCap,
    int* outMaxHz,
    char* outModes,
    size_t outModesCap) noexcept
{
    if (!envPayload || envPayload[0] == '\0' || !targetToken || targetToken[0] == '\0') {
        return false;
    }
    const char* cur = envPayload;
    while (*cur != '\0') {
        const char* slash = std::strchr(cur, '/');
        size_t entryLen = slash ? static_cast<size_t>(slash - cur) : std::strlen(cur);
        if (entryLen > 0 && entryLen < 1024) {
            char entry[1024];
            std::memcpy(entry, cur, entryLen);
            entry[entryLen] = '\0';

            // TOKEN|Idx|Name|Primary|W,H|MaxHz|Modes
            char* savePtr = nullptr;
            char* fTok = strtok_r(entry, "|", &savePtr);
            char* fIdx = strtok_r(nullptr, "|", &savePtr);
            char* fName = strtok_r(nullptr, "|", &savePtr);
            char* fPrim = strtok_r(nullptr, "|", &savePtr);
            char* fRes = strtok_r(nullptr, "|", &savePtr);
            char* fHz = strtok_r(nullptr, "|", &savePtr);
            char* fModes = strtok_r(nullptr, "|", &savePtr);
            (void)fIdx;
            (void)fPrim;
            (void)fRes;

            if (fTok && strcasecmp(fTok, targetToken) == 0) {
                if (fName && outName && outNameCap > 0) {
                    SanitizeDisplayLabel(fName, outName, outNameCap, targetToken);
                }
                if (fHz && outMaxHz) {
                    int hz = std::atoi(fHz);
                    if (hz > 0) *outMaxHz = hz;
                }
                if (fModes && outModes && outModesCap > 0) {
                    std::snprintf(outModes, outModesCap, "%s", fModes);
                }
                return true;
            }
        }
        if (!slash) break;
        cur = slash + 1;
    }
    return false;
}

static int RefreshLinuxMonitorsLocked() noexcept {
    g_linuxMonitorCount = 0;

    const char* envPayload = std::getenv("KONGOR_DETECTED_MONITORS");
    if (EnsureX11LoadedLocked() && g_auxDpy != nullptr) {
        X11Window root = g_x11.XDefaultRootWindow(g_auxDpy);
        X11RRScreenResources* res = g_x11.XRRGetScreenResourcesCurrent(g_auxDpy, root);
        X11RROutput primaryOut = g_x11.XRRGetOutputPrimary ? g_x11.XRRGetOutputPrimary(g_auxDpy, root) : 0;
        X11Atom edidAtom = g_x11.XInternAtom ? g_x11.XInternAtom(g_auxDpy, "EDID", 1) : 0;

        LinuxMonitorEntry raw[kMaxLinuxMonitors] = {};
        int rawCount = 0;

        if (res != nullptr) {
            for (int i = 0; i < res->noutput && rawCount < kMaxLinuxMonitors; ++i) {
                X11RROutput outId = res->outputs[i];
                X11RROutputInfo* outInfo = g_x11.XRRGetOutputInfo(g_auxDpy, res, outId);
                if (!outInfo) continue;

                if (outInfo->connection == 0 && outInfo->crtc != 0) {
                    X11RRCrtcInfo* crtc = g_x11.XRRGetCrtcInfo(g_auxDpy, res, outInfo->crtc);
                    if (crtc && crtc->width > 0 && crtc->height > 0) {
                        char rawOutName[64] = {};
                        if (outInfo->name && outInfo->nameLen > 0) {
                            size_t nlen = static_cast<size_t>(outInfo->nameLen) < sizeof(rawOutName) - 1
                                ? static_cast<size_t>(outInfo->nameLen)
                                : sizeof(rawOutName) - 1;
                            std::memcpy(rawOutName, outInfo->name, nlen);
                            rawOutName[nlen] = '\0';
                        } else {
                            std::snprintf(rawOutName, sizeof(rawOutName), "DISPLAY%d", rawCount + 1);
                        }

                        char token[32] = {};
                        NormalizeTokenBuf(rawOutName, token, sizeof(token));
                        char friendly[96] = {};
                        SanitizeDisplayLabel(rawOutName, friendly, sizeof(friendly), token);

                        if (edidAtom != 0 && g_x11.XRRGetOutputProperty) {
                            X11Atom actualType = 0;
                            int actualFormat = 0;
                            unsigned long nitems = 0;
                            unsigned long bytesAfter = 0;
                            unsigned char* propData = nullptr;
                            if (g_x11.XRRGetOutputProperty(
                                    g_auxDpy, outId, edidAtom, 0, 128, 0, 0, 0,
                                    &actualType, &actualFormat, &nitems, &bytesAfter, &propData) == 0 &&
                                propData != nullptr) {
                                if (actualFormat == 8 && nitems >= 128) {
                                    ParseEdidModelNameBytes(propData, static_cast<size_t>(nitems), friendly, sizeof(friendly));
                                }
                                if (g_x11.XFree) g_x11.XFree(propData);
                            }
                        }

                        int natW = static_cast<int>(crtc->width);
                        int natH = static_cast<int>(crtc->height);
                        int activeHz = 60;

                        struct ModeBucket {
                            int w;
                            int h;
                            int rates[16];
                            int rateCount;
                        };
                        ModeBucket buckets[64] = {};
                        int bucketCount = 0;

                        auto addRate = [&](int mw, int mh, int hz) {
                            if (mw < 640 || mh < 480) return;
                            if (hz <= 1 || hz > 1000) hz = 60;
                            int bIdx = -1;
                            for (int b = 0; b < bucketCount; ++b) {
                                if (buckets[b].w == mw && buckets[b].h == mh) {
                                    bIdx = b;
                                    break;
                                }
                            }
                            if (bIdx < 0) {
                                if (bucketCount >= 64) {
                                    // Never drop the native resolution bucket even if >64 custom modes exist
                                    if (mw == natW && mh == natH) {
                                        bIdx = 63;
                                        buckets[bIdx].w = mw;
                                        buckets[bIdx].h = mh;
                                        buckets[bIdx].rateCount = 0;
                                    } else {
                                        return;
                                    }
                                } else {
                                    bIdx = bucketCount++;
                                    buckets[bIdx].w = mw;
                                    buckets[bIdx].h = mh;
                                    buckets[bIdx].rateCount = 0;
                                }
                            }
                            for (int r = 0; r < buckets[bIdx].rateCount; ++r) {
                                if (buckets[bIdx].rates[r] == hz) return;
                            }
                            if (buckets[bIdx].rateCount < 16) {
                                buckets[bIdx].rates[buckets[bIdx].rateCount++] = hz;
                            } else {
                                // Replace lowest stored rate if a higher refresh rate is discovered
                                int minIdx = 0;
                                for (int r = 1; r < 16; ++r) {
                                    if (buckets[bIdx].rates[r] < buckets[bIdx].rates[minIdx]) minIdx = r;
                                }
                                if (hz > buckets[bIdx].rates[minIdx]) {
                                    buckets[bIdx].rates[minIdx] = hz;
                                }
                            }
                        };

                        for (int m = 0; m < outInfo->nmode; ++m) {
                            X11RRMode modeId = outInfo->modes[m];
                            for (int rm = 0; rm < res->nmode; ++rm) {
                                const X11RRModeInfo& mi = res->modes[rm];
                                if (mi.id == modeId) {
                                    if ((mi.modeFlags & kRRInterlaceFlag) != 0) break;
                                    if (mi.hTotal > 0 && mi.vTotal > 0 && mi.dotClock > 0) {
                                        double exactHz = static_cast<double>(mi.dotClock) /
                                                         (static_cast<double>(mi.hTotal) * static_cast<double>(mi.vTotal));
                                        int hz = static_cast<int>(std::lround(exactHz));
                                        if (modeId == crtc->mode && hz > 1) {
                                            activeHz = hz;
                                        }
                                        addRate(static_cast<int>(mi.width), static_cast<int>(mi.height), hz);
                                    }
                                    break;
                                }
                            }
                        }

                        addRate(natW, natH, activeHz);

                        // Prioritize native resolution bucket at index 0, sort remaining buckets by pixel area descending,
                        // and sort refresh rates descending within each bucket.
                        int natBucket = -1;
                        for (int b = 0; b < bucketCount; ++b) {
                            for (int a = 0; a < buckets[b].rateCount; ++a) {
                                for (int c = a + 1; c < buckets[b].rateCount; ++c) {
                                    if (buckets[b].rates[c] > buckets[b].rates[a]) {
                                        int tmp = buckets[b].rates[a];
                                        buckets[b].rates[a] = buckets[b].rates[c];
                                        buckets[b].rates[c] = tmp;
                                    }
                                }
                            }
                            if (buckets[b].w == natW && buckets[b].h == natH) {
                                natBucket = b;
                            }
                        }
                        if (natBucket > 0) {
                            ModeBucket tmp = buckets[0];
                            buckets[0] = buckets[natBucket];
                            buckets[natBucket] = tmp;
                        }
                        int sortStart = (bucketCount > 0 && buckets[0].w == natW && buckets[0].h == natH) ? 1 : 0;
                        for (int a = sortStart; a < bucketCount; ++a) {
                            for (int c = a + 1; c < bucketCount; ++c) {
                                long long areaA = static_cast<long long>(buckets[a].w) * static_cast<long long>(buckets[a].h);
                                long long areaC = static_cast<long long>(buckets[c].w) * static_cast<long long>(buckets[c].h);
                                if (areaC > areaA || (areaC == areaA && buckets[c].w > buckets[a].w)) {
                                    ModeBucket tmp = buckets[a];
                                    buckets[a] = buckets[c];
                                    buckets[c] = tmp;
                                }
                            }
                        }

                        int maxHzAtNat = (bucketCount > 0 && buckets[0].w == natW && buckets[0].h == natH && buckets[0].rateCount > 0)
                            ? buckets[0].rates[0]
                            : activeHz;
                        // Cap serialized modes per monitor (<= 8 modes, <= 6 rates, <= 180 chars) so 4+ monitors
                        // stay strictly below K2 CConsole::Execute MAX_CMD_LENGTH (1024 chars).
                        char modesBuf[220] = {};
                        size_t mpos = 0;
                        const int maxModesOut = bucketCount < 8 ? bucketCount : 8;
                        for (int b = 0; b < maxModesOut && mpos + 24 < sizeof(modesBuf); ++b) {
                            if (b > 0) modesBuf[mpos++] = '+';
                            mpos += static_cast<size_t>(std::snprintf(modesBuf + mpos, sizeof(modesBuf) - mpos, "%dx%d@", buckets[b].w, buckets[b].h));
                            const int maxRatesOut = buckets[b].rateCount < 6 ? buckets[b].rateCount : 6;
                            for (int r = 0; r < maxRatesOut && mpos + 8 < sizeof(modesBuf); ++r) {
                                if (r > 0) modesBuf[mpos++] = ',';
                                mpos += static_cast<size_t>(std::snprintf(modesBuf + mpos, sizeof(modesBuf) - mpos, "%d", buckets[b].rates[r]));
                            }
                        }

                        ParseKongorEnvFallbackMonitor(envPayload, token, friendly, sizeof(friendly), &maxHzAtNat, modesBuf, sizeof(modesBuf));

                        // Skip small auxiliary/StreamDeck panels (< 1024x720) unless primary
                        bool isPrimary = (primaryOut != 0 && outId == primaryOut);
                        if (isPrimary || (natW >= 1024 && natH >= 720)) {
                            LinuxMonitorEntry& dst = raw[rawCount++];
                            std::snprintf(dst.token, sizeof(dst.token), "%s", token);
                            std::snprintf(dst.friendlyName, sizeof(dst.friendlyName), "%s", friendly);
                            dst.isPrimary = isPrimary;
                            dst.boundsX = crtc->x;
                            dst.boundsY = crtc->y;
                            dst.nativeWidth = natW;
                            dst.nativeHeight = natH;
                            dst.maxHz = maxHzAtNat > 0 ? maxHzAtNat : 60;
                            std::snprintf(dst.modesSpec, sizeof(dst.modesSpec), "%s", modesBuf);
                        }
                    }
                    if (crtc) g_x11.XRRFreeCrtcInfo(crtc);
                }
                g_x11.XRRFreeOutputInfo(outInfo);
            }
            g_x11.XRRFreeScreenResources(res);
        }

        // XRandR 1.5 virtual multi-monitor fallback (e.g. xrandr --setmonitor on single-CRTC Xvfb/compositor)
        if (rawCount <= 1 && g_x11.XRRGetMonitors && g_x11.XRRFreeMonitors && g_x11.XGetAtomName) {
            int nmon = 0;
            X11RRMonitorInfo* vmon = g_x11.XRRGetMonitors(g_auxDpy, root, 1, &nmon);
            if (vmon != nullptr && nmon > rawCount) {
                rawCount = 0;
                for (int m = 0; m < nmon && rawCount < kMaxLinuxMonitors; ++m) {
                    if (vmon[m].width < 640 || vmon[m].height < 480) continue;
                    char* atomStr = g_x11.XGetAtomName(g_auxDpy, vmon[m].name);
                    char token[32] = {};
                    NormalizeTokenBuf(atomStr ? atomStr : "DISPLAY", token, sizeof(token));
                    if (atomStr && g_x11.XFree) g_x11.XFree(atomStr);
                    if (token[0] == '\0') {
                        std::snprintf(token, sizeof(token), "DISPLAY%d", rawCount + 1);
                    }

                    char friendly[96] = {};
                    SanitizeDisplayLabel(token, friendly, sizeof(friendly), token);
                    int maxHz = 60;
                    char modesBuf[512] = {};
                    std::snprintf(modesBuf, sizeof(modesBuf), "%dx%d@60", vmon[m].width, vmon[m].height);
                    ParseKongorEnvFallbackMonitor(envPayload, token, friendly, sizeof(friendly), &maxHz, modesBuf, sizeof(modesBuf));

                    LinuxMonitorEntry& dst = raw[rawCount++];
                    std::snprintf(dst.token, sizeof(dst.token), "%s", token);
                    std::snprintf(dst.friendlyName, sizeof(dst.friendlyName), "%s", friendly);
                    dst.isPrimary = vmon[m].primary != 0;
                    dst.boundsX = vmon[m].x;
                    dst.boundsY = vmon[m].y;
                    dst.nativeWidth = vmon[m].width;
                    dst.nativeHeight = vmon[m].height;
                    dst.maxHz = maxHz;
                    std::snprintf(dst.modesSpec, sizeof(dst.modesSpec), "%s", modesBuf);
                }
            }
            if (vmon != nullptr) g_x11.XRRFreeMonitors(vmon);
        }

        if (rawCount > 0) {
            bool anyPrimary = false;
            for (int i = 0; i < rawCount; ++i) {
                if (raw[i].isPrimary) anyPrimary = true;
            }
            if (!anyPrimary) {
                int zeroIdx = 0;
                for (int i = 0; i < rawCount; ++i) {
                    if (raw[i].boundsX == 0 && raw[i].boundsY == 0) {
                        zeroIdx = i;
                        break;
                    }
                }
                raw[zeroIdx].isPrimary = true;
            }

            // Sort primary first, then boundsX, then boundsY
            for (int i = 0; i < rawCount; ++i) {
                for (int j = i + 1; j < rawCount; ++j) {
                    bool swapIt = false;
                    if (!raw[i].isPrimary && raw[j].isPrimary) swapIt = true;
                    else if (raw[i].isPrimary == raw[j].isPrimary) {
                        if (raw[j].boundsX < raw[i].boundsX ||
                            (raw[j].boundsX == raw[i].boundsX && raw[j].boundsY < raw[i].boundsY)) {
                            swapIt = true;
                        }
                    }
                    if (swapIt) {
                        LinuxMonitorEntry tmp = raw[i];
                        raw[i] = raw[j];
                        raw[j] = tmp;
                    }
                }
            }

            g_linuxMonitorCount = rawCount;
            for (int i = 0; i < rawCount; ++i) {
                raw[i].index = i + 1;
                g_linuxMonitors[i] = raw[i];
            }
            return g_linuxMonitorCount;
        }
    }

    // Pure env fallback (e.g. unit tests or Wayland-only session seeded by WILLOWMAKER)
    if (envPayload && envPayload[0] != '\0') {
        const char* cur = envPayload;
        int offsetX = 0;
        while (*cur != '\0' && g_linuxMonitorCount < kMaxLinuxMonitors) {
            const char* slash = std::strchr(cur, '/');
            size_t entryLen = slash ? static_cast<size_t>(slash - cur) : std::strlen(cur);
            if (entryLen > 0 && entryLen < 1024) {
                char entry[1024];
                std::memcpy(entry, cur, entryLen);
                entry[entryLen] = '\0';

                char* savePtr = nullptr;
                char* fTok = strtok_r(entry, "|", &savePtr);
                char* fIdx = strtok_r(nullptr, "|", &savePtr);
                char* fName = strtok_r(nullptr, "|", &savePtr);
                char* fPrim = strtok_r(nullptr, "|", &savePtr);
                char* fRes = strtok_r(nullptr, "|", &savePtr);
                char* fHz = strtok_r(nullptr, "|", &savePtr);
                char* fModes = strtok_r(nullptr, "|", &savePtr);
                (void)fIdx;

                if (fTok && fRes && fHz) {
                    int w = 1920, h = 1080;
                    const char* comma = std::strchr(fRes, ',');
                    if (comma) {
                        w = std::atoi(fRes);
                        h = std::atoi(comma + 1);
                    }
                    if (w <= 0) w = 1920;
                    if (h <= 0) h = 1080;
                    int hz = std::atoi(fHz);
                    if (hz <= 0) hz = 60;

                    LinuxMonitorEntry& dst = g_linuxMonitors[g_linuxMonitorCount];
                    dst.index = g_linuxMonitorCount + 1;
                    NormalizeTokenBuf(fTok, dst.token, sizeof(dst.token));
                    SanitizeDisplayLabel(fName ? fName : fTok, dst.friendlyName, sizeof(dst.friendlyName), dst.token);
                    dst.isPrimary = (fPrim && fPrim[0] == '1');
                    dst.boundsX = offsetX;
                    dst.boundsY = 0;
                    dst.nativeWidth = w;
                    dst.nativeHeight = h;
                    dst.maxHz = hz;
                    std::snprintf(dst.modesSpec, sizeof(dst.modesSpec), "%s", (fModes && fModes[0] != '\0') ? fModes : "1920x1080@60");
                    offsetX += w;
                    ++g_linuxMonitorCount;
                }
            }
            if (!slash) break;
            cur = slash + 1;
        }
    }

    return g_linuxMonitorCount;
}

static size_t FormatLinuxMonitorsLocked(char* outBuf, size_t outCap) noexcept {
    if (!outBuf || outCap == 0) return 0;
    outBuf[0] = '\0';
    size_t pos = 0;
    for (int i = 0; i < g_linuxMonitorCount && pos + 64 < outCap; ++i) {
        const LinuxMonitorEntry& m = g_linuxMonitors[i];
        if (i > 0) outBuf[pos++] = '/';
        pos += static_cast<size_t>(std::snprintf(
            outBuf + pos,
            outCap - pos,
            "%s|%d|%s|%d|%d,%d|%d|%s",
            m.token,
            m.index,
            m.friendlyName,
            m.isPrimary ? 1 : 0,
            m.nativeWidth,
            m.nativeHeight,
            m.maxHz,
            m.modesSpec));
    }
    return pos;
}

static void ExecuteConsoleUtf8Direct(
    void* consolePtr,
    void (*execFn)(void*, const void*, unsigned int),
    unsigned int flags,
    const char* utf8Cmd) noexcept
{
    if (!consolePtr || !execFn || !utf8Cmd || utf8Cmd[0] == '\0') return;
    wchar_t wbuf[4096] = {};
    size_t wlen = Utf8ToWcharBuf(utf8Cmd, wbuf, 4096);
    LibcxxFakeLongWString fake{};
    fake.capWithLongBit = ((wlen + 1) << 1) | 1ULL;
    fake.size = wlen;
    fake.data = wbuf;
    execFn(consolePtr, &fake, flags);
}

static void MoveX11ClientWindowLocked(const LinuxMonitorEntry& target) noexcept {
    unsigned long rawWin = g_linuxClientWindow.load(std::memory_order_acquire);
    if (rawWin == 0 || !EnsureX11LoadedLocked() || g_auxDpy == nullptr) {
        return;
    }

    X11Window win = static_cast<X11Window>(rawWin);
    X11Window root = g_x11.XDefaultRootWindow(g_auxDpy);
    X11Atom wmState = g_x11.XInternAtom ? g_x11.XInternAtom(g_auxDpy, "_NET_WM_STATE", 0) : 0;
    X11Atom wmFullscreen = g_x11.XInternAtom ? g_x11.XInternAtom(g_auxDpy, "_NET_WM_STATE_FULLSCREEN", 0) : 0;

    auto sendFullscreenState = [&](long action) {
        if (!g_x11.XSendEvent || wmState == 0 || wmFullscreen == 0) return;
        X11ClientMessageEvent ev{};
        ev.type = 33; // ClientMessage
        ev.send_event = 1;
        ev.display = g_auxDpy;
        ev.window = win;
        ev.message_type = wmState;
        ev.format = 32;
        ev.data.l[0] = action; // 0 = REMOVE, 1 = ADD
        ev.data.l[1] = static_cast<long>(wmFullscreen);
        ev.data.l[2] = 0;
        ev.data.l[3] = 1; // Normal application source indication
        g_x11.XSendEvent(g_auxDpy, root, 0, (1L << 20) | (1L << 19), &ev);
    };

    // Temporarily drop EWMH fullscreen lock so X11 WMs permit cross-CRTC XMoveResizeWindow
    sendFullscreenState(0);
    g_x11.XMoveResizeWindow(
        g_auxDpy,
        win,
        target.boundsX,
        target.boundsY,
        static_cast<unsigned int>(target.nativeWidth),
        static_cast<unsigned int>(target.nativeHeight));
    if (g_x11.XRaiseWindow) {
        g_x11.XRaiseWindow(g_auxDpy, win);
    }
    sendFullscreenState(1);
    if (g_x11.XFlush) {
        g_x11.XFlush(g_auxDpy);
    }
}

static void PersistLinuxLauncherMonitorSelection(const char* deviceToken, int effectiveFps) noexcept {
    if (!deviceToken || deviceToken[0] == '\0') return;
    const char* candidatePaths[2] = {
        "userdataKONGOR/launcher_settings.json",
        "../userdataKONGOR/launcher_settings.json"
    };

    for (size_t pIdx = 0; pIdx < 2; ++pIdx) {
        std::FILE* fp = std::fopen(candidatePaths[pIdx], "rb");
        if (!fp) continue;
        char jsonBuf[16384] = {};
        size_t nread = std::fread(jsonBuf, 1, sizeof(jsonBuf) - 1, fp);
        std::fclose(fp);
        if (nread == 0) continue;
        jsonBuf[nread] = '\0';

        // Lightweight in-place patch of SelectedMonitorDeviceName / selectedMonitorDeviceName if present
        const char* keys[2] = { "\"SelectedMonitorDeviceName\"", "\"selectedMonitorDeviceName\"" };
        for (size_t k = 0; k < 2; ++k) {
            char* pos = std::strstr(jsonBuf, keys[k]);
            if (!pos) continue;
            char* colon = std::strchr(pos + std::strlen(keys[k]), ':');
            if (!colon) continue;
            char* q1 = std::strchr(colon + 1, '\"');
            if (!q1) continue;
            char* q2 = std::strchr(q1 + 1, '\"');
            if (!q2) continue;

            char updated[16896] = {};
            size_t prefixLen = static_cast<size_t>((q1 + 1) - jsonBuf);
            std::memcpy(updated, jsonBuf, prefixLen);
            int written = std::snprintf(updated + prefixLen, sizeof(updated) - prefixLen, "%s%s", deviceToken, q2);
            if (written > 0) {
                std::FILE* outFp = std::fopen(candidatePaths[pIdx], "wb");
                if (outFp) {
                    std::fwrite(updated, 1, prefixLen + static_cast<size_t>(written), outFp);
                    std::fclose(outFp);
                }
            }
            (void)effectiveFps;
            return;
        }
    }
}

static void ApplyMonitorRecordOnConsoleThread(
    const LinuxMonitorEntry& target,
    int explicitHz,
    void* consolePtr,
    void (*execFn)(void*, const void*, unsigned int),
    unsigned int flags,
    bool moveWindow) noexcept
{
    int targetHz = (explicitHz > 0) ? explicitHz : target.maxHz;
    if (targetHz <= 0) targetHz = 60;

    int manualOvr = g_linuxManualFpsOverride.load(std::memory_order_relaxed);
    int effectiveFps = (manualOvr > 0) ? manualOvr : targetHz;

    g_linuxActiveMonitorIndex.store(target.index, std::memory_order_release);
    g_linuxTargetMaxFps.store(effectiveFps, std::memory_order_release);
    g_linuxSuppressMoveUntilNs.store(GetMonotonicNs() + 2000000000ULL, std::memory_order_release);

    if (moveWindow) {
        pthread_mutex_lock(&g_linuxDispMutex);
        MoveX11ClientWindowLocked(target);
        pthread_mutex_unlock(&g_linuxDispMutex);
    }

    char cmd[2048];
    std::snprintf(
        cmd,
        sizeof(cmd),
        "SetSave \"vid_display\" \"%s\"; "
        "SetSave \"options_display\" \"%s\"; "
        "SetSave \"vid_resolution\" \"%d,%d\"; "
        "SetSave \"options_resolution\" \"%d,%d\"; "
        "SetSave \"vid_refreshRate\" \"%d\"; "
        "SetSave \"options_refreshrate\" \"%d\"; "
        "SetSave \"host_maxFPS\" \"%d\"; "
        "SetSave \"host_maximumFPS\" \"%d\"; "
        "SetSave \"cl_maxFPS\" \"%d\"; "
        "SetSave \"vid_maxFPS\" \"%d\"; "
        "UICmd \"if _G.SysBar_UpdateQuickSettingsUI then pcall(_G.SysBar_UpdateQuickSettingsUI) end; if _G.Kongor_PopulateDisplayOptions then pcall(_G.Kongor_PopulateDisplayOptions, 'sync') end\"",
        target.token,
        target.token,
        target.nativeWidth,
        target.nativeHeight,
        target.nativeWidth,
        target.nativeHeight,
        targetHz,
        targetHz,
        effectiveFps,
        effectiveFps,
        effectiveFps,
        effectiveFps);
    ExecuteConsoleUtf8Direct(consolePtr, execFn, flags, cmd);
    PersistLinuxLauncherMonitorSelection(target.token, effectiveFps);

    std::fprintf(
        stdout,
        "[NativeGuard:Linux] Applied Monitor #%d '%s' (%s) bounds=%dx%d+%d+%d @ %d Hz (effectiveMaxFPS=%d, moveWin=%d)\n",
        target.index,
        target.token,
        target.friendlyName,
        target.nativeWidth,
        target.nativeHeight,
        target.boundsX,
        target.boundsY,
        targetHz,
        effectiveFps,
        moveWindow ? 1 : 0);
    std::fflush(stdout);
}

static void SyncMonitorsToK2Console(
    void* consolePtr,
    void (*execFn)(void*, const void*, unsigned int),
    unsigned int flags) noexcept
{
    char payload[4096] = {};
    int count = 0;
    pthread_mutex_lock(&g_linuxDispMutex);
    count = RefreshLinuxMonitorsLocked();
    FormatLinuxMonitorsLocked(payload, sizeof(payload));
    pthread_mutex_unlock(&g_linuxDispMutex);

    if (count > 0 && payload[0] != '\0') {
        // K2 CConsole::Execute enforces MAX_CMD_LENGTH = 1024; execute CreateString separately from SetSave
        // and clamp payload <= 940 bytes so SetSave never triggers "Command too long:".
        payload[940] = '\0';
        ExecuteConsoleUtf8Direct(consolePtr, execFn, flags, "CreateString \"kongor_detected_monitors\" \"\"");
        char cmd[1020];
        std::snprintf(
            cmd,
            sizeof(cmd),
            "SetSave \"kongor_detected_monitors\" \"%s\"",
            payload);
        ExecuteConsoleUtf8Direct(consolePtr, execFn, flags, cmd);
        std::fprintf(stdout, "[NativeGuard:Linux] Synced %d XRandR monitor(s) -> %s\n", count, payload);
        std::fflush(stdout);
    }
}

void OnClientConsoleFrame(
    void* consolePtr,
    void (*execFn)(void*, const void*, unsigned int)) noexcept
{
    static uint32_t s_frameTick = 0;
    static bool s_bootSynced = false;

    ++s_frameTick;
    if (!s_bootSynced && s_frameTick >= 25) {
        s_bootSynced = true;

        const char* manEnv = std::getenv("KONGOR_MANUAL_FPS_OVERRIDE");
        if (manEnv && manEnv[0] != '\0') {
            int mv = std::atoi(manEnv);
            if (mv > 0) {
                g_linuxManualFpsOverride.store(mv, std::memory_order_release);
                g_linuxTargetMaxFps.store(mv, std::memory_order_release);
            }
        }

        SyncMonitorsToK2Console(consolePtr, execFn, 0);

        const char* targetEnv = std::getenv("KONGOR_TARGET_MONITOR");
        char normTarget[32] = {};
        if (targetEnv && targetEnv[0] != '\0') {
            NormalizeTokenBuf(targetEnv, normTarget, sizeof(normTarget));
        }

        pthread_mutex_lock(&g_linuxDispMutex);
        int chosenIdx = (g_linuxMonitorCount > 0) ? 0 : -1;
        if (normTarget[0] != '\0') {
            for (int i = 0; i < g_linuxMonitorCount; ++i) {
                if (strcasecmp(g_linuxMonitors[i].token, normTarget) == 0) {
                    chosenIdx = i;
                    break;
                }
            }
        }
        LinuxMonitorEntry bootMon = (chosenIdx >= 0) ? g_linuxMonitors[chosenIdx] : LinuxMonitorEntry{};
        pthread_mutex_unlock(&g_linuxDispMutex);

        if (chosenIdx >= 0) {
            ApplyMonitorRecordOnConsoleThread(bootMon, 0, consolePtr, execFn, 0, normTarget[0] != '\0');
        }
    }

    int pendingAutoIdx = g_linuxPendingAutoMoveIdx.exchange(0, std::memory_order_acq_rel);
    if (pendingAutoIdx > 0) {
        pthread_mutex_lock(&g_linuxDispMutex);
        bool valid = (pendingAutoIdx >= 1 && pendingAutoIdx <= g_linuxMonitorCount);
        LinuxMonitorEntry movedMon = valid ? g_linuxMonitors[pendingAutoIdx - 1] : LinuxMonitorEntry{};
        pthread_mutex_unlock(&g_linuxDispMutex);

        if (valid) {
            std::fprintf(
                stdout,
                "[NativeGuard:Linux] Detected manual X11 window move onto Monitor #%d (%s '%s' @ %d Hz)\n",
                movedMon.index,
                movedMon.token,
                movedMon.friendlyName,
                movedMon.maxHz);
            std::fflush(stdout);
            ApplyMonitorRecordOnConsoleThread(movedMon, 0, consolePtr, execFn, 0, false);
        }
    }
}

bool TryDispatchDisplayConsoleCommandW(
    const wchar_t* cmdLine,
    void* consolePtr,
    void (*execFn)(void*, const void*, unsigned int),
    unsigned int flags) noexcept
{
    if (!cmdLine) return false;
    const wchar_t* p = cmdLine;
    while (*p == L' ' || *p == L'\t') ++p;
    if (*p == L'\0') return false;

    const wchar_t* tokStart = p;
    while (*p != L'\0' && *p != L' ' && *p != L'\t' && *p != L'\r' && *p != L'\n') ++p;
    size_t tokLen = static_cast<size_t>(p - tokStart);
    while (*p == L' ' || *p == L'\t') ++p;

    if (tokLen == 22 && wcsncasecmp(tokStart, L"__kongor_sync_monitors", 22) == 0) {
        SyncMonitorsToK2Console(consolePtr, execFn, flags);
        return true;
    }

    if (tokLen == 22 && wcsncasecmp(tokStart, L"__kongor_apply_monitor", 22) == 0) {
        char argToken[64] = {};
        size_t aPos = 0;
        bool quoted = (*p == L'\"');
        if (quoted) ++p;
        while (*p != L'\0' && *p != L'\r' && *p != L'\n' && aPos + 1 < sizeof(argToken)) {
            if (quoted && *p == L'\"') { ++p; break; }
            if (!quoted && (*p == L' ' || *p == L'\t' || *p == L';')) break;
            argToken[aPos++] = static_cast<char>(*p & 0x7F);
            ++p;
        }
        argToken[aPos] = '\0';

        while (*p == L' ' || *p == L'\t') ++p;
        int explicitHz = 0;
        if (*p >= L'0' && *p <= L'9') {
            explicitHz = static_cast<int>(wcstol(p, nullptr, 10));
        }

        char normArg[32] = {};
        NormalizeTokenBuf(argToken, normArg, sizeof(normArg));
        int requestedIdx = std::atoi(normArg);

        pthread_mutex_lock(&g_linuxDispMutex);
        if (g_linuxMonitorCount == 0) {
            RefreshLinuxMonitorsLocked();
        }
        int foundIdx = -1;
        for (int i = 0; i < g_linuxMonitorCount; ++i) {
            if ((normArg[0] != '\0' && strcasecmp(g_linuxMonitors[i].token, normArg) == 0) ||
                (requestedIdx > 0 && g_linuxMonitors[i].index == requestedIdx)) {
                foundIdx = i;
                break;
            }
        }
        if (foundIdx < 0 && g_linuxMonitorCount > 0) {
            // Also support DISPLAY<N> alias mapping to 1-based index N on Linux
            if (std::strncmp(normArg, "DISPLAY", 7) == 0) {
                int dispNum = std::atoi(normArg + 7);
                if (dispNum >= 1 && dispNum <= g_linuxMonitorCount) {
                    foundIdx = dispNum - 1;
                }
            }
        }
        LinuxMonitorEntry target = (foundIdx >= 0) ? g_linuxMonitors[foundIdx] : LinuxMonitorEntry{};
        pthread_mutex_unlock(&g_linuxDispMutex);

        if (foundIdx >= 0) {
            ApplyMonitorRecordOnConsoleThread(target, explicitHz, consolePtr, execFn, flags, true);
        } else {
            std::fprintf(stderr, "[NativeGuard:Linux] WARN: __kongor_apply_monitor could not resolve target '%s'\n", normArg);
            std::fflush(stderr);
        }
        return true;
    }

    return false;
}

static void PollClientWindowMonitorMove(uint64_t nowNs) noexcept {
    if (nowNs < g_linuxSuppressMoveUntilNs.load(std::memory_order_acquire)) {
        return;
    }
    unsigned long rawWin = g_linuxClientWindow.load(std::memory_order_relaxed);
    if (rawWin == 0) return;

    pthread_mutex_lock(&g_linuxDispMutex);
    if (g_linuxMonitorCount > 1 && EnsureX11LoadedLocked() && g_auxDpy != nullptr &&
        g_x11.XTranslateCoordinates && g_x11.XGetWindowAttributes) {
        X11Window win = static_cast<X11Window>(rawWin);
        X11Window root = g_x11.XDefaultRootWindow(g_auxDpy);
        X11WindowAttributes attrs{};
        int rx = 0, ry = 0;
        X11Window child = 0;
        if (g_x11.XGetWindowAttributes(g_auxDpy, win, &attrs) != 0 &&
            attrs.width > 0 && attrs.height > 0 &&
            g_x11.XTranslateCoordinates(g_auxDpy, win, root, attrs.width / 2, attrs.height / 2, &rx, &ry, &child) != 0) {
            for (int i = 0; i < g_linuxMonitorCount; ++i) {
                const LinuxMonitorEntry& m = g_linuxMonitors[i];
                if (rx >= m.boundsX && rx < (m.boundsX + m.nativeWidth) &&
                    ry >= m.boundsY && ry < (m.boundsY + m.nativeHeight)) {
                    if (m.index != g_linuxActiveMonitorIndex.load(std::memory_order_relaxed)) {
                        g_linuxActiveMonitorIndex.store(m.index, std::memory_order_release);
                        g_linuxPendingAutoMoveIdx.store(m.index, std::memory_order_release);
                    }
                    break;
                }
            }
        }
    }
    pthread_mutex_unlock(&g_linuxDispMutex);
}

} // namespace KongorLinuxDisplay

extern "C" {

__attribute__((visibility("default")))
int Kongor_Linux_ParseEdidModelName(const uint8_t* edid, size_t len, char* outName, size_t outCap) {
    return ::KongorLinuxDisplay::ParseEdidModelNameBytes(edid, len, outName, outCap);
}

__attribute__((visibility("default")))
int Kongor_Linux_FormatDetectedMonitors(char* outBuf, size_t outCap) {
    pthread_mutex_lock(&::KongorLinuxDisplay::g_linuxDispMutex);
    int count = ::KongorLinuxDisplay::RefreshLinuxMonitorsLocked();
    ::KongorLinuxDisplay::FormatLinuxMonitorsLocked(outBuf, outCap);
    pthread_mutex_unlock(&::KongorLinuxDisplay::g_linuxDispMutex);
    return count;
}

__attribute__((visibility("default")))
int Kongor_Linux_GetTargetMaxFps() {
    return ::KongorLinuxDisplay::g_linuxTargetMaxFps.load(std::memory_order_acquire);
}

// Interpose glXSwapBuffers for Linux client (hon-x86_64 -> vid_gl2-x86_64.so)
// Captures active X11 client window drawable, detects cross-monitor window moves,
// and enforces sub-0.1ms CLOCK_MONOTONIC frame pacing matched to monitor MaxHz.
__attribute__((visibility("default")))
void glXSwapBuffers(void* dpy, unsigned long drawable) {
    using FnGlXSwapBuffers = void(*)(void*, unsigned long);
    static std::atomic<FnGlXSwapBuffers> s_realSwap{ nullptr };

    FnGlXSwapBuffers realSwap = s_realSwap.load(std::memory_order_acquire);
    if (!realSwap) {
        realSwap = reinterpret_cast<FnGlXSwapBuffers>(dlsym(RTLD_NEXT, "glXSwapBuffers"));
        if (!realSwap) {
            void* libGl = dlopen("libGL.so.1", RTLD_LAZY | RTLD_NOLOAD);
            if (!libGl) libGl = dlopen("libGLX.so.0", RTLD_LAZY | RTLD_NOLOAD);
            if (libGl) {
                realSwap = reinterpret_cast<FnGlXSwapBuffers>(dlsym(libGl, "glXSwapBuffers"));
                dlclose(libGl);
            }
        }
        if (realSwap) {
            s_realSwap.store(realSwap, std::memory_order_release);
        }
    }

    if (drawable != 0 && g_serverPort == 0 && !g_killSwitchActive.load(std::memory_order_relaxed)) {
        ::KongorLinuxDisplay::g_linuxClientWindow.store(drawable, std::memory_order_relaxed);
    }

    if (realSwap) {
        realSwap(dpy, drawable);
    }

    if (g_serverPort != 0 || g_killSwitchActive.load(std::memory_order_relaxed)) {
        return;
    }

    const uint64_t nowNs = ::KongorLinuxDisplay::GetMonotonicNs();
    static uint64_t s_lastMovePollNs = 0;
    if (nowNs - s_lastMovePollNs >= 250000000ULL) {
        s_lastMovePollNs = nowNs;
        ::KongorLinuxDisplay::PollClientWindowMonitorMove(nowNs);
    }

    const int targetFps = ::KongorLinuxDisplay::g_linuxTargetMaxFps.load(std::memory_order_relaxed);
    static uint64_t s_nextDeadlineNs = 0;
    if (targetFps >= 24 && targetFps < 1000) {
        const uint64_t frameIntervalNs = 1000000000ULL / static_cast<uint64_t>(targetFps);
        if (s_nextDeadlineNs == 0 || nowNs > s_nextDeadlineNs + frameIntervalNs * 4) {
            s_nextDeadlineNs = nowNs + frameIntervalNs;
        } else {
            if (nowNs < s_nextDeadlineNs) {
                const uint64_t remNs = s_nextDeadlineNs - nowNs;
                if (remNs > 500000ULL) {
                    const uint64_t wakeNs = s_nextDeadlineNs - 400000ULL;
                    struct timespec wakeTs{};
                    wakeTs.tv_sec = static_cast<time_t>(wakeNs / 1000000000ULL);
                    wakeTs.tv_nsec = static_cast<long>(wakeNs % 1000000000ULL);
                    clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &wakeTs, nullptr);
                }
                while (::KongorLinuxDisplay::GetMonotonicNs() < s_nextDeadlineNs) {
                    // Sub-0.4ms precision spin tail
                }
            }
            s_nextDeadlineNs += frameIntervalNs;
        }
    } else {
        s_nextDeadlineNs = 0;
    }
}

} // extern "C"

#endif // !_WIN32
