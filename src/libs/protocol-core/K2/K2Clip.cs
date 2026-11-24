// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;

namespace ASPIRE.Common.K2;

public class K2Clip
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public int NumBones { get; set; }
    public int NumFrames { get; set; }
    public int FrameRate { get; set; } = 30;
    public List<K2BoneMotion> BoneMotions { get; set; } = [];
}

public class K2BoneMotion
{
    public int BoneIndex { get; set; }
    public string BoneName { get; set; } = string.Empty;
    public int AnimType { get; set; }
    public Vector3[] Translations { get; set; } = [];
    public Quaternion[] Rotations { get; set; } = [];
    public Vector3[] Scales { get; set; } = [];
}

public class K2Mdf
{
    public string ModelFile { get; set; } = string.Empty;
    public List<K2MdfAnim> Animations { get; set; } = [];
    public List<K2MdfAttachment> Attachments { get; set; } = [];
}

public class K2MdfAttachment
{
    public string Bone { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
}

public class K2MdfFrameEvent
{
    public int Frame { get; set; }
    public string Command { get; set; } = string.Empty;
    public string? SoundPath { get; set; }
    public string? EffectPath { get; set; }
}

public class K2MdfAnim
{
    public string Name { get; set; } = string.Empty;
    public string ClipPath { get; set; } = string.Empty;
    public int Fps { get; set; } = 30;
    public bool Loop { get; set; } = true;
    public List<K2MdfFrameEvent> FrameEvents { get; set; } = [];
}
