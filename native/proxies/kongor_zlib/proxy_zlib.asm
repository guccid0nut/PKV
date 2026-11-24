; ============================================================================
; File: proxy_zlib.asm
; System: ProjectKongor Native Modernization - MASM Thunks for zlibwapi
; ============================================================================
.CODE

EXTERN pfn_compress:QWORD
EXTERN pfn_deflate:QWORD
EXTERN pfn_deflateCopy:QWORD
EXTERN pfn_deflateEnd:QWORD
EXTERN pfn_deflateInit2_:QWORD
EXTERN pfn_deflateInit_:QWORD
EXTERN pfn_deflateParams:QWORD
EXTERN pfn_deflateReset:QWORD
EXTERN pfn_deflateSetDictionary:QWORD
EXTERN pfn_gzclose:QWORD
EXTERN pfn_gzdopen:QWORD
EXTERN pfn_gzerror:QWORD
EXTERN pfn_gzflush:QWORD
EXTERN pfn_gzopen:QWORD
EXTERN pfn_gzread:QWORD
EXTERN pfn_gzwrite:QWORD
EXTERN pfn_inflateInit_:QWORD
EXTERN pfn_inflateSetDictionary:QWORD
EXTERN pfn_inflateSync:QWORD
EXTERN pfn_zlibVersion:QWORD
EXTERN pfn_gzprintf:QWORD
EXTERN pfn_gzputc:QWORD
EXTERN pfn_gzgetc:QWORD
EXTERN pfn_gzseek:QWORD
EXTERN pfn_gzrewind:QWORD
EXTERN pfn_gztell:QWORD
EXTERN pfn_gzeof:QWORD
EXTERN pfn_gzsetparams:QWORD
EXTERN pfn_zError:QWORD
EXTERN pfn_inflateSyncPoint:QWORD
EXTERN pfn_get_crc_table:QWORD
EXTERN pfn_compress2:QWORD
EXTERN pfn_gzputs:QWORD
EXTERN pfn_gzgets:QWORD
EXTERN pfn_inflateCopy:QWORD
EXTERN pfn_inflateBackInit_:QWORD
EXTERN pfn_inflateBack:QWORD
EXTERN pfn_inflateBackEnd:QWORD
EXTERN pfn_compressBound:QWORD
EXTERN pfn_deflateBound:QWORD
EXTERN pfn_gzclearerr:QWORD
EXTERN pfn_gzungetc:QWORD
EXTERN pfn_zlibCompileFlags:QWORD
EXTERN pfn_deflatePrime:QWORD
EXTERN pfn_deflatePending:QWORD
EXTERN pfn_unzOpen:QWORD
EXTERN pfn_unzClose:QWORD
EXTERN pfn_unzGetGlobalInfo:QWORD
EXTERN pfn_unzGetCurrentFileInfo:QWORD
EXTERN pfn_unzGoToFirstFile:QWORD
EXTERN pfn_unzGoToNextFile:QWORD
EXTERN pfn_unzOpenCurrentFile:QWORD
EXTERN pfn_unzReadCurrentFile:QWORD
EXTERN pfn_unzOpenCurrentFile3:QWORD
EXTERN pfn_unztell:QWORD
EXTERN pfn_unzeof:QWORD
EXTERN pfn_unzCloseCurrentFile:QWORD
EXTERN pfn_unzGetGlobalComment:QWORD
EXTERN pfn_unzStringFileNameCompare:QWORD
EXTERN pfn_unzLocateFile:QWORD
EXTERN pfn_unzGetLocalExtrafield:QWORD
EXTERN pfn_unzOpen2:QWORD
EXTERN pfn_unzOpenCurrentFile2:QWORD
EXTERN pfn_unzOpenCurrentFilePassword:QWORD
EXTERN pfn_zipOpen:QWORD
EXTERN pfn_zipOpenNewFileInZip:QWORD
EXTERN pfn_zipWriteInFileInZip:QWORD
EXTERN pfn_zipCloseFileInZip:QWORD
EXTERN pfn_zipClose:QWORD
EXTERN pfn_zipOpenNewFileInZip2:QWORD
EXTERN pfn_zipCloseFileInZipRaw:QWORD
EXTERN pfn_zipOpen2:QWORD
EXTERN pfn_zipOpenNewFileInZip3:QWORD
EXTERN pfn_unzGetFilePos:QWORD
EXTERN pfn_unzGoToFilePos:QWORD
EXTERN pfn_fill_win32_filefunc:QWORD
EXTERN pfn_fill_win32_filefunc64:QWORD
EXTERN pfn_fill_win32_filefunc64A:QWORD
EXTERN pfn_fill_win32_filefunc64W:QWORD
EXTERN pfn_unzOpen64:QWORD
EXTERN pfn_unzOpen2_64:QWORD
EXTERN pfn_unzGetGlobalInfo64:QWORD
EXTERN pfn_unzGetCurrentFileInfo64:QWORD
EXTERN pfn_unzGetCurrentFileZStreamPos64:QWORD
EXTERN pfn_unztell64:QWORD
EXTERN pfn_unzGetFilePos64:QWORD
EXTERN pfn_unzGoToFilePos64:QWORD
EXTERN pfn_zipOpen64:QWORD
EXTERN pfn_zipOpen2_64:QWORD
EXTERN pfn_zipOpenNewFileInZip64:QWORD
EXTERN pfn_zipOpenNewFileInZip2_64:QWORD
EXTERN pfn_zipOpenNewFileInZip3_64:QWORD
EXTERN pfn_zipOpenNewFileInZip4_64:QWORD
EXTERN pfn_zipCloseFileInZipRaw64:QWORD
EXTERN pfn_adler32_combine:QWORD
EXTERN pfn_crc32_combine:QWORD
EXTERN pfn_deflateSetHeader:QWORD
EXTERN pfn_deflateTune:QWORD
EXTERN pfn_gzbuffer:QWORD
EXTERN pfn_gzclose_r:QWORD
EXTERN pfn_gzclose_w:QWORD
EXTERN pfn_gzdirect:QWORD
EXTERN pfn_gzoffset:QWORD
EXTERN pfn_inflateGetHeader:QWORD
EXTERN pfn_inflateMark:QWORD
EXTERN pfn_inflatePrime:QWORD
EXTERN pfn_inflateReset2:QWORD
EXTERN pfn_inflateUndermine:QWORD
EXTERN pfn_gzgetc_:QWORD
EXTERN pfn_inflateResetKeep:QWORD
EXTERN pfn_deflateResetKeep:QWORD
EXTERN pfn_gzopen_w:QWORD
EXTERN pfn_inflateGetDictionary:QWORD
EXTERN pfn_gzvprintf:QWORD
EXTERN pfn_inflateCodesUsed:QWORD
EXTERN pfn_inflateValidate:QWORD
EXTERN pfn_gzfread:QWORD
EXTERN pfn_gzfwrite:QWORD
EXTERN pfn_deflateGetDictionary:QWORD
EXTERN pfn_crc32_combine_gen:QWORD
EXTERN pfn_crc32_combine_gen64:QWORD
EXTERN pfn_crc32_combine_op:QWORD

