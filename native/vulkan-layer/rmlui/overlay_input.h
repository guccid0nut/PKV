// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: overlay_input.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - Win32 Input Hook & Dispatch
// ============================================================================

#pragma once

#include <windows.h>
#include <RmlUi/Core/Context.h>
#include <RmlUi/Core/Input.h>
#include <atomic>
#include <functional>

class OverlayInput {
public:
    OverlayInput();
    ~OverlayInput();

    bool Attach(HWND hwnd, Rml::Context* rmlContext);
    void Detach();

    void SetHotkeyCallback(std::function<void()> onToggleDevTools);
    bool IsAttached() const { return m_isAttached.load(std::memory_order_relaxed) && m_hwnd != nullptr && IsWindow(m_hwnd); }
    bool IsAttachedFast() const noexcept { return m_isAttached.load(std::memory_order_relaxed) && m_hwnd != nullptr; }
    HWND GetHwnd() const { return m_hwnd; }
    int GetCachedClientWidth() const noexcept { return m_clientWidth; }
    int GetCachedClientHeight() const noexcept { return m_clientHeight; }

    static LRESULT CALLBACK SubclassWndProc(HWND hwnd, UINT uMsg, WPARAM wParam, LPARAM lParam);

private:
    static int GetKeyModifiers();
    static Rml::Input::KeyIdentifier ConvertKey(WPARAM vk);

private:
    HWND m_hwnd;
    WNDPROC m_originalWndProc;
    Rml::Context* m_rmlContext;
    std::function<void()> m_toggleDevToolsCallback;
    DWORD m_windowThreadId;
    int m_clientWidth;
    int m_clientHeight;
    std::atomic<bool> m_isAttached;
    bool m_hotkeyTriggered;
    bool m_escapeTriggered;
};

extern OverlayInput g_OverlayInput;
