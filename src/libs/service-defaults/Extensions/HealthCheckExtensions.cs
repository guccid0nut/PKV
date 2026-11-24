// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.CompilerServices;
using System.Text.Json;
using ASPIRE.Common.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: InternalsVisibleTo("MERRICK.DatabaseContext.Tests")]

namespace ASPIRE.Common.Extensions;

/// <summary>
///     Provides standardized Aspire health check endpoints (/health, /health/live, /health/ready, /alive)
///     and cloud-native readiness/liveness probe registrations for microservices.
/// </summary>
public static class HealthCheckExtensions
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";
    public static readonly TimeSpan DefaultDatabaseCheckTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumHealthCheckPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Adds standardized Aspire health checks including process self-liveness, circuit breakers,
    ///     database (PostgreSQL/Supabase or SQL Server), and distributed cache (Redis/Garnet).
    /// </summary>
    public static IHostApplicationBuilder AddNexusDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        IHealthChecksBuilder healthChecks = builder.Services.AddHealthChecks();

        // Ensure background health check publisher poll intervals strictly exceed the 10s DB check timeout
        builder.Services.PostConfigure<HealthCheckPublisherOptions>(options =>
        {
            if (options.Period < MinimumHealthCheckPollInterval)
            {
                options.Period = MinimumHealthCheckPollInterval;
            }
        });

        // 1. Process Liveness Probe (Always Healthy If Host Is Alive)
        healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("[HEALTHY] Service Process Is Alive"), [LiveTag]);

        // 2a. Resilient Background Services Probe (Degrades on faulted background workers)
        healthChecks.AddCheck("background-workers", () =>
        {
            if (ASPIRE.Common.Hosting.ResilientBackgroundServiceRegistry.HasDegradedWorkers(out IReadOnlyList<ASPIRE.Common.Hosting.ResilientBackgroundService> degraded))
            {
                Dictionary<string, object> data = new(StringComparer.OrdinalIgnoreCase);
                List<string> degradedNames = new(degraded.Count);

                foreach (ASPIRE.Common.Hosting.ResilientBackgroundService worker in degraded)
                {
                    degradedNames.Add(worker.ServiceName);
                    data[worker.ServiceName] = new
                    {
                        worker.ConsecutiveFailures,
                        worker.TotalUnhandledExceptions,
                        worker.LastExceptionAt,
                        worker.LastExceptionMessage
                    };
                }

                return HealthCheckResult.Degraded(
                    $"Degraded background workers ({degraded.Count}): {string.Join(", ", degradedNames)}",
                    data: data);
            }

            return HealthCheckResult.Healthy("All resilient background workers operational.");
        }, [LiveTag]);

        // 2. Circuit Breaker Resilience Probe (Degrades on open circuits)
        healthChecks.AddCheck("circuit-breakers", () =>
        {
            if (ASPIRE.Common.Resilience.NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines, out IReadOnlyList<ASPIRE.Common.Resilience.CircuitBreakerStateInfo> details))
            {
                Dictionary<string, object> data = new(StringComparer.OrdinalIgnoreCase);
                foreach (ASPIRE.Common.Resilience.CircuitBreakerStateInfo item in details)
                {
                    data[item.PipelineName] = new
                    {
                        item.State,
                        DurationSeconds = item.BreakDuration?.TotalSeconds,
                        Reason = item.FailureReason ?? "Downstream Failure",
                        item.LastTransitionUtc
                    };
                }

                return HealthCheckResult.Degraded(
                    $"Active open circuit breakers: {string.Join(", ", openPipelines)}",
                    data: data);
            }

            return HealthCheckResult.Healthy("All circuit breakers operational.");
        }, [LiveTag]);

        string? isTestEnv = builder.Configuration["IS_TEST_ENVIRONMENT"] ??
                            Environment.GetEnvironmentVariable("IS_TEST_ENVIRONMENT");

        // 3. Database Probe (PostgreSQL / Supabase or SQL Server)
        string? dbConnection = builder.Configuration[ConfigurationConstants.SupabaseDbConnectionStringKey]
                               ?? builder.Configuration[ConfigurationConstants.SupabaseDbConnectionStringEnv]
                               ?? builder.Configuration["Supabase:Database:ConnectionString"]
                               ?? builder.Configuration["Database:PostgreSQL:ConnectionString"]
                               ?? builder.Configuration["DATABASE_POSTGRESQL_CONNECTION_STRING"]
                               ?? builder.Configuration["SUPABASE_DB_CONNECTION_STRING"]
                               ?? builder.Configuration.GetConnectionString("MERRICK")
                               ?? builder.Configuration["ConnectionStrings:MERRICK"];

        if (isTestEnv != "true" && !string.IsNullOrWhiteSpace(dbConnection))
        {
            if (builder.Configuration.IsPostgreSqlOrSupabase())
            {
                bool isLegacySqlServerConn =
                    dbConnection.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase) ||
                    dbConnection.Contains("(localdb)", StringComparison.OrdinalIgnoreCase) ||
                    dbConnection.Contains("MultipleActiveResultSets", StringComparison.OrdinalIgnoreCase);

                if (isLegacySqlServerConn)
                {
                    Console.Error.WriteLine(
                        "[HealthCheckExtensions][WARN] Skipping PostgreSQL database health check: resolved connection string contains legacy SQL Server LocalDB/Trusted_Connection keys. Remove stale ConnectionStrings:MERRICK from appsettings.Development.json or configure a valid PostgreSQL/Supabase connection string.");
                }
                else
                {
                    string effectivePostgres = ConfigurationExtensions.NormalizePostgreSqlConnectionString(dbConnection);
                    if (!string.IsNullOrWhiteSpace(effectivePostgres))
                    {
                        // Fallback pool when AddServiceDefaults() runs on a standalone host without AddMerrickDatabase().
                        // Registered via factory so DI owns and disposes the NpgsqlDataSource cleanly on shutdown;
                        // AddMerrickDatabase() replaces this descriptor when present so only one pool ever exists.
                        builder.Services.TryAddSingleton<Npgsql.NpgsqlDataSource>(_ =>
                            Npgsql.NpgsqlDataSource.Create(effectivePostgres));
                    }

                    healthChecks.Add(new HealthCheckRegistration(
                        name: "database",
                        factory: sp => new ClassifiedPostgreSqlHealthCheck(sp, DefaultDatabaseCheckTimeout),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: [ReadyTag, "db", "critical"],
                        timeout: DefaultDatabaseCheckTimeout));
                }
            }
            else
            {
                healthChecks.AddSqlServer(
                    dbConnection,
                    name: "database",
                    tags: [ReadyTag, "db", "critical"],
                    timeout: DefaultDatabaseCheckTimeout,
                    failureStatus: HealthStatus.Unhealthy);
            }
        }

        // 4. Redis Distributed Cache Probe (If Configured)
        string? redisConnStr = builder.Configuration.GetConnectionString("DISTRIBUTED-CACHE")
                               ?? builder.Configuration["ConnectionStrings:DISTRIBUTED-CACHE"]
                               ?? builder.Configuration["REDIS_CONNECTION_STRING"]
                               ?? builder.Configuration.GetConnectionString("DistributedCache");

        if (isTestEnv != "true" && !string.IsNullOrWhiteSpace(redisConnStr))
        {
            healthChecks.AddRedis(
                redisConnStr,
                name: "redis",
                tags: [ReadyTag, "cache", "critical"],
                timeout: TimeSpan.FromSeconds(2),
                failureStatus: HealthStatus.Degraded);
        }

        return builder;
    }

    /// <summary>
    ///     Writes an indented JSON payload containing the full health check report and latency metrics.
    /// </summary>
    public static async Task WriteJsonResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var payload = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.ToString(),
            entries = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    status = kvp.Value.Status.ToString(),
                    duration = kvp.Value.Duration.ToString(),
                    description = kvp.Value.Description,
                    data = kvp.Value.Data.Count > 0 ? kvp.Value.Data : null
                })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, options));
    }

    /// <summary>
    ///     Maps cloud-native health check endpoints into ASP.NET Core endpoint routing.
    ///     - /health: Full diagnostics report in JSON
    ///     - /health/live: Liveness probe for Aspire / Kubernetes container restarts
    ///     - /health/ready: Readiness probe for Aspire / Kubernetes dependency gating
    ///     - /alive: Legacy Aspire liveness endpoint compatibility
    /// </summary>
    public static IEndpointRouteBuilder MapNexusHealthCheckEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 1. Comprehensive Health Check (/health)
        endpoints.MapHealthChecks("/health", new HealthCheckOptions
        {
            AllowCachingResponses = false,
            ResponseWriter = WriteJsonResponse,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });

        // 2. Readiness Probe (/health/ready)
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(ReadyTag) || r.Tags.Contains("critical"),
            AllowCachingResponses = false,
            ResponseWriter = WriteJsonResponse,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });

        // 3. Liveness Probe (/health/live)
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(LiveTag),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });

        // 4. Legacy Aliveness Endpoint (/alive)
        endpoints.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(LiveTag),
            AllowCachingResponses = false
        });

        return endpoints;
    }
}

