// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - Precondition Telemetry Assert Macros (Option C1: rz_assert.h)
// Logs contract violations to userdata/logs/native_guard_hooks.log without
// crashing production unless KONGOR_DEBUG_ASSERT=1 is enabled.
// ============================================================================
#pragma once
#include <windows.h>
#include <intrin.h>

namespace Kongor {
namespace PatchController {
    void LogPreconditionFailure(const char* file, int line, const char* func, const char* expr);
} // namespace PatchController
} // namespace Kongor

inline bool KongorShouldDebugBreakOnAssert() {
    static int s_cached = -1;
    if (s_cached == -1) {
        char buf[8] = {0};
        DWORD n = GetEnvironmentVariableA("KONGOR_DEBUG_ASSERT", buf, sizeof(buf));
        s_cached = (n > 0 && buf[0] == '1') ? 1 : 0;
    }
    return s_cached == 1;
}

#define KONGOR_RETURN_IF_FAIL(expr) \
    do { \
        if (!(expr)) { \
            ::Kongor::PatchController::LogPreconditionFailure(__FILE__, __LINE__, __FUNCTION__, #expr); \
            if (KongorShouldDebugBreakOnAssert()) { __debugbreak(); } \
            return; \
        } \
    } while (0)

#define KONGOR_RETURN_VAL_IF_FAIL(expr, val) \
    do { \
        if (!(expr)) { \
            ::Kongor::PatchController::LogPreconditionFailure(__FILE__, __LINE__, __FUNCTION__, #expr); \
            if (KongorShouldDebugBreakOnAssert()) { __debugbreak(); } \
            return (val); \
        } \
    } while (0)

#define KONGOR_WARN_IF_REACHED() \
    do { \
        ::Kongor::PatchController::LogPreconditionFailure(__FILE__, __LINE__, __FUNCTION__, "UNREACHABLE_BRANCH_REACHED"); \
        if (KongorShouldDebugBreakOnAssert()) { __debugbreak(); } \
    } while (0)
