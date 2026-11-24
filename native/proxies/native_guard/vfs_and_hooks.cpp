// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - NativeGuard VFS & Engine Hooking Subsystem
// ============================================================================

#include "native_guard_internal.h"
#include "cursor_confinement.h"
#include "vfs_loose_cache.h"
#include "modern_archive_hook.h"
#include "runtime_patch_controller.h"
#include "kongor_assert.h"
#include "kongor_overflow.h"
#include "kongor_constpool.h"
#include "kongor_base_cmd.h"
#include "kongor_lua_shared_mem.h"
#include "asset_prewarmer.h"
#include "concurrentqueue.h"
#include "queue_arcade_hooks.h"
#include "vmt_hook_manager.h"
#include "entity_field_container.h"
#include "kongor_sstream.h"
#include "kongor_tracy.h"
#include <windows.h>
#include <windowsx.h>
#include <cstdint>
#include <string>
#include <vector>
#include <atomic>
#include <mutex>
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cwchar>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <mmsystem.h>
#include <array>

static Kongor_SoundNegCacheTable g_soundNegCache;
static std::atomic<void**> g_ppIGameInstance{nullptr};
static constexpr const char* kSymIGameInstance = "?s_pGame@IGame@@0PEAV1@EA";

std::atomic<uint64_t> g_cachedLuaMemoryBytes{ 0 };
std::atomic<bool> g_rawInputEnabled{ true };
std::atomic<bool> g_rawInputRegistered{ false };
std::atomic<uint64_t> g_rawPacketsProcessed{ 0 };
std::atomic<uint64_t> g_mouseMoveSuppressedCount{ 0 };
std::atomic<float> g_mouseSensitivity{ 1.0f };
std::atomic<uint64_t> g_voicePttFlags{ 0 };
BOOL (WINAPI *g_pfnRealClipCursor)(const RECT* lpRect) = ::ClipCursor;
HANDLE (WINAPI *g_pfnRealCreateFileW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE) = ::CreateFileW;
Kongor::CursorStateMachine g_cursorStateMachine;
std::atomic<bool> g_currentlyConfined{ false };
CRITICAL_SECTION g_cursorLock;

void* g_origSetUnhandledExceptionFilter = nullptr;

HWND g_hGameWnd = NULL;
std::atomic<uint64_t> g_activeMainThreadOpStartTick{0};
std::atomic<bool> g_mainThreadBusy{false};
std::atomic<int32_t> g_rawAccumulatorX{ 0 };
std::atomic<int32_t> g_rawAccumulatorY{ 0 };

void** g_ppLuaScriptManager = nullptr;
FnCountMemoryUsed g_pfnCountMemoryUsed = nullptr;
std::atomic<bool> g_csystemErrorTriggered{ false };

// (#177) Decoupled Raw Input Event Ring & 256-Key State Array
struct DecoupledRawInputEvent {
    uint32_t eventKind = 0; // 1 = mouse delta, 2 = key down, 3 = key up
    int32_t  deltaX    = 0;
    int32_t  deltaY    = 0;
    uint8_t  virtualKey = 0;
    uint64_t timestampTick = 0;
};

class KeyStateTracker {
public:
    void OnKeyDown(uint8_t vk) noexcept {
        bool wasDown = m_currentDown[vk].exchange(true, std::memory_order_acq_rel);
        if (!wasDown) {
            m_justPressedThisTick[vk].store(true, std::memory_order_release);
        }
    }

    void OnKeyUp(uint8_t vk) noexcept {
        m_currentDown[vk].store(false, std::memory_order_release);
    }

    bool IsKeyDown(uint8_t vk) const noexcept {
        return m_currentDown[vk].load(std::memory_order_acquire);
    }

    bool WasKeyJustPressed(uint8_t vk) const noexcept {
        return m_justPressedThisTick[vk].load(std::memory_order_acquire);
    }

    void CommitTickFrame() noexcept {
        for (size_t i = 0; i < 256; ++i) {
            m_justPressedThisTick[i].store(false, std::memory_order_release);
        }
    }

private:
    std::array<std::atomic<bool>, 256> m_currentDown{};
    std::array<std::atomic<bool>, 256> m_justPressedThisTick{};
};

static moodycamel::ConcurrentQueue<DecoupledRawInputEvent> g_decoupledInputQueue;
static KeyStateTracker g_keyStateTracker;


#pragma pack(pop)
static_assert(sizeof(K2StringW) == 32, "K2StringW must be exactly 32 bytes");

// [L2-P0-02] Guard g_lastSelectedTaunt and g_lastSelectedAnnouncer against cross-thread data races
// between CConsole::Execute / CClientAccount::SelectUpgrade writers and CPlayer::UpdateUpgrades readers.
static SRWLOCK g_selectedUpgradeLock = SRWLOCK_INIT;
static std::wstring g_lastSelectedTaunt = L"Wolf_Time_Taunt";
static std::wstring g_lastSelectedAnnouncer = L"ASMR";
static std::atomic<bool> g_selectedUpgradeDirty{ true };

static std::wstring GetSelectedTauntSafe() {
    AcquireSRWLockShared(&g_selectedUpgradeLock);
    std::wstring copy = g_lastSelectedTaunt;
    ReleaseSRWLockShared(&g_selectedUpgradeLock);
    return copy;
}

static std::wstring GetSelectedAnnouncerSafe() {
    AcquireSRWLockShared(&g_selectedUpgradeLock);
    std::wstring copy = g_lastSelectedAnnouncer;
    ReleaseSRWLockShared(&g_selectedUpgradeLock);
    return copy;
}

static bool SetSelectedTauntSafe(const std::wstring& value) {
    bool changed = false;
    AcquireSRWLockExclusive(&g_selectedUpgradeLock);
    if (g_lastSelectedTaunt != value) {
        g_lastSelectedTaunt = value;
        changed = true;
    }
    ReleaseSRWLockExclusive(&g_selectedUpgradeLock);
    if (changed) {
        g_selectedUpgradeDirty.store(true, std::memory_order_release);
    }
    return changed;
}

static bool SetSelectedAnnouncerSafe(const std::wstring& value) {
    bool changed = false;
    AcquireSRWLockExclusive(&g_selectedUpgradeLock);
    if (g_lastSelectedAnnouncer != value) {
        g_lastSelectedAnnouncer = value;
        changed = true;
    }
    ReleaseSRWLockExclusive(&g_selectedUpgradeLock);
    if (changed) {
        g_selectedUpgradeDirty.store(true, std::memory_order_release);
    }
    return changed;
}



static void UpdateSelectedUpgradeFromStr(const wchar_t* wsz) {
    if (!wsz || !*wsz) return;

    const wchar_t* pT = wcsstr(wsz, L"cg_selectedTaunt");
    if (pT) {
        pT += 16;
        while (*pT == L' ' || *pT == L'\t' || *pT == L'=' || *pT == L'"' || *pT == L'\'') pT++;
        std::wstring name;
        while (*pT && *pT != L' ' && *pT != L'\t' && *pT != L'"' && *pT != L'\'' && *pT != L';' && *pT != L'\r' && *pT != L'\n') {
            name.push_back(*pT++);
        }
        if (name.rfind(L"t.", 0) == 0) name = name.substr(2);
        if (!name.empty()) {
            SetSelectedTauntSafe(name);
            char dbg[256];
            snprintf(dbg, sizeof(dbg), "[KONGOR NATIVE GUARD] Console updated selected taunt: %ls", name.c_str());
            WriteSentinelLine(dbg);
        }
        return;
    }

    const wchar_t* pAv = wcsstr(wsz, L"cg_selectedAnnouncer");
    if (pAv) {
        pAv += 20;
        while (*pAv == L' ' || *pAv == L'\t' || *pAv == L'=' || *pAv == L'"' || *pAv == L'\'') pAv++;
        std::wstring name;
        while (*pAv && *pAv != L' ' && *pAv != L'\t' && *pAv != L'"' && *pAv != L'\'' && *pAv != L';' && *pAv != L'\r' && *pAv != L'\n') {
            name.push_back(*pAv++);
        }
        if (name.rfind(L"av.", 0) == 0) name = name.substr(3);
        if (!name.empty()) {
            SetSelectedAnnouncerSafe(name);
            char dbg[256];
            snprintf(dbg, sizeof(dbg), "[KONGOR NATIVE GUARD] Console updated selected announcer: %ls", name.c_str());
            WriteSentinelLine(dbg);
        }
        return;
    }

    const wchar_t* pUpgrade = wcsstr(wsz, L"SelectUpgrade ");
    if (pUpgrade) {
        wsz = pUpgrade + 14;
        while (*wsz == L' ' || *wsz == L'\t' || *wsz == L'"' || *wsz == L'\'') wsz++;
    }

    if (wcsncmp(wsz, L"t.", 2) == 0) {
        const wchar_t* pName = wsz + 2;
        std::wstring name;
        while (*pName && *pName != L' ' && *pName != L'\t' && *pName != L'"' && *pName != L'\'' && *pName != L';' && *pName != L'\r' && *pName != L'\n') {
            name.push_back(*pName++);
        }
        if (!name.empty()) {
            SetSelectedTauntSafe(name);
            char dbg[256];
            snprintf(dbg, sizeof(dbg), "[KONGOR NATIVE GUARD] User equipped taunt: %ls", name.c_str());
            WriteSentinelLine(dbg);
        }
    } else if (wcsncmp(wsz, L"av.", 3) == 0) {
        const wchar_t* pName = wsz + 3;
        std::wstring name;
        while (*pName && *pName != L' ' && *pName != L'\t' && *pName != L'"' && *pName != L'\'' && *pName != L';' && *pName != L'\r' && *pName != L'\n') {
            name.push_back(*pName++);
        }
        if (!name.empty()) {
            SetSelectedAnnouncerSafe(name);
            char dbg[256];
            snprintf(dbg, sizeof(dbg), "[KONGOR NATIVE GUARD] User equipped announcer: %ls", name.c_str());
            WriteSentinelLine(dbg);
        }
    }
}

// WideToUtf8 declared in native_guard_internal.h

typedef void (__fastcall *FnConsoleExecute)(void* pConsole, const void* pK2StringW, unsigned int flags);
typedef void (__fastcall *FnConsoleAddCmdBuffer)(void* pConsole, const void* pK2StringW);

static FnConsoleExecute g_pfnConsoleExecute = nullptr;
static FnConsoleExecute g_origConsoleExecute = nullptr;
static FnConsoleAddCmdBuffer g_pfnConsoleAddCmdBuffer = nullptr;
static void** g_ppConsole = nullptr;
// g_ppLuaScriptManager defined at top of vfs_and_hooks.cpp
// ResolveK2LuaSymbols declared in native_guard_internal.h
static void ApplyCustomDamageFloatsFontScale(bool enabled);
static void NotifyConsoleConstrainCursorWrite(const wchar_t* wsz);
static void ApplyCursorConfinementPolicy(const char* triggerTag);
static void SyncActiveHostClientOfflineNickname(const char* triggerTag);
static bool ResolveK2ConsoleSymbols();

static std::atomic<uint16_t> g_activeClientLocalSvrPort{0};
static thread_local bool t_inPracticePortDispatch = false;



static thread_local bool t_inCompoundBaseCmdDispatch = false;

