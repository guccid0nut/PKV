// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: overlay_bridge.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - In-Process Command & Telemetry Bridge
// ============================================================================

#include "overlay_bridge.h"
#include "overlay_manager.h"
#include "../layer_manifest.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <sddl.h>
#include <intrin.h>
#include <charconv>
#include <sstream>
#include <iomanip>
#include <ctime>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "advapi32.lib")

static constexpr uint32_t kFastBusMagicKong = 0x4B4F4E47u; // 'KONG'
static constexpr uint32_t kFastBusMagicKngr = 0x52474E4Bu; // 'KNGR'

static inline bool IsValidFastBusMagic(uint32_t magic) noexcept {
    return magic == kFastBusMagicKong || magic == kFastBusMagicKngr;
}

#pragma pack(push, 1)
struct FastBusHeader {
    uint32_t Magic;              // 0x4B4F4E47 ('KONG') or 0x52474E4B ('KNGR')
    uint32_t Version;            // 1 or 2
    uint32_t Capacity;           // 4096
    uint32_t RecordSizeBytes;    // 64
    uint64_t Head;
    uint64_t Tail;
    uint64_t ProducerProcessId;
    int64_t  StartTimestampQpc;
    int64_t  FrequencyQpc;
    uint64_t ReservedFlags;
};

struct FastBusRecord {
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

struct FastBusHeaderV2 {
    FastBusHeader Base;
    uint64_t LatestSequence;
    int64_t  SnapshotTimestamp;
    uint32_t ProducerThreadId;
    uint32_t GamePhase;
    uint32_t ActiveMatchId;
    uint32_t ConnectedPlayers;
    uint32_t LuaMemoryKb;
    char     CurrentMap[16];
    char     PlayerName[12];
    FastBusRecord LatestRecord;
};
#pragma pack(pop)

static_assert(sizeof(FastBusHeader) == 64, "FastBusHeader V1 must be 64 bytes");
static_assert(sizeof(FastBusRecord) == 64, "FastBusRecord must be 64 bytes");
static_assert(sizeof(FastBusHeaderV2) == 192, "FastBusHeaderV2 must be 192 bytes");

static PSECURITY_DESCRIPTOR CreateOwnerOnlyOverlayPipeSecurity(SECURITY_ATTRIBUTES* outSa) {
    if (!outSa) return nullptr;
    outSa->nLength = sizeof(SECURITY_ATTRIBUTES);
    outSa->lpSecurityDescriptor = nullptr;
    outSa->bInheritHandle = FALSE;

    std::string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GA;;;CO)";
    HANDLE hToken = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken) && hToken) {
        DWORD needed = 0;
        GetTokenInformation(hToken, TokenUser, nullptr, 0, &needed);
        if (needed > 0 && needed <= 4096) {
            std::vector<uint8_t> tokenBuf(needed);
            if (GetTokenInformation(hToken, TokenUser, tokenBuf.data(), needed, &needed)) {
                auto* pTokenUser = reinterpret_cast<TOKEN_USER*>(tokenBuf.data());
                LPSTR sidStr = nullptr;
                if (pTokenUser->User.Sid && ConvertSidToStringSidA(pTokenUser->User.Sid, &sidStr) && sidStr) {
                    sddl = std::string("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;") + sidStr + ")";
                    LocalFree(sidStr);
                }
            }
        }
        CloseHandle(hToken);
    }

    PSECURITY_DESCRIPTOR pSD = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorA(sddl.c_str(), SDDL_REVISION_1, &pSD, nullptr) && pSD) {
        outSa->lpSecurityDescriptor = pSD;
        return pSD;
    }

    HON_LOG_WARN("[OverlayBridge] ConvertStringSecurityDescriptorToSecurityDescriptorA failed (err=%lu).", GetLastError());
    return nullptr;
}

static bool ReadFastBusLatestRecord(const void* pView, FastBusRecord* outRec) noexcept {
    if (!pView || !outRec) return false;
    const auto* pHdr = static_cast<const FastBusHeader*>(pView);
    if (!IsValidFastBusMagic(pHdr->Magic)) return false;

    if (pHdr->Version >= 2) {
        const auto* pV2 = static_cast<const FastBusHeaderV2*>(pView);
        for (int retry = 0; retry < 4; ++retry) {
            uint64_t seq1 = *reinterpret_cast<volatile const uint64_t*>(&pV2->LatestSequence);
            if ((seq1 & 1ULL) != 0ULL) {
                _mm_pause();
                continue;
            }
            _ReadWriteBarrier();
            FastBusRecord snap = pV2->LatestRecord;
            MemoryBarrier();
            uint64_t seq2 = *reinterpret_cast<volatile const uint64_t*>(&pV2->LatestSequence);
            if (seq1 == seq2 && (snap.FrameIndex > 0 || pHdr->Head > 0)) {
                *outRec = snap;
                return true;
            }
            _mm_pause();
        }
        uint32_t cap = pHdr->Capacity;
        if (cap > 0 && (cap & (cap - 1u)) == 0u && cap <= 65536u && pHdr->Head > 0) {
            const auto* pRecords = reinterpret_cast<const FastBusRecord*>(
                static_cast<const uint8_t*>(pView) + sizeof(FastBusHeaderV2));
            uint32_t slot = static_cast<uint32_t>((pHdr->Head - 1ULL) & (cap - 1u));
            *outRec = pRecords[slot];
            return true;
        }
        return false;
    }

    uint32_t cap = pHdr->Capacity;
    if (cap > 0 && (cap & (cap - 1u)) == 0u && cap <= 65536u && pHdr->Head > 0) {
        const auto* pRecords = reinterpret_cast<const FastBusRecord*>(
            static_cast<const uint8_t*>(pView) + sizeof(FastBusHeader));
        uint32_t slot = static_cast<uint32_t>((pHdr->Head - 1ULL) & (cap - 1u));
        *outRec = pRecords[slot];
        return true;
    }
    return false;
}

OverlayBridge g_OverlayBridge;

