// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - Zero-Allocation Stack String Stream, Lazy Binary Ring,
// Reproducer-Gated Widget Guard & Live Struct Peek (Options S, R, J, F)
// ============================================================================
#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <atomic>
#include "../../../src/engine/headers/k2_structs.generated.h"

namespace Kongor {

// Option S (Capstone SStream.h): Fixed-size stack-allocated string stream.
// Safe to invoke inside VEH / SEH crash handlers when the Windows CRT heap lock is held or corrupted (0xC0000374).
template <size_t Capacity = 512>
class StackSStream {
public:
    StackSStream() noexcept : m_len(0) {
        m_buf[0] = '\0';
    }

    void Clear() noexcept {
        m_len = 0;
        m_buf[0] = '\0';
    }

    void Append(const char* str) noexcept {
        if (!str) return;
        while (*str && m_len + 1 < Capacity) {
            m_buf[m_len++] = *str++;
        }
        m_buf[m_len] = '\0';
    }

    void AppendChar(char c) noexcept {
        if (m_len + 1 < Capacity) {
            m_buf[m_len++] = c;
            m_buf[m_len] = '\0';
        }
    }

    void AppendHex64(uint64_t val, int width = 16) noexcept {
        static const char kHexDigits[] = "0123456789ABCDEF";
        char tmp[16];
        for (int i = 15; i >= 0; --i) {
            tmp[i] = kHexDigits[val & 0xFULL];
            val >>= 4;
        }
        int start = (width > 0 && width <= 16) ? (16 - width) : 0;
        for (int i = start; i < 16 && m_len + 1 < Capacity; ++i) {
            m_buf[m_len++] = tmp[i];
        }
        m_buf[m_len] = '\0';
    }

    void AppendUInt64(uint64_t val) noexcept {
        char tmp[24];
        int pos = 0;
        if (val == 0) {
            AppendChar('0');
            return;
        }
        while (val > 0 && pos < 23) {
            tmp[pos++] = static_cast<char>('0' + (val % 10));
            val /= 10;
        }
        for (int i = pos - 1; i >= 0; --i) {
            AppendChar(tmp[i]);
        }
    }

    void AppendFmt(const char* fmt, ...) noexcept {
        if (!fmt || m_len + 1 >= Capacity) return;
        va_list args;
        va_start(args, fmt);
        int written = vsnprintf(m_buf + m_len, Capacity - m_len, fmt, args);
        va_end(args);
        if (written > 0) {
            size_t avail = Capacity - 1 - m_len;
            m_len += (static_cast<size_t>(written) < avail) ? static_cast<size_t>(written) : avail;
            m_buf[m_len] = '\0';
        }
    }

    const char* c_str() const noexcept { return m_buf; }
    size_t size() const noexcept { return m_len; }
    constexpr size_t capacity() const noexcept { return Capacity; }

private:
    char   m_buf[Capacity];
    size_t m_len;
};

// Option R (Capstone SStream_Init Lazy Formatting + Tarantool Flight-Recorder SeqLock Ring):
// Stores raw binary telemetry events + caller-owned 47-char detail label in a lock-free ring on the hot path,
// using per-slot odd/even commit sequence numbers so a mid-write crash cleanly skips torn slots.
struct BinaryTelemetryEvent {
    std::atomic<uint64_t> commitSeq{ 0 }; // Odd = write in-flight; Even ((idx + 1) << 1) = committed
    uint64_t              timestampQpc{ 0 };
    uint32_t              threadId{ 0 };
    uint32_t              eventId{ 0 };
    uint64_t              arg0{ 0 };
    uint64_t              arg1{ 0 };
    const char*           internedTag{ nullptr };
    char                  detail[48]{};
};

template <size_t Capacity = 256>
class BinaryTelemetryRing {
    static_assert((Capacity & (Capacity - 1)) == 0, "Capacity must be a power of 2");
public:
    static void CopySanitizedDetail(char (&dst)[48], const char* src) noexcept {
        if (!src || src[0] == '\0') {
            dst[0] = '\0';
            return;
        }
        size_t i = 0;
        for (; i < 47 && src[i] != '\0'; ++i) {
            char c = src[i];
            if (c == '\r' || c == '\n' || c == '\t') {
                c = ' ';
            }
            dst[i] = c;
        }
        dst[i] = '\0';
    }