static bool TryDispatchNativeGuardBaseCmdW(
    Kongor::KongorConsoleCmdId cmdId,
    const wchar_t* pCmdArgs)
{
    if (!pCmdArgs) {
        pCmdArgs = L"";
    }
    switch (cmdId) {
        case Kongor::KongorConsoleCmdId::EvalRes: {
            uint64_t evalId = 0;
            bool ok = false;
            std::string resUtf8;
            if (ParseEvalResCommandArgsW(pCmdArgs, &evalId, &ok, &resUtf8)) {
                Kongor::CompletePendingLuaEval(evalId, ok, resUtf8);
            }
            return true; // Consumed, prevent "Unknown command" in console
        }
        case Kongor::KongorConsoleCmdId::PrewarmHero: {
            if (*pCmdArgs) {
                std::string sArg = WideToUtf8(std::wstring(pCmdArgs));
                std::string heroName = sArg;
                std::string avatarKey = "";
                size_t sp = sArg.find_first_of(" \t");
                if (sp != std::string::npos) {
                    heroName = sArg.substr(0, sp);
                    avatarKey = sArg.substr(sp + 1);
                    while (!avatarKey.empty() && (avatarKey.front() == ' ' || avatarKey.front() == '\t')) avatarKey.erase(0, 1);
                }
                Kongor::EnqueueHeroForPrewarming(heroName, avatarKey);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::PrewarmMap: {
            if (*pCmdArgs) {
                std::string mapName = WideToUtf8(std::wstring(pCmdArgs));
                while (!mapName.empty() && (mapName.back() == ' ' || mapName.back() == '\t' || mapName.back() == '\r' || mapName.back() == '\n')) {
                    mapName.pop_back();
                }
                Kongor::EnqueueMapForPrewarming(mapName);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::PrewarmPostIntro: {
            std::string mapName = "caldavar";
            if (pCmdArgs && *pCmdArgs) {
                std::string parsed = WideToUtf8(std::wstring(pCmdArgs));
                while (!parsed.empty() && (parsed.back() == ' ' || parsed.back() == '\t' || parsed.back() == '\r' || parsed.back() == '\n')) {
                    parsed.pop_back();
                }
                while (!parsed.empty() && (parsed.front() == ' ' || parsed.front() == '\t')) {
                    parsed.erase(0, 1);
                }
                if (!parsed.empty()) {
                    mapName = parsed;
                }
            }
            Kongor::TriggerPostIntroBackgroundWarmup(mapName);
            return true; // Consumed
        }
        case Kongor::KongorConsoleCmdId::PrewarmForegroundBusy: {
            if (pCmdArgs && *pCmdArgs) {
                std::string arg = WideToUtf8(std::wstring(pCmdArgs));
                while (!arg.empty() && (arg.front() == ' ' || arg.front() == '\t')) arg.erase(0, 1);
                while (!arg.empty() && (arg.back() == ' ' || arg.back() == '\t' || arg.back() == '\r' || arg.back() == '\n')) arg.pop_back();
                bool busy = (arg == "1" || _stricmp(arg.c_str(), "true") == 0 || _stricmp(arg.c_str(), "on") == 0);
                Kongor::SetPrewarmerForegroundBusy(busy);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::PrewarmCancel: {
            std::string reason = "console_cancel";
            if (pCmdArgs && *pCmdArgs) {
                std::string parsed = WideToUtf8(std::wstring(pCmdArgs));
                while (!parsed.empty() && (parsed.front() == ' ' || parsed.front() == '\t')) parsed.erase(0, 1);
                while (!parsed.empty() && (parsed.back() == ' ' || parsed.back() == '\t' || parsed.back() == '\r' || parsed.back() == '\n')) parsed.pop_back();
                if (!parsed.empty()) reason = parsed;
            }
            Kongor::CancelPostIntroWarmup(reason.c_str());
            return true; // Consumed
        }
        case Kongor::KongorConsoleCmdId::PrewarmTelemetry: {
            if (pCmdArgs && *pCmdArgs) {
                std::string raw = WideToUtf8(std::wstring(pCmdArgs));
                char outcomeBuf[64] = "COMPLETE";
                unsigned long long elapsedMs = 0ULL;
                char mapBuf[64] = "caldavar";
                (void)sscanf_s(raw.c_str(), "%63s %llu %63s",
                    outcomeBuf, static_cast<unsigned>(sizeof(outcomeBuf)),
                    &elapsedMs,
                    mapBuf, static_cast<unsigned>(sizeof(mapBuf)));
                Kongor::LogPlayNowPreloadTelemetry(outcomeBuf, static_cast<uint64_t>(elapsedMs), mapBuf);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::VfsStats:
        case Kongor::KongorConsoleCmdId::ArchiveStats: {
            LogArchiveTelemetrySummary();
            return true; // Consumed
        }
        case Kongor::KongorConsoleCmdId::VfsPath: {
            LogArchiveSearchPaths();
            return true; // Consumed
        }
        case Kongor::KongorConsoleCmdId::VfsFind: {
            if (*pCmdArgs) {
                std::string patternUtf8 = WideToUtf8(std::wstring(pCmdArgs));
                while (!patternUtf8.empty() && (patternUtf8.back() == ' ' || patternUtf8.back() == '\t' || patternUtf8.back() == '\r' || patternUtf8.back() == '\n')) {
                    patternUtf8.pop_back();
                }
                LogArchiveFindFile(patternUtf8.c_str());
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::KongorHookToggle: {
            std::string group = "all";
            std::string action = "status";
            if (*pCmdArgs) {
                std::string sArg = WideToUtf8(std::wstring(pCmdArgs));
                while (!sArg.empty() && (sArg.back() == ' ' || sArg.back() == '\t' || sArg.back() == '\r' || sArg.back() == '\n')) {
                    sArg.pop_back();
                }
                size_t sp = sArg.find_first_of(" \t");
                if (sp != std::string::npos) {
                    group = sArg.substr(0, sp);
                    action = sArg.substr(sp + 1);
                    while (!action.empty() && (action.front() == ' ' || action.front() == '\t')) action.erase(0, 1);
                } else if (!sArg.empty()) {
                    group = sArg;
                }
            }
            char statusJson[4096] = {0};
            Kongor::PatchController::ExecuteHookToggle(group.c_str(), action.c_str(), statusJson, sizeof(statusJson), true);
            OutputDebugStringA(statusJson);
            OutputDebugStringA("\n");
            return true; // Consumed
        }
        case Kongor::KongorConsoleCmdId::VideoSeek: {
            if (*pCmdArgs) {
                float targetSec = (float)_wtof(pCmdArgs);
                if (targetSec < 0.0f) targetSec = 0.0f;

                HMODULE hAvcodec = GetModuleHandleA("avcodec-x64-58.dll");
                if (hAvcodec) {
                    typedef void (*PFN_VideoSeek)(float);
                    PFN_VideoSeek pfnSeek = (PFN_VideoSeek)GetProcAddress(hAvcodec, "Kongor_Video_Seek");
                    if (pfnSeek) {
                        pfnSeek(targetSec);
                    }
                }

                HMODULE hFmod = GetModuleHandleA("fmodex64.dll");
                if (hFmod) {
                    typedef void (*PFN_AudioSeek)(int, unsigned int);
                    PFN_AudioSeek pfnAudioSeek = (PFN_AudioSeek)GetProcAddress(hFmod, "Kongor_Audio_Seek");
                    if (pfnAudioSeek) {
                        pfnAudioSeek(7, (unsigned int)(targetSec * 1000.0f));
                    }
                }
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::SetChatMode: {
            if (*pCmdArgs) {
                uint8_t targetMode = 0;
                const wchar_t* p = pCmdArgs;
                while (*p == L' ' || *p == L'\t') ++p;
                if (*p == L'3' || _wcsicmp(p, L"invisible") == 0 || _wcsicmp(p, L"offline") == 0) {
                    targetMode = 3;
                } else if (*p == L'2' || _wcsicmp(p, L"busy") == 0 || _wcsicmp(p, L"dnd") == 0) {
                    targetMode = 2;
                } else {
                    targetMode = 0;
                }
                Kongor_SetChatMode(targetMode);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::ArcadeMode: {
            if (*pCmdArgs) {
                Kongor::ExecuteConsoleArcadeModeW(pCmdArgs);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::ArcadeReserve: {
            if (*pCmdArgs) {
                Kongor::ExecuteConsoleArcadeReserveW(pCmdArgs);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::ArcadeRelease: {
            if (*pCmdArgs) {
                Kongor::ExecuteConsoleArcadeReleaseW(pCmdArgs);
                return true; // Consumed
            }
            break;
        }
        case Kongor::KongorConsoleCmdId::ArcadeSwapHooks: {
            if (*pCmdArgs) {
                Kongor::ExecuteConsoleArcadeSwapHooksW(pCmdArgs);
                return true; // Consumed
            }
            break;
        }
        default:
            break;
    }
    return false;
}

static void EnsureDetectedMonitorsCvarPopulated(void* pConsole, bool forceRefresh);
static void HandleConsoleSyncMonitorsCommand(void* pConsole);
static void HandleConsoleApplyMonitorCommand(void* pConsole, const wchar_t* wszArgs);
static thread_local bool t_inMonitorConsoleDispatch = false;

static void __fastcall Hooked_CConsole_Execute(void* pConsole, const void* pK2StringW, unsigned int flags) {
    KONGOR_TRACY_ZONE_SCOPED_N("K2_CConsole_Execute");
    g_lastMainThreadHeartbeatTick.store(GetTickCount64(), std::memory_order_relaxed);
    static uint64_t s_lastHeroKeyframeSampleTick = 0;
    uint64_t nowKeyframe = GetTickCount64();
    if (nowKeyframe - s_lastHeroKeyframeSampleTick >= 250) {
        s_lastHeroKeyframeSampleTick = nowKeyframe;
        RefreshServerKeyframePlayersFromEngine();
    }
    if (pConsole && !t_inMonitorConsoleDispatch && !g_isHeadlessProcess && !IsDedicatedServerProcessCached()) {
        EnsureDetectedMonitorsCvarPopulated(pConsole, false);
    }
    bool isReloadInterfaces = false;
    bool touchedConstrainCursor = false;
    bool touchedOfflineNick = false;
    bool handledByPracticePortDispatch = false;
    if (pK2StringW) {
        const wchar_t* wsz = GetK2StringData(pK2StringW);
        if (wsz && *wsz) {
            if (_wcsicmp(wsz, L"__kongor_keyframe_sample") == 0) {
                RefreshServerKeyframePlayersFromEngine();
                return;
            }
            if (_wcsicmp(wsz, L"__kongor_sync_monitors") == 0) {
                HandleConsoleSyncMonitorsCommand(pConsole);
                return;
            }
            if (_wcsnicmp(wsz, L"__kongor_apply_monitor", 22) == 0 &&
                (wsz[22] == L'\0' || wsz[22] == L' ' || wsz[22] == L'\t')) {
                const wchar_t* pArgs = wsz + 22;
                while (*pArgs == L' ' || *pArgs == L'\t') ++pArgs;
                HandleConsoleApplyMonitorCommand(pConsole, pArgs);
                return;
            }
            // Compound ';' / '\n' / '\r' buffer splitter when NativeGuard BaseCmds are mixed with K2 CVars (#135/#137/OBL-QA-05)
            if (!t_inCompoundBaseCmdDispatch) {
                std::vector<Kongor::DispatchedConsoleSegment> compoundSegments;
                if (Kongor::PartitionCompoundBaseCmdBufferW(wsz, compoundSegments)) {
                    struct ScopedCompoundGuard {
                        ScopedCompoundGuard() noexcept { t_inCompoundBaseCmdDispatch = true; }
                        ~ScopedCompoundGuard() noexcept { t_inCompoundBaseCmdDispatch = false; }
                    } compoundGuard;

                    char splitLog[192] = {0};
                    snprintf(
                        splitLog,
                        sizeof(splitLog),
                        "[KONGOR NATIVE GUARD] [BASECMD-SPLIT] Partitioned compound console buffer into %zu ordered segment(s).\n",
                        compoundSegments.size());
                    OutputDebugStringA(splitLog);

                    for (size_t i = 0; i < compoundSegments.size(); ++i) {
                        const Kongor::DispatchedConsoleSegment& seg = compoundSegments[i];
                        if (seg.IsNativeBaseCmd) {
                            TryDispatchNativeGuardBaseCmdW(seg.CmdId, seg.CommandOrArgs.c_str());
                            if (Kongor::ConsumePendingArcadeSafeModeRetirement()) {
                                K2StringW quitCmd(L"Quit");
                                if (g_origConsoleExecute) {
                                    g_origConsoleExecute(pConsole, &quitCmd, flags);
                                }
                                return;
                            }
                        } else if (!seg.CommandOrArgs.empty()) {
                            K2StringW k2Sub(seg.CommandOrArgs.c_str());
                            Hooked_CConsole_Execute(pConsole, &k2Sub, flags);
                        }
                    }
                    return;
                }
            }

            // O(1) 128-bucket sorted-chain BaseCmd lookup for NativeGuard custom console commands (#135, #136)
            // satisfies: OBL-W3-02
            const wchar_t* pCmdArgs = L"";
            const Kongor::BaseCmdNode* matchedCmd = Kongor::GetGlobalBaseCmdTable().MatchCommandLine(wsz, &pCmdArgs);
            if (matchedCmd != nullptr && TryDispatchNativeGuardBaseCmdW(matchedCmd->Id, pCmdArgs)) {
                if (Kongor::ConsumePendingArcadeSafeModeRetirement()) {
                    K2StringW quitCmd(L"Quit");
                    if (g_origConsoleExecute) {
                        g_origConsoleExecute(pConsole, &quitCmd, flags);
                    }
                }
                return;
            }
            // Dedicated Server Reject-Only Guards (S1: AddBot pre-world guard, S2: StartGame mid-session guard)
            if (IsDedicatedServerProcessCached()) {
                if (Kongor::IsQueueArcadeModeActive()) {
                    Kongor::EnsureQueueArcadeK2HooksInstalled();
                }
                void** ppGame = g_ppIGameInstance.load(std::memory_order_acquire);
                if (!ppGame) {
                    HMODULE hGS = GetModuleHandleA("game_shared_x64.dll");
                    if (hGS) {
                        ppGame = reinterpret_cast<void**>(GetProcAddress(hGS, kSymIGameInstance));
                        if (ppGame) g_ppIGameInstance.store(ppGame, std::memory_order_release);
                    }
                }
                const bool hasActiveWorld = (ppGame != nullptr && *ppGame != nullptr);
                const auto guardRes = Kongor_EvaluateServerCommandGuardW(true, hasActiveWorld, wsz);
                if (guardRes == KongorServerCmdGuardResult::RejectAddBotNoWorld) {
                    WriteSentinelLine("[KONGOR SERVER GUARD] Rejected AddBot: No active server world. Wait for world ready before adding bots.");
                    OutputDebugStringA("[KONGOR SERVER GUARD] Rejected AddBot: No active server world. Wait for world ready before adding bots.\n");
                    return;
                } else if (guardRes == KongorServerCmdGuardResult::RejectStartGameWorldActive) {
                    WriteSentinelLine("[KONGOR SERVER GUARD] Rejected StartGame: Active server world already running. Run EndGame first.");
                    OutputDebugStringA("[KONGOR SERVER GUARD] Rejected StartGame: Active server world already running. Run EndGame first.\n");
                    return;
                }
            }
            // Check for quit or exit: write Phase-1 quit-initiated sentinel (keeping session-active on disk
            // until CHost::Shutdown finishes cleanly and writes clean-exit.<pid>.sentinel).
            if (_wcsicmp(wsz, L"quit") == 0 || _wcsicmp(wsz, L"exit") == 0 ||
                wcsstr(wsz, L"; Quit") != nullptr || wcsstr(wsz, L"; quit") != nullptr) {
                MarkQuitInitiated("Console::quit");
                WriteSentinelLine("[KONGOR NATIVE GUARD] [QUIT-INIT] User/script invoked console quit command.");
                LogArchiveTelemetrySummary();
                FlushSentinelLogs(true);
            }
            // Track Disconnect to immediately clear match/map state in shared-memory header
            else if (_wcsicmp(wsz, L"Disconnect") == 0 || _wcsnicmp(wsz, L"Disconnect ", 11) == 0) {
                if (g_pTelemetryHeader) {
                    g_pTelemetryHeader->GamePhase = 0;
                    g_pTelemetryHeader->ActiveMatchId = 0;
                    memset(g_pTelemetryHeader->CurrentMap, 0, sizeof(g_pTelemetryHeader->CurrentMap));
                }
            }
            // Track Set login_name / Set p2p_nickname to keep PlayerName and offline CClientAccount::m_sNickname updated
            else if (_wcsnicmp(wsz, L"Set login_name ", 15) == 0 || _wcsnicmp(wsz, L"Set p2p_nickname ", 17) == 0) {
                const wchar_t* pVal = wsz + (_wcsnicmp(wsz, L"Set login_name ", 15) == 0 ? 15 : 17);
                wchar_t sanitized[32] = {0};
                if (Kongor_SanitizeOfflineNicknameCandidateW(pVal, sanitized, _countof(sanitized))) {
                    wcsncpy_s(g_wszTrackedOfflineNick, _countof(g_wszTrackedOfflineNick), sanitized, _TRUNCATE);
                    if (g_pTelemetryHeader) {
                        char nameBuf[sizeof(g_pTelemetryHeader->PlayerName)] = {0};
                        size_t idx = 0;
                        while (sanitized[idx] != L'\0' && idx + 1 < sizeof(nameBuf)) {
                            nameBuf[idx] = static_cast<char>(sanitized[idx]);
                            ++idx;
                        }
                        if (idx > 0) {
                            memcpy(g_pTelemetryHeader->PlayerName, nameBuf, sizeof(nameBuf));
                        }
                    }
                } else {
                    g_wszTrackedOfflineNick[0] = L'\0';
                }
                touchedOfflineNick = true;
            }
            // Ensure KongorSharedMem is bound before any Script command executes
            else if (_wcsnicmp(wsz, L"Script ", 7) == 0 || _wcsnicmp(wsz, L"Script\t", 7) == 0) {
                if (ResolveK2LuaSymbols() && g_ppLuaScriptManager && *g_ppLuaScriptManager) {
                    void* L = *reinterpret_cast<void**>(*g_ppLuaScriptManager);
                    if (L) {
                        Kongor::EnsureKongorLuaSharedMem(L, &g_shmHandle);
                    }
                }
            }
            else if (_wcsnicmp(wsz, L"ReloadInterfaces", 16) == 0 ||
                     _wcsnicmp(wsz, L"ReloadInterface ", 16) == 0 ||
                     wcsstr(wsz, L"ui_reloadInterfaces") != nullptr) {
                isReloadInterfaces = true;
            }
            // Invalidate negative sample cache & refresh read-only loose-file index whenever archives, mods, or interfaces reload
            // satisfies: OBL-NG-LOADSAMPLE-03
            if (Kongor_ShouldInvalidateSoundNegCacheCommandW(wsz)) {
                Kongor::RebuildVfsLooseCacheAsync();
                const uint32_t clearedSamples = g_soundNegCache.Clear();
                if (clearedSamples > 0) {
                    char clrLog[192] = {0};
                    snprintf(clrLog, sizeof(clrLog),
                             "[KONGOR NATIVE GUARD] [SOUND-NEGCACHE] Cleared %u negative-cached sample path(s) on reload command.",
                             static_cast<unsigned int>(clearedSamples));
                    WriteSentinelLine(clrLog);
                }
            }
            if (wcsstr(wsz, L"cg_stumpsTrees") != nullptr) {
                const wchar_t* pSub = wcsstr(wsz, L"cg_stumpsTrees");
                if (pSub) {
                    pSub += 14;
                    while (*pSub == L' ' || *pSub == L'\t' || *pSub == L'"' || *pSub == L',' || *pSub == L'\'') ++pSub;
                    if (_wcsnicmp(pSub, L"true", 4) == 0 || *pSub == L'1') {
                        Kongor_SetStumpsTreesEnabled(true);
                    } else if (_wcsnicmp(pSub, L"false", 5) == 0 || *pSub == L'0') {
                        Kongor_SetStumpsTreesEnabled(false);
                    }
                }
            }
            if (wcsstr(wsz, L"cg_customDamageFloats") != nullptr) {
                const wchar_t* pSub = wcsstr(wsz, L"cg_customDamageFloats");
                if (pSub) {
                    pSub += 21;
                    while (*pSub == L' ' || *pSub == L'\t' || *pSub == L'"' || *pSub == L',' || *pSub == L'\'') ++pSub;
                    if (_wcsnicmp(pSub, L"true", 4) == 0 || *pSub == L'1') {
                        Kongor_SetCustomDamageFloatsEnabled(true);
                        ApplyCustomDamageFloatsFontScale(true);
                    } else if (_wcsnicmp(pSub, L"false", 5) == 0 || *pSub == L'0') {
                        Kongor_SetCustomDamageFloatsEnabled(false);
                        ApplyCustomDamageFloatsFontScale(false);
                    }
                }
            }
            if (wcsstr(wsz, L"cg_selectedTaunt") != nullptr ||
                wcsstr(wsz, L"cg_selectedAnnouncer") != nullptr ||
                wcsstr(wsz, L"SelectUpgrade") != nullptr) {
                UpdateSelectedUpgradeFromStr(wsz);
            }
            if (wcsstr(wsz, L"cg_constrainCursor") != nullptr) {
                NotifyConsoleConstrainCursorWrite(wsz);
                touchedConstrainCursor = true;
            }
            char activeMapStem[64] = {0};
            if (wcsstr(wsz, L"map:") != nullptr ||
                wcsstr(wsz, L"MAP:") != nullptr ||
                wcsstr(wsz, L"host_map") != nullptr ||
                wcsstr(wsz, L"HOST_MAP") != nullptr) {
                Kongor_ExtractMapNameFromConsoleCmdW(wsz, activeMapStem, sizeof(activeMapStem));
            }

            if (!IsDedicatedServerProcessCached() && !t_inPracticePortDispatch) {
                bool isBotMatch = false;
                bool isReplay = false;
                size_t startGameIdx = 0;
                if (Kongor_IsPracticeStartGameCommandW(wsz, &isBotMatch, &startGameIdx, &isReplay) && g_origConsoleExecute) {
                    struct ScopedDispatchGuard {
                        ScopedDispatchGuard() noexcept { t_inPracticePortDispatch = true; }
                        ~ScopedDispatchGuard() noexcept { t_inPracticePortDispatch = false; }
                    } guard;

                    const uint16_t gamePort = Kongor_SelectAvailableUdpPortInBands(
                        isBotMatch ? kKongorBotMatchPortStart : kKongorPracticePortStart,
                        isBotMatch ? kKongorBotMatchPortEnd   : kKongorPracticePortEnd,
                        isBotMatch ? kKongorBotMatchFallbackStart : kKongorPracticeFallbackStart,
                        isBotMatch ? kKongorBotMatchFallbackEnd   : kKongorPracticeFallbackEnd);

                    const uint16_t voicePort = Kongor_SelectAvailableUdpPortInBands(
                        isBotMatch ? kKongorBotMatchVoicePortStart : kKongorPracticeVoicePortStart,
                        isBotMatch ? kKongorBotMatchVoicePortEnd   : kKongorPracticeVoicePortEnd,
                        kKongorClientVoiceFallbackStart,
                        kKongorClientVoiceFallbackEnd,
                        gamePort);

                    g_activeClientLocalSvrPort.store(gamePort, std::memory_order_release);

                    // 1. If compound command had a prefix before StartGame/StartReplay (e.g. "Set dev true; Set svr_port ...; StartGame ..."),
                    //    run the prefix first so our probed port CVar overrides win deterministically right before CHost::StartServer.
                    if (startGameIdx > 0) {
                        std::wstring prefixCmd(wsz, startGameIdx);
                        while (!prefixCmd.empty() &&
                               (prefixCmd.back() == L' ' || prefixCmd.back() == L'\t' || prefixCmd.back() == L';')) {
                            prefixCmd.pop_back();
                        }
                        if (!prefixCmd.empty()) {
                            K2StringW k2Prefix(prefixCmd.c_str());
                            g_origConsoleExecute(pConsole, &k2Prefix, flags);
                        }
                    }

                    // 2. Apply verified free game & voice UDP ports and loopback isolation before CHost::StartServer
                    wchar_t portCmdW[256] = {0};
                    swprintf_s(
                        portCmdW,
                        _countof(portCmdW),
                        L"Set svr_port %u; Set svr_voicePortStart %u; Set svr_voicePortEnd %u; Set svr_ip 127.0.0.1; Set svr_broadcast false",
                        static_cast<unsigned int>(gamePort),
                        static_cast<unsigned int>(voicePort),
                        static_cast<unsigned int>(voicePort));
                    K2StringW k2PortCmd(portCmdW);
                    g_origConsoleExecute(pConsole, &k2PortCmd, flags);

                    char portLog[256] = {0};
                    snprintf(
                        portLog,
                        sizeof(portLog),
                        "[KONGOR NATIVE GUARD] [PORT-ALLOC] Allocated client local server UDP ports: mode=%s svr_port=%u voicePort=%u map=%s",
                        isReplay ? "replay" : (isBotMatch ? "botmatch" : "practice"),
                        static_cast<unsigned int>(gamePort),
                        static_cast<unsigned int>(voicePort),
                        activeMapStem[0] ? activeMapStem : (isReplay ? "<replay>" : "<default>"));
                    WriteSentinelLine(portLog);

                    // 3. Execute StartGame/StartReplay segment with the guaranteed-free svr_port active
                    std::wstring startCmd(wsz + startGameIdx);
                    K2StringW k2StartCmd(startCmd.c_str());
                    g_origConsoleExecute(pConsole, &k2StartCmd, flags);
                    handledByPracticePortDispatch = true;
                } else if (Kongor_IsClientLocalSessionEndCommandW(wsz)) {
                    const uint16_t prevPort = g_activeClientLocalSvrPort.exchange(0, std::memory_order_acq_rel);
                    if (prevPort != 0) {
                        char clearLog[160] = {0};
                        snprintf(clearLog, sizeof(clearLog),
                                 "[KONGOR NATIVE GUARD] [PORT-ALLOC] Cleared active local svr_port=%u on session teardown",
                                 static_cast<unsigned int>(prevPort));
                        WriteSentinelLine(clearLog);
                    }
                }
            }
        }
    }

    if (g_origConsoleExecute && !handledByPracticePortDispatch) {
        g_origConsoleExecute(pConsole, pK2StringW, flags);
    }

    if (touchedConstrainCursor) {
        ApplyCursorConfinementPolicy("Console::cg_constrainCursor");
    }

    if (touchedOfflineNick) {
        SyncActiveHostClientOfflineNickname("Console::SetNick");
    }

    if (isReloadInterfaces) {
        Kongor::InvalidateKongorLuaSharedMem();
        void* pLuaState = nullptr;
        if (ResolveK2LuaSymbols() && g_ppLuaScriptManager && *g_ppLuaScriptManager) {
            pLuaState = *reinterpret_cast<void**>(*g_ppLuaScriptManager);
            if (pLuaState) {
                Kongor::EnsureKongorLuaSharedMem(pLuaState, &g_shmHandle);
            }
        }
        // Only dispatch CConsole 'Script' when CLuaScriptManager has an active lua_State*;
        // during early CHost::Init ReloadInterfaces, m_pLuaState (+0x00) is NULL and k2_x64+0x92A260 would dereference NULL+0x10.
        if (pLuaState && !IsDedicatedServerProcessCached() && pConsole && (g_origConsoleExecute || g_pfnConsoleAddCmdBuffer)) {
            K2StringW k2Flush(L"Script \"if ClearGetWidgetCache then pcall(ClearGetWidgetCache) end\"");
            if (g_pfnConsoleAddCmdBuffer) {
                g_pfnConsoleAddCmdBuffer(pConsole, &k2Flush);
            } else if (g_origConsoleExecute) {
                g_origConsoleExecute(pConsole, &k2Flush, 0);
            }
            OutputDebugStringA("[KONGOR NATIVE GUARD] ReloadInterfaces: re-bound KongorSharedMem and dispatched Script ClearGetWidgetCache() flush.\n");
        } else if (!pLuaState) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] ReloadInterfaces: skipped Script ClearGetWidgetCache() flush because CLuaScriptManager lua_State* is NULL (early init).\n");
        }
    }
}

extern "C" bool InstallK2SafeHooks(HMODULE hK2);

extern "C" bool InstallConsoleHooks(HMODULE hK2) {
    if (!hK2) return false;
    InstallK2SafeHooks(hK2);
    static bool s_consoleHooked = false;
    if (s_consoleHooked) return true;

    void* pfnExecute = ResolveExport(
        hK2,
        "?Execute@CConsole@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@I@Z",
        0x843880);

    if (pfnExecute) {
        if (InstallInlineHook64(pfnExecute, reinterpret_cast<void*>(Hooked_CConsole_Execute),
            reinterpret_cast<void**>(&g_origConsoleExecute), 15)) {
            s_consoleHooked = true;
            OutputDebugStringA("[KONGOR NATIVE GUARD] Console hooks installed: prewarm_hero and prewarm_map commands active.\n");
            return true;
        }
    }
    return false;
}

static bool g_warnedAddCmdBufferFallback = false;

static bool ResolveK2ConsoleSymbols() {
    if (g_ppConsole && *g_ppConsole && (g_pfnConsoleAddCmdBuffer || g_pfnConsoleExecute)) {
        return true;
    }

    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) {
        return false;
    }

    InstallConsoleHooks(hK2);

    if (!g_ppConsole) {
        g_ppConsole = (void**)GetProcAddress(hK2, "?g_pConsole@@3PEAVCConsole@@EA");
    }
    if (!g_pfnConsoleAddCmdBuffer) {
        g_pfnConsoleAddCmdBuffer = (FnConsoleAddCmdBuffer)GetProcAddress(
            hK2, "?AddCmdBuffer@CConsole@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z");
    }
    if (!g_pfnConsoleExecute) {
        g_pfnConsoleExecute = (FnConsoleExecute)GetProcAddress(
            hK2, "?Execute@CConsole@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@I@Z");
    }

    if (!g_pfnConsoleAddCmdBuffer && g_pfnConsoleExecute && !g_warnedAddCmdBufferFallback) {
        g_warnedAddCmdBufferFallback = true;
        OutputDebugStringA("[NATIVE_GUARD] AddCmdBuffer unresolved — falling back to unsafe Execute\n");
    }

    return (g_ppConsole != nullptr && *g_ppConsole != nullptr && (g_pfnConsoleAddCmdBuffer != nullptr || g_pfnConsoleExecute != nullptr));
}

// g_hGameWnd defined at top of vfs_and_hooks.cpp
static WNDPROC g_pfnOrigWndProc = NULL;
// WM_KONGOR_DISPATCH_CMD declared in native_guard_internal.h
// WM_KONGOR_BG_HUMAN_CLICK declared in native_guard_internal.h

static std::atomic<bool> g_bgClickOverrideActive{ false };
static std::atomic<int32_t> g_bgClickScreenX{ 0 };
static std::atomic<int32_t> g_bgClickScreenY{ 0 };
static std::atomic<uint64_t> g_bgClickExpireTick{ 0 };

struct SubTickCommand {
    int64_t hardwareTimestampQpc;
    uint32_t sequence;
    std::wstring command;

    bool operator<(const SubTickCommand& other) const {
        if (hardwareTimestampQpc != other.hardwareTimestampQpc) {
            return hardwareTimestampQpc < other.hardwareTimestampQpc;
        }
        return sequence < other.sequence;
    }
};

static moodycamel::ConcurrentQueue<SubTickCommand, 4096> g_cmdQueue;
static std::atomic<uint32_t> g_subTickSeqGen{ 0 };

extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_GetSubTickQueueDepth(void) {
    return static_cast<uint32_t>(g_cmdQueue.size_approx());
}

// ============================================================================
// KONGOR HIGH-POLLING-RATE RAW INPUT ENGINE (8000Hz MODERNIZATION)
// ============================================================================

// g_rawInputEnabled defined at top of vfs_and_hooks.cpp
// g_rawInputRegistered defined at top of vfs_and_hooks.cpp
// g_rawAccumulatorX defined at top of vfs_and_hooks.cpp
// g_rawAccumulatorY defined at top of vfs_and_hooks.cpp
// g_rawPacketsProcessed defined at top of vfs_and_hooks.cpp
// g_mouseMoveSuppressedCount defined at top of vfs_and_hooks.cpp
// g_mouseSensitivity defined at top of vfs_and_hooks.cpp

static std::atomic<bool> g_gameWndFocused{ true };
static std::atomic<int> g_clientOriginX{ 0 };
static std::atomic<int> g_clientOriginY{ 0 };
static std::atomic<int> g_clientWindowWidth{ 1920 };
static std::atomic<int> g_clientWindowHeight{ 1080 };

static double g_virtualCursorX = 0.0;
static double g_virtualCursorY = 0.0;
static bool g_virtualCursorInitialized = false;
// g_cursorLock defined at top of vfs_and_hooks.cpp

typedef BOOL (WINAPI *PFN_GetCursorPos)(LPPOINT lpPoint);
typedef BOOL (WINAPI *PFN_SetCursorPos)(int X, int Y);
typedef BOOL (WINAPI *PFN_ClipCursor)(const RECT* lpRect);
typedef HANDLE (WINAPI *PFN_CreateFileW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef HANDLE (WINAPI *PFN_CreateFileA)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef int (__cdecl *PFN_waccess)(const wchar_t*, int);

static PFN_GetCursorPos g_pfnRealGetCursorPos = ::GetCursorPos;
static PFN_SetCursorPos g_pfnRealSetCursorPos = ::SetCursorPos;
// g_pfnRealClipCursor defined at top of vfs_and_hooks.cpp
// g_pfnRealCreateFileW defined at top of vfs_and_hooks.cpp
static PFN_CreateFileA  g_pfnRealCreateFileA  = ::CreateFileA;
static PFN_waccess      g_pfnRealWaccess      = ::_waccess;

static int __cdecl HookedWaccess(const wchar_t* path, int mode) {
    return Kongor::EvaluateVfsLooseWaccess(path, mode, g_pfnRealWaccess);
}

// g_cursorStateMachine defined at top of vfs_and_hooks.cpp
// g_currentlyConfined defined at top of vfs_and_hooks.cpp
static std::atomic<bool> g_constrainCvarDirty{ true };
static std::atomic<uint64_t> g_lastConstrainCvarPollTick{ 0 };

// PE Import Address Table (IAT) Hook Helper
static bool HookImport(HMODULE hTargetModule, const char* targetDllName, const char* functionName, void* newFunction, void** outOrigFunction, bool* outNewlyHooked = nullptr) {
    if (outNewlyHooked) *outNewlyHooked = false;
    if (!hTargetModule || !targetDllName || !functionName || !newFunction) return false;

    auto pDosHeader = reinterpret_cast<PIMAGE_DOS_HEADER>(hTargetModule);
    if (pDosHeader->e_magic != IMAGE_DOS_SIGNATURE) return false;

    auto pNtHeaders = reinterpret_cast<PIMAGE_NT_HEADERS>(
        reinterpret_cast<uint8_t*>(hTargetModule) + pDosHeader->e_lfanew);
    if (pNtHeaders->Signature != IMAGE_NT_SIGNATURE) return false;

    IMAGE_DATA_DIRECTORY importDir = pNtHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!importDir.VirtualAddress || !importDir.Size) return false;

    auto pImportDesc = reinterpret_cast<PIMAGE_IMPORT_DESCRIPTOR>(
        reinterpret_cast<uint8_t*>(hTargetModule) + importDir.VirtualAddress);

    for (; pImportDesc->Name; ++pImportDesc) {
        const char* modName = reinterpret_cast<const char*>(
            reinterpret_cast<uint8_t*>(hTargetModule) + pImportDesc->Name);
        bool dllMatches = (_stricmp(modName, targetDllName) == 0) ||
                          (_strnicmp(targetDllName, "user32", 6) == 0 && _strnicmp(modName, "user32", 6) == 0) ||
                          (_strnicmp(targetDllName, "kernel", 6) == 0 &&
                           (_strnicmp(modName, "kernel", 6) == 0 ||
                            _strnicmp(modName, "api-ms-win-core-file", 20) == 0 ||
                            _strnicmp(modName, "api-ms-win-core-processthreads", 30) == 0)) ||
                          (_strnicmp(targetDllName, "api-ms-win-crt-filesystem", 25) == 0 &&
                           (_strnicmp(modName, "api-ms-win-crt-filesystem", 25) == 0 ||
                            _strnicmp(modName, "ucrtbase", 8) == 0 ||
                            _strnicmp(modName, "msvcr", 5) == 0));
        if (dllMatches) {
            auto pThunk = reinterpret_cast<PIMAGE_THUNK_DATA>(
                reinterpret_cast<uint8_t*>(hTargetModule) + pImportDesc->FirstThunk);
            auto pOrigThunk = reinterpret_cast<PIMAGE_THUNK_DATA>(
                reinterpret_cast<uint8_t*>(hTargetModule) + (pImportDesc->OriginalFirstThunk ? pImportDesc->OriginalFirstThunk : pImportDesc->FirstThunk));

            for (; pOrigThunk->u1.AddressOfData; ++pThunk, ++pOrigThunk) {
                if (!(pOrigThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG)) {
                    auto pImportByName = reinterpret_cast<PIMAGE_IMPORT_BY_NAME>(
                        reinterpret_cast<uint8_t*>(hTargetModule) + pOrigThunk->u1.AddressOfData);
                    if (strcmp((const char*)pImportByName->Name, functionName) == 0) {
                        if (pThunk->u1.Function == reinterpret_cast<uintptr_t>(newFunction)) {
                            return true;
                        }
                        DWORD oldProtect = 0;
                        if (VirtualProtect(&pThunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect)) {
                            if (outOrigFunction && !*outOrigFunction) {
                                *outOrigFunction = reinterpret_cast<void*>(pThunk->u1.Function);
                            }
                            pThunk->u1.Function = reinterpret_cast<uintptr_t>(newFunction);
                            VirtualProtect(&pThunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
                            if (outNewlyHooked) *outNewlyHooked = true;
                            return true;
                        }
                    }
                }
            }
        }
    }
    return false;
}

static HANDLE WINAPI HookedCreateFileW(
    LPCWSTR lpFileName,
    DWORD dwDesiredAccess,
    DWORD dwShareMode,
    LPSECURITY_ATTRIBUTES lpSecurityAttributes,
    DWORD dwCreationDisposition,
    DWORD dwFlagsAndAttributes,
    HANDLE hTemplateFile)
{
    wchar_t userRedirected[MAX_PATH * 2] = {0};
    const wchar_t* effectivePath = lpFileName;
    if (Kongor::TryRedirectUserdataDirW(
            lpFileName, dwDesiredAccess, userRedirected, sizeof(userRedirected) / sizeof(wchar_t))) {
        static std::atomic<uint32_t> s_userRedirectLogCount{0};
        if (s_userRedirectLogCount.fetch_add(1, std::memory_order_relaxed) < 8) {
            char msg[512];
            snprintf(msg, sizeof(msg),
                "[KONGOR USERDATA DIR] Redirected CreateFileW from '%ls' -> '%ls'",
                lpFileName, userRedirected);
            WriteSentinelLine(msg);
        }
        effectivePath = userRedirected;
    }

    wchar_t headlessRedirected[MAX_PATH * 2] = {0};
    if (Kongor::TryRedirectHeadlessStartupCfgWriteW(
            g_isHeadlessProcess, effectivePath, dwDesiredAccess, headlessRedirected, sizeof(headlessRedirected) / sizeof(wchar_t))) {
        static std::atomic<uint32_t> s_redirectLogCount{0};
        if (s_redirectLogCount.fetch_add(1, std::memory_order_relaxed) < 8) {
            char msg[512];
            snprintf(msg, sizeof(msg),
                "[KONGOR HEADLESS CFG] Redirected CreateFileW write from '%ls' -> '%ls'",
                effectivePath, headlessRedirected);
            WriteSentinelLine(msg);
        }
        effectivePath = headlessRedirected;
    }

    return g_pfnRealCreateFileW
        ? g_pfnRealCreateFileW(effectivePath, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile)
        : ::CreateFileW(effectivePath, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
}

static HANDLE WINAPI HookedCreateFileA(
    LPCSTR lpFileName,
    DWORD dwDesiredAccess,
    DWORD dwShareMode,
    LPSECURITY_ATTRIBUTES lpSecurityAttributes,
    DWORD dwCreationDisposition,
    DWORD dwFlagsAndAttributes,
    HANDLE hTemplateFile)
{
    char userRedirected[MAX_PATH * 2] = {0};
    const char* effectivePath = lpFileName;
    if (Kongor::TryRedirectUserdataDirA(
            lpFileName, dwDesiredAccess, userRedirected, sizeof(userRedirected))) {
        static std::atomic<uint32_t> s_userRedirectLogCountA{0};
        if (s_userRedirectLogCountA.fetch_add(1, std::memory_order_relaxed) < 8) {
            char msg[512];
            snprintf(msg, sizeof(msg),
                "[KONGOR USERDATA DIR] Redirected CreateFileA from '%s' -> '%s'",
                lpFileName, userRedirected);
            WriteSentinelLine(msg);
        }
        effectivePath = userRedirected;
    }

    char headlessRedirected[MAX_PATH * 2] = {0};
    if (Kongor::TryRedirectHeadlessStartupCfgWriteA(
            g_isHeadlessProcess, effectivePath, dwDesiredAccess, headlessRedirected, sizeof(headlessRedirected))) {
        static std::atomic<uint32_t> s_redirectLogCountA{0};
        if (s_redirectLogCountA.fetch_add(1, std::memory_order_relaxed) < 8) {
            char msg[512];
            snprintf(msg, sizeof(msg),
                "[KONGOR HEADLESS CFG] Redirected CreateFileA write from '%s' -> '%s'",
                effectivePath, headlessRedirected);
            WriteSentinelLine(msg);
        }
        effectivePath = headlessRedirected;
    }

    return g_pfnRealCreateFileA
        ? g_pfnRealCreateFileA(effectivePath, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile)
        : ::CreateFileA(effectivePath, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
}

static void NotifyConsoleConstrainCursorWrite(const wchar_t* wsz) {
    if (!wsz) return;
    const wchar_t* pSub = wcsstr(wsz, L"cg_constrainCursor");
    if (!pSub) return;
    pSub += 18;
    while (*pSub == L' ' || *pSub == L'\t' || *pSub == L'=' || *pSub == L'"' || *pSub == L'\'' || *pSub == L',') {
        ++pSub;
    }
    EnterCriticalSection(&g_cursorLock);
    bool curVal = g_cursorStateMachine.GetConstrainCvar();
    bool nextVal = Kongor::ParseConstrainCursorBoolW(pSub, curVal);
    g_cursorStateMachine.SetConstrainCvar(nextVal);
    LeaveCriticalSection(&g_cursorLock);
    g_constrainCvarDirty.store(true, std::memory_order_release);
}

static void PollConstrainCursorCvarIfNeeded() {
    uint64_t nowTick = GetTickCount64();
    uint64_t prevTick = g_lastConstrainCvarPollTick.load(std::memory_order_relaxed);
    if (!g_constrainCvarDirty.exchange(false, std::memory_order_acq_rel) &&
        (nowTick - prevTick < 500)) {
        return;
    }
    g_lastConstrainCvarPollTick.store(nowTick, std::memory_order_relaxed);

    if (IsDedicatedServerProcessCached()) return;
    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (!hK2) return;

    typedef K2WString* (__fastcall *FnLocalICvarGetString)(K2WString* retVal, const K2WString* name);
    typedef void (__fastcall *FnLocalDeallocate)(void* pMem);

    static FnLocalICvarGetString s_pfnGetString = nullptr;
    static FnLocalDeallocate s_pfnDealloc = nullptr;
    if (!s_pfnGetString) {
        void* pSym = ResolveExport(
            hK2, "?GetString@ICvar@@SA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@AEBV23@@Z", 0x85a8b0);
        s_pfnGetString = reinterpret_cast<FnLocalICvarGetString>(pSym);
    }
    if (!s_pfnDealloc) {
        void* pSym = ResolveExport(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z", 0x2a4ab0);
        s_pfnDealloc = reinterpret_cast<FnLocalDeallocate>(pSym);
    }
    if (!s_pfnGetString) return;

    Kongor_EnterProtectedSeh();
    __try {
        K2WString cvarName(L"cg_constrainCursor");
        K2WString outVal;
        s_pfnGetString(&outVal, &cvarName);
        if (outVal.size > 0 && outVal.c_str() && outVal.c_str()[0] != L'\0') {
            EnterCriticalSection(&g_cursorLock);
            bool parsed = Kongor::ParseConstrainCursorBoolW(outVal.c_str(), g_cursorStateMachine.GetConstrainCvar());
            g_cursorStateMachine.SetConstrainCvar(parsed);
            LeaveCriticalSection(&g_cursorLock);
        }
        if (outVal.res > 7 && outVal.ptr && s_pfnDealloc) {
            s_pfnDealloc(const_cast<wchar_t*>(outVal.ptr));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    Kongor_LeaveProtectedSeh();
}

static void SyncLiveClientGeometryToCursorState(HWND hWnd) {
    if (!hWnd || !IsWindow(hWnd)) return;
    RECT rcClient = {0};
    POINT ptOrigin = {0, 0};
    if (GetClientRect(hWnd, &rcClient) && ClientToScreen(hWnd, &ptOrigin)) {
        int w = rcClient.right - rcClient.left;
        int h = rcClient.bottom - rcClient.top;
        if (w > 0 && h > 0) {
            if (ptOrigin.x > -5000 && ptOrigin.y > -5000) {
                g_clientOriginX.store(ptOrigin.x, std::memory_order_release);
                g_clientOriginY.store(ptOrigin.y, std::memory_order_release);
            }
            g_clientWindowWidth.store(w, std::memory_order_release);
            g_clientWindowHeight.store(h, std::memory_order_release);
        }
        EnterCriticalSection(&g_cursorLock);
        g_cursorStateMachine.SetHeadless(g_isHeadlessProcess);
        g_cursorStateMachine.SetClientGeometry(ptOrigin.x, ptOrigin.y, w, h);
        LeaveCriticalSection(&g_cursorLock);
    }
}

static bool SafeQueryK2CvarStringW(const wchar_t* name, wchar_t* outBuf, size_t maxChars) {
    if (!name || !outBuf || maxChars == 0) return false;
    outBuf[0] = L'\0';
    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (!hK2) return false;

    typedef K2WString* (__fastcall *FnLocalICvarGetString)(K2WString* retVal, const K2WString* name);
    typedef void (__fastcall *FnLocalDeallocate)(void* pMem);

    static FnLocalICvarGetString s_pfnGetString = nullptr;
    static FnLocalDeallocate s_pfnDealloc = nullptr;
    if (!s_pfnGetString) {
        void* pSym = ResolveExport(
            hK2, "?GetString@ICvar@@SA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@AEBV23@@Z", 0x85a8b0);
        s_pfnGetString = reinterpret_cast<FnLocalICvarGetString>(pSym);
    }
    if (!s_pfnDealloc) {
        void* pSym = ResolveExport(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z", 0x2a4ab0);
        s_pfnDealloc = reinterpret_cast<FnLocalDeallocate>(pSym);
    }
    if (!s_pfnGetString) return false;

    bool success = false;
    Kongor_EnterProtectedSeh();
    __try {
        K2WString cvarName(name);
        K2WString outVal;
        s_pfnGetString(&outVal, &cvarName);
        if (outVal.size > 0 && outVal.c_str()) {
            wcsncpy_s(outBuf, maxChars, outVal.c_str(), _TRUNCATE);
            success = true;
        }
        if (outVal.res > 7 && outVal.ptr && s_pfnDealloc) {
            s_pfnDealloc(const_cast<wchar_t*>(outVal.ptr));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        success = false;
    }
    Kongor_LeaveProtectedSeh();
    return success;
}

struct KongorResolutionModeEntry {
    int width = 0;
    int height = 0;
    bool isNative = false;
    int maxHz = 60;
    std::vector<int> ratesHz;
};

struct KongorDisplayEntry {
    HMONITOR hMon = nullptr;
    int index = 1;
    std::string rawDevice;
    std::string normToken;
    std::string friendlyName;
    bool isPrimary = false;
    RECT rcMonitor{};
    RECT rcWork{};
    int nativeW = 1920;
    int nativeH = 1080;
    int currentHz = 60;
    int maxHz = 60;
    std::vector<KongorResolutionModeEntry> modes;
};

static std::string NormalizeMonitorTokenA(const char* raw) {
    if (!raw || !*raw) return "";
    std::string s(raw);
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '"' || s.front() == '\'' ||
                          s.front() == '\\' || s.front() == '/' || s.front() == '.')) {
        s.erase(s.begin());
    }
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '"' || s.back() == '\'' ||
                          s.back() == '\r' || s.back() == '\n')) {
        s.pop_back();
    }
    for (char& ch : s) {
        if (ch >= 'a' && ch <= 'z') ch = static_cast<char>(ch - 'a' + 'A');
    }
    return s;
}

static int ExtractMonitorNumA(const std::string& normToken) {
    for (size_t i = 0; i < normToken.size(); ++i) {
        if (normToken[i] >= '0' && normToken[i] <= '9') {
            int val = atoi(normToken.c_str() + i);
            if (val > 0) return val;
            break;
        }
    }
    return 999999;
}

static std::string SanitizeMonitorFriendlyNameA(const std::string& rawName) {
    std::string out;
    out.reserve(rawName.size());
    for (unsigned char ch : rawName) {
        if (ch < 32 || ch == '/' || ch == '|' || ch == '@' || ch == '+' ||
            ch == ';' || ch == '\\' || ch == '%' || ch == '$' || ch == '^' || ch == '"' || ch == '\'') {
            if (!out.empty() && out.back() != ' ') out.push_back(' ');
        } else {
            out.push_back(static_cast<char>(ch));
        }
    }
    while (!out.empty() && out.front() == ' ') out.erase(out.begin());
    while (!out.empty() && out.back() == ' ') out.pop_back();
    return out;
}

static std::vector<std::pair<std::string, std::string>> QueryCcdMonitorFriendlyNames() {
    std::vector<std::pair<std::string, std::string>> result;
    UINT32 pathCount = 0;
    UINT32 modeCount = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS || pathCount == 0) {
        return result;
    }

    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(), nullptr) != ERROR_SUCCESS) {
        return result;
    }

    for (UINT32 i = 0; i < pathCount; ++i) {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME srcName{};
        srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        srcName.header.size = sizeof(srcName);
        srcName.header.adapterId = paths[i].sourceInfo.adapterId;
        srcName.header.id = paths[i].sourceInfo.id;

        DISPLAYCONFIG_TARGET_DEVICE_NAME dstName{};
        dstName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        dstName.header.size = sizeof(dstName);
        dstName.header.adapterId = paths[i].targetInfo.adapterId;
        dstName.header.id = paths[i].targetInfo.id;

        if (DisplayConfigGetDeviceInfo(&srcName.header) == ERROR_SUCCESS &&
            DisplayConfigGetDeviceInfo(&dstName.header) == ERROR_SUCCESS &&
            srcName.viewGdiDeviceName[0] != L'\0' &&
            dstName.monitorFriendlyDeviceName[0] != L'\0') {
            char gdiA[64] = {0};
            char friendlyA[128] = {0};
            WideCharToMultiByte(CP_UTF8, 0, srcName.viewGdiDeviceName, -1, gdiA, sizeof(gdiA) - 1, nullptr, nullptr);
            WideCharToMultiByte(CP_UTF8, 0, dstName.monitorFriendlyDeviceName, -1, friendlyA, sizeof(friendlyA) - 1, nullptr, nullptr);
            std::string norm = NormalizeMonitorTokenA(gdiA);
            std::string cleanFriendly = SanitizeMonitorFriendlyNameA(friendlyA);
            if (!norm.empty() && !cleanFriendly.empty()) {
                result.emplace_back(norm, cleanFriendly);
            }
        }
    }
    return result;
}

static const std::vector<KongorDisplayEntry>& EnumerateKongorDisplays(bool forceRefresh) {
    static std::mutex s_monMutex;
    static std::vector<KongorDisplayEntry> s_cached;
    static uint64_t s_lastEnumTick = 0;

    std::lock_guard<std::mutex> lock(s_monMutex);
    uint64_t now = GetTickCount64();
    if (!forceRefresh && !s_cached.empty() && (now - s_lastEnumTick) < 2000) {
        return s_cached;
    }

    auto ccdNames = QueryCcdMonitorFriendlyNames();
    std::vector<KongorDisplayEntry> rawList;

    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR hMon, HDC, LPRECT, LPARAM lParam) -> BOOL {
        auto* pList = reinterpret_cast<std::vector<KongorDisplayEntry>*>(lParam);
        MONITORINFOEXA mi{};
        mi.cbSize = sizeof(mi);
        if (!GetMonitorInfoA(hMon, &mi)) return TRUE;

        KongorDisplayEntry entry{};
        entry.hMon = hMon;
        entry.rawDevice = mi.szDevice;
        entry.normToken = NormalizeMonitorTokenA(mi.szDevice);
        entry.isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
        entry.rcMonitor = mi.rcMonitor;
        entry.rcWork = mi.rcWork;
        entry.nativeW = mi.rcMonitor.right - mi.rcMonitor.left;
        entry.nativeH = mi.rcMonitor.bottom - mi.rcMonitor.top;
        entry.currentHz = 60;

        DEVMODEA dmCur{};
        dmCur.dmSize = sizeof(dmCur);
        if (EnumDisplaySettingsA(mi.szDevice, ENUM_CURRENT_SETTINGS, &dmCur)) {
            if (dmCur.dmDisplayFrequency > 1) entry.currentHz = static_cast<int>(dmCur.dmDisplayFrequency);
            if (dmCur.dmPelsWidth > 0 && dmCur.dmPelsHeight > 0) {
                entry.nativeW = static_cast<int>(dmCur.dmPelsWidth);
                entry.nativeH = static_cast<int>(dmCur.dmPelsHeight);
            }
        }

        DEVMODEA dmMode{};
        dmMode.dmSize = sizeof(dmMode);
        for (DWORD iMode = 0; EnumDisplaySettingsA(mi.szDevice, iMode, &dmMode); ++iMode) {
            if (dmMode.dmBitsPerPel == 32 && dmMode.dmPelsWidth >= 640 && dmMode.dmPelsHeight >= 480) {
                int mw = static_cast<int>(dmMode.dmPelsWidth);
                int mh = static_cast<int>(dmMode.dmPelsHeight);
                int mhz = (dmMode.dmDisplayFrequency > 1) ? static_cast<int>(dmMode.dmDisplayFrequency) : 60;

                KongorResolutionModeEntry* pRes = nullptr;
                for (auto& existing : entry.modes) {
                    if (existing.width == mw && existing.height == mh) {
                        pRes = &existing;
                        break;
                    }
                }
                if (!pRes) {
                    KongorResolutionModeEntry newRes{};
                    newRes.width = mw;
                    newRes.height = mh;
                    newRes.isNative = (mw == entry.nativeW && mh == entry.nativeH);
                    newRes.maxHz = mhz;
                    entry.modes.push_back(std::move(newRes));
                    pRes = &entry.modes.back();
                }
                if (std::find(pRes->ratesHz.begin(), pRes->ratesHz.end(), mhz) == pRes->ratesHz.end()) {
                    pRes->ratesHz.push_back(mhz);
                }
                if (mhz > pRes->maxHz) pRes->maxHz = mhz;
            }
        }

        KongorResolutionModeEntry* pNative = nullptr;
        for (auto& m : entry.modes) {
            if (m.width == entry.nativeW && m.height == entry.nativeH) {
                pNative = &m;
                break;
            }
        }
        if (!pNative) {
            KongorResolutionModeEntry nat{};
            nat.width = entry.nativeW;
            nat.height = entry.nativeH;
            nat.isNative = true;
            nat.maxHz = entry.currentHz;
            nat.ratesHz.push_back(entry.currentHz);
            entry.modes.push_back(std::move(nat));
            pNative = &entry.modes.back();
        } else {
            pNative->isNative = true;
            if (std::find(pNative->ratesHz.begin(), pNative->ratesHz.end(), entry.currentHz) == pNative->ratesHz.end()) {
                pNative->ratesHz.push_back(entry.currentHz);
            }
            if (entry.currentHz > pNative->maxHz) pNative->maxHz = entry.currentHz;
        }

        entry.maxHz = pNative->maxHz;

        for (auto& m : entry.modes) {
            std::sort(m.ratesHz.begin(), m.ratesHz.end(), std::greater<int>());
            if (!m.ratesHz.empty()) m.maxHz = m.ratesHz.front();
        }

        std::sort(entry.modes.begin(), entry.modes.end(), [](const KongorResolutionModeEntry& a, const KongorResolutionModeEntry& b) {
            if (a.isNative != b.isNative) return a.isNative > b.isNative;
            int64_t areaA = static_cast<int64_t>(a.width) * a.height;
            int64_t areaB = static_cast<int64_t>(b.width) * b.height;
            if (areaA != areaB) return areaA > areaB;
            return a.width > b.width;
        });

        pList->push_back(std::move(entry));
        return TRUE;
    }, reinterpret_cast<LPARAM>(&rawList));

    for (auto& e : rawList) {
        for (const auto& kv : ccdNames) {
            if (kv.first == e.normToken) {
                e.friendlyName = kv.second;
                break;
            }
        }
        if (e.friendlyName.empty()) {
            DISPLAY_DEVICEA monDev{};
            monDev.cb = sizeof(monDev);
            if (EnumDisplayDevicesA(e.rawDevice.c_str(), 0, &monDev, 0) &&
                monDev.DeviceString[0] != '\0' &&
                _stricmp(monDev.DeviceString, "Generic PnP Monitor") != 0) {
                e.friendlyName = SanitizeMonitorFriendlyNameA(monDev.DeviceString);
            }
        }
        if (e.friendlyName.empty()) {
            e.friendlyName = e.normToken.empty() ? "Display" : e.normToken;
        }
    }

    std::sort(rawList.begin(), rawList.end(), [](const KongorDisplayEntry& a, const KongorDisplayEntry& b) {
        if (a.isPrimary != b.isPrimary) return a.isPrimary > b.isPrimary;
        int numA = ExtractMonitorNumA(a.normToken);
        int numB = ExtractMonitorNumA(b.normToken);
        if (numA != numB) return numA < numB;
        return a.normToken < b.normToken;
    });

    for (size_t i = 0; i < rawList.size(); ++i) {
        rawList[i].index = static_cast<int>(i + 1);
    }

    s_cached = std::move(rawList);
    s_lastEnumTick = now;
    return s_cached;
}

static std::string BuildDetectedMonitorsCvarPayload(bool forceRefresh) {
    const auto& displays = EnumerateKongorDisplays(forceRefresh);
    std::string payload;
    payload.reserve(768);

    for (size_t i = 0; i < displays.size(); ++i) {
        const auto& d = displays[i];
        if (i > 0) payload.push_back('/');

        std::string modesPart;
        size_t maxModes = std::min<size_t>(d.modes.size(), 8);
        for (size_t mIdx = 0; mIdx < maxModes; ++mIdx) {
            const auto& m = d.modes[mIdx];
            if (mIdx > 0) modesPart.push_back('+');
            char resHdr[32];
            snprintf(resHdr, sizeof(resHdr), "%dx%d@", m.width, m.height);
            modesPart += resHdr;
            size_t maxRates = std::min<size_t>(m.ratesHz.size(), 6);
            for (size_t rIdx = 0; rIdx < maxRates; ++rIdx) {
                if (rIdx > 0) modesPart.push_back(',');
                modesPart += std::to_string(m.ratesHz[rIdx]);
            }
        }

        char monHdr[256];
        snprintf(monHdr, sizeof(monHdr), "%s|%d|%s|%d|%d,%d|%d|",
            d.normToken.c_str(),
            d.index,
            d.friendlyName.c_str(),
            d.isPrimary ? 1 : 0,
            d.nativeW,
            d.nativeH,
            d.maxHz);
        payload += monHdr;
        payload += modesPart;
    }

    if (payload.size() > 940) {
        payload.resize(940);
    }
    return payload;
}

static void ExecuteMonitorConsoleCmdImmediateOrQueued(void* pConsoleExplicit, const std::wstring& wCmd) {
    if (wCmd.empty()) return;
    struct ScopedMonitorCmdGuard {
        bool prev;
        ScopedMonitorCmdGuard() noexcept : prev(t_inMonitorConsoleDispatch) { t_inMonitorConsoleDispatch = true; }
        ~ScopedMonitorCmdGuard() noexcept { t_inMonitorConsoleDispatch = prev; }
    } guard;

    K2StringW k2Cmd(wCmd.c_str());
    if (pConsoleExplicit && g_origConsoleExecute) {
        g_origConsoleExecute(pConsoleExplicit, &k2Cmd, 0);
    } else if (ResolveK2ConsoleSymbols() && *g_ppConsole) {
        if (g_pfnConsoleAddCmdBuffer) {
            g_pfnConsoleAddCmdBuffer(*g_ppConsole, &k2Cmd);
        } else if (g_pfnConsoleExecute) {
            g_pfnConsoleExecute(*g_ppConsole, &k2Cmd, 0);
        }
    }
}

static std::atomic<bool> g_monitorsCvarPopulated{false};

static void EnsureDetectedMonitorsCvarPopulated(void* pConsoleExplicit, bool forceRefresh) {
    if (g_isHeadlessProcess || IsDedicatedServerProcessCached()) return;
    if (!forceRefresh && g_monitorsCvarPopulated.load(std::memory_order_acquire)) return;

    void* pTargetConsole = pConsoleExplicit;
    if (!pTargetConsole) {
        if (ResolveK2ConsoleSymbols() && g_ppConsole) {
            pTargetConsole = *g_ppConsole;
        }
    }
    if (!pTargetConsole) return;

    std::string payload = BuildDetectedMonitorsCvarPayload(forceRefresh);
    if (payload.empty()) return;

    char envManual[32] = {0};
    int envManualVal = 0;
    if (GetEnvironmentVariableA("KONGOR_MANUAL_FPS_OVERRIDE", envManual, sizeof(envManual)) > 0 && envManual[0]) {
        envManualVal = atoi(envManual);
    }

    std::wstring wPayload(payload.begin(), payload.end());
    wchar_t cvarProbe[64] = {0};
    bool hasManualCvar = SafeQueryK2CvarStringW(L"cg_manualMaxFpsOverride", cvarProbe, _countof(cvarProbe));

    // SetSave works directly in CConsole (CreateString/CreateInt are UI-script-only and emit Unknown identifier in CConsole).
    std::wstring cmd = L"SetSave kongor_detected_monitors \"" + wPayload + L"\";";
    if (!hasManualCvar) {
        cmd += L" SetSave cg_manualMaxFpsOverride " + std::to_wstring(envManualVal) + L";";
    }
    ExecuteMonitorConsoleCmdImmediateOrQueued(pTargetConsole, cmd);

    wchar_t verifyBuf[128] = {0};
    if (SafeQueryK2CvarStringW(L"kongor_detected_monitors", verifyBuf, _countof(verifyBuf)) && verifyBuf[0] != L'\0') {
        g_monitorsCvarPopulated.store(true, std::memory_order_release);
        char dbg[512];
        snprintf(dbg, sizeof(dbg), "[KONGOR-MONITOR-DETECT] Populated kongor_detected_monitors (%zu chars): %.380s\n",
            payload.size(), payload.c_str());
        OutputDebugStringA(dbg);
    }
}

static int QueryManualFpsOverrideValue() {
    wchar_t cvarBuf[64] = {0};
    if (SafeQueryK2CvarStringW(L"cg_manualMaxFpsOverride", cvarBuf, _countof(cvarBuf)) && cvarBuf[0] != L'\0') {
        int v = _wtoi(cvarBuf);
        if (v > 0) return v;
    }
    char envManual[32] = {0};
    if (GetEnvironmentVariableA("KONGOR_MANUAL_FPS_OVERRIDE", envManual, sizeof(envManual)) > 0 && envManual[0]) {
        int v = atoi(envManual);
        if (v > 0) return v;
    }
    return 0;
}

static size_t FindJsonKeyCaseInsensitive(const std::string& json, const char* propName) {
    if (!propName || !*propName) return std::string::npos;
    std::string needle = std::string("\"") + propName + "\"";
    auto it = std::search(
        json.begin(), json.end(),
        needle.begin(), needle.end(),
        [](char a, char b) {
            return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b));
        });
    return (it == json.end()) ? std::string::npos : static_cast<size_t>(std::distance(json.begin(), it));
}

static void ReplaceJsonStringPropInPlace(std::string& json, const char* propName, const std::string& newVal) {
    size_t keyPos = FindJsonKeyCaseInsensitive(json, propName);
    if (keyPos == std::string::npos) return;
    size_t keyEnd = keyPos + strlen(propName) + 2;
    size_t colonPos = json.find(':', keyEnd);
    if (colonPos == std::string::npos) return;
    size_t valStart = colonPos + 1;
    while (valStart < json.size() && (json[valStart] == ' ' || json[valStart] == '\t' || json[valStart] == '\r' || json[valStart] == '\n')) {
        ++valStart;
    }
    if (valStart >= json.size()) return;
    if (json.compare(valStart, 4, "null") == 0) {
        json.replace(valStart, 4, std::string("\"") + newVal + "\"");
        return;
    }
    if (json[valStart] != '"') return;
    size_t q2 = valStart + 1;
    while (q2 < json.size()) {
        if (json[q2] == '"' && json[q2 - 1] != '\\') break;
        ++q2;
    }
    if (q2 >= json.size()) return;
    json.replace(valStart + 1, q2 - valStart - 1, newVal);
}

static void ReplaceJsonIntPropInPlace(std::string& json, const char* propName, int newVal) {
    size_t keyPos = FindJsonKeyCaseInsensitive(json, propName);
    if (keyPos == std::string::npos) return;
    size_t keyEnd = keyPos + strlen(propName) + 2;
    size_t colonPos = json.find(':', keyEnd);
    if (colonPos == std::string::npos) return;
    size_t start = colonPos + 1;
    while (start < json.size() && (json[start] == ' ' || json[start] == '\t')) ++start;
    if (start < json.size() && json.compare(start, 4, "null") == 0) {
        json.replace(start, 4, std::to_string(newVal));
        return;
    }
    size_t end = start;
    while (end < json.size() && ((json[end] >= '0' && json[end] <= '9') || json[end] == '-')) ++end;
    if (end > start) {
        json.replace(start, end - start, std::to_string(newVal));
    }
}

static void SyncLauncherSettingsJsonOnMonitorSwitch(const std::string& normToken, int resW, int resH, int targetHz, bool updateFpsCap) {
    try {
        wchar_t exePath[MAX_PATH] = {0};
        if (GetModuleFileNameW(nullptr, exePath, MAX_PATH) == 0) return;
        std::filesystem::path exeDir = std::filesystem::path(exePath).parent_path();
        std::filesystem::path candidate1 = exeDir / L"userdataKONGOR" / L"launcher_settings.json";
        std::filesystem::path candidate2 = exeDir.parent_path() / L"userdataKONGOR" / L"launcher_settings.json";
        std::filesystem::path settingsPath;
        if (std::filesystem::exists(candidate1)) {
            settingsPath = candidate1;
        } else if (std::filesystem::exists(candidate2)) {
            settingsPath = candidate2;
        } else {
            return;
        }

        std::ifstream ifs(settingsPath, std::ios::binary);
        if (!ifs) return;
        std::string content((std::istreambuf_iterator<char>(ifs)), std::istreambuf_iterator<char>());
        ifs.close();
        if (content.empty()) return;

        ReplaceJsonStringPropInPlace(content, "SelectedMonitorDeviceName", normToken);
        if (resW > 0 && resH > 0) {
            ReplaceJsonStringPropInPlace(content, "Resolution", std::to_string(resW) + "x" + std::to_string(resH));
        }
        if (updateFpsCap && targetHz > 0) {
            ReplaceJsonIntPropInPlace(content, "FpsCap", targetHz);
            ReplaceJsonStringPropInPlace(content, "RefreshRate", std::to_string(targetHz) + " Hz");
        }

        std::filesystem::path tmpPath = settingsPath;
        tmpPath += L".tmp";
        {
            std::ofstream ofs(tmpPath, std::ios::binary | std::ios::trunc);
            if (!ofs) return;
            ofs.write(content.data(), static_cast<std::streamsize>(content.size()));
        }
        MoveFileExW(tmpPath.c_str(), settingsPath.c_str(), MOVEFILE_REPLACE_EXISTING);
    } catch (...) {
        OutputDebugStringA("[KONGOR-MONITOR-SWITCH] Non-fatal warning syncing launcher_settings.json.\n");
    }
}

static HMONITOR g_lastSyncedMonitorHandle = nullptr;
static std::string g_lastSyncedMonitorToken;
static ULONGLONG g_explicitMonitorSwitchSuppressUntilTick = 0;
static std::string g_explicitTargetMonitorToken;

static void CheckAndHandleMonitorSwitchCore(HWND hWnd, const char* triggerSource, void* pConsoleExplicit, bool forceSync, int explicitHzOverride) {
    if (!hWnd || !IsWindow(hWnd) || IsIconic(hWnd) || g_isHeadlessProcess || IsDedicatedServerProcessCached()) return;

    bool isDisplayChange = (strcmp(triggerSource, "WM_DISPLAYCHANGE") == 0);
    EnsureDetectedMonitorsCvarPopulated(pConsoleExplicit, isDisplayChange);

    HMONITOR hCurMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
    if (!hCurMon) return;

    MONITORINFOEXA mi{};
    mi.cbSize = sizeof(mi);
    if (!GetMonitorInfoA(hCurMon, &mi)) return;

    std::string curToken = NormalizeMonitorTokenA(mi.szDevice);
    if (curToken.empty()) curToken = mi.szDevice;

    if (!forceSync && !isDisplayChange) {
        if (g_lastSyncedMonitorHandle == nullptr) {
            // Latch initial monitor once console is available so boot sync executes once cleanly
            void* pCon = pConsoleExplicit ? pConsoleExplicit : ((ResolveK2ConsoleSymbols() && g_ppConsole) ? *g_ppConsole : nullptr);
            if (!pCon) {
                return;
            }
        } else if (hCurMon == g_lastSyncedMonitorHandle && curToken == g_lastSyncedMonitorToken) {
            return;
        }
    }

    g_lastSyncedMonitorHandle = hCurMon;
    g_lastSyncedMonitorToken = curToken;

    const auto& displays = EnumerateKongorDisplays(isDisplayChange);
    const KongorDisplayEntry* pMatchedEntry = nullptr;
    for (const auto& d : displays) {
        if (d.hMon == hCurMon || d.normToken == curToken) {
            pMatchedEntry = &d;
            break;
        }
    }

    int monW = mi.rcMonitor.right - mi.rcMonitor.left;
    int monH = mi.rcMonitor.bottom - mi.rcMonitor.top;
    int nativeW = pMatchedEntry ? pMatchedEntry->nativeW : monW;
    int nativeH = pMatchedEntry ? pMatchedEntry->nativeH : monH;
    int maxHz = pMatchedEntry ? pMatchedEntry->maxHz : 60;
    if (!pMatchedEntry) {
        DEVMODEA dm{};
        dm.dmSize = sizeof(dm);
        if (EnumDisplaySettingsA(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm)) {
            if (dm.dmDisplayFrequency > 1) maxHz = static_cast<int>(dm.dmDisplayFrequency);
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) {
                nativeW = static_cast<int>(dm.dmPelsWidth);
                nativeH = static_cast<int>(dm.dmPelsHeight);
            }
        }
    }

    int effectiveHz = (explicitHzOverride > 0) ? explicitHzOverride : maxHz;
    int manualOverrideFps = QueryManualFpsOverrideValue();
    int targetFps = (manualOverrideFps > 0) ? manualOverrideFps : effectiveHz;

    bool isFullscreen = false;
    bool isBorderless = false;
    wchar_t cvarBuf[128] = {0};
    if (SafeQueryK2CvarStringW(L"vid_fullscreen", cvarBuf, _countof(cvarBuf))) {
        if (_wcsicmp(cvarBuf, L"true") == 0 || _wcsicmp(cvarBuf, L"1") == 0) isFullscreen = true;
    }
    if (SafeQueryK2CvarStringW(L"vid_borderless", cvarBuf, _countof(cvarBuf))) {
        if (_wcsicmp(cvarBuf, L"true") == 0 || _wcsicmp(cvarBuf, L"1") == 0) isBorderless = true;
    }

    char logBuf[512];
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR-MONITOR-SWITCH] Trigger=%s -> Active Monitor #%d '%s' (%s) Bounds=[%ld,%ld %dx%d] Native=%dx%d MaxHz=%d EffectiveHz=%d TargetFps=%d (ManualOverride=%d)\n",
        triggerSource,
        pMatchedEntry ? pMatchedEntry->index : 0,
        curToken.c_str(),
        pMatchedEntry ? pMatchedEntry->friendlyName.c_str() : "Display",
        mi.rcMonitor.left, mi.rcMonitor.top, monW, monH,
        nativeW, nativeH, maxHz, effectiveHz, targetFps, manualOverrideFps);
    OutputDebugStringA(logBuf);

    bool needsResolutionAdapt = false;
    wchar_t curResBuf[64] = {0};
    wchar_t desiredResW[64] = {0};
    _snwprintf(desiredResW, _countof(desiredResW), L"%d,%d", nativeW, nativeH);
    if ((isBorderless || isFullscreen) && nativeW > 0 && nativeH > 0) {
        if (!SafeQueryK2CvarStringW(L"vid_resolution", curResBuf, _countof(curResBuf)) ||
            _wcsicmp(curResBuf, desiredResW) != 0) {
            needsResolutionAdapt = true;
        }
    }

    wchar_t cmdBuf[1024] = {0};
    if (manualOverrideFps <= 0) {
        if (needsResolutionAdapt) {
            _snwprintf(cmdBuf, _countof(cmdBuf),
                L"SetSave host_maxFPS %d; SetSave host_maximumFPS %d; SetSave vid_maxFPS %d; SetSave cl_maxFPS %d; SetSave vid_refreshRate %d; SetSave options_refreshrate %d; SetSave vid_display \"%hs\"; SetSave options_display \"%hs\"; SetSave vid_resolution \"%d,%d\"; SetSave options_resolution \"%d,%d\";",
                targetFps, targetFps, targetFps, targetFps, effectiveHz, effectiveHz, curToken.c_str(), curToken.c_str(), nativeW, nativeH, nativeW, nativeH);
        } else {
            _snwprintf(cmdBuf, _countof(cmdBuf),
                L"SetSave host_maxFPS %d; SetSave host_maximumFPS %d; SetSave vid_maxFPS %d; SetSave cl_maxFPS %d; SetSave vid_refreshRate %d; SetSave options_refreshrate %d; SetSave vid_display \"%hs\"; SetSave options_display \"%hs\";",
                targetFps, targetFps, targetFps, targetFps, effectiveHz, effectiveHz, curToken.c_str(), curToken.c_str());
        }
    } else {
        if (needsResolutionAdapt) {
            _snwprintf(cmdBuf, _countof(cmdBuf),
                L"SetSave vid_refreshRate %d; SetSave options_refreshrate %d; SetSave vid_display \"%hs\"; SetSave options_display \"%hs\"; SetSave vid_resolution \"%d,%d\"; SetSave options_resolution \"%d,%d\";",
                effectiveHz, effectiveHz, curToken.c_str(), curToken.c_str(), nativeW, nativeH, nativeW, nativeH);
        } else {
            _snwprintf(cmdBuf, _countof(cmdBuf),
                L"SetSave vid_refreshRate %d; SetSave options_refreshrate %d; SetSave vid_display \"%hs\"; SetSave options_display \"%hs\";",
                effectiveHz, effectiveHz, curToken.c_str(), curToken.c_str());
        }
    }
    ExecuteMonitorConsoleCmdImmediateOrQueued(pConsoleExplicit, cmdBuf);

    if (isBorderless || isFullscreen) {
        LONG_PTR style = GetWindowLongPtrA(hWnd, GWL_STYLE);
        if ((style & WS_POPUP) == 0 || (style & WS_CAPTION) != 0) {
            LONG_PTR popupStyle = (style & ~WS_CAPTION) | WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
            SetWindowLongPtrA(hWnd, GWL_STYLE, popupStyle);
        }

        SetWindowPos(hWnd, nullptr,
            mi.rcMonitor.left, mi.rcMonitor.top, monW, monH,
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        PostMessageA(hWnd, WM_SIZE, SIZE_RESTORED, MAKELPARAM(monW, monH));

        if (needsResolutionAdapt && strcmp(triggerSource, "BOOT_SYNC") != 0) {
            g_explicitTargetMonitorToken = curToken;
            g_explicitMonitorSwitchSuppressUntilTick = GetTickCount64() + 1500;
            ExecuteMonitorConsoleCmdImmediateOrQueued(nullptr, L"SetVideoMode;");
        }
    } else {
        RECT rcWnd{};
        if (GetWindowRect(hWnd, &rcWnd)) {
            int winW = rcWnd.right - rcWnd.left;
            int winH = rcWnd.bottom - rcWnd.top;
            int workW = mi.rcWork.right - mi.rcWork.left;
            int workH = mi.rcWork.bottom - mi.rcWork.top;
            if (workW > 0 && workH > 0 && (winW > workW || winH > workH)) {
                int clampedW = (std::min)(winW, workW);
                int clampedH = (std::min)(winH, workH);
                int newX = mi.rcWork.left + (workW - clampedW) / 2;
                int newY = mi.rcWork.top + (workH - clampedH) / 2;
                SetWindowPos(hWnd, nullptr, newX, newY, clampedW, clampedH, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }

    ExecuteMonitorConsoleCmdImmediateOrQueued(
        nullptr,
        L"Script \"if _G.SysBar_UpdateQuickSettingsUI then pcall(_G.SysBar_UpdateQuickSettingsUI) end if _G.Kongor_PopulateDisplayOptions then pcall(_G.Kongor_PopulateDisplayOptions,'sync') end\"");

    SyncLauncherSettingsJsonOnMonitorSwitch(curToken, nativeW, nativeH, effectiveHz, manualOverrideFps <= 0);
}

static void CheckAndHandleMonitorSwitch(HWND hWnd, const char* triggerSource) {
    char inSetModeBuf[8] = {0};
    if (GetEnvironmentVariableA("KONGOR_IN_SETMODE", inSetModeBuf, sizeof(inSetModeBuf)) > 0 && inSetModeBuf[0] == '1') {
        return;
    }
    if (strcmp(triggerSource, "WM_DISPLAYCHANGE") != 0 && GetTickCount64() < g_explicitMonitorSwitchSuppressUntilTick) {
        HMONITOR hCurMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        MONITORINFOEXA mi{};
        mi.cbSize = sizeof(mi);
        if (hCurMon && GetMonitorInfoA(hCurMon, &mi)) {
            std::string curToken = NormalizeMonitorTokenA(mi.szDevice);
            if (!g_explicitTargetMonitorToken.empty() && curToken != g_explicitTargetMonitorToken) {
                return;
            }
        }
    }
    CheckAndHandleMonitorSwitchCore(hWnd, triggerSource, nullptr, false, 0);
}

static void HandleConsoleSyncMonitorsCommand(void* pConsole) {
    EnsureDetectedMonitorsCvarPopulated(pConsole, true);
    if (g_hGameWnd && IsWindow(g_hGameWnd) && !IsIconic(g_hGameWnd)) {
        CheckAndHandleMonitorSwitchCore(g_hGameWnd, "BOOT_SYNC", pConsole, true, 0);
    }
}

static void HandleConsoleApplyMonitorCommand(void* pConsole, const wchar_t* wszArgs) {
    EnsureDetectedMonitorsCvarPopulated(pConsole, true);
    if (!g_hGameWnd || !IsWindow(g_hGameWnd) || g_isHeadlessProcess || IsDedicatedServerProcessCached()) return;

    char argsA[128] = {0};
    if (wszArgs && *wszArgs) {
        WideCharToMultiByte(CP_UTF8, 0, wszArgs, -1, argsA, sizeof(argsA) - 1, nullptr, nullptr);
    }

    std::string targetArg;
    int explicitHz = 0;
    {
        std::istringstream iss(argsA);
        iss >> targetArg;
        int parsedHz = 0;
        if (iss >> parsedHz && parsedHz > 0) {
            explicitHz = parsedHz;
        }
    }

    std::string normTarget = NormalizeMonitorTokenA(targetArg.c_str());
    int parsedIndex = 0;
    if (!normTarget.empty() && normTarget[0] >= '0' && normTarget[0] <= '9') {
        parsedIndex = atoi(normTarget.c_str());
    }

    const auto& displays = EnumerateKongorDisplays(false);
    const KongorDisplayEntry* pTarget = nullptr;
    for (const auto& d : displays) {
        if ((!normTarget.empty() && d.normToken == normTarget) ||
            (parsedIndex > 0 && d.index == parsedIndex)) {
            pTarget = &d;
            break;
        }
    }

    if (!pTarget && !displays.empty()) {
        pTarget = &displays.front();
    }
    if (!pTarget || !pTarget->hMon) return;

    g_explicitTargetMonitorToken = pTarget->normToken;
    g_explicitMonitorSwitchSuppressUntilTick = GetTickCount64() + 1500;

    bool isFullscreen = false;
    bool isBorderless = false;
    wchar_t cvarBuf[64] = {0};
    if (SafeQueryK2CvarStringW(L"vid_fullscreen", cvarBuf, _countof(cvarBuf))) {
        if (_wcsicmp(cvarBuf, L"true") == 0 || _wcsicmp(cvarBuf, L"1") == 0) isFullscreen = true;
    }
    if (SafeQueryK2CvarStringW(L"vid_borderless", cvarBuf, _countof(cvarBuf))) {
        if (_wcsicmp(cvarBuf, L"true") == 0 || _wcsicmp(cvarBuf, L"1") == 0) isBorderless = true;
    }

    int monW = pTarget->rcMonitor.right - pTarget->rcMonitor.left;
    int monH = pTarget->rcMonitor.bottom - pTarget->rcMonitor.top;
    if (isBorderless || isFullscreen) {
        LONG_PTR style = GetWindowLongPtrA(g_hGameWnd, GWL_STYLE);
        if ((style & WS_POPUP) == 0 || (style & WS_CAPTION) != 0) {
            LONG_PTR popupStyle = (style & ~WS_CAPTION) | WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
            SetWindowLongPtrA(g_hGameWnd, GWL_STYLE, popupStyle);
        }
        SetWindowPos(g_hGameWnd, nullptr,
            pTarget->rcMonitor.left, pTarget->rcMonitor.top, monW, monH,
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    } else {
        RECT rcWnd{};
        GetWindowRect(g_hGameWnd, &rcWnd);
        int winW = rcWnd.right - rcWnd.left;
        int winH = rcWnd.bottom - rcWnd.top;
        int workW = pTarget->rcWork.right - pTarget->rcWork.left;
        int workH = pTarget->rcWork.bottom - pTarget->rcWork.top;
        int clampedW = (workW > 0) ? (std::min)(winW, workW) : winW;
        int clampedH = (workH > 0) ? (std::min)(winH, workH) : winH;
        int newX = pTarget->rcWork.left + ((workW > clampedW) ? ((workW - clampedW) / 2) : 0);
        int newY = pTarget->rcWork.top + ((workH > clampedH) ? ((workH - clampedH) / 2) : 0);
        SetWindowPos(g_hGameWnd, nullptr, newX, newY, clampedW, clampedH,
            SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    CheckAndHandleMonitorSwitchCore(g_hGameWnd, "CMD_APPLY_MONITOR", pConsole, true, explicitHz);
}

static void ApplyCursorConfinementPolicy(const char* triggerTag) {
    HWND hWnd = g_hGameWnd;
    if (hWnd && IsWindow(hWnd) && !g_isHeadlessProcess) {
        // Verify OS foreground & iconic state dynamically so stolen focus or minimized windows never stay clipped
        HWND hFg = GetForegroundWindow();
        bool osForeground = (hFg == hWnd) && !IsIconic(hWnd);
        if (!osForeground) {
            g_gameWndFocused.store(false, std::memory_order_release);
            EnterCriticalSection(&g_cursorLock);
            if (g_cursorStateMachine.IsWindowFocused()) {
                g_cursorStateMachine.OnFocusLost();
            }
            LeaveCriticalSection(&g_cursorLock);
        } else {
            RECT rcClient = {0};
            POINT ptOrigin = {0, 0};
            if (GetClientRect(hWnd, &rcClient) && ClientToScreen(hWnd, &ptOrigin)) {
                int w = rcClient.right - rcClient.left;
                int h = rcClient.bottom - rcClient.top;
                EnterCriticalSection(&g_cursorLock);
                g_cursorStateMachine.SetHeadless(g_isHeadlessProcess);
                g_cursorStateMachine.SetClientGeometry(ptOrigin.x, ptOrigin.y, w, h);
                LeaveCriticalSection(&g_cursorLock);
            }
        }
    } else if (g_isHeadlessProcess) {
        EnterCriticalSection(&g_cursorLock);
        g_cursorStateMachine.SetHeadless(true);
        LeaveCriticalSection(&g_cursorLock);
    }

    Kongor::CursorConfinementDecision decision{};
    EnterCriticalSection(&g_cursorLock);
    decision = g_cursorStateMachine.Evaluate();
    LeaveCriticalSection(&g_cursorLock);

    PFN_ClipCursor fnClip = g_pfnRealClipCursor ? g_pfnRealClipCursor : &::ClipCursor;
    BOOL apiOk = FALSE;
    if (decision.shouldConfine) {
        apiOk = fnClip(&decision.clipRect);
        g_currentlyConfined.store(apiOk != FALSE, std::memory_order_release);
    } else {
        apiOk = fnClip(nullptr);
        g_currentlyConfined.store(false, std::memory_order_release);
    }

    static bool s_loggedOnce = false;
    static bool s_lastConfined = false;
    static Kongor::CursorClipReason s_lastReason = Kongor::CursorClipReason::WindowUnfocused;
    static RECT s_lastRect = {0, 0, 0, 0};

    bool rectChanged = decision.shouldConfine &&
        (decision.clipRect.left != s_lastRect.left ||
         decision.clipRect.top != s_lastRect.top ||
         decision.clipRect.right != s_lastRect.right ||
         decision.clipRect.bottom != s_lastRect.bottom);

    if (!s_loggedOnce ||
        decision.shouldConfine != s_lastConfined ||
        decision.reason != s_lastReason ||
        rectChanged) {
        s_loggedOnce = true;
        s_lastConfined = decision.shouldConfine;
        s_lastReason = decision.reason;
        if (decision.shouldConfine) {
            s_lastRect = decision.clipRect;
        }
        char logBuf[256];
        if (decision.shouldConfine) {
            snprintf(logBuf, sizeof(logBuf),
                "[KONGOR CURSOR] CONFINED rect=[%ld, %ld, %ld, %ld] (%ldx%ld) trigger=%s ok=%d",
                decision.clipRect.left, decision.clipRect.top,
                decision.clipRect.right, decision.clipRect.bottom,
                decision.clipRect.right - decision.clipRect.left,
                decision.clipRect.bottom - decision.clipRect.top,
                triggerTag ? triggerTag : "unknown",
                (int)apiOk);
        } else {
            snprintf(logBuf, sizeof(logBuf),
                "[KONGOR CURSOR] RELEASED reason=%s trigger=%s ok=%d",
                Kongor::CursorClipReasonToString(decision.reason),
                triggerTag ? triggerTag : "unknown",
                (int)apiOk);
        }
        WriteSentinelLine(logBuf);
    }
}

// InstallCursorAndConfigIATHooks declared in native_guard_internal.h

static BOOL WINAPI HookedClipCursor(const RECT* lpRect) {
    (void)lpRect;
    InstallCursorAndConfigIATHooks();
    PollConstrainCursorCvarIfNeeded();
    ApplyCursorConfinementPolicy("K2::ClipCursor");
    return TRUE;
}

static BOOL WINAPI HookedGetCursorPos(LPPOINT lpPoint) {
    if (!lpPoint) return FALSE;

    // Drain any accumulated raw input deltas so they never build up stale offsets across Alt-Tab or multi-monitor movement
    g_rawAccumulatorX.store(0, std::memory_order_relaxed);
    g_rawAccumulatorY.store(0, std::memory_order_relaxed);

    // Zero-focus-theft background human click override: pin K2::CInput::Poll hover coordinates
    // to the target client/screen coordinate during synthetic background click actuation without moving OS cursor.
    if (g_bgClickOverrideActive.load(std::memory_order_acquire)) {
        uint64_t nowTick = GetTickCount64();
        if (nowTick <= g_bgClickExpireTick.load(std::memory_order_relaxed)) {
            lpPoint->x = g_bgClickScreenX.load(std::memory_order_relaxed);
            lpPoint->y = g_bgClickScreenY.load(std::memory_order_relaxed);
            EnterCriticalSection(&g_cursorLock);
            g_virtualCursorX = (double)lpPoint->x;
            g_virtualCursorY = (double)lpPoint->y;
            g_virtualCursorInitialized = true;
            LeaveCriticalSection(&g_cursorLock);
            return TRUE;
        }
        g_bgClickOverrideActive.store(false, std::memory_order_release);
    }

    // Always query the true live OS cursor position so K2::CInput::Poll hover coordinates
    // stay 100% locked to the Win32 WM_LBUTTONDOWN / WM_RBUTTONDOWN click lParam coordinates.
    BOOL ok = g_pfnRealGetCursorPos ? g_pfnRealGetCursorPos(lpPoint) : ::GetCursorPos(lpPoint);
    if (ok) {
        EnterCriticalSection(&g_cursorLock);
        g_virtualCursorX = (double)lpPoint->x;
        g_virtualCursorY = (double)lpPoint->y;
        g_virtualCursorInitialized = true;
        LeaveCriticalSection(&g_cursorLock);
    }
    return ok;
}

static BOOL WINAPI HookedSetCursorPos(int X, int Y) {
    EnterCriticalSection(&g_cursorLock);
    g_virtualCursorX = (double)X;
    g_virtualCursorY = (double)Y;
    g_virtualCursorInitialized = true;
    LeaveCriticalSection(&g_cursorLock);

    return g_pfnRealSetCursorPos ? g_pfnRealSetCursorPos(X, Y) : ::SetCursorPos(X, Y);
}

typedef void (WINAPI *FnExitProcess)(UINT uExitCode);
static FnExitProcess g_origExitProcess = nullptr;

static bool IsWrapperModuleForExitTelemetry(const char* baseName) noexcept {
    if (!baseName || !baseName[0]) return true;
    return (_stricmp(baseName, "version.dll") == 0 ||
            _stricmp(baseName, "ntdll.dll") == 0 ||
            _stricmp(baseName, "kernel32.dll") == 0 ||
            _stricmp(baseName, "kernelbase.dll") == 0 ||
            _stricmp(baseName, "ucrtbase.dll") == 0 ||
            _strnicmp(baseName, "msvcr", 5) == 0 ||
            _strnicmp(baseName, "vcruntime", 9) == 0 ||
            _strnicmp(baseName, "api-ms-win-", 11) == 0);
}

static void ResolveAddressModuleAndRvaA(const void* addr, char* outMod, size_t modCap, uintptr_t* outRva) noexcept {
    if (!outMod || modCap == 0) return;
    strncpy_s(outMod, modCap, "unknown", _TRUNCATE);
    if (outRva) *outRva = 0;
    if (!addr) return;

    HMODULE hMod = nullptr;
    if (GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(addr),
            &hMod) && hMod) {
        char fullPath[MAX_PATH] = {0};
        if (GetModuleFileNameA(hMod, fullPath, MAX_PATH) > 0) {
            const char* slash = strrchr(fullPath, '\\');
            const char* fslash = strrchr(fullPath, '/');
            const char* base = slash ? (slash + 1) : (fslash ? (fslash + 1) : fullPath);
            strncpy_s(outMod, modCap, base, _TRUNCATE);
        }
        if (outRva) {
            *outRva = reinterpret_cast<uintptr_t>(addr) - reinterpret_cast<uintptr_t>(hMod);
        }
    }
}

static void WINAPI Hooked_ExitProcess(UINT uExitCode) {
    if (uExitCode != 0) {
        Kongor_ForceReleaseCursorEmergency();

        const DWORD pid = GetCurrentProcessId();
        void* frames[8] = {nullptr};
        const USHORT captured = CaptureStackBackTrace(0, 8, frames, nullptr);

        char primaryMod[64] = "unknown";
        uintptr_t primaryRva = 0;
        bool foundPrimary = false;

        char stackBuf[512] = {0};
        size_t stackPos = 0;

        if (captured == 0) {
            void* retAddr = _ReturnAddress();
            ResolveAddressModuleAndRvaA(retAddr, primaryMod, sizeof(primaryMod), &primaryRva);
            snprintf(stackBuf, sizeof(stackBuf), "%s+0x%llX", primaryMod, static_cast<unsigned long long>(primaryRva));
        } else {
            for (USHORT i = 0; i < captured; ++i) {
                char frameMod[64] = "unknown";
                uintptr_t frameRva = 0;
                ResolveAddressModuleAndRvaA(frames[i], frameMod, sizeof(frameMod), &frameRva);

                if (!foundPrimary && !IsWrapperModuleForExitTelemetry(frameMod)) {
                    strncpy_s(primaryMod, sizeof(primaryMod), frameMod, _TRUNCATE);
                    primaryRva = frameRva;
                    foundPrimary = true;
                }

                const int written = snprintf(
                    stackBuf + stackPos,
                    sizeof(stackBuf) - stackPos,
                    "%s%s+0x%llX",
                    (stackPos > 0) ? " <- " : "",
                    frameMod,
                    static_cast<unsigned long long>(frameRva));
                if (written > 0 && stackPos + static_cast<size_t>(written) < sizeof(stackBuf)) {
                    stackPos += static_cast<size_t>(written);
                } else {
                    break;
                }
            }
            if (!foundPrimary) {
                ResolveAddressModuleAndRvaA(frames[0], primaryMod, sizeof(primaryMod), &primaryRva);
            }
        }

        SYSTEMTIME stUtc{};
        GetSystemTime(&stUtc);

        char msg[896] = {0};
        const int msgLen = snprintf(
            msg,
            sizeof(msg),
            "[EXIT_TELEMETRY] Non-zero ExitProcess(0x%08X) UTC=%04u-%02u-%02uT%02u:%02u:%02uZ PrimaryCaller=%s+0x%llX Stack=[%s] PID=%lu\n",
            static_cast<unsigned int>(uExitCode),
            static_cast<unsigned int>(stUtc.wYear),
            static_cast<unsigned int>(stUtc.wMonth),
            static_cast<unsigned int>(stUtc.wDay),
            static_cast<unsigned int>(stUtc.wHour),
            static_cast<unsigned int>(stUtc.wMinute),
            static_cast<unsigned int>(stUtc.wSecond),
            primaryMod,
            static_cast<unsigned long long>(primaryRva),
            stackBuf,
            static_cast<unsigned long>(pid));

        OutputDebugStringA(msg);
        WriteSentinelLine(msg);

        CreateDirectoryA("logs", nullptr);
        CreateDirectoryA("logs/crash", nullptr);
        if (msgLen > 0) {
            const DWORD bytesToWrite = static_cast<DWORD>((msgLen < static_cast<int>(sizeof(msg))) ? msgLen : (sizeof(msg) - 1));
            HANDLE hEngineCrash = CreateFileA(
                "logs/crash/engine_crashes.log",
                FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (hEngineCrash != INVALID_HANDLE_VALUE) {
                DWORD written = 0;
                WriteFile(hEngineCrash, msg, bytesToWrite, &written, nullptr);
                CloseHandle(hEngineCrash);
            }

            char exitLogPath[MAX_PATH] = {0};
            snprintf(exitLogPath, sizeof(exitLogPath), "logs/crash/exit_process_%lu.log", static_cast<unsigned long>(pid));
            HANDLE hExitLog = CreateFileA(
                exitLogPath,
                FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (hExitLog != INVALID_HANDLE_VALUE) {
                DWORD written = 0;
                WriteFile(hExitLog, msg, bytesToWrite, &written, nullptr);
                CloseHandle(hExitLog);
            }
        }
    }
    fflush(stdout);
    fflush(stderr);
    FlushSentinelLogs(true);

    if (g_origExitProcess) {
        g_origExitProcess(uExitCode);
    } else {
        ExitProcess(uExitCode);
    }
}

void InstallCursorAndConfigIATHooks() {
    static std::atomic<HMODULE> s_hookedClipK2{ nullptr };
    static std::atomic<HMODULE> s_hookedClipMain{ nullptr };
    static std::atomic<HMODULE> s_hookedClipCGame{ nullptr };
    static std::atomic<HMODULE> s_hookedCfgMsvcr{ nullptr };
    static std::atomic<HMODULE> s_hookedExitUcrt{ nullptr };
    static std::atomic<HMODULE> s_hookedExitD3d9{ nullptr };

    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (hK2 && s_hookedClipK2.load(std::memory_order_acquire) != hK2) {
        HookImport(hK2, "USER32.dll", "ClipCursor", (void*)HookedClipCursor, (void**)&g_pfnRealClipCursor);
        HookImport(hK2, "KERNEL32.dll", "CreateFileW", (void*)HookedCreateFileW, (void**)&g_pfnRealCreateFileW);
        HookImport(hK2, "KERNEL32.dll", "CreateFileA", (void*)HookedCreateFileA, (void**)&g_pfnRealCreateFileA);
        HookImport(hK2, "KERNEL32.dll", "ExitProcess", (void*)Hooked_ExitProcess, (void**)&g_origExitProcess);
        HookImport(hK2, "api-ms-win-crt-filesystem-l1-1-0.dll", "_waccess", (void*)HookedWaccess, (void**)&g_pfnRealWaccess);
        s_hookedClipK2.store(hK2, std::memory_order_release);
        Kongor_RegisterEmergencyCursorReleaseContext(g_pfnRealClipCursor, g_hGameWnd, &g_currentlyConfined, &g_cursorStateMachine);
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed ClipCursor + CreateFile + _waccess IAT hooks on k2_x64.dll.\n");
    }

    HMODULE hMain = GetModuleHandleA(NULL);
    if (hMain && s_hookedClipMain.load(std::memory_order_acquire) != hMain) {
        HookImport(hMain, "USER32.dll", "ClipCursor", (void*)HookedClipCursor, (void**)&g_pfnRealClipCursor);
        HookImport(hMain, "KERNEL32.dll", "CreateFileW", (void*)HookedCreateFileW, (void**)&g_pfnRealCreateFileW);
        HookImport(hMain, "KERNEL32.dll", "CreateFileA", (void*)HookedCreateFileA, (void**)&g_pfnRealCreateFileA);
        HookImport(hMain, "KERNEL32.dll", "ExitProcess", (void*)Hooked_ExitProcess, (void**)&g_origExitProcess);
        s_hookedClipMain.store(hMain, std::memory_order_release);
        Kongor_RegisterEmergencyCursorReleaseContext(g_pfnRealClipCursor, g_hGameWnd, &g_currentlyConfined, &g_cursorStateMachine);
    }

    HMODULE hCGame = GetModuleHandleA("cgame_x64.dll");
    if (hCGame && s_hookedClipCGame.load(std::memory_order_acquire) != hCGame) {
        HookImport(hCGame, "USER32.dll", "ClipCursor", (void*)HookedClipCursor, (void**)&g_pfnRealClipCursor);
        HookImport(hCGame, "KERNEL32.dll", "ExitProcess", (void*)Hooked_ExitProcess, (void**)&g_origExitProcess);
        s_hookedClipCGame.store(hCGame, std::memory_order_release);
        InstallCGameEntityDirectoryHooks(hCGame);
    }

    HMODULE hMsvcr = GetModuleHandleA("msvcr100.dll");
    if (hMsvcr && s_hookedCfgMsvcr.load(std::memory_order_acquire) != hMsvcr) {
        HookImport(hMsvcr, "KERNEL32.dll", "CreateFileW", (void*)HookedCreateFileW, (void**)&g_pfnRealCreateFileW);
        HookImport(hMsvcr, "KERNEL32.dll", "CreateFileA", (void*)HookedCreateFileA, (void**)&g_pfnRealCreateFileA);
        HookImport(hMsvcr, "KERNEL32.dll", "ExitProcess", (void*)Hooked_ExitProcess, (void**)&g_origExitProcess);
        s_hookedCfgMsvcr.store(hMsvcr, std::memory_order_release);
    }

    HMODULE hUcrt = GetModuleHandleA("ucrtbase.dll");
    if (hUcrt && s_hookedExitUcrt.load(std::memory_order_acquire) != hUcrt) {
        HookImport(hUcrt, "KERNEL32.dll", "ExitProcess", (void*)Hooked_ExitProcess, (void**)&g_origExitProcess);
        s_hookedExitUcrt.store(hUcrt, std::memory_order_release);
    }

    HMODULE hD3d9 = GetModuleHandleA("d3d9.dll");
    if (hD3d9 && s_hookedExitD3d9.load(std::memory_order_acquire) != hD3d9) {
        HookImport(hD3d9, "KERNEL32.dll", "ExitProcess", (void*)Hooked_ExitProcess, (void**)&g_origExitProcess);
        s_hookedExitD3d9.store(hD3d9, std::memory_order_release);
    }
}

void InstallRawInputIATHooks() {
    InstallCursorAndConfigIATHooks();
    if (!g_rawInputEnabled.load(std::memory_order_acquire)) return;

    static std::atomic<HMODULE> s_hookedRawK2{ nullptr };
    static std::atomic<HMODULE> s_hookedRawMain{ nullptr };

    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (hK2 && s_hookedRawK2.load(std::memory_order_acquire) != hK2) {
        HookImport(hK2, "USER32.dll", "GetCursorPos", (void*)HookedGetCursorPos, (void**)&g_pfnRealGetCursorPos);
        HookImport(hK2, "USER32.dll", "SetCursorPos", (void*)HookedSetCursorPos, (void**)&g_pfnRealSetCursorPos);
        s_hookedRawK2.store(hK2, std::memory_order_release);
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed IAT hooks for GetCursorPos and SetCursorPos on k2_x64.dll.\n");
    }

    HMODULE hMain = GetModuleHandleA(NULL);
    if (hMain && s_hookedRawMain.load(std::memory_order_acquire) != hMain) {
        HookImport(hMain, "USER32.dll", "GetCursorPos", (void*)HookedGetCursorPos, (void**)&g_pfnRealGetCursorPos);
        HookImport(hMain, "USER32.dll", "SetCursorPos", (void*)HookedSetCursorPos, (void**)&g_pfnRealSetCursorPos);
        s_hookedRawMain.store(hMain, std::memory_order_release);
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed IAT hooks for GetCursorPos and SetCursorPos on host exe.\n");
    }
}

// ============================================================================
// ALT AVATAR UNLOCK & GAME_SHARED HOOKS
// ============================================================================


typedef bool (__fastcall *FnCanAccessAltAvatar2)(void* pPlayer, const void* sHero, const void* sAvatar);
typedef bool (__fastcall *FnCanAccessAltAvatar1)(void* pPlayer, const void* sHeroAvatar);

static FnCanAccessAltAvatar2 g_origCanAccessAltAvatar2 = nullptr;
static FnCanAccessAltAvatar1 g_origCanAccessAltAvatar1 = nullptr;

static bool __fastcall HookedCanAccessAltAvatar2(void* pPlayer, const void* sHero, const void* sAvatar) {
    return true;
}

static bool __fastcall HookedCanAccessAltAvatar1(void* pPlayer, const void* sHeroAvatar) {
    return true;
}

// ResolveExport defined inline in native_guard_internal.h

// InstallInlineHook64 implemented in modern_archive_hook.cpp

typedef LPTOP_LEVEL_EXCEPTION_FILTER (WINAPI *PFN_SetUnhandledExceptionFilter)(LPTOP_LEVEL_EXCEPTION_FILTER lpTopLevelExceptionFilter);
// g_origSetUnhandledExceptionFilter defined at top of vfs_and_hooks.cpp

LPTOP_LEVEL_EXCEPTION_FILTER WINAPI Hooked_SetUnhandledExceptionFilter(LPTOP_LEVEL_EXCEPTION_FILTER lpTopLevelExceptionFilter) {
    OutputDebugStringA("[KONGOR NATIVE GUARD] SetUnhandledExceptionFilter intercepted - preserving NativeGuard.\n");
    return NativeGuardUnhandledExceptionFilter;
}

BOOL WINAPI Hooked_IsDebuggerPresent() {
    return FALSE;
}

BOOL WINAPI Hooked_CheckRemoteDebuggerPresent(HANDLE hProcess, PBOOL pbDebuggerPresent) {
    if (pbDebuggerPresent) *pbDebuggerPresent = FALSE;
    return TRUE;
}

// ============================================================================
// K2_X64 SAFE TYPE CONVERSION & LUA EXCEPTION NEUTRALIZERS
// ============================================================================

static bool Hooked_AtoBFastW(const wchar_t* pStr) {
    if (!pStr || (uintptr_t)pStr <= 0xFFFF) {
        return (uintptr_t)pStr == 1;
    }
    wchar_t c = *pStr;
    if (c == L'1' || c == L't' || c == L'T' || c == L'y' || c == L'Y') return true;
    if (c == L'0' || c == L'f' || c == L'F' || c == L'n' || c == L'N' || c == L'\0') return false;
    wchar_t* end = nullptr;
    float val = wcstof(pStr, &end);
    return val != 0.0f;
}

static bool Hooked_AtoBFastA(const char* pStr) {
    if (!pStr || (uintptr_t)pStr <= 0xFFFF) {
        return (uintptr_t)pStr == 1;
    }
    char c = *pStr;
    if (c == '1' || c == 't' || c == 'T' || c == 'y' || c == 'Y') return true;
    if (c == '0' || c == 'f' || c == 'F' || c == 'n' || c == 'N' || c == '\0') return false;
    char* end = nullptr;
    float val = strtof(pStr, &end);
    return val != 0.0f;
}

static float Hooked_AtoFFastW(const wchar_t* pStr) {
    if (!pStr || (uintptr_t)pStr <= 0xFFFF) {
        return 0.0f;
    }
    wchar_t* end = nullptr;
    return wcstof(pStr, &end);
}

static float Hooked_AtoFFastA(const char* pStr) {
    if (!pStr || (uintptr_t)pStr <= 0xFFFF) {
        return 0.0f;
    }
    char* end = nullptr;
    return strtof(pStr, &end);
}

typedef void (*FnLuaExecuteStack)(void* L, int nargs, bool clear);
static FnLuaExecuteStack g_origLuaExecuteStack = nullptr;

static void Hooked_Lua_ExecuteStack_Impl(void* L, int nargs, bool clear);

static void Hooked_Lua_ExecuteStack(void* L, int nargs, bool clear) {
    KONGOR_TRACY_ZONE_SCOPED_N("K2_Lua_ExecuteStack");
    Hooked_Lua_ExecuteStack_Impl(L, nargs, clear);
}

static void Hooked_Lua_ExecuteStack_Impl(void* L, int nargs, bool clear) {
    if (L && (GetTickCount64() - GetLastHeroKeyframeSampleTickMs() >= 250)) {
        RefreshServerKeyframePlayersFromEngine();
    }
    if (!L) return;

    Kongor::EnsureKongorLuaSharedMem(L, &g_shmHandle);

    int64_t top = *reinterpret_cast<int64_t*>(reinterpret_cast<uint8_t*>(L) + 0x10);
    int64_t base = *reinterpret_cast<int64_t*>(reinterpret_cast<uint8_t*>(L) + 0x18);
    int count = static_cast<int>((top - base) >> 4);
    if (count < nargs + 1) {
        // Stack underflow / invalid stack state: return cleanly to prevent fatal C++ exception throw
        return;
    }
    Kongor_EnterProtectedSeh();
    __try {
        if (g_origLuaExecuteStack) {
            g_origLuaExecuteStack(L, nargs, clear);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Intercepted and neutralized exception inside Lua::ExecuteStack.\n");
    }
    Kongor_LeaveProtectedSeh();
}

// ----------------------------------------------------------------------------
// IWidget::ExecuteLuaCallback Crash Shield
// Prevents unhandled C++ exceptions (0xE06D7363) and access violations (0xC0000005)
// during widget event callbacks and teardown/purges from crashing the process.
// ----------------------------------------------------------------------------
typedef void (__fastcall *FnWidgetExecuteLuaCallback)(void* pWidget, uint32_t eventId, void* pParams);
static FnWidgetExecuteLuaCallback g_origWidgetExecuteLuaCallback = nullptr;

static void __fastcall Hooked_WidgetExecuteLuaCallback(void* pWidget, uint32_t eventId, void* pParams) {
    if (!pWidget) return;
    Kongor::GetGlobalBinaryTelemetryRing().Record(
        eventId,
        "IWidget::ExecuteLuaCallback",
        reinterpret_cast<uint64_t>(pWidget),
        reinterpret_cast<uint64_t>(pParams));
    if (!Kongor::WidgetSafety::IsWidgetPointerReadableAndValid(pWidget)) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Option J: Blocked IWidget::ExecuteLuaCallback on unreadable widget or non-executable vtable.\n");
        return;
    }
    Kongor_EnterProtectedSeh();
    __try {
        if (g_origWidgetExecuteLuaCallback) {
            g_origWidgetExecuteLuaCallback(pWidget, eventId, pParams);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Intercepted and neutralized exception inside IWidget::ExecuteLuaCallback.\n");
    }
    Kongor_LeaveProtectedSeh();
}

// ----------------------------------------------------------------------------
// CBitmap Software Mipmap Downsampling Crash Shield
// Prevents 0xC0000005 crash in vid_d3d9 software mipmap loops when CBitmap::ScaleDown
// fails on block-compressed (DXT) or unsupported texture formats.
// ----------------------------------------------------------------------------
typedef void (__fastcall *FnCBitmapAlloc)(void* pThis, int iWidth, int iHeight, int iBMPType, unsigned char* pData);
static FnCBitmapAlloc g_pfnCBitmapAlloc = nullptr;

typedef void (__fastcall *FnCBitmapScaleDownGamma)(const void* pThis, void* pDest, int iWidth, int iHeight, float fGamma);
static FnCBitmapScaleDownGamma g_origCBitmapScaleDownGamma = nullptr;

typedef void (__fastcall *FnCBitmapScaleDown)(const void* pThis, void* pDest, int iWidth, int iHeight);
static FnCBitmapScaleDown g_origCBitmapScaleDown = nullptr;

static void __fastcall Hooked_CBitmap_ScaleDownGamma(const void* pThis, void* pDest, int iWidth, int iHeight, float fGamma) {
    if (!pThis || !pDest) return;

    int bmpType = *reinterpret_cast<const int*>(reinterpret_cast<const char*>(pThis) + 0x14);
    if (bmpType < 1 || (bmpType > 4 && bmpType != 0x10)) {
        if (g_pfnCBitmapAlloc) {
            g_pfnCBitmapAlloc(pDest, iWidth, iHeight, 4 /* BMP_RGBA */, nullptr);
            void* pData = *reinterpret_cast<void**>(pDest);
            int size = *reinterpret_cast<int*>(reinterpret_cast<char*>(pDest) + 8);
            if (pData && size > 0) {
                memset(pData, 0, size);
            }
        }
        return;
    }

    if (g_origCBitmapScaleDownGamma) {
        g_origCBitmapScaleDownGamma(pThis, pDest, iWidth, iHeight, fGamma);
    }

    if (*reinterpret_cast<void**>(pDest) == nullptr && g_pfnCBitmapAlloc) {
        g_pfnCBitmapAlloc(pDest, iWidth, iHeight, 4, nullptr);
        void* pData = *reinterpret_cast<void**>(pDest);
        int size = *reinterpret_cast<int*>(reinterpret_cast<char*>(pDest) + 8);
        if (pData && size > 0) {
            memset(pData, 0, size);
        }
    }
}

static void __fastcall Hooked_CBitmap_ScaleDown(const void* pThis, void* pDest, int iWidth, int iHeight) {
    if (!pThis || !pDest) return;

    int bmpType = *reinterpret_cast<const int*>(reinterpret_cast<const char*>(pThis) + 0x14);
    if (bmpType < 1 || (bmpType > 4 && bmpType != 0x10)) {
        if (g_pfnCBitmapAlloc) {
            g_pfnCBitmapAlloc(pDest, iWidth, iHeight, 4 /* BMP_RGBA */, nullptr);
            void* pData = *reinterpret_cast<void**>(pDest);
            int size = *reinterpret_cast<int*>(reinterpret_cast<char*>(pDest) + 8);
            if (pData && size > 0) {
                memset(pData, 0, size);
            }
        }
        return;
    }

    if (g_origCBitmapScaleDown) {
        g_origCBitmapScaleDown(pThis, pDest, iWidth, iHeight);
    }

    if (*reinterpret_cast<void**>(pDest) == nullptr && g_pfnCBitmapAlloc) {
        g_pfnCBitmapAlloc(pDest, iWidth, iHeight, 4, nullptr);
        void* pData = *reinterpret_cast<void**>(pDest);
        int size = *reinterpret_cast<int*>(reinterpret_cast<char*>(pDest) + 8);
        if (pData && size > 0) {
            memset(pData, 0, size);
        }
    }
}

// ----------------------------------------------------------------------------
// CResourceManager::Unregister Procedural Texture Shield
// Protects built-in procedural textures ($white = 0x0500000c / #0012, $black,
// $checker, $flat, $invis, etc.) registered at startup with empty m_sPath from
// being destroyed when CInterfaceResource::Free unregisters child widget handles.
// ----------------------------------------------------------------------------
typedef bool (__fastcall *FnResourceManagerUnregister)(void* pThis, uint32_t hResource, uint32_t eUnregisterOp);
static FnResourceManagerUnregister g_origResourceManagerUnregister = nullptr;

static bool __fastcall Hooked_CResourceManager_Unregister(void* pThis, uint32_t hResource, uint32_t eUnregisterOp) {
    uint32_t resType = (hResource >> 24) & 0xFF;
    uint32_t resIdx  = hResource & 0x00FFFFFF;
    if (resType == 0x05 && resIdx <= 0x20) {
        static std::atomic<uint32_t> s_protectedUnregLogCount{0};
        uint32_t count = s_protectedUnregLogCount.fetch_add(1, std::memory_order_relaxed);
        if (count < 32) {
            char msg[192];
            snprintf(msg, sizeof(msg),
                "[KONGOR NATIVE GUARD] Blocked CResourceManager::Unregister on built-in procedural texture handle 0x%08X (idx=#%04u, op=%u)\n",
                hResource, resIdx, eUnregisterOp);
            OutputDebugStringA(msg);
        }
        return true;
    }

    if (g_origResourceManagerUnregister) {
        __try {
            return g_origResourceManagerUnregister(pThis, hResource, eUnregisterOp);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Neutralized crash inside CResourceManager::Unregister.\n");
            return false;
        }
    }
    return false;
}

// ============================================================================
// CSystem::GetWindowArea Live Client-Rect Synchronization Hook
// Fixes Alt-Tab hover vs. click desynchronization:
// - K2::MainWndProc WM_LBUTTONDOWN/WM_RBUTTONDOWN uses Win32 lParam, which is
//   always relative to ClientToScreen(hWnd, {0, 0}).
// - K2::CInput::Poll / CSystem::GetMousePos computes hover coordinates as
//   GetCursorPos(&pt) - CSystem::GetWindowArea().topLeft.
// - After Alt-Tab under DXVK / borderless / multi-monitor setups, CVid::GetWindowArea
//   can return stale or non-client-offset coordinates. Returning live
//   ClientToScreen + GetClientRect keeps hover and click coordinates identical.
// ============================================================================
typedef int* (__fastcall *FnCSystemGetWindowArea)(void* pThis, int* pOutRect);
static FnCSystemGetWindowArea g_origCSystemGetWindowArea = nullptr;

static int* __fastcall Hooked_CSystem_GetWindowArea(void* pThis, int* pOutRect) {
    __try {
        if (g_origCSystemGetWindowArea) {
            g_origCSystemGetWindowArea(pThis, pOutRect);
        }
        if (pOutRect) {
            HWND hWnd = g_hGameWnd;
            if (hWnd && IsWindow(hWnd) && !IsIconic(hWnd)) {
                RECT rcClient = {0, 0, 0, 0};
                POINT ptTopLeft = {0, 0};
                if (GetClientRect(hWnd, &rcClient) && ClientToScreen(hWnd, &ptTopLeft)) {
                    int w = rcClient.right - rcClient.left;
                    int h = rcClient.bottom - rcClient.top;
                    // Only override when the window has valid client dimensions and is not parked offscreen headlessly
                    if (w > 0 && h > 0 && ptTopLeft.x > -5000 && ptTopLeft.y > -5000) {
                        pOutRect[0] = ptTopLeft.x;
                        pOutRect[1] = ptTopLeft.y;
                        pOutRect[2] = ptTopLeft.x + w;
                        pOutRect[3] = ptTopLeft.y + h;
                        g_clientOriginX.store(ptTopLeft.x, std::memory_order_relaxed);
                        g_clientOriginY.store(ptTopLeft.y, std::memory_order_relaxed);
                        g_clientWindowWidth.store(w, std::memory_order_relaxed);
                        g_clientWindowHeight.store(h, std::memory_order_relaxed);
                    }
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Exception caught inside Hooked_CSystem_GetWindowArea.\n");
    }
    return pOutRect;
}

// ============================================================================
// W2-008: Live Mid-Game Tree Stumps (CModel::Load @ 0x947520) &
// High-Visibility Floating Combat Damage Text (littletextpopup CFontMap +0xA4)
// ============================================================================
typedef int (__fastcall *FnCModelLoad)(void* pThis, uint32_t uiIgnoreFlags, const char* pData, uint32_t uiSize);
static FnCModelLoad g_origCModelLoad = nullptr;

static const char kStumpModelMdfXml[] =
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
    "<model name=\"model.max\" file=\"../stump/model.model\" type=\"K2\">\n"
    "<anim name=\"death_1\" clip=\"clips/death_01.clip\" fps=\"30\" loop=\"false\">\n"
    "\t\t<frameevent frame=\"1\" cmd=\"StartEffect death.effect\"/>\n"
    "\t</anim>\n"
    "</model>\n";

static int __fastcall Hooked_CModel_Load(void* pThis, uint32_t uiIgnoreFlags, const char* pData, uint32_t uiSize) {
    if (!pThis || !g_origCModelLoad) return 0;
    // S2 Games uninitialized member bug fix: zero out m_pFileChangeCallback at pThis + 0xC0
    *(void**)((uint8_t*)pThis + 0xC0) = nullptr;
    Kongor_EnterProtectedSeh();
    int res = 0;
    __try {
        if (Kongor_IsStumpsTreesEnabled()) {
            const wchar_t* wszPath = GetK2StringData(reinterpret_cast<const uint8_t*>(pThis) + 8);
            if (wszPath && *wszPath) {
                char utf8Path[512] = {0};
                int n = WideCharToMultiByte(CP_UTF8, 0, wszPath, -1, utf8Path, sizeof(utf8Path), NULL, NULL);
                if (n > 0 &&
                    Kongor_MatchWildcardA(utf8Path, "*world/props/trees/*/model.mdf", true) &&
                    !Kongor_MatchWildcardA(utf8Path, "*world/props/trees/stump/model.mdf", true) &&
                    !Kongor_MatchWildcardA(utf8Path, "*death*", true)) {
                    static std::atomic<uint32_t> s_stumpLogCount{0};
                    uint32_t cnt = s_stumpLogCount.fetch_add(1, std::memory_order_relaxed);
                    if (cnt < 32) {
                        char msg[256];
                        snprintf(msg, sizeof(msg),
                            "[KONGOR NATIVE GUARD] [MOD-17] Redirecting CModel::Load '%s' -> stump/model.mdf\n",
                            utf8Path);
                        OutputDebugStringA(msg);
                    }
                    res = g_origCModelLoad(pThis, uiIgnoreFlags, kStumpModelMdfXml, static_cast<uint32_t>(sizeof(kStumpModelMdfXml) - 1));
                    Kongor_LeaveProtectedSeh();
                    return res;
                }
            }
        }
        res = g_origCModelLoad(pThis, uiIgnoreFlags, pData, uiSize);
    } __except (GetExceptionCode() == 0xE06D7363u ? EXCEPTION_CONTINUE_SEARCH : EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Exception neutralized inside Hooked_CModel_Load.\n");
    }
    Kongor_LeaveProtectedSeh();
    return res;
}

typedef uint32_t (__fastcall *FnResourceManagerLookUpName)(void* pThis, const void* pK2StringW, uint32_t eResType);
typedef void* (__fastcall *FnResourceManagerGetFontMap)(void* pThis, uint32_t hFontMap);
typedef bool (__fastcall *FnResourceManagerReload)(void* pThis, uint32_t hResource);

static void ApplyCustomDamageFloatsFontScale(bool enabled) {
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) return;

    Kongor_EnterProtectedSeh();
    __try {
        void** ppRM = reinterpret_cast<void**>(ResolveExport(hK2, "?g_ResourceManager@@3AEAVCResourceManager@@EA", 0x1279018));
        void* pRM = (ppRM && *ppRM) ? *ppRM : nullptr;
        auto pfnLookUpName = reinterpret_cast<FnResourceManagerLookUpName>(
            ResolveExport(hK2, "?LookUpName@CResourceManager@@QEAAIAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@W4EResourceType@@@Z", 0x995110));
        auto pfnGetFontMap = reinterpret_cast<FnResourceManagerGetFontMap>(
            ResolveExport(hK2, "?GetFontMap@CResourceManager@@QEAAPEAVCFontMap@@I@Z", 0x9940d0));
        auto pfnReload = reinterpret_cast<FnResourceManagerReload>(
            ResolveExport(hK2, "?Reload@CResourceManager@@QEAA_NI@Z", 0x991300));

        if (pRM && pfnLookUpName && pfnGetFontMap && pfnReload) {
            K2StringW fontName(L"littletextpopup");
            uint32_t hFontMap = pfnLookUpName(pRM, &fontName, 0x0Bu /* RES_FONTMAP */);
            if (hFontMap != 0xFFFFFFFFu && hFontMap != 0u) {
                void* pFontMap = pfnGetFontMap(pRM, hFontMap);
                if (pFontMap) {
                    int targetSize = enabled ? 26 : 16;
                    int prevSize = *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(pFontMap) + 0xA4);
                    if (prevSize != targetSize) {
                        *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(pFontMap) + 0xA4) = targetSize;
                        bool reloaded = pfnReload(pRM, hFontMap);
                        char msg[256];
                        snprintf(msg, sizeof(msg),
                            "[KONGOR NATIVE GUARD] [MOD-5] Resized littletextpopup CFontMap base size %d -> %d (hFont=0x%08X, reloaded=%d)\n",
                            prevSize, targetSize, hFontMap, reloaded ? 1 : 0);
                        OutputDebugStringA(msg);
                    }
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Exception neutralized inside ApplyCustomDamageFloatsFontScale.\n");
    }
    Kongor_LeaveProtectedSeh();
}

typedef Kongor::FnK2SliderMouseDown FnSliderMouseDown;
static FnSliderMouseDown g_pfnCSliderMouseDown = nullptr;
static FnSliderMouseDown g_origScrollbarSliderMouseDown = nullptr;

// satisfies: OBL-NG-SCROLLBAR-02
static void __fastcall Hooked_CScrollbarSlider_MouseDown(void* thisPtr, int eButton, const void* cursorPos) {
    // When left mouse button (0x137 / BUTTON_MOUSEL) is clicked on the scrollbar track and both cursorPos
    // and CSlider::m_pHandle (+0x190) are non-null, invoke base CSlider::MouseDown (k2_x64.dll + 0x9C9D80)
    // inside a protected SEH frame; otherwise delegate wheel/other events to g_origScrollbarSliderMouseDown.
    Kongor::DispatchScrollbarSliderMouseDown(
        thisPtr,
        eButton,
        cursorPos,
        g_pfnCSliderMouseDown,
        g_origScrollbarSliderMouseDown);
}

// ============================================================================
// Upgrades & Taunts Runtime Bridge (Project Kongor)
// Synchronizes Vault equipped taunt/announcer with offline practice & matches
// ============================================================================

typedef bool (__fastcall *FnPlayerHasUpgrade)(void* pPlayer, const void* pUpgradeString);
static FnPlayerHasUpgrade g_origPlayerHasUpgrade = nullptr;

typedef void (__fastcall *FnPlayerUpdateUpgrades)(void* pPlayer);
static FnPlayerUpdateUpgrades g_origPlayerUpdateUpgrades = nullptr;

typedef void (__fastcall *FnPlayerSelectHero)(void* pPlayer, uint16_t unHeroID);
static FnPlayerSelectHero g_origPlayerSelectHero = nullptr;

typedef bool (__fastcall *FnPlayerSpawnHero)(void* pPlayer);
static FnPlayerSpawnHero g_origPlayerSpawnHero = nullptr;

typedef void (__fastcall *FnClientAccountSelectUpgrade)(void* pThis, const K2WString* pProductCode);
static FnClientAccountSelectUpgrade g_origClientAccountSelectUpgrade = nullptr;

typedef void (__fastcall *FnHostClientConnect)(
    void* pThis,
    const void* pAddr,
    bool bSilent,
    const void* pLoadingInterface,
    const void* pExtra,
    bool bPractice,
    bool bSpectate);
static FnHostClientConnect g_origCHostClientConnect = nullptr;

typedef void* (__fastcall *FnK2WStringAssign)(void* pK2WStr, const wchar_t* pPtr, size_t count);
static FnK2WStringAssign g_pfnK2WStringAssign = nullptr;
static bool g_k2WStringAssignChecked = false;

typedef void (__fastcall *FnUnregFileChangeCallback)(void* pThis, void* pCallback);
static FnUnregFileChangeCallback g_origUnregFileChangeCallback = nullptr;

typedef uint32_t (__fastcall *FnUpgradesLookup)(void* pUpgrades, const void* pName);
static FnUpgradesLookup g_pfnLookupTaunt = nullptr;
static FnUpgradesLookup g_pfnLookupAnnouncerVoice = nullptr;

typedef K2WString* (__fastcall *FnICvarGetString)(K2WString* retVal, const K2WString* name);
static FnICvarGetString g_pfnICvarGetString = nullptr;

typedef void (__fastcall *FnCMemManagerDeallocate)(void* pMem);
static FnCMemManagerDeallocate g_pfnCMemManagerDeallocate = nullptr;

typedef uint32_t (__fastcall *FnLookupAvatarKey)(void* pRegistry, const K2WString* name);
static FnLookupAvatarKey g_pfnLookupAvatarKey = nullptr;
static void** g_ppEntityRegistry = nullptr;

typedef bool (__fastcall *FnPlayerIsBot)(const void* pPlayer);
static FnPlayerIsBot g_pfnPlayerIsBot = nullptr;

// [L2-P0-02] SEH-guarded K2StringW substring matcher — never casts K2's 32-byte basic_string<wchar_t,
// K2StringAllocator> to MSVC's std::wstring (ABI/allocator/iterator-debug mismatch hazard).
static bool SafeK2StringContainsSuperTaunt(const void* pUpgradeString) noexcept {
    if (!pUpgradeString) return false;
    static constexpr wchar_t kNeedle[] = L"Super-Taunt";
    static constexpr size_t kNeedleLen = (sizeof(kNeedle) / sizeof(wchar_t)) - 1;
    bool matched = false;
    Kongor_EnterProtectedSeh();
    __try {
        const wchar_t* data = GetK2StringData(pUpgradeString);
        size_t len = GetK2StringLength(pUpgradeString);
        if (data != nullptr && len >= kNeedleLen && len <= 4096) {
            for (size_t i = 0; i + kNeedleLen <= len; ++i) {
                if (wmemcmp(data + i, kNeedle, kNeedleLen) == 0) {
                    matched = true;
                    break;
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        matched = false;
    }
    Kongor_LeaveProtectedSeh();
    return matched;
}

static bool __fastcall Hooked_CPlayer_HasUpgrade(void* pPlayer, const void* pUpgradeString) {
    if (SafeK2StringContainsSuperTaunt(pUpgradeString)) {
        // Grant Super-Taunt cooldown reset unconditionally
        return true;
    }
    if (g_origPlayerHasUpgrade) {
        return g_origPlayerHasUpgrade(pPlayer, pUpgradeString);
    }
    return true;
}

static void __fastcall Hooked_CClientAccount_SelectUpgrade(void* pThis, const K2WString* pProductCode) {
    if (pProductCode) {
        const wchar_t* wsz = pProductCode->c_str();
        if (wsz && *wsz) {
            UpdateSelectedUpgradeFromStr(wsz);
        }
    }
    if (g_origClientAccountSelectUpgrade) {
        g_origClientAccountSelectUpgrade(pThis, pProductCode);
    }
}

static void EnsureK2NicknameSymbolsResolved() noexcept {
    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (!hK2) return;
    if (!g_ppHost) {
        g_ppHost = reinterpret_cast<void**>(GetProcAddress(hK2, "?g_pHost@@3PEAVCHost@@EA"));
    }
    if (!g_pfnICvarGetString) {
        g_pfnICvarGetString = reinterpret_cast<FnICvarGetString>(
            ResolveExport(hK2, "?GetString@ICvar@@SA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@AEBV23@@Z", 0x85a8b0));
    }
    if (!g_pfnCMemManagerDeallocate) {
        g_pfnCMemManagerDeallocate = reinterpret_cast<FnCMemManagerDeallocate>(
            ResolveExport(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z", 0x2a4ab0));
    }
    if (!g_k2WStringAssignChecked) {
        g_k2WStringAssignChecked = true;
        const uint8_t* pCand = reinterpret_cast<const uint8_t*>(hK2) + 0x2A4890;
        static const uint8_t kExpectedAssignPrologue[8] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C };
        if (memcmp(pCand, kExpectedAssignPrologue, sizeof(kExpectedAssignPrologue)) == 0) {
            g_pfnK2WStringAssign = reinterpret_cast<FnK2WStringAssign>(const_cast<uint8_t*>(pCand));
        } else {
            WriteSentinelLine("[KONGOR NATIVE GUARD] [NICKNAME] [WARN] K2WString::assign prologue mismatch at k2_x64+0x2A4890; skipping native string reallocation.");
        }
    }
}

static bool SyncOfflineHostClientNicknameSeh(
    void* pHostClient,
    const char* triggerTag,
    bool logWhenUnchangedOrOnline,
    char* outLogBuf,
    size_t outLogCap) noexcept
{
    if (!pHostClient || !outLogBuf || outLogCap == 0) return false;
    outLogBuf[0] = '\0';

    bool emitLog = false;
    Kongor_EnterProtectedSeh();
    __try {
        uint8_t* pClient = reinterpret_cast<uint8_t*>(pHostClient);
        // CClientAccount embedded at CHostClient + 0x48:
        //   +0xD0 = m_uiAccountID (0xFFFFFFFF when logged out / offline)
        //   +0xE0 = m_sCookie     (empty when logged out / offline)
        //   +0x120 = m_sNickname  (K2WString serialized into NETCMD_CONNECT @ k2_x64+0x8D5450)
        uint32_t acctId = *reinterpret_cast<const uint32_t*>(pClient + 0xD0);
        bool hasCookie = (GetK2StringLength(pClient + 0xE0) > 0);

        if ((acctId != 0u && acctId != 0xFFFFFFFFu) || hasCookie) {
            if (logWhenUnchangedOrOnline) {
                snprintf(outLogBuf, outLogCap,
                    "[KONGOR NATIVE GUARD] [NICKNAME] Preserving authenticated online nickname (trigger=%s, acctId=%u, hasCookie=%d).",
                    triggerTag ? triggerTag : "unknown", acctId, hasCookie ? 1 : 0);
                emitLog = true;
            }
        } else if (g_pfnK2WStringAssign != nullptr) {
            wchar_t p2pVal[64] = {0};
            wchar_t loginVal[64] = {0};

            if (g_pfnICvarGetString) {
                K2WString k2P2pName(L"p2p_nickname");
                K2WString outP2p;
                g_pfnICvarGetString(&outP2p, &k2P2pName);
                if (outP2p.size > 0 && outP2p.c_str()) {
                    wcsncpy_s(p2pVal, _countof(p2pVal), outP2p.c_str(), _TRUNCATE);
                }
                if (outP2p.res > 7 && outP2p.ptr && g_pfnCMemManagerDeallocate) {
                    g_pfnCMemManagerDeallocate(const_cast<wchar_t*>(outP2p.ptr));
                }

                K2WString k2LoginName(L"login_name");
                K2WString outLogin;
                g_pfnICvarGetString(&outLogin, &k2LoginName);
                if (outLogin.size > 0 && outLogin.c_str()) {
                    wcsncpy_s(loginVal, _countof(loginVal), outLogin.c_str(), _TRUNCATE);
                }
                if (outLogin.res > 7 && outLogin.ptr && g_pfnCMemManagerDeallocate) {
                    g_pfnCMemManagerDeallocate(const_cast<wchar_t*>(outLogin.ptr));
                }
            }

            const wchar_t* fallbackTracked = (g_pfnICvarGetString == nullptr) ? g_wszTrackedOfflineNick : nullptr;
            wchar_t resolvedNick[32] = {0};
            KongorOfflineNickSource src = KongorOfflineNickSource::DefaultPlayerFallback;

            if (Kongor_ResolveOfflinePlayerNickname(
                    acctId, hasCookie, p2pVal, loginVal, fallbackTracked,
                    resolvedNick, _countof(resolvedNick), &src)) {
                wchar_t prevNick[64] = {0};
                const wchar_t* curData = GetK2StringData(pClient + 0x120);
                size_t curLen = GetK2StringLength(pClient + 0x120);
                if (curData && curLen > 0) {
                    size_t copyLen = (curLen < _countof(prevNick) - 1) ? curLen : (_countof(prevNick) - 1);
                    wcsncpy_s(prevNick, _countof(prevNick), curData, copyLen);
                }

                bool changed = (wcscmp(prevNick, resolvedNick) != 0);
                if (changed) {
                    g_pfnK2WStringAssign(pClient + 0x120, resolvedNick, wcslen(resolvedNick));
                }

                if (g_pTelemetryHeader) {
                    char asciiName[sizeof(g_pTelemetryHeader->PlayerName)] = {0};
                    size_t nIdx = 0;
                    while (resolvedNick[nIdx] != L'\0' && nIdx + 1 < sizeof(asciiName)) {
                        asciiName[nIdx] = static_cast<char>(resolvedNick[nIdx]);
                        ++nIdx;
                    }
                    if (nIdx > 0) {
                        strncpy_s(g_pTelemetryHeader->PlayerName, sizeof(g_pTelemetryHeader->PlayerName), asciiName, _TRUNCATE);
                    }
                }

                if (changed || logWhenUnchangedOrOnline) {
                    snprintf(outLogBuf, outLogCap,
                        "[KONGOR NATIVE GUARD] [NICKNAME] Offline/P2P nickname synced (len=%zu, prevEmpty=%d, changed=%d, source=%s, trigger=%s).",
                        wcslen(resolvedNick),
                        prevNick[0] ? 0 : 1,
                        changed ? 1 : 0,
                        Kongor_OfflineNickSourceToString(src),
                        triggerTag ? triggerTag : "unknown");
                    emitLog = true;
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        snprintf(outLogBuf, outLogCap,
            "[KONGOR NATIVE GUARD] [NICKNAME] [ERROR] SEH exception 0x%08lX during SyncOfflineHostClientNicknameSeh (trigger=%s).",
            GetExceptionCode(), triggerTag ? triggerTag : "unknown");
        emitLog = true;
    }
    Kongor_LeaveProtectedSeh();
    return emitLog;
}

static void SyncOfflineHostClientNickname(void* pHostClient, const char* triggerTag, bool logWhenUnchangedOrOnline) {
    if (!pHostClient || IsDedicatedServerProcessCached()) return;
    if (g_hGameWnd != NULL && IsWindow(g_hGameWnd)) {
        DWORD wndTid = GetWindowThreadProcessId(g_hGameWnd, NULL);
        if (wndTid != 0 && GetCurrentThreadId() != wndTid) {
            return;
        }
    }
    EnsureK2NicknameSymbolsResolved();
    char logBuf[384] = {0};
    if (SyncOfflineHostClientNicknameSeh(pHostClient, triggerTag, logWhenUnchangedOrOnline, logBuf, sizeof(logBuf))) {
        WriteSentinelLine(logBuf);
    }
}

static void* ResolvePrimaryHostClientSeh() noexcept {
    void* pHostClient = nullptr;
    Kongor_EnterProtectedSeh();
    __try {
        if (g_ppHost && *g_ppHost) {
            uint8_t* pHost = reinterpret_cast<uint8_t*>(*g_ppHost);
            uint8_t* pMapHead = *reinterpret_cast<uint8_t* const*>(pHost + 0xF0);
            uint64_t mapSize = *reinterpret_cast<const uint64_t*>(pHost + 0xF8);
            if (pMapHead && mapSize > 0 && mapSize <= 64) {
                uint8_t* pFirstNode = *reinterpret_cast<uint8_t* const*>(pMapHead);
                if (pFirstNode && pFirstNode != pMapHead) {
                    pHostClient = *reinterpret_cast<void* const*>(pFirstNode + 0x28);
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        pHostClient = nullptr;
    }
    Kongor_LeaveProtectedSeh();
    return pHostClient;
}

static void SyncActiveHostClientOfflineNickname(const char* triggerTag) {
    if (IsDedicatedServerProcessCached()) return;
    EnsureK2NicknameSymbolsResolved();
    void* pHostClient = ResolvePrimaryHostClientSeh();
    if (pHostClient) {
        SyncOfflineHostClientNickname(pHostClient, triggerTag, false);
    }
}

static bool TryReadHostClientConnectAddrSeh(const void* pAddr, wchar_t* outBuf, size_t outCap) noexcept {
    if (!pAddr || !outBuf || outCap == 0) return false;
    outBuf[0] = L'\0';
    bool ok = false;
    Kongor_EnterProtectedSeh();
    __try {
        const wchar_t* rawData = GetK2StringData(pAddr);
        const size_t rawLen = GetK2StringLength(pAddr);
        if (rawData && rawLen > 0 && rawLen < 256) {
            const size_t copyLen = (rawLen < outCap - 1) ? rawLen : (outCap - 1);
            wcsncpy_s(outBuf, outCap, rawData, copyLen);
            ok = true;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        outBuf[0] = L'\0';
        ok = false;
    }
    Kongor_LeaveProtectedSeh();
    return ok;
}

static void __fastcall Hooked_CHostClient_Connect(
    void* pThis,
    const void* pAddr,
    bool bSilent,
    const void* pLoadingInterface,
    const void* pExtra,
    bool bPractice,
    bool bSpectate)
{
    SyncOfflineHostClientNickname(pThis, "CHostClient::Connect(pre)", true);

    wchar_t rawAddrW[128] = {0};
    wchar_t rewrittenAddrW[64] = {0};
    bool useRewrittenAddr = false;

    if (!IsDedicatedServerProcessCached() && pAddr != nullptr &&
        TryReadHostClientConnectAddrSeh(pAddr, rawAddrW, _countof(rawAddrW))) {
        uint16_t activeLocalPort = g_activeClientLocalSvrPort.load(std::memory_order_acquire);
        if (activeLocalPort == 0 && g_pfnICvarGetString != nullptr && wcschr(rawAddrW, L':') == nullptr) {
            K2WString k2SvrPortName(L"svr_port");
            K2WString outSvrPort;
            g_pfnICvarGetString(&outSvrPort, &k2SvrPortName);
            if (outSvrPort.size > 0 && outSvrPort.c_str()) {
                const int cvarPort = _wtoi(outSvrPort.c_str());
                if (cvarPort > 0 && cvarPort <= 65535 && cvarPort != 11235) {
                    activeLocalPort = static_cast<uint16_t>(cvarPort);
                }
            }
            if (outSvrPort.res > 7 && outSvrPort.ptr && g_pfnCMemManagerDeallocate) {
                g_pfnCMemManagerDeallocate(const_cast<wchar_t*>(outSvrPort.ptr));
                outSvrPort.ptr = nullptr;
                outSvrPort.res = 7;
            }
        }
        if (Kongor_RewriteBareLoopbackConnectTargetW(rawAddrW, activeLocalPort, rewrittenAddrW, _countof(rewrittenAddrW))) {
            useRewrittenAddr = true;
            char rewriteLog[256] = {0};
            snprintf(
                rewriteLog,
                sizeof(rewriteLog),
                "[KONGOR NATIVE GUARD] [CONNECT-REWRITE] Rewrote bare Connect target '%ls' -> '%ls' (bPractice=%d)",
                rawAddrW,
                rewrittenAddrW,
                bPractice ? 1 : 0);
            WriteSentinelLine(rewriteLog);
        } else {
            const wchar_t* colon = wcschr(rawAddrW, L':');
            if (colon != nullptr) {
                const int explicitPort = _wtoi(colon + 1);
                if (explicitPort > 0 && explicitPort <= 65535 &&
                    static_cast<uint16_t>(explicitPort) != activeLocalPort) {
                    g_activeClientLocalSvrPort.store(0, std::memory_order_release);
                }
            }
        }
    }

    if (g_origCHostClientConnect) {
        if (useRewrittenAddr) {
            K2StringW k2RewrittenAddr(rewrittenAddrW);
            g_origCHostClientConnect(pThis, &k2RewrittenAddr, bSilent, pLoadingInterface, pExtra, bPractice, bSpectate);
        } else {
            g_origCHostClientConnect(pThis, pAddr, bSilent, pLoadingInterface, pExtra, bPractice, bSpectate);
        }
    }
    SyncOfflineHostClientNickname(pThis, "CHostClient::Connect(post)", false);
}

static void ApplyPlayerUpgrades(void* pPlayer) {
    if (!pPlayer) return;

    // Guard A — Dedicated Server No-Op
    static int s_isDedicatedServer = -1;
    if (s_isDedicatedServer == -1) {
        const char* cmd = GetCommandLineA();
        if (cmd && (strstr(cmd, "-dedicated") != nullptr || strstr(cmd, "-server") != nullptr)) {
            s_isDedicatedServer = 1;
        } else {
            s_isDedicatedServer = 0;
        }
    }
    if (s_isDedicatedServer == 1) return;

    HMODULE hGameShared = GetModuleHandleW(L"game_shared_x64.dll");
    if (hGameShared) {
        if (!g_pfnPlayerIsBot) {
            g_pfnPlayerIsBot = reinterpret_cast<FnPlayerIsBot>(
                ResolveExport(hGameShared, "?IsBot@CPlayer@@QEBA_NXZ", 0x1b9e10));
        }
        if (!g_pfnLookupAvatarKey) {
            g_pfnLookupAvatarKey = reinterpret_cast<FnLookupAvatarKey>(
                ResolveExport(hGameShared, "?LookupAvatarKey@CEntityRegistry@@QEBAIAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z", 0x4a8350));
        }
        if (!g_ppEntityRegistry) {
            g_ppEntityRegistry = reinterpret_cast<void**>(
                GetProcAddress(hGameShared, "?s_pInstance@CEntityRegistry@@0PEAV1@EA"));
            if (!g_ppEntityRegistry) {
                g_ppEntityRegistry = reinterpret_cast<void**>((uint8_t*)hGameShared + 0x762de8);
            }
        }
    }

    // Guard B — Reject Bots & Connectionless Virtual Players (Ghidra-Verified)
    if (g_pfnPlayerIsBot && g_pfnPlayerIsBot(pPlayer)) {
        return;
    }
    if (*reinterpret_cast<const uint8_t*>(reinterpret_cast<const uint8_t*>(pPlayer) + 0x66b) != 0) {
        return;
    }
    if ((*reinterpret_cast<const uint16_t*>(reinterpret_cast<const uint8_t*>(pPlayer) + 0x3de) & 0x200) != 0) {
        return;
    }
    int32_t clientNum = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pPlayer) + 0xa8);
    if (clientNum < 0) {
        return;
    }

    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (hK2 && !g_ppHost) {
        g_ppHost = (void**)GetProcAddress(hK2, "?g_pHost@@3PEAVCHost@@EA");
    }
    if (!g_ppHost || !*g_ppHost) return;
    uint8_t* pHost = reinterpret_cast<uint8_t*>(*g_ppHost);

    // Verify pPlayer matches the local CHostClient::m_iClientNumber (+0x2c) when connected,
    // so remote human players in multiplayer matches never inherit local CVar overrides.
    uint8_t* pMapHead = *reinterpret_cast<uint8_t* const*>(pHost + 0xF0);
    uint64_t mapSize = *reinterpret_cast<const uint64_t*>(pHost + 0xF8);
    if (pMapHead && mapSize > 0 && mapSize <= 64) {
        uint8_t* pFirstNode = *reinterpret_cast<uint8_t* const*>(pMapHead);
        if (pFirstNode && pFirstNode != pMapHead) {
            uint8_t* pHostClient = *reinterpret_cast<uint8_t* const*>(pFirstNode + 0x28);
            if (pHostClient) {
                int32_t localClientNum = *reinterpret_cast<const int32_t*>(pHostClient + 0x2c);
                if (localClientNum >= 0 && clientNum != localClientNum) {
                    return;
                }
            }
        }
    }

    void* pUpgrades = pHost + 0x238;

    if (hK2) {
        if (!g_pfnLookupTaunt) {
            g_pfnLookupTaunt = reinterpret_cast<FnUpgradesLookup>(
                GetProcAddress(hK2, "?LookupTaunt@CUpgrades@@QEBAIAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z"));
        }
        if (!g_pfnLookupAnnouncerVoice) {
            g_pfnLookupAnnouncerVoice = reinterpret_cast<FnUpgradesLookup>(
                GetProcAddress(hK2, "?LookupAnnouncerVoice@CUpgrades@@QEBAIAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z"));
        }
        if (!g_pfnICvarGetString) {
            g_pfnICvarGetString = reinterpret_cast<FnICvarGetString>(
                ResolveExport(hK2, "?GetString@ICvar@@SA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@AEBV23@@Z", 0x85a8b0));
        }
        if (!g_pfnCMemManagerDeallocate) {
            g_pfnCMemManagerDeallocate = reinterpret_cast<FnCMemManagerDeallocate>(
                ResolveExport(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z", 0x2a4ab0));
        }
    }

    // Guard C — Throttled CVar Poll & Cached Upgrade Resolution + Transition-Only Logging
    uint64_t nowTick = GetTickCount64();
    static uint64_t s_lastCvarPollTick = 0;
    if (g_selectedUpgradeDirty.exchange(false, std::memory_order_acq_rel) || (nowTick - s_lastCvarPollTick >= 2000)) {
        s_lastCvarPollTick = nowTick;
        if (g_pfnICvarGetString) {
            {
                K2WString k2TauntCvarName(L"cg_selectedTaunt");
                K2WString outTaunt;
                g_pfnICvarGetString(&outTaunt, &k2TauntCvarName);
                if (outTaunt.size > 0 && outTaunt.c_str() && outTaunt.c_str()[0] != L'\0') {
                    std::wstring val = outTaunt.c_str();
                    if (val.rfind(L"t.", 0) == 0) val = val.substr(2);
                    if (!val.empty()) {
                        SetSelectedTauntSafe(val);
                    }
                }
                if (outTaunt.res > 7 && outTaunt.ptr && g_pfnCMemManagerDeallocate) {
                    g_pfnCMemManagerDeallocate((void*)outTaunt.ptr);
                }
            }
            {
                K2WString k2AnnouncerCvarName(L"cg_selectedAnnouncer");
                K2WString outAnnouncer;
                g_pfnICvarGetString(&outAnnouncer, &k2AnnouncerCvarName);
                if (outAnnouncer.size > 0 && outAnnouncer.c_str() && outAnnouncer.c_str()[0] != L'\0') {
                    std::wstring val = outAnnouncer.c_str();
                    if (val.rfind(L"av.", 0) == 0) val = val.substr(3);
                    if (!val.empty()) {
                        SetSelectedAnnouncerSafe(val);
                    }
                }
                if (outAnnouncer.res > 7 && outAnnouncer.ptr && g_pfnCMemManagerDeallocate) {
                    g_pfnCMemManagerDeallocate((void*)outAnnouncer.ptr);
                }
            }
        }
    }

    const std::wstring selectedTauntSnapshot = GetSelectedTauntSafe();
    const std::wstring selectedAnnouncerSnapshot = GetSelectedAnnouncerSafe();

    // Taunt lookup caching
    static std::wstring s_cachedTauntQuery;
    static uint8_t* s_cachedTauntHost = nullptr;
    static uint32_t s_cachedTauntIdx = 0xFFFFFFFF;
    static std::wstring s_cachedTauntModifier;
    static uint32_t s_cachedAvatarKey = 0xFFFFFFFF;

    bool needTauntLookup = (s_cachedTauntQuery != selectedTauntSnapshot) || (s_cachedTauntHost != pHost);
    bool needAvatarKeyRetry = (s_cachedAvatarKey == 0xFFFFFFFF && !s_cachedTauntModifier.empty() && g_ppEntityRegistry && *g_ppEntityRegistry && g_pfnLookupAvatarKey);

    if (needTauntLookup) {
        s_cachedTauntQuery = selectedTauntSnapshot;
        s_cachedTauntHost = pHost;
        s_cachedTauntIdx = 0xFFFFFFFF;
        s_cachedTauntModifier.clear();
        s_cachedAvatarKey = 0xFFFFFFFF;

        if (g_pfnLookupTaunt && !selectedTauntSnapshot.empty()) {
            K2WString k2Taunt(selectedTauntSnapshot.c_str());
            uint32_t idx = g_pfnLookupTaunt(pUpgrades, &k2Taunt);
            if (idx == 0xFFFFFFFF) {
                std::wstring fallbackTaunt = L"t." + selectedTauntSnapshot;
                K2WString k2TauntFb(fallbackTaunt.c_str());
                idx = g_pfnLookupTaunt(pUpgrades, &k2TauntFb);
            }
            s_cachedTauntIdx = idx;

            if (idx != 0xFFFFFFFF) {
                // Read taunt entry in the array at *(uintptr_t*)(pHost + 0x2e8) + (uintptr_t)idx * 0x48
                uintptr_t pArray = *reinterpret_cast<uintptr_t*>(pHost + 0x2e8);
                if (pArray) {
                    uintptr_t pEntry = pArray + static_cast<uintptr_t>(idx) * 0x48;
                    const void* pModStr = reinterpret_cast<const void*>(pEntry + 0x28);
                    const wchar_t* pChars = GetK2StringData(pModStr);
                    size_t len = GetK2StringLength(pModStr);
                    if (pChars && len > 0) {
                        s_cachedTauntModifier = std::wstring(pChars, len);
                    }
                }

                if (!s_cachedTauntModifier.empty() && g_pfnLookupAvatarKey && g_ppEntityRegistry && *g_ppEntityRegistry) {
                    std::wstring avatarKeyStr = L"Ability_Taunt." + s_cachedTauntModifier;
                    K2WString k2AvatarKey(avatarKeyStr.c_str());
                    s_cachedAvatarKey = g_pfnLookupAvatarKey(*g_ppEntityRegistry, &k2AvatarKey);
                }
            }
        }
    } else if (needAvatarKeyRetry) {
        std::wstring avatarKeyStr = L"Ability_Taunt." + s_cachedTauntModifier;
        K2WString k2AvatarKey(avatarKeyStr.c_str());
        s_cachedAvatarKey = g_pfnLookupAvatarKey(*g_ppEntityRegistry, &k2AvatarKey);
    }

    // Announcer lookup caching
    static std::wstring s_cachedAnnouncerQuery;
    static uint8_t* s_cachedAnnouncerHost = nullptr;
    static uint32_t s_cachedAnnouncerIdx = 0xFFFFFFFF;

    bool needAnnouncerLookup = (s_cachedAnnouncerQuery != selectedAnnouncerSnapshot) || (s_cachedAnnouncerHost != pHost);
    if (needAnnouncerLookup) {
        s_cachedAnnouncerQuery = selectedAnnouncerSnapshot;
        s_cachedAnnouncerHost = pHost;
        s_cachedAnnouncerIdx = 0xFFFFFFFF;

        if (g_pfnLookupAnnouncerVoice && !selectedAnnouncerSnapshot.empty()) {
            K2WString k2Announcer(selectedAnnouncerSnapshot.c_str());
            uint32_t idx = g_pfnLookupAnnouncerVoice(pUpgrades, &k2Announcer);
            if (idx == 0xFFFFFFFF) {
                std::wstring fallbackAnnouncer = L"av." + selectedAnnouncerSnapshot;
                K2WString k2AnnFb(fallbackAnnouncer.c_str());
                idx = g_pfnLookupAnnouncerVoice(pUpgrades, &k2AnnFb);
            }
            s_cachedAnnouncerIdx = idx;
        }
    }

    // Apply Taunt (+0x6dc) & m_uiTauntAvatarKey (+0xd8)
    uint32_t* pTaunt = reinterpret_cast<uint32_t*>((uint8_t*)pPlayer + 0x6dc);
    uint32_t* pTauntAvatarKey = reinterpret_cast<uint32_t*>((uint8_t*)pPlayer + 0xd8);
    uint32_t* pAnnouncer = reinterpret_cast<uint32_t*>((uint8_t*)pPlayer + 0x6d8);

    uint32_t prevTaunt = *pTaunt;
    uint32_t prevAvatarKey = *pTauntAvatarKey;
    uint32_t prevAnnouncer = *pAnnouncer;
    (void)prevTaunt;
    (void)prevAvatarKey;
    (void)prevAnnouncer;

    static void* s_lastLoggedTauntPlayer = nullptr;
    static uint32_t s_lastLoggedTauntIdx = 0xFFFFFFFF;
    static uint32_t s_lastLoggedAvatarKey = 0xFFFFFFFF;

    uint32_t idx = s_cachedTauntIdx;
    uint32_t avatarKey = s_cachedAvatarKey;

    *pTaunt = idx;
    *pTauntAvatarKey = avatarKey;

    if (idx != 0xFFFFFFFF) {
        if (s_lastLoggedTauntPlayer != pPlayer || s_lastLoggedTauntIdx != idx || s_lastLoggedAvatarKey != avatarKey) {
            s_lastLoggedTauntPlayer = pPlayer;
            s_lastLoggedTauntIdx = idx;
            s_lastLoggedAvatarKey = avatarKey;

            char dbg[256];
            snprintf(dbg, sizeof(dbg),
                     "[KONGOR NATIVE GUARD] Set CPlayer::m_uiTaunt = %u (%ls), modifier = '%ls', m_uiTauntAvatarKey = 0x%08X (+0xd8)",
                     idx, s_cachedTauntQuery.c_str(), s_cachedTauntModifier.c_str(), avatarKey);
            WriteSentinelLine(dbg);
        }
    }

    // Apply Announcer (+0x6d8)
    static void* s_lastLoggedAnnouncerPlayer = nullptr;
    static uint32_t s_lastLoggedAnnouncerIdx = 0xFFFFFFFF;

    uint32_t announcerIdx = s_cachedAnnouncerIdx;
    *pAnnouncer = announcerIdx;

    if (announcerIdx != 0xFFFFFFFF) {
        if (s_lastLoggedAnnouncerPlayer != pPlayer || s_lastLoggedAnnouncerIdx != announcerIdx) {
            s_lastLoggedAnnouncerPlayer = pPlayer;
            s_lastLoggedAnnouncerIdx = announcerIdx;

            char dbg[256];
            snprintf(dbg, sizeof(dbg),
                     "[KONGOR NATIVE GUARD] Set CPlayer::m_uiAnnouncerVoice = %u (%ls) (+0x6d8)",
                     announcerIdx, s_cachedAnnouncerQuery.c_str());
            WriteSentinelLine(dbg);
        }
    }
}

static void __fastcall Hooked_CPlayer_UpdateUpgrades(void* pPlayer) {
    if (g_origPlayerUpdateUpgrades) {
        g_origPlayerUpdateUpgrades(pPlayer);
    }
    ApplyPlayerUpgrades(pPlayer);
}

using PFN_CPlayer_InitializeConnectionless = void(__fastcall*)(void* pPlayer, void* pHostServer, int clientNum, const void* sName, bool bFakePlayer);
using PFN_CPlayer_Terminate = void(__fastcall*)(void* pPlayer);
using PFN_CPlayer_Disconnected = void(__fastcall*)(void* pPlayer, uint32_t time, uint32_t maxDiscTime);
using PFN_CPlayer_AttachBot = void*(__fastcall*)(void* pPlayer, const void* pBotDef);
static std::atomic<PFN_CPlayer_InitializeConnectionless> g_origInitializeConnectionless{nullptr};
static PFN_CPlayer_Terminate g_origPlayerTerminate = nullptr;
static PFN_CPlayer_Disconnected g_origPlayerDisconnected = nullptr;
static PFN_CPlayer_AttachBot g_origPlayerAttachBot = nullptr;
static std::atomic<HMODULE> g_lastHookedGameX64{nullptr};
static constexpr const char* kSymInitializeConnectionless =
    "?InitializeConnectionless@CPlayer@@QEAAXPEAVCHostServer@@HAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@_N@Z";
static constexpr const char* kSymPlayerSpawnHero = "?SpawnHero@CPlayer@@QEAA_NXZ";
static constexpr const char* kSymPlayerTerminate = "?Terminate@CPlayer@@QEAAXXZ";
static constexpr const char* kSymPlayerDisconnected = "?Disconnected@CPlayer@@QEAAXII@Z";
static constexpr const char* kSymPlayerAttachBot = "?AttachBot@CPlayer@@QEAAPEAVCBotBrain@@PEBVCBotDefinition@@@Z";

static void EnsureGameSharedPlayerHooksInstalled(HMODULE hMod, bool allowClientRvaFallback) noexcept;

static void EnsureConnectionlessPlayerActive(
    void* pPlayer, int clientNum, bool bFakePlayer, const char* sourceTag, bool alwaysLog) noexcept {
    if (!pPlayer) return;
    __try {
        uint16_t* pFlags = reinterpret_cast<uint16_t*>(reinterpret_cast<uintptr_t>(pPlayer) + 0x3DE);
        const uint16_t prevFlags = *pFlags;
        if (!bFakePlayer && (prevFlags & 0x0200u) == 0) {
            return;
        }
        const uint16_t nextFlags = static_cast<uint16_t>(prevFlags & ~static_cast<uint16_t>(0x0080u));
        *pFlags = nextFlags;
        // Disarm disconnect grace timer at CPlayer+0x438.
        *reinterpret_cast<uint32_t*>(reinterpret_cast<uintptr_t>(pPlayer) + 0x438) = 0xFFFFFFFFu;

        if (alwaysLog || (prevFlags != nextFlags)) {
            char logBuf[256];
            if (bFakePlayer) {
                snprintf(logBuf, sizeof(logBuf),
                    "[KONGOR NATIVE GUARD] [FAKEPLAYER] Active-client flag preserved for FakePlayer clientNum=%d (flags 0x%04X -> 0x%04X).",
                    clientNum, static_cast<unsigned>(prevFlags), static_cast<unsigned>(nextFlags));
            } else {
                snprintf(logBuf, sizeof(logBuf),
                    "[KONGOR NATIVE GUARD] [BOT-ECONOMY] Active-client flag preserved for Bot clientNum=%d via %s (flags 0x%04X -> 0x%04X).",
                    clientNum, sourceTag ? sourceTag : "init", static_cast<unsigned>(prevFlags), static_cast<unsigned>(nextFlags));
            }
            WriteSentinelLine(logBuf);
            OutputDebugStringA(logBuf);
            OutputDebugStringA("\n");
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught exception in EnsureConnectionlessPlayerActive.\n");
    }
}

static void ReapConnectionlessBotsIfNoActiveHumans() noexcept {
    // satisfies: OBL-QA-01
    if (Kongor::IsQueueArcadeModeActive()) {
        // Queue Arcade servers intentionally keep pre-allocated connectionless slots alive during 0-human intervals.
        return;
    }
    __try {
        void** ppGame = g_ppIGameInstance.load(std::memory_order_acquire);
        if (!ppGame) {
            HMODULE hGS = GetModuleHandleA("game_shared_x64.dll");
            if (hGS) {
                ppGame = reinterpret_cast<void**>(GetProcAddress(hGS, kSymIGameInstance));
                if (ppGame) {
                    g_ppIGameInstance.store(ppGame, std::memory_order_release);
                }
            }
        }
        if (!ppGame || !*ppGame) return;

        const uintptr_t pGame = reinterpret_cast<uintptr_t>(*ppGame);
        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (!head || *reinterpret_cast<const uint8_t*>(head + 0x19) == 0) {
            return;
        }

        void* botPlayers[64] = {};
        uint32_t botCount = 0;
        uint32_t activeHumans = 0;

        uintptr_t node = *reinterpret_cast<const uintptr_t*>(head + 0x00);
        for (uint32_t step = 0; step < 128 && node && node != head; ++step) {
            if (*reinterpret_cast<const uint8_t*>(node + 0x19) != 0) {
                break;
            }

            void* pEntryPlayer = *reinterpret_cast<void* const*>(node + 0x28);
            if (pEntryPlayer) {
                const uintptr_t entryAddr = reinterpret_cast<uintptr_t>(pEntryPlayer);
                const uint16_t flags = *reinterpret_cast<const uint16_t*>(entryAddr + 0x3DE);
                const uint8_t isBot = *reinterpret_cast<const uint8_t*>(entryAddr + 0x66B);
                const int32_t accId = *reinterpret_cast<const int32_t*>(entryAddr + 0x2D0);
                if ((flags & 0x0080u) == 0) {
                    // Loaded humans also have 0x0200 (PLAYER_FLAG_LOADED) set after NETCMD_FINISHED_LOADING_WORLD;
                    // detect active humans strictly via isBot == 0 && accountId > 0.
                    if (isBot == 0 && accId > 0) {
                        ++activeHumans;
                    } else if (botCount < 64) {
                        botPlayers[botCount++] = pEntryPlayer;
                    }
                }
            }

            const uintptr_t right = *reinterpret_cast<const uintptr_t*>(node + 0x10);
            if (right && *reinterpret_cast<const uint8_t*>(right + 0x19) == 0) {
                node = right;
                for (uint32_t sub = 0; sub < 64; ++sub) {
                    const uintptr_t left = *reinterpret_cast<const uintptr_t*>(node + 0x00);
                    if (!left || *reinterpret_cast<const uint8_t*>(left + 0x19) != 0) break;
                    node = left;
                }
            } else {
                uintptr_t parent = *reinterpret_cast<const uintptr_t*>(node + 0x08);
                for (uint32_t sub = 0; sub < 64 && parent && *reinterpret_cast<const uint8_t*>(parent + 0x19) == 0; ++sub) {
                    if (node != *reinterpret_cast<const uintptr_t*>(parent + 0x10)) break;
                    node = parent;
                    parent = *reinterpret_cast<const uintptr_t*>(parent + 0x08);
                }
                node = parent;
            }
        }

        if (activeHumans == 0 && botCount > 0) {
            for (uint32_t i = 0; i < botCount; ++i) {
                uint16_t* pBotFlags = reinterpret_cast<uint16_t*>(
                    reinterpret_cast<uintptr_t>(botPlayers[i]) + 0x3DE);
                *pBotFlags = static_cast<uint16_t>(*pBotFlags | 0x0080u);
            }
            char logBuf[256];
            snprintf(logBuf, sizeof(logBuf),
                "[KONGOR NATIVE GUARD] [BOT-ECONOMY] Last human terminated; marked %u bot(s) terminated for clean CGameServer::Frame shutdown.",
                botCount);
            WriteSentinelLine(logBuf);
            OutputDebugStringA(logBuf);
            OutputDebugStringA("\n");
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught exception in ReapConnectionlessBotsIfNoActiveHumans.\n");
    }
}

static void __fastcall Hooked_CPlayer_Terminate(void* pPlayer) {
    if (!g_origPlayerTerminate) return;
    __try {
        if (Kongor::OnArcadePlayerTerminate(pPlayer, nullptr)) {
            return;
        }
        // satisfies: OBL-QA-10
        if (pPlayer) {
            Kongor::ArcadeReleaseParkedBotOnPlayerTerminate(pPlayer, nullptr);
        }
        bool wasHuman = false;
        if (pPlayer) {
            const uintptr_t playerAddr = reinterpret_cast<uintptr_t>(pPlayer);
            const uint8_t preIsBot = *reinterpret_cast<const uint8_t*>(playerAddr + 0x66B);
            const int32_t preAccId = *reinterpret_cast<const int32_t*>(playerAddr + 0x2D0);
            wasHuman = (preIsBot == 0 && preAccId > 0);
        }
        g_origPlayerTerminate(pPlayer);
        if (wasHuman) {
            ReapConnectionlessBotsIfNoActiveHumans();
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught exception in Hooked_CPlayer_Terminate.\n");
    }
}

static void __fastcall Hooked_CPlayer_Disconnected(void* pPlayer, uint32_t time, uint32_t maxDiscTime) {
    if (!g_origPlayerDisconnected) return;
    __try {
        if (Kongor::OnArcadePlayerDisconnected(pPlayer, time, maxDiscTime, nullptr)) {
            return;
        }
        g_origPlayerDisconnected(pPlayer, time, maxDiscTime);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught exception in Hooked_CPlayer_Disconnected.\n");
    }
}

static void* __fastcall Hooked_CPlayer_AttachBot(void* pPlayer, const void* pBotDef) {
    if (!g_origPlayerAttachBot) return nullptr;
    void* pBrain = nullptr;
    __try {
        pBrain = g_origPlayerAttachBot(pPlayer, pBotDef);
        if (pPlayer && pBotDef) {
            Kongor::OnArcadePlayerAttachBot(pPlayer, pBotDef, nullptr);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught exception in Hooked_CPlayer_AttachBot.\n");
    }
    return pBrain;
}

static void __fastcall Hooked_CPlayer_InitializeConnectionless(
    void* pPlayer, void* pHostServer, int clientNum, const void* sName, bool bFakePlayer) {
    HMODULE hGS = GetModuleHandleA("game_shared_x64.dll");
    PFN_CPlayer_InitializeConnectionless orig = g_origInitializeConnectionless.load(std::memory_order_acquire);
    if (!orig && hGS) {
        orig = reinterpret_cast<PFN_CPlayer_InitializeConnectionless>(
            GetProcAddress(hGS, kSymInitializeConnectionless));
        if (orig) {
            g_origInitializeConnectionless.store(orig, std::memory_order_release);
        }
    }
    if (!orig) return;

    if (pHostServer) {
        Kongor::NotifyObservedArcadeHostServer(pHostServer);
    }
    if (hGS) {
        EnsureGameSharedPlayerHooksInstalled(hGS, false);
    }
    if (Kongor::IsQueueArcadeModeActive()) {
        Kongor::EnsureQueueArcadeK2HooksInstalled();
    }

    __try {
        orig(pPlayer, pHostServer, clientNum, sName, bFakePlayer);
        if (pPlayer && pHostServer) {
            // Clear PLAYER_FLAG_TERMINATED (0x0080) at CPlayer+0x3DE for BOTH FakePlayers (bFakePlayer=true)
            // and Bots (AddBot passes bFakePlayer=false!) so CTeamInfo::GetNumActiveClients counts all 10
            // match participants, restoring 2.5g/s Mid Wars passive gold and 850g starting gold on both teams.
            EnsureConnectionlessPlayerActive(pPlayer, clientNum, bFakePlayer, "InitializeConnectionless", true);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught exception in Hooked_CPlayer_InitializeConnectionless.\n");
    }
}

static bool TryPatchGameX64IatSeh(HMODULE hGame, bool* outAnyNewlyHooked) {
    if (outAnyNewlyHooked) *outAnyNewlyHooked = false;
    __try {
        HMODULE hGS = GetModuleHandleA("game_shared_x64.dll");
        if (hGS) {
            auto orig = reinterpret_cast<PFN_CPlayer_InitializeConnectionless>(
                GetProcAddress(hGS, kSymInitializeConnectionless));
            if (orig) {
                g_origInitializeConnectionless.store(orig, std::memory_order_release);
            }
        }

        bool hookedAvatar = false;
        HookImport(hGame, "game_shared_x64.dll",
            "?CanAccessAltAvatar@CPlayer@@QEAA_NAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@0@Z",
            reinterpret_cast<void*>(HookedCanAccessAltAvatar2), nullptr, &hookedAvatar);

        bool hookedFakePlayer = false;
        HookImport(hGame, "game_shared_x64.dll",
            kSymInitializeConnectionless,
            reinterpret_cast<void*>(Hooked_CPlayer_InitializeConnectionless), nullptr, &hookedFakePlayer);

        if (outAnyNewlyHooked) {
            *outAnyNewlyHooked = (hookedAvatar || hookedFakePlayer);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught SEH exception during TryPatchGameX64IatSeh (concurrent FreeLibrary).\n");
        return false;
    }
}

static void EnsureGameX64IatHooksInstalled() {
    HMODULE hGame = GetModuleHandleA("game_x64.dll");
    if (!hGame) {
        g_lastHookedGameX64.store(nullptr, std::memory_order_release);
        return;
    }

    static SRWLOCK s_gameX64IatLock = SRWLOCK_INIT;
    AcquireSRWLockExclusive(&s_gameX64IatLock);

    bool anyNewlyHooked = false;
    if (TryPatchGameX64IatSeh(hGame, &anyNewlyHooked) && anyNewlyHooked) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] Hooked game_x64.dll IAT for CanAccessAltAvatar + CPlayer::InitializeConnectionless (FakePlayer concede guard).");
        OutputDebugStringA("[KONGOR NATIVE GUARD] Hooked game_x64.dll IAT for CanAccessAltAvatar + CPlayer::InitializeConnectionless (FakePlayer concede guard).\n");
    }

    g_lastHookedGameX64.store(hGame, std::memory_order_release);
    ReleaseSRWLockExclusive(&s_gameX64IatLock);
}

static void __fastcall Hooked_CPlayer_SelectHero(void* pPlayer, uint16_t unHeroID) {
    EnsureGameX64IatHooksInstalled();
    ApplyPlayerUpgrades(pPlayer);
    if (g_origPlayerSelectHero) {
        g_origPlayerSelectHero(pPlayer, unHeroID);
    }
    ApplyPlayerUpgrades(pPlayer);
}

static bool __fastcall Hooked_CPlayer_SpawnHero(void* pPlayer) {
    // satisfies: OBL-QA-09
    EnsureGameX64IatHooksInstalled();
    if (pPlayer) {
        __try {
            const int32_t clientNum = *reinterpret_cast<const int32_t*>(
                reinterpret_cast<const uint8_t*>(pPlayer) + 0xA8);
            EnsureConnectionlessPlayerActive(pPlayer, clientNum, false, "SpawnHero", false);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            // Ignored - proceed to original SpawnHero
        }
    }
    bool spawned = true;
    if (!Kongor::ShouldSuppressPreMatchDuplicateHeroSpawn(pPlayer, nullptr)) {
        ApplyPlayerUpgrades(pPlayer);
        if (g_origPlayerSpawnHero) {
            spawned = g_origPlayerSpawnHero(pPlayer);
        }
    }
    RefreshServerKeyframePlayersFromEngine();
    if (Kongor::GetQueueArcadeMode() == Kongor::QueueArcadeServerMode::PartyDormantSlots) {
        Kongor::ArcadeParkUnreservedBots(nullptr);
    }
    return spawned;
}

static void EnsureGameSharedPlayerHooksInstalled(HMODULE hMod, bool allowClientRvaFallback) noexcept {
    if (!hMod) return;
    static std::atomic<HMODULE> s_lastPlayerHookedMod{nullptr};
    if (s_lastPlayerHookedMod.load(std::memory_order_acquire) == hMod) {
        return;
    }

    static SRWLOCK s_playerHooksLock = SRWLOCK_INIT;
    AcquireSRWLockExclusive(&s_playerHooksLock);
    if (s_lastPlayerHookedMod.load(std::memory_order_acquire) != hMod) {
        uint8_t* pBase = reinterpret_cast<uint8_t*>(hMod);

        void* pSpawnHero = reinterpret_cast<void*>(GetProcAddress(hMod, kSymPlayerSpawnHero));
        if (!pSpawnHero && allowClientRvaFallback) {
            pSpawnHero = reinterpret_cast<void*>(pBase + 0x4f1b50);
        }
        if (pSpawnHero) {
            static const uint8_t patSpawnHeroOrig15[] = {
                0x48, 0x89, 0x4C, 0x24, 0x08, 0x55, 0x53, 0x56,
                0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56
            };
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CPlayer::SpawnHero", pSpawnHero,
                    reinterpret_cast<void*>(Hooked_CPlayer_SpawnHero),
                    reinterpret_cast<void**>(&g_origPlayerSpawnHero), 15,
                    patSpawnHeroOrig15, sizeof(patSpawnHeroOrig15))) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::SpawnHero (client+server).\n");
            }
        }

        void* pTerminate = reinterpret_cast<void*>(GetProcAddress(hMod, kSymPlayerTerminate));
        if (pTerminate) {
            static const uint8_t patTerminateOrig15[] = {
                0x48, 0x8B, 0xC4, 0x48, 0x89, 0x48, 0x08, 0x55,
                0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55
            };
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CPlayer::Terminate", pTerminate,
                    reinterpret_cast<void*>(Hooked_CPlayer_Terminate),
                    reinterpret_cast<void**>(&g_origPlayerTerminate), 15,
                    patTerminateOrig15, sizeof(patTerminateOrig15))) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::Terminate (empty-match bot reaper + Queue Arcade active-phase guard).\n");
            }
        }

        void* pDisconnected = reinterpret_cast<void*>(GetProcAddress(hMod, kSymPlayerDisconnected));
        if (pDisconnected) {
            // Verified 15-byte prologue on both server (0x4F40E0) and client (0x4F33B0) game_shared_x64.dll:
            //   48 89 5C 24 08    MOV [RSP+0x08], RBX
            //   48 89 74 24 10    MOV [RSP+0x10], RSI
            //   57                PUSH RDI
            //   48 83 EC 20       SUB RSP, 0x20
            static const uint8_t patDisconnectedOrig15[] = {
                0x48, 0x89, 0x5C, 0x24, 0x08,
                0x48, 0x89, 0x74, 0x24, 0x10,
                0x57,
                0x48, 0x83, 0xEC, 0x20
            };
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CPlayer::Disconnected", pDisconnected,
                    reinterpret_cast<void*>(Hooked_CPlayer_Disconnected),
                    reinterpret_cast<void**>(&g_origPlayerDisconnected), 15,
                    patDisconnectedOrig15, sizeof(patDisconnectedOrig15))) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::Disconnected (Queue Arcade human<->bot disconnect handoff).\n");
            }
        }

        void* pAttachBot = reinterpret_cast<void*>(GetProcAddress(hMod, kSymPlayerAttachBot));
        if (pAttachBot) {
            // Verified 16-byte prologue on both server (0x4F7050) and client (0x4F6320) game_shared_x64.dll:
            //   48 89 5C 24 08    MOV [RSP+0x08], RBX
            //   55 56 57          PUSH RBP; PUSH RSI; PUSH RDI
            //   41 54 41 55 41 56 41 57  PUSH R12..R15
            static const uint8_t patAttachBotOrig16[] = {
                0x48, 0x89, 0x5C, 0x24, 0x08,
                0x55, 0x56, 0x57,
                0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57
            };
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CPlayer::AttachBot", pAttachBot,
                    reinterpret_cast<void*>(Hooked_CPlayer_AttachBot),
                    reinterpret_cast<void**>(&g_origPlayerAttachBot), 16,
                    patAttachBotOrig16, sizeof(patAttachBotOrig16))) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::AttachBot (Queue Arcade per-slot bot origin recorder).\n");
            }
        }

        s_lastPlayerHookedMod.store(hMod, std::memory_order_release);
    }
    ReleaseSRWLockExclusive(&s_playerHooksLock);
}

static void __fastcall Hooked_CResourceManager_UnregisterFileChangeCallback(void* pThis, void* pCallback) {
    if (!pCallback) return;
    __try {
        if (g_origUnregFileChangeCallback) {
            g_origUnregFileChangeCallback(pThis, pCallback);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Caught and neutralized crash in CResourceManager::UnregisterFileChangeCallback.\n");
    }
}

static bool __fastcall Hooked_CClientAccount_CanAccessGameMode(const void* /*pThis*/, const void* /*sGameMode*/) {
    return true;
}

// satisfies: OBL-FOW-01
// satisfies: OBL-FOW-02
typedef void (__fastcall *FnCVisibilityMap_FillBitmap)(void* pThis, void* pBitmap, uint8_t u1, uint8_t u2);
static FnCVisibilityMap_FillBitmap g_origVisibilityMapFillBitmap = nullptr;
static uint32_t g_lastFowMapWidth = 0;
static uint32_t g_lastFowMapHeight = 0;

static uint8_t ClampInvertedDarknessLutByte(float darknessValue) noexcept {
    int scaled = static_cast<int>(darknessValue * 255.0f + 0.5f);
    if (scaled < 0) scaled = 0;
    else if (scaled > 255) scaled = 255;
    return static_cast<uint8_t>(~static_cast<uint8_t>(scaled));
}

// MSVC C2712 compliant: zero C++ destructor objects inside __try frame!
static void __fastcall Hooked_CVisibilityMap_FillBitmap(void* pThis, void* pBitmap, uint8_t u1, uint8_t u2) {
    if (!pThis || !pBitmap) return;

    __try {
        const void* const pCells = *reinterpret_cast<const void* const*>(reinterpret_cast<const uint8_t*>(pThis) + 8);
        const void* const pBmpPixels = *reinterpret_cast<const void* const*>(pBitmap);
        const int32_t mapW = *reinterpret_cast<const int32_t*>(pThis);
        const int32_t mapH = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pThis) + 4);
        const int32_t bmpW = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pBitmap) + 0x0C);
        const int32_t bmpH = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pBitmap) + 0x10);
        const int32_t bmpType = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pBitmap) + 0x14);

        if (!pCells || !pBmpPixels || mapW <= 0 || mapH <= 0 || bmpW != mapW || bmpH != mapH || bmpType != 1) {
            return;
        }

        const uint32_t currentWidth = static_cast<uint32_t>(mapW);
        const uint32_t currentHeight = static_cast<uint32_t>(mapH);
        const uint32_t halfW = currentWidth >> 1;
        const uint32_t halfH = currentHeight >> 1;

        const uintptr_t pBase = reinterpret_cast<uintptr_t>(GetModuleHandleA("game_shared_x64.dll"));
        if (pBase) {
            uint8_t*  pMod0        = reinterpret_cast<uint8_t*>(pBase + 0x77d7b4);
            uint8_t*  pMod1        = reinterpret_cast<uint8_t*>(pBase + 0x77d994);
            uint8_t*  pMod2        = reinterpret_cast<uint8_t*>(pBase + 0x77db74);
            const float* pDarkMin  = reinterpret_cast<const float*>(pBase + 0x77d93c);
            const float* pDarkMax  = reinterpret_cast<const float*>(pBase + 0x77db1c);
            const int32_t* pRingCv = reinterpret_cast<const int32_t*>(pBase + 0x77dcfc);
            uintptr_t* pBmpBufPtr  = reinterpret_cast<uintptr_t*>(pBase + 0x77dee0);
            int32_t*  pRingSize    = reinterpret_cast<int32_t*>(pBase + 0x77dee8);
            int32_t*  pBorderInset = reinterpret_cast<int32_t*>(pBase + 0x77deec);
            uint32_t* pStaticHalfW = reinterpret_cast<uint32_t*>(pBase + 0x77def0);
            uint32_t* pStaticHalfH = reinterpret_cast<uint32_t*>(pBase + 0x77def4);
            uint8_t*  pLutMin      = reinterpret_cast<uint8_t*>(pBase + 0x77def8);
            uint8_t*  pLutMax      = reinterpret_cast<uint8_t*>(pBase + 0x77def9);
            uint8_t*  pLutVis      = reinterpret_cast<uint8_t*>(pBase + 0x77defa);

            if (halfW > 255U || halfH > 255U) {
                // Giant maps (e.g. 512x512 -> halfW=256): native FillBitmap's CVar init block
                // truncates (*(uint*)this >> 1) & 0xFF to 0! Compute the FoW v2 LUT/ring globals
                // directly, clear all 3 m_bModified flags so FillBitmap skips its 8-bit truncation,
                // and write full 32-bit half-dimensions to 0x77def0 / 0x77def4.
                if (*pRingSize <= 0 || *pMod0 != 0 || *pMod1 != 0 || *pMod2 != 0 ||
                    *pStaticHalfW != halfW || *pStaticHalfH != halfH ||
                    currentWidth != g_lastFowMapWidth || currentHeight != g_lastFowMapHeight) {
                    const uint32_t prevW = g_lastFowMapWidth;
                    const uint32_t prevH = g_lastFowMapHeight;
                    *pMod0 = 0;
                    *pMod1 = 0;
                    *pMod2 = 0;
                    *pLutMin = ClampInvertedDarknessLutByte(*pDarkMin);
                    *pLutMax = ClampInvertedDarknessLutByte(*pDarkMax);
                    *pLutVis = 0xFF;
                    *pBorderInset = 1;
                    *pBmpBufPtr = 0;
                    const int32_t computedRing = (*pRingCv >> 1);
                    *pRingSize = (computedRing > 0) ? computedRing : 1;
                    *pStaticHalfW = halfW;
                    *pStaticHalfH = halfH;
                    g_lastFowMapWidth = currentWidth;
                    g_lastFowMapHeight = currentHeight;

                    char fowLog[256] = {0};
                    snprintf(
                        fowLog,
                        sizeof(fowLog),
                        "[KONGOR NATIVE GUARD] [FOW_GUARD] Giant-map 32-bit FoW half-dimensions applied (%ux%u -> %ux%u, half=%ux%u, ring=%d)",
                        prevW, prevH, currentWidth, currentHeight, halfW, halfH, static_cast<int>(*pRingSize));
                    WriteSentinelLine(fowLog);
                }
            } else if (*pStaticHalfW != halfW || *pStaticHalfH != halfH ||
                       currentWidth != g_lastFowMapWidth || currentHeight != g_lastFowMapHeight) {
                // Standard map transition (e.g. MidWars 256x256 -> Pebbles Duel 128x128):
                // Mark cg_fogofwarRingSize.m_bModified = 1 (0x77d7b4) so FillBitmap enters its
                // native CVar init block and recomputes 0x77def0 (halfW) and 0x77def4 (halfH).
                const uint32_t prevStaticHalfW = *pStaticHalfW;
                const uint32_t prevStaticHalfH = *pStaticHalfH;
                *pMod0 = 1;
                if (*pRingCv <= 1) {
                    // Guard against divide-by-zero in FUN_18053bd40 if cg_fogofwarRingSize was set <= 1
                    *const_cast<int32_t*>(pRingCv) = 2;
                }
                g_lastFowMapWidth = currentWidth;
                g_lastFowMapHeight = currentHeight;

                char fowLog[256] = {0};
                snprintf(
                    fowLog,
                    sizeof(fowLog),
                    "[KONGOR NATIVE GUARD] [FOW_GUARD] Invalidated stale FoW static half-dimensions (%ux%u -> %ux%u, map=%ux%u)",
                    prevStaticHalfW, prevStaticHalfH, halfW, halfH, currentWidth, currentHeight);
                WriteSentinelLine(fowLog);
            }
        }

        if (g_origVisibilityMapFillBitmap) {
            g_origVisibilityMapFillBitmap(pThis, pBitmap, u1, u2);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Trapped access violation in CVisibilityMap::FillBitmap (neutralized OOB write).\n");
    }
}

typedef uint32_t (__fastcall *FnIHeroEntity_GetDeathGoldCost)(void* pThis, bool bIsDeath);
static FnIHeroEntity_GetDeathGoldCost g_origHeroGetDeathGoldCost = nullptr;

static bool IsMapStemDevoWarsA(const char* rawName) noexcept {
    if (!rawName || !*rawName) return false;
    const char* stem = rawName;
    for (const char* p = rawName; *p; ++p) {
        if (*p == '/' || *p == '\\') stem = p + 1;
    }
    return (_stricmp(stem, "devowars") == 0 ||
            _stricmp(stem, "devowars_10v10") == 0 ||
            _stricmp(stem, "devowars.s2z") == 0 ||
            _stricmp(stem, "devowars_10v10.s2z") == 0);
}

static bool IsMapStemDevoWarsW(const wchar_t* rawName, size_t len) noexcept {
    if (!rawName || len == 0 || len >= 260) return false;
    wchar_t buf[264] = {0};
    for (size_t i = 0; i < len && rawName[i] != L'\0'; ++i) {
        buf[i] = rawName[i];
    }
    const wchar_t* stem = buf;
    for (const wchar_t* p = buf; *p; ++p) {
        if (*p == L'/' || *p == L'\\') stem = p + 1;
    }
    return (_wcsicmp(stem, L"devowars") == 0 ||
            _wcsicmp(stem, L"devowars_10v10") == 0 ||
            _wcsicmp(stem, L"devowars.s2z") == 0 ||
            _wcsicmp(stem, L"devowars_10v10.s2z") == 0);
}

static bool TryCheckDevoWarsMapSEH(char* outMapName, size_t outMapCap) noexcept {
    if (outMapName && outMapCap > 0) outMapName[0] = '\0';
    __try {
        void** ppGame = g_ppIGameInstance.load(std::memory_order_acquire);
        if (ppGame && *ppGame) {
            const uint8_t* pGame = reinterpret_cast<const uint8_t*>(*ppGame);
            const uint8_t* pWorld = *reinterpret_cast<const uint8_t* const*>(pGame + 0x20);
            if (pWorld) {
                const size_t nameLen = *reinterpret_cast<const size_t*>(pWorld + 0x28);
                const size_t nameCap = *reinterpret_cast<const size_t*>(pWorld + 0x30);
                const wchar_t* pName = (nameCap > 7)
                    ? *reinterpret_cast<const wchar_t* const*>(pWorld + 0x18)
                    : reinterpret_cast<const wchar_t*>(pWorld + 0x18);
                if (pName && nameLen > 0 && nameLen < 260) {
                    if (outMapName && outMapCap > 0) {
                        for (size_t i = 0; i < nameLen && i + 1 < outMapCap && pName[i] != L'\0'; ++i) {
                            outMapName[i] = (pName[i] < 128) ? static_cast<char>(pName[i]) : '?';
                            outMapName[i + 1] = '\0';
                        }
                    }
                    if (IsMapStemDevoWarsW(pName, nameLen)) {
                        return true;
                    }
                }
            }
        }
        if (g_pTelemetryHeader && g_pTelemetryHeader->CurrentMap[0] != '\0') {
            if (outMapName && outMapCap > 0 && outMapName[0] == '\0') {
                snprintf(outMapName, outMapCap, "%s", g_pTelemetryHeader->CurrentMap);
            }
            if (IsMapStemDevoWarsA(g_pTelemetryHeader->CurrentMap)) {
                return true;
            }
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool TryTransferReliableGoldForDevoDeathSEH(
    void* pHero,
    uint32_t deathCost,
    int32_t* outClientNum,
    uint16_t* outRelBefore,
    uint16_t* outUnrelBefore,
    uint16_t* outMoved,
    uint16_t* outRelAfter,
    uint16_t* outUnrelAfter) noexcept
{
    __try {
        void** ppGame = g_ppIGameInstance.load(std::memory_order_acquire);
        if (!ppGame || !*ppGame || !pHero) return false;
        const uint8_t* pGame = reinterpret_cast<const uint8_t*>(*ppGame);
        uint8_t* const* pTreeHead = *reinterpret_cast<uint8_t* const* const*>(pGame + 0x1e8);
        if (!pTreeHead) return false;

        const int32_t targetClientNum = *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(pHero) + 0x6d4);
        uint8_t* node = pTreeHead[1];
        uint8_t* best = reinterpret_cast<uint8_t*>(const_cast<uint8_t**>(pTreeHead));
        int guard = 256;
        while (node && *reinterpret_cast<const char*>(node + 0x19) == 0 && --guard > 0) {
            if (*reinterpret_cast<const int32_t*>(node + 0x20) < targetClientNum) {
                node = *reinterpret_cast<uint8_t* const*>(node + 0x10);
            } else {
                best = node;
                node = *reinterpret_cast<uint8_t* const*>(node);
            }
        }
        if (!best || best == reinterpret_cast<uint8_t*>(const_cast<uint8_t**>(pTreeHead)) ||
            *reinterpret_cast<const char*>(best + 0x19) != 0 ||
            *reinterpret_cast<const int32_t*>(best + 0x20) > targetClientNum) {
            return false;
        }

        uint8_t* pPlayer = *reinterpret_cast<uint8_t* const*>(best + 0x28);
        if (!pPlayer) return false;

        uint16_t* pRel = reinterpret_cast<uint16_t*>(pPlayer + 0x402);
        uint16_t* pUnrel = reinterpret_cast<uint16_t*>(pPlayer + 0x404);
        const uint16_t relBefore = *pRel;
        const uint16_t unrelBefore = *pUnrel;
        *outClientNum = targetClientNum;
        *outRelBefore = relBefore;
        *outUnrelBefore = unrelBefore;

        uint16_t moved = 0;
        if (static_cast<uint32_t>(unrelBefore) < deathCost && relBefore > 0) {
            const uint32_t deficit = deathCost - static_cast<uint32_t>(unrelBefore);
            moved = static_cast<uint16_t>(
                (deficit < static_cast<uint32_t>(relBefore)) ? deficit : static_cast<uint32_t>(relBefore));
            *pRel = static_cast<uint16_t>(relBefore - moved);
            *pUnrel = static_cast<uint16_t>(unrelBefore + moved);
        }
        *outMoved = moved;
        *outRelAfter = *pRel;
        *outUnrelAfter = *pUnrel;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// MSVC C2712 compliant: zero C++ objects inside SEH frame
static uint32_t __fastcall Hooked_IHeroEntity_GetDeathGoldCost(void* pThis, bool bIsDeath) {
    uint32_t cost = g_origHeroGetDeathGoldCost ? g_origHeroGetDeathGoldCost(pThis, bIsDeath) : 0U;
    if (!bIsDeath || cost == 0U || !pThis) {
        return cost;
    }

    char mapName[64] = {0};
    if (!TryCheckDevoWarsMapSEH(mapName, sizeof(mapName))) {
        return cost;
    }

    int32_t clientNum = -1;
    uint16_t relBefore = 0;
    uint16_t unrelBefore = 0;
    uint16_t moved = 0;
    uint16_t relAfter = 0;
    uint16_t unrelAfter = 0;
    if (TryTransferReliableGoldForDevoDeathSEH(
            pThis, cost, &clientNum, &relBefore, &unrelBefore, &moved, &relAfter, &unrelAfter)) {
        char goldLog[256] = {0};
        snprintf(
            goldLog,
            sizeof(goldLog),
            "[KONGOR NATIVE GUARD] [DEVO_GOLD] map=%s client=%d cost=%u moved=%u reliable=%u->%u unreliable=%u->%u",
            mapName[0] ? mapName : "devowars",
            static_cast<int>(clientNum),
            static_cast<unsigned int>(cost),
            static_cast<unsigned int>(moved),
            static_cast<unsigned int>(relBefore),
            static_cast<unsigned int>(relAfter),
            static_cast<unsigned int>(unrelBefore),
            static_cast<unsigned int>(unrelAfter));
        WriteSentinelLine(goldLog);
        OutputDebugStringA(goldLog);
        OutputDebugStringA("\n");
    }
    return cost;
}

static bool __fastcall Hooked_CClientConnection_CanAccessGameMode(const void* /*pThis*/, const void* /*sGameMode*/) {
    return true;
}

typedef void* (__fastcall *FnCSoundManagerLoadSample)(void* pThis, const void* pK2StringW, int iFlags);
static FnCSoundManagerLoadSample g_origCSoundManagerLoadSample = nullptr;
static std::atomic<uint32_t> g_soundNegCacheLoggedMisses{0};

static bool IsSoundNegCacheDisabled() noexcept {
    static int s_disabled = -1;
    if (s_disabled == -1) {
        char envBuf[16] = {0};
        if (GetEnvironmentVariableA("KONGOR_DISABLE_SOUND_NEGCACHE", envBuf, sizeof(envBuf)) > 0 &&
            (envBuf[0] == '1' || envBuf[0] == 'y' || envBuf[0] == 'Y')) {
            s_disabled = 1;
        } else {
            s_disabled = 0;
        }
    }
    return (s_disabled == 1);
}

static void LogNewNegativeCachedSoundPath(const wchar_t* normPathW, int32_t iFlags) noexcept {
    const uint32_t n = g_soundNegCacheLoggedMisses.fetch_add(1, std::memory_order_relaxed);
    if (n < 16 && normPathW) {
        char msg[320] = {0};
        snprintf(msg, sizeof(msg),
                 "[KONGOR NATIVE GUARD] [SOUND-NEGCACHE] Cached missing sample (#%u, flags=%d): %ls",
                 static_cast<unsigned int>(n + 1), static_cast<int>(iFlags), normPathW);
        WriteSentinelLine(msg);
    }
}

// MSVC C2712 compliant: zero C++ destructor objects in __try frame!
static void* __fastcall Hooked_CSoundManager_LoadSample(void* pThis, const void* pK2StringW, int iFlags) {
    // satisfies: OBL-NG-LOADSAMPLE-01
    if (!g_origCSoundManagerLoadSample) {
        return nullptr;
    }
    if (!pThis || !pK2StringW || IsSoundNegCacheDisabled()) {
        return g_origCSoundManagerLoadSample(pThis, pK2StringW, iFlags);
    }

    wchar_t normPathW[Kongor_SoundNegCacheTable::kMaxPathW] = {0};
    uint64_t pathHash = 0;
    bool cacheable = false;

    Kongor_EnterProtectedSeh();
    __try {
        const wchar_t* rawW = GetK2StringData(pK2StringW);
        if (rawW && rawW[0] != L'\0') {
            cacheable = Kongor_NormalizeSoundCachePathW(
                rawW, Kongor_SoundNegCacheTable::kMaxPathW - 1, iFlags,
                normPathW, Kongor_SoundNegCacheTable::kMaxPathW, &pathHash);
        }
        if (cacheable && g_soundNegCache.Lookup(pathHash, iFlags, normPathW)) {
            Kongor_LeaveProtectedSeh();
            return nullptr;
        }

        void* pSample = g_origCSoundManagerLoadSample(pThis, pK2StringW, iFlags);
        if (!pSample && cacheable) {
            if (g_soundNegCache.InsertIfInitialized(pThis, pathHash, iFlags, normPathW)) {
                LogNewNegativeCachedSoundPath(normPathW, iFlags);
            }
        }
        Kongor_LeaveProtectedSeh();
        return pSample;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Kongor_LeaveProtectedSeh();
        OutputDebugStringA("[KONGOR NATIVE GUARD] [SEH] Intercepted fault inside Hooked_CSoundManager_LoadSample; returning nullptr.\n");
        return nullptr;
    }
}

typedef bool (__fastcall *FnCServerListReconnectStatus)(void* pServerList, const void* pServerAddr, void* pPacket);
typedef int  (__fastcall *FnCPacketReadInt)(void* pPacket, int iDefault);
static FnCServerListReconnectStatus g_origCServerListReconnectStatus = nullptr;
static FnCPacketReadInt g_pfnCPacketReadInt = nullptr;
static std::atomic<uint64_t> g_reconnect6fDrainedCount{0};

static bool __fastcall Hooked_CServerList_ReconnectStatus(void* pServerList, const void* pServerAddr, void* pPacket) {
    // satisfies: OBL-NG-RECONNECT6F-01
    if (!g_origCServerListReconnectStatus) {
        return false;
    }

    Kongor_EnterProtectedSeh();
    __try {
        const bool ok = g_origCServerListReconnectStatus(pServerList, pServerAddr, pPacket);
        if (ok && pPacket && g_pfnCPacketReadInt && Kongor_ShouldDrainReconnect6fTrailingInt(pPacket)) {
            const int remainingReconnectMs = g_pfnCPacketReadInt(pPacket, 0);
            const uint64_t drainIdx = g_reconnect6fDrainedCount.fetch_add(1, std::memory_order_relaxed) + 1;
            if (drainIdx <= 8) {
                char logBuf[256] = {0};
                snprintf(logBuf, sizeof(logBuf),
                         "[KONGOR NATIVE GUARD] [NET-0x6F] Drained 4-byte remainingReconnectMs=%d from 9-byte NETCMD_RECONNECT_STATUS_RESPONSE (#%llu).",
                         remainingReconnectMs, static_cast<unsigned long long>(drainIdx));
                WriteSentinelLine(logBuf);
            }
        }
        Kongor_LeaveProtectedSeh();
        return ok;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Kongor_LeaveProtectedSeh();
        OutputDebugStringA("[KONGOR NATIVE GUARD] [SEH] Intercepted fault inside Hooked_CServerList_ReconnectStatus.\n");
        return false;
    }
}

typedef void (__fastcall *FnCSystemError)(void* pThis, const K2StringW* pMsg1, const K2StringW* pMsg2, bool bFlag);
typedef void (__fastcall *FnCSystemExit)(void* pThis, int exitCode);
typedef void (__fastcall *FnCHostShutdown)(void* pThis);

static FnCSystemError  g_origCSystemError  = nullptr;
static FnCSystemExit   g_origCSystemExit   = nullptr;
static FnCHostShutdown g_origCHostShutdown = nullptr;
static std::atomic<bool> g_chostShutdownHookInstalled{ false };

static void InvokeOverlayPrepareProcessExitSeh(const char* callerTag) noexcept {
    __try {
        HMODULE hLayer = ::GetModuleHandleW(L"VkLayer_hon_replay_capture.dll");
        if (!hLayer) {
            return;
        }
        using FnPrepareProcessExit = void (WINAPI *)(void);
        auto pfnPrepare = reinterpret_cast<FnPrepareProcessExit>(
            ::GetProcAddress(hLayer, "Kongor_Overlay_PrepareProcessExit"));
        if (!pfnPrepare) {
            return;
        }
        char msg[224] = {0};
        snprintf(msg, sizeof(msg),
                 "[KONGOR NATIVE GUARD] [EXIT-HOOK] Invoking VkLayer_hon_replay_capture!Kongor_Overlay_PrepareProcessExit (%s).",
                 callerTag ? callerTag : "unknown");
        WriteSentinelLineNoAllocBestEffort(msg);
        pfnPrepare();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        WriteSentinelLineNoAllocBestEffort(
            "[KONGOR NATIVE GUARD] [EXIT-HOOK] [WARN] Exception caught inside Kongor_Overlay_PrepareProcessExit.");
    }
}

static void __fastcall Hooked_CHost_Shutdown(void* pThis) {
    if (g_origCHostShutdown) {
        g_origCHostShutdown(pThis);
    }

    // Phase-2 Clean Exit promotion: only write clean-exit.<pid>.sentinel AFTER
    // CWebResourceManager::Shutdown, CVid::Shutdown, and CHost::Shutdown have ALL returned cleanly!
    if (g_quitInitiated.load(std::memory_order_acquire) &&
        !g_csystemErrorTriggered.load(std::memory_order_acquire)) {
        MarkCleanUserExit("CHost::Shutdown_Completed");
        WriteSentinelLineNoAllocBestEffort(
            "[KONGOR NATIVE GUARD] [EXIT-HOOK] CHost::Shutdown completed cleanly; promoted quit-initiated -> clean-exit.");
        FlushSentinelLogs(true);
    }
}

static void ExtractK2StringUtf8NoAlloc(const K2StringW* pStr, char* outBuf, size_t outCap) noexcept {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = '\0';
    if (!pStr) return;
    __try {
        const size_t len = pStr->length;
        if (len == 0 || len > 4096) return;
        const wchar_t* wptr = (pStr->capacity < 8) ? pStr->bx.buf : pStr->bx.ptr;
        if (!wptr) return;
        int n = ::WideCharToMultiByte(
            CP_UTF8, 0, wptr, static_cast<int>((std::min)(len, static_cast<size_t>(384))),
            outBuf, static_cast<int>(outCap - 1), nullptr, nullptr);
        if (n > 0 && static_cast<size_t>(n) < outCap) {
            outBuf[n] = '\0';
        } else {
            outBuf[0] = '\0';
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        outBuf[0] = '\0';
    }
}

static void __fastcall Hooked_CSystem_Error(void* pThis, const K2StringW* pMsg1, const K2StringW* pMsg2, bool bFlag) {
    g_csystemErrorTriggered.store(true, std::memory_order_release);

    char msg1Utf8[256] = {0};
    char msg2Utf8[256] = {0};
    char stackBuf[512] = {0};
    ExtractK2StringUtf8NoAlloc(pMsg1, msg1Utf8, sizeof(msg1Utf8));
    ExtractK2StringUtf8NoAlloc(pMsg2, msg2Utf8, sizeof(msg2Utf8));
    FormatStackBackTraceRvasNoAlloc(stackBuf, sizeof(stackBuf), 0);

    char detailBuf[540] = {0};
    snprintf(detailBuf, sizeof(detailBuf), "msg1='%s' msg2='%s' flag=%d",
             msg1Utf8, msg2Utf8, bFlag ? 1 : 0);
    WritePerPidCrashSentinelNoAlloc("CSystem::Error", -1, detailBuf, stackBuf);

    char sentinelLog[960] = {0};
    snprintf(sentinelLog, sizeof(sentinelLog),
             "[KONGOR NATIVE GUARD] [EXIT-HOOK] [FATAL] CSystem::Error intercepted (%s) | stack=%s",
             detailBuf, stackBuf);
    WriteSentinelLine(sentinelLog);
    FlushSentinelLogs(true);

    if (g_origCSystemError) {
        g_origCSystemError(pThis, pMsg1, pMsg2, bFlag);
    }
}

static bool IsKnownCleanUserQuitCallerRva(uintptr_t callerRva) noexcept {
    // Verified via Ghidra against k2_x64.dll xrefs to CSystem::Exit (0x1809fdb90):
    //   - CSystem::HandleOSMessages WM_QUIT (0x180a001fc -> RVA 0xA001FC)
    //   - Console 'quit'/'exit' handler     (0x18092cf5d -> RVA 0x92CF5D)
    //   - UICmd Quit / Restart handlers     (RVAs 0xC011F2..0xC0126B, 0xC5957B..0xC595BB)
    return (callerRva >= 0xA001F0 && callerRva <= 0xA00208) ||
           (callerRva >= 0x92CF50 && callerRva <= 0x92CF68) ||
           (callerRva >= 0xC011E0 && callerRva <= 0xC01278) ||
           (callerRva >= 0xC59560 && callerRva <= 0xC595C8);
}

static bool IsCSystemUpdaterExitActive(const void* pThis) noexcept {
    if (!pThis) return false;
    __try {
        const uint8_t* pBytes = reinterpret_cast<const uint8_t*>(pThis);
        // Ghidra-verified CSystem::Exit (0x1809fdb90) & CSystem::IsDedicatedUpdater (0x1802f3160):
        //   +0x53a = IsDedicatedUpdater, +0x228..+0x22a = restart/update/repair flags.
        //   (Note: +0x22d is normal runtime client state and MUST NOT be treated as updater exit!)
        return pBytes[0x53a] != 0 || pBytes[0x228] != 0 || pBytes[0x229] != 0 ||
               pBytes[0x22a] != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static void __fastcall Hooked_CSystem_Exit(void* pThis, int exitCode) {
    void* retAddr = _ReturnAddress();
    HMODULE hK2 = ::GetModuleHandleA("k2_x64.dll");
    uintptr_t callerRva = 0;
    if (hK2 && reinterpret_cast<uintptr_t>(retAddr) >= reinterpret_cast<uintptr_t>(hK2)) {
        callerRva = reinterpret_cast<uintptr_t>(retAddr) - reinterpret_cast<uintptr_t>(hK2);
    }

    char stackBuf[512] = {0};
    FormatStackBackTraceRvasNoAlloc(stackBuf, sizeof(stackBuf), 0);

    const bool errorTriggered = g_csystemErrorTriggered.load(std::memory_order_acquire);
    const bool quitRequested  = g_quitInitiated.load(std::memory_order_acquire) ||
                                g_userInitiatedCleanExit.load(std::memory_order_acquire);
    const bool updaterExit    = IsCSystemUpdaterExitActive(pThis);
    const bool knownQuitRva   = (callerRva != 0 && IsKnownCleanUserQuitCallerRva(callerRva));

    const bool isCleanExit = !errorTriggered && exitCode == 0 &&
                             (quitRequested || updaterExit || knownQuitRva);

    char callerDesc[160] = {0};
    snprintf(callerDesc, sizeof(callerDesc), "callerRva=0x%llX userQuit=%d updater=%d err=%d",
             static_cast<unsigned long long>(callerRva),
             quitRequested ? 1 : 0,
             updaterExit ? 1 : 0,
             errorTriggered ? 1 : 0);

    if (isCleanExit) {
        const char* quitReason = updaterExit ? "CSystem::Exit_Updater" : "CSystem::Exit_UserQuit";
        MarkQuitInitiated(quitReason);
        if (!g_chostShutdownHookInstalled.load(std::memory_order_acquire)) {
            MarkCleanUserExit(quitReason);
        }
        char cleanLog[800] = {0};
        snprintf(cleanLog, sizeof(cleanLog),
                 "[KONGOR NATIVE GUARD] [EXIT-HOOK] User CSystem::Exit(0) initiated (%s, chostHook=%d) | stack=%s",
                 callerDesc,
                 g_chostShutdownHookInstalled.load(std::memory_order_acquire) ? 1 : 0,
                 stackBuf);
        WriteSentinelLine(cleanLog);
        FlushSentinelLogs(true);
    } else {
        RevokeCleanExitSentinelsNoAlloc();
        WritePerPidCrashSentinelNoAlloc("Unsolicited_CSystem_Exit", exitCode, callerDesc, stackBuf);
        char crashLog[800] = {0};
        snprintf(crashLog, sizeof(crashLog),
                 "[KONGOR NATIVE GUARD] [EXIT-HOOK] [CRASH] Unsolicited CSystem::Exit(%d) intercepted (%s) | stack=%s",
                 exitCode, callerDesc, stackBuf);
        WriteSentinelLine(crashLog);
        FlushSentinelLogs(true);
    }

    // Synchronously tear down Vulkan capture/overlay layer on the main thread BEFORE CVid::Shutdown / exit(0)
    // while GPU/DXVK/bridge threads and HWND are still alive and LdrpLoaderLock is not held.
    InvokeOverlayPrepareProcessExitSeh("CSystem::Exit");

    if (g_origCSystemExit) {
        g_origCSystemExit(pThis, exitCode);
    }
}

static std::atomic<bool> g_k2SafeHooksInstalled{false};
extern "C" bool InstallK2SafeHooks(HMODULE hK2) {
    if (!hK2 || g_k2SafeHooksInstalled.load(std::memory_order_acquire)) return true;

    // 0a. Disable CArchive::ExamineChecksums (?ExamineChecksums@CArchive@@2_NA) on both server and client k2_x64.dll
    // immediately on DLL load (before CHost::Init mounts resources0..99.s2z) unless KONGOR_EXAMINE_ARCHIVE_CHECKSUMS=1,
    // preventing 200+ false "MODIFIED RESOURCES DETECTED" errors on custom .s2z archives.
    bool* pExamineChecksums = reinterpret_cast<bool*>(
        GetProcAddress(hK2, "?ExamineChecksums@CArchive@@2_NA"));
    if (Kongor::DisableArchiveChecksumsIfAllowed(pExamineChecksums)) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] Disabled CArchive::ExamineChecksums on k2_x64.dll (custom .s2z archive support).");
        OutputDebugStringA("[KONGOR NATIVE GUARD] Disabled CArchive::ExamineChecksums on k2_x64.dll (custom .s2z archive support).\n");
    }

    // 0b. Hook CanAccessGameMode unlock hooks on both client and server k2_x64.dll
    // Retail 17-byte prologues:
    // CClientAccount:    48 89 5C 24 08 57 48 83 EC 20 83 B9 90 00 00 00 03
    // CClientConnection: 48 89 5C 24 08 57 48 83 EC 20 83 B9 20 01 00 00 03
    static const uint8_t patClientAccountCanAccessMode[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x83, 0xB9, 0x90, 0x00, 0x00, 0x00, 0x03
    };
    static const uint8_t patClientConnectionCanAccessMode[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x83, 0xB9, 0x20, 0x01, 0x00, 0x00, 0x03
    };

    void* pfnAccountCanAccess = reinterpret_cast<void*>(GetProcAddress(
        hK2, "?CanAccessGameMode@CClientAccount@@QEBA_NAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z"));
    if (pfnAccountCanAccess) {
        Kongor::PatchController::InstallManagedInlineHook64(
            "gamemode", "CClientAccount::CanAccessGameMode", pfnAccountCanAccess,
            reinterpret_cast<void*>(Hooked_CClientAccount_CanAccessGameMode),
            nullptr, 17, patClientAccountCanAccessMode, sizeof(patClientAccountCanAccessMode));
    }

    void* pfnConnectionCanAccess = reinterpret_cast<void*>(GetProcAddress(
        hK2, "?CanAccessGameMode@CClientConnection@@QEBA_NAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z"));
    if (pfnConnectionCanAccess) {
        Kongor::PatchController::InstallManagedInlineHook64(
            "gamemode", "CClientConnection::CanAccessGameMode", pfnConnectionCanAccess,
            reinterpret_cast<void*>(Hooked_CClientConnection_CanAccessGameMode),
            nullptr, 17, patClientConnectionCanAccessMode, sizeof(patClientConnectionCanAccessMode));
    }

    if (pfnAccountCanAccess || pfnConnectionCanAccess) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] Installed CanAccessGameMode unlock hooks (CClientAccount + CClientConnection) on k2_x64.dll.");
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed CanAccessGameMode unlock hooks (CClientAccount + CClientConnection) on k2_x64.dll.\n");
    }

    if (!IsK2ClientModule(hK2)) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] K2_SERVER / non-client k2_x64.dll detected; skipping client-only UI/render hooks in InstallK2SafeHooks.");
        g_k2SafeHooksInstalled.store(true, std::memory_order_release);
        return true;
    }

    if (Kongor::EnsureMidwarsSnapshotDesyncHookInstalled(hK2)) {
        WriteSentinelLine("[NATIVE-GUARD][SNAPSHOT-DESYNC] Installed Layer 2 telemetry hook on CHostClient::ProcessCurrentSnapshot.");
    }
    uint8_t* pBase = reinterpret_cast<uint8_t*>(hK2);

    // 1. Hook AtoBFastW at RVA 0xC722F0 (14-byte reversible hook, group "seh")
    // Retail prologue: 48 83 EC 28 0F B7 11 66 85 D2 75 07 32 C0
    static const uint8_t patAtoBW[] = { 0x48, 0x83, 0xEC, 0x28, 0x0F, 0xB7, 0x11, 0x66, 0x85, 0xD2, 0x75, 0x07, 0x32, 0xC0 };
    uint8_t* pAtoBW = pBase + 0xC722F0;
    // (#268) Full-replacement hook: pass nullptr for ppOrigTrampoline (no trampoline needed)
    if (Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "AtoBFastW", pAtoBW, reinterpret_cast<void*>(Hooked_AtoBFastW),
            nullptr, 14, patAtoBW, sizeof(patAtoBW))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for AtoBFastW at k2_x64+0xC722F0.\n");
    }

    // 2. Hook AtoBFastA at RVA 0xC6E920 (14-byte reversible hook, group "atob")
    // Retail prologue: 48 83 EC 28 0F B6 11 84 D2 75 07 32 C0 48
    static const uint8_t patAtoBA[] = { 0x48, 0x83, 0xEC, 0x28, 0x0F, 0xB6, 0x11, 0x84, 0xD2, 0x75, 0x07, 0x32, 0xC0, 0x48 };
    uint8_t* pAtoBA = pBase + 0xC6E920;
    // (#268) Full-replacement hook: pass nullptr for ppOrigTrampoline (no trampoline needed)
    if (Kongor::PatchController::InstallManagedInlineHook64(
            "atob", "AtoBFastA", pAtoBA, reinterpret_cast<void*>(Hooked_AtoBFastA),
            nullptr, 14, patAtoBA, sizeof(patAtoBA))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for AtoBFastA at k2_x64+0xC6E920.\n");
    }

    // 2b. Hook AtoFFastW at RVA 0xC71330 (14-byte reversible hook, group "atob")
    // Retail prologue: 66 90 0F B7 01 66 83 F8 20 74 06 66 83 F8
    static const uint8_t patAtoFW[] = { 0x66, 0x90, 0x0F, 0xB7, 0x01, 0x66, 0x83, 0xF8, 0x20, 0x74, 0x06, 0x66, 0x83, 0xF8 };
    uint8_t* pAtoFW = reinterpret_cast<uint8_t*>(ResolveExport(hK2, "?AtoFFast@@YAMPEB_W@Z", 0xC71330));
    // (#268) Full-replacement hook: pass nullptr for ppOrigTrampoline (no trampoline needed)
    if (pAtoFW && Kongor::PatchController::InstallManagedInlineHook64(
            "atob", "AtoFFastW", pAtoFW, reinterpret_cast<void*>(Hooked_AtoFFastW),
            nullptr, 14, patAtoFW, sizeof(patAtoFW))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for AtoFFastW at k2_x64+0xC71330.\n");
    }

    // 2c. Hook AtoFFastA at RVA 0xC6F3A0 (14-byte reversible hook, group "atob")
    // Retail prologue: 66 90 0F B6 01 3C 20 74 04 3C 09 75 05 48
    static const uint8_t patAtoFA[] = { 0x66, 0x90, 0x0F, 0xB6, 0x01, 0x3C, 0x20, 0x74, 0x04, 0x3C, 0x09, 0x75, 0x05, 0x48 };
    uint8_t* pAtoFA = reinterpret_cast<uint8_t*>(ResolveExport(hK2, "?AtoFFast@@YAMPEBD@Z", 0xC6F3A0));
    // (#268) Full-replacement hook: pass nullptr for ppOrigTrampoline (no trampoline needed)
    if (pAtoFA && Kongor::PatchController::InstallManagedInlineHook64(
            "atob", "AtoFFastA", pAtoFA, reinterpret_cast<void*>(Hooked_AtoFFastA),
            nullptr, 14, patAtoFA, sizeof(patAtoFA))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for AtoFFastA at k2_x64+0xC6F3A0.\n");
    }

    // 3. Hook Lua::ExecuteStack at RVA 0x92EC30 (18-byte trampoline hook, group "seh")
    // Retail prologue: 48 89 5C 24 10 48 89 6C 24 18 56 57 41 56 48 83 EC 50
    static const uint8_t patExecStack[] = { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x56, 0x57, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x50 };
    uint8_t* pExecStack = pBase + 0x92EC30;
    if (Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "Lua::ExecuteStack", pExecStack, reinterpret_cast<void*>(Hooked_Lua_ExecuteStack),
            reinterpret_cast<void**>(&g_origLuaExecuteStack), 18, patExecStack, sizeof(patExecStack))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible trampoline hook for Lua::ExecuteStack at k2_x64+0x92EC30.\n");
    }

    // 3b. Hook IWidget::ExecuteLuaCallback at RVA 0xC19150 (21-byte trampoline hook, group "seh")
    // Retail prologue: 48 89 5C 24 08 48 89 74 24 10 55 57 41 56 48 8B EC 48 83 EC 70
    static const uint8_t patExecCb[] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x55, 0x57, 0x41, 0x56, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x70 };
    uint8_t* pExecCb = pBase + 0xC19150;
    if (Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "IWidget::ExecuteLuaCallback", pExecCb, reinterpret_cast<void*>(Hooked_WidgetExecuteLuaCallback),
            reinterpret_cast<void**>(&g_origWidgetExecuteLuaCallback), 21, patExecCb, sizeof(patExecCb))) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible trampoline hook for IWidget::ExecuteLuaCallback at k2_x64+0xC19150.\n");
    }

    // 4. Hook CBitmap::ScaleDown (Gamma and non-Gamma, group "seh") to neutralize invalid m_iBMPType crashes
    // Retail prologue (both @ 0x6815A0 and 0x681D60): 48 8B C4 44 89 48 20 44 89 40 18 48 89 50 10
    static const uint8_t patScaleDown[] = { 0x48, 0x8B, 0xC4, 0x44, 0x89, 0x48, 0x20, 0x44, 0x89, 0x40, 0x18, 0x48, 0x89, 0x50, 0x10 };
    void* pfnAlloc = ResolveExport(hK2, "?Alloc@CBitmap@@QEAAXHHHPEAE@Z", 0x6835f0);
    if (pfnAlloc) {
        g_pfnCBitmapAlloc = reinterpret_cast<FnCBitmapAlloc>(pfnAlloc);
    }

    void* pfnScaleDownGamma = ResolveExport(hK2, "?ScaleDown@CBitmap@@AEBAXAEAV1@HHM@Z", 0x6815a0);
    if (pfnScaleDownGamma) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CBitmap::ScaleDownGamma", pfnScaleDownGamma, reinterpret_cast<void*>(Hooked_CBitmap_ScaleDownGamma),
                reinterpret_cast<void**>(&g_origCBitmapScaleDownGamma), 15, patScaleDown, sizeof(patScaleDown))) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for CBitmap::ScaleDown(Gamma) at k2_x64+0x6815A0.\n");
        }
    }

    void* pfnScaleDown = ResolveExport(hK2, "?ScaleDown@CBitmap@@AEBAXAEAV1@HH@Z", 0x681d60);
    if (pfnScaleDown) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CBitmap::ScaleDown", pfnScaleDown, reinterpret_cast<void*>(Hooked_CBitmap_ScaleDown),
                reinterpret_cast<void**>(&g_origCBitmapScaleDown), 15, patScaleDown, sizeof(patScaleDown))) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for CBitmap::ScaleDown at k2_x64+0x681D60.\n");
        }
    }

    // 5. Hook CResourceManager::Unregister at RVA 0x9940F0 (15-byte reversible hook, group "seh")
    // Protects built-in procedural textures ($white = 0x0500000c, $black, $checker, $flat, $invis)
    // Retail prologue: 48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18
    static const uint8_t patResUnreg[] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x48, 0x89, 0x7C, 0x24, 0x18 };
    void* pfnResUnreg = ResolveExport(hK2, "?Unregister@CResourceManager@@QEAA_NIW4EUnregisterResource@@@Z", 0x9940f0);
    if (pfnResUnreg) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CResourceManager::Unregister", pfnResUnreg, reinterpret_cast<void*>(Hooked_CResourceManager_Unregister),
                reinterpret_cast<void**>(&g_origResourceManagerUnregister), 15, patResUnreg, sizeof(patResUnreg))) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for CResourceManager::Unregister at k2_x64+0x9940F0.\n");
        }
    }

    // 6. Hook CSystem::GetWindowArea at RVA 0x9FD770 (15-byte reversible hook, group "seh")
    // Ensures K2::CInput::Poll and CSystem::GetMousePos always subtract the live Win32 client-area
    // screen origin (ClientToScreen(hWnd, {0,0})), preventing hover-vs-click offset after Alt-Tab.
    // Retail prologue: 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18
    static const uint8_t patGetWinArea[] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18 };
    void* pfnGetWinArea = ResolveExport(hK2, "?GetWindowArea@CSystem@@QEAA?AV?$CRect@H@@XZ", 0x9fd770);
    if (pfnGetWinArea) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CSystem::GetWindowArea", pfnGetWinArea, reinterpret_cast<void*>(Hooked_CSystem_GetWindowArea),
                reinterpret_cast<void**>(&g_origCSystemGetWindowArea), 15, patGetWinArea, sizeof(patGetWinArea))) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for CSystem::GetWindowArea at k2_x64+0x9FD770.\n");
        }
    }

    // 7. Hook CModel::Load at RVA 0x947520 (15-byte reversible hook, group "seh")
    // Enables live mid-game Tree Stumps hot-swap (cg_stumpsTrees + ResourceCmd *trees*model.mdf reload)
    // without requiring KONGOR_ENABLE_MINIZ_ARCHIVE=1.
    // Retail prologue: 48 89 5C 24 10 48 89 74 24 18 48 89 4C 24 08
    static const uint8_t patModelLoad[] = { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x4C, 0x24, 0x08 };
    void* pfnModelLoad = ResolveExport(hK2, "?Load@CModel@@UEAAHIPEBDI@Z", 0x947520);
    if (pfnModelLoad) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CModel::Load", pfnModelLoad, reinterpret_cast<void*>(Hooked_CModel_Load),
                reinterpret_cast<void**>(&g_origCModelLoad), 15, patModelLoad, sizeof(patModelLoad))) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed reversible hook for CModel::Load at k2_x64+0x947520.\n");
        }
    }

    // 8. Vtable hook for CScrollbarSlider::MouseDown (slot 32, offset +0x100 in CScrollbarSlider vtable @ 0xF52C38)
    // Allows left-click (BUTTON_MOUSEL = 0x137) anywhere on scrollbar tracks to jump directly to clicked coordinate
    // satisfies: OBL-NG-SCROLLBAR-01
    // satisfies: OBL-NG-SCROLLBAR-03
    void** ppScrollbarVtableSlot = reinterpret_cast<void**>(pBase + Kongor::kScrollbarSliderVmtSlotRva);
    const void* pExpectedScrollbarMouseDown = pBase + Kongor::kScrollbarSliderMouseDownRva;
    const void* pExpectedCSliderMouseDown = pBase + Kongor::kCSliderMouseDownRva;
    g_pfnCSliderMouseDown = reinterpret_cast<FnSliderMouseDown>(const_cast<void*>(pExpectedCSliderMouseDown));
    const Kongor::ScrollbarVmtInstallStatus vmtStatus = Kongor::InstallScrollbarSliderVmtHook(
        ppScrollbarVtableSlot,
        pExpectedScrollbarMouseDown,
        pExpectedCSliderMouseDown,
        reinterpret_cast<void*>(Hooked_CScrollbarSlider_MouseDown),
        &g_origScrollbarSliderMouseDown);
    if (vmtStatus == Kongor::ScrollbarVmtInstallStatus::Installed) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed verified vtable hook for CScrollbarSlider::MouseDown at k2_x64+0xF52D38 (Click-to-Jump active).\n");
    } else if (vmtStatus != Kongor::ScrollbarVmtInstallStatus::AlreadyInstalled) {
        g_pfnCSliderMouseDown = nullptr;
        char vmtWarn[256];
        snprintf(vmtWarn, sizeof(vmtWarn),
            "[KONGOR NATIVE GUARD] [WARN] Aborted CScrollbarSlider::MouseDown VMT hook at k2_x64+0xF52D38 (status=%u, slotVal=%p, expected=%p).\n",
            static_cast<unsigned>(vmtStatus), *ppScrollbarVtableSlot, pExpectedScrollbarMouseDown);
        OutputDebugStringA(vmtWarn);
    }

    // 9. Hook CClientAccount::SelectUpgrade (RVA 0x739640)
    void* pfnClientAccountSelectUpgrade = ResolveExport(hK2, "?SelectUpgrade@CClientAccount@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z", 0x739640);
    if (pfnClientAccountSelectUpgrade) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CClientAccount::SelectUpgrade", pfnClientAccountSelectUpgrade, reinterpret_cast<void*>(Hooked_CClientAccount_SelectUpgrade),
                reinterpret_cast<void**>(&g_origClientAccountSelectUpgrade), 15, nullptr, 0)) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed hook for CClientAccount::SelectUpgrade at k2_x64+0x739640.\n");
        }
    }

    // 10. Hook CResourceManager::UnregisterFileChangeCallback (RVA 0x994F00)
    void* pfnUnregCb = ResolveExport(hK2, "?UnregisterFileChangeCallback@CResourceManager@@QEAAXPEAVIFileChangeCallback@@@Z", 0x994f00);
    if (pfnUnregCb) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CResourceManager::UnregisterFileChangeCallback", pfnUnregCb, reinterpret_cast<void*>(Hooked_CResourceManager_UnregisterFileChangeCallback),
                reinterpret_cast<void**>(&g_origUnregFileChangeCallback), 15, nullptr, 0)) {
            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed hook for CResourceManager::UnregisterFileChangeCallback.\n");
        }
    }

    // 11. Hook CHostClient::Connect at RVA 0x8DD4F0 (15-byte reversible hook, group "seh")
    // Wires login-screen / P2P nickname (p2p_nickname -> login_name -> "Player") into
    // CClientAccount::m_sNickname (+0x120) when unauthenticated so P2P & Offline Practice
    // matches never spawn players as "UnnamedNewbie".
    // Retail prologue: 48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18
    static const uint8_t patHostClientConnect[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x74, 0x24, 0x10,
        0x48, 0x89, 0x7C, 0x24, 0x18
    };
    void* pfnHostClientConnect = ResolveExport(
        hK2,
        "?Connect@CHostClient@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@_N0011@Z",
        0x8dd4f0);
    if (pfnHostClientConnect) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CHostClient::Connect", pfnHostClientConnect,
                reinterpret_cast<void*>(Hooked_CHostClient_Connect),
                reinterpret_cast<void**>(&g_origCHostClientConnect),
                15, patHostClientConnect, sizeof(patHostClientConnect))) {
            WriteSentinelLine("[KONGOR NATIVE GUARD] [NICKNAME] Installed reversible hook for CHostClient::Connect at k2_x64+0x8DD4F0.");
        }
    }

    // 12. Hook CSoundManager::LoadSample at RVA 0x9E7260 (15-byte reversible hook, group "seh")
    // Eliminates repeated 23-archive VFS scans on missing UI/hero samples after CSoundManager init (+0x20 != 0).
    // Retail prologue: 48 8B C4 48 89 58 10 44 89 40 18 48 89 48 08
    static const uint8_t patSoundLoadSample[] = {
        0x48, 0x8B, 0xC4,
        0x48, 0x89, 0x58, 0x10,
        0x44, 0x89, 0x40, 0x18,
        0x48, 0x89, 0x48, 0x08
    };
    void* pfnSoundLoadSample = ResolveExport(
        hK2,
        "?LoadSample@CSoundManager@@QEAAPEAVSound@FMOD@@AEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@H@Z",
        0x9e7260);
    if (pfnSoundLoadSample) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CSoundManager::LoadSample", pfnSoundLoadSample,
                reinterpret_cast<void*>(Hooked_CSoundManager_LoadSample),
                reinterpret_cast<void**>(&g_origCSoundManagerLoadSample),
                15, patSoundLoadSample, sizeof(patSoundLoadSample))) {
            WriteSentinelLine("[KONGOR NATIVE GUARD] [SOUND-NEGCACHE] Installed reversible hook for CSoundManager::LoadSample at k2_x64+0x9E7260.");
        }
    }

    // 13. Hook CServerList::ProcessReconnectStatusResponse (0x6f) at RVA 0x8CD7C0 (15-byte reversible hook, group "seh")
    // Drains optional 4-byte remainingReconnectMs trailer from 9-byte 0x6f packets so UDP packet loop never hits Unknown command.
    // Retail prologue: 48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 20
    static const uint8_t patReconnectStatus6f[] = {
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x74, 0x24, 0x10,
        0x48, 0x89, 0x7C, 0x24, 0x20
    };
    if (!g_pfnCPacketReadInt) {
        g_pfnCPacketReadInt = reinterpret_cast<FnCPacketReadInt>(
            ResolveExport(hK2, "?ReadInt@CPacket@@QEAAHH@Z", 0x973240));
    }
    uint8_t* pfnReconnectStatus6f = IsK2ClientModule(hK2) ? (pBase + 0x8cd7c0) : nullptr;
    if (pfnReconnectStatus6f && g_pfnCPacketReadInt) {
        if (Kongor::PatchController::InstallManagedInlineHook64(
                "seh", "CServerList::ReconnectStatus0x6f", pfnReconnectStatus6f,
                reinterpret_cast<void*>(Hooked_CServerList_ReconnectStatus),
                reinterpret_cast<void**>(&g_origCServerListReconnectStatus),
                15, patReconnectStatus6f, sizeof(patReconnectStatus6f))) {
            WriteSentinelLine("[KONGOR NATIVE GUARD] [NET-0x6F] Installed reversible hook for 0x6f ReconnectStatus at k2_x64+0x8CD7C0.");
        }
    }

    // 14. Hook CSystem::Error (RVA 0xA04F30) and CSystem::Exit (RVA 0x9FDB90) on K2_CLIENT (WS3B)
    // Captures lock-free stack RVAs & per-PID crash sentinels for silent ExitCode 0 / fatal engine exits.
    // Kill switch: KONGOR_DISABLE_EXIT_HOOKS=1
    char disableExitHooksEnv[16] = {0};
    const bool exitHooksDisabled =
        (::GetEnvironmentVariableA("KONGOR_DISABLE_EXIT_HOOKS", disableExitHooksEnv, sizeof(disableExitHooksEnv)) > 0 &&
         (disableExitHooksEnv[0] == '1' || disableExitHooksEnv[0] == 'y' || disableExitHooksEnv[0] == 'Y'));
    if (IsK2ClientModule(hK2) && !exitHooksDisabled) {
        // CSystem::Error exact 15-byte prologue + 7-byte tail verification:
        // 48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 | 55 41 56 41 57 48 8D
        static const uint8_t patCSystemError15[] = {
            0x48, 0x89, 0x5C, 0x24, 0x08,
            0x48, 0x89, 0x74, 0x24, 0x10,
            0x48, 0x89, 0x7C, 0x24, 0x18
        };
        static const uint8_t patCSystemErrorTail7[] = {
            0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D
        };
        void* pfnCSystemError = ResolveExport(
            hK2,
            "?Error@CSystem@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@0_N@Z",
            0xa04f30);
        if (pfnCSystemError &&
            memcmp(reinterpret_cast<const uint8_t*>(pfnCSystemError) + 15, patCSystemErrorTail7, sizeof(patCSystemErrorTail7)) == 0) {
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CSystem::Error", pfnCSystemError,
                    reinterpret_cast<void*>(Hooked_CSystem_Error),
                    reinterpret_cast<void**>(&g_origCSystemError),
                    15, patCSystemError15, sizeof(patCSystemError15))) {
                WriteSentinelLine("[KONGOR NATIVE GUARD] [EXIT-HOOK] Installed AOB-verified hook for CSystem::Error at k2_x64+0xA04F30.");
            }
        }

        // CSystem::Exit exact 15-byte prologue + 7-byte tail verification:
        // 40 55 53 56 57 41 56 48 8D AC 24 70 FF FF FF | 48 81 EC 90 01 00 00
        static const uint8_t patCSystemExit15[] = {
            0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x56,
            0x48, 0x8D, 0xAC, 0x24, 0x70, 0xFF, 0xFF, 0xFF
        };
        static const uint8_t patCSystemExitTail7[] = {
            0x48, 0x81, 0xEC, 0x90, 0x01, 0x00, 0x00
        };
        void* pfnCSystemExit = ResolveExport(hK2, "?Exit@CSystem@@QEAAXH@Z", 0x9fdb90);
        if (pfnCSystemExit &&
            memcmp(reinterpret_cast<const uint8_t*>(pfnCSystemExit) + 15, patCSystemExitTail7, sizeof(patCSystemExitTail7)) == 0) {
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CSystem::Exit", pfnCSystemExit,
                    reinterpret_cast<void*>(Hooked_CSystem_Exit),
                    reinterpret_cast<void**>(&g_origCSystemExit),
                    15, patCSystemExit15, sizeof(patCSystemExit15))) {
                WriteSentinelLine("[KONGOR NATIVE GUARD] [EXIT-HOOK] Installed AOB-verified hook for CSystem::Exit at k2_x64+0x9FDB90.");
            }
        }

        // CHost::Shutdown exact 17-byte prologue + 7-byte tail verification (k2_x64+0x8B78C0):
        //   [0..16]  48 89 5C 24 10 | 48 89 74 24 18 | 55 | 57 | 41 56 | 48 8B EC
        //   [17..23] 48 83 EC 40 | 48 8B F1
        static const uint8_t patCHostShutdown17[] = {
            0x48, 0x89, 0x5C, 0x24, 0x10,
            0x48, 0x89, 0x74, 0x24, 0x18,
            0x55, 0x57, 0x41, 0x56,
            0x48, 0x8B, 0xEC
        };
        static const uint8_t patCHostShutdownTail7[] = {
            0x48, 0x83, 0xEC, 0x40,
            0x48, 0x8B, 0xF1
        };
        void* pfnCHostShutdown = ResolveExport(hK2, "?Shutdown@CHost@@QEAAXXZ", 0x8b78c0);
        if (pfnCHostShutdown &&
            memcmp(reinterpret_cast<const uint8_t*>(pfnCHostShutdown) + 17, patCHostShutdownTail7, sizeof(patCHostShutdownTail7)) == 0) {
            if (Kongor::PatchController::InstallManagedInlineHook64(
                    "seh", "CHost::Shutdown", pfnCHostShutdown,
                    reinterpret_cast<void*>(Hooked_CHost_Shutdown),
                    reinterpret_cast<void**>(&g_origCHostShutdown),
                    17, patCHostShutdown17, sizeof(patCHostShutdown17))) {
                g_chostShutdownHookInstalled.store(true, std::memory_order_release);
                WriteSentinelLine("[KONGOR NATIVE GUARD] [EXIT-HOOK] Installed AOB-verified hook for CHost::Shutdown at k2_x64+0x8B78C0.");
            }
        }
    } else if (exitHooksDisabled) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] [EXIT-HOOK] Skipped CSystem::Error/Exit hooks via KONGOR_DISABLE_EXIT_HOOKS=1.");
    }

    g_k2SafeHooksInstalled.store(true, std::memory_order_release);
    return true;
}

