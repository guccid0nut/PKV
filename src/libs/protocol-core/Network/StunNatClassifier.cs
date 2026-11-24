// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Kongor.Core.Network;

/// <summary>
///     RFC 5389 / RFC 5780 NAT mapping behavior classification used to select the optimal DirectPlay P2P transport.
/// </summary>
public enum NatBehaviorClassification
{
    Unknown = 0,
    OpenInternet = 1,
    ConeNat = 2,
    SymmetricOrCgnat = 3,
    UdpBlocked = 4
}

/// <summary>
///     Outcome of a 2-endpoint RFC 5389/5780 STUN NAT classification probe.
/// </summary>
public sealed record StunProbeResult(
    NatBehaviorClassification Classification,
    IPEndPoint? LocalEndPoint,
    IPEndPoint? PrimaryMappedEndPoint,
    IPEndPoint? SecondaryMappedEndPoint,
    bool UsedXorMappedAddress,
    bool IsCgnatAddress,
    double PrimaryRttMs,
    double SecondaryRttMs,
    int RejectedSpoofedPackets,
    string DiagnosticMessage)
{
    public string? ExternalIpAddress => PrimaryMappedEndPoint?.Address.ToString() ?? SecondaryMappedEndPoint?.Address.ToString();

    public bool RequiresRelay =>
        Classification is NatBehaviorClassification.SymmetricOrCgnat or NatBehaviorClassification.UdpBlocked;
}

/// <summary>
///     Pure-C#, AOT-safe RFC 5389 / RFC 5780 STUN Binding Request/Response codec and 2-endpoint NAT behavior classifier.
///     Sends two Binding Requests from the same local UDP socket to distinct STUN endpoints to distinguish
///     OpenInternet, Endpoint-Independent Cone NAT, Endpoint-Dependent Symmetric NAT / RFC 6598 CGNAT, and UDP-Blocked networks.
/// </summary>
public static class StunNatClassifier
{
    public const int HeaderLengthBytes = 20;
    public const int TransactionIdLengthBytes = 12;
    public const ushort BindingRequestMessageType = 0x0001;
    public const ushort BindingSuccessResponseMessageType = 0x0101;
    public const ushort BindingErrorResponseMessageType = 0x0111;
    public const uint MagicCookie = 0x2112A442u;

    public const ushort MappedAddressAttributeType = 0x0001;
    public const ushort ErrorCodeAttributeType = 0x0009;
    public const ushort XorMappedAddressAttributeType = 0x0020;
    public const ushort SoftwareAttributeType = 0x8022;

    public const byte AddressFamilyIpv4 = 0x01;
    public const byte AddressFamilyIpv6 = 0x02;

    public const string DefaultPrimaryStunHost = "stun.l.google.com";
    public const int DefaultPrimaryStunPort = 19302;
    public const string DefaultSecondaryStunHost = "stun.cloudflare.com";
    public const int DefaultSecondaryStunPort = 3478;

