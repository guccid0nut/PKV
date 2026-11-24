// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ServerFolderNormalizer.cs
// System: COMPEL Services - Dedicated Game Server Directory Normalization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Detects user-added game server folders with non-standard names (e.g.
///     'HoN Game Server v4.10.1 Windows Clean Installation', 'hon-server')
///     and safely normalizes them to the canonical 'games/server' path expected by COMPEL.
/// </summary>
public static class ServerFolderNormalizer
{
    /// <summary>
    ///     Inspects search directories and normalizes any folder containing 'server' and the HoN server executable
    ///     into 'games/server'.
    /// </summary>
    /// <param name="startingDirectory">Starting search directory (defaults to AppContext.BaseDirectory).</param>
    /// <param name="logAction">Optional logger callback.</param>
    /// <returns>The normalized 'games/server' path if resolved, or null.</returns>
    public static string? NormalizeServerDirectory(string? startingDirectory = null, Action<string>? logAction = null)
    {
        string targetExeName = HeroesOfNewerthExecutable.FileName;
        string start = startingDirectory ?? AppContext.BaseDirectory;

        // Traverse up to 10 parent directories looking for workspace or games/ root
        string current = start;
        for (int i = 0; i < 10; i++)
        {
            string canonicalServerDir = Path.Combine(current, "games", "server");

            // 1. If canonical games/server already exists with the executable, nothing to normalize
            if (Directory.Exists(canonicalServerDir) &&
                (File.Exists(Path.Combine(canonicalServerDir, targetExeName)) ||
                 File.Exists(Path.Combine(canonicalServerDir, "windows-x64", targetExeName)) ||
                 File.Exists(Path.Combine(canonicalServerDir, "linux-x64", targetExeName))))
            {
                return canonicalServerDir;
            }

            // 2. Check within games/ directory for any folder containing 'server'
            string gamesDir = Path.Combine(current, "games");
            if (Directory.Exists(gamesDir))
            {
                string? normalized = CheckAndRenameCandidate(gamesDir, canonicalServerDir, targetExeName, logAction);
                if (!string.IsNullOrEmpty(normalized))
                {
                    return normalized;
                }
            }

            // 3. Also check within current directory itself
            string? rootCandidate = CheckAndRenameCandidate(current, canonicalServerDir, targetExeName, logAction);
            if (!string.IsNullOrEmpty(rootCandidate))
            {
                return rootCandidate;
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                break;
            }
            current = parent;
        }

        return null;
    }

    private static string? CheckAndRenameCandidate(
        string searchDir,
        string canonicalTargetDir,
        string targetExeName,
        Action<string>? logAction)
    {
        try
        {
            string[] subDirectories = Directory.GetDirectories(searchDir);
            foreach (string subDir in subDirectories)
            {
                string dirName = Path.GetFileName(subDir);

                // Skip canonical 'server' itself
                if (string.Equals(dirName, "server", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Match any directory name containing 'server' (case-insensitive)
                if (dirName.Contains("server", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasExe = File.Exists(Path.Combine(subDir, targetExeName)) ||
                                  File.Exists(Path.Combine(subDir, "windows-x64", targetExeName)) ||
                                  File.Exists(Path.Combine(subDir, "linux-x64", targetExeName)) ||
                                  File.Exists(Path.Combine(subDir, "k2_x64.dll")) ||
                                  File.Exists(Path.Combine(subDir, "libk2-x86_64-server.so"));

                    if (hasExe)
                    {
                        logAction?.Invoke(
                            $"[SERVER_NORMALIZER] Found non-standard game server folder: '{subDir}'. Auto-normalizing to '{canonicalTargetDir}'...");

                        string? targetParent = Path.GetDirectoryName(canonicalTargetDir);
                        if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
                        {
                            Directory.CreateDirectory(targetParent);
                        }

                        // Perform atomic directory move/rename
                        Directory.Move(subDir, canonicalTargetDir);
                        logAction?.Invoke(
                            $"[SERVER_NORMALIZER] Successfully normalized server directory to '{canonicalTargetDir}'.");
                        return canonicalTargetDir;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[SERVER_NORMALIZER] Warning: Could not normalize server directory in '{searchDir}': {ex.Message}");
        }

        return null;
    }
}
