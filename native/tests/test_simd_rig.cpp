// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * Project Kongor - SIMD Bone Rigging & Hierarchy Concatenation Unit Tests
 * File: native/tests/test_simd_rig.cpp
 */

#include "../include/kongor_simd_rig.h"
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <chrono>
#include <iostream>

#define ASSERT_TRUE(cond) do { \
    if (!(cond)) { \
        fprintf(stderr, "[FAIL] %s:%d: Assertion failed: %s\n", __FILE__, __LINE__, #cond); \
        exit(1); \
    } \
} while(0)

#define ASSERT_NEAR(a, b, eps) do { \
    float diff = std::abs((a) - (b)); \
    if (diff > (eps)) { \
        fprintf(stderr, "[FAIL] %s:%d: Assertion failed: |%f - %f| = %f > %f\n", \
            __FILE__, __LINE__, static_cast<double>(a), static_cast<double>(b), static_cast<double>(diff), static_cast<double>(eps)); \
        exit(1); \
    } \
} while(0)

void test_basic_math() {
    printf("[TEST] test_basic_math...\n");
    using namespace Kongor;

    // Quat Identity
    Quat qId = Quat::Identity();
    ASSERT_NEAR(qId.x, 0.0f, 1e-6f);
    ASSERT_NEAR(qId.y, 0.0f, 1e-6f);
    ASSERT_NEAR(qId.z, 0.0f, 1e-6f);
    ASSERT_NEAR(qId.w, 1.0f, 1e-6f);

    // Vector rotation by 90 degrees around Y axis
    // Angle = pi/2, half angle = pi/4
    float halfAngle = 3.14159265f / 4.0f;
    Quat rotY{ 0.0f, std::sin(halfAngle), 0.0f, std::cos(halfAngle) };
    Vec3 forward{ 0.0f, 0.0f, 1.0f };
    Vec3 rotated = rotY.Rotate(forward);

    // Rotating (0, 0, 1) by +90 deg around Y should yield (1, 0, 0)
    ASSERT_NEAR(rotated.x, 1.0f, 1e-4f);
    ASSERT_NEAR(rotated.y, 0.0f, 1e-4f);
    ASSERT_NEAR(rotated.z, 0.0f, 1e-4f);

    printf("  -> PASSED: Vector rotation by quaternion verified.\n");
}

void test_hierarchy_concatenation() {
    printf("[TEST] test_hierarchy_concatenation...\n");
    using namespace Kongor;

    SimdRig rig;
    rig.SetBoneCount(3);

    // Bone 0: Root at (0, 10, 0)
    rig.SetParent(0, -1);
    rig.SetLocalTransform(0, { Quat::Identity(), Vec3{ 0.0f, 10.0f, 0.0f }, Vec3{ 1.0f, 1.0f, 1.0f } });

    // Bone 1: Child of 0 at (0, 5, 0)
    rig.SetParent(1, 0);
    rig.SetLocalTransform(1, { Quat::Identity(), Vec3{ 0.0f, 5.0f, 0.0f }, Vec3{ 1.0f, 1.0f, 1.0f } });

    // Bone 2: Child of 1 at (0, 2, 0)
    rig.SetParent(2, 1);
    rig.SetLocalTransform(2, { Quat::Identity(), Vec3{ 0.0f, 2.0f, 0.0f }, Vec3{ 1.0f, 1.0f, 1.0f } });

    rig.EvaluateHierarchyScalar();

    // World position of Bone 2 should be 10 + 5 + 2 = 17
    const auto& w2 = rig.GetWorldTransform(2);
    ASSERT_NEAR(w2.pos.x, 0.0f, 1e-5f);
    ASSERT_NEAR(w2.pos.y, 17.0f, 1e-5f);
    ASSERT_NEAR(w2.pos.z, 0.0f, 1e-5f);

    const Mat4& m2 = rig.GetWorldMatrix(2);
    ASSERT_NEAR(m2.m[12], 0.0f, 1e-5f);
    ASSERT_NEAR(m2.m[13], 17.0f, 1e-5f);
    ASSERT_NEAR(m2.m[14], 0.0f, 1e-5f);
    ASSERT_NEAR(m2.m[15], 1.0f, 1e-5f);

    printf("  -> PASSED: 3-bone chain hierarchy concatenated correctly.\n");
}

void test_simd_parity_and_benchmark() {
    printf("[TEST] test_simd_parity_and_benchmark...\n");
    using namespace Kongor;

    SimdRig rig;
    const int BONE_COUNT = 64;
    const int ITERATIONS = 100000;
    rig.InitSyntheticHumanoid(BONE_COUNT);

    printf("  Rig configured with %d bones (%s)\n", BONE_COUNT, KONGOR_SIMD_NAME);
    printf("  Executing %d iterations (%d million bone evaluations)...\n",
        ITERATIONS, static_cast<int>((static_cast<long long>(BONE_COUNT) * ITERATIONS) / 1000000));

    auto res = rig.RunBenchmark(ITERATIONS);

    printf("  Results:\n");
    printf("    Backend:        %s\n", res.simdBackend.c_str());
    printf("    Scalar Time:    %.2f ms\n", res.scalarElapsedMs);
    printf("    SIMD Time:      %.2f ms\n", res.simdElapsedMs);
    printf("    Speedup:        %.2fx\n", res.speedup);
    printf("    Max Error:      %.8f\n", res.maxError);
    printf("    Parity Match:   %s\n", res.paritySuccess ? "PASS" : "FAIL");

    ASSERT_TRUE(res.paritySuccess);
    ASSERT_TRUE(res.maxError < 1e-4f);

    // Emit standardized JSON for CI / gate scripts
    printf("\nJSON_OUTPUT: {\"status\":\"SUCCESS\",\"backend\":\"%s\",\"bones\":%d,\"iterations\":%d,\"scalar_ms\":%.2f,\"simd_ms\":%.2f,\"speedup\":%.2f,\"max_error\":%.8f}\n",
        res.simdBackend.c_str(), res.boneCount, res.iterations, res.scalarElapsedMs, res.simdElapsedMs, res.speedup, res.maxError);

    printf("  -> PASSED: SIMD mathematical parity and performance verified.\n");
}

int main(int argc, char** argv) {
    printf("==========================================================\n");
    printf("  Project Kongor: SIMD Bone Rigging Test Suite\n");
    printf("  Backend: %s\n", KONGOR_SIMD_NAME);
    printf("==========================================================\n");

    test_basic_math();
    test_hierarchy_concatenation();
    test_simd_parity_and_benchmark();

    printf("\nAll SIMD Rigging tests passed successfully.\n");
    return 0;
}
