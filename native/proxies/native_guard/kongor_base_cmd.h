// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
// ============================================================================
// File: kongor_base_cmd.h
// Purpose: Xash3D-FWGS BaseCmd 128-bucket (0x7F mask) sorted-chain O(1)
//          console command hash table (#136) and quote-balanced command
//          buffer splitter + privilege filter (#137).
// ============================================================================

#include <cstdint>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>
#include <atomic>

namespace Kongor {

enum class KongorConsoleCmdId : uint32_t {
    None = 0,
    EvalRes = 1,
    PrewarmHero = 2,
    PrewarmMap = 3,
    VfsStats = 4,
    ArchiveStats = 5,
    VfsPath = 6,
    VfsFind = 7,
    KongorHookToggle = 8,
    VideoSeek = 9,
    SetChatMode = 10,
    ArcadeMode = 11,
    ArcadeReserve = 12,
    ArcadeRelease = 13,
    PrewarmPostIntro = 14,
    PrewarmForegroundBusy = 15,
    PrewarmCancel = 16,
    PrewarmTelemetry = 17,
    ArcadeSwapHooks = 18
};

struct BaseCmdNode {
    const wchar_t* NameW;
    const char* NameA;
    KongorConsoleCmdId Id;
    bool RequiresArgs;
    BaseCmdNode* Next;
};

// Xash3D engine/common/base_cmd.c 128-bucket case-insensitive hash table with
// alphabetically sorted bucket chains for early termination (cmp > 0 -> break) (#136)
// satisfies: OBL-W3-02
class BaseCmdTable {
public:
    static constexpr size_t kBucketCount = 128;
    static constexpr size_t kBucketMask = kBucketCount - 1; // 0x7F

    BaseCmdTable() noexcept {
        for (size_t i = 0; i < kBucketCount; ++i) {
            m_buckets[i] = nullptr;
        }
    }

    static inline wchar_t ToLowerAsciiW(wchar_t c) noexcept {
        return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + 32) : c;
    }

    // Com_HashKey case-insensitive hash masked to 128 buckets (0x7F)
    static inline size_t HashTokenW(const wchar_t* token, size_t len) noexcept {
        uint32_t hash = 0u;
        for (size_t i = 0; i < len; ++i) {
            uint32_t ch = static_cast<uint32_t>(ToLowerAsciiW(token[i]));
            hash = (hash * 33u) ^ ch;
        }
        return static_cast<size_t>(hash) & kBucketMask;
    }

    static int CompareTokenW(const wchar_t* a, const wchar_t* b, size_t bLen) noexcept {
        for (size_t i = 0; i < bLen; ++i) {
            wchar_t ca = ToLowerAsciiW(a[i]);
            wchar_t cb = ToLowerAsciiW(b[i]);
            if (ca == L'\0' || ca != cb) {
                return static_cast<int>(ca) - static_cast<int>(cb);
            }
        }
        return static_cast<int>(ToLowerAsciiW(a[bLen]));
    }

    // BaseCmd_Insert: inserts node in ascending case-insensitive alphabetical order within its bucket
    void Insert(BaseCmdNode* node) noexcept {
        if (!node || !node->NameW) return;
        size_t len = wcslen(node->NameW);
        size_t bucket = HashTokenW(node->NameW, len);

        BaseCmdNode* prev = nullptr;
        BaseCmdNode* cur = m_buckets[bucket];
        while (cur != nullptr) {
            int cmp = _wcsicmp(cur->NameW, node->NameW);
            if (cmp >= 0) {
                break;
            }
            prev = cur;
            cur = cur->Next;
        }

        node->Next = cur;
        if (prev == nullptr) {
            m_buckets[bucket] = node;
        } else {
            prev->Next = node;
        }
    }

    // BaseCmd_FindInBucket: O(1) lookup with sorted-chain early exit (cmp > 0 -> break) (#136)
    // satisfies: OBL-W3-02
    const BaseCmdNode* FindToken(const wchar_t* tokenStart, size_t tokenLen) const noexcept {
        if (!tokenStart || tokenLen == 0) return nullptr;
        m_totalLookups.fetch_add(1, std::memory_order_relaxed);

        size_t bucket = HashTokenW(tokenStart, tokenLen);
        const BaseCmdNode* cur = m_buckets[bucket];
        while (cur != nullptr) {
            int cmp = CompareTokenW(cur->NameW, tokenStart, tokenLen);
            if (cmp == 0) {
                m_totalHits.fetch_add(1, std::memory_order_relaxed);
                return cur;
            }
            if (cmp > 0) {
                // Sorted chain invariant: all subsequent entries in this bucket are lexicographically greater
                m_earlyTerminations.fetch_add(1, std::memory_order_relaxed);
                return nullptr;
            }
            cur = cur->Next;
        }
        return nullptr;
    }

