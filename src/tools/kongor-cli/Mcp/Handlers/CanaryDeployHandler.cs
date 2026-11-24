// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_deploy_canary' (alias: 'deploy_canary').
/// Manages canary deployments on Agones Kubernetes: creates isolated test game servers,
/// verifies SDK health, promotes via live JSON patch, or rolls back safely.
/// </summary>
public sealed class CanaryDeployHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly CanaryDeployService _service;

    public CanaryDeployHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        CanaryDeployService? service = null,
        string? repoRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CanaryDeploy] {msg}"));
        _service = service ?? new CanaryDeployService(repoRoot, _logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string image = string.Empty;
        if ((arguments.TryGetProperty("image", out JsonElement imgEl) ||
             arguments.TryGetProperty("docker_image", out imgEl) ||
             arguments.TryGetProperty("image_tag", out imgEl)) &&
            !string.IsNullOrWhiteSpace(imgEl.GetString()))
        {
            image = imgEl.GetString()!.Trim();
        }

        _logger?.Invoke($"Canary deploy action '{action}' requested (image='{image}')...");

        ToolCallResult result = action switch
        {
            "deploy" => !string.IsNullOrEmpty(image)
                ? await _service.DeployCanaryAsync(image, cancellationToken)
                : ToolCallResult.Error("'image' parameter is required for action='deploy'."),
            "check" => await _service.CheckCanaryAsync(cancellationToken),
            "promote" => await _service.PromoteCanaryAsync(cancellationToken),
            "rollback" => await _service.RollbackCanaryAsync(cancellationToken),
            "status" => await _service.GetStatusAsync(cancellationToken),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Supported: deploy, check, promote, rollback, status.")
        };

        _logger?.Invoke($"Canary deploy action '{action}' completed in {sw.ElapsedMilliseconds}ms (isError={result.IsError})");
        return result;
    }
}
