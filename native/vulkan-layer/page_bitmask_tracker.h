// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <vector>

#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace hon_capture {

inline int CountTrailingZeros64(uint64_t mask) noexcept {
    if (mask == 0ULL) return 64;
#if defined(_MSC_VER)
    unsigned long idx = 0;
    _BitScanForward64(&idx, mask);
    return static_cast<int>(idx);
#else
    return __builtin_ctzll(mask);
#endif
}

inline constexpr size_t AlignCaptureSlotStride4KB(size_t frameSizeBytes) noexcept {
    return (frameSizeBytes + 4095ULL) & ~size_t(4095ULL);
}

// ============================================================================
// (#206, [L2-P0-06]) Two-Level 4KB Page Bitmask Memory Tracker
// Supports per-slot range counting & clearing so draining completedSlot never
// wipes currentSlot's in-flight dirty page bitmask.
// ============================================================================
class TwoLevelPageBitmaskTracker {
public:
    static constexpr size_t PAGE_SIZE_4KB      = 4096;
    static constexpr size_t PAGES_PER_L1_WORD  = 64;
    static constexpr size_t L1_REGION_BYTES    = PAGE_SIZE_4KB * PAGES_PER_L1_WORD; // 256 KiB

    inline void Initialize(size_t totalBytes) {
        m_totalBytes = totalBytes > 0 ? totalBytes : L1_REGION_BYTES;
        m_totalPages = (m_totalBytes + PAGE_SIZE_4KB - 1) / PAGE_SIZE_4KB;
        size_t wordCount = (m_totalPages + PAGES_PER_L1_WORD - 1) / PAGES_PER_L1_WORD;
        m_cpuModifiedWords.assign(wordCount, 0ULL);
        m_gpuModifiedWords.assign(wordCount, 0ULL);
    }

    inline void MarkRegionCpuModified(size_t byteOffset, size_t byteLength) {
        if (byteLength == 0 || byteOffset >= m_totalBytes) return;
        size_t endByte = (std::min)(m_totalBytes, byteOffset + byteLength);
        size_t firstPage = byteOffset / PAGE_SIZE_4KB;
        size_t lastPage = (endByte - 1) / PAGE_SIZE_4KB;
        for (size_t page = firstPage; page <= lastPage && page < m_totalPages; ++page) {
            m_cpuModifiedWords[page / PAGES_PER_L1_WORD] |= (1ULL << (page % PAGES_PER_L1_WORD));
        }
    }

    inline void MarkRegionGpuModified(size_t byteOffset, size_t byteLength) {
        if (byteLength == 0 || byteOffset >= m_totalBytes) return;
        size_t endByte = (std::min)(m_totalBytes, byteOffset + byteLength);
        size_t firstPage = byteOffset / PAGE_SIZE_4KB;
        size_t lastPage = (endByte - 1) / PAGE_SIZE_4KB;
        for (size_t page = firstPage; page <= lastPage && page < m_totalPages; ++page) {
            m_gpuModifiedWords[page / PAGES_PER_L1_WORD] |= (1ULL << (page % PAGES_PER_L1_WORD));
        }
    }

    inline bool IsRegionCpuModified(size_t byteOffset, size_t byteLength) const noexcept {
        if (byteLength == 0 || byteOffset >= m_totalBytes) return false;
        size_t endByte = (std::min)(m_totalBytes, byteOffset + byteLength);
        size_t firstPage = byteOffset / PAGE_SIZE_4KB;
        size_t lastPage = (endByte - 1) / PAGE_SIZE_4KB;
        for (size_t page = firstPage; page <= lastPage && page < m_totalPages; ++page) {
            if ((m_cpuModifiedWords[page / PAGES_PER_L1_WORD] & (1ULL << (page % PAGES_PER_L1_WORD))) != 0ULL) {
                return true;
            }
        }
        return false;
    }

    inline bool IsRegionGpuModified(size_t byteOffset, size_t byteLength) const noexcept {
        if (byteLength == 0 || byteOffset >= m_totalBytes) return false;
        size_t endByte = (std::min)(m_totalBytes, byteOffset + byteLength);
        size_t firstPage = byteOffset / PAGE_SIZE_4KB;
        size_t lastPage = (endByte - 1) / PAGE_SIZE_4KB;
        for (size_t page = firstPage; page <= lastPage && page < m_totalPages; ++page) {
            if ((m_gpuModifiedWords[page / PAGES_PER_L1_WORD] & (1ULL << (page % PAGES_PER_L1_WORD))) != 0ULL) {
                return true;
            }
        }
        return false;
    }

    // [L2-P0-06] Per-slot range dirty count & clear: touches ONLY [byteOffset, byteOffset + byteLength)
    // so draining completedSlot never erases currentSlot's dirty bits.
    inline size_t CountDirtyGpuPagesInRangeAndClear(size_t byteOffset, size_t byteLength, bool clearAfterScan = false) noexcept {
        if (byteLength == 0 || byteOffset >= m_totalBytes || m_gpuModifiedWords.empty()) {
            return 0;
        }
        size_t endByte = (std::min)(m_totalBytes, byteOffset + byteLength);
        size_t firstPage = byteOffset / PAGE_SIZE_4KB;
        size_t lastPage = (endByte - 1) / PAGE_SIZE_4KB;
        if (lastPage >= m_totalPages) {
            lastPage = m_totalPages - 1;
        }

        size_t firstWord = firstPage / PAGES_PER_L1_WORD;
        size_t lastWord = lastPage / PAGES_PER_L1_WORD;
        size_t dirtyPages = 0;

        for (size_t w = firstWord; w <= lastWord && w < m_gpuModifiedWords.size(); ++w) {
            size_t bitStart = (w == firstWord) ? (firstPage % PAGES_PER_L1_WORD) : 0;
            size_t bitEnd = (w == lastWord) ? (lastPage % PAGES_PER_L1_WORD) : (PAGES_PER_L1_WORD - 1);

            uint64_t rangeMask = (bitEnd - bitStart == 63)
                ? ~0ULL
                : (((1ULL << (bitEnd - bitStart + 1)) - 1ULL) << bitStart);

            uint64_t word = m_gpuModifiedWords[w] & rangeMask;
            if (clearAfterScan) {
                m_gpuModifiedWords[w] &= ~rangeMask;
            }
            while (word != 0ULL) {
                int bit = CountTrailingZeros64(word);
                size_t pageIdx = (w * PAGES_PER_L1_WORD) + static_cast<size_t>(bit);
                if (pageIdx < m_totalPages) {
                    ++dirtyPages;
                }
                word &= (word - 1ULL);
            }
        }
        return dirtyPages;
    }

    inline size_t CountDirtyGpuPagesAndClear(bool clearAfterScan = false) noexcept {
        return CountDirtyGpuPagesInRangeAndClear(0, m_totalBytes, clearAfterScan);
    }

private:
    size_t m_totalBytes = 0;
    size_t m_totalPages = 0;
    std::vector<uint64_t> m_cpuModifiedWords;
    std::vector<uint64_t> m_gpuModifiedWords;
};

} // namespace hon_capture
