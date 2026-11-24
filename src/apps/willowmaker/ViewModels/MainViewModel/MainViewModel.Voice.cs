// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MainViewModel.Voice.cs
// System: WILLOWMAKER - Modern Opus Voice Chat & Microphone Diagnostic Panel
// Policy: Strict Explicit Typing (NO 'var') - Concentus Opus 2.2.2 Loopback
// ============================================================================

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kongor.Core.Services.Voice;
using WILLOWMAKER.Core.Constants;
using WILLOWMAKER.Core.Services.IPC;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsVoiceTesting { get; set; } = false;

    [ObservableProperty]
    public partial bool CanTestVoice { get; set; } = true;

    [ObservableProperty]
    public partial string VoiceTestButtonText { get; set; } = "🎙️ Test Microphone (3s)";

    [ObservableProperty]
    public partial string VoiceTestStatusText { get; set; } = "Concentus Opus 48 kHz Mono • Click to record 3s and audition loopback";

    [ObservableProperty]
    public partial string VoiceTestStatusColor { get; set; } = "#38BDF8";

    [ObservableProperty]
    public partial string VoiceDetectedMicText { get; set; } = "Microphone: Detecting hardware...";

    [ObservableProperty]
    public partial int VoiceTestProgress { get; set; } = 0;

    [ObservableProperty]
    public partial float VoiceTestPeakLevel { get; set; } = 0f;

    [ObservableProperty]
    public partial string VoiceTestPeakPercentText { get; set; } = "0%";

    [ObservableProperty]
    public partial bool HasVoiceTestCompleted { get; set; } = false;

    [ObservableProperty]
    public partial string VoiceTestSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int VoiceMicVolume { get; set; } = 100;

    [ObservableProperty]
    public partial int VoiceMicGain { get; set; } = 0;

    [ObservableProperty]
    public partial bool EnableVoiceAiNoiseSuppression { get; set; } = true;

    [ObservableProperty]
    public partial string VoiceMicVolumeText { get; set; } = "100%";

    [ObservableProperty]
    public partial string VoiceMicGainText { get; set; } = "Off (+0 dB)";

    private VoiceClientService? _activeVoiceClient;
    private WasapiAudioEngine? _activeWasapiEngine;

    public VoiceClientService? ActiveVoiceClient => _activeVoiceClient;

    public void InitializeVoiceClient(uint localAccountId, System.Net.IPEndPoint? relayEndPoint = null)
    {
        if (_activeVoiceClient != null) return;

        _activeWasapiEngine = new WasapiAudioEngine();
        _activeVoiceClient = new VoiceClientService(localAccountId, relayEndPoint)
        {
            AudioEngine = _activeWasapiEngine,
            InputVolumePercent = (float)VoiceMicVolume,
            GainDb = (float)VoiceMicGain,
            EnableAiNoiseSuppression = EnableVoiceAiNoiseSuppression
        };

        _activeVoiceClient.OnPeerVoiceStateUpdated += (uint accountId, bool isSpeaking, bool isMuted) =>
        {
            if (_activeIpcService != null)
            {
                int allyIndex = (int)(accountId % 5);
                string json = $"{{\"allyIndex\":{allyIndex},\"isSpeaking\":{(isSpeaking ? "true" : "false")},\"isMuted\":{(isMuted ? "true" : "false")}}}";
                global::WILLOWMAKER.Core.Services.IPC.WillowmakerIpcMessage msg =
                    global::WILLOWMAKER.Core.Services.IPC.WillowmakerIpcMessage.CreateString(
                        global::WILLOWMAKER.Core.Services.IPC.IpcOpCode.DispatchVoiceUiTrigger, json);
                Task _ = _activeIpcService.BroadcastAsync(msg);
            }
        };

        _activeVoiceClient.Start();
        Log(LogCategory.General, $"[VOICE] High-Definition 48 kHz Opus Voice Engine initialized for Account {localAccountId}.");
    }

    public void DisposeVoiceClient()
    {
        try
        {
            VoiceClientService? voiceClient = Interlocked.Exchange(ref _activeVoiceClient, null);
            WasapiAudioEngine? wasapi = Interlocked.Exchange(ref _activeWasapiEngine, null);
            voiceClient?.Dispose();
            wasapi?.Dispose();
            Log(LogCategory.General, "[VOICE] High-Definition Opus Voice Engine disposed.");
        }
        catch (Exception ex)
        {
            Log(LogCategory.General, $"[VOICE] Voice engine disposal warning: {ex.Message}");
        }
    }

    public async Task DisposeVoiceClientAsync()
    {
        VoiceClientService? voiceClient = Interlocked.Exchange(ref _activeVoiceClient, null);
        WasapiAudioEngine? wasapi = Interlocked.Exchange(ref _activeWasapiEngine, null);

        if (voiceClient == null && wasapi == null)
        {
            return;
        }

        if (voiceClient != null)
        {
            try
            {
                await Task.Run(async () => await voiceClient.DisposeAsync().ConfigureAwait(false))
                    .WaitAsync(TimeSpan.FromMilliseconds(1500))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(LogCategory.General, $"[VOICE] Async voice client disposal notice: {ex.Message}");
            }
        }

        if (wasapi != null)
        {
            try
            {
                await Task.Run(() => wasapi.Dispose())
                    .WaitAsync(TimeSpan.FromMilliseconds(1500))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(LogCategory.General, $"[VOICE] Async WASAPI engine disposal notice: {ex.Message}");
            }
        }

        Log(LogCategory.General, "[VOICE] High-Definition Opus Voice Engine disposed asynchronously.");
    }

    public void HandlePttFlagsUpdated(ulong pttFlags, uint matchId = 0, int teamIndex = 1, int laneIndex = 0, uint partyId = 0, uint pupilId = 0)
    {
        if (_activeVoiceClient == null) return;

        if ((pttFlags & NEXUS.Contracts.Telemetry.TelemetryRingHeader.VoicePttTeamFlag) != 0)
        {
            uint teamChannelId = VoiceClientService.GetTeamChannelId(matchId, teamIndex);
            _activeVoiceClient.SetPushToTalkState(true, Kongor.Core.Netcode.Voice.VoiceChannelType.Team, teamChannelId);
        }
        else if ((pttFlags & NEXUS.Contracts.Telemetry.TelemetryRingHeader.VoicePttLaneFlag) != 0)
        {
            uint laneChannelId = VoiceClientService.GetLaneChannelId(matchId, teamIndex, laneIndex);
            _activeVoiceClient.SetPushToTalkState(true, Kongor.Core.Netcode.Voice.VoiceChannelType.Lane, laneChannelId);
        }
        else if ((pttFlags & NEXUS.Contracts.Telemetry.TelemetryRingHeader.VoicePttMentorFlag) != 0)
        {
            uint mentorChannelId = VoiceClientService.GetMentorChannelId(pupilId);
            _activeVoiceClient.SetPushToTalkState(true, Kongor.Core.Netcode.Voice.VoiceChannelType.Mentor, mentorChannelId);
        }
        else if ((pttFlags & NEXUS.Contracts.Telemetry.TelemetryRingHeader.VoicePttPartyFlag) != 0)
        {
            _activeVoiceClient.SetPushToTalkState(true, Kongor.Core.Netcode.Voice.VoiceChannelType.Party, partyId);
        }
        else
        {
            _activeVoiceClient.SetPushToTalkState(false, _activeVoiceClient.ActiveChannel, _activeVoiceClient.ActiveChannelId);
        }
    }

    partial void OnVoiceMicVolumeChanged(int value)
    {
        VoiceMicVolumeText = $"{value}%";
        if (_activeVoiceClient != null) _activeVoiceClient.InputVolumePercent = (float)value;
        SaveLauncherSettings();
    }

    partial void OnVoiceMicGainChanged(int value)
    {
        VoiceMicGainText = value == 0 ? "Off (+0 dB)" : $"+{value} dB";
        if (_activeVoiceClient != null) _activeVoiceClient.GainDb = (float)value;
        SaveLauncherSettings();
    }

    partial void OnEnableVoiceAiNoiseSuppressionChanged(bool value)
    {
        if (_activeVoiceClient != null) _activeVoiceClient.EnableAiNoiseSuppression = value;
        SaveLauncherSettings();
    }

    /// <summary>
    ///     Reads the active <c>sound_driver</c> CVar from <c>startup.cfg</c> if present so the audio
    ///     panel accurately displays the unified game and voice output pipeline.
    /// </summary>
    public static string ResolveConfiguredK2SoundDriver(string? clientRoot)
    {
        if (!string.IsNullOrWhiteSpace(clientRoot))
        {
            string[] candidates =
            [
                Path.Combine(clientRoot, "userdata", "startup.cfg"),
                Path.Combine(clientRoot, "KONGOR", "startup.cfg"),
                Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "KONGOR", "startup.cfg"),
                Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "KONGOR", "startup.cfg"),
                Path.Combine(clientRoot, "userdata", "Library", "Application Support", "Heroes of Newerth", "KONGOR", "startup.cfg")
            ];

            foreach (string cfg in candidates)
            {
                if (!File.Exists(cfg))
                {
                    continue;
                }

                try
                {
                    foreach (string line in File.ReadLines(cfg))
                    {
                        if (!line.Contains("sound_driver", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string[] parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4 && parts[1].Equals("sound_driver", StringComparison.OrdinalIgnoreCase))
                        {
                            string raw = parts[3].Trim();
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                return raw;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore transient startup.cfg read errors
                }
            }
        }

        return OperatingSystem.IsWindows() ? "sound_fmod" : "fmod_ex";
    }

    /// <summary>
    ///     Refreshes the default Windows recording and playback device names for UI display.
    /// </summary>
    public void RefreshVoiceDevices()
    {
        try
        {
            string? clientRoot = LocationGuard.ResolveGameClientDirectory();
            string soundDriver = EnableSpatialAudio
                ? "OpenAL Soft HRTF"
                : ResolveConfiguredK2SoundDriver(clientRoot);

            if (OperatingSystem.IsWindows())
            {
                int outputHz = AudioModernizationService.ProbeDefaultOutputEndpointSampleRate();
                string hzLabel = outputHz == 44100 ? "44.1 kHz" : "48 kHz";
                string outDev = OpusMicTestService.GetDefaultOutputDeviceName();
                if (OpusMicTestService.HasMicrophone())
                {
                    string inDev = OpusMicTestService.GetDefaultInputDeviceName();
                    VoiceDetectedMicText = $"Output: {outDev} ({hzLabel}, {soundDriver}) • Input: {inDev}";
                    Log(LogCategory.General, $"[VOICE] Discovered default audio devices - In: '{inDev}', Out: '{outDev}' ({hzLabel}, Driver: '{soundDriver}')");
                }
                else
                {
                    VoiceDetectedMicText = $"Output: {outDev} ({hzLabel}, {soundDriver}) • Input: No input hardware detected";
                    Log(LogCategory.General, $"[VOICE] Discovered default output device - Out: '{outDev}' ({hzLabel}, Driver: '{soundDriver}'), Mic: none");
                }
            }
            else
            {
                VoiceDetectedMicText = $"Output: System Default (48 kHz, {soundDriver}) • Input: Platform Default";
            }
        }
        catch (Exception ex)
        {
            VoiceDetectedMicText = "Microphone: Detection unavailable";
            Log(LogCategory.General, $"[VOICE] Failed querying audio devices: {ex.Message}");
        }
    }

    /// <summary>
    ///     Records 3 seconds of live microphone audio, compresses through Concentus Opus 2.2.2,
    ///     decodes to PCM, normalizes gain, and immediately auditions playback in headphones.
    /// </summary>
    [RelayCommand]
    public async Task TestMicrophoneAsync()
    {
        if (IsVoiceTesting) return;

        IsVoiceTesting = true;
        CanTestVoice = false;
        VoiceTestButtonText = "🔴 Recording (3s)...";
        VoiceTestProgress = 0;
        VoiceTestPeakLevel = 0f;
        VoiceTestPeakPercentText = "0%";
        VoiceTestStatusColor = "#FBBF24"; // Amber
        VoiceTestStatusText = "🎙️ Recording from microphone... Speak now!";
        Log(LogCategory.General, "[VOICE] Starting 3-second Opus loopback test...");

        try
        {
            string scratchDir = Path.Combine(Environment.CurrentDirectory, "scratch");
            string wavPath = Path.Combine(scratchDir, "opus_loopback_test.wav");

            OpusMicTestResult result = await OpusMicTestService.RecordAndAuditionAsync(
                durationSeconds: 3,
                sampleRate: 48000,
                inputVolumePercent: (float)VoiceMicVolume,
                gainDb: (float)VoiceMicGain,
                enableAiNoiseSuppression: EnableVoiceAiNoiseSuppression,
                playBackImmediately: false,
                saveWavPath: wavPath,
                onProgressPercent: (int pct) =>
                {
                    void UpdatePct() { VoiceTestProgress = pct; }
                    if (Dispatcher.UIThread.CheckAccess()) UpdatePct();
                    else Dispatcher.UIThread.Post(UpdatePct);
                },
                onLevelUpdate: (float lvl) =>
                {
                    void UpdateLevel()
                    {
                        VoiceTestPeakLevel = lvl;
                        VoiceTestPeakPercentText = $"{(int)(lvl * 100)}%";
                    }
                    if (Dispatcher.UIThread.CheckAccess()) UpdateLevel();
                    else Dispatcher.UIThread.Post(UpdateLevel);
                });

            if (!result.Success)
            {
                VoiceTestStatusColor = "#EF4444"; // Red
                VoiceTestStatusText = $"❌ Test failed: {result.Message}";
                VoiceTestButtonText = "🎙️ Test Microphone (3s)";
                Log(LogCategory.General, $"[VOICE] Test failed: {result.Message}");
                return;
            }

            Log(LogCategory.General, $"[VOICE] Recording complete. Peak: {result.PeakAmplitude * 100:F1}%. Now auditioning Opus playback through '{result.OutputDeviceName}'...");

            // Audition Playback Phase
            VoiceTestButtonText = "🔊 Auditioning...";
            VoiceTestStatusColor = "#38BDF8"; // Cyan
            VoiceTestStatusText = $"🔊 Auditioning decoded Opus playback through {result.OutputDeviceName}...";
            VoiceTestProgress = 0;

            await OpusMicTestService.PlayWavBytesAsync(
                result.DecodedWavBytes,
                onProgressPercent: (int pct) =>
                {
                    void UpdatePlaybackPct() { VoiceTestProgress = pct; }
                    if (Dispatcher.UIThread.CheckAccess()) UpdatePlaybackPct();
                    else Dispatcher.UIThread.Post(UpdatePlaybackPct);
                });

            string noiseDetail = result.AiNoiseSuppressionActive && result.NoiseReductionDb > 0.1f
                ? $", Noise Suppression DSP: -{result.NoiseReductionDb:F1} dB"
                : string.Empty;

            VoiceTestStatusColor = "#34D399"; // Green
            VoiceTestStatusText = $"✓ Tested! Peak: {result.PeakAmplitude * 100:F1}%, Norm: {result.NormalizedPeak * 100:F0}%, {result.CompressionRatio:F1}:1 Opus{noiseDetail}.";
            VoiceTestSummaryText = $"{result.InputDeviceName} → {result.OutputDeviceName}";
            VoiceTestButtonText = "🎙️ Test Again";
            HasVoiceTestCompleted = true;
            Log(LogCategory.General, $"[VOICE] Loopback audition complete. {result.Message}");
        }
        catch (Exception ex)
        {
            VoiceTestStatusColor = "#EF4444";
            VoiceTestStatusText = $"❌ Error: {ex.Message}";
            VoiceTestButtonText = "🎙️ Test Microphone (3s)";
            Log(LogCategory.General, $"[VOICE ERROR] {ex.Message}");
        }
        finally
        {
            IsVoiceTesting = false;
            CanTestVoice = true;
        }
    }
}
