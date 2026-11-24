// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Parsed error event from a log file or stream.
/// </summary>
public sealed record RawErrorEvent
{
    public required string Source { get; init; }
    public required string HeaderLine { get; init; }
    public required string FullPayload { get; init; }
    public string? StackTrace { get; init; }
    public IssueSubsystem Subsystem { get; init; } = IssueSubsystem.General;
    public IssueSeverity Severity { get; init; } = IssueSeverity.Error;
    public string? FaultingModule { get; init; }
    public ulong? FaultingAddress { get; init; }
}

/// <summary>
/// Interface for log stream tailing and batch scanning with multi-line stack trace accumulation.
/// </summary>
public interface ILogTailWatcherService
{
    IReadOnlyList<RawErrorEvent> ScanText(string text, string source = "direct");
    IReadOnlyList<RawErrorEvent> ScanFile(string filePath);
    Task<int> TailFileOnceAsync(string filePath, Func<RawErrorEvent, Task> onEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Non-blocking log watcher and parser supporting:
/// - FileShare.ReadWrite stream tailing (zero locking against running game engine).
/// - Log truncation and process restart detection (reset offset to 0 when length shrinks).
/// - Multi-line stack trace accumulation (groups header with child frame lines).
/// - Automatic subsystem and severity inference.
/// </summary>
public sealed class LogTailWatcherService : ILogTailWatcherService
{
    private static readonly Regex ErrorIndicatorRegex = new(
        @"\b(ERROR|EXCEPTION|ASSERT|FATAL|CRASH|ACCESS VIOLATION|DIVIDE BY ZERO|Failed to load|Bitmap_Load: File not found|LUA_ERROR|DEVELOPER_ISSUE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ModuleOffsetRegex = new(
        @"\b([a-zA-Z0-9_\-\.]+\.(?:dll|exe))\+0x([0-9a-fA-F]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CaretColorRegex = new(
        @"\^([a-zA-Z0-9\*]|#[0-9a-fA-F]{6})",
        RegexOptions.Compiled);

    private static readonly Regex DeveloperIssueTagRegex = new(
        @"\[DEVELOPER_ISSUE\]\[(?<subsystem>[^\]]+)\]\[(?<severity>[^\]]+)\]\s*(?<title>[^|]+)\|\s*(?<detail>.*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, long> _fileOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _logger;

    public LogTailWatcherService(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans a text buffer and accumulates multi-line error blocks.
    /// </summary>
    public IReadOnlyList<RawErrorEvent> ScanText(string text, string source = "direct")
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var results = new List<RawErrorEvent>();

        string? currentHeader = null;
        var currentStack = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string cleanLine = CaretColorRegex.Replace(line, string.Empty);
            bool isStackLine = line.StartsWith("   ") || line.StartsWith('\t') || line.StartsWith("  at ") || cleanLine.StartsWith("[LUA_STACK]");
            bool isErrorHeader = ErrorIndicatorRegex.IsMatch(cleanLine);

            if (isErrorHeader)
            {
                // If we were accumulating a previous error, finalize it
                if (currentHeader != null)
                {
                    results.Add(CreateErrorEvent(source, currentHeader, currentStack.ToString()));
                    currentStack.Clear();
                }
                currentHeader = cleanLine;
            }
            else if (currentHeader != null && isStackLine)
            {
                currentStack.AppendLine(cleanLine);
            }
            else if (currentHeader != null && !isStackLine)
            {
                // End of multi-line error block
                results.Add(CreateErrorEvent(source, currentHeader, currentStack.ToString()));
                currentHeader = null;
                currentStack.Clear();
            }
        }

        if (currentHeader != null)
        {
            results.Add(CreateErrorEvent(source, currentHeader, currentStack.ToString()));
        }

        return results;
    }

    /// <summary>
    /// Scans an entire file on disk using non-blocking FileShare.ReadWrite.
    /// </summary>
    public IReadOnlyList<RawErrorEvent> ScanFile(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string content = reader.ReadToEnd();
            return ScanText(content, filePath);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[LogTailWatcherService] Failed to read '{filePath}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Tails a file incrementally from the last known offset.
    /// Detects file truncation or process restart (stream.Length < lastOffset).
    /// </summary>
    public async Task<int> TailFileOnceAsync(string filePath, Func<RawErrorEvent, Task> onEvent, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return 0;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long lastOffset = _fileOffsets.GetValueOrDefault(filePath, 0);

            // Log Truncation / Process Restart Guard
            if (stream.Length < lastOffset)
            {
                _logger?.Invoke($"[LogTailWatcherService] File '{filePath}' truncated or process restarted. Resetting offset to 0.");
                lastOffset = 0;
            }

            if (stream.Length == lastOffset)
                return 0; // No new data

            stream.Seek(lastOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string newContent = await reader.ReadToEndAsync(cancellationToken);
            _fileOffsets[filePath] = stream.Position;

            var events = ScanText(newContent, filePath);
            foreach (var evt in events)
            {
                await onEvent(evt);
            }

            return events.Count;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[LogTailWatcherService] Tail error on '{filePath}': {ex.Message}");
            return 0;
        }
    }

    private static RawErrorEvent CreateErrorEvent(string source, string header, string stack)
    {
        string cleanHeader = CaretColorRegex.Replace(header, string.Empty).Trim();
        string cleanStack = CaretColorRegex.Replace(stack, string.Empty).TrimEnd();
        string fullPayload = string.IsNullOrEmpty(cleanStack) ? cleanHeader : $"{cleanHeader}\n{cleanStack}";

        var match = DeveloperIssueTagRegex.Match(cleanHeader);
        if (match.Success)
        {
            string subStr = match.Groups["subsystem"].Value;
            string sevStr = match.Groups["severity"].Value;
            string title = match.Groups["title"].Value.Trim();
            string detail = match.Groups["detail"].Value.Trim();

            IssueSubsystem parsedSub = ParseSubsystem(subStr);
            IssueSeverity parsedSev = ParseSeverity(sevStr);

            return new RawErrorEvent
            {
                Source = source,
                HeaderLine = title,
                FullPayload = string.IsNullOrEmpty(cleanStack) ? $"{cleanHeader} | {detail}" : $"{cleanHeader} | {detail}\n{cleanStack}",
                StackTrace = string.IsNullOrWhiteSpace(cleanStack) ? null : cleanStack,
                Subsystem = parsedSub,
                Severity = parsedSev,
                FaultingModule = null,
                FaultingAddress = null
            };
        }

        var (subsystem, severity) = InferSubsystemAndSeverity(source, cleanHeader, fullPayload);
        var (faultModule, faultAddr) = ExtractFaultingModuleAndAddress(fullPayload);

        return new RawErrorEvent
        {
            Source = source,
            HeaderLine = cleanHeader,
            FullPayload = fullPayload,
            StackTrace = string.IsNullOrWhiteSpace(cleanStack) ? null : cleanStack,
            Subsystem = subsystem,
            Severity = severity,
            FaultingModule = faultModule,
            FaultingAddress = faultAddr
        };
    }

    private static IssueSubsystem ParseSubsystem(string text)
    {
        if (Enum.TryParse<IssueSubsystem>(text, true, out var exact))
            return exact;

        string lower = text.ToLowerInvariant();
        if (lower.Contains("client") || lower.Contains("lua") || lower.Contains("ui"))
            return IssueSubsystem.GameClient;
        if (lower.Contains("server") || lower.Contains("dedicated"))
            return IssueSubsystem.DedicatedServer;
        if (lower.Contains("master"))
            return IssueSubsystem.MasterServer;
        if (lower.Contains("chat"))
            return IssueSubsystem.ChatServer;
        if (lower.Contains("asset") || lower.Contains("texture") || lower.Contains("material"))
            return IssueSubsystem.AssetPipeline;
        if (lower.Contains("audio") || lower.Contains("sound") || lower.Contains("voice"))
            return IssueSubsystem.AudioSpatializer;
        if (lower.Contains("detour") || lower.Contains("willow") || lower.Contains("native"))
            return IssueSubsystem.WillowmakerDetours;
        if (lower.Contains("web") || lower.Contains("portal") || lower.Contains("aspire"))
            return IssueSubsystem.AspireWebPortal;

        return IssueSubsystem.General;
    }

    private static IssueSeverity ParseSeverity(string text)
    {
        if (Enum.TryParse<IssueSeverity>(text, true, out var exact))
            return exact;

        string lower = text.ToLowerInvariant();
        if (lower.Contains("crash") || lower.Contains("fatal") || lower.Contains("critical"))
            return IssueSeverity.CriticalCrash;
        if (lower.Contains("warn"))
            return IssueSeverity.Warning;
        if (lower.Contains("info"))
            return IssueSeverity.Info;

        return IssueSeverity.Error;
    }

    private static (IssueSubsystem Subsystem, IssueSeverity Severity) InferSubsystemAndSeverity(string source, string header, string full)
    {
        string lowerFull = full.ToLowerInvariant();
        string lowerSource = source.ToLowerInvariant();

        IssueSeverity severity = IssueSeverity.Error;
        if (lowerFull.Contains("crash") || lowerFull.Contains("access violation") || lowerFull.Contains("divide by zero") || lowerFull.Contains("fatal"))
        {
            severity = IssueSeverity.CriticalCrash;
        }
        else if (lowerFull.Contains("warning") || lowerFull.Contains("not found"))
        {
            severity = IssueSeverity.Warning;
        }

        IssueSubsystem subsystem = IssueSubsystem.General;
        if (lowerSource.Contains("server") || lowerSource.Contains("1123") || lowerFull.Contains("dedicatedserver") || lowerFull.Contains("masterserver"))
        {
            subsystem = lowerFull.Contains("masterserver") ? IssueSubsystem.MasterServer : IssueSubsystem.DedicatedServer;
        }
        else if (lowerSource.Contains("console.log") || lowerSource.Contains("client") || lowerFull.Contains("k2_x64") || lowerFull.Contains("cgame_x64") || lowerFull.Contains("lua_error"))
        {
            subsystem = IssueSubsystem.GameClient;
        }
        else if (lowerFull.Contains("chatserver") || lowerFull.Contains("11031"))
        {
            subsystem = IssueSubsystem.ChatServer;
        }
        else if (lowerFull.Contains("texture") || lowerFull.Contains(".dds") || lowerFull.Contains(".material") || lowerFull.Contains("bitmap_load"))
        {
            subsystem = IssueSubsystem.AssetPipeline;
        }
        else if (lowerFull.Contains("audio") || lowerFull.Contains("sound") || lowerFull.Contains("lufs") || lowerFull.Contains(".wav"))
        {
            subsystem = IssueSubsystem.AudioSpatializer;
        }
        else if (lowerFull.Contains("willowmaker") || lowerFull.Contains("detour") || lowerFull.Contains("hook"))
        {
            subsystem = IssueSubsystem.WillowmakerDetours;
        }
        else if (lowerFull.Contains("aspire") || lowerFull.Contains("webportal") || lowerFull.Contains("5266"))
        {
            subsystem = IssueSubsystem.AspireWebPortal;
        }

        return (subsystem, severity);
    }

    private static (string? Module, ulong? Address) ExtractFaultingModuleAndAddress(string payload)
    {
        var match = ModuleOffsetRegex.Match(payload);
        if (match.Success)
        {
            string module = match.Groups[1].Value;
            if (ulong.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong offset))
            {
                return (module, offset);
            }
            return (module, null);
        }

        return (null, null);
    }
}
