// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kongor.Core.Services.Native;

/// <summary>
/// Phase 16 (#196 — ran-j/PS2Recomp Gem 4A): Single 32-bit instruction word and relocation bitmask
/// where 1-bits denote invariant opcode/register bits and 0-bits mask out relocated immediate/displacement bits.
/// </summary>
public sealed record MaskedInstructionPattern(uint InstructionWord, uint Mask)
{
    public uint MaskedWord => InstructionWord & Mask;
    public int StaticBits => BitOperations.PopCount(Mask);
}

/// <summary>
/// Phase 16 (#196 — ran-j/PS2Recomp Gem 4A): Relocation-masked function signature enforcing
/// <c>StaticBitCount &gt;= 256</c> and <c>InstructionCount &gt;= 8</c> to prevent false-positive collisions on short thunks.
/// </summary>
public sealed record RelocationMaskedSignature(
    string SymbolName,
    IReadOnlyList<MaskedInstructionPattern> Patterns,
    int InstructionCount,
    int StaticBitCount,
    string MaskedSha1Hex,
    bool IsEligibleForCorrelation,
    string RejectionReason);

/// <summary>
/// Phase 16 (#196 — ran-j/PS2Recomp Gem 4A): Directed edge in the word-masked decision-tree trie.
/// </summary>
public sealed class MatchEdge
{
    public required uint Mask { get; init; }
    public required uint MaskedValue { get; init; }
    public required MatchNode Target { get; init; }
}

/// <summary>
/// Phase 16 (#196 — ran-j/PS2Recomp Gem 4A): Decision-tree trie node keyed on <c>(instructionWord &amp; mask)</c>.
/// </summary>
public sealed class MatchNode
{
    public List<MatchEdge> Edges { get; } = new List<MatchEdge>();
    public List<string> MatchedSymbols { get; } = new List<string>();
}

/// <summary>
/// Phase 16 (#196 — ran-j/PS2Recomp Gem 4A): Word-masked decision-tree trie and relocation-masked SHA-1 signature scanner.
/// </summary>
internal sealed class RelocationMaskedSignatureTrie
{
    public const int MinStaticBits = SymbolCorrelatorService.MinStaticBits;
    public const int MinInstructionCount = SymbolCorrelatorService.MinInstructionCount;

    private readonly Dictionary<string, List<string>> _sha1Index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public MatchNode Root { get; } = new MatchNode();
    public int InsertedSignatureCount { get; private set; }
    public int RejectedShortSignatureCount { get; private set; }

    public bool TryInsert(RelocationMaskedSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (!signature.IsEligibleForCorrelation ||
            signature.InstructionCount < MinInstructionCount ||
            signature.StaticBitCount < MinStaticBits ||
            !SymbolCorrelatorService.IsReliableSymbolName(signature.SymbolName))
        {
            RejectedShortSignatureCount++;
            return false;
        }

        MatchNode current = Root;
        for (int i = 0; i < signature.Patterns.Count; i++)
        {
            MaskedInstructionPattern pat = signature.Patterns[i];
            uint maskedVal = pat.InstructionWord & pat.Mask;
            MatchEdge? matchedEdge = null;

            for (int e = 0; e < current.Edges.Count; e++)
            {
                MatchEdge candidate = current.Edges[e];
                if (candidate.Mask == pat.Mask && candidate.MaskedValue == maskedVal)
                {
                    matchedEdge = candidate;
                    break;
                }
            }

            if (matchedEdge == null)
            {
                matchedEdge = new MatchEdge
                {
                    Mask = pat.Mask,
                    MaskedValue = maskedVal,
                    Target = new MatchNode()
                };
                current.Edges.Add(matchedEdge);
            }

            current = matchedEdge.Target;
        }

        if (!current.MatchedSymbols.Contains(signature.SymbolName, StringComparer.Ordinal))
        {
            current.MatchedSymbols.Add(signature.SymbolName);
        }

        if (!_sha1Index.TryGetValue(signature.MaskedSha1Hex, out List<string>? bucket))
        {
            bucket = new List<string>();
            _sha1Index[signature.MaskedSha1Hex] = bucket;
        }

        if (!bucket.Contains(signature.SymbolName, StringComparer.Ordinal))
        {
            bucket.Add(signature.SymbolName);
        }

        InsertedSignatureCount++;
        return true;
    }

