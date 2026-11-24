// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_db_migrate' (alias: 'db_migrate').
/// Provides safe database schema management: lists pending migrations, previews SQL changes,
/// applies migrations, scaffolds new ones, and performs verified rollbacks.
/// </summary>
public sealed class DbMigrateHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly DbMigrateService _service;

    public DbMigrateHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        DbMigrateService? service = null,
        string? workspaceRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[DbMigrate] {msg}"));
        _service = service ?? new DbMigrateService(workspaceRoot, _logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "list";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string target = string.Empty;
        if ((arguments.TryGetProperty("target", out JsonElement tEl) ||
             arguments.TryGetProperty("target_migration", out tEl) ||
             arguments.TryGetProperty("migration", out tEl)) &&
            !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim();
        }

        string name = string.Empty;
        if ((arguments.TryGetProperty("name", out JsonElement nEl) ||
             arguments.TryGetProperty("migration_name", out nEl)) &&
            !string.IsNullOrWhiteSpace(nEl.GetString()))
        {
            name = nEl.GetString()!.Trim();
        }

        _logger?.Invoke($"Executing database migration action '{action}' (target='{target}', name='{name}')...");

        if (action is "apply" or "rollback")
        {
            string? explicitCs = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("connectionString", out var csEl)
                ? csEl.GetString()
                : null;
            string targetHost = !string.IsNullOrWhiteSpace(explicitCs)
                ? DbPoolDiagnosticsHandler.ParseHostFromConnectionString(explicitCs)
                : DbPoolDiagnosticsHandler.ResolveDatabaseEndpoint().Host;

            if (!DbPoolDiagnosticsHandler.TryValidateRemoteDatabaseMutation(targetHost, "kongor_db_migrate", action, arguments, out var blockedResult))
            {
                return blockedResult!;
            }
        }

        ToolCallResult result = action switch
        {
            "list" or "status" => await _service.ListMigrationsAsync(cancellationToken),
            "preview" => await _service.PreviewMigrationsAsync(cancellationToken),
            "apply" => await _service.ApplyMigrationsAsync(cancellationToken),
            "rollback" => await _service.RollbackMigrationAsync(target, cancellationToken),
            "add" => await _service.AddMigrationAsync(name, cancellationToken),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Supported: list, status, preview, apply, rollback, add.")
        };

        _logger?.Invoke($"DbMigrate action '{action}' finished in {sw.ElapsedMilliseconds}ms (isError={result.IsError})");
        return result;
    }
}
