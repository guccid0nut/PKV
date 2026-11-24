// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: proxy_zlib.cpp
// System: ProjectKongor Native Modernization
// Subsystem: kongor_zlib (High-Speed libdeflate SIMD Drop-in for zlibwapi_x64.dll)
// ============================================================================

#include "proxy_zlib.h"
#include "libdeflate.h"
#include <cstdio>
#include <cstdarg>
#include <atomic>
#include <mutex>
#include <chrono>
#include <string>

HMODULE g_hRealZlib = NULL;
static FILE* g_pLogFile = nullptr;
static wchar_t g_logFilePath[MAX_PATH] = {0};
static std::mutex g_logMutex;

// Performance & Telemetry Counters
static std::atomic<uint64_t> g_uncompressCalls{0};
static std::atomic<uint64_t> g_uncompressLibdeflateHits{0};
static std::atomic<uint64_t> g_uncompressFallbacks{0};
static std::atomic<uint64_t> g_uncompressBytesIn{0};
static std::atomic<uint64_t> g_uncompressBytesOut{0};
static std::atomic<uint64_t> g_uncompressTotalMicroseconds{0};
static std::atomic<uint64_t> g_crc32Calls{0};
static std::atomic<uint64_t> g_adler32Calls{0};
static std::atomic<uint64_t> g_inflateStreamsActive{0};

void Log(const char* format, ...) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_pLogFile) {
        wchar_t modulePath[MAX_PATH] = {0};
        HMODULE hSelf = GetModuleHandleW(L"zlibwapi_x64.dll");
        if (hSelf) {
            GetModuleFileNameW(hSelf, modulePath, MAX_PATH);
            wchar_t* pSlash = wcsrchr(modulePath, L'\\');
            if (pSlash) {
                *(pSlash + 1) = L'\0';
                wcscat_s(modulePath, L"zlibwapi_proxy.log");
                g_pLogFile = _wfopen(modulePath, L"a");
            }
        }
    }

    char buffer[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);

    SYSTEMTIME st;
    GetLocalTime(&st);
    if (g_pLogFile) {
        fprintf(g_pLogFile, "[%02d:%02d:%02d.%03d] [KONGOR ZLIB] %s\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, buffer);
        if (strstr(buffer, "ERR") || strstr(buffer, "CRITICAL") || strstr(buffer, "WARNING")) {
            fflush(g_pLogFile);
        }
    }
}

