// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;

namespace ASPIRE.Tests.KONGOR.MasterServer.Infrastructure;

/// <summary>
///     Startup Filter To Set Fake Remote IP Address
/// </summary>
public sealed class RemoteIPAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(nextDelegate => async context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;

                await nextDelegate(context);
            });

            next(app);
        };
    }
}
