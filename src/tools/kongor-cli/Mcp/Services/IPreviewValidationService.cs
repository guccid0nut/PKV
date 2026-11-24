// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

public sealed class PreviewValidationIssueDto
{
    public string HeroName { get; set; } = string.Empty;
    public string AltKey { get; set; } = string.Empty;
    public string AvatarType { get; set; } = string.Empty;
    public string MdfPath { get; set; } = string.Empty;
    public string Severity { get; set; } = "ERROR"; // "ERROR" (teapot guaranteed), "WARN" (missing anim), "INFO"
    public string IssueType { get; set; } = string.Empty; // "MISSING_MDF", "MISSING_MODEL", "MISSING_CLIP", "SKELETAL_MISMATCH"
    public string Description { get; set; } = string.Empty;
    public string? MissingFile { get; set; }
}

public sealed class PreviewValidationOptions
{
    public string? HeroFilter { get; set; }
    public bool StrictBoneCheck { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class PreviewValidationResultDto
{
    public string Status { get; set; } = "clean";
    public bool HasTeapotRisk { get; set; }
    public int TotalHeroesChecked { get; set; }
    public int TotalAvatarsChecked { get; set; }
    public int TeapotRiskCount { get; set; }
    public int WarningsCount { get; set; }
    public List<PreviewValidationIssueDto> Issues { get; set; } = [];
    public double ElapsedMs { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}

public interface IPreviewValidationService
{
    Task<PreviewValidationResultDto> ValidatePreviewsAsync(PreviewValidationOptions? options = null, CancellationToken cancellationToken = default);
    string? ResolveVfsPath(string vfsPath);
}