extern "C" {
    FARPROC pfn_adler32 = NULL;
    FARPROC pfn_compress = NULL;
    FARPROC pfn_crc32 = NULL;
    FARPROC pfn_deflate = NULL;
    FARPROC pfn_deflateCopy = NULL;
    FARPROC pfn_deflateEnd = NULL;
    FARPROC pfn_deflateInit2_ = NULL;
    FARPROC pfn_deflateInit_ = NULL;
    FARPROC pfn_deflateParams = NULL;
    FARPROC pfn_deflateReset = NULL;
    FARPROC pfn_deflateSetDictionary = NULL;
    FARPROC pfn_gzclose = NULL;
    FARPROC pfn_gzdopen = NULL;
    FARPROC pfn_gzerror = NULL;
    FARPROC pfn_gzflush = NULL;
    FARPROC pfn_gzopen = NULL;
    FARPROC pfn_gzread = NULL;
    FARPROC pfn_gzwrite = NULL;
    FARPROC pfn_inflate = NULL;
    FARPROC pfn_inflateEnd = NULL;
    FARPROC pfn_inflateInit2_ = NULL;
    FARPROC pfn_inflateInit_ = NULL;
    FARPROC pfn_inflateReset = NULL;
    FARPROC pfn_inflateSetDictionary = NULL;
    FARPROC pfn_inflateSync = NULL;
    FARPROC pfn_uncompress = NULL;
    FARPROC pfn_zlibVersion = NULL;
    FARPROC pfn_gzprintf = NULL;
    FARPROC pfn_gzputc = NULL;
    FARPROC pfn_gzgetc = NULL;
    FARPROC pfn_gzseek = NULL;
    FARPROC pfn_gzrewind = NULL;
    FARPROC pfn_gztell = NULL;
    FARPROC pfn_gzeof = NULL;
    FARPROC pfn_gzsetparams = NULL;
    FARPROC pfn_zError = NULL;
    FARPROC pfn_inflateSyncPoint = NULL;
    FARPROC pfn_get_crc_table = NULL;
    FARPROC pfn_compress2 = NULL;
    FARPROC pfn_gzputs = NULL;
    FARPROC pfn_gzgets = NULL;
    FARPROC pfn_inflateCopy = NULL;
    FARPROC pfn_inflateBackInit_ = NULL;
    FARPROC pfn_inflateBack = NULL;
    FARPROC pfn_inflateBackEnd = NULL;
    FARPROC pfn_compressBound = NULL;
    FARPROC pfn_deflateBound = NULL;
    FARPROC pfn_gzclearerr = NULL;
    FARPROC pfn_gzungetc = NULL;
    FARPROC pfn_zlibCompileFlags = NULL;
    FARPROC pfn_deflatePrime = NULL;
    FARPROC pfn_deflatePending = NULL;
    FARPROC pfn_unzOpen = NULL;
    FARPROC pfn_unzClose = NULL;
    FARPROC pfn_unzGetGlobalInfo = NULL;
    FARPROC pfn_unzGetCurrentFileInfo = NULL;
    FARPROC pfn_unzGoToFirstFile = NULL;
    FARPROC pfn_unzGoToNextFile = NULL;
    FARPROC pfn_unzOpenCurrentFile = NULL;
    FARPROC pfn_unzReadCurrentFile = NULL;
    FARPROC pfn_unzOpenCurrentFile3 = NULL;
    FARPROC pfn_unztell = NULL;
    FARPROC pfn_unzeof = NULL;
    FARPROC pfn_unzCloseCurrentFile = NULL;
    FARPROC pfn_unzGetGlobalComment = NULL;
    FARPROC pfn_unzStringFileNameCompare = NULL;
    FARPROC pfn_unzLocateFile = NULL;
    FARPROC pfn_unzGetLocalExtrafield = NULL;
    FARPROC pfn_unzOpen2 = NULL;
    FARPROC pfn_unzOpenCurrentFile2 = NULL;
    FARPROC pfn_unzOpenCurrentFilePassword = NULL;
    FARPROC pfn_zipOpen = NULL;
    FARPROC pfn_zipOpenNewFileInZip = NULL;
    FARPROC pfn_zipWriteInFileInZip = NULL;
    FARPROC pfn_zipCloseFileInZip = NULL;
    FARPROC pfn_zipClose = NULL;
    FARPROC pfn_zipOpenNewFileInZip2 = NULL;
    FARPROC pfn_zipCloseFileInZipRaw = NULL;
    FARPROC pfn_zipOpen2 = NULL;
    FARPROC pfn_zipOpenNewFileInZip3 = NULL;
    FARPROC pfn_unzGetFilePos = NULL;
    FARPROC pfn_unzGoToFilePos = NULL;
    FARPROC pfn_fill_win32_filefunc = NULL;
    FARPROC pfn_fill_win32_filefunc64 = NULL;
    FARPROC pfn_fill_win32_filefunc64A = NULL;
    FARPROC pfn_fill_win32_filefunc64W = NULL;
    FARPROC pfn_unzOpen64 = NULL;
    FARPROC pfn_unzOpen2_64 = NULL;
    FARPROC pfn_unzGetGlobalInfo64 = NULL;
    FARPROC pfn_unzGetCurrentFileInfo64 = NULL;
    FARPROC pfn_unzGetCurrentFileZStreamPos64 = NULL;
    FARPROC pfn_unztell64 = NULL;
    FARPROC pfn_unzGetFilePos64 = NULL;
    FARPROC pfn_unzGoToFilePos64 = NULL;
    FARPROC pfn_zipOpen64 = NULL;
    FARPROC pfn_zipOpen2_64 = NULL;
    FARPROC pfn_zipOpenNewFileInZip64 = NULL;
    FARPROC pfn_zipOpenNewFileInZip2_64 = NULL;
    FARPROC pfn_zipOpenNewFileInZip3_64 = NULL;
    FARPROC pfn_zipOpenNewFileInZip4_64 = NULL;
    FARPROC pfn_zipCloseFileInZipRaw64 = NULL;
    FARPROC pfn_adler32_combine = NULL;
    FARPROC pfn_crc32_combine = NULL;
    FARPROC pfn_deflateSetHeader = NULL;
    FARPROC pfn_deflateTune = NULL;
    FARPROC pfn_gzbuffer = NULL;
    FARPROC pfn_gzclose_r = NULL;
    FARPROC pfn_gzclose_w = NULL;
    FARPROC pfn_gzdirect = NULL;
    FARPROC pfn_gzoffset = NULL;
    FARPROC pfn_inflateGetHeader = NULL;
    FARPROC pfn_inflateMark = NULL;
    FARPROC pfn_inflatePrime = NULL;
    FARPROC pfn_inflateReset2 = NULL;
    FARPROC pfn_inflateUndermine = NULL;
    FARPROC pfn_gzgetc_ = NULL;
    FARPROC pfn_inflateResetKeep = NULL;
    FARPROC pfn_deflateResetKeep = NULL;
    FARPROC pfn_gzopen_w = NULL;
    FARPROC pfn_inflateGetDictionary = NULL;
    FARPROC pfn_gzvprintf = NULL;
    FARPROC pfn_inflateCodesUsed = NULL;
    FARPROC pfn_inflateValidate = NULL;
    FARPROC pfn_uncompress2 = NULL;
    FARPROC pfn_gzfread = NULL;
    FARPROC pfn_gzfwrite = NULL;
    FARPROC pfn_deflateGetDictionary = NULL;
    FARPROC pfn_adler32_z = NULL;
    FARPROC pfn_crc32_z = NULL;
    FARPROC pfn_crc32_combine_gen = NULL;
    FARPROC pfn_crc32_combine_gen64 = NULL;
    FARPROC pfn_crc32_combine_op = NULL;
}

