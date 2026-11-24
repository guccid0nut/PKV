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
#include <cstring>
#include <unordered_map>
#include <vector>
#include <algorithm>
#include <cstdarg>
#include <immintrin.h>
#include <intrin.h>
#include "../include/SVidDriverAPI.h"
#define KONGOR_TRACY_DEFINE_MARKER_EXPORT 1
#include "kongor_tracy.h"

static HMODULE g_hRealD3D9 = nullptr;
static SVidDriverAPI g_realAPI = {};
static FILE* g_pLogFile = nullptr;
static std::atomic<uint64_t> g_frameCount{0};
static constexpr size_t kMaxSpyLogBytes = 16ULL * 1024ULL * 1024ULL; // 16 MiB cap (#254)
static size_t g_logBytesWritten = 0;
static wchar_t g_logFilePath[MAX_PATH] = {0};
static wchar_t g_prevLogFilePath[MAX_PATH] = {0};

// -------------------------------------------------------------------------------------------------
// Exit-Safe Win32 Async Background Log Writer (Zero C++ static destructors, zero DllMain locks)
// -------------------------------------------------------------------------------------------------
static constexpr uint32_t kAsyncLogSlotCount = 1024; // Must be power of 2
static constexpr size_t kAsyncLogLineCapacity = 1024;
static constexpr DWORD kAsyncLogFlushIntervalMs = 250;
static constexpr DWORD kAsyncLogShutdownWaitMs = 250;

enum AsyncLogState : int {
    kAsyncLogUninitialized = 0,
    kAsyncLogRunning       = 1,
    kAsyncLogStopping      = 2,
    kAsyncLogStopped       = 3,
    kAsyncLogTimedOut      = 4
};

struct AsyncLogSlot {
    char     text[kAsyncLogLineCapacity];
    uint16_t length;
    bool     urgent;
};

static SRWLOCK g_asyncLogLock = SRWLOCK_INIT;
static AsyncLogSlot g_asyncLogRing[kAsyncLogSlotCount] = {};
static uint32_t g_asyncLogHead = 0;
static uint32_t g_asyncLogTail = 0;
static uint32_t g_asyncLogCount = 0;
static uint64_t g_asyncLogDropped = 0;
static uint64_t g_asyncLogDroppedReported = 0;
static HANDLE g_hLogWakeEvent = nullptr;
static HANDLE g_hLogWriterThread = nullptr;
static std::atomic<int> g_asyncLogState{kAsyncLogUninitialized};
static std::atomic<bool> g_asyncLogStopRequested{false};

// Owned exclusively by AsyncLogWriterThreadProc once running (never called from DllMain).
static void RotateAndOpenSpyLogWriterThread(bool rotateRegardlessOfSize) {
    if (g_pLogFile) {
        fflush(g_pLogFile);
        fclose(g_pLogFile);
        g_pLogFile = nullptr;
    }

    if (g_logFilePath[0] == L'\0') {
        wchar_t modulePath[MAX_PATH] = {0};
        HMODULE hSelf = GetModuleHandleW(L"vid_vulkan_x64.dll");
        if (!hSelf) hSelf = GetModuleHandleW(L"vulkan_x64.dll");
        if (hSelf && GetModuleFileNameW(hSelf, modulePath, MAX_PATH) > 0) {
            wchar_t* pSlash = wcsrchr(modulePath, L'\\');
            if (pSlash) {
                *(pSlash + 1) = L'\0';
                wcscpy_s(g_logFilePath, modulePath);
                wcscat_s(g_logFilePath, L"vid_vulkan_spy.log");
                wcscpy_s(g_prevLogFilePath, modulePath);
                wcscat_s(g_prevLogFilePath, L"vid_vulkan_spy.prev.log");
            }
        }
        if (g_logFilePath[0] == L'\0') {
            wcscpy_s(g_logFilePath, L"vid_vulkan_spy.log");
            wcscpy_s(g_prevLogFilePath, L"vid_vulkan_spy.prev.log");
        }
    }

    WIN32_FILE_ATTRIBUTE_DATA fad = {};
    if (GetFileAttributesExW(g_logFilePath, GetFileExInfoStandard, &fad)) {
        uint64_t existingBytes = ((uint64_t)fad.nFileSizeHigh << 32) | (uint64_t)fad.nFileSizeLow;
        if ((rotateRegardlessOfSize && existingBytes > 0) || existingBytes >= kMaxSpyLogBytes) {
            MoveFileExW(g_logFilePath, g_prevLogFilePath, MOVEFILE_REPLACE_EXISTING);
            existingBytes = 0;
        }
        g_logBytesWritten = (size_t)existingBytes;
    } else {
        g_logBytesWritten = 0;
    }

    g_pLogFile = _wfopen(g_logFilePath, L"a");
}

static DWORD WINAPI AsyncLogWriterThreadProc(LPVOID) {
    static constexpr uint32_t kDrainBatchSize = 16;
    AsyncLogSlot batch[kDrainBatchSize];
    bool dirty = false;
    ULONGLONG lastFlushMs = GetTickCount64();

    for (;;) {
        bool urgentSeen = false;

        for (;;) {
            uint32_t batchCount = 0;
            uint64_t droppedDelta = 0;

            AcquireSRWLockExclusive(&g_asyncLogLock);
            while (batchCount < kDrainBatchSize && g_asyncLogCount > 0) {
                batch[batchCount++] = g_asyncLogRing[g_asyncLogHead];
                g_asyncLogHead = (g_asyncLogHead + 1u) & (kAsyncLogSlotCount - 1u);
                --g_asyncLogCount;
            }
            if (g_asyncLogDropped > g_asyncLogDroppedReported) {
                droppedDelta = g_asyncLogDropped - g_asyncLogDroppedReported;
                g_asyncLogDroppedReported += droppedDelta;
            }
            ReleaseSRWLockExclusive(&g_asyncLogLock);

            if (batchCount == 0 && droppedDelta == 0) {
                break;
            }

            for (uint32_t i = 0; i < batchCount; ++i) {
                if (!g_pLogFile || g_logBytesWritten >= kMaxSpyLogBytes) {
                    RotateAndOpenSpyLogWriterThread(true);
                }
                if (g_pLogFile && batch[i].length > 0) {
                    size_t written = fwrite(batch[i].text, 1, batch[i].length, g_pLogFile);
                    g_logBytesWritten += written;
                    dirty = true;
                }
                if (batch[i].urgent) {
                    urgentSeen = true;
                }
            }

            if (droppedDelta > 0) {
                if (!g_pLogFile || g_logBytesWritten >= kMaxSpyLogBytes) {
                    RotateAndOpenSpyLogWriterThread(true);
                }
                if (g_pLogFile) {
                    SYSTEMTIME st;
                    GetLocalTime(&st);
                    int n = fprintf(g_pLogFile,
                        "[%02d:%02d:%02d.%03d] [VID_VULKAN_SPY] [ASYNC_LOG] WARNING: Dropped %llu log lines (bounded queue capacity=%u saturated)\n",
                        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                        (unsigned long long)droppedDelta, kAsyncLogSlotCount);
                    if (n > 0) {
                        g_logBytesWritten += (size_t)n;
                        dirty = true;
                    }
                }
            }
        }

        ULONGLONG nowMs = GetTickCount64();
        bool stopNow = g_asyncLogStopRequested.load(std::memory_order_acquire);

        if (g_pLogFile && dirty && (urgentSeen || stopNow || (nowMs - lastFlushMs) >= kAsyncLogFlushIntervalMs)) {
            fflush(g_pLogFile);
            dirty = false;
            lastFlushMs = nowMs;
        }

        if (stopNow) {
            AcquireSRWLockExclusive(&g_asyncLogLock);
            uint32_t remaining = g_asyncLogCount;
            ReleaseSRWLockExclusive(&g_asyncLogLock);
            if (remaining == 0) {
                break;
            }
            continue;
        }

        DWORD waitMs = kAsyncLogFlushIntervalMs;
        if (dirty) {
            ULONGLONG elapsed = nowMs - lastFlushMs;
            waitMs = (elapsed >= kAsyncLogFlushIntervalMs) ? 0u : (DWORD)(kAsyncLogFlushIntervalMs - elapsed);
        }
        if (g_hLogWakeEvent) {
            WaitForSingleObject(g_hLogWakeEvent, waitMs);
        } else {
            ::Sleep(waitMs > 0 ? waitMs : 1);
        }
    }

    if (g_pLogFile) {
        fflush(g_pLogFile);
        fclose(g_pLogFile);
        g_pLogFile = nullptr;
    }
    return 0;
}

static void StartAsyncLogWriter() {
    int expected = kAsyncLogUninitialized;
    if (!g_asyncLogState.compare_exchange_strong(expected, kAsyncLogRunning, std::memory_order_acq_rel)) {
        if (expected == kAsyncLogStopped) {
            if (!g_asyncLogState.compare_exchange_strong(expected, kAsyncLogRunning, std::memory_order_acq_rel)) {
                return;
            }
        } else {
            return;
        }
    }

    g_asyncLogStopRequested.store(false, std::memory_order_release);
    if (!g_hLogWakeEvent) {
        // Initially signaled so queued DLL_PROCESS_ATTACH lines drain immediately.
        g_hLogWakeEvent = CreateEventW(nullptr, FALSE, TRUE, nullptr);
    }
    if (!g_hLogWriterThread) {
        g_hLogWriterThread = CreateThread(nullptr, 0, AsyncLogWriterThreadProc, nullptr, 0, nullptr);
        if (!g_hLogWriterThread) {
            OutputDebugStringA("[VID_VULKAN_SPY] [ERROR] Failed to create async log writer thread.\n");
            g_asyncLogState.store(kAsyncLogStopped, std::memory_order_release);
        }
    }
}

void Log(const char* format, ...);

static void ShutdownAsyncLogWriter() {
    int prevState = g_asyncLogState.load(std::memory_order_acquire);
    if (prevState != kAsyncLogRunning) {
        return;
    }

    uint32_t queuedSnapshot = 0;
    uint64_t droppedSnapshot = 0;
    AcquireSRWLockExclusive(&g_asyncLogLock);
    queuedSnapshot = g_asyncLogCount;
    droppedSnapshot = g_asyncLogDropped;
    ReleaseSRWLockExclusive(&g_asyncLogLock);

    Log("[SHUTDOWN] Async log writer draining (queued=%u, dropped=%llu, timeout=%lums)",
        queuedSnapshot, (unsigned long long)droppedSnapshot, (unsigned long)kAsyncLogShutdownWaitMs);

    g_asyncLogState.store(kAsyncLogStopping, std::memory_order_release);
    g_asyncLogStopRequested.store(true, std::memory_order_release);
    AcquireSRWLockExclusive(&g_asyncLogLock);
    if (g_hLogWakeEvent) {
        SetEvent(g_hLogWakeEvent);
    }
    ReleaseSRWLockExclusive(&g_asyncLogLock);

    HANDLE hThread = g_hLogWriterThread;
    if (hThread) {
        DWORD waitRes = WaitForSingleObject(hThread, kAsyncLogShutdownWaitMs);
        if (waitRes == WAIT_OBJECT_0) {
            CloseHandle(hThread);
            g_hLogWriterThread = nullptr;
            g_asyncLogState.store(kAsyncLogStopped, std::memory_order_release);
            HANDLE hEvToClose = nullptr;
            AcquireSRWLockExclusive(&g_asyncLogLock);
            hEvToClose = g_hLogWakeEvent;
            g_hLogWakeEvent = nullptr;
            ReleaseSRWLockExclusive(&g_asyncLogLock);
            if (hEvToClose) {
                CloseHandle(hEvToClose);
            }
        } else {
            // Bounded timeout expired: leave thread/event/file untouched to avoid race or TerminateThread hazard.
            g_asyncLogState.store(kAsyncLogTimedOut, std::memory_order_release);
        }
    } else {
        g_asyncLogState.store(kAsyncLogStopped, std::memory_order_release);
    }
}

