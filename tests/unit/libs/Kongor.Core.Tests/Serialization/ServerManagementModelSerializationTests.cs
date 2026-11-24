// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using MemoryPack;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Core.Tests.Serialization;

/// <summary>
///     Pins the Redis wire format of the server management cache models. These types are the single source of truth
///     shared by the chat server and the master server (both read/write MATCH-SERVERS, MATCH-SERVER-SESSIONS,
///     MATCH-SERVER-MANAGERS and MATCH-START-DATA:* via ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions).
/// </summary>
public sealed class ServerManagementModelSerializationTests(ITestOutputHelper output)
{
    [Fact]
    public void MatchOptions_FlagsShouldCombineCorrectly()
    {
        MatchOptions options = MatchOptions.AllPick | MatchOptions.CasualMode | MatchOptions.BanPhase;

        output.WriteLine($"[MatchOptions] combined value = {(int)options} ({options})");

        options.HasFlag(MatchOptions.AllPick).Should().BeTrue();
        options.HasFlag(MatchOptions.CasualMode).Should().BeTrue();
        options.HasFlag(MatchOptions.BanPhase).Should().BeTrue();
        options.HasFlag(MatchOptions.AllRandom).Should().BeFalse();
    }

    [Fact]
    public void MatchOptions_BitPositions_MustNotChange()
    {
        // Persisted inside MatchStartData in Redis; re-numbering would corrupt in-flight matches across a deploy.
        ((int)MatchOptions.AllPick).Should().Be(1 << 0);
        ((int)MatchOptions.CasualMode).Should().Be(1 << 7);
        ((int)MatchOptions.NoStatistics).Should().Be(1 << 21);
        ((int)MatchOptions.VerifiedOnly).Should().Be(1 << 29);
    }

    [Fact]
    public void ServerStatus_Ordinals_MustMatchChatServerProtocol()
    {
        ((int)ServerStatus.SERVER_STATUS_SLEEPING).Should().Be(0);
        ((int)ServerStatus.SERVER_STATUS_IDLE).Should().Be(1);
        ((int)ServerStatus.SERVER_STATUS_LOADING).Should().Be(2);
        ((int)ServerStatus.SERVER_STATUS_ACTIVE).Should().Be(3);
        ((int)ServerStatus.SERVER_STATUS_CRASHED).Should().Be(4);
        ((int)ServerStatus.SERVER_STATUS_KILLED).Should().Be(5);
        ((int)ServerStatus.SERVER_STATUS_UNKNOWN).Should().Be(6);
    }

    [Fact]
    public void MatchServer_MemoryPackRoundTrip_PreservesAllFields()
    {
        MatchServer original = new()
        {
            HostAccountID = 42,
            HostAccountName = "HostAcc",
            ID = 123456,
            Name = "Kongor US-East #1",
            MatchServerManagerID = 7,
            Instance = 3,
            IPAddress = "203.0.113.10",
            TruePublicIPAddress = "203.0.113.11",
            LocalIPAddress = "10.0.0.5",
            Port = 11235,
            LocalPort = 11236,
            Location = "USE",
            Description = "test",
            IsPrivate = true,
            Status = ServerStatus.SERVER_STATUS_ACTIVE,
            ConnectedPlayersCount = 10,
            Cookie = "cookie-abc",
            TimestampRegistered = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            TimestampLastHeartbeat = new DateTimeOffset(2026, 10, 3, 12, 5, 0, TimeSpan.Zero)
        };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        output.WriteLine($"[MatchServer] serialized {bytes.Length} bytes; first byte 0x{bytes[0]:X2}");

        // The cache deserializers treat a leading '{' as legacy JSON; MemoryPack output must never start with it.
        bytes[0].Should().NotBe((byte)'{');

        MatchServer? roundTripped = MemoryPackSerializer.Deserialize<MatchServer>(bytes);
        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void MatchStartData_MemoryPackRoundTrip_PreservesAllFields()
    {
        MatchStartData original = new()
        {
            MatchID = 1001,
            MatchName = "TMM",
            ServerID = 123456,
            ServerName = "Kongor US-East #1",
            HostAccountName = "HostAcc",
            Map = "caldavar",
            Version = "4.10.1",
            IsCasual = false,
            MatchType = 2,
            MatchMode = "ap",
            TimestampStarted = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            ConnectedPlayersCount = 10,
            MaximumPlayersCount = 10,
            Tier = 2,
            League = 1,
            Options = MatchOptions.AllPick | MatchOptions.TournamentRules,
            IsStarted = true,
            Phase = 4
        };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        output.WriteLine($"[MatchStartData] serialized {bytes.Length} bytes; first byte 0x{bytes[0]:X2}");

        bytes[0].Should().NotBe((byte)'{');

        MatchStartData? roundTripped = MemoryPackSerializer.Deserialize<MatchStartData>(bytes);
        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void MatchServerManager_MemoryPackRoundTrip_PreservesAllFields()
    {
        MatchServerManager original = new()
        {
            HostAccountID = 42,
            HostAccountName = "HostAcc",
            ID = 99,
            MatchServerIDs = [1, 2, 3],
            IPAddress = "203.0.113.10",
            Cookie = "manager-cookie",
            TimestampRegistered = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)
        };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        output.WriteLine($"[MatchServerManager] serialized {bytes.Length} bytes; first byte 0x{bytes[0]:X2}");

        bytes[0].Should().NotBe((byte)'{');

        MatchServerManager? roundTripped = MemoryPackSerializer.Deserialize<MatchServerManager>(bytes);
        roundTripped.Should().BeEquivalentTo(original);
    }
}
