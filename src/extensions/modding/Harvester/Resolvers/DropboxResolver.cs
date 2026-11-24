// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Specialized;
using System.Web;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Download resolver for Dropbox links (converting web sharing pages to direct content streams).
/// </summary>
public class DropboxResolver : IDownloadHostResolver
{
    public DownloadHostType HostType => DownloadHostType.Dropbox;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return host == "dropbox.com" ||
               host.EndsWith(".dropbox.com") ||
               host == "dl.dropboxusercontent.com" ||
               host.EndsWith(".dropboxusercontent.com");
    }

    public Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        // Build direct download URL
        UriBuilder builder = new(uri);

        // Normalize hostname: dl.dropboxusercontent.com or preserve with dl=1
        NameValueCollection query = HttpUtility.ParseQueryString(builder.Query);
        query["dl"] = "1";
        builder.Query = query.ToString();

        string directUrl = builder.ToString();

        // Extract filename
        string rawFileName = Path.GetFileName(uri.AbsolutePath);
        string decodedFileName = HttpUtility.UrlDecode(rawFileName);

        if (string.IsNullOrWhiteSpace(decodedFileName) || decodedFileName == "file" || decodedFileName == "dl")
            decodedFileName = "dropbox_download.honmod";

        if (!decodedFileName.EndsWith(".honmod", StringComparison.OrdinalIgnoreCase) &&
            !decodedFileName.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) &&
            !decodedFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            // If filename has no mod extension but is a mod download, append .honmod
            decodedFileName += ".honmod";

        ResolvedDownloadTarget target = new()
        {
            OriginalUrl = url,
            DirectDownloadUrl = directUrl,
            SuggestedFileName = decodedFileName,
            HostType = DownloadHostType.Dropbox
        };

        return Task.FromResult(target);
    }
}
