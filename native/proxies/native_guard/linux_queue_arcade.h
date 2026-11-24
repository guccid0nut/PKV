// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

#ifndef KONGOR_LINUX_QUEUE_ARCADE_H
#define KONGOR_LINUX_QUEUE_ARCADE_H
// ============================================================================
// File: linux_queue_arcade.h
// Purpose: Linux Queue Arcade ("While You Wait" Mini-Game) Architecture for
//          hon-x86_64-server (libk2-x86_64.so, libgame_shared-x86_64-server.so,
//          game-x86_64-server.so).
// Dossier: artifacts/evidence/queue_arcade_linux_dossier.json
// Spec: docs/audits/linux-queue-arcade-dossier-spec.md
// ============================================================================

#if !defined(_WIN32) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif

#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <atomic>

#if !defined(_WIN32)
#include <wchar.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/uio.h>
#include <unistd.h>
#include <signal.h>
#include <setjmp.h>
#include <errno.h>
#include <dlfcn.h>
#include <link.h>
#endif

#if defined(__GNUC__) || defined(__clang__)
#define KONGOR_ARCADE_API __attribute__((visibility("default")))
#else
#define KONGOR_ARCADE_API
#endif

namespace Kongor {

// Mode 0 = Disabled (standard Ranked / Public server; zero detours active)
// Mode 1 = Queue Arcade Active-Bots Mode (Caldavar, Mid Wars, Grimm's Crossing)
// Mode 2 = Queue Arcade Party Dormant-Slots Mode (Soccer, CTF, TDM, DevoWars, 10v10)
enum class QueueArcadeServerMode : int32_t {
    Disabled = 0,
    ActiveBots = 1,
    PartyDormantSlots = 2
};

constexpr size_t kMaxArcadeReservations = 32;

// ============================================================================
// Linux ELF Verified Struct Offsets (Capstone Disassembly Citations)
// Verified in docs/audits/linux-queue-arcade-dossier-spec.md
// ============================================================================
namespace LinuxOffsets {
    // CPlayer offsets (libgame_shared-x86_64-server.so)
    constexpr uintptr_t kPlayerClientNumber             = 0x09C; // int32_t (Initialize @ 0x00C94AA6)
    constexpr uintptr_t kPlayerTeam                     = 0x0A0; // int32_t (Terminate @ 0x00C98EAC)
    constexpr uintptr_t kPlayerHeroIndex                = 0x0A4; // uint32_t (SetHeroIndex @ 0x00C99C70)
    constexpr uintptr_t kPlayerAccountID                = 0x2D0; // int32_t (Initialize @ 0x00C94AB0)
    constexpr uintptr_t kPlayerNameIndex                = 0x2D8; // uint16_t (Initialize @ 0x00C94ACE)
    constexpr uintptr_t kPlayerClanTagIndex             = 0x2DA; // uint16_t (Initialize @ 0x00C94ADF)
    constexpr uintptr_t kPlayerClanNameIndex            = 0x2DC; // uint16_t (Initialize @ 0x00C94AFA)
    constexpr uintptr_t kPlayerFlags                    = 0x3CE; // uint16_t (Disconnected @ 0x00C98E07)
    constexpr uintptr_t kPlayerFlags2                   = 0x3D0; // uint16_t (AttachBot @ 0x00C9535A, RemoveBot @ 0x00C953B6)
    constexpr uintptr_t kPlayerPing                     = 0x3F4; // uint16_t (Initialize @ 0x00C9466D)
    constexpr uintptr_t kPlayerDisconnectTime           = 0x420; // uint32_t (Disconnected @ 0x00C98E15)
    constexpr uintptr_t kPlayerTotalDiscTime            = 0x424; // uint32_t (Disconnected @ 0x00C98E1C)
    constexpr uintptr_t kPlayerDiscTermTime             = 0x428; // uint32_t (Disconnected @ 0x00C98E42)
    constexpr uintptr_t kPlayerOverrideByte             = 0x50A; // uint8_t (SetupClientConnection @ 0x000A0ACE)
    constexpr uintptr_t kPlayerClientFlags              = 0x648; // uint32_t (Initialize @ 0x00C94B8F)
    constexpr uintptr_t kPlayerMatchmakingPostInit      = 0x650; // uint8_t (EndHeroLoading @ 0x000E4EC5)
    constexpr uintptr_t kPlayerIsBot                    = 0x653; // uint8_t (Initialize @ 0x00C94A9B)
    constexpr uintptr_t kPlayerBotBrain                 = 0x730; // void* (RemoveBot @ 0x00C953A4)

