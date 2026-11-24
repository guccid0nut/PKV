// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     Verifies the byte layout of the master-server pong template: the unreliable flag and pong message type at their
///     fixed offsets, and the server name and version at the offsets the client expects.
/// </summary>
public sealed class UDPPingResponderTests
{
    [Fact]
    public void The_Response_Template_Places_The_Markers_Name_And_Version_At_The_Expected_Offsets()
    {
        const string serverName = "KONGOR ARENA";
        const string version = "4.10.1";

        byte[] response = UDPPingResponder.BuildResponseTemplate(serverName, version);

        byte[] serverNameBytes = Encoding.UTF8.GetBytes(serverName);
        byte[] versionBytes = Encoding.UTF8.GetBytes(version);

        Assert.Equal((byte)0x01, response[42]);
        Assert.Equal((byte)0x66, response[43]);
        Assert.Equal(69 + serverNameBytes.Length + versionBytes.Length, response.Length);
        Assert.True(response.Skip(46).Take(serverNameBytes.Length).SequenceEqual(serverNameBytes));
        Assert.True(response.Skip(50 + serverNameBytes.Length).Take(versionBytes.Length).SequenceEqual(versionBytes));
    }

    [Fact]
    public void A_Missing_Version_Produces_A_Template_With_No_Version_Bytes()
    {
        byte[] response = UDPPingResponder.BuildResponseTemplate("Server", null);

        byte[] serverNameBytes = Encoding.UTF8.GetBytes("Server");

        Assert.Equal(69 + serverNameBytes.Length, response.Length);
    }
}
