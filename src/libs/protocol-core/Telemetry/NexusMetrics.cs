// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ASPIRE.Common.Telemetry;

public static class NexusMetrics
{
    public const string MeterName = "NEXUS.Telemetry";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> CcuEvents = Meter.CreateCounter<long>(
        "nexus_chat_ccu_events_total",
        description: "Total player connection/disconnection events");

    public static readonly Counter<long> SlowQueriesTotal = Meter.CreateCounter<long>(
        "nexus_database_slow_queries_total",
        description: "Total SQL queries exceeding latency threshold");

    public static readonly Histogram<double> QueueWaitDuration = Meter.CreateHistogram<double>(
        "nexus_matchmaking_queue_wait_seconds",
        "s",
        "Matchmaking queue wait duration in seconds");

    public static readonly Counter<long> TelemetryEventsPublished = Meter.CreateCounter<long>(
        "nexus_telemetry_events_published_total",
        description: "Total Redis telemetry events published");

    public static readonly Counter<long> CommandsProcessedTotal = Meter.CreateCounter<long>(
        "nexus_chat_commands_processed_total",
        description: "Total TCP chat commands processed");

    public static readonly Counter<long> MasterServerRequestsTotal = Meter.CreateCounter<long>(
        "nexus_masterserver_requests_total",
        description: "Total MasterServer PHP protocol requests");

    public static readonly Counter<long> EntityCacheHitsTotal = Meter.CreateCounter<long>(
        "nexus_entity_cache_hits_total",
        description: "Total pre-baked entity cache hits");

    public static readonly Counter<long> EntityCacheMissesTotal = Meter.CreateCounter<long>(
        "nexus_entity_cache_misses_total",
        description: "Total pre-baked entity cache misses");

    public static readonly Counter<long> CircuitBreakerTripsTotal = Meter.CreateCounter<long>(
        "nexus_resilience_circuit_breaker_trips_total",
        description: "Total number of times a resilience circuit breaker opened");

    public static readonly Histogram<double> EntityResolutionDuration = Meter.CreateHistogram<double>(
        "nexus_entity_resolution_duration_microseconds",
        "us",
        "Duration of entity resolution operations in microseconds");

    public static readonly Counter<long> MatchProcessingDeadLettersTotal = Meter.CreateCounter<long>(
        "nexus_match_processing_dead_letters_total",
        description: "Total match submissions routed to dead-letter queue");

    /// <summary>
    ///     Records a match submission that failed processing and was routed to the dead-letter queue.
    /// </summary>
    public static void RecordMatchDeadLetter(string failureCategory)
    {
        string boundedCategory = string.IsNullOrWhiteSpace(failureCategory) ? "Unknown" : failureCategory;
        if (boundedCategory.Length > 64) boundedCategory = boundedCategory[..64];

        MatchProcessingDeadLettersTotal.Add(
            1,
            new KeyValuePair<string, object?>("failure_category", boundedCategory));
    }

    /// <summary>
    ///     Records a pre-baked entity cache hit with bounded entity_type and source_tier tags.
    /// </summary>
    public static void RecordEntityCacheHit(string entityType, string sourceTier = "PreBakedMemory")
    {
        string boundedType = string.IsNullOrWhiteSpace(entityType) ? "Unknown" : entityType;
        string boundedTier = string.IsNullOrWhiteSpace(sourceTier) ? "PreBakedMemory" : sourceTier;

        EntityCacheHitsTotal.Add(
            1,
            new KeyValuePair<string, object?>("entity_type", boundedType),
            new KeyValuePair<string, object?>("source_tier", boundedTier));
    }

    /// <summary>
    ///     Records a pre-baked entity cache miss with bounded entity_type and source_tier tags.
    /// </summary>
    public static void RecordEntityCacheMiss(string entityType, string sourceTier = "NotFound")
    {
        string boundedType = string.IsNullOrWhiteSpace(entityType) ? "Unknown" : entityType;
        string boundedTier = string.IsNullOrWhiteSpace(sourceTier) ? "NotFound" : sourceTier;

        EntityCacheMissesTotal.Add(
            1,
            new KeyValuePair<string, object?>("entity_type", boundedType),
            new KeyValuePair<string, object?>("source_tier", boundedTier));
    }

