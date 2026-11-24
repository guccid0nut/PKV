// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.Hosting;
using COMPEL.Services.Supervision;

namespace COMPEL.Services.Proxy;

/// <summary>
///     The managed, cross-platform proxy. When enabled, it runs a UDP relay per instance for both the game and voice ports,
///     forwarding the public ports (offset by <see cref="PortPlan.ProxyPublicOffset"/>) to the local server ports.
///     Heroes Of Newerth clients throttle their own traffic on the public port range until the proxy authenticates them,
///     so each forwarder issues a challenge to every session on creation and this service renews those challenges periodically.
/// </summary>
public sealed class UDPProxyService : ResilientBackgroundService
{
    internal static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(2);

    // A Session That Has Never Authenticated Is Swept Far Sooner Than One Carrying A Real Match,
    // Because Any Datagram From A Novel Source Creates One And The Repeat Above Then Transmits To It Every Second.
    // Eviction Only Runs On A Rotating Pass, So The Effective Unauthenticated Lifetime Is Fifteen To Twenty-Five Seconds Rather Than Exactly Fifteen.
    internal static readonly TimeSpan UnauthenticatedSessionTimeout = TimeSpan.FromSeconds(15);

    // Renewed Well Within The Client's Authentication Window So A Session Never Lapses Back To The Throttled, Unauthenticated State Between Renewals.
    internal static readonly TimeSpan ChallengeRenewalInterval = TimeSpan.FromSeconds(10);

