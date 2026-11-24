// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker service facade for PE Import Address Table (IAT) inspection,
///     safety verification, and DLL module redirection across native game client binaries.
/// </summary>
public static class PeImportAddressTableService
{
    /// <summary>
    ///     Inspects imports of an engine binary located in the specified game directory.
    /// </summary>
    public static PeImportReport InspectClientBinaryImports(string gameDirectory, string relativeBinaryPath)
    {
        string fullPath = Path.Combine(gameDirectory, relativeBinaryPath);
        return Kongor.Core.Services.Native.PeImportAddressTableService.GetImports(fullPath);
    }

    /// <summary>
    ///     Redirects an imported DLL name in a target binary within the game directory,
    ///     maintaining verified two-tier backups (.bak and .bak.old).
    /// </summary>
    public static PeRedirectionResult RedirectClientBinaryImport(
        string gameDirectory,
        string relativeBinaryPath,
        string oldDllName,
        string newDllName,
        Action<string>? logAction = null)
    {
        string fullPath = Path.Combine(gameDirectory, relativeBinaryPath);

        if (!File.Exists(fullPath))
        {
            string msg = $"Target binary not found: {fullPath}";
            logAction?.Invoke($"[PE_IAT] {msg}");
            return new PeRedirectionResult(false, msg, fullPath, oldDllName, newDllName, false);
        }

        try
        {
            PeRedirectionResult result = Kongor.Core.Services.Native.PeImportAddressTableService.RedirectImportedDll(
                fullPath,
                oldDllName,
                newDllName,
                createBackup: true,
                skipProcessCheck: false);

            if (result.Success)
            {
                logAction?.Invoke(
                    $"[PE_IAT] Successfully redirected '{oldDllName}' -> '{newDllName}' in '{relativeBinaryPath}' (Cavity slack: {result.UsedCavitySlack}).");
            }
            else
            {
                logAction?.Invoke($"[PE_IAT] Redirection failed for '{relativeBinaryPath}': {result.Message}");
            }

            return result;
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[PE_IAT] Redirection exception for '{relativeBinaryPath}': {ex.Message}");
            return new PeRedirectionResult(false, ex.Message, fullPath, oldDllName, newDllName, false);
        }
    }

    /// <summary>
    ///     Directly parses the PE Import Table and returns an inspection report.
    /// </summary>
    public static PeImportReport GetImports(string filePath)
        => Kongor.Core.Services.Native.PeImportAddressTableService.GetImports(filePath);

    /// <summary>
    ///     Directly redirects an imported DLL within a target PE binary.
    /// </summary>
    public static PeRedirectionResult RedirectImportedDll(
        string targetFilePath,
        string oldDllName,
        string newDllName,
        bool createBackup = true,
        bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.PeImportAddressTableService.RedirectImportedDll(
            targetFilePath, oldDllName, newDllName, createBackup, skipProcessCheck);

    /// <summary>
    ///     Directly redirects multiple imported DLLs within a target PE binary in a single atomic pass.
    /// </summary>
    public static PeBatchRedirectionResult RedirectImportedDlls(
        string targetFilePath,
        IReadOnlyDictionary<string, string> redirections,
        bool createBackup = true,
        bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.PeImportAddressTableService.RedirectImportedDlls(
            targetFilePath, redirections, createBackup, skipProcessCheck);
}
