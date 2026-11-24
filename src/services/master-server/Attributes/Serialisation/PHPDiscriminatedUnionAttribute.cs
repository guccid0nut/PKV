// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Attributes.Serialisation;

/// <summary>
///     PHP serialisation filter for discriminated union properties.
///     This attribute needs to annotate discriminated union properties in order to automatically unwrap and serialise the
///     inner value.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class PHPDiscriminatedUnionAttribute : Attribute
{
    /// <summary>
    ///     Serialises a discriminated union property key and unwrapped value to PHP format.
    /// </summary>
    public string Serialize(object key, object? value)
    {
        object? processedValue = value is null ? null : DiscriminatedUnionUnwrapper.ProcessValue(value);
        using PooledArrayBufferWriter writer = new(256);
        PhpSerializer.Serialize(writer, key);
        PhpSerializer.Serialize(writer, processedValue);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }
}
