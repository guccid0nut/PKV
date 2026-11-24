// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ASPIRE.Common.MachineLearning;

namespace COMPEL.Services.Telemetry;

public sealed record RlStatusSnapshot(
    int ChampionGeneration,
    int CandidateGeneration,
    int ArenaMatchesPlayed,
    int MinEvaluationMatches,
    int CandidateWins,
    int ChampionWins,
    int Draws,
    double CandidateWinRate,
    long TotalEpisodesIngested,
    long TotalStepsIngested,
    int RingBufferEpisodes,
    long RingBufferBytes,
    int ActiveBufferedMatches,
    string LastTrainingLoss,
    DateTimeOffset? LastTrainedAtUtc,
    DateTimeOffset? LastPromotedAtUtc,
    string RuntimeDirectory,
    string TrajectoryDirectory);

public sealed class RlTrajectoryCollectorService : BackgroundService
{
    public const long DefaultMaxRingBufferBytes = 256L * 1024L * 1024L;
    public const int DefaultMaxRingBufferEpisodes = 200;

    private readonly RlArenaCoordinator _arenaCoordinator;
    private readonly string _trajectoryDirectory;
    private readonly long _maxRingBufferBytes;
    private readonly int _maxRingBufferEpisodes;
    private readonly ILogger<RlTrajectoryCollectorService> _logger;

