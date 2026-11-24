// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: proxy_curl.cpp
// System: ProjectKongor Native Modernization
// Subsystem: kongor_curl (libcurl_x64 ABI Tagged Memory Guard Proxy)
// Description: Bridges modern libcurl 8.21.0 (TLS 1.3 + Schannel) with K2's
//              micro-heap memory architecture, preventing 0xC0000374 heap corruptions.
// ============================================================================

#include "proxy_curl.h"
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cstdio>
#include <cstdarg>

extern "C" {
    FARPROC pfn_curl_easy_duphandle = NULL;
    FARPROC pfn_curl_easy_escape = NULL;
    FARPROC pfn_curl_easy_getinfo = NULL;
    FARPROC pfn_curl_easy_header = NULL;
    FARPROC pfn_curl_easy_init = NULL;
    FARPROC pfn_curl_easy_nextheader = NULL;
    FARPROC pfn_curl_easy_option_by_id = NULL;
    FARPROC pfn_curl_easy_option_by_name = NULL;
    FARPROC pfn_curl_easy_option_next = NULL;
    FARPROC pfn_curl_easy_pause = NULL;
    FARPROC pfn_curl_easy_perform = NULL;
    FARPROC pfn_curl_easy_recv = NULL;
    FARPROC pfn_curl_easy_reset = NULL;
    FARPROC pfn_curl_easy_send = NULL;
    FARPROC pfn_curl_easy_setopt = NULL;
    FARPROC pfn_curl_easy_ssls_export = NULL;
    FARPROC pfn_curl_easy_ssls_import = NULL;
    FARPROC pfn_curl_easy_strerror = NULL;
    FARPROC pfn_curl_easy_unescape = NULL;
    FARPROC pfn_curl_easy_upkeep = NULL;
    FARPROC pfn_curl_escape = NULL;
    FARPROC pfn_curl_formadd = NULL;
    FARPROC pfn_curl_formfree = NULL;
    FARPROC pfn_curl_formget = NULL;
    FARPROC pfn_curl_free = NULL;
    FARPROC pfn_curl_getdate = NULL;
    FARPROC pfn_curl_getenv = NULL;
    FARPROC pfn_curl_global_init_mem = NULL;
    FARPROC pfn_curl_global_sslset = NULL;
    FARPROC pfn_curl_global_trace = NULL;
    FARPROC pfn_curl_maprintf = NULL;
    FARPROC pfn_curl_mfprintf = NULL;
    FARPROC pfn_curl_mime_addpart = NULL;
    FARPROC pfn_curl_mime_data = NULL;
    FARPROC pfn_curl_mime_data_cb = NULL;
    FARPROC pfn_curl_mime_encoder = NULL;
    FARPROC pfn_curl_mime_filedata = NULL;
    FARPROC pfn_curl_mime_filename = NULL;
    FARPROC pfn_curl_mime_free = NULL;
    FARPROC pfn_curl_mime_headers = NULL;
    FARPROC pfn_curl_mime_init = NULL;
    FARPROC pfn_curl_mime_name = NULL;
    FARPROC pfn_curl_mime_subparts = NULL;
    FARPROC pfn_curl_mime_type = NULL;
    FARPROC pfn_curl_mprintf = NULL;
    FARPROC pfn_curl_msnprintf = NULL;
    FARPROC pfn_curl_msprintf = NULL;
    FARPROC pfn_curl_multi_add_handle = NULL;
    FARPROC pfn_curl_multi_assign = NULL;
    FARPROC pfn_curl_multi_cleanup = NULL;
    FARPROC pfn_curl_multi_fdset = NULL;
    FARPROC pfn_curl_multi_get_handles = NULL;
    FARPROC pfn_curl_multi_get_offt = NULL;
    FARPROC pfn_curl_multi_info_read = NULL;
    FARPROC pfn_curl_multi_init = NULL;
    FARPROC pfn_curl_multi_notify_disable = NULL;
    FARPROC pfn_curl_multi_notify_enable = NULL;
    FARPROC pfn_curl_multi_perform = NULL;
    FARPROC pfn_curl_multi_poll = NULL;
    FARPROC pfn_curl_multi_remove_handle = NULL;
    FARPROC pfn_curl_multi_setopt = NULL;
    FARPROC pfn_curl_multi_socket = NULL;
    FARPROC pfn_curl_multi_socket_action = NULL;
    FARPROC pfn_curl_multi_socket_all = NULL;
    FARPROC pfn_curl_multi_strerror = NULL;
    FARPROC pfn_curl_multi_timeout = NULL;
    FARPROC pfn_curl_multi_wait = NULL;
    FARPROC pfn_curl_multi_waitfds = NULL;
    FARPROC pfn_curl_multi_wakeup = NULL;
    FARPROC pfn_curl_mvaprintf = NULL;
    FARPROC pfn_curl_mvfprintf = NULL;
    FARPROC pfn_curl_mvprintf = NULL;
    FARPROC pfn_curl_mvsnprintf = NULL;
    FARPROC pfn_curl_mvsprintf = NULL;
    FARPROC pfn_curl_pushheader_byname = NULL;
    FARPROC pfn_curl_pushheader_bynum = NULL;
    FARPROC pfn_curl_share_cleanup = NULL;
    FARPROC pfn_curl_share_init = NULL;
    FARPROC pfn_curl_share_setopt = NULL;
    FARPROC pfn_curl_share_strerror = NULL;
    FARPROC pfn_curl_slist_append = NULL;
    FARPROC pfn_curl_slist_free_all = NULL;
    FARPROC pfn_curl_strequal = NULL;
    FARPROC pfn_curl_strnequal = NULL;
    FARPROC pfn_curl_unescape = NULL;
    FARPROC pfn_curl_url = NULL;
    FARPROC pfn_curl_url_cleanup = NULL;
    FARPROC pfn_curl_url_dup = NULL;
    FARPROC pfn_curl_url_get = NULL;
    FARPROC pfn_curl_url_set = NULL;
    FARPROC pfn_curl_url_strerror = NULL;
    FARPROC pfn_curl_version = NULL;
    FARPROC pfn_curl_version_info = NULL;
    FARPROC pfn_curl_ws_meta = NULL;
    FARPROC pfn_curl_ws_recv = NULL;
    FARPROC pfn_curl_ws_send = NULL;
    FARPROC pfn_curl_ws_start_frame = NULL;
}

