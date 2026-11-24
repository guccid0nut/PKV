// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: P2pConnectionServiceTests.cs
// System: WILLOWMAKER Unit Tests - P2P Connection & Address Normalization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using ASPIRE.Common.Serialization.PHP;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Services.P2P;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class P2pConnectionServiceTests
{
    [Fact]
    public void NormalizeConnectAddress_WithBareIp_AppendsDefaultPort()
    {
        string result = P2pConnectionService.NormalizeConnectAddress("192.168.1.100", 11235);
        Assert.Equal("192.168.1.100:11235", result);
    }

    [Fact]
    public void NormalizeConnectAddress_WithIpAndPort_PreservesCustomPort()
    {
        string result = P2pConnectionService.NormalizeConnectAddress("192.168.1.100:11239", 11235);
        Assert.Equal("192.168.1.100:11239", result);
    }

    [Fact]
    public void NormalizeConnectAddress_WithConnectPrefixAndWhitespace_StripsPrefixAndWhitespace()
    {
        string result = P2pConnectionService.NormalizeConnectAddress("  connect 10.0.0.42:11235  ", 11235);
        Assert.Equal("10.0.0.42:11235", result);
    }

    [Fact]
    public void NormalizeConnectAddress_WithEmptyString_ReturnsLoopbackDefault()
    {
        string result = P2pConnectionService.NormalizeConnectAddress("   ", 11235);
        Assert.Equal("127.0.0.1:11235", result);
    }

    [Fact]
    public void P2pConnectionService_GetLocalIpAddress_ReturnsValidIpAddress()
    {
        string ip = P2pConnectionService.GetLocalIpAddress();
        Assert.False(string.IsNullOrWhiteSpace(ip));
        bool isParsed = IPAddress.TryParse(ip, out IPAddress? parsed);
        Assert.True(isParsed);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void P2pConnectionService_ResolveDefaultNickname_NeverReturnsHostUserName()
    {
        string nick = P2pConnectionService.ResolveDefaultNickname();
        Assert.False(string.IsNullOrWhiteSpace(nick));
        Assert.NotEqual(Environment.UserName, nick, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("UnnamedNewbie", nick, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void P2pConnectionService_EnsureClientDefaultNameConfigured_WritesUserdataKongorFilesWithoutMutatingOverride()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(tempDir);
            P2pConnectionService.EnsureClientDefaultNameConfigured(tempDir, "TestWarrior");

            string enPath = System.IO.Path.Combine(tempDir, "userdataKONGOR", "extensions", "stringtables", "client_messages_en.str");
            Assert.True(System.IO.File.Exists(enPath));
            string content = System.IO.File.ReadAllText(enPath);
            Assert.Contains("TestWarrior", content);
            Assert.Contains("client_default_name", content);

            string overrideEnPath = System.IO.Path.Combine(tempDir, "override", "stringtables", "client_messages_en.str");
            Assert.False(System.IO.File.Exists(overrideEnPath));
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void P2pConnectionService_DefaultPort_IsDeconflictedFromCompelTierBands()
    {
        Assert.Equal(11550, P2pConnectionService.DefaultPort);
        string normalized = P2pConnectionService.NormalizeConnectAddress("192.168.1.50");
        Assert.Equal("192.168.1.50:11550", normalized);
    }

    [Fact]
    public void FindAvailableUdpPort_WhenPreferredSocketIsOccupied_SkipsToNextFreePort()
    {
        // Bind an unoccupied port in the normal user range (< 60000) so candidate + VoicePortOffset does not overflow 65000
        int occupiedPort = 0;
        Socket blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blocker.ExclusiveAddressUse = true;
        for (int p = 25000; p < 25100; p++)
        {
            try
            {
                blocker.Bind(new IPEndPoint(IPAddress.Loopback, p));
                occupiedPort = p;
                break;
            }
            catch (SocketException)
            {
                // Port in use, probe next candidate
            }
        }

        Assert.True(occupiedPort > 0, "Failed to bind test UDP blocker socket.");
        using (blocker)
        {
            Assert.False(P2pConnectionService.IsUdpPortAvailable(occupiedPort));

            int resolved = P2pConnectionService.FindAvailableUdpPort(occupiedPort, 20);
            Assert.NotEqual(occupiedPort, resolved);
            Assert.True(resolved > occupiedPort);
        }
    }

    [Fact]
    public void P2pHostServerService_EnsureSharedClientAssetsLinked_LinksAssetsWhileIsolatingServerDllsAndSkippingTexturesS2z()
    {
        string rootTemp = Path.Combine(Path.GetTempPath(), "KongorP2pLink_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(rootTemp, "games", "client");
        string serverP2pDir = Path.Combine(rootTemp, "games", "server_p2p");

        try
        {
            Directory.CreateDirectory(Path.Combine(clientDir, "base"));
            Directory.CreateDirectory(Path.Combine(clientDir, "override", "bots"));
            Directory.CreateDirectory(Path.Combine(clientDir, "game", "maps"));
            Directory.CreateDirectory(Path.Combine(serverP2pDir, "game"));

            File.WriteAllText(Path.Combine(clientDir, "override", "bots", "marker.txt"), "bot-override");
            File.WriteAllText(Path.Combine(clientDir, "game", "maps", "midwars.s2z"), "map-archive");
            File.WriteAllText(Path.Combine(clientDir, "game", "resources0.s2z"), "shared-resources0");
            File.WriteAllText(Path.Combine(clientDir, "game", "textures.s2z"), "client-only-textures");
            File.WriteAllText(Path.Combine(clientDir, "game", "game_x64.dll"), "CLIENT_GAME_DLL");

            File.WriteAllText(Path.Combine(serverP2pDir, "k2_x64.dll"), "SERVER_K2_DLL");
            File.WriteAllText(Path.Combine(serverP2pDir, "game", "game_x64.dll"), "SERVER_GAME_DLL");

            P2pHostServerService.EnsureSharedClientAssetsLinked(serverP2pDir, clientDir);

            Assert.True(File.Exists(Path.Combine(serverP2pDir, "override", "bots", "marker.txt")));
            Assert.True(File.Exists(Path.Combine(serverP2pDir, "game", "maps", "midwars.s2z")));
            Assert.True(File.Exists(Path.Combine(serverP2pDir, "game", "resources0.s2z")));
            Assert.False(File.Exists(Path.Combine(serverP2pDir, "game", "textures.s2z")));
            Assert.Equal("SERVER_GAME_DLL", File.ReadAllText(Path.Combine(serverP2pDir, "game", "game_x64.dll")));
            Assert.Equal("SERVER_K2_DLL", File.ReadAllText(Path.Combine(serverP2pDir, "k2_x64.dll")));
        }
        finally
        {
            if (Directory.Exists(rootTemp))
            {
                Directory.Delete(rootTemp, true);
            }
        }
    }

    [Fact]
    public void P2pHostServerService_BuildDedicatedHostArguments_ConfiguresLoopbackSessionAndNonZeroAuthTimeout()
    {
        // protects: K2_SERVER P2P worker CLI arguments against svr_authTimeout=0 instant client kicks and missing session cookies.
        // fails_when: svr_authTimeout regresses to 0, svr_authRequestAttempts regresses to 0, or custom loopback endpoint is ignored.
        // seam: Real P2pHostServerService.BuildDedicatedHostArguments CLI builder.
        string tempServerDir = Path.Combine(Path.GetTempPath(), "KongorP2pArgs_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempServerDir, "override"));
            Directory.CreateDirectory(Path.Combine(tempServerDir, "base"));
            Directory.CreateDirectory(Path.Combine(tempServerDir, "game"));

            string args = P2pHostServerService.BuildDedicatedHostArguments(
                tempServerDir,
                11550,
                12050,
                "SmokeHost_P2P",
                "127.0.0.1:20123");

            Assert.DoesNotContain("svr_login false", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sv_confirmMatchIDForRecon;", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("svr_authTimeout 0;", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("svr_authTimeout 0 ", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("svr_broadcast false", args, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-slave 1", args, StringComparison.Ordinal);
            Assert.Contains("-logfile server_11550.log", args, StringComparison.Ordinal);
            Assert.Contains("-masterserver 127.0.0.1:20123", args, StringComparison.Ordinal);
            Assert.Contains("-webserver 127.0.0.1:20123", args, StringComparison.Ordinal);
            Assert.Contains("-messageserver 127.0.0.1:20123", args, StringComparison.Ordinal);
            Assert.Contains("Set svr_login p2phost:1;", args, StringComparison.Ordinal);
            Assert.Contains("Set svr_adminPassword kongorp2p;", args, StringComparison.Ordinal);
            Assert.Contains("+Set svr_adminPassword \"kongorp2p\"", args, StringComparison.Ordinal);
            Assert.Contains("Set svr_broadcast true;", args, StringComparison.Ordinal);
            Assert.Contains("Set host_needTokenToCreateGame false;", args, StringComparison.Ordinal);
            Assert.Contains("+Set svr_login \"p2phost:1\"", args, StringComparison.Ordinal);
            Assert.Contains("Set svr_authTimeout 86400000;", args, StringComparison.Ordinal);
            Assert.Contains("Set svr_authRequestAttempts 3;", args, StringComparison.Ordinal);
            Assert.Contains("Set svr_matchIDRequestAttempts 3;", args, StringComparison.Ordinal);
            Assert.Contains("sv_confirmMatchIDForReconnect false", args, StringComparison.Ordinal);
            Assert.Contains("svr_matchIDRequired false", args, StringComparison.Ordinal);
            Assert.Contains("svr_gameFPS 20", args, StringComparison.Ordinal);
            Assert.Contains("+Set svr_port 11550", args, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempServerDir))
            {
                Directory.Delete(tempServerDir, true);
            }
        }
    }

    [Fact]
    public void P2pCookieCodec_EncodeAndDecode_RoundTripsRemoteFriendNicknamesAndHandlesOnlineCookies()
    {
        // protects: Stateless 32-hex P2P cookie identity encoding across distinct Host/Guest machines with zero WAN HTTP ports.
        // fails_when: Cookie hex length != 32, remote friend nickname fails round-trip decoding, or online MasterServer cookies fail fallback.
        // seam: Real P2pCookieCodec + CRC32 deterministic AccountId mapping.
        string friendCookie = P2pCookieCodec.Encode("  Smoke Friend!  ");
        Assert.Equal(32, friendCookie.Length);
        Assert.StartsWith("a5", friendCookie, StringComparison.Ordinal);

        (string decodedNick, int decodedId, bool isP2p) = P2pCookieCodec.DecodeOrFallback(friendCookie);
        Assert.True(isP2p);
        Assert.Equal("Smoke_Friend!", decodedNick);
        Assert.Equal(P2pCookieCodec.ComputeDeterministicAccountId("Smoke_Friend!"), decodedId);
        Assert.InRange(decodedId, 100000, 999999);

        string longCookie = P2pCookieCodec.Encode("12345678901234567890");
        Assert.Equal(32, longCookie.Length);
        (string truncatedNick, int truncatedId, bool isLongP2p) = P2pCookieCodec.DecodeOrFallback(longCookie);
        Assert.True(isLongP2p);
        Assert.Equal("123456789012345", truncatedNick);
        Assert.InRange(truncatedId, 100000, 999999);

        (string onlineNick, int onlineId, bool isOnlineP2p) = P2pCookieCodec.DecodeOrFallback("7f8e9d0c1b2a3f4e5d6c7b8a9f0e1d2c");
        Assert.False(isOnlineP2p);
        Assert.StartsWith("Player_", onlineNick, StringComparison.Ordinal);
        Assert.InRange(onlineId, 100000, 999999);
    }

    [Fact]
    public async Task P2pLoopbackAuthServer_FullClientAndServerP2pHandshake_ReturnsValidPhpPayloadsForRemoteFriend()
    {
        // protects: K2 client_requester.php (auth) + server_requester.php (new_session, c_conn, start_game, aids2cookie) wire contract & port fallback.
        // fails_when: Any PHP payload fails PhpDeserializer.Deserialize, remote Guest cookie is rejected by Host server_p2p, or occupied port crashes startup.
        // seam: Live TCP P2pLoopbackAuthServer + HttpClient + ASPIRE.Common.Serialization.PHP.PhpDeserializer.
        using TcpListener occupiedListener = new TcpListener(IPAddress.Loopback, 0);
        occupiedListener.Start();
        int occupiedPort = ((IPEndPoint)occupiedListener.LocalEndpoint).Port;

        using P2pLoopbackAuthServer hostAuthServer = P2pLoopbackAuthServer.StartOnAvailableLoopbackPort(occupiedPort, 8);
        using P2pLoopbackAuthServer guestAuthServer = P2pLoopbackAuthServer.StartOnAvailableLoopbackPort(0);

        Assert.NotEqual(occupiedPort, hostAuthServer.BoundPort);
        Assert.True(hostAuthServer.IsRunning);
        Assert.True(guestAuthServer.IsRunning);

        using HttpClient http = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(5));
        http.DefaultRequestHeaders.ExpectContinue = true;

        // 1. Remote Guest authenticates against Guest's OWN loopback responder (simulating a separate PC)
        using FormUrlEncodedContent guestAuthForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "auth",
            ["login"] = "SmokeFriend",
            ["pass"] = "p2p"
        });
        HttpResponseMessage guestAuthResp = await http.PostAsync($"http://{guestAuthServer.Endpoint}/client_requester.php?f=auth", guestAuthForm);
        guestAuthResp.EnsureSuccessStatusCode();
        string guestAuthPhp = await guestAuthResp.Content.ReadAsStringAsync();
        PhpHashtable guestAuthTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(guestAuthPhp));
        Assert.Equal(true, guestAuthTable[0]);
        Assert.Equal("SmokeFriend", guestAuthTable["nickname"]);
        string guestCookie = Assert.IsType<string>(guestAuthTable["cookie"]);
        Assert.Equal(32, guestCookie.Length);

        // 2. Host's server_p2p registers session on Host's loopback responder
        using FormUrlEncodedContent newSessionForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "new_session",
            ["login"] = "p2phost:1",
            ["pass"] = "p2p",
            ["port"] = "11550"
        });
        HttpResponseMessage sessionResp = await http.PostAsync($"http://{hostAuthServer.Endpoint}/server_requester.php?f=new_session", newSessionForm);
        sessionResp.EnsureSuccessStatusCode();
        PhpHashtable sessionTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(await sessionResp.Content.ReadAsStringAsync()));
        string serverSession = Assert.IsType<string>(sessionTable["session"]);
        Assert.False(string.IsNullOrWhiteSpace(serverSession));
        Assert.Equal(1, sessionTable["server_id"]);
        Assert.Equal(hostAuthServer.GsChatPort, sessionTable["chat_port"]);

        // 2b. Verify MatchServer TCP Chat Handshake (0x0500 NET_CHAT_GS_CONNECT -> 0x1500 NET_CHAT_GS_ACCEPT) and Ping/Pong (0x2A00 -> 0x2A01)
        using TcpClient gsTcp = new TcpClient();
        await gsTcp.ConnectAsync(IPAddress.Loopback, hostAuthServer.GsChatPort);
        await using NetworkStream gsStream = gsTcp.GetStream();

        byte[] gsConnectFrame = [0x02, 0x00, 0x00, 0x05];
        await gsStream.WriteAsync(gsConnectFrame);
        await gsStream.FlushAsync();

        byte[] gsAcceptReply = new byte[4];
        int acceptRead = 0;
        while (acceptRead < 4)
        {
            int n = await gsStream.ReadAsync(gsAcceptReply.AsMemory(acceptRead, 4 - acceptRead));
            Assert.True(n > 0);
            acceptRead += n;
        }

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(gsAcceptReply.AsSpan(0, 2)));
        Assert.Equal(0x1500, BinaryPrimitives.ReadUInt16LittleEndian(gsAcceptReply.AsSpan(2, 2)));

        byte[] gsPingFrame = [0x02, 0x00, 0x00, 0x2A];
        await gsStream.WriteAsync(gsPingFrame);
        await gsStream.FlushAsync();

        byte[] gsPongReply = new byte[4];
        int pongRead = 0;
        while (pongRead < 4)
        {
            int n = await gsStream.ReadAsync(gsPongReply.AsMemory(pongRead, 4 - pongRead));
            Assert.True(n > 0);
            pongRead += n;
        }

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(gsPongReply.AsSpan(0, 2)));
        Assert.Equal(0x2A01, BinaryPrimitives.ReadUInt16LittleEndian(gsPongReply.AsSpan(2, 2)));

        // 2c. Verify patcher/patcher.php returns up-to-date manifest (latest == current) so K2_SERVER never stalls on patch check
        using FormUrlEncodedContent patchForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["os"] = "was-crIac6LASwoafrl8FrOa",
            ["arch"] = "x86_64",
            ["current_version"] = "4.10.1.0"
        });
        HttpResponseMessage patchResp = await http.PostAsync($"http://{hostAuthServer.Endpoint}/patcher/patcher.php", patchForm);
        patchResp.EnsureSuccessStatusCode();
        PhpHashtable patchTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(await patchResp.Content.ReadAsStringAsync()));
        Assert.Equal("4.10.1.0", patchTable["current_version"]);
        PhpHashtable patchDetails = Assert.IsType<PhpHashtable>(patchTable[0]);
        Assert.Equal("4.10.1", patchDetails["latest_version"]);

        // 2d. Verify client_requester.php?f=server_list returns valid S2Crypto acc_key + acc_key_hash for silent host connect
        using FormUrlEncodedContent srvListForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "server_list",
            ["cookie"] = guestCookie,
            ["gametype"] = "90",
            ["region"] = "ALL"
        });
        HttpResponseMessage srvListResp = await http.PostAsync($"http://{hostAuthServer.Endpoint}/client_requester.php?f=server_list", srvListForm);
        srvListResp.EnsureSuccessStatusCode();
        PhpHashtable srvListTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(await srvListResp.Content.ReadAsStringAsync()));
        Assert.Equal(true, srvListTable[0]);
        string accKey = Assert.IsType<string>(srvListTable["acc_key"]);
        string accKeyHash = Assert.IsType<string>(srvListTable["acc_key_hash"]);
        Assert.NotEmpty(accKey);
        Assert.Equal(Kongor.Core.Security.S2Crypto.ComputeMatchServerChatAuthenticationHash(accKey, guestCookie), accKeyHash);
        PhpHashtable srvEntries = Assert.IsType<PhpHashtable>(srvListTable["server_list"]);
        PhpHashtable srvEntry0 = Assert.IsType<PhpHashtable>(srvEntries[0]);
        Assert.Equal("11550", srvEntry0["port"]);

        // 3. Host's server_p2p validates Remote Guest's cookie via f=c_conn (zero prior state on Host!)
        using FormUrlEncodedContent cConnForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "c_conn",
            ["session"] = serverSession,
            ["cookie"] = guestCookie,
            ["ip"] = "127.0.0.1"
        });
        HttpResponseMessage cConnResp = await http.PostAsync($"http://{hostAuthServer.Endpoint}/server_requester.php?f=c_conn", cConnForm);
        cConnResp.EnsureSuccessStatusCode();
        PhpHashtable cConnTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(await cConnResp.Content.ReadAsStringAsync()));
        Assert.Equal("SmokeFriend", cConnTable["nickname"]);
        Assert.Equal(P2pCookieCodec.ComputeDeterministicAccountId("SmokeFriend"), cConnTable["account_id"]);

        // 4. Host's server_p2p creates lobby via f=start_game and queries f=aids2cookie
        using FormUrlEncodedContent startGameForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "start_game",
            ["session"] = serverSession,
            ["map"] = "caldavar"
        });
        HttpResponseMessage startGameResp = await http.PostAsync($"http://{hostAuthServer.Endpoint}/server_requester.php?f=start_game", startGameForm);
        startGameResp.EnsureSuccessStatusCode();
        PhpHashtable startGameTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(await startGameResp.Content.ReadAsStringAsync()));
        int matchId = Assert.IsType<int>(startGameTable["match_id"]);
        Assert.True(matchId >= 1000);

        int friendAid = P2pCookieCodec.ComputeDeterministicAccountId("SmokeFriend");
        using FormUrlEncodedContent aidsForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "aids2cookie",
            ["aids[0]"] = friendAid.ToString()
        });
        HttpResponseMessage aidsResp = await http.PostAsync($"http://{hostAuthServer.Endpoint}/server_requester.php?f=aids2cookie", aidsForm);
        aidsResp.EnsureSuccessStatusCode();
        PhpHashtable aidsTable = Assert.IsType<PhpHashtable>(PhpDeserializer.Deserialize(await aidsResp.Content.ReadAsStringAsync()));
        PhpHashtable nestedCookies = Assert.IsType<PhpHashtable>(aidsTable["aids2cookie"]);
        Assert.Equal(guestCookie, nestedCookies[friendAid]);
    }

    [Fact]
    public void DirectPlayViewModel_BuildHostAndGuestClientArguments_EmitsExecuteCvarScriptAndLoopbackEndpoints()
    {
        // protects: Cross-platform K2 CLI CVar injection (-execute "Set ...;") for both Host and Guest Direct Play launches.
        // fails_when: -execute script is omitted (causing retail Linux/macOS/Windows K2 to ignore +Set flags) or SRP/loopback flags drift.
        // seam: Real DirectPlayViewModel.BuildHostClientArguments & BuildGuestClientArguments CLI builders.
        List<string> hostArgs = DirectPlayViewModel.BuildHostClientArguments(
            "SmokeHost",
            11550,
            bypassMasterServer: true,
            hostConnectAddress: "127.0.0.1:11550",
            loopbackAuthEndpoint: "127.0.0.1:19999");

        string joinedHost = string.Join(" ", hostArgs);
        Assert.Contains("-masterserver 127.0.0.1:19999", joinedHost, StringComparison.Ordinal);
        Assert.Contains("-webserver 127.0.0.1:19999", joinedHost, StringComparison.Ordinal);
        Assert.Contains("-messageserver 127.0.0.1:19999", joinedHost, StringComparison.Ordinal);
        Assert.Contains("-execute \"", joinedHost, StringComparison.Ordinal);
        Assert.Contains("Set p2p_nickname SmokeHost;", joinedHost, StringComparison.Ordinal);
        Assert.Contains("Set p2p_mode true;", joinedHost, StringComparison.Ordinal);
        Assert.Contains("Set p2p_auto_host true;", joinedHost, StringComparison.Ordinal);
        Assert.Contains("Set p2p_standalone true;", joinedHost, StringComparison.Ordinal);
        Assert.Contains("Set p2p_host_addr 127.0.0.1:11550;", joinedHost, StringComparison.Ordinal);
        Assert.Contains("Set login_useSRP false;", joinedHost, StringComparison.Ordinal);

        List<string> guestArgs = DirectPlayViewModel.BuildGuestClientArguments(
            "SmokeFriend",
            "192.168.1.42:11550",
            bypassMasterServer: true,
            loopbackAuthEndpoint: "127.0.0.1:20050");

        string joinedGuest = string.Join(" ", guestArgs);
        Assert.Contains("-masterserver 127.0.0.1:20050", joinedGuest, StringComparison.Ordinal);
        Assert.Contains("-execute \"", joinedGuest, StringComparison.Ordinal);
        Assert.Contains("Set p2p_nickname SmokeFriend;", joinedGuest, StringComparison.Ordinal);
        Assert.Contains("Set p2p_auto_connect true;", joinedGuest, StringComparison.Ordinal);
        Assert.Contains("Set p2p_connect_addr 192.168.1.42:11550;", joinedGuest, StringComparison.Ordinal);
        Assert.Contains("Set login_useSRP false;", joinedGuest, StringComparison.Ordinal);
    }
}
