// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: StreamSanitizerAndLakehouseTests.cs
// System: COMPEL Unit Tests - Layer 5B Stream Sanitizer, Cookie Resync, & S3 Cache-Control
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Net;
using System.Net.Http;
using COMPEL.Services.Supervision;
using COMPEL.Services.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog.Events;
using Xunit;

namespace COMPEL.Tests;

public sealed class StreamSanitizerAndLakehouseTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handlerFunc = handlerFunc;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handlerFunc(request));
        }
    }

    [Fact]
    public void CompelStreamSanitizer_SplitsCarriageReturnAndAnsiOverwrites_PreventsMatchIdAndCookieCorruption()
    {
        // protects: CompelStreamSanitizer + FastCombatLogParser.TryParseMatchId + MatchServerManagerSupervisor.TryParseNewSessionCookie
        //           against terminal-overwrite \r / ANSI CSI / OSC / NUL byte stream corruption (e.g. "match_id, INT: 1434\r26]" -> 143426]).
        // fails_when: Unsplit \r or cursor-erase sequences merge trailing console noise onto MatchID or session cookies, or token-boundary guard accepts "143426]".
        // seam: Real CompelStreamSanitizer.SanitizeLines -> FastCombatLogParser.TryParseMatchId & MatchServerManagerSupervisor.TryParseNewSessionCookie.
        const string expectedCookie = "6b2d4f6a-8c3e-4b2a-9a1e-1234567890ab";
        string rawCombined =
            "\x1B]0;HoN Dedicated\x07\0" +
            "Sv: [18:22:39] Host: Sending match_id, INT: 1434\r26]\x1B[K\r\n" +
            $"\x1B[32m[03:18:10] New session cookie [{expectedCookie}]\x1B[0m\x1B[1G26]";

        IReadOnlyList<string> segments = CompelStreamSanitizer.SanitizeLines(rawCombined);

        segments.Should().Contain("Sv: [18:22:39] Host: Sending match_id, INT: 1434");
        segments.Should().Contain($"[03:18:10] New session cookie [{expectedCookie}]");

        FastCombatLogParser.TryParseMatchId(segments[0].AsSpan(), out int cleanMatchId).Should().BeTrue();
        cleanMatchId.Should().Be(1434);

        // Strict token-boundary check rejects corrupted unsplittable trailing ']' without matching '['
        FastCombatLogParser.TryParseMatchId("Sv: [18:22:39] Host: Sending match_id, INT: 143426]".AsSpan(), out _).Should().BeFalse();

        string cookieSegment = segments.First(s => s.Contains("New session cookie", StringComparison.Ordinal));
        MatchServerManagerSupervisor.TryParseNewSessionCookie(cookieSegment, out string parsedCookie).Should().BeTrue();
        parsedCookie.Should().Be(expectedCookie);

        // Trailing junk or invalid GUIDs must fail anchored validation
        MatchServerManagerSupervisor.TryParseNewSessionCookie($"[03:18:10] New session cookie [{expectedCookie}]26]", out _).Should().BeFalse();
        MatchServerManagerSupervisor.TryParseNewSessionCookie("[03:18:10] New session cookie [not-a-valid-guid]", out _).Should().BeFalse();
    }

    [Fact]
    public void DispatchStreamLine_WithCarriageReturnMangledCookieAndMatchId_UpdatesWorkerSessionCookieAndCleanMatchId()
    {
        // protects: MatchServerManagerSupervisor.DispatchStreamLine cookie extraction & MatchID capture under \r-mangled worker output.
        // fails_when: DispatchStreamLine fails to sanitize raw \r segments, drops New session cookie updates, or captures mangled MatchID 143426.
        // seam: Real MatchServerManagerSupervisor.DispatchStreamLine + DedicatedServerWorker.
        const string rotatedCookie = "9f112233-4455-6677-8899-aabbccddeeff";
        MatchServerManagerOptions options = new()
        {
            Instances = 1,
            AutoStart = false,
            PortRangeOffset = 250,
            QueueArcadeMode = true
        };

        using MatchServerManagerSupervisor supervisor = new(
            Options.Create(options),
            NullLogger<MatchServerManagerSupervisor>.Instance);
        supervisor.InitializeWorkersForTest();

        DedicatedServerWorker worker = supervisor.Workers.Single();
        worker.IsRunningOverride = true;
        worker.SessionCookie = "11111111-1111-1111-1111-111111111111";

        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(
            worker.Port,
            $"Prompt> \r[03:18:10] New session cookie [{rotatedCookie}]\x1B[K\r26]",
            false));
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(
            worker.Port,
            "Sv: [18:22:39] Host: Sending match_id, INT: 1434\r26]\x1B[K",
            false));

        worker.SessionCookie.Should().Be(rotatedCookie);
        worker.MatchId.Should().Be(1434);
    }

    [Fact]
    public void ShouldAllowHeartbeat401ReRegistration_WhenWorkerIsRunning_ReturnsFalseNeverMintingCookie()
    {
        // protects: Layer 2A invariant that COMPEL never calls f=new_session on heartbeat 401 while the worker PID is running.
        // fails_when: COMPEL attempts f=new_session cookie minting mid-match on 401, overwriting K2's rotated session cookie in Redis.
        // seam: MatchServerManagerSupervisor.ShouldAllowHeartbeat401ReRegistration + DedicatedServerWorker.IsRunningOverride.
        DedicatedServerWorker worker = new(1, 11485, 11685, 21485, 21685, "QUEUE ARCADE #Q1");

        worker.IsRunningOverride = true;
        MatchServerManagerSupervisor.ShouldAllowHeartbeat401ReRegistration(worker).Should().BeFalse();

        worker.IsRunningOverride = false;
        MatchServerManagerSupervisor.ShouldAllowHeartbeat401ReRegistration(worker).Should().BeTrue();
    }

    [Fact]
    public async Task CompelTelemetryRelayService_OnServerStdoutLine_SanitizesCarriageReturnBeforeMatchIdCapture()
    {
        // protects: CompelTelemetryRelayService.OnServerStdoutLine against \r/ANSI overwrites inflating MatchID (1434 -> 143426).
        // fails_when: OnServerStdoutLine passes unsanitized raw stdout to TryParseMatchId or fails to reject trailing '26]'.
        // seam: Real CompelTelemetryRelayService.OnServerStdoutLine + GetMatchSnapshot.
        MatchServerManagerOptions options = new() { Gateway = "localhost" };
        AddressResolver addressResolver = new(Options.Create(options), NullLogger<AddressResolver>.Instance);
        await using CompelTelemetryRelayService relay = new(addressResolver, NullLogger<CompelTelemetryRelayService>.Instance);

        relay.OnServerStdoutLine(0, "Sv: [18:22:39] Host: Sending match_id, INT: 143426]", 11485);
        relay.GetMatchSnapshot(143426).Should().BeNull();

        relay.OnServerStdoutLine(0, "Sv: [18:22:39] Host: Sending match_id, INT: 1434\r26]\x1B[K", 11485);
        relay.GetMatchSnapshot(1434).Should().NotBeNull();
        relay.GetMatchSnapshot(1434)!.MatchID.Should().Be(1434);
    }

    [Fact]
    public async Task UploadParquetFileAsync_SetsCacheControlOnRequestHeaders_MatchesSignedHeaders()
    {
        // protects: Layer 5B S3 Cache-Control wire parity (must live on request.Headers, NOT content.Headers where HttpContentHeaders drops it).
        // fails_when: Cache-Control is attached only to content.Headers or stripped from wire headers while remaining in SigV4 SignedHeaders.
        // seam: Real MatchLakehouseStorageUploader.UploadParquetFileAsync + AwsSigV4Signer.
        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler handler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using HttpClient httpClient = new(handler);
        LakehouseStorageSettings settings = new(
            "https://test-project.supabase.co/storage/v1/s3",
            "us-east-1",
            "match-analytics",
            "TESTACCESSKEY",
            "TESTSECRETKEY");

        using MatchLakehouseStorageUploader uploader = new(
            httpClient,
            NullLogger<MatchLakehouseStorageUploader>.Instance,
            settings);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, "PAR1_DUMMY_PAYLOAD_PAR1"u8.ToArray());

            bool success = await uploader.UploadParquetFileAsync(1434, tempFile);

            success.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? wireCacheControl).Should().BeTrue();
            string wireValue = string.Join(",", wireCacheControl!);
            wireValue.Should().Contain("public");
            wireValue.Should().Contain("max-age=31536000");
            wireValue.Should().Contain("immutable");
            capturedRequest.Content!.Headers.Contains("Cache-Control").Should().BeFalse();

            string authHeader = string.Join(' ', capturedRequest.Headers.GetValues("Authorization"));
            authHeader.Should().Contain("cache-control");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void CompelRequestLogging_DemotesFastHealthyPollsToDebug_KeepsSlowErrorsAndMutationsAtInformation()
    {
        // protects: Layer 6C COMPEL Serilog request log demotion for routine polls (< 250ms, 200 OK).
        // fails_when: Fast /ping or /status polls log at Information, or slow polls/errors/mutations get silently dropped to Debug.
        // seam: Real CompelRequestLogging.ResolveRequestLogLevel with DefaultHttpContext.
        DefaultHttpContext fastStatusCtx = new();
        fastStatusCtx.Request.Method = HttpMethods.Get;
        fastStatusCtx.Request.Path = "/status";
        fastStatusCtx.Response.StatusCode = StatusCodes.Status200OK;

        CompelRequestLogging.ResolveRequestLogLevel(fastStatusCtx, 12.4, exception: null, verboseHeartbeatsOverride: false)
            .Should().Be(LogEventLevel.Debug);

        CompelRequestLogging.ResolveRequestLogLevel(fastStatusCtx, 12.4, exception: null, verboseHeartbeatsOverride: true)
            .Should().Be(LogEventLevel.Information);

        CompelRequestLogging.ResolveRequestLogLevel(fastStatusCtx, 250.0, exception: null, verboseHeartbeatsOverride: false)
            .Should().Be(LogEventLevel.Information);

        DefaultHttpContext postMutationCtx = new();
        postMutationCtx.Request.Method = HttpMethods.Post;
        postMutationCtx.Request.Path = "/workers/11485/restart";
        postMutationCtx.Response.StatusCode = StatusCodes.Status200OK;
        CompelRequestLogging.ResolveRequestLogLevel(postMutationCtx, 5.0, exception: null, verboseHeartbeatsOverride: false)
            .Should().Be(LogEventLevel.Information);

        DefaultHttpContext errorPingCtx = new();
        errorPingCtx.Request.Method = HttpMethods.Get;
        errorPingCtx.Request.Path = "/ping";
        errorPingCtx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        CompelRequestLogging.ResolveRequestLogLevel(errorPingCtx, 3.0, exception: null, verboseHeartbeatsOverride: false)
            .Should().Be(LogEventLevel.Error);
    }
}
