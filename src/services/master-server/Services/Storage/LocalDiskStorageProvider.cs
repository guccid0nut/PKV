// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Storage;

public class LocalDiskStorageProvider(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IOptions<ReplayStorageSettings> options,
    ILogger<LocalDiskStorageProvider>? logger = null) : IReplayStorageProvider
{
    private readonly ReplayStorageSettings _settings = options.Value;

    public async Task<string> UploadReplayAsync(string matchId, Stream fileStream, string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            logger?.LogError(
                "[LocalDiskStorageProvider] Blocked empty replay filename upload attempt for Match '{MatchId}'.",
                matchId);
            throw new ArgumentException("Replay file name must not be empty.", nameof(fileName));
        }

        string trimmedFileName = fileName.Trim();
        string safeFileName = Path.GetFileName(trimmedFileName);

        bool hasPathSeparatorsOrTraversal =
            trimmedFileName.Contains('/', StringComparison.Ordinal) ||
            trimmedFileName.Contains('\\', StringComparison.Ordinal) ||
            trimmedFileName.Contains("..", StringComparison.Ordinal) ||
            !string.Equals(safeFileName, trimmedFileName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(safeFileName) ||
            safeFileName is "." or ".." ||
            safeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

        bool hasAllowedReplayExtension =
            safeFileName.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase) ||
            safeFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        if (hasPathSeparatorsOrTraversal || !hasAllowedReplayExtension)
        {
            logger?.LogError(
                "[LocalDiskStorageProvider] Blocked path traversal or disallowed replay filename '{FileName}' for Match '{MatchId}'.",
                fileName, matchId);
            throw new ArgumentException(
                "Invalid or disallowed replay file name. Only basename '.honreplay' or '.zip' files are permitted.",
                nameof(fileName));
        }

        string uploadDir = !string.IsNullOrEmpty(_settings.LocalDisk.UploadDirectory)
            ? _settings.LocalDisk.UploadDirectory
            : Path.Combine(environment.ContentRootPath, "Resources", "Replays");

        string fullUploadDir = Path.GetFullPath(uploadDir);
        if (!Directory.Exists(fullUploadDir)) Directory.CreateDirectory(fullUploadDir);

        string normalizedRoot =
            fullUploadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullFilePath = Path.GetFullPath(Path.Combine(fullUploadDir, safeFileName));

        if (!fullFilePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogError(
                "[LocalDiskStorageProvider] Blocked canonicalized root escape '{FullFilePath}' outside '{NormalizedRoot}' for Match '{MatchId}'.",
                fullFilePath, normalizedRoot, matchId);
            throw new ArgumentException("Resolved replay path escapes configured upload directory.", nameof(fileName));
        }

        bool isSameFile = fileStream is FileStream sourceFs &&
                          string.Equals(Path.GetFullPath(sourceFs.Name), fullFilePath,
                              StringComparison.OrdinalIgnoreCase);
        if (!isSameFile)
            await using (FileStream stream = new(fullFilePath, FileMode.Create))
            {
                await fileStream.CopyToAsync(stream, cancellationToken);
            }

        string domain = _settings.Domain;
        if (string.IsNullOrWhiteSpace(domain) || domain == "http://newbs.io")
            domain = configuration["ReplayStorage:Domain"] ?? configuration["Operational:Domain"] ??
                configuration["MASTER_SERVER_ADDRESS"] ?? configuration["INFRASTRUCTURE_GATEWAY"] ?? "http://newbs.io";

        if (!domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            domain = "http://" + domain;

        return $"{domain.TrimEnd('/')}/replays/{safeFileName}";
    }
}
