// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NEXUS.Contracts.Replay;
using NEXUS.ControlPanel.GUI.Services;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.Replay;

public sealed class ReplayRenderManagerServiceTests
{
    [Fact]
    public async Task EnqueueJobAsync_WhenDaemonEndpointConfigured_SubmitsPostRenderAndPollsJobsToCompletion()
    {
        // protects: Live HTTP contract between ReplayRenderManagerService and ReplayRenderer.Service (/render + /jobs)
        // fails_when: Constructor ignores httpClient/daemonEndpoint, POST /render payload drops fields, or GET /jobs status fails to complete job
        // seam: Real ReplayRenderManagerService background loop + custom HttpMessageHandler
        string? capturedPostBody = null;
        int jobsPollCount = 0;
        ConcurrentQueue<string> logs = new();

        using StubHttpMessageHandler handler = new(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/render")
            {
                capturedPostBody = await request.Content!.ReadAsStringAsync(ct);
                string ackJson = JsonSerializer.Serialize(new ReplayRenderJobResponseDto
                {
                    JobId = "daemon-job-777",
                    Status = "Queued",
                    Message = "Accepted",
                    EstimatedDurationSeconds = 5
                });
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(ackJson, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/jobs")
            {
                Interlocked.Increment(ref jobsPollCount);
                string jobsJson = """
                [
                  {
                    "jobId": "daemon-job-777",
                    "matchId": 777,
                    "status": "Completed",
                    "outputPath": "C:/renders/Match_777_PlayerPOV.mp4",
                    "completedAt": "2026-11-04T09:00:00Z"
                  }
                ]
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jobsJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient client = new(handler);
        TaskCompletionSource<ReplayRenderJobItemDto> completedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ReplayRenderManagerService sut = new(
            logSink: (source, message, isError) => logs.Enqueue($"{source}:{isError}:{message}"),
            httpClient: client,
            daemonEndpoint: "http://127.0.0.1:18089/");

        sut.QueueChanged += (_, _) =>
        {
            if (sut.CompletedJobs.Count > 0)
            {
                completedTcs.TrySetResult(sut.CompletedJobs[0]);
            }
        };

        ReplayRenderJobItemDto enqueued = await sut.EnqueueJobAsync(new ReplayRenderJobRequestDto
        {
            MatchId = 777,
            ReplayFilePath = "C:/replays/M777.honreplay",
            CameraMode = "PlayerPOV",
            Resolution = "1080p",
            Framerate = 60,
            DurationMs = 15000
        });

        ReplayRenderJobItemDto completed = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capturedPostBody.Should().NotBeNullOrWhiteSpace();
        capturedPostBody.Should().Contain("M777.honreplay");
        jobsPollCount.Should().BeGreaterThanOrEqualTo(1);
        completed.JobId.Should().Be(enqueued.JobId);
        completed.Status.Should().Be("Completed");
        completed.ProgressPercent.Should().Be(100);
        completed.OutputPath.Should().Be("C:/renders/Match_777_PlayerPOV.mp4");
        logs.Should().Contain(entry => entry.Contains("[LIVE_DAEMON]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnqueueJobAsync_WhenNoDaemonConfigured_TagsStatusAndLogsAsSimulatedPreview()
    {
        // protects: Operator transparency invariant — offline UI preview must never masquerade as a real FFmpeg encode
        // fails_when: Unconfigured ReplayRenderManagerService omits [SIMULATED_PREVIEW] tag or fails cancellation
        // seam: Real ReplayRenderManagerService preview loop + CancelActiveJobAsync
        ConcurrentQueue<string> logs = new();
        TaskCompletionSource<bool> previewStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ReplayRenderJobItemDto> finishedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ReplayRenderManagerService sut = new(
            logSink: (_, message, _) =>
            {
                logs.Enqueue(message);
                if (message.Contains("[SIMULATED_PREVIEW]", StringComparison.Ordinal))
                {
                    previewStartedTcs.TrySetResult(true);
                }
            },
            httpClient: null,
            daemonEndpoint: string.Empty);

        sut.QueueChanged += (_, _) =>
        {
            if (sut.CompletedJobs.Count > 0)
            {
                finishedTcs.TrySetResult(sut.CompletedJobs[0]);
            }
        };

        await sut.EnqueueJobAsync(new ReplayRenderJobRequestDto
        {
            MatchId = 888,
            ReplayFilePath = "C:/replays/M888.honreplay",
            Framerate = 60,
            DurationMs = 5000
        });

        await previewStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        sut.ActiveJob?.Status.Should().Contain("[SIMULATED_PREVIEW]");

        bool cancelled = await sut.CancelActiveJobAsync();
        cancelled.Should().BeTrue();

        ReplayRenderJobItemDto finished = await finishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        finished.Status.Should().Be("Cancelled");
        logs.Should().Contain(m => m.Contains("[SIMULATED_PREVIEW]", StringComparison.Ordinal));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
}