static constexpr uint64_t KONGOR_TAG = 0x4B4F4E474F524355ULL; // 'KONGORCU'

#pragma pack(push, 1)
struct MemHeader {
    uint64_t magic;
    uint64_t size;
};
#pragma pack(pop)

typedef int CURLcode;
typedef void *(*curl_malloc_callback)(size_t size);
typedef void (*curl_free_callback)(void *ptr);
typedef void *(*curl_realloc_callback)(void *ptr, size_t size);
typedef char *(*curl_strdup_callback)(const char *str);
typedef void *(*curl_calloc_callback)(size_t nmemb, size_t size);

typedef CURLcode (*PFN_curl_global_init_mem)(long flags,
                                             curl_malloc_callback m,
                                             curl_free_callback f,
                                             curl_realloc_callback r,
                                             curl_strdup_callback s,
                                             curl_calloc_callback c);
typedef void (*PFN_curl_global_cleanup)();
typedef void (*PFN_curl_easy_cleanup)(void *curl);

static HMODULE g_hRealCurl = NULL;
static HANDLE g_hCurlHeap = NULL;
static HANDLE g_hLogFile = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_logCs;
static bool g_logCsInit = false;

static INIT_ONCE g_curlLogInitOnce = INIT_ONCE_STATIC_INIT;
static INIT_ONCE g_curlHeapInitOnce = INIT_ONCE_STATIC_INIT;
static INIT_ONCE g_realCurlInitOnce = INIT_ONCE_STATIC_INIT;

static PFN_curl_global_init_mem g_pfnGlobalInitMem = NULL;
static PFN_curl_global_cleanup g_pfnGlobalCleanup = NULL;
static PFN_curl_easy_cleanup g_pfnEasyCleanup = NULL;

