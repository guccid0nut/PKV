// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Modding.Core.Models;

namespace WILLOWMAKER.Core.ViewModels;

public enum ModType
{
    HonMod,
    AvatarS2z,
    AudioPack,
    ShaderPack
}

public partial class ModItemViewModel : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string Version { get; set; } = "1.0.0";

    [ObservableProperty] public partial string Author { get; set; } = "Community";

    [ObservableProperty] public partial string Category { get; set; } = "UI";

    [ObservableProperty] public partial string Description { get; set; } = string.Empty;

    [ObservableProperty] public partial string FilePath { get; set; } = string.Empty;

    [ObservableProperty] public partial ModType Type { get; set; } = ModType.HonMod;

    [ObservableProperty] public partial bool IsEnabled { get; set; } = false;

    [ObservableProperty] public partial bool IsFavorite { get; set; } = false;

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteColor));
        OnPropertyChanged(nameof(FavoriteToolTip));
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public string FavoriteColor => IsFavorite ? "#FBBF24" : "#64748B";

    public string FavoriteToolTip => IsFavorite
        ? "Pinned to Dashboard Favorites (Click to Unpin)"
        : "Pin to Dashboard Favorites (Click to Pin)";

    [ObservableProperty] public partial bool IsRepaired { get; set; } = false;

    [ObservableProperty] public partial string CompatibilityStatus { get; set; } = "Verified";

    [ObservableProperty] public partial string StatusBadgeColor { get; set; } = "#22C55E";

    [ObservableProperty] public partial string IconGlyph { get; set; } = "🎮";

    public string DisplayGlyph
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(IconGlyph) && IconGlyph != "🎮" && IconGlyph != "??")
                return IconGlyph;

            return Category switch
            {
                "Super Smash Bros" => "🥊",
                "Warcraft III" => "⚔",
                "Dota 2" => "🛡",
                "UI & HUD" or "Interface" or "UI" => "🖥️",
                "Audio & Sound" or "Audio" => "🔊",
                "Gameplay & Assists" or "Gameplay" => "⚡",
                "Graphics" or "Shader" or "Shaders" => "✨",
                "Maps" or "Minimap" or "Map" => "🗺️",
                "Utility" or "Repaired" => "🔧",
                _ => !string.IsNullOrWhiteSpace(IconGlyph) ? IconGlyph : "🎮"
            };
        }
    }

    public ModPackage? UnderlyingPackage { get; set; }
}
