// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: overlay_manager.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - Overlay Lifecycle Manager
// ============================================================================

#include "overlay_manager.h"
#include "tao_qr_matrix.h"
#include <RmlUi/Core/Core.h>
#include <RmlUi/Core/Element.h>
#include <RmlUi/Core/ElementDocument.h>
#include <RmlUi/Core/Elements/ElementFormControlInput.h>
#include <RmlUi/Core/Factory.h>
#include <RmlUi/Core/EventListener.h>
#include <RmlUi/Core/EventListenerInstancer.h>
#include <sstream>
#include <iomanip>
#include <psapi.h>
#include <charconv>

OverlayManager g_OverlayManager;

void OverlayManager::SetInnerRmlIfChanged(Rml::Element* el, const std::string& val) {
    if (!el) return;
    auto it = m_cachedElementRml.find(el);
    if (it != m_cachedElementRml.end() && it->second == val) {
        return;
    }
    m_cachedElementRml[el] = val;
    el->SetInnerRML(val);
}

void OverlayManager::SetClassNamesIfChanged(Rml::Element* el, const char* cls) {
    if (!el || !cls) return;
    auto it = m_cachedElementClass.find(el);
    if (it != m_cachedElementClass.end() && it->second == cls) {
        return;
    }
    m_cachedElementClass[el] = cls;
    el->SetClassNames(cls);
}

static bool IsTabPaneVisible(Rml::ElementDocument* doc, const char* paneId) {
    if (!doc || !paneId) return true;
    Rml::Element* pane = doc->GetElementById(paneId);
    return !pane || pane->IsVisible();
}

static std::string EscapeRmlText(const std::string& raw) {
    std::string out;
    out.reserve(raw.size() + 16);
    for (unsigned char c : raw) {
        switch (c) {
            case '&': out += "&amp;"; break;
            case '<': out += "&lt;"; break;
            case '>': out += "&gt;"; break;
            case '"': out += "&quot;"; break;
            case '\'': out += "&#39;"; break;
            case '\t': out += "    "; break;
            default:
                if (c >= 32 && c <= 126) {
                    out.push_back(static_cast<char>(c));
                }
                break;
        }
    }
    return out;
}

class KongorEventListener : public Rml::EventListener {
public:
    KongorEventListener(const Rml::String& value, Rml::Element* element)
        : m_value(value), m_element(element) {}

    void ProcessEvent(Rml::Event& event) override {
        HON_LOG_INFO("[KongorEventListener] ProcessEvent: value='%s', event='%s', element=<%s id='%s'>",
                     m_value.c_str(), event.GetType().c_str(),
                     m_element ? m_element->GetTagName().c_str() : "null",
                     m_element ? m_element->GetId().c_str() : "");

        if (m_value.rfind("BuyItem ", 0) == 0) {
            std::string itemName = m_value.substr(8);
            HON_LOG_INFO("[KongorEventListener] BuyItem triggered for '%s'.", itemName.c_str());
            g_OverlayBridge.ExecuteCommand("buy " + itemName);
        } else if (m_value.rfind("exec ", 0) == 0) {
            std::string cmd = m_value.substr(5);
            g_OverlayBridge.ExecuteCommand(cmd);
        } else {
            g_OverlayManager.HandleUiAction(m_value);
        }
    }

    void OnDetach(Rml::Element* /*element*/) override {
        delete this;
    }

private:
    Rml::String m_value;
    Rml::Element* m_element;
};

class KongorEventListenerInstancer : public Rml::EventListenerInstancer {
public:
    Rml::EventListener* InstanceEventListener(const Rml::String& value, Rml::Element* element) override {
        HON_LOG_INFO("[KongorEventListenerInstancer] Instanced listener for inline event '%s' on <%s id='%s'>.",
                     value.c_str(),
                     element ? element->GetTagName().c_str() : "null",
                     element ? element->GetId().c_str() : "");
        return new KongorEventListener(value, element);
    }
};

static KongorEventListenerInstancer s_kongorEventListenerInstancer;

class DocumentClickDispatcher : public Rml::EventListener {
public:
    void ProcessEvent(Rml::Event& event) override {
        if (event.GetId() != Rml::EventId::Click) return;
        auto target = event.GetTargetElement();
        if (!target) return;

        for (Rml::Element* el = target; el != nullptr && el->GetTagName() != "body"; el = el->GetParentNode()) {
            const std::string& id = el->GetId();
            if (!id.empty()) {
                if (id == "btn-tao-console" || id == "btn-open-shop" ||
                    id == "btn-back-devtools" || id == "btn-back-devtools-footer" ||
                    id == "btn-close-devtools" || id == "btn-close-motd" ||
                    id == "btn-close-console" || id == "btn-close-console-footer" || id == "btn-close-shop" ||
                    id == "btn-refresh-trace" ||
                    id == "btn-self-heal" || id == "btn-reload-hud" ||
                    id == "btn-exec-cmd" ||
                    id == "btn-prev-slide" || id == "btn-next-slide" ||
                    id == "slide-action-btn" || id == "btn-visit-store" ||
                    id == "deal-1" || id == "deal-2" ||
                    id.rfind("dot-", 0) == 0 ||
                    id.rfind("tab-btn-", 0) == 0 ||
                    id.rfind("devtab-btn-", 0) == 0 ||
                    id.rfind("ScenarioPreset:", 0) == 0 ||
                    id.rfind("btn-preset-", 0) == 0)
                {
                    g_OverlayManager.HandleUiAction(id);
                    event.StopPropagation();
                    return;
                }
            }
        }
    }

    void OnDetach(Rml::Element* /*element*/) override {}
};

void OverlayManager::HandleUiAction(const std::string& action) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    HON_LOG_INFO("[OverlayManager] HandleUiAction: '%s'", action.c_str());

    // Phase P2.1: 1-Click Interactive Scenario Presets
    if (action.rfind("ScenarioPreset:", 0) == 0 || action.rfind("btn-preset-", 0) == 0) {
        std::string preset = action;
        if (action.rfind("btn-preset-", 0) == 0) {
            std::string slug = action.substr(11);
            if (slug == "teamfight") preset = "ScenarioPreset:MidTeamfight5v5";
            else if (slug == "heartbeat") preset = "ScenarioPreset:LowHpHeartbeat";
            else if (slug == "maxgold") preset = "ScenarioPreset:MaxLevelGold";
            else if (slug == "kongor") preset = "ScenarioPreset:KongorBossPit";
            else if (slug == "reloadui") preset = "ScenarioPreset:HotReloadAllUi";
        }

        HON_LOG_INFO("[OverlayManager] Executing 1-Click Scenario Preset: '%s'", preset.c_str());

        if (preset == "ScenarioPreset:MidTeamfight5v5") {
            g_OverlayBridge.PushLogEntry("OK", "PRESET", "Scenario Preset: Mid Teamfight 5v5 triggered.");
            g_OverlayBridge.ExecuteCommand("Script if BotChoreography then BotChoreography:TriggerPreset('mid_teamfight_5v5') end; StartGame practice GameName map:caldavar allheroes:true; cam_setpos 7500 7000");
        } else if (preset == "ScenarioPreset:LowHpHeartbeat") {
            g_OverlayBridge.PushLogEntry("OK", "PRESET", "Scenario Preset: Low HP Heartbeat triggered.");
            g_OverlayBridge.ExecuteCommand("Set sv_cheats 1; Set cg_heartbeatPulse 1; Set cg_lowHealthThreshold 0.25; Script if Testing and Testing.GetSelectedEntity then Testing.DamageEntity(Testing.GetSelectedEntity(), 5000) end; ServerCommand \"sv_cheats 1\"");
        } else if (preset == "ScenarioPreset:MaxLevelGold") {
            g_OverlayBridge.PushLogEntry("OK", "PRESET", "Scenario Preset: Max Level 25 & 99999 Gold triggered.");
            g_OverlayBridge.ExecuteCommand("Set sv_cheats 1; Script if Testing then if Testing.GetSelectedEntity then Testing.LevelMax(Testing.GetSelectedEntity()) end; if GetLocalClientNumber then Testing.GiveGold(GetLocalClientNumber(), 99999) end end; ServerCommand \"givegold 99999\"; ServerCommand \"levelup 25\"");
        } else if (preset == "ScenarioPreset:KongorBossPit") {
            g_OverlayBridge.PushLogEntry("OK", "PRESET", "Scenario Preset: Kongor Boss Pit triggered.");
            g_OverlayBridge.ExecuteCommand("Script if BotChoreography then BotChoreography:TriggerPreset('kongor_pit_clash') end; cam_setpos 9800 6500; ServerCommand \"bot_choreography preset kongor_pit_clash\"");
        } else if (preset == "ScenarioPreset:HotReloadAllUi") {
            g_OverlayBridge.PushLogEntry("OK", "PRESET", "Scenario Preset: Hot Reload All UI triggered.");
            g_OverlayBridge.ExecuteCommand("Script if ClearGetWidgetCache then ClearGetWidgetCache() end; Set ui_reloadInterfaces true; ReloadRes; ReloadSelected");
            ReloadDocuments();
            g_OverlayBridge.ExecuteCommand("Script UIManager.GetInterface('main'):DoEvent()");
        }
        return;
    }

    // MOTD News & Carousel Navigation Actions (Instant, non-debounced)
    if (action == "PrevSlide" || action == "btn-prev-slide") {
        PrevMotdSlide();
        return;
    }
    if (action == "NextSlide" || action == "btn-next-slide") {
        NextMotdSlide();
        return;
    }
    if (action.rfind("SelectSlide:", 0) == 0) {
        int idx = 0;
        const char* s = action.data() + 12;
        const char* e = action.data() + action.size();
        auto [ptr, ec] = std::from_chars(s, e, idx);
        if (ec != std::errc{}) return;
        SetMotdSlide(idx);
        return;
    }
    if (action.rfind("dot-", 0) == 0) {
        int idx = 0;
        const char* s = action.data() + 4;
        const char* e = action.data() + action.size();
        auto [ptr, ec] = std::from_chars(s, e, idx);
        if (ec != std::errc{}) return;
        SetMotdSlide(idx);
        return;
    }
    if (action == "SlideAction" || action == "slide-action-btn") {
        ExecuteMotdSlideAction();
        return;
    }
    if (action == "OpenStore" || action == "btn-visit-store") {
        g_OverlayBridge.ExecuteCommand("Script local s = GetCvarBool('cg_store2_') and 'store_container2' or 'store_container'; UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', s, nil, false)");
        SetDocumentVisible("motd", false);
        return;
    }
    if (action == "PracticeHero") {
        g_OverlayBridge.ExecuteCommand("StartGame practice GameName map:caldavar hero:Hero_Gwynplaine allheroes:true");
        SetDocumentVisible("motd", false);
        return;
    }
    if (action == "ViewDeal:1" || action == "deal-1") {
        g_OverlayBridge.ExecuteCommand("Script local s = GetCvarBool('cg_store2_') and 'store_container2' or 'store_container'; UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', s, nil, false); if HoN_Store and HoN_Store.SelectProduct then HoN_Store:SelectProduct('Hero_Bubbles.Alt16') end");
        SetDocumentVisible("motd", false);
        return;
    }
    if (action == "ViewDeal:2" || action == "deal-2") {
        g_OverlayBridge.ExecuteCommand("Script local s = GetCvarBool('cg_store2_') and 'store_container2' or 'store_container'; UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', s, nil, false); if HoN_Store and HoN_Store.SelectProduct then HoN_Store:SelectProduct('Hero_Gwynplaine') end");
        SetDocumentVisible("motd", false);
        return;
    }
    if (action == "ToggleMotd" || action == "OpenMotd") {
        ToggleDocument("motd");
        return;
    }
    if (action == "CloseMotd" || action == "btn-close-motd") {
        SetDocumentVisible("motd", false);
        return;
    }

    // Phase 5 Tab Switching (Instant, non-debounced)
    if (action.rfind("SwitchTab:", 0) == 0) {
        SwitchTab(action.substr(10));
        return;
    }
    if (action.rfind("tab-btn-", 0) == 0) {
        SwitchTab("tab-" + action.substr(8));
        return;
    }
    if (action.rfind("devtab-btn-", 0) == 0) {
        SwitchTab("devtab-" + action.substr(11));
        return;
    }

    // Phase 5 Interactive Command Execution (Instant)
    if (action == "ExecCmd" || action == "btn-exec-cmd") {
        ExecuteConsoleInput();
        return;
    }
    if (action.rfind("ExecCmd:", 0) == 0) {
        std::string cmd = action.substr(8);
        SetConsoleInputValue(cmd);
        ExecuteCommandDirect(cmd);
        return;
    }
    if (action.rfind("ChipCmd:", 0) == 0) {
        std::string chipCmd = action.substr(8);
        SetConsoleInputValue(chipCmd);
        ExecuteCommandDirect(chipCmd);
        return;
    }
    if (action == "ClearConsoleTerminal") {
        ClearConsoleTerminal();
        return;
    }

    // Overlay Window Navigation Actions (Instant, non-debounced so clicks never drop)
    if (action == "BackToDevTools" || action == "OpenDevTools" || action == "btn-back-devtools" || action == "btn-back-devtools-footer") {
        HON_LOG_INFO("[OverlayManager] Navigating from TAO Console back to DevTools Cockpit (action='%s').", action.c_str());
        SetDocumentVisible("tao_console", false);
        SetDocumentVisible("devtools", true);
        return;
    }
    if (action == "ToggleConsole" || action == "ToggleShop") {
        ToggleDocument("tao_console");
        if (IsDocumentVisible("tao_console")) {
            SetDocumentVisible("devtools", false);
        }
        return;
    }
    if (action == "OpenConsole" || action == "OpenShop" || action == "btn-tao-console" || action == "btn-open-shop") {
        HON_LOG_INFO("[OverlayManager] Opening TAO Diagnostics Console from DevTools Cockpit (action='%s').", action.c_str());
        SetDocumentVisible("tao_console", true);
        SetDocumentVisible("devtools", false);
        return;
    }
    if (action == "CloseConsole" || action == "CloseShop" || action == "btn-close-console" || action == "btn-close-console-footer" || action == "btn-close-shop") {
        SetDocumentVisible("tao_console", false);
        return;
    }
    if (action == "Dismiss" || action == "Close" || action == "CloseDevTools" || action == "btn-close-devtools") {
        SetDocumentVisible("devtools", false);
        SetDocumentVisible("tao_console", false);
        SetDocumentVisible("motd", false);
        return;
    }

    uint64_t now = GetTickCount64();
    if (now < m_lastActionTick + 250) {
        HON_LOG_DEBUG("[OverlayManager] HandleUiAction: '%s' throttled by 250ms debouncer.", action.c_str());
        return;
    }
    m_lastActionTick = now;

    if (action == "SelfHealUi" || action == "btn-self-heal" || action == "btn-reload-hud" || action == "ReloadHud") {
        auto it = m_documents.find("tao_console");
        if (it != m_documents.end() && it->second) {
            auto elBtn = it->second->GetElementById("btn-self-heal");
            if (elBtn) {
                elBtn->SetInnerRML("Remediating...");
            }
        }
        std::string receipt = g_OverlayBridge.ExecuteSelfHealing();
        ReloadDocuments();
        g_OverlayBridge.ExecuteCommand("Script UIManager.GetInterface('main'):DoEvent()");
    } else if (action == "RefreshTrace" || action == "btn-refresh-trace") {
        g_OverlayBridge.PushLogEntry("OK", "TRACE", "Diagnostics trace probe refreshed.");
        g_OverlayBridge.WakeProbeWorker();
        ForceQrRefresh();
    } else if (action == "ReloadDocuments") {
        ReloadDocuments();
        g_OverlayBridge.ExecuteCommand("Script UIManager.GetInterface('main'):DoEvent()");
    }
}

