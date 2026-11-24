// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: queue_arcade_hooks.cpp
// Purpose: Queue Mini-Game ("While You Wait" Arcade) drop-in/drop-out CPlayer
//          slot reservation, CBotBrain removal/dormancy, and CHostServer
//          GenerateClientID handshake matching for hon_x64.exe -dedicated.
// Dossier: artifacts/evidence/queue_arcade_slot_swap_dossier.json
// ============================================================================

#include "queue_arcade_hooks.h"
#include "native_guard_internal.h"
#include "runtime_patch_controller.h"
#include <windows.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>

namespace Kongor {

namespace {

// [Architecture] Queue Arcade slot-swap, GenerateClientID, and pre-Phase-5 duplicate SpawnHero
// hooks for Windows hon_x64.exe -dedicated live in queue_arcade_hooks.cpp (version.dll), while
// Linux System V AMD64 ABI hooks (OBL-QA-LINUX-00..11) live in linux_queue_arcade.h / linux_native_guard.cpp (libkongor_native_guard.so).
constexpr size_t kMaxArcadeReservations = 32;

struct ArcadeReservationEntry {
    bool    active;
    int32_t accountId;
    int32_t clientNum;
    int32_t teamId;
    void*   parkedBotBrain;
    wchar_t playerName[64];
    wchar_t savedBotName[64];
};

struct ArcadeSlotOriginEntry {
    bool        populated;
    const void* botDef;
    wchar_t     originalBotName[64];
};

// Read-only stack view matching MSVC x64 K2::wstring (std::basic_string<wchar_t, ..., K2StringAllocator<wchar_t>>)
// layout consumed by CNetworkResourceManager::SetString (k2_x64.dll+0x960a20):
//   +0x00: const wchar_t* _Ptr (used when _Myres > 7)
//   +0x08: uint64_t       _Pad
//   +0x10: size_t         _Mysize
//   +0x18: size_t         _Myres (set to 64 > 7 so SetString always reads _Ptr without heap allocation)
struct K2WStringReadView {
    const wchar_t* ptr;
    uint64_t       pad;
    size_t         size;
    size_t         cap;
};
static_assert(sizeof(K2WStringReadView) == 0x20, "K2WStringReadView must match 32-byte K2::wstring ABI");

static SRWLOCK g_arcadeReservationLock = SRWLOCK_INIT;
static SRWLOCK g_arcadeHookInstallLock = SRWLOCK_INIT;
static ArcadeReservationEntry g_arcadeReservations[kMaxArcadeReservations] = {};
static ArcadeSlotOriginEntry g_arcadeSlotOrigins[kMaxArcadeReservations] = {};

static std::atomic<int32_t> g_arcadeMode{ -1 }; // -1 = uninitialized (lazy env check)
static std::atomic<int32_t> g_arcadeSwapHooksEnabled{ -1 }; // -1 = uninitialized, 0 = disabled, 1 = enabled
static std::atomic<void*> g_observedArcadeHostServer{ nullptr };
static std::atomic<uint32_t> g_arcadeKickInProgressCount{ 0 };
static thread_local uint32_t t_arcadeKickReentrancyDepth = 0;
static std::atomic<bool> g_generateClientIdHookInstalled{ false };
static std::atomic<const uint8_t*> g_generateClientIdTargetAddr{ nullptr };
static std::atomic<bool> g_pendingSafeModeRetirement{ false };
static std::atomic<HMODULE> g_hookedGameX64Module{ nullptr };

typedef int (__fastcall *PFN_CHostServer_GenerateClientID)(void* pHostServer, uint16_t* pConnId, int32_t accountId);
static PFN_CHostServer_GenerateClientID g_origGenerateClientID = nullptr;

typedef void* (__fastcall *PFN_CGameServer_AddBot)(
    void* pGameServer,
    uint32_t uiTeam,
    void* pBotNameWStr,
    void* pPlayerNameWStr,
    uint8_t bSpawnImmediate,
    uint32_t uiTeamSlot,
    void* pOutResultCode
);
static PFN_CGameServer_AddBot g_origGameServerAddBot = nullptr;

static QueueArcadeEngineContext g_liveArcadeCtx = {};
static std::atomic<bool> g_liveArcadeCtxResolved{ false };

static void LogArcadeLine(const char* msg) noexcept {
    if (!msg) return;
    OutputDebugStringA(msg);
    OutputDebugStringA("\n");
}

static void ResetArcadeSlotOriginsLocked() noexcept {
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        g_arcadeSlotOrigins[i] = {};
    }
}

static void SanitizeChatTokenW(const wchar_t* raw, wchar_t* outBuf, size_t outCap, const wchar_t* fallback) noexcept {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = L'\0';
    const wchar_t* src = (raw && *raw) ? raw : (fallback ? fallback : L"Player");
    size_t dst = 0;
    for (size_t i = 0; src[i] != L'\0' && dst + 1 < outCap; ++i) {
        wchar_t ch = src[i];
        if (ch < 0x20 || ch == L'"' || ch == L';' || ch == L'\\' || ch == L'\r' || ch == L'\n') {
            continue;
        }
        outBuf[dst++] = ch;
    }
    outBuf[dst] = L'\0';
    if (dst == 0 && fallback && *fallback) {
        wcsncpy_s(outBuf, outCap, fallback, _TRUNCATE);
    }
}

static void __fastcall EnqueueLiveWindowsServerConsoleCmdW(const wchar_t* cmdLine) noexcept {
    if (!cmdLine || !*cmdLine) return;
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) return;

    using PFN_AddCmdBuffer = void (__fastcall*)(void* pConsole, const void* pK2WStr);
    static std::atomic<void**> s_ppConsole{ nullptr };
    static std::atomic<PFN_AddCmdBuffer> s_pfnAddCmdBuffer{ nullptr };

    void** ppConsole = s_ppConsole.load(std::memory_order_acquire);
    if (!ppConsole) {
        ppConsole = reinterpret_cast<void**>(GetProcAddress(hK2, "?g_pConsole@@3PEAVCConsole@@EA"));
        if (ppConsole) {
            s_ppConsole.store(ppConsole, std::memory_order_release);
        }
    }
    PFN_AddCmdBuffer pfnAdd = s_pfnAddCmdBuffer.load(std::memory_order_acquire);
    if (!pfnAdd) {
        pfnAdd = reinterpret_cast<PFN_AddCmdBuffer>(GetProcAddress(
            hK2, "?AddCmdBuffer@CConsole@@QEAAXAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@@Z"));
        if (pfnAdd) {
            s_pfnAddCmdBuffer.store(pfnAdd, std::memory_order_release);
        }
    }
    if (!ppConsole || !pfnAdd) return;

