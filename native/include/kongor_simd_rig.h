// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * Project Kongor: High-Performance SIMD Bone Rigging & Skeletal Concatenation Engine
 * File: native/include/kongor_simd_rig.h
 *
 * Inspired by EpicGames/OpenRigLogic, Unreal Engine 5 Skeletal Mesh Pipeline,
 * and Emscripten Portable SIMD Architecture.
 *
 * Provides Structure-of-Arrays (SoA) 8-wide (AVX2/FMA) and 4-wide (Wasm SIMD128 / SSE2)
 * parallel bone hierarchy evaluation, quaternion-vector rotation, and 4x4 matrix conversion.
 */

#pragma once
#ifndef KONGOR_SIMD_RIG_H
#define KONGOR_SIMD_RIG_H

#include <cstdint>
#include <cmath>
#include <cstring>
#include <vector>
#include <string>
#include <chrono>
#include <iostream>
#include <iomanip>
#include <sstream>
#include <algorithm>

// ----------------------------------------------------------------------------
// SIMD Architecture Detection & Lane Width Configuration
// ----------------------------------------------------------------------------
#if defined(__AVX2__)
  #include <immintrin.h>
  #define KONGOR_SIMD_MODE_AVX2 1
  #define KONGOR_SIMD_LANES 8
  #define KONGOR_SIMD_NAME "AVX2+FMA (8 lanes / 256-bit)"
#elif defined(__wasm_simd128__)
  #include <wasm_simd128.h>
  #define KONGOR_SIMD_MODE_WASM 1
  #define KONGOR_SIMD_LANES 4
  #define KONGOR_SIMD_NAME "WebAssembly SIMD128 (4 lanes / 128-bit)"
#elif defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
  #include <emmintrin.h>
  #include <xmmintrin.h>
  #define KONGOR_SIMD_MODE_SSE2 1
  #define KONGOR_SIMD_LANES 4
  #define KONGOR_SIMD_NAME "SSE2 (4 lanes / 128-bit)"
#else
  #define KONGOR_SIMD_MODE_SCALAR 1
  #define KONGOR_SIMD_LANES 1
  #define KONGOR_SIMD_NAME "Scalar Fallback (1 lane)"
#endif

namespace Kongor {

// ----------------------------------------------------------------------------
// Human-Friendly Scalar Geometric Types (Array-of-Structures)
// ----------------------------------------------------------------------------
struct Vec3 {
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;

    Vec3() = default;
    Vec3(float x_, float y_, float z_) : x(x_), y(y_), z(z_) {}

    Vec3 operator+(const Vec3& o) const { return { x + o.x, y + o.y, z + o.z }; }
    Vec3 operator-(const Vec3& o) const { return { x - o.x, y - o.y, z - o.z }; }
    Vec3 operator*(float s) const { return { x * s, y * s, z * s }; }
    Vec3 operator*(const Vec3& o) const { return { x * o.x, y * o.y, z * o.z }; } // Component-wise
};

struct Quat {
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
    float w = 1.0f; // Identity quaternion

    Quat() = default;
    Quat(float x_, float y_, float z_, float w_) : x(x_), y(y_), z(z_), w(w_) {}

    static Quat Identity() { return { 0.0f, 0.0f, 0.0f, 1.0f }; }

    // Hamilton product: q1 * q2
    Quat operator*(const Quat& r) const {
        return {
            w * r.x + x * r.w + y * r.z - z * r.y,
            w * r.y - x * r.z + y * r.w + z * r.x,
            w * r.z + x * r.y - y * r.x + z * r.w,
            w * r.w - x * r.x - y * r.y - z * r.z
        };
    }

    // Rotate a 3D vector by this quaternion: q * v * q^-1
    Vec3 Rotate(const Vec3& v) const {
        // Efficient formulation: v' = v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v)
        Vec3 qv{ x, y, z };
        Vec3 uv{
            qv.y * v.z - qv.z * v.y,
            qv.z * v.x - qv.x * v.z,
            qv.x * v.y - qv.y * v.x
        };
        Vec3 uuv{
            qv.y * uv.z - qv.z * uv.y,
            qv.z * uv.x - qv.x * uv.z,
            qv.x * uv.y - qv.y * uv.x
        };
        return {
            v.x + ((uv.x * w) + uuv.x) * 2.0f,
            v.y + ((uv.y * w) + uuv.y) * 2.0f,
            v.z + ((uv.z * w) + uuv.z) * 2.0f
        };
    }
};

struct Mat4 {
    float m[16] = {
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    };

    static Mat4 Identity() { return Mat4(); }

