// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <mutex>
#include <atomic>
#include <string>
#include <d3d9.h>
#include <d3d9on12.h>
#include "../include/SVidDriverAPI.h"
#include "d3d12_bridge.h"

static HMODULE g_hRealD3D9 = nullptr;
static SVidDriverAPI g_realAPI = {};
static FILE* g_pLogFile = nullptr;
static std::mutex g_logMutex;
static std::atomic<uint64_t> g_frameCount{0};

// Extensively log to vid_d3d12_spy.log and OutputDebugStringA
void D3D12Log(const char* format, ...) {
    char buf[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buf, sizeof(buf), format, args);
    va_end(args);

    OutputDebugStringA("[VID_D3D12] ");
    OutputDebugStringA(buf);
    OutputDebugStringA("\n");

    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_pLogFile) {
        wchar_t modulePath[MAX_PATH] = {0};
        HMODULE hSelf = GetModuleHandleW(L"vid_d3d12_x64.dll");
        if (hSelf) {
            GetModuleFileNameW(hSelf, modulePath, MAX_PATH);
            wchar_t* pSlash = wcsrchr(modulePath, L'\\');
            if (pSlash) {
                *(pSlash + 1) = L'\0';
                wcscat_s(modulePath, L"vid_d3d12_spy.log");
                g_pLogFile = _wfopen(modulePath, L"a");
            }
        }
        if (!g_pLogFile) {
            g_pLogFile = fopen("vid_d3d12_spy.log", "a");
        }
        if (!g_pLogFile) return;
        setvbuf(g_pLogFile, nullptr, _IONBF, 0); // Unbuffered for instant crash diagnostics
    }
    
    SYSTEMTIME st;
    GetLocalTime(&st);
    fprintf(g_pLogFile, "[%02d:%02d:%02d.%03d] [VID_D3D12_SPY] %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, buf);
    fflush(g_pLogFile);
}

// Function pointer signature aliases for lifecycle operations
typedef bool  (__fastcall *FnInit)(void*);
typedef bool  (__fastcall *FnStartWindow)();
typedef int   (__fastcall *FnSetMode)();
typedef void  (__fastcall *FnShutdown)();
typedef bool  (__fastcall *FnBeginFrame)();
typedef void  (__fastcall *FnEndFrame)();
typedef void  (__fastcall *FnSetVSync)(bool);
typedef void  (__fastcall *FnSetMaxFPS)(uint32_t);

// -------------------------------------------------------------------------------------------------
// COM VTable Hooking for IDirect3D9 and IDirect3DDevice9
// -------------------------------------------------------------------------------------------------
typedef HRESULT (STDMETHODCALLTYPE *FnCreateDevice)(
    IDirect3D9* This,
    UINT Adapter,
    D3DDEVTYPE DeviceType,
    HWND hFocusWindow,
    DWORD BehaviorFlags,
    D3DPRESENT_PARAMETERS* pPresentationParameters,
    IDirect3DDevice9** ppReturnedDeviceInterface
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateDeviceEx)(
    IDirect3D9Ex* This,
    UINT Adapter,
    D3DDEVTYPE DeviceType,
    HWND hFocusWindow,
    DWORD BehaviorFlags,
    D3DPRESENT_PARAMETERS* pPresentationParameters,
    D3DDISPLAYMODEEX* pFullscreenDisplayMode,
    IDirect3DDevice9Ex** ppReturnedDeviceInterface
);

typedef HRESULT (STDMETHODCALLTYPE *FnReset)(
    IDirect3DDevice9* This,
    D3DPRESENT_PARAMETERS* pPresentationParameters
);

typedef HRESULT (STDMETHODCALLTYPE *FnResetEx)(
    IDirect3DDevice9Ex* This,
    D3DPRESENT_PARAMETERS* pPresentationParameters,
    D3DDISPLAYMODEEX* pFullscreenDisplayMode
);

typedef HRESULT (STDMETHODCALLTYPE *FnPresent)(
    IDirect3DDevice9* This,
    CONST RECT* pSourceRect,
    CONST RECT* pDestRect,
    HWND hDestWindowOverride,
    CONST RGNDATA* pDirtyRegion
);

