// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
// ============================================================================
// File: queue_arcade_hooks.h
// Purpose: Queue Mini-Game ("While You Wait" Arcade) drop-in/drop-out CPlayer
//          slot reservation, CBotBrain removal/dormancy, and CHostServer
//          GenerateClientID handshake matching for hon_x64.exe -dedicated.
// Dossier: artifacts/evidence/queue_arcade_slot_swap_dossier.json
// ============================================================================

#include <cstdint>
#include <cstddef>

namespace Kongor {

// Mode 0 = Disabled (standard Ranked / Public server; zero detours installed)
// Mode 1 = Queue Arcade Active-Bots Mode (Caldavar, Mid Wars, Grimm's Crossing)
// Mode 2 = Queue Arcade Party Dormant-Slots Mode (Soccer, CTF, TDM, DevoWars, 10v10)
enum class QueueArcadeServerMode : int32_t {
    Disabled = 0,
    ActiveBots = 1,
    PartyDormantSlots = 2
};

struct QueueArcadeEngineContext {
    void** ppGame; // IGame** (?s_pGame@IGame@@0PEAV1@EA)
    void (__fastcall *pfnRemoveBot)(void* pPlayer); // ?RemoveBot@CPlayer@@QEAAXXZ
    void* (__fastcall *pfnAttachBot)(void* pPlayer, const void* pBotDef); // ?AttachBot@CPlayer@@QEAAPEAVCBotBrain@@PEBVCBotDefinition@@@Z
    void** ppNetResourceManager; // CNetworkResourceManager** (?g_NetworkResourceManager@@3AEAVCNetworkResourceManager@@EA)
    void (__fastcall *pfnSetNetString)(void* pNetResMgr, uint16_t unIndex, const void* pK2WString, bool bGrow); // ?SetString@CNetworkResourceManager@@...
    const void* (__fastcall *pfnGetNetString)(void* pNetResMgr, uint16_t unIndex); // ?GetString@CNetworkResourceManager@@...
    void* pHostServerOverride; // Optional explicit CHostServer* for unit tests
    void** ppHost; // CHost** (?g_pHost@@3PEAVCHost@@EA)
    void* (__fastcall *pfnGetHostServer)(void* pHost); // ?GetServer@CHost@@QEAAPEAVCHostServer@@XZ
    void* (__fastcall *pfnGetClient)(void* pHostServer, int32_t clientNum); // ?GetClient@CHostServer@@QEAAPEAVCClientConnection@@H@Z
    void (__fastcall *pfnKickClient)(void* pHostServer, int32_t clientNum, int32_t reason); // ?KickClient@CHostServer@@QEAAXHW4EDisconnectReason@@@Z
    void (__fastcall *pfnQueueConsoleCommand)(const wchar_t* cmdLine); // Queues ServerChat via CConsole::AddCmdBuffer
};

struct QueueArcadeSlotReservationResult {
    bool     success;
    int32_t  clientNum;
    int32_t  teamId;
    int32_t  accountId;
    bool     removedBotBrain;
    bool     updatedNetStrings;
    uint16_t priorFlags3DE;
    uint16_t postFlags3DE;
    uint8_t  priorPostInit668;
    uint8_t  postInit668;
    uint32_t priorDiscTerm438;
    uint32_t postDiscTerm438;
    void*    detachedBotBrain;
    wchar_t  previousBotName[64];
    char     reason[96];
};

// Disables CArchive::ExamineChecksums unless KONGOR_EXAMINE_ARCHIVE_CHECKSUMS=1/true/yes/on is set.
bool DisableArchiveChecksumsIfAllowed(bool* pExamineChecksums) noexcept;

// Queries or sets active Queue Arcade mode (checks KONGOR_QUEUE_ARCADE env var on first query).
QueueArcadeServerMode GetQueueArcadeMode() noexcept;
bool IsQueueArcadeModeActive() noexcept;
void SetQueueArcadeMode(QueueArcadeServerMode mode) noexcept;

// Runtime kill-switch for Disconnected / active-phase Terminate swap interceptors
// (default true; controlled by KONGOR_ARCADE_SWAP_HOOKS=0 env var or kongor_arcade_swap_hooks <0|1>).
bool IsQueueArcadeSwapHooksEnabled() noexcept;
void SetQueueArcadeSwapHooksEnabled(bool enabled) noexcept;
void RefreshQueueArcadeSwapHooksFromEnv() noexcept;

// Caches observed CHostServer* from GenerateClientID / InitializeConnectionless for GetClient / KickClient.
void NotifyObservedArcadeHostServer(void* pHostServer) noexcept;
bool IsArcadeKickReentrancyActive() noexcept;

// Per-slot (0..31) persistent bot origin cache & CPlayer::AttachBot hook recorder.
void RecordArcadeSlotOrigin(
    int32_t clientNum,
    const void* pBotDef,
    const wchar_t* originalBotName = nullptr
) noexcept;
bool GetArcadeSlotOrigin(
    int32_t clientNum,
    const void** outBotDef,
    wchar_t* outOriginalBotName,
    size_t nameCap
) noexcept;
void OnArcadePlayerAttachBot(
    void* pPlayer,
    const void* pBotDef,
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Active-match Disconnected & Terminate swap interceptors (return true when intercepted/suppressed,
// false when caller should invoke stock K2 g_origDisconnected / g_origTerminate).
bool OnArcadePlayerDisconnected(
    void* pPlayer,
    uint32_t time,
    uint32_t maxDiscTime,
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;
bool OnArcadePlayerTerminate(
    void* pPlayer,
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Reaps connectionless bot slots when 0 active humans (isBot == 0 && accountId > 0) remain outside Queue Arcade.
void ReapConnectionlessBotsIfNoActiveHumans(
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Installs the k2_x64.dll CHostServer::GenerateClientID inline detour if Queue Arcade mode is active.
bool EnsureQueueArcadeK2HooksInstalled() noexcept;

// Returns true only when Safe Mode is inactive and CHostServer::GenerateClientID is actively armed in memory.
bool IsQueueArcadeClientIdHookArmed() noexcept;

// Atomically consumes a pending fail-closed retirement signal when kongor_arcade_mode >= 1 is rejected due to Safe Mode / disarmed hooks.
bool ConsumePendingArcadeSafeModeRetirement() noexcept;

// Reserves a connectionless CPlayer slot for an incoming queued human player.
// Removes any active CBotBrain (and explicitly nulls +0x730), clears 0x0200 connectionless + 0x66B m_bIsBot flags,
// stamps m_iAccountID (+0x2D0) and CNetworkResourceManager strings (+0x2D8 bare name, +0x2DA bare clan tag),
// and records (accountId, clientNum, playerName) so GenerateClientID returns clientNum on connect.
QueueArcadeSlotReservationResult ArcadeReserveSlot(
    int32_t accountId,
    int32_t preferredTeam,
    const wchar_t* playerName,
    const QueueArcadeEngineContext* overrideCtx = nullptr,
    const wchar_t* explicitClanTag = nullptr
) noexcept;

// Releases a human-occupied or previously reserved CPlayer slot back to reusable connectionless state
// without deleting CPlayer or setting PLAYER_FLAG_TERMINATED (0x0080). When kickClient is true, kicks any
// lingering CHostServer CClientConnection under a re-entrancy guard after restoring the bot.
bool ArcadeReleaseSlot(
    int32_t accountId,
    const QueueArcadeEngineContext* overrideCtx = nullptr,
    bool kickClient = false
) noexcept;

// In PartyDormantSlots mode (Mode 2), removes CBotBrain from all unreserved connectionless CPlayer
// entities so custom maps (Strikers, CTF, TDM, 10v10) keep placeholder seats stationary at fountain.
uint32_t ArcadeParkUnreservedBots(
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Resolves a pre-reserved CPlayer::m_iClientNumber (+0xA8) during CHostServer::GenerateClientID
// before packet 'P' is serialized, matching either accountId (> 0) or K2::wstring name at pConnId + 0x08.
int32_t ResolveReservedArcadeClientId(
    const uint16_t* pConnId,
    int32_t accountId
) noexcept;

// Clears all in-memory arcade slot reservations (used on match end / unit test reset),
// safely re-docking and freeing any parked CBotBrain* while IGame::s_pGame is live.
void ClearAllArcadeReservations(
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Returns true when m_pGameInfo->m_eGamePhase (+0xB4) < 5 and CPlayer::m_uiHeroIndex (+0xB0)
// already resolves to a live hero unit via IGame+0x28 (vtable+0x10 -> vtable+0x178), suppressing
// EndHeroLoading's duplicate SpawnHero after Phase 3/4 AddBot while leaving Phase >= 5 repicks untouched.
bool ShouldSuppressPreMatchDuplicateHeroSpawn(
    void* pPlayer,
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Re-docks and frees any Mode 1 parked CBotBrain* via CPlayer::RemoveBot during CPlayer::Terminate
// while IGame::s_pGame and CLuaScriptManager are still alive.
bool ArcadeReleaseParkedBotOnPlayerTerminate(
    void* pPlayer,
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;

// Console command parsers invoked from Hooked_CConsole_Execute on the main server thread:
//   kongor_arcade_mode <0|1|2>
//   kongor_arcade_reserve <accountId> <preferredTeam> <playerName>
//   kongor_arcade_release <accountId> [kick]
//   kongor_arcade_swap_hooks <0|1>
void ExecuteConsoleArcadeModeW(const wchar_t* args) noexcept;
void ExecuteConsoleArcadeReserveW(const wchar_t* args) noexcept;
void ExecuteConsoleArcadeReleaseW(const wchar_t* args) noexcept;
void ExecuteConsoleArcadeSwapHooksW(const wchar_t* args) noexcept;

// Samples live CPlayer + IUnitEntity coordinates on the K2 main thread into KGKF keyframe slots.
void RefreshServerKeyframePlayersFromEngine(
    const QueueArcadeEngineContext* overrideCtx = nullptr
) noexcept;
uint64_t GetLastHeroKeyframeSampleTickMs() noexcept;

// ============================================================================
// 10v10 Mid Wars Kill Packet (0x60) Framing Guard, 128B Expansion & Desync Telemetry
// Dossier: artifacts/evidence/midwars_10v10_kill_packet_truncation.json
// ============================================================================

constexpr uint8_t  kGameCmdKillMessage          = 0x60;
constexpr size_t   kKillMessageHeaderBytes      = 12;
constexpr size_t   kKillMessageExpandedCapacity = 128;
constexpr size_t   kKillMessageMaxAssists128B   = (kKillMessageExpandedCapacity - kKillMessageHeaderBytes) / 4; // 29

// Shared K2 IBuffer memory layout across CBufferFixed<N>, CBufferStatic, CBufferDynamic
// (Win64 MSVC & Linux x86_64 SysV identical).
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

struct SnapshotDesyncTrackerState {
    uint32_t consecutiveMismatchFrames = 0;
    uint32_t totalAlertsEmitted        = 0;
    uint64_t lastAlertTickMs           = 0;
    uint8_t  lastReceivedSeq           = 0;
    uint8_t  lastExpectedSeq           = 0;
};

// Stage 1A write-side guard: if byte[0] == 0x60 && bufferLen >= 12 && bufferLen < 12 + 4 * byte[11],
// clamps byte[11] = (bufferLen - 12) / 4 in place and returns true.
bool ClampKillMessageBufferInPlace(
    uint8_t* buffer,
    size_t bufferLen,
    uint8_t* outOldAssists = nullptr,
    uint8_t* outNewAssists = nullptr
) noexcept;

// Stage 1A client read-side / replay guard: returns true if truncated 0x60 header (< 12 bytes).
bool IsKillMessageHeaderTruncated(
    const uint8_t* unreadData,
    size_t unreadBytes
) noexcept;

// Stage 1A client read-side / replay guard: guards unread CPacket bytes against numAssists over-read.
// Returns true if byte[11] was clamped to fit within unreadBytes.
bool GuardClientKillMessagePacketRead(
    uint8_t* unreadData,
    size_t unreadBytes,
    uint8_t* outOldAssists = nullptr,
    uint8_t* outNewAssists = nullptr
) noexcept;

// Stage 1B helper: extracts unique, ascending-sorted assist ownerClientNumbers from a vector<IUnitEntity*>.
bool CollectUniqueSortedAssistClientNums(
    const uintptr_t* unitPtrBegin,
    const uintptr_t* unitPtrEnd,
    uintptr_t attackerUnit,
    ptrdiff_t ownerClientOffset,
    int32_t* outAssists,
    size_t maxAssists,
    size_t* outCount
) noexcept;

// Stage 1B serializer: builds a full (12 + 4 * N)-byte 0x60 payload into a >= 128-byte buffer (up to 29 assists).
bool BuildExpandedKillMessagePayload(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* sortedAssistClientNums,
    size_t assistCount,
    uint8_t* outBuf,
    size_t outCap,
    size_t* outLen
) noexcept;

// Stage 1B pre-expansion guard: requires declared numAssists in [5, 10], strips any killer/victim
// duplicate (e.g. killer pet/illusion with separate IUnitEntity*), and verifies filteredCount == declared numAssists.
bool FilterAndValidateExpandedKillAssists(
    const uint8_t* origBufHeader,
    size_t origLen,
    const int32_t* candidateAssists,
    size_t candidateCount,
    int32_t* outFilteredAssists,
    size_t outCap,
    size_t* outFilteredCount
) noexcept;

// Layer 2 helper: tracks modular uint8_t state-sequence desync across >= 2 consecutive frames.
bool UpdateSnapshotDesyncTracker(
    SnapshotDesyncTrackerState* state,
    uint8_t receivedSeq,
    uint8_t expectedSeq,
    uint64_t nowTickMs,
    bool* outShouldRateLimitedLog = nullptr
) noexcept;

// Hook installers invoked by vfs_and_hooks.cpp when target DLLs load.
bool EnsureMidwarsServerHooksInstalled() noexcept;
bool EnsureMidwarsClientReplayGuardInstalled(void* hCGameModule) noexcept;
bool EnsureMidwarsSnapshotDesyncHookInstalled(void* hK2ClientModule) noexcept;

} // namespace Kongor
