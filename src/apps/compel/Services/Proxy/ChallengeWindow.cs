// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Proxy;

/// <summary>
///     Why a datagram was or was not admitted under a challenge.
/// </summary>
public enum ChallengeAdmission
{
    Admitted,
    OverQuota,
    Duplicate
}

/// <summary>
///     One challenge's packet allowance for one client.
///     The client stamps an increasing counter into each datagram and restarts it when it accepts a new challenge, so each window admits every counter below its quota exactly once.
///     The quota is checked before the counter is used to index the seen set, because the counter arrives from the client and may be any value the field can hold.
/// </summary>
public sealed class ChallengeWindow
{
    private readonly Lock admissionLock = new();
    private readonly bool[] seen;

    public ChallengeWindow(uint challenge, ushort quota)
    {
        Challenge = challenge;
        Quota = quota;
        seen = new bool[quota];
    }

    public uint Challenge { get; }

    public ushort Quota { get; }

    /// <summary>
    ///     Admits <paramref name="counter"/> if it is within the quota and has not been seen under this challenge before.
    /// </summary>
    public bool TryAdmit(ushort counter, out ChallengeAdmission admission)
    {
        if (counter >= Quota)
        {
            admission = ChallengeAdmission.OverQuota;

            return false;
        }

        lock (admissionLock)
        {
            if (seen[counter])
            {
                admission = ChallengeAdmission.Duplicate;

                return false;
            }

            seen[counter] = true;
        }

        admission = ChallengeAdmission.Admitted;

        return true;
    }
}
