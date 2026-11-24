// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.K2;

public class K2MeshMaterial
{
    public string Name { get; set; } = string.Empty;
    public byte[]? DiffusePngBytes { get; set; }
    public byte[]? NormalPngBytes { get; set; }
    public byte[]? MetallicRoughnessPngBytes { get; set; }
    public byte[]? GlowPngBytes { get; set; }
    public bool HasAlpha { get; set; }
    public bool IsAdditive { get; set; }
    public bool IsUnlit { get; set; }
    public float[]? SrgbBaseColorFactor { get; set; }
    public float Metallic { get; set; }
    public float Roughness { get; set; } = 0.85f;
}