// ============================================================================
// CGAME ENTITY DIRECTORY UAF GUARD (cgame_x64.dll+0xB1D17 / 2nd-Map-Load)
// DOSSIER-CGAME-ENTITY-DIRECTORY-UAF-0xB1D17
// ============================================================================


// satisfies: OBL-CG-02
static std::atomic<bool> g_cgameDynamicListDirty{ true };
static std::atomic<uint64_t> g_cgamePostSnapshotScans{ 0 };
static std::atomic<uint64_t> g_cgamePrunedEntitiesTotal{ 0 };
static std::atomic<uint64_t> g_cgameBidirSplicesTotal{ 0 };

typedef void (__fastcall *FnCEntityDirectoryPostSnapshot)(void* pThis);
typedef void (__fastcall *FnCEntityDirectoryDelete)(void* pThis, uint32_t uiIndex);
typedef void (__fastcall *FnCEntityDirectoryClear)(void* pThis);

static FnCEntityDirectoryPostSnapshot g_origCEntityDirectoryPostSnapshot = nullptr;
static FnCEntityDirectoryDelete g_origCEntityDirectoryDelete = nullptr;
static FnCEntityDirectoryClear g_origCEntityDirectoryClear = nullptr;

static void __fastcall Hooked_CEntityDirectory_Delete(void* pThis, uint32_t uiIndex) {
    // satisfies: OBL-CG-02
    g_cgameDynamicListDirty.store(true, std::memory_order_release);
    if (g_origCEntityDirectoryDelete) {
        g_origCEntityDirectoryDelete(pThis, uiIndex);
    }
}

