// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Web;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Download resolver for GitHub Releases and GitHub raw repository files.
/// </summary>
public class GitHubReleaseResolver : IDownloadHostResolver
{
    public DownloadHostType HostType => DownloadHostType.GitHubRelease;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return host == "github.com" ||
               host.EndsWith(".github.com") ||
               host == "raw.githubusercontent.com" ||
               host == "github-releases.githubusercontent.com" ||
               host == "release-assets.githubusercontent.com";
    }

    public Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        string directUrl = url;

        // Convert github.com/<user>/<repo>/raw/<branch>/<path> to raw.githubusercontent.com
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Contains("/raw/"))
        {
            // /owner/repo/raw/branch/path -> /owner/repo/branch/path on raw.githubusercontent.com
            string path = uri.AbsolutePath;
            int rawIdx = path.IndexOf("/raw/", StringComparison.OrdinalIgnoreCase);
            if (rawIdx >= 0)
            {
                string ownerRepo = path.Substring(0, rawIdx);
                string rest = path.Substring(rawIdx + "/raw/".Length);
                directUrl = $"https://raw.githubusercontent.com{ownerRepo}/{rest}";
            }
        }

        // Ensure HTTPS
        if (directUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            directUrl = string.Concat("https://", directUrl.AsSpan(7));

        // Extract filename
        string rawFileName = Path.GetFileName(uri.AbsolutePath);
        string decodedFileName = HttpUtility.UrlDecode(rawFileName);

        if (string.IsNullOrWhiteSpace(decodedFileName)) decodedFileName = "github_download.honmod";

        ResolvedDownloadTarget target = new()
        {
            OriginalUrl = url,
            DirectDownloadUrl = directUrl,
            SuggestedFileName = decodedFileName,
            HostType = DownloadHostType.GitHubRelease
        };

        target.CustomHeaders["User-Agent"] = "ProjectKongor-ModHarvester/1.0";

        return Task.FromResult(target);
    }
}
