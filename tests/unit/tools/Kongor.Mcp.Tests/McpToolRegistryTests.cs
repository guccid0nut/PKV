// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpToolRegistryTests
{
    [Fact]
    public void DefaultRegistry_WhenInvoked_ContainsAllSixCanonicalTools()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        IReadOnlyList<ToolDefinition> tools = registry.ListTools();

        string[] expectedNames =
        [
            "kongor_capture_window",
            "kongor_game_command",
            "kongor_game_manage",
            "kongor_game_telemetry",
            "kongor_server_manage",
            "kongor_ui_render_view"
        ];

        tools.Select(t => t.Name).Should().Contain(expectedNames);
        tools.Select(t => t.Name).Should().Contain("kongor_ui_control");
        tools.Select(t => t.Name).Should().NotContain("kongor_ui_controller");
        registry.HasTool("kongor_ui_controller").Should().BeTrue();
    }

    [Theory]
    [InlineData("kongor_server_manage", "action")]
    [InlineData("kongor_game_manage", "action")]
    [InlineData("kongor_game_command", "command")]
    [InlineData("kongor_ui_render_view", "view_name")]
    public void DefaultTools_WhenInvoked_HaveRequiredPropertiesConfigured(string toolName, string requiredProp)
    {
        McpToolRegistry registry = new(registerDefaults: true);
        ToolDefinition? tool = registry.GetTool(toolName);

        tool.Should().NotBeNull();
        tool!.Description.Should().NotBeNullOrWhiteSpace();
        tool.InputSchema.Should().NotBeNull();
        tool.InputSchema.Type.Should().Be("object");
        tool.InputSchema.Required.Should().Contain(requiredProp);
        tool.InputSchema.Properties.Should().ContainKey(requiredProp);
    }

    [Fact]
    public async Task ExecuteToolAsync_CustomTool_ExecutesSuccessfully()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        ToolDefinition customDef = new()
        {
            Name = "custom_test_tool",
            Description = "Custom tool for unit tests",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["input"] = new() { Type = "string", Description = "Input message" }
                },
                Required = ["input"]
            }
        };

        registry.RegisterTool(customDef, (args, ct) =>
        {
            string msg = args.GetProperty("input").GetString()!;
            return Task.FromResult(ToolCallResult.Text($"Echo: {msg}"));
        });

        registry.HasTool("custom_test_tool").Should().BeTrue();

        JsonElement args = JsonElement.Parse("{\"input\": \"hello world\"}");
        ToolCallResult result = await registry.ExecuteToolAsync("custom_test_tool", args);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();
        result.Content[0].Should().BeOfType<TextContentItem>()
            .Which.Text.Should().Be("Echo: hello world");
    }

    [Fact]
    public async Task ExecuteToolAsync_UnknownTool_ReturnsErrorWithAvailableTools()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        JsonElement args = JsonElement.Parse("{}");

        ToolCallResult result = await registry.ExecuteToolAsync("non_existent_tool", args);

        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle();
        result.Content[0].Should().BeOfType<TextContentItem>()
            .Which.Text.Should().Contain("Unknown tool 'non_existent_tool'")
            .And.Contain("kongor_server_manage");
    }

    [Fact]
    public async Task ExecuteToolAsync_HandlerThrowsException_WrapsInErrorResult()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        ToolDefinition faultDef = new()
        {
            Name = "faulty_tool",
            Description = "Tool that throws"
        };

        registry.RegisterTool(faultDef, (_, _) => throw new InvalidOperationException("Simulated hardware fault"));

        JsonElement args = JsonElement.Parse("{}");
        ToolCallResult result = await registry.ExecuteToolAsync("faulty_tool", args);

        result.IsError.Should().BeTrue();
        result.Content[0].Should().BeOfType<TextContentItem>()
            .Which.Text.Should().Contain("Simulated hardware fault");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task DefaultHandlers_AllSixTools_ExecuteBaselineWithoutCrashing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Moq.Mock<Kongor.Cli.Mcp.Services.ILifecycleService> mockLifecycle = new();
        mockLifecycle.Setup(l => l.GetStatusAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Kongor.Cli.Mcp.Services.ClusterLifecycleResult
            {
                Status = "success",
                Action = "status",
                Target = "cluster",
                ClusterState = "Running"
            });

        Moq.Mock<Kongor.Cli.Mcp.Services.IGameLauncherService> mockLauncher = new();
        mockLauncher.Setup(l => l.GetStatus())
            .Returns(new Kongor.Cli.Mcp.Services.GameStatusResult
            {
                Status = "success",
                Action = "status",
                Running = false
            });

        Moq.Mock<Kongor.Cli.Mcp.Services.IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.ExecuteCommandAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync("Set vid_maxFPS 1000");
        mockIpc.Setup(i => i.GetEngineStateAsync(Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Kongor.Cli.Mcp.Services.EngineStateDto { CurrentFps = 500, MemoryWorkingSetBytes = 100000 });

        McpToolRegistry registry = new(
            registerDefaults: true,
            lifecycleService: mockLifecycle.Object,
            gameLauncherService: mockLauncher.Object,
            gameIpcBridge: mockIpc.Object);

        // 1. kongor_server_manage
        var res1 = await registry.ExecuteToolAsync("kongor_server_manage", JsonElement.Parse("{\"action\":\"status\"}"));
        res1.IsError.Should().BeFalse();
        ((TextContentItem)res1.Content[0]).Text.Should().Contain("Running");

        // 2. kongor_game_manage
        var res2 = await registry.ExecuteToolAsync("kongor_game_manage", JsonElement.Parse("{\"action\":\"status\"}"));
        res2.IsError.Should().BeFalse();
        ((TextContentItem)res2.Content[0]).Text.Should().Contain("running");

        // 3. kongor_game_command
        var res3 = await registry.ExecuteToolAsync("kongor_game_command", JsonElement.Parse("{\"command\":\"Set vid_maxFPS 1000\"}"));
        res3.IsError.Should().BeFalse();

        // 4. kongor_game_telemetry
        var res4 = await registry.ExecuteToolAsync("kongor_game_telemetry", JsonElement.Parse("{}"));
        res4.IsError.Should().BeFalse();

        // 5. kongor_capture_window
        var res5 = await registry.ExecuteToolAsync("kongor_capture_window", JsonElement.Parse("{\"target\":\"foreground\",\"save_artifact\":false}"));
        res5.Content.Should().NotBeEmpty();
        if (!res5.IsError)
        {
            res5.Content.Should().Contain(c => c is ImageContentItem);
        }

        // 6. kongor_ui_render_view
        var res6 = await registry.ExecuteToolAsync("kongor_ui_render_view", JsonElement.Parse("{\"view_name\":\"ModManagerView\",\"save_artifact\":false}"));
        res6.IsError.Should().BeFalse();
        res6.Content.Should().Contain(c => c is ImageContentItem);
    }

    [Theory]
    [InlineData("kongor_game_client_manager", "kongor_game_client_manage")]
    [InlineData("kongor_willowmaker_manager", "kongor_willowmaker_manage")]
    [InlineData("kongor_compel_manager", "kongor_compel_manage")]
    [InlineData("kongor_game_server_manager", "kongor_game_server_manage")]
    [InlineData("kongor_ui_controller", "kongor_ui_control")]
    [InlineData("kongor_ui_inspect", "kongor_k2_ui_inspect")]
    [InlineData("kongor_ui_tree", "kongor_k2_ui_inspect")]
    [InlineData("kongor_lua", "kongor_game_lua")]
    [InlineData("kongor_asset_inspector", "kongor_asset_inspect")]
    [InlineData("kongor_practice_launch", "kongor_match_launch")]
    [InlineData("kongor_cache_purge", "kongor_engine_cache_purge")]
    [InlineData("kongor_cluster_diagnostics", "kongor_diagnostics")]
    public void AliasRedirects_WhenInvoked_ResolveToCanonicalToolDefinitions(string aliasName, string canonicalName)
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool(aliasName).Should().BeTrue($"Alias '{aliasName}' must be recognized by HasTool");

        ToolDefinition? def = registry.GetTool(aliasName);
        def.Should().NotBeNull($"Alias '{aliasName}' should return definition of canonical tool");
        def!.Name.Should().Be(canonicalName);

        // ListTools must NOT contain the pruned alias
        registry.ListTools().Select(t => t.Name).Should().NotContain(aliasName);
    }

    [Fact]
    public async Task K2Lint_WithValidArguments_ValidatesCleanXmlAndFlagsIssues()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        string tempDir = Path.Combine(Path.GetTempPath(), "k2lint_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string cleanXml = "<package><panel name=\"test_panel\" width=\"100%\" height=\"100%\" /></package>";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "clean.package"), cleanXml);

            string badXml = "<package><button onclick=\"Windows.main:DoEvent('onclick')\" /></package>";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "bad.package"), badXml);

            var res = await registry.ExecuteToolAsync("kongor_k2_lint", JsonElement.Parse($"{{\"path\":\"{tempDir.Replace('\\', '/')}\"}}"));
            res.IsError.Should().BeFalse();

            string json = ((TextContentItem)res.Content[0]).Text;
            using JsonDocument doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("filesScanned").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("errorCount").GetInt32().Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EngineInspect_QueriesBinary_ReturnsSymbolsOrGracefulMessage()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_engine_inspect", JsonElement.Parse("{\"target\":\"cgame_x64.dll\",\"query\":\"StartWatchingMatch\"}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("targetBinary").GetString().Should().Be("cgame_x64.dll");
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task VfsResolve_FindAction_ReturnsWinningAndShadowedFiles()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_vfs_resolve", JsonElement.Parse("{\"action\":\"find\",\"pattern\":\"*.interface\",\"max_results\":10}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("action").GetString().Should().Be("find");
    }

    [Fact]
    public async Task NativeManage_StatusAction_ReturnsDiscoveredBinaries()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_native_manage", JsonElement.Parse("{\"action\":\"status\"}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("action").GetString().Should().Be("status");
        doc.RootElement.GetProperty("targetCount").GetInt32().Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task NativeManage_VerifyAction_ConfirmsExportParity()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_native_manage", JsonElement.Parse("{\"action\":\"verify\"}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("candidateSymbolCount").GetInt32().Should().Be(132);
        doc.RootElement.GetProperty("missingSymbolsCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public void NativeManage_AliasRegistration_ResolvesCorrectly()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_native_manage").Should().BeTrue();
        registry.HasTool("kongor_native").Should().BeTrue();
        registry.HasTool("native_manage").Should().BeTrue();
    }

    [Fact]
    public void VulkanManage_AliasRegistration_ResolvesCorrectly()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_vulkan").Should().BeTrue();
        registry.HasTool("kongor_vulkan_manage").Should().BeTrue();
        registry.HasTool("vulkan_manage").Should().BeTrue();
        registry.HasTool("kongor_dxvk").Should().BeTrue();
    }

    [Fact]
    public async Task VulkanManage_StatusAction_ReturnsDriverReport()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_vulkan", JsonElement.Parse("{\"action\":\"status\"}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("action").GetString().Should().Be("status");
        doc.RootElement.GetProperty("hasSystemVulkanLoader").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task VulkanManage_UpgradeAction_ReturnsUpgradeResult()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_vulkan", JsonElement.Parse("{\"action\":\"upgrade\",\"target\":\"server\",\"preset\":\"competitive\"}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("action").GetString().Should().Be("upgrade");
        doc.RootElement.GetProperty("target").GetString().Should().Be("server");
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task VulkanManage_PurgeCacheAction_ReturnsPurgeResult()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_vulkan", JsonElement.Parse("{\"action\":\"purge_cache\"}"));

        res.IsError.Should().BeFalse();
        string json = ((TextContentItem)res.Content[0]).Text;
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        doc.RootElement.GetProperty("action").GetString().Should().Be("purge_cache");
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task VulkanManage_UnknownAction_ReturnsError()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var res = await registry.ExecuteToolAsync("kongor_vulkan", JsonElement.Parse("{\"action\":\"invalid_action_xyz\"}"));

        res.IsError.Should().BeTrue();
        string text = ((TextContentItem)res.Content[0]).Text;
        text.Should().Contain("Unknown action 'invalid_action_xyz'");
    }
}
