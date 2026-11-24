// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeSocketTableTests.cs
// System: ProjectKongor Netcode Tests [SR-08]
// Subsystem: Kernel Socket Ownership Discovery Verification
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Netcode.Sockets;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class NativeSocketTableTests
{
    [Fact]
    public void TryGetPortOwnerPid_WhenUdpPortBoundByCurrentProcess_ReturnsCurrentProcessId()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int boundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;

        int? ownerPid = NativeSocketTable.TryGetPortOwnerPid(boundPort, ProtocolType.Udp);

        // On Windows and Linux with standard procfs, owner must match current process
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            ownerPid.Should().NotBeNull();
            ownerPid!.Value.Should().Be(Environment.ProcessId);
        }
    }

    [Fact]
    public void TryGetPortOwnerPid_WhenInvalidPort_ReturnsNull()
    {
        NativeSocketTable.TryGetPortOwnerPid(0).Should().BeNull();
        NativeSocketTable.TryGetPortOwnerPid(-1).Should().BeNull();
        NativeSocketTable.TryGetPortOwnerPid(70000).Should().BeNull();
    }

    [Fact]
    public void GetAllPortOwners_OnWindowsOrLinux_ReturnsNonEmptyDictionary()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int boundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;

        Dictionary<int, int> owners = NativeSocketTable.GetAllPortOwners(ProtocolType.Udp);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            owners.Should().NotBeEmpty();
            owners.Should().ContainKey(boundPort);
            owners[boundPort].Should().Be(Environment.ProcessId);
        }
    }
}
