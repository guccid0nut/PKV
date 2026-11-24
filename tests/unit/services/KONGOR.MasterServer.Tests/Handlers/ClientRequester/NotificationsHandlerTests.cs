// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Text;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using Xunit;

namespace KONGOR.MasterServer.Tests.Handlers.ClientRequester;

public sealed class NotificationsHandlerTests
{
    private static async Task<(Account AccountA, Account AccountB)> SeedAccountsWithNotificationsAsync(
        MerrickContext context,
        int accountANotificationCount = 2,
        int accountBNotificationCount = 1)
    {
        Role userRole = new() { ID = 2, Name = "User" };
        context.Roles.Add(userRole);

        User userA = new()
        {
            EmailAddress = "notify_a@projectkongor.com",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            DiscordId = "100000000000000001",
            Role = userRole
        };
        User userB = new()
        {
            EmailAddress = "notify_b@projectkongor.com",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            DiscordId = "100000000000000002",
            Role = userRole
        };
        context.Users.AddRange(userA, userB);

        Account accountA = new()
        {
            Name = "NotifyPilotA",
            User = userA,
            Type = AccountType.Normal,
            IsMain = true
        };
        Account accountB = new()
        {
            Name = "NotifyPilotB",
            User = userB,
            Type = AccountType.Normal,
            IsMain = true
        };
        context.Accounts.AddRange(accountA, accountB);
        await context.SaveChangesAsync();

        for (int i = 1; i <= accountANotificationCount; i++)
        {
            context.Notifications.Add(new Notification
            {
                AccountId = accountA.ID,
                Content = $"AccountA Notification #{i}"
            });
        }

        for (int i = 1; i <= accountBNotificationCount; i++)
        {
            context.Notifications.Add(new Notification
            {
                AccountId = accountB.ID,
                Content = $"AccountB Notification #{i}"
            });
        }

        await context.SaveChangesAsync();
        return (accountA, accountB);
    }

    [Fact]
    public async Task RemoveAllNotifications_ValidSession_DeletesOnlyCallerRowsAndReturnsPhpStatusOk_Idempotently()
    {
        // protects: k2_x64.dll 0x1807064a0 -> 0x1806d4240 CPHPData["status"] == "OK" (0x180f1f4e4) contract & tenant isolation
        // fails_when: remove_all_notifications returns 400, omits status="OK", fails on 0 rows (Cloud Save local notifications), or wipes another account's rows
        // seam: NotificationsHandler + real AccountRepository over MerrickContext
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (Account accountA, Account accountB) = await SeedAccountsWithNotificationsAsync(context, 3, 2);

        AccountRepository repo = new(context, NullLogger<AccountRepository>.Instance);
        Mock<ISessionContextResolver> mockResolver = new();
        mockResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accountA);