    void Record(uint32_t eventId, const char* internedTag, uint64_t arg0 = 0, uint64_t arg1 = 0, const char* detailStr = nullptr) noexcept {
        LARGE_INTEGER qpc;
        QueryPerformanceCounter(&qpc);
        const uint64_t idx = m_writeIdx.fetch_add(1, std::memory_order_relaxed);
        BinaryTelemetryEvent& slot = m_slots[idx & (Capacity - 1)];

        const uint64_t writingSeq = (idx << 1) | 1ULL;
        slot.commitSeq.store(writingSeq, std::memory_order_release);
        std::atomic_signal_fence(std::memory_order_acq_rel);

        slot.timestampQpc = static_cast<uint64_t>(qpc.QuadPart);
        slot.threadId     = GetCurrentThreadId();
        slot.eventId      = eventId;
        slot.arg0         = arg0;
        slot.arg1         = arg1;
        slot.internedTag  = internedTag ? internedTag : "event";
        CopySanitizedDetail(slot.detail, detailStr);

        std::atomic_signal_fence(std::memory_order_acq_rel);
        const uint64_t committedSeq = (idx + 1ULL) << 1;
        slot.commitSeq.store(committedSeq, std::memory_order_release);
    }

    // Test-only helper: leaves a slot in the odd (in-flight) state to simulate a crash mid-Record().
    uint64_t SimulateInterruptedWriteForTest(const char* internedTag, const char* partialDetail) noexcept {
        const uint64_t idx = m_writeIdx.fetch_add(1, std::memory_order_relaxed);
        BinaryTelemetryEvent& slot = m_slots[idx & (Capacity - 1)];
        const uint64_t writingSeq = (idx << 1) | 1ULL;
        slot.commitSeq.store(writingSeq, std::memory_order_release);
        slot.internedTag = internedTag ? internedTag : "torn";
        CopySanitizedDetail(slot.detail, partialDetail);
        // Intentionally omit committedSeq store so commitSeq stays odd (uncommitted)
        return idx;
    }

    template <size_t StreamCap>
    void FormatRecentToStream(StackSStream<StreamCap>& out, size_t maxEvents = 32) const noexcept {
        const uint64_t head = m_writeIdx.load(std::memory_order_acquire);
        size_t inspectCount = (head < Capacity) ? static_cast<size_t>(head) : Capacity;
        if (inspectCount > maxEvents) inspectCount = maxEvents;

        size_t skippedTorn = 0;
        for (size_t i = 0; i < inspectCount; ++i) {
            const uint64_t targetIdx = head - 1 - i;
            const uint64_t expectedSeq = (targetIdx + 1ULL) << 1;
            const BinaryTelemetryEvent& ev = m_slots[targetIdx & (Capacity - 1)];

            const uint64_t seq1 = ev.commitSeq.load(std::memory_order_acquire);
            if ((seq1 & 1ULL) != 0 || seq1 != expectedSeq) {
                ++skippedTorn;
                continue;
            }

            std::atomic_signal_fence(std::memory_order_acq_rel);
            const char* localTag = ev.internedTag ? ev.internedTag : "event";
            const uint32_t localTid = ev.threadId;
            const uint64_t localQpc = ev.timestampQpc;
            const uint64_t localArg0 = ev.arg0;
            char localDetail[48];
            memcpy(localDetail, ev.detail, sizeof(localDetail));
            localDetail[47] = '\0';
            std::atomic_signal_fence(std::memory_order_acq_rel);

            const uint64_t seq2 = ev.commitSeq.load(std::memory_order_acquire);
            if (seq2 != expectedSeq) {
                ++skippedTorn;
                continue;
            }

            out.Append("  [Ring] tag=");
            out.Append(localTag);
            out.Append(" tid=");
            out.AppendUInt64(localTid);
            out.Append(" qpc=");
            out.AppendUInt64(localQpc);
            out.Append(" arg0=0x");
            out.AppendHex64(localArg0, 8);
            if (localDetail[0] != '\0') {
                out.Append(" detail=\"");
                out.Append(localDetail);
                out.Append("\"");
            }
            out.Append("\n");
        }

        if (skippedTorn > 0) {
            out.Append("  [Ring] skipped_in_flight_slots=");
            out.AppendUInt64(skippedTorn);
            out.Append("\n");
        }
    }

