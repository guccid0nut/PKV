// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: QuicCdnTransportService.cs
// System: NEXUS CDN Broker - QUIC & HTTP/3 Modernization Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Quic;

namespace WILLOWMAKER.Core.Services;

/// <summary>
/// Service managing QUIC (RFC 9000) and HTTP/3 (RFC 9114) transport channels for CDN asset
/// synchronisation and replay downloads with automatic, zero-downtime fallback to TCP.
/// </summary>
public sealed class QuicCdnTransportService : IQuicCdnTransportService
{
    private static readonly Lazy<bool> s_isQuicSupported = new(CheckQuicSupported);
    private static readonly SocketsHttpHandler s_sharedHandler = CreateQuicSocketsHttpHandler();

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public QuicCdnTransportService(HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        _httpClient = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(s_sharedHandler, disposeHandler: false);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets whether QUIC is natively supported by the host operating system and runtime.
    /// </summary>
    public static bool IsQuicSupported => s_isQuicSupported.Value;

    bool IQuicCdnTransportService.IsQuicSupported => IsQuicSupported;

    /// <summary>
    /// Shared, high-throughput <see cref="SocketsHttpHandler" /> configured for HTTP/3 over QUIC,
    /// multiplexed HTTP/2, connection pooling, and low-latency DNS resolution.
    /// </summary>
    public static SocketsHttpHandler SharedSocketsHttpHandler => s_sharedHandler;

    public SocketsHttpHandler SharedHandler => s_sharedHandler;

    /// <summary>
    /// Creates a high-performance <see cref="SocketsHttpHandler" /> with HTTP/3 multiplexing enabled.
    /// </summary>
    public static SocketsHttpHandler CreateQuicSocketsHttpHandler()
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
        };
    }

    /// <summary>
    /// Creates an <see cref="HttpRequestMessage" /> preconfigured to request HTTP/3 (QUIC)
    /// while gracefully falling back to HTTP/2 or HTTP/1.1 if UDP port 443 is filtered.
    /// </summary>
    public static HttpRequestMessage CreateQuicRequest(HttpMethod method, string url)
    {
        return new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    HttpRequestMessage IQuicCdnTransportService.CreateQuicRequest(HttpMethod method, string url)
    {
        return CreateQuicRequest(method, url);
    }

    /// <summary>
    /// Streams an asset from the remote CDN into a local destination stream with chunked progress reporting.
    /// </summary>
    public async Task<CdnStreamingResult> StreamAssetAsync(
        string url,
        Stream destinationStream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(destinationStream);

        long startTimestamp = _timeProvider.GetTimestamp();
        try
        {
            using HttpRequestMessage request = CreateQuicRequest(HttpMethod.Get, url);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[65536]; // 64KB high-throughput buffer
            long totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;
                progress?.Report(totalBytesRead);
            }

            TimeSpan duration = _timeProvider.GetElapsedTime(startTimestamp);
            Version version = response.Version;

            return new CdnStreamingResult
            {
                Success = true,
                BytesTransferred = totalBytesRead,
                Duration = duration,
                NegotiatedProtocol = version,
                ProtocolDescription = FormatProtocolDescription(version)
            };
        }
        catch (Exception ex)
        {
            TimeSpan duration = _timeProvider.GetElapsedTime(startTimestamp);
            return new CdnStreamingResult
            {
                Success = false,
                BytesTransferred = 0,
                Duration = duration,
                NegotiatedProtocol = HttpVersion.Unknown,
                ProtocolDescription = "Failed / Disconnected",
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Probes an endpoint to determine if HTTP/3 QUIC connection succeeds or requires TCP fallback.
    /// </summary>
    public async Task<QuicCapabilityReport> ProbeQuicCapabilityAsync(
        string testEndpointUrl,
        CancellationToken cancellationToken = default)
    {
        bool quicSupported = IsQuicSupported;
        long startTimestamp = _timeProvider.GetTimestamp();

        try
        {
            using HttpRequestMessage request = CreateQuicRequest(HttpMethod.Head, testEndpointUrl);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            long latencyMs = (long)_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            Version version = response.Version;
            bool isHttp3 = version == HttpVersion.Version30;

            return new QuicCapabilityReport
            {
                IsQuicSupported = quicSupported,
                Platform = Environment.OSVersion.ToString(),
                NegotiatedProtocol = version,
                ProtocolDescription = FormatProtocolDescription(version),
                HandshakeLatencyMs = latencyMs,
                FallbackToTcpRequired = !isHttp3,
                Diagnostics = isHttp3
                    ? "HTTP/3 QUIC negotiated successfully over UDP port 443."
                    : $"Fell back to {FormatProtocolDescription(version)} over TCP."
            };
        }
        catch (Exception ex)
        {
            long latencyMs = (long)_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            return new QuicCapabilityReport
            {
                IsQuicSupported = quicSupported,
                Platform = Environment.OSVersion.ToString(),
                NegotiatedProtocol = HttpVersion.Unknown,
                ProtocolDescription = "Probe Unreachable",
                HandshakeLatencyMs = latencyMs,
                FallbackToTcpRequired = true,
                Diagnostics = $"Connection probe failed: {ex.Message}. Automatic TCP fallback will be engaged."
            };
        }
    }

    /// <summary>
    /// Checks if System.Net.Quic is supported on this platform without throwing exceptions.
    /// </summary>
    private static bool CheckQuicSupported()
    {
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
#pragma warning disable CA1416 // QuicConnection supported on Windows, Linux, macOS
                return QuicConnection.IsSupported;
#pragma warning restore CA1416
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Formats a user-friendly string describing the negotiated protocol for telemetry.
    /// </summary>
    public static string FormatProtocolDescription(Version version)
    {
        if (version == HttpVersion.Version30)
            return "HTTP/3 (QUIC / UDP)";
        if (version == HttpVersion.Version20)
            return "HTTP/2 (TCP Multiplexed)";
        if (version == HttpVersion.Version11)
            return "HTTP/1.1 (TCP)";

        return $"HTTP/{version.Major}.{version.Minor}";
    }
}