// Non-blocking async logger to vid_vulkan_spy.log with startup & 16 MiB rotation (#254)
void Log(const char* format, ...) {
    if (!format) return;

    int state = g_asyncLogState.load(std::memory_order_acquire);
    if (state == kAsyncLogStopped || state == kAsyncLogTimedOut) {
        return;
    }

    char lineBuf[kAsyncLogLineCapacity];
    SYSTEMTIME st;
    GetLocalTime(&st);
    int prefixLen = snprintf(lineBuf, sizeof(lineBuf), "[%02d:%02d:%02d.%03d] [VID_VULKAN_SPY] ",
                             st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    if (prefixLen < 0) prefixLen = 0;
    if ((size_t)prefixLen >= sizeof(lineBuf) - 2) {
        prefixLen = (int)(sizeof(lineBuf) - 2);
    }

    va_list args;
    va_start(args, format);
    size_t maxBody = sizeof(lineBuf) - (size_t)prefixLen - 2;
    int bodyLen = vsnprintf(lineBuf + prefixLen, maxBody + 1, format, args);
    va_end(args);

    size_t actualBody = 0;
    if (bodyLen > 0) {
        actualBody = ((size_t)bodyLen > maxBody) ? maxBody : (size_t)bodyLen;
    }
    size_t totalLen = (size_t)prefixLen + actualBody;
    lineBuf[totalLen++] = '\n';
    lineBuf[totalLen] = '\0';

    bool isUrgent = (strstr(lineBuf, "[ERROR]") != nullptr) ||
                    (strstr(lineBuf, "[FATAL]") != nullptr) ||
                    (strstr(lineBuf, "[SHUTDOWN]") != nullptr) ||
                    (strstr(lineBuf, "ERROR:") != nullptr);

    AcquireSRWLockExclusive(&g_asyncLogLock);
    if (g_asyncLogCount >= kAsyncLogSlotCount) {
        if (isUrgent) {
            // Evict oldest entry so urgent error/fatal/shutdown diagnostics are never lost.
            g_asyncLogHead = (g_asyncLogHead + 1u) & (kAsyncLogSlotCount - 1u);
            --g_asyncLogCount;
            ++g_asyncLogDropped;
        } else {
            ++g_asyncLogDropped;
            ReleaseSRWLockExclusive(&g_asyncLogLock);
            return;
        }
    }

    AsyncLogSlot& slot = g_asyncLogRing[g_asyncLogTail];
    memcpy(slot.text, lineBuf, totalLen);
    slot.length = (uint16_t)totalLen;
    slot.urgent = isUrgent;
    g_asyncLogTail = (g_asyncLogTail + 1u) & (kAsyncLogSlotCount - 1u);
    ++g_asyncLogCount;
    bool shouldWake = isUrgent || (g_asyncLogCount >= (kAsyncLogSlotCount / 4u));
    if (shouldWake && g_hLogWakeEvent) {
        SetEvent(g_hLogWakeEvent);
    }
    ReleaseSRWLockExclusive(&g_asyncLogLock);
}

// External logger for VulkanContext
void VulkanLog(const char* format, ...) {
    va_list args;
    va_start(args, format);
    char buf[1024];
    vsnprintf(buf, sizeof(buf), format, args);
    va_end(args);
    Log("%s", buf);
}

#include <d3d9.h>
#include "vulkan_context.h"
#include "vulkan_2d.h"
#include "vulkan_3d.h"
#include "vulkan_devtools.h"
#include <psapi.h>

// -------------------------------------------------------------------------------------------------
// Window Procedure Subclass for In-Engine ImGui DevTools (Ctrl+Shift+D / Ctrl+F12) Interception
// -------------------------------------------------------------------------------------------------
static WNDPROC g_pfnDevToolsOriginalWndProc = nullptr;
static bool g_bIsUnicodeWndProc = true;

static LRESULT CALLBACK DevTools_WndProcHook(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    DWORD windowThread = GetWindowThreadProcessId(hWnd, nullptr);
    DWORD currentThread = GetCurrentThreadId();
    if (windowThread != 0 && currentThread != windowThread) {
        static std::atomic<bool> s_loggedOffThreadDevTools{false};
        if (!s_loggedOffThreadDevTools.exchange(true, std::memory_order_relaxed)) {
            char buf[256];
            snprintf(buf, sizeof(buf), "[DevTools_WndProcHook] Off-thread WndProc call bypassed (CurrentTID=%u, WindowTID=%u, uMsg=0x%04X)\n",
                     currentThread, windowThread, uMsg);
            OutputDebugStringA(buf);
            Log("[DevTools_WndProcHook] Off-thread WndProc call bypassed (CurrentTID=%u, WindowTID=%u, uMsg=0x%04X)",
                currentThread, windowThread, uMsg);
        }
        if (g_pfnDevToolsOriginalWndProc) {
            return g_bIsUnicodeWndProc
                ? CallWindowProcW(g_pfnDevToolsOriginalWndProc, hWnd, uMsg, wParam, lParam)
                : CallWindowProcA(g_pfnDevToolsOriginalWndProc, hWnd, uMsg, wParam, lParam);
        }
        return g_bIsUnicodeWndProc
            ? DefWindowProcW(hWnd, uMsg, wParam, lParam)
            : DefWindowProcA(hWnd, uMsg, wParam, lParam);
    }
    if (VulkanDevTools::Get().HandleMessage(hWnd, uMsg, wParam, lParam)) {
        return 0; // Consumed by DevTools
    }
    if (g_pfnDevToolsOriginalWndProc) {
        return g_bIsUnicodeWndProc
            ? CallWindowProcW(g_pfnDevToolsOriginalWndProc, hWnd, uMsg, wParam, lParam)
            : CallWindowProcA(g_pfnDevToolsOriginalWndProc, hWnd, uMsg, wParam, lParam);
    }
    return g_bIsUnicodeWndProc
        ? DefWindowProcW(hWnd, uMsg, wParam, lParam)
        : DefWindowProcA(hWnd, uMsg, wParam, lParam);
}


// -------------------------------------------------------------------------------------------------
// Robust self-contained IAT Hook for intercepting D3D9 creation in vid_d3d9_x64.dll
// -------------------------------------------------------------------------------------------------
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
                        if (VirtualProtect(&pThunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect)) {
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
// Phase 4: Direct3D 9 COM Device VTable Interception for 3D Draw Calls
// -------------------------------------------------------------------------------------------------
typedef HRESULT (STDMETHODCALLTYPE *FnCreateDevice)(
    IDirect3D9* This, UINT Adapter, D3DDEVTYPE DeviceType, HWND hFocusWindow,
    DWORD BehaviorFlags, D3DPRESENT_PARAMETERS* pPresentationParameters,
    IDirect3DDevice9** ppReturnedDeviceInterface
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateDeviceEx)(
    IDirect3D9Ex* This, UINT Adapter, D3DDEVTYPE DeviceType, HWND hFocusWindow,
    DWORD BehaviorFlags, D3DPRESENT_PARAMETERS* pPresentationParameters,
    D3DDISPLAYMODEEX* pFullscreenDisplayMode, IDirect3DDevice9Ex** ppReturnedDeviceInterface
);

typedef HRESULT (STDMETHODCALLTYPE *FnDrawIndexedPrimitive)(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE Type, INT BaseVertexIndex,
    UINT MinVertexIndex, UINT NumVertices, UINT StartIndex, UINT PrimitiveCount
);

typedef HRESULT (STDMETHODCALLTYPE *FnDrawPrimitive)(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE PrimitiveType, UINT StartVertex, UINT PrimitiveCount
);

typedef HRESULT (STDMETHODCALLTYPE *FnDrawIndexedPrimitiveUP)(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE PrimitiveType, UINT MinVertexIndex,
    UINT NumVertices, UINT PrimitiveCount, CONST void* pIndexData,
    D3DFORMAT IndexDataFormat, CONST void* pVertexStreamZeroData, UINT VertexStreamZeroStride
);

typedef HRESULT (STDMETHODCALLTYPE *FnDrawPrimitiveUP)(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE PrimitiveType, UINT PrimitiveCount,
    CONST void* pVertexStreamZeroData, UINT VertexStreamZeroStride
);

typedef HRESULT (STDMETHODCALLTYPE *FnSetStreamSource)(
    IDirect3DDevice9* This, UINT StreamNumber, IDirect3DVertexBuffer9* pStreamData,
    UINT OffsetInBytes, UINT Stride
);

typedef HRESULT (STDMETHODCALLTYPE *FnSetIndices)(
    IDirect3DDevice9* This, IDirect3DIndexBuffer9* pIndexData
);

typedef HRESULT (STDMETHODCALLTYPE *FnDeviceReset)(
    IDirect3DDevice9* This, D3DPRESENT_PARAMETERS* pPresentationParameters
);

typedef HRESULT (STDMETHODCALLTYPE *FnDeviceResetEx)(
    IDirect3DDevice9Ex* This, D3DPRESENT_PARAMETERS* pPresentationParameters, D3DDISPLAYMODEEX* pFullscreenDisplayMode
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateTexture)(
    IDirect3DDevice9* This, UINT Width, UINT Height, UINT Levels, DWORD Usage,
    D3DFORMAT Format, D3DPOOL Pool, IDirect3DTexture9** ppTexture, HANDLE* pSharedHandle
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateRenderTarget)(
    IDirect3DDevice9* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Lockable,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateDepthStencilSurface)(
    IDirect3DDevice9* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Discard,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateRenderTargetEx)(
    IDirect3DDevice9Ex* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Lockable,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle, DWORD Usage
);

typedef HRESULT (STDMETHODCALLTYPE *FnCreateDepthStencilSurfaceEx)(
    IDirect3DDevice9Ex* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Discard,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle, DWORD Usage
);

static FnCreateDevice   g_pfnRealCreateDevice = nullptr;
static FnCreateDeviceEx g_pfnRealCreateDeviceEx = nullptr;
static void*            g_d3d9ProxyVTable[17] = {0};
static void*            g_d3d9ExProxyVTable[21] = {0};

// [L2-P1-02] Pure-C SEH helpers for COM vtable & MSVC x64 RTTI probing (zero C++ destructors inside __try)
static bool IsPotentialVtableSeh(void* const* vtbl, size_t minSlots) {
    if (!vtbl || minSlots == 0) return false;
    const uintptr_t addr = reinterpret_cast<uintptr_t>(vtbl);
    if ((addr & (sizeof(void*) - 1)) != 0 || addr < 0x10000ULL || addr > 0x00007FFFFFFFFFFFULL) {
        return false;
    }
    __try {
        for (size_t i = 0; i < minSlots; ++i) {
            const uintptr_t fn = reinterpret_cast<uintptr_t>(vtbl[i]);
            if (fn < 0x10000ULL || fn > 0x00007FFFFFFFFFFFULL) {
                return false;
            }
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

#pragma pack(push, 4)
struct KongorRttiCompleteObjectLocator64 {
    uint32_t signature;
    uint32_t offset;
    uint32_t cdOffset;
    int32_t  pTypeDescriptorRva;
    int32_t  pClassDescriptorRva;
    int32_t  pSelfRva;
};
#pragma pack(pop)

static bool TryGetRttiNameSeh(const void* pObj, char* outName, size_t outCap) {
    if (!outName || outCap == 0) return false;
    outName[0] = '\0';
    if (!pObj || reinterpret_cast<uintptr_t>(pObj) < 0x10000ULL) return false;
    __try {
        void* const* vtbl = *reinterpret_cast<void* const* const*>(pObj);
        if (!IsPotentialVtableSeh(vtbl, 1)) return false;
        const auto* col = reinterpret_cast<const KongorRttiCompleteObjectLocator64*>(vtbl[-1]);
        if (!col || reinterpret_cast<uintptr_t>(col) < 0x10000ULL) return false;
        if (col->signature != 1 || col->pSelfRva <= 0 || col->pTypeDescriptorRva <= 0) return false;
        const uintptr_t imageBase = reinterpret_cast<uintptr_t>(col) - static_cast<uintptr_t>(col->pSelfRva);
        const char* tdName = reinterpret_cast<const char*>(imageBase + static_cast<uintptr_t>(col->pTypeDescriptorRva) + 0x10);
        size_t i = 0;
        for (; i + 1 < outCap && i < 128; ++i) {
            char c = tdName[i];
            if (c == '\0') break;
            if (static_cast<unsigned char>(c) < 0x20 || static_cast<unsigned char>(c) > 0x7E) return false;
            outName[i] = c;
        }
        outName[i] = '\0';
        return (i > 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        outName[0] = '\0';
        return false;
    }
}

static FnDrawIndexedPrimitive   g_pfnRealDrawIndexedPrimitive = nullptr;
static FnDrawPrimitive          g_pfnRealDrawPrimitive = nullptr;
static FnDrawIndexedPrimitiveUP g_pfnRealDrawIndexedPrimitiveUP = nullptr;
static FnDrawPrimitiveUP        g_pfnRealDrawPrimitiveUP = nullptr;
static FnSetStreamSource        g_pfnRealSetStreamSource = nullptr;
static FnSetIndices             g_pfnRealSetIndices = nullptr;
static FnDeviceReset                  g_pfnRealDeviceReset = nullptr;
static FnDeviceResetEx                g_pfnRealDeviceResetEx = nullptr;
static FnCreateTexture                g_pfnRealCreateTexture = nullptr;
static FnCreateRenderTarget           g_pfnRealCreateRenderTarget = nullptr;
static FnCreateDepthStencilSurface    g_pfnRealCreateDepthStencilSurface = nullptr;
static FnCreateRenderTargetEx         g_pfnRealCreateRenderTargetEx = nullptr;
static FnCreateDepthStencilSurfaceEx  g_pfnRealCreateDepthStencilSurfaceEx = nullptr;
// IDirect3DDevice9 has 119 slots (0..118); IDirect3DDevice9Ex has 134 slots (0..133).
static void*                    g_d3d9DeviceProxyVTable[140] = {0};
static IDirect3DDevice9*        g_pActiveD3DDevice = nullptr;
static bool                     g_bForceSystemD3D9Fallback = false;
static bool                     g_bFallbackDecisionEvaluated = false;
static HMODULE                  g_hSystemD3D9 = nullptr;
static char                     g_lastFallbackReason[1024] = {0};

static HWND FindCurrentProcessK2Window();

static void PrepareBorderlessPopupWindowForDxvk(HWND hTargetWnd) noexcept {
    HWND hWnd = (hTargetWnd && IsWindow(hTargetWnd)) ? hTargetWnd : FindCurrentProcessK2Window();
    if (!hWnd || !IsWindow(hWnd) || IsIconic(hWnd)) return;

    HMONITOR hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
    if (!hMon) return;
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    if (!GetMonitorInfoA(hMon, &mi)) return;

    LONG_PTR curStyle = GetWindowLongPtrA(hWnd, GWL_STYLE);
    LONG_PTR desiredStyle = (LONG_PTR)(WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
    if ((curStyle & (WS_CAPTION | WS_THICKFRAME)) != 0 || (curStyle & WS_POPUP) == 0) {
        SetWindowLongPtrA(hWnd, GWL_STYLE, desiredStyle);
    }

    int monW = mi.rcMonitor.right - mi.rcMonitor.left;
    int monH = mi.rcMonitor.bottom - mi.rcMonitor.top;
    SetWindowPos(hWnd, nullptr,
                 mi.rcMonitor.left, mi.rcMonitor.top, monW, monH,
                 SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
}

static IDirect3DVertexBuffer9* g_currentStreamSource = nullptr;
static UINT g_currentStreamOffset = 0;
static UINT g_currentStreamStride = 0;
static IDirect3DIndexBuffer9* g_currentIndexBuffer = nullptr;

static void ClearCachedD3D9StreamBindings() noexcept {
    g_currentStreamSource = nullptr;
    g_currentStreamOffset = 0;
    g_currentStreamStride = 0;
    g_currentIndexBuffer  = nullptr;
}

static std::atomic<uint64_t> g_d3d9DrawCallsFrame{0};

static HRESULT STDMETHODCALLTYPE Hook_DrawIndexedPrimitive(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE Type, INT BaseVertexIndex,
    UINT MinVertexIndex, UINT NumVertices, UINT StartIndex, UINT PrimitiveCount
) {
    g_d3d9DrawCallsFrame.fetch_add(1, std::memory_order_relaxed);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().RecordDrawIndexed((uint32_t)Type, BaseVertexIndex, MinVertexIndex,
                                         NumVertices, StartIndex, PrimitiveCount,
                                         g_currentStreamSource, g_currentStreamOffset,
                                         g_currentStreamStride, g_currentIndexBuffer);
    }
    return g_pfnRealDrawIndexedPrimitive(This, Type, BaseVertexIndex, MinVertexIndex,
                                        NumVertices, StartIndex, PrimitiveCount);
}

static HRESULT STDMETHODCALLTYPE Hook_DrawPrimitive(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE PrimitiveType, UINT StartVertex, UINT PrimitiveCount
) {
    g_d3d9DrawCallsFrame.fetch_add(1, std::memory_order_relaxed);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().RecordDrawPrimitive((uint32_t)PrimitiveType, StartVertex, PrimitiveCount,
                                           g_currentStreamSource, g_currentStreamOffset,
                                           g_currentStreamStride);
    }
    return g_pfnRealDrawPrimitive(This, PrimitiveType, StartVertex, PrimitiveCount);
}

static HRESULT STDMETHODCALLTYPE Hook_DrawIndexedPrimitiveUP(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE PrimitiveType, UINT MinVertexIndex,
    UINT NumVertices, UINT PrimitiveCount, CONST void* pIndexData,
    D3DFORMAT IndexDataFormat, CONST void* pVertexStreamZeroData, UINT VertexStreamZeroStride
) {
    g_d3d9DrawCallsFrame.fetch_add(1, std::memory_order_relaxed);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().RecordDrawIndexedUP((uint32_t)PrimitiveType, MinVertexIndex, NumVertices,
                                           PrimitiveCount, pIndexData, (uint32_t)IndexDataFormat,
                                           pVertexStreamZeroData, VertexStreamZeroStride);
    }
    return g_pfnRealDrawIndexedPrimitiveUP(This, PrimitiveType, MinVertexIndex,
                                          NumVertices, PrimitiveCount, pIndexData,
                                          IndexDataFormat, pVertexStreamZeroData,
                                          VertexStreamZeroStride);
}

static HRESULT STDMETHODCALLTYPE Hook_DrawPrimitiveUP(
    IDirect3DDevice9* This, D3DPRIMITIVETYPE PrimitiveType, UINT PrimitiveCount,
    CONST void* pVertexStreamZeroData, UINT VertexStreamZeroStride
) {
    g_d3d9DrawCallsFrame.fetch_add(1, std::memory_order_relaxed);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().RecordDrawPrimitiveUP((uint32_t)PrimitiveType, PrimitiveCount,
                                             pVertexStreamZeroData, VertexStreamZeroStride);
    }
    return g_pfnRealDrawPrimitiveUP(This, PrimitiveType, PrimitiveCount,
                                   pVertexStreamZeroData, VertexStreamZeroStride);
}

static HRESULT STDMETHODCALLTYPE Hook_SetStreamSource(
    IDirect3DDevice9* This, UINT StreamNumber, IDirect3DVertexBuffer9* pStreamData,
    UINT OffsetInBytes, UINT Stride
) {
    if (StreamNumber == 0) {
        g_currentStreamSource = pStreamData;
        g_currentStreamOffset = OffsetInBytes;
        g_currentStreamStride = Stride;
    }
    return g_pfnRealSetStreamSource(This, StreamNumber, pStreamData, OffsetInBytes, Stride);
}

static HRESULT STDMETHODCALLTYPE Hook_SetIndices(
    IDirect3DDevice9* This, IDirect3DIndexBuffer9* pIndexData
) {
    g_currentIndexBuffer = pIndexData;
    return g_pfnRealSetIndices(This, pIndexData);
}

static HRESULT STDMETHODCALLTYPE Hook_DeviceReset(
    IDirect3DDevice9* This, D3DPRESENT_PARAMETERS* pPresentationParameters
) {
    ClearCachedD3D9StreamBindings();
    D3DPRESENT_PARAMETERS sanitized = {};
    D3DPRESENT_PARAMETERS* pEffective = pPresentationParameters;
    if (!g_bForceSystemD3D9Fallback && pPresentationParameters) {
        sanitized = *pPresentationParameters;
        if (!sanitized.Windowed) {
            PrepareBorderlessPopupWindowForDxvk(sanitized.hDeviceWindow);
            sanitized.Windowed = TRUE;
            sanitized.FullScreen_RefreshRateInHz = 0;
            Log("[VULKAN_3D] Sanitized Reset Windowed=FALSE -> TRUE (DirectFlip borderless popup preserved under DXVK)");
        } else if (sanitized.FullScreen_RefreshRateInHz != 0) {
            sanitized.FullScreen_RefreshRateInHz = 0;
        }
        pEffective = &sanitized;
    }
    UINT bbW = pEffective ? pEffective->BackBufferWidth : 0;
    UINT bbH = pEffective ? pEffective->BackBufferHeight : 0;
    BOOL win = pEffective ? pEffective->Windowed : TRUE;
    UINT rate = pEffective ? pEffective->FullScreen_RefreshRateInHz : 0;
    Log("[VULKAN_3D] >>> IDirect3DDevice9::Reset(%ux%u, Windowed=%d, Refresh=%uHz, NativeFallback=%d)",
        bbW, bbH, (int)win, rate, g_bForceSystemD3D9Fallback ? 1 : 0);
    uint64_t t0 = GetTickCount64();
    HRESULT hr = g_pfnRealDeviceReset ? g_pfnRealDeviceReset(This, pEffective) : D3DERR_INVALIDCALL;
    uint64_t dtMs = GetTickCount64() - t0;
    ClearCachedD3D9StreamBindings();
    Log("[VULKAN_3D] <<< IDirect3DDevice9::Reset returned hr=0x%08X in %llums", (unsigned int)hr, (unsigned long long)dtMs);
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hook_DeviceResetEx(
    IDirect3DDevice9Ex* This, D3DPRESENT_PARAMETERS* pPresentationParameters, D3DDISPLAYMODEEX* pFullscreenDisplayMode
) {
    ClearCachedD3D9StreamBindings();
    D3DPRESENT_PARAMETERS sanitized = {};
    D3DPRESENT_PARAMETERS* pEffective = pPresentationParameters;
    D3DDISPLAYMODEEX* pEffectiveMode = pFullscreenDisplayMode;
    if (!g_bForceSystemD3D9Fallback && pPresentationParameters) {
        sanitized = *pPresentationParameters;
        if (!sanitized.Windowed || pFullscreenDisplayMode != nullptr) {
            PrepareBorderlessPopupWindowForDxvk(sanitized.hDeviceWindow);
            sanitized.Windowed = TRUE;
            sanitized.FullScreen_RefreshRateInHz = 0;
            pEffectiveMode = nullptr;
            Log("[VULKAN_3D] Sanitized ResetEx exclusive fullscreen -> Windowed=TRUE, pModeEx=NULL under DXVK");
        }
        pEffective = &sanitized;
    }
    UINT bbW = pEffective ? pEffective->BackBufferWidth : 0;
    UINT bbH = pEffective ? pEffective->BackBufferHeight : 0;
    BOOL win = pEffective ? pEffective->Windowed : TRUE;
    UINT rate = pEffectiveMode ? pEffectiveMode->RefreshRate :
                (pEffective ? pEffective->FullScreen_RefreshRateInHz : 0);
    Log("[VULKAN_3D] >>> IDirect3DDevice9Ex::ResetEx(%ux%u, Windowed=%d, Refresh=%uHz, pModeEx=0x%p, NativeFallback=%d)",
        bbW, bbH, (int)win, rate, pEffectiveMode, g_bForceSystemD3D9Fallback ? 1 : 0);
    uint64_t t0 = GetTickCount64();
    HRESULT hr = g_pfnRealDeviceResetEx ? g_pfnRealDeviceResetEx(This, pEffective, pEffectiveMode) : D3DERR_INVALIDCALL;
    uint64_t dtMs = GetTickCount64() - t0;
    ClearCachedD3D9StreamBindings();
    Log("[VULKAN_3D] <<< IDirect3DDevice9Ex::ResetEx returned hr=0x%08X in %llums", (unsigned int)hr, (unsigned long long)dtMs);
    return hr;
}

static void FormatCallerModuleOffset(const void* retAddr, char* outBuf, size_t outCap) noexcept {
    if (!outBuf || outCap == 0) return;
    outBuf[0] = '\0';
    HMODULE hMod = nullptr;
    if (retAddr && GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(retAddr), &hMod) && hMod) {
        char modPath[MAX_PATH] = {0};
        if (GetModuleFileNameA(hMod, modPath, MAX_PATH) > 0) {
            const char* baseName = strrchr(modPath, '\\');
            baseName = baseName ? (baseName + 1) : modPath;
            const uintptr_t rva = reinterpret_cast<uintptr_t>(retAddr) - reinterpret_cast<uintptr_t>(hMod);
            snprintf(outBuf, outCap, "%s+0x%llX", baseName, static_cast<unsigned long long>(rva));
            return;
        }
    }
    snprintf(outBuf, outCap, "0x%p", retAddr);
}

// Clamps 0-width / 0-height render-target and depth-stencil allocations (e.g. 1px UI <modelpanel>
// triggering vid_d3d9_x64.dll CWaterDistortionMap::Init "$waterdistortion" 317x0 -> neon-magenta water)
// to >= 1px so D3D9/DXVK succeeds and CWaterDistortionMap stays enabled for the next world camera pass.
static HRESULT STDMETHODCALLTYPE Hook_CreateTexture(
    IDirect3DDevice9* This, UINT Width, UINT Height, UINT Levels, DWORD Usage,
    D3DFORMAT Format, D3DPOOL Pool, IDirect3DTexture9** ppTexture, HANDLE* pSharedHandle
) {
    UINT effW = Width;
    UINT effH = Height;
    if ((Usage & (D3DUSAGE_RENDERTARGET | D3DUSAGE_DEPTHSTENCIL)) != 0 && (effW == 0 || effH == 0)) {
        effW = (effW == 0) ? 1u : effW;
        effH = (effH == 0) ? 1u : effH;
        char callerBuf[96] = {0};
        FormatCallerModuleOffset(_ReturnAddress(), callerBuf, sizeof(callerBuf));
        Log("[VULKAN_3D] Sanitized 0-dimension RenderTarget CreateTexture(%ux%u -> %ux%u, Usage=0x%lX, Format=%d, Caller=%s)",
            Width, Height, effW, effH, Usage, (int)Format, callerBuf);
    }
    return g_pfnRealCreateTexture
        ? g_pfnRealCreateTexture(This, effW, effH, Levels, Usage, Format, Pool, ppTexture, pSharedHandle)
        : D3DERR_INVALIDCALL;
}

static HRESULT STDMETHODCALLTYPE Hook_CreateRenderTarget(
    IDirect3DDevice9* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Lockable,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle
) {
    UINT effW = (Width == 0) ? 1u : Width;
    UINT effH = (Height == 0) ? 1u : Height;
    if (effW != Width || effH != Height) {
        char callerBuf[96] = {0};
        FormatCallerModuleOffset(_ReturnAddress(), callerBuf, sizeof(callerBuf));
        Log("[VULKAN_3D] Sanitized 0-dimension CreateRenderTarget(%ux%u -> %ux%u, Format=%d, Caller=%s)",
            Width, Height, effW, effH, (int)Format, callerBuf);
    }
    return g_pfnRealCreateRenderTarget
        ? g_pfnRealCreateRenderTarget(This, effW, effH, Format, MultiSample, MultisampleQuality, Lockable, ppSurface, pSharedHandle)
        : D3DERR_INVALIDCALL;
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDepthStencilSurface(
    IDirect3DDevice9* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Discard,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle
) {
    UINT effW = (Width == 0) ? 1u : Width;
    UINT effH = (Height == 0) ? 1u : Height;
    if (effW != Width || effH != Height) {
        char callerBuf[96] = {0};
        FormatCallerModuleOffset(_ReturnAddress(), callerBuf, sizeof(callerBuf));
        Log("[VULKAN_3D] Sanitized 0-dimension CreateDepthStencilSurface(%ux%u -> %ux%u, Format=%d, Caller=%s)",
            Width, Height, effW, effH, (int)Format, callerBuf);
    }
    return g_pfnRealCreateDepthStencilSurface
        ? g_pfnRealCreateDepthStencilSurface(This, effW, effH, Format, MultiSample, MultisampleQuality, Discard, ppSurface, pSharedHandle)
        : D3DERR_INVALIDCALL;
}

static HRESULT STDMETHODCALLTYPE Hook_CreateRenderTargetEx(
    IDirect3DDevice9Ex* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Lockable,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle, DWORD Usage
) {
    UINT effW = (Width == 0) ? 1u : Width;
    UINT effH = (Height == 0) ? 1u : Height;
    if (effW != Width || effH != Height) {
        char callerBuf[96] = {0};
        FormatCallerModuleOffset(_ReturnAddress(), callerBuf, sizeof(callerBuf));
        Log("[VULKAN_3D] Sanitized 0-dimension CreateRenderTargetEx(%ux%u -> %ux%u, Usage=0x%lX, Format=%d, Caller=%s)",
            Width, Height, effW, effH, Usage, (int)Format, callerBuf);
    }
    return g_pfnRealCreateRenderTargetEx
        ? g_pfnRealCreateRenderTargetEx(This, effW, effH, Format, MultiSample, MultisampleQuality, Lockable, ppSurface, pSharedHandle, Usage)
        : D3DERR_INVALIDCALL;
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDepthStencilSurfaceEx(
    IDirect3DDevice9Ex* This, UINT Width, UINT Height, D3DFORMAT Format,
    D3DMULTISAMPLE_TYPE MultiSample, DWORD MultisampleQuality, BOOL Discard,
    IDirect3DSurface9** ppSurface, HANDLE* pSharedHandle, DWORD Usage
) {
    UINT effW = (Width == 0) ? 1u : Width;
    UINT effH = (Height == 0) ? 1u : Height;
    if (effW != Width || effH != Height) {
        char callerBuf[96] = {0};
        FormatCallerModuleOffset(_ReturnAddress(), callerBuf, sizeof(callerBuf));
        Log("[VULKAN_3D] Sanitized 0-dimension CreateDepthStencilSurfaceEx(%ux%u -> %ux%u, Usage=0x%lX, Format=%d, Caller=%s)",
            Width, Height, effW, effH, Usage, (int)Format, callerBuf);
    }
    return g_pfnRealCreateDepthStencilSurfaceEx
        ? g_pfnRealCreateDepthStencilSurfaceEx(This, effW, effH, Format, MultiSample, MultisampleQuality, Discard, ppSurface, pSharedHandle, Usage)
        : D3DERR_INVALIDCALL;
}

static int InstallDeviceProxySeh(IDirect3DDevice9* pDevice, bool isEx, size_t* outSlotCount) {
    if (!pDevice) return -1;
    const size_t slotCount = isEx ? 134 : 119;
    if (outSlotCount) *outSlotCount = slotCount;
    __try {
        void** origVTable = *reinterpret_cast<void***>(pDevice);
        if (origVTable == g_d3d9DeviceProxyVTable) return 0; // Already installed
        if (!IsPotentialVtableSeh(origVTable, slotCount)) return -2;

        memset(g_d3d9DeviceProxyVTable, 0, sizeof(g_d3d9DeviceProxyVTable));
        memcpy(g_d3d9DeviceProxyVTable, origVTable, sizeof(void*) * slotCount);

        g_pfnRealDeviceReset = (FnDeviceReset)origVTable[16];
        g_d3d9DeviceProxyVTable[16] = (void*)Hook_DeviceReset;

        g_pfnRealCreateTexture = (FnCreateTexture)origVTable[23];
        g_d3d9DeviceProxyVTable[23] = (void*)Hook_CreateTexture;

        g_pfnRealCreateRenderTarget = (FnCreateRenderTarget)origVTable[28];
        g_d3d9DeviceProxyVTable[28] = (void*)Hook_CreateRenderTarget;

        g_pfnRealCreateDepthStencilSurface = (FnCreateDepthStencilSurface)origVTable[29];
        g_d3d9DeviceProxyVTable[29] = (void*)Hook_CreateDepthStencilSurface;

        g_pfnRealDrawPrimitive = (FnDrawPrimitive)origVTable[81];
        g_d3d9DeviceProxyVTable[81] = (void*)Hook_DrawPrimitive;

        g_pfnRealDrawIndexedPrimitive = (FnDrawIndexedPrimitive)origVTable[82];
        g_d3d9DeviceProxyVTable[82] = (void*)Hook_DrawIndexedPrimitive;

        g_pfnRealDrawPrimitiveUP = (FnDrawPrimitiveUP)origVTable[83];
        g_d3d9DeviceProxyVTable[83] = (void*)Hook_DrawPrimitiveUP;

        g_pfnRealDrawIndexedPrimitiveUP = (FnDrawIndexedPrimitiveUP)origVTable[84];
        g_d3d9DeviceProxyVTable[84] = (void*)Hook_DrawIndexedPrimitiveUP;

        g_pfnRealSetStreamSource = (FnSetStreamSource)origVTable[100];
        g_d3d9DeviceProxyVTable[100] = (void*)Hook_SetStreamSource;

        g_pfnRealSetIndices = (FnSetIndices)origVTable[104];
        g_d3d9DeviceProxyVTable[104] = (void*)Hook_SetIndices;

        if (isEx) {
            g_pfnRealCreateRenderTargetEx = (FnCreateRenderTargetEx)origVTable[129];
            g_d3d9DeviceProxyVTable[129] = (void*)Hook_CreateRenderTargetEx;

            g_pfnRealCreateDepthStencilSurfaceEx = (FnCreateDepthStencilSurfaceEx)origVTable[131];
            g_d3d9DeviceProxyVTable[131] = (void*)Hook_CreateDepthStencilSurfaceEx;

            g_pfnRealDeviceResetEx = (FnDeviceResetEx)origVTable[132];
            g_d3d9DeviceProxyVTable[132] = (void*)Hook_DeviceResetEx;
        } else {
            g_pfnRealCreateRenderTargetEx = nullptr;
            g_pfnRealCreateDepthStencilSurfaceEx = nullptr;
            g_pfnRealDeviceResetEx = nullptr;
        }

        *reinterpret_cast<void***>(pDevice) = g_d3d9DeviceProxyVTable;
        return 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -3;
    }
}

static void InstallDeviceProxy(IDirect3DDevice9* pDevice, bool isEx) {
    if (!pDevice) return;
    g_pActiveD3DDevice = pDevice;
    size_t slotCount = 0;
    char rttiName[128] = {0};
    TryGetRttiNameSeh(pDevice, rttiName, sizeof(rttiName));
    int rc = InstallDeviceProxySeh(pDevice, isEx, &slotCount);
    if (rc == 1) {
        Log("[VULKAN_3D] Proxy vtable installed on %s (%zu slots, rtti='%s', Reset/ResetEx & 3D Draw Calls hooked)",
            isEx ? "IDirect3DDevice9Ex" : "IDirect3DDevice9", slotCount, rttiName[0] ? rttiName : "n/a");
    } else if (rc < 0) {
        Log("[VULKAN_3D] WARNING: InstallDeviceProxySeh skipped on %s (0x%p, rc=%d) due to invalid/unreadable vtable.",
            isEx ? "IDirect3DDevice9Ex" : "IDirect3DDevice9", pDevice, rc);
    }
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDevice(
    IDirect3D9* This, UINT Adapter, D3DDEVTYPE DeviceType, HWND hFocusWindow,
    DWORD BehaviorFlags, D3DPRESENT_PARAMETERS* pPresentationParameters,
    IDirect3DDevice9** ppReturnedDeviceInterface
) {
    HRESULT hr = g_pfnRealCreateDevice(This, Adapter, DeviceType, hFocusWindow, BehaviorFlags, pPresentationParameters, ppReturnedDeviceInterface);
    if (SUCCEEDED(hr) && ppReturnedDeviceInterface && *ppReturnedDeviceInterface) {
        Log("[VULKAN_3D] Intercepted IDirect3D9::CreateDevice -> IDirect3DDevice9 = 0x%p", *ppReturnedDeviceInterface);
        InstallDeviceProxy(*ppReturnedDeviceInterface, false);
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDeviceEx(
    IDirect3D9Ex* This, UINT Adapter, D3DDEVTYPE DeviceType, HWND hFocusWindow,
    DWORD BehaviorFlags, D3DPRESENT_PARAMETERS* pPresentationParameters,
    D3DDISPLAYMODEEX* pFullscreenDisplayMode, IDirect3DDevice9Ex** ppReturnedDeviceInterface
) {
    HRESULT hr = g_pfnRealCreateDeviceEx(This, Adapter, DeviceType, hFocusWindow, BehaviorFlags, pPresentationParameters, pFullscreenDisplayMode, ppReturnedDeviceInterface);
    if (SUCCEEDED(hr) && ppReturnedDeviceInterface && *ppReturnedDeviceInterface) {
        Log("[VULKAN_3D] Intercepted IDirect3D9Ex::CreateDeviceEx -> IDirect3DDevice9Ex = 0x%p", *ppReturnedDeviceInterface);
        InstallDeviceProxy((IDirect3DDevice9*)*ppReturnedDeviceInterface, true);
    }
    return hr;
}

typedef IDirect3D9* (WINAPI *FnDirect3DCreate9)(UINT);
static FnDirect3DCreate9 g_pfnRealDirect3DCreate9 = nullptr;

typedef HRESULT (WINAPI *FnDirect3DCreate9Ex)(UINT, IDirect3D9Ex**);
static FnDirect3DCreate9Ex g_pfnRealDirect3DCreate9Ex = nullptr;

static void EnsureHighPerformanceGpuPreferenceForCurrentProcess() {
    wchar_t exePath[MAX_PATH] = {0};
    DWORD len = GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return;

    // Only register for hon_x64.exe (never pollute registry for python/dotnet test runners)
    const wchar_t* baseName = wcsrchr(exePath, L'\\');
    baseName = baseName ? (baseName + 1) : exePath;
    if (_wcsicmp(baseName, L"hon_x64.exe") != 0) return;

    HKEY hKey = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER,
                        L"Software\\Microsoft\\DirectX\\UserGpuPreferences",
                        0, nullptr, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, nullptr, &hKey, nullptr) == ERROR_SUCCESS && hKey) {
        wchar_t existingVal[128] = {0};
        DWORD cbData = sizeof(existingVal);
        DWORD dwType = 0;
        bool alreadyHighPerf = (RegQueryValueExW(hKey, exePath, nullptr, &dwType, (LPBYTE)existingVal, &cbData) == ERROR_SUCCESS) &&
                               (dwType == REG_SZ) &&
                               (wcsstr(existingVal, L"GpuPreference=2;") != nullptr);
        if (!alreadyHighPerf) {
            const wchar_t* pref = L"GpuPreference=2;";
            DWORD cbWrite = (DWORD)((wcslen(pref) + 1) * sizeof(wchar_t));
            if (RegSetValueExW(hKey, exePath, 0, REG_SZ, (const BYTE*)pref, cbWrite) == ERROR_SUCCESS) {
                Log("[GPU_ROUTING] Registered High-Performance Discrete GPU preference (GpuPreference=2;) for '%ls'", exePath);
            }
        }
        RegCloseKey(hKey);
    }
}

static bool IsNativeD3D9ForcedByEnv(char* outReason, size_t reasonCap) {
    const char* flagVars[] = { "HON_FORCE_NATIVE_D3D9", "KONGOR_FORCE_NATIVE_D3D9" };
    for (const char* name : flagVars) {
        char val[32] = {0};
        DWORD n = GetEnvironmentVariableA(name, val, sizeof(val));
        if (n > 0 && n < sizeof(val)) {
            if (_stricmp(val, "1") == 0 || _stricmp(val, "true") == 0 || _stricmp(val, "yes") == 0 || _stricmp(val, "on") == 0) {
                if (outReason && reasonCap > 0) {
                    snprintf(outReason, reasonCap, "%s=%s", name, val);
                }
                return true;
            }
        }
    }
    char backend[64] = {0};
    DWORD nb = GetEnvironmentVariableA("HON_SWAPCHAIN_BACKEND", backend, sizeof(backend));
    if (nb > 0 && nb < sizeof(backend)) {
        if (_stricmp(backend, "d3d9") == 0 || _stricmp(backend, "d3d9on12") == 0 || _stricmp(backend, "native") == 0) {
            if (outReason && reasonCap > 0) {
                snprintf(outReason, reasonCap, "HON_SWAPCHAIN_BACKEND=%s", backend);
            }
            return true;
        }
    }
    return false;
}

static HMODULE LoadNativeSystemD3D9() {
    if (g_hSystemD3D9) return g_hSystemD3D9;
    wchar_t sysDir[MAX_PATH] = {0};
    UINT len = GetSystemDirectoryW(sysDir, MAX_PATH);
    if (len == 0 || len + 12 >= MAX_PATH) {
        Log("[VULKAN_FALLBACK] ERROR: GetSystemDirectoryW failed (len=%u)", len);
        return nullptr;
    }
    wcscat_s(sysDir, L"\\d3d9.dll");
    g_hSystemD3D9 = LoadLibraryExW(sysDir, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (g_hSystemD3D9) {
        Log("[VULKAN_FALLBACK] Loaded native Microsoft D3D9 runtime: '%ls' (HMODULE=0x%p)", sysDir, g_hSystemD3D9);
    } else {
        Log("[VULKAN_FALLBACK] ERROR: LoadLibraryExW('%ls') failed (Win32 error=%lu)", sysDir, GetLastError());
    }
    return g_hSystemD3D9;
}

static bool EvaluateVulkan13OrFallbackOnce(bool forceReevaluate = false) {
    if (g_bFallbackDecisionEvaluated && !forceReevaluate) {
        return g_bForceSystemD3D9Fallback;
    }

    char envReason[128] = {0};
    if (IsNativeD3D9ForcedByEnv(envReason, sizeof(envReason))) {
        g_bForceSystemD3D9Fallback = true;
        snprintf(g_lastFallbackReason, sizeof(g_lastFallbackReason), "Explicit Native D3D9 override (%s)", envReason);
        g_bFallbackDecisionEvaluated = true;
        Log("[VULKAN_FALLBACK] %s -> Bypassing local DXVK d3d9.dll and routing to native System32\\d3d9.dll!", g_lastFallbackReason);
        return true;
    }

    std::string gpuSummary;
    if (!VulkanContext::ProbeVulkan13Support(&gpuSummary)) {
        g_bForceSystemD3D9Fallback = true;
        snprintf(g_lastFallbackReason, sizeof(g_lastFallbackReason),
                 "Pre-DXVK Vulkan 1.3 hardware check failed (%s)",
                 gpuSummary.empty() ? "no Vulkan 1.3 GPU" : gpuSummary.c_str());
        g_bFallbackDecisionEvaluated = true;
        Log("[VULKAN_FALLBACK] %s -> Bypassing local DXVK d3d9.dll (preventing MinGW SEH unwind crash) and routing to native System32\\d3d9.dll!",
            g_lastFallbackReason);
        return true;
    }

    g_bForceSystemD3D9Fallback = false;
    snprintf(g_lastFallbackReason, sizeof(g_lastFallbackReason),
             "Vulkan 1.3 hardware GPU verified (%s)", gpuSummary.c_str());
    g_bFallbackDecisionEvaluated = true;
    Log("[VULKAN_FALLBACK] Pre-DXVK gate PASSED: %s", g_lastFallbackReason);
    return false;
}

extern "C" __declspec(dllexport) int __stdcall KongorProbeDriverBackend(int forceReevaluate, char* outReasonBuf, int reasonBufCap) {
    bool useNativeD3D9 = EvaluateVulkan13OrFallbackOnce(forceReevaluate != 0);
    if (useNativeD3D9) {
        LoadNativeSystemD3D9();
    }
    if (outReasonBuf && reasonBufCap > 0) {
        strncpy_s(outReasonBuf, (size_t)reasonBufCap, g_lastFallbackReason, _TRUNCATE);
    }
    // Returns 1 when Native System32\d3d9.dll fallback is active, 0 when DXVK Vulkan 1.3 is active
    return useNativeD3D9 ? 1 : 0;
}

static bool SafeInvokeDxvkCreate9(FnDirect3DCreate9 pfn, UINT sdkVer, IDirect3D9** outD3D9, DWORD* outExceptionCode) {
    if (outExceptionCode) *outExceptionCode = 0;
    if (outD3D9) *outD3D9 = nullptr;
    if (!pfn) return false;
    __try {
        IDirect3D9* ptr = pfn(sdkVer);
        if (outD3D9) *outD3D9 = ptr;
        return (ptr != nullptr);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        if (outExceptionCode) *outExceptionCode = GetExceptionCode();
        return false;
    }
}

static bool SafeInvokeDxvkCreate9Ex(FnDirect3DCreate9Ex pfn, UINT sdkVer, IDirect3D9Ex** outD3D9Ex, HRESULT* outHr, DWORD* outExceptionCode) {
    if (outExceptionCode) *outExceptionCode = 0;
    if (outHr) *outHr = E_FAIL;
    if (!pfn || !outD3D9Ex) return false;
    *outD3D9Ex = nullptr;
    __try {
        HRESULT hr = pfn(sdkVer, outD3D9Ex);
        if (outHr) *outHr = hr;
        return (SUCCEEDED(hr) && *outD3D9Ex != nullptr);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        if (outExceptionCode) *outExceptionCode = GetExceptionCode();
        if (outHr) *outHr = D3DERR_NOTAVAILABLE;
        return false;
    }
}

static int InstallD3D9FactoryProxySeh(void* pObj, bool isEx) {
    if (!pObj) return -1;
    // IDirect3D9 has exactly 17 slots (0..16); IDirect3D9Ex has 21 slots (0..20).
    // Never over-read past slot 16 on base IDirect3D9!
    const size_t slotCount = isEx ? 21 : 17;
    void** proxyTable = isEx ? g_d3d9ExProxyVTable : g_d3d9ProxyVTable;
    __try {
        void** originalVTable = *reinterpret_cast<void***>(pObj);
        if (originalVTable == proxyTable) return 0;
        if (!IsPotentialVtableSeh(originalVTable, slotCount)) return -2;

        memcpy(proxyTable, originalVTable, sizeof(void*) * slotCount);
        g_pfnRealCreateDevice = (FnCreateDevice)originalVTable[16];
        proxyTable[16] = (void*)Hook_CreateDevice;
        if (isEx) {
            g_pfnRealCreateDeviceEx = (FnCreateDeviceEx)originalVTable[20];
            proxyTable[20] = (void*)Hook_CreateDeviceEx;
        }
        *reinterpret_cast<void***>(pObj) = proxyTable;
        return 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -3;
    }
}

static IDirect3D9* WINAPI Hook_Direct3DCreate9(UINT SDKVersion) {
    EvaluateVulkan13OrFallbackOnce();
    Log("[VULKAN_3D] Direct3DCreate9(0x%08X) called by vid_d3d9_x64.dll (NativeSystemD3D9Fallback=%d)",
        SDKVersion, g_bForceSystemD3D9Fallback ? 1 : 0);
    IDirect3D9* pD3D9 = nullptr;

    if (!g_bForceSystemD3D9Fallback) {
        FnDirect3DCreate9 pfnDxvk = g_pfnRealDirect3DCreate9;
        if (!pfnDxvk) {
            HMODULE hD3D9 = LoadLibraryW(L"d3d9.dll");
            if (hD3D9) {
                pfnDxvk = (FnDirect3DCreate9)GetProcAddress(hD3D9, "Direct3DCreate9");
            }
        }
        DWORD sehCode = 0;
        if (!SafeInvokeDxvkCreate9(pfnDxvk, SDKVersion, &pD3D9, &sehCode)) {
            Log("[VULKAN_FALLBACK] DXVK Direct3DCreate9 failed/faulted (seh=0x%08lX) -> recovering via native System32\\d3d9.dll!",
                (unsigned long)sehCode);
            g_bForceSystemD3D9Fallback = true;
        }
    }

    if (g_bForceSystemD3D9Fallback && !pD3D9) {
        HMODULE hSysD3D9 = LoadNativeSystemD3D9();
        if (hSysD3D9) {
            auto pfnSys = (FnDirect3DCreate9)GetProcAddress(hSysD3D9, "Direct3DCreate9");
            if (pfnSys) {
                pD3D9 = pfnSys(SDKVersion);
                Log("[VULKAN_FALLBACK] Native System32\\d3d9.dll!Direct3DCreate9 returned 0x%p", pD3D9);
            }
        }
    }

    if (pD3D9) {
        int rc = InstallD3D9FactoryProxySeh(pD3D9, false);
        if (rc == 1) {
            Log("[VULKAN_3D] Proxy vtable installed on IDirect3D9 (17 slots, CreateDevice slot 16 hooked)");
        } else if (rc < 0) {
            Log("[VULKAN_3D] WARNING: InstallD3D9FactoryProxySeh failed on IDirect3D9 (rc=%d)", rc);
        }
    }
    return pD3D9;
}

static HRESULT WINAPI Hook_Direct3DCreate9Ex(UINT SDKVersion, IDirect3D9Ex** ppOutputInterface) {
    EvaluateVulkan13OrFallbackOnce();
    Log("[VULKAN_3D] Direct3DCreate9Ex(0x%08X) called by vid_d3d9_x64.dll (NativeSystemD3D9Fallback=%d)",
        SDKVersion, g_bForceSystemD3D9Fallback ? 1 : 0);
    HRESULT hr = E_FAIL;

    if (!g_bForceSystemD3D9Fallback) {
        FnDirect3DCreate9Ex pfnDxvk = g_pfnRealDirect3DCreate9Ex;
        if (!pfnDxvk) {
            HMODULE hD3D9 = LoadLibraryW(L"d3d9.dll");
            if (hD3D9) {
                pfnDxvk = (FnDirect3DCreate9Ex)GetProcAddress(hD3D9, "Direct3DCreate9Ex");
            }
        }
        DWORD sehCode = 0;
        if (!SafeInvokeDxvkCreate9Ex(pfnDxvk, SDKVersion, ppOutputInterface, &hr, &sehCode)) {
            Log("[VULKAN_FALLBACK] DXVK Direct3DCreate9Ex failed/faulted (hr=0x%08X, seh=0x%08lX) -> recovering via native System32\\d3d9.dll!",
                (unsigned int)hr, (unsigned long)sehCode);
            g_bForceSystemD3D9Fallback = true;
        }
    }

    if (g_bForceSystemD3D9Fallback) {
        HMODULE hSysD3D9 = LoadNativeSystemD3D9();
        if (hSysD3D9) {
            auto pfnSysEx = (FnDirect3DCreate9Ex)GetProcAddress(hSysD3D9, "Direct3DCreate9Ex");
            if (pfnSysEx) {
                hr = pfnSysEx(SDKVersion, ppOutputInterface);
                Log("[VULKAN_FALLBACK] Native System32\\d3d9.dll!Direct3DCreate9Ex returned hr=0x%08X (iface=0x%p)",
                    (unsigned int)hr, (ppOutputInterface ? *ppOutputInterface : nullptr));
            }
        }
    }

    if (SUCCEEDED(hr) && ppOutputInterface && *ppOutputInterface) {
        int rc = InstallD3D9FactoryProxySeh(*ppOutputInterface, true);
        if (rc == 1) {
            Log("[VULKAN_3D] Proxy vtable installed on IDirect3D9Ex (21 slots, CreateDevice slot 16 & CreateDeviceEx slot 20 hooked)");
        } else if (rc < 0) {
            Log("[VULKAN_3D] WARNING: InstallD3D9FactoryProxySeh failed on IDirect3D9Ex (rc=%d)", rc);
        }
    }
    return hr;
}

typedef FARPROC (WINAPI *FnGetProcAddress)(HMODULE, LPCSTR);
static FnGetProcAddress g_pfnRealGetProcAddress = nullptr;

static FARPROC WINAPI Hook_GetProcAddress(HMODULE hModule, LPCSTR lpProcName) {
    if (lpProcName && !((ULONG_PTR)lpProcName >> 16)) {
        return g_pfnRealGetProcAddress(hModule, lpProcName);
    }
    if (lpProcName && strcmp(lpProcName, "Direct3DCreate9Ex") == 0) {
        if (g_pfnRealGetProcAddress) {
            g_pfnRealDirect3DCreate9Ex = (FnDirect3DCreate9Ex)g_pfnRealGetProcAddress(hModule, lpProcName);
        }
        return (FARPROC)Hook_Direct3DCreate9Ex;
    }
    if (lpProcName && strcmp(lpProcName, "Direct3DCreate9") == 0) {
        if (g_pfnRealGetProcAddress) {
            g_pfnRealDirect3DCreate9 = (FnDirect3DCreate9)g_pfnRealGetProcAddress(hModule, lpProcName);
        }
        return (FARPROC)Hook_Direct3DCreate9;
    }
    return g_pfnRealGetProcAddress(hModule, lpProcName);
}

// -------------------------------------------------------------------------------------------------
// Display Mode Switching Suppression for Multi-Monitor / Virtual Display / DXVK Stability
// -------------------------------------------------------------------------------------------------
typedef LONG (WINAPI *FnChangeDisplaySettingsW)(DEVMODEW* lpDevMode, DWORD dwFlags);
typedef LONG (WINAPI *FnChangeDisplaySettingsExW)(LPCWSTR lpszDeviceName, DEVMODEW* lpDevMode, HWND hwnd, DWORD dwflags, LPVOID lParam);
typedef LONG (WINAPI *FnChangeDisplaySettingsA)(DEVMODEA* lpDevMode, DWORD dwFlags);
typedef LONG (WINAPI *FnChangeDisplaySettingsExA)(LPCSTR lpszDeviceName, DEVMODEA* lpDevMode, HWND hwnd, DWORD dwflags, LPVOID lParam);

static FnChangeDisplaySettingsW g_pfnRealChangeDisplaySettingsW = nullptr;
static FnChangeDisplaySettingsExW g_pfnRealChangeDisplaySettingsExW = nullptr;
static FnChangeDisplaySettingsA g_pfnRealChangeDisplaySettingsA = nullptr;
static FnChangeDisplaySettingsExA g_pfnRealChangeDisplaySettingsExA = nullptr;

static std::atomic<bool> g_lastDisplayModeChangeFailed{false};

static LONG WINAPI Hook_ChangeDisplaySettingsW(DEVMODEW* lpDevMode, DWORD dwFlags) {
    // On multi-monitor setups with USB DisplayLink or high-refresh panels, ChangeDisplaySettings(NULL, 0)
    // causes DWM to flush and re-enumerate all display adapters, freezing the entire PC for several seconds.
    // In windowed/borderless mode, the OS display resolution does not need resetting.
    if (!lpDevMode || dwFlags == 0) {
        Log("[VULKAN_WSI] Bypassing redundant ChangeDisplaySettingsW(devMode=0x%p, flags=0x%x) -> Returning DISP_CHANGE_SUCCESSFUL (Desktop Freeze Prevented)", lpDevMode, dwFlags);
        return DISP_CHANGE_SUCCESSFUL;
    }
    LONG res = DISP_CHANGE_SUCCESSFUL;
    if (g_pfnRealChangeDisplaySettingsW) {
        res = g_pfnRealChangeDisplaySettingsW(lpDevMode, dwFlags);
    } else {
        res = ChangeDisplaySettingsW(lpDevMode, dwFlags);
    }
    Log("[VULKAN_WSI] ChangeDisplaySettingsW(devMode=0x%p, flags=0x%x) returned %ld", lpDevMode, dwFlags, res);
    if (res != DISP_CHANGE_SUCCESSFUL) {
        g_lastDisplayModeChangeFailed.store(true, std::memory_order_release);
        Log("[VULKAN_WSI][WARN] Display mode switch failed (code=%ld); Hook_SetMode will reconcile driver dimensions to desktop monitor bounds.", res);
        return DISP_CHANGE_SUCCESSFUL;
    }
    g_lastDisplayModeChangeFailed.store(false, std::memory_order_release);
    return res;
}

static LONG WINAPI Hook_ChangeDisplaySettingsExW(LPCWSTR lpszDeviceName, DEVMODEW* lpDevMode, HWND hwnd, DWORD dwflags, LPVOID lParam) {
    if (!lpDevMode || dwflags == 0) {
        Log("[VULKAN_WSI] Bypassing redundant ChangeDisplaySettingsExW(devMode=0x%p, flags=0x%x) -> Returning DISP_CHANGE_SUCCESSFUL (Desktop Freeze Prevented)", lpDevMode, dwflags);
        return DISP_CHANGE_SUCCESSFUL;
    }
    if (lpDevMode) {
        if ((lpDevMode->dmFields & DM_PELSWIDTH) && lpDevMode->dmPelsWidth < 640) lpDevMode->dmPelsWidth = 640;
        if ((lpDevMode->dmFields & DM_PELSHEIGHT) && lpDevMode->dmPelsHeight < 480) lpDevMode->dmPelsHeight = 480;
        Log("[VULKAN_WSI] Multi-Monitor ChangeDisplaySettingsExW: device='%ls', %ux%u@%uHz pos=(%d,%d)",
            lpszDeviceName ? lpszDeviceName : L"PRIMARY",
            lpDevMode->dmPelsWidth, lpDevMode->dmPelsHeight,
            (lpDevMode->dmFields & DM_DISPLAYFREQUENCY) ? lpDevMode->dmDisplayFrequency : 0,
            (lpDevMode->dmFields & DM_POSITION) ? lpDevMode->dmPosition.x : 0,
            (lpDevMode->dmFields & DM_POSITION) ? lpDevMode->dmPosition.y : 0);
    }
    LONG res = DISP_CHANGE_SUCCESSFUL;
    if (g_pfnRealChangeDisplaySettingsExW) {
        res = g_pfnRealChangeDisplaySettingsExW(lpszDeviceName, lpDevMode, hwnd, dwflags, lParam);
    } else {
        res = ChangeDisplaySettingsExW(lpszDeviceName, lpDevMode, hwnd, dwflags, lParam);
    }
    if (res != DISP_CHANGE_SUCCESSFUL) {
        g_lastDisplayModeChangeFailed.store(true, std::memory_order_release);
        Log("[VULKAN_WSI][WARN] Display mode switch failed (code=%ld); Hook_SetMode will reconcile driver dimensions to desktop monitor bounds.", res);
        return DISP_CHANGE_SUCCESSFUL;
    }
    g_lastDisplayModeChangeFailed.store(false, std::memory_order_release);
    return res;
}

static LONG WINAPI Hook_ChangeDisplaySettingsA(DEVMODEA* lpDevMode, DWORD dwFlags) {
    if (!lpDevMode || dwFlags == 0) {
        Log("[VULKAN_WSI] Bypassing redundant ChangeDisplaySettingsA(devMode=0x%p, flags=0x%x) -> Returning DISP_CHANGE_SUCCESSFUL (Desktop Freeze Prevented)", lpDevMode, dwFlags);
        return DISP_CHANGE_SUCCESSFUL;
    }
    LONG res = DISP_CHANGE_SUCCESSFUL;
    if (g_pfnRealChangeDisplaySettingsA) {
        res = g_pfnRealChangeDisplaySettingsA(lpDevMode, dwFlags);
    } else {
        res = ChangeDisplaySettingsA(lpDevMode, dwFlags);
    }
    if (res != DISP_CHANGE_SUCCESSFUL) {
        g_lastDisplayModeChangeFailed.store(true, std::memory_order_release);
        Log("[VULKAN_WSI][WARN] Display mode switch failed (code=%ld); Hook_SetMode will reconcile driver dimensions to desktop monitor bounds.", res);
        return DISP_CHANGE_SUCCESSFUL;
    }
    g_lastDisplayModeChangeFailed.store(false, std::memory_order_release);
    return res;
}

static LONG WINAPI Hook_ChangeDisplaySettingsExA(LPCSTR lpszDeviceName, DEVMODEA* lpDevMode, HWND hwnd, DWORD dwflags, LPVOID lParam) {
    if (lpDevMode) {
        if ((lpDevMode->dmFields & DM_PELSWIDTH) && lpDevMode->dmPelsWidth < 640) lpDevMode->dmPelsWidth = 640;
        if ((lpDevMode->dmFields & DM_PELSHEIGHT) && lpDevMode->dmPelsHeight < 480) lpDevMode->dmPelsHeight = 480;
        Log("[VULKAN_WSI] Multi-Monitor ChangeDisplaySettingsExA: device='%s', %ux%u@%uHz pos=(%d,%d)",
            lpszDeviceName ? lpszDeviceName : "PRIMARY",
            lpDevMode->dmPelsWidth, lpDevMode->dmPelsHeight,
            (lpDevMode->dmFields & DM_DISPLAYFREQUENCY) ? lpDevMode->dmDisplayFrequency : 0,
            (lpDevMode->dmFields & DM_POSITION) ? lpDevMode->dmPosition.x : 0,
            (lpDevMode->dmFields & DM_POSITION) ? lpDevMode->dmPosition.y : 0);
    }
    LONG res = DISP_CHANGE_SUCCESSFUL;
    if (g_pfnRealChangeDisplaySettingsExA) {
        res = g_pfnRealChangeDisplaySettingsExA(lpszDeviceName, lpDevMode, hwnd, dwflags, lParam);
    } else {
        res = ChangeDisplaySettingsExA(lpszDeviceName, lpDevMode, hwnd, dwflags, lParam);
    }
    if (res != DISP_CHANGE_SUCCESSFUL) {
        g_lastDisplayModeChangeFailed.store(true, std::memory_order_release);
        Log("[VULKAN_WSI][WARN] Display mode switch failed (code=%ld); Hook_SetMode will reconcile driver dimensions to desktop monitor bounds.", res);
        return DISP_CHANGE_SUCCESSFUL;
    }
    g_lastDisplayModeChangeFailed.store(false, std::memory_order_release);
    return res;
}

typedef BOOL (WINAPI *FnEnumDisplaySettingsW)(LPCWSTR lpszDeviceName, DWORD iModeNum, DEVMODEW* lpDevMode);
typedef BOOL (WINAPI *FnEnumDisplaySettingsExW)(LPCWSTR lpszDeviceName, DWORD iModeNum, DEVMODEW* lpDevMode, DWORD dwFlags);

static FnEnumDisplaySettingsW g_pfnRealEnumDisplaySettingsW = nullptr;
static FnEnumDisplaySettingsExW g_pfnRealEnumDisplaySettingsExW = nullptr;

static BOOL WINAPI Hook_EnumDisplaySettingsW(LPCWSTR lpszDeviceName, DWORD iModeNum, DEVMODEW* lpDevMode) {
    BOOL res = FALSE;
    if (g_pfnRealEnumDisplaySettingsW)
        res = g_pfnRealEnumDisplaySettingsW(lpszDeviceName, iModeNum, lpDevMode);
    else
        res = EnumDisplaySettingsW(lpszDeviceName, iModeNum, lpDevMode);

    if (res && lpDevMode) {
        if ((lpDevMode->dmFields & DM_PELSWIDTH) && lpDevMode->dmPelsWidth < 640) lpDevMode->dmPelsWidth = 640;
        if ((lpDevMode->dmFields & DM_PELSHEIGHT) && lpDevMode->dmPelsHeight < 480) lpDevMode->dmPelsHeight = 480;
    }
    return res;
}

static BOOL WINAPI Hook_EnumDisplaySettingsExW(LPCWSTR lpszDeviceName, DWORD iModeNum, DEVMODEW* lpDevMode, DWORD dwFlags) {
    BOOL res = FALSE;
    if (g_pfnRealEnumDisplaySettingsExW)
        res = g_pfnRealEnumDisplaySettingsExW(lpszDeviceName, iModeNum, lpDevMode, dwFlags);
    else
        res = EnumDisplaySettingsExW(lpszDeviceName, iModeNum, lpDevMode, dwFlags);

    if (res && lpDevMode) {
        if ((lpDevMode->dmFields & DM_PELSWIDTH) && lpDevMode->dmPelsWidth < 640) lpDevMode->dmPelsWidth = 640;
        if ((lpDevMode->dmFields & DM_PELSHEIGHT) && lpDevMode->dmPelsHeight < 480) lpDevMode->dmPelsHeight = 480;
    }
    return res;
}

// Function pointer signature aliases for lifecycle operations
typedef bool  (__fastcall *FnInit)(void*);
typedef bool  (__fastcall *FnStartWindow)();
typedef int   (__fastcall *FnSetMode)();
typedef void  (__fastcall *FnShutdown)();
typedef bool  (__fastcall *FnBeginFrame)();
typedef void  (__fastcall *FnEndFrame)();
typedef bool  (__fastcall *FnGetMode)(int, void*);
typedef bool  (__fastcall *FnGetCurrentMode)(void*);

static SVidDriverAPI* g_pSavedDriverAPI = nullptr;
static void InitFastBusTelemetryProducer();

// Specific Hook Implementations for Critical Engine Lifecycle
static bool __fastcall Hook_Init(void* pParams) {
    Log(">>> Slot +0x020: Init(pParams=0x%p) CALLED", pParams);
    EvaluateVulkan13OrFallbackOnce();
    bool res = ((FnInit)g_realAPI.Init)(pParams);
    Log("<<< Slot +0x020: Init RETURNED %s", res ? "SUCCESS" : "FAILED");

    // Phase 2: Initialize Fast Bus Telemetry Producer early (works in both DXVK and Native D3D9 modes)
    InitFastBusTelemetryProducer();

    if (g_bForceSystemD3D9Fallback) {
        Log("[VULKAN_FALLBACK] Skipping VulkanContext::Initialize (Native System32\\d3d9.dll compatibility mode active).");
        return res;
    }

    // Initialize native Vulkan 1.3 core engine alongside K2
    bool vkInit = VulkanContext::Get().Initialize(nullptr);
    Log("Vulkan 1.3 Core Initialized: %s (GPU: %s)", 
        vkInit ? "SUCCESS" : "FAILED", 
        VulkanContext::Get().GetGpuName().c_str());

    return res;
}

static LARGE_INTEGER g_perfFrequency = {0};
static LARGE_INTEGER g_lastFrameTime = {0};
static std::atomic<int64_t> g_frameStartQpc{0};
static std::atomic<DWORD> g_mainThreadId{0};
static double g_frameDeltaMs = 0.0;
static double g_instantFps = 0.0;
static double g_presentWaitMs = 0.0;
static void RefreshVulkan2DConfig();

// -------------------------------------------------------------------------------------------------
// Phase 2: Lock-Free Fast Bus Telemetry Producer & Autonomous Hitch Sentinel
// -------------------------------------------------------------------------------------------------
struct HitchEventRecord {
    uint64_t frameIndex;
    int64_t  timestampQpc;
    float    totalDeltaMs;
    float    cpuEngineMs;
    float    gpuRenderMs;
    float    presentWaitMs;
    uint32_t drawCalls;
    uint32_t flags;
    char     reason[32];
};

static constexpr size_t SENTINEL_HITCH_RING_CAPACITY = 64;
static HitchEventRecord g_hitchRingBuffer[SENTINEL_HITCH_RING_CAPACITY] = {};
static std::atomic<uint64_t> g_hitchRingHead{0};
static std::mutex g_hitchMutex;

static KongorSharedMemHandle g_shmFastBusHandle = {0};
static bool g_shmFastBusCreated = false;
static uint32_t g_lastWorkingSetMb = 0;
static std::atomic<uint64_t> g_sentinelHitches{0};

static void InitFastBusTelemetryProducer() {
    if (g_shmFastBusCreated) return;

    // Create the canonical Fast Bus shared memory buffer (4096 slots, 192-byte V2 header with Seqlock)
    if (kongor_shm_create_producer("Local\\KongorTelemetryBuffer", 4096, &g_shmFastBusHandle)) {
        g_shmFastBusCreated = true;
        Log("[FAST_BUS] Created producer 'Local\\KongorTelemetryBuffer' (4096 slots, 192-byte Seqlock header, %zu bytes).",
            sizeof(KongorTelemetryRingHeaderV2) + (4096 * sizeof(FrameTelemetryRecord)));
    } else {
        Log("[FAST_BUS] WARNING: Failed to create 'Local\\KongorTelemetryBuffer' (Error %lu).", GetLastError());
    }
}

static void ShutdownFastBusTelemetry() {
    if (g_shmFastBusCreated) {
        kongor_shm_close(&g_shmFastBusHandle);
        g_shmFastBusCreated = false;
        Log("[FAST_BUS] Closed Fast Bus producer mapping.");
    }
}

static void PublishFastBusFrameTelemetry(
    uint64_t f,
    LARGE_INTEGER qpcTimestamp,
    double engineMs,
    double renderMs,
    double presentWaitMs,
    double frameDeltaMs,
    uint32_t drawCalls)
{
    if (!g_shmFastBusCreated) return;

    // Sample working set once every 60 frames to keep overhead at 0ns on typical frames
    if (f % 60 == 1) {
        PROCESS_MEMORY_COUNTERS pmc = {0};
        if (GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc))) {
            g_lastWorkingSetMb = (uint32_t)(pmc.WorkingSetSize / (1024 * 1024));
        }
    }

    uint32_t flags = 0;
    bool isHitch = false;
    const char* hitchReason = nullptr;

    // Autonomous Hitch Sentinel: Trap and classify frame hitches > 10ms after frame 60
    if (f > 60) {
        if (frameDeltaMs > 10.0) {
            isHitch = true;
            flags |= KONGOR_FRAME_FLAG_HITCH;
            if (engineMs > 8.0 && renderMs <= 8.0) {
                flags |= KONGOR_FRAME_FLAG_CPU_STALL;
                hitchReason = "CPU_ENGINE_TICK_STALL";
            } else if (renderMs > 8.0 && engineMs <= 8.0) {
                flags |= KONGOR_FRAME_FLAG_GPU_STALL;
                hitchReason = "GPU_RENDER_PRESENT_STALL";
            } else if (presentWaitMs > 8.0) {
                flags |= KONGOR_FRAME_FLAG_DWM_WAIT;
                hitchReason = "DWM_COMPOSITOR_WAIT";
            } else {
                flags |= KONGOR_FRAME_FLAG_PREEMPTION;
                hitchReason = "QUANTUM_PREEMPTION_OR_THROTTLE";
            }
        }
    }

    if (isHitch) {
        uint64_t totalHitches = g_sentinelHitches.fetch_add(1) + 1;

        // Rate-limit structured log output to at most 1 summary per second,
        // and suppress log spam when alt-tabbed or during long load pauses (> 500ms)
        HWND hGameWnd = VulkanContext::Get().GetHWND();
        bool isInForeground = (!hGameWnd || GetForegroundWindow() == hGameWnd);
        static int64_t s_lastHitchLogQpc = 0;
        int64_t qpcDelta = qpcTimestamp.QuadPart - s_lastHitchLogQpc;
        bool allowLog = (g_perfFrequency.QuadPart > 0) &&
                        (qpcDelta >= g_perfFrequency.QuadPart) &&
                        isInForeground &&
                        (frameDeltaMs < 500.0);

        if (allowLog) {
            s_lastHitchLogQpc = qpcTimestamp.QuadPart;
            Log("[SENTINEL_HITCH #%llu] Frame #%llu | Total: %.2f ms | Engine: %.2f ms | Render: %.2f ms | Wait: %.3f ms | Draws: %u | Reason: %s",
                totalHitches, f, frameDeltaMs, engineMs, renderMs, presentWaitMs, drawCalls, hitchReason ? hitchReason : "UNKNOWN");
        }

        {
            std::lock_guard<std::mutex> lock(g_hitchMutex);
            uint64_t slot = g_hitchRingHead.fetch_add(1, std::memory_order_relaxed) % SENTINEL_HITCH_RING_CAPACITY;
            g_hitchRingBuffer[slot].frameIndex = f;
            g_hitchRingBuffer[slot].timestampQpc = qpcTimestamp.QuadPart;
            g_hitchRingBuffer[slot].totalDeltaMs = (float)frameDeltaMs;
            g_hitchRingBuffer[slot].cpuEngineMs = (float)engineMs;
            g_hitchRingBuffer[slot].gpuRenderMs = (float)renderMs;
            g_hitchRingBuffer[slot].presentWaitMs = (float)presentWaitMs;
            g_hitchRingBuffer[slot].drawCalls = drawCalls;
            g_hitchRingBuffer[slot].flags = flags;
            strncpy_s(g_hitchRingBuffer[slot].reason, sizeof(g_hitchRingBuffer[slot].reason),
                      hitchReason ? hitchReason : "UNKNOWN", _TRUNCATE);
        }
    }

    FrameTelemetryRecord rec = {};
    rec.FrameIndex = f;
    rec.TimestampQpc = qpcTimestamp.QuadPart;
    rec.CpuFrameTimeMs = (float)engineMs;
    rec.GpuFrameTimeMs = (float)renderMs;
    rec.InputLatencyMs = (float)presentWaitMs;
    rec.DrawCallCount = drawCalls;
    rec.ActiveEntities = 0;
    rec.ParticlesAlive = 0;
    rec.PingMicroseconds = 0;
    rec.PacketLossPercent = 0.0f;
    rec.MemoryWorkingSetMb = g_lastWorkingSetMb;
    rec.AudioVoicesActive = 0;
    rec.ServerTick = (uint32_t)f;
    rec.ReservedFlags = flags;

    kongor_shm_write_record(&g_shmFastBusHandle, &rec);
}

static HWND FindCurrentProcessK2Window() {
    DWORD currentPid = GetCurrentProcessId();
    HWND hIter = nullptr;
    while ((hIter = FindWindowExA(nullptr, hIter, nullptr, nullptr)) != nullptr) {
        DWORD pid = 0;
        GetWindowThreadProcessId(hIter, &pid);
        if (pid != currentPid) continue;
        char cls[64] = {0};
        if (GetClassNameA(hIter, cls, sizeof(cls)) > 0 && strncmp(cls, "K2_", 3) == 0) {
            return hIter;
        }
    }
    HWND hCtx = VulkanContext::Get().GetHWND();
    if (hCtx && IsWindow(hCtx)) return hCtx;
    return nullptr;
}

static bool __fastcall Hook_StartWindow() {
    Log(">>> Slot +0x028: StartWindow() CALLED");
    bool res = ((FnStartWindow)g_realAPI.StartWindow)();
    Log("<<< Slot +0x028: StartWindow RETURNED %s", res ? "SUCCESS" : "FAILED");

    if (res) {
        // Resolve the game window created by K2 strictly within our own process
        HWND hWnd = FindCurrentProcessK2Window();
        if (!hWnd) hWnd = GetActiveWindow();

        if (hWnd) {
            RECT rc = {0};
            GetClientRect(hWnd, &rc);
            uint32_t w = rc.right - rc.left;
            uint32_t h = rc.bottom - rc.top;
            Log("[PRESENTATION_BRIDGE] Handshake initialized for game window HWND: 0x%p (%ux%u)", hWnd, w, h);
            VulkanContext::Get().SetHWND(hWnd);
            RefreshVulkan2DConfig();

            // Initialize high-precision frame pacing timer
            QueryPerformanceFrequency(&g_perfFrequency);
            QueryPerformanceCounter(&g_lastFrameTime);
            g_frameStartQpc.store(g_lastFrameTime.QuadPart, std::memory_order_relaxed);
            g_mainThreadId.store(GetCurrentThreadId(), std::memory_order_relaxed);

            Log("[PRESENTATION_BRIDGE] Zero-collision presentation arbitration active: Dual-swapchain conflict eliminated.");

            // Phase 2: Initialize Fast Bus Telemetry Producer (Local\KongorTelemetryBuffer)
            InitFastBusTelemetryProducer();

            if (!g_bForceSystemD3D9Fallback && VulkanContext::Get().IsInitialized() && VulkanContext::Get().GetDevice() != VK_NULL_HANDLE) {
                // Phase 2.4B: Initialize Native Vulkan 2D Memory & Pipeline Scaffolding
                bool vk2dInit = Vulkan2D::Get().Initialize(
                    VulkanContext::Get().GetDevice(),
                    VulkanContext::Get().GetPhysicalDevice(),
                    VulkanContext::Get().GetGraphicsQueue(),
                    VulkanContext::Get().GetGraphicsQueueFamilyIndex(),
                    VK_FORMAT_B8G8R8A8_UNORM,
                    w, h
                );
                Log("[VULKAN_2D_SCAFFOLD] Vulkan2D Initialized: %s (4 MB Coherent Ring Buffer, SPIR-V Pipeline)",
                    vk2dInit ? "SUCCESS" : "FAILED");

                // Phase 4 (#257): Initialize Vulkan 3D Draw-Call & Model/Shader Telemetry Subsystem
                bool vk3dInit = Vulkan3D::Get().Initialize(
                    VulkanContext::Get().GetDevice(),
                    VulkanContext::Get().GetPhysicalDevice(),
                    VulkanContext::Get().GetGraphicsQueue(),
                    VulkanContext::Get().GetGraphicsQueueFamilyIndex()
                );
                Log("[VULKAN_3D_SCAFFOLD] Vulkan3D Initialized: %s (Draw-Call & Model/Shader Telemetry, 3D Vulkan via DXVK v3.0.2)",
                    vk3dInit ? "SUCCESS" : "FAILED");

                // Phase 5: Initialize In-Engine ImGui DevTools (Ctrl+Shift+D / Ctrl+F12) & Hook Window Procedure
                VulkanDevTools::Get().Initialize(hWnd);
                if (!g_pfnDevToolsOriginalWndProc) {
                    g_bIsUnicodeWndProc = (IsWindowUnicode(hWnd) != FALSE);
                    g_pfnDevToolsOriginalWndProc = (WNDPROC)(g_bIsUnicodeWndProc
                        ? SetWindowLongPtrW(hWnd, GWLP_WNDPROC, (LONG_PTR)DevTools_WndProcHook)
                        : SetWindowLongPtrA(hWnd, GWLP_WNDPROC, (LONG_PTR)DevTools_WndProcHook));
                    Log("[DEVTOOLS] Installed WndProc hook for Ctrl+Shift+D / Ctrl+F12 HUD interception. Unicode: %s, Original WndProc: 0x%p",
                        g_bIsUnicodeWndProc ? "YES" : "NO", g_pfnDevToolsOriginalWndProc);
                }
            } else {
                Log("[VULKAN_FALLBACK] StartWindow: Skipping Vulkan2D/Vulkan3D/DevTools initialization (NativeFallback=%d, VkInitialized=%d)",
                    g_bForceSystemD3D9Fallback ? 1 : 0,
                    VulkanContext::Get().IsInitialized() ? 1 : 0);
            }
        } else {
            Log("Warning: Could not find window HWND for Vulkan presentation coordination.");
        }
    }

    return res;
}

static bool SafeQueryK2CvarBool(const wchar_t* name) {
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2 || !name) return false;

    typedef void* (*FnLocalICvarGetString)(void* outStr, const void* cvarName);
    typedef void (*FnLocalDeallocate)(void* p);
    static auto s_pfnGetString = reinterpret_cast<FnLocalICvarGetString>(
        GetProcAddress(hK2, "?GetString@ICvar@@SA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@AEBV23@@Z"));
    static auto s_pfnDealloc = reinterpret_cast<FnLocalDeallocate>(
        GetProcAddress(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z"));
    if (!s_pfnGetString) return false;

    struct K2RawWStr {
        union { wchar_t buf[8]; const wchar_t* ptr; };
        size_t size; size_t res;
    };

    K2RawWStr inVal{};
    size_t len = wcslen(name);
    inVal.size = len;
    inVal.res = len > 7 ? len : 7;
    wchar_t* dynBuf = nullptr;
    if (len > 7) {
        dynBuf = _wcsdup(name);
        inVal.ptr = dynBuf;
    } else {
        wcsncpy(inVal.buf, name, 8);
    }

    K2RawWStr outVal{};
    bool bVal = false;
    __try {
        s_pfnGetString(&outVal, &inVal);
        const wchar_t* strPtr = (outVal.res > 7) ? outVal.ptr : outVal.buf;
        if (outVal.size > 0 && strPtr) {
            if (_wcsicmp(strPtr, L"true") == 0 || _wcsicmp(strPtr, L"1") == 0) bVal = true;
        }
        if (outVal.res > 7 && outVal.ptr && s_pfnDealloc) {
            s_pfnDealloc(const_cast<wchar_t*>(outVal.ptr));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    if (dynBuf) {
        free(dynBuf);
    }
    return bVal;
}

static bool SafeQueryK2CvarStringA(const wchar_t* name, char* outBuf, size_t maxChars) {
    if (!name || !outBuf || maxChars == 0) return false;
    outBuf[0] = '\0';
    HMODULE hK2 = GetModuleHandleA("k2_x64.dll");
    if (!hK2) return false;

    typedef void* (*FnLocalICvarGetString)(void* outStr, const void* cvarName);
    typedef void (*FnLocalDeallocate)(void* p);
    static auto s_pfnGetString = reinterpret_cast<FnLocalICvarGetString>(
        GetProcAddress(hK2, "?GetString@ICvar@@SA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$K2StringAllocator@_W@@@std@@AEBV23@@Z"));
    static auto s_pfnDealloc = reinterpret_cast<FnLocalDeallocate>(
        GetProcAddress(hK2, "?Deallocate@CMemManager@@SAXPEAX@Z"));
    if (!s_pfnGetString) return false;

    struct K2RawWStr {
        union { wchar_t buf[8]; const wchar_t* ptr; };
        size_t size; size_t res;
    };

    K2RawWStr inVal{};
    size_t len = wcslen(name);
    inVal.size = len;
    inVal.res = len > 7 ? len : 7;
    wchar_t* dynBuf = nullptr;
    if (len > 7) {
        dynBuf = _wcsdup(name);
        inVal.ptr = dynBuf;
    } else {
        wcsncpy(inVal.buf, name, 8);
    }

    K2RawWStr outVal{};
    bool success = false;
    __try {
        s_pfnGetString(&outVal, &inVal);
        const wchar_t* strPtr = (outVal.res > 7) ? outVal.ptr : outVal.buf;
        if (outVal.size > 0 && strPtr) {
            int ret = WideCharToMultiByte(CP_UTF8, 0, strPtr, -1, outBuf, (int)maxChars, nullptr, nullptr);
            if (ret > 0) success = true;
        }
        if (outVal.res > 7 && outVal.ptr && s_pfnDealloc) {
            s_pfnDealloc(const_cast<wchar_t*>(outVal.ptr));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    if (dynBuf) {
        free(dynBuf);
    }
    return success;
}

static std::string NormalizeVulkanMonitorToken(const char* raw) {
    if (!raw || !*raw) return "";
    std::string s(raw);
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '"' || s.front() == '\'' ||
                          s.front() == '\\' || s.front() == '/' || s.front() == '.')) {
        s.erase(s.begin());
    }
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '"' || s.back() == '\'' ||
                          s.back() == '\r' || s.back() == '\n')) {
        s.pop_back();
    }
    for (char& ch : s) {
        if (ch >= 'a' && ch <= 'z') ch = static_cast<char>(ch - 'a' + 'A');
    }
    return s;
}

static int ExtractVulkanMonitorNumber(const std::string& normToken) {
    for (size_t i = 0; i < normToken.size(); ++i) {
        if (normToken[i] >= '0' && normToken[i] <= '9') {
            int v = atoi(normToken.c_str() + i);
            if (v > 0) return v;
            break;
        }
    }
    return 999999;
}

static int __fastcall Hook_SetMode() {
    Log(">>> Slot +0x030: SetMode() CALLED");
    HWND hBefore = FindCurrentProcessK2Window();
    HMONITOR hTargetMon = nullptr;

    char targetDevBuf[64] = {0};
    if (SafeQueryK2CvarStringA(L"vid_display", targetDevBuf, sizeof(targetDevBuf)) && targetDevBuf[0]) {
        // vid_display specified
    } else if (SafeQueryK2CvarStringA(L"options_display", targetDevBuf, sizeof(targetDevBuf)) && targetDevBuf[0]) {
        // options_display specified
    } else {
        GetEnvironmentVariableA("KONGOR_TARGET_MONITOR", targetDevBuf, sizeof(targetDevBuf));
    }

    if (targetDevBuf[0]) {
        std::string normTarget = NormalizeVulkanMonitorToken(targetDevBuf);
        int parsedIdx = -1;
        if (!normTarget.empty() && isdigit((unsigned char)normTarget[0])) {
            parsedIdx = atoi(normTarget.c_str());
        }

        struct RawMonCandidate {
            HMONITOR hMon;
            std::string normToken;
            bool isPrimary;
        };
        std::vector<RawMonCandidate> mons;

        EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR hMon, HDC, LPRECT, LPARAM lParam) -> BOOL {
            auto* pMons = reinterpret_cast<std::vector<RawMonCandidate>*>(lParam);
            MONITORINFOEXA mi{};
            mi.cbSize = sizeof(mi);
            if (GetMonitorInfoA(hMon, &mi)) {
                pMons->push_back(RawMonCandidate{
                    hMon,
                    NormalizeVulkanMonitorToken(mi.szDevice),
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0
                });
            }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&mons));

        std::sort(mons.begin(), mons.end(), [](const RawMonCandidate& a, const RawMonCandidate& b) {
            if (a.isPrimary != b.isPrimary) return a.isPrimary > b.isPrimary;
            int na = ExtractVulkanMonitorNumber(a.normToken);
            int nb = ExtractVulkanMonitorNumber(b.normToken);
            if (na != nb) return na < nb;
            return a.normToken < b.normToken;
        });

        for (size_t i = 0; i < mons.size(); ++i) {
            bool nameMatch = (!normTarget.empty() && mons[i].normToken == normTarget);
            bool idxMatch = (parsedIdx >= 1 && parsedIdx == static_cast<int>(i + 1)) ||
                            (parsedIdx == 0 && i == 0);
            if (nameMatch || idxMatch) {
                hTargetMon = mons[i].hMon;
                Log("[SETMODE_PLACEMENT] Resolved target monitor from target='%s' (norm='%s') -> #%zu '%s' HMONITOR=0x%p",
                    targetDevBuf, normTarget.c_str(), i + 1, mons[i].normToken.c_str(), hTargetMon);
                break;
            }
        }
    }

    if (!hTargetMon) {
        hTargetMon = (hBefore && IsWindow(hBefore))
            ? MonitorFromWindow(hBefore, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
    }

    HWND fgBefore = GetForegroundWindow();
    HWND activeBefore = GetActiveWindow();
    bool wasActiveBeforeSetMode = (hBefore && (fgBefore == hBefore || activeBefore == hBefore));

    SetEnvironmentVariableA("KONGOR_IN_SETMODE", "1");
    int modeIndex = ((FnSetMode)g_realAPI.SetMode)();
    Log("<<< Slot +0x030: SetMode RETURNED modeIndex=%d", modeIndex);

    HWND hWnd = FindCurrentProcessK2Window();
    if (hWnd && IsWindow(hWnd) && !IsIconic(hWnd)) {
        VulkanContext::Get().SetHWND(hWnd);
        POINT ptOrigin = {0, 0};
        ClientToScreen(hWnd, &ptOrigin);
        char headlessEnv[16] = {0};
        bool isHeadless = (GetEnvironmentVariableA("KONGOR_HEADLESS", headlessEnv, sizeof(headlessEnv)) > 0 && headlessEnv[0] == '1') ||
                          (ptOrigin.x <= -5000 || ptOrigin.y <= -5000);
        if (!isHeadless && hTargetMon) {
            MONITORINFO mi{};
            mi.cbSize = sizeof(mi);
            if (GetMonitorInfoA(hTargetMon, &mi)) {
                LONG_PTR style = GetWindowLongPtrA(hWnd, GWL_STYLE);
                bool isExplicitBorderless = SafeQueryK2CvarBool(L"vid_borderless");
                bool isExplicitFullscreen = SafeQueryK2CvarBool(L"vid_fullscreen");

                bool isWindowedWithCaption = ((style & WS_CAPTION) != 0) && !isExplicitBorderless && !isExplicitFullscreen;
                RECT rcWnd{};
                GetWindowRect(hWnd, &rcWnd);
                int winW = rcWnd.right - rcWnd.left;
                int winH = rcWnd.bottom - rcWnd.top;
                if (!isWindowedWithCaption) {
                    // DirectFlip Non-Exclusive Fullscreen & Borderless Windowed: ensure WS_POPUP is set, strip caption, and spans target monitor bounds
                    bool fixedStyle = false;
                    if ((style & WS_POPUP) == 0 || (style & WS_CAPTION) != 0) {
                        LONG_PTR popupStyle = (style & ~WS_CAPTION) | WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
                        SetWindowLongPtrA(hWnd, GWL_STYLE, popupStyle);
                        fixedStyle = true;
                    }
                    int monW = mi.rcMonitor.right - mi.rcMonitor.left;
                    int monH = mi.rcMonitor.bottom - mi.rcMonitor.top;

                    bool modeChangeFailed = g_lastDisplayModeChangeFailed.exchange(false, std::memory_order_acq_rel);
                    if ((modeChangeFailed || isExplicitBorderless || isExplicitFullscreen) && g_pSavedDriverAPI && monW > 0 && monH > 0) {
                        uint32_t drvW = *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x220);
                        uint32_t drvH = *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x224);
                        if (drvW != (uint32_t)monW || drvH != (uint32_t)monH) {
                            *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x220) = (uint32_t)monW;
                            *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x224) = (uint32_t)monH;
                            Log("[SETMODE_PLACEMENT] Reconciled driver dimensions %ux%u -> %dx%d for target monitor span (modeChangeFailed=%d)",
                                drvW, drvH, monW, monH, modeChangeFailed ? 1 : 0);
                        }
                    }

                    UINT swpFlags = SWP_NOZORDER | SWP_FRAMECHANGED | (wasActiveBeforeSetMode ? SWP_SHOWWINDOW : SWP_NOACTIVATE);
                    if (fixedStyle || rcWnd.left != mi.rcMonitor.left || rcWnd.top != mi.rcMonitor.top || winW != monW || winH != monH) {
                        SetWindowPos(hWnd, nullptr,
                            mi.rcMonitor.left, mi.rcMonitor.top, monW, monH,
                            swpFlags);
                        Log("[SETMODE_PLACEMENT] DirectFlip Fullscreen spanned target monitor [%ld,%ld %dx%d] (fixedPopupStyle=%d)",
                            mi.rcMonitor.left, mi.rcMonitor.top, monW, monH, fixedStyle ? 1 : 0);
                    }

                    if (wasActiveBeforeSetMode) {
                        ReleaseCapture();
                        if (GetForegroundWindow() != hWnd) SetForegroundWindow(hWnd);
                        if (GetFocus() != hWnd) SetFocus(hWnd);
                        SendMessageA(hWnd, WM_ACTIVATE, WA_ACTIVE, 0);
                        PostMessageA(hWnd, WM_SIZE, SIZE_RESTORED, MAKELPARAM(monW, monH));
                        Log("[SETMODE_PLACEMENT] Restored foreground focus & posted WM_ACTIVATE/WM_SIZE resync (%dx%d)",
                            monW, monH);
                    }
                } else {
                    // Windowed with caption: keep on target monitor work area if K2 reset it onto another monitor
                    HMONITOR hAfterMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                    int workW = mi.rcWork.right - mi.rcWork.left;
                    int workH = mi.rcWork.bottom - mi.rcWork.top;
                    if (hAfterMon != hTargetMon || rcWnd.top < mi.rcWork.top) {
                        int newX = mi.rcWork.left + ((workW > winW) ? ((workW - winW) / 2) : 0);
                        int newY = mi.rcWork.top + ((workH > winH) ? ((workH - winH) / 2) : 0);
                        SetWindowPos(hWnd, nullptr, newX, newY, 0, 0,
                            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                        Log("[SETMODE_PLACEMENT] Centered Windowed frame on active monitor work area (%d, %d, %dx%d)",
                            newX, newY, winW, winH);
                    }
                }
            }
        }
    }

    if (g_pSavedDriverAPI && Vulkan2D::Get().IsInitialized()) {
        uint32_t width = *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x220);
        uint32_t height = *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x224);
        uint32_t bpp = *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x228);
        uint32_t rate = *(uint32_t*)((uint8_t*)g_pSavedDriverAPI + 0x22c);
        Log("Active Mode Set Confirmed: %ux%u @ %u Hz (bpp=%u, modeIndex=%d)", width, height, rate, bpp, modeIndex);
        if (width > 0 && height > 0) {
            Vulkan2D::Get().Resize(width, height);
        }
    }
    SetEnvironmentVariableA("KONGOR_IN_SETMODE", "0");
    return modeIndex;
}

