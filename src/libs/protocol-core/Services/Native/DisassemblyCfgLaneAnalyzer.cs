// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kongor.Core.Services.Native;

/// <summary>
/// Represents a single disassembled instruction row for CFG lane and detour safety analysis.
/// </summary>
public sealed record DisassembledInstructionRow(
    ulong Address,
    int ByteLength,
    string Mnemonic,
    string Operands);

/// <summary>
/// Represents an intra-procedural branch edge with assigned sweep-line gutter lane and golden-ratio color hue.
/// </summary>
public sealed record DisassemblyCfgEdge(
    int SourceIndex,
    int TargetIndex,
    ulong SourceAddress,
    ulong TargetAddress,
    int TopIndex,
    int BottomIndex,
    int SpanLength,
    bool IsBackwardJump,
    int AssignedLane,
    float GoldenRatioHue);

/// <summary>
/// Result of auditing a proposed native detour overwrite span <c>[DetourStart, DetourStart + DetourLength)</c>
/// against a disassembled instruction stream and its intra-procedural CFG edges.
/// </summary>
public sealed record DetourOverwriteSafetyReport(
    bool IsSafe,
    ulong DetourStart,
    int DetourLength,
    bool CrossesReturnBoundary,
    int ReturnCountInSpan,
    IReadOnlyList<DisassemblyCfgEdge> InteriorJumpHazards,
    IReadOnlyList<DisassembledInstructionRow> SplitInstructionHazards,
    string DiagnosticSummary);

/// <summary>
/// Phase 13 (#156): Disassembler Control-Flow Graph (CFG) Branch-Edge Extractor, 8-Lane Sweep-Line Gutter Allocator
/// &amp; <c>m_returnPrefix</c> <c>RET</c>-Boundary Index (<c>view_disassembler.cpp:217-520</c> port).
/// </summary>
public sealed class DisassemblyCfgAnalysisResult
{
    public IReadOnlyList<DisassembledInstructionRow> Instructions { get; }
    public IReadOnlyList<DisassemblyCfgEdge> Edges { get; }
    private readonly int[] _returnPrefix;

    internal DisassemblyCfgAnalysisResult(
        IReadOnlyList<DisassembledInstructionRow> instructions,
        IReadOnlyList<DisassemblyCfgEdge> edges,
        int[] returnPrefix)
    {
        Instructions = instructions;
        Edges = edges;
        _returnPrefix = returnPrefix;
    }

    /// <summary>
    /// Gets the 1-indexed <c>RET</c> prefix-sum array of length <c>Instructions.Count + 1</c>
    /// (<c>view_disassembler.cpp:280-287</c>).
    /// </summary>
    public ReadOnlySpan<int> ReturnPrefix => _returnPrefix;

    /// <summary>
    /// Returns in O(1) time the number of <c>RET</c> instructions within the inclusive instruction index range
    /// <c>[startIndexInclusive, endIndexInclusive]</c>.
    /// </summary>
    public int CountReturnsInRange(int startIndexInclusive, int endIndexInclusive)
    {
        if (Instructions.Count == 0 || startIndexInclusive > endIndexInclusive)
        {
            return 0;
        }

        int clampedStart = Math.Clamp(startIndexInclusive, 0, Instructions.Count);
        int clampedEnd = Math.Clamp(endIndexInclusive + 1, 0, Instructions.Count);
        if (clampedEnd <= clampedStart)
        {
            return 0;
        }

        return _returnPrefix[clampedEnd] - _returnPrefix[clampedStart];
    }

    /// <summary>
    /// Returns true in O(1) time if any instruction in <c>[startIndexInclusive, endIndexInclusive]</c> is a return instruction.
    /// </summary>
    public bool CrossesReturnBoundary(int startIndexInclusive, int endIndexInclusive)
    {
        return CountReturnsInRange(startIndexInclusive, endIndexInclusive) > 0;
    }

    /// <summary>
    /// Computes the vertical Y offset of <paramref name="rowIndex"/> in O(1) time using <c>ReturnPrefix</c>
    /// (<c>view_disassembler.cpp:494-500</c>).
    /// </summary>
    public float ComputeRowTopY(int rowIndex, float rowHeight = 20.0f, float returnExtraSpacing = 8.0f)
    {
        if (rowIndex <= 0 || Instructions.Count == 0)
        {
            return 0.0f;
        }

        int clamped = Math.Clamp(rowIndex, 0, Instructions.Count);
        return (clamped * rowHeight) + (_returnPrefix[clamped] * returnExtraSpacing);
    }

