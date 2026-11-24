// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Telemetry;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace ASPIRE.Common.Resilience;

/// <summary>
///     High-performance zero-allocation resilience pipelines powered by Polly.Core v8
///     for non-HTTP workflows including raw TCP chat socket reconnection, Redis failover retries,
///     and UDP game server process supervision, as well as standard HTTP resilience.
/// </summary>
public static class NexusResiliencePipelines
{
    public const string HttpStandardPipelineName = "nexus-http-standard";
    public const string SocketReconnectPipelineName = "nexus-socket-reconnect";
    public const string RedisRetryPipelineName = "nexus-redis-retry";
    public const string ProcessSupervisionPipelineName = "nexus-process-supervision";
    public const string CdnDownloadPipelineName = "nexus-cdn-download";
    public const string ReplayUploadPipelineName = "nexus-replay-upload";
    public const string RedisFallbackPipelineName = "nexus-redis-fallback";
    public const string ReplayRendererPipelineName = "nexus-replay-renderer";
    public const string ExternalNetworkPipelineName = "nexus-external-network";

    /// <summary>
    ///     Creates a zero-allocation resilience pipeline for standard HTTP client operations with exponential backoff,
    ///     jitter, circuit breaker protection, and timeout.
    /// </summary>
    public static ResiliencePipeline CreateHttpStandardPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(ex => ex.StatusCode == null ||
                                                        (int)ex.StatusCode >= 500 ||
                                                        ex.StatusCode == HttpStatusCode.RequestTimeout ||
                                                        ex.StatusCode == HttpStatusCode.TooManyRequests)
                    .Handle<TimeoutException>()
                    .Handle<SocketException>()
                    .Handle<Exception>(IsQuicException),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(20),
                MaxDelay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-HTTP] Retrying HTTP request. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(ex => ex.StatusCode == null ||
                                                        (int)ex.StatusCode >= 500 ||
                                                        ex.StatusCode == HttpStatusCode.RequestTimeout ||
                                                        ex.StatusCode == HttpStatusCode.TooManyRequests)
                    .Handle<TimeoutException>()
                    .Handle<SocketException>()
                    .Handle<Exception>(IsQuicException),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                OnOpened = args =>
                {
                    NexusCircuitBreakerTracker.RecordOpen(HttpStandardPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    NexusMetrics.RecordCircuitBreakerStateChange(HttpStandardPipelineName, "Open");
                    logger?.LogError(args.Outcome.Exception,
                        "[RESILIENCE-ALERT] Circuit breaker OPENED for pipeline '{PipelineName}'. Breaking for {BreakDuration}s",
                        HttpStandardPipelineName, args.BreakDuration.TotalSeconds);
                    NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert(HttpStandardPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    NexusCircuitBreakerTracker.RecordClosed(HttpStandardPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(HttpStandardPipelineName, "Closed");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker CLOSED for pipeline '{PipelineName}'. Downstream recovered.", HttpStandardPipelineName);
                    NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert(HttpStandardPipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    NexusCircuitBreakerTracker.RecordHalfOpen(HttpStandardPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(HttpStandardPipelineName, "HalfOpen");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker HALF-OPEN for pipeline '{PipelineName}'. Testing probe.", HttpStandardPipelineName);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-HTTP] HTTP request timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a zero-allocation resilience pipeline for TCP socket reconnection with exponential backoff,
    ///     full jitter, and circuit breaker protection.
    /// </summary>
    public static ResiliencePipeline CreateSocketReconnectPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<SocketException>().Handle<IOException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromMilliseconds(20),
                MaxDelay = TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-SOCKET] Retrying TCP socket operation. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<SocketException>().Handle<IOException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                OnOpened = args =>
                {
                    NexusCircuitBreakerTracker.RecordOpen(SocketReconnectPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    NexusMetrics.RecordCircuitBreakerStateChange(SocketReconnectPipelineName, "Open");
                    logger?.LogError(args.Outcome.Exception,
                        "[RESILIENCE-ALERT] Circuit breaker OPENED for pipeline '{PipelineName}'. Breaking for {BreakDuration}s",
                        SocketReconnectPipelineName, args.BreakDuration.TotalSeconds);
                    NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert(SocketReconnectPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    NexusCircuitBreakerTracker.RecordClosed(SocketReconnectPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(SocketReconnectPipelineName, "Closed");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker CLOSED for pipeline '{PipelineName}'. TCP socket recovered.", SocketReconnectPipelineName);
                    NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert(SocketReconnectPipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    NexusCircuitBreakerTracker.RecordHalfOpen(SocketReconnectPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(SocketReconnectPipelineName, "HalfOpen");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker HALF-OPEN for pipeline '{PipelineName}'. Testing probe.", SocketReconnectPipelineName);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-SOCKET] Socket operation timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a low-latency resilience pipeline for Redis cache and distributed lock operations.
    /// </summary>
    public static ResiliencePipeline CreateRedisRetryPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex.GetType().Name.Contains("Redis", StringComparison.OrdinalIgnoreCase) ||
                    ex is TimeoutException),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(50),
                MaxDelay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogDebug(args.Outcome.Exception,
                        "[RESILIENCE-REDIS] Retrying Redis command. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-REDIS] Redis operation timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a resilience pipeline for COMPEL game server process supervision and heartbeat pacing.
    /// </summary>
    public static ResiliencePipeline CreateProcessSupervisionPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<InvalidOperationException>().Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Linear,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-COMPEL] Retrying game server process supervision. Attempt #{AttemptNumber}",
                        args.AttemptNumber);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-COMPEL] Process supervision timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a high-performance resilience pipeline for CDN manifest fetching and chunk downloading.
    /// </summary>
    public static ResiliencePipeline CreateCdnDownloadPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<IOException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>()
                    .Handle<Exception>(IsQuicException),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(3),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-CDN] Retrying CDN download operation. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<IOException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>()
                    .Handle<Exception>(IsQuicException),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(15),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(20),
                OnOpened = args =>
                {
                    NexusCircuitBreakerTracker.RecordOpen(CdnDownloadPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    NexusMetrics.RecordCircuitBreakerStateChange(CdnDownloadPipelineName, "Open");
                    logger?.LogError(args.Outcome.Exception,
                        "[RESILIENCE-ALERT] Circuit breaker OPENED for pipeline '{PipelineName}'. Breaking for {BreakDuration}s",
                        CdnDownloadPipelineName, args.BreakDuration.TotalSeconds);
                    NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert(CdnDownloadPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    NexusCircuitBreakerTracker.RecordClosed(CdnDownloadPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(CdnDownloadPipelineName, "Closed");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker CLOSED for pipeline '{PipelineName}'. CDN endpoint recovered.", CdnDownloadPipelineName);
                    NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert(CdnDownloadPipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    NexusCircuitBreakerTracker.RecordHalfOpen(CdnDownloadPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(CdnDownloadPipelineName, "HalfOpen");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker HALF-OPEN for pipeline '{PipelineName}'. Testing CDN probe.", CdnDownloadPipelineName);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(60),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-CDN] CDN download timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a resilience pipeline for asynchronous replay file discovery and cloud storage upload.
    /// </summary>
    public static ResiliencePipeline CreateReplayUploadPipeline(ILogger? logger = null, TimeSpan? initialRetryDelay = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(),
                MaxRetryAttempts = 4,
                Delay = initialRetryDelay ?? TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-REPLAY] Retrying replay upload operation. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(20),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    NexusCircuitBreakerTracker.RecordOpen(ReplayUploadPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    NexusMetrics.RecordCircuitBreakerStateChange(ReplayUploadPipelineName, "Open");
                    logger?.LogError(args.Outcome.Exception,
                        "[RESILIENCE-ALERT] Circuit breaker OPENED for pipeline '{PipelineName}'. Breaking for {BreakDuration}s",
                        ReplayUploadPipelineName, args.BreakDuration.TotalSeconds);
                    NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert(ReplayUploadPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    NexusCircuitBreakerTracker.RecordClosed(ReplayUploadPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(ReplayUploadPipelineName, "Closed");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker CLOSED for pipeline '{PipelineName}'. Replay upload storage recovered.", ReplayUploadPipelineName);
                    NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert(ReplayUploadPipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    NexusCircuitBreakerTracker.RecordHalfOpen(ReplayUploadPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(ReplayUploadPipelineName, "HalfOpen");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker HALF-OPEN for pipeline '{PipelineName}'. Testing replay probe.", ReplayUploadPipelineName);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-REPLAY] Replay upload timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a low-latency fallback-ready resilience pipeline for Redis cache reads and session validation.
    /// </summary>
    public static ResiliencePipeline CreateRedisFallbackPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex.GetType().Name.Contains("Redis", StringComparison.OrdinalIgnoreCase) ||
                    ex is TimeoutException or SocketException),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(25),
                MaxDelay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogDebug(args.Outcome.Exception,
                        "[RESILIENCE-REDIS-FALLBACK] Retrying Redis operation before database fallback. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromMilliseconds(1500),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-REDIS-FALLBACK] Redis operation timed out after {Timeout}s; failing over to database repository",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a resilience pipeline for Vulkan headless replay frame rendering and video encoding.
    /// </summary>
    public static ResiliencePipeline CreateReplayRendererPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<InvalidOperationException>()
                    .Handle<TimeoutException>()
                    .Handle<IOException>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Linear,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-RENDERER] Retrying replay frame rendering. Attempt #{AttemptNumber}",
                        args.AttemptNumber);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(60),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-RENDERER] Replay rendering pipeline timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Creates a resilience pipeline for external network requests (GeoIP, IP discovery, webhooks)
    ///     with short circuit breakers and timeouts to prevent login and server blocking.
    /// </summary>
    public static ResiliencePipeline CreateExternalNetworkPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
                    .Handle<SocketException>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "[RESILIENCE-EXTNET] Retrying external network request. Attempt #{AttemptNumber}, Delay: {RetryDelay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
                    .Handle<SocketException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(15),
                OnOpened = args =>
                {
                    NexusCircuitBreakerTracker.RecordOpen(ExternalNetworkPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    NexusMetrics.RecordCircuitBreakerStateChange(ExternalNetworkPipelineName, "Open");
                    logger?.LogError(args.Outcome.Exception,
                        "[RESILIENCE-ALERT] Circuit breaker OPENED for pipeline '{PipelineName}'. Breaking for {BreakDuration}s",
                        ExternalNetworkPipelineName, args.BreakDuration.TotalSeconds);
                    NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert(ExternalNetworkPipelineName, args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    NexusCircuitBreakerTracker.RecordClosed(ExternalNetworkPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(ExternalNetworkPipelineName, "Closed");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker CLOSED for pipeline '{PipelineName}'. External network endpoint recovered.", ExternalNetworkPipelineName);
                    NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert(ExternalNetworkPipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    NexusCircuitBreakerTracker.RecordHalfOpen(ExternalNetworkPipelineName);
                    NexusMetrics.RecordCircuitBreakerStateChange(ExternalNetworkPipelineName, "HalfOpen");
                    logger?.LogInformation("[RESILIENCE-ALERT] Circuit breaker HALF-OPEN for pipeline '{PipelineName}'. Testing probe.", ExternalNetworkPipelineName);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(3),
                OnTimeout = args =>
                {
                    logger?.LogWarning("[RESILIENCE-EXTNET] External network request timed out after {Timeout}s",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    ///     Registers Polly v8 zero-allocation resilience pipelines into the dependency injection container.
    /// </summary>
    public static IServiceCollection AddNexusResiliencePipelines(this IServiceCollection services)
    {
        services.AddKeyedSingleton(HttpStandardPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.HttpStandard");
            return CreateHttpStandardPipeline(logger);
        });

        services.AddKeyedSingleton(SocketReconnectPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.SocketReconnect");
            return CreateSocketReconnectPipeline(logger);
        });

        services.AddKeyedSingleton(RedisRetryPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.RedisRetry");
            return CreateRedisRetryPipeline(logger);
        });

        services.AddKeyedSingleton(ProcessSupervisionPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.ProcessSupervision");
            return CreateProcessSupervisionPipeline(logger);
        });

        services.AddKeyedSingleton(CdnDownloadPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.CdnDownload");
            return CreateCdnDownloadPipeline(logger);
        });

        services.AddKeyedSingleton(ReplayUploadPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.ReplayUpload");
            return CreateReplayUploadPipeline(logger);
        });

        services.AddKeyedSingleton(RedisFallbackPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.RedisFallback");
            return CreateRedisFallbackPipeline(logger);
        });

        services.AddKeyedSingleton(ReplayRendererPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.ReplayRenderer");
            return CreateReplayRendererPipeline(logger);
        });

        services.AddKeyedSingleton(ExternalNetworkPipelineName, (sp, _) =>
        {
            ILoggerFactory? factory = sp.GetService<ILoggerFactory>();
            ILogger? logger = factory?.CreateLogger("ASPIRE.Common.Resilience.ExternalNetwork");
            return CreateExternalNetworkPipeline(logger);
        });

        return services;
    }

    private static bool IsQuicException(Exception ex)
    {
        return ex.GetType().Name.Contains("Quic", StringComparison.OrdinalIgnoreCase) ||
               (ex.InnerException != null && ex.InnerException.GetType().Name.Contains("Quic", StringComparison.OrdinalIgnoreCase));
    }
}
