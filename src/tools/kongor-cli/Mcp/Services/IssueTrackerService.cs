// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Status of an issue in docs/ISSUES.md.
/// </summary>
public enum IssueStatus
{
    Open,
    Investigating,
    Patched,
    Resolved
}

/// <summary>
/// Structured model for an issue entry in the living issue backlog.
/// </summary>
public sealed class IssueEntry
{
    public string? IssueId { get; set; }
    public required string Title { get; init; }
    public required string Subsystem { get; init; }
    public required string DiscoveryMethod { get; init; }
    public required string Symptom { get; init; }
    public required string RootCause { get; init; }
    public IssueStatus Status { get; init; } = IssueStatus.Resolved;
    public required string Remediation { get; init; }
}

/// <summary>
/// Interface for reading and recording issues into docs/ISSUES.md.
/// </summary>
public interface IIssueTrackerService
{
    Task<int> GetNextIssueIdAsync(CancellationToken cancellationToken = default);
    Task<string> RecordIssueAsync(IssueEntry entry, CancellationToken cancellationToken = default);
    Task<bool> IssueExistsAsync(string subsystem, string signature, CancellationToken cancellationToken = default);
    Task<string> RecordIssueIfNotExistsAsync(IssueEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Autonomous Issue Tracker Service (Phase 2):
/// Grounded in the Living Issue Backlog requirement:
/// Automatically tracks, assigns sequential IDs ([ISSUE-140]),
/// updates summary status counters, and atomically appends entries into docs/ISSUES.md.
/// </summary>
public sealed class IssueTrackerService : IIssueTrackerService
{
    private static readonly Regex IssueIdRegex = new(@"\[ISSUE-(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex TotalTrackedRegex = new(@"- \*\*Total Tracked\*\*:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex ResolvedRegex = new(@"- \*\*Patched / Resolved\*\*:\s*(\d+)", RegexOptions.Compiled);

    private readonly string _issuesFilePath;
    private readonly Action<string>? _logger;

    public IssueTrackerService(string? issuesFilePath = null, Action<string>? logger = null)
    {
        _issuesFilePath = issuesFilePath ?? Path.Combine(Directory.GetCurrentDirectory(), "docs", "ISSUES.md");
        _logger = logger ?? (msg => Console.Error.WriteLine($"[IssueTracker] {msg}"));
    }

    public async Task<int> GetNextIssueIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_issuesFilePath)) return 140;

        int highest = 139;
        string text = await File.ReadAllTextAsync(_issuesFilePath, cancellationToken).ConfigureAwait(false);
        Match totalTrackedMatch = TotalTrackedRegex.Match(text);
        if (totalTrackedMatch.Success && int.TryParse(totalTrackedMatch.Groups[1].Value, out int totalTracked) && totalTracked > highest)
        {
            highest = totalTracked;
        }

        foreach (Match match in IssueIdRegex.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, out int id) && id > highest)
            {
                highest = id;
            }
        }

        return highest + 1;
    }

    public async Task<string> RecordIssueAsync(IssueEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int nextId = await GetNextIssueIdAsync(cancellationToken).ConfigureAwait(false);
        string formattedId = $"[ISSUE-{nextId}]";
        entry.IssueId = formattedId;

        string statusTag = entry.Status switch
        {
            IssueStatus.Open => "`[OPEN]`",
            IssueStatus.Investigating => "`[INVESTIGATING]`",
            IssueStatus.Patched => "`[PATCHED]`",
            IssueStatus.Resolved => "`[RESOLVED]`",
            _ => "`[RESOLVED]`"
        };

        string formattedEntry = $@"### {formattedId} {entry.Title}
* **Subsystem**: `{entry.Subsystem}`
* **Discovery Method**: {entry.DiscoveryMethod}
* **Symptom / Log Snippet**:
  {entry.Symptom.Trim()}
* **Root Cause Diagnosis**:
  {entry.RootCause.Trim()}
* **Status**: {statusTag}
* **Remediation & Verification**:
  {entry.Remediation.Trim()}
";

        if (!File.Exists(_issuesFilePath))
        {
            await File.WriteAllTextAsync(_issuesFilePath, formattedEntry, cancellationToken).ConfigureAwait(false);
            return formattedId;
        }

        string originalText = await File.ReadAllTextAsync(_issuesFilePath, cancellationToken).ConfigureAwait(false);

        // Update Total Tracked counter
        string updatedText = TotalTrackedRegex.Replace(originalText, m =>
        {
            int current = int.Parse(m.Groups[1].Value);
            return $"- **Total Tracked**: {Math.Max(current + 1, nextId)}";
        }, 1);

        if (entry.Status == IssueStatus.Resolved || entry.Status == IssueStatus.Patched)
        {
            updatedText = ResolvedRegex.Replace(updatedText, m =>
            {
                int current = int.Parse(m.Groups[1].Value);
                return $"- **Patched / Resolved**: {Math.Max(current + 1, nextId)}";
            }, 1);
        }

        // Insert at the top of Resolved Archive or Active Issues
        string targetAnchor = (entry.Status == IssueStatus.Resolved || entry.Status == IssueStatus.Patched)
            ? "## Resolved Archive"
            : "## Active Issues";

        int anchorIndex = updatedText.IndexOf(targetAnchor, StringComparison.OrdinalIgnoreCase);
        if (anchorIndex >= 0)
        {
            int insertionPoint = anchorIndex + targetAnchor.Length;
            // Skip newline
            if (insertionPoint < updatedText.Length && updatedText[insertionPoint] == '\r') insertionPoint++;
            if (insertionPoint < updatedText.Length && updatedText[insertionPoint] == '\n') insertionPoint++;

            updatedText = updatedText.Insert(insertionPoint, "\r\n" + formattedEntry);
        }
        else
        {
            updatedText += "\r\n" + formattedEntry;
        }

        await File.WriteAllTextAsync(_issuesFilePath, updatedText, cancellationToken).ConfigureAwait(false);
        _logger?.Invoke($"Recorded {formattedId} into '{_issuesFilePath}'.");

        return formattedId;
    }

    public async Task<bool> IssueExistsAsync(string subsystem, string signature, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_issuesFilePath)) return false;

        string text = await File.ReadAllTextAsync(_issuesFilePath, cancellationToken).ConfigureAwait(false);

        // Check if an issue with the signature is already in the file
        if (text.Contains(signature, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public async Task<string> RecordIssueIfNotExistsAsync(IssueEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await IssueExistsAsync(entry.Subsystem, entry.Title, cancellationToken).ConfigureAwait(false))
        {
            _logger?.Invoke($"Issue '{entry.Title}' for subsystem '{entry.Subsystem}' already exists in backlog. Suppressing duplicate.");
            return "[EXISTING]";
        }

        return await RecordIssueAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}