    public IReadOnlyList<string> MatchInstructionStream(ReadOnlySpan<uint> instructionWords)
    {
        List<string> results = new List<string>();
        if (instructionWords.Length < MinInstructionCount)
        {
            return results;
        }

        MatchRecursive(Root, instructionWords, 0, results);
        return results;
    }

    public IReadOnlyList<string> LookupByMaskedSha1(string maskedSha1Hex)
    {
        if (string.IsNullOrWhiteSpace(maskedSha1Hex))
        {
            return Array.Empty<string>();
        }

        if (_sha1Index.TryGetValue(maskedSha1Hex.Trim(), out List<string>? symbols))
        {
            return symbols;
        }

        return Array.Empty<string>();
    }

    private static void MatchRecursive(
        MatchNode node,
        ReadOnlySpan<uint> words,
        int depth,
        List<string> matches)
    {
        for (int i = 0; i < node.MatchedSymbols.Count; i++)
        {
            string sym = node.MatchedSymbols[i];
            if (!matches.Contains(sym, StringComparer.Ordinal))
            {
                matches.Add(sym);
            }
        }

        if (depth >= words.Length)
        {
            return;
        }

        uint word = words[depth];
        for (int i = 0; i < node.Edges.Count; i++)
        {
            MatchEdge edge = node.Edges[i];
            if ((word & edge.Mask) == edge.MaskedValue)
            {
                MatchRecursive(edge.Target, words, depth + 1, matches);
            }
        }
    }
}

/// <summary>
/// Phase 16 (#197 — ran-j/PS2Recomp Gem 4N): Classification of compiler/linker stub functions and thunks.
/// </summary>
public enum FunctionStubKind
{
    None = 0,
    ImportThunk = 1,
    TailJumpThunk = 2,
    CrtSecurityCookieStub = 3,
    PureReturnNoOpStub = 4,
    Int3PaddingOrTrap = 5
}

/// <summary>
/// Phase 16 (#202 — ran-j/PS2Recomp Gem 4J): Jump-table descriptor harvested from PyGhidra batch export.
/// </summary>
public sealed record HarvestedJumpTableEntry(
    uint InstructionRva,
    IReadOnlyList<uint> TargetRvas);

/// <summary>
/// Phase 16 (#202 — ran-j/PS2Recomp Gem 4J): Function metadata record harvested from PyGhidra batch export.
/// </summary>
public sealed record HarvestedBinaryFunctionEntry(
    string Name,
    uint StartRva,
    uint EndRva,
    string CallingConvention,
    int StackFrameSize,
    bool IsThunk,
    bool IsReliableSymbol,
    IReadOnlyList<HarvestedJumpTableEntry> JumpTables)
{
    public uint SizeBytes => EndRva > StartRva ? (EndRva - StartRva) : 0U;
}

/// <summary>
/// Phase 16 (#202 — ran-j/PS2Recomp Gem 4J): Structured function boundary &amp; jump-table map for a K2 module.
/// </summary>
public sealed record HarvestedBinaryFunctionMap(
    string BinaryName,
    ulong ImageBase,
    IReadOnlyList<HarvestedBinaryFunctionEntry> Functions)
{
    public HarvestedBinaryFunctionEntry? ResolveFunctionContainingRva(uint rva)
    {
        for (int i = 0; i < Functions.Count; i++)
        {
            HarvestedBinaryFunctionEntry fn = Functions[i];
            if (rva >= fn.StartRva && rva < fn.EndRva)
            {
                return fn;
            }
        }

        return null;
    }
}

