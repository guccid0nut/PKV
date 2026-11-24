// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.KONGOR.MasterServer.Infrastructure;

public class KONGORTestFixture : IAsyncLifetime
{
    public WebApplicationFactory<KONGORAssemblyMarker> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await PostgreSqlContainerFixture.EnsureStartedAsync();
        Factory = KONGORServiceProvider.CreateOrchestratedInstance(Guid.NewGuid().ToString());
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        try
        {
            Client.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await Factory.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public IServiceScope CreateScope()
    {
        return Factory.Services.CreateScope();
    }

    public MerrickContext GetDbContext(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<MerrickContext>();
    }

    public IDatabase GetCache(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<IDatabase>();
    }
}

[CollectionDefinition("KONGOR MasterServer Collection")]
public class KONGORCollection : ICollectionFixture<KONGORTestFixture>;
