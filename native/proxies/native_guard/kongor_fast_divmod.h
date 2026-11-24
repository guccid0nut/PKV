// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
// ============================================================================
// File: kongor_fast_divmod.h
// Purpose: CUTLASS-inspired hardware-division-free FastDivmod (mulhi + shr)
//          for 32-bit unsigned integer quotient and remainder computation (#129).
// ============================================================================

#include <cstdint>

namespace Kongor {

struct FastDivmod {
    uint32_t Divisor;
    uint32_t Multiplier;
    uint32_t ShiftRight;
    uint32_t IsPowerOfTwo;
    uint32_t NeedsAddShift;

    constexpr FastDivmod() noexcept
        : Divisor(1u), Multiplier(1u), ShiftRight(0u), IsPowerOfTwo(1u), NeedsAddShift(0u) {}

    explicit constexpr FastDivmod(uint32_t divisor) noexcept
        : Divisor(divisor == 0u ? 1u : divisor),
          Multiplier(1u),
          ShiftRight(0u),
          IsPowerOfTwo(0u),
          NeedsAddShift(0u)
    {
        uint32_t d = Divisor;
        if ((d & (d - 1u)) == 0u) {
            IsPowerOfTwo = 1u;
            Multiplier = 1u;
            uint32_t s = 0u;
            while ((1u << s) < d && s < 31u) {
                ++s;
            }
            ShiftRight = s;
            return;
        }

        // Floor(log2(d))
        uint32_t floorLog2 = 0u;
        uint32_t temp = d;
        while (temp >>= 1u) {
            ++floorLog2;
        }

        // Ceil(log2(d))
        uint32_t ceilLog2 = floorLog2 + 1u;

        // Try direct 32-bit mulhi + shr with shift = floorLog2 or ceilLog2 (when < 32)
        for (uint32_t s = floorLog2; s <= ceilLog2 && s < 32u; ++s) {
            uint64_t twoToP = 1ULL << (32u + s);
            uint64_t m = (twoToP + d - 1ULL) / d;
            uint64_t rem = (m * d) - twoToP;
            // Exact for all n <= 0xFFFFFFFFu iff rem * 0xFFFFFFFFULL < twoToP
            if (m <= 0xFFFFFFFFULL && (rem == 0ULL || ((twoToP - 1ULL) / rem) >= 0xFFFFFFFFULL)) {
                Multiplier = static_cast<uint32_t>(m);
                ShiftRight = s;
                NeedsAddShift = 0u;
                return;
            }
        }

        // Granlund-Montgomery 33-bit multiplier fallback: M = ceil(2^(32 + ceilLog2) / d) - 2^32
        uint64_t twoToP = (ceilLog2 < 32u) ? (1ULL << (32u + ceilLog2)) : 0ULL;
        uint64_t mFull = (ceilLog2 < 32u)
            ? ((twoToP + d - 1ULL) / d)
            : ((0xFFFFFFFFFFFFFFFFULL / d) + 1ULL);
        Multiplier = static_cast<uint32_t>(mFull & 0xFFFFFFFFULL);
        ShiftRight = ceilLog2;
        NeedsAddShift = 1u;
    }

    constexpr uint32_t Div(uint32_t dividend) const noexcept {
        if (IsPowerOfTwo != 0u) {
            return dividend >> ShiftRight;
        }
        uint32_t t = static_cast<uint32_t>((static_cast<uint64_t>(dividend) * Multiplier) >> 32u);
        if (NeedsAddShift != 0u) {
            return (t + ((dividend - t) >> 1u)) >> (ShiftRight - 1u);
        }
        return t >> ShiftRight;
    }

    constexpr uint32_t Mod(uint32_t dividend) const noexcept {
        if (IsPowerOfTwo != 0u) {
            return dividend & (Divisor - 1u);
        }
        uint32_t q = Div(dividend);
        return dividend - (q * Divisor);
    }

    constexpr void DivMod(uint32_t dividend, uint32_t& outQuotient, uint32_t& outRemainder) const noexcept {
        outQuotient = Div(dividend);
        outRemainder = dividend - (outQuotient * Divisor);
    }
};

static_assert(FastDivmod(1u).Div(0xFFFFFFFFu) == 0xFFFFFFFFu, "FastDivmod(1) quotient check");
static_assert(FastDivmod(6u).Div(47u) == 7u && FastDivmod(6u).Mod(47u) == 5u, "FastDivmod(6) ring wrap check");
static_assert(FastDivmod(7u).Div(0xFFFFFFFFu) == (0xFFFFFFFFu / 7u), "FastDivmod(7) uint32 max check");
static_assert(FastDivmod(0xFFFFFFFFu).Div(0xFFFFFFFFu) == 1u && FastDivmod(0xFFFFFFFFu).Div(0xFFFFFFFEu) == 0u, "FastDivmod(UINT32_MAX) boundary check");

} // namespace Kongor
