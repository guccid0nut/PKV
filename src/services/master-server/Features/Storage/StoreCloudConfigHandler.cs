// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Services.Supabase;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Features.Storage;

/// <summary>
///     MediatR command handler for storing cloud configuration archives (<see cref="StoreCloudConfigCommand"/>).
///     Persists configuration bytes to Supabase Storage with local filesystem fallback, then records metadata in database.
/// </summary>
public sealed class StoreCloudConfigHandler(
    IAccountRepository accountRepository,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    SupabaseStorageClient? supabaseStorageClient = null,
    ILogger<StoreCloudConfigHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<StoreCloudConfigCommand, StoreCloudConfigResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<StoreCloudConfigResultDto> Handle(
        StoreCloudConfigCommand request,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[{Timestamp}] [StoreCloudConfigHandler] Storing cloud config for Account ID: {AccountId}, Bucket: {Bucket}, FileSize: {FileSize} bytes",
            _timeProvider.GetUtcNow(),
            request.AccountId,
            request.Bucket,
            request.FileBytes.Length);

        // 1. Upload to Supabase Storage in 'configs' bucket: {account.ID}/cloud.zip
        if (supabaseStorageClient != null)
        {
            try
            {
                using MemoryStream uploadStream = new(request.FileBytes);
                await supabaseStorageClient.UploadFileAsync(
                    SupabaseStorageClient.ConfigsBucket,
                    $"{request.AccountId}/cloud.zip",
                    uploadStream,
                    "application/zip",
                    true,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "[{Timestamp}] [StoreCloudConfigHandler] Supabase upload failed for Account ID: {AccountId}, continuing with local storage",
                    _timeProvider.GetUtcNow(),
                    request.AccountId);
            }
        }

        // 2. Also save to local disk fallback
        string relativePath = configuration["Storage:CloudPath"] ?? "App_Data/Cloud";
        string storageRoot = Path.Combine(environment.ContentRootPath, relativePath);
        string accountStoragePath = Path.Combine(storageRoot, request.AccountId.ToString());

        if (!Directory.Exists(accountStoragePath)) Directory.CreateDirectory(accountStoragePath);

        string filePath = Path.Combine(accountStoragePath, "cloud.zip");
        await File.WriteAllBytesAsync(filePath, request.FileBytes, cancellationToken);

        // 3. Update Account Entity in Database via repository
        DateTimeOffset backupTime;
        if (!string.IsNullOrEmpty(request.FileModifyTime) && DateTimeOffset.TryParseExact(request.FileModifyTime,
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsedTime))
            backupTime = parsedTime;
        else
            backupTime = _timeProvider.GetUtcNow();

        await accountRepository.UpdateCloudStoragePreferencesAsync(request.AccountId, useCloud: true, autoUpload: null, backupTime: backupTime, cancellationToken);

        logger?.LogInformation(
            "[{Timestamp}] [StoreCloudConfigHandler] Successfully stored cloud config for Account ID: {AccountId}",
            _timeProvider.GetUtcNow(),
            request.AccountId);

        return new StoreCloudConfigResultDto(true);
    }
}
