// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class HeroesPreviewXmlParser(ILogger<HeroesPreviewXmlParser>? logger = null)
    : IEntityParser<Dictionary<string, List<PreviewPresetDto>>>
{
    private readonly ILogger<HeroesPreviewXmlParser>? _logger = logger;

    public async Task<Dictionary<string, List<PreviewPresetDto>>> ParseFileAsync(string filePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, List<PreviewPresetDto>>> ParseStreamAsync(Stream stream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        XElement? root = doc.Root;
        if (root == null) throw new InvalidDataException("Root element missing in heroes_preview.xml");

        // Key can be HeroName (for base hero) or AvatarKey (e.g. "Hero_Accursed.Alt")
        Dictionary<string, List<PreviewPresetDto>> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (XElement heroElem in root.Elements("hero"))
        {
            ct.ThrowIfCancellationRequested();
            string heroName = heroElem.Attribute("name")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(heroName)) continue;

            foreach (XElement avatarElem in heroElem.Elements("altavatar"))
            {
                string? avatarKey = avatarElem.Attribute("key")?.Value;
                string dictionaryKey = string.IsNullOrWhiteSpace(avatarKey) ? heroName : avatarKey;

                if (!result.TryGetValue(dictionaryKey, out List<PreviewPresetDto>? presetList))
                {
                    presetList = [];
                    result[dictionaryKey] = presetList;
                }

                PreviewPresetDto preset = new()
                {
                    Type = avatarElem.Attribute("type")?.Value ?? "preview",
                    ModelPath = avatarElem.Attribute("modelPath")?.Value ??
                                avatarElem.Attribute("modelpath")?.Value ?? string.Empty,
                    EffectPath = avatarElem.Attribute("effectPath")?.Value ??
                                 avatarElem.Attribute("effectpath")?.Value ?? string.Empty,
                    ModelScale =
                        ParseFloat(
                            avatarElem.Attribute("modelScale")?.Value ?? avatarElem.Attribute("modelscale")?.Value,
                            1.0f),
                    ModelPosition = avatarElem.Attribute("modelPosition")?.Value ??
                                    avatarElem.Attribute("modelposition")?.Value ?? string.Empty,
                    ModelAngle = avatarElem.Attribute("modelAngle")?.Value ??
                                 avatarElem.Attribute("modelangle")?.Value ?? string.Empty,
                    Ambient = avatarElem.Attribute("ambient")?.Value ?? string.Empty,
                    SunHeight = ParseFloat(avatarElem.Attribute("sunHeight")?.Value ??
                                           avatarElem.Attribute("sunheight")?.Value),
                    SunAngle = ParseFloat(avatarElem.Attribute("sunAngle")?.Value ??
                                          avatarElem.Attribute("sunangle")?.Value),
                    SunColor = avatarElem.Attribute("sunColor")?.Value ??
                               avatarElem.Attribute("suncolor")?.Value ?? string.Empty,
                    PedestalScale =
                        ParseFloat(
                            avatarElem.Attribute("pedestalScale")?.Value ??
                            avatarElem.Attribute("pedestalscale")?.Value, 1.0f),
                    UsingEffect =
                        string.Equals(
                            avatarElem.Attribute("usingEffect")?.Value ?? avatarElem.Attribute("usingeffect")?.Value,
                            "true", StringComparison.OrdinalIgnoreCase),
                    NewFeatureModel =
                        string.Equals(
                            avatarElem.Attribute("newfeaturemodel")?.Value ??
                            avatarElem.Attribute("newFeatureModel")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                };

                presetList.Add(preset);
            }
        }

        _logger?.LogInformation("Parsed {Count} hero/avatar preview configs from heroes_preview.xml", result.Count);
        return result;
    }

    private static float ParseFloat(string? text, float fallback = 0f)
    {
        return float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture,
            out float val)
            ? val
            : fallback;
    }
}
