// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once

// ============================================================================
// Project Kongor — Entity Sidecar Field Container (`EntityFieldContainer`) (#179)
// ============================================================================
// Attaches custom runtime state to native K2 engine objects (e.g. IUnitEntity,
// CWidget, CClientSnapshot) keyed by object pointer + string field key without
// altering `sizeof(NativeClass)` or shifting engine member offsets.
// Invokes registered destructor callbacks automatically when `OnEntityDestroyed`
// or `ClearAll` is called on entity teardown.
// ============================================================================

#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

namespace kongor::native_guard {

class EntityFieldContainer {
public:
    struct ErasedFieldSlot {
        void* rawPtr = nullptr;
        std::function<void(void*)> deleter;

        ErasedFieldSlot() = default;
        ErasedFieldSlot(void* p, std::function<void(void*)> d)
            : rawPtr(p), deleter(std::move(d)) {}

        ~ErasedFieldSlot() {
            if (rawPtr && deleter) {
                deleter(rawPtr);
                rawPtr = nullptr;
            }
        }

        ErasedFieldSlot(const ErasedFieldSlot&) = delete;
        ErasedFieldSlot& operator=(const ErasedFieldSlot&) = delete;

        ErasedFieldSlot(ErasedFieldSlot&& other) noexcept
            : rawPtr(other.rawPtr), deleter(std::move(other.deleter)) {
            other.rawPtr = nullptr;
        }

        ErasedFieldSlot& operator=(ErasedFieldSlot&& other) noexcept {
            if (this != &other) {
                if (rawPtr && deleter) {
                    deleter(rawPtr);
                }
                rawPtr = other.rawPtr;
                deleter = std::move(other.deleter);
                other.rawPtr = nullptr;
            }
            return *this;
        }
    };

    static EntityFieldContainer& Instance() {
        static EntityFieldContainer s_instance;
        return s_instance;
    }

    // Lazily constructs or returns a typed sidecar field attached to `entityPtr` under `fieldKey`.
    template <typename T, typename... Args>
    T* GetOrCreateField(const void* entityPtr, const std::string& fieldKey, Args&&... args) {
        if (!entityPtr || fieldKey.empty()) {
            return nullptr;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        uintptr_t entityKey = reinterpret_cast<uintptr_t>(entityPtr);
        auto& fieldMap = m_storage[entityKey];
        auto it = fieldMap.find(fieldKey);
        if (it != fieldMap.end() && it->second.rawPtr != nullptr) {
            return reinterpret_cast<T*>(it->second.rawPtr);
        }

        T* allocated = new T(std::forward<Args>(args)...);
        fieldMap[fieldKey] = ErasedFieldSlot(
            allocated,
            [](void* ptr) {
                delete reinterpret_cast<T*>(ptr);
            });
        return allocated;
    }

    template <typename T>
    T* TryGetField(const void* entityPtr, const std::string& fieldKey) const {
        if (!entityPtr || fieldKey.empty()) {
            return nullptr;
        }

        std::lock_guard<std::mutex> lock(m_mutex);
        uintptr_t entityKey = reinterpret_cast<uintptr_t>(entityPtr);
        auto entityIt = m_storage.find(entityKey);
        if (entityIt == m_storage.end()) {
            return nullptr;
        }
        auto fieldIt = entityIt->second.find(fieldKey);
        if (fieldIt == entityIt->second.end()) {
            return nullptr;
        }
        return reinterpret_cast<T*>(fieldIt->second.rawPtr);
    }

    // Invokes all registered field destructors for `entityPtr` and removes its sidecar map.
    size_t OnEntityDestroyed(const void* entityPtr) {
        if (!entityPtr) {
            return 0;
        }

        std::unordered_map<std::string, ErasedFieldSlot> extracted;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            uintptr_t entityKey = reinterpret_cast<uintptr_t>(entityPtr);
            auto it = m_storage.find(entityKey);
            if (it == m_storage.end()) {
                return 0;
            }
            extracted = std::move(it->second);
            m_storage.erase(it);
        }
        // Destructors run outside the lock to prevent lock-order inversion
        return extracted.size();
    }

    void ClearAll() {
        std::unordered_map<uintptr_t, std::unordered_map<std::string, ErasedFieldSlot>> extracted;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            extracted.swap(m_storage);
        }
    }

    size_t GetTrackedEntityCount() const {
        std::lock_guard<std::mutex> lock(m_mutex);
        return m_storage.size();
    }

private:
    EntityFieldContainer() = default;

    mutable std::mutex m_mutex;
    std::unordered_map<uintptr_t, std::unordered_map<std::string, ErasedFieldSlot>> m_storage;
};

} // namespace kongor::native_guard