    __try {
        void* pConsole = *ppConsole;
        if (!pConsole) return;
        K2WStringReadView view = {};
        view.ptr = cmdLine;
        view.pad = 0;
        view.size = wcslen(cmdLine);
        view.cap = 256;
        pfnAdd(pConsole, &view);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

static void QueueArcadeSwapChatNoticeW(
    const QueueArcadeEngineContext* ctx,
    const wchar_t* humanName,
    const wchar_t* botName,
    bool isJoin
) noexcept {
    if (!ctx || !ctx->pfnQueueConsoleCommand) return;
    wchar_t safeHuman[64] = {};
    wchar_t safeBot[64] = {};
    SanitizeChatTokenW(humanName, safeHuman, _countof(safeHuman), L"Player");
    SanitizeChatTokenW(botName, safeBot, _countof(safeBot), L"ArcadeBot");

    wchar_t cmdBuf[224] = {};
    if (isJoin) {
        _snwprintf_s(cmdBuf, _countof(cmdBuf), _TRUNCATE,
            L"ServerChat \"[Queue Arcade] %s replaced %s!\"", safeHuman, safeBot);
    } else {
        _snwprintf_s(cmdBuf, _countof(cmdBuf), _TRUNCATE,
            L"ServerChat \"[Queue Arcade] %s left - %s took over!\"", safeHuman, safeBot);
    }
    ctx->pfnQueueConsoleCommand(cmdBuf);

    char utf8Notice[256] = {};
    WideCharToMultiByte(CP_UTF8, 0, cmdBuf, -1, utf8Notice, sizeof(utf8Notice) - 1, NULL, NULL);
    char logBuf[320] = {};
    snprintf(logBuf, sizeof(logBuf), "[KONGOR QUEUE ARCADE] Queued swap chat notice: %s", utf8Notice);
    LogArcadeLine(logBuf);
}

static void* ResolveEffectiveHostServerSeh(const QueueArcadeEngineContext* ctx) noexcept {
    if (!ctx) {
        return g_observedArcadeHostServer.load(std::memory_order_acquire);
    }
    if (ctx->pHostServerOverride != nullptr) {
        return ctx->pHostServerOverride;
    }
    __try {
        if (ctx->ppHost && *ctx->ppHost) {
            void* pHost = *ctx->ppHost;
            void* pSrv = ctx->pfnGetHostServer
                ? ctx->pfnGetHostServer(pHost)
                : *reinterpret_cast<void* const*>(reinterpret_cast<uintptr_t>(pHost) + 0xE0);
            if (pSrv) {
                return pSrv;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return g_observedArcadeHostServer.load(std::memory_order_acquire);
}

static int32_t ReadCurrentGamePhaseSeh(const QueueArcadeEngineContext* ctx) noexcept {
    if (!ctx || !ctx->ppGame) return 0;
    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) return 0;
        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t pGameInfo = *reinterpret_cast<const uintptr_t*>(pGame + 0xB0);
        if (!pGameInfo) return 0;
        return *reinterpret_cast<const int32_t*>(pGameInfo + 0xB4);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

static const wchar_t* StripLeadingClanTagW(const wchar_t* name) noexcept {
    if (!name) return L"";
    while (*name == L' ' || *name == L'\t') ++name;
    if (*name == L'[') {
        const wchar_t* closeBracket = wcschr(name + 1, L']');
        if (closeBracket && *(closeBracket + 1) != L'\0') {
            return closeBracket + 1;
        }
    }
    return name;
}

static bool NamesMatchPlayerW(const wchar_t* a, const wchar_t* b) noexcept {
    if (!a || !b || !*a || !*b) return false;
    if (_wcsicmp(a, b) == 0) return true;
    const wchar_t* sa = StripLeadingClanTagW(a);
    const wchar_t* sb = StripLeadingClanTagW(b);
    return (*sa != L'\0' && *sb != L'\0' && _wcsicmp(sa, sb) == 0);
}

static void SplitDisplayNameAndClanTagW(
    const wchar_t* rawInput,
    const wchar_t* explicitClanTag,
    wchar_t* outBareName,
    size_t bareCap,
    wchar_t* outBareClanTag,
    size_t clanCap
) noexcept {
    if (outBareName && bareCap > 0) outBareName[0] = L'\0';
    if (outBareClanTag && clanCap > 0) outBareClanTag[0] = L'\0';

    const wchar_t* namePtr = rawInput ? rawInput : L"";
    while (*namePtr == L' ' || *namePtr == L'\t') ++namePtr;

    if (*namePtr == L'[') {
        const wchar_t* closeBracket = wcschr(namePtr + 1, L']');
        if (closeBracket && *(closeBracket + 1) != L'\0') {
            const size_t tagLen = static_cast<size_t>(closeBracket - (namePtr + 1));
            if (outBareClanTag && clanCap > 1 && tagLen > 0) {
                wcsncpy_s(outBareClanTag, clanCap, namePtr + 1, (tagLen < clanCap - 1) ? tagLen : (clanCap - 1));
            }
            namePtr = closeBracket + 1;
            while (*namePtr == L' ' || *namePtr == L'\t') ++namePtr;
        }
    }

    if (outBareName && bareCap > 1 && *namePtr != L'\0') {
        wcsncpy_s(outBareName, bareCap, namePtr, _TRUNCATE);
    }

    if (explicitClanTag && *explicitClanTag && outBareClanTag && clanCap > 1) {
        const wchar_t* cPtr = explicitClanTag;
        while (*cPtr == L' ' || *cPtr == L'\t' || *cPtr == L'[') ++cPtr;
        size_t cLen = wcslen(cPtr);
        while (cLen > 0 && (cPtr[cLen - 1] == L']' || cPtr[cLen - 1] == L' ' || cPtr[cLen - 1] == L'\t')) {
            --cLen;
        }
        if (cLen > 0) {
            wcsncpy_s(outBareClanTag, clanCap, cPtr, (cLen < clanCap - 1) ? cLen : (clanCap - 1));
        }
    }
}

static bool ReadK2WStringFromPtrSeh(const void* pK2Str, wchar_t* outBuf, size_t outCap) noexcept {
    if (!pK2Str || !outBuf || outCap == 0) return false;
    outBuf[0] = L'\0';
    __try {
        const uint8_t* rawStr = reinterpret_cast<const uint8_t*>(pK2Str);
        const size_t wlen = *reinterpret_cast<const size_t*>(rawStr + 0x10);
        const size_t wcap = *reinterpret_cast<const size_t*>(rawStr + 0x18);
        if (wlen >= outCap || wcap < wlen || wcap > 256) {
            return false;
        }
        if (wlen == 0) {
            outBuf[0] = L'\0';
            return true;
        }
        const wchar_t* wptr = (wcap > 7)
            ? *reinterpret_cast<const wchar_t* const*>(rawStr)
            : reinterpret_cast<const wchar_t*>(rawStr);
        if (!wptr) return false;
        for (size_t i = 0; i < wlen; ++i) {
            wchar_t ch = wptr[i];
            if (ch == L'\0') {
                outBuf[i] = L'\0';
                return i > 0;
            }
            outBuf[i] = ch;
        }
        outBuf[wlen] = L'\0';
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        outBuf[0] = L'\0';
        return false;
    }
}

static bool SetPlayerNetStringSlotSeh(
    const QueueArcadeEngineContext* ctx,
    uint16_t strIndex,
    const wchar_t* text
) noexcept {
    if (!ctx || !ctx->ppNetResourceManager || !ctx->pfnSetNetString || strIndex == 0xFFFFu) {
        return false;
    }
    void* pNetMgr = *ctx->ppNetResourceManager;
    if (!pNetMgr) {
        return false;
    }
    const wchar_t* safeText = text ? text : L"";
    K2WStringReadView view = {};
    view.ptr = safeText;
    view.pad = 0;
    view.size = wcslen(safeText);
    view.cap = 64; // > 7 forces CNetworkResourceManager::SetString to read view.ptr directly without heap ops
    ctx->pfnSetNetString(pNetMgr, strIndex, &view, false);
    return true;
}

static const QueueArcadeEngineContext* ResolveLiveArcadeEngineContext() noexcept {
    if (g_liveArcadeCtxResolved.load(std::memory_order_acquire)) {
        return &g_liveArcadeCtx;
    }

    HMODULE hGS = GetModuleHandleA("game_shared_x64.dll");
    if (!hGS) {
        return nullptr;
    }

    QueueArcadeEngineContext ctx = {};
    ctx.ppGame = reinterpret_cast<void**>(GetProcAddress(hGS, "?s_pGame@IGame@@0PEAV1@EA"));
    ctx.pfnRemoveBot = reinterpret_cast<decltype(ctx.pfnRemoveBot)>(
        GetProcAddress(hGS, "?RemoveBot@CPlayer@@QEAAXXZ"));
    ctx.pfnAttachBot = reinterpret_cast<decltype(ctx.pfnAttachBot)>(
        GetProcAddress(hGS, "?AttachBot@CPlayer@@QEAAPEAVCBotBrain@@PEBVCBotDefinition@@@Z"));

    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (hK2) {
        ctx.ppNetResourceManager = reinterpret_cast<void**>(
            GetProcAddress(hK2, "?g_NetworkResourceManager@@3AEAVCNetworkResourceManager@@EA"));
        ctx.pfnSetNetString = reinterpret_cast<decltype(ctx.pfnSetNetString)>(
            GetProcAddress(hK2, "?SetString@CNetworkResourceManager@@QEAAXGAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@_N@Z"));
        ctx.pfnGetNetString = reinterpret_cast<decltype(ctx.pfnGetNetString)>(
            GetProcAddress(hK2, "?GetString@CNetworkResourceManager@@QEAAAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@G@Z"));
        ctx.ppHost = reinterpret_cast<void**>(
            GetProcAddress(hK2, "?g_pHost@@3PEAVCHost@@EA"));
        ctx.pfnGetHostServer = reinterpret_cast<decltype(ctx.pfnGetHostServer)>(
            GetProcAddress(hK2, "?GetServer@CHost@@QEAAPEAVCHostServer@@XZ"));
        ctx.pfnGetClient = reinterpret_cast<decltype(ctx.pfnGetClient)>(
            GetProcAddress(hK2, "?GetClient@CHostServer@@QEAAPEAVCClientConnection@@H@Z"));
        ctx.pfnKickClient = reinterpret_cast<decltype(ctx.pfnKickClient)>(
            GetProcAddress(hK2, "?KickClient@CHostServer@@QEAAXHW4EDisconnectReason@@@Z"));
        ctx.pfnQueueConsoleCommand = &EnqueueLiveWindowsServerConsoleCmdW;
    }

    if (!ctx.ppGame || !ctx.pfnRemoveBot) {
        return nullptr;
    }

    g_liveArcadeCtx = ctx;
    g_liveArcadeCtxResolved.store(true, std::memory_order_release);
    LogArcadeLine("[KONGOR QUEUE ARCADE] Resolved game_shared_x64.dll IGame::s_pGame + CPlayer::RemoveBot/AttachBot + k2_x64.dll CNetworkResourceManager exports.");
    return &g_liveArcadeCtx;
}

static bool ReadK2WStringAtConnNameSeh(const uint16_t* pConnId, wchar_t* outBuf, size_t outCap) noexcept {
    if (!pConnId || !outBuf || outCap == 0) return false;
    outBuf[0] = L'\0';
    __try {
        // Pseudo/bot connection cookie sentinel in FUN_1807254a0 is 0xDCD8 -> never a remote human handshake
        if (*pConnId == 0xDCD8u) {
            return false;
        }
        // In CClientConnection (FUN_180721e40 / FUN_1808e5c60):
        //   +0x20 = uint16_t unConnectionID (pConnId)
        //   +0x28 = K2::wstring m_sName     ((const uint8_t*)pConnId + 0x08)
        const uint8_t* rawStr = reinterpret_cast<const uint8_t*>(pConnId) + 0x08;
        const size_t wlen = *reinterpret_cast<const size_t*>(rawStr + 0x10);
        const size_t wcap = *reinterpret_cast<const size_t*>(rawStr + 0x18);
        if (wlen == 0 || wlen >= outCap || wcap < wlen || wcap > 256) {
            return false;
        }
        const wchar_t* wptr = (wcap > 7)
            ? *reinterpret_cast<const wchar_t* const*>(rawStr)
            : reinterpret_cast<const wchar_t*>(rawStr);
        if (!wptr) return false;

        for (size_t i = 0; i < wlen; ++i) {
            wchar_t ch = wptr[i];
            if (ch == L'\0') {
                outBuf[i] = L'\0';
                return i > 0;
            }
            outBuf[i] = ch;
        }
        outBuf[wlen] = L'\0';
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        outBuf[0] = L'\0';
        return false;
    }
}

static void UpsertReservationLocked(
    int32_t accountId,
    int32_t clientNum,
    int32_t teamId,
    const wchar_t* playerName,
    const wchar_t* savedBotName,
    void* detachedBotBrain,
    int32_t* outDisplacedClientNum,
    void** outDisplacedBotBrain
) noexcept {
    if (outDisplacedClientNum) *outDisplacedClientNum = -1;
    if (outDisplacedBotBrain) *outDisplacedBotBrain = nullptr;

    size_t freeIdx = kMaxArcadeReservations;
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_arcadeReservations[i].active) {
            if (g_arcadeReservations[i].accountId == accountId ||
                g_arcadeReservations[i].clientNum == clientNum ||
                (playerName && *playerName && NamesMatchPlayerW(g_arcadeReservations[i].playerName, playerName))) {
                freeIdx = i;
                break;
            }
        } else if (freeIdx == kMaxArcadeReservations) {
            freeIdx = i;
        }
    }
    if (freeIdx == kMaxArcadeReservations) {
        freeIdx = 0; // Overwrite oldest slot 0 if saturated
    }

    ArcadeReservationEntry& dst = g_arcadeReservations[freeIdx];
    if (dst.active && dst.parkedBotBrain != nullptr && dst.clientNum != clientNum) {
        if (outDisplacedClientNum) *outDisplacedClientNum = dst.clientNum;
        if (outDisplacedBotBrain) *outDisplacedBotBrain = dst.parkedBotBrain;
        dst.parkedBotBrain = nullptr;
    }

    const bool keepExistingSavedBot = dst.active && dst.clientNum == clientNum && dst.savedBotName[0] != L'\0';
    const bool keepExistingParkedBrain = dst.active && dst.clientNum == clientNum && dst.parkedBotBrain != nullptr && detachedBotBrain == nullptr;
    dst.active = true;
    dst.accountId = accountId;
    dst.clientNum = clientNum;
    dst.teamId = teamId;
    if (!keepExistingParkedBrain) {
        dst.parkedBotBrain = detachedBotBrain;
    }
    dst.playerName[0] = L'\0';
    if (playerName && *playerName) {
        wcsncpy_s(dst.playerName, _countof(dst.playerName), playerName, _TRUNCATE);
    }
    if (!keepExistingSavedBot) {
        dst.savedBotName[0] = L'\0';
        if (savedBotName && *savedBotName) {
            wcsncpy_s(dst.savedBotName, _countof(dst.savedBotName), savedBotName, _TRUNCATE);
        }
    }
}

static bool RemoveReservationByAccountLocked(
    int32_t accountId,
    int32_t* outClientNum,
    wchar_t* outDepartedPlayerName,
    size_t outDepartedPlayerCap,
    wchar_t* outSavedBotName,
    size_t outSavedBotCap,
    void** outParkedBotBrain
) noexcept {
    if (outDepartedPlayerName && outDepartedPlayerCap > 0) outDepartedPlayerName[0] = L'\0';
    if (outSavedBotName && outSavedBotCap > 0) outSavedBotName[0] = L'\0';
    if (outParkedBotBrain) *outParkedBotBrain = nullptr;
    bool removed = false;
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_arcadeReservations[i].active && g_arcadeReservations[i].accountId == accountId) {
            if (outClientNum) *outClientNum = g_arcadeReservations[i].clientNum;
            if (outDepartedPlayerName && outDepartedPlayerCap > 1 && g_arcadeReservations[i].playerName[0] != L'\0') {
                wcsncpy_s(outDepartedPlayerName, outDepartedPlayerCap, g_arcadeReservations[i].playerName, _TRUNCATE);
            }
            if (outSavedBotName && outSavedBotCap > 1 && g_arcadeReservations[i].savedBotName[0] != L'\0') {
                wcsncpy_s(outSavedBotName, outSavedBotCap, g_arcadeReservations[i].savedBotName, _TRUNCATE);
            }
            if (outParkedBotBrain && g_arcadeReservations[i].parkedBotBrain != nullptr) {
                *outParkedBotBrain = g_arcadeReservations[i].parkedBotBrain;
            }
            g_arcadeReservations[i] = {};
            removed = true;
        }
    }
    return removed;
}

static int __fastcall Hooked_CHostServer_GenerateClientID(
    void* pHostServer,
    uint16_t* pConnId,
    int32_t accountId
) {
    // satisfies: OBL-QA-03
    if (pHostServer != nullptr) {
        NotifyObservedArcadeHostServer(pHostServer);
    }
    if (IsQueueArcadeModeActive()) {
        const int32_t reservedClientNum = ResolveReservedArcadeClientId(pConnId, accountId);
        if (reservedClientNum >= 0) {
            if (pConnId && *pConnId == 0u) {
                // Assign deterministic non-zero session cookie so reconnects preserve identity
                uint16_t synthCookie = static_cast<uint16_t>(0x4B00u | (static_cast<uint16_t>(reservedClientNum) & 0x00FFu));
                if (synthCookie == 0xDCD8u) synthCookie = 0x4B01u;
                *pConnId = synthCookie;
            }
            char logBuf[256] = {};
            snprintf(logBuf, sizeof(logBuf),
                "[KONGOR QUEUE ARCADE] GenerateClientID intercepted -> bound incoming client (accountId=%d, cookie=0x%04X) to pre-reserved CPlayer slot clientNum=%d.",
                accountId, pConnId ? static_cast<unsigned>(*pConnId) : 0u, reservedClientNum);
            LogArcadeLine(logBuf);
            return reservedClientNum;
        }
    }
    if (g_origGenerateClientID) {
        return g_origGenerateClientID(pHostServer, pConnId, accountId);
    }
    return 0;
}

static void* __fastcall Hooked_CGameServer_AddBot(
    void* pGameServer,
    uint32_t uiTeam,
    void* pBotNameWStr,
    void* pPlayerNameWStr,
    uint8_t bSpawnImmediate,
    uint32_t uiTeamSlot,
    void* pOutResultCode
) {
    // satisfies: OBL-QA-01
    if (!g_origGameServerAddBot) {
        return nullptr;
    }
    if (!IsQueueArcadeModeActive() || !pGameServer) {
        return g_origGameServerAddBot(
            pGameServer, uiTeam, pBotNameWStr, pPlayerNameWStr,
            bSpawnImmediate, uiTeamSlot, pOutResultCode);
    }

    // Party/Arcade maps (soccer='soccerpick', riftwars='normal', arena='normal', pebbles_duel='normal')
    // omit 'botmatch' from worldconfig sv_worldMapGameModes to preserve their custom map mechanics.
    // Temporarily stamp m_pGameInfo->m_eGameMode (+0xD4) = 0x0B (GAME_MODE_BOTMATCH) during AddBot on the
    // single-threaded server tick so CGameServer::AddBot populates all connectionless slots cleanly.
    int32_t* pGameMode = nullptr;
    int32_t savedGameMode = 0x0B;
    __try {
        const uintptr_t pGameInfo = *reinterpret_cast<const uintptr_t*>(reinterpret_cast<uintptr_t>(pGameServer) + 0xB0);
        if (pGameInfo != 0) {
            int32_t* candidateMode = reinterpret_cast<int32_t*>(pGameInfo + 0xD4);
            const int32_t currentMode = *candidateMode;
            if (currentMode != 0x0B) {
                savedGameMode = currentMode;
                *candidateMode = 0x0B;
                pGameMode = candidateMode;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        pGameMode = nullptr;
    }

    void* result = g_origGameServerAddBot(
        pGameServer, uiTeam, pBotNameWStr, pPlayerNameWStr,
        bSpawnImmediate, uiTeamSlot, pOutResultCode);

    if (pGameMode != nullptr) {
        __try {
            *pGameMode = savedGameMode;
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    return result;
}

static QueueArcadeSlotReservationResult ArcadeReserveSlotSeh(
    const QueueArcadeEngineContext* ctx,
    int32_t accountId,
    int32_t preferredTeam,
    const wchar_t* playerName,
    const wchar_t* explicitClanTag,
    wchar_t* outFullDisplayName,
    size_t fullDisplayCap
) noexcept {
    // satisfies: OBL-QA-02
    // satisfies: OBL-QA-06
    // satisfies: OBL-QA-07
    if (outFullDisplayName && fullDisplayCap > 0) outFullDisplayName[0] = L'\0';
    QueueArcadeSlotReservationResult res = {};
    res.clientNum = -1;
    res.accountId = accountId;

    if (!ctx || !ctx->ppGame) {
        snprintf(res.reason, sizeof(res.reason), "missing_engine_context");
        return res;
    }
    if (accountId <= 0) {
        snprintf(res.reason, sizeof(res.reason), "invalid_account_id");
        return res;
    }

    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) {
            snprintf(res.reason, sizeof(res.reason), "null_igame_world");
            return res;
        }

        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (!head || *reinterpret_cast<const uint8_t*>(head + 0x19) == 0) {
            snprintf(res.reason, sizeof(res.reason), "empty_client_map_head");
            return res;
        }

        void* const pHostServer = ResolveEffectiveHostServerSeh(ctx);
        bool loggedSkippedSlot[kMaxArcadeReservations] = {};

        void* existingSameAccount = nullptr;
        void* bestPreferredSlot = nullptr;
        void* bestFallbackSlot = nullptr;
        uint32_t activeHumansTeam1 = 0;
        uint32_t activeHumansTeam2 = 0;

        uintptr_t node = *reinterpret_cast<const uintptr_t*>(head + 0x00);
        for (uint32_t step = 0; step < 128 && node && node != head; ++step) {
            if (*reinterpret_cast<const uint8_t*>(node + 0x19) != 0) {
                break;
            }

            void* pPlayer = *reinterpret_cast<void* const*>(node + 0x28);
            if (pPlayer) {
                const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                const uint16_t flags = *reinterpret_cast<const uint16_t*>(addr + 0x3DE);
                const int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + 0x2D0);
                const int32_t slotClientNum = *reinterpret_cast<const int32_t*>(addr + 0xA8);
                const int32_t slotTeam = *reinterpret_cast<const int32_t*>(addr + 0xAC);
                const uint8_t slotIsBot = *reinterpret_cast<const uint8_t*>(addr + 0x66B);

                if ((flags & 0x0080u) == 0) { // Non-terminated
                    if (slotAccId == accountId) {
                        existingSameAccount = pPlayer;
                    } else if (slotIsBot == 0 && slotAccId > 0) {
                        if (slotTeam == 1) ++activeHumansTeam1;
                        else if (slotTeam == 2) ++activeHumansTeam2;
                    } else if ((flags & 0x0200u) != 0 && slotAccId <= 0 && (slotTeam == 1 || slotTeam == 2)) {
                        const bool hasLingeringUdp = (pHostServer != nullptr && ctx->pfnGetClient != nullptr &&
                            slotClientNum >= 0 && ctx->pfnGetClient(pHostServer, slotClientNum) != nullptr);
                        if (hasLingeringUdp) {
                            if (slotClientNum >= 0 && static_cast<size_t>(slotClientNum) < kMaxArcadeReservations &&
                                !loggedSkippedSlot[slotClientNum]) {
                                loggedSkippedSlot[slotClientNum] = true;
                                char skipLog[192] = {};
                                snprintf(skipLog, sizeof(skipLog),
                                    "[KONGOR QUEUE ARCADE] [SLOT_GUARD] Skipping slot with lingering UDP CClientConnection (clientNum=%d, team=%d)",
                                    slotClientNum, slotTeam);
                                LogArcadeLine(skipLog);
                            }
                        } else {
                            if (preferredTeam == slotTeam && !bestPreferredSlot) {
                                bestPreferredSlot = pPlayer;
                            }
                            if (!bestFallbackSlot) {
                                bestFallbackSlot = pPlayer;
                            }
                        }
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

        // Auto-balance team selection if caller passed preferredTeam == 0
        if (!existingSameAccount && !bestPreferredSlot && preferredTeam == 0) {
            const int32_t balancedTeam = (activeHumansTeam1 <= activeHumansTeam2) ? 1 : 2;
            uintptr_t scanNode = *reinterpret_cast<const uintptr_t*>(head + 0x00);
            for (uint32_t step = 0; step < 128 && scanNode && scanNode != head; ++step) {
                if (*reinterpret_cast<const uint8_t*>(scanNode + 0x19) != 0) break;
                void* pPlayer = *reinterpret_cast<void* const*>(scanNode + 0x28);
                if (pPlayer) {
                    const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                    const uint16_t flags = *reinterpret_cast<const uint16_t*>(addr + 0x3DE);
                    const int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + 0x2D0);
                    const int32_t slotClientNum = *reinterpret_cast<const int32_t*>(addr + 0xA8);
                    const int32_t slotTeam = *reinterpret_cast<const int32_t*>(addr + 0xAC);
                    if ((flags & 0x0080u) == 0 && (flags & 0x0200u) != 0 && slotAccId <= 0 && slotTeam == balancedTeam) {
                        const bool hasLingeringUdp = (pHostServer != nullptr && ctx->pfnGetClient != nullptr &&
                            slotClientNum >= 0 && ctx->pfnGetClient(pHostServer, slotClientNum) != nullptr);
                        if (hasLingeringUdp) {
                            if (slotClientNum >= 0 && static_cast<size_t>(slotClientNum) < kMaxArcadeReservations &&
                                !loggedSkippedSlot[slotClientNum]) {
                                loggedSkippedSlot[slotClientNum] = true;
                                char skipLog[192] = {};
                                snprintf(skipLog, sizeof(skipLog),
                                    "[KONGOR QUEUE ARCADE] [SLOT_GUARD] Skipping slot with lingering UDP CClientConnection (clientNum=%d, team=%d)",
                                    slotClientNum, slotTeam);
                                LogArcadeLine(skipLog);
                            }
                        } else {
                            bestPreferredSlot = pPlayer;
                            break;
                        }
                    }
                }
                const uintptr_t right = *reinterpret_cast<const uintptr_t*>(scanNode + 0x10);
                if (right && *reinterpret_cast<const uint8_t*>(right + 0x19) == 0) {
                    scanNode = right;
                    for (uint32_t sub = 0; sub < 64; ++sub) {
                        const uintptr_t left = *reinterpret_cast<const uintptr_t*>(scanNode + 0x00);
                        if (!left || *reinterpret_cast<const uint8_t*>(left + 0x19) != 0) break;
                        scanNode = left;
                    }
                } else {
                    uintptr_t parent = *reinterpret_cast<const uintptr_t*>(scanNode + 0x08);
                    for (uint32_t sub = 0; sub < 64 && parent && *reinterpret_cast<const uint8_t*>(parent + 0x19) == 0; ++sub) {
                        if (scanNode != *reinterpret_cast<const uintptr_t*>(parent + 0x10)) break;
                        scanNode = parent;
                        parent = *reinterpret_cast<const uintptr_t*>(parent + 0x08);
                    }
                    scanNode = parent;
                }
            }
        }

        void* chosen = existingSameAccount ? existingSameAccount
            : (bestPreferredSlot ? bestPreferredSlot : bestFallbackSlot);
        if (!chosen) {
            snprintf(res.reason, sizeof(res.reason), "no_available_connectionless_slot");
            return res;
        }

        const uintptr_t chosenAddr = reinterpret_cast<uintptr_t>(chosen);
        const uint16_t priorFlags = *reinterpret_cast<const uint16_t*>(chosenAddr + 0x3DE);
        const uint8_t priorIsBot = *reinterpret_cast<const uint8_t*>(chosenAddr + 0x66B);
        const int32_t priorAccId = *reinterpret_cast<const int32_t*>(chosenAddr + 0x2D0);
        res.priorFlags3DE = priorFlags;
        res.priorPostInit668 = *reinterpret_cast<const uint8_t*>(chosenAddr + 0x668);
        res.priorDiscTerm438 = *reinterpret_cast<const uint32_t*>(chosenAddr + 0x438);

        void* pBotBrain = *reinterpret_cast<void* const*>(chosenAddr + 0x730);
        if (pBotBrain != nullptr) {
            const bool parkForReuse = (GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots);
            if (parkForReuse) {
                // Mode 1 (ActiveBots): Park m_pBotBrain aside without destroying its Lua state so ArcadeReleaseSlot
                // restores live AI control in O(1) when the human leaves for Ranked or clicks Leave Game.
                // satisfies: OBL-QA-10
                res.detachedBotBrain = pBotBrain;
            } else {
                if (ctx->pfnRemoveBot != nullptr) {
                    ctx->pfnRemoveBot(chosen);
                }
                res.detachedBotBrain = nullptr;
            }
            // CPlayer::RemoveBot (game_shared_x64.dll+0x4F62E0) deletes m_pBotBrain (+0x730) WITHOUT nulling +0x730!
            // Unconditionally zero +0x730 and clear 0x0048 (+0x3E0) so CGameServer::BotFrame never collects a freed/parked CBotBrain*.
            // satisfies: OBL-QA-06
            *reinterpret_cast<void**>(chosenAddr + 0x730) = nullptr;
            uint16_t* pBotFlags = reinterpret_cast<uint16_t*>(chosenAddr + 0x3E0);
            *pBotFlags = static_cast<uint16_t>(*pBotFlags & ~0x0048u);
            res.removedBotBrain = true;
        }

        // Snapshot prior bot display name before overwriting CNetworkResourceManager (+0x2D8)
        const uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(chosenAddr + 0x2D8);
        const uint16_t clanIdx = *reinterpret_cast<const uint16_t*>(chosenAddr + 0x2DA);
        const uint16_t rankIdx = *reinterpret_cast<const uint16_t*>(chosenAddr + 0x2DC);
        if (((priorFlags & 0x0200u) != 0 || priorIsBot != 0) && priorAccId <= 0 &&
            ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
            ctx->pfnGetNetString && nameIdx != 0xFFFFu) {
            const void* pExistingName = ctx->pfnGetNetString(*ctx->ppNetResourceManager, nameIdx);
            ReadK2WStringFromPtrSeh(pExistingName, res.previousBotName, _countof(res.previousBotName));
        }

        // Clear connectionless (0x0200), disconnected/was-disconnected (0x0101), terminated (0x0080),
        // loading (0x0040), AFK (0x8000), and prior-player MatchmakingPostInitialize upgrade flags (0x3014).
        // satisfies: OBL-QA-08
        uint16_t* pFlags = reinterpret_cast<uint16_t*>(chosenAddr + 0x3DE);
        *pFlags = static_cast<uint16_t>(*pFlags & ~(0x0200u | 0x0101u | 0x0080u | 0x0040u | 0x8000u | 0x3014u));
        uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(chosenAddr + 0x3E0);
        *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0020u);

        // Reset ping (+0x420) and disconnect grace timers (+0x430/+0x434/+0x438) so ServerFrame updates live Ping
        // and never runs a residual disconnect countdown on the newly reserved human slot.
        *reinterpret_cast<uint16_t*>(chosenAddr + 0x420) = 0u;
        *reinterpret_cast<uint32_t*>(chosenAddr + 0x430) = 0xFFFFFFFFu;
        *reinterpret_cast<uint32_t*>(chosenAddr + 0x434) = 0u;
        *reinterpret_cast<uint32_t*>(chosenAddr + 0x438) = 0xFFFFFFFFu;

        // Clear m_bIsBot (+0x66B), override-account (+0x51A), and client flags (+0x660); stamp m_iAccountID (+0x2D0).
        // Keep m_bMatchmakingPostInitialized (+0x668) = 1 so EndHeroLoading (game_x64.dll+0x7E530, Phase 4 -> 5)
        // never calls CPlayer::Disconnected on the human slot when sv_precreateArrangedPlayers == 0.
        // shortcut: keeping +0x668=1 with sv_precreateArrangedPlayers==0 skips CPlayer::MatchmakingPostInitialize (cosmetic AssignAndUpdateUpgrades), upgrade to exported MatchmakingPostInitialize call in AddClient if human cosmetics/taunts are desired in Queue Arcade
        // satisfies: OBL-QA-07
        // satisfies: OBL-QA-08
        *reinterpret_cast<uint8_t*>(chosenAddr + 0x66B) = 0;
        *reinterpret_cast<uint8_t*>(chosenAddr + 0x51A) = 0;
        *reinterpret_cast<uint32_t*>(chosenAddr + 0x660) = 0;
        *reinterpret_cast<uint8_t*>(chosenAddr + 0x668) = 1;
        *reinterpret_cast<int32_t*>(chosenAddr + 0x2D0) = accountId;

        res.postFlags3DE = *pFlags;
        res.postInit668 = *reinterpret_cast<const uint8_t*>(chosenAddr + 0x668);
        res.postDiscTerm438 = *reinterpret_cast<const uint32_t*>(chosenAddr + 0x438);

        wchar_t barePlayerName[64] = {};
        wchar_t bareClanTag[32] = {};
        SplitDisplayNameAndClanTagW(
            playerName, explicitClanTag,
            barePlayerName, _countof(barePlayerName),
            bareClanTag, _countof(bareClanTag));
        if (barePlayerName[0] != L'\0') {
            wchar_t fullDisplayName[96] = {};
            if (bareClanTag[0] != L'\0') {
                _snwprintf_s(fullDisplayName, _countof(fullDisplayName), _TRUNCATE, L"[%s]%s", bareClanTag, barePlayerName);
            } else {
                wcsncpy_s(fullDisplayName, _countof(fullDisplayName), barePlayerName, _TRUNCATE);
            }
            if (outFullDisplayName && fullDisplayCap > 1) {
                wcsncpy_s(outFullDisplayName, fullDisplayCap, fullDisplayName, _TRUNCATE);
            }
            // Per CPlayer::Initialize (game_shared_x64.dll + 0x4f6a40), m_unNameIndex (+0x2D8) stores
            // CClientConnection::m_sName ("[TAG]Name" when in a clan) while +0x2DA and +0x2DC are TSNULL (L"").
            const bool setPrimary = SetPlayerNetStringSlotSeh(ctx, nameIdx, fullDisplayName);
            SetPlayerNetStringSlotSeh(ctx, clanIdx, L"");
            SetPlayerNetStringSlotSeh(ctx, rankIdx, L"");
            res.updatedNetStrings = setPrimary;
        }

        res.clientNum = *reinterpret_cast<const int32_t*>(chosenAddr + 0xA8);
        res.teamId = *reinterpret_cast<const int32_t*>(chosenAddr + 0xAC);
        res.success = true;
        snprintf(res.reason, sizeof(res.reason), existingSameAccount ? "reused_existing_slot" : "reserved_bot_slot");
        return res;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        res.success = false;
        res.clientNum = -1;
        snprintf(res.reason, sizeof(res.reason), "seh_access_violation");
        return res;
    }
}

static bool FlushParkedBotBrainOnPlayerSeh(
    const QueueArcadeEngineContext* ctx,
    void* pPlayer,
    void* parkedBotBrain
) noexcept {
    // satisfies: OBL-QA-10
    if (!ctx || !ctx->pfnRemoveBot || !pPlayer || !parkedBotBrain) {
        return false;
    }
    __try {
        const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
        if (*reinterpret_cast<void* const*>(addr + 0x730) == nullptr) {
            *reinterpret_cast<void**>(addr + 0x730) = parkedBotBrain;
        }
        ctx->pfnRemoveBot(pPlayer);
        *reinterpret_cast<void**>(addr + 0x730) = nullptr;
        uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + 0x3E0);
        *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0048u);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool FlushParkedBotBrainForClientSeh(
    const QueueArcadeEngineContext* ctx,
    int32_t clientNum,
    void* parkedBotBrain
) noexcept {
    // satisfies: OBL-QA-10
    if (!ctx || !ctx->ppGame || !ctx->pfnRemoveBot || clientNum < 0 || !parkedBotBrain) {
        return false;
    }
    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) return false;
        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (!head || *reinterpret_cast<const uint8_t*>(head + 0x19) == 0) {
            return false;
        }

        uintptr_t node = *reinterpret_cast<const uintptr_t*>(head + 0x00);
        for (uint32_t step = 0; step < 128 && node && node != head; ++step) {
            if (*reinterpret_cast<const uint8_t*>(node + 0x19) != 0) break;

            void* pPlayer = *reinterpret_cast<void* const*>(node + 0x28);
            if (pPlayer) {
                const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                if (*reinterpret_cast<const int32_t*>(addr + 0xA8) == clientNum) {
                    return FlushParkedBotBrainOnPlayerSeh(ctx, pPlayer, parkedBotBrain);
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
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool ArcadeReleaseSlotSeh(
    const QueueArcadeEngineContext* ctx,
    int32_t accountId,
    int32_t expectedClientNum,
    const wchar_t* restoreBotName,
    const ArcadeSlotOriginEntry* originsSnapshot,
    void* parkedBotBrain,
    int32_t* outReleasedClientNum,
    uint16_t* outPriorFlags3DE,
    uint16_t* outPostFlags3DE,
    bool* outRestoredBotBrain,
    wchar_t* ioDepartedHumanName,
    size_t departedHumanCap,
    wchar_t* outEffectiveBotName,
    size_t effectiveBotCap
) noexcept {
    // satisfies: OBL-QA-04
    // satisfies: OBL-QA-07
    // satisfies: OBL-QA-08
    // satisfies: OBL-QA-10
    if (outReleasedClientNum) *outReleasedClientNum = -1;
    if (outPriorFlags3DE) *outPriorFlags3DE = 0;
    if (outPostFlags3DE) *outPostFlags3DE = 0;
    if (outRestoredBotBrain) *outRestoredBotBrain = false;
    if (outEffectiveBotName && effectiveBotCap > 0) outEffectiveBotName[0] = L'\0';
    if (!ctx || !ctx->ppGame) return false;

    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) return false;

        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (!head || *reinterpret_cast<const uint8_t*>(head + 0x19) == 0) {
            return false;
        }

        bool updatedAny = false;
        uintptr_t node = *reinterpret_cast<const uintptr_t*>(head + 0x00);
        for (uint32_t step = 0; step < 128 && node && node != head; ++step) {
            if (*reinterpret_cast<const uint8_t*>(node + 0x19) != 0) break;

            void* pPlayer = *reinterpret_cast<void* const*>(node + 0x28);
            if (pPlayer) {
                const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                const int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + 0x2D0);
                const int32_t slotClientNum = *reinterpret_cast<const int32_t*>(addr + 0xA8);
                if ((accountId > 0 && slotAccId == accountId) ||
                    (expectedClientNum >= 0 && slotClientNum == expectedClientNum)) {
                    const uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(addr + 0x2D8);
                    const uint16_t clanIdx = *reinterpret_cast<const uint16_t*>(addr + 0x2DA);
                    const uint16_t rankIdx = *reinterpret_cast<const uint16_t*>(addr + 0x2DC);

                    if (ioDepartedHumanName && departedHumanCap > 1 && ioDepartedHumanName[0] == L'\0' &&
                        ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
                        ctx->pfnGetNetString && nameIdx != 0xFFFFu) {
                        const void* pExistingHuman = ctx->pfnGetNetString(*ctx->ppNetResourceManager, nameIdx);
                        ReadK2WStringFromPtrSeh(pExistingHuman, ioDepartedHumanName, departedHumanCap);
                    }

                    // Never delete CPlayer and never set 0x0080 (terminated)!
                    // Clear m_iAccountID (+0x2D0), disconnect/loading/AFK/upgrade flags (0x0101 | 0x0040 | 0x8000 | 0x3014),
                    // reset disconnect timers (+0x430/+0x434/+0x438) so ServerFrame never terminates the restored bot 4 min later,
                    // keep +0x668 = 1, and restore m_bIsBot (+0x66B = 1) + original bot name (+0x2D8) + 0x0200 connectionless flag.
                    *reinterpret_cast<int32_t*>(addr + 0x2D0) = 0;
                    *reinterpret_cast<uint8_t*>(addr + 0x51A) = 0;
                    *reinterpret_cast<uint32_t*>(addr + 0x660) = 0;
                    *reinterpret_cast<uint8_t*>(addr + 0x668) = 1;
                    *reinterpret_cast<uint8_t*>(addr + 0x66B) = 1;
                    *reinterpret_cast<uint16_t*>(addr + 0x420) = 0u;
                    *reinterpret_cast<uint32_t*>(addr + 0x430) = 0xFFFFFFFFu;
                    *reinterpret_cast<uint32_t*>(addr + 0x434) = 0u;
                    *reinterpret_cast<uint32_t*>(addr + 0x438) = 0xFFFFFFFFu;

                    uint16_t* pFlags = reinterpret_cast<uint16_t*>(addr + 0x3DE);
                    const uint16_t beforeFlags = *pFlags;
                    *pFlags = static_cast<uint16_t>((beforeFlags | 0x0200u) & ~(0x0101u | 0x0080u | 0x0040u | 0x8000u | 0x3014u));
                    uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + 0x3E0);
                    *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0020u);

                    const uint32_t heroIdx = *reinterpret_cast<const uint32_t*>(addr + 0xB0);
                    const bool validOriginIdx = (originsSnapshot != nullptr && slotClientNum >= 0 &&
                        static_cast<size_t>(slotClientNum) < kMaxArcadeReservations);
                    const void* originBotDef = validOriginIdx ? originsSnapshot[slotClientNum].botDef : nullptr;

                    if (parkedBotBrain != nullptr) {
                        if (GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots &&
                            *reinterpret_cast<void* const*>(addr + 0x730) == nullptr) {
                            *reinterpret_cast<void**>(addr + 0x730) = parkedBotBrain;
                            *reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(parkedBotBrain) + 0x10) = heroIdx;
                            *pFlags2 = static_cast<uint16_t>(*pFlags2 | 0x0008u);
                            if (outRestoredBotBrain) *outRestoredBotBrain = true;
                        } else {
                            FlushParkedBotBrainOnPlayerSeh(ctx, pPlayer, parkedBotBrain);
                        }
                        parkedBotBrain = nullptr; // Consumed on matched player slot
                    } else if (GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots &&
                               *reinterpret_cast<void* const*>(addr + 0x730) == nullptr &&
                               originBotDef != nullptr &&
                               ctx->pfnAttachBot != nullptr) {
                        void* healedBrain = ctx->pfnAttachBot(pPlayer, originBotDef);
                        if (healedBrain == nullptr) {
                            healedBrain = *reinterpret_cast<void* const*>(addr + 0x730);
                        }
                        if (healedBrain != nullptr) {
                            *reinterpret_cast<void**>(addr + 0x730) = healedBrain;
                            *reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(healedBrain) + 0x10) = heroIdx;
                            *pFlags2 = static_cast<uint16_t>(*pFlags2 | 0x0008u);
                            if (outRestoredBotBrain) *outRestoredBotBrain = true;
                            char healLog[256] = {};
                            snprintf(healLog, sizeof(healLog),
                                "[KONGOR QUEUE ARCADE] [ATTACH_BOT_SELF_HEAL] Re-attached CBotBrain=%p via CPlayer::AttachBot(clientNum=%d, botDef=%p, heroIdx=%u)",
                                healedBrain, slotClientNum, originBotDef, heroIdx);
                            LogArcadeLine(healLog);
                        }
                    }

                    if (outPriorFlags3DE) *outPriorFlags3DE = beforeFlags;
                    if (outPostFlags3DE) *outPostFlags3DE = *pFlags;

                    const wchar_t* originBotLabel = (validOriginIdx && originsSnapshot[slotClientNum].originalBotName[0] != L'\0')
                        ? originsSnapshot[slotClientNum].originalBotName
                        : nullptr;
                    const wchar_t* botLabel = originBotLabel ? originBotLabel
                        : ((restoreBotName && *restoreBotName) ? restoreBotName : L"ArcadeBot");
                    if (outEffectiveBotName && effectiveBotCap > 1) {
                        wcsncpy_s(outEffectiveBotName, effectiveBotCap, botLabel, _TRUNCATE);
                    }
                    SetPlayerNetStringSlotSeh(ctx, nameIdx, botLabel);
                    SetPlayerNetStringSlotSeh(ctx, clanIdx, L"");
                    SetPlayerNetStringSlotSeh(ctx, rankIdx, L"");

                    if (outReleasedClientNum) *outReleasedClientNum = slotClientNum;
                    updatedAny = true;
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
        return updatedAny;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool KickReleasedClientIfConnectedSeh(
    const QueueArcadeEngineContext* ctx,
    int32_t accountId,
    int32_t releasedClientNum
) noexcept {
    if (!ctx || releasedClientNum < 0 || !ctx->pfnGetClient || !ctx->pfnKickClient) {
        return false;
    }
    void* pHostServer = ResolveEffectiveHostServerSeh(ctx);
    if (!pHostServer) {
        return false;
    }

    bool kicked = false;
    __try {
        void* pLiveConn = ctx->pfnGetClient(pHostServer, releasedClientNum);
        if (pLiveConn != nullptr) {
            ++t_arcadeKickReentrancyDepth;
            g_arcadeKickInProgressCount.fetch_add(1, std::memory_order_acq_rel);
            __try {
                ctx->pfnKickClient(pHostServer, releasedClientNum, 0);
                kicked = true;
            } __finally {
                g_arcadeKickInProgressCount.fetch_sub(1, std::memory_order_acq_rel);
                --t_arcadeKickReentrancyDepth;
            }
            char kickLog[256] = {};
            snprintf(kickLog, sizeof(kickLog),
                "[KONGOR QUEUE ARCADE] Kicked lingering UDP CClientConnection on ArcadeReleaseSlot(accountId=%d, clientNum=%d, conn=%p).",
                accountId, releasedClientNum, pLiveConn);
            LogArcadeLine(kickLog);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogArcadeLine("[KONGOR QUEUE ARCADE] WARN: SEH exception caught inside KickReleasedClientIfConnectedSeh.");
    }
    return kicked;
}

static uint32_t ArcadeParkUnreservedBotsSeh(const QueueArcadeEngineContext* ctx) noexcept {
    // satisfies: OBL-QA-04
    // satisfies: OBL-QA-06
    if (!ctx || !ctx->ppGame) return 0;

    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) return 0;

        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (!head || *reinterpret_cast<const uint8_t*>(head + 0x19) == 0) {
            return 0;
        }

        uint32_t parkedCount = 0;
        uintptr_t node = *reinterpret_cast<const uintptr_t*>(head + 0x00);
        for (uint32_t step = 0; step < 128 && node && node != head; ++step) {
            if (*reinterpret_cast<const uint8_t*>(node + 0x19) != 0) break;

            void* pPlayer = *reinterpret_cast<void* const*>(node + 0x28);
            if (pPlayer) {
                const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
                const uint16_t flags = *reinterpret_cast<const uint16_t*>(addr + 0x3DE);
                const int32_t slotAccId = *reinterpret_cast<const int32_t*>(addr + 0x2D0);
                void* pBotBrain = *reinterpret_cast<void* const*>(addr + 0x730);
                if ((flags & 0x0200u) != 0 && slotAccId <= 0 && pBotBrain != nullptr) {
                    if (ctx->pfnRemoveBot) {
                        ctx->pfnRemoveBot(pPlayer);
                    }
                    // Unconditionally null +0x730 and clear 0x0048 (+0x3E0) even after pfnRemoveBot!
                    *reinterpret_cast<void**>(addr + 0x730) = nullptr;
                    uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + 0x3E0);
                    *pFlags2 = static_cast<uint16_t>(*pFlags2 & ~0x0048u);
                    ++parkedCount;
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
        return parkedCount;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

} // anonymous namespace

QueueArcadeServerMode GetQueueArcadeMode() noexcept {
    int32_t cur = g_arcadeMode.load(std::memory_order_acquire);
    if (cur >= 0) {
        return static_cast<QueueArcadeServerMode>(cur);
    }

    char envBuf[16] = {};
    DWORD envLen = GetEnvironmentVariableA("KONGOR_QUEUE_ARCADE", envBuf, sizeof(envBuf));
    int32_t resolved = 0;
    if (envLen > 0) {
        if (envBuf[0] == '2') {
            resolved = 2;
        } else if (envBuf[0] == '1' || envBuf[0] == 'y' || envBuf[0] == 'Y' || envBuf[0] == 't' || envBuf[0] == 'T') {
            resolved = 1;
        }
    }

    int32_t expected = -1;
    if (g_arcadeMode.compare_exchange_strong(expected, resolved, std::memory_order_acq_rel)) {
        return static_cast<QueueArcadeServerMode>(resolved);
    }
    return static_cast<QueueArcadeServerMode>(expected);
}

bool IsQueueArcadeModeActive() noexcept {
    return GetQueueArcadeMode() != QueueArcadeServerMode::Disabled;
}

void SetQueueArcadeMode(QueueArcadeServerMode mode) noexcept {
    const int32_t clamped = (mode == QueueArcadeServerMode::PartyDormantSlots) ? 2
        : (mode == QueueArcadeServerMode::ActiveBots ? 1 : 0);
    const int32_t prev = g_arcadeMode.exchange(clamped, std::memory_order_acq_rel);
    if (clamped == 0 || prev <= 0) {
        AcquireSRWLockExclusive(&g_arcadeReservationLock);
        ResetArcadeSlotOriginsLocked();
        ReleaseSRWLockExclusive(&g_arcadeReservationLock);
    }

    char logBuf[192] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] SetQueueArcadeMode -> %d (%s)",
        clamped,
        clamped == 2 ? "PartyDormantSlots" : (clamped == 1 ? "ActiveBots" : "Disabled"));
    LogArcadeLine(logBuf);

    if (clamped > 0) {
        EnsureQueueArcadeK2HooksInstalled();
        if (clamped == 2) {
            ArcadeParkUnreservedBots(nullptr);
        }
    }
}

void RefreshQueueArcadeSwapHooksFromEnv() noexcept {
    char envBuf[32] = {};
    const DWORD envLen = GetEnvironmentVariableA("KONGOR_ARCADE_SWAP_HOOKS", envBuf, sizeof(envBuf));
    int32_t resolved = 1; // Default enabled
    if (envLen > 0 && envLen < sizeof(envBuf)) {
        if (_stricmp(envBuf, "0") == 0 ||
            _stricmp(envBuf, "false") == 0 ||
            _stricmp(envBuf, "no") == 0 ||
            _stricmp(envBuf, "off") == 0) {
            resolved = 0;
        }
    }
    g_arcadeSwapHooksEnabled.store(resolved, std::memory_order_release);
    char logBuf[192] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] KONGOR_ARCADE_SWAP_HOOKS resolved -> %d (%s)",
        resolved, resolved ? "enabled" : "disabled_stock_fallback");
    LogArcadeLine(logBuf);
}

bool IsQueueArcadeSwapHooksEnabled() noexcept {
    int32_t cur = g_arcadeSwapHooksEnabled.load(std::memory_order_acquire);
    if (cur >= 0) {
        return cur != 0;
    }
    RefreshQueueArcadeSwapHooksFromEnv();
    return g_arcadeSwapHooksEnabled.load(std::memory_order_acquire) != 0;
}

void SetQueueArcadeSwapHooksEnabled(bool enabled) noexcept {
    const int32_t val = enabled ? 1 : 0;
    g_arcadeSwapHooksEnabled.store(val, std::memory_order_release);
    char logBuf[192] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] SetQueueArcadeSwapHooksEnabled -> %d (%s)",
        val, enabled ? "enabled" : "disabled_stock_fallback");
    LogArcadeLine(logBuf);
}

void NotifyObservedArcadeHostServer(void* pHostServer) noexcept {
    if (pHostServer != nullptr) {
        g_observedArcadeHostServer.store(pHostServer, std::memory_order_release);
    }
}

bool IsArcadeKickReentrancyActive() noexcept {
    return t_arcadeKickReentrancyDepth > 0 ||
           g_arcadeKickInProgressCount.load(std::memory_order_acquire) > 0;
}

void RecordArcadeSlotOrigin(
    int32_t clientNum,
    const void* pBotDef,
    const wchar_t* originalBotName
) noexcept {
    if (clientNum < 0 || static_cast<size_t>(clientNum) >= kMaxArcadeReservations) {
        return;
    }
    AcquireSRWLockExclusive(&g_arcadeReservationLock);
    ArcadeSlotOriginEntry& entry = g_arcadeSlotOrigins[clientNum];
    if (pBotDef != nullptr) {
        entry.botDef = pBotDef;
        entry.populated = true;
    }
    if (originalBotName && *originalBotName && entry.originalBotName[0] == L'\0') {
        wcsncpy_s(entry.originalBotName, _countof(entry.originalBotName), originalBotName, _TRUNCATE);
        entry.populated = true;
    }
    ReleaseSRWLockExclusive(&g_arcadeReservationLock);
}

bool GetArcadeSlotOrigin(
    int32_t clientNum,
    const void** outBotDef,
    wchar_t* outOriginalBotName,
    size_t nameCap
) noexcept {
    if (outBotDef) *outBotDef = nullptr;
    if (outOriginalBotName && nameCap > 0) outOriginalBotName[0] = L'\0';
    if (clientNum < 0 || static_cast<size_t>(clientNum) >= kMaxArcadeReservations) {
        return false;
    }
    AcquireSRWLockShared(&g_arcadeReservationLock);
    const ArcadeSlotOriginEntry& entry = g_arcadeSlotOrigins[clientNum];
    const bool populated = entry.populated;
    if (outBotDef) *outBotDef = entry.botDef;
    if (outOriginalBotName && nameCap > 1 && entry.originalBotName[0] != L'\0') {
        wcsncpy_s(outOriginalBotName, nameCap, entry.originalBotName, _TRUNCATE);
    }
    ReleaseSRWLockShared(&g_arcadeReservationLock);
    return populated;
}

static bool ReadPlayerAttachBotSnapshotSeh(
    void* pPlayer,
    const QueueArcadeEngineContext* ctx,
    int32_t* outClientNum,
    uint8_t* outIsBot,
    int32_t* outAccountId,
    wchar_t* outBotName,
    size_t botNameCap
) noexcept {
    if (outClientNum) *outClientNum = -1;
    if (outIsBot) *outIsBot = 0;
    if (outAccountId) *outAccountId = 0;
    if (outBotName && botNameCap > 0) outBotName[0] = L'\0';
    if (!pPlayer) return false;
    __try {
        const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
        const int32_t clientNum = *reinterpret_cast<const int32_t*>(addr + 0xA8);
        const uint8_t isBot = *reinterpret_cast<const uint8_t*>(addr + 0x66B);
        const int32_t accId = *reinterpret_cast<const int32_t*>(addr + 0x2D0);
        const uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(addr + 0x2D8);
        if (outClientNum) *outClientNum = clientNum;
        if (outIsBot) *outIsBot = isBot;
        if (outAccountId) *outAccountId = accId;
        if (outBotName && botNameCap > 1 && accId <= 0 &&
            ctx && ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
            ctx->pfnGetNetString && nameIdx != 0xFFFFu) {
            const void* pStr = ctx->pfnGetNetString(*ctx->ppNetResourceManager, nameIdx);
            ReadK2WStringFromPtrSeh(pStr, outBotName, botNameCap);
        }
        return clientNum >= 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void OnArcadePlayerAttachBot(
    void* pPlayer,
    const void* pBotDef,
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer || !pBotDef) return;
    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    int32_t clientNum = -1;
    uint8_t isBot = 0;
    int32_t accId = 0;
    wchar_t botName[64] = {};
    if (!ReadPlayerAttachBotSnapshotSeh(pPlayer, ctx, &clientNum, &isBot, &accId, botName, _countof(botName))) {
        return;
    }
    if (clientNum < 0 || static_cast<size_t>(clientNum) >= kMaxArcadeReservations) {
        return;
    }
    RecordArcadeSlotOrigin(clientNum, pBotDef, (accId <= 0 && botName[0] != L'\0') ? botName : nullptr);
    char botUtf8[64] = {};
    if (botName[0] != L'\0') {
        WideCharToMultiByte(CP_UTF8, 0, botName, -1, botUtf8, sizeof(botUtf8) - 1, NULL, NULL);
    }
    char logBuf[256] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] Recorded ArcadeSlotOrigin[clientNum=%d]: botDef=%p initialBotName='%s' isBot=%u",
        clientNum, pBotDef, botUtf8, static_cast<unsigned>(isBot));
    LogArcadeLine(logBuf);
}

