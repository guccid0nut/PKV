// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpToolRegistryBundledSafeTests : IDisposable
{
    private readonly string _tempBundle;

    public McpToolRegistryBundledSafeTests()
    {
        _tempBundle = Path.Combine(Path.GetTempPath(), "kongor_bundle_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempBundle);
        File.WriteAllText(Path.Combine(_tempBundle, "playtest_policy.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_tempBundle, "games", "client"));
        Directory.CreateDirectory(Path.Combine(_tempBundle, "tools-win-x64"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempBundle))
            {
                Directory.Delete(_tempBundle, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestCleanup] Failed to delete temp directory '{_tempBundle}': {ex.Message}");
        }
    }

    [Fact]
    public void BundledMode_ConstructsCleanlyWithoutThrowingOnSkippedAliases()
    {
        string probeDir = Path.Combine(_tempBundle, "tools-win-x64");
        Action act = () => _ = new McpToolRegistry(registerDefaults: true, bundleProbeDirectory: probeDir);
        act.Should().NotThrow();
    }

    [Fact]
    public void BundledMode_UnsafeToolsAreNotPresentInRegistryOrList()
    {
        string probeDir = Path.Combine(_tempBundle, "tools-win-x64");
        McpToolRegistry registry = new(registerDefaults: true, bundleProbeDirectory: probeDir);

        registry.IsBundledMode.Should().BeTrue();

        // kongor_db_migrate, db_migrate, kongor_app_eval, kongor_db_fixture are NOT present
        registry.HasTool("kongor_db_migrate").Should().BeFalse();
        registry.HasTool("db_migrate").Should().BeFalse();
        registry.HasTool("kongor_app_eval").Should().BeFalse();
        registry.HasTool("kongor_db_fixture").Should().BeFalse();

        var toolNames = registry.ListTools().Select(t => t.Name).ToList();
        toolNames.Should().NotContain("kongor_db_migrate");
        toolNames.Should().NotContain("db_migrate");
        toolNames.Should().NotContain("kongor_app_eval");
        toolNames.Should().NotContain("kongor_db_fixture");
    }

    [Fact]
    public void BundledMode_SafeToolsArePresent()
    {
        string probeDir = Path.Combine(_tempBundle, "tools-win-x64");
        McpToolRegistry registry = new(registerDefaults: true, bundleProbeDirectory: probeDir);

        registry.IsBundledMode.Should().BeTrue();

        // Safe standalone tools (including local server & COMPEL lifecycle management) ARE present
        registry.HasTool("kongor_health_check").Should().BeTrue();
        registry.HasTool("kongor_errors_since").Should().BeTrue();
        registry.HasTool("kongor_vfs_resolve").Should().BeTrue();
        registry.HasTool("kongor_vfs_locate").Should().BeTrue();
        registry.HasTool("kongor_get_client_logs").Should().BeTrue();
        registry.HasTool("kongor_log_correlate").Should().BeTrue();
        registry.HasTool("kongor_server_manage").Should().BeTrue();
        registry.HasTool("kongor_compel_manage").Should().BeTrue();

        var toolNames = registry.ListTools().Select(t => t.Name).ToList();
        toolNames.Should().Contain("kongor_health_check");
        toolNames.Should().Contain("kongor_errors_since");
        toolNames.Should().Contain("kongor_vfs_resolve");
        toolNames.Should().Contain("kongor_vfs_locate");
        toolNames.Should().Contain("kongor_get_client_logs");
        toolNames.Should().Contain("kongor_log_correlate");
        toolNames.Should().Contain("kongor_server_manage");
        toolNames.Should().Contain("kongor_compel_manage");
    }

    [Fact]
    public void NonBundledMode_DefaultRegistryStillRegistersDbMigrate()
    {
        // Default new McpToolRegistry() (non-bundled) still registers kongor_db_migrate
        McpToolRegistry registry = new(registerDefaults: true);
        registry.IsBundledMode.Should().BeFalse();

        registry.HasTool("kongor_db_migrate").Should().BeTrue();
        registry.HasTool("db_migrate").Should().BeTrue();
        registry.ListTools().Select(t => t.Name).Should().Contain("kongor_db_migrate");
    }
}
