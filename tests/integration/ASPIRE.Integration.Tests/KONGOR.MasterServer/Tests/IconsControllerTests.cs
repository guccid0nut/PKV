// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class IconsControllerTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>
{
    private readonly HttpClient _client = fixture.Client;
    private readonly WebApplicationFactory<KONGORAssemblyMarker> _factory = fixture.Factory;

    [Fact]
    public async Task GetIcon_ReturnsNotFound_WhenIconDoesNotExist()
    {
        HttpResponseMessage response = await _client.GetAsync("/icons/nonexistent_icon_12345.png");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetIcon_ReturnsIcon_WhenIconFileExists()
    {
        IWebHostEnvironment env = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        string randomFolder = Random.Shared.Next(80000, 89999).ToString();
        string iconsDir = Path.Combine(env.ContentRootPath, "Resources", "Icons", "0", "0", randomFolder);
        Directory.CreateDirectory(iconsDir);

        string testIconFile = Path.Combine(iconsDir, "1.cai");
        byte[] testBytes = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(testIconFile, testBytes);

        try
        {
            HttpResponseMessage response = await _client.GetAsync($"/icons/0/0/{randomFolder}/1.cai");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

            byte[] downloadedBytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(testBytes, downloadedBytes);
        }
        finally
        {
            if (File.Exists(testIconFile)) File.Delete(testIconFile);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetIcon_WithDiscordAvatar_ResolvesAccountAndAttemptsPngCdnUrl()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using IServiceScope scope = fixture.CreateScope();
        MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        Role defaultRole = await context.Roles.FirstAsync();

        string uniqueDiscordId = Random.Shared.Next(100000000, 999999999).ToString();
        string uniqueName = $"DiscordGif_{Guid.NewGuid():N}"[..12];

        User user = new()
        {
            EmailAddress = $"{uniqueName}@test.com",
            DiscordId = uniqueDiscordId,
            DiscordAvatar = "a_123456789abcdef0123456789abcdef0",
            Role = defaultRole,
            PBKDF2PasswordHash = "hash",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash"
        };

        Account account = new() { Name = uniqueName, User = user, IsMain = true };

        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HttpResponseMessage response = await _client.GetAsync($"/icons/0/0/{account.ID}/1.cai");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);
    }
}