bool EnsureQueueArcadeK2HooksInstalled() noexcept {
    // satisfies: OBL-QA-03
    if (!IsQueueArcadeModeActive()) {
        return false;
    }

    HMODULE hGameCurrent = GetModuleHandleA("game_x64.dll");
    if (g_generateClientIdHookInstalled.load(std::memory_order_acquire) &&
        hGameCurrent == g_hookedGameX64Module.load(std::memory_order_acquire)) {
        return true;
    }

    AcquireSRWLockExclusive(&g_arcadeHookInstallLock);

    bool k2Ready = g_generateClientIdHookInstalled.load(std::memory_order_acquire);
    if (!k2Ready) {
        HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
        if (hK2) {
            void* pTarget = reinterpret_cast<void*>(
                GetProcAddress(hK2, "?GenerateClientID@CHostServer@@QEAAHAEAGH@Z"));
            if (!pTarget) {
                LogArcadeLine("[KONGOR QUEUE ARCADE] WARN: Export ?GenerateClientID@CHostServer@@QEAAHAEAGH@Z not found in k2_x64.dll.");
            } else {
                // K2_SERVER (games/server/k2_x64.dll+0x8b9b80) 16-byte prologue:
                //   48 89 74 24 10    MOV [RSP+0x10], RSI
                //   48 89 7C 24 18    MOV [RSP+0x18], RDI
                //   41 56             PUSH R14
                //   48 83 EC 30       SUB RSP, 0x30
                static const uint8_t patServerOrig16[] = {
                    0x48, 0x89, 0x74, 0x24, 0x10,
                    0x48, 0x89, 0x7C, 0x24, 0x18,
                    0x41, 0x56,
                    0x48, 0x83, 0xEC, 0x30
                };

                // K2_CLIENT (games/client/k2_x64.dll+0x8ea4f0) 15-byte prologue:
                //   48 89 5C 24 08    MOV [RSP+0x08], RBX
                //   48 89 74 24 10    MOV [RSP+0x10], RSI
                //   57                PUSH RDI
                //   48 83 EC 30       SUB RSP, 0x30
                static const uint8_t patClientOrig15[] = {
                    0x48, 0x89, 0x5C, 0x24, 0x08,
                    0x48, 0x89, 0x74, 0x24, 0x10,
                    0x57,
                    0x48, 0x83, 0xEC, 0x30
                };

                const uint8_t* pCode = reinterpret_cast<const uint8_t*>(pTarget);
                bool installed = false;
                if (memcmp(pCode, patServerOrig16, sizeof(patServerOrig16)) == 0) {
                    installed = PatchController::InstallManagedInlineHook64(
                        "arcade", "CHostServer::GenerateClientID", pTarget,
                        reinterpret_cast<void*>(Hooked_CHostServer_GenerateClientID),
                        reinterpret_cast<void**>(&g_origGenerateClientID),
                        sizeof(patServerOrig16), patServerOrig16, sizeof(patServerOrig16));
                } else if (memcmp(pCode, patClientOrig15, sizeof(patClientOrig15)) == 0) {
                    installed = PatchController::InstallManagedInlineHook64(
                        "arcade", "CHostServer::GenerateClientID", pTarget,
                        reinterpret_cast<void*>(Hooked_CHostServer_GenerateClientID),
                        reinterpret_cast<void**>(&g_origGenerateClientID),
                        sizeof(patClientOrig15), patClientOrig15, sizeof(patClientOrig15));
                } else {
                    const size_t safeLen = PatchController::ComputeSafeHookLength64(pCode, 24, 14);
                    if (safeLen >= 14) {
                        installed = PatchController::InstallManagedInlineHook64(
                            "arcade", "CHostServer::GenerateClientID", pTarget,
                            reinterpret_cast<void*>(Hooked_CHostServer_GenerateClientID),
                            reinterpret_cast<void**>(&g_origGenerateClientID),
                            safeLen, nullptr, 0);
                    }
                }

                if (installed) {
                    g_generateClientIdTargetAddr.store(pCode, std::memory_order_release);
                    g_generateClientIdHookInstalled.store(true, std::memory_order_release);
                    k2Ready = true;
                    LogArcadeLine("[KONGOR QUEUE ARCADE] Installed HDE64-validated inline detour on CHostServer::GenerateClientID.");
                } else {
                    LogArcadeLine("[KONGOR QUEUE ARCADE] ERROR: Failed to install inline detour on CHostServer::GenerateClientID.");
                }
            }
        }
    }

    // Install CGameServer::AddBot inline detour whenever game_x64.dll is loaded:
    //   - Dedicated server (games/server/game/game_x64.dll, 1,206,272 bytes): RVA 0x6E2E0
    //   - Client practice  (games/client/game/game_x64.dll, 1,101,312 bytes): RVA 0x69C10
    HMODULE hGame = GetModuleHandleA("game_x64.dll");
    if (!hGame) {
        g_hookedGameX64Module.store(nullptr, std::memory_order_release);
    } else if (hGame != g_hookedGameX64Module.load(std::memory_order_acquire)) {
        // Verified 15-byte prologue across both server (0x6E2E0) and client (0x69C10) game_x64.dll:
        //   48 89 5C 24 08    MOV [RSP+0x08], RBX
        //   48 89 74 24 18    MOV [RSP+0x18], RSI
        //   48 89 7C 24 20    MOV [RSP+0x20], RDI
        static const uint8_t patAddBotOrig15[] = {
            0x48, 0x89, 0x5C, 0x24, 0x08,
            0x48, 0x89, 0x74, 0x24, 0x18,
            0x48, 0x89, 0x7C, 0x24, 0x20
        };
        // Secondary +0x63 structural signature: MOV RAX, [RCX+0xB0]; TEST RAX, RAX (m_pGameInfo check)
        static const uint8_t patAddBotGameInfoCheck10[] = {
            0x48, 0x8B, 0x81, 0xB0, 0x00, 0x00, 0x00,
            0x48, 0x85, 0xC0
        };
        static const uint32_t kCandidateAddBotRvas[] = { 0x6E2E0u, 0x69C10u };

        bool addBotHooked = false;
        uint32_t matchedRva = 0;
        for (size_t i = 0; i < sizeof(kCandidateAddBotRvas) / sizeof(kCandidateAddBotRvas[0]); ++i) {
            const uint32_t rva = kCandidateAddBotRvas[i];
            uint8_t* pAddBotTarget = reinterpret_cast<uint8_t*>(hGame) + rva;
            __try {
                if (memcmp(pAddBotTarget, patAddBotOrig15, sizeof(patAddBotOrig15)) == 0 &&
                    memcmp(pAddBotTarget + 0x63, patAddBotGameInfoCheck10, sizeof(patAddBotGameInfoCheck10)) == 0) {
                    addBotHooked = PatchController::InstallManagedInlineHook64(
                        "arcade", "CGameServer::AddBot", pAddBotTarget,
                        reinterpret_cast<void*>(Hooked_CGameServer_AddBot),
                        reinterpret_cast<void**>(&g_origGameServerAddBot),
                        sizeof(patAddBotOrig15), patAddBotOrig15, sizeof(patAddBotOrig15));
                    if (addBotHooked) {
                        matchedRva = rva;
                        break;
                    }
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                addBotHooked = false;
            }
        }

        g_hookedGameX64Module.store(hGame, std::memory_order_release);
        if (addBotHooked) {
            char msg[192] = {};
            snprintf(msg, sizeof(msg),
                "[KONGOR QUEUE ARCADE] Installed HDE64-validated inline detour on CGameServer::AddBot (game_x64.dll+0x%X, Party-mode bot support).",
                matchedRva);
            LogArcadeLine(msg);
        } else {
            LogArcadeLine("[KONGOR QUEUE ARCADE] WARN: CGameServer::AddBot signature did not match loaded game_x64.dll (checked RVAs 0x6E2E0, 0x69C10).");
        }
    }

    ReleaseSRWLockExclusive(&g_arcadeHookInstallLock);
    return k2Ready;
}

bool IsQueueArcadeClientIdHookArmed() noexcept {
    if (PatchController::IsSafeModeActive()) {
        return false;
    }
    if (!g_generateClientIdHookInstalled.load(std::memory_order_acquire) || !g_origGenerateClientID) {
        return false;
    }
    const uint8_t* pTarget = g_generateClientIdTargetAddr.load(std::memory_order_acquire);
    if (!pTarget) {
        return false;
    }
    __try {
        // Verify active 14-byte FF 25 [RIP+0] absolute detour opcode (not left unpatched by DISARMED_SAFE_MODE / boot seed)
        return (pTarget[0] == 0xFFu && pTarget[1] == 0x25u);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool ConsumePendingArcadeSafeModeRetirement() noexcept {
    return g_pendingSafeModeRetirement.exchange(false, std::memory_order_acq_rel);
}

QueueArcadeSlotReservationResult ArcadeReserveSlot(
    int32_t accountId,
    int32_t preferredTeam,
    const wchar_t* playerName,
    const QueueArcadeEngineContext* overrideCtx,
    const wchar_t* explicitClanTag
) noexcept {
    // satisfies: OBL-QA-02
    // satisfies: OBL-QA-07
    if (PatchController::IsSafeModeActive() ||
        (overrideCtx == nullptr && !IsQueueArcadeClientIdHookArmed())) {
        QueueArcadeSlotReservationResult disarmedRes = {};
        disarmedRes.success = false;
        disarmedRes.clientNum = -1;
        disarmedRes.accountId = accountId;
        snprintf(disarmedRes.reason, sizeof(disarmedRes.reason), "arcade_hooks_disarmed");
        char warnBuf[256] = {};
        snprintf(warnBuf, sizeof(warnBuf),
            "[KONGOR QUEUE ARCADE] ERROR: Refusing ArcadeReserveSlot(accountId=%d) because GenerateClientID hook is disarmed (SafeMode=%d, reason='%s').",
            accountId,
            PatchController::IsSafeModeActive() ? 1 : 0,
            PatchController::GetSafeModeReason());
        LogArcadeLine(warnBuf);
        return disarmedRes;
    }

    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    wchar_t fullDisplayName[96] = {};
    QueueArcadeSlotReservationResult res = ArcadeReserveSlotSeh(
        ctx, accountId, preferredTeam, playerName, explicitClanTag,
        fullDisplayName, _countof(fullDisplayName));

    int32_t displacedClientNum = -1;
    void* displacedBrain = nullptr;
    if (res.success && res.clientNum >= 0) {
        AcquireSRWLockExclusive(&g_arcadeReservationLock);
        if (static_cast<size_t>(res.clientNum) < kMaxArcadeReservations) {
            ArcadeSlotOriginEntry& origin = g_arcadeSlotOrigins[res.clientNum];
            if (origin.originalBotName[0] == L'\0' && res.previousBotName[0] != L'\0') {
                wcsncpy_s(origin.originalBotName, _countof(origin.originalBotName), res.previousBotName, _TRUNCATE);
                origin.populated = true;
            } else if (origin.originalBotName[0] != L'\0') {
                wcsncpy_s(res.previousBotName, _countof(res.previousBotName), origin.originalBotName, _TRUNCATE);
            }
        }
        UpsertReservationLocked(
            accountId, res.clientNum, res.teamId, playerName, res.previousBotName,
            res.detachedBotBrain, &displacedClientNum, &displacedBrain);
        ReleaseSRWLockExclusive(&g_arcadeReservationLock);
    } else if (res.detachedBotBrain != nullptr) {
        displacedClientNum = res.clientNum;
        displacedBrain = res.detachedBotBrain;
    }

    if (displacedBrain != nullptr && displacedClientNum >= 0) {
        FlushParkedBotBrainForClientSeh(ctx, displacedClientNum, displacedBrain);
    }

    if (res.success && strcmp(res.reason, "reserved_bot_slot") == 0) {
        const wchar_t* humanNoticeName = (fullDisplayName[0] != L'\0') ? fullDisplayName : playerName;
        const wchar_t* botNoticeName = (res.previousBotName[0] != L'\0') ? res.previousBotName : L"ArcadeBot";
        QueueArcadeSwapChatNoticeW(ctx, humanNoticeName, botNoticeName, true);
    }

    char nameUtf8[64] = {};
    if (playerName && *playerName) {
        WideCharToMultiByte(CP_UTF8, 0, playerName, -1, nameUtf8, sizeof(nameUtf8) - 1, NULL, NULL);
    }
    char prevBotUtf8[64] = {};
    if (res.previousBotName[0] != L'\0') {
        WideCharToMultiByte(CP_UTF8, 0, res.previousBotName, -1, prevBotUtf8, sizeof(prevBotUtf8) - 1, NULL, NULL);
    }

    char logBuf[512] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] ArcadeReserveSlot(accountId=%d, preferredTeam=%d, name='%s') -> success=%d clientNum=%d team=%d removedBot=%d parkedBrain=%d netNameUpdated=%d flags3DE=0x%04X->0x%04X postInit668=%u->%u discTerm438=0x%08X->0x%08X prevBot='%s' reason='%s'",
        accountId, preferredTeam, nameUtf8,
        res.success ? 1 : 0, res.clientNum, res.teamId,
        res.removedBotBrain ? 1 : 0, res.detachedBotBrain ? 1 : 0, res.updatedNetStrings ? 1 : 0,
        static_cast<unsigned>(res.priorFlags3DE), static_cast<unsigned>(res.postFlags3DE),
        static_cast<unsigned>(res.priorPostInit668), static_cast<unsigned>(res.postInit668),
        res.priorDiscTerm438, res.postDiscTerm438,
        prevBotUtf8, res.reason);
    LogArcadeLine(logBuf);
    return res;
}

bool ArcadeReleaseSlot(
    int32_t accountId,
    const QueueArcadeEngineContext* overrideCtx,
    bool kickClient
) noexcept {
    // satisfies: OBL-QA-04
    // satisfies: OBL-QA-10
    int32_t reservedClientNum = -1;
    wchar_t departedHumanName[96] = {};
    wchar_t savedBotName[64] = {};
    void* parkedBotBrain = nullptr;
    ArcadeSlotOriginEntry originsSnapshot[kMaxArcadeReservations] = {};

    AcquireSRWLockExclusive(&g_arcadeReservationLock);
    const bool hadReservation = RemoveReservationByAccountLocked(
        accountId, &reservedClientNum,
        departedHumanName, _countof(departedHumanName),
        savedBotName, _countof(savedBotName),
        &parkedBotBrain);
    memcpy(originsSnapshot, g_arcadeSlotOrigins, sizeof(originsSnapshot));
    ReleaseSRWLockExclusive(&g_arcadeReservationLock);

    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    int32_t releasedClientNum = -1;
    uint16_t priorFlags3DE = 0;
    uint16_t postFlags3DE = 0;
    bool restoredBotBrain = false;
    wchar_t effectiveBotName[64] = {};
    const bool updatedPlayer = ArcadeReleaseSlotSeh(
        ctx, accountId, reservedClientNum, savedBotName, originsSnapshot, parkedBotBrain,
        &releasedClientNum, &priorFlags3DE, &postFlags3DE, &restoredBotBrain,
        departedHumanName, _countof(departedHumanName),
        effectiveBotName, _countof(effectiveBotName));

    if (!updatedPlayer && parkedBotBrain != nullptr && reservedClientNum >= 0) {
        FlushParkedBotBrainForClientSeh(ctx, reservedClientNum, parkedBotBrain);
    }

    const wchar_t* finalBotName = (effectiveBotName[0] != L'\0') ? effectiveBotName
        : ((savedBotName[0] != L'\0') ? savedBotName : L"ArcadeBot");

    if (updatedPlayer) {
        QueueArcadeSwapChatNoticeW(ctx, departedHumanName, finalBotName, false);
    }

    bool kickedLingering = false;
    const int32_t targetKickClientNum = (releasedClientNum >= 0) ? releasedClientNum : reservedClientNum;
    if (kickClient && targetKickClientNum >= 0) {
        kickedLingering = KickReleasedClientIfConnectedSeh(ctx, accountId, targetKickClientNum);
    }

    char restoredUtf8[64] = {};
    WideCharToMultiByte(CP_UTF8, 0, finalBotName, -1, restoredUtf8, sizeof(restoredUtf8) - 1, NULL, NULL);

    char logBuf[448] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] ArcadeReleaseSlot(accountId=%d, kick=%d) -> hadReservation=%d updatedPlayer=%d restoredBrain=%d kicked=%d clientNum=%d flags3DE=0x%04X->0x%04X restoredBot='%s'",
        accountId, kickClient ? 1 : 0, hadReservation ? 1 : 0, updatedPlayer ? 1 : 0,
        restoredBotBrain ? 1 : 0, kickedLingering ? 1 : 0, targetKickClientNum,
        static_cast<unsigned>(priorFlags3DE), static_cast<unsigned>(postFlags3DE),
        restoredUtf8[0] ? restoredUtf8 : "ArcadeBot");
    LogArcadeLine(logBuf);
    return hadReservation || updatedPlayer;
}

