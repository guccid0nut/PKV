// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.SourceGenerator.Attributes;

/// <summary>
///     Marks a partial class for automatic implementation of missing interface members.
///     The source generator will create stub implementations that throw <see cref="NotImplementedException" /> for all
///     interface members not explicitly implemented in the class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AutoImplementMissingMembersAttribute : Attribute;
