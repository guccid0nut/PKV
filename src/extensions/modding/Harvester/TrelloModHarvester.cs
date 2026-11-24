// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Kongor.Modding.Harvester.Downloader;
using Kongor.Modding.Harvester.Manifest;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Harvester.Resolvers;
using Kongor.Modding.Harvester.Scraper;

namespace Kongor.Modding.Harvester;

/// <summary>
///     Authoritative Trello community mod harvesting engine for ProjectKongor.
/// </summary>
public class TrelloModHarvester
{
    private readonly StreamingDownloadManager _downloadManager;
    private readonly HttpClient _httpClient;
    private readonly CommunityCatalogGenerator _manifestGenerator;
    private readonly MultiHostDownloadResolver _resolver;
    private readonly TrelloBoardScraper _scraper;

    public TrelloModHarvester(
        TrelloBoardScraper? scraper = null,
        StreamingDownloadManager? downloadManager = null,
        MultiHostDownloadResolver? resolver = null,
        CommunityCatalogGenerator? manifestGenerator = null,
        HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
        _resolver = resolver ?? new MultiHostDownloadResolver();
        _scraper = scraper ?? new TrelloBoardScraper(_resolver);
        _downloadManager = downloadManager ?? new StreamingDownloadManager(_resolver, _httpClient);
        _manifestGenerator = manifestGenerator ?? new CommunityCatalogGenerator();
    }

    /// <summary>
    ///     Scrapes and parses the community mod catalog without downloading binary payloads.
    /// </summary>
    public async Task<List<CommunityModEntry>> ScrapeCatalogAsync(
        string? customCatalogPath = null,
        string? categoryFilter = null,
        CancellationToken ct = default)
    {
        List<CommunityModEntry> entries = await _scraper.ScrapeBoardAsync(customCatalogPath, ct);

        if (!string.IsNullOrWhiteSpace(categoryFilter) &&
            !categoryFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            entries = [.. entries.Where(e => e.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))];

        return entries;
    }

    /// <summary>
    ///     Executes the complete harvesting pipeline: scraping, multi-host resolution, streaming download,
    ///     checksum validation, caching, and manifest generation.
    /// </summary>
    public async Task<HarvestResult> HarvestAsync(
        HarvestOptions options,
        Action<string>? logger = null,
        IProgress<HarvestProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Stopwatch sw = Stopwatch.StartNew();
        HarvestResult result = new();

        void Log(string msg)
        {
            result.Logs.Add(msg);
            logger?.Invoke(msg);
        }

        Log(
            $"[Harvester] Starting Trello Harvesting Pipeline (DryRun: {options.DryRun}, OutDir: '{options.OutputDirectory}')");

        // 1. Scrape catalog
        List<CommunityModEntry> catalog;
        try
        {
            catalog = await ScrapeCatalogAsync(options.CustomCatalogPath, options.CategoryFilter, ct);
        }
        catch (Exception ex)
        {
            Log($"[Harvester] ✗ Fatal scraping error: {ex.Message}");
            result.TotalFailed = 1;
            sw.Stop();
            result.ElapsedTime = sw.Elapsed;
            return result;
        }

        result.TotalDiscovered = catalog.Count;
        result.TotalCataloged = catalog.Count;
        result.Catalog = catalog;

        Log(
            $"[Harvester] Successfully cataloged {catalog.Count} community mods across {catalog.Select(c => c.Category).Distinct().Count()} categories.");

        // 2. Resolve URLs & Filter downloadable items
        List<CommunityModEntry> downloadableMods = [.. catalog.Where(m => m.IsDownloadable)];
        int nonDownloadableCount = catalog.Count - downloadableMods.Count;
        result.TotalSkipped = nonDownloadableCount;

        Log(
            $"[Harvester] Identified {downloadableMods.Count} downloadable mods ({nonDownloadableCount} without direct binaries).");

        if (options.DryRun)
        {
            Log("[Harvester] Dry-run enabled: verifying host resolution without downloading binaries.");
            int resolvedCount = 0;

            await Parallel.ForEachAsync(downloadableMods,
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (mod, token) =>
                {
                    try
                    {
                        ResolvedDownloadTarget target =
                            await _resolver.ResolveAsync(mod.PrimaryDownloadUrl, _httpClient, token);
                        mod.ResolvedDownloadUrl = target.DirectDownloadUrl;
                        mod.ResolvedHost = target.HostType;
                        Interlocked.Increment(ref resolvedCount);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Harvester] ⚠ Resolution warning for '{mod.Name}': {ex.Message}");
                    }
                });

            Log($"[Harvester] Resolved {resolvedCount}/{downloadableMods.Count} download targets.");
        }
        else
        {
            // 3. Execute Parallel Streaming Downloads
            Directory.CreateDirectory(options.OutputDirectory);
            using SemaphoreSlim throttler = new(Math.Max(1, options.MaxParallelDownloads));
            int completed = 0;
            object syncRoot = new();

            List<Task> downloadTasks =
            [
                .. downloadableMods.Select(async mod =>
                {
                    await throttler.WaitAsync(ct);
                    try
                    {
                        DownloadedModInfo info = await _downloadManager.DownloadModAsync(
                            mod,
                            options.OutputDirectory,
                            options.OverwriteExisting,
                            Log,
                            ct);

                        lock (syncRoot)
                        {
                            result.DownloadedMods.Add(info);
                            if (info.WasCached)
                                result.TotalCached++;
                            else
                                result.TotalDownloaded++;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (syncRoot)
                        {
                            result.FailedMods.Add(new FailedModInfo
                            {
                                ModId = mod.Id,
                                ModName = mod.Name,
                                Url = mod.PrimaryDownloadUrl,
                                ErrorMessage = ex.Message,
                                ExceptionType = ex.GetType().Name
                            });
                            result.TotalFailed++;
                        }

                        Log($"[Harvester] ✗ Failed to harvest '{mod.Name}': {ex.Message}");
                    }
                    finally
                    {
                        throttler.Release();
                        int currentDone = Interlocked.Increment(ref completed);
                        progress?.Report(new HarvestProgressInfo
                        {
                            TotalItems = downloadableMods.Count,
                            CompletedItems = currentDone,
                            CurrentItemName = mod.Name,
                            StatusMessage = $"Processed {currentDone}/{downloadableMods.Count} mods"
                        });
                    }
                })
            ];

            await Task.WhenAll(downloadTasks);
        }

        // 4. Generate Manifests
        if (options.GenerateManifests)
        {
            string jsonPath = Path.Combine(options.OutputDirectory, "trello_community_catalog.json");
            string mdPath = Path.Combine(options.OutputDirectory, "trello_community_catalog.md");

            await _manifestGenerator.SaveManifestsAsync(catalog, jsonPath, mdPath, result, ct);
            result.ManifestPathJson = jsonPath;
            result.ManifestPathMd = mdPath;

            Log($"[Harvester] ✓ Generated master catalog manifests:\n  JSON: {jsonPath}\n  MD:   {mdPath}");
        }

        sw.Stop();
        result.ElapsedTime = sw.Elapsed;

        Log(
            $"[Harvester] Harvesting finished in {sw.Elapsed.TotalSeconds:F2}s. Total: {result.TotalCataloged}, Downloaded: {result.TotalDownloaded}, Cached: {result.TotalCached}, Failed: {result.TotalFailed}, Skipped: {result.TotalSkipped}");

        return result;
    }
}
