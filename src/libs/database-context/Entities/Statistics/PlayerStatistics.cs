// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Entities.Statistics;

[Table("PlayerStatistics", Schema = "stat")]
[Index(nameof(MatchID), nameof(AccountID), IsUnique = true)]
[Index(nameof(AccountID))]
[Index(nameof(ClanID))]
[Index(nameof(HeroIdentifier))]
[Index(nameof(AccountID), nameof(HeroIdentifier))]
public class PlayerStatistics
{
    private uint? _heroProductID;
    private int _matchId;
    private int _accountId;
    [Key] public int ID { get; set; }

    public int MatchID
    {
        get => _matchId;
        set
        {
            _matchId = value;
            if (Inventories != null) foreach (MatchPlayerInventory inv in Inventories) inv.MatchID = value;
            if (ItemEvents != null) foreach (MatchItemEvent ev in ItemEvents) ev.MatchID = value;
            if (AbilityEvents != null) foreach (MatchAbilityEvent ev in AbilityEvents) ev.MatchID = value;
        }
    }

    [ForeignKey(nameof(MatchID))] public MatchStatistics? Match { get; set; }

    public int MVP { get; set; }

    public int AccountID
    {
        get => _accountId;
        set
        {
            _accountId = value;
            if (Inventories != null) foreach (MatchPlayerInventory inv in Inventories) inv.AccountID = value;
            if (ItemEvents != null) foreach (MatchItemEvent ev in ItemEvents) ev.AccountID = value;
            if (AbilityEvents != null) foreach (MatchAbilityEvent ev in AbilityEvents) ev.AccountID = value;
        }
    }

    [ForeignKey(nameof(AccountID))] public Account? Account { get; set; }

    [MaxLength(15)] public string AccountName { get; set; } = string.Empty;

    public int? ClanID { get; set; }

    [ForeignKey(nameof(ClanID))] public Clan? Clan { get; set; }

    [MaxLength(4)] public string? ClanTag { get; set; }

    public int Team { get; set; }

    public int LobbyPosition { get; set; }

    public int GroupNumber { get; set; }

    public int Benefit { get; set; }

    public uint? HeroProductID
    {
        get => _heroProductID is 0 ? null : _heroProductID;
        set => _heroProductID = value is 0 ? null : value;
    }

    [MaxLength(64)] public string HeroIdentifier { get; set; } = string.Empty;

    /// <summary>
    ///     Structured JSON dictionary mapping map-specific metric names (from <c>GameplayStat0..9</c>
    ///     and core combat counters) for custom scoreboard display and career rollups.
    /// </summary>
    public string? CustomStats { get; set; }

    private string? _alternativeAvatarName;
    private string? _wardProductName;
    private string? _tauntProductName;
    private string? _announcerProductName;
    private string? _courierProductName;
    private string? _accountIconProductName;
    private string? _chatColourProductName;
    private List<string>? _inventory;

    [NotMapped]
    [MaxLength(100)]
    public string? AlternativeAvatarName
    {
        get => _alternativeAvatarName;
        set => _alternativeAvatarName = value;
    }

    public uint? AlternativeAvatarProductID { get; set; }

    [NotMapped]
    [MaxLength(100)]
    public string? WardProductName
    {
        get => _wardProductName;
        set => _wardProductName = value;
    }

    public uint? WardProductID { get; set; }

    [NotMapped]
    [MaxLength(100)]
    public string? TauntProductName
    {
        get => _tauntProductName;
        set => _tauntProductName = value;
    }

    public uint? TauntProductID { get; set; }

    [NotMapped]
    [MaxLength(100)]
    public string? AnnouncerProductName
    {
        get => _announcerProductName;
        set => _announcerProductName = value;
    }

    public uint? AnnouncerProductID { get; set; }

    [NotMapped]
    [MaxLength(100)]
    public string? CourierProductName
    {
        get => _courierProductName;
        set => _courierProductName = value;
    }

    public uint? CourierProductID { get; set; }

    [NotMapped]
    [MaxLength(100)]
    public string? AccountIconProductName
    {
        get => _accountIconProductName;
        set => _accountIconProductName = value;
    }

    public uint? AccountIconProductID { get; set; }

    [NotMapped]
    [MaxLength(100)]
    public string? ChatColourProductName
    {
        get => _chatColourProductName;
        set => _chatColourProductName = value;
    }

    public uint? ChatColourProductID { get; set; }

    [InverseProperty(nameof(MatchPlayerInventory.PlayerStatistic))]
    public List<MatchPlayerInventory> Inventories { get; set; } = [];

    [NotMapped]
    public List<string> Inventory
    {
        get
        {
            if (Inventories is { Count: > 0 })
            {
                return Inventories.OrderBy(i => i.SlotIndex).Select(i => i.ItemIdentifier).ToList();
            }
            return _inventory ?? [];
        }
        set
        {
            _inventory = value;
            SyncInventoriesFromList(value);
        }
    }

