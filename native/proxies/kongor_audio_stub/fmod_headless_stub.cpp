// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
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

extern "C" int __cdecl Stub_Channel_addDSP_0(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<void**>(p2) = g_dummyDSPConnection;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_ChannelGroup_addGroup_1(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Geometry_addPolygon_2(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_createChannelGroup_3(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<void**>(p2) = g_dummyChannelGroup;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_createDSPByType_4(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<void**>(p2) = g_dummyDSP;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_createGeometry_5(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p3) *reinterpret_cast<void**>(p3) = g_dummyGeometry;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_createSound_6(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p4) *reinterpret_cast<void**>(p4) = g_dummySound;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_createStream_7(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p4) *reinterpret_cast<void**>(p4) = g_dummySound;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_get3DAttributes_8(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_get3DMinMaxDistance_9(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<float*>(p1) = 1.0f;
    if (p2) *reinterpret_cast<float*>(p2) = 10000.0f;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getChannelsPlaying_10(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_getCurrentSound_11(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<void**>(p1) = g_dummySound;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_getDefaults_12(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<float*>(p1) = 44100.0f;
    if (p2) *reinterpret_cast<float*>(p2) = 1.0f;
    if (p3) *reinterpret_cast<float*>(p3) = 0.0f;
    if (p4) *reinterpret_cast<int*>(p4) = 128;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getDriverCaps_13(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<uint32_t*>(p2) = 0;
    if (p3) *reinterpret_cast<int*>(p3) = 44100;
    if (p4) *reinterpret_cast<int*>(p4) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getDriverInfoW_14(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2 && p3 > 0) reinterpret_cast<wchar_t*>(p2)[0] = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_getFormat_15(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    if (p2) *reinterpret_cast<int*>(p2) = 0;
    if (p3) *reinterpret_cast<int*>(p3) = 2;
    if (p4) *reinterpret_cast<int*>(p4) = 16;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_getFrequency_16(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<float*>(p1) = 44100.0f;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_getLength_17(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<uint32_t*>(p1) = 1000;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getMasterChannelGroup_18(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<void**>(p2) = g_dummyChannelGroup;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getMasterSoundGroup_19(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<void**>(p1) = g_dummySoundGroup;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_getMemoryInfo_20(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_getMode_21(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_getMute_22(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<bool*>(p1) = false;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_ChannelGroup_getMute_23(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<bool*>(p1) = false;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getNumDrivers_24(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_SoundGroup_getNumSounds_25(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getOutput_26(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_getPosition_27(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<uint32_t*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getRecordDriverInfoW_28(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2 && p3 > 0) reinterpret_cast<wchar_t*>(p2)[0] = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getRecordNumDrivers_29(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getRecordPosition_30(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<uint32_t*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_SoundGroup_getSound_31(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<void**>(p2) = g_dummySound;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getSpeakerMode_32(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_getTag_33(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 18; // FMOD_ERR_TAGNOTFOUND
}

extern "C" int __cdecl Stub_Channel_getUserData_34(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<void**>(p1) = nullptr;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_getVersion_35(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<uint32_t*>(p1) = 0x00044457; // FMOD 4.44.57
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_getVolume_36(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<float*>(p1) = 1.0f;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_init_37(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_isPlaying_38(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<bool*>(p1) = false;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_isRecording_39(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p1) *reinterpret_cast<bool*>(p1) = false;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_loadGeometry_40(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p3) *reinterpret_cast<void**>(p3) = g_dummyGeometry;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_lock_41(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p3) *reinterpret_cast<void**>(p3) = g_scratchAudioBuffer;
    if (p5) *reinterpret_cast<uint32_t*>(p5) = (reinterpret_cast<uintptr_t>(p2) < sizeof(g_scratchAudioBuffer)) ? static_cast<uint32_t>(reinterpret_cast<uintptr_t>(p2)) : static_cast<uint32_t>(sizeof(g_scratchAudioBuffer));
    if (p4) *reinterpret_cast<void**>(p4) = nullptr;
    if (p6) *reinterpret_cast<uint32_t*>(p6) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_playSound_42(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p4) *reinterpret_cast<void**>(p4) = g_dummyChannel;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_recordStart_43(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_recordStop_44(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_ChannelGroup_release_45(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_DSP_release_46(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Geometry_release_47(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_release_48(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_release_49(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Geometry_save_50(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p2) *reinterpret_cast<int*>(p2) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_set3DAttributes_51(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_set3DCustomRolloff_52(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_set3DListenerAttributes_53(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_set3DMinMaxDistance_54(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_set3DRolloffCallback_55(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_set3DSettings_56(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_set3DSpread_57(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Geometry_setActive_58(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setCallback_59(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setChannelGroup_60(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setDSPBufferSize_61(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setDriver_62(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setFileSystem_63(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setFrequency_64(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setGeometrySettings_65(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_setLoopPoints_66(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setMode_67(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setMute_68(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_ChannelGroup_setMute_69(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setOutput_70(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_DSP_setParameter_71(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setPaused_72(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_ChannelGroup_setPitch_73(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setPosition_74(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Geometry_setPosition_75(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setPriority_76(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setSoftwareChannels_77(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setSoftwareFormat_78(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_setSpeakerMode_79(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setUserData_80(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_setVolume_81(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_ChannelGroup_setVolume_82(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Channel_stop_83(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_SoundGroup_stop_84(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_Sound_unlock_85(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p3) *reinterpret_cast<void**>(p3) = g_scratchAudioBuffer;
    if (p5) *reinterpret_cast<uint32_t*>(p5) = (reinterpret_cast<uintptr_t>(p2) < sizeof(g_scratchAudioBuffer)) ? static_cast<uint32_t>(reinterpret_cast<uintptr_t>(p2)) : static_cast<uint32_t>(sizeof(g_scratchAudioBuffer));
    if (p4) *reinterpret_cast<void**>(p4) = nullptr;
    if (p6) *reinterpret_cast<uint32_t*>(p6) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_System_update_86(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_FMOD_Memory_GetStats_87(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p0) *reinterpret_cast<int*>(p0) = 0;
    if (p1) *reinterpret_cast<int*>(p1) = 0;
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_FMOD_Memory_Initialize_88(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    return 0; // FMOD_OK
}

extern "C" int __cdecl Stub_FMOD_System_Create_89(void* p0, void* p1, void* p2, void* p3, void* p4, void* p5, void* p6, void* p7)
{
    if (p0) *reinterpret_cast<void**>(p0) = g_dummySystem;
    return 0; // FMOD_OK
}