static void InitializeZlibProxies(HMODULE hMod) {
    if (!hMod) return;
    pfn_adler32 = GetProcAddress(hMod, "adler32");
    pfn_compress = GetProcAddress(hMod, "compress");
    pfn_crc32 = GetProcAddress(hMod, "crc32");
    pfn_deflate = GetProcAddress(hMod, "deflate");
    pfn_deflateCopy = GetProcAddress(hMod, "deflateCopy");
    pfn_deflateEnd = GetProcAddress(hMod, "deflateEnd");
    pfn_deflateInit2_ = GetProcAddress(hMod, "deflateInit2_");
    pfn_deflateInit_ = GetProcAddress(hMod, "deflateInit_");
    pfn_deflateParams = GetProcAddress(hMod, "deflateParams");
    pfn_deflateReset = GetProcAddress(hMod, "deflateReset");
    pfn_deflateSetDictionary = GetProcAddress(hMod, "deflateSetDictionary");
    pfn_gzclose = GetProcAddress(hMod, "gzclose");
    pfn_gzdopen = GetProcAddress(hMod, "gzdopen");
    pfn_gzerror = GetProcAddress(hMod, "gzerror");
    pfn_gzflush = GetProcAddress(hMod, "gzflush");
    pfn_gzopen = GetProcAddress(hMod, "gzopen");
    pfn_gzread = GetProcAddress(hMod, "gzread");
    pfn_gzwrite = GetProcAddress(hMod, "gzwrite");
    pfn_inflate = GetProcAddress(hMod, "inflate");
    pfn_inflateEnd = GetProcAddress(hMod, "inflateEnd");
    pfn_inflateInit2_ = GetProcAddress(hMod, "inflateInit2_");
    pfn_inflateInit_ = GetProcAddress(hMod, "inflateInit_");
    pfn_inflateReset = GetProcAddress(hMod, "inflateReset");
    pfn_inflateSetDictionary = GetProcAddress(hMod, "inflateSetDictionary");
    pfn_inflateSync = GetProcAddress(hMod, "inflateSync");
    pfn_uncompress = GetProcAddress(hMod, "uncompress");
    pfn_zlibVersion = GetProcAddress(hMod, "zlibVersion");
    pfn_gzprintf = GetProcAddress(hMod, "gzprintf");
    pfn_gzputc = GetProcAddress(hMod, "gzputc");
    pfn_gzgetc = GetProcAddress(hMod, "gzgetc");
    pfn_gzseek = GetProcAddress(hMod, "gzseek");
    pfn_gzrewind = GetProcAddress(hMod, "gzrewind");
    pfn_gztell = GetProcAddress(hMod, "gztell");
    pfn_gzeof = GetProcAddress(hMod, "gzeof");
    pfn_gzsetparams = GetProcAddress(hMod, "gzsetparams");
    pfn_zError = GetProcAddress(hMod, "zError");
    pfn_inflateSyncPoint = GetProcAddress(hMod, "inflateSyncPoint");
    pfn_get_crc_table = GetProcAddress(hMod, "get_crc_table");
    pfn_compress2 = GetProcAddress(hMod, "compress2");
    pfn_gzputs = GetProcAddress(hMod, "gzputs");
    pfn_gzgets = GetProcAddress(hMod, "gzgets");
    pfn_inflateCopy = GetProcAddress(hMod, "inflateCopy");
    pfn_inflateBackInit_ = GetProcAddress(hMod, "inflateBackInit_");
    pfn_inflateBack = GetProcAddress(hMod, "inflateBack");
    pfn_inflateBackEnd = GetProcAddress(hMod, "inflateBackEnd");
    pfn_compressBound = GetProcAddress(hMod, "compressBound");
    pfn_deflateBound = GetProcAddress(hMod, "deflateBound");
    pfn_gzclearerr = GetProcAddress(hMod, "gzclearerr");
    pfn_gzungetc = GetProcAddress(hMod, "gzungetc");
    pfn_zlibCompileFlags = GetProcAddress(hMod, "zlibCompileFlags");
    pfn_deflatePrime = GetProcAddress(hMod, "deflatePrime");
    pfn_deflatePending = GetProcAddress(hMod, "deflatePending");
    pfn_unzOpen = GetProcAddress(hMod, "unzOpen");
    pfn_unzClose = GetProcAddress(hMod, "unzClose");
    pfn_unzGetGlobalInfo = GetProcAddress(hMod, "unzGetGlobalInfo");
    pfn_unzGetCurrentFileInfo = GetProcAddress(hMod, "unzGetCurrentFileInfo");
    pfn_unzGoToFirstFile = GetProcAddress(hMod, "unzGoToFirstFile");
    pfn_unzGoToNextFile = GetProcAddress(hMod, "unzGoToNextFile");
    pfn_unzOpenCurrentFile = GetProcAddress(hMod, "unzOpenCurrentFile");
    pfn_unzReadCurrentFile = GetProcAddress(hMod, "unzReadCurrentFile");
    pfn_unzOpenCurrentFile3 = GetProcAddress(hMod, "unzOpenCurrentFile3");
    pfn_unztell = GetProcAddress(hMod, "unztell");
    pfn_unzeof = GetProcAddress(hMod, "unzeof");
    pfn_unzCloseCurrentFile = GetProcAddress(hMod, "unzCloseCurrentFile");
    pfn_unzGetGlobalComment = GetProcAddress(hMod, "unzGetGlobalComment");
    pfn_unzStringFileNameCompare = GetProcAddress(hMod, "unzStringFileNameCompare");
    pfn_unzLocateFile = GetProcAddress(hMod, "unzLocateFile");
    pfn_unzGetLocalExtrafield = GetProcAddress(hMod, "unzGetLocalExtrafield");
    pfn_unzOpen2 = GetProcAddress(hMod, "unzOpen2");
    pfn_unzOpenCurrentFile2 = GetProcAddress(hMod, "unzOpenCurrentFile2");
    pfn_unzOpenCurrentFilePassword = GetProcAddress(hMod, "unzOpenCurrentFilePassword");
    pfn_zipOpen = GetProcAddress(hMod, "zipOpen");
    pfn_zipOpenNewFileInZip = GetProcAddress(hMod, "zipOpenNewFileInZip");
    pfn_zipWriteInFileInZip = GetProcAddress(hMod, "zipWriteInFileInZip");
    pfn_zipCloseFileInZip = GetProcAddress(hMod, "zipCloseFileInZip");
    pfn_zipClose = GetProcAddress(hMod, "zipClose");
    pfn_zipOpenNewFileInZip2 = GetProcAddress(hMod, "zipOpenNewFileInZip2");
    pfn_zipCloseFileInZipRaw = GetProcAddress(hMod, "zipCloseFileInZipRaw");
    pfn_zipOpen2 = GetProcAddress(hMod, "zipOpen2");
    pfn_zipOpenNewFileInZip3 = GetProcAddress(hMod, "zipOpenNewFileInZip3");
    pfn_unzGetFilePos = GetProcAddress(hMod, "unzGetFilePos");
    pfn_unzGoToFilePos = GetProcAddress(hMod, "unzGoToFilePos");
    pfn_fill_win32_filefunc = GetProcAddress(hMod, "fill_win32_filefunc");
    pfn_fill_win32_filefunc64 = GetProcAddress(hMod, "fill_win32_filefunc64");
    pfn_fill_win32_filefunc64A = GetProcAddress(hMod, "fill_win32_filefunc64A");
    pfn_fill_win32_filefunc64W = GetProcAddress(hMod, "fill_win32_filefunc64W");
    pfn_unzOpen64 = GetProcAddress(hMod, "unzOpen64");
    pfn_unzOpen2_64 = GetProcAddress(hMod, "unzOpen2_64");
    pfn_unzGetGlobalInfo64 = GetProcAddress(hMod, "unzGetGlobalInfo64");
    pfn_unzGetCurrentFileInfo64 = GetProcAddress(hMod, "unzGetCurrentFileInfo64");
    pfn_unzGetCurrentFileZStreamPos64 = GetProcAddress(hMod, "unzGetCurrentFileZStreamPos64");
    pfn_unztell64 = GetProcAddress(hMod, "unztell64");
    pfn_unzGetFilePos64 = GetProcAddress(hMod, "unzGetFilePos64");
    pfn_unzGoToFilePos64 = GetProcAddress(hMod, "unzGoToFilePos64");
    pfn_zipOpen64 = GetProcAddress(hMod, "zipOpen64");
    pfn_zipOpen2_64 = GetProcAddress(hMod, "zipOpen2_64");
    pfn_zipOpenNewFileInZip64 = GetProcAddress(hMod, "zipOpenNewFileInZip64");
    pfn_zipOpenNewFileInZip2_64 = GetProcAddress(hMod, "zipOpenNewFileInZip2_64");
    pfn_zipOpenNewFileInZip3_64 = GetProcAddress(hMod, "zipOpenNewFileInZip3_64");
    pfn_zipOpenNewFileInZip4_64 = GetProcAddress(hMod, "zipOpenNewFileInZip4_64");
    pfn_zipCloseFileInZipRaw64 = GetProcAddress(hMod, "zipCloseFileInZipRaw64");
    pfn_adler32_combine = GetProcAddress(hMod, "adler32_combine");
    pfn_crc32_combine = GetProcAddress(hMod, "crc32_combine");
    pfn_deflateSetHeader = GetProcAddress(hMod, "deflateSetHeader");
    pfn_deflateTune = GetProcAddress(hMod, "deflateTune");
    pfn_gzbuffer = GetProcAddress(hMod, "gzbuffer");
    pfn_gzclose_r = GetProcAddress(hMod, "gzclose_r");
    pfn_gzclose_w = GetProcAddress(hMod, "gzclose_w");
    pfn_gzdirect = GetProcAddress(hMod, "gzdirect");
    pfn_gzoffset = GetProcAddress(hMod, "gzoffset");
    pfn_inflateGetHeader = GetProcAddress(hMod, "inflateGetHeader");
    pfn_inflateMark = GetProcAddress(hMod, "inflateMark");
    pfn_inflatePrime = GetProcAddress(hMod, "inflatePrime");
    pfn_inflateReset2 = GetProcAddress(hMod, "inflateReset2");
    pfn_inflateUndermine = GetProcAddress(hMod, "inflateUndermine");
    pfn_gzgetc_ = GetProcAddress(hMod, "gzgetc_");
    pfn_inflateResetKeep = GetProcAddress(hMod, "inflateResetKeep");
    pfn_deflateResetKeep = GetProcAddress(hMod, "deflateResetKeep");
    pfn_gzopen_w = GetProcAddress(hMod, "gzopen_w");
    pfn_inflateGetDictionary = GetProcAddress(hMod, "inflateGetDictionary");
    pfn_gzvprintf = GetProcAddress(hMod, "gzvprintf");
    pfn_inflateCodesUsed = GetProcAddress(hMod, "inflateCodesUsed");
    pfn_inflateValidate = GetProcAddress(hMod, "inflateValidate");
    pfn_uncompress2 = GetProcAddress(hMod, "uncompress2");
    pfn_gzfread = GetProcAddress(hMod, "gzfread");
    pfn_gzfwrite = GetProcAddress(hMod, "gzfwrite");
    pfn_deflateGetDictionary = GetProcAddress(hMod, "deflateGetDictionary");
    pfn_adler32_z = GetProcAddress(hMod, "adler32_z");
    pfn_crc32_z = GetProcAddress(hMod, "crc32_z");
    pfn_crc32_combine_gen = GetProcAddress(hMod, "crc32_combine_gen");
    pfn_crc32_combine_gen64 = GetProcAddress(hMod, "crc32_combine_gen64");
    pfn_crc32_combine_op = GetProcAddress(hMod, "crc32_combine_op");
}

