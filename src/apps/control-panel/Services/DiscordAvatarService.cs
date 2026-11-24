// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NEXUS.ControlPanel.GUI.Services;

public static class DiscordAvatarService
{
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(5));
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NEXUS.ControlPanel/5.0");
        return client;
    }

    private static readonly ConcurrentDictionary<string, Bitmap> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static Bitmap? s_defaultKongorAvatar;
    private static readonly Lock s_lock = new();

    public static Bitmap? GetDefaultAvatar()
    {
        if (s_defaultKongorAvatar is not null) return s_defaultKongorAvatar;

        lock (s_lock)
        {
            if (s_defaultKongorAvatar is not null) return s_defaultKongorAvatar;

            try
            {
                Uri uri = new("avares://NEXUS.ControlPanel.GUI/Assets/nexus-icon.png");
                if (AssetLoader.Exists(uri))
                {
                    using Stream stream = AssetLoader.Open(uri);
                    s_defaultKongorAvatar = new Bitmap(stream);
                    return s_defaultKongorAvatar;
                }
            }
            catch
            {
                // In test or non-XAML host, continue to disk fallback
            }

            try
            {
                string diskPath = Path.Combine(AppContext.BaseDirectory, "Assets", "nexus-icon.png");
                if (File.Exists(diskPath))
                {
                    using FileStream fs = File.OpenRead(diskPath);
                    s_defaultKongorAvatar = new Bitmap(fs);
                    return s_defaultKongorAvatar;
                }
            }
            catch
            {
                // Non-fatal if asset cannot be read from disk
            }

            return null;
        }
    }

    public static async Task<Bitmap?> LoadAvatarFromCdnAsync(string? discordId, string? discordAvatar)
    {
        if (string.IsNullOrWhiteSpace(discordId) && string.IsNullOrWhiteSpace(discordAvatar))
            return GetDefaultAvatar();

        string cacheKey = $"{discordId ?? "none"}:{discordAvatar ?? "none"}";
        if (s_cache.TryGetValue(cacheKey, out Bitmap? cached))
            return cached;

        string? url = null;
        if (!string.IsNullOrWhiteSpace(discordId) && !string.IsNullOrWhiteSpace(discordAvatar))
        {
            // Discord CDN delivers static PNG renders of both static and animated avatars
            url = $"https://cdn.discordapp.com/avatars/{discordId}/{discordAvatar}.png?size=128";
        }
        else if (!string.IsNullOrWhiteSpace(discordId))
        {
            ulong dId = ulong.TryParse(discordId, out ulong parsedId) ? parsedId : 0;
            url = $"https://cdn.discordapp.com/embed/avatars/{(dId >> 22) % 6}.png";
        }

        if (url != null)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                using HttpResponseMessage response = await s_httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    using MemoryStream ms = new(data);
                    Bitmap bmp = new(ms);
                    s_cache[cacheKey] = bmp;
                    return bmp;
                }
            }
            catch
            {
                // Fall back to default avatar on network or decoding error
            }
        }

        Bitmap? fallback = GetDefaultAvatar();
        if (fallback != null)
            s_cache[cacheKey] = fallback;

        return fallback;
    }

    public static void ClearCache()
    {
        s_cache.Clear();
    }
}