    // Extracts the first whitespace-delimited command token and looks it up in O(1)
    const BaseCmdNode* MatchCommandLine(const wchar_t* commandLine, const wchar_t** outArgsStart) const noexcept {
        if (outArgsStart) *outArgsStart = L"";
        if (!commandLine || !*commandLine) return nullptr;

        const wchar_t* p = commandLine;
        while (*p == L' ' || *p == L'\t') ++p;
        if (!*p) return nullptr;

        const wchar_t* tokenStart = p;
        while (*p && *p != L' ' && *p != L'\t' && *p != L'\r' && *p != L'\n') {
            ++p;
        }
        size_t tokenLen = static_cast<size_t>(p - tokenStart);

        const BaseCmdNode* matched = FindToken(tokenStart, tokenLen);
        if (!matched) return nullptr;

        while (*p == L' ' || *p == L'\t') ++p;
        if (matched->RequiresArgs && *p == L'\0') {
            return nullptr;
        }
        if (outArgsStart) *outArgsStart = p;
        return matched;
    }

private:
    BaseCmdNode* m_buckets[kBucketCount];
    mutable std::atomic<uint64_t> m_totalLookups{ 0 };
    mutable std::atomic<uint64_t> m_totalHits{ 0 };
    mutable std::atomic<uint64_t> m_earlyTerminations{ 0 };
};

inline const BaseCmdTable& GetGlobalBaseCmdTable() noexcept {
    static BaseCmdNode s_nodes[] = {
        { L"__kongor_eval_res",  "__kongor_eval_res",  KongorConsoleCmdId::EvalRes,          true,  nullptr },
        { L"archive_stats",      "archive_stats",      KongorConsoleCmdId::ArchiveStats,     false, nullptr },
        { L"kongor_hook_toggle",      "kongor_hook_toggle",      KongorConsoleCmdId::KongorHookToggle,      false, nullptr },
        { L"prewarm_cancel",          "prewarm_cancel",          KongorConsoleCmdId::PrewarmCancel,         false, nullptr },
        { L"prewarm_foreground_busy", "prewarm_foreground_busy", KongorConsoleCmdId::PrewarmForegroundBusy, true,  nullptr },
        { L"prewarm_hero",            "prewarm_hero",            KongorConsoleCmdId::PrewarmHero,           true,  nullptr },
        { L"prewarm_map",             "prewarm_map",             KongorConsoleCmdId::PrewarmMap,            true,  nullptr },
        { L"prewarm_post_intro",      "prewarm_post_intro",      KongorConsoleCmdId::PrewarmPostIntro,      false, nullptr },
        { L"prewarm_telemetry",       "prewarm_telemetry",       KongorConsoleCmdId::PrewarmTelemetry,      true,  nullptr },
        { L"vfs_find",           "vfs_find",           KongorConsoleCmdId::VfsFind,          true,  nullptr },
        { L"vfs_path",           "vfs_path",           KongorConsoleCmdId::VfsPath,          false, nullptr },
        { L"vfs_stats",          "vfs_stats",          KongorConsoleCmdId::VfsStats,         false, nullptr },
        { L"video_seek",         "video_seek",         KongorConsoleCmdId::VideoSeek,        true,  nullptr },
        { L"kongorsetchatmode",  "kongorsetchatmode",  KongorConsoleCmdId::SetChatMode,      true,  nullptr },
        // satisfies: OBL-QA-05
        { L"kongor_arcade_mode",       "kongor_arcade_mode",       KongorConsoleCmdId::ArcadeMode,      true, nullptr },
        { L"kongor_arcade_reserve",    "kongor_arcade_reserve",    KongorConsoleCmdId::ArcadeReserve,   true, nullptr },
        { L"kongor_arcade_release",    "kongor_arcade_release",    KongorConsoleCmdId::ArcadeRelease,   true, nullptr },
        { L"kongor_arcade_swap_hooks", "kongor_arcade_swap_hooks", KongorConsoleCmdId::ArcadeSwapHooks, true, nullptr }
    };
    static BaseCmdTable s_table;
    static const bool s_initialized = []() {
        for (size_t i = 0; i < sizeof(s_nodes) / sizeof(s_nodes[0]); ++i) {
            s_table.Insert(&s_nodes[i]);
        }
        return true;
    }();
    (void)s_initialized;
    return s_table;
}

