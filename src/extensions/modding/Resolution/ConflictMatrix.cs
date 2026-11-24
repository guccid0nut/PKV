// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Resolution;

public enum CollisionSeverity
{
    Info,
    Warning,
    Conflict
}

public sealed record FileCollision(
    string TargetFile,
    IReadOnlyList<string> CopyingMods,
    IReadOnlyList<string> EditingMods,
    CollisionSeverity Severity,
    string Description);

public sealed record ConflictReport
{
    public required IReadOnlyList<FileCollision> Collisions { get; init; }
    public bool HasConflicts => Collisions.Any(c => c.Severity == CollisionSeverity.Conflict);
    public bool HasWarnings => Collisions.Any(c => c.Severity == CollisionSeverity.Warning);

    public string GenerateSummary()
    {
        if (Collisions.Count == 0) return "No file collisions detected.";

        List<string> lines = [];
        foreach (FileCollision c in Collisions) lines.Add($"[{c.Severity}] {c.TargetFile}: {c.Description}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
///     File collision and overlap detection matrix across active mods.
/// </summary>
public static class ConflictMatrix
{
    public static ConflictReport Analyze(IEnumerable<ModPackage> mods)
    {
        Dictionary<string, List<string>> targetCopies = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> targetEdits = new(StringComparer.OrdinalIgnoreCase);

        foreach (ModPackage mod in mods)
        {
            string name = mod.Manifest.Name;

            foreach (CopyFileOperation copy in mod.Manifest.FileCopies)
            {
                string norm = ModPackage.NormalizeZipPath(copy.TargetName);
                if (!targetCopies.TryGetValue(norm, out List<string>? list))
                {
                    list = [];
                    targetCopies[norm] = list;
                }

                list.Add(name);
            }

            foreach (EditFileOperation edit in mod.Manifest.FileEdits)
            {
                string norm = ModPackage.NormalizeZipPath(edit.TargetName);
                if (!targetEdits.TryGetValue(norm, out List<string>? list))
                {
                    list = [];
                    targetEdits[norm] = list;
                }

                list.Add(name);
            }

            foreach (EditStringtableOperation strEdit in mod.Manifest.StringtableEdits)
            {
                string norm = ModPackage.NormalizeZipPath(strEdit.TargetName);
                if (!targetEdits.TryGetValue(norm, out List<string>? list))
                {
                    list = [];
                    targetEdits[norm] = list;
                }

                list.Add(name);
            }
        }

        HashSet<string> allFiles = new(targetCopies.Keys, StringComparer.OrdinalIgnoreCase);
        allFiles.UnionWith(targetEdits.Keys);

        List<FileCollision> collisions = [];

        foreach (string file in allFiles)
        {
            IReadOnlyList<string> copies = targetCopies.TryGetValue(file, out List<string>? cp)
                ? cp
                : Array.Empty<string>();
            IReadOnlyList<string> edits = targetEdits.TryGetValue(file, out List<string>? ed)
                ? ed
                : Array.Empty<string>();

            if (copies.Count > 1)
                collisions.Add(new FileCollision(
                    file,
                    copies,
                    edits,
                    CollisionSeverity.Conflict,
                    $"Multiple mods overwrite whole file: {string.Join(", ", copies)}"
                ));
            else if (copies.Count == 1 && edits.Count > 0)
                collisions.Add(new FileCollision(
                    file,
                    copies,
                    edits,
                    CollisionSeverity.Warning,
                    $"Mod '{copies[0]}' copies whole file while other mods edit it ({string.Join(", ", edits)})"
                ));
            else if (edits.Count > 1)
                collisions.Add(new FileCollision(
                    file,
                    copies,
                    edits,
                    CollisionSeverity.Info,
                    $"Multiple mods patch delta edits to file: {string.Join(", ", edits)}"
                ));
        }

        return new ConflictReport
        {
            Collisions = collisions
        };
    }
}
