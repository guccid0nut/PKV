// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

/// <summary>
///     Contract implemented by types with high-performance, zero-allocation compile-time generated PHP deserialization
///     routines.
/// </summary>
public interface IPhpDeserializable<out T>
{
    /// <summary>
    ///     Deserializes an instance from a <see cref="PhpHashtable" /> with zero reflection.
    /// </summary>
    static abstract T DeserializePhp(PhpHashtable table);
}
