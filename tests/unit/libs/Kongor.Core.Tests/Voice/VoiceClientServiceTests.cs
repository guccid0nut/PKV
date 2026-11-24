// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceClientServiceTests.cs
// System: ProjectKongor - Voice Subsystem Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Kongor.Core.Netcode.Voice;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class VoiceClientServiceTests
{
    [Fact]
    public void ProcessIncomingPacket_TriggersSpeakingEventAndDecodesAudio()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100);

        List<ReceivedVoiceFrame> receivedFrames = [];
        List<(uint accountId, bool isSpeaking)> speakingEvents = [];

        client.OnVoiceFrameReceived += frame => receivedFrames.Add(frame);
        client.OnPeerSpeakingStateChanged += (accId, speaking) => speakingEvents.Add((accId, speaking));

        // Create an encoded voice packet from peer (account 200)
        using OpusVoiceCodec peerEncoder = new OpusVoiceCodec();
        short[] pcm = new short[peerEncoder.FrameSizeSamples];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(i * 0.2) * 8000.0);
        }

        byte[] opus = new byte[1276];
        int encodedLen = peerEncoder.Encode(pcm, opus);

        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 42,
            senderAccountId: 200,
            sequence: 1,
            payload: opus.AsMemory(0, encodedLen));

        client.ProcessIncomingPacket(packet);

        speakingEvents.Should().ContainSingle(e => e.accountId == 200 && e.isSpeaking);
        receivedFrames.Should().ContainSingle();
        receivedFrames[0].SenderAccountId.Should().Be(200u);
        receivedFrames[0].PcmSamples.Length.Should().Be(peerEncoder.FrameSizeSamples);
    }

    [Fact]
    public void ProcessIncomingPacket_EndOfTransmission_ClearsSpeakingState()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100);

        List<(uint accountId, bool isSpeaking)> speakingEvents = [];
        client.OnPeerSpeakingStateChanged += (accId, speaking) => speakingEvents.Add((accId, speaking));

        // Start speaking
        using OpusVoiceCodec peerEncoder = new OpusVoiceCodec();
        short[] pcm = new short[peerEncoder.FrameSizeSamples];
        byte[] opus = new byte[1276];
        int encodedLen = peerEncoder.Encode(pcm, opus);

        VoiceDatagramPacket packet1 = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 42,
            senderAccountId: 300,
            sequence: 1,
            payload: opus.AsMemory(0, encodedLen));

        client.ProcessIncomingPacket(packet1);
        speakingEvents.Should().ContainSingle(e => e.accountId == 300 && e.isSpeaking);

        // End of transmission
        VoiceDatagramPacket eotPacket = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 42,
            senderAccountId: 300,
            sequence: 2,
            payload: ReadOnlyMemory<byte>.Empty,
            flags: VoicePacketFlags.EndOfTransmission);

        client.ProcessIncomingPacket(eotPacket);

        speakingEvents.Should().HaveCount(2);
        speakingEvents[1].accountId.Should().Be(300u);
        speakingEvents[1].isSpeaking.Should().BeFalse();
    }

    [Fact]
    public void ProcessIncomingPacket_WithDroppedSequence_InterpolatesPlcFrames()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100);

        List<ReceivedVoiceFrame> receivedFrames = [];
        client.OnVoiceFrameReceived += frame => receivedFrames.Add(frame);

        using OpusVoiceCodec peerEncoder = new OpusVoiceCodec();
        short[] pcm = new short[peerEncoder.FrameSizeSamples];
        byte[] opus = new byte[1276];
        int encodedLen = peerEncoder.Encode(pcm, opus);

        // Packet sequence 1
        VoiceDatagramPacket packet1 = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 10,
            senderAccountId: 500,
            sequence: 1,
            payload: opus.AsMemory(0, encodedLen));

        client.ProcessIncomingPacket(packet1);
        receivedFrames.Should().ContainSingle();

        // Packet sequence 3 (sequence 2 dropped!)
        VoiceDatagramPacket packet3 = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 10,
            senderAccountId: 500,
            sequence: 3,
            payload: opus.AsMemory(0, encodedLen));

        client.ProcessIncomingPacket(packet3);

        // Expected: frame 1 (normal), frame 2 (PLC interpolated), frame 3 (normal)
        receivedFrames.Should().HaveCount(3);
        receivedFrames[1].Sequence.Should().Be((ushort)2);
        receivedFrames[2].Sequence.Should().Be((ushort)3);
    }

    [Fact]
    public void SetPeerIgnored_WhenPeerIgnored_SuppressesVoiceFramesAndSpeakingState()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100);

        List<ReceivedVoiceFrame> receivedFrames = [];
        List<(uint accountId, bool isSpeaking)> speakingEvents = [];

        client.OnVoiceFrameReceived += frame => receivedFrames.Add(frame);
        client.OnPeerSpeakingStateChanged += (accId, speaking) => speakingEvents.Add((accId, speaking));

        // Mark peer 200 as ignored
        client.SetPeerIgnored(200, true);
        client.IsPeerIgnored(200).Should().BeTrue();

        using OpusVoiceCodec peerEncoder = new OpusVoiceCodec();
        short[] pcm = new short[peerEncoder.FrameSizeSamples];
        byte[] opus = new byte[1276];
        int encodedLen = peerEncoder.Encode(pcm, opus);

        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 42,
            senderAccountId: 200,
            sequence: 1,
            payload: opus.AsMemory(0, encodedLen));

        client.ProcessIncomingPacket(packet);

        // Zero decoding, zero indicator glow
        receivedFrames.Should().BeEmpty();
        speakingEvents.Should().BeEmpty();
    }

    [Fact]
    public void SetPeerIgnored_WhenPeerUnignored_ResumesVoiceFrames()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100);

        List<ReceivedVoiceFrame> receivedFrames = [];
        List<(uint accountId, bool isSpeaking)> speakingEvents = [];

        client.OnVoiceFrameReceived += frame => receivedFrames.Add(frame);
        client.OnPeerSpeakingStateChanged += (accId, speaking) => speakingEvents.Add((accId, speaking));

        client.SetPeerIgnored(200, true);
        client.SetPeerIgnored(200, false);
        client.IsPeerIgnored(200).Should().BeFalse();

        using OpusVoiceCodec peerEncoder = new OpusVoiceCodec();
        short[] pcm = new short[peerEncoder.FrameSizeSamples];
        byte[] opus = new byte[1276];
        int encodedLen = peerEncoder.Encode(pcm, opus);

        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 42,
            senderAccountId: 200,
            sequence: 1,
            payload: opus.AsMemory(0, encodedLen));

        client.ProcessIncomingPacket(packet);

        receivedFrames.Should().ContainSingle();
        speakingEvents.Should().ContainSingle(e => e.accountId == 200 && e.isSpeaking);
    }

    [Fact]
    public void SetIgnoredPeers_BatchConfiguresIgnoredPeers()
    {
        VoiceClientService client = new VoiceClientService(localAccountId: 100);

        client.SetIgnoredPeers([201, 202, 203]);

        client.IsPeerIgnored(201).Should().BeTrue();
        client.IsPeerIgnored(202).Should().BeTrue();
        client.IsPeerIgnored(203).Should().BeTrue();
        client.IsPeerIgnored(204).Should().BeFalse();
    }

    [Fact]
    public void ChannelIdHelpers_GenerateDistinctIds_PreventingCrossTalk()
    {
        uint matchId = 12345;

        uint legionTeamId = VoiceClientService.GetTeamChannelId(matchId, teamIndex: 1);
        uint hellbourneTeamId = VoiceClientService.GetTeamChannelId(matchId, teamIndex: 2);

        legionTeamId.Should().NotBe(hellbourneTeamId);
        legionTeamId.Should().Be((matchId << 1) | 0);
        hellbourneTeamId.Should().Be((matchId << 1) | 1);

        uint legionTopLane = VoiceClientService.GetLaneChannelId(matchId, teamIndex: 1, laneIndex: 0);
        uint legionMidLane = VoiceClientService.GetLaneChannelId(matchId, teamIndex: 1, laneIndex: 1);
        uint hellbourneTopLane = VoiceClientService.GetLaneChannelId(matchId, teamIndex: 2, laneIndex: 0);

        legionTopLane.Should().NotBe(legionMidLane);
        legionTopLane.Should().NotBe(hellbourneTopLane);

        uint mentorId = VoiceClientService.GetMentorChannelId(pupilAccountId: 9999);
        mentorId.Should().Be(9999);
    }
}

