// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file kongor_lua_shared_mem.cpp
 * @brief Implementation of Native Lua 5.1 C Closures for Shared-Memory Fast Bus
 */

#include "native_guard_internal.h"
#include "kongor_lua_shared_mem.h"
#include "kongor_sstream.h"
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <cmath>
#include <cstring>
#include <atomic>
#include <vector>

namespace Kongor {

// ============================================================================
// LUA 5.1 C API TYPEDEFS & INLINE STACK PUSHERS (K2_X64 ABI)
// ============================================================================

typedef int (*lua_CFunction) (void *L);
typedef void (*pfn_lua_pushcclosure) (void *L, lua_CFunction fn, int n);
typedef void (*pfn_lua_createtable) (void *L, int narr, int nrec);
typedef void (*pfn_lua_setfield) (void *L, int idx, const char *k);
typedef void (*pfn_lua_pushlstring) (void *L, const char *s, size_t len);
typedef void (*pfn_lua_pushstring) (void *L, const char *s);

#define LUA_GLOBALSINDEX (-10002)

#pragma pack(push, 8)
struct LuaTValue {
    union {
        void* p;
        double n;
        int b;
    } value;
    int tt;
    int pad;
};
#pragma pack(pop)
static_assert(sizeof(LuaTValue) == 16, "Lua 5.1 TValue on x64 must be 16 bytes");

// In k2_x64.dll (/O2 MSVC), lua_pushnumber / lua_pushinteger / lua_pushboolean are inlined
// directly as 16-byte TValue writes at L->top (L + 0x10).
static inline void LuaPushNumberInline(void* L, double n) {
    uint8_t** ppTop = reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(L) + 0x10);
    LuaTValue* top = reinterpret_cast<LuaTValue*>(*ppTop);
    top->value.n = n;
    top->tt = 3; // LUA_TNUMBER
    *ppTop += sizeof(LuaTValue);
}

static inline void LuaPushIntegerInline(void* L, ptrdiff_t n) {
    LuaPushNumberInline(L, static_cast<double>(n));
}

static inline void LuaPushBooleanInline(void* L, int b) {
    uint8_t** ppTop = reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(L) + 0x10);
    LuaTValue* top = reinterpret_cast<LuaTValue*>(*ppTop);
    top->value.b = (b != 0) ? 1 : 0;
    top->tt = 1; // LUA_TBOOLEAN
    *ppTop += sizeof(LuaTValue);
}

// Verified in k2_x64.dll via Ghidra:
// - index2adr (0x180CA3F00): L+0x10 = L->top, L+0x18 = L->base; arg idx (1-based) = base + (idx - 1)
// - luaS_newlstr (0x180CBD110): TString* at TValue.value.p (tt == 4) stores size_t len at +0x10 and char[] at +0x18
static inline const LuaTValue* LuaGetStackArgInline(void* L, int idx1Based) {
    if (!L || idx1Based < 1) return nullptr;
    uint8_t* pTop = *reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(L) + 0x10);
    uint8_t* pBase = *reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(L) + 0x18);
    if (!pTop || !pBase || pTop <= pBase) return nullptr;
    uint8_t* pSlot = pBase + static_cast<size_t>(idx1Based - 1) * sizeof(LuaTValue);
    if (pSlot >= pTop) return nullptr;
    return reinterpret_cast<const LuaTValue*>(pSlot);
}

static inline bool LuaReadStringArgInline(const LuaTValue* tv, const char** outData, size_t* outLen) {
    if (!tv || tv->tt != 4 /* LUA_TSTRING */ || !tv->value.p) return false;
    const uint8_t* pTStr = reinterpret_cast<const uint8_t*>(tv->value.p);
    if (outLen) *outLen = *reinterpret_cast<const size_t*>(pTStr + 0x10);
    if (outData) *outData = reinterpret_cast<const char*>(pTStr + 0x18);
    return true;
}

static pfn_lua_pushcclosure g_lua_pushcclosure = nullptr;
static pfn_lua_createtable  g_lua_createtable  = nullptr;
static pfn_lua_setfield     g_lua_setfield     = nullptr;
static pfn_lua_pushlstring  g_lua_pushlstring  = nullptr;
static pfn_lua_pushstring   g_lua_pushstring   = nullptr;

static KongorSharedMemHandle* g_pShmHandle = nullptr;
static std::atomic<bool>      g_luaBindingsRegistered{false};
static std::atomic<void*>     g_lastRegisteredLuaState{nullptr};

// ============================================================================
// SYNCHRONOUS 1-FRAME LUA EVALUATION SLOTS
// ============================================================================

struct PendingLuaEvalSlot {
    uint64_t evalId;
    std::string codeUtf8;
    std::string resultUtf8;
    bool dispatched;
    bool completed;
    bool success;
    HANDLE hEvent;
    ULONGLONG createdTickMs;
};

static SRWLOCK g_evalLock = SRWLOCK_INIT;
static std::vector<PendingLuaEvalSlot> g_pendingEvals;
static std::atomic<uint64_t> g_nextEvalId{0};

uint64_t EnqueuePendingLuaEval(const std::string& luaCodeUtf8) {
    HANDLE hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!hEvent) {
        return 0;
    }

    uint64_t id = ++g_nextEvalId;
    ULONGLONG nowTick = GetTickCount64();
    GetGlobalBinaryTelemetryRing().Record(0x4C554101U, "lua_eval", id, luaCodeUtf8.size(), luaCodeUtf8.c_str());

    AcquireSRWLockExclusive(&g_evalLock);
    // Prune any orphaned slots older than 30 seconds
    for (auto it = g_pendingEvals.begin(); it != g_pendingEvals.end(); ) {
        if (nowTick - it->createdTickMs > 30000ULL) {
            if (it->hEvent) {
                CloseHandle(it->hEvent);
                it->hEvent = NULL;
            }
            it = g_pendingEvals.erase(it);
        } else {
            ++it;
        }
    }

    PendingLuaEvalSlot slot;
    slot.evalId = id;
    slot.codeUtf8 = luaCodeUtf8;
    slot.dispatched = false;
    slot.completed = false;
    slot.success = false;
    slot.hEvent = hEvent;
    slot.createdTickMs = nowTick;
    g_pendingEvals.push_back(std::move(slot));
    ReleaseSRWLockExclusive(&g_evalLock);

    return id;
}

