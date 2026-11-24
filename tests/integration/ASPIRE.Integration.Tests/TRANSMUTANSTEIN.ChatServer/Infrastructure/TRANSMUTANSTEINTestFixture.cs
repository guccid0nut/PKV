// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;

public class TRANSMUTANSTEINTestFixture : IAsyncLifetime
{
    public TRANSMUTANSTEINServiceProvider Provider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await PostgreSqlContainerFixture.EnsureStartedAsync();
        Provider = await TRANSMUTANSTEINServiceProvider.CreateOrchestratedInstanceAsync(0, allowTwoPlayerFallback: true);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await Provider.DisposeAsync();
        }
        catch (Exception)
        {
            // Suppress cleanup exceptions during test fixture teardown
        }
    }
}

[CollectionDefinition("TRANSMUTANSTEIN ChatServer Collection")]
public class TRANSMUTANSTEINCollection : ICollectionFixture<TRANSMUTANSTEINTestFixture>;