static DocumentClickDispatcher s_documentClickDispatcher;

struct EnumWindowData {
    DWORD processId;
    HWND hwnd;
};

static BOOL CALLBACK EnumWindowsCallback(HWND hwnd, LPARAM lParam) {
    auto data = reinterpret_cast<EnumWindowData*>(lParam);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);

    if (pid == data->processId) {
        char cls[128] = { 0 };
        GetClassNameA(hwnd, cls, sizeof(cls));
        char title[128] = { 0 };
        GetWindowTextA(hwnd, title, sizeof(title));

        // Prioritize actual HoN render window (K2_Direct3D, K2_OpenGL, etc.)
        if (_strnicmp(cls, "K2_", 3) == 0 ||
            strstr(title, "Heroes of Newerth") != nullptr ||
            strstr(title, "Project Kongor") != nullptr)
        {
            data->hwnd = hwnd;
            return FALSE; // Exact match found, stop enumeration
        }

        // Secondary fallback: Any visible window owned by process with client area > 200x200
        if (data->hwnd == nullptr && IsWindowVisible(hwnd)) {
            RECT rc;
            if (GetClientRect(hwnd, &rc) && (rc.right - rc.left) > 200 && (rc.bottom - rc.top) > 200) {
                data->hwnd = hwnd;
            }
        }
    }
    return TRUE;
}

OverlayManager::OverlayManager()
    : m_device(VK_NULL_HANDLE)
    , m_physicalDevice(VK_NULL_HANDLE)
    , m_queue(VK_NULL_HANDLE)
    , m_queueFamilyIndex(0)
    , m_format(VK_FORMAT_B8G8R8A8_UNORM)
    , m_width(0)
    , m_height(0)
    , m_isInitialized(false)
    , m_isEnabled(true)
    , m_context(nullptr)
    , m_commandPool(VK_NULL_HANDLE)
    , m_smoothedFps(60.0)
    , m_smoothedFrametimeMs(16.6)
    , m_devtoolsVisible(false)
    , m_taoConsoleVisible(false)
    , m_motdVisible(false)
    , m_forceQrRefresh(false)
    , m_motdCurrentSlide(0)
    , m_lastSlideAdvanceTick(0)
{
    memset(&m_devDispatch, 0, sizeof(m_devDispatch));
    memset(&m_instDispatch, 0, sizeof(m_instDispatch));
    QueryPerformanceFrequency(&m_qpcFrequency);
    QueryPerformanceCounter(&m_lastFrameQpc);
}

OverlayManager::~OverlayManager() {
    if (IsLayerProcessTerminating()) {
        m_isInitialized = false;
        m_context = nullptr;
        m_device = VK_NULL_HANDLE;
        return;
    }
    Cleanup();
}

HWND OverlayManager::FindGameWindow() {
    EnumWindowData data = { GetCurrentProcessId(), nullptr };
    EnumWindows(EnumWindowsCallback, reinterpret_cast<LPARAM>(&data));
    return data.hwnd;
}

void OverlayManager::RefreshInputHook() {
    HWND hwnd = FindGameWindow();
    if (hwnd && m_context) {
        bool attached = g_OverlayInput.Attach(hwnd, m_context);
        g_OverlayInput.SetHotkeyCallback([this]() {
            if (IsDocumentVisible("tao_console")) {
                HON_LOG_INFO("[OverlayManager] Ctrl+Shift+D pressed while tao_console visible -> returning to devtools cockpit.");
                SetDocumentVisible("tao_console", false);
                SetDocumentVisible("devtools", true);
            } else {
                ToggleDocument("devtools");
            }
        });
        HON_LOG_INFO("[OverlayManager] RefreshInputHook: HWND=%p, attached=%d (TID: %u)",
                     (void*)hwnd, attached ? 1 : 0, GetCurrentThreadId());
    } else {
        HON_LOG_WARN("[OverlayManager] RefreshInputHook: hwnd=%p, context=%p", (void*)hwnd, (void*)m_context);
    }
}

bool OverlayManager::Initialize(
    VkDevice device,
    VkPhysicalDevice physicalDevice,
    VkQueue queue,
    uint32_t queueFamilyIndex,
    VkFormat format,
    uint32_t width,
    uint32_t height,
    const LayerDeviceDispatchTable& devDispatch,
    const LayerInstanceDispatchTable& instDispatch)
{
    // Check environment flag (default OFF to ensure lean, crash-free headless runs)
    // Resolve RmlUi overlay enablement across Willowmaker and kongor-cli/MCP launchers:
    //   1. Explicit HON_MODERN_HUD=1/true enables (and HON_MODERN_HUD=0/false explicitly disables).
    //   2. Fallback: when HON_MODERN_HUD is unset/empty, HON_DEVTOOLS=1/true enables unless KONGOR_HEADLESS=1/true.
    //   3. Default OFF when neither flag is set to ensure lean, crash-free headless runs.
    auto isEnvTruthy = [](const char* val) -> bool {
        return val && (strcmp(val, "1") == 0 || _stricmp(val, "true") == 0);
    };
    const char* envHud = getenv("HON_MODERN_HUD");
    const char* envDevTools = getenv("HON_DEVTOOLS");
    const char* envHeadless = getenv("KONGOR_HEADLESS");
    const bool hudUnset = (!envHud || envHud[0] == '\0');
    const bool enabledByHud = isEnvTruthy(envHud);
    const bool enabledByDevTools = hudUnset && isEnvTruthy(envDevTools) && !isEnvTruthy(envHeadless);
    m_isEnabled = enabledByHud || enabledByDevTools;

    HON_LOG_INFO("[OverlayManager] Env check: m_isEnabled=%d (HON_MODERN_HUD='%s', HON_DEVTOOLS='%s', KONGOR_HEADLESS='%s')",
                 m_isEnabled ? 1 : 0,
                 envHud ? envHud : "<unset>",
                 envDevTools ? envDevTools : "<unset>",
                 envHeadless ? envHeadless : "<unset>");

    if (!m_isEnabled) {
        HON_LOG_INFO("[OverlayManager] Modern HUD & DevTools overlay disabled (set HON_MODERN_HUD=1 or HON_DEVTOOLS=1 with KONGOR_HEADLESS=0 to enable).");
        return true;
    }

    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    Cleanup();

    m_device = device;
    m_physicalDevice = physicalDevice;
    m_queue = queue;
    m_queueFamilyIndex = queueFamilyIndex;
    m_format = format;
    m_width = width;
    m_height = height;
    m_devDispatch = devDispatch;
    m_instDispatch = instDispatch;

    // 1. Initialize Vulkan RenderInterface
    if (!m_renderInterface.Initialize(device, physicalDevice, queue, queueFamilyIndex,
                                     format, width, height, devDispatch, instDispatch)) {
        HON_LOG_ERROR("[OverlayManager] Failed to initialize RmlUi RenderInterface.");
        return false;
    }

    // 2. Set RmlUi core interfaces
    Rml::SetSystemInterface(&m_systemInterface);
    Rml::SetRenderInterface(&m_renderInterface);
    Rml::SetFontEngineInterface(&m_fontEngine);

    if (!Rml::Initialise()) {
        HON_LOG_ERROR("[OverlayManager] Failed to initialize RmlUi core library.");
        return false;
    }

    Rml::Factory::RegisterEventListenerInstancer(&s_kongorEventListenerInstancer);
    HON_LOG_INFO("[OverlayManager] Registered KongorEventListenerInstancer with RmlUi factory.");

    // 3. Create main context
    m_context = Rml::CreateContext("main", Rml::Vector2i(width, height));
    if (!m_context) {
        HON_LOG_ERROR("[OverlayManager] Failed to create RmlUi main context.");
        return false;
    }

    // 4. Synchronization and command execution resources (multi-buffered)
    VkCommandPoolCreateInfo poolInfo = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    poolInfo.queueFamilyIndex = m_queueFamilyIndex;
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    m_devDispatch.CreateCommandPool(m_device, &poolInfo, nullptr, &m_commandPool);

    AllocateFrameResources(3);

    // 5. Attach Win32 input hook to game window
    RefreshInputHook();

    // 6. Initialize bridge and load documents
    g_OverlayBridge.Initialize();
    LoadDocuments();

    m_isInitialized = true;
    HON_LOG_INFO("[OverlayManager] RmlUi overlay manager fully initialized (%ux%u). Hotkey Ctrl+Shift+D bound.", width, height);
    return true;
}

void OverlayManager::AllocateFrameResources(size_t count) {
    if (m_device == VK_NULL_HANDLE || m_commandPool == VK_NULL_HANDLE || count == 0) return;
    if (m_frameResources.size() >= count) return;

    size_t prevSize = m_frameResources.size();
    m_frameResources.resize(count);

    for (size_t i = prevSize; i < count; ++i) {
        FrameResource& res = m_frameResources[i];
        res.isFencePending = false;

        VkCommandBufferAllocateInfo allocInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
        allocInfo.commandPool = m_commandPool;
        allocInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        allocInfo.commandBufferCount = 1;
        if (m_devDispatch.AllocateCommandBuffers(m_device, &allocInfo, &res.cmdBuffer) != VK_SUCCESS) {
            HON_LOG_ERROR("[OverlayManager] Failed to allocate command buffer for slot %zu", i);
        }

        VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        if (m_devDispatch.CreateFence(m_device, &fenceInfo, nullptr, &res.fence) != VK_SUCCESS) {
            HON_LOG_ERROR("[OverlayManager] Failed to create fence for slot %zu", i);
        }

        VkSemaphoreCreateInfo semInfo = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
        if (m_devDispatch.CreateSemaphore(m_device, &semInfo, nullptr, &res.completeSemaphore) != VK_SUCCESS) {
            HON_LOG_ERROR("[OverlayManager] Failed to create semaphore for slot %zu", i);
        }
    }
    HON_LOG_INFO("[OverlayManager] Initialized %zu per-frame synchronization slots.", m_frameResources.size());
}