/// <summary>
/// Phase 16 (#208 — ran-j/PS2Recomp Gem 4L): 64-byte cacheline-aligned single-entry 8 KiB hot-page texture lookup cache.
/// Mirrors <c>alignas(64) HotPageTextureCache</c> in <c>native/proxies/native_guard/d3d9_texture_healer.h</c>.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct HotPageTextureCache64
{
    public const ulong PageSize8Kb = 8192UL;
    public const ulong PageMask8Kb = ~0x1FFFUL;

    [FieldOffset(0)]
    public ulong CachedPageBase;

    [FieldOffset(8)]
    public ulong CachedTexturePtr;

    [FieldOffset(16)]
    public uint CachedSlot;

    [FieldOffset(20)]
    public uint ReservedPad;

    [FieldOffset(24)]
    public ulong HitCount;

    [FieldOffset(32)]
    public ulong MissCount;

    [FieldOffset(40)]
    public ulong InvalidationCount;

    [FieldOffset(48)]
    public ulong CacheLineReserved0;

    [FieldOffset(56)]
    public ulong CacheLineReserved1;

    public bool TryLookupOrPopulate(ulong address, uint fallbackSlot, ulong fallbackTexturePtr, out uint resolvedSlot, out ulong resolvedTexturePtr)
    {
        ulong pageBase = address & PageMask8Kb;
        if (pageBase != 0UL && CachedPageBase == pageBase && CachedSlot != 0xFFFFFFFFU)
        {
            HitCount++;
            resolvedSlot = CachedSlot;
            resolvedTexturePtr = CachedTexturePtr;
            return true;
        }

        MissCount++;
        CachedPageBase = pageBase;
        CachedSlot = fallbackSlot;
        CachedTexturePtr = fallbackTexturePtr;
        resolvedSlot = fallbackSlot;
        resolvedTexturePtr = fallbackTexturePtr;
        return false;
    }

    public void Invalidate()
    {
        CachedPageBase = 0UL;
        CachedTexturePtr = 0UL;
        CachedSlot = 0xFFFFFFFFU;
        InvalidationCount++;
    }
}

/// <summary>
/// Result of correlating a native binary crash symbol to repository source code.
/// </summary>
public class SymbolCorrelationResult
{
    public required string Module { get; init; }
    public required string Symbol { get; init; }
    public required string Rva { get; init; }
    public required List<string> CandidateFiles { get; init; }
    public string? PrimarySourceFile { get; init; }
    public int? LineNumberHint { get; init; }
    public string? ClickableLink { get; init; }
    public required string AstGrepPattern { get; init; }
    public required string ClangdSymbolQuery { get; init; }
    public required string Recommendation { get; init; }
}

/// <summary>
/// Correlates native module crash offsets and Ghidra decompiled symbols to
/// C++, C#, and Lua source files in the repository. Emits clickable IDE links
/// and pre-formulated ast-grep / clangd queries.
/// </summary>
public class SymbolCorrelatorService
{
    public const int MinStaticBits = 256;
    public const int MinInstructionCount = 8;

    private static readonly string[] SyntheticPlaceholderPrefixes =
    [
        "FUN_",
        "sub_",
        "LAB_",
        "DAT_",
        "unk_",
        "loc_",
        "jpt_",
        "def_",
        "off_",
        "nullsub_",
        "thunk_",
        "SEH_",
        "dword_",
        "qword_",
        "byte_",
        "word_",
        "PTR_"
    ];

    private readonly string _workspaceRoot;

