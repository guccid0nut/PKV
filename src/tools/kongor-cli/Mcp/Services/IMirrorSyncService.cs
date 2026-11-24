// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

public sealed class MirrorSyncOptions
{
    public string Target { get; set; } = "all";
    public bool DryRun { get; set; }
    public bool Force { get; set; }
}

public sealed class MirrorSyncResultDto
{
    public string Status { get; set; } = "success";
    public bool Success { get; set; } = true;
    public int TotalFilesEvaluated { get; set; }
    public int TotalFilesCopied { get; set; }
    public int TotalFilesUnchanged { get; set; }
    public long TotalBytesWritten { get; set; }
    public double ElapsedMs { get; set; }
    public List<string> SyncedFiles { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public string Timestamp { get; set; } = string.Empty;
}

public sealed class MirrorDiffItemDto
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty; // "missing_destination" | "content_mismatch"
    public string SourceHash { get; set; } = string.Empty;
    public string DestinationHash { get; set; } = string.Empty;
}

public sealed class MirrorDiffResultDto
{
    public string Status { get; set; } = "synced";
    public bool HasDrift { get; set; }
    public int TotalFilesEvaluated { get; set; }
    public int DriftCount { get; set; }
    public List<MirrorDiffItemDto> DriftingFiles { get; set; } = [];
    public double ElapsedMs { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}

public sealed class MirrorStatusDto
{
    public string Status { get; set; } = "active";
    public bool IsWatching { get; set; }
    public string WatchedPath { get; set; } = string.Empty;
    public int DebounceMs { get; set; }
    public int TotalSyncPasses { get; set; }
    public string? LastSyncTime { get; set; }
    public MirrorSyncResultDto? LastSyncResult { get; set; }
}

public interface IMirrorSyncService : IAsyncDisposable, IDisposable
{
    bool IsWatching { get; }
    int DebounceMs { get; }
    int TotalSyncPasses { get; }
    string WatchedPath { get; }
    MirrorStatusDto GetStatus();

    Task<MirrorSyncResultDto> SyncAsync(MirrorSyncOptions? options = null, CancellationToken cancellationToken = default);
    Task<MirrorDiffResultDto> DiffAsync(string target = "all", CancellationToken cancellationToken = default);

    void StartWatching(int debounceMs = 200);
    void StopWatching();
}