static thread_local struct libdeflate_decompressor* t_decompressor = nullptr;

static struct libdeflate_decompressor* GetThreadDecompressor() {
    if (!t_decompressor) {
        t_decompressor = libdeflate_alloc_decompressor();
        if (!t_decompressor) {
            Log("ERROR: Failed to allocate libdeflate_decompressor for ThreadID %lu", GetCurrentThreadId());
        }
    }
    return t_decompressor;
}

// ============================================================================
// INTERCEPTED FAST-PATH DECOMPRESSION (libdeflate AVX2 / BMI2)
// ============================================================================

extern "C" int Proxy_uncompress(Bytef *dest, uLongf *destLen, const Bytef *source, uLong sourceLen) {
    if (!dest || !destLen || !source || sourceLen == 0) {
        if (pfn_uncompress) {
            return ((int(*)(Bytef*, uLongf*, const Bytef*, uLong))pfn_uncompress)(dest, destLen, source, sourceLen);
        }
        return Z_DATA_ERROR;
    }

    struct libdeflate_decompressor* decompressor = GetThreadDecompressor();
    if (decompressor) {
        size_t actual_out_bytes = 0;
        // Fast path 1: Try zlib container format
        enum libdeflate_result res = libdeflate_zlib_decompress(
            decompressor, source, (size_t)sourceLen, dest, (size_t)*destLen, &actual_out_bytes
        );

        // Fast path 2: If zlib header fails, try raw DEFLATE stream (common in zip archives)
        if (res == LIBDEFLATE_BAD_DATA) {
            res = libdeflate_deflate_decompress(
                decompressor, source, (size_t)sourceLen, dest, (size_t)*destLen, &actual_out_bytes
            );
        }

        if (res == LIBDEFLATE_SUCCESS) {
            *destLen = (uLongf)actual_out_bytes;
            g_uncompressCalls.fetch_add(1, std::memory_order_relaxed);
            g_uncompressLibdeflateHits.fetch_add(1, std::memory_order_relaxed);
            g_uncompressBytesIn.fetch_add(sourceLen, std::memory_order_relaxed);
            g_uncompressBytesOut.fetch_add(actual_out_bytes, std::memory_order_relaxed);
            return Z_OK;
        }
    }

    // Safe Fallback: Original zlibwapi_real_x64.dll
    g_uncompressFallbacks.fetch_add(1, std::memory_order_relaxed);
    if (pfn_uncompress) {
        return ((int(*)(Bytef*, uLongf*, const Bytef*, uLong))pfn_uncompress)(dest, destLen, source, sourceLen);
    }

    return Z_DATA_ERROR;
}

