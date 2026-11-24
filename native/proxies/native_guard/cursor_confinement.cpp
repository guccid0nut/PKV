// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "cursor_confinement.h"
#include "native_guard_internal.h"

#include <atomic>
#include <cwchar>
#include <cstring>
#include <cstdio>
#include <filesystem>

namespace {
using PfnClipCursor = BOOL (WINAPI*)(const RECT*);
std::atomic<PfnClipCursor> s_pfnEmergencyRealClipCursor{ nullptr };
std::atomic<HWND>          s_hEmergencyGameWnd{ NULL };
std::atomic<std::atomic<bool>*> s_pEmergencyConfinedFlag{ nullptr };
std::atomic<Kongor::CursorStateMachine*> s_pEmergencyStateMachine{ nullptr };
} // namespace

void Kongor_RegisterEmergencyCursorReleaseContext(
    BOOL (WINAPI* pfnRealClipCursor)(const RECT*),
    HWND hGameWnd,
    std::atomic<bool>* pCurrentlyConfinedFlag,
    void* pStateMachine) noexcept
{
    if (pfnRealClipCursor) {
        s_pfnEmergencyRealClipCursor.store(pfnRealClipCursor, std::memory_order_release);
    }
    if (hGameWnd) {
        s_hEmergencyGameWnd.store(hGameWnd, std::memory_order_release);
    }
    if (pCurrentlyConfinedFlag) {
        s_pEmergencyConfinedFlag.store(pCurrentlyConfinedFlag, std::memory_order_release);
    }
    if (pStateMachine) {
        s_pEmergencyStateMachine.store(static_cast<Kongor::CursorStateMachine*>(pStateMachine), std::memory_order_release);
    }
}

