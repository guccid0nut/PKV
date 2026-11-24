// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Utilities;
using WILLOWMAKER.Core.ViewModels;

namespace WILLOWMAKER.Core.Models;

/// <summary>
///     Maintains canonical 1-to-1 pairings between Master Server endpoints and Content Delivery Network (CDN) endpoints,
///     enforcing bidirectional auto-switching and cross-ecosystem mismatch detection.
/// </summary>
public static class EndpointPairRegistry
{
    public const string MasterServerLocalOfflineAuto = LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer;
    public const string MasterServerOfflineMode = LauncherSettingsModel.OfflineModeSelection;
    public const string MasterServerLocalhost55555 = "localhost:55555";
    public const string MasterServerNewbsIo = "masterserver.newbs.io";
    public const string MasterServerKongorNet = "api.kongor.net";
    public const string MasterServerCustomAddress = "Custom Address ...";

    /// <summary>
    ///     Toggle controlling whether legacy kongor.net endpoints are enabled or greyed out/disabled.
    ///     Defaults to false until kongor.net completes its transition to the PKV v5 protocol.
    ///     Thread/Async-isolated via AsyncLocal to prevent test runner concurrency races.
    /// </summary>
    private static readonly AsyncLocal<bool?> s_scopedKongorNetPkvEnabled = new();
    private static volatile bool s_globalKongorNetPkvEnabled = false;

    public static bool IsKongorNetPkvEnabled
    {
        get => s_scopedKongorNetPkvEnabled.Value ?? s_globalKongorNetPkvEnabled;
        set => s_scopedKongorNetPkvEnabled.Value = value;
    }

    public const string CdnLocalOfflineAuto = "Local / Offline Auto (localhost:55555/cdn)";
    public const string CdnOfflineMode = "Offline Mode (Local Files Only)";
    public const string CdnLocalhost55555 = "localhost:55555/cdn";
    public const string CdnNewbsIo = "cdn.newbs.io";
    public const string CdnNewbsIoServices = "masterserver.newbs.io/cdn";
    public const string CdnKongorNet = "cdn.kongor.net";
    public const string CdnKongorNetServices = "api.kongor.net/cdn";
    public const string CdnCustomAddress = "Custom Address ...";

    /// <summary>
    ///     Builds the complete list of ecosystem CDN selection options in deterministic order so reverse switching
    ///     (CDN -> Master Server) operates cleanly from any state.
    /// </summary>
    public static List<AddressSelectItem> BuildAvailableCdnOptions(
        string? masterServerSelection,
        string? customMasterServerAddress,
        bool isCustomMasterFieldVisible)
    {
        List<AddressSelectItem> options =
        [
            new AddressSelectItem
            {
                DisplayText = CdnLocalOfflineAuto,
                TargetURL = "localhost:55555/cdn",
                TooltipText = "Hosted by the local Master Server (auto-switches to offline files if local server is not running)."
            },
            new AddressSelectItem
            {
                DisplayText = CdnOfflineMode,
                TargetURL = "offline",
                TooltipText = "Local game client files only (zero network requests, bots & practice mode)."
            },
            new AddressSelectItem
            {
                DisplayText = CdnLocalhost55555,
                TargetURL = "localhost:55555/cdn",
                TooltipText = "Hosted by the local Master Server."
            }
        ];

        if (isCustomMasterFieldVisible &&
            !string.IsNullOrWhiteSpace(customMasterServerAddress) &&
            AddressValidation.IsValidAddress(customMasterServerAddress))
        {
            string host = customMasterServerAddress.Trim().TrimEnd('/');
            options.Add(new AddressSelectItem
            {
                DisplayText = $"{host}/cdn",
                TargetURL = $"{host}/cdn",
                TooltipText = $"Hosted by the custom Master Server at {host}."
            });
        }

        options.Add(new AddressSelectItem
        {
            DisplayText = CdnNewbsIo,
            TargetURL = CdnNewbsIo,
            TooltipText = "Hosted by the Newbs community network."
        });

        options.Add(new AddressSelectItem
        {
            DisplayText = CdnNewbsIoServices,
            TargetURL = CdnNewbsIoServices,
            TooltipText = "Hosted by the Newbs community services host as a fallback."
        });

        options.Add(new AddressSelectItem
        {
            DisplayText = CdnKongorNet,
            TargetURL = CdnKongorNet,
            IsEnabled = IsKongorNetPkvEnabled,
            TooltipText = IsKongorNetPkvEnabled
                ? "Hosted on a global mesh network."
                : "Temporarily disabled — awaiting kongor.net transition to PKV v5."
        });

        options.Add(new AddressSelectItem
        {
            DisplayText = CdnKongorNetServices,
            TargetURL = CdnKongorNetServices,
            IsEnabled = IsKongorNetPkvEnabled,
            TooltipText = IsKongorNetPkvEnabled
                ? "Hosted by the Project KONGOR services host as a fallback."
                : "Temporarily disabled — awaiting kongor.net transition to PKV v5."
        });

        options.Add(new AddressSelectItem
        {
            DisplayText = CdnCustomAddress,
            TargetURL = null,
            TooltipText = "Specify a custom CDN endpoint or server."
        });

        return options;
    }

