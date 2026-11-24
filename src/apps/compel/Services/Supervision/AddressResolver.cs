// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves the server's own advertised address and the master server endpoint from the configured gateway,
///     reproducing the legacy COMPEL's address logic.
///     The public master server is the Project KONGOR gateway; a gateway of localhost routes to a local master server,
///     which is how the manager is pointed at a local NEXUS instance.
/// </summary>
public sealed class AddressResolver(IOptions<MatchServerManagerOptions> manager, ILogger<AddressResolver> logger)
{
    private const string PublicMasterServerAddress = "masterserver.newbs.io";
    private const string LocalMasterServerAddress = "127.0.0.1";
    private const int LocalMasterServerPort = 55555;

    private static readonly string[] PublicIPServices =
    [
        "https://ipv4.icanhazip.com",
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://checkip.amazonaws.com"
    ];

    private readonly ILogger<AddressResolver> logger = logger;

    private readonly MatchServerManagerOptions manager = manager.Value;

    private string? cachedServerAddress;

    private bool GatewayIsLocalhost =>
        manager.Gateway.ToUpperInvariant() is "LOCALHOST" or "127.0.0.1" or "0.0.0.0" or "AUTO" or "LAN";

    /// <summary>
    ///     Detects whether COMPEL is currently executing inside a Docker / OCI container.
    /// </summary>
    public static bool IsRunningInContainer =>
        string.Equals(Environment.GetEnvironmentVariable("COMPEL_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
        File.Exists("/.dockerenv");

    /// <summary>
    ///     Normalizes loopback host addresses to host.docker.internal when running in a container
    ///     so that containerized dedicated server workers can communicate with the host machine.
    /// </summary>
    public static string NormalizeHostForContainer(string hostAndPort, bool isContainer)
    {
        if (!isContainer)
            return hostAndPort;

        if (hostAndPort.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal:" + hostAndPort["127.0.0.1:".Length..];
        if (hostAndPort.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal";
        if (hostAndPort.StartsWith("0.0.0.0:", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal:" + hostAndPort["0.0.0.0:".Length..];
        if (hostAndPort.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal";
        if (hostAndPort.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal:" + hostAndPort["localhost:".Length..];
        if (hostAndPort.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal";

        return hostAndPort;
    }

    /// <summary>
    ///     Resolves a hostname for container-to-host communication, prioritizing IPv4 to bypass unreachable IPv6 ULA routes.
    /// </summary>
    public static string ResolveContainerHost(string fallbackHost = "host.docker.internal", string? port = null)
    {
        string host = fallbackHost;
        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(fallbackHost);
            IPAddress? ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is not null)
            {
                host = ipv4.ToString();
            }
            else
            {
                string? gateway = TryGetContainerGateway();
                if (!string.IsNullOrEmpty(gateway))
                {
                    host = gateway;
                }
            }
        }
        catch
        {
            string? gateway = TryGetContainerGateway();
            if (!string.IsNullOrEmpty(gateway))
            {
                host = gateway;
            }
        }

        return string.IsNullOrEmpty(port) ? host : $"{host}:{port}";
    }

    /// <summary>
    ///     Attempts to read the default route gateway from /proc/net/route in a Linux container.
    /// </summary>
    public static string? TryGetContainerGateway()
    {
        try
        {
            if (File.Exists("/proc/net/route"))
            {
                foreach (string line in File.ReadLines("/proc/net/route"))
                {
                    string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && parts[1] == "00000000")
                    {
                        uint hex = Convert.ToUInt32(parts[2], 16);
                        if (hex != 0)
                        {
                            return new IPAddress(BitConverter.GetBytes(hex)).ToString();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AddressResolver] TryGetContainerGateway error: {ex.Message}");
        }
        return null;
    }

    private static (bool Reachable, long LastProbeTicks) reachabilityCache;

    /// <summary>
    ///     Checks whether the target TCP endpoint is currently reachable asynchronously.
    ///     Positive results are cached for 15 seconds; negative results for 2 seconds to rapidly detect recovery.
    /// </summary>
    public static async Task<bool> CheckMasterServerReachabilityAsync(string hostAndPort, int timeoutMs = 1500, CancellationToken cancellationToken = default)
    {
        long now = Environment.TickCount64;
        (bool Reachable, long LastProbeTicks) cache = reachabilityCache;
        long cacheDuration = cache.Reachable ? 15000 : 2000;
        if (cache.LastProbeTicks != 0 && (now - cache.LastProbeTicks) < cacheDuration)
        {
            return cache.Reachable;
        }

        bool reachable = await IsEndpointReachableAsync(hostAndPort, timeoutMs, cancellationToken).ConfigureAwait(false);
        reachabilityCache = (reachable, Environment.TickCount64);
        return reachable;
    }

    /// <summary>
    ///     Checks whether the target TCP endpoint is currently reachable.
    ///     Positive results are cached for 15 seconds; negative results for 2 seconds to rapidly detect recovery.
    /// </summary>
    public static bool CheckMasterServerReachability(string hostAndPort, int timeoutMs = 1500)
    {
        long now = Environment.TickCount64;
        (bool Reachable, long LastProbeTicks) cache = reachabilityCache;
        long cacheDuration = cache.Reachable ? 15000 : 2000;
        if (cache.LastProbeTicks != 0 && (now - cache.LastProbeTicks) < cacheDuration)
        {
            return cache.Reachable;
        }

        bool reachable = IsEndpointReachable(hostAndPort, timeoutMs);
        reachabilityCache = (reachable, Environment.TickCount64);
        return reachable;
    }

    /// <summary>
    ///     Returns true if the hostname is a known container-to-host alias (Docker, k3d, or k3s).
    /// </summary>
    public static bool IsContainerHostAlias(string host) =>
        host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("host.k3d.internal", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("host.k3s.internal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Probes a host:port TCP endpoint asynchronously with a strict timeout, prioritizing IPv4 to prevent container IPv6 network unreachable errors.
    /// </summary>
    public static async Task<bool> IsEndpointReachableAsync(string hostAndPort, int timeoutMs = 1500, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostAndPort))
            return false;

        try
        {
            string host;
            int port;
            int colonIndex = hostAndPort.LastIndexOf(':');
            if (colonIndex > 0 && int.TryParse(hostAndPort[(colonIndex + 1)..], out int parsedPort))
            {
                host = hostAndPort[..colonIndex];
                port = parsedPort;
            }
            else
            {
                host = hostAndPort;
                port = 80;
            }

            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                addresses = [];
            }

            // Prioritize IPv4 addresses to avoid unreachable IPv6 ULA addresses in container environments
            IPAddress[] prioritized = [
                .. addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork),
                .. addresses.Where(a => a.AddressFamily != AddressFamily.InterNetwork)
            ];

            foreach (IPAddress targetIp in prioritized)
            {
                try
                {
                    using Socket socket = new(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(timeoutMs);
                    await socket.ConnectAsync(new IPEndPoint(targetIp, port), cts.Token).ConfigureAwait(false);
                    if (socket.Connected)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AddressResolver] IsEndpointReachableAsync candidate {targetIp}:{port} unreachable: {ex.Message}");
                }
            }

            // Fallback for container: probe gateway if host was a container host alias and failed
            if (IsRunningInContainer && IsContainerHostAlias(host))
            {
                string? gateway = TryGetContainerGateway();
                if (!string.IsNullOrEmpty(gateway) && IPAddress.TryParse(gateway, out IPAddress? gatewayIp))
                {
                    try
                    {
                        using Socket socket = new(gatewayIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(timeoutMs);
                        await socket.ConnectAsync(new IPEndPoint(gatewayIp, port), cts.Token).ConfigureAwait(false);
                        if (socket.Connected)
                        {
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AddressResolver] IsEndpointReachableAsync gateway {gatewayIp}:{port} unreachable: {ex.Message}");
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Probes a host:port TCP endpoint synchronously using native non-blocking socket select,
    ///     prioritizing IPv4 to prevent container IPv6 network unreachable errors without sync-over-async threadpool blocking.
    /// </summary>
    public static bool IsEndpointReachable(string hostAndPort, int timeoutMs = 1500)
    {
        if (string.IsNullOrWhiteSpace(hostAndPort))
            return false;

        try
        {
            string host;
            int port;
            int colonIndex = hostAndPort.LastIndexOf(':');
            if (colonIndex > 0 && int.TryParse(hostAndPort[(colonIndex + 1)..], out int parsedPort))
            {
                host = hostAndPort[..colonIndex];
                port = parsedPort;
            }
            else
            {
                host = hostAndPort;
                port = 80;
            }

            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch
            {
                addresses = [];
            }

            // Prioritize IPv4 addresses to avoid unreachable IPv6 ULA addresses in container environments
            IPAddress[] prioritized = [
                .. addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork),
                .. addresses.Where(a => a.AddressFamily != AddressFamily.InterNetwork)
            ];

            foreach (IPAddress targetIp in prioritized)
            {
                try
                {
                    using Socket socket = new(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.Blocking = false;
                    try
                    {
                        socket.Connect(new IPEndPoint(targetIp, port));
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AddressResolver] Non-blocking connect initiated to {targetIp}:{port} ({ex.SocketErrorCode}).");
                    }

                    if (socket.Poll(timeoutMs * 1000, SelectMode.SelectWrite))
                    {
                        int error = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                        if (error == 0)
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AddressResolver] IsEndpointReachable candidate {targetIp}:{port} unreachable: {ex.Message}");
                }
            }

            // Fallback for container: probe gateway if host was a container host alias and failed
            if (IsRunningInContainer && IsContainerHostAlias(host))
            {
                string? gateway = TryGetContainerGateway();
                if (!string.IsNullOrEmpty(gateway) && IPAddress.TryParse(gateway, out IPAddress? gatewayIp))
                {
                    try
                    {
                        using Socket socket = new(gatewayIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        socket.Blocking = false;
                        try
                        {
                            socket.Connect(new IPEndPoint(gatewayIp, port));
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AddressResolver] Non-blocking connect initiated to gateway {gatewayIp}:{port} ({ex.SocketErrorCode}).");
                        }

                        if (socket.Poll(timeoutMs * 1000, SelectMode.SelectWrite))
                        {
                            int error = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                            if (error == 0)
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AddressResolver] IsEndpointReachable gateway {gatewayIp}:{port} unreachable: {ex.Message}");
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     The master server host and port passed to the manager via the "-masterserver" argument.
    ///     Prioritizes the configured MasterServerAddress (from dotnet user secrets / configuration).
    /// </summary>
    public string MasterServerHostAndPort
    {
        get
        {
            bool isStandalone = string.Equals(Environment.GetEnvironmentVariable("KONGOR_STANDALONE"), "true", StringComparison.OrdinalIgnoreCase) ||
                               Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out _);

            if (!string.IsNullOrWhiteSpace(manager.MasterServerAddress) &&
                !((GatewayIsLocalhost || isStandalone) && manager.MasterServerAddress.Equals(PublicMasterServerAddress, StringComparison.OrdinalIgnoreCase)))
            {
                string address = manager.MasterServerAddress.Trim();
                if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    address = address["https://".Length..].TrimEnd('/');
                else if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    address = address["http://".Length..].TrimEnd('/');
                return NormalizeHostForContainer(address, IsRunningInContainer);
            }

            string localHost = IsRunningInContainer ? "host.docker.internal" : LocalMasterServerAddress;
            return (GatewayIsLocalhost || isStandalone)
                ? $"{localHost}:{LocalMasterServerPort}"
                : PublicMasterServerAddress;
        }
    }

    /// <summary>
    ///     Resolves the server's own advertised IPv4 address. The result is cached for the lifetime of the process.
    /// </summary>
    public async Task<string> ResolveServerAddress(CancellationToken cancellationToken = default)
    {
        if (cachedServerAddress is not null)
            return cachedServerAddress;

        string gateway = manager.Gateway;

        string resolved;

        if (gateway.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            resolved = "127.0.0.1";
        }

        else if (gateway.Equals("AUTO", StringComparison.OrdinalIgnoreCase) ||
                 gateway.Equals("LAN", StringComparison.OrdinalIgnoreCase))
        {
            resolved = DetectLocalRoutableIPv4Address();
        }

        else if (gateway.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase))
        {
            resolved = await DetectPublicIPAddress(cancellationToken).ConfigureAwait(false);
        }

        else if (IPAddress.TryParse(gateway, out IPAddress? parsed))
        {
            // "MapToIPv4" Silently Reinterprets Any IPv6 Address's Low-Order Bits Rather Than Throwing, So A Genuine (Non-Mapped) IPv6 Literal Must Be Rejected Explicitly Here.
            if (parsed.AddressFamily is AddressFamily.InterNetworkV6 && parsed.IsIPv4MappedToIPv6 is false)
                throw new InvalidOperationException(
                    $@"Gateway ""{gateway}"" Is An IPv6 Address; COMPEL Requires An IPv4 Address");

            resolved = parsed.MapToIPv4().ToString();
        }

        else
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(gateway, cancellationToken).ConfigureAwait(false);

            IPAddress? ipv4Address =
                addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);

            resolved = ipv4Address?.ToString() ??
                       throw new InvalidOperationException(
                           $@"Unable To Resolve Gateway ""{gateway}"" To An IPv4 Address");
        }

        cachedServerAddress = resolved;

        return resolved;
    }

    public static string DetectLocalRoutableIPv4Address()
    {
        try
        {
            string? fallbackCandidate = null;
            System.Net.NetworkInformation.NetworkInterface[] interfaces =
                System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

            foreach (System.Net.NetworkInformation.NetworkInterface ni in interfaces)
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                System.Net.NetworkInformation.IPInterfaceProperties props = ni.GetIPProperties();
                bool hasIpv4DefaultGateway = false;
                foreach (System.Net.NetworkInformation.GatewayIPAddressInformation gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !gw.Address.Equals(IPAddress.Any))
                    {
                        hasIpv4DefaultGateway = true;
                        break;
                    }
                }

                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(uni.Address))
                    {
                        continue;
                    }

                    byte[] octets = uni.Address.GetAddressBytes();
                    if (octets.Length == 4 && octets[0] == 169 && octets[1] == 254)
                    {
                        continue; // Skip APIPA link-local addresses
                    }

                    if (hasIpv4DefaultGateway)
                    {
                        return uni.Address.ToString();
                    }

                    fallbackCandidate ??= uni.Address.ToString();
                }
            }

            if (!string.IsNullOrWhiteSpace(fallbackCandidate))
            {
                return fallbackCandidate;
            }
        }
        catch
        {
            // Fall through cleanly to loopback when network interface enumeration is unavailable
        }

        return "127.0.0.1";
    }

    private async Task<string> DetectPublicIPAddress(CancellationToken cancellationToken)
    {
        using HttpClient httpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(5));

        foreach (string service in PublicIPServices)
            try
            {
                string response = await httpClient.GetStringAsync(service, cancellationToken).ConfigureAwait(false);

                string ipAddress = response.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

                if (IPAddress.TryParse(ipAddress, out _))
                    return ipAddress;
            }

            catch (Exception exception)
            {
                logger.LogDebug(exception, "Public IP Detection Service {Service} Failed", service);
            }

        throw new InvalidOperationException("Unable To Detect Public IP Address From Any Service");
    }
}
