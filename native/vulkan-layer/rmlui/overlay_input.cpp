// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: overlay_input.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - Win32 Input Hook & Dispatch
// ============================================================================

#include "overlay_input.h"
#include "overlay_manager.h"
#include <atomic>
#include <RmlUi/Core/Element.h>
#include "../layer_manifest.h"

#ifndef GET_X_LPARAM
#define GET_X_LPARAM(lp) ((int)(short)LOWORD(lp))
#endif
#ifndef GET_Y_LPARAM
#define GET_Y_LPARAM(lp) ((int)(short)HIWORD(lp))
#endif

OverlayInput g_OverlayInput;

OverlayInput::OverlayInput()
    : m_hwnd(nullptr)
    , m_originalWndProc(nullptr)
    , m_rmlContext(nullptr)
    , m_windowThreadId(0)
    , m_clientWidth(0)
    , m_clientHeight(0)
    , m_isAttached(false)
    , m_hotkeyTriggered(false)
    , m_escapeTriggered(false)
{
}

OverlayInput::~OverlayInput() {
    if (IsLayerProcessTerminating()) {
        m_isAttached.store(false, std::memory_order_release);
        m_rmlContext = nullptr;
        return;
    }
    Detach();
}

void OverlayInput::SetHotkeyCallback(std::function<void()> onToggleDevTools) {
    m_toggleDevToolsCallback = std::move(onToggleDevTools);
}

bool OverlayInput::Attach(HWND hwnd, Rml::Context* rmlContext) {
    if (!hwnd || !rmlContext) return false;
    if (m_isAttached.load(std::memory_order_relaxed) && m_hwnd == hwnd && IsWindow(m_hwnd)) {
        m_rmlContext = rmlContext;
        return true;
    }
    if (m_isAttached.load(std::memory_order_relaxed)) {
        Detach();
    }

    DWORD windowThread = GetWindowThreadProcessId(hwnd, nullptr);
    DWORD currentThread = GetCurrentThreadId();
    if (windowThread != currentThread) {
        HON_LOG_WARN("[OverlayInput] Attach called on TID %u, but HWND %p belongs to TID %u! Deferring to main thread.",
                     currentThread, (void*)hwnd, windowThread);
        return false;
    }

    m_hwnd = hwnd;
    m_rmlContext = rmlContext;
    m_windowThreadId = windowThread;
    RECT initialRect = {};
    if (GetClientRect(hwnd, &initialRect)) {
        m_clientWidth = initialRect.right - initialRect.left;
        m_clientHeight = initialRect.bottom - initialRect.top;
    }

    SetLastError(0);
    m_originalWndProc = reinterpret_cast<WNDPROC>(SetWindowLongPtr(m_hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(SubclassWndProc)));
    DWORD err = GetLastError();
    const bool attached = (m_originalWndProc != nullptr);
    m_isAttached.store(attached, std::memory_order_release);

    if (attached) {
        HON_LOG_INFO("[OverlayInput] Subclassed game window HWND %p successfully (TID: %u, Client: %dx%d).",
                     (void*)m_hwnd, currentThread, m_clientWidth, m_clientHeight);
    } else {
        m_windowThreadId = 0;
        m_clientWidth = 0;
        m_clientHeight = 0;
        HON_LOG_ERROR("[OverlayInput] Failed to subclass HWND %p (TID: %u, Error: %u).", (void*)m_hwnd, currentThread, err);
    }

    return attached;
}

void OverlayInput::Detach() {
    if (IsLayerProcessTerminating()) {
        m_isAttached.store(false, std::memory_order_release);
        m_windowThreadId = 0;
        m_clientWidth = 0;
        m_clientHeight = 0;
        m_hwnd = nullptr;
        m_rmlContext = nullptr;
        return;
    }

    bool leftInWndProcChain = false;
    if (m_isAttached.load(std::memory_order_relaxed) && m_hwnd && m_originalWndProc) {
        if (IsWindow(m_hwnd)) {
            LONG_PTR currentProc = GetWindowLongPtr(m_hwnd, GWLP_WNDPROC);
            if (currentProc == reinterpret_cast<LONG_PTR>(SubclassWndProc)) {
                SetWindowLongPtr(m_hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(m_originalWndProc));
                HON_LOG_INFO("[OverlayInput] Restored original WndProc for HWND %p.", (void*)m_hwnd);
            } else {
                // Another outer subclass (e.g. NativeGuard) sits above SubclassWndProc in the chain;
                // keep m_originalWndProc non-null so SubclassWndProc cleanly forwards remaining messages.
                leftInWndProcChain = true;
                HON_LOG_INFO("[OverlayInput] Preserving chained outer WndProc (0x%p) on HWND %p; switching SubclassWndProc to pass-through.",
                             reinterpret_cast<void*>(currentProc), (void*)m_hwnd);
            }
        }
    }
    m_isAttached.store(false, std::memory_order_release);
    m_windowThreadId = 0;
    m_clientWidth = 0;
    m_clientHeight = 0;
    m_hwnd = nullptr;
    if (!leftInWndProcChain) {
        m_originalWndProc = nullptr;
    }
    m_rmlContext = nullptr;
}

