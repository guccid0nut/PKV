// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

[Authorize]
public class TelemetryHub : Hub
{
    public async Task RequestMetricsUpdate()
    {
        // Allowed for manual client push requests if needed
        await Task.CompletedTask;
    }
}
