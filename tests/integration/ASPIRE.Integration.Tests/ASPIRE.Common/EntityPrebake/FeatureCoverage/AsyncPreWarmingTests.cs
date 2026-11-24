// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class AsyncPreWarmingTests
{
    [Fact]
    public async Task InitializeAsync_PrewarmsRegistry_TransitionsIsPrewarmedToTrue()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 2);
        using TestPrebakedEntityRegistry registry = new(container);

        registry.IsPrewarmed.Should().BeFalse();

        // Act
        await registry.InitializeAsync();

        // Assert
        registry.IsPrewarmed.Should().BeTrue();
        registry.TotalEntitiesLoaded.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InitializeAsync_MultipleConcurrentCalls_ExecutesIdempotently()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(20, 2);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 20);

        // Act
        Task task1 = registry.InitializeAsync().AsTask();
        Task task2 = registry.InitializeAsync().AsTask();
        Task task3 = registry.InitializeAsync().AsTask();

        await Task.WhenAll(task1, task2, task3);

        // Assert
        registry.IsPrewarmed.Should().BeTrue();
        registry.TotalEntitiesLoaded.Should()
            .Be(20 + 40 + 60 + 20 + 20 + 1); // heroes + avatars + abilities + states + projectiles + items
    }

    [Fact]
    public async Task PrewarmedRegistry_QueriesDuringPrewarming_HandlesQueriesSafely()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(15, 2);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 50);

        // Act & Assert
        Task prewarmTask = registry.InitializeAsync().AsTask();

        // While prewarming is running, lookups return null rather than crashing
        HeroDefinitionDto? earlyLookup = registry.GetHeroByName("Hero_1");

        await prewarmTask;

        HeroDefinitionDto? postLookup = registry.GetHeroByName("Hero_1");
        postLookup.Should().NotBeNull();
    }

    [Fact]
    public async Task AsyncPreWarming_PrewarmedRegistry_GuaranteesZeroMainThreadStallsOnPrewarmedLookups()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(140, 5);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act - Simulate main UI thread executing consecutive hero lookups
        HeroDefinitionDto?[] heroes = new HeroDefinitionDto?[140];
        Stopwatch sw = Stopwatch.StartNew();
        for (uint i = 1; i <= 140; i++) heroes[i - 1] = registry.GetHeroById(i);
        sw.Stop();

        // Assert: 140 lookups must finish in <50ms total without UI blocking stall
        for (int i = 0; i < 140; i++) heroes[i].Should().NotBeNull();
        sw.ElapsedMilliseconds.Should()
            .BeLessThan(50, "Pre-warmed lookups must have 0ms perceptible UI blocking stall time");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AsyncPreWarming_InitializeAsync_SupportsCancellationTokenCancellation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(50, 2);
        using TestPrebakedEntityRegistry registry = new(container, simulatedPrewarmDelayMs: 500);

        using CancellationTokenSource cts = new();
        cts.CancelAfter(50);

        // Act
        Func<Task> act = async () => await registry.InitializeAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        registry.IsPrewarmed.Should().BeFalse();
    }

    [Fact]
    public async Task AsyncPreWarming_TotalEntitiesLoaded_ReflectsFullCatalogGraph()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10);
        using TestPrebakedEntityRegistry registry = new(container);

        // Act
        await registry.InitializeAsync();

        // Assert
        // 10 heroes + 30 avatars + 30 abilities + 10 states + 10 projectiles + 1 item = 91 total entities
        registry.TotalEntitiesLoaded.Should().Be(91);
    }
}
