// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.InProcess;
using KONGOR.MasterServer.Extensions.Cache;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class DistributedCacheExtensionTests

{
    [Fact]
    public async Task DistributedCacheExtension_PurgeSessionCookies_RemovesOnlyMatchingKeys()

    {
        // Arrange

        IDatabase database = new InProcessDistributedCacheStore();

        Mock<IConnectionMultiplexer> mockMultiplexer = new();

        mockMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database);


        IConnectionMultiplexer multiplexer = mockMultiplexer.Object;


        // Clean up

        await multiplexer.PurgeSessionCookies();


        const string matchingKey1 = "ACCOUNT-SESSION-COOKIE:123";

        const string matchingKey2 = "ACCOUNT-SESSION-COOKIE:456";

        const string nonMatchingKey = "SOME-OTHER-KEY:789";


        await database.StringSetAsync(matchingKey1, "value1");

        await database.StringSetAsync(matchingKey2, "value2");

        await database.StringSetAsync(nonMatchingKey, "value3");


        // Act

        await multiplexer.PurgeSessionCookies();


        // Assert

        Assert.False(await database.KeyExistsAsync(matchingKey1));

        Assert.False(await database.KeyExistsAsync(matchingKey2));

        Assert.True(await database.KeyExistsAsync(nonMatchingKey));
    }
}
