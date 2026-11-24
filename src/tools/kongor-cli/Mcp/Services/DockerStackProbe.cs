// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

public sealed record DockerComposeServiceState(
    string Service,
    string ContainerName,
    string State,
    string Health,
    string StatusText)
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    public string DisplaySummary =>
        string.IsNullOrWhiteSpace(Health) ? State : $"{State}, {Health}";
}

public sealed record DockerCompelFleetSnapshot(
    int ControlPort,
    string Tier,
    bool Reachable,
    bool ManagerRunning,
    int ActiveWorkerCount,
    string? HostAccount);

public sealed class DockerComposeStackSnapshot
{
    public static DockerComposeStackSnapshot Inactive(bool daemonAvailable = false) => new()
    {
        IsDockerDaemonAvailable = daemonAvailable,
        Services = new Dictionary<string, DockerComposeServiceState>(StringComparer.OrdinalIgnoreCase)
    };

    public bool IsDockerDaemonAvailable { get; init; }

    public string? PerformancePreset { get; init; }

    public required IReadOnlyDictionary<string, DockerComposeServiceState> Services { get; init; }

    public DockerCompelFleetSnapshot? MatchFleet { get; init; }

    public DockerCompelFleetSnapshot? ArcadeFleet { get; init; }

    public bool IsStackActive => Services.Values.Any(static s => s.IsRunning);

    public int RunningContainerCount => Services.Values.Count(static s => s.IsRunning);

    public int TotalContainerCount => Services.Count;

    public int TotalActiveGameWorkers =>
        (MatchFleet?.ActiveWorkerCount ?? 0) + (ArcadeFleet?.ActiveWorkerCount ?? 0);

    public DockerComposeServiceState? TryGetRunningService(string composeServiceName) =>
        Services.TryGetValue(composeServiceName, out DockerComposeServiceState? svc) && svc.IsRunning
            ? svc
            : null;
}

/// <summary>
/// Fast, non-blocking inspector and runner for the turnkey <c>kongor-stack</c> Docker Compose topology
/// (<c>infra/docker/docker-compose.stack.yml</c>). Shared across <c>kongor status</c>, <c>kongor doctor</c>,
/// TAO MCP (<see cref="LifecycleService"/>, <see cref="CompelManageHandler"/>), and NEXUS Control Panel GUI.
/// </summary>
public static class DockerStackProbe
{
    public const string ComposeProjectName = "kongor-stack";
    public const int MatchCompelPort = 11334;
    public const int ArcadeCompelPort = 11484;

