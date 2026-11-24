// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Serialization;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Challenger;

public sealed class AdversarialRoundtripDtoTests
{
    private static EntityCacheContainer CreateFullyPopulatedContainer()
    {
        return new EntityCacheContainer
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            BuildTimestamp = TimeProvider.System.GetUtcNow().ToString("O"),
            ChecksumXxHash3 = 0,
            TotalEntityCount = 10,
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 101,
                    Name = "Hero_Devourer",
                    DisplayName = "Devourer",
                    Path = "/heroes/devourer",
                    EntityPath = "/heroes/devourer/hero.entity",
                    DefaultIcon = "/heroes/devourer/icon.tga",
                    IconPath = "/heroes/devourer/icon.tga",
                    DefaultModel = "hd_devourer/preview.mdf",
                    ModelPath = "hd_devourer/preview.mdf",
                    DefaultEffect = "effects/body.effect",
                    Category = "ganker disabler",
                    Difficulty = 4.5f,
                    PrimaryAttribute = "strength",
                    Strength = 25f,
                    StrengthPerLevel = 3.2f,
                    Agility = 14f,
                    AgilityPerLevel = 1.5f,
                    Intelligence = 17f,
                    IntelligencePerLevel = 1.8f,
                    AttackType = "Melee",
                    AttackDamageMin = 52f,
                    AttackDamageMax = 58f,
                    AttackRange = 128f,
                    AttackCooldown = 1700,
                    MoveSpeed = 300f,
                    Armor = 1.96f,
                    MagicArmor = 5.5f,
                    SoloRating = 4,
                    JungleRating = 2,
                    CarryRating = 3,
                    SupportRating = 1,
                    InitiatorRating = 5,
                    GankerRating = 5,
                    PusherRating = 2,
                    RangedRating = 1,
                    MeleeRating = 5,
                    InventorySlots = ["Item_Bottle", "Item_Boots"],
                    RecommendedItems = ["Item_HelmOfTheBlackLegion", "Item_BehemothsHeart", "Item_PortalKey"],
                    AbilityNames = ["Ability_Devourer1", "Ability_Devourer2", "Ability_Devourer3", "Ability_Devourer4"],
                    AltAvatarKeys = ["Hero_Devourer.Alt1", "Hero_Devourer.Alt2"],
                    PreloadResources = ["/heroes/devourer/hook.mdf", "/heroes/devourer/sounds/rot.wav"],
                    Abilities =
                    [
                        new AbilityDefinitionDto
                        {
                            Name = "Ability_Devourer1",
                            DisplayName = "Gudgeon Hook",
                            Slot = 0,
                            Icon = "/heroes/devourer/ability_01/icon.tga",
                            IconPath = "/heroes/devourer/ability_01/icon.tga",
                            Anim = "ability_1",
                            MaxLevel = 4,
                            RequiredLevel = "1,3,5,7",
                            ActionType = "target_position",
                            TargetScheme = "all_units",
                            CastEffectType = "SuperiorMagic",
                            CastEffect = "cast.effect",
                            CastTime = 300,
                            CastActionTime = 200,
                            ManaCost = "110,120,130,140",
                            CooldownTime = "14000,13000,12000,11000",
                            Cooldown = "14000,13000,12000,11000",
                            Range = "1100",
                            TargetRadius = "75",
                            KeepToggleOnWhenDead = false,
                            TooltipNumParam1 = "100,200,300,400",
                            TooltipNumParam2 = "1100",
                            AreaCastMaterial = "/shared/materials/area_cast.material",
                            ShowAreaCast = true,
                            TargetPriorities = "target_enemy",
                            ModifierKeys = ["hook_bonus"],
                            AppliedStates = ["State_Devourer_HookStun"]
                        }
                    ],
                    AltAvatars =
                    [
                        new AltAvatarDto
                        {
                            Key = "Hero_Devourer.Alt1",
                            HeroName = "Hero_Devourer",
                            DisplayName = "Clown Devourer",
                            Path = "/alt",
                            IconPath = "/heroes/devourer/alt/icon.tga",
                            DefaultModel = "preview.mdf",
                            ModelPath = "preview.mdf",
                            DefaultEffect = "effects/clown.effect",
                            PreviewModel = "/heroes/devourer/alt/preview.mdf",
                            PreviewEffect = "/heroes/devourer/alt/effects/clown.effect",
                            PreviewPos = "0 0 -15",
                            PreviewAngles = "0 0 45",
                            PreviewScale = 1.15f,
                            SelectedSound = "sounds/voice/clown_select.wav",
                            ConfirmMoveSound = "sounds/voice/clown_move.wav",
                            ConfirmAttackSound = "sounds/voice/clown_attack.wav",
                            CooldownSound = "sounds/voice/clown_cooldown.wav",
                            NoManaSound = "sounds/voice/clown_nomana.wav",
                            SelectedFlavorSound = "sounds/voice/clown_flavor.wav",
                            TauntedSound = "sounds/voice/clown_taunted.wav",
                            TauntKillSound = "sounds/voice/clown_tauntkill.wav",
                            LocalizeUnitSounds = true,
                            PreviewPresets =
                            [
                                new PreviewPresetDto
                                {
                                    Type = "preview",
                                    ModelPath = "heroes/devourer/alt/preview.mdf",
                                    EffectPath = "heroes/devourer/alt/effects/clown.effect",
                                    ModelScale = 1.2f,
                                    ModelPosition = "0 0 -10",
                                    ModelAngle = "0 0 30",
                                    Ambient = "0.8 0.8 0.8",
                                    SunHeight = 120f,
                                    SunAngle = 45f,
                                    SunColor = "1 1 1",
                                    PedestalScale = 1.0f,
                                    UsingEffect = true,
                                    NewFeatureModel = false
                                }
                            ]
                        }
                    ]
                }
            ],
            AltAvatars =
            [
                new AltAvatarDto
                {
                    Key = "Hero_Devourer.Alt1",
                    HeroName = "Hero_Devourer",
                    DisplayName = "Clown Devourer",
                    IconPath = "/heroes/devourer/alt/icon.tga"
                }
            ],
            Abilities =
            [
                new AbilityDefinitionDto
                {
                    Name = "Ability_Devourer1",
                    DisplayName = "Gudgeon Hook",
                    Slot = 0,
                    MaxLevel = 4
                }
            ],
            States =
            [
                new StateDefinitionDto
                {
                    Name = "State_Devourer_Rot",
                    Icon = "/heroes/devourer/ability_02/icon.tga",
                    PassiveEffect = "rot.effect",
                    EffectType = "StatusDebuff",
                    AllowTransfer = false,
                    IsHidden = false,
                    ModifierKey = "rot_slow",
                    MoveSpeedMultiplier = -0.2f,
                    AttackSpeedMultiplier = -0.1f,
                    Stunned = false,
                    Silenced = false,
                    Disarmed = false,
                    Perplexed = false,
                    Revealed = false,
                    Invulnerable = false,
                    Duration = 0,
                    StatusEffectTooltip = "Deals magic damage and slows nearby units."
                }
            ],
            Projectiles =
            [
                new ProjectileDefinitionDto
                {
                    Name = "Projectile_Devourer_Hook",
                    Model = "/heroes/devourer/ability_01/hook.mdf",
                    ModelScale = 1.0f,
                    EffectScale = 1.0f,
                    ImpactEffect = "impact.effect",
                    TrailEffect = "trail.effect",
                    DeathEffect = "death.effect",
                    Speed = 1600f,
                    MaxSpeed = 2000f,
                    Gravity = 0f,
                    Flying = true,
                    FlyHeight = 50f,
                    TouchRadius = 75f,
                    TouchTargetScheme = "all_units",
                    LifeTime = 1200,
                    CanTurn = false,
                    Disjointable = false,
                    Homing = false,
                    HomingTurnSpeed = 0f
                }
            ],
            Items =
            [
                new ItemDefinitionDto
                {
                    Name = "Item_PortalKey",
                    DisplayName = "Portal Key",
                    Icon = "/items/portal_key/icon.tga",
                    IconPath = "/items/portal_key/icon.tga",
                    Cost = 2150,
                    Components = [],
                    ActionType = "target_position",
                    ActivateScheme = "target_cursor",
                    CastTime = 0,
                    CastActionTime = 0,
                    CooldownTime = 14000,
                    CooldownType = "portal_key",
                    CastEffect = "blink.effect",
                    StatusEffectTooltip = "Teleports to target position up to 1200 units away.",
                    AllowSharing = false,
                    NonInterrupting = true,
                    NoBreakChannel = false,
                    NonAggressive = true,
                    Strength = 0f,
                    Agility = 0f,
                    Intelligence = 0f,
                    Armor = 0f,
                    MagicArmor = 0f,
                    Damage = 0f,
                    Modifiers = ["blink_ready"]
                }
            ],
            CosmeticUpgrades =
            [
                new CosmeticUpgradeDto
                {
                    Name = "flamboyant",
                    Category = "Announcer",
                    Texture = "/ui/icons/announcers/flamboyant.tga",
                    Modifier = "Announcer_Flamboyant",
                    Color = "#FF69B4",
                    InGameColor = "#FF69B4",
                    SortIndex = 12,
                    VoiceSet = "flamboyant",
                    ArcadeText = "RAINBOW!"
                }
            ],
            GameMechanics = new GameMechanicsDto
            {
                AttackTypes =
                [
                    new AttackTypeDto { Name = "Melee", DeniedExpMultiplier = 0.5f, UphillMissChance = 0f },
                    new AttackTypeDto { Name = "Ranged", DeniedExpMultiplier = 0.35f, UphillMissChance = 0.25f }
                ],
                CombatTables =
                [
                    new CombatTableDto
                    {
                        Name = "Hero",
                        AttackMultipliers = new Dictionary<string, float> { ["Hero"] = 1.0f, ["Structure"] = 0.5f },
                        SpellMultipliers = new Dictionary<string, float> { ["Hero"] = 1.0f, ["Structure"] = 0.0f },
                        AggroPriorities = new Dictionary<string, int> { ["Hero"] = 1, ["Creep"] = 2 }
                    }
                ],
                StealthTypes = ["Invisibility", "Camouflage"],
                RevealTypes = [new RevealTypeDto { Name = "TrueSight", Reveal = "Stealth" }],
                EffectTypes = ["Magic", "Physical", "Pure", "SuperiorMagic"],
                ImmunityTypes = [new ImmunityTypeDto { Name = "MagicImmunity", Immune = "Magic" }],
                HeroCategories = ["Strength", "Agility", "Intelligence"],
                ArmorTypes = [new ArmorTypeDto { Name = "Hero", Effects = "HeroArmor", Factor = 0.06f }],
                Popups =
                [
                    new PopupDto
                    {
                        Name = "CriticalHit",
                        Color = "#FF0000",
                        Message = "CRITICAL!",
                        Value = true,
                        Duration = 1500,
                        FadeTime = 300,
                        Visibility = "all"
                    }
                ],
                TargetSchemes =
                [
                    new TargetSchemeDto { Name = "all_units", Allow = "enemy,ally,neutral" }
                ]
            },
            AllPreloadResources = ["/heroes/devourer/hook.mdf", "/heroes/devourer/sounds/rot.wav"],
            PreloadResources =
            [
                new PreloadResourceDto
                {
                    ResourcePath = "/heroes/devourer/hook.mdf", ResourceType = "model",
                    HeroOrEntityName = "Hero_Devourer"
                }
            ]
        };
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(22)]
    public void Roundtrip_AllDtoTypes_ZstdCompressionLevels_MaintainsPerfectEquivalence(int compressionLevel)
    {
        EntityCacheContainer original = CreateFullyPopulatedContainer();

        byte[] serialized = EntityCacheSerializer.Serialize(original, compressionLevel, true);
        EntityCacheContainer restored = EntityCacheDeserializer.Deserialize(serialized);

        restored.Should().NotBeNull();
        restored.Version.Should().Be(original.Version);
        restored.Heroes.Should().BeEquivalentTo(original.Heroes);
        restored.AltAvatars.Should().BeEquivalentTo(original.AltAvatars);
        restored.Abilities.Should().BeEquivalentTo(original.Abilities);
        restored.States.Should().BeEquivalentTo(original.States);
        restored.Projectiles.Should().BeEquivalentTo(original.Projectiles);
        restored.Items.Should().BeEquivalentTo(original.Items);
        restored.CosmeticUpgrades.Should().BeEquivalentTo(original.CosmeticUpgrades);
        restored.GameMechanics.Should().BeEquivalentTo(original.GameMechanics);
        restored.PreloadResources.Should().BeEquivalentTo(original.PreloadResources);
        restored.AllPreloadResources.Should().BeEquivalentTo(original.AllPreloadResources);
    }

    [Fact]
    public void Roundtrip_AllDtoTypes_RawUncompressedMode_MaintainsPerfectEquivalence()
    {
        EntityCacheContainer original = CreateFullyPopulatedContainer();

        byte[] serialized = EntityCacheSerializer.Serialize(original, compress: false);
        EntityCacheContainer restored = EntityCacheDeserializer.Deserialize(serialized);

        restored.Should().NotBeNull();
        restored.Heroes.Should().BeEquivalentTo(original.Heroes);
        restored.AltAvatars.Should().BeEquivalentTo(original.AltAvatars);
        restored.Abilities.Should().BeEquivalentTo(original.Abilities);
        restored.States.Should().BeEquivalentTo(original.States);
        restored.Projectiles.Should().BeEquivalentTo(original.Projectiles);
        restored.Items.Should().BeEquivalentTo(original.Items);
        restored.CosmeticUpgrades.Should().BeEquivalentTo(original.CosmeticUpgrades);
        restored.GameMechanics.Should().BeEquivalentTo(original.GameMechanics);
        restored.PreloadResources.Should().BeEquivalentTo(original.PreloadResources);
    }
}
