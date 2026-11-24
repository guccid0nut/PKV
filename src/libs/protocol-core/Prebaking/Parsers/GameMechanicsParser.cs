// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class GameMechanicsParser(ILogger<GameMechanicsParser>? logger = null) : IEntityParser<GameMechanicsDto>
{
    private readonly ILogger<GameMechanicsParser>? _logger = logger;

    public async Task<GameMechanicsDto> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<GameMechanicsDto> ParseStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        XElement? root = doc.Root;
        if (root == null) throw new InvalidDataException("Root element missing in base.gamemechanics");

        GameMechanicsDto mechanics = new();

        // Attack types
        foreach (XElement elem in root.Elements("attacktype"))
            mechanics.AttackTypes.Add(new AttackTypeDto
            {
                Name = elem.Attribute("name")?.Value ?? string.Empty,
                DeniedExpMultiplier =
                    ParseFloat(
                        elem.Attribute("deniedexpmultiplier")?.Value ?? elem.Attribute("deniedExpMultiplier")?.Value,
                        1.0f),
                UphillMissChance = ParseFloat(elem.Attribute("uphillmisschance")?.Value ??
                                              elem.Attribute("uphillMissChance")?.Value)
            });

        // Combat types & tables
        XElement? combatTypesElem = root.Element("combattypes") ?? root.Element("combatTypes");
        if (combatTypesElem != null)
            foreach (XElement tableElem in combatTypesElem.Elements("combattable"))
            {
                CombatTableDto table = new()
                {
                    Name = tableElem.Attribute("name")?.Value ?? string.Empty
                };

                foreach (XElement mult in tableElem.Elements("attackmultiplier"))
                {
                    string target = mult.Attribute("target")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(target))
                        table.AttackMultipliers[target] = ParseFloat(mult.Attribute("value")?.Value, 1.0f);
                }

                foreach (XElement mult in tableElem.Elements("spellmultiplier"))
                {
                    string target = mult.Attribute("target")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(target))
                        table.SpellMultipliers[target] = ParseFloat(mult.Attribute("value")?.Value, 1.0f);
                }

                foreach (XElement prio in tableElem.Elements("aggropriority"))
                {
                    string target = prio.Attribute("target")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(target))
                        table.AggroPriorities[target] = ParseInt(prio.Attribute("value")?.Value);
                }

                foreach (XElement prio in tableElem.Elements("attackpriority"))
                {
                    string target = prio.Attribute("target")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(target))
                        table.AttackPriorities[target] = ParseInt(prio.Attribute("value")?.Value);
                }

                foreach (XElement prio in tableElem.Elements("proximitypriority"))
                {
                    string target = prio.Attribute("target")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(target))
                        table.ProximityPriorities[target] = ParseInt(prio.Attribute("value")?.Value);
                }

                foreach (XElement prio in tableElem.Elements("targetpriority"))
                {
                    string target = prio.Attribute("target")?.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(target))
                        table.TargetPriorities[target] = ParseInt(prio.Attribute("value")?.Value);
                }

                mechanics.CombatTables.Add(table);
            }

        // Popups
        foreach (XElement popupElem in root.Elements("popup"))
            mechanics.Popups.Add(new PopupDto
            {
                Name = popupElem.Attribute("name")?.Value ?? string.Empty,
                Color = popupElem.Attribute("color")?.Value ?? string.Empty,
                Message = popupElem.Attribute("message")?.Value ?? string.Empty,
                Value = string.Equals(popupElem.Attribute("value")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                Duration = ParseInt(popupElem.Attribute("duration")?.Value),
                FadeTime = ParseInt(popupElem.Attribute("fadetime")?.Value ?? popupElem.Attribute("fadeTime")?.Value),
                Visibility = popupElem.Attribute("visibility")?.Value ?? string.Empty
            });

        // Immunity types
        foreach (XElement elem in root.Elements("immunitytype"))
            mechanics.ImmunityTypes.Add(new ImmunityTypeDto
            {
                Name = elem.Attribute("name")?.Value ?? string.Empty,
                Immune = elem.Attribute("immune")?.Value ?? string.Empty
            });

        // Target schemes
        foreach (XElement elem in root.Elements("targetscheme"))
            mechanics.TargetSchemes.Add(new TargetSchemeDto
            {
                Name = elem.Attribute("name")?.Value ?? string.Empty,
                Allow = elem.Attribute("allow")?.Value ?? string.Empty
            });

        // Stealth types
        foreach (XElement elem in root.Elements("stealthtype"))
        {
            string sName = elem.Attribute("name")?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(sName)) mechanics.StealthTypes.Add(sName);
        }

        // Reveal types
        foreach (XElement elem in root.Elements("revealtype"))
            mechanics.RevealTypes.Add(new RevealTypeDto
            {
                Name = elem.Attribute("name")?.Value ?? string.Empty,
                Reveal = elem.Attribute("reveal")?.Value ?? string.Empty
            });

        // Effect types
        foreach (XElement elem in root.Elements("effecttype"))
        {
            string eName = elem.Attribute("name")?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(eName)) mechanics.EffectTypes.Add(eName);
        }

        // Hero categories
        foreach (XElement elem in root.Elements("herocategory"))
        {
            string cName = elem.Attribute("name")?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(cName)) mechanics.HeroCategories.Add(cName);
        }

        // Armor types
        foreach (XElement elem in root.Elements("armortype"))
            mechanics.ArmorTypes.Add(new ArmorTypeDto
            {
                Name = elem.Attribute("name")?.Value ?? string.Empty,
                Effects = elem.Attribute("effects")?.Value ?? string.Empty,
                Factor = ParseFloat(elem.Attribute("factor")?.Value, 1.0f)
            });

        _logger?.LogInformation("Parsed GameMechanics with {TableCount} combat tables, {PopupCount} popups",
            mechanics.CombatTables.Count, mechanics.Popups.Count);
        return mechanics;
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
