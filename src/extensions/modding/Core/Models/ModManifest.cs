// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace Kongor.Modding.Core.Models;

/// <summary>
///     Immutable manifest representation of a HoN mod.xml file.
/// </summary>
public sealed record ModManifest
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? Date { get; init; }
    public string? AppVersion { get; init; }
    public string? MmVersion { get; init; }
    public string? WebLink { get; init; }
    public string? UpdateCheckUrl { get; init; }
    public string? UpdateDownloadUrl { get; init; }
    public string? Icon { get; init; }
    public string? Category { get; init; }

    public IReadOnlyList<ModRequirement> Requirements { get; init; } = [];
    public IReadOnlyList<ModIncompatibility> Incompatibilities { get; init; } = [];
    public IReadOnlyList<ModOrderingRule> ApplyBefore { get; init; } = [];
    public IReadOnlyList<ModOrderingRule> ApplyAfter { get; init; } = [];
    public IReadOnlyList<CopyFileOperation> FileCopies { get; init; } = [];
    public IReadOnlyList<EditFileOperation> FileEdits { get; init; } = [];
    public IReadOnlyList<EditStringtableOperation> StringtableEdits { get; init; } = [];

    public IReadOnlyDictionary<string, string> CustomProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     A required dependency specification.
/// </summary>
public sealed record ModRequirement(string Name, string? VersionExpression = null);

/// <summary>
///     An incompatibility constraint specification.
/// </summary>
public sealed record ModIncompatibility(string Name, string? VersionExpression = null);

/// <summary>
///     Ordering constraint (apply before or apply after).
/// </summary>
public sealed record ModOrderingRule(string Name, string? VersionExpression = null);

/// <summary>
///     File overwrite modes for whole file copies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileOverwriteMode
{
    Yes,
    No,
    Newer
}

/// <summary>
///     Operation copying a whole file from the mod package to the game filesystem.
/// </summary>
public sealed record CopyFileOperation
{
    public required string TargetName { get; init; }
    public required string SourcePath { get; init; }
    public string? Version { get; init; }
    public FileOverwriteMode Overwrite { get; init; } = FileOverwriteMode.Yes;
    public string? Condition { get; init; }
}

/// <summary>
///     Operation applying sequential delta edits to an existing game file.
/// </summary>
public sealed record EditFileOperation
{
    public required string TargetName { get; init; }
    public string? Condition { get; init; }
    public IReadOnlyList<PatchOperation> Operations { get; init; } = [];
}

/// <summary>
///     Localized stringtable edit operation (.str file).
/// </summary>
public sealed record EditStringtableOperation
{
    public required string TargetName { get; init; }
    public string? Condition { get; init; }
    public IReadOnlyList<StringtableEntryOperation> Entries { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StringtableAction
{
    Set,
    Delete
}

public sealed record StringtableEntryOperation(
    string Key,
    string Value,
    StringtableAction Action = StringtableAction.Set);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindPosition
{
    None,
    Before,
    Begin,
    Start,
    Head,
    After,
    End,
    Tail,
    Eof,
    Offset
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InsertPosition
{
    After,
    Before
}

/// <summary>
///     Abstract base class for delta patch operations.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FindOperation), "find")]
[JsonDerivedType(typeof(FindUpOperation), "findup")]
[JsonDerivedType(typeof(FindAllOperation), "findall")]
[JsonDerivedType(typeof(InsertOperation), "insert")]
[JsonDerivedType(typeof(ReplaceOperation), "replace")]
[JsonDerivedType(typeof(DeleteOperation), "delete")]
public abstract record PatchOperation;

public sealed record FindOperation(
    string Target,
    FindPosition Position = FindPosition.None,
    int Offset = 0,
    bool CaseSensitive = true) : PatchOperation;

public sealed record FindUpOperation(
    string Target,
    FindPosition Position = FindPosition.None,
    int Offset = 0,
    bool CaseSensitive = true) : PatchOperation;

public sealed record FindAllOperation(
    string Target,
    bool CaseSensitive = true) : PatchOperation;

public sealed record InsertOperation(
    string Content,
    string? SourceFile = null,
    InsertPosition Position = InsertPosition.After) : PatchOperation;

public sealed record ReplaceOperation(
    string Content,
    string? SourceFile = null) : PatchOperation;

public sealed record DeleteOperation : PatchOperation;
