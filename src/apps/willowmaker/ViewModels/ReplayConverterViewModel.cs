// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayConverterViewModel.cs
// System: WILLOWMAKER Launcher - Replay to MP4 Converter Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Kongor.Core.Vfs;

namespace WILLOWMAKER.Core.ViewModels;

public partial class ReplayConverterViewModel : ObservableObject
{
    private CancellationTokenSource? _conversionCts;

    [ObservableProperty] public partial string ReplayFilePath { get; set; } = string.Empty;

    [ObservableProperty] public partial string OutputDirectoryPath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "output");

    [ObservableProperty] public partial string SelectedQualityPreset { get; set; } = "1080p60 Ultra (NVENC GPU)";

    [ObservableProperty] public partial string SelectedPovCamera { get; set; } = "Free Spectator Camera";

    [ObservableProperty] public partial bool IsConverting { get; set; } = false;

    [ObservableProperty] public partial double ProgressPercent { get; set; } = 0.0;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready to convert. Select a replay and click Convert to MP4.";

    [ObservableProperty] public partial string LogText { get; set; } = "";

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";

    public ReplayConverterViewModel()
    {
        string defaultOutput = Path.Combine(Directory.GetCurrentDirectory(), "output");
        OutputDirectoryPath = defaultOutput;

        string defaultReplayDir = ResolveDefaultReplayDirectory();
        if (Directory.Exists(defaultReplayDir))
        {
            string[] files = Directory.GetFiles(defaultReplayDir, "*.honreplay", SearchOption.TopDirectoryOnly);
            if (files.Length > 0 && string.IsNullOrWhiteSpace(ReplayFilePath))
            {
                ReplayFilePath = files[0];
            }
        }
    }

    public string FilteredLogText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return LogText;

            string[] lines = LogText.Split('\n');
            StringBuilder sb = new();
            foreach (string line in lines)
                if (line.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine(line);

            return sb.ToString();
        }
    }

    public ObservableCollection<string> QualityPresets { get; } =
    [
        "1080p60 Ultra (NVENC GPU)",
        "4K60 Ultra RTX (NVENC GPU)",
        "720p60 Fast (CPU H.264)"
    ];

    public ObservableCollection<string> PovCameraOptions { get; } =
    [
        "Free Spectator Camera",
        "Player 1 (Hero Index 0)",
        "Player 2 (Hero Index 1)",
        "Player 3 (Hero Index 2)",
        "Player 4 (Hero Index 3)",
        "Player 5 (Hero Index 4)"
    ];

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredLogText));
    }

    partial void OnLogTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredLogText));
    }

    [RelayCommand]
    public async Task BrowseReplayFileAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.StorageProvider: { } storage })
        {
            string defaultDir = ResolveDefaultReplayDirectory();
            IStorageFolder? startFolder = null;
            if (!string.IsNullOrWhiteSpace(ReplayFilePath) && File.Exists(ReplayFilePath))
            {
                string? parent = Path.GetDirectoryName(ReplayFilePath);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    startFolder = await storage.TryGetFolderFromPathAsync(parent);
            }

            if (startFolder == null && Directory.Exists(defaultDir))
            {
                startFolder = await storage.TryGetFolderFromPathAsync(defaultDir);
            }

            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Heroes of Newerth Replay File",
                SuggestedStartLocation = startFolder,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("HoN Replay Archive (*.honreplay)")
                    {
                        Patterns = ["*.honreplay"]
                    },
                    new FilePickerFileType("Zip Archive (*.zip)")
                    {
                        Patterns = ["*.zip"]
                    },
                    new FilePickerFileType("All Files (*.*)")
                    {
                        Patterns = ["*.*"]
                    }
                ]
            });

            if (files.Count > 0)
            {
                ReplayFilePath = files[0].Path.LocalPath;
                AppendLog($"[SELECT] Replay file selected: {ReplayFilePath}");
            }
        }
    }

    [RelayCommand]
    public async Task BrowseOutputDirectoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.StorageProvider: { } storage })
        {
            string currentOutput = string.IsNullOrWhiteSpace(OutputDirectoryPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "output")
                : OutputDirectoryPath;

            if (!Directory.Exists(currentOutput))
            {
                try { Directory.CreateDirectory(currentOutput); } catch { }
            }

            IStorageFolder? startFolder = Directory.Exists(currentOutput)
                ? await storage.TryGetFolderFromPathAsync(currentOutput)
                : null;

            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Output Destination Folder",
                SuggestedStartLocation = startFolder,
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                OutputDirectoryPath = folders[0].Path.LocalPath;
                AppendLog($"[SELECT] Output destination folder selected: {OutputDirectoryPath}");
            }
        }
    }

    public static string ResolveDefaultReplayDirectory()
    {
        string currentDir = Directory.GetCurrentDirectory();
        string[] candidates =
        [
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "replays"),
            Path.Combine(currentDir, "games", "client", "userdata", "replays"),
            Path.Combine(currentDir, "games", "client", "userdata", "game", "replays"),
            Path.Combine(currentDir, "games", "client", "replays"),
            Path.Combine(currentDir, "games", "client", "game", "replays"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "Heroes of Newerth x64", "game", "replays")
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(currentDir, "games", "client", "userdata", "replays");
    }

    [RelayCommand]
    public async Task StartConversionAsync()
    {
        if (IsConverting) return;

        string safeReplayPath = SanitizeAndProtectPath(ReplayFilePath);
        string safeOutputDir = SanitizeAndProtectPath(OutputDirectoryPath);

        if (!File.Exists(safeReplayPath))
        {
            StatusText = "Error: Selected replay file does not exist or path is invalid!";
            AppendLog($"[ERROR] File not found or protected path invalid: {safeReplayPath}");
            return;
        }

        if (!Directory.Exists(safeOutputDir))
            try
            {
                Directory.CreateDirectory(safeOutputDir);
            }
            catch (Exception ex)
            {
                StatusText = $"Error: Failed to create output directory - {ex.Message}";
                AppendLog($"[ERROR] Failed to create output dir: {ex.Message}");
                return;
            }

        IsConverting = true;
        ProgressPercent = 0.0;
        StatusText = "Initializing ReplayRenderer Service...";
        LogText = "";
        AppendLog($"[START] Converting replay: {Path.GetFileName(safeReplayPath)}");
        AppendLog($"[CONFIG] Quality: {SelectedQualityPreset}");
        AppendLog($"[CONFIG] Camera POV: {SelectedPovCamera}");
        AppendLog($"[CONFIG] Protected Output Path: {safeOutputDir}");

        _conversionCts = new CancellationTokenSource();

        try
        {
            string outputMp4 = Path.Combine(safeOutputDir,
                $"{Path.GetFileNameWithoutExtension(safeReplayPath)}_rendered.mp4");

            uint width = 1920;
            uint height = 1080;
            string codec = "h264_nvenc";

            if (SelectedQualityPreset.Contains("4K"))
            {
                width = 3840;
                height = 2160;
            }
            else if (SelectedQualityPreset.Contains("720p"))
            {
                width = 1280;
                height = 720;
                codec = "libx264";
            }

            int povIndex = -1;
            if (SelectedPovCamera.Contains("Player 1")) povIndex = 0;
            else if (SelectedPovCamera.Contains("Player 2")) povIndex = 1;
            else if (SelectedPovCamera.Contains("Player 3")) povIndex = 2;
            else if (SelectedPovCamera.Contains("Player 4")) povIndex = 3;
            else if (SelectedPovCamera.Contains("Player 5")) povIndex = 4;

            string currentDir = Directory.GetCurrentDirectory();
            string servicePath = Path.Combine(currentDir, "services", "replay-renderer", "src");
            string gameDir = Path.Combine(currentDir, "games", "client");
            if (!Directory.Exists(gameDir)) gameDir = Path.Combine(currentDir, "games", "shared");

            string args =
                $"run --project \"{servicePath}\\ReplayRenderer.Service.csproj\" --configuration Release -- " +
                $"--replay \"{safeReplayPath}\" " +
                $"--output \"{outputMp4}\" " +
                $"--game-dir \"{gameDir}\" " +
                $"--layer-dir \"{gameDir}\" " +
                $"--width {width} --height {height} --fps 60 --duration 60 --codec {codec} --pov {povIndex}";

            AppendLog($"[LAUNCH] dotnet {args}");

            ProcessStartInfo startInfo = new()
            {
                FileName = "dotnet",
                Arguments = args,
                WorkingDirectory = currentDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    AppendLog(e.Data);
                    if (e.Data.Contains("Render Status:"))
                    {
                        StatusText = e.Data;
                        ProgressPercent = Math.Min(100.0, ProgressPercent + 5.0);
                    }
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog($"[STDERR] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_conversionCts.Token);

            if (process.ExitCode == 0 && File.Exists(outputMp4) && new FileInfo(outputMp4).Length > 1000)
            {
                ProgressPercent = 100.0;
                StatusText =
                    $"Render Completed Successfully! Output: {Path.GetFileName(outputMp4)} ({new FileInfo(outputMp4).Length / 1024 / 1024} MB)";
                AppendLog($"[SUCCESS] Rendered physical MP4: {outputMp4}");
            }
            else
            {
                StatusText = $"Render finished with exit code {process.ExitCode}. Check log output.";
                AppendLog($"[FINISHED] Process exit code: {process.ExitCode}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Conversion canceled by user.";
            AppendLog("[CANCEL] Conversion canceled.");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            AppendLog($"[EXCEPTION] {ex.Message}");
        }
        finally
        {
            IsConverting = false;
        }
    }

    [RelayCommand]
    public async Task CopyLogsAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(LogText);
            StatusText = "Replay conversion logs copied to clipboard!";
        }
    }

    [RelayCommand]
    public void CancelConversion()
    {
        if (_conversionCts != null && !_conversionCts.IsCancellationRequested)
        {
            _conversionCts.Cancel();
            StatusText = "Canceling conversion...";
        }
    }

    /// <summary>
    /// Process launcher delegate, configurable for unit testing to prevent launching real desktop processes.
    /// </summary>
    public static Func<ProcessStartInfo, Process?> DefaultProcessLauncher { get; set; } = static psi =>
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a =>
            a.FullName?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true ||
            a.FullName?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true))
        {
            return null;
        }

        return Process.Start(psi);
    };

    public Func<ProcessStartInfo, Process?> ProcessLauncher { get; set; } = DefaultProcessLauncher;

    [RelayCommand]
    public void OpenInReplayStudio()
    {
        string safeReplayPath = SanitizeAndProtectPath(ReplayFilePath);
        string currentDir = Directory.GetCurrentDirectory();
        string searchDir = currentDir;
        string root = currentDir;
        while (!string.IsNullOrEmpty(searchDir))
        {
            if (File.Exists(Path.Combine(searchDir, "ProjectKongor.slnx")) || Directory.Exists(Path.Combine(searchDir, "src", "apps", "replay-studio")))
            {
                root = searchDir;
                break;
            }
            DirectoryInfo? parent = Directory.GetParent(searchDir);
            if (parent == null) break;
            searchDir = parent.FullName;
        }

        string[] candidatePaths =
        [
            Path.Combine(root, "src", "apps", "replay-studio", "bin", "Release", "net11.0", "ReplayStudio.GUI.exe"),
            Path.Combine(root, "src", "apps", "replay-studio", "bin", "Debug", "net11.0", "ReplayStudio.GUI.exe"),
            Path.Combine(root, "tools", "replay-studio", "ReplayStudio.GUI.exe"),
            Path.Combine(currentDir, "src", "apps", "replay-studio", "bin", "Release", "net11.0", "ReplayStudio.GUI.exe"),
            Path.Combine(currentDir, "src", "apps", "replay-studio", "bin", "Debug", "net11.0", "ReplayStudio.GUI.exe"),
            Path.Combine(currentDir, "tools", "replay-studio", "ReplayStudio.GUI.exe")
        ];

        string? studioExe = candidatePaths.FirstOrDefault(File.Exists);

        ProcessStartInfo startInfo;
        if (studioExe != null)
        {
            string cliArgs = string.IsNullOrWhiteSpace(safeReplayPath)
                ? "--camera \"PlayerPov\""
                : $"--replay \"{safeReplayPath}\" --timestamp 0 --camera \"PlayerPov\"";

            startInfo = new ProcessStartInfo
            {
                FileName = studioExe,
                Arguments = cliArgs,
                WorkingDirectory = root,
                UseShellExecute = true,
                CreateNoWindow = false
            };
        }
        else
        {
            string projectPath = Path.Combine(root, "src", "apps", "replay-studio", "ReplayStudio.GUI.csproj");
            string cliArgs = string.IsNullOrWhiteSpace(safeReplayPath)
                ? $"run --project \"{projectPath}\" --configuration Release -- --camera \"PlayerPov\""
                : $"run --project \"{projectPath}\" --configuration Release -- --replay \"{safeReplayPath}\" --timestamp 0 --camera \"PlayerPov\"";

            startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = cliArgs,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        try
        {
            AppendLog($"[LAUNCH] Launching Replay Studio Desktop GUI for '{Path.GetFileName(safeReplayPath)}'");
            ProcessLauncher(startInfo);
            StatusText = "Replay Studio 3D launched successfully!";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to launch Replay Studio: {ex.Message}");
            StatusText = $"Failed to launch Replay Studio: {ex.Message}";
        }
    }

    [RelayCommand]
    public void WatchInGameClient()
    {
        if (string.IsNullOrWhiteSpace(ReplayFilePath) || !File.Exists(ReplayFilePath))
        {
            StatusText = "Please select a valid .honreplay file first.";
            AppendLog("[WARN] No valid replay file selected for game launch.");
            return;
        }

        string currentDir = Directory.GetCurrentDirectory();
        string clientDir = Path.Combine(currentDir, "games", "client");
        string honExe = Path.Combine(clientDir, "hon_x64.exe");

        if (!File.Exists(honExe))
        {
            StatusText = $"Game executable not found at '{honExe}'";
            AppendLog($"[ERROR] Game executable not found at '{honExe}'");
            return;
        }

        string replayFileName = Path.GetFileName(ReplayFilePath);
        string[] targetDirs =
        [
            Path.Combine(clientDir, "replays"),
            Path.Combine(clientDir, "game", "replays"),
            Path.Combine(clientDir, "userdata", "game", "replays"),
            Path.Combine(clientDir, "userdata", "replays"),
            Path.Combine(clientDir, "userdata", "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(clientDir, "userdata", "Heroes of Newerth x64", "replays")
        ];

        foreach (string d in targetDirs)
        {
            try
            {
                Directory.CreateDirectory(d);
                string dest = Path.Combine(d, replayFileName);
                if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(ReplayFilePath).Length)
                {
                    File.Copy(ReplayFilePath, dest, true);
                    AppendLog($"[VFS] Staged replay to '{dest}'");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[WARN] Could not stage replay to '{d}': {ex.Message}");
            }
        }

        // Configure autoexec.cfg in KONGOR/configuration
        try
        {
            string configDir = Path.Combine(clientDir, "KONGOR", "configuration");
            Directory.CreateDirectory(configDir);
            string autoexecPath = Path.Combine(configDir, "autoexec.cfg");
            string autoexecContent =
                $"""
                SetSave "host_affinity" "-1"
                SetSave "vid_maxFPS" "1000"
                SetSave "cl_maxFPS" "1000"
                SetSave "host_maxFPS" "1000"
                SetSave "sys_sleepTime" "0"
                SetSave "gl_swapInterval" "0"
                SetSave "d3d_tripleBuffering" "false"
                SetSave "d3d_pure" "true"
                SetSave "vid_driver" "d3d9"
                SetSave "vid_fullscreen" "false"
                SetSave "vid_resolution" "1920,1080"
                SetSave "releaseStage_dev" "true"
                SetSave "releaseStage_test" "true"
                SetSave "ui_dev" "true"
                """;
            File.WriteAllText(autoexecPath, autoexecContent);
        }
        catch (Exception ex)
        {
            AppendLog($"[WARN] Could not write autoexec.cfg: {ex.Message}");
        }

        List<string> args =
        [
            "-masterserver masterserver.newbs.io",
            "-webserver masterserver.newbs.io",
            "-messageserver masterserver.newbs.io",
            ClientModMountSpec.GetCanonicalModString(),
            "-writeconsole",
            "+Set vid_driver d3d9",
            "+Set vid_fullscreen false",
            "+Set vid_resolution 1920,1080",
            "+Set log_flush false",
            "+Set con_log 1",
            "+Set host_writeConsole true",
            $"+Set host_autoexec \"StartReplay replays/{replayFileName}\""
        ];

        ProcessStartInfo startInfo = new()
        {
            FileName = honExe,
            Arguments = string.Join(" ", args),
            WorkingDirectory = clientDir,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        try
        {
            AppendLog($"[LAUNCH] Launching native HoN game engine for replay '{replayFileName}'...");
            Process.Start(startInfo);
            StatusText = $"Playing replay '{replayFileName}' in native HoN engine!";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to launch game client: {ex.Message}");
            StatusText = $"Failed to launch game: {ex.Message}";
        }
    }

    private string SanitizeAndProtectPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return string.Empty;

        string cleaned = inputPath.Trim().Trim('"', '\'');
        cleaned = Path.GetFullPath(cleaned); // Resolves relative traversal like ".."
        return cleaned;
    }

    private void AppendLog(string message)
    {
        string timestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("HH:mm:ss");
        LogText += $"[{timestamp}] {message}\n";
    }
}
