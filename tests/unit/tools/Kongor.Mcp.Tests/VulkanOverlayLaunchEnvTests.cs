// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Collections.Specialized;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Protects: Foreground Ctrl+Shift+D RmlUi overlay initialization across kongor-cli/MCP launchers
/// and side-effect-free, case-insensitive Win32 CREATE_UNICODE_ENVIRONMENT block construction.
/// Seam: Real GameLauncherService.ConfigureVulkanOverlayEnvironment and InteractiveProcessLauncher.BuildUnicodeEnvironmentBlock.
/// </summary>
public sealed class VulkanOverlayLaunchEnvTests
{
    [Fact]
    public void ConfigureVulkanOverlayEnvironment_ForegroundLaunch_EnablesModernHudAndDevTools()
    {
        // Protects: Foreground launches via GameLauncherService, GameClientManageHandler, and MatchLaunchHandler.
        // Fails when: HON_MODERN_HUD or HON_DEVTOOLS is omitted or set to "0" during interactive sessions.
        var dict = new Dictionary<string, string>();
        GameLauncherService.ConfigureVulkanOverlayEnvironment(dict, isHeadless: false);

        dict["KONGOR_HEADLESS"].Should().Be("0");
        dict["HON_MODERN_HUD"].Should().Be("1");
        dict["HON_DEVTOOLS"].Should().Be("1");

        var stringDict = new StringDictionary();
        GameLauncherService.ConfigureVulkanOverlayEnvironment(stringDict, isHeadless: false);

        stringDict["KONGOR_HEADLESS"].Should().Be("0");
        stringDict["HON_MODERN_HUD"].Should().Be("1");
        stringDict["HON_DEVTOOLS"].Should().Be("1");
    }

    [Fact]
    public void ConfigureVulkanOverlayEnvironment_HeadlessLaunch_DisablesOverlayUnlessExplicitlyOverridden()
    {
        // Protects: Lean headless CI/bot launches (zero RmlUi allocation by default) + explicit diagnostic override support.
        // Fails when: Headless launches enable RmlUi by default or ignore explicit HON_MODERN_HUD/HON_DEVTOOLS overrides.
        var defaultHeadless = new Dictionary<string, string>();
        GameLauncherService.ConfigureVulkanOverlayEnvironment(defaultHeadless, isHeadless: true);

        defaultHeadless["KONGOR_HEADLESS"].Should().Be("1");
        defaultHeadless["HON_MODERN_HUD"].Should().Be("0");
        defaultHeadless["HON_DEVTOOLS"].Should().Be("0");

        var overriddenHeadless = new StringDictionary();
        GameLauncherService.ConfigureVulkanOverlayEnvironment(
            overriddenHeadless,
            isHeadless: true,
            ambientModernHud: "1",
            ambientDevTools: "true");

        overriddenHeadless["KONGOR_HEADLESS"].Should().Be("1");
        overriddenHeadless["HON_MODERN_HUD"].Should().Be("1");
        overriddenHeadless["HON_DEVTOOLS"].Should().Be("true");
    }

    [Fact]
    public void BuildUnicodeEnvironmentBlock_DeduplicatesMixedCaseKeysAndDoesNotMutateCurrentProcess()
    {
        // Protects: Win32 CreateProcess CREATE_UNICODE_ENVIRONMENT block sorting, case-insensitive deduplication
        // when StringDictionary lowercases keys, double-NUL termination, and parent kongor.exe env isolation.
        // Fails when: Duplicate mixed-case keys appear in lpEnvironment or Environment.GetEnvironmentVariable is mutated.
        string sentinelKey = $"KONGOR_TEST_ISOLATION_{Guid.NewGuid():N}";
        Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull();

        var baseEnv = new Hashtable
        {
            ["PATH"] = @"C:\Windows\System32",
            ["HON_MODERN_HUD"] = "0",
            ["KONGOR_HEADLESS"] = "1",
            ["=C:"] = @"C:\ProjectKongor"
        };

        var overrides = new Dictionary<string, string>
        {
            ["hon_modern_hud"] = "1",
            ["hon_devtools"] = "1",
            [sentinelKey] = "isolated_child_only"
        };

        string block = InteractiveProcessLauncher.BuildUnicodeEnvironmentBlock(
            headless: false,
            environmentVariables: overrides,
            baseEnvironment: baseEnv);

        // Parent process environment must remain untouched.
        Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull();

        // Must end with Win32 double-NUL terminator.
        block.EndsWith("\0\0", StringComparison.Ordinal).Should().BeTrue();

        string[] entries = block.TrimEnd('\0').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        entries.Should().Contain(e => string.Equals(e, "KONGOR_HEADLESS=0", StringComparison.OrdinalIgnoreCase));
        entries.Should().Contain(e => string.Equals(e, "hon_modern_hud=1", StringComparison.OrdinalIgnoreCase));
        entries.Should().Contain(e => string.Equals(e, "hon_devtools=1", StringComparison.OrdinalIgnoreCase));
        entries.Should().Contain($"{sentinelKey}=isolated_child_only");
        entries.Should().Contain(@"=C:=C:\ProjectKongor");

        // Verify zero duplicate keys ignoring case.
        string[] keys = entries.Select(e =>
        {
            int eqIdx = e.IndexOf('=', startIndex: 1);
            return eqIdx > 0 ? e[..eqIdx] : e;
        }).ToArray();

        keys.Distinct(StringComparer.OrdinalIgnoreCase).Count().Should().Be(keys.Length);

        // Verify ordinal case-insensitive sort order required by Win32 CreateProcess.
        string[] expectedSorted = [.. entries];
        Array.Sort(expectedSorted, StringComparer.OrdinalIgnoreCase);
        entries.Should().Equal(expectedSorted);
    }
}
