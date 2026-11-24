// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.Http;
using NEXUS.Contracts.Fleet;
using NEXUS.Contracts.Security;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class GameServerFleetService(
    ISecretsManagerService? secretsManager = null,
    HttpClient? httpClient = null,
    Action<string, string, bool>? logSink = null) : IGameServerFleetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient DefaultStreamingHttpClient = SharedSocketsHttpClientFactory.Create(Timeout.InfiniteTimeSpan);
    private readonly HttpClient _httpClient = httpClient ?? SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(8));
    private readonly HttpClient _streamingClient = httpClient ?? DefaultStreamingHttpClient;
    private readonly Action<string, string, bool>? _logSink = logSink;
    private readonly ISecretsManagerService? _secretsManager = secretsManager;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EndpointState> _endpointStates = new(StringComparer.OrdinalIgnoreCase);

    private sealed class EndpointState
    {
        public bool IsOnline { get; set; }
        public string LastError { get; set; } = string.Empty;
        public DateTimeOffset LastLogTime { get; set; } = DateTimeOffset.MinValue;
        public int SuppressedCount { get; set; }
    }

    public bool IsEndpointKnownOffline(string hostAddress, int port)
    {
        int controlPort = port > 0 ? port : 11234;
        string hostStatusKey = $"http://{hostAddress}:{controlPort}/status";
        if (_endpointStates.TryGetValue(hostStatusKey, out EndpointState? state))
        {
            lock (state)
            {
                return state.LastLogTime != DateTimeOffset.MinValue && !state.IsOnline;
            }
        }

        return false;
    }

    private void RecordEndpointSuccess(string key, string successMessage)
    {
        var state = _endpointStates.GetOrAdd(key, _ => new EndpointState());
        lock (state)
        {
            if (!state.IsOnline)
            {
                state.IsOnline = true;
                state.LastError = string.Empty;
                state.LastLogTime = TimeProvider.System.GetUtcNow();
                int suppressed = state.SuppressedCount;
                state.SuppressedCount = 0;
                string extra = suppressed > 0 ? $" (recovered after {suppressed} offline polls)" : string.Empty;
                Log($"{successMessage}{extra}", false);
            }
        }
    }

    private void RecordEndpointFailure(string key, string errorMessage, bool isDiscoveryProbe = false)
    {
        if (isDiscoveryProbe)
        {
            // Do not spam logs during speculative discovery of unconfigured ports
            return;
        }

        var state = _endpointStates.GetOrAdd(key, _ => new EndpointState());
        lock (state)
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            bool isFirstEncounter = state.LastLogTime == DateTimeOffset.MinValue;

            if (state.IsOnline)
            {
                // Transitioned from Online to Offline
                state.IsOnline = false;
                state.LastError = errorMessage;
                state.LastLogTime = now;
                state.SuppressedCount = 0;
                Log($"Host {key} is unreachable (offline): {errorMessage}", false);
            }
            else if (isFirstEncounter)
            {
                // First time encountering this offline host
                state.IsOnline = false;
                state.LastError = errorMessage;
                state.LastLogTime = now;
                state.SuppressedCount = 0;
                Log($"Host {key} is unreachable (offline): {errorMessage}. Suppressing duplicate polling logs.", false);
            }
            else
            {
                state.SuppressedCount++;
                // If error message changed or more than 5 minutes elapsed, log periodic heartbeat
                if (!string.Equals(state.LastError, errorMessage, StringComparison.Ordinal) ||
                    (now - state.LastLogTime).TotalMinutes >= 5)
                {
                    state.LastError = errorMessage;
                    state.LastLogTime = now;
                    string suppressedNotice = state.SuppressedCount > 1
                        ? $" ({state.SuppressedCount} duplicate offline polls suppressed)"
                        : string.Empty;
                    Log($"Host {key} remains offline: {errorMessage}{suppressedNotice}", false);
                }
            }
        }
    }

    public async Task<List<GameServerHostEntry>> GetDiscoveredHostsAsync(CancellationToken cancellationToken = default)
    {
        List<GameServerHostEntry> hosts = [];
        string fallbackToken = string.Empty;
        int defaultPort = 11234;
        string configuredHostUserName = string.Empty;

        if (_secretsManager != null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken) &&
                    secrets.CompelAuthenticationToken != "...")
                    fallbackToken = secrets.CompelAuthenticationToken.Trim();
                if (secrets.CompelPingPort > 0)
                    defaultPort = secrets.CompelPingPort;
                if (!string.IsNullOrWhiteSpace(secrets.HostAccountUserName))
                    configuredHostUserName = secrets.HostAccountUserName.Trim();
            }
            catch { }
        }

        string baseUrl = "http://127.0.0.1:5200";
        try
        {
            baseUrl = await ResolveBaseApiUrlAsync(cancellationToken);
            string url = $"{baseUrl}/api/admin/fleet/hosts";

            string internalKey = await ResolveInternalServiceKeyAsync(cancellationToken);
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(SecurityConstants.InternalServiceKeyHeader, internalKey);

            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                RecordEndpointSuccess(url, "API gateway connected successfully for registered host applications.");
                List<ApprovedHostDto>? approvedHosts =
                    await resp.Content.ReadFromJsonAsync<List<ApprovedHostDto>>(JsonOptions, cancellationToken);
                if (approvedHosts != null && approvedHosts.Count > 0)
                {
                    // Deduplicate approved hosts by endpoint (HostAddress:Port).
                    // If multiple hosts map to the same endpoint (e.g. 127.0.0.1:11234),
                    // prioritize the host that matches configuredHostUserName (e.g. "guccihost")
                    // and discard generic/unmatched placeholders (e.g. "HOST").
                    var groupedHosts = approvedHosts.GroupBy(h =>
                    {
                        string addr = !string.IsNullOrWhiteSpace(h.HostIpAddress) ? h.HostIpAddress.Trim() : "127.0.0.1";
                        int p = h.Port > 0 && h.Port != 8080 ? h.Port : defaultPort;
                        return $"{addr}:{p}";
                    });

                    foreach (var group in groupedHosts)
                    {
                        ApprovedHostDto selected = group.FirstOrDefault(h =>
                            !string.IsNullOrWhiteSpace(configuredHostUserName) &&
                            (string.Equals(h.HostAccountName, configuredHostUserName, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(h.ServerName, configuredHostUserName, StringComparison.OrdinalIgnoreCase)))
                            ?? group.FirstOrDefault(h => !string.Equals(h.HostAccountName, "HOST", StringComparison.OrdinalIgnoreCase))
                            ?? group.First();

                        string rawName = !string.IsNullOrWhiteSpace(selected.HostAccountName)
                            ? selected.HostAccountName
                            : selected.ServerName;
                        int hostPort = selected.Port > 0 && selected.Port != 8080 ? selected.Port : defaultPort;

                        string hostName;
                        if (hostPort == defaultPort)
                        {
                            hostName = rawName.Contains("Local", StringComparison.OrdinalIgnoreCase)
                                ? rawName
                                : $"Local Game Server ({rawName})";
                        }
                        else if (hostPort is 11334 or 11244)
                        {
                            hostName = rawName.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                                ? rawName
                                : $"🐳 Docker Container ({rawName})";
                        }
                        else if (hostPort is 11434 or 11254)
                        {
                            hostName = rawName.Contains("K3s", StringComparison.OrdinalIgnoreCase) || rawName.Contains("K8s", StringComparison.OrdinalIgnoreCase)
                                ? rawName
                                : $"☸️ K3s Cluster ({rawName})";
                        }
                        else if (hostPort is 11484 or 11483)
                        {
                            hostName = rawName.Contains("Arcade", StringComparison.OrdinalIgnoreCase)
                                ? rawName
                                : $"🕹️ Queue Arcade ({rawName})";
                        }
                        else
                        {
                            hostName = rawName;
                        }

                        string location = string.IsNullOrWhiteSpace(selected.ServerLocation)
                            ? "USE"
                            : selected.ServerLocation.ToUpperInvariant();

                        hosts.Add(new GameServerHostEntry
                        {
                            Id = $"host-app-{selected.ApplicationId}",
                            Name = hostName,
                            HostAddress = !string.IsNullOrWhiteSpace(selected.HostIpAddress) ? selected.HostIpAddress : "127.0.0.1",
                            Port = hostPort,
                            Location = location,
                            AuthToken = fallbackToken,
                            IsLocal = true
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RecordEndpointFailure($"{baseUrl}/api/admin/fleet/hosts", ex.Message, isDiscoveryProbe: false);
        }

        // Multi-Environment Adaptive Discovery:
        // Probe 127.0.0.1 and [::1] (IPv6 loopback Docker Desktop) to detect running Docker containers
        // alongside or independently of registered local hosts.
        try
        {
            var ipv4Status = await ProbeHostStatusQuietAsync("127.0.0.1", defaultPort, fallbackToken, cancellationToken);
            var ipv6Status = await ProbeHostStatusQuietAsync("[::1]", defaultPort, fallbackToken, cancellationToken);

            bool ipv4IsContainer = ipv4Status.Success && ipv4Status.Status?.OperatingSystem?.Contains("Linux", StringComparison.OrdinalIgnoreCase) == true;
            bool ipv6IsContainer = ipv6Status.Success && ipv6Status.Status?.OperatingSystem?.Contains("Linux", StringComparison.OrdinalIgnoreCase) == true;

            // Check if 127.0.0.1 is already in hosts
            var existingIpv4 = hosts.FirstOrDefault(h => h.HostAddress == "127.0.0.1" && h.Port == defaultPort);
            if (existingIpv4 != null)
            {
                if (ipv4IsContainer)
                {
                    existingIpv4.Name = "🐳 Docker Container (COMPEL)";
                    if (!string.IsNullOrWhiteSpace(ipv4Status.Status?.Location))
                        existingIpv4.Location = ipv4Status.Status.Location;
                }
            }
            else if (ipv4Status.Success)
            {
                hosts.Add(new GameServerHostEntry
                {
                    Id = ipv4IsContainer ? "docker-compel-01" : "local-compel-01",
                    Name = ipv4IsContainer ? "🐳 Docker Container (COMPEL)" : "💻 Local Windows Server (COMPEL)",
                    HostAddress = "127.0.0.1",
                    Port = defaultPort,
                    Location = string.IsNullOrWhiteSpace(ipv4Status.Status?.Location) ? "USE" : ipv4Status.Status.Location,
                    AuthToken = fallbackToken,
                    IsLocal = true
                });
            }

            // If [::1] is a container and 127.0.0.1 is NOT the same container (e.g. 127.0.0.1 is local Windows), add [::1]
            if (ipv6IsContainer)
            {
                bool isSameAsIpv4 = ipv4IsContainer && ipv4Status.Status != null && ipv6Status.Status != null &&
                                    Math.Abs(ipv4Status.Status.UptimeSeconds - ipv6Status.Status.UptimeSeconds) < 2.0;

                if (!isSameAsIpv4 && !hosts.Any(h => h.HostAddress == "[::1]" && h.Port == defaultPort))
                {
                    hosts.Add(new GameServerHostEntry
                    {
                        Id = "docker-compel-02",
                        Name = "🐳 Docker Container (COMPEL)",
                        HostAddress = "[::1]",
                        Port = defaultPort,
                        Location = string.IsNullOrWhiteSpace(ipv6Status.Status?.Location) ? "USE" : ipv6Status.Status.Location,
                        AuthToken = fallbackToken,
                        IsLocal = true
                    });
                }
            }

            // Multi-Fleet Probe: Check Docker control plane port (default offset 100: 11334, or legacy 11244) if not already registered
            if (!hosts.Any(h => h.Port is 11334 or 11244))
            {
                int dockerPort = 11334;
                var dockerStatus = await ProbeHostStatusQuietAsync("127.0.0.1", dockerPort, fallbackToken, cancellationToken);
                string effectiveDockerToken = fallbackToken;
                if (!dockerStatus.Success && dockerStatus.ErrorMessage?.Contains("401") == true && fallbackToken != "devtoken")
                {
                    var retryStatus = await ProbeHostStatusQuietAsync("127.0.0.1", dockerPort, "devtoken", cancellationToken);
                    if (retryStatus.Success)
                    {
                        dockerStatus = retryStatus;
                        effectiveDockerToken = "devtoken";
                    }
                }

                // If 11334 did not respond, probe legacy 11244
                if (!dockerStatus.Success)
                {
                    var legacyStatus = await ProbeHostStatusQuietAsync("127.0.0.1", 11244, fallbackToken, cancellationToken);
                    if (legacyStatus.Success)
                    {
                        dockerStatus = legacyStatus;
                        dockerPort = 11244;
                    }
                }

                if (dockerStatus.Success)
                {
                    hosts.Add(new GameServerHostEntry
                    {
                        Id = $"docker-compel-{dockerPort}",
                        Name = "🐳 Docker Container (COMPEL)",
                        HostAddress = "127.0.0.1",
                        Port = dockerPort,
                        Location = string.IsNullOrWhiteSpace(dockerStatus.Status?.Location) ? "USE" : dockerStatus.Status.Location,
                        AuthToken = effectiveDockerToken,
                        IsLocal = true
                    });
                }
            }

            // Multi-Fleet Probe: Check K3s control plane port (default offset 200: 11434, or legacy 11254) if not already registered
            if (!hosts.Any(h => h.Port is 11434 or 11254))
            {
                int k3sPort = 11434;
                var k3sStatus = await ProbeHostStatusQuietAsync("127.0.0.1", k3sPort, fallbackToken, cancellationToken);
                string effectiveK3sToken = fallbackToken;
                if (!k3sStatus.Success && k3sStatus.ErrorMessage?.Contains("401") == true && fallbackToken != "devtoken")
                {
                    var retryStatus = await ProbeHostStatusQuietAsync("127.0.0.1", k3sPort, "devtoken", cancellationToken);
                    if (retryStatus.Success)
                    {
                        k3sStatus = retryStatus;
                        effectiveK3sToken = "devtoken";
                    }
                }

                // If 11434 did not respond, probe legacy 11254
                if (!k3sStatus.Success)
                {
                    var legacyStatus = await ProbeHostStatusQuietAsync("127.0.0.1", 11254, fallbackToken, cancellationToken);
                    if (legacyStatus.Success)
                    {
                        k3sStatus = legacyStatus;
                        k3sPort = 11254;
                    }
                }

                if (k3sStatus.Success)
                {
                    hosts.Add(new GameServerHostEntry
                    {
                        Id = $"k3s-compel-{k3sPort}",
                        Name = "☸️ K3s Cluster (COMPEL)",
                        HostAddress = "127.0.0.1",
                        Port = k3sPort,
                        Location = string.IsNullOrWhiteSpace(k3sStatus.Status?.Location) ? "USE" : k3sStatus.Status.Location,
                        AuthToken = effectiveK3sToken,
                        IsLocal = true
                    });
                }
            }

            // Multi-Fleet Probe: Check Queue Arcade Tier Q control plane port (default offset 250: 11484) if not already registered
            if (!hosts.Any(h => h.Port is 11484 or 11483))
            {
                int arcadePort = 11484;
                (bool Success, CompelStatusResponseDto? Status, string? ErrorMessage) arcadeStatus =
                    await ProbeHostStatusQuietAsync("127.0.0.1", arcadePort, fallbackToken, cancellationToken);
                string effectiveArcadeToken = fallbackToken;
                if (!arcadeStatus.Success && arcadeStatus.ErrorMessage?.Contains("401") == true && fallbackToken != "devtoken")
                {
                    (bool Success, CompelStatusResponseDto? Status, string? ErrorMessage) retryArcadeStatus =
                        await ProbeHostStatusQuietAsync("127.0.0.1", arcadePort, "devtoken", cancellationToken);
                    if (retryArcadeStatus.Success)
                    {
                        arcadeStatus = retryArcadeStatus;
                        effectiveArcadeToken = "devtoken";
                    }
                }

                if (arcadeStatus.Success)
                {
                    hosts.Add(new GameServerHostEntry
                    {
                        Id = $"arcade-compel-{arcadePort}",
                        Name = "🕹️ Queue Arcade Server (COMPEL)",
                        HostAddress = "127.0.0.1",
                        Port = arcadePort,
                        Location = string.IsNullOrWhiteSpace(arcadeStatus.Status?.Location) ? "USE" : arcadeStatus.Status.Location,
                        AuthToken = effectiveArcadeToken,
                        IsLocal = true
                    });
                }
            }
        }
        catch { }

        // Ensure primary local host (127.0.0.1:11234) is always present as the first entry
        if (!hosts.Any(h => h.HostAddress == "127.0.0.1" && h.Port == defaultPort))
        {
            hosts.Insert(0, new GameServerHostEntry
            {
                Id = "local-compel-01",
                Name = "Local Game Server (COMPEL)",
                HostAddress = "127.0.0.1",
                Port = defaultPort,
                Location = "USE",
                AuthToken = fallbackToken,
                IsLocal = true
            });
        }

        // Sort so that primary local host (127.0.0.1:11234) is guaranteed at index 0, followed by Docker (11334), K3s (11434), then any remote hosts
        hosts = hosts
            .OrderBy(h => (h.HostAddress == "127.0.0.1" && h.Port == defaultPort) ? 0 : (h.Port > 0 ? h.Port : 99999))
            .ToList();

        return hosts;
    }

    public async Task<(bool Success, long LatencyMs, CompelPingResponseDto? Response)> PingHostAsync(string hostAddress,
        int port, CancellationToken cancellationToken = default)
    {
        int targetPort = port > 0 ? port : 11234;
        string url = $"http://{hostAddress}:{targetPort}/ping";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                CompelPingResponseDto? dto = JsonSerializer.Deserialize<CompelPingResponseDto>(content, JsonOptions);
                RecordEndpointSuccess(url, $"Host {hostAddress}:{targetPort} ping succeeded ({sw.ElapsedMilliseconds} ms).");
                return (true, sw.ElapsedMilliseconds, dto);
            }

            string err = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            RecordEndpointFailure(url, err, isDiscoveryProbe: false);
            return (false, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordEndpointFailure(url, ex.Message, isDiscoveryProbe: false);
            return (false, sw.ElapsedMilliseconds, null);
        }
    }

    private Task<(bool Success, CompelStatusResponseDto? Status, string? ErrorMessage)> ProbeHostStatusQuietAsync(
        string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default)
    {
        return GetHostStatusCoreAsync(hostAddress, port, authToken, isDiscoveryProbe: true, cancellationToken);
    }

    public Task<(bool Success, CompelStatusResponseDto? Status, string? ErrorMessage)> GetHostStatusAsync(
        string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default)
    {
        return GetHostStatusCoreAsync(hostAddress, port, authToken, isDiscoveryProbe: false, cancellationToken);
    }

    private async Task<(bool Success, CompelStatusResponseDto? Status, string? ErrorMessage)> GetHostStatusCoreAsync(
        string hostAddress, int port, string? authToken, bool isDiscoveryProbe, CancellationToken cancellationToken)
    {
        int targetPort = port > 0 ? port : 11234;
        string url = $"http://{hostAddress}:{targetPort}/status";
        string token = await ResolveAuthTokenAsync(authToken, cancellationToken);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                CompelStatusResponseDto? dto =
                    JsonSerializer.Deserialize<CompelStatusResponseDto>(content, JsonOptions);
                RecordEndpointSuccess(url, isDiscoveryProbe
                    ? $"[DISCOVERY] Discovered active fleet endpoint at {url} ({dto?.OperatingSystem ?? "COMPEL"})."
                    : $"Host {hostAddress}:{targetPort} status OK ({dto?.OperatingSystem ?? "COMPEL"}).");
                return (true, dto, null);
            }

            string err = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            RecordEndpointFailure(url, err, isDiscoveryProbe);
            return (false, null, err);
        }
        catch (Exception ex)
        {
            RecordEndpointFailure(url, ex.Message, isDiscoveryProbe);
            return (false, null, ex.Message);
        }
    }

    public Task<(bool Success, string Message)> StartInstancesAsync(string hostAddress, int port,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, "instances/start", authToken, cancellationToken);
    }

    public Task<(bool Success, string Message)> StopInstancesAsync(string hostAddress, int port,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, "instances/stop", authToken, cancellationToken);
    }

    public Task<(bool Success, string Message)> RestartInstancesAsync(string hostAddress, int port,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, "instances/restart", authToken, cancellationToken);
    }

    public Task<(bool Success, string Message)> SyncDistributionAsync(string hostAddress, int port,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, "sync", authToken, cancellationToken);
    }

    public Task<(bool Success, string Message)> StartWorkerAsync(string hostAddress, int port, int targetServerPort,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, $"instances/{targetServerPort}/start", authToken, cancellationToken);
    }

    public Task<(bool Success, string Message)> StopWorkerAsync(string hostAddress, int port, int targetServerPort,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, $"instances/{targetServerPort}/stop", authToken, cancellationToken);
    }

    public Task<(bool Success, string Message)> RestartWorkerAsync(string hostAddress, int port, int targetServerPort,
        string? authToken = null, CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync(hostAddress, port, $"instances/{targetServerPort}/restart", authToken, cancellationToken);
    }

    public async Task<(bool Success, List<CompelDedicatedServerLogLineDto> Logs, string? ErrorMessage)> GetServerLogsAsync(
        string hostAddress, int port, int targetServerPort, int tail = 100, string? authToken = null,
        CancellationToken cancellationToken = default)
    {
        int controlPort = port > 0 ? port : 11234;
        string hostStatusKey = $"http://{hostAddress}:{controlPort}/status";
        string url = $"http://{hostAddress}:{controlPort}/instances/{targetServerPort}/logs?tail={tail}";
        string token = await ResolveAuthTokenAsync(authToken, cancellationToken);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                RecordEndpointSuccess(hostStatusKey, $"Host {hostAddress}:{controlPort} log channel online.");
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                CompelDedicatedServerLogsResponseDto? dto =
                    JsonSerializer.Deserialize<CompelDedicatedServerLogsResponseDto>(content, JsonOptions);
                return (true, dto?.Logs ?? [], null);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                (hostAddress.Equals("127.0.0.1") || hostAddress.Equals("localhost")))
            {
                string altToken = string.Equals(token, "devtoken", StringComparison.Ordinal)
                    ? (!string.IsNullOrWhiteSpace(authToken) ? authToken.Trim() : "devtoken" /* no baked-in token: configure via Secrets & Configs */)
                    : "devtoken";

                using HttpRequestMessage retryReq = new(HttpMethod.Get, url);
                retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", altToken);
                using HttpResponseMessage retryResp = await _httpClient.SendAsync(retryReq, cancellationToken);
                if (retryResp.IsSuccessStatusCode)
                {
                    RecordEndpointSuccess(hostStatusKey, $"Host {hostAddress}:{controlPort} log channel online.");
                    string content = await retryResp.Content.ReadAsStringAsync(cancellationToken);
                    CompelDedicatedServerLogsResponseDto? dto =
                        JsonSerializer.Deserialize<CompelDedicatedServerLogsResponseDto>(content, JsonOptions);
                    return (true, dto?.Logs ?? [], null);
                }
            }

            string err = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            RecordEndpointFailure(hostStatusKey, err, isDiscoveryProbe: false);
            return (false, [], err);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, [], "Canceled");
        }
        catch (Exception ex)
        {
            RecordEndpointFailure(hostStatusKey, ex.Message, isDiscoveryProbe: false);
            return (false, [], ex.Message);
        }
    }

    public async IAsyncEnumerable<CompelDedicatedServerLogLineDto> StreamServerLogsAsync(
        string hostAddress, int port, int targetServerPort, int tail = 50, string? authToken = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int controlPort = port > 0 ? port : 11234;
        string hostStatusKey = $"http://{hostAddress}:{controlPort}/status";
        string url = $"http://{hostAddress}:{controlPort}/instances/{targetServerPort}/logs/stream?tail={tail}";
        string token = await ResolveAuthTokenAsync(authToken, cancellationToken).ConfigureAwait(false);

        HttpRequestMessage CreateRequest(string effectiveToken)
        {
            HttpRequestMessage req = new(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(effectiveToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveToken);
            }
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return req;
        }

        HttpResponseMessage? response = null;
        Stream? responseStream = null;
        StreamReader? reader = null;

        try
        {
            try
            {
                HttpRequestMessage request = CreateRequest(token);
                response = await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized &&
                    (hostAddress.Equals("127.0.0.1") || hostAddress.Equals("localhost")))
                {
                    response.Dispose();
                    string altToken = string.Equals(token, "devtoken", StringComparison.Ordinal)
                        ? (!string.IsNullOrWhiteSpace(authToken) ? authToken.Trim() : "devtoken" /* no baked-in token: configure via Secrets & Configs */)
                        : "devtoken";
                    HttpRequestMessage retryReq = CreateRequest(altToken);
                    response = await _streamingClient.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                RecordEndpointFailure(hostStatusKey, ex.Message, isDiscoveryProbe: false);
                yield break;
            }

            if (!response.IsSuccessStatusCode)
            {
                RecordEndpointFailure(hostStatusKey, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", isDiscoveryProbe: false);
                yield break;
            }

            RecordEndpointSuccess(hostStatusKey, $"Host {hostAddress}:{controlPort} SSE log stream online.");
            responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            reader = new StreamReader(responseStream, Encoding.UTF8);

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RecordEndpointFailure(hostStatusKey, ex.Message, isDiscoveryProbe: false);
                    break;
                }

                if (line is null) break;

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    string json = line["data:".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    CompelDedicatedServerLogLineDto? dto = null;
                    try
                    {
                        dto = JsonSerializer.Deserialize<CompelDedicatedServerLogLineDto>(json, JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        Log($"StreamServerLogsAsync JSON parse error: {ex.Message}", true);
                    }

                    if (dto != null)
                    {
                        yield return dto;
                    }
                }
            }
        }
        finally
        {
            reader?.Dispose();
            if (responseStream != null) await responseStream.DisposeAsync().ConfigureAwait(false);
            response?.Dispose();
        }
    }

    public async Task<(bool Success, string Channel, string Message)> SendWorkerCommandAsync(
        string hostAddress, int port, int targetServerPort, string command, string? authToken = null,
        CancellationToken cancellationToken = default)
    {
        int controlPort = port > 0 ? port : 11234;
        string url = $"http://{hostAddress}:{controlPort}/instances/{targetServerPort}/command";
        string token = await ResolveAuthTokenAsync(authToken, cancellationToken);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new CompelExecuteWorkerCommandRequestDto { Command = command })
            };
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                CompelExecuteWorkerCommandResponseDto? dto =
                    JsonSerializer.Deserialize<CompelExecuteWorkerCommandResponseDto>(content, JsonOptions);
                return (dto?.Success ?? true, dto?.Channel ?? "stdin", dto?.Message ?? "Command sent successfully.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                (hostAddress.Equals("127.0.0.1") || hostAddress.Equals("localhost")))
            {
                string altToken = string.Equals(token, "devtoken", StringComparison.Ordinal)
                    ? (!string.IsNullOrWhiteSpace(authToken) ? authToken.Trim() : "devtoken" /* no baked-in token: configure via Secrets & Configs */)
                    : "devtoken";

                using HttpRequestMessage retryReq = new(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(new CompelExecuteWorkerCommandRequestDto { Command = command })
                };
                retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", altToken);
                using HttpResponseMessage retryResp = await _httpClient.SendAsync(retryReq, cancellationToken);
                string retryContent = await retryResp.Content.ReadAsStringAsync(cancellationToken);
                if (retryResp.IsSuccessStatusCode)
                {
                    CompelExecuteWorkerCommandResponseDto? dto =
                        JsonSerializer.Deserialize<CompelExecuteWorkerCommandResponseDto>(retryContent, JsonOptions);
                    return (dto?.Success ?? true, dto?.Channel ?? "stdin", dto?.Message ?? "Command sent successfully.");
                }
            }

            CompelExecuteWorkerCommandResponseDto? errDto = null;
            try
            {
                errDto = JsonSerializer.Deserialize<CompelExecuteWorkerCommandResponseDto>(content, JsonOptions);
            }
            catch { }

            string msg = errDto?.Message ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            return (false, errDto?.Channel ?? "none", msg);
        }
        catch (Exception ex)
        {
            Log($"SendWorkerCommandAsync failed for {url}: {ex.Message}", true);
            return (false, "failed", ex.Message);
        }
    }

    private void Log(string message, bool isError = false)
    {
        _logSink?.Invoke("FLEET", message, isError);
    }

    private async Task<string> ResolveAuthTokenAsync(string? explicitToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken) && explicitToken != "...") return explicitToken.Trim();

        if (_secretsManager != null)
        {
            NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken) &&
                secrets.CompelAuthenticationToken != "...") return secrets.CompelAuthenticationToken.Trim();
        }

        string envToken = Environment.GetEnvironmentVariable("COMPEL_AUTHENTICATION_TOKEN") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(envToken)) return envToken.Trim();

        return "devtoken";
    }

    private async Task<string> ResolveInternalServiceKeyAsync(CancellationToken cancellationToken)
    {
        if (_secretsManager != null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(secrets.InternalServiceKey))
                    return secrets.InternalServiceKey.Trim();
            }
            catch
            {
                // Fallback default
            }
        }

        string? envKey = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey.Trim();

        return SecurityConstants.DefaultInternalServiceKey;
    }

    private async Task<string> ResolveBaseApiUrlAsync(CancellationToken cancellationToken)
    {
        string? envUrl = Environment.GetEnvironmentVariable("WEB_PORTAL_API_URL")
                         ?? Environment.GetEnvironmentVariable("ZORGATH_API_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
            return envUrl.TrimEnd('/');

        int port = 5200;
        if (_secretsManager != null)
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (secrets.WebPortalApiHttpPort > 0 && secrets.WebPortalApiHttpPort != 5100)
                    port = secrets.WebPortalApiHttpPort;
            }
            catch
            {
                // Fallback default
            }

        return $"http://127.0.0.1:{port}";
    }

    private async Task<(bool Success, string Message)> ExecuteActionAsync(string hostAddress, int port, string endpoint,
        string? authToken, CancellationToken cancellationToken)
    {
        int targetPort = port > 0 ? port : 11234;
        string url = $"http://{hostAddress}:{targetPort}/{endpoint.TrimStart('/')}";
        string token = await ResolveAuthTokenAsync(authToken, cancellationToken);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                CompelActionResponseDto? actionResp =
                    JsonSerializer.Deserialize<CompelActionResponseDto>(content, JsonOptions);
                string msg = actionResp?.Message ?? "Command executed successfully";
                return (true, msg);
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return (false, "COMPEL Authentication Token is not configured on remote host.");

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "Unauthorized: Authentication token mismatch.");

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                CompelActionResponseDto? actionResp =
                    JsonSerializer.Deserialize<CompelActionResponseDto>(content, JsonOptions);
                return (false, actionResp?.Message ?? "Conflict: Cannot perform action in current state.");
            }

            return (false, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Log($"Action {endpoint} failed on {url}: {ex.Message}", true);
            return (false, ex.Message);
        }
    }
}
