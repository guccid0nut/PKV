// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DAWNBRINGER.WebPortal.UI.Services;

public sealed class TwitchStreamService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TwitchStreamService> logger) : ITwitchStreamService
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, TwitchStreamInfo Info)>
        _streamCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, List<TwitchStreamInfo> Streams)>
        _honStreamsAggregateCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _tokenLock = new(1, 1);
    private static string? _appAccessToken;
    private static DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<TwitchStreamService> _logger = logger;

    public async Task<TwitchStreamInfo> GetStreamStatusAsync(string twitchUsername, string? twitchId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(twitchUsername))
        {
            return new TwitchStreamInfo { IsLive = false };
        }

        string cleanUsername = twitchUsername.Trim().TrimStart('@');
        string cacheKey = cleanUsername.ToLowerInvariant();

        if (_streamCache.TryGetValue(cacheKey, out (DateTimeOffset ExpiresAt, TwitchStreamInfo Info) cachedEntry))
        {
            if (TimeProvider.System.GetUtcNow() < cachedEntry.ExpiresAt)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[TwitchStreamService] Cache hit for '{Username}': Live={IsLive}", cleanUsername,
                        cachedEntry.Info.IsLive);
                }

                return cachedEntry.Info;
            }
        }

        (string? clientId, string? clientSecret) = GetTwitchCredentials();

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            clientId.Contains("development-twitch-client-id", StringComparison.OrdinalIgnoreCase))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "[TwitchStreamService] Twitch credentials not configured or set to dev defaults. Returning offline status.");
            }

            TwitchStreamInfo defaultInfo = new()
            {
                IsLive = false,
                TwitchUsername = cleanUsername,
                TwitchId = twitchId,
                ChannelUrl = $"https://twitch.tv/{cleanUsername}"
            };
            _streamCache[cacheKey] = (TimeProvider.System.GetUtcNow().AddMinutes(5), defaultInfo);
            return defaultInfo;
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        CancellationToken effectiveCt = timeoutCts.Token;

        string? accessToken = await GetAppAccessTokenAsync(clientId, clientSecret, effectiveCt);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            TwitchStreamInfo offlineFallback = new()
            {
                IsLive = false,
                TwitchUsername = cleanUsername,
                TwitchId = twitchId,
                ChannelUrl = $"https://twitch.tv/{cleanUsername}"
            };
            _streamCache[cacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(30), offlineFallback);
            return offlineFallback;
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            string queryUrl = !string.IsNullOrWhiteSpace(twitchId)
                ? $"https://api.twitch.tv/helix/streams?user_id={Uri.EscapeDataString(twitchId)}"
                : $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(cleanUsername)}";

            using HttpRequestMessage request = new(HttpMethod.Get, queryUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("Client-Id", clientId);

            using HttpResponseMessage response = await client.SendAsync(request, effectiveCt);

            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "[TwitchStreamService] Twitch API returned {StatusCode} when checking live status for '{Username}'",
                        response.StatusCode, cleanUsername);
                }

                TwitchStreamInfo errorFallback = new()
                {
                    IsLive = false,
                    TwitchUsername = cleanUsername,
                    TwitchId = twitchId,
                    ChannelUrl = $"https://twitch.tv/{cleanUsername}"
                };
                _streamCache[cacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(30), errorFallback);
                return errorFallback;
            }

            string responseJson = await response.Content.ReadAsStringAsync(effectiveCt);
            using JsonDocument doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("data", out JsonElement dataElement) && dataElement.GetArrayLength() > 0)
            {
                JsonElement stream = dataElement[0];
                string type = stream.TryGetProperty("type", out JsonElement typeEl) ? typeEl.GetString() ?? "" : "";
                bool isLive = string.Equals(type, "live", StringComparison.OrdinalIgnoreCase);

                string title = stream.TryGetProperty("title", out JsonElement titleEl) ? titleEl.GetString() ?? "" : "";
                string gameName = stream.TryGetProperty("game_name", out JsonElement gameEl)
                    ? gameEl.GetString() ?? ""
                    : "";
                int viewerCount = stream.TryGetProperty("viewer_count", out JsonElement viewersEl)
                    ? viewersEl.GetInt32()
                    : 0;
                string rawThumb = stream.TryGetProperty("thumbnail_url", out JsonElement thumbEl)
                    ? thumbEl.GetString() ?? ""
                    : "";
                string formattedThumb = rawThumb.Replace("{width}", "640").Replace("{height}", "360");

                DateTimeOffset? startedAt = null;
                if (stream.TryGetProperty("started_at", out JsonElement startedEl) &&
                    DateTimeOffset.TryParse(startedEl.GetString(), out DateTimeOffset parsedStart))
                {
                    startedAt = parsedStart;
                }

                TwitchStreamInfo liveInfo = new()
                {
                    IsLive = isLive,
                    TwitchUsername = cleanUsername,
                    TwitchId =
                        stream.TryGetProperty("user_id", out JsonElement uidEl) ? uidEl.GetString() : twitchId,
                    ChannelUrl = $"https://twitch.tv/{cleanUsername}",
                    StreamTitle = title,
                    GameName = gameName,
                    ViewerCount = viewerCount,
                    ThumbnailUrl = formattedThumb,
                    StartedAt = startedAt
                };

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[TwitchStreamService] Creator '{Username}' is LIVE on Twitch! Title: '{Title}', Viewers: {Viewers}, Game: '{Game}'",
                        cleanUsername, title, viewerCount, gameName);
                }

                // Cache live status for 45 seconds
                _streamCache[cacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(45), liveInfo);
                return liveInfo;
            }

            // Not live
            TwitchStreamInfo offlineInfo = new()
            {
                IsLive = false,
                TwitchUsername = cleanUsername,
                TwitchId = twitchId,
                ChannelUrl = $"https://twitch.tv/{cleanUsername}"
            };

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[TwitchStreamService] Creator '{Username}' is offline on Twitch.", cleanUsername);
            }

            _streamCache[cacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(60), offlineInfo);
            return offlineInfo;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex,
                    "[TwitchStreamService] Exception while querying Twitch stream status for '{Username}'",
                    cleanUsername);
            }

            TwitchStreamInfo exceptionFallback = new()
            {
                IsLive = false,
                TwitchUsername = cleanUsername,
                TwitchId = twitchId,
                ChannelUrl = $"https://twitch.tv/{cleanUsername}"
            };
            _streamCache[cacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(30), exceptionFallback);
            return exceptionFallback;
        }
    }

    public async Task<List<TwitchStreamInfo>> GetLiveHonStreamsAsync(IEnumerable<string> twitchLogins,
        CancellationToken cancellationToken = default)
    {
        List<string> cleanLogins =
        [
            .. twitchLogins
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim().TrimStart('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        if (cleanLogins.Count == 0)
        {
            return [];
        }

        string aggregateCacheKey = string.Join(",", cleanLogins.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (_honStreamsAggregateCache.TryGetValue(aggregateCacheKey, out (DateTimeOffset ExpiresAt, List<TwitchStreamInfo> Streams) cachedHon) &&
            TimeProvider.System.GetUtcNow() < cachedHon.ExpiresAt)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[TwitchStreamService] Cache hit for aggregate live HoN streams ({Count} streams).", cachedHon.Streams.Count);
            }
            return cachedHon.Streams;
        }

        (string? clientId, string? clientSecret) = GetTwitchCredentials();

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            clientId.Contains("development-twitch-client-id", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "[TwitchStreamService] Twitch credentials not configured. Returning empty live HoN stream list.");
            return [];
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        CancellationToken effectiveCt = timeoutCts.Token;

        string? accessToken = await GetAppAccessTokenAsync(clientId, clientSecret, effectiveCt);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return [];
        }

        List<TwitchStreamInfo> honStreams = [];

        try
        {
            HttpClient client = _httpClientFactory.CreateClient();

            foreach (string[] batch in cleanLogins.Chunk(100))
            {
                string queryString =
                    string.Join("&", batch.Select(login => $"user_login={Uri.EscapeDataString(login)}"));
                string queryUrl = $"https://api.twitch.tv/helix/streams?{queryString}";

                using HttpRequestMessage request = new(HttpMethod.Get, queryUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Add("Client-Id", clientId);

                using HttpResponseMessage response = await client.SendAsync(request, effectiveCt);

                if (!response.IsSuccessStatusCode)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(
                            "[TwitchStreamService] Twitch API returned {StatusCode} when batch querying live streams",
                            response.StatusCode);
                    }

                    continue;
                }

                string responseJson = await response.Content.ReadAsStringAsync(effectiveCt);
                using JsonDocument doc = JsonDocument.Parse(responseJson);

                if (doc.RootElement.TryGetProperty("data", out JsonElement dataElement) &&
                    dataElement.GetArrayLength() > 0)
                {
                    foreach (JsonElement stream in dataElement.EnumerateArray())
                    {
                        string login = stream.TryGetProperty("user_login", out JsonElement loginEl)
                            ? loginEl.GetString() ?? ""
                            : "";
                        string type = stream.TryGetProperty("type", out JsonElement typeEl)
                            ? typeEl.GetString() ?? ""
                            : "";
                        string gameName = stream.TryGetProperty("game_name", out JsonElement gameEl)
                            ? gameEl.GetString() ?? ""
                            : "";
                        string title = stream.TryGetProperty("title", out JsonElement titleEl)
                            ? titleEl.GetString() ?? ""
                            : "";
                        int viewerCount = stream.TryGetProperty("viewer_count", out JsonElement viewersEl)
                            ? viewersEl.GetInt32()
                            : 0;
                        string rawThumb = stream.TryGetProperty("thumbnail_url", out JsonElement thumbEl)
                            ? thumbEl.GetString() ?? ""
                            : "";
                        string formattedThumb = rawThumb.Replace("{width}", "640").Replace("{height}", "360");

                        bool isLive = string.Equals(type, "live", StringComparison.OrdinalIgnoreCase);

                        DateTimeOffset? startedAt = null;
                        if (stream.TryGetProperty("started_at", out JsonElement startedEl) &&
                            DateTimeOffset.TryParse(startedEl.GetString(), out DateTimeOffset parsedStart))
                        {
                            startedAt = parsedStart;
                        }

                        TwitchStreamInfo streamInfo = new()
                        {
                            IsLive = isLive,
                            TwitchUsername = login,
                            TwitchId =
                                stream.TryGetProperty("user_id", out JsonElement uidEl) ? uidEl.GetString() : null,
                            ChannelUrl = $"https://twitch.tv/{login}",
                            StreamTitle = title,
                            GameName = gameName,
                            ViewerCount = viewerCount,
                            ThumbnailUrl = formattedThumb,
                            StartedAt = startedAt
                        };

                        _streamCache[login.ToLowerInvariant()] =
                            (TimeProvider.System.GetUtcNow().AddSeconds(45), streamInfo);

                        // Only push to the game home page if they are playing Heroes of Newerth!
                        if (streamInfo.IsPlayingHon)
                        {
                            if (_logger.IsEnabled(LogLevel.Information))
                            {
                                _logger.LogInformation(
                                    "[TwitchStreamService] Found live Heroes of Newerth broadcast for '{Username}' ({Viewers} viewers): '{Title}'",
                                    login, viewerCount, title);
                            }

                            honStreams.Add(streamInfo);
                        }
                    }
                }
            }

            List<TwitchStreamInfo> ordered = [.. honStreams.OrderByDescending(s => s.ViewerCount)];
            _honStreamsAggregateCache[aggregateCacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(60), ordered);
            return ordered;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "[TwitchStreamService] Exception while batch querying live HoN streams");
            }

            _honStreamsAggregateCache[aggregateCacheKey] = (TimeProvider.System.GetUtcNow().AddSeconds(30), honStreams);
            return honStreams;
        }
    }

    private async Task<string?> GetAppAccessTokenAsync(string clientId, string clientSecret,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_appAccessToken) && TimeProvider.System.GetUtcNow() < _tokenExpiresAt)
        {
            return _appAccessToken;
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        CancellationToken effectiveCt = timeoutCts.Token;

        await _tokenLock.WaitAsync(effectiveCt);
        try
        {
            if (!string.IsNullOrWhiteSpace(_appAccessToken) && TimeProvider.System.GetUtcNow() < _tokenExpiresAt)
            {
                return _appAccessToken;
            }

            HttpClient client = _httpClientFactory.CreateClient();
            Dictionary<string, string> postParams = new()
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials"
            };

            using FormUrlEncodedContent content = new(postParams);
            using HttpResponseMessage tokenResponse =
                await client.PostAsync("https://id.twitch.tv/oauth2/token", content, effectiveCt);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("[TwitchStreamService] Failed to obtain Twitch App Access Token: {StatusCode}",
                        tokenResponse.StatusCode);
                }

                return null;
            }

            string tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(tokenJson);

            if (doc.RootElement.TryGetProperty("access_token", out JsonElement tokenElem))
            {
                string token = tokenElem.GetString() ?? "";
                int expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement expElem)
                    ? expElem.GetInt32()
                    : 3600;

                _appAccessToken = token;
                // Buffer by 5 minutes before actual expiry
                _tokenExpiresAt = TimeProvider.System.GetUtcNow().AddSeconds(Math.Max(60, expiresIn - 300));
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[TwitchStreamService] Acquired new Twitch App Access Token (valid for {Seconds}s)", expiresIn);
                }

                return _appAccessToken;
            }

            return null;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "[TwitchStreamService] Exception during Twitch App Access Token acquisition");
            }

            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private (string? ClientId, string? ClientSecret) GetTwitchCredentials()
    {
        string? configuredClientId = _configuration["twitch-client-id"] ??
                                     _configuration["Authentication:Twitch:ClientId"] ??
                                     _configuration["Parameters:twitch-client-id"] ??
                                     _configuration["TWITCH_CLIENT_ID"];

        string? configuredClientSecret = _configuration["twitch-client-secret"] ??
                                         _configuration["Authentication:Twitch:ClientSecret"] ??
                                         _configuration["Parameters:twitch-client-secret"] ??
                                         _configuration["TWITCH_CLIENT_SECRET"];

#if !PLAYTEST_BUILD

        if (string.IsNullOrWhiteSpace(configuredClientId) || string.IsNullOrWhiteSpace(configuredClientSecret))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidateFiles =
            [
                Path.Combine(appData, "Microsoft", "UserSecrets", "f1e1f81d-ed00-40b6-8d39-0b82a10a6338",
                    "secrets.json"),
                Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52",
                    "secrets.json")
            ];

            foreach (string filePath in candidateFiles)
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        string fileText = File.ReadAllText(filePath);
                        using JsonDocument doc = JsonDocument.Parse(fileText);
                        if (string.IsNullOrWhiteSpace(configuredClientId) &&
                            (doc.RootElement.TryGetProperty("twitch-client-id", out JsonElement idElem) ||
                             doc.RootElement.TryGetProperty("Authentication:Twitch:ClientId", out idElem)))
                        {
                            configuredClientId = idElem.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(configuredClientSecret) &&
                            (doc.RootElement.TryGetProperty("twitch-client-secret", out JsonElement secElem) ||
                             doc.RootElement.TryGetProperty("Authentication:Twitch:ClientSecret", out secElem)))
                        {
                            configuredClientSecret = secElem.GetString();
                        }
                    }
                    catch
                    {
                        // Ignore parse exceptions on fallback search
                    }
                }
            }
        }

#endif
        return (configuredClientId, configuredClientSecret);
    }
}
