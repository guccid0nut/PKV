// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service interface for client console output capture, log parsing, and state scraping.
/// </summary>
public interface IClientOutputRingBuffer
{
    int Count { get; }
    bool IsEmpty { get; }
    void AppendLine(string line);
    IReadOnlyList<string> GetLines(int maxLines = 50);
    IReadOnlyList<string> GetTail(int maxLines = 50);
    IReadOnlyList<string> GetHeadAndTailSnapshot();
    void Clear();
}

/// <summary>
/// Thread-safe circular output ring buffer holding up to 500 lines of game client stdout,
/// plus a Dual Head-Tail Governor (#173) that pins the first N/2 startup lines (command-line flags,
/// VFS mount order, GPU device init) and circularly retains the latest N/2 tail lines with an
/// explicit "...[truncated K lines]..." marker when output exceeds capacity.
/// </summary>
public sealed class ClientOutputRingBuffer : IClientOutputRingBuffer
{
    public const int DefaultMaxCapacity = 500;
    private readonly Lock _sync = new();
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly List<string> _headLines;
    private readonly Queue<string> _tailQueue;
    private readonly int _maxCapacity;
    private long _totalLinesAppended;
    private long _truncatedLineCount;

    public ClientOutputRingBuffer(int maxCapacity = DefaultMaxCapacity)
    {
        _maxCapacity = maxCapacity > 0 ? maxCapacity : DefaultMaxCapacity;
        HeadCapacity = (_maxCapacity + 1) / 2;
        TailCapacity = Math.Max(0, _maxCapacity - HeadCapacity);
        _headLines = new List<string>(HeadCapacity);
        _tailQueue = new Queue<string>(Math.Max(1, TailCapacity));
    }

    public int HeadCapacity { get; }
    public int TailCapacity { get; }

    public long TotalLinesAppended
    {
        get
        {
            lock (_sync)
            {
                return _totalLinesAppended;
            }
        }
    }

    public long TruncatedLineCount
    {
        get
        {
            lock (_sync)
            {
                return _truncatedLineCount;
            }
        }
    }

    public int Count => _queue.Count;
    public bool IsEmpty => _queue.IsEmpty;

    public void AppendLine(string line)
    {
        if (line == null) return;
        _queue.Enqueue(line);
        while (_queue.Count > _maxCapacity && _queue.TryDequeue(out _))
        {
            // Evict oldest lines to maintain circular ring buffer capacity
        }

        lock (_sync)
        {
            _totalLinesAppended++;
            if (_headLines.Count < HeadCapacity)
            {
                _headLines.Add(line);
            }
            else if (TailCapacity > 0)
            {
                if (_tailQueue.Count >= TailCapacity)
                {
                    _tailQueue.Dequeue();
                    _truncatedLineCount++;
                }
                _tailQueue.Enqueue(line);
            }
            else
            {
                _truncatedLineCount++;
            }
        }
    }

    public IReadOnlyList<string> GetLines(int maxLines = 50)
    {
        string[] snapshot = _queue.ToArray();
        if (maxLines <= 0 || snapshot.Length <= maxLines)
        {
            return snapshot;
        }

        int skip = snapshot.Length - maxLines;
        string[] result = new string[maxLines];
        Array.Copy(snapshot, skip, result, 0, maxLines);
        return result;
    }

    public IReadOnlyList<string> GetTail(int maxLines = 50) => GetLines(maxLines);

    /// <summary>
    /// Returns the dual head-tail snapshot (#173): pinned startup head lines followed by
    /// "...[truncated K lines]..." (when lines were evicted) and the most recent tail lines.
    /// </summary>
    public IReadOnlyList<string> GetHeadAndTailSnapshot()
    {
        lock (_sync)
        {
            List<string> snapshot = new List<string>(_headLines.Count + _tailQueue.Count + 1);
            snapshot.AddRange(_headLines);
            if (_truncatedLineCount > 0)
            {
                snapshot.Add($"...[truncated {_truncatedLineCount} lines]...");
            }
            snapshot.AddRange(_tailQueue);
            return snapshot;
        }
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
        lock (_sync)
        {
            _headLines.Clear();
            _tailQueue.Clear();
            _totalLinesAppended = 0;
            _truncatedLineCount = 0;
        }
    }

