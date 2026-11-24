// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Registration;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class AntigravityRegistrarTests
{
    [Fact]
    public void Register_ToCustomTempDirectory_GeneratesAllSixToolSchemasAndInstructions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorMcpTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            RegistrationResult result = AntigravityRegistrar.Register(tempDir);

            result.Success.Should().BeTrue();
            result.TargetDirectory.Should().Be(tempDir);
            result.GeneratedFiles.Should().HaveCountGreaterThanOrEqualTo(15);

            string[] expectedFiles =
            [
                "kongor_server_manage.json",
                "kongor_game_client_manage.json",
                "kongor_willowmaker_manage.json",
                "kongor_compel_manage.json",
                "kongor_game_server_manage.json",
                "kongor_game_manage.json",
                "kongor_game_command.json",
                "kongor_game_telemetry.json",
                "kongor_capture_window.json",
                "kongor_ui_render_view.json",
                "kongor_ui_control.json",
                "kongor_k2_ui_inspect.json",
                "kongor_k2_lint.json",
                "kongor_engine_inspect.json",
                "instructions.md"
            ];

            string[] prunedAliases =
            [
                "kongor_game_client_manager.json",
                "kongor_willowmaker_manager.json",
                "kongor_compel_manager.json",
                "kongor_game_server_manager.json",
                "kongor_ui_controller.json",
                "kongor_asset_inspector.json",
                "kongor_ui_inspect.json",
                "kongor_ui_tree.json",
                "kongor_lua.json"
            ];

            foreach (string aliasFile in prunedAliases)
            {
                string aliasPath = Path.Combine(tempDir, aliasFile);
                File.Exists(aliasPath).Should().BeFalse($"Pruned alias schema '{aliasFile}' should not be generated in {tempDir}");
            }

            foreach (string file in expectedFiles)
            {
                string fullPath = Path.Combine(tempDir, file);
                File.Exists(fullPath).Should().BeTrue($"Expected '{file}' to be generated in {tempDir}");

                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string json = File.ReadAllText(fullPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    doc.RootElement.TryGetProperty("name", out JsonElement nameElem).Should().BeTrue();
                    doc.RootElement.TryGetProperty("description", out JsonElement descElem).Should().BeTrue();
                    doc.RootElement.TryGetProperty("parameters", out JsonElement paramsElem).Should().BeTrue();
                    paramsElem.TryGetProperty("type", out JsonElement typeElem).Should().BeTrue();
                    typeElem.GetString().Should().Be("object");
                }
                else if (file == "instructions.md")
                {
                    string markdown = File.ReadAllText(fullPath);
                    markdown.Should().Contain("ProjectKongor MCP Server — Agent Usage Guidelines");
                    markdown.Should().Contain("kongor_server_manage");
                    markdown.Should().Contain("kongor_game_client_manage");
                    markdown.Should().Contain("kongor_willowmaker_manage");
                    markdown.Should().Contain("kongor_compel_manage");
                    markdown.Should().Contain("kongor_game_server_manage");
                    markdown.Should().Contain("kongor_game_manage");
                    markdown.Should().Contain("kongor_game_command");
                    markdown.Should().Contain("kongor_game_telemetry");
                    markdown.Should().Contain("kongor_capture_window");
                    markdown.Should().Contain("kongor_ui_render_view");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}