void OverlayManager::FreeFrameResources() {
    if (m_device == VK_NULL_HANDLE || IsLayerProcessTerminating()) {
        m_frameResources.clear();
        return;
    }

    for (size_t i = 0; i < m_frameResources.size(); ++i) {
        FrameResource& res = m_frameResources[i];
        if (res.fence != VK_NULL_HANDLE) {
            if (m_devDispatch.WaitForFences && res.isFencePending) {
                VkResult waitRes = m_devDispatch.WaitForFences(m_device, 1, &res.fence, VK_TRUE, 250000000ULL); // 250ms bounded wait
                if (waitRes != VK_SUCCESS) {
                    HON_LOG_WARN("[OverlayManager] Frame slot %zu fence wait returned %d during teardown.", i, waitRes);
                }
                res.isFencePending = false;
            }
            if (m_devDispatch.DestroyFence) {
                m_devDispatch.DestroyFence(m_device, res.fence, nullptr);
            }
            res.fence = VK_NULL_HANDLE;
        }
        if (res.completeSemaphore != VK_NULL_HANDLE && m_devDispatch.DestroySemaphore) {
            m_devDispatch.DestroySemaphore(m_device, res.completeSemaphore, nullptr);
            res.completeSemaphore = VK_NULL_HANDLE;
        }
        if (res.cmdBuffer != VK_NULL_HANDLE && m_commandPool != VK_NULL_HANDLE && m_devDispatch.FreeCommandBuffers) {
            m_devDispatch.FreeCommandBuffers(m_device, m_commandPool, 1, &res.cmdBuffer);
            res.cmdBuffer = VK_NULL_HANDLE;
        }
    }
    m_frameResources.clear();
}

void OverlayManager::SetSwapchainImages(const std::vector<VkImage>& images) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    m_renderInterface.SetSwapchainImages(images);
    if (!images.empty()) {
        AllocateFrameResources(images.size());
    }
}

void OverlayManager::OnResize(uint32_t width, uint32_t height) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    m_width = width;
    m_height = height;
    m_renderInterface.OnResize(width, height);
    if (m_context) {
        m_context->SetDimensions(Rml::Vector2i(width, height));
    }
}

void OverlayManager::ReleaseSwapchainResources() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    m_renderInterface.ReleaseSwapchainResources();
}

