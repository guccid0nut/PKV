// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Services.Models;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Pre-flight static VFS completeness checker interface.
/// Statically audits all UI packages, MDF manifests, and material definitions across the K2 VFS hierarchy.
/// </summary>
public interface IVfsCompletenessCheckerService
{
    Task<VfsCompletenessReport> AuditAsync(VfsCompletenessOptions? options = null, CancellationToken ct = default);
}
