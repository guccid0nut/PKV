// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.Tests;

/// <summary>
///     Guards against "tool pollution": the same capability advertised under 2-4 names in tools/list.
///     Legacy / short names must be registered via <see cref="McpToolRegistry.RegisterAlias" /> (hidden, still callable).
/// </summary>
public sealed class McpToolAliasConsolidationTests(ITestOutputHelper output)
{
    // Every name below used to be advertised as a separate tool with an identical schema + handler.
    public static IEnumerable<object[]> ConsolidatedAliases() =>
    [
        ["kongor_capture_transient", "kongor_transient_capture"],
        ["kongor_vector", "kongor_vector_search"],
        ["kongor_navigate", "kongor_client_navigate"],
        ["kongor_nav", "kongor_client_navigate"],
        ["kongor_practice", "kongor_client_practice"],
        ["client_practice", "kongor_client_practice"],
        ["practice_match", "kongor_client_practice"],
        ["fixture_inject", "kongor_fixture_inject"],
        ["game_fixture", "kongor_fixture_inject"],
        ["crash_triage", "kongor_crash_triage"],
        ["kongor_triage", "kongor_crash_triage"],
        ["packet_fuzz", "kongor_net_packet_fuzz"],
        ["net_fuzz", "kongor_net_packet_fuzz"],
        ["audit_stats", "kongor_audit_stats"],
        ["audit_ui", "kongor_audit_stats"],
        ["memory_sync", "kongor_memory_sync"],
        ["vfs_locate", "kongor_vfs_locate"],
        ["crash_patch", "kongor_crash_patch"],
        ["asset_bake_pipeline", "kongor_asset_bake_pipeline"],
        ["audio_studio", "kongor_audio_studio"],
        ["self_heal", "kongor_self_heal"],
        ["match_analytics", "kongor_match_analytics"],
        ["kongor_lakehouse_query", "kongor_match_analytics"],
        ["ci_trigger", "kongor_ci_trigger"],
        ["db_migrate", "kongor_db_migrate"],
        ["log_correlate", "kongor_log_correlate"],
        ["deploy_canary", "kongor_deploy_canary"],
        ["sync_mirrors", "kongor_sync_mirrors"],
        ["client_eval_lua", "kongor_client_eval_lua"],
        ["vfs_clean_cache", "kongor_vfs_clean_cache"],
        ["validate_previews", "kongor_validate_previews"],
        ["inspect_model_mdf", "kongor_inspect_model_mdf"],
        ["inspect_hero_pool", "kongor_inspect_hero_pool"],
        ["get_client_logs", "kongor_get_client_logs"],
        ["set_store_mode", "kongor_set_store_mode"],
        ["client_capture_preview", "kongor_client_capture_preview"],
        ["vfs_completeness_audit", "kongor_vfs_completeness_audit"]
    ];

    [Theory]
    [MemberData(nameof(ConsolidatedAliases))]
    public void ConsolidatedAlias_IsCallableButHiddenFromToolsList(string alias, string canonical)
    {
        McpToolRegistry registry = new(registerDefaults: true);
        List<string> listed = registry.ListTools().Select(t => t.Name).ToList();

        if (McpToolRegistry.QuarantinedSimulatedTools.Contains(canonical))
        {
            listed.Should().NotContain(canonical, $"quarantined tool '{canonical}' must not be advertised by default");
        }
        else
        {
            listed.Should().Contain(canonical, $"canonical tool '{canonical}' must be advertised");
        }

        listed.Should().NotContain(alias, $"alias '{alias}' must not pollute tools/list");

        registry.HasTool(alias).Should().BeTrue($"alias '{alias}' must remain callable for backwards compatibility");
        registry.GetTool(alias)!.Name.Should().Be(canonical);
        registry.ListAliases().Should().ContainKey(alias).WhoseValue.Should().Be(canonical);
    }

    [Fact]
    public void ToolsList_HasNoTwoToolsWithIdenticalDescriptionAndSchema()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        IReadOnlyList<ToolDefinition> tools = registry.ListTools();

        output.WriteLine($"[McpToolAliasConsolidation] advertised tools = {tools.Count}, hidden aliases = {registry.ListAliases().Count}");

        List<string> duplicateGroups = tools
            .GroupBy(t => t.Description + "\u0000" + JsonSerializer.Serialize(t.InputSchema))
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" = ", group.Select(t => t.Name)))
            .ToList();

        foreach (string group in duplicateGroups)
            output.WriteLine($"[McpToolAliasConsolidation] DUPLICATE: {group}");

        duplicateGroups.Should().BeEmpty(
            "identical tools must be registered once with RegisterAlias(...) for the extra names");
    }

    [Fact]
    public async Task ExecutingAlias_DispatchesToCanonicalHandler()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        int calls = 0;

        registry.RegisterTool(new ToolDefinition
        {
            Name = "kongor_canonical_probe",
            Description = "probe",
            InputSchema = new ToolInputSchema { Type = "object" }
        }, (_, _) =>
        {
            calls++;
            return Task.FromResult(ToolCallResult.Success("ok"));
        });
        registry.RegisterAlias("probe_alias", "kongor_canonical_probe");

        using JsonDocument doc = JsonDocument.Parse("{}");
        ToolCallResult result = await registry.ExecuteToolAsync("probe_alias", doc.RootElement);

        result.IsError.Should().BeFalse();
        calls.Should().Be(1);
        registry.ListTools().Select(t => t.Name).Should().Equal("kongor_canonical_probe");
    }

    [Fact]
    public void RegisterAlias_ForUnknownCanonical_Throws()
    {
        McpToolRegistry registry = new(registerDefaults: false);

        Action act = () => registry.RegisterAlias("orphan_alias", "kongor_does_not_exist");

        act.Should().Throw<InvalidOperationException>().WithMessage("*kongor_does_not_exist*");
    }

    [Fact]
    public void RegisterAlias_ShadowingCanonicalTool_Throws()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        foreach (string name in new[] { "kongor_a", "kongor_b" })
        {
            registry.RegisterTool(new ToolDefinition
            {
                Name = name,
                Description = name,
                InputSchema = new ToolInputSchema { Type = "object" }
            }, (_, _) => Task.FromResult(ToolCallResult.Success("ok")));
        }

        Action act = () => registry.RegisterAlias("kongor_b", "kongor_a");

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }
}
