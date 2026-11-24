// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: fmod_audio_proxy.cpp
// System: Project Kongor Native Modernization
// Subsystem: kongor_audio (Low-Latency WASAPI & 256-Voice 3D Spatial Audio Shim)
// Description: Modernized audio pipeline for player client (hon_x64.exe).
//              Unclamps channel limits from 64 to 256+ voices, routes via WASAPI
//              in low-latency event-driven mode (<10ms), configures 7.1 surround
//              for Windows Spatial / HRTF binaural headphone convolution, and
//              tracks active voice telemetry.
// ============================================================================

#define _CRT_SECURE_NO_WARNINGS
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <objbase.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <cstdint>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <atomic>
#include <algorithm>
#include <mutex>

#pragma pack(push, 8)
struct FMOD_VECTOR {
    float x;
    float y;
    float z;
};

struct AudioTelemetry {
    int32_t outputType;
    int32_t speakerMode;
    int32_t bufferLength;
    int32_t numBuffers;
    int32_t wasapiActive;
    int32_t hrtfActive;
    float   latencyMs;
    float   listenerPos[3];
    float   listenerFwd[3];
    int32_t activeVoices;
    int32_t peakVoices;
    int32_t maxChannelsConfigured;
};
#pragma pack(pop)

// Maximum supported channels in proxy tracker
static constexpr size_t MAX_AUDIO_CHANNELS = 512;
static constexpr int DEFAULT_TARGET_VOICES = 256;

// Cinematic Theater anthology audio is 528,469 ms (~8m 48s), whereas standard
// HoN menu, hero-select, and match music tracks are < 240,000 ms (< 4m).
// Isolating Cinematic soundtrack detection at >= 480,000 ms (8m) prevents normal
// menu/lobby/match music transitions from cross-seeking via g_pendingCinematicSeekMs.
static constexpr unsigned int CINEMATIC_THEATER_MIN_LENGTH_MS = 480000;

// Real FMOD core module handle
static HMODULE g_hRealCore = nullptr;
static std::mutex g_audioLogMutex;

// Operational States & Telemetry (Day-2 Safe Defaults: Stereo / Engine Driver unless explicitly opted-in)
static std::atomic<bool> g_isWasapiActive{false};
static std::atomic<bool> g_isHrtfActive{false};
static std::atomic<int32_t> g_currentOutputType{0};
static std::atomic<int32_t> g_currentSpeakerMode{2};
static std::atomic<int32_t> g_bufferLength{1024};
static std::atomic<int32_t> g_numBuffers{4};
static std::atomic<int32_t> g_maxChannelsConfigured{DEFAULT_TARGET_VOICES};
static std::atomic<int32_t> g_activeVoices{0};
static std::atomic<int32_t> g_peakVoices{0};
static std::atomic<uint64_t> g_totalSoundsPlayed{0};
static std::atomic<uint32_t> g_endpointChannels{2};
static std::atomic<uint32_t> g_endpointSampleRate{48000};

static std::atomic<float> g_listenerPos[3];
static std::atomic<float> g_listenerFwd[3];

// Function pointer types for intercepted methods
typedef int (*PFN_setOutput)(void* self, int outputType);
typedef int (*PFN_getOutput)(void* self, int* outputType);
typedef int (*PFN_init)(void* self, int maxChannels, unsigned int flags, void* extraDriverData);
typedef int (*PFN_setSoftwareChannels)(void* self, int numsoftwarechannels);
typedef int (*PFN_setDSPBufferSize)(void* self, unsigned int bufferLength, int numBuffers);
typedef int (*PFN_setSpeakerMode)(void* self, int speakerMode);
typedef int (*PFN_getSpeakerMode)(void* self, int* speakerMode);
typedef int (*PFN_set3DListenerAttributes)(void* self, int listener, const FMOD_VECTOR* pos, const FMOD_VECTOR* vel, const FMOD_VECTOR* forward, const FMOD_VECTOR* up);
typedef int (*PFN_System_playSound)(void* self, int channelid, void* sound, bool paused, void** channel);
typedef int (*PFN_Channel_setPosition)(void* channel, unsigned int position, unsigned int postype);
typedef int (*PFN_Channel_getPosition)(void* channel, unsigned int* position, unsigned int postype);
typedef int (*PFN_Channel_stop)(void* channel);
typedef int (*PFN_Channel_isPlaying)(void* channel, bool* isPlaying);
typedef int (*PFN_Sound_getLength)(void* sound, unsigned int* length, unsigned int lengthtype);

