// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Proxy;

/// <summary>
///     Which traffic a forwarder relays. The two kinds are quoted differently, because the reference proxy grants a voice client far fewer packets than a game client.
/// </summary>
public enum ProxyForwarderKind
{
    Game,
    Voice
}