extern "C" int Proxy_uncompress2(Bytef *dest, uLongf *destLen, const Bytef *source, uLong *sourceLen) {
    // [L2-P1-01] Validate pointers & bounds and update *sourceLen with exact consumed bytes.
    if (!dest || !destLen || !source || !sourceLen) {
        return Z_STREAM_ERROR;
    }
    if (*sourceLen == 0) {
        if (pfn_uncompress2) {
            return ((int(*)(Bytef*, uLongf*, const Bytef*, uLong*))pfn_uncompress2)(dest, destLen, source, sourceLen);
        }
        return Z_BUF_ERROR;
    }
    const uLong maxReasonableLen = 0x7FFFFFFFUL;
    if (*sourceLen > maxReasonableLen || *destLen > maxReasonableLen) {
        Log("Proxy_uncompress2 rejected out-of-bounds length: sourceLen=%lu destLen=%lu", *sourceLen, *destLen);
        return Z_BUF_ERROR;
    }

    const size_t inCap = static_cast<size_t>(*sourceLen);
    const size_t outCap = static_cast<size_t>(*destLen);
    struct libdeflate_decompressor* decompressor = GetThreadDecompressor();
    if (decompressor) {
        size_t actual_in_bytes = 0;
        size_t actual_out_bytes = 0;

        // Fast path 1: zlib wrapper format with exact input-consumed tracking
        enum libdeflate_result res = libdeflate_zlib_decompress_ex(
            decompressor, source, inCap, dest, outCap, &actual_in_bytes, &actual_out_bytes
        );

        // Fast path 2: raw DEFLATE stream fallback
        if (res == LIBDEFLATE_BAD_DATA) {
            actual_in_bytes = 0;
            actual_out_bytes = 0;
            res = libdeflate_deflate_decompress_ex(
                decompressor, source, inCap, dest, outCap, &actual_in_bytes, &actual_out_bytes
            );
        }

        if (res == LIBDEFLATE_SUCCESS && actual_in_bytes <= inCap && actual_out_bytes <= outCap) {
            *sourceLen = static_cast<uLong>(actual_in_bytes);
            *destLen = static_cast<uLongf>(actual_out_bytes);
            g_uncompressCalls.fetch_add(1, std::memory_order_relaxed);
            g_uncompressLibdeflateHits.fetch_add(1, std::memory_order_relaxed);
            g_uncompressBytesIn.fetch_add(actual_in_bytes, std::memory_order_relaxed);
            g_uncompressBytesOut.fetch_add(actual_out_bytes, std::memory_order_relaxed);
            return Z_OK;
        }

        if (res == LIBDEFLATE_INSUFFICIENT_SPACE && !pfn_uncompress2) {
            *sourceLen = 0;
            return Z_BUF_ERROR;
        }
    }

    // Safe Fallback: Original zlibwapi_real_x64.dll uncompress2 (preserves *sourceLen contract)
    g_uncompressFallbacks.fetch_add(1, std::memory_order_relaxed);
    if (pfn_uncompress2) {
        return ((int(*)(Bytef*, uLongf*, const Bytef*, uLong*))pfn_uncompress2)(dest, destLen, source, sourceLen);
    }

    *sourceLen = 0;
    return Z_DATA_ERROR;
}

