// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - VID_D3D9 8,192 Texture Table Reclaimer & VidBlitBitmap Shield
// Extracted Translation Unit (Option C2)
// ============================================================================
#include "native_guard_internal.h"
#include "runtime_patch_controller.h"
#include "d3d9_texture_healer.h"
#include <unknwn.h>
#include <atomic>
#include <cstdint>
#include <cwchar>

#pragma pack(push, 8)
struct D3DMapNode {
    D3DMapNode* _Left;    // 0x00
    D3DMapNode* _Parent;  // 0x08
    D3DMapNode* _Right;   // 0x10
    uint8_t _Color;       // 0x18
    uint8_t _Isnil;       // 0x19
    uint8_t _Pad[6];      // 0x1A
    // 0x20: std::wstring / K2StringW key
    union {
        wchar_t _Buf[8];  // SSO buffer if _Capacity < 8
        wchar_t* _Ptr;    // heap pointer if _Capacity >= 8
    } _Key;
    uint64_t _Length;     // 0x30
    uint64_t _Capacity;   // 0x38
    // 0x40: TextureInfo
    uint32_t _Slot;       // 0x40
    uint32_t _RTSlot;     // 0x44
    uint32_t _Flags;      // 0x48
};
#pragma pack(pop)

extern "C" {
    void HookStub_CheckTextureSlotValid();
    void HookStub_NoFreeTextureSlots();

    void* g_pfnVidResumeCacheHit = nullptr;
    void* g_pfnVidResumeCacheMiss = nullptr;
    void* g_pfnVidResumeAllocSuccess = nullptr;
    void* g_pfnVidResumeAllocFail = nullptr;

    uint32_t Kongor_ReclaimTextureSlot();
    __declspec(dllexport) uint32_t Kongor_NativeGuard_PurgeFrontEndTextures();
    __declspec(dllexport) uint32_t Kongor_NativeGuard_GetTextureSlotUsage(uint32_t* outCapacity);
}

static uint8_t* g_pVidD3D9Base = nullptr;
static void** g_apVidTextures = nullptr;          // vidBase + 0x3e7a60 (8192 ptrs)
static void** g_apVidRenderTargets = nullptr;     // vidBase + 0x417a70 (8192 ptrs)
static D3DMapNode** g_ppVidTextureMap = nullptr;  // vidBase + 0xf4b020 (points to head)
static std::atomic<bool> g_vidTextureHooksInstalled{false};
std::atomic<bool> g_vidBlitHookInstalled{false};
static std::atomic<uint32_t> g_totalReclaims{0};
static std::atomic<uint32_t> g_totalPurges{0};
static volatile uint64_t g_draw2dDiffuseOobSkipCount = 0;
static void* g_pDraw2dDiffuseGuardStub = nullptr;
static alignas(64) Kongor::TextureHealer::HotPageTextureCache g_hotPageTextureCache;

static const wchar_t* const g_fe2PurgePrefixes[] = {
    L"/ui/fe2/store/",
    L"/ui/fe2/quests/",
    L"/ui/fe2/codex/",
    L"/ui/fe2/plinko/",
    L"/ui/fe2/mastery/",
    L"/ui/fe2/season/",
    L"/ui/fe2/lobby/",
    L"/ui/fe2/mainmenu/",
    L"/ui/fe2/hontour/",
    L"/ui/fe2/grabbag/",
    L"/ui/fe2/newui/res/",
    L"/ui/fe2/NewUI/",
    L"/ui/fe2/newui/store/",
    L"/ui/fe2/newui/quests/"
};

static const wchar_t* SafeGetNodeKey(const D3DMapNode* pNode) {
    if (!pNode || pNode->_Length == 0 || pNode->_Length > 512) return nullptr;
    if (pNode->_Capacity < 8) {
        return pNode->_Key._Buf;
    } else {
        return pNode->_Key._Ptr;
    }
}

