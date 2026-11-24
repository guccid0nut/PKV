// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Authoritative metadata record for a running player game client instance managed by TAO.
/// </summary>
public sealed class ClientInstance
{
    public int ProcessId { get; init; }
    public IntPtr MainWindowHandle { get; set; }
    public int SlotIndex { get; init; }
    public int OffscreenX { get; init; }
    public int OffscreenY { get; init; }
    public string PipeName { get; init; } = string.Empty;
    public string? NativePipeName { get; init; }
    public string WindowMode { get; init; } = "headless";
    public string Username { get; set; } = string.Empty;
    public string? AgentId { get; init; }
    public string? AgentOwner { get => AgentId; init => AgentId = value; }
    public DateTimeOffset LaunchedAt { get; init; }
    public long ProcessStartTimeTicks { get; set; }
    public Process Process { get; init; } = null!;
    public StreamWriter? Stdin { get; set; }
    public CancellationTokenSource? WatchdogCts { get; set; }

    public bool IsAlive
    {
        get
        {
            try
            {
                return Process != null && !Process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}

/// <summary>
/// Service interface for registering, querying, and routing to managed game client instances.
/// </summary>
public interface IClientProcessRegistry
{
    void RegisterClient(ClientInstance instance);
    void Register(ClientInstance instance) => RegisterClient(instance);
    bool UnregisterClient(int processId, out ClientInstance? instance);
    bool Unregister(int processId) => UnregisterClient(processId, out _);
    ClientInstance? GetClient(int processId);
    ClientInstance? Get(int processId) => GetClient(processId);
    ClientInstance? GetClientByAgentId(string agentId);
    ClientInstance? GetByAgent(string agentId) => GetClientByAgentId(agentId);
    bool ValidateAgentAccess(string? callerAgentId, int processId, out string? errorReason);
    ClientInstance? GetBySlot(int slotIndex) => GetAllClients().FirstOrDefault(c => c.SlotIndex == slotIndex);
    ClientInstance? GetPrimaryClient();
    void SetPrimaryClient(int processId);
    IReadOnlyList<ClientInstance> GetAllClients();
    IReadOnlyList<ClientInstance> GetAll() => GetAllClients();
    bool IsManagedClient(int processId);
    ClientInstance? AutoAdoptRunningClient(string? agentId = null, string? pipeName = null);
    void PruneDeadClients();
}

/// <summary>
/// Concurrent, thread-safe registry tracking all player game clients spawned for automated or agent testing.
/// Protects unmanaged user game clients by maintaining an explicit boundary between managed test instances
/// and the player's personal interactive game session.
/// </summary>
public sealed class ClientProcessRegistry : IClientProcessRegistry
{
    public static ClientProcessRegistry Default { get; } = new();

    private static readonly string s_persistedRegistryPath = Path.Combine(Path.GetTempPath(), "kongor_mcp_client_registry.json");

    private sealed class PersistedClientRecord
    {
        public int ProcessId { get; set; }
        public int SlotIndex { get; set; }
        public int OffscreenX { get; set; }
        public int OffscreenY { get; set; }
        public string PipeName { get; set; } = string.Empty;
        public string? NativePipeName { get; set; }
        public string WindowMode { get; set; } = "headless";
        public string Username { get; set; } = string.Empty;
        public string? AgentId { get; set; }
        public DateTimeOffset LaunchedAt { get; set; }
        public long ProcessStartTimeTicks { get; set; }
    }

    internal static Func<int, long> OsProcessStartTimeProbe { get; set; } = DefaultProbeProcessStartTimeTicks;

    public static long SafeGetStartTimeTicks(Process? process)
    {
        if (process == null) return 0;
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static long DefaultProbeProcessStartTimeTicks(int pid)
    {
        if (pid <= 0) return 0;
        try
        {
            using Process p = Process.GetProcessById(pid);
            return SafeGetStartTimeTicks(p);
        }
        catch
        {
            return 0;
        }
    }

    private static bool StartTimeMatches(long expectedTicks, long actualTicks)
    {
        if (expectedTicks <= 0 || actualTicks <= 0) return true;
        return Math.Abs(actualTicks - expectedTicks) <= TimeSpan.FromSeconds(2).Ticks;
    }

    private readonly ConcurrentDictionary<int, ClientInstance> _clients = new();
    private readonly Lock _primaryLock = new();
    private volatile int _primaryPid;
    private readonly Action<string> _logger;

    public ClientProcessRegistry(Action<string>? logger = null)
    {
        _logger = logger ?? (msg => Console.Error.WriteLine($"[ClientProcessRegistry] {msg}"));
        RehydratePersistedClientsIfNeeded();
    }

    private void PersistLiveRegistry()
    {
        if (Handlers.GameServerManageHandler.IsTestExecution) return;
        try
        {
            var records = _clients.Values
                .Where(c => c.IsAlive)
                .Select(c => new PersistedClientRecord
                {
                    ProcessId = c.ProcessId,
                    SlotIndex = c.SlotIndex,
                    OffscreenX = c.OffscreenX,
                    OffscreenY = c.OffscreenY,
                    PipeName = c.PipeName,
                    NativePipeName = c.NativePipeName,
                    WindowMode = c.WindowMode,
                    Username = c.Username,
                    AgentId = c.AgentId,
                    LaunchedAt = c.LaunchedAt,
                    ProcessStartTimeTicks = c.ProcessStartTimeTicks
                })
                .ToList();

            string json = JsonSerializer.Serialize(records);
            File.WriteAllText(s_persistedRegistryPath, json);
        }
        catch (Exception ex)
        {
            _logger($"[DEBUG] Ignored registry persistence error: {ex.Message}");
        }
    }

    private void RehydratePersistedClientsIfNeeded()
    {
        if (Handlers.GameServerManageHandler.IsTestExecution) return;
        if (!_clients.IsEmpty) return;
        try
        {
            if (!File.Exists(s_persistedRegistryPath)) return;
            string json = File.ReadAllText(s_persistedRegistryPath);
            var records = JsonSerializer.Deserialize<List<PersistedClientRecord>>(json);
            if (records == null || records.Count == 0) return;

            var supervisor = new ClientProcessSupervisor();
            foreach (var rec in records)
            {
                if (rec.ProcessId <= 0 || _clients.ContainsKey(rec.ProcessId)) continue;
                try
                {
                    Process p = Process.GetProcessById(rec.ProcessId);
                    if (p.HasExited || !supervisor.IsClientProcess(p)) continue;
                    long actualTicks = SafeGetStartTimeTicks(p);
                    if (!StartTimeMatches(rec.ProcessStartTimeTicks, actualTicks)) continue;

                    var instance = new ClientInstance
                    {
                        ProcessId = p.Id,
                        MainWindowHandle = p.MainWindowHandle,
                        SlotIndex = rec.SlotIndex,
                        OffscreenX = rec.OffscreenX,
                        OffscreenY = rec.OffscreenY,
                        PipeName = string.IsNullOrWhiteSpace(rec.PipeName) ? "kongor-native-client" : rec.PipeName,
                        NativePipeName = rec.NativePipeName,
                        WindowMode = string.IsNullOrWhiteSpace(rec.WindowMode) ? "headless" : rec.WindowMode,
                        Username = string.IsNullOrWhiteSpace(rec.Username) ? "GUEST-11" : rec.Username,
                        AgentId = rec.AgentId,
                        LaunchedAt = rec.LaunchedAt,
                        ProcessStartTimeTicks = actualTicks > 0 ? actualTicks : rec.ProcessStartTimeTicks,
                        Process = p
                    };
                    _clients[instance.ProcessId] = instance;
                    lock (_primaryLock)
                    {
                        if (_primaryPid == 0) _primaryPid = instance.ProcessId;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger($"[DEBUG] Ignored registry rehydration error: {ex.Message}");
        }
    }

    public void RegisterClient(ClientInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.ProcessStartTimeTicks <= 0 && instance.Process != null)
        {
            instance.ProcessStartTimeTicks = SafeGetStartTimeTicks(instance.Process);
        }
        _clients[instance.ProcessId] = instance;

        lock (_primaryLock)
        {
            _primaryPid = instance.ProcessId;
        }

        PersistLiveRegistry();
        _logger($"Registered client instance PID {instance.ProcessId} (Slot: {instance.SlotIndex}, Coords: ({instance.OffscreenX},{instance.OffscreenY}), User: '{instance.Username}', Pipe: '{instance.PipeName}', Agent: '{instance.AgentId ?? "none"}'). Total managed: {_clients.Count}");
    }

    public bool UnregisterClient(int processId, out ClientInstance? instance)
    {
        if (_clients.TryRemove(processId, out instance))
        {
            try
            {
                instance.WatchdogCts?.Cancel();
                instance.WatchdogCts?.Dispose();
            }
            catch { }

            lock (_primaryLock)
            {
                if (_primaryPid == processId)
                {
                    // Select newest remaining alive client as primary
                    var next = _clients.Values.Where(c => c.IsAlive).OrderByDescending(c => c.LaunchedAt).FirstOrDefault();
                    _primaryPid = next?.ProcessId ?? 0;
                }
            }

            PersistLiveRegistry();
            _logger($"Unregistered client instance PID {processId}. Remaining managed: {_clients.Count}");
            return true;
        }

        instance = null;
        return false;
    }

    public ClientInstance? GetClient(int processId)
    {
        if (processId <= 0) return null;
        if (_clients.TryGetValue(processId, out var instance))
        {
            if (instance.IsAlive) return instance;
        }
        return null;
    }

    public ClientInstance? GetClientByAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        string clean = agentId.Trim();

        var directMatch = _clients.Values
            .Where(c => c.IsAlive && string.Equals(c.AgentId, clean, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LaunchedAt)
            .FirstOrDefault();

        if (directMatch != null) return directMatch;

        // Fallback: If caller is "ambient" or "auto", or if there is an unassigned / ambient client
        var ambientMatch = _clients.Values
            .Where(c => c.IsAlive && (string.IsNullOrWhiteSpace(c.AgentId) ||
                                      string.Equals(c.AgentId, "ambient", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(c.AgentId, "auto", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.LaunchedAt)
            .FirstOrDefault();

        if (ambientMatch != null) return ambientMatch;

        // Architectural boundary decision: When only 1 client is running, solo developer agents auto-adopt it;
        // when _clients.Count > 1, strict multi-agent isolation is enforced and unmatched agentIds return null.
        if (_clients.Count == 1)
        {
            var single = _clients.Values.FirstOrDefault(c => c.IsAlive);
            if (single != null) return single;
        }

        return null;
    }

    public ClientInstance? AutoAdoptRunningClient(string? agentId = null, string? pipeName = null)
    {
        PruneDeadClients();

        // 1. Check existing managed clients first
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var existing = GetClientByAgentId(agentId);
            if (existing != null) return existing;
        }
        else if (!_clients.IsEmpty)
        {
            var primary = GetPrimaryClient();
            if (primary != null) return primary;
        }

        // 2. Discover ambient hon_x64 process
        if (Handlers.GameServerManageHandler.IsTestExecution)
        {
            return null;
        }

        try
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            var supervisor = new ClientProcessSupervisor();

            foreach (Process p in procs)
            {
                if (p.Id == Environment.ProcessId || p.HasExited) continue;
                if (!supervisor.IsClientProcess(p)) continue;

                if (_clients.TryGetValue(p.Id, out var registered) && registered.IsAlive)
                {
                    return registered;
                }

                // Auto-adopt ambient process
                bool isOffscreen = p.MainWindowHandle != IntPtr.Zero && supervisor.IsWindowOffscreen(p.MainWindowHandle);
                string chosenPipe = !string.IsNullOrWhiteSpace(pipeName) ? pipeName : "kongor-native-client";

                var instance = new ClientInstance
                {
                    ProcessId = p.Id,
                    MainWindowHandle = p.MainWindowHandle,
                    SlotIndex = 0,
                    OffscreenX = 0,
                    OffscreenY = 0,
                    PipeName = chosenPipe,
                    WindowMode = isOffscreen ? "headless" : "foreground",
                    Username = "AmbientClient",
                    AgentId = agentId,
                    LaunchedAt = TimeProvider.System.GetUtcNow(),
                    Process = p
                };

                RegisterClient(instance);
                _logger($"Auto-adopted ambient game client PID {p.Id} under agent '{agentId ?? "ambient"}' (Pipe: '{chosenPipe}').");
                return instance;
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Failed to auto-adopt ambient game client: {ex.Message}");
        }

        return null;
    }

    public ClientInstance? GetPrimaryClient()
    {
        RehydratePersistedClientsIfNeeded();
        int pid = _primaryPid;
        if (pid > 0 && _clients.TryGetValue(pid, out var primary) && primary.IsAlive)
        {
            return primary;
        }

        // Fallback to any alive managed client
        var candidate = _clients.Values
            .Where(c => c.IsAlive)
            .OrderByDescending(c => c.LaunchedAt)
            .FirstOrDefault();

        if (candidate != null)
        {
            lock (_primaryLock)
            {
                _primaryPid = candidate.ProcessId;
            }
        }

        return candidate;
    }

    public void SetPrimaryClient(int processId)
    {
        if (_clients.ContainsKey(processId))
        {
            lock (_primaryLock)
            {
                _primaryPid = processId;
            }
        }
    }

    public IReadOnlyList<ClientInstance> GetAllClients()
    {
        RehydratePersistedClientsIfNeeded();
        return _clients.Values.Where(c => c.IsAlive).OrderBy(c => c.SlotIndex).ToList();
    }

    public void Register(ClientInstance instance) => RegisterClient(instance);
    public bool Unregister(int processId) => UnregisterClient(processId, out _);
    public ClientInstance? Get(int processId) => GetClient(processId);
    public ClientInstance? GetBySlot(int slotIndex) => GetAllClients().FirstOrDefault(c => c.SlotIndex == slotIndex);
    public IReadOnlyList<ClientInstance> GetAll() => GetAllClients();

    public bool IsManagedClient(int processId)
    {
        if (processId <= 0) return false;
        RehydratePersistedClientsIfNeeded();
        if (!_clients.TryGetValue(processId, out ClientInstance? instance))
        {
            return false;
        }

        if (instance.ProcessStartTimeTicks > 0)
        {
            long actualTicks = OsProcessStartTimeProbe(processId);
            if (!StartTimeMatches(instance.ProcessStartTimeTicks, actualTicks))
            {
                UnregisterClient(processId, out _);
                return false;
            }
        }

        return true;
    }

    public bool ValidateAgentAccess(string? callerAgentId, int processId, out string? errorReason)
    {
        errorReason = null;
        if (processId <= 0)
        {
            errorReason = "Invalid process ID.";
            return false;
        }

        if (!_clients.TryGetValue(processId, out var instance) || !instance.IsAlive)
        {
            // Auto-adopt if processId is a running hon_x64 client
            if (!Handlers.GameServerManageHandler.IsTestExecution)
            {
                try
                {
                    var proc = Process.GetProcessById(processId);
                    if (!proc.HasExited && new ClientProcessSupervisor().IsClientProcess(proc))
                    {
                        instance = AutoAdoptRunningClient(callerAgentId);
                    }
                }
                catch { }
            }

            if (instance == null)
            {
                if (!string.IsNullOrWhiteSpace(callerAgentId))
                {
                    errorReason = $"Access denied: Process {processId} is not a managed test client. Agents are prohibited from interacting with unmanaged processes or the user's personal game session.";
                    return false;
                }
                return true;
            }
        }

        // Target process is a managed or auto-adopted test client
        if (!string.IsNullOrWhiteSpace(callerAgentId))
        {
            if (_clients.Count <= 1 ||
                string.IsNullOrWhiteSpace(instance.AgentId) ||
                string.Equals(instance.AgentId, "ambient", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(instance.AgentId, "auto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(callerAgentId, "ambient", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(callerAgentId, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(instance.AgentId, callerAgentId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                errorReason = $"Access denied: Process {processId} was spawned by agent '{instance.AgentId}'. Current agent '{callerAgentId}' cannot send commands to a client owned by another active agent.";
                return false;
            }
        }

        return true;
    }

    public void PruneDeadClients()
    {
        List<int> deadPids = [];
        foreach (var (pid, instance) in _clients)
        {
            if (!instance.IsAlive)
            {
                deadPids.Add(pid);
            }
        }

        foreach (int pid in deadPids)
        {
            UnregisterClient(pid, out _);
        }
    }

    /// <summary>
    /// #218 (Gem 3G): Candidate window descriptor for OBS penalty-scored window rating and cloak filtering.
    /// </summary>
    public sealed record WindowCandidateDescriptor(
        IntPtr WindowHandle,
        string ExecutableName,
        string ClassName,
        string WindowTitle,
        bool IsCloaked = false,
        int Width = 1280,
        int Height = 720,
        bool IsVisible = true,
        bool IsToolWindow = false);

    /// <summary>
    /// #218 (Gem 3G): Evaluates candidate windows using OBS-Studio's penalty-scored window rating
    /// and DWMWA_CLOAKED=14 filter, selecting the highest-rated uncloaked window candidate.
    /// </summary>
    public WindowCandidateDescriptor? SelectBestClientWindowCandidate(
        IReadOnlyList<WindowCandidateDescriptor> candidates,
        string expectedExe,
        string? expectedClass,
        string? expectedTitle,
        out int bestScore)
    {
        bestScore = int.MinValue;
        if (candidates == null || candidates.Count == 0)
        {
            return null;
        }

        WindowCandidateDescriptor? bestCandidate = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            WindowCandidateDescriptor candidate = candidates[i];
            int score = WindowCapturer.ComputeObsWindowRating(
                candidate.ClassName,
                candidate.WindowTitle,
                candidate.ExecutableName,
                candidate.Width,
                candidate.Height,
                candidate.IsVisible,
                candidate.IsCloaked,
                candidate.IsToolWindow);

            if (score != int.MinValue)
            {
                if (!string.IsNullOrWhiteSpace(expectedExe) &&
                    !string.Equals(candidate.ExecutableName, expectedExe, StringComparison.OrdinalIgnoreCase))
                {
                    score -= 300;
                }
                if (!string.IsNullOrWhiteSpace(expectedClass) &&
                    !string.Equals(candidate.ClassName, expectedClass, StringComparison.OrdinalIgnoreCase))
                {
                    score -= 150;
                }
                if (!string.IsNullOrWhiteSpace(expectedTitle) &&
                    !string.Equals(candidate.WindowTitle, expectedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    score -= Math.Abs((candidate.WindowTitle?.Length ?? 0) - expectedTitle.Length) * 5;
                }
            }

            _logger($"[ObsWindowRating] Candidate HWND=0x{candidate.WindowHandle:X} exe='{candidate.ExecutableName}' class='{candidate.ClassName}' title='{candidate.WindowTitle}' cloaked={candidate.IsCloaked} -> score={score}");
            if (score == int.MinValue)
            {
                continue;
            }

            if (bestCandidate == null || score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }
}


