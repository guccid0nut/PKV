// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Netcode;
using ASPIRE.Common.Utilities;
using COMPEL.Internals;
using COMPEL.Services.Diagnostics;
using COMPEL.Services.Telemetry;
using Kongor.Core.Configuration;
using Kongor.Core.Services.Native;
using Kongor.Core.Vfs;
using NEXUS.Contracts.Security;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Launches and supervises the dedicated Heroes Of Newerth match server worker fleet directly.
///     Unlike the legacy -manager model, COMPEL directly controls individual hon_x64.exe -dedicated worker
///     processes, holding redirected standard input (stdin) for instant console command execution,
///     binding dedicated Named Pipe IPC (\\.\pipe\hon-server-{port}), streaming stdout/stderr in memory
///     via zero-allocation PipeReader pipelines, and enforcing per-worker crash recovery and CPU affinity.
/// </summary>
public sealed class MatchServerManagerSupervisor(
    IOptions<MatchServerManagerOptions> options,
    DistributionSynchronisationService distribution,
    UDPProxyService proxy,
    AddressResolver addressResolver,
    ArtefactsLocator artefacts,
    PortPlan ports,
    CompelTelemetryRelayService telemetryRelay,
    ProcessTreeSandbox sandbox,
    DedicatedServerCrashResilienceService? crashResilience,
    ILogger<MatchServerManagerSupervisor> logger,
    AutomatedCrashSymbolizerService? crashSymbolizer = null,
    COMPEL.Services.Proxy.XdpPacketFilterManager? xdpFilter = null,
    IConfiguration? configuration = null,
    IHostEnvironment? hostEnvironment = null,
    COMPEL.Services.Telemetry.RlTrajectoryCollectorService? rlCollector = null,
    TimeProvider? timeProvider = null) : ResilientBackgroundService(logger, nameof(MatchServerManagerSupervisor))
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);
    private static readonly SearchValues<char> SanitizeTriggerChars = SearchValues.Create("\0\x1b\r\n");
    private static readonly Regex NewSessionCookieRegex = new(
        @"New session cookie \[([^\[\]\s]+)\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly AddressResolver addressResolver = addressResolver;
    private readonly ArtefactsLocator artefacts = artefacts;
    private readonly DistributionSynchronisationService distribution = distribution;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly ILogger<MatchServerManagerSupervisor> logger = logger;

    private readonly MatchServerManagerOptions options = options.Value;
    private readonly PortPlan ports = ports;
    private readonly UDPProxyService proxy = proxy;

    private readonly SemaphoreSlim reconcileSignal = new(0, int.MaxValue);
    private readonly ProcessTreeSandbox sandbox = sandbox;
    private readonly CompelTelemetryRelayService telemetryRelay = telemetryRelay;
    private readonly DedicatedServerCrashResilienceService? crashResilience = crashResilience;
    private readonly AutomatedCrashSymbolizerService _crashSymbolizer = crashSymbolizer ?? new AutomatedCrashSymbolizerService(options);
    private readonly COMPEL.Services.Proxy.XdpPacketFilterManager? xdpFilter = xdpFilter;
    private readonly IConfiguration? _configuration = configuration;
    private readonly IHostEnvironment? _hostEnvironment = hostEnvironment;

    private readonly ConcurrentDictionary<int, DedicatedServerWorker> _workers = new();
    private readonly HttpClient _masterServerHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                addresses = [];
            }

            // Prioritize IPv4 addresses to bypass Docker Desktop's unreachable IPv6 ULA routes
            IPAddress[] prioritized = [
                .. addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork),
                .. addresses.Where(a => a.AddressFamily != AddressFamily.InterNetwork)
            ];

            foreach (IPAddress ip in prioritized)
            {
                try
                {
                    Socket socket = new(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MatchServerManagerSupervisor] ConnectAsync fallback to next address ({ip}): {ex.Message}");
                }
            }

            // Fallback for container: if host was a container host alias, try container gateway
            if (AddressResolver.IsRunningInContainer && AddressResolver.IsContainerHostAlias(context.DnsEndPoint.Host))
            {
                string? gateway = AddressResolver.TryGetContainerGateway();
                if (!string.IsNullOrEmpty(gateway) && IPAddress.TryParse(gateway, out IPAddress? gatewayIp))
                {
                    Socket socket = new(gatewayIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    await socket.ConnectAsync(new IPEndPoint(gatewayIp, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            }

            throw new SocketException((int)SocketError.HostUnreachable);
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private volatile bool desiredRunning = options.Value.AutoStart;
    private volatile bool _lastAdaptiveClientRunning;
    private volatile bool _serverManagerAuthenticated;

    /// <summary>
    /// Testing constructor for isolated command execution and worker routing unit tests.
    /// </summary>
    internal MatchServerManagerSupervisor(
        IOptions<MatchServerManagerOptions> options,
        ILogger<MatchServerManagerSupervisor> logger,
        TimeProvider? timeProvider = null)
        : this(
            options,
            null!,
            null!,
            null!,
            null!,
            new PortPlan(options.Value),
            null!,
            null!,
            null,
            logger,
            timeProvider: timeProvider)
    {
    }

    public PortPlan Ports => ports;

    public string? ServerAddress { get; private set; }

    /// <summary>
    /// Whether any dedicated worker process is currently running.
    /// </summary>
    public bool IsRunning => _workers.Values.Any(w => w.IsRunning);

    /// <summary>
    /// Total count of currently running dedicated worker processes.
    /// </summary>
    public int ActiveWorkerCount => _workers.Values.Count(w => w.IsRunning);

    /// <summary>
    /// All configured dedicated worker slots in the fleet.
    /// </summary>
    public IReadOnlyCollection<DedicatedServerWorker> Workers => _workers.Values.OrderBy(w => w.Port).ToList().AsReadOnly();

    /// <summary>
    /// Whether the fleet is intended to be running.
    /// </summary>
    public bool DesiredRunning => desiredRunning;

    /// <summary>
    /// Event raised when an active match cleanly concludes on a dedicated server worker.
    /// Used by FleetDrainingService and PostMatchParquetIngestorService to execute deferred clean drains and Parquet export.
    /// </summary>
    public event Func<DedicatedServerWorker, Task>? MatchEnded;

    /// <summary>
    /// Event raised when an active match starts on a dedicated server worker.
    /// Used by AgonesLifecycleService to dispatch /allocate to the Agones SDK sidecar.
    /// </summary>
    public event Func<DedicatedServerWorker, Task>? MatchStarted;

    /// <summary>
    /// Event raised when a dedicated server worker process exits abnormally with a non-zero exit code.
    /// Used by ReplayWalRecoveryService to salvage in-flight match journals.
    /// </summary>
    public event Func<DedicatedServerWorker, int, Task>? WorkerCrashed;

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        KillOrphanedProcesses();

        BundledLayout? bundledLayout = null;
        bool isStandalone = string.Equals(Environment.GetEnvironmentVariable("KONGOR_STANDALONE"), "true", StringComparison.OrdinalIgnoreCase) ||
                            BundledLayoutResolver.TryResolve(out bundledLayout);

        if (isStandalone)
        {
            if (bundledLayout is null)
            {
                BundledLayoutResolver.TryResolve(out bundledLayout);
            }

            string clientDir = bundledLayout?.ClientDir ?? Path.GetFullPath(Path.Combine(distribution.InstallationDirectory, "..", "client"));
            if (!Directory.Exists(clientDir))
            {
                string candidateClientDir = Path.GetFullPath(Path.Combine(distribution.InstallationDirectory, "..", "..", "client"));
                if (Directory.Exists(candidateClientDir))
                {
                    clientDir = candidateClientDir;
                }
            }

            SharedClientAssetLinker.EnsureLinked(distribution.InstallationDirectory, clientDir, msg => logger.LogInformation("{Msg}", msg));
        }

        // Pre-warm 64KB RFC 8878 Zstandard static netcode dictionary to guarantee zero cold-start JIT/IO stutter on UDP forwarders
        try
        {
            ZstdNetcodeDictionary dict = ZstdNetcodeDictionary.Instance;
            dict.EnsureLoaded();
            if (dict.IsLoaded)
            {
                logger.LogInformation(
                    "[FleetSupervisor] Pre-warmed 64KB Zstandard netcode dictionary (ID: 0x{DictId:X8}, Source: {Source}, Length: {Length} bytes) for MTU-clamped delta compression.",
                    dict.DictionaryId, dict.LoadSource, dict.DictionaryBytes.Length);
            }
            else
            {
                logger.LogWarning(
                    "[FleetSupervisor] Zstandard netcode dictionary running in degraded mode ({Source}). Continuing with standard UDP delta compression without crashing supervisor.",
                    dict.LoadSource);
            }
        }
        catch (Exception zstdEx)
        {
            logger.LogError(zstdEx,
                "[FleetSupervisor] Failed to initialize Zstandard netcode dictionary. Continuing in degraded mode without crash-looping.");
        }

        logger.LogInformation("[FleetSupervisor] Waiting For The Match Server Distribution To Be Ready...");

        try
        {
            await distribution.WaitUntilReady(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Resolve advertised address with backoff
        while (stoppingToken.IsCancellationRequested is false)
            try
            {
                ServerAddress = await addressResolver.ResolveServerAddress(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[FleetSupervisor] Unable To Resolve The Server Address; Retrying In {Seconds} Second(s)",
                    RestartBackoff.TotalSeconds);

                try
                {
                    await Task.Delay(RestartBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

        if (stoppingToken.IsCancellationRequested)
            return;

        LogPortAllocation();

        // When proxy is enabled, ensure proxy forwarders are bound
        if (options.UseProxy)
        {
            bool proxyReady;
            try
            {
                proxyReady = await proxy.WaitUntilReady(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (proxyReady is false)
            {
                logger.LogError("[FleetSupervisor] The Proxy Is Enabled But No Public Port Could Be Bound; Dedicated Workers Will Not Be Launched");
                return;
            }
        }

        // Initialize dedicated worker slots for the fleet
        InitializeWorkers();

        // Authenticate Server Manager with Master Server to clear stale registrations
        await RegisterServerManagerAsync(stoppingToken).ConfigureAwait(false);

        // Start background heartbeat loop to keep all active workers fresh in Master Server
        _ = Task.Run(() => HeartbeatLoopAsync(stoppingToken), stoppingToken);

        logger.LogInformation(
            "[FleetSupervisor] Initialized {Count} dedicated worker slot(s) (Warm Target: {WarmTarget}, Ports: {Start}-{End}, Ping: {PingPort}, AutoStart: {AutoStart}). Entering Reconcile Loop.",
            _workers.Count, EffectiveWarmInstancesTarget, Ports.LocalGameStart, Ports.LocalGameEnd, Ports.PingPort, options.AutoStart);

        // Event-driven reconcile loop: each pass reconciles per-worker state and waits for process exits or control plane signals
        while (stoppingToken.IsCancellationRequested is false)
        {
            await Reconcile(stoppingToken).ConfigureAwait(false);
            ReconcileAdaptivePriority();

            TimeSpan wakeTimeout = desiredRunning && ActiveWorkerCount < options.Instances
                ? RestartBackoff
                : TimeSpan.FromSeconds(10);

            try
            {
                await reconcileSignal.WaitAsync(wakeTimeout, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (desiredRunning && IsRunning)
            {
                JobObjectSandboxMetrics? metrics = sandbox.GetSandboxMetrics();
                int activeProcesses = metrics?.ActiveProcesses > 0 ? (int)metrics.ActiveProcesses : ActiveWorkerCount;
                long peakMemMb = (long)((metrics?.PeakJobMemoryBytes ?? 0) / (1024 * 1024));
                logger.LogInformation(
                    "[COMPEL Fleet Monitor] Active Workers: {ActiveWorkers}/{Total} (Running) | Sandbox Processes: {ActiveProcs} | Peak Memory: {PeakMem} MB | Ports: {GameStart}-{GameEnd} (Game), {Ping} (Ping) | Health: Healthy",
                    ActiveWorkerCount, options.Instances, activeProcesses, peakMemMb, Ports.LocalGameStart, Ports.LocalGameEnd, Ports.PingPort);
            }
        }

        await StopAllWorkersAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public string ResolveFleetTierPrefix()
    {
        if (options.QueueArcadeMode)
        {
            return "Q";
        }

        if (!string.IsNullOrWhiteSpace(options.FleetTier))
        {
            return options.FleetTier.Trim().ToUpperInvariant();
        }

        string? envTier = Environment.GetEnvironmentVariable("COMPEL_FLEET_TIER");
        if (!string.IsNullOrWhiteSpace(envTier))
        {
            return envTier.Trim().ToUpperInvariant();
        }

        bool isAgones = string.Equals(Environment.GetEnvironmentVariable("AGONES_ENABLED"), "true", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
        if (isAgones)
        {
            return "K";
        }

        if (options.PortRangeOffset is >= 250 and < 300)
        {
            return "Q";
        }

        if (options.PortRangeOffset is >= 200 and < 250 or 20)
        {
            return "K";
        }

        if (AddressResolver.IsRunningInContainer || options.PortRangeOffset is >= 100 and < 200 or 10)
        {
            return "D";
        }

        return "L";
    }

    public static int ResolveFleetTierBase(string tierPrefix)
    {
        return tierPrefix switch
        {
            "Q" => 4000,
            "K" => 3000,
            "D" => 2000,
            "L" => 1000,
            _ => 1000
        };
    }

    private int EffectiveWarmInstancesTarget =>
        options.QueueArcadeMode && options.Instances >= 2
            ? Math.Min(options.Instances, Math.Max(2, options.WarmInstancesTarget))
            : options.WarmInstancesTarget;

    internal void InitializeWorkersForTest() => InitializeWorkers();

    private void InitializeWorkers()
    {
        _workers.Clear();
        string tierPrefix = ResolveFleetTierPrefix();
        int tierBase = ResolveFleetTierBase(tierPrefix);
        int tierOffsetBase = tierPrefix switch
        {
            "Q" => 250,
            "K" => 200,
            "D" => 100,
            "L" => 0,
            _ => 0
        };
        int slotIndexInTier = Math.Max(0, options.PortRangeOffset - tierOffsetBase);
        int warmLimit = (EffectiveWarmInstancesTarget > 0 && EffectiveWarmInstancesTarget < options.Instances)
            ? EffectiveWarmInstancesTarget
            : options.Instances;

        for (int i = 0; i < options.Instances; i++)
        {
            int port = Ports.LocalGameStart + i;
            int voicePort = Ports.LocalVoiceStart + i;
            int publicGame = Ports.PublicGameStart + i;
            int publicVoice = Ports.PublicVoiceStart + i;
            int workerIndexInTier = slotIndexInTier + i + 1;
            string effectivePrefix = options.QueueArcadeMode &&
                                     !options.ServerNamePrefix.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase)
                ? $"QUEUE ARCADE {options.ServerNamePrefix}"
                : options.ServerNamePrefix;
            string serverName = $"{effectivePrefix} #{tierPrefix}{workerIndexInTier}";
            int instanceIndex = tierBase + workerIndexInTier;

            DedicatedServerWorker worker = new(
                instanceIndex: instanceIndex,
                port: port,
                voicePort: voicePort,
                publicGamePort: publicGame,
                publicVoicePort: publicVoice,
                serverName: serverName,
                workerSlotIndex: i);
            worker.DesiredRunning = options.AutoStart && (i < warmLimit);

            _workers[port] = worker;
        }
    }

    private async Task Reconcile(CancellationToken stoppingToken)
    {
        await lifecycleGate.WaitAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            if (distribution.IsSynchronising)
                return;

            // Self-healing: if Server Manager failed initial authentication (e.g. Master Server was starting up), retry when reachable
            if (!_serverManagerAuthenticated && desiredRunning)
            {
                string endpoint = addressResolver.MasterServerHostAndPort;
                if (await AddressResolver.CheckMasterServerReachabilityAsync(endpoint, cancellationToken: stoppingToken).ConfigureAwait(false))
                {
                    await RegisterServerManagerAsync(stoppingToken).ConfigureAwait(false);
                }
            }

            // Dynamic Warm Pool: ensure at least WarmInstancesTarget workers are warm and ready for match assignment
            if (desiredRunning && EffectiveWarmInstancesTarget > 0 && EffectiveWarmInstancesTarget < options.Instances)
            {
                int warmOrStartingCount = _workers.Values.Count(w =>
                    w.IsRunning && !w.IsDraining && (w.CurrentState.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
                                                     w.CurrentState.Equals("Starting", StringComparison.OrdinalIgnoreCase)));
                int shortfall = EffectiveWarmInstancesTarget - warmOrStartingCount;
                if (shortfall > 0)
                {
                    foreach (DedicatedServerWorker standbyWorker in _workers.Values.OrderBy(w => w.InstanceIndex).Where(w => !w.IsRunning && !w.DesiredRunning))
                    {
                        if (shortfall <= 0) break;
                        logger.LogInformation(
                            "[FleetSupervisor] Dynamic Warm Pool: Waking standby instance #{Instance} on Port {Port} to maintain WarmInstancesTarget ({Warm}/{Target}).",
                            standbyWorker.InstanceIndex, standbyWorker.Port, warmOrStartingCount, EffectiveWarmInstancesTarget);
                        standbyWorker.DesiredRunning = true;
                        shortfall--;
                    }
                }
            }

            foreach (DedicatedServerWorker worker in _workers.Values.OrderBy(w => w.InstanceIndex))
            {
                if (stoppingToken.IsCancellationRequested) break;

                if (desiredRunning && worker.DesiredRunning && !worker.IsRunning)
                {
                    long elapsed = Environment.TickCount64 - worker.LastAttemptTicks;
                    if (worker.LastAttemptTicks != 0 && elapsed < RestartBackoff.TotalMilliseconds)
                    {
                        continue;
                    }

                    worker.LastAttemptTicks = Environment.TickCount64;

                    try
                    {
                        LaunchWorkerProcess(worker);
                        await RegisterWorkerSessionAsync(worker, stoppingToken).ConfigureAwait(false);
                        // Stagger worker launches slightly to prevent disk queue contention
                        await Task.Delay(250, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[FleetSupervisor] Failed To Launch Dedicated Server Worker On Port {Port}", worker.Port);
                    }
                }
                else if (desiredRunning && worker.DesiredRunning && worker.IsRunning && string.IsNullOrEmpty(worker.SessionCookie))
                {
                    // Self-healing: worker launched in offline/standby mode; retry registration once Master Server becomes reachable
                    string endpoint = addressResolver.MasterServerHostAndPort;
                    if (await AddressResolver.CheckMasterServerReachabilityAsync(endpoint, cancellationToken: stoppingToken).ConfigureAwait(false))
                    {
                        long elapsed = Environment.TickCount64 - worker.LastAttemptTicks;
                        if (worker.LastAttemptTicks == 0 || elapsed >= RestartBackoff.TotalMilliseconds)
                        {
                            worker.LastAttemptTicks = Environment.TickCount64;
                            logger.LogInformation("[FleetSupervisor] Master Server reachable. Registering unauthenticated Dedicated Worker instance {Instance} on Port {Port}...",
                                worker.InstanceIndex, worker.Port);
                            await RegisterWorkerSessionAsync(worker, stoppingToken).ConfigureAwait(false);
                            if ( string.IsNullOrEmpty(worker.SessionCookie))
                            {
                                logger.LogWarning("[FleetSupervisor] Worker on Port {Port} still unauthenticated after probe. Restarting worker process for clean re-handshake...", worker.Port);
                                if (Environment.GetEnvironmentVariable("KONGOR_E2E_HARNESS") != "1") StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));
                                if (Environment.GetEnvironmentVariable("KONGOR_E2E_HARNESS") != "1") worker.DesiredRunning = true;
                            }
                        }
                    }
                }
                else if ((!desiredRunning || !worker.DesiredRunning) && worker.IsRunning)
                {
                    logger.LogInformation("[FleetSupervisor] Stopping Dedicated Server Worker On Port {Port}...", worker.Port);
                    await DeregisterWorkerSessionAsync(worker, stoppingToken).ConfigureAwait(false);
                    StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[FleetSupervisor] Failed To Reconcile Dedicated Server Fleet State");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void LaunchWorkerProcess(DedicatedServerWorker worker)
    {
        string executable = distribution.ManagerExecutablePath;

        if (File.Exists(executable) is false)
            throw new FileNotFoundException($@"Dedicated Server Executable Was Not Found At ""{executable}""");

        if (!ValidateDedicatedServerBinary(executable, out string? binaryValidationError))
        {
            logger.LogError("[FleetSupervisor] {ValidationError}", binaryValidationError);
            throw new InvalidOperationException(binaryValidationError);
        }

        EnsureExecutable(executable);

        string address = ServerAddress ??
                         throw new InvalidOperationException("The Server Address Has Not Been Resolved");

        // Clean up previous dead worker instance if any, and start with a clean log buffer
        StopAndUnassignWorker(worker, TimeSpan.FromMilliseconds(200), addLogLine: false);
        worker.ClearLogs();
        worker.LobbyOpenedAtUtc = null;
        worker.MatchId = 0;
        worker.MatchName = null;
        worker.DesiredRunning = true;

        try
        {
            Directory.CreateDirectory(artefacts.ArtefactsDirectory);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could Not Pre-Create The Artefacts Directory {Directory}",
                artefacts.ArtefactsDirectory);
        }

        string masterServerEndpoint = addressResolver.MasterServerHostAndPort;
        bool masterServerReachable = AddressResolver.CheckMasterServerReachability(masterServerEndpoint);

        if (!masterServerReachable)
        {
            logger.LogWarning(
                "[FleetSupervisor] Master Server at {Endpoint} is temporarily unreachable. Launching Dedicated Worker on port {Port} without Master Server.",
                masterServerEndpoint,
                worker.Port);
        }

        // Child workers must have the reachable master server endpoint configured for in-engine CURL calls
        // (such as start_game and auth) to prevent 118-second blocking frame freezes to 0.0.0.0:80.
        bool hasConfiguredCredentials = !string.IsNullOrWhiteSpace(masterServerEndpoint) &&
                                        !string.IsNullOrWhiteSpace(options.UserName);
        if (!masterServerReachable && hasConfiguredCredentials)
        {
            logger.LogInformation(
                "[FleetSupervisor] Master Server at {Endpoint} is still warming up during port {Port} launch; injecting svr_login/svr_password with bounded 2s/4s K2 timeouts so worker self-authenticates on its next 15s heartbeat.",
                masterServerEndpoint,
                worker.Port);
        }

        bool enableMasterServer = masterServerReachable || hasConfiguredCredentials;

        string[] arguments = DedicatedWorkerArguments.Build(
            worker,
            options,
            Ports,
            address,
            masterServerEndpoint,
            distribution.InstallationDirectory,
            enableMasterServer: enableMasterServer);

        ProcessStartInfo startInfo = new(executable)
        {
            WorkingDirectory = distribution.InstallationDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            List<string> windowsArguments = new(arguments.Length);
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];
                bool isExecutePayload = i > 0 && arguments[i - 1] == "-execute";
                if (isExecutePayload)
                {
                    string sanitized = argument.Trim('"').Replace("\"", string.Empty);
                    windowsArguments.Add($"\"{sanitized}\"");
                }
                else if (argument.Contains(' ') || (argument.StartsWith('"') && argument.EndsWith('"')))
                {
                    string quoted = argument.StartsWith('"') && argument.EndsWith('"') && argument.Length >= 2
                        ? argument
                        : $"\"{argument}\"";
                    windowsArguments.Add(quoted);
                }
                else
                {
                    windowsArguments.Add(argument);
                }
            }

            startInfo.Arguments = string.Join(' ', windowsArguments);
            startInfo.EnvironmentVariables["USERPROFILE"] = artefacts.ProfileDirectory;
            string portStr = worker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["COMPEL_PORT"] = portStr;
            startInfo.EnvironmentVariables["KONGOR_BOOT_SENTINEL_PATH"] = $"userdata/logs/native_guard_boot_{portStr}.sentinel";
            (bool enforceChecksumsWin, string checksumEnvWin, string checksumReasonWin) =
                DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(options);
            startInfo.EnvironmentVariables["KONGOR_EXAMINE_ARCHIVE_CHECKSUMS"] = checksumEnvWin;
            logger.LogInformation(
                "[FleetSupervisor] [ExamineChecksums] Windows worker Port {Port}: KONGOR_EXAMINE_ARCHIVE_CHECKSUMS={EnvVal} (Enforce={Enforce}, {Reason}).",
                worker.Port, checksumEnvWin, enforceChecksumsWin, checksumReasonWin);
            if (options.QueueArcadeMode)
            {
                startInfo.EnvironmentVariables["KONGOR_QUEUE_ARCADE"] = "1";
                logger.LogInformation(
                    "[FleetSupervisor] [QueueArcade] Armed KONGOR_QUEUE_ARCADE=1 on Windows worker Port {Port} (RoundDuration={DurationSec}s).",
                    worker.Port, options.QueueArcadeRoundDurationSeconds);
            }
        }
        else
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];
                // K2 Linux CSystem::Init concatenates argv[1..argc-1] with spaces, and CHost::Init (FUN_1808b9990)
                // strictly requires the payload following -execute to start and end with double quotes (ASCII 0x22).
                // If quotes are stripped, K2 fails the 0x22 check and skips -execute, causing instances to collide
                // on the default port 11235.
                bool isExecutePayload = i > 0 && arguments[i - 1] == "-execute";
                if (isExecutePayload || argument.Contains(' ') || (argument.StartsWith('"') && argument.EndsWith('"')))
                {
                    string quoted = argument.StartsWith('"') && argument.EndsWith('"') && argument.Length >= 2
                        ? argument
                        : $"\"{argument}\"";
                    if (isExecutePayload)
                    {
                        string sanitized = argument.Trim('"').Replace("\"", string.Empty);
                        quoted = $"\"{sanitized}\"";
                    }

                    startInfo.ArgumentList.Add(quoted);
                }
                else
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            startInfo.EnvironmentVariables["HOME"] = artefacts.ProfileDirectory;
            startInfo.EnvironmentVariables["COMPEL_PORT"] = worker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            (bool enforceChecksumsLinux, string checksumEnvLinux, string checksumReasonLinux) =
                DedicatedWorkerArguments.ResolveExamineChecksumsPolicy(options);
            startInfo.EnvironmentVariables["KONGOR_EXAMINE_ARCHIVE_CHECKSUMS"] = checksumEnvLinux;
            logger.LogInformation(
                "[FleetSupervisor] [ExamineChecksums] Linux worker Port {Port}: KONGOR_EXAMINE_ARCHIVE_CHECKSUMS={EnvVal} (Enforce={Enforce}, {Reason}).",
                worker.Port, checksumEnvLinux, enforceChecksumsLinux, checksumReasonLinux);
            if (options.QueueArcadeMode)
            {
                startInfo.EnvironmentVariables["KONGOR_QUEUE_ARCADE"] = "1";
                logger.LogInformation(
                    "[FleetSupervisor] [QueueArcade] Armed KONGOR_QUEUE_ARCADE=1 on Linux worker Port {Port} (RoundDuration={DurationSec}s).",
                    worker.Port, options.QueueArcadeRoundDurationSeconds);
            }

            if (!IsDirectoryWritable("/opt/hon/config"))
            {
                logger.LogWarning("[Standalone] Linux dedicated server requires writable /opt/hon/config. Run: sudo mkdir -p /opt/hon/config && sudo chown \"$USER:$USER\" /opt/hon/config");
            }

            List<string> preloadLibs = [];

            string shimPath = Path.Combine(distribution.InstallationDirectory, "compatibility", "libhon-ping-shim.so");
            if (!File.Exists(shimPath))
            {
                shimPath = Path.Combine(distribution.InstallationDirectory, "..", "compatibility", "libhon-ping-shim.so");
            }
            if (!File.Exists(shimPath))
            {
                shimPath = Path.Combine(distribution.InstallationDirectory, "compatibility", "libhon-rng-forksafe.so");
            }
            if (File.Exists(shimPath))
            {
                string formattedShim = FormatRelativePreloadPath(distribution.InstallationDirectory, shimPath);
                preloadLibs.Add(formattedShim);
                logger.LogInformation("[FleetSupervisor] Enabled HoN ping shim via LD_PRELOAD: {ShimPath}", formattedShim);
            }

            if (sandbox.Options.EnableNativeGuardInjection)
            {
                if (IsLinuxNativeGuardDisabledByEnvironment())
                {
                    startInfo.EnvironmentVariables["KONGOR_DISABLE_LINUX_NATIVE_GUARD"] = "1";
                    logger.LogInformation("[FleetSupervisor] Linux NativeGuard LD_PRELOAD bypassed via KONGOR_DISABLE_LINUX_NATIVE_GUARD=1 kill-switch.");
                }
                else
                {
                    string? guardSoPath = ResolveLinuxNativeGuardSoPath(distribution.InstallationDirectory);
                    if (!string.IsNullOrEmpty(guardSoPath))
                    {
                        string formattedGuard = FormatRelativePreloadPath(distribution.InstallationDirectory, guardSoPath);
                        preloadLibs.Add(formattedGuard);
                        logger.LogInformation("[FleetSupervisor] Enabled Linux NativeGuard via LD_PRELOAD: {GuardSoPath}", formattedGuard);
                    }
                }
            }

            if (preloadLibs.Count > 0)
            {
                string existingPreload = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(existingPreload))
                {
                    preloadLibs.Add(existingPreload);
                }
                startInfo.EnvironmentVariables["LD_PRELOAD"] = string.Join(':', preloadLibs);
            }
        }

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (sender, args) => OnWorkerExited(worker, process);

        if (process.Start() is false)
            throw new InvalidOperationException($"The Dedicated Game Server Worker Failed To Start On Port {worker.Port}");

        worker.Process = process;
        worker.StdinWriter = process.StandardInput;
        DateTimeOffset launchedUtc = _timeProvider.GetUtcNow();
        worker.StartTime = launchedUtc;
        worker.LaunchBootNonce = Guid.NewGuid().ToString("N");
        worker.LaunchProcessId = process.Id;
        try
        {
            worker.LaunchProcessStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            worker.LaunchProcessStartUtc = launchedUtc;
        }
        worker.CurrentState = "Starting";

        if (xdpFilter != null)
        {
            xdpFilter.RegisterPort((ushort)worker.Port, process.Id, isVoice: false, isProxy: false);
            xdpFilter.RegisterPort((ushort)worker.VoicePort, process.Id, isVoice: true, isProxy: false);
            if (options.UseProxy)
            {
                xdpFilter.RegisterPort((ushort)worker.PublicGamePort, process.Id, isVoice: false, isProxy: true);
                xdpFilter.RegisterPort((ushort)worker.PublicVoicePort, process.Id, isVoice: true, isProxy: true);
            }
        }

        // svr_name and net_enableWatermark are already supplied via startInfo.Arguments (-execute);
        // skip redundant redirected-stdin writes on SUBSYSTEM:WINDOWS hon_x64.exe.
        if (worker.StdinWriter != null && string.IsNullOrEmpty(startInfo.Arguments))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(worker.ServerName))
                {
                    worker.StdinWriter.WriteLine($"Set svr_name \"{worker.ServerName}\"");
                }
                worker.StdinWriter.WriteLine("Set net_enableWatermark false");
                worker.StdinWriter.Flush();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[FleetSupervisor] Failed to pipe initial configuration to Worker on Port {Port}", worker.Port);
            }
        }

        HostPriorityMode initialPriority = options.PriorityMode == HostPriorityMode.Adaptive
            ? (_lastAdaptiveClientRunning ? HostPriorityMode.Background : HostPriorityMode.HighPriority)
            : options.PriorityMode;

        // Calculate dedicated CPU core affinity mask, NUMA memory pinning, and dual-channel priority
        WorkerCpuPinning pinning = CpuTopologyResolver.GetDedicatedWorkerPinning(
            worker.WorkerSlotIndex,
            options.Instances,
            initialPriority,
            sandbox.Options.AffinityMode);

        CpuTopologyResolver.AssignProcessAffinity(process, worker.WorkerSlotIndex, options.Instances, initialPriority, sandbox.Options.AffinityMode, explicitMask: null, logger);

        // Assign to sandbox with dedicated port and NUMA core pinning
        sandbox.AssignProcess(process, worker.Port, pinning);

        // Runtime supervision: Inject Kongor.NativeGuard.dll if enabled
        if (OperatingSystem.IsWindows() && sandbox.Options.EnableNativeGuardInjection)
        {
            TryInjectNativeGuard(process, worker, distribution.InstallationDirectory);
        }

        if (worker.StagedDetourRecipe != null)
        {
            logger.LogInformation(
                "[FleetSupervisor] Dedicated Worker on Port {Port} launched with active rel32 detour recipe: {Strategy} ({Disassembly})",
                worker.Port, worker.StagedDetourRecipe.Strategy, worker.StagedDetourRecipe.DisassemblyInstruction);
        }

        // High-throughput zero-allocation PipeReader stream pipeline for stdout and stderr
        StartStreamPipelines(worker, process);

        // Start asynchronous Named Pipe server listening on \\.\pipe\hon-server-{port}
        worker.StartNamedPipeServer(msg => logger.LogInformation("{Message}", msg));

        // Start zero-copy Shared Memory telemetry poller directly from RAM
        worker.StartTelemetryPolling(
            msg => logger.LogInformation("{Message}", msg),
            (w, rec) =>
            {
                if (rec.ServerTick > 0 && (w.CurrentState == "Starting" || w.CurrentState == "Stopped"))
                {
                    w.CurrentState = "Available";
                }
            });

        logger.LogInformation(
            "[FleetSupervisor] Spawned Dedicated Game Server Worker (PID: {ProcessId}) On Port {Port} (Voice: {VoicePort}, Pipe: {PipeName}) [WorkingDir: {WorkingDir}]",
            process.Id, worker.Port, worker.VoicePort, worker.PipeName, startInfo.WorkingDirectory);

        if (process.HasExited)
        {
            logger.LogWarning("[FleetSupervisor] Dedicated server on port {Port} exited immediately after launch", worker.Port);
            reconcileSignal.Release();
        }
    }

    private void TryInjectNativeGuard(Process process, DedicatedServerWorker worker, string installationDir)
    {
        try
        {
            bool versionProxyPresent =
                (!string.IsNullOrWhiteSpace(installationDir) &&
                 (IsModuleLoadedAtPath(process, Path.Combine(installationDir, "version.dll")) ||
                  IsModuleLoadedAtPath(process, Path.Combine(installationDir, "windows-x64", "version.dll"))));

            if (versionProxyPresent)
            {
                logger.LogInformation(
                    "[FleetSupervisor] Unified version.dll proxy supervisor active in {InstallationDir} for Dedicated Worker PID {ProcessId} on port {Port}; skipping legacy Kongor.NativeGuard.dll injection.",
                    installationDir, process.Id, worker.Port);
                return;
            }

            string? guardPath = ResolveNativeGuardPath(installationDir);
            if (!string.IsNullOrEmpty(guardPath) && File.Exists(guardPath))
            {
                InjectionResult injectResult = NativeProcessInjectionService.InjectDll(process.Handle, guardPath, logger: logger);
                if (injectResult.Success)
                {
                    logger.LogInformation(
                        "[FleetSupervisor] Injected NativeGuard supervisor into Dedicated Worker PID {ProcessId} on port {Port} (HMODULE: 0x{Base:X8})",
                        process.Id, worker.Port, injectResult.ModuleBase);
                }
                else
                {
                    logger.LogWarning(
                        "[FleetSupervisor] NativeGuard injection skipped or failed for PID {ProcessId}: {Error}",
                        process.Id, injectResult.ErrorMessage);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[FleetSupervisor] Error during NativeGuard supervision check for PID {ProcessId}", process.Id);
        }
    }

    private static bool IsModuleLoadedAtPath(Process process, string candidatePath)
    {
        try
        {
            if (!File.Exists(candidatePath)) return false;
            string fullPath = Path.GetFullPath(candidatePath);
            return NativeProcessInjectionService.GetRemoteModuleHandleByFullPath(process.Handle, fullPath) != nint.Zero;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ResolveNativeGuardPath(string installationDir)
    {
        if (!string.IsNullOrWhiteSpace(installationDir))
        {
            string directVersion = Path.Combine(installationDir, "version.dll");
            if (File.Exists(directVersion)) return directVersion;

            string win64Version = Path.Combine(installationDir, "windows-x64", "version.dll");
            if (File.Exists(win64Version)) return win64Version;

            string direct = Path.Combine(installationDir, "Kongor.NativeGuard.dll");
            if (File.Exists(direct)) return direct;

            string win64 = Path.Combine(installationDir, "windows-x64", "Kongor.NativeGuard.dll");
            if (File.Exists(win64)) return win64;
        }

        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string honServerVersion = Path.Combine(current, "games", "hon server", "version.dll");
            if (File.Exists(honServerVersion)) return honServerVersion;

            string serverVersion = Path.Combine(current, "games", "server", "version.dll");
            if (File.Exists(serverVersion)) return serverVersion;

            string clientVersion = Path.Combine(current, "games", "client", "version.dll");
            if (File.Exists(clientVersion)) return clientVersion;

            string nativeBuildVersion = Path.Combine(current, "native", "build", "native_guard_build", "Release", "version.dll");
            if (File.Exists(nativeBuildVersion)) return nativeBuildVersion;

            string nativeBinVersion = Path.Combine(current, "artifacts", "native-binaries", "windows-x64", "version.dll");
            if (File.Exists(nativeBinVersion)) return nativeBinVersion;

            string candidate = Path.Combine(current, "artifacts", "native-binaries", "windows-x64", "Kongor.NativeGuard.dll");
            if (File.Exists(candidate)) return candidate;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return null;
    }

    public static bool IsLinuxNativeGuardDisabledByEnvironment(string? explicitValue = null)
    {
        string? raw = explicitValue ?? Environment.GetEnvironmentVariable("KONGOR_DISABLE_LINUX_NATIVE_GUARD");
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string trimmed = raw.Trim();
        return string.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatRelativePreloadPath(string workingDirectory, string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(libraryPath))
            return libraryPath;

        string fullWorkingDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        string fullLibPath = Path.GetFullPath(libraryPath);
        string rel = Path.GetRelativePath(fullWorkingDir, fullLibPath).Replace('\\', '/');

        if (!Path.IsPathRooted(rel) && !rel.Contains(' '))
        {
            return rel.StartsWith("./", StringComparison.Ordinal) ? rel : $"./{rel}";
        }

        return libraryPath;
    }

    public static bool IsDirectoryWritable(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return false;

        string probePath = Path.Combine(directoryPath, $".kongor_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchServerManagerSupervisor] IsDirectoryWritable probe failed for '{directoryPath}': {ex.Message}");
            return false;
        }
    }

    public static string? ResolveLinuxNativeGuardSoPath(string installationDir)
    {
        if (string.IsNullOrWhiteSpace(installationDir))
            return null;

        string[] candidates =
        [
            Path.Combine(installationDir, "linux-x64", "libk2_native_guard.so"),
            Path.Combine(installationDir, "linux-x64", "libkongor_native_guard.so"),
            Path.Combine(installationDir, "compatibility", "libk2_native_guard.so"),
            Path.Combine(installationDir, "compatibility", "libkongor_native_guard.so"),
            Path.Combine(installationDir, "..", "compatibility", "libk2_native_guard.so"),
            Path.Combine(installationDir, "..", "compatibility", "libkongor_native_guard.so"),
            Path.Combine(installationDir, "libk2_native_guard.so"),
            Path.Combine(installationDir, "libkongor_native_guard.so")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void StartStreamPipelines(DedicatedServerWorker worker, Process process)
    {
        worker.StreamCts?.Cancel();
        worker.StreamCts?.Dispose();
        worker.StreamCts = new CancellationTokenSource();
        CancellationToken streamCt = worker.StreamCts.Token;

        // Bounded lock-free ring buffer decoupling kernel pipe consumption from downstream logging & telemetry
        worker.StreamChannel = Channel.CreateBounded<DedicatedServerLogEntry>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

        worker.StreamConsumerTask = RunStreamConsumerPumpAsync(worker, worker.StreamChannel.Reader, streamCt);

        StreamPipeReaderOptions pipeOptions = new(
            bufferSize: 65536,
            minimumReadSize: 1,
            leaveOpen: true);

        PipeReader stdoutPipe = PipeReader.Create(process.StandardOutput.BaseStream, pipeOptions);
        PipeReader stderrPipe = PipeReader.Create(process.StandardError.BaseStream, pipeOptions);

        _ = ProcessStreamPipeAsync(worker, stdoutPipe, isError: false, streamCt);
        _ = ProcessStreamPipeAsync(worker, stderrPipe, isError: true, streamCt);

        // Real-time disk log file tailer for Windows and K2 engine console/slave logs.
        // On Linux/Docker, stdout is already captured via ProcessStreamPipeAsync, preventing file contention.
        if (OperatingSystem.IsWindows())
        {
            _ = StartWorkerLogTailerAsync(worker, process, streamCt);
        }
    }

    private async Task ProcessStreamPipeAsync(
        DedicatedServerWorker worker,
        PipeReader reader,
        bool isError,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                {
                    if (line.IsEmpty) continue;
                    string raw = Encoding.UTF8.GetString(line);
                    foreach (string text in CompelStreamSanitizer.SanitizeLines(raw))
                    {
                        DedicatedServerLogEntry entry = new(worker.Port, text, isError);
                        if (worker.StreamChannel != null && !worker.StreamChannel.Writer.TryWrite(entry))
                        {
                            CompelMetrics.StreamLinesDropped.Add(1);
                        }
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled)
                {
                    if (!buffer.IsEmpty)
                    {
                        string raw = Encoding.UTF8.GetString(buffer);
                        foreach (string text in CompelStreamSanitizer.SanitizeLines(raw))
                        {
                            DedicatedServerLogEntry entry = new(worker.Port, text, isError);
                            worker.StreamChannel?.Writer.TryWrite(entry);
                        }
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "PipeReader stream processing canceled for worker on port {Port} {StreamType}",
                worker.Port, isError ? "stderr" : "stdout");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "PipeReader stream processing ended for worker on port {Port} {StreamType}",
                worker.Port, isError ? "stderr" : "stdout");
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task StartWorkerLogTailerAsync(
        DedicatedServerWorker worker,
        Process process,
        CancellationToken cancellationToken)
    {
        string? currentlyTrackedPath = null;
        DateTime? currentlyTrackedLastWriteUtc = null;
        FileStream? fileStream = null;
        StreamReader? streamReader = null;

        try
        {
            // Give worker process a moment to initialize and create its log files
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested && worker.IsRunning && !process.HasExited)
            {
                // Check if we previously attached to a stale log file while waiting for the worker's fresh log to be created
                if (fileStream != null && currentlyTrackedLastWriteUtc.HasValue && worker.StartTime.HasValue &&
                    currentlyTrackedLastWriteUtc.Value < worker.StartTime.Value.UtcDateTime - TimeSpan.FromSeconds(5))
                {
                    string? freshCandidate = ResolveActiveLogFileForWorker(worker);
                    if (!string.IsNullOrEmpty(freshCandidate) &&
                        !string.Equals(freshCandidate, currentlyTrackedPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(freshCandidate) &&
                        File.GetLastWriteTimeUtc(freshCandidate) >= worker.StartTime.Value.UtcDateTime - TimeSpan.FromSeconds(5))
                    {
                        logger.LogInformation("[FleetSupervisor] Switching log tailer for port {Port} from stale '{OldFile}' to fresh '{NewFile}'",
                            worker.Port, currentlyTrackedPath, freshCandidate);
                        streamReader?.Dispose();
                        fileStream?.Dispose();
                        streamReader = null;
                        fileStream = null;
                        currentlyTrackedPath = null;
                        currentlyTrackedLastWriteUtc = null;
                    }
                }

                // 1. Resolve candidate log file if not currently tracking or if file was removed
                if (fileStream == null || !File.Exists(currentlyTrackedPath))
                {
                    streamReader?.Dispose();
                    fileStream?.Dispose();
                    streamReader = null;
                    fileStream = null;
                    currentlyTrackedPath = null;
                    currentlyTrackedLastWriteUtc = null;

                    string? bestCandidate = ResolveActiveLogFileForWorker(worker);
                    if (!string.IsNullOrEmpty(bestCandidate) && File.Exists(bestCandidate))
                    {
                        try
                        {
                            fileStream = new FileStream(
                                bestCandidate,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);

                            // Auto-detect BOM (UTF-16LE vs UTF-8)
                            Encoding detectedEnc = DetectStreamEncoding(fileStream);
                            streamReader = new StreamReader(fileStream, detectedEnc, detectEncodingFromByteOrderMarks: true);
                            currentlyTrackedPath = bestCandidate;
                            currentlyTrackedLastWriteUtc = File.GetLastWriteTimeUtc(bestCandidate);

                            // If file was created or last written before this worker process spawned, seek to end
                            // so recycled workers never replay a previous PID's _Mxxxx_console.clog from offset 0.
                            if (worker.StartTime.HasValue)
                            {
                                DateTime createdUtc = File.GetCreationTimeUtc(bestCandidate);
                                DateTime spawnUtc = worker.StartTime.Value.UtcDateTime;
                                if (currentlyTrackedLastWriteUtc.Value < spawnUtc || createdUtc < spawnUtc - TimeSpan.FromSeconds(1))
                                {
                                    fileStream.Seek(0, SeekOrigin.End);
                                    streamReader.DiscardBufferedData();
                                }
                            }

                            logger.LogInformation("[FleetSupervisor] Attached live log tailer for worker port {Port} to '{LogFile}'",
                                worker.Port, bestCandidate);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "[FleetSupervisor] Failed opening log candidate '{Candidate}' for port {Port}",
                                bestCandidate, worker.Port);
                            fileStream = null;
                            streamReader = null;
                            currentlyTrackedLastWriteUtc = null;
                        }
                    }
                }

                // 2. Read new lines if attached
                if (fileStream != null && streamReader != null)
                {
                    try
                    {
                        // Check for file truncation or recreation (Junie's Note 3)
                        if (fileStream.Length < fileStream.Position)
                        {
                            fileStream.Position = 0;
                            streamReader.DiscardBufferedData();
                        }

                        while (await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string rawLine)
                        {
                            foreach (string line in CompelStreamSanitizer.SanitizeLines(rawLine))
                            {
                                // Route EXCLUSIVELY through DispatchStreamLine (Junie's Note 1)
                                DispatchStreamLine(worker, new DedicatedServerLogEntry(worker.Port, line, false));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "[FleetSupervisor] Error reading from log file for worker {Port}", worker.Port);
                    }
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] Log tailer loop canceled for worker on port {Port}", worker.Port);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[FleetSupervisor] Log tailer loop ended for worker on port {Port}", worker.Port);
        }
        finally
        {
            streamReader?.Dispose();
            fileStream?.Dispose();
        }
    }

    private string? ResolveActiveLogFileForWorker(DedicatedServerWorker worker)
    {
        IReadOnlyList<string> candidateDirs = BuildCandidateLogDirectories(
            distribution?.InstallationDirectory,
            artefacts?.ArtefactsDirectory);

        List<FileInfo> candidateFiles = [];
        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                DirectoryInfo di = new(dir);
                candidateFiles.AddRange(di.GetFiles("*.log", SearchOption.TopDirectoryOnly));
                candidateFiles.AddRange(di.GetFiles("*.clog", SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex)
            {
                logger.LogTrace(ex, "[FleetSupervisor] Failed enumerating log files in '{Directory}'.", dir);
            }
        }

        FileInfo? prioritized = candidateFiles
            .Where(f => !f.Name.Equals("debug.log", StringComparison.OrdinalIgnoreCase) &&
                        !f.Name.EndsWith("_d3d9.log", StringComparison.OrdinalIgnoreCase) &&
                        CalculateFileScore(worker, f) > 0)
            .OrderByDescending(f => CalculateFileScore(worker, f))
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        return prioritized?.FullName;
    }

    internal static IReadOnlyList<string> BuildCandidateLogDirectories(string? installationDirectory, string? artefactsDirectory)
    {
        List<string> candidateDirs = [];

        static void AddServerAndGameUserdataDirs(List<string> dirs, string rootDir)
        {
            dirs.Add(rootDir);
            dirs.Add(Path.Combine(rootDir, "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "game", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "server", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "server", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "game", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "Heroes of Newerth", "server", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "Heroes of Newerth", "game", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdataKONGOR", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdataKONGOR", "extensions", "logs"));
            dirs.Add(Path.Combine(rootDir, "COMPEL", "logs"));
            dirs.Add(Path.Combine(rootDir, "game", "logs"));
            dirs.Add(Path.Combine(rootDir, "server", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "KONGOR", "configuration", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "Heroes of Newerth", "KONGOR", "configuration", "logs"));
            dirs.Add(Path.Combine(rootDir, "userdata", "logs"));
        }

        if (!string.IsNullOrWhiteSpace(installationDirectory) && Directory.Exists(installationDirectory))
        {
            AddServerAndGameUserdataDirs(candidateDirs, installationDirectory);

            string siblingServerDir = Path.GetFullPath(Path.Combine(installationDirectory, "..", "server"));
            if (!string.Equals(Path.GetFullPath(installationDirectory), siblingServerDir, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(siblingServerDir))
            {
                AddServerAndGameUserdataDirs(candidateDirs, siblingServerDir);
            }
        }

        if (!string.IsNullOrWhiteSpace(artefactsDirectory) && Directory.Exists(artefactsDirectory))
        {
            candidateDirs.Add(Path.Combine(artefactsDirectory, "server", "logs"));
            candidateDirs.Add(Path.Combine(artefactsDirectory, "game", "logs"));
            candidateDirs.Add(Path.Combine(artefactsDirectory, "game", "logs", "manager"));
        }

        if (OperatingSystem.IsWindows())
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
            {
                candidateDirs.Add(Path.Combine(docs, "Heroes of Newerth x64", "server", "logs"));
                candidateDirs.Add(Path.Combine(docs, "Heroes of Newerth x64", "game", "logs", "manager"));
                candidateDirs.Add(Path.Combine(docs, "Heroes of Newerth x64", "game", "logs"));
                candidateDirs.Add(Path.Combine(docs, "Heroes of Newerth x64", "KONGOR", "extensions", "logs"));
                candidateDirs.Add(Path.Combine(docs, "Heroes of Newerth x64", "KONGOR", "configuration", "logs"));
                candidateDirs.Add(Path.Combine(docs, "Heroes of Newerth", "KONGOR", "configuration", "logs"));
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "server", "logs"));
                candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "game", "logs", "manager"));
                candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "game", "logs"));
                candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "KONGOR", "extensions", "logs"));
                candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "KONGOR", "configuration", "logs"));
                candidateDirs.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth", "KONGOR", "configuration", "logs"));
            }
        }
        else
        {
            candidateDirs.Add("/opt/hon/shared/logs");
            candidateDirs.Add("/opt/hon/logs");
            candidateDirs.Add("/opt/hon/config/logs");
            candidateDirs.Add("/opt/hon/config/game/logs");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidateDirs.Add(Path.Combine(home, ".heroesofnewerth", "server", "logs"));
                candidateDirs.Add(Path.Combine(home, ".heroesofnewerth", "game", "logs"));
                candidateDirs.Add(Path.Combine(home, ".Heroes of Newerth", "server", "logs"));
                candidateDirs.Add(Path.Combine(home, ".Heroes of Newerth", "game", "logs"));
                candidateDirs.Add(Path.Combine(home, ".heroesofnewerth", "KONGOR", "configuration", "logs"));
                candidateDirs.Add(Path.Combine(home, ".Heroes of Newerth", "KONGOR", "configuration", "logs"));
            }
        }

        return candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static int CalculateFileScore(DedicatedServerWorker worker, FileInfo f)
    {
        int localOneBased = worker.WorkerSlotIndex + 1;
        int slaveOffset = 1000 + localOneBased;
        string localStr = localOneBased.ToString();
        string slaveOffsetStr = slaveOffset.ToString();
        string instanceStr = worker.InstanceIndex.ToString();
        string portStr = worker.Port.ToString();
        string todayPattern = TimeProvider.System.GetUtcNow().ToString("yyyy_MM_dd");

        // K2 formats m_iSlave == -1 (unassigned slave index prior to svr_slave) as "Slave-1_*.log".
        // Never allow Worker Slot 0 (Instance 1 / 1001) to mistake "Slave-1_" (-1) for Slave #1!
        if (worker.InstanceIndex > 0 &&
            (f.Name.StartsWith("Slave-1_", StringComparison.OrdinalIgnoreCase) ||
             f.Name.StartsWith("Slave-1.", StringComparison.OrdinalIgnoreCase)))
        {
            return -10000;
        }

        Match slaveMatch = Regex.Match(f.Name, @"^Slave-?(\d+)(?:[_.]|$)", RegexOptions.IgnoreCase);
        if (slaveMatch.Success && int.TryParse(slaveMatch.Groups[1].Value, out int parsedSlaveId))
        {
            if (parsedSlaveId != worker.InstanceIndex &&
                parsedSlaveId != localOneBased &&
                parsedSlaveId != slaveOffset)
            {
                return -10000;
            }
        }

        Match serverPortMatch = Regex.Match(f.Name, @"^server_(\d+)\.log$", RegexOptions.IgnoreCase);
        if (serverPortMatch.Success && int.TryParse(serverPortMatch.Groups[1].Value, out int parsedPort) && parsedPort != worker.Port)
        {
            return -10000;
        }

        int score = 0;
        if (f.Name.Equals($"server_{worker.Port}.log", StringComparison.OrdinalIgnoreCase)) score += 200;
        else if (f.Name.Contains(portStr, StringComparison.OrdinalIgnoreCase)) score += 180;
        else if (f.Name.StartsWith($"Slave{instanceStr}_", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave-{instanceStr}_", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave{slaveOffsetStr}_", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave-{slaveOffsetStr}_", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave{localStr}_", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave-{localStr}_", StringComparison.OrdinalIgnoreCase))
        {
            score += 160;
            if (f.Name.EndsWith("_console.clog", StringComparison.OrdinalIgnoreCase)) score += 15;
        }
        else if (f.Name.StartsWith($"Slave{instanceStr}", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave-{instanceStr}", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave{slaveOffsetStr}", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave-{slaveOffsetStr}", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave{localStr}", StringComparison.OrdinalIgnoreCase) ||
                 f.Name.StartsWith($"Slave-{localStr}", StringComparison.OrdinalIgnoreCase)) score += 150;
        else if (f.Name.Contains($"DONUT ARENA {localOneBased - 1}", StringComparison.OrdinalIgnoreCase)) score += 140;
        else
        {
            return -10000;
        }

        if (f.Name.Contains(todayPattern, StringComparison.OrdinalIgnoreCase)) score += 50;
        if (worker.StartTime.HasValue && f.LastWriteTimeUtc >= worker.StartTime.Value.UtcDateTime.AddSeconds(-30)) score += 20;

        return score;
    }

    internal static Encoding DetectStreamEncoding(Stream stream)
    {
        long initialPos = stream.CanSeek ? stream.Position : 0;
        try
        {
            byte[] header = new byte[4];
            int read = stream.Read(header, 0, header.Length);
            if (read >= 2 && header[0] == 0xFF && header[1] == 0xFE)
            {
                return Encoding.Unicode;
            }
            if (read >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            {
                return Encoding.UTF8;
            }
            if (read >= 4 && header[1] == 0x00 && header[3] == 0x00)
            {
                return Encoding.Unicode;
            }
            return Encoding.UTF8;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = initialPos;
            }
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequencePosition? posLf = buffer.PositionOf((byte)'\n');
        SequencePosition? posCr = buffer.PositionOf((byte)'\r');

        SequencePosition? target = null;
        if (posLf != null && posCr != null)
        {
            target = buffer.Slice(0, posLf.Value).Length < buffer.Slice(0, posCr.Value).Length ? posLf : posCr;
        }
        else
        {
            target = posLf ?? posCr;
        }

        if (target == null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, target.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, target.Value));
        return true;
    }

    private async Task RunStreamConsumerPumpAsync(
        DedicatedServerWorker worker,
        ChannelReader<DedicatedServerLogEntry> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out DedicatedServerLogEntry entry))
                {
                    DispatchStreamLine(worker, entry);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] Stream consumer pump canceled for worker on port {Port}", worker.Port);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FleetSupervisor] Stream consumer pump ended for worker on port {Port}", worker.Port);
        }
    }

    internal static bool TryParseNewSessionCookie(string? sanitizedLine, out string cookie)
    {
        cookie = string.Empty;
        if (string.IsNullOrWhiteSpace(sanitizedLine))
            return false;

        Match match = NewSessionCookieRegex.Match(sanitizedLine);
        if (!match.Success)
            return false;

        string candidate = match.Groups[1].Value;
        if (!(Guid.TryParseExact(candidate, "D", out _) || Guid.TryParseExact(candidate, "N", out _)))
            return false;

        cookie = candidate;
        return true;
    }

    internal static bool ShouldAllowHeartbeat401ReRegistration(DedicatedServerWorker worker) =>
        worker is { IsRunning: false };

    internal void DispatchStreamLine(DedicatedServerWorker worker, DedicatedServerLogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Text) && entry.Text.AsSpan().IndexOfAny(SanitizeTriggerChars) >= 0)
        {
            foreach (string cleanSegment in CompelStreamSanitizer.SanitizeLines(entry.Text))
            {
                DispatchStreamLine(worker, new DedicatedServerLogEntry(entry.Port, cleanSegment, entry.IsError));
            }
            return;
        }

        if (rlCollector is not null &&
            COMPEL.Services.Telemetry.RlTrajectoryCollectorService.TryHandleRawLine(rlCollector, worker.Port, entry.Text))
        {
            return;
        }

        if (entry.Text.Contains("[ALL]", StringComparison.OrdinalIgnoreCase) ||
            entry.Text.Contains("[TEAM]", StringComparison.OrdinalIgnoreCase))
        {
            if (worker.TryRecordRecentChatMessage(entry.Text))
            {
                worker.AddLogLine(entry.Text, false, "chat");
            }
        }
        else
        {
            worker.AddLogLine(entry.Text, entry.IsError, "server");
        }


        string text = entry.Text;
        if (TryParseNewSessionCookie(text, out string parsedCookie))
        {
            string? previousCookie = worker.SessionCookie;
            if (!string.Equals(previousCookie, parsedCookie, StringComparison.Ordinal))
            {
                worker.SessionCookie = parsedCookie;
                logger.LogInformation(
                    "[FleetSupervisor] Resynced live engine session cookie for Port {Port}: {OldCookie} -> {NewCookie}",
                    worker.Port,
                    SecurityMasking.MaskToken(previousCookie),
                    SecurityMasking.MaskToken(parsedCookie));
            }
        }

        if (FastCombatLogParser.TryParseMatchId(text.AsSpan(), out int mId) && mId > 0)
        {
            // Protect an active match's authoritative MatchID from carriage-return merged stdout lines (e.g. "match_id, INT: 141424]").
            bool hasLockedMatchId = worker.MatchId > 0 &&
                                    (worker.CurrentState.Equals("InMatch", StringComparison.OrdinalIgnoreCase) ||
                                     worker.CurrentState.Equals("PostMatchHold", StringComparison.OrdinalIgnoreCase) ||
                                     worker.CurrentState.Equals("Restarting", StringComparison.OrdinalIgnoreCase));
            if (!hasLockedMatchId)
            {
                bool isNewMatchAllocation = worker.MatchId != mId || !worker.LobbyOpenedAtUtc.HasValue;
                worker.MatchId = mId;
                if (isNewMatchAllocation)
                {
                    worker.LobbyOpenedAtUtc = _timeProvider.GetUtcNow();
                    logger.LogInformation(
                        "[FleetSupervisor] Lobby/match allocation detected on Port {Port} (MatchId: {MatchId}, LobbyOpenedAtUtc: {OpenedUtc:O}).",
                        worker.Port, mId, worker.LobbyOpenedAtUtc);
                }
                if (options.EnableReplayWal && !options.QueueArcadeMode && (worker.WalWriter == null || worker.WalWriter.MatchId != mId))
                {
                    worker.WalWriter = new COMPEL.Services.Telemetry.ReplayWalWriter(
                        mId,
                        worker.Port,
                        options.ReplayWalDirectory,
                        options.ReplayWalChunkSizeBytes,
                        logger);
                }
            }
            else if (worker.MatchId != mId)
            {
                logger.LogDebug(
                    "[FleetSupervisor] Ignoring MatchId change {OldMatchId} -> {ParsedMatchId} while worker on Port {Port} is in state '{State}'.",
                    worker.MatchId, mId, worker.Port, worker.CurrentState);
            }
        }

        if (text.Contains("c_state=2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LoadWorld: IsArrangedMatch()", StringComparison.OrdinalIgnoreCase))
        {
            if (!worker.LobbyOpenedAtUtc.HasValue)
            {
                worker.LobbyOpenedAtUtc = TimeProvider.System.GetUtcNow();
                logger.LogInformation(
                    "[FleetSupervisor] Lobby state (c_state=2 / LoadWorld) armed on Port {Port} at {OpenedUtc:O}.",
                    worker.Port, worker.LobbyOpenedAtUtc);
            }

            Match mnameMatch = Regex.Match(text, @"(?:^|&)mname=([^&\s\r\n]+)", RegexOptions.IgnoreCase);
            if (mnameMatch.Success)
            {
                string decodedName = Uri.UnescapeDataString(mnameMatch.Groups[1].Value.Replace('+', ' ')).Trim();
                if (!string.IsNullOrWhiteSpace(decodedName))
                {
                    worker.MatchName = decodedName;
                }
            }

            Match curlMapMatch = Regex.Match(text, @"(?:^|&)map=([^&\s\r\n]+)", RegexOptions.IgnoreCase);
            if (curlMapMatch.Success)
            {
                string decodedMap = Uri.UnescapeDataString(curlMapMatch.Groups[1].Value).Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(decodedMap))
                {
                    worker.CurrentMap = decodedMap;
                }
            }
        }

        if (text.Contains("flush_wal", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CMD_FLUSH_WAL", StringComparison.OrdinalIgnoreCase))
        {
            if (worker.WalWriter != null)
            {
                logger.LogInformation("[FleetSupervisor] In-engine WAL flush signal received for Port {Port} (Match {MatchId}).", worker.Port, worker.MatchId);
                _ = worker.WalWriter.FlushAsync();
            }
        }

        if (!text.Contains("[HON_PING_SHIM]", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("AddClient: client num:", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Finished sending state data to client #", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Voice: Adding client num ", StringComparison.OrdinalIgnoreCase)))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text, @"(?:AddClient:\s*client\s*num:|Finished\s+sending\s+state\s+data\s+to\s+client\s*#|Voice:\s*Adding\s+client\s+num)\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int clientNum))
            {
                worker.ActiveClientSlots[clientNum] = 1;
            }
            else
            {
                worker.ActiveClientSlots[worker.ActiveClientSlots.Count] = 1;
            }
            worker.ConnectedClients = worker.ActiveClientSlots.Count;
            long connectGeneration = Interlocked.Increment(ref worker.DisconnectGraceGeneration);
            logger.LogInformation("[FleetSupervisor] Client connected to Port {Port} (Count: {Count}, GraceGeneration: {Generation})", worker.Port, worker.ConnectedClients, connectGeneration);
        }
        else if (text.Contains("Client #", StringComparison.OrdinalIgnoreCase) &&
                 (text.Contains("disconnected:", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("has been removed", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("timed out during state", StringComparison.OrdinalIgnoreCase)))
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"Client\s*#(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int clientNum))
            {
                worker.ActiveClientSlots.TryRemove(clientNum, out _);
            }
            else if (!worker.ActiveClientSlots.IsEmpty)
            {
                foreach (KeyValuePair<int, byte> kvp in worker.ActiveClientSlots)
                {
                    if (worker.ActiveClientSlots.TryRemove(kvp.Key, out _))
                    {
                        break;
                    }
                }
            }

            worker.ConnectedClients = worker.ActiveClientSlots.Count;
            logger.LogInformation("[FleetSupervisor] Client disconnected from Port {Port} (Remaining: {Count})", worker.Port, worker.ConnectedClients);

            if (options.QueueArcadeMode && worker.ConnectedClients == 0 && worker.DesiredRunning && !worker.IsDraining)
            {
                logger.LogInformation(
                    "[FleetSupervisor] [QueueArcade] Human client count reached 0 on Port {Port} (MatchId: {MatchId}); keeping Queue Arcade mini-game alive for incoming queued players.",
                    worker.Port, worker.MatchId);
            }
            else if (worker.ConnectedClients == 0 &&
                     worker.DesiredRunning &&
                     !worker.IsDraining &&
                     !worker.CurrentState.Equals("PostMatchHold", StringComparison.OrdinalIgnoreCase) &&
                     !worker.CurrentState.Equals("Restarting", StringComparison.OrdinalIgnoreCase))
            {
                long currentMatchId = worker.MatchId;
                bool wasActiveMatch = worker.IsInMatch || currentMatchId > 0;
                long graceGeneration = Interlocked.Increment(ref worker.DisconnectGraceGeneration);
                int gracePeriodSeconds = Math.Max(0, wasActiveMatch
                    ? options.DisconnectGraceSecondsInMatch
                    : options.DisconnectGraceSecondsPreMatch);

                _ = Task.Run(async () =>
                {
                    logger.LogInformation(
                        "[FleetSupervisor] Client count hit 0 on Port {Port} (ActiveMatch: {WasActive}, MatchId: {MatchId}, GraceGeneration: {Generation}). Waiting {Sec}s grace period for reconnection...",
                        worker.Port, wasActiveMatch, currentMatchId, graceGeneration, gracePeriodSeconds);

                    await Task.Delay(TimeSpan.FromSeconds(gracePeriodSeconds), _timeProvider).ConfigureAwait(false);

                    long liveGeneration = Interlocked.Read(ref worker.DisconnectGraceGeneration);
                    if (liveGeneration != graceGeneration)
                    {
                        logger.LogInformation(
                            "[FleetSupervisor] Grace timer superseded on Port {Port} (TimerGeneration: {TimerGeneration}, LiveGeneration: {LiveGeneration}). A client reconnected or a newer zero-client event owns the recycle decision.",
                            worker.Port, graceGeneration, liveGeneration);
                        return;
                    }

                    if (worker.ConnectedClients > 0 ||
                        !worker.DesiredRunning ||
                        worker.IsDraining ||
                        worker.CurrentState.Equals("PostMatchHold", StringComparison.OrdinalIgnoreCase) ||
                        worker.CurrentState.Equals("Restarting", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation(
                            "[FleetSupervisor] Aborting worker auto-recycle on Port {Port}: client actively connected ({Count} clients), worker in state '{State}', or stopping after grace period.",
                            worker.Port, worker.ConnectedClients, worker.CurrentState);
                        return;
                    }

                    if (wasActiveMatch)
                    {
                        worker.MatchId = 0;
                        logger.LogInformation(
                            "[FleetSupervisor] Grace period expired. Match ended/abandoned on Port {Port} (MatchId: {MatchId}). Recycling worker process.",
                            worker.Port, currentMatchId);

                        if (currentMatchId > 0)
                        {
                            await NotifyMatchAbandonedAsync(worker, (int)currentMatchId, "AllClientsDisconnected").ConfigureAwait(false);
                        }
                        await RecycleWorkerAsync(worker).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogInformation(
                            "[FleetSupervisor] Pre-match client disconnect on Port {Port}. Recycling worker to ensure pristine listening socket.",
                            worker.Port);
                        worker.ActiveClientSlots.Clear();
                        worker.ConnectedClients = 0;
                        worker.MatchId = 0;
                        worker.MatchName = null;
                        worker.LobbyOpenedAtUtc = null;
                        await RecycleWorkerAsync(worker).ConfigureAwait(false);
                    }
                });
            }
        }

        if (text.Contains("Loading world: ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CWorld::StartLoad - ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Server Status: Map(", StringComparison.OrdinalIgnoreCase))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text, @"(?:Loading\s+world:\s*|CWorld::StartLoad\s*-\s*|Server\s+Status:\s*Map\()([^\s\)\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                worker.CurrentMap = match.Groups[1].Value.Trim().ToLowerInvariant();
            }
        }

        if (text.Contains("GS_GAME", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CGameServer::StartGame", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Game start", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("SGame:", StringComparison.OrdinalIgnoreCase) &&
             System.Text.RegularExpressions.Regex.IsMatch(text, @"\]\s*StartMatch\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
        {
            if (worker.CurrentState != "InMatch")
            {
                worker.CurrentState = "InMatch";
                TriggerMatchStarted(worker);
            }
        }
        else if ((worker.CurrentState == "InMatch" || worker.CurrentState == "Reserved") &&
                 (text.Contains("EndGame", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Match ended", StringComparison.OrdinalIgnoreCase)) &&
                 !text.Contains("Stat submission successful", StringComparison.OrdinalIgnoreCase) &&
                 !text.Contains("GS_SLEEP", StringComparison.OrdinalIgnoreCase) &&
                 !text.Contains("Returning to lobby", StringComparison.OrdinalIgnoreCase))
        {
            long expectedMatchId = worker.MatchId;
            logger.LogInformation(
                "[FleetSupervisor] Observed EndGame marker on Port {Port} (MatchId: {MatchId}); waiting for Stat submission successful / GS_SLEEP before recycle (60s fallback watchdog armed).",
                worker.Port, expectedMatchId);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60), _timeProvider).ConfigureAwait(false);
                if (!worker.IsRunning || !worker.DesiredRunning || worker.IsDraining)
                {
                    return;
                }

                if ((worker.CurrentState == "InMatch" || worker.CurrentState == "Reserved") &&
                    worker.MatchId == expectedMatchId)
                {
                    logger.LogWarning(
                        "[FleetSupervisor] 60s post-EndGame watchdog fired on Port {Port} (MatchId: {MatchId}); entering PostMatchHold and recycling worker.",
                        worker.Port, expectedMatchId);
                    await CompleteMatchAndMaybeRecycleAsync(worker, RecycleWorkerAsync).ConfigureAwait(false);
                }
            });
        }
        else if ((worker.CurrentState == "InMatch" || worker.CurrentState == "Reserved") &&
                 (text.Contains("GS_SLEEP", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Returning to lobby", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Stat submission successful", StringComparison.OrdinalIgnoreCase)))
        {
            CompleteMatchAndMaybeRecycleSync(worker);
        }
        else if ((worker.CurrentState == "Starting" || worker.CurrentState == "Stopped") &&
                 (text.Contains("listening", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Bound port", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Host_Init", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Requesting new session", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Long server frame", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("Server started", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("StateStringChanged", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("autoexec.cfg", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("init.cfg", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("K2 Engine start up", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("VoiceServer", StringComparison.OrdinalIgnoreCase) ||
                  text.StartsWith('>') ||
                  text.Contains("ping", StringComparison.OrdinalIgnoreCase)))
        {
            worker.CurrentState = "Available";
            if (!string.IsNullOrWhiteSpace(worker.ServerName))
            {
                _ = worker.WritePipeAsync($"Set svr_name \"{worker.ServerName}\"");
            }
        }

        if (entry.IsError)
        {
            logger.LogWarning("[DedicatedServer:{Port} STDERR] {Error}", entry.Port, entry.Text);
        }
        else
        {
            logger.LogInformation("[DedicatedServer:{Port}] {Output}", entry.Port, entry.Text);
        }

        // Null-conditional: the lightweight (options, logger) constructor used by unit tests does not wire a relay.
        if (text.Contains("NET_CHAT_GS_REJECT", StringComparison.OrdinalIgnoreCase) &&
            !worker.IsInMatch &&
            worker.MatchId == 0 &&
            worker.ConnectedClients == 0 &&
            worker.UptimeSeconds >= 30.0)
        {
            logger.LogWarning(
                "[FleetSupervisor] Worker on Port {Port} received NET_CHAT_GS_REJECT while idle (Uptime: {Uptime:F0}s). Recycling worker to obtain a fresh MasterServer session cookie.",
                worker.Port,
                worker.UptimeSeconds);
            worker.StartTime = TimeProvider.System.GetUtcNow();
            _ = Task.Run(async () =>
            {
                try
                {
                    await RecycleWorkerAsync(worker).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[FleetSupervisor] Failed to self-heal worker on Port {Port} after NET_CHAT_GS_REJECT", worker.Port);
                }
            });
        }

        telemetryRelay?.OnServerStdoutLine(entry.Port, entry.Text);
    }

    private void TriggerMatchEnded(DedicatedServerWorker worker, string? completedMap = null)
    {
        if (worker.MatchId > 0)
        {
            rlCollector?.NotifyMatchEnded(worker.Port, (int)worker.MatchId, completedMap ?? worker.CurrentMap);
        }

        if (worker.WalWriter != null)
        {
            _ = worker.WalWriter.CompleteAsync();
        }

        Func<DedicatedServerWorker, Task>? handler = MatchEnded;
        if (handler is not null)
        {
            _ = Task.Run(async () =>
            {
                foreach (Func<DedicatedServerWorker, Task> callback in handler.GetInvocationList().Cast<Func<DedicatedServerWorker, Task>>())
                {
                    try
                    {
                        await callback(worker).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[FleetSupervisor] Error handling MatchEnded event for worker on port {Port}", worker.Port);
                    }
                }
            });
        }
    }

    private void TriggerMatchStarted(DedicatedServerWorker worker)
    {
        Func<DedicatedServerWorker, Task>? handler = MatchStarted;
        if (handler is not null)
        {
            _ = Task.Run(async () =>
            {
                foreach (Func<DedicatedServerWorker, Task> callback in handler.GetInvocationList().Cast<Func<DedicatedServerWorker, Task>>())
                {
                    try
                    {
                        await callback(worker).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[FleetSupervisor] Error handling MatchStarted event for worker on port {Port}", worker.Port);
                    }
                }
            });
        }
    }

    private void TriggerWorkerCrashed(DedicatedServerWorker worker, int exitCode)
    {
        Func<DedicatedServerWorker, int, Task>? handler = WorkerCrashed;
        if (handler is not null)
        {
            _ = Task.Run(async () =>
            {
                foreach (Func<DedicatedServerWorker, int, Task> callback in handler.GetInvocationList().Cast<Func<DedicatedServerWorker, int, Task>>())
                {
                    try
                    {
                        await callback(worker, exitCode).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[FleetSupervisor] Error handling WorkerCrashed event for worker on port {Port}", worker.Port);
                    }
                }
            });
        }
    }

    internal static bool IsIntentionalWorkerExit(DedicatedServerWorker worker, int exitCode)
    {
        return !worker.DesiredRunning ||
               worker.IsDraining ||
               Volatile.Read(ref worker.IsRecycling) != 0 ||
               string.Equals(worker.CurrentState, "Restarting", StringComparison.OrdinalIgnoreCase) ||
               exitCode == 0;
    }

    private void StopAndUnassignWorker(
        DedicatedServerWorker worker,
        TimeSpan gracefulTimeout,
        bool addLogLine = true,
        bool setDesiredRunningFalse = false)
    {
        int trackedPid = worker.LaunchProcessId ?? 0;
        if (trackedPid <= 0 && worker.Process is { } activeProc)
        {
            try
            {
                trackedPid = activeProc.Id;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogTrace(ex, "[FleetSupervisor] Process on Port {Port} already disposed before StopAndUnassignWorker.", worker.Port);
            }
        }

        worker.Stop(gracefulTimeout, addLogLine, setDesiredRunningFalse);

        if (trackedPid > 0)
        {
            sandbox?.UnassignProcess(trackedPid);
        }
    }

    private void OnWorkerExited(DedicatedServerWorker worker, Process process)
    {
        int exitedPid = worker.LaunchProcessId ?? 0;
        if (exitedPid <= 0)
        {
            try
            {
                exitedPid = process.Id;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogTrace(ex, "[FleetSupervisor] Could not read PID for exited worker on Port {Port}.", worker.Port);
            }
        }

        if (exitedPid > 0)
        {
            sandbox?.UnassignProcess(exitedPid);
        }

        int exitCode = -1;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] Could not read ExitCode for worker on Port {Port}.", worker.Port);
        }

        string hint = exitCode switch
        {
            -536870911 or unchecked((int)0xE0000001) => " (SEH / Fatal: missing base/resources0.s2z or assets)",
            -1073741819 or unchecked((int)0xC0000005) => " (Access Violation / Segfault)",
            -1073741515 or unchecked((int)0xC0000135) => " (Missing DLL Dependency)",
            -1073741510 or unchecked((int)0xC000013A) => " (Control-C / Process Terminated)",
            _ => string.Empty
        };

        logger.LogWarning(
            "[FleetSupervisor] Dedicated Worker on Port {Port} (PID: {ProcessId}) exited with code {ExitCode}{Hint}",
            worker.Port, exitedPid, exitCode, hint);

        worker.StreamCts?.Cancel();
        bool intentionalExit = IsIntentionalWorkerExit(worker, exitCode);
        if (intentionalExit)
        {
            logger.LogInformation(
                "[FleetSupervisor] Dedicated Worker on Port {Port} (PID: {ProcessId}) completed intentional exit (State: {State}, Draining: {Draining}, Recycling: {Recycling}, DesiredRunning: {DesiredRunning}, ExitCode: {ExitCode}).",
                worker.Port, exitedPid, worker.CurrentState, worker.IsDraining, Volatile.Read(ref worker.IsRecycling) != 0, worker.DesiredRunning, exitCode);
        }
        else
        {
            worker.CrashCount++;
        }

        if (xdpFilter != null)
        {
            xdpFilter.UnregisterPort((ushort)worker.Port);
            xdpFilter.UnregisterPort((ushort)worker.VoicePort);
            if (options.UseProxy)
            {
                xdpFilter.UnregisterPort((ushort)worker.PublicGamePort);
                xdpFilter.UnregisterPort((ushort)worker.PublicVoicePort);
            }
        }

        // Autonomous Replay WAL Crash Salvage (SR-04)
        if (!intentionalExit && exitCode != 0)
        {
            TriggerWorkerCrashed(worker, exitCode);
        }

        // Autonomous Crash Symbolization (SR-03): Background non-blocking symbolization and issue filing
        if (!intentionalExit && options.EnableAutomatedCrashSymbolization && worker.DesiredRunning && exitCode != 0)
        {
            var logsSnapshot = worker.GetRecentLogs(100);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _crashSymbolizer.SymbolizeAndRecordCrashAsync(
                        worker,
                        exitCode,
                        logsSnapshot,
                        explicitDumpPath: null,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[FleetSupervisor] Automated crash symbolization encountered an error for Port {Port}.", worker.Port);
                }
            });
        }

        // Phase 8D: Check if Crash Resilience Fast Auto-Reboot can handle this crash
        if (!intentionalExit && crashResilience != null && worker.DesiredRunning)
        {
            var decision = crashResilience.EvaluateCrash(worker, exitCode, worker.GetRecentLogs(50));
            if (decision.CanFastReboot)
            {
                logger.LogInformation(
                    "[FleetSupervisor] Triggering Phase 8D Fast Auto-Reboot path for Port {Port} (PID: {ProcessId}, Reason: {Reason})",
                    worker.Port, exitedPid, decision.DiagnosticReason);

                // Milestone 4.5: Retain external client UDP sessions in proxy during worker resurrection
                proxy.RetainSessionsForWorker(worker.Port);

                worker.CurrentState = "Recovering";
                if (decision.SynthesizedDetour != null)
                {
                    worker.StagedDetourRecipe = decision.SynthesizedDetour;
                }

                _ = Task.Run(async () => await RecoverCrashedWorkerFastAsync(worker, decision).ConfigureAwait(false));
                return;
            }
        }

        _ = DeregisterWorkerSessionAsync(worker, CancellationToken.None);

        // Trigger reconcile loop only when worker should remain running and is not already draining/recycling
        if (worker.DesiredRunning && !worker.IsDraining && Volatile.Read(ref worker.IsRecycling) == 0)
        reconcileSignal.Release();
    }

    private async Task RecoverCrashedWorkerFastAsync(DedicatedServerWorker worker, CrashRecoveryDecision decision)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            // 1. Evict zombie sockets on worker.Port and worker.VoicePort
            bool portsAvailable = await ZombieSocketDetector.EvictZombieSocketsAsync(
                worker.Port,
                worker.VoicePort,
                maxWaitMs: 300,
                logger: logger).ConfigureAwait(false);

            if (!portsAvailable)
            {
                logger.LogWarning(
                    "[FleetSupervisor] Fast reboot socket eviction timed out for Port {Port}. Falling back to standard reconcile backoff.",
                    worker.Port);
                reconcileSignal.Release();
                return;
            }

            // 2. Launch new worker process immediately
            LaunchWorkerProcess(worker);

            // Milestone 4.5: Log recovered shared memory keyframe crash context if available
            if (decision.RecoveredKeyframe != null && worker.StdinWriter != null)
            {
                try
                {
                    // Diagnostic keyframe context logged (K2 dedicated server has no native svr_restoredTick cvar)
                    // Telemetry records crash tick/matchId for CompelTelemetryRelay and ReplayWalRecovery
                    worker.StdinWriter.Flush();
                    logger.LogInformation(
                        "[FleetSupervisor] Recovered crash diagnostic keyframe for Port {Port} at Tick {Tick} (Match: {MatchId})",
                        worker.Port, decision.RecoveredKeyframe.ServerTick, decision.RecoveredKeyframe.MatchId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[FleetSupervisor] Failed to pipe keyframe restoration commands to Port {Port}", worker.Port);
                }
            }

            // 3. Register session with Master Server
            await RegisterWorkerSessionAsync(worker, CancellationToken.None).ConfigureAwait(false);

            sw.Stop();
            decision.ElapsedMs = sw.ElapsedMilliseconds;

            logger.LogInformation(
                "[FleetSupervisor] [Phase 8D FastReboot] Successfully resurrected Dedicated Worker on Port {Port} in {Elapsed}ms (Target: <{Target}ms, Detour: {Detour})",
                worker.Port, sw.ElapsedMilliseconds, options.FastRebootTargetMs, decision.SynthesizedDetour?.Strategy ?? "None");

            telemetryRelay.OnServerStdoutLine(
                worker.Port,
                $"[COMPEL:Resilience] Auto-reboot completed in {sw.ElapsedMilliseconds}ms (Detour: {decision.SynthesizedDetour?.Strategy ?? "None"})");
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex,
                "[FleetSupervisor] Fast reboot failed for Dedicated Worker on Port {Port} after {Elapsed}ms. Falling back to reconcile loop.",
                worker.Port, sw.ElapsedMilliseconds);
            reconcileSignal.Release();
        }
        finally
        {
            // Milestone 4.5: Release session retention in proxy now that worker is restored
            proxy.ReleaseSessionsForWorker(worker.Port);
        }
    }

    private bool PreparePostMatchCompletion(DedicatedServerWorker worker)
    {
        string completedMap = worker.CurrentMap ?? string.Empty;
        TriggerMatchEnded(worker, completedMap);

        worker.MatchName = null;
        worker.LobbyOpenedAtUtc = null;
        worker.CurrentMap = "midwars";
        worker.MatchesHostedSinceRecycle++;
        worker.TotalLifetimeMatchesHosted++;
        CompelMetrics.MatchesHostedTotal.Add(1);

        bool isStandardCleanMap = completedMap.Equals("caldavar", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("caldavar_halloween", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("caldavar_reborn", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("caldavar_old", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("midwars", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("midwars_halloween", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("midwars_reborn", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("grimmscrossing", StringComparison.OrdinalIgnoreCase)
            || completedMap.Equals("riftwars", StringComparison.OrdinalIgnoreCase);
        bool forceCustomMapRecycle = !string.IsNullOrWhiteSpace(completedMap) && !isStandardCleanMap;
        int threshold = forceCustomMapRecycle ? 1 : options.RecycleEveryNMatches;

        bool shouldRecycle = worker.DesiredRunning && !worker.IsDraining && threshold > 0 && worker.MatchesHostedSinceRecycle >= threshold;
        if (shouldRecycle)
        {
            // Mark non-allocatable while waiting for connected players to exit the post-game victory/stats phase.
            worker.CurrentState = "PostMatchHold";
            logger.LogInformation(
                "[FleetSupervisor] Match completed on Port {Port} (Map: {Map}, ConnectedClients: {Clients}). Worker reached recycle threshold ({Count}/{Threshold}); entering PostMatchHold before recycle.",
                worker.Port, completedMap, worker.ConnectedClients, worker.MatchesHostedSinceRecycle, threshold);
            return true;
        }

        worker.CurrentState = "Available";
        worker.ActiveClientSlots.Clear();
        worker.ConnectedClients = 0;
        worker.MatchId = 0;

        if (worker.DesiredRunning && !worker.IsDraining)
        {
            int availableWarm = _workers.Values.Count(w => w.IsRunning && !w.IsDraining && w.CurrentState.Equals("Available", StringComparison.OrdinalIgnoreCase));
            if (EffectiveWarmInstancesTarget > 0 && EffectiveWarmInstancesTarget < options.Instances &&
                availableWarm > EffectiveWarmInstancesTarget && worker.WorkerSlotIndex >= EffectiveWarmInstancesTarget)
            {
                logger.LogInformation(
                    "[FleetSupervisor] Match completed on Port {Port}. Available warm workers ({Count}) exceeds WarmInstancesTarget ({Target}). Putting standby instance #{Instance} to sleep.",
                    worker.Port, availableWarm, EffectiveWarmInstancesTarget, worker.InstanceIndex);
                worker.DesiredRunning = false;
                StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));
                _ = DeregisterWorkerSessionAsync(worker, CancellationToken.None);
            }
            else
            {
                logger.LogInformation(
                    "[FleetSupervisor] Match completed on Port {Port}. Worker matches hosted: {Count} (Recycle threshold: {Threshold}). Worker remains online.",
                    worker.Port, worker.MatchesHostedSinceRecycle, threshold > 0 ? threshold.ToString() : "Disabled");
            }
        }

        return false;
    }

    private void CompleteMatchAndMaybeRecycleSync(DedicatedServerWorker worker)
    {
        if (PreparePostMatchCompletion(worker))
        {
            _ = Task.Run(() => HoldAndRecycleAfterMatchAsync(worker, RecycleWorkerAsync, CancellationToken.None));
        }
    }

    internal async Task CompleteMatchAndMaybeRecycleAsync(
        DedicatedServerWorker worker,
        Func<DedicatedServerWorker, Task> recycleAction,
        CancellationToken cancellationToken = default)
    {
        if (PreparePostMatchCompletion(worker))
        {
            await HoldAndRecycleAfterMatchAsync(worker, recycleAction, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task HoldAndRecycleAfterMatchAsync(
        DedicatedServerWorker worker,
        Func<DedicatedServerWorker, Task> recycleAction,
        CancellationToken cancellationToken = default)
    {
        int maxHoldSeconds = options.QueueArcadeMode ? 18 : 15;
        int minHoldSeconds = options.QueueArcadeMode ? 14 : 3;

        logger.LogInformation(
            "[FleetSupervisor] [PostMatchHold] Holding worker recycle on Port {Port} for up to {MaxHold}s (min {MinHold}s, QueueArcade={QueueArcade}, ConnectedClients={Clients}).",
            worker.Port, maxHoldSeconds, minHoldSeconds, options.QueueArcadeMode, worker.ConnectedClients);

        for (int sec = 0; sec < maxHoldSeconds; sec++)
        {
            if (!worker.IsRunning || !worker.DesiredRunning || worker.IsDraining || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);

            if ((sec + 1) >= minHoldSeconds && worker.ConnectedClients <= 0)
            {
                break;
            }
        }

        if (!worker.IsRunning || !worker.DesiredRunning || worker.IsDraining || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await recycleAction(worker).ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully stops, evicts zombie sockets, and resurrects a dedicated server worker process
    /// to reset internal K2 engine client state pointers (e.g. *(param_1 + 0x280)) and ensure
    /// pristine connection handling for subsequent match allocations.
    /// </summary>
    public async Task RecycleWorkerAsync(DedicatedServerWorker worker)
    {
        if (worker.IsDraining || !worker.DesiredRunning) return;

        if (Interlocked.CompareExchange(ref worker.IsRecycling, 1, 0) != 0)
        {
            return; // Already recycling
        }

        try
        {
            logger.LogInformation("[FleetSupervisor] Recycling Dedicated Worker on Port {Port} to ensure pristine connection slot", worker.Port);
            worker.CurrentState = "Restarting";
            worker.ActiveClientSlots.Clear();
            worker.ConnectedClients = 0;
            worker.MatchId = 0;
            worker.MatchName = null;
            worker.LobbyOpenedAtUtc = null;
            StopAndUnassignWorker(worker, TimeSpan.FromMilliseconds(500), addLogLine: true);
            await DeregisterWorkerSessionAsync(worker, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            await ZombieSocketDetector.EvictZombieSocketsAsync(worker.Port, worker.VoicePort, maxWaitMs: 300, logger: logger).ConfigureAwait(false);
            LaunchWorkerProcess(worker);
            await RegisterWorkerSessionAsync(worker, CancellationToken.None).ConfigureAwait(false);
            worker.MatchesHostedSinceRecycle = 0;
            CompelMetrics.WorkerRecyclesTotal.Add(1);
            logger.LogInformation("[FleetSupervisor] Dedicated Worker on Port {Port} successfully recycled and pristine", worker.Port);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[FleetSupervisor] Failed to recycle Dedicated Worker on Port {Port}", worker.Port);
        }
        finally
        {
            worker.IsRecycling = 0;
        }
    }

    /// <summary>
    /// Finds a worker by either its internal listening port or public forwarded proxy port.
    /// </summary>
    public DedicatedServerWorker? FindWorker(int port)
    {
        return _workers.TryGetValue(port, out DedicatedServerWorker? worker)
            ? worker
            : _workers.Values.FirstOrDefault(w => w.PublicGamePort == port || w.Port == port);
    }

    /// <summary>
    /// Executes a console command or cvar directly on a specific dedicated server port.
    /// Writes directly to the worker's standard input stream, adhering to the Dedicated
    /// Game Server Command Routing Rule. Truthfully reports failure if standard input
    /// is closed or broken, preventing circular named pipe loopback masking.
    /// </summary>
    public async Task<(bool Success, string Channel, string Message)> ExecuteCommandOnPortAsync(
        int port,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (false, "none", "Empty command specified.");

        DedicatedServerWorker? worker = FindWorker(port);
        if (worker == null || !worker.IsRunning)
            return (false, "none", $"Dedicated game server worker on port {port} is not running.");

        if (worker.CurrentState is "Starting" or "Stopped")
        {
            worker.CurrentState = "Available";
        }

        string trimmed = command.Trim();

        if (trimmed.Equals("ServerStatus", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            worker.AddLogLine($"> {trimmed}", isError: false, source: "operator");
            string statusLine = $"[COMPEL] Instance #{worker.InstanceIndex} (Port {worker.Port}) | PID: {worker.ProcessId?.ToString() ?? "N/A"} | State: {worker.CurrentState} | Memory: {worker.WorkingSetMb:F1} MB | Uptime: {worker.UptimeSeconds:F0}s";
            worker.AddLogLine(statusLine, isError: false, source: "server");
            logger.LogInformation("[DedicatedServer:{Port} STATUS] Handled status command: '{Status}'", port, statusLine);

            _ = worker.WriteCommandAsync("svr_name", cancellationToken);
            return (true, "supervisor", statusLine);
        }

        if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            worker.AddLogLine($"> {trimmed}", isError: false, source: "operator");
            string helpLine = "[COMPEL] Available commands: announce <message>, GiveGold [client|all] [amount], ServerStatus, ListPlayers, PlayerCount, AddFakePlayer 1, AddFakePlayer 2, svr_name, svr_port, svr_maxClients, echo <text>, quit";
            worker.AddLogLine(helpLine, isError: false, source: "server");
            return (true, "supervisor", helpLine);
        }

        if (trimmed.StartsWith("announce", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("say ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("broadcast ", StringComparison.OrdinalIgnoreCase))
        {
            string msg = string.Empty;
            if (trimmed.StartsWith("announce", StringComparison.OrdinalIgnoreCase))
            {
                msg = trimmed.Length > 8 ? trimmed[8..].Trim() : string.Empty;
            }
            else if (trimmed.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                msg = trimmed[4..].Trim();
            }
            else if (trimmed.StartsWith("broadcast ", StringComparison.OrdinalIgnoreCase))
            {
                msg = trimmed[10..].Trim();
            }

            worker.AddLogLine($"> {trimmed}", isError: false, source: "operator");

            if (string.IsNullOrWhiteSpace(msg))
            {
                string usage = "[COMPEL] Usage: announce <message to broadcast to all players>";
                worker.AddLogLine(usage, isError: false, source: "server");
                return (true, "supervisor", usage);
            }

            // Route via K2 ServerChat which broadcasts "Server Message: <msg>" to all players
            (bool announceSuccess, string announceChannel, string _) = await worker.WriteCommandAsync($"ServerChat {msg}", cancellationToken).ConfigureAwait(false);
            if (announceSuccess)
            {
                logger.LogInformation("[DedicatedServer:{Port} ANNOUNCE] Broadcasted: '{Message}'", port, msg);
                return (true, announceChannel, $"Broadcasted server message: '{msg}'");
            }
        }

        if (trimmed.Equals("flush_wal", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("WalFlush", StringComparison.OrdinalIgnoreCase))
        {
            worker.AddLogLine($"> {trimmed}", isError: false, source: "operator");
            if (worker.WalWriter != null)
            {
                await worker.WalWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                string msg = $"[WAL] Flushed match #{worker.WalWriter.MatchId} WAL buffer to disk (Sequence: {worker.WalWriter.CurrentBeacon.Sequence}, Flushed: {worker.WalWriter.CurrentBeacon.FlushedBytes} bytes).";
                worker.AddLogLine(msg, isError: false, source: "server");
                logger.LogInformation("[DedicatedServer:{Port} WAL] Manual flush executed: {Msg}", port, msg);
                return (true, "supervisor", msg);
            }
            else
            {
                string msg = "[WAL] No active WAL writer on this server (match may not be in progress).";
                worker.AddLogLine(msg, isError: false, source: "server");
                return (false, "supervisor", msg);
            }
        }

        if (trimmed.StartsWith("givegold", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("gold", StringComparison.OrdinalIgnoreCase))
        {
            worker.AddLogLine($"> {trimmed}", isError: false, source: "operator");

            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int goldAmount = 1000;
            bool targetAll = false;
            int? specificClient = null;

            if (parts.Length == 1)
            {
                // "GiveGold" -> default 1000 gold to all players
                targetAll = true;
            }
            else if (parts.Length == 2)
            {
                if (int.TryParse(parts[1], out int parsedAmount))
                {
                    goldAmount = parsedAmount;
                    targetAll = true;
                }
                else if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    targetAll = true;
                }
            }
            else if (parts.Length >= 3)
            {
                if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out int parsedAllAmount))
                {
                    goldAmount = parsedAllAmount;
                    targetAll = true;
                }
                else if (int.TryParse(parts[1], out int parsedClient) && int.TryParse(parts[2], out int parsedClientAmount))
                {
                    specificClient = parsedClient;
                    goldAmount = parsedClientAmount;
                }
            }

            if (targetAll)
            {
                for (int slot = 0; slot < 20; slot++)
                {
                    await worker.WriteCommandAsync($"GiveGold {slot} {goldAmount}", cancellationToken).ConfigureAwait(false);
                }
                string ack = $"[COMPEL] Dispatched {goldAmount} gold to all player slots.";
                worker.AddLogLine(ack, isError: false, source: "server");
                return (true, "supervisor", ack);
            }

            if (specificClient.HasValue)
            {
                (bool singleSuccess, string singleChannel, string _) = await worker.WriteCommandAsync($"GiveGold {specificClient.Value} {goldAmount}", cancellationToken).ConfigureAwait(false);
                if (singleSuccess)
                {
                    return (true, singleChannel, $"Dispatched {goldAmount} gold to player {specificClient.Value}.");
                }
            }
        }

        if (trimmed.StartsWith("svr_name ", StringComparison.OrdinalIgnoreCase))
        {
            string nameVal = trimmed[9..].Trim().Trim('"');
            trimmed = $"Set svr_name \"{nameVal}\"";
            worker.ServerName = nameVal;
            options.ServerNamePrefix = nameVal;
            _ = RegisterWorkerSessionAsync(worker, cancellationToken);
        }
        else if (trimmed.StartsWith("Set svr_name ", StringComparison.OrdinalIgnoreCase))
        {
            string nameVal = trimmed[13..].Trim().Trim('"');
            worker.ServerName = nameVal;
            options.ServerNamePrefix = nameVal;
            _ = RegisterWorkerSessionAsync(worker, cancellationToken);
        }

        (bool success, string cmdChannel, string cmdDetail) = await worker.WriteCommandAsync(trimmed, cancellationToken).ConfigureAwait(false);

        if (success)
        {
            worker.AddLogLine($"> {trimmed}", isError: false, source: "operator");
            logger.LogInformation("[DedicatedServer:{Port} {Channel}] Dispatched command: '{Command}' ({Detail})", port, cmdChannel.ToUpperInvariant(), trimmed, cmdDetail);

            if (trimmed.StartsWith("CreateGame ", StringComparison.OrdinalIgnoreCase))
            {
                worker.LobbyOpenedAtUtc = TimeProvider.System.GetUtcNow();
                System.Text.RegularExpressions.Match cgMapMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"\bmap:([^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (cgMapMatch.Success)
                {
                    worker.CurrentMap = cgMapMatch.Groups[1].Value.Trim().ToLowerInvariant();
                }
            }

            if (trimmed.StartsWith("ServerChat", StringComparison.OrdinalIgnoreCase))
            {
                string chatMsg = trimmed.Length > 10 ? trimmed[10..].Trim().Trim('"') : string.Empty;
                worker.AddLogLine($"[SERVER CHAT] Broadcasted to players: {chatMsg}", isError: false, source: "server");
            }
            else if (trimmed.StartsWith("AddFakePlayer", StringComparison.OrdinalIgnoreCase))
            {
                string team = trimmed.Length > 13 ? trimmed[13..].Trim() : "1";
                string teamName = team == "2" ? "Hellbourne" : "Legion";
                worker.AddLogLine($"[BOT] Added bot to {teamName} (Team {team}).", isError: false, source: "server");
            }
            else if (trimmed.StartsWith("RemoveFakePlayers", StringComparison.OrdinalIgnoreCase))
            {
                worker.AddLogLine("[BOT] Removed all bot fake players.", isError: false, source: "server");
            }
            return (true, cmdChannel, cmdDetail);
        }

        logger.LogError("[DedicatedServer:{Port}] Failed to dispatch command '{Command}': no active native pipe or standard input channel responded.",
            port, trimmed);
        return (false, cmdChannel, cmdDetail);
    }

    /// <summary>
    /// Explicitly flushes the active Replay Write-Ahead Log (WAL) for the specified worker port.
    /// Useful for in-engine developer overlay flush triggers and crash-isolation pre-checks.
    /// </summary>
    public async Task<bool> FlushWorkerWalAsync(int port, CancellationToken cancellationToken = default)
    {
        DedicatedServerWorker? worker = FindWorker(port);
        if (worker?.WalWriter != null)
        {
            await worker.WalWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[DedicatedServer:{Port} WAL] FlushWorkerWalAsync successfully flushed WAL buffer.", port);
            return true;
        }
        return false;
    }

    internal void RegisterWorkerForTesting(DedicatedServerWorker worker)
    {
        _workers[worker.Port] = worker;
    }

    /// <summary>
    /// Requests that all dedicated workers in the fleet be running.
    /// </summary>
    public void RequestStart()
    {
        desiredRunning = true;
        int warmLimit = (EffectiveWarmInstancesTarget > 0 && EffectiveWarmInstancesTarget < options.Instances)
            ? EffectiveWarmInstancesTarget
            : options.Instances;
        int index = 0;
        foreach (DedicatedServerWorker worker in _workers.Values.OrderBy(w => w.InstanceIndex))
        {
            worker.DesiredRunning = index < warmLimit;
            index++;
        }

        reconcileSignal.Release();
    }

    /// <summary>
    /// Requests that all dedicated workers in the fleet be stopped.
    /// </summary>
    public void RequestStop()
    {
        desiredRunning = false;
        foreach (DedicatedServerWorker worker in _workers.Values)
            worker.DesiredRunning = false;

        reconcileSignal.Release();
    }

    /// <summary>
    /// Requests that the entire dedicated server fleet be restarted.
    /// </summary>
    public async Task RequestRestart(CancellationToken cancellationToken)
    {
        desiredRunning = true;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (DedicatedServerWorker worker in _workers.Values)
            {
                await DeregisterWorkerSessionAsync(worker, cancellationToken).ConfigureAwait(false);
                StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));
                worker.DesiredRunning = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FleetSupervisor] Failed To Signal A Fleet Restart");
        }
        finally
        {
            lifecycleGate.Release();
        }

        reconcileSignal.Release();
    }

    /// <summary>
    /// Requests start of a single worker instance on a specific port.
    /// </summary>
    public async Task<bool> RequestWorkerStart(int port, CancellationToken cancellationToken)
    {
        DedicatedServerWorker? worker = FindWorker(port);
        if (worker == null)
            return false;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            worker.DesiredRunning = true;
            if (worker.IsRunning)
            {
                worker.CurrentState = "Available";
                worker.AddLogLine($"Dedicated game server on port {worker.Port} is already running and active.", false, "system");
            }
            else
            {
                worker.CurrentState = "Starting";
                worker.AddLogLine($"Server start requested on port {worker.Port}.", false, "system");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FleetSupervisor] Failed To Signal Start For Worker On Port {Port}", worker.Port);
        }
        finally
        {
            lifecycleGate.Release();
        }

        reconcileSignal.Release();
        return true;
    }

    /// <summary>
    /// Requests stop of a single worker instance on a specific port.
    /// </summary>
    public async Task<bool> RequestWorkerStop(int port, CancellationToken cancellationToken)
    {
        DedicatedServerWorker? worker = FindWorker(port);
        if (worker == null)
            return false;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeregisterWorkerSessionAsync(worker, cancellationToken).ConfigureAwait(false);
            worker.DesiredRunning = false;
            StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FleetSupervisor] Failed To Signal Stop For Worker On Port {Port}", worker.Port);
        }
        finally
        {
            lifecycleGate.Release();
        }

        reconcileSignal.Release();
        return true;
    }

    /// <summary>
    /// Requests restart of a single worker instance on a specific port without disrupting sibling matches.
    /// </summary>
    public async Task<bool> RequestWorkerRestart(int port, CancellationToken cancellationToken)
    {
        DedicatedServerWorker? worker = FindWorker(port);
        if (worker == null)
            return false;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeregisterWorkerSessionAsync(worker, cancellationToken).ConfigureAwait(false);
            StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));
            worker.DesiredRunning = true;
            worker.CurrentState = "Starting";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FleetSupervisor] Failed To Signal Restart For Worker On Port {Port}", port);
        }
        finally
        {
            lifecycleGate.Release();
        }

        reconcileSignal.Release();
        return true;
    }

    private async Task StopAllWorkersAsync(CancellationToken cancellationToken)
    {
        desiredRunning = false;

        foreach (DedicatedServerWorker worker in _workers.Values)
        {
            await DeregisterWorkerSessionAsync(worker, cancellationToken).ConfigureAwait(false);
            StopAndUnassignWorker(worker, TimeSpan.FromSeconds(2));

            if (xdpFilter != null)
            {
                xdpFilter.UnregisterPort((ushort)worker.Port);
                xdpFilter.UnregisterPort((ushort)worker.VoicePort);
                if (options.UseProxy)
                {
                    xdpFilter.UnregisterPort((ushort)worker.PublicGamePort);
                    xdpFilter.UnregisterPort((ushort)worker.PublicVoicePort);
                }
            }
        }
    }

    /// <summary>
    /// Validates on Windows that the target dedicated server directory contains genuine K2_SERVER / GAME_SERVER binaries
    /// rather than misplaced K2_CLIENT DLLs (which reject all external client connections with disconnect_unknown).
    /// </summary>
    public static bool ValidateDedicatedServerBinary(string serverExecutablePath, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(serverExecutablePath))
        {
            errorMessage = "Dedicated server executable path is empty. Fix: Configure Compel:InstallationDirectory to point to games/server.";
            return false;
        }

        string? serverDir = Path.GetDirectoryName(serverExecutablePath);
        if (string.IsNullOrWhiteSpace(serverDir))
        {
            return true;
        }

        string k2DllPath = Path.Combine(serverDir, "k2_x64.dll");
        if (File.Exists(k2DllPath))
        {
            try
            {
                using FileStream fs = new(k2DllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length >= 0xE0)
                {
                    Span<byte> header = stackalloc byte[0x400];
                    int read = fs.Read(header);
                    if (read >= 0x100 && header[0] == (byte)'M' && header[1] == (byte)'Z')
                    {
                        int peOffset = BitConverter.ToInt32(header.Slice(0x3C, 4));
                        if (peOffset > 0 && peOffset + 0x58 <= read &&
                            header[peOffset] == (byte)'P' && header[peOffset + 1] == (byte)'E')
                        {
                            uint sizeOfImage = BitConverter.ToUInt32(header.Slice(peOffset + 0x50, 4));
                            // K2_CLIENT k2_x64.dll has SizeOfImage 0x01394000 (19,811,840 bytes on disk);
                            // genuine K2_SERVER k2_x64.dll has SizeOfImage 0x01342000 (19,467,264 bytes on disk).
                            if (sizeOfImage == 0x01394000U)
                            {
                                errorMessage =
                                    $"Invalid dedicated server binary at '{k2DllPath}': detected K2_CLIENT build (PE SizeOfImage=0x01394000), which compiles out m_mapClients and rejects all external players with disconnect_unknown. " +
                                    "Fix: Restore genuine K2_SERVER binaries (k2_x64.dll SizeOfImage=0x01342000, game/game_x64.dll, game/game_shared_x64.dll) into games/server/.";
                                return false;
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Non-fatal if locked transiently during inspection; native process load will validate.
            }
        }

        string gameDllPath = Path.Combine(serverDir, "game", "game_x64.dll");
        if (File.Exists(gameDllPath))
        {
            long gameDllLength = new FileInfo(gameDllPath).Length;
            // K2_CLIENT game_x64.dll is 1,101,312 bytes; K2_SERVER game_x64.dll is 1,206,272 bytes.
            if (gameDllLength == 1_101_312L)
            {
                errorMessage =
                    $"Invalid dedicated server module at '{gameDllPath}': detected client game_x64.dll (1,101,312 bytes) instead of dedicated server game_x64.dll (1,206,272 bytes). " +
                    "Fix: Restore genuine K2_SERVER game/game_x64.dll and game/game_shared_x64.dll into games/server/game/.";
                return false;
            }
        }

        return true;
    }

    private void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            UnixFileMode currentMode = File.GetUnixFileMode(path);
            UnixFileMode executableMode = currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
                                          UnixFileMode.OtherExecute;

            if (executableMode != currentMode)
                File.SetUnixFileMode(path, executableMode);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could Not Set The Execute Bit On {Path}", path);
        }
    }

    private void KillOrphanedProcesses()
    {
        string executableName = Path.GetFileNameWithoutExtension(HeroesOfNewerthExecutable.FileName);

        if (OperatingSystem.IsLinux() && executableName.Length > 15)
            executableName = executableName[..15];

        string executablePath = distribution.ManagerExecutablePath;
        Dictionary<int, int> udpPortOwners = ASPIRE.Common.Netcode.Sockets.NativeSocketTable.GetAllPortOwners(ProtocolType.Udp);

        foreach (Process process in Process.GetProcessesByName(executableName))
            try
            {
                string? modulePath = process.MainModule?.FileName;

                if (string.Equals(modulePath, executablePath, StringComparison.OrdinalIgnoreCase) is false)
                    continue;

                if (!ShouldReapOrphanProcess(process.Id, udpPortOwners))
                {
                    logger.LogInformation(
                        "[FleetSupervisor] Preserving sibling fleet match server process (PID {ProcessID}) outside local port range {Start}-{End}.",
                        process.Id, Ports.LocalGameStart, Ports.LocalGameEnd);
                    continue;
                }

                logger.LogWarning(
                    "Killing An Orphaned Match Server Process (Process {ProcessID}) Left Over From A Previous Run",
                    process.Id);

                process.Kill(true);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed To Inspect Or Kill A Potential Orphan Process (Process {ProcessID})",
                    process.Id);
            }
            finally
            {
                process.Dispose();
            }
    }

    internal bool ShouldReapOrphanProcess(int processId, IReadOnlyDictionary<int, int> udpPortOwners)
    {
        int fleetWindowStart = PortPlan.BaseGamePort;
        int fleetWindowEnd = PortPlan.BaseGamePort + PortPlan.PortRangeWindow - 1;
        bool ownsLocalPortInThisFleet = false;
        bool ownsLocalPortInSiblingFleet = false;

        foreach (KeyValuePair<int, int> kvp in udpPortOwners)
        {
            if (kvp.Value != processId)
            {
                continue;
            }

            int boundPort = kvp.Key;
            if (boundPort >= Ports.LocalGameStart && boundPort <= Ports.LocalGameEnd)
            {
                ownsLocalPortInThisFleet = true;
            }
            else if (boundPort >= fleetWindowStart && boundPort <= fleetWindowEnd)
            {
                ownsLocalPortInSiblingFleet = true;
            }
        }

        if (ownsLocalPortInThisFleet)
        {
            return true;
        }

        if (ownsLocalPortInSiblingFleet)
        {
            return false;
        }

        // Unbound process (e.g. still initializing or crashed prior to socket bind):
        // Secondary fleets (Queue Arcade / non-zero offset) must not kill unbound sibling processes if another fleet is active.
        if (options.QueueArcadeMode || Ports.Offset > 0)
        {
            return false;
        }

        return true;
    }

    private void LogPortAllocation()
    {
        logger.LogInformation(
            "Match Server Fleet Configured: {Instances} Instance(s), Server Address {ServerAddress}, Master Server {MasterServer}",
            options.Instances, ServerAddress, addressResolver.MasterServerHostAndPort
        );

        logger.LogInformation(
            "Port Allocation: Game {LocalGameStart}-{LocalGameEnd}, Voice {LocalVoiceStart}-{LocalVoiceEnd}, Public Game {PublicGameStart}-{PublicGameEnd}, Public Voice {PublicVoiceStart}-{PublicVoiceEnd}, Ping {PingPort}{ProxyNote}",
            Ports.LocalGameStart, Ports.LocalGameEnd, Ports.LocalVoiceStart, Ports.LocalVoiceEnd,
            Ports.PublicGameStart, Ports.PublicGameEnd, Ports.PublicVoiceStart, Ports.PublicVoiceEnd,
            Ports.PingPort, Ports.UseProxy ? " (Proxy Enabled)" : string.Empty
        );

        logger.LogInformation("Runtime Artefacts Directory: {Directory}", artefacts.ArtefactsDirectory);
    }

    /// <summary>
    /// Applies dedicated CPU core affinity, dual-channel priority, and NUMA node pinning to all running worker processes.
    /// </summary>
    public void ApplyChildProcessAffinities(HostPriorityMode? overridePriorityMode = null)
    {
        HostPriorityMode effectiveMode = overridePriorityMode ?? (options.PriorityMode == HostPriorityMode.Adaptive
            ? (_lastAdaptiveClientRunning ? HostPriorityMode.Background : HostPriorityMode.HighPriority)
            : options.PriorityMode);

        foreach (DedicatedServerWorker worker in _workers.Values)
        {
            if (worker.Process is { HasExited: false } proc)
            {
                WorkerCpuPinning pinning = CpuTopologyResolver.GetDedicatedWorkerPinning(
                    worker.WorkerSlotIndex,
                    options.Instances,
                    effectiveMode,
                    sandbox.Options.AffinityMode);

                CpuTopologyResolver.AssignProcessAffinity(
                    proc,
                    worker.WorkerSlotIndex,
                    options.Instances,
                    effectiveMode,
                    sandbox.Options.AffinityMode,
                    explicitMask: null,
                    logger);

                sandbox.AssignProcess(proc, worker.Port, pinning);
            }
        }
    }

    private void ReconcileAdaptivePriority()
    {
        if (options.PriorityMode != HostPriorityMode.Adaptive) return;

        bool clientRunning = DetectLocalGameClient();
        if (clientRunning != _lastAdaptiveClientRunning)
        {
            _lastAdaptiveClientRunning = clientRunning;
            HostPriorityMode newMode = clientRunning ? HostPriorityMode.Background : HostPriorityMode.HighPriority;
            logger.LogInformation(
                "[AdaptivePriority] Local game client {State}. Shifting dedicated server fleet to {Mode}...",
                clientRunning ? "DETECTED (Player Active)" : "IDLE / EXITED",
                newMode);

            ApplyChildProcessAffinities(newMode);
        }
    }

    private bool DetectLocalGameClient()
    {
        try
        {
            HashSet<int> workerPids = [.. _workers.Values.Where(w => w.Process != null).Select(w => w.Process!.Id)];
            Process[] candidates = Process.GetProcessesByName("hon_x64");
            if (candidates.Length == 0 && !OperatingSystem.IsWindows())
            {
                candidates = Process.GetProcessesByName("hon");
            }

            foreach (Process p in candidates)
            {
                try
                {
                    if (!workerPids.Contains(p.Id) && !p.HasExited)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogTrace(ex, "[FleetSupervisor] Transient process exit while checking local game client candidate.");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] DetectLocalGameClient process inspection error.");
        }

        return false;
    }

    private string GetHashedPassword()
    {
        string pwd = options.Password?.Trim() ?? string.Empty;
        if (pwd.Length == 64 && pwd.All(char.IsAsciiHexDigit))
        {
            return pwd.ToLowerInvariant();
        }
        if (pwd.Length == 32 && pwd.All(char.IsAsciiHexDigit))
        {
            return pwd.ToLowerInvariant();
        }
        if (options.UseSha256Authentication)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pwd)));
        }
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(pwd)));
    }

    private async Task RegisterServerManagerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
        {
            logger.LogWarning("[FleetSupervisor] Server Manager username or password not configured; skipping Master Server authentication.");
            return;
        }

        string hostAndPort = addressResolver.MasterServerHostAndPort;
        if (!await AddressResolver.CheckMasterServerReachabilityAsync(hostAndPort, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            _serverManagerAuthenticated = false;
            logger.LogWarning("[FleetSupervisor] Master Server at {Endpoint} is offline or unreachable; skipping initial Server Manager authentication. Workers will launch in local standalone mode.", hostAndPort);
            return;
        }

        try
        {
            string baseUrl = hostAndPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostAndPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? hostAndPort
                : $"http://{hostAndPort}";
            string endpoint = $"{baseUrl.TrimEnd('/')}/server_requester.php";

            using FormUrlEncodedContent content = new([
                new KeyValuePair<string, string>("f", "replay_auth"),
                new KeyValuePair<string, string>("login", options.UserName),
                new KeyValuePair<string, string>("pass", GetHashedPassword())
            ]);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            HttpResponseMessage response = await _masterServerHttpClient.PostAsync(endpoint, content, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _serverManagerAuthenticated = true;
                logger.LogInformation("[FleetSupervisor] Authenticated Server Manager '{User}' with Master Server ({Endpoint}). Stale fleet registrations purged.",
                    options.UserName, endpoint);
            }
            else
            {
                _serverManagerAuthenticated = false;
                string errorBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                logger.LogWarning("[FleetSupervisor] Failed to authenticate Server Manager with Master Server: HTTP {StatusCode} - {Error}",
                    response.StatusCode, errorBody);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] RegisterServerManagerAsync canceled.");
        }
        catch (Exception ex)
        {
            _serverManagerAuthenticated = false;
            logger.LogError(ex, "[FleetSupervisor] Exception occurred while authenticating Server Manager with Master Server.");
        }
    }

    private async Task RegisterWorkerSessionAsync(DedicatedServerWorker worker, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
        {
            return;
        }

        string hostAndPort = addressResolver.MasterServerHostAndPort;
        if (!await AddressResolver.CheckMasterServerReachabilityAsync(hostAndPort, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            string address = ServerAddress ?? "127.0.0.1";
            string baseUrl = hostAndPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostAndPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? hostAndPort
                : $"http://{hostAndPort}";
            string endpoint = $"{baseUrl.TrimEnd('/')}/server_requester.php";

            string subAccountLogin = $"{options.UserName}:{worker.InstanceIndex}";
            int registeredPort = options.UseProxy ? worker.PublicGamePort : worker.Port;

            List<KeyValuePair<string, string>> formFields =
            [
                new("f", "new_session"),
                new("login", subAccountLogin),
                new("pass", GetHashedPassword()),
                new("port", registeredPort.ToString()),
                new("local_port", worker.Port.ToString()),
                new("name", worker.ServerName),
                new("desc", string.Empty),
                new("location", options.Location),
                new("ip", address)
            ];

            int? workerPid = worker.LaunchProcessId ?? worker.ProcessId;
            if (workerPid is int pid && pid > 0)
            {
                formFields.Add(new("pid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (worker.LaunchProcessStartUtc is DateTimeOffset procStartUtc)
            {
                formFields.Add(new("process_start_utc", procStartUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(worker.LaunchBootNonce))
            {
                formFields.Add(new("boot_nonce", worker.LaunchBootNonce));
            }

            using FormUrlEncodedContent content = new(formFields);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            HttpResponseMessage response = await _masterServerHttpClient.PostAsync(endpoint, content, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                Match sessionMatch = Regex.Match(responseBody, @"s:7:""session"";s:\d+:""([^""]+)""");
                if (sessionMatch.Success)
                {
                    worker.SessionCookie = sessionMatch.Groups[1].Value;
                }

                Match idMatch = Regex.Match(responseBody, @"s:9:""server_id"";(?:i:(-?\d+)|s:\d+:""(-?\d+)"")");
                if (idMatch.Success)
                {
                    string val = !string.IsNullOrEmpty(idMatch.Groups[1].Value) ? idMatch.Groups[1].Value : idMatch.Groups[2].Value;
                    if (int.TryParse(val, out int serverId))
                    {
                        worker.ServerId = serverId;
                    }
                }

                logger.LogInformation(
                    "[FleetSupervisor] Registered Dedicated Worker instance {Instance} on Port {Port} (Public: {PublicPort}) with Master Server. ServerId: {ServerId}, Session: {Session}",
                    worker.InstanceIndex, worker.Port, registeredPort, worker.ServerId,
                    worker.SessionCookie != null ? worker.SessionCookie[..Math.Min(8, worker.SessionCookie.Length)] + "..." : "none");
            }
            else
            {
                string errorBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                logger.LogWarning(
                    "[FleetSupervisor] Master Server rejected new_session for Worker on Port {Port}: HTTP {StatusCode} - {Error}",
                    worker.Port, response.StatusCode, errorBody);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] RegisterWorkerSessionAsync canceled for Port {Port}.", worker.Port);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[FleetSupervisor] Failed to register Worker on Port {Port} with Master Server.", worker.Port);
        }
    }

    private async Task DeregisterWorkerSessionAsync(DedicatedServerWorker worker, CancellationToken cancellationToken)
    {
        string? sessionCookie = worker.SessionCookie;
        if (string.IsNullOrEmpty(sessionCookie))
            return;

        worker.SessionCookie = null;
        worker.ServerId = null;

        try
        {
            string hostAndPort = addressResolver.MasterServerHostAndPort;
            string baseUrl = hostAndPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostAndPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? hostAndPort
                : $"http://{hostAndPort}";
            string endpoint = $"{baseUrl.TrimEnd('/')}/server_requester.php";

            using FormUrlEncodedContent content = new([
                new KeyValuePair<string, string>("f", "shutdown"),
                new KeyValuePair<string, string>("session", sessionCookie)
            ]);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            HttpResponseMessage response = await _masterServerHttpClient.PostAsync(endpoint, content, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[FleetSupervisor] Deregistered Dedicated Worker on Port {Port} (Session: {Session}) from Master Server.",
                    worker.Port, sessionCookie[..Math.Min(8, sessionCookie.Length)] + "...");
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] DeregisterWorkerSessionAsync canceled for Port {Port}.", worker.Port);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[FleetSupervisor] Failed to deregister worker session on port {Port}.", worker.Port);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[FleetSupervisor] Starting Master Server background heartbeat loop (15s interval).");
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);

                if (!desiredRunning || !IsRunning)
                {
                    continue;
                }

                DateTimeOffset nowUtc = TimeProvider.System.GetUtcNow();
                foreach (DedicatedServerWorker candidate in Workers)
                {
                    if (ShouldReapEmptyLobby(candidate, nowUtc))
                    {
                        double ageSec = (nowUtc - candidate.LobbyOpenedAtUtc!.Value).TotalSeconds;
                        long staleMatchId = candidate.MatchId;
                        string? staleMatchName = candidate.MatchName;
                        candidate.LobbyOpenedAtUtc = null;
                        candidate.MatchId = 0;
                        candidate.MatchName = null;

                        logger.LogInformation(
                            "[FleetSupervisor] Empty 0-player lobby reaper triggered on Port {Port} (MatchId: {MatchId}, MatchName: '{MatchName}', Age: {AgeSec:F1}s >= {ThresholdSec}s). Recycling worker to restore idle state.",
                            candidate.Port, staleMatchId, staleMatchName ?? "<unnamed>", ageSec, options.EmptyLobbyReapSeconds);

                        _ = Task.Run(async () =>
                        {
                            if (staleMatchId > 0)
                            {
                                await NotifyMatchAbandonedAsync(candidate, (int)staleMatchId, "EmptyLobbyTimeout").ConfigureAwait(false);
                            }
                            await RecycleWorkerAsync(candidate).ConfigureAwait(false);
                        }, CancellationToken.None);
                    }
                }

                string hostAndPort = addressResolver.MasterServerHostAndPort;
                if (!await AddressResolver.CheckMasterServerReachabilityAsync(hostAndPort, cancellationToken: stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }

                string baseUrl = hostAndPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostAndPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? hostAndPort
                    : $"http://{hostAndPort}";
                string endpoint = $"{baseUrl.TrimEnd('/')}/server_requester.php";

                foreach (DedicatedServerWorker worker in Workers)
                {
                    if (!worker.IsRunning) continue;

                    // If session cookie is missing, attempt to register session
                    if (string.IsNullOrEmpty(worker.SessionCookie))
                    {
                        await RegisterWorkerSessionAsync(worker, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await DispatchWorkerHeartbeatAsync(worker, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[FleetSupervisor] Error during heartbeat loop cycle.");
            }
        }
    }

    internal bool ShouldReapEmptyLobby(
        DedicatedServerWorker? worker,
        DateTimeOffset utcNow,
        bool ignoreProcessRunningForTest = false)
    {
        if (options.QueueArcadeMode || options.EmptyLobbyReapSeconds <= 0 || worker is null)
        {
            return false;
        }

        if (!ignoreProcessRunningForTest && !worker.IsRunning)
        {
            return false;
        }

        if (!worker.DesiredRunning || worker.IsDraining || Volatile.Read(ref worker.IsRecycling) != 0)
        {
            return false;
        }

        if (worker.IsInMatch || worker.ConnectedClients > 0 || !worker.ActiveClientSlots.IsEmpty)
        {
            return false;
        }

        int activeProxySessions = options.UseProxy && proxy != null ? proxy.GetActiveSessionCount(worker.Port) : 0;
        if (activeProxySessions > 0)
        {
            return false;
        }

        if (!worker.LobbyOpenedAtUtc.HasValue)
        {
            return false;
        }

        return (utcNow - worker.LobbyOpenedAtUtc.Value).TotalSeconds >= options.EmptyLobbyReapSeconds;
    }

    private async Task DispatchWorkerHeartbeatAsync(DedicatedServerWorker worker, CancellationToken cancellationToken = default)
    {
        if (worker == null || !worker.IsRunning || string.IsNullOrEmpty(worker.SessionCookie)) return;

        int activeProxySessions = options.UseProxy && proxy != null ? proxy.GetActiveSessionCount(worker.Port) : 0;
        if (worker.IsInMatch || worker.MatchId > 0 || worker.ConnectedClients > 0 || activeProxySessions > 0)
        {
            logger.LogDebug(
                "[FleetSupervisor] Skipping synthetic idle heartbeat for Port {Port}: worker has active client/match session (InMatch={InMatch}, MatchId={MatchId}, LogClients={LogClients}, ProxySessions={ProxySessions}); K2_SERVER heartbeats authoritatively.",
                worker.Port, worker.IsInMatch, worker.MatchId, worker.ConnectedClients, activeProxySessions);
            return;
        }

        string hostAndPort = addressResolver.MasterServerHostAndPort;
        if (string.IsNullOrWhiteSpace(hostAndPort)) return;

        string baseUrl = hostAndPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostAndPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? hostAndPort
            : $"http://{hostAndPort}";
        string endpoint = $"{baseUrl.TrimEnd('/')}/server_requester.php";

        int cState = worker.IsInMatch
            ? 3  // SERVER_STATUS_ACTIVE
            : (worker.ConnectedClients > 0 ? 2 : 1); // 2 = SERVER_STATUS_LOADING (in lobby), 1 = SERVER_STATUS_IDLE
        int numConn = worker.ConnectedClients;
        string map = !string.IsNullOrWhiteSpace(worker.CurrentMap) ? worker.CurrentMap : "midwars";
        string matchName = !string.IsNullOrWhiteSpace(worker.MatchName) ? worker.MatchName : worker.ServerName;

        using FormUrlEncodedContent content = new([
            new KeyValuePair<string, string>("f", "set_online"),
            new KeyValuePair<string, string>("session", worker.SessionCookie),
            new KeyValuePair<string, string>("num_conn", numConn.ToString()),
            new KeyValuePair<string, string>("cgt", "00:00:00"),
            new KeyValuePair<string, string>("map", map),
            new KeyValuePair<string, string>("mname", matchName),
            new KeyValuePair<string, string>("private", "0"),
            new KeyValuePair<string, string>("vip", "0"),
            new KeyValuePair<string, string>("c_state", cState.ToString()),
            new KeyValuePair<string, string>("prev_c_state", cState.ToString())
        ]);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            HttpResponseMessage response = await _masterServerHttpClient.PostAsync(endpoint, content, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || (int)response.StatusCode == 401)
            {
                if (!ShouldAllowHeartbeat401ReRegistration(worker))
                {
                    logger.LogWarning(
                        "[FleetSupervisor] Heartbeat for Port {Port} returned 401 Unauthorized while worker PID {Pid} is still running (Session: {Session}). Refusing f=new_session cookie mint; awaiting engine stdout 'New session cookie' resync.",
                        worker.Port,
                        worker.ProcessId ?? worker.LaunchProcessId,
                        SecurityMasking.MaskToken(worker.SessionCookie));
                }
                else
                {
                    logger.LogWarning(
                        "[FleetSupervisor] Heartbeat for Port {Port} returned 401 Unauthorized and worker process has exited; clearing stale session cookie.",
                        worker.Port);
                    worker.SessionCookie = null;
                }
            }
            else if (response.IsSuccessStatusCode)
            {
                logger.LogDebug("[FleetSupervisor] Heartbeat dispatched successfully for worker on Port {Port} (Session: {Session}, Clients: {Clients}, State: {State})",
                    worker.Port, SecurityMasking.MaskToken(worker.SessionCookie), numConn, cState);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "[FleetSupervisor] Failed to dispatch heartbeat for worker on Port {Port}", worker.Port);
        }
    }

    private async Task NotifyMatchAbandonedAsync(DedicatedServerWorker worker, int matchId, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            string hostAndPort = options.MasterServerAddress;
            if (string.IsNullOrWhiteSpace(hostAndPort)) return;

            string baseUrl = hostAndPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostAndPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? hostAndPort
                : $"http://{hostAndPort}";
            string endpoint = $"{baseUrl.TrimEnd('/')}/server_requester.php";

            List<KeyValuePair<string, string>> fields =
            [
                new KeyValuePair<string, string>("f", "abandon_match"),
                new KeyValuePair<string, string>("match_id", matchId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("reason", reason)
            ];

            if (!string.IsNullOrWhiteSpace(worker.SessionCookie))
            {
                fields.Add(new KeyValuePair<string, string>("session", worker.SessionCookie));
            }

            using FormUrlEncodedContent content = new(fields);
            using HttpRequestMessage requestMessage = new(HttpMethod.Post, endpoint) { Content = content };

            string? internalKey = _configuration?["InternalServiceKey"] ?? Environment.GetEnvironmentVariable("InternalServiceKey");
            if (string.IsNullOrWhiteSpace(internalKey) && (_hostEnvironment?.IsDevelopment() ?? false))
            {
                internalKey = NEXUS.Contracts.Security.SecurityConstants.DefaultInternalServiceKey;
            }

            if (!string.IsNullOrWhiteSpace(internalKey))
            {
                requestMessage.Headers.TryAddWithoutValidation(
                    NEXUS.Contracts.Security.SecurityConstants.InternalServiceKeyHeader,
                    internalKey);
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            using HttpResponseMessage response = await _masterServerHttpClient.SendAsync(requestMessage, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[FleetSupervisor] Notified Master Server that Match #{MatchID} on Port {Port} was abandoned ({Reason}).",
                    matchId, worker.Port, reason);
            }
            else
            {
                logger.LogWarning("[FleetSupervisor] Master Server rejected abandon_match for Match #{MatchID} on Port {Port} with HTTP {StatusCode}.",
                    matchId, worker.Port, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace(ex, "[FleetSupervisor] NotifyMatchAbandonedAsync canceled for Match #{MatchID} on Port {Port}.", matchId, worker.Port);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[FleetSupervisor] Failed to notify Master Server of abandoned match #{MatchID} on port {Port}.", matchId, worker.Port);
        }
    }

    public override void Dispose()
    {
        _masterServerHttpClient.Dispose();
        lifecycleGate.Dispose();
        reconcileSignal.Dispose();
        base.Dispose();
    }
}
