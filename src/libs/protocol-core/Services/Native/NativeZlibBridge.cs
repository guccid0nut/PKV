// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Kongor.Core.Diagnostics;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Result of live dynamic C-ABI validation on a candidate or active zlibwapi_x64.dll binary.
/// </summary>
public sealed record ZlibVerificationResult(
    bool Success,
    string DllPath,
    string Version,
    uint SampleCrc32,
    bool Crc32Passed,
    bool RoundTripPassed,
    string SecurityStatus,
    string? ErrorMessage = null
);

/// <summary>
///     High-performance managed P/Invoke and dynamic C-ABI bridge for zlib 1.3.1 (zlibwapi_x64.dll).
///     Provides live export validation, hardware-accelerated CRC32 calculation (SSE4.2/AVX2),
///     in-memory round-trip compression/decompression, and zero-leak dynamic DLL verification.
/// </summary>
public static class NativeZlibBridge
{
    private const uint ExpectedIeee8023Crc32 = 0xCBF43926;
    private static readonly byte[] Ieee8023TestVector = "123456789"u8.ToArray();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr ZlibVersionFunc();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint Crc32Func(uint crc, [In] byte[] buf, uint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CompressFunc([Out] byte[] dest, ref uint destLen, [In] byte[] source, uint sourceLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int UncompressFunc([Out] byte[] dest, ref uint destLen, [In] byte[] source, uint sourceLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint ZlibCompileFlagsFunc();

    /// <summary>
    ///     Stages a target zlib binary (and its companion <c>zlibwapi_real_x64.dll</c> if present) into an
    ///     isolated temporary directory that excludes <c>version.dll</c> and <c>mimalloc*.dll</c>.
    ///     This prevents <c>kongor_zlib</c>'s <c>DllMain</c> from loading <c>games/client/version.dll</c>
    ///     (NativeGuard) into managed host processes (<c>testhost.exe</c>, <c>WILLOWMAKER.exe</c>, <c>kongor.exe</c>)
    ///     and prevents unit tests from dirtying <c>zlibwapi_proxy.log</c> in the repository working tree.
    /// </summary>
    internal static string PrepareIsolatedLoadPath(string fullPath, out string? isolatedTempDir)
    {
        isolatedTempDir = null;
        string? sourceDir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(sourceDir))
        {
            return fullPath;
        }

        string tempDir = Path.Combine(
            Path.GetTempPath(),
            $"Kongor_ZlibIsolated_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        isolatedTempDir = tempDir;

        string stagedDllPath = Path.Combine(tempDir, Path.GetFileName(fullPath));
        File.Copy(fullPath, stagedDllPath, overwrite: true);

        // If the target is the 62 KB kongor_zlib proxy shim, stage its companion zlibwapi_real_x64.dll alongside it.
        string[] realBackendCandidates =
        [
            Path.Combine(sourceDir, "zlibwapi_real_x64.dll"),
            Path.Combine(sourceDir, "windows-x64", "zlibwapi_real_x64.dll"),
            Directory.GetParent(sourceDir) is { } parentDir
                ? Path.Combine(parentDir.FullName, "zlibwapi_real_x64.dll")
                : string.Empty
        ];

        foreach (string candidate in realBackendCandidates)
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
            {
                string stagedRealPath = Path.Combine(tempDir, "zlibwapi_real_x64.dll");
                File.Copy(candidate, stagedRealPath, overwrite: true);
                break;
            }
        }

        return stagedDllPath;
    }

    internal static void CleanupIsolatedTempDir(string? isolatedTempDir)
    {
        if (string.IsNullOrEmpty(isolatedTempDir) || !Directory.Exists(isolatedTempDir))
        {
            return;
        }

        try
        {
            Directory.Delete(isolatedTempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeZlibBridge] Best-effort cleanup of '{isolatedTempDir}' deferred: {ex.Message}");
        }
    }

    /// <summary>
    ///     Performs live, in-process C-ABI verification on any target zlibwapi_x64.dll file.
    ///     Loads the DLL dynamically using NativeLibrary, verifies exported symbols, executes
    ///     zlibVersion(), checks hardware CRC32 against IEEE 802.3, tests in-memory round-trip
    ///     compress/uncompress, and safely unloads the library handle.
    /// </summary>
    public static ZlibVerificationResult VerifyLibrary(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
        {
            return new ZlibVerificationResult(
                Success: false,
                DllPath: string.Empty,
                Version: "unknown",
                SampleCrc32: 0,
                Crc32Passed: false,
                RoundTripPassed: false,
                SecurityStatus: "NOT_SPECIFIED",
                ErrorMessage: "DLL path was null or whitespace."
            );
        }

        string fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
        {
            return new ZlibVerificationResult(
                Success: false,
                DllPath: fullPath,
                Version: "missing",
                SampleCrc32: 0,
                Crc32Passed: false,
                RoundTripPassed: false,
                SecurityStatus: "NOT_FOUND",
                ErrorMessage: $"Target binary not found at '{fullPath}'."
            );
        }

        string loadPath = PrepareIsolatedLoadPath(fullPath, out string? isolatedTempDir);
        if (!NativeLibrary.TryLoad(loadPath, out IntPtr handle) || handle == IntPtr.Zero)
        {
            CleanupIsolatedTempDir(isolatedTempDir);
            return new ZlibVerificationResult(
                Success: false,
                DllPath: fullPath,
                Version: "unreadable",
                SampleCrc32: 0,
                Crc32Passed: false,
                RoundTripPassed: false,
                SecurityStatus: "LOAD_FAILED",
                ErrorMessage: $"Operating system loader failed to load '{fullPath}'."
            );
        }

        try
        {
            // 1. Resolve exports
            if (!NativeLibrary.TryGetExport(handle, "zlibVersion", out IntPtr pZlibVersion) ||
                !NativeLibrary.TryGetExport(handle, "crc32", out IntPtr pCrc32) ||
                !NativeLibrary.TryGetExport(handle, "compress", out IntPtr pCompress) ||
                !NativeLibrary.TryGetExport(handle, "uncompress", out IntPtr pUncompress))
            {
                return new ZlibVerificationResult(
                    Success: false,
                    DllPath: fullPath,
                    Version: "missing_exports",
                    SampleCrc32: 0,
                    Crc32Passed: false,
                    RoundTripPassed: false,
                    SecurityStatus: "INVALID_ABI",
                    ErrorMessage: "One or more essential zlib C-ABI export symbols were missing."
                );
            }

            ZlibVersionFunc zlibVersionFn = Marshal.GetDelegateForFunctionPointer<ZlibVersionFunc>(pZlibVersion);
            Crc32Func crc32Fn = Marshal.GetDelegateForFunctionPointer<Crc32Func>(pCrc32);
            CompressFunc compressFn = Marshal.GetDelegateForFunctionPointer<CompressFunc>(pCompress);
            UncompressFunc uncompressFn = Marshal.GetDelegateForFunctionPointer<UncompressFunc>(pUncompress);

            // 2. Query zlibVersion
            IntPtr verPtr = zlibVersionFn();
            string version = Marshal.PtrToStringAnsi(verPtr) ?? "unknown";

            bool isModern = version.StartsWith("1.3", StringComparison.OrdinalIgnoreCase);
            string securityStatus;
            if (isModern)
            {
                securityStatus = $"SECURE (zlib {version} - CVE-2022-37434 patched)";
            }
            else if (version.StartsWith("1.2", StringComparison.OrdinalIgnoreCase))
            {
                securityStatus = $"VULNERABLE (zlib {version} - Heap Buffer Overflow CVE-2022-37434)";
            }
            else
            {
                securityStatus = $"UNKNOWN (zlib {version})";
            }

            // 3. Hardware / Vectorized CRC32 calculation + Rocksoft managed cross-check (#161)
            uint sampleCrc = crc32Fn(0, Ieee8023TestVector, (uint)Ieee8023TestVector.Length);
            uint managedSampleCrc = ComputeManagedCrc32Ieee(Ieee8023TestVector);
            bool crcPassed = sampleCrc == ExpectedIeee8023Crc32 && managedSampleCrc == ExpectedIeee8023Crc32;

            // 4. In-Memory DEFLATE / INFLATE round-trip verification
            byte[] originalPayload = "ProjectKongor: Modernized zlib 1.3.1 runtime validation payload for K2 engine VFS."u8.ToArray();
            byte[] compressedBuffer = new byte[originalPayload.Length + 64];
            uint compLen = (uint)compressedBuffer.Length;

            int compRes = compressFn(compressedBuffer, ref compLen, originalPayload, (uint)originalPayload.Length);
            bool compPassed = compRes == 0 && compLen > 0;

            bool roundTripPassed = false;
            if (compPassed)
            {
                byte[] uncompressedBuffer = new byte[originalPayload.Length];
                uint uncompLen = (uint)uncompressedBuffer.Length;

                int uncompRes = uncompressFn(uncompressedBuffer, ref uncompLen, compressedBuffer, compLen);
                roundTripPassed = uncompRes == 0 &&
                                  uncompLen == originalPayload.Length &&
                                  originalPayload.AsSpan().SequenceEqual(uncompressedBuffer.AsSpan(0, (int)uncompLen));
            }

            bool overallSuccess = isModern && crcPassed && roundTripPassed;
            string? errorMsg = overallSuccess ? null :
                (!isModern ? $"Detected non-modern zlib version: {version}" :
                 !crcPassed ? $"CRC32 mismatch: expected 0x{ExpectedIeee8023Crc32:X8}, got 0x{sampleCrc:X8}" :
                 "Round-trip compression/decompression failed.");

            return new ZlibVerificationResult(
                Success: overallSuccess,
                DllPath: fullPath,
                Version: version,
                SampleCrc32: sampleCrc,
                Crc32Passed: crcPassed,
                RoundTripPassed: roundTripPassed,
                SecurityStatus: securityStatus,
                ErrorMessage: errorMsg
            );
        }
        catch (Exception ex)
        {
            return new ZlibVerificationResult(
                Success: false,
                DllPath: fullPath,
                Version: "exception",
                SampleCrc32: 0,
                Crc32Passed: false,
                RoundTripPassed: false,
                SecurityStatus: "EXECUTION_FAULT",
                ErrorMessage: $"Exception executing native functions in '{fullPath}': {ex.Message}"
            );
        }
        finally
        {
            NativeLibrary.Free(handle);
            CleanupIsolatedTempDir(isolatedTempDir);
        }
    }

    /// <summary>
    ///     Attempts to locate the primary candidate or deployed zlibwapi_x64.dll in standard repository locations.
    /// </summary>
    public static string? TryDiscoverZlibPath(string? repoRoot = null)
    {
        string startDir = repoRoot ?? Directory.GetCurrentDirectory();
        DirectoryInfo? dir = new DirectoryInfo(startDir);

        while (dir != null)
        {
            string[] candidates =
            [
                Path.Combine(dir.FullName, "native", "build", "kongor_zlib_build", "Release", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "artifacts", "native-binaries", "windows-x64", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "artifacts", "native-build", "release", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "games", "client", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "games", "client", "windows-x64", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "games", "server", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "games", "server", "windows-x64", "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "zlibwapi_x64.dll"),
                Path.Combine(dir.FullName, "windows-x64", "zlibwapi_x64.dll")
            ];

            foreach (string path in candidates)
            {
                if (File.Exists(path)) return Path.GetFullPath(path);
            }

            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")))
            {
                break;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Opens a live, managed context to the native zlib library for in-memory compression operations.
    /// </summary>
    public static NativeZlibContext LoadContext(string dllPath)
    {
        return new NativeZlibContext(dllPath);
    }

    /// <summary>
    /// Phase 13 (#161): Pure managed CRC-32 IEEE 802.3 calculation via <see cref="ParameterizedRocksoftCrcEngine"/>
    /// when <c>zlibwapi_x64.dll</c> is unavailable or for <c>.s2z</c> archive entry validation.
    /// </summary>
    public static uint ComputeManagedCrc32Ieee(ReadOnlySpan<byte> data)
    {
        return ParameterizedRocksoftCrcEngine.Crc32Ieee.ComputeUInt32(data);
    }

    /// <summary>
    /// Phase 13 (#161): Pure managed CRC-32C (Castagnoli) checksum for packet and replay frame integrity.
    /// </summary>
    public static uint ComputeManagedCrc32Castagnoli(ReadOnlySpan<byte> data)
    {
        return ParameterizedRocksoftCrcEngine.Crc32Castagnoli.ComputeUInt32(data);
    }

    /// <summary>
    /// Phase 13 (#161): Pure managed CRC-16-CCITT-FALSE checksum for netcode header verification.
    /// </summary>
    public static ushort ComputeManagedCrc16Ccitt(ReadOnlySpan<byte> data)
    {
        return ParameterizedRocksoftCrcEngine.Crc16CcittFalse.ComputeUInt16(data);
    }
}

/// <summary>
///     Encapsulates an open handle to zlibwapi_x64.dll for persistent in-memory CRC32 and compression.
/// </summary>
public sealed class NativeZlibContext : IDisposable
{
    private readonly IntPtr _handle;
    private readonly string? _isolatedTempDir;
    private readonly NativeZlibBridge.ZlibVersionFunc _versionFn;
    private readonly NativeZlibBridge.Crc32Func _crc32Fn;
    private readonly NativeZlibBridge.CompressFunc _compressFn;
    private readonly NativeZlibBridge.UncompressFunc _uncompressFn;
    private bool _disposed;

    internal NativeZlibContext(string dllPath)
    {
        string fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Native zlib binary not found at '{fullPath}'.", fullPath);
        }

        string loadPath = NativeZlibBridge.PrepareIsolatedLoadPath(fullPath, out _isolatedTempDir);
        if (!NativeLibrary.TryLoad(loadPath, out _handle) || _handle == IntPtr.Zero)
        {
            NativeZlibBridge.CleanupIsolatedTempDir(_isolatedTempDir);
            throw new DllNotFoundException($"Failed to load native library '{fullPath}'.");
        }

        IntPtr pVer = NativeLibrary.GetExport(_handle, "zlibVersion");
        IntPtr pCrc = NativeLibrary.GetExport(_handle, "crc32");
        IntPtr pComp = NativeLibrary.GetExport(_handle, "compress");
        IntPtr pUncomp = NativeLibrary.GetExport(_handle, "uncompress");

        _versionFn = Marshal.GetDelegateForFunctionPointer<NativeZlibBridge.ZlibVersionFunc>(pVer);
        _crc32Fn = Marshal.GetDelegateForFunctionPointer<NativeZlibBridge.Crc32Func>(pCrc);
        _compressFn = Marshal.GetDelegateForFunctionPointer<NativeZlibBridge.CompressFunc>(pComp);
        _uncompressFn = Marshal.GetDelegateForFunctionPointer<NativeZlibBridge.UncompressFunc>(pUncomp);

        IntPtr verStr = _versionFn();
        Version = Marshal.PtrToStringAnsi(verStr) ?? "unknown";
    }

    public string Version { get; }

    public uint ComputeCrc32(byte[] data, uint initialCrc = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        return _crc32Fn(initialCrc, data, (uint)data.Length);
    }

    public byte[] Compress(byte[] source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        // zlib compressBound approximation: sourceLen + 0.1% + 12
        uint destLen = (uint)(source.Length + (source.Length >> 10) + 16);
        byte[] dest = new byte[destLen];

        int res = _compressFn(dest, ref destLen, source, (uint)source.Length);
        if (res != 0)
        {
            throw new InvalidOperationException($"zlib compress failed with error code {res}.");
        }

        if (destLen == dest.Length) return dest;

        byte[] trimmed = new byte[destLen];
        Buffer.BlockCopy(dest, 0, trimmed, 0, (int)destLen);
        return trimmed;
    }

    public byte[] Decompress(byte[] compressed, int uncompressedSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(compressed);
        ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

        byte[] dest = new byte[uncompressedSize];
        uint destLen = (uint)uncompressedSize;

        int res = _uncompressFn(dest, ref destLen, compressed, (uint)compressed.Length);
        if (res != 0)
        {
            throw new InvalidOperationException($"zlib uncompress failed with error code {res}.");
        }

        return dest;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            NativeLibrary.Free(_handle);
            NativeZlibBridge.CleanupIsolatedTempDir(_isolatedTempDir);
        }
    }
}
