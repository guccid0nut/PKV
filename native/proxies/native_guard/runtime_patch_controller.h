// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <algorithm>
#include <functional>
#include <mutex>
#include <vector>

#ifdef __cplusplus
namespace Kongor {
namespace PatchController {

static constexpr size_t REGION_SNAPSHOT_SIZE = 0x10000; // 64 KiB Windows allocation granularity
static constexpr size_t PAGE_SIZE_4K         = 0x1000;  // 4 KiB page granularity
static constexpr size_t PAGES_PER_REGION     = REGION_SNAPSHOT_SIZE / PAGE_SIZE_4K; // 16 pages
static constexpr size_t MAX_PATCH_BYTES      = 24;

struct RegionSnapshot {
    uintptr_t baseAddress;                    // 64 KiB aligned base (addr & ~0xFFFFULL)
    uint64_t  checksumFnv1a;                  // 64-bit FNV-1a over valid captured bytes
    uint32_t  capturedPagesCount;             // Number of committed 4 KiB pages captured
    uint8_t   validPageMask[PAGES_PER_REGION];// 1 if 4 KiB page was readable at snapshot time
    uint8_t   bytes[REGION_SNAPSHOT_SIZE];    // Immutable unpatched code backup
};

using GroupStateCallbackFn = void(*)(bool enabled);

// Initializes the controller and opens native_guard_hooks.log
void Initialize();

// Registers a group-level state callback (e.g. for "prewarm" worker thread or "vfs" archive state)
void RegisterGroupCallback(const char* groupName, GroupStateCallbackFn callback, bool initialEnabled = true);

// Captures or retrieves the 64 KiB RegionSnapshot covering targetAddr..targetAddr+len
bool EnsureRegionSnapshotForRange(void* targetAddr, size_t len, uint64_t* outChecksum = nullptr);

// Reads original unpatched bytes from the 64 KiB RegionSnapshot
bool ReadOriginalBytesFromSnapshot(void* targetAddr, uint8_t* outBuf, size_t len);

// Installs a 64-bit inline detour (14-byte FF 25 RIP+0 jump + NOPs, with optional trampoline)
// Verifies expectedPrologue (if provided) and validates instruction boundaries / RIP-relative / branch hazards (#268)
// when ppOrigTrampoline != nullptr. Pass ppOrigTrampoline == nullptr for full-replacement hooks.
bool InstallManagedInlineHook64(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    void* pHook,
    void** ppOrigTrampoline,
    size_t stolenBytes,
    const uint8_t* expectedPrologue = nullptr,
    size_t expectedPrologueLen = 0
);

struct PrologueValidationResult {
    bool        isValid          = false;
    size_t      decodedBytes     = 0;
    size_t      faultOffset      = 0;
    uint8_t     faultOpcode      = 0;
    const char* refusalReason    = "NONE";
};

// (#268) HDE64-based prologue validator:
// When requiresCallableTrampoline == true (ppOrigTrampoline != nullptr), refuses:
//   1. RIP-relative memory operands ((modrm & 0xC7) == 0x05) at any offset
//   2. Relative branches (70..7F, EB, E0..E3, 0F 80..8F, or E8/E9 at offset > 0; E8/E9 at offset 0 is relocated)
//   3. Early RET / INT3 (C3, CB, C2, CA, CC) before stolenBytes
//   4. Mid-instruction split (decoded instruction boundaries do not land exactly on stolenBytes)
bool ValidateHookPrologue64(
    const uint8_t* codeBytes,
    size_t stolenBytes,
    bool requiresCallableTrampoline,
    PrologueValidationResult* outResult = nullptr
) noexcept;

size_t ComputeSafeHookLength64(
    const uint8_t* codeBytes,
    size_t maxScanBytes,
    size_t minRequiredBytes = 14
) noexcept;

// Installs an in-place byte patch (e.g. 2-byte Sleep(0) bypass or 4-byte xorps/ret)
// Verifies expectedPrologue before modifying memory.
bool InstallManagedBytePatch(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    const uint8_t* patchBytes,
    size_t patchLen,
    const uint8_t* expectedPrologue,
    size_t expectedPrologueLen
);

// (#201) Pattern-scanned code-cave bidirectional 32-bit relative jump detour synthesizer (Mask_Jump32):
// Locates a contiguous 0xCC / 0x90 / 0x00 padding cave within +/- 2 GB of targetOffset, writes the
// replacement payload + 5-byte E9 rel32 return jump to (pTarget + patchSize), and overwrites pTarget
// with a 5-byte E9 rel32 jump to pCodeCave + 0x90 NOP fill.
bool FindContiguousCodeCaveNear(
    const uint8_t* moduleBytes,
    size_t moduleSize,
    size_t targetOffset,
    size_t requiredCaveBytes,
    size_t* outCaveOffset
);

bool InstallCodeCaveDetour32(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    size_t patchSize,
    void* pCodeCave,
    const uint8_t* payloadBytes,
    size_t payloadLen,
    const uint8_t* expectedPrologue = nullptr,
    size_t expectedPrologueLen = 0
);

// Notifies the controller that the calling thread is the main engine/window thread at a safe point
// and drains any queued toggle requests from background IPC threads.
void OnMainThreadSafePoint(HWND hGameWnd = NULL);

// Executes or queues a hook group toggle:
//   group:  "vfs" | "prewarm" | "seh" | "sleep" | "atob" | "all" | <siteName>
//   action: "on" | "off" | "status" | "toggle"
// Returns true if the operation succeeded (and writes JSON summary to outJson if provided).
bool ExecuteHookToggle(
    const char* group,
    const char* action,
    char* outJson,
    size_t outJsonCapacity,
    bool isOnMainThreadSafePoint = false
);

// Populates a JSON status report of all registered hook groups, sites, and 64 KiB RegionSnapshots
size_t GetStatusJson(char* outJson, size_t outJsonCapacity, const char* filterGroup = nullptr);

// Sets a simulated worker thread RIP for deterministic OBL-HOOK-QUIESCE hazard testing
void SetSimulatedThreadRip(uintptr_t simulatedRip);

// (#168) Resolves whether an instruction pointer lies inside a heap/VirtualAlloc trampoline
// allocated by RuntimePatchController, populating group/site names and target metadata.
bool TryLookupTrampolineSite(
    uintptr_t address,
    char* outGroup,
    size_t groupCap,
    char* outName,
    size_t nameCap,
    uintptr_t* outTrampolineBase = nullptr,
    uintptr_t* outTargetAddr = nullptr
);

// (#170) Safe-Mode state inspection and activation (ALT+SHIFT / KONGOR_SAFE_MODE=1 / last-crashed.sentinel)
bool IsSafeModeActive();
void SetSafeMode(bool active, const char* reason = nullptr);
const char* GetSafeModeReason();

// (#176) Lock-Free Double-Buffered Main-Thread Callback Queue
using MainThreadCallbackFn = void(*)(void* userData);
void EnqueueMainThreadCallback(MainThreadCallbackFn fn, void* userData = nullptr);
void EnqueueMainThreadTask(std::function<void()> task);
size_t DrainMainThreadCallbacks();

// (#178) Re-Entrant Priority Event Dispatcher with Deferred Add/Remove Queues
template <typename EventPayloadT>
class ReentrantPriorityEventDispatcher {
public:
    using ListenerId = uint64_t;
    using CallbackFn = std::function<bool(const EventPayloadT&)>;

