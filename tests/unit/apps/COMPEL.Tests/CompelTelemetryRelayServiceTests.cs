// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using COMPEL.Services.Telemetry;

namespace COMPEL.Tests;

public sealed class CompelTelemetryRelayServiceTests
{
    [Fact]
    public async Task CompelTelemetryRelayService_OnServerStdoutLine_HandlesTeamSwitchEventsAndMatchIsolation()
    {
        MatchServerManagerOptions options = new() { Gateway = "localhost" };
        IOptions<MatchServerManagerOptions> optionsWrapper = Options.Create(options);
        AddressResolver addressResolver = new(optionsWrapper, NullLogger<AddressResolver>.Instance);
        await using CompelTelemetryRelayService service = new(addressResolver,
            NullLogger<CompelTelemetryRelayService>.Instance);

        // Process lines for Match #100
        service.OnServerStdoutLine(100, "Sv: [12:00:00] MatchID: 100");
        service.OnServerStdoutLine(100, "Sv: [12:00:01] Voice: Adding client num 0, guccidonut, ID: 1");
        service.OnServerStdoutLine(100, "Sv: [12:00:02] Client #0 joined team: 2");

        // Verify Match #100 state after sitting on Hellbourne (Team 2)
        LiveServerTickPayload? snapshotHellbourne = service.GetMatchSnapshot(100);
        Assert.NotNull(snapshotHellbourne);
        Assert.Single(snapshotHellbourne!.Players);
        Assert.Equal(2, snapshotHellbourne.Players[0].Team);
        Assert.Equal("guccidonut", snapshotHellbourne.Players[0].AccountName);
        Assert.Equal(0, snapshotHellbourne.Players[0].CurrentGold);
        Assert.Equal(0.740f, snapshotHellbourne.Players[0].PositionX);

        // Process lines for Match #200 concurrently
        service.OnServerStdoutLine(200, "Sv: [12:00:00] MatchID: 200");
        service.OnServerStdoutLine(200, "Sv: [12:00:01] Voice: Adding client num 0, teammate, ID: 2");
        service.OnServerStdoutLine(200, "Sv: [12:00:02] Client #0 joined team: 1");

        // Verify Match #200 state on Legion (Team 1)
        LiveServerTickPayload? snapshotMatch200 = service.GetMatchSnapshot(200);
        Assert.NotNull(snapshotMatch200);
        Assert.Equal(1, snapshotMatch200!.Players[0].Team);
        Assert.Equal("teammate", snapshotMatch200.Players[0].AccountName);
        Assert.Equal(0, snapshotMatch200.Players[0].CurrentGold);
        Assert.Equal(0.140f, snapshotMatch200.Players[0].PositionX);

        // Now move Match #100 back to team 1 (Legion)
        service.OnServerStdoutLine(100, "Sv: [12:00:05] Client #0 joined team: 1");

        LiveServerTickPayload? snapshotLegion = service.GetMatchSnapshot(100);
        Assert.NotNull(snapshotLegion);
        Assert.Equal(1, snapshotLegion!.Players[0].Team);
        Assert.Equal(0.140f, snapshotLegion.Players[0].PositionX);
        Assert.Equal(0.520f, snapshotLegion.Players[0].PositionY);
        Assert.Equal(0, snapshotLegion.Players[0].CurrentGold);
    }

