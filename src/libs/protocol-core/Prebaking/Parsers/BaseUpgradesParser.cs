// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class BaseUpgradesParser(ILogger<BaseUpgradesParser>? logger = null)
    : IEntityParser<List<CosmeticUpgradeDto>>
{
    private readonly ILogger<BaseUpgradesParser>? _logger = logger;

    public async Task<List<CosmeticUpgradeDto>> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<List<CosmeticUpgradeDto>> ParseStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        XElement? root = doc.Root;
        if (root == null) throw new InvalidDataException("Root element missing in base.upgrades");

        List<CosmeticUpgradeDto> result = [];

        foreach (XElement elem in root.Elements())
        {
            ct.ThrowIfCancellationRequested();
            string localName = elem.Name.LocalName.ToLowerInvariant();
            string category = localName switch
            {
                "chatsymbol" => "ChatSymbol",
                "accounticon" => "AccountIcon",
                "ward" => "Ward",
                "chatnamecolor" => "ChatNameColor",
                "courier" => "Courier",
                "taunt" => "Taunt",
                "announcervoice" => "AnnouncerVoice",
                "tpeffect" => "TpEffect",
                "creep" => "Creep",
                "selectioncircle" => "SelectionCircle",
                _ => elem.Name.LocalName
            };

            CosmeticUpgradeDto upgrade = new()
            {
                Name = elem.Attribute("name")?.Value ?? string.Empty,
                Category = category,
                Texture = elem.Attribute("texture")?.Value ?? elem.Attribute("icon")?.Value ?? string.Empty,
                Modifier = elem.Attribute("modifier")?.Value ?? string.Empty,
                Color = elem.Attribute("color")?.Value ?? string.Empty,
                InGameColor = elem.Attribute("ingamecolor")?.Value ??
                              elem.Attribute("inGameColor")?.Value ?? string.Empty,
                SortIndex = int.TryParse(elem.Attribute("sortindex")?.Value ?? elem.Attribute("sortIndex")?.Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
                    ? s
                    : 0,
                VoiceSet = elem.Attribute("voiceset")?.Value ?? elem.Attribute("voiceSet")?.Value ?? string.Empty,
                ArcadeText = elem.Attribute("arcadetext")?.Value ?? elem.Attribute("arcadeText")?.Value ?? string.Empty
            };

            result.Add(upgrade);
        }

        _logger?.LogInformation("Parsed {Count} cosmetic upgrades from base.upgrades", result.Count);
        return result;
    }
}
