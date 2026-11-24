// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ASPIRE.Common.Extensions.Cryptography;

#pragma warning disable CA5351
// ReSharper disable ConvertToExtensionBlock
// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
public static class CryptographyExtensions
{
    /// <summary>
    ///     Generates a hash code with a numeric value between 0 and 4,294,967,295 (unsigned integer).
    ///     Unlike .NET's GetHashCode(), this method will always generate the same numeric hash code for the same input value.
    /// </summary>
    public static uint GetDeterministicUInt32Hash(this object hashable)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(hashable.ToString() ?? string.Empty));

        uint first = BitConverter.ToUInt32(hash, 0);
        uint second = BitConverter.ToUInt32(hash, 4);
        uint third = BitConverter.ToUInt32(hash, 8);
        uint fourth = BitConverter.ToUInt32(hash, 12);

        return first ^ second ^ third ^ fourth;
    }

    /// <summary>
    ///     Generates a hash code with a numeric value between 0 and 2,147,483,647 (positive signed integer).
    ///     Unlike .NET's GetHashCode(), this method will always generate the same numeric hash code for the same input value.
    /// </summary>
    public static int GetDeterministicInt32Hash(this object hashable, bool absolute = true)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(hashable.ToString() ?? string.Empty));

        int first = BitConverter.ToInt32(hash, 0);
        int second = BitConverter.ToInt32(hash, 4);
        int third = BitConverter.ToInt32(hash, 8);
        int fourth = BitConverter.ToInt32(hash, 12);

        return absolute ? Math.Abs(first ^ second ^ third ^ fourth) : first ^ second ^ third ^ fourth;
    }
}