int OverlayInput::GetKeyModifiers() {
    int modifiers = 0;
    if (GetKeyState(VK_SHIFT) & 0x8000) {
        modifiers |= Rml::Input::KM_SHIFT;
    }
    if (GetKeyState(VK_CONTROL) & 0x8000) {
        modifiers |= Rml::Input::KM_CTRL;
    }
    if (GetKeyState(VK_MENU) & 0x8000) {
        modifiers |= Rml::Input::KM_ALT;
    }
    return modifiers;
}

Rml::Input::KeyIdentifier OverlayInput::ConvertKey(WPARAM vk) {
    switch (vk) {
        case 'A': return Rml::Input::KI_A;
        case 'B': return Rml::Input::KI_B;
        case 'C': return Rml::Input::KI_C;
        case 'D': return Rml::Input::KI_D;
        case 'E': return Rml::Input::KI_E;
        case 'F': return Rml::Input::KI_F;
        case 'G': return Rml::Input::KI_G;
        case 'H': return Rml::Input::KI_H;
        case 'I': return Rml::Input::KI_I;
        case 'J': return Rml::Input::KI_J;
        case 'K': return Rml::Input::KI_K;
        case 'L': return Rml::Input::KI_L;
        case 'M': return Rml::Input::KI_M;
        case 'N': return Rml::Input::KI_N;
        case 'O': return Rml::Input::KI_O;
        case 'P': return Rml::Input::KI_P;
        case 'Q': return Rml::Input::KI_Q;
        case 'R': return Rml::Input::KI_R;
        case 'S': return Rml::Input::KI_S;
        case 'T': return Rml::Input::KI_T;
        case 'U': return Rml::Input::KI_U;
        case 'V': return Rml::Input::KI_V;
        case 'W': return Rml::Input::KI_W;
        case 'X': return Rml::Input::KI_X;
        case 'Y': return Rml::Input::KI_Y;
        case 'Z': return Rml::Input::KI_Z;
        case '0': return Rml::Input::KI_0;
        case '1': return Rml::Input::KI_1;
        case '2': return Rml::Input::KI_2;
        case '3': return Rml::Input::KI_3;
        case '4': return Rml::Input::KI_4;
        case '5': return Rml::Input::KI_5;
        case '6': return Rml::Input::KI_6;
        case '7': return Rml::Input::KI_7;
        case '8': return Rml::Input::KI_8;
        case '9': return Rml::Input::KI_9;
        case VK_BACK: return Rml::Input::KI_BACK;
        case VK_TAB: return Rml::Input::KI_TAB;
        case VK_RETURN: return Rml::Input::KI_RETURN;
        case VK_ESCAPE: return Rml::Input::KI_ESCAPE;
        case VK_SPACE: return Rml::Input::KI_SPACE;
        case VK_LEFT: return Rml::Input::KI_LEFT;
        case VK_UP: return Rml::Input::KI_UP;
        case VK_RIGHT: return Rml::Input::KI_RIGHT;
        case VK_DOWN: return Rml::Input::KI_DOWN;
        case VK_DELETE: return Rml::Input::KI_DELETE;
        case VK_F1: return Rml::Input::KI_F1;
        case VK_F2: return Rml::Input::KI_F2;
        case VK_F3: return Rml::Input::KI_F3;
        case VK_F4: return Rml::Input::KI_F4;
        case VK_F5: return Rml::Input::KI_F5;
        case VK_F6: return Rml::Input::KI_F6;
        case VK_F7: return Rml::Input::KI_F7;
        case VK_F8: return Rml::Input::KI_F8;
        case VK_F9: return Rml::Input::KI_F9;
        case VK_F10: return Rml::Input::KI_F10;
        case VK_F11: return Rml::Input::KI_F11;
        case VK_F12: return Rml::Input::KI_F12;
        default: return Rml::Input::KI_UNKNOWN;
    }
}

