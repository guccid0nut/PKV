// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Wave1BCompelSecurityTests.cs
// System: COMPEL Tests - Wave 1B Zero-Trust Auth, Route Hardening, Voice & UDP DoS
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ASPIRE.Common.Netcode;
using COMPEL.Configuration;
using COMPEL.Endpoints;
using COMPEL.Serialisation;
using COMPEL.Services.ContentBroker;
using COMPEL.Services.Ping;
using COMPEL.Services.Proxy;
using COMPEL.Services.Supervision;
using COMPEL.Services.Voice;
using FluentAssertions;
using Kongor.Core.Netcode.Voice;
using Kongor.Core.Services.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace COMPEL.Tests;

public sealed class CompelZeroTrustAndVoiceRelaySecurityTests
{
    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static Socket CreateLoopbackUdpSocket()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            const int sioUdpConnReset = -1744830452;
            socket.IOControl((IOControlCode)sioUdpConnReset, [0, 0, 0, 0], null);
        }

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static async Task<(byte[] Payload, EndPoint Sender)?> TryReceiveAsync(Socket socket, int timeoutMs = 250)
    {
        byte[] buffer = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                remote,
                cts.Token).ConfigureAwait(false);

            byte[] payload = new byte[result.ReceivedBytes];
            Array.Copy(buffer, payload, result.ReceivedBytes);
            return (payload, result.RemoteEndPoint);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // protects: ControlPlaneAuthentication bearer token validation across all environments including Development
    // fails_when: ControlPlaneAuthentication accepts hardcoded "devtoken" or wildcard "compel_*" prefixes without matching the configured token
    // seam: Real ControlPlaneAuthentication.Validate + DefaultHttpContext + IOptions<ControlPlaneOptions>
    [Fact]
    public void ControlPlaneAuthentication_InDevelopment_RejectsDevTokenAndCompelPrefixWildcard()
    {
        const string configuredToken = "alpha-token-01";

        Mock<IHostEnvironment> devEnv = new();
        devEnv.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(devEnv.Object);
        services.AddSingleton<IOptions<ControlPlaneOptions>>(
            Options.Create(new ControlPlaneOptions { AuthenticationToken = configuredToken }));
        using ServiceProvider sp = services.BuildServiceProvider();

        // 1. "devtoken" must be rejected by default even in Development
        DefaultHttpContext devTokenCtx = new() { RequestServices = sp };
        devTokenCtx.Request.Headers.Authorization = "Bearer devtoken";
        IResult? devTokenResult = ControlPlaneAuthentication.Validate(devTokenCtx);
        devTokenResult.Should().NotBeNull("hardcoded devtoken must be rejected unless explicit dev opt-in is enabled");

        // 2. Arbitrary "compel_*" prefix token must ALWAYS be rejected
        DefaultHttpContext wildcardCtx = new() { RequestServices = sp };
        wildcardCtx.Request.Headers.Authorization = "Bearer compel_test_short";
        IResult? wildcardResult = ControlPlaneAuthentication.Validate(wildcardCtx);
        wildcardResult.Should().NotBeNull("StartsWith('compel_') wildcard acceptance must never allow arbitrary tokens");

        // 3. Exact configured token must succeed (returns null filter continuation)
        DefaultHttpContext validCtx = new() { RequestServices = sp };
        validCtx.Request.Headers.Authorization = $"Bearer {configuredToken}";
        IResult? validResult = ControlPlaneAuthentication.Validate(validCtx);
        validResult.Should().BeNull("exact fixed-time match of configured token must be accepted");

        // 4. Even when AllowInsecureDevToken == true in Development, "compel_*" wildcard is STILL rejected while explicit "devtoken" is permitted
        ServiceCollection optInServices = new();
        optInServices.AddSingleton<IHostEnvironment>(devEnv.Object);
        optInServices.AddSingleton<IOptions<ControlPlaneOptions>>(
            Options.Create(new ControlPlaneOptions { AuthenticationToken = configuredToken, AllowInsecureDevToken = true }));
        using ServiceProvider optInSp = optInServices.BuildServiceProvider();

        DefaultHttpContext optInDevCtx = new() { RequestServices = optInSp };
        optInDevCtx.Request.Headers.Authorization = "Bearer devtoken";
        ControlPlaneAuthentication.Validate(optInDevCtx).Should().BeNull();

        DefaultHttpContext optInWildcardCtx = new() { RequestServices = optInSp };
        optInWildcardCtx.Request.Headers.Authorization = "Bearer compel_test_short";
        ControlPlaneAuthentication.Validate(optInWildcardCtx).Should().NotBeNull();
    }

    // protects: COMPEL HTTP control-plane telemetry, server roster, and internal moderation/ignore endpoints
    // fails_when: /status, /metrics, /servers, /api/moderation/mutes, or /internal routes allow anonymous callers without management bearer auth, or /healthz is missing
    // seam: Live Kestrel WebApplication + MapControlPlaneEndpoints + HttpClient HTTP probe
    [Fact]
    public async Task ControlPlaneEndpoints_RequiresAuthOnStatusMetricsServersAndInternalRoutes_AndExposesMinimalHealthz()
    {
        const string managementToken = "test-mgmt-token-bravo";

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IOptions<ControlPlaneOptions>>(
            Options.Create(new ControlPlaneOptions { AuthenticationToken = managementToken }));
        builder.Services.AddSingleton<IVoiceMuteRegistry>(new VoiceMuteRegistry());
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlPlaneJSONContext.Default);
        });

        WebApplication app = builder.Build();
        app.MapControlPlaneEndpoints();
        await app.StartAsync();

        try
        {
            ICollection<string>? addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            string baseAddress = addresses?.FirstOrDefault() ?? app.Urls.First();
            using HttpClient client = new() { BaseAddress = new Uri(baseAddress) };

            string[] protectedGetPaths =
            [
                "/status",
                "/api/status",
                "/metrics",
                "/api/metrics",
                "/servers",
                "/api/servers",
                "/servers/11235",
                "/api/moderation/mutes"
            ];

            foreach (string path in protectedGetPaths)
            {
                HttpResponseMessage anonRes = await client.GetAsync(path);
                anonRes.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    $"route '{path}' must require management bearer authentication");
            }

            // Internal moderation/ignore POST & DELETE routes must also require management auth
            using StringContent muteJson = new(
                "{\"accountId\":1001,\"durationSeconds\":60,\"reason\":\"test\"}",
                Encoding.UTF8,
                "application/json");
            HttpResponseMessage anonInternalMute = await client.PostAsync("/api/moderation/mutes/internal", muteJson);
            anonInternalMute.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            HttpResponseMessage anonDeleteMute = await client.DeleteAsync("/api/moderation/mutes/internal/1001");
            anonDeleteMute.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using StringContent ignoreJson = new(
                "{\"listenerAccountId\":1001,\"speakerAccountId\":1002}",
                Encoding.UTF8,
                "application/json");
            HttpResponseMessage anonInternalIgnore = await client.PostAsync("/api/voice/ignores/internal", ignoreJson);
            anonInternalIgnore.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Minimal /healthz liveness probe must succeed anonymously without leaking tokens or IPs
            HttpResponseMessage healthzRes = await client.GetAsync("/healthz");
            healthzRes.StatusCode.Should().Be(HttpStatusCode.OK);
            string healthzBody = await healthzRes.Content.ReadAsStringAsync();
            healthzBody.Should().NotContain(managementToken);
            healthzBody.Should().NotContain("11235");

            // Authenticated request with valid bearer token succeeds
            using HttpRequestMessage authedServersReq = new(HttpMethod.Get, "/api/moderation/mutes");
            authedServersReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
            HttpResponseMessage authedRes = await client.SendAsync(authedServersReq);
            authedRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // protects: Opus UDP voice relay room privacy, participant endpoint binding, and anti-amplification on UDP 11600
    // fails_when: Unregistered UDP callers auto-join rooms to eavesdrop/inject audio or spoof a registered SenderAccountId from another endpoint
    // seam: Live CompelVoiceRelayService UDP socket loop + RoutePacket + UDP client sockets
    [Fact]
    public async Task CompelVoiceRelayService_DropsUnregisteredCallersAndEndpointSpoofing_PreventingEavesdropAndAmplification()
    {
        int relayPort = FreeUdpPort();
        using Socket aliceSocket = CreateLoopbackUdpSocket();
        using Socket bobSocket = CreateLoopbackUdpSocket();
        using Socket attackerSocket = CreateLoopbackUdpSocket();

        IPEndPoint aliceEp = (IPEndPoint)aliceSocket.LocalEndPoint!;
        IPEndPoint bobEp = (IPEndPoint)bobSocket.LocalEndPoint!;
        IPEndPoint attackerEp = (IPEndPoint)attackerSocket.LocalEndPoint!;
        IPEndPoint relayEp = new(IPAddress.Loopback, relayPort);

        using CompelVoiceRelayService service = new(
            NullLogger<CompelVoiceRelayService>.Instance,
            port: relayPort);

        const uint matchRoomId = 5001;
        service.RegisterParticipant(VoiceChannelType.Team, matchRoomId, accountId: 101, aliceEp);
        service.RegisterParticipant(VoiceChannelType.Team, matchRoomId, accountId: 102, bobEp);

        await service.StartAsync(CancellationToken.None);

        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (!service.IsRunning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }
            service.IsRunning.Should().BeTrue();
            // 1. Unregistered attacker attempts to auto-join room 5001 as accountId 999
            VoiceDatagramPacket anonJoinPacket = new(
                VoiceChannelType.Team,
                matchRoomId,
                senderAccountId: 999,
                sequence: 1,
                payload: new byte[] { 0xAA, 0xBB, 0xCC });
            await attackerSocket.SendToAsync(anonJoinPacket.ToByteArray(), SocketFlags.None, relayEp);

            // Bob must NOT receive injected audio from unregistered account 999
            (byte[] Payload, EndPoint Sender)? bobFromAnon = await TryReceiveAsync(bobSocket, timeoutMs: 200);
            bobFromAnon.Should().BeNull("unregistered UDP callers must never have audio relayed into active voice rooms");
            service.ActiveParticipantsCount.Should().Be(2, "inbound UDP datagrams must never auto-register anonymous participants");

            // 2. Attacker spoofs Alice's registered accountId (101) from attackerSocket endpoint
            VoiceDatagramPacket spoofAlicePacket = new(
                VoiceChannelType.Team,
                matchRoomId,
                senderAccountId: 101,
                sequence: 2,
                payload: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            await attackerSocket.SendToAsync(spoofAlicePacket.ToByteArray(), SocketFlags.None, relayEp);

            (byte[] Payload, EndPoint Sender)? bobFromSpoof = await TryReceiveAsync(bobSocket, timeoutMs: 200);
            bobFromSpoof.Should().BeNull("spoofed SenderAccountId from non-matching endpoint must be dropped");

            // 3. Legitimate Alice speaks -> Bob receives it, while attacker receives NOTHING (no eavesdropping!)
            VoiceDatagramPacket aliceLegitPacket = new(
                VoiceChannelType.Team,
                matchRoomId,
                senderAccountId: 101,
                sequence: 3,
                payload: new byte[] { 0x11, 0x22, 0x33 });
            await aliceSocket.SendToAsync(aliceLegitPacket.ToByteArray(), SocketFlags.None, relayEp);

            (byte[] Payload, EndPoint Sender)? bobLegit = await TryReceiveAsync(bobSocket, timeoutMs: 300);
            bobLegit.Should().NotBeNull("pre-registered participant sending from bound endpoint must be relayed to room peers");

            (byte[] Payload, EndPoint Sender)? attackerEavesdrop = await TryReceiveAsync(attackerSocket, timeoutMs: 150);
            attackerEavesdrop.Should().BeNull("attacker must not receive eavesdropped room audio");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // protects: KongorPacketEnvelope ChaCha20-Poly1305 AEAD authenticity in UDPForwarder
    // fails_when: UDPForwarder falls back to deriving a deterministic key from "Kongor-Session-{PublicPort}-{client}" when sessionKeyResolver is null
    // seam: Live UDPForwarder socket loop + KongorPacketEnvelope + DatagramAuthenticator
    [Fact]
    public async Task UdpForwarder_WhenSessionKeyResolverIsNull_RejectsDeterministicPublicEndpointKeyEnvelope()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using Socket dedicatedServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dedicatedServer.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            ProxyForwarderKind.Game,
            TimeSpan.FromSeconds(10),
            new ViolationScoreContainer(TimeProvider.System),
            new AttackIndicatorContainer(TimeProvider.System),
            TimeProvider.System,
            NullLogger.Instance,
            sessionKeyResolver: null);

        using CancellationTokenSource cts = new();
        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            using Socket attacker = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            attacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint attackerLocalEp = (IPEndPoint)attacker.LocalEndPoint!;
            IPEndPoint forwarderPublicEp = new(IPAddress.Loopback, publicPort);

            // Predict the exact legacy deterministic key formula: $"Kongor-Session-{PublicPort}-{client}"
            byte[] predictableKey = DatagramAuthenticator.DeriveSessionKey(
                Encoding.UTF8.GetBytes($"Kongor-Session-{publicPort}-{attackerLocalEp}"));

            using DatagramAuthenticator forgedCrypto = new(predictableKey, sessionId: 0);
            byte[] payload = Encoding.UTF8.GetBytes("ForgedChallengeBypassCommand");
            KongorPacketHeader header = new(sessionId: 1337, packetSequence: 1, subTickDeltaUs: 0);
            byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + payload.Length];
            bool wrapped = forgedCrypto.TryEncryptAndWrap(
                envelope.AsSpan(),
                in header,
                payload.AsSpan(),
                DatagramDirection.ClientToServer,
                out int written);
            wrapped.Should().BeTrue();

            await attacker.SendToAsync(envelope.AsMemory(0, written), SocketFlags.None, forwarderPublicEp);

            (byte[] Payload, EndPoint Sender)? serverReceived = await TryReceiveAsync(dedicatedServer, timeoutMs: 250);
            serverReceived.Should().BeNull("UDPForwarder must never accept modern envelopes encrypted with a predictable IP:Port fallback key");
            forwarder.DroppedDatagramCount.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await forwarderTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }
    }

    // protects: Public UDP game forwarder against 9.3x unauthenticated 0xCA ping amplification floods
    // fails_when: UDPForwarder echoes unlimited ~56+ byte PONG packets in response to a rapid burst of 6-byte 0xCA ping frames from one IP
    // seam: Live UDPForwarder socket loop + SendPongAsync + UdpRateLimiter
    [Fact]
    public async Task UdpForwarder_WhenFloodedWithPingFramesFromSameIp_RateLimitsPongAmplification()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using Socket dedicatedServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dedicatedServer.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            ProxyForwarderKind.Game,
            TimeSpan.FromSeconds(10),
            new ViolationScoreContainer(TimeProvider.System),
            new AttackIndicatorContainer(TimeProvider.System),
            TimeProvider.System,
            NullLogger.Instance,
            serverName: "KONGOR ARENA");

        using CancellationTokenSource cts = new();
        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            using Socket floodClient = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            floodClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint forwarderPublicEp = new(IPAddress.Loopback, publicPort);

            const int burstCount = 30;
            byte[] compactPing = [0x00, 0x00, 0x01, 0xCA, 0x12, 0x34];

            for (int i = 0; i < burstCount; i++)
            {
                compactPing[4] = (byte)i;
                await floodClient.SendToAsync(compactPing, SocketFlags.None, forwarderPublicEp);
            }

            int pongCount = 0;
            while (true)
            {
                (byte[] Payload, EndPoint Sender)? pong = await TryReceiveAsync(floodClient, timeoutMs: 150);
                if (pong == null)
                {
                    break;
                }
                if (pong.Value.Payload.Length >= 50)
                {
                    pongCount++;
                }
            }

            pongCount.Should().BeGreaterThan(0, "initial legitimate server browser pings must receive a PONG");
            pongCount.Should().BeLessThanOrEqualTo(15, "ping bursts from a single IP must be throttled by UdpRateLimiter to prevent 9.3x amplification");
            forwarder.DroppedDatagramCount.Should().BeGreaterThan(0);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await forwarderTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }
    }

    // protects: COMPEL HTTP control-plane listener network exposure
    // fails_when: Control-plane bind address defaults to 0.0.0.0 (IPAddress.Any) when Compel:ControlPlaneBindAddress / COMPEL_CONTROL_PLANE_BIND_ADDRESS is unset or invalid
    // seam: ControlPlaneOptions.ResolveBindAddress + IConfigurationBuilder
    [Fact]
    public void ControlPlaneBind_DefaultsToLoopbackWhenUnconfiguredOrInvalid_AndHonorsExplicitAddress()
    {
        IConfiguration emptyConfig = new ConfigurationBuilder().Build();
        ControlPlaneOptions.ResolveBindAddress(emptyConfig).Should().Be(IPAddress.Loopback);

        IConfiguration invalidConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Compel:ControlPlaneBindAddress"] = "not-an-ip-address"
            })
            .Build();
        ControlPlaneOptions.ResolveBindAddress(invalidConfig).Should().Be(IPAddress.Loopback);

        IConfiguration explicitConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Compel:ControlPlaneBindAddress"] = "198.51.100.25"
            })
            .Build();
        ControlPlaneOptions.ResolveBindAddress(explicitConfig).Should().Be(IPAddress.Parse("198.51.100.25"));
    }

    // protects: DedicatedServerWorker local Win32 Named Pipe RCE boundary (\\.\pipe\hon-server-{port})
    // fails_when: NamedPipeServerStream uses default Windows DACL granting Everyone/WorldSid write access instead of locking strictly to the owner SID
    // seam: Real Windows NamedPipeServerStream + PipesAclExtensions.GetAccessControl + SecurityIdentifier
    [Fact]
    public void DedicatedServerWorker_OnWindows_CreatesNamedPipeRestrictedToCurrentUserOwnerSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AssertWindowsNamedPipeOwnerOnlyAcl();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertWindowsNamedPipeOwnerOnlyAcl()
    {
        string pipeName = $"hon-server-acl-test-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = DedicatedServerWorker.CreateCommandPipeServer(pipeName);

        SecurityIdentifier ownerSid = WindowsIdentity.GetCurrent().User!;
        SecurityIdentifier worldSid = new(WellKnownSidType.WorldSid, null);
        SecurityIdentifier authenticatedUsersSid = new(WellKnownSidType.AuthenticatedUserSid, null);

        PipeSecurity pipeSecurity = pipe.GetAccessControl();
        AuthorizationRuleCollection rules = pipeSecurity.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));

        bool hasOwnerAllow = false;
        bool hasWorldAllow = false;
        bool hasAuthenticatedUsersAllow = false;
        foreach (PipeAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                if (rule.IdentityReference == ownerSid)
                {
                    hasOwnerAllow = true;
                }
                if (rule.IdentityReference == worldSid)
                {
                    hasWorldAllow = true;
                }
                if (rule.IdentityReference == authenticatedUsersSid)
                {
                    hasAuthenticatedUsersAllow = true;
                }
            }
        }

        hasOwnerAllow.Should().BeTrue("Named pipe must grant access to the process owner SID");
        hasWorldAllow.Should().BeFalse("Named pipe must never grant Everyone (WorldSid) command injection access");
        hasAuthenticatedUsersAllow.Should().BeFalse("Named pipe must restrict access strictly to the process owner SID");
    }

    // protects: COMPEL master-server UDPPingResponder (0xCA -> 0x66) against unauthenticated ping amplification
    // fails_when: UDPPingResponder allows unbounded ping responses to a single IP burst without rate-limiting
    // seam: UDPPingResponder.TryAllowPingResponse + UdpRateLimiter
    [Fact]
    public void UdpPingResponder_WhenFloodedFromSingleIp_RateLimitsResponses()
    {
        MatchServerManagerOptions mgrOpts = new()
        {
            UserName = "TestHost",
            Password = "password",
            Instances = 1,
            Gateway = "127.0.0.1"
        };
        PortPlan plan = new(mgrOpts);

        using UDPPingResponder responder = new(
            Options.Create(mgrOpts),
            null!,
            plan,
            NullLogger<UDPPingResponder>.Instance);

        IPAddress attackerIp = IPAddress.Parse("203.0.113.77");
        int allowed = 0;
        for (int i = 0; i < 40; i++)
        {
            if (responder.TryAllowPingResponse(attackerIp))
            {
                allowed++;
            }
        }

        allowed.Should().BeGreaterThan(0);
        allowed.Should().BeLessThanOrEqualTo(15, "UDPPingResponder must throttle per-IP ping bursts");
    }
}
