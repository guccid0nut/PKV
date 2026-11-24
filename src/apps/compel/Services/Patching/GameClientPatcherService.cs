// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GameClientPatcherService.cs
// System: COMPEL Client Patcher - Differential Delta Patching Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Security.Cryptography;
using ASPIRE.Common.Patching;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Patching;

/// <summary>
///     Status of an individual local game file relative to the CDN release manifest.
/// </summary>
public enum FilePatchStatus
{
    UpToDate,
    Outdated,
    Missing
}

/// <summary>
///     Action required to bring an individual file to the latest version.
/// </summary>
public sealed record OutdatedFileAction(
    string RelativePath,
    FilePatchStatus Status,
    string? CurrentSha256,
    string TargetSha256,
    long TargetSizeBytes,
    string? DeltaUrl);

/// <summary>
///     Comprehensive audit report of all local game files against the latest CDN release manifest.
/// </summary>
public sealed record ClientPatchAuditReport(
    string GameVersion,
    int TotalFiles,
    int UpToDateFiles,
    int OutdatedFiles,
    int MissingFiles,
    IReadOnlyList<OutdatedFileAction> Actions)
{
    public bool IsFullyPatched => OutdatedFiles == 0 && MissingFiles == 0;
}

/// <summary>
///     High-speed client game updater service.
///     Audits local game files, resolves Cloudflare CDN delta patches,
///     and applies differential patches in-place with zero downtime.
/// </summary>
public sealed class GameClientPatcherService(
    IDeltaPatchEngine deltaEngine,
    ILogger<GameClientPatcherService>? logger = null)
{
    private readonly IDeltaPatchEngine _deltaEngine = deltaEngine;
    private readonly ILogger<GameClientPatcherService>? _logger = logger;

    /// <summary>
    ///     Audits a local game client installation directory against a target release manifest.
    /// </summary>
    public async Task<ClientPatchAuditReport> AuditClientDirectoryAsync(
        string clientDirectory,
        PatchManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        List<OutdatedFileAction> actions = new();
        int upToDateCount = 0;
        int outdatedCount = 0;
        int missingCount = 0;

        foreach (PatchFileEntry file in manifest.Files)
        {
            string localFilePath = ResolveSafeClientFilePath(clientDirectory, file.RelativePath, _logger);

            if (!File.Exists(localFilePath))
            {
                missingCount++;
                actions.Add(new OutdatedFileAction(
                    RelativePath: file.RelativePath,
                    Status: FilePatchStatus.Missing,
                    CurrentSha256: null,
                    TargetSha256: file.Sha256,
                    TargetSizeBytes: file.SizeBytes,
                    DeltaUrl: file.DownloadUrl));
                continue;
            }

            byte[] localBytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
            string localSha256 = Convert.ToHexStringLower(SHA256.HashData(localBytes));

            if (string.Equals(localSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                upToDateCount++;
            }
            else
            {
                outdatedCount++;

                // Lookup matching pre-calculated delta in manifest
                PatchDeltaEntry? matchingDelta = manifest.Deltas.FirstOrDefault(d =>
                    string.Equals(d.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.SourceSha256, localSha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.TargetSha256, file.Sha256, StringComparison.OrdinalIgnoreCase));

                actions.Add(new OutdatedFileAction(
                    RelativePath: file.RelativePath,
                    Status: FilePatchStatus.Outdated,
                    CurrentSha256: localSha256,
                    TargetSha256: file.Sha256,
                    TargetSizeBytes: file.SizeBytes,
                    DeltaUrl: matchingDelta?.DeltaUrl ?? file.DownloadUrl));
            }
        }

        ClientPatchAuditReport report = new(
            GameVersion: manifest.GameVersion,
            TotalFiles: manifest.Files.Count,
            UpToDateFiles: upToDateCount,
            OutdatedFiles: outdatedCount,
            MissingFiles: missingCount,
            Actions: actions);

        _logger?.LogInformation(
            "[PATCH_AUDIT] Audited {Total} files for v{Version}. UpToDate: {UpToDate}, Outdated: {Outdated}, Missing: {Missing}",
            report.TotalFiles, report.GameVersion, report.UpToDateFiles, report.OutdatedFiles, report.MissingFiles);

        return report;
    }

    /// <summary>
    ///     Applies a differential binary delta patch stream to an outdated local file in-place.
    /// </summary>
    public async Task ApplyPatchToFileAsync(
        string clientDirectory,
        OutdatedFileAction action,
        Stream patchStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientDirectory);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(patchStream);

        string localFilePath = ResolveSafeClientFilePath(clientDirectory, action.RelativePath, _logger);

        string? parentDirectory = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(parentDirectory) && !Directory.Exists(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        _logger?.LogInformation(
            "[PATCH_APPLY] Applying delta patch to {File} (Target SHA: {TargetSha})",
            action.RelativePath, action.TargetSha256);

        await _deltaEngine.ApplyDeltaToFileAsync(
            sourceFilePath: localFilePath,
            deltaStream: patchStream,
            targetFilePath: localFilePath,
            expectedSha256: action.TargetSha256,
            cancellationToken: cancellationToken);

        _logger?.LogInformation(
            "[PATCH_APPLY] Successfully patched {File} in-place with 100% SHA-256 parity.",
            action.RelativePath);
    }

    /// <summary>
    ///     Validates and canonicalizes a manifest relative path so it cannot escape <paramref name="clientDirectory" /> via <c>..</c> or rooted paths.
    /// </summary>
    public static string ResolveSafeClientFilePath(
        string clientDirectory,
        string relativePath,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0'))
        {
            logger?.LogError("[SECURITY-ALERT] Rejected empty or NUL-containing patch manifest RelativePath.");
            throw new InvalidOperationException(
                "[SECURITY-ALERT] Patch manifest RelativePath cannot be empty or contain NUL characters.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            logger?.LogError(
                "[SECURITY-ALERT] Blocked rooted/drive-prefixed patch manifest RelativePath '{RelativePath}' against '{ClientDir}'.",
                relativePath, clientDirectory);
            throw new InvalidOperationException(
                $"[SECURITY-ALERT] Patch manifest entry '{relativePath}' is rooted or contains a drive specifier and was blocked.");
        }

        string normalizedRoot = Path.GetFullPath(clientDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        string normalizedRelative = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        string candidateFullPath = Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelative));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidateFullPath.StartsWith(normalizedRoot, comparison))
        {
            logger?.LogError(
                "[SECURITY-ALERT] Blocked ZipSlip/path-traversal patch manifest entry '{RelativePath}' resolving to '{Candidate}' outside '{Root}'.",
                relativePath, candidateFullPath, normalizedRoot);
            throw new InvalidOperationException(
                $"[SECURITY-ALERT] Patch manifest entry '{relativePath}' escapes client root '{normalizedRoot}'.");
        }

        return candidateFullPath;
    }
}
