// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayProcessLauncher.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.IO.Compression;
using Kongor.Core.Vfs;
using Microsoft.Extensions.Logging;
using ReplayRenderer.Service.Models;

namespace ReplayRenderer.Service.ProcessLifecycle;

/// <summary>
///     Manages the process lifecycle, configuration generation, deterministic launch,
///     real-time telemetry extraction, and graceful shutdown of the native HoN engine (hon_x64.exe).
/// </summary>
public sealed class ReplayProcessLauncher(ILogger<ReplayProcessLauncher> logger, WindowsJobObjectSandbox jobSandbox)
    : IAsyncDisposable
{
    private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowsJobObjectSandbox _jobSandbox = jobSandbox;
    private readonly ILogger<ReplayProcessLauncher> _logger = logger;
    private readonly List<string> _generatedConfigFiles = [];
    private readonly List<string> _stagedReplayFiles = [];
    private string? _gameDirectory;
    private Process? _process;

    public Task<int> ExitTask => _exitTcs.Task;
    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ProcessId => _process?.Id;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5));
        _process?.Dispose();
        _process = null;
    }

    public event Action<string>? OnLogLineReceived;
    public event Action? OnReplayWorldLoaded;

    /// <summary>
    ///     Builds the K2 VFS <c>-mod</c> mount list in canonical FN-014 low-to-high precedence order
    ///     (<see cref="ClientModMountSpec.CanonicalMountTiers"/>) so <c>override</c> is evaluated first at <c>back()</c>.
    /// </summary>
    public static List<string> BuildModStack(string gameDirectory)
    {
        List<string> vfsMounts = [];
        foreach (string tier in ClientModMountSpec.CanonicalMountTiers)
        {
            if (string.Equals(tier, "ClientPackageFiles", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(Path.Combine(gameDirectory, "ClientPackageFiles")))
                {
                    vfsMounts.Add("ClientPackageFiles");
                }
            }
            else if (string.Equals(tier, "../shared", StringComparison.OrdinalIgnoreCase))
            {
                string sharedAbs = Path.GetFullPath(Path.Combine(gameDirectory, "../shared"));
                if (Directory.Exists(sharedAbs))
                {
                    vfsMounts.Add("../shared");
                }
                else if (Directory.Exists(Path.Combine(gameDirectory, "shared")))
                {
                    vfsMounts.Add("shared");
                }
            }
            else
            {
                vfsMounts.Add(tier);
            }
        }

        return vfsMounts;
    }

    /// <summary>
    ///     Builds the command-line arguments passed to <c>hon_x64.exe</c> for replay rendering,
    ///     enforcing FN-014 mount precedence and omitting neutralized singleton <c>-pipe willowmaker-ipc</c>.
    /// </summary>
    public static List<string> BuildEngineArguments(RenderJobConfiguration config, string gameDirectory)
    {
        List<string> vfsMounts = BuildModStack(gameDirectory);
        List<string> arguments =
        [
            "-masterserver 127.0.0.1:55555",
            "-webserver 127.0.0.1:55555",
            "-messageserver 127.0.0.1:55555",
            "-mod " + string.Join(";", vfsMounts)
        ];

        if (!string.IsNullOrWhiteSpace(config.IpcPipeName) &&
            !string.Equals(config.IpcPipeName, "willowmaker-ipc", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-pipe " + config.IpcPipeName);
        }

        arguments.Add("-writeconsole");
        arguments.Add("-logfile console.log");
        arguments.Add("-exec ~/replay_autoexec.cfg");
        arguments.Add("+Set svr_port 11299");
        arguments.Add("+Set vid_fullscreen false");

        if (config.Headless)
        {
            arguments.Add("+Set vid_pos \"-32000 -32000\"");
        }

        return arguments;
    }

    public event Action? OnReplayCompleted;
    public bool IsReplayWorldLoaded { get; private set; }

    /// <summary>
    ///     Launches hon_x64.exe with deterministic 60 FPS frame locking and Vulkan layer interception.
    /// </summary>
    public void Launch(RenderJobConfiguration config, string framePipeName)
    {
        _gameDirectory = config.GameDirectory;

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _logger.LogInformation("[ProcessLauncher] Terminating previous replay hon_x64 process (PID: {PID})...",
                    _process.Id);
                _process.Kill(true);
                _process.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProcessLauncher] Could not kill previous replay hon_x64 process.");
            }
        }

        string executablePath = Path.Combine(config.GameDirectory, "hon_x64.exe");
        if (!File.Exists(executablePath))
        {
            _logger.LogError("[ProcessLauncher] HoN executable not found at path: {Path}", executablePath);
            throw new FileNotFoundException($"HoN game client executable not found at '{executablePath}'",
                executablePath);
        }

        _logger.LogInformation("[ProcessLauncher] Preparing deterministic replay session for: {ReplayPath}",
            config.ReplayFilePath);

        // Ensure isolated userdata / userdataKONGOR directory hierarchy is initialized (NEVER touch git-tracked game/, base/, override/, KONGOR/*)
        string userdataRoot = Path.Combine(config.GameDirectory, "userdata");
        string userdataKongorRoot = Path.Combine(config.GameDirectory, "userdataKONGOR");
        string[] requiredUserDirs =
        [
            userdataRoot,
            Path.Combine(userdataRoot, "game"),
            Path.Combine(userdataRoot, "game", "logs"),
            Path.Combine(userdataRoot, "game", "replays"),
            Path.Combine(userdataRoot, "game", "shaders"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(userdataRoot, "editor"),
            userdataKongorRoot,
            Path.Combine(userdataKongorRoot, "configuration"),
            Path.Combine(userdataKongorRoot, "replays")
        ];
        foreach (string d in requiredUserDirs)
            if (!Directory.Exists(d))
                Directory.CreateDirectory(d);

        PurgeStaleReconnectFiles(config.GameDirectory);

        // 1. Stage replay ONLY inside isolated user runtime directories (userdataKONGOR/replays and userdata/.../replays)
        string replayFileName = Path.GetFileName(config.ReplayFilePath);
        string[] isolatedReplayDirs =
        [
            Path.Combine(userdataKongorRoot, "replays"),
            Path.Combine(userdataRoot, "game", "replays"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "replays")
        ];

        lock (_stagedReplayFiles)
        {
            _stagedReplayFiles.Clear();
            foreach (string dir in isolatedReplayDirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, replayFileName);
                    bool alreadyExisted = File.Exists(dest) &&
                                          string.Equals(Path.GetFullPath(config.ReplayFilePath), Path.GetFullPath(dest),
                                              StringComparison.OrdinalIgnoreCase);
                    StageNormalizedReplay(config.ReplayFilePath, dest);
                    if (!alreadyExisted)
                    {
                        _stagedReplayFiles.Add(dest);
                    }

                    _logger.LogInformation("[ProcessLauncher] Staged normalized replay to isolated user directory: {Path}", dest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProcessLauncher] Could not stage replay to isolated directory '{Dir}'.", dir);
                }
            }
        }

        // 2. Generate temporary replay autoexec strictly inside userdataKONGOR / userdata (NEVER git-tracked game/autoexec.cfg)
        WriteAutoexecScript(config, replayFileName);

        // 3. Configure ProcessStartInfo
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = config.GameDirectory,
            UseShellExecute = false,
            CreateNoWindow = config.Headless,
            WindowStyle = config.Headless ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        // 4. Inject Environment Variables for Vulkan Layer Interception & DXVK (only on child ProcessStartInfo)
        if (config.UseSharedMemoryIpc)
        {
            string layerPath = $"{config.VulkanLayerPath};{config.GameDirectory}";
            startInfo.EnvironmentVariables["VK_LAYER_PATH"] = layerPath;
            startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"] = "VK_LAYER_HON_replay_capture";
            startInfo.EnvironmentVariables["VK_LOADER_DEBUG"] = "all";
            startInfo.EnvironmentVariables["HON_CAPTURE_ENABLE"] = "1";
            startInfo.EnvironmentVariables["HON_CAPTURE_SLOTS"] = "3";
            startInfo.EnvironmentVariables["HON_REPLAY_FRAME_PIPE"] = framePipeName;
            startInfo.EnvironmentVariables["HON_REPLAY_WIDTH"] = config.RenderWidth.ToString();
            startInfo.EnvironmentVariables["HON_REPLAY_HEIGHT"] = config.RenderHeight.ToString();
            startInfo.EnvironmentVariables["HON_REPLAY_FPS"] = config.TargetFramerate.ToString();
            if (!string.IsNullOrWhiteSpace(config.DxvkFilterDeviceName))
            {
                startInfo.EnvironmentVariables["DXVK_FILTER_DEVICE_NAME"] = config.DxvkFilterDeviceName;
            }
            startInfo.EnvironmentVariables["DXVK_LOG_LEVEL"] = "info";
            startInfo.EnvironmentVariables["DXVK_HUD"] = "none";

            string userDxvkConfigPath = Path.Combine(userdataKongorRoot, "dxvk.conf");
            string fallbackDxvkConfigPath = Path.Combine(config.GameDirectory, "dxvk.conf");
            if (File.Exists(userDxvkConfigPath))
            {
                startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"] = userDxvkConfigPath;
            }
            else if (File.Exists(fallbackDxvkConfigPath))
            {
                startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"] = fallbackDxvkConfigPath;
            }
        }

        // 5. Build Command Line Arguments (FN-014 canonical low-to-high -mod stack with override last)
        List<string> arguments = BuildEngineArguments(config, config.GameDirectory);

        startInfo.Arguments = string.Join(" ", arguments);

        _logger.LogInformation("[ProcessLauncher] Launching HoN engine (Interactive Shell): {Exe} {Args}",
            executablePath, startInfo.Arguments);

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += (sender, e) =>
        {
            int exitCode = -1;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProcessLauncher] Could not read exit code from process.");
            }

            CleanupAutoexecFiles();
            _logger.LogInformation("[ProcessLauncher] HoN engine exited with code {ExitCode}.", exitCode);
            _exitTcs.TrySetResult(exitCode);
        };

        if (!process.Start())
        {
            CleanupAutoexecFiles();
            _logger.LogError("[ProcessLauncher] Failed to start HoN engine process.");
            throw new InvalidOperationException($"Failed to spawn HoN engine process: {executablePath}");
        }

        // Assign to Win32 Job Object sandbox
        try
        {
            _jobSandbox.AssignProcess(process);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[ProcessLauncher] Job Sandbox assignment completed or bypassed ({Msg}).",
                ex.Message);
        }

        // Real-time console.log multi-path scanner & streamer task
        _ = Task.Run(async () =>
        {
            string[] localLogPaths =
            [
                Path.Combine(config.GameDirectory, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "console.log"),
                Path.Combine(config.GameDirectory, "userdata", "Heroes of Newerth x64", "game", "console.log"),
                Path.Combine(config.GameDirectory, "userdata", "Heroes of Newerth x64", "game", "logs", "console.log"),
                Path.Combine(config.GameDirectory, "userdata", "Heroes of Newerth x64", "console.log"),
                Path.Combine(config.GameDirectory, "userdataKONGOR", "console.log"),
                Path.Combine(config.GameDirectory, "console.log"),
                Path.Combine(config.GameDirectory, "engine.log"),
                Path.Combine(config.GameDirectory, "userdata", "game", "console.log"),
                Path.Combine(config.GameDirectory, "userdata", "game", "logs", "console.log"),
                Path.Combine(config.GameDirectory, "userdata", "logs", "console.log"),
                Path.Combine(config.GameDirectory, "userdata", "console.log")
            ];

            DateTimeOffset spawnTime = TimeProvider.System.GetUtcNow();
            string? activeLogPath = null;
            int attempts = 0;
            while (activeLogPath == null && attempts < 100 && !_exitTcs.Task.IsCompleted)
            {
                activeLogPath = localLogPaths
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault(p => File.GetLastWriteTimeUtc(p) >= spawnTime.UtcDateTime.AddSeconds(-15));

                if (activeLogPath == null)
                {
                    await Task.Delay(100);
                    attempts++;
                }
            }

            if (activeLogPath == null)
            {
                activeLogPath = localLogPaths
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }

            if (activeLogPath == null)
            {
                _logger.LogWarning("[ProcessLauncher] No candidate console.log found after scanning candidate paths.");
                return;
            }

            _logger.LogInformation("[ProcessLauncher] Found active console log stream at: {Path}", activeLogPath);
            try
            {
                using FileStream fs = new(activeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(0, SeekOrigin.End);
                using StreamReader reader = new(fs, System.Text.Encoding.Unicode);

                while (!_exitTcs.Task.IsCompleted)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line != null)
                    {
                        _logger.LogInformation("[HoN Console Log] {Line}", line);
                        OnLogLineReceived?.Invoke(line);
                        CheckReplayTokens(line);
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProcessLauncher] Error reading console log file '{Path}'.", activeLogPath);
            }
        });

        _process = process;
        _logger.LogInformation("[ProcessLauncher] HoN engine successfully spawned (PID: {PID}).", process.Id);
    }

    private void CheckReplayTokens(string line)
    {
        if (!IsReplayWorldLoaded &&
            (line.Contains("New: 8-game_spectator", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("New: 2-game", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("NETCMD_FINISHED_LOADING_WORLD", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("NETCMD_CLIENT_IN_GAME", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Client loaded world", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Entering game", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Loaded world", StringComparison.OrdinalIgnoreCase)))
        {
            IsReplayWorldLoaded = true;
            _logger.LogInformation("[ProcessLauncher] Match world loaded token detected in engine output: '{Line}'", line);
            OnReplayWorldLoaded?.Invoke();
        }

        if (line.Contains("Replay playback finished", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("End of replay reached", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Disconnect: Replay ended", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Host: End of replay", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[ProcessLauncher] Replay completion token detected in engine output: '{Line}'",
                line);
            OnReplayCompleted?.Invoke();
        }
    }

    /// <summary>
    ///     Generates deterministic autoexec.cfg configuration script text.
    /// </summary>
    public static string GenerateAutoExecCfg(RenderJobConfiguration config, string replayFileName)
    {
        return AutoExecGenerator.Generate(config, replayFileName);
    }

    private void WriteAutoexecScript(RenderJobConfiguration config, string replayFileName)
    {
        PurgeAllReplayAutoexecFiles(config.GameDirectory, _logger);

        string scriptText = AutoExecGenerator.Generate(config, replayFileName);

        // Write strictly into gitignored user runtime paths (userdataKONGOR & userdata) — NEVER game/, base/, override/, or KONGOR/configuration!
        (string Dir, string FileName)[] isolatedTargets =
        [
            (Path.Combine(config.GameDirectory, "userdataKONGOR", "configuration"), "autoexec.cfg"),
            (Path.Combine(config.GameDirectory, "userdata", "game"), "replay_autoexec.cfg"),
            (Path.Combine(config.GameDirectory, "userdata", "Heroes of Newerth x64", "game"), "replay_autoexec.cfg")
        ];

        lock (_generatedConfigFiles)
        {
            _generatedConfigFiles.Clear();
            foreach ((string dir, string fileName) in isolatedTargets)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, fileName);
                    File.WriteAllText(path, scriptText, System.Text.Encoding.UTF8);
                    _generatedConfigFiles.Add(path);
                    _logger.LogInformation("[ProcessLauncher] Generated isolated replay config at: {Path}", path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProcessLauncher] Could not write isolated replay config to {Dir}", dir);
                }
            }
        }

        SanitizeUserConfigsOnly(config.GameDirectory);
    }

    /// <summary>
    ///     Cleans up all temporary replay config and staged .honreplay files generated exclusively for this replay render session,
    ///     guaranteeing normal game client launches (e.g. from WILLOWMAKER) are not redirected into a replay.
    /// </summary>
    public void CleanupAutoexecFiles()
    {
        lock (_generatedConfigFiles)
        {
            foreach (string path in _generatedConfigFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        _logger.LogInformation("[ProcessLauncher] Cleaned up temporary replay config at: {Path}", path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProcessLauncher] Could not delete temporary replay config at {Path}", path);
                }
            }
            _generatedConfigFiles.Clear();
        }

        lock (_stagedReplayFiles)
        {
            foreach (string stagedReplay in _stagedReplayFiles)
            {
                try
                {
                    if (File.Exists(stagedReplay))
                    {
                        File.Delete(stagedReplay);
                        _logger.LogInformation("[ProcessLauncher] Cleaned up staged replay file at: {Path}", stagedReplay);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProcessLauncher] Could not delete staged replay file at {Path}", stagedReplay);
                }
            }
            _stagedReplayFiles.Clear();
        }

        if (!string.IsNullOrWhiteSpace(_gameDirectory))
        {
            PurgeAllReplayAutoexecFiles(_gameDirectory, _logger);
        }
    }

    /// <summary>
    ///     Purges stale replay autoexec files strictly within isolated user runtime directories (NEVER git-tracked folders).
    /// </summary>
    public static void PurgeAllReplayAutoexecFiles(string gameDir, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;

        (string Dir, string FileName)[] candidatePaths =
        [
            (Path.Combine(gameDir, "userdataKONGOR", "configuration"), "autoexec.cfg"),
            (Path.Combine(gameDir, "userdata", "game"), "replay_autoexec.cfg"),
            (Path.Combine(gameDir, "userdata", "game"), "autoexec.cfg"),
            (Path.Combine(gameDir, "userdata", "Heroes of Newerth x64", "game"), "replay_autoexec.cfg"),
            (Path.Combine(gameDir, "userdata", "Heroes of Newerth x64", "game"), "autoexec.cfg"),
            (Path.Combine(gameDir, "userdata", "Heroes of Newerth x64", "KONGOR", "configuration"), "autoexec.cfg"),
            (Path.Combine(gameDir, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions"), "autoexec.cfg")
        ];

        foreach ((string dir, string fileName) in candidatePaths)
        {
            try
            {
                string path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    if (text.Contains("StartReplay") || text.Contains("NEXUS Replay Renderer") || text.Contains("host_autoexec"))
                    {
                        File.Delete(path);
                        logger?.LogInformation("[ProcessLauncher] Purged stale replay config file at: {Path}", path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[ProcessLauncher] Failed to inspect/purge replay config in {Dir}", dir);
            }
        }
    }

    private void SanitizeUserConfigsOnly(string gameDir)
    {
        string[] isolatedUserRoots =
        [
            Path.Combine(gameDir, "userdata"),
            Path.Combine(gameDir, "userdataKONGOR")
        ];

        foreach (string userRoot in isolatedUserRoots)
        {
            if (!Directory.Exists(userRoot)) continue;

            try
            {
                string[] cfgFiles = Directory.GetFiles(userRoot, "*.cfg", SearchOption.AllDirectories);
                foreach (string file in cfgFiles)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        if (bytes.Length == 0) continue;

                        System.Text.Encoding enc = (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                            ? System.Text.Encoding.Unicode
                            : System.Text.Encoding.UTF8;

                        string text = enc.GetString(bytes);
                        bool modified = false;

                        if (text.Contains("\"vid_fullscreen\" \"true\""))
                        {
                            text = text.Replace("\"vid_fullscreen\" \"true\"", "\"vid_fullscreen\" \"false\"");
                            modified = true;
                        }
                        if (text.Contains("vid_fullscreen true"))
                        {
                            text = text.Replace("vid_fullscreen true", "vid_fullscreen false");
                            modified = true;
                        }

                        if (modified)
                        {
                            File.WriteAllBytes(file, enc.GetBytes(text));
                            _logger.LogInformation("[ProcessLauncher] Sanitized vid_fullscreen to false in user config: {File}", file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ProcessLauncher] Error sanitizing user config file {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProcessLauncher] Error scanning user config files in {UserRoot}", userRoot);
            }
        }
    }

    private void PurgeStaleReconnectFiles(string gameDir)
    {
        try
        {
            string[] isolatedUserDirs =
            [
                Path.Combine(gameDir, "userdata"),
                Path.Combine(gameDir, "userdataKONGOR")
            ];

            foreach (string root in isolatedUserDirs)
            {
                if (!Directory.Exists(root)) continue;
                string[] reconnectFiles = Directory.GetFiles(root, "*reconnect*.cfg", SearchOption.AllDirectories);
                foreach (string file in reconnectFiles)
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogInformation("[ProcessLauncher] Purged stale reconnect configuration: {Path}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ProcessLauncher] Could not purge reconnect file {Path}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProcessLauncher] Error purging stale reconnect files.");
        }
    }

    private void StageNormalizedReplay(string sourcePath, string destPath)
    {
        try
        {
            string tempNormalized = Path.Combine(Path.GetTempPath(), $"norm_{Guid.NewGuid():N}.honreplay");
            bool needsNormalization = false;

            using (ZipArchive srcZip = ZipFile.OpenRead(sourcePath))
            {
                foreach (ZipArchiveEntry entry in srcZip.Entries)
                {
                    if (string.Equals(entry.FullName, "replayinfo", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName != "ReplayInfo")
                    {
                        needsNormalization = true;
                        break;
                    }

                    if (string.Equals(entry.FullName, "replaydata", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName != "ReplayData")
                    {
                        needsNormalization = true;
                        break;
                    }
                }

                if (needsNormalization)
                {
                    using ZipArchive dstZip = ZipFile.Open(tempNormalized, ZipArchiveMode.Create);
                    foreach (ZipArchiveEntry entry in srcZip.Entries)
                    {
                        string targetName = entry.FullName;
                        if (string.Equals(entry.FullName, "replayinfo", StringComparison.OrdinalIgnoreCase))
                            targetName = "ReplayInfo";
                        else if (string.Equals(entry.FullName, "replaydata", StringComparison.OrdinalIgnoreCase))
                            targetName = "ReplayData";

                        ZipArchiveEntry dstEntry = dstZip.CreateEntry(targetName, CompressionLevel.Optimal);
                        using Stream srcStream = entry.Open();
                        using Stream dstStream = dstEntry.Open();
                        srcStream.CopyTo(dstStream);
                    }
                }
            }

            string targetDir = Path.GetDirectoryName(destPath) ?? string.Empty;
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            if (needsNormalization)
            {
                File.Copy(tempNormalized, destPath, true);
                try
                {
                    File.Delete(tempNormalized);
                }
                catch
                {
                }
            }
            else if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath),
                         StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destPath, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProcessLauncher] Replay normalization notice: {Message}", ex.Message);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath),
                    StringComparison.OrdinalIgnoreCase))
                try
                {
                    File.Copy(sourcePath, destPath, true);
                }
                catch
                {
                }
        }
    }

    /// <summary>
    ///     Gracefully stops the HoN engine, or forcefully terminates the process tree if the timeout expires.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        try
        {
            if (_process == null || _process.HasExited) return;

            _logger.LogInformation("[ProcessLauncher] Requesting graceful termination of HoN engine (PID: {PID})...",
                _process.Id);

            try
            {
                _process.CloseMainWindow();
                using CancellationTokenSource cts = new(timeout);
                await _process.WaitForExitAsync(cts.Token);
                _logger.LogInformation("[ProcessLauncher] HoN engine exited gracefully.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "[ProcessLauncher] HoN engine shutdown timed out after {Timeout}s. Killing process tree...",
                    timeout.TotalSeconds);
                try
                {
                    _process.Kill(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ProcessLauncher] Error killing HoN process tree: {Message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProcessLauncher] Exception during HoN stop sequence: {Message}", ex.Message);
            }
        }
        finally
        {
            CleanupAutoexecFiles();
        }
    }
}