    /// <summary>
    ///     Records entity resolution latency in microseconds with bounded tags.
    /// </summary>
    public static void RecordEntityResolutionDuration(double durationMicroseconds, string entityType, string sourceTier,
        bool found)
    {
        string boundedType = string.IsNullOrWhiteSpace(entityType) ? "Unknown" : entityType;
        string boundedTier = string.IsNullOrWhiteSpace(sourceTier) ? "PreBakedMemory" : sourceTier;

        EntityResolutionDuration.Record(
            durationMicroseconds,
            new KeyValuePair<string, object?>("entity_type", boundedType),
            new KeyValuePair<string, object?>("source_tier", boundedTier),
            new KeyValuePair<string, object?>("found", found));
    }

    /// <summary>
    ///     Records a player connection or disconnection event with bounded low-cardinality status.
    /// </summary>
    public static void RecordCcuEvent(string status)
    {
        string boundedStatus = status switch
        {
            "connected" or "connect" => "connected",
            "disconnected" or "disconnect" => "disconnected",
            "reconnected" or "reconnect" => "reconnected",
            _ => "unknown"
        };

        CcuEvents.Add(1, new KeyValuePair<string, object?>("status", boundedStatus));
    }

    /// <summary>
    ///     Records a slow database query event with bounded command category tag.
    /// </summary>
    public static void RecordSlowQuery(string commandCategory)
    {
        string boundedCategory = string.IsNullOrWhiteSpace(commandCategory) ? "Unknown" : commandCategory;
        SlowQueriesTotal.Add(1, new KeyValuePair<string, object?>("command_category", boundedCategory));
    }

    /// <summary>
    ///     Records matchmaking queue wait duration in seconds with bounded region and mode tags.
    /// </summary>
    public static void RecordQueueWaitDuration(double durationSeconds, string region, string mode)
    {
        QueueWaitDuration.Record(
            durationSeconds,
            new KeyValuePair<string, object?>("region", region),
            new KeyValuePair<string, object?>("mode", mode));
    }

    /// <summary>
    ///     Records a processed chat command with bounded command name and success flag.
    /// </summary>
    public static void RecordCommandProcessed(string commandName, bool success = true)
    {
        CommandsProcessedTotal.Add(
            1,
            new KeyValuePair<string, object?>("command_name", commandName),
            new KeyValuePair<string, object?>("success", success));
    }

    /// <summary>
    ///     Records a master server request with bounded action name and HTTP status code.
    /// </summary>
    public static void RecordMasterServerRequest(string actionName, int statusCode = 200)
    {
        MasterServerRequestsTotal.Add(
            1,
            new KeyValuePair<string, object?>("action", actionName),
            new KeyValuePair<string, object?>("status_code", statusCode));
    }

    public static readonly Counter<long> EconomyTransactionsTotal = Meter.CreateCounter<long>(
        "nexus_economy_transactions_total",
        description: "Total economy transactions recorded");

    public static readonly Counter<long> EconomyCurrencyAwardedTotal = Meter.CreateCounter<long>(
        "nexus_economy_currency_awarded_total",
        description: "Total economy currency awarded");

    /// <summary>
    ///     Records a resilience circuit breaker state transition with bounded pipeline and state tags.
    /// </summary>
    public static void RecordCircuitBreakerStateChange(string pipelineName, string state)
    {
        string boundedPipeline = string.IsNullOrWhiteSpace(pipelineName) ? "Unknown" : pipelineName;
        string boundedState = string.IsNullOrWhiteSpace(state) ? "Unknown" : state;

        CircuitBreakerTripsTotal.Add(
            1,
            new KeyValuePair<string, object?>("pipeline", boundedPipeline),
            new KeyValuePair<string, object?>("state", boundedState));
    }

