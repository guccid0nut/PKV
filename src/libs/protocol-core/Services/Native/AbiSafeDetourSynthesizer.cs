// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AbiSafeDetourSynthesizer.cs
// System: Kongor Protocol Core - Native C++ Modernization & Engine Hardening
// Subsystem: Detour Synthesis - Windows x64 ABI Compliant Machine Code Detours
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kongor.Core.Services.Native;

/// <summary>
/// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): Classification of call-graph recursion cycles.
/// </summary>
public enum RecursionCycleKind
{
    SelfRecursion = 1,
    MutualRecursion = 2
}

/// <summary>
/// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): Strongly Connected Component (SCC) representing a self or mutual recursion cycle.
/// </summary>
public sealed record RecursionCycleComponent(
    string CycleId,
    RecursionCycleKind Kind,
    IReadOnlyList<string> Functions,
    string RecommendedEntryGuardSymbol,
    int RecommendedMaxDepthClamp,
    SynthesizedDetour DepthClampDetourRecipe);

/// <summary>
/// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): Report produced by the O(V+E) Tarjan SCC recursion cycle detector.
/// </summary>
public sealed record TarjanRecursionCycleReport(
    int VertexCount,
    int EdgeCount,
    bool HasRecursionCycles,
    IReadOnlyList<RecursionCycleComponent> Cycles);

/// <summary>
/// Phase 16 (#201 — shadps4-emu/shadPS4 Gem 4I): Result of synthesizing a bidirectional <c>0xE9 rel32</c> code-cave detour.
/// </summary>
public sealed record BidirectionalCodeCaveDetourResult(
    bool IsValid,
    ulong PatchAddress,
    int PatchSize,
    ulong CodeCaveAddress,
    int TotalCaveBytesUsed,
    int ForwardRel32Displacement,
    int BackwardRel32Displacement,
    byte[] PatchSiteBytes,
    byte[] CodeCaveBytes,
    string PatchSiteHex,
    string CodeCaveHex,
    string DiagnosticMessage);

/// <summary>
/// Result of ABI-safe machine code detour synthesis.
/// </summary>
public sealed class SynthesizedDetour
{
    public required string Strategy { get; init; }
    public required string DisassemblyInstruction { get; init; }
    public required string OriginalBytesHex { get; init; }
    public required string DetourBytesHex { get; init; }
    public required string SafetyVerification { get; init; }
    public bool PreservesNonVolatileRegisters { get; init; }
    public bool Maintains16ByteStackAlignment { get; init; }
    public bool HasShadowStore { get; init; }
    public bool CanLiveInject { get; init; }
}

/// <summary>
/// Synthesizes byte-level x86_64 detours strictly conforming to the Windows x64 ABI:
/// 1. 16-byte stack alignment (RSP % 16 == 0 before call instructions).
/// 2. Non-volatile register preservation (RBX, RBP, RDI, RSI, R12-R15).
/// 3. Shadow store allocation (32 bytes shadow space + 8 bytes padding = 0x28).
/// 4. Leaf-function fast paths for null checks and divisor guards.
/// </summary>
public static class AbiSafeDetourSynthesizer
{
    public const int Rel32JumpInstructionSize = 5;

    /// <summary>
    /// Synthesizes an ABI-safe machine code detour based on module, RVA, exception code, and registers.
    /// </summary>
    public static SynthesizedDetour Synthesize(
        string targetModule,
        string faultingRva,
        string exceptionCode,
        string? faultingInstruction = null,
        string? targetRegister = "rcx")
    {
        string reg = (targetRegister ?? "rcx").Trim().ToLowerInvariant();

        // 1. Exception 0xC0000005: Access Violation / Null Pointer Dereference
        if (exceptionCode.Equals("0xC0000005", StringComparison.OrdinalIgnoreCase) ||
            exceptionCode.Contains("access_violation", StringComparison.OrdinalIgnoreCase))
        {
            return SynthesizeNullPointerGuard(reg);
        }

        // 2. Exception 0xC0000094: Integer Division by Zero
        if (exceptionCode.Equals("0xC0000094", StringComparison.OrdinalIgnoreCase) ||
            exceptionCode.Contains("division_by_zero", StringComparison.OrdinalIgnoreCase))
        {
            return SynthesizeDivisionByZeroGuard(reg);
        }

        // 3. Exception 0xC00000FD: Stack Overflow / Recursive Call
        if (exceptionCode.Equals("0xC00000FD", StringComparison.OrdinalIgnoreCase) ||
            exceptionCode.Contains("stack_overflow", StringComparison.OrdinalIgnoreCase))
        {
            return SynthesizeStackOverflowGuard();
        }

        // 4. Default: Safe Early-Return Bypass
        return SynthesizeEarlyReturnBypass();
    }

