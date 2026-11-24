// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: avdevice_stub.cpp
// Subsystem: Project Kongor Null Device Stub (avdevice-x64-58.dll)
// Description: Replaces 687 KB dead library with a lightweight null register stub.
// ============================================================================

#include <windows.h>

extern "C" {
__declspec(dllexport) void avdevice_register_all(void) {
    // No-op in modern FFmpeg architectures
}
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    return TRUE;
}