static HANDLE g_hFramePacingTimer = nullptr;

static void ShutdownFramePacingTimer() {
    if (g_hFramePacingTimer) {
        CloseHandle(g_hFramePacingTimer);
        g_hFramePacingTimer = nullptr;
    }
}

static void SafeCallRealShutdown(void* pfnShutdown) {
    if (!pfnShutdown) return;
    __try {
        ((FnShutdown)pfnShutdown)();
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        Log("[SHUTDOWN] Safely caught and suppressed backend exception on exit (ExceptionCode: 0x%08X).", GetExceptionCode());
    }
}

static void __fastcall Hook_Shutdown() {
    Log(">>> Slot +0x050: Shutdown() CALLED");
    if (g_pfnDevToolsOriginalWndProc) {
        HWND hWnd = VulkanContext::Get().GetHWND();
        if (hWnd && IsWindow(hWnd)) {
            __try {
                LONG_PTR currentWndProc = g_bIsUnicodeWndProc
                    ? GetWindowLongPtrW(hWnd, GWLP_WNDPROC)
                    : GetWindowLongPtrA(hWnd, GWLP_WNDPROC);
                // Only unhook if DevTools_WndProcHook is still the outermost WndProc;
                // never unlink NativeGuard's KongorSubclassWndProc if chained above us!
                if (currentWndProc == (LONG_PTR)DevTools_WndProcHook) {
                    if (g_bIsUnicodeWndProc) {
                        SetWindowLongPtrW(hWnd, GWLP_WNDPROC, (LONG_PTR)g_pfnDevToolsOriginalWndProc);
                    } else {
                        SetWindowLongPtrA(hWnd, GWLP_WNDPROC, (LONG_PTR)g_pfnDevToolsOriginalWndProc);
                    }
                } else {
                    Log("[SHUTDOWN] Preserving chained outer WndProc (0x%p != DevTools_WndProcHook)", (void*)currentWndProc);
                }
            } __except(EXCEPTION_EXECUTE_HANDLER) {}
        }
        g_pfnDevToolsOriginalWndProc = nullptr;
    }
    VulkanDevTools::Get().Shutdown();
    ShutdownFastBusTelemetry();
    ShutdownFramePacingTimer();
    Vulkan3D::Get().Shutdown();
    Vulkan2D::Get().Shutdown();
    VulkanContext::Get().Shutdown();
    if (KONGOR_TRACY_SHUTDOWN_IF_ACTIVE()) {
        Log("[TRACY][DEV-ONLY] Local Tracy profiler SHUT DOWN cleanly.");
    }
    g_pActiveD3DDevice = nullptr;
    if (g_bForceSystemD3D9Fallback) {
        SafeCallRealShutdown(g_realAPI.Shutdown);
        Log("<<< [SHUTDOWN] Slot +0x050: Shutdown COMPLETED (Native System32\\d3d9.dll clean shutdown)");
        ShutdownAsyncLogWriter();
        return;
    }
    // Do NOT invoke g_realAPI.Shutdown() under DXVK: Vulkan is our active video driver and completely replaces D3D9.
    // vid_d3d9_x64's D3D9/D3D9On12 device was never initialized with valid resources, so calling Shutdown()
    // triggers a null vtable function pointer dereference (rip=0x0) inside d3d9.dll, generating a crash dump before process exit.
    Log("<<< [SHUTDOWN] Slot +0x050: Shutdown COMPLETED (Vulkan clean shutdown, D3D9 bypass preserved)");
    ShutdownAsyncLogWriter();
}

