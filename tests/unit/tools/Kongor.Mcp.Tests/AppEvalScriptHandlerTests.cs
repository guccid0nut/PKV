// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#if !PLAYTEST_BUILD
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class AppEvalScriptHandlerTests
{
    [Fact]
    [Trait("Category", "Manual")]
    public async Task HandleAsync_SimpleExpression_EvaluatesSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        string jsonArgs = JsonSerializer.Serialize(new { expression = "return 1 + 1;" });
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("2");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task HandleAsync_WithDomainTypeReference_ResolvesAndExecutes()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        string jsonArgs = JsonSerializer.Serialize(new { script = "var acc = new NEXUS.Contracts.Domain.Identifiers.AccountId(9999); return acc.Value;" });
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        var result = await handler.HandleAsync(doc.RootElement);

        string text = ((TextContentItem)result.Content[0]).Text;
        result.IsError.Should().BeFalse(because: text);
        text.Should().Contain("9999");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task HandleAsync_InvalidSyntax_ReturnsCompilationError()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        string jsonArgs = JsonSerializer.Serialize(new { script = "invalid c# syntax here !!!" });
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("compilation failed");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task HandleAsync_SessionVariables_PersistAcrossCalls()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        string session = $"test_session_{Guid.NewGuid():N}";

        string setArgs = JsonSerializer.Serialize(new
        {
            sessionId = session,
            script = "Variables[\"test_key\"] = 12345; return \"stored\";"
        });
        using (JsonDocument doc = JsonDocument.Parse(setArgs))
        {
            var res = await handler.HandleAsync(doc.RootElement);
            res.IsError.Should().BeFalse();
        }

        string getArgs = JsonSerializer.Serialize(new
        {
            sessionId = session,
            script = "return Variables[\"test_key\"];"
        });
        using (JsonDocument doc = JsonDocument.Parse(getArgs))
        {
            var res = await handler.HandleAsync(doc.RootElement);
            res.IsError.Should().BeFalse();
            string text = ((TextContentItem)res.Content[0]).Text;
            text.Should().Contain("12345");
        }
    }
}
#endif
