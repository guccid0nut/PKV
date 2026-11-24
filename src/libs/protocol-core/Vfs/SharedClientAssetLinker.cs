// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Kongor.Core.Vfs;

public readonly record struct AssetLinkSummary(int LinkedDirs, int LinkedFiles, int CopiedSmallFiles);

/// <summary>
/// Unprivileged NTFS junction and hardlink manager for sharing client VFS tiers and archives with
/// isolated K2_SERVER companion directories (0 MB duplication, .zip-extraction safe).
/// </summary>
public static class SharedClientAssetLinker
{
    private static readonly HashSet<string> ExcludedTopLevelDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "game",
        "logs",
        "crashdumps",
        "userdata",
        "replays",
        "shadercache",
        "videos",
        "music"
    };

    private static readonly HashSet<string> ExcludedGameSubdirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "replays",
        "shadercache",
        "videos",
        "music"
    };

    private static readonly HashSet<string> ExcludedGameFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "textures.s2z",
        "game_settings.cfg",
        "startup.cfg"
    };

    private static readonly HashSet<string> ExcludedGameFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".exe",
        ".pdb",
        ".log",
        ".honreplay",
        ".so",
        ".dylib"
    };

    /// <summary>
    /// Emits a diagnostic log message. Calls <paramref name="log"/> if provided, otherwise falls back
    /// to <see cref="Trace.WriteLine(string)"/>. Never writes to Console.WriteLine (stdout must remain pure JSON for kongor-render).
    /// </summary>
    private static void EmitLog(Action<string>? log, string message)
    {
        if (log is not null)
        {
            log(message);
        }
        else
        {
            Trace.WriteLine(message);
        }
    }

    /// <summary>
    /// Creates an unprivileged directory junction on Windows (<c>mklink /J</c>) or a directory symbolic link on POSIX.
    /// Idempotent: returns <c>true</c> immediately if <paramref name="linkPath"/> already exists as a directory.
    /// </summary>
    public static bool TryCreateDirectoryJunction(string linkPath, string targetPath, Action<string>? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath))
            {
                EmitLog(log, $"[AssetLinker] TryCreateDirectoryJunction invalid arguments: linkPath='{linkPath}', targetPath='{targetPath}'");
                return false;
            }

            string fullLinkPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(linkPath));
            string fullTargetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));

            if (!Directory.Exists(fullTargetPath))
            {
                EmitLog(log, $"[AssetLinker] TryCreateDirectoryJunction target directory does not exist: '{fullTargetPath}'");
                return false;
            }

            if (Directory.Exists(fullLinkPath))
            {
                return true;
            }

            if (File.Exists(fullLinkPath))
            {
                EmitLog(log, $"[AssetLinker] Cannot create directory junction at '{fullLinkPath}': file exists with that name.");
                return false;
            }

            string? parent = Path.GetDirectoryName(fullLinkPath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (OperatingSystem.IsWindows())
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{fullLinkPath}\" \"{fullTargetPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using Process? proc = Process.Start(psi);
                if (proc is not null)
                {
                    if (proc.WaitForExit(10000))
                    {
                        if (proc.ExitCode == 0 && Directory.Exists(fullLinkPath))
                        {
                            return true;
                        }

                        string stderr = proc.StandardError.ReadToEnd();
                        EmitLog(log, $"[AssetLinker] mklink /J exited with code {proc.ExitCode}: {stderr.Trim()}");
                    }
                    else
                    {
                        try
                        {
                            proc.Kill();
                        }
                        catch (Exception killEx)
                        {
                            EmitLog(log, $"[AssetLinker] Warning: failed to kill timed-out mklink process: {killEx.Message}");
                        }

                        EmitLog(log, $"[AssetLinker] mklink /J timed out after 10000 ms for '{fullLinkPath}' -> '{fullTargetPath}'");
                    }
                }

                try
                {
                    Directory.CreateSymbolicLink(fullLinkPath, fullTargetPath);
                    return Directory.Exists(fullLinkPath);
                }
                catch (Exception symEx)
                {
                    EmitLog(log, $"[AssetLinker] Directory.CreateSymbolicLink fallback failed ('{fullLinkPath}' -> '{fullTargetPath}'): {symEx.Message}");
                    return false;
                }
            }
            else
            {
                Directory.CreateSymbolicLink(fullLinkPath, fullTargetPath);
                return Directory.Exists(fullLinkPath);
            }
        }
        catch (Exception ex)
        {
            EmitLog(log, $"[AssetLinker] TryCreateDirectoryJunction failed ('{linkPath}' -> '{targetPath}'): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Links shared read-only client VFS tiers and .s2z archives into serverDir using unprivileged
    /// NTFS junctions and hardlinks (0 MB archive duplication, .zip-extraction safe) while keeping
    /// serverDir's K2_SERVER binaries strictly isolated.
    /// </summary>
    public static AssetLinkSummary EnsureLinked(string serverDir, string clientDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(serverDir) || string.IsNullOrWhiteSpace(clientDir))
        {
            EmitLog(log, $"[AssetLinker] skipped (serverDir='{serverDir}', clientDir='{clientDir}')");
            return default;
        }

        string fullServerDir;
        string fullClientDir;
        try
        {
            fullServerDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDir));
            fullClientDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientDir));
        }
        catch (Exception ex)
        {
            EmitLog(log, $"[AssetLinker] skipped (serverDir='{serverDir}', clientDir='{clientDir}'): {ex.Message}");
            return default;
        }

        if (!Directory.Exists(fullClientDir) || !Directory.Exists(fullServerDir) || string.Equals(fullServerDir, fullClientDir, StringComparison.OrdinalIgnoreCase))
        {
            EmitLog(log, $"[AssetLinker] skipped (serverDir='{fullServerDir}', clientDir='{fullClientDir}')");
            return default;
        }

        int linkedDirs = 0;
        int linkedFiles = 0;
        int copiedSmallFiles = 0;

        // a) Top-level directories in clientDir
        try
        {
            foreach (string srcDirPath in Directory.GetDirectories(fullClientDir))
            {
                string dirName = Path.GetFileName(srcDirPath);
                if (string.IsNullOrWhiteSpace(dirName))
                {
                    continue;
                }

                if (ExcludedTopLevelDirs.Contains(dirName) || dirName.StartsWith("000000", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destDirPath = Path.Combine(fullServerDir, dirName);
                if (!Directory.Exists(destDirPath))
                {
                    if (TryCreateDirectoryJunction(destDirPath, srcDirPath, log))
                    {
                        linkedDirs++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            EmitLog(log, $"[AssetLinker] Top-level directory scan exception for '{fullClientDir}': {ex.Message}");
        }

        // b) Sibling shared directory
        try
        {
            string clientSharedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(fullClientDir, "..", "shared")));
            if (Directory.Exists(clientSharedDir))
            {
                DirectoryInfo? serverParent = Directory.GetParent(fullServerDir);
                DirectoryInfo? clientParent = Directory.GetParent(fullClientDir);
                if (serverParent is not null)
                {
                    bool parentsDiffer = clientParent is null || !string.Equals(serverParent.FullName, clientParent.FullName, StringComparison.OrdinalIgnoreCase);
                    string serverSharedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(serverParent.FullName, "shared")));
                    if (parentsDiffer && !Directory.Exists(serverSharedDir))
                    {
                        if (TryCreateDirectoryJunction(serverSharedDir, clientSharedDir, log))
                        {
                            linkedDirs++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            EmitLog(log, $"[AssetLinker] Sibling shared directory check exception: {ex.Message}");
        }

        // c) client/game directory
        try
        {
            string serverGameDir = Path.Combine(fullServerDir, "game");
            Directory.CreateDirectory(serverGameDir);

            string clientGameDir = Path.Combine(fullClientDir, "game");
            if (Directory.Exists(clientGameDir))
            {
                foreach (string srcSubDirPath in Directory.GetDirectories(clientGameDir))
                {
                    string subDirName = Path.GetFileName(srcSubDirPath);
                    if (string.IsNullOrWhiteSpace(subDirName))
                    {
                        continue;
                    }

                    if (ExcludedGameSubdirs.Contains(subDirName) || subDirName.StartsWith("000000", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string destSubDirPath = Path.Combine(serverGameDir, subDirName);
                    if (!Directory.Exists(destSubDirPath))
                    {
                        if (TryCreateDirectoryJunction(destSubDirPath, srcSubDirPath, log))
                        {
                            linkedDirs++;
                        }
                    }
                }

                foreach (string srcFilePath in Directory.GetFiles(clientGameDir))
                {
                    string fileName = Path.GetFileName(srcFilePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    string destFilePath = Path.Combine(serverGameDir, fileName);
                    if (File.Exists(destFilePath))
                    {
                        continue;
                    }

                    if (ExcludedGameFileNames.Contains(fileName) || fileName.Contains(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string ext = Path.GetExtension(fileName);
                    if (ExcludedGameFileExtensions.Contains(ext))
                    {
                        continue;
                    }

                    if (TryCreateHardLinkOrSmallCopy(destFilePath, srcFilePath, log, out bool wasCopied))
                    {
                        if (wasCopied)
                        {
                            copiedSmallFiles++;
                        }
                        else
                        {
                            linkedFiles++;
                        }
                    }
                    else
                    {
                        EmitLog(log, $"[AssetLinker] Warning: could not link shared game asset '{fileName}' into '{serverGameDir}'.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            EmitLog(log, $"[AssetLinker] Game directory scan exception: {ex.Message}");
        }

        EmitLog(log, $"[AssetLinker] linked {linkedDirs} dirs, {linkedFiles} files, copied {copiedSmallFiles} small files");
        return new AssetLinkSummary(linkedDirs, linkedFiles, copiedSmallFiles);
    }

    private static bool TryCreateHardLinkOrSmallCopy(
        string destFile,
        string srcFile,
        Action<string>? log,
        out bool wasCopied)
    {
        wasCopied = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLinkW(destFile, srcFile, IntPtr.Zero))
                {
                    wasCopied = false;
                    return true;
                }

                int lastError = Marshal.GetLastWin32Error();
                EmitLog(log, $"[AssetLinker] CreateHardLinkW failed for '{destFile}' <- '{srcFile}' (Win32 error: {lastError}), attempting fallback.");
            }
            else
            {
                try
                {
                    File.CreateSymbolicLink(destFile, srcFile);
                    if (File.Exists(destFile))
                    {
                        wasCopied = false;
                        return true;
                    }
                }
                catch (Exception symEx)
                {
                    EmitLog(log, $"[AssetLinker] POSIX File.CreateSymbolicLink notice for '{destFile}' <- '{srcFile}': {symEx.Message}");
                }
            }

            FileInfo fi = new FileInfo(srcFile);
            if (fi.Exists && fi.Length <= 64L * 1024L * 1024L)
            {
                File.Copy(srcFile, destFile, overwrite: false);
                wasCopied = true;
                return true;
            }

            try
            {
                File.CreateSymbolicLink(destFile, srcFile);
                if (File.Exists(destFile))
                {
                    wasCopied = false;
                    return true;
                }
            }
            catch (Exception symFallbackEx)
            {
                EmitLog(log, $"[AssetLinker] Symbolic link fallback failed for '{destFile}' <- '{srcFile}': {symFallbackEx.Message}");
            }

            EmitLog(log, $"[AssetLinker] Failed to link or copy asset '{srcFile}' to '{destFile}'.");
            return false;
        }
        catch (Exception ex)
        {
            EmitLog(log, $"[AssetLinker] TryCreateHardLinkOrSmallCopy exception for '{destFile}' <- '{srcFile}': {ex.Message}");
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
