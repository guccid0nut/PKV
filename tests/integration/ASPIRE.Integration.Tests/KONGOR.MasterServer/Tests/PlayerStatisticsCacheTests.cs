// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using KONGOR.MasterServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class PlayerStatisticsCacheTests

{
    [Fact]
    public async Task PlayerStatisticsCache_InvalidateAggregatedStatistics_EvictsTagsAndKeysFromHybridCache()

    {
        await using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();


        Mock<INexusHybridCache> mockCache = new();


        PlayerStatisticsService service = new(dbContext, mockCache.Object,
            NullLogger<PlayerStatisticsService>.Instance);


        const int accountId = 42;


        await service.InvalidateAggregatedStatisticsAsync(accountId);


        mockCache.Verify(c => c.RemoveByTagAsync("account:42", It.IsAny<CancellationToken>()), Times.Once);

        mockCache.Verify(c => c.RemoveAsync("stats:aggregated:42", It.IsAny<CancellationToken>()), Times.Once);

        mockCache.Verify(c => c.RemoveAsync("stats:mastery:42", It.IsAny<CancellationToken>()), Times.Once);
    }
}
