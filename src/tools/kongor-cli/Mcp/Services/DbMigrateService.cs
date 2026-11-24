// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service wrapping Entity Framework Core migrations with developer safety rails:
/// - Pre-migration idempotent SQL preview
/// - Automated dotnet tool restore pre-flight
/// - PascalCase migration name validation
/// - Rollback safety checks
/// </summary>
public sealed class DbMigrateService
{
    private const string DbContextProject = "src/libs/database-context";
    private const string StartupProject = "src/services/web-api";

    private readonly string _repoRoot;
    private readonly Action<string> _logger;

    public DbMigrateService(string? repoRoot = null, Action<string>? logger = null)
    {
        _repoRoot = repoRoot ?? Directory.GetCurrentDirectory();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[DB Migrate] {msg}"));
    }

    public async Task<ToolCallResult> ListMigrationsAsync(CancellationToken ct)
    {
        _logger("Querying EF Core migrations status...");
        bool dbReachable = await IsDatabaseReachableAsync();
        string connectFlag = dbReachable ? "" : "--no-connect";

        var result = await RunEfCommandAsync($"migrations list --no-build {connectFlag}".Trim(), ct);
        if (result.IsError)
        {
            _logger("migrations list --no-build failed. Retrying with build...");
            result = await RunEfCommandAsync($"migrations list {connectFlag}".Trim(), ct);
        }
        if (result.IsError) return result;

        string stdout = result.FirstText;
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int applied = 0;
        int pending = 0;
        var migrationRows = new List<(string Name, bool? IsPending)>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Build started") ||
                line.StartsWith("Build succeeded") ||
                line.Contains("Entity Framework tools") ||
                line.StartsWith("Pending status not shown") ||
                line.StartsWith("Unable to determine"))
            {
                continue;
            }

