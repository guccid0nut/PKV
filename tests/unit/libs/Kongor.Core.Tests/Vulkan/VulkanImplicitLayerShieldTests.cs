// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.Collections.Specialized;
using ASPIRE.Common.Vulkan;
using Xunit;

namespace Kongor.Core.Tests.Vulkan;

public sealed class VulkanImplicitLayerShieldTests
{
    private const string RenderDocManifestJson = """
        {
          "file_format_version": "1.1.2",
          "layer": {
            "name": "VK_LAYER_RENDERDOC_Capture",
            "type": "GLOBAL",
            "library_path": ".\\renderdoc.dll",
            "enable_environment": {
              "ENABLE_VULKAN_RENDERDOC_CAPTURE": "1"
            },
            "disable_environment": {
              "DISABLE_VULKAN_RENDERDOC_CAPTURE_1_46": "1"
            }
          }
        }
        """;

    private const string MultiLayerManifestJson = """
        {
          "file_format_version": "1.1.2",
          "layers": [
            {
              "name": "VK_LAYER_HON_replay_capture",
              "disable_environment": {
                "HON_CAPTURE_DISABLE": "1"
              }
            },
            {
              "name": "VK_LAYER_MEDIASDK_HOOK",
              "disable_environment": {
                "DISABLE_VK_LAYER_MEDIASDK_HOOK_1": "1"
              }
            }
          ]
        }
        """;

    [Fact]
    public void ParseManifestJson_ExtractsDisableAndEnableKeys_AndSkipsKongorOwnLayer()
    {
        IReadOnlyList<VulkanImplicitLayerEntry> renderDocEntries =
            VulkanImplicitLayerShield.ParseManifestJson(RenderDocManifestJson, @"C:\Program Files\RenderDoc\renderdoc.json");
        VulkanImplicitLayerEntry rd = Assert.Single(renderDocEntries);
        Assert.Equal("VK_LAYER_RENDERDOC_Capture", rd.LayerName);
        Assert.Equal("1", rd.DisableEnvironment["DISABLE_VULKAN_RENDERDOC_CAPTURE_1_46"]);
        Assert.Equal("1", rd.EnableEnvironment["ENABLE_VULKAN_RENDERDOC_CAPTURE"]);

        IReadOnlyList<VulkanImplicitLayerEntry> multiEntries =
            VulkanImplicitLayerShield.ParseManifestJson(MultiLayerManifestJson, @"C:\Layers\multi.json");
        VulkanImplicitLayerEntry mediaSdk = Assert.Single(multiEntries);
        Assert.Equal("VK_LAYER_MEDIASDK_HOOK", mediaSdk.LayerName);
        Assert.Equal("1", mediaSdk.DisableEnvironment["DISABLE_VK_LAYER_MEDIASDK_HOOK_1"]);
    }

