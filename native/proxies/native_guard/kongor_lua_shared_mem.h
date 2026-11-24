// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file kongor_lua_shared_mem.h
 * @brief Native Lua 5.1 C Closures for Sub-Microsecond Shared Memory Telemetry
 * 
 * Project Kongor - Phase 2 Fast-Bus Synchronization
 */

#pragma once

#include "kongor_shared_mem.h"
#include <string>
#include <cstdint>

namespace Kongor {

/**
 * @brief Initializes and registers the global 'KongorSharedMem' Lua table.
 * 
 * Injects zero-GC stack functions (ReadFast, GetFps, GetPing, GetServerTick, GetCpuGpuMs)
 * and diagnostic/IPC functions (GetTickData, GetVersion, IsConnected, GetPendingEval)
 * into K2's Lua 5.1 runtime.
 * 
 * @param L Active lua_State pointer.
 * @param pShmHandle Pointer to the active shared memory handle.
 * @return true if successfully registered, false on failure.
 */
bool RegisterKongorLuaSharedMem(void* L, KongorSharedMemHandle* pShmHandle);

/**
 * @brief Ensures 'KongorSharedMem' is registered on the given lua_State* L.
 * Re-registers automatically if L changed or InvalidateKongorLuaSharedMem() was called.
 */
bool EnsureKongorLuaSharedMem(void* L, KongorSharedMemHandle* pShmHandle);

/**
 * @brief Marks the Lua shared memory bindings as needing re-registration (e.g. after ReloadInterfaces).
 */
void InvalidateKongorLuaSharedMem();

/**
 * @brief Enqueues a UTF-8 Lua chunk for synchronous 1-frame evaluation on the K2 main thread.
 * @return Unique evaluation ID (> 0) on success, or 0 on failure.
 */
uint64_t EnqueuePendingLuaEval(const std::string& luaCodeUtf8);

/**
 * @brief Completes a pending Lua evaluation from K2's main thread (__kongor_eval_res hook).
 */
void CompletePendingLuaEval(uint64_t evalId, bool success, const std::string& resultUtf8);

/**
 * @brief Blocks the calling Named Pipe worker thread until the K2 main thread completes the evaluation or timeoutMs elapses.
 * @return true if evaluation completed within timeoutMs, false on timeout or missing slot.
 */
bool WaitAndTakePendingLuaEval(uint64_t evalId, DWORD timeoutMs, bool* outSuccess, std::string* outResultUtf8);

// ============================================================================
// 20-PLAYER (>5V5) READ-ONLY CTEAMINFO / CPLAYER SNAPSHOT READER
// ============================================================================

struct LobbyTeamSlotSnapshot {
    bool  valid;            // true when CTeamInfo found and seat < teamSize
    int   slotStatus;       // 0 = empty, 1 = occupied, 2 = locked
    int   clientNum;        // -1 when empty
    int   accountId;
    int   psr;
    bool  isHost;
    bool  isReady;
    bool  isLocked;
    bool  isBot;
    bool  isReferee;
    bool  isMe;
    bool  localIsHost;
    bool  canJoin;
    bool  hasSelectedHero;
    float loadingPct;
    char  name[64];
    char  heroDefName[64];
};

constexpr int kKongor20PlayerBridgeVersion = 2;

struct ScoreboardTeamSlotSnapshot {
    bool  exists;           // true when occupied by a CPlayer
    int   clientNum;
    int   teamId;
    int   seat;
    int   level;
    int   kills;
    int   deaths;
    int   assists;
    int   gold;
    int   buybacks;
    int   pingOrScore;
    int   respawnSec;
    int   teamScore;        // CTeamInfo + 0x16c
    float loadingPct;
    bool  isDead;
    bool  isDisconnected;
    bool  isTerminated;
    bool  isBot;
    bool  isMe;
    bool  sameTeam;         // true strictly when local player is on the same active team (1 or 2)
    bool  canSeeItems;
    bool  isTalking;        // CVoiceManager + 0x41e0 std::set<int>
    bool  isVoiceMuted;     // CVoiceManager + 0x41f0 std::set<int>
    // Ally-only tactical HUD fields (populated strictly when sameTeam == true):
    int   heroEntityIndex;  // -1 when unavailable or !sameTeam
    float healthPct;        // 0.0..1.0
    float manaPct;          // 0.0..1.0
    bool  hasMana;          // maxMana > 0
    bool  isAFK;            // (CPlayer+0x3de & 0x8000) != 0
    int   dcTimeMs;         // remaining disconnect grace timer in ms
    int   shareFromAlly;    // 0=none, 1=courier, 2/3=full
    int   shareToAlly;      // 0=none, 1=courier, 2/3=full
    bool  noHelp;           // CPlayer::GetNoHelp(localPlayer, allyPlayer)
    int   abilityDot[4];    // 0=silver, 1=red, 2=yellow, 3=green, 4=blue, 5=lime, 6=orange
    char  name[64];
    char  heroName[64];
    char  heroIcon[128];
    char  items[8][128];
    float stats[6];         // CPlayer + 0x4b8 gameplaystat0..5
};

struct Engine20PlayerContext {
    void** ppGame;          // IGame** (?s_pGame@IGame@@0PEAV1@EA)
    void** ppEntityReg;     // CEntityRegistry** (?g_EntityRegistry@@3AEAVCEntityRegistry@@EA)
    void** ppNetResMgr;     // CNetworkResourceManager** (?g_NetworkResourceManager@@3AEAVCNetworkResourceManager@@EA)
    void** ppVoiceMgr;      // CVoiceManager** (?VoiceManager@@3AEAVCVoiceManager@@EA)
    int   (__fastcall *pfnGetClientIDFromTeamIndex)(const void* pTeamInfo, uint32_t seat);
    bool  (__fastcall *pfnCanJoinTeam)(const void* pTeamInfo, int clientNum, uint32_t seat);
    bool  (__fastcall *pfnIsBot)(const void* pPlayer);
    bool  (__fastcall *pfnIsReferee)(const void* pPlayer);
    bool  (__fastcall *pfnCanSee)(const void* pLocalPlayer, const void* pVisualEntity);
    float (__fastcall *pfnGetGameplayStat)(const void* pPlayer, int idx);
    bool  (__fastcall *pfnHasSharedPartialControl)(const void* pPlayer, int clientNum);
    bool  (__fastcall *pfnHasSharedFullControl)(const void* pPlayer, int clientNum);
    bool  (__fastcall *pfnGetNoHelp)(const void* pPlayer, void* pOtherPlayer);
    uint32_t (__fastcall *pfnGetRemainingCooldownTime)(const void* pEntityTool, bool bIgnoreGlobal);
    const void* (__fastcall *pfnGetNetString)(void* pNetResMgr, uint16_t idx);
    const void* (__fastcall *pfnLookupEntityName)(void* pEntityReg, uint16_t typeId);
};

int  Get20PlayerBridgeVersionNative();
bool Is20PlayerBridgeEnabledNative();
int  ReadTeamSlotCountNative(const Engine20PlayerContext* ctx, int teamId);
bool ReadLobbyTeamSlotNative(const Engine20PlayerContext* ctx, int teamId, int seat, LobbyTeamSlotSnapshot* outSlot);
bool ReadScoreboardTeamSlotNative(const Engine20PlayerContext* ctx, int teamId, int seat, ScoreboardTeamSlotSnapshot* outSlot);

} // namespace Kongor