    /// <summary>
    /// Finds the instruction row index corresponding to vertical position <paramref name="y"/> in O(log N) time
    /// (<c>view_disassembler.cpp:502-520</c>).
    /// </summary>
    public int FindRowIndexAtY(float y, float rowHeight = 20.0f, float returnExtraSpacing = 8.0f)
    {
        if (Instructions.Count == 0 || y <= 0.0f)
        {
            return 0;
        }

        int low = 0;
        int high = Instructions.Count - 1;
        while (low < high)
        {
            int mid = low + ((high - low + 1) / 2);
            if (ComputeRowTopY(mid, rowHeight, returnExtraSpacing) <= y)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// Audits whether placing a <paramref name="detourLength"/>-byte jump/detour at <paramref name="detourStart"/>
    /// crosses a <c>RET</c> boundary, splits an instruction mid-encoding, or gets jumped into at an interior byte offset
    /// by an intra-procedural branch edge.
    /// </summary>
    public DetourOverwriteSafetyReport AuditDetourOverwriteSafety(ulong detourStart, int detourLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(detourLength);

        ulong detourEndExclusive = detourStart + (ulong)detourLength;
        int firstOverwrittenIdx = -1;
        int lastOverwrittenIdx = -1;
        List<DisassembledInstructionRow> splitHazards = new List<DisassembledInstructionRow>();

        for (int i = 0; i < Instructions.Count; i++)
        {
            DisassembledInstructionRow insn = Instructions[i];
            ulong insnStart = insn.Address;
            ulong insnEnd = insn.Address + (ulong)Math.Max(1, insn.ByteLength);

            bool overlaps = insnStart < detourEndExclusive && insnEnd > detourStart;
            if (!overlaps)
            {
                continue;
            }

            if (firstOverwrittenIdx < 0)
            {
                firstOverwrittenIdx = i;
            }

            lastOverwrittenIdx = i;

            // An instruction is split if it starts before detourStart and ends after detourStart
            if (insnStart < detourStart && insnEnd > detourStart)
            {
                splitHazards.Add(insn);
            }
        }

        int returnCount = 0;
        if (firstOverwrittenIdx >= 0 && lastOverwrittenIdx >= firstOverwrittenIdx)
        {
            returnCount = CountReturnsInRange(firstOverwrittenIdx, lastOverwrittenIdx);
        }

        bool crossesReturn = returnCount > 0;
        List<DisassemblyCfgEdge> interiorJumpHazards = new List<DisassemblyCfgEdge>();

        for (int i = 0; i < Edges.Count; i++)
        {
            DisassemblyCfgEdge edge = Edges[i];
            // If a branch targets strictly inside (detourStart, detourEndExclusive), jumping to that target
            // after detour patching will execute the middle of the detour jmp/address operand!
            if (edge.TargetAddress > detourStart && edge.TargetAddress < detourEndExclusive)
            {
                interiorJumpHazards.Add(edge);
            }
        }

        bool isSafe = !crossesReturn && splitHazards.Count == 0 && interiorJumpHazards.Count == 0;
        string summary;
        if (isSafe)
        {
            summary = $"SAFE: Detour [0x{detourStart:X}, 0x{detourEndExclusive:X}) ({detourLength} bytes) overwrites clean instructions with 0 RET crossings and 0 interior jump targets.";
        }
        else
        {
            summary = $"UNSAFE: Detour [0x{detourStart:X}, 0x{detourEndExclusive:X}) ({detourLength} bytes) has crossesReturn={crossesReturn} (count={returnCount}), interiorJumpHazards={interiorJumpHazards.Count}, splitInstructionHazards={splitHazards.Count}.";
        }

        return new DetourOverwriteSafetyReport(
            IsSafe: isSafe,
            DetourStart: detourStart,
            DetourLength: detourLength,
            CrossesReturnBoundary: crossesReturn,
            ReturnCountInSpan: returnCount,
            InteriorJumpHazards: interiorJumpHazards,
            SplitInstructionHazards: splitHazards,
            DiagnosticSummary: summary);
    }
}

/// <summary>
/// Analyzes disassembled instruction streams to build 1-indexed <c>RET</c> prefix sums, extract intra-procedural
/// branch edges, allocate up to 8 non-overlapping gutter lanes via a sweep-line interval algorithm, and audit detour safety.
/// </summary>
public static class DisassemblyCfgLaneAnalyzer
{
    public const int MaxLanes = 8;
    private const double GoldenRatioConjugate = 0.618033988749895;

    public static DisassemblyCfgAnalysisResult Analyze(IReadOnlyList<DisassembledInstructionRow> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        int count = instructions.Count;
        int[] returnPrefix = new int[count + 1];
        Dictionary<ulong, int> addressToIndex = new Dictionary<ulong, int>(count);

        for (int i = 0; i < count; i++)
        {
            DisassembledInstructionRow row = instructions[i];
            returnPrefix[i + 1] = returnPrefix[i] + (IsReturnMnemonic(row.Mnemonic) ? 1 : 0);
            addressToIndex.TryAdd(row.Address, i);
        }

        List<DisassemblyCfgEdge> rawEdges = new List<DisassemblyCfgEdge>();
        for (int i = 0; i < count; i++)
        {
            DisassembledInstructionRow row = instructions[i];
            if (!IsBranchMnemonic(row.Mnemonic))
            {
                continue;
            }

            if (!TryExtractOperandAddress(row.Operands, out ulong targetAddr))
            {
                continue;
            }

            if (!addressToIndex.TryGetValue(targetAddr, out int targetIdx) || targetIdx == i)
            {
                continue;
            }

            int topIdx = Math.Min(i, targetIdx);
            int bottomIdx = Math.Max(i, targetIdx);
            int spanLen = bottomIdx - topIdx;
            bool isBackward = targetIdx < i;
            float hue = ComputeGoldenRatioHue(targetAddr);

            rawEdges.Add(new DisassemblyCfgEdge(
                SourceIndex: i,
                TargetIndex: targetIdx,
                SourceAddress: row.Address,
                TargetAddress: targetAddr,
                TopIndex: topIdx,
                BottomIndex: bottomIdx,
                SpanLength: spanLen,
                IsBackwardJump: isBackward,
                AssignedLane: -1,
                GoldenRatioHue: hue));
        }

        // Sweep-line greedy 8-lane allocator (view_disassembler.cpp:354-420):
        // Sort edges by (TopIndex ascending, SpanLength ascending) so shorter inner loops claim inner lanes first.
        rawEdges.Sort(static (DisassemblyCfgEdge a, DisassemblyCfgEdge b) =>
        {
            int topCmp = a.TopIndex.CompareTo(b.TopIndex);
            if (topCmp != 0)
            {
                return topCmp;
            }

            int spanCmp = a.SpanLength.CompareTo(b.SpanLength);
            return spanCmp != 0 ? spanCmp : a.SourceIndex.CompareTo(b.SourceIndex);
        });

        int[] laneLastBottom = new int[MaxLanes];
        for (int lane = 0; lane < MaxLanes; lane++)
        {
            laneLastBottom[lane] = -1;
        }

        List<DisassemblyCfgEdge> allocatedEdges = new List<DisassemblyCfgEdge>(rawEdges.Count);
        for (int i = 0; i < rawEdges.Count; i++)
        {
            DisassemblyCfgEdge edge = rawEdges[i];
            int assignedLane = -1;

            for (int lane = 0; lane < MaxLanes; lane++)
            {
                // A lane is free if the previous edge in that lane ended strictly before this edge's TopIndex
                if (laneLastBottom[lane] < edge.TopIndex)
                {
                    assignedLane = lane;
                    laneLastBottom[lane] = edge.BottomIndex;
                    break;
                }
            }

            allocatedEdges.Add(edge with { AssignedLane = assignedLane });
        }

        return new DisassemblyCfgAnalysisResult(instructions, allocatedEdges, returnPrefix);
    }

    /// <summary>
    /// Extracts a branch target address from an instruction operand string (<c>view_disassembler.cpp:217-245</c>).
    /// Scans for <c>0x...</c> hex tokens first, falling back to trailing <c>h</c> or bare hex tokens.
    /// </summary>
    public static bool TryExtractOperandAddress(string? operands, out ulong targetAddress)
    {
        targetAddress = 0;
        if (string.IsNullOrWhiteSpace(operands))
        {
            return false;
        }

        ReadOnlySpan<char> span = operands.AsSpan().Trim();

        // 1. Scan for explicit "0x" or "0X" prefix anywhere in the operand string
        for (int i = 0; i + 2 < span.Length; i++)
        {
            if (span[i] == '0' && (span[i + 1] == 'x' || span[i + 1] == 'X'))
            {
                int hexStart = i + 2;
                int hexEnd = hexStart;
                while (hexEnd < span.Length && Uri.IsHexDigit(span[hexEnd]))
                {
                    hexEnd++;
                }

                if (hexEnd > hexStart &&
                    ulong.TryParse(span.Slice(hexStart, hexEnd - hexStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out targetAddress))
                {
                    return true;
                }
            }
        }

        // 2. Fallback: bare hex address token
        if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out targetAddress))
        {
            return true;
        }

        return false;
    }

    public static bool IsReturnMnemonic(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            return false;
        }

        string m = mnemonic.Trim();
        return m.Equals("ret", StringComparison.OrdinalIgnoreCase) ||
               m.Equals("retn", StringComparison.OrdinalIgnoreCase) ||
               m.Equals("retf", StringComparison.OrdinalIgnoreCase) ||
               m.Equals("iret", StringComparison.OrdinalIgnoreCase) ||
               m.Equals("iretd", StringComparison.OrdinalIgnoreCase) ||
               m.Equals("iretq", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBranchMnemonic(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            return false;
        }

        string m = mnemonic.Trim();
        if (IsReturnMnemonic(m))
        {
            return false;
        }

        // Exclude indirect register/memory jumps by checking operand in TryExtractOperandAddress;
        // any j* (jmp, je, jne, jz, jnz, ja, jb, jg, jl, jge, jle, jrcxz, etc.), loop*, or call is a branch candidate.
        return m.StartsWith("j", StringComparison.OrdinalIgnoreCase) ||
               m.StartsWith("loop", StringComparison.OrdinalIgnoreCase) ||
               m.Equals("call", StringComparison.OrdinalIgnoreCase);
    }

    public static float ComputeGoldenRatioHue(ulong targetAddress)
    {
        double mixed = (targetAddress & 0xFFFFFFFFUL) * GoldenRatioConjugate;
        double frac = mixed - Math.Floor(mixed);
        return (float)frac;
    }

    public const int MaxSmcBackwardLookbackInstructions = 5;

    private static readonly string[] StoreMnemonics =
    [
        "mov",
        "movb",
        "movw",
        "movd",
        "movq",
        "stosb",
        "stosw",
        "stosd",
        "stosq",
        "add",
        "sub",
        "xor",
        "or",
        "and"
    ];

    /// <summary>
    /// Phase 16 (#200 — ran-j/PS2Recomp Gem 4F): Indirect <c>switch</c> jump-table extractor enforcing
    /// <c>(validCodeTargets &gt;= 2) &amp;&amp; (validCodeTargets * 2 &gt;= totalRead)</c> against executable section bounds <c>[execStartRva, execEndRva)</c>.
    /// </summary>
    public static JumpTableRecoveryReport RecoverJumpTableTargets(
        ReadOnlySpan<uint> rawTableEntries,
        ulong execStartRva,
        ulong execEndRva,
        ulong tableBaseRva = 0UL,
        ulong imageBase = 0UL)
    {
        int totalRead = rawTableEntries.Length;
        List<ulong> validTargets = new List<ulong>(totalRead);

        if (execEndRva > execStartRva)
        {
            for (int i = 0; i < totalRead; i++)
            {
                ulong rawVal = rawTableEntries[i];
                ulong candidateRva = (imageBase != 0UL && rawVal >= imageBase)
                    ? (rawVal - imageBase)
                    : rawVal;

                if (candidateRva >= execStartRva && candidateRva < execEndRva)
                {
                    validTargets.Add(candidateRva);
                }
            }
        }

        int validCount = validTargets.Count;
        bool isValid = (totalRead > 0) && (validCount >= 2) && ((validCount * 2) >= totalRead);
        double ratio = totalRead > 0 ? ((double)validCount / totalRead) : 0.0;

        string summary;
        if (isValid)
        {
            summary = $"VALID JUMP TABLE @ 0x{tableBaseRva:X}: {validCount}/{totalRead} ({ratio:P1}) targets within executable range [0x{execStartRva:X}, 0x{execEndRva:X}).";
        }
        else
        {
            summary = $"REJECTED JUMP TABLE @ 0x{tableBaseRva:X}: {validCount}/{totalRead} ({ratio:P1}) targets within [0x{execStartRva:X}, 0x{execEndRva:X}) failed (validCodeTargets >= 2 && validCodeTargets * 2 >= totalRead).";
        }

        IReadOnlyList<ulong> exposedTargets = isValid ? validTargets : Array.Empty<ulong>();
        return new JumpTableRecoveryReport(
            IsValidJumpTable: isValid,
            TableBaseRva: tableBaseRva,
            ExecStartRva: execStartRva,
            ExecEndRva: execEndRva,
            TotalEntriesRead: totalRead,
            ValidCodeTargets: validCount,
            ValidTargetRatio: ratio,
            RecoveredTargets: exposedTargets,
            DiagnosticSummary: summary);
    }

    /// <summary>
    /// Phase 16 (#200 — ran-j/PS2Recomp Gem 4F): Byte-buffer overload for 32-bit RVA or 64-bit VA indirect jump tables.
    /// </summary>
    public static JumpTableRecoveryReport RecoverJumpTableTargets(
        ReadOnlySpan<byte> rawTableBytes,
        ulong execStartRva,
        ulong execEndRva,
        bool entriesAre64Bit = false,
        ulong tableBaseRva = 0UL,
        ulong imageBase = 0UL)
    {
        int stride = entriesAre64Bit ? 8 : 4;
        int totalRead = rawTableBytes.Length / stride;
        List<ulong> validTargets = new List<ulong>(totalRead);

        if (execEndRva > execStartRva)
        {
            for (int i = 0; i < totalRead; i++)
            {
                int off = i * stride;
                ulong rawVal = entriesAre64Bit
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rawTableBytes.Slice(off, 8))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rawTableBytes.Slice(off, 4));

                ulong candidateRva = (imageBase != 0UL && rawVal >= imageBase)
                    ? (rawVal - imageBase)
                    : rawVal;

                if (candidateRva >= execStartRva && candidateRva < execEndRva)
                {
                    validTargets.Add(candidateRva);
                }
            }
        }

