// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.EntityCache;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Runtime;

public sealed class EntityCachePrewarmerTests
{
    private static EntityCacheContainer CreateTestContainer()
    {
        return new EntityCacheContainer
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("O"),
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 1,
                    Name = "Hero_Legionnaire",
                    DisplayName = "Legionnaire",
                    DefaultIcon = "/heroes/legionnaire/icon.tga",
                    DefaultModel = "hd_legionnaire/preview.mdf"
                }
            ]
        };
    }

    [Fact]
    public async Task Prewarmer_StartAsync_ExecutesBackgroundPrewarmWithoutBlocking()
    {
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        EntityCacheContainer container = CreateTestContainer();
        await registry.InitializeFromContainerAsync(container);

        using EntityCachePrewarmer prewarmer = new(
            registry,
            NullLogger<EntityCachePrewarmer>.Instance);

        // StartAsync should return immediately
        Task startTask = prewarmer.StartAsync(CancellationToken.None);
        Assert.True(startTask.IsCompletedSuccessfully);

        // Allow background threadpool task to finish
        await Task.Delay(50);
        Assert.True(registry.IsPrewarmed);

        await prewarmer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Prewarmer_Cancellation_HandlesGracefully()
    {
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        using EntityCachePrewarmer prewarmer = new(
            registry,
            NullLogger<EntityCachePrewarmer>.Instance);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Should not throw when starting with cancelled token
        Task startTask = prewarmer.StartAsync(cts.Token);
        Assert.True(startTask.IsCompletedSuccessfully);

        await prewarmer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Prewarmer_ExecutePrewarming_TouchesVfsAssetsSafely()
    {
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        EntityCacheContainer container = CreateTestContainer();
        await registry.InitializeFromContainerAsync(container);

        using EntityCachePrewarmer prewarmer = new(
            registry,
            NullLogger<EntityCachePrewarmer>.Instance);

        await prewarmer.ExecutePrewarmingAsync(CancellationToken.None);
        Assert.True(registry.IsPrewarmed);
    }
}