    // CBotBrain offset
    constexpr uintptr_t kBotBrainHeroIndex              = 0x010; // uint32_t (CBotBrain ctor @ 0x00F63CBE)

    // CGameServer / IGame offsets
    constexpr uintptr_t kGameEntityDirectory            = 0x028; // void* (EndHeroLoading @ 0x000E4D4E)
    constexpr uintptr_t kGameInfo                       = 0x0C0; // void* (AddBot @ 0x001040D2)
    constexpr uintptr_t kGameClientsMap                 = 0x210; // LLVM libc++ std::__tree (BotFrame @ 0x000CCA31)
    constexpr uintptr_t kGameClientsMapSentinel         = 0x218; // Inline end-node address

    // CGameInfo offset
    constexpr uintptr_t kGameInfoPhase                  = 0x0A8; // int32_t (AddBot @ 0x001040DE)

    // Vtable offsets
    constexpr uintptr_t kVtableEntityDirGetEntity       = 0x018; // GetEntityFromIndex (EndHeroLoading @ 0x000E4D55)
    constexpr uintptr_t kVtableUnitGetAsUnit            = 0x178; // GetAsUnit (EndHeroLoading @ 0x000E4D67)

    // Target Function RVAs
    constexpr uintptr_t kRvaGenerateClientID            = 0x00CC8AA0; // libk2-x86_64.so
    constexpr uintptr_t kRvaHostServerGetClient         = 0x00CC8540; // libk2-x86_64.so (_ZN11CHostServer9GetClientEi)
    constexpr uintptr_t kRvaHostServerKickClient        = 0x00CD89E0; // libk2-x86_64.so (_ZN11CHostServer10KickClientEi17EDisconnectReason)
    constexpr uintptr_t kHostServerPtrOnCHost           = 0x000000D8; // CHost::m_pHostServer (GetServerWorldName @ 0x005A6AE0)
    constexpr uintptr_t kRvaSpawnHero                   = 0x00C99C90; // libgame_shared-x86_64-server.so
    constexpr uintptr_t kRvaAttachBot                   = 0x00C952B0; // libgame_shared-x86_64-server.so (_ZN7CPlayer9AttachBotEPK14CBotDefinition)
    constexpr uintptr_t kRvaRemoveBot                   = 0x00C953A0; // libgame_shared-x86_64-server.so
    constexpr uintptr_t kRvaTerminate                   = 0x00C98E90; // libgame_shared-x86_64-server.so
    constexpr uintptr_t kRvaDisconnected                = 0x00C98E00; // libgame_shared-x86_64-server.so
    constexpr uintptr_t kRvaIGameInstance               = 0x017CE960; // libgame_shared (_ZN5IGame7s_pGameE)

