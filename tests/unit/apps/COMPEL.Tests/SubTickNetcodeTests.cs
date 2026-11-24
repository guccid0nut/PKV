// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SubTickNetcodeTests.cs
// System: COMPEL Tests - Sub-Tick Netcode & Mathematical Interpolation Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class SubTickNetcodeTests
{
    [Fact]
    public void SubTickCommandPacket_BinaryRoundtrip_PreservesAllFields()
    {
        // Arrange
        uint clientTick = 1420;
        ushort flags = 0x01;

        SubTickInputEvent evt1 = SubTickInputEvent.CreateMove(
            sequence: 101,
            baseTick: 1420,
            subTickOffset: 0.25f,
            timestampUs: 1_700_000_123_456L,
            destination: new Vector3(1500.5f, 2400.25f, 100.0f),
            cameraYaw: 1.57f);

        SubTickInputEvent evt2 = SubTickInputEvent.CreateTargeted(
            sequence: 102,
            baseTick: 1420,
            subTickOffset: 0.75f,
            timestampUs: 1_700_000_123_999L,
            commandType: SubTickCommandType.CastAbility,
            targetEntityIndex: 42,
            targetPosition: new Vector3(1600.0f, 2500.0f, 110.0f),
            cameraYaw: 3.14f);

        SubTickInputEvent[] originalEvents = [evt1, evt2];
        byte[] buffer = new byte[256];

        // Act - Serialize
        bool writeSuccess = SubTickCommandPacket.TryWrite(
            buffer,
            clientTick,
            originalEvents,
            flags,
            out int bytesWritten);

        // Assert - Write
        writeSuccess.Should().BeTrue();
        bytesWritten.Should().Be(SubTickCommandPacket.HeaderSize + (2 * SubTickInputEvent.BinarySize));
        SubTickCommandPacket.IsSubTickPacket(buffer.AsSpan(0, bytesWritten)).Should().BeTrue();

        // Act - Deserialize
        SubTickInputEvent[] readEvents = new SubTickInputEvent[4];
        bool parseSuccess = SubTickCommandPacket.TryParse(
            buffer.AsSpan(0, bytesWritten),
            out SubTickHeader header,
            readEvents,
            out int eventsRead);

        // Assert - Read
        parseSuccess.Should().BeTrue();
        header.ClientTick.Should().Be(clientTick);
        header.EventCount.Should().Be(2);
        eventsRead.Should().Be(2);

        SubTickInputEvent read1 = readEvents[0];
        read1.Sequence.Should().Be(101);
        read1.BaseTick.Should().Be(1420);
        read1.SubTickOffset.Should().BeApproximately(0.25f, 0.0001f);
        read1.HardwareTimestampUs.Should().Be(1_700_000_123_456L);
        read1.CommandType.Should().Be(SubTickCommandType.Move);
        read1.TargetX.Should().BeApproximately(1500.5f, 0.0001f);
        read1.TargetY.Should().BeApproximately(2400.25f, 0.0001f);
        read1.TargetZ.Should().BeApproximately(100.0f, 0.0001f);

        SubTickInputEvent read2 = readEvents[1];
        read2.Sequence.Should().Be(102);
        read2.TargetEntityIndex.Should().Be(42);
        read2.CommandType.Should().Be(SubTickCommandType.CastAbility);
        read2.SubTickOffset.Should().BeApproximately(0.75f, 0.0001f);
    }

    [Fact]
    public void SubTickCommandPacket_MalformedMagic_ReturnsFalseWithoutThrowing()
    {
        byte[] malformed = new byte[64];
        malformed[0] = 0x00; // Invalid magic

        SubTickInputEvent[] events = new SubTickInputEvent[4];
        bool success = SubTickCommandPacket.TryParse(malformed, out _, events, out int readCount);

        success.Should().BeFalse();
        readCount.Should().Be(0);
        SubTickCommandPacket.IsSubTickPacket(malformed).Should().BeFalse();
    }

    [Fact]
    public void SubTickSimulationInterpolator_InterpolatesPositionLinearly()
    {
        Vector3 prev = new(0f, 0f, 0f);
        Vector3 curr = new(100f, 200f, 300f);

        Vector3 midpoint = SubTickSimulationInterpolator.InterpolatePosition(prev, curr, 0.5f);
        midpoint.X.Should().Be(50f);
        midpoint.Y.Should().Be(100f);
        midpoint.Z.Should().Be(150f);

        Vector3 quarter = SubTickSimulationInterpolator.InterpolatePosition(prev, curr, 0.25f);
        quarter.X.Should().Be(25f);
        quarter.Y.Should().Be(50f);
        quarter.Z.Should().Be(75f);
    }

    [Fact]
    public void SubTickSimulationInterpolator_AngleInterpolation_TakesShortestArc()
    {
        // Angle near +Pi (e.g. +3.0 rad) moving to near -Pi (e.g. -3.0 rad)
        // Shortest arc crosses Pi (distance = ~0.28 rad), NOT the long way around (~6.0 rad)
        float prevAngle = 3.0f;
        float currAngle = -3.0f;

        float midpointAngle = SubTickSimulationInterpolator.InterpolateAngle(prevAngle, currAngle, 0.5f);

        // Midpoint should be approximately Pi (~3.14159f) or -Pi
        double absDist = Math.Abs(Math.Abs(midpointAngle) - Math.PI);
        absDist.Should().BeLessThan(0.05);
    }

    [Fact]
    public void SubTickSimulationInterpolator_SkillshotOrigin_AccountsForHeroRadius()
    {
        Vector3 casterPos = new(1000f, 1000f, 0f);
        Vector3 aimDir = new(1f, 0f, 0f); // aiming along +X
        float heroRadius = 40.0f;

        Vector3 origin = SubTickSimulationInterpolator.CalculateSkillshotOrigin(casterPos, aimDir, heroRadius);

        origin.X.Should().Be(1040f);
        origin.Y.Should().Be(1000f);
        origin.Z.Should().Be(0f);
    }

    [Fact]
    public void SubTickSimulationInterpolator_QuantizationErrorSaved_ComputesAccurateLatencySavings()
    {
        // 50ms tick interval, input clicked 20% into tick (offset = 0.2)
        // Saved latency vs fixed-tick boundary = 50 * (1 - 0.2) = 40ms!
        double savedMs = SubTickSimulationInterpolator.CalculateQuantizationErrorSavedMs(50.0, 0.2f);
        savedMs.Should().BeApproximately(40.0, 0.001);

        // Input clicked at the very end of the tick (offset = 0.95)
        double endSavedMs = SubTickSimulationInterpolator.CalculateQuantizationErrorSavedMs(50.0, 0.95f);
        endSavedMs.Should().BeApproximately(2.5, 0.001);
    }
}
