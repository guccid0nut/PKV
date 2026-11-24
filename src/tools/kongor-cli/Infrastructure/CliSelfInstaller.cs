// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kongor.Core.Configuration;

namespace Kongor.Cli.Infrastructure;

internal sealed record CliInstallResult(
    bool Success,
    string ActiveSlotPath,
    string ShimPath,
    string BinDir,
    int CopiedFilesCount,
    long CopiedBytes,
    int PrunedOldSlots,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null);

internal sealed record PathShadowAuditResult(
    string ExpectedShimPath,
    IReadOnlyList<string> DiscoveredKongorBinaries,
    bool IsExpectedFirst,
    bool HasShadowingConflict);

/// <summary>
/// Unified side-by-side global CLI installer (<c>~/.kongor/bin</c> + <c>~/.kongor/cli/&lt;slot&gt;/</c>).
/// Filters <c>bin/Release/net11.0</c> strictly to current host-OS runtime files (avoiding 2+ GB cross-RID bloat),
/// installs the tiny zero-lock <c>Kongor.Shim</c> forwarder executable (or POSIX shell wrapper),
/// prepends <c>~/.kongor/bin</c> onto User <c>PATH</c>, registers the workspace in <c>~/.kongor/config.json</c>,
/// and cleans up stale unlocked slots and legacy conflicting shims synchronously with zero sleep hacks.
/// </summary>
internal static class CliSelfInstaller
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly HashSet<string> ExcludedTopLevelDirs = new(StringComparer.OrdinalIgnoreCase)
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
    /// Installs or refreshes the global CLI from either a developer source workspace or standalone bundled layout.
    /// </summary>
    public static CliInstallResult InstallFromSourceDirectory(
        string cliBinSourceDir,
        string? shimBinSourceDir,
        string workspaceRoot,
        KongorWorkspaceKind workspaceKind,
        string versionStamp,
        string? userHomeOverride = null,
        bool? isWindowsOverride = null,
        bool updateUserPath = true,
        bool cleanLegacyDotnetTool = true,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        List<string> warnings = [];
        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        string binDir = KongorUserConfigStore.ResolveBinDir(userHomeOverride);
        string slotsDir = KongorUserConfigStore.ResolveCliSlotsDir(userHomeOverride);
        string exeName = isWindows ? "kongor.exe" : "kongor";
        string shimPath = Path.Combine(binDir, exeName);

        if (!Directory.Exists(cliBinSourceDir) || !File.Exists(Path.Combine(cliBinSourceDir, exeName)))
        {
            return new CliInstallResult(
                false,
                string.Empty,
                shimPath,
                binDir,
                0,
                0,
                0,
                warnings,
                $"Compiled CLI binary '{Path.Combine(cliBinSourceDir, exeName)}' not found. " +
                "Fix: Build Kongor.Cli in Release mode first ('dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release').");
        }

        try
        {
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(slotsDir);

            string safeVersion = SanitizeSlotToken(versionStamp);
            string timestamp = clock.GetUtcNow().ToString("yyyyMMddHHmmssfff");
            string slotDir = Path.Combine(slotsDir, $"{safeVersion}-{timestamp}");

            string hostRid = BundledLayout.DetectCurrentRid(
                osOverride: isWindows ? OSPlatform.Windows : (OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux));
            (int copiedFiles, long copiedBytes) = CopyFilteredHostRuntime(cliBinSourceDir, slotDir, hostRid, isWindows, logger);
            logger?.Invoke(
                $"[CliInstall] Staged slot '{slotDir}' ({copiedFiles} files, {copiedBytes / (1024.0 * 1024.0):F1} MB, rid={hostRid}).");

            // Atomically update current.txt pointer
            string pointerPath = Path.Combine(slotsDir, "current.txt");
            string tempPointerPath = pointerPath + $".tmp.{Environment.ProcessId}";
            File.WriteAllText(tempPointerPath, slotDir + "\n", Utf8NoBom);
            File.Move(tempPointerPath, pointerPath, overwrite: true);

            // Install zero-lock forwarder shim into ~/.kongor/bin
            if (isWindows)
            {
                InstallWindowsShim(binDir, shimBinSourceDir, slotDir, warnings, logger);
            }
            else
            {
                InstallPosixShim(binDir, userHomeOverride, pointerPath, slotDir, warnings, logger);
            }

            // Persist workspace + active CLI slot in ~/.kongor/config.json
            KongorUserConfigStore.RegisterOrUpdateWorkspace(
                workspaceRoot,
                workspaceKind,
                setActive: true,
                userHomeOverride: userHomeOverride,
                timeProvider: clock,
                diagnosticLogger: logger);
            KongorUserConfigStore.SetActiveCliSlot(
                slotDir,
                userHomeOverride: userHomeOverride,
                timeProvider: clock,
                diagnosticLogger: logger);

            // Prepend ~/.kongor/bin to User PATH so the new shim wins over any legacy ~/.dotnet/tools/kongor.exe
            if (updateUserPath && string.IsNullOrWhiteSpace(userHomeOverride))
            {
                if (isWindows && OperatingSystem.IsWindows())
                {
                    PrependWindowsUserPath(binDir, logger);
                }
            }

            // Best-effort cleanup of legacy conflicting shims
            if (cleanLegacyDotnetTool)
            {
                CleanLegacyConflictingShims(userHomeOverride, isWindows, warnings, logger);
            }

            // Best-effort garbage collection of unlocked older slots (keep active + 1 previous rollback slot)
            int pruned = PruneOldSlots(slotsDir, slotDir, keepPreviousCount: 1, logger);

            return new CliInstallResult(
                true,
                slotDir,
                shimPath,
                binDir,
                copiedFiles,
                copiedBytes,
                pruned,
                warnings);
        }
        catch (Exception ex)
        {
            return new CliInstallResult(
                false,
                string.Empty,
                shimPath,
                binDir,
                0,
                0,
                0,
                warnings,
                ex.Message);
        }
    }

    /// <summary>
    /// Copies only the files required to run on <paramref name="hostRid"/>, stripping sibling RID publish dirs
    /// and foreign <c>runtimes/&lt;other-os&gt;</c> native binaries (reducing 2.2 GB bins down to ~150 MB).
    /// </summary>
    public static (int FileCount, long TotalBytes) CopyFilteredHostRuntime(
        string sourceDir,
        string destinationDir,
        string hostRid,
        bool isWindows,
        Action<string>? logger = null)
    {
        Directory.CreateDirectory(destinationDir);
        int fileCount = 0;
        long totalBytes = 0;

        // 1. Copy top-level files (exe, dll, json, etc.)
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destFile = Path.Combine(destinationDir, fileName);
            File.Copy(file, destFile, overwrite: true);
            fileCount++;
            totalBytes += new FileInfo(destFile).Length;
        }

        // 2. Selectively copy required subdirectories
        HashSet<string> allowedRuntimeSubdirs = BuildAllowedRuntimeSubdirs(hostRid, isWindows);
        foreach (string subDir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string dirName = Path.GetFileName(subDir);
            if (ExcludedTopLevelDirs.Contains(dirName))
            {
                continue;
            }

            if (dirName.Equals("runtimes", StringComparison.OrdinalIgnoreCase))
            {
                string destRuntimes = Path.Combine(destinationDir, "runtimes");
                foreach (string ridDir in Directory.GetDirectories(subDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string ridName = Path.GetFileName(ridDir);
                    if (!allowedRuntimeSubdirs.Contains(ridName))
                    {
                        continue;
                    }

                    (int c, long b) = CopyDirectoryRecursive(ridDir, Path.Combine(destRuntimes, ridName));
                    fileCount += c;
                    totalBytes += b;
                }
                continue;
            }

            // Copy Data, satellite resource folders (e.g. de/fr/sv), and host-native folders (windows-x64)
            if (dirName.Equals("windows-x64", StringComparison.OrdinalIgnoreCase) && !isWindows)
            {
                continue;
            }

            (int subCount, long subBytes) = CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, dirName));
            fileCount += subCount;
            totalBytes += subBytes;
        }

        logger?.Invoke($"[CliInstall] Filtered runtime copy from '{sourceDir}' -> '{destinationDir}': {fileCount} files ({totalBytes / (1024.0 * 1024.0):F1} MB).");
        return (fileCount, totalBytes);
    }

    /// <summary>
    /// Audits the current <c>PATH</c> to detect whether any other <c>kongor</c> executable shadows <c>~/.kongor/bin</c>.
    /// </summary>
    public static PathShadowAuditResult AuditPathShadowing(
        string? pathEnvOverride = null,
        string? userHomeOverride = null,
        bool? isWindowsOverride = null)
    {
        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        string binDir = KongorUserConfigStore.ResolveBinDir(userHomeOverride);
        string expectedShim = Path.Combine(binDir, isWindows ? "kongor.exe" : "kongor");

        string pathEnv = pathEnvOverride ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        char sep = isWindows ? ';' : ':';
        string[] candidateNames = isWindows ? ["kongor.exe", "kongor.cmd", "kongor.bat"] : ["kongor"];

        List<string> discovered = [];
        HashSet<string> seen = new(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (string rawEntry in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string entry = rawEntry.Trim('"');
            if (string.IsNullOrWhiteSpace(entry) || !Directory.Exists(entry))
            {
                continue;
            }

            foreach (string name in candidateNames)
            {
                string full = Path.Combine(entry, name);
                if (File.Exists(full) && seen.Add(full))
                {
                    discovered.Add(full);
                }
            }
        }

        string home = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StringComparison comp = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        string standaloneShim = isWindows
            ? Path.Combine(
                !string.IsNullOrWhiteSpace(userHomeOverride) ? Path.Combine(home, "AppData", "Local") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProjectKongor", "bin", "kongor.cmd")
            : Path.Combine(home, ".local", "bin", "kongor");

        bool isExpectedFirst = false;
        if (discovered.Count > 0)
        {
            string firstNorm = KongorUserConfigStore.NormalizePathSafe(discovered[0]);
            isExpectedFirst =
                string.Equals(firstNorm, KongorUserConfigStore.NormalizePathSafe(expectedShim), comp) ||
                (!File.Exists(expectedShim) && string.Equals(firstNorm, KongorUserConfigStore.NormalizePathSafe(standaloneShim), comp)) ||
                (!isWindows && string.Equals(firstNorm, KongorUserConfigStore.NormalizePathSafe(standaloneShim), comp));
        }

        // On POSIX, ~/.local/bin/kongor symlinked to ~/.kongor/bin/kongor is an intentional pair, not a shadowing conflict
        bool posixIntentionalPair = !isWindows &&
            discovered.Count == 2 &&
            discovered.Any(d => string.Equals(KongorUserConfigStore.NormalizePathSafe(d), KongorUserConfigStore.NormalizePathSafe(expectedShim), comp)) &&
            discovered.Any(d => string.Equals(KongorUserConfigStore.NormalizePathSafe(d), KongorUserConfigStore.NormalizePathSafe(standaloneShim), comp));

        bool hasConflict = (!posixIntentionalPair && discovered.Count > 1) || (discovered.Count == 1 && !isExpectedFirst);
        return new PathShadowAuditResult(expectedShim, discovered, isExpectedFirst, hasConflict);
    }

    /// <summary>
    /// Prepends <paramref name="binDir"/> to a semicolon-delimited PATH string without creating duplicates.
    /// </summary>
    public static string PrependPathEntry(string? existingPath, string binDir, bool isWindows = true)
    {
        char sep = isWindows ? ';' : ':';
        StringComparison comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string normalizedTarget = KongorUserConfigStore.NormalizePath(binDir);

        List<string> parts = [normalizedTarget];
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            foreach (string raw in existingPath.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string cleaned = raw.Trim('"');
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                string? normalizedExisting = KongorUserConfigStore.TryNormalizePath(cleaned);
                if (normalizedExisting != null && string.Equals(normalizedExisting, normalizedTarget, comparison))
                {
                    continue;
                }

                parts.Add(cleaned);
            }
        }

        return string.Join(sep, parts);
    }

    private static void InstallWindowsShim(
        string binDir,
        string? shimBinSourceDir,
        string activeSlotDir,
        List<string> warnings,
        Action<string>? logger)
    {
        // Remove any stale kongor.cmd inside ~/.kongor/bin so kongor.exe always wins cleanly without cmd.exe Ctrl+C prompts
        string legacyCmd = Path.Combine(binDir, "kongor.cmd");
        if (File.Exists(legacyCmd))
        {
            try { File.Delete(legacyCmd); }
            catch (Exception ex) { warnings.Add($"Could not remove old shim '{legacyCmd}': {ex.Message}"); }
        }

        if (!string.IsNullOrWhiteSpace(shimBinSourceDir) &&
            File.Exists(Path.Combine(shimBinSourceDir, "kongor.exe")))
        {
            string[] shimFiles = ["kongor.exe", "kongor.dll", "kongor.runtimeconfig.json", "kongor.deps.json"];
            foreach (string fileName in shimFiles)
            {
                string src = Path.Combine(shimBinSourceDir, fileName);
                if (!File.Exists(src)) continue;

                string dst = Path.Combine(binDir, fileName);
                try
                {
                    if (File.Exists(dst))
                    {
                        // Rename running shim exe/dll out of the way cleanly (Windows allows renaming running images!)
                        string oldDst = dst + ".old";
                        if (File.Exists(oldDst))
                        {
                            try { File.Delete(oldDst); } catch { /* old version still in use by another terminal */ }
                        }
                        try { File.Move(dst, oldDst, overwrite: true); }
                        catch { /* overwrite attempt below */ }
                    }

                    File.Copy(src, dst, overwrite: true);
                }
                catch (Exception ex)
                {
                    // If the exact same shim is currently executing, that's fine because it reads current.txt dynamically!
                    warnings.Add($"Shim binary '{fileName}' is currently in use (continuing — active shim reads current.txt dynamically): {ex.Message}");
                    logger?.Invoke($"[CliInstall] Shim '{dst}' locked by active invocation; current.txt updated cleanly.");
                }
            }
        }
        else
        {
            // Fallback for standalone bundles where Kongor.Shim wasn't built separately: write minimal cmd forwarder
            string fallbackCmd =
                "@echo off\r\n" +
                $"\"{Path.Combine(activeSlotDir, "kongor.exe")}\" %*\r\n";
            File.WriteAllText(legacyCmd, fallbackCmd, Utf8NoBom);
            warnings.Add($"Kongor.Shim build output not supplied; wrote fallback shim '{legacyCmd}'.");
        }
    }

    private static void InstallPosixShim(
        string binDir,
        string? userHomeOverride,
        string pointerPath,
        string activeSlotDir,
        List<string> warnings,
        Action<string>? logger)
    {
        string shimPath = Path.Combine(binDir, "kongor");
        string tempPath = shimPath + ".tmp";
        string escapedPointer = pointerPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string escapedFallback = Path.Combine(activeSlotDir, "kongor").Replace("\\", "\\\\").Replace("\"", "\\\"");

        string script =
            "#!/usr/bin/env sh\n" +
            "# Auto-generated by Project Kongor Unified CLI Installer\n" +
            $"POINTER=\"{escapedPointer}\"\n" +
            "if [ -f \"$POINTER\" ]; then\n" +
            "  SLOT=\"$(head -n 1 \"$POINTER\" | tr -d '\\r\\n')\"\n" +
            "  if [ -x \"$SLOT/kongor\" ]; then\n" +
            "    exec \"$SLOT/kongor\" \"$@\"\n" +
            "  fi\n" +
            "fi\n" +
            $"exec \"{escapedFallback}\" \"$@\"\n";

        File.WriteAllText(tempPath, script, Utf8NoBom);
        TrySetExecutable(tempPath);
        File.Move(tempPath, shimPath, overwrite: true);
        TrySetExecutable(shimPath);
        TrySetExecutable(Path.Combine(activeSlotDir, "kongor"));

        string home = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localBin = Path.Combine(home, ".local", "bin");
        try
        {
            Directory.CreateDirectory(localBin);
            string localBinTarget = Path.Combine(localBin, "kongor");
            if (File.Exists(localBinTarget))
            {
                File.Delete(localBinTarget);
            }
            File.CreateSymbolicLink(localBinTarget, shimPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not symlink '{localBin}/kongor' -> '{shimPath}': {ex.Message}");
            logger?.Invoke($"[CliInstall] POSIX symlink notice: {ex.Message}");
        }
    }

    private static void PrependWindowsUserPath(string binDir, Action<string>? logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        string updatedUserPath = PrependPathEntry(currentUserPath, binDir, isWindows: true);
        if (!string.Equals(currentUserPath, updatedUserPath, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("Path", updatedUserPath, EnvironmentVariableTarget.User);
            logger?.Invoke($"[CliInstall] Prepended '{binDir}' to User PATH.");
        }

        // Also update current process PATH so chained commands immediately see it
        string currentProcPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", PrependPathEntry(currentProcPath, binDir, isWindows: true));
    }

    private static void CleanLegacyConflictingShims(
        string? userHomeOverride,
        bool isWindows,
        List<string> warnings,
        Action<string>? logger)
    {
        string home = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 1. Clean legacy %LOCALAPPDATA%\ProjectKongor\bin\kongor.cmd if present
        if (isWindows)
        {
            string localAppData = !string.IsNullOrWhiteSpace(userHomeOverride)
                ? Path.Combine(userHomeOverride, "AppData", "Local")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string legacyLocalShim = Path.Combine(localAppData, "ProjectKongor", "bin", "kongor.cmd");
            if (File.Exists(legacyLocalShim))
            {
                try
                {
                    File.Delete(legacyLocalShim);
                    logger?.Invoke($"[CliInstall] Removed legacy shim '{legacyLocalShim}'.");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Legacy shim '{legacyLocalShim}' could not be removed: {ex.Message}");
                }
            }
        }

        // 2. Clean legacy ~/.dotnet/tools/kongor.exe if present (best-effort; prepend PATH ensures ~/.kongor/bin wins regardless)
        string dotnetToolShim = Path.Combine(home, ".dotnet", "tools", isWindows ? "kongor.exe" : "kongor");
        if (File.Exists(dotnetToolShim))
        {
            if (!string.IsNullOrWhiteSpace(userHomeOverride))
            {
                // Sandboxed unit test: remove directly without invoking global dotnet tool uninstall
                try
                {
                    File.Delete(dotnetToolShim);
                    logger?.Invoke($"[CliInstall] Removed sandboxed legacy dotnet tool shim '{dotnetToolShim}'.");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not remove legacy dotnet tool shim '{dotnetToolShim}': {ex.Message}");
                }
                return;
            }

            try
            {
                ProcessStartInfo psi = new("dotnet", "tool uninstall -g Kongor.Cli")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using Process? proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                if (proc is { ExitCode: 0 })
                {
                    logger?.Invoke("[CliInstall] Uninstalled legacy global dotnet tool 'Kongor.Cli' to eliminate PATH shadowing.");
                }
                else
                {
                    warnings.Add(
                        $"Legacy global dotnet tool '{dotnetToolShim}' is still present (may be in use). " +
                        "Because '~/.kongor/bin' is prepended on User PATH, the new shim will take precedence; " +
                        "run 'dotnet tool uninstall -g Kongor.Cli' later to remove the old shim.");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Legacy dotnet tool cleanup skipped ({ex.Message}).");
            }
        }
    }

    private static int PruneOldSlots(string slotsDir, string activeSlotDir, int keepPreviousCount, Action<string>? logger)
    {
        if (!Directory.Exists(slotsDir))
        {
            return 0;
        }

        string normalizedActive = KongorUserConfigStore.NormalizePath(activeSlotDir);
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        List<DirectoryInfo> oldSlots = new DirectoryInfo(slotsDir)
            .GetDirectories()
            .Where(d => !comparer.Equals(KongorUserConfigStore.NormalizePath(d.FullName), normalizedActive))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ToList();

        int pruned = 0;
        for (int i = keepPreviousCount; i < oldSlots.Count; i++)
        {
            DirectoryInfo candidate = oldSlots[i];
            try
            {
                candidate.Delete(recursive: true);
                pruned++;
                logger?.Invoke($"[CliInstall] Pruned superseded CLI slot '{candidate.Name}'.");
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[CliInstall] Deferred pruning locked CLI slot '{candidate.Name}': {ex.Message}");
            }
        }

        return pruned;
    }

    private static HashSet<string> BuildAllowedRuntimeSubdirs(string hostRid, bool isWindows)
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

    private static (int FileCount, long TotalBytes) CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        int count = 0;
        long bytes = 0;

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
            count++;
            bytes += new FileInfo(destFile).Length;
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            (int c, long b) = CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
            count += c;
            bytes += b;
        }

        return (count, bytes);
    }

    private static string SanitizeSlotToken(string raw)
    {
        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        }
        string token = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "v5" : token;
    }

    private static void TrySetExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
        }
    }
}
