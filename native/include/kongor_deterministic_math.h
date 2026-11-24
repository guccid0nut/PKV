// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * @file kongor_deterministic_math.h
 * @brief Bit-Exact Lockstep Simulation Math: Single-Rounding Round-to-Odd Software FMA
 *
 * Project Kongor - Phase 4 (Rec #6: Bit-Exact Lockstep Simulation Math)
 *
 * Guarantees bit-identical IEEE-754 binary32 fused multiply-add (RN24(a * b + c)) across:
 *   1. MSVC (/fp:precise vs /fp:fast) and GCC/Clang (-ffp-contract=off vs fast)
 *   2. CPUs with or without FMA3 hardware execution units
 *   3. Adversarial double-rounding tie boundaries where naive (float)((double)a * (double)b + (double)c)
 *      suffers 1-ULP drift due to intermediate binary64 rounding onto a binary32 midpoint.
 */

#ifndef KONGOR_DETERMINISTIC_MATH_H
#define KONGOR_DETERMINISTIC_MATH_H

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

#if defined(_MSC_VER)
// satisfies: OBL-MATH-FP-CONTRACT-OFF
#pragma float_control(precise, on, push)
#pragma fp_contract(off)
#elif defined(__clang__)
// satisfies: OBL-MATH-FP-CONTRACT-OFF
#pragma STDC FP_CONTRACT OFF
#elif defined(__GNUC__)
// satisfies: OBL-MATH-FP-CONTRACT-OFF
#pragma GCC push_options
#pragma GCC optimize ("-ffp-contract=off")
#endif

#ifdef __cplusplus
extern "C" {
#endif

static inline uint32_t Kongor_FloatToBits(float f) {
    uint32_t u = 0;
    memcpy(&u, &f, sizeof(uint32_t));
    return u;
}

static inline float Kongor_BitsToFloat(uint32_t u) {
    float f = 0.0f;
    memcpy(&f, &u, sizeof(float));
    return f;
}

static inline uint64_t Kongor_DoubleToBits(double d) {
    uint64_t u = 0;
    memcpy(&u, &d, sizeof(uint64_t));
    return u;
}

static inline double Kongor_BitsToDouble(uint64_t u) {
    double d = 0.0;
    memcpy(&d, &u, sizeof(double));
    return d;
}

/**
 * @brief Computes single-rounded IEEE-754 binary32 fused multiply-add RN24(a * b + c)
 *        using Boldo-Melquiond round-to-odd in binary64 with Knuth 6-op TwoSum.
 */
#if defined(__GNUC__) && !defined(__clang__)
__attribute__((optimize("no-fast-math")))
#endif
static inline float Kongor_FmaRoundToOdd_f32(float a, float b, float c) {
    uint32_t ua = Kongor_FloatToBits(a);
    uint32_t ub = Kongor_FloatToBits(b);
    uint32_t uc = Kongor_FloatToBits(c);

    // NaN / Infinity fast-path
    if (((ua & 0x7F800000U) == 0x7F800000U) ||
        ((ub & 0x7F800000U) == 0x7F800000U) ||
        ((uc & 0x7F800000U) == 0x7F800000U)) {
        return (float)((double)a * (double)b + (double)c);
    }

    // Step 1: Exact 48-bit product in 53-bit IEEE-754 binary64 (satisfies: OBL-MATH-EXACT-PROD64)
    double pa = (double)a;
    double pb = (double)b;
    double pc = (double)c;
    double p  = pa * pb;

    // Step 2: Knuth 6-op branchless TwoSum to recover exact rounding residual e (satisfies: OBL-MATH-TWOSUM-RTO)
    double s       = p + pc;
    double p_prime = s - pc;
    double c_prime = s - p_prime;
    double err_p   = p - p_prime;
    double err_c   = pc - c_prime;
    double e       = err_p + err_c;

    // Step 3: Boldo-Melquiond round-to-odd adjustment in binary64 before single final float32 cast
    if (e != 0.0) {
        uint64_t s_bits = Kongor_DoubleToBits(s);
        uint64_t s_exp  = s_bits & 0x7FF0000000000000ULL;
        if (((s_bits & 1ULL) == 0ULL) && (s_exp != 0ULL) && (s_exp != 0x7FF0000000000000ULL)) {
            uint64_t e_bits = Kongor_DoubleToBits(e);
            bool same_magnitude_dir = (((s_bits ^ e_bits) & 0x8000000000000000ULL) == 0ULL);
            if (same_magnitude_dir) {
                s_bits += 1ULL;
            } else {
                s_bits -= 1ULL;
            }
            s = Kongor_BitsToDouble(s_bits);
        }
    }

    return (float)s;
}

/**
 * @brief Deterministic linear interpolation using single-rounded round-to-odd FMA:
 *        baseVal * (1.0f - alpha) + targetVal * alpha
 */
static inline float Kongor_LerpRoundToOdd_f32(float baseVal, float targetVal, float alpha) {
    float oneMinusAlpha = 1.0f - alpha;
    float scaledTarget  = targetVal * alpha;
    return Kongor_FmaRoundToOdd_f32(baseVal, oneMinusAlpha, scaledTarget);
}

/**
 * @brief Verifies all 8 golden lockstep FMA vectors, including the 4 algebraic double-rounding
 *        halfway-tie traps where both unfused (a * b + c) and naive double-rounded
 *        (float)((double)a * (double)b + (double)c) fail by 1 ULP.
 * @param outFailedMask Bitmask of any failed golden vectors (0 = all 8 passed).
 * @return 1 if all 8 golden vectors matched exact IEEE-754 single-rounding bit-for-bit, 0 otherwise.
 */
// satisfies: OBL-MATH-TIE-VECTORS
static inline int Kongor_VerifyFmaRoundToOddGoldenVectors(uint32_t* outFailedMask) {
    struct GoldenFmaVector {
        uint32_t aBits;
        uint32_t bBits;
        uint32_t cBits;
        uint32_t expectedSingleRoundedBits;
        uint32_t wrongDoubleRoundedBits;
    };

    static const struct GoldenFmaVector kVectors[8] = {
        // 0: Positive Downward Tie Trap: a=32784.0f (0x47001000), b=16382.0f (0x467FF800) -> p = 2^29 + 3*2^16 - 32, c = -2^-24f (0xB3800000)
        //    Exact = p - 2^-24 -> rounds DOWN to 0x4E000BFF (double-rounded & unfused give 0x4E000C00)
        { 0x47001000U, 0x467FF800U, 0xB3800000U, 0x4E000BFFU, 0x4E000C00U },

        // 1: Positive Upward Tie Trap: a=32776.0f (0x47000800), b=16388.0f (0x46800800) -> p = 2^29 + 2^18 + 32, c = +2^-24f (0x33800000)
        //    Exact = 2^29 + 2^18 + 32 + 2^-24 -> rounds UP to 0x4E001001 (double-rounded & unfused give 0x4E001000)
        { 0x47000800U, 0x46800800U, 0x33800000U, 0x4E001001U, 0x4E001000U },

        // 2: Negative Downward Magnitude Tie Trap: a=-32784.0f (0xC7001000), b=16382.0f (0x467FF800) -> p = -(2^29 + 3*2^16 - 32), c = +2^-24f (0x33800000)
        //    Exact = -(p - 2^-24) -> rounds to 0xCE000BFF (double-rounded & unfused give 0xCE000C00)
        { 0xC7001000U, 0x467FF800U, 0x33800000U, 0xCE000BFFU, 0xCE000C00U },

        // 3: Negative Upward Magnitude Tie Trap: a=-32776.0f (0xC7000800), b=16388.0f (0x46800800) -> p = -(2^29 + 2^18 + 32), c = -2^-24f (0xB3800000)
        //    Exact = -(2^29 + 2^18 + 32 + 2^-24) -> rounds to 0xCE001001 (double-rounded & unfused give 0xCE001000)
        { 0xC7000800U, 0x46800800U, 0xB3800000U, 0xCE001001U, 0xCE001000U },

        // 4: Exact Cancellation near 1.0f: (1 + 2^-23) * (1 - 2^-23) + (-1.0f) = -2^-46f (0xA8800000)
        //    Unfused (a * b + c) rounds a*b to 1.0f and returns +0.0f (0x00000000), losing 100% of precision!
        { 0x3F800001U, 0x3F7FFFFEU, 0xBF800000U, 0xA8800000U, 0x00000000U },

        // 5: Standard Exact Integer FMA: 300.0f * 0.016666668f + 1000.0f = 1005.0f (0x447B4000)
        { 0x43960000U, 0x3C888889U, 0x447A0000U, 0x447B4000U, 0x00000000U },

        // 6: Predictive Pathing Reconciliation Blend: 1450.25f * 0.65f + 508.4625f = 1451.125f (0x44B56400)
        { 0x44B54800U, 0x3F266666U, 0x43FE3B33U, 0x44B56400U, 0x00000000U },

        // 7: Zero / Identity Preservation: 0.0f * 300.0f + (-0.0f) = +0.0f
        { 0x00000000U, 0x43960000U, 0x80000000U, 0x00000000U, 0x00000000U }
    };

    uint32_t failMask = 0;
    for (uint32_t i = 0; i < 8; ++i) {
        float a = Kongor_BitsToFloat(kVectors[i].aBits);
        float b = Kongor_BitsToFloat(kVectors[i].bBits);
        float c = Kongor_BitsToFloat(kVectors[i].cBits);
        uint32_t gotBits = Kongor_FloatToBits(Kongor_FmaRoundToOdd_f32(a, b, c));
        if (gotBits != kVectors[i].expectedSingleRoundedBits) {
            failMask |= (1U << i);
        }
    }

    if (outFailedMask) {
        *outFailedMask = failMask;
    }
    return (failMask == 0U) ? 1 : 0;
}

#ifdef __cplusplus
} // extern "C"
#endif

#if defined(_MSC_VER)
#pragma float_control(pop)
#elif defined(__GNUC__) && !defined(__clang__)
#pragma GCC pop_options
#endif

#endif // KONGOR_DETERMINISTIC_MATH_H
