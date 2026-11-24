// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SocketReclamationTests.cs
// System: COMPEL Server Supervisor Tests [SR-08]
// Subsystem: Active Socket Reclamation Verification
// ============================================================================

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

internal static class TestNonEphemeralUdpPortAllocator
{
    private const int MinPort = 24100;
    private const int PortSpan = 7800; // [24100..31899] stays strictly below OS ephemeral dynamic range (32768..65535)
    private static int s_offset = Math.Abs(Environment.ProcessId * 37) % PortSpan;

    /// <summary>
    ///     Selects an available UDP port outside the OS ephemeral port pool so concurrent
    ///     <c>Bind(..., 0)</c> sockets across parallel test suites cannot steal the port mid-assertion.
    /// </summary>
    public static int NextFreePort()
    {
        for (int attempt = 0; attempt < PortSpan; attempt++)
        {
            int idx = (Interlocked.Increment(ref s_offset) & 0x7FFFFFFF) % PortSpan;
            int candidate = MinPort + idx;
            if (SocketReclamationService.IsPortAvailable(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to locate a free non-ephemeral UDP port in [24100..31899] for test isolation.");
    }
}

public sealed class SocketReclamationTests
{
    [Fact]
    public async Task ReclaimPortAsync_WhenPortIsFree_ReturnsImmediateSuccess()
    {
        int freePort = TestNonEphemeralUdpPortAllocator.NextFreePort();

        PortReclamationResult result = await SocketReclamationService.ReclaimPortAsync(freePort, maxWaitMs: 100);

        result.Success.Should().BeTrue();
        result.Port.Should().Be(freePort);
        result.OffendingPid.Should().BeNull();
        result.ProcessTerminated.Should().BeFalse();
        result.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public async Task ReclaimPortAsync_WhenPortOccupiedByCurrentProcess_IdentifiesOwnerPidWithoutKillingSelf()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = true;
        int port = TestNonEphemeralUdpPortAllocator.NextFreePort();
        socket.Bind(new IPEndPoint(IPAddress.Any, port));

        PortReclamationResult result = await SocketReclamationService.ReclaimPortAsync(port, maxWaitMs: 50);

        result.Success.Should().BeFalse();
        result.Port.Should().Be(port);
        result.OffendingPid.Should().Be(Environment.ProcessId);
        result.ProcessTerminated.Should().BeFalse();
    }

    [Fact]
    public async Task EvictZombiePortsAsync_WhenBothPortsFree_ReturnsTrue()
    {
        int p1 = TestNonEphemeralUdpPortAllocator.NextFreePort();
        int p2 = TestNonEphemeralUdpPortAllocator.NextFreePort();

        bool available = await SocketReclamationService.EvictZombiePortsAsync(p1, p2, maxWaitMs: 100);
        available.Should().BeTrue();
    }

    [Fact]
    public async Task ZombieSocketDetector_EnsurePortAvailableAsync_ReturnsTrueForFreePort()
    {
        int freePort = TestNonEphemeralUdpPortAllocator.NextFreePort();

        bool available = await ZombieSocketDetector.EnsurePortAvailableAsync(freePort, maxWaitMs: 100);
        available.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\ProjectKongor\games\client\hon_x64.exe", true)]
    [InlineData(@"/opt/kongor/games/client/hon-x86_64", true)]
    [InlineData(@"C:\ProjectKongor\games\server\hon_x64.exe", false)]
    [InlineData(@"C:\ProjectKongor\games\server_p2p\hon_x64.exe", false)]
    [InlineData(@"C:\ProjectKongor\games\hon server\hon_x64.exe", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsProtectedGameClientExecutablePath_ProtectsPlayerClientAndAllowsDedicatedWorkers(
        string? executablePath,
        bool expectedProtected)
    {
        bool actual = SocketReclamationService.IsProtectedGameClientExecutablePath(executablePath);
        actual.Should().Be(expectedProtected);
    }
}
