// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Audio classification for K2 engine sound design and spatialization routing.
/// </summary>
public enum AudioType
{
    Voice,
    Sfx,
    Music,
    Announcer,
    Ambient
}

/// <summary>
/// Normalization recipe specifying FFmpeg broadcast audio loudness parameters.
/// </summary>
public sealed class AudioNormalizationRecipe
{
    public required string InputFile { get; init; }
    public required AudioType AudioType { get; init; }
    public double IntegratedLoudnessLufs { get; init; }
    public double TruePeakCeilingDb { get; init; }
    public double LoudnessRangeLra { get; init; }
    public int SampleRateHz { get; init; }
    public int Channels { get; init; }
    public required string OutputFormat { get; init; }
    public required string FfmpegArguments { get; init; }
    public required string RecommendedFfmpegCommand { get; init; }
}

/// <summary>
/// Recipe for synthesizing a multi-sample K2 .sound definition XML container.
/// </summary>
public sealed class SoundXmlRecipe
{
    public required string SoundName { get; init; }
    public required AudioType AudioType { get; init; }
    public required string VfsDestinationPath { get; init; }
    public required string XmlContent { get; init; }
    public required List<string> SampleFiles { get; init; }
    public double Volume { get; init; }
    public int Falloff { get; init; }
    public int MinRange { get; init; }
    public int MaxRange { get; init; }
}

/// <summary>
/// Service orchestrating broadcast audio loudness normalization (EBU R128 -16 LUFS),
/// channel layout routing (mono WAV vs stereo OGG), and multi-sample K2 .sound XML generation.
/// </summary>
public sealed class AudioStudioService
{
    private readonly string _workspaceRoot;

    public AudioStudioService(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Formulates FFmpeg normalization command matching HoN broadcast standards.
    /// </summary>
    public AudioNormalizationRecipe FormulateNormalization(
        string inputFile,
        AudioType audioType = AudioType.Voice,
        double lufs = -16.0,
        double truePeak = -1.5,
        double lra = 11.0,
        string? outputFile = null)
    {
        bool isVoiceOrSfx = audioType is AudioType.Voice or AudioType.Sfx;
        int channels = isVoiceOrSfx ? 1 : 2; // Mono for point-source 3D spatialization, Stereo for music/announcer
        int sampleRate = 44100;
        string outExt = isVoiceOrSfx ? ".wav" : ".ogg";

        string lufsStr = lufs.ToString("0.0", CultureInfo.InvariantCulture);
        string tpStr = truePeak.ToString("0.0", CultureInfo.InvariantCulture);
        string lraStr = lra.ToString("0.0", CultureInfo.InvariantCulture);

        string filter = $"-af loudnorm=I={lufsStr}:TP={tpStr}:LRA={lraStr}";
        string codecFlags = isVoiceOrSfx
            ? $"-ar {sampleRate} -ac {channels}"
            : $"-ar {sampleRate} -ac {channels} -c:a libvorbis -q:a 5";

        string resolvedOut = outputFile ?? Path.ChangeExtension(inputFile, outExt);
        string fullArgs = $"{filter} {codecFlags}";
        string cmd = $"ffmpeg -i \"{inputFile}\" {fullArgs} \"{resolvedOut}\"";

        return new AudioNormalizationRecipe
        {
            InputFile = inputFile,
            AudioType = audioType,
            IntegratedLoudnessLufs = lufs,
            TruePeakCeilingDb = truePeak,
            LoudnessRangeLra = lra,
            SampleRateHz = sampleRate,
            Channels = channels,
            OutputFormat = outExt.TrimStart('.'),
            FfmpegArguments = fullArgs,
            RecommendedFfmpegCommand = cmd
        };
    }

    /// <summary>
    /// Generates multi-sample randomized K2 .sound XML definition.
    /// </summary>
    public SoundXmlRecipe GenerateSoundXml(
        string soundName,
        IEnumerable<string> sampleFiles,
        AudioType audioType = AudioType.Voice,
        double volume = 1.0,
        int falloff = 1200,
        int minRange = 200,
        int maxRange = 1500,
        string? vfsDestinationPath = null)
    {
        List<string> samples = sampleFiles.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (samples.Count == 0)
        {
            samples.Add($"/sounds/heroes/{soundName}.wav");
        }

        string volStr = volume.ToString("0.0", CultureInfo.InvariantCulture);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<sound name=\"{soundName}\">");

        foreach (string sample in samples)
        {
            string samplePath = sample.StartsWith('/') ? sample : $"/{sample}";
            if (audioType is AudioType.Music or AudioType.Announcer)
            {
                // Non-spatial 2D UI / background music
                sb.AppendLine($"\t<sample file=\"{samplePath}\" volume=\"{volStr}\" />");
            }
            else
            {
                // 3D Point-source spatial sound effect / hero voice
                sb.AppendLine($"\t<sample file=\"{samplePath}\" volume=\"{volStr}\" falloff=\"{falloff}\" minrange=\"{minRange}\" maxrange=\"{maxRange}\" />");
            }
        }

        sb.AppendLine("</sound>");

        string vfsPath = vfsDestinationPath ?? $"/heroes/{soundName}/sounds/{soundName}.sound";

        return new SoundXmlRecipe
        {
            SoundName = soundName,
            AudioType = audioType,
            VfsDestinationPath = vfsPath,
            XmlContent = sb.ToString(),
            SampleFiles = samples,
            Volume = volume,
            Falloff = falloff,
            MinRange = minRange,
            MaxRange = maxRange
        };
    }

