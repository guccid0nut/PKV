// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

// ============================================================================
// Project Kongor — Concrete-Type VMT Shadow Table Cloner & Hook Manager (#179)
// ============================================================================
// Clones a concrete C++ class's virtual method table into a heap-allocated
// shadow VMT (sizing up to MaxVmtSlots + RTTI complete-object-locator slot at
// index -1), swaps individual virtual slot pointers on the shadow copy, and
// atomically points target object instances' vptr to the shadow table without
// mutating read-only .rdata vtables or corrupting base-class polymorphic tables.
// Supports clean restoration of the original vptr on hook disable/teardown.
// ============================================================================

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace kongor::native_guard {

class ConcreteVmtHookManager {
public:
    static constexpr size_t kDefaultMaxVmtSlots = 128;

    struct ShadowTableRecord {
        uintptr_t* originalVtable = nullptr;
        // Allocated with 1 leading slot for RTTI CompleteObjectLocator (index -1)
        std::vector<uintptr_t> allocation;
        uintptr_t* shadowVtable = nullptr;
        size_t slotCount = 0;
        std::unordered_map<size_t, uintptr_t> hookedSlots; // slotIndex -> detourAddr
    };

    static ConcreteVmtHookManager& Instance() {
        static ConcreteVmtHookManager s_instance;
        return s_instance;
    }

    // Clones the VMT of a concrete object instance (if not already cloned for its
    // original vptr), replaces slot `slotIndex` with `detourFunc`, and points the
    // instance's vptr to the shadow VMT. Writes original slot address to *outOriginalFunc.
    bool HookInstanceVirtualSlot(
        void* instancePtr,
        size_t slotIndex,
        uintptr_t detourFunc,
        uintptr_t* outOriginalFunc,
        size_t maxSlots = kDefaultMaxVmtSlots)
    {
        if (!instancePtr || detourFunc == 0 || slotIndex >= maxSlots) {
            return false;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        uintptr_t** vptrAddr = reinterpret_cast<uintptr_t**>(instancePtr);
        uintptr_t* currentVtable = *vptrAddr;
        if (!currentVtable) {
            return false;
        }

        // Resolve original vtable if this instance is already pointing to a shadow table
        uintptr_t* origVtable = currentVtable;
        auto instIt = m_instanceOriginalVptr.find(instancePtr);
        if (instIt != m_instanceOriginalVptr.end()) {
            origVtable = instIt->second;
        }

        ShadowTableRecord& record = GetOrCreateShadowTableLocked(origVtable, maxSlots);
        if (!record.shadowVtable || slotIndex >= record.slotCount) {
            return false;
        }

        if (outOriginalFunc) {
            *outOriginalFunc = record.originalVtable[slotIndex];
        }

        record.shadowVtable[slotIndex] = detourFunc;
        record.hookedSlots[slotIndex] = detourFunc;

        // Point instance vptr to the shadow vtable (preserving RTTI slot at -1)
        m_instanceOriginalVptr[instancePtr] = origVtable;
        *vptrAddr = record.shadowVtable;
        return true;
    }

    // Restores an instance's vptr back to its original read-only .rdata VMT.
    bool RestoreInstanceVptr(void* instancePtr) {
        if (!instancePtr) {
            return false;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        auto it = m_instanceOriginalVptr.find(instancePtr);
        if (it == m_instanceOriginalVptr.end()) {
            return false;
        }

        uintptr_t** vptrAddr = reinterpret_cast<uintptr_t**>(instancePtr);
        *vptrAddr = it->second;
        m_instanceOriginalVptr.erase(it);
        return true;
    }

    // Restores all tracked object instances back to their original VMTs.
    size_t RestoreAllInstances() {
        std::lock_guard<std::mutex> lock(m_mutex);
        size_t restored = 0;
        for (auto& kv : m_instanceOriginalVptr) {
            if (kv.first && kv.second) {
                uintptr_t** vptrAddr = reinterpret_cast<uintptr_t**>(kv.first);
                *vptrAddr = kv.second;
                ++restored;
            }
        }
        m_instanceOriginalVptr.clear();
        m_shadowTables.clear();
        return restored;
    }

    size_t GetTrackedInstanceCount() const {
        std::lock_guard<std::mutex> lock(m_mutex);
        return m_instanceOriginalVptr.size();
    }

private:
    ConcreteVmtHookManager() = default;

    ShadowTableRecord& GetOrCreateShadowTableLocked(uintptr_t* origVtable, size_t maxSlots) {
        uintptr_t key = reinterpret_cast<uintptr_t>(origVtable);
        auto it = m_shadowTables.find(key);
        if (it != m_shadowTables.end()) {
            return it->second;
        }

        ShadowTableRecord rec{};
        rec.originalVtable = origVtable;
        rec.slotCount = maxSlots;
        // Allocate 1 extra leading slot for MSVC RTTI CompleteObjectLocator at vptr[-1]
        rec.allocation.resize(maxSlots + 1, 0);
        rec.allocation[0] = origVtable[-1];
        for (size_t i = 0; i < maxSlots; ++i) {
            rec.allocation[i + 1] = origVtable[i];
        }
        rec.shadowVtable = &rec.allocation[1];

        auto inserted = m_shadowTables.emplace(key, std::move(rec));
        return inserted.first->second;
    }

    mutable std::mutex m_mutex;
    std::unordered_map<uintptr_t, ShadowTableRecord> m_shadowTables;
    std::unordered_map<void*, uintptr_t*> m_instanceOriginalVptr;
};

} // namespace kongor::native_guard