static void GetScaledMouseCoords(HWND hwnd, LPARAM lParam, Rml::Context* ctx, int& outX, int& outY) {
    int rawX = GET_X_LPARAM(lParam);
    int rawY = GET_Y_LPARAM(lParam);
    outX = rawX;
    outY = rawY;
    if (!ctx || !hwnd) return;

    int clientW = g_OverlayInput.GetCachedClientWidth();
    int clientH = g_OverlayInput.GetCachedClientHeight();
    if (clientW <= 0 || clientH <= 0) {
        RECT clientRect = {};
        if (GetClientRect(hwnd, &clientRect)) {
            clientW = clientRect.right - clientRect.left;
            clientH = clientRect.bottom - clientRect.top;
        }
    }

    if (clientW > 0 && clientH > 0) {
        Rml::Vector2i ctxDims = ctx->GetDimensions();
        if (ctxDims.x > 0 && ctxDims.y > 0 && (clientW != ctxDims.x || clientH != ctxDims.y)) {
            outX = static_cast<int>(static_cast<float>(rawX) * static_cast<float>(ctxDims.x) / static_cast<float>(clientW));
            outY = static_cast<int>(static_cast<float>(rawY) * static_cast<float>(ctxDims.y) / static_cast<float>(clientH));
        }
    }
}

static bool ShouldConsumeMouse(Rml::Context* ctx) {
    if (!ctx) return false;
    auto hover = ctx->GetHoverElement();
    if (!hover || hover == ctx->GetRootElement()) return false;
    if (hover->GetTagName() == "body") return false;
    return true;
}

