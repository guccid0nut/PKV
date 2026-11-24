// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Production implementation of IHotReloadService with 150ms sliding debounce,
/// thread-safe file event accumulation, UTF-8 BOM sanitation, and sub-200ms IPC reload execution.
/// </summary>
public sealed class HotReloadService : IHotReloadService
{
    private const int DefaultDebounceMs = 150;
    private const int BufferSize = 65536; // 64 KB buffer

    private readonly IGameIpcBridge _ipcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly string _projectRoot;

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];

    private ITimer? _debounceTimer;
    private bool _isWatching;
    private int _debounceMs = DefaultDebounceMs;
    private List<string> _watchedPaths = [];
    private string[] _lastChangedFiles = [];
    private int _totalReloads;
    private int _totalFileEvents;
    private double _lastReloadDurationMs;
    private string? _lastError;
    private bool _isDisposed;

    public bool IsWatching
    {
        get { lock (_stateLock) return _isWatching; }
    }

    public int DebounceMs
    {
        get { lock (_stateLock) return _debounceMs; }
    }

    public IReadOnlyList<string> WatchedPaths
    {
        get { lock (_stateLock) return _watchedPaths.ToArray(); }
    }

    public int TotalReloads => Volatile.Read(ref _totalReloads);
    public int TotalFileEvents => Volatile.Read(ref _totalFileEvents);

    public double LastReloadDurationMs
    {
        get { lock (_stateLock) return _lastReloadDurationMs; }
    }

    public IReadOnlyList<string> LastChangedFiles
    {
        get { lock (_stateLock) return _lastChangedFiles; }
    }

    public string? LastError
    {
        get { lock (_stateLock) return _lastError; }
    }

    public event Action<HotReloadResultDto>? OnReloadCompleted;
    public event Action<HotReloadResultDto>? OnReloadExecuted;
    public event Action<string>? OnFileChanged;
    public event Action<Exception>? OnError;

    public HotReloadService(
        IGameIpcBridge? ipcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? projectRoot = null)
    {
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[HotReloadService] {msg}"));
        _projectRoot = projectRoot ?? FindProjectRoot();
    }

    public HotReloadStatusDto GetStatus()
    {
        lock (_stateLock)
        {
            return new HotReloadStatusDto
            {
                Status = _lastError == null ? "success" : "error",
                IsWatching = _isWatching,
                WatchedPaths = _watchedPaths.ToArray(),
                DebounceMs = _debounceMs,
                TotalReloads = _totalReloads,
                TotalFileEvents = _totalFileEvents,
                LastReloadDurationMs = _lastReloadDurationMs,
                LastChangedFiles = _lastChangedFiles,
                LastError = _lastError,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }
    }

    public void StartWatching(IEnumerable<string>? customPaths = null, int? debounceMs = null)
    {
        lock (_stateLock)
        {
            StopWatchingInternal();

            _debounceMs = debounceMs.GetValueOrDefault(DefaultDebounceMs);
            if (_debounceMs <= 0) _debounceMs = DefaultDebounceMs;

            List<string> targetPaths = [];
            if (customPaths != null)
            {
                foreach (string p in customPaths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    string fullPath = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(_projectRoot, p));
                    targetPaths.Add(fullPath);
                }
            }

            if (targetPaths.Count == 0)
            {
                targetPaths.Add(Path.GetFullPath(Path.Combine(_projectRoot, "games", "client", "override", "ui")));
                targetPaths.Add(Path.GetFullPath(Path.Combine(_projectRoot, "games", "client", "override", "ui", "scripts")));
            }

            _watchedPaths = targetPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (string path in _watchedPaths)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    FileSystemWatcher watcher = new(path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.CreationTime | NotifyFilters.Size,
                        Filter = "*.*",
                        InternalBufferSize = BufferSize
                    };

                    watcher.Changed += OnFileSystemEvent;
                    watcher.Created += OnFileSystemEvent;
                    watcher.Deleted += OnFileSystemEvent;
                    watcher.Renamed += OnFileSystemRenamed;
                    watcher.Error += OnFileSystemError;

                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    _logger($"Active file watcher attached to: '{path}'");
                }
                catch (Exception ex)
                {
                    _logger($"[ERROR] Failed to attach FileSystemWatcher to '{path}': {ex.Message}");
                    OnError?.Invoke(ex);
                }
            }

            _debounceTimer = _timeProvider.CreateTimer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _isWatching = _watchers.Count > 0;
            _logger($"Hot-reload monitoring started. Watched paths: {string.Join(", ", _watchedPaths)} (Debounce: {_debounceMs}ms)");
        }
    }

    public Task StartWatchingAsync(IEnumerable<string>? customPaths = null, int? debounceMs = null, CancellationToken cancellationToken = default)
    {
        StartWatching(customPaths, debounceMs);
        return Task.CompletedTask;
    }

    public void StopWatching()
    {
        lock (_stateLock)
        {
            StopWatchingInternal();
            _isWatching = false;
            _logger("Hot-reload monitoring stopped.");
        }
    }

    public Task StopWatchingAsync(CancellationToken cancellationToken = default)
    {
        StopWatching();
        return Task.CompletedTask;
    }

    private void StopWatchingInternal()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch { }
        }
        _watchers.Clear();

        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _pendingFiles.Clear();
    }

    public void NotifyFileChanged(string fullPath)
    {
        HandleIncomingFileChange(fullPath);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        HandleIncomingFileChange(e.FullPath);
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        HandleIncomingFileChange(e.OldFullPath);
        HandleIncomingFileChange(e.FullPath);
    }

    private void OnFileSystemError(object sender, ErrorEventArgs e)
    {
        Exception ex = e.GetException();
        _logger($"[ERROR] FileSystemWatcher internal error: {ex.Message}");
        OnError?.Invoke(ex);

        // Under buffer overflow, force immediate reload
        if (ex is InternalBufferOverflowException)
        {
            _logger("[WARN] Buffer overflow detected. Queuing emergency reload.");
            _ = Task.Run(async () => await TriggerReloadAsync(["[BUFFER_OVERFLOW]"]).ConfigureAwait(false));
        }
    }

    private void HandleIncomingFileChange(string fullPath)
    {
        if (ShouldIgnoreFile(fullPath)) return;

        string normalized = Path.GetFullPath(fullPath);
        _pendingFiles.TryAdd(normalized, 0);
        Interlocked.Increment(ref _totalFileEvents);
        OnFileChanged?.Invoke(normalized);

        // Sanitize UTF-8 BOM if XML or interface file
        if (normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".interface", StringComparison.OrdinalIgnoreCase))
        {
            CleanUtf8Bom(normalized);
        }

        lock (_stateLock)
        {
            if (_debounceTimer == null)
            {
                // Create timer on demand if watching was started or if NotifyFileChanged was used directly
                _debounceTimer = _timeProvider.CreateTimer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            // Slide debounce window
            _debounceTimer.Change(TimeSpan.FromMilliseconds(_debounceMs), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        string[] files;
        lock (_stateLock)
        {
            files = _pendingFiles.Keys.ToArray();
            _pendingFiles.Clear();
        }

        if (files.Length > 0)
        {
            _ = TriggerReloadAsync(files);
        }
    }

    public async Task<HotReloadResultDto> TriggerReloadAsync(IEnumerable<string>? triggerFiles = null, CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] changedFiles = triggerFiles?.ToArray() ?? [];
            if (changedFiles.Length == 0)
            {
                lock (_stateLock)
                {
                    changedFiles = _pendingFiles.Keys.ToArray();
                    _pendingFiles.Clear();
                }
            }

            if (changedFiles.Length == 0)
            {
                changedFiles = ["[MANUAL_TRIGGER]"];
            }

            _logger($"Executing in-engine hot reload sequence for {changedFiles.Length} file(s)...");

            List<string> dispatchedCommands = ["ReloadInterfaces", "Script dofile('/ui/scripts/nexus_bridge.lua')"];
            List<string> responses = [];
            Stopwatch sw = Stopwatch.StartNew();
            string? error = null;
            bool success = true;

            try
            {
                if (!_ipcBridge.IsConnected)
                {
                    _logger("IPC Bridge offline. Attempting connection to willowmaker-ipc...");
                    await _ipcBridge.ConnectAsync(timeoutMs: 2000, cancellationToken).ConfigureAwait(false);
                }

                // 1. Invalidate cached interfaces and templates
                string resp1 = await _ipcBridge.ExecuteCommandAsync("ReloadInterfaces", timeoutMs: 3000, cancellationToken).ConfigureAwait(false);
                responses.Add(resp1);

                // 2. Re-execute nexus_bridge.lua in global _G.Nexus state
                string resp2 = await _ipcBridge.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", timeoutMs: 3000, cancellationToken).ConfigureAwait(false);
                responses.Add(resp2);
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
                _logger($"[ERROR] IPC reload execution failed: {ex.Message}");
                OnError?.Invoke(ex);
            }

            sw.Stop();
            double durationMs = sw.Elapsed.TotalMilliseconds;

            if (durationMs > 200.0)
            {
                _logger($"[PERF WARNING] Reload execution duration ({durationMs:F1}ms) exceeded 200ms budget.");
            }
            else
            {
                _logger($"Hot reload sequence completed in {durationMs:F1}ms (Success: {success}).");
            }

            lock (_stateLock)
            {
                _lastReloadDurationMs = durationMs;
                _lastChangedFiles = changedFiles;
                _lastError = error;
                if (success)
                {
                    Interlocked.Increment(ref _totalReloads);
                }
            }

            HotReloadResultDto result = new()
            {
                Success = success,
                DurationMs = durationMs,
                ChangedFiles = changedFiles,
                DispatchedCommands = dispatchedCommands,
                CommandResponses = responses,
                Error = error,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            OnReloadCompleted?.Invoke(result);
            OnReloadExecuted?.Invoke(result);
            return result;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private static bool ShouldIgnoreFile(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.StartsWith(".hotreload", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("~", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void CleanUtf8Bom(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            byte[] header = new byte[3];
            using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 3) return;
                int read = fs.Read(header, 0, 3);
                if (read < 3) return;
            }

            if (header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            {
                byte[] allBytes = File.ReadAllBytes(filePath);
                byte[] stripped = new byte[allBytes.Length - 3];
                Array.Copy(allBytes, 3, stripped, 0, stripped.Length);
                File.WriteAllBytes(filePath, stripped);
            }
        }
        catch { }
    }

    private static string FindProjectRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx"))) return current;
            string? parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "ProjectKongor.slnx"))) return baseDir;
            string? parent = Path.GetDirectoryName(baseDir);
            if (parent == null || parent == baseDir) break;
            baseDir = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// #187 (Gem 1I): 8-facet cleanroom equivalence comparator ('identity', 'pseudocode', 'assembly',
    /// 'comments', 'calls', 'references', 'strings_names', 'cfg') with decompiler auto-name
    /// ('FUN_*'/'uVar*'/'iVar*') and NOP-padding normalization.
    /// </summary>
    public CleanroomEquivalenceReport CompareCleanroomEquivalence(
        CleanroomFunctionSnapshot original,
        CleanroomFunctionSnapshot cleanroom)
    {
        CleanroomEquivalenceReport report =
            ReaEpistemicForensicsEngine.CompareCleanroomEquivalence8Facet(original, cleanroom);
        _logger($"[CleanroomEquivalence] SemanticEquivalent={report.IsEquivalent} ({report.MatchedFacetCount}/{report.TotalFacetCount} facets matched).");
        return report;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopWatchingInternal();
        _reloadLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