// Xash3D engine/common/cmd.c Cbuf_ExecuteCommandsFromBuffer quote-balanced ';' splitter
// and Cmd_ShouldAllowCommand privilege firewall (#137)
// satisfies: OBL-W3-03
struct CmdSanitizer {
    static constexpr size_t MAX_CMD_LINE = 2048;

    static bool ShouldAllowCommandW(const std::wstring& cmdLine, bool isPrivileged) noexcept {
        size_t start = 0;
        while (start < cmdLine.size() && (cmdLine[start] == L' ' || cmdLine[start] == L'\t')) {
            ++start;
        }
        if (start >= cmdLine.size()) return false;
        if (isPrivileged) return true;

        size_t end = start;
        while (end < cmdLine.size() && cmdLine[end] != L' ' && cmdLine[end] != L'\t') {
            ++end;
        }
        std::wstring firstToken = cmdLine.substr(start, end - start);
        if (firstToken[0] == L'@' ||
            _wcsnicmp(firstToken.c_str(), L"__kongor_", 9) == 0 ||
            _wcsnicmp(firstToken.c_str(), L"kongor_", 7) == 0 ||
            _wcsicmp(firstToken.c_str(), L"quit") == 0 ||
            _wcsicmp(firstToken.c_str(), L"crash") == 0) {
            return false;
        }
        return true;
    }

