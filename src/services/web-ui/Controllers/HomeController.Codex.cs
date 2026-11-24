// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using System.Text.Json;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Models;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public class HeroPerformanceDto
{
    public required string HeroCode { get; set; }
    public string? HeroIdentifier { get; set; }
    public required int MatchesPlayed { get; set; }
    public required double WinRate { get; set; }
}

public partial class HomeController
{
    private static List<HeroDetailDto>? _cachedHeroDetails;
    private static List<HeroGuideDto>? _cachedDiskHeroGuides;
    private static readonly Lock _heroDataLock = new();

    public static void InvalidateHeroDataCache()
    {
        lock (_heroDataLock)
        {
            _cachedHeroItems = null;
            _cachedHeroDetails = null;
            _cachedDiskHeroGuides = null;
        }
    }

    private static List<HeroGuideDto> LoadDiskHeroGuidesForHero(string heroIdentifier, ILogger? logger = null)
    {
        List<HeroGuideDto>? allDiskGuides = _cachedDiskHeroGuides;
        if (allDiskGuides == null)
        {
            string[] candidateSubpaths =
            [
                Path.Combine("src", "libs", "database-context", "Data", "HeroGuides.json"),
                Path.Combine("Data", "HeroGuides.json"),
                "HeroGuides.json"
            ];

            string? foundPath = null;
            foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                DirectoryInfo? dir = new(root);
                while (dir != null && foundPath == null)
                {
                    foreach (string sub in candidateSubpaths)
                    {
                        string probe = Path.Combine(dir.FullName, sub);
                        if (System.IO.File.Exists(probe))
                        {
                            foundPath = probe;
                            break;
                        }
                    }
                    dir = dir.Parent;
                }
                if (foundPath != null) break;
            }

            List<HeroGuideDto> parsed = [];
            if (foundPath != null)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(foundPath));
                    foreach (JsonElement el in doc.RootElement.EnumerateArray())
                    {
                        static string JoinArr(JsonElement parent, string propName)
                        {
                            if (!parent.TryGetProperty(propName, out JsonElement prop)) return string.Empty;
                            if (prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? string.Empty;
                            if (prop.ValueKind != JsonValueKind.Array) return string.Empty;
                            return string.Join("|", prop.EnumerateArray()
                                .Select(x => x.GetString()?.Trim() ?? string.Empty)
                                .Where(s => !string.IsNullOrEmpty(s)));
                        }

                        parsed.Add(new HeroGuideDto
                        {
                            ID = el.TryGetProperty("guideID", out JsonElement gid) ? gid.GetInt32() : 0,
                            Name = el.TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? string.Empty : string.Empty,
                            HeroName = el.TryGetProperty("heroName", out JsonElement hn) ? hn.GetString() : null,
                            HeroIdentifier = el.TryGetProperty("heroIdentifier", out JsonElement hi) ? hi.GetString() ?? string.Empty : string.Empty,
                            Intro = el.TryGetProperty("intro", out JsonElement intr) ? intr.GetString() : null,
                            Content = el.TryGetProperty("content", out JsonElement cnt) ? cnt.GetString() : null,
                            StartingItems = JoinArr(el, "startingItems"),
                            EarlyGameItems = JoinArr(el, "earlyGameItems"),
                            CoreItems = JoinArr(el, "coreItems"),
                            LuxuryItems = JoinArr(el, "luxuryItems"),
                            AbilityQueue = JoinArr(el, "abilityQueue"),
                            Public = !el.TryGetProperty("public", out JsonElement pub) || pub.GetBoolean(),
                            Official = el.TryGetProperty("featured", out JsonElement feat) && feat.GetBoolean(),
                            Rating = el.TryGetProperty("rating", out JsonElement rat) ? (float)rat.GetDouble() : 0f,
                            Upvotes = el.TryGetProperty("upVotes", out JsonElement upv) ? upv.GetInt32() : 0
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[HomeController.Codex] Error loading fallback HeroGuides.json from {Path}", foundPath);
                }
            }

            lock (_heroDataLock)
            {
                _cachedDiskHeroGuides ??= parsed;
                allDiskGuides = _cachedDiskHeroGuides;
            }
        }

        return allDiskGuides
            .Where(g => string.Equals(g.HeroIdentifier, heroIdentifier, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Featured)
            .ThenByDescending(g => g.Upvotes)
            .ToList();
    }

    private static readonly Dictionary<string, string> _abilityLocalizationCache = LoadAbilityLocalization();
    private static readonly Dictionary<string, List<HeroAbilityRule>> _heroAbilityRulesCache = LoadHeroAbilityRules();

    private async Task<List<HeroDetailDto>> GetCachedHeroDetailsAsync(CancellationToken cancellationToken = default)
    {
        if (_hybridCache != null)
        {
            List<HeroDetailDto>? cached = await _hybridCache.GetOrCreateAsync(
                "dawn:heroes:details",
                async ct => await LoadHeroDetailsFromDbOrDiskAsync(ct),
                tags: ["heroes", "codex"],
                l1Expiration: TimeSpan.FromMinutes(15),
                l2Expiration: TimeSpan.FromHours(1),
                cancellationToken: cancellationToken
            );

            if (cached != null)
            {
                return cached;
            }
        }

        return await LoadHeroDetailsFromDbOrDiskAsync(cancellationToken);
    }

    private static string? FindHeroDetailsJsonPath()
    {
        string[] candidateSubpaths =
        [
            Path.Combine("src", "libs", "database-context", "Data", "HeroDetails.json"),
            Path.Combine("Data", "HeroDetails.json"),
            "HeroDetails.json"
        ];

        string[] roots = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            DirectoryInfo? dir = new(root);
            while (dir != null)
            {
                foreach (string subpath in candidateSubpaths)
                {
                    string probe = Path.Combine(dir.FullName, subpath);
                    if (System.IO.File.Exists(probe)) return probe;
                }
                dir = dir.Parent;
            }
        }

        return null;
    }

