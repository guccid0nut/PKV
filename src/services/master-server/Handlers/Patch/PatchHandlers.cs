// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Handlers.Patch;

public static class PatchHandlers
{
    // NOTE: Version 5.0.0 (Latest) intentionally aliases the canonical 4.10.1.0 base manifest SHA-1 hashes and
    // archive byte lengths across all supported distributions (wac, lac, mac, was-*, las-*). Project Kongor
    // advertises 5.0.0 as the active protocol compatibility version while validating against the immutable
    // 4.10.1.0 base manifest archives on disk.
    private static List<PatchDetails> DistributionVersions { get; } =
    [
        // Version 5.0.0 (Latest)
        new()
        {
            DistributionIdentifier = "wac",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "33b5151fca1704aff892cf76e41f3986634d38bb",
            ManifestArchiveSizeInBytes = "3628533",
            Latest = true
        },
        new()
        {
            DistributionIdentifier = "lac",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "3977f63f62954e06038c34572a00656a8cb7e311",
            ManifestArchiveSizeInBytes = "6122296",
            Latest = true
        },
        new()
        {
            DistributionIdentifier = "mac",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "ce18408b94a14a968736bb39213daac9a09e4026",
            ManifestArchiveSizeInBytes = "3933009",
            Latest = true
        },
        new()
        {
            DistributionIdentifier = "was-crIac6LASwoafrl8FrOa",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "37fc03fc781925e00ae633f8855f3bbd2996c5e7",
            ManifestArchiveSizeInBytes = "1507915",
            Latest = true
        },
        new()
        {
            DistributionIdentifier = "las-crIac6LASwoafrl8FrOa",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "8ba887c3de95b0b0d33b461bdb4721f29ace952e",
            ManifestArchiveSizeInBytes = "2705984",
            Latest = true
        },

        // Version 4.10.1 (Legacy Compatibility)
        new()
        {
            DistributionIdentifier = "wac",
            Version = "4.10.1",
            FullVersion = "4.10.1.0",
            ManifestArchiveSHA1Hash = "33b5151fca1704aff892cf76e41f3986634d38bb",
            ManifestArchiveSizeInBytes = "3628533",
            Latest = false
        },
        new()
        {
            DistributionIdentifier = "lac",
            Version = "4.10.1",
            FullVersion = "4.10.1.0",
            ManifestArchiveSHA1Hash = "3977f63f62954e06038c34572a00656a8cb7e311",
            ManifestArchiveSizeInBytes = "6122296",
            Latest = false
        },
        new()
        {
            DistributionIdentifier = "mac",
            Version = "4.10.1",
            FullVersion = "4.10.1.0",
            ManifestArchiveSHA1Hash = "ce18408b94a14a968736bb39213daac9a09e4026",
            ManifestArchiveSizeInBytes = "3933009",
            Latest = false
        },
        new()
        {
            DistributionIdentifier = "was-crIac6LASwoafrl8FrOa",
            Version = "4.10.1",
            FullVersion = "4.10.1.0",
            ManifestArchiveSHA1Hash = "37fc03fc781925e00ae633f8855f3bbd2996c5e7",
            ManifestArchiveSizeInBytes = "1507915",
            Latest = false
        },
        new()
        {
            DistributionIdentifier = "las-crIac6LASwoafrl8FrOa",
            Version = "4.10.1",
            FullVersion = "4.10.1.0",
            ManifestArchiveSHA1Hash = "8ba887c3de95b0b0d33b461bdb4721f29ace952e",
            ManifestArchiveSizeInBytes = "2705984",
            Latest = false
        }
    ];

    public static IReadOnlyList<PatchDetails> GetAllDistributionVersions() => DistributionVersions;

    public static PatchDetails? GetLatestClientPatchDetails(string distribution)
    {
        return DistributionVersions.FirstOrDefault(details =>
            details.DistributionIdentifier.Equals(distribution) && details.Latest.Equals(true));
    }

    public static PatchDetails? GetClientPatchDetails(string distribution, string version)
    {
        return DistributionVersions.FirstOrDefault(details =>
            details.DistributionIdentifier.Equals(distribution) &&
            (details.Version.Equals(version) || details.FullVersion.Equals(version)));
    }
}
