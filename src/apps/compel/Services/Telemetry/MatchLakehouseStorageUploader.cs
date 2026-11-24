// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchLakehouseStorageUploader.cs
// System: COMPEL Supervision - Dedicated Server Match Parquet Lakehouse Uploader
// Policy: Strict Explicit Typing (NO 'var'), Native AOT Trim-Safe (NO Reflection)
// ============================================================================

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Channels;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Security;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     Immutable payload queued for background upload to cloud object storage.
/// </summary>
public sealed record LakehouseUploadJob(
    long MatchId,
    string LocalFilePath,
    DateTimeOffset QueuedAtUtc);

/// <summary>
///     High-performance, Native AOT trim-safe background service that syncs compressed
///     Parquet match telemetry files to S3-compatible cloud object storage (Supabase Storage / Cloudflare R2).
///     Employs custom AWS Signature Version 4 (SigV4) HTTP signing without heavy reflection-based SDKs.
/// </summary>
public sealed class MatchLakehouseStorageUploader : ResilientBackgroundService
{
    private const int MaxLocalFifoRetainedFiles = 10;

    private readonly HttpClient _httpClient;
    private readonly ILogger<MatchLakehouseStorageUploader>? _logger;
    private readonly Channel<LakehouseUploadJob> _uploadQueue;
    private int _unconfiguredWarningLogged;

    public LakehouseStorageSettings Settings { get; }
    public bool IsConfigured => Settings.IsConfigured;
    public string Endpoint => Settings.Endpoint;
    public string Region => Settings.Region;
    public string Bucket => Settings.Bucket;
    public string AccessKey => Settings.AccessKey;
    public string SecretKey => Settings.SecretKey;

