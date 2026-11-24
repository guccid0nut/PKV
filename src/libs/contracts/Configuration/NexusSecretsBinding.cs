// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;

namespace NEXUS.Contracts.Configuration;

/// <summary>
///     Explicit (reflection-free) bridge between <see cref="NexusSettingRegistry" /> descriptor ids and
///     <see cref="NexusSecrets" /> properties. Every value travels as an invariant string; ints and bools are parsed on
///     the way in. A unit test guarantees every registry entry has a binding and vice versa.
/// </summary>
public static class NexusSecretsBinding
{
    public sealed record Accessor(Func<NexusSecrets, string> Get, Action<NexusSecrets, string> Set);

    public static IReadOnlyDictionary<string, Accessor> Accessors { get; } = new Dictionary<string, Accessor>
    {
        // Master Server
        [nameof(NexusSecrets.MasterServerAddress)] = Str(s => s.MasterServerAddress, (s, v) => s.MasterServerAddress = v),
        [nameof(NexusSecrets.InfrastructureGateway)] = Str(s => s.InfrastructureGateway, (s, v) => s.InfrastructureGateway = v),
        [nameof(NexusSecrets.MasterServerPort)] = Int(s => s.MasterServerPort, (s, v) => s.MasterServerPort = v),

        // Network
        [nameof(NexusSecrets.ChatServerHost)] = Str(s => s.ChatServerHost, (s, v) => s.ChatServerHost = v),
        [nameof(NexusSecrets.ChatServerPublicHost)] = Str(s => s.ChatServerPublicHost, (s, v) => s.ChatServerPublicHost = v),
        [nameof(NexusSecrets.ChatServerClientPort)] = Int(s => s.ChatServerClientPort, (s, v) => s.ChatServerClientPort = v),
        [nameof(NexusSecrets.ChatServerPublicClientPort)] = Int(s => s.ChatServerPublicClientPort, (s, v) => s.ChatServerPublicClientPort = v),
        [nameof(NexusSecrets.ChatServerMatchServerPort)] = Int(s => s.ChatServerMatchServerPort, (s, v) => s.ChatServerMatchServerPort = v),
        [nameof(NexusSecrets.ChatServerMatchServerManagerPort)] = Int(s => s.ChatServerMatchServerManagerPort, (s, v) => s.ChatServerMatchServerManagerPort = v),
        [nameof(NexusSecrets.WebPortalHttpPort)] = Int(s => s.WebPortalHttpPort, (s, v) => s.WebPortalHttpPort = v),
        [nameof(NexusSecrets.WebPortalHttpsPort)] = Int(s => s.WebPortalHttpsPort, (s, v) => s.WebPortalHttpsPort = v),
        [nameof(NexusSecrets.WebPortalApiHttpPort)] = Int(s => s.WebPortalApiHttpPort, (s, v) => s.WebPortalApiHttpPort = v),
        [nameof(NexusSecrets.WebPortalApiHttpsPort)] = Int(s => s.WebPortalApiHttpsPort, (s, v) => s.WebPortalApiHttpsPort = v),
        [nameof(NexusSecrets.DatabaseContextPort)] = Int(s => s.DatabaseContextPort, (s, v) => s.DatabaseContextPort = v),
        [nameof(NexusSecrets.DistributedCachePort)] = Int(s => s.DistributedCachePort, (s, v) => s.DistributedCachePort = v),
        [nameof(NexusSecrets.DistributedCacheSecondaryPort)] = Int(s => s.DistributedCacheSecondaryPort, (s, v) => s.DistributedCacheSecondaryPort = v),
        [nameof(NexusSecrets.DistributedCacheDashboardPort)] = Int(s => s.DistributedCacheDashboardPort, (s, v) => s.DistributedCacheDashboardPort = v),
        [nameof(NexusSecrets.MatchmakingPlayersPerTeam)] = Int(s => s.MatchmakingPlayersPerTeam, (s, v) => s.MatchmakingPlayersPerTeam = v),
        [nameof(NexusSecrets.MatchmakingIntervalMs)] = Int(s => s.MatchmakingIntervalMs, (s, v) => s.MatchmakingIntervalMs = v),
        [nameof(NexusSecrets.CompelPingPort)] = Int(s => s.CompelPingPort, (s, v) => s.CompelPingPort = v),
        [nameof(NexusSecrets.GameServerPublicIp)] = Str(s => s.GameServerPublicIp, (s, v) => s.GameServerPublicIp = v),
        [nameof(NexusSecrets.GameServerInstancePorts)] = Str(s => s.GameServerInstancePorts, (s, v) => s.GameServerInstancePorts = v),
        [nameof(NexusSecrets.PyroscopePort)] = Int(s => s.PyroscopePort, (s, v) => s.PyroscopePort = v),

        // OAuth
        [nameof(NexusSecrets.DiscordClientId)] = Str(s => s.DiscordClientId, (s, v) => s.DiscordClientId = v),
        [nameof(NexusSecrets.DiscordClientSecret)] = Str(s => s.DiscordClientSecret, (s, v) => s.DiscordClientSecret = v),
        [nameof(NexusSecrets.TwitchClientId)] = Str(s => s.TwitchClientId, (s, v) => s.TwitchClientId = v),
        [nameof(NexusSecrets.TwitchClientSecret)] = Str(s => s.TwitchClientSecret, (s, v) => s.TwitchClientSecret = v),

        // Database
        [nameof(NexusSecrets.DatabaseProvider)] = Str(s => s.DatabaseProvider, (s, v) => s.DatabaseProvider = v),
        [nameof(NexusSecrets.DatabasePassword)] = Str(s => s.DatabasePassword, (s, v) => s.DatabasePassword = v),
        [nameof(NexusSecrets.DistributedCachePassword)] = Str(s => s.DistributedCachePassword, (s, v) => s.DistributedCachePassword = v),

        // Supabase
        [nameof(NexusSecrets.SupabaseUrl)] = Str(s => s.SupabaseUrl, (s, v) => s.SupabaseUrl = v),
        [nameof(NexusSecrets.SupabaseAnonKey)] = Str(s => s.SupabaseAnonKey, (s, v) => s.SupabaseAnonKey = v),
        [nameof(NexusSecrets.SupabaseServiceRoleKey)] = Str(s => s.SupabaseServiceRoleKey, (s, v) => s.SupabaseServiceRoleKey = v),
        [nameof(NexusSecrets.SupabaseDatabaseConnectionString)] = Str(s => s.SupabaseDatabaseConnectionString, (s, v) => s.SupabaseDatabaseConnectionString = v),
        [nameof(NexusSecrets.SupabaseStorageBucket)] = Str(s => s.SupabaseStorageBucket, (s, v) => s.SupabaseStorageBucket = v),
        [nameof(NexusSecrets.SupabaseS3AccessKey)] = Str(s => s.SupabaseS3AccessKey, (s, v) => s.SupabaseS3AccessKey = v),
        [nameof(NexusSecrets.SupabaseS3SecretKey)] = Str(s => s.SupabaseS3SecretKey, (s, v) => s.SupabaseS3SecretKey = v),
        [nameof(NexusSecrets.SupabaseS3Region)] = Str(s => s.SupabaseS3Region, (s, v) => s.SupabaseS3Region = v),
        [nameof(NexusSecrets.SupabaseS3Endpoint)] = Str(s => s.SupabaseS3Endpoint, (s, v) => s.SupabaseS3Endpoint = v),
        [nameof(NexusSecrets.LakehouseBucket)] = Str(s => s.LakehouseBucket, (s, v) => s.LakehouseBucket = v),

        // Security
        [nameof(NexusSecrets.JwtSigningKey)] = Str(s => s.JwtSigningKey, (s, v) => s.JwtSigningKey = v),
        [nameof(NexusSecrets.InternalServiceKey)] = Str(s => s.InternalServiceKey, (s, v) => s.InternalServiceKey = v),
        [nameof(NexusSecrets.IpcAuthKey)] = Str(s => s.IpcAuthKey, (s, v) => s.IpcAuthKey = v),
        [nameof(NexusSecrets.SendGridApiKey)] = Str(s => s.SendGridApiKey, (s, v) => s.SendGridApiKey = v),

        // Paths
        [nameof(NexusSecrets.ProjectPath)] = Str(s => s.ProjectPath, (s, v) => s.ProjectPath = v),
        [nameof(NexusSecrets.CompelPath)] = Str(s => s.CompelPath, (s, v) => s.CompelPath = v),
        [nameof(NexusSecrets.GameClientPath)] = Str(s => s.GameClientPath, (s, v) => s.GameClientPath = v),

        // COMPEL & Host Account
        [nameof(NexusSecrets.CompelAuthenticationToken)] = Str(s => s.CompelAuthenticationToken, (s, v) => s.CompelAuthenticationToken = v),
        [nameof(NexusSecrets.HostAccountUserName)] = Str(s => s.HostAccountUserName, (s, v) => s.HostAccountUserName = v),
        [nameof(NexusSecrets.HostAccountPassword)] = Str(s => s.HostAccountPassword, (s, v) => s.HostAccountPassword = v),
        [nameof(NexusSecrets.CompelGateway)] = Str(s => s.CompelGateway, (s, v) => s.CompelGateway = v),
        [nameof(NexusSecrets.CompelInstances)] = Int(s => s.CompelInstances, (s, v) => s.CompelInstances = v),
        [nameof(NexusSecrets.CompelPriorityMode)] = Str(s => s.CompelPriorityMode, (s, v) => s.CompelPriorityMode = v),
        [nameof(NexusSecrets.ContainerRuntimeTarget)] = Str(s => s.ContainerRuntimeTarget, (s, v) => s.ContainerRuntimeTarget = v),
        [nameof(NexusSecrets.CompelServerNamePrefix)] = Str(s => s.CompelServerNamePrefix, (s, v) => s.CompelServerNamePrefix = v),
        [nameof(NexusSecrets.CompelLocation)] = Str(s => s.CompelLocation, (s, v) => s.CompelLocation = v),
        [nameof(NexusSecrets.CompelInstallationDirectory)] = Str(s => s.CompelInstallationDirectory, (s, v) => s.CompelInstallationDirectory = v),
        [nameof(NexusSecrets.CompelControlPlanePort)] = Int(s => s.CompelControlPlanePort, (s, v) => s.CompelControlPlanePort = v),
        [nameof(NexusSecrets.CompelPortRangeOffset)] = Int(s => s.CompelPortRangeOffset, (s, v) => s.CompelPortRangeOffset = v),
        [nameof(NexusSecrets.CompelUseProxy)] = Str(s => s.CompelUseProxy, (s, v) => s.CompelUseProxy = NormaliseTriState(v)),
        [nameof(NexusSecrets.CompelCdnSynchronisation)] = Str(s => s.CompelCdnSynchronisation, (s, v) => s.CompelCdnSynchronisation = NormaliseTriState(v)),

        // Replay Storage
        [nameof(NexusSecrets.ReplayStorageProvider)] = Str(s => s.ReplayStorageProvider, (s, v) => s.ReplayStorageProvider = v),
        [nameof(NexusSecrets.LocalReplayDirectory)] = Str(s => s.LocalReplayDirectory, (s, v) => s.LocalReplayDirectory = v),
        [nameof(NexusSecrets.CloudBucketName)] = Str(s => s.CloudBucketName, (s, v) => s.CloudBucketName = v),
        [nameof(NexusSecrets.CloudAccessKey)] = Str(s => s.CloudAccessKey, (s, v) => s.CloudAccessKey = v),
        [nameof(NexusSecrets.CloudSecretKey)] = Str(s => s.CloudSecretKey, (s, v) => s.CloudSecretKey = v),
        [nameof(NexusSecrets.CloudServiceUrl)] = Str(s => s.CloudServiceUrl, (s, v) => s.CloudServiceUrl = v),
        [nameof(NexusSecrets.CloudRegion)] = Str(s => s.CloudRegion, (s, v) => s.CloudRegion = v),

        // Observability
        [nameof(NexusSecrets.GrafanaOtlpEndpoint)] = Str(s => s.GrafanaOtlpEndpoint, (s, v) => s.GrafanaOtlpEndpoint = v),
        [nameof(NexusSecrets.GrafanaOtlpHeaders)] = Str(s => s.GrafanaOtlpHeaders, (s, v) => s.GrafanaOtlpHeaders = v),
        [nameof(NexusSecrets.PyroscopeServerAddress)] = Str(s => s.PyroscopeServerAddress, (s, v) => s.PyroscopeServerAddress = v),
        [nameof(NexusSecrets.PyroscopeBasicAuthUser)] = Str(s => s.PyroscopeBasicAuthUser, (s, v) => s.PyroscopeBasicAuthUser = v),
        [nameof(NexusSecrets.PyroscopeBasicAuthPassword)] = Str(s => s.PyroscopeBasicAuthPassword, (s, v) => s.PyroscopeBasicAuthPassword = v),

        // Alerts
        [nameof(NexusSecrets.AlertWebhookUrl)] = Str(s => s.AlertWebhookUrl, (s, v) => s.AlertWebhookUrl = v),

        // Store
        [nameof(NexusSecrets.UnlockAllItems)] = Bool(s => s.UnlockAllItems, (s, v) => s.UnlockAllItems = v),

        // Queue Arcade
        [nameof(NexusSecrets.QueueArcadeEnabled)] = Bool(s => s.QueueArcadeEnabled, (s, v) => s.QueueArcadeEnabled = v),
        [nameof(NexusSecrets.QueueArcadeInstances)] = Int(s => s.QueueArcadeInstances, (s, v) => s.QueueArcadeInstances = v)
    };

