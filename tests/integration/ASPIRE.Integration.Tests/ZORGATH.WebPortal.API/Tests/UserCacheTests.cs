// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class UserCacheTests(ZORGATHTestFixture fixture) : IClassFixture<ZORGATHTestFixture>

{
    [Fact]
    public async Task GetUser_ReturnsCachedResponse_WhenCalledMultipleTimes()

    {
        // 1. Setup

        WebApplicationFactory<IZorgathAssemblyMarker> factory = fixture.Factory;

        JWTAuthenticationService authService = new(factory);


        string uniqueEmail = $"cache_{Guid.NewGuid():N}@test.com";

        string uniqueAccount = $"Cache_{Guid.NewGuid():N}"[..12];


        // Create a user

        JWTAuthenticationData authData =
            await authService.CreateAuthenticatedUser(uniqueEmail, uniqueAccount, "Password123!");


        using HttpClient client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authData.AuthenticationToken);


        // 2. First Request (Prime Cache)

        HttpResponseMessage response1 = await client.GetAsync($"/User/{authData.UserID}");

        response1.EnsureSuccessStatusCode();

        GetBasicUserDTO? result1 = await response1.Content.ReadFromJsonAsync<GetBasicUserDTO>();


        // 3. Modify Data in DB Directly

        using (IServiceScope scope = fixture.CreateScope())

        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            Account account = await context.Accounts.SingleAsync(a => a.Name == uniqueAccount);

            account.Name = $"{uniqueAccount}_Mod";

            await context.SaveChangesAsync();
        }


        // 4. Second Request (Should be Cached)

        HttpResponseMessage response2 = await client.GetAsync($"/User/{authData.UserID}");

        response2.EnsureSuccessStatusCode();

        GetBasicUserDTO? result2 = await response2.Content.ReadFromJsonAsync<GetBasicUserDTO>();


        // 5. Assert

        Assert.NotNull(result1);

        Assert.NotNull(result2);


        // The first result should have the original name

        Assert.Equal(uniqueAccount, result1.Accounts.First().Name);


        // The second result should ALSO have the original name because it was served from cache

        Assert.Equal(uniqueAccount, result2.Accounts.First().Name);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetUser_VariesByAuthorization_WhenDifferentUsersRequestSameResource()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // 1. Setup

        WebApplicationFactory<IZorgathAssemblyMarker> factory = fixture.Factory;

        JWTAuthenticationService authService = new(factory);


        string uniqueEmailA = $"userA_{Guid.NewGuid():N}@test.com";

        string uniqueAccountA = $"UserA_{Guid.NewGuid():N}"[..12];

        string uniqueEmailB = $"userB_{Guid.NewGuid():N}@test.com";

        string uniqueAccountB = $"UserB_{Guid.NewGuid():N}"[..12];


        // User A

        JWTAuthenticationData authDataA =
            await authService.CreateAuthenticatedUser(uniqueEmailA, uniqueAccountA, "Password123!");

        using HttpClient clientA = factory.CreateClient();

        clientA.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authDataA.AuthenticationToken);


        // User B

        JWTAuthenticationData authDataB =
            await authService.CreateAuthenticatedUser(uniqueEmailB, uniqueAccountB, "Password123!");

        using HttpClient clientB = fixture.Client;

        clientB.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authDataB.AuthenticationToken);


        // 2. User A requests User A (Prime Cache for A)

        HttpResponseMessage responseA1 = await clientA.GetAsync($"/User/{authDataA.UserID}");

        responseA1.EnsureSuccessStatusCode();

        GetBasicUserDTO? resultA1 = await responseA1.Content.ReadFromJsonAsync<GetBasicUserDTO>();


        // 3. Modify User A in DB

        using (IServiceScope scope = fixture.CreateScope())

        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            Account accountA = await context.Accounts.SingleAsync(a => a.Name == uniqueAccountA);

            accountA.Name = $"{uniqueAccountA}_Mod";

            await context.SaveChangesAsync();
        }


        // 4. User B requests User A

        HttpResponseMessage responseB = await clientB.GetAsync($"/User/{authDataA.UserID}");

        responseB.EnsureSuccessStatusCode();

        GetBasicUserDTO? resultB = await responseB.Content.ReadFromJsonAsync<GetBasicUserDTO>();


        // 5. Assert

        Assert.NotNull(resultA1);

        Assert.NotNull(resultB);


        // User A saw original data

        Assert.Equal(uniqueAccountA, resultA1.Accounts.First().Name);


        // User B should see modified data because they got a fresh response

        Assert.Equal($"{uniqueAccountA}_Mod", resultB.Accounts.First().Name);
    }
}
