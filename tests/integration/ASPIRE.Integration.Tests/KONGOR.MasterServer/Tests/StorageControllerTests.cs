// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http.Headers;
using ASPIRE.Tests.Data;
using KONGOR.MasterServer.Extensions.Cache;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StorageControllerTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie,
            WebApplicationFactory<KONGORAssemblyMarker> Factory, string StoragePath)> SetupAsync(
            string? accountName = null)

    {
        string testId = Guid.NewGuid().ToString("N");

        string relativeStoragePath = $"App_Data/TestCloud/{testId}";


        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;


        HttpClient client = factory.CreateClient();

        IServiceScope scope = factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string name = accountName ?? $"TestUser_{Guid.NewGuid().ToString("N")[..8]}";


        User user = new()

        {
            EmailAddress = $"{name}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new Role { Name = UserRoles.User }
        };


        Account account = new() { Name = name, User = user, Type = AccountType.Staff, IsMain = true };


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        return (client, dbContext, cache, cookie, factory, relativeStoragePath);
    }


    [Fact]
    public async Task StorageController_Store_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, WebApplicationFactory<KONGORAssemblyMarker> factory,
            string storagePath) = await SetupAsync("StoreUser");


        try

        {
            MultipartFormDataContent content = new()
            {
                { new StringContent(cookie), "cookie" },
                { new StringContent("hon-game-configs"), "bucket" },
                { new StringContent("2024-01-01 12:00:00"), "file_modify_time" }
            };


            ByteArrayContent fileContent = new([0, 1, 2, 3]);

            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

            content.Add(fileContent, "cloud.zip", "cloud.zip");


            HttpResponseMessage response = await client.PostAsync("/master/storage/store", content);


            response.EnsureSuccessStatusCode();

            string responseString = await response.Content.ReadAsStringAsync();


            // Assert using Regex to avoid deserialization issues with custom attributes

            Assert.Contains("success", responseString);

            Assert.Contains("b:1", responseString); // Boolean true in PHP
        }

        finally

        {
            // Cleanup

            IWebHostEnvironment env = factory.Services.GetRequiredService<IWebHostEnvironment>();

            string fullPath = Path.Combine(env.ContentRootPath, storagePath);

            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
        }
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task StorageController_Retrieve_ReturnsFile()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (HttpClient client, _, _, string cookie, WebApplicationFactory<KONGORAssemblyMarker> factory,
            string storagePath) = await SetupAsync("RetrieveUser");


        try

        {
            // First Store a file

            MultipartFormDataContent storeContent = new()
            {
                { new StringContent(cookie), "cookie" },
                { new StringContent("hon-game-configs"), "bucket" },
                { new StringContent("2024-01-01 12:00:00"), "file_modify_time" }
            };


            byte[] fileBytes = [0, 1, 2, 3, 4, 5];

            ByteArrayContent fileContent = new(fileBytes);

            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

            storeContent.Add(fileContent, "cloud.zip", "cloud.zip");


            HttpResponseMessage storeResponse = await client.PostAsync("/master/storage/store", storeContent);

            storeResponse.EnsureSuccessStatusCode();


            // Act

            MultipartFormDataContent retrieveContent = new()
            {
                { new StringContent(cookie), "cookie" },
                { new StringContent("hon-game-configs"), "bucket" }
            };


            HttpResponseMessage response = await client.PostAsync("/master/storage/retrieve", retrieveContent);


            response.EnsureSuccessStatusCode();

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();


            Assert.NotNull(bytes);

            Assert.Equal(fileBytes.Length, bytes.Length);

            Assert.Equal(fileBytes, bytes);
        }

        finally

        {
            // Cleanup

            IWebHostEnvironment env = factory.Services.GetRequiredService<IWebHostEnvironment>();

            string fullPath = Path.Combine(env.ContentRootPath, storagePath);

            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
        }
    }


    [Fact]
    public async Task StorageController_StorageStatus_ReturnsCloudStorageInformation()

    {
        (HttpClient client, _, _, string cookie, _, _) =
            await SetupAsync("StatusUser");


        Dictionary<string, string> payload = MiscRequestPayloads.Unverified.StorageStatus(cookie);

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("/master/storage/status", content);


        response.EnsureSuccessStatusCode();

        string responseString = await response.Content.ReadAsStringAsync();


        Assert.Contains("success", responseString);
    }

    [Fact]
    public async Task StorageController_GetUserEnrollment_ReturnsEnrollmentInformation()
    {
        (HttpClient client, _, _, string cookie, _, _) =
            await SetupAsync("EnrollmentUser");

        Dictionary<string, string> payload = new()
        {
            { "cookie", cookie }
        };
        FormUrlEncodedContent content = new(payload);

        HttpResponseMessage response = await client.PostAsync("/master/cloud/get-user-enrollment", content);

        response.EnsureSuccessStatusCode();
        string responseString = await response.Content.ReadAsStringAsync();
        Assert.Contains("data", responseString);
        Assert.Contains("account_id", responseString);
    }
}
