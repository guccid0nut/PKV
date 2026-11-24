// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Modding.Harvester.Models;

namespace Kongor.Modding.Harvester.Resolvers;

/// <summary>
///     Resolved direct download target with direct URL, suggested filename, and headers.
/// </summary>
public class ResolvedDownloadTarget
{
    public string OriginalUrl { get; set; } = string.Empty;
    public string DirectDownloadUrl { get; set; } = string.Empty;
    public string SuggestedFileName { get; set; } = string.Empty;
    public DownloadHostType HostType { get; set; } = DownloadHostType.Unknown;
    public Dictionary<string, string> CustomHeaders { get; set; } = [];
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
}

/// <summary>
///     Interface for host-specific download resolvers converting sharing links to direct binary streams.
/// </summary>
public interface IDownloadHostResolver
{
    DownloadHostType HostType { get; }
    bool CanResolve(string url);
    Task<ResolvedDownloadTarget> ResolveAsync(string url, HttpClient httpClient, CancellationToken ct = default);
}
