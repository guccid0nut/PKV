// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - Explicit 32/64-Bit Arithmetic Overflow Guards
// (Option K: rz_types_overflow.h & MathExtras.h isIntN(32))
// ============================================================================
#pragma once
#include <cstdint>
#include <cstddef>
#include <limits>

// Checks if adding two signed 32-bit integers would overflow int32_t
#define KONGOR_ST32_ADD_OVFCHK(a, b) \
    ((((b) > 0) && ((a) > (INT32_MAX - (b)))) || (((b) < 0) && ((a) < (INT32_MIN - (b)))))

// Checks if adding two unsigned 32-bit integers would overflow uint32_t
#define KONGOR_UT32_ADD_OVFCHK(a, b) \
    ((static_cast<uint32_t>(a) > (UINT32_MAX - static_cast<uint32_t>(b))))

// Checks if adding two unsigned 64-bit integers would overflow uint64_t
#define KONGOR_UT64_ADD_OVFCHK(a, b) \
    ((static_cast<uint64_t>(a) > (UINT64_MAX - static_cast<uint64_t>(b))))

// Checks if adding two size_t values would overflow size_t
#define KONGOR_SZT_ADD_OVFCHK(a, b) \
    ((static_cast<size_t>(a) > (SIZE_MAX - static_cast<size_t>(b))))

// Checks if multiplying two size_t values would overflow size_t
#define KONGOR_SZT_MUL_OVFCHK(a, b) \
    (((a) != 0) && ((static_cast<size_t>(b) > (SIZE_MAX / static_cast<size_t>(a)))))

namespace Kongor {
namespace MathExtras {

// Option T (MathExtras.h): Returns true if signed 64-bit value fits in a signed 32-bit displacement
constexpr bool IsInt32(int64_t x) noexcept {
    return x >= (-2147483647LL - 1LL) && x <= 2147483647LL;
}

} // namespace MathExtras
} // namespace Kongor