    struct ListenerEntry {
        ListenerId id = 0;
        int priority = 0;
        CallbackFn callback;
    };

    ListenerId Subscribe(int priority, CallbackFn callback) {
        std::lock_guard<std::mutex> lock(m_mutex);
        ListenerId id = ++m_nextId;
        ListenerEntry entry{ id, priority, std::move(callback) };
        if (m_dispatchDepth > 0) {
            m_pendingAdd.push_back(std::move(entry));
        } else {
            InsertSortedLocked( std::move(entry) );
        }
        return id;
    }

    void Unsubscribe(ListenerId id) {
        std::lock_guard<std::mutex> lock(m_mutex);
        if (m_dispatchDepth > 0) {
            m_pendingRemove.push_back(id);
        } else {
            RemoveByIdLocked(id);
        }
    }

    size_t Dispatch(const EventPayloadT& payload) {
        std::vector<ListenerEntry> snapshot;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            ++m_dispatchDepth;
            snapshot = m_listeners;
        }

        size_t invoked = 0;
        for (const ListenerEntry& entry : snapshot) {
            if (IsPendingRemoval(entry.id)) {
                continue;
            }
            if (entry.callback) {
                ++invoked;
                bool stopPropagation = entry.callback(payload);
                if (stopPropagation) {
                    break;
                }
            }
        }

        {
            std::lock_guard<std::mutex> lock(m_mutex);
            if (m_dispatchDepth > 0) {
                --m_dispatchDepth;
            }
            if (m_dispatchDepth == 0) {
                FlushPendingMutationsLocked();
            }
        }
        return invoked;
    }

    size_t GetListenerCount() const {
        std::lock_guard<std::mutex> lock(m_mutex);
        return m_listeners.size();
    }

private:
    bool IsPendingRemoval(ListenerId id) const {
        std::lock_guard<std::mutex> lock(m_mutex);
        return std::find(m_pendingRemove.begin(), m_pendingRemove.end(), id) != m_pendingRemove.end();
    }

    void InsertSortedLocked(ListenerEntry&& entry) {
        auto pos = std::upper_bound(
            m_listeners.begin(),
            m_listeners.end(),
            entry.priority,
            [](int prio, const ListenerEntry& existing) {
                return prio < existing.priority; // lower integer priority runs earlier
            });
        m_listeners.insert(pos, std::move(entry));
    }

    void RemoveByIdLocked(ListenerId id) {
        m_listeners.erase(
            std::remove_if(
                m_listeners.begin(),
                m_listeners.end(),
                [id](const ListenerEntry& e) { return e.id == id; }),
            m_listeners.end());
    }