    uint64_t TotalRecorded() const noexcept {
        return m_writeIdx.load(std::memory_order_relaxed);
    }

private:
    std::atomic<uint64_t> m_writeIdx{ 0 };
    BinaryTelemetryEvent  m_slots[Capacity]{};
};

inline BinaryTelemetryRing<256>& GetGlobalBinaryTelemetryRing() noexcept {
    static BinaryTelemetryRing<256> s_ring;
    return s_ring;
}

// Tarantool-inspired Runtime Log Burst Rate Limiter (src/lib/core/ratelimit.h):
// Allows up to `burstLimit` events per `intervalMs` window, silently counts suppressed repeats
// so 144 FPS hot loops never hitch on disk I/O, and returns the exact suppressed count when the
// next window opens. STRICTLY FOR NORMAL RUNTIME LOGGING — NEVER CALL INSIDE VEH CRASH HANDLERS.
class LogBurstLimiter {
public:
    constexpr LogBurstLimiter(uint64_t intervalMs = 5000ULL, uint32_t burstLimit = 16U) noexcept
        : m_intervalMs(intervalMs > 0 ? intervalMs : 1ULL),
          m_burstLimit(burstLimit > 0 ? burstLimit : 1U) {}

    bool TryAcquire(uint64_t nowMs, uint64_t* outFlushedSuppressedCount = nullptr, uint64_t* outPrevWindowMs = nullptr) noexcept {
        if (outFlushedSuppressedCount) *outFlushedSuppressedCount = 0;
        if (outPrevWindowMs) *outPrevWindowMs = m_intervalMs;

        AcquireSRWLockExclusive(&m_lock);
        if (!m_initialized || (nowMs >= m_windowStartMs && (nowMs - m_windowStartMs) >= m_intervalMs)) {
            const uint64_t prevSuppressed = m_suppressedInWindow;
            const uint64_t elapsedMs = m_initialized ? (nowMs - m_windowStartMs) : m_intervalMs;
            m_initialized = true;
            m_windowStartMs = nowMs;
            m_emittedInWindow = 1U;
            m_suppressedInWindow = 0ULL;
            ReleaseSRWLockExclusive(&m_lock);

            if (outFlushedSuppressedCount) *outFlushedSuppressedCount = prevSuppressed;
            if (outPrevWindowMs) *outPrevWindowMs = elapsedMs;
            return true;
        }

        if (m_emittedInWindow < m_burstLimit) {
            ++m_emittedInWindow;
            ReleaseSRWLockExclusive(&m_lock);
            return true;
        }

        ++m_suppressedInWindow;
        ReleaseSRWLockExclusive(&m_lock);
        return false;
    }

