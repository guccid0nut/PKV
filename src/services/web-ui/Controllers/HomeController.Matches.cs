// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Models;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public partial class HomeController
{
    [Authorize]
    [HttpGet("matches")]
    [HttpGet("matches/{accountName}")]
    [HttpGet("player/{accountName}/matches")]
    [HttpGet("Home/Matches")]
    public async Task<IActionResult> Matches([FromRoute] string? accountName = null, [FromQuery] string? target = null,
        CancellationToken cancellationToken = default)
    {
        string? targetIgn = !string.IsNullOrWhiteSpace(accountName)
            ? accountName.Trim()
            : (!string.IsNullOrWhiteSpace(target)
                ? target.Trim()
                : (User.FindFirst("ign")?.Value ?? User.Identity?.Name));

        if (string.IsNullOrWhiteSpace(targetIgn))
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return RedirectToAction("Login", "Account");
            }

            return RedirectToAction("Index");
        }

        AccountDto? account = await _playerStatsClient.GetAccountByIgnAsync(targetIgn, cancellationToken);

        if (account == null)
        {
            ViewData["Account"] = null;
            ViewData["RecentMatches"] = new List<PlayerStatistics>();
            ViewData["MatchStatsMap"] = new Dictionary<int, MatchStatisticsDto>();
            return View();
        }

        IReadOnlyList<PlayerStatisticsDto> playerStats =
            await _playerStatsClient.GetRecentPlayerStatisticsAsync(account.ID, 20, cancellationToken);
        IReadOnlyList<MatchStatisticsDto> matches =
            await _playerStatsClient.GetMatchesForAccountAsync(account.ID, 20, cancellationToken);
        Dictionary<int, MatchStatisticsDto> matchStatsMap = matches
            .DistinctBy(ms => ms.MatchID)
            .ToDictionary(ms => ms.MatchID);

        ViewData["Account"] = account;
        ViewData["RecentMatches"] = playerStats.Select(p =>
        {
            int secondsPlayed = matchStatsMap.TryGetValue(p.MatchID, out MatchStatisticsDto? ms) ? ms.TimePlayed : 0;
            return new PlayerStatistics
            {
                MatchID = p.MatchID,
                AccountID = p.AccountID,
                AccountName = p.AccountName,
                HeroIdentifier = p.HeroIdentifier,
                HeroKills = p.HeroKills,
                HeroDeaths = p.Deaths,
                HeroAssists = p.HeroAssists,
                Win = p.Won ? 1 : 0,
                Loss = p.Won ? 0 : 1,
                Team = p.Team,
                LobbyPosition = p.Position,
                MVP = 0,
                ClanID = null,
                ClanTag = null,
                GroupNumber = 0,
                Benefit = 0,
                HeroProductID = null,
                Inventory = p.Inventory != null ? [.. p.Inventory] : [],
                Disconnected = 0,
                Conceded = 0,
                Kicked = 0,
                PublicMatch = 1,
                PublicSkillRatingChange = 0,
                RankedMatch = 0,
                RankedSkillRatingChange = 0,
                SocialBonus = 0,
                UsedToken = 0,
                ConcedeVotes = 0,
                HeroDamage = p.HeroDamage,
                GoldFromHeroKills = 0,
                HeroExperience = 0,
                Buybacks = 0,
                GoldLostToDeath = 0,
                SecondsDead = 0,
                TeamCreepKills = p.CreepKills,
                TeamCreepDamage = 0,
                TeamCreepGold = 0,
                TeamCreepExperience = 0,
                NeutralCreepKills = p.NeutralKills,
                NeutralCreepDamage = 0,
                NeutralCreepGold = 0,
                NeutralCreepExperience = 0,
                BuildingDamage = p.BuildingDamage,
                BuildingsRazed = 0,
                ExperienceFromBuildings = 0,
                GoldFromBuildings = 0,
                Denies = 0,
                ExperienceDenied = 0,
                Gold = p.GoldEarned,
                GoldSpent = p.GoldSpent,
                Experience = p.ExperienceEarned,
                Actions = 0,
                SecondsPlayed = secondsPlayed,
                HeroLevel = p.HeroLevel > 0 ? p.HeroLevel : 1,
                ConsumablesPurchased = 0,
                WardsPlaced = p.WardsPlaced,
                FirstBlood = 0,
                DoubleKill = 0,
                TripleKill = 0,
                QuadKill = 0,
                Annihilation = 0,
                KillStreak03 = 0,
                KillStreak04 = 0,
                KillStreak05 = 0,
                KillStreak06 = 0,
                KillStreak07 = 0,
                KillStreak08 = 0,
                KillStreak09 = 0,
                KillStreak10 = 0,
                KillStreak15 = 0,
                Smackdown = 0,
                Humiliation = 0,
                Nemesis = 0,
                Retribution = 0,
                Score = 0,
                GameplayStat0 = 0,
                GameplayStat1 = 0,
                GameplayStat2 = 0,
                GameplayStat3 = 0,
                GameplayStat4 = 0,
                GameplayStat5 = 0,
                GameplayStat6 = 0,
                GameplayStat7 = 0,
                GameplayStat8 = 0,
                GameplayStat9 = 0,
                TimeEarningExperience = 0
            };
        }).ToList();
        ViewData["MatchStatsMap"] = matchStatsMap;

        return View();
    }

    [HttpGet("matches/{matchId:int}")]
    [HttpGet("match/{matchId:int}")]
    [HttpGet("Home/MatchDetails/{matchId:int}")]
    [HttpGet("Home/MatchDetails")]
    [HttpGet("match")]
    [HttpGet("matches/details")]
    public async Task<IActionResult> MatchDetails(int matchId = 0, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0)
        {
            return RedirectToAction("Matches");
        }

        MatchStatisticsDto? matchStat = await _playerStatsClient.GetMatchByIdAsync(matchId, cancellationToken);
        IReadOnlyList<PlayerStatisticsDto> playerStatsRaw =
            await _playerStatsClient.GetPlayerStatisticsForMatchAsync(matchId, cancellationToken);
        List<PlayerStatisticsDto> playerStats =
            [.. playerStatsRaw.OrderBy(ps => ps.Team).ThenBy(ps => ps.LobbyPosition)];

        MatchReplayTimelineDto? replayTimeline = _replayService != null
            ? await _replayService.GetMatchReplayTimelineAsync(matchId, cancellationToken)
            : null;

        if (matchStat == null && playerStats.Count == 0 && replayTimeline == null)
        {
            return NotFound("Match not found.");
        }

        MatchStatistics? matchEntity = matchStat != null
            ? new MatchStatistics
            {
                MatchID = matchStat.MatchID,
                ServerID = 1,
                HostAccountName = !string.IsNullOrEmpty(matchStat.HostAccountName)
                    ? matchStat.HostAccountName
                    : "Server",
                Map = !string.IsNullOrEmpty(matchStat.Map) ? matchStat.Map : (replayTimeline?.Map ?? "caldavar"),
                MapVersion = "1.0",
                TimePlayed = matchStat.TimePlayed > 0
                    ? matchStat.TimePlayed
                    : (replayTimeline?.TotalDurationSeconds ?? 0),
                FileSize = 0,
                FileName = $"M{matchStat.MatchID}.honreplay",
                ReplayUrl = matchStat.ReplayUrl,
                VideoUrl = null,
                ConnectionState = 0,
                Version = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion,
                AveragePSR = 1500,
                AveragePSRTeamOne = 1500,
                AveragePSRTeamTwo = 1500,
                GameMode = !string.IsNullOrEmpty(matchStat.Mode)
                    ? matchStat.Mode
                    : (replayTimeline?.GameMode ?? "bot"),
                ScoreTeam1 = matchStat.ScoreTeam1 > 0 || matchStat.ScoreTeam2 > 0
                    ? matchStat.ScoreTeam1
                    : (replayTimeline?.ScoreLegion ?? 0),
                ScoreTeam2 = matchStat.ScoreTeam1 > 0 || matchStat.ScoreTeam2 > 0
                    ? matchStat.ScoreTeam2
                    : (replayTimeline?.ScoreHellbourne ?? 0),
                TeamScoreGoal = 0,
                PlayerScoreGoal = 0,
                NumberOfRounds = 1,
                ReleaseStage = "RELEASE",
                BannedHeroes = null,
                ScheduledEventID = null,
                ScheduledMatchID = null,
                MVPAccountID = null,
                AwardMostAnnihilations = null,
                AwardMostQuadKills = null,
                AwardLargestKillStreak = null,
                AwardMostSmackdowns = null,
                AwardMostKills = null,
                AwardMostAssists = null,
                AwardLeastDeaths = null,
                AwardMostBuildingDamage = null,
                AwardMostWardsKilled = null,
                AwardMostHeroDamageDealt = null,
                AwardHighestCreepScore = null
            }
            : (replayTimeline != null
                ? new MatchStatistics
                {
                    MatchID = replayTimeline.MatchID > 0 ? replayTimeline.MatchID : matchId,
                    ServerID = 1,
                    HostAccountName = "KONGOR Server",
                    Map = replayTimeline.Map,
                    MapVersion = "1.0",
                    TimePlayed = replayTimeline.TotalDurationSeconds,
                    FileSize = 0,
                    FileName = $"M{matchId}.honreplay",
                    ReplayUrl = $"/replays/{matchId}",
                    VideoUrl = null,
                    ConnectionState = 0,
                    Version = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion,
                    AveragePSR = 1500,
                    AveragePSRTeamOne = 1500,
                    AveragePSRTeamTwo = 1500,
                    GameMode = replayTimeline.GameMode,
                    ScoreTeam1 = replayTimeline.ScoreLegion,
                    ScoreTeam2 = replayTimeline.ScoreHellbourne,
                    TeamScoreGoal = 0,
                    PlayerScoreGoal = 0,
                    NumberOfRounds = 1,
                    ReleaseStage = "RELEASE"
                }
                : null);

        int resolvedDuration = matchEntity?.TimePlayed ?? replayTimeline?.TotalDurationSeconds ?? 0;

        List<PlayerStatistics> playerStatEntities;
        if (playerStats.Count > 0)
        {
            playerStatEntities =
            [
                .. playerStats.Select(p =>
                {
                    List<string> inv = p.Inventory != null ? [.. p.Inventory.Where(i => !string.IsNullOrWhiteSpace(i))] : [];
                    if (inv.Count == 0 && replayTimeline != null)
                    {
                        ReplayPlayerSummary? rp = replayTimeline.Players.FirstOrDefault(r =>
                            (r.AccountID > 0 && r.AccountID == p.AccountID) ||
                            string.Equals(r.AccountName, p.AccountName, StringComparison.OrdinalIgnoreCase));
                        if (rp is { FinalItems.Count: > 0 })
                        {
                            inv = [.. rp.FinalItems];
                        }
                    }

                    return new PlayerStatistics
                    {
                        MatchID = p.MatchID,
                        AccountID = p.AccountID,
                        AccountName = p.AccountName,
                        HeroIdentifier = p.HeroIdentifier,
                        HeroKills = p.HeroKills,
                        HeroDeaths = p.Deaths,
                        HeroAssists = p.HeroAssists,
                        Win = p.Win,
                        Loss = p.Won ? 0 : 1,
                        Team = p.Team,
                        LobbyPosition = p.Position,
                        MVP = 0,
                        ClanID = null,
                        ClanTag = null,
                        GroupNumber = 0,
                        Benefit = 0,
                        HeroProductID = null,
                        Inventory = inv,
                        Disconnected = 0,
                        Conceded = 0,
                        Kicked = 0,
                        PublicMatch = 1,
                        PublicSkillRatingChange = 0,
                        RankedMatch = 0,
                        RankedSkillRatingChange = 0,
                        SocialBonus = 0,
                        UsedToken = 0,
                        ConcedeVotes = 0,
                        HeroDamage = p.HeroDamage,
                        GoldFromHeroKills = 0,
                        HeroExperience = 0,
                        Buybacks = 0,
                        GoldLostToDeath = 0,
                        SecondsDead = 0,
                        TeamCreepKills = p.CreepKills,
                        TeamCreepDamage = 0,
                        TeamCreepGold = 0,
                        TeamCreepExperience = 0,
                        NeutralCreepKills = p.NeutralKills,
                        NeutralCreepDamage = 0,
                        NeutralCreepGold = 0,
                        NeutralCreepExperience = 0,
                        BuildingDamage = p.BuildingDamage,
                        BuildingsRazed = 0,
                        ExperienceFromBuildings = 0,
                        GoldFromBuildings = 0,
                        Denies = 0,
                        ExperienceDenied = 0,
                        Gold = p.GoldEarned,
                        GoldSpent = p.GoldSpent,
                        Experience = p.ExperienceEarned,
                        Actions = 0,
                        SecondsPlayed = resolvedDuration,
                        HeroLevel = p.HeroLevel > 0 ? p.HeroLevel : 1,
                        ConsumablesPurchased = 0,
                        WardsPlaced = p.WardsPlaced,
                        FirstBlood = 0,
                        DoubleKill = 0,
                        TripleKill = 0,
                        QuadKill = 0,
                        Annihilation = 0,
                        KillStreak03 = 0,
                        KillStreak04 = 0,
                        KillStreak05 = 0,
                        KillStreak06 = 0,
                        KillStreak07 = 0,
                        KillStreak08 = 0,
                        KillStreak09 = 0,
                        KillStreak10 = 0,
                        KillStreak15 = 0,
                        Smackdown = 0,
                        Humiliation = 0,
                        Nemesis = 0,
                        Retribution = 0,
                        Score = 0,
                        GameplayStat0 = 0,
                        GameplayStat1 = 0,
                        GameplayStat2 = 0,
                        GameplayStat3 = 0,
                        GameplayStat4 = 0,
                        GameplayStat5 = 0,
                        GameplayStat6 = 0,
                        GameplayStat7 = 0,
                        GameplayStat8 = 0,
                        GameplayStat9 = 0,
                        TimeEarningExperience = 0
                    };
                })
            ];
        }
        else if (replayTimeline != null)
        {
            playerStatEntities =
            [
                .. replayTimeline.Players.Select((rp, slotIdx) => new PlayerStatistics
                {
                    MatchID = matchId,
                    AccountID = rp.AccountID,
                    AccountName = rp.AccountName,
                    HeroIdentifier = rp.HeroIdentifier,
                    HeroKills = rp.FinalKills,
                    HeroDeaths = rp.FinalDeaths,
                    HeroAssists = rp.FinalAssists,
                    Win = rp.Team == replayTimeline.WinnerTeam ? 1 : 0,
                    Loss = rp.Team == replayTimeline.WinnerTeam ? 0 : 1,
                    Team = rp.Team,
                    LobbyPosition = slotIdx,
                    Inventory = [.. rp.FinalItems],
                    PublicMatch = 1,
                    Gold = rp.FinalGold,
                    Experience = (int)Math.Round(rp.FinalXpm * Math.Max(0.5, resolvedDuration / 60.0)),
                    SecondsPlayed = resolvedDuration,
                    HeroLevel = Math.Max(1, rp.FinalLevel)
                })
            ];
        }
        else
        {
            playerStatEntities = [];
        }

        if (_timelineService != null)
        {
            MatchTimelineDto timelineDto =
                _timelineService.BuildMatchTimeline(matchEntity, playerStatEntities, replayTimeline);
            CombatHeatmapDto heatmapDto =
                _timelineService.BuildCombatHeatmap(matchEntity, playerStatEntities, replayTimeline);
            List<PlayerDamageTelemetryDto> damageDto =
                _timelineService.BuildDamageTelemetry(playerStatEntities);

            ViewData["MatchTimeline"] = timelineDto;
            ViewData["MatchTimelineJson"] = JsonSerializer.Serialize(timelineDto);
            ViewData["CombatHeatmapJson"] = JsonSerializer.Serialize(heatmapDto);
            ViewData["DamageTelemetryJson"] = JsonSerializer.Serialize(damageDto);
        }

        ViewData["Match"] = matchEntity;
        ViewData["MatchStat"] = matchStat;
        ViewData["PlayerStats"] = playerStatEntities;
        ViewData["ReplayTimeline"] = replayTimeline;

        return View("MatchDetails");
    }

    [HttpGet("api/matches/{matchId:int}/timeline")]
    public async Task<IActionResult> GetMatchTimelineEndpoint(int matchId,
        CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) return BadRequest("Invalid match ID.");

        MatchStatisticsDto? matchStat = await _playerStatsClient.GetMatchByIdAsync(matchId, cancellationToken);
        IReadOnlyList<PlayerStatisticsDto> playerStatsRaw =
            await _playerStatsClient.GetPlayerStatisticsForMatchAsync(matchId, cancellationToken);
        List<PlayerStatisticsDto> playerStats = [.. playerStatsRaw];

        MatchReplayTimelineDto? replayTimeline = _replayService != null
            ? await _replayService.GetMatchReplayTimelineAsync(matchId, cancellationToken)
            : null;

        if (matchStat == null && playerStats.Count == 0 && replayTimeline == null) return NotFound();

        if (_timelineService != null)
        {
            return Json(new
            {
                matchId,
                telemetrySource = replayTimeline?.TelemetrySource ?? "db_reconstruction",
                events = replayTimeline?.EventPins ?? []
            });
        }

        return NotFound();
    }

    [HttpGet("replays/{matchId:int}")]
    [HttpGet("replays/{matchId:int}/download")]
    public async Task<IActionResult> DownloadReplay(int matchId, CancellationToken cancellationToken = default)
    {
        string? archivePath = _replayService?.FindReplayArchivePath(matchId);
        if (!string.IsNullOrWhiteSpace(archivePath) && System.IO.File.Exists(archivePath))
        {
            _logger.LogInformation("[HomeController.Matches] Streaming real .honreplay archive for Match #{MatchId}: {Path}",
                matchId, archivePath);
            return PhysicalFile(archivePath, "application/octet-stream", $"M{matchId}.honreplay");
        }

        string? logPath = _replayService?.FindServerLogPath(matchId);
        if (!string.IsNullOrWhiteSpace(logPath) && System.IO.File.Exists(logPath))
        {
            _logger.LogInformation("[HomeController.Matches] Streaming real K2 server log for Match #{MatchId}: {Path}",
                matchId, logPath);
            return PhysicalFile(logPath, "text/plain", $"M{matchId}.log");
        }

        MatchStatisticsDto? match = await _playerStatsClient.GetMatchByIdAsync(matchId, cancellationToken);
        if (match == null) return NotFound("Match not found.");

        return File([], "application/octet-stream", $"M{matchId}.honreplay");
    }

    [HttpGet("replays/{fileName}")]
    public IActionResult ServeReplayFile(string fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string digits = stem.StartsWith('M') || stem.StartsWith('m') ? stem[1..] : stem;
            if (int.TryParse(digits, out int parsedId) && parsedId > 0)
            {
                string? archivePath = _replayService?.FindReplayArchivePath(parsedId);
                if (!string.IsNullOrWhiteSpace(archivePath) && System.IO.File.Exists(archivePath))
                {
                    return PhysicalFile(archivePath, "application/octet-stream", Path.GetFileName(fileName));
                }
            }
        }

        return File([], "application/octet-stream", fileName);
    }

    [HttpGet("api/matches/{matchId:int}/replay")]
    [HttpGet("api/replays/{matchId:int}/telemetry")]
    public async Task<IActionResult> GetMatchReplayTelemetry(int matchId, CancellationToken cancellationToken = default)
    {
        if (matchId <= 0)
        {
            return BadRequest("Invalid match ID.");
        }

        if (_replayService != null)
        {
            MatchReplayTimelineDto? timeline =
                await _replayService.GetMatchReplayTimelineAsync(matchId, cancellationToken);
            if (timeline != null)
            {
                _logger.LogInformation(
                    "[HomeController.Matches] Served 2D replay telemetry for Match #{MatchId} (Source={Source}, Frames={FrameCount}, Pins={PinCount})",
                    matchId, timeline.TelemetrySource, timeline.Keyframes.Count, timeline.EventPins.Count);
                return Json(timeline);
            }
        }

        return NotFound();
    }
}
