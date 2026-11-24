// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

#include <windows.h>
#include <cstdint>
#include <cstddef>

namespace Kongor {

enum class CursorClipReason : uint8_t {
    Confined = 0,
    HeadlessOrOffscreen = 1,
    DisabledByCvar = 2,
    WindowUnfocused = 3,
    EscapeLatched = 4,
    SizeMoveOrMenu = 5,
    InvalidGeometry = 6
};

struct CursorConfinementInputs {
    bool isHeadless = false;
    bool constrainCursorCvar = true;
    bool windowFocused = false;
    bool escapeLatched = false;
    bool inSizeMoveOrMenu = false;
    int32_t clientOriginX = 0;
    int32_t clientOriginY = 0;
    int32_t clientWidth = 0;
    int32_t clientHeight = 0;
};

struct CursorConfinementDecision {
    bool shouldConfine = false;
    CursorClipReason reason = CursorClipReason::WindowUnfocused;
    RECT clipRect = { 0, 0, 0, 0 };
};

class CursorStateMachine {
public:
    void Reset() noexcept;

    void SetHeadless(bool headless) noexcept { m_isHeadless = headless; }
    void SetConstrainCvar(bool constrain) noexcept { m_constrainCursorCvar = constrain; }
    void SetClientGeometry(int32_t originX, int32_t originY, int32_t width, int32_t height) noexcept;

    void OnFocusLost() noexcept;
    void OnActivate(uint16_t activateState, LRESULT nchitTestResult) noexcept;
    void OnActivateApp(bool active) noexcept;
    void OnSetFocus() noexcept;
    void OnClientMouseButtonDown() noexcept;
    void OnEnterSizeMoveOrMenu() noexcept;
    void OnExitSizeMoveOrMenu() noexcept;
    bool OnKeyDown(uint32_t vk, bool altDown, bool ctrlDown) noexcept;

    bool IsEscapeLatched() const noexcept { return m_escapeLatched; }
    bool IsInSizeMoveOrMenu() const noexcept { return m_inSizeMoveOrMenu; }
    bool IsWindowFocused() const noexcept { return m_windowFocused; }
    bool GetConstrainCvar() const noexcept { return m_constrainCursorCvar; }

    CursorConfinementDecision Evaluate() const noexcept;

private:
    bool m_isHeadless = false;
    bool m_constrainCursorCvar = true; // Default true before cgame_x64 registers cg_constrainCursor
    bool m_windowFocused = false;
    bool m_escapeLatched = false;
    bool m_inSizeMoveOrMenu = false;
    int32_t m_clientOriginX = 0;
    int32_t m_clientOriginY = 0;
    int32_t m_clientWidth = 0;
    int32_t m_clientHeight = 0;
};

const char* CursorClipReasonToString(CursorClipReason reason) noexcept;

CursorConfinementDecision EvaluateCursorConfinement(const CursorConfinementInputs& in) noexcept;

bool ParseConstrainCursorBoolW(const wchar_t* value, bool fallbackValue) noexcept;

bool IsEscapeShortcutKey(uint32_t vk, bool altDown, bool ctrlDown) noexcept;

bool TryRedirectHeadlessStartupCfgWriteW(
    bool isHeadless,
    const wchar_t* lpFileName,
    DWORD dwDesiredAccess,
    wchar_t* outRedirectedPath,
    size_t outCap) noexcept;

bool TryRedirectHeadlessStartupCfgWriteA(
    bool isHeadless,
    const char* lpFileName,
    DWORD dwDesiredAccess,
    char* outRedirectedPath,
    size_t outCap) noexcept;

bool TryRedirectUserdataDirW(
    const wchar_t* lpFileName,
    DWORD dwDesiredAccess,
    wchar_t* outRedirectedPath,
    size_t outCap) noexcept;

bool TryRedirectUserdataDirA(
    const char* lpFileName,
    DWORD dwDesiredAccess,
    char* outRedirectedPath,
    size_t outCap) noexcept;

} // namespace Kongor