static void RefreshVulkan2DConfig();

static std::atomic<uint64_t> g_sleep25Bypassed{0};
static std::atomic<uint64_t> g_sleepSubFrameYields{0};
static std::atomic<uint64_t> g_sleepRealCalls{0};
static std::atomic<uint64_t> g_setAnimTotalCalls{0};
static std::atomic<uint64_t> g_setAnimSkippedCalls{0};
static std::atomic<uint64_t> g_setAnimExecutedCalls{0};
static DWORD g_dwMainEngineThreadId = 0;
static LARGE_INTEGER g_timeBeginFrame = {0};
static LARGE_INTEGER g_timeEndFrame = {0};
static double g_renderMs = 0.0;
static double g_endFrameCallMs = 0.0;
static double g_engineMs = 0.0;

static bool __fastcall Hook_BeginFrame() {
    KONGOR_TRACY_ZONE_SCOPED_N("K2::VidVulkan::BeginFrame");
    uint64_t f = ++g_frameCount;

    // Dynamically capture the main engine thread ID and frame-start QPC once per frame
    DWORD currentTid = GetCurrentThreadId();
    g_dwMainEngineThreadId = currentTid;
    g_mainThreadId.store(currentTid, std::memory_order_relaxed);

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    g_frameStartQpc.store(now.QuadPart, std::memory_order_relaxed);
    if (g_perfFrequency.QuadPart > 0) {
        if (g_lastFrameTime.QuadPart > 0) {
            g_frameDeltaMs = (double)(now.QuadPart - g_lastFrameTime.QuadPart) * 1000.0 / (double)g_perfFrequency.QuadPart;
            if (g_frameDeltaMs > 0.001) {
                g_instantFps = 1000.0 / g_frameDeltaMs;
            }
        }
        if (g_timeEndFrame.QuadPart > 0) {
            g_engineMs = (double)(now.QuadPart - g_timeEndFrame.QuadPart) * 1000.0 / (double)g_perfFrequency.QuadPart;
        }
    }
    g_lastFrameTime = now;
    g_timeBeginFrame = now;

    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().BeginFrame(f);
    }

    if (Vulkan2D::Get().IsInitialized()) {
        Vulkan2D::Get().ResetBatch();
    }

    // Phase 2: Ultra-Low Latency Frame Pacing (VK_KHR_present_wait)
    // Synchronize CPU tick with the previous frame's physical display presentation.
    // This drains the display queue and reduces input latency to sub-5ms (esports reflex tier).
    double presentWaitMs = 0.0;
    if (VulkanContext::Get().IsPresentWaitSupported()) {
        uint64_t currentPresentId = VulkanContext::Get().GetCurrentPresentId();
        if (currentPresentId > 1) {
            LARGE_INTEGER waitStart, waitEnd;
            QueryPerformanceCounter(&waitStart);
            bool waitRes = VulkanContext::Get().WaitForPresent(currentPresentId - 1, 10000000ULL); // 10 ms max budget
            QueryPerformanceCounter(&waitEnd);
            if (g_perfFrequency.QuadPart > 0) {
                presentWaitMs = (double)(waitEnd.QuadPart - waitStart.QuadPart) * 1000.0 / (double)g_perfFrequency.QuadPart;
            }
        }
    }
    g_presentWaitMs = presentWaitMs;

    if (f <= 20 || (f % 72 == 0) || (f <= 200 && g_frameDeltaMs > 15.0)) {
        Log("Slot +0x068: BeginFrame() [Frame #%llu | Delta: %.2f ms (Render: %.2f ms [EndFrame: %.2f ms] | Engine: %.2f ms) | Instant FPS: %.1f | LowLatencyWait: %.3f ms | Sleep: %llu yield, %llu drop25, %llu real | MainTID: %lu]", 
            f, g_frameDeltaMs, g_renderMs, g_endFrameCallMs, g_engineMs, g_instantFps, presentWaitMs,
            g_sleepSubFrameYields.load(), g_sleep25Bypassed.load(), g_sleepRealCalls.load(),
            g_dwMainEngineThreadId);
    }
    return ((FnBeginFrame)g_realAPI.BeginFrame)();
}

