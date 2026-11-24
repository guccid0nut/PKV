// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Pipes;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

public sealed class GraphicsStatusResult
{
    public string Backend { get; init; } = "Vulkan 1.3 (DXVK)";
    public string Driver { get; init; } = "nvd3dum.dll";
    public string Adapter { get; init; } = "Generic Direct3D Adapter";
    public string TargetRefreshRate { get; init; } = "240Hz";
    public int MaxFps { get; init; } = 1000;
    public string DisplayMode { get; init; } = "Borderless";
}

public sealed class AudioStatusResult
{
    public string DriverName { get; init; } = "Default Audio Device";
    public string Api { get; init; } = "Windows Audio Session API (WASAPI)";
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 64;
    public bool IsReady { get; init; } = true;
    public bool FallbackToMute { get; init; } = false;
}

public static class EngineTelemetryDiagnostics
{
    public static string FormatPhaseName(int phase) => phase switch
    {
        0 or 1 => "Login (0-1)",
        2 or 3 or 4 => "Lobby/Chat (2-4)",
        5 => "HeroSelect (5)",
        6 => "LoadingMap (6)",
        7 => "InGame (7)",
        _ => phase < 2 ? "Login (0-1)" : phase <= 4 ? "Lobby/Chat (2-4)" : "Unknown"
    };

