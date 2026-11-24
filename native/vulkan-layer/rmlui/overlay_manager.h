// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: overlay_manager.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - Overlay Lifecycle Manager
// ============================================================================

#pragma once

#include <vulkan/vulkan.h>
#include <RmlUi/Core/Context.h>
#include <RmlUi/Core/ElementDocument.h>
#include <atomic>
#include <deque>
#include <functional>
#include <vector>
#include <string>
#include <unordered_map>
#include <mutex>
#include <memory>

#include "rmlui_system_vk.h"
#include "rmlui_render_vk.h"
#include "font_engine_stb.h"
#include "overlay_input.h"
#include "overlay_bridge.h"

class OverlayManager {
public:
    OverlayManager();
    ~OverlayManager();

    bool Initialize(
        VkDevice device,
        VkPhysicalDevice physicalDevice,
        VkQueue queue,
        uint32_t queueFamilyIndex,
        VkFormat format,
        uint32_t width,
        uint32_t height,
        const LayerDeviceDispatchTable& devDispatch,
        const LayerInstanceDispatchTable& instDispatch);

    void SetSwapchainImages(const std::vector<VkImage>& images);
    void OnResize(uint32_t width, uint32_t height);
    void ReleaseSwapchainResources();
    void Cleanup();

    bool UpdateAndRender(
        VkQueue queue,
        uint32_t imageIndex,
        uint32_t waitSemaphoreCount,
        const VkSemaphore* pWaitSemaphores,
        VkSemaphore* pOutWaitSemaphore);

    void LoadDocuments();
    void ReloadDocuments();
    void SetDocumentVisible(const std::string& docId, bool visible);
    void ToggleDocument(const std::string& docId);
    bool IsDocumentVisible(const std::string& docId);

    bool IsEnabled() const noexcept { return m_isEnabled.load(std::memory_order_relaxed); }
    void SetEnabled(bool enabled) noexcept { m_isEnabled.store(enabled, std::memory_order_relaxed); }
    bool IsInitialized() const noexcept { return m_isInitialized.load(std::memory_order_acquire); }
    VkDevice GetDevice() const noexcept { return m_device; }
    std::recursive_mutex& GetMutex() { return m_managerMutex; }

    Rml::Context* GetContext() { return m_context; }
    RmlUi_RenderInterface_VK& GetRenderInterface() { return m_renderInterface; }

    HWND FindGameWindow();
    void RefreshInputHook();
    bool ShouldAttemptInputHook() const noexcept {
        return !g_OverlayInput.IsAttachedFast() &&
               (GetTickCount64() - m_lastInputHookAttemptTick.load(std::memory_order_relaxed) >= 1000ULL);
    }

    bool IsDevToolsVisible() const noexcept { return m_devtoolsVisible.load(std::memory_order_relaxed); }
    bool IsTaoConsoleVisible() const noexcept { return m_taoConsoleVisible.load(std::memory_order_relaxed); }
    bool IsMotdVisible() const noexcept { return m_motdVisible.load(std::memory_order_relaxed); }
    bool HasAnyVisibleDocument() const noexcept {
        return m_devtoolsVisible.load(std::memory_order_relaxed) ||
               m_taoConsoleVisible.load(std::memory_order_relaxed) ||
               m_motdVisible.load(std::memory_order_relaxed);
    }
    void ForceQrRefresh() { m_forceQrRefresh.store(true, std::memory_order_relaxed); }
    void EnqueueUiTask(std::function<void()> task);
    void DrainPendingUiTasks();
    void HandleUiAction(const std::string& action);

    // Phase 5: Multi-Tab Navigation & Interactive Console Evaluator
    void SwitchTab(const std::string& tabId);
    void ExecuteConsoleInput();
    void ExecuteCommandDirect(const std::string& cmd);
    void SetConsoleInputValue(const std::string& val);
    void ClearConsoleTerminal();
    void NavigateCommandHistory(int direction);
    std::string GetActiveTab();

    // In-Game Store & Web News Panels Modernization (RmlUi)
    struct MotdSlideData {
        std::string id;
        std::string badge;
        std::string title;
        std::string subtitle;
        std::string bullet1;
        std::string bullet2;
        std::string bullet3;
        std::string action;
    };
    void SetMotdSlide(int index);
    void NextMotdSlide();
    void PrevMotdSlide();
    void ExecuteMotdSlideAction();
    void HydrateMotdUI();
    int GetMotdCurrentSlide();

private:
    void UpdateTelemetryUI();
    void UpdateMotdUI();
    void SetInnerRmlIfChanged(Rml::Element* el, const std::string& val);
    void SetClassNamesIfChanged(Rml::Element* el, const char* cls);

private:
    VkDevice m_device;
    VkPhysicalDevice m_physicalDevice;
    VkQueue m_queue;
    uint32_t m_queueFamilyIndex;
    VkFormat m_format;
    uint32_t m_width;
    uint32_t m_height;

    LayerDeviceDispatchTable m_devDispatch;
    LayerInstanceDispatchTable m_instDispatch;

    std::atomic<bool> m_isInitialized;
    std::atomic<bool> m_isEnabled;
    std::atomic<bool> m_devtoolsVisible;
    std::atomic<bool> m_taoConsoleVisible;
    std::atomic<bool> m_motdVisible;
    std::atomic<bool> m_forceTelemetryRefresh{ true };
    std::atomic<uint64_t> m_lastInputHookAttemptTick{ 0 };
    uint64_t m_lastRenderedLogGen = 0;
    std::atomic<bool> m_forceQrRefresh{ false };
    std::mutex m_uiTaskMutex;
    std::deque<std::function<void()>> m_pendingUiTasks;
    std::atomic<bool> m_hasPendingUiTasks{ false };
    uint64_t m_lastActionTick = 0;
    std::string m_lastQrPayload;
    std::string m_activeTab = "tab-overview";
    std::vector<std::string> m_cmdHistory;
    int m_cmdHistoryIdx = -1;

    // Cached DOM values to avoid redundant RmlUi XML re-parsing & LayoutEngine reflows on static 5 Hz ticks
    std::unordered_map<Rml::Element*, std::string> m_cachedElementRml;
    std::unordered_map<Rml::Element*, std::string> m_cachedElementClass;

    // MOTD Slide State
    std::vector<MotdSlideData> m_motdSlides;
    int m_motdCurrentSlide = 0;
    uint64_t m_lastSlideAdvanceTick = 0;

    std::recursive_mutex m_managerMutex;

    // RmlUi Subsystems
    RmlUi_SystemInterface_VK m_systemInterface;
    RmlUi_RenderInterface_VK m_renderInterface;
    FontEngine_STB m_fontEngine;
    Rml::Context* m_context;

    // Document Registry
    std::unordered_map<std::string, Rml::ElementDocument*> m_documents;

    // Vulkan Command & Synchronization (Per-Swapchain-Image Multi-Buffering)
    struct FrameResource {
        VkCommandBuffer cmdBuffer = VK_NULL_HANDLE;
        VkFence fence = VK_NULL_HANDLE;
        VkSemaphore completeSemaphore = VK_NULL_HANDLE;
        bool isFencePending = false;
    };
    VkCommandPool m_commandPool = VK_NULL_HANDLE;
    std::vector<FrameResource> m_frameResources;

    void AllocateFrameResources(size_t count);
    void FreeFrameResources();

    // Performance telemetry counters
    LARGE_INTEGER m_qpcFrequency;
    LARGE_INTEGER m_lastFrameQpc;
    double m_smoothedFps;
    double m_smoothedFrametimeMs;
};

extern OverlayManager g_OverlayManager;
