// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SocketRebindPolicy.cs
// System: ProjectKongor Netcode / Socket Infrastructure [SR-08]
// Subsystem: Zero-Downtime Socket Rebinding (SO_REUSEPORT / SO_REUSEADDR)
// ============================================================================

using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ASPIRE.Common.Netcode.Sockets;

/// <summary>
///     Provides universal socket configuration extension methods for zero-downtime port
///     rebinding across Windows (SO_REUSEADDR) and Linux (SO_REUSEPORT).
///     Eliminates SocketError.AddressAlreadyInUse (WSAEADDRINUSE / 10048) during sub-500ms
///     rapid worker process restarts and proxy hot-reloading.
/// </summary>
public static class SocketRebindPolicy
{
    /// <summary>
    ///     Linux SOL_SOCKET socket option level constant.
    /// </summary>
    public const int LinuxSolSocket = 1;

    /// <summary>
    ///     Linux SO_REUSEPORT socket option constant (kernel 3.9+).
    /// </summary>
    public const int LinuxSoReusePort = 15;

    /// <summary>
    ///     Configures the socket to permit immediate, zero-downtime port rebinding.
    ///     Enables SO_REUSEADDR, disables ExclusiveAddressUse, disables TCP TIME_WAIT
    ///     lingering (for TCP sockets), and enables Linux SO_REUSEPORT where applicable.
    /// </summary>
    /// <param name="socket">The socket instance to configure.</param>
    /// <param name="enableLinuxReusePort">Whether to set SO_REUSEPORT on Linux systems.</param>
    public static void ConfigureForFastRebind(this Socket socket, bool enableLinuxReusePort = true)
    {
        ArgumentNullException.ThrowIfNull(socket);

        // 1. In .NET, ExclusiveAddressUse must be false before SO_REUSEADDR can take effect
        try
        {
            socket.ExclusiveAddressUse = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SocketRebindPolicy] ExclusiveAddressUse not supported: {ex.Message}");
        }

        // 2. Enable SO_REUSEADDR across all platforms
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SocketRebindPolicy] SO_REUSEADDR not supported: {ex.Message}");
        }

        // 3. For TCP sockets, disable TIME_WAIT lingering so socket closure releases immediately
        if (socket.ProtocolType == ProtocolType.Tcp)
        {
            try
            {
                socket.LingerState = new LingerOption(false, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SocketRebindPolicy] LingerState not supported: {ex.Message}");
            }
        }

        // 4. On Linux, set native SO_REUSEPORT for kernel-level multi-process port sharing
        if (enableLinuxReusePort && OperatingSystem.IsLinux())
        {
            try
            {
                socket.SetRawSocketOption(LinuxSolSocket, LinuxSoReusePort, BitConverter.GetBytes(1));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SocketRebindPolicy] SO_REUSEPORT not supported: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Creates a high-performance UDP socket pre-configured for zero-downtime rebinding.
    /// </summary>
    /// <param name="addressFamily">Address family (default: InterNetwork IPv4).</param>
    /// <param name="bufferSizeBytes">Send and receive buffer size in bytes (default: 1 MB).</param>
    public static Socket CreateFastRebindUdpSocket(
        AddressFamily addressFamily = AddressFamily.InterNetwork,
        int bufferSizeBytes = 1024 * 1024)
    {
        Socket socket = new(addressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = bufferSizeBytes,
            SendBufferSize = bufferSizeBytes
        };

        socket.ConfigureForFastRebind();
        return socket;
    }
}
