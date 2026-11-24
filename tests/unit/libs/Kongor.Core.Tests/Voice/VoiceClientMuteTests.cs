// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceClientMuteTests.cs
// System: ProjectKongor - Voice Subsystem Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Kongor.Core.Netcode.Voice;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class VoiceClientMuteTests
{
    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void SetPushToTalkState_WhenMuted_SuppressesPTTAndFiresEvent()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100, timeProvider: _timeProvider)
        {
            IsMuted = true,
            MuteExpiration = _timeProvider.GetUtcNow().AddHours(2),
            MuteReason = "Verbal toxicity"
        };

        List<string> blockedAlerts = [];
        client.OnMuteBlocked += msg => blockedAlerts.Add(msg);

        client.SetPushToTalkState(true, VoiceChannelType.Party, 1);

        client.IsPushToTalkActive.Should().BeFalse();
        blockedAlerts.Should().ContainSingle();
        blockedAlerts[0].Should().Contain("^279* You are currently muted and are unable to communicate.");
    }

    [Fact]
    public void SetPushToTalkState_WhenNotMuted_ActivatesNormally()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100, timeProvider: _timeProvider)
        {
            IsMuted = false
        };

        List<string> blockedAlerts = [];
        client.OnMuteBlocked += msg => blockedAlerts.Add(msg);

        client.SetPushToTalkState(true, VoiceChannelType.Team, 2);

        client.IsPushToTalkActive.Should().BeTrue();
        client.ActiveChannel.Should().Be(VoiceChannelType.Team);
        client.ActiveChannelId.Should().Be(2u);
        blockedAlerts.Should().BeEmpty();
    }

    [Fact]
    public void SetPushToTalkState_WhenMuteLapsed_AutoClearsAndActivates()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100, timeProvider: _timeProvider)
        {
            IsMuted = true,
            MuteExpiration = _timeProvider.GetUtcNow().AddMinutes(30),
            MuteReason = "Temporary block"
        };

        // Advance time past expiration
        _timeProvider.Advance(TimeSpan.FromHours(1));

        List<string> blockedAlerts = [];
        client.OnMuteBlocked += msg => blockedAlerts.Add(msg);

        client.SetPushToTalkState(true, VoiceChannelType.Party, 5);

        client.IsMuted.Should().BeFalse();
        client.MuteExpiration.Should().BeNull();
        client.IsPushToTalkActive.Should().BeTrue();
        blockedAlerts.Should().BeEmpty();
    }

    [Fact]
    public void TransmitAudioFrame_WhenMuted_ReturnsFalseWithoutSending()
    {
        VoiceClientService client = new VoiceClientService(
            localAccountId: 100,
            relayEndPoint: new IPEndPoint(IPAddress.Loopback, 11236),
            timeProvider: _timeProvider)
        {
            IsMuted = true,
            MuteExpiration = _timeProvider.GetUtcNow().AddDays(1)
        };

        short[] pcm = new short[960];
        bool sent = client.TransmitAudioFrame(pcm);

        sent.Should().BeFalse();
    }

    [Fact]
    public void TransmitAudioFrame_WhenMuteLapsed_RestoresNormalTransmission()
    {
        VoiceClientService client = new VoiceClientService(
            localAccountId: 100,
            relayEndPoint: new IPEndPoint(IPAddress.Loopback, 11236),
            timeProvider: _timeProvider)
        {
            IsMuted = true,
            MuteExpiration = _timeProvider.GetUtcNow().AddMinutes(15)
        };

        // Advance time past expiration
        _timeProvider.Advance(TimeSpan.FromMinutes(20));

        // Start local socket so transmission can succeed
        client.Start();

        short[] pcm = new short[960];
        bool sent = client.TransmitAudioFrame(pcm);

        client.IsMuted.Should().BeFalse();
        sent.Should().BeTrue();
    }
}
