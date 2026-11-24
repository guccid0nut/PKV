// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>
#include <cstdint>

#ifdef __cplusplus
// K2 MSVC 64-bit std::basic_string<wchar_t> memory layout helper
inline const wchar_t* GetK2StringData(const void* pStr) {
    if (!pStr) return L"";
    __try {
        const size_t capacity = *reinterpret_cast<const size_t*>((const uint8_t*)pStr + 0x18);
        const size_t length = *reinterpret_cast<const size_t*>((const uint8_t*)pStr + 0x10);
        if (capacity >= 8 && capacity < 65536 && length <= capacity) {
            const wchar_t* const* ppHeap = reinterpret_cast<const wchar_t* const*>(pStr);
            return (ppHeap && *ppHeap) ? *ppHeap : L"";
        } else if (capacity < 8 && length <= 7) {
            return reinterpret_cast<const wchar_t*>(pStr);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return L"";
    }
    return L"";
}

inline size_t GetK2StringLength(const void* pStr) {
    if (!pStr) return 0;
    __try {
        return *reinterpret_cast<const size_t*>((const uint8_t*)pStr + 0x10);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// Returns true iff a virtual K2 archive path (starting with ":/" or ":\") contains uppercase
// ASCII in its directory prefix (e.g., ":/KONGOR/updates/textures.s2z" or ":/KONGOR/updates/").
// K2's native CArchive::ContainsFile (k2_x64.dll+0x679610) lowercases incoming query paths
// via tolower() before a case-sensitive wstring::compare against m_sPathToArchive (+0x28),
// causing 100% lookup misses on uppercase virtual mod prefixes while lowercase ":/game/*.s2z"
// and Windows filesystem drive paths ("C:\...") are unaffected.
inline bool Kongor_HasUppercaseVirtualArchivePrefixW(const wchar_t* virtualOrArchPath) {
    if (!virtualOrArchPath || virtualOrArchPath[0] != L':' ||
        (virtualOrArchPath[1] != L'/' && virtualOrArchPath[1] != L'\\')) {
        return false;
    }
    const wchar_t* lastSep = nullptr;
    for (const wchar_t* p = virtualOrArchPath + 2; *p != L'\0'; ++p) {
        if (*p == L'/' || *p == L'\\') {
            lastSep = p;
        }
    }
    if (!lastSep) {
        return false;
    }
    for (const wchar_t* p = virtualOrArchPath + 2; p < lastSep; ++p) {
        if (*p >= L'A' && *p <= L'Z') {
            return true;
        }
    }
    return false;
}

extern "C" {
#endif

// Installs miniz and libdeflate-backed modern archive hooks into k2_x64.dll
// Replaces CMMapUnzip backend to remove the 1.5 GB file size limit,
// support Zip64 archives, prevent 32-bit signed offset overflow crashes,
// provide zero-allocation <= 5ns path lookup, and direct AVX2 decompression.
bool InstallModernArchiveHooks(HMODULE hK2);

// Asynchronous watcher thread that waits for k2_x64.dll to load if not yet present
DWORD WINAPI ModernArchiveWatcherWorker(LPVOID lpParam);

// Helper for 64-bit inline detours (shared across native guard modules)
bool InstallInlineHook64(void* pTarget, void* pHook, void** ppOrigTrampoline, size_t stolenBytes);

// Emits comprehensive VFS telemetry summary to miniz_archive.log
void LogArchiveTelemetrySummary();

// Non-blocking try_lock-only flush of miniz_archive.log (safe for deadline watchdog pre-termination path)
void TryFlushArchiveLogNoBlock() noexcept;

// Emits ordered searchpath mount list (Xash3D FS_Path_f equivalent) (#135)
void LogArchiveSearchPaths();

// Searches all mounted archives for a virtual path or wildcard pattern and flags shadowed duplicates (Xash3D FS_FindFile_f) (#135)
void LogArchiveFindFile(const char* pattern);

// Exported query for MCP / CLI tools to inspect multi-archive shadowing (#135)
__declspec(dllexport) uint32_t Kongor_NativeGuard_VfsFindFile(const char* pattern, char* outBuf, uint32_t maxLen);

// Xash3D COM_CheckNastyPath path traversal & reserved DOS device firewall (#144, satisfies: OBL-W3-01)
bool Kongor_CheckNastyPathA(const char* path, bool allowLeadingSlash);
bool Kongor_CheckNastyPathW(const wchar_t* path, bool allowLeadingSlash);

// Xash3D matchpattern wildcard matcher (* and ?) (#135)
bool Kongor_MatchWildcardA(const char* str, const char* pattern, bool caseSensitive);

// Xash3D Q_vsnprintf SEH-guarded format string wrapper (#144, // satisfies: OBL-W3-06)
int SehSafeVsnprintf(char* buffer, size_t count, const char* format, va_list args) noexcept;

// Mod 5 (cg_customDamageFloats) & Mod 17 (cg_stumpsTrees) VFS redirection toggles
void Kongor_SetStumpsTreesEnabled(bool enabled);
void Kongor_SetCustomDamageFloatsEnabled(bool enabled);
bool Kongor_IsStumpsTreesEnabled();
bool Kongor_IsCustomDamageFloatsEnabled();

#ifdef __cplusplus
}
#endif


