// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <windows.h>

extern "C" {
    extern FARPROC pfnGetFileVersionInfoA;
    extern FARPROC pfnGetFileVersionInfoByHandle;
    extern FARPROC pfnGetFileVersionInfoExA;
    extern FARPROC pfnGetFileVersionInfoExW;
    extern FARPROC pfnGetFileVersionInfoSizeA;
    extern FARPROC pfnGetFileVersionInfoSizeExA;
    extern FARPROC pfnGetFileVersionInfoSizeExW;
    extern FARPROC pfnGetFileVersionInfoSizeW;
    extern FARPROC pfnGetFileVersionInfoW;
    extern FARPROC pfnVerFindFileA;
    extern FARPROC pfnVerFindFileW;
    extern FARPROC pfnVerInstallFileA;
    extern FARPROC pfnVerInstallFileW;
    extern FARPROC pfnVerLanguageNameA;
    extern FARPROC pfnVerLanguageNameW;
    extern FARPROC pfnVerQueryValueA;
    extern FARPROC pfnVerQueryValueW;
}