void CompletePendingLuaEval(uint64_t evalId, bool success, const std::string& resultUtf8) {
    AcquireSRWLockExclusive(&g_evalLock);
    for (auto& slot : g_pendingEvals) {
        if (slot.evalId == evalId) {
            slot.success = success;
            slot.resultUtf8 = resultUtf8;
            slot.completed = true;
            if (slot.hEvent) {
                SetEvent(slot.hEvent);
            }
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_evalLock);
}

bool WaitAndTakePendingLuaEval(uint64_t evalId, DWORD timeoutMs, bool* outSuccess, std::string* outResultUtf8) {
    HANDLE hEvent = NULL;
    AcquireSRWLockExclusive(&g_evalLock);
    for (const auto& slot : g_pendingEvals) {
        if (slot.evalId == evalId) {
            hEvent = slot.hEvent;
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_evalLock);

    if (!hEvent) {
        if (outSuccess) *outSuccess = false;
        if (outResultUtf8) *outResultUtf8 = "Internal error: evaluation slot not found";
        return false;
    }

    WaitForSingleObject(hEvent, timeoutMs);

    bool completed = false;
    AcquireSRWLockExclusive(&g_evalLock);
    for (auto it = g_pendingEvals.begin(); it != g_pendingEvals.end(); ++it) {
        if (it->evalId == evalId) {
            if (it->hEvent) {
                CloseHandle(it->hEvent);
                it->hEvent = NULL;
            }
            completed = it->completed;
            if (completed) {
                if (outSuccess) *outSuccess = it->success;
                if (outResultUtf8) *outResultUtf8 = std::move(it->resultUtf8);
            } else {
                if (outSuccess) *outSuccess = false;
                if (outResultUtf8) *outResultUtf8 = "Timed out waiting for K2 main thread Lua evaluation";
            }
            g_pendingEvals.erase(it);
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_evalLock);

    return completed;
}

// ============================================================================
// PRIMARY RVAS & PATTERN SCANNING FALLBACK
// ============================================================================

/* Verified RVAs from ProjectKongor.rep Ghidra decompiler database */
static constexpr uintptr_t RVA_LUA_PUSHCCLOSURE = 0xCA4C60;
static constexpr uintptr_t RVA_LUA_SETFIELD     = 0xCA4D30;
static constexpr uintptr_t RVA_LUA_CREATETABLE  = 0xCA4BF0;
static constexpr uintptr_t RVA_LUA_PUSHLSTRING  = 0xCA44B0;
static constexpr uintptr_t RVA_LUA_PUSHSTRING   = 0xCA4520;

static void* PatternScan(HMODULE hMod, const char* signature, const char* mask) {
    if (!hMod) return nullptr;
    MODULEINFO modInfo = {};
    GetModuleInformation(GetCurrentProcess(), hMod, &modInfo, sizeof(modInfo));

    uint8_t* base = reinterpret_cast<uint8_t*>(modInfo.lpBaseOfDll);
    size_t size = modInfo.SizeOfImage;
    size_t sigLen = strlen(mask);

    for (size_t i = 0; i < size - sigLen; ++i) {
        bool found = true;
        for (size_t j = 0; j < sigLen; ++j) {
            if (mask[j] != '?' && static_cast<uint8_t>(signature[j]) != base[i + j]) {
                found = false;
                break;
            }
        }
        if (found) return &base[i];
    }
    return nullptr;
}

static bool ResolveLuaSymbols() {
    if (g_lua_pushcclosure && g_lua_setfield && g_lua_createtable && g_lua_pushlstring) {
        return true;
    }

    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) {
        OutputDebugStringA("[KONGOR LUA] Failed to locate k2_x64.dll in process space.\n");
        return false;
    }

    uint8_t* pBase = reinterpret_cast<uint8_t*>(hK2);

    // Fast-path: Verify known RVAs
    g_lua_pushcclosure = reinterpret_cast<pfn_lua_pushcclosure>(pBase + RVA_LUA_PUSHCCLOSURE);
    g_lua_setfield     = reinterpret_cast<pfn_lua_setfield>(pBase + RVA_LUA_SETFIELD);
    g_lua_createtable  = reinterpret_cast<pfn_lua_createtable>(pBase + RVA_LUA_CREATETABLE);
    g_lua_pushlstring  = reinterpret_cast<pfn_lua_pushlstring>(pBase + RVA_LUA_PUSHLSTRING);
    g_lua_pushstring   = reinterpret_cast<pfn_lua_pushstring>(pBase + RVA_LUA_PUSHSTRING);

    // Sanity check: ensure valid code addresses
    if (!g_lua_pushcclosure || !g_lua_setfield || !g_lua_createtable) {
        OutputDebugStringA("[KONGOR LUA] Primary Lua RVAs invalid, executing signature scan fallback...\n");
        // Fallback pattern scan for lua_pushcclosure
        void* pPushC = PatternScan(hK2, "\x48\x83\xEC\x28\x8B\xD2\x48\x8B\xC8", "xxxxxxxxx");
        if (pPushC) {
            g_lua_pushcclosure = reinterpret_cast<pfn_lua_pushcclosure>(pPushC);
            OutputDebugStringA("[KONGOR LUA] Successfully recovered lua_pushcclosure via pattern scan!\n");
        } else {
            OutputDebugStringA("[KONGOR LUA] Pattern scan fallback failed to locate Lua C API.\n");
            return false;
        }
    }

    OutputDebugStringA("[KONGOR LUA] Successfully resolved all K2 Lua 5.1 C API function pointers.\n");
    return true;
}

// ============================================================================
// NATIVE LUA C-CLOSURES (SUB-MICROSECOND, ZERO-GC ALLOCATION)
// ============================================================================

/**
 * @brief KongorSharedMem.ReadFast()
 * Returns (frameIndex, cpuTimeMs, gpuTimeMs, pingMs, serverTick) directly on the Lua stack.
 * Performs ZERO Lua GC allocations. Executes in <= 250 nanoseconds!
 */
static int Lua_Kongor_ReadFast(void* L) {
    FrameTelemetryRecord rec = {};
    if (!g_pShmHandle || !kongor_shm_read_latest(g_pShmHandle, &rec)) {
        return 0; // nil return
    }

    LuaPushIntegerInline(L, (ptrdiff_t)rec.FrameIndex);
    LuaPushNumberInline(L, (double)rec.CpuFrameTimeMs);
    LuaPushNumberInline(L, (double)rec.GpuFrameTimeMs);
    LuaPushNumberInline(L, (double)rec.PingMicroseconds / 1000.0);
    LuaPushIntegerInline(L, (ptrdiff_t)rec.ServerTick);
    return 5;
}

/**
 * @brief KongorSharedMem.GetFps()
 * Returns instantaneous engine FPS as a float (0 GC allocations).
 */
static int Lua_Kongor_GetFps(void* L) {
    FrameTelemetryRecord rec = {};
    if (g_pShmHandle && kongor_shm_read_latest(g_pShmHandle, &rec) && rec.CpuFrameTimeMs > 0.001f) {
        LuaPushNumberInline(L, 1000.0 / (double)rec.CpuFrameTimeMs);
    } else {
        LuaPushNumberInline(L, 0.0);
    }
    return 1;
}

/**
 * @brief KongorSharedMem.GetPing()
 * Returns network ping in milliseconds as a float (0 GC allocations).
 */
static int Lua_Kongor_GetPing(void* L) {
    FrameTelemetryRecord rec = {};
    if (g_pShmHandle && kongor_shm_read_latest(g_pShmHandle, &rec)) {
        LuaPushNumberInline(L, (double)rec.PingMicroseconds / 1000.0);
    } else {
        LuaPushNumberInline(L, 0.0);
    }
    return 1;
}

/**
 * @brief KongorSharedMem.GetServerTick()
 * Returns current server simulation tick as an integer (0 GC allocations).
 */
static int Lua_Kongor_GetServerTick(void* L) {
    FrameTelemetryRecord rec = {};
    if (g_pShmHandle && kongor_shm_read_latest(g_pShmHandle, &rec)) {
        LuaPushIntegerInline(L, (ptrdiff_t)rec.ServerTick);
    } else {
        LuaPushIntegerInline(L, 0);
    }
    return 1;
}

/**
 * @brief KongorSharedMem.GetCpuGpuMs()
 * Returns (cpuMs, gpuMs) floats on the Lua stack (0 GC allocations).
 */
static int Lua_Kongor_GetCpuGpuMs(void* L) {
    FrameTelemetryRecord rec = {};
    if (g_pShmHandle && kongor_shm_read_latest(g_pShmHandle, &rec)) {
        LuaPushNumberInline(L, (double)rec.CpuFrameTimeMs);
        LuaPushNumberInline(L, (double)rec.GpuFrameTimeMs);
    } else {
        LuaPushNumberInline(L, 0.0);
        LuaPushNumberInline(L, 0.0);
    }
    return 2;
}

/**
 * @brief KongorSharedMem.GetTickData()
 * Diagnostic full-table query returning all 13 telemetry fields.
 * Includes a microsecond frame guard warning if execution exceeds 100 us.
 */
static int Lua_Kongor_GetTickData(void* L) {
    LARGE_INTEGER t0, t1, freq;
    QueryPerformanceCounter(&t0);

    FrameTelemetryRecord rec = {};
    bool ok = g_pShmHandle && kongor_shm_read_latest(g_pShmHandle, &rec);

    g_lua_createtable(L, 0, 14);
    if (ok) {
        LuaPushIntegerInline(L, (ptrdiff_t)rec.FrameIndex);
        g_lua_setfield(L, -2, "frame_index");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.TimestampQpc);
        g_lua_setfield(L, -2, "timestamp_qpc");

        LuaPushNumberInline(L, (double)rec.CpuFrameTimeMs);
        g_lua_setfield(L, -2, "cpu_ms");

        LuaPushNumberInline(L, (double)rec.GpuFrameTimeMs);
        g_lua_setfield(L, -2, "gpu_ms");

        LuaPushNumberInline(L, (double)rec.InputLatencyMs);
        g_lua_setfield(L, -2, "input_latency_ms");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.DrawCallCount);
        g_lua_setfield(L, -2, "draw_calls");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.ActiveEntities);
        g_lua_setfield(L, -2, "entities");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.ParticlesAlive);
        g_lua_setfield(L, -2, "particles");

        LuaPushNumberInline(L, (double)rec.PingMicroseconds / 1000.0);
        g_lua_setfield(L, -2, "ping_ms");

        LuaPushNumberInline(L, (double)rec.PacketLossPercent);
        g_lua_setfield(L, -2, "loss_pct");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.MemoryWorkingSetMb);
        g_lua_setfield(L, -2, "working_set_mb");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.AudioVoicesActive);
        g_lua_setfield(L, -2, "audio_voices");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.ServerTick);
        g_lua_setfield(L, -2, "server_tick");

        LuaPushIntegerInline(L, (ptrdiff_t)rec.ReservedFlags);
        g_lua_setfield(L, -2, "ptt_flags");
    }

    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&freq);
    double elapsedUs = (double)(t1.QuadPart - t0.QuadPart) * 1000000.0 / (double)freq.QuadPart;
    if (elapsedUs > 100.0) {
        char warnBuf[160];
        snprintf(warnBuf, sizeof(warnBuf),
                 "[KONGOR LUA WARN] GetTickData() frame hitch: %.2f us (> 100 us budget!)\n", elapsedUs);
        OutputDebugStringA(warnBuf);
    }

    return 1;
}

/**
 * @brief KongorSharedMem.GetVersion()
 * Returns telemetry protocol version (2).
 */
static int Lua_Kongor_GetVersion(void* L) {
    LuaPushIntegerInline(L, KONGOR_SHM_CURRENT_VERSION);
    return 1;
}

/**
 * @brief KongorSharedMem.IsConnected()
 * Returns boolean indicating whether the shared memory bus is active.
 */
static int Lua_Kongor_IsConnected(void* L) {
    LuaPushBooleanInline(L, (g_pShmHandle != nullptr && g_pShmHandle->pHeader != nullptr) ? 1 : 0);
    return 1;
}

