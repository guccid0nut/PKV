// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#include "modern_archive_hook.h"
#include "native_guard_internal.h"
#include "runtime_patch_controller.h"
#include "kongor_assert.h"
#include "kongor_overflow.h"
#include "kongor_constpool.h"
#include <miniz.h>
#include <libdeflate.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <shared_mutex>
#include <mutex>
#include <memory>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <atomic>
#include <psapi.h>

// ============================================================================
// LOGGING, SEH-SAFE VSNPRINTF, NASTY-PATH FIREWALL & DDS MIPMAP GUARD
// ============================================================================

// Pure C SEH wrapper around vsnprintf (Xash3D Q_vsnprintf pattern) (#144)
// satisfies: OBL-W3-06
static int SehSafeVsnprintfInner(char* buf, size_t bufSize, const char* fmt, va_list args) noexcept {
    __try {
        int written = vsnprintf(buf, bufSize, fmt, args);
        buf[bufSize - 1] = '\0';
        return written;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        const char* fallback = "[KONGOR ARCHIVE] <vsnprintf exception neutralized>\n";
        strncpy_s(buf, bufSize, fallback, _TRUNCATE);
        return -1;
    }
}

extern "C" int SehSafeVsnprintf(char* buf, size_t bufSize, const char* fmt, va_list args) noexcept {
    if (!buf || bufSize == 0) return 0;
    if (!fmt) {
        buf[0] = '\0';
        return 0;
    }
    KongorScopedProtectedSeh sehGuard;
    return SehSafeVsnprintfInner(buf, bufSize, fmt, args);
}

static FILE* s_archiveLogFp = nullptr;
static std::mutex s_archiveLogMutex;
static ULONGLONG s_lastArchiveLogFlushTick = 0;

static void FlushArchiveLog() {
    std::unique_lock<std::mutex> lock(s_archiveLogMutex, std::try_to_lock);
    if (!lock.owns_lock()) return;
    if (s_archiveLogFp) {
        fflush(s_archiveLogFp);
        s_lastArchiveLogFlushTick = GetTickCount64();
    }
}

extern "C" void TryFlushArchiveLogNoBlock() noexcept {
    try {
        FlushArchiveLog();
    } catch (...) {
    }
}

static void LogArchive(const char* fmt, ...) {
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    SehSafeVsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);

    OutputDebugStringA(buf);

    std::lock_guard<std::mutex> lock(s_archiveLogMutex);
    static bool s_attemptedOpen = false;
    if (!s_attemptedOpen) {
        s_attemptedOpen = true;
        CreateDirectoryA("userdata", NULL);
        CreateDirectoryA("userdata/logs", NULL);
        std::error_code ec;
        std::filesystem::path logPath("userdata/logs/miniz_archive.log");
        std::filesystem::path bakPath("userdata/logs/miniz_archive.log.bak");
        if (std::filesystem::exists(logPath, ec) && std::filesystem::file_size(logPath, ec) > (10ULL * 1024ULL * 1024ULL)) {
            std::filesystem::remove(bakPath, ec);
            std::filesystem::rename(logPath, bakPath, ec);
        }
        s_archiveLogFp = fopen("userdata/logs/miniz_archive.log", "a");
    }
    if (s_archiveLogFp) {
        fputs(buf, s_archiveLogFp);
        ULONGLONG now = GetTickCount64();
        if (strstr(buf, "[ERROR]") != nullptr || (now - s_lastArchiveLogFlushTick >= 1000)) {
            fflush(s_archiveLogFp);
            s_lastArchiveLogFlushTick = now;
        }
    }
}

static inline char ToLowerAsciiA(char c) {
    return (c >= 'A' && c <= 'Z') ? static_cast<char>(c + 32) : c;
}

static inline bool IsReservedDosStemA(const char* seg, size_t stemLen) {
    if (stemLen == 3) {
        char c0 = ToLowerAsciiA(seg[0]);
        char c1 = ToLowerAsciiA(seg[1]);
        char c2 = ToLowerAsciiA(seg[2]);
        if ((c0 == 'c' && c1 == 'o' && c2 == 'n') ||
            (c0 == 'p' && c1 == 'r' && c2 == 'n') ||
            (c0 == 'a' && c1 == 'u' && c2 == 'x') ||
            (c0 == 'n' && c1 == 'u' && c2 == 'l')) {
            return true;
        }
    } else if (stemLen == 4 && seg[3] >= '1' && seg[3] <= '9') {
        char c0 = ToLowerAsciiA(seg[0]);
        char c1 = ToLowerAsciiA(seg[1]);
        char c2 = ToLowerAsciiA(seg[2]);
        if ((c0 == 'c' && c1 == 'o' && c2 == 'm') ||
            (c0 == 'l' && c1 == 'p' && c2 == 't')) {
            return true;
        }
    }
    return false;
}

// Xash3D COM_CheckNastyPath traversal & reserved DOS device firewall (#144)
// satisfies: OBL-W3-01
extern "C" bool Kongor_CheckNastyPathA(const char* path, bool allowLeadingSlash) {
    if (!path || path[0] == '\0') return false;
    size_t len = strlen(path);
    if (len > 1024) return false;

    if (!allowLeadingSlash && (path[0] == '/' || path[0] == '\\')) {
        return false;
    }

    for (size_t i = 0; i < len; ++i) {
        unsigned char c = static_cast<unsigned char>(path[i]);
        if (c < 0x20 || c == 0x7F || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') {
            return false;
        }
    }

    size_t segStart = 0;
    while (segStart <= len) {
        size_t segEnd = segStart;
        while (segEnd < len && path[segEnd] != '/' && path[segEnd] != '\\') {
            ++segEnd;
        }
        size_t segLen = segEnd - segStart;
        if (segLen == 0) {
            if (!(segStart == 0 && allowLeadingSlash && len > 1)) {
                return false;
            }
        } else {
            const char* seg = path + segStart;
            if ((segLen == 1 && seg[0] == '.') ||
                (segLen == 2 && seg[0] == '.' && seg[1] == '.')) {
                return false;
            }
            char lastChar = seg[segLen - 1];
            if (lastChar == '.' || lastChar == ' ') {
                return false;
            }
            size_t stemLen = segLen;
            for (size_t k = 0; k < segLen; ++k) {
                if (seg[k] == '.') {
                    stemLen = k;
                    break;
                }
            }
            if (IsReservedDosStemA(seg, stemLen)) {
                return false;
            }
        }
        segStart = segEnd + 1;
    }
    return true;
}

extern "C" bool Kongor_CheckNastyPathW(const wchar_t* path, bool allowLeadingSlash) {
    if (!path || path[0] == L'\0') return false;
    char utf8Buf[1028] = { 0 };
    int written = WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8Buf, sizeof(utf8Buf), NULL, NULL);
    if (written <= 0) return false;
    return Kongor_CheckNastyPathA(utf8Buf, allowLeadingSlash);
}

// Xash3D matchpattern_with_separator linear-time wildcard glob matcher (#135, #144)
extern "C" bool Kongor_MatchWildcardA(const char* input, const char* pattern, bool caseInsensitive) {
    if (!input || !pattern) return false;
    size_t i = 0;
    size_t p = 0;
    size_t inLen = strlen(input);
    size_t patLen = strlen(pattern);
    size_t starIdx = static_cast<size_t>(-1);
    size_t matchIdx = 0;

    auto charsEqual = [caseInsensitive](char a, char b) -> bool {
        char na = (a == '\\') ? '/' : a;
        char nb = (b == '\\') ? '/' : b;
        if (caseInsensitive) {
            na = ToLowerAsciiA(na);
            nb = ToLowerAsciiA(nb);
        }
        return na == nb;
    };

    while (i < inLen) {
        if (p < patLen && (pattern[p] == '?' || charsEqual(input[i], pattern[p]))) {
            ++i;
            ++p;
        } else if (p < patLen && pattern[p] == '*') {
            starIdx = p;
            matchIdx = i;
            ++p;
        } else if (starIdx != static_cast<size_t>(-1)) {
            p = starIdx + 1;
            ++matchIdx;
            i = matchIdx;
        } else {
            return false;
        }
    }

    while (p < patLen && pattern[p] == '*') {
        ++p;
    }
    return p == patLen;
}

// Pre-blit DDS/DX10 mipmap pyramid byte-size verifier (Xash3D img_dds.c Image_DXTCalcSize) (#142)
// satisfies: OBL-W3-06
static bool VerifyDdsMipmapChainSize(const uint8_t* data, size_t actualSize, size_t& outExpectedMinSize) {
    outExpectedMinSize = 128;
    if (!data || actualSize < 128) return false;

    uint32_t magic = *reinterpret_cast<const uint32_t*>(data);
    uint32_t dwSize = *reinterpret_cast<const uint32_t*>(data + 4);
    if (magic != 0x20534444u || dwSize != 124u) { // "DDS "
        return false;
    }

    uint32_t height = *reinterpret_cast<const uint32_t*>(data + 12);
    uint32_t width = *reinterpret_cast<const uint32_t*>(data + 16);
    uint32_t mipMapCount = *reinterpret_cast<const uint32_t*>(data + 28);
    uint32_t pfFlags = *reinterpret_cast<const uint32_t*>(data + 80);
    uint32_t fourCC = *reinterpret_cast<const uint32_t*>(data + 84);
    uint32_t rgbBitCount = *reinterpret_cast<const uint32_t*>(data + 88);
    uint32_t caps2 = *reinterpret_cast<const uint32_t*>(data + 112);

    if (width == 0 || height == 0 || width > 16384 || height > 16384) return false;
    uint32_t mips = (mipMapCount == 0 || mipMapCount > 16) ? 1u : mipMapCount;

    size_t headerBytes = 128;
    bool isBlockCompressed = false;
    size_t blockBytes = 16;
    size_t bytesPerPixel = 4;

    if ((pfFlags & 0x4u) != 0) { // DDPF_FOURCC
        if (fourCC == 0x30315844u) { // "DX10"
            headerBytes = 148;
            if (actualSize < headerBytes) {
                outExpectedMinSize = headerBytes;
                return false;
            }
            uint32_t dxgiFormat = *reinterpret_cast<const uint32_t*>(data + 128);
            // BC1 (70..72) & BC4 (79..81) = 8B/block; BC2/BC3/BC5/BC6H/BC7 = 16B/block
            if ((dxgiFormat >= 70 && dxgiFormat <= 72) || (dxgiFormat >= 79 && dxgiFormat <= 81)) {
                isBlockCompressed = true;
                blockBytes = 8;
            } else if ((dxgiFormat >= 73 && dxgiFormat <= 78) || (dxgiFormat >= 82 && dxgiFormat <= 99)) {
                isBlockCompressed = true;
                blockBytes = 16;
            }
        } else if (fourCC == 0x31545844u || fourCC == 0x31495441u || fourCC == 0x55344342u) { // DXT1 / ATI1 / BC4U
            isBlockCompressed = true;
            blockBytes = 8;
        } else if (fourCC == 0x32545844u || fourCC == 0x33545844u ||
                   fourCC == 0x34545844u || fourCC == 0x35545844u ||
                   fourCC == 0x32495441u || fourCC == 0x55354342u) { // DXT2..5 / ATI2 / BC5U
            isBlockCompressed = true;
            blockBytes = 16;
        }
    } else if (rgbBitCount >= 8 && rgbBitCount <= 128) {
        bytesPerPixel = (rgbBitCount + 7u) / 8u;
    }

    size_t singleFaceBytes = 0;
    uint32_t w = width;
    uint32_t h = height;
    for (uint32_t m = 0; m < mips; ++m) {
        uint32_t mw = (w > 1u) ? w : 1u;
        uint32_t mh = (h > 1u) ? h : 1u;
        size_t levelBytes = isBlockCompressed
            ? (static_cast<size_t>((mw + 3u) / 4u) * static_cast<size_t>((mh + 3u) / 4u) * blockBytes)
            : (static_cast<size_t>(mw) * static_cast<size_t>(mh) * bytesPerPixel);
        singleFaceBytes += levelBytes;
        w >>= 1u;
        h >>= 1u;
    }

    size_t faceCount = ((caps2 & 0x00000200u) != 0) ? 6u : 1u; // DDSCAPS2_CUBEMAP
    outExpectedMinSize = headerBytes + (singleFaceBytes * faceCount);
    return actualSize >= outExpectedMinSize;
}

static bool HasDdsExtensionA(const char* filename) {
    if (!filename) return false;
    size_t len = strlen(filename);
    if (len < 4) return false;
    return _stricmp(filename + (len - 4), ".dds") == 0;
}


// (K2 String helpers declared in modern_archive_hook.h)

// ============================================================================
// ZERO-ALLOCATION PATH NORMALIZATION & 64-BIT FNV-1A HASHING (<= 5ns)
// ============================================================================

static constexpr uint64_t FNV_OFFSET_BASIS = 14695981039346656037ULL;
static constexpr uint64_t FNV_PRIME = 1099511628211ULL;

// Strips K2 virtual prefixes: '/', '\', '@', '#', '~', ':'
inline const wchar_t* StripVirtualPrefixW(const wchar_t* p) {
    if (!p) return L"";
    while (*p == L'/' || *p == L'\\' || *p == L'@' || *p == L'#' || *p == L'~' || *p == L':') {
        p++;
    }
    return p;
}