// -------------------------------------------------------------------------------------------------
// Phase 2.4D: Complete 2D Batch Takeover & Dynamic Vulkan Dispatch
// -------------------------------------------------------------------------------------------------
static bool g_vulkan2dEnabled = false;
static bool g_vulkan2dBypassD3D = false; // When true, D3D9 2D software batches are bypassed

static void RefreshVulkan2DConfig() {
    static uint32_t s_checkCounter = 0;
    if (++s_checkCounter % 180 != 1) return; // Check roughly every ~3 seconds at 60 FPS

    char envVal[32];
    if (GetEnvironmentVariableA("KONGOR_VULKAN_2D", envVal, sizeof(envVal)) > 0) {
        g_vulkan2dEnabled = (atoi(envVal) != 0);
    }
    if (GetEnvironmentVariableA("KONGOR_VULKAN_2D_BYPASS", envVal, sizeof(envVal)) > 0) {
        g_vulkan2dBypassD3D = (atoi(envVal) != 0);
    }

    FILE* fp = fopen("vid_vulkan.cfg", "r");
    if (fp) {
        char line[256];
        while (fgets(line, sizeof(line), fp)) {
            if (strncmp(line, "vulkan2d=", 9) == 0) {
                g_vulkan2dEnabled = (atoi(line + 9) != 0);
            } else if (strncmp(line, "vulkan2d_bypass=", 16) == 0) {
                g_vulkan2dBypassD3D = (atoi(line + 16) != 0);
            }
        }
        fclose(fp);
    }
}

