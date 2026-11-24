// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: IQuicCdnTransportService.cs
// System: NEXUS CDN Broker - QUIC & HTTP/3 Modernization Engine
// Policy: Strict Explicit Typing, Resilient CDN Asset Streaming
// ============================================================================

using System.Net;
using System.Net.Http;

namespace WILLOWMAKER.Core.Services;

/// <summary>
/// Result of streaming an asset over QUIC / HTTP/3.
/// </summary>
public sealed record CdnStreamingResult
{
    public bool Success { get; init; }
    public long BytesTransferred { get; init; }
    public TimeSpan Duration { get; init; }
    public Version NegotiatedProtocol { get; init; } = HttpVersion.Version30;
    public string ProtocolDescription { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Diagnostic report on QUIC transport capabilities for the current platform and network.
/// </summary>
public sealed record QuicCapabilityReport
{
    public bool IsQuicSupported { get; init; }
    public string Platform { get; init; } = string.Empty;
    public Version NegotiatedProtocol { get; init; } = HttpVersion.Version30;
    public string ProtocolDescription { get; init; } = string.Empty;
    public long HandshakeLatencyMs { get; init; }
    public bool FallbackToTcpRequired { get; init; }
    public string Diagnostics { get; init; } = string.Empty;
}

/// <summary>
/// Interface for QUIC (RFC 9000) and HTTP/3 (RFC 9114) asset distribution channels
/// with automatic zero-downtime fallback to HTTP/2 and HTTP/1.1 TCP.
/// </summary>
public interface IQuicCdnTransportService
{
    /// <summary>
    /// Gets whether System.Net.Quic is natively supported by the host OS and runtime.
    /// </summary>
    bool IsQuicSupported { get; }

    /// <summary>
    /// Shared, high-throughput SocketsHttpHandler with HTTP/3 and connection pooling.
    /// </summary>
    SocketsHttpHandler SharedHandler { get; }

    /// <summary>
    /// Creates an HttpRequestMessage requesting HTTP/3 with RequestVersionOrLower fallback policy.
    /// </summary>
    HttpRequestMessage CreateQuicRequest(HttpMethod method, string url);

    /// <summary>
    /// Streams an asset from the remote CDN into a local destination stream with chunked progress reporting.
    /// </summary>
    Task<CdnStreamingResult> StreamAssetAsync(
        string url,
        Stream destinationStream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes an endpoint to determine if HTTP/3 QUIC connection succeeds or requires TCP fallback.
    /// </summary>
    Task<QuicCapabilityReport> ProbeQuicCapabilityAsync(
        string testEndpointUrl,
        CancellationToken cancellationToken = default);
}
