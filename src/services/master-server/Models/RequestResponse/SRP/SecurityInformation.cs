// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

public partial class SecurityInformation
{
    /// <summary>
    ///     Used for the Tencent anti-DDoS protection component, which does network packet watermarking that gets verified by
    ///     the game server proxy.
    /// </summary>
    [PHPProperty("initial_vector")]
    public string InitialVector { get; set; } = "73088db5e71cfb6d";

    /// <summary>
    ///     Unknown.
    /// </summary>
    [PHPProperty("hash_code")]
    public string HashCode { get; set; } = "73088db5e71cfb6d43ae0bb4abf095dd43862200";

    /// <summary>
    ///     Unknown.
    /// </summary>
    [PHPProperty("key_version")]
    public string KeyVersion { get; set; } = "3e2d";
}
