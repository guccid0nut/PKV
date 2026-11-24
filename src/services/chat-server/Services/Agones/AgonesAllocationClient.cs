// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesAllocationClient.cs
// System: TRANSMUTANSTEIN - Chat Server & Matchmaking
// Subsystem: Cloud-Native Fleet Orchestration (Agones Integration)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TRANSMUTANSTEIN.ChatServer.Utilities;

namespace TRANSMUTANSTEIN.ChatServer.Services.Agones;

/// <summary>
///     High-performance HTTP client for Kubernetes Agones GameServerAllocation API.
/// </summary>
public sealed class AgonesAllocationClient : IAgonesAllocationClient
{
    private readonly HttpClient _httpClient;
    private readonly AgonesAllocationOptions _options;
    private readonly ILogger<AgonesAllocationClient> _logger;

    public bool IsEnabled => _options.Enabled;

    public AgonesAllocationClient(
        IConfiguration configuration,
        HttpClient? httpClient = null,
        ILogger<AgonesAllocationClient>? logger = null)
    {
        _logger = logger ?? NullLogger<AgonesAllocationClient>.Instance;
        _options = new AgonesAllocationOptions();

        IConfigurationSection section = configuration.GetSection(AgonesAllocationOptions.SectionName);
        if (section.Exists())
        {
            section.Bind(_options);
        }

        // Allow environment variable overrides
        string? envEnabled = configuration["AGONES_ALLOCATION_ENABLED"] ?? Environment.GetEnvironmentVariable("AGONES_ALLOCATION_ENABLED");
        if (bool.TryParse(envEnabled, out bool enabled))
        {
            _options.Enabled = enabled;
        }

        string? envEndpoint = configuration["AGONES_ALLOCATION_ENDPOINT"] ?? Environment.GetEnvironmentVariable("AGONES_ALLOCATION_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(envEndpoint))
        {
            _options.Endpoint = envEndpoint;
        }

        string? envNamespace = configuration["AGONES_ALLOCATION_NAMESPACE"] ?? Environment.GetEnvironmentVariable("AGONES_ALLOCATION_NAMESPACE");
        if (!string.IsNullOrWhiteSpace(envNamespace))
        {
            _options.Namespace = envNamespace;
        }

        string? envFleet = configuration["AGONES_ALLOCATION_FLEET"] ?? Environment.GetEnvironmentVariable("AGONES_ALLOCATION_FLEET");
        if (!string.IsNullOrWhiteSpace(envFleet))
        {
            _options.FleetName = envFleet;
        }

        _httpClient = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
    }

    public async Task<GameServerAllocationResult> AllocateGameServerAsync(
        IReadOnlyList<string> desiredRegions,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return GameServerAllocationResult.Failed("Agones allocation is disabled.");
        }

        string requestUrl = ResolveEndpointUrl();
        GameServerAllocationRequest requestBody = BuildAllocationRequest(desiredRegions);

        Log.Information("[Agones] Dispatching GameServerAllocation to {Url} (Fleet: {Fleet}, Regions: [{Regions}])",
            requestUrl, _options.FleetName, string.Join(", ", desiredRegions));

        try
        {
            string jsonPayload = JsonSerializer.Serialize(requestBody, AgonesJsonContext.Default.GameServerAllocationRequest);
            using StringContent httpContent = new(jsonPayload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.PostAsync(requestUrl, httpContent, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Log.Warning("[Agones] GameServerAllocation failed with HTTP {StatusCode}: {Error}",
                    (int)response.StatusCode, errorBody);
                return GameServerAllocationResult.Failed($"HTTP {(int)response.StatusCode}: {errorBody}");
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            GameServerAllocationResponse? allocationResponse = JsonSerializer.Deserialize(responseJson, AgonesJsonContext.Default.GameServerAllocationResponse);

            if (allocationResponse?.Status == null)
            {
                Log.Warning("[Agones] GameServerAllocation returned empty or invalid status response: {Response}", responseJson);
                return GameServerAllocationResult.Failed("Empty allocation status returned from Agones");
            }

            GameServerAllocationStatus status = allocationResponse.Status;
            if (!status.IsAllocated)
            {
                Log.Warning("[Agones] GameServerAllocation did not allocate a server. State: {State}", status.State);
                return GameServerAllocationResult.Unallocated($"Agones state: {status.State}");
            }

            int gamePort = 0;
            int voicePort = 0;

            GameServerPort? gamePortObj = status.Ports.FirstOrDefault(p =>
                string.Equals(p.Name, "game-udp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "default", StringComparison.OrdinalIgnoreCase));

            GameServerPort? voicePortObj = status.Ports.FirstOrDefault(p =>
                string.Equals(p.Name, "voice-udp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "voice", StringComparison.OrdinalIgnoreCase));

            if (gamePortObj != null)
            {
                gamePort = gamePortObj.Port;
            }
            else if (status.Ports.Count > 0)
            {
                gamePort = status.Ports[0].Port;
            }

            if (voicePortObj != null)
            {
                voicePort = voicePortObj.Port;
            }

            Log.Information(
                "[Agones] GameServer SUCCESSFULLY ALLOCATED! Pod: {PodName}, Address: {Address}, GamePort: {GamePort}, VoicePort: {VoicePort}",
                status.GameServerName, status.Address, gamePort, voicePort);

            return GameServerAllocationResult.Success(status.GameServerName, status.Address, gamePort, voicePort);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Warning("[Agones] GameServerAllocation timed out or was cancelled.");
            return GameServerAllocationResult.Failed("Allocation request timed out.");
        }
        catch (Exception ex)
        {
            Log.Error("[Agones] GameServerAllocation exception: {Message}", ex.Message);
            return GameServerAllocationResult.Failed($"Allocation exception: {ex.Message}");
        }
    }

    private string ResolveEndpointUrl()
    {
        string endpoint = _options.Endpoint.TrimEnd('/');
        if (endpoint.EndsWith("/gameserverallocations", StringComparison.OrdinalIgnoreCase) ||
            endpoint.EndsWith("/gameserverallocation", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        return $"{endpoint}/gameserverallocation";
    }

    private GameServerAllocationRequest BuildAllocationRequest(IReadOnlyList<string> desiredRegions)
    {
        GameServerAllocationRequest request = new()
        {
            Namespace = _options.Namespace,
            Spec = new GameServerAllocationSpec
            {
                MultiClusterSetting = new MultiClusterSetting { Enabled = false },
                Preferred = new List<GameServerPreferredSelector>(),
                Selectors = new List<GameServerSelector>()
            }
        };

        // 1. Regional Preferred Selectors (Priority 1..N)
        int priority = 1;
        foreach (string region in desiredRegions.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            GameServerPreferredSelector regionalPreferred = new()
            {
                Priority = priority++,
                MatchLabels = new Dictionary<string, string>
                {
                    ["agones.dev/fleet"] = _options.FleetName,
                    ["region"] = region.ToLowerInvariant()
                }
            };
            request.Spec.Preferred.Add(regionalPreferred);

            GameServerSelector regionalSelector = new()
            {
                MatchLabels = new Dictionary<string, string>
                {
                    ["agones.dev/fleet"] = _options.FleetName,
                    ["region"] = region.ToLowerInvariant()
                }
            };
            request.Spec.Selectors.Add(regionalSelector);
        }

        // 2. Base Fleet fallback selector (allocates from any node in fleet if regional buffer exhausted)
        if (_options.FallbackToAnyRegion || desiredRegions.Count == 0)
        {
            request.Spec.Selectors.Add(new GameServerSelector
            {
                MatchLabels = new Dictionary<string, string>
                {
                    ["agones.dev/fleet"] = _options.FleetName
                }
            });

            request.Spec.Required = new GameServerSelector
            {
                MatchLabels = new Dictionary<string, string>
                {
                    ["agones.dev/fleet"] = _options.FleetName
                }
            };
        }

        return request;
    }
}