            if (line.EndsWith("(Pending)", StringComparison.OrdinalIgnoreCase))
            {
                pending++;
                string name = line[..^"(Pending)".Length].Trim();
                migrationRows.Add((name, true));
            }
            else if (!string.IsNullOrWhiteSpace(line) && !line.Contains(' '))
            {
                applied++;
                migrationRows.Add((line, dbReachable ? false : null));
            }
        }

        var sb = new System.Text.StringBuilder();
        string statusHeader = dbReachable
            ? $"Database Migrations Summary ({applied} Applied, {pending} Pending):\n"
            : $"Database Migrations Summary ({migrationRows.Count} Migrations in Model, Database Offline):\n";
        sb.AppendLine(statusHeader);
        sb.AppendLine("| Status | Migration Name |");
        sb.AppendLine("|--------|----------------|");

        foreach (var m in migrationRows)
        {
            string icon = m.IsPending == true ? "[PENDING]" : (m.IsPending == false ? "[APPLIED]" : "[INCLUDED]");
            sb.AppendLine($"| {icon} | {m.Name} |");
        }

        return ToolCallResult.Success(sb.ToString());
    }

    public async Task<ToolCallResult> PreviewMigrationsAsync(CancellationToken ct)
    {
        _logger("Generating idempotent SQL preview for pending migrations...");
        var result = await RunEfCommandAsync("migrations script --idempotent --no-build", ct);
        if (result.IsError)
        {
            // If --no-build fails, retry with build
            _logger("Idempotent script --no-build failed. Retrying with build...");
            result = await RunEfCommandAsync("migrations script --idempotent", ct);
        }

        if (result.IsError) return result;

        string sql = result.FirstText;
        int scriptBytes = System.Text.Encoding.UTF8.GetByteCount(sql);

        return ToolCallResult.Success(
            $"Idempotent SQL Migration Script ({scriptBytes} bytes):\n\n```sql\n{sql}\n```");
    }

    public async Task<ToolCallResult> ApplyMigrationsAsync(CancellationToken ct)
    {
        _logger("Pre-flight: Checking pending migrations before applying...");
        var listResult = await ListMigrationsAsync(ct);
        if (listResult.IsError) return listResult;

        _logger("Applying database migrations via 'dotnet ef database update'...");
        var updateResult = await RunEfCommandAsync("database update", ct);
        if (updateResult.IsError)
        {
            return ToolCallResult.Error(
                $"Database migration failed.\n\nPre-migration status:\n{listResult.FirstText}\n\nError output:\n{updateResult.FirstText}");
        }

        return ToolCallResult.Success(
            $"Migrations applied successfully.\n\nStatus:\n{updateResult.FirstText}");
    }

    public async Task<ToolCallResult> RollbackMigrationAsync(string targetMigration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetMigration))
        {
            return ToolCallResult.Error("Target migration name is required for rollback (e.g. '20260922190000_ComprehensiveNormalizationAndItemTimeline' or '0' for clean).");
        }

        _logger($"Rolling back database to migration target '{targetMigration}'...");
        var result = await RunEfCommandAsync($"database update {targetMigration.Trim()}", ct);
        if (result.IsError)
        {
            return ToolCallResult.Error($"Rollback to '{targetMigration}' failed:\n{result.FirstText}");
        }

        return ToolCallResult.Success($"Database successfully rolled back to target '{targetMigration}':\n{result.FirstText}");
    }

    public async Task<ToolCallResult> AddMigrationAsync(string migrationName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(migrationName))
        {
            return ToolCallResult.Error("Migration name is required (e.g. 'AddPlayerLoadoutPresets').");
        }

        string cleanName = migrationName.Trim();
        if (!Regex.IsMatch(cleanName, @"^[A-Z][A-Za-z0-9_]+$"))
        {
            return ToolCallResult.Error($"Migration name '{cleanName}' must start with an uppercase letter and contain only alphanumeric characters or underscores.");
        }

        _logger($"Adding new EF Core migration '{cleanName}'...");
        var result = await RunEfCommandAsync($"migrations add {cleanName}", ct);
        if (result.IsError)
        {
            return ToolCallResult.Error($"Failed to scaffold migration '{cleanName}':\n{result.FirstText}");
        }

        return ToolCallResult.Success($"Migration '{cleanName}' scaffolded successfully:\n{result.FirstText}");
    }

    private static async Task<bool> IsDatabaseReachableAsync(int timeoutMs = 500)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync("127.0.0.1", 5432, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ToolCallResult> RunEfCommandAsync(string efArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("ef");
        foreach (var part in efArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(part);
        }

        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(DbContextProject);
        psi.ArgumentList.Add("--startup-project");
        psi.ArgumentList.Add(StartupProject);
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add("Release");

        _logger($"Executing: dotnet {string.Join(" ", psi.ArgumentList)}");

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return ToolCallResult.Error("Failed to start dotnet ef process.");
            }

            process.StandardInput.Close();

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(ct);

                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    // Auto-healing pre-flight: if dotnet-ef is not restored, attempt restore
                    if ((stderr.Contains("dotnet tool restore") || stdout.Contains("dotnet tool restore")) &&
                        !efArgs.StartsWith("tool restore"))
                    {
                        _logger("dotnet-ef command not found. Attempting automatic 'dotnet tool restore'...");
                        var restorePsi = new ProcessStartInfo
                        {
                            FileName = "dotnet",
                            Arguments = "tool restore",
                            WorkingDirectory = _repoRoot,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var restoreProc = Process.Start(restorePsi);
                        if (restoreProc != null)
                        {
                            await restoreProc.WaitForExitAsync(ct);
                            if (restoreProc.ExitCode == 0)
                            {
                                _logger("Automatic restore succeeded. Retrying EF command...");
                                return await RunEfCommandAsync(efArgs, ct);
                            }
                        }
                    }

                    return ToolCallResult.Error($"dotnet ef exited with code {process.ExitCode}:\n{stderr}\n{stdout}");
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
                    _logger($"[WARN] Failed killing timed-out dotnet ef process: {killEx.Message}");
                }
                return ToolCallResult.Error("dotnet ef operation timed out or was cancelled.");
            }
        }
        catch (Exception ex)
        {
            _logger($"Execution exception: {ex.Message}");
            return ToolCallResult.Error($"dotnet ef failed with exception: {ex.Message}");
        }
    }
}