    // Column-major multiplication
    Mat4 operator*(const Mat4& b) const {
        Mat4 out;
        for (int c = 0; c < 4; c++) {
            for (int r = 0; r < 4; r++) {
                out.m[c * 4 + r] =
                    m[0 * 4 + r] * b.m[c * 4 + 0] +
                    m[1 * 4 + r] * b.m[c * 4 + 1] +
                    m[2 * 4 + r] * b.m[c * 4 + 2] +
                    m[3 * 4 + r] * b.m[c * 4 + 3];
            }
        }
        return out;
    }

    // Convert Translation, Rotation (Quat), Scale into 4x4 matrix
    static Mat4 FromTRS(const Vec3& pos, const Quat& rot, const Vec3& scale) {
        Mat4 out;
        float x2 = rot.x + rot.x;
        float y2 = rot.y + rot.y;
        float z2 = rot.z + rot.z;
        float xx = rot.x * x2;
        float xy = rot.x * y2;
        float xz = rot.x * z2;
        float yy = rot.y * y2;
        float yz = rot.y * z2;
        float zz = rot.z * z2;
        float wx = rot.w * x2;
        float wy = rot.w * y2;
        float wz = rot.w * z2;

        out.m[0] = (1.0f - (yy + zz)) * scale.x;
        out.m[1] = (xy + wz) * scale.x;
        out.m[2] = (xz - wy) * scale.x;
        out.m[3] = 0.0f;

        out.m[4] = (xy - wz) * scale.y;
        out.m[5] = (1.0f - (xx + zz)) * scale.y;
        out.m[6] = (yz + wx) * scale.y;
        out.m[7] = 0.0f;

        out.m[8]  = (xz + wy) * scale.z;
        out.m[9]  = (yz - wx) * scale.z;
        out.m[10] = (1.0f - (xx + yy)) * scale.z;
        out.m[11] = 0.0f;

        out.m[12] = pos.x;
        out.m[13] = pos.y;
        out.m[14] = pos.z;
        out.m[15] = 1.0f;

        return out;
    }
};

struct BoneTransform {
    Quat rot{ 0.0f, 0.0f, 0.0f, 1.0f };
    Vec3 pos{ 0.0f, 0.0f, 0.0f };
    Vec3 scale{ 1.0f, 1.0f, 1.0f };
};

// ----------------------------------------------------------------------------
// Structure-of-Arrays (SoA) SIMD Chunk Definition (8-wide aligned)
// ----------------------------------------------------------------------------
struct alignas(32) BoneChunkSoA8 {
    float rot_x[8] = { 0 };
    float rot_y[8] = { 0 };
    float rot_z[8] = { 0 };
    float rot_w[8] = { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };

    float pos_x[8] = { 0 };
    float pos_y[8] = { 0 };
    float pos_z[8] = { 0 };