    // 10v10 Mid Wars Kill Packet (0x60) & Broadcast RVAs
    constexpr uintptr_t kUnitOwnerClientNumber          = 0x06BC;     // IUnitEntity::m_iOwnerClientNumber (0xD1E989)
    constexpr uintptr_t kRvaKillNotifyHelper            = 0x00D1E3B0; // libgame_shared-x86_64-server.so / libgame_shared-x86_64.so
    constexpr uintptr_t kRvaGameServerBroadcastGameData = 0x0012F260; // game-x86_64-server.so
    constexpr uintptr_t kRvaGameServerSendGameData      = 0x0012F510; // game-x86_64-server.so
    constexpr uintptr_t kRvaHostServerBroadcastGameData = 0x00CD77E0; // libk2-x86_64-server.so (_ZN11CHostServer17BroadcastGameDataERK7IBufferbi)
    constexpr uintptr_t kRvaHostServerSendGameData      = 0x00CD7760; // libk2-x86_64-server.so (_ZN11CHostServer12SendGameDataEiRK7IBufferb)
} // namespace LinuxOffsets

struct ArcadeReservationEntry {
    bool    active;
    int32_t accountId;
    int32_t clientNum;
    int32_t teamId;
    void*   parkedBotBrain;
    wchar_t playerName[64];
    wchar_t savedBotName[64];
};

struct LinuxQueueArcadeEngineContext {
    void** ppGame; // IGame** (_ZN5IGame7s_pGameE @ 0x017CE960)
    void (*pfnRemoveBot)(void* pPlayer); // _ZN7CPlayer9RemoveBotEv @ 0x00C953A0
    void* (*pfnAttachBot)(void* pPlayer, const void* pBotDef); // _ZN7CPlayer9AttachBotEPK14CBotDefinition @ 0x00C952B0
    void** ppNetResourceManager;
    void (*pfnSetNetString)(void* pNetResMgr, uint16_t unIndex, const void* pK2WString, bool bGrow);
    const void* (*pfnGetNetString)(void* pNetResMgr, uint16_t unIndex);
    void* pHostServerOverride; // Optional explicit CHostServer* for unit tests
    void** ppHost; // CHost** (g_pHost) -> *(void**)(*ppHost + 0xD8)
    void* (*pfnGetClient)(void* pHostServer, int32_t clientNum); // _ZN11CHostServer9GetClientEi @ 0x00CC8540
    void (*pfnKickClient)(void* pHostServer, int32_t clientNum, int32_t reason); // _ZN11CHostServer10KickClientEi17EDisconnectReason @ 0x00CD89E0
    void (*pfnQueueConsoleCommandA)(const char* cmdLine); // Queues ServerChat via EnqueueLinuxConsoleCommand / AddCmdBuffer
};

struct LinuxQueueArcadeSlotReservationResult {
    bool     success;
    int32_t  clientNum;
    int32_t  teamId;
    int32_t  accountId;
    bool     removedBotBrain;
    bool     updatedNetStrings;
    uint16_t priorFlags3CE;
    uint16_t postFlags3CE;
    uint8_t  priorPostInit650;
    uint8_t  postInit650;
    uint32_t priorDiscTerm428;
    uint32_t postDiscTerm428;
    void*    detachedBotBrain;
    wchar_t  previousBotName[64];
    char     reason[96];
};

// LLVM libc++.so.1 std::__tree RB-Tree node layout at IGame+0x210
struct LibcxxTreeNode {
    void*   left;      // +0x00 (__left_)
    void*   right;     // +0x08 (__right_)
    void*   parent;    // +0x10 (__parent_)
    bool    isBlack;   // +0x18 (__is_black_)
    uint8_t _pad0[7];  // +0x19 alignment padding up to +0x20
    int32_t key;       // +0x20 int clientNum
    uint8_t _pad1[4];  // +0x24 alignment padding up to +0x28
    void*   value;     // +0x28 CPlayer*
};

static_assert(offsetof(LibcxxTreeNode, left) == 0x00, "offset left");
static_assert(offsetof(LibcxxTreeNode, right) == 0x08, "offset right");
static_assert(offsetof(LibcxxTreeNode, parent) == 0x10, "offset parent");
static_assert(offsetof(LibcxxTreeNode, isBlack) == 0x18, "offset isBlack");
static_assert(offsetof(LibcxxTreeNode, key) == 0x20, "offset key");
static_assert(offsetof(LibcxxTreeNode, value) == 0x28, "offset value");
static_assert(sizeof(LibcxxTreeNode) == 0x30, "sizeof LibcxxTreeNode");

// Read-only stack view matching LLVM libc++ long wstring layout (UTF-32 on Linux)
struct LibcxxFakeLongWStringView {
    uint64_t capWithLongBit; // ((len + 1) << 1) | 1ULL
    size_t   size;           // character count
    const wchar_t* data;     // pointer to UTF-32 buffer
};

// ============================================================================
// Two-Tier Fault Guard (POSIX Siglongjmp + Kernel-Validated Probing)
// satisfies: OBL-QA-LINUX-00
// ============================================================================
#if !defined(_WIN32)
struct LinuxCallFaultGuard {
    sigjmp_buf jmpBuf;
    volatile sig_atomic_t armed;
};

KONGOR_ARCADE_API extern __thread LinuxCallFaultGuard t_callFaultGuard;

// Tier 1: Page-spanning process_vm_readv kernel validation.
// Probes every 4096-byte page boundary across [ptr, ptr + size - 1].
// Returns true iff every spanned page is valid and readable in this process.
// Bounded strictly to event-driven slot operations, never per-tick loops.
// satisfies: OBL-QA-LINUX-00
static inline bool IsValidReadableMemory(const void* ptr, size_t size) noexcept {
    if (!ptr || size == 0) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(ptr);
    const uintptr_t end = start + size - 1;
    if (start < 0x10000ULL || end < start) return false;

    const uintptr_t firstPage = start & ~static_cast<uintptr_t>(0xFFFULL);
    const uintptr_t lastPage  = end   & ~static_cast<uintptr_t>(0xFFFULL);
    for (uintptr_t page = firstPage; page <= lastPage; page += 0x1000ULL) {
        uintptr_t probeAddr = (page == firstPage) ? start : page;
        char probeByte = 0;
        struct iovec local  = { &probeByte, 1 };
        struct iovec remote = { reinterpret_cast<void*>(probeAddr), 1 };
        if (process_vm_readv(getpid(), &local, 1, &remote, 1, 0) != 1) {
            return false;
        }
        if (page == lastPage) break;
    }
    return true;
}

#define LINUX_CALL_FAULT_GUARD_BEGIN() \
    if (sigsetjmp(::Kongor::t_callFaultGuard.jmpBuf, 1) == 0) { \
        ::Kongor::t_callFaultGuard.armed = 1;

#define LINUX_CALL_FAULT_GUARD_END(recovery_stmt) \
        ::Kongor::t_callFaultGuard.armed = 0; \
    } else { \
        ::Kongor::t_callFaultGuard.armed = 0; \
        recovery_stmt; \
    }
#else
// Simulated fallback for cross-OS compilation
static inline bool IsValidReadableMemory(const void* ptr, size_t size) noexcept {
    if (!ptr || size == 0) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(ptr);
    return (start >= 0x10000ULL && (start + size - 1) >= start);
}
#define LINUX_CALL_FAULT_GUARD_BEGIN() if (true) {
#define LINUX_CALL_FAULT_GUARD_END(recovery_stmt) }
#endif

// ============================================================================
// String Utilities (LLVM libc++ UTF-32 & Clan Tag Matching)
// ============================================================================

static inline const wchar_t* StripLeadingClanTagW(const wchar_t* name) noexcept {
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

static inline bool NamesMatchPlayerW(const wchar_t* a, const wchar_t* b) noexcept {
    if (!a || !b || !*a || !*b) return false;
#if !defined(_WIN32)
    if (wcscasecmp(a, b) == 0) return true;
#else
    if (_wcsicmp(a, b) == 0) return true;
#endif
    const wchar_t* sa = StripLeadingClanTagW(a);
    const wchar_t* sb = StripLeadingClanTagW(b);
#if !defined(_WIN32)
    return (*sa != L'\0' && *sb != L'\0' && wcscasecmp(sa, sb) == 0);
#else
    return (*sa != L'\0' && *sb != L'\0' && _wcsicmp(sa, sb) == 0);
#endif
}

static inline void SplitDisplayNameAndClanTagW(
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
                size_t copyLen = (tagLen < clanCap - 1) ? tagLen : (clanCap - 1);
                wmemcpy(outBareClanTag, namePtr + 1, copyLen);
                outBareClanTag[copyLen] = L'\0';
            }
            namePtr = closeBracket + 1;
            while (*namePtr == L' ' || *namePtr == L'\t') ++namePtr;
        }
    }

    if (outBareName && bareCap > 1 && *namePtr != L'\0') {
        size_t nLen = wcslen(namePtr);
        size_t copyLen = (nLen < bareCap - 1) ? nLen : (bareCap - 1);
        wmemcpy(outBareName, namePtr, copyLen);
        outBareName[copyLen] = L'\0';
    }

    if (explicitClanTag && *explicitClanTag && outBareClanTag && clanCap > 1) {
        const wchar_t* cPtr = explicitClanTag;
        while (*cPtr == L' ' || *cPtr == L'\t' || *cPtr == L'[') ++cPtr;
        size_t cLen = wcslen(cPtr);
        while (cLen > 0 && (cPtr[cLen - 1] == L']' || cPtr[cLen - 1] == L' ' || cPtr[cLen - 1] == L'\t')) {
            --cLen;
        }
        if (cLen > 0) {
            size_t copyLen = (cLen < clanCap - 1) ? cLen : (clanCap - 1);
            wmemcpy(outBareClanTag, cPtr, copyLen);
            outBareClanTag[copyLen] = L'\0';
        }
    }
}