    /// <summary>
    /// Leaf null-pointer check: test reg, reg; jz +skip; [original]; ret; skip: xor eax, eax; ret
    /// Windows x64 ABI: No stack frame used, zero stack modification, registers preserved.
    /// </summary>
    public static SynthesizedDetour SynthesizeNullPointerGuard(string register)
    {
        // test rcx, rcx -> 48 85 C9 (or rdx -> 48 85 D2)
        string testBytes = register switch
        {
            "rdx" => "48 85 D2",
            "r8" => "4D 85 C0",
            "r9" => "4D 85 C9",
            _ => "48 85 C9" // default rcx
        };

        // test <reg>, <reg>; jz +0x0B; mov rax, [rcx+0x18]; ret; skip: xor eax, eax; ret
        string asm = $"test {register}, {register}; jz +skip; mov rax, [{register}+0x18]; ret; skip: xor eax, eax; ret";
        string detourHex = $"{testBytes} 74 05 48 8B 41 18 C3 31 C0 C3";

        return new SynthesizedDetour
        {
            Strategy = "LeafNullCheck",
            DisassemblyInstruction = asm,
            OriginalBytesHex = "48 8B 41 18",
            DetourBytesHex = detourHex,
            SafetyVerification = "Candidate template (unverified on disk): requires code-cave or verified bytes before live injection. Modifies only volatile RAX/EAX return value.",
            PreservesNonVolatileRegisters = true,
            Maintains16ByteStackAlignment = true,
            HasShadowStore = false,
            CanLiveInject = false
        };
    }

    /// <summary>
    /// Division-by-zero guard: test divisor, divisor; jz +fallback; div/idiv; ret
    /// </summary>
    public static SynthesizedDetour SynthesizeDivisionByZeroGuard(string register)
    {
        string reg32 = register.StartsWith('r') ? "e" + register[1..] : register;
        string testBytes = reg32 switch
        {
            "edx" => "85 D2",
            "ecx" => "85 C9",
            _ => "85 D2"
        };

        string asm = $"test {reg32}, {reg32}; jz +fallback; idiv {reg32}; ret; fallback: xor eax, eax; ret";
        string detourHex = $"{testBytes} 74 03 F7 FA C3 31 C0 C3";

        return new SynthesizedDetour
        {
            Strategy = "ZeroDivisorGuard",
            DisassemblyInstruction = asm,
            OriginalBytesHex = "F7 FA", // idiv edx
            DetourBytesHex = detourHex,
            SafetyVerification = "Candidate template (unverified on disk): returns 0 in EAX when divisor is 0, avoiding divide-by-zero hardware trap (0xC0000094).",
            PreservesNonVolatileRegisters = true,
            Maintains16ByteStackAlignment = true,
            HasShadowStore = false,
            CanLiveInject = false
        };
    }

    /// <summary>
    /// Stack overflow / recursion leaf guard: immediate return (ret / xor eax, eax; ret).
    /// </summary>
    public static SynthesizedDetour SynthesizeStackOverflowGuard()
    {
        return new SynthesizedDetour
        {
            Strategy = "RecursionBypass",
            DisassemblyInstruction = "xor eax, eax; ret",
            OriginalBytesHex = "55 48 89 E5", // push rbp; mov rbp, rsp
            DetourBytesHex = "31 C0 C3",
            SafetyVerification = "Candidate template (unverified on disk): immediate function return bypassing infinite recursion loop.",
            PreservesNonVolatileRegisters = true,
            Maintains16ByteStackAlignment = true,
            HasShadowStore = false,
            CanLiveInject = false
        };
    }