PUBLIC thunk_compress
thunk_compress PROC
    jmp qword ptr [pfn_compress]
thunk_compress ENDP

PUBLIC thunk_deflate
thunk_deflate PROC
    jmp qword ptr [pfn_deflate]
thunk_deflate ENDP

PUBLIC thunk_deflateCopy
thunk_deflateCopy PROC
    jmp qword ptr [pfn_deflateCopy]
thunk_deflateCopy ENDP

PUBLIC thunk_deflateEnd
thunk_deflateEnd PROC
    jmp qword ptr [pfn_deflateEnd]
thunk_deflateEnd ENDP

PUBLIC thunk_deflateInit2_
thunk_deflateInit2_ PROC
    jmp qword ptr [pfn_deflateInit2_]
thunk_deflateInit2_ ENDP

PUBLIC thunk_deflateInit_
thunk_deflateInit_ PROC
    jmp qword ptr [pfn_deflateInit_]
thunk_deflateInit_ ENDP

PUBLIC thunk_deflateParams
thunk_deflateParams PROC
    jmp qword ptr [pfn_deflateParams]
thunk_deflateParams ENDP

PUBLIC thunk_deflateReset
thunk_deflateReset PROC
    jmp qword ptr [pfn_deflateReset]
thunk_deflateReset ENDP

