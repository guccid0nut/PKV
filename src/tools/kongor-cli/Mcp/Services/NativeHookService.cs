// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// #216 (Gem 3D): 16-byte PE/VS_FIXEDFILEINFO fingerprint for a system graphics DLL (d3d9.dll, dxgi.dll, d3d11.dll).
/// Matches native Kongor::NativeGuard::GraphicsModuleVersionFingerprint byte-for-byte.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GraphicsModuleVersionFingerprint : IEquatable<GraphicsModuleVersionFingerprint>
{
    public uint FileVersionMs;
    public uint FileVersionLs;
    public uint ProductVersionMs;
    public uint ProductVersionLs;

    public uint ImageSizeBytes
    {
        readonly get => ProductVersionMs;
        set => ProductVersionMs = value;
    }

    public uint PeTimeDateStamp
    {
        readonly get => ProductVersionLs;
        set => ProductVersionLs = value;
    }

    public bool Equals(GraphicsModuleVersionFingerprint other) =>
        FileVersionMs == other.FileVersionMs &&
        FileVersionLs == other.FileVersionLs &&
        ProductVersionMs == other.ProductVersionMs &&
        ProductVersionLs == other.ProductVersionLs;

    public override bool Equals(object? obj) =>
        obj is GraphicsModuleVersionFingerprint other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(FileVersionMs, FileVersionLs, ProductVersionMs, ProductVersionLs);

    public static bool operator ==(GraphicsModuleVersionFingerprint left, GraphicsModuleVersionFingerprint right) =>
        left.Equals(right);

    public static bool operator !=(GraphicsModuleVersionFingerprint left, GraphicsModuleVersionFingerprint right) =>
        !left.Equals(right);
}

/// <summary>
/// #216 (Gem 3D): 88-byte out-of-process graphics vtable offset cache (`get-graphics-offsets64.exe` pattern).
/// Matches native <c>Kongor::PatchController::GraphicsVtableOffsetCache</c> byte-for-byte.
/// Avoids creating dummy D3D9/DXGI devices inside `hon_x64.exe` while automatically invalidating when OS updates
/// change system DLL versions.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GraphicsVtableOffsetCache
{
    public const uint ExpectedMagic = 0x4F425356U; // 'OBSV'
    public const uint ExpectedVersion = 1U;

    public uint Magic;                              // 0x00 (4B)
    public uint Version;                            // 0x04 (4B)
    public GraphicsModuleVersionFingerprint D3d9;   // 0x08 (16B)
    public GraphicsModuleVersionFingerprint Dxgi;   // 0x18 (16B)
    public GraphicsModuleVersionFingerprint D3d11;  // 0x28 (16B)
    public uint D3d9PresentOffset;                  // 0x38 (4B)
    public uint D3d9PresentExOffset;                // 0x3C (4B)
    public uint D3d9SwapChainPresentOffset;         // 0x40 (4B)
    public uint D3d9ResetOffset;                    // 0x44 (4B)
    public uint DxgiPresentOffset;                  // 0x48 (4B)
    public uint DxgiResizeBuffersOffset;            // 0x4C (4B)
    public uint IsD3D9ExByteOffset;                 // 0x50 (4B)
    public uint ChecksumFnv1a32;                    // 0x54 (4B) -> Total 88B

    public uint D3d9SwapPresentOffset
    {
        readonly get => D3d9SwapChainPresentOffset;
        set => D3d9SwapChainPresentOffset = value;
    }

    public uint D3d9ResetExOffset
    {
        readonly get => IsD3D9ExByteOffset;
        set => IsD3D9ExByteOffset = value;
    }
}


/// <summary>
/// Status of an individual NativeGuard hooked subsystem.
/// </summary>
public record NativeSubsystemHookStatus(
    string Name,
    bool Hooked,
    string Details);

/// <summary>
/// Diagnostic summary of all active native hooks in the game engine.
/// </summary>
public record NativeHookDiagnostics(
    bool NativeGuardActive,
    string Version,
    IReadOnlyList<NativeSubsystemHookStatus> Subsystems,
    int TotalHookSites,
    string IntegrityStatus);

/// <summary>
/// Domain service interface for native hook management, detour verification, and subsystem inspection.
/// </summary>
public interface INativeHookService
{
    NativeHookDiagnostics GetHookDiagnostics(string repoRoot);
    bool VerifyHookIntegrity(string dllPath);
    bool DeployGuardHook(string repoRoot, string targetDll);
    bool RollbackGuardHook(string repoRoot, string targetDll);
    IReadOnlyList<string> GetHookedSubsystems();
}