/**
 * @brief KongorSharedMem.GetPendingEval()
 * Pops the next pending synchronous Lua evaluation request from the queue and returns (codeString, evalId)
 * directly on the Lua stack without any console escaping or truncation.
 */
static int Lua_Kongor_GetPendingEval(void* L) {
    if (!L || !g_lua_pushlstring) {
        return 0;
    }

    std::string code;
    uint64_t evalId = 0;
    bool found = false;

    AcquireSRWLockExclusive(&g_evalLock);
    for (auto& slot : g_pendingEvals) {
        if (!slot.dispatched && !slot.completed) {
            slot.dispatched = true;
            code = slot.codeUtf8;
            evalId = slot.evalId;
            found = true;
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_evalLock);

    if (!found) {
        return 0;
    }

    g_lua_pushlstring(L, code.c_str(), code.size());
    LuaPushIntegerInline(L, (ptrdiff_t)evalId);
    return 2;
}

/**
 * @brief KongorSharedMem.SetEvalResult(evalId, ok, resultString)
 * Completes a synchronous Lua evaluation directly from the Lua stack into C++ memory,
 * completely bypassing K2 CConsole's 2048-byte command buffer limit and Console string splitting.
 */
static int Lua_Kongor_SetEvalResult(void* L) {
    if (!L) return 0;

    const LuaTValue* argId = LuaGetStackArgInline(L, 1);
    const LuaTValue* argOk = LuaGetStackArgInline(L, 2);
    const LuaTValue* argOut = LuaGetStackArgInline(L, 3);
    if (!argId) {
        LuaPushBooleanInline(L, 0);
        return 1;
    }

    uint64_t evalId = 0;
    if (argId->tt == 3 /* LUA_TNUMBER */ && argId->value.n > 0.0) {
        evalId = static_cast<uint64_t>(argId->value.n);
    } else {
        const char* idChars = nullptr;
        size_t idLen = 0;
        if (LuaReadStringArgInline(argId, &idChars, &idLen) && idChars && idLen > 0) {
            evalId = std::strtoull(idChars, nullptr, 10);
        }
    }
    if (evalId == 0) {
        LuaPushBooleanInline(L, 0);
        return 1;
    }

    bool ok = false;
    if (argOk) {
        if (argOk->tt == 1 /* LUA_TBOOLEAN */) {
            ok = (argOk->value.b != 0);
        } else if (argOk->tt == 3 /* LUA_TNUMBER */) {
            ok = (argOk->value.n != 0.0);
        } else if (argOk->tt != 0 /* LUA_TNIL */) {
            ok = true;
        }
    }

    std::string outStr = "nil";
    if (argOut) {
        const char* sData = nullptr;
        size_t sLen = 0;
        if (LuaReadStringArgInline(argOut, &sData, &sLen) && sData) {
            outStr.assign(sData, sLen);
        } else if (argOut->tt == 3 /* LUA_TNUMBER */) {
            char numBuf[64] = {};
            snprintf(numBuf, sizeof(numBuf), "%.14g", argOut->value.n);
            outStr = numBuf;
        } else if (argOut->tt == 1 /* LUA_TBOOLEAN */) {
            outStr = (argOut->value.b != 0) ? "true" : "false";
        }
    }

    CompletePendingLuaEval(evalId, ok, outStr);
    LuaPushBooleanInline(L, 1);
    return 1;
}

/**
 * @brief KongorSharedMem.GetGamePhase()
 * Returns the live native GamePhase integer directly from the shared memory header (MCP-246).
 */
static int Lua_Kongor_GetGamePhase(void* L) {
    uint32_t phase = 0;
    if (g_pShmHandle && g_pShmHandle->pHeader) {
        phase = g_pShmHandle->pHeader->GamePhase;
    }
    LuaPushIntegerInline(L, static_cast<ptrdiff_t>(phase));
    return 1;
}

// ============================================================================
// 20-PLAYER (>5V5) READ-ONLY CTEAMINFO / CPLAYER SNAPSHOT READER
// ============================================================================

static Engine20PlayerContext g_20pCtx = {};
static std::atomic<bool> g_20pCtxResolved{false};

bool Is20PlayerBridgeEnabledNative() {
    // satisfies: OBL-NG-20P-05
    char envBuf[16] = {};
    DWORD n = GetEnvironmentVariableA("KONGOR_DISABLE_20P_BRIDGE", envBuf, sizeof(envBuf));
    if (n > 0 && (envBuf[0] == '1' || envBuf[0] == 't' || envBuf[0] == 'T')) {
        return false;
    }
    return true;
}

int Get20PlayerBridgeVersionNative() {
    return kKongor20PlayerBridgeVersion;
}

static const Engine20PlayerContext* EnsureEngine20PlayerContext() {
    if (g_20pCtxResolved.load(std::memory_order_acquire)) {
        return &g_20pCtx;
    }

    HMODULE hGameShared = GetModuleHandleA("game_shared_x64.dll");
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hGameShared || !hK2) {
        return nullptr;
    }

    Engine20PlayerContext resolved = {};
    resolved.ppGame = reinterpret_cast<void**>(
        GetProcAddress(hGameShared, "?s_pGame@IGame@@0PEAV1@EA"));
    resolved.ppEntityReg = reinterpret_cast<void**>(
        GetProcAddress(hGameShared, "?g_EntityRegistry@@3AEAVCEntityRegistry@@EA"));
    resolved.ppNetResMgr = reinterpret_cast<void**>(
        GetProcAddress(hK2, "?g_NetworkResourceManager@@3AEAVCNetworkResourceManager@@EA"));
    resolved.ppVoiceMgr = reinterpret_cast<void**>(
        GetProcAddress(hK2, "?VoiceManager@@3AEAVCVoiceManager@@EA"));

    resolved.pfnGetClientIDFromTeamIndex = reinterpret_cast<decltype(resolved.pfnGetClientIDFromTeamIndex)>(
        GetProcAddress(hGameShared, "?GetClientIDFromTeamIndex@CTeamInfo@@QEBAHI@Z"));
    resolved.pfnCanJoinTeam = reinterpret_cast<decltype(resolved.pfnCanJoinTeam)>(
        GetProcAddress(hGameShared, "?CanJoinTeam@CTeamInfo@@QEBA_NHI@Z"));
    resolved.pfnIsBot = reinterpret_cast<decltype(resolved.pfnIsBot)>(
        GetProcAddress(hGameShared, "?IsBot@CPlayer@@QEBA_NXZ"));
    resolved.pfnIsReferee = reinterpret_cast<decltype(resolved.pfnIsReferee)>(
        GetProcAddress(hGameShared, "?IsReferee@CPlayer@@QEBA_NXZ"));
    resolved.pfnCanSee = reinterpret_cast<decltype(resolved.pfnCanSee)>(
        GetProcAddress(hGameShared, "?CanSee@CPlayer@@QEBA_NPEBVIVisualEntity@@@Z"));
    resolved.pfnGetGameplayStat = reinterpret_cast<decltype(resolved.pfnGetGameplayStat)>(
        GetProcAddress(hGameShared, "?GetGameplayStat@CPlayer@@QEBAMH@Z"));
    resolved.pfnHasSharedPartialControl = reinterpret_cast<decltype(resolved.pfnHasSharedPartialControl)>(
        GetProcAddress(hGameShared, "?HasSharedPartialControl@CPlayer@@QEBA_NH@Z"));
    resolved.pfnHasSharedFullControl = reinterpret_cast<decltype(resolved.pfnHasSharedFullControl)>(
        GetProcAddress(hGameShared, "?HasSharedFullControl@CPlayer@@QEBA_NH@Z"));
    resolved.pfnGetNoHelp = reinterpret_cast<decltype(resolved.pfnGetNoHelp)>(
        GetProcAddress(hGameShared, "?GetNoHelp@CPlayer@@QEBA_NPEAV1@@Z"));
    resolved.pfnGetRemainingCooldownTime = reinterpret_cast<decltype(resolved.pfnGetRemainingCooldownTime)>(
        GetProcAddress(hGameShared, "?GetRemainingCooldownTime@IEntityTool@@QEBAI_N@Z"));
    resolved.pfnGetNetString = reinterpret_cast<decltype(resolved.pfnGetNetString)>(
        GetProcAddress(hK2, "?GetString@CNetworkResourceManager@@QEAAAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@G@Z"));
    resolved.pfnLookupEntityName = reinterpret_cast<decltype(resolved.pfnLookupEntityName)>(
        GetProcAddress(hGameShared, "?LookupName@CEntityRegistry@@QEAAAEBV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@G@Z"));

    if (!resolved.ppGame || !resolved.pfnGetClientIDFromTeamIndex) {
        OutputDebugStringA("[KONGOR 20P] Required game_shared_x64.dll exports unresolved; 20p bridge deferred.\n");
        return nullptr;
    }

    g_20pCtx = resolved;
    g_20pCtxResolved.store(true, std::memory_order_release);
    OutputDebugStringA("[KONGOR 20P] Resolved game_shared_x64.dll + k2_x64.dll exports for 20-player slot reader.\n");
    return &g_20pCtx;
}

