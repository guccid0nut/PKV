// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientFolderNormalizer.cs
// System: WILLOWMAKER Core Services - Game Client Directory Normalization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Detects user-added game client folders with non-standard names (e.g.
///     'HoN Game Client v4.10.1 Windows Clean Installation', 'hon-client')
///     and safely normalizes them to the canonical 'games/client' path expected by the engine.
/// </summary>
public static class ClientFolderNormalizer
{
    /// <summary>
    ///     Inspects search directories and normalizes any folder containing 'client' and the HoN executable
    ///     into 'games/client'. Disabled in Playtest mode so standalone exports never rename parent directories.
    /// </summary>
    /// <param name="startingDirectory">Starting search directory (defaults to AppContext.BaseDirectory).</param>
    /// <param name="logAction">Optional logger callback.</param>
    /// <returns>The normalized 'games/client' path if resolved, or null.</returns>
    public static string? NormalizeClientDirectory(string? startingDirectory = null, Action<string>? logAction = null)
    {
        string targetExeName = DeploymentManifest.HeroesOfNewerthExecutableFileName;
        string start = startingDirectory ?? AppContext.BaseDirectory;
        PlaytestPolicyDto policy = PlaytestPolicyService.Resolve(start, logAction);

        if (policy.DisableFolderNormalizer)
        {
            logAction?.Invoke("[PlaytestPolicy] ClientFolderNormalizer directory-rename scan disabled in Playtest mode.");
            string readOnlyCurrent = start;
            for (int i = 0; i < 3; i++)
            {
                string canonical = Path.Combine(readOnlyCurrent, "games", "client");
                if (HasCanonicalClientExecutable(canonical, targetExeName))
                {
                    return canonical;
                }

                string? parentDir = Directory.GetParent(readOnlyCurrent)?.FullName;
                if (string.IsNullOrEmpty(parentDir) || parentDir == readOnlyCurrent)
                {
                    break;
                }

                readOnlyCurrent = parentDir;
            }

            return null;
        }

        // Traverse up to 6 parent directories looking for the workspace or games/ root
        string current = start;
        for (int i = 0; i < 6; i++)
        {
            string canonicalClientDir = Path.Combine(current, "games", "client");

            // 1. If canonical games/client already exists with the executable, nothing to normalize
            if (HasCanonicalClientExecutable(canonicalClientDir, targetExeName))
            {
                return canonicalClientDir;
            }

            // 2. Check within games/ directory for any folder containing 'client'
            string gamesDir = Path.Combine(current, "games");
            if (Directory.Exists(gamesDir))
            {
                string? normalized = CheckAndRenameCandidate(gamesDir, canonicalClientDir, targetExeName, logAction);
                if (!string.IsNullOrEmpty(normalized))
                {
                    return normalized;
                }
            }

            // 3. Also check within current directory itself for any folder containing 'client'
            // (e.g. if the user dropped the folder directly at the root)
            string? rootCandidate = CheckAndRenameCandidate(current, canonicalClientDir, targetExeName, logAction);
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

    private static bool HasCanonicalClientExecutable(string clientDir, string targetExeName)
    {
        if (!Directory.Exists(clientDir))
        {
            return false;
        }

        return File.Exists(Path.Combine(clientDir, targetExeName)) ||
               File.Exists(Path.Combine(clientDir, "windows-x64", targetExeName)) ||
               File.Exists(Path.Combine(clientDir, "linux-x64", targetExeName)) ||
               File.Exists(Path.Combine(clientDir, "macos-x64", targetExeName)) ||
               File.Exists(Path.Combine(clientDir, "macos-x64", "Heroes of Newerth.app", "Contents", "MacOS", targetExeName));
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

                // Skip canonical 'client' itself
                if (string.Equals(dirName, "client", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Match any directory name containing 'client' (case-insensitive)
                if (dirName.Contains("client", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasExe = File.Exists(Path.Combine(subDir, targetExeName)) ||
                                  File.Exists(Path.Combine(subDir, "windows-x64", targetExeName)) ||
                                  File.Exists(Path.Combine(subDir, "k2_x64.dll")) ||
                                  File.Exists(Path.Combine(subDir, "windows-x64", "k2_x64.dll"));

                    if (hasExe)
                    {
                        logAction?.Invoke(
                            $"[CLIENT_NORMALIZER] Found non-standard game client folder: '{subDir}'. Auto-normalizing to '{canonicalTargetDir}'...");

                        string? targetParent = Path.GetDirectoryName(canonicalTargetDir);
                        if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
                        {
                            Directory.CreateDirectory(targetParent);
                        }

                        // Perform atomic directory move/rename
                        Directory.Move(subDir, canonicalTargetDir);
                        logAction?.Invoke(
                            $"[CLIENT_NORMALIZER] Successfully normalized client directory to '{canonicalTargetDir}'.");
                        return canonicalTargetDir;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[CLIENT_NORMALIZER] Warning: Could not normalize directory in '{searchDir}': {ex.Message}");
        }

        return null;
    }
}
