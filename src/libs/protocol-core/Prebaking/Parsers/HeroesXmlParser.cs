// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class HeroesXmlParser(ILogger<HeroesXmlParser>? logger = null) : IEntityParser<List<HeroDefinitionDto>>
{
    private static readonly HashSet<string> HellbourneHeroNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hero_Accursed", "Hero_Adrenaline", "Hero_Apex", "Hero_Arachna", "Hero_Artesia",
        "Hero_BabaYaga", "Hero_Bephelgor", "Hero_Bushwack", "Hero_Calamity", "Hero_Chronos",
        "Hero_Circe", "Hero_CorruptedDisciple", "Hero_Cthulhuphant", "Hero_Dampeer", "Hero_Deadlift",
        "Hero_Deadwood", "Hero_Defiler", "Hero_Devourer", "Hero_DiseasedRider", "Hero_DoctorRepulsor",
        "Hero_Dreadknight", "Hero_Ebulus", "Hero_Electrician", "Hero_Fade", "Hero_FlameDragon",
        "Hero_FlintBeastwood", "Hero_ForsakenArcher", "Hero_Frosty", "Hero_Gauntlet", "Hero_Gemini",
        "Hero_Geomancer", "Hero_Goldenveil", "Hero_Grinex", "Hero_Gunblade", "Hero_Gwynplaine",
        "Hero_Hellbringer",
        "Hero_HellDemon", "Hero_Hunter", "Hero_Hydromancer", "Hero_Kane", "Hero_KingKlout",
        "Hero_Klanx", "Hero_Kraken", "Hero_Lodestone", "Hero_Magmar", "Hero_Maliken",
        "Hero_Moraxus", "Hero_Mumra", "Hero_Parallax", "Hero_Parasite", "Hero_Pestilence",
        "Hero_Prophet", "Hero_PuppetMaster", "Hero_Ra", "Hero_Ravenor", "Hero_Revenant",
        "Hero_Riftmage", "Hero_Riptide", "Hero_SandWraith", "Hero_Scar", "Hero_ShadowBlade",
        "Hero_Shaman", "Hero_Soulstealer", "Hero_Succubis", "Hero_Taint", "Hero_Tremble",
        "Hero_Vanya", "Hero_Voodoo", "Hero_WolfMan", "Hero_Xalynx"
    };

    private readonly ILogger<HeroesXmlParser>? _logger = logger;

    public async Task<List<HeroDefinitionDto>> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<List<HeroDefinitionDto>> ParseStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        XElement? root = doc.Root;
        if (root == null || !string.Equals(root.Name.LocalName, "heroes", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Root element must be <heroes>");

        List<HeroDefinitionDto> result = [];
        uint heroIdCounter = 1;

        foreach (XElement heroElem in root.Elements("hero"))
        {
            ct.ThrowIfCancellationRequested();
            string name = heroElem.Attribute("name")?.Value ?? string.Empty;
            string displayName = heroElem.Attribute("displayName")?.Value ??
                                 heroElem.Attribute("displayname")?.Value ?? string.Empty;
            string defaultIcon = heroElem.Attribute("defaultIcon")?.Value ??
                                 heroElem.Attribute("icon")?.Value ?? string.Empty;
            string defaultModel = heroElem.Attribute("defaultModel")?.Value ??
                                  heroElem.Attribute("model")?.Value ?? string.Empty;

            string team = heroElem.Attribute("team")?.Value ?? heroElem.Attribute("faction")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(team))
            {
                team = HellbourneHeroNames.Contains(name) ? "Hellbourne" : "Legion";
            }

            HeroDefinitionDto hero = new()
            {
                Id = heroIdCounter++,
                Name = name,
                DisplayName = displayName,
                Path = heroElem.Attribute("path")?.Value ?? string.Empty,
                EntityPath = heroElem.Attribute("entity")?.Value ?? string.Empty,
                Team = team,
                Faction = team,
                DefaultIcon = defaultIcon,
                IconPath = defaultIcon,
                DefaultModel = defaultModel,
                ModelPath = defaultModel,
                DefaultEffect = heroElem.Attribute("defaultEffect")?.Value ??
                                heroElem.Attribute("effect")?.Value ?? string.Empty,
                Category = heroElem.Attribute("category")?.Value ?? string.Empty,
                Difficulty = ParseFloat(heroElem.Attribute("difficulty")?.Value),
                PrimaryAttribute = heroElem.Attribute("primaryattribute")?.Value ??
                                   heroElem.Attribute("primaryAttribute")?.Value ?? "strength",
                Strength = ParseFloat(heroElem.Attribute("strength")?.Value),
                StrengthPerLevel = ParseFloat(heroElem.Attribute("strengthperlevel")?.Value ??
                                              heroElem.Attribute("strengthPerLevel")?.Value),
                Agility = ParseFloat(heroElem.Attribute("agility")?.Value),
                AgilityPerLevel = ParseFloat(heroElem.Attribute("agilityperlevel")?.Value ??
                                             heroElem.Attribute("agilityPerLevel")?.Value),
                Intelligence = ParseFloat(heroElem.Attribute("intelligence")?.Value),
                IntelligencePerLevel = ParseFloat(heroElem.Attribute("intelligenceperlevel")?.Value ??
                                                  heroElem.Attribute("intelligencePerLevel")?.Value),
                AttackType = heroElem.Attribute("attacktype")?.Value ??
                             heroElem.Attribute("attackType")?.Value ?? "Melee",
                AttackDamageMin = ParseFloat(heroElem.Attribute("attackdamagemin")?.Value ??
                                             heroElem.Attribute("attackDamageMin")?.Value),
                AttackDamageMax = ParseFloat(heroElem.Attribute("attackdamagemax")?.Value ??
                                             heroElem.Attribute("attackDamageMax")?.Value),
                AttackRange = ParseFloat(heroElem.Attribute("attackrange")?.Value ??
                                         heroElem.Attribute("attackRange")?.Value),
                AttackCooldown = ParseInt(heroElem.Attribute("attackcooldown")?.Value ??
                                          heroElem.Attribute("attackCooldown")?.Value),
                MoveSpeed =
                    ParseFloat(heroElem.Attribute("movespeed")?.Value ?? heroElem.Attribute("moveSpeed")?.Value),
                Armor = ParseFloat(heroElem.Attribute("armor")?.Value),
                MagicArmor =
                    ParseFloat(heroElem.Attribute("magicarmor")?.Value ?? heroElem.Attribute("magicArmor")?.Value),
                SoloRating =
                    ParseInt(heroElem.Attribute("solorating")?.Value ?? heroElem.Attribute("soloRating")?.Value),
                JungleRating = ParseInt(heroElem.Attribute("junglerating")?.Value ??
                                        heroElem.Attribute("jungleRating")?.Value),
                CarryRating = ParseInt(heroElem.Attribute("carryrating")?.Value ??
                                       heroElem.Attribute("carryRating")?.Value),
                SupportRating = ParseInt(heroElem.Attribute("supportrating")?.Value ??
                                         heroElem.Attribute("supportRating")?.Value),
                InitiatorRating = ParseInt(heroElem.Attribute("initiatorrating")?.Value ??
                                           heroElem.Attribute("initiatorRating")?.Value),
                GankerRating = ParseInt(heroElem.Attribute("gankerrating")?.Value ??
                                        heroElem.Attribute("gankerRating")?.Value),
                PusherRating = ParseInt(heroElem.Attribute("pusherrating")?.Value ??
                                        heroElem.Attribute("pusherRating")?.Value),
                RangedRating = ParseInt(heroElem.Attribute("rangedrating")?.Value ??
                                        heroElem.Attribute("rangedRating")?.Value),
                MeleeRating = ParseInt(heroElem.Attribute("meleerating")?.Value ??
                                       heroElem.Attribute("meleeRating")?.Value)
            };

            // Parse inventory slots / recommended items if present
            for (int i = 0; i <= 12; i++)
            {
                string? invAttr = heroElem.Attribute($"inventory{i}")?.Value;
                if (!string.IsNullOrEmpty(invAttr)) hero.InventorySlots.Add(invAttr);
            }

            string? recItems = heroElem.Attribute("recommendedItems")?.Value ??
                               heroElem.Attribute("recommendeditems")?.Value;
            if (!string.IsNullOrWhiteSpace(recItems))
            {
                string[] items = recItems.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                hero.RecommendedItems.AddRange(items);
            }

            // Parse abilities
            foreach (XElement abilityElem in heroElem.Elements("ability"))
            {
                string abName = abilityElem.Attribute("name")?.Value ?? string.Empty;
                string abIcon = abilityElem.Attribute("icon")?.Value ?? string.Empty;
                string abCooldown = abilityElem.Attribute("cooldown")?.Value ??
                                    abilityElem.Attribute("cooldowntime")?.Value ?? string.Empty;
                string abDisp = abilityElem.Attribute("displayName")?.Value ??
                                abilityElem.Attribute("displayname")?.Value ?? abName;

                AbilityDefinitionDto ability = new()
                {
                    Slot = ParseInt(abilityElem.Attribute("slot")?.Value),
                    Name = abName,
                    DisplayName = abDisp,
                    Icon = abIcon,
                    IconPath = abIcon,
                    Anim = abilityElem.Attribute("anim")?.Value ?? string.Empty,
                    MaxLevel = ParseInt(
                        abilityElem.Attribute("maxLevel")?.Value ?? abilityElem.Attribute("maxlevel")?.Value, 4),
                    RequiredLevel = abilityElem.Attribute("requiredlevel")?.Value ??
                                    abilityElem.Attribute("requiredLevel")?.Value ?? string.Empty,
                    ActionType = abilityElem.Attribute("actionType")?.Value ??
                                 abilityElem.Attribute("actiontype")?.Value ?? string.Empty,
                    TargetScheme = abilityElem.Attribute("targetscheme")?.Value ??
                                   abilityElem.Attribute("targetScheme")?.Value ?? string.Empty,
                    CastEffectType = abilityElem.Attribute("casteffecttype")?.Value ??
                                     abilityElem.Attribute("castEffectType")?.Value ?? string.Empty,
                    CastEffect = abilityElem.Attribute("casteffect")?.Value ??
                                 abilityElem.Attribute("castEffect")?.Value ?? string.Empty,
                    CastTime = ParseInt(abilityElem.Attribute("casttime")?.Value ??
                                        abilityElem.Attribute("castTime")?.Value),
                    CastActionTime = ParseInt(abilityElem.Attribute("castactiontime")?.Value ??
                                              abilityElem.Attribute("castActionTime")?.Value),
                    ManaCost = abilityElem.Attribute("manaCost")?.Value ??
                               abilityElem.Attribute("manacost")?.Value ?? string.Empty,
                    TargetRadius = abilityElem.Attribute("targetRadius")?.Value ??
                                   abilityElem.Attribute("targetradius")?.Value ?? string.Empty,
                    KeepToggleOnWhenDead = string.Equals(abilityElem.Attribute("keeptoggleonwhendead")?.Value, "true",
                        StringComparison.OrdinalIgnoreCase),
                    Range = abilityElem.Attribute("range")?.Value ?? string.Empty,
                    CooldownTime = abCooldown,
                    Cooldown = abCooldown,
                    TooltipNumParam1 = abilityElem.Attribute("tooltipNumParam1")?.Value ??
                                       abilityElem.Attribute("tooltipnumparam1")?.Value ?? string.Empty,
                    TooltipNumParam2 = abilityElem.Attribute("tooltipNumParam2")?.Value ??
                                       abilityElem.Attribute("tooltipnumparam2")?.Value ?? string.Empty,
                    AreaCastMaterial = abilityElem.Attribute("areacastmaterial")?.Value ??
                                       abilityElem.Attribute("areaCastMaterial")?.Value ?? string.Empty,
                    ShowAreaCast = string.Equals(abilityElem.Attribute("showareacast")?.Value, "true",
                        StringComparison.OrdinalIgnoreCase),
                    TargetPriorities = abilityElem.Attribute("targetpriorities")?.Value ?? string.Empty
                };

                hero.Abilities.Add(ability);
                hero.AbilityNames.Add(ability.Name);
            }

            // Parse alt avatars
            foreach (XElement avatarElem in heroElem.Elements("altavatar"))
            {
                string avKey = avatarElem.Attribute("key")?.Value ?? string.Empty;
                string avPath = avatarElem.Attribute("path")?.Value ?? string.Empty;
                string avIcon = avatarElem.Attribute("iconPath")?.Value ??
                                avatarElem.Attribute("icon")?.Value ?? string.Empty;
                string avModel = avatarElem.Attribute("defaultModel")?.Value ??
                                 avatarElem.Attribute("model")?.Value ?? string.Empty;
                string avDisp = avatarElem.Attribute("displayName")?.Value ??
                                avatarElem.Attribute("displayname")?.Value ??
                                avatarElem.Attribute("name")?.Value ?? avKey;

                AltAvatarDto avatar = new()
                {
                    Key = avKey,
                    HeroName = hero.Name,
                    DisplayName = avDisp,
                    Path = avPath,
                    IconPath = avIcon,
                    DefaultModel = avModel,
                    ModelPath = avModel,
                    DefaultEffect = avatarElem.Attribute("defaultEffect")?.Value ??
                                    avatarElem.Attribute("effect")?.Value ?? string.Empty,
                    SelectedSound = avatarElem.Attribute("selectedsound")?.Value ??
                                    avatarElem.Attribute("selectedSound")?.Value ?? string.Empty,
                    ConfirmMoveSound = avatarElem.Attribute("confirmmovesound")?.Value ??
                                       avatarElem.Attribute("confirmMoveSound")?.Value ?? string.Empty,
                    ConfirmAttackSound = avatarElem.Attribute("confirmattacksound")?.Value ??
                                         avatarElem.Attribute("confirmAttackSound")?.Value ?? string.Empty,
                    CooldownSound = avatarElem.Attribute("cooldownsound")?.Value ??
                                    avatarElem.Attribute("cooldownSound")?.Value ?? string.Empty,
                    NoManaSound = avatarElem.Attribute("nomanasound")?.Value ??
                                  avatarElem.Attribute("noManaSound")?.Value ?? string.Empty,
                    SelectedFlavorSound = avatarElem.Attribute("selectedflavorsound")?.Value ??
                                          avatarElem.Attribute("selectedFlavorSound")?.Value ?? string.Empty,
                    TauntedSound = avatarElem.Attribute("tauntedsound")?.Value ??
                                   avatarElem.Attribute("tauntedSound")?.Value ?? string.Empty,
                    TauntKillSound = avatarElem.Attribute("tauntkillsound")?.Value ??
                                     avatarElem.Attribute("tauntKillSound")?.Value ?? string.Empty,
                    LocalizeUnitSounds = string.Equals(avatarElem.Attribute("localizeunitsounds")?.Value, "true",
                        StringComparison.OrdinalIgnoreCase),
                    PreviewModel = avatarElem.Attribute("previewmodel")?.Value ??
                                   avatarElem.Attribute("previewModel")?.Value ?? string.Empty,
                    PreviewEffect = avatarElem.Attribute("previeweffect")?.Value ??
                                    avatarElem.Attribute("previewEffect")?.Value ?? string.Empty,
                    PreviewPos = avatarElem.Attribute("previewpos")?.Value ??
                                 avatarElem.Attribute("previewPos")?.Value ?? string.Empty,
                    PreviewAngles = avatarElem.Attribute("previewangles")?.Value ??
                                    avatarElem.Attribute("previewAngles")?.Value ?? string.Empty,
                    PreviewScale =
                        ParseFloat(
                            avatarElem.Attribute("previewscale")?.Value ?? avatarElem.Attribute("previewScale")?.Value,
                            1.0f)
                };

                hero.AltAvatars.Add(avatar);
                hero.AltAvatarKeys.Add(avatar.Key);
            }

            result.Add(hero);
        }

        _logger?.LogInformation("Parsed {Count} heroes and {AvatarCount} alt avatars from heroes.xml", result.Count,
            result.Sum(h => h.AltAvatars.Count));
        return result;
    }

    private static float ParseFloat(string? text, float fallback = 0f)
    {
        return float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture,
            out float val)
            ? val
            : fallback;
    }

    private static int ParseInt(string? text, int fallback = 0)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : fallback;
    }
}