    void FlushPendingMutationsLocked() {
        for (ListenerId remId : m_pendingRemove) {
            RemoveByIdLocked(remId);
        }
        m_pendingRemove.clear();

        for (ListenerEntry& addEntry : m_pendingAdd) {
            InsertSortedLocked(std::move(addEntry));
        }
        m_pendingAdd.clear();
    }

    mutable std::mutex m_mutex;
    uint32_t m_dispatchDepth = 0;
    ListenerId m_nextId = 0;
    std::vector<ListenerEntry> m_listeners;
    std::vector<ListenerEntry> m_pendingAdd;
    std::vector<ListenerId> m_pendingRemove;
};

// ============================================================================
// #216 (Gem 3D): Out-of-Process Graphics VTable Offset Fingerprint Cache
// Inspired by obs-studio/plugins/win-capture/get-graphics-offsets/get-graphics-offsets.c
// and obs-studio/plugins/win-capture/graphics-hook-ver.h
// ============================================================================
#pragma pack(push, 1)
struct GraphicsModuleVersionFingerprint {
    uint32_t fileVersionMS    = 0;
    uint32_t fileVersionLS    = 0;
    uint32_t productVersionMS = 0;
    uint32_t productVersionLS = 0;

    constexpr bool Matches(const GraphicsModuleVersionFingerprint& other) const noexcept {
        return fileVersionMS == other.fileVersionMS &&
               fileVersionLS == other.fileVersionLS &&
               productVersionMS == other.productVersionMS &&
               productVersionLS == other.productVersionLS;
    }
};

struct GraphicsVtableOffsetCache {
    static constexpr uint32_t EXPECTED_MAGIC   = 0x4F425356U; // 'OBSV'
    static constexpr uint32_t EXPECTED_VERSION = 1U;

    uint32_t magic                                    = EXPECTED_MAGIC;   // 0x00 (4B)
    uint32_t version                                  = EXPECTED_VERSION; // 0x04 (4B)
    GraphicsModuleVersionFingerprint d3d9Fingerprint  = {};               // 0x08 (16B)
    GraphicsModuleVersionFingerprint dxgiFingerprint  = {};               // 0x18 (16B)
    GraphicsModuleVersionFingerprint d3d11Fingerprint = {};               // 0x28 (16B)
    uint32_t d3d9PresentOffset                        = 0;                // 0x38 (4B)
    uint32_t d3d9PresentExOffset                      = 0;                // 0x3C (4B)
    uint32_t d3d9SwapChainPresentOffset               = 0;                // 0x40 (4B)
    uint32_t d3d9ResetOffset                          = 0;                // 0x44 (4B)
    uint32_t dxgiPresentOffset                        = 0;                // 0x48 (4B)
    uint32_t dxgiResizeBuffersOffset                  = 0;                // 0x4C (4B)
    uint32_t isD3D9ExByteOffset                       = 0;                // 0x50 (4B)
    uint32_t checksumFnv1a32                          = 0;                // 0x54 (4B) -> Total 88B
};
#pragma pack(pop)

static_assert(sizeof(GraphicsModuleVersionFingerprint) == 16, "GraphicsModuleVersionFingerprint must be 16 bytes");
static_assert(sizeof(GraphicsVtableOffsetCache) == 88, "GraphicsVtableOffsetCache must have fixed 88-byte binary layout across C++ and C#");

uint32_t ComputeGraphicsVtableCacheChecksum(const GraphicsVtableOffsetCache& cache) noexcept;
bool ValidateGraphicsVtableOffsetCache(
    const GraphicsVtableOffsetCache& cache,
    const GraphicsModuleVersionFingerprint& expectedD3d9,
    const GraphicsModuleVersionFingerprint& expectedDxgi,
    const GraphicsModuleVersionFingerprint& expectedD3d11
) noexcept;

} // namespace PatchController
} // namespace Kongor
#endif

#ifdef __cplusplus
extern "C" {
#endif

__declspec(dllexport) int Kongor_NativeGuard_HookToggle(
    const char* groupName,
    const char* action,
    char* outJson,
    uint32_t outJsonCapacity
);

__declspec(dllexport) uint32_t Kongor_NativeGuard_GetHookStatusJson(
    char* outJson,
    uint32_t outJsonCapacity
);

__declspec(dllexport) int Kongor_NativeGuard_RegisterTestPatchSite(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    void* pHook,
    void** ppOrigTrampoline,
    uint32_t stolenBytes,
    const uint8_t* expectedPrologue,
    uint32_t expectedPrologueLen
);

__declspec(dllexport) void Kongor_NativeGuard_SetSimulatedThreadRip(
    uint64_t simulatedRip
);

__declspec(dllexport) int Kongor_NativeGuard_InstallCodeCaveDetour32(
    const char* groupName,
    const char* siteName,
    void* pTarget,
    uint32_t patchSize,
    void* pCodeCave,
    const uint8_t* payloadBytes,
    uint32_t payloadLen,
    const uint8_t* expectedPrologue,
    uint32_t expectedPrologueLen
);

__declspec(dllexport) int Kongor_NativeGuard_ValidateGraphicsVtableCache(
    const void* cacheBytes,
    uint32_t cacheByteCount
);

#ifdef __cplusplus
}
#endif
