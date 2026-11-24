// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Serialization;

namespace KONGOR.MasterServer.Services.Replay;

public sealed class ReplayOfflineBufferService : IReplayOfflineBufferService
{
    private readonly ILogger<ReplayOfflineBufferService> _logger;

    public ReplayOfflineBufferService(
        IWebHostEnvironment environment,
        IOptions<ReplayStorageSettings> options,
        ILogger<ReplayOfflineBufferService> logger)
    {
        _logger = logger;
        string configuredUploadDir = options.Value.LocalDisk.UploadDirectory;
        string baseDir = !string.IsNullOrWhiteSpace(configuredUploadDir)
            ? configuredUploadDir
            : Path.Combine(environment.ContentRootPath, "replays");

        BufferDirectory = Path.Combine(baseDir, "pending");

        if (!Directory.Exists(BufferDirectory)) Directory.CreateDirectory(BufferDirectory);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[ReplayOfflineBuffer] Initialized replay offline buffer directory at {Directory}",
                BufferDirectory);
    }

    public string BufferDirectory { get; }

    public async Task<string> EnqueuePendingReplayAsync(
        int matchId,
        string fileName,
        Stream stream,
        string? error,
        CancellationToken cancellationToken = default)
    {
        string targetFilePath = Path.Combine(BufferDirectory, fileName);
        string metaFilePath = targetFilePath + ".meta.json";

        if (stream.CanSeek) stream.Position = 0;

        await using (FileStream fileStream = new(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.CopyToAsync(fileStream, cancellationToken);
        }

        FileInfo fileInfo = new(targetFilePath);

        PendingReplayMetadata metadata = new()
        {
            MatchId = matchId,
            FileName = fileName,
            FirstAttemptUtc = TimeProvider.System.GetUtcNow(),
            LastAttemptUtc = TimeProvider.System.GetUtcNow(),
            AttemptCount = 1,
            LastError = error,
            FileSizeBytes = fileInfo.Length
        };

        string metaJson = JsonSerializer.Serialize(metadata, MasterServerJsonContext.Default.PendingReplayMetadata);
        await File.WriteAllTextAsync(metaFilePath, metaJson, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "[ReplayOfflineBuffer] Buffered failed replay for Match #{MatchId} ({FileName}, {Bytes} bytes) to {Path}. Reason: {Error}",
                matchId, fileName, fileInfo.Length, targetFilePath, error ?? "Unknown");

        return targetFilePath;
    }

    public async Task<string> EnqueuePendingReplayFromDiskAsync(
        int matchId,
        string fileName,
        string sourceFilePath,
        string? error,
        CancellationToken cancellationToken = default)
    {
        string targetFilePath = Path.Combine(BufferDirectory, fileName);
        string metaFilePath = targetFilePath + ".meta.json";

        if (!File.Exists(targetFilePath) && File.Exists(sourceFilePath))
            File.Copy(sourceFilePath, targetFilePath, true);

        FileInfo fileInfo = new(File.Exists(targetFilePath) ? targetFilePath : sourceFilePath);

        PendingReplayMetadata metadata = new()
        {
            MatchId = matchId,
            FileName = fileName,
            FirstAttemptUtc = TimeProvider.System.GetUtcNow(),
            LastAttemptUtc = TimeProvider.System.GetUtcNow(),
            AttemptCount = 1,
            LastError = error,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0
        };

        string metaJson = JsonSerializer.Serialize(metadata, MasterServerJsonContext.Default.PendingReplayMetadata);
        await File.WriteAllTextAsync(metaFilePath, metaJson, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "[ReplayOfflineBuffer] Buffered failed replay for Match #{MatchId} from disk ({FileName}, {Bytes} bytes) to {Path}. Reason: {Error}",
                matchId, fileName, metadata.FileSizeBytes, targetFilePath, error ?? "Unknown");

        return targetFilePath;
    }

    public async Task<List<PendingReplayMetadata>> GetPendingReplaysAsync(CancellationToken cancellationToken = default)
    {
        List<PendingReplayMetadata> results = [];

        if (!Directory.Exists(BufferDirectory)) return results;

        string[] metaFiles = Directory.GetFiles(BufferDirectory, "*.meta.json");

        foreach (string metaFile in metaFiles)
            try
            {
                string json = await File.ReadAllTextAsync(metaFile, cancellationToken);
                PendingReplayMetadata? metadata = JsonSerializer.Deserialize(json, MasterServerJsonContext.Default.PendingReplayMetadata);

                if (metadata != null)
                {
                    string replayPath = Path.Combine(BufferDirectory, metadata.FileName);
                    if (File.Exists(replayPath))
                        results.Add(metadata);
                    else
                        // Clean up orphaned metadata file
                        File.Delete(metaFile);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "[ReplayOfflineBuffer] Error reading pending replay metadata file {Path}",
                        metaFile);
            }

        return results;
    }

    public Task MarkReplayCompletedAsync(string fileName, CancellationToken cancellationToken = default)
    {
        string targetFilePath = Path.Combine(BufferDirectory, fileName);
        string metaFilePath = targetFilePath + ".meta.json";

        try
        {
            if (File.Exists(targetFilePath)) File.Delete(targetFilePath);

            if (File.Exists(metaFilePath)) File.Delete(metaFilePath);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[ReplayOfflineBuffer] Removed completed replay {FileName} from offline buffer queue.", fileName);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "[ReplayOfflineBuffer] Error removing completed replay {FileName} from buffer.",
                    fileName);
        }

        return Task.CompletedTask;
    }

    public async Task UpdatePendingAttemptAsync(string fileName, string error,
        CancellationToken cancellationToken = default)
    {
        string targetFilePath = Path.Combine(BufferDirectory, fileName);
        string metaFilePath = targetFilePath + ".meta.json";

        try
        {
            PendingReplayMetadata? metadata = null;
            if (File.Exists(metaFilePath))
            {
                string json = await File.ReadAllTextAsync(metaFilePath, cancellationToken);
                metadata = JsonSerializer.Deserialize(json, MasterServerJsonContext.Default.PendingReplayMetadata);
            }

            if (metadata != null)
            {
                metadata.AttemptCount++;
                metadata.LastAttemptUtc = TimeProvider.System.GetUtcNow();
                metadata.LastError = error;

                string updatedJson = JsonSerializer.Serialize(metadata, MasterServerJsonContext.Default.PendingReplayMetadata);
                await File.WriteAllTextAsync(metaFilePath, updatedJson, cancellationToken);

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[ReplayOfflineBuffer] Updated retry state for {FileName}: Attempt #{Attempt}, LastError: {Error}",
                        fileName, metadata.AttemptCount, error);
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "[ReplayOfflineBuffer] Error updating attempt count for {FileName}", fileName);
        }
    }

    public Stream? OpenPendingReplayStream(string fileName)
    {
        string targetFilePath = Path.Combine(BufferDirectory, fileName);
        if (File.Exists(targetFilePath))
            return new FileStream(targetFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        return null;
    }
}