    [Fact]
    public void BuildShieldPlan_AndApplyToEnvironment_NeutralizesDiscoveredAndFallbackLayers()
    {
        List<VulkanImplicitLayerEntry> entries =
        [
            .. VulkanImplicitLayerShield.ParseManifestJson(RenderDocManifestJson, @"C:\Program Files\RenderDoc\renderdoc.json"),
            .. VulkanImplicitLayerShield.ParseManifestJson(MultiLayerManifestJson, @"C:\Layers\multi.json")
        ];

        VulkanImplicitLayerShieldPlan plan = VulkanImplicitLayerShield.BuildShieldPlan(
            entries,
            allowThirdPartyVulkanOverlays: false);

        Assert.Contains("VK_LAYER_RENDERDOC_Capture", plan.NeutralizedLayerNames);
        Assert.Contains("VK_LAYER_MEDIASDK_HOOK", plan.NeutralizedLayerNames);
        Assert.Equal("1", plan.DisableEnvironmentVariables["DISABLE_VULKAN_RENDERDOC_CAPTURE_1_46"]);
        Assert.Equal("1", plan.DisableEnvironmentVariables["DISABLE_VK_LAYER_MEDIASDK_HOOK_1"]);
        Assert.Equal("1", plan.DisableEnvironmentVariables["DISABLE_VULKAN_OBS_CAPTURE"]);
        Assert.Contains("ENABLE_VULKAN_RENDERDOC_CAPTURE", plan.ClearedEnableEnvironmentVariables);

        StringDictionary childEnv = new()
        {
            ["ENABLE_VULKAN_RENDERDOC_CAPTURE"] = "1"
        };
        int injectedCount = VulkanImplicitLayerShield.ApplyToEnvironment(
            childEnv,
            plan,
            setEmptyInsteadOfRemoveForEnableVars: true);

        Assert.True(injectedCount >= 7);
        Assert.Equal("1", childEnv["DISABLE_VULKAN_RENDERDOC_CAPTURE_1_46"]);
        Assert.Equal("1", childEnv["DISABLE_VK_LAYER_MEDIASDK_HOOK_1"]);
        Assert.Equal(string.Empty, childEnv["ENABLE_VULKAN_RENDERDOC_CAPTURE"]);

        string summary = VulkanImplicitLayerShield.FormatDiagnosticSummary(plan);
        Assert.Contains("VK_LAYER_RENDERDOC_Capture", summary, StringComparison.Ordinal);
        Assert.Contains("DISABLE_VULKAN_RENDERDOC_CAPTURE_1_46", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShieldPlan_WhenThirdPartyOverlaysAllowed_DoesNotInjectDisableKeys()
    {
        IReadOnlyList<VulkanImplicitLayerEntry> entries =
            VulkanImplicitLayerShield.ParseManifestJson(RenderDocManifestJson, @"C:\Program Files\RenderDoc\renderdoc.json");

        VulkanImplicitLayerShieldPlan plan = VulkanImplicitLayerShield.BuildShieldPlan(
            entries,
            allowThirdPartyVulkanOverlays: true);

        Assert.Single(plan.DiscoveredThirdPartyLayerNames);
        Assert.Contains("VK_LAYER_RENDERDOC_Capture", plan.DiscoveredThirdPartyLayerNames);
        Assert.Empty(plan.NeutralizedLayerNames);
        Assert.Empty(plan.DisableEnvironmentVariables);
        Assert.Empty(plan.ClearedEnableEnvironmentVariables);
    }

    [Fact]
    public void BuildLoaderLayersDisableMask_IncludesRenderDocMediaSdkAndValvePatterns()
    {
        string? offscreenMask = VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
            injectKongorReplayCaptureLayer: true,
            allowThirdPartyVulkanOverlays: false);
        Assert.NotNull(offscreenMask);
        Assert.Contains("~implicit~", offscreenMask, StringComparison.Ordinal);
        Assert.Contains("VK_LAYER_RENDERDOC*", offscreenMask, StringComparison.Ordinal);
        Assert.Contains("VK_LAYER_MEDIASDK*", offscreenMask, StringComparison.Ordinal);
        Assert.Contains("VK_LAYER_VALVE*", offscreenMask, StringComparison.Ordinal);
        Assert.DoesNotContain("VK_LAYER_HON_replay_capture", offscreenMask, StringComparison.Ordinal);

        string? perfCleanMask = VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
            injectKongorReplayCaptureLayer: false,
            allowThirdPartyVulkanOverlays: false);
        Assert.NotNull(perfCleanMask);
        Assert.Contains("VK_LAYER_HON_replay_capture", perfCleanMask, StringComparison.Ordinal);
        Assert.Contains("VK_LAYER_RENDERDOC*", perfCleanMask, StringComparison.Ordinal);

        Assert.Null(VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
            injectKongorReplayCaptureLayer: true,
            allowThirdPartyVulkanOverlays: true));
        Assert.Equal("VK_LAYER_HON_replay_capture", VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
            injectKongorReplayCaptureLayer: false,
            allowThirdPartyVulkanOverlays: true));
    }
}
