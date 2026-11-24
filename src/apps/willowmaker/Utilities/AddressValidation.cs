// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using System.Net.Sockets;

namespace WILLOWMAKER.Core.Utilities;

/// <summary>
///     Provides address validation and URL normalisation for content delivery network endpoints and services.
/// </summary>
public static class AddressValidation
{
    /// <summary>
    ///     Normalises a content delivery network address or URL into an absolute URL ending with a trailing slash.
    ///     Loopback, private network, CGNAT/mesh, and explicit :55555 hosts automatically receive an http:// prefix if missing,
    ///     while remote hosts receive an https:// prefix.
    /// </summary>
    public static string NormaliseCDNURL(string? rawURL)
    {
        if (string.IsNullOrWhiteSpace(rawURL))
            return string.Empty;

        string trimmed = rawURL.Trim();

        // An address which already has a scheme keeps it, so validation rejects an unsupported scheme instead of nesting it
        string urlWithScheme = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : ShouldDefaultToHttp(trimmed) ? $"http://{trimmed}" : $"https://{trimmed}";

        return urlWithScheme.EndsWith('/') ? urlWithScheme : $"{urlWithScheme}/";
    }

    /// <summary>
    ///     Determines whether the specified raw address or URL string is syntactically valid for network communication.
    /// </summary>
    public static bool IsValidAddress(string? rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            return false;

        // A query (?), fragment (#), or user information (@) would displace appended manifest/package paths
        foreach (char character in rawAddress)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '"' or ';' or '?' or '#' or '@')
                return false;
        }

        string? candidateURL = rawAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rawAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? rawAddress
            : rawAddress.Contains("://", StringComparison.Ordinal)
                ? null
                : $"http://{rawAddress}";

        if (candidateURL is null)
            return false;

        if (Uri.TryCreate(candidateURL, UriKind.Absolute, out Uri? uri) is false)
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        if (string.IsNullOrEmpty(uri.Host))
            return false;

        return Uri.CheckHostName(uri.DnsSafeHost) is not UriHostNameType.Unknown;
    }

    private static bool ShouldDefaultToHttp(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        if (address.Contains("://", StringComparison.Ordinal))
            return false;

        if (!Uri.TryCreate($"http://{address}", UriKind.Absolute, out Uri? uri))
        {
            return IsLoopbackHost(address);
        }

        if (HasExplicitPort55555(address, uri))
        {
            return true;
        }

        string host = uri.DnsSafeHost;

        if (IsLoopbackHost(host))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            return IsPrivateOrLocalNetwork(ip);
        }

        return false;
    }

    private static bool HasExplicitPort55555(string address, Uri uri)
    {
        return uri.Port == 55555;
    }

    private static bool IsLoopbackHost(string hostOrAddress)
    {
        if (string.IsNullOrWhiteSpace(hostOrAddress))
            return false;

        string host = hostOrAddress;
        if (host.StartsWith('['))
        {
            int closingBracketIndex = host.IndexOf(']');
            if (closingBracketIndex > 0)
            {
                host = host[1..closingBracketIndex];
            }
        }
        else
        {
            int delimiterIndex = host.IndexOfAny(['/', ':']);
            if (delimiterIndex >= 0)
            {
                host = host[..delimiterIndex];
            }
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            return IPAddress.IsLoopback(ip);
        }

        return false;
    }

    private static bool IsPrivateOrLocalNetwork(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = ip.GetAddressBytes();
            bool isUla = (bytes[0] & 0xFE) == 0xFC; // fc00::/7
            bool isLinkLocal = ip.IsIPv6LinkLocal || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80); // fe80::/10
            return isUla || isLinkLocal;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            bool isRfc1918 = bytes[0] == 10 ||
                             (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                             (bytes[0] == 192 && bytes[1] == 168);
            bool isCgnat = bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127; // RFC 6598 CGNAT / Tailscale / ZeroTier (100.64.0.0/10)
            bool isLinkLocal = bytes[0] == 169 && bytes[1] == 254; // Link-local IPv4 (169.254.0.0/16)

            return isRfc1918 || isCgnat || isLinkLocal;
        }

        return false;
    }
}
