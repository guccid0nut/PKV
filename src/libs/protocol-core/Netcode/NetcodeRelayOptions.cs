// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NetcodeRelayOptions.cs
// System: Kongor Core Netcode - Options Configuration for Client Netcode Relay
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Runtime rollout and fallback options for the client netcode UDP compression relay.
/// </summary>
public sealed class NetcodeRelayOptions
{
    public const string SectionName = "Netcode";

    /// <summary>
    ///     Whether the transparent client-side loopback UDP relay is enabled.
    ///     Defaults to false for safety until explicitly rolled out via configuration.
    /// </summary>
    public bool RelayEnabled { get; set; } = false;

    /// <summary>
    ///     Maximum allowable time in milliseconds for the first server response during handshake
    ///     before automatically falling back to direct UDP.
    /// </summary>
    public int HandshakeTimeoutMs { get; set; } = 500;

    /// <summary>
    ///     Whether XOR forward error correction (FEC) is enabled on client-to-server traffic.
    /// </summary>
    public bool EnableFec { get; set; } = false;
}
