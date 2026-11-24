// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service for canary deployments of game server images on Kubernetes / Agones.
/// Deploys an isolated standalone GameServer with exact fleet specifications,
/// verifies readiness, promotes via live JSON patch without dirtying git, or rolls back.
/// </summary>
public sealed class CanaryDeployService
{
    private const string FleetYamlRelativePath = "infra/k8s/agones/fleet.yaml";
    private const string CanaryYamlRelativePath = "infra/k8s/agones/canary-gameserver.yaml";
    private const string FleetName = "kongor-dedicated-fleet";

    private readonly string _repoRoot;
    private readonly Action<string> _logger;

    public CanaryDeployService(string? repoRoot = null, Action<string>? logger = null)
    {
        _repoRoot = repoRoot ?? Directory.GetCurrentDirectory();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CanaryDeploy] {msg}"));
    }

    public async Task<ToolCallResult> DeployCanaryAsync(string image, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return ToolCallResult.Error("'image' parameter is required (e.g. 'projectkongor/compel-dedicated:sha-1a2b3c4').");
        }

        string cleanImage = image.Trim();
        _logger($"Preparing canary deployment for image: {cleanImage}...");

        string fleetPath = Path.Combine(_repoRoot, FleetYamlRelativePath);
        if (!File.Exists(fleetPath))
        {
            return ToolCallResult.Error($"Fleet manifest not found at '{fleetPath}'.");
        }

        // Read template from fleet.yaml to extract real container environment, ports, and hostAliases
        string fleetYaml = await File.ReadAllTextAsync(fleetPath, ct);

        // Build standalone GameServer manifest mirroring fleet template
        string canaryManifest = BuildCanaryManifestFromFleet(fleetYaml, cleanImage);
        string canaryPath = Path.Combine(_repoRoot, CanaryYamlRelativePath);

        try
        {
            await File.WriteAllTextAsync(canaryPath, canaryManifest, ct);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to write canary manifest: {ex.Message}");
        }

        _logger($"Applying canary GameServer manifest: {CanaryYamlRelativePath}...");
        var applyResult = await RunKubectlAsync($"apply -f {CanaryYamlRelativePath}", ct);
        if (applyResult.IsError)
        {
            return ToolCallResult.Error(
                $"Failed to deploy canary to Agones cluster.\n" +
                $"Ensure Kubernetes/k3d is running (see infra/k8s/setup_local_k3d_agones.ps1).\n\n" +
                $"Error output:\n{applyResult.FirstText}");
        }

        // Wait briefly for Agones controller to schedule pod
        await Task.Delay(3000, ct);
        return await CheckCanaryAsync(ct);
    }

    public async Task<ToolCallResult> CheckCanaryAsync(CancellationToken ct)
    {
        _logger("Checking status of canary GameServer 'kongor-canary'...");
        var result = await RunKubectlAsync("get gameserver kongor-canary -o json", ct);
        if (result.IsError)
        {
            return ToolCallResult.Error(
                "Canary GameServer 'kongor-canary' not found in cluster.\n" +
                "Deploy one first with action='deploy' image='<image-tag>'.");
        }

        try
        {
            using var doc = JsonDocument.Parse(result.FirstText);
            var root = doc.RootElement;

            string state = "Unknown";
            string address = "Pending";
            string port = "Pending";
            string image = "Unknown";

            if (root.TryGetProperty("status", out var statusEl))
            {
                if (statusEl.TryGetProperty("state", out var stEl)) state = stEl.GetString() ?? "Unknown";
                if (statusEl.TryGetProperty("address", out var addrEl)) address = addrEl.GetString() ?? "Pending";
                if (statusEl.TryGetProperty("ports", out var portsEl) && portsEl.GetArrayLength() > 0)
                {
                    port = portsEl[0].GetProperty("port").GetInt32().ToString();
                }
            }

            if (root.TryGetProperty("spec", out var specEl) &&
                specEl.TryGetProperty("template", out var tmplEl) &&
                tmplEl.TryGetProperty("spec", out var podSpecEl) &&
                podSpecEl.TryGetProperty("containers", out var containersEl) &&
                containersEl.GetArrayLength() > 0)
            {
                image = containersEl[0].GetProperty("image").GetString() ?? "Unknown";
            }

            string icon = state switch
            {
                "Ready" => "[READY]",
                "Allocated" => "[ALLOCATED]",
                "Scheduled" => "[SCHEDULED]",
                "Requesting" => "[REQUESTING]",
                "Error" => "[ERROR]",
                "Unhealthy" => "[UNHEALTHY]",
                _ => "[STARTING]"
            };

            bool isHealthy = state is "Ready" or "Allocated";
            string verdict = isHealthy
                ? "HEALTHY - Safe to promote to fleet."
                : (state is "Scheduled" or "Starting" or "Requesting"
                    ? "STARTING - Waiting for game server to ping Agones SDK."
                    : "UNHEALTHY - Do not promote. Inspect server logs.");

            return ToolCallResult.Success(
                $"{icon} Canary GameServer Status:\n" +
                $"- Name: kongor-canary\n" +
                $"- State: {state}\n" +
                $"- Image: {image}\n" +
                $"- Endpoint: {address}:{port}\n" +
                $"- Verdict: {verdict}");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Failed to parse gameserver JSON: {ex.Message}");
        }
    }

    public async Task<ToolCallResult> PromoteCanaryAsync(CancellationToken ct)
    {
        _logger("Promoting canary image to Agones fleet via JSON patch...");

        // 1. Get image from canary
        var checkResult = await RunKubectlAsync("get gameserver kongor-canary -o jsonpath={.spec.template.spec.containers[0].image}", ct);
        if (checkResult.IsError || string.IsNullOrWhiteSpace(checkResult.FirstText))
        {
            return ToolCallResult.Error("Could not retrieve image from 'kongor-canary'. Ensure a canary is deployed.");
        }

        string newImage = checkResult.FirstText.Trim(['\'', '"', ' ', '\r', '\n']);
        _logger($"Promoting fleet '{FleetName}' to image: {newImage}...");

        // 2. Patch fleet using JSON patch on CRD without touching files on disk
        string patchArg = $"[{{\"op\":\"replace\",\"path\":\"/spec/template/spec/template/spec/containers/0/image\",\"value\":\"{newImage}\"}}]";

        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("patch");
        psi.ArgumentList.Add("fleet");
        psi.ArgumentList.Add(FleetName);
        psi.ArgumentList.Add("--type=json");
        psi.ArgumentList.Add($"-p={patchArg}");

        var patchResult = await RunProcessAsync(psi, ct);
        if (patchResult.IsError)
        {
            return ToolCallResult.Error($"Fleet promotion patch failed:\n{patchResult.Content}");
        }

        // 3. Clean up the canary GameServer
        _logger("Cleaning up canary GameServer after successful promotion...");
        await RunKubectlAsync("delete gameserver kongor-canary --ignore-not-found", ct);

        // Delete canary manifest artifact if exists
        string canaryPath = Path.Combine(_repoRoot, CanaryYamlRelativePath);
        if (File.Exists(canaryPath))
        {
            try
            {
                File.Delete(canaryPath);
            }
            catch (Exception delEx)
            {
                _logger($"[WARN] Failed deleting canary manifest '{canaryPath}': {delEx.Message}");
            }
        }

        return ToolCallResult.Success(
            $"Fleet '{FleetName}' successfully promoted to image: {newImage}!\n\n" +
            $"Patch result:\n{patchResult.FirstText}\n" +
            "Canary GameServer cleaned up. Rolling update initiated.");
    }

    public async Task<ToolCallResult> RollbackCanaryAsync(CancellationToken ct)
    {
        _logger("Rolling back canary GameServer...");
        var result = await RunKubectlAsync("delete gameserver kongor-canary --ignore-not-found", ct);

        string canaryPath = Path.Combine(_repoRoot, CanaryYamlRelativePath);
        if (File.Exists(canaryPath))
        {
            try
            {
                File.Delete(canaryPath);
            }
            catch (Exception delEx)
            {
                _logger($"[WARN] Failed deleting canary manifest '{canaryPath}': {delEx.Message}");
            }
        }

        return ToolCallResult.Success("Canary GameServer deleted. Main fleet left completely untouched.");
    }

    public async Task<ToolCallResult> GetStatusAsync(CancellationToken ct)
    {
        _logger("Fetching fleet and canary status side-by-side...");

        var fleetTask = RunKubectlAsync($"get fleet {FleetName} -o json", ct);
        var canaryTask = RunKubectlAsync("get gameserver kongor-canary -o json", ct);
        await Task.WhenAll(fleetTask, canaryTask);

        var fleetResult = await fleetTask;
        var canaryResult = await canaryTask;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Agones GameServer Deployment Status\n");

        if (!fleetResult.IsError && !string.IsNullOrWhiteSpace(fleetResult.FirstText))
        {
            try
            {
                using var doc = JsonDocument.Parse(fleetResult.FirstText);
                var root = doc.RootElement;
                string fleetImage = "unknown";
                if (root.TryGetProperty("spec", out var spec) &&
                    spec.TryGetProperty("template", out var tmpl) &&
                    tmpl.TryGetProperty("spec", out var tmplSpec) &&
                    tmplSpec.TryGetProperty("template", out var podTmpl) &&
                    podTmpl.TryGetProperty("spec", out var podSpec) &&
                    podSpec.TryGetProperty("containers", out var containers) &&
                    containers.GetArrayLength() > 0)
                {
                    fleetImage = containers[0].GetProperty("image").GetString() ?? "unknown";
                }

                int replicas = 0;
                int readyReplicas = 0;
                int allocatedReplicas = 0;

                if (root.TryGetProperty("status", out var status))
                {
                    if (status.TryGetProperty("replicas", out var r)) replicas = r.GetInt32();
                    if (status.TryGetProperty("readyReplicas", out var rr)) readyReplicas = rr.GetInt32();
                    if (status.TryGetProperty("allocatedReplicas", out var ar)) allocatedReplicas = ar.GetInt32();
                }

                sb.AppendLine("### Main Dedicated Fleet (`kongor-dedicated-fleet`)\n");
                sb.AppendLine($"- Current Image: `{fleetImage}`");
                sb.AppendLine($"- Replicas: {replicas} total ({readyReplicas} Ready, {allocatedReplicas} Allocated)\n");
            }
            catch
            {
                sb.AppendLine("### Main Dedicated Fleet: Unable to parse fleet JSON.\n");
            }
        }
        else
        {
            sb.AppendLine("### Main Dedicated Fleet: Offline or not reachable.\n");
        }

        sb.AppendLine("### Canary GameServer (`kongor-canary`)\n");
        if (!canaryResult.IsError && !string.IsNullOrWhiteSpace(canaryResult.FirstText))
        {
            var checkRes = await CheckCanaryAsync(ct);
            sb.AppendLine(checkRes.FirstText);
        }
        else
        {
            sb.AppendLine("No active canary deployed. Fleet is operating on base image.");
        }

        return ToolCallResult.Success(sb.ToString());
    }

    private string BuildCanaryManifestFromFleet(string fleetYaml, string canaryImage)
    {
        // Extract spec.template from fleet.yaml to retain all ports, hostAliases, env vars, and capabilities
        // Replace kind: Fleet with kind: GameServer and inject canary metadata
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("apiVersion: \"agones.dev/v1\"");
        sb.AppendLine("kind: GameServer");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: kongor-canary");
        sb.AppendLine("  namespace: default");
        sb.AppendLine("  labels:");
        sb.AppendLine("    app: kongor-dedicated-canary");
        sb.AppendLine("    kongor.dev/canary: \"true\"");

        // Find spec: inside template:
        int templateSpecIdx = fleetYaml.IndexOf("template:\n    spec:", StringComparison.Ordinal);
        if (templateSpecIdx >= 0)
        {
            string innerSpec = fleetYaml[(templateSpecIdx + "template:\n    ".Length)..];
            // Adjust indentation from 4 spaces to 0 for root spec
            var lines = innerSpec.Split('\n');
            foreach (var line in lines)
            {
                string deindented = line.StartsWith("    ") ? line[4..] : line;
                if (deindented.Contains("image: projectkongor/compel-dedicated") || deindented.Contains("image: ") && deindented.Contains("compel-dedicated"))
                {
                    sb.AppendLine($"          image: {canaryImage}");
                }
                else
                {
                    sb.AppendLine(deindented);
                }
            }
        }
        else
        {
            // Fallback default Agones manifest
            sb.AppendLine("spec:");
            sb.AppendLine("  ports:");
            sb.AppendLine("    - name: game-udp");
            sb.AppendLine("      protocol: UDP");
            sb.AppendLine("      portPolicy: Passthrough");
            sb.AppendLine("      range: default");
            sb.AppendLine("    - name: voice-udp");
            sb.AppendLine("      protocol: UDP");
            sb.AppendLine("      portPolicy: Passthrough");
            sb.AppendLine("      range: voice");
            sb.AppendLine("  template:");
            sb.AppendLine("    spec:");
            sb.AppendLine("      containers:");
            sb.AppendLine("        - name: compel-worker");
            sb.AppendLine($"          image: {canaryImage}");
            sb.AppendLine("          env:");
            sb.AppendLine("            - name: AGONES_ENABLED");
            sb.AppendLine("              value: \"true\"");
            sb.AppendLine("            - name: AGONES_SDK_HTTP_PORT");
            sb.AppendLine("              value: \"9358\"");
        }

        return sb.ToString();
    }

    private async Task<ToolCallResult> RunKubectlAsync(string kubectlArgs, CancellationToken ct)
    {
        string fullArgs = kubectlArgs.Contains("--request-timeout")
            ? kubectlArgs
            : $"--request-timeout=3s {kubectlArgs}";

        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            Arguments = fullArgs,
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        return await RunProcessAsync(psi, cts.Token);
    }

    private async Task<ToolCallResult> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        _logger($"Executing: {psi.FileName} {psi.Arguments}");
        try
        {
            using var process = Process.Start(psi);
            if (process == null) return ToolCallResult.Error($"Failed to start {psi.FileName}.");

            try
            {
                string stdout = await process.StandardOutput.ReadToEndAsync(ct);
                string stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    return ToolCallResult.Error($"Process exited with code {process.ExitCode}:\n{stderr}\n{stdout}");
                }

                return ToolCallResult.Success(stdout.TrimEnd());
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch (Exception killEx)
                {
                    _logger($"[WARN] Failed killing timed-out process '{psi.FileName}': {killEx.Message}");
                }
                return ToolCallResult.Error($"Process '{psi.FileName}' timed out or was cancelled.");
            }
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Exception running {psi.FileName}: {ex.Message}");
        }
    }
}
