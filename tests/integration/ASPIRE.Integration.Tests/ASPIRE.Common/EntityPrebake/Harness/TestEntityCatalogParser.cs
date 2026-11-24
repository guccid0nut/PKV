// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml.Linq;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;

public sealed class TestEntityCatalogParser
{
    public EntityCacheContainer ParseFromXmlContent(string heroesXml, string? preloadResXml = null)
    {
        if (string.IsNullOrWhiteSpace(heroesXml))
            throw new ArgumentException("Heroes XML content cannot be null or empty.", nameof(heroesXml));

        XDocument doc = XDocument.Parse(heroesXml);
        XElement root = doc.Root ?? throw new InvalidOperationException("XML root element missing.");

        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("o")
        };

        Dictionary<string, List<string>> preloadMap = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preloadResXml))
        {
            XDocument preloadDoc = XDocument.Parse(preloadResXml);
            if (preloadDoc.Root != null)
                foreach (XElement heroElem in preloadDoc.Root.Elements("hero"))
                {
                    string? hName = heroElem.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(hName)) continue;

                    List<string> resList = [];
                    foreach (XElement resElem in heroElem.Elements("resource"))
                    {
                        string? path = resElem.Attribute("path")?.Value;
                        if (!string.IsNullOrEmpty(path)) resList.Add(path);
                    }

                    preloadMap[hName] = resList;
                }
        }

        foreach (XElement heroElem in root.Elements("hero"))
        {
            string idStr = heroElem.Attribute("id")?.Value ?? "0";
            uint id = uint.TryParse(idStr, out uint parsedId) ? parsedId : 0;
            string name = heroElem.Attribute("name")?.Value ?? string.Empty;
            string displayName = heroElem.Attribute("displayname")?.Value ?? name;
            string primaryAttribute = heroElem.Attribute("primaryattribute")?.Value ?? "Strength";
            string iconPath = heroElem.Attribute("icon")?.Value ?? string.Empty;
            string modelPath = heroElem.Attribute("model")?.Value ?? string.Empty;

            HeroDefinitionDto hero = new()
            {
                Id = id,
                Name = name,
                DisplayName = displayName,
                PrimaryAttribute = primaryAttribute,
                IconPath = iconPath,
                ModelPath = modelPath,
                AbilityNames = [],
                AltAvatarKeys = [],
                PreloadResources = preloadMap.TryGetValue(name, out List<string>? preloads) ? [.. preloads] : []
            };

            foreach (XElement abilityElem in heroElem.Elements("ability"))
            {
                string? abilityName = abilityElem.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(abilityName))
                {
                    hero.AbilityNames.Add(abilityName);
                    AbilityDefinitionDto abilityDto = new()
                    {
                        Name = abilityName,
                        DisplayName = abilityElem.Attribute("displayname")?.Value ?? abilityName,
                        IconPath = abilityElem.Attribute("icon")?.Value ??
                                   $"/abilities/{abilityName.ToLowerInvariant()}/icon.tga",
                        ManaCost = abilityElem.Attribute("manacost")?.Value ?? "0",
                        Cooldown = abilityElem.Attribute("cooldown")?.Value ?? "0",
                        ActionType = abilityElem.Attribute("actiontype")?.Value ?? "target_entity"
                    };
                    container.Abilities.Add(abilityDto);
                }
            }

            foreach (XElement avatarElem in heroElem.Elements("altavatar"))
            {
                string? avatarKey = avatarElem.Attribute("key")?.Value;
                if (!string.IsNullOrEmpty(avatarKey))
                {
                    hero.AltAvatarKeys.Add(avatarKey);
                    string priorityStr = avatarElem.Attribute("priority")?.Value ?? "0";
                    int priority = int.TryParse(priorityStr, out int parsedPriority) ? parsedPriority : 0;

                    AltAvatarDto avatarDto = new()
                    {
                        Key = avatarKey,
                        HeroName = name,
                        DisplayName = avatarElem.Attribute("displayname")?.Value ?? $"{displayName} {avatarKey}",
                        IconPath = avatarElem.Attribute("icon")?.Value ??
                                   $"/heroes/{name.ToLowerInvariant()}/{avatarKey}/icon.tga",
                        ModelPath = avatarElem.Attribute("model")?.Value ??
                                    $"/heroes/{name.ToLowerInvariant()}/{avatarKey}/model.mdf",
                        Priority = priority
                    };
                    container.AltAvatars.Add(avatarDto);
                }
            }

            container.Heroes.Add(hero);
        }

        return container;
    }

    public EntityDefinitionDto ParseLooseEntityFile(string filePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Loose entity content cannot be empty.", nameof(content));

        XDocument doc = XDocument.Parse(content);
        XElement root = doc.Root ?? throw new InvalidOperationException("Entity XML root element missing.");

        string entityType = root.Name.LocalName; // e.g. "ability", "state", "projectile", "hero", "item"
        string name = root.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(filePath);

        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        foreach (XAttribute attr in root.Attributes()) properties[attr.Name.LocalName] = attr.Value;

        return new EntityDefinitionDto
        {
            Name = name,
            EntityType = entityType,
            SourcePath = filePath,
            Properties = properties
        };
    }
}
