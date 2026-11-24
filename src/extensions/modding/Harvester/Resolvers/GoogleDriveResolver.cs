// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Web;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Download resolver for Google Drive share links.
/// </summary>
public class GoogleDriveResolver : IDownloadHostResolver
{
    private static readonly Regex FileIdRegex = new(@"/file/d/([a-zA-Z0-9_-]+)", RegexOptions.Compiled);

    public DownloadHostType HostType => DownloadHostType.GoogleDrive;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return host == "drive.google.com" ||
               host.EndsWith(".drive.google.com") ||
               host == "docs.google.com" ||
               host.EndsWith(".docs.google.com");
    }

    public Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        string fileId = string.Empty;

        // Try extracting from query string ?id=...
        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        if (!string.IsNullOrWhiteSpace(query["id"])) fileId = query["id"]!;

        // Try extracting from path /file/d/<id>/view
        if (string.IsNullOrWhiteSpace(fileId))
        {
            Match m = FileIdRegex.Match(uri.AbsolutePath);
            if (m.Success) fileId = m.Groups[1].Value;
        }

        string directUrl;
        if (!string.IsNullOrWhiteSpace(fileId))
            directUrl = $"https://drive.google.com/uc?export=download&id={fileId}&confirm=t";
        else
            directUrl = url;

        ResolvedDownloadTarget target = new()
        {
            OriginalUrl = url,
            DirectDownloadUrl = directUrl,
            SuggestedFileName =
                !string.IsNullOrWhiteSpace(fileId) ? $"gdrive_{fileId}.honmod" : "gdrive_download.honmod",
            HostType = DownloadHostType.GoogleDrive
        };

        return Task.FromResult(target);
    }
}
