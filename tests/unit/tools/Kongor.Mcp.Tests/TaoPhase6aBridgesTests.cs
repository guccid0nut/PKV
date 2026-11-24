// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TaoPhase6aBridgesTests
{
    [Fact]
    public void ToolHandoff_TranslatesBoundaryPaths_BetweenNativeAndContainer()
    {
        string relativePath = @"games/shared/types/k2_api.lua";
        var handoff = ToolHandoff.Create(
            server: "luals",
            tool: "lua_check_diagnostics",
            reason: "Verify LuaLS EmmyLua types",
            arguments: new { path = relativePath },
            path: relativePath);

        handoff.Server.Should().Be("luals");
        handoff.Tool.Should().Be("lua_check_diagnostics");
        handoff.Reason.Should().Be("Verify LuaLS EmmyLua types");
        handoff.TargetWorkspacePath.Should().NotBeNull();
        handoff.TargetWorkspacePath!.Should().Contain("/").And.NotContain("\\");
        handoff.TargetNativePath.Should().NotBeNull();
        handoff.TargetNativePath!.Should().Contain("\\");
    }

    [Fact]
    public void ExecutionTruthEnvelope_SerializesHandoffs_Correctly()
    {
        var handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create("memory", "create_entities", "Seed entities", new { count = 10 }),
            ToolHandoff.Create("everything", "everything_search", "Locate file", new { query = "pyromancer.entity" })
        };

        var result = ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: null,
            data: new { test = true },
            isError: false,
            handoffs: handoffs);

        result.Should().NotBeNull();
        result.Content.Should().NotBeEmpty();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("executionMode").GetString().Should().Be(ExecutionMode.OfflineDirect);
        root.GetProperty("test").GetBoolean().Should().BeTrue();
        root.TryGetProperty("handoffs", out var handoffsEl).Should().BeTrue();
        handoffsEl.GetArrayLength().Should().Be(2);

        var first = handoffsEl[0];
        first.GetProperty("server").GetString().Should().Be("memory");
        first.GetProperty("tool").GetString().Should().Be("create_entities");
    }

    [Fact]
    public async Task MemorySyncHandler_Extract_ConformsToServerMemorySchema()
    {
        var handler = new MemorySyncHandler();
        var args = JsonElement.Parse("{\"action\": \"extract\"}");

        var result = await handler.HandleAsync(args);
        result.Should().NotBeNull();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("extract");
        root.GetProperty("entityCount").GetInt32().Should().BeGreaterThan(5);
        root.GetProperty("relationCount").GetInt32().Should().BeGreaterThan(5);

        // Verify entities format
        var entities = root.GetProperty("entities");
        entities.GetArrayLength().Should().BeGreaterThan(5);

        bool foundUiManager = false;
        bool foundDoEvent = false;
        foreach (var entity in entities.EnumerateArray())
        {
            entity.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
            entity.GetProperty("entityType").GetString().Should().NotBeNullOrWhiteSpace();
            entity.GetProperty("observations").GetArrayLength().Should().BeGreaterThan(0);

            if (entity.GetProperty("name").GetString() == "UIManager") foundUiManager = true;
            if (entity.GetProperty("name").GetString() == "CWidget_DoEvent") foundDoEvent = true;
        }

        foundUiManager.Should().BeTrue("UIManager invariant entity must be in knowledge graph");
        foundDoEvent.Should().BeTrue("CWidget_DoEvent invariant entity must be in knowledge graph");

        // Verify handoffs
        root.TryGetProperty("handoffs", out var handoffsEl).Should().BeTrue();
        handoffsEl.GetArrayLength().Should().BeGreaterThan(0);
        handoffsEl[0].GetProperty("server").GetString().Should().Be("memory");
    }

    [Fact]
    public async Task MemorySyncHandler_Query_FiltersEntitiesAndRelations()
    {
        var handler = new MemorySyncHandler();
        var args = JsonElement.Parse("{\"action\": \"query\", \"query\": \"UIManager\"}");

        var result = await handler.HandleAsync(args);
        result.Should().NotBeNull();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("matchedEntityCount").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("matchedRelationCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MemorySyncHandler_Export_WritesValidJsonArtifact()
    {
        string tempExport = Path.Combine(Path.GetTempPath(), $"kongor_memory_test_{Guid.NewGuid():N}.json");
        try
        {
            var handler = new MemorySyncHandler();
            string jsonArgs = $"{{\"action\": \"export\", \"output_path\": \"{tempExport.Replace('\\', '/')}\"}}";
            var args = JsonElement.Parse(jsonArgs);

            var result = await handler.HandleAsync(args);
            result.Should().NotBeNull();

            File.Exists(tempExport).Should().BeTrue();
            string content = await File.ReadAllTextAsync(tempExport);
            content.Should().NotBeEmpty();

            using var doc = JsonDocument.Parse(content);
            doc.RootElement.TryGetProperty("entities", out var entities).Should().BeTrue();
            entities.GetArrayLength().Should().BeGreaterThan(5);
        }
        finally
        {
            if (File.Exists(tempExport))
            {
                File.Delete(tempExport);
            }
        }
    }

    [Fact]
    public async Task VfsLocateHandler_FormulatesEverythingHandoff()
    {
        var handler = new VfsLocateHandler();
        var args = JsonElement.Parse("{\"virtual_path\": \"heroes/pyromancer/ability_01/icon.tga\", \"refresh_cache\": true}");

        var result = await handler.HandleAsync(args);
        result.Should().NotBeNull();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        root.GetProperty("virtualPath").GetString().Should().Be("heroes/pyromancer/ability_01/icon.tga");
        root.GetProperty("fileName").GetString().Should().Be("icon.tga");
        root.GetProperty("mountStackLayers").GetArrayLength().Should().Be(8);

        // Verify downstream handoffs point to everything
        root.TryGetProperty("handoffs", out var handoffsEl).Should().BeTrue();
        handoffsEl.GetArrayLength().Should().BeGreaterThan(0);
        handoffsEl[0].GetProperty("server").GetString().Should().Be("everything");
        handoffsEl[0].GetProperty("tool").GetString().Should().Be("everything_search");
    }

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
    public void LuaLSTypeFiles_ExistAndAreValid()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string workspaceRoot = FindRepoRoot();

        // 1. Check games/shared/types/k2_api.lua
        string luaStubPath = Path.Combine(workspaceRoot, "games", "shared", "types", "k2_api.lua");
        File.Exists(luaStubPath).Should().BeTrue("k2_api.lua EmmyLua definitions file must exist");
        string luaContent = File.ReadAllText(luaStubPath);
        luaContent.Should().Contain("---@class UIManager");
        luaContent.Should().Contain("---@class CWidget");
        luaContent.Should().Contain("---@class CInterface");
        luaContent.Should().Contain("function CWidget:DoEvent()");
        luaContent.Should().Contain("function CWidget:SetText(text)");
        luaContent.Should().Contain("function CWidget:SetColor(rgba)");

        // 2. Check .luarc.json
        string luarcPath = Path.Combine(workspaceRoot, ".luarc.json");
        File.Exists(luarcPath).Should().BeTrue(".luarc.json must exist in workspace root");
        string luarcContent = File.ReadAllText(luarcPath);

        using var doc = JsonDocument.Parse(luarcContent);
        var root = doc.RootElement;
        root.GetProperty("runtime").GetProperty("version").GetString().Should().Be("Lua 5.1");
        root.GetProperty("workspace").GetProperty("library")[0].GetString().Should().Be("games/shared/types");
    }

    [Fact]
    public async Task McpToolRegistry_RegistersPhase6ToolsAndAliases()
    {
        var registry = new McpToolRegistry(registerDefaults: true);

        // Verify tools exist in registry
        registry.HasTool("kongor_memory_sync").Should().BeTrue();
        registry.HasTool("memory_sync").Should().BeTrue();
        registry.HasTool("kongor_vfs_locate").Should().BeTrue();
        registry.HasTool("vfs_locate").Should().BeTrue();

        // Test invocation of kongor_memory_sync status
        var statusArgs = JsonElement.Parse("{\"action\": \"status\"}");
        var statusResult = await registry.ExecuteToolAsync("kongor_memory_sync", statusArgs);
        statusResult.Should().NotBeNull();
        statusResult.IsError.Should().BeFalse();

        // Test invocation via alias memory_sync
        var aliasResult = await registry.ExecuteToolAsync("memory_sync", statusArgs);
        aliasResult.Should().NotBeNull();
        aliasResult.IsError.Should().BeFalse();

        // Test invocation of kongor_vfs_locate
        var vfsArgs = JsonElement.Parse("{\"virtual_path\": \"heroes/pyromancer/hero.entity\"}");
        var vfsResult = await registry.ExecuteToolAsync("kongor_vfs_locate", vfsArgs);
        vfsResult.Should().NotBeNull();
        vfsResult.IsError.Should().BeFalse();
    }
}