PUBLIC thunk_deflateSetDictionary
thunk_deflateSetDictionary PROC
    jmp qword ptr [pfn_deflateSetDictionary]
thunk_deflateSetDictionary ENDP

PUBLIC thunk_gzclose
thunk_gzclose PROC
    jmp qword ptr [pfn_gzclose]
thunk_gzclose ENDP

PUBLIC thunk_gzdopen
thunk_gzdopen PROC
    jmp qword ptr [pfn_gzdopen]
thunk_gzdopen ENDP

PUBLIC thunk_gzerror
thunk_gzerror PROC
    jmp qword ptr [pfn_gzerror]
thunk_gzerror ENDP

PUBLIC thunk_gzflush
thunk_gzflush PROC
    jmp qword ptr [pfn_gzflush]
thunk_gzflush ENDP

PUBLIC thunk_gzopen
thunk_gzopen PROC
    jmp qword ptr [pfn_gzopen]
thunk_gzopen ENDP

PUBLIC thunk_gzread
thunk_gzread PROC
    jmp qword ptr [pfn_gzread]
thunk_gzread ENDP

PUBLIC thunk_gzwrite
thunk_gzwrite PROC
    jmp qword ptr [pfn_gzwrite]
thunk_gzwrite ENDP

PUBLIC thunk_inflateInit_
thunk_inflateInit_ PROC
    jmp qword ptr [pfn_inflateInit_]
thunk_inflateInit_ ENDP

PUBLIC thunk_inflateSetDictionary
thunk_inflateSetDictionary PROC
    jmp qword ptr [pfn_inflateSetDictionary]
thunk_inflateSetDictionary ENDP

PUBLIC thunk_inflateSync
thunk_inflateSync PROC
    jmp qword ptr [pfn_inflateSync]
thunk_inflateSync ENDP

PUBLIC thunk_zlibVersion
thunk_zlibVersion PROC
    jmp qword ptr [pfn_zlibVersion]
thunk_zlibVersion ENDP

PUBLIC thunk_gzprintf
thunk_gzprintf PROC
    jmp qword ptr [pfn_gzprintf]
thunk_gzprintf ENDP

PUBLIC thunk_gzputc
thunk_gzputc PROC
    jmp qword ptr [pfn_gzputc]
thunk_gzputc ENDP

PUBLIC thunk_gzgetc
thunk_gzgetc PROC
    jmp qword ptr [pfn_gzgetc]
thunk_gzgetc ENDP

PUBLIC thunk_gzseek
thunk_gzseek PROC
    jmp qword ptr [pfn_gzseek]
thunk_gzseek ENDP

PUBLIC thunk_gzrewind
thunk_gzrewind PROC
    jmp qword ptr [pfn_gzrewind]
thunk_gzrewind ENDP

PUBLIC thunk_gztell
thunk_gztell PROC
    jmp qword ptr [pfn_gztell]
thunk_gztell ENDP

PUBLIC thunk_gzeof
thunk_gzeof PROC
    jmp qword ptr [pfn_gzeof]
thunk_gzeof ENDP

PUBLIC thunk_gzsetparams
thunk_gzsetparams PROC
    jmp qword ptr [pfn_gzsetparams]
thunk_gzsetparams ENDP

PUBLIC thunk_zError
thunk_zError PROC
    jmp qword ptr [pfn_zError]
thunk_zError ENDP

PUBLIC thunk_inflateSyncPoint
thunk_inflateSyncPoint PROC
    jmp qword ptr [pfn_inflateSyncPoint]
thunk_inflateSyncPoint ENDP

PUBLIC thunk_get_crc_table
thunk_get_crc_table PROC
    jmp qword ptr [pfn_get_crc_table]
thunk_get_crc_table ENDP

PUBLIC thunk_compress2
thunk_compress2 PROC
    jmp qword ptr [pfn_compress2]
thunk_compress2 ENDP

PUBLIC thunk_gzputs
thunk_gzputs PROC
    jmp qword ptr [pfn_gzputs]
thunk_gzputs ENDP

PUBLIC thunk_gzgets
thunk_gzgets PROC
    jmp qword ptr [pfn_gzgets]
thunk_gzgets ENDP

