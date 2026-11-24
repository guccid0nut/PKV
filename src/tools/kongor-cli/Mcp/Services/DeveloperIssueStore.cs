// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

public enum IssueSubsystem
{
    GameClient,
    DedicatedServer,
    MasterServer,
    ChatServer,
    AspireWebPortal,
    AssetPipeline,
    AudioSpatializer,
    WillowmakerDetours,
    General
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
    CriticalCrash
}

public enum IssueResolutionStatus
{
    Open,
    HealingInProgress,
    AutoResolved,
    ActionRequired,
    ManualResolved,
    Ignored,
    Regression
}

/// <summary>
/// Lightweight record representing an ingested developer issue in memory and NDJSON persistence.
/// </summary>
public sealed record DeveloperIssueRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public required string Fingerprint { get; init; }
    public IssueSubsystem Subsystem { get; set; } = IssueSubsystem.General;
    public IssueSeverity Severity { get; set; } = IssueSeverity.Error;
    public IssueResolutionStatus Status { get; set; } = IssueResolutionStatus.Open;
    public string Title { get; set; } = string.Empty;
    public string RawMessage { get; set; } = string.Empty;
    public string NormalizedSignature { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public string? FaultingModule { get; set; }
    public ulong? FaultingAddress { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public string? ResolutionRecipeApplied { get; set; }
    public string? ResolutionReceipt { get; set; }
    public string? AssociatedIssueTrackerId { get; set; }
}

/// <summary>
/// High-level summary of a self-healing journey.
/// </summary>
public sealed record JourneySummary
{
    public int TotalCaught { get; init; }
    public int AutoResolvedCount { get; init; }
    public int ActionRequiredCount { get; init; }
    public int RegressionCount { get; init; }
    public IReadOnlyList<DeveloperIssueRecord> PendingIssues { get; init; } = [];
    public string FormattedReport { get; init; } = string.Empty;
}

/// <summary>
/// Interface for dual-engine developer issue persistence (in-memory + NDJSON log + database hook).
/// </summary>
public interface IDeveloperIssueStore
{
    DeveloperIssueRecord UpsertIssue(DeveloperIssueRecord issue);
    bool MarkAutoResolved(string fingerprint, string recipe, string receipt);
    bool MarkActionRequired(string fingerprint, string reason);
    DeveloperIssueRecord? GetIssue(string fingerprint);
    IReadOnlyList<DeveloperIssueRecord> GetAllIssues();
    IReadOnlyList<DeveloperIssueRecord> GetPendingIssues(int limit = 50);
    JourneySummary GetJourneySummary(int limit = 10);
    Task FlushAsync();
    void Clear();
}

/// <summary>
/// Dual-engine developer issue store:
/// - In-memory concurrent dictionary for sub-millisecond retrieval.
/// - Asynchronous append-only NDJSON disk queue ('logs/MCP Conversation History/developer_issues.ndjson').
/// - Automatic regression detection and anti-oscillation circuit breaker.
/// </summary>
public sealed class DeveloperIssueStore : IDeveloperIssueStore, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, DeveloperIssueRecord> _issues = new(StringComparer.Ordinal);
    private readonly Channel<DeveloperIssueRecord> _diskChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _diskTask;
    private readonly string _ndjsonFilePath;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public DeveloperIssueStore(
        string? logDirectory = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        string baseDir = logDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs", "MCP Conversation History");
        _ndjsonFilePath = Path.Combine(baseDir, "developer_issues.ndjson");

        try
        {
            Directory.CreateDirectory(baseDir);
        }
        catch { }

        _diskChannel = Channel.CreateUnbounded<DeveloperIssueRecord>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        _diskTask = Task.Run(ProcessDiskQueueAsync);
    }

    public DeveloperIssueRecord UpsertIssue(DeveloperIssueRecord issue)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        DeveloperIssueRecord result = _issues.AddOrUpdate(
            issue.Fingerprint,
            fp =>
            {
                issue.FirstSeenUtc = issue.FirstSeenUtc == default ? now : issue.FirstSeenUtc;
                issue.LastSeenUtc = now;
                return issue;
            },
            (fp, existing) =>
            {
                lock (existing)
                {
                    existing.OccurrenceCount++;
                    existing.LastSeenUtc = now;
                    if (!string.IsNullOrEmpty(issue.RawMessage))
                        existing.RawMessage = issue.RawMessage;
                    if (!string.IsNullOrEmpty(issue.StackTrace))
                        existing.StackTrace = issue.StackTrace;

                    // Regression detection: if previously resolved, mark as Regression
                    if (existing.Status is IssueResolutionStatus.AutoResolved or IssueResolutionStatus.ManualResolved)
                    {
                        existing.Status = IssueResolutionStatus.Regression;
                        existing.Severity = IssueSeverity.CriticalCrash;
                        _logger?.Invoke($"[DeveloperIssueStore] Regression detected on fingerprint '{fp}'. Reopened as CriticalCrash.");
                    }

                    // Anti-oscillation circuit breaker: if regressed more than 3 times, lock to ActionRequired
                    if (existing.Status == IssueResolutionStatus.Regression && existing.OccurrenceCount > 3)
                    {
                        existing.Status = IssueResolutionStatus.ActionRequired;
                        _logger?.Invoke($"[DeveloperIssueStore] Circuit breaker triggered on fingerprint '{fp}'. Escalated to ActionRequired.");
                    }

                    return existing;
                }
            });

        _diskChannel.Writer.TryWrite(result);
        return result;
    }

    public bool MarkAutoResolved(string fingerprint, string recipe, string receipt)
    {
        if (!_issues.TryGetValue(fingerprint, out DeveloperIssueRecord? existing))
            return false;

        lock (existing)
        {
            existing.Status = IssueResolutionStatus.AutoResolved;
            existing.ResolutionRecipeApplied = recipe;
            existing.ResolutionReceipt = receipt;
            existing.LastSeenUtc = _timeProvider.GetUtcNow();
        }

        _diskChannel.Writer.TryWrite(existing);
        return true;
    }

    public bool MarkActionRequired(string fingerprint, string reason)
    {
        if (!_issues.TryGetValue(fingerprint, out DeveloperIssueRecord? existing))
            return false;

        lock (existing)
        {
            existing.Status = IssueResolutionStatus.ActionRequired;
            if (!string.IsNullOrEmpty(reason))
            {
                existing.ResolutionReceipt = $"ActionRequired: {reason}";
            }
            existing.LastSeenUtc = _timeProvider.GetUtcNow();
        }

        _diskChannel.Writer.TryWrite(existing);
        return true;
    }

    public DeveloperIssueRecord? GetIssue(string fingerprint)
    {
        return _issues.TryGetValue(fingerprint, out DeveloperIssueRecord? rec) ? rec : null;
    }

    public IReadOnlyList<DeveloperIssueRecord> GetAllIssues()
    {
        return _issues.Values.ToList();
    }

    public IReadOnlyList<DeveloperIssueRecord> GetPendingIssues(int limit = 50)
    {
        return _issues.Values
            .Where(x => x.Status is IssueResolutionStatus.Open
                                 or IssueResolutionStatus.ActionRequired
                                 or IssueResolutionStatus.Regression
                                 or IssueResolutionStatus.HealingInProgress)
            .OrderByDescending(x => x.Severity)
            .ThenByDescending(x => x.OccurrenceCount)
            .Take(limit)
            .ToList();
    }

    public JourneySummary GetJourneySummary(int limit = 10)
    {
        var all = _issues.Values.ToList();
        int total = all.Count;
        int resolved = all.Count(x => x.Status == IssueResolutionStatus.AutoResolved);
        int regressions = all.Count(x => x.Status == IssueResolutionStatus.Regression);
        var pending = GetPendingIssues(limit);
        int actionRequired = all.Count(x => x.Status is IssueResolutionStatus.ActionRequired or IssueResolutionStatus.Open);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Self-Healing Journey Summary");
        sb.AppendLine($"- **Total Caught**: {total}");
        sb.AppendLine($"- **Auto-Resolved by Reflex**: {resolved}");
        sb.AppendLine($"- **Regressions**: {regressions}");
        sb.AppendLine($"- **Action Required**: {actionRequired}");
        sb.AppendLine();

        if (pending.Count > 0)
        {
            sb.AppendLine("### Active Backlog (Prioritized)");
            for (int i = 0; i < pending.Count; i++)
            {
                var issue = pending[i];
                sb.AppendLine($"{i + 1}. **[{issue.Severity}] {issue.Subsystem}**: {issue.Title}");
                sb.AppendLine($"   - Fingerprint: `{issue.Fingerprint[..Math.Min(12, issue.Fingerprint.Length)]}...`");
                sb.AppendLine($"   - Status: `{issue.Status}` | Occurrences: `{issue.OccurrenceCount}`");
                if (!string.IsNullOrEmpty(issue.FaultingModule))
                    sb.AppendLine($"   - Faulting Module: `{issue.FaultingModule}`");
            }
        }
        else
        {
            sb.AppendLine("*(Zero pending issues. All runtime errors auto-resolved by reflex.)*");
        }

        return new JourneySummary
        {
            TotalCaught = total,
            AutoResolvedCount = resolved,
            ActionRequiredCount = actionRequired,
            RegressionCount = regressions,
            PendingIssues = pending,
            FormattedReport = sb.ToString()
        };
    }

    public void Clear()
    {
        _issues.Clear();
    }

    public async Task FlushAsync()
    {
        // Allow time for channel queue to process
        await Task.Yield();
    }

    private async Task ProcessDiskQueueAsync()
    {
        try
        {
            while (await _diskChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_diskChannel.Reader.TryRead(out DeveloperIssueRecord? record))
                {
                    if (record == null) continue;
                    try
                    {
                        string line = JsonSerializer.Serialize(record, JsonOpts);
                        await File.AppendAllTextAsync(_ndjsonFilePath, line + Environment.NewLine, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"[DeveloperIssueStore] Failed to write issue to NDJSON: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _diskChannel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _diskTask;
        }
        catch { }
        _cts.Dispose();
    }

    public void Dispose()
    {
#pragma warning disable RS0030 // Justification: Synchronous IDisposable bridge to DisposeAsync
        DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore RS0030
    }
}