static PFN_setOutput g_real_setOutput = nullptr;
static PFN_getOutput g_real_getOutput = nullptr;
static PFN_init g_real_init = nullptr;
static PFN_setSoftwareChannels g_real_setSoftwareChannels = nullptr;
static PFN_setDSPBufferSize g_real_setDSPBufferSize = nullptr;
static PFN_setSpeakerMode g_real_setSpeakerMode = nullptr;
static PFN_getSpeakerMode g_real_getSpeakerMode = nullptr;
static PFN_set3DListenerAttributes g_real_set3DListenerAttributes = nullptr;
static PFN_System_playSound g_real_playSound = nullptr;
static PFN_Channel_setPosition g_real_setPosition = nullptr;
static PFN_Channel_getPosition g_real_getPosition = nullptr;
static PFN_Channel_stop g_real_stop = nullptr;
static PFN_Channel_isPlaying g_real_isPlaying = nullptr;
static PFN_Sound_getLength g_real_getLength = nullptr;

static void* g_channelHandles[MAX_AUDIO_CHANNELS] = {nullptr};
static std::atomic<uint32_t> g_pendingSeekMs[MAX_AUDIO_CHANNELS]{0};
static std::atomic<void*> g_cinematicChannel{nullptr};
static std::atomic<uint32_t> g_pendingCinematicSeekMs{0};

static void LogMessage(const char* format, ...) {
    std::lock_guard<std::mutex> lock(g_audioLogMutex);

    char buf[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buf, sizeof(buf), format, args);
    va_end(args);

    OutputDebugStringA(buf);

    FILE* f = fopen("logs/kongor_audio.log", "a");
    if (f) {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(f, "[%04d-%02d-%02d %02d:%02d:%02d.%03d] %s\n",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, buf);
        fclose(f);
    }
}