    static void SplitAndValidateCommandBufferW(
        const std::wstring& buffer,
        bool isPrivileged,
        std::vector<std::wstring>& outCommands,
        uint32_t& outBlockedCount,
        bool& outTruncated)
    {
        outCommands.clear();
        outBlockedCount = 0;
        outTruncated = false;
        if (buffer.empty()) return;

        size_t lineStart = 0;
        bool inQuotes = false;
        size_t len = buffer.size();

        auto emitSegment = [&](size_t segStart, size_t segLen) {
            while (segLen > 0 && (buffer[segStart] == L' ' || buffer[segStart] == L'\t' || buffer[segStart] == L'\r' || buffer[segStart] == L'\n')) {
                ++segStart;
                --segLen;
            }
            while (segLen > 0 && (buffer[segStart + segLen - 1] == L' ' || buffer[segStart + segLen - 1] == L'\t' || buffer[segStart + segLen - 1] == L'\r' || buffer[segStart + segLen - 1] == L'\n')) {
                --segLen;
            }
            if (segLen == 0) return;

            if (segLen > MAX_CMD_LINE) {
                segLen = MAX_CMD_LINE;
                outTruncated = true;
            }

            std::wstring cmd = buffer.substr(segStart, segLen);
            if (!ShouldAllowCommandW(cmd, isPrivileged)) {
                ++outBlockedCount;
                return;
            }
            outCommands.push_back(std::move(cmd));
        };

        auto isCurrentSegmentScriptCmd = [&]() -> bool {
            size_t s = lineStart;
            while (s < len && (buffer[s] == L' ' || buffer[s] == L'\t')) {
                ++s;
            }
            return (s + 7 <= len && (_wcsnicmp(buffer.c_str() + s, L"Script ", 7) == 0 || _wcsnicmp(buffer.c_str() + s, L"Script\t", 7) == 0));
        };

        for (size_t i = 0; i <= len; ++i) {
            bool atEnd = (i == len);
            bool isDelimiter = false;

            if (!atEnd) {
                wchar_t c = buffer[i];
                if (inQuotes && c == L'\\' && (i + 1) < len && (buffer[i + 1] == L'"' || buffer[i + 1] == L'\\')) {
                    ++i;
                    continue;
                }
                if (c == L'"') {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (!inQuotes && c == L'/' && (i + 1) < len && buffer[i + 1] == L'/') {
                    size_t commentEnd = i + 2;
                    while (commentEnd < len && buffer[commentEnd] != L'\n' && buffer[commentEnd] != L'\r') {
                        ++commentEnd;
                    }
                    emitSegment(lineStart, i - lineStart);
                    i = commentEnd;
                    lineStart = commentEnd + 1;
                    inQuotes = false;
                    continue;
                }
                if (!inQuotes && (c == L'\n' || c == L'\r' || (c == L';' && !isCurrentSegmentScriptCmd()))) {
                    isDelimiter = true;
                }
            }

            if (atEnd || isDelimiter) {
                emitSegment(lineStart, i - lineStart);
                lineStart = i + 1;
                if (!atEnd && (buffer[i] == L'\n' || buffer[i] == L'\r')) {
                    inQuotes = false;
                }
            }
        }
    }
};

struct DispatchedConsoleSegment {
    bool IsNativeBaseCmd;
    KongorConsoleCmdId CmdId;
    std::wstring CommandOrArgs;
};

// Splits a compound ';' / '\n' / '\r' console buffer into ordered NativeGuard BaseCmd
// vs. K2 engine command segments whenever at least one segment is a NativeGuard BaseCmd.
// Prevents mid-batch NativeGuard commands (e.g. "...;kongor_arcade_mode 1;...") from
// leaking into K2 CConsole::DoCommand as "Unknown identifier", and prevents leading-batch
// NativeGuard commands (e.g. "kongor_arcade_mode 1;sv_gameEndPhaseTime 500;...") from
// swallowing trailing K2 CVars.
// satisfies: OBL-QA-05
inline bool PartitionCompoundBaseCmdBufferW(
    const wchar_t* rawCommandLine,
    std::vector<DispatchedConsoleSegment>& outSegments)
{
    outSegments.clear();
    if (!rawCommandLine || !*rawCommandLine) {
        return false;
    }

    bool hasCompoundDelimiter = false;
    for (const wchar_t* p = rawCommandLine; *p != L'\0'; ++p) {
        if (*p == L';' || *p == L'\n' || *p == L'\r') {
            hasCompoundDelimiter = true;
            break;
        }
    }
    if (!hasCompoundDelimiter) {
        return false;
    }

    // Never split single-line __kongor_eval_res responses whose payload may carry raw ';'
    const wchar_t* firstArgs = nullptr;
    const BaseCmdNode* firstMatch = GetGlobalBaseCmdTable().MatchCommandLine(rawCommandLine, &firstArgs);
    if (firstMatch != nullptr && firstMatch->Id == KongorConsoleCmdId::EvalRes) {
        return false;
    }

    std::vector<std::wstring> splitSegments;
    uint32_t blockedCount = 0;
    bool truncated = false;
    CmdSanitizer::SplitAndValidateCommandBufferW(
        std::wstring(rawCommandLine),
        /*isPrivileged=*/true,
        splitSegments,
        blockedCount,
        truncated);

    if (splitSegments.empty()) {
        return false;
    }

    bool anyNativeBaseCmd = false;
    for (size_t i = 0; i < splitSegments.size(); ++i) {
        const wchar_t* segArgs = nullptr;
        if (GetGlobalBaseCmdTable().MatchCommandLine(splitSegments[i].c_str(), &segArgs) != nullptr) {
            anyNativeBaseCmd = true;
            break;
        }
    }
    if (!anyNativeBaseCmd) {
        return false;
    }

    // Single BaseCmd with trailing ';' (e.g. "kongor_arcade_mode 1;") must also strip ';' cleanly
    for (size_t i = 0; i < splitSegments.size(); ++i) {
        const wchar_t* segArgs = nullptr;
        const BaseCmdNode* matched = GetGlobalBaseCmdTable().MatchCommandLine(splitSegments[i].c_str(), &segArgs);
        if (matched != nullptr) {
            DispatchedConsoleSegment seg{};
            seg.IsNativeBaseCmd = true;
            seg.CmdId = matched->Id;
            seg.CommandOrArgs = segArgs ? std::wstring(segArgs) : std::wstring();
            outSegments.push_back(std::move(seg));
        } else {
            if (!outSegments.empty() && !outSegments.back().IsNativeBaseCmd) {
                outSegments.back().CommandOrArgs.append(L";");
                outSegments.back().CommandOrArgs.append(splitSegments[i]);
            } else {
                DispatchedConsoleSegment seg{};
                seg.IsNativeBaseCmd = false;
                seg.CmdId = KongorConsoleCmdId::None;
                seg.CommandOrArgs = splitSegments[i];
                outSegments.push_back(std::move(seg));
            }
        }
    }

    return !outSegments.empty();
}

// ============================================================================
// #222 (Gem 3H): Zero-Allocation Hierarchical QPC Microsecond Call-Tree Profiler
// Inspired by obs-studio/libobs/util/profiler.c:1-600
// Tracks min_us, max_us, p99_us, overbudget_count (<= 1000 us / 1.0 ms), and hitch5MsCount (> 5000 us / 5.0 ms).
// ============================================================================
class KongorQpcCallTreeProfiler {
public:
    static constexpr size_t MAX_PROFILE_NODES      = 64;
    static constexpr size_t SAMPLE_WINDOW_CAPACITY = 128;
    static constexpr uint64_t DEFAULT_BUDGET_US    = 1000ULL; // 1.0 ms Lua UI & IPC frame budget
    static constexpr uint64_t HITCH_THRESHOLD_US   = 5000ULL; // 5.0 ms hitch warning threshold

