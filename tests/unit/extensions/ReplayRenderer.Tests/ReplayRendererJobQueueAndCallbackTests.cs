// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRendererJobQueueAndCallbackTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NEXUS.Contracts.Replay;
using ReplayRenderer.Service;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;
using ReplayRenderer.Service.Queues;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class ReplayRendererJobQueueAndCallbackTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void ReplayRenderJobQueue_EnqueueAndRetrieve_Succeeds()
    {
        ReplayRenderJobQueue queue = new();

        ReplayRenderJob job = new()
        {
            MatchId = 12345,
            ReplayFilePath = "C:\\replays\\M12345.honreplay",
            OutputPath = "C:\\videos\\M12345.mp4",
            CallbackUrl = "http://localhost:55555/api/replays/render-callback",
            Status = "Queued"
        };

        bool enqueued = queue.EnqueueJob(job);
        Assert.True(enqueued);

        ReplayRenderJob? retrievedById = queue.GetJob(job.JobId);
        Assert.NotNull(retrievedById);
        Assert.Equal(12345, retrievedById.MatchId);

        ReplayRenderJob? retrievedByMatch = queue.GetJobByMatchId(12345);
        Assert.NotNull(retrievedByMatch);
        Assert.Equal(job.JobId, retrievedByMatch.JobId);

        Assert.Single(queue.GetAllJobs());
    }

    [Fact]
    public void ReplayRenderJobRequest_ImplicitConversion_RoundTripsAllFieldsWithoutLoss()
    {
        ReplayRenderJobRequestDto dto = new()
        {
            MatchId = 2060568725,
            ReplayFilePath = "C:\\replays\\M2060568725.honreplay",
            TargetAccountId = 58,
            TargetPlayerIndex = 1,
            TargetPlayerName = "guccidonut",
            CameraMode = "PlayerPOV",
            Resolution = "4K",
            Framerate = 120,
            ShowHud = false,
            StartMs = 45000,
            DurationMs = 30000,
            OutputPath = "C:\\videos\\M2060568725_4k120.mp4",
            CallbackUrl = "http://localhost:55555/callback"
        };

        ReplayRenderJobRequest req = dto;
        ReplayRenderJobRequestDto roundTripped = req;

        Assert.Equal(dto.MatchId, roundTripped.MatchId);
        Assert.Equal(dto.ReplayFilePath, roundTripped.ReplayFilePath);
        Assert.Equal(dto.TargetAccountId, roundTripped.TargetAccountId);
        Assert.Equal(dto.TargetPlayerIndex, roundTripped.TargetPlayerIndex);
        Assert.Equal(dto.TargetPlayerName, roundTripped.TargetPlayerName);
        Assert.Equal(dto.CameraMode, roundTripped.CameraMode);
        Assert.Equal(dto.Resolution, roundTripped.Resolution);
        Assert.Equal(dto.Framerate, roundTripped.Framerate);
        Assert.Equal(dto.ShowHud, roundTripped.ShowHud);
        Assert.Equal(dto.StartMs, roundTripped.StartMs);
        Assert.Equal(dto.DurationMs, roundTripped.DurationMs);
        Assert.Equal(dto.OutputPath, roundTripped.OutputPath);
        Assert.Equal(dto.CallbackUrl, roundTripped.CallbackUrl);
    }

    [Theory]
    [InlineData("4K", 120, false, 45000, 30000, "PlayerPOV", 3, 3840u, 2160u, 120, true, 45, 30.0, 3)]
    [InlineData("1440p", 60, true, 0, 0, "FreeCam", 5, 2560u, 1440u, 60, false, 0, 0.0, -1)]
    [InlineData("720p", 144, false, 15500, 12500, "PlayerPOV", 7, 1280u, 720u, 60, true, 15, 12.5, 7)]
    public void BuildConfiguration_MapsAllParametersAndCoercesInvalidAudioDivisorFramerate(
        string resolution,
        int requestedFps,
        bool showHud,
        int startMs,
        int durationMs,
        string cameraMode,
        int targetPlayerIndex,
        uint expectedWidth,
        uint expectedHeight,
        int expectedFps,
        bool expectedCleanUi,
        int expectedStartSec,
        double expectedDurationSec,
        int expectedPov)
    {
        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry);

        string tempDir = Path.Combine(Path.GetTempPath(), "BuildConfigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string replayPath = Path.Combine(tempDir, "test.honreplay");
        File.WriteAllText(replayPath, "DUMMY");

        try
        {
            ReplayRenderJob job = new()
            {
                MatchId = 777,
                ReplayFilePath = replayPath,
                OutputPath = Path.Combine(tempDir, "out.mp4"),
                Resolution = resolution,
                Framerate = requestedFps,
                ShowHud = showHud,
                StartMs = startMs,
                DurationMs = durationMs,
                CameraMode = cameraMode,
                TargetPlayerIndex = targetPlayerIndex
            };

            RenderJobConfiguration config = service.BuildConfiguration(job);

            Assert.Equal(expectedWidth, config.RenderWidth);
            Assert.Equal(expectedHeight, config.RenderHeight);
            Assert.Equal(expectedFps, config.TargetFramerate);
            Assert.Equal(expectedCleanUi, config.EnableCleanCinematicUi);
            Assert.Equal(expectedStartSec, config.StartSeconds);
            Assert.Equal(expectedPov, config.PovClientIndex);

            if (expectedDurationSec > 0)
            {
                Assert.NotNull(config.MaxDuration);
                Assert.Equal(expectedDurationSec, config.MaxDuration.Value.TotalSeconds, 3);
            }
            else
            {
                Assert.Null(config.MaxDuration);
            }

            // Ensure Validate() passes (including 48000 % TargetFramerate == 0 audio divisor check)
            config.GameDirectory = tempDir;
            config.VulkanLayerPath = tempDir;
            Exception? validationEx = Record.Exception(() => config.Validate());
            Assert.Null(validationEx);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildConfiguration_ResolvesPlayerNameFromTrackedGoldenReplayFixture()
    {
        string current = AppContext.BaseDirectory;
        string? goldenFixturePath = null;
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "tests", "fixtures", "replays", "golden_M2060568725_slice.honreplay");
            if (File.Exists(candidate))
            {
                goldenFixturePath = candidate;
                break;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        Assert.NotNull(goldenFixturePath);

        TelemetryLogger telemetry = new(NullLogger<TelemetryLogger>.Instance);
        ReplayRendererService service = new(
            NullLogger<ReplayRendererService>.Instance,
            NullLoggerFactory.Instance,
            telemetry);

        ReplayRenderJob job = new()
        {
            MatchId = 2060568725,
            ReplayFilePath = goldenFixturePath!,
            CameraMode = "PlayerPOV",
            TargetPlayerIndex = -1,
            TargetPlayerName = "guccidonut",
            Resolution = "1440p",
            Framerate = 120,
            ShowHud = false
        };

        RenderJobConfiguration config = service.BuildConfiguration(job);

        // In golden_M2060568725_slice.honreplay, ClickBotAlpha is clientnum=0 and guccidonut is clientnum=1
        Assert.Equal(1, config.PovClientIndex);
        Assert.Equal(2560u, config.RenderWidth);
        Assert.Equal(1440u, config.RenderHeight);
        Assert.Equal(120, config.TargetFramerate);
        Assert.True(config.EnableCleanCinematicUi);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ReplayRendererService_ProcessesJobAndTriggersCallback_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ReplayRenderJobQueue queue = new();
        string tempDir = Path.Combine(Path.GetTempPath(), "ReplayRendererTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string testOutputPath = Path.Combine(tempDir, "M99999.mp4");
        string callbackUrl = "http://localhost:55555/api/replays/render-callback";

        bool callbackInvoked = false;
        string? callbackStatus = null;
        int callbackMatchId = 0;

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
            {
                if (request.RequestUri?.ToString().Contains("render-callback") == true)
                {
                    callbackInvoked = true;
                    string content = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult() ?? "{}";
                    using JsonDocument doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("matchId", out JsonElement matchElem))
                        callbackMatchId = matchElem.GetInt32();
                    if (doc.RootElement.TryGetProperty("status", out JsonElement statusElem))
                        callbackStatus = statusElem.GetString();

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"success\":true}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
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
            mockFactory.Object
        );

        ReplayRenderJob job = new()
        {
            MatchId = 99999,
            ReplayFilePath = Path.Combine(tempDir, "M99999.honreplay"),
            OutputPath = testOutputPath,
            CallbackUrl = callbackUrl,
            Status = "Queued"
        };

        queue.EnqueueJob(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Start background service
        Task serviceTask = service.StartAsync(cts.Token);

        // Wait until job is completed or timeout
        DateTime timeout = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(4);
        while (TimeProvider.System.GetUtcNow().UtcDateTime < timeout && job.Status != "Completed" &&
               job.Status != "Failed") await Task.Delay(50, cts.Token);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal("Completed", job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.True(File.Exists(testOutputPath));
        Assert.True(callbackInvoked);
        Assert.Equal(99999, callbackMatchId);
        Assert.Equal("Completed", callbackStatus);

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
