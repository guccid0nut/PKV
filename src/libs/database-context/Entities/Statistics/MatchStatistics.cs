// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

[Index(nameof(MatchID), IsUnique = true)]
[Index(nameof(HostAccountName))]
[Index(nameof(TimestampRecorded))]
[Index(nameof(Map))]
public class MatchStatistics
{
    private int? _awardHighestCreepScore;

    private int? _awardLargestKillStreak;

    private int? _awardLeastDeaths;

    private int? _awardMostAnnihilations;

    private int? _awardMostAssists;

    private int? _awardMostBuildingDamage;

    private int? _awardMostHeroDamageDealt;

    private int? _awardMostKills;

    private int? _awardMostQuadKills;

    private int? _awardMostSmackdowns;

    private int? _awardMostWardsKilled;
    [Key] public int ID { get; set; }

    public DateTimeOffset TimestampRecorded { get; set; } = TimeProvider.System.GetUtcNow();

    public int ServerID { get; set; }

    [MaxLength(15)] public string HostAccountName { get; set; } = string.Empty;

    private int _matchId;
    public int MatchID
    {
        get => _matchId;
        set
        {
            _matchId = value;
            if (Bans != null)
            {
                foreach (MatchBan ban in Bans)
                {
                    if (ban.MatchID == 0 || ban.MatchID != value)
                    {
                        ban.MatchID = value;
                    }
                }
            }
            if (Frags != null)
            {
                foreach (MatchFrag frag in Frags)
                {
                    if (frag.MatchID == 0 || frag.MatchID != value)
                    {
                        frag.MatchID = value;
                    }
                }
            }
        }
    }

    [MaxLength(100)] public string Map { get; set; } = "caldavar";

    [MaxLength(15)] public string MapVersion { get; set; } = "1.0";

    public int TimePlayed { get; set; }

    public int FileSize { get; set; }

    [MaxLength(255)] public string FileName { get; set; } = string.Empty;

    [MaxLength(1024)] public string? ReplayUrl { get; set; }

    [MaxLength(1024)] public string? VideoUrl { get; set; }

    [NotMapped] public bool HasVideo => !string.IsNullOrWhiteSpace(VideoUrl);

    public int ConnectionState { get; set; }

    [MaxLength(50)] public string Version { get; set; } = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion;

    public int AveragePSR { get; set; } = 1500;

    public int AveragePSRTeamOne { get; set; } = 1500;

    public int AveragePSRTeamTwo { get; set; } = 1500;

    [MaxLength(50)] public string GameMode { get; set; } = "picking";

    public int ScoreTeam1 { get; set; }

    public int ScoreTeam2 { get; set; }

    public int TeamScoreGoal { get; set; }

    public int PlayerScoreGoal { get; set; }

    public int NumberOfRounds { get; set; } = 1;

    [MaxLength(32)] public string? CustomSchemaKey { get; set; }

    public short CustomSchemaVersion { get; set; } = 1;

    public string? CustomMatchStats { get; set; }

    [MaxLength(50)] public string ReleaseStage { get; set; } = "RELEASE";

    [InverseProperty(nameof(MatchBan.Match))]
    public List<MatchBan> Bans { get; set; } = [];

    private string? _bannedHeroes = string.Empty;

    [MaxLength(2000)]
    public string? BannedHeroes
    {
        get
        {
            if (Bans.Count > 0)
            {
                return string.Join(",", Bans.OrderBy(b => b.BanOrder).Select(b => b.HeroIdentifier));
            }

            return _bannedHeroes;
        }
        set
        {
            _bannedHeroes = value;
            SyncBansFromString(value);
        }
    }

