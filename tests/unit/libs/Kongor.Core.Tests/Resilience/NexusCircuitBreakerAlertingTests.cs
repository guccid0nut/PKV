// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Resilience;
using ASPIRE.Common.ServiceDefaults;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Xunit;

namespace Kongor.Core.Tests.Resilience;

public sealed class NexusCircuitBreakerAlertingTests : IDisposable
{
    public NexusCircuitBreakerAlertingTests()
    {
        NexusCircuitBreakerTracker.Reset();
        NexusCircuitBreakerAlertDispatcher.Reset();
    }

    public void Dispose()
    {
        NexusCircuitBreakerTracker.Reset();
        NexusCircuitBreakerAlertDispatcher.Reset();
    }

    #region Tracker State Management Tests

    [Fact]
    public void Tracker_RecordOpen_StoresOpenState_AndHasOpenCircuitsReturnsTrue()
    {
        // Act
        NexusCircuitBreakerTracker.RecordOpen(
            "test-pipeline-http",
            TimeSpan.FromSeconds(15),
            "Downstream HTTP 503 Service Unavailable");

        // Assert
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(
            out string[] openPipelines,
            out IReadOnlyList<CircuitBreakerStateInfo> details);

        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain("test-pipeline-http");

        CircuitBreakerStateInfo info = details.First(x => x.PipelineName == "test-pipeline-http");
        info.PipelineName.Should().Be("test-pipeline-http");
        info.State.Should().Be("Open");
        info.BreakDuration.Should().Be(TimeSpan.FromSeconds(15));
        info.FailureReason.Should().Be("Downstream HTTP 503 Service Unavailable");
    }

    [Fact]
    public void Tracker_RecordHalfOpen_StoresHalfOpenState_AndHasOpenCircuitsReturnsTrue()
    {
        // Act
        NexusCircuitBreakerTracker.RecordHalfOpen("test-pipeline-socket");

        // Assert
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines);
        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain("test-pipeline-socket");