        int validCount = validTargets.Count;
        bool isValid = (totalRead > 0) && (validCount >= 2) && ((validCount * 2) >= totalRead);
        double ratio = totalRead > 0 ? ((double)validCount / totalRead) : 0.0;

        string summary = isValid
            ? $"VALID JUMP TABLE @ 0x{tableBaseRva:X}: {validCount}/{totalRead} ({ratio:P1}) targets within executable range [0x{execStartRva:X}, 0x{execEndRva:X})."
            : $"REJECTED JUMP TABLE @ 0x{tableBaseRva:X}: {validCount}/{totalRead} ({ratio:P1}) targets within [0x{execStartRva:X}, 0x{execEndRva:X}) failed (validCodeTargets >= 2 && validCodeTargets * 2 >= totalRead).";

        IReadOnlyList<ulong> exposedTargets = isValid ? validTargets : Array.Empty<ulong>();
        return new JumpTableRecoveryReport(
            IsValidJumpTable: isValid,
            TableBaseRva: tableBaseRva,
            ExecStartRva: execStartRva,
            ExecEndRva: execEndRva,
            TotalEntriesRead: totalRead,
            ValidCodeTargets: validCount,
            ValidTargetRatio: ratio,
            RecoveredTargets: exposedTargets,
            DiagnosticSummary: summary);
    }

    /// <summary>
    /// Phase 16 (#198 — ran-j/PS2Recomp Gem 4B): 5-instruction backward base-register tracker detecting memory stores
    /// whose resolved target address falls within an executable <c>.text</c> section <c>[execSectionStart, execSectionEnd)</c>.
    /// </summary>
    public static SelfModifyingCodeAuditReport AuditSelfModifyingCodeWrites(
        IReadOnlyList<DisassembledInstructionRow> instructions,
        ulong execSectionStart,
        ulong execSectionEnd)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        List<SelfModifyingCodeWriteHazard> hazards = new List<SelfModifyingCodeWriteHazard>();
        if (execSectionEnd <= execSectionStart || instructions.Count == 0)
        {
            return new SelfModifyingCodeAuditReport(
                HasSmcWrites: false,
                ExecSectionStart: execSectionStart,
                ExecSectionEnd: execSectionEnd,
                TotalInstructionsScanned: instructions.Count,
                Hazards: hazards);
        }

        for (int i = 0; i < instructions.Count; i++)
        {
            DisassembledInstructionRow insn = instructions[i];
            if (!IsStoreCandidateMnemonic(insn.Mnemonic))
            {
                continue;
            }

            if (!TryExtractDestinationMemoryBracket(insn.Mnemonic, insn.Operands, out string bracketExpr))
            {
                continue;
            }

            if (!TryParseMemoryBracketExpression(bracketExpr, out string baseReg, out long displacement))
            {
                continue;
            }

            ulong resolvedTarget = 0UL;
            int lookbackUsed = -1;

            if (string.Equals(baseReg, "rip", StringComparison.OrdinalIgnoreCase))
            {
                ulong nextRip = insn.Address + (ulong)Math.Max(1, insn.ByteLength);
                resolvedTarget = (ulong)((long)nextRip + displacement);
                lookbackUsed = 0;
            }
            else if (string.IsNullOrEmpty(baseReg))
            {
                resolvedTarget = (ulong)displacement;
                lookbackUsed = 0;
            }
            else
            {
                if (TryResolveBaseRegisterBackward5(instructions, i, baseReg, MaxSmcBackwardLookbackInstructions, out ulong baseValue, out int distance))
                {
                    resolvedTarget = (ulong)((long)baseValue + displacement);
                    lookbackUsed = distance;
                }
            }

            if (lookbackUsed >= 0 && resolvedTarget >= execSectionStart && resolvedTarget < execSectionEnd)
            {
                string insnText = $"{insn.Mnemonic} {insn.Operands}".Trim();
                string regLabel = string.IsNullOrEmpty(baseReg) ? "abs" : baseReg;
                string diag = $"[PS2RECOMP-SMC] Store @ 0x{insn.Address:X} ('{insnText}') writes to executable .text address 0x{resolvedTarget:X} (base={regLabel}, lookback={lookbackUsed}).";
                hazards.Add(new SelfModifyingCodeWriteHazard(
                    InstructionIndex: i,
                    StoreInstructionAddress: insn.Address,
                    ResolvedTargetAddress: resolvedTarget,
                    BaseRegister: regLabel,
                    LookbackDistance: lookbackUsed,
                    StoreInstructionText: insnText,
                    DiagnosticMessage: diag));
            }
        }

        return new SelfModifyingCodeAuditReport(
            HasSmcWrites: hazards.Count > 0,
            ExecSectionStart: execSectionStart,
            ExecSectionEnd: execSectionEnd,
            TotalInstructionsScanned: instructions.Count,
            Hazards: hazards);
    }

    private static bool IsStoreCandidateMnemonic(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            return false;
        }

        string trimmed = mnemonic.Trim();
        for (int i = 0; i < StoreMnemonics.Length; i++)
        {
            if (trimmed.Equals(StoreMnemonics[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractDestinationMemoryBracket(string? mnemonic, string? operands, out string bracketExpr)
    {
        bracketExpr = string.Empty;
        string trimmedMnem = (mnemonic ?? string.Empty).Trim();
        bool isStos =
            trimmedMnem.Equals("stosb", StringComparison.OrdinalIgnoreCase) ||
            trimmedMnem.Equals("stosw", StringComparison.OrdinalIgnoreCase) ||
            trimmedMnem.Equals("stosd", StringComparison.OrdinalIgnoreCase) ||
            trimmedMnem.Equals("stosq", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(operands))
        {
            if (isStos)
            {
                bracketExpr = "rdi";
                return true;
            }

            return false;
        }

        int commaIdx = operands.IndexOf(',');
        ReadOnlySpan<char> destSpan = commaIdx >= 0
            ? operands.AsSpan(0, commaIdx).Trim()
            : operands.AsSpan().Trim();

        int openBracket = destSpan.IndexOf('[');
        int closeBracket = destSpan.IndexOf(']');
        if (openBracket < 0 || closeBracket <= openBracket + 1)
        {
            if (isStos)
            {
                bracketExpr = "rdi";
                return true;
            }

            return false;
        }

        bracketExpr = destSpan.Slice(openBracket + 1, closeBracket - openBracket - 1).Trim().ToString();
        return bracketExpr.Length > 0;
    }

    private static bool TryParseMemoryBracketExpression(string expr, out string baseRegister, out long displacement)
    {
        baseRegister = string.Empty;
        displacement = 0L;

        string cleaned = expr.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(cleaned))
        {
            return false;
        }

        int plusIdx = cleaned.IndexOf('+', 1);
        int minusIdx = cleaned.IndexOf('-', 1);
        int splitIdx = -1;
        int sign = 1;

        if (plusIdx > 0)
        {
            splitIdx = plusIdx;
            sign = 1;
        }
        else if (minusIdx > 0)
        {
            splitIdx = minusIdx;
            sign = -1;
        }

        if (splitIdx > 0)
        {
            string left = cleaned.Substring(0, splitIdx);
            string right = cleaned.Substring(splitIdx + 1);
            if (!TryParseHexOrDecInt64(right, out long parsedDisp))
            {
                return false;
            }

            baseRegister = NormalizeRegisterFamily(left);
            displacement = sign * parsedDisp;
            return true;
        }

        if (TryParseHexOrDecInt64(cleaned, out long absAddr))
        {
            baseRegister = string.Empty;
            displacement = absAddr;
            return true;
        }

        baseRegister = NormalizeRegisterFamily(cleaned);
        displacement = 0L;
        return !string.IsNullOrEmpty(baseRegister);
    }

    private static bool TryResolveBaseRegisterBackward5(
        IReadOnlyList<DisassembledInstructionRow> instructions,
        int storeIdx,
        string targetRegFamily,
        int maxLookback,
        out ulong resolvedBaseValue,
        out int lookbackDistance)
    {
        resolvedBaseValue = 0UL;
        lookbackDistance = -1;
        long accumulatedDelta = 0L;

        for (int dist = 1; dist <= maxLookback && (storeIdx - dist) >= 0; dist++)
        {
            DisassembledInstructionRow prev = instructions[storeIdx - dist];
            string mnem = (prev.Mnemonic ?? string.Empty).Trim();
            string ops = (prev.Operands ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(ops))
            {
                continue;
            }

            int commaIdx = ops.IndexOf(',');
            if (commaIdx <= 0)
            {
                string singleOperandReg = NormalizeRegisterFamily(ops);
                if (string.Equals(singleOperandReg, targetRegFamily, StringComparison.OrdinalIgnoreCase))
                {
                    if (mnem.Equals("inc", StringComparison.OrdinalIgnoreCase))
                    {
                        accumulatedDelta += 1L;
                        continue;
                    }

                    if (mnem.Equals("dec", StringComparison.OrdinalIgnoreCase))
                    {
                        accumulatedDelta -= 1L;
                        continue;
                    }

                    if (mnem.Equals("pop", StringComparison.OrdinalIgnoreCase) ||
                        mnem.Equals("neg", StringComparison.OrdinalIgnoreCase) ||
                        mnem.Equals("not", StringComparison.OrdinalIgnoreCase) ||
                        mnem.Equals("bswap", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                continue;
            }

            string dstOperand = NormalizeRegisterFamily(ops.Substring(0, commaIdx).Trim());
            if (!string.Equals(dstOperand, targetRegFamily, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string srcOperand = ops.Substring(commaIdx + 1).Trim();

            if (mnem.Equals("cmp", StringComparison.OrdinalIgnoreCase) ||
                mnem.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (mnem.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDecInt64(srcOperand, out long addImm))
                {
                    accumulatedDelta += addImm;
                    continue;
                }

                string srcRegFamily = NormalizeRegisterFamily(srcOperand);
                int remainingWindow = maxLookback - dist;
                if (!string.IsNullOrEmpty(srcRegFamily) &&
                    remainingWindow > 0 &&
                    TryResolveBaseRegisterBackward5(instructions, storeIdx - dist, srcRegFamily, remainingWindow, out ulong addRegVal, out _))
                {
                    accumulatedDelta += (long)addRegVal;
                    continue;
                }

                return false;
            }

            if (mnem.Equals("sub", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDecInt64(srcOperand, out long subImm))
                {
                    accumulatedDelta -= subImm;
                    continue;
                }

                string srcRegFamily = NormalizeRegisterFamily(srcOperand);
                int remainingWindow = maxLookback - dist;
                if (!string.IsNullOrEmpty(srcRegFamily) &&
                    remainingWindow > 0 &&
                    TryResolveBaseRegisterBackward5(instructions, storeIdx - dist, srcRegFamily, remainingWindow, out ulong subRegVal, out _))
                {
                    accumulatedDelta -= (long)subRegVal;
                    continue;
                }

                return false;
            }

            if (mnem.Equals("xor", StringComparison.OrdinalIgnoreCase))
            {
                string srcRegFamily = NormalizeRegisterFamily(srcOperand);
                if (string.Equals(srcRegFamily, targetRegFamily, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedBaseValue = (ulong)accumulatedDelta;
                    lookbackDistance = dist;
                    return true;
                }

                return false;
            }

            if (mnem.Equals("mov", StringComparison.OrdinalIgnoreCase) ||
                mnem.Equals("movabs", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDecInt64(srcOperand, out long immBase))
                {
                    resolvedBaseValue = (ulong)(immBase + accumulatedDelta);
                    lookbackDistance = dist;
                    return true;
                }

                // Register overwritten by non-constant memory/register source -> stop tracking
                return false;
            }

            if (mnem.Equals("lea", StringComparison.OrdinalIgnoreCase))
            {
                int openB = srcOperand.IndexOf('[');
                int closeB = srcOperand.IndexOf(']');
                if (openB >= 0 && closeB > openB + 1)
                {
                    string inner = srcOperand.Substring(openB + 1, closeB - openB - 1);
                    if (TryParseMemoryBracketExpression(inner, out string leaBase, out long leaDisp))
                    {
                        if (string.Equals(leaBase, "rip", StringComparison.OrdinalIgnoreCase))
                        {
                            ulong nextRip = prev.Address + (ulong)Math.Max(1, prev.ByteLength);
                            resolvedBaseValue = (ulong)((long)nextRip + leaDisp + accumulatedDelta);
                            lookbackDistance = dist;
                            return true;
                        }

                        if (string.IsNullOrEmpty(leaBase))
                        {
                            resolvedBaseValue = (ulong)(leaDisp + accumulatedDelta);
                            lookbackDistance = dist;
                            return true;
                        }
                    }
                }

                return false;
            }

            // Any other write to targetRegFamily clobbers the register -> abort backward tracking
            return false;
        }

        return false;
    }

    private static string NormalizeRegisterFamily(string regToken)
    {
        string r = regToken.Trim().ToLowerInvariant();
        return r switch
        {
            "rax" or "eax" or "ax" or "al" => "rax",
            "rbx" or "ebx" or "bx" or "bl" => "rbx",
            "rcx" or "ecx" or "cx" or "cl" => "rcx",
            "rdx" or "edx" or "dx" or "dl" => "rdx",
            "rsi" or "esi" or "si" or "sil" => "rsi",
            "rdi" or "edi" or "di" or "dil" => "rdi",
            "rbp" or "ebp" or "bp" => "rbp",
            "rsp" or "esp" or "sp" => "rsp",
            "r8" or "r8d" or "r8w" or "r8b" => "r8",
            "r9" or "r9d" or "r9w" or "r9b" => "r9",
            "r10" or "r10d" or "r10w" or "r10b" => "r10",
            "r11" or "r11d" or "r11w" or "r11b" => "r11",
            "r12" or "r12d" or "r12w" or "r12b" => "r12",
            "r13" or "r13d" or "r13w" or "r13b" => "r13",
            "r14" or "r14d" or "r14w" or "r14b" => "r14",
            "r15" or "r15d" or "r15w" or "r15b" => "r15",
            "rip" or "eip" => "rip",
            _ => r
        };
    }

    private static bool TryParseHexOrDecInt64(string token, out long value)
    {
        value = 0L;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string s = token.Trim();
        int sign = 1;
        if (s.StartsWith('+'))
        {
            s = s.Substring(1).Trim();
        }
        else if (s.StartsWith('-'))
        {
            sign = -1;
            s = s.Substring(1).Trim();
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hexVal))
            {
                value = sign * (long)hexVal;
                return true;
            }
            return false;
        }

        if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase) && s.Length > 1)
        {
            if (ulong.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hexSuffixVal))
            {
                value = sign * (long)hexSuffixVal;
                return true;
            }
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long decVal))
        {
            value = sign * decVal;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Phase 16 (#200 — ran-j/PS2Recomp Gem 4F): Result of indirect switch jump-table recovery with <c>&gt;= 50%</c> valid ratio guard.
/// </summary>
public sealed record JumpTableRecoveryReport(
    bool IsValidJumpTable,
    ulong TableBaseRva,
    ulong ExecStartRva,
    ulong ExecEndRva,
    int TotalEntriesRead,
    int ValidCodeTargets,
    double ValidTargetRatio,
    IReadOnlyList<ulong> RecoveredTargets,
    string DiagnosticSummary);

/// <summary>
/// Phase 16 (#198 — ran-j/PS2Recomp Gem 4B): Detected self-modifying code (SMC) store targeting an executable <c>.text</c> range.
/// </summary>
public sealed record SelfModifyingCodeWriteHazard(
    int InstructionIndex,
    ulong StoreInstructionAddress,
    ulong ResolvedTargetAddress,
    string BaseRegister,
    int LookbackDistance,
    string StoreInstructionText,
    string DiagnosticMessage);

/// <summary>
/// Phase 16 (#198 — ran-j/PS2Recomp Gem 4B): Audit report for self-modifying code writes within a 5-instruction backward window.
/// </summary>
public sealed record SelfModifyingCodeAuditReport(
    bool HasSmcWrites,
    ulong ExecSectionStart,
    ulong ExecSectionEnd,
    int TotalInstructionsScanned,
    IReadOnlyList<SelfModifyingCodeWriteHazard> Hazards);

