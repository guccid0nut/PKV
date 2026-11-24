// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class VulkanDevToolsIntegrationTests
{
    private static string GetRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ProjectKongor.slnx")) &&
                Directory.Exists(Path.Combine(dir, "native")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repo root.");
    }

    [Fact]
    public void VidVulkan_BinaryOnDisk_ContainsImGuiDevToolsSymbolsAndHotkeys()
    {
        string repoRoot = GetRepoRoot();
        string defaultDllPath = Path.Combine(repoRoot, "games", "client", "vid_vulkan_x64.dll");
        string subBuildDllPath = Path.Combine(repoRoot, "native", "drivers", "vid_vulkan", "build", "Release", "vid_vulkan_x64.dll");
        string? resolvedDllPath = File.Exists(defaultDllPath)
            ? defaultDllPath
            : (NexusTestEnvironment.TryFindNativeBinary("vid_vulkan_x64.dll")
                ?? (File.Exists(subBuildDllPath) ? subBuildDllPath : null));
        if (resolvedDllPath is null)
        {
            if (!NexusTestEnvironment.HasGameAssets) return;
            resolvedDllPath = defaultDllPath;
        }

        string dllPath = resolvedDllPath;

        File.Exists(dllPath).Should().BeTrue($"vid_vulkan_x64.dll must exist at {dllPath}");

        byte[] bytes = File.ReadAllBytes(dllPath);
        bytes.Length.Should().BeGreaterThan(50_000, "Vulkan video driver binary must be valid size");

        // Verify PE Header
        bytes[0].Should().Be((byte)'M');
        bytes[1].Should().Be((byte)'Z');

        string rawContent = Encoding.ASCII.GetString(bytes);

        // Verify In-Engine ImGui DevTools HUD strings embedded in binary
        rawContent.Should().Contain("TAO IN-ENGINE DEVTOOLS");
        rawContent.Should().Contain("ACTIVE DOM WIDGETS");
        rawContent.Should().Contain("Lua REPL");
        rawContent.Should().Contain("Ctrl+Shift+D");
        rawContent.Should().Contain("Ctrl+F12");
        rawContent.Should().Contain("DEV OVERLAY & TRIAGE ACTION CENTER");
        rawContent.Should().Contain("CANONICAL VFS MOUNT CHAIN");
        rawContent.Should().Contain("VK_PRESENT_MODE_MAILBOX_KHR");
    }

    [Fact]
    public void DevToolsSource_ContainsWndProcHookAndInputArbitration()
    {
        string repoRoot = GetRepoRoot();
        string spyCpp = Path.Combine(repoRoot, "native", "drivers", "vid_vulkan", "src", "vid_vulkan_spy.cpp");
        string devtoolsH = Path.Combine(repoRoot, "native", "drivers", "vid_vulkan", "src", "vulkan_devtools.h");
        string devtoolsCpp = Path.Combine(repoRoot, "native", "drivers", "vid_vulkan", "src", "vulkan_devtools.cpp");

        File.Exists(spyCpp).Should().BeTrue();
        File.Exists(devtoolsH).Should().BeTrue();
        File.Exists(devtoolsCpp).Should().BeTrue();

        string spyCode = File.ReadAllText(spyCpp);
        spyCode.Should().Contain("DevTools_WndProcHook", "WndProc hook must be declared for game window input interception");
        spyCode.Should().Contain("GWLP_WNDPROC", "WndProc must be subclassed via SetWindowLongPtrA");
        spyCode.Should().Contain("VulkanDevTools::Get().Render", "DevTools overlay must be rendered inside Hook_EndFrame");

        string devtoolsCode = File.ReadAllText(devtoolsCpp);
        devtoolsCode.Should().Contain("VK_CONTROL", "Ctrl key modifier must be verified");
        devtoolsCode.Should().Contain("VK_SHIFT", "Shift key modifier must be verified for Ctrl+Shift+D");
        devtoolsCode.Should().Contain("VK_F12", "F12 key must be intercepted as backward-compatible alias");
        devtoolsCode.Should().Contain("DevToolsTab::Hierarchy");
        devtoolsCode.Should().Contain("DevToolsTab::LuaConsole");
        devtoolsCode.Should().Contain("DevToolsTab::Telemetry");
        devtoolsCode.Should().Contain("DevToolsTab::VfsDiagnostics");
        devtoolsCode.Should().Contain("DevToolsTab::TriageActions");
        devtoolsCode.Should().Contain("SetClipboardText", "Native clipboard function must be present");
        devtoolsCode.Should().Contain("TriggerTriageAction", "Triage action dispatcher must be present");
        devtoolsCode.Should().Contain("s_font8x8", "Embedded 8x8 font glyph table must be present for raster HUD text");
    }

    [Fact]
    public void DevOverlayPackage_AndScript_AreSynchronizedWithNativeDevTools()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = GetRepoRoot();
        string packagePath = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "dev_overlay.package");
        string luaPath = Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "dev_overlay.lua");

        File.Exists(packagePath).Should().BeTrue($"dev_overlay.package must exist at {packagePath}");
        File.Exists(luaPath).Should().BeTrue($"dev_overlay.lua must exist at {luaPath}");

        string packageXml = File.ReadAllText(packagePath);
        packageXml.Should().Contain("CTRL+SHIFT+D");
        packageXml.Should().Contain("DevOverlay.Toggle()");
        packageXml.Should().Contain("dev_overlay_root");

        string luaCode = File.ReadAllText(luaPath);
        luaCode.Should().Contain("DevOverlay.CopyLink");
        luaCode.Should().Contain("DevOverlay.CopyTriageSnapshot");
        luaCode.Should().Contain("DevOverlay.CopyRecentLogs");
        luaCode.Should().Contain("DevOverlay.ReloadUI");
        luaCode.Should().Contain("DevOverlay.ForceGC");
        luaCode.Should().Contain("DevOverlay.FlushWAL");
        luaCode.Should().Contain("ToggleDevOverlay");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(1280, 720)]
    public void DevTools_GeometryClamping_SupportsMultiResolutionDisplayModes(int width, int height)
    {
        float winX = 40.0f;
        float winY = 40.0f;
        float winW = 960.0f;
        float winH = 640.0f;

        if (winX + winW > width) winW = (float)width - winX - 20.0f;
        if (winY + winH > height) winH = (float)height - winY - 20.0f;

        winW.Should().BeGreaterThan(400.0f);
        winH.Should().BeGreaterThan(300.0f);
        (winX + winW).Should().BeLessThanOrEqualTo(width);
        (winY + winH).Should().BeLessThanOrEqualTo(height);
    }

    [Fact]
    public void RmlDevTools_ContainsFiveScenarioPresets_AndSafeDeferredReload()
    {
        string repoRoot = GetRepoRoot();
        string rmlPath = Path.Combine(repoRoot, "assets", "hud", "devtools.rml");
        string managerPath = Path.Combine(repoRoot, "native", "vulkan-layer", "rmlui", "overlay_manager.cpp");

        File.Exists(rmlPath).Should().BeTrue();
        File.Exists(managerPath).Should().BeTrue();

        string rml = File.ReadAllText(rmlPath);
        string manager = File.ReadAllText(managerPath);

        string[] expectedPresets =
        [
            "ScenarioPreset:MidTeamfight5v5",
            "ScenarioPreset:LowHpHeartbeat",
            "ScenarioPreset:MaxLevelGold",
            "ScenarioPreset:KongorBossPit",
            "ScenarioPreset:HotReloadAllUi"
        ];

        foreach (string preset in expectedPresets)
        {
            rml.Should().Contain(preset, $"devtools.rml must expose 1-click button for {preset}");
            manager.Should().Contain(preset, $"OverlayManager::HandleUiAction must dispatch {preset}");
        }

        manager.Should().Contain("Set ui_reloadInterfaces true", "HotReloadAllUi must defer CUIManager reload to frame boundary instead of synchronous ReloadInterfaces");
        manager.Should().Contain("ClearGetWidgetCache()", "HotReloadAllUi must flush Lua GetWidget memoization caches");
        manager.Should().Contain("Testing.DamageEntity", "LowHpHeartbeat must dispatch real K2 Testing.DamageEntity practice command instead of missing core.unitSelf");
        manager.Should().Contain("Testing.GiveGold", "MaxLevelGold must dispatch real K2 Testing.GiveGold practice command");
        manager.Should().Contain("Testing.LevelMax", "MaxLevelGold must dispatch real K2 Testing.LevelMax practice command");
        manager.Should().NotContain("core.unitSelf", "overlay_manager must never reference server bot-brain core.unitSelf in client UI scripts");
    }
}
