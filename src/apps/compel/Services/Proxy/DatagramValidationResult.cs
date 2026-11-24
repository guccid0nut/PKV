// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Proxy;

/// <summary>
///     Represents the outcome of evaluating an incoming datagram against the proxy's length, challenge and counter rules.
/// </summary>
public readonly struct DatagramValidationResult
{
    public bool IsAdmitted { get; }

    public bool IsAuthenticatedChallenge { get; }

    public int ViolationWeight { get; }

    public string? DropReason { get; }

    private DatagramValidationResult(bool isAdmitted, bool isAuthenticatedChallenge, int violationWeight, string? dropReason)
    {
        IsAdmitted = isAdmitted;
        IsAuthenticatedChallenge = isAuthenticatedChallenge;
        ViolationWeight = violationWeight;
        DropReason = dropReason;
    }

    public static DatagramValidationResult Admitted(bool isAuthenticatedChallenge)
        => new(true, isAuthenticatedChallenge, 0, null);

    public static DatagramValidationResult Dropped(int violationWeight, string dropReason)
        => new(false, false, violationWeight, dropReason);
}
