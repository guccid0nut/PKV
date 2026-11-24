// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_compel_manage' (alias: 'kongor_compel_manager').
/// Controls the COMPEL dedicated server supervisor & REST API on port 11234.
/// Supports actions: start, stop, status, instances.
/// Queries /ping, /status, /instances/start, /instances/stop, and reports active worker pools and health.
/// </summary>
public sealed class CompelManageHandler
{
    private readonly ILifecycleService _lifecycleService;
    private readonly HttpClient _httpClient;
    private readonly bool _hasCustomHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public CompelManageHandler(
        ILifecycleService? lifecycleService = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecycleService = lifecycleService ?? new LifecycleService(_timeProvider);
        _hasCustomHttpClient = httpClient != null;
        _httpClient = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(5));
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CompelManage] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string host = "http://127.0.0.1:11234";
        bool hasExplicitHost = false;
        if (arguments.TryGetProperty("host", out JsonElement hostElem) && !string.IsNullOrWhiteSpace(hostElem.GetString()))
        {
            hasExplicitHost = true;
            host = hostElem.GetString()!.Trim().TrimEnd('/');
            if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = $"http://{host}";
            }
        }
        else if (!await ProbePingAsync(host, cancellationToken).ConfigureAwait(false))
        {
            if (await ProbePingAsync("http://127.0.0.1:11334", cancellationToken).ConfigureAwait(false))
            {
                host = "http://127.0.0.1:11334";
                _logger("Auto-selected containerized COMPEL Match Fleet control plane on http://127.0.0.1:11334 (host :11234 offline).");
            }
            else if (await ProbePingAsync("http://127.0.0.1:11484", cancellationToken).ConfigureAwait(false))
            {
                host = "http://127.0.0.1:11484";
                _logger("Auto-selected containerized COMPEL Queue Arcade control plane on http://127.0.0.1:11484 (host :11234 offline).");
            }
        }

        string? authToken = arguments.TryGetProperty("authToken", out JsonElement tokenElem) && !string.IsNullOrWhiteSpace(tokenElem.GetString())
            ? tokenElem.GetString()!.Trim()
            : TryResolveAuthToken();

        string instancesAction = "query";
        if (arguments.TryGetProperty("instancesAction", out JsonElement instActionElem) && !string.IsNullOrWhiteSpace(instActionElem.GetString()))
        {
            instancesAction = instActionElem.GetString()!.Trim().ToLowerInvariant();
        }

        _logger($"Handling action '{action}' on COMPEL host '{host}' (instancesAction: '{instancesAction}')...");

        try
        {
            switch (action)
            {
                case "start":
                {
                    return await StartCompelAsync(host, authToken, hasExplicitHost, cancellationToken).ConfigureAwait(false);
                }

                case "stop":
                {
                    return await StopCompelAsync(host, authToken, cancellationToken).ConfigureAwait(false);
                }

                case "instances":
                {
                    return await ManageInstancesAsync(host, instancesAction, authToken, cancellationToken).ConfigureAwait(false);
                }

                case "servers":
                {
                    return await GetCompelServersAsync(host, authToken, cancellationToken).ConfigureAwait(false);
                }

                case "proxy-restart":
                case "restart-proxy":
                {
                    return await RestartProxyAsync(host, authToken, cancellationToken).ConfigureAwait(false);
                }

                case "query":
                case "status":
                default:
                {
                    return await GetCompelStatusAsync(host, action, authToken, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception executing compel manage action '{action}': {ex}");
            return ToolCallResult.Error($"Failed executing COMPEL manage action '{action}': {ex.Message}");
        }
    }

    private async Task<ToolCallResult> StartCompelAsync(string host, string? authToken, bool hasExplicitHost, CancellationToken cancellationToken)
    {
        _logger($"Checking if COMPEL REST API is reachable on {host}...");
        bool isReachable = await ProbePingAsync(host, cancellationToken).ConfigureAwait(false);

        if (!isReachable && !hasExplicitHost)
        {
            _logger("COMPEL is not currently reachable on port 11234. Spawning COMPEL supervisor host via LifecycleService...");
            ClusterLifecycleResult startResult = await _lifecycleService.StartAsync("compel", withGameServers: false, null, cancellationToken).ConfigureAwait(false);
            _logger($"COMPEL start command result: {startResult.Status}, message: {startResult.Message}");

            // Wait up to 5 seconds for REST endpoint to become ready
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (await ProbePingAsync(host, cancellationToken).ConfigureAwait(false))
                {
                    isReachable = true;
                    _logger("COMPEL REST API endpoint is now live and healthy!");
                    break;
                }
            }
        }

        if (isReachable)
        {
            // Now request instances start via REST
            _logger("Requesting instance worker pool start via POST /instances/start...");
            HttpResponseMessage? resp = await SendAuthorizedPostAsync($"{host}/instances/start", authToken, cancellationToken).ConfigureAwait(false);
            string detail = resp != null && resp.IsSuccessStatusCode ? "Instances start requested successfully." : "Instance start accepted or pending.";

            ToolCallResult statusResult = await GetCompelStatusAsync(host, "start", authToken, cancellationToken).ConfigureAwait(false);
            return statusResult;
        }

        return ToolCallResult.Json(new
        {
            status = "pending",
            action = "start",
            reachable = false,
            message = "COMPEL startup requested. Service may take a few seconds to bind port 11234.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> StopCompelAsync(string host, string? authToken, CancellationToken cancellationToken)
    {
        _logger("Stopping COMPEL server instances...");
        bool stoppedInstances = false;

        if (await ProbePingAsync(host, cancellationToken).ConfigureAwait(false))
        {
            _logger("Calling POST /instances/stop on COMPEL control plane...");
            HttpResponseMessage? resp = await SendAuthorizedPostAsync($"{host}/instances/stop", authToken, cancellationToken).ConfigureAwait(false);
            if (resp != null && resp.IsSuccessStatusCode)
            {
                stoppedInstances = true;
                _logger("Successfully stopped game server manager instances via REST API.");
            }
        }

        _logger("Stopping COMPEL host process via LifecycleService...");
        ClusterLifecycleResult stopResult = await _lifecycleService.StopAsync("compel", cancellationToken).ConfigureAwait(false);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "stop",
            instancesStopped = stoppedInstances,
            clusterResult = stopResult.Status,
            message = "COMPEL dedicated server supervisor and match instances stopped cleanly.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> ManageInstancesAsync(string host, string instancesAction, string? authToken, CancellationToken cancellationToken)
    {
        _logger($"Executing instances action '{instancesAction}' against {host}...");

        if (string.Equals(instancesAction, "start", StringComparison.OrdinalIgnoreCase))
        {
            HttpResponseMessage? resp = await SendAuthorizedPostAsync($"{host}/instances/start", authToken, cancellationToken).ConfigureAwait(false);
            string body = resp != null ? await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : string.Empty;
            return ToolCallResult.Json(new
            {
                status = resp != null && resp.IsSuccessStatusCode ? "success" : "failed",
                action = "instances",
                instancesAction = "start",
                httpStatusCode = (int?)resp?.StatusCode,
                response = TryParseJson(body),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        else if (string.Equals(instancesAction, "stop", StringComparison.OrdinalIgnoreCase))
        {
            HttpResponseMessage? resp = await SendAuthorizedPostAsync($"{host}/instances/stop", authToken, cancellationToken).ConfigureAwait(false);
            string body = resp != null ? await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : string.Empty;
            return ToolCallResult.Json(new
            {
                status = resp != null && resp.IsSuccessStatusCode ? "success" : "failed",
                action = "instances",
                instancesAction = "stop",
                httpStatusCode = (int?)resp?.StatusCode,
                response = TryParseJson(body),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        else if (string.Equals(instancesAction, "restart", StringComparison.OrdinalIgnoreCase))
        {
            HttpResponseMessage? resp = await SendAuthorizedPostAsync($"{host}/instances/restart", authToken, cancellationToken).ConfigureAwait(false);
            string body = resp != null ? await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : string.Empty;
            return ToolCallResult.Json(new
            {
                status = resp != null && resp.IsSuccessStatusCode ? "success" : "failed",
                action = "instances",
                instancesAction = "restart",
                httpStatusCode = (int?)resp?.StatusCode,
                response = TryParseJson(body),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // Default: query instances and status
        return await GetCompelStatusAsync(host, "instances", authToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> RestartProxyAsync(string host, string? authToken, CancellationToken cancellationToken)
    {
        _logger($"Requesting proxy restart via POST {host}/proxy/restart...");
        HttpResponseMessage? resp = await SendAuthorizedPostAsync($"{host}/proxy/restart", authToken, cancellationToken).ConfigureAwait(false);
        string body = resp != null ? await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : string.Empty;
        return ToolCallResult.Json(new
        {
            status = resp != null && resp.IsSuccessStatusCode ? "success" : "failed",
            action = "proxy-restart",
            httpStatusCode = (int?)resp?.StatusCode,
            response = TryParseJson(body),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> GetCompelServersAsync(string host, string? authToken, CancellationToken cancellationToken)
    {
        _logger($"Querying COMPEL dedicated servers fleet at {host}/api/servers...");

        try
        {
            HttpResponseMessage? resp = await SendAuthorizedGetAsync($"{host}/api/servers", authToken, cancellationToken).ConfigureAwait(false);
            if (resp != null)
            {
                using (resp)
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        JsonNode? serversJson = TryParseJson(body);
                        return ToolCallResult.Json(new
                        {
                            status = "success",
                            action = "servers",
                            host,
                            servers = serversJson,
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        });
                    }

                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        action = "servers",
                        host,
                        httpStatusCode = (int)resp.StatusCode,
                        message = $"Failed to query servers: HTTP {resp.StatusCode}",
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }

            return ToolCallResult.Json(new
            {
                status = "error",
                action = "servers",
                host,
                message = "Failed to connect to COMPEL servers endpoint",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        catch (Exception ex)
        {
            _logger($"Failed to query {host}/api/servers: {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "servers",
                host,
                message = ex.Message,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
    }

    private async Task<ToolCallResult> GetCompelStatusAsync(string host, string actionName = "status", string? authToken = null, CancellationToken cancellationToken = default)
    {
        _logger($"Querying COMPEL health and status at {host}/ping, {host}/status, and {host}/api/servers...");

        bool pingOk = false;
        JsonNode? pingJson = null;
        if (_hasCustomHttpClient || IsLoopbackEndpointListening(host))
        {
            try
            {
                using HttpResponseMessage pingResp = await _httpClient.GetAsync($"{host}/ping", cancellationToken).ConfigureAwait(false);
                if (pingResp.IsSuccessStatusCode)
                {
                    pingOk = true;
                    string pingBody = await pingResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    pingJson = TryParseJson(pingBody);
                    _logger($"Ping check OK: {pingBody}");
                }
            }
            catch (Exception ex)
            {
                _logger($"Ping probe failed: {ex.Message}");
            }
        }

        bool statusOk = false;
        JsonNode? statusJson = null;
        if (pingOk)
        {
            try
            {
                HttpResponseMessage? statResp = await SendAuthorizedGetAsync($"{host}/status", authToken, cancellationToken).ConfigureAwait(false);
                if (statResp != null)
                {
                    using (statResp)
                    {
                        if (statResp.IsSuccessStatusCode)
                        {
                            statusOk = true;
                            string statBody = await statResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            statusJson = TryParseJson(statBody);
                            _logger($"Status endpoint returned: {statBody}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"Status query failed: {ex.Message}");
            }
        }

        JsonNode? serversJson = null;
        if (pingOk)
        {
            try
            {
                HttpResponseMessage? srvResp = await SendAuthorizedGetAsync($"{host}/api/servers", authToken, cancellationToken).ConfigureAwait(false);
                if (srvResp != null)
                {
                    using (srvResp)
                    {
                        if (srvResp.IsSuccessStatusCode)
                        {
                            string srvBody = await srvResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            serversJson = TryParseJson(srvBody);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"Servers query failed: {ex.Message}");
            }
        }

        // Also query lifecycle service status for process state
        ClusterLifecycleResult clusterStatus = await _lifecycleService.GetStatusAsync("compel", false, cancellationToken).ConfigureAwait(false);
        bool processRunning = clusterStatus.Services.TryGetValue("compel", out ServiceStatusInfo? compelInfo) && compelInfo.State == "Running";

        var response = new
        {
            status = pingOk ? "success" : (processRunning ? "starting" : "stopped"),
            action = actionName,
            running = pingOk || processRunning,
            healthy = pingOk && statusOk,
            host,
            restApiLive = pingOk,
            processRunning,
            ping = pingJson,
            nodeStatus = statusJson,
            servers = serversJson,
            ports = new
            {
                controlPlane = Uri.TryCreate(host, UriKind.Absolute, out Uri? parsedHostUri) && parsedHostUri.Port > 0
                    ? parsedHostUri.Port
                    : 11234,
                localGameStart = statusJson?["ports"]?["gameStart"]?.GetValue<int?>() ?? 11235,
                localGameEnd = statusJson?["ports"]?["gameEnd"]?.GetValue<int?>() ?? 11238,
                publicGameStart = statusJson?["ports"]?["publicGameStart"]?.GetValue<int?>() ?? 11235,
                publicGameEnd = statusJson?["ports"]?["publicGameEnd"]?.GetValue<int?>() ?? 11238,
                pingPort = statusJson?["ports"]?["pingPort"]?.GetValue<int?>() ?? 11234
            },
            managerRunning = statusJson?["managerRunning"]?.GetValue<bool?>() ?? false,
            proxyRunning = statusJson?["proxyRunning"]?.GetValue<bool?>() ?? false,
            proxyFailedForwarderCount = statusJson?["proxyFailedForwarderCount"]?.GetValue<int?>() ?? 0,
            proxyDroppedDatagramCount = statusJson?["proxyDroppedDatagramCount"]?.GetValue<long?>() ?? 0,
            proxyIsUnderAttack = statusJson?["proxyIsUnderAttack"]?.GetValue<bool?>() ?? false,
            instancesCount = statusJson?["instances"]?.GetValue<int?>() ?? 0,
            uptimeSeconds = statusJson?["uptimeSeconds"]?.GetValue<double?>() ?? 0.0,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(response);
    }

    private static bool IsLoopbackEndpointListening(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return true;
            if (!DbPoolDiagnosticsHandler.IsLoopbackHost(uri.Host)) return true;
            int port = uri.Port > 0 ? uri.Port : 11234;
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i].Port == port) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task<bool> ProbePingAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            if (!_hasCustomHttpClient && !IsLoopbackEndpointListening(host))
            {
                return false;
            }

            using HttpResponseMessage resp = await _httpClient.GetAsync($"{host}/ping", cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger($"[DEBUG] ProbePingAsync to {host}/ping encountered: {ex.Message}");
            return false;
        }
    }

    private async Task<HttpResponseMessage?> SendAuthorizedGetAsync(string url, string? authToken, CancellationToken cancellationToken)
    {
        try
        {
            if (!_hasCustomHttpClient && !IsLoopbackEndpointListening(url))
            {
                return null;
            }

            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            }

            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger($"[WARN] HTTP GET to {url} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendAuthorizedPostAsync(string url, string? authToken, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            }

            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger($"[WARN] HTTP POST to {url} failed: {ex.Message}");
            return null;
        }
    }

    private static JsonNode? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryResolveAuthToken()
    {
        string? envToken = Environment.GetEnvironmentVariable("COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN")
                           ?? Environment.GetEnvironmentVariable("COMPEL_AUTHENTICATION_TOKEN")
                           ?? Environment.GetEnvironmentVariable("Compel:ControlPlaneAuthenticationToken")
                           ?? Environment.GetEnvironmentVariable("Compel:AuthenticationToken");
        if (!string.IsNullOrWhiteSpace(envToken) && envToken != "...")
            return envToken.Trim();

        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52", "secrets.json");
            if (File.Exists(secretsPath))
            {
                string json = File.ReadAllText(secretsPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Compel:ControlPlaneAuthenticationToken", out JsonElement tokenElem) ||
                    doc.RootElement.TryGetProperty("Compel:AuthenticationToken", out tokenElem) ||
                    doc.RootElement.TryGetProperty("COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN", out tokenElem) ||
                    doc.RootElement.TryGetProperty("COMPEL_AUTHENTICATION_TOKEN", out tokenElem))
                {
                    string? val = tokenElem.GetString();
                    if (!string.IsNullOrWhiteSpace(val) && val != "...")
                        return val.Trim();
                }
            }
        }
        catch { }

        return null;
    }
}
