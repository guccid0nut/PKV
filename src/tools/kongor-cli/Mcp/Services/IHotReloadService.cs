// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Status snapshot returned by kongor_hot_reload.
/// </summary>
public sealed class HotReloadStatusDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "success";

    [JsonPropertyName("isWatching")]
    public bool IsWatching { get; init; }

    [JsonPropertyName("watchedPaths")]
    public IReadOnlyList<string> WatchedPaths { get; init; } = [];

    [JsonPropertyName("debounceMs")]
    public int DebounceMs { get; init; } = 150;

    [JsonPropertyName("totalReloads")]
    public int TotalReloads { get; init; }

    [JsonPropertyName("totalFileEvents")]
    public int TotalFileEvents { get; init; }

    [JsonPropertyName("lastReloadDurationMs")]
    public double LastReloadDurationMs { get; init; }

    [JsonPropertyName("lastChangedFiles")]
    public IReadOnlyList<string> LastChangedFiles { get; init; } = [];

    [JsonPropertyName("lastError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = TimeProvider.System.GetUtcNow().ToString("o");
}

/// <summary>
/// Detailed result of an individual hot reload execution.
/// </summary>
public sealed class HotReloadResultDto
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("status")]
    public string Status => Success ? "success" : "error";

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }

    [JsonPropertyName("changedFiles")]
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    [JsonPropertyName("dispatchedCommands")]
    public IReadOnlyList<string> DispatchedCommands { get; init; } = [];

    [JsonPropertyName("commandResponses")]
    public IReadOnlyList<string> CommandResponses { get; init; } = [];

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage => Error;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = TimeProvider.System.GetUtcNow().ToString("o");
}

/// <summary>
/// Service managing debounced file watching on UI/scripts directories and in-engine reload dispatching over Named Pipe IPC.
/// </summary>
public interface IHotReloadService : IAsyncDisposable, IDisposable
{
    bool IsWatching { get; }
    int DebounceMs { get; }
    IReadOnlyList<string> WatchedPaths { get; }
    int TotalReloads { get; }
    int TotalFileEvents { get; }
    double LastReloadDurationMs { get; }
    IReadOnlyList<string> LastChangedFiles { get; }
    string? LastError { get; }

    HotReloadStatusDto GetStatus();
    void StartWatching(IEnumerable<string>? customPaths = null, int? debounceMs = null);
    Task StartWatchingAsync(IEnumerable<string>? customPaths = null, int? debounceMs = null, CancellationToken cancellationToken = default);
    void StopWatching();
    Task StopWatchingAsync(CancellationToken cancellationToken = default);
    Task<HotReloadResultDto> TriggerReloadAsync(IEnumerable<string>? triggerFiles = null, CancellationToken cancellationToken = default);
    void NotifyFileChanged(string fullPath);

    event Action<HotReloadResultDto>? OnReloadCompleted;
    event Action<HotReloadResultDto>? OnReloadExecuted;
    event Action<string>? OnFileChanged;
    event Action<Exception>? OnError;
}