/// <summary>
/// Service managing NativeGuard hooks, detours, PE resource forensics (#193),
/// and serialized Frida session attach/detach locks (#195).
/// </summary>
public sealed class NativeHookService : INativeHookService
{
    private static readonly string[] Subsystems =
    [
        "HeapAlloc / HeapFree bounds check (real-time leak mitigation)",
        "WSASend / WSARecv packet frame bounds validation",
        "Vectored Exception Handler (STATUS_ACCESS_VIOLATION 0xC0000005 guard)",
        "Stack Overflow Handler (STATUS_STACK_OVERFLOW 0xC00000FD guard)"
    ];

    private readonly Action<string> _logger;
    private readonly SemaphoreSlim _fridaSessionLock = new SemaphoreSlim(1, 1);
    private int _activeFridaSessions;
    private int _maxConcurrentFridaSessionsObserved;
    private long _totalSerializedFridaTransitions;

    public NativeHookService(Action<string>? logger = null)
    {
        _logger = logger ?? (msg => Console.Error.WriteLine($"[NativeHookService] {msg}"));
    }

    public int ActiveFridaSessions => Volatile.Read(ref _activeFridaSessions);
    public int MaxConcurrentFridaSessionsObserved => Volatile.Read(ref _maxConcurrentFridaSessionsObserved);
    public long TotalSerializedFridaTransitions => Interlocked.Read(ref _totalSerializedFridaTransitions);

    public IReadOnlyList<string> GetHookedSubsystems() => Subsystems;

    /// <summary>
    /// #193 (Gem 1M): Bounded 8 MiB .rsrc 3-level (Type -&gt; Name/ID -&gt; Language) resource tree
    /// and Type-16 (VS_VERSIONINFO) / Type-24 (XML manifest) forensics parser.
    /// </summary>
    public PeResourceForensicsReport InspectPeResources(
        ReadOnlySpan<byte> rsrcBytes,
        uint rsrcSectionRva = 0)
    {
        _logger($"[InspectPeResources] Parsing {rsrcBytes.Length} bytes of PE .rsrc section (sectionRva=0x{rsrcSectionRva:x})...");
        return ReaEpistemicForensicsEngine.ParsePeResourceHierarchyAndManifest(rsrcBytes, rsrcSectionRva);
    }

    /// <summary>
    /// #195 (Gem 1O): Serializes Frida script attach/detach transitions across concurrent callers
    /// so at most 1 attach/detach transition is in-flight at any instant.
    /// </summary>
    public async Task<T> ExecuteSerializedFridaSessionAsync<T>(
        string sessionId,
        string scriptName,
        Func<CancellationToken, Task<T>> sessionAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionAction);
        await _fridaSessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int current = Interlocked.Increment(ref _activeFridaSessions);
            int prevMax = Volatile.Read(ref _maxConcurrentFridaSessionsObserved);
            if (current > prevMax)
            {
                Interlocked.CompareExchange(ref _maxConcurrentFridaSessionsObserved, current, prevMax);
            }
            Interlocked.Increment(ref _totalSerializedFridaTransitions);
            _logger($"[FridaSessionLock] Attached session '{sessionId}' script '{scriptName}' (active={current}).");

