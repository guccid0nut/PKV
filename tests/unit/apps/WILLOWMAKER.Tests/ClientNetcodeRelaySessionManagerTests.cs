// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientNetcodeRelaySessionManagerTests.cs
// System: WILLOWMAKER.Tests - ClientNetcodeRelaySessionManager Integration & Rollout Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using ASPIRE.Common.Netcode;
using Microsoft.Extensions.Options;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class ClientNetcodeRelaySessionManagerTests
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
    public async Task SessionManager_DefaultsToDisabled_ReturnsDirectRemoteEndPoint()
    {
        // protects: Game client launcher connects directly to game server when netcode relay is disabled
        // fails_when: Loopback relay is spawned needlessly when RelayEnabled is false
        // seam: ClientNetcodeRelaySessionManager.ResolveEffectiveConnectEndPoint
        IPEndPoint remoteServer = new(IPAddress.Parse("198.51.100.50"), 11235);
        await using ClientNetcodeRelaySessionManager manager = new();

        Assert.False(manager.EnableClientZstdRelay);
        Assert.Null(manager.ActiveRelay);

        IPEndPoint resolved = manager.ResolveEffectiveConnectEndPoint(remoteServer);

        Assert.Equal(remoteServer, resolved);
        Assert.Equal(remoteServer, manager.EffectiveClientConnectEndPoint);
        Assert.Null(manager.ActiveRelay);
    }

    [Fact]
    public async Task SessionManager_WhenEnabled_StartsRelayAndReturnsLoopbackEffectiveEndPoint()
    {
        // protects: ClientNetcodeRelay loopback endpoint exposed to game launcher when transparent zstd relay is enabled
        // fails_when: EffectiveClientConnectEndPoint does not return active loopback relay port
        // seam: ClientNetcodeRelaySessionManager + ClientNetcodeRelay.EffectiveClientConnectEndPoint
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int serverPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;
        IPEndPoint remoteServer = new(IPAddress.Loopback, serverPort);

        NetcodeRelayOptions options = new() { RelayEnabled = true, HandshakeTimeoutMs = 1500 };
        TestOptionsMonitor<NetcodeRelayOptions> monitor = new(options);

        await using ClientNetcodeRelaySessionManager manager = new(monitor);
        Assert.True(manager.EnableClientZstdRelay);

        IPEndPoint effective = manager.ResolveEffectiveConnectEndPoint(remoteServer);

        Assert.NotNull(manager.ActiveRelay);
        Assert.Equal(IPAddress.Loopback, effective.Address);
        Assert.Equal(manager.ActiveRelay.LocalPort, effective.Port);
        Assert.Equal(effective, manager.EffectiveClientConnectEndPoint);
    }

    [Fact]
    public async Task SessionManager_AutoBypassesToDirectRemoteEndPoint_OnServerHandshakeTimeout()
    {
        // protects: Transparent 500ms fallback to direct UDP when match server does not decompress or respond
        // fails_when: Game client is stranded on silent loopback socket after handshake timeout
        // seam: ClientNetcodeRelay auto-bypass event + EffectiveClientConnectEndPoint fallback
        using Socket silentServer = CreateBoundLoopbackUdpSocket();
        int serverPort = ((IPEndPoint)silentServer.LocalEndPoint!).Port;
        IPEndPoint remoteServer = new(IPAddress.Loopback, serverPort);

        NetcodeRelayOptions options = new() { RelayEnabled = true, HandshakeTimeoutMs = 80 };
        TestOptionsMonitor<NetcodeRelayOptions> monitor = new(options);

        await using ClientNetcodeRelaySessionManager manager = new(monitor);
        IPEndPoint loopbackEndpoint = manager.ResolveEffectiveConnectEndPoint(remoteServer);

        Assert.NotNull(manager.ActiveRelay);
        Assert.False(manager.ActiveRelay.IsBypassed);

        // Send a client packet to trigger the handshake timeout monitor
        using Socket gameClient = CreateBoundLoopbackUdpSocket();
        byte[] probePacket = Encoding.UTF8.GetBytes("CLIENT_PROBE");
        await gameClient.SendToAsync(probePacket, SocketFlags.None, loopbackEndpoint);

        for (int i = 0; i < 50 && !manager.ActiveRelay.IsBypassed; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(manager.ActiveRelay.IsBypassed);
        Assert.Equal(remoteServer, manager.EffectiveClientConnectEndPoint);
    }

    [Fact]
    public async Task SessionManager_OptionsMonitorLiveToggle_SwitchesEffectiveEndpoint()
    {
        // protects: Runtime config toggle via IOptionsMonitor immediately updates EffectiveClientConnectEndPoint
        // fails_when: Kill switch does not update EffectiveClientConnectEndPoint
        // seam: IOptionsMonitor.OnChange -> ClientNetcodeRelay.EffectiveClientConnectEndPoint
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int serverPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;
        IPEndPoint remoteServer = new(IPAddress.Loopback, serverPort);

        NetcodeRelayOptions options = new() { RelayEnabled = true, HandshakeTimeoutMs = 2000 };
        TestOptionsMonitor<NetcodeRelayOptions> monitor = new(options);

        await using ClientNetcodeRelaySessionManager manager = new(monitor);
        IPEndPoint initialLoopback = manager.ResolveEffectiveConnectEndPoint(remoteServer);

        Assert.Equal(IPAddress.Loopback, initialLoopback.Address);
        Assert.Equal(initialLoopback, manager.EffectiveClientConnectEndPoint);

        // Trip kill switch via IOptionsMonitor
        monitor.Update(new NetcodeRelayOptions { RelayEnabled = false });

        Assert.True(manager.ActiveRelay!.IsBypassed);
        Assert.Equal(remoteServer, manager.EffectiveClientConnectEndPoint);
    }

    [Fact]
    public async Task MainViewModel_WiresLiveRelaySessionManagerToDirectPlay_AndStopsRelayOnSessionTeardown()
    {
        // protects: Production WILLOWMAKER MainViewModel wiring of ClientNetcodeRelaySessionManager into DirectPlayViewModel and bounded teardown
        // fails_when: MainViewModel constructs DirectPlayViewModel with null relay manager or leaks UDP relay sockets across DisposeActiveGameSessionAsync
        // seam: MainViewModel -> DirectPlayViewModel -> ClientNetcodeRelaySessionManager.StopRelay
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int serverPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;
        IPEndPoint remoteServer = new(IPAddress.Loopback, serverPort);

        MainViewModel vm = new();
        try
        {
            Assert.NotNull(vm.NetcodeRelaySessionManager);
            Assert.Same(vm.NetcodeRelaySessionManager, vm.DirectPlay.RelaySessionManager);

            vm.NetcodeRelaySessionManager.EnableClientZstdRelay = true;
            IPEndPoint effective = vm.NetcodeRelaySessionManager.ResolveEffectiveConnectEndPoint(remoteServer);
            Assert.NotNull(vm.NetcodeRelaySessionManager.ActiveRelay);
            Assert.NotEqual(serverPort, effective.Port);

            await vm.DisposeActiveGameSessionAsync("unit-test-teardown");
            Assert.Null(vm.NetcodeRelaySessionManager.ActiveRelay);

            // Manager remains reusable for a subsequent match launch within the same launcher lifetime
            IPEndPoint secondEffective = vm.NetcodeRelaySessionManager.ResolveEffectiveConnectEndPoint(remoteServer);
            Assert.NotNull(vm.NetcodeRelaySessionManager.ActiveRelay);
            Assert.NotEqual(serverPort, secondEffective.Port);
        }
        finally
        {
            vm.DisposeApplicationBackgroundServices();
            Assert.Null(vm.NetcodeRelaySessionManager.ActiveRelay);
        }
    }

    [Fact]
    public async Task DirectPlayViewModel_ConnectToHostAsync_ResolvesHostnameThroughClientNetcodeRelay()
    {
        // protects: Hostname:port join addresses (e.g. localhost:11235) routing through ClientNetcodeRelay
        // fails_when: DirectPlayViewModel only calls IPEndPoint.TryParse and silently skips relay for DNS hostnames
        // seam: DirectPlayViewModel.ConnectToHostAsync + Dns.GetHostAddressesAsync + ClientNetcodeRelaySessionManager
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int serverPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;

        NetcodeRelayOptions options = new() { RelayEnabled = true, HandshakeTimeoutMs = 1500 };
        TestOptionsMonitor<NetcodeRelayOptions> monitor = new(options);
        await using ClientNetcodeRelaySessionManager manager = new(monitor);

        List<string>? capturedArgs = null;
        DirectPlayViewModel vm = new(
            args =>
            {
                capturedArgs = args.ToList();
                return Task.CompletedTask;
            },
            manager)
        {
            BypassMasterServer = false,
            PlayerNickname = "RelayTester",
            JoinAddress = $"localhost:{serverPort}"
        };

        await vm.ConnectToHostAsync();

        Assert.NotNull(manager.ActiveRelay);
        Assert.NotNull(capturedArgs);
        string joinedArgs = string.Join(" ", capturedArgs!);
        Assert.Contains($"127.0.0.1:{manager.ActiveRelay!.LocalPort}", joinedArgs);
    }

    [Fact]
    public void AppendPlayerSessionRecord_WhenInsideRepoLayout_WritesToRepoRootLogsDirectory()
    {
        // protects: SLO 6 telemetry path alignment between WILLOWMAKER session writer and scripts/verify_north_star_slos.ps1
        // fails_when: AppendPlayerSessionRecord strands records inside games/client/logs instead of <repoRoot>/logs when ProjectKongor.slnx is present
        // seam: MainViewModel.AppendPlayerSessionRecord filesystem layout probe
        string tempRepoRoot = Path.Combine(Path.GetTempPath(), "kongor_p38b_repo_" + Guid.NewGuid().ToString("N"));
        string fakeClientRoot = Path.Combine(tempRepoRoot, "games", "client");
        Directory.CreateDirectory(fakeClientRoot);
        File.WriteAllText(Path.Combine(tempRepoRoot, "ProjectKongor.slnx"), "<Solution />");

        try
        {
            MainViewModel.AppendPlayerSessionRecord(
                clientRoot: fakeClientRoot,
                processId: 4242,
                launchStartUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
                exitCode: 0u,
                isCrash: false,
                crashReason: null);

            string expectedSessionsPath = Path.Combine(tempRepoRoot, "logs", "player_sessions.jsonl");
            Assert.True(File.Exists(expectedSessionsPath));
            string content = File.ReadAllText(expectedSessionsPath);
            Assert.Contains("\"processId\":4242", content);
            Assert.Contains("\"crashed\":false", content);
        }
        finally
        {
            if (Directory.Exists(tempRepoRoot))
            {
                Directory.Delete(tempRepoRoot, recursive: true);
            }
        }
    }
}
