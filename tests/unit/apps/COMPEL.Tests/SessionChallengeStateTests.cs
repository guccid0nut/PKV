// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers.Binary;
using System.Security.Cryptography;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies that a client is still admitted immediately after its challenge is renewed,
///     using the challenge it echoed rather than only the newest one.
/// </summary>
public sealed class SessionChallengeStateTests
{
    // Without This, Every Renewal Would Drop The Datagrams Already In Flight: A False Positive For Every Player On Every Rotation
    [Fact]
    public void A_Datagram_Echoing_The_Previous_Challenge_Is_Still_Matched()
    {
        SessionChallengeState state = new();

        state.Rotate(challenge: 100, quota: 64);
        state.Rotate(challenge: 200, quota: 64);

        state.Match(200).Should().NotBeNull();
        state.Match(100).Should().NotBeNull();
    }

    // The Previous Window Keeps Its Own Consumed Counters, So A Client Mid-Rotation Is Judged Against The Window It Was Actually Using
    [Fact]
    public void The_Previous_Window_Retains_Its_Own_Consumed_Counters()
    {
        SessionChallengeState state = new();

        state.Rotate(challenge: 100, quota: 64);

        ChallengeWindow? first = state.Match(100);
        first?.TryAdmit(7, out _);

        state.Rotate(challenge: 200, quota: 64);

        state.Match(100)?.TryAdmit(7, out _).Should().BeFalse();
        state.Match(200)?.TryAdmit(7, out _).Should().BeTrue();
    }

    // Six Retained Challenges Is Sixty Seconds Of History At The Renewal Interval, So Losing Several Consecutive Renewals Cannot Strand A Client
    [Fact]
    public void Every_Retained_Challenge_Is_Still_Matched()
    {
        SessionChallengeState state = new();

        for (uint challenge = 1; challenge <= SessionChallengeState.RetainedChallengeCount; challenge++)
            state.Rotate(challenge, quota: 64);

        for (uint challenge = 1; challenge <= SessionChallengeState.RetainedChallengeCount; challenge++)
            state.Match(challenge).Should().NotBeNull();
    }

    [Fact]
    public void A_Challenge_Older_Than_The_Retained_History_Is_Not_Matched()
    {
        SessionChallengeState state = new();

        for (uint challenge = 1; challenge <= SessionChallengeState.RetainedChallengeCount + 1; challenge++)
            state.Rotate(challenge, quota: 64);

        state.Match(1).Should().BeNull();
        state.Match(2).Should().NotBeNull();
    }

    // The Reference Runs Its Duplicate Check Over Challenge Zero Exactly As Over An Issued Challenge, So It Must Be A Window Rather Than A Bare Ceiling
    [Fact]
    public void The_Unauthenticated_Challenge_Is_Always_Matched_And_Detects_A_Duplicate()
    {
        SessionChallengeState state = new();

        ChallengeWindow? before = state.Match(SessionChallengeState.UnauthenticatedChallenge);

        state.Rotate(challenge: 100, quota: 64);

        ChallengeWindow? after = state.Match(SessionChallengeState.UnauthenticatedChallenge);

        before.Should().NotBeNull();
        after.Should().NotBeNull();

        after?.TryAdmit(7, out _).Should().BeTrue();
        after?.TryAdmit(7, out _).Should().BeFalse();
    }

    [Fact]
    public void The_Unauthenticated_Window_Is_Reset_Periodically()
    {
        SessionChallengeState state = new();

        state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _);

        for (uint rotation = 1; rotation <= SessionChallengeState.UnauthenticatedResetRotations; rotation++)
            state.Rotate(rotation, quota: 64);

        state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _).Should().BeTrue();
    }

    [Fact]
    public void The_Unauthenticated_Window_Survives_A_Rotation_That_Does_Not_Reset_It()
    {
        SessionChallengeState state = new();

        state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _);

        state.Rotate(challenge: 100, quota: 64);

        state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _).Should().BeFalse();
    }

    [Fact]
    public void An_Unknown_Challenge_Is_Not_Matched()
    {
        SessionChallengeState state = new();

        state.Rotate(challenge: 100, quota: 64);

        state.Match(999).Should().BeNull();
    }

    [Fact]
    public void No_Issued_Challenge_Is_Matched_Before_The_First_Rotation()
    {
        SessionChallengeState state = new();

        state.Match(100).Should().BeNull();
    }

    [Fact]
    public void A_Challenge_Value_Currently_Retained_Is_Never_Reissued()
    {
        SessionChallengeState state = new();
        HashSet<uint> issued = [];

        for (int index = 0; index < 50; index++)
        {
            uint challenge;
            do
            {
                challenge = RandomUInt32();
            }
            while (state.ContainsChallenge(challenge));

            state.ContainsChallenge(challenge).Should().BeFalse();

            state.Rotate(challenge, quota: 64);
            issued.Add(challenge);
        }

        issued.Count.Should().Be(50);
    }

    [Fact]
    public void The_Unauthenticated_Challenge_Zero_Is_Always_Considered_Contained()
    {
        SessionChallengeState state = new();

        state.ContainsChallenge(SessionChallengeState.UnauthenticatedChallenge).Should().BeTrue();
    }

    private static uint RandomUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
