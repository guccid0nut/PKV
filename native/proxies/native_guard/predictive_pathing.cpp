// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "predictive_pathing.h"
#include "kongor_deterministic_math.h"
#include <windows.h>
#include <cmath>
#include <cstdint>
#include <atomic>
#include <fstream>
#include <chrono>
#include <algorithm>
#include <immintrin.h>
#include <intrin.h>

namespace Kongor {

void KongorWriteWillowmakerLog(const char* tag, const char* message);

namespace PredictivePathing {

// shortcut: client-side dead reckoning stub not hooked into K2 cgame movement dispatch, wire into cgame_x64.dll entity controller when client-side prediction is enabled

static const float PI = 3.14159265358979323846f;
static const float TWO_PI = 6.28318530717958647692f;

static inline float NormalizeAngle(float angle) {
    while (angle > PI) angle -= TWO_PI;
    while (angle < -PI) angle += TWO_PI;
    return angle;
}

static SRWLOCK g_stateLock = SRWLOCK_INIT;
static PredictedHeroState g_state = { {0,0,0}, {0,0,0}, {0,0,0}, 0.0f, 0.0f, 0.0f, 300.0f, 6.28318f, 0, 0, false, false, false };

static std::atomic<bool>     g_enabled{ true };
static std::atomic<bool>     g_turnOnly{ false };
static std::atomic<float>    g_maxLatencyMs{ 150.0f };

static std::atomic<uint64_t> g_totalMoveCommands{ 0 };
static std::atomic<uint64_t> g_totalReconciliations{ 0 };
static std::atomic<uint64_t> g_hardCorrections{ 0 };
static std::atomic<double>   g_sumErrorDistance{ 0.0 };
static std::atomic<float>    g_maxLatencyCompensatedMs{ 0.0f };

static void LogPredictivePathing(const char* format, ...) {
    char buffer[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);

    OutputDebugStringA(buffer);

    static const bool s_enablePathingLogs = []() {
        const char* env = getenv("KONGOR_PREDICTIVE_PATHING_LOGS");
        return env && (strcmp(env, "1") == 0 || _stricmp(env, "true") == 0);
    }();

    if (s_enablePathingLogs) {
        KongorWriteWillowmakerLog("PREDICTIVE_PATHING", buffer);
    }
}

void Initialize() {
    AcquireSRWLockExclusive(&g_stateLock);
    memset(&g_state, 0, sizeof(g_state));
    g_state.moveSpeed = 300.0f;
    g_state.turnRate = 6.28318f;
    ReleaseSRWLockExclusive(&g_stateLock);

    g_totalMoveCommands.store(0, std::memory_order_relaxed);
    g_totalReconciliations.store(0, std::memory_order_relaxed);
    g_hardCorrections.store(0, std::memory_order_relaxed);
    g_sumErrorDistance.store(0.0, std::memory_order_relaxed);
    g_maxLatencyCompensatedMs.store(0.0f, std::memory_order_relaxed);

    LogPredictivePathing("Predictive Hero Turn & Pathing Engine initialized (Mode: %s, MaxLatency: %.0fms).",
        g_turnOnly.load(std::memory_order_relaxed) ? "TURN_ONLY" : "FULL_PREDICTION",
        g_maxLatencyMs.load(std::memory_order_relaxed));
}

void Shutdown() {
    PredictionStats s = GetStats();
    if (s.TotalMoveCommandsPredicted > 0) {
        LogPredictivePathing("[INFO] Shutting down Predictive Hero Turn & Pathing Engine (Commands: %llu, Reconciliations: %llu, HardCorrections: %llu, AvgDelta: %.2f units).",
            (unsigned long long)s.TotalMoveCommandsPredicted,
            (unsigned long long)s.TotalReconciliations,
            (unsigned long long)s.HardCorrections,
            s.AvgErrorDistance);
    }

    AcquireSRWLockExclusive(&g_stateLock);
    memset(&g_state, 0, sizeof(g_state));
    ReleaseSRWLockExclusive(&g_stateLock);
}

void OnLocalMoveOrder(float currentX, float currentY, float currentZ, float targetX, float targetY, float moveSpeed, float turnRateRadPerSec) {
    if (!g_enabled.load(std::memory_order_relaxed)) return;

    float dx = targetX - currentX;
    float dy = targetY - currentY;
    float distSq = Kongor_FmaRoundToOdd_f32(dx, dx, dy * dy);
    if (distSq < 0.0001f) return;

    float targetAngle = NormalizeAngle(atan2f(dy, dx));

    AcquireSRWLockExclusive(&g_stateLock);
    if (!g_state.predictionActive) {
        g_state.predictedPos = { currentX, currentY, currentZ };
        g_state.serverAuthoritativePos = { currentX, currentY, currentZ };
        g_state.predictedYaw = targetAngle;
    }

    g_state.targetWaypoint = { targetX, targetY, currentZ };
    g_state.targetYaw = targetAngle;
    g_state.moveSpeed = (moveSpeed > 10.0f) ? moveSpeed : 300.0f;
    g_state.turnRate = (turnRateRadPerSec > 0.01f) ? turnRateRadPerSec : 6.28318f;
    g_state.isTurning = true;

    if (!g_turnOnly.load(std::memory_order_relaxed) && distSq > 1.0f) {
        g_state.isMoving = true;
    } else {
        g_state.isMoving = false;
    }

    g_state.predictionActive = true;
    g_state.lastCommandTimeMs = GetTickCount64();
    ReleaseSRWLockExclusive(&g_stateLock);

    g_totalMoveCommands.fetch_add(1, std::memory_order_relaxed);
    LogPredictivePathing("Local move order: Target (%.1f, %.1f), TargetYaw: %.3f rad, Speed: %.1f", targetX, targetY, targetAngle, moveSpeed);
}

void OnServerSnapshot(float serverX, float serverY, float serverZ, float serverYaw, uint32_t sequenceId) {
    if (!g_enabled.load(std::memory_order_relaxed)) return;

    AcquireSRWLockExclusive(&g_stateLock);
    g_state.serverAuthoritativePos = { serverX, serverY, serverZ };
    g_state.serverAuthoritativeYaw = NormalizeAngle(serverYaw);
    g_state.lastSequenceId = sequenceId;

    if (!g_state.predictionActive) {
        g_state.predictedPos = { serverX, serverY, serverZ };
        g_state.predictedYaw = g_state.serverAuthoritativeYaw;
        ReleaseSRWLockExclusive(&g_stateLock);
        return;
    }

    float dx = g_state.predictedPos.x - serverX;
    float dy = g_state.predictedPos.y - serverY;
    float deltaDist = sqrtf(Kongor_FmaRoundToOdd_f32(dx, dx, dy * dy));

    // Hard correction threshold (> 150 units = collision, stun, creep block)
    if (deltaDist > 150.0f) {
        g_state.predictedPos = g_state.serverAuthoritativePos;
        g_state.predictedYaw = g_state.serverAuthoritativeYaw;
        g_hardCorrections.fetch_add(1, std::memory_order_relaxed);
        LogPredictivePathing("Hard correction applied: Delta %.1f units > 150 (Stun/Obstacle). Reconciled to server authority.", deltaDist);
    } else if (deltaDist > 0.05f) {
        // Smooth exponential reconciliation blend via single-rounding round-to-odd FMA
        float alpha = 0.35f;
        g_state.predictedPos.x = Kongor_LerpRoundToOdd_f32(g_state.predictedPos.x, serverX, alpha);
        g_state.predictedPos.y = Kongor_LerpRoundToOdd_f32(g_state.predictedPos.y, serverY, alpha);
        g_state.predictedPos.z = Kongor_LerpRoundToOdd_f32(g_state.predictedPos.z, serverZ, alpha);
    }

    ReleaseSRWLockExclusive(&g_stateLock);

    g_totalReconciliations.fetch_add(1, std::memory_order_relaxed);

    // Atomically accumulate error
    double currentSum = g_sumErrorDistance.load(std::memory_order_relaxed);
    while (!g_sumErrorDistance.compare_exchange_weak(currentSum, currentSum + deltaDist, std::memory_order_relaxed)) {}
}

void Update(float deltaSeconds, float* outRenderX, float* outRenderY, float* outRenderZ, float* outRenderYaw) {
    if (deltaSeconds < 0.00001f) deltaSeconds = 0.00001f;

    AcquireSRWLockExclusive(&g_stateLock);
    if (g_enabled.load(std::memory_order_relaxed) && g_state.predictionActive) {
        // 1. Predictive Turn Rate Heading Integration (single-rounding round-to-odd FMA)
        if (g_state.isTurning) {
            float deltaYaw = NormalizeAngle(g_state.targetYaw - g_state.predictedYaw);
            float maxTurnStep = g_state.turnRate * deltaSeconds;

            if (fabsf(deltaYaw) <= maxTurnStep) {
                g_state.predictedYaw = g_state.targetYaw;
                g_state.isTurning = false;
            } else {
                float signedDeltaSeconds = (deltaYaw > 0.0f) ? deltaSeconds : -deltaSeconds;
                g_state.predictedYaw = NormalizeAngle(Kongor_FmaRoundToOdd_f32(g_state.turnRate, signedDeltaSeconds, g_state.predictedYaw));
            }
        }

        // 2. Dead Reckoning Positional Step (unless Turn-Only Mode is active)
        if (g_state.isMoving && !g_turnOnly.load(std::memory_order_relaxed)) {
            float dx = g_state.targetWaypoint.x - g_state.predictedPos.x;
            float dy = g_state.targetWaypoint.y - g_state.predictedPos.y;
            float dist = sqrtf(Kongor_FmaRoundToOdd_f32(dx, dx, dy * dy));
            float step = g_state.moveSpeed * deltaSeconds;

            if (dist <= step) {
                g_state.predictedPos.x = g_state.targetWaypoint.x;
                g_state.predictedPos.y = g_state.targetWaypoint.y;
                g_state.isMoving = false;
            } else {
                float invDist = 1.0f / dist;
                float dirX = dx * invDist;
                float dirY = dy * invDist;
                g_state.predictedPos.x = Kongor_FmaRoundToOdd_f32(dirX, step, g_state.predictedPos.x);
                g_state.predictedPos.y = Kongor_FmaRoundToOdd_f32(dirY, step, g_state.predictedPos.y);
            }
        }
    }

    if (outRenderX) *outRenderX = g_state.predictedPos.x;
    if (outRenderY) *outRenderY = g_state.predictedPos.y;
    if (outRenderZ) *outRenderZ = g_state.predictedPos.z;
    if (outRenderYaw) *outRenderYaw = g_state.predictedYaw;
    ReleaseSRWLockExclusive(&g_stateLock);
}

PredictionStats GetStats() {
    PredictionStats s;
    s.TotalMoveCommandsPredicted = g_totalMoveCommands.load(std::memory_order_relaxed);
    s.TotalReconciliations = g_totalReconciliations.load(std::memory_order_relaxed);
    s.HardCorrections = g_hardCorrections.load(std::memory_order_relaxed);
    
    uint64_t totalRecon = s.TotalReconciliations;
    double sumErr = g_sumErrorDistance.load(std::memory_order_relaxed);
    s.AvgErrorDistance = (totalRecon > 0) ? (float)(sumErr / (double)totalRecon) : 0.0f;
    s.MaxLatencyCompensatedMs = g_maxLatencyMs.load(std::memory_order_relaxed);
    s.IsEnabled = g_enabled.load(std::memory_order_relaxed);
    s.TurnOnlyMode = g_turnOnly.load(std::memory_order_relaxed);
    return s;
}

void SetConfig(bool enabled, bool turnOnly, float maxLatencyMs) {
    g_enabled.store(enabled, std::memory_order_relaxed);
    g_turnOnly.store(turnOnly, std::memory_order_relaxed);
    if (maxLatencyMs > 10.0f && maxLatencyMs < 1000.0f) {
        g_maxLatencyMs.store(maxLatencyMs, std::memory_order_relaxed);
    }

    LogPredictivePathing("Configuration updated: Enabled: %s, TurnOnly: %s, MaxLatency: %.0fms",
        enabled ? "true" : "false", turnOnly ? "true" : "false", g_maxLatencyMs.load(std::memory_order_relaxed));
}

bool IsEnabled() {
    return g_enabled.load(std::memory_order_relaxed);
}

bool IsTurnOnly() {
    return g_turnOnly.load(std::memory_order_relaxed);
}

// ============================================================================
// Milestone 4.3: Multi-Ray Packet Traversal (AVX2 SIMD Raymarching)
// ============================================================================

bool IsAvx2Supported() {
    static int s_supported = -1;
    if (s_supported == -1) {
        int cpuInfo[4] = { 0 };
        __cpuid(cpuInfo, 0);
        int nIds = cpuInfo[0];
        if (nIds >= 7) {
            __cpuidex(cpuInfo, 7, 0);
            s_supported = ((cpuInfo[1] & (1 << 5)) != 0) ? 1 : 0;
        } else {
            s_supported = 0;
        }
    }
    return s_supported == 1;
}

RayHitResult8 IntersectRayPacket8_AABB_AVX2(const RayPacket8& packet, const AABB2D& box) {
    RayHitResult8 result;
    result.hitMask = 0;

    __m256 origX = _mm256_load_ps(packet.originX);
    __m256 origY = _mm256_load_ps(packet.originY);
    __m256 invDirX = _mm256_load_ps(packet.invDirX);
    __m256 invDirY = _mm256_load_ps(packet.invDirY);
    __m256 maxDist = _mm256_load_ps(packet.maxDist);

    __m256 boxMinX = _mm256_set1_ps(box.minX);
    __m256 boxMaxX = _mm256_set1_ps(box.maxX);
    __m256 boxMinY = _mm256_set1_ps(box.minY);
    __m256 boxMaxY = _mm256_set1_ps(box.maxY);

    // t1x = (boxMinX - origX) * invDirX, t2x = (boxMaxX - origX) * invDirX
    __m256 t1x = _mm256_mul_ps(_mm256_sub_ps(boxMinX, origX), invDirX);
    __m256 t2x = _mm256_mul_ps(_mm256_sub_ps(boxMaxX, origX), invDirX);

    __m256 tMinX = _mm256_min_ps(t1x, t2x);
    __m256 tMaxX = _mm256_max_ps(t1x, t2x);

    // t1y = (boxMinY - origY) * invDirY, t2y = (boxMaxY - origY) * invDirY
    __m256 t1y = _mm256_mul_ps(_mm256_sub_ps(boxMinY, origY), invDirY);
    __m256 t2y = _mm256_mul_ps(_mm256_sub_ps(boxMaxY, origY), invDirY);

    __m256 tMinY = _mm256_min_ps(t1y, t2y);
    __m256 tMaxY = _mm256_max_ps(t1y, t2y);

    __m256 tNear = _mm256_max_ps(tMinX, tMinY);
    __m256 tFar  = _mm256_min_ps(tMaxX, tMaxY);

    __m256 zero = _mm256_setzero_ps();

    // Hit condition: (tFar >= tNear) & (tFar >= 0.0f) & (tNear <= maxDist)
    __m256 cond1 = _mm256_cmp_ps(tFar, tNear, _CMP_GE_OQ);
    __m256 cond2 = _mm256_cmp_ps(tFar, zero, _CMP_GE_OQ);
    __m256 cond3 = _mm256_cmp_ps(tNear, maxDist, _CMP_LE_OQ);

    __m256 hit = _mm256_and_ps(_mm256_and_ps(cond1, cond2), cond3);
    int mask = _mm256_movemask_ps(hit);
    result.hitMask = static_cast<uint8_t>(mask & 0xFF);

    // Hit distance is clamped to max(0.0f, tNear)
    __m256 clampedDist = _mm256_max_ps(zero, tNear);
    _mm256_store_ps(result.hitDistance, clampedDist);

    return result;
}

RayHitResult8 IntersectRayPacket8_AABB_Scalar(const RayPacket8& packet, const AABB2D& box) {
    RayHitResult8 result;
    result.hitMask = 0;

    for (int i = 0; i < 8; ++i) {
        float t1x = (box.minX - packet.originX[i]) * packet.invDirX[i];
        float t2x = (box.maxX - packet.originX[i]) * packet.invDirX[i];
        float tMinX = (std::min)(t1x, t2x);
        float tMaxX = (std::max)(t1x, t2x);

        float t1y = (box.minY - packet.originY[i]) * packet.invDirY[i];
        float t2y = (box.maxY - packet.originY[i]) * packet.invDirY[i];
        float tMinY = (std::min)(t1y, t2y);
        float tMaxY = (std::max)(t1y, t2y);

        float tNear = (std::max)(tMinX, tMinY);
        float tFar  = (std::min)(tMaxX, tMaxY);

        if (tFar >= tNear && tFar >= 0.0f && tNear <= packet.maxDist[i]) {
            result.hitMask |= (1 << i);
            result.hitDistance[i] = (std::max)(0.0f, tNear);
        } else {
            result.hitDistance[i] = packet.maxDist[i];
        }
    }

    return result;
}

RayHitResult8 IntersectRayPacket8_AABB(const RayPacket8& packet, const AABB2D& box) {
    if (IsAvx2Supported()) {
        return IntersectRayPacket8_AABB_AVX2(packet, box);
    }
    return IntersectRayPacket8_AABB_Scalar(packet, box);
}

uint8_t BatchTraceRayPacket8_Obstacles(
    const RayPacket8& packet,
    const AABB2D* boxes,
    size_t boxCount,
    float* outNearestDistances)
{
    for (int i = 0; i < 8; ++i) {
        outNearestDistances[i] = packet.maxDist[i];
    }
    uint8_t totalHitMask = 0;

    for (size_t b = 0; b < boxCount; ++b) {
        RayHitResult8 res = IntersectRayPacket8_AABB(packet, boxes[b]);
        if (res.hitMask != 0) {
            for (int i = 0; i < 8; ++i) {
                if (res.hitMask & (1 << i)) {
                    if (res.hitDistance[i] < outNearestDistances[i]) {
                        outNearestDistances[i] = res.hitDistance[i];
                        totalHitMask |= (1 << i);
                    }
                }
            }
        }
    }

    return totalHitMask;
}

} // namespace PredictivePathing
} // namespace Kongor