PUBLIC thunk_inflateCopy
thunk_inflateCopy PROC
    jmp qword ptr [pfn_inflateCopy]
thunk_inflateCopy ENDP

PUBLIC thunk_inflateBackInit_
thunk_inflateBackInit_ PROC
    jmp qword ptr [pfn_inflateBackInit_]
thunk_inflateBackInit_ ENDP

PUBLIC thunk_inflateBack
thunk_inflateBack PROC
    jmp qword ptr [pfn_inflateBack]
thunk_inflateBack ENDP

PUBLIC thunk_inflateBackEnd
thunk_inflateBackEnd PROC
    jmp qword ptr [pfn_inflateBackEnd]
thunk_inflateBackEnd ENDP

PUBLIC thunk_compressBound
thunk_compressBound PROC
    jmp qword ptr [pfn_compressBound]
thunk_compressBound ENDP

PUBLIC thunk_deflateBound
thunk_deflateBound PROC
    jmp qword ptr [pfn_deflateBound]
thunk_deflateBound ENDP

PUBLIC thunk_gzclearerr
thunk_gzclearerr PROC
    jmp qword ptr [pfn_gzclearerr]
thunk_gzclearerr ENDP

PUBLIC thunk_gzungetc
thunk_gzungetc PROC
    jmp qword ptr [pfn_gzungetc]
thunk_gzungetc ENDP

PUBLIC thunk_zlibCompileFlags
thunk_zlibCompileFlags PROC
    jmp qword ptr [pfn_zlibCompileFlags]
thunk_zlibCompileFlags ENDP

PUBLIC thunk_deflatePrime
thunk_deflatePrime PROC
    jmp qword ptr [pfn_deflatePrime]
thunk_deflatePrime ENDP

PUBLIC thunk_deflatePending
thunk_deflatePending PROC
    jmp qword ptr [pfn_deflatePending]
thunk_deflatePending ENDP

PUBLIC thunk_unzOpen
thunk_unzOpen PROC
    jmp qword ptr [pfn_unzOpen]
thunk_unzOpen ENDP

PUBLIC thunk_unzClose
thunk_unzClose PROC
    jmp qword ptr [pfn_unzClose]
thunk_unzClose ENDP

PUBLIC thunk_unzGetGlobalInfo
thunk_unzGetGlobalInfo PROC
    jmp qword ptr [pfn_unzGetGlobalInfo]
thunk_unzGetGlobalInfo ENDP

PUBLIC thunk_unzGetCurrentFileInfo
thunk_unzGetCurrentFileInfo PROC
    jmp qword ptr [pfn_unzGetCurrentFileInfo]
thunk_unzGetCurrentFileInfo ENDP

PUBLIC thunk_unzGoToFirstFile
thunk_unzGoToFirstFile PROC
    jmp qword ptr [pfn_unzGoToFirstFile]
thunk_unzGoToFirstFile ENDP

PUBLIC thunk_unzGoToNextFile
thunk_unzGoToNextFile PROC
    jmp qword ptr [pfn_unzGoToNextFile]
thunk_unzGoToNextFile ENDP

PUBLIC thunk_unzOpenCurrentFile
thunk_unzOpenCurrentFile PROC
    jmp qword ptr [pfn_unzOpenCurrentFile]
thunk_unzOpenCurrentFile ENDP

PUBLIC thunk_unzReadCurrentFile
thunk_unzReadCurrentFile PROC
    jmp qword ptr [pfn_unzReadCurrentFile]
thunk_unzReadCurrentFile ENDP

PUBLIC thunk_unzOpenCurrentFile3
thunk_unzOpenCurrentFile3 PROC
    jmp qword ptr [pfn_unzOpenCurrentFile3]
thunk_unzOpenCurrentFile3 ENDP

PUBLIC thunk_unztell
thunk_unztell PROC
    jmp qword ptr [pfn_unztell]
thunk_unztell ENDP

PUBLIC thunk_unzeof
thunk_unzeof PROC
    jmp qword ptr [pfn_unzeof]
thunk_unzeof ENDP

PUBLIC thunk_unzCloseCurrentFile
thunk_unzCloseCurrentFile PROC
    jmp qword ptr [pfn_unzCloseCurrentFile]
thunk_unzCloseCurrentFile ENDP

