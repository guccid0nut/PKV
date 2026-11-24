// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of an in-game hot reload console command execution.
/// </summary>
public sealed record HotReloadCommandResult(string Command, bool Success, string? Response, string? Error);

/// <summary>
/// Result of a cache purge operation.
/// </summary>
public sealed class CachePurgeResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> PurgedTargets { get; init; } = [];
    public int FilesDeleted { get; init; }
    public int DirectoriesCleaned { get; init; }
    public long BytesReclaimed { get; init; }
    public double MbReclaimed => Math.Round((double)BytesReclaimed / (1024.0 * 1024.0), 2);
    public int BomsCleaned { get; init; }
    public bool HotReloadDispatched { get; init; }
    public string? HotReloadStatus { get; init; }
    public IReadOnlyList<HotReloadCommandResult> HotReloadResponses { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string Timestamp { get; init; } = TimeProvider.System.GetUtcNow().ToString("o");
}

/// <summary>
/// Interface for engine cache purge operations.
/// </summary>
public interface IEngineCacheService
{
    Task<CachePurgeResult> PurgeCacheAsync(
        IEnumerable<string> targets,
        bool hotReload = true,
        bool cleanBoms = true,
        string? repoRoot = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service managing targeted atomic cache purging, XML UTF-8 BOM sanitation,
/// and in-game hot-reload console command dispatching via IPC bridge.
/// </summary>
public sealed class EngineCacheService : IEngineCacheService
{
    private readonly IGameIpcBridge _ipcBridge;
    private readonly Action<string>? _logAction;

    public EngineCacheService(IGameIpcBridge? ipcBridge = null, Action<string>? logAction = null)
    {
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke($"[EngineCacheService] {message}");
    }

    public async Task<CachePurgeResult> PurgeCacheAsync(
        IEnumerable<string> targets,
        bool hotReload = true,
        bool cleanBoms = true,
        string? repoRoot = null,
        CancellationToken cancellationToken = default)
    {
        string root = repoRoot ?? FindProjectRoot();
        Log($"Initiating cache purge with root: '{root}'");

        HashSet<string> targetSet = new(targets.Select(t => t.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0 || targetSet.Contains("all"))
        {
            targetSet = ["shaders", "textures", "luadata", "dxvk"];
        }

        List<string> errors = [];
        List<string> purgedTargets = [];
        int totalFilesDeleted = 0;
        int totalDirsCleaned = 0;
        long totalBytesReclaimed = 0;
        int bomsCleaned = 0;

        // 1. Purge Shader Caches
        if (targetSet.Contains("shaders") || targetSet.Contains("shadercache") || targetSet.Contains("shader"))
        {
            Log("Purging shader caches...");
            string[] shaderPaths =
            [
                Path.Combine(root, "games", "client", "game", "shadercache"),
                Path.Combine(root, "games", "client", "userdataKONGOR", "extensions", "shadercache"),
                Path.Combine(root, "games", "shared", "shadercache"),
                Path.Combine(root, "games", "client", "shadercache"),
                Path.Combine(root, "games", "server", "shadercache")
            ];

            foreach (string p in shaderPaths)
            {
                PurgeDirectory(p, ref totalFilesDeleted, ref totalDirsCleaned, ref totalBytesReclaimed, errors);
            }
            purgedTargets.Add("shadercache");
        }

        // 2. Purge Texture Caches
        if (targetSet.Contains("textures") || targetSet.Contains("texturecache") || targetSet.Contains("texture"))
        {
            Log("Purging texture caches...");
            string[] texturePaths =
            [
                Path.Combine(root, "games", "client", "userdataKONGOR", "extensions", "texturecache"),
                Path.Combine(root, "games", "client", "texturecache")
            ];

            foreach (string p in texturePaths)
            {
                PurgeDirectory(p, ref totalFilesDeleted, ref totalDirsCleaned, ref totalBytesReclaimed, errors);
            }
            purgedTargets.Add("texturecache");
        }

        // 3. Purge Lua Data
        if (targetSet.Contains("lua") || targetSet.Contains("luadata"))
        {
            Log("Purging Lua data cache...");
            string[] luaPaths =
            [
                Path.Combine(root, "games", "client", "userdataKONGOR", "extensions", "luadata"),
                Path.Combine(root, "games", "client", "luadata")
            ];

            foreach (string p in luaPaths)
            {
                PurgeDirectory(p, ref totalFilesDeleted, ref totalDirsCleaned, ref totalBytesReclaimed, errors);
            }
            purgedTargets.Add("luadata");
        }

        // 4. Purge DXVK Cache
        if (targetSet.Contains("dxvk") || targetSet.Contains("dxvkcache") || targetSet.Contains("vulkan"))
        {
            Log("Purging DXVK Vulkan state caches...");
            string clientDir = Path.Combine(root, "games", "client");
            if (Directory.Exists(clientDir))
            {
                string[] dxvkFiles = Directory.GetFiles(clientDir, "*.dxvk-cache", SearchOption.TopDirectoryOnly);
                foreach (string file in dxvkFiles)
                {
                    try
                    {
                        FileInfo fi = new(file);
                        long len = fi.Length;
                        File.Delete(file);
                        totalFilesDeleted++;
                        totalBytesReclaimed += len;
                        Log($"Deleted DXVK cache file: '{file}' ({len} bytes)");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to delete DXVK cache '{file}': {ex.Message}");
                        Log($"Error deleting '{file}': {ex.Message}");
                    }
                }
            }
            purgedTargets.Add("dxvk");
        }

        // 5. Clean UTF-8 BOMs from XML cache / config files
        if (cleanBoms)
        {
            Log("Scanning for UTF-8 BOM headers in cache and XML files...");
            string[] scanDirs =
            [
                Path.Combine(root, "games", "client", "userdataKONGOR"),
                Path.Combine(root, "games", "client", "game"),
                Path.Combine(root, "games", "shared")
            ];

            foreach (string dir in scanDirs)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    string[] xmlFiles = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories);
                    foreach (string xmlFile in xmlFiles)
                    {
                        if (CleanUtf8BomFromFile(xmlFile))
                        {
                            bomsCleaned++;
                            Log($"Stripped UTF-8 BOM header from XML file: '{xmlFile}'");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error scanning XML files in '{dir}': {ex.Message}");
                }
            }
        }

        // 6. In-Game Console Hot Reload via IPC Bridge
        bool hotReloadDispatched = false;
        string hotReloadStatus = "Disabled";
        List<HotReloadCommandResult> hotReloadResults = [];

        if (hotReload)
        {
            bool isGameRunning = Process.GetProcessesByName("hon_x64").Length > 0;
            if (isGameRunning)
            {
                Log("Detected running hon_x64 process. Dispatching in-game hot-reload console commands via IPC...");
                hotReloadDispatched = true;

                List<string> commandsToDispatch = [];
                if (purgedTargets.Contains("shadercache"))
                {
                    commandsToDispatch.Add("reloadshaders");
                    commandsToDispatch.Add("reloadmaterials");
                }
                if (purgedTargets.Contains("texturecache"))
                {
                    if (!commandsToDispatch.Contains("reloadmaterials"))
                        commandsToDispatch.Add("reloadmaterials");
                    commandsToDispatch.Add("reloadtextures");
                }
                if (purgedTargets.Contains("luadata"))
                {
                    commandsToDispatch.Add("reloadui");
                }

                if (commandsToDispatch.Count == 0)
                {
                    commandsToDispatch.Add("reloadshaders");
                    commandsToDispatch.Add("reloadtextures");
                    commandsToDispatch.Add("reloadui");
                }

                foreach (string cmd in commandsToDispatch)
                {
                    try
                    {
                        string output = await _ipcBridge.ExecuteCommandAsync(cmd, timeoutMs: 2000, cancellationToken).ConfigureAwait(false);
                        hotReloadResults.Add(new HotReloadCommandResult(cmd, true, output, null));
                        Log($"IPC command '{cmd}' executed: {output}");
                    }
                    catch (Exception ex)
                    {
                        hotReloadResults.Add(new HotReloadCommandResult(cmd, false, null, ex.Message));
                        Log($"IPC command '{cmd}' failed: {ex.Message}");
                    }
                }

                int successful = hotReloadResults.Count(r => r.Success);
                hotReloadStatus = $"{successful}/{hotReloadResults.Count} reload commands acknowledged by in-game engine";
            }
            else
            {
                hotReloadStatus = "hon_x64 is not running. In-game reload commands skipped.";
                Log(hotReloadStatus);
            }
        }

        Log($"Purge complete: {totalFilesDeleted} files deleted, {totalDirsCleaned} dirs cleaned, {totalBytesReclaimed} bytes reclaimed, {bomsCleaned} BOMs cleaned.");

        return new CachePurgeResult
        {
            Success = errors.Count == 0,
            PurgedTargets = purgedTargets,
            FilesDeleted = totalFilesDeleted,
            DirectoriesCleaned = totalDirsCleaned,
            BytesReclaimed = totalBytesReclaimed,
            BomsCleaned = bomsCleaned,
            HotReloadDispatched = hotReloadDispatched,
            HotReloadStatus = hotReloadStatus,
            HotReloadResponses = hotReloadResults,
            Errors = errors,
            Timestamp = TimeProvider.System.GetUtcNow().ToString("o")
        };
    }

    private void PurgeDirectory(
        string directoryPath,
        ref int filesDeleted,
        ref int dirsCleaned,
        ref long bytesReclaimed,
        List<string> errors)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            DirectoryInfo dirInfo = new(directoryPath);
            foreach (FileInfo fi in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    long len = fi.Length;
                    fi.Delete();
                    filesDeleted++;
                    bytesReclaimed += len;
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to delete file '{fi.FullName}': {ex.Message}");
                    Log($"Error deleting '{fi.FullName}': {ex.Message}");
                }
            }

            foreach (DirectoryInfo subDir in dirInfo.GetDirectories())
            {
                try
                {
                    subDir.Delete(recursive: true);
                    dirsCleaned++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to remove directory '{subDir.FullName}': {ex.Message}");
                    Log($"Error removing dir '{subDir.FullName}': {ex.Message}");
                }
            }

            dirsCleaned++;
            Log($"Purged cache directory: '{directoryPath}'");
        }
        catch (Exception ex)
        {
            errors.Add($"Error accessing cache directory '{directoryPath}': {ex.Message}");
            Log($"Error accessing '{directoryPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for UTF-8 BOM (0xEF, 0xBB, 0xBF) at the beginning of a file and removes it if present.
    /// K2 engine C++ XML parser rejects files with UTF-8 BOM headers.
    /// </summary>
    private static bool CleanUtf8BomFromFile(string filePath)
    {
        try
        {
            byte[] header = new byte[3];
            using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 3) return false;
                int read = fs.Read(header, 0, 3);
                if (read < 3) return false;
            }

            // UTF-8 BOM is 0xEF, 0xBB, 0xBF
            if (header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            {
                byte[] allBytes = File.ReadAllBytes(filePath);
                byte[] stripped = new byte[allBytes.Length - 3];
                Array.Copy(allBytes, 3, stripped, 0, stripped.Length);
                File.WriteAllBytes(filePath, stripped);
                return true;
            }
        }
        catch
        {
            // Ignore locked or transient files
        }

        return false;
    }

    private static string FindProjectRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")))
            {
                return current;
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "ProjectKongor.slnx")))
            {
                return baseDir;
            }
            string? parent = Path.GetDirectoryName(baseDir);
            if (parent == null || parent == baseDir) break;
            baseDir = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Wave 4 (#228): Simulates a skewed texture/entity cache eviction workload using
    /// <see cref="BijectiveShuffledZipfianGenerator"/> where hot-spot keys are scattered bijectively across `[0, keyspaceSize)`.
    /// </summary>
    public static BijectiveShuffledZipfianGenerator CreateHotspotWorkloadGenerator(
        ulong keyspaceSize,
        double theta = 0.99,
        ulong seed = 0x51F1_71BE_CAFE_BEEFUL)
    {
        return new BijectiveShuffledZipfianGenerator(keyspaceSize, theta, seed);
    }
}

