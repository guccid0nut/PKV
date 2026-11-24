// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Utilities;

namespace KONGOR.MasterServer.Models.RequestResponse.GameData;

public partial class GuideResponseSuccess(
    HeroGuide guide,
    int hostTime,
    bool isDefault = false,
    bool isFavorite = false)
{
    [PHPProperty("errors")] public string Errors => string.Empty;

    [PHPProperty("success")] public int Success => 1;

    [PHPProperty("datetime")]
    public DateTimeOffset TimestampCreated { get; set; } = guide.TimestampLastUpdated ?? guide.TimestampCreated;

    [PHPProperty("author_name")]
    public string AuthorName { get; set; } = SanitizeText(guide.Author?.Name ?? "Anonymous", 32);

    [PHPProperty("hero_cli_name")] public string HeroIdentifier { get; set; } = SanitizeText(guide.HeroIdentifier, 50);

    [PHPProperty("guide_name")] public string Name { get; set; } = SanitizeText(guide.Name, 50);

    [PHPProperty("hero_name")] public string HeroName { get; set; } = SanitizeText(guide.HeroName, 32);

    [PHPProperty("default")] public int Default => isDefault ? 1 : 0;

    [PHPProperty("favorite")] public int Favorite => isFavorite ? 1 : 0;

    [PHPProperty("rating")] public float Rating { get; set; } = guide.Rating;

    [PHPProperty("thumb")] public string Thumb => guide.UpVotes is 0 ? "noVote" : guide.UpVotes.ToString();

    [PHPProperty("vote")] public int Vote { get; set; } = 0;

    [PHPProperty("premium")] public bool Featured { get; set; } = guide.Featured;

    [PHPProperty("i_start")] public string StartingItems { get; set; } = NormalizeItems(guide.StartingItems);

    [PHPProperty("i_laning")] public string EarlyGameItems { get; set; } = NormalizeItems(guide.EarlyGameItems);

    [PHPProperty("i_core")] public string CoreItems { get; set; } = NormalizeItems(guide.CoreItems);

    [PHPProperty("i_luxury")] public string LuxuryItems { get; set; } = NormalizeItems(guide.LuxuryItems);

    [PHPProperty("abilQ")]
    public string AbilityQueue { get; set; } = NormalizeAbilityQueue(guide.AbilityQueue, guide.HeroIdentifier);

    [PHPProperty("txt_intro")] public string Intro { get; set; } = SanitizeText(guide.Intro, 500);

    [PHPProperty("txt_guide")] public string Content { get; set; } = SanitizeText(guide.Content, 1500);

    [PHPProperty("hosttime")] public int HostTime { get; set; } = hostTime;

    [PHPProperty("vested_threshold")] public int VestedThreshold => 5;

    [PHPProperty(0)] public bool Zero => true;

    private static string SanitizeText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        Span<char> buffer = stackalloc char[Math.Min(text.Length, maxLength)];
        int written = 0;
        foreach (char c in text)
        {
            if (written >= buffer.Length) break;
            if (c is '\'' or '`' or '\0' or < (char)32 && c is not '\t' and not '\r' and not '\n')
                continue;
            buffer[written++] = c;
        }

        return new string(buffer[..written]).Trim();
    }

    private static string NormalizeItems(string? rawItems)
    {
        if (string.IsNullOrWhiteSpace(rawItems)) return string.Empty;

        string[] tokens = rawItems
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<string> normalized =
        [
            .. tokens
                .Where(token => !string.IsNullOrWhiteSpace(token) && token.Length <= 64)
                .Select(token => HonItemMapping.ToEngineEntityName(token.Trim()))
                .Where(engineEntity => !string.IsNullOrWhiteSpace(engineEntity))
                .Take(10)
        ];

        return string.Join('|', normalized);
    }

    private static string NormalizeAbilityQueue(string? rawQueue, string? heroIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawQueue)) return string.Empty;

        string heroCode = (heroIdentifier ?? string.Empty).Replace("Hero_", "").Trim();
        if (heroCode.Length > 32) heroCode = heroCode[..32];

        string[] tokens = rawQueue
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<string> normalized = [];
        foreach (string token in tokens)
        {
            if (normalized.Count >= 25) break;
            if (string.IsNullOrWhiteSpace(token)) continue;

            string ab = token.Trim();
            if (ab.Length > 48) ab = ab[..48];

            if (ab == "1" || ab.Equals("Ability_1", StringComparison.OrdinalIgnoreCase) ||
                ab.Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(!string.IsNullOrEmpty(heroCode) ? $"Ability_{heroCode}1" : "Ability_1");
            }
            else if (ab == "2" || ab.Equals("Ability_2", StringComparison.OrdinalIgnoreCase) ||
                     ab.Equals("W", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(!string.IsNullOrEmpty(heroCode) ? $"Ability_{heroCode}2" : "Ability_2");
            }
            else if (ab == "3" || ab.Equals("Ability_3", StringComparison.OrdinalIgnoreCase) ||
                     ab.Equals("E", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(!string.IsNullOrEmpty(heroCode) ? $"Ability_{heroCode}3" : "Ability_3");
            }
            else if (ab == "4" || ab.Equals("Ability_4", StringComparison.OrdinalIgnoreCase) ||
                     ab.Equals("R", StringComparison.OrdinalIgnoreCase) ||
                     ab.Equals("Ultimate", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(!string.IsNullOrEmpty(heroCode) ? $"Ability_{heroCode}4" : "Ability_4");
            }
            else if (ab == "5" || ab.Contains("stat", StringComparison.OrdinalIgnoreCase) ||
                     ab.Contains("boost", StringComparison.OrdinalIgnoreCase) || ab.Contains('+') ||
                     ab.Equals("Ability_AttributeBoost", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add("Ability_AttributeBoost");
            }
            else if (ab.StartsWith("Ability_", StringComparison.OrdinalIgnoreCase))
            {
                if (ab.Equals("Ability_1", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(heroCode))
                    normalized.Add($"Ability_{heroCode}1");
                else if (ab.Equals("Ability_2", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(heroCode))
                    normalized.Add($"Ability_{heroCode}2");
                else if (ab.Equals("Ability_3", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(heroCode))
                    normalized.Add($"Ability_{heroCode}3");
                else if (ab.Equals("Ability_4", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(heroCode))
                    normalized.Add($"Ability_{heroCode}4");
                else
                    normalized.Add(ab);
            }
            else
            {
                normalized.Add(!string.IsNullOrEmpty(heroCode) ? $"Ability_{heroCode}{ab}" : $"Ability_{ab}");
            }
        }

        return string.Join('|', normalized);
    }
}
