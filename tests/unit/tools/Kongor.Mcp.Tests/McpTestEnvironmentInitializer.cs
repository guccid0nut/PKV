// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Runtime.CompilerServices;

namespace Kongor.Mcp.Tests;

/// <summary>
///     Initializes test environment for Kongor.Mcp.Tests.
///     Sets KONGOR_MCP_RATE_LIMIT_DISABLED to prevent artificial throttling in existing high-throughput test suites.
///     Dedicated rate-limiting tests instantiate McpRateLimiter explicitly.
/// </summary>
public static class McpTestEnvironmentInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Environment.SetEnvironmentVariable("KONGOR_MCP_RATE_LIMIT_DISABLED", "1");
    }
}
