// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Refuses to launch WILLOWMAKER from a directory whose contents do not match either an existing Heroes Of Newerth
///     install or a fresh WILLOWMAKER-only deployment.
///     Prevents the content synchronisation process from deleting unrelated user files when the user has accidentally
///     placed WILLOWMAKER in a personal directory.
/// </summary>
public static class LocationGuard
{
    /// <summary>
    ///     The maximum number of foreign entries to surface in user-facing displays (the guard dialog and the launcher log).
    ///     Lists longer than this cap are truncated and the remaining count is summarised with a trailing sentinel produced by
    ///     <see cref="ApplyForeignEntriesDisplayCap" />.
    /// </summary>
    public const int MaximumCountOfForeignEntriesToDisplay = 25;

    /// <summary>
    ///     Truncates <paramref name="foreignEntries" /> to at most <see cref="MaximumCountOfForeignEntriesToDisplay" /> items.
    ///     When the input exceeds the cap, a trailing summary entry of the form <c>"... and N more"</c> is appended in place
    ///     of the omitted names.
    ///     Both the guard dialog and the launcher log run their copies of the list through this helper so they stay
    ///     consistent.
    /// </summary>
    public static IReadOnlyList<string> ApplyForeignEntriesDisplayCap(IReadOnlyList<string> foreignEntries)
    {
        if (foreignEntries.Count <= MaximumCountOfForeignEntriesToDisplay)
            return foreignEntries;

        int remaining = foreignEntries.Count - MaximumCountOfForeignEntriesToDisplay;

        return [.. foreignEntries.Take(MaximumCountOfForeignEntriesToDisplay), $"... and {remaining} more"];
    }

    /// <summary>
    ///     Evaluates the supplied directory against the safety criteria, in order:
    ///     1) Development build → DEVELOPMENT ENVIRONMENT. The application is allowed to run, but operations that modify the
    ///     directory are skipped.
    ///     2) Heroes Of Newerth executable present at the top level → SAFE. The directory is an existing game install.
    ///     3) The directory contains only the WILLOWMAKER distribution files and ignored runtime artefacts → SAFE. The
    ///     directory is a fresh deployment ready for first-time synchronisation.
    ///     4) Otherwise → UNSAFE. The directory contains foreign content that the synchronisation process would put at risk.
    /// </summary>
    public static Result AssessLocationSafety(string directory)
    {
        if (DeploymentManifest.IsDevelopmentBuild)
            return new Result(LocationSafetyVerdict.DevelopmentEnvironment, "Development Environment Detected", []);

        string gameExecutableName = DeploymentManifest.HeroesOfNewerthExecutableFileName;

        if (HasGameExecutable(directory, gameExecutableName))
            return new Result(LocationSafetyVerdict.Safe,
                $@"Heroes Of Newerth Directory (""{gameExecutableName}"" Is Present)", []);

        string? discoveredGameDir = ResolveGameClientDirectory(directory);
        if (!string.IsNullOrEmpty(discoveredGameDir) &&
            HasGameExecutable(discoveredGameDir, gameExecutableName))
        {
            return new Result(LocationSafetyVerdict.Safe,
                $@"Heroes Of Newerth Game Client Directory Located (""{Path.Combine(discoveredGameDir, gameExecutableName)}"")",
                []);
        }

        IReadOnlyList<string> foreignEntries = EnumerateForeignEntries(directory);

        if (foreignEntries.Count is 0)
            return new Result(LocationSafetyVerdict.Safe, $"Baseline {DeploymentManifest.ApplicationName} Directory",
                []);

        return new Result(LocationSafetyVerdict.Unsafe, "Foreign Entries Detected", foreignEntries);
    }

    /// <summary>
    ///     Searches parent directories and environment variables for a standard 'games/client' directory containing the game client executable.
    /// </summary>
    public static string? ResolveGameClientDirectory(string? startingDirectory = null)
    {
        string gameExe = DeploymentManifest.HeroesOfNewerthExecutableFileName;

        // 1. Check environment variables
        string[] envVars = ["HON_GAME_PATH", "WILLOWMAKER_CLIENT_PATH", "GAME_CLIENT_PATH", "HON_CLIENT_PATH"];
        foreach (string varName in envVars)
        {
            string? envVal = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                if (File.Exists(envVal) && Path.GetFileName(envVal).Equals(gameExe, StringComparison.OrdinalIgnoreCase))
                {
                    string? dir = Path.GetDirectoryName(envVal);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
                if (Directory.Exists(envVal) && File.Exists(Path.Combine(envVal, gameExe)))
                    return envVal;
            }
        }

        // 1b. Check unified KONGOR_ROOT / KongorWorkspaceResolver (supports shadow-copy launches & out-of-tree invocations)
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(
                out Kongor.Core.Configuration.KongorWorkspaceResult? ws,
                new Kongor.Core.Configuration.WorkspaceResolveOptions
                {
                    InvocationCwd = startingDirectory ?? Environment.CurrentDirectory
                }) && ws is not null)
        {
            string wsClient = Path.Combine(ws.Root, "games", "client");
            if (HasGameExecutable(wsClient, gameExe))
                return wsClient;
        }