    bool TryAcquireNow(uint64_t* outFlushedSuppressedCount = nullptr, uint64_t* outPrevWindowMs = nullptr) noexcept {
        return TryAcquire(static_cast<uint64_t>(GetTickCount64()), outFlushedSuppressedCount, outPrevWindowMs);
    }

private:
    SRWLOCK  m_lock = SRWLOCK_INIT;
    uint64_t m_intervalMs;
    uint32_t m_burstLimit;
    bool     m_initialized = false;
    uint64_t m_windowStartMs = 0;
    uint32_t m_emittedInWindow = 0;
    uint64_t m_suppressedInWindow = 0;
};

// Option J: Reproducer-Gated Widget Teardown & Re-Entrancy Check
namespace WidgetSafety {

struct IWidget_Partial {
    void*    vftable;            // +0x0000
    uint8_t  _pad_0008[0x70];    // +0x0008..+0x0077
    uint32_t m_uiFlags_at_0x78;  // +0x0078
    uint32_t _pad_007C;          // +0x007C
};
static_assert(offsetof(IWidget_Partial, vftable) == 0x00, "IWidget_Partial::vftable offset mismatch");
static_assert(offsetof(IWidget_Partial, m_uiFlags_at_0x78) == 0x78, "IWidget_Partial::m_uiFlags_at_0x78 offset mismatch");
static_assert(sizeof(IWidget_Partial) == 0x80, "IWidget_Partial size mismatch");

inline bool IsWidgetPointerReadableAndValid(const void* pWidget, uint32_t* outFlags = nullptr) noexcept {
    if (!pWidget) return false;
    __try {
        const auto* w = reinterpret_cast<const IWidget_Partial*>(pWidget);
        if (!w->vftable) return false;
        // Probe first vtable slot readability
        volatile uintptr_t firstSlot = *reinterpret_cast<const uintptr_t*>(w->vftable);
        (void)firstSlot;
        if (outFlags) {
            *outFlags = w->m_uiFlags_at_0x78;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace WidgetSafety

// Option F: Live Struct Peek (PEEK_STRUCT <struct_name> <hex_address>)
namespace StructPeek {

inline bool SafeCopyStructBytes(const void* srcAddr, void* dstBuf, size_t numBytes) noexcept {
    if (!srcAddr || !dstBuf || numBytes == 0) return false;
    __try {
        memcpy(dstBuf, srcAddr, numBytes);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

template <size_t StreamCap>
inline void FormatStructPeekJson(const char* structName, uintptr_t targetAddr, StackSStream<StreamCap>& out) noexcept {
    out.Clear();
    if (!structName || targetAddr < 0x10000ULL) {
        out.Append("{\"success\":false,\"error\":\"Invalid struct name or address\"}");
        return;
    }

    if (_stricmp(structName, "CArchive_Partial") == 0 ||
        _stricmp(structName, "CArchive") == 0 ||
        _stricmp(structName, "CArchiveLayout") == 0) {
        Kongor::K2Structs::CArchiveLayout snap{};
        if (!SafeCopyStructBytes(reinterpret_cast<const void*>(targetAddr), &snap, sizeof(snap))) {
            out.Append("{\"success\":false,\"error\":\"Memory unreadable at target address\"}");
            return;
        }
        out.AppendFmt(
            "{\"success\":true,\"struct\":\"CArchive_Partial\",\"address\":\"0x%llX\",\"sizeof\":%zu,"
            "\"fields\":{\"pVtableOrArchiveImpl\":\"0x%llX\",\"uiOpenFlags\":\"0x%08X\",\"m_bIsOpen\":%s}}",
            (unsigned long long)targetAddr,
            sizeof(snap),
            (unsigned long long)reinterpret_cast<uintptr_t>(snap.pVtableOrArchiveImpl),
            snap.uiOpenFlags,
            (snap.uiOpenFlags & 1u) ? "true" : "false");
        return;
    }

    if (_stricmp(structName, "IWidget_Partial") == 0 ||
        _stricmp(structName, "IWidget") == 0 ||
        _stricmp(structName, "IWidgetLayout") == 0) {
        Kongor::WidgetSafety::IWidget_Partial snap{};
        if (!SafeCopyStructBytes(reinterpret_cast<const void*>(targetAddr), &snap, sizeof(snap))) {
            out.Append("{\"success\":false,\"error\":\"Memory unreadable at target address\"}");
            return;
        }
        out.AppendFmt(
            "{\"success\":true,\"struct\":\"IWidget_Partial\",\"address\":\"0x%llX\",\"sizeof\":%zu,"
            "\"fields\":{\"vftable\":\"0x%llX\",\"m_uiFlags_at_0x78\":\"0x%08X\"}}",
            (unsigned long long)targetAddr,
            sizeof(snap),
            (unsigned long long)reinterpret_cast<uintptr_t>(snap.vftable),
            snap.m_uiFlags_at_0x78);
        return;
    }

    out.AppendFmt("{\"success\":false,\"error\":\"Unknown struct '%s' (supported: CArchive_Partial, IWidget_Partial)\"}", structName);
}

inline int FormatStructPeekJson(const char* structName, uintptr_t targetAddr, char* outBuf, size_t maxLen) noexcept {
    if (!outBuf || maxLen == 0) return 0;
    StackSStream<2048> ss;
    FormatStructPeekJson(structName, targetAddr, ss);
    size_t copyLen = ss.size();
    if (copyLen >= maxLen) {
        copyLen = maxLen - 1;
    }
    memcpy(outBuf, ss.c_str(), copyLen);
    outBuf[copyLen] = '\0';
    return (strstr(outBuf, "\"success\":true") != nullptr) ? 1 : 0;
}

} // namespace StructPeek
} // namespace Kongor