    public SymbolCorrelatorService(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Phase 16 (#196 — ran-j/PS2Recomp Gem 4A): Builds a relocation-masked SHA-1 function signature
    /// and validates the <c>StaticBitCount &gt;= 256</c> and <c>InstructionCount &gt;= 8</c> correlation floor.
    /// </summary>
    public static RelocationMaskedSignature BuildRelocationMaskedSignature(
        string symbolName,
        IReadOnlyList<MaskedInstructionPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        string cleanName = (symbolName ?? string.Empty).Trim();
        int instructionCount = patterns.Count;
        int staticBits = 0;
        byte[] hashInput = new byte[instructionCount * 8];

        for (int i = 0; i < instructionCount; i++)
        {
            MaskedInstructionPattern pat = patterns[i];
            uint maskedWord = pat.InstructionWord & pat.Mask;
            staticBits += BitOperations.PopCount(pat.Mask);
            BinaryPrimitives.WriteUInt32LittleEndian(hashInput.AsSpan(i * 8, 4), maskedWord);
            BinaryPrimitives.WriteUInt32LittleEndian(hashInput.AsSpan((i * 8) + 4, 4), pat.Mask);
        }

        byte[] sha1Bytes = SHA1.HashData(hashInput);
        string sha1Hex = Convert.ToHexString(sha1Bytes).ToLowerInvariant();

        bool reliableName = IsReliableSymbolName(cleanName);
        bool hasEnoughInstructions = instructionCount >= MinInstructionCount;
        bool hasEnoughStaticBits = staticBits >= MinStaticBits;
        bool eligible = reliableName && hasEnoughInstructions && hasEnoughStaticBits;

        string rejectionReason = string.Empty;
        if (!reliableName)
        {
            rejectionReason = $"Unreliable synthetic placeholder symbol '{cleanName}'.";
        }
        else if (!hasEnoughInstructions)
        {
            rejectionReason = $"InstructionCount {instructionCount} is below minimum {MinInstructionCount}.";
        }
        else if (!hasEnoughStaticBits)
        {
            rejectionReason = $"StaticBitCount {staticBits} is below minimum {MinStaticBits} bits (high collision risk).";
        }

        return new RelocationMaskedSignature(
            SymbolName: cleanName,
            Patterns: patterns,
            InstructionCount: instructionCount,
            StaticBitCount: staticBits,
            MaskedSha1Hex: sha1Hex,
            IsEligibleForCorrelation: eligible,
            RejectionReason: rejectionReason);
    }

    /// <summary>
    /// Phase 16 (#197 — ran-j/PS2Recomp Gem 4N): Filters synthetic disassembler auto-generated placeholders
    /// (<c>FUN_*</c>, <c>sub_*</c>, <c>LAB_*</c>, <c>DAT_*</c>, <c>unk_*</c>, <c>loc_*</c>, <c>jpt_*</c>, <c>def_*</c>, <c>off_*</c>, and pure hex addresses).
    /// </summary>
    public static bool IsReliableSymbolName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        for (int i = 0; i < SyntheticPlaceholderPrefixes.Length; i++)
        {
            if (trimmed.StartsWith(SyntheticPlaceholderPrefixes[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Strip leading underscore or 0x prefix to test if the symbol is purely a hexadecimal literal
        ReadOnlySpan<char> hexCandidate = trimmed.AsSpan().TrimStart('_');
        if (hexCandidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hexCandidate = hexCandidate.Slice(2);
        }

        if (hexCandidate.Length > 0)
        {
            bool allHex = true;
            for (int i = 0; i < hexCandidate.Length; i++)
            {
                if (!Uri.IsHexDigit(hexCandidate[i]))
                {
                    allHex = false;
                    break;
                }
            }

            if (allHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Phase 16 (#197 — ran-j/PS2Recomp Gem 4N): Classifies whether a function prologue represents an import thunk,
    /// tail-jump thunk, CRT security cookie check stub, pure-return no-op stub, or INT3/UD2 trap.
    /// </summary>
    public static FunctionStubKind ClassifyFunctionStubKind(ReadOnlySpan<byte> prologueBytes, string? symbolName = null)
    {
        if (!string.IsNullOrWhiteSpace(symbolName))
        {
            string sym = symbolName.Trim();
            if (sym.Contains("__security_check_cookie", StringComparison.OrdinalIgnoreCase) ||
                sym.Contains("__report_gsfailure", StringComparison.OrdinalIgnoreCase) ||
                sym.Contains("__GSHandlerCheck", StringComparison.OrdinalIgnoreCase))
            {
                return FunctionStubKind.CrtSecurityCookieStub;
            }

            if (sym.StartsWith("__imp_", StringComparison.OrdinalIgnoreCase) ||
                sym.StartsWith("imp_", StringComparison.OrdinalIgnoreCase))
            {
                return FunctionStubKind.ImportThunk;
            }
        }

        if (prologueBytes.IsEmpty)
        {
            return FunctionStubKind.None;
        }

        // 1. INT3 padding (0xCC) or UD2 trap (0x0F 0x0B)
        if (prologueBytes[0] == 0xCC ||
            (prologueBytes.Length >= 2 && prologueBytes[0] == 0x0F && prologueBytes[1] == 0x0B))
        {
            return FunctionStubKind.Int3PaddingOrTrap;
        }

        // 2. Indirect RIP-relative jump to IAT: FF 25 <disp32> or REX.W 48 FF 25 <disp32>
        if ((prologueBytes.Length >= 6 && prologueBytes[0] == 0xFF && prologueBytes[1] == 0x25) ||
            (prologueBytes.Length >= 7 && prologueBytes[0] == 0x48 && prologueBytes[1] == 0xFF && prologueBytes[2] == 0x25))
        {
            return FunctionStubKind.ImportThunk;
        }

        // 3. Unconditional tail-jump thunk: E9 <rel32> or EB <rel8>
        if ((prologueBytes.Length >= 5 && prologueBytes[0] == 0xE9) ||
            (prologueBytes.Length >= 2 && prologueBytes[0] == 0xEB))
        {
            return FunctionStubKind.TailJumpThunk;
        }

        // 4. CRT security cookie check pattern: 48 3B 0D <disp32> (cmp rcx, [rip+__security_cookie])
        if (prologueBytes.Length >= 7 &&
            prologueBytes[0] == 0x48 &&
            prologueBytes[1] == 0x3B &&
            prologueBytes[2] == 0x0D)
        {
            return FunctionStubKind.CrtSecurityCookieStub;
        }

        // 5. Pure return no-op stub: C3 (ret) or 31 C0 C3 / 33 C0 C3 (xor eax, eax; ret)
        if (prologueBytes[0] == 0xC3 ||
            (prologueBytes.Length >= 3 && (prologueBytes[0] == 0x31 || prologueBytes[0] == 0x33) && prologueBytes[1] == 0xC0 && prologueBytes[2] == 0xC3))
        {
            return FunctionStubKind.PureReturnNoOpStub;
        }

        return FunctionStubKind.None;
    }

    /// <summary>
    /// Phase 16 (#202 — ran-j/PS2Recomp Gem 4J): Imports a structured PyGhidra function boundary &amp; jump-table map JSON.
    /// </summary>
    public static HarvestedBinaryFunctionMap ImportHarvestedFunctionMapJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string binaryName = "unknown.dll";
        if (root.TryGetProperty("binary_name", out JsonElement bnEl) && bnEl.ValueKind == JsonValueKind.String)
        {
            binaryName = bnEl.GetString() ?? "unknown.dll";
        }
        else if (root.TryGetProperty("module", out JsonElement modEl) && modEl.ValueKind == JsonValueKind.String)
        {
            binaryName = modEl.GetString() ?? "unknown.dll";
        }

        ulong imageBase = 0x180000000UL;
        if (root.TryGetProperty("image_base", out JsonElement ibEl))
        {
            imageBase = ParseHexOrUInt64(ibEl, 0x180000000UL);
        }

        List<HarvestedJumpTableEntry> topLevelJumpTables = new List<HarvestedJumpTableEntry>();
        if (root.TryGetProperty("jump_tables", out JsonElement rootJtArr) && rootJtArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement jtEl in rootJtArr.EnumerateArray())
            {
                topLevelJumpTables.Add(ParseHarvestedJumpTableEntry(jtEl, imageBase));
            }
        }

        List<HarvestedBinaryFunctionEntry> functions = new List<HarvestedBinaryFunctionEntry>();
        if (root.TryGetProperty("functions", out JsonElement fnArr) && fnArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement fnEl in fnArr.EnumerateArray())
            {
                string name = fnEl.TryGetProperty("name", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                    ? (nEl.GetString() ?? string.Empty)
                    : string.Empty;

                uint startRva = 0U;
                if (fnEl.TryGetProperty("start_rva", out JsonElement sEl))
                {
                    startRva = NormalizeToRva(ParseHexOrUInt64(sEl, 0UL), imageBase);
                }
                else if (fnEl.TryGetProperty("start", out JsonElement sLegacyEl))
                {
                    startRva = NormalizeToRva(ParseHexOrUInt64(sLegacyEl, 0UL), imageBase);
                }

                uint endRva = startRva;
                if (fnEl.TryGetProperty("end_rva", out JsonElement eEl))
                {
                    endRva = NormalizeToRva(ParseHexOrUInt64(eEl, startRva), imageBase);
                }
                else if (fnEl.TryGetProperty("end", out JsonElement eLegacyEl))
                {
                    endRva = NormalizeToRva(ParseHexOrUInt64(eLegacyEl, startRva), imageBase);
                }

                string cc = fnEl.TryGetProperty("calling_convention", out JsonElement ccEl) && ccEl.ValueKind == JsonValueKind.String
                    ? (ccEl.GetString() ?? "__fastcall")
                    : "__fastcall";
                int stackFrameSize = fnEl.TryGetProperty("stack_frame_size", out JsonElement sfEl) && sfEl.TryGetInt32(out int sfVal)
                    ? sfVal
                    : 0;
                bool isThunk = fnEl.TryGetProperty("is_thunk", out JsonElement thEl) && thEl.ValueKind == JsonValueKind.True;
                bool isReliable = IsReliableSymbolName(name);

                List<HarvestedJumpTableEntry> jumpTables = new List<HarvestedJumpTableEntry>();
                if (fnEl.TryGetProperty("jump_tables", out JsonElement jtArr) && jtArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement jtEl in jtArr.EnumerateArray())
                    {
                        jumpTables.Add(ParseHarvestedJumpTableEntry(jtEl, imageBase));
                    }
                }
                else if (topLevelJumpTables.Count > 0 && endRva > startRva)
                {
                    for (int j = 0; j < topLevelJumpTables.Count; j++)
                    {
                        HarvestedJumpTableEntry candidateJt = topLevelJumpTables[j];
                        if (candidateJt.InstructionRva >= startRva && candidateJt.InstructionRva < endRva)
                        {
                            jumpTables.Add(candidateJt);
                        }
                    }
                }

                if (endRva > startRva)
                {
                    functions.Add(new HarvestedBinaryFunctionEntry(
                        Name: name,
                        StartRva: startRva,
                        EndRva: endRva,
                        CallingConvention: cc,
                        StackFrameSize: stackFrameSize,
                        IsThunk: isThunk,
                        IsReliableSymbol: isReliable,
                        JumpTables: jumpTables));
                }
            }
        }

        return new HarvestedBinaryFunctionMap(binaryName, imageBase, functions);
    }

    private static HarvestedJumpTableEntry ParseHarvestedJumpTableEntry(JsonElement jtEl, ulong imageBase)
    {
        uint insnRva = 0U;
        if (jtEl.TryGetProperty("instruction_rva", out JsonElement irEl))
        {
            insnRva = NormalizeToRva(ParseHexOrUInt64(irEl, 0UL), imageBase);
        }
        else if (jtEl.TryGetProperty("address", out JsonElement addrEl))
        {
            insnRva = NormalizeToRva(ParseHexOrUInt64(addrEl, 0UL), imageBase);
        }

        List<uint> targets = new List<uint>();
        if (jtEl.TryGetProperty("targets", out JsonElement tArr) && tArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tEl in tArr.EnumerateArray())
            {
                targets.Add(NormalizeToRva(ParseHexOrUInt64(tEl, 0UL), imageBase));
            }
        }

        return new HarvestedJumpTableEntry(insnRva, targets);
    }

    private static uint NormalizeToRva(ulong rawVal, ulong imageBase)
    {
        if (imageBase > 0UL && rawVal >= imageBase)
        {
            return (uint)(rawVal - imageBase);
        }

        return (uint)rawVal;
    }

    private static ulong ParseHexOrUInt64(JsonElement el, ulong fallback)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out ulong numVal))
        {
            return numVal;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            string raw = (el.GetString() ?? string.Empty).Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hexVal))
                {
                    return hexVal;
                }
            }
            else if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong decVal))
            {
                return decVal;
            }
        }

        return fallback;
    }

    public SymbolCorrelationResult Correlate(string module, string? symbol = null, string? rva = null)
    {
        string normModule = Path.GetFileName(module).ToLowerInvariant();
        string normSymbol = NormalizeAndDemangleSymbol(symbol ?? string.Empty);
        string normRva = rva ?? "0x0";

        List<string> candidateFiles = [];
        string? primaryFile = null;
        int? lineHint = null;

        // 1. Module-to-source-tree routing
        if (normModule.Contains("vid_d3d9") || normModule.Contains("vid_vulkan"))
        {
            AddIfFileExists(candidateFiles, "src/extensions/vulkan-interop/NEXUS.VulkanInterop.csproj");
            AddIfFileExists(candidateFiles, "src/extensions/modding/Services/EnginePatchService.cs");
            primaryFile = Path.Combine(_workspaceRoot, "src", "extensions", "modding", "Services", "EnginePatchService.cs");
            lineHint = 77; // VidDriverOffset
        }
        else if (normModule.Contains("cgame"))
        {
            AddIfFileExists(candidateFiles, "src/apps/willowmaker/Services/ClientFolderNormalizer.cs");
            AddIfFileExists(candidateFiles, "games/shared/ui/scripts/nexus_bridge.lua");
            AddIfFileExists(candidateFiles, "games/shared/types/k2_api.lua");
            primaryFile = Path.Combine(_workspaceRoot, "games", "shared", "types", "k2_api.lua");
            lineHint = 1;
        }
        else if (normModule.Contains("k2"))
        {
            AddIfFileExists(candidateFiles, "src/extensions/modding/Services/EnginePatchService.cs");
            AddIfFileExists(candidateFiles, "src/libs/protocol-core/ASPIRE.Common.csproj");
            primaryFile = Path.Combine(_workspaceRoot, "src", "extensions", "modding", "Services", "EnginePatchService.cs");
            lineHint = 63; // ThrottleJumpOffset / CHost::Execute
        }
        else if (normModule.Contains("hon_x64"))
        {
            AddIfFileExists(candidateFiles, "src/tools/kongor-cli/Kongor.Cli.csproj");
            AddIfFileExists(candidateFiles, "src/apps/willowmaker/WILLOWMAKER.csproj");
            primaryFile = Path.Combine(_workspaceRoot, "src", "tools", "kongor-cli", "Kongor.Cli.csproj");
            lineHint = 1;
        }
        else
        {
            AddIfFileExists(candidateFiles, "src/extensions/modding/Services/EnginePatchService.cs");
            primaryFile = Path.Combine(_workspaceRoot, "src", "extensions", "modding", "Services", "EnginePatchService.cs");
        }

        string patternQuery = string.IsNullOrEmpty(normSymbol) ? normModule : normSymbol;
        string astGrepPattern = $"class $CLASS { patternQuery }";
        string clangdQuery = string.IsNullOrEmpty(normSymbol) ? normModule : normSymbol;

        string? clickableLink = null;
        if (!string.IsNullOrEmpty(primaryFile) && File.Exists(primaryFile))
        {
            string cleanPath = primaryFile.Replace('\\', '/');
            string fileName = Path.GetFileName(primaryFile);
            int line = lineHint ?? 1;
            clickableLink = $"[{fileName}#L{line}](file:///{cleanPath}#L{line})";
        }

        return new SymbolCorrelationResult
        {
            Module = normModule,
            Symbol = normSymbol,
            Rva = normRva,
            CandidateFiles = candidateFiles,
            PrimarySourceFile = primaryFile,
            LineNumberHint = lineHint,
            ClickableLink = clickableLink,
            AstGrepPattern = astGrepPattern,
            ClangdSymbolQuery = clangdQuery,
            Recommendation = string.IsNullOrEmpty(normSymbol)
                ? $"Correlated module '{normModule}' to {candidateFiles.Count} project source files. Inspect detour hooks in EnginePatchService."
                : $"Symbol '{normSymbol}' correlated with {primaryFile}. Query ast-grep or clangd for call sites."
        };
    }

    /// <summary>
    /// Phase 13 (#162): ImHex 3-prefix symbol demangler &amp; normalizer (<c>lib/trace/source/stacktrace.cpp:9-20</c>),
    /// probing <c>rawSymbol</c>, <c>"_" + rawSymbol</c>, and <c>"_Z" + rawSymbol</c> for Itanium (<c>_Z&lt;len&gt;&lt;name&gt;</c> /
    /// <c>_ZN&lt;len&gt;&lt;ns&gt;&lt;len&gt;&lt;fn&gt;E</c>) and MSVC (<c>?fn@Class@@...</c>) mangled exports.
    /// </summary>
    public static string NormalizeAndDemangleSymbol(string? rawSymbol)
    {
        if (string.IsNullOrWhiteSpace(rawSymbol))
        {
            return string.Empty;
        }

        string trimmed = rawSymbol.Trim();
        string[] candidates = new string[]
        {
            trimmed,
            "_" + trimmed,
            "_Z" + trimmed
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (TryDemangleCandidate(candidates[i], out string demangled))
            {
                return demangled;
            }
        }

        return trimmed.TrimStart('_');
    }

    private static bool TryDemangleCandidate(string candidate, out string demangled)
    {
        demangled = string.Empty;

        // 1. MSVC C++ mangled name: ?MethodName@ClassName@@...
        if (candidate.StartsWith('?') && candidate.Length > 2)
        {
            int doubleAt = candidate.IndexOf("@@", 1, StringComparison.Ordinal);
            string core = doubleAt > 1 ? candidate.Substring(1, doubleAt - 1) : candidate.Substring(1);
            string[] parts = core.Split('@', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                Array.Reverse(parts);
                demangled = string.Join("::", parts);
                return true;
            }

            if (parts.Length == 1 && parts[0].Length > 0)
            {
                demangled = parts[0];
                return true;
            }
        }

        // 2. Itanium C++ ABI nested name: _ZN<len1><seg1><len2><seg2>...E or simple _Z<len><name>
        if (candidate.StartsWith("_Z", StringComparison.Ordinal) && candidate.Length > 3)
        {
            int pos = 2;
            bool isNested = false;
            if (candidate[pos] == 'N')
            {
                isNested = true;
                pos++;
            }

            List<string> segments = new List<string>();
            while (pos < candidate.Length && char.IsDigit(candidate[pos]))
            {
                int lenStart = pos;
                while (pos < candidate.Length && char.IsDigit(candidate[pos]))
                {
                    pos++;
                }

                if (!int.TryParse(candidate.AsSpan(lenStart, pos - lenStart), out int segLen) ||
                    segLen <= 0 ||
                    pos + segLen > candidate.Length)
                {
                    break;
                }

                segments.Add(candidate.Substring(pos, segLen));
                pos += segLen;

                if (!isNested)
                {
                    break;
                }
            }

            if (segments.Count > 0)
            {
                demangled = string.Join("::", segments);
                return true;
            }
        }

        return false;
    }

    private void AddIfFileExists(List<string> list, string relativePath)
    {
        string full = Path.Combine(_workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
        {
            list.Add(full);
        }
    }
}