// Hardware-Accelerated CRC32 & Adler-32
#define KONGOR_ZLIB_MAGIC 0x4B475231UL /* 'KGR1' */

// Hardware-Accelerated CRC32 & Adler-32 with Batched Telemetry (0 LOCK XADD per hot-call)
static void BatchCountCrc32() {
    thread_local uint32_t s_localCrcCount = 0;
    if (++s_localCrcCount >= 4096) {
        g_crc32Calls.fetch_add(s_localCrcCount, std::memory_order_relaxed);
        s_localCrcCount = 0;
    }
}

static void BatchCountAdler32() {
    thread_local uint32_t s_localAdlerCount = 0;
    if (++s_localAdlerCount >= 4096) {
        g_adler32Calls.fetch_add(s_localAdlerCount, std::memory_order_relaxed);
        s_localAdlerCount = 0;
    }
}

extern "C" uLong Proxy_crc32(uLong crc, const Bytef *buf, uInt len) {
    BatchCountCrc32();
    if (!buf) return 0;
    return (uLong)libdeflate_crc32((uint32_t)crc, buf, (size_t)len);
}

extern "C" uLong Proxy_crc32_z(uLong crc, const Bytef *buf, size_t len) {
    BatchCountCrc32();
    if (!buf) return 0;
    return (uLong)libdeflate_crc32((uint32_t)crc, buf, len);
}

extern "C" uLong Proxy_adler32(uLong adler, const Bytef *buf, uInt len) {
    BatchCountAdler32();
    if (!buf) return 1;
    return (uLong)libdeflate_adler32((uint32_t)adler, buf, (size_t)len);
}

extern "C" uLong Proxy_adler32_z(uLong adler, const Bytef *buf, size_t len) {
    BatchCountAdler32();
    if (!buf) return 1;
    return (uLong)libdeflate_adler32((uint32_t)adler, buf, len);
}

// Telemetry & SIMD AVX2 Fast-Path for Streaming Inflate (K2 CMMapUnzip)
extern "C" int Proxy_inflateInit2_(z_streamp strm, int windowBits, const char *version, int stream_size) {
    if (!strm) return Z_STREAM_ERROR;
    g_inflateStreamsActive.fetch_add(1, std::memory_order_relaxed);

    // K2 CMMapUnzip always uses windowBits == -15 (raw DEFLATE inside .s2z zip entries).
    // Defer allocating zlib internal_state (saves ~40 KB heap allocation per file read).
    if (windowBits == -15) {
        strm->state = nullptr;
        strm->reserved = KONGOR_ZLIB_MAGIC;
        strm->msg = nullptr;
        strm->total_in = 0;
        strm->total_out = 0;
        return Z_OK;
    }

    strm->reserved = 0;
    if (pfn_inflateInit2_) {
        return ((int(*)(z_streamp, int, const char*, int))pfn_inflateInit2_)(strm, windowBits, version, stream_size);
    }
    return Z_VERSION_ERROR;
}

