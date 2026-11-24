// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using ASPIRE.Common.Services.Supabase;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Features.Storage;

/// <summary>
///     MediatR query handler for retrieving cloud configuration archives (<see cref="RetrieveCloudConfigQuery"/>).
///     Downloads from Supabase Storage with local filesystem fallback and empty archive generation fallback.
/// </summary>
public sealed class RetrieveCloudConfigHandler(
    IAccountRepository accountRepository,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    SupabaseStorageClient? supabaseStorageClient = null,
    ILogger<RetrieveCloudConfigHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<RetrieveCloudConfigQuery, RetrieveCloudConfigResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RetrieveCloudConfigResultDto> Handle(
        RetrieveCloudConfigQuery request,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[{Timestamp}] [RetrieveCloudConfigHandler] Retrieving cloud config for Account ID: {AccountId}, Bucket: {Bucket}",
            _timeProvider.GetUtcNow(),
            request.AccountId,
            request.Bucket);

        byte[]? zipBytes = null;

        // 1. Attempt to download from Supabase Storage 'configs' bucket
        if (supabaseStorageClient != null)
        {
            try
            {
                List<string> existingFiles = await supabaseStorageClient.ListFilesAsync(
                    SupabaseStorageClient.ConfigsBucket,
                    $"{request.AccountId}",
                    cancellationToken);

                List<string> candidatePaths = [];
                foreach (string f in existingFiles)
                    if (!string.IsNullOrWhiteSpace(f))
                        candidatePaths.Add($"{request.AccountId}/{f}");

                candidatePaths.Add($"{request.AccountId}/cloud.zip");
                candidatePaths.Add($"{request.AccountId}/startup.cfg");
                candidatePaths.Add($"{request.AccountId}/config.zip");
                candidatePaths.Add($"{request.AccountId}/{request.AccountId}.zip");
                candidatePaths.Add($"{request.AccountId}/game/startup.cfg");
                candidatePaths.Add($"{request.AccountId}/configuration/startup.cfg");

                foreach (string candidate in candidatePaths.Distinct())
                {
                    Stream? candidateStream = await supabaseStorageClient.DownloadFileAsync(
                        SupabaseStorageClient.ConfigsBucket,
                        candidate,
                        cancellationToken);

                    if (candidateStream != null && candidateStream.Length > 0)
                    {
                        if (candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            using MemoryStream ms = new();
                            candidateStream.Position = 0;
                            await candidateStream.CopyToAsync(ms, cancellationToken);
                            zipBytes = ms.ToArray();
                            break;
                        }

                        // Convert single text config file to a valid zip archive in-memory containing cloud.cfg (expected by K2 RetrieveCloudConfig)
                        using MemoryStream zipStream = new();
                        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create, true))
                        {
                            ZipArchiveEntry cloudEntry = archive.CreateEntry("cloud.cfg");
                            using (Stream cloudStream = cloudEntry.Open())
                            {
                                candidateStream.Position = 0;
                                await candidateStream.CopyToAsync(cloudStream, cancellationToken);
                            }

                            ZipArchiveEntry legacyEntry = archive.CreateEntry("startup.cfg");
                            using Stream entryStream = legacyEntry.Open();
                            candidateStream.Position = 0;
                            await candidateStream.CopyToAsync(entryStream, cancellationToken);
                        }
                        zipBytes = zipStream.ToArray();
                        break;
                    }

                    candidateStream?.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "[{Timestamp}] [RetrieveCloudConfigHandler] Supabase download failed for Account ID: {AccountId}, falling back to local storage",
                    _timeProvider.GetUtcNow(),
                    request.AccountId);
            }
        }

        // 2. Fallback to local storage
        if (zipBytes == null)
        {
            string relativePath = configuration["Storage:CloudPath"] ?? "App_Data/Cloud";
            string storageRoot = Path.Combine(environment.ContentRootPath, relativePath);
            string accountStoragePath = Path.Combine(storageRoot, request.AccountId.ToString());
            string filePath = Path.Combine(accountStoragePath, "cloud.zip");

            if (File.Exists(filePath))
            {
                try
                {
                    zipBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "[{Timestamp}] [RetrieveCloudConfigHandler] Failed to read local fallback cloud storage file for Account ID: {AccountId}",
                        _timeProvider.GetUtcNow(),
                        request.AccountId);
                }
            }
        }

        // 3. Fallback to empty zip
        if (zipBytes == null)
        {
            logger?.LogInformation(
                "[{Timestamp}] [RetrieveCloudConfigHandler] No existing cloud backup found for Account ID: {AccountId}, generating empty zip archive",
                _timeProvider.GetUtcNow(),
                request.AccountId);

            using MemoryStream emptyZipStream = new();
            using (ZipArchive archive = new(emptyZipStream, ZipArchiveMode.Create, true))
            {
                // Empty archive
            }
            zipBytes = emptyZipStream.ToArray();
        }

        // Downloading cloud config is a pure read operation and must NOT overwrite BackupLastUpdatedTime with server UTC.
        _ = accountRepository;
        logger?.LogInformation(
            "[{Timestamp}] [RetrieveCloudConfigHandler] Returning cloud.zip ({Bytes} bytes) for Account ID: {AccountId} without mutating BackupLastUpdatedTime",
            _timeProvider.GetUtcNow(),
            zipBytes.Length,
            request.AccountId);

        return new RetrieveCloudConfigResultDto(true, zipBytes, "application/octet-stream", "cloud.zip");
    }
}