static void ForceReleaseCursorEmergencySehBody() noexcept {
    __try {
        if (auto* pFlag = s_pEmergencyConfinedFlag.load(std::memory_order_acquire)) {
            pFlag->store(false, std::memory_order_release);
        }
        if (auto* pSm = s_pEmergencyStateMachine.load(std::memory_order_acquire)) {
            pSm->OnFocusLost();
        }

        PfnClipCursor fnClip = s_pfnEmergencyRealClipCursor.load(std::memory_order_acquire);
        if (!fnClip) {
            fnClip = &::ClipCursor;
        }

        BOOL released = fnClip(nullptr);
        DWORD curTid = ::GetCurrentThreadId();

        auto tryReleaseWithAttachedQueue = [&](HWND targetWnd) noexcept {
            if (!targetWnd || !::IsWindow(targetWnd)) return;
            DWORD targetTid = ::GetWindowThreadProcessId(targetWnd, NULL);
            if (targetTid == 0 || targetTid == curTid) return;
            BOOL attached = ::AttachThreadInput(curTid, targetTid, TRUE);
            fnClip(nullptr);
            if (attached) {
                ::AttachThreadInput(curTid, targetTid, FALSE);
            }
        };

        HWND hGame = s_hEmergencyGameWnd.load(std::memory_order_acquire);
        HWND hFg   = ::GetForegroundWindow();
        if (!released || (hGame && ::GetWindowThreadProcessId(hGame, NULL) != curTid)) {
            tryReleaseWithAttachedQueue(hGame);
        }
        if (hFg && hFg != hGame) {
            tryReleaseWithAttachedQueue(hFg);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

void Kongor_ForceReleaseCursorEmergency() noexcept {
    KongorScopedProtectedSeh sehGuard;
    ForceReleaseCursorEmergencySehBody();
}

namespace Kongor {

static constexpr int32_t kOffscreenCoordinateThreshold = -5000;

const char* CursorClipReasonToString(CursorClipReason reason) noexcept {
    switch (reason) {
        case CursorClipReason::Confined:            return "Confined";
        case CursorClipReason::HeadlessOrOffscreen: return "HeadlessOrOffscreen";
        case CursorClipReason::DisabledByCvar:      return "DisabledByCvar";
        case CursorClipReason::WindowUnfocused:     return "WindowUnfocused";
        case CursorClipReason::EscapeLatched:       return "EscapeLatched";
        case CursorClipReason::SizeMoveOrMenu:      return "SizeMoveOrMenu";
        case CursorClipReason::InvalidGeometry:     return "InvalidGeometry";
        default:                                    return "Unknown";
    }
}

CursorConfinementDecision EvaluateCursorConfinement(const CursorConfinementInputs& in) noexcept {
    CursorConfinementDecision out{};
    if (in.isHeadless ||
        in.clientOriginX <= kOffscreenCoordinateThreshold ||
        in.clientOriginY <= kOffscreenCoordinateThreshold) {
        out.shouldConfine = false;
        out.reason = CursorClipReason::HeadlessOrOffscreen;
        return out;
    }

    if (!in.constrainCursorCvar) {
        out.shouldConfine = false;
        out.reason = CursorClipReason::DisabledByCvar;
        return out;
    }

    if (in.inSizeMoveOrMenu) {
        out.shouldConfine = false;
        out.reason = CursorClipReason::SizeMoveOrMenu;
        return out;
    }

    if (!in.windowFocused) {
        out.shouldConfine = false;
        out.reason = CursorClipReason::WindowUnfocused;
        return out;
    }

    if (in.escapeLatched) {
        out.shouldConfine = false;
        out.reason = CursorClipReason::EscapeLatched;
        return out;
    }

    if (in.clientWidth <= 0 || in.clientHeight <= 0) {
        out.shouldConfine = false;
        out.reason = CursorClipReason::InvalidGeometry;
        return out;
    }

    // Strict 0..clientW, 0..clientH client-area confinement (0px top margin preserves MOBA y==0 upward camera panning)
    out.shouldConfine = true;
    out.reason = CursorClipReason::Confined;
    out.clipRect.left   = in.clientOriginX;
    out.clipRect.top    = in.clientOriginY;
    out.clipRect.right  = in.clientOriginX + in.clientWidth;
    out.clipRect.bottom = in.clientOriginY + in.clientHeight;
    return out;
}

bool ParseConstrainCursorBoolW(const wchar_t* value, bool fallbackValue) noexcept {
    if (!value) return fallbackValue;
    while (*value == L' ' || *value == L'\t' || *value == L'"' || *value == L'\'') {
        ++value;
    }
    if (*value == L'\0') {
        return fallbackValue;
    }
    if (_wcsnicmp(value, L"true", 4) == 0 || *value == L'1') {
        return true;
    }
    if (_wcsnicmp(value, L"false", 5) == 0 || *value == L'0') {
        return false;
    }
    return fallbackValue;
}

bool IsEscapeShortcutKey(uint32_t vk, bool altDown, bool ctrlDown) noexcept {
    if (vk == VK_LWIN || vk == VK_RWIN) {
        return true;
    }
    if (vk == VK_TAB && altDown) {
        return true;
    }
    if (vk == VK_ESCAPE && (ctrlDown || altDown)) {
        return true;
    }
    if (vk == VK_SPACE && altDown) {
        return true;
    }
    return false;
}

void CursorStateMachine::Reset() noexcept {
    m_isHeadless = false;
    m_constrainCursorCvar = true;
    m_windowFocused = false;
    m_escapeLatched = false;
    m_inSizeMoveOrMenu = false;
    m_clientOriginX = 0;
    m_clientOriginY = 0;
    m_clientWidth = 0;
    m_clientHeight = 0;
}

void CursorStateMachine::SetClientGeometry(int32_t originX, int32_t originY, int32_t width, int32_t height) noexcept {
    m_clientOriginX = originX;
    m_clientOriginY = originY;
    if (width > 0) m_clientWidth = width;
    if (height > 0) m_clientHeight = height;
}

void CursorStateMachine::OnFocusLost() noexcept {
    m_windowFocused = false;
    m_escapeLatched = true;
    m_inSizeMoveOrMenu = false;
}

void CursorStateMachine::OnActivate(uint16_t activateState, LRESULT nchitTestResult) noexcept {
    if (activateState == WA_INACTIVE) {
        OnFocusLost();
        return;
    }

    m_windowFocused = true;
    if (activateState == WA_ACTIVE) {
        // Programmatic or Alt+Tab keyboard activation back into game
        m_escapeLatched = false;
    } else if (activateState == WA_CLICKACTIVE) {
        // Only clear escape latch when clicking inside the client area (HTCLIENT).
        // Clicking the title bar (HTCAPTION), close/min/max buttons, or resize borders
        // keeps escapeLatched set so windowed title-bar dragging is never yanked!
        if (nchitTestResult == HTCLIENT) {
            m_escapeLatched = false;
        } else {
            m_escapeLatched = true;
        }
    }
}

void CursorStateMachine::OnActivateApp(bool active) noexcept {
    if (!active) {
        OnFocusLost();
    } else {
        m_windowFocused = true;
    }
}

void CursorStateMachine::OnSetFocus() noexcept {
    m_windowFocused = true;
}

void CursorStateMachine::OnClientMouseButtonDown() noexcept {
    m_windowFocused = true;
    m_escapeLatched = false;
}

void CursorStateMachine::OnEnterSizeMoveOrMenu() noexcept {
    m_inSizeMoveOrMenu = true;
}

void CursorStateMachine::OnExitSizeMoveOrMenu() noexcept {
    m_inSizeMoveOrMenu = false;
}

bool CursorStateMachine::OnKeyDown(uint32_t vk, bool altDown, bool ctrlDown) noexcept {
    if (IsEscapeShortcutKey(vk, altDown, ctrlDown)) {
        m_escapeLatched = true;
        return true;
    }
    return false;
}

CursorConfinementDecision CursorStateMachine::Evaluate() const noexcept {
    CursorConfinementInputs in{};
    in.isHeadless          = m_isHeadless;
    in.constrainCursorCvar = m_constrainCursorCvar;
    in.windowFocused       = m_windowFocused;
    in.escapeLatched       = m_escapeLatched;
    in.inSizeMoveOrMenu    = m_inSizeMoveOrMenu;
    in.clientOriginX       = m_clientOriginX;
    in.clientOriginY       = m_clientOriginY;
    in.clientWidth         = m_clientWidth;
    in.clientHeight        = m_clientHeight;
    return EvaluateCursorConfinement(in);
}

static bool HasWriteAccessMask(DWORD dwDesiredAccess) noexcept {
    constexpr DWORD kWriteMask = GENERIC_WRITE | GENERIC_ALL | FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE;
    return (dwDesiredAccess & kWriteMask) != 0;
}

bool TryRedirectHeadlessStartupCfgWriteW(
    bool isHeadless,
    const wchar_t* lpFileName,
    DWORD dwDesiredAccess,
    wchar_t* outRedirectedPath,
    size_t outCap) noexcept
{
    if (outRedirectedPath && outCap > 0) {
        outRedirectedPath[0] = L'\0';
    }
    if (!isHeadless || !lpFileName || lpFileName[0] == L'\0' || !outRedirectedPath || outCap == 0) {
        return false;
    }
    if (!HasWriteAccessMask(dwDesiredAccess)) {
        return false;
    }

    const wchar_t* lastBackslash = wcsrchr(lpFileName, L'\\');
    const wchar_t* lastSlash     = wcsrchr(lpFileName, L'/');
    const wchar_t* sep = lastBackslash;
    if (lastSlash && (!sep || lastSlash > sep)) {
        sep = lastSlash;
    }
    const wchar_t* leaf = sep ? (sep + 1) : lpFileName;
    if (_wcsicmp(leaf, L"startup.cfg") != 0) {
        return false;
    }

    size_t prefixLen = static_cast<size_t>(leaf - lpFileName);
    static constexpr wchar_t kReplacementLeaf[] = L"startup_headless.cfg";
    constexpr size_t kReplacementLen = (sizeof(kReplacementLeaf) / sizeof(wchar_t)) - 1;
    if (prefixLen + kReplacementLen + 1 > outCap) {
        return false;
    }

    if (prefixLen > 0) {
        wmemcpy(outRedirectedPath, lpFileName, prefixLen);
    }
    wmemcpy(outRedirectedPath + prefixLen, kReplacementLeaf, kReplacementLen);
    outRedirectedPath[prefixLen + kReplacementLen] = L'\0';
    return true;
}

bool TryRedirectHeadlessStartupCfgWriteA(
    bool isHeadless,
    const char* lpFileName,
    DWORD dwDesiredAccess,
    char* outRedirectedPath,
    size_t outCap) noexcept
{
    if (outRedirectedPath && outCap > 0) {
        outRedirectedPath[0] = '\0';
    }
    if (!isHeadless || !lpFileName || lpFileName[0] == '\0' || !outRedirectedPath || outCap == 0) {
        return false;
    }
    if (!HasWriteAccessMask(dwDesiredAccess)) {
        return false;
    }

    const char* lastBackslash = strrchr(lpFileName, '\\');
    const char* lastSlash     = strrchr(lpFileName, '/');
    const char* sep = lastBackslash;
    if (lastSlash && (!sep || lastSlash > sep)) {
        sep = lastSlash;
    }
    const char* leaf = sep ? (sep + 1) : lpFileName;
    if (_stricmp(leaf, "startup.cfg") != 0) {
        return false;
    }

    size_t prefixLen = static_cast<size_t>(leaf - lpFileName);
    static constexpr char kReplacementLeaf[] = "startup_headless.cfg";
    constexpr size_t kReplacementLen = sizeof(kReplacementLeaf) - 1;
    if (prefixLen + kReplacementLen + 1 > outCap) {
        return false;
    }

    if (prefixLen > 0) {
        memcpy(outRedirectedPath, lpFileName, prefixLen);
    }
    memcpy(outRedirectedPath + prefixLen, kReplacementLeaf, kReplacementLen);
    outRedirectedPath[prefixLen + kReplacementLen] = '\0';
    return true;
}

bool TryRedirectUserdataDirW(
    const wchar_t* lpFileName,
    DWORD dwDesiredAccess,
    wchar_t* outRedirectedPath,
    size_t outCap) noexcept
{
    if (outRedirectedPath && outCap > 0) {
        outRedirectedPath[0] = L'\0';
    }
    if (!lpFileName || lpFileName[0] == L'\0' || !outRedirectedPath || outCap == 0) {
        return false;
    }

    wchar_t customUserDir[MAX_PATH] = {0};
    DWORD envLen = GetEnvironmentVariableW(L"KONGOR_USERDATA_DIR", customUserDir, MAX_PATH);
    if (envLen == 0 || envLen >= MAX_PATH) {
        return false;
    }

    const wchar_t* match = nullptr;
    const wchar_t* p = lpFileName;
    while (*p) {
        if (_wcsnicmp(p, L"userdata", 8) == 0) {
            bool startOk = (p == lpFileName) || (*(p - 1) == L'\\' || *(p - 1) == L'/');
            bool endOk = (p[8] == L'\0') || (p[8] == L'\\' || p[8] == L'/');
            if (startOk && endOk) {
                match = p;
                break;
            }
        }
        ++p;
    }

    if (!match) {
        return false;
    }

    size_t prefixLen = static_cast<size_t>(match - lpFileName);
    const wchar_t* afterUserdata = match + 8;
    const wchar_t* remainder = afterUserdata;
    wchar_t sep = L'\\';
    if (*remainder == L'\\' || *remainder == L'/') {
        sep = *remainder;
        ++remainder;
    }

    bool isShaderCache = (_wcsicmp(remainder, L"shadercache.s2z") == 0);

    size_t customDirLen = wcslen(customUserDir);
    size_t remainderLen = wcslen(remainder);
    size_t needed = prefixLen + customDirLen + (remainderLen > 0 ? (1 + remainderLen) : 0) + 1;
    if (needed > outCap) {
        return false;
    }

    if (prefixLen > 0) {
        wmemcpy(outRedirectedPath, lpFileName, prefixLen);
    }
    wmemcpy(outRedirectedPath + prefixLen, customUserDir, customDirLen);
    size_t curLen = prefixLen + customDirLen;
    if (remainderLen > 0) {
        outRedirectedPath[curLen++] = sep;
        wmemcpy(outRedirectedPath + curLen, remainder, remainderLen);
        curLen += remainderLen;
    }
    outRedirectedPath[curLen] = L'\0';

    if (isShaderCache && !HasWriteAccessMask(dwDesiredAccess)) {
        DWORD attrs = GetFileAttributesW(outRedirectedPath);
        if (attrs == INVALID_FILE_ATTRIBUTES) {
            return false;
        }
    }

    if (HasWriteAccessMask(dwDesiredAccess)) {
        wchar_t dirBuf[MAX_PATH * 2] = {0};
        if (curLen < sizeof(dirBuf) / sizeof(wchar_t)) {
            wmemcpy(dirBuf, outRedirectedPath, curLen + 1);
            wchar_t* lastSep = wcsrchr(dirBuf, L'\\');
            wchar_t* lastFwd = wcsrchr(dirBuf, L'/');
            wchar_t* dirSep = (lastFwd && (!lastSep || lastFwd > lastSep)) ? lastFwd : lastSep;
            if (dirSep) {
                *dirSep = L'\0';
                std::error_code ec;
                std::filesystem::create_directories(std::filesystem::path(dirBuf), ec);
            }
        }
    }

    return true;
}

bool TryRedirectUserdataDirA(
    const char* lpFileName,
    DWORD dwDesiredAccess,
    char* outRedirectedPath,
    size_t outCap) noexcept
{
    if (outRedirectedPath && outCap > 0) {
        outRedirectedPath[0] = '\0';
    }
    if (!lpFileName || lpFileName[0] == '\0' || !outRedirectedPath || outCap == 0) {
        return false;
    }

    char customUserDir[MAX_PATH] = {0};
    DWORD envLen = GetEnvironmentVariableA("KONGOR_USERDATA_DIR", customUserDir, MAX_PATH);
    if (envLen == 0 || envLen >= MAX_PATH) {
        return false;
    }

    const char* match = nullptr;
    const char* p = lpFileName;
    while (*p) {
        if (_strnicmp(p, "userdata", 8) == 0) {
            bool startOk = (p == lpFileName) || (*(p - 1) == '\\' || *(p - 1) == '/');
            bool endOk = (p[8] == '\0') || (p[8] == '\\' || p[8] == '/');
            if (startOk && endOk) {
                match = p;
                break;
            }
        }
        ++p;
    }

    if (!match) {
        return false;
    }

    size_t prefixLen = static_cast<size_t>(match - lpFileName);
    const char* afterUserdata = match + 8;
    const char* remainder = afterUserdata;
    char sep = '\\';
    if (*remainder == '\\' || *remainder == '/') {
        sep = *remainder;
        ++remainder;
    }

    bool isShaderCache = (_stricmp(remainder, "shadercache.s2z") == 0);

    size_t customDirLen = strlen(customUserDir);
    size_t remainderLen = strlen(remainder);
    size_t needed = prefixLen + customDirLen + (remainderLen > 0 ? (1 + remainderLen) : 0) + 1;
    if (needed > outCap) {
        return false;
    }

    if (prefixLen > 0) {
        memcpy(outRedirectedPath, lpFileName, prefixLen);
    }
    memcpy(outRedirectedPath + prefixLen, customUserDir, customDirLen);
    size_t curLen = prefixLen + customDirLen;
    if (remainderLen > 0) {
        outRedirectedPath[curLen++] = sep;
        memcpy(outRedirectedPath + curLen, remainder, remainderLen);
        curLen += remainderLen;
    }
    outRedirectedPath[curLen] = '\0';

    if (isShaderCache && !HasWriteAccessMask(dwDesiredAccess)) {
        DWORD attrs = GetFileAttributesA(outRedirectedPath);
        if (attrs == INVALID_FILE_ATTRIBUTES) {
            return false;
        }
    }

    if (HasWriteAccessMask(dwDesiredAccess)) {
        char dirBuf[MAX_PATH * 2] = {0};
        if (curLen < sizeof(dirBuf)) {
            memcpy(dirBuf, outRedirectedPath, curLen + 1);
            char* lastSep = strrchr(dirBuf, '\\');
            char* lastFwd = strrchr(dirBuf, '/');
            char* dirSep = (lastFwd && (!lastSep || lastFwd > lastSep)) ? lastFwd : lastSep;
            if (dirSep) {
                *dirSep = '\0';
                std::error_code ec;
                std::filesystem::create_directories(std::filesystem::path(dirBuf), ec);
            }
        }
    }

    return true;
}

} // namespace Kongor
