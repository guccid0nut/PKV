// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Download resolver for Cloud Mail.ru shared public links.
/// </summary>
public class MailRuResolver : IDownloadHostResolver
{
    private static readonly Regex PublicFolderRegex = new(@"/public/([^/]+)/([^/?#]+)", RegexOptions.Compiled);

    public DownloadHostType HostType => DownloadHostType.MailRu;

    public bool CanResolve(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return host == "cloud.mail.ru" ||
               host.EndsWith(".mail.ru");
    }

    public async Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));

        string directUrl = url;
        string suggestedFileName = "mailru_mod.honmod";

        Match match = PublicFolderRegex.Match(uri.AbsolutePath);
        if (match.Success)
        {
            string key1 = match.Groups[1].Value;
            string key2 = match.Groups[2].Value;
            string publicPath = $"{key1}/{key2}";
            suggestedFileName = $"mailru_{key2}.honmod";

            try
            {
                // Mail.Ru API v2 dispatcher query for public download node
                string apiUrl = $"https://cloud.mail.ru/api/v2/dispatcher?key={publicPath}";
                using HttpResponseMessage resp = await httpClient.GetAsync(apiUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("body", out JsonElement body) &&
                        body.TryGetProperty("get", out JsonElement getArr) &&
                        getArr.GetArrayLength() > 0)
                    {
                        string downloadHost = getArr[0].GetProperty("url").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(downloadHost))
                            directUrl = $"{downloadHost.TrimEnd('/')}/public/{publicPath}";
                    }
                }
            }
            catch
            {
                // Fallback to original URL
            }
        }

        return new ResolvedDownloadTarget
        {
            OriginalUrl = url,
            DirectDownloadUrl = directUrl,
            SuggestedFileName = suggestedFileName,
            HostType = DownloadHostType.MailRu
        };
    }
}