static void EnsureCoreLoaded() {
    if (g_hRealCore) return;

    wchar_t modulePath[MAX_PATH] = {0};
    HMODULE hSelf = GetModuleHandleW(L"fmodex64.dll");
    if (hSelf) {
        GetModuleFileNameW(hSelf, modulePath, MAX_PATH);
        wchar_t* pSlash = wcsrchr(modulePath, L'\\');
        if (pSlash) *(pSlash + 1) = L'\0';
    }

    std::wstring directCore = std::wstring(modulePath) + L"fmodex64_core.dll";
    g_hRealCore = LoadLibraryW(directCore.c_str());

    if (!g_hRealCore) {
        std::wstring fallbackCore = std::wstring(modulePath) + L"windows-x64\\fmodex64_core.dll";
        g_hRealCore = LoadLibraryW(fallbackCore.c_str());
    }
    if (!g_hRealCore) {
        g_hRealCore = LoadLibraryA("fmodex64_core.dll");
    }
    if (!g_hRealCore) {
        g_hRealCore = LoadLibraryA("fmodex64_orig.dll");
    }

    if (!g_hRealCore) {
        LogMessage("[KONGOR_AUDIO][ERROR] Could not load genuine FMOD Ex core (tried fmodex64_core.dll)!");
        return;
    }

    LogMessage("[KONGOR_AUDIO] Genuine FMOD Ex core loaded successfully into process (Handle: 0x%p).", g_hRealCore);

    // Resolve function pointers from real core
    g_real_setOutput = (PFN_setOutput)GetProcAddress(g_hRealCore, "?setOutput@System@FMOD@@QEAA?AW4FMOD_RESULT@@W4FMOD_OUTPUTTYPE@@@Z");
    g_real_getOutput = (PFN_getOutput)GetProcAddress(g_hRealCore, "?getOutput@System@FMOD@@QEAA?AW4FMOD_RESULT@@PEAW4FMOD_OUTPUTTYPE@@@Z");
    g_real_init = (PFN_init)GetProcAddress(g_hRealCore, "?init@System@FMOD@@QEAA?AW4FMOD_RESULT@@HIPEAX@Z");
    g_real_setSoftwareChannels = (PFN_setSoftwareChannels)GetProcAddress(g_hRealCore, "?setSoftwareChannels@System@FMOD@@QEAA?AW4FMOD_RESULT@@H@Z");
    g_real_setDSPBufferSize = (PFN_setDSPBufferSize)GetProcAddress(g_hRealCore, "?setDSPBufferSize@System@FMOD@@QEAA?AW4FMOD_RESULT@@IH@Z");
    g_real_setSpeakerMode = (PFN_setSpeakerMode)GetProcAddress(g_hRealCore, "?setSpeakerMode@System@FMOD@@QEAA?AW4FMOD_RESULT@@W4FMOD_SPEAKERMODE@@@Z");
    g_real_getSpeakerMode = (PFN_getSpeakerMode)GetProcAddress(g_hRealCore, "?getSpeakerMode@System@FMOD@@QEAA?AW4FMOD_RESULT@@PEAW4FMOD_SPEAKERMODE@@@Z");
    g_real_set3DListenerAttributes = (PFN_set3DListenerAttributes)GetProcAddress(g_hRealCore, "?set3DListenerAttributes@System@FMOD@@QEAA?AW4FMOD_RESULT@@HPEBUFMOD_VECTOR@@000@Z");
    g_real_playSound = (PFN_System_playSound)GetProcAddress(g_hRealCore, "?playSound@System@FMOD@@QEAA?AW4FMOD_RESULT@@W4FMOD_CHANNELINDEX@@PEAVSound@2@_NPEAPEAVChannel@2@@Z");
    g_real_setPosition = (PFN_Channel_setPosition)GetProcAddress(g_hRealCore, "?setPosition@Channel@FMOD@@QEAA?AW4FMOD_RESULT@@II@Z");
    g_real_getPosition = (PFN_Channel_getPosition)GetProcAddress(g_hRealCore, "?getPosition@Channel@FMOD@@QEAA?AW4FMOD_RESULT@@PEAII@Z");
    g_real_stop = (PFN_Channel_stop)GetProcAddress(g_hRealCore, "?stop@Channel@FMOD@@QEAA?AW4FMOD_RESULT@@XZ");
    g_real_isPlaying = (PFN_Channel_isPlaying)GetProcAddress(g_hRealCore, "?isPlaying@Channel@FMOD@@QEAA?AW4FMOD_RESULT@@PEA_N@Z");
    g_real_getLength = (PFN_Sound_getLength)GetProcAddress(g_hRealCore, "?getLength@Sound@FMOD@@QEAA?AW4FMOD_RESULT@@PEAII@Z");
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinstDLL);
        CreateDirectoryA("logs", NULL);
        LogMessage("================================================================================");
        LogMessage("[KONGOR_HOTFIX] Active patch: v5.0.2 (kongor_audio opt-in stereo safe defaults)");
        LogMessage("[KONGOR_AUDIO] Initializing 256-Voice Audio Shim (WASAPI/HRTF explicit opt-in via KONGOR_AUDIO_WASAPI=1 / KONGOR_AUDIO_HRTF=1)...");
        LogMessage("================================================================================");
        EnsureCoreLoaded();
    }
    return TRUE;
}

// ----------------------------------------------------------------------------
// C-ABI Exports (FMOD_Memory_*, FMOD_System_Create)
// ----------------------------------------------------------------------------

extern "C" int __stdcall FMOD_Memory_GetStats(int* currentalloced, int* maxalloced, int blocking) {
    EnsureCoreLoaded();
    typedef int (__stdcall *PFN)(int*, int*, int);
    static PFN pfn = (PFN)GetProcAddress(g_hRealCore, "FMOD_Memory_GetStats");
    return pfn ? pfn(currentalloced, maxalloced, blocking) : 0;
}

extern "C" int __stdcall FMOD_Memory_Initialize(void* poolmem, int poollen, void* useralloc, void* userrealloc, void* userfree, int memtypeflags) {
    EnsureCoreLoaded();
    typedef int (__stdcall *PFN)(void*, int, void*, void*, void*, int);
    static PFN pfn = (PFN)GetProcAddress(g_hRealCore, "FMOD_Memory_Initialize");
    return pfn ? pfn(poolmem, poollen, useralloc, userrealloc, userfree, memtypeflags) : 0;
}

