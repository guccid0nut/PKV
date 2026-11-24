// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers.Binary;
using System.Net;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies the datagram validation pipeline: length enforcement, challenge matching, counter admission, and grace period handling.
/// </summary>
public sealed class DatagramValidatorTests
{
    private static readonly IPEndPoint ClientEndPoint = new(IPAddress.Parse("127.0.0.1"), 54321);

    [Fact]
    public void Datagram_Shorter_Than_Minimum_Length_Is_Dropped_As_Too_Short()
    {
        byte[] shortDatagram = new byte[10];
        SessionChallengeState challenges = new();

        DatagramValidationResult outcome = DatagramValidator.Validate(
            shortDatagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        outcome.IsAdmitted.Should().BeFalse();
        outcome.DropReason.Should().Be("Too Short");
        outcome.ViolationWeight.Should().Be(ViolationScoreContainer.TooShortViolationWeight);
    }

    [Fact]
    public void Datagram_With_Unknown_Challenge_Within_Grace_Is_Dropped_With_Zero_Violation_Weight()
    {
        byte[] datagram = BuildDatagram(challenge: 9999, counter: 0);
        SessionChallengeState challenges = new();

        DatagramValidationResult outcome = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: true);

        outcome.IsAdmitted.Should().BeFalse();
        outcome.DropReason.Should().Be("Unknown Challenge Within Grace");
        outcome.ViolationWeight.Should().Be(0);
    }

    [Fact]
    public void Datagram_With_Unknown_Challenge_Outside_Grace_Is_Dropped_With_Challenge_Violation_Weight()
    {
        byte[] datagram = BuildDatagram(challenge: 9999, counter: 0);
        SessionChallengeState challenges = new();

        DatagramValidationResult outcome = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        outcome.IsAdmitted.Should().BeFalse();
        outcome.DropReason.Should().Be("Unknown Challenge");
        outcome.ViolationWeight.Should().Be(ViolationScoreContainer.ChallengeViolationWeight);
    }

    [Fact]
    public void Datagram_With_Unauthenticated_Challenge_Over_Quota_Is_Dropped_As_Unauthenticated()
    {
        byte[] datagram = BuildDatagram(challenge: SessionChallengeState.UnauthenticatedChallenge, counter: ChallengeQuota.UnauthenticatedPacketQuota);
        SessionChallengeState challenges = new();

        DatagramValidationResult outcome = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        outcome.IsAdmitted.Should().BeFalse();
        outcome.DropReason.Should().Be("Unauthenticated");
        outcome.ViolationWeight.Should().Be(ViolationScoreContainer.UnauthenticatedViolationWeight);
    }

    [Fact]
    public void Datagram_With_Duplicate_Counter_Is_Dropped_As_Duplicate()
    {
        const uint challenge = 12345;
        SessionChallengeState challenges = new();
        challenges.Rotate(challenge, quota: 100);

        byte[] datagram = BuildDatagram(challenge, counter: 5);

        DatagramValidationResult firstPass = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        DatagramValidationResult secondPass = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        firstPass.IsAdmitted.Should().BeTrue();

        secondPass.IsAdmitted.Should().BeFalse();
        secondPass.DropReason.Should().Be("Duplicate");
        secondPass.ViolationWeight.Should().Be(ViolationScoreContainer.DuplicateViolationWeight);
    }

    [Fact]
    public void Datagram_With_Authenticated_Challenge_Over_Quota_Is_Dropped_As_Over_Quota()
    {
        const uint challenge = 12345;
        SessionChallengeState challenges = new();
        challenges.Rotate(challenge, quota: 10);

        byte[] datagram = BuildDatagram(challenge, counter: 10);

        DatagramValidationResult outcome = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        outcome.IsAdmitted.Should().BeFalse();
        outcome.DropReason.Should().Be("Over Quota");
        outcome.ViolationWeight.Should().Be(ViolationScoreContainer.RateLimitViolationWeight);
    }

    [Fact]
    public void Valid_Unauthenticated_Datagram_Is_Admitted_Without_Authenticating()
    {
        byte[] datagram = BuildDatagram(challenge: SessionChallengeState.UnauthenticatedChallenge, counter: 0);
        SessionChallengeState challenges = new();

        DatagramValidationResult outcome = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        outcome.IsAdmitted.Should().BeTrue();
        outcome.IsAuthenticatedChallenge.Should().BeFalse();
    }

    [Fact]
    public void Valid_Authenticated_Datagram_Is_Admitted_And_Marks_Authenticated()
    {
        const uint challenge = 54321;
        SessionChallengeState challenges = new();
        challenges.Rotate(challenge, quota: 100);

        byte[] datagram = BuildDatagram(challenge, counter: 0);

        DatagramValidationResult outcome = DatagramValidator.Validate(
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false);

        outcome.IsAdmitted.Should().BeTrue();
        outcome.IsAuthenticatedChallenge.Should().BeTrue();
    }

    private static byte[] BuildDatagram(uint challenge, ushort counter)
    {
        byte[] buffer = new byte[43];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ClientPacketReader.ChallengeOffset), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(ClientPacketReader.CounterOffset), counter);

        return buffer;
    }
}