static void CopyK2WStringToUtf8(const void* pK2WStr, char* outBuf, size_t outCap) {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = '\0';
    if (!pK2WStr) return;

    const uint8_t* raw = reinterpret_cast<const uint8_t*>(pK2WStr);
    size_t wlen = *reinterpret_cast<const size_t*>(raw + 0x10);
    size_t wcap = *reinterpret_cast<const size_t*>(raw + 0x18);
    if (wlen == 0) return;
    if (wlen > 512) wlen = 512;

    const wchar_t* wptr = (wcap > 7)
        ? *reinterpret_cast<const wchar_t* const*>(raw)
        : reinterpret_cast<const wchar_t*>(raw);
    if (!wptr) return;

    int written = WideCharToMultiByte(
        CP_UTF8, 0, wptr, static_cast<int>(wlen),
        outBuf, static_cast<int>(outCap - 1), NULL, NULL);
    if (written > 0 && static_cast<size_t>(written) < outCap) {
        outBuf[written] = '\0';
    } else {
        outBuf[0] = '\0';
    }
}

static const uint8_t* LookupUintMapValue(const uint8_t* mapHead, uint32_t targetKey) {
    if (!mapHead) return nullptr;
    const uint8_t* candidate = mapHead;
    const uint8_t* node = *reinterpret_cast<const uint8_t* const*>(mapHead + 0x08);
    for (int depth = 0; depth < 64 && node && node != mapHead && *(node + 0x19) == 0; ++depth) {
        uint32_t nodeKey = *reinterpret_cast<const uint32_t*>(node + 0x20);
        if (nodeKey < targetKey) {
            node = *reinterpret_cast<const uint8_t* const*>(node + 0x10);
        } else {
            candidate = node;
            node = *reinterpret_cast<const uint8_t* const*>(node + 0x00);
        }
    }
    if (!candidate || candidate == mapHead || *(candidate + 0x19) != 0) {
        return nullptr;
    }
    if (*reinterpret_cast<const uint32_t*>(candidate + 0x20) != targetKey) {
        return nullptr;
    }
    return *reinterpret_cast<const uint8_t* const*>(candidate + 0x28);
}

static const uint8_t* LookupIntMapValue(const uint8_t* mapHead, int32_t targetKey) {
    if (!mapHead) return nullptr;
    const uint8_t* candidate = mapHead;
    const uint8_t* node = *reinterpret_cast<const uint8_t* const*>(mapHead + 0x08);
    for (int depth = 0; depth < 64 && node && node != mapHead && *(node + 0x19) == 0; ++depth) {
        int32_t nodeKey = *reinterpret_cast<const int32_t*>(node + 0x20);
        if (nodeKey < targetKey) {
            node = *reinterpret_cast<const uint8_t* const*>(node + 0x10);
        } else {
            candidate = node;
            node = *reinterpret_cast<const uint8_t* const*>(node + 0x00);
        }
    }
    if (!candidate || candidate == mapHead || *(candidate + 0x19) != 0) {
        return nullptr;
    }
    if (*reinterpret_cast<const int32_t*>(candidate + 0x20) != targetKey) {
        return nullptr;
    }
    return *reinterpret_cast<const uint8_t* const*>(candidate + 0x28);
}

template <typename FnSig>
static FnSig ReadVtableSlot(const void* pObj, size_t byteOffset) {
    if (!pObj) return nullptr;
    const uint8_t* vtable = *reinterpret_cast<const uint8_t* const*>(pObj);
    if (!vtable) return nullptr;
    return *reinterpret_cast<const FnSig*>(vtable + byteOffset);
}

// Resolves local CPlayer* via IGame virtual slot +0x28 (CGameClient::GetLocalPlayer, cgame_x64.dll+0x1cba70),
// with fallback to CGameClient::m_pLocalPlayer (+0x238, cgame_x64.dll+0x153f60) when vtable is unpopulated in tests.
static const uint8_t* GetClientLocalPlayer(const uint8_t* pGame) {
    if (!pGame) return nullptr;
    using GetLocalPlayerFn = const uint8_t* (__fastcall*)(const void*);
    GetLocalPlayerFn pfnGetLocal = ReadVtableSlot<GetLocalPlayerFn>(pGame, 0x28);
    if (pfnGetLocal) {
        const uint8_t* pViaVtable = pfnGetLocal(pGame);
        if (pViaVtable) return pViaVtable;
    }
    return *reinterpret_cast<const uint8_t* const*>(pGame + 0x238);
}

// Evaluates MSVC x64 std::set<int> red-black tree membership (CVoiceManager +0x41e0 talking, +0x41f0 muted,
// verified against cgame_x64.dll+0x056fd0 / +0x078580 and k2_x64.dll+0xa9e150).
// satisfies: OBL-NG-20P-07
static bool LookupIntSetContains(const uint8_t* setHead, int32_t targetKey) {
    if (!setHead) return false;
    const uint8_t* candidate = setHead;
    const uint8_t* node = *reinterpret_cast<const uint8_t* const*>(setHead + 0x08);
    for (int depth = 0; depth < 64 && node && node != setHead && *(node + 0x19) == 0; ++depth) {
        int32_t nodeKey = *reinterpret_cast<const int32_t*>(node + 0x1c);
        if (nodeKey < targetKey) {
            node = *reinterpret_cast<const uint8_t* const*>(node + 0x10);
        } else {
            candidate = node;
            node = *reinterpret_cast<const uint8_t* const*>(node + 0x00);
        }
    }
    if (!candidate || candidate == setHead || *(candidate + 0x19) != 0) {
        return false;
    }
    return (*reinterpret_cast<const int32_t*>(candidate + 0x1c) == targetKey);
}

// Evaluates ally ability status dot enum (0=silver, 1=red, 2=yellow, 3=green, 4=blue, 5=lime, 6=orange)
// matching cgame_x64.dll+0x150a50 (UpdateAllyTool) and game_new.lua:AllyAbilityInfo.
static int EvaluateAllyAbilityDot(
    const Engine20PlayerContext* ctx,
    const uint8_t* pHeroBytes,
    float heroMana,
    bool isHeroAlive,
    int slotIdx)
{
    if (!pHeroBytes || slotIdx < 0 || slotIdx > 3) return 0;

    using GetAsToolFn = const void* (__fastcall*)(const void*);
    using GetBoolFn   = bool (__fastcall*)(const void*);
    using GetIntFn    = int (__fastcall*)(const void*);
    using GetFloatFn  = float (__fastcall*)(const void*);

    const void* pSlotObj = *reinterpret_cast<const void* const*>(
        pHeroBytes + 0x420 + static_cast<size_t>(slotIdx) * 8);
    if (!pSlotObj) return 0;

    GetAsToolFn pfnAsTool = ReadVtableSlot<GetAsToolFn>(pSlotObj, 0x208);
    const void* pTool = pfnAsTool ? pfnAsTool(pSlotObj) : nullptr;
    if (!pTool) return 0;

    // Follow sub-ability slot redirection (cgame_x64.dll+0x150b54: vtable+0x210 -> +0x220 -> +0xe98)
    GetBoolFn pfnIsAbility = ReadVtableSlot<GetBoolFn>(pTool, 0x210);
    if (pfnIsAbility && pfnIsAbility(pTool)) {
        GetAsToolFn pfnAsAbility = ReadVtableSlot<GetAsToolFn>(pTool, 0x220);
        const void* pAbility = pfnAsAbility ? pfnAsAbility(pTool) : nullptr;
        GetIntFn pfnSubSlot = pAbility ? ReadVtableSlot<GetIntFn>(pAbility, 0xe98) : nullptr;
        int subSlot = pfnSubSlot ? pfnSubSlot(pAbility) : -1;
        if (subSlot >= 0 && subSlot <= 0x4f) {
            const void* pSubObj = *reinterpret_cast<const void* const*>(
                pHeroBytes + 0x420 + static_cast<size_t>(subSlot) * 8);
            GetAsToolFn pfnSubAsTool = pSubObj ? ReadVtableSlot<GetAsToolFn>(pSubObj, 0x208) : nullptr;
            pTool = pfnSubAsTool ? pfnSubAsTool(pSubObj) : nullptr;
            if (!pTool) return 0;
        }
    }

    GetIntFn pfnLevel    = ReadVtableSlot<GetIntFn>(pTool, 0x4a8);
    GetIntFn pfnMaxLevel = ReadVtableSlot<GetIntFn>(pTool, 0x4b8);
    int level    = pfnLevel ? pfnLevel(pTool) : 0;
    int maxLevel = pfnMaxLevel ? pfnMaxLevel(pTool) : 0;
    if (level == 0 && maxLevel != 0) {
        return 0; // silver (unleveled)
    }

    GetBoolFn pfnDisabled = ReadVtableSlot<GetBoolFn>(pTool, 0xe08);
    if (pfnDisabled && pfnDisabled(pTool)) {
        return 1; // red (disabled)
    }

    if (ctx && ctx->pfnGetRemainingCooldownTime &&
        ctx->pfnGetRemainingCooldownTime(pTool, false) > 0u) {
        return 2; // yellow (on cooldown)
    }

    GetIntFn pfnActionType = ReadVtableSlot<GetIntFn>(pTool, 0xbd8);
    if (pfnActionType && pfnActionType(pTool) == 1) {
        return 3; // green (passive)
    }

    GetFloatFn pfnManaCost = ReadVtableSlot<GetFloatFn>(pTool, 0xa98);
    float manaCost = pfnManaCost ? pfnManaCost(pTool) : 0.0f;
    if (isHeroAlive && manaCost > 0.0f && manaCost > heroMana) {
        return 4; // blue (need mana)
    }

    GetBoolFn pfnCanActivate = ReadVtableSlot<GetBoolFn>(pTool, 0xe18);
    bool canActivate = pfnCanActivate ? pfnCanActivate(pTool) : false;
    uint16_t toolFlags = *reinterpret_cast<const uint16_t*>(
        reinterpret_cast<const uint8_t*>(pTool) + 0xfa);
    bool isActive = ((toolFlags >> 10) & 1u) != 0;
    if (canActivate || isActive) {
        return 5; // lime (ready / active)
    }

    return 6; // orange
}