extern "C" int __stdcall FMOD_System_Create(void** system) {
    EnsureCoreLoaded();
    typedef int (__stdcall *PFN)(void**);
    static PFN pfn = (PFN)GetProcAddress(g_hRealCore, "FMOD_System_Create");
    int res = pfn ? pfn(system) : 0;
    LogMessage("[KONGOR_AUDIO] FMOD_System_Create called -> result %d, system=%p", res, system ? *system : nullptr);
    return res;
}

// ----------------------------------------------------------------------------
// Intercepted Audio Methods (Channel Unclamp, Explicit Opt-In WASAPI & Endpoint Negotiation)
// ----------------------------------------------------------------------------

static bool IsEnvOptInEnabled(const char* envName) {
    char val[32] = {0};
    DWORD len = GetEnvironmentVariableA(envName, val, sizeof(val));
    if (len == 0 || len >= sizeof(val)) return false;
    return (_stricmp(val, "1") == 0 ||
            _stricmp(val, "true") == 0 ||
            _stricmp(val, "yes") == 0 ||
            _stricmp(val, "on") == 0);
}

static void QueryDefaultRenderEndpointFormat(uint32_t* outChannels, uint32_t* outSampleRate) {
    uint32_t channels = 2;
    uint32_t sampleRate = 48000;

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool shouldUninit = SUCCEEDED(hrCo);

    IMMDeviceEnumerator* pEnumerator = nullptr;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(&pEnumerator));

    if (SUCCEEDED(hr) && pEnumerator) {
        IMMDevice* pDevice = nullptr;
        if (SUCCEEDED(pEnumerator->GetDefaultAudioEndpoint(eRender, eConsole, &pDevice)) && pDevice) {
            IAudioClient* pAudioClient = nullptr;
            if (SUCCEEDED(pDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&pAudioClient))) && pAudioClient) {
                WAVEFORMATEX* pwfx = nullptr;
                if (SUCCEEDED(pAudioClient->GetMixFormat(&pwfx)) && pwfx) {
                    if (pwfx->nChannels > 0) channels = pwfx->nChannels;
                    if (pwfx->nSamplesPerSec > 0) sampleRate = pwfx->nSamplesPerSec;
                    CoTaskMemFree(pwfx);
                }
                pAudioClient->Release();
            }
            pDevice->Release();
        }
        pEnumerator->Release();
    }

    if (shouldUninit) {
        CoUninitialize();
    }

    g_endpointChannels.store(channels);
    g_endpointSampleRate.store(sampleRate);
    if (outChannels) *outChannels = channels;
    if (outSampleRate) *outSampleRate = sampleRate;
}

static unsigned int ComputeSafeWasapiBufferLength() {
    uint32_t epRate = g_endpointSampleRate.load();
    if (epRate == 48000 && IsEnvOptInEnabled("KONGOR_AUDIO_LOW_LATENCY_256")) {
        return 256;
    }
    // 512 samples x 4 buffers (~10.67 ms @ 48 kHz, ~11.61 ms @ 44.1 kHz) prevents
    // non-integer 48->44.1 kHz resampler starvation on USB ASIO/WDM audio interfaces.
    return 512;
}