    public static string GetValue(NexusSecrets secrets, NexusSettingDescriptor descriptor)
    {
        return Accessors[descriptor.Id].Get(secrets);
    }

    /// <summary>Applies <paramref name="value" />; unparsable ints/bools are ignored (the model default stays).</summary>
    public static void SetValue(NexusSecrets secrets, NexusSettingDescriptor descriptor, string value)
    {
        Accessors[descriptor.Id].Set(secrets, value);
    }

    private static Accessor Str(Func<NexusSecrets, string> get, Action<NexusSecrets, string> set)
    {
        return new Accessor(s => get(s) ?? string.Empty, set);
    }

    private static Accessor Int(Func<NexusSecrets, int> get, Action<NexusSecrets, int> set)
    {
        return new Accessor(
            s => get(s).ToString(CultureInfo.InvariantCulture),
            (s, v) =>
            {
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) set(s, parsed);
            });
    }

    private static Accessor Bool(Func<NexusSecrets, bool> get, Action<NexusSecrets, bool> set)
    {
        return new Accessor(
            s => get(s) ? "true" : "false",
            (s, v) =>
            {
                if (bool.TryParse(v, out bool parsed)) set(s, parsed);
            });
    }

    private static string NormaliseTriState(string value)
    {
        return bool.TryParse(value, out bool parsed) ? (parsed ? "true" : "false") : string.Empty;
    }
}
