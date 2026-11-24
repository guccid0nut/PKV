// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service providing robust, real-time asset supervision across ProjectKongor directories.
/// Implements 300-500ms sliding debounce buffering to wait for bursts (Photoshop, Blender, IDE saves),
/// verifies file lock release prior to ingestion, and performs atomic file swaps (MoveFileEx)
/// to eliminate Win32 ERROR_SHARING_VIOLATION (0x20) exceptions.
/// </summary>
public sealed class AssetWatcherService : IAsyncDisposable, IDisposable
{
    public const int DefaultDebounceMs = 400;

    private readonly IHotReloadService? _hotReloadService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly Lock _stateLock = new();
    private readonly ConcurrentDictionary<string, ITimer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];

    private bool _isWatching;
    private int _debounceMs = DefaultDebounceMs;
    private List<string> _watchedPaths = [];
    private int _totalEventsDebounced;
    private int _totalSwapsExecuted;
    private bool _isDisposed;

    public bool IsWatching
    {
        get { lock (_stateLock) return _isWatching; }
    }

    public int DebounceMs => _debounceMs;
    public IReadOnlyList<string> WatchedPaths
    {
        get { lock (_stateLock) return _watchedPaths.ToArray(); }
    }

    public int TotalEventsDebounced => Volatile.Read(ref _totalEventsDebounced);
    public int TotalSwapsExecuted => Volatile.Read(ref _totalSwapsExecuted);

    public event Action<string>? OnAssetSettled;
    public event Action<string, Exception>? OnError;

    public AssetWatcherService(
        IHotReloadService? hotReloadService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _hotReloadService = hotReloadService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Trace.WriteLine($"[AssetWatcher] {msg}"));
    }

    /// <summary>
    /// Starts watching the specified paths (or default project asset paths) with sliding debounce.
    /// </summary>
    public void StartWatching(IEnumerable<string>? paths = null, int debounceMs = DefaultDebounceMs)
    {
        lock (_stateLock)
        {
            if (_isWatching) return;

            _debounceMs = Math.Max(50, debounceMs);
            _watchedPaths = paths?.ToList() ?? GetDefaultWatchPaths();

            foreach (var path in _watchedPaths)
            {
                if (!Directory.Exists(path))
                {
                    try { Directory.CreateDirectory(path); }
                    catch (Exception ex)
                    {
                        _logger($"Warning: Unable to create watch directory '{path}': {ex.Message}");
                        continue;
                    }
                }

                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        InternalBufferSize = 65536
                    };

                    watcher.Changed += OnFileSystemEvent;
                    watcher.Created += OnFileSystemEvent;
                    watcher.Renamed += (s, e) => HandleDebouncedPath(e.FullPath);
                    watcher.Error += (s, e) => OnError?.Invoke(path, e.GetException());

                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    _logger($"Supervising assets at '{path}' (Debounce: {_debounceMs}ms)");
                }
                catch (Exception ex)
                {
                    _logger($"Failed to initialize FileSystemWatcher for '{path}': {ex.Message}");
                }
            }

            _isWatching = true;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        HandleDebouncedPath(e.FullPath);
    }

    /// <summary>
    /// Buffers the path into a sliding debounce timer. If another write happens within the debounce window,
    /// the timer resets, waiting for disk bursts to settle.
    /// </summary>
    public void HandleDebouncedPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;

        // Ignore temporary swap files or git internals
        if (fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fullPath.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(".git", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Interlocked.Increment(ref _totalEventsDebounced);

        _debounceTimers.AddOrUpdate(
            fullPath,
            path => _timeProvider.CreateTimer(_ => ProcessSettledAsset(path), null, TimeSpan.FromMilliseconds(_debounceMs), Timeout.InfiniteTimeSpan),
            (path, existingTimer) =>
            {
                existingTimer.Change(TimeSpan.FromMilliseconds(_debounceMs), Timeout.InfiniteTimeSpan);
                return existingTimer;
            });
    }

    private void ProcessSettledAsset(string fullPath)
    {
        _debounceTimers.TryRemove(fullPath, out var timer);
        timer?.Dispose();

        try
        {
            // Verify write completion / file lock release by external tools
            if (!WaitUntilFileIsReadable(fullPath, timeoutMs: 1500))
            {
                _logger($"File '{fullPath}' remained locked after debounce window. Skipping hot reload.");
                return;
            }

            _logger($"Asset settled on disk: {Path.GetFileName(fullPath)}");
            OnAssetSettled?.Invoke(fullPath);

            // Forward to hot reload service if available
            if (_hotReloadService != null && _hotReloadService.IsWatching)
            {
                _hotReloadService.NotifyFileChanged(fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger($"Error processing settled asset '{fullPath}': {ex.Message}");
            OnError?.Invoke(fullPath, ex);
        }
    }

    /// <summary>
    /// Performs an atomic file swap (MoveFileEx with MOVEFILE_REPLACE_EXISTING semantics).
    /// Writes stage safely in temp files, eliminating ERROR_SHARING_VIOLATION during live game asset reading.
    /// </summary>
    public bool AtomicReplaceFile(string sourceTempPath, string destinationPath, int maxRetries = 3)
    {
        if (!File.Exists(sourceTempPath))
        {
            _logger($"Source file does not exist for atomic swap: {sourceTempPath}");
            return false;
        }

        string destDir = Path.GetDirectoryName(destinationPath) ?? "";
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        int attempt = 0;
        int delayMs = 50;

        while (true)
        {
            try
            {
                attempt++;
                File.Move(sourceTempPath, destinationPath, overwrite: true);
                Interlocked.Increment(ref _totalSwapsExecuted);
                _logger($"Atomic swap succeeded: {Path.GetFileName(destinationPath)}");
                return true;
            }
            catch (IOException ioEx) when (attempt <= maxRetries)
            {
                _logger($"Attempt {attempt} failed swapping '{destinationPath}' (Win32 lock): {ioEx.Message}. Retrying in {delayMs}ms...");
#pragma warning disable RS0030 // Justification: Synchronous file system polling retry delay
                Task.Delay(delayMs).GetAwaiter().GetResult();
#pragma warning restore RS0030
                delayMs *= 2;
            }
            catch (Exception ex)
            {
                _logger($"Atomic swap failed permanently for '{destinationPath}': {ex.Message}");
                OnError?.Invoke(destinationPath, ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Polls until the file can be opened for reading, verifying that the writer has released its handle.
    /// </summary>
    public static bool WaitUntilFileIsReadable(string filePath, int timeoutMs = 1500)
    {
        if (!File.Exists(filePath)) return false;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length >= 0) return true;
            }
            catch (IOException)
            {
#pragma warning disable RS0030 // Justification: Synchronous file system polling retry delay
                Task.Delay(35).GetAwaiter().GetResult();
#pragma warning restore RS0030
            }
            catch (UnauthorizedAccessException)
            {
#pragma warning disable RS0030 // Justification: Synchronous file system polling retry delay
                Task.Delay(35).GetAwaiter().GetResult();
#pragma warning restore RS0030
            }
        }

        return false;
    }

    public void StopWatching()
    {
        lock (_stateLock)
        {
            if (!_isWatching) return;

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

            foreach (var kvp in _debounceTimers)
            {
                try { kvp.Value.Dispose(); } catch { }
            }
            _debounceTimers.Clear();

            _isWatching = false;
            _logger("Asset supervision stopped.");
        }
    }

    private static List<string> GetDefaultWatchPaths()
    {
        string current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "ProjectKongor.sln")))
        {
            return [
                Path.Combine(current, "games", "client", "ui"),
                Path.Combine(current, "games", "client", "override"),
                Path.Combine(current, "output", "staging")
            ];
        }

        var root = AppContext.BaseDirectory;
        // Ascend to repository root if running from bin/
        var dir = new DirectoryInfo(root);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ProjectKongor.sln")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? root;
        return [
            Path.Combine(repoRoot, "games", "client", "ui"),
            Path.Combine(repoRoot, "games", "client", "override"),
            Path.Combine(repoRoot, "output", "staging")
        ];
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        StopWatching();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
