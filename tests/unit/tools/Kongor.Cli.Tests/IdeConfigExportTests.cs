// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Kongor.Cli.Mcp.Registration;
using Xunit;

namespace Kongor.Cli.Tests;

/// <summary>
/// Value-Weighted Testing Gate for Cross-IDE MCP Configuration Export (H2-1c):
/// - protects: Exact schema shapes across all 6 IDEs (Antigravity, Cursor, VS Code, Zed, Claude, Codex TOML),
///   merge-never-overwrite preservation of sibling servers/settings, idempotent TOML re-runs, and global CLI path preference.
/// - fails_when: VS Code uses 'mcpServers' instead of 'servers'+'type:stdio', Zed omits 'context_servers'+'source:custom',
///   Codex TOML duplicates [mcp_servers.kongor] or clobbers sibling tables, or existing IDE settings are lost.
/// - seam: Real IdeConfigExporter.Export and AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand against temp filesystem.
/// </summary>
public sealed class IdeConfigExportTests : IDisposable
{
    private readonly string _tempRoot;

    public IdeConfigExportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "kongor_ide_export_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Export_AllSixIdes_ProducesExactGoldenShapes_PreservesExistingEntries_AndIsIdempotent()
    {
        string userHome = Path.Combine(_tempRoot, "home");
        string workspace = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(userHome);
        Directory.CreateDirectory(workspace);

        // Seed pre-existing user configurations with sibling servers and custom settings across all 6 IDEs
        string agyPath = IdeConfigExporter.ResolveTargetConfigPath("antigravity", userHome, isSandboxedHome: true, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(agyPath)!);
        File.WriteAllText(agyPath, """{"mcpServers":{"ghidra":{"command":"python","args":["ghidra_mcp.py"]}}}""");

        string cursorPath = IdeConfigExporter.ResolveTargetConfigPath("cursor", userHome, isSandboxedHome: true, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        File.WriteAllText(cursorPath, """{"mcpServers":{"playwright":{"command":"npx","args":["@playwright/mcp"]}}}""");

        string vscodePath = IdeConfigExporter.ResolveTargetConfigPath("vscode", userHome, isSandboxedHome: true, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(vscodePath)!);
        File.WriteAllText(vscodePath, """{"inputs":[{"id":"apiKey"}],"servers":{"github":{"type":"stdio","command":"gh-mcp"}}}""");

        string zedPath = IdeConfigExporter.ResolveTargetConfigPath("zed", userHome, isSandboxedHome: true, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(zedPath)!);
        File.WriteAllText(zedPath, """{"vim_mode":true,"theme":"One Dark","context_servers":{"postgres":{"source":"custom","command":"pg-mcp"}}}""");

        string claudePath = IdeConfigExporter.ResolveTargetConfigPath("claude", userHome, isSandboxedHome: true, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(claudePath)!);
        File.WriteAllText(claudePath, """{"globalShortcut":"Ctrl+Space","mcpServers":{"memory":{"command":"npx","args":["server-memory"]}}}""");

        string codexPath = IdeConfigExporter.ResolveTargetConfigPath("codex", userHome, isSandboxedHome: true, workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        File.WriteAllText(codexPath, """
            model = "gpt-5-codex"

            [mcp_servers.everything]
            command = "es-mcp"
            args = ["--fast"]

            [mcp_servers.kongor]
            command = "stale-kongor"
            args = ["old"]
            """);

        Dictionary<string, string> env = new(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = "/opt/dotnet",
            ["PYTHONUNBUFFERED"] = "1"
        };

        // Pass 1 + Pass 2 (verify idempotency)
        IReadOnlyList<IdeExportResult> results = [];
        for (int pass = 0; pass < 2; pass++)
        {
            results = IdeConfigExporter.Export(
                "all",
                commandPath: "/home/dev/.dotnet/tools/kongor",
                args: ["mcp"],
                env: env,
                userHomeOverride: userHome,
                workspaceDir: workspace);

            results.Should().HaveCount(6);
            results.Should().OnlyContain(r => r.Success, "all 6 IDE exports must succeed cleanly");
        }

        // 1. Antigravity golden verification
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(agyPath)))
        {
            JsonElement servers = doc.RootElement.GetProperty("mcpServers");
            servers.GetProperty("ghidra").GetProperty("command").GetString().Should().Be("python");
            JsonElement kongor = servers.GetProperty("kongor");
            kongor.GetProperty("command").GetString().Should().Be("/home/dev/.dotnet/tools/kongor");
            kongor.GetProperty("args")[0].GetString().Should().Be("mcp");
            kongor.GetProperty("env").GetProperty("DOTNET_ROOT").GetString().Should().Be("/opt/dotnet");
        }

        // 2. Cursor golden verification (.cursor/mcp.json -> mcpServers)
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cursorPath)))
        {
            JsonElement servers = doc.RootElement.GetProperty("mcpServers");
            servers.GetProperty("playwright").GetProperty("command").GetString().Should().Be("npx");
            servers.GetProperty("kongor").GetProperty("command").GetString().Should().Be("/home/dev/.dotnet/tools/kongor");
        }

        // 3. VS Code golden verification (.vscode/mcp.json -> servers + type: stdio, NOT mcpServers)
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(vscodePath)))
        {
            doc.RootElement.TryGetProperty("mcpServers", out _).Should().BeFalse("VS Code uses 'servers', not 'mcpServers'");
            doc.RootElement.GetProperty("inputs").GetArrayLength().Should().Be(1, "existing VS Code inputs array must be preserved");
            JsonElement servers = doc.RootElement.GetProperty("servers");
            servers.GetProperty("github").GetProperty("command").GetString().Should().Be("gh-mcp");
            JsonElement kongor = servers.GetProperty("kongor");
            kongor.GetProperty("type").GetString().Should().Be("stdio");
            kongor.GetProperty("command").GetString().Should().Be("/home/dev/.dotnet/tools/kongor");
            kongor.GetProperty("args")[0].GetString().Should().Be("mcp");
        }

        // 4. Zed golden verification (settings.json -> context_servers + source: custom + editor settings preserved)
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(zedPath)))
        {
            doc.RootElement.GetProperty("vim_mode").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("theme").GetString().Should().Be("One Dark");
            JsonElement ctxServers = doc.RootElement.GetProperty("context_servers");
            ctxServers.GetProperty("postgres").GetProperty("command").GetString().Should().Be("pg-mcp");
            JsonElement kongor = ctxServers.GetProperty("kongor");
            kongor.GetProperty("source").GetString().Should().Be("custom");
            kongor.GetProperty("command").GetString().Should().Be("/home/dev/.dotnet/tools/kongor");
        }

        // 5. Claude golden verification (claude_desktop_config.json -> mcpServers + globalShortcut preserved)
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(claudePath)))
        {
            doc.RootElement.GetProperty("globalShortcut").GetString().Should().Be("Ctrl+Space");
            JsonElement servers = doc.RootElement.GetProperty("mcpServers");
            servers.GetProperty("memory").GetProperty("command").GetString().Should().Be("npx");
            servers.GetProperty("kongor").GetProperty("command").GetString().Should().Be("/home/dev/.dotnet/tools/kongor");
        }

        // 6. Codex TOML golden verification (~/.codex/config.toml -> single [mcp_servers.kongor] + [mcp_servers.kongor.env])
        string codexToml = File.ReadAllText(codexPath);
        codexToml.Should().Contain("model = \"gpt-5-codex\"");
        codexToml.Should().Contain("[mcp_servers.everything]");
        codexToml.Should().NotContain("stale-kongor", "previous kongor TOML block must be cleanly replaced");
        Regex.Count(codexToml, @"^\[mcp_servers\.kongor\]\r?$", RegexOptions.Multiline).Should().Be(1);
        Regex.Count(codexToml, @"^\[mcp_servers\.kongor\.env\]\r?$", RegexOptions.Multiline).Should().Be(1);
        codexToml.Should().Contain("command = \"/home/dev/.dotnet/tools/kongor\"");
        codexToml.Should().Contain("args = [\"mcp\"]");
        codexToml.Should().Contain("DOTNET_ROOT = \"/opt/dotnet\"");
    }

    [Fact]
    public void ResolveCanonicalDeveloperMcpCommand_PrefersGlobalCliWhenRequestedOrOutsideCheckout()
    {
        string fakeHome = Path.Combine(_tempRoot, "cli_home");
        string toolsDir = Path.Combine(fakeHome, ".dotnet", "tools");
        Directory.CreateDirectory(toolsDir);
        string globalExe = Path.Combine(toolsDir, OperatingSystem.IsWindows() ? "kongor.exe" : "kongor");
        File.WriteAllText(globalExe, "fake-global-cli");

        string nonRepoDir = Path.Combine(_tempRoot, "empty_dir");
        Directory.CreateDirectory(nonRepoDir);

        var (cmdOutside, argsOutside, _) = AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand(
            processPath: null,
            repoRootOverride: nonRepoDir,
            userHomeOverride: fakeHome);

        cmdOutside.Should().Be(globalExe);
        argsOutside.Should().Equal("mcp");

        string fakeRepo = Path.Combine(_tempRoot, "fake_repo");
        Directory.CreateDirectory(Path.Combine(fakeRepo, "src", "tools"));
        File.WriteAllText(Path.Combine(fakeRepo, "ProjectKongor.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(fakeRepo, "src", "tools", "tao_mcp.py"), "# tao");

        var (cmdPreferGlobal, argsPreferGlobal, _) = AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand(
            processPath: null,
            repoRootOverride: fakeRepo,
            userHomeOverride: fakeHome,
            preferGlobalCli: true);

        cmdPreferGlobal.Should().Be(globalExe);
        argsPreferGlobal.Should().Equal("mcp");
    }
}