static BOOL CALLBACK InitCurlLoggingOnceCallback(PINIT_ONCE, PVOID, PVOID*) {
    InitializeCriticalSection(&g_logCs);
    g_logCsInit = true;

    wchar_t exePath[MAX_PATH] = {0};
    if (GetModuleFileNameW(NULL, exePath, MAX_PATH) > 0) {
        wchar_t* lastSlash = wcsrchr(exePath, L'\\');
        if (lastSlash) {
            *(lastSlash + 1) = L'\0';
            wchar_t logsDir[MAX_PATH] = {0};
            wcscpy_s(logsDir, MAX_PATH, exePath);
            wcscat_s(logsDir, MAX_PATH, L"logs");
            CreateDirectoryW(logsDir, NULL);

            wcscat_s(exePath, MAX_PATH, L"logs\\kongor_curl.log");
            g_hLogFile = CreateFileW(exePath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                     NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        }
    }
    return TRUE;
}

static void InitCurlLogging() {
    InitOnceExecuteOnce(&g_curlLogInitOnce, InitCurlLoggingOnceCallback, nullptr, nullptr);
}

static BOOL CALLBACK InitCurlHeapOnceCallback(PINIT_ONCE, PVOID, PVOID*) {
    g_hCurlHeap = HeapCreate(0, 0, 0);
    return g_hCurlHeap != NULL ? TRUE : FALSE;
}

static HANDLE EnsureCurlHeap() {
    InitOnceExecuteOnce(&g_curlHeapInitOnce, InitCurlHeapOnceCallback, nullptr, nullptr);
    return g_hCurlHeap;
}

static void LogGuard(const char* fmt, ...) {
    InitCurlLogging();
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    int len = vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    if (len <= 0) return;
    if (len >= (int)sizeof(buf)) {
        len = (int)sizeof(buf) - 1;
    }

    OutputDebugStringA(buf);

    if (g_logCsInit && g_hLogFile != INVALID_HANDLE_VALUE) {
        EnterCriticalSection(&g_logCs);
        SYSTEMTIME st;
        GetLocalTime(&st);
        char prefix[64];
        int plen = snprintf(prefix, sizeof(prefix), "[%02d:%02d:%02d.%03d] [TID:%lu] ",
                            st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentThreadId());
        DWORD written = 0;
        if (plen > 0) {
            WriteFile(g_hLogFile, prefix, (DWORD)plen, &written, NULL);
        }
        WriteFile(g_hLogFile, buf, (DWORD)len, &written, NULL);
        LeaveCriticalSection(&g_logCs);
    }
}

static void* safe_malloc(size_t size) {
    if (size == 0 || size > SIZE_MAX - sizeof(MemHeader)) return NULL;
    HANDLE hHeap = EnsureCurlHeap();
    if (!hHeap) return NULL;
    size_t total = size + sizeof(MemHeader);
    uint8_t* raw = (uint8_t*)HeapAlloc(hHeap, 0, total);
    if (!raw) return NULL;
    MemHeader* hdr = (MemHeader*)raw;
    hdr->magic = KONGOR_TAG;
    hdr->size = size;
    return raw + sizeof(MemHeader);
}

static void safe_free(void* ptr) {
    if (!ptr || !g_hCurlHeap) return;

    __try {
        uint8_t* raw = (uint8_t*)ptr - sizeof(MemHeader);
        MemHeader* hdr = (MemHeader*)raw;
        if (InterlockedCompareExchange64((volatile LONG64*)&hdr->magic, 0, (LONG64)KONGOR_TAG) == (LONG64)KONGOR_TAG) {
            HeapFree(g_hCurlHeap, 0, raw);
        } else {
            // Alien pointer originating from K2 micro-heap, CRT, or double free
            LogGuard("[KONGOR CURL GUARD] Filtered out alien/double-free pointer in safe_free: %p\n", ptr);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogGuard("[KONGOR CURL GUARD] Absorbed unmapped pointer exception in safe_free: %p\n", ptr);
    }
}

static void* safe_realloc(void* ptr, size_t size) {
    if (!ptr) return safe_malloc(size);
    if (size == 0) {
        safe_free(ptr);
        return NULL;
    }
    if (size > SIZE_MAX - sizeof(MemHeader)) return NULL;
    HANDLE hHeap = EnsureCurlHeap();
    if (!hHeap) return NULL;

    __try {
        uint8_t* raw = (uint8_t*)ptr - sizeof(MemHeader);
        MemHeader* hdr = (MemHeader*)raw;
        if (hdr->magic == KONGOR_TAG) {
            size_t total = size + sizeof(MemHeader);
            uint8_t* newRaw = (uint8_t*)HeapReAlloc(hHeap, 0, raw, total);
            if (!newRaw) return NULL;
            MemHeader* newHdr = (MemHeader*)newRaw;
            newHdr->size = size;
            return newRaw + sizeof(MemHeader);
        } else {
            // [L2-P1-01] Binary-safe alien pointer migration: NEVER scan for '\0' on binary HTTP payloads!
            // Bound memcpy strictly by the committed virtual memory region containing ptr and requested size.
            void* newPtr = safe_malloc(size);
            if (newPtr) {
                memset(newPtr, 0, size);
                MEMORY_BASIC_INFORMATION mbi = {0};
                if (VirtualQuery(ptr, &mbi, sizeof(mbi)) != 0 && mbi.State == MEM_COMMIT) {
                    const uint8_t* regionEnd = reinterpret_cast<const uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
                    const uint8_t* srcBytes = reinterpret_cast<const uint8_t*>(ptr);
                    if (regionEnd > srcBytes) {
                        size_t maxReadable = static_cast<size_t>(regionEnd - srcBytes);
                        size_t copyLen = (size < maxReadable) ? size : maxReadable;
                        memcpy(newPtr, srcBytes, copyLen);
                    }
                }
            }
            return newPtr;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return safe_malloc(size);
    }
}

static char* safe_strdup(const char* str) {
    if (!str) return NULL;
    size_t len = strlen(str) + 1;
    char* copy = (char*)safe_malloc(len);
    if (copy) {
        memcpy(copy, str, len);
    }
    return copy;
}

static void* safe_calloc(size_t nmemb, size_t size) {
    if (nmemb == 0 || size == 0) return NULL;
    if (nmemb > (SIZE_MAX - sizeof(MemHeader)) / size) return NULL;
    HANDLE hHeap = EnsureCurlHeap();
    if (!hHeap) return NULL;
    size_t total = nmemb * size;
    size_t allocSize = total + sizeof(MemHeader);
    uint8_t* raw = (uint8_t*)HeapAlloc(hHeap, HEAP_ZERO_MEMORY, allocSize);
    if (!raw) return NULL;
    MemHeader* hdr = (MemHeader*)raw;
    hdr->magic = KONGOR_TAG;
    hdr->size = total;
    return raw + sizeof(MemHeader);
}

static void InitializeProxies(HMODULE hMod) {
    pfn_curl_easy_duphandle = GetProcAddress(hMod, "curl_easy_duphandle");
    pfn_curl_easy_escape = GetProcAddress(hMod, "curl_easy_escape");
    pfn_curl_easy_getinfo = GetProcAddress(hMod, "curl_easy_getinfo");
    pfn_curl_easy_header = GetProcAddress(hMod, "curl_easy_header");
    pfn_curl_easy_init = GetProcAddress(hMod, "curl_easy_init");
    pfn_curl_easy_nextheader = GetProcAddress(hMod, "curl_easy_nextheader");
    pfn_curl_easy_option_by_id = GetProcAddress(hMod, "curl_easy_option_by_id");
    pfn_curl_easy_option_by_name = GetProcAddress(hMod, "curl_easy_option_by_name");
    pfn_curl_easy_option_next = GetProcAddress(hMod, "curl_easy_option_next");
    pfn_curl_easy_pause = GetProcAddress(hMod, "curl_easy_pause");
    pfn_curl_easy_perform = GetProcAddress(hMod, "curl_easy_perform");
    pfn_curl_easy_recv = GetProcAddress(hMod, "curl_easy_recv");
    pfn_curl_easy_reset = GetProcAddress(hMod, "curl_easy_reset");
    pfn_curl_easy_send = GetProcAddress(hMod, "curl_easy_send");
    pfn_curl_easy_setopt = GetProcAddress(hMod, "curl_easy_setopt");
    pfn_curl_easy_ssls_export = GetProcAddress(hMod, "curl_easy_ssls_export");
    pfn_curl_easy_ssls_import = GetProcAddress(hMod, "curl_easy_ssls_import");
    pfn_curl_easy_strerror = GetProcAddress(hMod, "curl_easy_strerror");
    pfn_curl_easy_unescape = GetProcAddress(hMod, "curl_easy_unescape");
    pfn_curl_easy_upkeep = GetProcAddress(hMod, "curl_easy_upkeep");
    pfn_curl_escape = GetProcAddress(hMod, "curl_escape");
    pfn_curl_formadd = GetProcAddress(hMod, "curl_formadd");
    pfn_curl_formfree = GetProcAddress(hMod, "curl_formfree");
    pfn_curl_formget = GetProcAddress(hMod, "curl_formget");
    pfn_curl_free = GetProcAddress(hMod, "curl_free");
    pfn_curl_getdate = GetProcAddress(hMod, "curl_getdate");
    pfn_curl_getenv = GetProcAddress(hMod, "curl_getenv");
    pfn_curl_global_init_mem = GetProcAddress(hMod, "curl_global_init_mem");
    pfn_curl_global_sslset = GetProcAddress(hMod, "curl_global_sslset");
    pfn_curl_global_trace = GetProcAddress(hMod, "curl_global_trace");
    pfn_curl_maprintf = GetProcAddress(hMod, "curl_maprintf");
    pfn_curl_mfprintf = GetProcAddress(hMod, "curl_mfprintf");
    pfn_curl_mime_addpart = GetProcAddress(hMod, "curl_mime_addpart");
    pfn_curl_mime_data = GetProcAddress(hMod, "curl_mime_data");
    pfn_curl_mime_data_cb = GetProcAddress(hMod, "curl_mime_data_cb");
    pfn_curl_mime_encoder = GetProcAddress(hMod, "curl_mime_encoder");
    pfn_curl_mime_filedata = GetProcAddress(hMod, "curl_mime_filedata");
    pfn_curl_mime_filename = GetProcAddress(hMod, "curl_mime_filename");
    pfn_curl_mime_free = GetProcAddress(hMod, "curl_mime_free");
    pfn_curl_mime_headers = GetProcAddress(hMod, "curl_mime_headers");
    pfn_curl_mime_init = GetProcAddress(hMod, "curl_mime_init");
    pfn_curl_mime_name = GetProcAddress(hMod, "curl_mime_name");
    pfn_curl_mime_subparts = GetProcAddress(hMod, "curl_mime_subparts");
    pfn_curl_mime_type = GetProcAddress(hMod, "curl_mime_type");
    pfn_curl_mprintf = GetProcAddress(hMod, "curl_mprintf");
    pfn_curl_msnprintf = GetProcAddress(hMod, "curl_msnprintf");
    pfn_curl_msprintf = GetProcAddress(hMod, "curl_msprintf");
    pfn_curl_multi_add_handle = GetProcAddress(hMod, "curl_multi_add_handle");
    pfn_curl_multi_assign = GetProcAddress(hMod, "curl_multi_assign");
    pfn_curl_multi_cleanup = GetProcAddress(hMod, "curl_multi_cleanup");
    pfn_curl_multi_fdset = GetProcAddress(hMod, "curl_multi_fdset");
    pfn_curl_multi_get_handles = GetProcAddress(hMod, "curl_multi_get_handles");
    pfn_curl_multi_get_offt = GetProcAddress(hMod, "curl_multi_get_offt");
    pfn_curl_multi_info_read = GetProcAddress(hMod, "curl_multi_info_read");
    pfn_curl_multi_init = GetProcAddress(hMod, "curl_multi_init");
    pfn_curl_multi_notify_disable = GetProcAddress(hMod, "curl_multi_notify_disable");
    pfn_curl_multi_notify_enable = GetProcAddress(hMod, "curl_multi_notify_enable");
    pfn_curl_multi_perform = GetProcAddress(hMod, "curl_multi_perform");
    pfn_curl_multi_poll = GetProcAddress(hMod, "curl_multi_poll");
    pfn_curl_multi_remove_handle = GetProcAddress(hMod, "curl_multi_remove_handle");
    pfn_curl_multi_setopt = GetProcAddress(hMod, "curl_multi_setopt");
    pfn_curl_multi_socket = GetProcAddress(hMod, "curl_multi_socket");
    pfn_curl_multi_socket_action = GetProcAddress(hMod, "curl_multi_socket_action");
    pfn_curl_multi_socket_all = GetProcAddress(hMod, "curl_multi_socket_all");
    pfn_curl_multi_strerror = GetProcAddress(hMod, "curl_multi_strerror");
    pfn_curl_multi_timeout = GetProcAddress(hMod, "curl_multi_timeout");
    pfn_curl_multi_wait = GetProcAddress(hMod, "curl_multi_wait");
    pfn_curl_multi_waitfds = GetProcAddress(hMod, "curl_multi_waitfds");
    pfn_curl_multi_wakeup = GetProcAddress(hMod, "curl_multi_wakeup");
    pfn_curl_mvaprintf = GetProcAddress(hMod, "curl_mvaprintf");
    pfn_curl_mvfprintf = GetProcAddress(hMod, "curl_mvfprintf");
    pfn_curl_mvprintf = GetProcAddress(hMod, "curl_mvprintf");
    pfn_curl_mvsnprintf = GetProcAddress(hMod, "curl_mvsnprintf");
    pfn_curl_mvsprintf = GetProcAddress(hMod, "curl_mvsprintf");
    pfn_curl_pushheader_byname = GetProcAddress(hMod, "curl_pushheader_byname");
    pfn_curl_pushheader_bynum = GetProcAddress(hMod, "curl_pushheader_bynum");
    pfn_curl_share_cleanup = GetProcAddress(hMod, "curl_share_cleanup");
    pfn_curl_share_init = GetProcAddress(hMod, "curl_share_init");
    pfn_curl_share_setopt = GetProcAddress(hMod, "curl_share_setopt");
    pfn_curl_share_strerror = GetProcAddress(hMod, "curl_share_strerror");
    pfn_curl_slist_append = GetProcAddress(hMod, "curl_slist_append");
    pfn_curl_slist_free_all = GetProcAddress(hMod, "curl_slist_free_all");
    pfn_curl_strequal = GetProcAddress(hMod, "curl_strequal");
    pfn_curl_strnequal = GetProcAddress(hMod, "curl_strnequal");
    pfn_curl_unescape = GetProcAddress(hMod, "curl_unescape");
    pfn_curl_url = GetProcAddress(hMod, "curl_url");
    pfn_curl_url_cleanup = GetProcAddress(hMod, "curl_url_cleanup");
    pfn_curl_url_dup = GetProcAddress(hMod, "curl_url_dup");
    pfn_curl_url_get = GetProcAddress(hMod, "curl_url_get");
    pfn_curl_url_set = GetProcAddress(hMod, "curl_url_set");
    pfn_curl_url_strerror = GetProcAddress(hMod, "curl_url_strerror");
    pfn_curl_version = GetProcAddress(hMod, "curl_version");
    pfn_curl_version_info = GetProcAddress(hMod, "curl_version_info");
    pfn_curl_ws_meta = GetProcAddress(hMod, "curl_ws_meta");
    pfn_curl_ws_recv = GetProcAddress(hMod, "curl_ws_recv");
    pfn_curl_ws_send = GetProcAddress(hMod, "curl_ws_send");
    pfn_curl_ws_start_frame = GetProcAddress(hMod, "curl_ws_start_frame");

    g_pfnGlobalInitMem = (PFN_curl_global_init_mem)GetProcAddress(hMod, "curl_global_init_mem");
    g_pfnGlobalCleanup = (PFN_curl_global_cleanup)GetProcAddress(hMod, "curl_global_cleanup");
    g_pfnEasyCleanup = (PFN_curl_easy_cleanup)GetProcAddress(hMod, "curl_easy_cleanup");
}

static BOOL CALLBACK InitRealCurlOnceCallback(PINIT_ONCE, PVOID, PVOID*) {
    EnsureCurlHeap();

    wchar_t selfPath[MAX_PATH] = {0};
    HMODULE hSelf = NULL;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)InitRealCurlOnceCallback, &hSelf);
    if (hSelf && GetModuleFileNameW(hSelf, selfPath, MAX_PATH) > 0) {
        wchar_t* lastSlash = wcsrchr(selfPath, L'\\');
        if (lastSlash) {
            *(lastSlash + 1) = L'\0';
            wcscat_s(selfPath, MAX_PATH, L"libcurl_real_x64.dll");
            g_hRealCurl = LoadLibraryW(selfPath);
        }
    }

    if (!g_hRealCurl) {
        g_hRealCurl = LoadLibraryA("libcurl_real_x64.dll");
    }

    if (g_hRealCurl) {
        InitializeProxies(g_hRealCurl);
        LogGuard("[KONGOR CURL GUARD] libcurl_real_x64.dll loaded successfully with Isolated Win32 Heap.\n");
    } else {
        LogGuard("[KONGOR CURL GUARD] Error: Failed to load libcurl_real_x64.dll!\n");
    }
    return TRUE;
}

static void EnsureRealCurlLoaded() {
    InitOnceExecuteOnce(&g_realCurlInitOnce, InitRealCurlOnceCallback, nullptr, nullptr);
}

extern "C" {

CURLcode curl_global_init(long flags) {
    EnsureRealCurlLoaded();
    LogGuard("[KONGOR CURL GUARD] curl_global_init(flags=%ld) called. Binding Isolated Win32 Heap allocators.\n", flags);
    if (g_pfnGlobalInitMem) {
        return g_pfnGlobalInitMem(flags, safe_malloc, safe_free, safe_realloc, safe_strdup, safe_calloc);
    }
    return 0;
}

void curl_global_cleanup() {
    LogGuard("[KONGOR CURL GUARD] curl_global_cleanup() called.\n");
    if (g_pfnGlobalCleanup) {
        g_pfnGlobalCleanup();
    }
}

void curl_easy_cleanup(void *curl) {
    if (!curl) return;
    EnsureRealCurlLoaded();
    LogGuard("[KONGOR CURL GUARD] curl_easy_cleanup(%p) starting.\n", curl);
    if (g_pfnEasyCleanup) {
        __try {
            g_pfnEasyCleanup(curl);
            LogGuard("[KONGOR CURL GUARD] curl_easy_cleanup(%p) finished cleanly.\n", curl);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            LogGuard("[KONGOR CURL GUARD] Caught and neutralized exception during curl_easy_cleanup(%p).\n", curl);
        }
    }
}

__declspec(dllexport) int Kongor_CurlGuard_SelfTestBinaryRealloc() {
    const uint8_t binaryPayload[8] = { 0xDE, 0x00, 0xAD, 0x00, 0xBE, 0xEF, 0x00, 0x42 };

    // 1. Tagged Kongor heap block reallocation with embedded null bytes
    void* pTagged = safe_malloc(sizeof(binaryPayload));
    if (!pTagged) return 0;
    memcpy(pTagged, binaryPayload, sizeof(binaryPayload));
    void* pGrown = safe_realloc(pTagged, 32);
    if (!pGrown) {
        safe_free(pTagged);
        return 0;
    }
    bool taggedOk = (memcmp(pGrown, binaryPayload, sizeof(binaryPayload)) == 0);
    safe_free(pGrown);
    if (!taggedOk) return 0;

    // 2. Alien (untagged) binary buffer migration with embedded null bytes
    alignas(16) uint8_t alienBlock[32] = {0};
    memcpy(alienBlock + 16, binaryPayload, sizeof(binaryPayload));
    void* pMigrated = safe_realloc(alienBlock + 16, 16);
    if (!pMigrated) return 0;
    bool alienOk = (memcmp(pMigrated, binaryPayload, sizeof(binaryPayload)) == 0);
    safe_free(pMigrated);
    return alienOk ? 1 : 0;
}

} // extern "C"

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            DisableThreadLibraryCalls(hModule);
            EnsureRealCurlLoaded();
            break;
        case DLL_PROCESS_DETACH:
            // [L2-P1-02] When lpReserved != nullptr, the process is terminating under the OS loader lock;
            // return immediately without HeapDestroy / FreeLibrary / DeleteCriticalSection / FlushFileBuffers.
            if (lpReserved != nullptr) {
                return TRUE;
            }
            if (g_hCurlHeap) {
                HeapDestroy(g_hCurlHeap);
                g_hCurlHeap = NULL;
            }
            if (g_hRealCurl) {
                FreeLibrary(g_hRealCurl);
                g_hRealCurl = NULL;
            }
            if (g_hLogFile != INVALID_HANDLE_VALUE) {
                FlushFileBuffers(g_hLogFile);
                CloseHandle(g_hLogFile);
                g_hLogFile = INVALID_HANDLE_VALUE;
            }
            if (g_logCsInit) {
                DeleteCriticalSection(&g_logCs);
                g_logCsInit = false;
            }
            break;
    }
    return TRUE;
}