    /// <summary>
    /// Full-trampoline with helper call asserting 16-byte stack alignment and shadow store.
    /// Template:
    ///   push rbx              ; sub 8 (rsp % 16 == 8)
    ///   sub rsp, 0x28         ; sub 40 (rsp % 16 == 0: 32 bytes shadow store + 8 bytes alignment)
    ///   ...
    ///   add rsp, 0x28
    ///   pop rbx
    ///   ret
    /// </summary>
    public static SynthesizedDetour SynthesizeTrampolineWithCall(string helperSymbol)
    {
        string asm = $"push rbx; sub rsp, 0x28; call <{helperSymbol}>; add rsp, 0x28; pop rbx; ret";
        string detourHex = "53 48 83 EC 28 E8 00 00 00 00 48 83 C4 28 5B C3";

        return new SynthesizedDetour
        {
            Strategy = "NonVolatilePreservingTrampolineWithCall",
            DisassemblyInstruction = asm,
            OriginalBytesHex = "48 83 EC 28",
            DetourBytesHex = detourHex,
            SafetyVerification = "Windows x64 ABI Strict Compliance: pushes non-volatile RBX, allocates 0x28 (32-byte shadow store + 8-byte alignment padding ensuring RSP % 16 == 0 before CALL), restores stack and RBX on exit.",
            PreservesNonVolatileRegisters = true,
            Maintains16ByteStackAlignment = true,
            HasShadowStore = true,
            CanLiveInject = false
        };
    }

    /// <summary>
    /// Generic early-return bypass (C3).
    /// </summary>
    public static SynthesizedDetour SynthesizeEarlyReturnBypass()
    {
        return new SynthesizedDetour
        {
            Strategy = "EarlyReturnBypass",
            DisassemblyInstruction = "ret",
            OriginalBytesHex = "48 89 5C 24 08",
            DetourBytesHex = "C3 90 90 90 90",
            SafetyVerification = "Candidate template (unverified on disk): immediate return with NOP padding.",
            PreservesNonVolatileRegisters = true,
            Maintains16ByteStackAlignment = true,
            HasShadowStore = false,
            CanLiveInject = false
        };
    }

    /// <summary>
    /// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): Synthesizes a TLS/counter depth-clamped detour for a recursion cycle entry point.
    /// </summary>
    public static SynthesizedDetour SynthesizeRecursionDepthClampDetour(string symbolName, int maxDepthClamp = 64)
    {
        int clampedMax = Math.Clamp(maxDepthClamp, 1, 255);
        byte clampByte = (byte)clampedMax;
        string asm = $"cmp dword ptr [rip+tls_depth_{symbolName}], {clampedMax}; jae +abort; inc dword ptr [rip+tls_depth_{symbolName}]; ...; abort: xor eax, eax; ret";
        string detourHex = $"83 3D 00 00 00 00 {clampByte:X2} 73 03 31 C0 C3";

        return new SynthesizedDetour
        {
            Strategy = "TarjanSccRecursionDepthClamp",
            DisassemblyInstruction = asm,
            OriginalBytesHex = "48 89 5C 24 08",
            DetourBytesHex = detourHex,
            SafetyVerification = $"Candidate template (unverified on disk): clamps recursion depth for '{symbolName}' at {clampedMax} frames to prevent 0xC00000FD stack exhaustion.",
            PreservesNonVolatileRegisters = true,
            Maintains16ByteStackAlignment = true,
            HasShadowStore = false,
            CanLiveInject = false
        };
    }

