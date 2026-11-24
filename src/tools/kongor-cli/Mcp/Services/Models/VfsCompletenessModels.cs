// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;

namespace Kongor.Cli.Mcp.Services.Models;

/// <summary>
/// Represents an asset reference extracted from source code, XML, MDF, or material definitions.
/// </summary>
public sealed record VfsAssetReference
{
    public required string VirtualPath { get; init; }
    public required string DeclaringFile { get; init; }
    public int LineNumber { get; init; }
    public required string AssetType { get; init; } // "Texture", "Frame", "AnimatedImage", "Model", "Clip", "Sampler", "Cubemap", "PreviewMdf"
    public string? Context { get; init; } // Widget name, Sampler name ("diffuse", "normalmap"), Anim name ("idle")
    public bool IsFatalIfMissing { get; init; } = true;
}

/// <summary>
/// Represents a confirmed missing asset along with probed VFS candidate paths and remediation guidance.
/// </summary>
public sealed record VfsMissingAssetIssue
{
    public required string Severity { get; init; } // "FATAL", "WARNING", "INFO"
    public required string Category { get; init; } // "MISSING_UI_TEXTURE", "MISSING_FRAME_SLICE", "MISSING_MODEL", "MISSING_CLIP", "MISSING_MATERIAL_SAMPLER", "MISSING_CUBEMAP"
    public required string VirtualPath { get; init; }
    public required string DeclaringFile { get; init; }
    public int LineNumber { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string> ProbedPaths { get; init; } = [];
    public string? RemediationHint { get; init; }
}

/// <summary>
/// Options for configuring a static VFS completeness audit.
/// </summary>
public sealed record VfsCompletenessOptions
{
    public string Scope { get; init; } = "all"; // "all", "ui", "models", "materials"
    public string? TargetPath { get; init; }
    public bool Strict { get; init; }
    public int Limit { get; init; } = 250;
    public string? ClientDirectory { get; init; }
}

/// <summary>
/// Comprehensive diagnostic report returned by the VFS Completeness Checker.
/// </summary>
public sealed record VfsCompletenessReport
{
    public bool Success { get; init; }
    public string Status { get; init; } = "clean"; // "clean", "warnings", "fatal_errors"
    public int TotalFilesScanned { get; init; }
    public int TotalReferencesChecked { get; init; }
    public int MissingFatalCount { get; init; }
    public int MissingWarningCount { get; init; }
    public int TotalIndexedVfsEntries { get; init; }
    public double IndexDurationMs { get; init; }
    public double ScanDurationMs { get; init; }
    public double TotalDurationMs { get; init; }
    public bool ScanTruncated { get; init; }
    public IReadOnlyList<string> CompleteScannedRoots { get; init; } = [];
    public IReadOnlyList<VfsMissingAssetIssue> Issues { get; init; } = [];
    public string Timestamp { get; init; } = string.Empty;
}

/// <summary>
/// Phase 14 (#175: rea-observe-effect-vfs-completeness-invariant):
/// Epistemic classification of a filesystem effect between pre- and post-snapshots.
/// Created and Deleted are only asserted when absence in the opposite snapshot is proven
/// by an un-truncated scan covering the target root.
/// </summary>
public enum VfsFilesystemEffectKind
{
    Unknown = 0,
    Unchanged = 1,
    Created = 2,
    Modified = 3,
    Deleted = 4
}

public sealed record VfsSnapshotEntry(
    string RelativePath,
    long SizeBytes,
    string? Sha256Hex = null);

public sealed record VfsFilesystemEffectClassification(
    string RelativePath,
    string RootPrefix,
    VfsFilesystemEffectKind Effect,
    bool PreRootComplete,
    bool PostRootComplete,
    bool ScanTruncated,
    string? UnobservedAbsenceReason = null);

public sealed record VfsObserveEffectDiffReport(
    bool IsAuthoritative,
    bool ScanTruncated,
    IReadOnlyList<string> PreCompleteRoots,
    IReadOnlyList<string> PostCompleteRoots,
    IReadOnlyList<VfsFilesystemEffectClassification> Effects,
    int CreatedCount,
    int DeletedCount,
    int ModifiedCount,
    int UnknownCount);
