// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class Phase9BToolsTests : IDisposable
{
    private readonly string _testTempDir;

    public Phase9BToolsTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "kongor_phase9b_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    private static string GetResultText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return string.Empty;
    }

    [Fact]
    public void McpToolRegistry_RegistersPhase9BTools()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.GetTool("kongor_validate_previews").Should().NotBeNull();
        registry.GetTool("validate_previews").Should().NotBeNull();
        registry.GetTool("kongor_inspect_model_mdf").Should().NotBeNull();
        registry.GetTool("inspect_model_mdf").Should().NotBeNull();
        registry.GetTool("kongor_inspect_hero_pool").Should().NotBeNull();
        registry.GetTool("inspect_hero_pool").Should().NotBeNull();
    }

    [Fact]
    public async Task ValidatePreviews_WhenMdfMissing_ReportsTeapotRisk()
    {
        // 1. Create client override dir with heroes_preview.xml
        string clientDir = Path.Combine(_testTempDir, "games", "client", "override");
        Directory.CreateDirectory(clientDir);

        string xmlContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <heroes>
            <hero name="Hero_MissingMdf" default_avatar="default">
                <avatar name="default" preview_mdf="heroes/missing_mdf/preview.mdf" />
            </hero>
        </heroes>
        """;
        await File.WriteAllTextAsync(Path.Combine(clientDir, "heroes_preview.xml"), xmlContent);

        var service = new PreviewValidationService(repoRoot: _testTempDir);
        var handler = new ValidatePreviewsHandler(service);

        using var doc = JsonDocument.Parse("{\"hero\":\"Hero_MissingMdf\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = GetResultText(result);
        text.Should().Contain("teapot_risk");
        text.Should().Contain("heroes/missing_mdf/preview.mdf");
    }

    [Fact]
    public async Task InspectModelMdf_ParsesManifestAndIdentifiesMissingModel()
    {
        string heroesDir = Path.Combine(_testTempDir, "games", "client", "override", "heroes", "pyro");
        Directory.CreateDirectory(heroesDir);

        string mdfContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <model name="pyro_preview" file="high.model" type="K2">
            <anim name="idle" clip="default_1.clip" fps="30" loop="true" />
        </model>
        """;
        string mdfPath = Path.Combine(heroesDir, "preview.mdf");
        await File.WriteAllTextAsync(mdfPath, mdfContent);

        var service = new PreviewValidationService(repoRoot: _testTempDir);
        var handler = new InspectModelMdfHandler(service);

        using var doc = JsonDocument.Parse($"{{\"path\":\"heroes/pyro/preview.mdf\"}}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = GetResultText(result);
        text.Should().Contain("high.model");
        text.Should().Contain("default_1.clip");
        text.Should().Contain("Referenced model file 'high.model' does not exist on disk");
    }

    [Fact]
    public async Task InspectHeroPool_ReconcilesServerClientAndLua()
    {
        // 1. Server hero entity
        string serverHeroDir = Path.Combine(_testTempDir, "games", "server", "override", "heroes", "Hero_Alpha");
        Directory.CreateDirectory(serverHeroDir);
        await File.WriteAllTextAsync(Path.Combine(serverHeroDir, "hero.entity"), "<entity name=\"Hero_Alpha\" />");

        // 2. Client preview XML has Hero_Alpha and Hero_Beta
        string clientDir = Path.Combine(_testTempDir, "games", "client", "override");
        Directory.CreateDirectory(clientDir);
        string xmlContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <heroes>
            <hero name="Hero_Alpha" default_avatar="default" />
            <hero name="Hero_Beta" default_avatar="default" />
        </heroes>
        """;
        await File.WriteAllTextAsync(Path.Combine(clientDir, "heroes_preview.xml"), xmlContent);

        // 3. Lua definition only for Hero_Alpha
        string luaDir = Path.Combine(_testTempDir, "games", "client", "override", "ui", "scripts", "newui", "definitions", "heroes");
        Directory.CreateDirectory(luaDir);
        await File.WriteAllTextAsync(Path.Combine(luaDir, "Hero_Alpha.lua"), "return { key = 'Hero_Alpha' }");

        var handler = new InspectHeroPoolHandler(repoRoot: _testTempDir);

        using var doc = JsonDocument.Parse("{\"filter_missing_only\":false}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = GetResultText(result);
        using var jsonDoc = JsonDocument.Parse(text);
        var root = jsonDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("discrepancies_found");
        var summary = root.GetProperty("summary");
        summary.GetProperty("serverEntitiesCount").GetInt32().Should().Be(1);
        summary.GetProperty("clientPreviewCount").GetInt32().Should().Be(2);
        summary.GetProperty("modularLuaCount").GetInt32().Should().Be(1);
        summary.GetProperty("missingOnServerCount").GetInt32().Should().Be(1);

        var discrepancies = root.GetProperty("discrepancies");
        discrepancies.GetProperty("missingOnServer")[0].GetString().Should().Be("Hero_Beta");
        discrepancies.GetProperty("missingLuaDefinition")[0].GetString().Should().Be("Hero_Beta");
    }
}