/// <summary>
///     PostgreSQL health check that resolves <see cref="Npgsql.NpgsqlDataSource" /> lazily from DI
///     and classifies failures: single transient timeouts/cancellations report <see cref="HealthStatus.Degraded" />,
///     while auth failures (28P01), socket connection refusals, DNS errors, and &gt;= 3 consecutive timeouts
///     report <see cref="HealthStatus.Unhealthy" /> immediately.
/// </summary>
public sealed class ClassifiedPostgreSqlHealthCheck : IHealthCheck
{
    public const int UnhealthyConsecutiveTimeoutThreshold = 3;

    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _probeTimeout;
    private readonly Func<Npgsql.NpgsqlDataSource, CancellationToken, Task>? _probeAsync;
    private int _consecutiveTimeouts;

    public ClassifiedPostgreSqlHealthCheck(IServiceProvider serviceProvider)
        : this(serviceProvider, HealthCheckExtensions.DefaultDatabaseCheckTimeout, null)
    {
    }

    internal ClassifiedPostgreSqlHealthCheck(
        IServiceProvider serviceProvider,
        TimeSpan probeTimeout,
        Func<Npgsql.NpgsqlDataSource, CancellationToken, Task>? probeAsync = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _probeTimeout = probeTimeout > TimeSpan.Zero ? probeTimeout : HealthCheckExtensions.DefaultDatabaseCheckTimeout;
        _probeAsync = probeAsync;
    }

