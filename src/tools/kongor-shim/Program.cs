// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;

namespace Kongor.Shim;

internal static class Program
{
    public static int Main(string[] args)
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
        {
            userHome = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        string cliDir = Path.Combine(userHome, ".kongor", "cli");
        string currentPointerFile = Path.Combine(cliDir, "current.txt");
        string exeName = OperatingSystem.IsWindows() ? "kongor.exe" : "kongor";

        string? targetExe = ResolveTargetExecutable(cliDir, currentPointerFile, exeName);
        if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
        {
            Console.Error.WriteLine(
                $"[kongor-shim] ERROR: Active Kongor CLI binary was not found (pointer: '{currentPointerFile}').\n" +
                "Fix: Re-run 'powershell -ExecutionPolicy Bypass -File scripts/install-cli.ps1' from your ProjectKongor checkout, " +
                "or run 'kongor install' from your bundle folder.");
            return 1;
        }

        ProcessStartInfo psi = new()
        {
            FileName = targetExe,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            psi.Environment["KONGOR_SHIM_PATH"] = Environment.ProcessPath;
        }
        psi.Environment["KONGOR_SHIM_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        ConsoleCancelEventHandler cancelHandler = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using Process? child = Process.Start(psi);
            if (child is null)
            {
                Console.Error.WriteLine($"[kongor-shim] ERROR: Failed to spawn '{targetExe}'.");
                return 1;
            }

            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[kongor-shim] ERROR: Exception launching '{targetExe}': {ex.Message}\n" +
                "Fix: Run 'scripts/install-cli.ps1' to repair the global Kongor CLI installation.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static string? ResolveTargetExecutable(string cliDir, string currentPointerFile, string exeName)
    {
        string? envSlot = Environment.GetEnvironmentVariable("KONGOR_CLI_TARGET");
        string? fromEnv = NormalizeCandidate(envSlot, exeName);
        if (fromEnv != null)
        {
            return fromEnv;
        }

        if (File.Exists(currentPointerFile))
        {
            try
            {
                string raw = File.ReadAllText(currentPointerFile).Trim();
                string? fromPointer = NormalizeCandidate(raw, exeName);
                if (fromPointer != null)
                {
                    return fromPointer;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kongor-shim] WARN: Failed reading '{currentPointerFile}': {ex.Message}");
            }
        }

        if (Directory.Exists(cliDir))
        {
            try
            {
                string? newest = Directory.GetDirectories(cliDir)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(d => Path.Combine(d, exeName))
                    .FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(newest))
                {
                    return newest;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kongor-shim] WARN: Failed scanning fallback CLI slots in '{cliDir}': {ex.Message}");
            }
        }

        return null;
    }

    private static string? NormalizeCandidate(string? raw, string exeName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim().Trim('"');
        if (File.Exists(trimmed))
        {
            return trimmed;
        }

        string combined = Path.Combine(trimmed, exeName);
        if (File.Exists(combined))
        {
            return combined;
        }

        return null;
    }
}