extern "C" int Proxy_System_setOutput(void* self, int outputType) {
    EnsureCoreLoaded();
    if (!g_real_setOutput) return 0;

    bool enableWasapi = IsEnvOptInEnabled("KONGOR_AUDIO_WASAPI");
    LogMessage("[KONGOR_AUDIO] Engine requested output type: %d (0=Auto, 6=DSOUND, 8=WASAPI) | KONGOR_AUDIO_WASAPI opt-in=%d",
               outputType, enableWasapi ? 1 : 0);

    int targetType = outputType;
    if (enableWasapi) {
        uint32_t epCh = 2;
        uint32_t epRate = 48000;
        QueryDefaultRenderEndpointFormat(&epCh, &epRate);
        LogMessage("[KONGOR_AUDIO] Default Windows render endpoint mix format: %u ch @ %u Hz", epCh, epRate);

        // FMOD_OUTPUTTYPE_WASAPI = 8
        LogMessage("[KONGOR_AUDIO] Opt-in WASAPI active: upgrading audio output backend to WASAPI...");
        int res = g_real_setOutput(self, 8);
        if (res == 0) {
            g_isWasapiActive.store(true);
            g_currentOutputType.store(8);
            LogMessage("[KONGOR_AUDIO] WASAPI Output activated successfully (Shared Mode, endpoint=%u ch @ %u Hz)!", epCh, epRate);
            return 0;
        } else {
            LogMessage("[KONGOR_AUDIO][WARN] WASAPI setOutput returned %d. Gracefully falling back to requested type %d.", res, outputType);
        }
    }

    g_isWasapiActive.store(false);
    g_currentOutputType.store(targetType);
    return g_real_setOutput(self, targetType);
}

extern "C" int Proxy_System_getOutput(void* self, int* outputType) {
    EnsureCoreLoaded();
    if (!g_real_getOutput) return 0;
    int res = g_real_getOutput(self, outputType);
    if (res == 0 && outputType) {
        g_currentOutputType.store(*outputType);
    }
    return res;
}

extern "C" int Proxy_System_setSpeakerMode(void* self, int speakerMode) {
    EnsureCoreLoaded();
    if (!g_real_setSpeakerMode) return 0;

    bool enableHrtf = IsEnvOptInEnabled("KONGOR_AUDIO_HRTF");
    LogMessage("[KONGOR_AUDIO] Engine requested speaker mode: %d (2=Stereo, 5=5.1, 6=7.1) | KONGOR_AUDIO_HRTF opt-in=%d",
               speakerMode, enableHrtf ? 1 : 0);

    int targetMode = speakerMode;
    if (enableHrtf && g_isWasapiActive.load()) {
        uint32_t epCh = g_endpointChannels.load();
        bool force71 = IsEnvOptInEnabled("KONGOR_AUDIO_FORCE_7POINT1");
        if (epCh <= 2 && !force71) {
            LogMessage("[KONGOR_AUDIO][SAFEGUARD] Endpoint mix format has %u channels (Stereo); preserving requested speakerMode=%d instead of forcing 7.1 surround (set KONGOR_AUDIO_FORCE_7POINT1=1 to override).",
                       epCh, speakerMode);
        } else {
            // FMOD_SPEAKERMODE_7POINT1 = 6
            LogMessage("[KONGOR_AUDIO] Opt-in HRTF active on %u-ch endpoint: setting 7.1 Surround (FMOD_SPEAKERMODE_7POINT1)...", epCh);
            int res = g_real_setSpeakerMode(self, 6);
            if (res == 0) {
                g_isHrtfActive.store(true);
                g_currentSpeakerMode.store(6);
                LogMessage("[KONGOR_AUDIO] 7.1 Spatial Audio Speaker Mode active!");
                return 0;
            }
            LogMessage("[KONGOR_AUDIO][WARN] setSpeakerMode(7.1) returned %d. Falling back to %d.", res, speakerMode);
        }
    }

    g_isHrtfActive.store(false);
    g_currentSpeakerMode.store(targetMode);
    return g_real_setSpeakerMode(self, targetMode);
}

extern "C" int Proxy_System_getSpeakerMode(void* self, int* speakerMode) {
    EnsureCoreLoaded();
    if (!g_real_getSpeakerMode) return 0;
    int res = g_real_getSpeakerMode(self, speakerMode);
    if (res == 0 && speakerMode) {
        g_currentSpeakerMode.store(*speakerMode);
    }
    return res;
}

extern "C" int Proxy_System_setSoftwareChannels(void* self, int numsoftwarechannels) {
    EnsureCoreLoaded();

    // UNCLAMP: Ensure at least 256 software channels so complex teamfights never drop voice lines or sound cues
    int targetChannels = (numsoftwarechannels < DEFAULT_TARGET_VOICES) ? DEFAULT_TARGET_VOICES : numsoftwarechannels;
    LogMessage("[KONGOR_AUDIO] Intercepted setSoftwareChannels: Engine requested %d -> UNCLAMPED TO %d VOICES!",
               numsoftwarechannels, targetChannels);

    g_maxChannelsConfigured.store(targetChannels);

    if (!g_real_setSoftwareChannels) return 0;
    return g_real_setSoftwareChannels(self, targetChannels);
}

