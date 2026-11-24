// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Security;

/// <summary>
///     Security header constants and shared internal service key defaults.
/// </summary>
public static class SecurityConstants
{
    /// <summary>
    ///     HTTP header key used to authenticate internal service-to-service calls.
    /// </summary>
    public const string InternalServiceKeyHeader = "X-Internal-Service-Key";

    /// <summary>
    ///     Default fallback internal service key used strictly during local development and testing.
    ///     MUST NOT be used in production environments.
    /// </summary>
    public const string DefaultInternalServiceKey = "NEXUS-Internal-Service-Key-Dev-2026";
}