    /// <summary>
    ///     Records an economy ledger transaction with transaction type and coin deltas.
    /// </summary>
    public static void RecordEconomyTransaction(string transactionType, long goldDelta, long silverDelta, long plinkoDelta)
    {
        string boundedType = string.IsNullOrWhiteSpace(transactionType) ? "Unknown" : transactionType;
        EconomyTransactionsTotal.Add(1, new KeyValuePair<string, object?>("transaction_type", boundedType));

        if (goldDelta != 0)
            EconomyCurrencyAwardedTotal.Add(goldDelta, new KeyValuePair<string, object?>("currency_type", "gold"), new KeyValuePair<string, object?>("transaction_type", boundedType));
        if (silverDelta != 0)
            EconomyCurrencyAwardedTotal.Add(silverDelta, new KeyValuePair<string, object?>("currency_type", "silver"), new KeyValuePair<string, object?>("transaction_type", boundedType));
        if (plinkoDelta != 0)
            EconomyCurrencyAwardedTotal.Add(plinkoDelta, new KeyValuePair<string, object?>("currency_type", "plinko"), new KeyValuePair<string, object?>("transaction_type", boundedType));
    }

    /// <summary>
    ///     Records match rewards distributed to a player.
    /// </summary>
    public static void RecordMatchRewardAwarded(int groupSize, bool isWin, bool isMvp, long gold, long silver, long plinko)
    {
        EconomyTransactionsTotal.Add(1,
            new KeyValuePair<string, object?>("transaction_type", "MatchReward"),
            new KeyValuePair<string, object?>("group_size", groupSize),
            new KeyValuePair<string, object?>("is_win", isWin),
            new KeyValuePair<string, object?>("is_mvp", isMvp));

        if (gold > 0)
            EconomyCurrencyAwardedTotal.Add(gold, new KeyValuePair<string, object?>("currency_type", "gold"), new KeyValuePair<string, object?>("transaction_type", "MatchReward"));
        if (silver > 0)
            EconomyCurrencyAwardedTotal.Add(silver, new KeyValuePair<string, object?>("currency_type", "silver"), new KeyValuePair<string, object?>("transaction_type", "MatchReward"));
        if (plinko > 0)
            EconomyCurrencyAwardedTotal.Add(plinko, new KeyValuePair<string, object?>("currency_type", "plinko"), new KeyValuePair<string, object?>("transaction_type", "MatchReward"));
    }

    public static readonly Counter<long> AllocatedBytesTotal = Meter.CreateCounter<long>(
        "nexus_memory_allocated_bytes_total",
        "By",
        "Total memory allocated in bytes tracked across high-frequency operations");

    public static readonly Counter<long> MicrosecondAllocationsTotal = Meter.CreateCounter<long>(
        "nexus_microsecond_allocations_total",
        description: "Total number of microsecond allocation events tracked");

    public static readonly Histogram<double> AllocationDurationMicroseconds = Meter.CreateHistogram<double>(
        "nexus_allocation_duration_microseconds",
        "us",
        "Duration of allocation operations in microseconds");

    /// <summary>
    /// Records memory allocation in bytes and duration in microseconds for continuous profiling.
    /// </summary>
    public static void RecordAllocation(long bytes, double durationMicroseconds, string operation, string category)
    {
        string boundedOp = string.IsNullOrWhiteSpace(operation) ? "Unknown" : operation;
        string boundedCat = string.IsNullOrWhiteSpace(category) ? "General" : category;

        if (bytes > 0)
        {
            AllocatedBytesTotal.Add(bytes,
                new KeyValuePair<string, object?>("operation", boundedOp),
                new KeyValuePair<string, object?>("category", boundedCat));
        }

        MicrosecondAllocationsTotal.Add(1,
            new KeyValuePair<string, object?>("operation", boundedOp),
            new KeyValuePair<string, object?>("category", boundedCat));

        AllocationDurationMicroseconds.Record(durationMicroseconds,
            new KeyValuePair<string, object?>("operation", boundedOp),
            new KeyValuePair<string, object?>("category", boundedCat));
    }
}

