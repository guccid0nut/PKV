// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Repair;

/// <summary>
///     Relocates legacy and deprecated UI/script target file paths to modern Kongor v4.14+ client targets.
///     For example:
///     - ui/game.interface -> ui/game_legacy.interface
///     - ui/fe2/game_lobby.package -> ui/fe2/newui/lobby_v2.package
///     - ui/scripts/player_stats.lua -> ui/scripts/newui/player_stats_v2.lua
/// </summary>
public static class TargetFileRelocator
{
    private static readonly Dictionary<string, string> PathMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui/game.interface"] = "ui/game_legacy.interface",
        ["ui/game_chat.package"] = "ui/game_legacy.interface",
        ["ui/fe2/game_lobby.package"] = "ui/fe2/newui/lobby_v2.package",
        ["ui/fe2/main.interface"] = "ui/fe2/newui/lobby_v2.package",
        ["ui/fe2/game_setup.package"] = "ui/fe2/newui/game_setup_v2.package",
        ["ui/scripts/player_stats.lua"] = "ui/scripts/newui/player_stats_v2.lua",
        ["ui/scripts/alt_info.lua"] = "ui/scripts/alt_info_building.lua",
        ["ui/scripts/hero_info.lua"] = "ui/scripts/newui/hero_info.lua",
        ["ui/scripts/game_chat.lua"] = "ui/scripts/newui/game_chat.lua",
        ["ui/hero_select.package"] = "ui/fe2/newui/lobby_v2.package"
    };

    /// <summary>
    ///     Normalizes path delimiters and removes leading slashes.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string p = path.Trim().Replace('\\', '/');
        while (p.StartsWith('/')) p = p[1..];
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    /// <summary>
    ///     Checks if the target path is a known deprecated file.
    /// </summary>
    public static bool IsDeprecatedTarget(string path)
    {
        string norm = NormalizePath(path);
        return PathMappings.ContainsKey(norm);
    }

    /// <summary>
    ///     Relocates a target file path to its modern v4.14+ location.
    /// </summary>
    public static string RelocatePath(string rawPath, out bool wasRelocated)
    {
        string norm = NormalizePath(rawPath);
        if (PathMappings.TryGetValue(norm, out string? modernPath))
        {
            wasRelocated = true;
            return modernPath;
        }

        wasRelocated = !norm.Equals(rawPath, StringComparison.Ordinal);
        return norm;
    }

    /// <summary>
    ///     Rewrites all FileEdits, FileCopies, and StringtableEdits in a ModManifest to target modern files.
    /// </summary>
    public static ModManifest RelocateManifest(ModManifest manifest, out int relocationsCount,
        bool duplicateForApex = false)
    {
        relocationsCount = 0;
        int localCount = 0;

        List<EditFileOperation> updatedEdits = [];
        foreach (EditFileOperation edit in manifest.FileEdits)
        {
            string newTarget = RelocatePath(edit.TargetName, out bool relocated);
            if (relocated) localCount++;

            EditFileOperation newEdit = edit with { TargetName = newTarget };
            updatedEdits.Add(newEdit);

            // If duplicating for apex HUD when game_legacy is targeted
            if (duplicateForApex && newTarget.Equals("ui/game_legacy.interface", StringComparison.OrdinalIgnoreCase))
            {
                updatedEdits.Add(edit with { TargetName = "ui/game_apex.interface" });
                localCount++;
            }
        }

        List<CopyFileOperation> updatedCopies = [];
        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            string newTarget = RelocatePath(copy.TargetName, out bool relocated);
            if (relocated) localCount++;
            updatedCopies.Add(copy with { TargetName = newTarget });
        }

        List<EditStringtableOperation> updatedStringtables = [];
        foreach (EditStringtableOperation strEdit in manifest.StringtableEdits)
        {
            string newTarget = RelocatePath(strEdit.TargetName, out bool relocated);
            if (relocated) localCount++;
            updatedStringtables.Add(strEdit with { TargetName = newTarget });
        }

        relocationsCount = localCount;

        return manifest with
        {
            FileEdits = updatedEdits,
            FileCopies = updatedCopies,
            StringtableEdits = updatedStringtables
        };
    }

    /// <summary>
    ///     Relocates target path occurrences directly within raw XML text (e.g. inside &lt;editfile name="..."&gt;).
    /// </summary>
    public static string RelocateXmlText(string xmlContent, out int relocationsCount)
    {
        relocationsCount = 0;
        if (string.IsNullOrWhiteSpace(xmlContent)) return xmlContent;

        string content = xmlContent;
        int localCount = 0;

        foreach (KeyValuePair<string, string> kvp in PathMappings)
        {
            string legacy = kvp.Key;
            string modern = kvp.Value;

            // Pattern for name="legacy" or file="legacy" or target="legacy" (with optional leading /)
            string pattern = $@"(name|file|target)\s*=\s*([""'])\/?{Regex.Escape(legacy)}\2";
            MatchCollection matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                localCount += matches.Count;
                content = Regex.Replace(content, pattern, $"$1=$2{modern}$2", RegexOptions.IgnoreCase);
            }
        }

        relocationsCount = localCount;
        return content;
    }
}
