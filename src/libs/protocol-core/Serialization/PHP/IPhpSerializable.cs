// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

/// <summary>
///     Contract implemented by types with high-performance, zero-allocation compile-time generated PHP serialization
///     routines.
/// </summary>
public interface IPhpSerializable
{
    /// <summary>
    ///     Serializes this instance directly into the provided <see cref="Utf8PhpWriter" /> with zero reflection.
    /// </summary>
    void SerializePhp(ref Utf8PhpWriter writer);
}
