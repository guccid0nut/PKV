// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: overlay_bridge.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - In-Process Command & Telemetry Bridge
// ============================================================================

#pragma once

#include <string>
#include <vector>
#include <deque>
#include <unordered_map>
#include <functional>
#include <mutex>
#include <atomic>
#include <cstdint>

struct SubsystemTelemetrySnapshot {
    // Service availability & latency
    bool masterOnline = true;
    int masterLatencyMs = 1;
    bool chatOnline = true;
    int chatPort = 11031;
    int chatLatencyMs = 2;
    bool gameServerOnline = true;
    int gameServerPort = 11235;
    int gameServerLatencyMs = 3;

    // Network QoS
    uint32_t pingMs = 12;
    float packetLossPercent = 0.0f;
    uint32_t serverTick = 1420;
    uint64_t clientFrame = 0;

    // Engine & hardware
    double fps = 60.0;
    double frametimeMs = 16.6;
    uint32_t workingSetMb = 380;
    uint32_t drawCalls = 180;
    uint32_t audioVoices = 64;
    uint32_t vfsLooseFiles = 0;
    bool ipcConnected = true;

    // Active Fault Attribution
    std::string activeFaultSource = "NONE";
    std::string activeFaultDescription = "NOMINAL (All systems operational)";
    int faultSeverity = 0; // 0 = Nominal, 1 = Warning, 2 = Critical
};

struct OverlayLogEntry {
    std::string timestamp;
    std::string level; // INFO, OK, WARN, ERROR
    std::string subsystem; // TAO, VULKAN, CEF, MASTER, CHAT, SERVER, NET, LUA
    std::string message;
};

class OverlayBridge {
public:
    OverlayBridge();
    ~OverlayBridge();

    void Initialize();
    void Shutdown();
    void ExecuteCommand(const std::string& command);
    void DrainPendingEngineCommands();

    void SetMetric(const std::string& key, double value);
    double GetMetric(const std::string& key);

    void SetTextMetric(const std::string& key, const std::string& value);
    std::string GetTextMetric(const std::string& key);

    void PushLogEntry(const std::string& level, const std::string& subsystem, const std::string& message);
    std::vector<OverlayLogEntry> GetRecentLogs(size_t maxCount = 8);
    uint64_t GetLogGeneration() const noexcept { return m_logGeneration.load(std::memory_order_relaxed); }

    SubsystemTelemetrySnapshot GetSnapshot();
    void UpdateSnapshot(const std::function<void(SubsystemTelemetrySnapshot&)>& updater);

    void RegisterCommandCallback(std::function<void(const std::string&)> callback);

    void WakeProbeWorker();
    void SimulateFault(const std::string& source, int severity, const std::string& desc);
    void ClearSimulatedFault();
    bool IsSimulatedFaultActive();
    bool IsRunning() const { return m_workerRunning.load(std::memory_order_relaxed); }
    std::string ExecuteSelfHealing(const std::string& recipeHint = "");
    std::string GetSnapshotJson();

private:
    void DispatchConsoleCommandImmediate(const std::string& formattedCmd);
    void SendPipeCommand(const std::string& cmd);
    void ProbeWorkerLoop();
    void PipeListenerLoop();

private:
    std::mutex m_bridgeMutex;
    std::unordered_map<std::string, double> m_metrics;
    std::unordered_map<std::string, std::string> m_textMetrics;
    std::function<void(const std::string&)> m_commandCallback;
    std::deque<std::string> m_pendingEngineCommands;
    std::atomic<uint32_t> m_mainRenderThreadId{ 0 };
    std::atomic<bool> m_hasPendingEngineCommands{ false };
    std::atomic<uint64_t> m_logGeneration{ 0 };
    static constexpr size_t MAX_PENDING_COMMANDS = 128;

    SubsystemTelemetrySnapshot m_snapshot;
    std::deque<OverlayLogEntry> m_recentLogs;
    static constexpr size_t MAX_LOG_HISTORY = 64;

    std::atomic<bool> m_workerRunning{ false };
    std::atomic<bool> m_simulatedFaultActive{ false };
    void* m_workerThreadHandle = nullptr;
    void* m_pipeThreadHandle = nullptr;
    void* m_probeWakeEvent = nullptr;
};

extern OverlayBridge g_OverlayBridge;

#ifdef __cplusplus
extern "C" {
#endif

__declspec(dllexport) void Kongor_Overlay_SetVisible(const char* docId, int visible);
__declspec(dllexport) void Kongor_Overlay_Toggle(const char* docId);
__declspec(dllexport) void Kongor_Overlay_ShowMotd(int show);
__declspec(dllexport) void Kongor_Overlay_DispatchCommand(const char* command);
__declspec(dllexport) void Kongor_Overlay_UpdateMetric(const char* name, double val);
__declspec(dllexport) void Kongor_Overlay_ReloadDocuments();
__declspec(dllexport) void Kongor_Overlay_PushLog(const char* level, const char* subsystem, const char* message);
__declspec(dllexport) void Kongor_Overlay_UpdateSubsystemStatus(const char* subsystem, int online, int latencyMs, const char* details);
__declspec(dllexport) void Kongor_Overlay_UpdateNetQoS(double pingMs, double packetLoss, unsigned int serverTick);

// Internal C linkage helper for logging without including C++ headers
void Kongor_Overlay_PushLogInternal(const char* level, const char* subsystem, const char* message);

#ifdef __cplusplus
}
#endif
