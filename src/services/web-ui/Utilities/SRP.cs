// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Core.Security;

namespace DAWNBRINGER.WebPortal.UI.Utilities;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
public static class SRP
{
    /// <summary>
    ///     Generates a 64-character long SHA256 hash of the account's password.
    ///     Delegates directly to <see cref="S2Crypto" />.
    /// </summary>
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
