// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Media;
using System.Runtime.InteropServices;

namespace NEXUS.ControlPanel.GUI.Services;

public static partial class SoundEffectService
{
    private const uint SND_SYNC = 0x00000000;
    private const uint SND_ASYNC = 0x00000001;
    private const uint SND_NODEFAULT = 0x00000002;
    private const uint SND_FILENAME = 0x00020000;

    private static readonly Lock _quitLock = new();
    private static long _lastAlertTick;
    private static volatile bool _isQuitPlaying;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

    public static void PlayErrorAlert()
    {
        if (_isQuitPlaying) return;

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastAlertTick) < 3000) return; // Rate limit to once every 3 seconds max

        Interlocked.Exchange(ref _lastAlertTick, now);

        _ = Task.Run(() =>
        {
            try
            {
                if (_isQuitPlaying) return;

                string wavPath = ResolveSoundPath("ability_error.wav");
                if (!File.Exists(wavPath)) wavPath = ResolveSoundPath("ui_error.wav");

                if (File.Exists(wavPath))
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        _ = PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                    }
                    else
                    {
                        using SoundPlayer player = new(wavPath);
                        player.Play();
                    }
                }
            }
            catch
            {
                // Fallback gracefully if audio hardware is busy
            }
        });
    }

    public static void PlayBanjoQuitSync()
    {
        lock (_quitLock)
        {
            _isQuitPlaying = true;

            try
            {
                string wavPath = ResolveSoundPath("banjo_quit.wav");
                if (File.Exists(wavPath))
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        _ = PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_SYNC | SND_NODEFAULT);
                    }
                    else
                    {
                        using SoundPlayer player = new(wavPath);
                        player.Load();
                        player.PlaySync();
                    }
                }
            }
            catch
            {
                // Ignore sound playback exceptions on exit
            }
            finally
            {
                _isQuitPlaying = false;
            }
        }
    }

    private static string ResolveSoundPath(string fileName)
    {
        string? cleanroomFlag = Environment.GetEnvironmentVariable("NEXUS_FORCE_CLEANROOM_ASSETS") ??
                                Environment.GetEnvironmentVariable("KONGOR_FORCE_CLEANROOM_ASSETS");
        if (string.Equals(cleanroomFlag, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanroomFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string baseDir = AppContext.BaseDirectory;
        string direct = Path.Combine(baseDir, "Assets", "sounds", fileName);
        if (File.Exists(direct)) return direct;

        string directSounds = Path.Combine(baseDir, "sounds", fileName);
        if (File.Exists(directSounds)) return directSounds;

        DirectoryInfo? current = new(baseDir);
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidateOverlay = Path.Combine(current.FullName, "games", "control-panel-overlay", "sounds", fileName);
            if (File.Exists(candidateOverlay)) return candidateOverlay;

            string candidateSrc = Path.Combine(current.FullName, "src", "apps", "control-panel", "Assets", "sounds", fileName);
            if (File.Exists(candidateSrc)) return candidateSrc;

            string candidateDirect = Path.Combine(current.FullName, "Assets", "sounds", fileName);
            if (File.Exists(candidateDirect)) return candidateDirect;

            string candidate = Path.Combine(current.FullName, "NEXUS.ControlPanel.GUI", "Assets", "sounds", fileName);
            if (File.Exists(candidate)) return candidate;

            current = current.Parent;
        }

        return direct;
    }
}