static void __fastcall Hooked_CEntityDirectory_Clear(void* pThis) {
    // satisfies: OBL-CG-02
    g_cgameDynamicListDirty.store(true, std::memory_order_release);
    if (g_origCEntityDirectoryClear) {
        g_origCEntityDirectoryClear(pThis);
    }
}

static void __fastcall Hooked_CEntityDirectory_PostSnapshot(void* pThis) {
    // satisfies: OBL-CG-02
    // satisfies: OBL-CG-03
    // satisfies: OBL-CG-04
    const bool wasDirty = g_cgameDynamicListDirty.exchange(false, std::memory_order_acq_rel);
    LARGE_INTEGER qpcStart{}, qpcEnd{}, qpcFreq{};
    QueryPerformanceCounter(&qpcStart);

    size_t bidirSplices = 0;
    size_t pruned = Kongor_PruneOrphanDynamicEntities(pThis, g_pfnCMemManagerDeallocate, &bidirSplices);
    const uint64_t scanIdx = g_cgamePostSnapshotScans.fetch_add(1, std::memory_order_relaxed) + 1;

    if (pruned > 0 || bidirSplices > 0) {
        QueryPerformanceCounter(&qpcEnd);
        QueryPerformanceFrequency(&qpcFreq);
        const double elapsedUs = (qpcFreq.QuadPart > 0)
            ? (static_cast<double>(qpcEnd.QuadPart - qpcStart.QuadPart) * 1000000.0 / static_cast<double>(qpcFreq.QuadPart))
            : 0.0;
        const uint64_t totalPruned = g_cgamePrunedEntitiesTotal.fetch_add(pruned, std::memory_order_relaxed) + pruned;
        const uint64_t totalBidir = g_cgameBidirSplicesTotal.fetch_add(bidirSplices, std::memory_order_relaxed) + bidirSplices;

        char logBuf[256];
        snprintf(logBuf, sizeof(logBuf),
            "[KONGOR NATIVE GUARD] [CGAME-UAF-GUARD] Pruned %zu orphan dynamic entities (bidirSplices=%zu, wasDirty=%d, elapsed=%.1f us, totalPruned=%llu, totalBidir=%llu, scans=%llu) before PostSnapshot.",
            pruned, bidirSplices, wasDirty ? 1 : 0, elapsedUs,
            static_cast<unsigned long long>(totalPruned),
            static_cast<unsigned long long>(totalBidir),
            static_cast<unsigned long long>(scanIdx));
        WriteSentinelLine(logBuf);
        OutputDebugStringA(logBuf);
        OutputDebugStringA("\n");
    } else if (scanIdx == 1 || (scanIdx % 1800) == 0) {
        QueryPerformanceCounter(&qpcEnd);
        QueryPerformanceFrequency(&qpcFreq);
        const double elapsedUs = (qpcFreq.QuadPart > 0)
            ? (static_cast<double>(qpcEnd.QuadPart - qpcStart.QuadPart) * 1000000.0 / static_cast<double>(qpcFreq.QuadPart))
            : 0.0;
        char hbBuf[200];
        snprintf(hbBuf, sizeof(hbBuf),
            "[KONGOR NATIVE GUARD] [CGAME-UAF-GUARD] PostSnapshot scan #%llu healthy (elapsed=%.2f us, totalPruned=%llu)",
            static_cast<unsigned long long>(scanIdx),
            elapsedUs,
            static_cast<unsigned long long>(g_cgamePrunedEntitiesTotal.load(std::memory_order_relaxed)));
        WriteSentinelLine(hbBuf);
    }

    if (g_origCEntityDirectoryPostSnapshot) {
        g_origCEntityDirectoryPostSnapshot(pThis);
    }
}