    [Fact]
    public async Task OnServerStdoutLine_WithoutExplicitName_DefaultsToCleanPlayerSlot()
    {
        MatchServerManagerOptions options = new() { Gateway = "localhost" };
        IOptions<MatchServerManagerOptions> optionsWrapper = Options.Create(options);
        AddressResolver addressResolver = new(optionsWrapper, NullLogger<AddressResolver>.Instance);
        await using CompelTelemetryRelayService service = new(addressResolver,
            NullLogger<CompelTelemetryRelayService>.Instance);

        // Process team join without prior voice or connect name
        service.OnServerStdoutLine(300, "Sv: [12:00:00] MatchID: 300");
        service.OnServerStdoutLine(300, "Sv: [12:00:02] Client #0 joined team: 1");
        service.OnServerStdoutLine(300, "Sv: [12:00:03] Client #1 joined team: 2");

        LiveServerTickPayload? snapshot = service.GetMatchSnapshot(300);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Players.Count);
        Assert.Equal("Player #1", snapshot.Players[0].AccountName);
        Assert.Equal("Player #2", snapshot.Players[1].AccountName);
    }

    [Fact]
    public async Task OnServerStdoutLine_MultiPortIsolation_RoutesCorrectlyByPort()
    {
        MatchServerManagerOptions options = new() { Gateway = "localhost" };
        IOptions<MatchServerManagerOptions> optionsWrapper = Options.Create(options);
        AddressResolver addressResolver = new(optionsWrapper, NullLogger<AddressResolver>.Instance);
        await using CompelTelemetryRelayService service = new(addressResolver,
            NullLogger<CompelTelemetryRelayService>.Instance);

        const int portA = 11235;
        const int portB = 11236;

        // Worker A (Port 11235) announces Match #2060501
        service.OnServerStdoutLine(portA, "Sv: [12:00:00] MatchID: 2060501");
        // Worker B (Port 11236) announces Match #2060602
        service.OnServerStdoutLine(portB, "Sv: [12:00:00] MatchID: 2060602");

        // Worker A sends lines without MatchID (mapped by port)
        service.OnServerStdoutLine(portA, "Sv: [12:00:01] Voice: Adding client num 0, AlphaPlayer, ID: 10");
        service.OnServerStdoutLine(portA, "Sv: [12:00:02] Client #0 joined team: 1");

        // Worker B sends lines without MatchID (mapped by port)
        service.OnServerStdoutLine(portB, "Sv: [12:00:01] Voice: Adding client num 0, BetaPlayer, ID: 20");
        service.OnServerStdoutLine(portB, "Sv: [12:00:02] Client #0 joined team: 2");

        LiveServerTickPayload? snapshotA = service.GetMatchSnapshot(2060501);
        LiveServerTickPayload? snapshotB = service.GetMatchSnapshot(2060602);

        Assert.NotNull(snapshotA);
        Assert.NotNull(snapshotB);

        Assert.Single(snapshotA!.Players);
        Assert.Equal("AlphaPlayer", snapshotA.Players[0].AccountName);
        Assert.Equal(1, snapshotA.Players[0].Team);

        Assert.Single(snapshotB!.Players);
        Assert.Equal("BetaPlayer", snapshotB.Players[0].AccountName);
        Assert.Equal(2, snapshotB.Players[0].Team);
    }

    [Fact]
    public async Task OnServerStdoutLine_NonTelemetryLine_RejectedWithoutStateChange()
    {
        MatchServerManagerOptions options = new() { Gateway = "localhost" };
        IOptions<MatchServerManagerOptions> optionsWrapper = Options.Create(options);
        AddressResolver addressResolver = new(optionsWrapper, NullLogger<AddressResolver>.Instance);
        await using CompelTelemetryRelayService service = new(addressResolver,
            NullLogger<CompelTelemetryRelayService>.Instance);

        // Process various non-telemetry engine log lines
        service.OnServerStdoutLine(999, "Physics step took 16.2ms on worker 11235");
        service.OnServerStdoutLine(999, "NavMesh path query completed for entity 1024");
        service.OnServerStdoutLine(999, "Cvar 'cl_packetEpsilon' set to 0.001");
        service.OnServerStdoutLine(999, "Sound system loaded 14 wav files");

        // Verify no match state was created
        LiveServerTickPayload? snapshot = service.GetMatchSnapshot(999);
        Assert.Null(snapshot);
    }

    [Fact]
    public void CompelTelemetryRelayService_NormalizeHeroIdentifier_NormalizesAndCachesCorrectly()
    {
        string id1 = CompelTelemetryRelayService.NormalizeHeroIdentifier("Hero_Pyromancer".AsSpan());
        string id2 = CompelTelemetryRelayService.NormalizeHeroIdentifier("Hero_Pyromancer".AsSpan());
        string id3 = CompelTelemetryRelayService.NormalizeHeroIdentifier("hero_pyromancer".AsSpan());
        string id4 = CompelTelemetryRelayService.NormalizeHeroIdentifier("HERO_FLINT".AsSpan());

        Assert.Equal("hero_pyromancer", id1);
        Assert.Equal("hero_flint", id4);
        Assert.Same(id1, id2); // String instance caching
        Assert.Same(id1, id3); // Case-insensitive alternate lookup match
    }

    [Fact]
    public void CompelTelemetryRelayService_GetOrInternString_ReturnsSameReferenceForIdenticalSpans()
    {
        string item1 = CompelTelemetryRelayService.GetOrInternString("Item_FlamingEye".AsSpan());
        string item2 = CompelTelemetryRelayService.GetOrInternString("Item_FlamingEye".AsSpan());
        string ability1 = CompelTelemetryRelayService.GetOrInternString("Ability_WitchSlayer1".AsSpan());
        string ability2 = CompelTelemetryRelayService.GetOrInternString("Ability_WitchSlayer1".AsSpan());

        Assert.Equal("Item_FlamingEye", item1);
        Assert.Same(item1, item2);
        Assert.Equal("Ability_WitchSlayer1", ability1);
        Assert.Same(ability1, ability2);
    }

    [Fact]
    public void CompelTelemetryRelayService_GetDefaultPlayerName_UsesPreBakedArray()
    {
        for (int i = 0; i < 32; i++)
        {
            string name1 = CompelTelemetryRelayService.GetDefaultPlayerName(i);
            string name2 = CompelTelemetryRelayService.GetDefaultPlayerName(i);
            Assert.Equal($"Player #{i + 1}", name1);
            Assert.Same(name1, name2); // Reference identity verification
        }
    }
}