// Decodes a 24-byte LLVM libc++.so.1 std::basic_string<wchar_t> (UTF-32 on Linux)
static inline bool ReadLibcxxWString(const void* pK2Str, wchar_t* outBuf, size_t outCap) noexcept {
    if (!pK2Str || !outBuf || outCap == 0) return false;
    outBuf[0] = L'\0';

    if (!IsValidReadableMemory(pK2Str, 24)) {
        return false;
    }

    const uint8_t* raw = reinterpret_cast<const uint8_t*>(pK2Str);
    const uint8_t modeByte = raw[0];
    size_t strLen = 0;
    const wchar_t* strData = nullptr;

    if ((modeByte & 1) == 0) {
        // Short string mode: bit 0 == 0, length = modeByte >> 1
        strLen = static_cast<size_t>(modeByte >> 1);
        strData = reinterpret_cast<const wchar_t*>(raw + 4);
    } else {
        // Long string mode: bit 0 == 1, length at +0x08, pointer at +0x10
        strLen = *reinterpret_cast<const size_t*>(raw + 0x08);
        strData = *reinterpret_cast<const wchar_t* const*>(raw + 0x10);
    }

    if (!strData || strLen >= outCap || strLen > 256) {
        return false;
    }

    if (!IsValidReadableMemory(strData, strLen * sizeof(wchar_t))) {
        return false;
    }

    for (size_t i = 0; i < strLen; ++i) {
        wchar_t ch = strData[i];
        if (ch == L'\0') {
            outBuf[i] = L'\0';
            return i > 0;
        }
        outBuf[i] = ch;
    }
    outBuf[strLen] = L'\0';
    return true;
}