    public static async Task<(int Phase, string PhaseName)> GetEnginePhaseAsync(
        Process? proc,
        IGameIpcBridge? ipcBridge,
        string? clientDir = null,
        CancellationToken cancellationToken = default)
    {
        if (proc == null || proc.HasExited)
        {
            return (-1, "Offline");
        }

        // 1. Try querying live IPC if bridge is connected
        if (ipcBridge != null && ipcBridge.IsConnected)
        {
            try
            {
                EngineStateDto? state = await ipcBridge.GetEngineStateAsync(500, cancellationToken).ConfigureAwait(false);
                if (state != null)
                {
                    if (state.ActiveMatchId > 0 && !string.IsNullOrEmpty(state.CurrentMap))
                        return (7, "InGame (7)");

                    if (!string.IsNullOrEmpty(state.CurrentMap))
                        return (6, "LoadingMap (6)");

                    if (state.GamePhase >= 0)
                        return (state.GamePhase, FormatPhaseName(state.GamePhase));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineTelemetryDiagnostics] Ignored GetEngineStateAsync error: {ex.Message}");
            }
        }

        // 2. Inspect window title and active modules
        try
        {
            proc.Refresh();
            string title = proc.MainWindowTitle;
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (title.Contains("Caldavar", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Mid Wars", StringComparison.OrdinalIgnoreCase))
                {
                    return (7, "InGame (7)");
                }
                if (title.Contains("Loading", StringComparison.OrdinalIgnoreCase))
                {
                    return (6, "LoadingMap (6)");
                }
                if (title.Contains("Hero Select", StringComparison.OrdinalIgnoreCase))
                {
                    return (5, "HeroSelect (5)");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EngineTelemetryDiagnostics] Ignored window title inspection error: {ex.Message}");
        }

        // 3. Inspect recent console.log or clientstate
        string baseDir = clientDir ?? ResolveClientDirectory();
        string extDir = Path.Combine(baseDir, "userdataKONGOR", "extensions");

        if (Directory.Exists(extDir))
        {
            try
            {
                // Check recent clientstate-*.xml files
                string[] stateFiles = Directory.GetFiles(extDir, "clientstate-*.xml");
                if (stateFiles.Length > 0)
                {
                    FileInfo latest = stateFiles.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).First();
                    if ((TimeProvider.System.GetUtcNow() - latest.LastWriteTimeUtc).TotalMinutes < 5)
                    {
                        string xml = File.ReadAllText(latest.FullName);
                        if (xml.Contains("state=\"in_game\"", StringComparison.OrdinalIgnoreCase)) return (7, "InGame (7)");
                        if (xml.Contains("state=\"loading\"", StringComparison.OrdinalIgnoreCase)) return (6, "LoadingMap (6)");
                        if (xml.Contains("state=\"hero_select\"", StringComparison.OrdinalIgnoreCase)) return (5, "HeroSelect (5)");
                        if (xml.Contains("state=\"lobby\"", StringComparison.OrdinalIgnoreCase)) return (3, "Lobby/Chat (2-4)");
                        if (xml.Contains("state=\"idle\"", StringComparison.OrdinalIgnoreCase)) return (1, "Login (0-1)");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineTelemetryDiagnostics] Ignored clientstate XML read error: {ex.Message}");
            }
        }

        // Default running phase is Login (0-1)
        return (1, "Login (0-1)");
    }

    public static bool CheckIpcConnectivity(string pipeName = "kongor-native-client", int timeoutMs = 150)
    {
        if (McpIpcServer.IsPipeHostedInCurrentProcess(pipeName))
        {
            return McpIpcServer.GetHostedPipeConnectedClientCount(pipeName) > 0;
        }

        try
        {
            using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(timeoutMs);
            return client.IsConnected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EngineTelemetryDiagnostics] CheckIpcConnectivity('{pipeName}') failed: {ex.Message}");
            return false;
        }
    }

    public static GraphicsStatusResult GetGraphicsStatus(string? clientDir = null)
    {
        string baseDir = clientDir ?? ResolveClientDirectory();
        string d3d9Log = Path.Combine(baseDir, "hon_x64_d3d9.log");
        string consoleLog = Path.Combine(baseDir, "userdataKONGOR", "extensions", "console.log");

        bool isDxvk = File.Exists(d3d9Log);
        string adapter = "Generic Direct3D Adapter";
        string driver = isDxvk ? "nvd3dum.dll (Vulkan Layer)" : "nvd3dum.dll";
        string refreshRate = "240Hz";
        int maxFps = 1000;
        string displayMode = "Borderless";

        if (isDxvk)
        {
            try
            {
                string text = File.ReadAllText(d3d9Log);
                Match m = Regex.Match(text, @"DXVK:\s*(v[0-9\.]+)");
                if (m.Success)
                {
                    driver = $"nvd3dum.dll (DXVK {m.Groups[1].Value})";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineTelemetryDiagnostics] Ignored d3d9.log read error: {ex.Message}");
            }
        }

        if (File.Exists(consoleLog))
        {
            try
            {
                using FileStream fs = new(consoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(fs, System.Text.Encoding.Unicode);
                string content = reader.ReadToEnd();

                Match adapterMatch = Regex.Match(content, @"Found \d+ video adapters\s*Vid:\s*\[0\]\s*-\s*([^\r\n]+)");
                if (adapterMatch.Success)
                {
                    adapter = adapterMatch.Groups[1].Value.Trim();
                }

                Match refreshMatch = Regex.Match(content, @"Refresh rate:\s*(\d+)");
                if (refreshMatch.Success)
                {
                    refreshRate = $"{refreshMatch.Groups[1].Value}Hz";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineTelemetryDiagnostics] Ignored console.log graphics read error: {ex.Message}");
            }
        }

        return new GraphicsStatusResult
        {
            Backend = isDxvk ? "Vulkan 1.3 (DXVK)" : "Direct3D 9 (Native)",
            Driver = driver,
            Adapter = adapter,
            TargetRefreshRate = refreshRate,
            MaxFps = maxFps,
            DisplayMode = displayMode
        };
    }

    public static AudioStatusResult GetAudioStatus(string? clientDir = null)
    {
        string baseDir = clientDir ?? ResolveClientDirectory();
        string consoleLog = Path.Combine(baseDir, "userdataKONGOR", "extensions", "console.log");

        string driverName = "Default Audio Device";
        string api = "Windows Audio Session API (WASAPI)";
        int sampleRate = 48000;
        int channels = 64;
        bool isReady = true;
        bool fallbackToMute = false;

        if (File.Exists(consoleLog))
        {
            try
            {
                using FileStream fs = new(consoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(fs, System.Text.Encoding.Unicode);
                string content = reader.ReadToEnd();

                if (content.Contains("SoundManager: Playback driver is ready!"))
                {
                    isReady = true;
                    fallbackToMute = false;
                    Match m = Regex.Match(content, @"driver name:\s*([^\r\n]+)");
                    if (m.Success) driverName = m.Groups[1].Value.Trim();
                }
                else if (content.Contains("Playback driver is not ready") || content.Contains("dummy"))
                {
                    isReady = false;
                    fallbackToMute = true;
                }

                if (content.Contains("Windows Audio Session API"))
                {
                    api = "Windows Audio Session API (WASAPI)";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineTelemetryDiagnostics] Ignored console.log audio read error: {ex.Message}");
            }
        }

        return new AudioStatusResult
        {
            DriverName = driverName,
            Api = api,
            SampleRate = sampleRate,
            Channels = channels,
            IsReady = isReady,
            FallbackToMute = fallbackToMute
        };
    }

    public static string GetClientNextAction(bool running, bool ipcConnected, int phase, int shadowedFilesCount)
    {
        if (!running)
        {
            return "Run 'kongor_game_client_manage action:launch' to start the game client.";
        }

        if (!ipcConnected)
        {
            return "Game client process is active but Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) is not connected. Check if hon_x64 was launched with '-pipe willowmaker-ipc'.";
        }

        if (phase <= 1)
        {
            return "Client is on Login phase. Verify MasterServer (port 55555) is responding using 'kongor_server_manage action:status target:cluster'.";
        }

        if (shadowedFilesCount > 0)
        {
            return $"Info: {shadowedFilesCount} override file(s) are active in games/client/override/. If textures or shaders appear stale, run 'kongor_engine_cache_purge'.";
        }

        return "Client is healthy and responding to IPC commands.";
    }

    private static string ResolveClientDirectory()
    {
        string current = Environment.CurrentDirectory;
        string direct = Path.Combine(current, "games", "client");
        if (Directory.Exists(direct)) return direct;

        DirectoryInfo? dir = new(current);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "games", "client");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return Path.Combine(current, "games", "client");
    }
}

/// <summary>
/// #222 (Gem 3H): Summary metrics for a single scope node in <see cref="KongorQpcCallTreeProfiler"/>.
/// </summary>
public sealed record QpcCallTreeScopeSummary(
    string ScopeName,
    string? ParentScopeName,
    long CallCount,
    long MinUs,
    long MaxUs,
    long TotalUs,
    long P99Us,
    long BudgetUs,
    long OverbudgetCount,
    long Hitch5MsCount);

/// <summary>
/// #222 (Gem 3H): OBS-Studio inspired (`libobs/util/profiler.c`) hierarchical QPC microsecond call-tree profiler
/// enforcing the Project Kongor &lt;= 1.0 ms (1000 us) Lua/UI frame-budget invariant and 5.0 ms (5000 us) hitch detector.
/// </summary>
internal sealed class KongorQpcCallTreeProfiler
{
    public const long DefaultUiFrameBudgetUs = 1000L;
    public const long DefaultHitchWarningThresholdUs = 5000L;
    private const int MaxRetainedSamplesPerScope = 256;

    private sealed class ScopeAccumulator
    {
        public required string ScopeName { get; init; }
        public string? ParentScopeName { get; set; }
        public long BudgetUs { get; set; } = DefaultUiFrameBudgetUs;
        public long CallCount { get; set; }
        public long MinUs { get; set; } = long.MaxValue;
        public long MaxUs { get; set; }
        public long TotalUs { get; set; }
        public long OverbudgetCount { get; set; }
        public long Hitch5MsCount { get; set; }
        public List<long> RecentSamplesUs { get; } = new List<long>(MaxRetainedSamplesPerScope);
    }

    private readonly Lock _sync = new Lock();
    private readonly Dictionary<string, ScopeAccumulator> _scopes = new Dictionary<string, ScopeAccumulator>(StringComparer.Ordinal);
    private readonly Stack<(string ScopeName, long StartTimestampTicks, long BudgetUs)> _activeStack = new Stack<(string, long, long)>();

    public void BeginScope(string scopeName, long budgetUs = DefaultUiFrameBudgetUs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        long nowTicks = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _activeStack.Push((scopeName, nowTicks, budgetUs > 0 ? budgetUs : DefaultUiFrameBudgetUs));
        }
    }

    public long EndScope(long? overrideElapsedUs = null)
    {
        long endTicks = Stopwatch.GetTimestamp();
        string scopeName;
        string? parentName;
        long startTicks;
        long budgetUs;

        lock (_sync)
        {
            if (_activeStack.Count == 0)
            {
                return 0L;
            }

            (scopeName, startTicks, budgetUs) = _activeStack.Pop();
            parentName = _activeStack.Count > 0 ? _activeStack.Peek().ScopeName : null;
        }

        long elapsedUs = overrideElapsedUs ?? (long)((endTicks - startTicks) * 1_000_000.0 / Stopwatch.Frequency);
        if (elapsedUs < 0) elapsedUs = 0;

        RecordScopeSample(scopeName, parentName, elapsedUs, budgetUs);
        return elapsedUs;
    }

    public void RecordScopeSample(
        string scopeName,
        string? parentScopeName,
        long elapsedUs,
        long budgetUs = DefaultUiFrameBudgetUs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        long clampedElapsed = Math.Max(0L, elapsedUs);
        long effectiveBudget = budgetUs > 0 ? budgetUs : DefaultUiFrameBudgetUs;

        lock (_sync)
        {
            if (!_scopes.TryGetValue(scopeName, out ScopeAccumulator? acc))
            {
                acc = new ScopeAccumulator
                {
                    ScopeName = scopeName,
                    ParentScopeName = parentScopeName,
                    BudgetUs = effectiveBudget
                };
                _scopes[scopeName] = acc;
            }

            acc.ParentScopeName ??= parentScopeName;
            acc.BudgetUs = effectiveBudget;
            acc.CallCount++;
            acc.TotalUs += clampedElapsed;
            if (clampedElapsed < acc.MinUs) acc.MinUs = clampedElapsed;
            if (clampedElapsed > acc.MaxUs) acc.MaxUs = clampedElapsed;
            if (clampedElapsed > effectiveBudget) acc.OverbudgetCount++;
            if (clampedElapsed > DefaultHitchWarningThresholdUs) acc.Hitch5MsCount++;

            if (acc.RecentSamplesUs.Count >= MaxRetainedSamplesPerScope)
            {
                acc.RecentSamplesUs.RemoveAt(0);
            }
            acc.RecentSamplesUs.Add(clampedElapsed);
        }
    }

    public IReadOnlyList<QpcCallTreeScopeSummary> GetSnapshots()
    {
        lock (_sync)
        {
            List<QpcCallTreeScopeSummary> list = new List<QpcCallTreeScopeSummary>(_scopes.Count);
            foreach (ScopeAccumulator acc in _scopes.Values)
            {
                long p99 = ComputePercentile99(acc.RecentSamplesUs);
                long minUs = acc.CallCount > 0 ? acc.MinUs : 0L;
                list.Add(new QpcCallTreeScopeSummary(
                    ScopeName: acc.ScopeName,
                    ParentScopeName: acc.ParentScopeName,
                    CallCount: acc.CallCount,
                    MinUs: minUs,
                    MaxUs: acc.MaxUs,
                    TotalUs: acc.TotalUs,
                    P99Us: p99,
                    BudgetUs: acc.BudgetUs,
                    OverbudgetCount: acc.OverbudgetCount,
                    Hitch5MsCount: acc.Hitch5MsCount));
            }
            return list;
        }
    }

    public string ExportCsvSnapshot()
    {
        IReadOnlyList<QpcCallTreeScopeSummary> snapshots = GetSnapshots();
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("scope_name,parent_scope,call_count,min_us,max_us,total_us,p99_us,budget_us,overbudget_count,hitch_5ms_count");
        for (int i = 0; i < snapshots.Count; i++)
        {
            QpcCallTreeScopeSummary s = snapshots[i];
            sb.Append(s.ScopeName).Append(',')
              .Append(s.ParentScopeName ?? string.Empty).Append(',')
              .Append(s.CallCount).Append(',')
              .Append(s.MinUs).Append(',')
              .Append(s.MaxUs).Append(',')
              .Append(s.TotalUs).Append(',')
              .Append(s.P99Us).Append(',')
              .Append(s.BudgetUs).Append(',')
              .Append(s.OverbudgetCount).Append(',')
              .Append(s.Hitch5MsCount).AppendLine();
        }
        return sb.ToString();
    }

    public string ExportJsonSnapshot()
    {
        IReadOnlyList<QpcCallTreeScopeSummary> snapshots = GetSnapshots();
        return System.Text.Json.JsonSerializer.Serialize(snapshots);
    }

    private static long ComputePercentile99(List<long> samples)
    {
        if (samples.Count == 0) return 0L;
        long[] sorted = samples.ToArray();
        Array.Sort(sorted);
        int idx = (int)Math.Ceiling(sorted.Length * 0.99) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }
}

