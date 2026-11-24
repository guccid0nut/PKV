// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Admin.Users;
using Xunit;
using ZORGATH.WebPortal.API.Features.Admin.Users;
using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class AdminUserMediatRTests
{
    [Fact]
    public async Task GetAdminUserSummaryHandler_CalculatesCounts_ReturnsSummary()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Role adminRole = new() { ID = 1, Name = UserRoles.Administrator };
        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.net",
            Role = adminRole,
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account staffAccount = new() { ID = 1, Name = "AdminAccount", Type = AccountType.Staff, User = adminUser };
        adminUser.Accounts.Add(staffAccount);

        User standardUser = new()
        {
            ID = 2,
            EmailAddress = "player@kongor.net",
            Role = new Role { ID = 2, Name = UserRoles.User },
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account normalAccount = new() { ID = 2, Name = "NormalPlayer", Type = AccountType.Normal, User = standardUser };
        standardUser.Accounts.Add(normalAccount);

        Account hostAccount = new() { ID = 3, Name = "HostServer1", Type = AccountType.ServerHost, User = standardUser };
        standardUser.Accounts.Add(hostAccount);

        db.Roles.Add(adminRole);
        db.Users.AddRange(adminUser, standardUser);
        db.Accounts.AddRange(staffAccount, normalAccount, hostAccount);
        await db.SaveChangesAsync();

        GetAdminUserSummaryHandler handler = new(db, NullLogger<GetAdminUserSummaryHandler>.Instance);

        var result = await handler.Handle(new GetAdminUserSummaryQuery(), default);

        result.IsDatabaseConnected.Should().BeTrue();
        result.TotalUsers.Should().Be(2);
        result.TotalAccounts.Should().Be(3);
        result.TotalStaff.Should().Be(1);
        result.TotalHosts.Should().Be(1);
    }

    [Fact]
    public async Task SetUserRoleHandler_UpdatesRole_ReturnsSuccess()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = UserRoles.User };
        User user = new()
        {
            ID = 10,
            EmailAddress = "rolechange@kongor.net",
            Role = userRole,
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        db.Roles.Add(userRole);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        SetUserRoleHandler handler = new(db, NullLogger<SetUserRoleHandler>.Instance);

        var result = await handler.Handle(new SetUserRoleCommand(10, UserRoles.Administrator), default);

        result.Success.Should().BeTrue();
        user.Role.Name.Should().Be(UserRoles.Administrator);
    }

    [Fact]
    public async Task UpdateUserEconomyHandler_UpdatesBalancesAndLogsTransaction()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 20,
            EmailAddress = "economy@kongor.net",
            GoldCoins = 50,
            SilverCoins = 100,
            PlinkoTickets = 5,
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account acc = new() { ID = 20, Name = "EconomyPlayer", IsMain = true, User = user };
        user.Accounts.Add(acc);

        db.Users.Add(user);
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        UpdateUserEconomyHandler handler = new(db, NullLogger<UpdateUserEconomyHandler>.Instance);

        var result = await handler.Handle(new UpdateUserEconomyCommand(20, GoldCoins: 250, SilverCoins: 500, PlinkoTickets: 12), default);

        result.Success.Should().BeTrue();
        user.GoldCoins.Should().Be(250);
        user.SilverCoins.Should().Be(500);
        user.PlinkoTickets.Should().Be(12);

        var transaction = db.EconomyTransactions.FirstOrDefault(t => t.UserID == 20);
        transaction.Should().NotBeNull();
        transaction!.GoldCoinsDelta.Should().Be(200);
        transaction.SilverCoinsDelta.Should().Be(400);
        transaction.PlinkoTicketsDelta.Should().Be(7);
    }

    [Fact]
    public async Task PromoteUserToStaffHandler_PromotesUserAndMainAccount()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = UserRoles.User };
        User user = new()
        {
            ID = 30,
            EmailAddress = "staffpromote@kongor.net",
            Role = userRole,
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account acc = new() { ID = 30, Name = "StaffCandidate", IsMain = true, Type = AccountType.Normal, User = user };
        user.Accounts.Add(acc);

        db.Roles.Add(userRole);
        db.Users.Add(user);
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        PromoteUserToStaffHandler handler = new(db, NullLogger<PromoteUserToStaffHandler>.Instance);

        var result = await handler.Handle(new PromoteUserToStaffCommand(30), default);

        result.Success.Should().BeTrue();
        user.Role.Name.Should().Be(UserRoles.Administrator);
        acc.Type.Should().Be(AccountType.Staff);
    }

    [Fact]
    public async Task PromoteUserToHostHandler_PromotesMainAccountToServerHost()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 40,
            EmailAddress = "hostpromote@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account acc = new() { ID = 40, Name = "HostCandidate", IsMain = true, Type = AccountType.Normal, User = user };
        user.Accounts.Add(acc);

        db.Users.Add(user);
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        PromoteUserToHostHandler handler = new(db, NullLogger<PromoteUserToHostHandler>.Instance);

        var result = await handler.Handle(new PromoteUserToHostCommand(40), default);

        result.Success.Should().BeTrue();
        acc.Type.Should().Be(AccountType.ServerHost);
    }

    [Fact]
    public async Task RevertUserToStandardHandler_ResetsRoleAndAccountsToNormal()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role adminRole = new() { ID = 1, Name = UserRoles.Administrator };
        User user = new()
        {
            ID = 50,
            EmailAddress = "demote@kongor.net",
            Role = adminRole,
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account acc = new() { ID = 50, Name = "FormerStaff", IsMain = true, Type = AccountType.Staff, User = user };
        user.Accounts.Add(acc);

        db.Roles.Add(adminRole);
        db.Users.Add(user);
        db.Accounts.Add(acc);
        await db.SaveChangesAsync();

        RevertUserToStandardHandler handler = new(db, NullLogger<RevertUserToStandardHandler>.Instance);

        var result = await handler.Handle(new RevertUserToStandardCommand(50), default);

        result.Success.Should().BeTrue();
        user.Role.Name.Should().Be(UserRoles.User);
        acc.Type.Should().Be(AccountType.Normal);
    }
}
