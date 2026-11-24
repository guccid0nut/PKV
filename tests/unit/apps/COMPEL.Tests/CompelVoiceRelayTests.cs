// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelVoiceRelayTests.cs
// System: COMPEL - Opus Voice Datagram Relay Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Net;
using COMPEL.Services.Voice;
using FluentAssertions;
using Kongor.Core.Netcode.Voice;
using Kongor.Core.Services.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class CompelVoiceRelayTests
{
    [Fact]
    public void RegisterParticipant_IncreasesCountsAndStoresEndpoint()
    {
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11299);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);

        service.RegisterParticipant(VoiceChannelType.Party, channelId: 100, accountId: 1, ep1);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 100, accountId: 2, ep2);

        service.ActiveChannelsCount.Should().Be(1);
        service.ActiveParticipantsCount.Should().Be(2);

        // Update endpoint for account 1
        IPEndPoint ep1New = new IPEndPoint(IPAddress.Loopback, 50003);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 100, accountId: 1, ep1New);

        service.ActiveChannelsCount.Should().Be(1);
        service.ActiveParticipantsCount.Should().Be(2);
    }

    [Fact]
    public void UnregisterParticipant_RemovesParticipantAndEmptyChannel()
    {
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11299);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        service.RegisterParticipant(VoiceChannelType.Team, channelId: 777, accountId: 1, ep1);

        service.ActiveChannelsCount.Should().Be(1);
        service.ActiveParticipantsCount.Should().Be(1);

        service.UnregisterParticipant(VoiceChannelType.Team, channelId: 777, accountId: 1);

        service.ActiveChannelsCount.Should().Be(0);
        service.ActiveParticipantsCount.Should().Be(0);
    }

    [Fact]
    public void RoutePacket_DoesNotEchoToSender()
    {
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11299);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 200, accountId: 10, ep1);

        // Sender transmits in party 200, but is the only member
        byte[] payload = [0xAA, 0xBB];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 200,
            senderAccountId: 10,
            sequence: 1,
            payload: payload);

        // With no socket bound yet, RoutePacket iterates recipients and skips sender (returns 0)
        int relayed = service.RoutePacket(packet, ep1);
        relayed.Should().Be(0);
    }

    [Fact]
    public void RoutePacket_ForDifferentChannels_IsIsolated()
    {
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11299);

        // Party A (Channel 100): Account 1 and 2
        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 100, accountId: 1, ep1);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 100, accountId: 2, ep2);

        // Party B (Channel 200): Account 3 and 4
        IPEndPoint ep3 = new IPEndPoint(IPAddress.Loopback, 50003);
        IPEndPoint ep4 = new IPEndPoint(IPAddress.Loopback, 50004);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 200, accountId: 3, ep3);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 200, accountId: 4, ep4);

        service.ActiveChannelsCount.Should().Be(2);
        service.ActiveParticipantsCount.Should().Be(4);

        // Packet targeting non-existent channel 999
        VoiceDatagramPacket unknownPacket = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 999,
            senderAccountId: 1,
            sequence: 1,
            payload: new byte[] { 0x01 });

        service.RoutePacket(unknownPacket, ep1).Should().Be(0);
    }

    [Fact]
    public void RoutePacket_LegionAndHellbourneInSameMatch_NeverRouteCrossTeam()
    {
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11299);

        uint matchId = 5000;
        uint legionTeamId = VoiceClientService.GetTeamChannelId(matchId, teamIndex: 1);
        uint hellbourneTeamId = VoiceClientService.GetTeamChannelId(matchId, teamIndex: 2);

        legionTeamId.Should().NotBe(hellbourneTeamId);

        // Legion: Account 1 and 2
        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);
        service.RegisterParticipant(VoiceChannelType.Team, legionTeamId, accountId: 1, ep1);
        service.RegisterParticipant(VoiceChannelType.Team, legionTeamId, accountId: 2, ep2);

        // Hellbourne: Account 3 and 4
        IPEndPoint ep3 = new IPEndPoint(IPAddress.Loopback, 50003);
        IPEndPoint ep4 = new IPEndPoint(IPAddress.Loopback, 50004);
        service.RegisterParticipant(VoiceChannelType.Team, hellbourneTeamId, accountId: 3, ep3);
        service.RegisterParticipant(VoiceChannelType.Team, hellbourneTeamId, accountId: 4, ep4);

        service.ActiveChannelsCount.Should().Be(2);
        service.ActiveParticipantsCount.Should().Be(4);

        // Packet from Legion Account 1
        VoiceDatagramPacket legionPacket = new VoiceDatagramPacket(
            VoiceChannelType.Team,
            legionTeamId,
            senderAccountId: 1,
            sequence: 1,
            payload: new byte[] { 0xAA, 0xBB });

        // Packet targeting Legion channel should never reach Hellbourne channel
        service.RoutePacket(legionPacket, ep1);
    }
}

