// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public class DynamicMatchmakingTests
{
    [Fact]
    public void GetDynamicRatingSpread_InitialQueueEntry_ReturnsFifty()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        MatchmakingGroup group = new() { Members = [], Information = CreateDummyGroupInfo(), QueueStartTime = now };

        double spread = MatchmakingService.GetDynamicRatingSpread(group, now);
        Assert.Equal(50.0, spread, 2);
    }

    [Fact]
    public void GetDynamicRatingSpread_ExpandsOverTime_ReachesMaxCapAtThreeMinutes()
    {
        DateTimeOffset start = TimeProvider.System.GetUtcNow();
        MatchmakingGroup group = new() { Members = [], Information = CreateDummyGroupInfo(), QueueStartTime = start };

        // At 1 minute (60 seconds): 50 + (6 * 6.9444) = 91.66 MMR
        DateTimeOffset oneMinLater = start.AddSeconds(60);
        double spread60s = MatchmakingService.GetDynamicRatingSpread(group, oneMinLater);
        Assert.InRange(spread60s, 90.0, 93.0);

        // At 3 minutes (180 seconds): capped at 175 MMR
        DateTimeOffset threeMinLater = start.AddSeconds(180);
        double spread180s = MatchmakingService.GetDynamicRatingSpread(group, threeMinLater);
        Assert.Equal(175.0, spread180s, 1);

        // At 10 minutes: still capped at 175 MMR
        DateTimeOffset tenMinLater = start.AddMinutes(10);
        double spreadTenMin = MatchmakingService.GetDynamicRatingSpread(group, tenMinLater);
        Assert.Equal(175.0, spreadTenMin, 1);
    }

    [Fact]
    public void DynamicMatchmaking_CanCombineGroupsWithinSpread_RejectsPlayersDivergentByMoreThanMaxCap()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset queueStart = now.AddMinutes(-5); // queued 5 mins (max cap 175 MMR)

        MatchmakingGroup groupA = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1400.0)]
        };

        MatchmakingGroup groupB = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1600.0)]
        };

        bool compatible = MatchmakingService.CanCombineGroupsWithinSpread(
            [groupA],
            groupB,
            now);

        Assert.False(compatible);
    }

    [Fact]
    public void DynamicMatchmaking_CanCombineGroupsWithinSpread_AcceptsPlayersWithinSpreadCap()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset queueStart = now.AddMinutes(-3); // queued 3 mins (cap 175 MMR)

        MatchmakingGroup groupA = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1450.0)]
        };

        MatchmakingGroup groupB = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1600.0)]
        };

        bool compatible = MatchmakingService.CanCombineGroupsWithinSpread(
            [groupA],
            groupB,
            now);

        Assert.True(compatible);
    }

    [Fact]
    public void CanCombineGroupsWithinSpread_ExpandsWithQueueTime_AllowsGreaterRatingGaps()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset queueStartInitial = now; // 0s wait -> spread is 50 MMR
        DateTimeOffset queueStartOneMin = now.AddSeconds(-60); // 60s wait -> spread ~91.66 MMR

        MatchmakingGroup groupA = new()
        {
            QueueStartTime = queueStartInitial,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1500.0)]
        };

        MatchmakingGroup groupB = new()
        {
            QueueStartTime = queueStartInitial,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1575.0)]
        };

        // At t=0s: rating difference is 75 MMR, allowed spread is 50 MMR -> Incompatible
        bool compatibleAtStart = MatchmakingService.CanCombineGroupsWithinSpread([groupA], groupB, now);
        Assert.False(compatibleAtStart);

        // At t=60s: queue start is 60s ago -> allowed spread is > 90 MMR -> Compatible
        groupA.QueueStartTime = queueStartOneMin;
        groupB.QueueStartTime = queueStartOneMin;
        bool compatibleAfterWait = MatchmakingService.CanCombineGroupsWithinSpread([groupA], groupB, now);
        Assert.True(compatibleAfterWait);
    }

    [Fact]
    public void QueueRetention_RetainsOriginalStartTime_SpreadContinuesExpanding()
    {
        DateTimeOffset queueStart = TimeProvider.System.GetUtcNow().AddMinutes(-2); // Queued for 2 minutes
        MatchmakingGroup group = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1500.0)]
        };

        DateTimeOffset check1 = queueStart.AddSeconds(60);
        double spreadAt1Min = MatchmakingService.GetDynamicRatingSpread(group, check1);

        DateTimeOffset check2 = queueStart.AddSeconds(120);
        double spreadAt2Min = MatchmakingService.GetDynamicRatingSpread(group, check2);

        DateTimeOffset check3 = queueStart.AddSeconds(180);
        double spreadAt3Min = MatchmakingService.GetDynamicRatingSpread(group, check3);

        Assert.True(spreadAt2Min > spreadAt1Min);
        Assert.True(spreadAt3Min >= spreadAt2Min);
        Assert.Equal(175.0, spreadAt3Min, 1);
    }

    [Fact]
    public void CalculateEffectiveGroupRating_AppliesCarryBiasSkewToDisparateStacks()
    {
        // 1. Solo player: unchanged
        MatchmakingGroup solo = new()
        {
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1500.0)]
        };
        Assert.Equal(1500.0, MatchmakingService.CalculateEffectiveGroupRating(solo));

        // 2. Balanced Duo: unchanged
        MatchmakingGroup balancedDuo = new()
        {
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1600.0), CreateDummyMember(1600.0)]
        };
        Assert.Equal(1600.0, MatchmakingService.CalculateEffectiveGroupRating(balancedDuo));

        // 3. Disparate Duo: 1900 & 1400 -> Raw Avg = 1650, Spread = 500, Skew (0.15*500) = 75 -> Effective = 1725
        MatchmakingGroup disparateDuo = new()
        {
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1900.0), CreateDummyMember(1400.0)]
        };
        Assert.Equal(1725.0, MatchmakingService.CalculateEffectiveGroupRating(disparateDuo));
    }

    [Fact]
    public void GetDynamicRatingSpread_WithHighDisparityStack_AppliesFrictionDelayBeforeExpansion()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        // High disparity duo: 1900 & 1400 (spread 500 > 75). Delay = 60s
        MatchmakingGroup disparateDuo = new()
        {
            QueueStartTime = now,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1900.0), CreateDummyMember(1400.0)]
        };

        // At t=30s (within 60s delay window): spread has not expanded, remains at 50.0 MMR
        DateTimeOffset check30s = now.AddSeconds(30);
        double spread30s = MatchmakingService.GetDynamicRatingSpread(disparateDuo, check30s);
        Assert.Equal(50.0, spread30s);

        // At t=90s (30s after delay ends): effective duration is 30s -> spread ~70.83 MMR
        DateTimeOffset check90s = now.AddSeconds(90);
        double spread90s = MatchmakingService.GetDynamicRatingSpread(disparateDuo, check90s);
        Assert.True(spread90s > 70.0 && spread90s < 71.5);
    }

    [Fact]
    public void CanCombineGroupsWithinSpread_PremadePartyWithWideInternalSpread_MatchesBasedOnEffectiveRating()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset queueStart = now; // 0s wait -> 50 MMR spread cap

        // Group A: Friends party with 500 MMR internal spread (1900 and 1400) -> Effective = 1725
        MatchmakingGroup groupA = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members =
            [
                CreateDummyMember(1900.0),
                CreateDummyMember(1400.0)
            ]
        };

        // Group B: Candidate solo player at 1710 MMR -> Effective = 1710
        MatchmakingGroup groupB = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1710.0)]
        };

        // Spread between effective ratings is |1725 - 1710| = 15 MMR <= 50 MMR allowed spread
        bool compatible = MatchmakingService.CanCombineGroupsWithinSpread([groupA], groupB, now);
        Assert.True(compatible);

        // Candidate at 1600 MMR (far below effective 1725, gap = 125 MMR > 50 MMR) is REJECTED at t=0s
        MatchmakingGroup lowCandidate = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members = [CreateDummyMember(1600.0)]
        };
        bool compatibleLow = MatchmakingService.CanCombineGroupsWithinSpread([groupA], lowCandidate, now);
        Assert.False(compatibleLow);
    }

    [Fact]
    public void CanCombineGroupsWithinSpread_PremadePartyVsPremadeParty_MatchesBasedOnGroupAverages()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset queueStart = now; // 0s wait -> 50 MMR spread cap

        // Group A: Duo (1800 and 1500) -> Spread 300 -> Effective: 1650 + (0.15*300) = 1695
        MatchmakingGroup groupA = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members =
            [
                CreateDummyMember(1800.0),
                CreateDummyMember(1500.0)
            ]
        };

        // Group B: Trio (1700, 1690, 1680) -> Spread 20 -> Effective: 1690 + (0.15*20) = 1693
        MatchmakingGroup groupB = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members =
            [
                CreateDummyMember(1700.0),
                CreateDummyMember(1690.0),
                CreateDummyMember(1680.0)
            ]
        };

        // Spread between effective ratings is |1695 - 1693| = 2 MMR <= 50 MMR allowed spread
        bool compatible = MatchmakingService.CanCombineGroupsWithinSpread([groupA], groupB, now);
        Assert.True(compatible);
    }

    [Fact]
    public void CanCombineGroupsWithinSpread_PremadePartiesWithDivergentAverages_RejectsUntilQueueExpands()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset queueStart = now; // 0s wait -> 50 MMR spread cap

        // Group A: Duo (1900 and 1900) -> Average = 1900
        MatchmakingGroup groupA = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members =
            [
                CreateDummyMember(1900.0),
                CreateDummyMember(1900.0)
            ]
        };

        // Group B: Duo (1820 and 1820) -> Average = 1820
        MatchmakingGroup groupB = new()
        {
            QueueStartTime = queueStart,
            Information = CreateDummyGroupInfo(),
            Members =
            [
                CreateDummyMember(1820.0),
                CreateDummyMember(1820.0)
            ]
        };

        // At t=0s: Average difference is 80 MMR > 50 MMR allowed -> Rejected
        bool compatibleAtStart = MatchmakingService.CanCombineGroupsWithinSpread([groupA], groupB, now);
        Assert.False(compatibleAtStart);

        // At t=60s: Allowed spread > 90 MMR -> Compatible
        DateTimeOffset queueStartOneMin = now.AddSeconds(-60);
        groupA.QueueStartTime = queueStartOneMin;
        groupB.QueueStartTime = queueStartOneMin;
        bool compatibleAfterWait = MatchmakingService.CanCombineGroupsWithinSpread([groupA], groupB, now);
        Assert.True(compatibleAfterWait);
    }

    private static MatchmakingGroupInformation CreateDummyGroupInfo()
    {
        return new MatchmakingGroupInformation
        {
            ClientVersion = "4.10.0",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_SOLO,
            GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
            MapName = "caldavar",
            GameModes = ["normal"],
            GameRegions = ["USE"],
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };
    }

    private static MatchmakingGroupMember CreateDummyMember(double rating)
    {
        Account account = new()
        {
            ID = Random.Shared.Next(1, 100000),
            Name = "TestPlayer",
            User = null!,
            IsMain = true
        };

        Mock<IServiceProvider> mockProvider = new();
        mockProvider.Setup(p => p.GetService(typeof(ILogger<ChatSession>)))
            .Returns(NullLogger<ChatSession>.Instance);
        mockProvider.Setup(p => p.GetService(typeof(IChatContext)))
            .Returns(Mock.Of<IChatContext>());
        mockProvider.Setup(p => p.GetService(typeof(IDiscordPresencePublisherService)))
            .Returns(Mock.Of<IDiscordPresencePublisherService>());
        mockProvider.Setup(p => p.GetService(typeof(IMatchmakingService)))
            .Returns(Mock.Of<IMatchmakingService>());
        mockProvider.Setup(p => p.GetService(typeof(IWebHostEnvironment)))
            .Returns(Mock.Of<IWebHostEnvironment>());

        ChatSession session = new(null!, mockProvider.Object) { Account = account };

        return new MatchmakingGroupMember(session)
        {
            Slot = 1,
            IsLeader = true,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = rating,
            GameModeAccess = "true|true"
        };
    }
}
