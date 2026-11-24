// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
///     Per-agent token bucket rate limiter for MCP tool execution.
///     Protects game servers, pipelines, and database state from runaway LLM loops.
///     All diagnostic messages are directed strictly to stderr to protect JSON-RPC stdio transport.
/// </summary>
public sealed class McpRateLimiter : IMcpRateLimiter
{
    private readonly ConcurrentDictionary<(string AgentId, McpToolCategory Category), TokenBucketRateLimiter> _limiters = new();
    private readonly TextWriter _diagnosticWriter;

    private static readonly Dictionary<McpToolCategory, (int Limit, TimeSpan Period)> s_budgets = new()
    {
        [McpToolCategory.ReadOnly]           = (60, TimeSpan.FromMinutes(1)),
        [McpToolCategory.UiControl]          = (60, TimeSpan.FromMinutes(1)),
        [McpToolCategory.GameCommand]        = (60, TimeSpan.FromMinutes(1)),
        [McpToolCategory.ServerManagement]   = (25, TimeSpan.FromMinutes(1)),
        [McpToolCategory.NetworkDiagnostics] = (20, TimeSpan.FromMinutes(1)),
        [McpToolCategory.DatabaseOps]        = (15, TimeSpan.FromMinutes(1)),
        [McpToolCategory.AssetPipeline]      = (10, TimeSpan.FromMinutes(1)),
        [McpToolCategory.Simulation]         = (2, TimeSpan.FromMinutes(1)),
        [McpToolCategory.HeavyProfiling]     = (10, TimeSpan.FromMinutes(1)),
        [McpToolCategory.Default]            = (30, TimeSpan.FromMinutes(1))
    };

    public McpRateLimiter(TextWriter? diagnosticWriter = null)
    {
        _diagnosticWriter = diagnosticWriter ?? Console.Error;
    }

    public bool IsEnabled => true;

    /// <summary>
    ///     Attempts to acquire a permit for the specified tool under its category for the calling agent.
    /// </summary>
    public (bool Permitted, TimeSpan? RetryAfter) TryAcquire(string toolName, McpToolCategory category, string? agentId)
    {
        string agent = string.IsNullOrWhiteSpace(agentId) ? "global" : agentId.Trim();
        var key = (agent, category);

        TokenBucketRateLimiter limiter = _limiters.GetOrAdd(key, k => CreateLimiter(k.Category));

        using RateLimitLease lease = limiter.AttemptAcquire(1);
        if (lease.IsAcquired)
        {
            _diagnosticWriter.WriteLine(
                $"[MCP RateLimit] Permit granted for tool '{toolName}' [agent: {agent}, category: {category}]");
            return (true, null);
        }

        // Extract exact retry-after timestamp from lease metadata
        TimeSpan retryAfter = TimeSpan.FromMinutes(1);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan metadataRetry) && metadataRetry > TimeSpan.Zero)
        {
            retryAfter = metadataRetry;
        }
        else if (s_budgets.TryGetValue(category, out var budget))
        {
            retryAfter = budget.Period;
        }

        _diagnosticWriter.WriteLine(
            $"[MCP RateLimit] THROTTLED: Tool '{toolName}' [agent: {agent}, category: {category}] exceeded budget. " +
            $"Retry after {retryAfter.TotalSeconds:F0}s.");

        return (false, retryAfter);
    }

    /// <summary>
    ///     Provides a status snapshot of rate limit configurations and categories for introspection.
    /// </summary>
    public IReadOnlyDictionary<string, object> GetCategoryStatus(string? agentId)
    {
        string agent = string.IsNullOrWhiteSpace(agentId) ? "global" : agentId.Trim();
        var categoryMap = new Dictionary<string, object>();

        foreach (var (cat, budget) in s_budgets)
        {
            bool hasLimiter = _limiters.TryGetValue((agent, cat), out var limiter);
            categoryMap[cat.ToString()] = new
            {
                Limit = budget.Limit,
                PeriodSeconds = budget.Period.TotalSeconds,
                LimiterActive = hasLimiter,
                ActiveLimiterIdleDuration = hasLimiter && limiter != null ? limiter.GetStatistics()?.CurrentAvailablePermits : budget.Limit
            };
        }

        return new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["agentId"] = agent,
            ["categories"] = categoryMap
        };
    }

    private static TokenBucketRateLimiter CreateLimiter(McpToolCategory category)
    {
        var (limit, period) = s_budgets.TryGetValue(category, out var b)
            ? b
            : s_budgets[McpToolCategory.Default];

        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit,
            TokensPerPeriod = limit,
            ReplenishmentPeriod = period,
            AutoReplenishment = true,
            QueueLimit = 0 // Immediate rejection, no blocking
        });
    }

    public void Dispose()
    {
        foreach (TokenBucketRateLimiter limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
        _limiters.Clear();
    }
}