// satisfies: OBL-CG-01
// satisfies: OBL-CG-05
bool InstallCGameEntityDirectoryHooks(HMODULE hCGame) {
    if (!hCGame) return false;
    // satisfies: OBL-CG-05
    if (Kongor_IsCGameEntityGuardDisabled()) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] [CGAME-UAF-GUARD] Disarmed by KONGOR_DISABLE_CGAME_ENTITY_GUARD kill switch.\n");
        return false;
    }

    auto pDos = reinterpret_cast<PIMAGE_DOS_HEADER>(hCGame);
    if (pDos->e_magic != IMAGE_DOS_SIGNATURE) return false;

    if (!g_pfnCMemManagerDeallocate) {
        HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
        if (hK2) {
            g_pfnCMemManagerDeallocate = reinterpret_cast<FnCMemManagerDeallocate>(
                ResolveExport(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z", 0x2a4ab0));
        }
    }

    uint8_t* pBase = reinterpret_cast<uint8_t*>(hCGame);

    // Expected retail 15-byte / 16-byte prologues
    // satisfies: OBL-CG-01
    static const uint8_t patPostSnapshot15[] = {
        0x48, 0x89, 0x4C, 0x24, 0x08, 0x55, 0x53, 0x56,
        0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56
    };

    static const uint8_t patDelete15[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20
    };

    static const uint8_t patClear16[] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56
    };

    uint8_t* pPostSnapshot = pBase + 0xB1400;
    uint8_t* pDelete = pBase + 0xB2B90;
    uint8_t* pClear = pBase + 0xB3CF0;

    bool okPostSnapshot = Kongor::PatchController::InstallManagedInlineHook64(
        "cgame", "CEntityDirectory::PostSnapshot", pPostSnapshot,
        reinterpret_cast<void*>(Hooked_CEntityDirectory_PostSnapshot),
        reinterpret_cast<void**>(&g_origCEntityDirectoryPostSnapshot),
        15, patPostSnapshot15, sizeof(patPostSnapshot15));

    bool okDelete = Kongor::PatchController::InstallManagedInlineHook64(
        "cgame", "CEntityDirectory::Delete", pDelete,
        reinterpret_cast<void*>(Hooked_CEntityDirectory_Delete),
        reinterpret_cast<void**>(&g_origCEntityDirectoryDelete),
        15, patDelete15, sizeof(patDelete15));

    bool okClear = Kongor::PatchController::InstallManagedInlineHook64(
        "cgame", "CEntityDirectory::Clear", pClear,
        reinterpret_cast<void*>(Hooked_CEntityDirectory_Clear),
        reinterpret_cast<void**>(&g_origCEntityDirectoryClear),
        16, patClear16, sizeof(patClear16));

    if (okPostSnapshot && okDelete && okClear) {
        WriteSentinelLine("[KONGOR NATIVE GUARD] [CGAME-UAF-GUARD] Installed dirty-gated CEntityDirectory UAF guards on cgame_x64.dll (PostSnapshot+Delete+Clear).");
        OutputDebugStringA("[KONGOR NATIVE GUARD] [CGAME-UAF-GUARD] Installed dirty-gated CEntityDirectory UAF guards on cgame_x64.dll (PostSnapshot+Delete+Clear).\n");
        return true;
    } else {
        char errBuf[256];
        snprintf(errBuf, sizeof(errBuf),
            "[KONGOR NATIVE GUARD] [CGAME-UAF-GUARD] Failed to install all hooks (post=%d, del=%d, clr=%d).\n",
            okPostSnapshot ? 1 : 0, okDelete ? 1 : 0, okClear ? 1 : 0);
        OutputDebugStringA(errBuf);
        return false;
    }
}