bool DisableArchiveChecksumsIfAllowed(bool* pExamineChecksums) noexcept {
    if (!pExamineChecksums) {
        return false;
    }

    char envBuf[32] = {};
    const DWORD envLen = GetEnvironmentVariableA("KONGOR_EXAMINE_ARCHIVE_CHECKSUMS", envBuf, sizeof(envBuf));
    if (envLen > 0 && envLen < sizeof(envBuf)) {
        if (_stricmp(envBuf, "1") == 0 ||
            _stricmp(envBuf, "true") == 0 ||
            _stricmp(envBuf, "yes") == 0 ||
            _stricmp(envBuf, "on") == 0) {
            return false;
        }
    }

    __try {
        *pExamineChecksums = false;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint32_t ArcadeParkUnreservedBots(
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    // satisfies: OBL-QA-04
    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    const uint32_t parked = ArcadeParkUnreservedBotsSeh(ctx);
    if (parked > 0) {
        char logBuf[192] = {};
        snprintf(logBuf, sizeof(logBuf),
            "[KONGOR QUEUE ARCADE] ArcadeParkUnreservedBots -> parked %u unreserved connectionless slot(s) at fountain.",
            parked);
        LogArcadeLine(logBuf);
    }
    return parked;
}

int32_t ResolveReservedArcadeClientId(
    const uint16_t* pConnId,
    int32_t accountId
) noexcept {
    // satisfies: OBL-QA-03
    // Only fall back to connection-name matching when accountId is not yet resolved (accountId <= 0);
    // once c_conn populates accountId > 0, bind strictly by accountId to prevent name-collision hijacks.
    wchar_t connName[64] = {};
    const bool hasConnName = (accountId <= 0) && ReadK2WStringAtConnNameSeh(pConnId, connName, _countof(connName));

    int32_t matchedClientNum = -1;
    AcquireSRWLockShared(&g_arcadeReservationLock);
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        const ArcadeReservationEntry& entry = g_arcadeReservations[i];
        if (!entry.active) continue;
        if (accountId > 0) {
            if (entry.accountId == accountId) {
                matchedClientNum = entry.clientNum;
                break;
            }
        } else if (hasConnName && entry.playerName[0] != L'\0' && NamesMatchPlayerW(entry.playerName, connName)) {
            matchedClientNum = entry.clientNum;
            break;
        }
    }
    ReleaseSRWLockShared(&g_arcadeReservationLock);
    return matchedClientNum;
}

void ClearAllArcadeReservations(
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    // satisfies: OBL-QA-10
    struct PendingParkedDrain {
        int32_t clientNum;
        void*   parkedBotBrain;
    };
    PendingParkedDrain pending[kMaxArcadeReservations] = {};
    size_t pendingCount = 0;

    AcquireSRWLockExclusive(&g_arcadeReservationLock);
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_arcadeReservations[i].active &&
            g_arcadeReservations[i].parkedBotBrain != nullptr &&
            g_arcadeReservations[i].clientNum >= 0) {
            pending[pendingCount].clientNum = g_arcadeReservations[i].clientNum;
            pending[pendingCount].parkedBotBrain = g_arcadeReservations[i].parkedBotBrain;
            ++pendingCount;
        }
        g_arcadeReservations[i] = {};
    }
    ResetArcadeSlotOriginsLocked();
    ReleaseSRWLockExclusive(&g_arcadeReservationLock);

    if (pendingCount > 0) {
        const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
        for (size_t i = 0; i < pendingCount; ++i) {
            FlushParkedBotBrainForClientSeh(ctx, pending[i].clientNum, pending[i].parkedBotBrain);
        }
    }
}

