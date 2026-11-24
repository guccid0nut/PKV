// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Network;

namespace WILLOWMAKER.Services.P2P;

/// <summary>
/// Service providing IP auto-detection, invite code formatting, and address parsing for P2P matches.
/// </summary>
public static class P2pConnectionService
{
    public const int DefaultPort = 11550;
    public const int VoicePortOffset = 500;
    private static readonly HttpClient HttpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(3));

    private static readonly Regex Rfc1123HostnameRegex = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Verifies that a UDP port is genuinely unbound on both wildcard (0.0.0.0) and loopback (127.0.0.1).
    /// </summary>
    public static bool IsUdpPortAvailable(int port)
    {
        if (port <= 0 || port > 65535)
        {
            return false;
        }

        try
        {
            using Socket anySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            anySocket.ExclusiveAddressUse = true;
            anySocket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException)
        {
            return false;
        }

        try
        {
            using Socket loopbackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            loopbackSocket.ExclusiveAddressUse = true;
            loopbackSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Probes for the first free P2P game UDP port whose paired voice UDP port (port + 500) is also free.
    /// </summary>
    public static int FindAvailableUdpPort(int preferredPort = DefaultPort, int maxAttempts = 30)
    {
        int startPort = (preferredPort > 0 && preferredPort <= 65000) ? preferredPort : DefaultPort;
        int attempts = Math.Max(1, maxAttempts);

        for (int offset = 0; offset < attempts; offset++)
        {
            int candidate = startPort + offset;
            if (candidate > 65000)
            {
                break;
            }

            if (IsUdpPortAvailable(candidate) && IsUdpPortAvailable(candidate + VoicePortOffset))
            {
                return candidate;
            }
        }

        return startPort;
    }

    /// <summary>
    /// Gets the primary local IPv4 address (e.g. 192.168.x.x or 10.x.x.x).
    /// </summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties props = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2pConnectionService] Local IP inspection notice: {ex.Message}");
        }

        return "127.0.0.1";
    }

    /// <summary>
    /// Asynchronously resolves public WAN IPv4 address via lightweight IP echo APIs.
    /// </summary>
    public static async Task<string?> GetPublicIpAddressAsync(CancellationToken cancellationToken = default)
    {
        string[] endpoints =
        [
            "https://api.ipify.org",
            "https://icanhazip.com",
            "https://ifconfig.me/ip"
        ];

        foreach (string endpoint in endpoints)
        {
            try
            {
                string ip = (await HttpClient.GetStringAsync(endpoint, cancellationToken)).Trim();
                if (IPAddress.TryParse(ip, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[P2pConnectionService] Public IP probe fallback from '{endpoint}': {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes user-inputted address into a valid IP:port pair for the K2 Connect command.
    /// Handles raw IP, hostnames, "[IPv6]:port", "Connect &lt;IP&gt;", and "kongor://&lt;IP&gt;".
    /// Strictly rejects CLI switches, quotes, semicolons, whitespace, control characters, and invalid ports.
    /// </summary>
    public static string NormalizeConnectAddress(string rawInput, int defaultPort = DefaultPort)
    {
        EnsureValidPortOrThrow(defaultPort, nameof(defaultPort));

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return $"127.0.0.1:{defaultPort}";
        }

        RejectUnsafeCharactersOrSwitches(rawInput, nameof(rawInput));

        string clean = rawInput.Trim();

        // Strip Connect prefix if copied from console
        if (clean.StartsWith("Connect ", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[8..].Trim();
        }

        // Strip custom URI schemes
        if (clean.StartsWith("kongor://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[9..].Trim();
        }

        clean = clean.TrimEnd('/');
        return ValidateConnectAddressOrThrow(clean, defaultPort);
    }

    /// <summary>
    /// Validates that a host(:port) or [IPv6](:port) endpoint contains no K2 CLI / console injection vectors
    /// and resolves to a canonical IPv4:port, [IPv6]:port, or RFC-1123 hostname:port with port in 1..65535.
    /// </summary>
    public static string ValidateConnectAddressOrThrow(string candidate, int defaultPort = DefaultPort)
    {
        EnsureValidPortOrThrow(defaultPort, nameof(defaultPort));

        if (string.IsNullOrWhiteSpace(candidate))
        {
            ThrowP2pInjection("Connect address must not be empty.", nameof(candidate));
        }

        RejectUnsafeCharactersOrSwitches(candidate, nameof(candidate));
        string trimmed = candidate.Trim();

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsWhiteSpace(trimmed[i]))
            {
                ThrowP2pInjection($"Connect address '{trimmed}' must not contain embedded whitespace.", nameof(candidate));
            }
        }

        // 1. Bracketed IPv6: [2001:db8::1] or [2001:db8::1]:11550
        if (trimmed.StartsWith('['))
        {
            int closeBracket = trimmed.IndexOf(']');
            if (closeBracket <= 1)
            {
                ThrowP2pInjection($"Malformed bracketed IPv6 connect address '{trimmed}'.", nameof(candidate));
            }

            string ipv6Text = trimmed[1..closeBracket];
            IPAddress? ipv6Addr = null;
            if (ipv6Text.Contains('%', StringComparison.Ordinal) ||
                !IPAddress.TryParse(ipv6Text, out ipv6Addr) ||
                ipv6Addr.AddressFamily != AddressFamily.InterNetworkV6)
            {
                ThrowP2pInjection($"Invalid IPv6 host literal '{ipv6Text}'.", nameof(candidate));
            }

            int resolvedV6Port = defaultPort;
            if (closeBracket < trimmed.Length - 1)
            {
                if (trimmed[closeBracket + 1] != ':')
                {
                    ThrowP2pInjection($"Unexpected trailing characters after IPv6 literal in '{trimmed}'.", nameof(candidate));
                }

                string v6PortText = trimmed[(closeBracket + 2)..];
                resolvedV6Port = ParseStrictPortOrThrow(v6PortText, nameof(candidate));
            }

            return $"[{ipv6Addr}]:{resolvedV6Port}";
        }

        // 2. Unbracketed multi-colon literal (bare IPv6 without port)
        int firstColon = trimmed.IndexOf(':');
        int lastColon = trimmed.LastIndexOf(':');
        if (firstColon >= 0 && firstColon != lastColon)
        {
            if (!trimmed.Contains('%', StringComparison.Ordinal) &&
                IPAddress.TryParse(trimmed, out IPAddress? bareIpv6) &&
                bareIpv6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return $"[{bareIpv6}]:{defaultPort}";
            }

            ThrowP2pInjection($"Invalid multi-colon host/IPv6 address '{trimmed}'. Use [IPv6]:port notation.", nameof(candidate));
        }

        // 3. IPv4(:port) or RFC-1123 hostname(:port)
        string hostPart = trimmed;
        int resolvedPort = defaultPort;
        if (lastColon >= 0)
        {
            hostPart = trimmed[..lastColon];
            string portPart = trimmed[(lastColon + 1)..];
            resolvedPort = ParseStrictPortOrThrow(portPart, nameof(candidate));
        }

        if (string.IsNullOrWhiteSpace(hostPart) || hostPart.Length > 253)
        {
            ThrowP2pInjection($"Invalid host length in '{trimmed}'.", nameof(candidate));
        }

        bool isNumericDotsOnly = hostPart.All(c => char.IsAsciiDigit(c) || c == '.');
        if (isNumericDotsOnly)
        {
            string[] octets = hostPart.Split('.');
            IPAddress? ipv4Addr = null;
            if (octets.Length != 4 ||
                octets.Any(o => string.IsNullOrEmpty(o) || !byte.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ||
                !IPAddress.TryParse(hostPart, out ipv4Addr) ||
                ipv4Addr.AddressFamily != AddressFamily.InterNetwork)
            {
                ThrowP2pInjection($"Invalid IPv4 literal '{hostPart}'.", nameof(candidate));
            }

            return $"{ipv4Addr}:{resolvedPort}";
        }

        if (!Rfc1123HostnameRegex.IsMatch(hostPart))
        {
            ThrowP2pInjection($"Host '{hostPart}' violates RFC-1123 hostname rules.", nameof(candidate));
        }

        return $"{hostPart}:{resolvedPort}";
    }

    private static void RejectUnsafeCharactersOrSwitches(string input, string paramName)
    {
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch is '"' or '\'' or ';' or '`' or '$' or '|' or '&' || char.IsControl(ch))
            {
                ThrowP2pInjection($"Rejected forbidden character (0x{(int)ch:X2}) in P2P connect address.", paramName);
            }
        }

        string trimmed = input.TrimStart();
        if (trimmed.StartsWith('-') || trimmed.StartsWith('+'))
        {
            ThrowP2pInjection($"Rejected CLI switch prefix in P2P connect address '{trimmed}'.", paramName);
        }
    }

    private static int ParseStrictPortOrThrow(string portText, string paramName)
    {
        if (string.IsNullOrEmpty(portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            port < 1 ||
            port > 65535)
        {
            ThrowP2pInjection($"Port '{portText}' is outside valid TCP/UDP port bounds (1..65535).", paramName);
            return DefaultPort;
        }

        return port;
    }

    private static void EnsureValidPortOrThrow(int port, string paramName)
    {
        if (port < 1 || port > 65535)
        {
            Trace.WriteLine($"[SECURITY-ALERT][P2P-INJECTION] Invalid default/active P2P port {port}.");
            throw new ArgumentOutOfRangeException(paramName, port, "[SECURITY-ALERT][P2P-INJECTION] P2P port must be between 1 and 65535.");
        }
    }

    private static void ThrowP2pInjection(string reason, string paramName)
    {
        Trace.WriteLine($"[SECURITY-ALERT][P2P-INJECTION] {reason}");
        throw new ArgumentException($"[SECURITY-ALERT][P2P-INJECTION] {reason} Run with a valid IPv4:port, [IPv6]:port, or RFC-1123 hostname:port.", paramName);
    }

    public static bool IsDisallowedDefaultNickname(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return true;

        string trimmed = candidate.Trim();
        return trimmed.Equals("UnnamedNewbie", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("guccidonut", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Cheater", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("KONGOR Player", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the last played or logged-in username.
    /// Checks saved launcher settings, game client startup.cfg/login.cfg files (p2p_nickname -> login_name), and falls back to "Player".
    /// NEVER defaults to developer names or the OS username.
    /// </summary>
    public static string ResolveDefaultNickname(string? clientDirectory = null)
    {
        try
        {
            LauncherSettingsModel settings = LauncherSettingsService.Load(clientDirectory);
            if (!IsDisallowedDefaultNickname(settings.LastPlayerNickname))
            {
                return settings.LastPlayerNickname!.Trim();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2pConnectionService] Settings nickname lookup notice: {ex.Message}");
        }

        string baseDir = clientDirectory ?? Environment.CurrentDirectory;
        string[] candidateConfigs =
        [
            Path.Combine(baseDir, "userdataKONGOR", "configuration", "p2p_session.cfg"),
            Path.Combine(baseDir, "userdata", "startup.cfg"),
            Path.Combine(baseDir, "userdata", "game", "startup.cfg"),
            Path.Combine(baseDir, "userdata", "login.cfg"),
            Path.Combine(baseDir, "KONGOR", "configuration", "login.cfg"),
            Path.Combine(baseDir, "userdata", "game", "login.cfg"),
            Path.Combine(baseDir, "userdataKONGOR", "extensions", "login.cfg")
        ];

        string[] cvarPriority = ["p2p_nickname", "login_name"];
        foreach (string cvarName in cvarPriority)
        {
            string pattern = $@"SetSave\s+[""']?{cvarName}[""']?\s+[""']?([^""'\r\n]+)[""']?";
            foreach (string cfgPath in candidateConfigs)
            {
                if (!File.Exists(cfgPath))
                    continue;

                try
                {
                    foreach (string line in File.ReadAllLines(cfgPath))
                    {
                        Match match = Regex.Match(
                            line,
                            pattern,
                            RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            string val = match.Groups[1].Value.Trim();
                            if (!IsDisallowedDefaultNickname(val))
                            {
                                return val;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[P2pConnectionService] Candidate config read notice ({cfgPath}): {ex.Message}");
                }
            }
        }

        return "Player";
    }

    /// <summary>
    /// Configures per-user P2P session nickname files strictly inside git-ignored userdataKONGOR/
    /// (NEVER mutating git-tracked override/stringtables/client_messages_*.str).
    /// </summary>
    public static void EnsureClientDefaultNameConfigured(string gameDir, string nickname)
    {
        string rawNickname = string.IsNullOrWhiteSpace(nickname) ? "Player" : nickname.Trim();
        string safeNickname = P2pCookieCodec.SanitizeNickname(rawNickname);
        if (string.IsNullOrWhiteSpace(safeNickname))
        {
            safeNickname = "Player";
        }

        string userConfigDir = Path.Combine(gameDir, "userdataKONGOR", "configuration");
        string userStringtablesDir = Path.Combine(gameDir, "userdataKONGOR", "extensions", "stringtables");
        Directory.CreateDirectory(userConfigDir);
        Directory.CreateDirectory(userStringtablesDir);

        string sessionCfgPath = Path.Combine(userConfigDir, "p2p_session.cfg");
        File.WriteAllText(sessionCfgPath, $"// Ephemeral per-user P2P session nickname (Wave 4A isolated)\r\nSetSave \"p2p_nickname\" \"{safeNickname}\"\r\n");

        string[] languages = ["en", "ru", "th", "es", "ua", "vn"];
        string sharedDir = Path.Combine(gameDir, "..", "shared", "stringtables");
        if (!Directory.Exists(sharedDir))
        {
            sharedDir = Path.Combine(gameDir, "shared", "stringtables");
        }

        foreach (string lang in languages)
        {
            string fileName = $"client_messages_{lang}.str";
            string targetPath = Path.Combine(userStringtablesDir, fileName);
            string sourcePath = Path.Combine(sharedDir, fileName);

            try
            {
                string targetContent;
                if (File.Exists(targetPath))
                {
                    string existing = File.ReadAllText(targetPath);
                    if (Regex.IsMatch(
                            existing,
                            @"^client_default_name\s+",
                            RegexOptions.Multiline))
                    {
                        targetContent = Regex.Replace(
                            existing,
                            @"^client_default_name\s+[^\r\n]*",
                            $"client_default_name\t\t\t\t{safeNickname}",
                            RegexOptions.Multiline);
                    }
                    else
                    {
                        targetContent = $"client_default_name\t\t\t\t{safeNickname}\r\n" + existing;
                    }
                }
                else if (File.Exists(sourcePath))
                {
                    string original = File.ReadAllText(sourcePath);
                    targetContent = Regex.Replace(
                        original,
                        @"^client_default_name\s+[^\r\n]*",
                        $"client_default_name\t\t\t\t{safeNickname}",
                        RegexOptions.Multiline);
                }
                else
                {
                    targetContent = $"client_default_name\t\t\t\t{safeNickname}\r\n";
                }

                File.WriteAllText(targetPath, targetContent);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[P2pConnectionService] User stringtable write notice ({targetPath}): {ex.Message}");
            }
        }

        Trace.WriteLine($"[P2pConnectionService] Configured isolated P2P nickname '{safeNickname}' under '{userStringtablesDir}'.");
    }

    /// <summary>
    /// Reads optional community relay endpoints (COMPEL proxy, coturn, rathole) from environment variables.
    /// Zero hardcoded hosts or secrets.
    /// </summary>
    public static DirectPlayRelayOptions BuildRelayOptionsFromEnvironment()
    {
        return new DirectPlayRelayOptions(
            CompelProxyHost: ReadOptionalEnv("KONGOR_P2P_COMPEL_RELAY_HOST"),
            CompelProxyPort: ReadOptionalPortEnv("KONGOR_P2P_COMPEL_RELAY_PORT", 20000),
            CoturnHost: ReadOptionalEnv("KONGOR_P2P_COTURN_HOST"),
            CoturnPort: ReadOptionalPortEnv("KONGOR_P2P_COTURN_PORT", 3478),
            CoturnTlsPort: ReadOptionalPortEnv("KONGOR_P2P_COTURN_TLS_PORT", 5349),
            CoturnRealm: ReadOptionalEnv("KONGOR_P2P_COTURN_REALM") ?? "p2p.kongor.local",
            RatholeServerHost: ReadOptionalEnv("KONGOR_P2P_RATHOLE_HOST"),
            RatholeControlPort: ReadOptionalPortEnv("KONGOR_P2P_RATHOLE_CONTROL_PORT", 2333),
            RatholePublicUdpPort: ReadOptionalPortEnv("KONGOR_P2P_RATHOLE_UDP_PORT", 20550),
            VoicePortOffset: VoicePortOffset);
    }

    /// <summary>
    /// Runs a 2-endpoint RFC 5389/5780 STUN NAT behavior classification probe with graceful offline fallback.
    /// </summary>
    public static async Task<StunProbeResult> ProbeNatBehaviorAsync(
        int localPort = 0,
        TimeSpan? timeout = null,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        void EmitLog(string msg)
        {
            Trace.WriteLine(msg);
            logSink?.Invoke(msg);
        }

        EmitLog($"[P2P-NAT] Probing NAT mapping behavior via 2-endpoint STUN (localPort={localPort})...");
        try
        {
            StunProbeResult result = await StunNatClassifier.ProbeNatWithHostnamesAsync(
                timeout: timeout ?? TimeSpan.FromMilliseconds(1500),
                localPort: localPort,
                logSink: EmitLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            EmitLog($"[P2P-NAT] Probe complete: Classification={result.Classification} ({FormatNatClassificationLabel(result.Classification)}), External={result.PrimaryMappedEndPoint?.ToString() ?? "none"}, RequiresRelay={result.RequiresRelay}.");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string msg = $"STUN NAT probe unavailable ({ex.GetType().Name}: {ex.Message}); treating UDP reachability as blocked/offline.";
            EmitLog($"[P2P-NAT] [WARN] {msg}");
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
    }

    /// <summary>
    /// Computes the DirectPlay P2P transport &amp; CGNAT relay plan from UPnP + STUN diagnostics.
    /// </summary>
    public static DirectPlayTransportPlan PlanDirectPlayTransport(
        int hostPort,
        string localIp,
        UpnpPortMappingResult? upnpResult,
        StunProbeResult? stunResult,
        DirectPlayRelayOptions? relayOptions = null,
        Action<string>? logSink = null)
    {
        void EmitLog(string msg)
        {
            Trace.WriteLine(msg);
            logSink?.Invoke(msg);
        }

        DirectPlayRelayOptions effectiveOptions = relayOptions ?? BuildRelayOptionsFromEnvironment();
        EmitLog($"[P2P-RELAY] Evaluating transport strategy (hostPort={hostPort}, localIp={localIp}, upnpOk={upnpResult?.Succeeded == true}, nat={stunResult?.Classification ?? NatBehaviorClassification.Unknown})...");

        return DirectPlayRelayCoordinator.PlanTransport(
            hostPort,
            localIp,
            upnpResult,
            stunResult,
            effectiveOptions,
            EmitLog);
    }

    public static string FormatNatClassificationLabel(NatBehaviorClassification classification) => classification switch
    {
        NatBehaviorClassification.OpenInternet => "Open Internet (Direct WAN UDP)",
        NatBehaviorClassification.ConeNat => "Cone NAT (UPnP / STUN Hole-Punch Ready)",
        NatBehaviorClassification.SymmetricOrCgnat => "Symmetric NAT / CGNAT (Relay Required)",
        NatBehaviorClassification.UdpBlocked => "UDP Blocked / Offline (TLS TURN or TCP Tunnel Required)",
        _ => "Unclassified / Offline"
    };

    private static string? ReadOptionalEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static int ReadOptionalPortEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) &&
            port is >= 1 and <= 65535)
        {
            return port;
        }

        return fallback;
    }
}
