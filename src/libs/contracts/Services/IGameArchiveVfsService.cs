// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Services;

/// <summary>
///     Service abstraction for accessing Heroes of Newerth game archives (.s2z), textures, models, and audio.
/// </summary>
public interface IGameArchiveVfsService
{
    bool FileExists(string vfsPath);
    Stream? OpenFile(string vfsPath);
    byte[]? ReadAllBytes(string vfsPath);
    string? ReadAllText(string vfsPath);
    byte[]? GetModelGlb(string mdfPath);
    byte[]? GetHeroModelGlb(string heroName, string? altAvatar = null, string? resourcePath = null);
    byte[]? GetTextureBytes(string texturePath);
    byte[]? GetTexturePng(string texturePath);
    byte[]? GetHeroSound(string heroName, string? altAvatar = null, string? actionName = null);
    HeroEntityInfoDto? GetHeroEntityInfo(string heroName);
    bool PrebakeHeroModel(string heroName, string? altAvatar = null);
    IEnumerable<string> FindFiles(string prefix);
}