bool ShouldSuppressPreMatchDuplicateHeroSpawn(
    void* pPlayer,
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    // satisfies: OBL-QA-09
    if (!pPlayer) {
        return false;
    }
    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    if (!ctx || !ctx->ppGame) {
        return false;
    }

    int32_t clientNum = -1;
    uint32_t heroIdx = 0xFFFFFFFFu;
    int32_t gamePhase = -1;
    bool suppress = false;

    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) {
            return false;
        }
        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t pGameInfo = *reinterpret_cast<const uintptr_t*>(pGame + 0xB0);
        if (!pGameInfo) {
            return false;
        }
        gamePhase = *reinterpret_cast<const int32_t*>(pGameInfo + 0xB4);
        if (gamePhase >= 5) {
            // Phase >= 5 (PRE_MATCH / ACTIVE): leave SelectHero/RandomHero/RiftWars repick spawns completely untouched.
            return false;
        }

        const uintptr_t playerAddr = reinterpret_cast<uintptr_t>(pPlayer);
        heroIdx = *reinterpret_cast<const uint32_t*>(playerAddr + 0xB0);
        if (heroIdx == 0xFFFFFFFFu) {
            return false;
        }

        void* pEntityDir = *reinterpret_cast<void* const*>(pGame + 0x28);
        if (!pEntityDir) {
            return false;
        }

        using PFN_GetEntityFromIndex = void* (__fastcall*)(void* pDir, uint32_t uiIndex);
        using PFN_GetAsUnit = void* (__fastcall*)(void* pEntity);

        const uintptr_t dirVtbl = *reinterpret_cast<const uintptr_t*>(pEntityDir);
        if (!dirVtbl) {
            return false;
        }
        const auto pfnGetEnt = *reinterpret_cast<PFN_GetEntityFromIndex const*>(dirVtbl + 0x10);
        if (!pfnGetEnt) {
            return false;
        }
        void* pGameEntity = pfnGetEnt(pEntityDir, heroIdx);
        if (!pGameEntity) {
            return false;
        }

        const uintptr_t entVtbl = *reinterpret_cast<const uintptr_t*>(pGameEntity);
        if (!entVtbl) {
            return false;
        }
        const auto pfnGetAsUnit = *reinterpret_cast<PFN_GetAsUnit const*>(entVtbl + 0x178);
        if (!pfnGetAsUnit || pfnGetAsUnit(pGameEntity) == nullptr) {
            return false;
        }

        clientNum = *reinterpret_cast<const int32_t*>(playerAddr + 0xA8);
        suppress = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }

    if (suppress) {
        char logBuf[256] = {};
        snprintf(logBuf, sizeof(logBuf),
            "[KONGOR NATIVE GUARD] Suppressed duplicate pre-Phase-5 CPlayer::SpawnHero(clientNum=%d, existingHeroIdx=%u, gamePhase=%d).",
            clientNum, heroIdx, gamePhase);
        LogArcadeLine(logBuf);
    }
    return suppress;
}