LRESULT CALLBACK OverlayInput::SubclassWndProc(HWND hwnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    DWORD windowThread = (hwnd == g_OverlayInput.m_hwnd && g_OverlayInput.m_windowThreadId != 0)
        ? g_OverlayInput.m_windowThreadId
        : GetWindowThreadProcessId(hwnd, nullptr);
    DWORD currentThread = GetCurrentThreadId();
    if (windowThread != 0 && currentThread != windowThread) {
        static std::atomic<bool> s_loggedOffThreadWndProc{false};
        if (!s_loggedOffThreadWndProc.exchange(true, std::memory_order_relaxed)) {
            HON_LOG_WARN("[OverlayInput] Off-thread WndProc call bypassed (CurrentTID=%u, WindowTID=%u, uMsg=0x%04X).", currentThread, windowThread, uMsg);
        }
        return g_OverlayInput.m_originalWndProc ? CallWindowProc(g_OverlayInput.m_originalWndProc, hwnd, uMsg, wParam, lParam) : DefWindowProc(hwnd, uMsg, wParam, lParam);
    }
    if (!g_OverlayInput.m_isAttached.load(std::memory_order_acquire) ||
        !g_OverlayInput.m_rmlContext ||
        IsLayerProcessTerminating()) {
        WNDPROC orig = g_OverlayInput.m_originalWndProc;
        return orig ? CallWindowProc(orig, hwnd, uMsg, wParam, lParam) : DefWindowProc(hwnd, uMsg, wParam, lParam);
    }

    if (uMsg == WM_SIZE) {
        g_OverlayInput.m_clientWidth = static_cast<int>(LOWORD(lParam));
        g_OverlayInput.m_clientHeight = static_cast<int>(HIWORD(lParam));
    }

    // Fast path: if no overlay documents are visible, bypass mutex lock unless it's a keydown that might toggle overlay
    if (!g_OverlayManager.HasAnyVisibleDocument()) {
        if (uMsg != WM_KEYDOWN && uMsg != WM_SYSKEYDOWN) {
            return CallWindowProc(g_OverlayInput.m_originalWndProc, hwnd, uMsg, wParam, lParam);
        }
    }

    std::lock_guard<std::recursive_mutex> lock(g_OverlayManager.GetMutex());
    Rml::Context* ctx = g_OverlayInput.m_rmlContext;
    int modifiers = GetKeyModifiers();

    switch (uMsg) {
        case WM_MOUSEMOVE: {
            int x = 0, y = 0;
            GetScaledMouseCoords(hwnd, lParam, ctx, x, y);
            ctx->ProcessMouseMove(x, y, modifiers);
            break;
        }

        case WM_LBUTTONDOWN: {
            int x = 0, y = 0;
            GetScaledMouseCoords(hwnd, lParam, ctx, x, y);
            ctx->ProcessMouseMove(x, y, modifiers);
            bool consume = ShouldConsumeMouse(ctx);
            ctx->ProcessMouseButtonDown(0, modifiers);
            if (consume) {
                auto hover = ctx->GetHoverElement();
                HON_LOG_INFO("[OverlayInput] WM_LBUTTONDOWN consumed at (%d, %d). Target: <%s id='%s'>",
                             x, y,
                             hover ? hover->GetTagName().c_str() : "none",
                             hover ? hover->GetId().c_str() : "");
                return 0; // Consume click over overlay
            }
            break;
        }

        case WM_LBUTTONUP: {
            int x = 0, y = 0;
            GetScaledMouseCoords(hwnd, lParam, ctx, x, y);
            ctx->ProcessMouseMove(x, y, modifiers);
            bool consume = ShouldConsumeMouse(ctx);
            ctx->ProcessMouseButtonUp(0, modifiers);
            if (consume) {
                HON_LOG_INFO("[OverlayInput] WM_LBUTTONUP consumed at (%d, %d).", x, y);
                return 0; // Consume release over overlay
            }
            break;
        }

        case WM_RBUTTONDOWN: {
            int x = 0, y = 0;
            GetScaledMouseCoords(hwnd, lParam, ctx, x, y);
            ctx->ProcessMouseMove(x, y, modifiers);
            bool consume = ShouldConsumeMouse(ctx);
            ctx->ProcessMouseButtonDown(1, modifiers);
            if (consume) {
                return 0;
            }
            break;
        }

        case WM_RBUTTONUP: {
            int x = 0, y = 0;
            GetScaledMouseCoords(hwnd, lParam, ctx, x, y);
            ctx->ProcessMouseMove(x, y, modifiers);
            bool consume = ShouldConsumeMouse(ctx);
            ctx->ProcessMouseButtonUp(1, modifiers);
            if (consume) {
                return 0;
            }
            break;
        }

        case WM_MOUSEWHEEL: {
            float delta = static_cast<float>(-GET_WHEEL_DELTA_WPARAM(wParam)) / static_cast<float>(WHEEL_DELTA);
            ctx->ProcessMouseWheel(delta, modifiers);
            if (ShouldConsumeMouse(ctx)) {
                return 0; // Consume scroll
            }
            break;
        }

        case WM_SYSKEYDOWN:
        case WM_KEYDOWN: {
            bool isCtrl = (modifiers & Rml::Input::KM_CTRL) ||
                          ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) ||
                          ((GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0) ||
                          ((GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0);
            bool isShift = (modifiers & Rml::Input::KM_SHIFT) ||
                           ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) ||
                           ((GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0) ||
                           ((GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0);

            // Hotkey: Ctrl+Shift+D to toggle DevTools overlay
            if ((wParam == 'D' || wParam == 'd' || wParam == 0x44) && isCtrl && isShift)
            {
                HON_LOG_INFO("[OverlayInput] Hotkey Ctrl+Shift+D detected! Toggling DevTools overlay.");
                g_OverlayInput.m_hotkeyTriggered = true;
                if (g_OverlayInput.m_toggleDevToolsCallback) {
                    g_OverlayInput.m_toggleDevToolsCallback();
                }
                return 0; // Completely consumed: K2 never sees this key down
            }

            // Escape key dismisses MOTD News, TAO Console, or DevTools overlay
            if (wParam == VK_ESCAPE) {
                if (g_OverlayManager.IsDocumentVisible("motd")) {
                    HON_LOG_INFO("[OverlayInput] Escape pressed: dismissing MOTD News & Store modal.");
                    g_OverlayManager.SetDocumentVisible("motd", false);
                    g_OverlayInput.m_escapeTriggered = true;
                    return 0; // Consume: K2 never sees Escape
                } else if (g_OverlayManager.IsDocumentVisible("tao_console")) {
                    HON_LOG_INFO("[OverlayInput] Escape pressed: dismissing TAO Diagnostics Console.");
                    g_OverlayManager.SetDocumentVisible("tao_console", false);
                    g_OverlayInput.m_escapeTriggered = true;
                    return 0; // Consume: K2 never sees Escape
                } else if (g_OverlayManager.IsDocumentVisible("devtools")) {
                    HON_LOG_INFO("[OverlayInput] Escape pressed: dismissing DevTools overlay.");
                    g_OverlayManager.SetDocumentVisible("devtools", false);
                    g_OverlayInput.m_escapeTriggered = true;
                    return 0; // Consume: K2 never sees Escape
                }
            }

            // Phase 5: Enter key executes command on #tao-input-cmd
            if (wParam == VK_RETURN) {
                auto focus = ctx->GetFocusElement();
                if (focus && focus->GetId() == "tao-input-cmd") {
                    HON_LOG_INFO("[OverlayInput] Enter pressed on #tao-input-cmd: executing command.");
                    g_OverlayManager.ExecuteConsoleInput();
                    return 0; // Consume Enter key
                }
            }

            // Phase 5: Up/Down arrow navigation for command history
            if (wParam == VK_UP) {
                auto focus = ctx->GetFocusElement();
                if (focus && focus->GetId() == "tao-input-cmd") {
                    g_OverlayManager.NavigateCommandHistory(-1);
                    return 0;
                }
            }
            if (wParam == VK_DOWN) {
                auto focus = ctx->GetFocusElement();
                if (focus && focus->GetId() == "tao-input-cmd") {
                    g_OverlayManager.NavigateCommandHistory(1);
                    return 0;
                }
            }

            Rml::Input::KeyIdentifier key = ConvertKey(wParam);
            if (key != Rml::Input::KI_UNKNOWN) {
                ctx->ProcessKeyDown(key, modifiers);
            }

            auto focus = ctx->GetFocusElement();
            if (focus && (focus->GetTagName() == "input" || focus->GetTagName() == "textarea")) {
                return 0; // Consume keys when typing in input fields
            }
            break;
        }

        case WM_SYSKEYUP:
        case WM_KEYUP: {
            if ((wParam == 'D' || wParam == 'd' || wParam == 0x44) && g_OverlayInput.m_hotkeyTriggered) {
                g_OverlayInput.m_hotkeyTriggered = false;
                HON_LOG_INFO("[OverlayInput] Hotkey Ctrl+Shift+D keyup consumed.");
                return 0; // Consume key up of the hotkey so K2 does not trigger any release actions
            }

            if (wParam == VK_ESCAPE && g_OverlayInput.m_escapeTriggered) {
                g_OverlayInput.m_escapeTriggered = false;
                HON_LOG_INFO("[OverlayInput] Escape keyup consumed.");
                return 0;
            }

            Rml::Input::KeyIdentifier key = ConvertKey(wParam);
            if (key != Rml::Input::KI_UNKNOWN) {
                ctx->ProcessKeyUp(key, modifiers);
            }

            auto focus = ctx->GetFocusElement();
            if (focus && (focus->GetTagName() == "input" || focus->GetTagName() == "textarea")) {
                return 0;
            }
            break;
        }

        case WM_SYSCHAR:
        case WM_CHAR: {
            bool isCtrl = (modifiers & Rml::Input::KM_CTRL) ||
                          ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0);
            if (g_OverlayInput.m_hotkeyTriggered ||
                ((wParam == 4 || wParam == 'D' || wParam == 'd') && isCtrl)) {
                return 0; // Consume character event generated by Ctrl+Shift+D (ASCII 4 / Ctrl+D)
            }

            if (wParam == 27 && g_OverlayInput.m_escapeTriggered) {
                return 0; // Consume Escape char (ASCII 27)
            }

            if (wParam >= 32) {
                ctx->ProcessTextInput(static_cast<Rml::Character>(wParam));
            }
            auto focus = ctx->GetFocusElement();
            if (focus && (focus->GetTagName() == "input" || focus->GetTagName() == "textarea")) {
                return 0;
            }
            break;
        }

        default:
            break;
    }

    return CallWindowProc(g_OverlayInput.m_originalWndProc, hwnd, uMsg, wParam, lParam);
}
