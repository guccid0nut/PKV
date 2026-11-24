// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Composite multi-host download resolver supporting Dropbox, GitHub Releases, MediaFire,
///     Google Drive, Mail.ru, Trello attachments, and generic HTTP endpoints.
/// </summary>
public class MultiHostDownloadResolver(IEnumerable<IDownloadHostResolver>? resolvers = null)
{
    private readonly List<IDownloadHostResolver> _resolvers = resolvers?.ToList() ??
    [
        new DropboxResolver(),
        new GitHubReleaseResolver(),
        new GoogleDriveResolver(),
        new MediaFireResolver(),
        new MailRuResolver(),
        new TrelloAttachmentResolver(),
        new DirectHttpResolver()
    ];

    /// <summary>
    ///     Identifies the host type of the specified URL without network queries.
    /// </summary>
    public DownloadHostType IdentifyHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return DownloadHostType.Unknown;

        foreach (IDownloadHostResolver resolver in _resolvers)
            if (resolver is not DirectHttpResolver && resolver.CanResolve(url))
                return resolver.HostType;

        return _resolvers.OfType<DirectHttpResolver>().Any(r => r.CanResolve(url))
            ? DownloadHostType.DirectHttp
            : DownloadHostType.Unknown;
    }

    /// <summary>
    ///     Resolves a remote URL into a direct streaming download target.
    /// </summary>
    public async Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Download URL cannot be null or empty", nameof(url));

        HttpClient client = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();

        foreach (IDownloadHostResolver resolver in _resolvers)
            if (resolver.CanResolve(url))
                return await resolver.ResolveAsync(url, client, ct);

        throw new NotSupportedException($"No resolver available for URL: {url}");
    }
}