DWORD WINAPI GameSharedPatcherWorker(LPVOID lpParam) {
    bool gameSharedPatched = false;
    bool gameSharedHooked = false;
    HMODULE lastGameShared = nullptr;
    bool cgameHooked = false;
    HMODULE lastCGame = nullptr;

    while (true) {
        HMODULE hMod = GetModuleHandleA("game_shared_x64.dll");
        if (!hMod) {
            lastGameShared = nullptr;
            gameSharedPatched = false;
            gameSharedHooked = false;
        } else if (hMod != lastGameShared || !gameSharedHooked || !gameSharedPatched) {
            if (hMod != lastGameShared) {
                gameSharedPatched = false;
                gameSharedHooked = false;
            }
            uint8_t* pBase = reinterpret_cast<uint8_t*>(hMod);
            const bool isClientGameShared = IsGameSharedClientModule(hMod);

            // 1. TimeOfDay crash patch (group "sleep" - client RVA layout only)
            if (!gameSharedPatched) {
                if (isClientGameShared) {
                    uint8_t* pTarget = pBase + 0x569D24;
                    uint8_t* pCrash = pBase + 0x569D2F;
                    static const uint8_t patTimeOfDayOrig[] = { 0x4C, 0x8B, 0x81, 0xB0 };
                    static const uint8_t patTimeOfDayPatch[] = { 0x0F, 0x57, 0xC0, 0xC3 };
                    if (pCrash[0] == 0x41 && pCrash[1] == 0x8B && pCrash[2] == 0x88) {
                        if (Kongor::PatchController::InstallManagedBytePatch(
                                "sleep", "IGame::GetTimeOfDay", pTarget, patTimeOfDayPatch, sizeof(patTimeOfDayPatch),
                                patTimeOfDayOrig, sizeof(patTimeOfDayOrig))) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Reversible patch applied to game_shared_x64!IGame::GetTimeOfDay.\n");
                        }
                    }

                    // 1b. ServerPrecache Sleep(0) elimination patches (group "sleep")
                    static const uint8_t patSleepOrig[]  = { 0x74, 0x09 };
                    static const uint8_t patSleepPatch[] = { 0xEB, 0x09 };
                    uint8_t* pPrecacheEntity = pBase + 0x4A5FB9;
                    if (Kongor::PatchController::InstallManagedBytePatch(
                            "sleep", "CEntityRegistry::ServerPrecache", pPrecacheEntity, patSleepPatch, sizeof(patSleepPatch),
                            patSleepOrig, sizeof(patSleepOrig))) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Reversible patch applied to CEntityRegistry::ServerPrecache Sleep(0) yield.\n");
                    }

                    uint8_t* pPrecacheBot = pBase + 0x47A383;
                    if (Kongor::PatchController::InstallManagedBytePatch(
                                "sleep", "CBotRegistry::ServerPrecache", pPrecacheBot, patSleepPatch, sizeof(patSleepPatch),
                                patSleepOrig, sizeof(patSleepOrig))) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Reversible patch applied to CBotRegistry::ServerPrecache Sleep(0) yield.\n");
                    }
                }

                // 1c. Bot Economy & Active-Client Flag Patch at CPlayer::InitializeConnectionless + 0x26 (group "seh")
                // Prevents MOV EAX, 0x0282 from setting PLAYER_FLAG_TERMINATED (0x0080) on bots on both client & dedicated server,
                // restoring 2.5g/s Mid Wars passive gold and 850g starting gold across Legion and Hellbourne.
                void* pfnInitConn = GetProcAddress(hMod, kSymInitializeConnectionless);
                if (pfnInitConn) {
                    uint8_t* pMovFlags = reinterpret_cast<uint8_t*>(pfnInitConn) + 0x26;
                    static const uint8_t patInitConnOrig19[] = {
                        0xB8, 0x82, 0x02, 0x00, 0x00,                   // MOV EAX, 0x0282
                        0xC6, 0x86, 0x6B, 0x06, 0x00, 0x00, 0x01,       // MOV byte ptr [RSI+0x66B], 1
                        0x66, 0x09, 0x86, 0xDE, 0x03, 0x00, 0x00        // OR  word ptr [RSI+0x3DE], AX
                    };
                    static const uint8_t patInitConnActive5[] = {
                        0xB8, 0x02, 0x02, 0x00, 0x00                    // MOV EAX, 0x0202 (clear 0x0080 TERMINATED)
                    };
                    if (Kongor::PatchController::InstallManagedBytePatch(
                            "seh", "CPlayer::InitializeConnectionless_ActiveFlag",
                            pMovFlags, patInitConnActive5, sizeof(patInitConnActive5),
                            patInitConnOrig19, sizeof(patInitConnOrig19))) {
                        WriteSentinelLine("[KONGOR NATIVE GUARD] [BOT-ECONOMY] Applied reversible active-flag patch at CPlayer::InitializeConnectionless+0x26 (0x0282 -> 0x0202).");
                    }
                }

                gameSharedPatched = true;
            }

            // 2. CanAccessAltAvatar inline hooks (exported by name on both K2_CLIENT and K2_SERVER)
            if (!gameSharedHooked) {
                void* pFunc2 = ResolveExport(hMod, "?CanAccessAltAvatar@CPlayer@@QEAA_NAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@0@Z", 0x4fc7f0);
                if (pFunc2) {
                    if (InstallInlineHook64(pFunc2, reinterpret_cast<void*>(HookedCanAccessAltAvatar2), reinterpret_cast<void**>(&g_origCanAccessAltAvatar2), 15)) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::CanAccessAltAvatar (2-arg).\n");
                    }
                }

                void* pFunc1 = ResolveExport(hMod, "?CanAccessAltAvatar@CPlayer@@QEAA_NAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z", 0x4eb260);
                if (pFunc1) {
                    if (InstallInlineHook64(pFunc1, reinterpret_cast<void*>(HookedCanAccessAltAvatar1), reinterpret_cast<void**>(&g_origCanAccessAltAvatar1), 15)) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::CanAccessAltAvatar (1-arg).\n");
                    }
                }

                // 3. Upgrades and Taunts inline hooks (CPlayer)
                void* pHasUpgrade = ResolveExport(hMod, "?HasUpgrade@CPlayer@@QEBA_NAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z", 0x4fea90);
                if (pHasUpgrade) {
                    if (InstallInlineHook64(pHasUpgrade, reinterpret_cast<void*>(Hooked_CPlayer_HasUpgrade), reinterpret_cast<void**>(&g_origPlayerHasUpgrade), 15)) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::HasUpgrade (Super-Taunt unlock).\n");
                    }
                }

                // Client-only CPlayer upgrade/taunt sync hooks (skipped on K2_SERVER where ApplyPlayerUpgrades is a no-op)
                if (isClientGameShared) {
                    void* pUpdateUpgrades = reinterpret_cast<void*>(pBase + 0x4eb660);
                    if (pUpdateUpgrades) {
                        if (InstallInlineHook64(pUpdateUpgrades, reinterpret_cast<void*>(Hooked_CPlayer_UpdateUpgrades), reinterpret_cast<void**>(&g_origPlayerUpdateUpgrades), 15)) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::UpdateUpgrades at game_shared_x64+0x4eb660.\n");
                        }
                    }

                    void* pSelectHero = ResolveExport(hMod, "?SelectHero@CPlayer@@QEAAXG@Z", 0x4f2ed0);
                    if (pSelectHero) {
                        if (InstallInlineHook64(pSelectHero, reinterpret_cast<void*>(Hooked_CPlayer_SelectHero), reinterpret_cast<void**>(&g_origPlayerSelectHero), 16)) {
                            OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CPlayer::SelectHero (16 bytes).\n");
                        }
                    }
                }

                // Shared client + dedicated server CPlayer::SpawnHero, Terminate, Disconnected & AttachBot hooks
                EnsureGameSharedPlayerHooksInstalled(hMod, isClientGameShared);

                // CVisibilityMap::FillBitmap hook (Pebbles Duel FoW crash fix & giant-map stride guard)
                void* pFillBitmap = ResolveExport(hMod, "?FillBitmap@CVisibilityMap@@QEAAXAEAVCBitmap@@EE@Z", 0x53b1c0);
                if (pFillBitmap) {
                    static const uint8_t patFillBitmapOrig15[] = {
                        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x30
                    };
                    if (Kongor::PatchController::InstallManagedInlineHook64(
                            "seh", "CVisibilityMap::FillBitmap", pFillBitmap,
                            reinterpret_cast<void*>(Hooked_CVisibilityMap_FillBitmap),
                            reinterpret_cast<void**>(&g_origVisibilityMapFillBitmap), 15,
                            patFillBitmapOrig15, sizeof(patFillBitmapOrig15))) {
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for CVisibilityMap::FillBitmap (FoW bounds guard).\n");
                    }
                }

                // IHeroEntity::GetDeathGoldCost hook (BUG-501-01b: allow Devo Wars death gold loss from Reliable Gold)
                void* pGetDeathGoldCost = ResolveExport(hMod, "?GetDeathGoldCost@IHeroEntity@@QEBAI_N@Z", isClientGameShared ? 0x572fa0 : 0x573ce0);
                if (pGetDeathGoldCost) {
                    static const uint8_t patGetDeathGoldCostOrig15[] = {
                        0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x20, 0x88,
                        0x50, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54
                    };
                    if (Kongor::PatchController::InstallManagedInlineHook64(
                            "seh", "IHeroEntity::GetDeathGoldCost", pGetDeathGoldCost,
                            reinterpret_cast<void*>(Hooked_IHeroEntity_GetDeathGoldCost),
                            reinterpret_cast<void**>(&g_origHeroGetDeathGoldCost), 15,
                            patGetDeathGoldCostOrig15, sizeof(patGetDeathGoldCostOrig15))) {
                        WriteSentinelLine("[KONGOR NATIVE GUARD] [DEVO_GOLD] Installed inline hook for IHeroEntity::GetDeathGoldCost (15 bytes).");
                        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed inline hook for IHeroEntity::GetDeathGoldCost (Devo Wars gold loss fix).\n");
                    }
                }

                void** ppIGame = reinterpret_cast<void**>(GetProcAddress(hMod, kSymIGameInstance));
                if (ppIGame) {
                    g_ppIGameInstance.store(ppIGame, std::memory_order_release);
                }

                gameSharedHooked = true;
            }

            lastGameShared = hMod;
        }

        EnsureGameX64IatHooksInstalled();
        Kongor::EnsureMidwarsServerHooksInstalled();
        if (Kongor::IsQueueArcadeModeActive()) {
            Kongor::EnsureQueueArcadeK2HooksInstalled();
        }

        HMODULE hCGame = GetModuleHandleA("cgame_x64.dll");
        if (!hCGame) {
            lastCGame = nullptr;
            cgameHooked = false;
        } else if (hCGame != lastCGame || !cgameHooked) {
            if (hCGame != lastCGame) {
                cgameHooked = false;
            }
            cgameHooked = InstallCGameEntityDirectoryHooks(hCGame);
            Kongor::EnsureMidwarsClientReplayGuardInstalled(hCGame);
            lastCGame = hCGame;
        }

        Sleep(250);
    }
    return 0;
}


