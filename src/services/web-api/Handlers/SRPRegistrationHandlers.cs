// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics.CodeAnalysis;
using Kongor.Core.Security;

namespace ZORGATH.WebPortal.API.Handlers;

public static class SRPRegistrationHandlers
{
    /// <summary>
    ///     Generates a 64-character long SHA256 hash of the account's password.
    ///     Delegates directly to <see cref="S2Crypto" />.
    /// </summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Required for legacy HoN S2 SRP protocol compatibility")]
    public static string ComputeSRPPasswordHash(string password, string salt, bool passwordIsHashed = false)
    {
        return passwordIsHashed
            ? S2Crypto.ComputeSrpFromPrehashedMd5(password, salt)
            : S2Crypto.ComputeSrpFromPlaintext(password, salt);
    }

    /// <summary>
    ///     Generates a 64-character long SRP password salt.
    /// </summary>
    public static string GenerateSRPPasswordSalt()
    {
        return S2Crypto.GenerateSrpSalt();
    }
}