bool ArcadeReleaseParkedBotOnPlayerTerminate(
    void* pPlayer,
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    // satisfies: OBL-QA-10
    if (!pPlayer) {
        return false;
    }

    int32_t clientNum = -1;
    __try {
        clientNum = *reinterpret_cast<const int32_t*>(reinterpret_cast<uintptr_t>(pPlayer) + 0xA8);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    if (clientNum < 0) {
        return false;
    }

    void* parkedBrain = nullptr;
    AcquireSRWLockExclusive(&g_arcadeReservationLock);
    for (size_t i = 0; i < kMaxArcadeReservations; ++i) {
        if (g_arcadeReservations[i].active && g_arcadeReservations[i].clientNum == clientNum) {
            parkedBrain = g_arcadeReservations[i].parkedBotBrain;
            g_arcadeReservations[i] = {};
        }
    }
    ReleaseSRWLockExclusive(&g_arcadeReservationLock);

    if (!parkedBrain) {
        return false;
    }

    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    const bool freed = FlushParkedBotBrainOnPlayerSeh(ctx, pPlayer, parkedBrain);
    char logBuf[256] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] ArcadeReleaseParkedBotOnPlayerTerminate(clientNum=%d) -> freed=%d while CLuaScriptManager is live.",
        clientNum, freed ? 1 : 0);
    LogArcadeLine(logBuf);
    return freed;
}

void ExecuteConsoleArcadeModeW(const wchar_t* args) noexcept {
    if (!args) return;
    while (*args == L' ' || *args == L'\t') ++args;
    const int32_t modeVal = _wtoi(args);
    // Flush any leftover reservations/parked brains from the prior match while IGame::s_pGame is still live (pre-CreateMatch)
    ClearAllArcadeReservations(nullptr);
    if (modeVal >= 2) {
        SetQueueArcadeMode(QueueArcadeServerMode::PartyDormantSlots);
    } else if (modeVal == 1) {
        SetQueueArcadeMode(QueueArcadeServerMode::ActiveBots);
    } else {
        SetQueueArcadeMode(QueueArcadeServerMode::Disabled);
        return;
    }

    // Fail-closed Safe-Mode guard: never auto-re-arm disarmed hooks; retire unusable Arcade server cleanly via Quit
    if (PatchController::IsSafeModeActive() ||
        (GetModuleHandleA("k2_x64.dll") != nullptr && !IsQueueArcadeClientIdHookArmed())) {
        char errBuf[256] = {};
        snprintf(errBuf, sizeof(errBuf),
            "[KONGOR QUEUE ARCADE] ERROR: GenerateClientID hook disarmed on kongor_arcade_mode=%d (SafeMode=%d, reason='%s') — retiring unusable Queue Arcade server cleanly.",
            modeVal,
            PatchController::IsSafeModeActive() ? 1 : 0,
            PatchController::GetSafeModeReason());
        LogArcadeLine(errBuf);
        g_arcadeMode.store(0, std::memory_order_release);
        ClearAllArcadeReservations();
        g_pendingSafeModeRetirement.store(true, std::memory_order_release);
    }
}

void ExecuteConsoleArcadeReserveW(const wchar_t* args) noexcept {
    if (!args || !*args) return;
    if (!IsQueueArcadeModeActive()) {
        SetQueueArcadeMode(QueueArcadeServerMode::ActiveBots);
    }

    const wchar_t* p = args;
    while (*p == L' ' || *p == L'\t') ++p;
    wchar_t* endPtr = nullptr;
    const int32_t accountId = static_cast<int32_t>(wcstol(p, &endPtr, 10));
    if (endPtr == p || accountId <= 0) return;

    p = endPtr;
    while (*p == L' ' || *p == L'\t') ++p;
    int32_t preferredTeam = 0;
    if (*p >= L'0' && *p <= L'9') {
        preferredTeam = static_cast<int32_t>(wcstol(p, &endPtr, 10));
        p = endPtr;
    }

    auto parseToken = [](const wchar_t*& cursor, wchar_t* outBuf, size_t outCap) noexcept {
        if (!outBuf || outCap == 0) return;
        outBuf[0] = L'\0';
        while (*cursor == L' ' || *cursor == L'\t') ++cursor;
        if (*cursor == L'\0' || *cursor == L';' || *cursor == L'\r' || *cursor == L'\n') return;
        size_t idx = 0;
        const bool quoted = (*cursor == L'"');
        if (quoted) ++cursor;
        while (*cursor != L'\0' && *cursor != L'\r' && *cursor != L'\n' && idx + 1 < outCap) {
            if (quoted && *cursor == L'"') {
                ++cursor;
                break;
            }
            if (!quoted && (*cursor == L';' || *cursor == L' ' || *cursor == L'\t')) break;
            outBuf[idx++] = *cursor++;
        }
        outBuf[idx] = L'\0';
    };

    wchar_t nameBuf[64] = {};
    wchar_t clanBuf[32] = {};
    parseToken(p, nameBuf, _countof(nameBuf));
    parseToken(p, clanBuf, _countof(clanBuf));

    ArcadeReserveSlot(accountId, preferredTeam, nameBuf, nullptr, clanBuf[0] != L'\0' ? clanBuf : nullptr);
}

void ExecuteConsoleArcadeReleaseW(const wchar_t* args) noexcept {
    if (!args || !*args) return;
    const wchar_t* p = args;
    while (*p == L' ' || *p == L'\t') ++p;
    wchar_t* endPtr = nullptr;
    const int32_t accountId = static_cast<int32_t>(wcstol(p, &endPtr, 10));
    if (endPtr == p || accountId <= 0) return;

    p = endPtr;
    while (*p == L' ' || *p == L'\t' || *p == L'"') ++p;
    bool kickClient = false;
    if (_wcsnicmp(p, L"kick", 4) == 0 &&
        (p[4] == L'\0' || p[4] == L' ' || p[4] == L'\t' || p[4] == L';' || p[4] == L'"' || p[4] == L'\r' || p[4] == L'\n')) {
        kickClient = true;
    } else if (*p == L'1' &&
               (p[1] == L'\0' || p[1] == L' ' || p[1] == L'\t' || p[1] == L';' || p[1] == L'"' || p[1] == L'\r' || p[1] == L'\n')) {
        kickClient = true;
    }

    ArcadeReleaseSlot(accountId, nullptr, kickClient);
}

void ExecuteConsoleArcadeSwapHooksW(const wchar_t* args) noexcept {
    if (!args) return;
    while (*args == L' ' || *args == L'\t' || *args == L'"') ++args;
    bool enable = true;
    if (*args == L'0' ||
        _wcsnicmp(args, L"false", 5) == 0 ||
        _wcsnicmp(args, L"off", 3) == 0 ||
        _wcsnicmp(args, L"no", 2) == 0) {
        enable = false;
    }
    SetQueueArcadeSwapHooksEnabled(enable);
}