static inline bool ReadLibcxxWStringAtConnName(const uint16_t* pConnId, wchar_t* outBuf, size_t outCap) noexcept {
    if (!pConnId || !outBuf || outCap == 0) return false;
    outBuf[0] = L'\0';
    if (*pConnId == 0xDCD8u) return false; // Bot/pseudo connection cookie sentinel
    // In CClientConnection: +0x20 unConnId, +0x28 m_sName -> (const uint8_t*)pConnId + 0x08
    const void* rawStr = reinterpret_cast<const uint8_t*>(pConnId) + 0x08;
    return ReadLibcxxWString(rawStr, outBuf, outCap);
}

static inline bool SetPlayerNetStringSlot(
    const LinuxQueueArcadeEngineContext* ctx,
    uint16_t strIndex,
    const wchar_t* text
) noexcept {
    if (!ctx || !ctx->ppNetResourceManager || !ctx->pfnSetNetString || strIndex == 0xFFFFu) {
        return false;
    }
    void* pNetMgr = *ctx->ppNetResourceManager;
    if (!pNetMgr) return false;

    const wchar_t* safeText = text ? text : L"";
    size_t len = wcslen(safeText);
    LibcxxFakeLongWStringView fakeStr{};
    fakeStr.capWithLongBit = ((len + 1) << 1) | 1ULL;
    fakeStr.size = len;
    fakeStr.data = safeText;
    ctx->pfnSetNetString(pNetMgr, strIndex, &fakeStr, false);
    return true;
}