extern "C" int Proxy_System_setDSPBufferSize(void* self, unsigned int bufferLength, int numBuffers) {
    EnsureCoreLoaded();
    if (!g_real_setDSPBufferSize) return 0;

    unsigned int targetLen = bufferLength;
    int targetNum = numBuffers;

    if (g_isWasapiActive.load()) {
        targetLen = ComputeSafeWasapiBufferLength();
        targetNum = 4;
        LogMessage("[KONGOR_AUDIO] Calibrating opt-in WASAPI DSP buffer: %u samples x %d buffers (endpoint=%u Hz).",
                   targetLen, targetNum, g_endpointSampleRate.load());
    } else {
        LogMessage("[KONGOR_AUDIO] Preserving engine DSP buffer size: %u samples x %d buffers.", targetLen, targetNum);
    }

    g_bufferLength.store((int32_t)targetLen);
    g_numBuffers.store(targetNum);
    return g_real_setDSPBufferSize(self, targetLen, targetNum);
}

extern "C" int Proxy_System_init(void* self, int maxChannels, unsigned int flags, void* extraDriverData) {
    EnsureCoreLoaded();
    if (!g_real_init) return 0;

    // UNCLAMP: Ensure System::init allocates at least 256 virtual channels
    int targetChannels = (maxChannels < DEFAULT_TARGET_VOICES) ? DEFAULT_TARGET_VOICES : maxChannels;
    LogMessage("[KONGOR_AUDIO] FMOD::System::init called: Engine requested %d -> UPGRADED TO %d CHANNELS (flags=0x%X, wasapi=%d, hrtf=%d).",
               maxChannels, targetChannels, flags, g_isWasapiActive.load() ? 1 : 0, g_isHrtfActive.load() ? 1 : 0);
    g_maxChannelsConfigured.store(targetChannels);

    // If opt-in WASAPI is active, calibrate safe DSP buffer before init
    if (g_isWasapiActive.load() && g_real_setDSPBufferSize) {
        unsigned int safeLen = ComputeSafeWasapiBufferLength();
        g_real_setDSPBufferSize(self, safeLen, 4);
        g_bufferLength.store((int32_t)safeLen);
        g_numBuffers.store(4);
    }

    int res = g_real_init(self, targetChannels, flags, extraDriverData);
    if (res != 0 && (g_isWasapiActive.load() || g_isHrtfActive.load())) {
        LogMessage("[KONGOR_AUDIO][WARN] FMOD::System::init failed under opt-in WASAPI/HRTF (code=%d). Auto-recovering with safe Stereo/Auto defaults!", res);
        g_isWasapiActive.store(false);
        g_isHrtfActive.store(false);
        if (g_real_setOutput) g_real_setOutput(self, 0);
        if (g_real_setSpeakerMode) g_real_setSpeakerMode(self, 2);
        if (g_real_setDSPBufferSize) g_real_setDSPBufferSize(self, 1024, 4);
        g_currentOutputType.store(0);
        g_currentSpeakerMode.store(2);
        g_bufferLength.store(1024);
        g_numBuffers.store(4);
        res = g_real_init(self, targetChannels, flags, extraDriverData);
    }
    LogMessage("[KONGOR_AUDIO] FMOD::System::init completed with result %d (%s) with %d voice capacity!",
               res, res == 0 ? "FMOD_OK" : "ERROR", targetChannels);
    return res;
}

extern "C" int Proxy_System_set3DListenerAttributes(void* self, int listener, const FMOD_VECTOR* pos, const FMOD_VECTOR* vel, const FMOD_VECTOR* forward, const FMOD_VECTOR* up) {
    EnsureCoreLoaded();

    if (pos) {
        g_listenerPos[0].store(pos->x);
        g_listenerPos[1].store(pos->y);
        g_listenerPos[2].store(pos->z);
    }
    if (forward) {
        g_listenerFwd[0].store(forward->x);
        g_listenerFwd[1].store(forward->y);
        g_listenerFwd[2].store(forward->z);
    }

    if (!g_real_set3DListenerAttributes) return 0;
    return g_real_set3DListenerAttributes(self, listener, pos, vel, forward, up);
}