extern "C" int Proxy_inflate(z_streamp strm, int flush) {
    if (!strm) return Z_STREAM_ERROR;

    if (strm->reserved == KONGOR_ZLIB_MAGIC && strm->state == nullptr) {
        // Fast path: single-shot raw deflate decompression via libdeflate AVX2 / BMI2
        if (strm->avail_in > 0 && strm->avail_out > 0 && strm->total_in == 0) {
            struct libdeflate_decompressor* decompressor = GetThreadDecompressor();
            if (decompressor) {
                size_t actual_out_bytes = 0;
                size_t actual_in_bytes = 0;
                enum libdeflate_result res = libdeflate_deflate_decompress_ex(
                    decompressor,
                    strm->next_in, (size_t)strm->avail_in,
                    strm->next_out, (size_t)strm->avail_out,
                    &actual_in_bytes, &actual_out_bytes
                );

                if (res == LIBDEFLATE_SUCCESS) {
                    strm->next_in += actual_in_bytes;
                    strm->avail_in -= (uInt)actual_in_bytes;
                    strm->total_in += (uLong)actual_in_bytes;

                    strm->next_out += actual_out_bytes;
                    strm->avail_out -= (uInt)actual_out_bytes;
                    strm->total_out += (uLong)actual_out_bytes;

                    g_uncompressCalls.fetch_add(1, std::memory_order_relaxed);
                    g_uncompressLibdeflateHits.fetch_add(1, std::memory_order_relaxed);
                    g_uncompressBytesIn.fetch_add(actual_in_bytes, std::memory_order_relaxed);
                    g_uncompressBytesOut.fetch_add(actual_out_bytes, std::memory_order_relaxed);

                    return Z_STREAM_END;
                }
            }
        }

        // If libdeflate single-shot fails or is a partial streaming chunk, instantiate real zlib state:
        strm->reserved = 0;
        if (pfn_inflateInit2_) {
            int initRes = ((int(*)(z_streamp, int, const char*, int))pfn_inflateInit2_)(strm, -15, "1.2.3", sizeof(z_stream));
            if (initRes != Z_OK) {
                return initRes;
            }
        }
    }

    if (pfn_inflate) {
        return ((int(*)(z_streamp, int))pfn_inflate)(strm, flush);
    }
    return Z_STREAM_ERROR;
}

extern "C" int Proxy_inflateReset(z_streamp strm) {
    if (!strm) return Z_STREAM_ERROR;
    if (strm->reserved == KONGOR_ZLIB_MAGIC && strm->state == nullptr) {
        strm->total_in = 0;
        strm->total_out = 0;
        strm->msg = nullptr;
        return Z_OK;
    }
    if (pfn_inflateReset) {
        return ((int(*)(z_streamp))pfn_inflateReset)(strm);
    }
    return Z_STREAM_ERROR;
}

extern "C" int Proxy_inflateEnd(z_streamp strm) {
    if (!strm) return Z_STREAM_ERROR;
    g_inflateStreamsActive.fetch_sub(1, std::memory_order_relaxed);
    if (strm->reserved == KONGOR_ZLIB_MAGIC && strm->state == nullptr) {
        strm->reserved = 0;
        return Z_OK; // Zero heap allocations to free!
    }
    strm->reserved = 0;
    if (pfn_inflateEnd) {
        return ((int(*)(z_streamp))pfn_inflateEnd)(strm);
    }
    return Z_STREAM_ERROR;
}

