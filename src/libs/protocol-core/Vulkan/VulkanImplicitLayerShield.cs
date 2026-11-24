// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

// ============================================================================
// File: VulkanImplicitLayerShield.cs
// System: ASPIRE.Common - Shared Vulkan Runtime Hardening (WILLOWMAKER + MCP)
// Subsystem: Universal Khronos ImplicitLayers Discovery & Neutralization
// Policy: Strict Explicit Typing (NO 'var'), AOT-Safe JsonDocument Parsing
// ============================================================================

using System.Collections.Specialized;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace ASPIRE.Common.Vulkan;

/// <summary>
///     Represents a single third-party Khronos Vulkan implicit layer discovered from a manifest JSON file.
/// </summary>
internal sealed record VulkanImplicitLayerEntry(
    string LayerName,
    string ManifestPath,
    IReadOnlyDictionary<string, string> DisableEnvironment,
    IReadOnlyDictionary<string, string> EnableEnvironment
);

/// <summary>
///     Immutable environment-injection plan produced by <see cref="VulkanImplicitLayerShield" />.
/// </summary>
public sealed record VulkanImplicitLayerShieldPlan(
    IReadOnlyList<string> DiscoveredThirdPartyLayerNames,
    IReadOnlyList<string> NeutralizedLayerNames,
    IReadOnlyDictionary<string, string> DisableEnvironmentVariables,
    IReadOnlyList<string> ClearedEnableEnvironmentVariables
);

/// <summary>
///     Enumerates registered Windows Khronos Vulkan <c>ImplicitLayers</c> (HKLM and HKCU 64-bit views),
///     parses each manifest's <c>disable_environment</c> and <c>enable_environment</c> keys without reflection,
///     and injects them into child <c>hon_x64.exe</c> process environments so third-party hooks
///     (OBS, Steam Overlay, Steam Fossilize, RenderDoc, TikTok MediaSDK, RTSS, Overwolf, etc.) cannot load
///     via pre-instance Vulkan callbacks or older Vulkan Loader builds.
/// </summary>
public static class VulkanImplicitLayerShield
{
    public const string KhronosImplicitLayersRegistryPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    private const string ThirdPartyLoaderDisablePatterns =
        "~implicit~,VK_LAYER_OBS_*,VK_LAYER_RTSS*,VK_LAYER_ACTION*,VK_LAYER_OW_*,VK_LAYER_DISCORD*,VK_LAYER_MEDAL*,VK_LAYER_RENDERDOC*,VK_LAYER_MEDIASDK*,VK_LAYER_VALVE*";

