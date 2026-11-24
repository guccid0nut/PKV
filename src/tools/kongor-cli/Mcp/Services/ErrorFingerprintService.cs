// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of error fingerprinting and sliding-window rate limit evaluation.
/// </summary>
public sealed record FingerprintResult
{
    public required string Fingerprint { get; init; }
    public required string NormalizedSignature { get; init; }
    public bool IsThrottled { get; init; }
    public int OccurrenceCount { get; init; }
}

/// <summary>
/// Interface for normalizing error text, calculating deterministic SHA-256 fingerprints,
/// and throttling high-frequency engine log bursts.
/// </summary>
public interface IErrorFingerprintService
{
    string Normalize(string text);
    FingerprintResult ProcessError(string title, string? stackTrace = null, TimeSpan? window = null);
    void ClearThrottling();
}

/// <summary>
/// Normalizes dynamic tokens (memory addresses, timestamps, PIDs, entity IDs)
/// from native and managed log errors, computes SHA-256 deduplication signatures,
/// and provides a sliding-window rate limiter against 60 FPS log spam.
/// </summary>
public sealed partial class ErrorFingerprintService : IErrorFingerprintService
{
    private static readonly Regex AddrRegex = new(@"0x[0-9a-fA-F]{4,16}\b", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"\b\d{2}:\d{2}:\d{2}(\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex TimestampRegex = new(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?Z?\b", RegexOptions.Compiled);
    private static readonly Regex PidRegex = new(@"\b(PID|TID|Process|Thread)\s*[:=]?\s*\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EntityIdRegex = new(@"\b(Unit|Hero|Entity)\s*#\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PortRegex = new(@"\b(port|listen|connect)\s*[:=]?\s*\d{4,5}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CaretColorRegex = new(@"\^([a-zA-Z0-9\*]|#[0-9a-fA-F]{6})", RegexOptions.Compiled);

    private sealed class FingerprintState
    {
        public DateTimeOffset LastSeenUtc { get; set; }
        public int Count { get; set; }
    }

    private readonly ConcurrentDictionary<string, FingerprintState> _slidingWindow = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultWindow;

    public ErrorFingerprintService(TimeProvider? timeProvider = null, TimeSpan? defaultWindow = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _defaultWindow = defaultWindow ?? TimeSpan.FromSeconds(5.0);
    }

    /// <summary>
    /// Normalizes dynamic runtime tokens from an error string.
    /// </summary>
    public string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = CaretColorRegex.Replace(text, string.Empty);
        normalized = TimestampRegex.Replace(normalized, "<TIMESTAMP>");
        normalized = TimeRegex.Replace(normalized, "<TIME>");
        normalized = AddrRegex.Replace(normalized, "<ADDR>");
        normalized = PidRegex.Replace(normalized, "$1:<PID>");
        normalized = EntityIdRegex.Replace(normalized, "$1:<ID>");
        normalized = PortRegex.Replace(normalized, "$1:<PORT>");

        return normalized.Trim();
    }

    /// <summary>
    /// Processes an error title and optional stack trace:
    /// 1. Normalizes dynamic tokens.
    /// 2. Hashes the title + top 3–5 stack frames into a SHA-256 fingerprint.
    /// 3. Evaluates the sliding-window rate limiter to throttle high-frequency log spam.
    /// </summary>
    public FingerprintResult ProcessError(string title, string? stackTrace = null, TimeSpan? window = null)
    {
        TimeSpan effectiveWindow = window ?? _defaultWindow;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        string normalizedTitle = Normalize(title);
        string normalizedStack = ExtractNormalizedTopFrames(stackTrace, maxFrames: 4);

        string signatureToHash = string.IsNullOrEmpty(normalizedStack)
            ? normalizedTitle
            : $"{normalizedTitle}\n{normalizedStack}";

        string fingerprint = ComputeSha256(signatureToHash);

        var state = _slidingWindow.AddOrUpdate(
            fingerprint,
            _ => new FingerprintState { LastSeenUtc = now, Count = 1 },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Count++;
                    bool withinWindow = (now - existing.LastSeenUtc) <= effectiveWindow;
                    existing.LastSeenUtc = now;
                    return existing;
                }
            });

        bool isThrottled;
        int count;
        lock (state)
        {
            count = state.Count;
            isThrottled = count > 1 && (now - state.LastSeenUtc) <= effectiveWindow;
        }

        return new FingerprintResult
        {
            Fingerprint = fingerprint,
            NormalizedSignature = signatureToHash,
            IsThrottled = isThrottled,
            OccurrenceCount = count
        };
    }

    public void ClearThrottling()
    {
        _slidingWindow.Clear();
    }

    private string ExtractNormalizedTopFrames(string? stackTrace, int maxFrames = 4)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return string.Empty;

        string[] lines = stackTrace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        int captured = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Frame #", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(".dll+", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(".exe+", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(Normalize(trimmed));
                captured++;
                if (captured >= maxFrames)
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string ComputeSha256(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
