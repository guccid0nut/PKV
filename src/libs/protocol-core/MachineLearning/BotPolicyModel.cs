// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Text.Json.Serialization;

namespace ASPIRE.Common.MachineLearning;

public static class BotPolicyArchitecture
{
    public const int StateDim = 128;
    public const int Hidden1 = 32;
    public const int Hidden2 = 16;
    public const int ActionDim = 16;

    // 0-based C# / PyTorch indices (1-based index in Lua is +1)
    public const int MoveXIndex = 0;
    public const int MoveYIndex = 1;
    public const int AttackTargetIndex = 2;
    public const int LegacyComboScalarIndex = 3;
    public const int ConfidenceIndex = 4;
    public const int AggroModifierIndex = 5;
    public const int JukeIndex = 6;
    public const int ValueHeadIndex = 7;
    public const int ComboLogitOffset = 8;
    public const int ComboCount = 5;

    public const int TotalParameters =
        (StateDim * Hidden1 + Hidden1) +
        (Hidden1 * Hidden2 + Hidden2) +
        (Hidden2 * ActionDim + ActionDim); // 4,928 parameters
}

public sealed class BotPolicyCheckpoint
{
    public int SchemaVersion { get; set; } = 1;
    public int Generation { get; set; }
    public int ParentGeneration { get; set; }
    public string Role { get; set; } = "baseline";
    public string SourceFramework { get; set; } = "csharp-awac";
    public DateTimeOffset CreatedAtUtc { get; set; } = TimeProvider.System.GetUtcNow();
    public double MeanTrainingLoss { get; set; }
    public double MeanEvalReturn { get; set; }
    public double EvalWinRate { get; set; }
    public int EvaluatedMatches { get; set; }
    public double[][] W1 { get; set; } = [];
    public double[] B1 { get; set; } = [];
    public double[][] W2 { get; set; } = [];
    public double[] B2 { get; set; } = [];
    public double[][] WOut { get; set; } = [];
    public double[] BOut { get; set; } = [];
}

public sealed class RlTrajectoryStep
{
    public int MatchId { get; set; }
    public string Hero { get; set; } = string.Empty;
    public int Team { get; set; }
    public string ModelRole { get; set; } = "champion";
    public int Tick { get; set; }
    public double[] Features { get; set; } = [];
    public int ComboAction { get; set; }
    public bool JukeFlag { get; set; }
    public double MoveX { get; set; }
    public double MoveY { get; set; }
    public double Confidence { get; set; }
    public double Reward { get; set; }
    public bool Done { get; set; }
}

public sealed class RlEpisodeRecord
{
    public int MatchId { get; set; }
    public string MapName { get; set; } = "caldavar";
    public int WinnerTeam { get; set; }
    public int CandidateTeam { get; set; }
    public double MatchDurationSeconds { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; } = TimeProvider.System.GetUtcNow();
    public double MeanChampionReturn { get; set; }
    public double MeanCandidateReturn { get; set; }
    public List<RlTrajectoryStep> Steps { get; set; } = [];
}

public sealed class RlPromotionRecord
{
    public DateTimeOffset PromotedAtUtc { get; set; } = TimeProvider.System.GetUtcNow();
    public int PreviousChampionGeneration { get; set; }
    public int NewChampionGeneration { get; set; }
    public double CandidateWinRate { get; set; }
    public double ChampionMeanReturn { get; set; }
    public double CandidateMeanReturn { get; set; }
    public int MatchesEvaluated { get; set; }
}

public sealed class RlArenaState
{
    public int ChampionGeneration { get; set; }
    public int CandidateGeneration { get; set; }
    public int ArenaMatchesPlayed { get; set; }
    public int CandidateWins { get; set; }
    public int ChampionWins { get; set; }
    public int Draws { get; set; }
    public double CandidateReturnSum { get; set; }
    public double ChampionReturnSum { get; set; }
    public long TotalEpisodesIngested { get; set; }
    public long TotalStepsIngested { get; set; }
    public DateTimeOffset? LastTrainedAtUtc { get; set; }
    public DateTimeOffset? LastPromotedAtUtc { get; set; }
    public string LastTrainingLoss { get; set; } = "n/a";
    public List<RlPromotionRecord> PromotionHistory { get; set; } = [];
}

public sealed record RlTrainingSummary(
    int EpisodesUsed,
    int StepsUsed,
    int Epochs,
    double InitialLoss,
    double FinalLoss,
    double PolicyLoss,
    double ValueLoss,
    double ConfidenceLoss,
    int NewCandidateGeneration);

public sealed record RlArenaEvaluationResult(
    bool Evaluated,
    bool Promoted,
    string Reason,
    double CandidateWinRate,
    double ChampionMeanReturn,
    double CandidateMeanReturn,
    int MatchesEvaluated,
    int ChampionGeneration,
    int CandidateGeneration);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BotPolicyCheckpoint))]
[JsonSerializable(typeof(RlTrajectoryStep))]
[JsonSerializable(typeof(RlEpisodeRecord))]
[JsonSerializable(typeof(RlArenaState))]
[JsonSerializable(typeof(RlPromotionRecord))]
[JsonSerializable(typeof(RlTrainingSummary))]
[JsonSerializable(typeof(RlArenaEvaluationResult))]
public sealed partial class RlJsonContext : JsonSerializerContext
{
}
