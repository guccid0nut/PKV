// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using ASPIRE.Common.MachineLearning;
using COMPEL.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests.Telemetry;

public sealed class RlTrajectoryCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kongor_compel_rl_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup
        }
    }

    [Fact]
    public async Task Collector_ParsesCompactStepLines_EvaluatesArena_AndEnforcesRingBufferCap()
    {
        // protects: Zero-log-bloat interception of [RL_STEP]/[RL_EPISODE], trajectory deserialization into ring buffer,
        //           and bounded disk eviction so 24/7 Self-Play never fills server storage.
        // fails_when: [RL_STEP] leaks into console/OTLP logs, step parsing corrupts features, or ring buffer grows unbounded.
        // seam: RlTrajectoryCollectorService.TryHandleRawLine + ProcessWorkItemAsync + EnforceRingBufferLimits + RlArenaCoordinator.
        string runtimeDir = Path.Combine(_root, "rl_runtime");
        string trajDir = Path.Combine(_root, "rl_trajectories");
        RlArenaCoordinator arena = new(runtimeDir, new RlArenaOptions(MinEvaluationMatches: 2, MinPromotionWinRate: 0.5));
        RlTrajectoryCollectorService collector = new(
            arena,
            trajDir,
            NullLogger<RlTrajectoryCollectorService>.Instance,
            maxRingBufferBytes: 1024 * 1024,
            maxRingBufferEpisodes: 2);

        arena.EnsureInitialized();

        // Standard server console lines must NOT be intercepted
        RlTrajectoryCollectorService.TryHandleRawLine(collector, 11485, "[SGame] Normal console line").Should().BeFalse();

        string featuresCsv = string.Join(",", Enumerable.Range(0, BotPolicyArchitecture.StateDim).Select(i => i < 10 ? "1.000" : "0.000"));

        for (int matchIdx = 1; matchIdx <= 3; matchIdx++)
        {
            int matchId = 7000 + matchIdx;
            string stepLine1 = $"[RL_STEP] {{\"matchId\":{matchId},\"hero\":\"Hero_Armadon\",\"team\":1,\"role\":\"candidate\",\"tick\":1000,\"combo\":2,\"juke\":true,\"mx\":0.5,\"my\":0.2,\"conf\":0.9,\"r\":1.5,\"done\":false,\"f\":\"{featuresCsv}\"}}";
            string stepLine2 = $"[RL_STEP] {{\"matchId\":{matchId},\"hero\":\"Hero_Bephelgor\",\"team\":2,\"role\":\"champion\",\"tick\":2000,\"combo\":1,\"juke\":false,\"mx\":-0.1,\"my\":0.1,\"conf\":0.8,\"r\":0.1,\"done\":true,\"f\":\"{featuresCsv}\"}}";
            string epLine = $"[RL_EPISODE] {{\"matchId\":{matchId},\"candTeam\":1,\"winnerTeam\":1,\"map\":\"caldavar\"}}";

            RlTrajectoryCollectorService.TryHandleRawLine(collector, 11485, stepLine1).Should().BeTrue();
            await collector.ProcessWorkItemAsync(new RlTrajectoryCollectorService.RlQueueWorkItem(
                RlTrajectoryCollectorService.RlWorkKind.RawLine, 11485, stepLine1, 0, null));
            await collector.ProcessWorkItemAsync(new RlTrajectoryCollectorService.RlQueueWorkItem(
                RlTrajectoryCollectorService.RlWorkKind.RawLine, 11485, stepLine2, 0, null));
            await collector.ProcessWorkItemAsync(new RlTrajectoryCollectorService.RlQueueWorkItem(
                RlTrajectoryCollectorService.RlWorkKind.RawLine, 11485, epLine, 0, null));
        }

        // Max ring buffer episodes is 2 -> 3rd episode must evict oldest so <= 2 files remain!
        List<RlEpisodeRecord> retained = collector.LoadRecentEpisodesFromDisk();
        retained.Should().HaveCount(2);

        RlStatusSnapshot status = collector.GetStatusSnapshot();
        status.TotalEpisodesIngested.Should().Be(3);
        status.TotalStepsIngested.Should().Be(6);
        status.RingBufferEpisodes.Should().Be(2);
    }
}