    /// <summary>
    /// Parses string audio category into AudioType enum.
    /// </summary>
    public static AudioType ParseAudioType(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return AudioType.Voice;

        return category.Trim().ToLowerInvariant() switch
        {
            "voice" or "dialogue" or "hero_voice" => AudioType.Voice,
            "sfx" or "spell" or "ability" => AudioType.Sfx,
            "music" or "bgm" => AudioType.Music,
            "announcer" or "megakill" => AudioType.Announcer,
            "ambient" or "ambience" => AudioType.Ambient,
            _ => AudioType.Voice
        };
    }

    // =========================================================================
    // #221 (Gem 3J): WASAPI Process-Isolated Application Audio Loopback Capture
    // (`obs-studio/plugins/win-wasapi/win-wasapi.cpp` -> VAD\Process_Loopback)
    // =========================================================================

    public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    public const uint AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1U;
    public const uint PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0U;
    public const uint PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1U;
    public const ushort VT_BLOB = 65;

    /// <summary>
    /// #221 (Gem 3J): Windows 10 2004+ <c>PROCESS_LOOPBACK_MODE</c> values for <c>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS</c>.
    /// </summary>
    public enum ProcessLoopbackMode : uint
    {
        IncludeTargetProcessTree = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        ExcludeTargetProcessTree = PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
    }

    /// <summary>
    /// #221 (Gem 3J): Native <c>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS</c> layout (8 bytes).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    /// <summary>
    /// #221 (Gem 3J): Native <c>AUDIOCLIENT_ACTIVATION_PARAMS</c> layout (12 bytes) passed inside a <c>VT_BLOB</c>
    /// <c>PROPVARIANT</c> to <c>ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ...)</c>.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct AudioClientActivationParamsBlob
    {
        public uint ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    /// <summary>
    /// #221 (Gem 3J): Builds the 12-byte <c>AUDIOCLIENT_ACTIVATION_PARAMS</c> binary activation blob for
    /// <c>ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ...)</c> so audio capture can isolate
    /// a single <c>hon_x64.exe</c> PID without bleeding desktop audio or other client slots.
    /// </summary>
    public static bool BuildProcessIsolatedLoopbackParams(
        uint targetProcessId,
        bool includeTargetProcessTree,
        out byte[] activationBlob,
        out AudioClientActivationParamsBlob structuredParams)
    {
        ProcessLoopbackMode mode = includeTargetProcessTree
            ? ProcessLoopbackMode.IncludeTargetProcessTree
            : ProcessLoopbackMode.ExcludeTargetProcessTree;
        return BuildProcessIsolatedLoopbackActivationBlob(targetProcessId, mode, out activationBlob, out structuredParams);
    }

    /// <summary>
    /// #221 (Gem 3J): Builds the 12-byte <c>AUDIOCLIENT_ACTIVATION_PARAMS</c> binary activation blob for
    /// <c>ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ...)</c> so audio capture can isolate
    /// a single <c>hon_x64.exe</c> PID without bleeding desktop audio or other client slots.
    /// </summary>
    public static bool BuildProcessIsolatedLoopbackActivationBlob(
        uint targetProcessId,
        ProcessLoopbackMode mode,
        out byte[] activationBlob,
        out AudioClientActivationParamsBlob structuredParams)
    {
        if (targetProcessId == 0U ||
            (mode != ProcessLoopbackMode.IncludeTargetProcessTree &&
             mode != ProcessLoopbackMode.ExcludeTargetProcessTree))
        {
            activationBlob = [];
            structuredParams = default;
            return false;
        }

        structuredParams = new AudioClientActivationParamsBlob
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = targetProcessId,
                ProcessLoopbackMode = mode
            }
        };

        activationBlob = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(activationBlob.AsSpan(0, 4), structuredParams.ActivationType);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(activationBlob.AsSpan(4, 4), structuredParams.ProcessLoopbackParams.TargetProcessId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(activationBlob.AsSpan(8, 4), (uint)structuredParams.ProcessLoopbackParams.ProcessLoopbackMode);
        return true;
    }

