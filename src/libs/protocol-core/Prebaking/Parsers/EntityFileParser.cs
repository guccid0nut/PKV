// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class EntityFileParser(ILogger<EntityFileParser>? logger = null)
{
    private readonly ILogger<EntityFileParser>? _logger = logger;

    public async Task<object?> ParseEntityFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ParseEntityStreamAsync(stream, filePath, ct).ConfigureAwait(false);
    }

    public async Task<object?> ParseEntityStreamAsync(Stream stream, string sourcePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        XElement? root = doc.Root;
        if (root == null) return null;

        string localName = root.Name.LocalName.ToLowerInvariant();
        string baseFileName = Path.GetFileNameWithoutExtension(sourcePath);

        switch (localName)
        {
            case "ability":
            case "abilityattribute":
            {
                string name = root.Attribute("name")?.Value ?? baseFileName;
                string icon = root.Attribute("icon")?.Value ?? string.Empty;
                string cd = root.Attribute("cooldowntime")?.Value ?? root.Attribute("cooldown")?.Value ?? string.Empty;
                string disp = root.Attribute("displayName")?.Value ?? root.Attribute("displayname")?.Value ?? name;

                return new AbilityDefinitionDto
                {
                    Name = name,
                    DisplayName = disp,
                    Icon = icon,
                    IconPath = icon,
                    Anim = root.Attribute("anim")?.Value ?? string.Empty,
                    CastTime = int.TryParse(root.Attribute("casttime")?.Value ?? root.Attribute("castTime")?.Value,
                        out int ctVal)
                        ? ctVal
                        : 0,
                    CastActionTime =
                        int.TryParse(root.Attribute("castactiontime")?.Value ?? root.Attribute("castActionTime")?.Value,
                            out int catVal)
                            ? catVal
                            : 0,
                    MaxLevel = int.TryParse(root.Attribute("maxlevel")?.Value ?? root.Attribute("maxLevel")?.Value,
                        out int mlVal)
                        ? mlVal
                        : 4,
                    RequiredLevel = root.Attribute("requiredlevel")?.Value ??
                                    root.Attribute("requiredLevel")?.Value ?? string.Empty,
                    ActionType = root.Attribute("actiontype")?.Value ??
                                 root.Attribute("actionType")?.Value ?? string.Empty,
                    TargetScheme = root.Attribute("targetscheme")?.Value ??
                                   root.Attribute("targetScheme")?.Value ?? string.Empty,
                    CastEffectType = root.Attribute("casteffecttype")?.Value ??
                                     root.Attribute("castEffectType")?.Value ?? string.Empty,
                    CastEffect = root.Attribute("casteffect")?.Value ??
                                 root.Attribute("castEffect")?.Value ?? string.Empty,
                    ManaCost = root.Attribute("manacost")?.Value ?? root.Attribute("manaCost")?.Value ?? string.Empty,
                    CooldownTime = cd,
                    Cooldown = cd,
                    Range = root.Attribute("range")?.Value ?? string.Empty,
                    TargetRadius = root.Attribute("targetradius")?.Value ??
                                   root.Attribute("targetRadius")?.Value ?? string.Empty,
                    AreaCastMaterial = root.Attribute("areacastmaterial")?.Value ??
                                       root.Attribute("areaCastMaterial")?.Value ?? string.Empty
                };
            }

            case "state":
            case "statenetaccum":
            {
                string name = root.Attribute("name")?.Value ?? baseFileName;
                string icon = root.Attribute("icon")?.Value ?? string.Empty;

                return new StateDefinitionDto
                {
                    Name = name,
                    Icon = icon,
                    PassiveEffect = root.Attribute("passiveeffect")?.Value ??
                                    root.Attribute("passiveEffect")?.Value ?? string.Empty,
                    EffectType = root.Attribute("effecttype")?.Value ??
                                 root.Attribute("effectType")?.Value ?? string.Empty,
                    AllowTransfer =
                        string.Equals(root.Attribute("allowtransfer")?.Value ?? root.Attribute("allowTransfer")?.Value,
                            "true", StringComparison.OrdinalIgnoreCase),
                    IsHidden = string.Equals(root.Attribute("ishidden")?.Value ?? root.Attribute("isHidden")?.Value,
                        "true", StringComparison.OrdinalIgnoreCase),
                    StatusEffectTooltip = root.Attribute("statuseffecttooltip")?.Value ??
                                          root.Attribute("statusEffectTooltip")?.Value ?? string.Empty,
                    ModifierKey = root.Attribute("modifierkey")?.Value ??
                                  root.Attribute("modifierKey")?.Value ?? string.Empty,
                    Duration = int.TryParse(root.Attribute("duration")?.Value, out int durVal) ? durVal : 0
                };
            }

            case "projectile":
            {
                string name = root.Attribute("name")?.Value ?? baseFileName;
                return new ProjectileDefinitionDto
                {
                    Name = name,
                    Model = root.Attribute("model")?.Value ?? string.Empty,
                    ModelScale =
                        float.TryParse(root.Attribute("modelscale")?.Value ?? root.Attribute("modelScale")?.Value,
                            out float ms)
                            ? ms
                            : 1.0f,
                    EffectScale =
                        float.TryParse(root.Attribute("effectscale")?.Value ?? root.Attribute("effectScale")?.Value,
                            out float es)
                            ? es
                            : 1.0f,
                    ImpactEffect = root.Attribute("impacteffect")?.Value ??
                                   root.Attribute("impactEffect")?.Value ?? string.Empty,
                    TrailEffect = root.Attribute("traileffect")?.Value ??
                                  root.Attribute("trailEffect")?.Value ?? string.Empty,
                    DeathEffect = root.Attribute("deatheffect")?.Value ??
                                  root.Attribute("deathEffect")?.Value ?? string.Empty,
                    Speed = float.TryParse(root.Attribute("speed")?.Value, out float sp) ? sp : 0f,
                    MaxSpeed = float.TryParse(root.Attribute("maxspeed")?.Value ?? root.Attribute("maxSpeed")?.Value,
                        out float msp)
                        ? msp
                        : 0f,
                    Gravity = float.TryParse(root.Attribute("gravity")?.Value, out float grav) ? grav : 0f,
                    Flying = string.Equals(root.Attribute("flying")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                    FlyHeight = float.TryParse(root.Attribute("flyheight")?.Value ?? root.Attribute("flyHeight")?.Value,
                        out float fh)
                        ? fh
                        : 0f,
                    TouchRadius =
                        float.TryParse(root.Attribute("touchradius")?.Value ?? root.Attribute("touchRadius")?.Value,
                            out float tr)
                            ? tr
                            : 0f,
                    TouchTargetScheme = root.Attribute("touchtargetscheme")?.Value ??
                                        root.Attribute("touchTargetScheme")?.Value ?? string.Empty,
                    LifeTime = int.TryParse(root.Attribute("lifetime")?.Value ?? root.Attribute("lifeTime")?.Value,
                        out int lt)
                        ? lt
                        : 0,
                    CanTurn = string.Equals(root.Attribute("canturn")?.Value ?? root.Attribute("canTurn")?.Value,
                        "true", StringComparison.OrdinalIgnoreCase),
                    Disjointable = string.Equals(root.Attribute("disjointable")?.Value, "true",
                        StringComparison.OrdinalIgnoreCase),
                    Homing = string.Equals(root.Attribute("homing")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                    HomingTurnSpeed =
                        float.TryParse(
                            root.Attribute("homingturnspeed")?.Value ?? root.Attribute("homingTurnSpeed")?.Value,
                            out float hts)
                            ? hts
                            : 0f
                };
            }

            case "item":
            {
                string name = root.Attribute("name")?.Value ?? baseFileName;
                string rawIcon = root.Attribute("icon")?.Value ?? string.Empty;
                string icon = ResolveVirtualResourcePath(sourcePath, rawIcon);
                string disp = root.Attribute("displayName")?.Value ?? root.Attribute("displayname")?.Value ?? name;

                return new ItemDefinitionDto
                {
                    Name = name,
                    DisplayName = disp,
                    Icon = icon,
                    IconPath = icon,
                    Cost = int.TryParse(root.Attribute("cost")?.Value, out int costVal) ? costVal : 0,
                    ActionType = root.Attribute("actiontype")?.Value ??
                                 root.Attribute("actionType")?.Value ?? string.Empty,
                    ActivateScheme = root.Attribute("activatescheme")?.Value ??
                                     root.Attribute("activateScheme")?.Value ?? string.Empty,
                    CastTime = int.TryParse(root.Attribute("casttime")?.Value ?? root.Attribute("castTime")?.Value,
                        out int itemCt)
                        ? itemCt
                        : 0,
                    CastActionTime =
                        int.TryParse(root.Attribute("castactiontime")?.Value ?? root.Attribute("castActionTime")?.Value,
                            out int itemCat)
                            ? itemCat
                            : 0,
                    CooldownTime =
                        int.TryParse(root.Attribute("cooldowntime")?.Value ?? root.Attribute("cooldownTime")?.Value,
                            out int itemCd)
                            ? itemCd
                            : 0,
                    CooldownType = root.Attribute("cooldowntype")?.Value ??
                                   root.Attribute("cooldownType")?.Value ?? string.Empty,
                    CastEffect = root.Attribute("casteffect")?.Value ??
                                 root.Attribute("castEffect")?.Value ?? string.Empty,
                    StatusEffectTooltip = root.Attribute("statuseffecttooltip")?.Value ??
                                          root.Attribute("statusEffectTooltip")?.Value ?? string.Empty,
                    AllowSharing =
                        string.Equals(root.Attribute("allowsharing")?.Value ?? root.Attribute("allowSharing")?.Value,
                            "true", StringComparison.OrdinalIgnoreCase),
                    NonInterrupting =
                        string.Equals(
                            root.Attribute("noninterrupting")?.Value ?? root.Attribute("nonInterrupting")?.Value,
                            "true", StringComparison.OrdinalIgnoreCase),
                    NoBreakChannel =
                        string.Equals(
                            root.Attribute("nobreakchannel")?.Value ?? root.Attribute("noBreakChannel")?.Value, "true",
                            StringComparison.OrdinalIgnoreCase),
                    NonAggressive =
                        string.Equals(root.Attribute("nonaggressive")?.Value ?? root.Attribute("nonAggressive")?.Value,
                            "true", StringComparison.OrdinalIgnoreCase)
                };
            }

            default:
                return null;
        }
    }

    private static string ResolveVirtualResourcePath(string sourcePath, string relativeResource)
    {
        if (string.IsNullOrWhiteSpace(relativeResource)) return string.Empty;
        if (relativeResource.StartsWith('/')) return relativeResource;

        string normalized = sourcePath.Replace('\\', '/');
        int idx = normalized.IndexOf("/items/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = normalized.IndexOf("/heroes/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = normalized.IndexOf("/buildings/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = normalized.IndexOf("/props/", StringComparison.OrdinalIgnoreCase);

        if (idx >= 0)
        {
            string sub = normalized.Substring(idx);
            int lastSlash = sub.LastIndexOf('/');
            if (lastSlash > 0)
            {
                string dir = sub.Substring(0, lastSlash);
                return $"{dir}/{relativeResource}";
            }
        }
        else if (normalized.StartsWith("items/", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("heroes/", StringComparison.OrdinalIgnoreCase))
        {
            int lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
            {
                string dir = normalized.Substring(0, lastSlash);
                return $"/{dir}/{relativeResource}";
            }
        }

        return $"/{relativeResource}";
    }
}
