// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// Project Kongor - Immutable String Interning Pool for O(1) Pointer Equality
// (Option L: rz_str_constpool.h)
// ============================================================================
#pragma once
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_set>

namespace Kongor {

class ConstStringPool {
public:
    static ConstStringPool& Instance() {
        static ConstStringPool s_pool;
        return s_pool;
    }

    // Interns a C-string into an immutable std::unordered_set<std::string> node
    // (C++11 guarantees node address stability across rehashing) so two interned
    // strings can be compared in O(1) via raw pointer equality (a == b).
    const char* Intern(const char* str) {
        if (!str) return "";
        std::lock_guard<std::mutex> lock(m_mutex);
        auto it = m_pool.emplace(str).first;
        return it->c_str();
    }

private:
    ConstStringPool() = default;
    std::mutex m_mutex;
    std::unordered_set<std::string> m_pool;
};

} // namespace Kongor
