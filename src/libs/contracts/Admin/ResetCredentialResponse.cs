// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Response payload returning a newly generated password or authentication token.
/// </summary>
public sealed class ResetCredentialResponse
{
    public string? Value { get; set; }
}