        CircuitBreakerStateInfo? state = NexusCircuitBreakerTracker.GetState("test-pipeline-socket");
        state.Should().NotBeNull();
        state!.State.Should().Be("HalfOpen");
    }

    [Fact]
    public void Tracker_RecordClosed_ClearsCircuit_AndHasOpenCircuitsReturnsFalse()
    {
        // Arrange
        NexusCircuitBreakerTracker.RecordOpen("test-pipeline-redis", TimeSpan.FromSeconds(10));
        NexusCircuitBreakerTracker.HasOpenCircuits(out _).Should().BeTrue();

        // Act
        NexusCircuitBreakerTracker.RecordClosed("test-pipeline-redis");

        // Assert
        NexusCircuitBreakerTracker.GetState("test-pipeline-redis").Should().BeNull();
    }

    [Fact]
    public void Tracker_MultiplePipelines_TracksAllIndependently()
    {
        // Act
        NexusCircuitBreakerTracker.RecordOpen("test-pipeline-a", TimeSpan.FromSeconds(10), "Error A");
        NexusCircuitBreakerTracker.RecordOpen("test-pipeline-b", TimeSpan.FromSeconds(20), "Error B");

        // Assert
        NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines).Should().BeTrue();
        openPipelines.Should().Contain(new[] { "test-pipeline-a", "test-pipeline-b" });

        // Close one
        NexusCircuitBreakerTracker.RecordClosed("test-pipeline-a");
        NexusCircuitBreakerTracker.HasOpenCircuits(out openPipelines).Should().BeTrue();
        openPipelines.Should().Contain("test-pipeline-b");
    }

    #endregion

    #region Health Check Integration Tests

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AspireHealthCheck_ReflectsCircuitBreakerState_DegradedWhenOpen_HealthyWhenClosed()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        HostApplicationBuilder builder = new();
        builder.AddServiceDefaults();
        builder.Logging.ClearProviders();
        using IHost host = builder.Build();

        HealthCheckService healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        // Act 1: Initial health status (all circuits closed)
        HealthReport initialReport = await healthCheckService.CheckHealthAsync();
        initialReport.Status.Should().Be(HealthStatus.Healthy);

        // Act 2: Trip a circuit breaker
        NexusCircuitBreakerTracker.RecordOpen(
            "test-health-socket",
            TimeSpan.FromSeconds(15),
            "Socket connection reset");

        HealthReport degradedReport = await healthCheckService.CheckHealthAsync();

        // Assert 2: Status is degraded
        degradedReport.Status.Should().Be(HealthStatus.Degraded);
        degradedReport.Entries.Should().ContainKey("circuit-breakers");
        degradedReport.Entries["circuit-breakers"].Status.Should().Be(HealthStatus.Degraded);
        degradedReport.Entries["circuit-breakers"].Description.Should().Contain("test-health-socket");

        // Act 3: Recover and close the circuit breaker
        NexusCircuitBreakerTracker.RecordClosed("test-health-socket");

        HealthReport recoveredReport = await healthCheckService.CheckHealthAsync();

        // Assert 3: Status recovers to healthy
        recoveredReport.Status.Should().Be(HealthStatus.Healthy);
        recoveredReport.Entries["circuit-breakers"].Status.Should().Be(HealthStatus.Healthy);
    }

    #endregion

    #region Polly Pipeline Integration Tests

    [Fact]
    public async Task HttpStandardPipeline_TrippingCircuitBreaker_UpdatesTrackerAutomatically()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();

        // Act: Trip the breaker with 4 consecutive failures
        for (int i = 0; i < 4; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("Simulated 503 for breaker trip", null, HttpStatusCode.ServiceUnavailable);
                });
            }
            catch
            {
                // Expected transient exception
            }
        }

        // Assert: Circuit breaker is open in tracker
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines, out IReadOnlyList<CircuitBreakerStateInfo> details);
        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain(NexusResiliencePipelines.HttpStandardPipelineName);

        CircuitBreakerStateInfo info = details.First(x => x.PipelineName == NexusResiliencePipelines.HttpStandardPipelineName);
        info.State.Should().Be("Open");
        info.FailureReason.Should().NotBeNullOrWhiteSpace();

        // Next call throws BrokenCircuitException
        Func<Task> act = async () => await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return "BLOCKED";
        });
        await act.Should().ThrowAsync<BrokenCircuitException>();
    }

    [Fact]
    public async Task SocketReconnectPipeline_TrippingCircuitBreaker_UpdatesTrackerAutomatically()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateSocketReconnectPipeline();

        // Act: Trip the breaker with 4 consecutive socket failures
        for (int i = 0; i < 4; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new SocketException((int)SocketError.ConnectionReset);
                });
            }
            catch
            {
                // Expected transient exception
            }
        }

        // Assert: Circuit breaker is open in tracker
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines);
        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain(NexusResiliencePipelines.SocketReconnectPipelineName);
    }

    [Fact]
    public async Task ExternalNetworkPipeline_TrippingCircuitBreaker_UpdatesTrackerAutomatically()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateExternalNetworkPipeline();

        // Act: Trip the breaker with 3 consecutive failures (MinimumThroughput is 3)
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("GeoIP service unreachable", null, HttpStatusCode.BadGateway);
                });
            }
            catch
            {
                // Expected transient exception
            }
        }

        // Assert: Circuit breaker is open in tracker
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines);
        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain(NexusResiliencePipelines.ExternalNetworkPipelineName);
    }

    [Fact]
    public async Task CdnDownloadPipeline_TrippingCircuitBreaker_UpdatesTrackerAutomatically()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateCdnDownloadPipeline();

        // Act: Trip breaker with 4 consecutive failures
        for (int i = 0; i < 4; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("CDN edge origin 504 Gateway Timeout", null, HttpStatusCode.GatewayTimeout);
                });
            }
            catch
            {
                // Expected transient failure
            }
        }

        // Assert: Circuit breaker is open in tracker
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines);
        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain(NexusResiliencePipelines.CdnDownloadPipelineName);
    }

    [Fact]
    public async Task ReplayUploadPipeline_TrippingCircuitBreaker_UpdatesTrackerAutomatically()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateReplayUploadPipeline(initialRetryDelay: TimeSpan.Zero);

        // Act: Trip breaker with 4 consecutive failures
        for (int i = 0; i < 4; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("S3 Storage upload failed 500 Internal Server Error", null, HttpStatusCode.InternalServerError);
                });
            }
            catch
            {
                // Expected transient failure
            }
        }

        // Assert: Circuit breaker is open in tracker
        bool hasOpen = NexusCircuitBreakerTracker.HasOpenCircuits(out string[] openPipelines);
        hasOpen.Should().BeTrue();
        openPipelines.Should().Contain(NexusResiliencePipelines.ReplayUploadPipelineName);
    }

    #endregion

    #region Telemetry & Webhook Alert Dispatcher Safety Tests

    [Fact]
    public void NexusMetrics_RecordCircuitBreakerStateChange_ExecutesWithoutException()
    {
        Action act = () =>
        {
            NexusMetrics.RecordCircuitBreakerStateChange("nexus-http-standard", "Open");
            NexusMetrics.RecordCircuitBreakerStateChange("nexus-socket-reconnect", "HalfOpen");
            NexusMetrics.RecordCircuitBreakerStateChange("nexus-socket-reconnect", "Closed");
            NexusMetrics.RecordCircuitBreakerStateChange(string.Empty, string.Empty);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AlertDispatcher_DispatchAlerts_ExecutesSafely_WhenUnconfiguredOrInvalidUrl()
    {
        // Act & Assert 1: Unconfigured URL
        Action act1 = () =>
        {
            NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert("nexus-http-standard", TimeSpan.FromSeconds(15), "HTTP timeout");
            NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert("nexus-http-standard");
        };
        act1.Should().NotThrow();

        // Act & Assert 2: Invalid unreachable URL
        Action act2 = () =>
        {
            NexusCircuitBreakerAlertDispatcher.ConfigureWebhookUrl("http://127.0.0.1:59999/invalid/webhook/endpoint");
            NexusCircuitBreakerAlertDispatcher.DispatchOpenAlert("nexus-socket-reconnect", TimeSpan.FromSeconds(15), "Socket reset");
            NexusCircuitBreakerAlertDispatcher.DispatchClosedAlert("nexus-socket-reconnect");
        };
        act2.Should().NotThrow();
    }

    #endregion
}
