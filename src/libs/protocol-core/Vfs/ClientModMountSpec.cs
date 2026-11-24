// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Net;

namespace Kongor.Core.Vfs;

/// <summary>
/// Canonical VFS mod mount specification and master-server endpoint helpers shared between
/// WILLOWMAKER and Kongor.Cli without coupling desktop launchers to MCP tooling.
/// </summary>
public static class ClientModMountSpec
{
    public const int CanonicalSimulationTickRate = 20;

    public static readonly IReadOnlyList<string> CanonicalMountTiers =
    [
        "base",
        "game",
        "ClientPackageFiles",
        "../shared",
        "KONGOR/configuration",
        "KONGOR/updates",
        "KONGOR/extensions",
        "override"
    ];

    public static readonly IReadOnlyList<string> PureCompatibilityMountTiers =
    [
        "base",
        "game",
        "ClientPackageFiles",
        "../shared",
        "KONGOR/configuration",
        "KONGOR/updates",
        "KONGOR/extensions"
    ];

    public static readonly IReadOnlyList<string> SafeModeMountTiers =
    [
        "base",
        "game",
        "ClientPackageFiles",
        "../shared",
        "KONGOR/configuration",
        "KONGOR/updates",
        "override"
    ];

    public static readonly IReadOnlyList<string> SafeModePureCompatibilityMountTiers =
    [
        "base",
        "game",
        "ClientPackageFiles",
        "../shared",
        "KONGOR/configuration",
        "KONGOR/updates"
    ];

    /// <summary>
    /// Builds the K2 <c>-mod</c> command-line argument.
    /// When <paramref name="includeOverride"/> is <c>false</c> ("Pure 4.10 Compatibility Mode"),
    /// omits the <c>override</c> tier for strict third-party vanilla master server compatibility.
    /// When <paramref name="isSafeMode"/> is <c>true</c>, omits <c>KONGOR/extensions</c> while preserving
    /// the <c>override</c> tier for critical core entity and UI bugfixes.
    /// </summary>
    public static string GetCanonicalModString(bool includeOverride = true, bool isSafeMode = false)
    {
        IReadOnlyList<string> tiers = (includeOverride, isSafeMode) switch
        {
            (true, false) => CanonicalMountTiers,
            (false, false) => PureCompatibilityMountTiers,
            (true, true) => SafeModeMountTiers,
            (false, true) => SafeModePureCompatibilityMountTiers
        };
        return $"-mod {string.Join(";", tiers)}";
    }

    /// <summary>
    /// Returns <c>true</c> only when <paramref name="endpoint"/> resolves to a local loopback host
    /// (<c>localhost</c>, <c>127.0.0.0/8</c>, or <c>::1</c>). Used to gate local-cluster
    /// <c>-chatserver 127.0.0.1:11031</c> overrides so remote master servers can route chat normally.
    /// </summary>
    public static bool IsLocalhostEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        string trimmed = endpoint.Trim();
        string candidateUri = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"http://{trimmed}";

        if (Uri.TryCreate(candidateUri, UriKind.Absolute, out Uri? parsed) && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            if (parsed.IsLoopback)
            {
                return true;
            }

            string host = parsed.Host.Trim('[', ']');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(host, out IPAddress? ip))
            {
                return IPAddress.IsLoopback(ip);
            }

            return false;
        }

        string fallbackHost = trimmed;
        int slashIdx = fallbackHost.IndexOf('/');
        if (slashIdx >= 0)
        {
            fallbackHost = fallbackHost[..slashIdx];
        }

        if (fallbackHost.StartsWith('[') && fallbackHost.Contains(']'))
        {
            int endBracket = fallbackHost.IndexOf(']');
            fallbackHost = fallbackHost[1..endBracket];
        }
        else
        {
            int colonIdx = fallbackHost.IndexOf(':');
            if (colonIdx >= 0)
            {
                fallbackHost = fallbackHost[..colonIdx];
            }
        }

        if (string.Equals(fallbackHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(fallbackHost, out IPAddress? fallbackIp) && IPAddress.IsLoopback(fallbackIp);
    }
}
