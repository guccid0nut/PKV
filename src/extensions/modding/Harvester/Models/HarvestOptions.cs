// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace Kongor.Modding.Harvester.Models;

/// <summary>
///     Configuration options controlling the Trello mod harvesting pipeline.
/// </summary>
public class HarvestOptions
{
    /// <summary>
    ///     Local output directory where harvested mods and manifests are stored.
    /// </summary>
    public string OutputDirectory { get; set; } = "harvested_mods";

    /// <summary>
    ///     Optional category filter (e.g. "Working mods", "Broken/Outdated mods", "All"). Null or empty scrapes everything.
    /// </summary>
    public string? CategoryFilter { get; set; }

    /// <summary>
    ///     When true, scrapes cards, resolves URLs, and generates manifests without downloading remote binary files.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    ///     When true, forces re-downloading even if a valid matching file already exists locally.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    ///     Network timeout in seconds per HTTP operation.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum degree of parallelism for concurrent downloads.
    /// </summary>
    public int MaxParallelDownloads { get; set; } = 4;

    /// <summary>
    ///     Optional local path to pre-downloaded Trello JSON catalog for offline execution or tests.
    /// </summary>
    public string? CustomCatalogPath { get; set; }

    /// <summary>
    ///     Whether to generate trello_community_catalog.json and .md manifests upon completion.
    /// </summary>
    public bool GenerateManifests { get; set; } = true;
}