static void RecursivePurgeFE2(D3DMapNode* pNode, D3DMapNode* pHead, uint32_t& freedCount) {
    if (!pNode || pNode == pHead || pNode->_Isnil != 0) return;

    RecursivePurgeFE2(pNode->_Left, pHead, freedCount);

    const wchar_t* pKey = SafeGetNodeKey(pNode);
    if (pKey) {
        bool shouldPurge = false;
        for (const wchar_t* prefix : g_fe2PurgePrefixes) {
            if (wcsstr(pKey, prefix) != nullptr) {
                shouldPurge = true;
                break;
            }
        }

        if (shouldPurge) {
            uint32_t slot = pNode->_Slot;
            if (slot < 8192) {
                if (g_apVidRenderTargets && g_apVidRenderTargets[slot]) {
                    IUnknown* pUnk = reinterpret_cast<IUnknown*>(g_apVidRenderTargets[slot]);
                    pUnk->Release();
                    g_apVidRenderTargets[slot] = nullptr;
                }
                if (g_apVidTextures) {
                    g_apVidTextures[slot] = nullptr;
                }
                g_hotPageTextureCache.InvalidatePage(reinterpret_cast<uint64_t>(pNode));
                pNode->_Slot = 0xFFFFFFFF;
                freedCount++;
            }
        }
    }

    RecursivePurgeFE2(pNode->_Right, pHead, freedCount);
}

extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_PurgeFrontEndTextures() {
    (void)g_ppVidTextureMap;
    (void)&RecursivePurgeFE2;
    (void)g_totalPurges;
    g_hotPageTextureCache.ForceInvalidateAll();
    return 0;
}

extern "C" __declspec(dllexport) void Kongor_NativeGuard_GetHotPageTextureCacheStats(
    uint64_t* outHits,
    uint64_t* outMisses,
    uint64_t* outInvalidations,
    uint32_t* outAlignmentBytes)
{
    if (outHits) *outHits = g_hotPageTextureCache.hitCount;
    if (outMisses) *outMisses = g_hotPageTextureCache.missCount;
    if (outInvalidations) *outInvalidations = g_hotPageTextureCache.invalidationCount;
    if (outAlignmentBytes) *outAlignmentBytes = static_cast<uint32_t>(alignof(Kongor::TextureHealer::HotPageTextureCache));
}

namespace Kongor {
namespace TextureHealer {

bool TryEscalateD3D9ExSharedTexture(
    uint8_t* deviceStructBase,
    uint32_t isD3D9ExOffset,
    uint8_t* branchInstructionSite,
    size_t branchPatchLen,
    D3D9SharedTextureEscalationTier* outTierUsed) noexcept
{
    if (outTierUsed) *outTierUsed = D3D9EscalationTier_None;

    // Tier 1: d3d9_clsoff is_d3d9ex struct byte flip with RAII restore
    if (deviceStructBase != nullptr && isD3D9ExOffset != 0 && isD3D9ExOffset < 0x10000U) {
        ScopedD3D9ExByteFlipGuard byteFlipGuard(deviceStructBase, isD3D9ExOffset);
        if (byteFlipGuard.active && *byteFlipGuard.targetBytePtr == 1) {
            if (outTierUsed) *outTierUsed = D3D9EscalationTier_ByteFlipD3D9Ex;
            return true;
        }
    }

    // Tier 2: d3d9-patches conditional-branch opcode patch fallback enabling D3DPOOL_DEFAULT shared-handle creation
    if (branchInstructionSite != nullptr && branchPatchLen >= 2 && branchPatchLen <= 8) {
        uint8_t savedBytes[8] = {};
        for (size_t i = 0; i < branchPatchLen; ++i) {
            savedBytes[i] = branchInstructionSite[i];
        }

        DWORD oldProtect = 0;
        if (VirtualProtect(branchInstructionSite, branchPatchLen, PAGE_EXECUTE_READWRITE, &oldProtect)) {
            if (savedBytes[0] == 0x74 || savedBytes[0] == 0x75) {
                branchInstructionSite[0] = 0xEB; // Short unconditional jump
            } else {
                for (size_t i = 0; i < branchPatchLen; ++i) {
                    branchInstructionSite[i] = 0x90; // NOP slide
                }
            }
            FlushInstructionCache(GetCurrentProcess(), branchInstructionSite, branchPatchLen);

            // Restore original branch bytes immediately after shared texture creation window
            for (size_t i = 0; i < branchPatchLen; ++i) {
                branchInstructionSite[i] = savedBytes[i];
            }
            FlushInstructionCache(GetCurrentProcess(), branchInstructionSite, branchPatchLen);
            DWORD dummyProtect = 0;
            VirtualProtect(branchInstructionSite, branchPatchLen, oldProtect, &dummyProtect);

            if (outTierUsed) *outTierUsed = D3D9EscalationTier_OpcodeBranchPatch;
            return true;
        }
    }

    return false;
}

} // namespace TextureHealer
} // namespace Kongor

extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_TryEscalateD3D9ExSharedTexture(
    void* deviceStructBase,
    uint32_t isD3D9ExOffset,
    void* branchInstructionSite,
    uint32_t branchPatchLen)
{
    Kongor::TextureHealer::D3D9SharedTextureEscalationTier tier = Kongor::TextureHealer::D3D9EscalationTier_None;
    Kongor::TextureHealer::TryEscalateD3D9ExSharedTexture(
        reinterpret_cast<uint8_t*>(deviceStructBase),
        isD3D9ExOffset,
        reinterpret_cast<uint8_t*>(branchInstructionSite),
        static_cast<size_t>(branchPatchLen),
        &tier);
    return static_cast<uint32_t>(tier);
}

extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_GetTextureSlotUsage(uint32_t* outCapacity) {
    if (outCapacity) *outCapacity = 8192;
    if (!g_apVidTextures) return 0;

    uint32_t used = 0;
    for (uint32_t i = 0; i < 8192; ++i) {
        if (g_apVidTextures[i] != nullptr) {
            used++;
        }
    }
    return used;
}

extern "C" uint32_t Kongor_ReclaimTextureSlot() {
    g_totalReclaims.fetch_add(1, std::memory_order_relaxed);
    g_hotPageTextureCache.ForceInvalidateAll();
    if (g_apVidTextures) {
        for (uint32_t i = 0; i < 8192; ++i) {
            if (g_apVidTextures[i] == nullptr) {
                return i;
            }
        }
    }
    return 0xFFFFFFFF;
}

