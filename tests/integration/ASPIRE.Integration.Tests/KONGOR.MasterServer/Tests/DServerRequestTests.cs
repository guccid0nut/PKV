// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Tests.Data;
using KONGOR.MasterServer.Extensions.Cache;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;
using ServerManagementStatus = ASPIRE.Common.Models.ServerManagement.ServerStatus;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class DServerRequestTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie, Account Account,
            WebApplicationFactory<KONGORAssemblyMarker> Factory)> SetupAsync(string? accountName = null,
            ServerManagementStatus initialStatus = ServerManagementStatus.SERVER_STATUS_UNKNOWN)

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();

        IServiceScope scope = factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string name = accountName ?? $"ServerTest_{Guid.NewGuid().ToString("N")[..8]}";


        User user = new()

        {
            EmailAddress = $"{name}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new EntityRole { Name = UserRoles.Administrator } // Admin/Staff often needed for server ops
        };


        Account account = new()

        {
            Name = name,

            User = user,

            Type = AccountType.ServerHost, // Distinctive for server ops

            IsMain = true
        };


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        // Ensure session cookie map

        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        // Pre-seed MatchServer just in case needed (often tied to account name)

        MatchServer matchServer = new()

        {
            HostAccountID = user.ID,

            HostAccountName = account.Name,

            ID = 1,

            Name = "Test Server",

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "US",

            Description = "Test Server Description",

            Cookie = cookie,

            Status = initialStatus
        };

        await cache.SetMatchServer(matchServer.HostAccountName, matchServer);


        return (client, dbContext, cache, cookie, account, factory);
    }


    #region From ServerRequesterUnverifiedTests.cs

    [Fact]
    public async Task ReplayAuth_Unverified_Returns401()

    {
        (HttpClient client, _, _, _, Account account, _) =
            await SetupAsync();

        {
            // Unverified payload with bad password -> 401

            Dictionary<string, string> payload = ServerRequestPayloads.Verified.ReplayAuth(account.Name, "password");

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);


            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }


    [Fact]
    public async Task NewSession_Unverified_Returns401()

    {
        (HttpClient client, _, _, _, Account account, _) =
            await SetupAsync();

        {
            string login = $"{account.Name}:1";

            Dictionary<string, string> payload = ServerRequestPayloads.Verified.NewSession(
                login,
                "password",
                11235,
                "New Server",
                "Description",
                "US",
                "127.0.0.1"
            );

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);


            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }


    [Fact]
    public async Task Aids2Cookie_Unverified_Returns401()

    {
        (HttpClient client, _, _, _, _, _) = await SetupAsync();

        {
            Dictionary<string, string> payload =
                ServerRequestPayloads.Unverified.Aids2Cookie("123", "127.0.0.1", "bad_hash");

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);


            // Stubbed -> OK

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("auth_failure", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unregistered_session_cookie_12345", body, StringComparison.Ordinal);
        }
    }

    #endregion


    #region From ServerRequesterVerifiedPayloadTests.cs

    [Fact]
    public async Task SetOnline_Verified_UpdatesStatus()

    {
        (HttpClient client, _, IDatabase cache, string cookie, _, _) =
            await SetupAsync("ServerHost");

        {
            // Simulate 'LOADING' status update

            Dictionary<string, string> payload = ServerRequestPayloads.Verified.SetOnline(
                cookie,
                1,
                num_conn: 5,
                cgt: 123456,
                c_state: "SERVER_STATUS_LOADING",
                prev_c_state: "SERVER_STATUS_UNKNOWN"
            );


            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();


            Assert.Empty(responseBody);


            MatchServer? cachedServer = await cache.GetMatchServerBySessionCookie(cookie);

            Assert.NotNull(cachedServer);

            Assert.Equal(ServerManagementStatus.SERVER_STATUS_LOADING, cachedServer.Status);
        }
    }


    [Fact]
    public async Task Heartbeat_Verified_UpdatesLastSeen()

    {
        // Start as LOADING

        (HttpClient client, _, IDatabase cache, string cookie, _, _) =
            await SetupAsync("HeartbeatHost", ServerManagementStatus.SERVER_STATUS_LOADING);

        {
            // Heartbeat sets status to ACTIVE by default (via set_online logic)

            Dictionary<string, string> payload = ServerRequestPayloads.Verified.Heartbeat(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();


            Assert.Empty(responseBody);


            MatchServer? updatedServer = await cache.GetMatchServerBySessionCookie(cookie);

            Assert.NotNull(updatedServer);

            Assert.Equal(ServerManagementStatus.SERVER_STATUS_ACTIVE, updatedServer.Status);
        }
    }


    [Fact]
    public async Task GetSpectatorHeader_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, _, _, _) = await SetupAsync();

        {
            Dictionary<string, string> payload = ServerRequestPayloads.Verified.GetSpectatorHeader();

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("target_url", responseBody);
        }
    }


    [Fact]
    public async Task ClientConnection_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, Account account, _) =
            await SetupAsync("ConnUser");

        {
            Dictionary<string, string> payload =
                ServerRequestPayloads.Verified.ClientConnection(cookie, "127.0.0.1", account.ID);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("nickname", responseBody);

            Assert.Contains(account.Name, responseBody);

            Assert.Contains("account_id", responseBody);
        }
    }


    [Fact]
    public async Task AcceptKey_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, Account account, _) =
            await SetupAsync("AcceptUser");

        {
            Dictionary<string, string> payload = ServerRequestPayloads.Verified.AcceptKey(cookie, account.ID);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            // Expecting serialized response with server_id

            Assert.Contains("server_id", responseBody);
            Assert.Contains("official", responseBody);
        }
    }


    [Fact]
    public async Task GetQuickStats_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, IDatabase cache, string cookie, Account account, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ServerRequestPayloads.Verified.GetQuickStats(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            // Expecting empty serialized array a:0:{}

            Assert.Contains("a:0:{}", responseBody);
        }
    }


    [Fact]
    public async Task Shutdown_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, IDatabase cache, string cookie, Account account, _) =
            await SetupAsync("ShutdownHost");

        {
            Dictionary<string, string> payload = ServerRequestPayloads.Verified.Shutdown(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();
        }
    }


    [Fact]
    public async Task StartGame_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, Account account, _) =
            await SetupAsync("StartGameHost", ServerManagementStatus.SERVER_STATUS_IDLE);

        {
            Dictionary<string, string> payload = ServerRequestPayloads.Verified.StartGame(cookie, 12345);

            // Payload mstr default needs to match seeded host name

            payload["mstr"] = account.Name + ":";


            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("match_id", responseBody);
        }
    }


    [Fact]
    public async Task Aids2Cookie_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, IDatabase cache, string cookie, Account account, _) =
            await SetupAsync("AidsUser");

        {
            // Aids2Cookie usually validates the cookie exists. 

            // Our SetupAsync ensures cookie maps to account name in cache.

            await cache.StringSetAsync($"ACCOUNT-ID-SESSION-COOKIE:{account.ID}", cookie, TimeSpan.FromHours(1));
            Dictionary<string, string> payload = ServerRequestPayloads.Verified.Aids2Cookie(cookie, account.ID);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);


            string aidsResponseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("aids2cookie", aidsResponseBody);
            Assert.Contains(cookie, aidsResponseBody);

            response.EnsureSuccessStatusCode();
        }
    }


    [Fact]
    public async Task SetReplaySize_MissingSession_ReturnsBadRequest()

    {
        (HttpClient client, _, _, _, _, _) = await SetupAsync();

        {
            Dictionary<string, string> payload = new()

            {
                ["f"] = "set_replay_size",
                ["match_id"] = "12345",
                ["file_size"] = "1048576"
            };

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task SetReplaySize_MissingMatchId_ReturnsBadRequest()

    {
        (HttpClient client, _, IDatabase cache, string cookie, Account account, _) =
            await SetupAsync("ReplaySizeUser");

        {
            Dictionary<string, string> payload = new()

            {
                ["f"] = "set_replay_size",
                ["session"] = cookie,
                ["file_size"] = "1048576"
            };

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task SetReplaySize_ValidPayload_ReturnsSuccess()

    {
        (HttpClient client, _, IDatabase cache, string cookie, Account account, _) =
            await SetupAsync("ReplaySizeSuccessUser");

        {
            Dictionary<string, string> payload = new()

            {
                ["f"] = "set_replay_size",
                ["session"] = cookie,
                ["match_id"] = "12345",
                ["file_size"] = "1048576"
            };

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("server_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("set_replay_size", responseBody);

            Assert.Contains("OK", responseBody);
        }
    }

    #endregion
}
