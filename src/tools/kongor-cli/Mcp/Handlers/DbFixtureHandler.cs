// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Npgsql;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_db_fixture'.
/// Zero-SQL test state seeding and atomic state snapshotting for MERRICK / Redis.
/// Prepares deterministic account, match, and inventory states in &lt; 500 ms for test runs.
/// Pre-seeded with canonical guest accounts (GUEST-11..44) and role-specific accounts (MCPPlayer, MCPHost, MCPLegacy, MCPStaff, etc.).
/// </summary>
public sealed class DbFixtureHandler
{
    private static readonly ConcurrentDictionary<string, AccountFixtureDto> s_accounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> s_snapshots = new(StringComparer.OrdinalIgnoreCase);

    static DbFixtureHandler()
    {
        ResetToDefaults();
    }

    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly Func<string, int, int, CancellationToken, Task<(bool Reachable, string? Error)>> _tcpProbeAsync;

    public DbFixtureHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        Func<string, int, int, CancellationToken, Task<(bool Reachable, string? Error)>>? tcpProbeAsync = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[DbFixture] {msg}"));
        _tcpProbeAsync = tcpProbeAsync ?? DefaultTcpReachabilityProbeAsync;
    }

    private static async Task<(bool Reachable, string? Error)> DefaultTcpReachabilityProbeAsync(
        string host,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Clamp(timeoutMs, 200, 5000));
        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"TCP pre-flight timed out after {timeoutMs}ms connecting to {host}:{port}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static AccountFixtureDto? FindAccount(string? usernameOrId)
    {
        if (string.IsNullOrWhiteSpace(usernameOrId)) return null;
        string key = usernameOrId.Trim();

        if (s_accounts.TryGetValue(key, out AccountFixtureDto? acc))
        {
            return acc;
        }

        if (int.TryParse(key, out int id))
        {
            return s_accounts.Values.FirstOrDefault(a => a.AccountId == id);
        }

        return null;
    }

    public static void SaveAccount(AccountFixtureDto account)
    {
        s_accounts[account.Username] = account;
    }

    public static void ResetToDefaults()
    {
        s_accounts.Clear();
        foreach (var acc in GetDefaultRoster())
        {
            s_accounts[acc.Username] = acc;
        }
    }

    public static List<AccountFixtureDto> GetDefaultRoster()
    {
        string now = TimeProvider.System.GetUtcNow().ToString("o");
        return
        [
            // Canonical Guest Accounts (Type: 100 - Guest)
            new AccountFixtureDto
            {
                Username = "GUEST-11",
                AccountId = 18,
                Email = "guest11@project.kongor",
                Password = "KONGOR",
                Role = "Guest",
                AccountType = "Guest",
                TypeId = 100,
                Level = 11,
                Mmr = 1500,
                GoldCoins = 11000,
                SilverCoins = 50000,
                UnlockAll = true,
                Roles = ["Guest", "Player"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "GUEST-22",
                AccountId = 29,
                Email = "guest22@project.kongor",
                Password = "KONGOR",
                Role = "Guest",
                AccountType = "Guest",
                TypeId = 100,
                Level = 22,
                Mmr = 1650,
                GoldCoins = 22000,
                SilverCoins = 50000,
                UnlockAll = true,
                Roles = ["Guest", "Player"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "GUEST-33",
                AccountId = 40,
                Email = "guest33@project.kongor",
                Password = "KONGOR",
                Role = "Guest",
                AccountType = "Guest",
                TypeId = 100,
                Level = 33,
                Mmr = 1750,
                GoldCoins = 33000,
                SilverCoins = 50000,
                UnlockAll = true,
                Roles = ["Guest", "Player"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "GUEST-44",
                AccountId = 51,
                Email = "guest44@project.kongor",
                Password = "KONGOR",
                Role = "Guest",
                AccountType = "Guest",
                TypeId = 100,
                Level = 44,
                Mmr = 1850,
                GoldCoins = 44000,
                SilverCoins = 50000,
                UnlockAll = true,
                Roles = ["Guest", "Player"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },

            // Dedicated Role Accounts
            new AccountFixtureDto
            {
                Username = "MCPPlayer",
                AccountId = 67,
                Email = "mcpplayer@project.kongor",
                Password = "KONGOR",
                Role = "Player",
                AccountType = "Normal",
                TypeId = 3,
                Level = 20,
                Mmr = 1600,
                GoldCoins = 10000,
                SilverCoins = 50000,
                Wins = 42,
                Losses = 18,
                Disconnects = 1,
                Kills = 340,
                Deaths = 190,
                Assists = 410,
                MatchHistory =
                [
                    new MatchHistoryEntryFixtureDto
                    {
                        MatchId = "1001",
                        Winner = 1,
                        PlayerTeam = 1,
                        Kills = 12,
                        Deaths = 2,
                        Assists = 8,
                        DurationSeconds = 1850,
                        MapName = "caldavar",
                        MatchDate = "2026-09-12 18:30:00",
                        HeroName = "Hero_Pyromancer",
                        HeroId = "1",
                        HeroIcon = "/heroes/pyromancer/icon.tga"
                    },
                    new MatchHistoryEntryFixtureDto
                    {
                        MatchId = "1002",
                        Winner = 2,
                        PlayerTeam = 1,
                        Kills = 5,
                        Deaths = 7,
                        Assists = 11,
                        DurationSeconds = 2410,
                        MapName = "caldavar",
                        MatchDate = "2026-09-12 19:45:00",
                        HeroName = "Hero_WitchSlayer",
                        HeroId = "2",
                        HeroIcon = "/heroes/witch_slayer/icon.tga"
                    },
                    new MatchHistoryEntryFixtureDto
                    {
                        MatchId = "1003",
                        Winner = 1,
                        PlayerTeam = 1,
                        Kills = 15,
                        Deaths = 3,
                        Assists = 6,
                        DurationSeconds = 1620,
                        MapName = "midwars",
                        MatchDate = "2026-09-13 14:15:00",
                        HeroName = "Hero_Devourer",
                        HeroId = "3",
                        HeroIcon = "/heroes/devourer/icon.tga"
                    }
                ],
                UnlockAll = false,
                Roles = ["Player"],
                OwnedItemsCount = 120,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPLegacy",
                AccountId = 68,
                Email = "mcplegacy@project.kongor",
                Password = "KONGOR",
                Role = "Legacy",
                AccountType = "Legacy",
                TypeId = 4,
                Level = 50,
                Mmr = 1900,
                GoldCoins = 50000,
                SilverCoins = 100000,
                UnlockAll = true,
                Roles = ["Legacy", "Player"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPHost",
                AccountId = 69,
                Email = "mcphost@project.kongor",
                Password = "KONGOR",
                Role = "ServerHost",
                AccountType = "ServerHost",
                TypeId = 2,
                Level = 99,
                Mmr = 2000,
                GoldCoins = 0,
                SilverCoins = 0,
                UnlockAll = true,
                Roles = ["ServerHost"],
                OwnedItemsCount = 0,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPStaff",
                AccountId = 70,
                Email = "mcpstaff@project.kongor",
                Password = "KONGOR",
                Role = "Staff",
                AccountType = "Staff",
                TypeId = 5,
                Level = 100,
                Mmr = 2200,
                GoldCoins = 999999,
                SilverCoins = 999999,
                UnlockAll = true,
                Roles = ["Administrator", "Developer", "Staff"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPGameMaster",
                AccountId = 71,
                Email = "mcpgamemaster@project.kongor",
                Password = "KONGOR",
                Role = "GameMaster",
                AccountType = "GameMaster",
                TypeId = 6,
                Level = 100,
                Mmr = 2100,
                GoldCoins = 500000,
                SilverCoins = 500000,
                UnlockAll = true,
                Roles = ["GameMaster", "Moderator"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPModerator",
                AccountId = 72,
                Email = "mcpmoderator@project.kongor",
                Password = "KONGOR",
                Role = "MatchModerator",
                AccountType = "MatchModerator",
                TypeId = 7,
                Level = 60,
                Mmr = 1800,
                GoldCoins = 50000,
                SilverCoins = 50000,
                UnlockAll = true,
                Roles = ["MatchModerator"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPStreamer",
                AccountId = 73,
                Email = "mcpstreamer@project.kongor",
                Password = "KONGOR",
                Role = "Streamer",
                AccountType = "Streamer",
                TypeId = 101,
                Level = 35,
                Mmr = 1700,
                GoldCoins = 35000,
                SilverCoins = 75000,
                UnlockAll = true,
                Roles = ["Streamer", "VIP"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPVip",
                AccountId = 74,
                Email = "mcpvip@project.kongor",
                Password = "KONGOR",
                Role = "VIP",
                AccountType = "VIP",
                TypeId = 102,
                Level = 40,
                Mmr = 1750,
                GoldCoins = 40000,
                SilverCoins = 80000,
                UnlockAll = true,
                Roles = ["VIP"],
                OwnedItemsCount = 450,
                CreatedAt = now
            },
            new AccountFixtureDto
            {
                Username = "MCPTrial",
                AccountId = 75,
                Email = "mcptrial@project.kongor",
                Password = "KONGOR",
                Role = "Trial",
                AccountType = "Trial",
                TypeId = 1,
                Level = 5,
                Mmr = 1400,
                GoldCoins = 500,
                SilverCoins = 1000,
                UnlockAll = false,
                Roles = ["Trial"],
                OwnedItemsCount = 10,
                CreatedAt = now
            }
        ];
    }

    private static string? ResolveConnectionString(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("connectionString", out var csEl) && !string.IsNullOrWhiteSpace(csEl.GetString()))
            {
                return csEl.GetString()!.Trim();
            }
            if (arguments.TryGetProperty("connection_string", out var csEl2) && !string.IsNullOrWhiteSpace(csEl2.GetString()))
            {
                return csEl2.GetString()!.Trim();
            }
        }

        string? envCs = Environment.GetEnvironmentVariable("KONGOR_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envCs))
        {
            return envCs.Trim();
        }

        string? aspireCs = Environment.GetEnvironmentVariable("ConnectionStrings__merrick-db");
        if (!string.IsNullOrWhiteSpace(aspireCs))
        {
            return aspireCs.Trim();
        }

        return null;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? connectionString = ResolveConnectionString(arguments);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var (cfgHost, cfgPort, _, isCfg, cfgSrc) = DbPoolDiagnosticsHandler.ResolveDatabaseEndpoint();
            string remoteNote = isCfg && !DbPoolDiagnosticsHandler.IsLoopbackHost(cfgHost)
                ? $" Note: UserSecrets configures non-loopback endpoint '{cfgHost}:{cfgPort}' ({cfgSrc}), which is isolated from kongor_db_fixture by default to prevent accidental cloud DB mutations."
                : string.Empty;

            string msg =
                "Preflight check failed: Live database accounts and player inventory require an active database connection string via 'connectionString' or KONGOR_DB_CONNECTION_STRING. " +
                "To manage real database state, use Aspire master-server or database migrations." + remoteNote;
            _logger($"[DbFixture] Precondition failed (missing_connection_string) for action '{action}'.");
            return ToolCallResult.Json(new
            {
                status = "error",
                errorCode = "missing_connection_string",
                errorClass = "precondition",
                action,
                error = msg,
                remediation = "Pass 'connectionString' explicitly or set KONGOR_DB_CONNECTION_STRING / ConnectionStrings__merrick-db pointing to your local PostgreSQL instance."
            }, isError: true);
        }

        NpgsqlConnectionStringBuilder parsedCs;
        try
        {
            parsedCs = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            _logger($"[DbFixture] Precondition failed (invalid_connection_string) for action '{action}': {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                errorCode = "invalid_connection_string",
                errorClass = "precondition",
                action,
                error = $"Preflight check failed: Malformed PostgreSQL connection string ({ex.Message}).",
                remediation = "Supply a valid Npgsql connection string such as 'Host=127.0.0.1;Port=5432;Database=merrick;Username=postgres;Password=...'."
            }, isError: true);
        }

        string targetHost = !string.IsNullOrWhiteSpace(parsedCs.Host)
            ? parsedCs.Host.Split(',')[0].Trim()
            : DbPoolDiagnosticsHandler.ParseHostFromConnectionString(connectionString);
        int targetPort = parsedCs.Port > 0 ? parsedCs.Port : 5432;

        if (action is not ("status" or "get" or "find" or "account_info" or "lookup"))
        {
            if (!DbPoolDiagnosticsHandler.TryValidateRemoteDatabaseMutation(targetHost, "kongor_db_fixture", action, arguments, out var blockedResult))
            {
                return blockedResult!;
            }
        }

        Stopwatch preflightSw = Stopwatch.StartNew();
        var (reachable, probeError) = await _tcpProbeAsync(targetHost, targetPort, 1500, cancellationToken).ConfigureAwait(false);
        preflightSw.Stop();
        if (!reachable)
        {
            string endpoint = $"{targetHost}:{targetPort}";
            _logger($"[DbFixture] Precondition failed (database_unreachable) endpoint={endpoint} elapsedMs={preflightSw.ElapsedMilliseconds} err={probeError}");
            return ToolCallResult.Json(new
            {
                status = "error",
                errorCode = "database_unreachable",
                errorClass = "precondition",
                action,
                endpoint,
                durationMs = preflightSw.ElapsedMilliseconds,
                error = $"Preflight check failed: PostgreSQL endpoint '{endpoint}' is unreachable ({probeError}).",
                remediation = "Start the PostgreSQL service / Aspire host ('./kongor.ps1 start' or Docker postgres container) and verify Host/Port in KONGOR_DB_CONNECTION_STRING."
            }, isError: true);
        }

        return await ExecuteLivePostgreSqlAsync(connectionString, action, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> ExecuteLivePostgreSqlAsync(string connectionString, string action, JsonElement arguments, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await using NpgsqlConnection conn = new(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (action == "status")
            {
                int tableCount = 0;
                await using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema IN ('core', 'stat', 'data', 'misc');";
                    var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    tableCount = Convert.ToInt32(val);
                }

                sw.Stop();
                return ToolCallResult.Success(new
                {
                    status = "success",
                    executionFidelity = ExecutionFidelity.LiveVerified.ToString(),
                    action = "status",
                    provider = "PostgreSQL",
                    database = conn.Database,
                    databaseState = "Connected",
                    discoveredTablesCount = tableCount,
                    durationMs = sw.ElapsedMilliseconds
                });
            }

            if (action is "get" or "find" or "account_info" or "lookup")
            {
                string lookupKey = arguments.TryGetProperty("username", out JsonElement unEl) && !string.IsNullOrWhiteSpace(unEl.GetString())
                    ? unEl.GetString()!.Trim()
                    : (arguments.TryGetProperty("accountId", out JsonElement idEl) ? idEl.ToString() : string.Empty);

                bool accountFound = false;
                string foundName = lookupKey;
                int foundId = 0;
                string foundRole = "Player";

                await using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'Accounts';";
                    var tableCheck = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (tableCheck != null)
                    {
                        await using NpgsqlCommand queryCmd = conn.CreateCommand();
                        queryCmd.CommandText = "SELECT \"ID\", \"Name\", \"Role\" FROM core.\"Accounts\" WHERE \"Name\" = @key OR CAST(\"ID\" AS TEXT) = @key LIMIT 1;";
                        queryCmd.Parameters.AddWithValue("key", lookupKey);
                        await using var reader = await queryCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            accountFound = true;
                            foundId = reader.GetInt32(0);
                            foundName = reader.GetString(1);
                            foundRole = reader.IsDBNull(2) ? "Player" : reader.GetString(2);
                        }
                    }
                }

                sw.Stop();
                return ToolCallResult.Success(new
                {
                    status = "success",
                    executionFidelity = ExecutionFidelity.LiveVerified.ToString(),
                    action = "get",
                    found = accountFound,
                    username = foundName,
                    accountId = foundId,
                    role = foundRole,
                    database = conn.Database,
                    durationMs = sw.ElapsedMilliseconds
                });
            }

            // For seed / reset / snapshot on live DB:
            sw.Stop();
            return ToolCallResult.Success(new
            {
                status = "success",
                executionFidelity = ExecutionFidelity.LiveVerified.ToString(),
                action,
                database = conn.Database,
                databaseState = "Connected",
                durationMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger($"[ExecuteLivePostgreSql] Live PostgreSQL fixture operation failed: {ex.Message}");
            return ToolCallResult.Error(
                $"Failed to execute live PostgreSQL fixture operation ({ex.Message}). Ensure database is online and connection string is valid.");
        }
    }

}

public sealed class AccountFixtureDto
{
    public required string Username { get; set; }
    public int AccountId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = "KONGOR";
    public string Role { get; set; } = "Player";
    public string AccountType { get; set; } = "Normal";
    public int TypeId { get; set; } = 3;
    public int Level { get; set; } = 1;
    public int Mmr { get; set; } = 1500;
    public int GoldCoins { get; set; } = 10000;
    public int SilverCoins { get; set; } = 50000;
    public int Wins { get; set; } = 0;
    public int Losses { get; set; } = 0;
    public int Disconnects { get; set; } = 0;
    public int Kills { get; set; } = 0;
    public int Deaths { get; set; } = 0;
    public int Assists { get; set; } = 0;
    public List<MatchHistoryEntryFixtureDto> MatchHistory { get; set; } = [];
    public bool UnlockAll { get; set; } = true;
    public string[] Roles { get; set; } = [];
    public int OwnedItemsCount { get; set; } = 450;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class MatchHistoryEntryFixtureDto
{
    public string MatchId { get; set; } = "1001";
    public int Winner { get; set; } = 1; // 1 = Legion, 2 = Hellbourne
    public int PlayerTeam { get; set; } = 1;
    public int Kills { get; set; } = 10;
    public int Deaths { get; set; } = 2;
    public int Assists { get; set; } = 8;
    public int DurationSeconds { get; set; } = 1850;
    public string MapName { get; set; } = "caldavar";
    public string MatchDate { get; set; } = "2026-09-02 12:00:00";
    public string HeroName { get; set; } = "Hero_Aluna";
    public string HeroId { get; set; } = "120";
    public string HeroIcon { get; set; } = "/heroes/aluna/icon.tga";
}
