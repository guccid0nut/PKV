// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace KONGOR.MasterServer.Services.Storage;

public sealed class CloudStorageProvider(
    IOptions<ReplayStorageSettings> options,
    ILogger<CloudStorageProvider> logger,
    IAmazonS3? s3Client = null) : IReplayStorageProvider, IDisposable
{
    private readonly CloudStorageSettings _settings = options.Value.Cloud;
    private readonly bool _ownsClient = s3Client is null;
    private readonly IAmazonS3 _s3Client = s3Client ?? CreateS3Client(options.Value.Cloud);

    private static IAmazonS3 CreateS3Client(CloudStorageSettings settings)
    {
        AmazonS3Config s3Config = new();
        if (!string.IsNullOrEmpty(settings.ServiceUrl))
            s3Config.ServiceURL = settings.ServiceUrl;
        else if (!string.IsNullOrEmpty(settings.Region))
            s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);

        return new AmazonS3Client(settings.AccessKey, settings.SecretKey, s3Config);
    }

    public async Task<string> UploadReplayAsync(string matchId, Stream fileStream, string fileName,
        CancellationToken cancellationToken = default)
    {
        string safeFileName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName) ||
            !string.Equals(safeFileName, fileName, StringComparison.Ordinal) ||
            safeFileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid replay file name.", nameof(fileName));
        }

        string endpoint = !string.IsNullOrEmpty(_settings.ServiceUrl)
            ? _settings.ServiceUrl.TrimEnd('/')
            : $"https://{_settings.BucketName}.s3.amazonaws.com";

        string publicUrl = $"{endpoint}/replays/{safeFileName}";

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[CloudStorage] Uploading replay {FileName} for Match #{MatchId} to S3 bucket '{Bucket}'",
                fileName, matchId, _settings.BucketName);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            PutObjectRequest request = new()
            {
                BucketName = _settings.BucketName,
                Key = $"replays/{safeFileName}",
                InputStream = fileStream,
                ContentType = "application/octet-stream"
            };

            if (_settings.UsePublicReadAcl)
            {
                request.CannedACL = S3CannedACL.PublicRead;
            }

            await _s3Client.PutObjectAsync(request, cancellationToken);

            stopwatch.Stop();
            long elapsed = stopwatch.ElapsedMilliseconds;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "[CloudStorage] Successfully uploaded {FileName} for Match #{MatchId} in {ElapsedMs}ms. URL: {Url}",
                    fileName, matchId, elapsed, publicUrl);

            return publicUrl;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            long elapsed = stopwatch.ElapsedMilliseconds;
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex,
                    "[CloudStorage] Failed to upload {FileName} to S3 bucket '{Bucket}' after {ElapsedMs}ms",
                    fileName, _settings.BucketName, elapsed);

            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _s3Client.Dispose();
        }
    }
}