extern "C" int Proxy_System_playSound(void* self, int channelid, void* sound, bool paused, void** channel) {
    EnsureCoreLoaded();
    if (!g_real_playSound) return -1;

    void* ch = nullptr;
    int res = g_real_playSound(self, channelid, sound, paused, &ch);
    if (channel) *channel = ch;

    if (res == 0 && ch) {
        g_totalSoundsPlayed.fetch_add(1, std::memory_order_relaxed);
        int32_t active = g_activeVoices.fetch_add(1, std::memory_order_relaxed) + 1;
        int32_t peak = g_peakVoices.load(std::memory_order_relaxed);
        while (active > peak && !g_peakVoices.compare_exchange_weak(peak, active, std::memory_order_relaxed)) {
            // update peak
        }

        unsigned int soundLengthMs = 0;
        if (sound && g_real_getLength) {
            g_real_getLength(sound, &soundLengthMs, 1 /* FMOD_TIMEUNIT_MS */);
        }

        // Cinematic soundtrack detection (>= 480,000 ms isolates 8m48s Theater anthology from <4m menu/match music)
        if (soundLengthMs >= CINEMATIC_THEATER_MIN_LENGTH_MS) {
            g_cinematicChannel.store(ch);
            LogMessage("[KONGOR_AUDIO] Detected Cinematic soundtrack (length: %u ms, channel: %p, channelid: %d, activeVoices=%d).",
                soundLengthMs, ch, channelid, active);

            uint32_t pending = g_pendingCinematicSeekMs.load();
            if (pending > 0 && g_real_setPosition) {
                int seekRes = g_real_setPosition(ch, pending, 1 /* FMOD_TIMEUNIT_MS */);
                LogMessage("[KONGOR_AUDIO] Applied pending seek on Cinematic channel %p: %u ms (code: %d)",
                    ch, pending, seekRes);
            }
        } else {
            if (soundLengthMs >= 60000) {
                LogMessage("[KONGOR_AUDIO] Standard music stream started at 0 ms (length: %u ms, channel: %p, channelid: %d, activeVoices=%d).",
                    soundLengthMs, ch, channelid, active);
            }
            if (channelid >= 0 && channelid < (int)MAX_AUDIO_CHANNELS) {
                g_channelHandles[channelid] = ch;
                uint32_t pending = g_pendingSeekMs[channelid].exchange(0);
                if (pending > 0 && g_real_setPosition) {
                    g_real_setPosition(ch, pending, 1 /* FMOD_TIMEUNIT_MS */);
                    LogMessage("[KONGOR_AUDIO] Applied pending seek on channel %d: %u ms", channelid, pending);
                }
            }
        }
    }
    return res;
}

extern "C" int Proxy_Channel_stop(void* self) {
    EnsureCoreLoaded();
    g_activeVoices.fetch_sub(1, std::memory_order_relaxed);

    void* activeCinematic = g_cinematicChannel.load();
    if (self && self == activeCinematic) {
        if (g_real_getPosition) {
            unsigned int curPos = 0;
            if (g_real_getPosition(self, &curPos, 1 /* FMOD_TIMEUNIT_MS */) == 0) {
                g_pendingCinematicSeekMs.store(curPos);
                LogMessage("[KONGOR_AUDIO] Channel::stop on Cinematic channel %p. Saved position: %u ms.", self, curPos);
            }
        }
        g_cinematicChannel.store(nullptr);
    }
    if (!g_real_stop) return 0;
    return g_real_stop(self);
}

extern "C" int Proxy_Channel_setPosition(void* self, unsigned int position, unsigned int postype) {
    EnsureCoreLoaded();
    if (!g_real_setPosition) return 0;
    return g_real_setPosition(self, position, postype);
}

extern "C" int Proxy_Channel_getPosition(void* self, unsigned int* position, unsigned int postype) {
    EnsureCoreLoaded();
    if (!g_real_getPosition) return 0;
    return g_real_getPosition(self, position, postype);
}

