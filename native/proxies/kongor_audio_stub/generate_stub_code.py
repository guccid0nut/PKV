# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

import re

def generate():
    with open("native/proxies/kongor_audio_stub/symbols.txt", "r") as f:
        lines = [line.strip().split("\t") for line in f if line.strip()]

    def_entries = []
    cpp_funcs = []
    headers = """// ============================================================================
// File: fmod_headless_stub.cpp
// Subsystem: Project Kongor Dedicated Server Headless Audio Stub (fmodex64.dll)
// Description: Ultra-lightweight null audio implementation satisfying all 90
//              C++ / C-ABI exports imported by k2_x64.dll without creating mixer
//              threads, device context, or sound buffers.
// ============================================================================

#include <windows.h>
#include <cstdint>
#include <cstring>

// Static dummy structures to provide non-null valid pointers to caller
static uint8_t g_dummySystem[256] = {0};
static uint8_t g_dummySound[256] = {0};
static uint8_t g_dummyChannel[256] = {0};
static uint8_t g_dummyChannelGroup[256] = {0};
static uint8_t g_dummySoundGroup[256] = {0};
static uint8_t g_dummyDSP[256] = {0};
static uint8_t g_dummyDSPConnection[256] = {0};
static uint8_t g_dummyGeometry[256] = {0};
static char g_scratchAudioBuffer[65536] = {0};

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved)
{
    return TRUE;
}
"""

    for i, (mangled, undec) in enumerate(lines):
        # Create a unique clean stub function name
        # e.g. ?createSound@System@FMOD@@... -> Stub_System_createSound_7
        base_match = re.search(r'\bFMOD::(\w+)::(\w+)', undec)
        if base_match:
            cls, method = base_match.groups()
            stub_name = f"Stub_{cls}_{method}_{i}"
        else:
            clean = re.sub(r'[^a-zA-Z0-9_]', '_', undec.split('(')[0].strip())
            stub_name = f"Stub_{clean}_{i}"

        def_entries.append(f"    {mangled} = {stub_name}")

        # Determine behavior based on undecorated signature
        body_lines = []

        # Return type: almost all return enum FMOD_RESULT (0 = FMOD_OK)
        # Check if undec contains specific patterns
        if "createSound" in undec or "createStream" in undec:
            body_lines.append("    if (p4) *reinterpret_cast<void**>(p4) = g_dummySound;")
        elif "playSound" in undec:
            body_lines.append("    if (p4) *reinterpret_cast<void**>(p4) = g_dummyChannel;")
        elif "createChannelGroup" in undec or "getMasterChannelGroup" in undec:
            body_lines.append("    if (p2) *reinterpret_cast<void**>(p2) = g_dummyChannelGroup;")
        elif "getMasterSoundGroup" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<void**>(p1) = g_dummySoundGroup;")
        elif "createDSPByType" in undec:
            body_lines.append("    if (p2) *reinterpret_cast<void**>(p2) = g_dummyDSP;")
        elif "createGeometry" in undec or "loadGeometry" in undec:
            body_lines.append("    if (p3) *reinterpret_cast<void**>(p3) = g_dummyGeometry;")
        elif "getCurrentSound" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<void**>(p1) = g_dummySound;")
        elif "getSound" in undec and "SoundGroup" in undec:
            body_lines.append("    if (p2) *reinterpret_cast<void**>(p2) = g_dummySound;")
        elif "addDSP" in undec and "Channel" in undec:
            body_lines.append("    if (p2) *reinterpret_cast<void**>(p2) = g_dummyDSPConnection;")
        elif "getUserData" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<void**>(p1) = nullptr;")
        elif "FMOD_System_Create" in undec:
            body_lines.append("    if (p0) *reinterpret_cast<void**>(p0) = g_dummySystem;")
        elif "getVersion" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<uint32_t*>(p1) = 0x00044457; // FMOD 4.44.57")
        elif "isPlaying" in undec or "getMute" in undec or "isRecording" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<bool*>(p1) = false;")
        elif "getChannelsPlaying" in undec or "getNumDrivers" in undec or "getRecordNumDrivers" in undec or "getNumSounds" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<int*>(p1) = 0;")
        elif "getRecordPosition" in undec or "getPosition" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<uint32_t*>(p1) = 0;")
        elif "getSpeakerMode" in undec or "getOutput" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<int*>(p1) = 0;")
        elif "getFrequency" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<float*>(p1) = 44100.0f;")
        elif "getVolume" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<float*>(p1) = 1.0f;")
        elif "get3DMinMaxDistance" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<float*>(p1) = 1.0f;")
            body_lines.append("    if (p2) *reinterpret_cast<float*>(p2) = 10000.0f;")
        elif "getLength" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<uint32_t*>(p1) = 1000;")
        elif "getDefaults" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<float*>(p1) = 44100.0f;")
            body_lines.append("    if (p2) *reinterpret_cast<float*>(p2) = 1.0f;")
            body_lines.append("    if (p3) *reinterpret_cast<float*>(p3) = 0.0f;")
            body_lines.append("    if (p4) *reinterpret_cast<int*>(p4) = 128;")
        elif "getFormat" in undec:
            body_lines.append("    if (p1) *reinterpret_cast<int*>(p1) = 0;")
            body_lines.append("    if (p2) *reinterpret_cast<int*>(p2) = 0;")
            body_lines.append("    if (p3) *reinterpret_cast<int*>(p3) = 2;")
            body_lines.append("    if (p4) *reinterpret_cast<int*>(p4) = 16;")
        elif "getDriverInfoW" in undec or "getRecordDriverInfoW" in undec:
            body_lines.append("    if (p2 && p3 > 0) reinterpret_cast<wchar_t*>(p2)[0] = 0;")
        elif "getDriverCaps" in undec:
            body_lines.append("    if (p2) *reinterpret_cast<uint32_t*>(p2) = 0;")
            body_lines.append("    if (p3) *reinterpret_cast<int*>(p3) = 44100;")
            body_lines.append("    if (p4) *reinterpret_cast<int*>(p4) = 0;")
        elif "FMOD_Memory_GetStats" in undec:
            body_lines.append("    if (p0) *reinterpret_cast<int*>(p0) = 0;")
            body_lines.append("    if (p1) *reinterpret_cast<int*>(p1) = 0;")
        elif "lock" in undec and "Sound" in undec:
            body_lines.append("    if (p3) *reinterpret_cast<void**>(p3) = g_scratchAudioBuffer;")
            body_lines.append("    if (p5) *reinterpret_cast<uint32_t*>(p5) = (reinterpret_cast<uintptr_t>(p2) < sizeof(g_scratchAudioBuffer)) ? static_cast<uint32_t>(reinterpret_cast<uintptr_t>(p2)) : static_cast<uint32_t>(sizeof(g_scratchAudioBuffer));")
            body_lines.append("    if (p4) *reinterpret_cast<void**>(p4) = nullptr;")
            body_lines.append("    if (p6) *reinterpret_cast<uint32_t*>(p6) = 0;")
        elif "getTag" in undec:
            body_lines.append("    return 18; // FMOD_ERR_TAGNOTFOUND")
        elif "save" in undec and "Geometry" in undec:
            body_lines.append("    if (p2) *reinterpret_cast<int*>(p2) = 0;")

        if "getTag" not in undec:
            body_lines.append("    return 0; // FMOD_OK")

        # x64 MSVC fastcall: takes up to 8 generic pointer-sized arguments safely
        # RCX, RDX, R8, R9, [rsp+28], [rsp+30], [rsp+38], [rsp+40]
        arg_list = ", ".join([f"void* p{k}" for k in range(8)])
        func_def = f"extern \"C\" int __cdecl {stub_name}({arg_list})\n{{\n" + "\n".join(body_lines) + "\n}\n"
        cpp_funcs.append(func_def)

    def_content = "LIBRARY fmodex64\nEXPORTS\n" + "\n".join(def_entries) + "\n"
    cpp_content = headers + "\n" + "\n".join(cpp_funcs)

    with open("native/proxies/kongor_audio_stub/fmodex64.def", "w") as f:
        f.write(def_content)

    with open("native/proxies/kongor_audio_stub/fmod_headless_stub.cpp", "w") as f:
        f.write(cpp_content)

    print(f"Generated fmodex64.def with {len(def_entries)} exports.")
    print(f"Generated fmod_headless_stub.cpp with {len(cpp_funcs)} stubs.")

if __name__ == "__main__":
    generate()
