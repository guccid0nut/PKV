// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using Kongor.Core.Configuration;
using Spectre.Console;

namespace Kongor.Cli.Infrastructure;

internal sealed record AppLaunchSpec(
    string DisplayName,
    string CacheKey,
    string RelativeProjectPath,
    string BinaryBaseName,
    bool UseShadowCopy,
    string? RelativeWorkingDirectory = null);

internal sealed record StalenessCheckResult(bool IsStale, string Reason);

internal sealed record ShadowSyncResult(
    string SlotDirectory,
    string ExecutablePath,
    int CopiedFiles,
    int SkippedUnchangedFiles,
    long CopiedBytes,
    bool RolledOverDueToLock);

/// <summary>
/// Fast, lock-free application and sub-tool launcher:
/// 1) Checks compiled Release binary staleness against project sources (&lt;5ms).
/// 2) Auto-builds through <c>scripts/Invoke-KongorTestLock.ps1</c> ONLY when missing, stale, or <c>--build</c> is passed.
/// 3) Syncs long-lived GUI/supervisor apps (WILLOWMAKER, COMPEL, Control Panel) into a Host-RID filtered
///    reusable dual-slot cache (<c>slot-a</c>/<c>slot-b</c>) so keeping apps open never triggers Windows <c>MSB3027</c> DLL locks
///    during <c>kongor build</c> and never copies 2+ GB of foreign RID publish folders.
/// </summary>
internal static class FastAppLauncher
{
    private static readonly HashSet<string> WatchedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".axaml",
        ".xaml",
        ".razor",
        ".cshtml",
        ".props",
        ".targets"
    };

    private static readonly HashSet<string> ExcludedSourceDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        ".venv",
        "logs",
        "TestResults",
        "publish"
    };

    private static readonly HashSet<string> ExcludedRuntimeTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
        "win-x64",
        "win-arm64",
        "win-x86",
        "publish",
        "ref",
        "refint",
        "nupkg",
        "logs"
    };

    /// <summary>
    /// Fast source-staleness check comparing <paramref name="binaryExePath"/> against project source timestamps.
    /// </summary>
    public static StalenessCheckResult CheckStaleness(string projectFilePath, string binaryExePath, string workspaceRoot)
    {
        if (!File.Exists(binaryExePath))
        {
            return new StalenessCheckResult(true, "compiled binary does not exist yet");
        }

        DateTime binaryUtc = File.GetLastWriteTimeUtc(binaryExePath);

        // Check solution-wide props first
        string[] rootProps =
        [
            Path.Combine(workspaceRoot, "Directory.Build.props"),
            Path.Combine(workspaceRoot, "Directory.Packages.props")
        ];
        foreach (string propFile in rootProps)
        {
            if (File.Exists(propFile) && File.GetLastWriteTimeUtc(propFile) > binaryUtc)
            {
                return new StalenessCheckResult(true, $"{Path.GetFileName(propFile)} is newer than compiled binary");
            }
        }

        string? projectDir = Path.GetDirectoryName(projectFilePath);
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return new StalenessCheckResult(false, "project directory not found");
        }

        Stack<string> dirs = new();
        dirs.Push(projectDir);
        while (dirs.Count > 0)
        {
            string current = dirs.Pop();
            foreach (string file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (!WatchedSourceExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(file) > binaryUtc)
                {
                    string rel = Path.GetRelativePath(projectDir, file);
                    return new StalenessCheckResult(true, $"source file '{rel}' is newer than compiled binary");
                }
            }

            foreach (string subDir in Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                string dirName = Path.GetFileName(subDir);
                if (!ExcludedSourceDirs.Contains(dirName))
                {
                    dirs.Push(subDir);
                }
            }
        }

        return new StalenessCheckResult(false, "up-to-date");
    }

    /// <summary>
    /// Incrementally mirrors Host-RID filtered binaries into a reusable dual-slot shadow cache (<c>slot-a</c>/<c>slot-b</c>).
    /// Copies 0 bytes (&lt;15ms) when unchanged; automatically rolls over to <c>slot-b</c> if <c>slot-a</c> is locked by a running instance.
    /// </summary>
    public static ShadowSyncResult SyncToShadowSlot(
        string sourceBinDir,
        string appCacheKey,
        string binaryBaseName,
        string? hostRidOverride = null,
        bool? isWindowsOverride = null,
        string? cacheBaseDirOverride = null,
        Action<string>? logger = null)
    {
        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        string hostRid = !string.IsNullOrWhiteSpace(hostRidOverride)
            ? hostRidOverride
            : BundledLayout.DetectCurrentRid(osOverride: isWindows ? OSPlatform.Windows : (OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux));
        string exeName = binaryBaseName + (isWindows ? ".exe" : string.Empty);

        string baseCache = !string.IsNullOrWhiteSpace(cacheBaseDirOverride)
            ? cacheBaseDirOverride
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProjectKongor",
                "runtime-cache");

        string appCacheRoot = Path.Combine(baseCache, appCacheKey);
        Directory.CreateDirectory(appCacheRoot);

        string[] preferredSlots =
        [
            Path.Combine(appCacheRoot, "slot-a"),
            Path.Combine(appCacheRoot, "slot-b"),
            Path.Combine(appCacheRoot, $"slot-{Environment.ProcessId}")
        ];

        bool rolledOver = false;
        Exception? lastLockEx = null;

        for (int s = 0; s < preferredSlots.Length; s++)
        {
            string slotDir = preferredSlots[s];
            try
            {
                (int copied, int skipped, long copiedBytes) = IncrementalCopyFilteredRuntime(sourceBinDir, slotDir, hostRid, isWindows);
                string exePath = Path.Combine(slotDir, exeName);
                logger?.Invoke(
                    $"[FastLaunch] Shadow slot '{Path.GetFileName(slotDir)}' ready for {appCacheKey}: " +
                    $"copied={copied} ({copiedBytes / (1024.0 * 1024.0):F1} MB), unchanged={skipped}, rolledOver={rolledOver}");
                return new ShadowSyncResult(slotDir, exePath, copied, skipped, copiedBytes, rolledOver);
            }
            catch (IOException ex)
            {
                lastLockEx = ex;
                rolledOver = true;
                logger?.Invoke($"[FastLaunch] Shadow slot '{Path.GetFileName(slotDir)}' locked ({ex.Message}); rolling over to next slot.");
            }
            catch (UnauthorizedAccessException ex)
            {
                lastLockEx = ex;
                rolledOver = true;
                logger?.Invoke($"[FastLaunch] Shadow slot '{Path.GetFileName(slotDir)}' access denied ({ex.Message}); rolling over to next slot.");
            }
        }

        throw new IOException(
            $"Failed to synchronize shadow-copy runtime for '{appCacheKey}' across all slots in '{appCacheRoot}': {lastLockEx?.Message}",
            lastLockEx);
    }

    /// <summary>
    /// Launches a ProjectKongor desktop app, supervisor, or CLI sub-tool using the fast binary path
    /// (with staleness check, TestLock auto-build, and optional dual-slot shadow copy).
    /// </summary>
    public static int LaunchFromSourceWorkspace(
        AppLaunchSpec spec,
        string[] rawArgs,
        KongorWorkspaceResult workspace,
        Func<string, string, string, int> runProcess,
        Func<string, string, string, int> runDotnetWithLock)
    {
        bool forceBuild = false;
        bool forceNoBuild = false;
        List<string> forwardedArgs = new(rawArgs.Length);
        foreach (string arg in rawArgs)
        {
            if (arg.Equals("--build", StringComparison.OrdinalIgnoreCase) && !spec.CacheKey.Equals("mod-cli", StringComparison.OrdinalIgnoreCase))
            {
                forceBuild = true;
                continue;
            }
            if (arg.Equals("--no-build", StringComparison.OrdinalIgnoreCase))
            {
                forceNoBuild = true;
                continue;
            }
            forwardedArgs.Add(arg);
        }

        string repo = workspace.Root;
        string projectPath = Path.Combine(repo, spec.RelativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
        string projectDir = Path.GetDirectoryName(projectPath) ?? repo;
        string exeSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string releaseBinDir = Path.Combine(projectDir, "bin", "Release", "net11.0");
        string releaseExe = Path.Combine(releaseBinDir, spec.BinaryBaseName + exeSuffix);
        string debugBinDir = Path.Combine(projectDir, "bin", "Debug", "net11.0");
        string debugExe = Path.Combine(debugBinDir, spec.BinaryBaseName + exeSuffix);

        string activeBinDir = releaseBinDir;
        string activeExe = releaseExe;

        if (!File.Exists(releaseExe) && File.Exists(debugExe) && forceNoBuild)
        {
            activeBinDir = debugBinDir;
            activeExe = debugExe;
        }
        else
        {
            StalenessCheckResult staleness = CheckStaleness(projectPath, releaseExe, repo);
            bool needsBuild = forceBuild || (!forceNoBuild && staleness.IsStale);

            if (needsBuild)
            {
                string reason = forceBuild ? "--build requested" : staleness.Reason;
                AnsiConsole.MarkupLine($"[yellow]🔨 Building {Markup.Escape(spec.DisplayName)} (Release — {Markup.Escape(reason)})...[/]");
                string testLock = Path.Combine(repo, "scripts", "Invoke-KongorTestLock.ps1");
                int buildCode = runDotnetWithLock(testLock, $"build \"{projectPath}\" -c Release --nologo", repo);
                if (buildCode != 0)
                {
                    AnsiConsole.MarkupLine($"[bold red]✗ Build failed for {Markup.Escape(spec.DisplayName)} (exit {buildCode}).[/]");
                    return buildCode;
                }
            }
        }

        if (!File.Exists(activeExe))
        {
            AnsiConsole.MarkupLine(
                $"[bold red]✗ Executable not found after build:[/] [white]{Markup.Escape(activeExe)}[/]\n" +
                $"[yellow]Fix:[/] Run [bold]kongor build {Markup.Escape(spec.CacheKey)}[/]");
            return 1;
        }

        string launchExe = activeExe;
        if (spec.UseShadowCopy)
        {
            Stopwatch syncSw = Stopwatch.StartNew();
            ShadowSyncResult shadow = SyncToShadowSlot(
                activeBinDir,
                spec.CacheKey,
                spec.BinaryBaseName,
                logger: msg => Console.Error.WriteLine(msg));
            syncSw.Stop();
            launchExe = shadow.ExecutablePath;
            AnsiConsole.MarkupLine(
                $"[grey]  Shadow runtime: {Markup.Escape(Path.GetFileName(shadow.SlotDirectory))} " +
                $"({shadow.CopiedFiles} updated, {shadow.SkippedUnchangedFiles} cached, {syncSw.ElapsedMilliseconds}ms — bin/ remains unlocked)[/]");
        }

        string workDir = !string.IsNullOrWhiteSpace(spec.RelativeWorkingDirectory)
            ? Path.Combine(repo, spec.RelativeWorkingDirectory.Replace('/', Path.DirectorySeparatorChar))
            : repo;
        if (!Directory.Exists(workDir))
        {
            workDir = repo;
        }

        // Ensure WILLOWMAKER_CLIENT_PATH is propagated so shadow-copied GUI processes always discover games/client immediately
        string clientDir = Path.Combine(repo, "games", "client");
        if (Directory.Exists(clientDir))
        {
            Environment.SetEnvironmentVariable("WILLOWMAKER_CLIENT_PATH", clientDir);
        }

        return runProcess(launchExe, Program.FormatProcessArgs(forwardedArgs), workDir);
    }

    private static (int Copied, int Skipped, long CopiedBytes) IncrementalCopyFilteredRuntime(
        string sourceDir,
        string destDir,
        string hostRid,
        bool isWindows)
    {
        Directory.CreateDirectory(destDir);
        int copied = 0;
        int skipped = 0;
        long copiedBytes = 0;

        foreach (string srcFile in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(srcFile);
            if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyIfChanged(srcFile, Path.Combine(destDir, fileName), ref copied, ref skipped, ref copiedBytes);
        }

        HashSet<string> allowedRids = BuildAllowedRids(hostRid, isWindows);
        foreach (string subDir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(subDir);
            if (ExcludedRuntimeTopDirs.Contains(name))
            {
                continue;
            }

            if (name.Equals("windows-x64", StringComparison.OrdinalIgnoreCase) && !isWindows)
            {
                continue;
            }

            if (name.Equals("runtimes", StringComparison.OrdinalIgnoreCase))
            {
                string destRuntimes = Path.Combine(destDir, "runtimes");
                foreach (string ridDir in Directory.GetDirectories(subDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string ridName = Path.GetFileName(ridDir);
                    if (!allowedRids.Contains(ridName))
                    {
                        continue;
                    }

                    SyncDirectoryRecursive(ridDir, Path.Combine(destRuntimes, ridName), ref copied, ref skipped, ref copiedBytes);
                }
                continue;
            }

            SyncDirectoryRecursive(subDir, Path.Combine(destDir, name), ref copied, ref skipped, ref copiedBytes);
        }

        return (copied, skipped, copiedBytes);
    }

    private static void SyncDirectoryRecursive(
        string srcDir,
        string destDir,
        ref int copied,
        ref int skipped,
        ref long copiedBytes)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
        {
            CopyIfChanged(file, Path.Combine(destDir, Path.GetFileName(file)), ref copied, ref skipped, ref copiedBytes);
        }

        foreach (string subDir in Directory.GetDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
        {
            SyncDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), ref copied, ref skipped, ref copiedBytes);
        }
    }

    private static void CopyIfChanged(
        string srcFile,
        string dstFile,
        ref int copied,
        ref int skipped,
        ref long copiedBytes)
    {
        FileInfo srcInfo = new(srcFile);
        if (File.Exists(dstFile))
        {
            FileInfo dstInfo = new(dstFile);
            if (dstInfo.Length == srcInfo.Length &&
                Math.Abs((dstInfo.LastWriteTimeUtc - srcInfo.LastWriteTimeUtc).TotalSeconds) < 1.0)
            {
                skipped++;
                return;
            }
        }

        File.Copy(srcFile, dstFile, overwrite: true);
        copied++;
        copiedBytes += srcInfo.Length;
    }

    private static HashSet<string> BuildAllowedRids(string hostRid, bool isWindows)
    {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase) { hostRid };
        if (isWindows)
        {
            allowed.Add("win");
        }
        else if (hostRid.StartsWith("osx", StringComparison.OrdinalIgnoreCase))
        {
            allowed.Add("osx");
            allowed.Add("unix");
        }
        else
        {
            allowed.Add("unix");
            allowed.Add("linux");
        }
        return allowed;
    }
}
