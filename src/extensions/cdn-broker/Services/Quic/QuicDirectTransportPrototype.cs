// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: QuicDirectTransportPrototype.cs
// System: NEXUS CDN Broker - Direct System.Net.Quic Stream Prototype
// Policy: Strict Explicit Typing, Sub-Second Execution, Safe Environment Fallback
// ============================================================================

using System.Net;
using System.Net.Quic;
using System.Net.Security;

namespace WILLOWMAKER.Core.Services;

/// <summary>
/// Status of direct QUIC transport prototype.
/// </summary>
internal sealed record QuicDirectTransportStatus
{
    public bool IsSupported { get; init; }
    public string AlpnProtocol { get; init; } = "kongor-cdn-v1";
    public string TransportMode { get; init; } = "DirectQuic";
    public string DiagnosticMessage { get; init; } = string.Empty;
}

/// <summary>
/// Low-level prototype demonstrating direct <see cref="QuicConnection"/> and <see cref="QuicListener"/>
/// multiplexing for differential patch streaming and sub-tick game telemetry.
/// </summary>
internal static class QuicDirectTransportPrototype
{
    public const string DefaultAlpn = "kongor-cdn-v1";

    /// <summary>
    /// Gets the capability status of direct System.Net.Quic transport on this machine.
    /// </summary>
    public static QuicDirectTransportStatus GetStatus()
    {
        bool supported = CheckQuicSupported();
        return new QuicDirectTransportStatus
        {
            IsSupported = supported,
            AlpnProtocol = DefaultAlpn,
            TransportMode = supported ? "DirectQuic" : "TcpFallback",
            DiagnosticMessage = supported
                ? "System.Net.Quic is natively supported. Independent multiplexed stream channels available."
                : "System.Net.Quic requires msquic native library. Falling back to SocketsHttpHandler TCP multiplexing."
        };
    }

#pragma warning disable CA1416 // QuicConnection supported on Windows, Linux, macOS
    /// <summary>
    /// Creates client connection options configured for Project Kongor binary asset distribution.
    /// </summary>
    public static QuicClientConnectionOptions CreateClientOptions(IPEndPoint endPoint, string targetHost = "cdn.kongor.net")
    {
        return new QuicClientConnectionOptions
        {
            RemoteEndPoint = endPoint,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            MaxInboundUnidirectionalStreams = 16,
            MaxInboundBidirectionalStreams = 16,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ApplicationProtocols = [new SslApplicationProtocol(DefaultAlpn)]
            }
        };
    }

    /// <summary>
    /// Creates server listener options configured for dedicated servers and CDN edge nodes.
    /// </summary>
    public static QuicServerConnectionOptions CreateServerOptions(SslServerAuthenticationOptions serverAuthOptions)
    {
        return new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            MaxInboundUnidirectionalStreams = 64,
            MaxInboundBidirectionalStreams = 64,
            ServerAuthenticationOptions = serverAuthOptions
        };
    }
#pragma warning restore CA1416

    private static bool CheckQuicSupported()
    {
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
#pragma warning disable CA1416
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
}