    private void SyncInventoriesFromList(List<string>? items)
    {
        Inventories ??= [];
        List<string>? currentItems = Inventories.Count > 0
            ? Inventories.OrderBy(i => i.SlotIndex).Select(i => i.ItemIdentifier).ToList()
            : null;

        if (currentItems != null && items != null && currentItems.SequenceEqual(items, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Inventories.Clear();
        if (items == null || items.Count == 0) return;

        for (int i = 0; i < items.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(items[i])) continue;
            Inventories.Add(new MatchPlayerInventory
            {
                MatchID = MatchID,
                AccountID = AccountID,
                SlotIndex = i,
                ItemIdentifier = items[i],
                Charges = 0
            });
        }
    }

    public int Win { get; set; }

    public int Loss { get; set; }

    public int Disconnected { get; set; }

    public int Conceded { get; set; }

    public int Kicked { get; set; }

    public int PublicMatch { get; set; }

    public double PublicSkillRatingChange { get; set; }

    public int RankedMatch { get; set; }

    public double RankedSkillRatingChange { get; set; }

    public int SocialBonus { get; set; }

    public int UsedToken { get; set; }

    public int ConcedeVotes { get; set; }

    public int HeroKills { get; set; }

    public int HeroDamage { get; set; }

    public int GoldFromHeroKills { get; set; }

    public int HeroAssists { get; set; }

    public int HeroExperience { get; set; }

    public int HeroDeaths { get; set; }

    [NotMapped]
    public int Kills
    {
        get => HeroKills;
        set => HeroKills = value;
    }

    [NotMapped]
    public int Deaths
    {
        get => HeroDeaths;
        set => HeroDeaths = value;
    }

    [NotMapped]
    public int Assists
    {
        get => HeroAssists;
        set => HeroAssists = value;
    }

    public int Buybacks { get; set; }

    public int GoldLostToDeath { get; set; }

    public int SecondsDead { get; set; }

    public int TeamCreepKills { get; set; }

    public int TeamCreepDamage { get; set; }

    public int TeamCreepGold { get; set; }

    public int TeamCreepExperience { get; set; }

    public int NeutralCreepKills { get; set; }

    public int NeutralCreepDamage { get; set; }

    public int NeutralCreepGold { get; set; }

    public int NeutralCreepExperience { get; set; }

    public int BuildingDamage { get; set; }

    public int BuildingsRazed { get; set; }

    public int ExperienceFromBuildings { get; set; }

    public int GoldFromBuildings { get; set; }

    public int Denies { get; set; }

    public int ExperienceDenied { get; set; }

    public int Gold { get; set; }

    public int GoldSpent { get; set; }

    public int Experience { get; set; }

    public int Actions { get; set; }

    public int SecondsPlayed { get; set; }

    public int HeroLevel { get; set; }

    public int ConsumablesPurchased { get; set; }

    public int WardsPlaced { get; set; }

    public int FirstBlood { get; set; }

    public int DoubleKill { get; set; }

    public int TripleKill { get; set; }

    public int QuadKill { get; set; }

    public int Annihilation { get; set; }

    public int KillStreak03 { get; set; }

    public int KillStreak04 { get; set; }

    public int KillStreak05 { get; set; }

    public int KillStreak06 { get; set; }

    public int KillStreak07 { get; set; }

    public int KillStreak08 { get; set; }

    public int KillStreak09 { get; set; }

    public int KillStreak10 { get; set; }

    public int KillStreak15 { get; set; }

    public int Smackdown { get; set; }

    public int Humiliation { get; set; }

    public int Nemesis { get; set; }

    public int Retribution { get; set; }

    public int Score { get; set; }

    /// <summary>
    ///     Total healing done to allies. (Telemetry channel 0)
    /// </summary>
    public double GameplayStat0 { get; set; }

    /// <summary>
    ///     Total damage taken / mitigated prior to reductions. (Telemetry channel 1)
    /// </summary>
    public double GameplayStat1 { get; set; }

    /// <summary>
    ///     Total crowd control / disable duration applied in seconds. (Telemetry channel 2)
    /// </summary>
    public double GameplayStat2 { get; set; }

    /// <summary>
    ///     Total buff and utility applications. (Telemetry channel 3)
    /// </summary>
    public double GameplayStat3 { get; set; }

    /// <summary>
    ///     Total pure / true damage dealt (bypasses armor and magic resistance). (Telemetry channel 4)
    /// </summary>
    public double GameplayStat4 { get; set; }

    /// <summary>
    ///     Custom / event telemetry channel 5.
    /// </summary>
    public double GameplayStat5 { get; set; }

    /// <summary>
    ///     Custom / event telemetry channel 6.
    /// </summary>
    public double GameplayStat6 { get; set; }

    /// <summary>
    ///     Custom / event telemetry channel 7.
    /// </summary>
    public double GameplayStat7 { get; set; }

    /// <summary>
    ///     Custom / event telemetry channel 8.
    /// </summary>
    public double GameplayStat8 { get; set; }

    /// <summary>
    ///     Custom / event telemetry channel 9.
    /// </summary>
    public double GameplayStat9 { get; set; }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat0" />: Total healing done to allies.
    /// </summary>
    [NotMapped]
    public double HealingDone
    {
        get => GameplayStat0;
        set => GameplayStat0 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat1" />: Total damage taken / mitigated.
    /// </summary>
    [NotMapped]
    public double DamageTaken
    {
        get => GameplayStat1;
        set => GameplayStat1 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat2" />: Total duration of crowd control / disables applied in seconds.
    /// </summary>
    [NotMapped]
    public double CrowdControlDuration
    {
        get => GameplayStat2;
        set => GameplayStat2 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat3" />: Total buff and utility applications.
    /// </summary>
    [NotMapped]
    public double BuffsApplied
    {
        get => GameplayStat3;
        set => GameplayStat3 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat4" />: Total pure / true damage dealt.
    /// </summary>
    [NotMapped]
    public double PureDamageDealt
    {
        get => GameplayStat4;
        set => GameplayStat4 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat5" />: Total damage dealt to neutral boss objectives (e.g., Kongor,
    ///     Transmutanstein).
    /// </summary>
    [NotMapped]
    public double BossDamageDealt
    {
        get => GameplayStat5;
        set => GameplayStat5 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat6" />: Total powerups / runes collected.
    /// </summary>
    [NotMapped]
    public double PowerupsCollected
    {
        get => GameplayStat6;
        set => GameplayStat6 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat7" />: Total skillshots / non-targeted abilities landed.
    /// </summary>
    [NotMapped]
    public double SkillshotsLanded
    {
        get => GameplayStat7;
        set => GameplayStat7 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat8" />: Total barrier / shield damage absorbed.
    /// </summary>
    [NotMapped]
    public double ShieldsDamageAbsorbed
    {
        get => GameplayStat8;
        set => GameplayStat8 = value;
    }

    /// <summary>
    ///     Semantic alias for <see cref="GameplayStat9" />: Custom mode / event objective score.
    /// </summary>
    [NotMapped]
    public double ObjectiveScore
    {
        get => GameplayStat9;
        set => GameplayStat9 = value;
    }

    public int TimeEarningExperience { get; set; }

    private List<ItemEvent>? _itemHistory;

    [InverseProperty(nameof(MatchItemEvent.PlayerStatistic))]
    public List<MatchItemEvent> ItemEvents { get; set; } = [];

    [NotMapped]
    public List<ItemEvent>? ItemHistory
    {
        get
        {
            if (ItemEvents is { Count: > 0 })
            {
                return ItemEvents
                    .OrderBy(e => e.GameTimeSeconds)
                    .ThenBy(e => e.ID)
                    .Select(e => new ItemEvent
                    {
                        ItemName = e.ItemIdentifier,
                        GameTimeSeconds = e.GameTimeSeconds,
                        EventType = e.EventType
                    })
                    .ToList();
            }
            return _itemHistory;
        }
        set
        {
            _itemHistory = value;
            SyncItemEventsFromList(value);
        }
    }

    private void SyncItemEventsFromList(List<ItemEvent>? events)
    {
        ItemEvents ??= [];
        ItemEvents.Clear();
        if (events == null || events.Count == 0) return;

        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.ItemName)) continue;
            ItemEvents.Add(new MatchItemEvent
            {
                MatchID = MatchID,
                AccountID = AccountID,
                GameTimeSeconds = ev.GameTimeSeconds,
                EventType = ev.EventType,
                ItemIdentifier = ev.ItemName
            });
        }
    }

    private List<AbilityEvent>? _abilityHistory;

    [InverseProperty(nameof(MatchAbilityEvent.PlayerStatistic))]
    public List<MatchAbilityEvent> AbilityEvents { get; set; } = [];

    [NotMapped]
    public List<AbilityEvent>? AbilityHistory
    {
        get
        {
            if (AbilityEvents is { Count: > 0 })
            {
                return AbilityEvents
                    .OrderBy(e => e.GameTimeSeconds)
                    .ThenBy(e => e.ID)
                    .Select(e => new AbilityEvent
                    {
                        HeroName = e.HeroIdentifier,
                        AbilityName = e.AbilityIdentifier,
                        SlotIndex = e.SlotIndex,
                        Level = e.Level,
                        GameTimeSeconds = e.GameTimeSeconds
                    })
                    .ToList();
            }
            return _abilityHistory;
        }
        set
        {
            _abilityHistory = value;
            SyncAbilityEventsFromList(value);
        }
    }

    private void SyncAbilityEventsFromList(List<AbilityEvent>? events)
    {
        AbilityEvents ??= [];
        AbilityEvents.Clear();
        if (events == null || events.Count == 0) return;

        foreach (AbilityEvent ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.AbilityName)) continue;
            AbilityEvents.Add(new MatchAbilityEvent
            {
                MatchID = MatchID,
                AccountID = AccountID,
                HeroIdentifier = !string.IsNullOrWhiteSpace(ev.HeroName) ? ev.HeroName : HeroIdentifier,
                AbilityIdentifier = ev.AbilityName,
                SlotIndex = ev.SlotIndex,
                Level = (byte)Math.Clamp(ev.Level, 1, 4),
                GameTimeSeconds = ev.GameTimeSeconds
            });
        }
    }
}