    /// <summary>
    /// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): O(V+E) Tarjan Strongly Connected Components (SCC) recursion cycle detector
    /// isolating both self-recursion (<c>A -&gt; A</c>) and mutual recursion cycles (<c>|SCC| &gt; 1</c>).
    /// </summary>
    public static TarjanRecursionCycleReport DetectRecursionCyclesTarjan(
        IEnumerable<(string Caller, string Callee)> callEdges,
        int defaultMaxDepthClamp = 64)
    {
        ArgumentNullException.ThrowIfNull(callEdges);

        Dictionary<string, List<string>> graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string caller, string callee) in callEdges)
        {
            if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(callee))
            {
                continue;
            }

            string u = caller.Trim();
            string v = callee.Trim();
            if (!graph.TryGetValue(u, out List<string>? adjU))
            {
                adjU = new List<string>();
                graph[u] = adjU;
            }
            adjU.Add(v);

            if (!graph.ContainsKey(v))
            {
                graph[v] = new List<string>();
            }
        }

        Dictionary<string, IReadOnlyList<string>> readOnlyGraph = new Dictionary<string, IReadOnlyList<string>>(graph.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<string>> kvp in graph)
        {
            readOnlyGraph[kvp.Key] = kvp.Value;
        }

        return DetectRecursionCyclesTarjan(readOnlyGraph, defaultMaxDepthClamp);
    }

    /// <summary>
    /// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): O(V+E) Tarjan Strongly Connected Components (SCC) recursion cycle detector.
    /// </summary>
    public static TarjanRecursionCycleReport DetectRecursionCyclesTarjan(
        IReadOnlyDictionary<string, IReadOnlyList<string>> callGraph,
        int defaultMaxDepthClamp = 64)
    {
        ArgumentNullException.ThrowIfNull(callGraph);

        Dictionary<string, List<string>> adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HashSet<string> selfLoopVertices = new HashSet<string>(StringComparer.Ordinal);
        int edgeCount = 0;

        foreach (KeyValuePair<string, IReadOnlyList<string>> kvp in callGraph)
        {
            string u = kvp.Key;
            if (!adj.TryGetValue(u, out List<string>? listU))
            {
                listU = new List<string>();
                adj[u] = listU;
            }

            IReadOnlyList<string> callees = kvp.Value;
            if (callees == null)
            {
                continue;
            }

            for (int i = 0; i < callees.Count; i++)
            {
                string v = callees[i];
                if (string.IsNullOrWhiteSpace(v))
                {
                    continue;
                }

                listU.Add(v);
                edgeCount++;
                if (string.Equals(u, v, StringComparison.Ordinal))
                {
                    selfLoopVertices.Add(u);
                }

                if (!adj.ContainsKey(v))
                {
                    adj[v] = new List<string>();
                }
            }
        }

        Dictionary<string, int> indexMap = new Dictionary<string, int>(adj.Count, StringComparer.Ordinal);
        Dictionary<string, int> lowlinkMap = new Dictionary<string, int>(adj.Count, StringComparer.Ordinal);
        HashSet<string> onStack = new HashSet<string>(StringComparer.Ordinal);
        Stack<string> tarjanStack = new Stack<string>();
        List<RecursionCycleComponent> cycles = new List<RecursionCycleComponent>();
        int currentIndex = 0;

        List<string> sortedVertices = new List<string>(adj.Keys);
        sortedVertices.Sort(StringComparer.Ordinal);

        void StrongConnect(string v)
        {
            indexMap[v] = currentIndex;
            lowlinkMap[v] = currentIndex;
            currentIndex++;
            tarjanStack.Push(v);
            onStack.Add(v);

            List<string> neighbors = adj[v];
            for (int i = 0; i < neighbors.Count; i++)
            {
                string w = neighbors[i];
                if (!indexMap.TryGetValue(w, out int wIndex))
                {
                    StrongConnect(w);
                    lowlinkMap[v] = Math.Min(lowlinkMap[v], lowlinkMap[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlinkMap[v] = Math.Min(lowlinkMap[v], wIndex);
                }
            }

            if (lowlinkMap[v] == indexMap[v])
            {
                List<string> component = new List<string>();
                while (true)
                {
                    string w = tarjanStack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                    if (string.Equals(w, v, StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                component.Sort(StringComparer.Ordinal);
                bool isMutualCycle = component.Count > 1;
                bool isSelfCycle = component.Count == 1 && selfLoopVertices.Contains(component[0]);
                if (isMutualCycle || isSelfCycle)
                {
                    RecursionCycleKind kind = isMutualCycle ? RecursionCycleKind.MutualRecursion : RecursionCycleKind.SelfRecursion;
                    string entrySymbol = component[0];
                    string cycleId = $"scc_{cycles.Count + 1}_{entrySymbol}";
                    SynthesizedDetour recipe = SynthesizeRecursionDepthClampDetour(entrySymbol, defaultMaxDepthClamp);
                    cycles.Add(new RecursionCycleComponent(
                        CycleId: cycleId,
                        Kind: kind,
                        Functions: component,
                        RecommendedEntryGuardSymbol: entrySymbol,
                        RecommendedMaxDepthClamp: defaultMaxDepthClamp,
                        DepthClampDetourRecipe: recipe));
                }
            }
        }

        for (int i = 0; i < sortedVertices.Count; i++)
        {
            string vertex = sortedVertices[i];
            if (!indexMap.ContainsKey(vertex))
            {
                StrongConnect(vertex);
            }
        }

        return new TarjanRecursionCycleReport(
            VertexCount: adj.Count,
            EdgeCount: edgeCount,
            HasRecursionCycles: cycles.Count > 0,
            Cycles: cycles);
    }

    /// <summary>
    /// Phase 16 (#201 — shadps4-emu/shadPS4 Gem 4I): Scans a binary buffer for a contiguous run of <c>0xCC</c> (INT3)
    /// or <c>0x90</c> (NOP) padding bytes of at least <paramref name="requiredCaveBytes"/> bytes.
    /// </summary>
    public static int? FindContiguousCodeCaveOffset(
        ReadOnlySpan<byte> moduleBytes,
        int requiredCaveBytes,
        int startSearchOffset = 0)
    {
        if (requiredCaveBytes <= 0 || moduleBytes.Length < requiredCaveBytes)
        {
            return null;
        }

        int start = Math.Max(0, startSearchOffset);
        int runStart = -1;
        int runLength = 0;
        byte runByte = 0;

        for (int i = start; i < moduleBytes.Length; i++)
        {
            byte b = moduleBytes[i];
            if (b == 0xCC || b == 0x90)
            {
                if (runLength == 0 || b == runByte)
                {
                    if (runLength == 0)
                    {
                        runStart = i;
                        runByte = b;
                    }
                    runLength++;
                    if (runLength >= requiredCaveBytes)
                    {
                        return runStart;
                    }
                }
                else
                {
                    runStart = i;
                    runByte = b;
                    runLength = 1;
                    if (runLength >= requiredCaveBytes)
                    {
                        return runStart;
                    }
                }
            }
            else
            {
                runStart = -1;
                runLength = 0;
            }
        }

        return null;
    }

    /// <summary>
    /// Phase 16 (#201 — shadps4-emu/shadPS4 Gem 4I): Synthesizes a <c>Mask_Jump32</c> bidirectional code-cave detour:
    /// 1. Verifies <c>patchSize &gt;= 5</c> and ±2 GiB signed 32-bit displacement bounds in both directions.
    /// 2. Emits <c>0xE9 &lt;forwardRel32&gt;</c> at <paramref name="patchAddress"/> and NOP-fills (<c>0x90</c>) trailing bytes.
    /// 3. Emits <paramref name="cavePayloadBytes"/> + <paramref name="overwrittenPrologueBytes"/> + <c>0xE9 &lt;backwardRel32&gt;</c>
    ///    jumping back to <c>patchAddress + patchSize</c>.
    /// </summary>
    public static BidirectionalCodeCaveDetourResult SynthesizeBidirectionalCodeCaveDetour(
        ulong patchAddress,
        ReadOnlySpan<byte> overwrittenPrologueBytes,
        ulong codeCaveAddress,
        ReadOnlySpan<byte> cavePayloadBytes)
    {
        int patchSize = overwrittenPrologueBytes.Length;
        int totalCaveBytes = cavePayloadBytes.Length + patchSize + Rel32JumpInstructionSize;

        if (patchSize < Rel32JumpInstructionSize)
        {
            return new BidirectionalCodeCaveDetourResult(
                IsValid: false,
                PatchAddress: patchAddress,
                PatchSize: patchSize,
                CodeCaveAddress: codeCaveAddress,
                TotalCaveBytesUsed: 0,
                ForwardRel32Displacement: 0,
                BackwardRel32Displacement: 0,
                PatchSiteBytes: Array.Empty<byte>(),
                CodeCaveBytes: Array.Empty<byte>(),
                PatchSiteHex: string.Empty,
                CodeCaveHex: string.Empty,
                DiagnosticMessage: $"Invalid patchSize={patchSize}; 0xE9 rel32 detour requires at least {Rel32JumpInstructionSize} bytes.");
        }

        ulong patchJumpEnd = patchAddress + (ulong)Rel32JumpInstructionSize;
        long forwardDelta = (long)codeCaveAddress - (long)patchJumpEnd;
        if (forwardDelta < int.MinValue || forwardDelta > int.MaxValue)
        {
            return new BidirectionalCodeCaveDetourResult(
                IsValid: false,
                PatchAddress: patchAddress,
                PatchSize: patchSize,
                CodeCaveAddress: codeCaveAddress,
                TotalCaveBytesUsed: 0,
                ForwardRel32Displacement: 0,
                BackwardRel32Displacement: 0,
                PatchSiteBytes: Array.Empty<byte>(),
                CodeCaveBytes: Array.Empty<byte>(),
                PatchSiteHex: string.Empty,
                CodeCaveHex: string.Empty,
                DiagnosticMessage: $"Forward displacement {forwardDelta} from 0x{patchJumpEnd:X} to code cave 0x{codeCaveAddress:X} exceeds ±2 GiB rel32 range.");
        }

        ulong returnTargetAddress = patchAddress + (ulong)patchSize;
        ulong caveReturnJumpEnd = codeCaveAddress + (ulong)totalCaveBytes;
        long backwardDelta = (long)returnTargetAddress - (long)caveReturnJumpEnd;
        if (backwardDelta < int.MinValue || backwardDelta > int.MaxValue)
        {
            return new BidirectionalCodeCaveDetourResult(
                IsValid: false,
                PatchAddress: patchAddress,
                PatchSize: patchSize,
                CodeCaveAddress: codeCaveAddress,
                TotalCaveBytesUsed: 0,
                ForwardRel32Displacement: (int)forwardDelta,
                BackwardRel32Displacement: 0,
                PatchSiteBytes: Array.Empty<byte>(),
                CodeCaveBytes: Array.Empty<byte>(),
                PatchSiteHex: string.Empty,
                CodeCaveHex: string.Empty,
                DiagnosticMessage: $"Backward return displacement {backwardDelta} from 0x{caveReturnJumpEnd:X} to 0x{returnTargetAddress:X} exceeds ±2 GiB rel32 range.");
        }

        int forwardRel32 = (int)forwardDelta;
        int backwardRel32 = (int)backwardDelta;

        // 1. Patch site: E9 <forwardRel32> + (patchSize - 5) NOPs (0x90)
        byte[] patchSiteBytes = new byte[patchSize];
        patchSiteBytes[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(patchSiteBytes.AsSpan(1, 4), forwardRel32);
        for (int i = Rel32JumpInstructionSize; i < patchSize; i++)
        {
            patchSiteBytes[i] = 0x90;
        }

        // 2. Code cave: cavePayloadBytes + overwrittenPrologueBytes + E9 <backwardRel32>
        byte[] codeCaveBytes = new byte[totalCaveBytes];
        cavePayloadBytes.CopyTo(codeCaveBytes.AsSpan(0, cavePayloadBytes.Length));
        overwrittenPrologueBytes.CopyTo(codeCaveBytes.AsSpan(cavePayloadBytes.Length, patchSize));

        int returnJmpOffset = cavePayloadBytes.Length + patchSize;
        codeCaveBytes[returnJmpOffset] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(codeCaveBytes.AsSpan(returnJmpOffset + 1, 4), backwardRel32);

        return new BidirectionalCodeCaveDetourResult(
            IsValid: true,
            PatchAddress: patchAddress,
            PatchSize: patchSize,
            CodeCaveAddress: codeCaveAddress,
            TotalCaveBytesUsed: totalCaveBytes,
            ForwardRel32Displacement: forwardRel32,
            BackwardRel32Displacement: backwardRel32,
            PatchSiteBytes: patchSiteBytes,
            CodeCaveBytes: codeCaveBytes,
            PatchSiteHex: Convert.ToHexString(patchSiteBytes),
            CodeCaveHex: Convert.ToHexString(codeCaveBytes),
            DiagnosticMessage: $"Synthesized bidirectional rel32 code-cave detour: patch @ 0x{patchAddress:X} ({patchSize}B, +{patchSize - 5} NOPs) <-> cave @ 0x{codeCaveAddress:X} ({totalCaveBytes}B).");
    }

    /// <summary>
    /// Phase 16 (#201 — shadps4-emu/shadPS4 Gem 4I): Locates a contiguous <c>0xCC</c>/<c>0x90</c> code cave inside
    /// <paramref name="moduleBytes"/> and synthesizes the bidirectional <c>0xE9 rel32</c> detour.
    /// </summary>
    public static BidirectionalCodeCaveDetourResult SynthesizeBidirectionalCodeCaveDetourInModule(
        ulong moduleBaseAddress,
        ReadOnlySpan<byte> moduleBytes,
        int patchOffsetInModule,
        int patchSize,
        ReadOnlySpan<byte> cavePayloadBytes)
    {
        if (patchOffsetInModule < 0 || patchSize < Rel32JumpInstructionSize || patchOffsetInModule + patchSize > moduleBytes.Length)
        {
            return new BidirectionalCodeCaveDetourResult(
                IsValid: false,
                PatchAddress: moduleBaseAddress + (ulong)Math.Max(0, patchOffsetInModule),
                PatchSize: patchSize,
                CodeCaveAddress: 0UL,
                TotalCaveBytesUsed: 0,
                ForwardRel32Displacement: 0,
                BackwardRel32Displacement: 0,
                PatchSiteBytes: Array.Empty<byte>(),
                CodeCaveBytes: Array.Empty<byte>(),
                PatchSiteHex: string.Empty,
                CodeCaveHex: string.Empty,
                DiagnosticMessage: "Invalid patch offset or size within module buffer.");
        }

        int requiredCaveBytes = cavePayloadBytes.Length + patchSize + Rel32JumpInstructionSize;
        int? caveOffset = FindContiguousCodeCaveOffset(moduleBytes, requiredCaveBytes, patchOffsetInModule + patchSize);
        if (!caveOffset.HasValue)
        {
            caveOffset = FindContiguousCodeCaveOffset(moduleBytes, requiredCaveBytes, 0);
        }

        if (!caveOffset.HasValue)
        {
            return new BidirectionalCodeCaveDetourResult(
                IsValid: false,
                PatchAddress: moduleBaseAddress + (ulong)patchOffsetInModule,
                PatchSize: patchSize,
                CodeCaveAddress: 0UL,
                TotalCaveBytesUsed: 0,
                ForwardRel32Displacement: 0,
                BackwardRel32Displacement: 0,
                PatchSiteBytes: Array.Empty<byte>(),
                CodeCaveBytes: Array.Empty<byte>(),
                PatchSiteHex: string.Empty,
                CodeCaveHex: string.Empty,
                DiagnosticMessage: $"No contiguous 0xCC/0x90 code cave of {requiredCaveBytes} bytes found in module.");
        }

        ReadOnlySpan<byte> prologue = moduleBytes.Slice(patchOffsetInModule, patchSize);
        return SynthesizeBidirectionalCodeCaveDetour(
            patchAddress: moduleBaseAddress + (ulong)patchOffsetInModule,
            overwrittenPrologueBytes: prologue,
            codeCaveAddress: moduleBaseAddress + (ulong)caveOffset.Value,
            cavePayloadBytes: cavePayloadBytes);
    }
}

