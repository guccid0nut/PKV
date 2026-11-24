// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents an imported dynamic link library and its imported symbol functions.
/// </summary>
public sealed record PeImportedDll(
    string DllName,
    uint NameRva,
    int NameFileOffset,
    uint FirstThunkRva,
    uint OriginalFirstThunkRva,
    IReadOnlyList<string> ImportedFunctions
);

/// <summary>
///     Comprehensive PE import directory inspection report.
/// </summary>
public sealed record PeImportReport(
    string FilePath,
    bool IsValidPe,
    bool Is64Bit,
    uint Checksum,
    IReadOnlyList<PeImportedDll> ImportedModules,
    string? ErrorMessage = null
);

/// <summary>
///     Result of an Import Address Table (IAT) DLL name redirection operation.
/// </summary>
public sealed record PeRedirectionResult(
    bool Success,
    string Message,
    string TargetBinary,
    string OldDllName,
    string NewDllName,
    bool UsedCavitySlack,
    string? BackupPath = null,
    string? RotatedBakOldPath = null
);

/// <summary>
///     Result of batch Import Address Table (IAT) DLL name redirection operations.
/// </summary>
public sealed record PeBatchRedirectionResult(
    bool Success,
    string Message,
    string TargetBinary,
    int RedirectedCount,
    IReadOnlyDictionary<string, string> AppliedRedirections,
    bool AnyUsedCavitySlack,
    string? BackupPath = null,
    string? RotatedBakOldPath = null
);

/// <summary>
///     Section metadata for RVA to file offset translation.
/// </summary>
public sealed record PeSectionInfo(
    string Name,
    uint VirtualSize,
    uint VirtualAddress,
    uint SizeOfRawData,
    uint PointerToRawData,
    uint Characteristics,
    int SectionHeaderOffset
);

/// <summary>
///     Managed PE Import Address Table (IAT) reader, inspector, and binary redirection engine.
///     Enables dynamic redirection of imported DLL module names (e.g., redirecting libssl-1_1-x64.dll
///     or libcurl_x64.dll to modern drop-in shims) and recalculates PE checksums with rolling backups.
/// </summary>
public static class PeImportAddressTableService
{
    private const ushort DosMagic = 0x5A4D; // "MZ"
    private const uint PeSignature = 0x00004550; // "PE\0\0"
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B; // 64-bit

    /// <summary>
    ///     Inspects and extracts all imported DLL module names and imported functions from a PE binary file.
    /// </summary>
    public static PeImportReport GetImports(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return new PeImportReport(filePath, false, false, 0, Array.Empty<PeImportedDll>(), $"File not found: {filePath}");
        }

