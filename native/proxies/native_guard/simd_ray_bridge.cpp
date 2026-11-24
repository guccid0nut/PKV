// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - Deterministic Math, SIMD Ray Packet8 & StructPeek Bridges
// Extracted Translation Unit (Option C2)
// ============================================================================
#include "native_guard_internal.h"
#include "predictive_pathing.h"
#include "kongor_deterministic_math.h"
#include "kongor_fast_divmod.h"
#include "kongor_sstream.h"
#include <intrin.h>
#include <cmath>
#include <vector>

extern "C" {

    __declspec(dllexport) unsigned int Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism(void) {
        unsigned int oldMxcsr = _mm_getcsr();
        unsigned int newMxcsr = oldMxcsr;
        newMxcsr |= 0x8000;   // Flush-To-Zero (FTZ)
        newMxcsr |= 0x0040;   // Denormals-Are-Zero (DAZ)
        newMxcsr &= ~0x6000;  // Round-to-Nearest (RN)
        _mm_setcsr(newMxcsr);
        return newMxcsr;
    }

    __declspec(dllexport) unsigned int Kongor_NativeGuard_GetMxcsr(void) {
        return _mm_getcsr();
    }

    __declspec(dllexport) float Kongor_NativeGuard_FmaRoundToOdd_f32(float a, float b, float c) {
        return Kongor_FmaRoundToOdd_f32(a, b, c);
    }

    __declspec(dllexport) int Kongor_NativeGuard_VerifyFmaLockstepOracle(uint32_t* outFailedMask) {
        return Kongor_VerifyFmaRoundToOddGoldenVectors(outFailedMask);
    }

    __declspec(dllexport) int Kongor_NativeGuard_IsAvx2Supported(void) {
        return Kongor::PredictivePathing::IsAvx2Supported() ? 1 : 0;
    }

    static void PopulateRayPacket8Simd(
        Kongor::PredictivePathing::RayPacket8& packet,
        const float* originX, const float* originY,
        const float* dirX, const float* dirY,
        const float* maxDist)
    {
        for (int i = 0; i < 8; ++i) {
            packet.originX[i] = originX[i];
            packet.originY[i] = originY[i];
            packet.dirX[i] = dirX[i];
            packet.dirY[i] = dirY[i];
            packet.maxDist[i] = maxDist[i];
        }

        if (Kongor::PredictivePathing::IsAvx2Supported()) {
            const __m256 vSignMask = _mm256_set1_ps(-0.0f);
            const __m256 vEps = _mm256_set1_ps(1e-6f);
            const __m256 vOne = _mm256_set1_ps(1.0f);
            const __m256 vFallback = _mm256_set1_ps(1e6f);

            __m256 vDirX = _mm256_load_ps(packet.dirX);
            __m256 vAbsX = _mm256_andnot_ps(vSignMask, vDirX);
            __m256 vValidX = _mm256_cmp_ps(vAbsX, vEps, _CMP_GT_OQ);
            __m256 vSafeDirX = _mm256_blendv_ps(vOne, vDirX, vValidX);
            __m256 vInvX = _mm256_blendv_ps(vFallback, _mm256_div_ps(vOne, vSafeDirX), vValidX);
            _mm256_store_ps(packet.invDirX, vInvX);

            __m256 vDirY = _mm256_load_ps(packet.dirY);
            __m256 vAbsY = _mm256_andnot_ps(vSignMask, vDirY);
            __m256 vValidY = _mm256_cmp_ps(vAbsY, vEps, _CMP_GT_OQ);
            __m256 vSafeDirY = _mm256_blendv_ps(vOne, vDirY, vValidY);
            __m256 vInvY = _mm256_blendv_ps(vFallback, _mm256_div_ps(vOne, vSafeDirY), vValidY);
            _mm256_store_ps(packet.invDirY, vInvY);
        } else {
            for (int i = 0; i < 8; ++i) {
                packet.invDirX[i] = (fabsf(dirX[i]) > 1e-6f) ? (1.0f / dirX[i]) : 1e6f;
                packet.invDirY[i] = (fabsf(dirY[i]) > 1e-6f) ? (1.0f / dirY[i]) : 1e6f;
            }
        }
    }

    // shortcut: standalone AVX2/SSE2 8-ray packet AABB intersection export without direct K2 NavGrid integration, wire to CWorld::TraceRay / CPathFinder when server-side raymarch acceleration is required
    __declspec(dllexport) uint32_t Kongor_NativeGuard_SimdRayPacket8_IntersectAABB(
        const float* originX, const float* originY,
        const float* dirX, const float* dirY,
        const float* maxDist,
        float boxMinX, float boxMinY, float boxMaxX, float boxMaxY,
        float* outHitDistances)
    {
        if (!originX || !originY || !dirX || !dirY || !maxDist) return 0;

        Kongor::PredictivePathing::RayPacket8 packet;
        PopulateRayPacket8Simd(packet, originX, originY, dirX, dirY, maxDist);

        Kongor::PredictivePathing::AABB2D box{ boxMinX, boxMinY, boxMaxX, boxMaxY };
        Kongor::PredictivePathing::RayHitResult8 res = Kongor::PredictivePathing::IntersectRayPacket8_AABB(packet, box);

        if (outHitDistances) {
            for (int i = 0; i < 8; ++i) {
                outHitDistances[i] = res.hitDistance[i];
            }
        }

        return static_cast<uint32_t>(res.hitMask);
    }

    __declspec(dllexport) uint32_t Kongor_NativeGuard_SimdRayPacket8_BatchTrace(
        const float* originX, const float* originY,
        const float* dirX, const float* dirY,
        const float* maxDist,
        const float* boxMinX, const float* boxMinY,
        const float* boxMaxX, const float* boxMaxY,
        uint32_t boxCount,
        float* outNearestDistances)
    {
        if (!originX || !originY || !dirX || !dirY || !maxDist || !boxMinX || !boxMinY || !boxMaxX || !boxMaxY || boxCount == 0) return 0;

        Kongor::PredictivePathing::RayPacket8 packet;
        PopulateRayPacket8Simd(packet, originX, originY, dirX, dirY, maxDist);

        float nearest[8];
        for (int i = 0; i < 8; ++i) {
            nearest[i] = packet.maxDist[i];
        }

        // Process obstacles in stack-allocated 64-box L1-cache tiles (1 KiB, 0 heap allocations) (#132)
        constexpr uint32_t kTileBoxes = 64;
        alignas(32) Kongor::PredictivePathing::AABB2D tileBoxes[kTileBoxes];
        float tileNearest[8];
        uint8_t totalHitMask = 0;

        for (uint32_t tileStart = 0; tileStart < boxCount; tileStart += kTileBoxes) {
            uint32_t currentTileCount = (boxCount - tileStart < kTileBoxes) ? (boxCount - tileStart) : kTileBoxes;

            if (tileStart + kTileBoxes < boxCount) {
                _mm_prefetch(reinterpret_cast<const char*>(&boxMinX[tileStart + kTileBoxes]), _MM_HINT_T0);
                _mm_prefetch(reinterpret_cast<const char*>(&boxMinY[tileStart + kTileBoxes]), _MM_HINT_T0);
                _mm_prefetch(reinterpret_cast<const char*>(&boxMaxX[tileStart + kTileBoxes]), _MM_HINT_T0);
                _mm_prefetch(reinterpret_cast<const char*>(&boxMaxY[tileStart + kTileBoxes]), _MM_HINT_T0);
            }

            for (uint32_t b = 0; b < currentTileCount; ++b) {
                uint32_t srcIdx = tileStart + b;
                tileBoxes[b] = { boxMinX[srcIdx], boxMinY[srcIdx], boxMaxX[srcIdx], boxMaxY[srcIdx] };
            }

            uint8_t tileMask = Kongor::PredictivePathing::BatchTraceRayPacket8_Obstacles(
                packet,
                tileBoxes,
                currentTileCount,
                tileNearest);

            if (tileMask != 0) {
                for (int i = 0; i < 8; ++i) {
                    if ((tileMask & (1u << i)) != 0u && tileNearest[i] < nearest[i]) {
                        nearest[i] = tileNearest[i];
                        totalHitMask |= static_cast<uint8_t>(1u << i);
                    }
                }
            }
        }

        if (outNearestDistances) {
            for (int i = 0; i < 8; ++i) {
                outNearestDistances[i] = nearest[i];
            }
        }

        return static_cast<uint32_t>(totalHitMask);
    }

    __declspec(dllexport) int Kongor_NativeGuard_PeekStructJson(
        const char* structName,
        uintptr_t address,
        char* outJsonBuffer,
        uint32_t bufferSize)
    {
        return Kongor::StructPeek::FormatStructPeekJson(structName, address, outJsonBuffer, bufferSize);
    }

    __declspec(dllexport) int Kongor_NativeGuard_ValidateWidgetPointer(uintptr_t address) {
        return Kongor::WidgetSafety::IsWidgetPointerReadableAndValid(reinterpret_cast<void*>(address)) ? 1 : 0;
    }

}
