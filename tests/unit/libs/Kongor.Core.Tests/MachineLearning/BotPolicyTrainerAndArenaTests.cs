// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.MachineLearning;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.MachineLearning;

public sealed class BotPolicyTrainerAndArenaTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"kongor_rl_core_tests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup
        }
    }

    [Fact]
    public void CreateSeedBaseline_Validates4928Parameters_AndProducesValidLuaModule()
    {
        // protects: Canonical 128->32->16->16 architecture invariants and C# -> Lua weights module export syntax.
        // fails_when: Matrix/bias dimensions drift from Lua rl_inference.lua expectations or Lua export emits malformed syntax.
        // seam: BotPolicyNetwork.CreateSeedBaseline + ValidateCheckpoint + Forward + RenderLuaWeightsModule.
        BotPolicyCheckpoint baseline = BotPolicyNetwork.CreateSeedBaseline(1337);
        BotPolicyNetwork.ValidateCheckpoint(baseline, out string? error).Should().BeTrue(error);

        double[] state = new double[BotPolicyArchitecture.StateDim];
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = (i % 4 == 0) ? 0.5 : -0.25;
        }

        BotPolicyForwardPass pass = BotPolicyNetwork.Forward(baseline, state);
        pass.Output.Should().HaveCount(BotPolicyArchitecture.ActionDim);
        pass.ComboProbs.Should().HaveCount(BotPolicyArchitecture.ComboCount);
        pass.ComboProbs.Sum().Should().BeApproximately(1.0, 1e-6);
        pass.ConfidenceProb.Should().BeInRange(0.0, 1.0);
        pass.JukeProb.Should().BeInRange(0.0, 1.0);

        string lua = BotPolicyNetwork.RenderLuaWeightsModule(baseline);
        lua.Should().Contain("return {");
        lua.Should().Contain("W1 = {");
        lua.Should().Contain("W_OUT = {");
        lua.Should().Contain("B_OUT = {");
        lua.Should().Contain("generation = 0,");
    }

    [Fact]
    public void ValidateCheckpoint_RejectsNaNInfinityAndOutOfBoundsWeights()
    {
        // protects: Runtime game-server safety guard against NaN, Infinity, or exploding weights reaching live bots.
        // fails_when: Corrupt checkpoint passes validation and bricks Lua forward evaluation.
        // seam: BotPolicyNetwork.ValidateCheckpoint + RlArenaCoordinator.InstallExternalCandidate.
        BotPolicyCheckpoint bad = BotPolicyNetwork.CreateSeedBaseline();
        bad.W1[0][0] = double.NaN;
        BotPolicyNetwork.ValidateCheckpoint(bad, out string? err1).Should().BeFalse();
        err1.Should().Contain("W1[0][0]");

        bad.W1[0][0] = 100.0;
        BotPolicyNetwork.ValidateCheckpoint(bad, out string? err2).Should().BeFalse();
        err2.Should().Contain("W1[0][0]");

        RlArenaCoordinator coordinator = new(_tempDir);
        Action act = () => coordinator.InstallExternalCandidate(bad);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TrainAwac_ReducesLossAndIncreasesPreferredComboProbability_OnSyntheticTrajectories()
    {
        // protects: AWAC + GAE + Critic Value MSE + Confidence BCE convergence and gradient flow across all 3 layers.
        // fails_when: Advantage weighting or backprop fails to reinforce positive-reward actions or diverges into NaN.
        // seam: Real BotPolicyTrainer.TrainAwac + BotPolicyNetwork.Forward.
        BotPolicyCheckpoint start = BotPolicyNetwork.CreateSeedBaseline();
        double[] positiveState = Enumerable.Range(0, BotPolicyArchitecture.StateDim).Select(i => i < 16 ? 1.0 : 0.0).ToArray();
        double[] negativeState = Enumerable.Range(0, BotPolicyArchitecture.StateDim).Select(i => i < 16 ? -1.0 : 0.0).ToArray();

        double initialProbCombo2 = BotPolicyNetwork.Forward(start, positiveState).ComboProbs[2];

        List<RlTrajectoryStep> steps = [];
        for (int tick = 1; tick <= 24; tick++)
        {
            bool good = tick % 2 == 0;
            steps.Add(new RlTrajectoryStep
            {
                MatchId = 5001,
                Hero = "Hero_Armadon",
                Team = 1,
                ModelRole = "candidate",
                Tick = tick * 1000,
                Features = good ? positiveState : negativeState,
                ComboAction = good ? 2 : 0,
                JukeFlag = good,
                MoveX = good ? 0.8 : -0.6,
                MoveY = good ? 0.4 : -0.4,
                Confidence = 0.85,
                Reward = good ? 2.5 : -1.5,
                Done = tick == 24
            });
        }

        RlEpisodeRecord ep = new()
        {
            MatchId = 5001,
            WinnerTeam = 1,
            CandidateTeam = 1,
            Steps = steps
        };

        (BotPolicyCheckpoint trained, RlTrainingSummary summary) = BotPolicyTrainer.TrainAwac(
            start,
            [ep],
            new AwacTrainingOptions(Epochs: 20, LearningRate: 1e-2));

        summary.StepsUsed.Should().Be(24);
        summary.FinalLoss.Should().BeLessThan(summary.InitialLoss);
        trained.Generation.Should().Be(1);

        double postProbCombo2 = BotPolicyNetwork.Forward(trained, positiveState).ComboProbs[2];
        postProbCombo2.Should().BeGreaterThan(initialProbCombo2);
    }

    [Fact]
    public void RlArenaCoordinator_EnforcesPromotionGate_RetainsHistory_AndRollsBackCleanly()
    {
        // protects: Champion safety — losing candidates must never overwrite Champion; winning candidates promote atomically,
        //           archive old Champion in history/, and rollback restores exact previous generation + Lua table.
        // fails_when: Regression promotes despite win rate < 55%, loses backup history, or rollback fails to rewrite champion.lua.
        // seam: RlArenaCoordinator file storage + RecordEpisodeAndMaybeEvaluate + RollbackChampion + ExportChampionToBaselineLua.
        RlArenaCoordinator arena = new(_tempDir, new RlArenaOptions(MinEvaluationMatches: 4, MinPromotionWinRate: 0.55));
        arena.EnsureInitialized();

        // Install Candidate Gen 1
        BotPolicyCheckpoint candGen1 = BotPolicyNetwork.CreateSeedBaseline(2026);
        candGen1.Generation = 1;
        candGen1.Role = "candidate";
        arena.InstallExternalCandidate(candGen1);

        // 1. Losing evaluation window (1 win / 4 matches = 25%): MUST NOT promote Gen 1!
        for (int i = 0; i < 4; i++)
        {
            int candTeam = (i % 2) + 1;
            int winnerTeam = i == 0 ? candTeam : (candTeam == 1 ? 2 : 1);
            RlArenaEvaluationResult res = arena.RecordEpisodeAndMaybeEvaluate(new RlEpisodeRecord
            {
                MatchId = 100 + i,
                WinnerTeam = winnerTeam,
                CandidateTeam = candTeam,
                MeanCandidateReturn = i == 0 ? 1.0 : -0.5,
                MeanChampionReturn = 0.8,
                Steps = []
            });
            if (i == 3)
            {
                res.Evaluated.Should().BeTrue();
                res.Promoted.Should().BeFalse();
                res.ChampionGeneration.Should().Be(0);
            }
        }

        // 2. Winning evaluation window (3 wins / 4 matches = 75% AND higher return): MUST promote Gen 1 & archive Gen 0!
        for (int i = 0; i < 4; i++)
        {
            int candTeam = (i % 2) + 1;
            int winnerTeam = i < 3 ? candTeam : (candTeam == 1 ? 2 : 1);
            RlArenaEvaluationResult res = arena.RecordEpisodeAndMaybeEvaluate(new RlEpisodeRecord
            {
                MatchId = 200 + i,
                WinnerTeam = winnerTeam,
                CandidateTeam = candTeam,
                MeanCandidateReturn = i < 3 ? 2.0 : 0.2,
                MeanChampionReturn = 0.4,
                Steps = []
            });
            if (i == 3)
            {
                res.Evaluated.Should().BeTrue();
                res.Promoted.Should().BeTrue();
                res.ChampionGeneration.Should().Be(1);
            }
        }

        arena.ListBackupGenerations().Should().Contain(0);
        File.ReadAllText(arena.ChampionLuaPath).Should().Contain("generation = 1");

        // 3. Export Champion to baseline Lua path & verify rollback back to Gen 0!
        string baselineOut = Path.Combine(_tempDir, "rl_weights_baseline.lua");
        arena.ExportChampionToBaselineLua(baselineOut);
        File.ReadAllText(baselineOut).Should().Contain("generation = 1");

        BotPolicyCheckpoint rolledBack = arena.RollbackChampion(0);
        rolledBack.Generation.Should().Be(0);
        File.ReadAllText(arena.ChampionLuaPath).Should().Contain("generation = 0");
    }
}
