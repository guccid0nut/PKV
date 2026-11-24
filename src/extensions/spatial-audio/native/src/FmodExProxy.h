// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

#pragma once

#include <windows.h>
#include <cstdint>

#ifdef FMOD_EXPORTS
#define FMOD_API __declspec(dllexport)
#else
#define FMOD_API __declspec(dllimport)
#endif

// Forward-declared FMOD Vector struct
struct FMOD_VECTOR {
    float x;
    float y;
    float z;
};

// Core K2 FMOD Ex API prototypes intercepted for OpenAL Soft HRTF
extern "C" {
    FMOD_API int __stdcall FMOD_System_Create(void** system);
    FMOD_API int __stdcall FMOD_Memory_Initialize(void* poolmem, int poollen, void* useralloc, void* userrealloc, void* userfree, int memtypeflags);
    FMOD_API int __stdcall FMOD_Memory_GetStats(int* currentalloced, int* maxalloced, int blocking);
    FMOD_API int __stdcall FMOD_System_Set3DListenerAttributes(void* system, int listener, const FMOD_VECTOR* pos, const FMOD_VECTOR* vel, const FMOD_VECTOR* forward, const FMOD_VECTOR* up);
    FMOD_API int __stdcall FMOD_System_Get3DListenerAttributes(void* system, int listener, FMOD_VECTOR* pos, FMOD_VECTOR* vel, FMOD_VECTOR* forward, FMOD_VECTOR* up);

    // Spatial Voice Interop endpoints
    FMOD_API void __stdcall Kongor_SpatialAudio_GetListenerPose(FMOD_VECTOR* pos, FMOD_VECTOR* forward, FMOD_VECTOR* up);
    typedef void(__stdcall* PFN_SpatialAudioListenerCallback)(const FMOD_VECTOR* pos, const FMOD_VECTOR* forward, const FMOD_VECTOR* up);
    FMOD_API void __stdcall Kongor_SpatialAudio_RegisterListenerCallback(PFN_SpatialAudioListenerCallback callback);
}