void OverlayManager::LoadDocuments() {
    m_cachedElementRml.clear();
    m_cachedElementClass.clear();
    m_lastRenderedLogGen = 0;
    m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);
    if (!m_context) return;

    // Embedded default DevTools RML template for instant zero-dependency availability
    const char* devToolsTemplate = R"(
    <rml>
    <head>
        <title>DevTools</title>
        <style>
            body {
                width: 100%;
                height: 100%;
                font-family: Arial, sans-serif;
                font-size: 13px;
                color: #e0e6ed;
                pointer-events: none;
            }
            #devtools-window {
                pointer-events: auto;
                position: absolute;
                top: 4%;
                left: 5%;
                width: 90%;
                height: 91%;
                background-color: rgba(15, 23, 42, 0.92);
                border: 1px solid #38bdf8;
                border-radius: 8px;
                padding: 16px;
                box-shadow: 0 10px 25px rgba(0, 0, 0, 0.5);
            }
            .header {
                font-size: 15px;
                font-weight: bold;
                color: #38bdf8;
                border-bottom: 1px solid #334155;
                padding-bottom: 8px;
                margin-bottom: 12px;
            }
            .metric-row {
                display: flex;
                justify-content: space-between;
                margin-bottom: 6px;
            }
            .metric-label {
                color: #94a3b8;
            }
            .metric-value {
                font-weight: bold;
                color: #4ade80;
            }
            .button-group {
                display: flex;
                gap: 8px;
                margin-top: 12px;
            }
            .btn {
                flex: 1;
                background-color: #0284c7;
                color: #ffffff;
                text-align: center;
                padding: 6px 10px;
                border-radius: 4px;
                cursor: pointer;
                font-size: 12px;
            }
            .btn:hover {
                background-color: #0369a1;
            }
            .btn-secondary {
                background-color: #334155;
                color: #cbd5e1;
            }
            .btn-secondary:hover {
                background-color: #475569;
            }
        </style>
    </head>
    <body>
        <div id="devtools-window">
            <div class="header">ProjectKongor DevTools (Ctrl+Shift+D)</div>
            <div class="metric-row">
                <span class="metric-label">FPS:</span>
                <span id="metric-fps" class="metric-value">60.0</span>
            </div>
            <div class="metric-row">
                <span class="metric-label">Frame Time:</span>
                <span id="metric-ft" class="metric-value">16.6 ms</span>
            </div>
            <div class="metric-row">
                <span class="metric-label">Vulkan Swapchain:</span>
                <span id="metric-res" class="metric-value">1920x1080</span>
            </div>
            <div class="metric-row">
                <span class="metric-label">Memory Working Set:</span>
                <span id="metric-ram" class="metric-value">&lt; 450 MB</span>
            </div>
            <div class="metric-row">
                <span class="metric-label">CEF State:</span>
                <span class="metric-value" style="color: #38bdf8;">DEACTIVATED (0 int 3)</span>
            </div>
            <div class="button-group">
                <div id="btn-tao-console" class="btn" onclick="ToggleConsole">TAO Console</div>
                <div id="btn-close-devtools" class="btn btn-secondary" onclick="Dismiss">Close</div>
            </div>
        </div>
    </body>
    </rml>
    )";

    // Try loading from external assets/hud/devtools.rml first, fallback to embedded
    Rml::ElementDocument* doc = m_context->LoadDocument("assets/hud/devtools.rml");
    if (!doc) {
        doc = m_context->LoadDocumentFromMemory(devToolsTemplate);
    }

    if (doc) {
        doc->AddEventListener(Rml::EventId::Click, &s_documentClickDispatcher);
        m_devtoolsVisible = false;
        doc->Hide();
        m_documents["devtools"] = doc;
        HON_LOG_INFO("[OverlayManager] Loaded DevTools document (hidden by default).");
    }

    // Modern TAO Diagnostics Console Document
    const char* taoConsoleTemplate = R"rml1(
    <rml>
    <head>
        <title>TAO Diagnostics Console</title>
        <style>
            body {
                width: 100%;
                height: 100%;
                font-family: Arial, sans-serif;
                font-size: 15px;
                color: #e2e8f0;
                pointer-events: none;
            }
            #tao-console-window {
                pointer-events: auto;
                position: absolute;
                top: 4%;
                left: 5%;
                width: 90%;
                height: 91%;
                background-color: #070c18;
                border-width: 2px;
                border-color: #38bdf8;
                border-radius: 14px;
                padding: 22px 28px;
            }
            .console-header {
                display: block;
                border-bottom-width: 2px;
                border-bottom-color: #1e293b;
                padding-bottom: 14px;
                margin-bottom: 14px;
            }
            .header-left {
                float: left;
                display: block;
            }
            .console-title {
                font-size: 24px;
                font-weight: bold;
                color: #38bdf8;
                display: inline-block;
                vertical-align: middle;
            }
            .header-pill {
                font-size: 13px;
                background-color: #0284c7;
                color: #ffffff;
                padding: 4px 12px;
                border-radius: 6px;
                font-weight: bold;
                display: inline-block;
                vertical-align: middle;
                margin-left: 14px;
            }
            .close-x-btn {
                float: right;
                display: block;
                background-color: #dc2626;
                color: #ffffff;
                font-size: 18px;
                font-weight: bold;
                padding: 6px 18px;
                border-radius: 6px;
                cursor: pointer;
            }
            .close-x-btn:hover {
                background-color: #b91c1c;
            }
            .service-badges {
                display: block;
                margin-bottom: 14px;
            }
            .badge {
                display: inline-block;
                background-color: #0f172a;
                border-width: 1px;
                border-color: #334155;
                border-radius: 8px;
                padding: 6px 14px;
                margin-right: 10px;
                margin-bottom: 6px;
                vertical-align: middle;
            }
            .badge-dot {
                display: inline-block;
                width: 12px;
                height: 12px;
                border-radius: 6px;
                vertical-align: middle;
                margin-right: 8px;
            }
            .dot-green { background-color: #4ade80; }
            .dot-amber { background-color: #fbbf24; }
            .dot-red { background-color: #f87171; }
            .badge-text {
                display: inline-block;
                color: #f1f5f9;
                font-weight: bold;
                font-size: 13px;
                vertical-align: middle;
            }
            .nav-tabs {
                display: block;
                margin-bottom: 14px;
                border-bottom-width: 2px;
                border-bottom-color: #1e293b;
                padding-bottom: 8px;
            }
            .tab-btn {
                float: left;
                display: block;
                padding: 8px 18px;
                margin-right: 10px;
                background-color: #0f172a;
                border-width: 1px;
                border-color: #334155;
                border-radius: 8px;
                color: #94a3b8;
                font-size: 14px;
                font-weight: bold;
                cursor: pointer;
            }
            .tab-btn:hover { background-color: #1e293b; color: #f1f5f9; }
            .tab-btn.active { background-color: #0284c7; border-color: #38bdf8; color: #ffffff; }
            .tab-content { display: block; }
            .tab-pane { display: none; }
            .tab-pane.active { display: block; }
            .content-columns {
                display: block;
                margin-bottom: 12px;
            }
            .left-column {
                float: left;
                width: 48%;
            }
            .right-column {
                float: right;
                width: 49%;
            }
            .diagnostics-panel {
                display: block;
                background-color: #0c1322;
                border-width: 1px;
                border-color: #1e293b;
                border-radius: 10px;
                padding: 12px 18px;
                margin-bottom: 10px;
            }
            .panel-section-title {
                display: block;
                font-size: 15px;
                font-weight: bold;
                color: #38bdf8;
                text-transform: uppercase;
                margin-bottom: 10px;
                border-bottom-width: 1px;
                border-bottom-color: #1e293b;
                padding-bottom: 6px;
            }
            .stat-row {
                display: block;
                margin-bottom: 6px;
                padding-bottom: 6px;
                border-bottom-width: 1px;
                border-bottom-color: #131d30;
            }
            .stat-label { float: left; color: #94a3b8; font-size: 14px; }
            .stat-val { float: right; color: #f8fafc; font-weight: bold; font-size: 14px; }
            .font-accent { color: #4ade80; }
            .font-blue { color: #38bdf8; }
            .font-warn { color: #fbbf24; }
            .font-danger { color: #f87171; }
            .font-mono { font-family: Courier, monospace; }
            .log-panel {
                display: block;
                background-color: #050811;
                border-width: 1px;
                border-color: #1e293b;
                border-radius: 10px;
                padding: 12px 18px;
            }
            .log-terminal {
                display: block;
                font-family: Courier, monospace;
                font-size: 13px;
                max-height: 480px;
                overflow-y: auto;
            }
            .log-line { display: block; margin-bottom: 5px; line-height: 1.4; }
            .log-info { color: #94a3b8; }
            .log-ok { color: #4ade80; }
            .log-warn { color: #fbbf24; }
            .log-error { color: #f87171; }
            .log-heal { color: #38bdf8; font-weight: bold; }
            .chips-bar { display: block; margin-bottom: 10px; }
            .chip-btn {
                float: left;
                display: block;
                background-color: #1e293b;
                color: #38bdf8;
                border-width: 1px;
                border-color: #334155;
                border-radius: 6px;
                padding: 5px 12px;
                margin-right: 8px;
                margin-bottom: 6px;
                font-size: 13px;
                font-family: Courier, monospace;
                cursor: pointer;
            }
            .chip-btn:hover { background-color: #334155; color: #ffffff; }
            .chip-clear { color: #f87171; border-color: #7f1d1d; }
            .console-terminal-large {
                display: block;
                background-color: #030712;
                border-width: 1px;
                border-color: #1e293b;
                border-radius: 10px;
                padding: 14px 18px;
                font-family: Courier, monospace;
                font-size: 13px;
                height: 290px;
                overflow-y: auto;
                margin-bottom: 12px;
            }
            .console-input-bar {
                display: block;
                background-color: #0c1322;
                border-width: 1px;
                border-color: #334155;
                border-radius: 8px;
                padding: 6px 12px;
            }
            .console-prompt {
                float: left;
                display: block;
                color: #38bdf8;
                font-weight: bold;
                font-size: 18px;
                line-height: 36px;
                margin-right: 10px;
                font-family: Courier, monospace;
            }
            .console-input {
                float: left;
                display: block;
                width: 78%;
                height: 34px;
                background-color: #050811;
                border-width: 1px;
                border-color: #1e293b;
                border-radius: 6px;
                color: #f8fafc;
                font-size: 14px;
                font-family: Courier, monospace;
                padding: 4px 10px;
            }
            .btn-exec {
                float: right;
                background-color: #16a34a;
                color: #ffffff;
                padding: 8px 20px;
                margin-right: 0;
            }
            .btn-exec:hover { background-color: #15803d; }
            .console-footer {
                display: block;
                margin-top: 14px;
                border-top-width: 1px;
                border-top-color: #1e293b;
                padding-top: 12px;
            }
            .footer-actions { float: left; display: block; }
            .btn {
                display: inline-block;
                padding: 9px 22px;
                border-radius: 8px;
                font-size: 14px;
                font-weight: bold;
                cursor: pointer;
                text-align: center;
                margin-right: 12px;
                vertical-align: middle;
            }
            .btn-action { background-color: #0284c7; color: #ffffff; }
            .btn-action:hover { background-color: #0369a1; }
            .btn-warn { background-color: #d97706; color: #ffffff; }
            .btn-warn:hover { background-color: #b45309; }
            .btn-danger { background-color: #dc2626; color: #ffffff; }
            .btn-danger:hover { background-color: #b91c1c; }
            .btn-secondary { float: right; background-color: #334155; color: #f1f5f9; }
            .btn-secondary:hover { background-color: #475569; }
        </style>
    </head>
    )rml1"
    R"rml2(
    <body>
        <div id="tao-console-window">
            <div class="console-header">
                <div class="header-left">
                    <span class="console-title">[TAO] Developer Console &amp; Agent Diagnostics</span>
                    <span class="header-pill">Together as One</span>
                </div>
                <div id="btn-close-console" class="close-x-btn" onclick="CloseConsole">X</div>
                <div class="clear-fix"></div>
            </div>
            <div class="service-badges">
                <div id="badge-master" class="badge">
                    <span id="dot-master" class="badge-dot dot-green"></span>
                    <span id="text-master" class="badge-text">Master: 55555 (OK)</span>
                </div>
                <div id="badge-chat" class="badge">
                    <span id="dot-chat" class="badge-dot dot-green"></span>
                    <span id="text-chat" class="badge-text">Chat: 11236 (OK)</span>
                </div>
                <div id="badge-gameserver" class="badge">
                    <span id="dot-gameserver" class="badge-dot dot-green"></span>
                    <span id="text-gameserver" class="badge-text">Game Server: 11235 (20Hz)</span>
                </div>
                <div id="badge-network" class="badge">
                    <span id="dot-network" class="badge-dot dot-green"></span>
                    <span id="text-network" class="badge-text">Network: 12ms / 0% Loss</span>
                </div>
                <div id="badge-engine" class="badge">
                    <span id="dot-engine" class="badge-dot dot-green"></span>
                    <span id="text-engine" class="badge-text">Engine: Vulkan 1.3</span>
                </div>
            </div>

            <!-- Navigation Tabs Bar -->
            <div class="nav-tabs">
                <div id="tab-btn-overview" class="tab-btn active" onclick="SwitchTab:tab-overview">Overview &amp; Diagnostics</div>
                <div id="tab-btn-fleet" class="tab-btn" onclick="SwitchTab:tab-fleet">Cluster Fleet</div>
                <div id="tab-btn-vulkan" class="tab-btn" onclick="SwitchTab:tab-vulkan">Vulkan &amp; Memory</div>
                <div id="tab-btn-vfs" class="tab-btn" onclick="SwitchTab:tab-vfs">VFS Mounts</div>
                <div id="tab-btn-console" class="tab-btn" onclick="SwitchTab:tab-console">Interactive Console</div>
                <div class="clear-fix"></div>
            </div>

            <!-- Tab Content Container -->
            <div class="tab-content">
                <!-- TAB 1: Overview & Diagnostics -->
                <div id="tab-overview" class="tab-pane active">
                    <div class="content-columns">
                        <div class="left-column">
                            <div class="diagnostics-panel">
                                <div class="panel-section-title">Live System Telemetry</div>
                                <div class="stat-row">
                                    <span class="stat-label">IPC Channel:</span>
                                    <span id="tao-stat-ipc" class="stat-val font-mono">\\.\pipe\willowmaker-ipc (ACTIVE)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Swapchain:</span>
                                    <span id="tao-stat-res" class="stat-val">1920x1080 (B8G8R8A8)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Framerate / Latency:</span>
                                    <span id="tao-stat-fps" class="stat-val font-accent">240.0 FPS (4.16 ms)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Net QoS / Ping:</span>
                                    <span id="tao-stat-net" class="stat-val font-accent">12 ms (0.0% loss)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Server Tick:</span>
                                    <span id="tao-stat-tick" class="stat-val font-mono font-blue">Tick: 0 | Client: 0</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">VFS Archives:</span>
                                    <span id="tao-stat-vfs" class="stat-val">14 Mounted (0 Loose)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Audio / HRTF:</span>
                                    <span id="tao-stat-audio" class="stat-val">WASAPI (64 Voices)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Fault Attribution:</span>
                                    <span id="tao-stat-fault" class="stat-val font-accent">NOMINAL (All Systems OK)</span>
                                    <div class="clear-fix"></div>
                                </div>
                            </div>
                        </div>
                        <div class="right-column">
                            <div class="log-panel">
                                <div class="panel-section-title">Recent Engine &amp; Layer Log Stream</div>
                                <div id="tao-log-terminal" class="log-terminal">
                                    <div class="log-line log-info">[INFO][TAO] Together as One (TAO) MCP session initialized.</div>
                                </div>
                            </div>
                        </div>
                        <div class="clear-fix"></div>
                    </div>
                </div>

                <!-- TAB 2: Cluster Fleet -->
                <div id="tab-fleet" class="tab-pane">
                    <div class="content-columns">
                        <div class="left-column">
                            <div class="diagnostics-panel">
                                <div class="panel-section-title">Microservice Fleet Status</div>
                                <div class="stat-row">
                                    <span class="stat-label">Master Server:</span>
                                    <span id="fleet-master-val" class="stat-val font-accent">127.0.0.1:55555 (ONLINE)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Chat Server:</span>
                                    <span id="fleet-chat-val" class="stat-val font-accent">127.0.0.1:11031 (ONLINE)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Dedicated Server:</span>
                                    <span id="fleet-server-val" class="stat-val font-blue">Port 11235 (20 Hz | 0 Bots)</span>
                                    <div class="clear-fix"></div>
                                </div>
                                <div class="stat-row">
                                    <span class="stat-label">Redis Cache:</span>
                                    <span id="fleet-redis-val" class="stat-val font-accent">127.0.0.1:28298 (CONNECTED)</span>
                                    <div class="clear-fix"></div>
                                </div>
                            </div>
                        </div>
                        <div class="right-column">
                            <div class="diagnostics-panel">
                                <div class="panel-section-title">Fleet Actions &amp; Dedicated Workers</div>
                                <div class="stat-row">
                                    <div class="btn btn-action" onclick="ExecCmd:AddFakePlayer 1">Add Bot (Legion)</div>
                                    <div class="btn btn-action" onclick="ExecCmd:AddFakePlayer 2">Add Bot (Hellbourne)</div>
                                    <div class="clear-fix"></div>
                                </div>
                            </div>
                        </div>
                        <div class="clear-fix"></div>
                    </div>
            </div>
            )"
            R"(
            <!-- TAB 3: Vulkan & Memory -->
            <div id="tab-vulkan" class="tab-pane">
                    <div class="diagnostics-panel">
                        <div class="panel-section-title">Vulkan Swapchain Pipeline &amp; Memory Allocation</div>
                        <div class="stat-row">
                            <span class="stat-label">Physical Device:</span>
                            <span class="stat-val font-mono">DirectX / Vulkan GPU (Vulkan 1.3)</span>
                            <div class="clear-fix"></div>
                        </div>
                        <div class="stat-row">
                            <span class="stat-label">Ring Buffer:</span>
                            <span class="stat-val font-mono">Local\HonReplayFrameBuffer_{pid} (3 Slots)</span>
                            <div class="clear-fix"></div>
                        </div>
                    </div>
                </div>

                <!-- TAB 4: VFS Mounts -->
                <div id="tab-vfs" class="tab-pane">
                    <div class="diagnostics-panel">
                        <div class="panel-section-title">Virtual File System (VFS) Mount Hierarchy</div>
                        <div class="stat-row">
                            <span class="stat-label">Priority 1 (Override):</span>
                            <span class="stat-val font-mono">games/client/override/</span>
                            <div class="clear-fix"></div>
                        </div>
                        <div class="stat-row">
                            <span class="stat-label">Priority 2 (Base Engine):</span>
                            <span class="stat-val font-mono">game/base.s2z (Core Shaders, Fonts, RmlUi)</span>
                            <div class="clear-fix"></div>
                        </div>
                    </div>
                </div>

                <!-- TAB 5: Interactive Console & Lua Evaluator -->
                <div id="tab-console" class="tab-pane">
                    <div id="tao-console-terminal" class="console-terminal-large">
                        <div class="log-line log-info">[TAO EVAL] Interactive In-Engine Developer Console Ready.</div>
                        <div class="log-line log-info">[TAO EVAL] Anti-Chat Spill active: Win32 SubclassWndProc consumes keystrokes.</div>
                        <div class="log-line log-ok">[TAO EVAL] Enter engine command or 'lua: &lt;script&gt;' below. Press Enter to execute.</div>
                    </div>
                    <div class="console-input-bar">
                        <span class="console-prompt">&gt;</span>
                        <input type="text" id="tao-input-cmd" class="console-input" placeholder="Enter engine command (e.g. echo hello) or 'lua: <script>'..." />
                        <div id="btn-exec-cmd" class="btn btn-exec" onclick="ExecCmd">Execute</div>
                        <div class="clear-fix"></div>
                    </div>
                    <div class="chips-bar chips-bar-bottom">
                        <div class="chip-btn" onclick="ChipCmd:echo Hello TAO">echo Hello TAO</div>
                        <div class="chip-btn" onclick="ChipCmd:lua:if SysBar_LaunchPractice then SysBar_LaunchPractice('caldavar', false) else Cmd('Set svr_login false; Set svr_broadcastLocal false; Set ui_quickStart true; Set sv_precacheEntities false; StartGame practice GameName map:caldavar allheroes:true') end">Practice Match</div>
                        <div class="chip-btn" onclick="ChipCmd:AddFakePlayer 1">AddFakePlayer 1</div>
                        <div class="chip-btn" onclick="ChipCmd:AddFakePlayer 2">AddFakePlayer 2</div>
                        <div class="chip-btn" onclick="ChipCmd:lua:UIManager.GetInterface('main')">Lua: UIManager</div>
                        <div class="chip-btn" onclick="ChipCmd:Script collectgarbage()">Script collectgarbage()</div>
                        <div class="chip-btn chip-clear" onclick="ClearConsoleTerminal">Clear Log</div>
                        <div class="clear-fix"></div>
                    </div>
                </div>
            </div>

            <!-- Footer -->
            <div class="console-footer">
                <div class="footer-actions">
                    <div id="btn-refresh-trace" class="btn btn-action" onclick="RefreshTrace">Refresh Trace</div>
                    <div id="btn-self-heal" class="btn btn-action" onclick="SelfHealUi">Self-Heal HUD</div>
                </div>
                <div id="btn-close-console-footer" class="btn btn-secondary" onclick="CloseConsole">Close (Esc)</div>
                <div class="clear-fix"></div>
            </div>
        </div>
    </body>
    </rml>
    )rml2";

    Rml::ElementDocument* consoleDoc = m_context->LoadDocument("assets/hud/tao_console.rml");
    if (!consoleDoc) {
        consoleDoc = m_context->LoadDocumentFromMemory(taoConsoleTemplate);
    }
    if (consoleDoc) {
        consoleDoc->AddEventListener(Rml::EventId::Click, &s_documentClickDispatcher);
        m_taoConsoleVisible = false;
        consoleDoc->Hide();
        m_documents["tao_console"] = consoleDoc;
        HON_LOG_INFO("[OverlayManager] Loaded TAO Diagnostics Console document (hidden by default).");
    }

    // Modern MOTD In-Game Store & News Panoramic Modal Document
    const char* motdTemplate = R"motd(
    <rml>
    <head>
        <title>NEXUS Panoramic News Feed</title>
        <style>
            body { width: 100%; height: 100%; margin: 0; padding: 0; pointer-events: none; font-family: Arial, sans-serif; font-size: 13px; color: #e2e8f0; }
            #motd-window { pointer-events: auto; position: absolute; top: 15%; left: 10%; width: 80%; max-width: 1340px; background-color: rgba(10, 16, 26, 0.96); border-width: 2px; border-color: #0284c7; border-radius: 10px; padding: 16px; box-shadow: 0 16px 36px rgba(0, 0, 0, 0.7); }
            .motd-header { display: flex; justify-content: space-between; align-items: center; border-bottom-width: 1px; border-bottom-color: #1e293b; padding-bottom: 8px; margin-bottom: 14px; }
            .motd-header-title { font-size: 16px; font-weight: bold; color: #38bdf8; letter-spacing: 1px; }
            .motd-close-btn { font-size: 18px; color: #94a3b8; cursor: pointer; padding: 2px 8px; border-radius: 4px; }
            .motd-close-btn:hover { color: #ef4444; background-color: rgba(239, 68, 68, 0.15); }
            .motd-columns { display: flex; flex-direction: row; justify-content: space-between; gap: 14px; }
            .hero-card { flex: 0 0 310px; background-color: rgba(15, 23, 42, 0.9); border-width: 1px; border-color: #991b1b; border-radius: 8px; padding: 12px; display: flex; flex-direction: column; align-items: center; text-align: center; }
            .badge-red { background-color: #b91c1c; color: #ffffff; font-size: 10px; font-weight: bold; padding: 3px 8px; border-radius: 4px; margin-bottom: 10px; }
            .hero-portrait-frame { width: 72px; height: 72px; border-width: 2px; border-color: #ef4444; border-radius: 6px; background-color: #1e1e2f; margin-bottom: 8px; display: flex; justify-content: center; align-items: center; }
            .hero-name { font-size: 16px; font-weight: bold; color: #f8fafc; }
            .hero-subtitle { font-size: 11px; color: #f87171; margin-bottom: 4px; }
            .hero-tags { font-size: 10px; color: #94a3b8; margin-bottom: 8px; }
            .hero-lore { font-size: 10px; color: #cbd5e1; background-color: rgba(30, 41, 59, 0.6); border-radius: 4px; padding: 6px; margin-bottom: 10px; }
            .abilities-row { display: flex; gap: 6px; justify-content: center; margin-bottom: 12px; }
            .ability-box { width: 36px; height: 36px; border-width: 1px; border-color: #ef4444; border-radius: 4px; background-color: #0f172a; }
            .ability-box.ult { border-color: #fbbf24; }
            .hero-btn-row { display: flex; gap: 8px; width: 100%; }
            .btn-hero { flex: 1; background-color: #991b1b; color: #ffffff; padding: 6px; border-radius: 4px; font-size: 11px; font-weight: bold; cursor: pointer; text-align: center; }
            .btn-hero:hover { background-color: #dc2626; }
            .carousel-card { flex: 1 1 auto; background-color: rgba(15, 23, 42, 0.9); border-width: 1px; border-color: #0284c7; border-radius: 8px; padding: 14px; display: flex; flex-direction: column; }
            .badge-blue { background-color: #0284c7; color: #ffffff; font-size: 10px; font-weight: bold; padding: 3px 8px; border-radius: 4px; align-self: flex-start; margin-bottom: 8px; }
            .carousel-title { font-size: 18px; font-weight: bold; color: #38bdf8; }
            .carousel-subtitle { font-size: 11px; color: #bae6fd; margin-bottom: 10px; }
            .feature-bullets { display: flex; flex-direction: column; gap: 6px; margin-bottom: 12px; }
            .feature-bullet { background-color: rgba(3, 105, 161, 0.15); border-width: 1px; border-color: #0369a1; border-radius: 4px; padding: 8px; font-size: 11px; color: #cbd5e1; }
            .carousel-nav-row { display: flex; justify-content: center; align-items: center; gap: 8px; margin-top: auto; }
            .carousel-arrow { width: 22px; height: 22px; background-color: rgba(30, 41, 59, 0.8); border-width: 1px; border-color: #0284c7; border-radius: 4px; color: #38bdf8; font-size: 13px; font-weight: bold; cursor: pointer; text-align: center; line-height: 18px; }
            .carousel-arrow:hover { background-color: #0284c7; color: #ffffff; }
            .dot { width: 8px; height: 8px; border-radius: 4px; background-color: #334155; cursor: pointer; }
            .dot.active { background-color: #38bdf8; width: 20px; }
            .btn-carousel { background-color: #0284c7; color: #ffffff; padding: 6px; border-radius: 4px; font-size: 11px; font-weight: bold; cursor: pointer; text-align: center; margin-top: 8px; }
            .btn-carousel:hover { background-color: #0369a1; }
            .vault-card { flex: 0 0 310px; background-color: rgba(15, 23, 42, 0.9); border-width: 1px; border-color: #d97706; border-radius: 8px; padding: 12px; display: flex; flex-direction: column; text-align: center; }
            .badge-gold { background-color: #b45309; color: #ffffff; font-size: 10px; font-weight: bold; padding: 3px 8px; border-radius: 4px; align-self: center; margin-bottom: 10px; }
            .vault-title { font-size: 13px; font-weight: bold; color: #fde68a; }
            .vault-subtitle { font-size: 10px; color: #f59e0b; margin-bottom: 10px; }
            .deal-item { background-color: rgba(30, 41, 59, 0.6); border-width: 1px; border-color: #475569; border-radius: 6px; padding: 8px; margin-bottom: 8px; text-align: left; cursor: pointer; }
            .deal-item:hover { border-color: #f59e0b; background-color: rgba(45, 60, 85, 0.8); }
            .deal-name { font-size: 11px; font-weight: bold; color: #38bdf8; }
            .deal-price { font-size: 11px; font-weight: bold; color: #fbbf24; }
            .deal-desc { font-size: 9px; color: #94a3b8; margin-top: 2px; }
            .btn-vault { background-color: #d97706; color: #ffffff; padding: 6px; border-radius: 4px; font-size: 11px; font-weight: bold; cursor: pointer; margin-top: auto; text-align: center; }
            .btn-vault:hover { background-color: #f59e0b; }
        </style>
    </head>
    <body>
        <div id="motd-window">
            <div class="motd-header">
                <span class="motd-header-title">NEXUS PANORAMIC NEWS (RmlUi Modern Engine)</span>
                <span class="motd-close-btn" id="btn-close-motd" onclick="Dismiss">&#215;</span>
            </div>
            <div class="motd-columns">
                <div class="hero-card">
                    <span class="badge-red">NEW HERO SPOTLIGHT</span>
                    <div class="hero-portrait-frame">
                        <img src="/heroes/gwynplaine/icon.tga" width="64" height="64"/>
                    </div>
                    <div class="hero-name" id="hero-name">Gwynplaine</div>
                    <div class="hero-subtitle" id="hero-subtitle">The Man Who Laughs</div>
                    <div class="hero-tags" id="hero-tags">Agility / Magic Assassin</div>
                    <div class="hero-lore" id="hero-lore">
                        Gwynplaine flings ancient runic chain scythes across the battlefield, reeling in victims with Runic Tether.
                    </div>
                    <div class="abilities-row">
                        <div class="ability-box"><img src="/heroes/gwynplaine/ability_01/icon.tga" width="34" height="34"/></div>
                        <div class="ability-box"><img src="/heroes/gwynplaine/ability_02/icon.tga" width="34" height="34"/></div>
                        <div class="ability-box"><img src="/heroes/gwynplaine/ability_03/icon.tga" width="34" height="34"/></div>
                        <div class="ability-box ult"><img src="/heroes/gwynplaine/ability_04/icon.tga" width="34" height="34"/></div>
                    </div>
                    <div class="hero-btn-row">
                        <div class="btn-hero" onclick="PracticeHero">Practice Hero</div>
                    </div>
                </div>
                <div class="carousel-card">
                    <span class="badge-blue" id="slide-badge">LEGENDARY AVATAR</span>
                    <div class="carousel-title" id="slide-title">Abyssal Bubbles</div>
                    <div class="carousel-subtitle" id="slide-subtitle">Deep-Sea Trench Leviathan * Bioluminescent Chitin</div>
                    <div class="feature-bullets">
                        <div class="feature-bullet" id="bullet-1">* Bioluminescent Ability Spell Effects (Shell Surf &amp; Kelp Field)</div>
                        <div class="feature-bullet" id="bullet-2">* Atmospheric Deep-Sea Soundscape &amp; Voice</div>
                        <div class="feature-bullet" id="bullet-3">* Animated 3D Store Pedestal &amp; Kill Celebrations</div>
                    </div>
                    <div class="carousel-nav-row">
                        <div class="carousel-arrow" id="btn-prev-slide" onclick="PrevSlide">&lt;</div>
                        <div class="dot active" id="dot-0" onclick="SelectSlide:0"></div>
                        <div class="dot" id="dot-1" onclick="SelectSlide:1"></div>
                        <div class="dot" id="dot-2" onclick="SelectSlide:2"></div>
                        <div class="dot" id="dot-3" onclick="SelectSlide:3"></div>
                        <div class="carousel-arrow" id="btn-next-slide" onclick="NextSlide">&gt;</div>
                    </div>
                    <div class="btn-carousel" id="slide-action-btn" onclick="SlideAction">View In Store</div>
                </div>
                <div class="vault-card">
                    <span class="badge-gold">MERRICK'S SECRET VAULT</span>
                    <div class="vault-title">DAILY FEATURED DEALS</div>
                    <div class="vault-subtitle">50% Off Exclusive Bundles * Rotates 00:00 UTC</div>
                    <div class="deal-item" id="deal-1" onclick="ViewDeal:1">
                        <div class="deal-name" id="deal-1-title">Abyssal Bubbles Bundle</div>
                        <div class="deal-price" id="deal-1-price">390 Gold [ 40% OFF ]</div>
                        <div class="deal-desc" id="deal-1-desc">Includes Abyssal Bubbles avatar, deep-sea ward, and voicepack.</div>
                    </div>
                    <div class="deal-item" id="deal-2" onclick="ViewDeal:2">
                        <div class="deal-name" id="deal-2-title">Gwynplaine Launch Pack</div>
                        <div class="deal-price" id="deal-2-price">3000 Silver [ NEW HERO ]</div>
                        <div class="deal-desc" id="deal-2-desc">Unlock Gwynplaine plus his terrifying Crimson Grin alternate avatar.</div>
                    </div>
                    <div class="btn-vault" id="btn-visit-store" onclick="OpenStore">Visit Store</div>
                </div>
            </div>
        </div>
    </body>
    </rml>
    )motd";

    Rml::ElementDocument* motdDoc = m_context->LoadDocument("assets/hud/motd.rml");
    if (!motdDoc) {
        motdDoc = m_context->LoadDocumentFromMemory(motdTemplate);
    }
    if (motdDoc) {
        motdDoc->AddEventListener(Rml::EventId::Click, &s_documentClickDispatcher);
        m_motdVisible = false;
        motdDoc->Hide();
        m_documents["motd"] = motdDoc;
        HON_LOG_INFO("[OverlayManager] Loaded MOTD News & Store modal document (hidden by default).");
        HydrateMotdUI();
    }
}

void OverlayManager::ReloadDocuments() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (!m_context) return;
    bool wasDevTools = m_devtoolsVisible;
    bool wasTaoConsole = m_taoConsoleVisible;
    bool wasMotd = m_motdVisible;

    for (auto& pair : m_documents) {
        if (pair.second) {
            pair.second->Close();
        }
    }
    m_documents.clear();
    m_cachedElementRml.clear();
    m_cachedElementClass.clear();
    m_lastRenderedLogGen = 0;
    m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);

    LoadDocuments();

    if (wasDevTools) SetDocumentVisible("devtools", true);
    if (wasTaoConsole) SetDocumentVisible("tao_console", true);
    if (wasMotd) SetDocumentVisible("motd", true);
    HON_LOG_INFO("[OverlayManager] Reloaded all HUD documents from disk.");
}

void OverlayManager::SetDocumentVisible(const std::string& docId, bool visible) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    std::string key = (docId == "shop") ? "tao_console" : docId;
    auto it = m_documents.find(key);
    if (it != m_documents.end() && it->second) {
        if (visible) m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);
        if (key == "devtools") m_devtoolsVisible = visible;
        if (key == "tao_console") m_taoConsoleVisible = visible;
        if (key == "motd") {
            m_motdVisible = visible;
            if (visible) {
                m_lastSlideAdvanceTick = GetTickCount64();
                UpdateMotdUI();
            }
        }
        if (visible) {
            it->second->Show();
        } else {
            it->second->Hide();
            if (m_context) {
                m_context->UnfocusDocument(it->second);
            }
        }
        HON_LOG_INFO("[OverlayManager] Document '%s' visibility set to %d.", key.c_str(), visible ? 1 : 0);
    }
}

void OverlayManager::ToggleDocument(const std::string& docId) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    std::string key = (docId == "shop") ? "tao_console" : docId;
    auto it = m_documents.find(key);
    if (it != m_documents.end() && it->second) {
        bool newState = !it->second->IsVisible();
        if (newState) m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);
        if (key == "devtools") m_devtoolsVisible = newState;
        if (key == "tao_console") m_taoConsoleVisible = newState;
        if (key == "motd") {
            m_motdVisible = newState;
            if (newState) {
                m_lastSlideAdvanceTick = GetTickCount64();
                UpdateMotdUI();
            }
        }
        if (newState) {
            it->second->Show();
        } else {
            it->second->Hide();
            if (m_context) {
                m_context->UnfocusDocument(it->second);
            }
        }
        HON_LOG_INFO("[OverlayManager] Document '%s' toggled to %d.", key.c_str(), newState ? 1 : 0);
    }
}

bool OverlayManager::IsDocumentVisible(const std::string& docId) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    std::string key = (docId == "shop") ? "tao_console" : docId;
    auto it = m_documents.find(key);
    return (it != m_documents.end() && it->second && it->second->IsVisible());
}

void OverlayManager::SwitchTab(const std::string& tabId) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);

    std::string targetPane = tabId;
    if (targetPane.rfind("tab-btn-", 0) == 0) {
        targetPane = "tab-" + targetPane.substr(8);
    } else if (targetPane.rfind("devtab-btn-", 0) == 0) {
        targetPane = "devtab-" + targetPane.substr(11);
    }

    const std::vector<std::pair<std::string, std::string>> devTabs = {
        { "devtab-btn-perf",      "devtab-perf" },
        { "devtab-btn-audio",     "devtab-audio" },
        { "devtab-btn-inspect",   "devtab-inspect" },
        { "devtab-btn-spectator", "devtab-spectator" },
        { "devtab-btn-actions",   "devtab-actions" }
    };

    const std::vector<std::pair<std::string, std::string>> taoTabs = {
        { "tab-btn-overview", "tab-overview" },
        { "tab-btn-fleet",    "tab-fleet" },
        { "tab-btn-vulkan",   "tab-vulkan" },
        { "tab-btn-vfs",      "tab-vfs" },
        { "tab-btn-console",  "tab-console" }
    };

    // 1. Try devtools document
    auto devIt = m_documents.find("devtools");
    if (devIt != m_documents.end() && devIt->second) {
        Rml::ElementDocument* doc = devIt->second;
        bool devMatched = false;
        for (const auto& [btnId, paneId] : devTabs) {
            if (paneId == targetPane) devMatched = true;
        }
        if (devMatched) {
            for (const auto& [btnId, paneId] : devTabs) {
                bool isActive = (paneId == targetPane);
                auto elBtn = doc->GetElementById(btnId);
                if (elBtn) elBtn->SetClassNames(isActive ? "tab-btn active" : "tab-btn");
                auto elPane = doc->GetElementById(paneId);
                if (elPane) {
                    elPane->SetProperty("display", isActive ? "block" : "none");
                    elPane->SetClassNames(isActive ? "tab-pane active" : "tab-pane");
                }
            }
            m_activeTab = targetPane;
            m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);
            HON_LOG_INFO("[OverlayManager] Switched active DevTools tab to '%s'.", m_activeTab.c_str());
            return;
        }
    }

    // 2. Try tao_console document
    auto taoIt = m_documents.find("tao_console");
    if (taoIt != m_documents.end() && taoIt->second) {
        Rml::ElementDocument* doc = taoIt->second;
        bool taoMatched = false;
        for (const auto& [btnId, paneId] : taoTabs) {
            if (paneId == targetPane) taoMatched = true;
        }
        if (taoMatched) {
            for (const auto& [btnId, paneId] : taoTabs) {
                bool isActive = (paneId == targetPane);
                auto elBtn = doc->GetElementById(btnId);
                if (elBtn) elBtn->SetClassNames(isActive ? "tab-btn active" : "tab-btn");
                auto elPane = doc->GetElementById(paneId);
                if (elPane) {
                    elPane->SetProperty("display", isActive ? "block" : "none");
                    elPane->SetClassNames(isActive ? "tab-pane active" : "tab-pane");
                }
            }
            m_activeTab = targetPane;
            m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);
            HON_LOG_INFO("[OverlayManager] Switched active TAO Console tab to '%s'.", m_activeTab.c_str());
            return;
        }
    }

    HON_LOG_WARN("[OverlayManager] SwitchTab: Unknown tab '%s'.", tabId.c_str());
}

std::string OverlayManager::GetActiveTab() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    return m_activeTab;
}

int OverlayManager::GetMotdCurrentSlide() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    return m_motdCurrentSlide;
}

void OverlayManager::ExecuteConsoleInput() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    auto taoIt = m_documents.find("tao_console");
    if (taoIt == m_documents.end() || !taoIt->second) return;

    Rml::ElementDocument* doc = taoIt->second;
    auto elInput = doc->GetElementById("tao-input-cmd");
    if (!elInput) return;

    auto formInput = dynamic_cast<Rml::ElementFormControlInput*>(elInput);
    if (!formInput) return;

    std::string cmd = formInput->GetValue();
    while (!cmd.empty() && (cmd.front() == ' ' || cmd.front() == '\t')) cmd.erase(cmd.begin());
    while (!cmd.empty() && (cmd.back() == ' ' || cmd.back() == '\t' || cmd.back() == '\r' || cmd.back() == '\n')) cmd.pop_back();

    if (cmd.empty()) return;

    if (m_cmdHistory.empty() || m_cmdHistory.back() != cmd) {
        m_cmdHistory.push_back(cmd);
        if (m_cmdHistory.size() > 50) {
            m_cmdHistory.erase(m_cmdHistory.begin());
        }
    }
    m_cmdHistoryIdx = -1;

    formInput->SetValue("");

    ExecuteCommandDirect(cmd);
}

void OverlayManager::ExecuteCommandDirect(const std::string& cmd) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    HON_LOG_INFO("[OverlayManager] ExecuteCommandDirect: '%s'", cmd.c_str());

    g_OverlayBridge.PushLogEntry("INFO", "EXEC", "> " + cmd);
    g_OverlayBridge.ExecuteCommand(cmd);

    auto taoIt = m_documents.find("tao_console");
    if (taoIt != m_documents.end() && taoIt->second) {
        auto elTerm = taoIt->second->GetElementById("tao-console-terminal");
        if (elTerm) {
            std::string curRml = elTerm->GetInnerRML();
            curRml += "<div class=\"log-line log-info\">[USER][EXEC] &gt; " + EscapeRmlText(cmd) + "</div>";
            curRml += "<div class=\"log-line log-ok\">[OK][OUTPUT] Dispatched to engine pipeline successfully.</div>";
            elTerm->SetInnerRML(curRml.c_str());
            elTerm->SetScrollTop(100000.0f);
        }
    }
}

void OverlayManager::SetConsoleInputValue(const std::string& val) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    auto taoIt = m_documents.find("tao_console");
    if (taoIt == m_documents.end() || !taoIt->second) return;

    auto elInput = taoIt->second->GetElementById("tao-input-cmd");
    if (!elInput) return;

    auto formInput = dynamic_cast<Rml::ElementFormControlInput*>(elInput);
    if (formInput) {
        formInput->SetValue(val.c_str());
    }
}

void OverlayManager::ClearConsoleTerminal() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    auto taoIt = m_documents.find("tao_console");
    if (taoIt == m_documents.end() || !taoIt->second) return;

    auto elTerm = taoIt->second->GetElementById("tao-console-terminal");
    if (elTerm) {
        elTerm->SetInnerRML("<div class=\"log-line log-info\">[TAO EVAL] Console terminal log cleared.</div>");
    }
}

void OverlayManager::NavigateCommandHistory(int direction) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (m_cmdHistory.empty()) return;

    if (direction < 0) { // Up arrow (older)
        if (m_cmdHistoryIdx == -1) {
            m_cmdHistoryIdx = static_cast<int>(m_cmdHistory.size()) - 1;
        } else if (m_cmdHistoryIdx > 0) {
            m_cmdHistoryIdx--;
        }
    } else { // Down arrow (newer)
        if (m_cmdHistoryIdx >= 0) {
            m_cmdHistoryIdx++;
            if (m_cmdHistoryIdx >= static_cast<int>(m_cmdHistory.size())) {
                m_cmdHistoryIdx = -1;
                SetConsoleInputValue("");
                return;
            }
        }
    }

    if (m_cmdHistoryIdx >= 0 && m_cmdHistoryIdx < static_cast<int>(m_cmdHistory.size())) {
        SetConsoleInputValue(m_cmdHistory[m_cmdHistoryIdx]);
    }
}

void OverlayManager::UpdateTelemetryUI() {
    LARGE_INTEGER currentQpc;
    QueryPerformanceCounter(&currentQpc);

    double dt = static_cast<double>(currentQpc.QuadPart - m_lastFrameQpc.QuadPart) / static_cast<double>(m_qpcFrequency.QuadPart);
    m_lastFrameQpc = currentQpc;

    if (dt > 0.0001 && dt < 1.0) {
        double currentFps = 1.0 / dt;
        double currentFtMs = dt * 1000.0;
        m_smoothedFps = 0.95 * m_smoothedFps + 0.05 * currentFps;
        m_smoothedFrametimeMs = 0.95 * m_smoothedFrametimeMs + 0.05 * currentFtMs;
    }

    g_OverlayBridge.SetMetric("fps", m_smoothedFps);
    g_OverlayBridge.SetMetric("frametime_ms", m_smoothedFrametimeMs);

#pragma pack(push, 8)
    struct AudioTelemetryData {
        int32_t outputType;
        int32_t speakerMode;
        int32_t bufferLength;
        int32_t numBuffers;
        int32_t wasapiActive;
        int32_t hrtfActive;
        float   latencyMs;
        float   listenerPos[3];
        float   listenerFwd[3];
        int32_t activeVoices;
        int32_t peakVoices;
        int32_t maxChannelsConfigured;
    };
#pragma pack(pop)

    typedef void (*PFN_Kongor_Audio_GetStatus)(AudioTelemetryData* outTelemetry);
    static PFN_Kongor_Audio_GetStatus s_pfnAudioGetStatus = nullptr;
    static bool s_audioResolved = false;

    if (!s_audioResolved) {
        HMODULE hFmod = GetModuleHandleW(L"fmodex64.dll");
        if (hFmod) {
            s_pfnAudioGetStatus = (PFN_Kongor_Audio_GetStatus)GetProcAddress(hFmod, "Kongor_Audio_GetStatus");
            if (s_pfnAudioGetStatus) s_audioResolved = true;
        }
    }

    AudioTelemetryData at = {};
    const bool forceRefresh = m_forceTelemetryRefresh.exchange(false, std::memory_order_relaxed);
    bool hasAudio = false;
    if (s_pfnAudioGetStatus) {
        s_pfnAudioGetStatus(&at);
        hasAudio = true;
    }

    auto it = m_documents.find("devtools");
    if (it != m_documents.end() && it->second && it->second->IsVisible()) {
        static LARGE_INTEGER s_lastDevtoolsUpdateQpc = { 0 };
        double devtoolsElapsed = 0.0;
        if (s_lastDevtoolsUpdateQpc.QuadPart > 0 && m_qpcFrequency.QuadPart > 0) {
            devtoolsElapsed = static_cast<double>(currentQpc.QuadPart - s_lastDevtoolsUpdateQpc.QuadPart) / static_cast<double>(m_qpcFrequency.QuadPart);
        }

        if (forceRefresh || s_lastDevtoolsUpdateQpc.QuadPart == 0 || devtoolsElapsed >= 0.20) {
            s_lastDevtoolsUpdateQpc = currentQpc;
            if (IsTabPaneVisible(it->second, "devtab-perf")) {
                std::stringstream ssFps, ssFt, ssRes, ssRam;
            ssFps << std::fixed << std::setprecision(1) << m_smoothedFps << " FPS";
            ssFt << std::fixed << std::setprecision(2) << m_smoothedFrametimeMs << " ms";
            ssRes << m_width << "x" << m_height << " (B8G8R8A8)";

            PROCESS_MEMORY_COUNTERS pmc;
            if (GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc))) {
                ssRam << (pmc.WorkingSetSize / (1024 * 1024)) << " MB";
            } else {
                ssRam << "< 450 MB";
            }

            auto elFps = it->second->GetElementById("metric-fps");
            SetInnerRmlIfChanged(elFps, ssFps.str());

            auto elFt = it->second->GetElementById("metric-ft");
            SetInnerRmlIfChanged(elFt, ssFt.str());

            auto elRes = it->second->GetElementById("metric-res");
            SetInnerRmlIfChanged(elRes, ssRes.str());

            auto elRam = it->second->GetElementById("metric-ram");
                SetInnerRmlIfChanged(elRam, ssRam.str());
            }

            // Audio Tab Metrics
            if (hasAudio) {
                auto elVoices = it->second->GetElementById("audio-stat-voices");
            if (IsTabPaneVisible(it->second, "devtab-audio")) {
                if (elVoices) {
                    std::string v = std::to_string(at.activeVoices) + " / " + std::to_string(at.maxChannelsConfigured) + " Voices (Peak: " + std::to_string(at.peakVoices) + ")";
                    SetInnerRmlIfChanged(elVoices, v);
                }

                auto elLat = it->second->GetElementById("audio-stat-lat");
                if (elLat) {
                    std::ostringstream ssLat;
                    ssLat << std::fixed << std::setprecision(2) << at.latencyMs << " ms (" << at.bufferLength << " smp x " << at.numBuffers << " buf)";
                    SetInnerRmlIfChanged(elLat, ssLat.str());
                }

                auto elHrtf = it->second->GetElementById("audio-stat-hrtf");
                if (elHrtf) {
                    SetInnerRmlIfChanged(elHrtf, at.hrtfActive ? "7.1 Windows Spatial Audio (HRTF)" : "Stereo Fallback");
                }

                auto elPos = it->second->GetElementById("audio-stat-pos");
                if (elPos) {
                    std::ostringstream ssPos;
                    ssPos << "X: " << std::fixed << std::setprecision(1) << at.listenerPos[0]
                          << ", Y: " << at.listenerPos[1]
                          << ", Z: " << at.listenerPos[2];
                    SetInnerRmlIfChanged(elPos, ssPos.str());
                }

                auto elBar = it->second->GetElementById("audio-meter-bar");
                if (elBar) {
                    int pct = 0;
                    if (at.maxChannelsConfigured > 0) {
                        pct = (at.activeVoices * 100) / at.maxChannelsConfigured;
                        if (pct > 100) pct = 100;
                        if (pct < 5 && at.activeVoices > 0) pct = 5;
                    }
                    std::string widthProp = std::to_string(pct) + "%";
                    if (m_cachedElementRml[elBar] != widthProp) {
                        m_cachedElementRml[elBar] = widthProp;
                        elBar->SetProperty("width", widthProp);
                    }
                }
                }
            }
        }
    }

    auto taoIt = m_documents.find("tao_console");
    if (taoIt != m_documents.end() && taoIt->second && taoIt->second->IsVisible()) {
        static LARGE_INTEGER s_lastTaoUpdateQpc = { 0 };
        double taoUpdateElapsed = 0.0;
        if (s_lastTaoUpdateQpc.QuadPart > 0 && m_qpcFrequency.QuadPart > 0) {
            taoUpdateElapsed = static_cast<double>(currentQpc.QuadPart - s_lastTaoUpdateQpc.QuadPart) / static_cast<double>(m_qpcFrequency.QuadPart);
        }

        // Throttle DOM updates to 5 Hz (every 200ms) to ensure zero impact on high FPS rendering
        if (forceRefresh || s_lastTaoUpdateQpc.QuadPart == 0 || taoUpdateElapsed >= 0.20) {
            s_lastTaoUpdateQpc = currentQpc;
            SubsystemTelemetrySnapshot snap = g_OverlayBridge.GetSnapshot();

            // 1. Service Badges
            auto updateBadge = [&](const char* dotId, const char* textId, bool online, const std::string& label) {
                auto elDot = taoIt->second->GetElementById(dotId);
                SetClassNamesIfChanged(elDot, online ? "badge-dot dot-green" : "badge-dot dot-red");
                auto elText = taoIt->second->GetElementById(textId);
                SetInnerRmlIfChanged(elText, label);
            };

            updateBadge("dot-master", "text-master", snap.masterOnline,
                snap.masterOnline ? "Master: 55555 (OK)" : "Master: 55555 (OFFLINE)");

            std::string chatText = "Chat: " + std::to_string(snap.chatPort) + (snap.chatOnline ? " (OK)" : " (OFFLINE)");
            updateBadge("dot-chat", "text-chat", snap.chatOnline, chatText);

            std::string gsText = "Game Server: " + std::to_string(snap.gameServerPort) + (snap.gameServerOnline ? " (20Hz)" : " (OFFLINE)");
            updateBadge("dot-gameserver", "text-gameserver", snap.gameServerOnline, gsText);

            auto elDotNet = taoIt->second->GetElementById("dot-network");
            if (elDotNet) {
                if (snap.packetLossPercent > 5.0f) SetClassNamesIfChanged(elDotNet, "badge-dot dot-red");
                else if (snap.pingMs > 100 || snap.packetLossPercent > 1.0f) SetClassNamesIfChanged(elDotNet, "badge-dot dot-amber");
                else SetClassNamesIfChanged(elDotNet, "badge-dot dot-green");
            }
            auto elTextNet = taoIt->second->GetElementById("text-network");
            if (elTextNet) {
                std::ostringstream ssNet;
                ssNet << "Network: " << snap.pingMs << "ms / " << std::fixed << std::setprecision(1) << snap.packetLossPercent << "% Loss";
                SetInnerRmlIfChanged(elTextNet, ssNet.str());
            }

            auto elDotEng = taoIt->second->GetElementById("dot-engine");
            SetClassNamesIfChanged(elDotEng, "badge-dot dot-green");
            auto elTextEng = taoIt->second->GetElementById("text-engine");
            if (elTextEng) {
                std::string engText = "Engine: Vulkan 1.3 (" + std::to_string(static_cast<int>(m_smoothedFps)) + " FPS)";
                SetInnerRmlIfChanged(elTextEng, engText);
            }

            // 2. Telemetry Rows
            if (IsTabPaneVisible(taoIt->second, "tab-overview")) {
            auto elIpc = taoIt->second->GetElementById("tao-stat-ipc");
            if (elIpc) {
                SetInnerRmlIfChanged(elIpc, snap.ipcConnected ? "\\\\.\\pipe\\willowmaker-ipc (ACTIVE)" : "\\\\.\\pipe\\willowmaker-ipc (OFFLINE)");
            }

            std::stringstream ssRes, ssFps, ssNetQoS, ssTick, ssAudio;
            ssRes << m_width << "x" << m_height << " (B8G8R8A8)";
            auto elRes = taoIt->second->GetElementById("tao-stat-res");
            SetInnerRmlIfChanged(elRes, ssRes.str());

            ssFps << std::fixed << std::setprecision(1) << m_smoothedFps << " FPS (" << std::fixed << std::setprecision(2) << m_smoothedFrametimeMs << " ms)";
            auto elFps = taoIt->second->GetElementById("tao-stat-fps");
            SetInnerRmlIfChanged(elFps, ssFps.str());

            ssNetQoS << snap.pingMs << " ms (" << std::fixed << std::setprecision(1) << snap.packetLossPercent << "% loss)";
            auto elNetQoS = taoIt->second->GetElementById("tao-stat-net");
            SetInnerRmlIfChanged(elNetQoS, ssNetQoS.str());

            ssTick << "Tick: " << snap.serverTick << " | Draw: " << snap.drawCalls;
            auto elTick = taoIt->second->GetElementById("tao-stat-tick");
            SetInnerRmlIfChanged(elTick, ssTick.str());

            auto elVfs = taoIt->second->GetElementById("tao-stat-vfs");
            if (elVfs) {
                std::string vfsStr = "14 Mounted (" + std::to_string(snap.vfsLooseFiles) + " Loose)";
                SetInnerRmlIfChanged(elVfs, vfsStr);
            }

            if (hasAudio) {
                ssAudio << "WASAPI (" << at.activeVoices << "/" << at.maxChannelsConfigured << " Voices)";
            } else {
                ssAudio << "WASAPI (" << snap.audioVoices << " Voices)";
            }
            auto elAudio = taoIt->second->GetElementById("tao-stat-audio");
            SetInnerRmlIfChanged(elAudio, ssAudio.str());

            auto elFault = taoIt->second->GetElementById("tao-stat-fault");
            if (elFault) {
                std::string safeFault = EscapeRmlText(snap.activeFaultDescription);
                SetInnerRmlIfChanged(elFault, safeFault);
                if (snap.faultSeverity >= 2) SetClassNamesIfChanged(elFault, "stat-val font-danger");
                else if (snap.faultSeverity == 1) SetClassNamesIfChanged(elFault, "stat-val font-warn");
                else SetClassNamesIfChanged(elFault, "stat-val font-accent");
            }

            // 2b. Tab 2 (Cluster Fleet) & Tab 3 (Vulkan & Memory) Live Telemetry
            }

            if (IsTabPaneVisible(taoIt->second, "tab-fleet")) {
            auto elFleetMaster = taoIt->second->GetElementById("fleet-master-val");
            if (elFleetMaster) {
                SetInnerRmlIfChanged(elFleetMaster, snap.masterOnline ? "127.0.0.1:55555 (ONLINE)" : "127.0.0.1:55555 (OFFLINE)");
                SetClassNamesIfChanged(elFleetMaster, snap.masterOnline ? "stat-val font-accent" : "stat-val font-danger");
            }
            auto elFleetChat = taoIt->second->GetElementById("fleet-chat-val");
            if (elFleetChat) {
                std::string fc = "127.0.0.1:" + std::to_string(snap.chatPort) + (snap.chatOnline ? " (ONLINE)" : " (OFFLINE)");
                SetInnerRmlIfChanged(elFleetChat, fc);
                SetClassNamesIfChanged(elFleetChat, snap.chatOnline ? "stat-val font-accent" : "stat-val font-danger");
            }
            auto elFleetServer = taoIt->second->GetElementById("fleet-server-val");
            if (elFleetServer) {
                std::string fs = "Port " + std::to_string(snap.gameServerPort) + (snap.gameServerOnline ? " (20 Hz | ONLINE)" : " (OFFLINE)");
                SetInnerRmlIfChanged(elFleetServer, fs);
                SetClassNamesIfChanged(elFleetServer, snap.gameServerOnline ? "stat-val font-blue" : "stat-val font-warn");
            }

            }

            if (IsTabPaneVisible(taoIt->second, "tab-vulkan")) {
            auto elVkExtent = taoIt->second->GetElementById("vk-stat-extent");
            if (elVkExtent) {
                std::ostringstream ssVkExt;
                ssVkExt << m_width << "x" << m_height << " (B8G8R8A8_UNORM)";
                SetInnerRmlIfChanged(elVkExtent, ssVkExt.str());
            }
            auto elVkRam = taoIt->second->GetElementById("vk-stat-ram");
            if (elVkRam) {
                PROCESS_MEMORY_COUNTERS taoPmc;
                std::ostringstream ssVkRam;
                if (GetProcessMemoryInfo(GetCurrentProcess(), &taoPmc, sizeof(taoPmc))) {
                    ssVkRam << (taoPmc.WorkingSetSize / (1024 * 1024)) << " MB (WorkingSet64)";
                } else {
                    ssVkRam << snap.workingSetMb << " MB (WorkingSet64)";
                }
                SetInnerRmlIfChanged(elVkRam, ssVkRam.str());
            }
            auto elVkFt = taoIt->second->GetElementById("vk-stat-ft");
            if (elVkFt) {
                std::ostringstream ssVkFt;
                ssVkFt << std::fixed << std::setprecision(2) << m_smoothedFrametimeMs << " ms (Target &lt;= 6.9 ms)";
                SetInnerRmlIfChanged(elVkFt, ssVkFt.str());
                SetClassNamesIfChanged(elVkFt, m_smoothedFrametimeMs <= 8.5 ? "stat-val font-accent" : "stat-val font-warn");
            }

            }

            auto elSelfHealBtn = taoIt->second->GetElementById("btn-self-heal");
            if (elSelfHealBtn) {
                if (snap.faultSeverity >= 2) {
                    SetInnerRmlIfChanged(elSelfHealBtn, "Auto-Remediate (Critical)");
                    SetClassNamesIfChanged(elSelfHealBtn, "btn btn-action btn-danger");
                } else if (snap.faultSeverity == 1) {
                    SetInnerRmlIfChanged(elSelfHealBtn, "Auto-Remediate (Warning)");
                    SetClassNamesIfChanged(elSelfHealBtn, "btn btn-action btn-warn");
                } else {
                    SetInnerRmlIfChanged(elSelfHealBtn, "Self-Heal HUD");
                    SetClassNamesIfChanged(elSelfHealBtn, "btn btn-action");
                }
            }

            // 3. Recent Engine & Layer Log Stream (XML-escaped so '<div ...>' in logs never triggers RmlUi parse errors)
            const uint64_t currentLogGen = g_OverlayBridge.GetLogGeneration();
            auto elLogTerm = (IsTabPaneVisible(taoIt->second, "tab-overview") && (forceRefresh || currentLogGen != m_lastRenderedLogGen))
                ? taoIt->second->GetElementById("tao-log-terminal")
                : nullptr;
            if (elLogTerm) {
                m_lastRenderedLogGen = currentLogGen;
                std::vector<OverlayLogEntry> logs = g_OverlayBridge.GetRecentLogs(16);
                std::ostringstream ssLogs;
                for (const auto& entry : logs) {
                    std::string cssClass = "log-line log-info";
                    if (entry.level == "OK") cssClass = "log-line log-ok";
                    else if (entry.level == "WARN") cssClass = "log-line log-warn";
                    else if (entry.level == "ERROR") cssClass = "log-line log-error";
                    if (entry.subsystem == "HEAL") cssClass += " log-heal";

                    ssLogs << "<div class=\"" << cssClass << "\">[" << EscapeRmlText(entry.timestamp) << "]["
                           << EscapeRmlText(entry.subsystem) << "] " << EscapeRmlText(entry.message) << "</div>";
                }
                if (!logs.empty()) {
                    SetInnerRmlIfChanged(elLogTerm, ssLogs.str());
                }
            }

            // 4. Visual Trace Matrix QR updates disabled for animation smoothness
        }
    }
}

void OverlayManager::EnqueueUiTask(std::function<void()> task) {
    if (!task) return;
    std::lock_guard<std::mutex> lock(m_uiTaskMutex);
    if (m_pendingUiTasks.size() >= 128) {
        m_pendingUiTasks.pop_front();
    }
    m_pendingUiTasks.push_back(std::move(task));
    m_hasPendingUiTasks.store(true, std::memory_order_release);
}

void OverlayManager::DrainPendingUiTasks() {
    if (!m_hasPendingUiTasks.load(std::memory_order_acquire)) {
        return;
    }

    std::deque<std::function<void()>> batch;
    {
        std::lock_guard<std::mutex> lock(m_uiTaskMutex);
        m_hasPendingUiTasks.store(false, std::memory_order_release);
        if (m_pendingUiTasks.empty()) {
            return;
        }
        batch.swap(m_pendingUiTasks);
    }

    for (auto& fn : batch) {
        if (fn) {
            fn();
        }
    }
}

bool OverlayManager::UpdateAndRender(
    VkQueue queue,
    uint32_t imageIndex,
    uint32_t waitSemaphoreCount,
    const VkSemaphore* pWaitSemaphores,
    VkSemaphore* pOutWaitSemaphore)
{
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);

    if (pOutWaitSemaphore) {
        *pOutWaitSemaphore = VK_NULL_HANDLE;
    }

    // Drain pipe-queued engine console commands AND cross-thread RmlUi DOM tasks on the render thread even if documents are hidden.
    g_OverlayBridge.DrainPendingEngineCommands();
    DrainPendingUiTasks();

    if (!m_isInitialized || !m_isEnabled || !m_context || m_frameResources.empty()) {
        return false;
    }

    // Lazy attach input hook if game window became visible after swapchain creation
    if (ShouldAttemptInputHook()) {
        m_lastInputHookAttemptTick.store(GetTickCount64(), std::memory_order_relaxed);
        HWND candidateHwnd = FindGameWindow();
        HWND hwnd = (candidateHwnd && GetWindowThreadProcessId(candidateHwnd, nullptr) == GetCurrentThreadId()) ? candidateHwnd : nullptr;
        if (hwnd) {
            g_OverlayInput.Attach(hwnd, m_context);
            g_OverlayInput.SetHotkeyCallback([this]() {
                if (IsDocumentVisible("tao_console")) {
                    HON_LOG_INFO("[OverlayManager] Ctrl+Shift+D pressed while tao_console visible -> returning to devtools cockpit.");
                    SetDocumentVisible("tao_console", false);
                    SetDocumentVisible("devtools", true);
                } else {
                    ToggleDocument("devtools");
                }
            });
        }
    }

    // Fast path: if no overlay documents are currently visible, skip rendering entirely
    if (!HasAnyVisibleDocument()) {
        return false;
    }

    uint32_t slot = imageIndex % m_frameResources.size();
    FrameResource& frameRes = m_frameResources[slot];

    // Synchronize slot fence before recording next frame
    if (frameRes.fence != VK_NULL_HANDLE && m_devDispatch.WaitForFences) {
        if (frameRes.isFencePending) {
            m_devDispatch.WaitForFences(m_device, 1, &frameRes.fence, VK_TRUE, 200000000); // 200ms
        }
        m_devDispatch.ResetFences(m_device, 1, &frameRes.fence);
        frameRes.isFencePending = false;
        m_renderInterface.NotifyFrameFenceCompleted(slot);
    }

    static bool s_hasLoggedInit = false;
    if (!s_hasLoggedInit) {
        s_hasLoggedInit = true;
        auto devIt = m_documents.find("devtools");
        bool devVis = (devIt != m_documents.end() && devIt->second && devIt->second->IsVisible());
        HON_LOG_INFO("[OverlayManager] First frame rendered: img=%u, slot=%u, init=%d, enabled=%d, ctx=%p, devtoolsVisible=%d",
            imageIndex, slot, m_isInitialized.load() ? 1 : 0, m_isEnabled.load() ? 1 : 0, (void*)m_context, devVis ? 1 : 0);
    }

    UpdateTelemetryUI();

    // Auto-advance MOTD carousel if visible (every 7 seconds)
    if (m_motdVisible) {
        uint64_t nowTick = GetTickCount64();
        if (nowTick >= m_lastSlideAdvanceTick + 7000) {
            NextMotdSlide();
        }
    }

    // Record command buffer
    VkCommandBufferBeginInfo beginInfo = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    m_devDispatch.BeginCommandBuffer(frameRes.cmdBuffer, &beginInfo);

    if (m_renderInterface.BeginFrame(frameRes.cmdBuffer, imageIndex, slot)) {
        m_context->Update();
        m_context->Render();
        m_renderInterface.EndFrame();
    } else {
        static uint32_t s_failCount = 0;
        if (s_failCount++ < 5) {
            HON_LOG_WARN("[OverlayManager] BeginFrame failed for imageIndex %u (total: %u)", imageIndex, s_failCount);
        }
    }

    m_devDispatch.EndCommandBuffer(frameRes.cmdBuffer);

    // Submit with wait semaphores
    VkSubmitInfo submitInfo = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
    submitInfo.commandBufferCount = 1;
    submitInfo.pCommandBuffers = &frameRes.cmdBuffer;

    VkPipelineStageFlags stackWaitStages[8];
    std::vector<VkPipelineStageFlags> heapWaitStages;
    if (waitSemaphoreCount > 0 && pWaitSemaphores != nullptr) {
        VkPipelineStageFlags* pWaitStages = stackWaitStages;
        if (waitSemaphoreCount <= 8) {
            for (uint32_t i = 0; i < waitSemaphoreCount; ++i) {
                stackWaitStages[i] = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
            }
        } else {
            heapWaitStages.assign(waitSemaphoreCount, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
            pWaitStages = heapWaitStages.data();
        }
        submitInfo.waitSemaphoreCount = waitSemaphoreCount;
        submitInfo.pWaitSemaphores = pWaitSemaphores;
        submitInfo.pWaitDstStageMask = pWaitStages;
    }

    if (frameRes.completeSemaphore != VK_NULL_HANDLE) {
        submitInfo.signalSemaphoreCount = 1;
        submitInfo.pSignalSemaphores = &frameRes.completeSemaphore;
    }

    VkResult submitRes = m_devDispatch.QueueSubmit(queue, 1, &submitInfo, frameRes.fence);
    if (submitRes != VK_SUCCESS) {
        HON_LOG_ERROR("[OverlayManager] QueueSubmit failed with code %d on slot %u", submitRes, slot);
        return false;
    }

    frameRes.isFencePending = true;

    if (pOutWaitSemaphore && frameRes.completeSemaphore != VK_NULL_HANDLE) {
        *pOutWaitSemaphore = frameRes.completeSemaphore;
    }

    return true;
}

void OverlayManager::Cleanup() {
    if (IsLayerProcessTerminating()) {
        // [L2-P1-02] Under LdrShutdownProcess / OS loader lock, never lock mutexes or call Vulkan ICD teardown.
        m_isInitialized = false;
        m_context = nullptr;
        m_device = VK_NULL_HANDLE;
        return;
    }

    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (!m_isInitialized.load(std::memory_order_acquire) && m_context == nullptr && m_device == VK_NULL_HANDLE) {
        return;
    }

    g_OverlayInput.Detach();

    m_cachedElementRml.clear();
    m_cachedElementClass.clear();
    m_lastRenderedLogGen = 0;
    m_forceTelemetryRefresh.store(true, std::memory_order_relaxed);
    m_devtoolsVisible = false;
    m_taoConsoleVisible = false;
    m_motdVisible = false;

    // Wait on OverlayManager's own submitted per-frame VkFences FIRST (thread-safe with DXVK's
    // background dxvk-submit thread on the shared VkDevice) before tearing down RmlUi/Vulkan resources.
    FreeFrameResources();

    if (m_context) {
        for (auto& pair : m_documents) {
            if (pair.second) {
                pair.second->RemoveEventListener(Rml::EventId::Click, &s_documentClickDispatcher);
                m_context->UnfocusDocument(pair.second);
                m_context->UnloadDocument(pair.second);
            }
        }
        m_context->UnloadAllDocuments();
        m_documents.clear();
        m_context->Update();
        Rml::RemoveContext(m_context->GetName());
        m_context = nullptr;
        HON_LOG_INFO("[OverlayManager] Unloaded all RmlUi documents before Rml::Shutdown");
        Rml::Shutdown();
    } else {
        m_documents.clear();
    }

    m_renderInterface.Cleanup();
    m_fontEngine.Reset();

    if (m_device != VK_NULL_HANDLE) {
        if (m_commandPool && m_devDispatch.DestroyCommandPool) {
            m_devDispatch.DestroyCommandPool(m_device, m_commandPool, nullptr);
            m_commandPool = VK_NULL_HANDLE;
        }
        m_device = VK_NULL_HANDLE;
    }

    m_isInitialized = false;
}

void OverlayManager::SetMotdSlide(int index) {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (m_motdSlides.empty()) return;
    if (index < 0) index = 0;
    if (index >= static_cast<int>(m_motdSlides.size())) index = static_cast<int>(m_motdSlides.size()) - 1;
    m_motdCurrentSlide = index;
    m_lastSlideAdvanceTick = GetTickCount64();
    UpdateMotdUI();
}

void OverlayManager::NextMotdSlide() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (m_motdSlides.empty()) return;
    int next = (m_motdCurrentSlide + 1) % static_cast<int>(m_motdSlides.size());
    SetMotdSlide(next);
}

void OverlayManager::PrevMotdSlide() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (m_motdSlides.empty()) return;
    int prev = (m_motdCurrentSlide - 1 + static_cast<int>(m_motdSlides.size())) % static_cast<int>(m_motdSlides.size());
    SetMotdSlide(prev);
}

void OverlayManager::ExecuteMotdSlideAction() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (m_motdSlides.empty()) return;
    if (m_motdCurrentSlide < 0 || m_motdCurrentSlide >= static_cast<int>(m_motdSlides.size())) return;
    const auto& slide = m_motdSlides[m_motdCurrentSlide];

    HON_LOG_INFO("[OverlayManager] Executing MOTD slide action for '%s': action='%s'",
                 slide.id.c_str(), slide.action.c_str());

    if (slide.action.rfind("store:", 0) == 0) {
        std::string productId = slide.action.substr(6);
        std::string cmd = "Script local s = GetCvarBool('cg_store2_') and 'store_container2' or 'store_container'; "
                          "UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', s, nil, false); "
                          "if HoN_Store and HoN_Store.SelectProduct then HoN_Store:SelectProduct('" + productId + "') end";
        g_OverlayBridge.ExecuteCommand(cmd);
        SetDocumentVisible("motd", false);
    } else if (slide.action == "settings:video" || slide.action == "settings:sound") {
        g_OverlayBridge.ExecuteCommand("Script UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', 'options_window', nil, false)");
        SetDocumentVisible("motd", false);
    } else if (slide.action == "social:streamers") {
        g_OverlayBridge.ExecuteCommand("Join #STREAMERS");
    } else if (!slide.action.empty()) {
        g_OverlayBridge.ExecuteCommand(slide.action);
    }
}

void OverlayManager::UpdateMotdUI() {
    auto it = m_documents.find("motd");
    if (it == m_documents.end() || !it->second) return;
    auto doc = it->second;

    if (m_motdCurrentSlide >= 0 && m_motdCurrentSlide < static_cast<int>(m_motdSlides.size())) {
        const auto& slide = m_motdSlides[m_motdCurrentSlide];
        auto elBadge = doc->GetElementById("slide-badge");
        if (elBadge) elBadge->SetInnerRML(slide.badge);

        auto elTitle = doc->GetElementById("slide-title");
        if (elTitle) elTitle->SetInnerRML(slide.title);

        auto elSubtitle = doc->GetElementById("slide-subtitle");
        if (elSubtitle) elSubtitle->SetInnerRML(slide.subtitle);

        auto elB1 = doc->GetElementById("bullet-1");
        if (elB1) elB1->SetInnerRML(slide.bullet1);

        auto elB2 = doc->GetElementById("bullet-2");
        if (elB2) elB2->SetInnerRML(slide.bullet2);

        auto elB3 = doc->GetElementById("bullet-3");
        if (elB3) elB3->SetInnerRML(slide.bullet3);

        auto elBtn = doc->GetElementById("slide-action-btn");
        if (elBtn) {
            if (slide.id == "bubbles") elBtn->SetInnerRML("View in Store");
            else if (slide.id == "fps") elBtn->SetInnerRML("Video Settings");
            else if (slide.id == "audio") elBtn->SetInnerRML("Audio Settings");
            else if (slide.id == "twitch") elBtn->SetInnerRML("Join #STREAMERS");
            else elBtn->SetInnerRML("Explore Feature");
        }
    }

    // Update dot indicator classes
    for (int i = 0; i < 4; ++i) {
        std::string dotId = "dot-" + std::to_string(i);
        auto elDot = doc->GetElementById(dotId);
        if (elDot) {
            elDot->SetClass("active", i == m_motdCurrentSlide);
        }
    }
}

void OverlayManager::HydrateMotdUI() {
    std::lock_guard<std::recursive_mutex> lock(m_managerMutex);
    if (m_motdSlides.empty()) {
        m_motdSlides = {
            {
                "bubbles",
                "LEGENDARY AVATAR",
                "Abyssal Bubbles",
                "Deep-Sea Trench Leviathan * Bioluminescent Chitin",
                "* Bioluminescent Ability Spell Effects (Shell Surf &amp; Kelp Field)",
                "* Atmospheric Deep-Sea Soundscape &amp; Voice",
                "* Animated 3D Store Pedestal &amp; Kill Celebrations",
                "store:Hero_Bubbles.Alt16"
            },
            {
                "fps",
                "ENGINE 5.0",
                "500+ FPS Unlocked Pipeline",
                "Ultra-Low Latency Rendering",
                "* RCAS Edge-Adaptive Contrast Sharpening with zero blur",
                "* Sub-Tick Raw Input &amp; Ultra-Low Frame Times",
                "* Zero-CEF architecture with direct Vulkan swapchain rendering",
                "settings:video"
            },
            {
                "audio",
                "AUDIO STUDIO",
                "3D Spatial Audio &amp; Surround",
                "Binaural HRTF Positioning",
                "* WASAPI Exclusive Mode with 5.1 and 7.1 surround mixdown",
                "* Directional HRTF audio positioning for fog-of-war abilities",
                "* Dynamic acoustic limiter preventing distortion in teamfights",
                "settings:sound"
            },
            {
                "twitch",
                "COMMUNITY",
                "Twitch Live Stream Studio",
                "Watch, Stream, and Compete",
                "* Live community creators featured in-game",
                "* Automated drop progression for viewing official broadcasts",
                "* Join #STREAMERS channel in-game for match scrims",
                "social:streamers"
            }
        };
    }
    UpdateMotdUI();
}

