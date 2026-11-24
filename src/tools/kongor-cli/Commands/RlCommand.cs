// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.MachineLearning;
using Spectre.Console;
using Spectre.Console.Cli;
using StackExchange.Redis;

namespace Kongor.Cli.Commands;

public sealed class RlCommandSettings : CommandSettings
{
    [CommandArgument(0, "<action>")]
    [Description("RL action: status, self-play, train, export, or rollback")]
    public string Action { get; init; } = "status";

    [CommandArgument(1, "[value]")]
    [Description("Optional value: 'on'|'off'|'status' for self-play, or target generation for rollback")]
    public string? Value { get; init; }

    [CommandOption("--runtime-dir <PATH>")]
    [Description("RL runtime directory (default: games/server/override/bots/rl_runtime)")]
    public string? RuntimeDir { get; init; }

    [CommandOption("--trajectories-dir <PATH>")]
    [Description("Trajectory JSONL directory (default: auto-detect logs/rl_trajectories)")]
    public string? TrajectoriesDir { get; init; }

    [CommandOption("--epochs <N>")]
    [Description("Training epochs for C# AWAC offline pass (default: 8)")]
    public int Epochs { get; init; } = 8;

    [CommandOption("--redis <CONN>")]
    [Description("Optional Redis connection string override")]
    public string? RedisConnection { get; init; }
}

public sealed class RlCommand : AsyncCommand<RlCommandSettings>
{
    public const string RedisIdleModeKey = "kongor:queue_arcade:idle_mode";

    public override async Task<int> ExecuteAsync(CommandContext context, RlCommandSettings settings)
    {
        CancellationToken cancellationToken = CancellationToken.None;
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = ResolveRepoRoot();
        string runtimeDir = !string.IsNullOrWhiteSpace(settings.RuntimeDir)
            ? Path.GetFullPath(settings.RuntimeDir)
            : Path.Combine(repoRoot, "games", "server", "override", "bots", "rl_runtime");
        string trajDir = ResolveTrajectoriesDirectory(repoRoot, settings.TrajectoriesDir);

        return action switch
        {
            "self-play" or "selfplay" or "mode" => await HandleSelfPlayAsync(settings.Value, settings.RedisConnection, cancellationToken).ConfigureAwait(false),
            "train" => HandleTrain(runtimeDir, trajDir, settings.Epochs),
            "export" => HandleExport(runtimeDir, repoRoot),
            "rollback" => HandleRollback(runtimeDir, settings.Value),
            "status" or _ => await HandleStatusAsync(runtimeDir, trajDir, settings.RedisConnection, cancellationToken).ConfigureAwait(false),
        };
    }

    internal static async Task<(bool Connected, string CurrentMode, string Detail)> SetOrGetIdleModeInRedisAsync(
        string? modeArg,
        string? redisConnectionOverride,
        CancellationToken cancellationToken = default)
    {
        string? connStr = ResolveRedisConnectionString(redisConnectionOverride);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            return (false, "hibernate", "No reachable Redis/Garnet instance found on localhost:28298 or localhost:6379.");
        }

        try
        {
            using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);
            IDatabase db = mux.GetDatabase();

            if (!string.IsNullOrWhiteSpace(modeArg) &&
                !modeArg.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                string normalized = modeArg.Trim().ToLowerInvariant() is "on" or "selfplay" or "self-play" or "enable" or "1" or "true"
                    ? "selfplay"
                    : "hibernate";
                await db.StringSetAsync(RedisIdleModeKey, normalized).ConfigureAwait(false);
                return (true, normalized, $"Updated Redis '{RedisIdleModeKey}'='{normalized}'.");
            }