    /// <summary>
    ///     Builds a 20-byte RFC 5389 STUN Binding Request with magic cookie 0x2112A442 and a 96-bit cryptographic Transaction ID.
    /// </summary>
    public static int BuildBindingRequest(Span<byte> destination, Span<byte> transactionIdOut)
    {
        if (destination.Length < HeaderLengthBytes)
        {
            throw new ArgumentException($"STUN Binding Request requires at least {HeaderLengthBytes} bytes.", nameof(destination));
        }

        if (transactionIdOut.Length < TransactionIdLengthBytes)
        {
            throw new ArgumentException($"STUN Transaction ID requires at least {TransactionIdLengthBytes} bytes.", nameof(transactionIdOut));
        }

        RandomNumberGenerator.Fill(transactionIdOut.Slice(0, TransactionIdLengthBytes));

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), BindingRequestMessageType);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), MagicCookie);
        transactionIdOut.Slice(0, TransactionIdLengthBytes).CopyTo(destination.Slice(8, TransactionIdLengthBytes));

        return HeaderLengthBytes;
    }

    /// <summary>
    ///     Builds an RFC 5389 STUN Binding Success Response (0x0101) carrying XOR-MAPPED-ADDRESS (0x0020)
    ///     and/or MAPPED-ADDRESS (0x0001) for IPv4 or IPv6 endpoints.
    /// </summary>
    public static int BuildBindingSuccessResponse(
        ReadOnlySpan<byte> transactionId,
        IPEndPoint mappedEndPoint,
        Span<byte> destination,
        bool useXorMappedAddress = true,
        bool includeMappedAddressFallback = false)
    {
        if (transactionId.Length < TransactionIdLengthBytes)
        {
            throw new ArgumentException($"Transaction ID must be at least {TransactionIdLengthBytes} bytes.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(mappedEndPoint);

        IPAddress address = mappedEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        bool isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
        bool isIpv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        if (!isIpv4 && !isIpv6)
        {
            throw new ArgumentException($"Unsupported address family '{address.AddressFamily}' for STUN response.", nameof(mappedEndPoint));
        }

        int attrValueLen = isIpv4 ? 8 : 20;
        int singleAttrTotalLen = 4 + attrValueLen;
        int attrCount = (useXorMappedAddress ? 1 : 0) + (includeMappedAddressFallback || !useXorMappedAddress ? 1 : 0);
        int payloadLen = singleAttrTotalLen * attrCount;
        int totalLen = HeaderLengthBytes + payloadLen;

        if (destination.Length < totalLen)
        {
            throw new ArgumentException($"Destination buffer ({destination.Length}B) too small for STUN response ({totalLen}B).", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), BindingSuccessResponseMessageType);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)payloadLen);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), MagicCookie);
        transactionId.Slice(0, TransactionIdLengthBytes).CopyTo(destination.Slice(8, TransactionIdLengthBytes));

        int offset = HeaderLengthBytes;
        if (useXorMappedAddress)
        {
            offset += WriteAddressAttribute(
                destination.Slice(offset),
                XorMappedAddressAttributeType,
                address,
                (ushort)mappedEndPoint.Port,
                xorMask: true,
                transactionId);
        }

        if (includeMappedAddressFallback || !useXorMappedAddress)
        {
            offset += WriteAddressAttribute(
                destination.Slice(offset),
                MappedAddressAttributeType,
                address,
                (ushort)mappedEndPoint.Port,
                xorMask: false,
                transactionId);
        }

        return offset;
    }

    /// <summary>
    ///     Validates and parses an RFC 5389 STUN Binding Response.
    ///     Enforces leading-zero bits, magic cookie 0x2112A442, exact constant-time 96-bit Transaction ID verification,
    ///     and prefers XOR-MAPPED-ADDRESS (0x0020) over MAPPED-ADDRESS (0x0001) for IPv4 and IPv6.
    /// </summary>
    public static bool TryParseBindingResponse(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> expectedTransactionId,
        out IPEndPoint? mappedEndPoint,
        out bool usedXorMappedAddress,
        out string? diagnosticReason)
    {
        mappedEndPoint = null;
        usedXorMappedAddress = false;
        diagnosticReason = null;

        if (expectedTransactionId.Length < TransactionIdLengthBytes)
        {
            diagnosticReason = "Expected Transaction ID is shorter than 12 bytes.";
            return false;
        }

        if (packet.Length < HeaderLengthBytes)
        {
            diagnosticReason = $"Packet too short ({packet.Length}B < {HeaderLengthBytes}B STUN header).";
            return false;
        }

        if ((packet[0] & 0xC0) != 0)
        {
            diagnosticReason = $"Invalid STUN top bits (0x{packet[0]:X2}); RFC 5389 requires top two bits 00.";
            return false;
        }

        ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(0, 2));
        ushort messageLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));

        if (cookie != MagicCookie)
        {
            diagnosticReason = $"STUN magic cookie mismatch (got 0x{cookie:X8}, expected 0x{MagicCookie:X8}).";
            return false;
        }

        if ((messageLength & 0x03) != 0)
        {
            diagnosticReason = $"Invalid STUN message length {messageLength} (not 4-byte aligned).";
            return false;
        }

        if (HeaderLengthBytes + messageLength > packet.Length)
        {
            diagnosticReason = $"Truncated STUN payload (declared {messageLength}B, available {packet.Length - HeaderLengthBytes}B).";
            return false;
        }

        ReadOnlySpan<byte> txId = packet.Slice(8, TransactionIdLengthBytes);
        if (!CryptographicOperations.FixedTimeEquals(txId, expectedTransactionId.Slice(0, TransactionIdLengthBytes)))
        {
            diagnosticReason = "Transaction ID mismatch (potential spoofed or stale STUN response).";
            return false;
        }

        if (messageType == BindingErrorResponseMessageType)
        {
            diagnosticReason = ParseErrorResponseSummary(packet.Slice(HeaderLengthBytes, messageLength));
            return false;
        }

        if (messageType != BindingSuccessResponseMessageType)
        {
            diagnosticReason = $"Unexpected STUN message type 0x{messageType:X4} (expected 0x{BindingSuccessResponseMessageType:X4}).";
            return false;
        }

        ReadOnlySpan<byte> attributes = packet.Slice(HeaderLengthBytes, messageLength);
        IPEndPoint? xorMapped = null;
        IPEndPoint? legacyMapped = null;

        int pos = 0;
        while (pos + 4 <= attributes.Length)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(pos, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(pos + 2, 2));
            pos += 4;

            if (pos + attrLen > attributes.Length)
            {
                diagnosticReason = $"Malformed STUN attribute 0x{attrType:X4} exceeding message bounds.";
                return false;
            }

            ReadOnlySpan<byte> attrValue = attributes.Slice(pos, attrLen);
            if (attrType == XorMappedAddressAttributeType &&
                TryParseAddressAttribute(attrValue, xorMask: true, txId, out IPEndPoint? parsedXor))
            {
                xorMapped = parsedXor;
            }
            else if (attrType == MappedAddressAttributeType &&
                     TryParseAddressAttribute(attrValue, xorMask: false, txId, out IPEndPoint? parsedLegacy))
            {
                legacyMapped = parsedLegacy;
            }

            int paddedLen = (attrLen + 3) & ~3;
            pos += paddedLen;
        }

        if (xorMapped is not null)
        {
            mappedEndPoint = xorMapped;
            usedXorMappedAddress = true;
            return true;
        }

        if (legacyMapped is not null)
        {
            mappedEndPoint = legacyMapped;
            usedXorMappedAddress = false;
            return true;
        }

        diagnosticReason = "STUN Binding Success Response contained neither XOR-MAPPED-ADDRESS nor MAPPED-ADDRESS.";
        return false;
    }

    /// <summary>
    ///     Returns true if the IPv4 address lies inside RFC 6598 Carrier-Grade NAT shared address space (100.64.0.0/10).
    /// </summary>
    public static bool IsRfc6598CgnatAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (normalized.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        if (!normalized.TryWriteBytes(octets, out int written) || written != 4)
        {
            return false;
        }

        return octets[0] == 100 && (octets[1] & 0xC0) == 64;
    }

    /// <summary>
    ///     Returns true if the address is RFC 6598 CGNAT, RFC 1918 private, or IPv4 link-local (indicating upstream NAT).
    /// </summary>
    public static bool IsCgnatOrPrivateAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (normalized.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        if (!normalized.TryWriteBytes(octets, out int written) || written != 4)
        {
            return false;
        }

        return octets[0] == 10 ||
               (octets[0] == 100 && (octets[1] & 0xC0) == 64) ||
               (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
               (octets[0] == 192 && octets[1] == 168) ||
               (octets[0] == 169 && octets[1] == 254);
    }

    /// <summary>
    ///     Classifies NAT mapping behavior from two STUN responses observed on the same bound local UDP socket.
    /// </summary>
    public static StunProbeResult ClassifyMapping(
        IPEndPoint localEndPoint,
        IPEndPoint? primaryMapped,
        IPEndPoint? secondaryMapped,
        bool primaryUsedXor,
        bool secondaryUsedXor,
        double primaryRttMs,
        double secondaryRttMs,
        int rejectedSpoofedPackets,
        IPAddress? localHostAddressOverride = null)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);

        if (primaryMapped is null && secondaryMapped is null)
        {
            return new StunProbeResult(
                NatBehaviorClassification.UdpBlocked,
                localEndPoint,
                null,
                null,
                UsedXorMappedAddress: false,
                IsCgnatAddress: false,
                primaryRttMs,
                secondaryRttMs,
                rejectedSpoofedPackets,
                "Neither STUN endpoint responded over UDP (outbound UDP blocked, firewall drop, or offline).");
        }

        IPEndPoint effectivePrimary = primaryMapped ?? secondaryMapped!;
        bool usedXor = primaryMapped is not null ? primaryUsedXor : secondaryUsedXor;
        bool isCgnat = IsRfc6598CgnatAddress(effectivePrimary.Address) ||
                       (secondaryMapped is not null && IsRfc6598CgnatAddress(secondaryMapped.Address));

        if (isCgnat)
        {
            return new StunProbeResult(
                NatBehaviorClassification.SymmetricOrCgnat,
                localEndPoint,
                primaryMapped,
                secondaryMapped,
                usedXor,
                IsCgnatAddress: true,
                primaryRttMs,
                secondaryRttMs,
                rejectedSpoofedPackets,
                $"STUN detected RFC 6598 Carrier-Grade NAT (CGNAT) external address {effectivePrimary} — UDP relay required.");
        }

        if (primaryMapped is not null && secondaryMapped is not null)
        {
            bool sameIp = NormalizeAddress(primaryMapped.Address).Equals(NormalizeAddress(secondaryMapped.Address));
            bool samePort = primaryMapped.Port == secondaryMapped.Port;

            if (!sameIp || !samePort)
            {
                return new StunProbeResult(
                    NatBehaviorClassification.SymmetricOrCgnat,
                    localEndPoint,
                    primaryMapped,
                    secondaryMapped,
                    primaryUsedXor || secondaryUsedXor,
                    IsCgnatAddress: false,
                    primaryRttMs,
                    secondaryRttMs,
                    rejectedSpoofedPackets,
                    $"Symmetric NAT detected (Endpoint-Dependent Mapping): EP1={primaryMapped} vs EP2={secondaryMapped} from local {localEndPoint} — UDP relay required.");
            }

            if (MatchesLocalEndpoint(localEndPoint, primaryMapped, localHostAddressOverride))
            {
                return new StunProbeResult(
                    NatBehaviorClassification.OpenInternet,
                    localEndPoint,
                    primaryMapped,
                    secondaryMapped,
                    primaryUsedXor || secondaryUsedXor,
                    IsCgnatAddress: false,
                    primaryRttMs,
                    secondaryRttMs,
                    rejectedSpoofedPackets,
                    $"Open Internet / Direct Public UDP detected: mapped {primaryMapped} matches local socket {localEndPoint}.");
            }

            return new StunProbeResult(
                NatBehaviorClassification.ConeNat,
                localEndPoint,
                primaryMapped,
                secondaryMapped,
                primaryUsedXor || secondaryUsedXor,
                IsCgnatAddress: false,
                primaryRttMs,
                secondaryRttMs,
                rejectedSpoofedPackets,
                $"Cone NAT (Endpoint-Independent Mapping) detected: both STUN servers saw {primaryMapped} (RTT {primaryRttMs:F1}ms / {secondaryRttMs:F1}ms). UPnP or STUN UDP hole-punching feasible.");
        }

        if (MatchesLocalEndpoint(localEndPoint, effectivePrimary, localHostAddressOverride))
        {
            return new StunProbeResult(
                NatBehaviorClassification.OpenInternet,
                localEndPoint,
                primaryMapped,
                secondaryMapped,
                usedXor,
                IsCgnatAddress: false,
                primaryRttMs,
                secondaryRttMs,
                rejectedSpoofedPackets,
                $"Single-endpoint STUN response matched local socket {localEndPoint} -> Open Internet.");
        }

        return new StunProbeResult(
            NatBehaviorClassification.Unknown,
            localEndPoint,
            primaryMapped,
            secondaryMapped,
            usedXor,
            IsCgnatAddress: false,
            primaryRttMs,
            secondaryRttMs,
            rejectedSpoofedPackets,
            $"Partial STUN probe: only one endpoint replied ({effectivePrimary}); secondary STUN timed out so Symmetric vs Cone NAT could not be confirmed.");
    }

    /// <summary>
    ///     Probes two explicit STUN endpoints from a single local UDP socket to classify NAT mapping behavior.
    /// </summary>
    public static async Task<StunProbeResult> ProbeNatAsync(
        IPEndPoint primaryServer,
        IPEndPoint secondaryServer,
        TimeSpan? timeout = null,
        int localPort = 0,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primaryServer);
        ArgumentNullException.ThrowIfNull(secondaryServer);

        TimeSpan perEndpointTimeout = timeout ?? TimeSpan.FromMilliseconds(1500);
        if (perEndpointTimeout < TimeSpan.FromMilliseconds(50))
        {
            perEndpointTimeout = TimeSpan.FromMilliseconds(50);
        }

        bool primaryIsV6 = primaryServer.AddressFamily == AddressFamily.InterNetworkV6 && !primaryServer.Address.IsIPv4MappedToIPv6;
        bool secondaryIsV6 = secondaryServer.AddressFamily == AddressFamily.InterNetworkV6 && !secondaryServer.Address.IsIPv4MappedToIPv6;
        bool useIpv6Socket = primaryIsV6 || secondaryIsV6;
        bool dualMode = primaryIsV6 != secondaryIsV6;

        using Socket socket = new Socket(
            useIpv6Socket ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);

        if (useIpv6Socket && dualMode)
        {
            socket.DualMode = true;
        }

        DisableWindowsUdpConnReset(socket, logSink);

        IPEndPoint bindEndpoint = new IPEndPoint(
            useIpv6Socket ? IPAddress.IPv6Any : IPAddress.Any,
            localPort is >= 1 and <= 65535 ? localPort : 0);

        try
        {
            socket.Bind(bindEndpoint);
        }
        catch (SocketException ex)
        {
            Log(logSink, $"[P2P-STUN] Failed to bind local UDP port {bindEndpoint.Port} ({ex.SocketErrorCode}); retrying ephemeral port 0.");
            socket.Bind(new IPEndPoint(useIpv6Socket ? IPAddress.IPv6Any : IPAddress.Any, 0));
        }

        IPEndPoint localEp = (IPEndPoint)socket.LocalEndPoint!;
        Log(logSink, $"[P2P-STUN] Starting 2-endpoint RFC 5389/5780 NAT probe on local UDP {localEp} -> EP1={primaryServer}, EP2={secondaryServer} (timeout={perEndpointTimeout.TotalMilliseconds:F0}ms/ep).");

        (IPEndPoint? mapped1, bool xor1, double rtt1, int spoofed1) = await QuerySingleEndpointAsync(
            socket,
            PrepareTarget(socket, primaryServer),
            "EP1",
            perEndpointTimeout,
            logSink,
            cancellationToken).ConfigureAwait(false);

        (IPEndPoint? mapped2, bool xor2, double rtt2, int spoofed2) = await QuerySingleEndpointAsync(
            socket,
            PrepareTarget(socket, secondaryServer),
            "EP2",
            perEndpointTimeout,
            logSink,
            cancellationToken).ConfigureAwait(false);

        StunProbeResult result = ClassifyMapping(
            localEp,
            mapped1,
            mapped2,
            xor1,
            xor2,
            rtt1,
            rtt2,
            spoofed1 + spoofed2);

        Log(logSink, $"[P2P-STUN] Classification={result.Classification} | EP1={result.PrimaryMappedEndPoint?.ToString() ?? "timeout"} ({result.PrimaryRttMs:F1}ms) | EP2={result.SecondaryMappedEndPoint?.ToString() ?? "timeout"} ({result.SecondaryRttMs:F1}ms) | XOR={result.UsedXorMappedAddress} | CGNAT={result.IsCgnatAddress} | SpoofedRejected={result.RejectedSpoofedPackets} | {result.DiagnosticMessage}");
        return result;
    }

    /// <summary>
    ///     Resolves two STUN hostnames via DNS and executes a 2-endpoint NAT behavior probe.
    /// </summary>
    public static async Task<StunProbeResult> ProbeNatWithHostnamesAsync(
        string primaryHost = DefaultPrimaryStunHost,
        int primaryPort = DefaultPrimaryStunPort,
        string secondaryHost = DefaultSecondaryStunHost,
        int secondaryPort = DefaultSecondaryStunPort,
        TimeSpan? timeout = null,
        int localPort = 0,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMilliseconds(1500);
        Log(logSink, $"[P2P-STUN] Resolving STUN endpoints '{primaryHost}:{primaryPort}' and '{secondaryHost}:{secondaryPort}'...");

        IPEndPoint? primaryEp = await ResolveHostEndPointAsync(primaryHost, primaryPort, effectiveTimeout, logSink, cancellationToken).ConfigureAwait(false);
        IPEndPoint? secondaryEp = await ResolveHostEndPointAsync(secondaryHost, secondaryPort, effectiveTimeout, logSink, cancellationToken).ConfigureAwait(false);

        if (primaryEp is null || secondaryEp is null)
        {
            string msg = $"Unable to resolve STUN hostnames (EP1={primaryEp?.ToString() ?? "DNS-failed"}, EP2={secondaryEp?.ToString() ?? "DNS-failed"}). Network offline or DNS blocked.";
            Log(logSink, $"[P2P-STUN] [WARN] {msg}");
            return new StunProbeResult(
                NatBehaviorClassification.UdpBlocked,
                LocalEndPoint: null,
                PrimaryMappedEndPoint: null,
                SecondaryMappedEndPoint: null,
                UsedXorMappedAddress: false,
                IsCgnatAddress: false,
                PrimaryRttMs: 0,
                SecondaryRttMs: 0,
                RejectedSpoofedPackets: 0,
                DiagnosticMessage: msg);
        }

        return await ProbeNatAsync(primaryEp, secondaryEp, effectiveTimeout, localPort, logSink, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IPEndPoint? Mapped, bool UsedXor, double RttMs, int RejectedSpoofed)> QuerySingleEndpointAsync(
        Socket socket,
        IPEndPoint serverEndPoint,
        string label,
        TimeSpan timeout,
        Action<string>? logSink,
        CancellationToken cancellationToken)
    {
        byte[] requestBuffer = new byte[HeaderLengthBytes];
        byte[] transactionId = new byte[TransactionIdLengthBytes];
        BuildBindingRequest(requestBuffer, transactionId);

        string txHex = Convert.ToHexString(transactionId, 0, 4);
        byte[] receiveBuffer = new byte[1500];
        EndPoint anyRemote = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        int rejectedSpoofed = 0;
        const int maxAttempts = 2;
        TimeSpan attemptBudget = TimeSpan.FromMilliseconds(Math.Max(50, timeout.TotalMilliseconds / maxAttempts));

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await socket.SendToAsync(requestBuffer, SocketFlags.None, serverEndPoint, cancellationToken).ConfigureAwait(false);
                Log(logSink, $"[P2P-STUN] [{label}] Sent Binding Request (attempt {attempt}/{maxAttempts}, tx=0x{txHex}..., {requestBuffer.Length}B) -> {serverEndPoint}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(logSink, $"[P2P-STUN] [{label}] SendTo failed to {serverEndPoint} ({ex.GetType().Name}: {ex.Message}).");
                return (null, false, sw.Elapsed.TotalMilliseconds, rejectedSpoofed);
            }

            using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(attemptBudget);

            while (!attemptCts.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(receiveBuffer, SocketFlags.None, anyRemote, attemptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log(logSink, $"[P2P-STUN] [{label}] Attempt {attempt}/{maxAttempts} timed out after {sw.Elapsed.TotalMilliseconds:F1}ms waiting for {serverEndPoint}.");
                    break;
                }
                catch (SocketException ex)
                {
                    Log(logSink, $"[P2P-STUN] [{label}] Socket receive error ({ex.SocketErrorCode}): {ex.Message}");
                    break;
                }

                ReadOnlySpan<byte> datagram = receiveBuffer.AsSpan(0, received.ReceivedBytes);
                if (TryParseBindingResponse(datagram, transactionId, out IPEndPoint? mapped, out bool usedXor, out string? reason))
                {
                    double rttMs = sw.Elapsed.TotalMilliseconds;
                    Log(logSink, $"[P2P-STUN] [{label}] Valid Binding Response from {received.RemoteEndPoint} in {rttMs:F1}ms -> mapped={mapped} (attr={(usedXor ? "XOR-MAPPED-ADDRESS" : "MAPPED-ADDRESS")})");
                    return (mapped, usedXor, rttMs, rejectedSpoofed);
                }

                rejectedSpoofed++;
                Log(logSink, $"[P2P-STUN] [{label}] [SECURITY] Rejected invalid/spoofed datagram ({received.ReceivedBytes}B) from {received.RemoteEndPoint}: {reason}");
            }
        }

        return (null, false, timeout.TotalMilliseconds, rejectedSpoofed);
    }

    private static async Task<IPEndPoint?> ResolveHostEndPointAsync(
        string host,
        int port,
        TimeSpan timeout,
        Action<string>? logSink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return null;
        }

        string trimmed = host.Trim();
        if (IPAddress.TryParse(trimmed, out IPAddress? literal))
        {
            return new IPEndPoint(literal, port);
        }

        try
        {
            using CancellationTokenSource dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsCts.CancelAfter(timeout);
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(trimmed, dnsCts.Token).ConfigureAwait(false);

            IPAddress? selected = null;
            for (int i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    selected = addresses[i];
                    break;
                }
            }

            selected ??= addresses.Length > 0 ? addresses[0] : null;
            if (selected is not null)
            {
                Log(logSink, $"[P2P-STUN] DNS resolved '{trimmed}:{port}' -> {selected}:{port}");
                return new IPEndPoint(selected, port);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(logSink, $"[P2P-STUN] DNS lookup failed for '{trimmed}:{port}' ({ex.GetType().Name}: {ex.Message}).");
        }

        return null;
    }

    private static int WriteAddressAttribute(
        Span<byte> destination,
        ushort attrType,
        IPAddress address,
        ushort port,
        bool xorMask,
        ReadOnlySpan<byte> transactionId)
    {
        bool isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
        ushort attrLen = (ushort)(isIpv4 ? 8 : 20);

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), attrType);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), attrLen);
        destination[4] = 0x00;
        destination[5] = isIpv4 ? AddressFamilyIpv4 : AddressFamilyIpv6;

        ushort encodedPort = xorMask
            ? (ushort)(port ^ (ushort)(MagicCookie >> 16))
            : port;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(6, 2), encodedPort);

        if (isIpv4)
        {
            Span<byte> raw = stackalloc byte[4];
            address.TryWriteBytes(raw, out _);
            if (xorMask)
            {
                Span<byte> cookieBytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(cookieBytes, MagicCookie);
                for (int i = 0; i < 4; i++)
                {
                    destination[8 + i] = (byte)(raw[i] ^ cookieBytes[i]);
                }
            }
            else
            {
                raw.CopyTo(destination.Slice(8, 4));
            }
        }
        else
        {
            Span<byte> raw = stackalloc byte[16];
            address.TryWriteBytes(raw, out _);
            if (xorMask)
            {
                Span<byte> mask = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(mask.Slice(0, 4), MagicCookie);
                transactionId.Slice(0, TransactionIdLengthBytes).CopyTo(mask.Slice(4, 12));
                for (int i = 0; i < 16; i++)
                {
                    destination[8 + i] = (byte)(raw[i] ^ mask[i]);
                }
            }
            else
            {
                raw.CopyTo(destination.Slice(8, 16));
            }
        }

        return 4 + attrLen;
    }

    private static bool TryParseAddressAttribute(
        ReadOnlySpan<byte> value,
        bool xorMask,
        ReadOnlySpan<byte> transactionId,
        out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (value.Length < 8)
        {
            return false;
        }

        byte family = value[1];
        ushort rawPort = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        int port = xorMask ? rawPort ^ (int)(MagicCookie >> 16) : rawPort;
        if (port is < 0 or > 65535)
        {
            return false;
        }

        if (family == AddressFamilyIpv4)
        {
            if (value.Length < 8)
            {
                return false;
            }

            Span<byte> ipBytes = stackalloc byte[4];
            if (xorMask)
            {
                Span<byte> cookieBytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(cookieBytes, MagicCookie);
                for (int i = 0; i < 4; i++)
                {
                    ipBytes[i] = (byte)(value[4 + i] ^ cookieBytes[i]);
                }
            }
            else
            {
                value.Slice(4, 4).CopyTo(ipBytes);
            }

            endpoint = new IPEndPoint(new IPAddress(ipBytes), port);
            return true;
        }

        if (family == AddressFamilyIpv6)
        {
            if (value.Length < 20)
            {
                return false;
            }

            Span<byte> ipBytes = stackalloc byte[16];
            if (xorMask)
            {
                Span<byte> mask = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(mask.Slice(0, 4), MagicCookie);
                transactionId.Slice(0, TransactionIdLengthBytes).CopyTo(mask.Slice(4, 12));
                for (int i = 0; i < 16; i++)
                {
                    ipBytes[i] = (byte)(value[4 + i] ^ mask[i]);
                }
            }
            else
            {
                value.Slice(4, 16).CopyTo(ipBytes);
            }

            endpoint = new IPEndPoint(new IPAddress(ipBytes), port);
            return true;
        }

        return false;
    }

    private static string ParseErrorResponseSummary(ReadOnlySpan<byte> attributes)
    {
        int pos = 0;
        while (pos + 4 <= attributes.Length)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(pos, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(pos + 2, 2));
            pos += 4;
            if (pos + attrLen > attributes.Length)
            {
                break;
            }

            if (attrType == ErrorCodeAttributeType && attrLen >= 4)
            {
                ReadOnlySpan<byte> errVal = attributes.Slice(pos, attrLen);
                int errorClass = errVal[2] & 0x07;
                int errorNumber = errVal[3];
                int code = (errorClass * 100) + errorNumber;
                string reasonPhrase = attrLen > 4 ? Encoding.UTF8.GetString(errVal.Slice(4)).Trim() : string.Empty;
                return string.IsNullOrEmpty(reasonPhrase)
                    ? $"STUN Binding Error Response {code}."
                    : $"STUN Binding Error Response {code} ({reasonPhrase}).";
            }

            pos += (attrLen + 3) & ~3;
        }

        return "STUN Binding Error Response (0x0111) without ERROR-CODE attribute.";
    }

    private static bool MatchesLocalEndpoint(
        IPEndPoint localEndPoint,
        IPEndPoint mappedEndPoint,
        IPAddress? localHostAddressOverride)
    {
        if (localEndPoint.Port != mappedEndPoint.Port)
        {
            return false;
        }

        IPAddress normalizedMapped = NormalizeAddress(mappedEndPoint.Address);
        IPAddress normalizedLocal = NormalizeAddress(localEndPoint.Address);

        if (localHostAddressOverride is not null)
        {
            return NormalizeAddress(localHostAddressOverride).Equals(normalizedMapped);
        }

        if (!normalizedLocal.Equals(IPAddress.Any) && !normalizedLocal.Equals(IPAddress.IPv6Any))
        {
            return normalizedLocal.Equals(normalizedMapped);
        }

        if (IPAddress.IsLoopback(normalizedMapped))
        {
            return true;
        }

        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties props = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation unicast in props.UnicastAddresses)
                {
                    if (NormalizeAddress(unicast.Address).Equals(normalizedMapped))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2P-STUN] Local NIC address inspection notice: {ex.Message}");
        }

        return false;
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IPEndPoint PrepareTarget(Socket socket, IPEndPoint target)
    {
        if (socket.AddressFamily == AddressFamily.InterNetworkV6 &&
            target.AddressFamily == AddressFamily.InterNetwork)
        {
            return new IPEndPoint(target.Address.MapToIPv6(), target.Port);
        }

        return target;
    }

    private static void DisableWindowsUdpConnReset(Socket socket, Action<string>? logSink)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            const int sioUdpConnReset = unchecked((int)0x9800000C);
            socket.IOControl(sioUdpConnReset, [0x00, 0x00, 0x00, 0x00], null);
        }
        catch (Exception ex)
        {
            Log(logSink, $"[P2P-STUN] SIO_UDP_CONNRESET ioctl notice: {ex.Message}");
        }
    }

    private static void Log(Action<string>? logSink, string message)
    {
        Trace.WriteLine(message);
        logSink?.Invoke(message);
    }
}
