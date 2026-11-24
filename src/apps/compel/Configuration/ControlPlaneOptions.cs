// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using Microsoft.Extensions.Configuration;

namespace COMPEL.Configuration;

/// <summary>
///     Configures the HTTP control plane that NEXUS and host operators use to query and manage this match server manager.
/// </summary>
public sealed class ControlPlaneOptions
{
    /// <summary>
    ///     The shared bearer token required to call the management endpoints. When empty, the management endpoints reject
    ///     every request, so a token must be configured before remote management can be used.
    /// </summary>
    public string AuthenticationToken { get; set; } = string.Empty;

    /// <summary>
    ///     Explicit development-only opt-in to accept the legacy local "devtoken" bearer literal when
    ///     <c>ASPNETCORE_ENVIRONMENT == "Development"</c>. Defaults to <see langword="false" /> (fail-closed).
    /// </summary>
    public bool AllowInsecureDevToken { get; set; }

    /// <summary>
    ///     Resolves the control-plane HTTP bind address, defaulting to <see cref="IPAddress.Loopback" /> (<c>127.0.0.1</c>)
    ///     unless a valid IP is explicitly configured via <c>Compel:ControlPlaneBindAddress</c> or
    ///     <c>COMPEL_CONTROL_PLANE_BIND_ADDRESS</c>.
    /// </summary>
    public static IPAddress ResolveBindAddress(IConfiguration configuration)
    {
        string? rawBindIp = configuration["Compel:ControlPlaneBindAddress"]
                            ?? configuration["COMPEL_CONTROL_PLANE_BIND_ADDRESS"];

        if (!string.IsNullOrWhiteSpace(rawBindIp) &&
            IPAddress.TryParse(rawBindIp.Trim(), out IPAddress? parsedIp) &&
            parsedIp is not null)
        {
            return parsedIp;
        }

        return IPAddress.Loopback;
    }
}
