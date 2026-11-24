// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesLifecycleService.cs
// System: COMPEL - Heroes of Newerth Dedicated Server Supervisor
// Subsystem: Cloud-Native Fleet Orchestration (K3s & Agones Integration)
// ============================================================================

using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Services.Supervision;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Bridges COMPEL dedicated game server lifecycle events with the Agones GameServer Sidecar
///     (REST port 9358) in Kubernetes / k3s cloud-native clusters.
///     Dispatches /ready when worker enters Available state, /health heartbeat periodically,
///     and /shutdown when match ends to achieve zero-leak container recycling.
/// </summary>
public sealed class AgonesLifecycleService : ResilientBackgroundService
{
    private readonly MatchServerManagerSupervisor? _supervisor;
    private readonly Func<IReadOnlyCollection<DedicatedServerWorker>>? _workersResolver;
    private readonly ILogger<AgonesLifecycleService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _agonesBaseUrl;
    private readonly bool _isEnabled;
    private bool _hasSentReady;

    public bool IsEnabled => _isEnabled;
    public bool HasSentReady => _hasSentReady;
    public string AgonesBaseUrl => _agonesBaseUrl;

    public AgonesLifecycleService(
        MatchServerManagerSupervisor? supervisor,
        ILogger<AgonesLifecycleService> logger,
        IConfiguration configuration,
        HttpClient? httpClient = null,
        Func<IReadOnlyCollection<DedicatedServerWorker>>? workersResolver = null)
        : base(logger, nameof(AgonesLifecycleService))
    {
        _supervisor = supervisor;
        _workersResolver = workersResolver ?? (supervisor != null ? () => supervisor.Workers : null);
        _logger = logger;
        _httpClient = httpClient ?? SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(2));

        string? envEnabled = configuration["AGONES_ENABLED"] ?? Environment.GetEnvironmentVariable("AGONES_ENABLED");
        _isEnabled = bool.TryParse(envEnabled, out bool enabled) && enabled;

        int port = 9358;
        string? portStr = configuration["AGONES_SDK_HTTP_PORT"] ?? Environment.GetEnvironmentVariable("AGONES_SDK_HTTP_PORT");
        if (int.TryParse(portStr, out int p) && p > 0)
        {
            port = p;
        }

        _agonesBaseUrl = $"http://localhost:{port}";
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation("[Agones] Integration disabled (AGONES_ENABLED is not set to true). Running in standalone supervisor mode.");
            return;
        }

        _logger.LogInformation("[Agones] Cloud-native Agones integration ACTIVE. Target Sidecar: {BaseUrl}", _agonesBaseUrl);

        // Hook match completion and match start events
        if (_supervisor != null)
        {
            _supervisor.MatchEnded += OnMatchEndedAsync;
            _supervisor.MatchStarted += OnMatchStartedAsync;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_hasSentReady)
                {
                    bool hasAvailableWorker = _workersResolver == null || _workersResolver().Any(w => w.IsRunning && w.CanAcceptNewMatches);
                    if (hasAvailableWorker)
                    {
                        bool success = await SendAgonesCommandAsync("ready", stoppingToken).ConfigureAwait(false);
                        if (success)
                        {
                            _hasSentReady = true;
                            _logger.LogInformation("[Agones] Successfully signaled /ready. GameServer is now allocated in Agones Ready pool.");
                        }
                    }
                }
                else
                {
                    // Regular health heartbeat every 2 seconds
                    await SendAgonesCommandAsync("health", stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown
        }
        finally
        {
            if (_supervisor != null)
            {
                _supervisor.MatchEnded -= OnMatchEndedAsync;
                _supervisor.MatchStarted -= OnMatchStartedAsync;
            }
        }
    }

    internal async Task OnMatchStartedAsync(DedicatedServerWorker? worker)
    {
        if (!_isEnabled) return;

        int port = worker?.Port ?? 0;
        _logger.LogInformation(
            "[Agones] Match started on port {Port}. Signaling /allocate to transition GameServer to Allocated pool...",
            port);
        bool success = await SendAgonesCommandAsync("allocate", CancellationToken.None).ConfigureAwait(false);
        if (success)
        {
            _logger.LogInformation("[Agones] Successfully signaled /allocate for GameServer on port {Port}.", port);
        }
        else
        {
            _logger.LogWarning("[Agones] Failed to signal /allocate for GameServer on port {Port}.", port);
        }
    }

    internal async Task OnMatchEndedAsync(DedicatedServerWorker? worker)
    {
        if (!_isEnabled) return;

        int port = worker?.Port ?? 0;

        IReadOnlyCollection<DedicatedServerWorker>? workers = _workersResolver?.Invoke();
        if (workers != null && workers.Any(w => w.Port != port && w.IsInMatch))
        {
            _logger.LogInformation(
                "[Agones] Match concluded on port {Port}, but sibling dedicated workers are still in active matches. Deferring /shutdown.",
                port);
            return;
        }

        _logger.LogInformation(
            "[Agones] Match concluded on port {Port} and no sibling workers have active matches. Triggering /shutdown to recycle GameServer pod cleanly...",
            port);
        await SendAgonesCommandAsync("shutdown", CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<bool> SendAgonesCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            using StringContent content = new("{}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync($"{_agonesBaseUrl}/{command}", content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("[Agones] Command '/{Command}' returned HTTP status {StatusCode}", command, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Agones] Failed to dispatch '/{Command}' to {BaseUrl}: {Message}", command, _agonesBaseUrl, ex.Message);
            return false;
        }
    }
}