        // 2. Check starting / base directory
        string current = startingDirectory ?? AppContext.BaseDirectory;

        if (HasGameExecutable(current, gameExe))
            return current;

        // Auto-normalize any user-added *client* folder in parent hierarchy
        string? normalizedFromBase = ClientFolderNormalizer.NormalizeClientDirectory(current);
        if (!string.IsNullOrEmpty(normalizedFromBase) && HasGameExecutable(normalizedFromBase, gameExe))
            return normalizedFromBase;

        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.Combine(current, "games", "client");
            if (HasGameExecutable(candidate, gameExe))
                return candidate;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        // 3. Check Environment.CurrentDirectory
        current = Environment.CurrentDirectory;
        if (HasGameExecutable(current, gameExe))
            return current;

        string? normalizedFromCurrent = ClientFolderNormalizer.NormalizeClientDirectory(current);
        if (!string.IsNullOrEmpty(normalizedFromCurrent) && HasGameExecutable(normalizedFromCurrent, gameExe))
            return normalizedFromCurrent;

        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.Combine(current, "games", "client");
            if (HasGameExecutable(candidate, gameExe))
                return candidate;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        return null;
    }

    /// <summary>
    ///     Computes the recommended clean destination directory for 1-click self-relocation when WILLOWMAKER is launched
    ///     from an unsafe personal directory (e.g. Desktop or Downloads).
    /// </summary>
    public static string ResolveRecommendedRelocationDirectory(string sourceDirectory)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "ProjectKongor", "WILLOWMAKER");
        }

        return Path.Combine(sourceDirectory, "ProjectKongor-WILLOWMAKER");
    }

    /// <summary>
    ///     1-click safe self-relocation: copies ONLY recognized WILLOWMAKER payload files from
    ///     <paramref name="sourceDirectory" /> into a clean dedicated directory without modifying or deleting any foreign
    ///     user files in <paramref name="sourceDirectory" />.
    /// </summary>
    public static RelocationResult RelocateToDedicatedFolder(
        string sourceDirectory,
        string? targetDirectoryOverride = null,
        bool launchRelocated = false)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return new RelocationResult(false, string.Empty, [], $"Source directory '{sourceDirectory}' does not exist.");
        }

        string targetDirectory = string.IsNullOrWhiteSpace(targetDirectoryOverride)
            ? ResolveRecommendedRelocationDirectory(sourceDirectory)
            : Path.GetFullPath(targetDirectoryOverride);

        string normalizedSource = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedTarget = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return new RelocationResult(false, targetDirectory, [], "Target relocation folder cannot be the same as the unsafe source folder.");
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);

            HashSet<string> filesToCopy = new(StringComparer.OrdinalIgnoreCase)
            {
                DeploymentManifest.ApplicationExecutableFileName,
                "launcher_settings.json",
                "playtest_policy.json",
                "build_manifest.json"
            };
            foreach (string nativeLib in DeploymentManifest.NativeLibrariesFileNames)
            {
                filesToCopy.Add(nativeLib);
            }

            List<string> copiedFiles = [];
            foreach (string fileName in filesToCopy)
            {
                string srcPath = Path.Combine(sourceDirectory, fileName);
                if (!File.Exists(srcPath))
                    continue;

                string dstPath = Path.Combine(targetDirectory, fileName);
                File.Copy(srcPath, dstPath, overwrite: true);
                copiedFiles.Add(fileName);
            }

            System.Diagnostics.Trace.WriteLine(
                $"[LocationGuard] Relocated {copiedFiles.Count} WILLOWMAKER file(s) from '{sourceDirectory}' to '{targetDirectory}' (0 foreign files touched).");

            if (launchRelocated)
            {
                string relocatedExe = Path.Combine(targetDirectory, DeploymentManifest.ApplicationExecutableFileName);
                if (File.Exists(relocatedExe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = relocatedExe,
                        WorkingDirectory = targetDirectory,
                        UseShellExecute = true
                    });
                    System.Diagnostics.Trace.WriteLine($"[LocationGuard] Launched relocated WILLOWMAKER from '{relocatedExe}'.");
                }
            }

            return new RelocationResult(
                true,
                targetDirectory,
                copiedFiles,
                $"Copied {copiedFiles.Count} launcher file(s) to '{targetDirectory}' without touching your other files.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[LocationGuard] Self-relocation failed: {ex.GetType().Name}: {ex.Message}");
            return new RelocationResult(false, targetDirectory, [], $"Relocation failed: {ex.Message}");
        }
    }

    public sealed record RelocationResult(
        bool Succeeded,
        string TargetDirectory,
        IReadOnlyList<string> CopiedFiles,
        string Message);

    internal static bool HasGameExecutable(string directory, string gameExe)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        if (File.Exists(Path.Combine(directory, gameExe)))
        {
            return true;
        }

        string platformSubdir = OperatingSystem.IsWindows() ? "windows-x64"
            : OperatingSystem.IsLinux() ? "linux-x64"
            : OperatingSystem.IsMacOS() ? "macos-x64"
            : string.Empty;

        if (!string.IsNullOrEmpty(platformSubdir) &&
            File.Exists(Path.Combine(directory, platformSubdir, gameExe)))
        {
            return true;
        }

        return OperatingSystem.IsMacOS() &&
               (File.Exists(Path.Combine(directory, "Heroes of Newerth.app", "Contents", "MacOS", gameExe)) ||
                File.Exists(Path.Combine(directory, "macos-x64", "Heroes of Newerth.app", "Contents", "MacOS", gameExe)));
    }

    /// <summary>
    ///     Returns the top-level entries in <paramref name="directory" /> that are neither part of WILLOWMAKER's distribution
    ///     payload (the executable plus the native libraries that ship with the AOT publish on the current platform) nor on
    ///     the ignore list (runtime artefacts such as the log file).
    ///     Each entry is reported as a path relative to <paramref name="directory" />, which for the top-level enumeration is
    ///     the entry's bare name.
    ///     Subdirectory entries carry a trailing <c>/**</c> suffix so the calling UI and the launcher log can distinguish them
    ///     from same-named files.
    ///     The enumeration is configured to surface hidden and system entries as well, so that no foreign content can slip
    ///     past the guard by virtue of an attribute flag.
    ///     Subdirectories are always treated as foreign, regardless of their name, because the recognised list is defined in
    ///     terms of distribution files.
    ///     An empty subdirectory tree (no files at any depth) is omitted from the foreign-entries list because removing it
    ///     cannot cause data loss.
    /// </summary>
    private static IReadOnlyList<string> EnumerateForeignEntries(string directory)
    {
        HashSet<string> recognisedNames = new(StringComparer.OrdinalIgnoreCase)
            { DeploymentManifest.ApplicationExecutableFileName };

        foreach (string nativeLibraryName in DeploymentManifest.NativeLibrariesFileNames)
            recognisedNames.Add(nativeLibraryName);

        foreach (string ignoredFileName in DeploymentManifest.IgnoredFileNames)
            recognisedNames.Add(ignoredFileName);

        EnumerationOptions topLevelEnumerationOptions = new()
        {
            AttributesToSkip = FileAttributes.None,
            RecurseSubdirectories = false
        };

        EnumerationOptions recursiveEnumerationOptions = new()
        {
            AttributesToSkip = FileAttributes.None,
            RecurseSubdirectories = true
        };

        List<string> foreignEntries = [];

        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*",
                     topLevelEnumerationOptions))
            if (entry is FileInfo)
            {
                if (recognisedNames.Contains(entry.Name) is false)
                    foreignEntries.Add(Path.GetRelativePath(directory, entry.FullName));
            }

            else if (recognisedNames.Contains(entry.Name) is false &&
                     Directory.EnumerateFiles(entry.FullName, "*", recursiveEnumerationOptions).Any())
            {
                foreignEntries.Add($"{Path.GetRelativePath(directory, entry.FullName)}/**");
            }

        return foreignEntries;
    }

    /// <summary>
    ///     The outcome of a location safety assessment.
    ///     <paramref name="Verdict" /> classifies the directory.
    ///     <paramref name="Reason" /> is a short, human-readable justification suitable for logging.
    ///     <paramref name="ForeignEntries" /> lists the top-level entries that caused an
    ///     <see cref="LocationSafetyVerdict.Unsafe" /> verdict (empty for every other verdict).
    /// </summary>
    public sealed record Result(LocationSafetyVerdict Verdict, string Reason, IReadOnlyList<string> ForeignEntries);
}
