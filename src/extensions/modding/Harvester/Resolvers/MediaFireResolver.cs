// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;
using System.Web;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Download resolver for MediaFire links.
/// </summary>
public class MediaFireResolver : IDownloadHostResolver
{
    private static readonly Regex MediaFireDownloadButtonRegex = new(
        @"aria-label=[""']Download file[""']\s+href=[""'](https?://download[^""']+)[""']|href=[""'](https?://download\d*\.mediafire\.com/[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DownloadHostType HostType => DownloadHostType.MediaFire;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return host == "mediafire.com" ||
               host.EndsWith(".mediafire.com");
    }

    public async Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        // Extract filename from /file/{id}/{filename} or /file/{id}/{filename}/file
        string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string suggestedFileName = "mediafire_download.honmod";

        foreach (string seg in segments)
            if (seg.EndsWith(".honmod", StringComparison.OrdinalIgnoreCase) ||
                seg.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) ||
                seg.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                suggestedFileName = HttpUtility.UrlDecode(seg);
                break;
            }

        string directUrl = url;

        // Try resolving direct download link by inspecting page HTML if client provided
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using HttpResponseMessage response =
                await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                string html = await response.Content.ReadAsStringAsync(ct);
                Match match = MediaFireDownloadButtonRegex.Match(html);
                if (match.Success)
                {
                    string resolved = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    if (!string.IsNullOrWhiteSpace(resolved)) directUrl = resolved;
                }
            }
        }
        catch
        {
            // Fallback to original URL if live resolution fails
        }

        return new ResolvedDownloadTarget
        {
            OriginalUrl = url,
            DirectDownloadUrl = directUrl,
            SuggestedFileName = suggestedFileName,
            HostType = DownloadHostType.MediaFire
        };
    }
}
