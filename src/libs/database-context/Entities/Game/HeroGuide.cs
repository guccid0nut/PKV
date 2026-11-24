// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

[Table("HeroGuides", Schema = "misc")]
[Index(nameof(AuthorID))]
[Index(nameof(HeroIdentifier))]
[Index(nameof(HeroIdentifier), nameof(Public))]
public class HeroGuide
{
    [Key] public int ID { get; set; }

    [MaxLength(50)] public string Name { get; set; } = string.Empty;

    [MaxLength(20)] public string HeroName { get; set; } = string.Empty;

    [MaxLength(25)] public string HeroIdentifier { get; set; } = string.Empty;

    [MaxLength(500)] public string Intro { get; set; } = string.Empty;

    [MaxLength(1500)] public string Content { get; set; } = string.Empty;

    [InverseProperty(nameof(HeroGuideItem.Guide))]
    public List<HeroGuideItem> Items { get; set; } = [];

    [InverseProperty(nameof(HeroGuideAbility.Guide))]
    public List<HeroGuideAbility> Abilities { get; set; } = [];

    [NotMapped]
    public string StartingItems
    {
        get => GetItemsForSlot(GuideSlotType.Starting);
        set => SetItemsForSlot(GuideSlotType.Starting, value);
    }

    [NotMapped]
    public string EarlyGameItems
    {
        get => GetItemsForSlot(GuideSlotType.EarlyGame);
        set => SetItemsForSlot(GuideSlotType.EarlyGame, value);
    }

    [NotMapped]
    public string CoreItems
    {
        get => GetItemsForSlot(GuideSlotType.Core);
        set => SetItemsForSlot(GuideSlotType.Core, value);
    }

    [NotMapped]
    public string LuxuryItems
    {
        get => GetItemsForSlot(GuideSlotType.Luxury);
        set => SetItemsForSlot(GuideSlotType.Luxury, value);
    }

    [NotMapped]
    public string SituationalItems
    {
        get => GetItemsForSlot(GuideSlotType.Situational);
        set => SetItemsForSlot(GuideSlotType.Situational, value);
    }

    [NotMapped]
    public string AbilityQueue
    {
        get => GetAbilitiesQueue();
        set => SetAbilitiesQueue(value);
    }

    [NotMapped]
    public string SkillBuild
    {
        get => AbilityQueue;
        set => AbilityQueue = value;
    }

    public int AuthorID { get; set; }

    [ForeignKey(nameof(AuthorID))] public Account Author { get; set; } = null!;

    public float Rating { get; set; }

    public int UpVotes { get; set; }

    public int DownVotes { get; set; }

    public bool Public { get; set; }

    public bool Featured { get; set; }

    [MaxLength(50)] public string TargetMap { get; set; } = "caldavar";

    [NotMapped]
    public string GuideTitle
    {
        get => Name;
        set => Name = value;
    }

    [NotMapped]
    public string GuideDescription
    {
        get => Intro;
        set => Intro = value;
    }

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampLastUpdated { get; set; }

    #region Helper Synchronization Methods

    private string GetItemsForSlot(GuideSlotType slot)
    {
        if (Items is null || Items.Count == 0) return string.Empty;

        IEnumerable<string> items = Items
            .Where(i => i.SlotType == slot)
            .OrderBy(i => i.OrderIndex)
            .Select(i => i.ItemCode)
            .Where(code => !string.IsNullOrWhiteSpace(code));

        return string.Join(',', items);
    }

    private void SetItemsForSlot(GuideSlotType slot, string? value)
    {
        Items ??= [];
        Items.RemoveAll(i => i.SlotType == slot);

        if (string.IsNullOrWhiteSpace(value)) return;

        string[] tokens = value
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int order = 0;
        foreach (string token in tokens)
        {
            string code = token.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            Items.Add(new HeroGuideItem
            {
                GuideID = ID,
                Guide = this,
                SlotType = slot,
                OrderIndex = order++,
                ItemCode = code
            });
        }
    }

    private string GetAbilitiesQueue()
    {
        if (Abilities is null || Abilities.Count == 0) return string.Empty;

        IEnumerable<string> abilities = Abilities
            .OrderBy(a => a.LevelSequence)
            .Select(a => !string.IsNullOrWhiteSpace(a.AbilityCode)
                ? a.AbilityCode
                : a.AbilitySlot > 0 ? a.AbilitySlot.ToString() : string.Empty)
            .Where(code => !string.IsNullOrWhiteSpace(code));

        return string.Join(',', abilities);
    }

    private void SetAbilitiesQueue(string? value)
    {
        Abilities ??= [];
        Abilities.Clear();

        if (string.IsNullOrWhiteSpace(value)) return;

        string[] tokens = value
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int sequence = 1;
        foreach (string token in tokens)
        {
            string code = token.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            int slot = ParseAbilitySlot(code);

            Abilities.Add(new HeroGuideAbility
            {
                GuideID = ID,
                Guide = this,
                LevelSequence = sequence++,
                AbilitySlot = slot,
                AbilityCode = code
            });
        }
    }

    private static int ParseAbilitySlot(string token)
    {
        if (int.TryParse(token, out int slot)) return slot;
        if (token.Equals("Q", StringComparison.OrdinalIgnoreCase)) return 1;
        if (token.Equals("W", StringComparison.OrdinalIgnoreCase)) return 2;
        if (token.Equals("E", StringComparison.OrdinalIgnoreCase)) return 3;
        if (token.Equals("R", StringComparison.OrdinalIgnoreCase) || token.Equals("Ultimate", StringComparison.OrdinalIgnoreCase)) return 4;
        if (token.Contains("stat", StringComparison.OrdinalIgnoreCase) || token.Contains("boost", StringComparison.OrdinalIgnoreCase) || token.Contains('+')) return 5;

        if (token.Length > 0 && char.IsDigit(token[^1]))
        {
            return token[^1] - '0';
        }

        return 0;
    }

    #endregion
}
