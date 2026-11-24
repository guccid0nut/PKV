// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;

namespace KONGOR.MasterServer.Models.RequestResponse.Patch;

public class PatchDetails
{
    private readonly string _distributionIdentifier = string.Empty;
    private readonly string _version = string.Empty;
    private readonly string _fullVersion = string.Empty;
    private readonly string _manifestArchiveSha1Hash = string.Empty;
    private readonly string _manifestArchiveSizeInBytes = string.Empty;

    public required string DistributionIdentifier
    {
        get => _distributionIdentifier;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "DistributionIdentifier cannot be null or whitespace. Fix: Provide a valid distribution identifier (e.g. 'wac', 'lac', 'mac') in PatchHandlers.cs.",
                    nameof(DistributionIdentifier));
            }

            _distributionIdentifier = value;
        }
    }

    public required string Version
    {
        get => _version;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Version cannot be null or whitespace. Fix: Provide a valid semantic version string (e.g. '4.10.1' or '5.0.0') in PatchHandlers.cs.",
                    nameof(Version));
            }

            _version = value;
        }
    }

    public required string FullVersion
    {
        get => _fullVersion;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "FullVersion cannot be null or whitespace. Fix: Provide a valid 4-part version string (e.g. '4.10.1.0' or '5.0.0.0') in PatchHandlers.cs.",
                    nameof(FullVersion));
            }

            _fullVersion = value;
        }
    }

    public required string ManifestArchiveSHA1Hash
    {
        get => _manifestArchiveSha1Hash;
        init
        {
            if (!IsValidSha1Hex(value))
            {
                throw new ArgumentException(
                    $"Invalid ManifestArchiveSHA1Hash '{value}'. Expected a 40-character lowercase hexadecimal SHA-1 digest (ensure ManifestArchiveSHA1Hash and ManifestArchiveSizeInBytes are not transposed in PatchHandlers.cs).",
                    nameof(ManifestArchiveSHA1Hash));
            }

            _manifestArchiveSha1Hash = value;
        }
    }

    public required string ManifestArchiveSizeInBytes
    {
        get => _manifestArchiveSizeInBytes;
        init
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedSize) ||
                parsedSize <= 0)
            {
                throw new ArgumentException(
                    $"Invalid ManifestArchiveSizeInBytes '{value}'. Expected a positive base-10 byte length string (ensure ManifestArchiveSizeInBytes and ManifestArchiveSHA1Hash are not transposed in PatchHandlers.cs).",
                    nameof(ManifestArchiveSizeInBytes));
            }

            _manifestArchiveSizeInBytes = value;
        }
    }

    public required bool Latest { get; set; }

    private static bool IsValidSha1Hex(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != 40)
        {
            return false;
        }

        for (int i = 0; i < candidate.Length; i++)
        {
            char c = candidate[i];
            bool isDigit = c is >= '0' and <= '9';
            bool isLowerHex = c is >= 'a' and <= 'f';
            if (!isDigit && !isLowerHex)
            {
                return false;
            }
        }

        return true;
    }
}