        NotificationsHandler handler = new(repo, mockResolver.Object, NullLogger<NotificationsHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "remove_all_notifications"
        });
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["account_id"] = accountA.ID.ToString(),
            ["cookie"] = "valid_session_cookie_a"
        });

        // First call deletes 3 DB rows
        PhpResult firstResult = (await handler.HandleRequestAsync(httpContext)).Should().BeOfType<PhpResult>().Subject;
        await using MemoryStream firstBody = new();
        httpContext.Response.Body = firstBody;
        await firstResult.ExecuteAsync(httpContext);
        Encoding.UTF8.GetString(firstBody.ToArray()).Should().Be(@"a:1:{s:6:""status"";s:2:""OK"";}");
        (await context.Notifications.CountAsync(n => n.AccountId == accountA.ID)).Should().Be(0);
        (await context.Notifications.CountAsync(n => n.AccountId == accountB.ID)).Should().Be(2);

        // Second call (0 DB rows, e.g. local Cloud Save notifications) remains idempotent with status="OK"
        PhpResult secondResult = (await handler.HandleRequestAsync(httpContext)).Should().BeOfType<PhpResult>().Subject;
        await using MemoryStream secondBody = new();
        httpContext.Response.Body = secondBody;
        await secondResult.ExecuteAsync(httpContext);
        Encoding.UTF8.GetString(secondBody.ToArray()).Should().Be(@"a:1:{s:6:""status"";s:2:""OK"";}");
    }

    [Fact]
    public async Task RemoveAllNotifications_CrossAccountIdMismatch_RejectsUnauthorizedAndPreservesTargetRows()
    {
        // protects: Horizontal privilege escalation guard on account_id form parameter
        // fails_when: Authenticated Account A supplies account_id=<AccountB> and wipes Account B's notifications
        // seam: NotificationsHandler.TryValidateAccountOwnership + AccountRepository
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (Account accountA, Account accountB) = await SeedAccountsWithNotificationsAsync(context, 1, 2);

        AccountRepository repo = new(context, NullLogger<AccountRepository>.Instance);
        Mock<ISessionContextResolver> mockResolver = new();
        mockResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accountA);

        NotificationsHandler handler = new(repo, mockResolver.Object, NullLogger<NotificationsHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "remove_all_notifications"
        });
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["account_id"] = accountB.ID.ToString(),
            ["cookie"] = "valid_session_cookie_a"
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);
        result.Should().BeOfType<UnauthorizedObjectResult>();
        (await context.Notifications.CountAsync(n => n.AccountId == accountB.ID)).Should().Be(2);
    }

    [Fact]
    public async Task DeleteNotification_AndTestCreateNotification_SatisfyNativeK2PhpContracts()
    {
        // protects: k2_x64.dll 0x1806d45d0 (delete_notification: status, internal_id, notify_id) and 0x1806d4ba0 (test_create_notification)
        // fails_when: Missing internal_id/notify_id echo causes 0x1806d45d0 to print '* Failed to remove notification #<id>...'
        // seam: NotificationsHandler + real AccountRepository
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        (Account accountA, _) = await SeedAccountsWithNotificationsAsync(context, 0, 0);

        AccountRepository repo = new(context, NullLogger<AccountRepository>.Instance);
        Mock<ISessionContextResolver> mockResolver = new();
        mockResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accountA);

        NotificationsHandler handler = new(repo, mockResolver.Object, NullLogger<NotificationsHandler>.Instance);

        // 1. Create notification via test_create_notification
        DefaultHttpContext createCtx = new();
        createCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "test_create_notification"
        });
        createCtx.Request.ContentType = "application/x-www-form-urlencoded";
        createCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["account_id[]"] = accountA.ID.ToString(),
            ["params[notification]"] = "Welcome to Project Kongor!",
            ["cookie"] = "valid_session_cookie_a"
        });

        PhpResult createPhp = (await handler.HandleRequestAsync(createCtx)).Should().BeOfType<PhpResult>().Subject;
        IDictionary createDict = createPhp.Value.Should().BeAssignableTo<IDictionary>().Subject;
        createDict["status"].Should().Be("OK");
        createDict["notification"].Should().Be("Welcome to Project Kongor!");
        int createdNotifyId = Convert.ToInt32(createDict["notify_id"]);
        createdNotifyId.Should().BeGreaterThan(0);
        (await context.Notifications.CountAsync(n => n.AccountId == accountA.ID)).Should().Be(1);

        // 2. Delete notification via delete_notification
        DefaultHttpContext deleteCtx = new();
        deleteCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "delete_notification"
        });
        deleteCtx.Request.ContentType = "application/x-www-form-urlencoded";
        deleteCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["account_id"] = accountA.ID.ToString(),
            ["internal_id"] = "77",
            ["notify_id"] = createdNotifyId.ToString(),
            ["cookie"] = "valid_session_cookie_a"
        });

        PhpResult deletePhp = (await handler.HandleRequestAsync(deleteCtx)).Should().BeOfType<PhpResult>().Subject;
        IDictionary deleteDict = deletePhp.Value.Should().BeAssignableTo<IDictionary>().Subject;
        deleteDict["status"].Should().Be("OK");
        deleteDict["internal_id"].Should().Be(77);
        deleteDict["notify_id"].Should().Be(createdNotifyId);
        (await context.Notifications.CountAsync(n => n.AccountId == accountA.ID)).Should().Be(0);
    }
}
