// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.Configuration;

public class OperationalConfigurationCDN
{
    public required string Host { get; set; }

    public required string PrimaryPatchURL { get; set; }

    public required string SecondaryPatchURL { get; set; }

    public string? ServeFromLocalDirectoryURL { get; set; }

    public string? LocalDirectory { get; set; }
}
