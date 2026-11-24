// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Configuration;

/// <summary>
///     Canonical shared operator secrets and cluster configuration model consumed by
///     NEXUS.ControlPanel.GUI, COMPEL.GUI, and infrastructure tooling.
/// </summary>
public sealed class NexusSecrets
{
    /// <summary>Shallow copy (all members are strings, ints or bools, so this is a full value copy).</summary>
    public NexusSecrets Clone()
    {
        return (NexusSecrets)MemberwiseClone();
    }

    // Master Server & Infrastructure Gateway (SRP / API / Patch)
    public string MasterServerAddress { get; set; } = "http://localhost:55555";
    public string InfrastructureGateway { get; set; } = string.Empty;
    public int MasterServerPort { get; set; } = 55555;

    // Chat Server Ports & Bindings (TRANSMUTANSTEIN)
    public string ChatServerHost { get; set; } = "127.0.0.1";
    public string ChatServerPublicHost { get; set; } = "127.0.0.1";
    public int ChatServerClientPort { get; set; } = 11031;
    public int ChatServerPublicClientPort { get; set; } = 11031;
    public int ChatServerMatchServerPort { get; set; } = 11032;
    public int ChatServerMatchServerManagerPort { get; set; } = 11033;

    // Web Portal & API Microservice Ports
    public int WebPortalHttpPort { get; set; } = 5266;
    public int WebPortalHttpsPort { get; set; } = 5557;
    public int WebPortalApiHttpPort { get; set; } = 5200;
    public int WebPortalApiHttpsPort { get; set; } = 5201;
    public int DatabaseContextPort { get; set; } = 5553;

    // Distributed Cache & Dashboard Ports (Valkey / Redis)
    public int DistributedCachePort { get; set; } = 28298;
    public int DistributedCacheSecondaryPort { get; set; } = 28299;
    public int DistributedCacheDashboardPort { get; set; } = 28300;

    // Matchmaking Engine Settings
    public int MatchmakingPlayersPerTeam { get; set; } = 1; // 1 for Solo Dev / Testing, 5 for Standard HoN
    public int MatchmakingIntervalMs { get; set; } = 5000;

    // Game Server Fleet & COMPEL Ports
    public int CompelPingPort { get; set; } = 11234;
    public string GameServerPublicIp { get; set; } = "127.0.0.1";
    public string GameServerInstancePorts { get; set; } = "11235,11236,11237,11238";

    // Discord OAuth2 (Primary Web Portal Login)
    public string DiscordClientId { get; set; } = string.Empty;
    public string DiscordClientSecret { get; set; } = string.Empty;

    // Twitch OAuth2 & Streamer Integration
    public string TwitchClientId { get; set; } = string.Empty;
    public string TwitchClientSecret { get; set; } = string.Empty;

    // Database Provider & Connection Mode
    public string DatabaseProvider { get; set; } = "PostgreSQL"; // "PostgreSQL", "Supabase", "SqlServer" (legacy)

    // Infrastructure Passwords
    public string DatabasePassword { get; set; } = "MerrickDatabasePassword123!";
    public string DistributedCachePassword { get; set; } = "DistributedCachePassword123!";

    // Supabase Cloud & PostgreSQL Database Integration
    public string SupabaseUrl { get; set; } = string.Empty;
    public string SupabaseAnonKey { get; set; } = string.Empty;
    public string SupabaseServiceRoleKey { get; set; } = string.Empty;
    public string SupabaseDatabaseConnectionString { get; set; } = string.Empty;
    public string SupabaseStorageBucket { get; set; } = "replays";
    public string SupabaseS3AccessKey { get; set; } = string.Empty;
    public string SupabaseS3SecretKey { get; set; } = string.Empty;
    public string SupabaseS3Region { get; set; } = "us-east-1";

    // Blank = derived from SupabaseUrl + "/storage/v1/s3"
    public string SupabaseS3Endpoint { get; set; } = string.Empty;

    // Bucket the COMPEL match lakehouse uploader writes Parquet files into
    public string LakehouseBucket { get; set; } = "match-analytics";

    // Security & Auth Keys
    public string JwtSigningKey { get; set; } = "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!";

    // Blank = services fall back to SecurityConstants.DefaultInternalServiceKey (development only)
    public string InternalServiceKey { get; set; } = string.Empty;
    public string IpcAuthKey { get; set; } = string.Empty;
    public string SendGridApiKey { get; set; } = string.Empty;

    // Operational Alerts
    public string AlertWebhookUrl { get; set; } = string.Empty;

    // Local Game & COMPEL Paths and Host Credentials
    public string ProjectPath { get; set; } = string.Empty;
    public string CompelPath { get; set; } = string.Empty;
    public string GameClientPath { get; set; } = string.Empty;
    public string CompelAuthenticationToken { get; set; } = string.Empty;
    public string HostAccountUserName { get; set; } = "HOST";
    public string HostAccountPassword { get; set; } = string.Empty;
    public string CompelGateway { get; set; } = "localhost";
    public int CompelInstances { get; set; } = 2;
    public string CompelServerNamePrefix { get; set; } = "KONGOR ARENA";
    public string CompelLocation { get; set; } = "USE";
    public string CompelPriorityMode { get; set; } = "Background";
    public int CompelPortRangeOffset { get; set; } = 0;

    // 0 = COMPEL default (11234 + port range offset)
    public int CompelControlPlanePort { get; set; } = 0;
    public string ContainerRuntimeTarget { get; set; } = "Docker";
    public string CompelInstallationDirectory { get; set; } = string.Empty;

    // Tri-state: "" = COMPEL.json / built-in default, "true", "false"
    public string CompelUseProxy { get; set; } = string.Empty;
    public string CompelCdnSynchronisation { get; set; } = string.Empty;

    // Replay Storage
    public string ReplayStorageProvider { get; set; } = "LocalDisk"; // "LocalDisk", "Supabase", "Cloud", "S3"
    public string LocalReplayDirectory { get; set; } = string.Empty;
    public string CloudBucketName { get; set; } = string.Empty;
    public string CloudAccessKey { get; set; } = string.Empty;
    public string CloudSecretKey { get; set; } = string.Empty;
    public string CloudServiceUrl { get; set; } = string.Empty;
    public string CloudRegion { get; set; } = "us-east-1";

    // Grafana Cloud & Pyroscope Profiling
    public string GrafanaOtlpEndpoint { get; set; } = string.Empty;
    public string GrafanaOtlpHeaders { get; set; } = string.Empty;
    public int PyroscopePort { get; set; } = 4040;
    public string PyroscopeServerAddress { get; set; } = string.Empty;
    public string PyroscopeBasicAuthUser { get; set; } = string.Empty;
    public string PyroscopeBasicAuthPassword { get; set; } = string.Empty;

    // In-Game Store & Item Progression Mode
    public bool UnlockAllItems { get; set; } = true;
    public bool QueueArcadeEnabled { get; set; } = true;
    public int QueueArcadeInstances { get; set; } = 2;

    // Standalone & LAN/WAN Hosting Settings
    public string HostingMode { get; set; } = "Local"; // "Local", "Lan", "PublicInternet"
    public string SelectedLanIp { get; set; } = string.Empty;
    public string WanHost { get; set; } = string.Empty;
    public bool WanWarningAcknowledged { get; set; } = false;
}