    struct ProfileNode {
        const char* name                       = nullptr; // Pointer-identity interned scope name
        int32_t parentIndex                    = -1;
        uint64_t callCount                     = 0;
        uint64_t totalUs                       = 0;
        uint64_t minUs                         = UINT64_MAX;
        uint64_t maxUs                         = 0;
        uint64_t p99Us                         = 0;
        uint64_t overbudgetCount               = 0;
        uint64_t hitch5MsCount                 = 0;
        uint64_t budgetThresholdUs             = DEFAULT_BUDGET_US;
        uint64_t samplesUs[SAMPLE_WINDOW_CAPACITY] = {};
        uint32_t sampleWriteIndex              = 0;
        uint32_t sampleCount                   = 0;
    };

    KongorQpcCallTreeProfiler() noexcept {
        Reset();
    }

    static uint64_t QueryCurrentQpcUs() noexcept {
        LARGE_INTEGER freq = {};
        LARGE_INTEGER counter = {};
        if (!QueryPerformanceFrequency(&freq) || freq.QuadPart <= 0 || !QueryPerformanceCounter(&counter)) {
            return GetTickCount64() * 1000ULL;
        }
        return static_cast<uint64_t>((counter.QuadPart * 1000000ULL) / freq.QuadPart);
    }

    void Reset() noexcept {
        m_nodeCount = 0;
        m_activeNodeIndex = -1;
        for (size_t i = 0; i < MAX_PROFILE_NODES; ++i) {
            m_nodes[i] = ProfileNode{};
        }
    }

    int32_t EnterScope(const char* internedScopeName, uint64_t budgetThresholdUs = DEFAULT_BUDGET_US) noexcept {
        if (!internedScopeName) return -1;
        int32_t parentIdx = m_activeNodeIndex;
        int32_t foundIdx = -1;
        for (size_t i = 0; i < m_nodeCount; ++i) {
            if (m_nodes[i].parentIndex == parentIdx &&
                (m_nodes[i].name == internedScopeName || strcmp(m_nodes[i].name, internedScopeName) == 0)) {
                foundIdx = static_cast<int32_t>(i);
                break;
            }
        }
        if (foundIdx < 0 && m_nodeCount < MAX_PROFILE_NODES) {
            foundIdx = static_cast<int32_t>(m_nodeCount++);
            m_nodes[foundIdx].name = internedScopeName;
            m_nodes[foundIdx].parentIndex = parentIdx;
            m_nodes[foundIdx].budgetThresholdUs = budgetThresholdUs;
        }
        if (foundIdx >= 0) {
            m_activeNodeIndex = foundIdx;
        }
        return foundIdx;
    }

    int32_t BeginScopeQpc(const char* internedScopeName, uint64_t* outStartQpcUs, uint64_t budgetThresholdUs = DEFAULT_BUDGET_US) noexcept {
        if (outStartQpcUs) {
            *outStartQpcUs = QueryCurrentQpcUs();
        }
        return EnterScope(internedScopeName, budgetThresholdUs);
    }

    void EndScopeQpc(int32_t nodeIndex, uint64_t startQpcUs) noexcept {
        const uint64_t endQpcUs = QueryCurrentQpcUs();
        const uint64_t elapsedUs = (endQpcUs >= startQpcUs) ? (endQpcUs - startQpcUs) : 0ULL;
        LeaveScope(nodeIndex, elapsedUs);
    }

