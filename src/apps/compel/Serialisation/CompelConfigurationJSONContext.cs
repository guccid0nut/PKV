// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Serialisation;

/// <summary>
///     Source-generated serialisation metadata for the on-disk "COMPEL.json" configuration file.
///     Required because the application is published with Native AOT, which strips the reflection-based
///     <see cref="JsonSerializer" /> paths.
///     Default (PascalCase) property naming is used so the file's keys read as "UserName", "Value", "Description", and so
///     on.
/// </summary>
[JsonSerializable(typeof(CompelConfigurationFile))]
internal sealed partial class CompelConfigurationJSONContext : JsonSerializerContext;
