// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.Configuration.Matchmaking;
using KONGOR.MasterServer.Serialization;

namespace KONGOR.MasterServer.Configuration;

public static class JSONConfiguration
{
    private static readonly string BasePath =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "Configuration");

    private static readonly string EconomyConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Economy", "EconomyConfiguration.json"));

    private static readonly string MatchmakingConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Matchmaking", "MatchmakingConfiguration.json"));

    private static readonly string MasteryRewardsConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Mastery", "MasteryRewardsConfiguration.json"));

    private static readonly string MasteryCouponsConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Mastery", "MasteryCouponsConfiguration.json"));

    private static readonly string StoreItemsConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Store", "StoreItemsConfiguration.json"));

    private static readonly string DailySpecialsConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Store", "DailySpecialsConfiguration.json"));

    private static readonly string FeaturedItemsConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Store", "FeaturedItemsConfiguration.json"));

    public static readonly EconomyConfiguration EconomyConfiguration =
        JsonSerializer.Deserialize(EconomyConfigurationJSON, MasterServerJsonContext.Default.EconomyConfiguration)
        ?? throw new NullReferenceException("Economy Configuration Is NULL");

    public static readonly MasteryRewardsConfiguration MasteryRewardsConfiguration =
        JsonSerializer.Deserialize(MasteryRewardsConfigurationJSON, MasterServerJsonContext.Default.MasteryRewardsConfiguration)
        ?? throw new NullReferenceException("Mastery Rewards Configuration Is NULL");

    public static readonly List<MasteryCoupon> MasteryCoupons =
        JsonSerializer.Deserialize(MasteryCouponsConfigurationJSON, MasterServerJsonContext.Default.ListMasteryCoupon)
        ?? throw new NullReferenceException("Mastery Coupons Configuration Is NULL");

    private static readonly Lock _storeItemsLock = new();
    private static StoreItemsConfiguration _storeItemsConfiguration = LoadStoreItemsConfiguration();
    private static readonly FileSystemWatcher? _storeWatcher;

    public static readonly DailySpecialsConfiguration DailySpecialsConfiguration =
        JsonSerializer.Deserialize(DailySpecialsConfigurationJSON, MasterServerJsonContext.Default.DailySpecialsConfiguration)
        ?? throw new NullReferenceException("Daily Specials Configuration Is NULL");

    public static readonly FeaturedItemsConfiguration FeaturedItemsConfiguration =
        JsonSerializer.Deserialize(FeaturedItemsConfigurationJSON, MasterServerJsonContext.Default.FeaturedItemsConfiguration)
        ?? throw new NullReferenceException("Featured Items Configuration Is NULL");

    private static readonly string PlinkoConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Plinko", "PlinkoConfiguration.json"));

    private static readonly string PlinkoTierProductsConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Plinko", "PlinkoTierProducts.json"));

    private static readonly string TicketExchangeConfigurationJSON =
        File.ReadAllText(Path.Combine(BasePath, "Plinko", "TicketExchangeConfiguration.json"));

    public static readonly PlinkoConfiguration PlinkoConfiguration = LoadPlinkoConfiguration();

    public static readonly PlinkoTierProductsConfiguration PlinkoTierProductsConfiguration =
        LoadPlinkoTierProductsConfiguration();

    public static readonly TicketExchangeConfiguration TicketExchangeConfiguration = LoadTicketExchangeConfiguration();

    private static Timer? _storeDebounceTimer;

    static JSONConfiguration()
    {
        if (JsonSerializer.Deserialize(MatchmakingConfigurationJSON, MasterServerJsonContext.Default.MatchmakingConfiguration) is { } loadedConfig)
            SharedConfiguration.MatchmakingConfiguration = loadedConfig;

        try
        {
            string? storePath = ResolveStoreItemsJsonPath();
            if (!string.IsNullOrEmpty(storePath))
            {
                string? dir = Path.GetDirectoryName(storePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _storeWatcher = new FileSystemWatcher(dir, "StoreItemsConfiguration.json")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };
                    _storeWatcher.Changed += (_, _) => ScheduleReloadStoreItemsConfiguration();
                    _storeWatcher.Created += (_, _) => ScheduleReloadStoreItemsConfiguration();
                }
            }
        }
        catch
        {
            // Ignored if file watcher permissions are restricted
        }
    }

    public static StoreItemsConfiguration StoreItemsConfiguration
    {
        get
        {
            lock (_storeItemsLock)
            {
                return _storeItemsConfiguration;
            }
        }
        set
        {
            lock (_storeItemsLock)
            {
                _storeItemsConfiguration = value;
            }
        }
    }

    public static MatchmakingConfiguration MatchmakingConfiguration
    {
        get => SharedConfiguration.MatchmakingConfiguration;
        set => SharedConfiguration.MatchmakingConfiguration = value;
    }

    private static void ScheduleReloadStoreItemsConfiguration()
    {
        lock (_storeItemsLock)
        {
            _storeDebounceTimer?.Dispose();
            _storeDebounceTimer = new Timer(_ => ReloadStoreItemsConfiguration(), null, 100, Timeout.Infinite);
        }
    }

    public static void ReloadStoreItemsConfiguration()
    {
        try
        {
            StoreItemsConfiguration loaded = LoadStoreItemsConfiguration();
            lock (_storeItemsLock)
            {
                _storeItemsConfiguration = loaded;
            }
        }
        catch
        {
            // Resilient fallback - never crash background threads
        }
    }

    private static StoreItemsConfiguration LoadStoreItemsConfiguration()
    {
        try
        {
            string? resolvedPath = ResolveStoreItemsJsonPath();
            if (resolvedPath != null && File.Exists(resolvedPath))
            {
                string? json = SafeReadAllText(resolvedPath);
                if (!string.IsNullOrEmpty(json))
                {
                    List<StoreItem>? items = JsonSerializer.Deserialize(json, MasterServerJsonContext.Default.ListStoreItem);
                    if (items is { Count: > 0 }) return new StoreItemsConfiguration(items);
                }
            }
        }
        catch
        {
            // Fallback to embedded json
        }

        return new StoreItemsConfiguration(
            JsonSerializer.Deserialize(StoreItemsConfigurationJSON, MasterServerJsonContext.Default.ListStoreItem)
            ?? throw new NullReferenceException("Store Items Configuration Is NULL"));
    }

    private static string? SafeReadAllText(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
#pragma warning disable RS0030 // Justification: File lock retry backoff in synchronous helper
                Task.Delay(50).GetAwaiter().GetResult();
#pragma warning restore RS0030
            }
            catch
            {
                return null;
            }

        return null;
    }

    private static string? ResolveStoreItemsJsonPath()
    {
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? current = new(baseDir);

        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "KONGOR.MasterServer", "Configuration", "Store",
                "StoreItemsConfiguration.json");
            if (File.Exists(candidate)) return candidate;

            current = current.Parent;
        }

        string directPath = Path.Combine(BasePath, "Store", "StoreItemsConfiguration.json");
        return File.Exists(directPath) ? directPath : null;
    }

    private static PlinkoConfiguration LoadPlinkoConfiguration()
    {
        PlinkoConfiguration configuration = JsonSerializer.Deserialize(PlinkoConfigurationJSON, MasterServerJsonContext.Default.PlinkoConfiguration)
                                            ?? throw new NullReferenceException("Plinko Configuration Is NULL");

        configuration.Validate();

        return configuration;
    }

    private static PlinkoTierProductsConfiguration LoadPlinkoTierProductsConfiguration()
    {
        List<PlinkoTierProducts> tiers =
            JsonSerializer.Deserialize(PlinkoTierProductsConfigurationJSON, MasterServerJsonContext.Default.ListPlinkoTierProducts)
            ?? throw new NullReferenceException("Plinko Tier Products Configuration Is NULL");

        PlinkoTierProductsConfiguration configuration = new() { Tiers = tiers };

        configuration.Initialise();

        return configuration;
    }

    private static TicketExchangeConfiguration LoadTicketExchangeConfiguration()
    {
        TicketExchangeConfiguration configuration =
            JsonSerializer.Deserialize(TicketExchangeConfigurationJSON, MasterServerJsonContext.Default.TicketExchangeConfiguration)
            ?? throw new NullReferenceException("Ticket Exchange Configuration Is NULL");

        configuration.Initialise();

        return configuration;
    }
}