// ============================================================================
// LLVM libc++ std::__tree Bounded Traversal Helper
// satisfies: OBL-QA-LINUX-02
// ============================================================================
template <typename Fn>
static inline bool TraverseClientsMap(void* pGameVoid, Fn&& callback) noexcept {
    if (!pGameVoid) return false;
    if (!IsValidReadableMemory(pGameVoid, LinuxOffsets::kGameClientsMapSentinel + sizeof(void*))) {
        return false;
    }

    const uint8_t* pGame = reinterpret_cast<const uint8_t*>(pGameVoid);
    const void* endNode = pGame + LinuxOffsets::kGameClientsMapSentinel;
    void* node = *reinterpret_cast<void* const*>(pGame + LinuxOffsets::kGameClientsMap);

    uint32_t step = 0;
    while (step++ < 256 && node != nullptr && node != endNode) {
        if (!IsValidReadableMemory(node, sizeof(LibcxxTreeNode))) {
            break;
        }

        const LibcxxTreeNode* tNode = reinterpret_cast<const LibcxxTreeNode*>(node);
        void* pPlayer = tNode->value;

        if (pPlayer != nullptr && IsValidReadableMemory(pPlayer, 0x800)) {
            if (!callback(tNode->key, pPlayer)) {
                break;
            }
        }

        // In-order traversal successor:
        if (tNode->right != nullptr) {
            node = tNode->right;
            uint32_t sub = 0;
            while (sub++ < 64 && node != nullptr) {
                if (!IsValidReadableMemory(node, sizeof(LibcxxTreeNode))) {
                    node = nullptr;
                    break;
                }
                const LibcxxTreeNode* cur = reinterpret_cast<const LibcxxTreeNode*>(node);
                if (cur->left == nullptr) break;
                node = cur->left;
            }
        } else {
            const void* p = tNode->parent;
            uint32_t sub = 0;
            while (sub++ < 64 && p != nullptr && p != endNode) {
                if (!IsValidReadableMemory(p, sizeof(LibcxxTreeNode))) {
                    p = nullptr;
                    break;
                }
                const LibcxxTreeNode* pNode = reinterpret_cast<const LibcxxTreeNode*>(p);
                if (node != pNode->right) break;
                node = const_cast<void*>(p);
                p = pNode->parent;
            }
            node = const_cast<void*>(p);
        }
    }
    return true;
}

// ============================================================================
// Public Function Declarations
// ============================================================================

