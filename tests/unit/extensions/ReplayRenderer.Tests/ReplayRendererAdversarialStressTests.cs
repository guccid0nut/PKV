// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRendererAdversarialStressTests.cs
// System: NEXUS Replay Video Renderer Test Suite - Adversarial Challenger Tier
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework, Empirical Verification
// ============================================================================

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ReplayRenderer.Service;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;
using ReplayRenderer.Service.Queues;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class ReplayRendererAdversarialStressTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static IConfiguration CreateStubEnabledConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReplayRenderer:AllowHeadlessStub"] = "true"
            })
            .Build();

    #region Category 1: Concurrent Job Enqueueing Under Load

    [Fact]
    public void Stress_ConcurrentEnqueue_1000Jobs_AllSuccessfullyQueuedAndRetrievable()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        int totalJobs = 1000;
        ConcurrentBag<ReplayRenderJob> enqueuedJobs = [];

        // Act - 50 concurrent threads enqueuing jobs
        Parallel.For(0, totalJobs, new ParallelOptions { MaxDegreeOfParallelism = 50 }, i =>
        {
            ReplayRenderJob job = new()
            {
                MatchId = 100000 + i,
                ReplayFilePath = $@"C:\Replays\M{100000 + i}.honreplay",
                OutputPath = $@"C:\Videos\M{100000 + i}.mp4",
                CallbackUrl = "http://localhost:55555/api/replays/render-callback",
                Status = "Queued"
            };

            bool enqueued = queue.EnqueueJob(job);
            enqueued.Should().BeTrue();
            enqueuedJobs.Add(job);
        });

        // Assert
        IReadOnlyCollection<ReplayRenderJob> allJobs = queue.GetAllJobs();
        allJobs.Count.Should().Be(totalJobs);

        foreach (ReplayRenderJob job in enqueuedJobs)
        {
            ReplayRenderJob? retrievedById = queue.GetJob(job.JobId);
            retrievedById.Should().NotBeNull();
            retrievedById!.MatchId.Should().Be(job.MatchId);

            ReplayRenderJob? retrievedByMatch = queue.GetJobByMatchId(job.MatchId);
            retrievedByMatch.Should().NotBeNull();
            retrievedByMatch!.JobId.Should().Be(job.JobId);
        }
    }

    [Fact]
    public async Task Stress_MultiProducer_SingleConsumer_ChannelIntegrity_UnderHeavyLoad()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        int producerCount = 10;
        int jobsPerProducer = 100;
        int totalJobs = producerCount * jobsPerProducer;

        ConcurrentBag<string> enqueuedJobIds = [];
        ConcurrentBag<string> dequeuedJobIds = [];

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        // Consumer task
        Task consumerTask = Task.Run(async () =>
        {
            while (dequeuedJobIds.Count < totalJobs && !cts.Token.IsCancellationRequested)
            {
                ReplayRenderJob job = await queue.DequeueAsync(cts.Token);
                dequeuedJobIds.Add(job.JobId);
            }
        }, cts.Token);

        // Act - Multiple concurrent producer tasks
        Task[] producerTasks =
        [
            .. Enumerable.Range(0, producerCount).Select(producerId => Task.Run(() =>
            {
                for (int i = 0; i < jobsPerProducer; i++)
                {
                    int matchId = producerId * jobsPerProducer + i;
                    ReplayRenderJob job = new()
                    {
                        MatchId = matchId,
                        ReplayFilePath = $"replays/M{matchId}.honreplay",
                        OutputPath = $"videos/M{matchId}.mp4",
                        Status = "Queued"
                    };

                    queue.EnqueueJob(job);
                    enqueuedJobIds.Add(job.JobId);
                }
            }, cts.Token))
        ];

        await Task.WhenAll(producerTasks);
        await consumerTask;

        // Assert
        enqueuedJobIds.Count.Should().Be(totalJobs);
        dequeuedJobIds.Count.Should().Be(totalJobs);
        dequeuedJobIds.Distinct().Count().Should().Be(totalJobs);
    }

    [Fact]
    public void Stress_ConcurrentEnqueue_DuplicateMatchIds_HandledSafelyWithoutDeadlock()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        int duplicateMatchId = 777777;
        int threadCount = 100;

        // Act - Concurrent enqueueing of jobs with identical MatchId
        Parallel.For(0, threadCount, i =>
        {
            ReplayRenderJob job = new()
            {
                MatchId = duplicateMatchId,
                ReplayFilePath = $@"C:\Replays\M{duplicateMatchId}_{i}.honreplay",
                OutputPath = $@"C:\Videos\M{duplicateMatchId}_{i}.mp4",
                Status = "Queued"
            };

            queue.EnqueueJob(job);
        });

        // Assert
        queue.GetAllJobs().Count.Should().Be(threadCount);
        ReplayRenderJob? matchedJob = queue.GetJobByMatchId(duplicateMatchId);
        matchedJob.Should().NotBeNull();
        matchedJob!.MatchId.Should().Be(duplicateMatchId);
    }

    [Fact]
    public async Task Stress_ConcurrentReadWrite_SnapshotSafety_NoExceptions()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));
        ConcurrentBag<Exception> exceptions = [];

        // Writer task
        Task writerTask = Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested && counter < 1000)
                try
                {
                    queue.EnqueueJob(new ReplayRenderJob
                    {
                        MatchId = Interlocked.Increment(ref counter),
                        ReplayFilePath = "test.honreplay",
                        Status = "Queued"
                    });
                    Thread.Yield();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
        });

        // Reader tasks iterating over snapshot
        Task[] readerTasks =
        [
            .. Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                    try
                    {
                        IReadOnlyCollection<ReplayRenderJob> snapshot = queue.GetAllJobs();
                        foreach (ReplayRenderJob j in snapshot)
                        {
                            string id = j.JobId;
                        }

                        Thread.Yield();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
            }))
        ];

        await Task.WhenAll(new[] { writerTask }.Concat(readerTasks));

        // Assert
        exceptions.Should().BeEmpty();
    }

    #endregion

    #region Category 2: Callback Retry, Timeout, and Network Error Handling

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Stress_Callback_MasterServerReturnsNon200_DaemonContinuesProcessing(HttpStatusCode statusCode)
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        string tempDir = Path.Combine(Path.GetTempPath(), "ReplayRendererStress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        int callbackAttempts = 0;
        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken token) =>
            {
                Interlocked.Increment(ref callbackAttempts);
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("{\"error\":\"Simulated Server Error\"}")
                });
            });

        HttpClient httpClient = new(mockHandler.Object);
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry,
            null,
            queue,
            mockFactory.Object,
            CreateStubEnabledConfiguration()
        );

        ReplayRenderJob job1 = new()
        {
            MatchId = 1111,
            ReplayFilePath = Path.Combine(tempDir, "M1111.honreplay"),
            OutputPath = Path.Combine(tempDir, "M1111.mp4"),
            CallbackUrl = "http://localhost:55555/api/replays/render-callback",
            Status = "Queued"
        };

        ReplayRenderJob job2 = new()
        {
            MatchId = 2222,
            ReplayFilePath = Path.Combine(tempDir, "M2222.honreplay"),
            OutputPath = Path.Combine(tempDir, "M2222.mp4"),
            CallbackUrl = "http://localhost:55555/api/replays/render-callback",
            Status = "Queued"
        };

        queue.EnqueueJob(job1);
        queue.EnqueueJob(job2);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(25));

        // Act
        Task serviceTask = service.StartAsync(cts.Token);

        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(20);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout &&
               (callbackAttempts < 2 || job2.Status == "Queued" || job2.Status == "Processing"))
            await Task.Delay(50, cts.Token);

        await service.StopAsync(CancellationToken.None);

        // Assert - Both jobs processed despite non-200 callback response
        job1.Status.Should().Be("Completed");
        job2.Status.Should().Be("Completed");
        callbackAttempts.Should().Be(2);

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Stress_Callback_NetworkConnectionRefused_HandledGracefully()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        string tempDir = Path.Combine(Path.GetTempPath(), "ReplayRendererStress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused (localhost:59999)"));

        HttpClient httpClient = new(mockHandler.Object);
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry,
            null,
            queue,
            mockFactory.Object,
            CreateStubEnabledConfiguration()
        );

        ReplayRenderJob job = new()
        {
            MatchId = 3333,
            ReplayFilePath = Path.Combine(tempDir, "M3333.honreplay"),
            OutputPath = Path.Combine(tempDir, "M3333.mp4"),
            CallbackUrl = "http://127.0.0.1:59999/api/replays/render-callback",
            Status = "Queued"
        };

        queue.EnqueueJob(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Act
        Task serviceTask = service.StartAsync(cts.Token);

        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(4);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout && job.Status != "Completed" &&
               job.Status != "Failed") await Task.Delay(50, cts.Token);

        await service.StopAsync(CancellationToken.None);

        // Assert - Job completed and service did not crash
        job.Status.Should().Be("Completed");
        File.Exists(job.OutputPath).Should().BeTrue();

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Stress_Callback_MalformedUrl_HandledGracefully()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        string tempDir = Path.Combine(Path.GetTempPath(), "ReplayRendererStress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry,
            null,
            queue,
            httpClientFactory: null,
            configuration: CreateStubEnabledConfiguration()
        );

        ReplayRenderJob job = new()
        {
            MatchId = 4444,
            ReplayFilePath = Path.Combine(tempDir, "M4444.honreplay"),
            OutputPath = Path.Combine(tempDir, "M4444.mp4"),
            CallbackUrl = "not-a-valid-http-url://bad_host:9999/callback",
            Status = "Queued"
        };

        queue.EnqueueJob(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Act
        Task serviceTask = service.StartAsync(cts.Token);

        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(4);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout && job.Status != "Completed" &&
               job.Status != "Failed") await Task.Delay(50, cts.Token);

        await service.StopAsync(CancellationToken.None);

        // Assert
        job.Status.Should().Be("Completed");
        File.Exists(job.OutputPath).Should().BeTrue();

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Stress_AllowHeadlessStub_DefaultsToDisabled_WhenConfigurationIsNull()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry,
            initialJobConfig: null,
            jobQueue: queue,
            httpClientFactory: null,
            configuration: null
        );

        ReplayRenderJob job = new()
        {
            MatchId = 99999,
            ReplayFilePath = "nonexistent.honreplay",
            OutputPath = "output.mp4",
            Status = "Queued"
        };
        queue.EnqueueJob(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        Task serviceTask = service.StartAsync(cts.Token);

        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(2);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout && job.Status != "Completed" && job.Status != "Failed")
        {
            await Task.Delay(50, cts.Token);
        }

        await service.StopAsync(CancellationToken.None);

        // When stub is disabled and replay/hon file does not exist, job should fail
        job.Status.Should().Be("Failed");
    }

    #endregion

    #region Category 3: Replay Path Validation & Error Propagation

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Stress_PathValidation_DeeplyNestedMissingDirectory_AutoCreatedAndRendered()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        ReplayRenderJobQueue queue = new();
        string tempBaseDir = Path.Combine(Path.GetTempPath(), "ReplayRendererStress_" + Guid.NewGuid().ToString("N"));
        string deeplyNestedPath = Path.Combine(tempBaseDir, "sub1", "sub2", "sub3", "videos", "M5555.mp4");

        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry,
            null,
            queue,
            httpClientFactory: null,
            configuration: CreateStubEnabledConfiguration()
        );

        ReplayRenderJob job = new()
        {
            MatchId = 5555,
            ReplayFilePath = "M5555.honreplay",
            OutputPath = deeplyNestedPath,
            Status = "Queued"
        };

        queue.EnqueueJob(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Act
        Task serviceTask = service.StartAsync(cts.Token);

        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(4);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout && job.Status != "Completed" &&
               job.Status != "Failed") await Task.Delay(50, cts.Token);

        await service.StopAsync(CancellationToken.None);

        // Assert
        job.Status.Should().Be("Completed");
        File.Exists(deeplyNestedPath).Should().BeTrue();
        new FileInfo(deeplyNestedPath).Length.Should().BeGreaterThan(0);

        try
        {
            Directory.Delete(tempBaseDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Stress_PathValidation_NullOrEmptyOutputPath_DefaultsToStandardVideosDirectory()
    {
        // Arrange
        ReplayRenderJobQueue queue = new();
        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry,
            null,
            queue,
            httpClientFactory: null,
            configuration: CreateStubEnabledConfiguration()
        );

        ReplayRenderJob job = new()
        {
            MatchId = 6666,
            ReplayFilePath = "M6666.honreplay",
            OutputPath = "", // Empty string
            Status = "Queued"
        };

        queue.EnqueueJob(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Act
        Task serviceTask = service.StartAsync(cts.Token);

        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(4);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout && job.Status != "Completed" &&
               job.Status != "Failed") await Task.Delay(50, cts.Token);

        await service.StopAsync(CancellationToken.None);

        // Assert
        job.Status.Should().Be("Completed");
        job.OutputPath.Should().Contain("M6666.mp4");
        File.Exists(job.OutputPath).Should().BeTrue();

        try
        {
            File.Delete(job.OutputPath);
        }
        catch
        {
        }
    }

    [Fact]
    public void Stress_RenderJobConfiguration_ValidationRules_EnforcesIntegrity()
    {
        // Arrange & Act - Missing ReplayFilePath
        RenderJobConfiguration config1 = new()
        {
            ReplayFilePath = "",
            OutputFilePath = "out.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers\\layer.dll"
        };
        Action act1 = config1.Validate;
        act1.Should().Throw<ArgumentException>();

        // Missing OutputFilePath
        RenderJobConfiguration config2 = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers\\layer.dll"
        };
        Action act2 = config2.Validate;
        act2.Should().Throw<ArgumentException>();

        // Invalid resolution bounds
        RenderJobConfiguration config3 = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "out.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers\\layer.dll",
            RenderWidth = 0,
            RenderHeight = 1080
        };
        Action act3 = config3.Validate;
        act3.Should().Throw<ArgumentException>();

        // Invalid framerate
        RenderJobConfiguration config4 = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "out.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers\\layer.dll",
            TargetFramerate = -10
        };
        Action act4 = config4.Validate;
        act4.Should().Throw<ArgumentException>();
    }

    #endregion
}
