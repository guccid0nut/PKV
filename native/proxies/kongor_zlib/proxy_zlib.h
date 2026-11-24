// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: proxy_zlib.h
// System: ProjectKongor Native Modernization
// Subsystem: kongor_zlib (High-Speed libdeflate SIMD Drop-in for zlibwapi_x64.dll)
// ============================================================================
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <cstdint>
#include <cstddef>

// Standard zlib types
typedef unsigned char Bytef;
typedef unsigned int uInt;
typedef unsigned long uLong;
typedef uLong uLongf;
typedef void* voidpf;

typedef struct z_stream_s {
    Bytef    *next_in;
    uInt     avail_in;
    uLong    total_in;
    Bytef    *next_out;
    uInt     avail_out;
    uLong    total_out;
    char     *msg;
    struct internal_state *state;
    voidpf   (*zalloc)(voidpf opaque, uInt items, uInt size);
    void     (*zfree)(voidpf opaque, voidpf address);
    voidpf   opaque;
    int      data_type;
    uLong    adler;
    uLong    reserved;
} z_stream;
typedef z_stream *z_streamp;

#define Z_OK            0
#define Z_STREAM_END    1
#define Z_NEED_DICT     2
#define Z_ERRNO        (-1)
#define Z_STREAM_ERROR (-2)
#define Z_DATA_ERROR   (-3)
#define Z_MEM_ERROR    (-4)
#define Z_BUF_ERROR    (-5)
#define Z_VERSION_ERROR (-6)

extern HMODULE g_hRealZlib;
void Log(const char* format, ...);

