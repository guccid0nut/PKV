// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Globalization;
using System.Text.Json;

namespace ASPIRE.Common.MachineLearning;

public sealed record RlArenaOptions(
    int MinEvaluationMatches = 20,
    double MinPromotionWinRate = 0.55,
    int MaxBackupGenerations = 20,
    int MaxTrainingEpisodes = 200);

public sealed class RlArenaCoordinator
{
    private readonly string _runtimeDirectory;
    private readonly RlArenaOptions _options;
    private readonly object _sync = new();

    private RlArenaState _state = new();

    public RlArenaCoordinator(string runtimeDirectory, RlArenaOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            throw new ArgumentException("Runtime directory must be provided.", nameof(runtimeDirectory));
        }

        _runtimeDirectory = runtimeDirectory;
        _options = options ?? new RlArenaOptions();
    }

    public string RuntimeDirectory => _runtimeDirectory;
    public string ChampionJsonPath => Path.Combine(_runtimeDirectory, "champion.json");
    public string ChampionLuaPath => Path.Combine(_runtimeDirectory, "champion.lua");
    public string CandidateJsonPath => Path.Combine(_runtimeDirectory, "candidate.json");
    public string CandidateLuaPath => Path.Combine(_runtimeDirectory, "candidate.lua");
    public string ArenaStatePath => Path.Combine(_runtimeDirectory, "arena_state.json");
    public string HistoryDirectory => Path.Combine(_runtimeDirectory, "history");

    public void EnsureInitialized()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_runtimeDirectory);
            Directory.CreateDirectory(HistoryDirectory);

            BotPolicyCheckpoint? champion = TryLoadCheckpoint(ChampionJsonPath);
            if (champion is null)
            {
                champion = BotPolicyNetwork.CreateSeedBaseline();
                champion.Role = "champion";
                champion.Generation = 0;
                WriteCheckpointAtomic(champion, ChampionJsonPath, ChampionLuaPath);
            }
            else if (!File.Exists(ChampionLuaPath))
            {
                WriteTextAtomic(ChampionLuaPath, BotPolicyNetwork.RenderLuaWeightsModule(champion));
            }

            BotPolicyCheckpoint? candidate = TryLoadCheckpoint(CandidateJsonPath);
            if (candidate is null)
            {
                candidate = BotPolicyNetwork.CloneCheckpoint(champion, "candidate", champion.Generation);
                WriteCheckpointAtomic(candidate, CandidateJsonPath, CandidateLuaPath);
            }
            else if (!File.Exists(CandidateLuaPath))
            {
                WriteTextAtomic(CandidateLuaPath, BotPolicyNetwork.RenderLuaWeightsModule(candidate));
            }

            if (File.Exists(ArenaStatePath))
            {
                try
                {
                    string json = File.ReadAllText(ArenaStatePath);
                    RlArenaState? loaded = JsonSerializer.Deserialize(json, RlJsonContext.Default.RlArenaState);
                    if (loaded is not null)
                    {
                        _state = loaded;
                    }
                }
                catch
                {
                    _state = new RlArenaState();
                }
            }

            _state.ChampionGeneration = champion.Generation;
            _state.CandidateGeneration = Math.Max(_state.CandidateGeneration, candidate.Generation);
            SaveStateAtomicUnsafe();
        }
    }

    public RlArenaState GetArenaState()
    {
        lock (_sync)
        {
            EnsureInitialized();
            return new RlArenaState
            {
                ChampionGeneration = _state.ChampionGeneration,
                CandidateGeneration = _state.CandidateGeneration,
                ArenaMatchesPlayed = _state.ArenaMatchesPlayed,
                CandidateWins = _state.CandidateWins,
                ChampionWins = _state.ChampionWins,
                Draws = _state.Draws,
                CandidateReturnSum = _state.CandidateReturnSum,
                ChampionReturnSum = _state.ChampionReturnSum,
                TotalEpisodesIngested = _state.TotalEpisodesIngested,
                TotalStepsIngested = _state.TotalStepsIngested,
                LastTrainedAtUtc = _state.LastTrainedAtUtc,
                LastPromotedAtUtc = _state.LastPromotedAtUtc,
                LastTrainingLoss = _state.LastTrainingLoss,
                PromotionHistory = [.. _state.PromotionHistory]
            };
        }
    }

    public (BotPolicyCheckpoint Champion, BotPolicyCheckpoint Candidate) LoadModels()
    {
        lock (_sync)
        {
            EnsureInitialized();
            BotPolicyCheckpoint champion = TryLoadCheckpoint(ChampionJsonPath)
                ?? throw new InvalidOperationException("Failed to load Champion RL checkpoint.");
            BotPolicyCheckpoint candidate = TryLoadCheckpoint(CandidateJsonPath)
                ?? throw new InvalidOperationException("Failed to load Candidate RL checkpoint.");
            return (champion, candidate);
        }
    }

    public RlArenaEvaluationResult RecordEpisodeAndMaybeEvaluate(
        RlEpisodeRecord episode,
        IReadOnlyList<RlEpisodeRecord>? trainingCorpus = null,
        AwacTrainingOptions? trainingOptions = null)
    {
        ArgumentNullException.ThrowIfNull(episode);

        lock (_sync)
        {
            EnsureInitialized();
            _state.TotalEpisodesIngested++;
            _state.TotalStepsIngested += episode.Steps?.Count ?? 0;

            if (episode.CandidateTeam is 1 or 2)
            {
                _state.ArenaMatchesPlayed++;
                if (episode.WinnerTeam == episode.CandidateTeam)
                {
                    _state.CandidateWins++;
                }
                else if (episode.WinnerTeam is 1 or 2)
                {
                    _state.ChampionWins++;
                }
                else
                {
                    _state.Draws++;
                }

                if (double.IsFinite(episode.MeanCandidateReturn))
                {
                    _state.CandidateReturnSum += episode.MeanCandidateReturn;
                }
                if (double.IsFinite(episode.MeanChampionReturn))
                {
                    _state.ChampionReturnSum += episode.MeanChampionReturn;
                }
            }

            if (_state.ArenaMatchesPlayed < _options.MinEvaluationMatches)
            {
                SaveStateAtomicUnsafe();
                return new RlArenaEvaluationResult(
                    Evaluated: false,
                    Promoted: false,
                    Reason: $"collecting_arena_samples ({_state.ArenaMatchesPlayed}/{_options.MinEvaluationMatches})",
                    CandidateWinRate: ComputeWinRateUnsafe(),
                    ChampionMeanReturn: ComputeMeanReturnUnsafe(_state.ChampionReturnSum),
                    CandidateMeanReturn: ComputeMeanReturnUnsafe(_state.CandidateReturnSum),
                    MatchesEvaluated: _state.ArenaMatchesPlayed,
                    ChampionGeneration: _state.ChampionGeneration,
                    CandidateGeneration: _state.CandidateGeneration);
            }

            double winRate = ComputeWinRateUnsafe();
            double champMean = ComputeMeanReturnUnsafe(_state.ChampionReturnSum);
            double candMean = ComputeMeanReturnUnsafe(_state.CandidateReturnSum);
            int matches = _state.ArenaMatchesPlayed;

            bool promoted = false;
            string reason;

            BotPolicyCheckpoint? champion = TryLoadCheckpoint(ChampionJsonPath);
            BotPolicyCheckpoint? candidate = TryLoadCheckpoint(CandidateJsonPath);

            if (champion is not null &&
                candidate is not null &&
                winRate >= _options.MinPromotionWinRate &&
                candMean > champMean &&
                BotPolicyNetwork.ValidateCheckpoint(candidate, out _))
            {
                ArchiveChampionUnsafe(champion);
                BotPolicyCheckpoint nextChampion = BotPolicyNetwork.CloneCheckpoint(candidate, "champion", candidate.Generation);
                nextChampion.MeanEvalReturn = candMean;
                nextChampion.EvalWinRate = winRate;
                nextChampion.EvaluatedMatches = matches;
                WriteCheckpointAtomic(nextChampion, ChampionJsonPath, ChampionLuaPath);

                _state.PromotionHistory.Add(new RlPromotionRecord
                {
                    PromotedAtUtc = TimeProvider.System.GetUtcNow(),
                    PreviousChampionGeneration = champion.Generation,
                    NewChampionGeneration = nextChampion.Generation,
                    CandidateWinRate = winRate,
                    ChampionMeanReturn = champMean,
                    CandidateMeanReturn = candMean,
                    MatchesEvaluated = matches
                });
                if (_state.PromotionHistory.Count > 50)
                {
                    _state.PromotionHistory.RemoveRange(0, _state.PromotionHistory.Count - 50);
                }

                _state.ChampionGeneration = nextChampion.Generation;
                _state.LastPromotedAtUtc = TimeProvider.System.GetUtcNow();
                champion = nextChampion;
                promoted = true;
                reason = $"promoted_gen_{nextChampion.Generation}";
            }
            else
            {
                reason = $"rejected_gate (WinRate={winRate:P1} vs {_options.MinPromotionWinRate:P1}, CandReturn={candMean:F3} vs ChampReturn={champMean:F3})";
            }

            // Reset arena counters for the next evaluation cycle
            _state.ArenaMatchesPlayed = 0;
            _state.CandidateWins = 0;
            _state.ChampionWins = 0;
            _state.Draws = 0;
            _state.CandidateReturnSum = 0.0;
            _state.ChampionReturnSum = 0.0;

            if (trainingCorpus is { Count: > 0 } && champion is not null)
            {
                int nextGen = Math.Max(_state.ChampionGeneration, _state.CandidateGeneration) + 1;
                (BotPolicyCheckpoint nextCandidate, RlTrainingSummary summary) = BotPolicyTrainer.TrainAwac(
                    champion,
                    trainingCorpus,
                    trainingOptions,
                    nextGeneration: nextGen);
                WriteCheckpointAtomic(nextCandidate, CandidateJsonPath, CandidateLuaPath);
                _state.CandidateGeneration = nextCandidate.Generation;
                _state.LastTrainedAtUtc = TimeProvider.System.GetUtcNow();
                _state.LastTrainingLoss = summary.FinalLoss.ToString("F5", CultureInfo.InvariantCulture);
            }

            SaveStateAtomicUnsafe();
            return new RlArenaEvaluationResult(
                Evaluated: true,
                Promoted: promoted,
                Reason: reason,
                CandidateWinRate: winRate,
                ChampionMeanReturn: champMean,
                CandidateMeanReturn: candMean,
                MatchesEvaluated: matches,
                ChampionGeneration: _state.ChampionGeneration,
                CandidateGeneration: _state.CandidateGeneration);
        }
    }

    public RlTrainingSummary TrainNewCandidate(
        IReadOnlyList<RlEpisodeRecord> trainingCorpus,
        AwacTrainingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trainingCorpus);

        lock (_sync)
        {
            EnsureInitialized();
            BotPolicyCheckpoint champion = TryLoadCheckpoint(ChampionJsonPath)
                ?? throw new InvalidOperationException("Champion checkpoint missing.");

            int nextGen = Math.Max(_state.ChampionGeneration, _state.CandidateGeneration) + 1;
            IReadOnlyList<RlEpisodeRecord> sliced = trainingCorpus.Count > _options.MaxTrainingEpisodes
                ? trainingCorpus.Skip(trainingCorpus.Count - _options.MaxTrainingEpisodes).ToList()
                : trainingCorpus;

            (BotPolicyCheckpoint candidate, RlTrainingSummary summary) =
                BotPolicyTrainer.TrainAwac(champion, sliced, options, nextGen);

            WriteCheckpointAtomic(candidate, CandidateJsonPath, CandidateLuaPath);

            _state.CandidateGeneration = candidate.Generation;
            _state.ArenaMatchesPlayed = 0;
            _state.CandidateWins = 0;
            _state.ChampionWins = 0;
            _state.Draws = 0;
            _state.CandidateReturnSum = 0.0;
            _state.ChampionReturnSum = 0.0;
            _state.LastTrainedAtUtc = TimeProvider.System.GetUtcNow();
            _state.LastTrainingLoss = summary.FinalLoss.ToString("F5", CultureInfo.InvariantCulture);
            SaveStateAtomicUnsafe();

            return summary;
        }
    }

    public void InstallExternalCandidate(BotPolicyCheckpoint candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!BotPolicyNetwork.ValidateCheckpoint(candidate, out string? error))
        {
            throw new InvalidOperationException($"Rejected external candidate checkpoint: {error}");
        }

        lock (_sync)
        {
            EnsureInitialized();
            int nextGen = Math.Max(candidate.Generation, Math.Max(_state.ChampionGeneration, _state.CandidateGeneration) + 1);
            BotPolicyCheckpoint stamped = BotPolicyNetwork.CloneCheckpoint(candidate, "candidate", nextGen);
            WriteCheckpointAtomic(stamped, CandidateJsonPath, CandidateLuaPath);

            _state.CandidateGeneration = stamped.Generation;
            _state.ArenaMatchesPlayed = 0;
            _state.CandidateWins = 0;
            _state.ChampionWins = 0;
            _state.Draws = 0;
            _state.CandidateReturnSum = 0.0;
            _state.ChampionReturnSum = 0.0;
            _state.LastTrainedAtUtc = TimeProvider.System.GetUtcNow();
            _state.LastTrainingLoss = stamped.MeanTrainingLoss.ToString("F5", CultureInfo.InvariantCulture);
            SaveStateAtomicUnsafe();
        }
    }

    public BotPolicyCheckpoint RollbackChampion(int? targetGeneration = null)
    {
        lock (_sync)
        {
            EnsureInitialized();
            string[] backups = Directory.Exists(HistoryDirectory)
                ? [.. Directory.GetFiles(HistoryDirectory, "gen_*.json").OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)]
                : [];

            string? chosenPath = null;
            if (targetGeneration.HasValue)
            {
                string exact = Path.Combine(HistoryDirectory, $"gen_{targetGeneration.Value:D4}.json");
                if (File.Exists(exact))
                {
                    chosenPath = exact;
                }
            }
            else if (backups.Length > 0)
            {
                chosenPath = backups[0];
            }

            BotPolicyCheckpoint restored;
            if (chosenPath is not null)
            {
                restored = TryLoadCheckpoint(chosenPath)
                    ?? throw new InvalidOperationException($"Corrupt backup checkpoint at '{chosenPath}'.");
            }
            else if (targetGeneration is null or 0)
            {
                restored = BotPolicyNetwork.CreateSeedBaseline();
            }
            else
            {
                throw new FileNotFoundException($"Backup checkpoint for generation {targetGeneration.Value} was not found.");
            }

            restored.Role = "champion";
            WriteCheckpointAtomic(restored, ChampionJsonPath, ChampionLuaPath);
            _state.ChampionGeneration = restored.Generation;
            _state.ArenaMatchesPlayed = 0;
            _state.CandidateWins = 0;
            _state.ChampionWins = 0;
            _state.Draws = 0;
            _state.CandidateReturnSum = 0.0;
            _state.ChampionReturnSum = 0.0;
            SaveStateAtomicUnsafe();

            return restored;
        }
    }

    public string ExportChampionToBaselineLua(string outputBaselineLuaPath)
    {
        if (string.IsNullOrWhiteSpace(outputBaselineLuaPath))
        {
            throw new ArgumentException("Output path must be provided.", nameof(outputBaselineLuaPath));
        }

        lock (_sync)
        {
            EnsureInitialized();
            BotPolicyCheckpoint champion = TryLoadCheckpoint(ChampionJsonPath)
                ?? throw new InvalidOperationException("Champion checkpoint missing.");
            BotPolicyCheckpoint baselineCopy = BotPolicyNetwork.CloneCheckpoint(champion, "baseline", champion.Generation);
            string luaContent = BotPolicyNetwork.RenderLuaWeightsModule(baselineCopy);
            WriteTextAtomic(outputBaselineLuaPath, luaContent);
            return outputBaselineLuaPath;
        }
    }

    public IReadOnlyList<int> ListBackupGenerations()
    {
        lock (_sync)
        {
            EnsureInitialized();
            if (!Directory.Exists(HistoryDirectory))
            {
                return [];
            }

            List<int> gens = [];
            foreach (string file in Directory.GetFiles(HistoryDirectory, "gen_*.json"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length > 4 && int.TryParse(stem[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gen))
                {
                    gens.Add(gen);
                }
            }
            gens.Sort();
            return gens;
        }
    }

    private double ComputeWinRateUnsafe()
    {
        int decided = _state.CandidateWins + _state.ChampionWins;
        if (decided > 0)
        {
            return (double)_state.CandidateWins / decided;
        }
        return _state.ArenaMatchesPlayed > 0
            ? (_state.CandidateWins + 0.5 * _state.Draws) / _state.ArenaMatchesPlayed
            : 0.0;
    }

    private double ComputeMeanReturnUnsafe(double sum)
    {
        return _state.ArenaMatchesPlayed > 0 ? sum / _state.ArenaMatchesPlayed : 0.0;
    }

    private void ArchiveChampionUnsafe(BotPolicyCheckpoint previousChampion)
    {
        Directory.CreateDirectory(HistoryDirectory);
        string archivePath = Path.Combine(HistoryDirectory, $"gen_{previousChampion.Generation:D4}.json");
        string json = JsonSerializer.Serialize(previousChampion, RlJsonContext.Default.BotPolicyCheckpoint);
        WriteTextAtomic(archivePath, json);

        string[] allBackups = [.. Directory.GetFiles(HistoryDirectory, "gen_*.json").OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)];
        for (int i = _options.MaxBackupGenerations; i < allBackups.Length; i++)
        {
            try
            {
                File.Delete(allBackups[i]);
            }
            catch
            {
                // Best-effort history pruning
            }
        }
    }

    private static BotPolicyCheckpoint? TryLoadCheckpoint(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            BotPolicyCheckpoint? cp = JsonSerializer.Deserialize(json, RlJsonContext.Default.BotPolicyCheckpoint);
            return BotPolicyNetwork.ValidateCheckpoint(cp, out _) ? cp : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCheckpointAtomic(BotPolicyCheckpoint checkpoint, string jsonPath, string luaPath)
    {
        string json = JsonSerializer.Serialize(checkpoint, RlJsonContext.Default.BotPolicyCheckpoint);
        string lua = BotPolicyNetwork.RenderLuaWeightsModule(checkpoint);
        WriteTextAtomic(jsonPath, json);
        WriteTextAtomic(luaPath, lua);
    }

    private void SaveStateAtomicUnsafe()
    {
        string json = JsonSerializer.Serialize(_state, RlJsonContext.Default.RlArenaState);
        WriteTextAtomic(ArenaStatePath, json);
    }

    private static void WriteTextAtomic(string targetPath, string content)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = targetPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