static int ReadTeamSlotCountNativeSeh(const Engine20PlayerContext* ctx, int teamId) noexcept {
    // satisfies: OBL-NG-20P-02
    __try {
        const uint8_t* pGame = reinterpret_cast<const uint8_t*>(*ctx->ppGame);
        if (!pGame) return 0;

        const uint8_t* pTeamMapHead = *reinterpret_cast<const uint8_t* const*>(pGame + 0xd0);
        const uint8_t* pTeam = LookupUintMapValue(pTeamMapHead, static_cast<uint32_t>(teamId));
        if (!pTeam) return 0;

        uint32_t teamSize = *reinterpret_cast<const uint32_t*>(pTeam + 0xc8);
        if (teamSize > 32u) return 0;
        return static_cast<int>(teamSize);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringA("[KONGOR 20P WARN] SEH caught exception in ReadTeamSlotCountNative.\n");
        return 0;
    }
}

int ReadTeamSlotCountNative(const Engine20PlayerContext* ctx, int teamId) {
    // satisfies: OBL-NG-20P-05
    if (!Is20PlayerBridgeEnabledNative()) return 0;
    // satisfies: OBL-NG-20P-01
    if (!ctx || !ctx->ppGame || (teamId != 1 && teamId != 2)) {
        return 0;
    }

    KongorScopedProtectedSeh sehGuard;
    return ReadTeamSlotCountNativeSeh(ctx, teamId);
}

static bool ReadLobbyTeamSlotNativeSeh(
    const Engine20PlayerContext* ctx,
    int teamId,
    int seat,
    LobbyTeamSlotSnapshot* outSlot) noexcept
{
    // satisfies: OBL-NG-20P-02
    __try {
        const uint8_t* pGame = reinterpret_cast<const uint8_t*>(*ctx->ppGame);
        if (!pGame) return false;

        const uint8_t* pTeamMapHead = *reinterpret_cast<const uint8_t* const*>(pGame + 0xd0);
        const uint8_t* pTeam = LookupUintMapValue(pTeamMapHead, static_cast<uint32_t>(teamId));
        if (!pTeam) return false;

        uint32_t teamSize = *reinterpret_cast<const uint32_t*>(pTeam + 0xc8);
        if (static_cast<uint32_t>(seat) >= teamSize) {
            return false;
        }

        outSlot->valid = true;

        // satisfies: OBL-NG-20P-04
        uint32_t slotLocks = *reinterpret_cast<const uint32_t*>(pTeam + 0xe8);
        bool isLocked = (slotLocks & (1u << static_cast<uint32_t>(seat))) != 0;
        outSlot->isLocked = isLocked;

        const uint8_t* pLocalPlayer = GetClientLocalPlayer(pGame);
        uint16_t localFlags = pLocalPlayer ? *reinterpret_cast<const uint16_t*>(pLocalPlayer + 0x3de) : 0;
        outSlot->localIsHost = (localFlags & 0x0004u) != 0 ||
            (pLocalPlayer && ctx->pfnIsReferee && ctx->pfnIsReferee(pLocalPlayer));

        int clientNum = ctx->pfnGetClientIDFromTeamIndex(pTeam, static_cast<uint32_t>(seat));
        outSlot->clientNum = clientNum;
        // S2's CTeamInfo::CanJoinTeam (game_shared_x64.dll+0x534c70) truncates m_uiSlotLocks (+0xe8)
        // and (1 << seat) to uint8_t, breaking seats 8..9 and client-side spectator checks.
        // Evaluate seat joinability directly from 32-bit lock bitmask and occupancy.
        outSlot->canJoin = (clientNum < 0) && !isLocked && (static_cast<uint32_t>(seat) < teamSize);

        if (clientNum < 0) {
            outSlot->slotStatus = isLocked ? 2 : 0;
            return true;
        }

        const uint8_t* pClientMapHead = *reinterpret_cast<const uint8_t* const*>(pGame + 0x1e8);
        const uint8_t* pPlayer = LookupIntMapValue(pClientMapHead, clientNum);
        if (!pPlayer) {
            outSlot->clientNum = -1;
            outSlot->canJoin = !isLocked && (static_cast<uint32_t>(seat) < teamSize);
            outSlot->slotStatus = isLocked ? 2 : 0;
            return true;
        }

        outSlot->slotStatus = 1;
        outSlot->accountId = *reinterpret_cast<const int32_t*>(pPlayer + 0x2d0);
        outSlot->psr = static_cast<int>(*reinterpret_cast<const uint16_t*>(pPlayer + 0x404));
        outSlot->loadingPct = *reinterpret_cast<const float*>(pPlayer + 0x330);

        uint16_t flags = *reinterpret_cast<const uint16_t*>(pPlayer + 0x3de);
        outSlot->isReady = (flags & 0x0002u) != 0;
        outSlot->isHost  = (flags & 0x0004u) != 0;
        outSlot->isMe    = (pLocalPlayer != nullptr && pPlayer == pLocalPlayer);
        outSlot->isBot   = ctx->pfnIsBot ? ctx->pfnIsBot(pPlayer) : false;
        outSlot->isReferee = ctx->pfnIsReferee ? ctx->pfnIsReferee(pPlayer) : false;

        if (ctx->ppNetResMgr && *ctx->ppNetResMgr && ctx->pfnGetNetString) {
            uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(pPlayer + 0x2d8);
            const void* pWName = ctx->pfnGetNetString(*ctx->ppNetResMgr, nameIdx);
            CopyK2WStringToUtf8(pWName, outSlot->name, sizeof(outSlot->name));
        }

        uint16_t selectedHeroId = *reinterpret_cast<const uint16_t*>(pPlayer + 0xc0);
        uint16_t potentialHeroId = *reinterpret_cast<const uint16_t*>(pPlayer + 0xcc);
        uint16_t heroId = 0xFFFFu;
        if (selectedHeroId != 0xFFFFu) {
            outSlot->hasSelectedHero = true;
            heroId = selectedHeroId;
        } else if (potentialHeroId != 0xFFFFu) {
            outSlot->hasSelectedHero = false;
            heroId = potentialHeroId;
        }
        if (heroId != 0xFFFFu && ctx->ppEntityReg && *ctx->ppEntityReg && ctx->pfnLookupEntityName) {
            const void* pWHeroDef = ctx->pfnLookupEntityName(*ctx->ppEntityReg, heroId);
            CopyK2WStringToUtf8(pWHeroDef, outSlot->heroDefName, sizeof(outSlot->heroDefName));
        }

        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        std::memset(outSlot, 0, sizeof(*outSlot));
        outSlot->clientNum = -1;
        OutputDebugStringA("[KONGOR 20P WARN] SEH caught exception in ReadLobbyTeamSlotNative.\n");
        return false;
    }
}

bool ReadLobbyTeamSlotNative(
    const Engine20PlayerContext* ctx,
    int teamId,
    int seat,
    LobbyTeamSlotSnapshot* outSlot)
{
    if (!outSlot) return false;
    std::memset(outSlot, 0, sizeof(*outSlot));
    outSlot->clientNum = -1;

    // satisfies: OBL-NG-20P-05
    if (!Is20PlayerBridgeEnabledNative()) return false;
    // satisfies: OBL-NG-20P-01
    if (!ctx || !ctx->ppGame || !ctx->pfnGetClientIDFromTeamIndex ||
        (teamId != 1 && teamId != 2) || seat < 0 || seat > 9) {
        return false;
    }

    KongorScopedProtectedSeh sehGuard;
    return ReadLobbyTeamSlotNativeSeh(ctx, teamId, seat, outSlot);
}