    /// <summary>
    ///     Resolves the canonical CDN option paired with the active Master Server selection.
    /// </summary>
    public static AddressSelectItem ResolvePairedCdnOption(
        IReadOnlyList<AddressSelectItem> options,
        string? masterServerSelection,
        string? customMasterServerAddress,
        bool isCustomMasterFieldVisible)
    {
        if (options.Count == 0)
        {
            return new AddressSelectItem
            {
                DisplayText = CdnLocalOfflineAuto,
                TargetURL = "localhost:55555/cdn",
                TooltipText = "Hosted by the local Master Server."
            };
        }

        if (isCustomMasterFieldVisible ||
            masterServerSelection?.Contains("Custom", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!string.IsNullOrWhiteSpace(customMasterServerAddress) &&
                AddressValidation.IsValidAddress(customMasterServerAddress))
            {
                string customTarget = $"{customMasterServerAddress.Trim().TrimEnd('/')}/cdn";
                AddressSelectItem? match = options.FirstOrDefault(o =>
                    string.Equals(o.DisplayText, customTarget, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.TargetURL, customTarget, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match;
            }

            AddressSelectItem? customItem = options.FirstOrDefault(o => o.IsCustom);
            if (customItem is not null)
                return customItem;
        }

        string raw = masterServerSelection?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return options.FirstOrDefault(o => o.DisplayText.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        if (raw.Equals(LauncherSettingsModel.OfflineModeSelection, StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("Offline Mode", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("offline", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(o => string.Equals(o.TargetURL, "offline", StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        if (raw.Equals(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer, StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("local-auto", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(o => o.DisplayText.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        if (raw.Equals("localhost:55555", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("127.0.0.1:55555", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(o => !o.DisplayText.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase) &&
                                               (string.Equals(o.DisplayText, "localhost:55555/cdn", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(o.TargetURL, "localhost:55555/cdn", StringComparison.OrdinalIgnoreCase))) ?? options[0];
        }

        if (raw.Contains("newbs.io", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(o => string.Equals(o.TargetURL, CdnNewbsIo, StringComparison.OrdinalIgnoreCase) ||
                                               o.DisplayText.Contains(CdnNewbsIo, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        if (raw.Contains("kongor.net", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsKongorNetPkvEnabled)
            {
                return options.FirstOrDefault(o => string.Equals(o.TargetURL, CdnNewbsIo, StringComparison.OrdinalIgnoreCase) ||
                                                   o.DisplayText.Contains(CdnNewbsIo, StringComparison.OrdinalIgnoreCase)) ?? options[0];
            }

            return options.FirstOrDefault(o => string.Equals(o.TargetURL, CdnKongorNet, StringComparison.OrdinalIgnoreCase) ||
                                               o.DisplayText.Contains(CdnKongorNet, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }

        if (AddressValidation.IsValidAddress(raw))
        {
            string target = $"{raw.Trim().TrimEnd('/')}/cdn";
            AddressSelectItem? match = options.FirstOrDefault(o =>
                string.Equals(o.DisplayText, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.TargetURL, target, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return options.FirstOrDefault(o => o.DisplayText.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase)) ?? options[0];
    }

    /// <summary>
    ///     Resolves the canonical Master Server selection text for a given CDN dropdown selection item.
    /// </summary>
    public static string ResolvePairedMasterServerSelection(AddressSelectItem? cdnItem)
    {
        if (cdnItem is null)
            return MasterServerLocalOfflineAuto;

        if (cdnItem.IsCustom || cdnItem.DisplayText.Contains("Custom", StringComparison.OrdinalIgnoreCase))
            return MasterServerCustomAddress;

        if (string.Equals(cdnItem.TargetURL, "offline", StringComparison.OrdinalIgnoreCase) ||
            cdnItem.DisplayText.Contains("Offline Mode", StringComparison.OrdinalIgnoreCase))
        {
            return MasterServerOfflineMode;
        }

        if (cdnItem.DisplayText.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase))
        {
            return MasterServerLocalOfflineAuto;
        }

        if (string.Equals(cdnItem.DisplayText, "localhost:55555/cdn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cdnItem.TargetURL, "localhost:55555/cdn", StringComparison.OrdinalIgnoreCase))
        {
            return MasterServerLocalhost55555;
        }

        if (cdnItem.TargetURL?.Contains("newbs.io", StringComparison.OrdinalIgnoreCase) == true ||
            cdnItem.DisplayText.Contains("newbs.io", StringComparison.OrdinalIgnoreCase))
        {
            return MasterServerNewbsIo;
        }

        if (cdnItem.TargetURL?.Contains("kongor.net", StringComparison.OrdinalIgnoreCase) == true ||
            cdnItem.DisplayText.Contains("kongor.net", StringComparison.OrdinalIgnoreCase))
        {
            return MasterServerKongorNet;
        }

        return MasterServerCustomAddress;
    }

    /// <summary>
    ///     Checks whether a saved master server and CDN selection represent a cross-ecosystem mismatch
    ///     requiring migration to the canonical paired option.
    /// </summary>
    public static bool IsCrossEcosystemMismatch(string? masterServerSelection, string? savedCdnDisplayText)
    {
        if (string.IsNullOrWhiteSpace(masterServerSelection) || string.IsNullOrWhiteSpace(savedCdnDisplayText))
            return false;

        string ms = masterServerSelection.Trim();
        string cdn = savedCdnDisplayText.Trim();

        // Explicit Custom overrides are never considered a mismatch
        if (ms.Contains("Custom", StringComparison.OrdinalIgnoreCase) ||
            cdn.Contains("Custom", StringComparison.OrdinalIgnoreCase) ||
            cdn.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool msIsNewbs = ms.Contains("newbs.io", StringComparison.OrdinalIgnoreCase);
        bool cdnIsNewbs = cdn.Contains("newbs.io", StringComparison.OrdinalIgnoreCase);

        bool msIsKongor = ms.Contains("kongor.net", StringComparison.OrdinalIgnoreCase);
        bool cdnIsKongor = cdn.Contains("kongor.net", StringComparison.OrdinalIgnoreCase);

        if (!IsKongorNetPkvEnabled && (msIsKongor || cdnIsKongor))
        {
            return true;
        }

        bool msIsOffline = ms.Equals(LauncherSettingsModel.OfflineModeSelection, StringComparison.OrdinalIgnoreCase) ||
                           ms.StartsWith("Offline Mode", StringComparison.OrdinalIgnoreCase) ||
                           ms.Equals("offline", StringComparison.OrdinalIgnoreCase);
        bool cdnIsOffline = cdn.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
                            cdn.Contains("Offline Mode", StringComparison.OrdinalIgnoreCase);

        bool msIsLocalAuto = ms.Equals(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer, StringComparison.OrdinalIgnoreCase) ||
                             ms.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase) ||
                             ms.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                             ms.Equals("local-auto", StringComparison.OrdinalIgnoreCase);
        bool cdnIsLocalAuto = cdn.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase);

        bool msIsLocalhost = ms.Equals("localhost:55555", StringComparison.OrdinalIgnoreCase) ||
                             ms.Equals("127.0.0.1:55555", StringComparison.OrdinalIgnoreCase);
        bool cdnIsLocalhost = !cdnIsLocalAuto &&
                              (cdn.Equals("localhost:55555/cdn", StringComparison.OrdinalIgnoreCase) ||
                               cdn.Equals("127.0.0.1:55555/cdn", StringComparison.OrdinalIgnoreCase));

        if (msIsNewbs)
        {
            return !cdnIsNewbs;
        }

        if (msIsKongor)
        {
            return !cdnIsKongor;
        }

        if (msIsOffline)
        {
            return !cdnIsOffline;
        }

        if (msIsLocalAuto)
        {
            return !(cdnIsLocalAuto || cdnIsLocalhost);
        }

        if (msIsLocalhost)
        {
            return !(cdnIsLocalhost || cdnIsLocalAuto);
        }

        return false;
    }

    /// <summary>
    ///     Resolves the fallback CDN base URL for a master server when no explicit target is supplied.
    /// </summary>
    public static string ResolveDefaultCdnUrlForMasterServer(string? masterServerSelection, string? builtMasterServerAddress)
    {
        string s = (masterServerSelection ?? string.Empty).Trim();
        string b = (builtMasterServerAddress ?? string.Empty).Trim();
        string combined = $"{s} {b}";

        if (combined.Contains("newbs.io", StringComparison.OrdinalIgnoreCase))
            return "https://cdn.newbs.io/";

        if (combined.Contains("kongor.net", StringComparison.OrdinalIgnoreCase))
            return IsKongorNetPkvEnabled ? "https://cdn.kongor.net/" : "https://cdn.newbs.io/";

        if (MainViewModel.IsOfflineModeSelection(s) ||
            MainViewModel.IsLocalOfflineAutoSelection(s) ||
            combined.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("::1", StringComparison.OrdinalIgnoreCase))
        {
            return "http://localhost:55555/cdn/";
        }

        if (!string.IsNullOrWhiteSpace(builtMasterServerAddress) && AddressValidation.IsValidAddress(builtMasterServerAddress))
        {
            string normalised = AddressValidation.NormaliseCDNURL(builtMasterServerAddress).TrimEnd('/');
            return $"{normalised}/cdn/";
        }

        return "http://localhost:55555/cdn/";
    }
}