    private static readonly HttpClient SharedHttp =
        ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromMilliseconds(1200));

    public static string FindRepoRoot(string? startDirectory = null)
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(
                out var ws,
                new Kongor.Core.Configuration.WorkspaceResolveOptions
                {
                    InvocationCwd = !string.IsNullOrWhiteSpace(startDirectory) ? startDirectory : Directory.GetCurrentDirectory()
                }) && ws is not null)
        {
            return ws.Root;
        }

        string? current = string.IsNullOrWhiteSpace(startDirectory) ? AppContext.BaseDirectory : startDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "kongor.ps1")) ||
                File.Exists(Path.Combine(current, "ProjectKongor.slnx")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Directory.GetCurrentDirectory();
    }

    public static bool IsDockerEngineAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(@"\\.\pipe\docker_engine");
        }

        return File.Exists("/var/run/docker.sock");
    }

    public static string? TryReadActivePerformancePreset(string repoRoot) =>
        TryReadRuntimeEnvValue(repoRoot, "PERFORMANCE_PRESET");

    public static string? TryReadRuntimeEnvValue(string repoRoot, string key)
    {
        try
        {
            string envStackPath = Path.Combine(repoRoot, "userdata", "docker", ".env.stack");
            if (!File.Exists(envStackPath))
            {
                return null;
            }

            foreach (string rawLine in File.ReadLines(envStackPath))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                int eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0) continue;

                string k = trimmed[..eqIdx].Trim();
                if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                string v = trimmed[(eqIdx + 1)..].Trim();
                if (v.Length >= 2 && ((v[0] == '\'' && v[^1] == '\'') || (v[0] == '"' && v[^1] == '"')))
                {
                    v = v[1..^1];
                }

                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DockerStackProbe] Ignored .env.stack read error for '{key}': {ex.Message}");
        }

        return null;
    }

    public static DockerComposeStackSnapshot ParseComposePsJson(
        string? rawJson,
        string? performancePreset = null,
        bool daemonAvailable = true,
        DockerCompelFleetSnapshot? matchFleet = null,
        DockerCompelFleetSnapshot? arcadeFleet = null)
    {
        Dictionary<string, DockerComposeServiceState> map = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new DockerComposeStackSnapshot
            {
                IsDockerDaemonAvailable = daemonAvailable,
                PerformancePreset = performancePreset,
                Services = map,
                MatchFleet = matchFleet,
                ArcadeFleet = arcadeFleet
            };
        }

        string trimmed = rawJson.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        AddParsedEntry(map, item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerStackProbe] Failed parsing JSON array output from docker compose ps: {ex.Message}");
            }
        }
        else
        {
            foreach (string line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith('{')) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        AddParsedEntry(map, doc.RootElement);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DockerStackProbe] Ignored malformed NDJSON line in docker compose ps: {ex.Message}");
                }
            }
        }

        return new DockerComposeStackSnapshot
        {
            IsDockerDaemonAvailable = daemonAvailable,
            PerformancePreset = performancePreset,
            Services = map,
            MatchFleet = matchFleet,
            ArcadeFleet = arcadeFleet
        };
    }

    private static void AddParsedEntry(Dictionary<string, DockerComposeServiceState> map, JsonElement item)
    {
        string service = GetJsonString(item, "Service");
        string name = GetJsonString(item, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = GetJsonString(item, "Names");
        }

        if (string.IsNullOrWhiteSpace(service) && !string.IsNullOrWhiteSpace(name))
        {
            service = InferServiceFromContainerName(name);
        }

        if (string.IsNullOrWhiteSpace(service))
        {
            return;
        }

        string state = GetJsonString(item, "State");
        string health = GetJsonString(item, "Health");
        string statusText = GetJsonString(item, "Status");

        // Exclude completed one-shot init containers (e.g. volume-init with Exited (0)) so
        // RunningContainerCount / TotalContainerCount accurately reflects long-lived services (8/8).
        // Failed init containers (non-zero exit) remain visible so operators see bootstrap errors.
        if (service.Equals("volume-init", StringComparison.OrdinalIgnoreCase) &&
            state.Equals("exited", StringComparison.OrdinalIgnoreCase) &&
            statusText.StartsWith("Exited (0)", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        map[service] = new DockerComposeServiceState(
            service,
            string.IsNullOrWhiteSpace(name) ? $"{ComposeProjectName}-{service}" : name,
            string.IsNullOrWhiteSpace(state) ? "unknown" : state,
            health,
            statusText);
    }

    private static string InferServiceFromContainerName(string containerName)
    {
        string normalized = containerName.Trim().TrimStart('/');
        if (normalized.Equals("kongor-stack-compel-arcade", StringComparison.OrdinalIgnoreCase))
        {
            return "compel-queue-arcade";
        }

        const string prefix = "kongor-stack-";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static string GetJsonString(JsonElement obj, string propName)
    {
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(propName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool IsProbeSuppressedByEnvironment()
    {
        return string.Equals(Environment.GetEnvironmentVariable("KONGOR_HEADLESS_RENDER"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("KONGOR_DISABLE_DOCKER_PROBE"), "1", StringComparison.Ordinal);
    }

    public static async Task<DockerComposeStackSnapshot> ProbeAsync(
        string? repoRoot = null,
        bool includeFleetHttpStatus = true,
        CancellationToken cancellationToken = default)
    {
        if (GameServerManageHandler.IsTestExecution || IsProbeSuppressedByEnvironment())
        {
            Debug.WriteLine("[DockerStackProbe] ProbeAsync skipped (test execution or headless render suppression active).");
            return DockerComposeStackSnapshot.Inactive(daemonAvailable: false);
        }

        bool daemonAvailable = IsDockerEngineAvailable();
        if (!daemonAvailable)
        {
            return DockerComposeStackSnapshot.Inactive(daemonAvailable: false);
        }

        string resolvedRepo = FindRepoRoot(repoRoot);
        string composeFile = Path.Combine(resolvedRepo, "infra", "docker", "docker-compose.stack.yml");
        if (!File.Exists(composeFile))
        {
            return DockerComposeStackSnapshot.Inactive(daemonAvailable: true);
        }

        string? preset = TryReadActivePerformancePreset(resolvedRepo);
        string rawPsOutput = string.Empty;

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "docker",
                WorkingDirectory = resolvedRepo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("compose");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(composeFile);
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add("local-postgres");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(ComposeProjectName);
            psi.ArgumentList.Add("ps");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("json");

            using Process? proc = Process.Start(psi);
            if (proc != null)
            {
                using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(1500));
                using CancellationTokenSource linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
                    Task<string> stderrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);
                    await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(linkedCts.Token)).ConfigureAwait(false);
                    rawPsOutput = await stdoutTask.ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(500);
                        }
                    }
                    catch (Exception killEx)
                    {
                        Debug.WriteLine($"[DockerStackProbe] Failed to terminate timed-out docker compose process tree: {killEx.Message}");
                    }

                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DockerStackProbe] docker compose ps probe timed out or failed: {ex.Message}");
            return DockerComposeStackSnapshot.Inactive(daemonAvailable: true);
        }

        DockerComposeStackSnapshot initial = ParseComposePsJson(rawPsOutput, preset, daemonAvailable: true);
        if (!includeFleetHttpStatus || !initial.IsStackActive)
        {
            return initial;
        }

        string? token = CompelManageHandler.TryResolveAuthToken();
        string? matchHost = TryReadRuntimeEnvValue(resolvedRepo, "MATCH_HOST_USERNAME");
        string? arcadeHost = TryReadRuntimeEnvValue(resolvedRepo, "ARCADE_HOST_USERNAME");

        Task<DockerCompelFleetSnapshot?> matchTask = initial.TryGetRunningService("compel-dedicated") != null
            ? ProbeCompelFleetAsync(MatchCompelPort, "D", token, matchHost, cancellationToken)
            : Task.FromResult<DockerCompelFleetSnapshot?>(null);

        Task<DockerCompelFleetSnapshot?> arcadeTask = initial.TryGetRunningService("compel-queue-arcade") != null
            ? ProbeCompelFleetAsync(ArcadeCompelPort, "Q", token, arcadeHost, cancellationToken)
            : Task.FromResult<DockerCompelFleetSnapshot?>(null);

        await Task.WhenAll(matchTask, arcadeTask).ConfigureAwait(false);

        return new DockerComposeStackSnapshot
        {
            IsDockerDaemonAvailable = true,
            PerformancePreset = preset,
            Services = initial.Services,
            MatchFleet = await matchTask.ConfigureAwait(false),
            ArcadeFleet = await arcadeTask.ConfigureAwait(false)
        };
    }

    private static async Task<DockerCompelFleetSnapshot?> ProbeCompelFleetAsync(
        int port,
        string tier,
        string? authToken,
        string? hostAccount,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(1000));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            using HttpRequestMessage statusReq = new(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                statusReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            }

            using HttpResponseMessage resp = await SharedHttp.SendAsync(statusReq, linked.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                int workers = 0;
                if ((TryGetPropertyIgnoreCase(root, "activeWorkers", out JsonElement wEl) ||
                     TryGetPropertyIgnoreCase(root, "activeWorkerCount", out wEl)) &&
                    wEl.TryGetInt32(out int parsedWorkers))
                {
                    workers = parsedWorkers;
                }
                else if (TryGetPropertyIgnoreCase(root, "workers", out JsonElement workersArr) &&
                         workersArr.ValueKind == JsonValueKind.Array)
                {
                    workers = workersArr.GetArrayLength();
                }

                bool mgrRunning = TryGetPropertyIgnoreCase(root, "managerRunning", out JsonElement mEl) &&
                                  mEl.ValueKind is JsonValueKind.True;

                return new DockerCompelFleetSnapshot(port, tier, Reachable: true, mgrRunning, workers, hostAccount);
            }

            using HttpResponseMessage pingResp =
                await SharedHttp.GetAsync($"http://127.0.0.1:{port}/ping", linked.Token).ConfigureAwait(false);
            if (pingResp.IsSuccessStatusCode)
            {
                return new DockerCompelFleetSnapshot(port, tier, Reachable: true, ManagerRunning: true, ActiveWorkerCount: 0, hostAccount);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DockerStackProbe] COMPEL fleet probe on :{port} failed: {ex.Message}");
        }

        return new DockerCompelFleetSnapshot(port, tier, Reachable: false, ManagerRunning: false, ActiveWorkerCount: 0, hostAccount);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static async Task<(int ExitCode, string Output)> RunStackScriptAsync(
        string action,
        IEnumerable<string>? extraArgs = null,
        string? repoRoot = null,
        Action<string, bool>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        string resolvedRepo = FindRepoRoot(repoRoot);
        string scriptPath = Path.Combine(resolvedRepo, "scripts", "docker", "launch_docker_stack.ps1");
        if (!File.Exists(scriptPath))
        {
            string missingMsg = $"Docker stack orchestrator script not found at '{scriptPath}'. Verify workspace checkout.";
            onLine?.Invoke(missingMsg, true);
            return (1, missingMsg);
        }

        string shellExe = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        ProcessStartInfo psi = new()
        {
            FileName = shellExe,
            WorkingDirectory = resolvedRepo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(action);

        if (extraArgs != null)
        {
            foreach (string arg in extraArgs)
            {
                if (!string.IsNullOrWhiteSpace(arg))
                {
                    psi.ArgumentList.Add(arg);
                }
            }
        }

        StringBuilder combined = new();
        object sync = new();

        using Process? process = Process.Start(psi);
        if (process == null)
        {
            string failMsg = $"Failed to launch '{shellExe}' for '{scriptPath}'.";
            onLine?.Invoke(failMsg, true);
            return (1, failMsg);
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (sync) { combined.AppendLine(e.Data); }
            onLine?.Invoke(e.Data, false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (sync) { combined.AppendLine(e.Data); }
            onLine?.Invoke(e.Data, true);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Debug.WriteLine($"[DockerStackProbe] Ignored cancellation kill error: {killEx.Message}");
            }

            throw;
        }

        return (process.ExitCode, combined.ToString());
    }
}