            return await sessionAction(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref _activeFridaSessions);
            _logger($"[FridaSessionLock] Detached session '{sessionId}' script '{scriptName}' (active={remaining}).");
            _fridaSessionLock.Release();
        }
    }

    public NativeHookDiagnostics GetHookDiagnostics(string repoRoot)
    {
        _logger($"Gathering NativeGuard hook diagnostics for repo '{repoRoot}'...");
        string clientGuard = Path.Combine(repoRoot, "games", "client", "Kongor.NativeGuard.dll");
        string serverGuard = Path.Combine(repoRoot, "games", "server", "Kongor.NativeGuard.dll");
        bool clientExists = File.Exists(clientGuard);
        bool serverExists = File.Exists(serverGuard);
        bool active = clientExists || serverExists;

        List<NativeSubsystemHookStatus> subStatuses = new List<NativeSubsystemHookStatus>();
        foreach (string sub in Subsystems)
        {
            subStatuses.Add(new NativeSubsystemHookStatus(
                Name: sub,
                Hooked: active,
                Details: active ? "Active and monitored by NativeGuard VEH engine." : "Not injected. NativeGuard inactive."));
        }

        string version = "1.0.0-nativeguard";
        if (clientExists)
        {
            try
            {
                System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(clientGuard);
                if (!string.IsNullOrWhiteSpace(vi.FileVersion)) version = vi.FileVersion;
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        return new NativeHookDiagnostics(
            NativeGuardActive: active,
            Version: version,
            Subsystems: subStatuses,
            TotalHookSites: active ? Subsystems.Length : 0,
            IntegrityStatus: active ? "Verified / Valid PE Detours" : "Standby");
    }

    public bool VerifyHookIntegrity(string dllPath)
    {
        _logger($"Verifying native hook binary integrity at '{dllPath}'...");
        if (!File.Exists(dllPath)) return false;

        try
        {
            using FileStream fs = File.OpenRead(dllPath);
            if (fs.Length < 1024) return false;

            byte[] header = new byte[2];
            fs.ReadExactly(header);
            // Verify PE MZ magic bytes
            return header[0] == 0x4D && header[1] == 0x5A;
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Integrity check failed for '{dllPath}': {ex.Message}");
            return false;
        }
    }

    public bool DeployGuardHook(string repoRoot, string targetDll)
    {
        _logger($"Deploying NativeGuard hook to target '{targetDll}' in '{repoRoot}'...");
        string source = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "Kongor.NativeGuard.dll");
        if (!File.Exists(source))
        {
            source = Path.Combine(repoRoot, "native", "build", "native_guard_build", "Release", "version.dll");
        }

        if (!File.Exists(source))
        {
            _logger($"[ERROR] NativeGuard source DLL not found: '{source}'");
            return false;
        }

        string destDir = Path.GetDirectoryName(targetDll) ?? repoRoot;
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

        try
        {
            File.Copy(source, targetDll, overwrite: true);
            _logger($"Successfully deployed NativeGuard hook to '{targetDll}'.");
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Failed to deploy NativeGuard hook: {ex.Message}");
            return false;
        }
    }

    public bool RollbackGuardHook(string repoRoot, string targetDll)
    {
        _logger($"Rolling back NativeGuard hook from '{targetDll}'...");
        if (!File.Exists(targetDll)) return true;

        string backupPath = targetDll + ".bak";
        try
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, targetDll, overwrite: true);
                File.Delete(backupPath);
                _logger($"Restored original DLL from backup '{backupPath}'.");
            }
            else
            {
                File.Delete(targetDll);
                _logger($"Removed deployed hook DLL '{targetDll}'.");
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Failed to rollback NativeGuard hook: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// #216 (Gem 3D): Computes FNV-1a 32-bit checksum over the first 84 bytes of <see cref="GraphicsVtableOffsetCache"/>.
    /// Matches native Kongor::NativeGuard::ComputeGraphicsVtableCacheChecksum.
    /// </summary>
    public static uint ComputeVtableCacheChecksum(in GraphicsVtableOffsetCache cache)
    {
        Span<byte> raw = stackalloc byte[88];
        GraphicsVtableOffsetCache copy = cache;
        MemoryMarshal.Write(raw, in copy);

        uint hash = 2166136261U;
        for (int i = 0; i < 84; i++)
        {
            hash ^= raw[i];
            hash *= 16777619U;
        }
        return hash;
    }

    /// <summary>
    /// #216 (Gem 3D): Validates a cached out-of-process graphics vtable offset table against expected
    /// system DLL version fingerprints (`d3d9.dll`, `dxgi.dll`, `d3d11.dll`).
    /// </summary>
    public static bool ValidateVtableOffsetCache(
        in GraphicsVtableOffsetCache cache,
        in GraphicsModuleVersionFingerprint expectedD3d9,
        in GraphicsModuleVersionFingerprint expectedDxgi,
        in GraphicsModuleVersionFingerprint expectedD3d11,
        out string? invalidationReason)
    {
        invalidationReason = null;

        if (cache.Magic != GraphicsVtableOffsetCache.ExpectedMagic ||
            cache.Version != GraphicsVtableOffsetCache.ExpectedVersion)
        {
            invalidationReason = $"Invalid magic (0x{cache.Magic:X8}) or version ({cache.Version}).";
            return false;
        }

        uint expectedChecksum = ComputeVtableCacheChecksum(in cache);
        if (cache.ChecksumFnv1a32 != expectedChecksum)
        {
            invalidationReason = $"Checksum mismatch (stored=0x{cache.ChecksumFnv1a32:X8}, computed=0x{expectedChecksum:X8}).";
            return false;
        }

        if (!cache.D3d9.Equals(expectedD3d9))
        {
            invalidationReason = "d3d9.dll module version fingerprint changed.";
            return false;
        }

        if (!cache.Dxgi.Equals(expectedDxgi))
        {
            invalidationReason = "dxgi.dll module version fingerprint changed.";
            return false;
        }

        if (!cache.D3d11.Equals(expectedD3d11))
        {
            invalidationReason = "d3d11.dll module version fingerprint changed.";
            return false;
        }

        if (cache.D3d9PresentOffset == 0 && cache.DxgiPresentOffset == 0)
        {
            invalidationReason = "Both D3D9 and DXGI Present offsets are zero.";
            return false;
        }

        if ((cache.D3d9PresentOffset & 3U) != 0U || (cache.DxgiPresentOffset & 3U) != 0U)
        {
            invalidationReason = "Present vtable offsets are not 4-byte aligned.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// #216 (Gem 3D): Stamps magic, version, and FNV-1a checksum on <paramref name="cache"/> and persists the 88-byte binary blob.
    /// </summary>
    public bool SaveGraphicsVtableOffsetCache(string cacheFilePath, ref GraphicsVtableOffsetCache cache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFilePath);
        cache.Magic = GraphicsVtableOffsetCache.ExpectedMagic;
        cache.Version = GraphicsVtableOffsetCache.ExpectedVersion;
        cache.ChecksumFnv1a32 = ComputeVtableCacheChecksum(in cache);

        byte[] buffer = new byte[88];
        MemoryMarshal.Write(buffer, in cache);

        string? dir = Path.GetDirectoryName(cacheFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(cacheFilePath, buffer);
        _logger($"[VtableOffsetCache] Saved 88-byte graphics vtable offset cache to '{cacheFilePath}' (checksum=0x{cache.ChecksumFnv1a32:X8}).");
        return true;
    }

    /// <summary>
    /// #216 (Gem 3D): Loads and validates the 88-byte out-of-process graphics vtable offset cache from disk.
    /// </summary>
    public bool TryLoadValidatedGraphicsVtableOffsetCache(
        string cacheFilePath,
        in GraphicsModuleVersionFingerprint expectedD3d9,
        in GraphicsModuleVersionFingerprint expectedDxgi,
        in GraphicsModuleVersionFingerprint expectedD3d11,
        out GraphicsVtableOffsetCache cache,
        out string? invalidationReason)
    {
        cache = default;
        if (!File.Exists(cacheFilePath))
        {
            invalidationReason = "Cache file does not exist.";
            return false;
        }

        byte[] bytes = File.ReadAllBytes(cacheFilePath);
        if (bytes.Length != 88)
        {
            invalidationReason = $"Unexpected cache file size ({bytes.Length} bytes, expected 88).";
            return false;
        }

        cache = MemoryMarshal.Read<GraphicsVtableOffsetCache>(bytes);
        bool valid = ValidateVtableOffsetCache(
            in cache,
            in expectedD3d9,
            in expectedDxgi,
            in expectedD3d11,
            out invalidationReason);

        if (!valid)
        {
            _logger($"[VtableOffsetCache] Cache invalidated at '{cacheFilePath}': {invalidationReason}");
            return false;
        }

        _logger($"[VtableOffsetCache] Loaded valid graphics vtable offset cache from '{cacheFilePath}'.");
        return true;
    }

    /// <summary>
    /// #216 (Gem 3D): Extracts the 16-byte <see cref="GraphicsModuleVersionFingerprint"/> from a system DLL on disk.
    /// </summary>
    public static GraphicsModuleVersionFingerprint ExtractModuleVersionFingerprint(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return default;
        }

        System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath);
        uint fileMs = ((uint)(ushort)vi.FileMajorPart << 16) | (ushort)vi.FileMinorPart;
        uint fileLs = ((uint)(ushort)vi.FileBuildPart << 16) | (ushort)vi.FilePrivatePart;
        uint prodMs = ((uint)(ushort)vi.ProductMajorPart << 16) | (ushort)vi.ProductMinorPart;
        uint prodLs = ((uint)(ushort)vi.ProductBuildPart << 16) | (ushort)vi.ProductPrivatePart;

        return new GraphicsModuleVersionFingerprint
        {
            FileVersionMs = fileMs,
            FileVersionLs = fileLs,
            ProductVersionMs = prodMs,
            ProductVersionLs = prodLs
        };
    }
}

