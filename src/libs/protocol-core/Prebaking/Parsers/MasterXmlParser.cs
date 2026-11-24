// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class MasterXmlParser(
    HeroesXmlParser? heroesParser = null,
    HeroesPreloadResParser? preloadParser = null,
    HeroesPreviewXmlParser? previewParser = null,
    BaseUpgradesParser? upgradesParser = null,
    GameMechanicsParser? mechanicsParser = null,
    EntityFileParser? entityFileParser = null,
    ILogger<MasterXmlParser>? logger = null)
{
    private readonly EntityFileParser _entityFileParser = entityFileParser ?? new EntityFileParser();
    private readonly HeroesXmlParser _heroesParser = heroesParser ?? new HeroesXmlParser();
    private readonly ILogger<MasterXmlParser>? _logger = logger;
    private readonly GameMechanicsParser _mechanicsParser = mechanicsParser ?? new GameMechanicsParser();
    private readonly HeroesPreloadResParser _preloadParser = preloadParser ?? new HeroesPreloadResParser();
    private readonly HeroesPreviewXmlParser _previewParser = previewParser ?? new HeroesPreviewXmlParser();
    private readonly BaseUpgradesParser _upgradesParser = upgradesParser ?? new BaseUpgradesParser();

    public async Task<EntityCacheContainer> ParseMasterDataAsync(
        string clientDir,
        string? sharedDir = null,
        CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        _logger?.LogInformation("Starting Master Data Parsing from clientDir={ClientDir}, sharedDir={SharedDir}",
            clientDir, sharedDir);

        string timestamp = TimeProvider.System.GetUtcNow().ToString("O");
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = timestamp,
            BuildTimestamp = timestamp
        };

        string gameDir = Path.Combine(clientDir, "game");
        if (!Directory.Exists(gameDir) && Directory.Exists(clientDir))
            // clientDir might already be game dir
            if (File.Exists(Path.Combine(clientDir, "heroes.xml")) || File.Exists(Path.Combine(clientDir, "resources0.s2z")))
                gameDir = clientDir;

        // Open resources0.s2z as archive fallback if it exists in gameDir
        string r0Path = Path.Combine(gameDir, "resources0.s2z");
        using ZipArchive? r0Archive = File.Exists(r0Path) ? ZipFile.OpenRead(r0Path) : null;

        // 1. Parse heroes.xml
        string heroesXmlPath = Path.Combine(gameDir, "heroes.xml");
        if (File.Exists(heroesXmlPath))
        {
            _logger?.LogInformation("Parsing heroes.xml from {Path}", heroesXmlPath);
            container.Heroes = await _heroesParser.ParseFileAsync(heroesXmlPath, ct).ConfigureAwait(false);
        }
        else if (r0Archive != null)
        {
            ZipArchiveEntry? entry = r0Archive.GetEntry("heroes.xml");
            if (entry != null)
            {
                _logger?.LogInformation("Parsing heroes.xml from resources0.s2z archive entry");
                await using Stream entryStream = entry.Open();
                container.Heroes = await _heroesParser.ParseStreamAsync(entryStream, ct).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogWarning("heroes.xml not found at {Path} or in resources0.s2z", heroesXmlPath);
            }
        }
        else
        {
            _logger?.LogWarning("heroes.xml not found at {Path}", heroesXmlPath);
        }

        if (container.Heroes.Count > 0)
        {
            // Populate accurate Team / Faction from hero.entity
            foreach (HeroDefinitionDto hero in container.Heroes)
            {
                string heroFolder = hero.Path.TrimStart('/');
                string? entityCandidate1 = !string.IsNullOrEmpty(sharedDir)
                    ? Path.Combine(sharedDir, heroFolder, "hero.entity")
                    : null;
                string? entityCandidate2 = Path.Combine(gameDir, heroFolder, "hero.entity");

                string? targetEntity = entityCandidate1 != null && File.Exists(entityCandidate1)
                    ? entityCandidate1
                    : File.Exists(entityCandidate2)
                        ? entityCandidate2
                        : null;

                if (targetEntity != null)
                {
                    try
                    {
                        XDocument hDoc = XDocument.Load(targetEntity);
                        string? teamAttr = hDoc.Root?.Attribute("team")?.Value;
                        if (!string.IsNullOrEmpty(teamAttr))
                        {
                            hero.Team = teamAttr;
                            hero.Faction = teamAttr;
                        }
                    }
                    catch
                    {
                    }
                }
                else if (r0Archive != null)
                {
                    string zipPath = $"{heroFolder}/hero.entity";
                    ZipArchiveEntry? hEntry = r0Archive.GetEntry(zipPath) ?? r0Archive.GetEntry(zipPath.Replace('\\', '/'));
                    if (hEntry != null)
                    {
                        try
                        {
                            using Stream hStream = hEntry.Open();
                            XDocument hDoc = XDocument.Load(hStream);
                            string? teamAttr = hDoc.Root?.Attribute("team")?.Value;
                            if (!string.IsNullOrEmpty(teamAttr))
                            {
                                hero.Team = teamAttr;
                                hero.Faction = teamAttr;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        // 2. Parse heroes_preloadres.xml
        string preloadXmlPath = Path.Combine(gameDir, "heroes_preloadres.xml");
        Dictionary<string, List<string>>? preloadMap = null;
        if (File.Exists(preloadXmlPath))
        {
            _logger?.LogInformation("Parsing heroes_preloadres.xml from {Path}", preloadXmlPath);
            preloadMap = await _preloadParser.ParseFileAsync(preloadXmlPath, ct).ConfigureAwait(false);
        }
        else if (r0Archive != null)
        {
            ZipArchiveEntry? entry = r0Archive.GetEntry("heroes_preloadres.xml");
            if (entry != null)
            {
                _logger?.LogInformation("Parsing heroes_preloadres.xml from resources0.s2z");
                await using Stream entryStream = entry.Open();
                preloadMap = await _preloadParser.ParseStreamAsync(entryStream, ct).ConfigureAwait(false);
            }
        }

        if (preloadMap != null)
        {
            HashSet<string> allResources = new(StringComparer.OrdinalIgnoreCase);
            List<PreloadResourceDto> preloadDtos = [];

            foreach (HeroDefinitionDto hero in container.Heroes)
                if (preloadMap.TryGetValue(hero.Name, out List<string>? resList))
                {
                    hero.PreloadResources = resList;
                    foreach (string res in resList)
                    {
                        allResources.Add(res);
                        preloadDtos.Add(new PreloadResourceDto
                        {
                            ResourcePath = res,
                            HeroOrEntityName = hero.Name,
                            ResourceType = CategorizeResourceType(res)
                        });
                    }
                }

            container.AllPreloadResources = [.. allResources.OrderBy(r => r)];
            container.PreloadResources = preloadDtos;
        }

        // 3. Parse heroes_preview.xml
        string previewXmlPath = Path.Combine(gameDir, "heroes_preview.xml");
        Dictionary<string, List<PreviewPresetDto>>? previewMap = null;
        if (File.Exists(previewXmlPath))
        {
            _logger?.LogInformation("Parsing heroes_preview.xml from {Path}", previewXmlPath);
            previewMap = await _previewParser.ParseFileAsync(previewXmlPath, ct).ConfigureAwait(false);
        }
        else if (r0Archive != null)
        {
            ZipArchiveEntry? entry = r0Archive.GetEntry("heroes_preview.xml");
            if (entry != null)
            {
                _logger?.LogInformation("Parsing heroes_preview.xml from resources0.s2z");
                await using Stream entryStream = entry.Open();
                previewMap = await _previewParser.ParseStreamAsync(entryStream, ct).ConfigureAwait(false);
            }
        }

        if (previewMap != null)
        {
            foreach (HeroDefinitionDto hero in container.Heroes)
            foreach (AltAvatarDto avatar in hero.AltAvatars)
                if (previewMap.TryGetValue(avatar.Key, out List<PreviewPresetDto>? presets) ||
                    previewMap.TryGetValue(hero.Name, out presets))
                    avatar.PreviewPresets = presets;
        }

        // 4. Parse base.upgrades
        string upgradesPath = Path.Combine(gameDir, "base.upgrades");
        if (File.Exists(upgradesPath))
        {
            _logger?.LogInformation("Parsing base.upgrades from {Path}", upgradesPath);
            container.CosmeticUpgrades = await _upgradesParser.ParseFileAsync(upgradesPath, ct).ConfigureAwait(false);
        }
        else if (r0Archive != null)
        {
            ZipArchiveEntry? entry = r0Archive.GetEntry("base.upgrades");
            if (entry != null)
            {
                _logger?.LogInformation("Parsing base.upgrades from resources0.s2z");
                await using Stream entryStream = entry.Open();
                container.CosmeticUpgrades = await _upgradesParser.ParseStreamAsync(entryStream, ct).ConfigureAwait(false);
            }
        }

        // 5. Parse base.gamemechanics
        string mechanicsPath = Path.Combine(gameDir, "base.gamemechanics");
        if (File.Exists(mechanicsPath))
        {
            _logger?.LogInformation("Parsing base.gamemechanics from {Path}", mechanicsPath);
            container.GameMechanics = await _mechanicsParser.ParseFileAsync(mechanicsPath, ct).ConfigureAwait(false);
        }
        else if (r0Archive != null)
        {
            ZipArchiveEntry? entry = r0Archive.GetEntry("base.gamemechanics");
            if (entry != null)
            {
                _logger?.LogInformation("Parsing base.gamemechanics from resources0.s2z");
                await using Stream entryStream = entry.Open();
                container.GameMechanics = await _mechanicsParser.ParseStreamAsync(entryStream, ct).ConfigureAwait(false);
            }
        }

        // 6. Harvest .entity files: first from sharedDir if available, otherwise from resources0..20.s2z archives
        bool harvestedFromShared = false;
        if (!string.IsNullOrWhiteSpace(sharedDir) && Directory.Exists(sharedDir))
        {
            string[] entityFiles = Directory.GetFiles(sharedDir, "*.entity", SearchOption.AllDirectories);
            if (entityFiles.Length > 0)
            {
                harvestedFromShared = true;
                _logger?.LogInformation("Discovered {Count} loose .entity files in {Dir}", entityFiles.Length, sharedDir);

                foreach (string entityFile in entityFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    object? parsed = await _entityFileParser.ParseEntityFileAsync(entityFile, ct).ConfigureAwait(false);
                    AddEntityToContainer(container, parsed);
                }
            }
        }

        // Fallback: If sharedDir had no entities, stream directly from resources0.s2z .. resources20.s2z in gameDir
        if (!harvestedFromShared && Directory.Exists(gameDir))
        {
            _logger?.LogInformation("sharedDir entity source absent or empty; harvesting .entity definitions from .s2z archives in {Dir}", gameDir);

            List<string> candidateArchives = [];
            for (int r = 0; r <= 20; r++)
            {
                string s2zFile = Path.Combine(gameDir, $"resources{r}.s2z");
                if (File.Exists(s2zFile)) candidateArchives.Add(s2zFile);
            }

            foreach (string s2z in Directory.GetFiles(gameDir, "*.s2z"))
            {
                string fn = Path.GetFileName(s2z).ToLowerInvariant();
                if (!fn.Contains("textures") && !fn.Contains("prebaked") && !candidateArchives.Contains(s2z, StringComparer.OrdinalIgnoreCase))
                {
                    candidateArchives.Add(s2z);
                }
            }

            Dictionary<string, object> entitiesByPath = new(StringComparer.OrdinalIgnoreCase);

            foreach (string archivePath in candidateArchives)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using ZipArchive archive = ZipFile.OpenRead(archivePath);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".entity", StringComparison.OrdinalIgnoreCase))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                await using Stream entryStream = entry.Open();
                                object? parsed = await _entityFileParser.ParseEntityStreamAsync(entryStream, entry.FullName, ct).ConfigureAwait(false);
                                if (parsed != null)
                                {
                                    entitiesByPath[entry.FullName] = parsed;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Failed parsing entity from archive {Archive}: {Entry}", archivePath, entry.FullName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed reading .s2z archive for entities: {Archive}", archivePath);
                }
            }

            foreach (object parsed in entitiesByPath.Values)
            {
                AddEntityToContainer(container, parsed);
            }

            _logger?.LogInformation("Harvested {Count} unique .entity definitions across {ArchiveCount} archive(s)", entitiesByPath.Count, candidateArchives.Count);
        }

        // Flatten all alt avatars into container list
        List<AltAvatarDto> allAvatars = [];
        foreach (HeroDefinitionDto hero in container.Heroes) allAvatars.AddRange(hero.AltAvatars);
        container.AltAvatars = allAvatars;

        container.TotalEntityCount = container.Heroes.Count + container.AltAvatars.Count +
                                     container.Abilities.Count + container.States.Count +
                                     container.Projectiles.Count + container.Items.Count +
                                     container.CosmeticUpgrades.Count;

        sw.Stop();
        _logger?.LogInformation("MasterXmlParser finished parsing {Total} entities in {ElapsedMs} ms",
            container.TotalEntityCount, sw.ElapsedMilliseconds);
        return container;
    }

    private static string CategorizeResourceType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".model" => "Model",
            ".mdf" => "ModelDef",
            ".clip" => "Clip",
            ".material" => "Material",
            ".tga" or ".dds" or ".png" => "Texture",
            ".effect" => "Effect",
            ".wav" or ".mp3" or ".ogg" => "Sound",
            ".shader" => "Shader",
            _ => "Other"
        };
    }

    private static void AddEntityToContainer(EntityCacheContainer container, object? parsed)
    {
        switch (parsed)
        {
            case AbilityDefinitionDto ability:
                container.Abilities.Add(ability);
                break;
            case StateDefinitionDto state:
                container.States.Add(state);
                break;
            case ProjectileDefinitionDto proj:
                container.Projectiles.Add(proj);
                break;
            case ItemDefinitionDto item:
                container.Items.Add(item);
                break;
        }
    }
}
