// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
// ============================================================================
// Project Kongor - 64-Byte Cache-Line Aligned Hot-Page Texture & Pointer Lookup
// Inspired by PS2Recomp/ps2xRuntime/include/ps2_runtime.h:158-219 (HotPageCache)
// ============================================================================
#include <cstdint>
#include <cstddef>

namespace Kongor {
namespace TextureHealer {

static constexpr uint64_t HOT_PAGE_MASK_8KB = ~0x1FFFULL; // 8 KiB hot-page granularity
static constexpr uint64_t INVALID_PAGE_TAG  = 0xFFFFFFFFFFFFFFFFULL;

// (#208) Single 64-byte L1 cache-line aligned hot-page lookup struct for D3D9 texture slot & pointer lookups
struct alignas(64) HotPageTextureCache {
    uint64_t lastPageTag       = INVALID_PAGE_TAG; // 0x00: 8KB page base (addr & ~0x1FFFULL)
    uintptr_t cachedHostPtr    = 0;                // 0x08: Resolved host texture/surface base pointer
    uint64_t hitCount          = 0;                // 0x10: Fast-path L1 page hits
    uint64_t missCount         = 0;                // 0x18: Slow-path tree/table walks
    uint64_t invalidationCount = 0;                // 0x20: Page invalidations on texture purge/reclaim
    uint32_t cachedSlotIndex   = 0xFFFFFFFFU;      // 0x28: Cached texture slot index (0..8191)
    uint32_t cachedFlags       = 0;                // 0x2C: Cached texture descriptor flags
    uint64_t reserved0         = 0;                // 0x30: Pad to 64-byte cache line
    uint64_t reserved1         = 0;                // 0x38: Pad to 64-byte cache line

    inline void Reset() noexcept {
        lastPageTag       = INVALID_PAGE_TAG;
        cachedHostPtr    = 0;
        hitCount          = 0;
        missCount         = 0;
        invalidationCount = 0;
        cachedSlotIndex   = 0xFFFFFFFFU;
        cachedFlags       = 0;
        reserved0         = 0;
        reserved1         = 0;
    }

    template <typename SlowResolverFn>
    inline uintptr_t LookupOrPopulate(
        uint64_t virtualAddress,
        SlowResolverFn&& slowResolver,
        uint32_t* outSlotIndex = nullptr,
        bool* outWasHit = nullptr) noexcept
    {
        const uint64_t pageTag = virtualAddress & HOT_PAGE_MASK_8KB;
        const uint64_t pageOffset = virtualAddress & ~HOT_PAGE_MASK_8KB;

        if (pageTag == lastPageTag && cachedHostPtr != 0) {
            ++hitCount;
            if (outSlotIndex) *outSlotIndex = cachedSlotIndex;
            if (outWasHit) *outWasHit = true;
            return cachedHostPtr + static_cast<uintptr_t>(pageOffset);
        }

        ++missCount;
        if (outWasHit) *outWasHit = false;
        uint32_t resolvedSlot = 0xFFFFFFFFU;
        uintptr_t resolvedBase = slowResolver(pageTag, &resolvedSlot);
        lastPageTag     = pageTag;
        cachedHostPtr   = resolvedBase;
        cachedSlotIndex = resolvedSlot;
        if (outSlotIndex) *outSlotIndex = resolvedSlot;
        return (resolvedBase != 0) ? (resolvedBase + static_cast<uintptr_t>(pageOffset)) : 0;
    }

    inline bool InvalidatePage(uint64_t pageBaseAddress) noexcept {
        const uint64_t pageTag = pageBaseAddress & HOT_PAGE_MASK_8KB;
        if (lastPageTag == pageTag) {
            lastPageTag     = INVALID_PAGE_TAG;
            cachedHostPtr   = 0;
            cachedSlotIndex = 0xFFFFFFFFU;
            ++invalidationCount;
            return true;
        }
        return false;
    }

    inline void ForceInvalidateAll() noexcept {
        lastPageTag     = INVALID_PAGE_TAG;
        cachedHostPtr   = 0;
        cachedSlotIndex = 0xFFFFFFFFU;
        ++invalidationCount;
    }
};

static_assert(alignof(HotPageTextureCache) == 64, "HotPageTextureCache must be 64-byte cache-line aligned");
static_assert(sizeof(HotPageTextureCache) == 64, "HotPageTextureCache must occupy exactly one 64-byte cache line");

// ============================================================================
// #215 (Gem 3C): 2-Tier D3D9 Shared-Handle Escalation
// Inspired by obs-studio/plugins/win-capture/graphics-hook/d3d9-capture.cpp:250-355
// and obs-studio/plugins/win-capture/graphics-hook/d3d9-patches.hpp:1-130
// ============================================================================
enum D3D9SharedTextureEscalationTier : uint32_t {
    D3D9EscalationTier_None              = 0,
    D3D9EscalationTier_ByteFlipD3D9Ex    = 1, // Tier 1: d3d9_clsoff is_d3d9ex struct byte flip with RAII restore
    D3D9EscalationTier_OpcodeBranchPatch = 2  // Tier 2: d3d9-patches conditional-branch opcode patch fallback
};

struct ScopedD3D9ExByteFlipGuard {
    uint8_t* targetBytePtr = nullptr;
    uint8_t savedByteValue = 0;
    bool active            = false;

    ScopedD3D9ExByteFlipGuard(uint8_t* baseDevicePtr, uint32_t isD3D9ExOffset) noexcept {
        if (baseDevicePtr != nullptr && isD3D9ExOffset != 0 && isD3D9ExOffset < 0x10000U) {
            targetBytePtr  = baseDevicePtr + isD3D9ExOffset;
            savedByteValue = *targetBytePtr;
            *targetBytePtr = 1;
            active         = true;
        }
    }

    ~ScopedD3D9ExByteFlipGuard() noexcept {
        if (active && targetBytePtr != nullptr) {
            *targetBytePtr = savedByteValue;
            active = false;
        }
    }
};

bool TryEscalateD3D9ExSharedTexture(
    uint8_t* deviceStructBase,
    uint32_t isD3D9ExOffset,
    uint8_t* branchInstructionSite,
    size_t branchPatchLen,
    D3D9SharedTextureEscalationTier* outTierUsed) noexcept;

} // namespace TextureHealer
} // namespace Kongor

#ifdef __cplusplus
extern "C" {
#endif

__declspec(dllexport) void Kongor_NativeGuard_GetHotPageTextureCacheStats(
    uint64_t* outHits,
    uint64_t* outMisses,
    uint64_t* outInvalidations,
    uint32_t* outAlignmentBytes
);

__declspec(dllexport) uint32_t Kongor_NativeGuard_TryEscalateD3D9ExSharedTexture(
    void* deviceStructBase,
    uint32_t isD3D9ExOffset,
    void* branchInstructionSite,
    uint32_t branchPatchLen
);

#ifdef __cplusplus
}
#endif