static bool InitializeRawInput(HWND hWnd) {
    if (g_rawInputRegistered.load(std::memory_order_acquire)) return true;
    if (!hWnd || !IsWindow(hWnd)) return false;

    RAWINPUTDEVICE rid[1];
    rid[0].usUsagePage = 0x01; // HID Generic Desktop
    rid[0].usUsage = 0x02;     // HID Mouse
    rid[0].dwFlags = RIDEV_INPUTSINK;
    rid[0].hwndTarget = hWnd;

    if (RegisterRawInputDevices(rid, 1, sizeof(rid[0]))) {
        g_rawInputRegistered.store(true, std::memory_order_release);
        OutputDebugStringA("[KONGOR NATIVE GUARD] RegisterRawInputDevices (Mouse, RIDEV_INPUTSINK) succeeded.\n");
        return true;
    } else {
        DWORD err = GetLastError();
        char logBuf[128];
        snprintf(logBuf, sizeof(logBuf), "[KONGOR NATIVE GUARD] RegisterRawInputDevices failed: %lu\n", err);
        OutputDebugStringA(logBuf);
        return false;
    }
}

static LRESULT CALLBACK KongorSubclassWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    DWORD dwWindowThread = GetWindowThreadProcessId(hWnd, NULL);
    bool isOnWindowThread = (GetCurrentThreadId() == dwWindowThread);
    if (isOnWindowThread) {
        g_lastMainThreadHeartbeatTick.store(GetTickCount64(), std::memory_order_relaxed);
    static uint64_t s_lastHeroKeyframeSampleTick = 0;
    uint64_t nowKeyframe = GetTickCount64();
    if (nowKeyframe - s_lastHeroKeyframeSampleTick >= 250) {
        s_lastHeroKeyframeSampleTick = nowKeyframe;
        RefreshServerKeyframePlayersFromEngine();
    }
        static std::atomic<bool> s_initialSafePointReached{false};
        if (!s_initialSafePointReached.exchange(true)) {
            Kongor::PatchController::OnMainThreadSafePoint(hWnd);
        }
    }
    if (g_isHeadlessProcess) {
        if (uMsg == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_MINIMIZE) {
            return 0;
        }
    }

    if (uMsg == WM_KONGOR_DISPATCH_CMD) {
        // Guard against execution from hook threads (e.g. OBS graphics-hook64, Discord overlay)
        DWORD dwWindowThread = GetWindowThreadProcessId(hWnd, NULL);
        if (GetCurrentThreadId() != dwWindowThread) {
            PostMessage(hWnd, WM_KONGOR_DISPATCH_CMD, wParam, lParam);
            return 0;
        }

        if (g_isHeadlessProcess) {
            LONG_PTR exStyle = GetWindowLongPtrA(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_APPWINDOW) != 0 || (exStyle & WS_EX_TOOLWINDOW) == 0) {
                SetWindowLongPtrA(hWnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~((LONG_PTR)WS_EX_APPWINDOW));
            }
            if (IsIconic(hWnd)) {
                ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            }
            RECT rcClient = {0};
            if (GetClientRect(hWnd, &rcClient) && ((rcClient.right - rcClient.left) < 640 || (rcClient.bottom - rcClient.top) < 360)) {
                SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 1280, 720, SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        // Option DD: Track main-thread active command dispatch for non-blocking pipe status probes
        g_activeMainThreadOpStartTick.store(GetTickCount64(), std::memory_order_relaxed);
        g_mainThreadBusy.store(true, std::memory_order_release);
        struct MainThreadBusyGuard {
            ~MainThreadBusyGuard() {
                g_mainThreadBusy.store(false, std::memory_order_release);
            }
        } busyGuard;

        // Drain any pending RuntimePatchController safe-point hook toggles on the main window thread
        Kongor::PatchController::OnMainThreadSafePoint(hWnd);

        // (#177) Drain decoupled Raw Input events on the main tick thread and commit 256-key state frame
        DecoupledRawInputEvent rawEv{};
        while (g_decoupledInputQueue.try_dequeue(rawEv)) {
            if (rawEv.eventKind == 2) {
                g_keyStateTracker.OnKeyDown(rawEv.virtualKey);
            } else if (rawEv.eventKind == 3) {
                g_keyStateTracker.OnKeyUp(rawEv.virtualKey);
            }
        }
        g_keyStateTracker.CommitTickFrame();

        std::vector<SubTickCommand> cmdsToRun;
        SubTickCommand dequeuedItem;
        while (g_cmdQueue.try_dequeue(dequeuedItem)) {
            cmdsToRun.push_back(std::move(dequeuedItem));
        }

        // Sub-Tick chronological sort guarantees twitch combo priority (e.g. Blink -> Hex -> Stun)
        std::stable_sort(cmdsToRun.begin(), cmdsToRun.end());

        if (ResolveK2ConsoleSymbols() && *g_ppConsole && (g_pfnConsoleAddCmdBuffer || g_pfnConsoleExecute)) {
            if (ResolveK2LuaSymbols() && g_ppLuaScriptManager && *g_ppLuaScriptManager) {
                void* L = *reinterpret_cast<void**>(*g_ppLuaScriptManager);
                if (L) {
                    Kongor::EnsureKongorLuaSharedMem(L, &g_shmHandle);
                }
            }
            for (const auto& item : cmdsToRun) {
                K2StringW k2Str(item.command.c_str());
                if (g_pfnConsoleAddCmdBuffer) {
                    g_pfnConsoleAddCmdBuffer(*g_ppConsole, &k2Str);
                } else {
                    g_pfnConsoleExecute(*g_ppConsole, &k2Str, 0);
                }
            }
        }
        return 0;
    }

    if (uMsg == WM_KONGOR_BG_HUMAN_CLICK) {
        DWORD dwWindowThread = GetWindowThreadProcessId(hWnd, NULL);
        if (GetCurrentThreadId() != dwWindowThread) {
            PostMessage(hWnd, WM_KONGOR_BG_HUMAN_CLICK, wParam, lParam);
            return 0;
        }

        int clientX = static_cast<int>(static_cast<short>(LOWORD(lParam)));
        int clientY = static_cast<int>(static_cast<short>(HIWORD(lParam)));
        POINT ptScreen = { clientX, clientY };
        ClientToScreen(hWnd, &ptScreen);

        g_bgClickScreenX.store(ptScreen.x, std::memory_order_relaxed);
        g_bgClickScreenY.store(ptScreen.y, std::memory_order_relaxed);
        g_bgClickExpireTick.store(GetTickCount64() + 120, std::memory_order_relaxed);
        g_bgClickOverrideActive.store(true, std::memory_order_release);

        EnterCriticalSection(&g_cursorLock);
        g_virtualCursorX = static_cast<double>(ptScreen.x);
        g_virtualCursorY = static_cast<double>(ptScreen.y);
        g_virtualCursorInitialized = true;
        LeaveCriticalSection(&g_cursorLock);

        if (g_pfnOrigWndProc) {
            LPARAM clickCoords = MAKELPARAM(clientX, clientY);
            CallWindowProc(g_pfnOrigWndProc, hWnd, WM_MOUSEMOVE, 0, clickCoords);
            if (wParam == 1) {
                CallWindowProc(g_pfnOrigWndProc, hWnd, WM_RBUTTONDOWN, MK_RBUTTON, clickCoords);
                CallWindowProc(g_pfnOrigWndProc, hWnd, WM_RBUTTONUP, 0, clickCoords);
            } else {
                CallWindowProc(g_pfnOrigWndProc, hWnd, WM_LBUTTONDOWN, MK_LBUTTON, clickCoords);
                CallWindowProc(g_pfnOrigWndProc, hWnd, WM_LBUTTONUP, 0, clickCoords);
            }
        }
        return 0;
    }

    if (uMsg == WM_INPUT) {
        if (g_rawInputEnabled.load(std::memory_order_relaxed)) {
            // Only process and accumulate raw input deltas if the game window currently has foreground focus
            if (g_gameWndFocused.load(std::memory_order_relaxed)) {
                HRAWINPUT hRawInput = (HRAWINPUT)lParam;
                UINT size = 0;
                GetRawInputData(hRawInput, RID_INPUT, NULL, &size, sizeof(RAWINPUTHEADER));
                if (size > 0 && size <= 256) {
                    uint8_t buffer[256];
                    if (GetRawInputData(hRawInput, RID_INPUT, buffer, &size, sizeof(RAWINPUTHEADER)) == size) {
                        RAWINPUT* raw = (RAWINPUT*)buffer;
                        if (raw->header.dwType == RIM_TYPEMOUSE) {
                            if ((raw->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0) {
                                int32_t dx = raw->data.mouse.lLastX;
                                int32_t dy = raw->data.mouse.lLastY;
                                if (dx != 0 || dy != 0) {
                                    g_rawAccumulatorX.fetch_add(dx, std::memory_order_acq_rel);
                                    g_rawAccumulatorY.fetch_add(dy, std::memory_order_acq_rel);
                                    g_rawPacketsProcessed.fetch_add(1, std::memory_order_relaxed);
                                    DecoupledRawInputEvent ev{};
                                    ev.eventKind     = 1;
                                    ev.deltaX        = dx;
                                    ev.deltaY        = dy;
                                    ev.timestampTick = GetTickCount64();
                                    g_decoupledInputQueue.enqueue(ev);
                                }
                            }
                        }
                    }
                }
            }
        }
        return CallWindowProc(g_pfnOrigWndProc, hWnd, uMsg, wParam, lParam);
    }

    if (uMsg == WM_MOUSEMOVE) {
        // Forward WM_MOUSEMOVE to CallWindowProc: restores Windows WM_SETCURSOR processing
        // and keeps physical OS mouse position in sync with raw OS click coordinates.
        // We do NOT swallow WM_MOUSEMOVE (no return 0).
    }

    // Intercept in-game Push-To-Talk hotkeys (T: Team, Y: Lane, U: Mentor, Mouse4/N: Party) and Cursor Escape Shortcuts
    if (uMsg == WM_KEYDOWN || uMsg == WM_SYSKEYDOWN) {
        uint8_t vk = static_cast<uint8_t>(wParam & 0xFF);
        g_keyStateTracker.OnKeyDown(vk);
        DecoupledRawInputEvent ev{};
        ev.eventKind     = 2;
        ev.virtualKey    = vk;
        ev.timestampTick = GetTickCount64();
        g_decoupledInputQueue.enqueue(ev);

        if (isOnWindowThread && !g_isHeadlessProcess) {
            bool altDown  = (uMsg == WM_SYSKEYDOWN) || ((GetKeyState(VK_MENU) & 0x8000) != 0);
            bool ctrlDown = ((GetKeyState(VK_CONTROL) & 0x8000) != 0);
            bool escaped = false;
            EnterCriticalSection(&g_cursorLock);
            escaped = g_cursorStateMachine.OnKeyDown(static_cast<uint32_t>(wParam), altDown, ctrlDown);
            LeaveCriticalSection(&g_cursorLock);
            if (escaped) {
                ApplyCursorConfinementPolicy("EscapeKey");
            }
        }

        if (wParam == 'T') {
            g_voicePttFlags.fetch_or(KONGOR_VOICE_PTT_TEAM, std::memory_order_relaxed);
        } else if (wParam == 'Y') {
            g_voicePttFlags.fetch_or(KONGOR_VOICE_PTT_LANE, std::memory_order_relaxed);
        } else if (wParam == 'U') {
            g_voicePttFlags.fetch_or(KONGOR_VOICE_PTT_MENTOR, std::memory_order_relaxed);
        } else if (wParam == 'N') {
            g_voicePttFlags.fetch_or(KONGOR_VOICE_PTT_PARTY, std::memory_order_relaxed);
        }
        if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = g_voicePttFlags.load(std::memory_order_relaxed);
    } else if (uMsg == WM_KEYUP || uMsg == WM_SYSKEYUP) {
        uint8_t vk = static_cast<uint8_t>(wParam & 0xFF);
        g_keyStateTracker.OnKeyUp(vk);
        DecoupledRawInputEvent ev{};
        ev.eventKind     = 3;
        ev.virtualKey    = vk;
        ev.timestampTick = GetTickCount64();
        g_decoupledInputQueue.enqueue(ev);

        if (wParam == 'T') {
            g_voicePttFlags.fetch_and(~KONGOR_VOICE_PTT_TEAM, std::memory_order_relaxed);
        } else if (wParam == 'Y') {
            g_voicePttFlags.fetch_and(~KONGOR_VOICE_PTT_LANE, std::memory_order_relaxed);
        } else if (wParam == 'U') {
            g_voicePttFlags.fetch_and(~KONGOR_VOICE_PTT_MENTOR, std::memory_order_relaxed);
        } else if (wParam == 'N') {
            g_voicePttFlags.fetch_and(~KONGOR_VOICE_PTT_PARTY, std::memory_order_relaxed);
        }
        if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = g_voicePttFlags.load(std::memory_order_relaxed);
    } else if (uMsg == WM_LBUTTONDOWN || uMsg == WM_RBUTTONDOWN || uMsg == WM_MBUTTONDOWN) {
        if (isOnWindowThread && !g_isHeadlessProcess) {
            g_gameWndFocused.store(true, std::memory_order_release);
            EnterCriticalSection(&g_cursorLock);
            g_cursorStateMachine.OnClientMouseButtonDown();
            LeaveCriticalSection(&g_cursorLock);
            PollConstrainCursorCvarIfNeeded();
            ApplyCursorConfinementPolicy("ClientMouseDown");
        }
    } else if (uMsg == WM_XBUTTONDOWN) {
        if (HIWORD(wParam) == XBUTTON1) {
            g_voicePttFlags.fetch_or(KONGOR_VOICE_PTT_PARTY, std::memory_order_relaxed);
            if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = g_voicePttFlags.load(std::memory_order_relaxed);
        }
        if (isOnWindowThread && !g_isHeadlessProcess) {
            g_gameWndFocused.store(true, std::memory_order_release);
            EnterCriticalSection(&g_cursorLock);
            g_cursorStateMachine.OnClientMouseButtonDown();
            LeaveCriticalSection(&g_cursorLock);
            PollConstrainCursorCvarIfNeeded();
            ApplyCursorConfinementPolicy("ClientXButtonDown");
        }
    } else if (uMsg == WM_XBUTTONUP) {
        if (HIWORD(wParam) == XBUTTON1) {
            g_voicePttFlags.fetch_and(~KONGOR_VOICE_PTT_PARTY, std::memory_order_relaxed);
            if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = g_voicePttFlags.load(std::memory_order_relaxed);
        }
    } else if (uMsg == WM_ENTERSIZEMOVE || uMsg == WM_ENTERMENULOOP) {
        if (isOnWindowThread) {
            EnterCriticalSection(&g_cursorLock);
            g_cursorStateMachine.OnEnterSizeMoveOrMenu();
            LeaveCriticalSection(&g_cursorLock);
            ApplyCursorConfinementPolicy(uMsg == WM_ENTERSIZEMOVE ? "WM_ENTERSIZEMOVE" : "WM_ENTERMENULOOP");
        }
    } else if (uMsg == WM_EXITSIZEMOVE || uMsg == WM_EXITMENULOOP) {
        if (isOnWindowThread) {
            EnterCriticalSection(&g_cursorLock);
            g_cursorStateMachine.OnExitSizeMoveOrMenu();
            LeaveCriticalSection(&g_cursorLock);
            SyncLiveClientGeometryToCursorState(hWnd);
            PollConstrainCursorCvarIfNeeded();
            ApplyCursorConfinementPolicy(uMsg == WM_EXITSIZEMOVE ? "WM_EXITSIZEMOVE" : "WM_EXITMENULOOP");
            if (uMsg == WM_EXITSIZEMOVE) {
                CheckAndHandleMonitorSwitch(hWnd, "WM_EXITSIZEMOVE");
            }
        }
    } else if (uMsg == WM_ACTIVATE) {
        uint16_t actState = LOWORD(wParam);
        bool active = (actState != WA_INACTIVE);
        g_gameWndFocused.store(active, std::memory_order_release);
        g_rawAccumulatorX.store(0, std::memory_order_release);
        g_rawAccumulatorY.store(0, std::memory_order_release);
        if (active) {
            LRESULT hitTest = HTCLIENT;
            if (actState == WA_CLICKACTIVE && isOnWindowThread) {
                hitTest = DefWindowProcA(hWnd, WM_NCHITTEST, 0, GetMessagePos());
            }
            SyncLiveClientGeometryToCursorState(hWnd);
            POINT pt = {0, 0};
            if (g_pfnRealGetCursorPos) g_pfnRealGetCursorPos(&pt);
            else ::GetCursorPos(&pt);
            EnterCriticalSection(&g_cursorLock);
            g_virtualCursorX = (double)pt.x;
            g_virtualCursorY = (double)pt.y;
            g_virtualCursorInitialized = true;
            g_cursorStateMachine.OnActivate(actState, hitTest);
            LeaveCriticalSection(&g_cursorLock);
            if (isOnWindowThread) {
                PollConstrainCursorCvarIfNeeded();
                ApplyCursorConfinementPolicy(actState == WA_CLICKACTIVE ? "WM_ACTIVATE(WA_CLICKACTIVE)" : "WM_ACTIVATE(WA_ACTIVE)");
            }
            OutputDebugStringA("[KONGOR NATIVE GUARD] WM_ACTIVATE: Window activated, client rect & cursor synchronized.\n");
        } else {
            g_voicePttFlags.store(0, std::memory_order_relaxed);
            if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = 0;
            EnterCriticalSection(&g_cursorLock);
            g_virtualCursorInitialized = false;
            g_cursorStateMachine.OnActivate(WA_INACTIVE, HTNOWHERE);
            LeaveCriticalSection(&g_cursorLock);
            if (isOnWindowThread) {
                ApplyCursorConfinementPolicy("WM_ACTIVATE(WA_INACTIVE)");
            }
            OutputDebugStringA("[KONGOR NATIVE GUARD] WM_ACTIVATE: Window deactivated, virtual cursor reset.\n");
            if (g_isHeadlessProcess) return 0;
        }
    } else if (uMsg == WM_ACTIVATEAPP) {
        bool active = (wParam != FALSE);
        g_gameWndFocused.store(active, std::memory_order_release);
        g_rawAccumulatorX.store(0, std::memory_order_release);
        g_rawAccumulatorY.store(0, std::memory_order_release);
        if (active) {
            SyncLiveClientGeometryToCursorState(hWnd);
            POINT pt = {0, 0};
            if (g_pfnRealGetCursorPos) g_pfnRealGetCursorPos(&pt);
            else ::GetCursorPos(&pt);
            EnterCriticalSection(&g_cursorLock);
            g_virtualCursorX = (double)pt.x;
            g_virtualCursorY = (double)pt.y;
            g_virtualCursorInitialized = true;
            g_cursorStateMachine.OnActivateApp(true);
            LeaveCriticalSection(&g_cursorLock);
            if (isOnWindowThread) {
                PollConstrainCursorCvarIfNeeded();
                ApplyCursorConfinementPolicy("WM_ACTIVATEAPP(TRUE)");
            }
            OutputDebugStringA("[KONGOR NATIVE GUARD] WM_ACTIVATEAPP: App activated, client rect & cursor synchronized.\n");
        } else {
            g_voicePttFlags.store(0, std::memory_order_relaxed);
            if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = 0;
            EnterCriticalSection(&g_cursorLock);
            g_virtualCursorInitialized = false;
            g_cursorStateMachine.OnActivateApp(false);
            LeaveCriticalSection(&g_cursorLock);
            if (isOnWindowThread) {
                ApplyCursorConfinementPolicy("WM_ACTIVATEAPP(FALSE)");
            }
            OutputDebugStringA("[KONGOR NATIVE GUARD] WM_ACTIVATEAPP: App deactivated, virtual cursor reset.\n");
            if (g_isHeadlessProcess) return 0;
        }
    } else if (uMsg == WM_SETFOCUS) {
        g_gameWndFocused.store(true, std::memory_order_release);
        g_rawAccumulatorX.store(0, std::memory_order_release);
        g_rawAccumulatorY.store(0, std::memory_order_release);
        SyncLiveClientGeometryToCursorState(hWnd);
        POINT pt = {0, 0};
        if (g_pfnRealGetCursorPos) g_pfnRealGetCursorPos(&pt);
        else ::GetCursorPos(&pt);
        EnterCriticalSection(&g_cursorLock);
        g_virtualCursorX = (double)pt.x;
        g_virtualCursorY = (double)pt.y;
        g_virtualCursorInitialized = true;
        g_cursorStateMachine.OnSetFocus();
        LeaveCriticalSection(&g_cursorLock);
        if (isOnWindowThread) {
            PollConstrainCursorCvarIfNeeded();
            ApplyCursorConfinementPolicy("WM_SETFOCUS");
        }
        OutputDebugStringA("[KONGOR NATIVE GUARD] WM_SETFOCUS: Window gained focus, client rect & cursor synchronized.\n");
    } else if (uMsg == WM_KILLFOCUS) {
        g_gameWndFocused.store(false, std::memory_order_release);
        g_rawAccumulatorX.store(0, std::memory_order_release);
        g_rawAccumulatorY.store(0, std::memory_order_release);
        // Clear all active PTT flags and virtual cursor state when game window loses foreground input focus
        g_voicePttFlags.store(0, std::memory_order_relaxed);
        if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = 0;
        EnterCriticalSection(&g_cursorLock);
        g_virtualCursorInitialized = false;
        g_cursorStateMachine.OnFocusLost();
        LeaveCriticalSection(&g_cursorLock);
        if (isOnWindowThread) {
            ApplyCursorConfinementPolicy("WM_KILLFOCUS");
        }
        OutputDebugStringA("[KONGOR NATIVE GUARD] WM_KILLFOCUS: Window lost focus, virtual cursor reset.\n");
    } else if (uMsg == WM_SIZE) {
        int w = LOWORD(lParam);
        int h = HIWORD(lParam);
        if (w > 0 && h > 0) {
            SyncLiveClientGeometryToCursorState(hWnd);
            if (isOnWindowThread && !g_isHeadlessProcess) {
                ApplyCursorConfinementPolicy("WM_SIZE");
            }
            char szBuf[128];
            snprintf(szBuf, sizeof(szBuf), "[KONGOR NATIVE GUARD] WM_SIZE updated client dims: %dx%d\n", w, h);
            OutputDebugStringA(szBuf);
        }
    } else if (uMsg == WM_MOVE) {
        int x = (short)LOWORD(lParam);
        int y = (short)HIWORD(lParam);
        SyncLiveClientGeometryToCursorState(hWnd);
        EnterCriticalSection(&g_cursorLock);
        g_virtualCursorInitialized = false;
        LeaveCriticalSection(&g_cursorLock);
        if (isOnWindowThread && !g_isHeadlessProcess) {
            ApplyCursorConfinementPolicy("WM_MOVE");
        }
        char szBuf[128];
        snprintf(szBuf, sizeof(szBuf), "[KONGOR NATIVE GUARD] WM_MOVE updated client origin: (%d, %d)\n", x, y);
        OutputDebugStringA(szBuf);
    } else if (uMsg == WM_WINDOWPOSCHANGED) {
        if (isOnWindowThread && !g_isHeadlessProcess) {
            SyncLiveClientGeometryToCursorState(hWnd);
            if (g_currentlyConfined.load(std::memory_order_relaxed)) {
                ApplyCursorConfinementPolicy("WM_WINDOWPOSCHANGED");
            }
            CheckAndHandleMonitorSwitch(hWnd, "WM_WINDOWPOSCHANGED");
        }
    } else if (uMsg == WM_DISPLAYCHANGE || uMsg == 0x02E0 /* WM_DPICHANGED */) {
        SyncLiveClientGeometryToCursorState(hWnd);
        EnterCriticalSection(&g_cursorLock);
        g_virtualCursorInitialized = false;
        LeaveCriticalSection(&g_cursorLock);
        if (isOnWindowThread && !g_isHeadlessProcess) {
            ApplyCursorConfinementPolicy(uMsg == WM_DISPLAYCHANGE ? "WM_DISPLAYCHANGE" : "WM_DPICHANGED");
            CheckAndHandleMonitorSwitch(hWnd, uMsg == WM_DISPLAYCHANGE ? "WM_DISPLAYCHANGE" : "WM_DPICHANGED");
        }
        char szBuf[160];
        snprintf(szBuf, sizeof(szBuf), "[KONGOR NATIVE GUARD] Display/DPI change: origin=(%d, %d), size=%dx%d\n",
            g_clientOriginX.load(std::memory_order_relaxed),
            g_clientOriginY.load(std::memory_order_relaxed),
            g_clientWindowWidth.load(std::memory_order_relaxed),
            g_clientWindowHeight.load(std::memory_order_relaxed));
        OutputDebugStringA(szBuf);
    } else if (uMsg == WM_CLOSE) {
        MarkQuitInitiated("WM_CLOSE");
        WriteSentinelLine("[KONGOR NATIVE GUARD] [QUIT-INIT] Game window received WM_CLOSE.");
    } else if (uMsg == WM_ENDSESSION) {
        MarkCleanUserExit("WM_ENDSESSION");
        WriteSentinelLine("[KONGOR NATIVE GUARD] [CLEAN-EXIT] Game window received WM_ENDSESSION.");
    }

    return CallWindowProc(g_pfnOrigWndProc, hWnd, uMsg, wParam, lParam);
}

static bool IsK2RenderWindow(HWND hWnd) {
    if (!hWnd || !IsWindow(hWnd)) return false;
    char className[64] = {0};
    if (GetClassNameA(hWnd, className, sizeof(className)) <= 0) return false;
    // Match K2 engine main render window classes ("K2_Direct3D", "K2_OpenGL", "K2_Window")
    // and reject DXVK temporary probe windows ("temp_d3d_window_*"), IME ("MSCTFIME UI"), or driver helper windows.
    return (strncmp(className, "K2_", 3) == 0);
}

static void EnsureWindowSubclassed() {
    if (g_hGameWnd && IsK2RenderWindow(g_hGameWnd)) return;

    DWORD currentPid = GetCurrentProcessId();
    HWND hWnd = NULL;
    while ((hWnd = FindWindowExA(NULL, hWnd, NULL, NULL)) != NULL) {
        DWORD pid = 0;
        GetWindowThreadProcessId(hWnd, &pid);
        if (pid == currentPid && IsK2RenderWindow(hWnd)) {
            LONG style = GetWindowLongA(hWnd, GWL_STYLE);
            if ((style & (WS_VISIBLE | WS_CAPTION | WS_POPUP))) {
                g_hGameWnd = hWnd;
                if (g_isHeadlessProcess) {
                    LONG_PTR exStyle = GetWindowLongPtrA(hWnd, GWL_EXSTYLE);
                    SetWindowLongPtrA(hWnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~((LONG_PTR)WS_EX_APPWINDOW));
                    LONG wndStyle = GetWindowLongA(hWnd, GWL_STYLE);
                    SetWindowLongA(hWnd, GWL_STYLE, (wndStyle | WS_POPUP) & ~(WS_CAPTION | WS_THICKFRAME));
                    SetWindowPos(hWnd, HWND_BOTTOM, 25000, 25000, 0, 0,
                                 SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
                }

                // Cache initial client window dimensions, screen origin, and focus state
                RECT rcClient = {0};
                if (GetClientRect(hWnd, &rcClient)) {
                    int w = rcClient.right - rcClient.left;
                    int h = rcClient.bottom - rcClient.top;
                    if (w > 0) g_clientWindowWidth.store(w, std::memory_order_release);
                    if (h > 0) g_clientWindowHeight.store(h, std::memory_order_release);
                }
                POINT ptOrigin = {0, 0};
                if (ClientToScreen(hWnd, &ptOrigin)) {
                    g_clientOriginX.store(ptOrigin.x, std::memory_order_release);
                    g_clientOriginY.store(ptOrigin.y, std::memory_order_release);
                }
                HWND fg = GetForegroundWindow();
                bool isFg = (fg == hWnd) && !IsIconic(hWnd);
                g_gameWndFocused.store(isFg, std::memory_order_release);
                g_lastMainThreadHeartbeatTick.store(GetTickCount64(), std::memory_order_relaxed);
    static uint64_t s_lastHeroKeyframeSampleTick = 0;
    uint64_t nowKeyframe = GetTickCount64();
    if (nowKeyframe - s_lastHeroKeyframeSampleTick >= 250) {
        s_lastHeroKeyframeSampleTick = nowKeyframe;
        RefreshServerKeyframePlayersFromEngine();
    }
                EnterCriticalSection(&g_cursorLock);
                g_cursorStateMachine.SetHeadless(g_isHeadlessProcess);
                g_cursorStateMachine.SetClientGeometry(
                    g_clientOriginX.load(std::memory_order_relaxed),
                    g_clientOriginY.load(std::memory_order_relaxed),
                    g_clientWindowWidth.load(std::memory_order_relaxed),
                    g_clientWindowHeight.load(std::memory_order_relaxed));
                if (isFg && !g_isHeadlessProcess) {
                    g_cursorStateMachine.OnActivate(WA_ACTIVE, HTCLIENT);
                } else {
                    g_cursorStateMachine.OnFocusLost();
                }
                LeaveCriticalSection(&g_cursorLock);
                Kongor_RegisterEmergencyCursorReleaseContext(g_pfnRealClipCursor, hWnd, &g_currentlyConfined, &g_cursorStateMachine);

                char geoBuf[160];
                snprintf(geoBuf, sizeof(geoBuf),
                    "[KONGOR NATIVE GUARD] Initialized client window geometry: origin=(%d, %d), size=%dx%d, focused=%s\n",
                    g_clientOriginX.load(std::memory_order_relaxed),
                    g_clientOriginY.load(std::memory_order_relaxed),
                    g_clientWindowWidth.load(std::memory_order_relaxed),
                    g_clientWindowHeight.load(std::memory_order_relaxed),
                    isFg ? "true" : "false");
                OutputDebugStringA(geoBuf);

                g_pfnOrigWndProc = (WNDPROC)SetWindowLongPtr(hWnd, GWLP_WNDPROC, (LONG_PTR)KongorSubclassWndProc);
                OutputDebugStringA("[KONGOR NATIVE GUARD] Hooked game window WndProc for main-thread command execution and Raw Input.\n");
                if (g_isHeadlessProcess) {
                    PostMessage(hWnd, WM_KONGOR_DISPATCH_CMD, 0, 0);
                }
                InitializeRawInput(hWnd);
                InstallRawInputIATHooks();
                InstallCursorAndConfigIATHooks();
                if (g_isHeadlessProcess) {
                    ApplyCursorConfinementPolicy("EnsureWindowSubclassed");
                } else if (isFg) {
                    PollConstrainCursorCvarIfNeeded();
                    ApplyCursorConfinementPolicy("EnsureWindowSubclassed");
                }

                // Auto-skip queue-concurrent PreloadWorld only on critical low free RAM (< 3.5 GiB), headless test runs, or KONGOR_DISABLE_BOOT_PRELOAD=1
                MEMORYSTATUSEX bootMemStatus{};
                bootMemStatus.dwLength = sizeof(bootMemStatus);
                const bool memOk = (::GlobalMemoryStatusEx(&bootMemStatus) != FALSE);
                const uint64_t availPhysBytes = memOk ? bootMemStatus.ullAvailPhys : (8ULL * 1024ULL * 1024ULL * 1024ULL);
                const uint64_t availPhysMiB = availPhysBytes / (1024ULL * 1024ULL);
                char disableBootPreloadEnv[16] = {0};
                const bool envDisableBootPreload =
                    (::GetEnvironmentVariableA("KONGOR_DISABLE_BOOT_PRELOAD", disableBootPreloadEnv, sizeof(disableBootPreloadEnv)) > 0 &&
                     (disableBootPreloadEnv[0] == '1' || disableBootPreloadEnv[0] == 'y' || disableBootPreloadEnv[0] == 'Y'));
                const bool shouldDisablePreload =
                    Kongor::ShouldDisableBootWorldPreload(availPhysBytes, envDisableBootPreload, g_isHeadlessProcess);
                char guardDiag[256] = {0};
                std::snprintf(
                    guardDiag,
                    sizeof(guardDiag),
                    "Queue-concurrent PreloadWorld %s (availRamMiB=%llu, minRequiredMiB=3584, headless=%d, envDisable=%d)",
                    shouldDisablePreload ? "DISABLED" : "ENABLED",
                    static_cast<unsigned long long>(availPhysMiB),
                    g_isHeadlessProcess ? 1 : 0,
                    envDisableBootPreload ? 1 : 0);
                Kongor::KongorWriteWillowmakerLog("BOOT_PRELOAD_GUARD", guardDiag);
                if (shouldDisablePreload) {
                    DispatchCommandToEngine(L"CreateBool kongor_disable_boot_preload true; Set kongor_disable_boot_preload true");
                    PostMessage(hWnd, WM_KONGOR_DISPATCH_CMD, 0, 0);
                }
                break;
            }
        }
    }
}

DWORD WINAPI NativeGuardWindowHookWatcher(LPVOID lpParam) {
    DWORD currentPid = GetCurrentProcessId();
    for (int i = 0; i < 150; ++i) { // 30 seconds (150 * 200ms)
        Sleep(200);
        if (g_hGameWnd && IsK2RenderWindow(g_hGameWnd) && g_rawInputRegistered.load(std::memory_order_relaxed)) {
            break;
        }
        HWND hWnd = NULL;
        while ((hWnd = FindWindowExA(NULL, hWnd, NULL, NULL)) != NULL) {
            DWORD pid = 0;
            GetWindowThreadProcessId(hWnd, &pid);
            if (pid == currentPid && IsK2RenderWindow(hWnd)) {
                LONG style = GetWindowLongA(hWnd, GWL_STYLE);
                if ((style & WS_VISIBLE) || (style & WS_CAPTION) || (style & WS_POPUP)) {
                    EnsureWindowSubclassed();
                    if (g_hGameWnd && IsK2RenderWindow(g_hGameWnd)) break;
                }
            }
        }
    }
    return 0;
}


static std::atomic<bool> g_gracefulQuitWatchdogArmed{false};
static std::atomic<bool> g_gracefulQuitWatchdogCancel{false};
static std::atomic<bool> g_gracefulQuitWatchdogEarlyLoad{true};
static std::atomic<HANDLE> g_hGracefulQuitWatchdogThread{nullptr};

// Pure POD/C frame helper so MSVC SEH __try/__except has zero unwindable C++ objects.
static void GracefulQuitWatchdogPreTerminateSeh(
    ULONGLONG totalElapsed,
    uint32_t deadlineMs,
    uint32_t enginePhase,
    bool isEarlyLoad) noexcept
{
    __try {
        InvokeOverlayPrepareProcessExitSeh("GracefulQuitWatchdog");
        MarkCleanUserExit(isEarlyLoad ? "IPC::quit_early_load_watchdog" : "IPC::quit_watchdog");
        Kongor_ForceReleaseCursorEmergency();

        const char* phaseName = GetCanonicalGamePhaseName(enginePhase);
        char msg[384] = {0};
        snprintf(
            msg,
            sizeof(msg),
            "[KONGOR NATIVE GUARD] [CLEAN-EXIT] Graceful IPC quit watchdog deadline reached "
            "(elapsed_ms=%llu, deadline_ms=%u, phase=%u[%s], early_load=%d); "
            "flushing sentinels and exiting cleanly with status 0.",
            static_cast<unsigned long long>(totalElapsed),
            deadlineMs,
            enginePhase,
            phaseName ? phaseName : "unknown",
            isEarlyLoad ? 1 : 0);
        // Lock-free, zero-allocation Win32 append + single OutputDebugStringA, plus try_lock-only flushes.
        WriteSentinelLineNoAllocBestEffort(msg);
        TryFlushArchiveLogNoBlock();
        FlushSentinelLogs(true);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA(
            "[KONGOR NATIVE GUARD] [CLEAN-EXIT] Exception caught in graceful quit watchdog pre-termination cleanup; proceeding to TerminateProcess(0).\n");
    }
}

void ShutdownGracefulQuitWatchdog(bool isProcessTermination) noexcept {
    g_gracefulQuitWatchdogCancel.store(true, std::memory_order_release);
    if (isProcessTermination) {
        // Under LdrShutdownProcess (lpReserved != NULL), kernel has already terminated worker threads;
        // never call WaitForSingleObject under loader lock.
        return;
    }

    HANDLE hWatchdog = g_hGracefulQuitWatchdogThread.exchange(nullptr, std::memory_order_acq_rel);
    if (hWatchdog) {
        const DWORD waitRes = WaitForSingleObject(hWatchdog, 500);
        if (waitRes != WAIT_OBJECT_0) {
            char warnMsg[192] = {0};
            snprintf(
                warnMsg,
                sizeof(warnMsg),
                "[KONGOR NATIVE GUARD] [CLEAN-EXIT] ShutdownGracefulQuitWatchdog bounded wait ended with status=%lu.\n",
                static_cast<unsigned long>(waitRes));
            OutputDebugStringA(warnMsg);
        }
        CloseHandle(hWatchdog);
    }
    g_gracefulQuitWatchdogArmed.store(false, std::memory_order_release);
}

static DWORD WINAPI GracefulQuitWatchdogThreadProc(LPVOID lpParam) {
    const uint32_t deadlineMs = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(lpParam));
    const ULONGLONG startTick = GetTickCount64();

    // Give K2's own CConsole::AddCmdBuffer("quit") / WM_CLOSE path first opportunity
    // to unwind cleanly (normal lobby/in-game exit completes in ~150-400 ms).
    while (true) {
        if (g_gracefulQuitWatchdogCancel.load(std::memory_order_acquire)) {
            return 0;
        }
        const ULONGLONG elapsed = GetTickCount64() - startTick;
        if (elapsed >= static_cast<ULONGLONG>(deadlineMs)) {
            break;
        }
        const ULONGLONG remaining = static_cast<ULONGLONG>(deadlineMs) - elapsed;
        Sleep(remaining > 25ULL ? 25U : static_cast<DWORD>(remaining));
    }

    if (g_gracefulQuitWatchdogCancel.load(std::memory_order_acquire)) {
        return 0;
    }

    const ULONGLONG totalElapsed = GetTickCount64() - startTick;
    const uint32_t currentPhase = GetCurrentNativeGamePhaseForLuaShim();
    const bool isEarlyLoad = g_gracefulQuitWatchdogEarlyLoad.load(std::memory_order_acquire);

    GracefulQuitWatchdogPreTerminateSeh(totalElapsed, deadlineMs, currentPhase, isEarlyLoad);

    // Deterministic clean exit code 0: avoids CRT/driver DLL_PROCESS_DETACH deadlocks
    // when K2 is mid-way through synchronous early asset/shader initialization.
    ::TerminateProcess(::GetCurrentProcess(), 0);
    return 0;
}

static void ArmGracefulQuitWatchdog(const char* sourceTag) noexcept {
    char testModeEnv[16] = {0};
    const DWORD testEnvLen = GetEnvironmentVariableA("KONGOR_NATIVE_GUARD_TEST_MODE", testModeEnv, sizeof(testModeEnv));
    if (testEnvLen > 0 && testEnvLen < sizeof(testModeEnv) &&
        (testModeEnv[0] == '1' || testModeEnv[0] == 'y' || testModeEnv[0] == 'Y')) {
        return;
    }

    MarkQuitInitiated(sourceTag ? sourceTag : "IPC::quit");
    if (g_gracefulQuitWatchdogCancel.load(std::memory_order_acquire)) {
        return;
    }
    if (g_gracefulQuitWatchdogArmed.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    const uint32_t enginePhase = GetCurrentNativeGamePhaseForLuaShim();
    const bool hasSubclassedWindow = (g_hGameWnd != NULL && IsWindow(g_hGameWnd) && g_pfnOrigWndProc != nullptr);
    const bool hasMainHeartbeat = (g_lastMainThreadHeartbeatTick.load(std::memory_order_relaxed) != 0);
    // Short early-load deadline while window isn't subclassed yet or main loop hasn't ticked in phase 0;
    // longer normal-shutdown deadline once lobby/in-game is active unless explicitly overridden via env.
    const bool isEarlyLoad = (!hasSubclassedWindow) || (!hasMainHeartbeat && enginePhase == 0);
    g_gracefulQuitWatchdogEarlyLoad.store(isEarlyLoad, std::memory_order_release);

    constexpr uint32_t kEarlyLoadDefaultDeadlineMs = 1250;
    constexpr uint32_t kNormalShutdownDefaultDeadlineMs = 4000;
    const uint32_t phaseDefaultDeadlineMs = isEarlyLoad
        ? kEarlyLoadDefaultDeadlineMs
        : kNormalShutdownDefaultDeadlineMs;

    char deadlineEnv[32] = {0};
    const DWORD envLen = GetEnvironmentVariableA("KONGOR_GRACEFUL_QUIT_DEADLINE_MS", deadlineEnv, sizeof(deadlineEnv));
    const bool hasValidEnvBuf = (envLen > 0 && envLen < sizeof(deadlineEnv));
    const uint32_t deadlineMs = Kongor_ResolveGracefulQuitDeadlineMs(
        hasValidEnvBuf ? deadlineEnv : nullptr,
        phaseDefaultDeadlineMs);

    const char* phaseName = GetCanonicalGamePhaseName(enginePhase);
    char armMsg[320] = {0};
    snprintf(
        armMsg,
        sizeof(armMsg),
        "[KONGOR NATIVE GUARD] [CLEAN-EXIT] Armed graceful quit watchdog "
        "(source=%s, deadline_ms=%u, default_ms=%u, phase=%u[%s], early_load=%d, subclassed=%d).",
        sourceTag ? sourceTag : "IPC::quit",
        deadlineMs,
        phaseDefaultDeadlineMs,
        enginePhase,
        phaseName ? phaseName : "unknown",
        isEarlyLoad ? 1 : 0,
        hasSubclassedWindow ? 1 : 0);
    // WriteSentinelLine already emits OutputDebugStringA once (no duplicate OutputDebugStringA).
    WriteSentinelLine(armMsg);
    FlushSentinelLogs(true);

    HANDLE hWatchdog = CreateThread(
        nullptr,
        0,
        GracefulQuitWatchdogThreadProc,
        reinterpret_cast<LPVOID>(static_cast<uintptr_t>(deadlineMs)),
        0,
        nullptr);
    if (!hWatchdog) {
        const DWORD err = GetLastError();
        g_gracefulQuitWatchdogArmed.store(false, std::memory_order_release);
        char errMsg[256] = {0};
        snprintf(
            errMsg,
            sizeof(errMsg),
            "[KONGOR NATIVE GUARD] [CLEAN-EXIT] CreateThread failed for graceful quit watchdog (GetLastError=%lu); disarmed latch.",
            static_cast<unsigned long>(err));
        WriteSentinelLine(errMsg);
        FlushSentinelLogs(true);
        return;
    }

    HANDLE prevHandle = g_hGracefulQuitWatchdogThread.exchange(hWatchdog, std::memory_order_acq_rel);
    if (prevHandle) {
        CloseHandle(prevHandle);
    }

    // Close race window if ShutdownGracefulQuitWatchdog executed before we published hWatchdog.
    if (g_gracefulQuitWatchdogCancel.load(std::memory_order_acquire)) {
        HANDLE hRace = g_hGracefulQuitWatchdogThread.exchange(nullptr, std::memory_order_acq_rel);
        if (hRace) {
            WaitForSingleObject(hRace, 500);
            CloseHandle(hRace);
        }
    }
}


void DispatchCommandToEngine(const std::wstring& wcmd, int64_t qpcTimestamp, uint32_t sequence) {
    EnsureWindowSubclassed();

    // #137: Quote-aware semicolon/newline command splitter + privilege filter (satisfies: OBL-W3-03)
    std::vector<std::wstring> sanitizedCmds;
    uint32_t blockedCount = 0;
    bool wasTruncated = false;
    Kongor::CmdSanitizer::SplitAndValidateCommandBufferW(
        wcmd, /*fromIpcOrUntrustedSource=*/true, sanitizedCmds, blockedCount, wasTruncated);

    if (wasTruncated) {
        OutputDebugStringW(L"[XASH3D-CMD-TRUNCATED] IPC command buffer exceeded 16384-char cap\n");
    }
    if (blockedCount > 0) {
        OutputDebugStringW(L"[XASH3D-CMD-PRIV-BLOCK] Blocked privileged/unsafe command(s) from IPC buffer\n");
    }
    if (sanitizedCmds.empty()) {
        return;
    }

    if (qpcTimestamp == 0) {
        LARGE_INTEGER qpc;
        QueryPerformanceCounter(&qpc);
        qpcTimestamp = qpc.QuadPart;
    }

    bool requestedQuit = false;
    for (size_t i = 0; i < sanitizedCmds.size(); ++i) {
        if (Kongor_IsQuitConsoleCommandW(sanitizedCmds[i].c_str())) {
            requestedQuit = true;
            ArmGracefulQuitWatchdog("IPC::quit");
        }

        uint32_t cmdSeq = (sequence != 0 && i == 0) ? sequence : ++g_subTickSeqGen;
        SubTickCommand cmdEntry;
        cmdEntry.hardwareTimestampQpc = qpcTimestamp;
        cmdEntry.sequence = cmdSeq;
        cmdEntry.command = sanitizedCmds[i];

        if (g_hGameWnd && IsWindow(g_hGameWnd) && g_pfnOrigWndProc) {
            g_cmdQueue.enqueue(std::move(cmdEntry));
            PostMessage(g_hGameWnd, WM_KONGOR_DISPATCH_CMD, 0, 0);
        } else {
            if (ResolveK2ConsoleSymbols() && *g_ppConsole && g_pfnConsoleAddCmdBuffer) {
                K2StringW k2Str(sanitizedCmds[i].c_str());
                g_pfnConsoleAddCmdBuffer(*g_ppConsole, &k2Str);
            }
        }
    }

    if (requestedQuit && g_hGameWnd && IsWindow(g_hGameWnd)) {
        PostMessage(g_hGameWnd, WM_CLOSE, 0, 0);
    }
}

// ============================================================================
// K2 LUA SCRIPT MANAGER & GC PACING (PHASE 12)
// ============================================================================


typedef int (*FnCountMemoryUsed)(void* pThis);
typedef void (*FnPerformGarbageCollection)(void* pThis, bool bCollect);
typedef void (*FnStartGarbageCollector)(void* pThis);
typedef void (*FnStopGarbageCollector)(void* pThis);

// g_pfnCountMemoryUsed defined at top of vfs_and_hooks.cpp
static FnPerformGarbageCollection g_pfnPerformGarbageCollection = nullptr;
static FnStartGarbageCollector g_pfnStartGarbageCollector = nullptr;
static FnStopGarbageCollector g_pfnStopGarbageCollector = nullptr;
// g_cachedLuaMemoryBytes defined at top of vfs_and_hooks.cpp

bool ResolveK2LuaSymbols() {
    if (g_ppLuaScriptManager && *g_ppLuaScriptManager) return true;
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) return false;

    if (!g_ppLuaScriptManager) {
        g_ppLuaScriptManager = (void**)GetProcAddress(hK2, "?g_pLuaScriptManager@@3PEAVCLuaScriptManager@@EA");
    }
    if (!g_pfnCountMemoryUsed) {
        g_pfnCountMemoryUsed = (FnCountMemoryUsed)GetProcAddress(hK2, "?CountMemoryUsed@CLuaScriptManager@@QEBAHXZ");
    }
    if (!g_pfnPerformGarbageCollection) {
        g_pfnPerformGarbageCollection = (FnPerformGarbageCollection)GetProcAddress(hK2, "?PerformGarbageCollection@CLuaScriptManager@@QEAAX_N@Z");
    }
    if (!g_pfnStartGarbageCollector) {
        g_pfnStartGarbageCollector = (FnStartGarbageCollector)GetProcAddress(hK2, "?StartGarbageCollector@CLuaScriptManager@@QEAAXXZ");
    }
    if (!g_pfnStopGarbageCollector) {
        g_pfnStopGarbageCollector = (FnStopGarbageCollector)GetProcAddress(hK2, "?StopGarbageCollector@CLuaScriptManager@@QEAAXXZ");
    }
    return (g_ppLuaScriptManager != nullptr && *g_ppLuaScriptManager != nullptr);
}


    __declspec(dllexport) uint64_t Kongor_NativeGuard_GetLuaMemoryUsed() {
        uint64_t cachedBytes = g_cachedLuaMemoryBytes.load(std::memory_order_relaxed);
        if (cachedBytes > 0) {
            return cachedBytes;
        }
        if (g_pTelemetryHeader && g_pTelemetryHeader->LuaMemoryKb > 0) {
            return static_cast<uint64_t>(g_pTelemetryHeader->LuaMemoryKb) * 1024ULL;
        }
        if (g_hGameWnd != NULL && GetCurrentThreadId() == GetWindowThreadProcessId(g_hGameWnd, NULL)) {
            if (ResolveK2LuaSymbols() && *g_ppLuaScriptManager && g_pfnCountMemoryUsed) {
                int luaBytes = g_pfnCountMemoryUsed(*g_ppLuaScriptManager);
                if (luaBytes >= 0) {
                    g_cachedLuaMemoryBytes.store(static_cast<uint64_t>(luaBytes), std::memory_order_relaxed);
                    return static_cast<uint64_t>(luaBytes);
                }
            }
        }
        return 0;
    }

    __declspec(dllexport) void Kongor_NativeGuard_TriggerLuaGC(bool fullCollect) {
        if (fullCollect) {
            DispatchCommandToEngine(L"Script collectgarbage(\"collect\")");
            g_fullGcCount.fetch_add(1, std::memory_order_relaxed);
        } else {
            DispatchCommandToEngine(L"Script collectgarbage(\"step\", 20)");
        }
    }

    __declspec(dllexport) uint64_t Kongor_NativeGuard_GetVoicePttFlags() {
        return g_voicePttFlags.load(std::memory_order_relaxed);
    }

    __declspec(dllexport) void Kongor_NativeGuard_SetVoicePttFlags(uint64_t flags) {
        g_voicePttFlags.store(flags, std::memory_order_relaxed);
        if (g_pTelemetryHeader) g_pTelemetryHeader->ReservedFlags = flags;
    }


    __declspec(dllexport) bool Kongor_NativeGuard_IsRawInputActive() {
        return g_rawInputEnabled.load(std::memory_order_relaxed) && g_rawInputRegistered.load(std::memory_order_relaxed);
    }

    __declspec(dllexport) void Kongor_NativeGuard_GetRawInputStats(uint64_t* outPackets, uint64_t* outSuppressed) {
        if (outPackets) *outPackets = g_rawPacketsProcessed.load(std::memory_order_relaxed);
        if (outSuppressed) *outSuppressed = g_mouseMoveSuppressedCount.load(std::memory_order_relaxed);
    }

    __declspec(dllexport) void Kongor_NativeGuard_SetRawInputEnabled(bool enabled) {
        g_rawInputEnabled.store(enabled, std::memory_order_relaxed);
    }

    __declspec(dllexport) void Kongor_NativeGuard_SetMouseSensitivity(float sensitivity) {
        if (sensitivity > 0.0001f && sensitivity < 100.0f) {
            g_mouseSensitivity.store(sensitivity, std::memory_order_relaxed);
        }
    }

extern "C" __declspec(dllexport) int __cdecl Kongor_NativeGuard_IsQuitConsoleCommandW(const wchar_t* cmdLine) {
    return Kongor_IsQuitConsoleCommandW(cmdLine) ? 1 : 0;
}

extern "C" __declspec(dllexport) uint32_t __cdecl Kongor_NativeGuard_ResolveGracefulQuitDeadlineMs(const char* envOverrideUtf8) {
    return Kongor_ResolveGracefulQuitDeadlineMs(envOverrideUtf8);
}


