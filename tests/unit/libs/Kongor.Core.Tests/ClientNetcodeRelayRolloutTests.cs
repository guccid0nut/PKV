// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientNetcodeRelayRolloutTests.cs
// System: Kongor Core Netcode - Unit Tests for ClientNetcodeRelay Rollout & Sockets
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Netcode;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class ClientNetcodeRelayRolloutTests
{
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _currentValue;
        private readonly List<Action<T, string?>> _listeners = new();

        public TestOptionsMonitor(T initialValue)
        {
            _currentValue = initialValue;
        }

        public T CurrentValue => _currentValue;

        public T Get(string? name) => _currentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public void Update(T newValue)
        {
            _currentValue = newValue;
            Action<T, string?>[] snapshot = _listeners.ToArray();
            foreach (Action<T, string?> listener in snapshot)
            {
                listener(newValue, null);
            }
        }

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _action;
            public ActionDisposable(Action action) => _action = action;
            public void Dispose() => _action();
        }
    }

    private static Socket CreateBoundLoopbackUdpSocket()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    [Fact]
    public async Task OutboundSocket_RemoteBindAddress_BindsAnyForLanWanAndLoopbackForLocal()
    {
        // 1. Remote IPv4 WAN/LAN endpoint binds IPAddress.Any (0.0.0.0)
        IPEndPoint remoteWanIpv4 = new(IPAddress.Parse("198.51.100.42"), 11235);
        await using (ClientNetcodeRelay relayWanIpv4 = new(remoteWanIpv4))
        {
            Assert.Equal(AddressFamily.InterNetwork, relayWanIpv4.RemoteSocketLocalEndPoint.AddressFamily);
            Assert.Equal(IPAddress.Any, relayWanIpv4.RemoteSocketLocalEndPoint.Address);
        }

        // 2. Remote IPv6 WAN/LAN endpoint binds IPAddress.IPv6Any (::)
        IPEndPoint remoteWanIpv6 = new(IPAddress.Parse("2001:db8::1"), 11235);
        await using (ClientNetcodeRelay relayWanIpv6 = new(remoteWanIpv6))
        {
            Assert.Equal(AddressFamily.InterNetworkV6, relayWanIpv6.RemoteSocketLocalEndPoint.AddressFamily);
            Assert.Equal(IPAddress.IPv6Any, relayWanIpv6.RemoteSocketLocalEndPoint.Address);
        }

        // 3. Local IPv4 loopback endpoint binds IPAddress.Loopback (127.0.0.1) and relays bidirectional traffic
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int mockServerPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;
        IPEndPoint mockServerEndpoint = new(IPAddress.Loopback, mockServerPort);

        await using (ClientNetcodeRelay relayLoopback = new(mockServerEndpoint, optionsMonitor: null, handshakeTimeout: TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(IPAddress.Loopback, relayLoopback.RemoteSocketLocalEndPoint.Address);
            relayLoopback.Start();

            using Socket gameClient = CreateBoundLoopbackUdpSocket();
            IPEndPoint relayLocalEndpoint = new(IPAddress.Loopback, relayLoopback.LocalPort);

            byte[] clientPacket = Encoding.UTF8.GetBytes("CLIENT_HANDSHAKE_PACKET");
            await gameClient.SendToAsync(clientPacket, SocketFlags.None, relayLocalEndpoint);

            byte[] serverReceiveBuf = new byte[2048];
            EndPoint serverSender = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult serverRecv = await mockServer.ReceiveFromAsync(
                serverReceiveBuf, SocketFlags.None, serverSender);
            Assert.True(serverRecv.ReceivedBytes > 0);

            byte[] rawState = Encoding.UTF8.GetBytes("SERVER_FRAME_DATA_PAYLOAD");
            byte[] compressedState = ZstdDeltaCompressor.CompressDelta(rawState, level: 1);
            await mockServer.SendToAsync(compressedState, SocketFlags.None, serverRecv.RemoteEndPoint);

            byte[] clientReceiveBuf = new byte[2048];
            EndPoint clientSender = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult clientRecv = await gameClient.ReceiveFromAsync(
                clientReceiveBuf, SocketFlags.None, clientSender);

            byte[] decompressedAtClient = new byte[clientRecv.ReceivedBytes];
            Array.Copy(clientReceiveBuf, decompressedAtClient, clientRecv.ReceivedBytes);
            Assert.Equal(rawState, decompressedAtClient);
        }
    }

    [Fact]
    public async Task HandshakeTimeout_AutoBypassesToDirectUdp_WhenServerFailsToRespond()
    {
        NetcodeRelayOptions defaultOptions = new();
        Assert.Equal(500, defaultOptions.HandshakeTimeoutMs);
        Assert.False(defaultOptions.RelayEnabled);

        using Socket silentMockServer = CreateBoundLoopbackUdpSocket();
        int mockPort = ((IPEndPoint)silentMockServer.LocalEndPoint!).Port;
        IPEndPoint silentServerEndpoint = new(IPAddress.Loopback, mockPort);

        TimeSpan fastTestTimeout = TimeSpan.FromMilliseconds(80);
        await using ClientNetcodeRelay relay = new(
            silentServerEndpoint,
            localListenPort: 0,
            enableFec: false,
            optionsMonitor: null,
            handshakeTimeout: fastTestTimeout,
            logger: null);

        IPEndPoint? bypassedTarget = null;
        string? bypassedReason = null;
        relay.OnAutoBypassToDirectUdp += (IPEndPoint target, string reason) =>
        {
            bypassedTarget = target;
            bypassedReason = reason;
        };

        relay.Start();
        Assert.False(relay.IsBypassed);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, relay.LocalPort), relay.EffectiveClientConnectEndPoint);

        using Socket gameClient = CreateBoundLoopbackUdpSocket();
        IPEndPoint relayLocalEndpoint = new(IPAddress.Loopback, relay.LocalPort);
        byte[] clientPacket = Encoding.UTF8.GetBytes("CLIENT_HELLO_SILENT");
        await gameClient.SendToAsync(clientPacket, SocketFlags.None, relayLocalEndpoint);

        // Wait for 80ms timeout to trip (with 500ms safety window)
        for (int i = 0; i < 50 && !relay.IsBypassed; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(relay.IsBypassed);
        Assert.Equal("ServerHandshakeTimeout", relay.BypassReason);
        Assert.Equal(silentServerEndpoint, relay.EffectiveClientConnectEndPoint);
        Assert.Equal(silentServerEndpoint, bypassedTarget);
        Assert.Equal("ServerHandshakeTimeout", bypassedReason);
        Assert.Equal(1, relay.Stats.HandshakeTimeouts);
        Assert.True(relay.Stats.IsBypassed);
    }

    [Fact]
    public async Task OptionsMonitor_LiveToggle_StartsBypassedWhenDisabledAndHotReloadTripsBypass()
    {
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int mockPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;
        IPEndPoint mockServerEndpoint = new(IPAddress.Loopback, mockPort);

        // 1. RelayEnabled = false starts bypassed immediately
        NetcodeRelayOptions disabledOptions = new() { RelayEnabled = false };
        TestOptionsMonitor<NetcodeRelayOptions> disabledMonitor = new(disabledOptions);
        await using (ClientNetcodeRelay relayDisabled = new(mockServerEndpoint, disabledMonitor))
        {
            Assert.True(relayDisabled.IsBypassed);
            Assert.Equal("DisabledViaOptionsMonitor", relayDisabled.BypassReason);
            Assert.Equal(mockServerEndpoint, relayDisabled.EffectiveClientConnectEndPoint);
        }

        // 2. RelayEnabled = true starts active (not bypassed)
        NetcodeRelayOptions enabledOptions = new() { RelayEnabled = true, HandshakeTimeoutMs = 2000 };
        TestOptionsMonitor<NetcodeRelayOptions> activeMonitor = new(enabledOptions);
        await using (ClientNetcodeRelay relayActive = new(mockServerEndpoint, activeMonitor))
        {
            relayActive.Start();
            Assert.False(relayActive.IsBypassed);
            Assert.Null(relayActive.BypassReason);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, relayActive.LocalPort), relayActive.EffectiveClientConnectEndPoint);

            IPEndPoint? eventTarget = null;
            string? eventReason = null;
            relayActive.OnAutoBypassToDirectUdp += (IPEndPoint target, string reason) =>
            {
                eventTarget = target;
                eventReason = reason;
            };

            // 3. Live toggle true -> false trips bypass immediately via IOptionsMonitor.OnChange
            activeMonitor.Update(new NetcodeRelayOptions { RelayEnabled = false });

            Assert.True(relayActive.IsBypassed);
            Assert.Equal("DisabledViaOptionsMonitor", relayActive.BypassReason);
            Assert.Equal(mockServerEndpoint, relayActive.EffectiveClientConnectEndPoint);
            Assert.Equal(mockServerEndpoint, eventTarget);
            Assert.Equal("DisabledViaOptionsMonitor", eventReason);
        }
    }

    [Fact]
    public async Task LauncherEndpointResolution_EffectiveClientConnectEndPoint_ResolvesLoopbackWhenEnabled_AndDirectWhenBypassedOrDisabled()
    {
        // protects: Launcher game server endpoint resolution via EffectiveClientConnectEndPoint
        // fails_when: Loopback endpoint is not exposed when relay is enabled or direct endpoint is not returned when bypassed
        // seam: ClientNetcodeRelay.EffectiveClientConnectEndPoint resolution
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int mockPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;
        IPEndPoint remoteServer = new(IPAddress.Loopback, mockPort);

        // 1. When RelayEnabled is false, EffectiveClientConnectEndPoint resolves directly to remote server
        NetcodeRelayOptions disabledOptions = new() { RelayEnabled = false };
        TestOptionsMonitor<NetcodeRelayOptions> disabledMonitor = new(disabledOptions);
        await using (ClientNetcodeRelay relayDisabled = new(remoteServer, disabledMonitor))
        {
            Assert.Equal(remoteServer, relayDisabled.EffectiveClientConnectEndPoint);
        }

        // 2. When RelayEnabled is true, EffectiveClientConnectEndPoint resolves to local loopback relay endpoint
        NetcodeRelayOptions enabledOptions = new() { RelayEnabled = true, HandshakeTimeoutMs = 2000 };
        TestOptionsMonitor<NetcodeRelayOptions> enabledMonitor = new(enabledOptions);
        await using (ClientNetcodeRelay relayActive = new(remoteServer, enabledMonitor))
        {
            relayActive.Start();
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, relayActive.LocalPort), relayActive.EffectiveClientConnectEndPoint);

            // 3. Dynamic kill switch trips bypass back to remote server endpoint
            enabledMonitor.Update(new NetcodeRelayOptions { RelayEnabled = false });
            Assert.Equal(remoteServer, relayActive.EffectiveClientConnectEndPoint);
        }
    }
}