PUBLIC thunk_unzGetGlobalComment
thunk_unzGetGlobalComment PROC
    jmp qword ptr [pfn_unzGetGlobalComment]
thunk_unzGetGlobalComment ENDP

PUBLIC thunk_unzStringFileNameCompare
thunk_unzStringFileNameCompare PROC
    jmp qword ptr [pfn_unzStringFileNameCompare]
thunk_unzStringFileNameCompare ENDP

PUBLIC thunk_unzLocateFile
thunk_unzLocateFile PROC
    jmp qword ptr [pfn_unzLocateFile]
thunk_unzLocateFile ENDP

PUBLIC thunk_unzGetLocalExtrafield
thunk_unzGetLocalExtrafield PROC
    jmp qword ptr [pfn_unzGetLocalExtrafield]
thunk_unzGetLocalExtrafield ENDP

PUBLIC thunk_unzOpen2
thunk_unzOpen2 PROC
    jmp qword ptr [pfn_unzOpen2]
thunk_unzOpen2 ENDP

PUBLIC thunk_unzOpenCurrentFile2
thunk_unzOpenCurrentFile2 PROC
    jmp qword ptr [pfn_unzOpenCurrentFile2]
thunk_unzOpenCurrentFile2 ENDP

PUBLIC thunk_unzOpenCurrentFilePassword
thunk_unzOpenCurrentFilePassword PROC
    jmp qword ptr [pfn_unzOpenCurrentFilePassword]
thunk_unzOpenCurrentFilePassword ENDP

PUBLIC thunk_zipOpen
thunk_zipOpen PROC
    jmp qword ptr [pfn_zipOpen]
thunk_zipOpen ENDP

PUBLIC thunk_zipOpenNewFileInZip
thunk_zipOpenNewFileInZip PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip]
thunk_zipOpenNewFileInZip ENDP

PUBLIC thunk_zipWriteInFileInZip
thunk_zipWriteInFileInZip PROC
    jmp qword ptr [pfn_zipWriteInFileInZip]
thunk_zipWriteInFileInZip ENDP

PUBLIC thunk_zipCloseFileInZip
thunk_zipCloseFileInZip PROC
    jmp qword ptr [pfn_zipCloseFileInZip]
thunk_zipCloseFileInZip ENDP

PUBLIC thunk_zipClose
thunk_zipClose PROC
    jmp qword ptr [pfn_zipClose]
thunk_zipClose ENDP

PUBLIC thunk_zipOpenNewFileInZip2
thunk_zipOpenNewFileInZip2 PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip2]
thunk_zipOpenNewFileInZip2 ENDP

PUBLIC thunk_zipCloseFileInZipRaw
thunk_zipCloseFileInZipRaw PROC
    jmp qword ptr [pfn_zipCloseFileInZipRaw]
thunk_zipCloseFileInZipRaw ENDP

PUBLIC thunk_zipOpen2
thunk_zipOpen2 PROC
    jmp qword ptr [pfn_zipOpen2]
thunk_zipOpen2 ENDP

PUBLIC thunk_zipOpenNewFileInZip3
thunk_zipOpenNewFileInZip3 PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip3]
thunk_zipOpenNewFileInZip3 ENDP

PUBLIC thunk_unzGetFilePos
thunk_unzGetFilePos PROC
    jmp qword ptr [pfn_unzGetFilePos]
thunk_unzGetFilePos ENDP

PUBLIC thunk_unzGoToFilePos
thunk_unzGoToFilePos PROC
    jmp qword ptr [pfn_unzGoToFilePos]
thunk_unzGoToFilePos ENDP

PUBLIC thunk_fill_win32_filefunc
thunk_fill_win32_filefunc PROC
    jmp qword ptr [pfn_fill_win32_filefunc]
thunk_fill_win32_filefunc ENDP

PUBLIC thunk_fill_win32_filefunc64
thunk_fill_win32_filefunc64 PROC
    jmp qword ptr [pfn_fill_win32_filefunc64]
thunk_fill_win32_filefunc64 ENDP

PUBLIC thunk_fill_win32_filefunc64A
thunk_fill_win32_filefunc64A PROC
    jmp qword ptr [pfn_fill_win32_filefunc64A]
thunk_fill_win32_filefunc64A ENDP

