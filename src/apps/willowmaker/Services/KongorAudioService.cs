// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: KongorAudioService.cs
// System: WILLOWMAKER Launcher - Dynamic Sound Effect Service
// Policy: Strict Explicit Typing (NO 'var') - Zero Git Repo Storage
// ============================================================================

namespace WILLOWMAKER.Core.Services;

public static class KongorAudioService
{
    private static string? _cachedSoundFile;

    /// <summary>
    ///     Extracts Kongor taunt sound directly from client .s2z archives at runtime into temp path.
    ///     Plays sound dynamically on user click. Zero files saved in git repo.
    /// </summary>
    public static void PlayKongorTauntSound(string clientRoot)
    {
        try
        {
            if (_cachedSoundFile == null || !File.Exists(_cachedSoundFile))
                _cachedSoundFile = ExtractKongorAudioToTemp(clientRoot);

            if (_cachedSoundFile != null && File.Exists(_cachedSoundFile))
            {
                if (OperatingSystem.IsWindows() && BinauralSpatialAudioSynthesizer.HasAudioOutputDevice())
                {
                    if (_cachedSoundFile.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] bytes = File.ReadAllBytes(_cachedSoundFile);
                        BinauralSpatialAudioSynthesizer.PlayWavBytes(bytes, loop: false);
                        return;
                    }
                }
            }

            // Fallback synthesized directional audio chime for instant feedback without external process execution
            BinauralSpatialAudioSynthesizer.PlayDirectionalChime(0f, 1.0f);
        }
        catch
        {
            // Silently handle audio play errors
        }
    }

    private static string? ExtractKongorAudioToTemp(string clientRoot)
    {
        if (string.IsNullOrWhiteSpace(clientRoot) || !Directory.Exists(clientRoot)) return null;

        string gameDir = Path.Combine(clientRoot, "game");
        if (!Directory.Exists(gameDir)) return null;

        string[] targetSounds =
        [
            "shared/effects/taunts/kongor/sounds/kongortaunt.wav",
            "npcs/kongor/sounds/taunt.wav",
            "npcs/kongor/sounds/attack_1.wav",
            "shared/effects/taunts/kongor/sounds/kongortaunt.ogg",
            "npcs/kongor/sounds/taunt.ogg",
            "npcs/kongor/sounds/attack_1.ogg"
        ];

        string[] s2zFiles = Directory.GetFiles(gameDir, "*.s2z", SearchOption.TopDirectoryOnly);
        foreach (string targetSound in targetSounds)
        foreach (string s2zPath in s2zFiles)
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(s2zPath);
                foreach (ZipArchiveEntry entry in zip.Entries)
                    if (entry.FullName.Contains(targetSound, StringComparison.OrdinalIgnoreCase))
                    {
                        string ext = Path.GetExtension(entry.FullName);
                        string tempFile = Path.Combine(Path.GetTempPath(), $"willowmaker_kongor_taunt{ext}");
                        using Stream entryStream = entry.Open();
                        using FileStream fileStream = File.Create(tempFile);
                        entryStream.CopyTo(fileStream);
                        return tempFile;
                    }
            }
            catch
            {
                // Continue scanning archives
            }

        return null;
    }
}
