// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Status and metrics for K2 Virtual File System (VFS) and modding overrides.
/// </summary>
public sealed class VfsStatusResult
{
    public IReadOnlyList<string> SearchOrder { get; init; } =
    [
        "override",
        "base",
        "game",
        "../shared",
        "KONGOR/configuration",
        "KONGOR/updates",
        "KONGOR/extensions"
    ];

    public bool OverrideMountedFirst { get; init; } = true;
    public int OverrideFilesCount { get; init; }
    public int ShadowedFilesCount { get; init; }
    public IReadOnlyList<string> ShadowedFiles { get; init; } = [];
    public string OverrideDirectory { get; init; } = string.Empty;
}

/// <summary>
/// Diagnostic inspector for K2 VFS mount hierarchy, modding overrides, and shadowed files.
/// </summary>
public static class VfsDiagnostics
{
    private static readonly string[] CanonicalSearchOrder =
    [
        "override",
        "base",
        "game",
        "../shared",
        "KONGOR/configuration",
        "KONGOR/updates",
        "KONGOR/extensions"
    ];

    public static VfsStatusResult GetVfsStatus(string? clientDir = null)
    {
        string baseDir = clientDir ?? ResolveClientDirectory();
        string overrideDir = Path.Combine(baseDir, "override");
        string gameDir = Path.Combine(baseDir, "game");
        string sharedDir = Path.GetFullPath(Path.Combine(baseDir, "..", "shared"));

        if (!Directory.Exists(overrideDir))
        {
            return new VfsStatusResult
            {
                SearchOrder = CanonicalSearchOrder,
                OverrideMountedFirst = true,
                OverrideFilesCount = 0,
                ShadowedFilesCount = 0,
                ShadowedFiles = [],
                OverrideDirectory = overrideDir
            };
        }

        List<string> shadowed = [];
        int overrideCount = 0;

        try
        {
            DirectoryInfo overrideInfo = new(overrideDir);
            FileInfo[] allOverrideFiles = overrideInfo.GetFiles("*", SearchOption.AllDirectories);
            overrideCount = allOverrideFiles.Length;

            foreach (FileInfo fi in allOverrideFiles)
            {
                string relPath = Path.GetRelativePath(overrideDir, fi.FullName);

                // Check if identical relative path exists in game/ or shared/
                string inGame = Path.Combine(gameDir, relPath);
                string inShared = Path.Combine(sharedDir, relPath);

                if (File.Exists(inGame) || File.Exists(inShared))
                {
                    shadowed.Add(relPath.Replace('\\', '/'));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VfsDiagnostics] Error scanning override files: {ex.Message}");
        }

        return new VfsStatusResult
        {
            SearchOrder = CanonicalSearchOrder,
            OverrideMountedFirst = true,
            OverrideFilesCount = overrideCount,
            ShadowedFilesCount = shadowed.Count,
            ShadowedFiles = shadowed.Take(50).ToList(),
            OverrideDirectory = overrideDir
        };
    }

    private static string ResolveClientDirectory()
    {
        string current = Environment.CurrentDirectory;
        string direct = Path.Combine(current, "games", "client");
        if (Directory.Exists(direct)) return direct;

        DirectoryInfo? dir = new(current);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "games", "client");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return Path.Combine(current, "games", "client");
    }
}
