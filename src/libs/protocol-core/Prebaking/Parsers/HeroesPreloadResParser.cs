// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class HeroesPreloadResParser(ILogger<HeroesPreloadResParser>? logger = null)
    : IEntityParser<Dictionary<string, List<string>>>
{
    private readonly ILogger<HeroesPreloadResParser>? _logger = logger;

    public async Task<Dictionary<string, List<string>>> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, List<string>>> ParseStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        XElement? root = doc.Root;
        if (root == null) throw new InvalidDataException("Root element missing in heroes_preloadres.xml");

        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        int totalFiles = 0;

        foreach (XElement heroElem in root.Elements("hero"))
        {
            ct.ThrowIfCancellationRequested();
            string heroName = heroElem.Attribute("name")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(heroName)) continue;

            List<string> fileList = [];
            XElement? resList = heroElem.Element("reslist");
            if (resList != null)
                foreach (XElement fileElem in resList.Elements("file"))
                {
                    string path = fileElem.Attribute("path")?.Value ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        fileList.Add(path);
                        totalFiles++;
                    }
                }

            result[heroName] = fileList;
        }

        _logger?.LogInformation("Parsed {HeroCount} heroes with {FileCount} preload resources", result.Count,
            totalFiles);
        return result;
    }
}
