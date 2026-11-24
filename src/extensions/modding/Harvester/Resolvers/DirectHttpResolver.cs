// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Web;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Fallback download resolver for direct HTTP/HTTPS mod packages.
/// </summary>
public class DirectHttpResolver : IDownloadHostResolver
{
    public DownloadHostType HostType => DownloadHostType.DirectHttp;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        string rawFileName = Path.GetFileName(uri.AbsolutePath);
        string decodedFileName = HttpUtility.UrlDecode(rawFileName);

        if (string.IsNullOrWhiteSpace(decodedFileName) || !Path.HasExtension(decodedFileName))
            decodedFileName = "direct_download.honmod";

        ResolvedDownloadTarget target = new()
        {
            OriginalUrl = url,
            DirectDownloadUrl = url,
            SuggestedFileName = decodedFileName,
            HostType = DownloadHostType.DirectHttp
        };

        return Task.FromResult(target);
    }
}