static bool ReadScoreboardTeamSlotNativeSeh(
    const Engine20PlayerContext* ctx,
    int teamId,
    int seat,
    ScoreboardTeamSlotSnapshot* outSlot) noexcept
{
    // satisfies: OBL-NG-20P-02
    __try {
        const uint8_t* pGame = reinterpret_cast<const uint8_t*>(*ctx->ppGame);
        if (!pGame) return false;

        const uint8_t* pTeamMapHead = *reinterpret_cast<const uint8_t* const*>(pGame + 0xd0);
        const uint8_t* pTeam = LookupUintMapValue(pTeamMapHead, static_cast<uint32_t>(teamId));
        if (!pTeam) return false;

        uint32_t teamSize = *reinterpret_cast<const uint32_t*>(pTeam + 0xc8);
        if (static_cast<uint32_t>(seat) >= teamSize) {
            return false;
        }

        outSlot->teamId = teamId;
        outSlot->seat = seat;
        outSlot->teamScore = *reinterpret_cast<const int32_t*>(pTeam + 0x16c);

        int clientNum = ctx->pfnGetClientIDFromTeamIndex(pTeam, static_cast<uint32_t>(seat));
        outSlot->clientNum = clientNum;
        if (clientNum < 0) {
            outSlot->exists = false;
            return true;
        }

        const uint8_t* pClientMapHead = *reinterpret_cast<const uint8_t* const*>(pGame + 0x1e8);
        const uint8_t* pPlayer = LookupIntMapValue(pClientMapHead, clientNum);
        if (!pPlayer) {
            outSlot->clientNum = -1;
            outSlot->exists = false;
            return true;
        }

        outSlot->exists = true;
        outSlot->kills  = *reinterpret_cast<const int32_t*>(pPlayer + 0x470);
        outSlot->deaths = *reinterpret_cast<const int32_t*>(pPlayer + 0x474);
        outSlot->assists = *reinterpret_cast<const int32_t*>(pPlayer + 0x478);
        outSlot->gold   = static_cast<int>(*reinterpret_cast<const uint16_t*>(pPlayer + 0x404)); // cgame+0x195f25 param11 m_unGold (synced for self & allies)
        outSlot->buybacks = *reinterpret_cast<const int32_t*>(pPlayer + 0x49c);
        outSlot->pingOrScore = *reinterpret_cast<const int32_t*>(pPlayer + 0x4b0);
        outSlot->loadingPct = *reinterpret_cast<const float*>(pPlayer + 0x330);

        uint16_t flags = *reinterpret_cast<const uint16_t*>(pPlayer + 0x3de);
        uint16_t statusFlags = *reinterpret_cast<const uint16_t*>(pPlayer + 0x3e0);
        outSlot->isDisconnected = (flags & 0x0040u) != 0;
        outSlot->isTerminated   = (statusFlags & 0x0010u) != 0;
        outSlot->isBot = ctx->pfnIsBot ? ctx->pfnIsBot(pPlayer) : false;

        const uint8_t* pLocalPlayer = GetClientLocalPlayer(pGame);
        outSlot->isMe = (pLocalPlayer != nullptr && pPlayer == pLocalPlayer);

        int32_t localTeamId = pLocalPlayer ? *reinterpret_cast<const int32_t*>(pLocalPlayer + 0xac) : 0;
        int32_t localClientNum = pLocalPlayer ? *reinterpret_cast<const int32_t*>(pLocalPlayer + 0xa8) : -1;
        int32_t targetTeamId = *reinterpret_cast<const int32_t*>(pPlayer + 0xac);
        // satisfies: OBL-NG-20P-06
        bool sameTeam = (pLocalPlayer != nullptr) && (localTeamId > 0) && (localTeamId == targetTeamId);
        outSlot->sameTeam = sameTeam;

        // satisfies: OBL-NG-20P-07
        if (ctx->ppVoiceMgr && *ctx->ppVoiceMgr) {
            const uint8_t* pVoiceMgr = reinterpret_cast<const uint8_t*>(*ctx->ppVoiceMgr);
            const uint8_t* pTalkingHead = *reinterpret_cast<const uint8_t* const*>(pVoiceMgr + 0x41e0);
            const uint8_t* pMutedHead   = *reinterpret_cast<const uint8_t* const*>(pVoiceMgr + 0x41f0);
            outSlot->isTalking    = LookupIntSetContains(pTalkingHead, clientNum);
            outSlot->isVoiceMuted = LookupIntSetContains(pMutedHead, clientNum);
        }

        if (sameTeam) {
            outSlot->isAFK = (flags & 0x8000u) != 0;
            int32_t gameTimeNowMs = *reinterpret_cast<const int32_t*>(pGame + 0x08);
            int32_t dcRemMs = *reinterpret_cast<const int32_t*>(pPlayer + 0x438) - gameTimeNowMs;
            outSlot->dcTimeMs = (outSlot->isDisconnected && dcRemMs > 0) ? dcRemMs : 0;

            if (localClientNum >= 0) {
                int shareFrom = (ctx->pfnHasSharedPartialControl &&
                    ctx->pfnHasSharedPartialControl(pPlayer, localClientNum)) ? 1 : 0;
                if (ctx->pfnHasSharedFullControl &&
                    ctx->pfnHasSharedFullControl(pPlayer, localClientNum)) {
                    shareFrom += 2;
                }
                int shareTo = (ctx->pfnHasSharedPartialControl &&
                    ctx->pfnHasSharedPartialControl(pLocalPlayer, clientNum)) ? 1 : 0;
                if (ctx->pfnHasSharedFullControl &&
                    ctx->pfnHasSharedFullControl(pLocalPlayer, clientNum)) {
                    shareTo += 2;
                }
                outSlot->shareFromAlly = shareFrom;
                outSlot->shareToAlly   = shareTo;
            }
            if (ctx->pfnGetNoHelp) {
                outSlot->noHelp = ctx->pfnGetNoHelp(pLocalPlayer, const_cast<uint8_t*>(pPlayer));
            }
        }

        if (ctx->ppNetResMgr && *ctx->ppNetResMgr && ctx->pfnGetNetString) {
            uint16_t nameIdx = *reinterpret_cast<const uint16_t*>(pPlayer + 0x2d8);
            const void* pWName = ctx->pfnGetNetString(*ctx->ppNetResMgr, nameIdx);
            CopyK2WStringToUtf8(pWName, outSlot->name, sizeof(outSlot->name));
        }

        for (int s = 0; s < 6; ++s) {
            if (ctx->pfnGetGameplayStat) {
                outSlot->stats[s] = ctx->pfnGetGameplayStat(pPlayer, s);
            } else {
                outSlot->stats[s] = *reinterpret_cast<const float*>(pPlayer + 0x4b8 + s * 4);
            }
        }

        // Resolve IHeroEntity via IEntityDirectory (pGame + 0x28)
        const void* pEntityDir = *reinterpret_cast<const void* const*>(pGame + 0x28);
        uint32_t heroIdx = *reinterpret_cast<const uint32_t*>(pPlayer + 0xb0);
        const void* pHero = nullptr;
        if (pEntityDir && heroIdx != 0xFFFFFFFFu) {
            using GetEntityFn = void* (__fastcall*)(const void*, uint32_t);
            using GetAsHeroFn = void* (__fastcall*)(const void*);
            GetEntityFn pfnGetEnt = ReadVtableSlot<GetEntityFn>(pEntityDir, 0x10);
            void* pEnt = pfnGetEnt ? pfnGetEnt(pEntityDir, heroIdx) : nullptr;
            GetAsHeroFn pfnAsHero = pEnt ? ReadVtableSlot<GetAsHeroFn>(pEnt, 0x178) : nullptr;
            pHero = pfnAsHero ? pfnAsHero(pEnt) : nullptr;
        }

        if (!pHero) {
            std::snprintf(outSlot->heroName, sizeof(outSlot->heroName), "No hero");
            outSlot->level = 1;
            outSlot->isDead = true;
            outSlot->canSeeItems = false;
            return true;
        }

        using GetK2WStringFn = const void* (__fastcall*)(const void*);
        using GetLevelFn = uint32_t (__fastcall*)(const void*);
        using GetFloatMetricFn = float (__fastcall*)(const void*);

        GetK2WStringFn pfnDispName = ReadVtableSlot<GetK2WStringFn>(pHero, 0x1150);
        if (pfnDispName) {
            CopyK2WStringToUtf8(pfnDispName(pHero), outSlot->heroName, sizeof(outSlot->heroName));
        }
        GetK2WStringFn pfnIcon = ReadVtableSlot<GetK2WStringFn>(pHero, 0xc08);
        if (pfnIcon) {
            CopyK2WStringToUtf8(pfnIcon(pHero), outSlot->heroIcon, sizeof(outSlot->heroIcon));
        }
        GetLevelFn pfnLevel = ReadVtableSlot<GetLevelFn>(pHero, 0x608);
        outSlot->level = pfnLevel ? static_cast<int>(pfnLevel(pHero)) : 1;

        const uint8_t* pHeroBytes = reinterpret_cast<const uint8_t*>(pHero);
        outSlot->isDead = (*(pHeroBytes + 0xd0) != 1);

        int32_t respawnEndMs = *reinterpret_cast<const int32_t*>(pHeroBytes + 0x152c);
        int32_t gameTimeMs   = *reinterpret_cast<const int32_t*>(pGame + 0x08);
        if (respawnEndMs != -1 && respawnEndMs > gameTimeMs) {
            outSlot->respawnSec = static_cast<int>(std::ceil((respawnEndMs - gameTimeMs) * 0.001f));
        } else {
            outSlot->respawnSec = 0;
        }

        // satisfies: OBL-NG-20P-06
        if (sameTeam) {
            uint32_t entIdxField = *reinterpret_cast<const uint32_t*>(pHeroBytes + 0x1c);
            outSlot->heroEntityIndex = static_cast<int>((entIdxField != 0u) ? entIdxField : heroIdx);

            float curHp = *reinterpret_cast<const float*>(pHeroBytes + 0x360);
            GetFloatMetricFn pfnMaxHp = ReadVtableSlot<GetFloatMetricFn>(pHero, 0x678);
            GetFloatMetricFn pfnHpPct = ReadVtableSlot<GetFloatMetricFn>(pHero, 0xa90);
            float maxHp = pfnMaxHp ? pfnMaxHp(pHero) : 0.0f;
            float hpPct = pfnHpPct ? pfnHpPct(pHero) : ((maxHp > 0.0f) ? (curHp / maxHp) : 0.0f);
            if (hpPct < 0.0f) hpPct = 0.0f;
            if (hpPct > 1.0f) hpPct = 1.0f;
            outSlot->healthPct = outSlot->isDead ? 0.0f : hpPct;

            GetFloatMetricFn pfnMana    = ReadVtableSlot<GetFloatMetricFn>(pHero, 0xab0);
            GetFloatMetricFn pfnMaxMana = ReadVtableSlot<GetFloatMetricFn>(pHero, 0x6b8);
            float curMana = pfnMana ? pfnMana(pHero) : 0.0f;
            float maxMana = pfnMaxMana ? pfnMaxMana(pHero) : 0.0f;
            outSlot->hasMana = (maxMana > 0.0f);
            float mpPct = (maxMana > 0.0f) ? (curMana / maxMana) : 0.0f;
            if (mpPct < 0.0f) mpPct = 0.0f;
            if (mpPct > 1.0f) mpPct = 1.0f;
            outSlot->manaPct = outSlot->isDead ? 0.0f : mpPct;

            bool isAlive = !outSlot->isDead;
            for (int a = 0; a < 4; ++a) {
                outSlot->abilityDot[a] = EvaluateAllyAbilityDot(ctx, pHeroBytes, curMana, isAlive, a);
            }
        }

        // satisfies: OBL-NG-20P-03
        bool sameTeamOrSpectator = (pLocalPlayer == nullptr) || (localTeamId == 0) || (localTeamId == targetTeamId);
        bool canSeeHero = sameTeamOrSpectator ||
            (ctx->pfnCanSee != nullptr && ctx->pfnCanSee(pLocalPlayer, pHero));
        outSlot->canSeeItems = canSeeHero;

        if (canSeeHero) {
            static const int kInvSlots[8] = { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x3f, 0x40 };
            using GetAsItemFn = const void* (__fastcall*)(const void*);
            for (int i = 0; i < 8; ++i) {
                const void* pSlotObj = *reinterpret_cast<const void* const*>(
                    pHeroBytes + 0x420 + static_cast<size_t>(kInvSlots[i]) * 8);
                if (!pSlotObj) continue;
                GetAsItemFn pfnAsItem = ReadVtableSlot<GetAsItemFn>(pSlotObj, 0x208);
                const void* pItem = pfnAsItem ? pfnAsItem(pSlotObj) : nullptr;
                if (!pItem) continue;
                GetK2WStringFn pfnItemIcon = ReadVtableSlot<GetK2WStringFn>(pItem, 0x540);
                if (pfnItemIcon) {
                    CopyK2WStringToUtf8(pfnItemIcon(pItem), outSlot->items[i], sizeof(outSlot->items[i]));
                }
            }
        }

        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        std::memset(outSlot, 0, sizeof(*outSlot));
        outSlot->clientNum = -1;
        outSlot->heroEntityIndex = -1;
        OutputDebugStringA("[KONGOR 20P WARN] SEH caught exception in ReadScoreboardTeamSlotNative.\n");
        return false;
    }
}

