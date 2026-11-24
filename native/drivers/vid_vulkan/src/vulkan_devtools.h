// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#ifndef VK_USE_PLATFORM_WIN32_KHR
#define VK_USE_PLATFORM_WIN32_KHR
#endif
#include <windows.h>
#include <cstdint>
#include <string>
#include <vector>
#include <mutex>
#include "kongor_shared_mem.h"

enum class DevToolsTab : uint8_t {
    Hierarchy = 0,
    LuaConsole = 1,
    Telemetry = 2,
    VfsDiagnostics = 3,
    TriageActions = 4,
    Count = 5
};

struct DevToolsWidgetNode {
    std::string name;
    std::string type;
    int x, y, width, height;
    bool visible;
    std::string texture;
    std::string info;
};

class VulkanDevTools {
public:
    static VulkanDevTools& Get();

    bool Initialize(HWND hWnd);
    void Shutdown();

    // Hotkey & Input Arbitration (Ctrl+Shift+D exclusively; no F12)
    bool HandleMessage(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam);
    bool IsVisible() const { return m_bVisible; }
    void SetVisible(bool visible);
    void ToggleVisible() { SetVisible(!m_bVisible); }

    // Fast Bus Telemetry Synchronization
    bool ConnectFastBus();
    bool PollFastBus(FrameTelemetryRecord* outRecord);

    DevToolsTab GetActiveTab() const { return m_activeTab; }
    void SetActiveTab(DevToolsTab tab) { m_activeTab = tab; }

    // Rendering pipeline (invoked inside Vulkan Hook_EndFrame)
    void Render(uint32_t viewportWidth, uint32_t viewportHeight,
                double instantFps, double frameDeltaMs, uint64_t frameCount,
                uint64_t rects, uint64_t quads, uint64_t tris, uint64_t lines);

    // Lua REPL Execution
    void ExecuteLuaCommand(const std::string& command);
    const std::vector<std::string>& GetConsoleLog() const { return m_consoleLog; }
    const std::string& GetCurrentInput() const { return m_currentInput; }

    // Live Widget DOM Registration
    void RegisterWidget(const DevToolsWidgetNode& node);
    void ClearWidgets();

    // DevOverlay & Triage Integration
    bool SetClipboardText(const std::string& text);
    void TriggerTriageAction(int actionIndex);
    const std::string& GetStatusFeedback() const { return m_statusFeedback; }
    void SetStatusFeedback(const std::string& feedback) { m_statusFeedback = feedback; }

private:
    VulkanDevTools();
    ~VulkanDevTools();

    void DrawString(float x, float y, const char* str, uint32_t color, float scale = 1.0f);
    void DrawPanel(float x, float y, float w, float h, uint32_t bgColor, uint32_t borderColor = 0);
    void DrawButton(float x, float y, float w, float h, const char* label, bool active, bool hovered);

    void RenderHeader(float x, float y, float w, double fps, double frameDeltaMs);
    void RenderTabBar(float x, float y, float w);
    void RenderHierarchyTab(float x, float y, float w, float h);
    void RenderLuaConsoleTab(float x, float y, float w, float h);
    void RenderTelemetryTab(float x, float y, float w, float h,
                            double instantFps, double frameDeltaMs, uint64_t frameCount,
                            uint64_t rects, uint64_t quads, uint64_t tris, uint64_t lines);
    void RenderVfsTab(float x, float y, float w, float h);
    void RenderTriageActionsTab(float x, float y, float w, float h, double instantFps);

    bool m_bInitialized = false;
    bool m_bVisible = false;
    HWND m_hWnd = nullptr;
    DevToolsTab m_activeTab = DevToolsTab::Telemetry;

    int m_mouseX = 0;
    int m_mouseY = 0;
    bool m_mouseDown = false;

    // Window layout
    float m_winX = 40.0f;
    float m_winY = 40.0f;
    float m_winW = 960.0f;
    float m_winH = 640.0f;

    // Lua REPL Console State
    std::string m_currentInput;
    std::vector<std::string> m_consoleLog;
    std::vector<std::string> m_commandHistory;
    size_t m_historyIndex = 0;
    std::recursive_mutex m_stateMutex;

    // DOM Hierarchy State
    std::vector<DevToolsWidgetNode> m_widgets;
    int m_selectedWidgetIndex = -1;

    // DevOverlay / Triage Integration State
    std::string m_statusFeedback = "Ready (Ctrl+Shift+D to toggle)";
    int m_matchId = 99420;
    int m_serverPort = 11235;
    std::string m_mapName = "caldavar";
    double m_lastFps = 144.0;

    // Fast Bus Shared Memory State
    KongorSharedMemHandle m_shmHandle{};
    bool m_shmConnected = false;
};

