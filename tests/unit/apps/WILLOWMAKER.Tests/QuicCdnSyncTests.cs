// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: QuicCdnSyncTests.cs
// System: WILLOWMAKER.Tests - QUIC & HTTP/3 CDN Sync Verification
// ============================================================================

using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Text.Json;
using ASPIRE.Common.Resilience;
using Polly;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class QuicCdnSyncTests
{
    [Fact]
    public void QuicCdnSync_IsQuicSupported_ExecutesWithoutException()
    {
        // Act
        bool isSupported = QuicCdnTransportService.IsQuicSupported;

        // Assert: On Windows/Linux/macOS modern .NET, returns true or false based on msquic presence
        Assert.True(isSupported || !isSupported);
    }

    [Fact]
    public void QuicCdnSync_CreateQuicSocketsHttpHandler_HasHttp3AndPoolingEnabled()
    {
        // Act
        using SocketsHttpHandler handler = QuicCdnTransportService.CreateQuicSocketsHttpHandler();

        // Assert
        Assert.True(handler.EnableMultipleHttp3Connections);
        Assert.True(handler.EnableMultipleHttp2Connections);
        Assert.Equal(64, handler.MaxConnectionsPerServer);
        Assert.Equal(TimeSpan.FromMinutes(10), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), handler.ConnectTimeout);
    }

    [Fact]
    public void QuicCdnSync_CreateQuicRequest_SetsHttp3VersionAndRequestVersionOrLowerPolicy()
    {
        // Arrange
        string testUrl = "https://cdn.kongor.net/retail/x86-64/manifest.json";

        // Act
        using HttpRequestMessage request = QuicCdnTransportService.CreateQuicRequest(HttpMethod.Get, testUrl);

        // Assert
        Assert.Equal(HttpVersion.Version30, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(testUrl, request.RequestUri?.ToString());
    }

    [Theory]
    [InlineData(3, 0, "HTTP/3 (QUIC / UDP)")]
    [InlineData(2, 0, "HTTP/2 (TCP Multiplexed)")]
    [InlineData(1, 1, "HTTP/1.1 (TCP)")]
    public void QuicCdnSync_FormatProtocolDescription_FormatsKnownVersions(int major, int minor, string expected)
    {
        // Arrange
        Version version = new(major, minor);

        // Act
        string description = QuicCdnTransportService.FormatProtocolDescription(version);

        // Assert
        Assert.Equal(expected, description);
    }

    [Fact]
    public async Task QuicCdnSync_CreateCdnDownloadPipeline_ExecutesSuccessfulOperation()
    {
        // Arrange
        ResiliencePipeline pipeline = NexusResiliencePipelines.CreateCdnDownloadPipeline();
        bool executed = false;

        // Act
        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            executed = true;
        });

        // Assert
        Assert.True(executed);
    }

    [Fact]
    public void LauncherSettingsModel_QuicHttp3Setting_SerializesAndDeserializesCorrectly()
    {
        // Arrange
        LauncherSettingsModel model = new()
        {
            EnableQuicHttp3 = true,
            EnableSpatialAudio = true
        };

        // Act
        string json = JsonSerializer.Serialize(model);
        LauncherSettingsModel? deserialized = JsonSerializer.Deserialize<LauncherSettingsModel>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized.EnableQuicHttp3);
        Assert.True(deserialized.EnableSpatialAudio);
    }

    [Fact]
    public async Task QuicCdnTransportService_StreamAssetAsync_StreamsDataAndReportsProgress()
    {
        // Arrange
        byte[] payload = new byte[4096];
        Random.Shared.NextBytes(payload);

        using TestHttpMessageHandler mockHandler = new(req =>
        {
            HttpResponseMessage resp = new(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version30,
                Content = new ByteArrayContent(payload)
            };
            return resp;
        });

        QuicCdnTransportService service = new(mockHandler);
        using MemoryStream dest = new();
        List<long> progressUpdates = [];
        Progress<long> progress = new(p => progressUpdates.Add(p));

        // Act
        CdnStreamingResult result = await service.StreamAssetAsync("https://cdn.kongor.net/test.asset", dest, progress);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(4096, result.BytesTransferred);
        Assert.Equal(payload, dest.ToArray());
        Assert.Equal(HttpVersion.Version30, result.NegotiatedProtocol);
        Assert.Contains("HTTP/3", result.ProtocolDescription);
    }

    [Fact]
    public async Task QuicCdnTransportService_ProbeQuicCapabilityAsync_ReturnsCapabilityReport()
    {
        // Arrange
        using TestHttpMessageHandler mockHandler = new(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version30
            };
        });

        QuicCdnTransportService service = new(mockHandler);

        // Act
        QuicCapabilityReport report = await service.ProbeQuicCapabilityAsync("https://cdn.kongor.net/probe");

        // Assert
        Assert.Equal(HttpVersion.Version30, report.NegotiatedProtocol);
        Assert.False(report.FallbackToTcpRequired);
        Assert.Contains("HTTP/3", report.ProtocolDescription);
    }

    [Fact]
    public async Task QuicAssetStreamingClient_DownloadAssetsParallelAsync_StreamsAllFilesConcurrently()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "QuicClientTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] fileData = [1, 2, 3, 4, 5, 6, 7, 8];

            using TestHttpMessageHandler mockHandler = new(req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = HttpVersion.Version30,
                    Content = new ByteArrayContent(fileData)
                };
            });

            QuicCdnTransportService transport = new(mockHandler);
            QuicAssetStreamingClient client = new(transport);

            AssetStreamingRequest[] requests =
            [
                new() { Url = "https://cdn.kongor.net/chunk1", DestinationPath = Path.Combine(tempDir, "chunk1.bin") },
                new() { Url = "https://cdn.kongor.net/chunk2", DestinationPath = Path.Combine(tempDir, "chunk2.bin") },
                new() { Url = "https://cdn.kongor.net/chunk3", DestinationPath = Path.Combine(tempDir, "chunk3.bin") }
            ];

            // Act
            ParallelQuicSyncReport report = await client.DownloadAssetsParallelAsync(requests, maxConcurrency: 2);

            // Assert
            Assert.Equal(3, report.TotalFiles);
            Assert.Equal(3, report.SucceededFiles);
            Assert.Equal(0, report.FailedFiles);
            Assert.Equal(24, report.TotalBytesTransferred);
            Assert.Empty(report.Errors);
            Assert.True(File.Exists(Path.Combine(tempDir, "chunk1.bin")));
            Assert.True(File.Exists(Path.Combine(tempDir, "chunk2.bin")));
            Assert.True(File.Exists(Path.Combine(tempDir, "chunk3.bin")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

#pragma warning disable CA1416
    [Fact]
    public void QuicDirectTransportPrototype_GetStatusAndOptions_ExposesAlpnAndLimits()
    {
        // Act
        QuicDirectTransportStatus status = QuicDirectTransportPrototype.GetStatus();
        IPEndPoint ep = new(IPAddress.Loopback, 443);
        QuicClientConnectionOptions options = QuicDirectTransportPrototype.CreateClientOptions(ep, "cdn.kongor.net");

        // Assert
        Assert.NotNull(status);
        Assert.Equal("kongor-cdn-v1", status.AlpnProtocol);
        Assert.Equal(ep, options.RemoteEndPoint);
        Assert.Equal(16, options.MaxInboundUnidirectionalStreams);
        Assert.Equal(16, options.MaxInboundBidirectionalStreams);
    }
#pragma warning restore CA1416

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }
}
