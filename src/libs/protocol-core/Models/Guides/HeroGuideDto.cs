// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Models.Guides;

public sealed class HeroGuideDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string HeroIdentifier { get; set; } = string.Empty;
    public string TargetMap { get; set; } = "all";
    public string Intro { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string StartingItems { get; set; } = string.Empty;
    public string EarlyGameItems { get; set; } = string.Empty;
    public string CoreItems { get; set; } = string.Empty;
    public string LuxuryItems { get; set; } = string.Empty;
    public string AbilityQueue { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public int AuthorAccountId { get; set; }
    public float Rating { get; set; }
    public int UpVotes { get; set; }
    public int DownVotes { get; set; }
    public bool IsPublic { get; set; } = true;
    public bool IsFeatured { get; set; }
    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset? TimestampLastUpdated { get; set; }
}

public sealed class SaveHeroGuideRequest
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HeroIdentifier { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string TargetMap { get; set; } = "all";
    public string Intro { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string StartingItems { get; set; } = string.Empty;
    public string EarlyGameItems { get; set; } = string.Empty;
    public string CoreItems { get; set; } = string.Empty;
    public string LuxuryItems { get; set; } = string.Empty;
    public string AbilityQueue { get; set; } = string.Empty;
    public bool IsPublic { get; set; } = true;
}

public sealed class GuideExportPackage
{
    public string Version { get; set; } = "1.0";
    public string ExportedAt { get; set; } = TimeProvider.System.GetUtcNow().ToString("o");
    public string Generator { get; set; } = "DAWNBRINGER Guide Studio";
    public string HeroIdentifier { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string GuideName { get; set; } = string.Empty;
    public string TargetMap { get; set; } = "all";
    public string Intro { get; set; } = string.Empty;
    public string DetailedDescription { get; set; } = string.Empty;
    public string StartingItems { get; set; } = string.Empty;
    public string EarlyGameItems { get; set; } = string.Empty;
    public string CoreItems { get; set; } = string.Empty;
    public string LuxuryItems { get; set; } = string.Empty;
    public string AbilityQueue { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
}

public sealed class HeroOptionDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CleanId { get; set; } = string.Empty;
}