// Exact 2D function pointer signatures verified against Ghidra disassembly of k2_x64.dll and vid_d3d9_x64.dll
typedef void (__fastcall *FnAdd2dRect)(float, float, float, float, float, float, float, float, int, uint32_t, uint32_t);
typedef void (__fastcall *FnAdd2dQuad)(const float*, const float*, const float*, const float*,
                                      const float*, const float*, const float*, const float*,
                                      int, uint32_t, uint32_t);
typedef void (__fastcall *FnAdd2dTri)(const float*, const float*, const float*,
                                     const float*, const float*, const float*,
                                     int, uint32_t, uint32_t);
typedef void (__fastcall *FnAdd2dLine)(const float*, const float*, const float*, const float*, int);
typedef void (__fastcall *FnAdd2dSmoothLine)(const float*, const float*, const float*, const float*, int);
typedef void (__fastcall *FnSetScissor)(int, int, int, int);

typedef int  (__fastcall *FnRegisterTexture)(void*, uint32_t);
typedef void (__fastcall *FnUnregisterTexture)(int);
typedef void (__fastcall *FnUpdateTexture)(int, void*, uint32_t);

// Cumulative element counters
static std::atomic<uint64_t> g_rectCount{0};
static std::atomic<uint64_t> g_quadCount{0};
static std::atomic<uint64_t> g_triCount{0};
static std::atomic<uint64_t> g_lineCount{0};
static std::atomic<uint64_t> g_smoothLineCount{0};

// Per-frame counters
static std::atomic<uint64_t> g_rectCountFrame{0};
static std::atomic<uint64_t> g_quadCountFrame{0};
static std::atomic<uint64_t> g_triCountFrame{0};
static std::atomic<uint64_t> g_lineCountFrame{0};
static std::atomic<uint64_t> g_smoothLineCountFrame{0};

// Texture registration tracking
static std::atomic<uint64_t> g_texRegisterCount{0};
static std::atomic<uint64_t> g_texUnregisterCount{0};

// Helper: Pack 4-float color (RGBA 0.0 .. 1.0) into packed 32-bit uint
static inline uint32_t PackColorVec4(const float* c) {
    if (!c) return 0xFFFFFFFF;
    uint32_t r = (uint32_t)(std::clamp(c[0], 0.0f, 1.0f) * 255.0f);
    uint32_t g = (uint32_t)(std::clamp(c[1], 0.0f, 1.0f) * 255.0f);
    uint32_t b = (uint32_t)(std::clamp(c[2], 0.0f, 1.0f) * 255.0f);
    uint32_t a = (uint32_t)(std::clamp(c[3], 0.0f, 1.0f) * 255.0f);
    return (a << 24) | (b << 16) | (g << 8) | r;
}

// Shadow Batch structure (Milestone 2.4A verification buffer)
struct SShadow2DRect {
    float x, y, w, h;
    float s0, t0, s1, t1;
    int hTexture;
    uint32_t c1, c2;
};

static std::vector<SShadow2DRect> g_shadowBuffer;
static std::mutex g_2dTelemetryMutex;
static float g_minX = 1e9f, g_maxX = -1e9f, g_minY = 1e9f, g_maxY = -1e9f;
static std::unordered_map<int, uint64_t> g_textureHistogram;
static bool g_enableShadowTelemetry = false;
static uint32_t g_consecutiveFastDeviceLostFrames = 0;

static HRESULT SafeTestCooperativeLevel(IDirect3DDevice9* pDev) noexcept {
    if (!pDev) return D3D_OK;
    __try {
        return pDev->TestCooperativeLevel();
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return D3DERR_DEVICELOST;
    }
}

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

static int g_cachedTargetCapFps = 0;
static bool g_cachedVsyncEnabled = false;

static constexpr uint32_t kHighHzWindowCap = 1024;
struct HighHzTelemetryWindow {
    int64_t  windowStartQpc = 0;
    uint32_t frameCount = 0;
    double   sumDeltaMs = 0.0;
    double   sumRenderMs = 0.0;
    double   sumEngineMs = 0.0;
    uint64_t sumDraws = 0;
    float    frameDeltasMs[kHighHzWindowCap] = {};
};
static HighHzTelemetryWindow g_highHzWindow = {};

static void RecordAndMaybeEmitHighHzTelemetry(
    int64_t nowQpc,
    double frameDeltaMs,
    double renderMs,
    double engineMs,
    uint64_t frameDraws)
{
    if (g_perfFrequency.QuadPart <= 0 || frameDeltaMs <= 0.001) {
        return;
    }

    if (g_highHzWindow.windowStartQpc == 0) {
        g_highHzWindow.windowStartQpc = nowQpc;
    }

    if (g_highHzWindow.frameCount < kHighHzWindowCap) {
        g_highHzWindow.frameDeltasMs[g_highHzWindow.frameCount] = (float)frameDeltaMs;
    }
    ++g_highHzWindow.frameCount;
    g_highHzWindow.sumDeltaMs += frameDeltaMs;
    g_highHzWindow.sumRenderMs += renderMs;
    g_highHzWindow.sumEngineMs += engineMs;
    g_highHzWindow.sumDraws += frameDraws;

    int64_t elapsedQpc = nowQpc - g_highHzWindow.windowStartQpc;
    if (elapsedQpc >= g_perfFrequency.QuadPart && g_highHzWindow.frameCount > 0) {
        const uint32_t totalFrames = g_highHzWindow.frameCount;
        const uint32_t sampleCount = (totalFrames < kHighHzWindowCap) ? totalFrames : kHighHzWindowCap;
        const uint32_t worstCount = (sampleCount + 99u) / 100u;
        const uint32_t pivotIdx = sampleCount - worstCount;

        std::nth_element(
            g_highHzWindow.frameDeltasMs,
            g_highHzWindow.frameDeltasMs + pivotIdx,
            g_highHzWindow.frameDeltasMs + sampleCount);

        double worstSumMs = 0.0;
        for (uint32_t i = pivotIdx; i < sampleCount; ++i) {
            worstSumMs += (double)g_highHzWindow.frameDeltasMs[i];
        }
        const double avgWorst1PctMs = worstSumMs / (double)worstCount;
        const double avgDeltaMs = g_highHzWindow.sumDeltaMs / (double)totalFrames;
        const double avgFps = (avgDeltaMs > 0.001) ? (1000.0 / avgDeltaMs) : 0.0;
        const double p1LowFps = (avgWorst1PctMs > 0.001) ? (1000.0 / avgWorst1PctMs) : 0.0;
        const double avgRenderMs = g_highHzWindow.sumRenderMs / (double)totalFrames;
        const double avgEngineMs = g_highHzWindow.sumEngineMs / (double)totalFrames;
        const double avgDraws = (double)g_highHzWindow.sumDraws / (double)totalFrames;

        Log("[HIGH_HZ_TELEMETRY] AvgFPS=%.1f | P1LowFPS=%.1f | AvgRenderMs=%.2f | AvgEngineMs=%.2f | AvgDraws=%.1f | TargetCapFps=%d | Samples=%u | VSync=%d",
            avgFps, p1LowFps, avgRenderMs, avgEngineMs, avgDraws,
            g_cachedTargetCapFps, totalFrames, g_cachedVsyncEnabled ? 1 : 0);

        g_highHzWindow.windowStartQpc = nowQpc;
        g_highHzWindow.frameCount = 0;
        g_highHzWindow.sumDeltaMs = 0.0;
        g_highHzWindow.sumRenderMs = 0.0;
        g_highHzWindow.sumEngineMs = 0.0;
        g_highHzWindow.sumDraws = 0;
    }
}

static void PaceFrameToTargetMaxFps() {
    if (g_perfFrequency.QuadPart <= 0) return;

    static int s_headlessState = -1;
    if (s_headlessState < 0) {
        char envBuf[16] = {0};
        s_headlessState = (GetEnvironmentVariableA("KONGOR_HEADLESS", envBuf, sizeof(envBuf)) > 0 && envBuf[0] == '1') ? 1 : 0;
    }
    if (s_headlessState == 1) return;

    static LARGE_INTEGER s_lastConfigPoll = {0};
    static LARGE_INTEGER s_nextFrameDeadline = {0};

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);

    LONGLONG pollInterval = g_perfFrequency.QuadPart / 5; // 200 ms CVar poll cadence
    if (s_lastConfigPoll.QuadPart == 0 || (now.QuadPart - s_lastConfigPoll.QuadPart) >= pollInterval) {
        s_lastConfigPoll = now;
        char fpsBuf[32] = {0};
        int parsedFps = 0;
        if (SafeQueryK2CvarStringA(L"host_maxFPS", fpsBuf, sizeof(fpsBuf)) && fpsBuf[0]) {
            parsedFps = atoi(fpsBuf);
        }
        bool vsync = SafeQueryK2CvarBool(L"vid_vsync");
        if (parsedFps != g_cachedTargetCapFps || vsync != g_cachedVsyncEnabled) {
            Log("[FRAME_PACING] Active target frame cap updated: %d -> %d FPS (host_maxFPS='%s', vid_vsync=%d)",
                g_cachedTargetCapFps, parsedFps, fpsBuf, vsync ? 1 : 0);
            g_cachedTargetCapFps = parsedFps;
            g_cachedVsyncEnabled = vsync;
            s_nextFrameDeadline.QuadPart = 0;
        }
    }

    // Uncapped (<= 0 or >= 1000) or hardware VSync active: skip software pacing
    if (g_cachedVsyncEnabled || g_cachedTargetCapFps <= 0 || g_cachedTargetCapFps >= 1000) {
        s_nextFrameDeadline.QuadPart = 0;
        return;
    }

    LONGLONG intervalTicks = (LONGLONG)((double)g_perfFrequency.QuadPart / (double)g_cachedTargetCapFps + 0.5);
    if (intervalTicks <= 0) return;

    if (s_nextFrameDeadline.QuadPart <= 0) {
        LONGLONG anchor = (g_timeBeginFrame.QuadPart > 0) ? g_timeBeginFrame.QuadPart : now.QuadPart;
        s_nextFrameDeadline.QuadPart = anchor + intervalTicks;
    } else {
        s_nextFrameDeadline.QuadPart += intervalTicks;
    }

    if (now.QuadPart >= s_nextFrameDeadline.QuadPart) {
        if ((now.QuadPart - s_nextFrameDeadline.QuadPart) > intervalTicks) {
            s_nextFrameDeadline = now;
        }
        return;
    }

    if ((s_nextFrameDeadline.QuadPart - now.QuadPart) > (intervalTicks * 2)) {
        s_nextFrameDeadline.QuadPart = now.QuadPart + intervalTicks;
    }

    double remMs = (double)(s_nextFrameDeadline.QuadPart - now.QuadPart) * 1000.0 / (double)g_perfFrequency.QuadPart;
    if (remMs > 1.5) {
        if (!g_hFramePacingTimer) {
            g_hFramePacingTimer = CreateWaitableTimerExW(
                nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (!g_hFramePacingTimer) {
                g_hFramePacingTimer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
            }
        }
        if (g_hFramePacingTimer) {
            LARGE_INTEGER dueTime;
            dueTime.QuadPart = -(LONGLONG)((remMs - 1.0) * 10000.0);
            if (dueTime.QuadPart < -1000) {
                if (SetWaitableTimer(g_hFramePacingTimer, &dueTime, 0, nullptr, nullptr, FALSE)) {
                    WaitForSingleObject(g_hFramePacingTimer, 25);
                }
            }
        }
    }

    for (;;) {
        QueryPerformanceCounter(&now);
        if (now.QuadPart >= s_nextFrameDeadline.QuadPart) break;
        _mm_pause();
    }
}

static void __fastcall Hook_EndFrame() {
    uint64_t f = g_frameCount.load();
    LARGE_INTEGER beforeEnd;
    QueryPerformanceCounter(&beforeEnd);
    {
        KONGOR_TRACY_ZONE_SCOPED_N("K2::VidVulkan::EndFrame");
        ((FnEndFrame)g_realAPI.EndFrame)();
    }
    LARGE_INTEGER afterEnd;
    QueryPerformanceCounter(&afterEnd);
    if (g_perfFrequency.QuadPart > 0) {
        g_endFrameCallMs = (double)(afterEnd.QuadPart - beforeEnd.QuadPart) * 1000.0 / (double)g_perfFrequency.QuadPart;
        g_renderMs = (double)(afterEnd.QuadPart - g_timeBeginFrame.QuadPart) * 1000.0 / (double)g_perfFrequency.QuadPart;
    }
    g_timeEndFrame = afterEnd;

    uint64_t rects = g_rectCountFrame.exchange(0);
    uint64_t quads = g_quadCountFrame.exchange(0);
    uint64_t tris = g_triCountFrame.exchange(0);
    uint64_t lines = g_lineCountFrame.exchange(0);
    uint64_t smoothLines = g_smoothLineCountFrame.exchange(0);
    uint64_t d3d9Draws = g_d3d9DrawCallsFrame.exchange(0, std::memory_order_relaxed);

    // Circuit breaker for DXVK VK_ERROR_DEVICE_LOST / 1000 FPS non-blocking Present spin-loop (#Issue10)
    if (!g_bForceSystemD3D9Fallback && f > 120 && g_endFrameCallMs < 0.05 && g_frameDeltaMs > 0.0 && g_frameDeltaMs <= 1.1 && rects == 0) {
        g_consecutiveFastDeviceLostFrames++;
        if (g_consecutiveFastDeviceLostFrames >= 120) {
            HRESULT coopHr = SafeTestCooperativeLevel(g_pActiveD3DDevice);
            if (coopHr == D3DERR_DEVICELOST || coopHr == D3DERR_DEVICENOTRESET || coopHr == D3DERR_DRIVERINTERNALERROR) {
                if (g_consecutiveFastDeviceLostFrames == 120 || (g_consecutiveFastDeviceLostFrames % 300) == 0) {
                    Log("[VULKAN_DEVICE_LOST] Detected non-blocking Present spin (coop=0x%08X, frames=%u) -> throttling 16ms to prevent 1000Hz CPU hang!",
                        (unsigned int)coopHr, g_consecutiveFastDeviceLostFrames);
                }
                ::Sleep(16);
            }
        }
    } else {
        g_consecutiveFastDeviceLostFrames = 0;
    }

    if (f <= 5) {
        Log("Slot +0x070: EndFrame() [Frame #%llu | Presented | Mode: %s | 2D: %llu rects, %llu quads, %llu tris, %llu lines, %llu smooth | Total Rects: %llu]", 
            f, g_vulkan2dBypassD3D ? "VULKAN_TAKEOVER_ACTIVE" : "VULKAN_RING_MIRROR",
            rects, quads, tris, lines, smoothLines, g_rectCount.load());

        if (rects > 0 && Vulkan2D::Get().IsInitialized()) {
            Log("[VULKAN_2D_TAKEOVER] Host-Coherent Ring: %u vertices, %u indices, %zu batches queued (Capacity: 4 MB Coherent Memory)",
                Vulkan2D::Get().GetCurrentVertexCount(),
                Vulkan2D::Get().GetCurrentIndexCount(),
                Vulkan2D::Get().GetBatchCount());
        }
    }

    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().EndFrame(f);
    }

    // Phase 2: Publish frame to Fast Bus with 64-bit atomic Seqlock and Hitch Sentinel
    uint32_t total2DPrimitives = (uint32_t)(rects + quads + tris + lines + smoothLines);
    uint64_t frameDraws = (d3d9Draws > 0) ? d3d9Draws : (uint64_t)total2DPrimitives;
    PublishFastBusFrameTelemetry(f, afterEnd, g_engineMs, g_renderMs, g_presentWaitMs, g_frameDeltaMs, (uint32_t)frameDraws);

    // 1-second rolling High-Hz diagnostic summary (emitted asynchronously)
    RecordAndMaybeEmitHighHzTelemetry(afterEnd.QuadPart, g_frameDeltaMs, g_renderMs, g_engineMs, frameDraws);

    // Phase 5: Render In-Engine ImGui DevTools Overlay if active (Ctrl+Shift+D / Ctrl+F12)
    if (VulkanDevTools::Get().IsVisible() && Vulkan2D::Get().IsInitialized()) {
        HWND hWnd = VulkanContext::Get().GetHWND();
        RECT rc = {0};
        if (hWnd) GetClientRect(hWnd, &rc);
        uint32_t vw = (rc.right > rc.left) ? (uint32_t)(rc.right - rc.left) : 1920;
        uint32_t vh = (rc.bottom > rc.top) ? (uint32_t)(rc.bottom - rc.top) : 1080;
        VulkanDevTools::Get().Render(vw, vh, g_instantFps, g_frameDeltaMs, f, rects, quads, tris, lines);
    }

    KONGOR_TRACY_PLOT("FrameDeltaMs", g_frameDeltaMs);
    KONGOR_TRACY_FRAME_MARK();

    PaceFrameToTargetMaxFps();
    QueryPerformanceCounter(&g_timeEndFrame);
}

static bool __fastcall Hook_GetMode(int modeIndex, void* pMode) {
    bool res = g_realAPI.GetMode ? ((FnGetMode)g_realAPI.GetMode)(modeIndex, pMode) : false;
    Log("Slot +0x1F0: GetMode(%d, %p) returned %s", modeIndex, pMode, res ? "true" : "false");
    return res;
}

static bool __fastcall Hook_GetCurrentMode(void* pMode) {
    bool res = g_realAPI.GetCurrentMode ? ((FnGetCurrentMode)g_realAPI.GetCurrentMode)(pMode) : false;
    Log("Slot +0x1F8: GetCurrentMode(%p) returned %s", pMode, res ? "true" : "false");
    return res;
}

static void __fastcall Hook_Add2dRect(float x, float y, float w, float h, float s0, float t0, float s1, float t1, int hTexture, uint32_t c1, uint32_t c2) {
    g_rectCount.fetch_add(1, std::memory_order_relaxed);
    g_rectCountFrame.fetch_add(1, std::memory_order_relaxed);

    if (g_enableShadowTelemetry) {
        std::lock_guard<std::mutex> lock(g_2dTelemetryMutex);
        if (x < g_minX) g_minX = x;
        if (x + w > g_maxX) g_maxX = x + w;
        if (y < g_minY) g_minY = y;
        if (y + h > g_maxY) g_maxY = y + h;
        g_textureHistogram[hTexture]++;

        if (g_shadowBuffer.size() < 16384) {
            g_shadowBuffer.push_back({x, y, w, h, s0, t0, s1, t1, hTexture, c1, c2});
        }
    }

    // Phase 2.4D: Direct emission into dynamic host-coherent Vulkan 2D ring buffer
    if (g_vulkan2dEnabled && Vulkan2D::Get().IsInitialized()) {
        Vulkan2D::Get().AddRect(x, y, w, h, s0, t0, s1, t1, c1, c2);
    }

    if (!g_vulkan2dBypassD3D && g_realAPI.Add2dRect) {
        ((FnAdd2dRect)g_realAPI.Add2dRect)(x, y, w, h, s0, t0, s1, t1, hTexture, c1, c2);
    }
}

static void __fastcall Hook_Add2dQuad(const float* v1, const float* v2, const float* v3, const float* v4,
                                      const float* tc1, const float* tc2, const float* tc3, const float* tc4,
                                      int hTex, uint32_t c1, uint32_t c2) {
    g_quadCount++;
    g_quadCountFrame++;

    if (g_vulkan2dEnabled && Vulkan2D::Get().IsInitialized() && v1 && v2 && v3 && v4 && tc1 && tc2 && tc3 && tc4) {
        float v[8] = { v1[0], v1[1], v2[0], v2[1], v3[0], v3[1], v4[0], v4[1] };
        float tc[8] = { tc1[0], tc1[1], tc2[0], tc2[1], tc3[0], tc3[1], tc4[0], tc4[1] };
        Vulkan2D::Get().AddQuad(v, tc, c1);
    }

    if (!g_vulkan2dBypassD3D && g_realAPI.Add2dQuad) {
        ((FnAdd2dQuad)g_realAPI.Add2dQuad)(v1, v2, v3, v4, tc1, tc2, tc3, tc4, hTex, c1, c2);
    }
}

