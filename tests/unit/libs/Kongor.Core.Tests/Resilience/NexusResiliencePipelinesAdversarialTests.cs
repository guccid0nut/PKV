// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Resilience;
using ASPIRE.Common.ServiceDefaults;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace Kongor.Core.Tests.Resilience;

public sealed class NexusResiliencePipelinesAdversarialTests
{
    #region Timeout Strategy & Cooperative Cancellation

    [Fact]
    public async Task HttpPipeline_CooperativeCancellation_CancelsUnderlyingWork()
    {
        ResiliencePipeline shortTimeoutPipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        bool wasCancelled = false;

        Func<Task> act = async () =>
        {
            await shortTimeoutPipeline.ExecuteAsync(async ct =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                    throw;
                }
            });
        };

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        wasCancelled.Should().BeTrue();
    }

    #endregion

    private sealed class CustomRedisConnectionException(string message) : Exception(message)
    {
    }

    #region HTTP Standard Pipeline Fault Injection & Retries

    [Fact]
    public async Task HttpPipeline_FaultInjection_503ServiceUnavailable_RetriesAndRecovers()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 3) // 3 failures, succeeds on attempt 4
                throw new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
            await Task.Yield();
            return "RECOVERED_FROM_503";
        });

        result.Should().Be("RECOVERED_FROM_503");
        attempts.Should().Be(4);
    }

    [Fact]
    public async Task HttpPipeline_FaultInjection_429TooManyRequests_RetriesAndRecovers()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2) // 2 rate-limit errors, succeeds on attempt 3
                throw new HttpRequestException("429 Too Many Requests", null, HttpStatusCode.TooManyRequests);
            await Task.Yield();
            return "RATE_LIMIT_RESOLVED";
        });

        result.Should().Be("RATE_LIMIT_RESOLVED");
        attempts.Should().Be(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task HttpPipeline_FaultInjection_OtherTransientErrors_RetriesAndSucceeds(HttpStatusCode status)
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2) throw new HttpRequestException($"Fault {status}", null, status);
            await Task.Yield();
            return $"OK_{status}";
        });

        result.Should().Be($"OK_{status}");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task HttpPipeline_FaultInjection_503PersistentFailure_ThrowsAfterMaxRetries()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new HttpRequestException("Continuous 503 Outage", null, HttpStatusCode.ServiceUnavailable);
            });
        };

        // 1 initial attempt + 3 retries = 4 total attempts
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Continuous 503 Outage*");
        attempts.Should().Be(4);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task HttpPipeline_NonTransientStatusCodes_FailFastWithoutRetrying(HttpStatusCode status)
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new HttpRequestException($"Fatal error {status}", null, status);
            });
        };

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1, $"Status code {status} is non-transient and must fail fast on the 1st attempt");
    }

    #endregion

    #region Circuit Breaker Transitions & Isolation

    [Fact]
    public async Task HttpPipeline_CircuitBreaker_TripsAfterThreshold_BlocksSubsequentCalls()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();

        // Trip the circuit breaker by executing 4 consecutive transient failures
        for (int i = 0; i < 4; i++)
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("Breaker trip error", null, HttpStatusCode.ServiceUnavailable);
                });
            }
            catch
            {
                // Expected while tripping breaker (either HttpRequestException or BrokenCircuitException)
            }

        // Breaker must now be in OPEN state
        bool delegateExecuted = false;
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                delegateExecuted = true;
                await Task.Yield();
                return "UNEXPECTED";
            });
        };

        await act.Should().ThrowAsync<BrokenCircuitException>();
        delegateExecuted.Should().BeFalse("Circuit breaker must short-circuit without invoking the target delegate");
    }

    [Fact]
    public async Task HttpPipeline_MultipleInstances_HaveIsolatedCircuitBreakerState()
    {
        ResiliencePipeline pipeline1 = NexusResiliencePipelines.CreateHttpStandardPipeline();
        ResiliencePipeline pipeline2 = NexusResiliencePipelines.CreateHttpStandardPipeline();

        // Trip pipeline1
        for (int i = 0; i < 4; i++)
            try
            {
                await pipeline1.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("Fault pipeline1", null, HttpStatusCode.ServiceUnavailable);
                });
            }
            catch
            {
                // Expected while tripping pipeline1
            }

        // pipeline1 is tripped
        Func<Task> act1 = async () => await pipeline1.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return 1;
        });
        await act1.Should().ThrowAsync<BrokenCircuitException>();

        // pipeline2 must remain closed and fully operational
        string result2 = await pipeline2.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return "PIPELINE_2_HEALTHY";
        });
        result2.Should().Be("PIPELINE_2_HEALTHY");
    }

    #endregion

    #region Keyed DI Resolution Verification

    [Fact]
    public void AddNexusResiliencePipelines_KeyedSingletons_ResolveIdenticalInstances()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusResiliencePipelines();

        using ServiceProvider sp = services.BuildServiceProvider();

        // 1. nexus-http-standard
        ResiliencePipeline http1 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.HttpStandardPipelineName);
        ResiliencePipeline http2 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.HttpStandardPipelineName);
        http1.Should().NotBeNull();
        http1.Should().BeSameAs(http2);

        // 2. nexus-socket-reconnect
        ResiliencePipeline socket1 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.SocketReconnectPipelineName);
        ResiliencePipeline socket2 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.SocketReconnectPipelineName);
        socket1.Should().NotBeNull();
        socket1.Should().BeSameAs(socket2);

        // 3. nexus-redis-retry
        ResiliencePipeline redis1 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.RedisRetryPipelineName);
        ResiliencePipeline redis2 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.RedisRetryPipelineName);
        redis1.Should().NotBeNull();
        redis1.Should().BeSameAs(redis2);

        // 4. nexus-process-supervision
        ResiliencePipeline process1 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.ProcessSupervisionPipelineName);
        ResiliencePipeline process2 =
            sp.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.ProcessSupervisionPipelineName);
        process1.Should().NotBeNull();
        process1.Should().BeSameAs(process2);
    }

    [Fact]
    public void AddServiceDefaults_KeyedPipelines_ResolveCorrectlyAcrossAllFourKeys()
    {
        HostApplicationBuilder builder = new();
        builder.AddServiceDefaults();
        using IHost host = builder.Build();

        string[] pipelineNames =
        [
            NexusResiliencePipelines.HttpStandardPipelineName,
            NexusResiliencePipelines.SocketReconnectPipelineName,
            NexusResiliencePipelines.RedisRetryPipelineName,
            NexusResiliencePipelines.ProcessSupervisionPipelineName
        ];

        foreach (string name in pipelineNames)
        {
            ResiliencePipeline pipeline = host.Services.GetRequiredKeyedService<ResiliencePipeline>(name);
            pipeline.Should().NotBeNull($"Keyed pipeline '{name}' must be resolvable from DI container");
        }

        // Non-existent key
        ResiliencePipeline? unk = host.Services.GetKeyedService<ResiliencePipeline>("nonexistent-pipeline-key");
        unk.Should().BeNull();
    }

    #endregion

    #region Socket, Redis, and Process Supervision Pipeline Tests

    [Fact]
    public async Task SocketPipeline_TransientRetries_RecoversBeforeBreakerTrips()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateSocketReconnectPipeline();
        int attempts = 0;

        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 3) // 3 transient errors, succeeds on attempt 4
                throw new SocketException((int)SocketError.ConnectionReset);
            await Task.Yield();
            return "SOCKET_RECONNECTED";
        });

        result.Should().Be("SOCKET_RECONNECTED");
        attempts.Should().Be(4);
    }

    [Fact]
    public async Task SocketPipeline_PersistentFailures_TripsCircuitBreaker()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateSocketReconnectPipeline();
        int attempts = 0;

        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new SocketException((int)SocketError.ConnectionReset);
            });
        };

        // 4 consecutive failures will trip the inner circuit breaker
        await act.Should().ThrowAsync<BrokenCircuitException>();
        attempts.Should().Be(4);
    }

    [Fact]
    public async Task RedisPipeline_RetriesCustomRedisException_AndSucceeds()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateRedisRetryPipeline();
        int attempts = 0;

        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2) throw new CustomRedisConnectionException("Redis node failover in progress");
            await Task.Yield();
            return "REDIS_RECOVERED";
        });

        result.Should().Be("REDIS_RECOVERED");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task ProcessSupervisionPipeline_RetriesInvalidOperationException_AndSucceeds()
    {
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateProcessSupervisionPipeline();
        int attempts = 0;

        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2) throw new InvalidOperationException("Process state synchronization pending");
            await Task.Yield();
            return "PROCESS_HEALTHY";
        });

        result.Should().Be("PROCESS_HEALTHY");
        attempts.Should().Be(3);
    }

    #endregion
}
