// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Manifest payload for in-game hot-reloading communication.
/// </summary>
[MemoryPackable]
public sealed partial class HotReloadManifest
{
    public string FilePath { get; set; } = string.Empty;
    public string VfsPath { get; set; } = string.Empty;
    public string Action { get; set; } = "modified";
    public long Timestamp { get; set; } = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
    public bool IsValid { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

/// <summary>
///     Background service that monitors the 'override/' directory for real-time UI/Lua/VFS modifications,
///     performs pre-execution syntax validation, and signals the running K2 engine client.
/// </summary>
public sealed class HotReloadWatcherService(
    ILogger<HotReloadWatcherService> logger,
    IWillowmakerIpcService? ipcService = null) : IDisposable, IAsyncDisposable
{
    private readonly IWillowmakerIpcService? _ipcService = ipcService;
    private readonly Lock _lock = new();
    private readonly ILogger<HotReloadWatcherService> _logger = logger;
    private Timer? _debounceTimer;
    private string? _overrideRoot;
    private readonly HashSet<string> _pendingFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        Dispose();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _overrideRoot = ResolveOverrideRoot();

        if (string.IsNullOrEmpty(_overrideRoot) || !Directory.Exists(_overrideRoot))
        {
            _logger.LogWarning("[HotReload] Override directory not found. Hot-reloader watcher idle.");
            return Task.CompletedTask;
        }

        try
        {
            _watcher = new FileSystemWatcher(_overrideRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                Filter = "*.*"
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += (sender, e) => OnFileEvent(sender,
                new FileSystemEventArgs(WatcherChangeTypes.Renamed, Path.GetDirectoryName(e.FullPath) ?? _overrideRoot,
                    Path.GetFileName(e.FullPath)));

            _watcher.EnableRaisingEvents = true;

            _logger.LogInformation("[HotReload] VFS Live Hot-Reload Watcher ACTIVE on: {Root}", _overrideRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HotReload] Failed to initialize FileSystemWatcher on {Root}", _overrideRoot);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingFilePaths.Clear();
        }

        _logger.LogInformation("[HotReload] VFS Hot-Reload Watcher stopped.");
        return Task.CompletedTask;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        string fullPath = e.FullPath;
        string fileName = Path.GetFileName(fullPath);

        // Ignore internal metadata files, logs, and git artifacts
        if (fileName.StartsWith(".hotreload", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(".git", StringComparison.OrdinalIgnoreCase))
            return;

        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext is not ".lua" and not ".interface" and not ".package" and not ".xml" and
            not ".str" and not ".entity" and not ".mdf" and not ".effect" and
            not ".material" and not ".tga" and not ".dds")
            return;

        lock (_lock)
        {
            _pendingFilePaths.Add(fullPath);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, 100, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        List<string> targetPaths;
        lock (_lock)
        {
            targetPaths = [.. _pendingFilePaths];
            _pendingFilePaths.Clear();
        }

        if (targetPaths.Count == 0 || string.IsNullOrEmpty(_overrideRoot))
            return;

        foreach (string targetPath in targetPaths)
        {
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
                continue;

            try
            {
                string relativePath = Path.GetRelativePath(_overrideRoot, targetPath).Replace('\\', '/');
                string vfsPath = "/" + relativePath.TrimStart('/');

                _logger.LogInformation("[HotReload] Detected change on: {VfsPath} ({PhysicalPath})", vfsPath, targetPath);

                bool isValid = true;
                string? errorMessage = null;

                if (targetPath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    (isValid, errorMessage) = ValidateLuaSyntax(targetPath);
                    if (!isValid)
                        _logger.LogWarning("[HotReload] Lua Pre-validation Error in {VfsPath}: {Error}", vfsPath,
                            errorMessage);
                }

                HotReloadManifest manifest = new()
                {
                    FilePath = targetPath,
                    VfsPath = vfsPath,
                    Action = "modified",
                    Timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                    IsValid = isValid,
                    ErrorMessage = errorMessage
                };

                WriteManifest(manifest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HotReload] Failed to process hot-reload for {Path}", targetPath);
            }
        }
    }

    /// <summary>
    ///     Fast static syntax validation for Lua files to catch unclosed blocks, quotes, or mismatched braces.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateLuaSyntax(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            return ValidateLuaSyntaxString(content);
        }
        catch (Exception ex)
        {
            return (false, $"Unable to read file: {ex.Message}");
        }
    }

    /// <summary>
    ///     Validates Lua script source for basic lexical balance (block keywords, parenthesis, quotes, comments).
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateLuaSyntaxString(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return (true, null);

        int blockDepth = 0;
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        int line = 1;

        int i = 0;
        int len = source.Length;

        while (i < len)
        {
            char c = source[i];

            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            // Single-line comment or multi-line comment: starts with --
            if (c == '-' && i + 1 < len && source[i + 1] == '-')
            {
                i += 2;
                // Check for multi-line comment: --[=*[
                if (i < len && source[i] == '[')
                {
                    int equalsCount = 0;
                    int j = i + 1;
                    while (j < len && source[j] == '=')
                    {
                        equalsCount++;
                        j++;
                    }

                    if (j < len && source[j] == '[')
                    {
                        int startLine = line;
                        i = j + 1;
                        string closingSeq = "]" + new string('=', equalsCount) + "]";
                        int closeIdx = source.IndexOf(closingSeq, i, StringComparison.Ordinal);
                        if (closeIdx == -1)
                        {
                            return (false, $"Unclosed multi-line comment starting at line {startLine}.");
                        }
                        for (int k = i; k < closeIdx; k++)
                        {
                            if (source[k] == '\n') line++;
                        }
                        i = closeIdx + closingSeq.Length;
                        continue;
                    }
                }

                // Normal single-line comment: consume until newline
                while (i < len && source[i] != '\n' && source[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            // Double quoted string "..."
            if (c == '"')
            {
                int startLine = line;
                i++;
                bool closed = false;
                while (i < len)
                {
                    char sc = source[i];
                    if (sc == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (sc == '"')
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    if (sc == '\n')
                    {
                        return (false, $"Line {startLine}: Unclosed string literal.");
                    }
                    i++;
                }
                if (!closed)
                {
                    return (false, $"Line {startLine}: Unclosed string literal.");
                }
                continue;
            }

            // Single quoted string '...'
            if (c == '\'')
            {
                int startLine = line;
                i++;
                bool closed = false;
                while (i < len)
                {
                    char sc = source[i];
                    if (sc == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (sc == '\'')
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    if (sc == '\n')
                    {
                        return (false, $"Line {startLine}: Unclosed string literal.");
                    }
                    i++;
                }
                if (!closed)
                {
                    return (false, $"Line {startLine}: Unclosed string literal.");
                }
                continue;
            }

            // Multi-line raw string: [=*[ ... ]=*]
            if (c == '[')
            {
                int equalsCount = 0;
                int j = i + 1;
                while (j < len && source[j] == '=')
                {
                    equalsCount++;
                    j++;
                }

                if (j < len && source[j] == '[')
                {
                    int startLine = line;
                    i = j + 1;
                    string closingSeq = "]" + new string('=', equalsCount) + "]";
                    int closeIdx = source.IndexOf(closingSeq, i, StringComparison.Ordinal);
                    if (closeIdx == -1)
                    {
                        return (false, $"Unclosed multi-line string starting at line {startLine}.");
                    }
                    for (int k = i; k < closeIdx; k++)
                    {
                        if (source[k] == '\n') line++;
                    }
                    i = closeIdx + closingSeq.Length;
                    continue;
                }

                // Regular bracket
                bracketDepth++;
                i++;
                continue;
            }

            if (c == ']')
            {
                bracketDepth--;
                if (bracketDepth < 0) return (false, $"Line {line}: Unmatched closing bracket ']'.");
                i++;
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                parenDepth--;
                if (parenDepth < 0) return (false, $"Line {line}: Unmatched closing parenthesis ')'.");
                i++;
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                braceDepth--;
                if (braceDepth < 0) return (false, $"Line {line}: Unmatched closing brace '}}'.");
                i++;
                continue;
            }

            // Check for identifiers/keywords
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int wordStart = i;
                while (i < len && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }
                string word = source.Substring(wordStart, i - wordStart);

                if (word is "function" or "if" or "do" or "repeat")
                {
                    blockDepth++;
                }
                else if (word is "until" or "end")
                {
                    blockDepth--;
                    if (blockDepth < 0)
                        return (false, $"Line {line}: Unexpected '{word}' without matching opening block keyword.");
                }
                continue;
            }

            i++;
        }

        if (blockDepth != 0)
            return (false,
                $"Unbalanced blocks: missing {(blockDepth > 0 ? $"{blockDepth} 'end' keyword(s)" : "opening statement")}.");
        if (parenDepth != 0) return (false, "Unbalanced parenthesis '()'.");
        if (bracketDepth != 0) return (false, "Unbalanced brackets '[]'.");
        if (braceDepth != 0) return (false, "Unbalanced braces '{}'.");

        return (true, null);
    }

    private void WriteManifest(HotReloadManifest manifest)
    {
        if (string.IsNullOrEmpty(_overrideRoot))
            return;

        try
        {
            string manifestPath = Path.Combine(_overrideRoot, ".hotreload_manifest.json");
            string triggerPath = Path.Combine(_overrideRoot, ".hotreload.trigger");

            string json = JsonSerializer.Serialize(manifest, WillowmakerJsonSerializerContext.Default.HotReloadManifest);
            File.WriteAllText(manifestPath, json);

            // Also write single-line trigger for minimal-overhead parsing in Lua
            string triggerContent =
                $"{manifest.VfsPath}|{manifest.Timestamp}|{(manifest.IsValid ? "1" : "0")}|{manifest.Action}|{manifest.ErrorMessage ?? string.Empty}";
            File.WriteAllText(triggerPath, triggerContent);

            _logger.LogInformation("[HotReload] Manifest dispatched for {VfsPath} (Valid={IsValid})", manifest.VfsPath,
                manifest.IsValid);

            if (_ipcService != null && _ipcService.IsRunning)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _ipcService.BroadcastHotReloadAsync(manifest).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[HotReload] Error broadcasting hot-reload manifest over IPC.");
                    }
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HotReload] Failed to write trigger manifest in {Root}", _overrideRoot);
        }
    }

    private static string? ResolveOverrideRoot()
    {
        string currentDir = Directory.GetCurrentDirectory();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string[] candidateRoots =
        [
            Path.Combine(currentDir, "games", "shared", "override"),
            Path.Combine(currentDir, "games", "client", "override"),
            Path.Combine(currentDir, "override"),
            Path.Combine(baseDir, "games", "shared", "override"),
            Path.Combine(baseDir, "override")
        ];

        foreach (string candidate in candidateRoots)
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

        return null;
    }
}
