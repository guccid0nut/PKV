// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Http;
using ASPIRE.Common.Network;

namespace TRANSMUTANSTEIN.ChatServer.Services;

/// <summary>
///     Background service that discovers and refreshes the host's public WAN IPv4 address for
///     <see cref="SplitHorizonEndpointResolver" /> inside the TRANSMUTANSTEIN.ChatServer process
///     when public/WAN hosting is enabled without a static CHAT_SERVER_PUBLIC_HOST override.
/// </summary>
internal sealed class SplitHorizonPublicIpDiscoveryService : ResilientBackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly ILogger<SplitHorizonPublicIpDiscoveryService> _logger;
    private readonly Func<string, string?> _environmentReader;

    public SplitHorizonPublicIpDiscoveryService(
        ILogger<SplitHorizonPublicIpDiscoveryService> logger,
        IHttpClientFactory? httpClientFactory = null,
        HttpClient? httpClient = null,
        Func<string, string?>? environmentReader = null)
        : base(logger, nameof(SplitHorizonPublicIpDiscoveryService))
    {
        _logger = logger;
        _httpClient = httpClient
                      ?? httpClientFactory?.CreateClient(nameof(SplitHorizonPublicIpDiscoveryService))
                      ?? SharedSocketsHttpClientFactory.Create(RequestTimeout);
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        string? configuredPublicHost = _environmentReader("CHAT_SERVER_PUBLIC_HOST")?.Trim()
                                       ?? _environmentReader("PUBLIC_GAME_SERVER_IP")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPublicHost)
            && !SplitHorizonEndpointResolver.IsLoopbackOrLocalhost(configuredPublicHost)
            && IPAddress.TryParse(configuredPublicHost, out _))
        {
            SplitHorizonEndpointResolver.SetCachedPublicIP(configuredPublicHost);
            _logger.LogInformation(
                "[ChatRouting] Initialized SplitHorizonEndpointResolver public IP from environment override: {PublicIP}",
                configuredPublicHost);
        }

        if (ShouldSkipExternalDiscovery())
        {
            _logger.LogInformation(
                "[ChatRouting] Skipping external public IP discovery in ChatServer (KONGOR_STANDALONE={Standalone}, KONGOR_DISCOVER_PUBLIC_IP={DiscoverPublicIp}).",
                _environmentReader("KONGOR_STANDALONE") ?? "false",
                _environmentReader("KONGOR_DISCOVER_PUBLIC_IP") ?? "false");
            return;
        }

        await RefreshPublicIpAsync(stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(RefreshInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await RefreshPublicIpAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ChatRouting] Error during periodic ChatServer public IP refresh.");
            }
        }
    }

    internal async Task RefreshPublicIpAsync(CancellationToken ct = default)
    {
        if (ShouldSkipExternalDiscovery())
        {
            return;
        }

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            string raw = await _httpClient.GetStringAsync("https://api.ipify.org", cts.Token).ConfigureAwait(false);
            string trimmed = raw.Trim();
            if (IPAddress.TryParse(trimmed, out _))
            {
                SplitHorizonEndpointResolver.SetCachedPublicIP(trimmed);
                _logger.LogInformation("[ChatRouting] ChatServer public WAN IP discovered/refreshed: {PublicIP}", trimmed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ChatRouting] Failed to discover ChatServer public WAN IP via api.ipify.org. Set CHAT_SERVER_PUBLIC_HOST if external WAN peers cannot join friends.");
        }
    }

    private bool ShouldSkipExternalDiscovery()
    {
        if (string.Equals(_environmentReader("IS_TEST_ENVIRONMENT"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(_environmentReader("KONGOR_STANDALONE"), "true", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(_environmentReader("KONGOR_DISCOVER_PUBLIC_IP"), "true", StringComparison.OrdinalIgnoreCase);
    }
}
