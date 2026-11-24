// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Http.Headers;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Models.Supabase;

namespace KONGOR.MasterServer.Services.Storage;

public sealed partial class SupabaseStorageProvider(
    IOptions<ReplayStorageSettings> options,
    IConfiguration configuration,
    ILogger<SupabaseStorageProvider> logger,
    HttpClient? customHttpClient = null) : IReplayStorageProvider
{
    private readonly HttpClient _httpClient = customHttpClient ?? CreateDefaultHttpClient();
    private readonly ReplayStorageSettings _settings = options.Value;

    public async Task<string> UploadReplayAsync(string matchId, Stream fileStream, string fileName,
        CancellationToken cancellationToken = default)
    {
        SupabaseSettings globalSupabase = configuration.GetSupabaseSettings();

        string projectUrl = !string.IsNullOrWhiteSpace(_settings.Supabase.ProjectUrl)
            ? _settings.Supabase.ProjectUrl
            : globalSupabase.Url;

        string apiKey = !string.IsNullOrWhiteSpace(_settings.Supabase.ServiceRoleKey)
            ? _settings.Supabase.ServiceRoleKey
            : !string.IsNullOrWhiteSpace(globalSupabase.ServiceRoleKey)
                ? globalSupabase.ServiceRoleKey
                : !string.IsNullOrWhiteSpace(_settings.Supabase.AnonKey)
                    ? _settings.Supabase.AnonKey
                    : globalSupabase.AnonKey;

        string bucketName = !string.IsNullOrWhiteSpace(_settings.Supabase.BucketName)
            ? _settings.Supabase.BucketName
            : !string.IsNullOrWhiteSpace(globalSupabase.StorageBucket)
                ? globalSupabase.StorageBucket
                : ConfigurationConstants.DefaultSupabaseReplayBucket;

        if (string.IsNullOrWhiteSpace(projectUrl))
        {
            LogProjectUrlNotConfigured(logger, matchId);
            return $"{configuration.GetMasterServerAddress()}/replays/{fileName}";
        }

        string cleanProjectUrl = projectUrl.TrimEnd('/');
        string uploadEndpoint = $"{cleanProjectUrl}/storage/v1/object/{bucketName}/{fileName}";
        string publicDownloadUrl = $"{cleanProjectUrl}/storage/v1/object/public/{bucketName}/{fileName}";

        LogUploadingReplay(logger, fileName, matchId, bucketName, uploadEndpoint);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, uploadEndpoint);

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            request.Headers.TryAddWithoutValidation("x-upsert", "true");
            request.Headers.TryAddWithoutValidation("cache-control", "public, max-age=31536000, immutable");

            using StreamContent streamContent = new(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = streamContent;

            HttpResponseMessage response =
                await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                LogUploadSuccess(logger, fileName, matchId, stopwatch.ElapsedMilliseconds, publicDownloadUrl);
                return publicDownloadUrl;
            }

            // If bucket does not exist (404), attempt to auto-create the bucket and retry upload
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                bool created = await EnsureBucketExistsAsync(cleanProjectUrl, bucketName, apiKey, cancellationToken);
                if (created && fileStream.CanSeek)
                {
                    fileStream.Position = 0;
                    using HttpRequestMessage retryRequest = new(HttpMethod.Post, uploadEndpoint);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        retryRequest.Headers.TryAddWithoutValidation("apikey", apiKey);
                    }

                    retryRequest.Headers.TryAddWithoutValidation("x-upsert", "true");
                    retryRequest.Headers.TryAddWithoutValidation("cache-control",
                        "public, max-age=31536000, immutable");
                    using StreamContent retryContent = new(fileStream);
                    retryContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    retryRequest.Content = retryContent;

                    HttpResponseMessage retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken);
                    if (retryResponse.IsSuccessStatusCode)
                    {
                        LogUploadSuccess(logger, fileName, matchId, stopwatch.ElapsedMilliseconds, publicDownloadUrl);
                        return publicDownloadUrl;
                    }
                }
            }

            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            LogUploadFailed(logger, fileName, (int)response.StatusCode, errorBody, stopwatch.ElapsedMilliseconds);

            // If the file already exists in Supabase storage (400 Duplicate / 409 Conflict), the public URL is valid
            if ((response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Conflict) &&
                (errorBody.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                 errorBody.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)))
                return publicDownloadUrl;

            throw new HttpRequestException(
                $"Failed to upload replay '{fileName}' to Supabase Storage (HTTP {(int)response.StatusCode}): {errorBody}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogUploadException(logger, ex, fileName, bucketName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };

        HttpClient client = new(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromMinutes(2)
        };

        return client;
    }

    private async Task<bool> EnsureBucketExistsAsync(string projectUrl, string bucketName, string apiKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string bucketEndpoint = $"{projectUrl}/storage/v1/bucket";
            using HttpRequestMessage createBucketRequest = new(HttpMethod.Post, bucketEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                createBucketRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                createBucketRequest.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            string payload = $"{{\"id\":\"{bucketName}\",\"name\":\"{bucketName}\",\"public\":true}}";
            createBucketRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(createBucketRequest, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "[SupabaseStorage] Supabase Project URL is not configured. Falling back to local storage URL pattern for Match #{MatchId}")]
    private static partial void LogProjectUrlNotConfigured(ILogger logger, string matchId);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[SupabaseStorage] Uploading replay {FileName} for Match #{MatchId} to Supabase bucket '{Bucket}' at {Endpoint}")]
    private static partial void LogUploadingReplay(ILogger logger, string fileName, string matchId, string bucket,
        string endpoint);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[SupabaseStorage] Successfully uploaded {FileName} for Match #{MatchId} in {ElapsedMs}ms. Public URL: {Url}")]
    private static partial void LogUploadSuccess(ILogger logger, string fileName, string matchId, long elapsedMs,
        string url);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "[SupabaseStorage] Failed to upload {FileName} to Supabase. HTTP {StatusCode}: {Error}. Elapsed: {ElapsedMs}ms")]
    private static partial void LogUploadFailed(ILogger logger, string fileName, int statusCode, string error,
        long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[SupabaseStorage] Exception uploading {FileName} to Supabase bucket '{Bucket}' after {ElapsedMs}ms")]
    private static partial void LogUploadException(ILogger logger, Exception ex, string fileName, string bucket,
        long elapsedMs);
}
