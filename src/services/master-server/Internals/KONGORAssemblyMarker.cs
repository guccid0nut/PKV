// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics.CodeAnalysis;

namespace KONGOR.MasterServer.Internals;

/// <summary>
///     Used in the unit/integration tests project as a marker for the KONGOR.MasterServer assembly.
///     It acts as the type parameter for WebApplicationFactory, in order to point it to this compilation unit.
///     Any type in this project would also work, however using this pattern is the preferred practice.
/// </summary>
[SuppressMessage("Naming", "CA1715:Identifiers should have correct prefix",
    Justification = "Assembly marker interface for WebApplicationFactory")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public interface KONGORAssemblyMarker;
