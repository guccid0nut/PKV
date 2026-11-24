// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Extensions.Cache;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class CookieTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>
{
    [Fact]
    public async Task Logout_RemovesSession_ReturnsTrue()
    {
        HttpClient httpClient = fixture.Client;

        using IServiceScope scope = fixture.CreateScope();
        IDatabase distributedCache = scope.ServiceProvider.GetRequiredService<IDatabase>();

        // 1. Set Cookie
        string cookie = Guid.NewGuid().ToString("N");
        await distributedCache.SetAccountNameForSessionCookie(cookie, "LogoutUser");

        // 2. Request
        Dictionary<string, string> formData = new() { ["f"] = "logout", ["cookie"] = cookie };
        FormUrlEncodedContent content = new(formData);

        // 3. Act
        HttpResponseMessage response = await httpClient.PostAsync("client_requester.php", content);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("b:1;", responseBody);

        // Verify cookie is gone
        string? accountName = await distributedCache.GetAccountNameForSessionCookie(cookie);
        Assert.Null(accountName);
    }

    [Fact]
    public async Task Aids2Cookie_OnServerRequester_RequiresRegisteredMatchServerSession()
    {
        HttpClient httpClient = fixture.Client;

        using IServiceScope scope = fixture.CreateScope();
        IDatabase distributedCache = scope.ServiceProvider.GetRequiredService<IDatabase>();

        // 1. Missing 'session' -> 400 BadRequest
        FormUrlEncodedContent missingSessionContent = new(new Dictionary<string, string>
        {
            ["cookie"] = Guid.NewGuid().ToString("N")
        });
        HttpResponseMessage missingResponse = await httpClient.PostAsync("server_requester.php?f=aids2cookie", missingSessionContent);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);

        // 2. Unregistered 'session' -> 401 Unauthorized (plaintext, no "auth_failure", no raw session token echo)
        string unregisteredSession = $"unreg_{Guid.NewGuid():N}";
        FormUrlEncodedContent unregisteredContent = new(new Dictionary<string, string>
        {
            ["session"] = unregisteredSession,
            ["aids[0]"] = "42"
        });
        HttpResponseMessage unregisteredResponse = await httpClient.PostAsync("server_requester.php?f=aids2cookie", unregisteredContent);
        string unregisteredBody = await unregisteredResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, unregisteredResponse.StatusCode);
        Assert.DoesNotContain("auth_failure", unregisteredBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unregisteredSession, unregisteredBody, StringComparison.Ordinal);

        // 3. Registered MatchServer 'session' -> 200 OK with aids2cookie / aids2gcookie
        string registeredSession = Guid.NewGuid().ToString("N");
        MatchServer matchServer = new()
        {
            HostAccountID = 9001,
            HostAccountName = $"CookieTestHost_{Guid.NewGuid().ToString("N")[..6]}",
            ID = Random.Shared.Next(10000, 99999),
            Name = "CookieTestServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "US",
            Description = "CookieTest",
            Cookie = registeredSession,
            Status = ServerStatus.SERVER_STATUS_IDLE
        };
        await distributedCache.SetMatchServer(matchServer.HostAccountName, matchServer);

        FormUrlEncodedContent validContent = new(new Dictionary<string, string>
        {
            ["session"] = registeredSession
        });
        HttpResponseMessage validResponse = await httpClient.PostAsync("server_requester.php?f=aids2cookie", validContent);
        string validBody = await validResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Contains("aids2cookie", validBody);
        Assert.Contains("aids2gcookie", validBody);
    }
}
