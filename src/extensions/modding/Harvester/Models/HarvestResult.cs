// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace Kongor.Modding.Harvester.Models;

/// <summary>
///     Detailed result information for a successfully downloaded and verified community mod.
/// </summary>
public class DownloadedModInfo
{
    public string ModId { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256Hash { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public DownloadHostType ResolvedHost { get; set; }
    public bool WasCached { get; set; }
    public bool IsValidHonMod { get; set; }
    public string ValidationNotes { get; set; } = string.Empty;
}

/// <summary>
///     Detailed error information for a mod that failed during harvest or verification.
/// </summary>
public class FailedModInfo
{
    public string ModId { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
}

/// <summary>
///     Progress reporting data emitted during harvesting.
/// </summary>
public class HarvestProgressInfo
{
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public string CurrentItemName { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public double ProgressPercentage => TotalItems > 0 ? (double)CompletedItems / TotalItems * 100.0 : 0.0;
}

/// <summary>
///     Aggregated execution results of the Trello harvesting pipeline.
/// </summary>
public class HarvestResult
{
    public bool Success => FailedMods.Count == 0 || DownloadedMods.Count > 0;
    public int TotalDiscovered { get; set; }
    public int TotalCataloged { get; set; }
    public int TotalDownloaded { get; set; }
    public int TotalCached { get; set; }
    public int TotalFailed { get; set; }
    public int TotalSkipped { get; set; }
    public List<DownloadedModInfo> DownloadedMods { get; set; } = [];
    public List<FailedModInfo> FailedMods { get; set; } = [];
    public List<CommunityModEntry> Catalog { get; set; } = [];
    public string? ManifestPathJson { get; set; }
    public string? ManifestPathMd { get; set; }
    public List<string> Logs { get; set; } = [];
    public TimeSpan ElapsedTime { get; set; }
}