inline const char* StripVirtualPrefixA(const char* p) {
    if (!p) return "";
    while (*p == '/' || *p == '\\' || *p == '@' || *p == '#' || *p == '~' || *p == ':') {
        p++;
    }
    return p;
}

// Strips a directory/mod prefix (e.g. L"game" or L"/maps/") slash-agnostically and case-insensitively
inline const wchar_t* StripPathPrefixW(const wchar_t* p, const wchar_t* prefix) {
    if (!p || !prefix) return p;
    p = StripVirtualPrefixW(p);
    prefix = StripVirtualPrefixW(prefix);
    if (!*prefix) return p;

    const wchar_t* curP = p;
    const wchar_t* curPre = prefix;
    while (*curPre) {
        if (*curPre == L'/' || *curPre == L'\\') {
            while (*curPre == L'/' || *curPre == L'\\') curPre++;
            if (!*curPre) break;
            if (*curP != L'/' && *curP != L'\\') return p;
            while (*curP == L'/' || *curP == L'\\') curP++;
            continue;
        }
        wchar_t cp = *curP;
        wchar_t cpre = *curPre;
        if (cp >= L'A' && cp <= L'Z') cp += (L'a' - L'A');
        if (cpre >= L'A' && cpre <= L'Z') cpre += (L'a' - L'A');
        if (cp != cpre) return p;
        curP++;
        curPre++;
    }
    if (*curP == L'/' || *curP == L'\\') {
        while (*curP == L'/' || *curP == L'\\') curP++;
        return curP;
    }
    return p;
}

inline const wchar_t* StripMasterPrefixW(const wchar_t* p, const std::wstring& masterCleanW) {
    if (!p || masterCleanW.empty()) return p;
    return StripPathPrefixW(p, masterCleanW.c_str());
}

// Case-insensitive, path-normalizing 64-bit FNV-1a hash of wide string (UTF-16)
// Performs on-the-fly normalization: '\' -> '/', collapses duplicate '/', lowercases ASCII,
// encodes > 127 to UTF-8 stream so wide and UTF-8 hashes match identically.
// Zero heap allocations.
inline uint64_t HashNormalizedPathW(const wchar_t* p) {
    if (!p || !*p) return 0;
    p = StripVirtualPrefixW(p);
    if (!*p) return 0;

    uint64_t h = FNV_OFFSET_BASIS;
    wchar_t prev = 0;
    while (*p) {
        wchar_t c = *p++;
        if (c == L'\\') c = L'/';
        if (c >= L'A' && c <= L'Z') c += (L'a' - L'A');
        if (c == L'/' && prev == L'/') continue;
        prev = c;

        if (c <= 0x7F) {
            h = (h ^ (uint8_t)c) * FNV_PRIME;
        } else if (c <= 0x7FF) {
            h = (h ^ (uint8_t)(0xC0 | (c >> 6))) * FNV_PRIME;
            h = (h ^ (uint8_t)(0x80 | (c & 0x3F))) * FNV_PRIME;
        } else {
            h = (h ^ (uint8_t)(0xE0 | (c >> 12))) * FNV_PRIME;
            h = (h ^ (uint8_t)(0x80 | ((c >> 6) & 0x3F))) * FNV_PRIME;
            h = (h ^ (uint8_t)(0x80 | (c & 0x3F))) * FNV_PRIME;
        }
    }
    return h;
}

// Case-insensitive, path-normalizing 64-bit FNV-1a hash of ASCII/UTF-8 string.
// Zero heap allocations.
inline uint64_t HashNormalizedPathA(const char* p) {
    if (!p || !*p) return 0;
    p = StripVirtualPrefixA(p);
    if (!*p) return 0;

    uint64_t h = FNV_OFFSET_BASIS;
    char prev = 0;
    while (*p) {
        char c = *p++;
        if (c == '\\') c = '/';
        if (c >= 'A' && c <= 'Z') c += ('a' - 'A');
        if (c == '/' && prev == '/') continue;
        prev = c;
        h = (h ^ (uint8_t)c) * FNV_PRIME;
    }
    return h;
}

static std::string NormalizePath(const std::string& path) {
    std::string s;
    s.reserve(path.size());
    for (char c : path) {
        if (c == '\\') c = '/';
        if (c >= 'A' && c <= 'Z') c += ('a' - 'A');
        if (c == '/' && !s.empty() && s.back() == '/') continue;
        s.push_back(c);
    }
    size_t start = 0;
    while (start < s.size() && s[start] == '/') {
        start++;
    }
    return s.substr(start);
}

static std::string NormalizeWPath(const wchar_t* wszPath) {
    if (!wszPath) return "";
    std::string s;
    size_t len = wcslen(wszPath);
    s.reserve(len);
    for (size_t i = 0; i < len; ++i) {
        wchar_t c = wszPath[i];
        if (c == L'\\') c = L'/';
        if (c >= L'A' && c <= L'Z') c += (L'a' - L'A');
        if (c == L'/' && !s.empty() && s.back() == '/') continue;
        s.push_back((char)c);
    }
    size_t start = 0;
    while (start < s.size() && s[start] == '/') {
        start++;
    }
    return s.substr(start);
}

// ============================================================================
// CACHE-ALIGNED FLAT OPEN-ADDRESSING HASH MAP (<= 5ns Zero-Allocation)
// ============================================================================

static constexpr uint32_t FLAT_MAP_EMPTY = 0xFFFFFFFF;

struct FlatHashSlot {
    uint64_t hash;
    uint32_t fileIndex;
    uint32_t pad;
};
static_assert(sizeof(FlatHashSlot) == 16, "FlatHashSlot must be exactly 16 bytes for 64-byte cache alignment");

class FlatArchiveMap {
public:
    FlatArchiveMap() : m_slots(nullptr), m_capacity(0), m_size(0) {}
    ~FlatArchiveMap() {
        if (m_slots) {
            _aligned_free(m_slots);
            m_slots = nullptr;
        }
    }

    FlatArchiveMap(const FlatArchiveMap&) = delete;
    FlatArchiveMap& operator=(const FlatArchiveMap&) = delete;

    FlatArchiveMap(FlatArchiveMap&& other) noexcept
        : m_slots(other.m_slots), m_capacity(other.m_capacity), m_size(other.m_size) {
        other.m_slots = nullptr;
        other.m_capacity = 0;
        other.m_size = 0;
    }

    FlatArchiveMap& operator=(FlatArchiveMap&& other) noexcept {
        if (this != &other) {
            if (m_slots) _aligned_free(m_slots);
            m_slots = other.m_slots;
            m_capacity = other.m_capacity;
            m_size = other.m_size;
            other.m_slots = nullptr;
            other.m_capacity = 0;
            other.m_size = 0;
        }
        return *this;
    }

    void Init(size_t expectedEntries) {
        if (m_slots) {
            _aligned_free(m_slots);
            m_slots = nullptr;
        }
        size_t cap = 32;
        while (cap < (expectedEntries * 2 + 32)) {
            cap <<= 1;
        }
        if (KONGOR_SZT_MUL_OVFCHK(cap, sizeof(FlatHashSlot))) {
            LogArchive("[KONGOR ARCHIVE] [OVERFLOW GUARD] FlatArchiveMap capacity %zu * %zu overflowed!\n", cap, sizeof(FlatHashSlot));
            m_capacity = 0;
            m_size = 0;
            return;
        }
        m_capacity = cap;
        m_size = 0;
        m_slots = static_cast<FlatHashSlot*>(_aligned_malloc(m_capacity * sizeof(FlatHashSlot), 64));
        if (m_slots) {
            for (size_t i = 0; i < m_capacity; ++i) {
                m_slots[i].hash = 0;
                m_slots[i].fileIndex = FLAT_MAP_EMPTY;
                m_slots[i].pad = 0;
            }
        }
    }

    inline void Insert(uint64_t hash, uint32_t fileIndex) {
        if (!m_slots || m_capacity == 0 || hash == 0) return;
        size_t mask = m_capacity - 1;
        size_t idx = hash & mask;
        while (m_slots[idx].fileIndex != FLAT_MAP_EMPTY) {
            if (m_slots[idx].hash == hash) {
                m_slots[idx].fileIndex = fileIndex;
                return;
            }
            idx = (idx + 1) & mask;
        }
        m_slots[idx].hash = hash;
        m_slots[idx].fileIndex = fileIndex;
        m_size++;
    }

    inline bool Find(uint64_t hash, uint32_t& outFileIndex) const {
        if (!m_slots || m_capacity == 0 || hash == 0) return false;
        size_t mask = m_capacity - 1;
        size_t idx = hash & mask;
        while (m_slots[idx].fileIndex != FLAT_MAP_EMPTY) {
            if (m_slots[idx].hash == hash) {
                outFileIndex = m_slots[idx].fileIndex;
                return true;
            }
            idx = (idx + 1) & mask;
        }
        return false;
    }

    inline size_t Size() const { return m_size; }
    inline size_t Capacity() const { return m_capacity; }

private:
    FlatHashSlot* m_slots;
    size_t m_capacity;
    size_t m_size;
};

// ============================================================================
// SYSTEM PATH RESOLVER
// ============================================================================
static std::wstring GetExecutableDirectory() {
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);
    wchar_t* pLastSlash = wcsrchr(exePath, L'\\');
    if (!pLastSlash) pLastSlash = wcsrchr(exePath, L'/');
    if (pLastSlash) {
        *pLastSlash = L'\0';
    }
    return exePath;
}

static HMODULE g_hK2Module = nullptr;