    public static string ParseClientLogLevel(string text)
    {
        if (string.IsNullOrEmpty(text)) return "INFO";

        if (text.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UnhandledException", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Crash", StringComparison.OrdinalIgnoreCase))
            return "FATAL";

        if (text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Err:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LUA_ERR", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Error (", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Callstack:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Could not process", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Exception:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Shader compile failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DXVK error", StringComparison.OrdinalIgnoreCase))
            return "ERROR";

        if (text.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("warning:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Warn:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DXVK warn", StringComparison.OrdinalIgnoreCase))
            return "WARN";

        return "INFO";
    }

    public static bool MatchesSeverityFilter(string actualLevel, string filterLevel)
    {
        if (string.IsNullOrWhiteSpace(filterLevel) || filterLevel.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;

        if (filterLevel.Equals("error", StringComparison.OrdinalIgnoreCase))
            return actualLevel is "ERROR" or "FATAL";

        if (filterLevel.Equals("warn", StringComparison.OrdinalIgnoreCase) || filterLevel.Equals("warning", StringComparison.OrdinalIgnoreCase))
            return actualLevel is "WARN" or "ERROR" or "FATAL";

        if (filterLevel.Equals("info", StringComparison.OrdinalIgnoreCase))
            return true;

        return true;
    }

    public static string ReadTailOfLogFile(string path, int maxBytes = 65536)
    {
        try
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length == 0) return string.Empty;

            byte[] bom = new byte[4];
            int bomRead = fs.Read(bom, 0, Math.Min(4, (int)length));
            bool isUtf16Le = (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                || (bomRead >= 4 && bom[1] == 0x00 && bom[3] == 0x00);

            long offset = Math.Max(0, length - maxBytes);
            if (isUtf16Le && (offset % 2) != 0)
            {
                offset++;
            }
            fs.Seek(offset, SeekOrigin.Begin);

            Encoding encoding = isUtf16Le ? Encoding.Unicode : Encoding.UTF8;
            using StreamReader reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Replace("\0", string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientOutputRingBuffer] ReadTailOfLogFile failed for '{path}': {ex.Message}");
            return string.Empty;
        }
    }

    public static string? FindNewestConsoleLog(string baseDir)
    {
        try
        {
            string? newestLog = null;
            DateTime newestTime = DateTime.MinValue;

            string[] candidates =
            [
                Path.Combine(baseDir, "userdata", "Heroes of Newerth x64", "override", "console.log"),
                Path.Combine(baseDir, "userdata", "Heroes of Newerth x64", "base", "console.log"),
                Path.Combine(baseDir, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "console.log"),
                Path.Combine(baseDir, "userdataKONGOR", "extensions", "console.log"),
                Path.Combine(baseDir, "userdataKONGOR", "configuration", "console.log"),
                Path.Combine(baseDir, "userdataKONGOR", "updates", "console.log"),
                Path.Combine(baseDir, "userdata", "logs", "console.log"),
                Path.Combine(baseDir, "console.log")
            ];

            foreach (string file in candidates)
            {
                if (File.Exists(file))
                {
                    DateTime lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite > newestTime)
                    {
                        newestTime = lastWrite;
                        newestLog = file;
                    }
                }
            }

            return newestLog;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientOutputRingBuffer] FindNewestConsoleLog failed in '{baseDir}': {ex.Message}");
        }
        return null;
    }

    public static int TryScrapeLatestGamePhaseFromLog(string? customWorkingDirectory = null)
    {
        try
        {
            string repoRoot = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
            string baseDir = customWorkingDirectory ?? Path.Combine(repoRoot, "games", "client");
            string? newest = FindNewestConsoleLog(baseDir);
            string[] consoleCandidates = newest != null
                ? [newest]
                :
                [
                    Path.Combine(baseDir, "userdataKONGOR", "extensions", "console.log"),
                    Path.Combine(baseDir, "userdataKONGOR", "configuration", "console.log"),
                    Path.Combine(baseDir, "userdataKONGOR", "updates", "console.log"),
                    Path.Combine(baseDir, "userdata", "logs", "console.log"),
                    Path.Combine(baseDir, "console.log")
                ];

            foreach (string path in consoleCandidates)
            {
                if (!File.Exists(path)) continue;
                string text = ReadTailOfLogFile(path, 65536);
                if (string.IsNullOrEmpty(text)) continue;

                MatchCollection matches = Regex.Matches(text, @"(?:\['gamePhase'\]\s*=\s*|GAME_PHASE[:|])(\d+)");
                if (matches.Count > 0)
                {
                    if (int.TryParse(matches[^1].Groups[1].Value, out int phase))
                    {
                        return phase;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientOutputRingBuffer] TryScrapeLatestGamePhaseFromLog failed: {ex.Message}");
        }
        return -1;
    }

    public static string? TryScrapeLatestHeroAssertFromLog(string? customWorkingDirectory = null)
    {
        try
        {
            string repoRoot = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
            string baseDir = customWorkingDirectory ?? Path.Combine(repoRoot, "games", "client");
            string? newest = FindNewestConsoleLog(baseDir);
            string[] consoleCandidates = newest != null
                ? [newest]
                :
                [
                    Path.Combine(baseDir, "userdataKONGOR", "extensions", "console.log"),
                    Path.Combine(baseDir, "userdataKONGOR", "configuration", "console.log"),
                    Path.Combine(baseDir, "userdataKONGOR", "updates", "console.log"),
                    Path.Combine(baseDir, "userdata", "logs", "console.log"),
                    Path.Combine(baseDir, "console.log")
                ];

            foreach (string path in consoleCandidates)
            {
                if (!File.Exists(path)) continue;
                string text = ReadTailOfLogFile(path, 65536);
                if (string.IsNullOrEmpty(text)) continue;

                MatchCollection matches = Regex.Matches(text, @"HERO_ASSERT\|[^\r\n]+");
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    if (matches[i].Value.Contains("HERO_ASSERT|true", StringComparison.OrdinalIgnoreCase))
                    {
                        return matches[i].Value;
                    }
                }

                // Fallback: Check NEXUS_TELEMETRY
                Match teleMatch = Regex.Match(text, @"\[NEXUS_TELEMETRY\][^\r\n]*?\bhero=([A-Za-z0-9_]+)", RegexOptions.RightToLeft);
                if (teleMatch.Success && !teleMatch.Groups[1].Value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    string hName = teleMatch.Groups[1].Value;
                    if (!hName.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) hName = "Hero_" + hName;
                    return $"HERO_ASSERT|true|600|600|{hName}|0|0";
                }

                // Fallback: Check chat pick announcement (e.g. UnnamedNewbie has selected Pyromancer)
                Match pickMatch = Regex.Match(text, @"(?:has selected|selected hero|picked hero)\s+\^*[0-9]*([A-Za-z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
                if (pickMatch.Success)
                {
                    string hName = pickMatch.Groups[1].Value;
                    if (!hName.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) hName = "Hero_" + hName;
                    return $"HERO_ASSERT|true|600|600|{hName}|0|0";
                }

                if (matches.Count > 0)
                {
                    return matches[^1].Value;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientOutputRingBuffer] TryScrapeLatestHeroAssertFromLog failed: {ex.Message}");
        }
        return null;
    }

    private static string FindRepoRoot(string startingDir)
    {
        DirectoryInfo? current = new(startingDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "PROJECT.md")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return startingDir;
    }
}
