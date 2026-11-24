// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

public static class UserSecretsConstants
{
    /// <summary>
    ///     The canonical unified solution User Secrets ID shared across all NEXUS projects via Directory.Build.props.
    /// </summary>
    public const string SolutionUserSecretsId = "00005072-6f6a-6563-7420-4b4f4e474f52";

    /// <summary>
    ///     Legacy project-specific User Secrets IDs used prior to the unified solution vault migration.
    /// </summary>
    public static readonly string[] LegacyProjectSecretsIds =
    [
        "00005072-6f6a-6563-7420-4b4f4e474f52", // ASPIRE.ApplicationHost (Canonical)
        "d45d6029-7f88-483f-90e0-4ff785297640", // KONGOR.MasterServer
        "f1e1f81d-ed00-40b6-8d39-0b82a10a6338", // DAWNBRINGER.WebPortal.UI
        "b5f9c02d-2345-5678-90bc-defa12345678", // ZORGATH.WebPortal.API
        "a4f8b91c-1234-4567-89ab-cdef01234567", // TRANSMUTANSTEIN.ChatServer
        "project-kongor-compel" // COMPEL Game Server Manager
    ];
}