// Pure C SEH helper to safely read memory addresses without triggering C2712 or false VEH dumps
static void* SafeReadPointerInner(void* pAddr) noexcept {
    __try {
        return *reinterpret_cast<void**>(pAddr);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

static inline void* SafeReadPointer(void* pAddr) noexcept {
    if (!pAddr) return nullptr;
    KongorScopedProtectedSeh sehGuard;
    return SafeReadPointerInner(pAddr);
}

static std::wstring ResolveArchiveSystemPath(const wchar_t* wszPath, const wchar_t* wszMaster) {
    if (!wszPath || !wszPath[0]) return L"";

    if (GetFileAttributesW(wszPath) != INVALID_FILE_ATTRIBUTES) {
        return wszPath;
    }

    std::wstring exeDir = GetExecutableDirectory();
    std::wstring rawPath = wszPath;

    wchar_t prefix = L'\0';
    size_t offset = 0;
    if (rawPath.size() >= 2 && rawPath[0] == L':' && (rawPath[1] == L'/' || rawPath[1] == L'\\')) {
        prefix = L':';
        offset = 2;
    } else if (rawPath[0] == L':') {
        prefix = L':';
        offset = 1;
    } else if (rawPath.size() >= 2 && rawPath[0] == L'#' && (rawPath[1] == L'/' || rawPath[1] == L'\\')) {
        prefix = L'#';
        offset = 2;
    } else if (rawPath[0] == L'#') {
        prefix = L'#';
        offset = 1;
    } else if (rawPath.size() >= 2 && rawPath[0] == L'~' && (rawPath[1] == L'/' || rawPath[1] == L'\\')) {
        prefix = L'~';
        offset = 2;
    } else if (rawPath[0] == L'~') {
        prefix = L'~';
        offset = 1;
    }

    while (offset < rawPath.size() && (rawPath[offset] == L'/' || rawPath[offset] == L'\\')) {
        offset++;
    }

    std::wstring cleanPath = rawPath.substr(offset);
    for (auto& c : cleanPath) {
        if (c == L'/') c = L'\\';
    }

    // Direct OS path after stripping K2's leading '/' (e.g. "/c:/.../file.s2z" -> "c:\...\file.s2z")
    if (!cleanPath.empty() && GetFileAttributesW(cleanPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        return cleanPath;
    }

    std::wstring cleanMaster = wszMaster ? wszMaster : L"";
    while (!cleanMaster.empty() && (cleanMaster[0] == L'/' || cleanMaster[0] == L'\\' || cleanMaster[0] == L':')) {
        cleanMaster.erase(0, 1);
    }
    while (!cleanMaster.empty() && (cleanMaster.back() == L'/' || cleanMaster.back() == L'\\')) {
        cleanMaster.pop_back();
    }
    for (auto& c : cleanMaster) {
        if (c == L'/') c = L'\\';
    }

    std::vector<std::wstring> candidates;

    // Check K2System::m_sRootDir (+0x480) if k2_x64.dll is loaded
    if (g_hK2Module) {
        void* pK2System = SafeReadPointer(reinterpret_cast<uint8_t*>(g_hK2Module) + 0x1279EB8);
        if (pK2System) {
            const wchar_t* wszRootDir = GetK2StringData(reinterpret_cast<uint8_t*>(pK2System) + 0x480);
            if (wszRootDir && wszRootDir[0]) {
                std::wstring k2Root(wszRootDir);
                for (auto& c : k2Root) {
                    if (c == L'/') c = L'\\';
                }
                while (!k2Root.empty() && k2Root.back() == L'\\') {
                    k2Root.pop_back();
                }
                if (!k2Root.empty()) {
                    if (!cleanMaster.empty()) {
                        candidates.push_back(k2Root + L"\\" + cleanMaster + L"\\" + cleanPath);
                    }
                    candidates.push_back(k2Root + L"\\" + cleanPath);
                    candidates.push_back(k2Root + L"\\game\\" + cleanPath);
                    candidates.push_back(k2Root + L"\\base\\" + cleanPath);
                }
            }
        }
    }

    if (prefix == L'#') {
        candidates.push_back(exeDir + L"\\userdata\\Heroes of Newerth x64\\game\\" + cleanPath);
        candidates.push_back(exeDir + L"\\userdata\\Heroes of Newerth x64\\base\\" + cleanPath);
        candidates.push_back(exeDir + L"\\userdata\\Heroes of Newerth x64\\override\\" + cleanPath);
        candidates.push_back(exeDir + L"\\userdata\\Heroes of Newerth x64\\KONGOR\\extensions\\" + cleanPath);
        candidates.push_back(exeDir + L"\\userdata\\Heroes of Newerth x64\\" + cleanPath);
        candidates.push_back(exeDir + L"\\userdata\\" + cleanPath);
    }

    if (!cleanMaster.empty()) {
        candidates.push_back(exeDir + L"\\" + cleanMaster + L"\\" + cleanPath);
    }

    candidates.push_back(exeDir + L"\\" + cleanPath);
    candidates.push_back(exeDir + L"\\game\\" + cleanPath);
    candidates.push_back(exeDir + L"\\base\\" + cleanPath);
    candidates.push_back(exeDir + L"\\editor\\" + cleanPath);

    wchar_t cwd[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, cwd);
    std::wstring sCwd(cwd);
    if (!cleanMaster.empty()) {
        candidates.push_back(sCwd + L"\\" + cleanMaster + L"\\" + cleanPath);
    }
    candidates.push_back(sCwd + L"\\" + cleanPath);
    candidates.push_back(sCwd + L"\\game\\" + cleanPath);
    candidates.push_back(sCwd + L"\\base\\" + cleanPath);

    for (const auto& cand : candidates) {
        if (GetFileAttributesW(cand.c_str()) != INVALID_FILE_ATTRIBUTES) {
            return cand;
        }
    }

    return L"";
}

// ============================================================================
// MODERN ARCHIVE DATA STRUCTURE
// ============================================================================
struct ModernArchive {
    mz_zip_archive zip;
    bool is_zip64;
    bool has_s2zdir;
    bool synthesized_unzip_vec;
    void* dummy_unzip_ptr;
    uint64_t orig_vec_40;
    uint64_t orig_vec_48;
    uint64_t orig_vec_50;
    std::wstring disk_path;
    std::wstring s2zdir_path;
    std::wstring archive_path;
    std::wstring path_to_archive_w;
    std::wstring master_path;
    std::wstring master_clean_w;
    std::string master_path_utf8;

    // Fast O(1) cache-aligned flat index map (<= 5ns zero-allocation lookup)
    FlatArchiveMap flat_map;
    std::vector<std::string> file_list;
    std::vector<std::string> sorted_entries;

    mutable std::shared_mutex mutex;

    // Telemetry counters
    std::atomic<uint64_t> lookupsCount{ 0 };
    std::atomic<uint64_t> hitsCount{ 0 };
    std::atomic<uint64_t> totalLookupNs{ 0 };
    std::atomic<uint64_t> libdeflateCount{ 0 };
    std::atomic<uint64_t> minizFallbackCount{ 0 };
    std::atomic<uint64_t> storedDirectCount{ 0 };
    std::atomic<uint64_t> s2zdirOverrideCount{ 0 };
    std::atomic<uint64_t> canaryCorruptCount{ 0 };
    std::atomic<uint64_t> ddsTruncatedCount{ 0 };

    ModernArchive()
        : is_zip64(false),
          has_s2zdir(false),
          synthesized_unzip_vec(false),
          dummy_unzip_ptr(nullptr),
          orig_vec_40(0),
          orig_vec_48(0),
          orig_vec_50(0) {
        memset(&zip, 0, sizeof(zip));
    }

    ~ModernArchive() {
        if (zip.m_pState) {
            mz_zip_reader_end(&zip);
        }
    }
};

static std::unordered_map<void*, std::shared_ptr<ModernArchive>> g_archiveMap;
static std::unordered_set<void*> g_passthroughArchives;
static std::shared_mutex g_archiveMapMutex;
static std::atomic<uint32_t> g_registeredArchiveCount{0};

// ============================================================================
// INLINE HOOK INFRASTRUCTURE (64-BIT)
// ============================================================================
extern "C" bool InstallInlineHook64(void* pTarget, void* pHook, void** ppOrigTrampoline, size_t stolenBytes) {
    KONGOR_RETURN_VAL_IF_FAIL(pTarget != nullptr && pHook != nullptr && ppOrigTrampoline != nullptr && stolenBytes >= 14, false);
    return Kongor::PatchController::InstallManagedInlineHook64(
        "inline_legacy", "LegacyInlineHook", pTarget, pHook, ppOrigTrampoline, stolenBytes, nullptr, 0);
}

// Dynamic AOB pattern scanner fallback
static uint8_t* FindAobPattern(HMODULE hMod, const uint8_t* pattern, const char* mask) {
    if (!hMod || !pattern || !mask) return nullptr;
    MODULEINFO mi = { 0 };
    if (!GetModuleInformation(GetCurrentProcess(), hMod, &mi, sizeof(mi))) return nullptr;

    uint8_t* pBase = reinterpret_cast<uint8_t*>(mi.lpBaseOfDll);
    size_t size = mi.SizeOfImage;
    size_t patLen = strlen(mask);

    if (size < patLen) return nullptr;
    size_t searchLimit = size - patLen;

    for (size_t i = 0; i < searchLimit; ++i) {
        bool match = true;
        for (size_t j = 0; j < patLen; ++j) {
            if (mask[j] == 'x' && pBase[i + j] != pattern[j]) {
                match = false;
                break;
            }
        }
        if (match) {
            return pBase + i;
        }
    }
    return nullptr;
}

// Function pointer typedefs
typedef bool (__fastcall *FnCArchiveOpen)(void* pArchive, const void* pPathStr, int iMode, const void* pMasterStr);
typedef bool (__fastcall *FnCArchiveContainsFile)(void* pArchive, const void* pFilenameStr);
typedef int  (__fastcall *FnCArchiveReadFile)(void* pArchive, const void* pFilenameStr, char** ppBuffer);
typedef bool (__fastcall *FnCArchiveClose)(void* pArchive, unsigned int uiMode);
typedef void (__fastcall *FnCArchiveGetFileList)(void* pArchive, void* pFileListVec);
typedef const std::string* (__fastcall *FnCArchiveGetNextFileAtPath)(void* pArchive, void** pIterator, const void* pSearchPath);
typedef void* (__fastcall *FnK2StringWAssign)(void* pThis, const wchar_t* pPtr, size_t len);
typedef void  (__fastcall *FnK2StringWVecPushBackRval)(void* pVec, void* pTempK2StringW);
typedef void  (__fastcall *FnK2StringWDtor)(void* pThis);

static FnCArchiveOpen              g_origCArchiveOpen = nullptr;
static FnCArchiveContainsFile      g_origCArchiveContainsFile = nullptr;
static FnCArchiveReadFile          g_origCArchiveReadFile = nullptr;
static FnCArchiveClose             g_origCArchiveClose = nullptr;
static FnCArchiveGetFileList       g_origCArchiveGetFileList = nullptr;
static FnCArchiveGetNextFileAtPath g_origCArchiveGetNextFileAtPath = nullptr;
static FnK2StringWAssign           g_pK2StringWAssign = nullptr;
static FnK2StringWVecPushBackRval  g_pK2StringWVecPushBackRval = nullptr;
static FnK2StringWDtor             g_pK2StringWDtor = nullptr;

// Resolves a sanitized loose file inside <archive>.s2zdir/<vpath> (#135, #144)
// satisfies: OBL-W3-01
static bool TryResolveS2zDirLooseFileW(const std::shared_ptr<ModernArchive>& arch, const wchar_t* pRelPath, std::wstring& outFullDiskPath) {
    if (!arch || !arch->has_s2zdir || !pRelPath || pRelPath[0] == L'\0') return false;
    if (!Kongor_CheckNastyPathW(pRelPath, false)) return false;

    std::wstring rel(pRelPath);
    for (wchar_t& ch : rel) {
        if (ch == L'/') ch = L'\\';
    }
    std::wstring candidate = arch->s2zdir_path + L"\\" + rel;
    DWORD attrs = GetFileAttributesW(candidate.c_str());
    if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0 && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0) {
        outFullDiskPath = std::move(candidate);
        return true;
    }
    return false;
}

static constexpr uint64_t kModernArchiveThresholdBytes = 1610612736ULL; // 1.5 GiB

static bool IsForceMinizAllEnabled() {
    static int s_cached = -1;
    if (s_cached < 0) {
        char envBuf[16] = {0};
        DWORD len = GetEnvironmentVariableA("KONGOR_FORCE_MINIZ_ALL", envBuf, sizeof(envBuf));
        s_cached = (len > 0 && (envBuf[0] == '1' || envBuf[0] == 'y' || envBuf[0] == 'Y')) ? 1 : 0;
    }
    return s_cached == 1;
}

static bool ShouldUseModernArchive(
    const std::wstring& resolvedSystemPath,
    bool k2NativeOpened,
    bool hasS2zDir,
    bool hasUppercaseVirtualPrefix,
    uint64_t& outDiskBytes)
{
    outDiskBytes = 0;
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (GetFileAttributesExW(resolvedSystemPath.c_str(), GetFileExInfoStandard, &fad)) {
        outDiskBytes = (static_cast<uint64_t>(fad.nFileSizeHigh) << 32) | static_cast<uint64_t>(fad.nFileSizeLow);
    }
    if (!k2NativeOpened) return true;
    if (hasS2zDir) return true;
    if (hasUppercaseVirtualPrefix) return true;
    if (outDiskBytes >= kModernArchiveThresholdBytes) return true;
    if (IsForceMinizAllEnabled()) return true;
    return false;
}

// Pre-checks whether an archive on disk is >= 1.5 GiB or contains a Zip64 EOCD (cDirOffset/cDirSize == 0xFFFFFFFF).
// When fs_disableMMap == 0 (RVA 0x1295BB9), K2's CMMapUnzip::Initialize (RVA 0x945EDF) dereferences
// pMappedView + 0xFFFFFFFF if ARCHIVE_DISABLE_MMAP (0x80) is not set in iMode, causing a 0xC0000005 crash.
static bool IsArchiveZip64OrLargeOnDisk(const std::wstring& diskPath) {
    if (diskPath.empty()) return false;
    HANDLE hFile = CreateFileW(diskPath.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(hFile, &fileSize)) {
        CloseHandle(hFile);
        return false;
    }
    if (static_cast<uint64_t>(fileSize.QuadPart) >= kModernArchiveThresholdBytes) {
        CloseHandle(hFile);
        return true;
    }
    DWORD readLen = static_cast<DWORD>(fileSize.QuadPart < 65558 ? fileSize.QuadPart : 65558);
    if (readLen >= 22) {
        LARGE_INTEGER seekPos{};
        seekPos.QuadPart = fileSize.QuadPart - readLen;
        std::vector<uint8_t> tail(readLen);
        DWORD bytesRead = 0;
        if (SetFilePointerEx(hFile, seekPos, NULL, FILE_BEGIN) &&
            ReadFile(hFile, tail.data(), readLen, &bytesRead, NULL) && bytesRead >= 22) {
            for (long idx = static_cast<long>(bytesRead) - 22; idx >= 0; --idx) {
                uint32_t sig = 0;
                memcpy(&sig, &tail[idx], sizeof(uint32_t));
                if (sig == 0x06054b50u) {
                    uint16_t entriesDisk = 0, entriesTotal = 0;
                    uint32_t cDirSize = 0, cDirOffset = 0;
                    memcpy(&entriesDisk, &tail[idx + 8], 2);
                    memcpy(&entriesTotal, &tail[idx + 10], 2);
                    memcpy(&cDirSize, &tail[idx + 12], 4);
                    memcpy(&cDirOffset, &tail[idx + 16], 4);
                    if (entriesDisk == 0xFFFFu || entriesTotal == 0xFFFFu ||
                        cDirSize == 0xFFFFFFFFu || cDirOffset == 0xFFFFFFFFu) {
                        CloseHandle(hFile);
                        return true;
                    }
                    if (idx >= 20) {
                        uint32_t locSig = 0;
                        memcpy(&locSig, &tail[idx - 20], sizeof(uint32_t));
                        if (locSig == 0x07064b50u) {
                            CloseHandle(hFile);
                            return true;
                        }
                    }
                    break;
                }
            }
        }
    }
    CloseHandle(hFile);
    return false;
}

// ============================================================================
// HOOKED FUNCTIONS
// ============================================================================

// Lazily registers an open CArchive instance into ModernArchive FlatArchiveMap when:
// (1) K2 native 32-bit CMMapUnzip failed to open it (e.g. Zip64 or > 2 GB),
// (2) The archive file size on disk is >= 1.5 GiB (e.g. textures.s2z),
// (3) An unpacked <archive>.s2zdir directory overlay exists on disk, or
// (4) KONGOR_FORCE_MINIZ_ALL=1 is set.
static std::shared_ptr<ModernArchive> TryRegisterOpenArchive(
    void* pArchive,
    const wchar_t* wszCallerPath = nullptr,
    const wchar_t* wszCallerMaster = nullptr) {
    if (!pArchive) return nullptr;

    // Fast check under shared lock
    {
        std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            return it->second;
        }
        if (g_passthroughArchives.find(pArchive) != g_passthroughArchives.end()) {
            return nullptr;
        }
    }

    uint8_t* p = reinterpret_cast<uint8_t*>(pArchive);
    uint8_t* pInternal = *reinterpret_cast<uint8_t**>(p);
    bool k2NativeOpened = false;
    if (pInternal) {
        uint64_t orig40 = *reinterpret_cast<uint64_t*>(pInternal + 0x40);
        uint64_t orig48 = *reinterpret_cast<uint64_t*>(pInternal + 0x48);
        k2NativeOpened = (orig40 != orig48);
    }

    // Read system path (pArchive + 0x88), archive path (pArchive + 0x08), master (pArchive + 0x48).
    // Fall back to caller arguments when native CArchive::Open cleared m_sPath (+0x08) on 32-bit failure
    // or before CFileManager::RegisterArchive assigns m_sMod (+0x48).
    const wchar_t* wszSystemPath = GetK2StringData(p + 0x88);
    const wchar_t* wszPath = GetK2StringData(p + 0x08);
    if ((!wszPath || !wszPath[0]) && wszCallerPath && wszCallerPath[0]) {
        wszPath = wszCallerPath;
    }
    const wchar_t* wszMaster = GetK2StringData(p + 0x48);
    if ((!wszMaster || !wszMaster[0]) && wszCallerMaster && wszCallerMaster[0]) {
        wszMaster = wszCallerMaster;
    }

    std::wstring resolvedSystemPath;
    if (wszSystemPath && wszSystemPath[0] && GetFileAttributesW(wszSystemPath) != INVALID_FILE_ATTRIBUTES) {
        resolvedSystemPath = wszSystemPath;
    } else {
        resolvedSystemPath = ResolveArchiveSystemPath(wszPath, wszMaster);
    }

    if (resolvedSystemPath.empty() || GetFileAttributesW(resolvedSystemPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        if (k2NativeOpened) {
            std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
            g_passthroughArchives.insert(pArchive);
        }
        return nullptr;
    }

    // Check for Xash3D SEARCHPATH_PK3DIR equivalent: <archive>.s2zdir loose-directory shadow (#135)
    std::wstring candidateS2zDir = resolvedSystemPath + L"dir";
    DWORD s2zDirAttrs = GetFileAttributesW(candidateS2zDir.c_str());
    bool hasS2zDir = (s2zDirAttrs != INVALID_FILE_ATTRIBUTES && (s2zDirAttrs & FILE_ATTRIBUTE_DIRECTORY) != 0);

    const wchar_t* wszStoredPathToArch = GetK2StringData(p + 0x28);
    bool hasUppercaseVirtualPrefix =
        Kongor_HasUppercaseVirtualArchivePrefixW(wszStoredPathToArch) ||
        Kongor_HasUppercaseVirtualArchivePrefixW(wszPath) ||
        Kongor_HasUppercaseVirtualArchivePrefixW(wszCallerPath);

    uint64_t diskBytes = 0;
    if (!ShouldUseModernArchive(resolvedSystemPath, k2NativeOpened, hasS2zDir, hasUppercaseVirtualPrefix, diskBytes)) {
        std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
        g_passthroughArchives.insert(pArchive);
        return nullptr;
    }

    // Double-check under unique lock before expensive indexing
    {
        std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            return it->second;
        }
    }

    char szDiskPathUtf8[MAX_PATH * 3] = {0};
    WideCharToMultiByte(CP_UTF8, 0, resolvedSystemPath.c_str(), -1, szDiskPathUtf8, sizeof(szDiskPathUtf8), NULL, NULL);

    auto modernArch = std::make_shared<ModernArchive>();
    modernArch->disk_path = resolvedSystemPath;
    modernArch->archive_path = wszPath ? wszPath : L"";
    modernArch->master_path = wszMaster ? wszMaster : L"";

    const wchar_t* wszPathToArch = GetK2StringData(p + 0x28);
    if (wszPathToArch && wszPathToArch[0]) {
        modernArch->path_to_archive_w = wszPathToArch;
    } else if (!modernArch->archive_path.empty()) {
        std::wstring parentDir = modernArch->archive_path;
        for (wchar_t& ch : parentDir) {
            if (ch == L'\\') ch = L'/';
        }
        size_t lastSlash = parentDir.find_last_of(L'/');
        if (lastSlash != std::wstring::npos) {
            parentDir.resize(lastSlash + 1);
        } else {
            parentDir = L"/";
        }
        modernArch->path_to_archive_w = parentDir;
    }

    if (hasS2zDir) {
        modernArch->s2zdir_path = candidateS2zDir;
        modernArch->has_s2zdir = true;
        LogArchive("[KONGOR ARCHIVE] [XASH3D-S2ZDIR] Mounted unpacked directory overlay '%ls' for archive '%ls'\n",
            candidateS2zDir.c_str(), resolvedSystemPath.c_str());
    }

    std::wstring cleanMasterW = wszMaster ? wszMaster : L"";
    while (!cleanMasterW.empty() && (cleanMasterW[0] == L'/' || cleanMasterW[0] == L'\\' || cleanMasterW[0] == L':')) {
        cleanMasterW.erase(0, 1);
    }
    while (!cleanMasterW.empty() && (cleanMasterW.back() == L'/' || cleanMasterW.back() == L'\\')) {
        cleanMasterW.pop_back();
    }
    modernArch->master_clean_w = cleanMasterW;
    modernArch->master_path_utf8 = NormalizeWPath(wszMaster);

    auto tStart = std::chrono::high_resolution_clock::now();

    if (!mz_zip_reader_init_file_v2(&modernArch->zip, szDiskPathUtf8, 0, 0, 0)) {
        mz_zip_error mzErr = mz_zip_get_last_error(&modernArch->zip);
        LogArchive("[KONGOR ARCHIVE] miniz failed to open '%s' (error code: %d).\n", szDiskPathUtf8, (int)mzErr);
        return nullptr;
    }

    modernArch->is_zip64 = mz_zip_is_zip64(&modernArch->zip);
    mz_uint numFiles = mz_zip_reader_get_num_files(&modernArch->zip);

    // Index all entries into cache-aligned open-addressing flat hash map (<= 5ns lookup)
    modernArch->flat_map.Init(numFiles);
    modernArch->file_list.reserve(numFiles);
    modernArch->sorted_entries.reserve(numFiles);

    for (mz_uint i = 0; i < numFiles; ++i) {
        mz_zip_archive_file_stat stat;
        if (mz_zip_reader_file_stat(&modernArch->zip, i, &stat)) {
            uint64_t h = HashNormalizedPathA(stat.m_filename);
            modernArch->flat_map.Insert(h, i);
            modernArch->file_list.push_back(stat.m_filename);

            std::string normEntry = NormalizePath(stat.m_filename);
            if (!normEntry.empty() && normEntry.back() != '/') {
                modernArch->sorted_entries.push_back(std::move(normEntry));
            }
        }
    }

    std::sort(modernArch->sorted_entries.begin(), modernArch->sorted_entries.end());
    modernArch->sorted_entries.erase(
        std::unique(modernArch->sorted_entries.begin(), modernArch->sorted_entries.end()),
        modernArch->sorted_entries.end());

    // Only synthesize non-equal internal pointers if K2 native open left them empty (e.g. Zip64 or >2 GB fallback)
    if (pInternal) {
        uint64_t orig40 = *reinterpret_cast<uint64_t*>(pInternal + 0x40);
        uint64_t orig48 = *reinterpret_cast<uint64_t*>(pInternal + 0x48);
        uint64_t orig50 = *reinterpret_cast<uint64_t*>(pInternal + 0x50);
        if (orig40 == orig48) {
            modernArch->orig_vec_40 = orig40;
            modernArch->orig_vec_48 = orig48;
            modernArch->orig_vec_50 = orig50;
            modernArch->dummy_unzip_ptr = nullptr;
            modernArch->synthesized_unzip_vec = true;
            *reinterpret_cast<void***>(pInternal + 0x40) = &modernArch->dummy_unzip_ptr;
            *reinterpret_cast<void***>(pInternal + 0x48) = &modernArch->dummy_unzip_ptr + 1;
            *reinterpret_cast<void***>(pInternal + 0x50) = &modernArch->dummy_unzip_ptr + 1;
        }
    }

    // Ensure m_sPath (+0x08), m_sPathToArchive (+0x28), m_sMod (+0x48), and m_sCompleteDiskPath (+0x88)
    // are populated when native open exited early or before CFileManager::RegisterArchive runs.
    if (g_pK2StringWAssign) {
        if (GetK2StringLength(p + 0x08) == 0 && !modernArch->archive_path.empty()) {
            std::wstring normArchPath = modernArch->archive_path;
            for (wchar_t& ch : normArchPath) {
                if (ch == L'\\') ch = L'/';
            }
            g_pK2StringWAssign(p + 0x08, normArchPath.c_str(), normArchPath.size());
        }
        if (GetK2StringLength(p + 0x28) == 0 && !modernArch->path_to_archive_w.empty()) {
            g_pK2StringWAssign(p + 0x28, modernArch->path_to_archive_w.c_str(), modernArch->path_to_archive_w.size());
        }
        if (GetK2StringLength(p + 0x48) == 0 && !modernArch->master_path.empty()) {
            g_pK2StringWAssign(p + 0x48, modernArch->master_path.c_str(), modernArch->master_path.size());
        }
        if (GetK2StringLength(p + 0x88) == 0 && !resolvedSystemPath.empty()) {
            std::wstring normSysPath = resolvedSystemPath;
            for (wchar_t& ch : normSysPath) {
                if (ch == L'\\') ch = L'/';
            }
            g_pK2StringWAssign(p + 0x88, normSysPath.c_str(), normSysPath.size());
        }
    }

    auto tEnd = std::chrono::high_resolution_clock::now();
    double indexMs = std::chrono::duration<double, std::milli>(tEnd - tStart).count();

    LogArchive("[KONGOR ARCHIVE] [SUCCESS] Indexed %u entries from '%ls' into FlatArchiveMap in %.3f ms (Zip64=%s, Synthesized=%s, Size=%.2f MB, Cap=%zu)\n",
        numFiles, resolvedSystemPath.c_str(), indexMs,
        modernArch->is_zip64 ? "YES" : "NO",
        modernArch->synthesized_unzip_vec ? "YES" : "NO",
        (double)modernArch->zip.m_archive_size / (1024.0 * 1024.0),
        modernArch->flat_map.Capacity());

    {
        std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
        g_passthroughArchives.erase(pArchive);
        g_archiveMap[pArchive] = modernArch;
        g_registeredArchiveCount.store((uint32_t)g_archiveMap.size(), std::memory_order_release);
    }

    return modernArch;
}

static bool __fastcall Hooked_CArchive_Open(void* pArchive, const void* pPathStr, int iMode, const void* pMasterStr) {
    if (!pArchive) return false;

    // Capture caller path and master strings BEFORE g_origCArchiveOpen (which clears m_sPath on 32-bit failure).
    const wchar_t* wszCallerPathPtr = GetK2StringData(pPathStr);
    const wchar_t* wszCallerMasterPtr = GetK2StringData(pMasterStr);
    std::wstring sCallerPathW = wszCallerPathPtr ? wszCallerPathPtr : L"";
    std::wstring sCallerMasterW = wszCallerMasterPtr ? wszCallerMasterPtr : L"";

    // If re-opening an existing CArchive instance that had a synthesized unzip vector,
    // restore pInternal + 0x40..0x50 before K2's native CArchive::Open calls CArchive::Close.
    {
        std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
        g_passthroughArchives.erase(pArchive);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            if (it->second && it->second->synthesized_unzip_vec) {
                uint8_t* pInternal = *reinterpret_cast<uint8_t**>(pArchive);
                if (pInternal) {
                    *reinterpret_cast<uint64_t*>(pInternal + 0x40) = it->second->orig_vec_40;
                    *reinterpret_cast<uint64_t*>(pInternal + 0x48) = it->second->orig_vec_48;
                    *reinterpret_cast<uint64_t*>(pInternal + 0x50) = it->second->orig_vec_50;
                }
            }
            g_archiveMap.erase(it);
            g_registeredArchiveCount.store((uint32_t)g_archiveMap.size(), std::memory_order_release);
        }
    }

    // If opening for read and the target archive is Zip64 or >= 1.5 GiB, pass ARCHIVE_DISABLE_MMAP (0x80)
    // to g_origCArchiveOpen so CMMapUnzip::Open does not MapViewOfFile the full archive and crash at
    // CMMapUnzip::Initialize (RVA 0x945EDF: pMappedView + 0xFFFFFFFF) when fs_disableMMap == false.
    int nativeOpenMode = iMode;
    if (iMode & 1) {
        std::wstring preResolved = ResolveArchiveSystemPath(sCallerPathW.c_str(), sCallerMasterW.c_str());
        if (IsArchiveZip64OrLargeOnDisk(preResolved)) {
            nativeOpenMode = iMode | 0x80;
        }
    }

    // Always allow K2 engine to perform its native open and resolve its internal handles/path
    bool bK2Opened = false;
    if (g_origCArchiveOpen) {
        bK2Opened = g_origCArchiveOpen(pArchive, pPathStr, nativeOpenMode, pMasterStr);
    }

    if (nativeOpenMode != iMode) {
        *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(pArchive) + 0xa8) = iMode;
    }

    // If writing to archive (write flag set and read flag NOT set), pass through to legacy engine
    if (!(iMode & 1)) {
        LogArchive("[KONGOR ARCHIVE] CArchive::Open called with Write-only mode (%d), delegating to K2.\n", iMode);
        return bK2Opened;
    }

    const wchar_t* wszOpenedPathToArch = GetK2StringData(reinterpret_cast<const uint8_t*>(pArchive) + 0x28);
    bool bHasUpperVirtualPrefix =
        Kongor_HasUppercaseVirtualArchivePrefixW(sCallerPathW.c_str()) ||
        Kongor_HasUppercaseVirtualArchivePrefixW(wszOpenedPathToArch);

    LogArchive("[KONGOR ARCHIVE] CArchive::Open: this=%p path='%ls' pathToArch='%ls' master='%ls' k2Opened=%d isZip64OrLarge=%d upperVirtualPrefix=%d\n",
        pArchive, sCallerPathW.c_str(), wszOpenedPathToArch ? wszOpenedPathToArch : L"", sCallerMasterW.c_str(),
        bK2Opened ? 1 : 0, (nativeOpenMode != iMode) ? 1 : 0, bHasUpperVirtualPrefix ? 1 : 0);

    // Register and parse with ModernArchive (miniz + FlatArchiveMap) if:
    //  1) the archive is Zip64 / >= 1.5 GiB,
    //  2) K2 failed to open it, or
    //  3) its virtual mod prefix contains uppercase ASCII (e.g. ":/KONGOR/updates/*.s2z"), bypassing
    //     K2 CArchive::ContainsFile's tolower()-vs-case-sensitive m_sPathToArchive bug.
    // Standard lowercase < 1.5 GiB archives (e.g. ":/game/textures.s2z", ":/game/resources0.s2z")
    // remain on K2's native path untouched.
    bool bNeedsModern = (nativeOpenMode != iMode) || !bK2Opened || bHasUpperVirtualPrefix;
    if (bNeedsModern) {
        auto modernArch = TryRegisterOpenArchive(pArchive, sCallerPathW.c_str(), sCallerMasterW.c_str());
        if (modernArch) {
            *reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(pArchive) + 0xa8) = iMode;
            return true;
        }
    } else {
        std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
        g_passthroughArchives.insert(pArchive);
    }

    return bK2Opened;
}

