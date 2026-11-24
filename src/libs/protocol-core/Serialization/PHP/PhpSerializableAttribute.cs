// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

/// <summary>
///     Attribute indicating that a class or struct should have a high-performance, zero-allocation
///     PHP serializer generated at compile-time by the Roslyn Source Generator.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PhpSerializableAttribute : Attribute;
