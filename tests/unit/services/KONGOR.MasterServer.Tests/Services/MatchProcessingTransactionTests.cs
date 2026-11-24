// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Domain;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class MatchProcessingTransactionTests
{
    private static IngestedMatchContext CreateTestContext(int matchId)
    {
        return new IngestedMatchContext
        {
            MatchID = matchId,
            MatchStatistics = new MatchStatistics
            {
                MatchID = matchId,
                Map = "caldavar",
                MapVersion = "1.0",
                TimePlayed = 1200,
                FileSize = 1000,
                FileName = "test.replay",
                Version = "4.10.1.0",
                ScoreTeam1 = 30,
                ScoreTeam2 = 15,
                AveragePSR = 1500,
                AveragePSRTeamOne = 1500,
                AveragePSRTeamTwo = 1500,
                GameMode = "bot"
            },
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            },
            MatchType = AccountStatisticsType.Matchmaking,
            DurationSeconds = 1200,
            PlayerAccountNames = new Dictionary<int, string>(),
            AccountMap = new Dictionary<string, Account>(),
            ProcessedPlayerEntries = new List<PlayerIngestionEntry>()
        };
    }

    [Fact]
    public async Task ProcessMatchSubmission_WhenPipelineSucceeds_ExecutesPersistThenFinalizeSideEffects()
    {
        // Arrange
        int matchId = 5001;
        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId,
            RemoteIp = "127.0.0.1",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            }
        };

        IngestedMatchContext context = CreateTestContext(matchId);

        Mock<IMatchIngestionHandler> mockIngestion = new();
        mockIngestion.Setup(i => i.IngestMatchSubmissionAsync(payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        Mock<IHeroMasteryProgressionService> mockMastery = new();
        Mock<IOpenSkillRatingCalculator> mockRating = new();
        Mock<IReplayPersistenceWorker> mockPersistence = new();

        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        MatchProcessingService service = new(
            mockIngestion.Object,
            mockMastery.Object,
            mockRating.Object,
            mockPersistence.Object,
            NullLogger<MatchProcessingService>.Instance,
            db);

        // Act
        await service.ProcessMatchSubmissionAsync(payload);

        // Assert
        mockIngestion.Verify(i => i.IngestMatchSubmissionAsync(payload, It.IsAny<CancellationToken>()), Times.Once);
        mockMastery.Verify(m => m.ProcessProgressionAndRewardsAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        mockRating.Verify(r => r.CalculateAndUpdateRatingsAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        mockPersistence.Verify(p => p.PersistAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        mockPersistence.Verify(p => p.FinalizeSideEffectsAsync(context, payload.MatchServer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMatchSubmission_WhenCalculationFails_DoesNotPersistOrFinalizeSideEffects()
    {
        // Arrange
        int matchId = 5002;
        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId,
            RemoteIp = "127.0.0.1",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            }
        };

        IngestedMatchContext context = CreateTestContext(matchId);

        Mock<IMatchIngestionHandler> mockIngestion = new();
        mockIngestion.Setup(i => i.IngestMatchSubmissionAsync(payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        Mock<IHeroMasteryProgressionService> mockMastery = new();
        Mock<IOpenSkillRatingCalculator> mockRating = new();
        mockRating.Setup(r => r.CalculateAndUpdateRatingsAsync(context, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Rating algorithm simulation error"));

        Mock<IReplayPersistenceWorker> mockPersistence = new();

        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        MatchProcessingService service = new(
            mockIngestion.Object,
            mockMastery.Object,
            mockRating.Object,
            mockPersistence.Object,
            NullLogger<MatchProcessingService>.Instance,
            db);

        // Act
        Func<Task> act = async () => await service.ProcessMatchSubmissionAsync(payload);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Rating algorithm simulation error");

        mockPersistence.Verify(p => p.PersistAsync(It.IsAny<IngestedMatchContext>(), It.IsAny<CancellationToken>()), Times.Never);
        mockPersistence.Verify(p => p.FinalizeSideEffectsAsync(It.IsAny<IngestedMatchContext>(), It.IsAny<MatchServer>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMatchSubmission_WhenPersistenceFails_DoesNotFinalizeSideEffects()
    {
        // Arrange
        int matchId = 5003;
        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId,
            RemoteIp = "127.0.0.1",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            }
        };

        IngestedMatchContext context = CreateTestContext(matchId);

        Mock<IMatchIngestionHandler> mockIngestion = new();
        mockIngestion.Setup(i => i.IngestMatchSubmissionAsync(payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        Mock<IHeroMasteryProgressionService> mockMastery = new();
        Mock<IOpenSkillRatingCalculator> mockRating = new();
        Mock<IReplayPersistenceWorker> mockPersistence = new();
        mockPersistence.Setup(p => p.PersistAsync(context, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("Simulated unique constraint conflict"));

        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        MatchProcessingService service = new(
            mockIngestion.Object,
            mockMastery.Object,
            mockRating.Object,
            mockPersistence.Object,
            NullLogger<MatchProcessingService>.Instance,
            db);

        // Act
        Func<Task> act = async () => await service.ProcessMatchSubmissionAsync(payload);

        // Assert
        await act.Should().ThrowAsync<DbUpdateException>()
            .WithMessage("Simulated unique constraint conflict");

        mockPersistence.Verify(p => p.FinalizeSideEffectsAsync(It.IsAny<IngestedMatchContext>(), It.IsAny<MatchServer>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMatchSubmission_WhenSideEffectFails_DoesNotThrowException()
    {
        // Arrange
        int matchId = 5004;
        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId,
            RemoteIp = "127.0.0.1",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            }
        };

        IngestedMatchContext context = CreateTestContext(matchId);

        Mock<IMatchIngestionHandler> mockIngestion = new();
        mockIngestion.Setup(i => i.IngestMatchSubmissionAsync(payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        Mock<IHeroMasteryProgressionService> mockMastery = new();
        Mock<IOpenSkillRatingCalculator> mockRating = new();
        Mock<IReplayPersistenceWorker> mockPersistence = new();
        mockPersistence.Setup(p => p.FinalizeSideEffectsAsync(context, payload.MatchServer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Redis temporary socket timeout"));

        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        MatchProcessingService service = new(
            mockIngestion.Object,
            mockMastery.Object,
            mockRating.Object,
            mockPersistence.Object,
            NullLogger<MatchProcessingService>.Instance,
            db);

        // Act
        Func<Task> act = async () => await service.ProcessMatchSubmissionAsync(payload);

        // Assert
        await act.Should().NotThrowAsync();
        mockPersistence.Verify(p => p.PersistAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        mockPersistence.Verify(p => p.FinalizeSideEffectsAsync(context, payload.MatchServer, It.IsAny<CancellationToken>()), Times.Once);
    }
}