    void LeaveScope(int32_t nodeIndex, uint64_t elapsedUs) noexcept {
        if (nodeIndex < 0 || static_cast<size_t>(nodeIndex) >= m_nodeCount) return;
        ProfileNode& node = m_nodes[nodeIndex];
        ++node.callCount;
        node.totalUs += elapsedUs;
        if (elapsedUs < node.minUs) node.minUs = elapsedUs;
        if (elapsedUs > node.maxUs) node.maxUs = elapsedUs;
        if (elapsedUs > node.budgetThresholdUs) {
            ++node.overbudgetCount;
        }
        if (elapsedUs > HITCH_THRESHOLD_US) {
            ++node.hitch5MsCount;
        }

        node.samplesUs[node.sampleWriteIndex % SAMPLE_WINDOW_CAPACITY] = elapsedUs;
        ++node.sampleWriteIndex;
        if (node.sampleCount < SAMPLE_WINDOW_CAPACITY) {
            ++node.sampleCount;
        }
        node.p99Us = ComputeP99SampleUs(node);
        m_activeNodeIndex = node.parentIndex;
    }

    void RecordSampleDirect(const char* internedScopeName, uint64_t elapsedUs, uint64_t budgetThresholdUs = DEFAULT_BUDGET_US) noexcept {
        int32_t idx = EnterScope(internedScopeName, budgetThresholdUs);
        if (idx >= 0) {
            LeaveScope(idx, elapsedUs);
        }
    }

    size_t FormatCsvSnapshot(char* outBuffer, size_t capacity) const noexcept {
        if (!outBuffer || capacity == 0) return 0;
        int written = snprintf(outBuffer, capacity, "scope,parent_index,calls,min_us,max_us,p99_us,overbudget_count,hitch_5ms_count\n");
        if (written < 0) {
            outBuffer[0] = '\0';
            return 0;
        }
        size_t offset = (static_cast<size_t>(written) < capacity) ? static_cast<size_t>(written) : (capacity - 1);
        for (size_t i = 0; i < m_nodeCount && offset + 1 < capacity; ++i) {
            const ProfileNode& n = m_nodes[i];
            const uint64_t minVal = (n.minUs == UINT64_MAX) ? 0ULL : n.minUs;
            int rowLen = snprintf(
                outBuffer + offset,
                capacity - offset,
                "%s,%d,%llu,%llu,%llu,%llu,%llu,%llu\n",
                n.name ? n.name : "unnamed",
                n.parentIndex,
                static_cast<unsigned long long>(n.callCount),
                static_cast<unsigned long long>(minVal),
                static_cast<unsigned long long>(n.maxUs),
                static_cast<unsigned long long>(n.p99Us),
                static_cast<unsigned long long>(n.overbudgetCount),
                static_cast<unsigned long long>(n.hitch5MsCount));
            if (rowLen <= 0) break;
            offset += (static_cast<size_t>(rowLen) < (capacity - offset)) ? static_cast<size_t>(rowLen) : (capacity - offset - 1);
        }
        return offset;
    }

    size_t GetNodeCount() const noexcept { return m_nodeCount; }
    const ProfileNode* GetNode(size_t index) const noexcept {
        return (index < m_nodeCount) ? &m_nodes[index] : nullptr;
    }

private:
    static uint64_t ComputeP99SampleUs(const ProfileNode& node) noexcept {
        if (node.sampleCount == 0) return 0;
        uint64_t sorted[SAMPLE_WINDOW_CAPACITY];
        for (uint32_t i = 0; i < node.sampleCount; ++i) {
            sorted[i] = node.samplesUs[i];
        }
        // Simple insertion sort over <= 128 samples (zero heap allocation)
        for (uint32_t i = 1; i < node.sampleCount; ++i) {
            uint64_t key = sorted[i];
            int32_t j = static_cast<int32_t>(i) - 1;
            while (j >= 0 && sorted[j] > key) {
                sorted[j + 1] = sorted[j];
                --j;
            }
            sorted[j + 1] = key;
        }
        uint32_t p99Idx = (node.sampleCount * 99U) / 100U;
        if (p99Idx >= node.sampleCount) p99Idx = node.sampleCount - 1;
        return sorted[p99Idx];
    }

    ProfileNode m_nodes[MAX_PROFILE_NODES];
    size_t m_nodeCount = 0;
    int32_t m_activeNodeIndex = -1;
};

} // namespace Kongor