        byte[] peBytes = File.ReadAllBytes(filePath);
        return GetImports(peBytes, filePath);
    }

    /// <summary>
    ///     Inspects and extracts all imported DLL module names and imported functions from an in-memory PE byte buffer.
    /// </summary>
    public static PeImportReport GetImports(byte[] peBytes, string filePath = "memory.dll")
    {
        ArgumentNullException.ThrowIfNull(peBytes);

        if (!TryParsePeHeaders(peBytes, out PeHeaderContext ctx, out string? error))
        {
            return new PeImportReport(filePath, false, false, 0, Array.Empty<PeImportedDll>(), error);
        }

        if (ctx.ImportDirectoryRva == 0 || ctx.ImportDirectorySize == 0)
        {
            return new PeImportReport(filePath, true, ctx.Is64Bit, ctx.Checksum, Array.Empty<PeImportedDll>());
        }

        int importDescriptorOffset = RvaToFileOffset(ctx.ImportDirectoryRva, ctx.Sections);
        if (importDescriptorOffset < 0 || importDescriptorOffset + 20 > peBytes.Length)
        {
            return new PeImportReport(filePath, true, ctx.Is64Bit, ctx.Checksum, Array.Empty<PeImportedDll>(),
                $"Import Directory RVA 0x{ctx.ImportDirectoryRva:X8} points outside raw file bounds.");
        }

        List<PeImportedDll> importedDlls = [];
        int currentOffset = importDescriptorOffset;

        while (currentOffset + 20 <= peBytes.Length)
        {
            uint originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentOffset, 4));
            uint timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentOffset + 4, 4));
            uint forwarderChain = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentOffset + 8, 4));
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentOffset + 12, 4));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentOffset + 16, 4));

            // Terminating null descriptor: all 20 bytes are zero
            if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            string dllName = string.Empty;
            int nameFileOffset = -1;

            if (nameRva != 0)
            {
                nameFileOffset = RvaToFileOffset(nameRva, ctx.Sections);
                if (nameFileOffset >= 0 && nameFileOffset < peBytes.Length)
                {
                    dllName = ReadNullTerminatedAscii(peBytes, nameFileOffset);
                }
            }

            List<string> functions = [];
            uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            if (thunkRva != 0)
            {
                int thunkOffset = RvaToFileOffset(thunkRva, ctx.Sections);
                if (thunkOffset >= 0)
                {
                    functions = ReadImportedFunctions(peBytes, thunkOffset, ctx.Is64Bit, ctx.Sections);
                }
            }

            if (!string.IsNullOrEmpty(dllName))
            {
                importedDlls.Add(new PeImportedDll(
                    dllName,
                    nameRva,
                    nameFileOffset,
                    firstThunk,
                    originalFirstThunk,
                    functions
                ));
            }

            currentOffset += 20;
        }

        return new PeImportReport(filePath, true, ctx.Is64Bit, ctx.Checksum, importedDlls);
    }

    /// <summary>
    ///     Redirects an imported DLL module name in a PE binary file on disk.
    ///     Performs pre-flight running process detection, creates two-tier backups (.bak and .bak.old),
    ///     updates the import directory string or section slack cavity, and recalculates the PE checksum.
    /// </summary>
    public static PeRedirectionResult RedirectImportedDll(
        string filePath,
        string oldDllName,
        string newDllName,
        bool createBackup = true,
        bool skipProcessCheck = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDllName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDllName);

        if (!File.Exists(filePath))
        {
            return new PeRedirectionResult(false, $"File does not exist: {filePath}", filePath, oldDllName, newDllName, false);
        }

        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            throw new InvalidOperationException(
                $"Cannot modify native binary while game client or server processes are running ({runningProcs}). Please terminate them first.");
        }

        byte[] peBytes = File.ReadAllBytes(filePath);

        bool redirected = TryRedirectImportedDllInMemory(
            peBytes,
            oldDllName,
            newDllName,
            out bool usedCavitySlack,
            out string? errorMessage);

        if (!redirected)
        {
            return new PeRedirectionResult(false, errorMessage ?? "Redirection failed.", filePath, oldDllName, newDllName, false);
        }

        string? backupPath = null;
        string? rotatedBakOldPath = null;

        if (createBackup)
        {
            string bak = filePath + ".bak";
            string bakOld = filePath + ".bak.old";

            // Rotate existing .bak to .bak.old to preserve historical baseline
            if (File.Exists(bak))
            {
                File.Copy(bak, bakOld, true);
                rotatedBakOldPath = bakOld;
            }

            File.Copy(filePath, bak, true);
            backupPath = bak;
        }

        File.WriteAllBytes(filePath, peBytes);

        return new PeRedirectionResult(
            true,
            $"Successfully redirected import '{oldDllName}' to '{newDllName}' in '{Path.GetFileName(filePath)}'.",
            filePath,
            oldDllName,
            newDllName,
            usedCavitySlack,
            backupPath,
            rotatedBakOldPath);
    }

    /// <summary>
    ///     Redirects multiple imported DLL module names in a PE binary file on disk in a single atomic pass.
    ///     Performs pre-flight running process detection, creates two-tier backups (.bak and .bak.old),
    ///     updates the import directory strings or section slack cavities, and recalculates the PE checksum.
    /// </summary>
    public static PeBatchRedirectionResult RedirectImportedDlls(
        string filePath,
        IReadOnlyDictionary<string, string> redirections,
        bool createBackup = true,
        bool skipProcessCheck = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(redirections);

        if (!File.Exists(filePath))
        {
            return new PeBatchRedirectionResult(false, $"File does not exist: {filePath}", filePath, 0, new Dictionary<string, string>(), false);
        }

        if (redirections.Count == 0)
        {
            return new PeBatchRedirectionResult(true, "No redirections requested.", filePath, 0, new Dictionary<string, string>(), false);
        }

        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            throw new InvalidOperationException(
                $"Cannot modify native binary while game client or server processes are running ({runningProcs}). Please terminate them first.");
        }

        byte[] peBytes = File.ReadAllBytes(filePath);

        bool redirected = TryRedirectImportedDllsInMemory(
            peBytes,
            redirections,
            out int redirectedCount,
            out var applied,
            out bool anyUsedCavitySlack,
            out string? errorMessage);

        if (!redirected)
        {
            return new PeBatchRedirectionResult(false, errorMessage ?? "Batch redirection failed.", filePath, 0, new Dictionary<string, string>(), false);
        }

        string? backupPath = null;
        string? rotatedBakOldPath = null;

        if (createBackup)
        {
            string bak = filePath + ".bak";
            string bakOld = filePath + ".bak.old";

            // Rotate existing .bak to .bak.old to preserve historical baseline
            if (File.Exists(bak))
            {
                File.Copy(bak, bakOld, true);
                rotatedBakOldPath = bakOld;
            }

            File.Copy(filePath, bak, true);
            backupPath = bak;
        }

        File.WriteAllBytes(filePath, peBytes);

        return new PeBatchRedirectionResult(
            true,
            $"Successfully redirected {redirectedCount} imports in '{Path.GetFileName(filePath)}'.",
            filePath,
            redirectedCount,
            applied,
            anyUsedCavitySlack,
            backupPath,
            rotatedBakOldPath);
    }

    /// <summary>
    ///     Performs Import Address Table redirection in an in-memory PE byte buffer.
    ///     Supports in-place replacement (length <= original) and section alignment cavity injection (length > original).
    ///     Recalculates PE checksum upon successful redirection.
    /// </summary>
    public static bool TryRedirectImportedDllInMemory(
        byte[] peBytes,
        string oldDllName,
        string newDllName,
        out bool usedCavitySlack,
        out string? errorMessage)
    {
        usedCavitySlack = false;
        errorMessage = null;

        if (!TryParsePeHeaders(peBytes, out PeHeaderContext ctx, out string? headerError))
        {
            errorMessage = headerError;
            return false;
        }

        if (ctx.ImportDirectoryRva == 0 || ctx.ImportDirectorySize == 0)
        {
            errorMessage = "PE binary does not contain an Import Table Directory.";
            return false;
        }

        int importDescriptorOffset = RvaToFileOffset(ctx.ImportDirectoryRva, ctx.Sections);
        if (importDescriptorOffset < 0 || importDescriptorOffset + 20 > peBytes.Length)
        {
            errorMessage = $"Import Directory RVA 0x{ctx.ImportDirectoryRva:X8} resolves outside file bounds.";
            return false;
        }

        int currentDescriptorOffset = importDescriptorOffset;
        bool foundMatch = false;

        while (currentDescriptorOffset + 20 <= peBytes.Length)
        {
            uint originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset, 4));
            uint timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 4, 4));
            uint forwarderChain = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 8, 4));
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 12, 4));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 16, 4));

            // Terminating null descriptor
            if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            if (nameRva != 0)
            {
                int nameFileOffset = RvaToFileOffset(nameRva, ctx.Sections);
                if (nameFileOffset >= 0 && nameFileOffset < peBytes.Length)
                {
                    string currentDllName = ReadNullTerminatedAscii(peBytes, nameFileOffset);
                    if (string.Equals(currentDllName, oldDllName, StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] newNameAscii = Encoding.ASCII.GetBytes(newDllName);

                        if (newDllName.Length <= currentDllName.Length)
                        {
                            // In-place rewrite: write new name and null-pad the remainder
                            newNameAscii.CopyTo(peBytes.AsSpan(nameFileOffset));
                            int remaining = (currentDllName.Length + 1) - newNameAscii.Length;
                            peBytes.AsSpan(nameFileOffset + newNameAscii.Length, remaining).Clear();
                            usedCavitySlack = false;
                        }
                        else
                        {
                            // Replacement name is longer: allocate from section slack cavity
                            if (!TryAllocateSectionSlackCavity(peBytes, ctx, newNameAscii, out uint newNameRva, out string? cavityError))
                            {
                                errorMessage = cavityError;
                                return false;
                            }

                            // Update Name RVA in IMAGE_IMPORT_DESCRIPTOR
                            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 12, 4), newNameRva);
                            usedCavitySlack = true;
                        }

                        foundMatch = true;
                        break;
                    }
                }
            }

            currentDescriptorOffset += 20;
        }

        if (!foundMatch)
        {
            errorMessage = $"Imported DLL '{oldDllName}' was not found in the binary's Import Directory.";
            return false;
        }

        // Recalculate PE Checksum
        uint newChecksum = CalculatePeChecksum(peBytes, ctx.ChecksumOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(ctx.ChecksumOffset, 4), newChecksum);

        return true;
    }

    /// <summary>
    ///     Performs batch Import Address Table redirection in an in-memory PE byte buffer.
    ///     Applies multiple DLL redirections in a single atomic pass and recalculates the PE checksum.
    /// </summary>
    public static bool TryRedirectImportedDllsInMemory(
        byte[] peBytes,
        IReadOnlyDictionary<string, string> redirections,
        out int redirectedCount,
        out IReadOnlyDictionary<string, string> applied,
        out bool anyUsedCavitySlack,
        out string? errorMessage)
    {
        redirectedCount = 0;
        anyUsedCavitySlack = false;
        errorMessage = null;
        var appliedDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        applied = appliedDict;

        if (!TryParsePeHeaders(peBytes, out PeHeaderContext ctx, out string? headerError))
        {
            errorMessage = headerError;
            return false;
        }

        if (ctx.ImportDirectoryRva == 0 || ctx.ImportDirectorySize == 0)
        {
            errorMessage = "PE binary does not contain an Import Table Directory.";
            return false;
        }

        int importDescriptorOffset = RvaToFileOffset(ctx.ImportDirectoryRva, ctx.Sections);
        if (importDescriptorOffset < 0 || importDescriptorOffset + 20 > peBytes.Length)
        {
            errorMessage = $"Import Directory RVA 0x{ctx.ImportDirectoryRva:X8} resolves outside file bounds.";
            return false;
        }

        // Build lookup map: oldDllName (case-insensitive) -> newDllName
        var pendingRedirections = new Dictionary<string, string>(redirections, StringComparer.OrdinalIgnoreCase);

        int currentDescriptorOffset = importDescriptorOffset;
        while (currentDescriptorOffset + 20 <= peBytes.Length && pendingRedirections.Count > 0)
        {
            uint originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset, 4));
            uint timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 4, 4));
            uint forwarderChain = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 8, 4));
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 12, 4));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 16, 4));

            if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            if (nameRva != 0)
            {
                int nameFileOffset = RvaToFileOffset(nameRva, ctx.Sections);
                if (nameFileOffset >= 0 && nameFileOffset < peBytes.Length)
                {
                    string currentDllName = ReadNullTerminatedAscii(peBytes, nameFileOffset);
                    if (pendingRedirections.TryGetValue(currentDllName, out string? newDllName))
                    {
                        byte[] newNameAscii = Encoding.ASCII.GetBytes(newDllName);

                        if (newDllName.Length <= currentDllName.Length)
                        {
                            newNameAscii.CopyTo(peBytes.AsSpan(nameFileOffset));
                            int remaining = (currentDllName.Length + 1) - newNameAscii.Length;
                            peBytes.AsSpan(nameFileOffset + newNameAscii.Length, remaining).Clear();
                        }
                        else
                        {
                            if (!TryAllocateSectionSlackCavity(peBytes, ctx, newNameAscii, out uint newNameRva, out string? cavityError))
                            {
                                errorMessage = cavityError;
                                return false;
                            }

                            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(currentDescriptorOffset + 12, 4), newNameRva);
                            anyUsedCavitySlack = true;
                        }

                        redirectedCount++;
                        appliedDict[currentDllName] = newDllName;
                        pendingRedirections.Remove(currentDllName);
                    }
                }
            }

            currentDescriptorOffset += 20;
        }

        if (redirectedCount == 0)
        {
            errorMessage = "None of the specified target DLLs were found in the binary's Import Directory.";
            return false;
        }

        // Recalculate PE Checksum
        uint newChecksum = CalculatePeChecksum(peBytes, ctx.ChecksumOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(ctx.ChecksumOffset, 4), newChecksum);

        return true;
    }

    /// <summary>
    ///     Allocates space for a string within the alignment slack cavity of a suitable PE section (e.g. .rdata),
    ///     updates the section's VirtualSize, and writes the ASCII string with a null terminator.
    /// </summary>
    private static bool TryAllocateSectionSlackCavity(
        byte[] peBytes,
        PeHeaderContext ctx,
        byte[] asciiData,
        out uint allocatedRva,
        out string? errorMessage)
    {
        allocatedRva = 0;
        errorMessage = null;

        int requiredBytes = asciiData.Length + 1; // including null terminator

        // Search sections that permit data and have slack space (SizeOfRawData > VirtualSize)
        foreach (PeSectionInfo section in ctx.Sections)
        {
            if (section.SizeOfRawData > section.VirtualSize)
            {
                uint slackBytes = section.SizeOfRawData - section.VirtualSize;
                if (slackBytes >= requiredBytes + 4)
                {
                    // Found suitable section slack space
                    int writeOffset = (int)(section.PointerToRawData + section.VirtualSize);
                    allocatedRva = section.VirtualAddress + section.VirtualSize;

                    if (writeOffset + requiredBytes <= peBytes.Length)
                    {
                        asciiData.CopyTo(peBytes.AsSpan(writeOffset));
                        peBytes[writeOffset + asciiData.Length] = 0; // null terminator

                        // Update section's VirtualSize in its Section Header
                        uint updatedVirtualSize = section.VirtualSize + (uint)requiredBytes;
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            peBytes.AsSpan(section.SectionHeaderOffset + 8, 4),
                            updatedVirtualSize);

                        // Update in-memory record in ctx.Sections so subsequent allocations don't collide
                        int secIdx = ctx.Sections.IndexOf(section);
                        if (secIdx >= 0)
                        {
                            ctx.Sections[secIdx] = section with { VirtualSize = updatedVirtualSize };
                        }

                        return true;
                    }
                }
            }
        }

        errorMessage = $"Insufficient section alignment slack space to accommodate {requiredBytes} bytes for replacement DLL name.";
        return false;
    }

    /// <summary>
    ///     Calculates the standard PE Checksum conforming to Microsoft PE/COFF specification.
    /// </summary>
    public static uint CalculatePeChecksum(Span<byte> peBytes, int checksumOffset)
    {
        ulong checksum = 0;
        int length = peBytes.Length;

        for (int i = 0; i < length - 1; i += 2)
        {
            // Treat the existing CheckSum field as 0
            if (i == checksumOffset || i == checksumOffset + 2)
            {
                continue;
            }

            ushort word = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.Slice(i, 2));
            checksum += word;
            if (checksum > 0xFFFFFFFF)
            {
                checksum = (checksum & 0xFFFFFFFF) + (checksum >> 32);
            }
        }

        // Add odd trailing byte if length is odd
        if ((length & 1) != 0)
        {
            checksum += peBytes[length - 1];
            if (checksum > 0xFFFFFFFF)
            {
                checksum = (checksum & 0xFFFFFFFF) + (checksum >> 32);
            }
        }

        while ((checksum >> 16) != 0)
        {
            checksum = (checksum & 0xFFFF) + (checksum >> 16);
        }

        checksum += (ulong)length;
        return (uint)(checksum & 0xFFFFFFFF);
    }

    /// <summary>
    ///     Translates a Relative Virtual Address (RVA) to a raw file offset using PE section headers.
    /// </summary>
    public static int RvaToFileOffset(uint rva, IReadOnlyList<PeSectionInfo> sections)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            PeSectionInfo sec = sections[i];
            uint extent = Math.Max(sec.VirtualSize, sec.SizeOfRawData);
            if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + extent)
            {
                uint diff = rva - sec.VirtualAddress;
                if (diff < sec.SizeOfRawData)
                {
                    return (int)(sec.PointerToRawData + diff);
                }

                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Phase 22 (#264): Resolves an RVA to a raw file offset for on-disk PE binaries ("MZ" + "PE\0\0").
    ///     For non-PE raw test buffers (not starting with "MZ"), treats <paramref name="rvaOrOffset"/> as a direct file offset.
    /// </summary>
    public static bool TryResolveFileOffsetFromRva(
        string filePath,
        long rvaOrOffset,
        out long resolvedFileOffset,
        out bool wasTranslatedFromPeRva,
        out string? error)
    {
        resolvedFileOffset = -1;
        wasTranslatedFromPeRva = false;
        error = null;

        if (rvaOrOffset < 0)
        {
            error = $"Negative RVA or offset (0x{rvaOrOffset:X}) is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            error = $"Binary file not found at '{filePath}'.";
            return false;
        }

        try
        {
            using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileLen = fs.Length;
            int headerReadLen = (int)Math.Min(fileLen, 65536L);
            byte[] headerBytes = new byte[headerReadLen];
            fs.ReadExactly(headerBytes, 0, headerReadLen);

            if (headerReadLen >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(0, 2)) == DosMagic)
            {
                if (!TryParsePeHeaders(headerBytes, out PeHeaderContext ctx, out string? peErr))
                {
                    error = $"File '{filePath}' has DOS 'MZ' magic but invalid PE headers: {peErr}";
                    return false;
                }

                if (rvaOrOffset > uint.MaxValue)
                {
                    error = $"RVA 0x{rvaOrOffset:X} exceeds 32-bit PE RVA bounds.";
                    return false;
                }

                int mapped = RvaToFileOffset((uint)rvaOrOffset, ctx.Sections);
                if (mapped < 0 || mapped >= fileLen)
                {
                    error = $"PE RVA 0x{rvaOrOffset:X} does not map to any valid raw section in '{filePath}'.";
                    return false;
                }

                resolvedFileOffset = mapped;
                wasTranslatedFromPeRva = true;
                return true;
            }

            if (rvaOrOffset >= fileLen)
            {
                error = $"Offset 0x{rvaOrOffset:X} exceeds file length ({fileLen} bytes) in '{filePath}'.";
                return false;
            }

            resolvedFileOffset = rvaOrOffset;
            wasTranslatedFromPeRva = false;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to resolve RVA 0x{rvaOrOffset:X} in '{filePath}': {ex.Message}";
            return false;
        }
    }

    private static string ReadNullTerminatedAscii(byte[] bytes, int startOffset)
    {
        int curr = startOffset;
        while (curr < bytes.Length && bytes[curr] != 0 && (curr - startOffset) < 1024)
        {
            curr++;
        }

        return Encoding.ASCII.GetString(bytes, startOffset, curr - startOffset);
    }

    private static List<string> ReadImportedFunctions(byte[] peBytes, int thunkOffset, bool is64Bit, IReadOnlyList<PeSectionInfo> sections)
    {
        List<string> functions = [];
        int entrySize = is64Bit ? 8 : 4;
        int currentOffset = thunkOffset;

        while (currentOffset + entrySize <= peBytes.Length && functions.Count < 2048)
        {
            ulong thunkValue = is64Bit
                ? BinaryPrimitives.ReadUInt64LittleEndian(peBytes.AsSpan(currentOffset, 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(currentOffset, 4));

            if (thunkValue == 0) break; // End of thunk array

            ulong ordinalMask = is64Bit ? 0x8000000000000000UL : 0x80000000UL;
            if ((thunkValue & ordinalMask) != 0)
            {
                // Import by ordinal
                functions.Add($"Ordinal_{thunkValue & 0xFFFF}");
            }
            else
            {
                // Import by name: lower 31 bits point to IMAGE_IMPORT_BY_NAME
                uint hintNameRva = (uint)(thunkValue & 0x7FFFFFFF);
                int hintNameOffset = RvaToFileOffset(hintNameRva, sections);
                if (hintNameOffset >= 0 && hintNameOffset + 2 < peBytes.Length)
                {
                    // First 2 bytes are Hint (ushort), followed by ASCII function name
                    string funcName = ReadNullTerminatedAscii(peBytes, hintNameOffset + 2);
                    if (!string.IsNullOrEmpty(funcName))
                    {
                        functions.Add(funcName);
                    }
                }
            }

            currentOffset += entrySize;
        }

        return functions;
    }

    private static bool TryParsePeHeaders(byte[] peBytes, out PeHeaderContext context, out string? error)
    {
        context = default;
        error = null;

        if (peBytes.Length < 0x40)
        {
            error = "Buffer is too small to contain a valid DOS header.";
            return false;
        }

        ushort dosMagic = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.AsSpan(0, 2));
        if (dosMagic != DosMagic)
        {
            error = "Invalid DOS header magic: missing 'MZ' signature.";
            return false;
        }

        int eLfanew = BinaryPrimitives.ReadInt32LittleEndian(peBytes.AsSpan(0x3C, 4));
        if (eLfanew <= 0 || eLfanew + 24 > peBytes.Length)
        {
            error = "Invalid e_lfanew pointer in DOS header.";
            return false;
        }

        uint peSig = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(eLfanew, 4));
        if (peSig != PeSignature)
        {
            error = "Invalid NT header signature: missing 'PE\\0\\0'.";
            return false;
        }

        int fileHeaderOffset = eLfanew + 4;
        ushort numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 2, 2));
        ushort sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 16, 2));

        int optHeaderOffset = fileHeaderOffset + 20;
        if (optHeaderOffset + sizeOfOptionalHeader > peBytes.Length || sizeOfOptionalHeader < 96)
        {
            error = "Invalid or truncated PE Optional Header.";
            return false;
        }

        ushort optMagic = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset, 2));
        bool is64Bit = optMagic == Pe32PlusMagic;
        if (!is64Bit && optMagic != Pe32Magic)
        {
            error = $"Unsupported PE Optional Header magic 0x{optMagic:X4}.";
            return false;
        }

        int checksumOffset = optHeaderOffset + 64;
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(checksumOffset, 4));

        int dataDirectoriesOffset = is64Bit ? optHeaderOffset + 112 : optHeaderOffset + 96;
        int importDirEntryOffset = dataDirectoriesOffset + 8; // DataDirectory[1]

        uint importRva = 0;
        uint importSize = 0;
        if (importDirEntryOffset + 8 <= optHeaderOffset + sizeOfOptionalHeader)
        {
            importRva = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(importDirEntryOffset, 4));
            importSize = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(importDirEntryOffset + 4, 4));
        }

        int sectionHeadersOffset = optHeaderOffset + sizeOfOptionalHeader;
        List<PeSectionInfo> sections = [];

        for (int i = 0; i < numberOfSections; i++)
        {
            int secOffset = sectionHeadersOffset + (i * 40);
            if (secOffset + 40 > peBytes.Length) break;

            string secName = ReadNullTerminatedAscii(peBytes, secOffset).Trim();
            uint vSize = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(secOffset + 8, 4));
            uint vAddr = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(secOffset + 12, 4));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(secOffset + 16, 4));
            uint rawPtr = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(secOffset + 20, 4));
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.AsSpan(secOffset + 36, 4));

            sections.Add(new PeSectionInfo(secName, vSize, vAddr, rawSize, rawPtr, characteristics, secOffset));
        }

        context = new PeHeaderContext(
            eLfanew,
            is64Bit,
            checksum,
            checksumOffset,
            importRva,
            importSize,
            sections
        );

        return true;
    }

    /// <summary>
    ///     Inspects imports of an engine binary located in the specified game directory.
    /// </summary>
    public static PeImportReport InspectClientBinaryImports(string gameDirectory, string relativeBinaryPath)
    {
        string fullPath = Path.Combine(gameDirectory, relativeBinaryPath);
        return GetImports(fullPath);
    }

    /// <summary>
    ///     Redirects an imported DLL name in a target binary within the game directory,
    ///     maintaining verified two-tier backups (.bak and .bak.old).
    /// </summary>
    public static PeRedirectionResult RedirectClientBinaryImport(
        string gameDirectory,
        string relativeBinaryPath,
        string oldDllName,
        string newDllName,
        Action<string>? logAction = null)
    {
        string fullPath = Path.Combine(gameDirectory, relativeBinaryPath);

        if (!File.Exists(fullPath))
        {
            string msg = $"Target binary not found: {fullPath}";
            logAction?.Invoke($"[PE_IAT] {msg}");
            return new PeRedirectionResult(false, msg, fullPath, oldDllName, newDllName, false);
        }

        try
        {
            PeRedirectionResult result = RedirectImportedDll(
                fullPath,
                oldDllName,
                newDllName,
                createBackup: true,
                skipProcessCheck: false);

            if (result.Success)
            {
                logAction?.Invoke(
                    $"[PE_IAT] Successfully redirected '{oldDllName}' -> '{newDllName}' in '{relativeBinaryPath}' (Cavity slack: {result.UsedCavitySlack}).");
            }
            else
            {
                logAction?.Invoke($"[PE_IAT] Redirection failed for '{relativeBinaryPath}': {result.Message}");
            }

            return result;
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[PE_IAT] Redirection exception for '{relativeBinaryPath}': {ex.Message}");
            return new PeRedirectionResult(false, ex.Message, fullPath, oldDllName, newDllName, false);
        }
    }

    private readonly record struct PeHeaderContext(
        int ELfanew,
        bool Is64Bit,
        uint Checksum,
        int ChecksumOffset,
        uint ImportDirectoryRva,
        uint ImportDirectorySize,
        List<PeSectionInfo> Sections
    );
}
