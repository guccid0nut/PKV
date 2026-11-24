// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class AsyncPreWarmingBoundaryTests
{
    [Fact]
    public async Task InitializeAsync_ImmediatelyCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 100);

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Cancel immediately

        // Act
        Func<Task> act = async () => await registry.InitializeAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        registry.IsPrewarmed.Should().BeFalse();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task InitializeAsync_HighConcurrencyCalls_DoesNotCorruptState()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 2);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 10);

        List<Task> tasks = [];
        for (int i = 0; i < 50; i++) tasks.Add(registry.InitializeAsync().AsTask());

        // Act
        await Task.WhenAll(tasks);

        // Assert
        registry.IsPrewarmed.Should().BeTrue();
        registry.TotalEntitiesLoaded.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PrewarmedRegistry_ConcurrentReadersWhileNotPrewarmed_NeverDeadlocks()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(20, 2);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 30);

        List<Task> readerTasks = [];
        for (int i = 0; i < 20; i++)
        {
            uint id = (uint)(i % 5 + 1);
            readerTasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    registry.GetHeroById(id);
                    registry.GetHeroByName($"Hero_{id}");
                }
            }));
        }

        Task initTask = registry.InitializeAsync().AsTask();

        // Act
        await Task.WhenAll(readerTasks);
        await initTask;

        // Assert
        registry.IsPrewarmed.Should().BeTrue();
        registry.GetHeroById(1).Should().NotBeNull();
    }

    [Fact]
    public async Task InitializeAsync_ReinitializationAfterCompletion_IsNoOp()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);
        using TestPrebakedEntityRegistry registry = new(container);

        // Act
        await registry.InitializeAsync();
        int firstCount = registry.TotalEntitiesLoaded;

        await registry.InitializeAsync();
        int secondCount = registry.TotalEntitiesLoaded;

        // Assert
        firstCount.Should().Be(secondCount);
        registry.IsPrewarmed.Should().BeTrue();
    }

    [Fact]
    public async Task PrewarmedRegistry_QueriesDuringPrewarming_ReturnsSafelyWithoutException()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 1);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 50);

        // Act
        Task initTask = registry.InitializeAsync().AsTask();
        HeroDefinitionDto? earlyHero = registry.GetHeroById(1);
        await initTask;
        HeroDefinitionDto? lateHero = registry.GetHeroById(1);

        // Assert
        lateHero.Should().NotBeNull();
    }
}
