// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <string>

namespace Kongor {

struct VfsLooseCacheStats {
    uint64_t TotalCalls;
    uint64_t FastNegativeMisses;
    uint64_t PositivePassThroughs;
    uint64_t BypassPassThroughs;
    uint32_t IndexedEntries;
    uint32_t IndexedRoots;
    float    BuildDurationMs;
    bool     Ready;
    bool     Enabled;
};

using PfnWaccess = int (__cdecl *)(const wchar_t*, int);

// Initializes the read-only loose-file existence index for static K2 mount roots
// (game/, base/, ClientPackageFiles/, ../shared/, KONGOR/extensions/).
// Dynamic/mutable roots (override/, userdata/, KONGOR/updates/, etc.) always bypass the cache.
void InitializeVfsLooseCache(const std::wstring& gameRootDir = L"", bool synchronousForTest = false);

// Shuts down background worker thread (when !isProcessTermination) and flushes lock-free telemetry summary.
void ShutdownVfsLooseCache(bool isProcessTermination = false);

// Flushes lock-free Win32 WriteFile summary to userdata/logs/vfs_loose_cache.log.
void LogVfsLooseCacheTelemetrySummary();

// Triggers an asynchronous background refresh of the read-only loose-file snapshot (e.g. on ReloadInterfaces).
void RebuildVfsLooseCacheAsync();

// PatchController group toggle ("vfs_loose_cache")
void SetVfsLooseCacheEnabled(bool enabled);
bool IsVfsLooseCacheEnabled();

// Returns current atomic statistics snapshot.
VfsLooseCacheStats GetVfsLooseCacheStats();

// Zero-allocation stack path normalizer (ASCII lowercase, '/' separators, '.' & '..' resolved, trailing '/' stripped).
// Returns false on null/empty, >1023 chars, non-ASCII (>=0x80), wildcards ('*','?'), or UNC/device prefixes.
bool NormalizePathForLooseCacheW(const wchar_t* rawPath, wchar_t* outBuf, size_t outCap, size_t* outLen) noexcept;

// Evaluates _waccess(path, mode) against the published read-only loose-file existence snapshot.
// - On miss inside an indexed read-only root (mode == 0): sets errno = ENOENT and returns -1 in user-space (~20 ns).
// - On hit inside an indexed read-only root, or any path outside indexed roots (override/, userdata/, KONGOR/updates/):
//   delegates directly to realWaccess(path, mode) (0 false positives, 100% live hot-reload fidelity).
int EvaluateVfsLooseWaccess(const wchar_t* path, int mode, PfnWaccess realWaccess) noexcept;

} // namespace Kongor