KONGOR_ARCADE_API QueueArcadeServerMode GetQueueArcadeMode() noexcept;
KONGOR_ARCADE_API bool IsQueueArcadeModeActive() noexcept;
KONGOR_ARCADE_API void SetQueueArcadeMode(QueueArcadeServerMode mode) noexcept;
KONGOR_ARCADE_API bool IsQueueArcadeSwapHooksEnabled() noexcept;
KONGOR_ARCADE_API void SetQueueArcadeSwapHooksEnabled(bool enabled) noexcept;
KONGOR_ARCADE_API void RefreshQueueArcadeSwapHooksFromEnv() noexcept;
KONGOR_ARCADE_API void NotifyObservedArcadeHostServer(void* pHostServer) noexcept;
KONGOR_ARCADE_API bool IsArcadeKickReentrancyActive() noexcept;
KONGOR_ARCADE_API void RecordArcadeSlotOrigin(
    int32_t clientNum,
    const void* pBotDef,
    const wchar_t* originalBotName = nullptr
) noexcept;
KONGOR_ARCADE_API bool GetArcadeSlotOrigin(
    int32_t clientNum,
    const void** outBotDef,
    wchar_t* outOriginalBotName,
    size_t nameCap
) noexcept;
KONGOR_ARCADE_API void OnArcadePlayerAttachBot(
    void* pPlayer,
    const void* pBotDef,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;
KONGOR_ARCADE_API bool OnArcadePlayerDisconnected(
    void* pPlayer,
    uint32_t time,
    uint32_t maxDiscTime,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;
KONGOR_ARCADE_API bool OnArcadePlayerTerminate(
    void* pPlayer,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;
KONGOR_ARCADE_API bool IsQueueArcadeClientIdHookArmed() noexcept;
KONGOR_ARCADE_API bool ConsumePendingArcadeSafeModeRetirement() noexcept;
KONGOR_ARCADE_API bool EnsureQueueArcadeK2HooksInstalled() noexcept;

// satisfies: OBL-QA-LINUX-01
KONGOR_ARCADE_API void ReapConnectionlessBotsIfNoActiveHumans(const LinuxQueueArcadeEngineContext* ctx = nullptr) noexcept;

// satisfies: OBL-QA-LINUX-02
// satisfies: OBL-QA-LINUX-06
// satisfies: OBL-QA-LINUX-07
// satisfies: OBL-QA-LINUX-08
KONGOR_ARCADE_API LinuxQueueArcadeSlotReservationResult ArcadeReserveSlot(
    int32_t accountId,
    int32_t preferredTeam,
    const wchar_t* playerName,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr,
    const wchar_t* explicitClanTag = nullptr
) noexcept;

// satisfies: OBL-QA-LINUX-04
// satisfies: OBL-QA-LINUX-07
// satisfies: OBL-QA-LINUX-08
// satisfies: OBL-QA-LINUX-10
KONGOR_ARCADE_API bool ArcadeReleaseSlot(
    int32_t accountId,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr,
    bool kickClient = false
) noexcept;

// satisfies: OBL-QA-LINUX-04
// satisfies: OBL-QA-LINUX-06
KONGOR_ARCADE_API uint32_t ArcadeParkUnreservedBots(
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// satisfies: OBL-QA-LINUX-03
KONGOR_ARCADE_API int32_t ResolveReservedArcadeClientId(
    const uint16_t* pConnId,
    int32_t accountId
) noexcept;

// satisfies: OBL-QA-LINUX-10
KONGOR_ARCADE_API void ClearAllArcadeReservations(
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// satisfies: OBL-QA-LINUX-09
KONGOR_ARCADE_API bool ShouldSuppressPreMatchDuplicateHeroSpawn(
    void* pPlayer,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// satisfies: OBL-QA-LINUX-10
KONGOR_ARCADE_API bool ArcadeReleaseParkedBotOnPlayerTerminate(
    void* pPlayer,
    const LinuxQueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// satisfies: OBL-QA-LINUX-05
KONGOR_ARCADE_API void ExecuteConsoleArcadeModeW(const wchar_t* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeReserveW(const wchar_t* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeReleaseW(const wchar_t* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeSwapHooksW(const wchar_t* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeModeA(const char* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeReserveA(const char* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeReleaseA(const char* args) noexcept;
KONGOR_ARCADE_API void ExecuteConsoleArcadeSwapHooksA(const char* args) noexcept;
KONGOR_ARCADE_API bool ShouldAllowUnprivilegedIpcCommandBufferA(const char* buffer, size_t len) noexcept;
KONGOR_ARCADE_API bool TryDispatchLinuxArcadeConsoleCmdW(const wchar_t* cmdLine) noexcept;

// ============================================================================
// RTLD_LOCAL Module Discovery & Prologue-Verified W^X Trampoline Engine
// satisfies: OBL-QA-LINUX-11
// ============================================================================
KONGOR_ARCADE_API uintptr_t FindModuleBaseByName(const char* libSuffix) noexcept;
KONGOR_ARCADE_API bool VerifyLinuxPrologueNoRipRelative(const uint8_t* code, size_t len) noexcept;
KONGOR_ARCADE_API void* InstallLinuxInlineTrampoline64(
    void* targetFunc,
    void* hookFunc,
    const uint8_t* expectedPrologue,
    size_t prologueLen
) noexcept;

// ============================================================================
// 10v10 Mid Wars Kill Packet (0x60) Framing Guard & 128B Expansion (Linux ELF)
// Dossier: artifacts/evidence/midwars_10v10_kill_packet_truncation.json
// ============================================================================
constexpr uint8_t kGameCmdKillMessage          = 0x60;
constexpr size_t  kKillMessageHeaderBytes      = 12;
constexpr size_t  kKillMessageExpandedCapacity = 128;
constexpr size_t  kKillMessageMaxAssists128B   = (kKillMessageExpandedCapacity - kKillMessageHeaderBytes) / 4; // 29

struct K2IBufferLayout {
    void*    vptr;       // +0x00
    uint8_t* m_pBuffer;  // +0x08
    uint32_t m_uiSize;   // +0x10
    uint32_t m_uiEnd;    // +0x14
    uint32_t m_uiRead;   // +0x18
    uint32_t m_iFaults;  // +0x1C
};
static_assert(offsetof(K2IBufferLayout, m_pBuffer) == 0x08, "K2IBufferLayout m_pBuffer offset");
static_assert(offsetof(K2IBufferLayout, m_uiSize) == 0x10, "K2IBufferLayout m_uiSize offset");
static_assert(offsetof(K2IBufferLayout, m_uiEnd) == 0x14, "K2IBufferLayout m_uiEnd offset");
static_assert(offsetof(K2IBufferLayout, m_uiRead) == 0x18, "K2IBufferLayout m_uiRead offset");
static_assert(offsetof(K2IBufferLayout, m_iFaults) == 0x1C, "K2IBufferLayout m_iFaults offset");

KONGOR_ARCADE_API bool ClampKillMessageBufferInPlace(
    uint8_t* buffer,
    size_t bufferLen,
    uint8_t* outOldAssists = nullptr,
    uint8_t* outNewAssists = nullptr
) noexcept;

KONGOR_ARCADE_API bool CollectUniqueSortedAssistClientNums(
    const uintptr_t* unitPtrBegin,
    const uintptr_t* unitPtrEnd,
    uintptr_t attackerUnit,
    ptrdiff_t ownerClientOffset,
    int32_t* outAssists,
    size_t maxAssists,
    size_t* outCount
) noexcept;

KONGOR_ARCADE_API bool BuildExpandedKillMessagePayload(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* sortedAssistClientNums,
    size_t assistCount,
    uint8_t* outBuf,
    size_t outCap,
    size_t* outLen
) noexcept;

KONGOR_ARCADE_API bool FilterAndValidateExpandedKillAssists(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* candidateAssists,
    size_t candidateCount,
    int32_t* outFilteredAssists,
    size_t outCap,
    size_t* outFilteredCount
) noexcept;

KONGOR_ARCADE_API bool EnsureKillPacketGuardsApplied() noexcept;
KONGOR_ARCADE_API uint64_t GetLinuxKillPacketClampCount() noexcept;
KONGOR_ARCADE_API uint64_t GetLinuxKillPacketExpandCount() noexcept;

} // namespace Kongor

#endif // KONGOR_LINUX_QUEUE_ARCADE_H