    public MatchLakehouseStorageUploader(
        HttpClient? httpClient = null,
        ILogger<MatchLakehouseStorageUploader>? logger = null,
        LakehouseStorageSettings? settings = null)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchLakehouseStorageUploader>.Instance, nameof(MatchLakehouseStorageUploader))
    {
        _httpClient = httpClient ?? SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(30));
        _logger = logger;

        BoundedChannelOptions channelOptions = new(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _uploadQueue = Channel.CreateBounded<LakehouseUploadJob>(channelOptions);

        Settings = settings ?? LakehouseStorageSettings.Resolve(null);

        if (Settings.IsConfigured)
            _logger?.LogInformation("[LAKEHOUSE_UPLOADER] Configured: {Settings}", Settings.ToString());
        else
            _logger?.LogWarning(
                "[LAKEHOUSE_UPLOADER] Disabled - missing {Missing}. Set them in NEXUS Control Panel -> Secrets & Configs -> Supabase.",
                string.Join(", ", Settings.MissingSettings()));
    }

    /// <summary>DI constructor: resolves settings from the host configuration (user-secrets vault, env).</summary>
    public MatchLakehouseStorageUploader(IConfiguration configuration, ILogger<MatchLakehouseStorageUploader> logger)
        : this(null, logger, LakehouseStorageSettings.Resolve(configuration))
    {
    }

    /// <summary>
    ///     Enqueues a newly produced Parquet file for asynchronous cloud upload.
    /// </summary>
    public bool EnqueueUpload(long matchId, string localFilePath)
    {
        LakehouseUploadJob job = new(matchId, localFilePath, TimeProvider.System.GetUtcNow());
        bool enqueued = _uploadQueue.Writer.TryWrite(job);

        if (enqueued)
        {
            _logger?.LogInformation(
                "[LAKEHOUSE_UPLOADER] Enqueued Match #{MatchId} ({Path}) for cloud storage sync",
                matchId, localFilePath);
        }
        else
        {
            _logger?.LogWarning(
                "[LAKEHOUSE_UPLOADER] Failed to enqueue Match #{MatchId}; queue capacity reached",
                matchId);
        }

        return enqueued;
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("[LAKEHOUSE_UPLOADER] Background cloud storage upload worker started.");

        while (!stoppingToken.IsCancellationRequested &&
               await _uploadQueue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            while (_uploadQueue.Reader.TryRead(out LakehouseUploadJob? job))
            {
                if (job == null) continue;

                try
                {
                    bool success = await UploadWithRetryAsync(job.MatchId, job.LocalFilePath, stoppingToken);
                    if (success)
                    {
                        PruneLocalFifoCache(job.LocalFilePath);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "[LAKEHOUSE_UPLOADER] Unhandled error during upload processing for Match #{MatchId}",
                        job.MatchId);
                }
            }
        }

        _logger?.LogInformation("[LAKEHOUSE_UPLOADER] Background cloud storage upload worker stopped.");
    }

    /// <summary>
    ///     Executes a single upload attempt with Polly-style exponential backoff for transient errors.
    /// </summary>
    public async Task<bool> UploadWithRetryAsync(long matchId, string localFilePath, CancellationToken ct = default)
    {
        if (!Settings.IsConfigured) return await UploadParquetFileAsync(matchId, localFilePath, ct);

        const int maxAttempts = 3;
        int delayMs = 300;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                bool success = await UploadParquetFileAsync(matchId, localFilePath, ct);
                if (success) return true;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex,
                    "[LAKEHOUSE_UPLOADER] Attempt {Attempt}/{Max} failed for Match #{MatchId}. Retrying in {Delay}ms...",
                    attempt, maxAttempts, matchId, delayMs);
            }

            await Task.Delay(delayMs, ct);
            delayMs *= 2;
        }

        _logger?.LogError(
            "[LAKEHOUSE_UPLOADER] Exhausted all {Max} attempts uploading Match #{MatchId}",
            maxAttempts, matchId);
        return false;
    }

    /// <summary>
    ///     Uploads the Parquet file to the S3 bucket using Native AOT AWS SigV4 signed HTTP PUT.
    /// </summary>
    public async Task<bool> UploadParquetFileAsync(long matchId, string localFilePath, CancellationToken ct = default)
    {
        if (!Settings.IsConfigured)
        {
            if (Interlocked.Exchange(ref _unconfiguredWarningLogged, 1) == 0)
                _logger?.LogWarning(
                    "[LAKEHOUSE_UPLOADER] Skipping cloud sync for Match #{MatchId}: storage not configured (missing {Missing})",
                    matchId, string.Join(", ", Settings.MissingSettings()));
            return false;
        }

        if (!File.Exists(localFilePath))
        {
            _logger?.LogWarning(
                "[LAKEHOUSE_UPLOADER] Local file not found: {Path}", localFilePath);
            return false;
        }

        byte[] payloadBytes = await File.ReadAllBytesAsync(localFilePath, ct);
        string objectKey = $"match_{matchId}.parquet";

        // Path-style addressing that preserves the endpoint's path prefix (Supabase: /storage/v1/s3)
        Uri fullRequestUri = AwsSigV4Signer.BuildObjectUri(Endpoint, Bucket, objectKey);
        string payloadHash = AwsSigV4Signer.HashPayload(payloadBytes);

        const string contentType = "application/vnd.apache.parquet";
        const string cacheControl = "public, max-age=31536000, immutable";

        using HttpRequestMessage request = new(HttpMethod.Put, fullRequestUri);

        ByteArrayContent content = new(payloadBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content = content;
        // Cache-Control is a general/request header; HttpContentHeaders.TryAddWithoutValidation silently returns false and drops it.
        request.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);

        AwsSigV4Signer.Sign(request, payloadHash, Region, AccessKey, SecretKey, TimeProvider.System.GetUtcNow(),
            new Dictionary<string, string> { ["cache-control"] = cacheControl, ["content-type"] = contentType });

        HttpResponseMessage response = await _httpClient.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            _logger?.LogInformation(
                "[LAKEHOUSE_UPLOADER] Successfully synced Match #{MatchId} to {Uri} (Bytes: {Bytes:N0}, HTTP {Status})",
                matchId, fullRequestUri, payloadBytes.Length, (int)response.StatusCode);
            return true;
        }

        string responseBody = await response.Content.ReadAsStringAsync(ct);
        _logger?.LogWarning(
            "[LAKEHOUSE_UPLOADER] Sync failed for Match #{MatchId} (HTTP {Status}): {Body}",
            matchId, (int)response.StatusCode, responseBody);
        return false;
    }

    /// <summary>
    ///     Prunes local scratch Parquet files, retaining up to the newest N files as a fast local cache.
    /// </summary>
    public void PruneLocalFifoCache(string currentFilePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(currentFilePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            DirectoryInfo dirInfo = new(directory);
            FileInfo[] parquetFiles = dirInfo.GetFiles("match_*.parquet", SearchOption.TopDirectoryOnly);

            if (parquetFiles.Length > MaxLocalFifoRetainedFiles)
            {
                IOrderedEnumerable<FileInfo> filesByAge = parquetFiles.OrderBy(f => f.LastWriteTimeUtc);
                int toDeleteCount = parquetFiles.Length - MaxLocalFifoRetainedFiles;

                foreach (FileInfo file in filesByAge.Take(toDeleteCount))
                {
                    try
                    {
                        file.Delete();
                        _logger?.LogInformation(
                            "[LAKEHOUSE_UPLOADER] Purged older local parquet file from FIFO cache: {Name}", file.Name);
                    }
                    catch
                    {
                        // Ignore active file handle locks
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LAKEHOUSE_UPLOADER] Error pruning local FIFO cache");
        }
    }
}