static bool __fastcall Hooked_CArchive_IsOpen(void* pArchive) {
    if (!pArchive) return false;

    {
        std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
        if (g_archiveMap.find(pArchive) != g_archiveMap.end()) {
            return true;
        }
    }

    uint8_t* p = reinterpret_cast<uint8_t*>(pArchive);
    int mode = *reinterpret_cast<int*>(p + 0xa8);
    uint8_t* pInternal = *reinterpret_cast<uint8_t**>(p);
    if (!pInternal) return false;

    if (mode & 1) {
        return *reinterpret_cast<uint8_t**>(pInternal + 0x40) != *reinterpret_cast<uint8_t**>(pInternal + 0x48);
    }
    if (mode & 2) {
        return *reinterpret_cast<uint8_t**>(pInternal + 0x28) != *reinterpret_cast<uint8_t**>(pInternal + 0x30);
    }
    return false;
}

static bool __fastcall Hooked_CArchive_ContainsFile(void* pArchive, const void* pFilenameStr);
static int __fastcall Hooked_CArchive_ReadFile(void* pArchive, const void* pFilenameStr, char** ppBuffer);
static int TryReadDiskOverrideFileW(const wchar_t* diskPathW, char** ppBuffer);

static bool __fastcall Hooked_CArchive_ContainsFile(void* pArchive, const void* pFilenameStr) {
    if (!pArchive || !pFilenameStr) return false;

    if (g_registeredArchiveCount.load(std::memory_order_relaxed) == 0) {
        return g_origCArchiveContainsFile ? g_origCArchiveContainsFile(pArchive, pFilenameStr) : false;
    }

    std::shared_ptr<ModernArchive> arch;
    bool knownPassthrough = false;
    {
        std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            arch = it->second;
        } else if (g_passthroughArchives.find(pArchive) != g_passthroughArchives.end()) {
            knownPassthrough = true;
        }
    }

    if (!arch && !knownPassthrough) {
        arch = TryRegisterOpenArchive(pArchive);
    }

    if (arch) {
        auto tStart = std::chrono::high_resolution_clock::now();

        uint8_t* p = reinterpret_cast<uint8_t*>(pArchive);
        const wchar_t* wszFile = GetK2StringData(pFilenameStr);
        const wchar_t* pRaw = StripVirtualPrefixW(wszFile);

        const wchar_t* wszLiveMaster = !arch->master_clean_w.empty() ? arch->master_clean_w.c_str() : GetK2StringData(p + 0x48);
        const wchar_t* pAfterMaster = StripPathPrefixW(pRaw, wszLiveMaster);

        const wchar_t* wszPathToArch = GetK2StringData(p + 0x28);
        if (!wszPathToArch || !wszPathToArch[0]) {
            wszPathToArch = arch->path_to_archive_w.c_str();
        }
        const wchar_t* pStripped = StripPathPrefixW(pAfterMaster, wszPathToArch);

        std::wstring s2zDirFull;
        if (arch->has_s2zdir &&
            (TryResolveS2zDirLooseFileW(arch, pStripped, s2zDirFull) ||
             (pAfterMaster != pStripped && TryResolveS2zDirLooseFileW(arch, pAfterMaster, s2zDirFull)))) {
            arch->lookupsCount.fetch_add(1, std::memory_order_relaxed);
            arch->hitsCount.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        uint32_t fileIndex = FLAT_MAP_EMPTY;
        bool found = false;

        // Probe 1: Stripped master + m_sPathToArchive prefix (archive relative path)
        uint64_t hStripped = HashNormalizedPathW(pStripped);
        {
            std::shared_lock<std::shared_mutex> archLock(arch->mutex);
            found = arch->flat_map.Find(hStripped, fileIndex);
            if (!found && pAfterMaster != pStripped) {
                // Probe 2: Stripped master prefix only
                uint64_t hAfterMaster = HashNormalizedPathW(pAfterMaster);
                found = arch->flat_map.Find(hAfterMaster, fileIndex);
            }
            if (!found && pRaw != pAfterMaster && pRaw != pStripped) {
                // Probe 3: Raw virtual prefix stripped path
                uint64_t hRaw = HashNormalizedPathW(pRaw);
                found = arch->flat_map.Find(hRaw, fileIndex);
            }
        }

        auto tEnd = std::chrono::high_resolution_clock::now();
        uint64_t lookupNs = (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(tEnd - tStart).count();
        arch->lookupsCount.fetch_add(1, std::memory_order_relaxed);
        arch->totalLookupNs.fetch_add(lookupNs, std::memory_order_relaxed);
        if (found) arch->hitsCount.fetch_add(1, std::memory_order_relaxed);

        return found;
    }

    return g_origCArchiveContainsFile(pArchive, pFilenameStr);
}

typedef void* (*K2AllocFn)(size_t size);
typedef void (*K2FreeFn)(void* ptr);

static K2AllocFn g_pK2Alloc = nullptr;
static K2FreeFn  g_pK2Free  = nullptr;

static constexpr uint8_t kXash3dTailCanaryByte = 0xDFu; // Xash3D MEMHEADER_SENTINEL2 (#138)
static std::atomic<bool> g_stumpsTreesEnabled{false};
static std::atomic<bool> g_customDamageFloatsEnabled{false};

extern "C" void Kongor_SetStumpsTreesEnabled(bool enabled) {
    bool prev = g_stumpsTreesEnabled.exchange(enabled, std::memory_order_relaxed);
    if (prev != enabled) {
        LogArchive("[KONGOR ARCHIVE] [MOD-17] cg_stumpsTrees VFS redirection set to %s\n", enabled ? "ENABLED" : "DISABLED");
    }
}

extern "C" void Kongor_SetCustomDamageFloatsEnabled(bool enabled) {
    bool prev = g_customDamageFloatsEnabled.exchange(enabled, std::memory_order_relaxed);
    if (prev != enabled) {
        LogArchive("[KONGOR ARCHIVE] [MOD-5] cg_customDamageFloats VFS redirection set to %s\n", enabled ? "ENABLED" : "DISABLED");
    }
}

extern "C" bool Kongor_IsStumpsTreesEnabled() {
    return g_stumpsTreesEnabled.load(std::memory_order_relaxed);
}

extern "C" bool Kongor_IsCustomDamageFloatsEnabled() {
    return g_customDamageFloatsEnabled.load(std::memory_order_relaxed);
}

static bool IsTreeModelMdfPathW(const wchar_t* pPath) {
    if (!pPath) return false;
    char utf8[512] = {0};
    int n = WideCharToMultiByte(CP_UTF8, 0, pPath, -1, utf8, sizeof(utf8), NULL, NULL);
    if (n <= 0) return false;
    if (!Kongor_MatchWildcardA(utf8, "world/props/trees/*/model.mdf", true)) return false;
    if (Kongor_MatchWildcardA(utf8, "world/props/trees/stump/model.mdf", true)) return false;
    return true;
}

// [L2-P1-02] Strict K2 heap allocator for all buffers returned to K2 via CArchive::ReadFile (*ppBuffer).
// Never fall back to version.dll's CRT malloc(), because K2 frees *ppBuffer via k2_x64.dll's K2_DELETE_ARRAY.
static char* AllocateK2VfsBuffer(size_t byteCount) {
    if (!g_pK2Alloc) {
        HMODULE hK2 = g_hK2Module ? g_hK2Module : GetModuleHandleA("k2_x64.dll");
        if (hK2) {
            g_hK2Module = hK2;
            uint8_t* pBase = reinterpret_cast<uint8_t*>(hK2);
            g_pK2Alloc = reinterpret_cast<K2AllocFn>(pBase + 0x6413AC);
            g_pK2Free  = reinterpret_cast<K2FreeFn>(pBase + 0x376930);
        }
    }
    if (!g_pK2Alloc) {
        LogArchive("[KONGOR ARCHIVE] [ERROR] Refused VFS buffer allocation (%zu bytes): g_pK2Alloc is unresolved (never fall back to cross-CRT malloc).\n", byteCount);
        return nullptr;
    }
    return static_cast<char*>(g_pK2Alloc(byteCount));
}

static void FreeK2VfsBuffer(void* pBuf) {
    if (!pBuf) return;
    if (!g_pK2Free) {
        HMODULE hK2 = g_hK2Module ? g_hK2Module : GetModuleHandleA("k2_x64.dll");
        if (hK2) {
            g_pK2Free = reinterpret_cast<K2FreeFn>(reinterpret_cast<uint8_t*>(hK2) + 0x376930);
        }
    }
    if (g_pK2Free) {
        g_pK2Free(pBuf);
    }
}

static int TryReadDiskOverrideFileW(const wchar_t* diskPathW, char** ppBuffer) {
    HANDLE hFile = CreateFileW(diskPathW, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER fSize{};
    if (GetFileSizeEx(hFile, &fSize) && fSize.QuadPart > 0 &&
        !KONGOR_SZT_ADD_OVFCHK(static_cast<size_t>(fSize.QuadPart), 2ULL)) {
        size_t uncompSize = static_cast<size_t>(fSize.QuadPart);
        char* pBuf = AllocateK2VfsBuffer(uncompSize + 2);
        if (pBuf) {
            pBuf[uncompSize] = '\0';
            pBuf[uncompSize + 1] = static_cast<char>(kXash3dTailCanaryByte);
            DWORD bytesRead = 0;
            if (ReadFile(hFile, pBuf, static_cast<DWORD>(uncompSize), &bytesRead, NULL) && bytesRead == uncompSize) {
                CloseHandle(hFile);
                *ppBuffer = pBuf;
                return static_cast<int>(uncompSize);
            }
            FreeK2VfsBuffer(pBuf);
        }
    }
    CloseHandle(hFile);
    return 0;
}

static int __fastcall Hooked_CArchive_ReadFile(void* pArchive, const void* pFilenameStr, char** ppBuffer) {
    if (!pArchive || !pFilenameStr || !ppBuffer) return 0;

    const wchar_t* wszFile = GetK2StringData(pFilenameStr);
    const wchar_t* pRaw = StripVirtualPrefixW(wszFile);

    // Fast-path: When no ModernArchive instances are active and no gameplay redirection CVars are active,
    // bypass all mutex locks and delegate directly to K2 native CMMapUnzip (accelerated by libdeflate).
    if (g_registeredArchiveCount.load(std::memory_order_relaxed) == 0 &&
        !g_stumpsTreesEnabled.load(std::memory_order_relaxed) &&
        !g_customDamageFloatsEnabled.load(std::memory_order_relaxed)) {
        return g_origCArchiveReadFile ? g_origCArchiveReadFile(pArchive, pFilenameStr, ppBuffer) : 0;
    }

    // Mod 17 (cg_stumpsTrees): Redirect world/props/trees/*/model.mdf to stump/model.mdf when enabled
    if (g_stumpsTreesEnabled.load(std::memory_order_relaxed) && IsTreeModelMdfPathW(pRaw)) {
        int redirectedBytes = TryReadDiskOverrideFileW(L"override\\world\\props\\trees\\stump\\model.mdf", ppBuffer);
        if (redirectedBytes > 0) {
            return redirectedBytes;
        }
    }

    // Mod 5 (cg_customDamageFloats): Redirect base.gamemechanics to override/base_highvis.gamemechanics when enabled
    if (g_customDamageFloatsEnabled.load(std::memory_order_relaxed) && pRaw && _wcsicmp(pRaw, L"base.gamemechanics") == 0) {
        int redirectedBytes = TryReadDiskOverrideFileW(L"override\\base_highvis.gamemechanics", ppBuffer);
        if (redirectedBytes > 0) {
            return redirectedBytes;
        }
    }

    std::shared_ptr<ModernArchive> arch;
    bool knownPassthrough = false;
    {
        std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            arch = it->second;
        } else if (g_passthroughArchives.find(pArchive) != g_passthroughArchives.end()) {
            knownPassthrough = true;
        }
    }

    if (!arch && !knownPassthrough) {
        arch = TryRegisterOpenArchive(pArchive);
    }

    if (arch) {
        uint8_t* p = reinterpret_cast<uint8_t*>(pArchive);
        const wchar_t* wszLiveMaster = !arch->master_clean_w.empty() ? arch->master_clean_w.c_str() : GetK2StringData(p + 0x48);
        const wchar_t* pAfterMaster = StripPathPrefixW(pRaw, wszLiveMaster);

        const wchar_t* wszPathToArch = GetK2StringData(p + 0x28);
        if (!wszPathToArch || !wszPathToArch[0]) {
            wszPathToArch = arch->path_to_archive_w.c_str();
        }
        const wchar_t* pStripped = StripPathPrefixW(pAfterMaster, wszPathToArch);

        // Xash3D .s2zdir loose-directory shadow path (#135, guarded by Kongor_CheckNastyPathW #144)
        std::wstring s2zDirFile;
        if (arch->has_s2zdir &&
            (TryResolveS2zDirLooseFileW(arch, pStripped, s2zDirFile) ||
             (pAfterMaster != pStripped && TryResolveS2zDirLooseFileW(arch, pAfterMaster, s2zDirFile)))) {
            HANDLE hLoose = CreateFileW(s2zDirFile.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
            if (hLoose != INVALID_HANDLE_VALUE) {
                LARGE_INTEGER looseSize{};
                if (GetFileSizeEx(hLoose, &looseSize) && looseSize.QuadPart >= 0 &&
                    !KONGOR_SZT_ADD_OVFCHK(static_cast<size_t>(looseSize.QuadPart), 2ULL)) {
                    size_t uncompSize = static_cast<size_t>(looseSize.QuadPart);
                    char* pBuf = AllocateK2VfsBuffer(uncompSize + 2);
                    if (pBuf) {
                        pBuf[uncompSize] = '\0';
                        pBuf[uncompSize + 1] = static_cast<char>(kXash3dTailCanaryByte);
                        DWORD bytesRead = 0;
                        if (ReadFile(hLoose, pBuf, static_cast<DWORD>(uncompSize), &bytesRead, NULL) && bytesRead == uncompSize) {
                            CloseHandle(hLoose);
                            arch->s2zdirOverrideCount.fetch_add(1, std::memory_order_relaxed);
                            *ppBuffer = pBuf;
                            return static_cast<int>(uncompSize);
                        }
                        FreeK2VfsBuffer(pBuf);
                    }
                }
                CloseHandle(hLoose);
            }
        }

        uint32_t fileIndex = FLAT_MAP_EMPTY;
        bool found = false;

        uint64_t hStripped = HashNormalizedPathW(pStripped);
        {
            std::shared_lock<std::shared_mutex> archLock(arch->mutex);
            found = arch->flat_map.Find(hStripped, fileIndex);
            if (!found && pAfterMaster != pStripped) {
                uint64_t hAfterMaster = HashNormalizedPathW(pAfterMaster);
                found = arch->flat_map.Find(hAfterMaster, fileIndex);
            }
            if (!found && pRaw != pAfterMaster && pRaw != pStripped) {
                uint64_t hRaw = HashNormalizedPathW(pRaw);
                found = arch->flat_map.Find(hRaw, fileIndex);
            }
        }

        if (!found) {
            *ppBuffer = nullptr;
            return 0;
        }

        std::unique_lock<std::shared_mutex> archLock(arch->mutex);

        mz_zip_archive_file_stat stat;
        if (!mz_zip_reader_file_stat(&arch->zip, fileIndex, &stat)) {
            LogArchive("[KONGOR ARCHIVE] mz_zip_reader_file_stat failed for fileIndex %u\n", fileIndex);
            *ppBuffer = nullptr;
            return 0;
        }

        if (KONGOR_UT64_ADD_OVFCHK(stat.m_local_header_ofs, stat.m_comp_size) ||
            KONGOR_SZT_ADD_OVFCHK(static_cast<size_t>(stat.m_uncomp_size), 2ULL)) {
            LogArchive("[KONGOR ARCHIVE] [OVERFLOW GUARD] Zip header arithmetic overflow for fileIndex %u ('%s', ofs=%llu, comp=%llu, uncomp=%llu)\n",
                fileIndex, stat.m_filename, (unsigned long long)stat.m_local_header_ofs,
                (unsigned long long)stat.m_comp_size, (unsigned long long)stat.m_uncomp_size);
            *ppBuffer = nullptr;
            return 0;
        }

        size_t uncompSize = static_cast<size_t>(stat.m_uncomp_size);

        // Allocate uncompSize + 2 bytes using K2's exact CRT allocator (RVA 0x6413AC in k2_x64.dll) (#138)
        // Byte [uncompSize] = '\0' (null terminator + 1-byte overrun guard)
        // Byte [uncompSize + 1] = 0xDF (Xash3D MEMHEADER_SENTINEL2 2-byte overrun guard)
        // satisfies: OBL-W3-04
        char* pBuf = AllocateK2VfsBuffer(uncompSize + 2);
        if (!pBuf) {
            LogArchive("[KONGOR ARCHIVE] Out of memory allocating %zu bytes for fileIndex %u\n", uncompSize, fileIndex);
            *ppBuffer = nullptr;
            return 0;
        }

        pBuf[uncompSize] = '\0';
        pBuf[uncompSize + 1] = static_cast<char>(kXash3dTailCanaryByte);

        auto tExtractStart = std::chrono::high_resolution_clock::now();
        bool extractSuccess = false;

        // Path A: Uncompressed STORED asset (Method 0) - Zero-copy fast read
        if (stat.m_method == 0) {
            if (mz_zip_reader_extract_to_mem(&arch->zip, fileIndex, pBuf, uncompSize, 0)) {
                extractSuccess = true;
                arch->storedDirectCount.fetch_add(1, std::memory_order_relaxed);
            }
        }
        // Path B: DEFLATED asset (Method 8) - AVX2 libdeflate decompressor
        else if (stat.m_method == MZ_DEFLATED) {
            static thread_local libdeflate_decompressor* t_decompressor = nullptr;
            if (!t_decompressor) {
                t_decompressor = libdeflate_alloc_decompressor();
            }

            // Extract raw compressed stream directly into thread-local buffer
            static thread_local std::vector<uint8_t> t_compBuf;
            if (t_compBuf.size() < stat.m_comp_size) {
                t_compBuf.resize(stat.m_comp_size);
            }

            if (mz_zip_reader_extract_to_mem(&arch->zip, fileIndex, t_compBuf.data(), stat.m_comp_size, MZ_ZIP_FLAG_COMPRESSED_DATA)) {
                if (t_decompressor) {
                    size_t actualOutBytes = 0;
                    libdeflate_result res = libdeflate_deflate_decompress(
                        t_decompressor,
                        t_compBuf.data(),
                        stat.m_comp_size,
                        pBuf,
                        uncompSize,
                        &actualOutBytes
                    );
                    if (res == LIBDEFLATE_SUCCESS && actualOutBytes == uncompSize) {
                        extractSuccess = true;
                        arch->libdeflateCount.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }

            // Infallible fallback: If libdeflate failed or was skipped, use miniz decompressor
            if (!extractSuccess) {
                if (mz_zip_reader_extract_to_mem(&arch->zip, fileIndex, pBuf, uncompSize, 0)) {
                    extractSuccess = true;
                    arch->minizFallbackCount.fetch_add(1, std::memory_order_relaxed);
                }
            }
        }
        // Path C: Any other compression method fallback
        else {
            if (mz_zip_reader_extract_to_mem(&arch->zip, fileIndex, pBuf, uncompSize, 0)) {
                extractSuccess = true;
                arch->minizFallbackCount.fetch_add(1, std::memory_order_relaxed);
            }
        }

        if (!extractSuccess) {
            mz_zip_error mzErr = mz_zip_get_last_error(&arch->zip);
            LogArchive("[KONGOR ARCHIVE] Extract failed for fileIndex %u '%s' (err=%d)\n",
                fileIndex, stat.m_filename, (int)mzErr);
            FreeK2VfsBuffer(pBuf);
            *ppBuffer = nullptr;
            return 0;
        }

        // Verify BOTH dual-byte tail canaries immediately after extraction (#138, OBL-W3-04)
        if (pBuf[uncompSize] != '\0' || static_cast<uint8_t>(pBuf[uncompSize + 1]) != kXash3dTailCanaryByte) {
            arch->canaryCorruptCount.fetch_add(1, std::memory_order_relaxed);
            LogArchive("[KONGOR ARCHIVE] [XASH3D-CANARY-CORRUPT] Trashed tail sentinel in '%s' (size=%zu, b0=0x%02X, b1=0x%02X)\n",
                stat.m_filename, uncompSize,
                static_cast<unsigned int>(static_cast<uint8_t>(pBuf[uncompSize])),
                static_cast<unsigned int>(static_cast<uint8_t>(pBuf[uncompSize + 1])));
            pBuf[uncompSize] = '\0';
            pBuf[uncompSize + 1] = static_cast<char>(kXash3dTailCanaryByte);
        }

        // Pre-blit DDS/DX10 mipmap pyramid size check (#142, OBL-W3-06)
        if (HasDdsExtensionA(stat.m_filename)) {
            size_t expectedMinDdsBytes = 0;
            if (!VerifyDdsMipmapChainSize(reinterpret_cast<const uint8_t*>(pBuf), uncompSize, expectedMinDdsBytes)) {
                arch->ddsTruncatedCount.fetch_add(1, std::memory_order_relaxed);
                LogArchive("[KONGOR ARCHIVE] [XASH3D-DDS-GUARD] Truncated DDS mipmap chain in '%s' (actual=%zu, expected>=%zu)\n",
                    stat.m_filename, uncompSize, expectedMinDdsBytes);
            }
        }

        *ppBuffer = pBuf;

        auto tExtractEnd = std::chrono::high_resolution_clock::now();
        double extractUs = std::chrono::duration<double, std::micro>(tExtractEnd - tExtractStart).count();

        // Telemetry for large files (> 1 MB) or slower decompressions (> 2 ms)
        if (uncompSize > 1024 * 1024 || extractUs > 2000.0) {
            LogArchive("[KONGOR ARCHIVE] ReadFile '%s' (%zu bytes) in %.2f us from '%ls' (libdeflate=%llu, stored=%llu, miniz=%llu)\n",
                stat.m_filename, uncompSize, extractUs, arch->disk_path.c_str(),
                arch->libdeflateCount.load(std::memory_order_relaxed),
                arch->storedDirectCount.load(std::memory_order_relaxed),
                arch->minizFallbackCount.load(std::memory_order_relaxed));
        }

        return static_cast<int>(uncompSize);
    }

    return g_origCArchiveReadFile(pArchive, pFilenameStr, ppBuffer);
}

static void __fastcall Hooked_CArchive_GetFileList(void* pArchive, void* pFileListVec) {
    if (!pArchive || !pFileListVec) return;

    if (g_registeredArchiveCount.load(std::memory_order_relaxed) == 0) {
        if (g_origCArchiveGetFileList) {
            g_origCArchiveGetFileList(pArchive, pFileListVec);
        }
        return;
    }

    std::shared_ptr<ModernArchive> arch;
    bool knownPassthrough = false;
    {
        std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            arch = it->second;
        } else if (g_passthroughArchives.find(pArchive) != g_passthroughArchives.end()) {
            knownPassthrough = true;
        }
    }

    if (!arch && !knownPassthrough) {
        arch = TryRegisterOpenArchive(pArchive);
    }

    if (arch && !arch->sorted_entries.empty() &&
        g_pK2StringWAssign && g_pK2StringWVecPushBackRval && g_pK2StringWDtor) {
        std::shared_lock<std::shared_mutex> archLock(arch->mutex);
        wchar_t wBuf[1024];
        for (const std::string& entry : arch->sorted_entries) {
            int wLen = MultiByteToWideChar(CP_UTF8, 0, entry.c_str(), static_cast<int>(entry.size()), wBuf, 1023);
            if (wLen <= 0) continue;
            wBuf[wLen] = L'\0';

            alignas(8) uint64_t tempK2StrW[4] = { 0, 0, 0, 7 };
            g_pK2StringWAssign(tempK2StrW, wBuf, static_cast<size_t>(wLen));
            g_pK2StringWVecPushBackRval(pFileListVec, tempK2StrW);
            g_pK2StringWDtor(tempK2StrW);
        }
        return;
    }

    if (g_origCArchiveGetFileList) {
        g_origCArchiveGetFileList(pArchive, pFileListVec);
    }
}

static const std::string* __fastcall Hooked_CArchive_GetNextFileAtPath(void* pArchive, void** pIterator, const void* pSearchPath) {
    if (!pArchive || !pIterator || !pSearchPath) return nullptr;

    std::shared_ptr<ModernArchive> arch;
    bool knownPassthrough = false;
    {
        std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            arch = it->second;
        } else if (g_passthroughArchives.find(pArchive) != g_passthroughArchives.end()) {
            knownPassthrough = true;
        }
    }

    if (!arch && !knownPassthrough) {
        arch = TryRegisterOpenArchive(pArchive);
    }

    if (arch && !arch->sorted_entries.empty()) {
        static constexpr uintptr_t kKongorIterTagMask = 0xFFFFFFFF00000000ULL;
        static constexpr uintptr_t kKongorIterTag     = 0x4B4F4E4700000000ULL;

        std::string searchPrefix = NormalizeWPath(GetK2StringData(pSearchPath));
        uintptr_t rawIt = reinterpret_cast<uintptr_t>(*pIterator);

        std::shared_lock<std::shared_mutex> archLock(arch->mutex);
        size_t startIdx = 0;
        if ((rawIt & kKongorIterTagMask) == kKongorIterTag) {
            startIdx = static_cast<size_t>(rawIt & 0xFFFFFFFFULL);
        } else if (!searchPrefix.empty()) {
            auto lb = std::lower_bound(arch->sorted_entries.begin(), arch->sorted_entries.end(), searchPrefix);
            startIdx = static_cast<size_t>(lb - arch->sorted_entries.begin());
        }

        for (size_t i = startIdx; i < arch->sorted_entries.size(); ++i) {
            const std::string& candidate = arch->sorted_entries[i];
            if (searchPrefix.empty() || candidate.compare(0, searchPrefix.size(), searchPrefix) == 0) {
                *pIterator = reinterpret_cast<void*>(kKongorIterTag | static_cast<uintptr_t>(i + 1));
                return &candidate;
            }
            if (!searchPrefix.empty() && candidate > searchPrefix) {
                break;
            }
        }
        return nullptr;
    }

    return g_origCArchiveGetNextFileAtPath ? g_origCArchiveGetNextFileAtPath(pArchive, pIterator, pSearchPath) : nullptr;
}

static bool __fastcall Hooked_CArchive_Close(void* pArchive, unsigned int uiMode) {
    if (!pArchive) return false;

    std::shared_ptr<ModernArchive> arch;
    {
        std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
        g_passthroughArchives.erase(pArchive);
        auto it = g_archiveMap.find(pArchive);
        if (it != g_archiveMap.end()) {
            arch = it->second;
            g_archiveMap.erase(it);
        }
    }

    if (arch) {
        uint64_t totalLookups = arch->lookupsCount.load(std::memory_order_relaxed);
        uint64_t totalHits = arch->hitsCount.load(std::memory_order_relaxed);
        uint64_t totalNs = arch->totalLookupNs.load(std::memory_order_relaxed);
        double avgNs = totalLookups > 0 ? (double)totalNs / (double)totalLookups : 0.0;

        LogArchive("[KONGOR ARCHIVE] CArchive::Close: Released '%ls' (Lookups=%llu, Hits=%llu, AvgLookup=%.2f ns, libdeflate=%llu, stored=%llu, miniz=%llu)\n",
            arch->disk_path.c_str(), totalLookups, totalHits, avgNs,
            arch->libdeflateCount.load(std::memory_order_relaxed),
            arch->storedDirectCount.load(std::memory_order_relaxed),
            arch->minizFallbackCount.load(std::memory_order_relaxed));

        if (arch->synthesized_unzip_vec) {
            uint8_t* p = reinterpret_cast<uint8_t*>(pArchive);
            uint8_t* pInternal = *reinterpret_cast<uint8_t**>(p);
            if (pInternal) {
                *reinterpret_cast<uint64_t*>(pInternal + 0x40) = arch->orig_vec_40;
                *reinterpret_cast<uint64_t*>(pInternal + 0x48) = arch->orig_vec_48;
                *reinterpret_cast<uint64_t*>(pInternal + 0x50) = arch->orig_vec_50;
            }
        }
    }

    return g_origCArchiveClose(pArchive, uiMode);
}

static void ProactivelyScanFileManager(uint8_t* pBase) {
    if (!pBase) return;
    void* pFM = SafeReadPointer(pBase + 0x12618e8);
    if (!pFM) return;

    void** ppBegin = reinterpret_cast<void**>(SafeReadPointer(reinterpret_cast<uint8_t*>(pFM) + 0x48));
    void** ppEnd = reinterpret_cast<void**>(SafeReadPointer(reinterpret_cast<uint8_t*>(pFM) + 0x50));
    if (ppBegin && ppEnd && ppBegin < ppEnd) {
        ptrdiff_t count = ppEnd - ppBegin;
        if (count > 0 && count < 1000) {
            LogArchive("[KONGOR ARCHIVE] Proactively scanning %td pre-existing archives in CFileManager...\n", count);
            for (void** pCurr = ppBegin; pCurr != ppEnd; ++pCurr) {
                void* pArch = SafeReadPointer(pCurr);
                if (pArch) {
                    TryRegisterOpenArchive(pArch);
                }
            }
        }
    }

    void* pCompat = SafeReadPointer(reinterpret_cast<uint8_t*>(pFM) + 0x80);
    if (pCompat) {
        TryRegisterOpenArchive(pCompat);
    }
}

static void RestoreOrReapplyArchiveVectorsForToggle(bool enabled) {
    std::unique_lock<std::shared_mutex> lock(g_archiveMapMutex);
    size_t updatedCount = 0;
    for (auto& kv : g_archiveMap) {
        void* pArchive = kv.first;
        auto& arch = kv.second;
        if (!pArchive || !arch || !arch->synthesized_unzip_vec) {
            continue;
        }
        uint8_t* pInternal = reinterpret_cast<uint8_t*>(SafeReadPointer(pArchive));
        if (!pInternal) {
            continue;
        }
        if (!enabled) {
            // [L2-P1-02] Restore original empty CMMapUnzip vector pointers BEFORE native CArchive methods execute
            // so K2 never dereferences &arch->dummy_unzip_ptr (nullptr) while "vfs" hooks are toggled off.
            *reinterpret_cast<uint64_t*>(pInternal + 0x40) = arch->orig_vec_40;
            *reinterpret_cast<uint64_t*>(pInternal + 0x48) = arch->orig_vec_48;
            *reinterpret_cast<uint64_t*>(pInternal + 0x50) = arch->orig_vec_50;
            ++updatedCount;
        } else {
            // Re-apply synthesized non-empty marker pointers when "vfs" hooks are toggled back ON.
            uint64_t cur40 = *reinterpret_cast<uint64_t*>(pInternal + 0x40);
            uint64_t cur48 = *reinterpret_cast<uint64_t*>(pInternal + 0x48);
            if (cur40 == cur48) {
                arch->dummy_unzip_ptr = nullptr;
                *reinterpret_cast<void***>(pInternal + 0x40) = &arch->dummy_unzip_ptr;
                *reinterpret_cast<void***>(pInternal + 0x48) = &arch->dummy_unzip_ptr + 1;
                *reinterpret_cast<void***>(pInternal + 0x50) = &arch->dummy_unzip_ptr + 1;
                ++updatedCount;
            }
        }
    }
    LogArchive("[KONGOR ARCHIVE] Runtime 'vfs' group toggle -> %s (updated %zu synthesized archive vector(s)).\n",
        enabled ? "ON" : "OFF", updatedCount);
}

// ============================================================================
// HOOK INSTALLATION ENTRY POINT
// ============================================================================
bool InstallModernArchiveHooks(HMODULE hK2) {
    if (!hK2) {
        LogArchive("[KONGOR ARCHIVE] InstallModernArchiveHooks failed: hK2 is null.\n");
        return false;
    }

    static bool s_installed = false;
    if (s_installed) return true;

    g_hK2Module = hK2;
    uint8_t* pBase = reinterpret_cast<uint8_t*>(hK2);

    g_pK2Alloc = reinterpret_cast<K2AllocFn>(pBase + 0x6413AC);
    g_pK2Free  = reinterpret_cast<K2FreeFn>(pBase + 0x376930);
    g_pK2StringWAssign          = reinterpret_cast<FnK2StringWAssign>(pBase + 0x2A4890);
    g_pK2StringWVecPushBackRval = reinterpret_cast<FnK2StringWVecPushBackRval>(pBase + 0x2B2E40);
    g_pK2StringWDtor            = reinterpret_cast<FnK2StringWDtor>(pBase + 0x2A4A60);
    LogArchive("[KONGOR ARCHIVE] Resolved K2 Allocator @ %p, Deallocator @ %p, K2StringW::assign @ %p\n",
        g_pK2Alloc, g_pK2Free, g_pK2StringWAssign);

    // Primary RVAs in k2_x64.dll:
    // CArchive::Open:              RVA 0x67A620 (15 stolen bytes)
    // CArchive::ReadFile:          RVA 0x6799A0 (15 stolen bytes)
    // CArchive::ContainsFile:      RVA 0x679610 (16 stolen bytes)
    // CArchive::Close:             RVA 0x679E70 (15 stolen bytes)
    // CArchive::IsOpen:            RVA 0x679CE0 (17 stolen bytes)
    // CArchive::GetFileList:       RVA 0x679270 (15 stolen bytes)
    // CArchive::GetNextFileAtPath: RVA 0x679410 (15 stolen bytes)
    void* pFuncOpen = reinterpret_cast<void*>(pBase + 0x67A620);
    void* pFuncReadFile = reinterpret_cast<void*>(pBase + 0x6799A0);
    void* pFuncContainsFile = reinterpret_cast<void*>(pBase + 0x679610);
    void* pFuncClose = reinterpret_cast<void*>(pBase + 0x679E70);
    void* pFuncIsOpen = reinterpret_cast<void*>(pBase + 0x679CE0);
    void* pFuncGetFileList = reinterpret_cast<void*>(pBase + 0x679270);
    void* pFuncGetNextFileAtPath = reinterpret_cast<void*>(pBase + 0x679410);

    // Empirically verified retail k2_x64.dll prologues:
    // ContainsFile (0x679610, 16B): 48 89 5C 24 10 55 56 57 41 54 41 55 41 56 41 57
    static const uint8_t patContainsFile[] = { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57 };
    static const char maskContainsFile[]   = "xxxxxxxxxxxxxxxx";

    // ReadFile (0x6799A0, 15B): 48 89 5C 24 18 48 89 54 24 10 55 56 57 41 54
    static const uint8_t patReadFile[] = { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x48, 0x89, 0x54, 0x24, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54 };
    static const char maskReadFile[]   = "xxxxxxxxxxxxxxx";

    // Open (0x67A620, 15B): 48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20
    static const uint8_t patOpen[] = { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20 };
    static const char maskOpen[]   = "xxxxxxxxxxxxxxx";

    // Close (0x679E70, 15B): 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18
    static const uint8_t patClose[] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18 };

    // IsOpen (0x679CE0, 17B): 8B 81 A8 00 00 00 A8 01 74 0F 48 8B 09 48 8B 41 48
    static const uint8_t patIsOpen[] = { 0x8B, 0x81, 0xA8, 0x00, 0x00, 0x00, 0xA8, 0x01, 0x74, 0x0F, 0x48, 0x8B, 0x09, 0x48, 0x8B, 0x41, 0x48 };

    // GetFileList (0x679270, 15B): 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18
    static const uint8_t patGetFileList[] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18 };

    // GetNextFileAtPath (0x679410, 15B): 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18
    static const uint8_t patGetNextFileAtPath[] = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18 };

    if (memcmp(pFuncContainsFile, patContainsFile, sizeof(patContainsFile)) != 0) {
        uint8_t* aob = FindAobPattern(hK2, patContainsFile, maskContainsFile);
        if (aob) pFuncContainsFile = aob;
    }
    if (memcmp(pFuncReadFile, patReadFile, sizeof(patReadFile)) != 0) {
        uint8_t* aob = FindAobPattern(hK2, patReadFile, maskReadFile);
        if (aob) pFuncReadFile = aob;
    }
    if (memcmp(pFuncOpen, patOpen, sizeof(patOpen)) != 0) {
        uint8_t* aob = FindAobPattern(hK2, patOpen, maskOpen);
        if (aob) pFuncOpen = aob;
    }

    bool b1 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::Open", pFuncOpen, reinterpret_cast<void*>(Hooked_CArchive_Open),
        reinterpret_cast<void**>(&g_origCArchiveOpen), 15, patOpen, sizeof(patOpen));
    bool b2 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::ReadFile", pFuncReadFile, reinterpret_cast<void*>(Hooked_CArchive_ReadFile),
        reinterpret_cast<void**>(&g_origCArchiveReadFile), 15, patReadFile, sizeof(patReadFile));
    bool b3 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::ContainsFile", pFuncContainsFile, reinterpret_cast<void*>(Hooked_CArchive_ContainsFile),
        reinterpret_cast<void**>(&g_origCArchiveContainsFile), 16, patContainsFile, sizeof(patContainsFile));
    bool b4 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::Close", pFuncClose, reinterpret_cast<void*>(Hooked_CArchive_Close),
        reinterpret_cast<void**>(&g_origCArchiveClose), 15, patClose, sizeof(patClose));
    // (#268) Full-replacement hook: CArchive::IsOpen contains a short je +0x0F at byte 9 and never calls original
    bool b5 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::IsOpen", pFuncIsOpen, reinterpret_cast<void*>(Hooked_CArchive_IsOpen),
        nullptr, 17, patIsOpen, sizeof(patIsOpen));
    bool b6 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::GetFileList", pFuncGetFileList, reinterpret_cast<void*>(Hooked_CArchive_GetFileList),
        reinterpret_cast<void**>(&g_origCArchiveGetFileList), 15, patGetFileList, sizeof(patGetFileList));
    bool b7 = Kongor::PatchController::InstallManagedInlineHook64(
        "vfs", "CArchive::GetNextFileAtPath", pFuncGetNextFileAtPath, reinterpret_cast<void*>(Hooked_CArchive_GetNextFileAtPath),
        reinterpret_cast<void**>(&g_origCArchiveGetNextFileAtPath), 15, patGetNextFileAtPath, sizeof(patGetNextFileAtPath));

    if (b1 && b2 && b3 && b4 && b5 && b6 && b7) {
        s_installed = true;
        Kongor::PatchController::RegisterGroupCallback("vfs", RestoreOrReapplyArchiveVectorsForToggle, true);
        LogArchive("[KONGOR ARCHIVE] [SUCCESS] All 7 modern miniz + libdeflate archive hooks installed into k2_x64.dll via RuntimePatchController!\n");
        LogArchive("                 - CArchive::Open              @ %p (Hooked)\n", pFuncOpen);
        LogArchive("                 - CArchive::ReadFile          @ %p (Hooked)\n", pFuncReadFile);
        LogArchive("                 - CArchive::ContainsFile      @ %p (Hooked)\n", pFuncContainsFile);
        LogArchive("                 - CArchive::Close             @ %p (Hooked)\n", pFuncClose);
        LogArchive("                 - CArchive::IsOpen            @ %p (Hooked)\n", pFuncIsOpen);
        LogArchive("                 - CArchive::GetFileList       @ %p (Hooked)\n", pFuncGetFileList);
        LogArchive("                 - CArchive::GetNextFileAtPath @ %p (Hooked)\n", pFuncGetNextFileAtPath);

        // Proactively scan and register any archives already open in CFileManager
        ProactivelyScanFileManager(pBase);

        return true;
    } else {
        LogArchive("[KONGOR ARCHIVE] [ERROR] Hook installation failed: Open=%d ReadFile=%d Contains=%d Close=%d IsOpen=%d GetFileList=%d GetNextFileAtPath=%d\n",
            b1, b2, b3, b4, b5, b6, b7);
        return false;
    }
}