PUBLIC thunk_fill_win32_filefunc64W
thunk_fill_win32_filefunc64W PROC
    jmp qword ptr [pfn_fill_win32_filefunc64W]
thunk_fill_win32_filefunc64W ENDP

PUBLIC thunk_unzOpen64
thunk_unzOpen64 PROC
    jmp qword ptr [pfn_unzOpen64]
thunk_unzOpen64 ENDP

PUBLIC thunk_unzOpen2_64
thunk_unzOpen2_64 PROC
    jmp qword ptr [pfn_unzOpen2_64]
thunk_unzOpen2_64 ENDP

PUBLIC thunk_unzGetGlobalInfo64
thunk_unzGetGlobalInfo64 PROC
    jmp qword ptr [pfn_unzGetGlobalInfo64]
thunk_unzGetGlobalInfo64 ENDP

PUBLIC thunk_unzGetCurrentFileInfo64
thunk_unzGetCurrentFileInfo64 PROC
    jmp qword ptr [pfn_unzGetCurrentFileInfo64]
thunk_unzGetCurrentFileInfo64 ENDP

PUBLIC thunk_unzGetCurrentFileZStreamPos64
thunk_unzGetCurrentFileZStreamPos64 PROC
    jmp qword ptr [pfn_unzGetCurrentFileZStreamPos64]
thunk_unzGetCurrentFileZStreamPos64 ENDP

PUBLIC thunk_unztell64
thunk_unztell64 PROC
    jmp qword ptr [pfn_unztell64]
thunk_unztell64 ENDP

PUBLIC thunk_unzGetFilePos64
thunk_unzGetFilePos64 PROC
    jmp qword ptr [pfn_unzGetFilePos64]
thunk_unzGetFilePos64 ENDP

PUBLIC thunk_unzGoToFilePos64
thunk_unzGoToFilePos64 PROC
    jmp qword ptr [pfn_unzGoToFilePos64]
thunk_unzGoToFilePos64 ENDP

PUBLIC thunk_zipOpen64
thunk_zipOpen64 PROC
    jmp qword ptr [pfn_zipOpen64]
thunk_zipOpen64 ENDP

PUBLIC thunk_zipOpen2_64
thunk_zipOpen2_64 PROC
    jmp qword ptr [pfn_zipOpen2_64]
thunk_zipOpen2_64 ENDP

PUBLIC thunk_zipOpenNewFileInZip64
thunk_zipOpenNewFileInZip64 PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip64]
thunk_zipOpenNewFileInZip64 ENDP

PUBLIC thunk_zipOpenNewFileInZip2_64
thunk_zipOpenNewFileInZip2_64 PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip2_64]
thunk_zipOpenNewFileInZip2_64 ENDP

PUBLIC thunk_zipOpenNewFileInZip3_64
thunk_zipOpenNewFileInZip3_64 PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip3_64]
thunk_zipOpenNewFileInZip3_64 ENDP

PUBLIC thunk_zipOpenNewFileInZip4_64
thunk_zipOpenNewFileInZip4_64 PROC
    jmp qword ptr [pfn_zipOpenNewFileInZip4_64]
thunk_zipOpenNewFileInZip4_64 ENDP

PUBLIC thunk_zipCloseFileInZipRaw64
thunk_zipCloseFileInZipRaw64 PROC
    jmp qword ptr [pfn_zipCloseFileInZipRaw64]
thunk_zipCloseFileInZipRaw64 ENDP

PUBLIC thunk_adler32_combine
thunk_adler32_combine PROC
    jmp qword ptr [pfn_adler32_combine]
thunk_adler32_combine ENDP

PUBLIC thunk_crc32_combine
thunk_crc32_combine PROC
    jmp qword ptr [pfn_crc32_combine]
thunk_crc32_combine ENDP

PUBLIC thunk_deflateSetHeader
thunk_deflateSetHeader PROC
    jmp qword ptr [pfn_deflateSetHeader]
thunk_deflateSetHeader ENDP

PUBLIC thunk_deflateTune
thunk_deflateTune PROC
    jmp qword ptr [pfn_deflateTune]
thunk_deflateTune ENDP

PUBLIC thunk_gzbuffer
thunk_gzbuffer PROC
    jmp qword ptr [pfn_gzbuffer]