extern "C" {
    extern FARPROC pfn_adler32;
    extern FARPROC pfn_compress;
    extern FARPROC pfn_crc32;
    extern FARPROC pfn_deflate;
    extern FARPROC pfn_deflateCopy;
    extern FARPROC pfn_deflateEnd;
    extern FARPROC pfn_deflateInit2_;
    extern FARPROC pfn_deflateInit_;
    extern FARPROC pfn_deflateParams;
    extern FARPROC pfn_deflateReset;
    extern FARPROC pfn_deflateSetDictionary;
    extern FARPROC pfn_gzclose;
    extern FARPROC pfn_gzdopen;
    extern FARPROC pfn_gzerror;
    extern FARPROC pfn_gzflush;
    extern FARPROC pfn_gzopen;
    extern FARPROC pfn_gzread;
    extern FARPROC pfn_gzwrite;
    extern FARPROC pfn_inflate;
    extern FARPROC pfn_inflateEnd;
    extern FARPROC pfn_inflateInit2_;
    extern FARPROC pfn_inflateInit_;
    extern FARPROC pfn_inflateReset;
    extern FARPROC pfn_inflateSetDictionary;
    extern FARPROC pfn_inflateSync;
    extern FARPROC pfn_uncompress;
    extern FARPROC pfn_zlibVersion;
    extern FARPROC pfn_gzprintf;
    extern FARPROC pfn_gzputc;
    extern FARPROC pfn_gzgetc;
    extern FARPROC pfn_gzseek;
    extern FARPROC pfn_gzrewind;
    extern FARPROC pfn_gztell;
    extern FARPROC pfn_gzeof;
    extern FARPROC pfn_gzsetparams;
    extern FARPROC pfn_zError;
    extern FARPROC pfn_inflateSyncPoint;
    extern FARPROC pfn_get_crc_table;
    extern FARPROC pfn_compress2;
    extern FARPROC pfn_gzputs;
    extern FARPROC pfn_gzgets;
    extern FARPROC pfn_inflateCopy;
    extern FARPROC pfn_inflateBackInit_;
    extern FARPROC pfn_inflateBack;
    extern FARPROC pfn_inflateBackEnd;
    extern FARPROC pfn_compressBound;
    extern FARPROC pfn_deflateBound;
    extern FARPROC pfn_gzclearerr;
    extern FARPROC pfn_gzungetc;
    extern FARPROC pfn_zlibCompileFlags;
    extern FARPROC pfn_deflatePrime;
    extern FARPROC pfn_deflatePending;
    extern FARPROC pfn_unzOpen;
    extern FARPROC pfn_unzClose;
    extern FARPROC pfn_unzGetGlobalInfo;
    extern FARPROC pfn_unzGetCurrentFileInfo;
    extern FARPROC pfn_unzGoToFirstFile;
    extern FARPROC pfn_unzGoToNextFile;
    extern FARPROC pfn_unzOpenCurrentFile;
    extern FARPROC pfn_unzReadCurrentFile;
    extern FARPROC pfn_unzOpenCurrentFile3;
    extern FARPROC pfn_unztell;
    extern FARPROC pfn_unzeof;
    extern FARPROC pfn_unzCloseCurrentFile;
    extern FARPROC pfn_unzGetGlobalComment;
    extern FARPROC pfn_unzStringFileNameCompare;
    extern FARPROC pfn_unzLocateFile;
    extern FARPROC pfn_unzGetLocalExtrafield;
    extern FARPROC pfn_unzOpen2;
    extern FARPROC pfn_unzOpenCurrentFile2;
    extern FARPROC pfn_unzOpenCurrentFilePassword;
    extern FARPROC pfn_zipOpen;
    extern FARPROC pfn_zipOpenNewFileInZip;
    extern FARPROC pfn_zipWriteInFileInZip;
    extern FARPROC pfn_zipCloseFileInZip;
    extern FARPROC pfn_zipClose;
    extern FARPROC pfn_zipOpenNewFileInZip2;
    extern FARPROC pfn_zipCloseFileInZipRaw;
    extern FARPROC pfn_zipOpen2;
    extern FARPROC pfn_zipOpenNewFileInZip3;
    extern FARPROC pfn_unzGetFilePos;
    extern FARPROC pfn_unzGoToFilePos;
    extern FARPROC pfn_fill_win32_filefunc;
    extern FARPROC pfn_fill_win32_filefunc64;
    extern FARPROC pfn_fill_win32_filefunc64A;
    extern FARPROC pfn_fill_win32_filefunc64W;
    extern FARPROC pfn_unzOpen64;
    extern FARPROC pfn_unzOpen2_64;
    extern FARPROC pfn_unzGetGlobalInfo64;
    extern FARPROC pfn_unzGetCurrentFileInfo64;
    extern FARPROC pfn_unzGetCurrentFileZStreamPos64;
    extern FARPROC pfn_unztell64;
    extern FARPROC pfn_unzGetFilePos64;
    extern FARPROC pfn_unzGoToFilePos64;
    extern FARPROC pfn_zipOpen64;
    extern FARPROC pfn_zipOpen2_64;
    extern FARPROC pfn_zipOpenNewFileInZip64;
    extern FARPROC pfn_zipOpenNewFileInZip2_64;
    extern FARPROC pfn_zipOpenNewFileInZip3_64;
    extern FARPROC pfn_zipOpenNewFileInZip4_64;
    extern FARPROC pfn_zipCloseFileInZipRaw64;
    extern FARPROC pfn_adler32_combine;
    extern FARPROC pfn_crc32_combine;
    extern FARPROC pfn_deflateSetHeader;
    extern FARPROC pfn_deflateTune;
    extern FARPROC pfn_gzbuffer;
    extern FARPROC pfn_gzclose_r;
    extern FARPROC pfn_gzclose_w;
    extern FARPROC pfn_gzdirect;
    extern FARPROC pfn_gzoffset;
    extern FARPROC pfn_inflateGetHeader;
    extern FARPROC pfn_inflateMark;
    extern FARPROC pfn_inflatePrime;
    extern FARPROC pfn_inflateReset2;
    extern FARPROC pfn_inflateUndermine;
    extern FARPROC pfn_gzgetc_;
    extern FARPROC pfn_inflateResetKeep;
    extern FARPROC pfn_deflateResetKeep;
    extern FARPROC pfn_gzopen_w;
    extern FARPROC pfn_inflateGetDictionary;
    extern FARPROC pfn_gzvprintf;
    extern FARPROC pfn_inflateCodesUsed;
    extern FARPROC pfn_inflateValidate;
    extern FARPROC pfn_uncompress2;
    extern FARPROC pfn_gzfread;
    extern FARPROC pfn_gzfwrite;
    extern FARPROC pfn_deflateGetDictionary;
    extern FARPROC pfn_adler32_z;
    extern FARPROC pfn_crc32_z;
    extern FARPROC pfn_crc32_combine_gen;
    extern FARPROC pfn_crc32_combine_gen64;
    extern FARPROC pfn_crc32_combine_op;

    // Intercepted exports
    int Proxy_uncompress(Bytef *dest, uLongf *destLen, const Bytef *source, uLong sourceLen);
    int Proxy_uncompress2(Bytef *dest, uLongf *destLen, const Bytef *source, uLong *sourceLen);
    uLong Proxy_crc32(uLong crc, const Bytef *buf, uInt len);
    uLong Proxy_crc32_z(uLong crc, const Bytef *buf, size_t len);
    uLong Proxy_adler32(uLong adler, const Bytef *buf, uInt len);
    uLong Proxy_adler32_z(uLong adler, const Bytef *buf, size_t len);
    int Proxy_inflateInit2_(z_streamp strm, int windowBits, const char *version, int stream_size);
    int Proxy_inflate(z_streamp strm, int flush);
    int Proxy_inflateReset(z_streamp strm);
    int Proxy_inflateEnd(z_streamp strm);
}