extern "C" bool InstallConsoleHooks(HMODULE hK2);

extern "C" void LogArchiveTelemetrySummary() {
    std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex, std::try_to_lock);
    if (!lock.owns_lock() || g_archiveMap.empty()) return;

    uint64_t sig = 14695981039346656037ULL;
    auto mix64 = [](uint64_t& h, uint64_t v) {
        const uint64_t prime = 1099511628211ULL;
        for (int i = 0; i < 8; ++i) {
            uint8_t byte = static_cast<uint8_t>((v >> (i * 8)) & 0xFF);
            h ^= byte;
            h *= prime;
        }
    };
    mix64(sig, static_cast<uint64_t>(g_archiveMap.size()));
    for (const auto& kv : g_archiveMap) {
        const auto& arch = kv.second;
        if (!arch) continue;
        const uint64_t prime = 1099511628211ULL;
        for (wchar_t wc : arch->disk_path) {
            sig ^= static_cast<uint8_t>(wc & 0xFF);
            sig *= prime;
            sig ^= static_cast<uint8_t>((wc >> 8) & 0xFF);
            sig *= prime;
        }
        mix64(sig, static_cast<uint64_t>(arch->file_list.size()));
        mix64(sig, arch->lookupsCount.load(std::memory_order_relaxed));
        mix64(sig, arch->hitsCount.load(std::memory_order_relaxed));
        mix64(sig, arch->libdeflateCount.load(std::memory_order_relaxed));
        mix64(sig, arch->storedDirectCount.load(std::memory_order_relaxed));
        mix64(sig, arch->minizFallbackCount.load(std::memory_order_relaxed));
        mix64(sig, arch->s2zdirOverrideCount.load(std::memory_order_relaxed));
        mix64(sig, arch->canaryCorruptCount.load(std::memory_order_relaxed));
        mix64(sig, arch->ddsTruncatedCount.load(std::memory_order_relaxed));
    }

    static std::atomic<uint64_t> s_lastTelemetrySummarySig{ 0 };
    if (sig != 0 && s_lastTelemetrySummarySig.exchange(sig, std::memory_order_acq_rel) == sig) {
        FlushArchiveLog();
        return;
    }

    LogArchive("=================================================================\n");
    LogArchive("          KONGOR VFS MODERN ARCHIVE TELEMETRY SUMMARY            \n");
    LogArchive("=================================================================\n");
    LogArchive("Active Archives Registered: %zu\n", g_archiveMap.size());
    for (const auto& kv : g_archiveMap) {
        const auto& arch = kv.second;
        if (!arch) continue;
        uint64_t lookups = arch->lookupsCount.load(std::memory_order_relaxed);
        uint64_t hits = arch->hitsCount.load(std::memory_order_relaxed);
        uint64_t totalNs = arch->totalLookupNs.load(std::memory_order_relaxed);
        double avgNs = lookups > 0 ? (double)totalNs / (double)lookups : 0.0;
        double hitPct = lookups > 0 ? ((double)hits * 100.0) / (double)lookups : 0.0;
        LogArchive("Archive: '%ls' (Entries: %u, Cap: %zu, Zip64: %s)\n",
            arch->disk_path.c_str(), (mz_uint)arch->file_list.size(), arch->flat_map.Capacity(),
            arch->is_zip64 ? "YES" : "NO");
        LogArchive("  -> Lookups: %llu, Hits: %llu (%.1f%%), Avg Lookup: %.2f ns\n",
            lookups, hits, hitPct, avgNs);
        LogArchive("  -> AVX2 libdeflate: %llu, Zero-Copy Stored: %llu, miniz Fallback: %llu, .s2zdir: %llu, CanaryCorrupt: %llu, DdsTrunc: %llu\n",
            arch->libdeflateCount.load(std::memory_order_relaxed),
            arch->storedDirectCount.load(std::memory_order_relaxed),
            arch->minizFallbackCount.load(std::memory_order_relaxed),
            arch->s2zdirOverrideCount.load(std::memory_order_relaxed),
            arch->canaryCorruptCount.load(std::memory_order_relaxed),
            arch->ddsTruncatedCount.load(std::memory_order_relaxed));
    }
    LogArchive("=================================================================\n");
    FlushArchiveLog();
}

