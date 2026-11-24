// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Harvester.Resolvers;
using Kongor.Modding.Serialization;

namespace Kongor.Modding.Harvester.Scraper;

/// <summary>
///     Parser for the local community mods catalog manifest.
/// </summary>
public class TrelloBoardScraper(MultiHostDownloadResolver? resolver = null)
{
    private static readonly Regex AuthorRegex = new(@"(?:Made by|Author:?|by)\s+([A-Za-z0-9_\-\+]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionRegex =
        new(@"(?:v|\bv)?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarkdownLinkRegex =
        new(@"\[{1,2}([^\]]*)\]{1,2}\((https?://[^\)\s""]+)(?:[\s""][^\)]*)?\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectUrlRegex =
        new(@"https?://[^\s\)\""<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private readonly MultiHostDownloadResolver _resolver = resolver ?? new MultiHostDownloadResolver();

    /// <summary>
    ///     Loads the local community mods catalog manifest and parses all 99 community mods without external network calls.
    /// </summary>
    public async Task<List<CommunityModEntry>> ScrapeBoardAsync(string? customCatalogOrJsonPath = null,
        CancellationToken ct = default)
    {
        string? targetPath = !string.IsNullOrWhiteSpace(customCatalogOrJsonPath) && File.Exists(customCatalogOrJsonPath)
            ? customCatalogOrJsonPath
            : ResolveDefaultCatalogPath(FindRepoRoot());

        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            throw new FileNotFoundException(
                "Local community mod catalog file (data/mods/trello_mods_catalog.json) was not found. Run 'dotnet test tests/unit/extensions/Kongor.Modding.Tests' after verifying data/mods/trello_mods_catalog.json exists.");
        }

        string jsonContent = await File.ReadAllTextAsync(targetPath, ct);
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            throw new InvalidOperationException($"Community mod catalog file at '{targetPath}' is empty.");
        }

        return ParseBoardJson(jsonContent);
    }

    /// <summary>
    ///     Traverses directory hierarchy upwards to locate the repository root.
    /// </summary>
    public static string? FindRepoRoot(string? startDir = null)
    {
        string? dir = startDir ?? AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(dir, "NEXUS.slnx")) ||
                Directory.Exists(Path.Combine(dir, ".agents")))
                return dir;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir || string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }

        return null;
    }

    /// <summary>
    ///     Resolves the bundled community mod catalog path across active and archived workspace locations.
    /// </summary>
    public static string? ResolveDefaultCatalogPath(string? repoRoot = null)
    {
        repoRoot ??= FindRepoRoot();
        if (repoRoot != null)
        {
            string[] candidates =
            [
                Path.Combine(repoRoot, "data", "mods", "trello_mods_catalog.json"),
                Path.Combine(repoRoot, ".agents", "teamwork_preview_explorer_survey_3", "trello_mods_catalog.json"),
                Path.Combine(repoRoot, ".agents", "_archive", "teamwork_preview_explorer_survey_3", "trello_mods_catalog.json"),
                Path.Combine(repoRoot, "trello_community_catalog.json"),
                Path.Combine(repoRoot, "scratch", "trello_community_catalog.json")
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        string cwdCatalog = Path.Combine(Directory.GetCurrentDirectory(), "trello_mods_catalog.json");
        return File.Exists(cwdCatalog) ? cwdCatalog : null;
    }

    /// <summary>
    ///     Parses Trello JSON (either full Board schema or Survey-3 catalog format) into normalized CommunityModEntry objects.
    /// </summary>
    public List<CommunityModEntry> ParseBoardJson(string json)
    {
        JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        // Check if this is already an array of pre-scraped CommunityModEntry
        try
        {
            List<CommunityModEntry>? preScraped =
                JsonSerializer.Deserialize(json, ModdingJsonSerializerContext.Default.ListCommunityModEntry);
            if (preScraped != null && preScraped.Count > 0 && !string.IsNullOrWhiteSpace(preScraped[0].Category))
            {
                EnrichCommunityModEntries(preScraped);
                return preScraped;
            }
        }
        catch
        {
            // Not a pre-scraped array, continue with full Trello Board schema
        }

        TrelloBoardResponse board = JsonSerializer.Deserialize(json, ModdingJsonSerializerContext.Default.TrelloBoardResponse)
                                    ?? throw new InvalidOperationException(
                                        "Failed to deserialize Trello board response JSON.");

        Dictionary<string, string> listMap = board.Lists.ToDictionary(l => l.Id, l => l.Name);
        List<CommunityModEntry> entries = [];

        foreach (TrelloCard card in board.Cards)
        {
            if (card.Closed) continue;

            string listName = listMap.TryGetValue(card.IdList, out string? name) ? name : "Unknown";

            // Ignore meta/info lists if desired, but capture all 6 functional mod lists:
            // "Working mods", "Broken/Outdated mods", "Incompatible mods", "Pending...", "Old sources", "In develop"
            if (listName.Equals("General Info", StringComparison.OrdinalIgnoreCase)) continue;

            CommunityModEntry entry = ParseCard(card, listName);
            entries.Add(entry);
        }

        EnrichCommunityModEntries(entries);
        return entries;
    }

    /// <summary>
    ///     Parses an individual Trello card into a CommunityModEntry.
    /// </summary>
    public CommunityModEntry ParseCard(TrelloCard card, string category)
    {
        string desc = card.Desc ?? string.Empty;
        string cardName = card.Name.Trim();

        // Extract Author
        string author = "Unknown";
        Match authorMatch = AuthorRegex.Match(desc);
        if (authorMatch.Success)
        {
            author = authorMatch.Groups[1].Value.Trim();
        }
        else
        {
            Match titleAuthorMatch = AuthorRegex.Match(cardName);
            if (titleAuthorMatch.Success) author = titleAuthorMatch.Groups[1].Value.Trim();
        }

        // Extract Version
        string version = "N/A";
        Match versionMatch = VersionRegex.Match(cardName);
        if (versionMatch.Success)
        {
            version = versionMatch.Groups[1].Value.Trim();
        }
        else
        {
            Match descVerMatch = VersionRegex.Match(desc);
            if (descVerMatch.Success) version = descVerMatch.Groups[1].Value.Trim();
        }

        // Extract Status / Labels
        List<string> labelNames =
            card.Labels?.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
        string status = labelNames.Count > 0 ? string.Join(", ", labelNames) : "Unlabeled";

        // Extract Download Links
        List<DownloadUrlInfo> downloadUrls = [];

        // 1. Check card attachments
        if (card.Attachments != null)
            foreach (TrelloAttachment att in card.Attachments)
            {
                if (string.IsNullOrWhiteSpace(att.Url)) continue;
                string ext = Path.GetExtension(att.Name ?? att.Url);
                if (ImageExtensions.Contains(ext)) continue;

                downloadUrls.Add(new DownloadUrlInfo
                {
                    Source = "TrelloAttachment",
                    Name = att.Name ?? "Attachment",
                    Url = att.Url
                });
            }

        // 2. Check Markdown links
        foreach (Match match in MarkdownLinkRegex.Matches(desc))
        {
            string linkText = match.Groups[1].Value.Trim();
            string linkUrl = match.Groups[2].Value.Trim();

            if (IsImageOrScreenshotUrl(linkUrl, linkText)) continue;

            downloadUrls.Add(new DownloadUrlInfo
            {
                Source = "Markdown",
                Name = string.IsNullOrWhiteSpace(linkText) ? "DownloadLink" : linkText,
                Url = linkUrl
            });
        }

        // 3. Direct URLs in description if no markdown links matched
        if (downloadUrls.Count == 0)
            foreach (Match match in DirectUrlRegex.Matches(desc))
            {
                string rawUrl = match.Value.Trim();
                if (IsImageOrScreenshotUrl(rawUrl, "")) continue;

                downloadUrls.Add(new DownloadUrlInfo
                {
                    Source = "DirectUrl",
                    Name = "DownloadLink",
                    Url = rawUrl
                });
            }

        // Pick primary download URL
        string primaryUrl = SelectPrimaryDownloadUrl(downloadUrls);

        return new CommunityModEntry
        {
            Id = card.Id,
            Name = cardName,
            Category = category,
            Author = author,
            Version = version,
            Status = status,
            PrimaryDownloadUrl = primaryUrl,
            AllDownloadUrls = downloadUrls,
            TrelloUrl = card.Url ?? $"https://trello.com/c/{card.ShortUrl}",
            Desc = desc
        };
    }

    private static bool IsImageOrScreenshotUrl(string url, string linkText)
    {
        if (linkText.Contains("screenshot", StringComparison.OrdinalIgnoreCase) ||
            linkText.Contains("image", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string ext = Path.GetExtension(uri.AbsolutePath);
            if (ImageExtensions.Contains(ext)) return true;
        }

        return false;
    }

    private static string SelectPrimaryDownloadUrl(List<DownloadUrlInfo> urls)
    {
        if (urls.Count == 0) return string.Empty;

        // Prefer .honmod, .s2z, .zip
        foreach (DownloadUrlInfo u in urls)
            if (u.Url.EndsWith(".honmod", StringComparison.OrdinalIgnoreCase) ||
                u.Url.Contains(".honmod?", StringComparison.OrdinalIgnoreCase) ||
                u.Url.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) ||
                u.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return u.Url;

        // Prefer Dropbox, GitHub, MediaFire, GDrive, MailRu
        foreach (DownloadUrlInfo u in urls)
        {
            string lower = u.Url.ToLowerInvariant();
            if (lower.Contains("dropbox.com") ||
                lower.Contains("github.com") ||
                lower.Contains("mediafire.com") ||
                lower.Contains("drive.google.com") ||
                lower.Contains("cloud.mail.ru"))
                return u.Url;
        }

        return urls[0].Url;
    }

    private void EnrichCommunityModEntries(List<CommunityModEntry> entries)
    {
        foreach (CommunityModEntry entry in entries)
            if (!string.IsNullOrWhiteSpace(entry.PrimaryDownloadUrl))
                entry.ResolvedHost = _resolver.IdentifyHost(entry.PrimaryDownloadUrl);
            else
                entry.ResolvedHost = DownloadHostType.Unknown;
    }
}