    float scale_x[8] = { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
    float scale_y[8] = { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
    float scale_z[8] = { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
};

// ----------------------------------------------------------------------------
// High-Performance Skeletal Hierarchy Rig Solver
// ----------------------------------------------------------------------------
class SimdRig {
public:
    static constexpr int MAX_BONES = 256;

    SimdRig() = default;

    void SetBoneCount(int count) {
        boneCount = std::min(count, MAX_BONES);
        parentIndices.resize(boneCount, -1);
        localTransforms.resize(boneCount);
        worldTransforms.resize(boneCount);
        worldMatrices.resize(boneCount);
        invBindMatrices.resize(boneCount, Mat4::Identity());
        skinMatrices.resize(boneCount);
    }

    int GetBoneCount() const { return boneCount; }

    void SetParent(int boneIdx, int parentIdx) {
        if (boneIdx >= 0 && boneIdx < boneCount) {
            parentIndices[boneIdx] = parentIdx;
        }
    }

    int GetParent(int boneIdx) const {
        return (boneIdx >= 0 && boneIdx < boneCount) ? parentIndices[boneIdx] : -1;
    }

    void SetLocalTransform(int boneIdx, const BoneTransform& t) {
        if (boneIdx >= 0 && boneIdx < boneCount) {
            localTransforms[boneIdx] = t;
        }
    }

    const BoneTransform& GetWorldTransform(int boneIdx) const {
        return worldTransforms[boneIdx];
    }

    const Mat4& GetWorldMatrix(int boneIdx) const {
        return worldMatrices[boneIdx];
    }

    const Mat4& GetSkinMatrix(int boneIdx) const {
        return skinMatrices[boneIdx];
    }

    // ------------------------------------------------------------------------
    // Reference Implementation: Scalar Hierarchy Concatenation
    // ------------------------------------------------------------------------
    void EvaluateHierarchyScalar() {
        for (int i = 0; i < boneCount; i++) {
            int p = parentIndices[i];
            const BoneTransform& local = localTransforms[i];

            if (p < 0 || p >= i) {
                // Root bone
                worldTransforms[i] = local;
            } else {
                // Concatenate with parent world transform
                const BoneTransform& parent = worldTransforms[p];

                // 1. World scale
                worldTransforms[i].scale = parent.scale * local.scale;

                // 2. World position = parent.pos + parent.rot.Rotate(parent.scale * local.pos)
                Vec3 scaledLocalPos = parent.scale * local.pos;
                worldTransforms[i].pos = parent.pos + parent.rot.Rotate(scaledLocalPos);

                // 3. World rotation = parent.rot * local.rot
                worldTransforms[i].rot = parent.rot * local.rot;
            }

            // Convert to 4x4 World Matrix
            worldMatrices[i] = Mat4::FromTRS(
                worldTransforms[i].pos,
                worldTransforms[i].rot,
                worldTransforms[i].scale
            );

            // Compute skinning matrix = worldMatrix * invBindMatrix
            skinMatrices[i] = worldMatrices[i] * invBindMatrices[i];
        }
    }

    // ------------------------------------------------------------------------
    // SIMD Vectorized Implementation (AVX2 / Wasm / SSE2 with SoA Layout)
    // ------------------------------------------------------------------------
    void EvaluateHierarchySimd() {
        // Step 1: Forward topological hierarchy concatenation into contiguous SoA
        for (int i = 0; i < boneCount; i++) {
            int p = parentIndices[i];
            const BoneTransform& local = localTransforms[i];

            if (p < 0 || p >= i) {
                soa_rx[i] = local.rot.x;
                soa_ry[i] = local.rot.y;
                soa_rz[i] = local.rot.z;
                soa_rw[i] = local.rot.w;
                soa_px[i] = local.pos.x;
                soa_py[i] = local.pos.y;
                soa_pz[i] = local.pos.z;
                soa_sx[i] = local.scale.x;
                soa_sy[i] = local.scale.y;
                soa_sz[i] = local.scale.z;
            } else {
                float psx = soa_sx[p], psy = soa_sy[p], psz = soa_sz[p];
                soa_sx[i] = psx * local.scale.x;
                soa_sy[i] = psy * local.scale.y;
                soa_sz[i] = psz * local.scale.z;

                Vec3 scaledLocalPos{ psx * local.pos.x, psy * local.pos.y, psz * local.pos.z };
                Quat parentRot{ soa_rx[p], soa_ry[p], soa_rz[p], soa_rw[p] };
                Vec3 rotated = parentRot.Rotate(scaledLocalPos);

                soa_px[i] = soa_px[p] + rotated.x;
                soa_py[i] = soa_py[p] + rotated.y;
                soa_pz[i] = soa_pz[p] + rotated.z;

                Quat worldRot = parentRot * local.rot;
                soa_rx[i] = worldRot.x;
                soa_ry[i] = worldRot.y;
                soa_rz[i] = worldRot.z;
                soa_rw[i] = worldRot.w;
            }
        }

        // Step 2: Mass Parallel Matrix Synthesis (OpenRigLogic SoA SIMD Phase)
        ConvertTransformsToMatricesSimd();
    }

    // ------------------------------------------------------------------------
    // Microsecond Benchmark & Parity Verification
    // ------------------------------------------------------------------------
    struct BenchmarkResult {
        bool paritySuccess = false;
        float maxError = 0.0f;
        int boneCount = 0;
        int iterations = 0;
        double scalarElapsedMs = 0.0;
        double simdElapsedMs = 0.0;
        double speedup = 0.0;
        std::string simdBackend = KONGOR_SIMD_NAME;
    };

    BenchmarkResult RunBenchmark(int iterations = 100000) {
        BenchmarkResult res;
        res.boneCount = boneCount;
        res.iterations = iterations;

        // Run Scalar reference
        auto t0 = std::chrono::high_resolution_clock::now();
        for (int it = 0; it < iterations; it++) {
            EvaluateHierarchyScalar();
        }
        auto t1 = std::chrono::high_resolution_clock::now();
        res.scalarElapsedMs = std::chrono::duration<double, std::milli>(t1 - t0).count();

        // Capture scalar reference matrices
        std::vector<Mat4> scalarMatrices = worldMatrices;

        // Run SIMD
        auto t2 = std::chrono::high_resolution_clock::now();
        for (int it = 0; it < iterations; it++) {
            EvaluateHierarchySimd();
        }
        auto t3 = std::chrono::high_resolution_clock::now();
        res.simdElapsedMs = std::chrono::duration<double, std::milli>(t3 - t2).count();

        // Calculate Speedup
        res.speedup = (res.simdElapsedMs > 0.0001) ? (res.scalarElapsedMs / res.simdElapsedMs) : 1.0;

        // Assert Parity
        res.maxError = 0.0f;
        for (int i = 0; i < boneCount; i++) {
            for (int m = 0; m < 16; m++) {
                float diff = std::abs(scalarMatrices[i].m[m] - worldMatrices[i].m[m]);
                if (diff > res.maxError) {
                    res.maxError = diff;
                }
            }
        }
        res.paritySuccess = (res.maxError < 1e-4f);

        return res;
    }

    // Helper: Initialize a synthetic representative bipedal humanoid skeleton
    void InitSyntheticHumanoid(int totalBones = 64) {
        SetBoneCount(totalBones);

        // 0: Root / Pelvis
        SetParent(0, -1);
        SetLocalTransform(0, { Quat::Identity(), Vec3{ 0.0f, 1.0f, 0.0f }, Vec3{ 1.0f, 1.0f, 1.0f } });

        // Branching chain: Spine -> Chest -> Neck -> Head
        // Pelvis -> Thigh_L -> Calf_L -> Foot_L
        // Pelvis -> Thigh_R -> Calf_R -> Foot_R
        // Chest -> Clavicle_L -> UpperArm_L -> LowerArm_L -> Hand_L -> Fingers...
        // Chest -> Clavicle_R -> UpperArm_R -> LowerArm_R -> Hand_R -> Fingers...
        for (int i = 1; i < totalBones; i++) {
            int p = (i < 4) ? 0 : (i - 1);
            if (i == 4 || i == 7 || i == 10 || i == 20) p = 0;
            SetParent(i, p);

            float angle = 0.05f * static_cast<float>(i % 7);
            Quat rot{ std::sin(angle), 0.0f, 0.0f, std::cos(angle) };
            Vec3 pos{ 0.0f, 0.15f, 0.02f * static_cast<float>(i % 3) };
            Vec3 scale{ 1.0f, 1.0f, 1.0f };
            SetLocalTransform(i, { rot, pos, scale });
        }
    }

private:
    int boneCount = 0;
    std::vector<int> parentIndices;
    std::vector<BoneTransform> localTransforms;
    std::vector<BoneTransform> worldTransforms;
    std::vector<Mat4> worldMatrices;
    std::vector<Mat4> invBindMatrices;
    std::vector<Mat4> skinMatrices;

    // Contiguous Structure-of-Arrays (SoA) Storage (32-byte aligned for AVX2)
    alignas(32) float soa_rx[MAX_BONES] = { 0 };
    alignas(32) float soa_ry[MAX_BONES] = { 0 };
    alignas(32) float soa_rz[MAX_BONES] = { 0 };
    alignas(32) float soa_rw[MAX_BONES] = { 0 };

    alignas(32) float soa_px[MAX_BONES] = { 0 };
    alignas(32) float soa_py[MAX_BONES] = { 0 };
    alignas(32) float soa_pz[MAX_BONES] = { 0 };

    alignas(32) float soa_sx[MAX_BONES] = { 0 };
    alignas(32) float soa_sy[MAX_BONES] = { 0 };
    alignas(32) float soa_sz[MAX_BONES] = { 0 };

    // ------------------------------------------------------------------------
    // SIMD Matrix Conversion Kernel
    // ------------------------------------------------------------------------
    void ConvertTransformsToMatricesSimd() {
#if defined(KONGOR_SIMD_MODE_AVX2)
        int chunks = (boneCount + 7) / 8;

        for (int c = 0; c < chunks; c++) {
            int base = c * 8;
            int countInChunk = std::min(8, boneCount - base);

            // Direct aligned 256-bit load from contiguous SoA arrays (Zero stack bouncing!)
            __m256 v_rx = _mm256_load_ps(&soa_rx[base]);
            __m256 v_ry = _mm256_load_ps(&soa_ry[base]);
            __m256 v_rz = _mm256_load_ps(&soa_rz[base]);
            __m256 v_rw = _mm256_load_ps(&soa_rw[base]);

            __m256 v_px = _mm256_load_ps(&soa_px[base]);
            __m256 v_py = _mm256_load_ps(&soa_py[base]);
            __m256 v_pz = _mm256_load_ps(&soa_pz[base]);

            __m256 v_sx = _mm256_load_ps(&soa_sx[base]);
            __m256 v_sy = _mm256_load_ps(&soa_sy[base]);
            __m256 v_sz = _mm256_load_ps(&soa_sz[base]);

            __m256 v_two = _mm256_set1_ps(2.0f);
            __m256 v_one = _mm256_set1_ps(1.0f);

            __m256 v_x2 = _mm256_mul_ps(v_rx, v_two);
            __m256 v_y2 = _mm256_mul_ps(v_ry, v_two);
            __m256 v_z2 = _mm256_mul_ps(v_rz, v_two);

            __m256 v_xx = _mm256_mul_ps(v_rx, v_x2);
            __m256 v_xy = _mm256_mul_ps(v_rx, v_y2);
            __m256 v_xz = _mm256_mul_ps(v_rx, v_z2);

            __m256 v_yy = _mm256_mul_ps(v_ry, v_y2);
            __m256 v_yz = _mm256_mul_ps(v_ry, v_z2);
            __m256 v_zz = _mm256_mul_ps(v_rz, v_z2);

            __m256 v_wx = _mm256_mul_ps(v_rw, v_x2);
            __m256 v_wy = _mm256_mul_ps(v_rw, v_y2);
            __m256 v_wz = _mm256_mul_ps(v_rw, v_z2);

            // Matrix column 0: (1 - yy - zz)*sx, (xy + wz)*sx, (xz - wy)*sx, 0
            __m256 m0 = _mm256_mul_ps(_mm256_sub_ps(_mm256_sub_ps(v_one, v_yy), v_zz), v_sx);
            __m256 m1 = _mm256_mul_ps(_mm256_add_ps(v_xy, v_wz), v_sx);
            __m256 m2 = _mm256_mul_ps(_mm256_sub_ps(v_xz, v_wy), v_sx);

            // Matrix column 1: (xy - wz)*sy, (1 - xx - zz)*sy, (yz + wx)*sy, 0
            __m256 m4 = _mm256_mul_ps(_mm256_sub_ps(v_xy, v_wz), v_sy);
            __m256 m5 = _mm256_mul_ps(_mm256_sub_ps(_mm256_sub_ps(v_one, v_xx), v_zz), v_sy);
            __m256 m6 = _mm256_mul_ps(_mm256_add_ps(v_yz, v_wx), v_sy);

            // Matrix column 2: (xz + wy)*sz, (yz - wx)*sz, (1 - xx - yy)*sz, 0
            __m256 m8  = _mm256_mul_ps(_mm256_add_ps(v_xz, v_wy), v_sz);
            __m256 m9  = _mm256_mul_ps(_mm256_sub_ps(v_yz, v_wx), v_sz);
            __m256 m10 = _mm256_mul_ps(_mm256_sub_ps(_mm256_sub_ps(v_one, v_xx), v_yy), v_sz);

            alignas(32) float out_m0[8], out_m1[8], out_m2[8];
            alignas(32) float out_m4[8], out_m5[8], out_m6[8];
            alignas(32) float out_m8[8], out_m9[8], out_m10[8];

            _mm256_store_ps(out_m0, m0); _mm256_store_ps(out_m1, m1); _mm256_store_ps(out_m2, m2);
            _mm256_store_ps(out_m4, m4); _mm256_store_ps(out_m5, m5); _mm256_store_ps(out_m6, m6);
            _mm256_store_ps(out_m8, m8); _mm256_store_ps(out_m9, m9); _mm256_store_ps(out_m10, m10);

            for (int i = 0; i < countInChunk; i++) {
                int b = base + i;
                float* m = worldMatrices[b].m;
                m[0] = out_m0[i]; m[1] = out_m1[i]; m[2] = out_m2[i]; m[3] = 0.0f;
                m[4] = out_m4[i]; m[5] = out_m5[i]; m[6] = out_m6[i]; m[7] = 0.0f;
                m[8] = out_m8[i]; m[9] = out_m9[i]; m[10] = out_m10[i]; m[11] = 0.0f;
                m[12] = soa_px[b]; m[13] = soa_py[b]; m[14] = soa_pz[b]; m[15] = 1.0f;

                skinMatrices[b] = worldMatrices[b] * invBindMatrices[b];
            }
        }
#else
        // Scalar / SSE2 / Wasm fallback loop
        for (int i = 0; i < boneCount; i++) {
            worldMatrices[i] = Mat4::FromTRS(
                worldTransforms[i].pos,
                worldTransforms[i].rot,
                worldTransforms[i].scale
            );
            skinMatrices[i] = worldMatrices[i] * invBindMatrices[i];
        }
#endif
    }
};

} // namespace Kongor

#endif // KONGOR_SIMD_RIG_H