bool ReadScoreboardTeamSlotNative(
    const Engine20PlayerContext* ctx,
    int teamId,
    int seat,
    ScoreboardTeamSlotSnapshot* outSlot)
{
    if (!outSlot) return false;
    std::memset(outSlot, 0, sizeof(*outSlot));
    outSlot->clientNum = -1;
    outSlot->heroEntityIndex = -1;

    // satisfies: OBL-NG-20P-05
    if (!Is20PlayerBridgeEnabledNative()) return false;
    // satisfies: OBL-NG-20P-01
    if (!ctx || !ctx->ppGame || !ctx->pfnGetClientIDFromTeamIndex ||
        (teamId != 1 && teamId != 2) || seat < 0 || seat > 9) {
        return false;
    }

    KongorScopedProtectedSeh sehGuard;
    return ReadScoreboardTeamSlotNativeSeh(ctx, teamId, seat, outSlot);
}

static bool LuaReadIntArgInline(void* L, int idx1Based, int* outVal) {
    const LuaTValue* tv = LuaGetStackArgInline(L, idx1Based);
    if (!tv || tv->tt != 3 /* LUA_TNUMBER */) return false;
    if (outVal) *outVal = static_cast<int>(tv->value.n);
    return true;
}

static void LuaSetCStringField(void* L, const char* key, const char* val) {
    const char* s = val ? val : "";
    g_lua_pushlstring(L, s, std::strlen(s));
    g_lua_setfield(L, -2, key);
}

static int Lua_Kongor_GetBridgeVersion(void* L) {
    LuaPushIntegerInline(L, static_cast<ptrdiff_t>(Get20PlayerBridgeVersionNative()));
    return 1;
}

static int Lua_Kongor_Is20PlayerBridgeEnabled(void* L) {
    LuaPushBooleanInline(L, Is20PlayerBridgeEnabledNative() ? 1 : 0);
    return 1;
}

static int Lua_Kongor_GetTeamSlotCount(void* L) {
    int teamId = 0;
    if (!LuaReadIntArgInline(L, 1, &teamId)) {
        LuaPushIntegerInline(L, 0);
        return 1;
    }
    const Engine20PlayerContext* ctx = EnsureEngine20PlayerContext();
    LuaPushIntegerInline(L, static_cast<ptrdiff_t>(ReadTeamSlotCountNative(ctx, teamId)));
    return 1;
}

static int Lua_Kongor_GetLobbyTeamSlot(void* L) {
    if (!L || !g_lua_createtable || !g_lua_setfield || !g_lua_pushlstring) return 0;
    int teamId = 0, seat = -1;
    if (!LuaReadIntArgInline(L, 1, &teamId) || !LuaReadIntArgInline(L, 2, &seat)) {
        return 0;
    }

    const Engine20PlayerContext* ctx = EnsureEngine20PlayerContext();
    LobbyTeamSlotSnapshot snap = {};
    if (!ReadLobbyTeamSlotNative(ctx, teamId, seat, &snap) || !snap.valid) {
        return 0;
    }

    g_lua_createtable(L, 0, 16);
    LuaPushIntegerInline(L, snap.slotStatus);     g_lua_setfield(L, -2, "slotStatus");
    LuaPushIntegerInline(L, snap.clientNum);      g_lua_setfield(L, -2, "clientNum");
    LuaPushIntegerInline(L, snap.accountId);      g_lua_setfield(L, -2, "accountId");
    LuaPushIntegerInline(L, snap.psr);            g_lua_setfield(L, -2, "psr");
    LuaPushBooleanInline(L, snap.isHost ? 1 : 0); g_lua_setfield(L, -2, "isHost");
    LuaPushBooleanInline(L, snap.isReady ? 1 : 0); g_lua_setfield(L, -2, "isReady");
    LuaPushBooleanInline(L, snap.isLocked ? 1 : 0); g_lua_setfield(L, -2, "isLocked");
    LuaPushBooleanInline(L, snap.isBot ? 1 : 0);  g_lua_setfield(L, -2, "isBot");
    LuaPushBooleanInline(L, snap.isReferee ? 1 : 0); g_lua_setfield(L, -2, "isReferee");
    LuaPushBooleanInline(L, snap.isMe ? 1 : 0);   g_lua_setfield(L, -2, "isMe");
    LuaPushBooleanInline(L, snap.localIsHost ? 1 : 0); g_lua_setfield(L, -2, "localIsHost");
    LuaPushBooleanInline(L, snap.canJoin ? 1 : 0); g_lua_setfield(L, -2, "canJoin");
    LuaPushBooleanInline(L, snap.hasSelectedHero ? 1 : 0); g_lua_setfield(L, -2, "hasSelectedHero");
    LuaPushNumberInline(L, static_cast<double>(snap.loadingPct)); g_lua_setfield(L, -2, "loadingPct");
    LuaSetCStringField(L, "name", snap.name);
    LuaSetCStringField(L, "heroDefName", snap.heroDefName);
    return 1;
}

