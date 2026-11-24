// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Models.Supabase;

namespace ASPIRE.Common.Services.Supabase;

/// <summary>
///     Unified Supabase Storage Client for managing multi-bucket media, replays, avatars,
///     and image transformation CDN pipelines across NEXUS.
/// </summary>
public sealed class SupabaseStorageClient(
    IConfiguration configuration,
    HttpClient? httpClient = null,
    ILogger<SupabaseStorageClient>? logger = null)
{
    public const string ReplaysBucket = "replays";
    public const string AvatarsBucket = "avatars";
    public const string ClansBucket = "clans";
    public const string ModsBucket = "mods";
    public const string ConfigsBucket = "configs";

    private readonly HttpClient _httpClient = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();

    public async Task<List<string>> ListFilesAsync(
        string bucketName,
        string prefix = "",
        CancellationToken cancellationToken = default)
    {
        SupabaseSettings settings = configuration.GetSupabaseSettings();
        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            if (logger != null && logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(
                    "[SupabaseStorageClient] Supabase URL is not configured. Cannot list files in bucket '{Bucket}'",
                    bucketName);
            return [];
        }

        string cleanUrl = settings.Url.TrimEnd('/');
        string apiKey = !string.IsNullOrWhiteSpace(settings.ServiceRoleKey)
            ? settings.ServiceRoleKey
            : settings.AnonKey;

        string listEndpoint = $"{cleanUrl}/storage/v1/object/list/{bucketName}";

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, listEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            string cleanPrefix = prefix.TrimStart('/');
            string payload = $"{{\"prefix\":\"{cleanPrefix}\",\"limit\":100,\"offset\":0}}";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument doc = JsonDocument.Parse(json);
                List<string> fileNames = [];
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                    if (element.TryGetProperty("name", out JsonElement nameProp))
                    {
                        string? name = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) fileNames.Add(name);
                    }

                if (logger != null && logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "[SupabaseStorageClient] Listed {Count} items in bucket '{Bucket}' with prefix '{Prefix}': {Items}",
                        fileNames.Count, bucketName, prefix, string.Join(", ", fileNames));

                return fileNames;
            }

            if (logger != null && logger.IsEnabled(LogLevel.Warning))
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "[SupabaseStorageClient] List files failed for bucket '{Bucket}' prefix '{Prefix}'. HTTP {Status}: {Error}",
                    bucketName, prefix, (int)response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex,
                    "[SupabaseStorageClient] Exception listing files with prefix '{Prefix}' in bucket '{Bucket}'",
                    prefix, bucketName);
        }

        return [];
    }

    public async Task<Stream?> DownloadFileAsync(
        string bucketName,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        SupabaseSettings settings = configuration.GetSupabaseSettings();
        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            if (logger != null && logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("[SupabaseStorageClient] Supabase URL is not configured. Cannot download {FilePath}",
                    filePath);

            return null;
        }

        string cleanUrl = settings.Url.TrimEnd('/');
        string cleanPath = filePath.TrimStart('/');
        string apiKey = !string.IsNullOrWhiteSpace(settings.ServiceRoleKey)
            ? settings.ServiceRoleKey
            : settings.AnonKey;

        // 1. Direct authenticated object endpoint: GET /storage/v1/object/{bucket}/{path}
        string directEndpoint = $"{cleanUrl}/storage/v1/object/{bucketName}/{cleanPath}";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, directEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            HttpResponseMessage response =
                await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (logger != null && logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "[SupabaseStorageClient] Successfully downloaded {FilePath} from bucket '{Bucket}' ({Bytes} bytes)",
                        cleanPath, bucketName, bytes.Length);
                return new MemoryStream(bytes);
            }

            // 2. Public endpoint: GET /storage/v1/object/public/{bucket}/{path}
            string publicEndpoint = $"{cleanUrl}/storage/v1/object/public/{bucketName}/{cleanPath}";
            using HttpRequestMessage publicRequest = new(HttpMethod.Get, publicEndpoint);
            HttpResponseMessage publicResponse = await _httpClient.SendAsync(publicRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (publicResponse.IsSuccessStatusCode)
            {
                byte[] bytes = await publicResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (logger != null && logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "[SupabaseStorageClient] Successfully downloaded {FilePath} via public endpoint from bucket '{Bucket}' ({Bytes} bytes)",
                        cleanPath, bucketName, bytes.Length);
                return new MemoryStream(bytes);
            }

            // 3. Fallback: Authenticated sub-route
            string authEndpoint = $"{cleanUrl}/storage/v1/object/authenticated/{bucketName}/{cleanPath}";
            using HttpRequestMessage authRequest = new(HttpMethod.Get, authEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                authRequest.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            HttpResponseMessage authResponse = await _httpClient.SendAsync(authRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (authResponse.IsSuccessStatusCode)
            {
                byte[] bytes = await authResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                return new MemoryStream(bytes);
            }

            if (logger != null && logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(
                    "[SupabaseStorageClient] File not found at {FilePath} in bucket '{Bucket}' (HTTP {Status})",
                    cleanPath, bucketName, (int)response.StatusCode);

            return null;
        }
        catch (Exception ex)
        {
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "[SupabaseStorageClient] Exception downloading {FilePath} from bucket '{Bucket}'",
                    cleanPath, bucketName);

            return null;
        }
    }

    public string GetPublicUrl(string bucketName, string filePath)
    {
        SupabaseSettings settings = configuration.GetSupabaseSettings();
        if (string.IsNullOrWhiteSpace(settings.Url)) return string.Empty;

        string cleanUrl = settings.Url.TrimEnd('/');
        string cleanPath = filePath.TrimStart('/');
        return $"{cleanUrl}/storage/v1/object/public/{bucketName}/{cleanPath}";
    }

    public string GetTransformedImageUrl(
        string bucketName,
        string filePath,
        int? width = null,
        int? height = null,
        int? quality = null,
        string format = "origin",
        string resize = "cover")
    {
        SupabaseSettings settings = configuration.GetSupabaseSettings();
        if (string.IsNullOrWhiteSpace(settings.Url)) return string.Empty;

        string cleanUrl = settings.Url.TrimEnd('/');
        string cleanPath = filePath.TrimStart('/');
        string baseUrl = $"{cleanUrl}/storage/v1/render/image/public/{bucketName}/{cleanPath}";

        List<string> queryParams = [];
        if (width is > 0) queryParams.Add($"width={width.Value}");

        if (height is > 0) queryParams.Add($"height={height.Value}");

        if (quality is > 0 and <= 100) queryParams.Add($"quality={quality.Value}");

        if (!string.IsNullOrWhiteSpace(format) && format != "origin") queryParams.Add($"format={format}");

        if (!string.IsNullOrWhiteSpace(resize) && resize != "cover") queryParams.Add($"resize={resize}");

        return queryParams.Count == 0 ? baseUrl : $"{baseUrl}?{string.Join('&', queryParams)}";
    }

    public async Task<string> UploadFileAsync(
        string bucketName,
        string filePath,
        Stream fileStream,
        string contentType = "application/octet-stream",
        bool isPublic = true,
        int cacheControlSeconds = 86400,
        CancellationToken cancellationToken = default)
    {
        SupabaseSettings settings = configuration.GetSupabaseSettings();
        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            if (logger != null && logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("[SupabaseStorageClient] Supabase URL is not configured. Cannot upload {FilePath}",
                    filePath);

            return string.Empty;
        }

        string cleanUrl = settings.Url.TrimEnd('/');
        string cleanPath = filePath.TrimStart('/');
        string uploadEndpoint = $"{cleanUrl}/storage/v1/object/{bucketName}/{cleanPath}";
        string publicUrl = GetPublicUrl(bucketName, cleanPath);
        string apiKey = !string.IsNullOrWhiteSpace(settings.ServiceRoleKey)
            ? settings.ServiceRoleKey
            : settings.AnonKey;

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
            request.Headers.TryAddWithoutValidation("cache-control", $"max-age={cacheControlSeconds}, public");

            using StreamContent streamContent = new(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Content = streamContent;

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                if (logger != null && logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "[SupabaseStorageClient] Uploaded {FilePath} to bucket '{Bucket}' in {ElapsedMs}ms",
                        cleanPath, bucketName, stopwatch.ElapsedMilliseconds);

                return publicUrl;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                bool created = await EnsureBucketExistsAsync(cleanUrl, bucketName, apiKey, isPublic, cancellationToken);
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
                        $"max-age={cacheControlSeconds}, public");

                    using StreamContent retryContent = new(fileStream);
                    retryContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    retryRequest.Content = retryContent;

                    HttpResponseMessage retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken);
                    if (retryResponse.IsSuccessStatusCode)
                    {
                        if (logger != null && logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation(
                                "[SupabaseStorageClient] Created bucket '{Bucket}' and uploaded {FilePath} in {ElapsedMs}ms",
                                bucketName, cleanPath, stopwatch.ElapsedMilliseconds);

                        return publicUrl;
                    }
                }
            }

            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger.LogError(
                    "[SupabaseStorageClient] Failed to upload {FilePath} to bucket '{Bucket}'. HTTP {Status}: {Error}",
                    cleanPath, bucketName, (int)response.StatusCode, error);

            return publicUrl;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex,
                    "[SupabaseStorageClient] Exception uploading {FilePath} to bucket '{Bucket}' after {ElapsedMs}ms",
                    cleanPath, bucketName, stopwatch.ElapsedMilliseconds);

            return publicUrl;
        }
    }

    public async Task<bool> EnsureBucketExistsAsync(
        string projectUrl,
        string bucketName,
        string apiKey,
        bool isPublic = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string bucketEndpoint = $"{projectUrl}/storage/v1/bucket";
            using HttpRequestMessage request = new(HttpMethod.Post, bucketEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.TryAddWithoutValidation("apikey", apiKey);
            }

            string publicFlag = isPublic ? "true" : "false";
            string payload = $"{{\"id\":\"{bucketName}\",\"name\":\"{bucketName}\",\"public\":{publicFlag}}}";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            if (logger != null && logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(ex, "[SupabaseStorageClient] Failed to ensure bucket '{Bucket}' exists", bucketName);

            return false;
        }
    }
}
