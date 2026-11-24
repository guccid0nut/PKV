// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace Kongor.Modding.Harvester.Models;

/// <summary>
///     Root schema representing the full Trello board response from the public API or exported JSON.
/// </summary>
public class TrelloBoardResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")] public string Desc { get; set; } = string.Empty;

    [JsonPropertyName("closed")] public bool Closed { get; set; }

    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    [JsonPropertyName("shortUrl")] public string ShortUrl { get; set; } = string.Empty;

    [JsonPropertyName("lists")] public List<TrelloList> Lists { get; set; } = [];

    [JsonPropertyName("cards")] public List<TrelloCard> Cards { get; set; } = [];

    [JsonPropertyName("labels")] public List<TrelloLabel> Labels { get; set; } = [];
}

/// <summary>
///     Represents a Trello list (column) such as "Working mods", "Broken/Outdated mods", etc.
/// </summary>
public class TrelloList
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("closed")] public bool Closed { get; set; }

    [JsonPropertyName("idBoard")] public string IdBoard { get; set; } = string.Empty;

    [JsonPropertyName("pos")] public double Pos { get; set; }

    [JsonPropertyName("softLimit")] public object? SoftLimit { get; set; }
}

/// <summary>
///     Represents a Trello card corresponding to a community mod or info item.
/// </summary>
public class TrelloCard
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")] public string Desc { get; set; } = string.Empty;

    [JsonPropertyName("closed")] public bool Closed { get; set; }

    [JsonPropertyName("idList")] public string IdList { get; set; } = string.Empty;

    [JsonPropertyName("idBoard")] public string IdBoard { get; set; } = string.Empty;

    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    [JsonPropertyName("shortUrl")] public string ShortUrl { get; set; } = string.Empty;

    [JsonPropertyName("pos")] public double Pos { get; set; }

    [JsonPropertyName("dateLastActivity")] public DateTime? DateLastActivity { get; set; }

    [JsonPropertyName("labels")] public List<TrelloLabel> Labels { get; set; } = [];

    [JsonPropertyName("attachments")] public List<TrelloAttachment> Attachments { get; set; } = [];
}

/// <summary>
///     Represents an attachment associated with a Trello card.
/// </summary>
public class TrelloAttachment
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("bytes")] public long? Bytes { get; set; }

    [JsonPropertyName("date")] public DateTime? Date { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

    [JsonPropertyName("isUpload")] public bool IsUpload { get; set; }

    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
}

/// <summary>
///     Represents a label applied to a Trello card (e.g. "Works fine", "User Interface", "Broken").
/// </summary>
public class TrelloLabel
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("idBoard")] public string IdBoard { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")] public string? Color { get; set; }
}