static void __fastcall Hook_Add2dTri(const float* v1, const float* v2, const float* v3,
                                     const float* tc1, const float* tc2, const float* tc3,
                                     int hTex, uint32_t c1, uint32_t c2) {
    g_triCount++;
    g_triCountFrame++;

    if (g_vulkan2dEnabled && Vulkan2D::Get().IsInitialized() && v1 && v2 && v3 && tc1 && tc2 && tc3) {
        float v[6] = { v1[0], v1[1], v2[0], v2[1], v3[0], v3[1] };
        float tc[6] = { tc1[0], tc1[1], tc2[0], tc2[1], tc3[0], tc3[1] };
        Vulkan2D::Get().AddTri(v, tc, c1);
    }

    if (!g_vulkan2dBypassD3D && g_realAPI.Add2dTri) {
        ((FnAdd2dTri)g_realAPI.Add2dTri)(v1, v2, v3, tc1, tc2, tc3, hTex, c1, c2);
    }
}

static void __fastcall Hook_Add2dLine(const float* v1, const float* v2, const float* c1, const float* c2, int flags) {
    g_lineCount++;
    g_lineCountFrame++;

    if (g_vulkan2dEnabled && Vulkan2D::Get().IsInitialized() && v1 && v2) {
        uint32_t color = PackColorVec4(c1);
        Vulkan2D::Get().AddLine(v1[0], v1[1], v2[0], v2[1], color);
    }

    if (!g_vulkan2dBypassD3D && g_realAPI.Add2dLine) {
        ((FnAdd2dLine)g_realAPI.Add2dLine)(v1, v2, c1, c2, flags);
    }
}

static void __fastcall Hook_Add2dSmoothLine(const float* v1, const float* v2, const float* c1, const float* c2, int flags) {
    g_smoothLineCount++;
    g_smoothLineCountFrame++;

    if (g_vulkan2dEnabled && Vulkan2D::Get().IsInitialized() && v1 && v2) {
        uint32_t color = PackColorVec4(c1);
        Vulkan2D::Get().AddLine(v1[0], v1[1], v2[0], v2[1], color);
    }

    if (!g_vulkan2dBypassD3D && g_realAPI.Add2dSmoothLine) {
        ((FnAdd2dSmoothLine)g_realAPI.Add2dSmoothLine)(v1, v2, c1, c2, flags);
    }
}

static void __fastcall Hook_SetScissor(int x, int y, int w, int h) {
    if (Vulkan2D::Get().IsInitialized()) {
        Vulkan2D::Get().SetScissor(x, y, w, h);
    }
    if (g_realAPI.SetScissor) {
        ((FnSetScissor)g_realAPI.SetScissor)(x, y, w, h);
    }
}

static int __fastcall Hook_RegisterTexture(void* pBitmap, uint32_t flags) {
    int hTex = ((FnRegisterTexture)g_realAPI.RegisterTexture)(pBitmap, flags);
    uint64_t regIdx = ++g_texRegisterCount;
    if (regIdx <= 50 || (regIdx % 20 == 0)) {
        Log("[TEX_LIFECYCLE] RegisterTexture(pBitmap=0x%p, flags=0x%04X) -> Handle #%d (Total Registered: %llu)",
            pBitmap, flags, hTex, regIdx);
    }
    return hTex;
}

static void __fastcall Hook_UnregisterTexture(int hTex) {
    uint64_t unregIdx = ++g_texUnregisterCount;
    if (unregIdx <= 20 || (unregIdx % 50 == 0)) {
        Log("[TEX_LIFECYCLE] UnregisterTexture(Handle #%d) (Total Unregistered: %llu)", hTex, unregIdx);
    }
    ((FnUnregisterTexture)g_realAPI.UnregisterTexture)(hTex);
}

static void __fastcall Hook_UpdateTexture(int hTex, void* pBmp, uint32_t flags) {
    ((FnUpdateTexture)g_realAPI.UpdateTexture)(hTex, pBmp, flags);
}

// -------------------------------------------------------------------------------------------------
// Phase 4: 3D Scene Rendering, Shaders, and Model Registration Function Hooks
// -------------------------------------------------------------------------------------------------
typedef void (__fastcall *FnRenderScene)(void*);
typedef int  (__fastcall *FnRegisterModel)(void*);
typedef void (__fastcall *FnUnregisterModel)(void*);
typedef int  (__fastcall *FnRegisterVertexShader)(void*);
typedef void (__fastcall *FnUnregisterVertexShader)(void*);
typedef int  (__fastcall *FnRegisterPixelShader)(void*);
typedef void (__fastcall *FnUnregisterPixelShader)(void*);
typedef void (__fastcall *FnRegisterShaderPair)(void*, void*);

static void __fastcall Hook_RenderScene(void* pCamera) {
    KONGOR_TRACY_ZONE_SCOPED_N("K2::VidVulkan::RenderScene");
    if (g_realAPI.RenderScene) {
        ((FnRenderScene)g_realAPI.RenderScene)(pCamera);
    }
}

static int __fastcall Hook_RegisterModel(void* pModel) {
    int handle = ((FnRegisterModel)g_realAPI.RegisterModel)(pModel);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnRegisterModel(pModel, handle);
    }
    return handle;
}

static void __fastcall Hook_UnregisterModel(void* pModel) {
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnUnregisterModel(pModel);
    }
    if (g_realAPI.UnregisterModel) {
        ((FnUnregisterModel)g_realAPI.UnregisterModel)(pModel);
    }
}

static int __fastcall Hook_RegisterVertexShader(void* pShader) {
    int handle = ((FnRegisterVertexShader)g_realAPI.RegisterVertexShader)(pShader);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnRegisterVertexShader(pShader, handle);
    }
    return handle;
}

static void __fastcall Hook_UnregisterVertexShader(void* pShader) {
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnUnregisterVertexShader(pShader);
    }
    if (g_realAPI.UnregisterVertexShader) {
        ((FnUnregisterVertexShader)g_realAPI.UnregisterVertexShader)(pShader);
    }
}

static int __fastcall Hook_RegisterPixelShader(void* pShader) {
    int handle = ((FnRegisterPixelShader)g_realAPI.RegisterPixelShader)(pShader);
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnRegisterPixelShader(pShader, handle);
    }
    return handle;
}

static void __fastcall Hook_UnregisterPixelShader(void* pShader) {
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnUnregisterPixelShader(pShader);
    }
    if (g_realAPI.UnregisterPixelShader) {
        ((FnUnregisterPixelShader)g_realAPI.UnregisterPixelShader)(pShader);
    }
}

static void __fastcall Hook_RegisterShaderPair(void* pVS, void* pPS) {
    if (Vulkan3D::Get().IsInitialized()) {
        Vulkan3D::Get().OnRegisterShaderPair(pVS, pPS);
    }
    if (g_realAPI.RegisterShaderPair) {
        ((FnRegisterShaderPair)g_realAPI.RegisterShaderPair)(pVS, pPS);
    }
}

// -------------------------------------------------------------------------------------------------
// Engine Frame Pacing & Menu Throttle Elimination Hook (KERNEL32.dll!Sleep)
// -------------------------------------------------------------------------------------------------
typedef void (WINAPI *FnSleep)(DWORD);
static FnSleep g_pfnRealSleep = nullptr;

static void WINAPI Hook_Sleep(DWORD dwMilliseconds) {
    if (GetCurrentThreadId() == g_dwMainEngineThreadId) {
        // Main engine loop (CHost::Execute) frame pacing interception:
        // 1. If dwMilliseconds <= 2: Sub-frame wait in QPC loop.
        //    CRITICAL PERFORMANCE FIX: NEVER call real Sleep(0) or Sleep(1) on the main engine thread!
        //    On Windows 10/11, Sleep(0) forces an OS context switch, surrendering the remaining CPU quantum
        //    to other threads (DWM, audio, background tasks) and stalling the engine for 15-25 ms per frame.
        //    Using _mm_pause() provides hardware CPU pipeline relief at nanosecond latency without yielding
        //    the Windows OS quantum, dropping Engine delta from 24.92 ms to < 0.2 ms for locked 144 FPS.
        if (dwMilliseconds <= 2) {
            g_sleepSubFrameYields.fetch_add(1, std::memory_order_relaxed);
            _mm_pause();
            return;
        }
        // 2. If dwMilliseconds == 25: Archaic menu/console throttle -> bypass completely
        //    to allow silky smooth 144 Hz rendering on the menus.
        if (dwMilliseconds == 25) {
            g_sleep25Bypassed.fetch_add(1, std::memory_order_relaxed);
            return;
        }
    }
    // All other sleeps (or non-main threads): perform real OS sleep
    g_sleepRealCalls.fetch_add(1, std::memory_order_relaxed);
    if (g_pfnRealSleep) {
        g_pfnRealSleep(dwMilliseconds);
    } else {
        Sleep(dwMilliseconds);
    }
}

// -------------------------------------------------------------------------------------------------
// Native CModelPanel::SetAnim Deduplication & Skeletal Preservation Hook (k2_x64.dll RVA 0x94eb70)
// -------------------------------------------------------------------------------------------------
typedef void (__fastcall *FnModelPanelSetAnim)(void*, const void*);
static FnModelPanelSetAnim g_pfnRealModelPanelSetAnim = nullptr;
static uint8_t* g_pModelPanelSetAnimTrampoline = nullptr;

static inline const wchar_t* GetK2StringPtr(const void* pStr) {
    if (!pStr) return L"";
    size_t cap = *(const size_t*)((const uint8_t*)pStr + 0x18);
    return (cap > 7) ? *(const wchar_t* const*)pStr : (const wchar_t*)pStr;
}

static inline size_t GetK2StringLen(const void* pStr) {
    if (!pStr) return 0;
    return *(const size_t*)((const uint8_t*)pStr + 0x10);
}

struct PanelAnimRecord {
    std::wstring animName;
    uint32_t modelHandle;
    void* pSkeleton;
    uint64_t lastStartFrame;
};
static SRWLOCK g_panelAnimLock = SRWLOCK_INIT;
static std::unordered_map<void*, PanelAnimRecord> g_activePanelAnims;
static std::atomic<uint64_t> g_modelPanelAutoHealCount{0};

static bool IsIdleOrPreviewLoopAnimName(const wchar_t* animName) {
    if (!animName || animName[0] == L'\0') return false;
    return _wcsicmp(animName, L"idle") == 0 ||
           _wcsicmp(animName, L"store_idle") == 0 ||
           _wcsicmp(animName, L"store") == 0 ||
           _wcsicmp(animName, L"default_store_l") == 0 ||
           _wcsicmp(animName, L"default_1") == 0 ||
           _wcsicmp(animName, L"idle_1") == 0 ||
           _wcsicmp(animName, L"portrait") == 0 ||
           _wcsicmp(animName, L"stand") == 0 ||
           _wcsicmp(animName, L"walk_1") == 0;
}