// ============================================================================
// DLL LIFECYCLE & INITIALIZATION
// ============================================================================

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinstDLL);

        wchar_t modulePath[MAX_PATH] = {0};
        GetModuleFileNameW(hinstDLL, modulePath, MAX_PATH);
        wchar_t* pSlash = wcsrchr(modulePath, L'\\');
        if (pSlash) *(pSlash + 1) = L'\0';
        wcsncpy_s(g_logFilePath, MAX_PATH, modulePath, _TRUNCATE);
        wcscat_s(g_logFilePath, MAX_PATH, L"zlibwapi_proxy.log");

        // Pre-arm NativeGuard (version.dll) at Tick 0 only when hosted inside K2 game/server executables,
        // never inside managed test runners (testhost.exe / dotnet.exe).
        wchar_t hostExePath[MAX_PATH] = {0};
        GetModuleFileNameW(NULL, hostExePath, MAX_PATH);
        const wchar_t* hostBase = wcsrchr(hostExePath, L'\\');
        hostBase = hostBase ? (hostBase + 1) : hostExePath;
        bool isTestOrToolHost = (_wcsnicmp(hostBase, L"testhost", 8) == 0) ||
                                (_wcsnicmp(hostBase, L"dotnet", 6) == 0) ||
                                (_wcsnicmp(hostBase, L"vstest", 6) == 0) ||
                                (_wcsnicmp(hostBase, L"willowmaker", 11) == 0) ||
                                (_wcsnicmp(hostBase, L"kongor", 6) == 0) ||
                                (_wcsnicmp(hostBase, L"test_", 5) == 0) ||
                                (GetEnvironmentVariableW(L"KONGOR_NATIVE_GUARD_TEST_MODE", nullptr, 0) > 0);

        HMODULE hNativeGuard = NULL;
        if (!isTestOrToolHost) {
            std::wstring versionDllPath = std::wstring(modulePath) + L"version.dll";
            hNativeGuard = LoadLibraryW(versionDllPath.c_str());
            if (!hNativeGuard) {
                hNativeGuard = LoadLibraryW(L"version.dll");
            }
        }

        std::wstring realDllPath = std::wstring(modulePath) + L"zlibwapi_real_x64.dll";
        g_hRealZlib = LoadLibraryW(realDllPath.c_str());

        if (!g_hRealZlib) {
            // Fallback 1: Look in windows-x64 subfolder
            std::wstring fallbackPath = std::wstring(modulePath) + L"windows-x64\\zlibwapi_real_x64.dll";
            g_hRealZlib = LoadLibraryW(fallbackPath.c_str());
        }
        if (!g_hRealZlib) {
            // Fallback 2: Look in root games/client
            g_hRealZlib = LoadLibraryW(L"zlibwapi_real_x64.dll");
        }

        Log("================================================================================");
        Log("KONGOR HIGH-SPEED ZLIB PROXY INITIALIZED (libdeflate %s SIMD)", LIBDEFLATE_VERSION_STRING);
        if (hNativeGuard) {
            Log("NativeGuard (version.dll) armed at Tick 0: 0x%p", hNativeGuard);
        } else {
            Log("WARNING: NativeGuard (version.dll) failed to load at Tick 0! Win32 Error=%lu", GetLastError());
        }
        Log("Loaded Real Backend: 0x%p (Path: %ls)", g_hRealZlib, realDllPath.c_str());
        Log("================================================================================");

        if (g_hRealZlib) {
            InitializeZlibProxies(g_hRealZlib);
            Log("Successfully bound 132 real zlib entry points.");
        } else {
            Log("CRITICAL: Failed to load zlibwapi_real_x64.dll! Win32 Error=%lu", GetLastError());
        }
    }
    else if (fdwReason == DLL_PROCESS_DETACH) {
        uint64_t totalCalls = g_uncompressCalls.load(std::memory_order_relaxed);
        uint64_t hits = g_uncompressLibdeflateHits.load(std::memory_order_relaxed);
        uint64_t fallbacks = g_uncompressFallbacks.load(std::memory_order_relaxed);
        uint64_t inBytes = g_uncompressBytesIn.load(std::memory_order_relaxed);
        uint64_t outBytes = g_uncompressBytesOut.load(std::memory_order_relaxed);
        uint64_t totalUs = g_uncompressTotalMicroseconds.load(std::memory_order_relaxed);
        uint64_t crcCalls = g_crc32Calls.load(std::memory_order_relaxed);
        uint64_t adlerCalls = g_adler32Calls.load(std::memory_order_relaxed);

        double hitRate = (totalCalls > 0) ? (100.0 * (double)hits / (double)totalCalls) : 0.0;
        double avgSpeedMBs = (totalUs > 0) ? ((double)outBytes / (1024.0 * 1024.0)) / ((double)totalUs / 1000000.0) : 0.0;

        // [L2-P1-02] Process termination (lpReserved != nullptr) runs under the OS loader lock
        // after threads may have been terminated mid-lock: bypass CRT FILE*/mutex/FreeLibrary
        // and write atomic telemetry summary via lock-free stack buffer + Win32 WriteFile.
        if (lpReserved != nullptr) {
            if (g_logFilePath[0] != L'\0') {
                SYSTEMTIME st{};
                ::GetLocalTime(&st);
                char summaryBuf[640] = {0};
                int n = snprintf(
                    summaryBuf, sizeof(summaryBuf),
                    "[%02u:%02u:%02u.%03u] [KONGOR ZLIB] SHUTDOWN SUMMARY (ExitProcess): uncompress=%llu (Hits=%llu [%.1f%%], Fallbacks=%llu), MB=%.2f->%.2f (%.1f MB/s), CRC32=%llu, Adler32=%llu\r\n",
                    static_cast<unsigned>(st.wHour),
                    static_cast<unsigned>(st.wMinute),
                    static_cast<unsigned>(st.wSecond),
                    static_cast<unsigned>(st.wMilliseconds),
                    static_cast<unsigned long long>(totalCalls),
                    static_cast<unsigned long long>(hits),
                    hitRate,
                    static_cast<unsigned long long>(fallbacks),
                    (double)inBytes / (1024.0 * 1024.0),
                    (double)outBytes / (1024.0 * 1024.0),
                    avgSpeedMBs,
                    static_cast<unsigned long long>(crcCalls),
                    static_cast<unsigned long long>(adlerCalls));
                if (n > 0) {
                    HANDLE hLog = ::CreateFileW(
                        g_logFilePath,
                        FILE_APPEND_DATA,
                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                        NULL,
                        OPEN_ALWAYS,
                        FILE_ATTRIBUTE_NORMAL,
                        NULL);
                    if (hLog != INVALID_HANDLE_VALUE) {
                        DWORD written = 0;
                        ::WriteFile(hLog, summaryBuf, static_cast<DWORD>((n < (int)sizeof(summaryBuf)) ? n : ((int)sizeof(summaryBuf) - 1)), &written, NULL);
                        ::CloseHandle(hLog);
                    }
                }
            }
            return TRUE;
        }

        Log("================================================================================");
        Log("KONGOR ZLIB PROXY SHUTDOWN SUMMARY:");
        Log("  Total uncompress calls: %llu (Hits: %llu [%.1f%%], Fallbacks: %llu)", totalCalls, hits, hitRate, fallbacks);
        Log("  Total Data Decompressed: %.2f MB -> %.2f MB (Avg Speed: %.1f MB/s)",
            (double)inBytes / (1024.0 * 1024.0), (double)outBytes / (1024.0 * 1024.0), avgSpeedMBs);
        Log("  CRC32 Calls: %llu | Adler32 Calls: %llu", crcCalls, adlerCalls);
        Log("================================================================================");

        if (g_pLogFile) {
            fclose(g_pLogFile);
            g_pLogFile = nullptr;
        }
        if (g_hRealZlib) {
            FreeLibrary(g_hRealZlib);
            g_hRealZlib = NULL;
        }
    }
    return TRUE;
}
