// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     3D Viewport Preset from heroes_preview.xml.
/// </summary>
[MemoryPackable]
public partial class PreviewPresetDto
{
    public string Type { get; set; } = string.Empty; // "small", "middle", "large", "preview", "special"
    public string ModelPath { get; set; } = string.Empty;
    public string EffectPath { get; set; } = string.Empty;
    public float ModelScale { get; set; } = 1.0f;
    public string ModelPosition { get; set; } = string.Empty;
    public string ModelAngle { get; set; } = string.Empty;
    public string Ambient { get; set; } = string.Empty;
    public float SunHeight { get; set; }
    public float SunAngle { get; set; }
    public string SunColor { get; set; } = string.Empty;
    public float PedestalScale { get; set; } = 1.0f;
    public bool UsingEffect { get; set; }
    public bool NewFeatureModel { get; set; }
}
