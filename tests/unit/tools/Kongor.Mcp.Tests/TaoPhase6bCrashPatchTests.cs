// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TaoPhase6bCrashPatchTests
{
    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, ".luarc.json")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public async Task CrashTriageHandler_EmitsDownstreamToolHandoffs()
    {
        CrashTriageHandler handler = new CrashTriageHandler();
        JsonElement args = JsonElement.Parse("{\"action\": \"triage\", \"target_module\": \"k2_x64.dll\", \"address\": \"0x0005B0D0\"}");

        ToolCallResult result = await handler.HandleAsync(args);
        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("triage");
        root.TryGetProperty("sourceCorrelation", out JsonElement corrEl).Should().BeTrue();
        corrEl.GetProperty("module").GetString().Should().Be("k2_x64.dll");

        // Verify downstream handoffs exist
        root.TryGetProperty("handoffs", out JsonElement handoffsEl).Should().BeTrue();
        handoffsEl.GetArrayLength().Should().BeGreaterOrEqualTo(4);

        List<string?> servers = handoffsEl.EnumerateArray().Select(h => h.GetProperty("server").GetString()).ToList();
        servers.Should().Contain("ghidra");
        servers.Should().Contain("windbg");
        servers.Should().Contain("clangd");
        servers.Should().Contain("kongor");
    }

    [Fact]
    public void AbiSafeDetourSynthesizer_CompliesWithWindowsX64Abi_AndHonestCandidateCanLiveInject()
    {
        // 1. Null-pointer check
        SynthesizedDetour nullDetour = AbiSafeDetourSynthesizer.Synthesize("k2_x64.dll", "0x1000", "0xC0000005");
        nullDetour.Strategy.Should().Be("LeafNullCheck");
        nullDetour.PreservesNonVolatileRegisters.Should().BeTrue();
        nullDetour.Maintains16ByteStackAlignment.Should().BeTrue();
        nullDetour.HasShadowStore.Should().BeFalse();
        nullDetour.CanLiveInject.Should().BeFalse();
        nullDetour.DetourBytesHex.Should().Contain("31 C0 C3"); // xor eax, eax; ret

        // 2. Division by zero
        SynthesizedDetour divDetour = AbiSafeDetourSynthesizer.Synthesize("cgame_x64.dll", "0x2000", "0xC0000094");
        divDetour.Strategy.Should().Be("ZeroDivisorGuard");
        divDetour.PreservesNonVolatileRegisters.Should().BeTrue();
        divDetour.Maintains16ByteStackAlignment.Should().BeTrue();
        divDetour.CanLiveInject.Should().BeFalse();

        // 3. Stack overflow recursion bypass
        SynthesizedDetour stackDetour = AbiSafeDetourSynthesizer.Synthesize("k2_x64.dll", "0x3000", "0xC00000FD");
        stackDetour.Strategy.Should().Be("RecursionBypass");
        stackDetour.DetourBytesHex.Should().Be("31 C0 C3");
        stackDetour.CanLiveInject.Should().BeFalse();

        // 4. Trampoline with call (shadow store & alignment)
        SynthesizedDetour callDetour = AbiSafeDetourSynthesizer.SynthesizeTrampolineWithCall("CrashLogger_Helper");
        callDetour.Strategy.Should().Be("NonVolatilePreservingTrampolineWithCall");
        callDetour.HasShadowStore.Should().BeTrue();
        callDetour.Maintains16ByteStackAlignment.Should().BeTrue();
        callDetour.CanLiveInject.Should().BeFalse();
        callDetour.DetourBytesHex.Should().Contain("48 83 EC 28"); // sub rsp, 0x28 (32 shadow + 8 padding)
    }

    [Fact]
    public void SymbolCorrelatorService_MapsModulesToSourceCode()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        SymbolCorrelatorService correlator = new SymbolCorrelatorService(FindRepoRoot());

        // 1. vid_d3d9_x64.dll
        SymbolCorrelationResult vidCorr = correlator.Correlate("vid_d3d9_x64.dll", null, "0x0005B0D0");
        vidCorr.Module.Should().Be("vid_d3d9_x64.dll");
        vidCorr.PrimarySourceFile.Should().NotBeNullOrEmpty();
        vidCorr.ClickableLink.Should().NotBeNullOrEmpty();
        vidCorr.ClickableLink.Should().Contain("EnginePatchService.cs");

        // 2. cgame_x64.dll
        SymbolCorrelationResult cgameCorr = correlator.Correlate("cgame_x64.dll", "CUICommander::Frame");
        cgameCorr.Module.Should().Be("cgame_x64.dll");
        cgameCorr.PrimarySourceFile.Should().NotBeNullOrEmpty();
        cgameCorr.ClickableLink.Should().Contain("k2_api.lua");
    }

    [Fact]
    public async Task CrashPatchHandler_SynthesizeAction_ReturnsRecipe()
    {
        CrashPatchHandler handler = new CrashPatchHandler();
        JsonElement args = JsonElement.Parse("{\"action\": \"synthesize\", \"target_module\": \"k2_x64.dll\", \"faulting_rva\": \"0x8B72A8\", \"exception_code\": \"0xC0000005\"}");

        ToolCallResult result = await handler.HandleAsync(args);
        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("synthesize");
        root.GetProperty("originalBytesHex").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("detourBytesHex").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("maintains16ByteStackAlignment").GetBoolean().Should().BeTrue();
        root.GetProperty("canLiveInject").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CrashPatchHandler_DryRun_Apply_Revert_Cycle_AndOversizedDetourRejection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_patch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempBinary = Path.Combine(tempDir, "mock_k2_x64.dll");

        try
        {
            // Create 1024-byte dummy binary with known 4-byte instruction at offset 0x100
            byte[] fileBytes = new byte[1024];
            byte[] originalBytes = [0x48, 0x8B, 0x41, 0x18]; // 48 8B 41 18 (4 bytes)
            byte[] inPlacePatchBytes = [0x31, 0xC0, 0xC3, 0x90]; // xor eax, eax; ret; nop (4 bytes <= 4 bytes)
            Array.Copy(originalBytes, 0, fileBytes, 0x100, originalBytes.Length);
            await File.WriteAllBytesAsync(tempBinary, fileBytes);

            CrashPatchHandler handler = new CrashPatchHandler(workspaceRoot: tempDir);

            // 0. Oversized 13-byte detour over 4-byte instruction MUST be refused in dry_run and apply!
            string oversizedJson = $"{{\"action\": \"dry_run\", \"binary_path\": \"{tempBinary.Replace('\\', '/')}\", \"faulting_rva\": \"0x100\", \"original_bytes_hex\": \"48 8B 41 18\", \"detour_bytes_hex\": \"48 85 C9 74 05 48 8B 41 18 C3 31 C0 C3\"}}";
            ToolCallResult oversizedDryRun = await handler.HandleAsync(JsonElement.Parse(oversizedJson));
            oversizedDryRun.IsError.Should().BeTrue();
            using (JsonDocument doc = JsonDocument.Parse(((TextContentItem)oversizedDryRun.Content[0]).Text))
            {
                doc.RootElement.GetProperty("detourFitsInPlace").GetBoolean().Should().BeFalse();
                doc.RootElement.GetProperty("safeToApply").GetBoolean().Should().BeFalse();
            }

            string oversizedApplyJson = $"{{\"action\": \"apply\", \"binary_path\": \"{tempBinary.Replace('\\', '/')}\", \"faulting_rva\": \"0x100\", \"original_bytes_hex\": \"48 8B 41 18\", \"detour_bytes_hex\": \"48 85 C9 74 05 48 8B 41 18 C3 31 C0 C3\"}}";
            ToolCallResult oversizedApply = await handler.HandleAsync(JsonElement.Parse(oversizedApplyJson));
            oversizedApply.IsError.Should().BeTrue();

            // 1. Dry run with fitting 4-byte in-place patch - verify original matches
            string dryRunJson = $"{{\"action\": \"dry_run\", \"binary_path\": \"{tempBinary.Replace('\\', '/')}\", \"faulting_rva\": \"0x100\", \"original_bytes_hex\": \"48 8B 41 18\", \"detour_bytes_hex\": \"31 C0 C3 90\"}}";
            ToolCallResult dryRunResult = await handler.HandleAsync(JsonElement.Parse(dryRunJson));
            dryRunResult.IsError.Should().BeFalse();

            using (JsonDocument doc = JsonDocument.Parse(((TextContentItem)dryRunResult.Content[0]).Text))
            {
                doc.RootElement.GetProperty("bytesMatch").GetBoolean().Should().BeTrue();
                doc.RootElement.GetProperty("detourFitsInPlace").GetBoolean().Should().BeTrue();
                doc.RootElement.GetProperty("safeToApply").GetBoolean().Should().BeTrue();
                doc.RootElement.GetProperty("alreadyPatched").GetBoolean().Should().BeFalse();
            }

            // 2. Dry run - safety mismatch detection
            string badDryRunJson = $"{{\"action\": \"dry_run\", \"binary_path\": \"{tempBinary.Replace('\\', '/')}\", \"faulting_rva\": \"0x100\", \"original_bytes_hex\": \"90 90 90 90\", \"detour_bytes_hex\": \"31 C0 C3 90\"}}";
            ToolCallResult badResult = await handler.HandleAsync(JsonElement.Parse(badDryRunJson));
            badResult.IsError.Should().BeTrue();

            using (JsonDocument doc = JsonDocument.Parse(((TextContentItem)badResult.Content[0]).Text))
            {
                doc.RootElement.GetProperty("bytesMatch").GetBoolean().Should().BeFalse();
                doc.RootElement.GetProperty("safeToApply").GetBoolean().Should().BeFalse();
            }

            // 3. Apply fitting 4-byte patch
            string applyJson = $"{{\"action\": \"apply\", \"binary_path\": \"{tempBinary.Replace('\\', '/')}\", \"faulting_rva\": \"0x100\", \"original_bytes_hex\": \"48 8B 41 18\", \"detour_bytes_hex\": \"31 C0 C3 90\"}}";
            ToolCallResult applyResult = await handler.HandleAsync(JsonElement.Parse(applyJson));
            applyResult.IsError.Should().BeFalse();

            // Assert backup was created
            File.Exists(tempBinary + ".bak").Should().BeTrue();

            // Assert bytes were changed
            byte[] patchedFile = await File.ReadAllBytesAsync(tempBinary);
            patchedFile.AsSpan(0x100, inPlacePatchBytes.Length).SequenceEqual(inPlacePatchBytes).Should().BeTrue();

            // 4. Second apply is idempotent
            ToolCallResult secondApplyResult = await handler.HandleAsync(JsonElement.Parse(applyJson));
            secondApplyResult.IsError.Should().BeFalse();
            using (JsonDocument doc = JsonDocument.Parse(((TextContentItem)secondApplyResult.Content[0]).Text))
            {
                doc.RootElement.GetProperty("status").GetString().Should().Be("already_patched");
            }

            // 5. Revert patch
            string revertJson = $"{{\"action\": \"revert\", \"binary_path\": \"{tempBinary.Replace('\\', '/')}\"}}";
            ToolCallResult revertResult = await handler.HandleAsync(JsonElement.Parse(revertJson));
            revertResult.IsError.Should().BeFalse();

            // Assert original bytes are restored
            byte[] revertedFile = await File.ReadAllBytesAsync(tempBinary);
            revertedFile.AsSpan(0x100, originalBytes.Length).SequenceEqual(originalBytes).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task McpToolRegistry_RegistersCrashPatchToolAndAlias()
    {
        McpToolRegistry registry = new McpToolRegistry(registerDefaults: true);

        registry.HasTool("kongor_crash_patch").Should().BeTrue();
        registry.HasTool("crash_patch").Should().BeTrue();

        JsonElement synthArgs = JsonElement.Parse("{\"action\": \"synthesize\", \"target_module\": \"k2_x64.dll\"}");
        ToolCallResult synthResult = await registry.ExecuteToolAsync("kongor_crash_patch", synthArgs);
        synthResult.Should().NotBeNull();
        synthResult.IsError.Should().BeFalse();

        ToolCallResult aliasResult = await registry.ExecuteToolAsync("crash_patch", synthArgs);
        aliasResult.Should().NotBeNull();
        aliasResult.IsError.Should().BeFalse();
    }
}