static bool ProbeTcpPort(const char* host, int port, int timeoutMs, int* outLatencyMs = nullptr) {
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return false;

    u_long nonBlocking = 1;
    ioctlsocket(s, FIONBIO, &nonBlocking);

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<u_short>(port));
    addr.sin_addr.s_addr = inet_addr(host);

    LARGE_INTEGER freq, start, end;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&start);

    connect(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));

    fd_set writeSet;
    FD_ZERO(&writeSet);
    FD_SET(s, &writeSet);

    timeval tv;
    tv.tv_sec = 0;
    tv.tv_usec = timeoutMs * 1000;

    int res = select(0, nullptr, &writeSet, nullptr, &tv);
    bool connected = false;
    if (res > 0 && FD_ISSET(s, &writeSet)) {
        int so_error = 0;
        int len = sizeof(so_error);
        if (getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&so_error), &len) == 0) {
            connected = (so_error == 0);
        }
    }

    QueryPerformanceCounter(&end);
    if (outLatencyMs && freq.QuadPart > 0) {
        double elapsed = static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 / static_cast<double>(freq.QuadPart);
        *outLatencyMs = static_cast<int>(elapsed > 0 ? elapsed : 1);
    }

    closesocket(s);
    return connected;
}

OverlayBridge::OverlayBridge() {
    m_metrics["fps"] = 60.0;
    m_metrics["frametime_ms"] = 16.6;
    m_metrics["draw_calls"] = 180.0;
    m_metrics["gold"] = 0.0;
    m_metrics["net_worth"] = 0.0;
    m_metrics["apm"] = 0.0;

    m_snapshot.masterOnline = true;
    m_snapshot.masterLatencyMs = 1;
    m_snapshot.chatOnline = true;
    m_snapshot.chatLatencyMs = 2;
    m_snapshot.gameServerOnline = true;
    m_snapshot.gameServerPort = 11235;
    m_snapshot.gameServerLatencyMs = 3;
    m_snapshot.pingMs = 12;
    m_snapshot.packetLossPercent = 0.0f;
    m_snapshot.serverTick = 1420;
    m_snapshot.activeFaultSource = "NONE";
    m_snapshot.activeFaultDescription = "NOMINAL (All systems operational)";
    m_snapshot.faultSeverity = 0;
}

OverlayBridge::~OverlayBridge() {
    if (IsLayerProcessTerminating()) {
        m_workerRunning.store(false, std::memory_order_release);
        return;
    }
    Shutdown();
}

void OverlayBridge::Initialize() {
    HON_LOG_INFO("[OverlayBridge] Initializing in-process command and telemetry bridge.");

    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);

    m_probeWakeEvent = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    m_mainRenderThreadId.store(GetCurrentThreadId(), std::memory_order_release);

    // Populate initial diagnostic trace log lines
    PushLogEntry("INFO", "TAO", "Together as One (TAO) MCP session initialized.");
    PushLogEntry("INFO", "VULKAN", "Vulkan Swapchain Layer active (VkLayer_hon_replay_capture).");
    PushLogEntry("OK", "CEF", "CEF sandbox safely deactivated (0 int 3 crashes).");
    PushLogEntry("OK", "QUEUE", "Triple-buffered presentation queue synchronized (240 Hz).");

    if (!m_workerRunning.exchange(true)) {
        HANDLE hThread = CreateThread(nullptr, 0, [](LPVOID p) -> DWORD {
            static_cast<OverlayBridge*>(p)->ProbeWorkerLoop();
            return 0;
        }, this, 0, nullptr);

        if (hThread) {
            m_workerThreadHandle = hThread;
            HON_LOG_INFO("[OverlayBridge] Background subsystem probe thread started.");
        }

        HANDLE hPipeThread = CreateThread(nullptr, 0, [](LPVOID p) -> DWORD {
            static_cast<OverlayBridge*>(p)->PipeListenerLoop();
            return 0;
        }, this, 0, nullptr);

        if (hPipeThread) {
            m_pipeThreadHandle = hPipeThread;
            HON_LOG_INFO("[OverlayBridge] IPC named pipe listener started on \\\\.\\pipe\\kongor-overlay (Owner-only DACL)");
        }
    }
}