    private readonly Channel<RlQueueWorkItem> _channel = Channel.CreateBounded<RlQueueWorkItem>(
        new BoundedChannelOptions(16384)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly ConcurrentDictionary<(int Port, int MatchId), MatchStepBuffer> _activeBuffers = new();
    private readonly ConcurrentDictionary<int, (int WinnerTeam, int CandidateTeam)> _matchMetadataHints = new();
    private readonly HashSet<int> _completedMatchIds = [];

    private long _stepsAcceptedSession;

    public RlTrajectoryCollectorService(
        RlArenaCoordinator arenaCoordinator,
        IConfiguration configuration,
        ILogger<RlTrajectoryCollectorService> logger)
    {
        _arenaCoordinator = arenaCoordinator;
        _logger = logger;
        _trajectoryDirectory = ResolveTrajectoryDirectory(configuration);
        _maxRingBufferBytes = configuration.GetValue<long?>("Compel:RlMaxRingBufferBytes") ?? DefaultMaxRingBufferBytes;
        _maxRingBufferEpisodes = configuration.GetValue<int?>("Compel:RlMaxRingBufferEpisodes") ?? DefaultMaxRingBufferEpisodes;
    }

    internal RlTrajectoryCollectorService(
        RlArenaCoordinator arenaCoordinator,
        string trajectoryDirectory,
        ILogger<RlTrajectoryCollectorService> logger,
        long maxRingBufferBytes = DefaultMaxRingBufferBytes,
        int maxRingBufferEpisodes = DefaultMaxRingBufferEpisodes)
    {
        _arenaCoordinator = arenaCoordinator;
        _trajectoryDirectory = trajectoryDirectory;
        _logger = logger;
        _maxRingBufferBytes = maxRingBufferBytes;
        _maxRingBufferEpisodes = maxRingBufferEpisodes;
    }

    public string TrajectoryDirectory => _trajectoryDirectory;
    public RlArenaCoordinator ArenaCoordinator => _arenaCoordinator;

    public static bool TryHandleRawLine(RlTrajectoryCollectorService? collector, int port, string? text)
    {
        if (collector is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        int stepIdx = text.IndexOf("[RL_STEP]", StringComparison.Ordinal);
        if (stepIdx >= 0)
        {
            collector._channel.Writer.TryWrite(new RlQueueWorkItem(RlWorkKind.RawLine, port, text[stepIdx..], 0, null));
            return true;
        }

        int epIdx = text.IndexOf("[RL_EPISODE]", StringComparison.Ordinal);
        if (epIdx >= 0)
        {
            collector._channel.Writer.TryWrite(new RlQueueWorkItem(RlWorkKind.RawLine, port, text[epIdx..], 0, null));
            return true;
        }

        return false;
    }

    public void RecordMatchWinnerHint(int matchId, int winnerTeam, int candidateTeam = 0)
    {
        if (matchId <= 0)
        {
            return;
        }

        _matchMetadataHints.AddOrUpdate(
            matchId,
            _ => (winnerTeam, candidateTeam),
            (_, prev) => (winnerTeam > 0 ? winnerTeam : prev.WinnerTeam, candidateTeam > 0 ? candidateTeam : prev.CandidateTeam));
    }

    public void NotifyMatchEnded(int port, int matchId, string? mapName)
    {
        if (matchId <= 0)
        {
            return;
        }

        _channel.Writer.TryWrite(new RlQueueWorkItem(RlWorkKind.MatchEnded, port, string.Empty, matchId, mapName));
    }

    public RlStatusSnapshot GetStatusSnapshot()
    {
        _arenaCoordinator.EnsureInitialized();
        RlArenaState state = _arenaCoordinator.GetArenaState();
        (int epCount, long totalBytes) = MeasureRingBuffer();
        int decided = state.CandidateWins + state.ChampionWins;
        double winRate = decided > 0 ? (double)state.CandidateWins / decided : 0.0;

        return new RlStatusSnapshot(
            ChampionGeneration: state.ChampionGeneration,
            CandidateGeneration: state.CandidateGeneration,
            ArenaMatchesPlayed: state.ArenaMatchesPlayed,
            MinEvaluationMatches: 20,
            CandidateWins: state.CandidateWins,
            ChampionWins: state.ChampionWins,
            Draws: state.Draws,
            CandidateWinRate: winRate,
            TotalEpisodesIngested: state.TotalEpisodesIngested,
            TotalStepsIngested: state.TotalStepsIngested,
            RingBufferEpisodes: epCount,
            RingBufferBytes: totalBytes,
            ActiveBufferedMatches: _activeBuffers.Count,
            LastTrainingLoss: state.LastTrainingLoss,
            LastTrainedAtUtc: state.LastTrainedAtUtc,
            LastPromotedAtUtc: state.LastPromotedAtUtc,
            RuntimeDirectory: _arenaCoordinator.RuntimeDirectory,
            TrajectoryDirectory: _trajectoryDirectory);
    }

    public RlTrainingSummary ForceTrainFromRingBuffer(int epochs = 6)
    {
        List<RlEpisodeRecord> episodes = LoadRecentEpisodesFromDisk();
        if (episodes.Count == 0)
        {
            _arenaCoordinator.EnsureInitialized();
            RlArenaState state = _arenaCoordinator.GetArenaState();
            return new RlTrainingSummary(0, 0, 0, 0, 0, 0, 0, 0, state.CandidateGeneration);
        }

        return _arenaCoordinator.TrainNewCandidate(episodes, new AwacTrainingOptions(Epochs: Math.Max(1, epochs)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _arenaCoordinator.EnsureInitialized();
            Directory.CreateDirectory(_trajectoryDirectory);
            _logger.LogInformation(
                "[RL_COLLECTOR] Started (RuntimeDir='{RuntimeDir}', TrajectoryDir='{TrajectoryDir}', MaxBytes={MaxBytes}, MaxEpisodes={MaxEpisodes})",
                _arenaCoordinator.RuntimeDirectory,
                _trajectoryDirectory,
                _maxRingBufferBytes,
                _maxRingBufferEpisodes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RL_COLLECTOR] Failed initial directory bootstrap.");
        }

        await foreach (RlQueueWorkItem item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessWorkItemAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RL_COLLECTOR] Error processing RL telemetry work item ({Kind}, Port={Port}).", item.Kind, item.Port);
            }
        }
    }

    internal async Task ProcessWorkItemAsync(RlQueueWorkItem item, CancellationToken cancellationToken = default)
    {
        if (item.Kind == RlWorkKind.RawLine)
        {
            if (item.Line.StartsWith("[RL_STEP]", StringComparison.Ordinal))
            {
                string json = item.Line["[RL_STEP]".Length..].Trim();
                if (TryParseStepJson(json, out RlTrajectoryStep? step) && step is not null)
                {
                    MatchStepBuffer buf = _activeBuffers.GetOrAdd((item.Port, step.MatchId), _ => new MatchStepBuffer());
                    buf.AddStep(step);
                    Interlocked.Increment(ref _stepsAcceptedSession);
                }
            }
            else if (item.Line.StartsWith("[RL_EPISODE]", StringComparison.Ordinal))
            {
                string json = item.Line["[RL_EPISODE]".Length..].Trim();
                if (TryParseEpisodeHeader(json, out int matchId, out int candTeam, out int winnerTeam, out string? mapName))
                {
                    if (candTeam > 0 || winnerTeam > 0)
                    {
                        RecordMatchWinnerHint(matchId, winnerTeam, candTeam);
                    }
                    await FinalizeEpisodeAsync(item.Port, matchId, mapName ?? "caldavar", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (item.Kind == RlWorkKind.MatchEnded)
        {
            await FinalizeEpisodeAsync(item.Port, item.MatchId, item.MapName ?? "caldavar", cancellationToken).ConfigureAwait(false);
        }
    }

    private Task FinalizeEpisodeAsync(int port, int matchId, string mapName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_activeBuffers.TryRemove((port, matchId), out MatchStepBuffer? buffer))
        {
            // Check if steps arrived under port 0 / any matching matchId
            foreach (KeyValuePair<(int Port, int MatchId), MatchStepBuffer> kvp in _activeBuffers)
            {
                if (kvp.Key.MatchId == matchId && _activeBuffers.TryRemove(kvp.Key, out MatchStepBuffer? found))
                {
                    buffer = found;
                    break;
                }
            }
        }

        if (buffer is null || buffer.Steps.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (_completedMatchIds)
        {
            if (!_completedMatchIds.Add(matchId))
            {
                return Task.CompletedTask;
            }
            if (_completedMatchIds.Count > 2000)
            {
                _completedMatchIds.Clear();
                _completedMatchIds.Add(matchId);
            }
        }

        _matchMetadataHints.TryRemove(matchId, out (int WinnerTeam, int CandidateTeam) hints);

        int candidateTeam = hints.CandidateTeam;
        if (candidateTeam == 0)
        {
            candidateTeam = buffer.Steps.FirstOrDefault(s => string.Equals(s.ModelRole, "candidate", StringComparison.OrdinalIgnoreCase))?.Team ?? 0;
        }

        double team1Sum = 0.0;
        double team2Sum = 0.0;
        int candSteps = 0;
        double candSum = 0.0;
        int champSteps = 0;
        double champSum = 0.0;

        foreach (RlTrajectoryStep s in buffer.Steps)
        {
            if (s.Team == 1) team1Sum += s.Reward;
            else if (s.Team == 2) team2Sum += s.Reward;

            if (string.Equals(s.ModelRole, "candidate", StringComparison.OrdinalIgnoreCase))
            {
                candSteps++;
                candSum += s.Reward;
            }
            else
            {
                champSteps++;
                champSum += s.Reward;
            }
        }

        int winnerTeam = hints.WinnerTeam;
        if (winnerTeam == 0)
        {
            winnerTeam = team1Sum > team2Sum ? 1 : (team2Sum > team1Sum ? 2 : 0);
        }

        RlEpisodeRecord episode = new()
        {
            MatchId = matchId,
            MapName = string.IsNullOrWhiteSpace(mapName) ? "caldavar" : mapName,
            WinnerTeam = winnerTeam,
            CandidateTeam = candidateTeam,
            MatchDurationSeconds = buffer.Steps.Max(s => s.Tick) / 1000.0,
            CompletedAtUtc = TimeProvider.System.GetUtcNow(),
            MeanCandidateReturn = candSteps > 0 ? candSum / candSteps : 0.0,
            MeanChampionReturn = champSteps > 0 ? champSum / champSteps : 0.0,
            Steps = buffer.Steps
        };

        WriteEpisodeToRingBuffer(episode);
        List<RlEpisodeRecord> recentCorpus = LoadRecentEpisodesFromDisk();
        RlArenaEvaluationResult evalResult = _arenaCoordinator.RecordEpisodeAndMaybeEvaluate(episode, recentCorpus);

        _logger.LogInformation(
            "[RL_ARENA] Recorded Episode MatchID={MatchId} Map='{Map}' Steps={Steps} WinnerTeam={Winner} CandTeam={CandTeam} | Eval: {Reason} (ChampGen={ChampGen}, CandGen={CandGen}, WinRate={WinRate:P1})",
            matchId,
            episode.MapName,
            episode.Steps.Count,
            winnerTeam,
            candidateTeam,
            evalResult.Reason,
            evalResult.ChampionGeneration,
            evalResult.CandidateGeneration,
            evalResult.CandidateWinRate);

        return Task.CompletedTask;
    }

    private void WriteEpisodeToRingBuffer(RlEpisodeRecord episode)
    {
        Directory.CreateDirectory(_trajectoryDirectory);
        string stamp = episode.CompletedAtUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        string targetPath = Path.Combine(_trajectoryDirectory, $"ep_{stamp}_{episode.MatchId}.jsonl");
        string tempPath = targetPath + ".tmp";

        string json = JsonSerializer.Serialize(episode, RlJsonContext.Default.RlEpisodeRecord);
        File.WriteAllText(tempPath, json + Environment.NewLine);
        File.Move(tempPath, targetPath, overwrite: true);

        EnforceRingBufferLimits();
    }

    internal void EnforceRingBufferLimits()
    {
        if (!Directory.Exists(_trajectoryDirectory))
        {
            return;
        }

        List<FileInfo> files = new DirectoryInfo(_trajectoryDirectory)
            .GetFiles("ep_*.jsonl")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        long totalBytes = files.Sum(f => f.Length);
        int idx = 0;

        while (idx < files.Count && (files.Count - idx > _maxRingBufferEpisodes || totalBytes > _maxRingBufferBytes))
        {
            FileInfo oldest = files[idx];
            idx++;
            try
            {
                long len = oldest.Length;
                oldest.Delete();
                totalBytes -= len;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RL_COLLECTOR] Failed pruning oldest trajectory file '{FilePath}'.", oldest.FullName);
            }
        }
    }

    public List<RlEpisodeRecord> LoadRecentEpisodesFromDisk()
    {
        if (!Directory.Exists(_trajectoryDirectory))
        {
            return [];
        }

        List<RlEpisodeRecord> result = [];
        foreach (string file in Directory.GetFiles(_trajectoryDirectory, "ep_*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    RlEpisodeRecord? ep = JsonSerializer.Deserialize(line, RlJsonContext.Default.RlEpisodeRecord);
                    if (ep is { Steps.Count: > 0 })
                    {
                        result.Add(ep);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RL_COLLECTOR] Skipping unreadable or corrupted trajectory file '{FilePath}'.", file);
            }
        }

        return result;
    }

    private (int Count, long Bytes) MeasureRingBuffer()
    {
        if (!Directory.Exists(_trajectoryDirectory))
        {
            return (0, 0);
        }

        FileInfo[] files = new DirectoryInfo(_trajectoryDirectory).GetFiles("ep_*.jsonl");
        return (files.Length, files.Sum(f => f.Length));
    }

    private static bool TryParseStepJson(string json, out RlTrajectoryStep? step)
    {
        step = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            int matchId = root.TryGetProperty("matchId", out JsonElement mEl) && mEl.TryGetInt32(out int m) ? m : 0;
            string hero = root.TryGetProperty("hero", out JsonElement hEl) ? (hEl.GetString() ?? string.Empty) : string.Empty;
            int team = root.TryGetProperty("team", out JsonElement tEl) && tEl.TryGetInt32(out int t) ? t : 1;
            string role = root.TryGetProperty("role", out JsonElement rEl) ? (rEl.GetString() ?? "champion") : "champion";
            int tick = root.TryGetProperty("tick", out JsonElement tkEl) && tkEl.TryGetInt32(out int tk) ? tk : 0;
            int combo = root.TryGetProperty("combo", out JsonElement cEl) && cEl.TryGetInt32(out int cb) ? cb : 0;
            bool juke = root.TryGetProperty("juke", out JsonElement jEl) && jEl.ValueKind == JsonValueKind.True;
            double mx = root.TryGetProperty("mx", out JsonElement mxEl) && mxEl.TryGetDouble(out double dvx) ? dvx : 0.0;
            double my = root.TryGetProperty("my", out JsonElement myEl) && myEl.TryGetDouble(out double dvy) ? dvy : 0.0;
            double conf = root.TryGetProperty("conf", out JsonElement cfEl) && cfEl.TryGetDouble(out double dcf) ? dcf : 0.0;
            double rew = root.TryGetProperty("r", out JsonElement rwEl) && rwEl.TryGetDouble(out double drw) ? drw : 0.0;
            bool done = root.TryGetProperty("done", out JsonElement dnEl) && dnEl.ValueKind == JsonValueKind.True;
            string fStr = root.TryGetProperty("f", out JsonElement fEl) ? (fEl.GetString() ?? string.Empty) : string.Empty;

            if (!double.IsFinite(rew) || !double.IsFinite(mx) || !double.IsFinite(my))
            {
                return false;
            }

            double[] features = new double[BotPolicyArchitecture.StateDim];
            string[] parts = fStr.Split(',');
            int count = Math.Min(parts.Length, BotPolicyArchitecture.StateDim);
            for (int i = 0; i < count; i++)
            {
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double val) &&
                    double.IsFinite(val))
                {
                    features[i] = val;
                }
            }

            step = new RlTrajectoryStep
            {
                MatchId = matchId,
                Hero = hero,
                Team = team,
                ModelRole = role,
                Tick = tick,
                Features = features,
                ComboAction = Math.Clamp(combo, 0, BotPolicyArchitecture.ComboCount - 1),
                JukeFlag = juke,
                MoveX = mx,
                MoveY = my,
                Confidence = conf,
                Reward = Math.Clamp(rew, -10.0, 10.0),
                Done = done
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseEpisodeHeader(string json, out int matchId, out int candTeam, out int winnerTeam, out string? mapName)
    {
        matchId = 0;
        candTeam = 0;
        winnerTeam = 0;
        mapName = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("matchId", out JsonElement mEl) && mEl.TryGetInt32(out int m)) matchId = m;
            if (root.TryGetProperty("candTeam", out JsonElement ctEl) && ctEl.TryGetInt32(out int ct)) candTeam = ct;
            if (root.TryGetProperty("winnerTeam", out JsonElement wtEl) && wtEl.TryGetInt32(out int wt)) winnerTeam = wt;
            if (root.TryGetProperty("map", out JsonElement mpEl)) mapName = mpEl.GetString();
            return matchId > 0;
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveRuntimeDirectory(IConfiguration configuration)
    {
        string? configured = configuration["Compel:RlRuntimeDirectory"] ?? configuration["RL_RUNTIME_DIRECTORY"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string[] candidates =
        [
            "/app/server-rl-runtime",
            "/app/run-server/override/bots/rl_runtime",
            Path.Combine(AppContext.BaseDirectory, "bots", "rl_runtime"),
            Path.Combine(Directory.GetCurrentDirectory(), "games", "server", "override", "bots", "rl_runtime"),
        ];

        foreach (string c in candidates)
        {
            string? parent = Path.GetDirectoryName(c);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                return c;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "rl_runtime");
    }

    public static string ResolveTrajectoryDirectory(IConfiguration configuration)
    {
        string? configured = configuration["Compel:RlTrajectoryDirectory"] ?? configuration["RL_TRAJECTORY_DIRECTORY"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (Directory.Exists("/app/logs"))
        {
            return "/app/logs/rl_trajectories";
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "logs", "rl_trajectories");
    }

    internal enum RlWorkKind
    {
        RawLine = 0,
        MatchEnded = 1
    }

    internal readonly record struct RlQueueWorkItem(RlWorkKind Kind, int Port, string Line, int MatchId, string? MapName);

    private sealed class MatchStepBuffer
    {
        private readonly object _gate = new();
        public List<RlTrajectoryStep> Steps { get; } = [];

        public void AddStep(RlTrajectoryStep step)
        {
            lock (_gate)
            {
                // Cap per-episode memory in case a marathon match runs without ending (> 25,000 steps)
                if (Steps.Count < 25000)
                {
                    Steps.Add(step);
                }
            }
        }
    }
}

public sealed record RlTrainRequest(int Epochs = 6);
public sealed record RlRollbackRequest(int? TargetGeneration = null);
public sealed record RlExportResponse(bool Success, string OutputPath, int ExportedGeneration);
public sealed record RlTrainResponse(bool Success, RlTrainingSummary Summary);
public sealed record RlRollbackResponse(bool Success, int RestoredGeneration);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RlStatusSnapshot))]
[JsonSerializable(typeof(RlTrainRequest))]
[JsonSerializable(typeof(RlRollbackRequest))]
[JsonSerializable(typeof(RlExportResponse))]
[JsonSerializable(typeof(RlTrainResponse))]
[JsonSerializable(typeof(RlRollbackResponse))]
public sealed partial class RlControlPlaneJsonContext : JsonSerializerContext
{
}
