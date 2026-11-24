// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Configuration;

/// <summary>
///     Result of comparing the vault with the generated <c>.env</c> file and with OS environment variables.
///     Contains variable NAMES only, never values.
/// </summary>
public sealed record SecretsDriftReport(
    bool EnvFileTracked,
    bool EnvFileMissing,
    IReadOnlyList<string> EnvFileMismatchedNames,
    IReadOnlyList<string> ShadowingEnvironmentVariables)
{
    public static SecretsDriftReport None { get; } = new(false, false, [], []);

    public bool HasEnvFileDrift => EnvFileTracked && (EnvFileMissing || EnvFileMismatchedNames.Count > 0);

    public bool HasDrift => HasEnvFileDrift || ShadowingEnvironmentVariables.Count > 0;
}