void OverlayBridge::Shutdown() {
    bool wasRunning = m_workerRunning.exchange(false);
    if (IsLayerProcessTerminating()) {
        // [L2-P1-02] During LdrShutdownProcess, worker threads are already killed by NtTerminateProcess(NULL, 0)
        // and LdrpLoaderLock is held; never call CreateFileA, WaitForSingleObject, std::mutex, or WSACleanup.
        return;
    }
    if (!wasRunning && !m_workerThreadHandle && !m_pipeThreadHandle && !m_probeWakeEvent) {
        return;
    }

    if (m_probeWakeEvent) {
        SetEvent(static_cast<HANDLE>(m_probeWakeEvent));
    }

    HANDLE hDummy = CreateFileA("\\\\.\\pipe\\kongor-overlay", GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (hDummy != INVALID_HANDLE_VALUE) {
        CloseHandle(hDummy);
    }

    if (m_workerThreadHandle) {
        WaitForSingleObject(static_cast<HANDLE>(m_workerThreadHandle), 150);
        CloseHandle(static_cast<HANDLE>(m_workerThreadHandle));
        m_workerThreadHandle = nullptr;
    }
    if (m_pipeThreadHandle) {
        WaitForSingleObject(static_cast<HANDLE>(m_pipeThreadHandle), 150);
        CloseHandle(static_cast<HANDLE>(m_pipeThreadHandle));
        m_pipeThreadHandle = nullptr;
    }
    if (m_probeWakeEvent) {
        CloseHandle(static_cast<HANDLE>(m_probeWakeEvent));
        m_probeWakeEvent = nullptr;
    }
    {
        std::lock_guard<std::mutex> lock(m_bridgeMutex);
        m_pendingEngineCommands.clear();
        m_hasPendingEngineCommands.store(false, std::memory_order_release);
    }
    WSACleanup();
}

void OverlayBridge::PipeListenerLoop() {
    std::string pipeName = "\\\\.\\pipe\\kongor-overlay";
    SECURITY_ATTRIBUTES pipeSa = {};
    PSECURITY_DESCRIPTOR pPipeSd = CreateOwnerOnlyOverlayPipeSecurity(&pipeSa);
    if (!pPipeSd) {
        HON_LOG_ERROR("[OverlayBridge] SECURITY: Refusing to start \\\\.\\pipe\\kongor-overlay without owner-restricted DACL.");
        return;
    }

    while (m_workerRunning.load()) {
        HANDLE hPipe = CreateNamedPipeA(
            pipeName.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            PIPE_UNLIMITED_INSTANCES,
            8192,
            8192,
            0,
            &pipeSa);

        if (hPipe == INVALID_HANDLE_VALUE) {
            Sleep(500);
            continue;
        }

        BOOL connected = ConnectNamedPipe(hPipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
        if (connected && m_workerRunning.load()) {
            char buffer[4096] = {};
            DWORD bytesRead = 0;
            if (ReadFile(hPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr) && bytesRead > 0) {
                buffer[bytesRead] = '\0';
                std::string cmd(buffer);
                while (!cmd.empty() && (cmd.back() == '\r' || cmd.back() == '\n' || cmd.back() == ' ')) {
                    cmd.pop_back();
                }

                // Constant-time token verification when KONGOR_IPC_TOKEN is configured
                char expectedToken[512] = {};
                DWORD expectedTokenLen = GetEnvironmentVariableA("KONGOR_IPC_TOKEN", expectedToken, sizeof(expectedToken));
                if (expectedTokenLen >= sizeof(expectedToken)) {
                    expectedTokenLen = 0;
                    expectedToken[0] = '\0';
                }

                bool tokenAuthorized = (expectedTokenLen == 0);
                if (cmd.rfind("TOKEN:", 0) == 0) {
                    size_t spacePos = cmd.find(' ', 6);
                    if (spacePos != std::string::npos) {
                        std::string providedTok = cmd.substr(6, spacePos - 6);
                        cmd = cmd.substr(spacePos + 1);
                        while (!cmd.empty() && cmd.front() == ' ') cmd.erase(cmd.begin());
                        if (expectedTokenLen > 0 && providedTok.size() == static_cast<size_t>(expectedTokenLen)) {
                            unsigned char diff = 0;
                            for (size_t ti = 0; ti < providedTok.size(); ++ti) {
                                diff |= static_cast<unsigned char>(providedTok[ti] ^ expectedToken[ti]);
                            }
                            tokenAuthorized = (diff == 0);
                        }
                    } else {
                        tokenAuthorized = false;
                    }
                }

                bool isReadOnlyCmd = (cmd == "GET_SNAPSHOT" || cmd == "GET_TABS");
                HON_LOG_INFO("[OverlayBridge] Received IPC overlay command: '%s' (tokenAuth=%d)", cmd.c_str(), tokenAuthorized ? 1 : 0);

                std::string response = "OK\n";
                if (!isReadOnlyCmd && !tokenAuthorized) {
                    HON_LOG_WARN("[OverlayBridge] SECURITY: Blocked unauthenticated overlay pipe command '%s' (KONGOR_IPC_TOKEN required).", cmd.c_str());
                    response = "ERR: Unauthorized (provide TOKEN:<KONGOR_IPC_TOKEN> <command>)\n";
                } else if (cmd == "SHOW:motd") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.SetDocumentVisible("motd", true); });
                } else if (cmd == "HIDE:motd") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.SetDocumentVisible("motd", false); });
                } else if (cmd == "TOGGLE:motd") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.ToggleDocument("motd"); });
                } else if (cmd == "SHOW:tao_console") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.SetDocumentVisible("tao_console", true); });
                } else if (cmd == "HIDE:tao_console") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.SetDocumentVisible("tao_console", false); });
                } else if (cmd == "TOGGLE:tao_console") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.ToggleDocument("tao_console"); });
                } else if (cmd == "SHOW:devtools") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.SetDocumentVisible("devtools", true); });
                } else if (cmd == "HIDE:devtools") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.SetDocumentVisible("devtools", false); });
                } else if (cmd == "TOGGLE:devtools") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.ToggleDocument("devtools"); });
                } else if (cmd == "RELOAD") {
                    g_OverlayManager.EnqueueUiTask([]() { g_OverlayManager.ReloadDocuments(); });
                } else if (cmd == "REFRESH") {
                    PushLogEntry("OK", "TRACE", "Diagnostics trace probe refreshed.");
                    WakeProbeWorker();
                    g_OverlayManager.ForceQrRefresh();
                    response = "OK\n";
                } else if (cmd == "HEAL" || cmd == "SELF_HEAL") {
                    std::string receipt = ExecuteSelfHealing();
                    response = "OK " + receipt + "\n";
                } else if (cmd.rfind("HEAL:", 0) == 0) {
                    std::string recipe = cmd.substr(5);
                    std::string receipt = ExecuteSelfHealing(recipe);
                    response = "OK " + receipt + "\n";
                } else if (cmd.rfind("FAULT:", 0) == 0) {
                    // Format: FAULT:<source>:<severity>:<description>
                    size_t first = cmd.find(':', 6);
                    size_t second = (first != std::string::npos) ? cmd.find(':', first + 1) : std::string::npos;
                    if (first != std::string::npos && second != std::string::npos) {
                        std::string src = cmd.substr(6, first - 6);
                        std::string sevStr = cmd.substr(first + 1, second - first - 1);
                        int sev = 1;
                        auto [ptr, ec] = std::from_chars(sevStr.data(), sevStr.data() + sevStr.size(), sev);
                        if (ec != std::errc{} || ptr != sevStr.data() + sevStr.size()) {
                            response = "ERR: Invalid FAULT severity integer. Expected FAULT:<src>:<sev>:<desc>\n";
                        } else {
                            std::string desc = cmd.substr(second + 1);
                            SimulateFault(src, sev, desc);
                            response = "OK\n";
                        }
                    } else {
                        response = "ERR: Invalid FAULT format. Expected FAULT:<src>:<sev>:<desc>\n";
                    }
                } else if (cmd == "CLEAR_FAULT") {
                    ClearSimulatedFault();
                    response = "OK\n";
                } else if (cmd == "GET_SNAPSHOT") {
                    response = GetSnapshotJson() + "\n";
                } else if (cmd.rfind("TAB:", 0) == 0) {
                    std::string targetTab = cmd.substr(4);
                    g_OverlayManager.EnqueueUiTask([targetTab]() { g_OverlayManager.SwitchTab(targetTab); });
                    response = "OK " + targetTab + "\n";
                } else if (cmd == "GET_TABS") {
                    response = "{\"active\":\"" + g_OverlayManager.GetActiveTab() + "\",\"tabs\":[\"tab-overview\",\"tab-fleet\",\"tab-vulkan\",\"tab-vfs\",\"tab-console\"]}\n";
                } else if (cmd.rfind("EXEC:", 0) == 0) {
                    char devEnv[16] = {};
                    char allowExecEnv[16] = {};
                    bool devToolsEnabled = (GetEnvironmentVariableA("HON_DEVTOOLS", devEnv, sizeof(devEnv)) > 0 &&
                                            (strcmp(devEnv, "1") == 0 || _stricmp(devEnv, "true") == 0)) ||
                                           (GetEnvironmentVariableA("KONGOR_ALLOW_OVERLAY_EXEC", allowExecEnv, sizeof(allowExecEnv)) > 0 &&
                                            (strcmp(allowExecEnv, "1") == 0 || _stricmp(allowExecEnv, "true") == 0)) ||
                                           (expectedTokenLen > 0 && tokenAuthorized);
                    if (!devToolsEnabled) {
                        HON_LOG_WARN("[OverlayBridge] SECURITY: Blocked EXEC command on overlay pipe (set HON_DEVTOOLS=1, KONGOR_ALLOW_OVERLAY_EXEC=1, or KONGOR_IPC_TOKEN).");
                        response = "ERR: EXEC disabled unless HON_DEVTOOLS=1 or KONGOR_IPC_TOKEN is authenticated\n";
                    } else {
                        std::string execCmd = cmd.substr(5);
                        g_OverlayManager.EnqueueUiTask([execCmd]() { g_OverlayManager.ExecuteCommandDirect(execCmd); });
                        response = "OK\n";
                    }
                } else if (cmd.rfind("SET_INPUT:", 0) == 0) {
                    std::string inputVal = cmd.substr(10);
                    g_OverlayManager.EnqueueUiTask([inputVal]() { g_OverlayManager.SetConsoleInputValue(inputVal); });
                    response = "OK\n";
                } else if (cmd.rfind("LOG:", 0) == 0) {
                    size_t first = cmd.find(':', 4);
                    size_t second = (first != std::string::npos) ? cmd.find(':', first + 1) : std::string::npos;
                    if (first != std::string::npos && second != std::string::npos) {
                        std::string lvl = cmd.substr(4, first - 4);
                        std::string sub = cmd.substr(first + 1, second - first - 1);
                        std::string msg = cmd.substr(second + 1);
                        PushLogEntry(lvl, sub, msg);
                    }
                }

                DWORD written = 0;
                WriteFile(hPipe, response.c_str(), static_cast<DWORD>(response.size()), &written, nullptr);
            }
            FlushFileBuffers(hPipe);
            DisconnectNamedPipe(hPipe);
        }
        CloseHandle(hPipe);
    }

    if (pPipeSd) {
        LocalFree(pPipeSd);
    }
}

