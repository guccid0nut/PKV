// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using MemoryPack;
using NEXUS.Contracts.Domain.Identifiers;
using Xunit;

namespace NEXUS.Contracts.Tests;

public class StronglyTypedIdTests
{
    [Fact]
    public void AccountId_ImplicitConversions_WorkSeamlessly()
    {
        AccountId id = 12345;
        int intVal = id;

        intVal.Should().Be(12345);
        id.Value.Should().Be(12345);
        id.Should().Be(new AccountId(12345));
    }

    [Fact]
    public void MatchId_ImplicitConversions_WorkSeamlessly()
    {
        MatchId id = 987654;
        int intVal = id;

        intVal.Should().Be(987654);
        id.Value.Should().Be(987654);
        id.Should().Be(new MatchId(987654));
    }

    [Fact]
    public void UserId_And_ClanId_ImplicitConversions_WorkSeamlessly()
    {
        UserId userId = 42;
        ClanId clanId = 777;

        int uInt = userId;
        int cInt = clanId;

        uInt.Should().Be(42);
        cInt.Should().Be(777);
    }

    [Fact]
    public void StronglyTypedIds_ComparisonsAndSorting_WorkCorrectly()
    {
        AccountId id1 = 10;
        AccountId id2 = 20;
        AccountId id3 = 30;

        (id1 < id2).Should().BeTrue();
        (id2 <= id3).Should().BeTrue();
        (id3 > id1).Should().BeTrue();
        (id3 >= id2).Should().BeTrue();

        List<AccountId> list = [id3, id1, id2];
        list.Sort();

        list.Should().Equal([new AccountId(10), new AccountId(20), new AccountId(30)]);
    }

    [Fact]
    public void StronglyTypedIds_ParsingAndFormatting_AreAccurate()
    {
        AccountId parsed = AccountId.Parse("1337");
        parsed.Value.Should().Be(1337);

        bool success = AccountId.TryParse("9999", null, out AccountId result);
        success.Should().BeTrue();
        result.Value.Should().Be(9999);

        bool fail = AccountId.TryParse("not-an-id", null, out _);
        fail.Should().BeFalse();

        parsed.ToString().Should().Be("1337");
    }

    [Fact]
    public void StronglyTypedIds_SystemTextJson_SerializesAsNumberAndReadsNumberOrString()
    {
        AccountId id = 54321;
        string json = JsonSerializer.Serialize(id);
        json.Should().Be("54321");

        AccountId deserializedNumber = JsonSerializer.Deserialize<AccountId>("54321");
        deserializedNumber.Value.Should().Be(54321);

        AccountId deserializedString = JsonSerializer.Deserialize<AccountId>("\"54321\"");
        deserializedString.Value.Should().Be(54321);
    }

    [Fact]
    public void StronglyTypedIds_MemoryPack_RoundTripsAccurately()
    {
        AccountId accountId = 112233;
        MatchId matchId = 445566;

        byte[] accBytes = MemoryPackSerializer.Serialize(accountId);
        AccountId restoredAcc = MemoryPackSerializer.Deserialize<AccountId>(accBytes);
        restoredAcc.Should().Be(accountId);

        byte[] matchBytes = MemoryPackSerializer.Serialize(matchId);
        MatchId restoredMatch = MemoryPackSerializer.Deserialize<MatchId>(matchBytes);
        restoredMatch.Should().Be(matchId);
    }

    [Fact]
    public void CacheKeyExtensions_WhenFormatted_FormatKeysCorrectly()
    {
        AccountId accountId = 1001;
        MatchId matchId = 2002;
        ClanId clanId = 3003;
        UserId userId = 4004;

        accountId.ToAccountSessionKey().Should().Be("ACC-SESSION:1001");
        accountId.ToAccountStatsKey().Should().Be("hero:stats:account:1001");
        accountId.ToAccountTag().Should().Be("account:1001");
        matchId.ToMatchKey().Should().Be("match:2002");
        clanId.ToClanTag().Should().Be("clan:3003");
        clanId.ToClanKey().Should().Be("clan:roster:3003");
        userId.ToUserKey().Should().Be("user:4004");
    }
}