    private static readonly IReadOnlyDictionary<string, string> StaticFallbackDisableEnv =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DISABLE_VULKAN_OBS_CAPTURE"] = "1",
            ["DISABLE_VK_LAYER_VALVE_steam_overlay_1"] = "1",
            ["DISABLE_VK_LAYER_VALVE_steam_fossilize_1"] = "1",
            ["DISABLE_VK_LAYER_MEDIASDK_HOOK_1"] = "1",
            ["DISABLE_VULKAN_RENDERDOC_CAPTURE"] = "1",
            ["DISABLE_RTSS_VULKAN_LAYER"] = "1"
        };

    private static readonly string[] StaticFallbackEnableEnvKeys =
    [
        "ENABLE_VULKAN_RENDERDOC_CAPTURE"
    ];

    /// <summary>
    ///     Computes the canonical <c>VK_LOADER_LAYERS_DISABLE</c> mask for a child client launch.
    ///     Returns <see langword="null" /> when third-party overlays are explicitly allowed AND Kongor's
    ///     own replay-capture layer is also enabled.
    /// </summary>
    public static string? BuildLoaderLayersDisableMask(
        bool injectKongorReplayCaptureLayer,
        bool allowThirdPartyVulkanOverlays)
    {
        if (!allowThirdPartyVulkanOverlays)
        {
            return injectKongorReplayCaptureLayer
                ? ThirdPartyLoaderDisablePatterns
                : $"~implicit~,VK_LAYER_HON_replay_capture,{ThirdPartyLoaderDisablePatterns["~implicit~,".Length..]}";
        }

        return injectKongorReplayCaptureLayer
            ? null
            : "VK_LAYER_HON_replay_capture";
    }

    /// <summary>
    ///     Discovers enabled third-party Khronos Vulkan implicit layers on the local host (Windows HKLM + HKCU)
    ///     and constructs the neutralization plan.
    /// </summary>
    public static VulkanImplicitLayerShieldPlan DiscoverAndBuildPlan(
        bool allowThirdPartyVulkanOverlays = false,
        Action<string>? log = null)
    {
        IReadOnlyList<VulkanImplicitLayerEntry> discovered = OperatingSystem.IsWindows()
            ? DiscoverHostImplicitLayersWindows(log)
            : Array.Empty<VulkanImplicitLayerEntry>();

        return BuildShieldPlan(discovered, allowThirdPartyVulkanOverlays);
    }

    /// <summary>
    ///     Builds a deterministic neutralization plan from a pre-parsed set of implicit layer entries.
    /// </summary>
    internal static VulkanImplicitLayerShieldPlan BuildShieldPlan(
        IEnumerable<VulkanImplicitLayerEntry> entries,
        bool allowThirdPartyVulkanOverlays)
    {
        List<VulkanImplicitLayerEntry> thirdParty = [];
        SortedSet<string> discoveredNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenManifestLayers = new(StringComparer.OrdinalIgnoreCase);

        if (entries is not null)
        {
            foreach (VulkanImplicitLayerEntry entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.LayerName))
                {
                    continue;
                }

                if (IsKongorOwnedLayer(entry.LayerName))
                {
                    continue;
                }

                string dedupeKey = $"{entry.LayerName}|{entry.ManifestPath}";
                if (seenManifestLayers.Add(dedupeKey))
                {
                    thirdParty.Add(entry);
                    discoveredNames.Add(entry.LayerName);
                }
            }
        }

        if (allowThirdPartyVulkanOverlays)
        {
            return new VulkanImplicitLayerShieldPlan(
                DiscoveredThirdPartyLayerNames: discoveredNames.ToArray(),
                NeutralizedLayerNames: Array.Empty<string>(),
                DisableEnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ClearedEnableEnvironmentVariables: Array.Empty<string>());
        }

        SortedSet<string> neutralizedNames = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> disableEnv = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> clearedEnableKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> fallback in StaticFallbackDisableEnv)
        {
            disableEnv[fallback.Key] = fallback.Value;
        }

        foreach (string fallbackEnable in StaticFallbackEnableEnvKeys)
        {
            clearedEnableKeys.Add(fallbackEnable);
        }

        foreach (VulkanImplicitLayerEntry entry in thirdParty)
        {
            neutralizedNames.Add(entry.LayerName);

            foreach (KeyValuePair<string, string> kv in entry.DisableEnvironment)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                {
                    disableEnv[kv.Key.Trim()] = string.IsNullOrEmpty(kv.Value) ? "1" : kv.Value;
                }
            }

            foreach (KeyValuePair<string, string> kv in entry.EnableEnvironment)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                {
                    clearedEnableKeys.Add(kv.Key.Trim());
                }
            }
        }

        return new VulkanImplicitLayerShieldPlan(
            DiscoveredThirdPartyLayerNames: discoveredNames.ToArray(),
            NeutralizedLayerNames: neutralizedNames.ToArray(),
            DisableEnvironmentVariables: disableEnv,
            ClearedEnableEnvironmentVariables: clearedEnableKeys.ToArray());
    }

    /// <summary>
    ///     Parses a Khronos Vulkan layer manifest JSON document (single <c>layer</c> or multi <c>layers</c>)
    ///     and returns all non-Kongor implicit layer entries.
    /// </summary>
    internal static IReadOnlyList<VulkanImplicitLayerEntry> ParseManifestJson(string jsonText, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return Array.Empty<VulkanImplicitLayerEntry>();
        }

        List<VulkanImplicitLayerEntry> results = [];
        using JsonDocument doc = JsonDocument.Parse(
            jsonText,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        if (root.TryGetProperty("layer", out JsonElement singleLayer) && singleLayer.ValueKind == JsonValueKind.Object)
        {
            VulkanImplicitLayerEntry? parsed = ParseLayerElement(singleLayer, manifestPath);
            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        if (root.TryGetProperty("layers", out JsonElement layersArray) && layersArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement layerEl in layersArray.EnumerateArray())
            {
                if (layerEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                VulkanImplicitLayerEntry? parsed = ParseLayerElement(layerEl, manifestPath);
                if (parsed is not null)
                {
                    results.Add(parsed);
                }
            }
        }

        return results;
    }

    /// <summary>
    ///     Applies a computed <see cref="VulkanImplicitLayerShieldPlan" /> to a child process environment dictionary.
    /// </summary>
    public static int ApplyToEnvironment(
        StringDictionary environmentVariables,
        VulkanImplicitLayerShieldPlan plan,
        bool setEmptyInsteadOfRemoveForEnableVars = false)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        ArgumentNullException.ThrowIfNull(plan);

        int injected = 0;
        foreach (KeyValuePair<string, string> kv in plan.DisableEnvironmentVariables)
        {
            environmentVariables[kv.Key] = kv.Value;
            injected++;
        }

        foreach (string enableKey in plan.ClearedEnableEnvironmentVariables)
        {
            if (setEmptyInsteadOfRemoveForEnableVars)
            {
                environmentVariables[enableKey] = string.Empty;
            }
            else
            {
                environmentVariables.Remove(enableKey);
            }
        }

        return injected;
    }

    /// <summary>
    ///     Formats a concise, secret-free diagnostic summary suitable for launcher logs and telemetry.
    /// </summary>
    public static string FormatDiagnosticSummary(VulkanImplicitLayerShieldPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.NeutralizedLayerNames.Count == 0 && plan.DisableEnvironmentVariables.Count == 0)
        {
            return plan.DiscoveredThirdPartyLayerNames.Count > 0
                ? $"Detected {plan.DiscoveredThirdPartyLayerNames.Count} host implicit Vulkan layer(s) ({string.Join(", ", plan.DiscoveredThirdPartyLayerNames)}); third-party overlays explicitly allowed by user."
                : "No third-party host implicit Vulkan layers detected.";
        }

        string layersText = plan.NeutralizedLayerNames.Count > 0
            ? string.Join(", ", plan.NeutralizedLayerNames)
            : "static-fallback-only";
        string envKeysText = string.Join(", ", plan.DisableEnvironmentVariables.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        return $"Neutralized {plan.NeutralizedLayerNames.Count} host implicit Vulkan layer(s) [{layersText}] via {plan.DisableEnvironmentVariables.Count} disable_environment key(s) [{envKeysText}].";
    }

    /// <summary>
    ///     Returns <see langword="true" /> when an environment variable name is a Vulkan layer disable/enable shield key.
    /// </summary>
    public static bool IsVulkanShieldEnvironmentKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.StartsWith("DISABLE_VULKAN_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("DISABLE_VK_LAYER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("DISABLE_RTSS_VULKAN_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ENABLE_VULKAN_RENDERDOC_CAPTURE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKongorOwnedLayer(string layerName)
    {
        return layerName.StartsWith("VK_LAYER_HON_", StringComparison.OrdinalIgnoreCase);
    }

    private static VulkanImplicitLayerEntry? ParseLayerElement(JsonElement layerEl, string manifestPath)
    {
        if (!layerEl.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? layerName = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(layerName) || IsKongorOwnedLayer(layerName))
        {
            return null;
        }

        Dictionary<string, string> disableEnv = ReadStringDictionaryProperty(layerEl, "disable_environment", defaultEmptyValue: "1");
        Dictionary<string, string> enableEnv = ReadStringDictionaryProperty(layerEl, "enable_environment", defaultEmptyValue: "1");

        return new VulkanImplicitLayerEntry(
            LayerName: layerName.Trim(),
            ManifestPath: manifestPath,
            DisableEnvironment: disableEnv,
            EnableEnvironment: enableEnv);
    }

    private static Dictionary<string, string> ReadStringDictionaryProperty(
        JsonElement parent,
        string propertyName,
        string defaultEmptyValue)
    {
        Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty(propertyName, out JsonElement obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name))
            {
                continue;
            }

            string value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? defaultEmptyValue,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => defaultEmptyValue
            };

            dict[prop.Name.Trim()] = string.IsNullOrEmpty(value) ? defaultEmptyValue : value;
        }

        return dict;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VulkanImplicitLayerEntry> DiscoverHostImplicitLayersWindows(Action<string>? log)
    {
        List<VulkanImplicitLayerEntry> results = [];
        HashSet<string> visitedManifestPaths = new(StringComparer.OrdinalIgnoreCase);

        RegistryHive[] hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
        foreach (RegistryHive hive in hives)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? layersKey = baseKey.OpenSubKey(KhronosImplicitLayersRegistryPath, writable: false);
                if (layersKey is null)
                {
                    continue;
                }

                foreach (string manifestPath in layersKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(manifestPath))
                    {
                        continue;
                    }

                    object? rawValue = layersKey.GetValue(manifestPath);
                    if (!IsEnabledRegistryFlag(rawValue))
                    {
                        continue;
                    }

                    if (!visitedManifestPaths.Add(manifestPath))
                    {
                        continue;
                    }

                    if (!File.Exists(manifestPath))
                    {
                        log?.Invoke($"[VULKAN_SHIELD] Skipping missing Khronos implicit layer manifest: '{manifestPath}' (hive={hive}).");
                        continue;
                    }

                    try
                    {
                        string json = File.ReadAllText(manifestPath);
                        IReadOnlyList<VulkanImplicitLayerEntry> parsed = ParseManifestJson(json, manifestPath);
                        foreach (VulkanImplicitLayerEntry entry in parsed)
                        {
                            results.Add(entry);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        log?.Invoke($"[VULKAN_SHIELD] Failed parsing Khronos implicit layer manifest '{manifestPath}' (hive={hive}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                log?.Invoke($"[VULKAN_SHIELD] Registry probe skipped for hive={hive}: {ex.Message}");
            }
        }

        return results;
    }

    private static bool IsEnabledRegistryFlag(object? rawValue)
    {
        return rawValue switch
        {
            int dword => dword == 0,
            long qword => qword == 0,
            string str => string.Equals(str.Trim(), "0", StringComparison.Ordinal),
            _ => false
        };
    }
}
