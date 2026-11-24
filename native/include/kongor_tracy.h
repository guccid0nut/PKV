// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

// ============================================================================
// Project Kongor — Dev-Only Tracy Profiler Wrapper (kongor_tracy.h)
// ============================================================================
// Default (KONGOR_TRACY_ENABLE undefined): 100% zero-cost compile-time no-ops.
//   - ZERO exported symbols
//   - ZERO WS2_32.dll imports
//   - ZERO "KONGOR_DEV_ONLY_TRACY_INSTRUMENTED_DO_NOT_SHIP" strings in binary
//
// Dev-Only (-DKONGOR_TRACY_ENABLE=ON):
//   1. Emits 'KongorDevOnlyTracyMarker' export returning marker banner
//      guaranteeing scripts/re/Assert-NoDevProfilerLeak.ps1 blocks any leak.
//   2. If <tracy/Tracy.hpp> is present, forwards to native Tracy zones.
//   3. Otherwise provides lightweight RAII QPC/steady_clock fallback scopes.
// ============================================================================

#if (defined(KONGOR_TRACY_ENABLE) || defined(KONGOR_ENABLE_TRACY)) && !defined(KONGOR_TRACY_DISABLED)

#if defined(KONGOR_TRACY_DEFINE_MARKER_EXPORT)
#if defined(_MSC_VER)
extern "C" __declspec(dllexport) const char* __cdecl KongorDevOnlyTracyMarker(void) {
    return "KONGOR_DEV_ONLY_TRACY_INSTRUMENTED_DO_NOT_SHIP";
}
#pragma comment(linker, "/INCLUDE:KongorDevOnlyTracyMarker")
#else
extern "C" __attribute__((visibility("default"), used)) const char* KongorDevOnlyTracyMarker(void) {
    return "KONGOR_DEV_ONLY_TRACY_INSTRUMENTED_DO_NOT_SHIP";
}
#endif
#endif

#if defined(__has_include)
#if __has_include(<tracy/Tracy.hpp>)
#define KONGOR_HAS_TRACY_HEADER 1
#endif
#endif

#if defined(KONGOR_HAS_TRACY_HEADER)
#include <tracy/Tracy.hpp>

#define KONGOR_TRACY_ZONE_SCOPED()          ZoneScoped
#define KONGOR_TRACY_ZONE_SCOPED_N(name)    ZoneScopedN(name)
#define KONGOR_TRACY_FRAME_MARK()           FrameMark
#define KONGOR_TRACY_FRAME_MARK_NAMED(name) FrameMarkNamed(name)
#define KONGOR_TRACY_PLOT(name, val)        TracyPlot(name, val)

#define KONGOR_TRACY_STARTUP_IF_REQUESTED() (true)
#define KONGOR_TRACY_SHUTDOWN_IF_ACTIVE()   (true)

#else // Fallback RAII QPC / CLOCK_MONOTONIC dev scope counters (zero libstdc++ dependency)

#include <cstdint>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <time.h>
#endif

namespace Kongor::TracyDevFallback {

inline void RecordZoneSample(const char* zoneName, double elapsedMicroseconds) noexcept {
    (void)zoneName;
    (void)elapsedMicroseconds;
}

struct ScopedZone {
    const char* m_name;
#if defined(_WIN32)
    LARGE_INTEGER m_start;

    explicit ScopedZone(const char* name) noexcept : m_name(name) {
        m_start.QuadPart = 0;
        ::QueryPerformanceCounter(&m_start);
    }

    ~ScopedZone() noexcept {
        LARGE_INTEGER end;
        end.QuadPart = 0;
        ::QueryPerformanceCounter(&end);
        static LARGE_INTEGER s_freq = []() {
            LARGE_INTEGER f;
            f.QuadPart = 0;
            ::QueryPerformanceFrequency(&f);
            return f;
        }();
        if (s_freq.QuadPart > 0 && end.QuadPart >= m_start.QuadPart) {
            double us = static_cast<double>(end.QuadPart - m_start.QuadPart) * 1000000.0 / static_cast<double>(s_freq.QuadPart);
            RecordZoneSample(m_name, us);
        }
    }
#else
    struct timespec m_start;

    explicit ScopedZone(const char* name) noexcept : m_name(name) {
        m_start.tv_sec = 0;
        m_start.tv_nsec = 0;
        ::clock_gettime(CLOCK_MONOTONIC, &m_start);
    }

    ~ScopedZone() noexcept {
        struct timespec end;
        end.tv_sec = 0;
        end.tv_nsec = 0;
        ::clock_gettime(CLOCK_MONOTONIC, &end);
        const double secDiff = static_cast<double>(end.tv_sec - m_start.tv_sec);
        const double nsecDiff = static_cast<double>(end.tv_nsec - m_start.tv_nsec);
        const double us = secDiff * 1000000.0 + nsecDiff * 0.001;
        if (us >= 0.0) {
            RecordZoneSample(m_name, us);
        }
    }
#endif

    ScopedZone(const ScopedZone&) = delete;
    ScopedZone& operator=(const ScopedZone&) = delete;
};

} // namespace Kongor::TracyDevFallback

#define KONGOR_TRACY_CONCAT_INNER(a, b) a##b
#define KONGOR_TRACY_CONCAT(a, b) KONGOR_TRACY_CONCAT_INNER(a, b)

#define KONGOR_TRACY_ZONE_SCOPED()          ::Kongor::TracyDevFallback::ScopedZone KONGOR_TRACY_CONCAT(_kongor_tracy_zone_, __LINE__)(__FUNCTION__)
#define KONGOR_TRACY_ZONE_SCOPED_N(name)    ::Kongor::TracyDevFallback::ScopedZone KONGOR_TRACY_CONCAT(_kongor_tracy_zone_, __LINE__)(name)
#define KONGOR_TRACY_FRAME_MARK()           ((void)0)
#define KONGOR_TRACY_FRAME_MARK_NAMED(name) ((void)(name))
#define KONGOR_TRACY_PLOT(name, val)        ((void)(name), (void)(val))

#define KONGOR_TRACY_STARTUP_IF_REQUESTED() (false)
#define KONGOR_TRACY_SHUTDOWN_IF_ACTIVE()   (false)

#endif // KONGOR_HAS_TRACY_HEADER

#else // !KONGOR_TRACY_ENABLE

#define KONGOR_TRACY_ZONE_SCOPED()          ((void)0)
#define KONGOR_TRACY_ZONE_SCOPED_N(name)    ((void)0)
#define KONGOR_TRACY_FRAME_MARK()           ((void)0)
#define KONGOR_TRACY_FRAME_MARK_NAMED(name) ((void)0)
#define KONGOR_TRACY_PLOT(name, val)        ((void)0)

#define KONGOR_TRACY_STARTUP_IF_REQUESTED() (false)
#define KONGOR_TRACY_SHUTDOWN_IF_ACTIVE()   (false)

#endif // KONGOR_TRACY_ENABLE
