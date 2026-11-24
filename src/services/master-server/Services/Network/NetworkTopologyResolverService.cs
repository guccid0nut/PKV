// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Globalization;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Http;
using ASPIRE.Common.Network;
using KONGOR.MasterServer.Logging;
using NEXUS.Contracts.Services;

namespace KONGOR.MasterServer.Services.Network;

/// <summary>
///     Background service and resolver that continuously maintains cached network topology
///     (public IP discovery and zero-latency chat server routing).
/// </summary>
public sealed class NetworkTopologyResolverService : ResilientBackgroundService, INetworkTopologyResolver
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly ILogger<NetworkTopologyResolverService> _logger;
    private readonly Func<string, string?> _environmentReader;
    private static volatile string? s_cachedPublicIP;

    /// <summary>
    ///     Gets the latest known public IP address synchronously.
    /// </summary>
    public static string? CurrentPublicIP => s_cachedPublicIP ?? SplitHorizonEndpointResolver.CurrentPublicIP;

    internal static void ResetCachedPublicIPForTesting()
    {
        s_cachedPublicIP = null;
        SplitHorizonEndpointResolver.ResetCachedPublicIPForTesting();
    }

    public NetworkTopologyResolverService(
        ILogger<NetworkTopologyResolverService> logger,
        HttpClient? httpClient = null,
        Func<string, string?>? environmentReader = null)
        : base(logger, nameof(NetworkTopologyResolverService))
    {
        _logger = logger;
        _httpClient = httpClient ?? SharedSocketsHttpClientFactory.Create(RequestTimeout);
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc />
    public string? GetPublicIP()
    {
        return s_cachedPublicIP ?? GetLocalIPAddress();
    }

    /// <inheritdoc />
    public async Task<string?> GetPublicIPAsync(CancellationToken ct = default)
    {
        if (s_cachedPublicIP is not null)
        {
            return s_cachedPublicIP;
        }

        await RefreshPublicIPAsync(ct).ConfigureAwait(false);
        return s_cachedPublicIP ?? GetLocalIPAddress();
    }

    /// <inheritdoc />
    public (string Host, int Port) ResolveChatServer(
        IPAddress clientIP,
        string? hostHeader,
        string accountName,
        string accountType)
    {
        string chatServerHostLocal = _environmentReader("CHAT_SERVER_HOST") ?? "127.0.0.1";
        int chatServerPortLocal = int.Parse(
            _environmentReader("CHAT_SERVER_PORT_CLIENT_LOCAL") ?? "11031",
            CultureInfo.InvariantCulture);

        string chatServerHostPublic =
            _environmentReader("CHAT_SERVER_PUBLIC_HOST") ?? chatServerHostLocal;
        int chatServerPortPublic = int.Parse(
            _environmentReader("CHAT_SERVER_PUBLIC_CLIENT_PORT") ?? _environmentReader("CHAT_SERVER_PORT_CLIENT") ?? chatServerPortLocal.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

        string? publicIP = s_cachedPublicIP;

        bool hasPublicChatHostConfigured = !string.IsNullOrWhiteSpace(chatServerHostPublic)
                                           && chatServerHostPublic != "127.0.0.1"
                                           && chatServerHostPublic != "localhost";

        bool isLoopback = IPAddress.IsLoopback(clientIP);
        bool isLanOrVpn = !isLoopback && (IsPrivateIP(clientIP) || IsCgnat(clientIP) || (!string.IsNullOrWhiteSpace(publicIP) && clientIP.ToString() == publicIP));

        string chatServerHost;
        int chatServerClientConnectionsPort;
        string routingClass;
        string source;

        if (isLoopback)
        {
            chatServerHost = chatServerHostLocal;
            chatServerClientConnectionsPort = chatServerPortLocal;
            routingClass = "Loopback";
            source = "local";
        }
        else if (isLanOrVpn)
        {
            chatServerClientConnectionsPort = chatServerPortLocal;
            routingClass = "LanVpn";

            string? envLanHost = _environmentReader("CHAT_SERVER_LAN_HOST")?.Trim();
            string? hostPart = ExtractHostFromHeader(hostHeader);

            if (!string.IsNullOrWhiteSpace(envLanHost))
            {
                chatServerHost = envLanHost;
                source = "env";
            }
            else if (!string.IsNullOrWhiteSpace(hostPart)
                     && IPAddress.TryParse(hostPart, out IPAddress? parsedHostIp)
                     && !IPAddress.IsLoopback(parsedHostIp)
                     && !parsedHostIp.Equals(IPAddress.Any)
                     && !parsedHostIp.Equals(IPAddress.IPv6Any))
            {
                chatServerHost = hostPart;
                source = "hostHeader";
            }
            else
            {
                string localNicIp = GetLocalIPAddress();
                if (!string.IsNullOrWhiteSpace(localNicIp) && localNicIp != "127.0.0.1")
                {
                    chatServerHost = localNicIp;
                    source = "nicIp";
                }
                else
                {
                    chatServerHost = chatServerHostLocal;
                    source = "fallback";
                }
            }
        }
        else
        {
            routingClass = "Wan";

            if (hasPublicChatHostConfigured)
            {
                if (IPAddress.TryParse(chatServerHostPublic, out _))
                {
                    chatServerHost = chatServerHostPublic;
                }
                else
                {
                    try
                    {
                        IPHostEntry hostEntry = Dns.GetHostEntry(chatServerHostPublic);
                        IPAddress? ipv4 = hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
                        chatServerHost = ipv4?.ToString() ?? chatServerHostPublic;
                    }
                    catch
                    {
                        chatServerHost = chatServerHostPublic;
                    }
                }

                chatServerClientConnectionsPort = chatServerPortPublic;
                source = "env";
            }
            else if (!string.IsNullOrWhiteSpace(publicIP) && publicIP != "127.0.0.1")
            {
                chatServerHost = publicIP;
                chatServerClientConnectionsPort = chatServerPortPublic;
                source = "publicIp";
            }
            else if (!string.IsNullOrWhiteSpace(chatServerHostPublic) && chatServerHostPublic != "127.0.0.1" &&
                     chatServerHostPublic != "localhost")
            {
                chatServerHost = chatServerHostPublic;
                chatServerClientConnectionsPort = chatServerPortPublic;
                source = "env";
            }
            else
            {
                chatServerHost = chatServerHostLocal;
                chatServerClientConnectionsPort = chatServerPortLocal;
                source = "fallback";
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogChatServerRoutingResolved(
                accountName,
                accountType,
                clientIP.ToString(),
                hostHeader ?? string.Empty,
                chatServerHost,
                chatServerClientConnectionsPort);
        }

        _logger.LogInformation(
            "[ChatRouting] class={RoutingClass} client={ClientIp} -> {ChatHost}:{ChatPort} source={Source}",
            routingClass,
            clientIP.ToString(),
            chatServerHost,
            chatServerClientConnectionsPort,
            source);

        return (chatServerHost, chatServerClientConnectionsPort);
    }

    /// <inheritdoc />
    public string ResolveGameServerAddress(
        IPAddress? clientIP,
        string? registeredServerIp,
        string? serverLocalIp = null,
        string? serverTruePublicIp = null)
    {
        return ResolveSplitHorizonGameServerAddress(
            clientIP?.ToString(),
            registeredServerIp,
            serverLocalIp,
            serverTruePublicIp);
    }

    /// <summary>
    ///     Static split-horizon game server IP resolver usable from both DI services and response serializers.
    ///     Routes same-PC clients to 127.0.0.1, LAN/VPN/Tailscale (100.64.0.0/10) clients to the routable host LAN/VPN IP,
    ///     and external WAN clients to the public IP across Local (#L), Docker (#D), and K3s (#K) fleets.
    /// </summary>
    public static string ResolveSplitHorizonGameServerAddress(
        string? requestorIP,
        string? registeredServerIp,
        string? serverLocalIp = null,
        string? serverTruePublicIp = null)
    {
        return SplitHorizonEndpointResolver.ResolveSplitHorizonGameServerAddress(
            requestorIP,
            registeredServerIp,
            serverLocalIp,
            serverTruePublicIp,
            discoveredPublicIp: CurrentPublicIP);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[NetworkTopologyResolver] Initializing background public IP resolver loop.");

        if (string.Equals(_environmentReader("KONGOR_STANDALONE"), "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_environmentReader("KONGOR_DISCOVER_PUBLIC_IP"), "true", StringComparison.OrdinalIgnoreCase))
        {
            s_cachedPublicIP ??= GetLocalIPAddress();
            _logger.LogInformation("[Standalone] Skipping external public IP discovery (KONGOR_STANDALONE=true)");
            return;
        }

        // Initial resolution
        await RefreshPublicIPAsync(stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(RefreshInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await RefreshPublicIPAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NetworkTopologyResolver] Error occurred in background public IP refresh tick.");
            }
        }

        _logger.LogInformation("[NetworkTopologyResolver] Background public IP resolver loop stopped.");
    }

    private async Task RefreshPublicIPAsync(CancellationToken ct)
    {
        if (string.Equals(_environmentReader("KONGOR_STANDALONE"), "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_environmentReader("KONGOR_DISCOVER_PUBLIC_IP"), "true", StringComparison.OrdinalIgnoreCase))
        {
            s_cachedPublicIP ??= GetLocalIPAddress();
            _logger.LogInformation("[Standalone] Skipping external public IP discovery (KONGOR_STANDALONE=true)");
            return;
        }

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            string ip = await _httpClient.GetStringAsync("https://api.ipify.org", cts.Token).ConfigureAwait(false);
            string trimmed = ip.Trim();
            if (IPAddress.TryParse(trimmed, out _))
            {
                s_cachedPublicIP = trimmed;
                SplitHorizonEndpointResolver.SetCachedPublicIP(trimmed);
                _logger.LogInformation("[NetworkTopologyResolver] Public IP discovered/refreshed: {PublicIP}", s_cachedPublicIP);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NetworkTopologyResolver] Failed to resolve public IP from external provider. Falling back to local network interface.");
        }

        s_cachedPublicIP ??= GetLocalIPAddress();
    }

    private static string? ExtractHostFromHeader(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return null;
        }

        string trimmed = hostHeader.Trim();
        if (Uri.TryCreate(trimmed.Contains("://") ? trimmed : $"http://{trimmed}", UriKind.Absolute, out Uri? parsedUri))
        {
            return parsedUri.Host;
        }

        if (trimmed.StartsWith('[') && trimmed.Contains(']'))
        {
            int closeBracket = trimmed.IndexOf(']');
            return trimmed.Substring(1, closeBracket - 1);
        }

        int colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':') == colon)
        {
            return trimmed[..colon];
        }

        return trimmed;
    }

    private static bool IsCgnat(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                return false;
            }
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
        }

        return false;
    }

    private static bool IsPrivateIP(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                byte[] bytes = ip.GetAddressBytes();
                return (bytes[0] & 0xFE) == 0xFC;
            }
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   IsCgnat(ip); // RFC 6598 CGNAT / Tailscale / WireGuard mesh
        }

        return false;
    }

    private static bool IsPrivateOrVpnIPString(string? ipStr)
    {
        if (string.IsNullOrWhiteSpace(ipStr) || !IPAddress.TryParse(ipStr, out IPAddress? ip))
        {
            return false;
        }

        return IPAddress.IsLoopback(ip) || IsPrivateIP(ip);
    }

    private static bool AreInSamePrivateOrVpnSubnet(string? ip1Str, string? ip2Str)
    {
        if (string.IsNullOrWhiteSpace(ip1Str) || string.IsNullOrWhiteSpace(ip2Str))
            return false;

        if (!IPAddress.TryParse(ip1Str, out IPAddress? ip1) || !IPAddress.TryParse(ip2Str, out IPAddress? ip2))
            return false;

        if (ip1.AddressFamily != AddressFamily.InterNetwork || ip2.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] b1 = ip1.GetAddressBytes();
        byte[] b2 = ip2.GetAddressBytes();

        if (b1[0] == 192 && b1[1] == 168 && b2[0] == 192 && b2[1] == 168)
            return b1[2] == b2[2];

        if (b1[0] == 10 && b2[0] == 10)
            return b1[1] == b2[1];

        if (b1[0] == 172 && b1[1] >= 16 && b1[1] <= 31 && b2[0] == 172 && b2[1] == b1[1])
            return true;

        // Tailscale / CGNAT 100.64.0.0/10 mesh
        if (b1[0] == 100 && b1[1] >= 64 && b1[1] <= 127 && b2[0] == 100 && b2[1] >= 64 && b2[1] <= 127)
            return true;

        return false;
    }

    public static string GetLocalIPAddressForDestination(IPAddress destination)
    {
        try
        {
            IPAddress target = destination.IsIPv4MappedToIPv6 ? destination.MapToIPv4() : destination;
            if (target.AddressFamily == AddressFamily.InterNetwork)
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect(target, 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint && !IPAddress.IsLoopback(endPoint.Address))
                {
                    return endPoint.Address.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkTopologyResolver] Destination route probe for '{destination}' fell back to default interface: {ex.Message}");
        }

        return GetLocalIPAddress();
    }

    public static string GetLocalIPAddress()
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            try
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch
            {
                // Ignored
            }
        }

        return "127.0.0.1";
    }
}
