// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

#include <cstdint>
#include <windows.h>

namespace Kongor {
namespace PredictivePathing {

struct Vec3 {
    float x;
    float y;
    float z;
};

struct PredictedHeroState {
    Vec3 predictedPos;
    Vec3 serverAuthoritativePos;
    Vec3 targetWaypoint;
    float predictedYaw;             // Current visual/predicted yaw (radians [-PI, PI])
    float serverAuthoritativeYaw;   // Last confirmed server yaw
    float targetYaw;                // Target orientation towards waypoint
    float moveSpeed;                // Current unit movespeed (units/sec)
    float turnRate;                 // Turn rate (radians/sec, e.g. 6.28318 rad/s = 360 deg/s)
    uint32_t lastSequenceId;        // Last acknowledged network command sequence
    uint64_t lastCommandTimeMs;     // High-resolution timestamp of move order
    bool isMoving;
    bool isTurning;
    bool predictionActive;
};

struct PredictionStats {
    uint64_t TotalMoveCommandsPredicted;
    uint64_t TotalReconciliations;
    uint64_t HardCorrections;       // Stuns, collisions, creep-blocks (> 150 units)
    float AvgErrorDistance;         // Rolling average error in units
    float MaxLatencyCompensatedMs;  // Maximum latency window accommodated
    bool IsEnabled;
    bool TurnOnlyMode;
};

void Initialize();
void Shutdown();

void OnLocalMoveOrder(float currentX, float currentY, float currentZ, float targetX, float targetY, float moveSpeed, float turnRateRadPerSec);
void OnServerSnapshot(float serverX, float serverY, float serverZ, float serverYaw, uint32_t sequenceId);
void Update(float deltaSeconds, float* outRenderX, float* outRenderY, float* outRenderZ, float* outRenderYaw);

PredictionStats GetStats();
void SetConfig(bool enabled, bool turnOnly, float maxLatencyMs);
bool IsEnabled();
bool IsTurnOnly();

// ============================================================================
// Milestone 4.3: Multi-Ray Packet Traversal (AVX2 SIMD Raymarching)
// ============================================================================
struct RayPacket8 {
    alignas(32) float originX[8];
    alignas(32) float originY[8];
    alignas(32) float dirX[8];
    alignas(32) float dirY[8];
    alignas(32) float invDirX[8];
    alignas(32) float invDirY[8];
    alignas(32) float maxDist[8];
};

struct AABB2D {
    float minX;
    float minY;
    float maxX;
    float maxY;
};

struct RayHitResult8 {
    uint8_t hitMask;                  // Bit i is 1 if ray i hit the AABB
    alignas(32) float hitDistance[8]; // Distance to intersection for each ray
};

bool IsAvx2Supported();
RayHitResult8 IntersectRayPacket8_AABB_AVX2(const RayPacket8& packet, const AABB2D& box);
RayHitResult8 IntersectRayPacket8_AABB_Scalar(const RayPacket8& packet, const AABB2D& box);
RayHitResult8 IntersectRayPacket8_AABB(const RayPacket8& packet, const AABB2D& box);

uint8_t BatchTraceRayPacket8_Obstacles(
    const RayPacket8& packet,
    const AABB2D* boxes,
    size_t boxCount,
    float* outNearestDistances);

} // namespace PredictivePathing
} // namespace Kongor
