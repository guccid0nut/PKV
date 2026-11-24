// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

#include "FmodExProxy.h"
#include <iostream>

static HMODULE g_realFmod = nullptr;

static void EnsureRealFmodLoaded() {
    if (!g_realFmod) {
        // Look for the renamed core engine dll
        g_realFmod = LoadLibraryA("fmodex64_core.dll");
        if (!g_realFmod) {
            // Fallback to system or local default
            g_realFmod = LoadLibraryA("fmodex64.dll");
        }
    }
}

extern "C" {
    FMOD_API int __stdcall FMOD_System_Create(void** system) {
        EnsureRealFmodLoaded();
        if (g_realFmod) {
            typedef int(__stdcall* PFN_FMOD_System_Create)(void**);
            auto pfn = (PFN_FMOD_System_Create)GetProcAddress(g_realFmod, "FMOD_System_Create");
            if (pfn) return pfn(system);
        }
        return 0; // FMOD_OK
    }

    FMOD_API int __stdcall FMOD_Memory_Initialize(void* poolmem, int poollen, void* useralloc, void* userrealloc, void* userfree, int memtypeflags) {
        EnsureRealFmodLoaded();
        if (g_realFmod) {
            typedef int(__stdcall* PFN_FMOD_Memory_Initialize)(void*, int, void*, void*, void*, int);
            auto pfn = (PFN_FMOD_Memory_Initialize)GetProcAddress(g_realFmod, "FMOD_Memory_Initialize");
            if (pfn) return pfn(poolmem, poollen, useralloc, userrealloc, userfree, memtypeflags);
        }
        return 0;
    }

    FMOD_API int __stdcall FMOD_Memory_GetStats(int* currentalloced, int* maxalloced, int blocking) {
        EnsureRealFmodLoaded();
        if (g_realFmod) {
            typedef int(__stdcall* PFN_FMOD_Memory_GetStats)(int*, int*, int);
            auto pfn = (PFN_FMOD_Memory_GetStats)GetProcAddress(g_realFmod, "FMOD_Memory_GetStats");
            if (pfn) return pfn(currentalloced, maxalloced, blocking);
        }
        return 0;
    }

    static FMOD_VECTOR g_listenerPos = {0.0f, 0.0f, 0.0f};
    static FMOD_VECTOR g_listenerVel = {0.0f, 0.0f, 0.0f};
    static FMOD_VECTOR g_listenerForward = {0.0f, 1.0f, 0.0f};
    static FMOD_VECTOR g_listenerUp = {0.0f, 0.0f, 1.0f};
    static PFN_SpatialAudioListenerCallback g_listenerCallback = nullptr;

    FMOD_API int __stdcall FMOD_System_Set3DListenerAttributes(
        void* system,
        int listener,
        const FMOD_VECTOR* pos,
        const FMOD_VECTOR* vel,
        const FMOD_VECTOR* forward,
        const FMOD_VECTOR* up) {
        
        EnsureRealFmodLoaded();

        if (listener == 0) {
            if (pos) g_listenerPos = *pos;
            if (vel) g_listenerVel = *vel;
            if (forward) g_listenerForward = *forward;
            if (up) g_listenerUp = *up;

            if (g_listenerCallback) {
                g_listenerCallback(&g_listenerPos, &g_listenerForward, &g_listenerUp);
            }
        }

        if (g_realFmod) {
            typedef int(__stdcall* PFN_FMOD_System_Set3DListenerAttributes)(void*, int, const FMOD_VECTOR*, const FMOD_VECTOR*, const FMOD_VECTOR*, const FMOD_VECTOR*);
            auto pfn = (PFN_FMOD_System_Set3DListenerAttributes)GetProcAddress(g_realFmod, "FMOD_System_Set3DListenerAttributes");
            if (pfn) return pfn(system, listener, pos, vel, forward, up);
        }
        return 0; // FMOD_OK
    }

    FMOD_API int __stdcall FMOD_System_Get3DListenerAttributes(
        void* system,
        int listener,
        FMOD_VECTOR* pos,
        FMOD_VECTOR* vel,
        FMOD_VECTOR* forward,
        FMOD_VECTOR* up) {

        EnsureRealFmodLoaded();

        if (g_realFmod) {
            typedef int(__stdcall* PFN_FMOD_System_Get3DListenerAttributes)(void*, int, FMOD_VECTOR*, FMOD_VECTOR*, FMOD_VECTOR*, FMOD_VECTOR*);
            auto pfn = (PFN_FMOD_System_Get3DListenerAttributes)GetProcAddress(g_realFmod, "FMOD_System_Get3DListenerAttributes");
            if (pfn) return pfn(system, listener, pos, vel, forward, up);
        }

        if (listener == 0) {
            if (pos) *pos = g_listenerPos;
            if (vel) *vel = g_listenerVel;
            if (forward) *forward = g_listenerForward;
            if (up) *up = g_listenerUp;
            return 0;
        }

        return 0;
    }

    FMOD_API void __stdcall Kongor_SpatialAudio_GetListenerPose(FMOD_VECTOR* pos, FMOD_VECTOR* forward, FMOD_VECTOR* up) {
        if (pos) *pos = g_listenerPos;
        if (forward) *forward = g_listenerForward;
        if (up) *up = g_listenerUp;
    }

    FMOD_API void __stdcall Kongor_SpatialAudio_RegisterListenerCallback(PFN_SpatialAudioListenerCallback callback) {
        g_listenerCallback = callback;
    }
}
