// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeSocketTable.cs
// System: ProjectKongor Netcode / Socket Infrastructure [SR-08]
// Subsystem: Kernel Socket Ownership Discovery (iphlpapi / /proc/net)
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ASPIRE.Common.Netcode.Sockets;

/// <summary>
///     Queries operating system kernel socket tables to resolve the exact process ID (PID)
///     currently holding or binding any UDP or TCP port.
///     Supports Win32 GetExtendedUdpTable / GetExtendedTcpTable (iphlpapi.dll) on Windows,
///     and /proc/net pseudo-filesystem parsing on Linux.
/// </summary>
public static class NativeSocketTable
{
    private const uint AfInet = 2;   // AF_INET (IPv4)
    private const uint AfInet6 = 23; // AF_INET6 (IPv6)

    private const int UdpTableOwnerPid = 1;      // UDP_TABLE_OWNER_PID
    private const int TcpTableOwnerPidAll = 5;   // TCP_TABLE_OWNER_PID_ALL

    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        uint ulAf,
        int tableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        uint ulAf,
        int tableClass,
        uint reserved = 0);

    /// <summary>
    ///     Attempts to identify the exact operating system process ID (PID) holding the specified port.
    /// </summary>
    /// <param name="port">The local port to query (1-65535).</param>
    /// <param name="protocol">Protocol type (UDP or TCP, default: UDP).</param>
    /// <returns>The owning PID if found; otherwise null if the port is unoccupied or query unsupported.</returns>
    public static int? TryGetPortOwnerPid(int port, ProtocolType protocol = ProtocolType.Udp)
    {
        if (port <= 0 || port > 65535) return null;

        if (OperatingSystem.IsWindows())
        {
            return TryGetPortOwnerPidWindows(port, protocol);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetPortOwnerPidLinux(port, protocol);
        }

        return null;
    }

    /// <summary>
    ///     Retrieves a snapshot of all active local ports and their owning PIDs.
    /// </summary>
    public static Dictionary<int, int> GetAllPortOwners(ProtocolType protocol = ProtocolType.Udp)
    {
        Dictionary<int, int> results = new();

        if (OperatingSystem.IsWindows())
        {
            CollectWindowsPortOwners(results, protocol, AfInet);
            CollectWindowsPortOwners(results, protocol, AfInet6);
        }
        else if (OperatingSystem.IsLinux())
        {
            CollectLinuxPortOwners(results, protocol);
        }

        return results;
    }

    #region Windows Win32 iphlpapi Implementation

    private static int? TryGetPortOwnerPidWindows(int port, ProtocolType protocol)
    {
        // 1. Check IPv4 table first
        int? pid = QueryWindowsPortOwner(port, protocol, AfInet);
        if (pid.HasValue) return pid;

        // 2. Check IPv6 table
        return QueryWindowsPortOwner(port, protocol, AfInet6);
    }

    private static int? QueryWindowsPortOwner(int port, ProtocolType protocol, uint addressFamily)
    {
        IntPtr buffer = IntPtr.Zero;
        int size = 0;

        try
        {
            // Initial probe to determine required buffer size
            uint ret = protocol == ProtocolType.Udp
                ? GetExtendedUdpTable(IntPtr.Zero, ref size, false, addressFamily, UdpTableOwnerPid)
                : GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidAll);

            if (ret != ErrorInsufficientBuffer || size <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(size);
            ret = protocol == ProtocolType.Udp
                ? GetExtendedUdpTable(buffer, ref size, false, addressFamily, UdpTableOwnerPid)
                : GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidAll);

            if (ret != NoError)
            {
                return null;
            }

            int numEntries = Marshal.ReadInt32(buffer);
            if (numEntries <= 0) return null;

            IntPtr currentPtr = IntPtr.Add(buffer, 4);

            if (protocol == ProtocolType.Udp)
            {
                int entrySize = addressFamily == AfInet ? 12 : 28;
                int portOffset = addressFamily == AfInet ? 4 : 20;
                int pidOffset = addressFamily == AfInet ? 8 : 24;

                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr entry = IntPtr.Add(currentPtr, i * entrySize);
                    ushort rawPort = unchecked((ushort)Marshal.ReadInt32(entry, portOffset));
                    int localPort = BinaryPrimitives.ReverseEndianness(rawPort);

                    if (localPort == port)
                    {
                        return Marshal.ReadInt32(entry, pidOffset);
                    }
                }
            }
            else // TCP
            {
                int entrySize = addressFamily == AfInet ? 24 : 56;
                int portOffset = addressFamily == AfInet ? 8 : 24;
                int pidOffset = addressFamily == AfInet ? 20 : 52;

                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr entry = IntPtr.Add(currentPtr, i * entrySize);
                    ushort rawPort = unchecked((ushort)Marshal.ReadInt32(entry, portOffset));
                    int localPort = BinaryPrimitives.ReverseEndianness(rawPort);

                    if (localPort == port)
                    {
                        return Marshal.ReadInt32(entry, pidOffset);
                    }
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static void CollectWindowsPortOwners(Dictionary<int, int> dict, ProtocolType protocol, uint addressFamily)
    {
        IntPtr buffer = IntPtr.Zero;
        int size = 0;

        try
        {
            uint ret = protocol == ProtocolType.Udp
                ? GetExtendedUdpTable(IntPtr.Zero, ref size, false, addressFamily, UdpTableOwnerPid)
                : GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidAll);

            if (ret != ErrorInsufficientBuffer || size <= 0) return;

            buffer = Marshal.AllocHGlobal(size);
            ret = protocol == ProtocolType.Udp
                ? GetExtendedUdpTable(buffer, ref size, false, addressFamily, UdpTableOwnerPid)
                : GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidAll);

            if (ret != NoError) return;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr currentPtr = IntPtr.Add(buffer, 4);

            if (protocol == ProtocolType.Udp)
            {
                int entrySize = addressFamily == AfInet ? 12 : 28;
                int portOffset = addressFamily == AfInet ? 4 : 20;
                int pidOffset = addressFamily == AfInet ? 8 : 24;

                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr entry = IntPtr.Add(currentPtr, i * entrySize);
                    ushort rawPort = unchecked((ushort)Marshal.ReadInt32(entry, portOffset));
                    int localPort = BinaryPrimitives.ReverseEndianness(rawPort);
                    int pid = Marshal.ReadInt32(entry, pidOffset);
                    dict[localPort] = pid;
                }
            }
            else // TCP
            {
                int entrySize = addressFamily == AfInet ? 24 : 56;
                int portOffset = addressFamily == AfInet ? 8 : 24;
                int pidOffset = addressFamily == AfInet ? 20 : 52;

                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr entry = IntPtr.Add(currentPtr, i * entrySize);
                    ushort rawPort = unchecked((ushort)Marshal.ReadInt32(entry, portOffset));
                    int localPort = BinaryPrimitives.ReverseEndianness(rawPort);
                    int pid = Marshal.ReadInt32(entry, pidOffset);
                    dict[localPort] = pid;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NativeSocketTable] CollectWindowsPortOwners failed: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    #endregion

    #region Linux /proc/net Implementation

    private static int? TryGetPortOwnerPidLinux(int port, ProtocolType protocol)
    {
        string path = protocol == ProtocolType.Udp ? "/proc/net/udp" : "/proc/net/tcp";
        string path6 = protocol == ProtocolType.Udp ? "/proc/net/udp6" : "/proc/net/tcp6";

        string? inode = FindInodeForPortLinux(path, port) ?? FindInodeForPortLinux(path6, port);
        if (string.IsNullOrEmpty(inode)) return null;

        return FindPidForSocketInodeLinux(inode);
    }

    private static string? FindInodeForPortLinux(string filePath, int port)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            string hexPort = port.ToString("X4");
            using StreamReader reader = new(filePath);
            string? line = reader.ReadLine(); // Skip header
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 9)
                {
                    string localAddress = parts[1];
                    int colonIndex = localAddress.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        string lineHexPort = localAddress[(colonIndex + 1)..];
                        if (lineHexPort.Equals(hexPort, StringComparison.OrdinalIgnoreCase))
                        {
                            return parts[9]; // Inode
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NativeSocketTable] FindInodeForPortLinux failed for {filePath}: {ex.Message}");
        }

        return null;
    }

    private static int? FindPidForSocketInodeLinux(string targetInode)
    {
        try
        {
            string targetLink = $"socket:[{targetInode}]";
            string[] procDirs = Directory.GetDirectories("/proc");

            foreach (string procDir in procDirs)
            {
                string dirName = Path.GetFileName(procDir);
                if (int.TryParse(dirName, out int pid))
                {
                    string fdDir = Path.Combine(procDir, "fd");
                    if (Directory.Exists(fdDir))
                    {
                        try
                        {
                            string[] fds = Directory.GetFiles(fdDir);
                            foreach (string fd in fds)
                            {
                                try
                                {
                                    string? linkTarget = File.ResolveLinkTarget(fd, false)?.FullName;
                                    if (linkTarget != null && linkTarget.Contains(targetLink, StringComparison.Ordinal))
                                    {
                                        return pid;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[NativeSocketTable] ResolveLinkTarget failed for {fd}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[NativeSocketTable] GetFiles failed for {fdDir}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NativeSocketTable] FindPidForSocketInodeLinux failed: {ex.Message}");
        }

        return null;
    }

    private static void CollectLinuxPortOwners(Dictionary<int, int> dict, ProtocolType protocol)
    {
        string path = protocol == ProtocolType.Udp ? "/proc/net/udp" : "/proc/net/tcp";
        if (!File.Exists(path)) return;

        try
        {
            using StreamReader reader = new(path);
            string? line = reader.ReadLine();
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 9)
                {
                    string localAddress = parts[1];
                    int colon = localAddress.IndexOf(':');
                    if (colon >= 0 && ushort.TryParse(localAddress[(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out ushort port))
                    {
                        string inode = parts[9];
                        int? pid = FindPidForSocketInodeLinux(inode);
                        if (pid.HasValue)
                        {
                            dict[port] = pid.Value;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NativeSocketTable] CollectLinuxPortOwners failed: {ex.Message}");
        }
    }

    #endregion
}