    public int ConsecutiveTimeouts => Volatile.Read(ref _consecutiveTimeouts);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Npgsql.NpgsqlDataSource? dataSource = _serviceProvider.GetService<Npgsql.NpgsqlDataSource>();
        if (dataSource is null)
        {
            return HealthCheckResult.Unhealthy("[UNHEALTHY] No NpgsqlDataSource is registered in DI.");
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_probeTimeout);

        try
        {
            if (_probeAsync is not null)
            {
                await _probeAsync(dataSource, timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                await using Npgsql.NpgsqlConnection connection =
                    await dataSource.OpenConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
                await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                await command.ExecuteScalarAsync(timeoutCts.Token).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
            return HealthCheckResult.Healthy("[HEALTHY] PostgreSQL database connection succeeded.");
        }
        catch (Exception ex)
        {
            return ClassifyFailure(ex);
        }
    }

    internal HealthCheckResult ClassifyFailure(Exception ex)
    {
        if (TryFindException<Npgsql.PostgresException>(ex, static pg =>
                !string.IsNullOrEmpty(pg.SqlState) && pg.SqlState.StartsWith("28", StringComparison.Ordinal),
                out Npgsql.PostgresException? authEx))
        {
            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
            return HealthCheckResult.Unhealthy(
                $"[UNHEALTHY] PostgreSQL authentication failure (SqlState={authEx!.SqlState}): {authEx.Message}",
                ex);
        }

        if (TryFindException<SocketException>(ex, static sock => sock.SocketErrorCode != SocketError.TimedOut,
                out SocketException? socketEx))
        {
            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
            string kind = socketEx!.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                ? "DNS resolution failure"
                : "connection failure";
            return HealthCheckResult.Unhealthy(
                $"[UNHEALTHY] PostgreSQL {kind} ({socketEx.SocketErrorCode}): {socketEx.Message}",
                ex);
        }

        if (IsTransientTimeout(ex))
        {
            int streak = Interlocked.Increment(ref _consecutiveTimeouts);
            if (streak >= UnhealthyConsecutiveTimeoutThreshold)
            {
                return HealthCheckResult.Unhealthy(
                    $"[UNHEALTHY] PostgreSQL health check timed out {streak} consecutive times (threshold={UnhealthyConsecutiveTimeoutThreshold}): {ex.Message}",
                    ex);
            }

            return HealthCheckResult.Degraded(
                $"[DEGRADED] Transient PostgreSQL health check timeout ({streak}/{UnhealthyConsecutiveTimeoutThreshold}): {ex.Message}",
                ex);
        }

        Interlocked.Exchange(ref _consecutiveTimeouts, 0);
        return HealthCheckResult.Unhealthy(
            $"[UNHEALTHY] PostgreSQL health check failed ({ex.GetType().Name}): {ex.Message}",
            ex);
    }

    private static bool IsTransientTimeout(Exception ex)
    {
        return TryFindException<OperationCanceledException>(ex, static _ => true, out _) ||
               TryFindException<TimeoutException>(ex, static _ => true, out _) ||
               TryFindException<SocketException>(ex, static sock => sock.SocketErrorCode == SocketError.TimedOut, out _);
    }

    private static bool TryFindException<TException>(
        Exception root,
        Func<TException, bool> predicate,
        out TException? matched)
        where TException : Exception
    {
        Queue<Exception> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            Exception current = queue.Dequeue();
            if (current is TException typed && predicate(typed))
            {
                matched = typed;
                return true;
            }

            if (current is AggregateException agg)
            {
                foreach (Exception inner in agg.InnerExceptions)
                {
                    queue.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                queue.Enqueue(current.InnerException);
            }
        }

        matched = null;
        return false;
    }
}
