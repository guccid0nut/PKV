// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using OneOf;

namespace ASPIRE.Common.Serialization.PHP;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
/// <summary>
///     Attribute used to map a property to a specific PHP array key during serialization.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PHPPropertyAttribute : Attribute
{
    public PHPPropertyAttribute(string keyName)
    {
        PropertyKey = keyName;
        KeyName = keyName;
    }

    public PHPPropertyAttribute(int index)
    {
        PropertyKey = index;
        Index = index;
        IsIntKey = true;
    }

    public OneOf<int, string> PropertyKey { get; set; }
    public string KeyName { get; set; } = string.Empty;
    public int Index { get; set; }
    public bool IsIntKey { get; set; }
    public bool IsDiscriminatedUnion { get; set; }
}

/// <summary>
///     Alias attribute for PHPPropertyAttribute supporting camelCase attribute syntax.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PhpPropertyAttribute : PHPPropertyAttribute
{
    public PhpPropertyAttribute(string keyName) : base(keyName)
    {
    }

    public PhpPropertyAttribute(int index) : base(index)
    {
    }
}