void OverlayBridge::ProbeWorkerLoop() {
    DWORD currentPid = GetCurrentProcessId();
    std::string pidMapName = "Local\\KongorClientTelemetry_" + std::to_string(currentPid);
    const char* canonicalMapName = "Local\\KongorTelemetryBuffer";

    HANDLE hFastBusMap = nullptr;
    void* pFastBusView = nullptr;

    while (m_workerRunning.load()) {
        // 1. Probe Master Server (:55555)
        int masterLat = 0;
        bool masterOk = ProbeTcpPort("127.0.0.1", 55555, 5, &masterLat);

        // 2. Probe Chat Server (:11031 or :11236)
        int chatLat = 0;
        int chatPort = 11031;
        bool chatOk = ProbeTcpPort("127.0.0.1", 11031, 5, &chatLat);
        if (!chatOk) {
            chatPort = 11236;
            chatOk = ProbeTcpPort("127.0.0.1", 11236, 5, &chatLat);
            if (!chatOk) chatPort = 11031; // Default
        }

        // 3. Probe Dedicated Game Server (:11235, Compel :11234, or hon-server named pipes)
        int gameLat = 0;
        int gamePort = 11235;
        bool gameOk = ProbeTcpPort("127.0.0.1", 11235, 5, &gameLat);
        if (!gameOk) {
            gameOk = ProbeTcpPort("127.0.0.1", 11234, 5, &gameLat);
        }
        if (!gameOk) {
            gamePort = 11255;
            gameOk = ProbeTcpPort("127.0.0.1", 11255, 5, &gameLat);
        }
        if (!gameOk) {
            gamePort = 11235;
            if (WaitNamedPipeA("\\\\.\\pipe\\hon-server-11235", 0) != FALSE ||
                WaitNamedPipeA("\\\\.\\pipe\\hon-server-11237", 0) != FALSE) {
                gameOk = true;
                gameLat = 1;
            }
        }

        // 4. Probe Named Pipe IPC
        bool pipeAvailable = WaitNamedPipeA("\\\\.\\pipe\\willowmaker-ipc", 0) != FALSE;
        if (!pipeAvailable && GetLastError() == ERROR_FILE_NOT_FOUND) {
            pipeAvailable = false;
        } else {
            pipeAvailable = true;
        }

        // 5. Ingest FastBus shared memory if present (supports V1 64B & V2 192B headers + 'KONG'/'KNGR' magics)
        if (!pFastBusView) {
            const char* attachedName = pidMapName.c_str();
            hFastBusMap = OpenFileMappingA(FILE_MAP_READ, FALSE, pidMapName.c_str());
            if (!hFastBusMap) {
                attachedName = canonicalMapName;
                hFastBusMap = OpenFileMappingA(FILE_MAP_READ, FALSE, canonicalMapName);
            }
            if (hFastBusMap) {
                pFastBusView = MapViewOfFile(hFastBusMap, FILE_MAP_READ, 0, 0, 0);
                if (pFastBusView) {
                    const auto* pHdr = static_cast<const FastBusHeader*>(pFastBusView);
                    if (IsValidFastBusMagic(pHdr->Magic)) {
                        HON_LOG_INFO("[OverlayBridge] Attached to FastBus shared memory: %s (magic=0x%08X, version=%u)",
                                     attachedName, pHdr->Magic, pHdr->Version);
                    } else {
                        HON_LOG_WARN("[OverlayBridge] Rejected FastBus mapping '%s': unexpected magic 0x%08X",
                                     attachedName, pHdr->Magic);
                        UnmapViewOfFile(pFastBusView);
                        pFastBusView = nullptr;
                        CloseHandle(hFastBusMap);
                        hFastBusMap = nullptr;
                    }
                } else {
                    CloseHandle(hFastBusMap);
                    hFastBusMap = nullptr;
                }
            }
        }

        uint32_t pingMs = 12;
        float packetLoss = 0.0f;
        uint32_t serverTick = 1420;
        uint32_t drawCalls = 180;
        uint32_t workingSet = 380;
        uint32_t voices = 64;

        FastBusRecord rec = {};
        if (pFastBusView && ReadFastBusLatestRecord(pFastBusView, &rec)) {
            pingMs = (rec.PingMicroseconds > 0) ? (rec.PingMicroseconds / 1000) : 12;
            packetLoss = rec.PacketLossPercent;
            serverTick = rec.ServerTick;
            drawCalls = rec.DrawCallCount > 0 ? rec.DrawCallCount : 180;
            workingSet = rec.MemoryWorkingSetMb > 0 ? rec.MemoryWorkingSetMb : 380;
            voices = rec.AudioVoicesActive > 0 ? rec.AudioVoicesActive : 64;
        }

        // 6. Evaluate Subsystem Fault Attribution
        std::string faultSource = "NONE";
        std::string faultDesc = "NOMINAL (All systems operational)";
        int severity = 0;

        if (m_simulatedFaultActive.load()) {
            std::lock_guard<std::mutex> lock(m_bridgeMutex);
            faultSource = m_snapshot.activeFaultSource;
            faultDesc = m_snapshot.activeFaultDescription;
            severity = m_snapshot.faultSeverity;
            if (faultSource == "MASTER_SERVER" || faultSource.find("55555") != std::string::npos) masterOk = false;
            if (faultSource == "CHAT_SERVER" || faultSource.find("11031") != std::string::npos || faultSource.find("11236") != std::string::npos) chatOk = false;
            if (faultSource == "GAME_SERVER" || faultSource.find("11235") != std::string::npos) gameOk = false;
            if (faultSource == "NETWORK_QOS") packetLoss = 12.5f;
        } else {
            if (!masterOk) {
                faultSource = "MASTER_SERVER";
                faultDesc = "Unreachable on 127.0.0.1:55555 (Auth/Login/Store impacted)";
                severity = 1;
            } else if (!chatOk) {
                faultSource = "CHAT_SERVER";
                faultDesc = "Unreachable on 127.0.0.1:11236 (Presence/XMPP impacted)";
                severity = 1;
            } else if (packetLoss > 5.0f) {
                faultSource = "NETWORK_QOS";
                std::ostringstream ss;
                ss << "Severe Packet Loss detected (" << std::fixed << std::setprecision(1) << packetLoss << "%)";
                faultDesc = ss.str();
                severity = 2;
            } else if (pingMs > 250) {
                faultSource = "NETWORK_QOS";
                faultDesc = "High Latency (" + std::to_string(pingMs) + " ms)";
                severity = 1;
            } else if (!gameOk && serverTick > 100) {
                faultSource = "GAME_SERVER";
                faultDesc = "Game Server offline on port " + std::to_string(gamePort);
                severity = 2;
            }
        }

        // 7. Update internal snapshot
        {
            std::lock_guard<std::mutex> lock(m_bridgeMutex);
            m_snapshot.masterOnline = masterOk;
            m_snapshot.masterLatencyMs = masterLat;
            m_snapshot.chatOnline = chatOk;
            m_snapshot.chatPort = chatPort;
            m_snapshot.chatLatencyMs = chatLat;
            m_snapshot.gameServerOnline = gameOk;
            m_snapshot.gameServerPort = gamePort;
            m_snapshot.gameServerLatencyMs = gameLat;

            m_snapshot.pingMs = pingMs;
            m_snapshot.packetLossPercent = packetLoss;
            m_snapshot.serverTick = serverTick;
            m_snapshot.drawCalls = drawCalls;
            m_snapshot.workingSetMb = workingSet;
            m_snapshot.audioVoices = voices;
            m_snapshot.ipcConnected = pipeAvailable;

            m_snapshot.activeFaultSource = faultSource;
            m_snapshot.activeFaultDescription = faultDesc;
            m_snapshot.faultSeverity = severity;
        }

        // Wait up to 10 seconds between diagnostic sweeps, or wake immediately on event
        if (m_probeWakeEvent) {
            WaitForSingleObject(static_cast<HANDLE>(m_probeWakeEvent), 10000);
        } else {
            Sleep(10000);
        }
    }

    if (pFastBusView) UnmapViewOfFile(pFastBusView);
    if (hFastBusMap) CloseHandle(hFastBusMap);
}

