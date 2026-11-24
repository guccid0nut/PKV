// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;

public static class TestPrebakeHarness
{
    public static EntityCacheContainer CreateSampleCatalog(int heroCount = 5, int avatarsPerHero = 3)
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("o")
        };

        for (uint i = 1; i <= (uint)heroCount; i++)
        {
            string heroName = $"Hero_{i}";
            HeroDefinitionDto hero = new()
            {
                Id = i,
                Name = heroName,
                DisplayName = $"Hero Display {i}",
                PrimaryAttribute = i % 3 == 0 ? "Intelligence" : i % 3 == 1 ? "Strength" : "Agility",
                IconPath = $"/heroes/{heroName.ToLowerInvariant()}/icon.tga",
                ModelPath = $"/heroes/{heroName.ToLowerInvariant()}/model.mdf",
                AbilityNames = [$"{heroName}_Ability_1", $"{heroName}_Ability_2", $"{heroName}_Ultimate"],
                AltAvatarKeys = [],
                PreloadResources =
                [
                    $"/heroes/{heroName.ToLowerInvariant()}/sounds/spawn.wav",
                    $"/heroes/{heroName.ToLowerInvariant()}/effects/cast.effect"
                ]
            };

            for (int a = 1; a <= avatarsPerHero; a++)
            {
                string avatarKey = $"alt{a}";
                hero.AltAvatarKeys.Add(avatarKey);

                AltAvatarDto avatar = new()
                {
                    Key = avatarKey,
                    HeroName = heroName,
                    DisplayName = $"{hero.DisplayName} Avatar {a}",
                    IconPath = $"/heroes/{heroName.ToLowerInvariant()}/alt{a}/icon.tga",
                    ModelPath = $"/heroes/{heroName.ToLowerInvariant()}/alt{a}/model.mdf",
                    Priority = a * 10,
                    Modifiers = ["custom_voice", "custom_particles"],
                    PreloadResources =
                    [
                        $"/heroes/{heroName.ToLowerInvariant()}/alt{a}/sounds/voice_{a}.wav"
                    ]
                };
                container.AltAvatars.Add(avatar);
            }

            foreach (string abilityName in hero.AbilityNames)
            {
                AbilityDefinitionDto ability = new()
                {
                    Name = abilityName,
                    DisplayName = $"Spell {abilityName}",
                    IconPath = $"/abilities/{abilityName.ToLowerInvariant()}/icon.tga",
                    ManaCost = "100,110,120,130",
                    Cooldown = "15,12,9,6",
                    ActionType = "target_entity",
                    RequiredResources =
                    [
                        $"/abilities/{abilityName.ToLowerInvariant()}/cast.effect"
                    ]
                };
                container.Abilities.Add(ability);
            }

            StateDefinitionDto state = new()
            {
                Name = $"State_{heroName}",
                DisplayName = $"State of {hero.DisplayName}",
                IconPath = $"/states/{heroName.ToLowerInvariant()}/icon.tga",
                EffectPath = $"/states/{heroName.ToLowerInvariant()}/aura.effect",
                IsDebuff = false,
                Duration = 5.0f
            };
            container.States.Add(state);

            ProjectileDefinitionDto projectile = new()
            {
                Name = $"Projectile_{heroName}",
                ModelPath = $"/projectiles/{heroName.ToLowerInvariant()}/arrow.mdf",
                EffectPath = $"/projectiles/{heroName.ToLowerInvariant()}/trail.effect",
                Speed = 1200f,
                Lifetime = 3.0f
            };
            container.Projectiles.Add(projectile);

            container.Heroes.Add(hero);
        }

        ItemDefinitionDto sampleItem = new()
        {
            Id = 1001,
            Name = "Item_PostHaste",
            DisplayName = "Post Haste",
            Cost = 2500,
            IconPath = "/items/post_haste/icon.tga",
            Components = ["Item_Boots", "Item_Recipe_PostHaste"]
        };
        container.Items.Add(sampleItem);

        return container;
    }

    public static string CreateSampleHeroesXml(int heroCount = 3)
    {
        StringBuilder sb = new();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<heroes>");
        for (int i = 1; i <= heroCount; i++)
        {
            sb.AppendLine(
                $"  <hero id=\"{i}\" name=\"Hero_Test_{i}\" displayname=\"Test Hero {i}\" primaryattribute=\"Strength\" icon=\"/heroes/test_{i}/icon.tga\" model=\"/heroes/test_{i}/model.mdf\">");
            sb.AppendLine($"    <ability name=\"Hero_Test_{i}_Ability1\" />");
            sb.AppendLine($"    <ability name=\"Hero_Test_{i}_Ability2\" />");
            sb.AppendLine(
                $"    <altavatar key=\"alt1\" displayname=\"Alt Avatar 1\" icon=\"/heroes/test_{i}/alt1/icon.tga\" model=\"/heroes/test_{i}/alt1/model.mdf\" priority=\"10\" />");
            sb.AppendLine(
                $"    <altavatar key=\"alt2\" displayname=\"Alt Avatar 2\" icon=\"/heroes/test_{i}/alt2/icon.tga\" model=\"/heroes/test_{i}/alt2/model.mdf\" priority=\"20\" />");
            sb.AppendLine("  </hero>");
        }

        sb.AppendLine("</heroes>");
        return sb.ToString();
    }

    public static string CreateSamplePreloadXml()
    {
        StringBuilder sb = new();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<preloadresources>");
        sb.AppendLine("  <hero name=\"Hero_Test_1\">");
        sb.AppendLine("    <resource path=\"/heroes/test_1/sounds/ready.wav\" type=\"sound\" />");
        sb.AppendLine("    <resource path=\"/heroes/test_1/textures/diffuse.dds\" type=\"texture\" />");
        sb.AppendLine("  </hero>");
        sb.AppendLine("</preloadresources>");
        return sb.ToString();
    }

    public static void CreateSampleS2zArchive(string zipPath, Dictionary<string, byte[]> entries)
    {
        string? dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        using FileStream fs = new(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(fs, ZipArchiveMode.Create, false);
        foreach (KeyValuePair<string, byte[]> kvp in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(kvp.Key, CompressionLevel.Optimal);
            using Stream entryStream = entry.Open();
            entryStream.Write(kvp.Value, 0, kvp.Value.Length);
        }
    }
}