public static class NexusTracing
{
    public const string ActivitySourceName = "NEXUS.Tracing";
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    public static Activity? StartCommandActivity(string commandName, ushort commandCode, int accountId = 0)
    {
        Activity? activity = Source.StartActivity($"ChatCommand/{commandName}", ActivityKind.Server);
        if (activity != null)
        {
            activity.SetTag("rpc.system", "tcp_binary_protocol");
            activity.SetTag("rpc.service", "ChatServer");
            activity.SetTag("rpc.method", commandName);
            activity.SetTag("nexus.command_name", commandName);
            activity.SetTag("nexus.command_code", $"0x{commandCode:X4}");
            if (accountId > 0) activity.SetTag("nexus.account_id", accountId);
        }

        return activity;
    }

    public static Activity? StartPhpActivity(string actionName, int accountId = 0)
    {
        Activity? activity = Source.StartActivity($"PhpProtocol/{actionName}", ActivityKind.Server);
        if (activity != null)
        {
            activity.SetTag("rpc.system", "php_wire_protocol");
            activity.SetTag("rpc.service", "MasterServer");
            activity.SetTag("rpc.method", actionName);
            activity.SetTag("nexus.php_action", actionName);
            if (accountId > 0) activity.SetTag("nexus.account_id", accountId);
        }

        return activity;
    }

    public static Activity? StartRedisActivity(string operation, string key)
    {
        Activity? activity = Source.StartActivity($"Redis/{operation}", ActivityKind.Client);
        if (activity != null)
        {
            activity.SetTag("db.system", "redis");
            activity.SetTag("db.operation", operation);
            activity.SetTag("db.redis.key", key);
            activity.SetTag("nexus.cache_tier", "L2_Redis");
        }

        return activity;
    }

    public static Activity? StartDatabaseActivity(string operation, string tableName)
    {
        Activity? activity = Source.StartActivity($"Database/{operation} {tableName}", ActivityKind.Client);
        if (activity != null)
        {
            activity.SetTag("db.system", "postgresql");
            activity.SetTag("db.name", "merrick_db");
            activity.SetTag("db.operation", operation);
            activity.SetTag("db.sql.table", tableName);
        }

        return activity;
    }

    public static void InjectTraceContext(ActivityContext context, IDictionary<string, string> carrier)
    {
        if (context != default)
        {
            string traceparent =
                $"00-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-{(byte)context.TraceFlags:x2}";
            carrier["traceparent"] = traceparent;
            if (!string.IsNullOrWhiteSpace(context.TraceState)) carrier["tracestate"] = context.TraceState;
        }
    }

    public static ActivityContext ExtractTraceContext(IDictionary<string, string> carrier)
    {
        if (carrier.TryGetValue("traceparent", out string? traceParent) &&
            !string.IsNullOrWhiteSpace(traceParent))
        {
            carrier.TryGetValue("tracestate", out string? traceState);
            if (ActivityContext.TryParse(traceParent, traceState, out ActivityContext parsedContext))
                return parsedContext;
        }

        return default;
    }

    public static void RecordException(Activity? activity, Exception ex)
    {
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection
                {
                    ["exception.message"] = ex.Message,
                    ["exception.type"] = ex.GetType().FullName,
                    ["exception.stacktrace"] = ex.ToString()
                }));
        }
    }

    public static void EmitStartupTrace(string serviceName)
    {
        using Activity? activity = Source.StartActivity($"ServiceStartup/{serviceName}", ActivityKind.Server);
        if (activity != null)
        {
            activity.SetTag("service.name", serviceName);
            activity.SetTag("nexus.startup", true);
            activity.SetStatus(ActivityStatusCode.Ok, "Service Initialized Successfully");
        }
    }
}