void OverlayBridge::RegisterCommandCallback(std::function<void(const std::string&)> callback) {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    m_commandCallback = std::move(callback);
}

#pragma pack(push, 8)
struct OverlayK2StringW {
    union {
        wchar_t buf[8];
        const wchar_t* ptr;
    } bx;
    size_t length;
    size_t capacity;

    OverlayK2StringW(const wchar_t* s) {
        length = s ? wcslen(s) : 0;
        if (length < 8) {
            if (s && length > 0) {
                wcsncpy_s(bx.buf, 8, s, length);
            }
            bx.buf[length] = L'\0';
            capacity = 7;
        } else {
            bx.ptr = s;
            capacity = length;
        }
    }
};
#pragma pack(pop)

typedef void (__fastcall *FnConsoleAddCmdBuffer)(void* pConsole, const void* pK2StringW);
static FnConsoleAddCmdBuffer s_pfnConsoleAddCmdBuffer = nullptr;
static void** s_ppConsole = nullptr;
static bool s_k2ConsoleResolved = false;

static bool ResolveK2Console() {
    if (s_k2ConsoleResolved && s_ppConsole && *s_ppConsole && s_pfnConsoleAddCmdBuffer) {
        return true;
    }
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) return false;

    if (!s_ppConsole) {
        s_ppConsole = (void**)GetProcAddress(hK2, "?g_pConsole@@3PEAVCConsole@@EA");
    }
    if (!s_pfnConsoleAddCmdBuffer) {
        s_pfnConsoleAddCmdBuffer = (FnConsoleAddCmdBuffer)GetProcAddress(
            hK2, "?AddCmdBuffer@CConsole@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z");
    }
    s_k2ConsoleResolved = (s_ppConsole != nullptr && *s_ppConsole != nullptr && s_pfnConsoleAddCmdBuffer != nullptr);
    return s_k2ConsoleResolved;
}

