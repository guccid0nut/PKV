// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelVoiceRelayMuteTests.cs
// System: COMPEL - Opus Voice Datagram Relay Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Net;
using COMPEL.Services.Voice;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Kongor.Core.Netcode.Voice;
using Kongor.Core.Services.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class CompelVoiceRelayMuteTests
{
    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RoutePacket_WhenSenderIsMuted_DropsPacketAndIncrementsCounters()
    {
        VoiceMuteRegistry muteRegistry = new VoiceMuteRegistry(_timeProvider);
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11298,
            timeProvider: _timeProvider,
            muteRegistry: muteRegistry);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);

        service.RegisterParticipant(VoiceChannelType.Team, channelId: 42, accountId: 1001, ep1);
        service.RegisterParticipant(VoiceChannelType.Team, channelId: 42, accountId: 1002, ep2);

        // Mute account 1001
        muteRegistry.SetMute(1001, _timeProvider.GetUtcNow().AddHours(2), "Griefing");

        byte[] payload = [0x01, 0x02, 0x03];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Team,
            channelId: 42,
            senderAccountId: 1001,
            sequence: 1,
            payload: payload);

        int relayed = service.RoutePacket(packet, ep1);

        relayed.Should().Be(0);
        service.TotalPacketsDropped.Should().Be(1);
        service.TotalPacketsMuted.Should().Be(1);
    }

    [Fact]
    public void RoutePacket_WhenSenderNotMuted_DoesNotIncrementMutedCounter()
    {
        VoiceMuteRegistry muteRegistry = new VoiceMuteRegistry(_timeProvider);
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11298,
            timeProvider: _timeProvider,
            muteRegistry: muteRegistry);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);

        service.RegisterParticipant(VoiceChannelType.Team, channelId: 42, accountId: 1001, ep1);
        service.RegisterParticipant(VoiceChannelType.Team, channelId: 42, accountId: 1002, ep2);

        byte[] payload = [0x01, 0x02, 0x03];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Team,
            channelId: 42,
            senderAccountId: 1001,
            sequence: 1,
            payload: payload);

        // Without a bound UDP socket, RoutePacket skips sender ep1 and attempts send to ep2
        int relayed = service.RoutePacket(packet, ep1);

        service.TotalPacketsMuted.Should().Be(0);
    }

    [Fact]
    public void RoutePacket_WhenMuteRemoved_AllowsRelayImmediately()
    {
        VoiceMuteRegistry muteRegistry = new VoiceMuteRegistry(_timeProvider);
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11298,
            timeProvider: _timeProvider,
            muteRegistry: muteRegistry);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);

        service.RegisterParticipant(VoiceChannelType.Party, channelId: 99, accountId: 2001, ep1);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 99, accountId: 2002, ep2);

        muteRegistry.SetMute(2001, _timeProvider.GetUtcNow().AddDays(1), "Temporary Mute");

        byte[] payload1 = new byte[] { 0x55, 0xAA };
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 99,
            senderAccountId: 2001,
            sequence: 1,
            payload: payload1);

        // Attempt while muted
        service.RoutePacket(packet, ep1);
        service.TotalPacketsMuted.Should().Be(1);

        // Moderator un-mutes account
        muteRegistry.RemoveMute(2001);

        // Attempt again
        service.RoutePacket(packet, ep1);
        service.TotalPacketsMuted.Should().Be(1); // Did not increment again
    }

    [Fact]
    public void RoutePacket_WhenMuteLapsedByTime_AllowsRelay()
    {
        VoiceMuteRegistry muteRegistry = new VoiceMuteRegistry(_timeProvider);
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 11298,
            timeProvider: _timeProvider,
            muteRegistry: muteRegistry);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);

        service.RegisterParticipant(VoiceChannelType.Party, channelId: 88, accountId: 3001, ep1);
        service.RegisterParticipant(VoiceChannelType.Party, channelId: 88, accountId: 3002, ep2);

        muteRegistry.SetMute(3001, _timeProvider.GetUtcNow().AddMinutes(10), "10m cooldown");

        byte[] payload2 = new byte[] { 0x11, 0x22 };
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 88,
            senderAccountId: 3001,
            sequence: 1,
            payload: payload2);

        service.RoutePacket(packet, ep1);
        service.TotalPacketsMuted.Should().Be(1);

        // Advance past expiration
        _timeProvider.Advance(TimeSpan.FromMinutes(15));

        service.RoutePacket(packet, ep1);
        service.TotalPacketsMuted.Should().Be(1); // Did not increment, packet permitted
    }

    [Fact]
    public async Task RoutePacket_WhenParticipantIgnoredByOneListener_DropsOnlyForIgnoringListener()
    {
        VoiceMuteRegistry muteRegistry = new VoiceMuteRegistry(_timeProvider);
        CompelVoiceRelayService service = new CompelVoiceRelayService(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: 0,
            timeProvider: _timeProvider,
            muteRegistry: muteRegistry);

        IPEndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 50001);
        IPEndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 50002);
        IPEndPoint ep3 = new IPEndPoint(IPAddress.Loopback, 50003);

        service.RegisterParticipant(VoiceChannelType.Team, channelId: 99, accountId: 4001, ep1);
        service.RegisterParticipant(VoiceChannelType.Team, channelId: 99, accountId: 4002, ep2);
        service.RegisterParticipant(VoiceChannelType.Team, channelId: 99, accountId: 4003, ep3);

        // Account 4002 ignores 4001
        muteRegistry.SetIgnore(listenerAccountId: 4002, speakerAccountId: 4001, ignored: true);

        byte[] payload = [0xAA, 0xBB];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Team,
            channelId: 99,
            senderAccountId: 4001,
            sequence: 1,
            payload: payload);

        using CancellationTokenSource cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        for (int i = 0; i < 50 && !service.IsRunning; i++)
        {
            await Task.Delay(10);
        }
        try
        {
            int relayed = service.RoutePacket(packet, ep1);
            // 4002 ignored 4001 -> dropped for 4002. Only relayed to 4003.
            relayed.Should().Be(1);

            // Now 4003 also ignores 4001
            muteRegistry.SetIgnore(listenerAccountId: 4003, speakerAccountId: 4001, ignored: true);
            int relayedAfterBothIgnore = service.RoutePacket(packet, ep1);
            relayedAfterBothIgnore.Should().Be(0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
