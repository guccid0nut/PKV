// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Netcode.Sockets;
using COMPEL.Services.Proxy;

namespace COMPEL.Services.Ping;

/// <summary>
///     Answers the master server's "server list" pings on COMPEL's ping port, faithfully reproducing the legacy COMPEL's
///     UDP responder.
///     A ping request is 46 bytes carrying the marker <c>0xCA</c> at byte 43; the response advertises the server name and
///     version and echoes the request's two challenge bytes so each pong is distinct.
///     The advertised version comes from the synchronised distribution's manifest and is rebuilt whenever it changes (for
///     example after an on-demand synchronisation), rather than being fixed for the lifetime of the process.
/// </summary>
public sealed class UDPPingResponder(
    IOptions<MatchServerManagerOptions> options,
    DistributionSynchronisationService distribution,
    PortPlan ports,
    ILogger<UDPPingResponder> logger) : ResilientBackgroundService(logger, nameof(UDPPingResponder))
{
    private const byte UnreliableFlag = 0x01;
    private const byte PongMessageType = 0x66;
    private const byte PingMarker = 0xCA;
    private const int RequestLength = 46;
    private readonly DistributionSynchronisationService distribution = distribution;
    private readonly ILogger<UDPPingResponder> logger = logger;

    private readonly MatchServerManagerOptions options = options.Value;
    private readonly PortPlan ports = ports;
    private readonly UdpRateLimiter pingRateLimiter = new(
        refillRatePerSecond: 5.0,
        burstCapacity: 10.0);

    /// <summary>
    ///     Whether the ping responder has successfully bound its UDP port. <see langword="null" /> before the first bind
    ///     attempt completes, so a health check can distinguish "not yet started" from "failed to bind".
    /// </summary>
    public bool? IsBound { get; private set; }

    internal bool TryAllowPingResponse(IPAddress remoteAddress) => pingRateLimiter.ShouldAllow(remoteAddress);

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        // Wait Until The Distribution Is Ready So The Pong Advertises The Correct Version.
        try
        {
            await distribution.WaitUntilReady(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        int port = ports.PingPort;

        string? templateVersion = distribution.DistributionVersion;
        byte[] response = BuildResponseTemplate(options.ServerNamePrefix, templateVersion);

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ConfigureForFastRebind();

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            IsBound = true;
        }
        catch (Exception exception)
        {
            IsBound = false;

            logger.LogError(exception, "Failed To Bind The Ping Responder To UDP Port {Port}", port);

            return;
        }

        logger.LogInformation("Answering Master-Server Pings On UDP Port {Port}", port);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1460);
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (stoppingToken.IsCancellationRequested is false)
            {
                SocketReceiveFromResult result;

                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Ping Receive Failed");

                    continue;
                }

                if (result.ReceivedBytes != RequestLength || buffer[43] != PingMarker)
                    continue;

                if (result.RemoteEndPoint is IPEndPoint remoteEndPoint &&
                    !TryAllowPingResponse(remoteEndPoint.Address))
                {
                    logger.LogDebug(
                        "[SECURITY-ALERT] Rate-limited UDP 0xCA ping flood from {RemoteAddress} on port {Port}",
                        remoteEndPoint.Address,
                        port);
                    continue;
                }

                // Rebuild The Template Whenever The Distribution Version Has Changed Since It Was Last Built, So An On-Demand Synchronisation Is Reflected In Subsequent Pongs Instead Of Being Baked In Forever.
                if (distribution.DistributionVersion != templateVersion)
                {
                    templateVersion = distribution.DistributionVersion;
                    response = BuildResponseTemplate(options.ServerNamePrefix, templateVersion);
                }

                // Echo The Challenge Bytes So Each Pong Is Distinct.
                response[44] = buffer[44];
                response[45] = buffer[45];

                try
                {
                    await socket.SendToAsync(response, SocketFlags.None, result.RemoteEndPoint, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Ping Response Send Failed");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static byte[] BuildResponseTemplate(string serverName, string? version)
    {
        byte[] serverNameBytes = Encoding.UTF8.GetBytes(serverName);

        byte[] versionBytes = Encoding.UTF8.GetBytes(version ?? string.Empty);
        int versionLength = Math.Min(versionBytes.Length, 12);

        // The Trailing Bytes Beyond The Version Are Part Of The Wire Format And Are Left Zeroed, As In The Original Responder.
        byte[] response = new byte[69 + serverNameBytes.Length + versionLength];

        response[42] = UnreliableFlag;
        response[43] = PongMessageType;

        Array.Copy(serverNameBytes, 0, response, 46, serverNameBytes.Length);
        Array.Copy(versionBytes, 0, response, 50 + serverNameBytes.Length, versionLength);

        return response;
    }
}
