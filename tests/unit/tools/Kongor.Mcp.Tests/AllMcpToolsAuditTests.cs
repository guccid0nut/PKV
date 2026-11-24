// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class AllMcpToolsAuditTests
{
    public static IEnumerable<object[]> GetAllToolNames()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        foreach (ToolDefinition tool in registry.ListTools())
        {
            yield return new object[] { tool.Name };
        }
    }

    [Fact]
    public void McpToolRegistry_ContainsAllCanonicalTools_WithValidMetadata()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        IReadOnlyList<ToolDefinition> tools = registry.ListTools();

        tools.Count.Should().BeGreaterOrEqualTo(58, "All canonical MCP tools must be registered");

        foreach (ToolDefinition tool in tools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace($"Tool {tool.Name} must have a valid name");
            tool.Description.Should().NotBeNullOrWhiteSpace($"Tool {tool.Name} must have a description");
            tool.InputSchema.Should().NotBeNull($"Tool {tool.Name} must have an input schema");
            tool.InputSchema.Type.Should().Be("object", $"Tool {tool.Name} input schema type must be 'object'");
        }
    }

    [Theory]
    [Trait("Category", "Manual")]
    [MemberData(nameof(GetAllToolNames))]
    public async Task EveryTool_WhenInvoked_ExecutesWithoutUnhandledException(string toolName)
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        McpToolRegistry registry = new(registerDefaults: true);
        ToolDefinition? tool = registry.GetTool(toolName);
        tool.Should().NotBeNull();

        var argsDict = new Dictionary<string, object?>();
        if (tool!.InputSchema?.Properties != null)
        {
            foreach (var (propName, propSchema) in tool.InputSchema.Properties)
            {
                bool isRequired = tool.InputSchema.Required?.Contains(propName) == true;
                if (isRequired || propName.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    if (propSchema.Enum != null && propSchema.Enum.Count > 0)
                    {
                        argsDict[propName] = propSchema.Enum.Contains("status") ? "status" : propSchema.Enum[0];
                    }
                    else if (propSchema.Type == "string")
                    {
                        argsDict[propName] = propName.ToLowerInvariant() switch
                        {
                            "action" => "status",
                            "command" => "echo test",
                            "script" or "code" or "expression" => "return 1;",
                            "view_name" => "main",
                            "target" => "foreground",
                            "workflow" or "flow" => "status",
                            "modeparameter" or "mode" => "practice",
                            _ => "test"
                        };
                    }
                    else if (propSchema.Type is "integer" or "number")
                    {
                        argsDict[propName] = 1;
                    }
                    else if (propSchema.Type == "boolean")
                    {
                        argsDict[propName] = false;
                    }
                }
            }
        }

        string jsonArgs = JsonSerializer.Serialize(argsDict);
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);
        using CancellationTokenSource cts = new(2000);

        ToolCallResult result = await registry.ExecuteToolAsync(tool.Name, doc.RootElement, cts.Token);
        result.Should().NotBeNull();
        result.Content.Should().NotBeEmpty();
    }
}
