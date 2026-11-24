// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpenAlNative.cs
// System: NEXUS Spatial Audio Modernization Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Runtime.InteropServices;

namespace NEXUS.SpatialAudio;

/// <summary>
///     Native P/Invoke bindings for OpenAL Soft 1.23+ and ALC_SOFT_HRTF extensions.
/// </summary>
internal static unsafe class OpenAlNative
{
    private const string LibName = "openal32.dll";

    // AL Constants
    internal const int AL_FALSE = 0;
    internal const int AL_TRUE = 1;
    internal const int AL_SOURCE_RELATIVE = 0x0202;
    internal const int AL_CONE_INNER_ANGLE = 0x1001;
    internal const int AL_CONE_OUTER_ANGLE = 0x1002;
    internal const int AL_PITCH = 0x1003;
    internal const int AL_POSITION = 0x1004;
    internal const int AL_DIRECTION = 0x1005;
    internal const int AL_VELOCITY = 0x1006;
    internal const int AL_LOOPING = 0x1007;
    internal const int AL_BUFFER = 0x1009;
    internal const int AL_GAIN = 0x100A;
    internal const int AL_ORIENTATION = 0x100F;
    internal const int AL_SOURCE_STATE = 0x1010;
    internal const int AL_INITIAL = 0x1011;
    internal const int AL_PLAYING = 0x1012;
    internal const int AL_PAUSED = 0x1013;
    internal const int AL_STOPPED = 0x1014;
    internal const int AL_BUFFERS_QUEUED = 0x1015;
    internal const int AL_BUFFERS_PROCESSED = 0x1016;
    internal const int AL_REFERENCE_DISTANCE = 0x1020;
    internal const int AL_ROLLOFF_FACTOR = 0x1021;
    internal const int AL_CONE_OUTER_GAIN = 0x1022;
    internal const int AL_MAX_DISTANCE = 0x1023;
    internal const int AL_AIR_ABSORPTION_FACTOR = 0x20007;

    // AL Formats
    internal const int AL_FORMAT_MONO8 = 0x1100;
    internal const int AL_FORMAT_MONO16 = 0x1101;
    internal const int AL_FORMAT_STEREO8 = 0x1102;
    internal const int AL_FORMAT_STEREO16 = 0x1103;

    // AL Distance Models
    internal const int AL_INVERSE_DISTANCE_CLAMPED = 0xD002;

    // ALC Constants
    internal const int ALC_FREQUENCY = 0x1007;
    internal const int ALC_REFRESH = 0x1008;
    internal const int ALC_SYNC = 0x1009;
    internal const int ALC_MONO_SOURCES = 0x1010;
    internal const int ALC_STEREO_SOURCES = 0x1011;

    // ALC_SOFT_HRTF Extension Tokens
    internal const int ALC_HRTF_SOFT = 0x1992;
    internal const int ALC_DONT_CARE_SOFT = 0x0002;
    internal const int ALC_HRTF_STATUS_SOFT = 0x1993;
    internal const int ALC_NUM_HRTF_SPECIFIERS_SOFT = 0x1994;
    internal const int ALC_HRTF_SPECIFIER_SOFT = 0x1995;
    internal const int ALC_HRTF_ID_SOFT = 0x1996;
    internal const int ALC_HRTF_DISABLED_SOFT = 0x0000;
    internal const int ALC_HRTF_ENABLED_SOFT = 0x0001;
    internal const int ALC_HRTF_DENIED_SOFT = 0x0002;
    internal const int ALC_HRTF_REQUIRED_SOFT = 0x0003;
    internal const int ALC_HRTF_HEADPHONES_DETECTED_SOFT = 0x0004;
    internal const int ALC_HRTF_UNSUPPORTED_FORMAT_SOFT = 0x0005;

    // ALC Device & Context Management
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern nint alcOpenDevice([MarshalAs(UnmanagedType.LPStr)] string? deviceName);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern bool alcCloseDevice(nint device);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint alcCreateContext(nint device, int* attrList);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern bool alcMakeContextCurrent(nint context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alcDestroyContext(nint context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint alcGetCurrentContext();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint alcGetContextsDevice(nint context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int alcGetError(nint device);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern bool alcIsExtensionPresent(nint device, [MarshalAs(UnmanagedType.LPStr)] string extName);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alcGetIntegerv(nint device, int param, int size, int* values);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern sbyte* alcGetStringiSOFT(nint device, int paramName, int index);

    // AL Core Functions
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alGenSources(int n, uint* sources);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alDeleteSources(int n, uint* sources);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alSourcef(uint source, int param, float value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alSource3f(uint source, int param, float v1, float v2, float v3);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alSourcei(uint source, int param, int value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alSourcePlay(uint source);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alSourceStop(uint source);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alSourcePause(uint source);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alListenerf(int param, float value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alListener3f(int param, float v1, float v2, float v3);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alListenerfv(int param, float* values);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void alDistanceModel(int distanceModel);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int alGetError();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern sbyte* alGetString(int param);
}