static int Lua_Kongor_GetScoreboardTeamSlot(void* L) {
    if (!L || !g_lua_createtable || !g_lua_setfield || !g_lua_pushlstring) return 0;
    int teamId = 0, seat = -1;
    if (!LuaReadIntArgInline(L, 1, &teamId) || !LuaReadIntArgInline(L, 2, &seat)) {
        return 0;
    }

    const Engine20PlayerContext* ctx = EnsureEngine20PlayerContext();
    ScoreboardTeamSlotSnapshot snap = {};
    if (!ReadScoreboardTeamSlotNative(ctx, teamId, seat, &snap)) {
        return 0;
    }

    g_lua_createtable(L, 0, 48);
    LuaPushBooleanInline(L, snap.exists ? 1 : 0);       g_lua_setfield(L, -2, "exists");
    LuaPushIntegerInline(L, snap.clientNum);            g_lua_setfield(L, -2, "clientNum");
    LuaPushIntegerInline(L, snap.teamScore);            g_lua_setfield(L, -2, "teamScore");
    if (!snap.exists) {
        return 1;
    }

    LuaPushIntegerInline(L, snap.level);                g_lua_setfield(L, -2, "level");
    LuaPushIntegerInline(L, snap.kills);                g_lua_setfield(L, -2, "kills");
    LuaPushIntegerInline(L, snap.deaths);               g_lua_setfield(L, -2, "deaths");
    LuaPushIntegerInline(L, snap.assists);              g_lua_setfield(L, -2, "assists");
    LuaPushIntegerInline(L, snap.gold);                 g_lua_setfield(L, -2, "gold");
    LuaPushIntegerInline(L, snap.buybacks);             g_lua_setfield(L, -2, "buybacks");
    LuaPushIntegerInline(L, snap.pingOrScore);          g_lua_setfield(L, -2, "pingOrScore");
    LuaPushIntegerInline(L, snap.respawnSec);           g_lua_setfield(L, -2, "respawnSec");
    LuaPushNumberInline(L, static_cast<double>(snap.loadingPct)); g_lua_setfield(L, -2, "loadingPct");
    LuaPushBooleanInline(L, snap.isDead ? 1 : 0);       g_lua_setfield(L, -2, "isDead");
    LuaPushBooleanInline(L, snap.isDisconnected ? 1 : 0); g_lua_setfield(L, -2, "isDisconnected");
    LuaPushBooleanInline(L, snap.isTerminated ? 1 : 0); g_lua_setfield(L, -2, "isTerminated");
    LuaPushBooleanInline(L, snap.isBot ? 1 : 0);        g_lua_setfield(L, -2, "isBot");
    LuaPushBooleanInline(L, snap.isMe ? 1 : 0);         g_lua_setfield(L, -2, "isMe");
    LuaPushBooleanInline(L, snap.sameTeam ? 1 : 0);     g_lua_setfield(L, -2, "sameTeam");
    LuaPushBooleanInline(L, snap.canSeeItems ? 1 : 0);  g_lua_setfield(L, -2, "canSeeItems");
    LuaPushBooleanInline(L, snap.isTalking ? 1 : 0);    g_lua_setfield(L, -2, "isTalking");
    LuaPushBooleanInline(L, snap.isVoiceMuted ? 1 : 0); g_lua_setfield(L, -2, "isVoiceMuted");
    LuaPushIntegerInline(L, snap.heroEntityIndex);      g_lua_setfield(L, -2, "heroEntityIndex");
    LuaPushNumberInline(L, static_cast<double>(snap.healthPct)); g_lua_setfield(L, -2, "healthPct");
    LuaPushNumberInline(L, static_cast<double>(snap.manaPct));   g_lua_setfield(L, -2, "manaPct");
    LuaPushBooleanInline(L, snap.hasMana ? 1 : 0);      g_lua_setfield(L, -2, "hasMana");
    LuaPushBooleanInline(L, snap.isAFK ? 1 : 0);        g_lua_setfield(L, -2, "isAFK");
    LuaPushIntegerInline(L, snap.dcTimeMs);             g_lua_setfield(L, -2, "dcTimeMs");
    LuaPushIntegerInline(L, snap.shareFromAlly);        g_lua_setfield(L, -2, "shareFromAlly");
    LuaPushIntegerInline(L, snap.shareToAlly);          g_lua_setfield(L, -2, "shareToAlly");
    LuaPushBooleanInline(L, snap.noHelp ? 1 : 0);       g_lua_setfield(L, -2, "noHelp");
    LuaPushIntegerInline(L, snap.abilityDot[0]);        g_lua_setfield(L, -2, "abilityDot0");
    LuaPushIntegerInline(L, snap.abilityDot[1]);        g_lua_setfield(L, -2, "abilityDot1");
    LuaPushIntegerInline(L, snap.abilityDot[2]);        g_lua_setfield(L, -2, "abilityDot2");
    LuaPushIntegerInline(L, snap.abilityDot[3]);        g_lua_setfield(L, -2, "abilityDot3");
    LuaSetCStringField(L, "name", snap.name);
    LuaSetCStringField(L, "heroName", snap.heroName);
    LuaSetCStringField(L, "heroIcon", snap.heroIcon);

    static const char* kItemKeys[8] = { "item1", "item2", "item3", "item4", "item5", "item6", "item7", "item8" };
    for (int i = 0; i < 8; ++i) {
        LuaSetCStringField(L, kItemKeys[i], snap.items[i]);
    }
    static const char* kStatKeys[6] = { "stat0", "stat1", "stat2", "stat3", "stat4", "stat5" };
    for (int s = 0; s < 6; ++s) {
        LuaPushNumberInline(L, static_cast<double>(snap.stats[s]));
        g_lua_setfield(L, -2, kStatKeys[s]);
    }
    return 1;
}

// ============================================================================
// REGISTRATION ENTRYPOINT
// ============================================================================

void InvalidateKongorLuaSharedMem() {
    g_luaBindingsRegistered.store(false, std::memory_order_release);
    g_lastRegisteredLuaState.store(nullptr, std::memory_order_release);
}

bool EnsureKongorLuaSharedMem(void* L, KongorSharedMemHandle* pShmHandle) {
    if (!L) return false;
    if (g_luaBindingsRegistered.load(std::memory_order_acquire) &&
        g_lastRegisteredLuaState.load(std::memory_order_acquire) == L) {
        return true;
    }
    return RegisterKongorLuaSharedMem(L, pShmHandle);
}

bool RegisterKongorLuaSharedMem(void* L, KongorSharedMemHandle* pShmHandle) {
    if (!L) return false;

    // Cache shared memory handle
    if (pShmHandle) {
        g_pShmHandle = pShmHandle;
    }

    // Resolve Lua C API functions
    if (!ResolveLuaSymbols()) {
        OutputDebugStringA("[KONGOR LUA] Cannot register bindings: Lua symbols unresolved.\n");
        return false;
    }

    // Create 'KongorSharedMem' table
    g_lua_createtable(L, 0, 16);

    // Register methods
    g_lua_pushcclosure(L, Lua_Kongor_ReadFast, 0);
    g_lua_setfield(L, -2, "ReadFast");

    g_lua_pushcclosure(L, Lua_Kongor_GetFps, 0);
    g_lua_setfield(L, -2, "GetFps");

    g_lua_pushcclosure(L, Lua_Kongor_GetPing, 0);
    g_lua_setfield(L, -2, "GetPing");

    g_lua_pushcclosure(L, Lua_Kongor_GetServerTick, 0);
    g_lua_setfield(L, -2, "GetServerTick");

    g_lua_pushcclosure(L, Lua_Kongor_GetCpuGpuMs, 0);
    g_lua_setfield(L, -2, "GetCpuGpuMs");

    g_lua_pushcclosure(L, Lua_Kongor_GetTickData, 0);
    g_lua_setfield(L, -2, "GetTickData");

    g_lua_pushcclosure(L, Lua_Kongor_GetVersion, 0);
    g_lua_setfield(L, -2, "GetVersion");

    g_lua_pushcclosure(L, Lua_Kongor_IsConnected, 0);
    g_lua_setfield(L, -2, "IsConnected");

    g_lua_pushcclosure(L, Lua_Kongor_GetPendingEval, 0);
    g_lua_setfield(L, -2, "GetPendingEval");

    g_lua_pushcclosure(L, Lua_Kongor_SetEvalResult, 0);
    g_lua_setfield(L, -2, "SetEvalResult");

    g_lua_pushcclosure(L, Lua_Kongor_GetGamePhase, 0);
    g_lua_setfield(L, -2, "GetGamePhase");

    g_lua_pushcclosure(L, Lua_Kongor_GetBridgeVersion, 0);
    g_lua_setfield(L, -2, "GetBridgeVersion");

    g_lua_pushcclosure(L, Lua_Kongor_Is20PlayerBridgeEnabled, 0);
    g_lua_setfield(L, -2, "Is20PlayerBridgeEnabled");

    g_lua_pushcclosure(L, Lua_Kongor_GetTeamSlotCount, 0);
    g_lua_setfield(L, -2, "GetTeamSlotCount");

    g_lua_pushcclosure(L, Lua_Kongor_GetLobbyTeamSlot, 0);
    g_lua_setfield(L, -2, "GetLobbyTeamSlot");

    g_lua_pushcclosure(L, Lua_Kongor_GetScoreboardTeamSlot, 0);
    g_lua_setfield(L, -2, "GetScoreboardTeamSlot");

    // Publish to LUA_GLOBALSINDEX
    g_lua_setfield(L, LUA_GLOBALSINDEX, "KongorSharedMem");

    g_lastRegisteredLuaState.store(L, std::memory_order_release);
    g_luaBindingsRegistered.store(true, std::memory_order_release);
    OutputDebugStringA("[KONGOR NATIVE GUARD] Successfully injected 'KongorSharedMem' (including GetPendingEval/SetEvalResult/GetGamePhase) into Lua 5.1 environment.\n");
    return true;
}

} // namespace Kongor

