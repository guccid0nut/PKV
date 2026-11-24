// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Web;
using ASPIRE.Common.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.ViewModels;
using ZORGATH.WebPortal.API.Features.Admin.Users;
using ContractsAccountType = NEXUS.Contracts.Domain.AccountType;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class UserManagementServiceTests
{
    private static MerrickContext CreateContext(string dbName)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static UserManagementService CreateTestService(string dbName)
    {
        MerrickContext context = CreateContext(dbName);
        Mock<ISecretsManagerService> mockSecrets = new();
        HttpClient testClient = new(new AdminUserTestHandler(context));
        return new UserManagementService(mockSecrets.Object, testClient);
    }

    private static void SeedDatabase(string dbName)
    {
        using MerrickContext context = CreateContext(dbName);

        Role adminRole = new() { ID = 1, Name = UserRoles.Administrator };
        Role userRole = new() { ID = 2, Name = UserRoles.User };
        context.Roles.AddRange(adminRole, userRole);

        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@nexus.net",
            DiscordId = "111222333444",
            DiscordUsername = "AdminHero",
            Role = adminRole,
            GoldCoins = 100,
            SilverCoins = 500,
            PlinkoTickets = 5,
            SRPPasswordSalt = "salt1",
            SRPPasswordHash = "hash1",
            PBKDF2PasswordHash = "pbkdf1"
        };

        Account adminAccount = new()
        {
            ID = 1,
            Name = "AdminAccount",
            IsMain = true,
            Type = ContractsAccountType.Staff,
            User = adminUser
        };
        adminUser.Accounts.Add(adminAccount);

        User standardUser = new()
        {
            ID = 2,
            EmailAddress = "player@nexus.net",
            DiscordId = "555666777888",
            DiscordUsername = "PlayerOne",
            Role = userRole,
            GoldCoins = 50,
            SilverCoins = 250,
            PlinkoTickets = 2,
            SRPPasswordSalt = "salt2",
            SRPPasswordHash = "hash2",
            PBKDF2PasswordHash = "pbkdf2"
        };

        Account playerMain = new()
        {
            ID = 2,
            Name = "PlayerAcc",
            IsMain = true,
            Type = ContractsAccountType.Normal,
            User = standardUser
        };
        Account playerSmurf = new()
        {
            ID = 3,
            Name = "PlayerSmurf",
            IsMain = false,
            Type = ContractsAccountType.Normal,
            User = standardUser
        };
        standardUser.Accounts.Add(playerMain);
        standardUser.Accounts.Add(playerSmurf);

        User hostUser = new()
        {
            ID = 3,
            EmailAddress = "host@nexus.net",
            DiscordId = "999888777666",
            DiscordUsername = "HostMaster",
            Role = userRole,
            SRPPasswordSalt = "salt3",
            SRPPasswordHash = "hash3",
            PBKDF2PasswordHash = "pbkdf3"
        };

        Account hostAccount = new()
        {
            ID = 4,
            Name = "HostAcc",
            IsMain = true,
            Type = ContractsAccountType.ServerHost,
            User = hostUser
        };
        hostUser.Accounts.Add(hostAccount);

        HostApplication pendingApp = new()
        {
            ID = 1,
            UserID = 2,
            User = standardUser,
            MainAccountID = 2,
            MainAccount = playerMain,
            HostUsername = "PendingHostName",
            Status = HostApplicationStatus.Pending
        };

        HostApplication approvedApp = new()
        {
            ID = 2,
            UserID = 3,
            User = hostUser,
            MainAccountID = 4,
            MainAccount = hostAccount,
            HostAccountID = 4,
            HostAccount = hostAccount,
            HostUsername = "HostAcc",
            EncryptedHostPassword = HostSecurityService.EncryptPassword("SecretHostPass123!"),
            EncryptedAuthToken = HostSecurityService.EncryptPassword("tok_secret_host_token"),
            Status = HostApplicationStatus.Approved
        };

        context.Users.AddRange(adminUser, standardUser, hostUser);
        context.Accounts.AddRange(adminAccount, playerMain, playerSmurf, hostAccount);
        context.HostApplications.AddRange(pendingApp, approvedApp);
        context.SaveChanges();
    }

    [Fact]
    public async Task UserManagementService_GetSummaryStatsAsync_ReturnsAccurateCounts()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        UserSummaryStats stats = await service.GetSummaryStatsAsync();

        Assert.True(stats.IsDatabaseConnected);
        Assert.Equal(3, stats.TotalUsers);
        Assert.Equal(4, stats.TotalAccounts);
        Assert.Equal(1, stats.TotalStaff);
        Assert.Equal(1, stats.TotalHosts);
    }

    [Fact]
    public async Task UserManagementService_SearchUsersAsync_FindsUsersByQueryAndFilter()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        List<UserDetailDto> resultsByEmail = await service.SearchUsersAsync("player@nexus", string.Empty);
        Assert.Single(resultsByEmail);
        Assert.Equal(2, resultsByEmail[0].Id);
        Assert.Equal(2, resultsByEmail[0].Accounts.Count);

        List<UserDetailDto> resultsByAccount = await service.SearchUsersAsync("PlayerSmurf", string.Empty);
        Assert.Single(resultsByAccount);
        Assert.Equal(2, resultsByAccount[0].Id);

        List<UserDetailDto> staffResults = await service.SearchUsersAsync(string.Empty, "STAFF");
        Assert.Single(staffResults);
        Assert.Equal(1, staffResults[0].Id);

        List<UserDetailDto> hostResults = await service.SearchUsersAsync(string.Empty, "HOST");
        Assert.Single(hostResults);
        Assert.Equal(3, hostResults[0].Id);
    }

    [Fact]
    public async Task UserManagementService_UpdateUserEconomyAsync_UpdatesCoinsAndTickets()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        bool success = await service.UpdateUserEconomyAsync(2, 5000, 25000, 50);
        Assert.True(success);

        UserDetailDto? updatedUser = await service.GetUserByIdAsync(2);
        Assert.NotNull(updatedUser);
        Assert.Equal(5000, updatedUser.GoldCoins);
        Assert.Equal(25000, updatedUser.SilverCoins);
        Assert.Equal(50, updatedUser.PlinkoTickets);
    }

    [Fact]
    public async Task UserManagementService_PromoteUserToStaffAsync_PromotesRoleAndLinkedAccounts()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        bool success = await service.PromoteUserToStaffAsync(2);
        Assert.True(success);

        UserDetailDto? updatedUser = await service.GetUserByIdAsync(2);
        Assert.NotNull(updatedUser);
        Assert.Equal(UserRoles.Administrator, updatedUser.RoleName);
        Assert.True(updatedUser.IsStaff);
        Assert.NotEmpty(updatedUser.Accounts);
        foreach (AccountItemDto a in updatedUser.Accounts)
            if (a.IsMain)
            {
                Assert.True(a.IsStaff);
                Assert.Equal(ContractsAccountType.Staff, a.Type);
            }
    }

    [Fact]
    public async Task UserManagementService_PromoteUserToHostAsync_PromotesAccountsAndApprovesHostApp()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        bool success = await service.PromoteUserToHostAsync(2);
        Assert.True(success);

        UserDetailDto? updatedUser = await service.GetUserByIdAsync(2);
        Assert.NotNull(updatedUser);
        Assert.NotEmpty(updatedUser.Accounts);
        AccountItemDto? mainAcc = updatedUser.Accounts.FirstOrDefault(a => a.IsMain);
        Assert.NotNull(mainAcc);
        Assert.Equal(ContractsAccountType.ServerHost, mainAcc.Type);
    }

    [Fact]
    public async Task UserManagementService_RevertUserToStandardAsync_ResetsRoleAndAccounts()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        bool success = await service.RevertUserToStandardAsync(1);
        Assert.True(success);

        UserDetailDto? updatedUser = await service.GetUserByIdAsync(1);
        Assert.NotNull(updatedUser);
        Assert.Equal(UserRoles.User, updatedUser.RoleName);
        foreach (AccountItemDto a in updatedUser.Accounts) Assert.Equal(ContractsAccountType.Normal, a.Type);
    }

    [Fact]
    public async Task UserManagementService_ResetHostPasswordAsync_GeneratesSecurePassword()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        string? rawPassword = await service.ResetHostPasswordAsync(4);
        Assert.NotNull(rawPassword);
        Assert.Equal(16, rawPassword.Length);
    }

    [Fact]
    public async Task UserManagementService_ResetHostAuthTokenAsync_GeneratesSecureAuthToken()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        string? token = await service.ResetHostAuthTokenAsync(4);
        Assert.NotNull(token);
        Assert.StartsWith("tok_", token);
    }

    [Fact]
    public async Task UserManagementService_GetUserByIdAsync_PopulatesAndDecryptsHostPassword()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        UserDetailDto? user = await service.GetUserByIdAsync(3);
        Assert.NotNull(user);

        AccountItemDto? hostAcc = user.Accounts.FirstOrDefault(a => a.IsHost);
        Assert.NotNull(hostAcc);
        Assert.Equal("SecretHostPass123!", hostAcc.HostPassword);
        Assert.Equal("tok_secret_host_token", hostAcc.AuthToken);
        Assert.Equal("••••••••••••••••", hostAcc.HostPasswordDisplay);

        // Toggle reveal
        hostAcc.IsPasswordRevealed = true;
        Assert.Equal("SecretHostPass123!", hostAcc.HostPasswordDisplay);
    }

    [Fact]
    public async Task UserManagementService_ApprovedHostWithoutPassword_AutoHealsAndPersistsPassword()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);

        // Clear the password in database directly to simulate legacy uninitialized host
        using (MerrickContext db = CreateContext(dbName))
        {
            HostApplication app = db.HostApplications.First(h => h.ID == 2);
            app.EncryptedHostPassword = null;
            db.SaveChanges();
        }

        UserManagementService service = CreateTestService(dbName);
        UserDetailDto? user = await service.GetUserByIdAsync(3);
        Assert.NotNull(user);

        AccountItemDto? hostAcc = user.Accounts.FirstOrDefault(a => a.IsHost);
        Assert.NotNull(hostAcc);
        Assert.False(string.IsNullOrWhiteSpace(hostAcc.HostPassword));
        Assert.NotEqual("None Generated", hostAcc.HostPasswordDisplay);
        Assert.Equal("••••••••••••••••", hostAcc.HostPasswordDisplay);

        // Verify it was persisted to the database
        using (MerrickContext db = CreateContext(dbName))
        {
            HostApplication app = db.HostApplications.First(h => h.ID == 2);
            Assert.NotNull(app.EncryptedHostPassword);
            string decrypted = HostSecurityService.DecryptPassword(app.EncryptedHostPassword);
            Assert.Equal(hostAcc.HostPassword, decrypted);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void UsersViewModel_CurrencyPropertyChange_DetectsUnsavedChanges()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);
        UsersViewModel viewModel = new(service);

        UserDetailDto user = new() { Id = 1, GoldCoins = 100, SilverCoins = 200, PlinkoTickets = 5 };
        viewModel.SelectedUser = user;

        Assert.False(viewModel.HasCurrencyChanges);

        viewModel.EditableGoldCoins = 500;
        Assert.True(viewModel.HasCurrencyChanges);

        viewModel.EditableGoldCoins = viewModel.SelectedUser.GoldCoins;
        Assert.Equal(100, viewModel.EditableGoldCoins);
        Assert.False(viewModel.HasCurrencyChanges);
    }

    [Fact]
    public async Task UserManagementService_UserDetailDto_CategorizesMainHostAndSubAccountsCorrectly()
    {
        string dbName = Guid.NewGuid().ToString();
        SeedDatabase(dbName);
        UserManagementService service = CreateTestService(dbName);

        UserDetailDto? standardUser = await service.GetUserByIdAsync(2);
        Assert.NotNull(standardUser);
        Assert.Equal("@PlayerOne", standardUser.DisplayName);
        Assert.NotNull(standardUser.MainAccount);
        Assert.Equal("PlayerAcc", standardUser.MainAccount.Name);
        Assert.Single(standardUser.SubAccounts);
        Assert.Equal("PlayerSmurf", standardUser.SubAccounts[0].Name);
        Assert.False(standardUser.HasHostAccounts);

        UserDetailDto? hostUser = await service.GetUserByIdAsync(3);
        Assert.NotNull(hostUser);
        Assert.True(hostUser.HasHostAccounts);
        Assert.Single(hostUser.HostAccounts);
        Assert.Equal("HostAcc", hostUser.HostAccounts[0].Name);
    }

    [Fact]
    public async Task UserManagementService_DedicatedHostAccounts_MappedToMasterAccountCorrectly()
    {
        string dbName = Guid.NewGuid().ToString();
        using (MerrickContext setupContext = CreateContext(dbName))
        {
            Role userRole = new() { ID = 1, Name = UserRoles.User };
            setupContext.Roles.Add(userRole);

            User player = new()
            {
                ID = 10,
                EmailAddress = "gucci@nexus.local",
                DiscordUsername = "guccid0nut",
                Role = userRole,
                SRPPasswordSalt = "s1",
                SRPPasswordHash = "h1",
                PBKDF2PasswordHash = "p1"
            };

            Account playerAccount = new()
            {
                ID = 100,
                Name = "guccidonut",
                IsMain = true,
                Type = ContractsAccountType.Normal,
                User = player
            };
            player.Accounts.Add(playerAccount);

            User dedicatedHostUser = new()
            {
                ID = 11,
                EmailAddress = "host_guccihost@nexus.local",
                DiscordUsername = "HostSystem",
                Role = userRole,
                SRPPasswordSalt = "s2",
                SRPPasswordHash = "h2",
                PBKDF2PasswordHash = "p2"
            };

            Account hostAccount = new()
            {
                ID = 101,
                Name = "guccihost",
                IsMain = false,
                Type = ContractsAccountType.ServerHost,
                OwnerAccountID = 100,
                User = dedicatedHostUser
            };
            dedicatedHostUser.Accounts.Add(hostAccount);

            setupContext.Users.AddRange(player, dedicatedHostUser);
            setupContext.Accounts.AddRange(playerAccount, hostAccount);
            await setupContext.SaveChangesAsync();
        }

        UserManagementService service = CreateTestService(dbName);

        List<UserDetailDto> users = await service.SearchUsersAsync(string.Empty, string.Empty);
        // Dedicated host user (host_guccihost@nexus.local) should be filtered out from master user list
        Assert.Single(users);
        UserDetailDto playerDto = users[0];
        Assert.Equal(10, playerDto.Id);
        Assert.Equal("@guccid0nut", playerDto.DisplayName);

        // Player's profile should have their main account AND their owned server host account
        Assert.NotNull(playerDto.MainAccount);
        Assert.Equal("guccidonut", playerDto.MainAccount.Name);
        Assert.True(playerDto.HasHostAccounts);
        Assert.Single(playerDto.HostAccounts);
        Assert.Equal("guccihost", playerDto.HostAccounts[0].Name);
        Assert.Equal(ContractsAccountType.ServerHost, playerDto.HostAccounts[0].Type);
    }

    private sealed class AdminUserTestHandler(MerrickContext dbContext) : HttpMessageHandler
    {
        private readonly AdminUserController _controller = new(new LocalAdminUserMediator(dbContext), NullLogger<AdminUserController>.Instance);

        private readonly AdminFleetController _fleetController =
            new(new AccountRepository(dbContext), NullLogger<AdminFleetController>.Instance);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string query = request.RequestUri?.Query ?? string.Empty;

            if (path.EndsWith("/api/admin/users/stats", StringComparison.OrdinalIgnoreCase))
            {
                IActionResult result = await _controller.GetSummaryStats(cancellationToken);
                return ToHttpResponse(result);
            }

            if (path.Equals("/api/admin/users", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Get)
            {
                NameValueCollection queryParams = HttpUtility.ParseQueryString(query);
                string? q = queryParams["query"];
                string? r = queryParams["roleFilter"];
                IActionResult result = await _controller.SearchUsers(q, r, cancellationToken);
                return ToHttpResponse(result);
            }

            if (path.StartsWith("/api/admin/users/", StringComparison.OrdinalIgnoreCase) &&
                request.Method == HttpMethod.Get)
            {
                string idStr = path["/api/admin/users/".Length..];
                if (int.TryParse(idStr, out int id))
                {
                    IActionResult result = await _controller.GetUserById(id, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.Contains("/role") && request.Method == HttpMethod.Post)
            {
                if (path.StartsWith("/api/admin/users/"))
                {
                    string idStr = path["/api/admin/users/".Length..].Replace("/role", "");
                    if (int.TryParse(idStr, out int id))
                    {
                        SetUserRoleRequest? req =
                            await request.Content!.ReadFromJsonAsync<SetUserRoleRequest>(cancellationToken);
                        IActionResult result = await _controller.SetUserRole(id, req!, cancellationToken);
                        return ToHttpResponse(result);
                    }
                }
                else if (path.StartsWith("/api/admin/accounts/"))
                {
                    string idStr = path["/api/admin/accounts/".Length..].Replace("/role", "");
                    if (int.TryParse(idStr, out int id))
                    {
                        SetAccountRoleRequest? req =
                            await request.Content!.ReadFromJsonAsync<SetAccountRoleRequest>(cancellationToken);
                        IActionResult result = await _controller.SetAccountRole(id, req!, cancellationToken);
                        return ToHttpResponse(result);
                    }
                }
            }

            if (path.EndsWith("/reset-password", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/accounts/".Length..].Replace("/reset-password", "");
                if (int.TryParse(idStr, out int id))
                {
                    IActionResult result = await _controller.ResetHostPassword(id, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/reset-token", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/accounts/".Length..].Replace("/reset-token", "");
                if (int.TryParse(idStr, out int id))
                {
                    IActionResult result = await _controller.ResetHostAuthToken(id, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/economy", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/users/".Length..].Replace("/economy", "");
                if (int.TryParse(idStr, out int id))
                {
                    UpdateUserEconomyRequest? req =
                        await request.Content!.ReadFromJsonAsync<UpdateUserEconomyRequest>(cancellationToken);
                    IActionResult result = await _controller.UpdateUserEconomy(id, req!, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/adjust-balance", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/users/".Length..].Replace("/adjust-balance", "");
                if (int.TryParse(idStr, out int id))
                {
                    AdjustUserBalanceRequest? req =
                        await request.Content!.ReadFromJsonAsync<AdjustUserBalanceRequest>(cancellationToken);
                    IActionResult result = await _controller.AdjustUserBalance(id, req!, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/promote-staff", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/users/".Length..].Replace("/promote-staff", "");
                if (int.TryParse(idStr, out int id))
                {
                    IActionResult result = await _controller.PromoteUserToStaff(id, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/promote-host", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/users/".Length..].Replace("/promote-host", "");
                if (int.TryParse(idStr, out int id))
                {
                    IActionResult result = await _controller.PromoteUserToHost(id, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/revert-standard", StringComparison.OrdinalIgnoreCase))
            {
                string idStr = path["/api/admin/users/".Length..].Replace("/revert-standard", "");
                if (int.TryParse(idStr, out int id))
                {
                    IActionResult result = await _controller.RevertUserToStandard(id, cancellationToken);
                    return ToHttpResponse(result);
                }
            }

            if (path.EndsWith("/api/admin/fleet/hosts", StringComparison.OrdinalIgnoreCase))
            {
                IActionResult result = await _fleetController.GetApprovedHosts(cancellationToken);
                return ToHttpResponse(result);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage ToHttpResponse(IActionResult actionResult)
        {
            if (actionResult is OkObjectResult okObj)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(okObj.Value)
                };
            if (actionResult is OkResult) return new HttpResponseMessage(HttpStatusCode.OK);
            if (actionResult is NotFoundObjectResult or NotFoundResult)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (actionResult is ObjectResult objRes)
                return new HttpResponseMessage((HttpStatusCode)(objRes.StatusCode ?? 200))
                {
                    Content = JsonContent.Create(objRes.Value)
                };
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