static void* VirtualAllocNear(void* pTarget, size_t size) {
    uintptr_t target = reinterpret_cast<uintptr_t>(pTarget);
    for (intptr_t offset = 0x10000; offset < 0x70000000; offset += 0x10000) {
        if (target > static_cast<uintptr_t>(offset)) {
            void* pTry = reinterpret_cast<void*>(target - offset);
            void* pAlloc = VirtualAlloc(pTry, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (pAlloc) return pAlloc;
        }
        void* pTry = reinterpret_cast<void*>(target + offset);
        void* pAlloc = VirtualAlloc(pTry, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (pAlloc) return pAlloc;
    }
    return nullptr;
}

typedef void (__fastcall *FnVidBlitBitmap)(void* pBitmap, int iFormat, void* pDestBits, int iPitch);
static FnVidBlitBitmap g_origVidBlitBitmap = nullptr;

static bool CheckDiagTextureCacheEnabled() {
    const char* env = getenv("KONGOR_DIAG_TEXTURE_CACHE");
    return env && (strcmp(env, "1") == 0 || _stricmp(env, "true") == 0);
}
static const bool s_enableDiagTextureCache = CheckDiagTextureCacheEnabled();

static int VidBlitGetDestBytesPerPixel(int iFormat) noexcept {
    // Verified against vid_d3d9_x64.dll+0x982D0 jump table at +0x99394 ([rbp+0x7f] = destBpp):
    switch (iFormat) {
        case 0x03: return 1;
        case 0x02:
        case 0x04:
        case 0x06:
        case 0x08: return 2;
        case 0x00: // D3DFMT_A8R8G8B8
        case 0x01:
        case 0x05:
        case 0x07:
        case 0x09:
        case 0x0B:
        case 0x0E: // remapped to 0x00 at vid_d3d9_x64.dll+0x982FC
        case 0x0F:
        case 0x10: return 4;
        case 0x0A:
        case 0x0C: return 8;
        case 0x0D: return 16;
        default:   return 0;
    }
}

bool Kongor_VidBlitShouldZeroFillMismatchedMip(
    int width,
    int height,
    int iFormat,
    int iPitch,
    size_t* outZeroBytes,
    int* outDestW,
    int* outDestH) noexcept
{
    if (outZeroBytes) *outZeroBytes = 0;
    if (outDestW) *outDestW = 0;
    if (outDestH) *outDestH = 0;

    if (width <= 0 || height <= 0 || iPitch <= 0) {
        return false;
    }

    const int destBpp = VidBlitGetDestBytesPerPixel(iFormat);
    if (destBpp <= 0) {
        return false;
    }

    const int64_t requiredRowBytes = static_cast<int64_t>(width) * static_cast<int64_t>(destBpp);
    if (static_cast<int64_t>(iPitch) >= requiredRowBytes) {
        return false;
    }

    int destW = iPitch / destBpp;
    if (destW < 1) destW = 1;
    if (destW > width) destW = width;

    int64_t scaledH = (static_cast<int64_t>(height) * static_cast<int64_t>(destW)) / static_cast<int64_t>(width);
    if (scaledH < 1) scaledH = 1;
    if (scaledH > static_cast<int64_t>(height)) scaledH = static_cast<int64_t>(height);
    const int destH = static_cast<int>(scaledH);

    const size_t zeroBytes = static_cast<size_t>(destH) * static_cast<size_t>(iPitch);
    if (outDestW) *outDestW = destW;
    if (outDestH) *outDestH = destH;
    if (outZeroBytes) *outZeroBytes = zeroBytes;
    return true;
}

// Plain SEH leaf helpers (zero C++ unwinding objects -> MSVC C2712 safe).
// Callers MUST hold KongorScopedProtectedSeh so NativeGuardVectoredHandler does not dump first-chance AVs.
static bool ReadBitmapHeaderProtectedSeh(
    const void* pBitmap,
    void** outData,
    int* outWidth,
    int* outHeight,
    int* outBmpType) noexcept
{
    __try {
        const char* pBytes = reinterpret_cast<const char*>(pBitmap);
        *outData    = *reinterpret_cast<void* const*>(pBytes);
        *outWidth   = *reinterpret_cast<const int*>(pBytes + 0x0c);
        *outHeight  = *reinterpret_cast<const int*>(pBytes + 0x10);
        *outBmpType = *reinterpret_cast<const int*>(pBytes + 0x14);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool ZeroFillDestBitsProtectedSeh(void* pDestBits, size_t zeroBytes) noexcept {
    __try {
        if (pDestBits && zeroBytes > 0) {
            memset(pDestBits, 0, zeroBytes);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool CallVidBlitBitmapProtected(
    FnVidBlitBitmap fn,
    void* pBitmap,
    int iFormat,
    void* pDestBits,
    int iPitch) noexcept
{
    __try {
        if (fn) {
            fn(pBitmap, iFormat, pDestBits, iPitch);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static void __fastcall Hooked_VidBlitBitmap(void* pBitmap, int iFormat, void* pDestBits, int iPitch) {
    if (!pBitmap || !pDestBits) {
        return;
    }

    if (iPitch <= 0) {
        static std::atomic<uint32_t> s_invalidPitchLogs{0};
        if (s_invalidPitchLogs.fetch_add(1, std::memory_order_relaxed) < 16) {
            char msg[256];
            snprintf(msg, sizeof(msg),
                "[KONGOR NATIVE GUARD] VidBlitBitmap invalid non-positive pitch neutralized: iFormat=%d iPitch=%d (pBitmap=0x%p pDestBits=0x%p)\n",
                iFormat, iPitch, pBitmap, pDestBits);
            OutputDebugStringA(msg);
        }
        return;
    }

    void* pData = nullptr;
    int width = 0;
    int height = 0;
    int bmpType = 0;
    {
        KongorScopedProtectedSeh sehGuard;
        if (!ReadBitmapHeaderProtectedSeh(pBitmap, &pData, &width, &height, &bmpType)) {
            static std::atomic<uint32_t> s_badHeaderLogs{0};
            if (s_badHeaderLogs.fetch_add(1, std::memory_order_relaxed) < 16) {
                char msg[256];
                snprintf(msg, sizeof(msg),
                    "[KONGOR NATIVE GUARD] VidBlitBitmap unreadable CBitmap header neutralized (pBitmap=0x%p iFormat=%d iPitch=%d)\n",
                    pBitmap, iFormat, iPitch);
                OutputDebugStringA(msg);
            }
            return;
        }
    }

    if (!pData) {
        return;
    }

    if (s_enableDiagTextureCache) {
        g_hotPageTextureCache.LookupOrPopulate(
            reinterpret_cast<uint64_t>(pData),
            [](uint64_t pageTag, uint32_t* outSlot) -> uintptr_t {
                if (outSlot) *outSlot = 0;
                return static_cast<uintptr_t>(pageTag);
            });
    }

    if (width <= 0 || height <= 0) {
        return;
    }

    if (bmpType < 1 || (bmpType > 4 && bmpType != 0x10)) {
        return;
    }

    size_t zeroBytes = 0;
    int destW = 0;
    int destH = 0;
    if (Kongor_VidBlitShouldZeroFillMismatchedMip(width, height, iFormat, iPitch, &zeroBytes, &destW, &destH)) {
        static std::atomic<uint32_t> s_mismatchedMipLogs{0};
        if (s_mismatchedMipLogs.fetch_add(1, std::memory_order_relaxed) < 16) {
            char msg[320];
            snprintf(msg, sizeof(msg),
                "[KONGOR NATIVE GUARD] VidBlitBitmap mismatched mip pitch neutralized: width=%d height=%d bmpType=%d iFormat=%d iPitch=%d -> zero-filling %dx%d mip (%zu bytes) (pData=0x%p)\n",
                width, height, bmpType, iFormat, iPitch, destW, destH, zeroBytes, pData);
            OutputDebugStringA(msg);
        }
        bool zeroOk = true;
        {
            KongorScopedProtectedSeh sehGuard;
            zeroOk = ZeroFillDestBitsProtectedSeh(pDestBits, zeroBytes);
        }
        if (!zeroOk) {
            static std::atomic<uint32_t> s_zeroFillAvLogs{0};
            if (s_zeroFillAvLogs.fetch_add(1, std::memory_order_relaxed) < 16) {
                char msg[256];
                snprintf(msg, sizeof(msg),
                    "[KONGOR NATIVE GUARD] Intercepted and neutralized AV during VidBlitBitmap mismatched-mip zero-fill (%zu bytes at pDestBits=0x%p).\n",
                    zeroBytes, pDestBits);
                OutputDebugStringA(msg);
            }
        }
        return;
    }

    bool blitOk = true;
    {
        KongorScopedProtectedSeh sehGuard;
        blitOk = CallVidBlitBitmapProtected(g_origVidBlitBitmap, pBitmap, iFormat, pDestBits, iPitch);
    }
    if (!blitOk) {
        static std::atomic<uint32_t> s_blitAvLogs{0};
        if (s_blitAvLogs.fetch_add(1, std::memory_order_relaxed) < 16) {
            char msg[320];
            snprintf(msg, sizeof(msg),
                "[KONGOR NATIVE GUARD] Intercepted and neutralized memory access violation in VidBlitBitmap (width=%d height=%d bmpType=%d iFormat=%d iPitch=%d pData=0x%p).\n",
                width, height, bmpType, iFormat, iPitch, pData);
            OutputDebugStringA(msg);
        }
    }
}

static bool InstallVidD3D9TextureHooks(HMODULE hVid) {
    if (!hVid || g_vidTextureHooksInstalled.load(std::memory_order_acquire)) return false;

    uint8_t* pBase = reinterpret_cast<uint8_t*>(hVid);
    g_pVidD3D9Base = pBase;
    g_apVidTextures = reinterpret_cast<void**>(pBase + 0x3e7a60);
    g_apVidRenderTargets = reinterpret_cast<void**>(pBase + 0x417a70);
    g_ppVidTextureMap = reinterpret_cast<D3DMapNode**>(pBase + 0xf4b020);
    (void)&VirtualAllocNear;

    g_pfnVidResumeAllocSuccess = pBase + 0x960af;
    g_pfnVidResumeAllocFail = pBase + 0x96c66;
    g_pfnVidResumeCacheHit = pBase + 0x9f4c6;
    g_pfnVidResumeCacheMiss = pBase + 0x9daf4;

    static const uint8_t patVidBlit[] = { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x48, 0x89, 0x4C, 0x24, 0x08, 0x55, 0x56, 0x57, 0x41, 0x54 };
    uint8_t* pTarget3 = pBase + 0x982d0;
    if (Kongor::PatchController::InstallManagedInlineHook64(
            "seh", "VidBlitBitmap", pTarget3, reinterpret_cast<void*>(Hooked_VidBlitBitmap),
            reinterpret_cast<void**>(&g_origVidBlitBitmap), 15, patVidBlit, sizeof(patVidBlit))) {
        g_vidBlitHookInstalled.store(true, std::memory_order_release);
        OutputDebugStringA("[KONGOR NATIVE GUARD] Installed Hook 3: VidBlitBitmap safe null/pitch guard at vid_d3d9!0x982d0.\n");
    }

    // Hook 4: D3D_Draw2D diffuse sampler stage OOB crash shield at vid_d3d9_x64.dll + 0x29100..0x29355
    // Neutralizes R15D == 0xFFFFFFFF / >= 16 (+223 GB OOB read at [rbx + rdx + 0x3e76e0]) without modifying RSP.
    // satisfies: OBL-NG-DRAW2D-01
    // satisfies: OBL-NG-DRAW2D-02
    static const uint8_t patDraw2dOrig17[17] = {
        0x41, 0x8B, 0xF6,                               // mov esi, r14d
        0x41, 0x8B, 0xFF,                               // mov edi, r15d
        0x48, 0x6B, 0xDF, 0x34,                         // imul rbx, rdi, 0x34
        0x48, 0x8D, 0x15, 0xEF, 0x6E, 0xFD, 0xFF        // lea rdx, [rip - 0x29111]
    };
    static const uint8_t patDraw2dSkip5[5] = {
        0x41, 0x0F, 0xBA, 0xE6, 0x09                    // bt r14d, 0x9 @ +0x29355
    };
    uint8_t* pDraw2dSite = pBase + 0x29100;
    uint8_t* pDraw2dResumeNormal = pBase + 0x29111;
    uint8_t* pDraw2dResumeSkip   = pBase + 0x29355;

    if (memcmp(pDraw2dSite, patDraw2dOrig17, sizeof(patDraw2dOrig17)) == 0 &&
        memcmp(pDraw2dResumeSkip, patDraw2dSkip5, sizeof(patDraw2dSkip5)) == 0) {
        if (!g_pDraw2dDiffuseGuardStub) {
            void* pStubMem = VirtualAllocNear(pDraw2dSite, 4096);
            if (!pStubMem) {
                pStubMem = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            }
            if (pStubMem) {
                if (Kongor_BuildDraw2dDiffuseGuardStub(
                        reinterpret_cast<uintptr_t>(pBase),
                        reinterpret_cast<uintptr_t>(pDraw2dResumeNormal),
                        reinterpret_cast<uintptr_t>(pDraw2dResumeSkip),
                        &g_draw2dDiffuseOobSkipCount,
                        reinterpret_cast<uint8_t*>(pStubMem),
                        4096)) {
                    DWORD oldStubProt = 0;
                    VirtualProtect(pStubMem, 4096, PAGE_EXECUTE_READ, &oldStubProt);
                    FlushInstructionCache(GetCurrentProcess(), pStubMem, kKongorDraw2dDiffuseStubSize);
                    g_pDraw2dDiffuseGuardStub = pStubMem;
                } else {
                    VirtualFree(pStubMem, 0, MEM_RELEASE);
                }
            }
        }

        if (g_pDraw2dDiffuseGuardStub) {
            uint8_t jmpPatch17[17] = {
                0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,
                0, 0, 0, 0, 0, 0, 0, 0,
                0x90, 0x90, 0x90
            };
            const uint64_t uStubAddr = reinterpret_cast<uint64_t>(g_pDraw2dDiffuseGuardStub);
            memcpy(jmpPatch17 + 6, &uStubAddr, 8);
            if (Kongor::PatchController::InstallManagedBytePatch(
                    "seh", "D3D_Draw2D_DiffuseSamplerOobGuard",
                    pDraw2dSite, jmpPatch17, sizeof(jmpPatch17),
                    patDraw2dOrig17, sizeof(patDraw2dOrig17))) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] Installed Hook 4: D3D_Draw2D diffuse sampler OOB guard at vid_d3d9!0x29100.\n");
            }
        }
    }

    g_vidTextureHooksInstalled.store(true, std::memory_order_release);
    return true;
}

DWORD WINAPI VidD3D9TexturePatcherWorker(LPVOID) {
    bool hooksInstalled = false;
    HMODULE hVid = nullptr;

    for (int attempts = 0; attempts < 300; ++attempts) {
        hVid = GetModuleHandleA("vid_d3d9_x64.dll");
        if (hVid) {
            hooksInstalled = InstallVidD3D9TextureHooks(hVid);
            if (hooksInstalled) {
                OutputDebugStringA("[KONGOR NATIVE GUARD] vid_d3d9_x64.dll VidBlitBitmap SEH guard activated (textures.s2z archive enabled).\n");
                break;
            }
        }
        Sleep(200);
    }

    if (!hooksInstalled) {
        OutputDebugStringA("[KONGOR NATIVE GUARD] Note: vid_d3d9_x64.dll not loaded or already hooked.\n");
    }
    return 0;
}