    private async Task<List<HeroDetailDto>> LoadHeroDetailsFromDbOrDiskAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedHeroDetails != null && _cachedHeroDetails.Count >= 139)
        {
            return _cachedHeroDetails;
        }

        List<HeroDetailDto> res = [];
        try
        {
            IReadOnlyList<HeroDetailDto> list = await _playerStatsClient.GetAllHeroDetailsAsync(cancellationToken);
            res = [.. list];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HomeController] Error fetching hero details from client API");
        }

        if (res.Count < 139)
        {
            string? foundPath = FindHeroDetailsJsonPath();
            if (foundPath != null)
            {
                try
                {
                    string json = await System.IO.File.ReadAllTextAsync(foundPath, cancellationToken);
                    List<HeroDetail>? diskHeroes = JsonSerializer.Deserialize<List<HeroDetail>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (diskHeroes != null && diskHeroes.Count > 0)
                    {
                        HashSet<string> seenCodes = new(res.Select(h => h.HeroCode), StringComparer.OrdinalIgnoreCase);
                        foreach (HeroDetail h in diskHeroes)
                        {
                            if (!seenCodes.Contains(h.HeroCode))
                            {
                                seenCodes.Add(h.HeroCode);
                                res.Add(h.ToDto());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HomeController] Error loading fallback hero details from {Path}", foundPath);
                }
            }
        }

        if (res.Count > 0)
        {
            lock (_heroDataLock)
            {
                _cachedHeroDetails = res;
            }
        }

        return res;
    }

    [HttpGet("heroes")]
    [HttpGet("codex")]
    [HttpGet("Home/Codex")]
    public async Task<IActionResult> Codex(CancellationToken cancellationToken = default)
    {
        List<HeroDetailDto> dbHeroes = await GetCachedHeroDetailsAsync(cancellationToken);
        return View("Codex", dbHeroes);
    }

    [HttpGet("codex/{heroIdentifier}")]
    [HttpGet("hero/{heroIdentifier}")]
    [HttpGet("Home/HeroDetail/{heroIdentifier}")]
    public async Task<IActionResult> HeroDetail(string heroIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(heroIdentifier))
        {
            return RedirectToAction("Codex");
        }

        List<string> allHeroIds = Heroes.AllHeroIdentifiers();
        string? matchedId = allHeroIds.FirstOrDefault(id =>
            id.Equals(heroIdentifier, StringComparison.OrdinalIgnoreCase) ||
            id.Replace("Hero_", "").Equals(heroIdentifier, StringComparison.OrdinalIgnoreCase) ||
            Heroes.ResolveHeroDisplayName(id).Replace(" ", "")
                .Equals(heroIdentifier.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

        if (matchedId == null)
        {
            HeroDetailDto? fallbackDbHero =
                await _playerStatsClient.GetHeroDetailAsync(heroIdentifier, cancellationToken);
            if (fallbackDbHero != null)
            {
                matchedId = fallbackDbHero.HeroCode;
            }
        }

        if (matchedId == null)
        {
            return RedirectToAction("Codex");
        }

        string displayName = Heroes.ResolveHeroDisplayName(matchedId);
        string cleanId = matchedId.ToLowerInvariant();
        string rawFolder = matchedId.Replace("Hero_", "").ToLowerInvariant();

        int currentIndex = allHeroIds.IndexOf(matchedId);
        string prevId = allHeroIds[(currentIndex - 1 + allHeroIds.Count) % allHeroIds.Count];
        string nextId = allHeroIds[(currentIndex + 1) % allHeroIds.Count];

        HeroMetaDto meta = GetHeroMetadata(rawFolder, cleanId);

        List<HeroGuideDto> allGuides =
            [.. await _guideApiClient.GetGuidesForHeroAsync(matchedId, cancellationToken)];
        if (!allGuides.Any(g => g.Featured))
        {
            List<HeroGuideDto> diskGuides = LoadDiskHeroGuidesForHero(matchedId, _logger);
            foreach (HeroGuideDto dg in diskGuides)
            {
                if (!allGuides.Any(existing => string.Equals(existing.Name, dg.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    allGuides.Add(dg);
                }
            }

            allGuides = allGuides
                .OrderByDescending(g => g.Featured)
                .ThenByDescending(g => g.Upvotes)
                .ToList();
        }
        ViewBag.AllGuides = allGuides;

        HeroDetailDto? heroDetailEntity = await _playerStatsClient.GetHeroDetailAsync(matchedId, cancellationToken);
        ViewData["HeroDetailEntity"] = heroDetailEntity;

        List<AbilityDto> abilities = GetHeroAbilities(matchedId, rawFolder, displayName);

        string ign = User?.Identity?.Name ?? string.Empty;
        if (!string.IsNullOrEmpty(ign))
        {
            AccountDto? account = await _playerStatsClient.GetAccountByIgnAsync(ign, cancellationToken);
            if (account != null)
            {
                ViewBag.CurrentAccount = account;
            }
        }

        IReadOnlyList<HeroDifficultyVoteDto> heroVotes =
            await _guideApiClient.GetHeroDifficultyVotesAsync(matchedId, cancellationToken);
        double? communityAvg = heroVotes.Count > 0 ? heroVotes.Average(v => (double)v.Rating) : null;
        double communityRounded = communityAvg.HasValue ? Math.Round(communityAvg.Value * 2) / 2 : 0;

        int userVote = 0;
        if (!string.IsNullOrEmpty(ign))
        {
            AccountDto? acc = await _playerStatsClient.GetAccountByIgnAsync(ign, cancellationToken);
            HeroDifficultyVoteDto? myVote = acc != null ? heroVotes.FirstOrDefault(v => v.AccountID == acc.ID) : null;
            if (myVote != null)
            {
                userVote = myVote.Rating;
            }
        }

        ViewData["HeroIdentifier"] = matchedId;
        ViewData["DisplayName"] = displayName;
        ViewData["IconName"] = matchedId.ToLowerInvariant();
        ViewData["Guide"] = allGuides.FirstOrDefault();
        ViewData["Abilities"] = abilities;
        ViewData["Meta"] = meta;
        ViewData["PrevHero"] = prevId;
        ViewData["NextHero"] = nextId;
        ViewData["PrevId"] = prevId;
        ViewData["NextId"] = nextId;
        ViewData["PrevName"] = Heroes.ResolveHeroDisplayName(prevId);
        ViewData["NextName"] = Heroes.ResolveHeroDisplayName(nextId);
        ViewData["CommunityDifficulty"] = communityRounded;
        ViewData["DifficultyVoteCount"] = heroVotes.Count;
        ViewData["UserDifficultyVote"] = userVote;

        return View();
    }

    [HttpPost("api/hero/vote-difficulty")]
    public async Task<IActionResult> VoteDifficulty([FromBody] VoteDifficultyRequest req,
        CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { error = "You must be logged in to vote on hero difficulty." });
        }

        string ign = User.Identity.Name!;
        HeroDifficultyVoteResultDto result =
            await _guideApiClient.VoteHeroDifficultyAsync(req.HeroIdentifier, ign, req.Difficulty, cancellationToken);
        return Json(new
        {
            success = result.Success,
            averageDifficulty = result.AverageDifficulty,
            totalVotes = result.TotalVotes,
            userVote = req.Difficulty
        });
    }

    [HttpGet("api/heroes")]
    [HttpGet("api/v1/heroes")]
    public async Task<IActionResult> GetHeroes(CancellationToken cancellationToken = default)
    {
        List<HeroDetailDto> heroes = await GetCachedHeroDetailsAsync(cancellationToken);
        return Json(heroes);
    }

    [HttpGet("api/heroes/{heroIdentifier}/abilities")]
    [HttpGet("api/v1/heroes/{heroIdentifier}/abilities")]
    public async Task<IActionResult> GetHeroAbilitiesEndpoint(string heroIdentifier, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        string displayName = Heroes.ResolveHeroDisplayName(heroIdentifier);
        string rawFolder = heroIdentifier.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        List<AbilityDto> abilities = GetHeroAbilities(heroIdentifier, rawFolder, displayName);
        return await Task.FromResult<IActionResult>(Json(abilities));
    }

    public IActionResult GetHeroAbilitiesEndpoint(string heroIdentifier)
    {
        string displayName = Heroes.ResolveHeroDisplayName(heroIdentifier);
        string rawFolder = heroIdentifier.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        List<AbilityDto> abilities = GetHeroAbilities(heroIdentifier, rawFolder, displayName);
        return Json(abilities);
    }

    private static HeroMetaDto GetHeroMetadata(string rawFolder, string cleanId)
    {
        _ = rawFolder;
        _ = cleanId;
        return new HeroMetaDto
        {
            PrimaryAttribute = "Strength",
            AttackType = "Melee",
            Roles = ["Carry", "Initiator"]
        };
    }

    private static List<AbilityDto> GetHeroAbilities(string heroIdentifier, string rawFolder, string displayName)
    {
        List<AbilityDto> abilities = [];
        string cleanCode = heroIdentifier.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).Trim();
        string cleanDisplay = displayName.Replace(" ", "").Trim();

        // 1. Try resolving from _cachedHeroDetails AbilitiesJson if available
        if (_cachedHeroDetails != null)
        {
            HeroDetailDto? hero = _cachedHeroDetails.FirstOrDefault(h =>
                h.HeroCode.Equals(heroIdentifier, StringComparison.OrdinalIgnoreCase) ||
                h.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                h.HeroCode.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

            if (hero != null && !string.IsNullOrWhiteSpace(hero.AbilitiesJson) && hero.AbilitiesJson != "[]")
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(hero.AbilitiesJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        int slotIdx = 0;
                        foreach (JsonElement elem in doc.RootElement.EnumerateArray())
                        {
                            string key = slotIdx switch
                            {
                                0 => "Q",
                                1 => "W",
                                2 => "E",
                                3 => "R",
                                4 => "D",
                                _ => "F"
                            };

                            string? name = elem.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                            string? desc = elem.TryGetProperty("description", out JsonElement d) ? d.GetString() : null;

                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                abilities.Add(new AbilityDto
                                {
                                    Key = key,
                                    Name = name,
                                    Description = desc ?? $"Combat ability for {displayName}.",
                                    IconUrl = $"/images/abilities/hero_{rawFolder}_{slotIdx + 1}.png"
                                });
                            }
                            slotIdx++;
                        }
                    }
                }
                catch
                {
                    // Fall back to metadata localization cache
                }
            }
        }

        if (abilities.Count >= 4)
        {
            ApplyAbilityRules(abilities, rawFolder, cleanCode);
            return abilities;
        }

        // 2. Query ability localization cache (_abilityLocalizationCache)
        abilities.Clear();
        string[] namePrefixes = [cleanCode, cleanDisplay, rawFolder];

        for (int i = 1; i <= 4; i++)
        {
            string slot = i switch
            {
                1 => "Q",
                2 => "W",
                3 => "E",
                4 => "R",
                _ => "D"
            };

            string abilityName = string.Empty;
            string abilityDesc = string.Empty;

            foreach (string prefix in namePrefixes)
            {
                string key = $"Ability_{prefix}{i}_name";
                if (_abilityLocalizationCache.TryGetValue(key, out string? foundName) && !string.IsNullOrWhiteSpace(foundName))
                {
                    abilityName = foundName;
                    abilityDesc = _abilityLocalizationCache.GetValueOrDefault($"Ability_{prefix}{i}_description_simple")
                               ?? _abilityLocalizationCache.GetValueOrDefault($"Ability_{prefix}{i}_description")
                               ?? _abilityLocalizationCache.GetValueOrDefault($"Ability_{prefix}{i}_IMPACT_effect")
                               ?? string.Empty;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(abilityName))
            {
                abilityName = $"{displayName} {slot}";
            }

            if (string.IsNullOrWhiteSpace(abilityDesc))
            {
                abilityDesc = $"Core combat ability for {displayName}.";
            }

            abilities.Add(new AbilityDto
            {
                Key = slot,
                Name = abilityName,
                Description = abilityDesc,
                IconUrl = $"/images/abilities/hero_{rawFolder}_{i}.png"
            });
        }

        ApplyAbilityRules(abilities, rawFolder, cleanCode);
        return abilities;
    }

    private static void ApplyAbilityRules(List<AbilityDto> abilities, string rawFolder, string cleanCode)
    {
        List<HeroAbilityRule>? heroRules = null;
        if (!_heroAbilityRulesCache.TryGetValue(rawFolder, out heroRules) &&
            !_heroAbilityRulesCache.TryGetValue(cleanCode, out heroRules))
        {
            _heroAbilityRulesCache.TryGetValue("_default", out heroRules);
        }

        if (heroRules is { Count: > 0 })
        {
            for (int idx = 0; idx < abilities.Count; idx++)
            {
                AbilityDto ab = abilities[idx];
                HeroAbilityRule? rule = heroRules.FirstOrDefault(r => r.Slot == idx + 1 || string.Equals(r.Key, ab.Key, StringComparison.OrdinalIgnoreCase))
                                       ?? heroRules.FirstOrDefault(r => r.Slot == idx + 1);
                if (rule != null)
                {
                    ab.MaxLevel = rule.MaxLevel;
                    ab.RequiredLevels = rule.RequiredLevels;
                }
            }
        }
    }

    private static Dictionary<string, string> LoadAbilityLocalization()
    {
        Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);

        string[] baseSubpaths =
        [
            Path.Combine("src", "services", "web-ui", "wwwroot", "ability_metadata.json"),
            Path.Combine("wwwroot", "ability_metadata.json"),
            Path.Combine("src", "services", "master-server", "Data", "ability_metadata.json"),
            Path.Combine("Data", "ability_metadata.json"),
            "ability_metadata.json"
        ];

        string[] roots = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];

        static void MergeFirstFound(string[] searchRoots, string[] subpaths, Dictionary<string, string> target)
        {
            foreach (string root in searchRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                DirectoryInfo? dir = new(root);
                while (dir != null)
                {
                    foreach (string subpath in subpaths)
                    {
                        string probe = Path.Combine(dir.FullName, subpath);
                        if (!System.IO.File.Exists(probe)) continue;
                        try
                        {
                            string json = System.IO.File.ReadAllText(probe);
                            Dictionary<string, string>? dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (dict != null)
                            {
                                foreach (KeyValuePair<string, string> kvp in dict)
                                {
                                    target.TryAdd(kvp.Key, kvp.Value);
                                }
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.TraceWarning("[HomeController] Failed parsing ability metadata probe {0}: {1}", probe, ex.Message);
                        }
                    }
                    dir = dir.Parent;
                }
            }
        }

        MergeFirstFound(roots, baseSubpaths, merged);
        if (!CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(null))
        {
            MergeFirstFound(roots, [Path.Combine("games", "dawnbringer-overlay", "ability_metadata.json")], merged);
        }

        return merged;
    }

    private static Dictionary<string, List<HeroAbilityRule>> LoadHeroAbilityRules()
    {
        string[] candidateSubpaths =
        [
            Path.Combine("src", "services", "web-ui", "wwwroot", "hero_ability_rules.json"),
            Path.Combine("wwwroot", "hero_ability_rules.json"),
            Path.Combine("src", "services", "master-server", "Data", "hero_ability_rules.json"),
            Path.Combine("Data", "hero_ability_rules.json"),
            "hero_ability_rules.json"
        ];

        string[] roots = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        string? foundPath = null;

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            DirectoryInfo? dir = new(root);
            while (dir != null)
            {
                foreach (string subpath in candidateSubpaths)
                {
                    string probe = Path.Combine(dir.FullName, subpath);
                    if (System.IO.File.Exists(probe))
                    {
                        foundPath = probe;
                        break;
                    }
                }
                if (foundPath != null) break;
                dir = dir.Parent;
            }
            if (foundPath != null) break;
        }

        if (foundPath != null)
        {
            try
            {
                string json = System.IO.File.ReadAllText(foundPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                Dictionary<string, List<HeroAbilityRule>> result = new(StringComparer.OrdinalIgnoreCase);

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("abilities", out JsonElement abilitiesElem) &&
                        abilitiesElem.ValueKind == JsonValueKind.Array)
                    {
                        List<HeroAbilityRule> rulesList = [];
                        foreach (JsonElement item in abilitiesElem.EnumerateArray())
                        {
                            int slot = item.TryGetProperty("slot", out JsonElement s) ? s.GetInt32() : 1;
                            string key = item.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? "Q" : "Q";
                            int maxLevel = item.TryGetProperty("maxLevel", out JsonElement m) ? m.GetInt32() : 4;
                            bool isInnate = item.TryGetProperty("isInnateOrEarlyUlt", out JsonElement inn) && inn.GetBoolean();
                            List<int> reqLevels = [];
                            if (item.TryGetProperty("requiredLevels", out JsonElement reqs) && reqs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement r in reqs.EnumerateArray())
                                {
                                    reqLevels.Add(r.GetInt32());
                                }
                            }

                            rulesList.Add(new HeroAbilityRule
                            {
                                Slot = slot,
                                Key = key,
                                MaxLevel = maxLevel,
                                RequiredLevels = reqLevels,
                                IsInnateOrEarlyUlt = isInnate
                            });
                        }
                        result[prop.Name] = rulesList;
                    }
                }

                return result;
            }
            catch
            {
                // Fall back to empty dictionary
            }
        }

        return new Dictionary<string, List<HeroAbilityRule>>(StringComparer.OrdinalIgnoreCase);
    }

    public record VoteDifficultyRequest(string HeroIdentifier, int Difficulty);

    public class HeroMetaDto
    {
        public string PrimaryAttribute { get; set; } = "Strength";
        public string AttackType { get; set; } = "Melee";
        public List<string> Roles { get; set; } = [];
    }

    public class AbilityDto
    {
        public string Key { get; set; } = "Q";
        public string Name { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int MaxLevel { get; set; } = 4;
        public List<int> RequiredLevels { get; set; } = [];
    }

    public class HeroAbilityRule
    {
        public int Slot { get; set; } = 1;
        public string Key { get; set; } = "Q";
        public int MaxLevel { get; set; } = 4;
        public List<int> RequiredLevels { get; set; } = [];
        public bool IsInnateOrEarlyUlt { get; set; }
    }
}
