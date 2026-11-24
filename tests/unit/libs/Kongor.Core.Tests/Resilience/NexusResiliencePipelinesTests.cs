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

public class NexusResiliencePipelinesTests
{
    [Fact]
    public void NexusResiliencePipelines_AddNexusResiliencePipelines_RegistersAllPipelinesAsKeyedSingletons()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();

        // Act
        services.AddNexusResiliencePipelines();
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        // Assert
        ResiliencePipeline httpPipeline =
            serviceProvider.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .HttpStandardPipelineName);
        httpPipeline.Should().NotBeNull();

        ResiliencePipeline socketPipeline =
            serviceProvider.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .SocketReconnectPipelineName);
        socketPipeline.Should().NotBeNull();

        ResiliencePipeline redisPipeline =
            serviceProvider.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .RedisRetryPipelineName);
        redisPipeline.Should().NotBeNull();

        ResiliencePipeline processPipeline =
            serviceProvider.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .ProcessSupervisionPipelineName);
        processPipeline.Should().NotBeNull();

        ResiliencePipeline cdnPipeline =
            serviceProvider.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .CdnDownloadPipelineName);
        cdnPipeline.Should().NotBeNull();

        ResiliencePipeline replayPipeline =
            serviceProvider.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .ReplayUploadPipelineName);
        replayPipeline.Should().NotBeNull();
    }

    [Fact]
    public void NexusResiliencePipelines_AddServiceDefaults_RegistersNexusResiliencePipelines()
    {
        // Arrange
        HostApplicationBuilder builder = new();

        // Act
        builder.AddServiceDefaults();
        using IHost host = builder.Build();

        // Assert
        ResiliencePipeline httpPipeline =
            host.Services.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .HttpStandardPipelineName);
        httpPipeline.Should().NotBeNull();

        ResiliencePipeline socketPipeline =
            host.Services.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .SocketReconnectPipelineName);
        socketPipeline.Should().NotBeNull();

        ResiliencePipeline redisPipeline =
            host.Services.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines.RedisRetryPipelineName);
        redisPipeline.Should().NotBeNull();

        ResiliencePipeline processPipeline =
            host.Services.GetRequiredKeyedService<ResiliencePipeline>(NexusResiliencePipelines
                .ProcessSupervisionPipelineName);
        processPipeline.Should().NotBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CreateHttpStandardPipeline_RetriesTransientHttpStatusCodes_AndSucceeds(
        HttpStatusCode transientStatus)
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2)
                throw new HttpRequestException($"Transient error {transientStatus}", null, transientStatus);
            await Task.Yield();
            return "SUCCESS";
        });

        // Assert
        result.Should().Be("SUCCESS");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task CreateHttpStandardPipeline_RetriesNullStatusCodeHttpRequestException_AndSucceeds()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2)
                // Network failure or connection refused without an HTTP status code
                throw new HttpRequestException("Connection reset by peer", null);
            await Task.Yield();
            return "SUCCESS";
        });

        // Assert
        result.Should().Be("SUCCESS");
        attempts.Should().Be(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task NexusResiliencePipelines_CreateHttpStandardPipeline_DoesNotRetryNonTransientHttpStatusCodes(
        HttpStatusCode nonTransientStatus)
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        // Act
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new HttpRequestException($"Non-transient error {nonTransientStatus}", null, nonTransientStatus);
            });
        };

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task CreateHttpStandardPipeline_RetriesTimeoutException_AndSucceeds()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2) throw new TimeoutException("HTTP request timed out");
            await Task.Yield();
            return "SUCCESS";
        });

        // Assert
        result.Should().Be("SUCCESS");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task CreateHttpStandardPipeline_RetriesSocketException_AndSucceeds()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts <= 2) throw new SocketException((int)SocketError.ConnectionRefused);
            await Task.Yield();
            return "SUCCESS";
        });

        // Assert
        result.Should().Be("SUCCESS");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task CreateHttpStandardPipeline_ExceedingMaxRetries_ThrowsLastException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();
        int attempts = 0;

        // Act
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new HttpRequestException("Persistent 500 error", null, HttpStatusCode.InternalServerError);
            });
        };

        // Assert: 1 initial attempt + 3 retries = 4 total attempts
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Persistent 500 error*");
        attempts.Should().Be(4);
    }

    [Fact]
    public async Task CreateHttpStandardPipeline_CircuitBreaker_OpensAfterConsecutiveFailures()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateHttpStandardPipeline();

        // Minimum throughput is 4 and failure ratio is 0.5.
        // Run enough consecutive transient faults to trip the breaker.
        for (int i = 0; i < 4; i++)
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    throw new HttpRequestException("Fault to trip breaker", null, HttpStatusCode.ServiceUnavailable);
                });
            }
            catch
            {
                // Expected failure while tripping circuit breaker
            }

        // Act & Assert: Next call should be blocked immediately with BrokenCircuitException
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                return "SHOULD_NOT_EXECUTE";
            });
        };

        await act.Should().ThrowAsync<BrokenCircuitException>();
    }

    [Fact]
    public async Task HttpPipeline_TimeoutStrategy_ThrowsTimeoutRejectedExceptionOnExceededDuration()
    {
        // Arrange
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        // Act
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return "COMPLETED";
            });
        };

        // Assert
        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateSocketReconnectPipeline_RetriesOnSocketExceptionAndIOException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateSocketReconnectPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts == 1) throw new SocketException((int)SocketError.ConnectionReset);
            if (attempts == 2) throw new IOException("TCP stream read failure");
            if (attempts == 3) throw new TimeoutException("Socket timeout");
            await Task.Yield();
            return "CONNECTED";
        });

        // Assert
        result.Should().Be("CONNECTED");
        attempts.Should().Be(4);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateSocketReconnectPipeline_DoesNotRetryUnhandledException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateSocketReconnectPipeline();
        int attempts = 0;

        // Act
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("Invalid state");
            });
        };

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateRedisRetryPipeline_RetriesOnRedisExceptionAndTimeout()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateRedisRetryPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts == 1) throw new MockRedisException("Redis connection blip");
            if (attempts == 2) throw new TimeoutException("Redis command timeout");
            await Task.Yield();
            return "CACHE_HIT";
        });

        // Assert
        result.Should().Be("CACHE_HIT");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateRedisRetryPipeline_DoesNotRetryUnhandledException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateRedisRetryPipeline();
        int attempts = 0;

        // Act
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new ArgumentException("Invalid key argument");
            });
        };

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateProcessSupervisionPipeline_RetriesOnInvalidOperationExceptionAndTimeout()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateProcessSupervisionPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("Process state invalid");
            if (attempts == 2) throw new TimeoutException("Heartbeat timeout");
            await Task.Yield();
            return "PROCESS_RUNNING";
        });

        // Assert
        result.Should().Be("PROCESS_RUNNING");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateProcessSupervisionPipeline_DoesNotRetryUnhandledException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateProcessSupervisionPipeline();
        int attempts = 0;

        // Act
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new SocketException((int)SocketError.HostUnreachable);
            });
        };

        // Assert
        await act.Should().ThrowAsync<SocketException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateCdnDownloadPipeline_RetriesOnHttpRequestExceptionAndIOException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateCdnDownloadPipeline();
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts == 1) throw new HttpRequestException("CDN chunk download failed");
            if (attempts == 2) throw new IOException("CDN socket dropped mid-stream");
            await Task.Yield();
            return "CHUNK_BYTES";
        });

        // Assert
        result.Should().Be("CHUNK_BYTES");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task NexusResiliencePipelines_CreateReplayUploadPipeline_RetriesOnIOExceptionAndHttpRequestException()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateReplayUploadPipeline(initialRetryDelay: TimeSpan.FromMilliseconds(5));
        int attempts = 0;

        // Act
        string result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts == 1) throw new IOException("Replay locked by game server");
            if (attempts == 2) throw new HttpRequestException("S3 upload timeout");
            await Task.Yield();
            return "UPLOADED";
        });

        // Assert
        result.Should().Be("UPLOADED");
        attempts.Should().Be(3);
    }

    private class MockRedisException(string message) : Exception(message)
    {
    }
}