    // The Maintenance Pass Runs Far More Often Than A Rotation, Because The Reference Re-Sends The Current Challenge About Once A Second
    // So That One Lost Challenge Datagram Cannot Leave A Client Throttled.
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);

    internal static readonly int MaintenancePassesPerRotation = (int)Math.Max(1, ChallengeRenewalInterval.Ticks / MaintenanceInterval.Ticks);

    private readonly MatchServerManagerOptions options;
    private readonly PortPlan ports;
    private readonly ILogger<UDPProxyService> logger;

    private readonly List<UDPForwarder> forwarders = [];

    // One Clock For The Score Drain, Both Idle Timeouts And The Unknown-Challenge Grace, So Everything Time-Dependent In The Proxy Measures From The Same Place.
    private readonly TimeProvider timeProvider = TimeProvider.System;

    // Shared Across Every Forwarder So A Single Source's Score Is The Same Regardless Of Which Public Port It Sends To.
    private readonly ViolationScoreContainer scoreContainer;

    // Shared Across Every Forwarder So Attack Pressure On Any Public Port Activates The Under-Attack Protection Globally.
    private readonly AttackIndicatorContainer attackIndicator;

    // Completes With TRUE Once The Proxy Is Usable (Disabled, Or At Least One Forwarder Bound) And FALSE When The Proxy Is Enabled But No Forwarder Could Bind.
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim restartSignal = new(0);
    private CancellationTokenSource? cycleCancellationSource;
    private TaskCompletionSource<bool>? pendingRestartCompletion;

    private volatile bool running;
    private int failedForwarderCount;
    private long droppedDatagramCount;
    private int maintenancePassesSinceRotation;
    private readonly XdpPacketFilterManager? xdpFilter;

    public UDPProxyService(
        IOptions<MatchServerManagerOptions> options,
        PortPlan ports,
        ILogger<UDPProxyService> logger,
        XdpPacketFilterManager? xdpFilter = null)
        : base(logger, nameof(UDPProxyService))
    {
        this.options = options.Value;
        this.ports = ports;
        this.logger = logger;
        this.xdpFilter = xdpFilter;

        scoreContainer = new ViolationScoreContainer(timeProvider);
        attackIndicator = new AttackIndicatorContainer(timeProvider);
    }

    /// <summary>
    ///     Whether the proxy is enabled in configuration.
    /// </summary>
    public bool IsEnabled => options.UseProxy;

    public bool IsRunning => running;

    /// <summary>
    ///     Completes once the proxy has finished its bind attempt: <see langword="true"/> when the proxy is disabled or at least one forwarder bound,
    ///     and <see langword="false"/> when the proxy is enabled but no forwarder could bind.
    /// </summary>
    public Task<bool> WaitUntilReady(CancellationToken cancellationToken) => ready.Task.WaitAsync(cancellationToken);

    /// <summary>
    ///     The number of game/voice ports that failed to bind on startup. A non-zero value means the proxy is running in a degraded state.
    /// </summary>
    public int FailedForwarderCount => Volatile.Read(ref failedForwarderCount);

    /// <summary>
    ///     The number of client datagrams the proxy has refused to relay, across every forwarder, as at the last maintenance pass.
    /// </summary>
    public long DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);

    /// <summary>
    ///     Whether the proxy is currently under attack based on the live decaying attack indicator.
    /// </summary>
    public bool IsUnderAttack => attackIndicator.IsUnderAttack;

    /// <summary>
    ///     Retains external client UDP sessions for a specific local server port during crash resilience recovery,
    ///     preventing disconnects and holding client sockets open while the server worker restarts.
    /// </summary>
    public void RetainSessionsForWorker(int localPort)
    {
        lock (forwarders)
        {
            foreach (UDPForwarder forwarder in forwarders)
            {
                if (forwarder.LocalPort == localPort)
                {
                    forwarder.RetainSessions(true);
                    logger.LogInformation("[UDPProxy] Retained external client UDP sessions for LocalPort {Port} (Zero Disconnect Guard Active)", localPort);
                }
            }
        }
    }

    /// <summary>
    ///     Releases retained client UDP sessions for a specific local server port after crash recovery completes,
    ///     resuming normal forwarding and session lifecycle.
    /// </summary>
    public void ReleaseSessionsForWorker(int localPort)
    {
        lock (forwarders)
        {
            foreach (UDPForwarder forwarder in forwarders)
            {
                if (forwarder.LocalPort == localPort)
                {
                    forwarder.RetainSessions(false);
                    logger.LogInformation("[UDPProxy] Released retained client UDP sessions for LocalPort {Port}", localPort);
                }
            }
        }
    }

    /// <summary>
    ///     Gets the count of currently active sessions across all forwarders for a specific local server port.
    /// </summary>
    public int GetActiveSessionCount(int localPort)
    {
        lock (forwarders)
        {
            foreach (UDPForwarder forwarder in forwarders)
            {
                if (forwarder.LocalPort == localPort)
                {
                    return forwarder.ActiveSessionCount;
                }
            }
        }
        return 0;
    }

    /// <summary>
    ///     Requests that the proxy restart its forwarders, rebinding its public ports.
    /// </summary>
    public async Task<bool> RequestRestart(CancellationToken cancellationToken)
    {
        if (options.UseProxy is false)
            throw new InvalidOperationException("The Proxy Is Disabled");

        TaskCompletionSource<bool> completionSource;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (pendingRestartCompletion is not null)
            {
                completionSource = pendingRestartCompletion;
            }
            else
            {
                completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingRestartCompletion = completionSource;

                if (cycleCancellationSource is not null && cycleCancellationSource.IsCancellationRequested is false)
                    cycleCancellationSource.Cancel();
                else
                    restartSignal.Release();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        return await completionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (options.UseProxy is false)
        {
            logger.LogInformation("Proxy Is Disabled; The Servers' Local Ports Are Public");
            ready.TrySetResult(true);
            return;
        }

        while (stoppingToken.IsCancellationRequested is false)
        {
            await RunCycle(stoppingToken).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
                break;

            if (pendingRestartCompletion is null)
            {
                try
                {
                    await restartSignal.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunCycle(CancellationToken stoppingToken)
    {
        TaskCompletionSource<bool>? currentRestartCompletion;
        CancellationTokenSource cycleCancellation;

        await lifecycleGate.WaitAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cycleCancellationSource = cycleCancellation;

            currentRestartCompletion = pendingRestartCompletion;
            pendingRestartCompletion = null;

            failedForwarderCount = 0;
            forwarders.Clear();

            string tierPrefix = options.FleetTier ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tierPrefix))
            {
                string? envTier = Environment.GetEnvironmentVariable("COMPEL_FLEET_TIER");
                if (!string.IsNullOrWhiteSpace(envTier))
                {
                    tierPrefix = envTier.Trim().ToUpperInvariant();
                }
                else if (string.Equals(Environment.GetEnvironmentVariable("AGONES_ENABLED"), "true", StringComparison.OrdinalIgnoreCase) ||
                         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")) ||
                         options.PortRangeOffset is 200 or 20)
                {
                    tierPrefix = "K";
                }
                else if (AddressResolver.IsRunningInContainer || options.PortRangeOffset is 100 or 10)
                {
                    tierPrefix = "D";
                }
                else
                {
                    tierPrefix = "L";
                }
            }

            for (int instance = 0; instance < ports.Instances; instance++)
            {
                string serverName = $"{options.ServerNamePrefix} #{tierPrefix}{instance + 1}";
                TryAddForwarder(ports.PublicGameStart + instance, ports.LocalGameStart + instance, ProxyForwarderKind.Game, serverName, options.Location);
                TryAddForwarder(ports.PublicVoiceStart + instance, ports.LocalVoiceStart + instance, ProxyForwarderKind.Voice);
            }

            if (forwarders.Count is 0)
            {
                logger.LogError("No Proxy Forwarders Could Be Started");
                ready.TrySetResult(false);
                currentRestartCompletion?.TrySetResult(false);
                return;
            }

            running = true;
            ready.TrySetResult(true);
            currentRestartCompletion?.TrySetResult(true);

            logger.LogInformation
            (
                "Proxy Forwarding {Instances} Instance(s): Public Game {PublicGameStart}-{PublicGameEnd} And Voice {PublicVoiceStart}-{PublicVoiceEnd} To Local Game {LocalGameStart}-{LocalGameEnd} And Voice {LocalVoiceStart}-{LocalVoiceEnd}",
                ports.Instances, ports.PublicGameStart, ports.PublicGameEnd, ports.PublicVoiceStart, ports.PublicVoiceEnd, ports.LocalGameStart, ports.LocalGameEnd, ports.LocalVoiceStart, ports.LocalVoiceEnd
            );
        }
        finally
        {
            lifecycleGate.Release();
        }

        CancellationToken cycleToken = cycleCancellation.Token;

        List<Task> tasks = forwarders.Select(forwarder => forwarder.Run(cycleToken)).ToList();
        tasks.Add(RunMaintenanceLoop(cycleToken));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                running = false;

                foreach (UDPForwarder forwarder in forwarders)
                {
                    if (xdpFilter != null)
                    {
                        xdpFilter.UnregisterPort((ushort)forwarder.PublicPort);
                        xdpFilter.UnregisterPort((ushort)forwarder.LocalPort);
                    }
                    forwarder.Dispose();
                }

                forwarders.Clear();
                cycleCancellation.Dispose();

                if (ReferenceEquals(cycleCancellationSource, cycleCancellation))
                    cycleCancellationSource = null;
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
    }

    public override void Dispose()
    {
        if (xdpFilter != null)
        {
            foreach (UDPForwarder forwarder in forwarders)
            {
                xdpFilter.UnregisterPort((ushort)forwarder.PublicPort);
                xdpFilter.UnregisterPort((ushort)forwarder.LocalPort);
            }
        }
        lifecycleGate.Dispose();
        restartSignal.Dispose();
        base.Dispose();
    }

    private void TryAddForwarder(int publicPort, int localPort, ProxyForwarderKind kind, string? serverName = null, string? location = null)
    {
        try
        {
            bool enableZstd = options.EnableZstdNetcodeCompression && kind == ProxyForwarderKind.Game;
            bool enableBbr = options.UseBbrPacing && kind == ProxyForwarderKind.Game;
            bool enableSubTick = options.UseSubTickNetcode && kind == ProxyForwarderKind.Game;
            forwarders.Add(new UDPForwarder(
                publicPort,
                localPort,
                kind,
                ChallengeRenewalInterval,
                scoreContainer,
                attackIndicator,
                timeProvider,
                logger,
                enableZstd,
                enableBbr,
                enableSubTick,
                sessionKeyResolver: null,
                serverName: serverName,
                serverLocation: location,
                enableLegacyWatermarkChallenge: options.EnableLegacyWatermarkChallenge));

            xdpFilter?.RegisterPort((ushort)publicPort, Environment.ProcessId, isVoice: kind == ProxyForwarderKind.Voice, isProxy: true);
            xdpFilter?.RegisterPort((ushort)localPort, Environment.ProcessId, isVoice: kind == ProxyForwarderKind.Voice, isProxy: false);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref failedForwarderCount);
            logger.LogError(exception, "Failed To Bind The {Kind} Proxy On Public Port {PublicPort}", kind, publicPort);
        }
    }

    private async Task RunMaintenanceLoop(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested is false)
        {
            try
            {
                await Task.Delay(MaintenanceInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            scoreContainer.Drain();
            attackIndicator.Drain();

            bool rotating = ++maintenancePassesSinceRotation >= MaintenancePassesPerRotation;

            if (rotating)
                maintenancePassesSinceRotation = 0;

            long droppedDatagrams = 0;

            foreach (UDPForwarder forwarder in forwarders)
            {
                if (rotating)
                {
                    forwarder.EvictIdleSessions(IdleSessionTimeout, UnauthenticatedSessionTimeout);
                    forwarder.RotateChallenges();
                }
                else
                {
                    forwarder.RepeatChallenges();
                }

                droppedDatagrams += forwarder.DroppedDatagramCount;
            }

            Volatile.Write(ref droppedDatagramCount, droppedDatagrams);

            if (attackIndicator.IsUnderAttack)
                logger.LogWarning("The Proxy Is Currently Under Attack (Indicator Score: {Score})", attackIndicator.Score);
        }
    }
}
