// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class MerrickRepositoriesDITests
{
    [Fact]
    public void MerrickRepositoriesDI_AddMerrickRepositories_RegistersAllEightRepositories()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext());
        services.AddMerrickRepositories();

        ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAccountRepository>().Should().BeOfType<AccountRepository>();
        scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>().Should().BeOfType<MatchHistoryRepository>();
        scope.ServiceProvider.GetRequiredService<IInventoryRepository>().Should().BeOfType<InventoryRepository>();
        scope.ServiceProvider.GetRequiredService<ISessionRepository>().Should().BeOfType<SessionRepository>();
        scope.ServiceProvider.GetRequiredService<IClanRepository>().Should().BeOfType<ClanRepository>();
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>().Should().BeOfType<FriendshipRepository>();
        scope.ServiceProvider.GetRequiredService<IStoreRepository>().Should().BeOfType<StoreRepository>();
        scope.ServiceProvider.GetRequiredService<IGuideRepository>().Should().BeOfType<HeroGuideRepository>();
    }
}
