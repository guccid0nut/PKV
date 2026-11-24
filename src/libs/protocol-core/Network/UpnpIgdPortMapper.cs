// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;
using ASPIRE.Common.Http;

namespace Kongor.Core.Network;

public sealed record UpnpPortMappingResult(
    bool Succeeded,
    string Protocol,
    int ExternalPort,
    int InternalPort,
    string? GatewayAddress,
    string? ExternalIpAddress,
    string? ServiceType,
    string DiagnosticMessage);

public interface IUpnpDiscoveryTransport
{
    Task<IReadOnlyList<string>> DiscoverLocationUrlsAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<string?> FetchDeviceDescriptionXmlAsync(Uri locationUri, CancellationToken cancellationToken);
    Task<(bool Succeeded, int StatusCode, string ResponseBody)> SendSoapActionAsync(
        Uri controlUri,
        string serviceType,
        string actionName,
        string soapBodyXml,
        CancellationToken cancellationToken);
}

/// <summary>
///     Zero-dependency, AOT-safe UPnP Internet Gateway Device (IGD) client for automatic UDP port mapping
///     in DirectPlay P2P hosting and local dedicated server flows.
///     Uses SSDP multicast discovery (<c>239.255.255.250:1900</c>) followed by IGD SOAP
///     <c>AddPortMapping</c> / <c>DeletePortMapping</c> / <c>GetExternalIPAddress</c>.
/// </summary>
public static class UpnpIgdPortMapper
{
    private static readonly string[] SupportedServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    ];

    private static readonly IUpnpDiscoveryTransport DefaultTransport = new SocketAndHttpUpnpTransport();

    public static async Task<UpnpPortMappingResult> TryMapUdpPortAsync(
        int port,
        string internalClientIp,
        string description = "Project KONGOR DirectPlay",
        int leaseDurationSeconds = 3600,
        TimeSpan? discoveryTimeout = null,
        IUpnpDiscoveryTransport? transport = null,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            return new UpnpPortMappingResult(
                false, "UDP", port, port, null, null, null,
                $"Invalid UDP port {port}; expected 1..65535.");
        }

        if (string.IsNullOrWhiteSpace(internalClientIp) || !IPAddress.TryParse(internalClientIp.Trim(), out IPAddress? parsedIp) ||
            parsedIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return new UpnpPortMappingResult(
                false, "UDP", port, port, null, null, null,
                $"Invalid internal IPv4 address '{internalClientIp}' for UPnP port mapping.");
        }

        IUpnpDiscoveryTransport effectiveTransport = transport ?? DefaultTransport;
        TimeSpan timeout = discoveryTimeout ?? TimeSpan.FromMilliseconds(1800);
        string safeDescription = SanitizeDescription(description);
        int clampedLease = Math.Clamp(leaseDurationSeconds, 0, 86400);

        Log(logSink, $"[UPnP] Discovering UPnP IGD gateway (timeout={timeout.TotalMilliseconds:F0}ms) to map UDP {port} -> {parsedIp}:{port}...");

        IReadOnlyList<string> locations;
        try
        {
            locations = await effectiveTransport.DiscoverLocationUrlsAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string msg = $"UPnP SSDP discovery unavailable ({ex.GetType().Name}: {ex.Message}). Manual router port forward or LAN/VPN required.";
            Log(logSink, $"[UPnP] {msg}");
            return new UpnpPortMappingResult(false, "UDP", port, port, null, null, null, msg);
        }

        if (locations.Count == 0)
        {
            const string msg = "No UPnP IGD router responded on LAN (router UPnP disabled or CGNAT/isolated network).";
            Log(logSink, $"[UPnP] {msg}");
            return new UpnpPortMappingResult(false, "UDP", port, port, null, null, null, msg);
        }

        string lastError = "Discovered gateway did not expose a WANIPConnection/WANPPPConnection service.";

        foreach (string rawLocation in locations)
        {
            if (!Uri.TryCreate(rawLocation, UriKind.Absolute, out Uri? locationUri) ||
                (locationUri.Scheme != Uri.UriSchemeHttp && locationUri.Scheme != Uri.UriSchemeHttps))
            {
                Log(logSink, $"[UPnP] Skipping non-HTTP SSDP location '{rawLocation}'.");
                continue;
            }

            if (!IsPrivateOrLoopbackHost(locationUri.Host))
            {
                Log(logSink, $"[UPnP] [SECURITY] Skipping non-LAN SSDP descriptor host '{locationUri.Host}'.");
                continue;
            }

            try
            {
                string? xml = await effectiveTransport.FetchDeviceDescriptionXmlAsync(locationUri, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(xml))
                {
                    lastError = $"Gateway {locationUri.Host} returned an empty UPnP device descriptor.";
                    continue;
                }

                (Uri controlUri, string serviceType)? service = ParseWanControlEndpoint(locationUri, xml);
                if (service is null)
                {
                    lastError = $"Gateway {locationUri.Host} descriptor had no WANIPConnection/WANPPPConnection controlURL.";
                    continue;
                }

                string addBody = BuildAddPortMappingSoapBody(
                    service.Value.serviceType,
                    port,
                    "UDP",
                    port,
                    parsedIp.ToString(),
                    safeDescription,
                    clampedLease);

                Log(logSink, $"[UPnP] Sending AddPortMapping (UDP {port} -> {parsedIp}:{port}) to {service.Value.controlUri} ({service.Value.serviceType})...");

                (bool ok, int statusCode, string responseBody) = await effectiveTransport
                    .SendSoapActionAsync(service.Value.controlUri, service.Value.serviceType, "AddPortMapping", addBody, cancellationToken)
                    .ConfigureAwait(false);

                if (ok)
                {
                    string? externalIp = await TryQueryExternalIpAsync(
                        effectiveTransport, service.Value.controlUri, service.Value.serviceType, cancellationToken).ConfigureAwait(false);

                    string successMsg = string.IsNullOrWhiteSpace(externalIp)
                        ? $"UPnP mapped UDP {port} -> {parsedIp}:{port} via gateway {locationUri.Host}."
                        : $"UPnP mapped UDP {port} ({externalIp}:{port} -> {parsedIp}:{port}) via gateway {locationUri.Host}.";

                    Log(logSink, $"[UPnP] [OK] {successMsg}");
                    return new UpnpPortMappingResult(
                        true,
                        "UDP",
                        port,
                        port,
                        locationUri.Host,
                        externalIp,
                        service.Value.serviceType,
                        successMsg);
                }

                lastError = $"Gateway {locationUri.Host} rejected AddPortMapping (HTTP {statusCode}): {TrimForDiagnostic(responseBody)}";
                Log(logSink, $"[UPnP] [WARN] {lastError}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = $"Gateway {locationUri.Host} error ({ex.GetType().Name}: {ex.Message}).";
                Log(logSink, $"[UPnP] [WARN] {lastError}");
            }
        }

        return new UpnpPortMappingResult(false, "UDP", port, port, null, null, null, lastError);
    }

    public static async Task<bool> TryDeleteUdpPortMappingAsync(
        int port,
        TimeSpan? discoveryTimeout = null,
        IUpnpDiscoveryTransport? transport = null,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        IUpnpDiscoveryTransport effectiveTransport = transport ?? DefaultTransport;
        TimeSpan timeout = discoveryTimeout ?? TimeSpan.FromMilliseconds(1200);

        try
        {
            IReadOnlyList<string> locations = await effectiveTransport.DiscoverLocationUrlsAsync(timeout, cancellationToken).ConfigureAwait(false);
            foreach (string rawLocation in locations)
            {
                if (!Uri.TryCreate(rawLocation, UriKind.Absolute, out Uri? locationUri) ||
                    !IsPrivateOrLoopbackHost(locationUri.Host))
                {
                    continue;
                }

                string? xml = await effectiveTransport.FetchDeviceDescriptionXmlAsync(locationUri, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(xml))
                {
                    continue;
                }

                (Uri controlUri, string serviceType)? service = ParseWanControlEndpoint(locationUri, xml);
                if (service is null)
                {
                    continue;
                }

                string deleteBody = BuildDeletePortMappingSoapBody(service.Value.serviceType, port, "UDP");
                (bool ok, _, _) = await effectiveTransport
                    .SendSoapActionAsync(service.Value.controlUri, service.Value.serviceType, "DeletePortMapping", deleteBody, cancellationToken)
                    .ConfigureAwait(false);

                if (ok)
                {
                    Log(logSink, $"[UPnP] Released UDP {port} mapping on gateway {locationUri.Host}.");
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(logSink, $"[UPnP] DeletePortMapping cleanup notice for UDP {port}: {ex.Message}");
        }

        return false;
    }

    public static string? ExtractSsdpLocationHeader(string ssdpResponse)
    {
        if (string.IsNullOrWhiteSpace(ssdpResponse))
        {
            return null;
        }

        foreach (string rawLine in ssdpResponse.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["LOCATION:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public static (Uri ControlUri, string ServiceType)? ParseWanControlEndpoint(Uri descriptorUri, string deviceXml)
    {
        if (string.IsNullOrWhiteSpace(deviceXml))
        {
            return null;
        }

        XDocument doc = XDocument.Parse(deviceXml, LoadOptions.None);
        string? urlBaseText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("URLBase", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        Uri baseUri = !string.IsNullOrWhiteSpace(urlBaseText) && Uri.TryCreate(urlBaseText, UriKind.Absolute, out Uri? parsedBase)
            ? parsedBase
            : new Uri($"{descriptorUri.Scheme}://{descriptorUri.Authority}/");

        var services = doc.Descendants().Where(e => e.Name.LocalName.Equals("service", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (string preferredType in SupportedServiceTypes)
        {
            foreach (XElement serviceEl in services)
            {
                string? svcType = serviceEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("serviceType", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                if (!string.Equals(svcType, preferredType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? controlPath = serviceEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("controlURL", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(controlPath))
                {
                    continue;
                }

                if (Uri.TryCreate(baseUri, controlPath, out Uri? resolved) &&
                    (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps) &&
                    IsPrivateOrLoopbackHost(resolved.Host))
                {
                    return (resolved, svcType!);
                }
            }
        }

        return null;
    }

    public static string BuildAddPortMappingSoapBody(
        string serviceType,
        int externalPort,
        string protocol,
        int internalPort,
        string internalClientIp,
        string description,
        int leaseDurationSeconds)
    {
        string escapedService = SecurityElement.Escape(serviceType) ?? serviceType;
        string escapedProtocol = SecurityElement.Escape(protocol) ?? protocol;
        string escapedClient = SecurityElement.Escape(internalClientIp) ?? internalClientIp;
        string escapedDesc = SecurityElement.Escape(description) ?? description;

        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                  <s:Body>
                    <u:AddPortMapping xmlns:u="{escapedService}">
                      <NewRemoteHost></NewRemoteHost>
                      <NewExternalPort>{externalPort.ToString(CultureInfo.InvariantCulture)}</NewExternalPort>
                      <NewProtocol>{escapedProtocol}</NewProtocol>
                      <NewInternalPort>{internalPort.ToString(CultureInfo.InvariantCulture)}</NewInternalPort>
                      <NewInternalClient>{escapedClient}</NewInternalClient>
                      <NewEnabled>1</NewEnabled>
                      <NewPortMappingDescription>{escapedDesc}</NewPortMappingDescription>
                      <NewLeaseDuration>{leaseDurationSeconds.ToString(CultureInfo.InvariantCulture)}</NewLeaseDuration>
                    </u:AddPortMapping>
                  </s:Body>
                </s:Envelope>
                """;
    }

    public static string BuildDeletePortMappingSoapBody(string serviceType, int externalPort, string protocol)
    {
        string escapedService = SecurityElement.Escape(serviceType) ?? serviceType;
        string escapedProtocol = SecurityElement.Escape(protocol) ?? protocol;

        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                  <s:Body>
                    <u:DeletePortMapping xmlns:u="{escapedService}">
                      <NewRemoteHost></NewRemoteHost>
                      <NewExternalPort>{externalPort.ToString(CultureInfo.InvariantCulture)}</NewExternalPort>
                      <NewProtocol>{escapedProtocol}</NewProtocol>
                    </u:DeletePortMapping>
                  </s:Body>
                </s:Envelope>
                """;
    }

    private static async Task<string?> TryQueryExternalIpAsync(
        IUpnpDiscoveryTransport transport,
        Uri controlUri,
        string serviceType,
        CancellationToken cancellationToken)
    {
        try
        {
            string escapedService = SecurityElement.Escape(serviceType) ?? serviceType;
            string body = $"""
                           <?xml version="1.0" encoding="utf-8"?>
                           <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                             <s:Body>
                               <u:GetExternalIPAddress xmlns:u="{escapedService}" />
                             </s:Body>
                           </s:Envelope>
                           """;

            (bool ok, _, string responseXml) = await transport
                .SendSoapActionAsync(controlUri, serviceType, "GetExternalIPAddress", body, cancellationToken)
                .ConfigureAwait(false);
            if (!ok || string.IsNullOrWhiteSpace(responseXml))
            {
                return null;
            }

            XDocument doc = XDocument.Parse(responseXml, LoadOptions.None);
            string? ip = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("NewExternalIPAddress", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            return IPAddress.TryParse(ip, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
                ? parsed.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPrivateOrLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out IPAddress? addr))
        {
            return false;
        }

        if (IPAddress.IsLoopback(addr))
        {
            return true;
        }

        if (addr.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = addr.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }

    private static string SanitizeDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "Project KONGOR";
        }

        StringBuilder sb = new(Math.Min(description.Length, 48));
        foreach (char c in description.Trim())
        {
            if (sb.Length >= 48) break;
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "Project KONGOR";
    }

    private static string TrimForDiagnostic(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "empty response";
        }

        string singleLine = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 140 ? singleLine : singleLine[..140] + "...";
    }

    private static void Log(Action<string>? logSink, string message)
    {
        Trace.WriteLine(message);
        logSink?.Invoke(message);
    }

    private sealed class SocketAndHttpUpnpTransport : IUpnpDiscoveryTransport
    {
        private static readonly HttpClient Http = SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(3));
        private static readonly IPEndPoint SsdpMulticastEndPoint = new(IPAddress.Parse("239.255.255.250"), 1900);

        private static readonly string[] SearchTargets =
        [
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1"
        ];

        public async Task<IReadOnlyList<string>> DiscoverLocationUrlsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            HashSet<string> locations = new(StringComparer.OrdinalIgnoreCase);

            using UdpClient udp = new(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            foreach (string st in SearchTargets)
            {
                string payload =
                    $"M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: 239.255.255.250:1900\r\n" +
                    $"MAN: \"ssdp:discover\"\r\n" +
                    $"MX: 1\r\n" +
                    $"ST: {st}\r\n\r\n";
                byte[] bytes = Encoding.ASCII.GetBytes(payload);
                await udp.SendAsync(bytes, SsdpMulticastEndPoint, cancellationToken).ConfigureAwait(false);
            }

            using CancellationTokenSource deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineCts.CancelAfter(timeout);

            while (!deadlineCts.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult packet = await udp.ReceiveAsync(deadlineCts.Token).ConfigureAwait(false);
                    string text = Encoding.UTF8.GetString(packet.Buffer);
                    string? loc = ExtractSsdpLocationHeader(text);
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        locations.Add(loc);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
            }

            return [.. locations];
        }

        public async Task<string?> FetchDeviceDescriptionXmlAsync(Uri locationUri, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await Http.GetAsync(locationUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<(bool Succeeded, int StatusCode, string ResponseBody)> SendSoapActionAsync(
            Uri controlUri,
            string serviceType,
            string actionName,
            string soapBodyXml,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage req = new(HttpMethod.Post, controlUri)
            {
                Content = new StringContent(soapBodyXml, Encoding.UTF8, "text/xml")
            };
            req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{actionName}\"");

            using HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
    }
}