thunk_gzbuffer ENDP

PUBLIC thunk_gzclose_r
thunk_gzclose_r PROC
    jmp qword ptr [pfn_gzclose_r]
thunk_gzclose_r ENDP

PUBLIC thunk_gzclose_w
thunk_gzclose_w PROC
    jmp qword ptr [pfn_gzclose_w]
thunk_gzclose_w ENDP

PUBLIC thunk_gzdirect
thunk_gzdirect PROC
    jmp qword ptr [pfn_gzdirect]
thunk_gzdirect ENDP

PUBLIC thunk_gzoffset
thunk_gzoffset PROC
    jmp qword ptr [pfn_gzoffset]
thunk_gzoffset ENDP

PUBLIC thunk_inflateGetHeader
thunk_inflateGetHeader PROC
    jmp qword ptr [pfn_inflateGetHeader]
thunk_inflateGetHeader ENDP

PUBLIC thunk_inflateMark
thunk_inflateMark PROC
    jmp qword ptr [pfn_inflateMark]
thunk_inflateMark ENDP

PUBLIC thunk_inflatePrime
thunk_inflatePrime PROC
    jmp qword ptr [pfn_inflatePrime]
thunk_inflatePrime ENDP

PUBLIC thunk_inflateReset2
thunk_inflateReset2 PROC
    jmp qword ptr [pfn_inflateReset2]
thunk_inflateReset2 ENDP

PUBLIC thunk_inflateUndermine
thunk_inflateUndermine PROC
    jmp qword ptr [pfn_inflateUndermine]
thunk_inflateUndermine ENDP

PUBLIC thunk_gzgetc_
thunk_gzgetc_ PROC
    jmp qword ptr [pfn_gzgetc_]
thunk_gzgetc_ ENDP

PUBLIC thunk_inflateResetKeep
thunk_inflateResetKeep PROC
    jmp qword ptr [pfn_inflateResetKeep]
thunk_inflateResetKeep ENDP

PUBLIC thunk_deflateResetKeep
thunk_deflateResetKeep PROC
    jmp qword ptr [pfn_deflateResetKeep]
thunk_deflateResetKeep ENDP

PUBLIC thunk_gzopen_w
thunk_gzopen_w PROC
    jmp qword ptr [pfn_gzopen_w]
thunk_gzopen_w ENDP

PUBLIC thunk_inflateGetDictionary
thunk_inflateGetDictionary PROC
    jmp qword ptr [pfn_inflateGetDictionary]
thunk_inflateGetDictionary ENDP

PUBLIC thunk_gzvprintf
thunk_gzvprintf PROC
    jmp qword ptr [pfn_gzvprintf]
thunk_gzvprintf ENDP

PUBLIC thunk_inflateCodesUsed
thunk_inflateCodesUsed PROC
    jmp qword ptr [pfn_inflateCodesUsed]
thunk_inflateCodesUsed ENDP

PUBLIC thunk_inflateValidate
thunk_inflateValidate PROC
    jmp qword ptr [pfn_inflateValidate]
thunk_inflateValidate ENDP

PUBLIC thunk_gzfread
thunk_gzfread PROC
    jmp qword ptr [pfn_gzfread]
thunk_gzfread ENDP

PUBLIC thunk_gzfwrite
thunk_gzfwrite PROC
    jmp qword ptr [pfn_gzfwrite]
thunk_gzfwrite ENDP

PUBLIC thunk_deflateGetDictionary
thunk_deflateGetDictionary PROC
    jmp qword ptr [pfn_deflateGetDictionary]
thunk_deflateGetDictionary ENDP

PUBLIC thunk_crc32_combine_gen
thunk_crc32_combine_gen PROC
    jmp qword ptr [pfn_crc32_combine_gen]
thunk_crc32_combine_gen ENDP

PUBLIC thunk_crc32_combine_gen64
thunk_crc32_combine_gen64 PROC
    jmp qword ptr [pfn_crc32_combine_gen64]
thunk_crc32_combine_gen64 ENDP

PUBLIC thunk_crc32_combine_op
thunk_crc32_combine_op PROC
    jmp qword ptr [pfn_crc32_combine_op]
thunk_crc32_combine_op ENDP

END