static bool AutoHealModelPanelIdleLoopSeh(
    void* pPanel,
    const wchar_t* animName,
    int32_t* outStartFrame,
    int32_t* outNumFrames,
    int32_t* outOldLoopBack,
    int32_t* outOldNumLoop,
    uint8_t* outOldLoopFlag)
{
    if (!pPanel || reinterpret_cast<uintptr_t>(pPanel) < 0x10000ULL) return false;
    if (!IsIdleOrPreviewLoopAnimName(animName)) return false;

    __try {
        void* pSkeleton = *reinterpret_cast<void**>(static_cast<uint8_t*>(pPanel) + 0x338);
        if (!pSkeleton || reinterpret_cast<uintptr_t>(pSkeleton) < 0x10000ULL) return false;

        uint8_t* pAnim = *reinterpret_cast<uint8_t**>(static_cast<uint8_t*>(pSkeleton) + 0xC8);
        if (!pAnim || reinterpret_cast<uintptr_t>(pAnim) < 0x10000ULL) return false;

        // Verified against k2_x64.dll CAnim::RebuildClipBindings (0x180675340):
        // +0x90: m_iStartFrame, +0x94: m_iNumFrames, +0x98: m_iLoopBackFrame,
        // +0x9C: m_bLoop, +0xA8: m_iNumLoopFrames, +0xD0: m_iDefaultLoopBackFrame
        int32_t startFrame = *reinterpret_cast<int32_t*>(pAnim + 0x90);
        int32_t numFrames = *reinterpret_cast<int32_t*>(pAnim + 0x94);
        int32_t loopBackFrame = *reinterpret_cast<int32_t*>(pAnim + 0x98);
        uint8_t bLoop = *reinterpret_cast<uint8_t*>(pAnim + 0x9C);
        int32_t numLoopFrames = *reinterpret_cast<int32_t*>(pAnim + 0xA8);

        if (startFrame < 0 || numFrames <= 2 || numFrames > 100000) {
            return false;
        }
        if (bLoop == 0 || numLoopFrames <= 1) {
            if (outStartFrame) *outStartFrame = startFrame;
            if (outNumFrames) *outNumFrames = numFrames;
            if (outOldLoopBack) *outOldLoopBack = loopBackFrame;
            if (outOldNumLoop) *outOldNumLoop = numLoopFrames;
            if (outOldLoopFlag) *outOldLoopFlag = bLoop;

            *reinterpret_cast<uint8_t*>(pAnim + 0x9C) = 1;
            *reinterpret_cast<int32_t*>(pAnim + 0x98) = startFrame;
            *reinterpret_cast<int32_t*>(pAnim + 0xD0) = startFrame;
            *reinterpret_cast<int32_t*>(pAnim + 0xA8) = numFrames;
            return true;
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool SafeCopyK2WStringSeh(const void* pStr, wchar_t* outBuf, size_t outCap, size_t* outLen) {
    if (!outBuf || outCap == 0) return false;
    outBuf[0] = L'\0';
    if (outLen) *outLen = 0;
    if (!pStr || reinterpret_cast<uintptr_t>(pStr) < 0x10000ULL) return false;
    __try {
        size_t len = *reinterpret_cast<const size_t*>(static_cast<const uint8_t*>(pStr) + 0x10);
        size_t cap = *reinterpret_cast<const size_t*>(static_cast<const uint8_t*>(pStr) + 0x18);
        if (len > 255 || cap > 4096 || (cap > 7 && len > cap)) {
            return false;
        }
        const wchar_t* data = (cap > 7)
            ? *reinterpret_cast<const wchar_t* const*>(pStr)
            : reinterpret_cast<const wchar_t*>(pStr);
        if (!data || reinterpret_cast<uintptr_t>(data) < 0x10000ULL) return false;
        size_t copyCount = (len < outCap - 1) ? len : (outCap - 1);
        for (size_t i = 0; i < copyCount; ++i) {
            outBuf[i] = data[i];
        }
        outBuf[copyCount] = L'\0';
        if (outLen) *outLen = copyCount;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        outBuf[0] = L'\0';
        if (outLen) *outLen = 0;
        return false;
    }
}

static bool SafeReadModelPanelStateSeh(
    void* pPanel,
    const void* pNewAnim,
    uint32_t* outModelHandle,
    void** outSkeleton,
    bool* outHasActiveClip,
    wchar_t* outNewAnim,
    size_t newAnimCap,
    size_t* outNewLen,
    wchar_t* outCurAnim,
    size_t curAnimCap,
    size_t* outCurLen)
{
    if (!pPanel || !pNewAnim || reinterpret_cast<uintptr_t>(pPanel) < 0x10000ULL) return false;
    *outModelHandle = 0;
    *outSkeleton = nullptr;
    *outHasActiveClip = false;
    *outCurLen = 0;
    if (curAnimCap > 0) outCurAnim[0] = L'\0';

    if (!SafeCopyK2WStringSeh(pNewAnim, outNewAnim, newAnimCap, outNewLen)) {
        return false;
    }

    __try {
        uint32_t modelHandle = *reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(pPanel) + 0x330);
        void* pSkeleton = *reinterpret_cast<void**>(static_cast<uint8_t*>(pPanel) + 0x338);
        *outModelHandle = modelHandle;
        *outSkeleton = pSkeleton;

        if (pSkeleton != nullptr && reinterpret_cast<uintptr_t>(pSkeleton) >= 0x10000ULL) {
            void* pModel = *reinterpret_cast<void**>(static_cast<uint8_t*>(pSkeleton) + 0xc0);
            void* pClip = *reinterpret_cast<void**>(static_cast<uint8_t*>(pSkeleton) + 200);
            uint32_t startTime = *reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(pSkeleton) + 0xd8);
            if (pModel != nullptr && pClip != nullptr && startTime != 0xFFFFFFFF) {
                *outHasActiveClip = true;
                SafeCopyK2WStringSeh(static_cast<const uint8_t*>(pSkeleton) + 0x78, outCurAnim, curAnimCap, outCurLen);
            }
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static void __fastcall Hook_CModelPanel_SetAnim(void* pPanel, const void* pNewAnim) {
    if (!pPanel || !pNewAnim) return;
    g_setAnimTotalCalls.fetch_add(1, std::memory_order_relaxed);

    uint32_t modelHandle = 0;
    void* pSkeleton = nullptr;
    bool hasActiveClip = false;
    wchar_t newAnimBuf[256] = {0};
    size_t newLen = 0;
    wchar_t curAnimBuf[256] = {0};
    size_t curLen = 0;

    if (!SafeReadModelPanelStateSeh(
            pPanel, pNewAnim,
            &modelHandle, &pSkeleton, &hasActiveClip,
            newAnimBuf, 256, &newLen,
            curAnimBuf, 256, &curLen)) {
        if (g_pfnRealModelPanelSetAnim) {
            g_pfnRealModelPanelSetAnim(pPanel, pNewAnim);
        }
        return;
    }

    // Preservation: If the requested animation is empty ("") from CModelPanel::Frame
    // because anim attribute was omitted or cleared, do not call real SetAnim (which deletes the skeleton!).
    if (newLen == 0) {
        g_setAnimSkippedCalls.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    uint64_t curFrame = g_frameCount.load(std::memory_order_relaxed);
    const bool isIdleLoop = IsIdleOrPreviewLoopAnimName(newAnimBuf);
    if (hasActiveClip) {
        AcquireSRWLockExclusive(&g_panelAnimLock);
        auto it = g_activePanelAnims.find(pPanel);
        if (it != g_activePanelAnims.end()) {
            const bool sameAnimAndModel =
                it->second.modelHandle == modelHandle &&
                it->second.pSkeleton == pSkeleton &&
                it->second.animName == newAnimBuf &&
                curLen == newLen &&
                _wcsicmp(curAnimBuf, newAnimBuf) == 0;
            if (sameAnimAndModel && (isIdleLoop || curFrame <= it->second.lastStartFrame + 2)) {
                it->second.lastStartFrame = curFrame;
                ReleaseSRWLockExclusive(&g_panelAnimLock);
                g_setAnimSkippedCalls.fetch_add(1, std::memory_order_relaxed);
                return;
            }
        } else if (curLen == newLen && _wcsicmp(curAnimBuf, newAnimBuf) == 0 && isIdleLoop) {
            g_activePanelAnims[pPanel] = { newAnimBuf, modelHandle, pSkeleton, curFrame };
            ReleaseSRWLockExclusive(&g_panelAnimLock);
            g_setAnimSkippedCalls.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        ReleaseSRWLockExclusive(&g_panelAnimLock);
    }

    // Not playing yet, switching animation, or re-triggering after inactivity -> execute real SetAnim outside lock!
    uint64_t execCount = ++g_setAnimExecutedCalls;
    if (g_pfnRealModelPanelSetAnim) {
        g_pfnRealModelPanelSetAnim(pPanel, pNewAnim);
    }

    uint32_t postModelHandle = modelHandle;
    void* postSkeleton = pSkeleton;
    bool postHasClip = false;
    wchar_t postNewBuf[256] = {0};
    size_t postNewLen = 0;
    wchar_t postCurBuf[256] = {0};
    size_t postCurLen = 0;
    SafeReadModelPanelStateSeh(
        pPanel, pNewAnim,
        &postModelHandle, &postSkeleton, &postHasClip,
        postNewBuf, 256, &postNewLen,
        postCurBuf, 256, &postCurLen);

    int32_t healedStart = 0, healedNum = 0, oldLoopBack = 0, oldNumLoop = 0;
    uint8_t oldLoopFlag = 0;
    if (AutoHealModelPanelIdleLoopSeh(pPanel, newAnimBuf, &healedStart, &healedNum, &oldLoopBack, &oldNumLoop, &oldLoopFlag)) {
        uint64_t healIdx = g_modelPanelAutoHealCount.fetch_add(1, std::memory_order_relaxed) + 1;
        if (healIdx <= 64) {
            Log("[MODELPANEL_AUTOHEAL] Healed non-looping CAnim '%ls' on Panel 0x%p (ModelHandle=%u, start=%d, numFrames=%d, oldLoop=%u, oldLoopBack=%d, oldNumLoop=%d)",
                newAnimBuf, pPanel, postModelHandle, healedStart, healedNum,
                static_cast<unsigned>(oldLoopFlag), oldLoopBack, oldNumLoop);
        }
    }

    AcquireSRWLockExclusive(&g_panelAnimLock);
    g_activePanelAnims[pPanel] = { newAnimBuf, postModelHandle, postSkeleton, curFrame };
    ReleaseSRWLockExclusive(&g_panelAnimLock);

    if (execCount <= 10) {
        Log("[MODEL_PANEL_HOOK] SetAnim('%ls') EXECUTED on Panel 0x%p (ModelHandle=%u, Skeleton=0x%p, TotalExec=%llu, TotalSkip=%llu)",
            newAnimBuf, pPanel, postModelHandle, postSkeleton, execCount, g_setAnimSkippedCalls.load());
    }
}

static bool InstallModelPanelSetAnimHook(HMODULE hK2) {
    if (!hK2) return false;
    uint8_t* pTarget = (uint8_t*)hK2 + 0x94eb70;

    // Verify expected prologue bytes:
    // 48 89 5c 24 10 (mov [rsp+10h], rbx)
    // 48 89 6c 24 18 (mov [rsp+18h], rbp)
    // 48 89 74 24 20 (mov [rsp+20h], rsi)
    // 57             (push rdi)
    const uint8_t expected[16] = {
        0x48, 0x89, 0x5c, 0x24, 0x10,
        0x48, 0x89, 0x6c, 0x24, 0x18,
        0x48, 0x89, 0x74, 0x24, 0x20,
        0x57
    };
    if (memcmp(pTarget, expected, 16) != 0) {
        Log("[MODEL_PANEL_HOOK] WARNING: CModelPanel::SetAnim prologue bytes mismatch! Hook aborted.");
        return false;
    }

    // Allocate trampoline strictly W^X: PAGE_READWRITE first, then transition to PAGE_EXECUTE_READ
    g_pModelPanelSetAnimTrampoline = (uint8_t*)VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!g_pModelPanelSetAnimTrampoline) {
        Log("[MODEL_PANEL_HOOK] ERROR: Failed to allocate trampoline memory.");
        return false;
    }

    // Copy original 16 bytes into trampoline
    memcpy(g_pModelPanelSetAnimTrampoline, pTarget, 16);

    // Append 14-byte absolute jump back to pTarget + 16
    uint8_t* pJmpBack = g_pModelPanelSetAnimTrampoline + 16;
    pJmpBack[0] = 0xFF;
    pJmpBack[1] = 0x25;
    *(uint32_t*)(pJmpBack + 2) = 0; // [RIP + 0]
    *(uint64_t*)(pJmpBack + 6) = (uint64_t)(pTarget + 16);

    // Enforce W^X: transition 64-byte trampoline page from RWX (0x40) to RX (PAGE_EXECUTE_READ = 0x20).
    // Must be PAGE_EXECUTE_READ (0x20), NOT execute-only PAGE_EXECUTE (0x10), because
    // JMP QWORD PTR [RIP+0] (FF 25 00 00 00 00) reads the 8-byte target address from the trampoline page.
    DWORD oldTrampolineProtect = 0;
    if (!VirtualProtect(g_pModelPanelSetAnimTrampoline, 64, PAGE_EXECUTE_READ, &oldTrampolineProtect)) {
        Log("[MODEL_PANEL_HOOK] ERROR: VirtualProtect(PAGE_EXECUTE_READ) failed on trampoline (err=%lu). Hook aborted.", GetLastError());
        VirtualFree(g_pModelPanelSetAnimTrampoline, 0, MEM_RELEASE);
        g_pModelPanelSetAnimTrampoline = nullptr;
        return false;
    }
    FlushInstructionCache(GetCurrentProcess(), g_pModelPanelSetAnimTrampoline, 64);

    g_pfnRealModelPanelSetAnim = (FnModelPanelSetAnim)g_pModelPanelSetAnimTrampoline;

    // Overwrite pTarget with 14-byte absolute jump to Hook_CModelPanel_SetAnim + 2 NOPs
    DWORD oldProtect = 0;
    if (!VirtualProtect(pTarget, 16, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        Log("[MODEL_PANEL_HOOK] ERROR: VirtualProtect failed on pTarget.");
        return false;
    }

    pTarget[0] = 0xFF;
    pTarget[1] = 0x25;
    *(uint32_t*)(pTarget + 2) = 0; // [RIP + 0]
    *(uint64_t*)(pTarget + 6) = (uint64_t)Hook_CModelPanel_SetAnim;
    pTarget[14] = 0x90; // NOP
    pTarget[15] = 0x90; // NOP

    VirtualProtect(pTarget, 16, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), pTarget, 16);

    Log("[MODEL_PANEL_HOOK] Successfully installed CModelPanel::SetAnim deduplication detour at k2_x64.dll+0x94eb70 (trampoline=0x%p, protect=PAGE_EXECUTE_READ 0x20)",
        g_pModelPanelSetAnimTrampoline);
    return true;
}

// -------------------------------------------------------------------------------------------------
// CSkeleton::Pose Smooth Frame-Pacing & Sub-Tick Interpolation Detour (k2_x64.dll + 0x9c3a10)
// Per-skeleton 256-slot direct-mapped anchor table (zero heap allocs, zero mutex, zero per-pose QPC)
// -------------------------------------------------------------------------------------------------
typedef void (__fastcall *FnSkeletonPose)(void* pSkeleton, uint32_t uiTime, float param_2, float param_3);
static FnSkeletonPose g_pfnRealSkeletonPose = nullptr;
static uint8_t* g_pSkeletonPoseTrampoline = nullptr;
static std::atomic<uint64_t> g_skeletonPoseCalls{0};
static std::atomic<uint64_t> g_skeletonPoseInterpolatedCalls{0};
static std::atomic<uint64_t> g_skeletonPoseOffThreadCalls{0};
static uint64_t g_skeletonPoseSlotCollisions = 0;

struct SkeletonPoseAnchor {
    const void* pSkeleton;
    uint32_t    lastUiTime;
    int64_t     anchorQpc;
};

static constexpr size_t kSkeletonAnchorTableSize = 2048;
static SkeletonPoseAnchor g_skeletonAnchors[kSkeletonAnchorTableSize] = {};

void __fastcall Hook_CSkeleton_Pose(void* pSkeleton, uint32_t uiTime, float param_2, float param_3) {
    if (!pSkeleton || !g_pfnRealSkeletonPose) return;

    // Gate interpolation strictly to the K2 main render thread so the 2048-slot table is lock-free.
    const DWORD mainTid = g_mainThreadId.load(std::memory_order_relaxed);
    if (mainTid == 0 || GetCurrentThreadId() != mainTid) {
        g_skeletonPoseOffThreadCalls.fetch_add(1, std::memory_order_relaxed);
        g_pfnRealSkeletonPose(pSkeleton, uiTime, param_2, param_3);
        return;
    }

    const uint64_t totalCalls = g_skeletonPoseCalls.fetch_add(1, std::memory_order_relaxed) + 1;
    const int64_t frameQpc = g_frameStartQpc.load(std::memory_order_relaxed);
    const int64_t perfFreq = g_perfFrequency.QuadPart;
    // Fibonacci multiplicative pointer hash into 2048 slots (11-bit index) avoids stride-aligned collisions.
    const size_t slotIdx = static_cast<size_t>(
        (static_cast<uint64_t>(reinterpret_cast<uintptr_t>(pSkeleton)) * 11400714819323198485ULL) >> 53);
    SkeletonPoseAnchor& entry = g_skeletonAnchors[slotIdx];

    uint32_t targetTime = uiTime;
    if (entry.pSkeleton != pSkeleton) {
        if (entry.pSkeleton != nullptr) {
            ++g_skeletonPoseSlotCollisions;
        }
        entry.pSkeleton = pSkeleton;
        entry.lastUiTime = uiTime;
        entry.anchorQpc = frameQpc;
    } else if (uiTime != entry.lastUiTime || uiTime == 0) {
        entry.lastUiTime = uiTime;
        entry.anchorQpc = frameQpc;
    } else if (frameQpc > entry.anchorQpc && perfFreq > 0) {
        const int64_t deltaTicks = frameQpc - entry.anchorQpc;
        if (deltaTicks > 0 && deltaTicks < perfFreq) {
            const int64_t elapsedMs = (deltaTicks * 1000LL) / perfFreq;
            if (elapsedMs > 0 && elapsedMs < 50) {
                targetTime = uiTime + static_cast<uint32_t>(elapsedMs);
                g_skeletonPoseInterpolatedCalls.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    if (totalCalls == 1 || (totalCalls % 50000ULL) == 0) {
        const uint64_t interp = g_skeletonPoseInterpolatedCalls.load(std::memory_order_relaxed);
        const double interpPct = (totalCalls > 0) ? (100.0 * (double)interp / (double)totalCalls) : 0.0;
        Log("[SKELETON_POSE_HOOK] Pose(Skeleton=0x%p, uiTime=%u, smoothTime=%u, Total=%llu, Interpolated=%llu [%.2f%%], OffThread=%llu, Collisions=%llu)",
            pSkeleton, uiTime, targetTime, (unsigned long long)totalCalls, (unsigned long long)interp, interpPct,
            (unsigned long long)g_skeletonPoseOffThreadCalls.load(std::memory_order_relaxed),
            (unsigned long long)g_skeletonPoseSlotCollisions);
    }

    g_pfnRealSkeletonPose(pSkeleton, targetTime, param_2, param_3);
}

static bool InstallSkeletonPoseHook(HMODULE hK2) {
    if (!hK2) return false;
    uint8_t* pTarget = (uint8_t*)hK2 + 0x9c3a10;

    // Verify expected prologue bytes (20 bytes):
    // 48 89 5c 24 08 (mov [rsp+8], rbx)
    // 48 89 74 24 10 (mov [rsp+10h], rsi)
    // 57             (push rdi)
    // 48 83 ec 70    (sub rsp, 70h)
    // 0f 29 74 24 60 (movaps [rsp+60h], xmm6)
    const uint8_t expected[20] = {
        0x48, 0x89, 0x5c, 0x24, 0x08,
        0x48, 0x89, 0x74, 0x24, 0x10,
        0x57,
        0x48, 0x83, 0xec, 0x70,
        0x0f, 0x29, 0x74, 0x24, 0x60
    };
    if (memcmp(pTarget, expected, 20) != 0) {
        Log("[SKELETON_POSE_HOOK] WARNING: CSkeleton::Pose prologue bytes mismatch! Hook aborted.");
        return false;
    }

    // Allocate trampoline strictly W^X: PAGE_READWRITE first, then transition to PAGE_EXECUTE_READ
    g_pSkeletonPoseTrampoline = (uint8_t*)VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!g_pSkeletonPoseTrampoline) {
        Log("[SKELETON_POSE_HOOK] ERROR: Failed to allocate trampoline memory.");
        return false;
    }

    // Copy original 20 bytes into trampoline
    memcpy(g_pSkeletonPoseTrampoline, pTarget, 20);

    // Append 14-byte absolute jump back to pTarget + 20
    uint8_t* pJmpBack = g_pSkeletonPoseTrampoline + 20;
    pJmpBack[0] = 0xFF;
    pJmpBack[1] = 0x25;
    *(uint32_t*)(pJmpBack + 2) = 0; // [RIP + 0]
    *(uint64_t*)(pJmpBack + 6) = (uint64_t)(pTarget + 20);

    // Enforce W^X: transition 64-byte trampoline page from RWX (0x40) to RX (PAGE_EXECUTE_READ = 0x20).
    // Must be PAGE_EXECUTE_READ (0x20), NOT execute-only PAGE_EXECUTE (0x10), because
    // JMP QWORD PTR [RIP+0] (FF 25 00 00 00 00) reads the 8-byte target address from the trampoline page.
    DWORD oldTrampolineProtect = 0;
    if (!VirtualProtect(g_pSkeletonPoseTrampoline, 64, PAGE_EXECUTE_READ, &oldTrampolineProtect)) {
        Log("[SKELETON_POSE_HOOK] ERROR: VirtualProtect(PAGE_EXECUTE_READ) failed on trampoline (err=%lu). Hook aborted.", GetLastError());
        VirtualFree(g_pSkeletonPoseTrampoline, 0, MEM_RELEASE);
        g_pSkeletonPoseTrampoline = nullptr;
        return false;
    }
    FlushInstructionCache(GetCurrentProcess(), g_pSkeletonPoseTrampoline, 64);

    g_pfnRealSkeletonPose = (FnSkeletonPose)g_pSkeletonPoseTrampoline;

    // Overwrite pTarget with 14-byte absolute jump to Hook_CSkeleton_Pose + 6 NOPs (total 20 bytes)
    DWORD oldProtect = 0;
    if (!VirtualProtect(pTarget, 20, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        Log("[SKELETON_POSE_HOOK] ERROR: VirtualProtect failed on pTarget.");
        return false;
    }

    pTarget[0] = 0xFF;
    pTarget[1] = 0x25;
    *(uint32_t*)(pTarget + 2) = 0; // [RIP + 0]
    *(uint64_t*)(pTarget + 6) = (uint64_t)Hook_CSkeleton_Pose;
    for (int i = 14; i < 20; ++i) {
        pTarget[i] = 0x90; // NOP
    }

    VirtualProtect(pTarget, 20, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), pTarget, 20);

    Log("[SKELETON_POSE_HOOK] Successfully installed CSkeleton::Pose sub-tick interpolation detour at k2_x64.dll+0x9c3a10 (trampoline=0x%p, protect=PAGE_EXECUTE_READ 0x20)",
        g_pSkeletonPoseTrampoline);
    return true;
}

// -------------------------------------------------------------------------------------------------
// Primary Driver Entry Point: InitAPIs
// -------------------------------------------------------------------------------------------------
typedef void (__stdcall *PFN_InitAPIs)(SVidDriverAPI*, void*, void*);

extern "C" __declspec(dllexport) void __stdcall InitAPIs(SVidDriverAPI* pDriverAPI, void* pCoreEngine, void* pModManager) {
    StartAsyncLogWriter();
    if (g_perfFrequency.QuadPart == 0) {
        QueryPerformanceFrequency(&g_perfFrequency);
    }
    LARGE_INTEGER initQpc = {};
    QueryPerformanceCounter(&initQpc);
    g_frameStartQpc.store(initQpc.QuadPart, std::memory_order_relaxed);

    DWORD mainTid = GetCurrentThreadId();
    g_dwMainEngineThreadId = mainTid;
    g_mainThreadId.store(mainTid, std::memory_order_relaxed);

    Log("================================================================================");
    Log("K2 ENGINE INITIALIZING VIDEO DRIVER: vid_vulkan_x64");
    Log("[KONGOR_HOTFIX] Active patch: v5.0.2 (async Win32 log writer + per-skeleton 2048-slot Fibonacci pose interpolation + 1s high-Hz telemetry + pre-DXVK Vulkan 1.3 gate)");
    Log("[ASYNC_LOG] Background writer active (slots=%u, flushCadence=%lums, shutdownTimeout=%lums, urgentImmediateFlush=true)",
        kAsyncLogSlotCount, (unsigned long)kAsyncLogFlushIntervalMs, (unsigned long)kAsyncLogShutdownWaitMs);
    Log("pDriverAPI=0x%p, pCoreEngine=0x%p, pModManager=0x%p", pDriverAPI, pCoreEngine, pModManager);
    Log("================================================================================");
    g_pSavedDriverAPI = pDriverAPI;
    if (KONGOR_TRACY_STARTUP_IF_REQUESTED()) {
        Log("[TRACY][DEV-ONLY] Local Tracy profiler STARTED (127.0.0.1:8086, TRACY_ON_DEMAND, KONGOR_TRACY=1)");
    }
    EnsureHighPerformanceGpuPreferenceForCurrentProcess();
    EvaluateVulkan13OrFallbackOnce();

    // Hook K2 engine main thread frame pacing Sleep and CModelPanel::SetAnim in k2_x64.dll
    HMODULE hK2 = GetModuleHandleW(L"k2_x64.dll");
    if (hK2) {
        HookIAT(hK2, "KERNEL32.dll", "Sleep", (void*)Hook_Sleep, (void**)&g_pfnRealSleep);
        Log("[FRAME_PACING] Hooked KERNEL32.dll!Sleep in k2_x64.dll (Main Thread ID: %lu, Menu/Throttle sleep bypassed)", g_dwMainEngineThreadId);

        InstallModelPanelSetAnimHook(hK2);
        InstallSkeletonPoseHook(hK2);
    }

    // 1. Locate and load the stock D3D9 driver as pass-through backend
    if (!g_hRealD3D9) {
        g_hRealD3D9 = LoadLibraryW(L"vid_d3d9_x64.dll");
        if (!g_hRealD3D9) {
            Log("ERROR: Failed to load vid_d3d9_x64.dll! Error: %lu", GetLastError());
            return;
        }
        Log("Successfully loaded underlying backend: vid_d3d9_x64.dll (HMODULE: 0x%p)", g_hRealD3D9);
    }

    // 2. Install IAT hooks on vid_d3d9_x64.dll to intercept D3D9 device creation and capture 3D draw calls
    HookIAT(g_hRealD3D9, "KERNEL32.dll", "GetProcAddress", (void*)Hook_GetProcAddress, (void**)&g_pfnRealGetProcAddress);
    HookIAT(g_hRealD3D9, "d3d9.dll", "Direct3DCreate9", (void*)Hook_Direct3DCreate9, (void**)&g_pfnRealDirect3DCreate9);
    HookIAT(g_hRealD3D9, "USER32.dll", "ChangeDisplaySettingsW", (void*)Hook_ChangeDisplaySettingsW, (void**)&g_pfnRealChangeDisplaySettingsW);
    HookIAT(g_hRealD3D9, "USER32.dll", "ChangeDisplaySettingsExW", (void*)Hook_ChangeDisplaySettingsExW, (void**)&g_pfnRealChangeDisplaySettingsExW);
    HookIAT(g_hRealD3D9, "USER32.dll", "ChangeDisplaySettingsA", (void*)Hook_ChangeDisplaySettingsA, (void**)&g_pfnRealChangeDisplaySettingsA);
    HookIAT(g_hRealD3D9, "USER32.dll", "ChangeDisplaySettingsExA", (void*)Hook_ChangeDisplaySettingsExA, (void**)&g_pfnRealChangeDisplaySettingsExA);
    HookIAT(g_hRealD3D9, "USER32.dll", "EnumDisplaySettingsW", (void*)Hook_EnumDisplaySettingsW, (void**)&g_pfnRealEnumDisplaySettingsW);
    HookIAT(g_hRealD3D9, "USER32.dll", "EnumDisplaySettingsExW", (void*)Hook_EnumDisplaySettingsExW, (void**)&g_pfnRealEnumDisplaySettingsExW);

    // 3. Resolve InitAPIs on the stock driver
    PFN_InitAPIs pfnRealInit = (PFN_InitAPIs)GetProcAddress(g_hRealD3D9, "InitAPIs");
    if (!pfnRealInit) {
        Log("ERROR: Failed to find InitAPIs export in vid_d3d9_x64.dll!");
        return;
    }

    // 4. Initialize the real driver directly into pDriverAPI so all 60 slots and strings are fully valid
    pfnRealInit(pDriverAPI, pCoreEngine, pModManager);
    Log("Real D3D9 driver initialized directly into pDriverAPI.");

    // 5. Save stock function pointers to shadow table
    memcpy(&g_realAPI, pDriverAPI, sizeof(SVidDriverAPI));
    Log("Copied stock function pointers to shadow table.");

    // 6. Hook critical engine lifecycle functions
    if (g_realAPI.Init) pDriverAPI->Init = (void*)Hook_Init;
    if (g_realAPI.StartWindow) pDriverAPI->StartWindow = (void*)Hook_StartWindow;
    if (g_realAPI.SetMode) pDriverAPI->SetMode = (void*)Hook_SetMode;
    if (g_realAPI.Shutdown) pDriverAPI->Shutdown = (void*)Hook_Shutdown;
    if (g_realAPI.BeginFrame) pDriverAPI->BeginFrame = (void*)Hook_BeginFrame;
    if (g_realAPI.EndFrame) pDriverAPI->EndFrame = (void*)Hook_EndFrame;
    if (g_realAPI.GetMode) pDriverAPI->GetMode = (void*)Hook_GetMode;
    if (g_realAPI.GetCurrentMode) pDriverAPI->GetCurrentMode = (void*)Hook_GetCurrentMode;

    // 7. Hook 2D Immediate Primitive Pipeline & Takeover (Phase 2.4D)
    if (g_realAPI.Add2dRect) pDriverAPI->Add2dRect = (void*)Hook_Add2dRect;
    if (g_realAPI.Add2dQuad) pDriverAPI->Add2dQuad = (void*)Hook_Add2dQuad;
    if (g_realAPI.Add2dTri) pDriverAPI->Add2dTri = (void*)Hook_Add2dTri;
    if (g_realAPI.Add2dLine) pDriverAPI->Add2dLine = (void*)Hook_Add2dLine;
    if (g_realAPI.Add2dSmoothLine) pDriverAPI->Add2dSmoothLine = (void*)Hook_Add2dSmoothLine;
    if (g_realAPI.SetScissor) pDriverAPI->SetScissor = (void*)Hook_SetScissor;

    // 8. Hook 3D Shaders, Models, and Scene Rendering (Phase 4)
    if (g_realAPI.RenderScene) pDriverAPI->RenderScene = (void*)Hook_RenderScene;
    if (g_realAPI.RegisterModel) pDriverAPI->RegisterModel = (void*)Hook_RegisterModel;
    if (g_realAPI.UnregisterModel) pDriverAPI->UnregisterModel = (void*)Hook_UnregisterModel;
    if (g_realAPI.RegisterVertexShader) pDriverAPI->RegisterVertexShader = (void*)Hook_RegisterVertexShader;
    if (g_realAPI.UnregisterVertexShader) pDriverAPI->UnregisterVertexShader = (void*)Hook_UnregisterVertexShader;
    if (g_realAPI.RegisterPixelShader) pDriverAPI->RegisterPixelShader = (void*)Hook_RegisterPixelShader;
    if (g_realAPI.UnregisterPixelShader) pDriverAPI->UnregisterPixelShader = (void*)Hook_UnregisterPixelShader;
    if (g_realAPI.RegisterShaderPair) pDriverAPI->RegisterShaderPair = (void*)Hook_RegisterShaderPair;

    // All remaining slots (Clear/SetupCamera, RegisterModel, DrawIndexed, shaders, textures,
    // viewports, matrices, buffers) remain pointing DIRECTLY to stock vid_d3d9_x64.dll functions.
    // This guarantees 100% native performance, zero register clobbering, and zero pointer truncation.

    Log("Driver lifecycle and Phase 2.4D 2D hooks active (Takeover mode: %s, Bypass D3D: %s)!",
        g_vulkan2dEnabled ? "ENABLED" : "DISABLED",
        g_vulkan2dBypassD3D ? "YES" : "NO");
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinstDLL);
        // Enqueue into the statically-allocated ring buffer only (zero CreateThread / CRT stdio in DllMain).
        Log("vid_vulkan_x64.dll attached to process ID %lu", GetCurrentProcessId());
    } else if (fdwReason == DLL_PROCESS_DETACH) {
        // [L2-P1-02] During process termination (lpvReserved != nullptr), other threads may have
        // been terminated while holding CRT stdio or lock state. Return immediately 100% lock-free.
        if (lpvReserved != nullptr) {
            return TRUE;
        }
        // Even on explicit FreeLibrary (lpvReserved == nullptr), NEVER acquire mutexes or join
        // threads inside DllMain (OS loader-lock deadlock hazard). Drain/join happens strictly
        // in Hook_Shutdown(); here only signal the atomic stop flag wait-free.
        g_asyncLogStopRequested.store(true, std::memory_order_release);
    }
    return TRUE;
}