namespace {

static bool ReadArcadePlayerSwapStateSeh(
    void* pPlayer,
    int32_t* outClientNum,
    int32_t* outTeam,
    int32_t* outAccountId,
    uint8_t* outIsBot,
    uint16_t* outFlags
) noexcept {
    if (outClientNum) *outClientNum = -1;
    if (outTeam) *outTeam = 0;
    if (outAccountId) *outAccountId = 0;
    if (outIsBot) *outIsBot = 0;
    if (outFlags) *outFlags = 0;
    if (!pPlayer) return false;
    __try {
        const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
        if (outClientNum) *outClientNum = *reinterpret_cast<const int32_t*>(addr + 0xA8);
        if (outTeam) *outTeam = *reinterpret_cast<const int32_t*>(addr + 0xAC);
        if (outAccountId) *outAccountId = *reinterpret_cast<const int32_t*>(addr + 0x2D0);
        if (outIsBot) *outIsBot = *reinterpret_cast<const uint8_t*>(addr + 0x66B);
        if (outFlags) *outFlags = *reinterpret_cast<const uint16_t*>(addr + 0x3DE);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static void ScrubBotDisconnectAndTerminateFlagsSeh(
    void* pPlayer,
    bool ensureConnectionlessBit,
    const QueueArcadeEngineContext* ctx,
    const void* originBotDef
) noexcept {
    if (!pPlayer) return;
    __try {
        const uintptr_t addr = reinterpret_cast<uintptr_t>(pPlayer);
        uint16_t* pFlags = reinterpret_cast<uint16_t*>(addr + 0x3DE);
        uint16_t nextFlags = static_cast<uint16_t>(*pFlags & ~(0x0101u | 0x0080u | 0x0040u | 0x8000u));
        if (ensureConnectionlessBit) {
            nextFlags = static_cast<uint16_t>(nextFlags | 0x0200u);
            *reinterpret_cast<uint8_t*>(addr + 0x668) = 1;
            *reinterpret_cast<uint8_t*>(addr + 0x66B) = 1;
        }
        *pFlags = nextFlags;
        *reinterpret_cast<uint32_t*>(addr + 0x430) = 0xFFFFFFFFu;
        *reinterpret_cast<uint32_t*>(addr + 0x438) = 0xFFFFFFFFu;

        if (ensureConnectionlessBit &&
            GetQueueArcadeMode() == QueueArcadeServerMode::ActiveBots &&
            *reinterpret_cast<void* const*>(addr + 0x730) == nullptr &&
            originBotDef != nullptr &&
            ctx != nullptr &&
            ctx->pfnAttachBot != nullptr) {
            void* healedBrain = ctx->pfnAttachBot(pPlayer, originBotDef);
            if (healedBrain == nullptr) {
                healedBrain = *reinterpret_cast<void* const*>(addr + 0x730);
            }
            if (healedBrain != nullptr) {
                const uint32_t heroIdx = *reinterpret_cast<const uint32_t*>(addr + 0xB0);
                *reinterpret_cast<void**>(addr + 0x730) = healedBrain;
                *reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(healedBrain) + 0x10) = heroIdx;
                uint16_t* pFlags2 = reinterpret_cast<uint16_t*>(addr + 0x3E0);
                *pFlags2 = static_cast<uint16_t>(*pFlags2 | 0x0008u);
                const int32_t clientNum = *reinterpret_cast<const int32_t*>(addr + 0xA8);
                char healLog[256] = {};
                snprintf(healLog, sizeof(healLog),
                    "[KONGOR QUEUE ARCADE] [ATTACH_BOT_SELF_HEAL] Re-attached CBotBrain=%p on active-phase Terminate guard (clientNum=%d, botDef=%p, heroIdx=%u)",
                    healedBrain, clientNum, originBotDef, heroIdx);
                LogArcadeLine(healLog);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

static uint32_t ReapConnectionlessBotsIfNoActiveHumansSeh(const QueueArcadeEngineContext* ctx) noexcept {
    if (!ctx || !ctx->ppGame) return 0;
    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) return 0;

        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);
        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (!head || *reinterpret_cast<const uint8_t*>(head + 0x19) == 0) {
            return 0;
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
            return botCount;
        }
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

} // anonymous namespace

bool OnArcadePlayerDisconnected(
    void* pPlayer,
    uint32_t /*time*/,
    uint32_t /*maxDiscTime*/,
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer || !IsQueueArcadeModeActive()) {
        return false;
    }
    if (!IsQueueArcadeSwapHooksEnabled()) {
        LogArcadeLine("[KONGOR QUEUE ARCADE] [SWAP_KILLSWITCH] Bypassing Hooked_CPlayer_Disconnected (kongor_arcade_swap_hooks=0) -> stock K2 fallback.");
        return false;
    }

    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    const int32_t gamePhase = ReadCurrentGamePhaseSeh(ctx);
    if (gamePhase >= 7) {
        return false;
    }

    int32_t clientNum = -1;
    int32_t team = 0;
    int32_t accountId = 0;
    uint8_t isBot = 0;
    uint16_t flags = 0;
    if (!ReadArcadePlayerSwapStateSeh(pPlayer, &clientNum, &team, &accountId, &isBot, &flags)) {
        return false;
    }

    if (isBot == 1 || accountId <= 0 || IsArcadeKickReentrancyActive()) {
        ScrubBotDisconnectAndTerminateFlagsSeh(pPlayer, false, nullptr, nullptr);
        char logBuf[256] = {};
        snprintf(logBuf, sizeof(logBuf),
            "[KONGOR QUEUE ARCADE] Hooked_CPlayer_Disconnected suppressed on bot/connectionless slot (clientNum=%d, accId=%d, flags=0x%04X, kickGuard=%d)",
            clientNum, accountId, static_cast<unsigned>(flags), IsArcadeKickReentrancyActive() ? 1 : 0);
        LogArcadeLine(logBuf);
        return true;
    }

    char logBuf[256] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] Hooked_CPlayer_Disconnected releasing human slot (clientNum=%d, accId=%d, phase=%d) back to bot",
        clientNum, accountId, gamePhase);
    LogArcadeLine(logBuf);
    ArcadeReleaseSlot(accountId, ctx, false);
    ScrubBotDisconnectAndTerminateFlagsSeh(pPlayer, true, nullptr, nullptr);
    return true;
}

bool OnArcadePlayerTerminate(
    void* pPlayer,
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    if (!pPlayer || !IsQueueArcadeModeActive()) {
        return false;
    }
    if (!IsQueueArcadeSwapHooksEnabled()) {
        LogArcadeLine("[KONGOR QUEUE ARCADE] [SWAP_KILLSWITCH] Bypassing active-phase Hooked_CPlayer_Terminate guard (kongor_arcade_swap_hooks=0) -> stock K2 fallback.");
        return false;
    }

    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    const int32_t gamePhase = ReadCurrentGamePhaseSeh(ctx);
    if (gamePhase >= 7) {
        return false;
    }

    int32_t clientNum = -1;
    int32_t team = 0;
    int32_t accountId = 0;
    uint8_t isBot = 0;
    uint16_t flags = 0;
    if (!ReadArcadePlayerSwapStateSeh(pPlayer, &clientNum, &team, &accountId, &isBot, &flags)) {
        return false;
    }
    if (team != 1 && team != 2) {
        return false;
    }

    if (accountId > 0 && !IsArcadeKickReentrancyActive()) {
        ArcadeReleaseSlot(accountId, ctx, false);
    }

    const void* originBotDef = nullptr;
    GetArcadeSlotOrigin(clientNum, &originBotDef, nullptr, 0);
    ScrubBotDisconnectAndTerminateFlagsSeh(pPlayer, true, ctx, originBotDef);

    char logBuf[256] = {};
    snprintf(logBuf, sizeof(logBuf),
        "[KONGOR QUEUE ARCADE] [TERMINATE_GUARD] Suppressed CPlayer::Terminate during active phase (clientNum=%d, accId=%d, team=%d, phase=%d)",
        clientNum, accountId, team, gamePhase);
    LogArcadeLine(logBuf);
    return true;
}

void ReapConnectionlessBotsIfNoActiveHumans(
    const QueueArcadeEngineContext* overrideCtx
) noexcept {
    // satisfies: OBL-QA-01
    if (IsQueueArcadeModeActive()) {
        return;
    }
    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    const uint32_t botCount = ReapConnectionlessBotsIfNoActiveHumansSeh(ctx);
    if (botCount > 0) {
        char logBuf[256] = {};
        snprintf(logBuf, sizeof(logBuf),
            "[KONGOR NATIVE GUARD] [BOT-ECONOMY] Last human terminated; marked %u bot(s) terminated for clean CGameServer::Frame shutdown.",
            botCount);
        LogArcadeLine(logBuf);
    }
}

static std::atomic<uint64_t> g_lastHeroKeyframeSampleTickMs{0};

uint64_t GetLastHeroKeyframeSampleTickMs() noexcept {
    return g_lastHeroKeyframeSampleTickMs.load(std::memory_order_relaxed);
}

void RefreshServerKeyframePlayersFromEngine(const QueueArcadeEngineContext* overrideCtx) noexcept {
    const QueueArcadeEngineContext* ctx = overrideCtx ? overrideCtx : ResolveLiveArcadeEngineContext();
    if (!ctx || !ctx->ppGame || !*ctx->ppGame) {
        return;
    }

    g_lastHeroKeyframeSampleTickMs.store(GetTickCount64(), std::memory_order_relaxed);
    bool activeSlots[10] = {};

    __try {
        void* pGameVoid = *ctx->ppGame;
        if (!pGameVoid) return;
        const uintptr_t pGame = reinterpret_cast<uintptr_t>(pGameVoid);

        void* pEntityDir = *reinterpret_cast<void* const*>(pGame + 0x28);
        using PFN_GetEntityFromIndex = void* (__fastcall*)(void* pDir, uint32_t uiIndex);
        using PFN_GetAsUnit = void* (__fastcall*)(void* pEntity);

        PFN_GetEntityFromIndex pfnGetEnt = nullptr;
        if (pEntityDir) {
            const uintptr_t dirVtbl = *reinterpret_cast<const uintptr_t*>(pEntityDir);
            if (dirVtbl) {
                pfnGetEnt = *reinterpret_cast<PFN_GetEntityFromIndex const*>(dirVtbl + 0x10);
            }
        }

        const uintptr_t head = *reinterpret_cast<const uintptr_t*>(pGame + 0x1E8);
        if (head && *reinterpret_cast<const uint8_t*>(head + 0x19) != 0) {
            uintptr_t node = *reinterpret_cast<const uintptr_t*>(head + 0x00);
            for (uint32_t step = 0; step < 128 && node && node != head; ++step) {
                if (*reinterpret_cast<const uint8_t*>(node + 0x19) != 0) break;

                void* pPlayer = *reinterpret_cast<void* const*>(node + 0x28);
                if (pPlayer) {
                    const uintptr_t playerAddr = reinterpret_cast<uintptr_t>(pPlayer);
                    const int32_t slot = *reinterpret_cast<const int32_t*>(playerAddr + 0xA8);
                    const int32_t team = *reinterpret_cast<const int32_t*>(playerAddr + 0xAC);
                    const uint32_t heroIdx = *reinterpret_cast<const uint32_t*>(playerAddr + 0xB0);

                    if (slot >= 0 && slot < 10) {
                        activeSlots[slot] = true;

                        // CPlayer stores uint16_t m_unNameIndex at +0x2D8 (verified via SetName@CPlayer @ 0x4F71E0)
                        wchar_t wPlayerName[64] = {};
                        const uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(playerAddr + 0x2D8);
                        if (ctx->ppNetResourceManager && *ctx->ppNetResourceManager &&
                            ctx->pfnGetNetString && nameIdx != 0xFFFFu) {
                            const void* pExistingName = ctx->pfnGetNetString(*ctx->ppNetResourceManager, nameIdx);
                            ReadK2WStringFromPtrSeh(pExistingName, wPlayerName, _countof(wPlayerName));
                        }

                        char playerNameA[64] = {};
                        for (size_t i = 0; i < 63 && wPlayerName[i] != L'\0'; ++i) {
                            playerNameA[i] = (wPlayerName[i] < 128) ? static_cast<char>(wPlayerName[i]) : '?';
                        }
                        if (playerNameA[0] == '\0') {
                            snprintf(playerNameA, sizeof(playerNameA), "Player %d", slot + 1);
                        }

                        char heroNameA[64] = "None";
                        float x = 0.0f, y = 0.0f, z = 0.0f;
                        float heading = 0.0f;
                        bool isAlive = false;
                        float health = 0.0f, maxHealth = 0.0f;
                        float mana = 0.0f, maxMana = 0.0f;
                        int level = 1;

                        if (heroIdx != 0xFFFFFFFFu && pEntityDir && pfnGetEnt) {
                            void* pGameEntity = pfnGetEnt(pEntityDir, heroIdx);
                            if (pGameEntity) {
                                const uintptr_t entVtbl = *reinterpret_cast<const uintptr_t*>(pGameEntity);
                                if (entVtbl) {
                                    const auto pfnGetAsUnit = *reinterpret_cast<PFN_GetAsUnit const*>(entVtbl + 0x178);
                                    if (pfnGetAsUnit) {
                                        void* pUnit = pfnGetAsUnit(pGameEntity);
                                        if (pUnit) {
                                            const uintptr_t unitAddr = reinterpret_cast<uintptr_t>(pUnit);

                                            wchar_t wHeroName[64] = {};
                                            ReadK2WStringFromPtrSeh(reinterpret_cast<const void*>(unitAddr + 0x30), wHeroName, _countof(wHeroName));
                                            for (size_t i = 0; i < 63 && wHeroName[i] != L'\0'; ++i) {
                                                heroNameA[i] = (wHeroName[i] < 128) ? static_cast<char>(wHeroName[i]) : '?';
                                            }

                                            x = *reinterpret_cast<const float*>(unitAddr + 0xD8);
                                            y = *reinterpret_cast<const float*>(unitAddr + 0xDC);
                                            z = *reinterpret_cast<const float*>(unitAddr + 0xE0);
                                            heading = *reinterpret_cast<const float*>(unitAddr + 0xF8);

                                            const uint8_t status = *reinterpret_cast<const uint8_t*>(unitAddr + 0xD0);
                                            isAlive = (status == 1);

                                            health = *reinterpret_cast<const float*>(unitAddr + 0x360);
                                            maxHealth = *reinterpret_cast<const float*>(unitAddr + 0x368);
                                            mana = *reinterpret_cast<const float*>(unitAddr + 0x364);
                                            maxMana = *reinterpret_cast<const float*>(unitAddr + 0x36c);

                                            level = static_cast<int>(*reinterpret_cast<const uint32_t*>(unitAddr + 0x35c));
                                            if (level < 1) level = 1;
                                        }
                                    }
                                }
                            }
                        }

                        ::SetKeyframePlayerCoordinate(
                            slot, team, heroNameA, playerNameA,
                            x, y, z, health, maxHealth, mana, maxMana,
                            level, heading, isAlive);
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
        }

        for (int s = 0; s < 10; ++s) {
            if (!activeSlots[s]) {
                ::ClearKeyframePlayerSlot(s);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] [SEH] Intercepted fault inside RefreshServerKeyframePlayersFromEngine.\n");
    }
}

// ============================================================================
// 10v10 Mid Wars Kill Packet (0x60) Framing Guard, 128B Expansion & Desync Telemetry
// Dossier: artifacts/evidence/midwars_10v10_kill_packet_truncation.json
// ============================================================================

bool ClampKillMessageBufferInPlace(
    uint8_t* buffer,
    size_t bufferLen,
    uint8_t* outOldAssists,
    uint8_t* outNewAssists
) noexcept {
    // satisfies: OBL-MIDWARS-01
    // satisfies: OBL-MIDWARS-02
    if (!buffer || bufferLen < kKillMessageHeaderBytes) {
        return false;
    }
    if (buffer[0] != kGameCmdKillMessage) {
        return false;
    }
    const uint8_t declaredAssists = buffer[11];
    const size_t requiredLen = kKillMessageHeaderBytes + (4u * static_cast<size_t>(declaredAssists));
    if (bufferLen >= requiredLen) {
        return false;
    }
    const uint8_t maxSafeAssists = static_cast<uint8_t>((bufferLen - kKillMessageHeaderBytes) / 4u);
    if (outOldAssists) *outOldAssists = declaredAssists;
    if (outNewAssists) *outNewAssists = maxSafeAssists;
    buffer[11] = maxSafeAssists;
    return true;
}

bool IsKillMessageHeaderTruncated(
    const uint8_t* unreadData,
    size_t unreadBytes
) noexcept {
    // satisfies: OBL-MIDWARS-03
    return (unreadData != nullptr && unreadBytes > 0 && unreadBytes < kKillMessageHeaderBytes && unreadData[0] == kGameCmdKillMessage);
}

bool GuardClientKillMessagePacketRead(
    uint8_t* unreadData,
    size_t unreadBytes,
    uint8_t* outOldAssists,
    uint8_t* outNewAssists
) noexcept {
    // satisfies: OBL-MIDWARS-03
    return ClampKillMessageBufferInPlace(unreadData, unreadBytes, outOldAssists, outNewAssists);
}

bool CollectUniqueSortedAssistClientNums(
    const uintptr_t* unitPtrBegin,
    const uintptr_t* unitPtrEnd,
    uintptr_t attackerUnit,
    ptrdiff_t ownerClientOffset,
    int32_t* outAssists,
    size_t maxAssists,
    size_t* outCount
) noexcept {
    // satisfies: OBL-MIDWARS-04
    if (outCount) *outCount = 0;
    if (!unitPtrBegin || !unitPtrEnd || unitPtrEnd < unitPtrBegin || !outAssists || maxAssists == 0 || !outCount) {
        return false;
    }
    const size_t ptrCount = static_cast<size_t>(unitPtrEnd - unitPtrBegin);
    if (ptrCount > 256) {
        return false;
    }
    size_t count = 0;
    for (size_t i = 0; i < ptrCount; ++i) {
        const uintptr_t unitAddr = unitPtrBegin[i];
        if (unitAddr == 0 || unitAddr == attackerUnit) {
            continue;
        }
        const int32_t clientNum = *reinterpret_cast<const int32_t*>(unitAddr + ownerClientOffset);
        if (clientNum < 0) {
            continue;
        }
        bool duplicate = false;
        for (size_t j = 0; j < count; ++j) {
            if (outAssists[j] == clientNum) {
                duplicate = true;
                break;
            }
        }
        if (duplicate) {
            continue;
        }
        if (count >= maxAssists) {
            break;
        }
        // Insertion sort ascending to match std::set<int> iteration order
        size_t pos = count;
        while (pos > 0 && outAssists[pos - 1] > clientNum) {
            outAssists[pos] = outAssists[pos - 1];
            --pos;
        }
        outAssists[pos] = clientNum;
        ++count;
    }
    *outCount = count;
    return true;
}

bool BuildExpandedKillMessagePayload(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* sortedAssistClientNums,
    size_t assistCount,
    uint8_t* outBuf,
    size_t outCap,
    size_t* outLen
) noexcept {
    // satisfies: OBL-MIDWARS-04
    if (!origBufHeader || origLen < kKillMessageHeaderBytes || origBufHeader[0] != kGameCmdKillMessage) {
        return false;
    }
    if (!sortedAssistClientNums || !outBuf || outCap < kKillMessageHeaderBytes || !outLen) {
        return false;
    }
    const size_t maxByCap = (outCap - kKillMessageHeaderBytes) / 4u;
    const size_t effectiveAssists = (assistCount < maxByCap) ? assistCount : maxByCap;
    if (effectiveAssists > 255u) {
        return false;
    }

    memcpy(outBuf, origBufHeader, kKillMessageHeaderBytes);
    outBuf[11] = static_cast<uint8_t>(effectiveAssists);
    for (size_t i = 0; i < effectiveAssists; ++i) {
        memcpy(outBuf + kKillMessageHeaderBytes + (i * 4u), &sortedAssistClientNums[i], sizeof(int32_t));
    }
    *outLen = kKillMessageHeaderBytes + (effectiveAssists * 4u);
    return true;
}

bool FilterAndValidateExpandedKillAssists(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* candidateAssists,
    size_t candidateCount,
    int32_t* outFilteredAssists,
    size_t outCap,
    size_t* outFilteredCount
) noexcept {
    // satisfies: OBL-MIDWARS-04
    if (outFilteredCount) *outFilteredCount = 0;
    if (!origBufHeader || origLen < kKillMessageHeaderBytes || origBufHeader[0] != kGameCmdKillMessage) {
        return false;
    }
    if (!candidateAssists || !outFilteredAssists || !outFilteredCount) {
        return false;
    }
    const uint8_t declaredAssists = origBufHeader[11];
    if (declaredAssists < 5u || declaredAssists > 10u) {
        return false;
    }
    int32_t headerKiller = -1;
    int32_t headerVictim = -1;
    memcpy(&headerKiller, origBufHeader + 1, sizeof(int32_t));
    memcpy(&headerVictim, origBufHeader + 5, sizeof(int32_t));

    size_t filteredCount = 0;
    for (size_t i = 0; i < candidateCount; ++i) {
        const int32_t clientNum = candidateAssists[i];
        if (clientNum == headerKiller || clientNum == headerVictim) {
            continue;
        }
        if (filteredCount >= outCap) {
            return false;
        }
        outFilteredAssists[filteredCount++] = clientNum;
    }
    if (filteredCount != static_cast<size_t>(declaredAssists)) {
        return false;
    }
    *outFilteredCount = filteredCount;
    return true;
}

bool UpdateSnapshotDesyncTracker(
    SnapshotDesyncTrackerState* state,
    uint8_t receivedSeq,
    uint8_t expectedSeq,
    uint64_t nowTickMs,
    bool* outShouldRateLimitedLog
) noexcept {
    // satisfies: OBL-MIDWARS-05
    if (outShouldRateLimitedLog) *outShouldRateLimitedLog = false;
    if (!state) return false;

    state->lastReceivedSeq = receivedSeq;
    state->lastExpectedSeq = expectedSeq;

    // Modular uint8_t delta handles 255 -> 0 wraparound cleanly
    const uint8_t delta = static_cast<uint8_t>(receivedSeq - expectedSeq);
    if (delta == 0) {
        state->consecutiveMismatchFrames = 0;
        return false;
    }

    if (state->consecutiveMismatchFrames < 0xFFFFFFFFu) {
        ++state->consecutiveMismatchFrames;
    }
    if (state->consecutiveMismatchFrames < 2u) {
        return false;
    }

    if (outShouldRateLimitedLog) {
        if (state->totalAlertsEmitted == 0 ||
            nowTickMs < state->lastAlertTickMs ||
            (nowTickMs - state->lastAlertTickMs) >= 5000ULL) {
            state->lastAlertTickMs = nowTickMs;
            ++state->totalAlertsEmitted;
            *outShouldRateLimitedLog = true;
        }
    }
    return true;
}

namespace {

struct ThreadLocalExpandedKillAssists {
    size_t  count = 0;
    int32_t clientNums[kKillMessageMaxAssists128B] = {};
};

static thread_local ThreadLocalExpandedKillAssists t_pendingExpandedKillAssists;

typedef void (__fastcall *PFN_CGameServer_BroadcastGameData)(
    void* pGameServer, void* pBuffer, uint8_t bReliable, int32_t iExcludeClient, uint32_t uiDelay);
typedef void (__fastcall *PFN_CGameServer_SendGameData)(
    void* pGameServer, int32_t iClientNum, void* pBuffer, uint8_t bReliable, uint32_t uiDelay);
typedef void (__fastcall *PFN_CGameServer_BroadcastTeamFilter)(
    void* pGameServer, int32_t iFilterArg, void* pBuffer, uint8_t bReliable, uint32_t uiDelay);
typedef void (__fastcall *PFN_KillNotifyHelper)(
    void* pVictimUnit, void* pAttackerUnit, void* pKillerPlayer, void* pDamagerVector, uint16_t unGold);
typedef uint8_t (__fastcall *PFN_CClientGame_ProcessGameData)(
    void* pClientGame, void* pPacket, uint8_t bReliable);
typedef uint8_t (__fastcall *PFN_CHostClient_ProcessCurrentSnapshot)(
    void* pHostClient);

static PFN_CGameServer_BroadcastGameData       g_origGameServerBroadcastGameData = nullptr;
static PFN_CGameServer_SendGameData            g_origGameServerSendGameData = nullptr;
static PFN_CGameServer_BroadcastTeamFilter     g_origGameServerBroadcastTeam = nullptr;
static PFN_CGameServer_BroadcastTeamFilter     g_origGameServerBroadcastFilter = nullptr;
static PFN_KillNotifyHelper                    g_origKillNotifyHelper = nullptr;
static PFN_CClientGame_ProcessGameData         g_origCClientGameProcessGameData = nullptr;
static PFN_CHostClient_ProcessCurrentSnapshot  g_origCHostClientProcessCurrentSnapshot = nullptr;

static SRWLOCK g_midwarsHookLock = SRWLOCK_INIT;
static std::atomic<HMODULE> g_hookedMidwarsGameX64{ nullptr };
static std::atomic<HMODULE> g_hookedMidwarsGameShared{ nullptr };
static std::atomic<HMODULE> g_hookedMidwarsCGame{ nullptr };
static std::atomic<HMODULE> g_hookedMidwarsK2{ nullptr };
static std::atomic<DWORD>   g_midwarsSnapshotMainThreadId{ 0 };
static SnapshotDesyncTrackerState g_liveSnapshotDesyncState = {};

static void ClampIBufferKillPacketSeh(void* pIBuffer, const char* pathTag) noexcept {
    if (!pIBuffer) return;
    __try {
        auto* buf = reinterpret_cast<K2IBufferLayout*>(pIBuffer);
        if (!buf->m_pBuffer || buf->m_uiEnd < kKillMessageHeaderBytes || buf->m_uiEnd > buf->m_uiSize) {
            return;
        }
        uint8_t oldAssists = 0;
        uint8_t newAssists = 0;
        if (ClampKillMessageBufferInPlace(buf->m_pBuffer, buf->m_uiEnd, &oldAssists, &newAssists)) {
            g_killPacketClampCount.fetch_add(1, std::memory_order_relaxed);
            char logMsg[192];
            snprintf(logMsg, sizeof(logMsg),
                "[NATIVE-GUARD][KILL-PACKET-CLAMP] Clamped 0x60 numAssists %u -> %u on %s (len=%u)",
                static_cast<unsigned>(oldAssists), static_cast<unsigned>(newAssists),
                pathTag ? pathTag : "BroadcastGameData", static_cast<unsigned>(buf->m_uiEnd));
            LogArcadeLine(logMsg);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogArcadeLine("[NATIVE-GUARD][KILL-PACKET-CLAMP] [SEH] Ignored exception inspecting IBuffer.");
    }
}

static bool TryPrepareExpandedIBufferSeh(
    void* pOrigIBuffer,
    K2IBufferLayout* outExpandedBuf,
    uint8_t* outStorage,
    size_t storageCap
) noexcept {
    if (!pOrigIBuffer || !outExpandedBuf || !outStorage || storageCap < kKillMessageExpandedCapacity) {
        return false;
    }
    if (t_pendingExpandedKillAssists.count < 5) {
        return false;
    }
    __try {
        const auto* orig = reinterpret_cast<const K2IBufferLayout*>(pOrigIBuffer);
        if (!orig->m_pBuffer || orig->m_uiEnd < kKillMessageHeaderBytes || orig->m_pBuffer[0] != kGameCmdKillMessage) {
            return false;
        }
        int32_t filteredAssists[kKillMessageMaxAssists128B] = {};
        size_t filteredCount = 0;
        if (!FilterAndValidateExpandedKillAssists(
                orig->m_pBuffer, orig->m_uiEnd,
                t_pendingExpandedKillAssists.clientNums, t_pendingExpandedKillAssists.count,
                filteredAssists, kKillMessageMaxAssists128B, &filteredCount)) {
            return false;
        }
        size_t expandedLen = 0;
        if (!BuildExpandedKillMessagePayload(
                orig->m_pBuffer, orig->m_uiEnd,
                filteredAssists, filteredCount,
                outStorage, storageCap, &expandedLen)) {
            return false;
        }
        outExpandedBuf->vptr      = orig->vptr;
        outExpandedBuf->m_pBuffer = outStorage;
        outExpandedBuf->m_uiSize  = static_cast<uint32_t>(storageCap);
        outExpandedBuf->m_uiEnd   = static_cast<uint32_t>(expandedLen);
        outExpandedBuf->m_uiRead  = 0;
        outExpandedBuf->m_iFaults = 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static void __fastcall Hooked_CGameServer_BroadcastGameData(
    void* pGameServer, void* pBuffer, uint8_t bReliable, int32_t iExcludeClient, uint32_t uiDelay
) {
    K2IBufferLayout expandedIBuf{};
    uint8_t expandedStorage[kKillMessageExpandedCapacity] = {};
    void* effectiveBuffer = pBuffer;

    if (TryPrepareExpandedIBufferSeh(pBuffer, &expandedIBuf, expandedStorage, sizeof(expandedStorage))) {
        effectiveBuffer = &expandedIBuf;
        const size_t nAssists = t_pendingExpandedKillAssists.count;
        t_pendingExpandedKillAssists.count = 0;
        g_killPacketExpandCount.fetch_add(1, std::memory_order_relaxed);
        char logMsg[192];
        snprintf(logMsg, sizeof(logMsg),
            "[NATIVE-GUARD][KILL-PACKET-EXPAND] Expanded 0x60 kill packet to %u bytes (%zu assists).",
            static_cast<unsigned>(expandedIBuf.m_uiEnd), nAssists);
        LogArcadeLine(logMsg);
    }

    ClampIBufferKillPacketSeh(effectiveBuffer, "CGameServer::BroadcastGameData");
    if (g_origGameServerBroadcastGameData) {
        g_origGameServerBroadcastGameData(pGameServer, effectiveBuffer, bReliable, iExcludeClient, uiDelay);
    }
}

static void __fastcall Hooked_CGameServer_SendGameData(
    void* pGameServer, int32_t iClientNum, void* pBuffer, uint8_t bReliable, uint32_t uiDelay
) {
    ClampIBufferKillPacketSeh(pBuffer, "CGameServer::SendGameData");
    if (g_origGameServerSendGameData) {
        g_origGameServerSendGameData(pGameServer, iClientNum, pBuffer, bReliable, uiDelay);
    }
}

static void __fastcall Hooked_CGameServer_BroadcastTeam(
    void* pGameServer, int32_t iFilterArg, void* pBuffer, uint8_t bReliable, uint32_t uiDelay
) {
    ClampIBufferKillPacketSeh(pBuffer, "CGameServer::BroadcastTeam");
    if (g_origGameServerBroadcastTeam) {
        g_origGameServerBroadcastTeam(pGameServer, iFilterArg, pBuffer, bReliable, uiDelay);
    }
}

static void __fastcall Hooked_CGameServer_BroadcastFilter(
    void* pGameServer, int32_t iFilterArg, void* pBuffer, uint8_t bReliable, uint32_t uiDelay
) {
    ClampIBufferKillPacketSeh(pBuffer, "CGameServer::BroadcastFilter");
    if (g_origGameServerBroadcastFilter) {
        g_origGameServerBroadcastFilter(pGameServer, iFilterArg, pBuffer, bReliable, uiDelay);
    }
}

static void CapturePendingExpandedAssistsSeh(void* pAttackerUnit, void* pDamagerVector) noexcept {
    t_pendingExpandedKillAssists.count = 0;
    if (!pDamagerVector) return;
    __try {
        const uintptr_t beginPtr = *reinterpret_cast<const uintptr_t*>(pDamagerVector);
        const uintptr_t endPtr   = *reinterpret_cast<const uintptr_t*>(reinterpret_cast<const uint8_t*>(pDamagerVector) + 8);
        if (!beginPtr || !endPtr || endPtr < beginPtr || ((beginPtr | endPtr) & (sizeof(uintptr_t) - 1)) != 0) {
            return;
        }
        size_t assistCount = 0;
        if (CollectUniqueSortedAssistClientNums(
                reinterpret_cast<const uintptr_t*>(beginPtr),
                reinterpret_cast<const uintptr_t*>(endPtr),
                reinterpret_cast<uintptr_t>(pAttackerUnit),
                0x6D4,
                t_pendingExpandedKillAssists.clientNums,
                kKillMessageMaxAssists128B,
                &assistCount)) {
            t_pendingExpandedKillAssists.count = (assistCount >= 5) ? assistCount : 0;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        t_pendingExpandedKillAssists.count = 0;
    }
}

static void __fastcall Hooked_KillNotifyHelper(
    void* pVictimUnit, void* pAttackerUnit, void* pKillerPlayer, void* pDamagerVector, uint16_t unGold
) {
    CapturePendingExpandedAssistsSeh(pAttackerUnit, pDamagerVector);
    if (g_origKillNotifyHelper) {
        g_origKillNotifyHelper(pVictimUnit, pAttackerUnit, pKillerPlayer, pDamagerVector, unGold);
    }
    t_pendingExpandedKillAssists.count = 0;
}

static int InspectAndGuardClientKillPacketSeh(void* pPacket) noexcept {
    // Returns: 0 = ok/no-op, 1 = clamped, -1 = truncated header (< 12B) drop safely
    if (!pPacket) return 0;
    __try {
        auto* buf = reinterpret_cast<K2IBufferLayout*>(reinterpret_cast<uint8_t*>(pPacket) + 8);
        if (!buf->m_pBuffer || buf->m_uiEnd > buf->m_uiSize || buf->m_uiRead >= buf->m_uiEnd) {
            return 0;
        }
        uint8_t* unreadPtr = buf->m_pBuffer + buf->m_uiRead;
        const size_t unreadBytes = static_cast<size_t>(buf->m_uiEnd - buf->m_uiRead);
        if (unreadBytes > 0 && unreadPtr[0] == kGameCmdKillMessage) {
            const unsigned declaredAssists = (unreadBytes >= kKillMessageHeaderBytes)
                ? static_cast<unsigned>(unreadPtr[11]) : 255u;
            char obsMsg[192];
            snprintf(obsMsg, sizeof(obsMsg),
                "[NATIVE-GUARD][CLIENT-KILL-0x60] ProcessGameData 0x60 (numAssists=%u, unreadBytes=%zu)",
                declaredAssists, unreadBytes);
            LogArcadeLine(obsMsg);
        }
        if (IsKillMessageHeaderTruncated(unreadPtr, unreadBytes)) {
            g_replayKillReadClampCount.fetch_add(1, std::memory_order_relaxed);
            buf->m_uiRead = buf->m_uiEnd;
            return -1;
        }
        uint8_t oldAssists = 0;
        uint8_t newAssists = 0;
        if (GuardClientKillMessagePacketRead(unreadPtr, unreadBytes, &oldAssists, &newAssists)) {
            g_replayKillReadClampCount.fetch_add(1, std::memory_order_relaxed);
            char logMsg[192];
            snprintf(logMsg, sizeof(logMsg),
                "[NATIVE-GUARD][REPLAY-KILL-READ-GUARD] Clamped 0x60 numAssists %u -> %u (unread=%zu)",
                static_cast<unsigned>(oldAssists), static_cast<unsigned>(newAssists), unreadBytes);
            LogArcadeLine(logMsg);
            return 1;
        }
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

static uint8_t __fastcall Hooked_CClientGame_ProcessGameData(
    void* pClientGame, void* pPacket, uint8_t bReliable
) {
    if (InspectAndGuardClientKillPacketSeh(pPacket) < 0) {
        LogArcadeLine("[NATIVE-GUARD][REPLAY-KILL-READ-GUARD] Dropped truncated <12B 0x60 packet safely.");
        return 1;
    }
    return g_origCClientGameProcessGameData ? g_origCClientGameProcessGameData(pClientGame, pPacket, bReliable) : 0;
}

static void SampleHostClientSnapshotDesyncSeh(const void* pHostClient) noexcept {
    if (!pHostClient) return;
    __try {
        const auto* base = reinterpret_cast<const uint8_t*>(pHostClient);
        const uint8_t receivedSeq = base[0x8808];
        const uint8_t expectedSeq = base[0x88A8];
        bool shouldAlert = false;
        if (UpdateSnapshotDesyncTracker(&g_liveSnapshotDesyncState, receivedSeq, expectedSeq, GetTickCount64(), &shouldAlert)) {
            const uint64_t totalDrops = g_snapshotDesyncDropCount.fetch_add(1, std::memory_order_relaxed) + 1;
            if (shouldAlert) {
                char logMsg[192];
                snprintf(logMsg, sizeof(logMsg),
                    "[NATIVE-GUARD][SNAPSHOT-DESYNC] Detected persistent state sequence mismatch (received=%u, expected=%u, consecutive=%u, drops=%llu)",
                    static_cast<unsigned>(receivedSeq), static_cast<unsigned>(expectedSeq),
                    g_liveSnapshotDesyncState.consecutiveMismatchFrames,
                    static_cast<unsigned long long>(totalDrops));
                LogArcadeLine(logMsg);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Ignore transient teardown read faults cleanly
    }
}

static uint8_t __fastcall Hooked_CHostClient_ProcessCurrentSnapshot(void* pHostClient) {
    // satisfies: OBL-MIDWARS-05
    const DWORD curTid = GetCurrentThreadId();
    DWORD expectedTid = g_midwarsSnapshotMainThreadId.load(std::memory_order_relaxed);
    if (expectedTid == 0) {
        DWORD zero = 0;
        g_midwarsSnapshotMainThreadId.compare_exchange_strong(zero, curTid, std::memory_order_acq_rel);
        expectedTid = g_midwarsSnapshotMainThreadId.load(std::memory_order_relaxed);
    }
    if (curTid == expectedTid) {
        SampleHostClientSnapshotDesyncSeh(pHostClient);
    }
    return g_origCHostClientProcessCurrentSnapshot ? g_origCHostClientProcessCurrentSnapshot(pHostClient) : 0;
}

} // anonymous namespace

bool EnsureMidwarsServerHooksInstalled() noexcept {
    // satisfies: OBL-MIDWARS-01
    // satisfies: OBL-MIDWARS-02
    // satisfies: OBL-MIDWARS-04
    HMODULE hGame = GetModuleHandleA("game_x64.dll");
    HMODULE hGS   = GetModuleHandleA("game_shared_x64.dll");
    if (!hGame && !hGS) {
        return false;
    }

    AcquireSRWLockExclusive(&g_midwarsHookLock);

    bool ok = true;

    if (hGame && g_hookedMidwarsGameX64.load(std::memory_order_acquire) != hGame) {
        uint8_t* gameBase = reinterpret_cast<uint8_t*>(hGame);

        static const uint8_t patBroadcast15[15] = {
            0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18
        };
        static const uint8_t patBroadcastTeam15[15] = {
            0x4C, 0x8B, 0xDC, 0x49, 0x89, 0x5B, 0x08, 0x49, 0x89, 0x6B, 0x10, 0x49, 0x89, 0x73, 0x18
        };

        const bool okBcast = Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "CGameServer::BroadcastGameData", gameBase + 0x50F30,
            reinterpret_cast<void*>(Hooked_CGameServer_BroadcastGameData),
            reinterpret_cast<void**>(&g_origGameServerBroadcastGameData),
            15, patBroadcast15, sizeof(patBroadcast15));

        const bool okSend = Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "CGameServer::SendGameData", gameBase + 0x509F0,
            reinterpret_cast<void*>(Hooked_CGameServer_SendGameData),
            reinterpret_cast<void**>(&g_origGameServerSendGameData),
            15, patBroadcast15, sizeof(patBroadcast15));

        const bool okTeam = Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "CGameServer::BroadcastGameDataTeam", gameBase + 0x50CF0,
            reinterpret_cast<void*>(Hooked_CGameServer_BroadcastTeam),
            reinterpret_cast<void**>(&g_origGameServerBroadcastTeam),
            15, patBroadcastTeam15, sizeof(patBroadcastTeam15));

        const bool okFilter = Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "CGameServer::BroadcastGameDataTeamFilter", gameBase + 0x50AF0,
            reinterpret_cast<void*>(Hooked_CGameServer_BroadcastFilter),
            reinterpret_cast<void**>(&g_origGameServerBroadcastFilter),
            15, patBroadcastTeam15, sizeof(patBroadcastTeam15));

        if (okBcast) {
            g_hookedMidwarsGameX64.store(hGame, std::memory_order_release);
            LogArcadeLine("[NATIVE-GUARD][KILL-PACKET] Installed Stage 1A write-side framing guards on game_x64.dll (BroadcastGameData/SendGameData).");
        } else {
            ok = false;
        }
        (void)okSend; (void)okTeam; (void)okFilter;
    }

    if (hGS && g_hookedMidwarsGameShared.load(std::memory_order_acquire) != hGS) {
        const bool isClientGS = IsGameSharedClientModule(hGS);
        const uintptr_t killHelperRva = isClientGS ? 0x575460 : 0x5761A0;
        uint8_t* gsBase = reinterpret_cast<uint8_t*>(hGS);

        static const uint8_t patKillHelper14[14] = {
            0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56
        };

        const bool okKillHelper = Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "KillNotifyHelper_0x60", gsBase + killHelperRva,
            reinterpret_cast<void*>(Hooked_KillNotifyHelper),
            reinterpret_cast<void**>(&g_origKillNotifyHelper),
            14, patKillHelper14, sizeof(patKillHelper14));

        if (okKillHelper) {
            g_hookedMidwarsGameShared.store(hGS, std::memory_order_release);
            LogArcadeLine("[NATIVE-GUARD][KILL-PACKET] Installed Stage 1B 128B kill assist expansion hook on game_shared_x64.dll.");
        } else {
            ok = false;
        }
    }

    ReleaseSRWLockExclusive(&g_midwarsHookLock);
    return ok;
}

bool EnsureMidwarsClientReplayGuardInstalled(void* hCGameModule) noexcept {
    // satisfies: OBL-MIDWARS-03
    HMODULE hCGame = reinterpret_cast<HMODULE>(hCGameModule);
    if (!hCGame) return false;
    if (g_hookedMidwarsCGame.load(std::memory_order_acquire) == hCGame) return true;

    AcquireSRWLockExclusive(&g_midwarsHookLock);
    if (g_hookedMidwarsCGame.load(std::memory_order_acquire) == hCGame) {
        ReleaseSRWLockExclusive(&g_midwarsHookLock);
        return true;
    }

    static const uint8_t patProcessGameData15[15] = {
        0x44, 0x88, 0x44, 0x24, 0x18, 0x48, 0x89, 0x4C, 0x24, 0x08, 0x53, 0x56, 0x57, 0x41, 0x54
    };
    uint8_t* target = reinterpret_cast<uint8_t*>(hCGame) + 0xD7690;
    const bool ok = Kongor::PatchController::InstallManagedInlineHook64(
        "seh", "CClientGame::ProcessGameData_0x60", target,
        reinterpret_cast<void*>(Hooked_CClientGame_ProcessGameData),
        reinterpret_cast<void**>(&g_origCClientGameProcessGameData),
        15, patProcessGameData15, sizeof(patProcessGameData15));

    if (ok) {
        g_hookedMidwarsCGame.store(hCGame, std::memory_order_release);
        LogArcadeLine("[NATIVE-GUARD][REPLAY-KILL-READ-GUARD] Installed Stage 1A-client 0x60 read-bounds guard on cgame_x64.dll.");
    }
    ReleaseSRWLockExclusive(&g_midwarsHookLock);
    return ok;
}

bool EnsureMidwarsSnapshotDesyncHookInstalled(void* hK2ClientModule) noexcept {
    // satisfies: OBL-MIDWARS-05
    HMODULE hK2 = reinterpret_cast<HMODULE>(hK2ClientModule);
    if (!hK2 || !IsK2ClientModule(hK2)) return false;
    if (g_hookedMidwarsK2.load(std::memory_order_acquire) == hK2) return true;

    AcquireSRWLockExclusive(&g_midwarsHookLock);
    if (g_hookedMidwarsK2.load(std::memory_order_acquire) == hK2) {
        ReleaseSRWLockExclusive(&g_midwarsHookLock);
        return true;
    }

    static const uint8_t patProcessSnapshot15[15] = {
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20
    };
    uint8_t* target = reinterpret_cast<uint8_t*>(hK2) + 0x8D7C20;
    const bool ok = Kongor::PatchController::InstallManagedInlineHook64(
        "seh", "CHostClient::ProcessCurrentSnapshot_Telemetry", target,
        reinterpret_cast<void*>(Hooked_CHostClient_ProcessCurrentSnapshot),
        reinterpret_cast<void**>(&g_origCHostClientProcessCurrentSnapshot),
        15, patProcessSnapshot15, sizeof(patProcessSnapshot15));

    if (ok) {
        g_hookedMidwarsK2.store(hK2, std::memory_order_release);
        LogArcadeLine("[NATIVE-GUARD][SNAPSHOT-DESYNC] Installed Layer 2 telemetry hook on CHostClient::ProcessCurrentSnapshot (k2_x64.dll+0x8d7c20).");
    }
    ReleaseSRWLockExclusive(&g_midwarsHookLock);
    return ok;
}

} // namespace Kongor

std::atomic<uint64_t> g_killPacketClampCount{ 0 };
std::atomic<uint64_t> g_killPacketExpandCount{ 0 };
std::atomic<uint64_t> g_replayKillReadClampCount{ 0 };
std::atomic<uint64_t> g_snapshotDesyncDropCount{ 0 };

void RefreshServerKeyframePlayersFromEngine() noexcept {
    Kongor::RefreshServerKeyframePlayersFromEngine(nullptr);
}

uint64_t GetLastHeroKeyframeSampleTickMs() noexcept {
    return Kongor::GetLastHeroKeyframeSampleTickMs();
}