    private void SyncBansFromString(string? value)
    {
        string? currentBanString = Bans.Count > 0
            ? string.Join(",", Bans.OrderBy(b => b.BanOrder).Select(b => b.HeroIdentifier))
            : null;

        if (currentBanString != null && string.Equals(value, currentBanString, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (currentBanString == null && string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Bans.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;

        string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            Bans.Add(new MatchBan
            {
                MatchID = MatchID,
                BanOrder = i + 1,
                HeroIdentifier = parts[i],
                Team = (i % 2 == 0) ? 1 : 2
            });
        }
    }

    public int? ScheduledEventID { get; set; }

    public int? ScheduledMatchID { get; set; }

    public int? MVPAccountID { get; set; }

    public int? AwardMostAnnihilations
    {
        get => _awardMostAnnihilations is -1 ? null : _awardMostAnnihilations;
        set => _awardMostAnnihilations = value is -1 ? null : value;
    }

    public int? AwardMostQuadKills
    {
        get => _awardMostQuadKills is -1 ? null : _awardMostQuadKills;
        set => _awardMostQuadKills = value is -1 ? null : value;
    }

    public int? AwardLargestKillStreak
    {
        get => _awardLargestKillStreak is -1 ? null : _awardLargestKillStreak;
        set => _awardLargestKillStreak = value is -1 ? null : value;
    }

    public int? AwardMostSmackdowns
    {
        get => _awardMostSmackdowns is -1 ? null : _awardMostSmackdowns;
        set => _awardMostSmackdowns = value is -1 ? null : value;
    }

    public int? AwardMostKills
    {
        get => _awardMostKills is -1 ? null : _awardMostKills;
        set => _awardMostKills = value is -1 ? null : value;
    }

    public int? AwardMostAssists
    {
        get => _awardMostAssists is -1 ? null : _awardMostAssists;
        set => _awardMostAssists = value is -1 ? null : value;
    }

    public int? AwardLeastDeaths
    {
        get => _awardLeastDeaths is -1 ? null : _awardLeastDeaths;
        set => _awardLeastDeaths = value is -1 ? null : value;
    }

    public int? AwardMostBuildingDamage
    {
        get => _awardMostBuildingDamage is -1 ? null : _awardMostBuildingDamage;
        set => _awardMostBuildingDamage = value is -1 ? null : value;
    }

    public int? AwardMostWardsKilled
    {
        get => _awardMostWardsKilled is -1 ? null : _awardMostWardsKilled;
        set => _awardMostWardsKilled = value is -1 ? null : value;
    }

    public int? AwardMostHeroDamageDealt
    {
        get => _awardMostHeroDamageDealt is -1 ? null : _awardMostHeroDamageDealt;
        set => _awardMostHeroDamageDealt = value is -1 ? null : value;
    }

    public int? AwardHighestCreepScore
    {
        get => _awardHighestCreepScore is -1 ? null : _awardHighestCreepScore;
        set => _awardHighestCreepScore = value is -1 ? null : value;
    }

    private List<FragEvent>? _fragHistory;

    [InverseProperty(nameof(MatchFrag.Match))]
    public List<MatchFrag> Frags { get; set; } = [];

    [NotMapped]
    public List<FragEvent>? FragHistory
    {
        get
        {
            if (Frags is { Count: > 0 })
            {
                return Frags
                    .OrderBy(f => f.GameTimeSeconds)
                    .ThenBy(f => f.ID)
                    .Select(f => new FragEvent
                    {
                        SourceID = f.KillerAccountID,
                        TargetID = f.VictimAccountID,
                        GameTimeSeconds = f.GameTimeSeconds,
                        SupporterIDs = f.Assists.Select(a => a.AssistingAccountID).ToList()
                    })
                    .ToList();
            }
            return _fragHistory;
        }
        set
        {
            _fragHistory = value;
            SyncFragsFromList(value);
        }
    }

    private void SyncFragsFromList(List<FragEvent>? events)
    {
        Frags ??= [];
        Frags.Clear();
        if (events == null || events.Count == 0) return;

        bool isFirst = true;
        foreach (FragEvent ev in events)
        {
            MatchFrag frag = new()
            {
                MatchID = MatchID,
                KillerAccountID = ev.SourceID,
                VictimAccountID = ev.TargetID,
                GameTimeSeconds = ev.GameTimeSeconds,
                FirstBlood = isFirst
            };
            isFirst = false;

            if (ev.SupporterIDs != null)
            {
                foreach (int assistId in ev.SupporterIDs)
                {
                    frag.Assists.Add(new MatchFragAssist
                    {
                        Frag = frag,
                        AssistingAccountID = assistId
                    });
                }
            }

            Frags.Add(frag);
        }
    }
}
