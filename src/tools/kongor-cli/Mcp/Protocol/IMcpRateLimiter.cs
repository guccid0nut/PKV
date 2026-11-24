// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
///     Rate limiter abstraction for MCP tools dispatching.
/// </summary>
public interface IMcpRateLimiter : IDisposable
{
    /// <summary>
    ///     Attempts to acquire a permit for a tool under a given category and agent ID.
    ///     Returns (true, null) if permitted, or (false, retryAfter) if throttled.
    /// </summary>
    (bool Permitted, TimeSpan? RetryAfter) TryAcquire(string toolName, McpToolCategory category, string? agentId);

    /// <summary>
    ///     Returns remaining permit budget information for diagnostic introspection.
    /// </summary>
    IReadOnlyDictionary<string, object> GetCategoryStatus(string? agentId);

    /// <summary>
    ///     Whether rate limiting is actively enforcing limits.
    /// </summary>
    bool IsEnabled { get; }
}

/// <summary>
///     No-op rate limiter implementation for unit tests, benchmarking, and CI environments.
/// </summary>
public sealed class McpRateLimiterNoOp : IMcpRateLimiter
{
    public (bool Permitted, TimeSpan? RetryAfter) TryAcquire(string toolName, McpToolCategory category, string? agentId)
        => (true, null);

    public IReadOnlyDictionary<string, object> GetCategoryStatus(string? agentId)
        => new Dictionary<string, object> { ["enabled"] = false, ["mode"] = "NoOp" };

    public bool IsEnabled => false;

    public void Dispose() { }
}
