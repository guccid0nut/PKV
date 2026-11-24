// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Threading.Tasks;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;
using DAWNBRINGER.WebPortal.UI.Services.DeveloperIssues;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

/// <summary>
/// Real-time SignalR Hub providing bi-directional telemetry streaming for the Developer Issues Dashboard.
/// Restricted strictly to authenticated staff and administrators.
/// </summary>
[Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
public class DeveloperIssuesHub(
    IDeveloperIssueAdminService adminService,
    ILogger<DeveloperIssuesHub> logger) : Hub<IDeveloperIssuesClient>
{
    public const string FeedGroupName = "DeveloperIssuesFeed";

    /// <summary>
    /// Joins the developer issues real-time feed and receives the current snapshot.
    /// Double-checks staff authorization on invocation.
    /// </summary>
    public async Task JoinFeed()
    {
        if (!StaffAuthorizationHelper.IsStaff(Context.User))
        {
            logger.LogWarning(
                "[DeveloperIssuesHub] REJECTED JoinFeed: Connection {ConnectionId} (User: {User}) is not authorized staff",
                Context.ConnectionId, Context.User?.Identity?.Name ?? "Anonymous");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, FeedGroupName);
        DeveloperIssuesDashboardViewModel state = await adminService.GetDashboardViewModelAsync();
        await Clients.Caller.OnInitialState(state);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[DeveloperIssuesHub] Staff member {User} ({ConnectionId}) subscribed to Developer Issues Feed ({Count} tracked issues)",
                Context.User?.Identity?.Name ?? "Staff", Context.ConnectionId, state.Issues.Count);
        }
    }

    /// <summary>
    /// Leaves the developer issues real-time feed.
    /// </summary>
    public async Task LeaveFeed()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, FeedGroupName);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[DeveloperIssuesHub] Staff member {User} ({ConnectionId}) unsubscribed from Developer Issues Feed",
                Context.User?.Identity?.Name ?? "Staff", Context.ConnectionId);
        }
    }

    /// <summary>
    /// Invokes automated reflex self-healing for the specified fingerprint from the client.
    /// Verified strictly for staff credentials.
    /// </summary>
    public async Task<ReflexTriggerResultDto> TriggerReflexHeal(string fingerprint)
    {
        if (!StaffAuthorizationHelper.IsStaff(Context.User))
        {
            logger.LogWarning(
                "[DeveloperIssuesHub] REJECTED TriggerReflexHeal: Connection {ConnectionId} (User: {User}) attempted heal on '{Fingerprint}' without staff privileges",
                Context.ConnectionId, Context.User?.Identity?.Name ?? "Anonymous", fingerprint);
            return new ReflexTriggerResultDto
            {
                Fingerprint = fingerprint,
                Success = false,
                Healed = false,
                Receipt = "Unauthorized: Autonomous reflex healing is restricted to staff members.",
                RecipeName = "StaffAuthorizationDenied",
                NewStatus = "Unauthorized"
            };
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[DeveloperIssuesHub] Staff member {User} ({ConnectionId}) triggered reflex heal for fingerprint '{Fingerprint}'",
                Context.User?.Identity?.Name ?? "Staff", Context.ConnectionId, fingerprint);
        }

        return await adminService.TriggerReflexHealAsync(fingerprint);
    }
}