            RedisValue current = await db.StringGetAsync(RedisIdleModeKey).ConfigureAwait(false);
            string resolved = current.HasValue ? current.ToString() : "hibernate (default)";
            return (true, resolved, $"Read Redis '{RedisIdleModeKey}'='{resolved}'.");
        }
        catch (Exception ex)
        {
            return (false, "unknown", $"Redis operation failed: {ex.Message}");
        }
    }

    private static async Task<int> HandleStatusAsync(string runtimeDir, string trajDir, string? redisOverride, CancellationToken ct)
    {
        RlArenaCoordinator coordinator = new(runtimeDir);
        coordinator.EnsureInitialized();
        RlArenaState state = coordinator.GetArenaState();
        (bool redisOk, string idleMode, _) = await SetOrGetIdleModeInRedisAsync(null, redisOverride, ct).ConfigureAwait(false);

        int epFiles = Directory.Exists(trajDir) ? Directory.GetFiles(trajDir, "ep_*.jsonl").Length : 0;
        long totalBytes = Directory.Exists(trajDir)
            ? new DirectoryInfo(trajDir).GetFiles("ep_*.jsonl").Sum(f => f.Length)
            : 0;

        Table table = new Table().Title("[bold cyan]Project Kongor Bot Deep RL Arena Status[/]");
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Queue Arcade IdleMode", redisOk ? idleMode : "hibernate (Redis unreachable)");
        table.AddRow("Champion Generation", $"gen_{state.ChampionGeneration:D4}");
        table.AddRow("Candidate Generation", $"gen_{state.CandidateGeneration:D4}");
        table.AddRow("Arena Evaluation", $"{state.ArenaMatchesPlayed}/20 matches (W:{state.CandidateWins} / L:{state.ChampionWins} / D:{state.Draws})");
        table.AddRow("Total Episodes / Steps", $"{state.TotalEpisodesIngested} / {state.TotalStepsIngested}");
        table.AddRow("Trajectory Ring Buffer", $"{epFiles} files ({totalBytes / (1024.0 * 1024.0):F2} MB / 256 MB cap)");
        table.AddRow("Last Training Loss", state.LastTrainingLoss);
        table.AddRow("Runtime Directory", runtimeDir);
        table.AddRow("Trajectory Directory", trajDir);
        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> HandleSelfPlayAsync(string? value, string? redisOverride, CancellationToken ct)
    {
        (bool ok, string mode, string detail) = await SetOrGetIdleModeInRedisAsync(value ?? "status", redisOverride, ct).ConfigureAwait(false);
        if (!ok)
        {
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] {Markup.Escape(detail)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]OK:[/] Queue Arcade IdleMode is now [bold]{Markup.Escape(mode)}[/]. ({Markup.Escape(detail)})");
        return 0;
    }

    private static int HandleTrain(string runtimeDir, string trajDir, int epochs)
    {
        RlArenaCoordinator coordinator = new(runtimeDir);
        coordinator.EnsureInitialized();

        List<RlEpisodeRecord> episodes = LoadEpisodesFromDisk(trajDir);
        if (episodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] No RL trajectory episodes found under '{Markup.Escape(trajDir)}'. Enable self-play or run a match first.");
            return 1;
        }

        RlTrainingSummary summary = coordinator.TrainNewCandidate(episodes, new AwacTrainingOptions(Epochs: Math.Max(1, epochs)));
        AnsiConsole.MarkupLine(
            $"[green]OK:[/] Trained Candidate [bold]gen_{summary.NewCandidateGeneration:D4}[/] from {summary.EpisodesUsed} episodes ({summary.StepsUsed} steps): loss {summary.InitialLoss:F5} -> {summary.FinalLoss:F5}");
        return 0;
    }

    private static int HandleExport(string runtimeDir, string repoRoot)
    {
        RlArenaCoordinator coordinator = new(runtimeDir);
        coordinator.EnsureInitialized();
        string baselinePath = Path.Combine(repoRoot, "games", "server", "override", "bots", "rl_weights_baseline.lua");
        coordinator.ExportChampionToBaselineLua(baselinePath);

        string[] mirrorTargets =
        [
            Path.Combine(repoRoot, "games", "client", "override", "bots", "rl_weights_baseline.lua"),
            Path.Combine(repoRoot, "games", "client", "game", "bots", "rl_weights_baseline.lua"),
        ];
        foreach (string mirror in mirrorTargets)
        {
            if (Directory.Exists(Path.GetDirectoryName(mirror)))
            {
                File.Copy(baselinePath, mirror, overwrite: true);
            }
        }

        RlArenaState state = coordinator.GetArenaState();
        AnsiConsole.MarkupLine($"[green]OK:[/] Exported Champion [bold]gen_{state.ChampionGeneration:D4}[/] to '{Markup.Escape(baselinePath)}' and client mirrors.");
        return 0;
    }

    private static int HandleRollback(string runtimeDir, string? genArg)
    {
        RlArenaCoordinator coordinator = new(runtimeDir);
        coordinator.EnsureInitialized();
        int? targetGen = int.TryParse(genArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
        BotPolicyCheckpoint restored = coordinator.RollbackChampion(targetGen);
        AnsiConsole.MarkupLine($"[green]OK:[/] Rolled back Champion to [bold]gen_{restored.Generation:D4}[/].");
        return 0;
    }

    private static List<RlEpisodeRecord> LoadEpisodesFromDisk(string trajDir)
    {
        if (!Directory.Exists(trajDir))
        {
            return [];
        }

        List<RlEpisodeRecord> eps = [];
        foreach (string file in Directory.GetFiles(trajDir, "ep_*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
        {
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    RlEpisodeRecord? ep = JsonSerializer.Deserialize(line, RlJsonContext.Default.RlEpisodeRecord);
                    if (ep is { Steps.Count: > 0 }) eps.Add(ep);
                }
                catch
                {
                    // Skip malformed line
                }
            }
        }
        return eps;
    }

    private static string ResolveRepoRoot() => Program.FindRepoRoot();

    private static string ResolveTrajectoriesDirectory(string repoRoot, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        string[] candidates =
        [
            Path.Combine(repoRoot, "logs", "docker", "compel-queue-arcade", "rl_trajectories"),
            Path.Combine(repoRoot, "logs", "rl_trajectories"),
        ];

        foreach (string c in candidates)
        {
            if (Directory.Exists(c))
            {
                return c;
            }
        }
        return candidates[0];
    }

    private static string? ResolveRedisConnectionString(string? explicitOverride)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            return explicitOverride;
        }

        string? env = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING")
                      ?? Environment.GetEnvironmentVariable("ConnectionStrings__DISTRIBUTED-CACHE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        foreach ((string host, int port) in new[] { ("127.0.0.1", 28298), ("127.0.0.1", 6379) })
        {
            try
            {
                using System.Net.Sockets.TcpClient tcp = new();
                IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)) && tcp.Connected)
                {
                    return $"{host}:{port},abortConnect=false,connectTimeout=1000";
                }
            }
            catch
            {
                // Probe next endpoint
            }
        }
        return null;
    }
}
