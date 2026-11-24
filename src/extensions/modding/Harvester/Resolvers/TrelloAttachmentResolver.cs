// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Web;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Download resolver for direct Trello card attachments.
/// </summary>
public class TrelloAttachmentResolver : IDownloadHostResolver
{
    public DownloadHostType HostType => DownloadHostType.TrelloAttachment;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return (host == "trello.com" || host.EndsWith(".trello.com") ||
                host == "trello-attachments.s3.amazonaws.com") &&
               (uri.AbsolutePath.Contains("/attachments/") || uri.AbsolutePath.Contains("/download/"));
    }

    public Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        string rawFileName = Path.GetFileName(uri.AbsolutePath);
        string decodedFileName = HttpUtility.UrlDecode(rawFileName);

        if (string.IsNullOrWhiteSpace(decodedFileName) || decodedFileName == "download")
            decodedFileName = "trello_attachment.honmod";

        if (!decodedFileName.EndsWith(".honmod", StringComparison.OrdinalIgnoreCase) &&
            !decodedFileName.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) &&
            !decodedFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            decodedFileName += ".honmod";

        ResolvedDownloadTarget target = new()
        {
            OriginalUrl = url,
            DirectDownloadUrl = url,
            SuggestedFileName = decodedFileName,
            HostType = DownloadHostType.TrelloAttachment
        };

        return Task.FromResult(target);
    }
}