// Xash3D FS_Path_f equivalent: dumps all active archives and .s2zdir overlays under shared_lock (#135)
// satisfies: OBL-W3-01
extern "C" void LogArchiveSearchPaths() {
    std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);
    LogArchive("=================================================================\n");
    LogArchive("          KONGOR VFS SEARCH PATHS (vfs_path / FS_Path_f)         \n");
    LogArchive("=================================================================\n");
    LogArchive("Mounted Archives: %zu\n", g_archiveMap.size());
    size_t idx = 0;
    for (const auto& kv : g_archiveMap) {
        const auto& arch = kv.second;
        if (!arch) continue;
        LogArchive("  [%02zu] [R %s%s] '%ls' (%u files, master='%ls')\n",
            idx++,
            arch->is_zip64 ? "Z64 " : "S2Z ",
            arch->has_s2zdir ? "S2ZDIR" : "------",
            arch->disk_path.c_str(),
            static_cast<unsigned int>(arch->file_list.size()),
            arch->master_clean_w.c_str());
        if (arch->has_s2zdir) {
            LogArchive("       -> Unpacked Overlay (.s2zdir): '%ls'\n", arch->s2zdir_path.c_str());
        }
    }
    LogArchive("=================================================================\n");
}

// Xash3D FS_FindFile_f equivalent: searches all mounted archives for a virtual path or glob pattern (#135)
// satisfies: OBL-W3-01
extern "C" __declspec(dllexport) uint32_t Kongor_NativeGuard_VfsFindFile(const char* pattern, char* outBuf, uint32_t maxLen) {
    if (!pattern || pattern[0] == '\0') return 0;

    const char* cleanPat = StripVirtualPrefixA(pattern);
    bool hasWildcard = (strchr(cleanPat, '*') != nullptr) || (strchr(cleanPat, '?') != nullptr);

    std::string report;
    report.reserve(1024);
    report += "[KONGOR VFS FIND] Query: '";
    report += cleanPat;
    report += "'\n";

    uint32_t matchCount = 0;
    bool texturesS2zShadowSeen = false;

    std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex);

    if (!hasWildcard) {
        uint64_t hTarget = HashNormalizedPathA(cleanPat);
        for (const auto& kv : g_archiveMap) {
            const auto& arch = kv.second;
            if (!arch) continue;

            uint32_t fileIndex = FLAT_MAP_EMPTY;
            bool found = false;
            {
                std::shared_lock<std::shared_mutex> archLock(arch->mutex);
                found = arch->flat_map.Find(hTarget, fileIndex);
            }
            if (found && fileIndex < arch->file_list.size()) {
                char archPathUtf8[MAX_PATH * 2] = { 0 };
                WideCharToMultiByte(CP_UTF8, 0, arch->disk_path.c_str(), -1, archPathUtf8, sizeof(archPathUtf8), NULL, NULL);

                const char* roleTag = (matchCount == 0) ? "[WINNER]" : "[SHADOWED]";
                if (Kongor_MatchWildcardA(archPathUtf8, "*textures.s2z*", true)) {
                    texturesS2zShadowSeen = true;
                }

                char lineBuf[1024];
                snprintf(lineBuf, sizeof(lineBuf), "  %s '%s' in '%s' (index=%u%s)\n",
                    roleTag,
                    arch->file_list[fileIndex].c_str(),
                    archPathUtf8,
                    fileIndex,
                    arch->has_s2zdir ? ", +s2zdir" : "");
                report += lineBuf;
                ++matchCount;
            }
        }
    } else {
        for (const auto& kv : g_archiveMap) {
            const auto& arch = kv.second;
            if (!arch) continue;

            char archPathUtf8[MAX_PATH * 2] = { 0 };
            WideCharToMultiByte(CP_UTF8, 0, arch->disk_path.c_str(), -1, archPathUtf8, sizeof(archPathUtf8), NULL, NULL);

            std::shared_lock<std::shared_mutex> archLock(arch->mutex);
            for (size_t i = 0; i < arch->file_list.size(); ++i) {
                if (Kongor_MatchWildcardA(arch->file_list[i].c_str(), cleanPat, true)) {
                    const char* roleTag = (matchCount == 0) ? "[MATCH #1]" : "[MATCH]";
                    char lineBuf[1024];
                    snprintf(lineBuf, sizeof(lineBuf), "  %s '%s' in '%s' (index=%zu)\n",
                        roleTag, arch->file_list[i].c_str(), archPathUtf8, i);
                    report += lineBuf;
                    ++matchCount;
                    if (matchCount >= 64) break;
                }
            }
            if (matchCount >= 64) break;
        }
    }

    if (texturesS2zShadowSeen && matchCount > 1) {
        report += "  [WARNING] Legacy textures.s2z entry detected! CResourceManager::RegisterTexture gives textures.s2z (00000000/ui/...) special precedence that can shadow override/.\n";
    }
    if (matchCount == 0) {
        report += "  (0 matches across mounted archives)\n";
    }

    LogArchive("%s", report.c_str());

    if (outBuf && maxLen > 0) {
        size_t copyLen = (report.size() < static_cast<size_t>(maxLen - 1)) ? report.size() : static_cast<size_t>(maxLen - 1);
        memcpy(outBuf, report.data(), copyLen);
        outBuf[copyLen] = '\0';
    }

    return matchCount;
}

extern "C" void LogArchiveFindFile(const char* pattern) {
    Kongor_NativeGuard_VfsFindFile(pattern, nullptr, 0);
}

DWORD WINAPI ModernArchiveWatcherWorker(LPVOID) {
    LogArchive("[KONGOR ARCHIVE] ModernArchiveWatcherWorker started. Polling for k2_x64.dll...\n");

    for (int attempts = 0; attempts < 1000; ++attempts) {
        HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
        if (hK2) {
            if (InstallModernArchiveHooks(hK2)) {
                InstallConsoleHooks(hK2);
                LogArchive("[KONGOR ARCHIVE] Modern archive and console hooks successfully attached via watcher thread.\n");
                return 0;
            }
        }
        Sleep(attempts < 100 ? 1 : 10);
    }

    LogArchive("[KONGOR ARCHIVE] Timed out waiting for k2_x64.dll in watcher worker.\n");
    return 1;
}
