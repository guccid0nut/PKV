// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Core;

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
/// Authoritative persistence entity for developer issues, runtime errors, and automated self-healing telemetry.
/// Maps to 'core.DeveloperIssues' in MerrickContext.
/// </summary>
public sealed class DeveloperIssue
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Deduplication key: SHA-256 hash of normalized callsite / error signature.
    /// </summary>
    [MaxLength(64)]
    public string Fingerprint { get; set; } = string.Empty;

    public IssueSubsystem Subsystem { get; set; } = IssueSubsystem.General;

    public IssueSeverity Severity { get; set; } = IssueSeverity.Error;

    public IssueResolutionStatus Status { get; set; } = IssueResolutionStatus.Open;

    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    public string RawMessage { get; set; } = string.Empty;

    public string NormalizedSignature { get; set; } = string.Empty;

    public string? StackTrace { get; set; }

    [MaxLength(128)]
    public string? FaultingModule { get; set; }

    public ulong? FaultingAddress { get; set; }

    public int OccurrenceCount { get; set; } = 1;

    public DateTimeOffset FirstSeenUtc { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset LastSeenUtc { get; set; } = TimeProvider.System.GetUtcNow();

    [MaxLength(128)]
    public string? ResolutionRecipeApplied { get; set; }

    public string? ResolutionReceipt { get; set; }

    [MaxLength(64)]
    public string? AssociatedIssueTrackerId { get; set; }
}
