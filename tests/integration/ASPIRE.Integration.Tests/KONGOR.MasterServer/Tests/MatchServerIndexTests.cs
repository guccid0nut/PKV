// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Extensions.Cache;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MatchServerIndexTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    [Fact]
    public async Task MatchServerIndex_Lifecycle_WorksCorrectly()

    {
        // Arrange

        using IServiceScope scope = fixture.CreateScope();

        IConnectionMultiplexer multiplexer = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();

        IDatabase database = multiplexer.GetDatabase();


        int serverId = Random.Shared.Next(10000, 99999);

        string hostName = $"TestHost_{Guid.NewGuid():N}"[..12];


        MatchServer server = new()

        {
            ID = serverId,

            HostAccountID = 1,

            HostAccountName = hostName,

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 10000,

            Location = "USE",

            Name = "TestServer",

            Description = "Unit Test Server"
        };


        const string matchServersKey = "MATCH-SERVERS";

        const string matchServerIndexKey = "MATCH-SERVER-INDEX";


        // Act 1: Set (Should populate Index)

        await database.SetMatchServer(server.HostAccountName, server);


        // Assert 1

        Assert.True(await database.HashExistsAsync(matchServerIndexKey, server.ID));

        Assert.True(await database.HashExistsAsync(matchServersKey, $"{server.HostAccountName}:{server.Port}"));


        // Act 2: Get (Should use Index - implicit verify by result)

        MatchServer? retrieved = await database.GetMatchServerByID(server.ID);

        Assert.NotNull(retrieved);

        Assert.Equal(server.ID, retrieved.ID);


        // Act 3: Fallback & Repair (Delete Index, Get should still work and repair index)

        await database.HashDeleteAsync(matchServerIndexKey, server.ID);

        Assert.False(await database.HashExistsAsync(matchServerIndexKey, server.ID));


        MatchServer? retrievedFallback = await database.GetMatchServerByID(server.ID);

        Assert.NotNull(retrievedFallback);

        Assert.Equal(server.ID, retrievedFallback.ID);


        Assert.True(await database.HashExistsAsync(matchServerIndexKey, server.ID));


        // Act 4: Remove (Should remove Index)

        await database.RemoveMatchServerByID(server.ID);


        Assert.False(await database.HashExistsAsync(matchServerIndexKey, server.ID));

        Assert.False(await database.HashExistsAsync(matchServersKey, $"{server.HostAccountName}:{server.Port}"));
    }
}
