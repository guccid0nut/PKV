// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace Kongor.Modding.Harvester.Models;

/// <summary>
///     Identification of the remote host hosting a mod binary.
/// </summary>
public enum DownloadHostType
{
    Unknown,
    DirectHttp,
    Dropbox,
    GitHubRelease,
    MediaFire,
    GoogleDrive,
    MailRu,
    TrelloAttachment
}

/// <summary>
///     Individual download reference attached to or extracted from a mod card.
/// </summary>
public class DownloadUrlInfo
{
    [JsonPropertyName("source")] public string Source { get; set; } = "Markdown";

    [JsonPropertyName("name")] public string Name { get; set; } = "DownloadLink";

    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}

/// <summary>
///     Cross-referenced catalog representation of a community HoN mod harvested from Trello.
/// </summary>
public class CommunityModEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;

    [JsonPropertyName("author")] public string Author { get; set; } = "Unknown";

    [JsonPropertyName("version")] public string Version { get; set; } = "N/A";

    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    [JsonPropertyName("primaryDownloadUrl")]
    public string PrimaryDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("resolvedDownloadUrl")]
    public string ResolvedDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("allDownloadUrls")] public List<DownloadUrlInfo> AllDownloadUrls { get; set; } = [];

    [JsonPropertyName("fileSizeBytes")] public long? FileSizeBytes { get; set; }

    [JsonPropertyName("sha256Hash")] public string? Sha256Hash { get; set; }

    [JsonPropertyName("notes")] public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("trelloUrl")] public string TrelloUrl { get; set; } = string.Empty;

    [JsonPropertyName("desc")] public string Desc { get; set; } = string.Empty;

    [JsonPropertyName("isDownloadable")] public bool IsDownloadable => !string.IsNullOrWhiteSpace(PrimaryDownloadUrl);

    [JsonPropertyName("resolvedHost")] public DownloadHostType ResolvedHost { get; set; } = DownloadHostType.Unknown;
}