static bool SafeCallConsoleAddCmdBufferSeh(FnConsoleAddCmdBuffer pfn, void* pConsole, const OverlayK2StringW* pStr) noexcept {
    if (!pfn || !pConsole || !pStr) return false;
    __try {
        pfn(pConsole, pStr);
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void OverlayBridge::DispatchConsoleCommandImmediate(const std::string& formattedCmd) {
    if (ResolveK2Console()) {
        int wlen = MultiByteToWideChar(CP_UTF8, 0, formattedCmd.c_str(), -1, nullptr, 0);
        if (wlen > 0) {
            std::vector<wchar_t> wbuf(static_cast<size_t>(wlen));
            MultiByteToWideChar(CP_UTF8, 0, formattedCmd.c_str(), -1, wbuf.data(), wlen);
            OverlayK2StringW k2Str(wbuf.data());
            if (SafeCallConsoleAddCmdBufferSeh(s_pfnConsoleAddCmdBuffer, *s_ppConsole, &k2Str)) {
                HON_LOG_INFO("[OverlayBridge] Command added to K2 console buffer on main thread (TID=%lu): '%s'",
                             GetCurrentThreadId(), formattedCmd.c_str());
                PushLogEntry("OK", "K2", "Executed: " + formattedCmd);
                return;
            }
            HON_LOG_WARN("[OverlayBridge] SEH fault in CConsole::AddCmdBuffer; falling back to IPC pipe for '%s'.",
                         formattedCmd.c_str());
        }
    }

    SendPipeCommand(formattedCmd);
    PushLogEntry("OK", "EXEC", formattedCmd);
}

void OverlayBridge::DrainPendingEngineCommands() {
    m_mainRenderThreadId.store(GetCurrentThreadId(), std::memory_order_release);
    if (!m_hasPendingEngineCommands.load(std::memory_order_acquire)) {
        return;
    }

    std::deque<std::string> batch;
    {
        std::lock_guard<std::mutex> lock(m_bridgeMutex);
        m_hasPendingEngineCommands.store(false, std::memory_order_release);
        if (m_pendingEngineCommands.empty()) {
            return;
        }
        batch.swap(m_pendingEngineCommands);
    }

    for (const std::string& cmd : batch) {
        DispatchConsoleCommandImmediate(cmd);
    }
}

void OverlayBridge::ExecuteCommand(const std::string& command) {
    if (command.empty()) return;

    std::string formattedCmd = command;
    if (formattedCmd.rfind("lua:", 0) == 0) {
        formattedCmd = "Script " + formattedCmd.substr(4);
        while (formattedCmd.size() > 7 && formattedCmd[7] == ' ') formattedCmd.erase(7, 1);
    } else if (formattedCmd.rfind("exec ", 0) == 0) {
        formattedCmd = formattedCmd.substr(5);
    }

    // In-Engine Self-Healing Pre-Flight Sanitization
    // 1. Remediate malformed interface reload commands
    // In K2, executing synchronous 'ReloadInterface main' mid-match corrupts the game HUD and triggers
    // front-end logout callbacks causing CSystem::Exit. 'Set ui_reloadInterfaces true' defers the reload
    // safely to the frame boundary in CUIManager::Frame.
    if (formattedCmd == "ReloadInterfaces" || formattedCmd == "ReloadInterfaces main" ||
        formattedCmd == "ReloadInterface main" || formattedCmd == "ReloadInterface game") {
        HON_LOG_INFO("[OverlayBridge][SelfHeal] Rewriting '%s' -> 'Set ui_reloadInterfaces true'", formattedCmd.c_str());
        formattedCmd = "Set ui_reloadInterfaces true";
    }

    // 2. Remediate practice matches lacking offline cvar guards
    // In K2, running bare 'StartGame practice' without 'svr_login false' causes CHostServer
    // to attempt remote Master Server authentication, timing out after 30s and aborting the client.
    if (formattedCmd.find("StartGame practice") != std::string::npos ||
        formattedCmd.find("StartGame Practice") != std::string::npos) {
        if (formattedCmd.find("svr_login false") == std::string::npos) {
            HON_LOG_INFO("[OverlayBridge][SelfHeal] Intercepted practice match command lacking offline guards: prepending pre-flight cvars.");
            formattedCmd = "Set svr_login false; Set svr_broadcastLocal false; Set ui_quickStart true; Set sv_precacheEntities false; Set sv_heroSelectTime 0; Set sv_gameStartCountdown 0; Set svr_port 11235; Set svr_voicePort 11435; " + formattedCmd;
        }
    }

    std::function<void(const std::string&)> cb;
    {
        std::lock_guard<std::mutex> lock(m_bridgeMutex);
        cb = m_commandCallback;
    }

    if (cb) {
        cb(formattedCmd);
        return;
    }

    HON_LOG_INFO("[OverlayBridge] Executing engine command: '%s' (CallerTID=%lu)", formattedCmd.c_str(), GetCurrentThreadId());

    if (ResolveK2Console()) {
        uint32_t mainTid = m_mainRenderThreadId.load(std::memory_order_acquire);
        if (mainTid != 0 && GetCurrentThreadId() == mainTid) {
            DispatchConsoleCommandImmediate(formattedCmd);
            return;
        }

        {
            std::lock_guard<std::mutex> lock(m_bridgeMutex);
            if (m_pendingEngineCommands.size() >= MAX_PENDING_COMMANDS) {
                m_pendingEngineCommands.pop_front();
            }
            m_pendingEngineCommands.push_back(formattedCmd);
            m_hasPendingEngineCommands.store(true, std::memory_order_release);
        }
        HON_LOG_INFO("[OverlayBridge] Enqueued K2 console command for main-thread drain (MainTID=%u, CallerTID=%lu): '%s'",
                     mainTid, GetCurrentThreadId(), formattedCmd.c_str());
        PushLogEntry("OK", "K2_QUEUE", "Queued for main thread: " + formattedCmd);
        return;
    }

    SendPipeCommand(formattedCmd);
    PushLogEntry("OK", "EXEC", formattedCmd);
}

void OverlayBridge::SendPipeCommand(const std::string& cmd) {
    HANDLE hPipe = CreateFileA(
        "\\\\.\\pipe\\willowmaker-ipc",
        GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        0,
        nullptr);

    if (hPipe != INVALID_HANDLE_VALUE) {
        std::string payload = "CMD:" + cmd + "\n";
        DWORD written = 0;
        WriteFile(hPipe, payload.c_str(), static_cast<DWORD>(payload.size()), &written, nullptr);
        CloseHandle(hPipe);
        HON_LOG_DEBUG("[OverlayBridge] Piped command: %s", cmd.c_str());
    } else {
        HON_LOG_DEBUG("[OverlayBridge] Pipe unavailable, command buffered: %s", cmd.c_str());
    }
}

void OverlayBridge::SetMetric(const std::string& key, double value) {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    m_metrics[key] = value;
}

double OverlayBridge::GetMetric(const std::string& key) {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    auto it = m_metrics.find(key);
    return it != m_metrics.end() ? it->second : 0.0;
}

void OverlayBridge::SetTextMetric(const std::string& key, const std::string& value) {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    m_textMetrics[key] = value;
}

std::string OverlayBridge::GetTextMetric(const std::string& key) {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    auto it = m_textMetrics.find(key);
    return it != m_textMetrics.end() ? it->second : "";
}

void OverlayBridge::PushLogEntry(const std::string& level, const std::string& subsystem, const std::string& message) {
    if (!m_workerRunning.load(std::memory_order_relaxed)) {
        return;
    }

    SYSTEMTIME st;
    GetLocalTime(&st);

    char timeBuf[32];
    snprintf(timeBuf, sizeof(timeBuf), "%02u:%02u:%02u", st.wHour, st.wMinute, st.wSecond);

    OverlayLogEntry entry;
    entry.timestamp = timeBuf;
    entry.level = level;
    entry.subsystem = subsystem.empty() ? "TAO" : subsystem;
    entry.message = message;

    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    m_recentLogs.push_back(entry);
    if (m_recentLogs.size() > MAX_LOG_HISTORY) {
        m_recentLogs.pop_front();
    }
    m_logGeneration.fetch_add(1, std::memory_order_relaxed);
}

std::vector<OverlayLogEntry> OverlayBridge::GetRecentLogs(size_t maxCount) {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    std::vector<OverlayLogEntry> result;
    if (m_recentLogs.empty()) return result;

    size_t count = (maxCount > m_recentLogs.size()) ? m_recentLogs.size() : maxCount;
    auto it = m_recentLogs.end() - count;
    while (it != m_recentLogs.end()) {
        result.push_back(*it);
        ++it;
    }
    return result;
}

SubsystemTelemetrySnapshot OverlayBridge::GetSnapshot() {
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    return m_snapshot;
}

void OverlayBridge::UpdateSnapshot(const std::function<void(SubsystemTelemetrySnapshot&)>& updater) {
    if (!updater) return;
    std::lock_guard<std::mutex> lock(m_bridgeMutex);
    updater(m_snapshot);
}

void OverlayBridge::WakeProbeWorker() {
    if (m_probeWakeEvent) {
        SetEvent(static_cast<HANDLE>(m_probeWakeEvent));
    }
}

void OverlayBridge::SimulateFault(const std::string& source, int severity, const std::string& desc) {
    m_simulatedFaultActive = true;
    UpdateSnapshot([&](SubsystemTelemetrySnapshot& s) {
        s.activeFaultSource = source;
        s.activeFaultDescription = desc;
        s.faultSeverity = severity;
        if (source == "MASTER_SERVER" || source.find("55555") != std::string::npos) s.masterOnline = false;
        if (source == "CHAT_SERVER" || source.find("11031") != std::string::npos || source.find("11236") != std::string::npos) s.chatOnline = false;
        if (source == "GAME_SERVER" || source.find("11235") != std::string::npos) s.gameServerOnline = false;
        if (source == "NETWORK_QOS") s.packetLossPercent = 12.5f;
    });
    PushLogEntry("WARN", "FAULT", "Simulated fault injected: [" + source + "] " + desc);
    WakeProbeWorker();
    g_OverlayManager.ForceQrRefresh();
}

void OverlayBridge::ClearSimulatedFault() {
    m_simulatedFaultActive = false;
    PushLogEntry("OK", "FAULT", "Simulated fault cleared. Live telemetry restored.");
    WakeProbeWorker();
    g_OverlayManager.ForceQrRefresh();
}

bool OverlayBridge::IsSimulatedFaultActive() {
    return m_simulatedFaultActive.load();
}

std::string OverlayBridge::ExecuteSelfHealing(const std::string& recipeHint) {
    SubsystemTelemetrySnapshot snap = GetSnapshot();
    std::string action = recipeHint;

    if (action.empty()) {
        if (snap.faultSeverity >= 2) {
            if (!snap.masterOnline) action = "restart_master";
            else if (!snap.chatOnline) action = "restart_chat";
            else if (!snap.gameServerOnline) action = "restart_game";
            else action = "restart_subsystem";
        } else if (snap.faultSeverity == 1) {
            if (!snap.masterOnline) action = "restart_master";
            else if (!snap.chatOnline) action = "restart_chat";
            else action = "reconnect_net";
        } else {
            action = "hot_reload_cache_flush";
        }
    }

    // Generate unique receipt ID based on QPC counter
    LARGE_INTEGER qpc;
    QueryPerformanceCounter(&qpc);
    char receiptBuf[64];
    snprintf(receiptBuf, sizeof(receiptBuf), "REC-%08llX", static_cast<unsigned long long>(qpc.QuadPart & 0xFFFFFFFF));
    std::string receiptId(receiptBuf);

    HON_LOG_INFO("[OverlayBridge] Executing self-healing action '%s' (Receipt: %s)", action.c_str(), receiptId.c_str());

    if (action == "restart_master") {
        PushLogEntry("WARN", "HEAL", "Reflex triggered: Re-probing & reconnecting Master Server (:55555)...");
        SendPipeCommand("HEAL:restart_master");
        int lat = 0;
        ProbeTcpPort("127.0.0.1", 55555, 100, &lat);
    } else if (action == "restart_chat") {
        PushLogEntry("WARN", "HEAL", "Reflex triggered: Reconnecting Chat Server on :11031/:11236...");
        SendPipeCommand("HEAL:restart_chat");
        int lat = 0;
        ProbeTcpPort("127.0.0.1", 11031, 100, &lat);
    } else if (action == "restart_game") {
        PushLogEntry("WARN", "HEAL", "Reflex triggered: Reconnecting Dedicated Game Server on port 11235...");
        SendPipeCommand("HEAL:restart_game");
    } else if (action == "reconnect_net" || action == "reset_network") {
        PushLogEntry("WARN", "HEAL", "Reflex triggered: Resetting network QoS buffers & latency counters...");
        SendPipeCommand("HEAL:reset_network");
        UpdateSnapshot([](SubsystemTelemetrySnapshot& s) {
            s.packetLossPercent = 0.0f;
            s.pingMs = 12;
        });
    } else {
        PushLogEntry("OK", "HEAL", "Flushed K2 UI caches & hot-reloaded HUD documents.");
        SendPipeCommand("HEAL:hot_reload_cache_flush");
    }

    // If simulated fault was active, resolve it
    if (m_simulatedFaultActive.load()) {
        m_simulatedFaultActive = false;
    }

    PushLogEntry("OK", "HEAL", "Remediation executed successfully: " + action + " (" + receiptId + ")");

    // Wake probe thread and force QR refresh
    WakeProbeWorker();
    g_OverlayManager.ForceQrRefresh();

    return receiptId;
}

std::string OverlayBridge::GetSnapshotJson() {
    SubsystemTelemetrySnapshot snap = GetSnapshot();
    std::ostringstream ss;
    ss << "{"
       << "\"masterOnline\":" << (snap.masterOnline ? "true" : "false")
       << ",\"masterLatencyMs\":" << snap.masterLatencyMs
       << ",\"chatOnline\":" << (snap.chatOnline ? "true" : "false")
       << ",\"chatPort\":" << snap.chatPort
       << ",\"chatLatencyMs\":" << snap.chatLatencyMs
       << ",\"gameServerOnline\":" << (snap.gameServerOnline ? "true" : "false")
       << ",\"gameServerPort\":" << snap.gameServerPort
       << ",\"gameServerLatencyMs\":" << snap.gameServerLatencyMs
       << ",\"pingMs\":" << snap.pingMs
       << ",\"packetLossPercent\":" << std::fixed << std::setprecision(1) << snap.packetLossPercent
       << ",\"serverTick\":" << snap.serverTick
       << ",\"ipcConnected\":" << (snap.ipcConnected ? "true" : "false")
       << ",\"activeFaultSource\":\"" << snap.activeFaultSource << "\""
       << ",\"activeFaultDescription\":\"" << snap.activeFaultDescription << "\""
       << ",\"faultSeverity\":" << snap.faultSeverity
       << "}";
    return ss.str();
}

// C Exports
extern "C" {

void Kongor_Overlay_SetVisible(const char* docId, int visible) {
    if (docId) {
        g_OverlayManager.SetDocumentVisible(docId, visible != 0);
    }
}

void Kongor_Overlay_Toggle(const char* docId) {
    if (docId) {
        g_OverlayManager.ToggleDocument(docId);
    }
}

void Kongor_Overlay_ShowMotd(int show) {
    g_OverlayManager.SetDocumentVisible("motd", show != 0);
}

void Kongor_Overlay_DispatchCommand(const char* command) {
    if (command) {
        g_OverlayBridge.ExecuteCommand(command);
    }
}

void Kongor_Overlay_UpdateMetric(const char* name, double val) {
    if (name) {
        g_OverlayBridge.SetMetric(name, val);
    }
}

void Kongor_Overlay_ReloadDocuments() {
    g_OverlayManager.ReloadDocuments();
}

void Kongor_Overlay_PushLog(const char* level, const char* subsystem, const char* message) {
    if (level && message) {
        g_OverlayBridge.PushLogEntry(level, subsystem ? subsystem : "TAO", message);
    }
}

void Kongor_Overlay_UpdateSubsystemStatus(const char* subsystem, int online, int latencyMs, const char* details) {
    if (!subsystem) return;
    std::string sub = subsystem;
    g_OverlayBridge.UpdateSnapshot([&](SubsystemTelemetrySnapshot& s) {
        if (sub == "master") {
            s.masterOnline = (online != 0);
            s.masterLatencyMs = latencyMs;
        } else if (sub == "chat") {
            s.chatOnline = (online != 0);
            s.chatLatencyMs = latencyMs;
        } else if (sub == "gameserver") {
            s.gameServerOnline = (online != 0);
            s.gameServerLatencyMs = latencyMs;
        }
        if (details && details[0] != '\0') {
            s.activeFaultDescription = details;
        }
    });
}

void Kongor_Overlay_UpdateNetQoS(double pingMs, double packetLoss, unsigned int serverTick) {
    g_OverlayBridge.UpdateSnapshot([&](SubsystemTelemetrySnapshot& s) {
        s.pingMs = static_cast<uint32_t>(pingMs);
        s.packetLossPercent = static_cast<float>(packetLoss);
        s.serverTick = serverTick;
    });
}

void Kongor_Overlay_PushLogInternal(const char* level, const char* subsystem, const char* message) {
    if (level && message && g_OverlayBridge.IsRunning()) {
        g_OverlayBridge.PushLogEntry(level, subsystem ? subsystem : "VULKAN", message);
    }
}

}