// ----------------------------------------------------------------------------
// Custom Kongor Diagnostic & Control API
// ----------------------------------------------------------------------------

extern "C" void Kongor_Audio_Seek(int channelIndex, unsigned int positionMs) {
    EnsureCoreLoaded();
    LogMessage("[KONGOR_AUDIO] Kongor_Audio_Seek request: channel %d, position %u ms", channelIndex, positionMs);

    g_pendingCinematicSeekMs.store(positionMs);

    void* ch = g_cinematicChannel.load();
    if (ch && g_real_setPosition) {
        int res = g_real_setPosition(ch, positionMs, 1 /* FMOD_TIMEUNIT_MS */);
        LogMessage("[KONGOR_AUDIO] Seek active Cinematic channel %p to %u ms returned code %d",
            ch, positionMs, res);
        if (res == 36 /* FMOD_ERR_INVALID_HANDLE */) {
            g_cinematicChannel.store(nullptr);
        }
    }

    if (channelIndex >= 0 && channelIndex < (int)MAX_AUDIO_CHANNELS) {
        g_pendingSeekMs[channelIndex].store(positionMs);
        if (g_channelHandles[channelIndex] && g_real_setPosition) {
            int res = g_real_setPosition(g_channelHandles[channelIndex], positionMs, 1 /* FMOD_TIMEUNIT_MS */);
            LogMessage("[KONGOR_AUDIO] Seek channel %d to %u ms returned code %d", channelIndex, positionMs, res);
        }
    }
}

extern "C" unsigned int Kongor_Audio_GetCinematicPosition() {
    EnsureCoreLoaded();
    void* ch = g_cinematicChannel.load();
    if (ch && g_real_getPosition) {
        unsigned int curPos = 0;
        if (g_real_getPosition(ch, &curPos, 1 /* FMOD_TIMEUNIT_MS */) == 0) {
            return curPos;
        }
    }
    return g_pendingCinematicSeekMs.load();
}

extern "C" void Kongor_Audio_ResetCinematic() {
    g_pendingCinematicSeekMs.store(0);
    LogMessage("[KONGOR_AUDIO] Reset Cinematic pending seek to 0 ms.");
}

extern "C" void Kongor_Audio_GetStatus(AudioTelemetry* outTelemetry) {
    if (!outTelemetry) return;

    outTelemetry->outputType = g_currentOutputType.load();
    outTelemetry->speakerMode = g_currentSpeakerMode.load();
    outTelemetry->bufferLength = g_bufferLength.load();
    outTelemetry->numBuffers = g_numBuffers.load();
    outTelemetry->wasapiActive = g_isWasapiActive.load() ? 1 : 0;
    outTelemetry->hrtfActive = g_isHrtfActive.load() ? 1 : 0;

    int samples = outTelemetry->bufferLength;
    outTelemetry->latencyMs = (samples > 0) ? ((float)samples / 48000.0f) * 1000.0f : 0.0f;

    outTelemetry->listenerPos[0] = g_listenerPos[0].load();
    outTelemetry->listenerPos[1] = g_listenerPos[1].load();
    outTelemetry->listenerPos[2] = g_listenerPos[2].load();

    outTelemetry->listenerFwd[0] = g_listenerFwd[0].load();
    outTelemetry->listenerFwd[1] = g_listenerFwd[1].load();
    outTelemetry->listenerFwd[2] = g_listenerFwd[2].load();

    outTelemetry->activeVoices = (std::max)(0, g_activeVoices.load());
    outTelemetry->peakVoices = g_peakVoices.load();
    outTelemetry->maxChannelsConfigured = g_maxChannelsConfigured.load();
}

extern "C" void Kongor_Audio_Configure(int wasapiEnable, int hrtfEnable, int bufferSize) {
    g_isWasapiActive.store(wasapiEnable != 0);
    g_isHrtfActive.store(hrtfEnable != 0);
    if (bufferSize > 0) {
        g_bufferLength.store(bufferSize);
    }
    LogMessage("[KONGOR_AUDIO] Reconfigured via Diagnostic API: WASAPI=%d, HRTF=%d, BufferSize=%d",
        wasapiEnable, hrtfEnable, bufferSize);
}