typedef HRESULT (STDMETHODCALLTYPE *FnPresentEx)(
    IDirect3DDevice9Ex* This,
    CONST RECT* pSourceRect,
    CONST RECT* pDestRect,
    HWND hDestWindowOverride,
    CONST RGNDATA* pDirtyRegion,
    DWORD dwFlags
);

static FnCreateDevice   g_pfnRealCreateDevice = nullptr;
static FnCreateDeviceEx g_pfnRealCreateDeviceEx = nullptr;
static void*            g_d3d9ProxyVTable[32] = {0};
static HWND             g_hGameWindow = nullptr;

static void PatchPresentationParameters(D3DPRESENT_PARAMETERS* pPresentationParameters, HWND hFocusWindow) {
    if (!pPresentationParameters) return;
    D3D12Log("[D3D12_PARAMS_RAW] w=%u, h=%u, fmt=%u, cnt=%u, msType=%u, msQual=%u, swapEffect=%u, hDevWnd=0x%p, windowed=%d, flags=0x%x, interval=0x%x",
             pPresentationParameters->BackBufferWidth,
             pPresentationParameters->BackBufferHeight,
             pPresentationParameters->BackBufferFormat,
             pPresentationParameters->BackBufferCount,
             pPresentationParameters->MultiSampleType,
             pPresentationParameters->MultiSampleQuality,
             pPresentationParameters->SwapEffect,
             pPresentationParameters->hDeviceWindow,
             pPresentationParameters->Windowed,
             pPresentationParameters->Flags,
             pPresentationParameters->PresentationInterval);

    // Ensure target window is properly assigned to the valid game HWND
    if (!pPresentationParameters->hDeviceWindow && hFocusWindow) {
        pPresentationParameters->hDeviceWindow = hFocusWindow;
    }
    if (pPresentationParameters->hDeviceWindow) {
        g_hGameWindow = pPresentationParameters->hDeviceWindow;
    } else if (hFocusWindow) {
        g_hGameWindow = hFocusWindow;
    }

    D3D12Log("[D3D12_PARAMS_PATCHED] w=%u, h=%u, fmt=%u, cnt=%u, swapEffect=%u, hDevWnd=0x%p, windowed=%d",
             pPresentationParameters->BackBufferWidth,
             pPresentationParameters->BackBufferHeight,
             pPresentationParameters->BackBufferFormat,
             pPresentationParameters->BackBufferCount,
             pPresentationParameters->SwapEffect,
             pPresentationParameters->hDeviceWindow,
             pPresentationParameters->Windowed);
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDevice(
    IDirect3D9* This,
    UINT Adapter,
    D3DDEVTYPE DeviceType,
    HWND hFocusWindow,
    DWORD BehaviorFlags,
    D3DPRESENT_PARAMETERS* pPresentationParameters,
    IDirect3DDevice9** ppReturnedDeviceInterface
) {
    D3D12Log("[D3D12_INTERCEPT] IDirect3D9::CreateDevice called (hFocusWindow=0x%p)", hFocusWindow);
    PatchPresentationParameters(pPresentationParameters, hFocusWindow);

    HRESULT hr = g_pfnRealCreateDevice(This, Adapter, DeviceType, hFocusWindow, BehaviorFlags, pPresentationParameters, ppReturnedDeviceInterface);
    if (SUCCEEDED(hr) && ppReturnedDeviceInterface && *ppReturnedDeviceInterface) {
        D3D12Log("[D3D12_INTERCEPT] IDirect3D9::CreateDevice SUCCESS! Created IDirect3DDevice9 = 0x%p", *ppReturnedDeviceInterface);
        
        // Extract native D3D12 device and hardware queues!
        D3D12Bridge::Get().InitializeFrom9On12(*ppReturnedDeviceInterface);

        if (hFocusWindow) {
            char title[256];
            snprintf(title, sizeof(title), "Heroes of Newerth [DIRECTX 12 ACTIVE | %s | %s]",
                     D3D12Bridge::Get().GetGpuName().c_str(),
                     D3D12Bridge::Get().GetFeatureLevelString().c_str());
            SetWindowTextA(hFocusWindow, title);
        }
    } else {
        D3D12Log("[D3D12_INTERCEPT] IDirect3D9::CreateDevice FAILED! hr=0x%08X", hr);
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDeviceEx(
    IDirect3D9Ex* This,
    UINT Adapter,
    D3DDEVTYPE DeviceType,
    HWND hFocusWindow,
    DWORD BehaviorFlags,
    D3DPRESENT_PARAMETERS* pPresentationParameters,
    D3DDISPLAYMODEEX* pFullscreenDisplayMode,
    IDirect3DDevice9Ex** ppReturnedDeviceInterface
) {
    D3D12Log("[D3D12_INTERCEPT] IDirect3D9Ex::CreateDeviceEx called (hFocusWindow=0x%p)", hFocusWindow);
    PatchPresentationParameters(pPresentationParameters, hFocusWindow);

    HRESULT hr = g_pfnRealCreateDeviceEx(This, Adapter, DeviceType, hFocusWindow, BehaviorFlags, pPresentationParameters, pFullscreenDisplayMode, ppReturnedDeviceInterface);
    if (SUCCEEDED(hr) && ppReturnedDeviceInterface && *ppReturnedDeviceInterface) {
        D3D12Log("[D3D12_INTERCEPT] IDirect3D9Ex::CreateDeviceEx SUCCESS! Created IDirect3DDevice9Ex = 0x%p", *ppReturnedDeviceInterface);
        
        // Extract native D3D12 device and hardware queues!
        D3D12Bridge::Get().InitializeFrom9On12((IDirect3DDevice9*)*ppReturnedDeviceInterface);

        if (hFocusWindow) {
            char title[256];
            snprintf(title, sizeof(title), "Heroes of Newerth [DIRECTX 12 ACTIVE | %s | %s]",
                     D3D12Bridge::Get().GetGpuName().c_str(),
                     D3D12Bridge::Get().GetFeatureLevelString().c_str());
            SetWindowTextA(hFocusWindow, title);
        }
    } else {
        D3D12Log("[D3D12_INTERCEPT] IDirect3D9Ex::CreateDeviceEx FAILED! hr=0x%08X", hr);
    }
    return hr;
}

// -------------------------------------------------------------------------------------------------
// IAT Hook for Direct3DCreate9 in vid_d3d9_x64.dll
// -------------------------------------------------------------------------------------------------
typedef IDirect3D9* (WINAPI *FnDirect3DCreate9)(UINT);
static FnDirect3DCreate9 g_pfnRealDirect3DCreate9 = nullptr;

static IDirect3D9* WINAPI Hook_Direct3DCreate9(UINT SDKVersion) {
    D3D12Log("================================================================================");
    D3D12Log("[D3D12_INTERCEPT] Direct3DCreate9(SDKVersion=0x%08X) called by vid_d3d9_x64.dll!", SDKVersion);
    D3D12Log("[D3D12_INTERCEPT] Redirecting to Microsoft Inbox C:\\Windows\\System32\\d3d9.dll!Direct3DCreate9On12...");

    HMODULE hSystemD3D9 = LoadLibraryW(L"C:\\Windows\\System32\\d3d9.dll");
    if (!hSystemD3D9) {
        D3D12Log("FATAL: Failed to load C:\\Windows\\System32\\d3d9.dll! Error: %lu", GetLastError());
        return nullptr;
    }

    PFN_Direct3DCreate9On12 pfnCreate9On12 = (PFN_Direct3DCreate9On12)GetProcAddress(hSystemD3D9, "Direct3DCreate9On12");
    if (pfnCreate9On12) {
        D3D9ON12_ARGS args = {};
        args.Enable9On12 = TRUE;
        args.NumQueues = 0;
        args.NodeMask = 0;

        IDirect3D9* pD3D9 = pfnCreate9On12(SDKVersion, &args, 1);
        if (pD3D9) {
            D3D12Log("[D3D12_INTERCEPT] SUCCESS: Direct3DCreate9On12 returned IDirect3D9 = 0x%p", pD3D9);
            D3D12Log("[D3D12_INTERCEPT] Installing proxy vtable on IDirect3D9 to capture device creation...");

            void** originalVTable = *(void***)pD3D9;
            memcpy(g_d3d9ProxyVTable, originalVTable, sizeof(void*) * 32);

            g_pfnRealCreateDevice = (FnCreateDevice)originalVTable[16];
            g_d3d9ProxyVTable[16] = (void*)Hook_CreateDevice;

            g_pfnRealCreateDeviceEx = (FnCreateDeviceEx)originalVTable[20];
            g_d3d9ProxyVTable[20] = (void*)Hook_CreateDeviceEx;

            *(void***)pD3D9 = g_d3d9ProxyVTable;
            D3D12Log("[D3D12_INTERCEPT] Proxy vtable installed (CreateDevice slot 16 & CreateDeviceEx slot 20 hooked).");

            return pD3D9;
        } else {
            D3D12Log("ERROR: Direct3DCreate9On12 returned nullptr!");
        }
    } else {
        D3D12Log("ERROR: GetProcAddress for Direct3DCreate9On12 failed!");
    }

    D3D12Log("FALLBACK: Calling original Direct3DCreate9");
    if (g_pfnRealDirect3DCreate9) {
        return g_pfnRealDirect3DCreate9(SDKVersion);
    }
    return nullptr;
}

// -------------------------------------------------------------------------------------------------
// Interceptor for Direct3DCreate9Ex
// -------------------------------------------------------------------------------------------------
typedef HRESULT (WINAPI *FnDirect3DCreate9Ex)(UINT, IDirect3D9Ex**);
static FnDirect3DCreate9Ex g_pfnRealDirect3DCreate9Ex = nullptr;

static HRESULT WINAPI Hook_Direct3DCreate9Ex(UINT SDKVersion, IDirect3D9Ex** ppOutputInterface) {
    D3D12Log("================================================================================");
    D3D12Log("[D3D12_INTERCEPT] Direct3DCreate9Ex(SDKVersion=0x%08X) called by vid_d3d9_x64.dll!", SDKVersion);
    D3D12Log("[D3D12_INTERCEPT] Redirecting to Microsoft Inbox C:\\Windows\\System32\\d3d9.dll!Direct3DCreate9On12Ex...");

    HMODULE hSystemD3D9 = LoadLibraryW(L"C:\\Windows\\System32\\d3d9.dll");
    if (!hSystemD3D9) {
        D3D12Log("FATAL: Failed to load C:\\Windows\\System32\\d3d9.dll! Error: %lu", GetLastError());
        return E_FAIL;
    }

    PFN_Direct3DCreate9On12Ex pfnCreate9On12Ex = (PFN_Direct3DCreate9On12Ex)GetProcAddress(hSystemD3D9, "Direct3DCreate9On12Ex");
    if (pfnCreate9On12Ex) {
        D3D9ON12_ARGS args = {};
        args.Enable9On12 = TRUE;
        args.NumQueues = 0;
        args.NodeMask = 0;

        HRESULT hr = pfnCreate9On12Ex(SDKVersion, &args, 1, ppOutputInterface);
        if (SUCCEEDED(hr) && ppOutputInterface && *ppOutputInterface) {
            D3D12Log("[D3D12_INTERCEPT] SUCCESS: Direct3DCreate9On12Ex returned IDirect3D9Ex = 0x%p", *ppOutputInterface);
            D3D12Log("[D3D12_INTERCEPT] Installing proxy vtable on IDirect3D9Ex to capture device creation...");

            void** originalVTable = *(void***)(*ppOutputInterface);
            memcpy(g_d3d9ProxyVTable, originalVTable, sizeof(void*) * 32);

            g_pfnRealCreateDevice = (FnCreateDevice)originalVTable[16];
            g_d3d9ProxyVTable[16] = (void*)Hook_CreateDevice;

            g_pfnRealCreateDeviceEx = (FnCreateDeviceEx)originalVTable[20];
            g_d3d9ProxyVTable[20] = (void*)Hook_CreateDeviceEx;

            *(void***)(*ppOutputInterface) = g_d3d9ProxyVTable;
            D3D12Log("[D3D12_INTERCEPT] Proxy vtable installed (CreateDevice slot 16 & CreateDeviceEx slot 20 hooked).");

            return hr;
        } else {
            D3D12Log("ERROR: Direct3DCreate9On12Ex failed! hr=0x%08X", hr);
        }
    } else {
        D3D12Log("ERROR: GetProcAddress for Direct3DCreate9On12Ex failed!");
    }

    if (g_pfnRealDirect3DCreate9Ex) {
        return g_pfnRealDirect3DCreate9Ex(SDKVersion, ppOutputInterface);
    }
    return E_FAIL;
}

// -------------------------------------------------------------------------------------------------
// IAT Hook for GetProcAddress in vid_d3d9_x64.dll
// -------------------------------------------------------------------------------------------------
typedef FARPROC (WINAPI *FnGetProcAddress)(HMODULE, LPCSTR);
static FnGetProcAddress g_pfnRealGetProcAddress = nullptr;

static FARPROC WINAPI Hook_GetProcAddress(HMODULE hModule, LPCSTR lpProcName) {
    if (lpProcName && !((ULONG_PTR)lpProcName >> 16)) {
        return g_pfnRealGetProcAddress(hModule, lpProcName);
    }
    if (lpProcName && strcmp(lpProcName, "Direct3DCreate9Ex") == 0) {
        D3D12Log("[D3D12_INTERCEPT] GetProcAddress(..., \"Direct3DCreate9Ex\") intercepted! Redirecting to Hook_Direct3DCreate9Ex.");
        if (g_pfnRealGetProcAddress) {
            g_pfnRealDirect3DCreate9Ex = (FnDirect3DCreate9Ex)g_pfnRealGetProcAddress(hModule, lpProcName);
        }
        return (FARPROC)Hook_Direct3DCreate9Ex;
    }
    if (lpProcName && strcmp(lpProcName, "Direct3DCreate9") == 0) {
        D3D12Log("[D3D12_INTERCEPT] GetProcAddress(..., \"Direct3DCreate9\") intercepted! Redirecting to Hook_Direct3DCreate9.");
        if (g_pfnRealGetProcAddress) {
            g_pfnRealDirect3DCreate9 = (FnDirect3DCreate9)g_pfnRealGetProcAddress(hModule, lpProcName);
        }
        return (FARPROC)Hook_Direct3DCreate9;
    }
    return g_pfnRealGetProcAddress(hModule, lpProcName);
}

// Robust, self-contained IAT hook
static bool HookIAT(HMODULE hModule, const char* targetDllName, const char* functionName, void* newFunction, void** oldFunction) {
    if (!hModule) return false;
    PBYTE pBase = (PBYTE)hModule;
    PIMAGE_DOS_HEADER pDos = (PIMAGE_DOS_HEADER)pBase;
    if (pDos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    PIMAGE_NT_HEADERS pNt = (PIMAGE_NT_HEADERS)(pBase + pDos->e_lfanew);
    if (pNt->Signature != IMAGE_NT_SIGNATURE) return false;

    IMAGE_DATA_DIRECTORY importDir = pNt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (importDir.VirtualAddress == 0 || importDir.Size == 0) return false;

    PIMAGE_IMPORT_DESCRIPTOR pImport = (PIMAGE_IMPORT_DESCRIPTOR)(pBase + importDir.VirtualAddress);
    for (; pImport->Name != 0; ++pImport) {
        const char* modName = (const char*)(pBase + pImport->Name);
        if (_stricmp(modName, targetDllName) == 0) {
            PIMAGE_THUNK_DATA pOriginalThunk = (PIMAGE_THUNK_DATA)(pBase + (pImport->OriginalFirstThunk ? pImport->OriginalFirstThunk : pImport->FirstThunk));
            PIMAGE_THUNK_DATA pThunk = (PIMAGE_THUNK_DATA)(pBase + pImport->FirstThunk);

            for (; pOriginalThunk->u1.AddressOfData != 0; ++pOriginalThunk, ++pThunk) {
                if (!(pOriginalThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG)) {
                    PIMAGE_IMPORT_BY_NAME pImportByName = (PIMAGE_IMPORT_BY_NAME)(pBase + pOriginalThunk->u1.AddressOfData);
                    if (strcmp((char*)pImportByName->Name, functionName) == 0) {
                        DWORD oldProtect = 0;
                        if (VirtualProtect(&pThunk->u1.Function, sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect)) {
                            if (oldFunction) *oldFunction = (void*)pThunk->u1.Function;
                            pThunk->u1.Function = (ULONGLONG)newFunction;
                            VirtualProtect(&pThunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
                            return true;
                        }
                    }
                }
            }
        }
    }
    return false;
}

// -------------------------------------------------------------------------------------------------
// Lifecycle Hooks
// -------------------------------------------------------------------------------------------------
static bool __fastcall Hook_Init(void* pParams) {
    D3D12Log(">>> Slot +0x020: Init(pParams=0x%p) CALLED", pParams);
    bool res = ((FnInit)g_realAPI.Init)(pParams);
    D3D12Log("<<< Slot +0x020: Init RETURNED %s", res ? "SUCCESS" : "FAILED");
    return res;
}

static bool __fastcall Hook_StartWindow() {
    D3D12Log(">>> Slot +0x028: StartWindow() CALLED");
    bool res = ((FnStartWindow)g_realAPI.StartWindow)();
    D3D12Log("<<< Slot +0x028: StartWindow RETURNED %s", res ? "SUCCESS" : "FAILED");

    if (res) {
        HWND hWnd = FindWindowA("K2_Direct3D", nullptr);
        if (!hWnd) hWnd = g_hGameWindow;
        if (!hWnd) hWnd = GetActiveWindow();
        if (hWnd) {
            g_hGameWindow = hWnd;
            ShowWindow(hWnd, SW_SHOW);
            SetForegroundWindow(hWnd);
            SetFocus(hWnd);
            UpdateWindow(hWnd);

            RECT rc = {0};
            GetClientRect(hWnd, &rc);
            D3D12Log("[D3D12_WINDOW] Game Window verified & active: HWND=0x%p (%ux%u)", hWnd, rc.right - rc.left, rc.bottom - rc.top);
        }
    }
    return res;
}

static int __fastcall Hook_SetMode() {
    D3D12Log(">>> Slot +0x030: SetMode() CALLED");
    int res = ((FnSetMode)g_realAPI.SetMode)();
    D3D12Log("<<< Slot +0x030: SetMode RETURNED modeIndex=%d", res);
    return res;
}

static void __fastcall Hook_Shutdown() {
    D3D12Log(">>> Slot +0x050: Shutdown() CALLED");
    D3D12Bridge::Get().Shutdown();
    ((FnShutdown)g_realAPI.Shutdown)();
    D3D12Log("<<< Slot +0x050: Shutdown COMPLETED");
}

static bool __fastcall Hook_BeginFrame() {
    uint64_t f = ++g_frameCount;
    D3D12Bridge::Get().OnBeginFrame(f);
    return ((FnBeginFrame)g_realAPI.BeginFrame)();
}

static void __fastcall Hook_EndFrame() {
    uint64_t f = g_frameCount.load();
    ((FnEndFrame)g_realAPI.EndFrame)();
    D3D12Bridge::Get().OnEndFrame(f);
}

static void __fastcall Hook_SetVSync(bool bEnable) {
    D3D12Log("Slot +0x1F0: SetVSync(%s)", bEnable ? "true" : "false");
    ((FnSetVSync)g_realAPI.SetVSync)(bEnable);
}

static void __fastcall Hook_SetMaxFPS(uint32_t fps) {
    D3D12Log("Slot +0x1F8: SetMaxFPS(%u)", fps);
    ((FnSetMaxFPS)g_realAPI.SetMaxFPS)(fps);
}

// -------------------------------------------------------------------------------------------------
// Primary Driver Entry Point: InitAPIs
// -------------------------------------------------------------------------------------------------
typedef void (__stdcall *PFN_InitAPIs)(SVidDriverAPI*, void*, void*);

extern "C" __declspec(dllexport) void __stdcall InitAPIs(SVidDriverAPI* pDriverAPI, void* pCoreEngine, void* pModManager) {
    D3D12Log("================================================================================");
    D3D12Log("K2 ENGINE INITIALIZING VIDEO DRIVER: vid_d3d12_x64 (DirectX 12 Bridge)");
    D3D12Log("pDriverAPI=0x%p, pCoreEngine=0x%p, pModManager=0x%p", pDriverAPI, pCoreEngine, pModManager);
    D3D12Log("================================================================================");

    // 1. Locate and load the stock D3D9 driver as pass-through backend
    if (!g_hRealD3D9) {
        wchar_t d3d9Path[MAX_PATH] = {0};
        HMODULE hSelf = GetModuleHandleW(L"vid_d3d12_x64.dll");
        if (hSelf) {
            GetModuleFileNameW(hSelf, d3d9Path, MAX_PATH);
            wchar_t* pSlash = wcsrchr(d3d9Path, L'\\');
            if (pSlash) {
                *(pSlash + 1) = L'\0';
                wcscat_s(d3d9Path, L"vid_d3d9_x64.dll");
            }
        }
        if (d3d9Path[0] == L'\0') {
            wcscpy_s(d3d9Path, L"vid_d3d9_x64.dll");
        }
        g_hRealD3D9 = LoadLibraryExW(d3d9Path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!g_hRealD3D9) {
            g_hRealD3D9 = LoadLibraryW(L"vid_d3d9_x64.dll");
        }
        if (!g_hRealD3D9) {
            D3D12Log("ERROR: Failed to load vid_d3d9_x64.dll! Error: %lu", GetLastError());
            return;
        }
        D3D12Log("Successfully loaded underlying driver: vid_d3d9_x64.dll (HMODULE: 0x%p)", g_hRealD3D9);
    }

    // 2. Perform IAT Hooking on vid_d3d9_x64.dll to redirect Direct3DCreate9Ex / Direct3DCreate9 -> Direct3DCreate9On12
    bool gpaHooked = HookIAT(g_hRealD3D9, "KERNEL32.dll", "GetProcAddress", (void*)Hook_GetProcAddress, (void**)&g_pfnRealGetProcAddress);
    if (gpaHooked) {
        D3D12Log("[D3D12_BRIDGE] SUCCESS: IAT hook installed for GetProcAddress in vid_d3d9_x64.dll!");
    } else {
        D3D12Log("[D3D12_BRIDGE] WARNING: HookIAT for GetProcAddress returned false.");
    }

    bool iatHooked = HookIAT(g_hRealD3D9, "d3d9.dll", "Direct3DCreate9", (void*)Hook_Direct3DCreate9, (void**)&g_pfnRealDirect3DCreate9);
    if (iatHooked) {
        D3D12Log("[D3D12_BRIDGE] SUCCESS: IAT hook installed for Direct3DCreate9 in vid_d3d9_x64.dll!");
    } else {
        D3D12Log("[D3D12_BRIDGE] WARNING: HookIAT returned false. Will attempt fallback routing.");
    }

    // 3. Resolve InitAPIs on the stock driver
    PFN_InitAPIs pfnRealInit = (PFN_InitAPIs)GetProcAddress(g_hRealD3D9, "InitAPIs");
    if (!pfnRealInit) {
        D3D12Log("ERROR: Failed to find InitAPIs export in vid_d3d9_x64.dll!");
        return;
    }

    // 4. Initialize the real driver directly into pDriverAPI so all 60 slots and strings are fully valid
    pfnRealInit(pDriverAPI, pCoreEngine, pModManager);
    D3D12Log("Real driver initialized directly into pDriverAPI.");

    // 5. Save stock function pointers to shadow table
    memcpy(&g_realAPI, pDriverAPI, sizeof(SVidDriverAPI));
    D3D12Log("Copied stock function pointers to shadow table.");

    // 6. Hook critical engine lifecycle functions
    if (g_realAPI.Init) pDriverAPI->Init = (void*)Hook_Init;
    if (g_realAPI.StartWindow) pDriverAPI->StartWindow = (void*)Hook_StartWindow;
    if (g_realAPI.SetMode) pDriverAPI->SetMode = (void*)Hook_SetMode;
    if (g_realAPI.Shutdown) pDriverAPI->Shutdown = (void*)Hook_Shutdown;
    if (g_realAPI.BeginFrame) pDriverAPI->BeginFrame = (void*)Hook_BeginFrame;
    if (g_realAPI.EndFrame) pDriverAPI->EndFrame = (void*)Hook_EndFrame;
    if (g_realAPI.SetVSync) pDriverAPI->SetVSync = (void*)Hook_SetVSync;
    if (g_realAPI.SetMaxFPS) pDriverAPI->SetMaxFPS = (void*)Hook_SetMaxFPS;

    D3D12Log("DirectX 12 Bridge Active: All rendering requests routed through Direct3D 12 Command Queues.");
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinstDLL);
        D3D12Log("vid_d3d12_x64.dll attached to process ID %lu", GetCurrentProcessId());
    } else if (fdwReason == DLL_PROCESS_DETACH) {
        D3D12Log("vid_d3d12_x64.dll detached from process");
        if (g_pLogFile) {
            fclose(g_pLogFile);
            g_pLogFile = nullptr;
        }
    }
    return TRUE;
}
