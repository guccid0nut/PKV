// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions.Cache;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MiscVerifiedPayloadTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private readonly HttpClient _client = fixture.Client;

    private readonly WebApplicationFactory<KONGORAssemblyMarker> _factory = fixture.Factory;


    [Trait("Category", "Manual")]
    [Fact]
    public async Task MiscVerifiedPayload_GetSwaggerJson_ReturnsSuccess()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        HttpResponseMessage response = await _client.GetAsync("swagger/v1/swagger.json");

        response.EnsureSuccessStatusCode();


        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("openapi", responseBody);
    }


    [Fact]
    public async Task MiscVerifiedPayload_MessageList_ReturnsSuccess()

    {
        HttpResponseMessage response = await _client.PostAsync("message/list/59", null);

        response.EnsureSuccessStatusCode();


        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("a:0:{}", responseBody);
    }


    [Fact]
    public async Task MiscVerifiedPayload_StorageStatus_ReturnsSuccess()

    {
        using IServiceScope scope = fixture.CreateScope();

        IDatabase distributedCache = scope.ServiceProvider.GetRequiredService<IDatabase>();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        string cookie = Guid.NewGuid().ToString("N");

        string accountName = $"StorageUser_{Guid.NewGuid():N}"[..14];


        User user = new()

        {
            EmailAddress = $"{accountName}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new Role { Name = UserRoles.User }
        };


        Account account = new()

        {
            Name = accountName,

            User = user,

            Type = AccountType.Staff,

            IsMain = true,

            UseCloud = true,

            AutomaticCloudUpload = true,

            BackupLastUpdatedTime = DateTimeOffset.Parse("2023-01-01 12:00:00")
        };


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        await distributedCache.SetAccountNameForSessionCookie(cookie, accountName);


        Dictionary<string, string> payload = new() { ["cookie"] = cookie };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await _client.PostAsync("master/storage/status", content);

        response.EnsureSuccessStatusCode();


        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("cloud_storage_info", responseBody);

        Assert.Contains("success", responseBody);


        Assert.Contains("s:9:\"use_cloud\";s:1:\"1\";", responseBody);

        Assert.Contains("s:16:\"cloud_autoupload\";s:1:\"1\";", responseBody);

        Assert.Contains("2023-01-01 12:00:00", responseBody);
    }
}
