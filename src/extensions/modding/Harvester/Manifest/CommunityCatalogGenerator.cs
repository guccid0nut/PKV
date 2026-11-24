// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.Json;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Serialization;

namespace Kongor.Modding.Harvester.Manifest;

/// <summary>
///     Manifest generator producing structured JSON and formatted Markdown community mod catalogs.
/// </summary>
public class CommunityCatalogGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     Generates a JSON string representing the complete community mod catalog.
    /// </summary>
    public string GenerateJson(IEnumerable<CommunityModEntry> entries)
    {
        return JsonSerializer.Serialize(entries, ModdingJsonSerializerContext.Default.IEnumerableCommunityModEntry);
    }

    /// <summary>
    ///     Generates a rich Markdown document cataloging all community mods with categories,
    ///     direct download URLs, author metadata, sizes, and SHA-256 checksums.
    /// </summary>
    public string GenerateMarkdown(IEnumerable<CommunityModEntry> entries, HarvestResult? harvestResult = null)
    {
        List<CommunityModEntry> list = [.. entries];
        StringBuilder sb = new();

        sb.AppendLine("# HoN Community Mods Repository — Master Catalog Manifest");
        sb.AppendLine();
        sb.AppendLine(
            "> **Source**: HoN Mods Repository Trello Board (`https://trello.com/b/vOSfrxj8/hon-mods-repository`)  ");
        sb.AppendLine($"> **Generated**: {TimeProvider.System.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"> **Total Cataloged Mods**: **{list.Count}**  ");
        sb.AppendLine($"> **Directly Downloadable Mods**: **{list.Count(m => m.IsDownloadable)}**  ");
        if (harvestResult != null)
            sb.AppendLine(
                $"> **Harvest Status**: {harvestResult.DownloadedMods.Count} downloaded ({harvestResult.TotalCached} cached), {harvestResult.FailedMods.Count} failed");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Category Summary Table
        sb.AppendLine("## Category Summary");
        sb.AppendLine();
        sb.AppendLine("| Category | Mod Count | Downloadable | Primary Status |");
        sb.AppendLine("|---|---|---|---|");

        IEnumerable<IGrouping<string, CommunityModEntry>> categories =
            list.GroupBy(m => m.Category).OrderBy(g => GetCategoryOrder(g.Key));
        foreach (IGrouping<string, CommunityModEntry> group in categories)
        {
            int count = group.Count();
            int downloadable = group.Count(m => m.IsDownloadable);
            string sampleStatus = group.First().Category switch
            {
                "Working mods" => "✅ Functional",
                "Broken/Outdated mods" => "⚠️ Outdated / Requires M4 Repair",
                "Incompatible mods" => "❌ Incompatible / Needs Rewrite",
                "Pending..." => "⏳ Submissions Pending",
                "Old sources" => "📦 Historical Archives",
                "In develop" => "🔨 Prototype / Unreleased",
                _ => "ℹ️ Other"
            };
            sb.AppendLine($"| **{group.Key}** | {count} | {downloadable} | {sampleStatus} |");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Category Detailed Sections
        foreach (IGrouping<string, CommunityModEntry> group in categories)
        {
            sb.AppendLine($"## {group.Key} ({group.Count()} Mods)");
            sb.AppendLine();
            sb.AppendLine(
                "| Mod Name | Author | Version | Status | Size | SHA-256 | Direct Download URL | Description |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");

            foreach (CommunityModEntry mod in group.OrderBy(m => m.Name))
            {
                string name = EscapeMarkdown(mod.Name);
                string author = EscapeMarkdown(mod.Author);
                string version = EscapeMarkdown(mod.Version);
                string status = EscapeMarkdown(mod.Status);
                string sizeStr = mod.FileSizeBytes.HasValue ? FormatFileSize(mod.FileSizeBytes.Value) : "N/A";
                string shaStr = !string.IsNullOrWhiteSpace(mod.Sha256Hash)
                    ? $"`{mod.Sha256Hash[..Math.Min(8, mod.Sha256Hash.Length)]}...`"
                    : "N/A";

                string downloadLink;
                if (!string.IsNullOrWhiteSpace(mod.PrimaryDownloadUrl))
                {
                    string linkLabel = mod.ResolvedHost switch
                    {
                        DownloadHostType.Dropbox => "Dropbox",
                        DownloadHostType.GitHubRelease => "GitHub",
                        DownloadHostType.MediaFire => "MediaFire",
                        DownloadHostType.GoogleDrive => "GDrive",
                        DownloadHostType.MailRu => "Mail.ru",
                        DownloadHostType.TrelloAttachment => "Trello",
                        _ => "Direct"
                    };
                    downloadLink = $"[{linkLabel}]({mod.PrimaryDownloadUrl})";
                }
                else
                {
                    downloadLink = "*None*";
                }

                string desc = EscapeMarkdown(Truncate(mod.Desc, 60));

                sb.AppendLine(
                    $"| **{name}** | {author} | {version} | {status} | {sizeStr} | {shaStr} | {downloadLink} | {desc} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Writes JSON and Markdown manifest files to the designated paths.
    /// </summary>
    public async Task SaveManifestsAsync(
        IEnumerable<CommunityModEntry> entries,
        string outputJsonPath,
        string outputMdPath,
        HarvestResult? harvestResult = null,
        CancellationToken ct = default)
    {
        string? jsonDir = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrWhiteSpace(jsonDir)) Directory.CreateDirectory(jsonDir);

        string? mdDir = Path.GetDirectoryName(outputMdPath);
        if (!string.IsNullOrWhiteSpace(mdDir)) Directory.CreateDirectory(mdDir);

        string json = GenerateJson(entries);
        await File.WriteAllTextAsync(outputJsonPath, json, Encoding.UTF8, ct);

        string md = GenerateMarkdown(entries, harvestResult);
        await File.WriteAllTextAsync(outputMdPath, md, Encoding.UTF8, ct);
    }

    private static int GetCategoryOrder(string category)
    {
        return category switch
        {
            "Working mods" => 1,
            "Broken/Outdated mods" => 2,
            "Incompatible mods" => 3,
            "Pending..." => 4,
            "Old sources" => 5,
            "In develop" => 6,
            _ => 99
        };
    }

    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string Truncate(string str, int max)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        string s = str.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