    // =========================================================================
    // CoreAudio Per-PID Peak Meter Oracle
    // =========================================================================

    public enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    public enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [System.Runtime.InteropServices.Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [System.Runtime.InteropServices.Guid("D666063F-1587-4E43-81F1-B948E807363F"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.IUnknown)] out object ppInterface);
    }

    [System.Runtime.InteropServices.Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
        int GetSimpleAudioVolume(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr AudioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [System.Runtime.InteropServices.Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int SessionCount);
        int GetSession(int SessionCount, out IAudioSessionControl Session);
    }

    [System.Runtime.InteropServices.Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out AudioSessionState pRetVal);
    }

    [System.Runtime.InteropServices.Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out AudioSessionState pRetVal);
        int GetDisplayName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string pRetVal);
        int SetDisplayName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string Value, Guid EventContext);
        int GetIconPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string pRetVal);
        int SetIconPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string Value, Guid EventContext);
        int GetGroupingParam(out Guid pRetVal);
        int SetGroupingParam(Guid Override, Guid EventContext);
        int RegisterAudioSessionNotification(IntPtr NewNotifications);
        int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        int GetSessionIdentifier([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string pRetVal);
        int GetSessionInstanceIdentifier([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string pRetVal);
        int GetProcessId(out uint pRetVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [System.Runtime.InteropServices.Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }

    public sealed record PidPeakSampleResult
    {
        public bool Success { get; init; }
        public float PidMaxPeak { get; init; }
        public float MasterEndpointPeak { get; init; }
        public int MatchingActiveSessions { get; init; }
        public int ExpiredSessionsSkipped { get; init; }
        public string StatusReason { get; init; } = "OK";
    }

    public const int AUDCLNT_S_NO_SINGLE_PROCESS = 0x0889000D;

    public static PidPeakSampleResult SamplePidPeak(uint targetPid)
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return new PidPeakSampleResult
            {
                Success = false,
                StatusReason = "unsupported_platform"
            };
        }

        try
        {
            var enumObj = new MMDeviceEnumeratorComObject() as IMMDeviceEnumerator;
            if (enumObj == null)
            {
                return new PidPeakSampleResult { Success = false, StatusReason = "no_enumerator" };
            }

            int hr = enumObj.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device);
            if (hr != 0 || device == null)
            {
                return new PidPeakSampleResult { Success = false, StatusReason = "no_endpoint" };
            }

            float masterEndpointPeak = 0.0f;
            Guid iidMeter = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iidMeter, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object epMeterObj) == 0 &&
                epMeterObj is IAudioMeterInformation epMeter)
            {
                epMeter.GetPeakValue(out masterEndpointPeak);
            }

            Guid iidMgr2 = typeof(IAudioSessionManager2).GUID;
            hr = device.Activate(ref iidMgr2, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object mgrObj);
            if (hr != 0 || mgrObj is not IAudioSessionManager2 mgr2)
            {
                return new PidPeakSampleResult { Success = false, StatusReason = "no_session_manager" };
            }

            if (mgr2.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum) != 0 || sessionEnum == null)
            {
                return new PidPeakSampleResult { Success = false, StatusReason = "no_session_enum" };
            }

            sessionEnum.GetCount(out int count);
            float pidMaxPeak = 0.0f;
            int matchingActiveSessions = 0;
            int expiredSessionsSkipped = 0;

            for (int i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out IAudioSessionControl ctrl) != 0 || ctrl == null) continue;
                if (ctrl is not IAudioSessionControl2 ctrl2) continue;

                if (ctrl2.GetState(out AudioSessionState state) == 0 && state == AudioSessionState.Expired)
                {
                    expiredSessionsSkipped++;
                    continue;
                }

                int pidHr = ctrl2.GetProcessId(out uint sesPid);
                if (pidHr == AUDCLNT_S_NO_SINGLE_PROCESS)
                {
                    continue;
                }

                if (targetPid == 0 || sesPid == targetPid)
                {
                    matchingActiveSessions++;
                    if (ctrl is IAudioMeterInformation sesMeter)
                    {
                        if (sesMeter.GetPeakValue(out float pk) == 0 && pk > pidMaxPeak)
                        {
                            pidMaxPeak = pk;
                        }
                    }
                }
            }

            return new PidPeakSampleResult
            {
                Success = true,
                PidMaxPeak = pidMaxPeak,
                MasterEndpointPeak = masterEndpointPeak,
                MatchingActiveSessions = matchingActiveSessions,
                ExpiredSessionsSkipped = expiredSessionsSkipped,
                StatusReason = "OK"
            };
        }
        catch (Exception ex)
        {
            return new PidPeakSampleResult
            {
                Success = false,
                StatusReason = $"com_error: {ex.Message}"
            };
        }
    }
}

