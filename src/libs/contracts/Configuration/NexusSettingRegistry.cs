// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Configuration;

/// <summary>
///     The editor/storage shape of a NEXUS setting.
/// </summary>
public enum NexusSettingKind
{
    /// <summary>Free-form, non-sensitive text.</summary>
    Text,

    /// <summary>Sensitive value (password, token, API key). Always masked and never logged.</summary>
    Secret,

    /// <summary>A file-system path (machine-specific, never committed).</summary>
    Path,

    /// <summary>A whole number, stored as a string in the vault.</summary>
    Integer,

    /// <summary>A boolean, stored as a JSON boolean in the vault.</summary>
    Boolean,

    /// <summary>One value from a fixed list, stored as a string.</summary>
    Choice,

    /// <summary>An absolute URL.</summary>
    Url
}

/// <summary>
///     Describes a single setting managed by the NEXUS Control Panel "Secrets &amp; Configs" page: the canonical
///     configuration key, the compatibility keys still consumed by services, and the legacy aliases that are only read
///     for migration purposes.
/// </summary>
public sealed record NexusSettingDescriptor
{
    /// <summary>Stable identifier. Matches the <c>NexusSecrets</c> property name in the Control Panel.</summary>
    public required string Id { get; init; }

    /// <summary>Canonical configuration key (colon form, e.g. <c>Supabase:S3AccessKey</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Logical panel section the setting belongs to.</summary>
    public required string Section { get; init; }

    /// <summary>Human-readable label.</summary>
    public required string Label { get; init; }

    /// <summary>Editor/storage kind.</summary>
    public NexusSettingKind Kind { get; init; } = NexusSettingKind.Text;

    /// <summary>
    ///     Compatibility keys that services still read today. They are written alongside <see cref="Key" /> on every
    ///     save so nothing breaks while consumers are migrated to the canonical key.
    /// </summary>
    public string[] WriteAlso { get; init; } = [];

    /// <summary>
    ///     Legacy aliases that NOTHING in the codebase reads any more. They are read once (migration) and then removed
    ///     from the vault on save.
    /// </summary>
    public string[] ReadAliases { get; init; } = [];

    /// <summary>
    ///     Keys that some consumer still reads but that the panel has never written. They are read as a last-resort
    ///     fallback and are never written nor removed.
    /// </summary>
    public string[] PreservedAliases { get; init; } = [];

    /// <summary>Extra names emitted into the generated <c>.env</c> file (e.g. docker-compose variable names).</summary>
    public string[] EnvNames { get; init; } = [];

    /// <summary>Allowed values when <see cref="Kind" /> is <see cref="NexusSettingKind.Choice" />.</summary>
    public string[] Choices { get; init; } = [];

    /// <summary>Whether a fresh developer setup must provide this value.</summary>
    public bool Required { get; init; }

    /// <summary>Default value shown in the panel when nothing is stored.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    ///     When the value equals this sentinel the keys are removed from the vault instead of written, letting the
    ///     consuming service apply its own default (e.g. <c>0</c> for an optional port).
    /// </summary>
    public string? OmitValue { get; init; }

    /// <summary>Comma separated list of the services that consume the setting.</summary>
    public string UsedBy { get; init; } = string.Empty;

    /// <summary>Short help text shown in the panel.</summary>
    public string HelpText { get; init; } = string.Empty;

    /// <summary>Whether the value is sensitive.</summary>
    public bool IsSecret => Kind == NexusSettingKind.Secret;

    /// <summary>All keys written on save (canonical first).</summary>
    public IReadOnlyList<string> WriteKeys => [Key, .. WriteAlso];

    /// <summary>All keys considered when loading, in precedence order.</summary>
    public IReadOnlyList<string> ReadKeys => [Key, .. WriteAlso, .. ReadAliases, .. PreservedAliases];

    /// <summary>All keys owned by the panel (written or scrubbed). Preserved aliases are excluded.</summary>
    public IReadOnlyList<string> ManagedKeys => [Key, .. WriteAlso, .. ReadAliases];

    /// <summary>
    ///     Environment-variable names emitted into the generated <c>.env</c> file: the canonical key in
    ///     double-underscore form, every env-safe compatibility key, and <see cref="EnvNames" />.
    /// </summary>
    public IReadOnlyList<string> DotEnvNames
    {
        get
        {
            List<string> names = [Key.Replace(":", "__", StringComparison.Ordinal)];
            foreach (string candidate in WriteAlso.Concat(EnvNames))
                if (IsEnvironmentSafe(candidate) && !names.Contains(candidate, StringComparer.Ordinal))
                    names.Add(candidate);

            return names.Where(IsEnvironmentSafe).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>True when <paramref name="name" /> is a valid POSIX/docker-compose variable name.</summary>
    public static bool IsEnvironmentSafe(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;

        for (int i = 1; i < name.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_'))
                return false;

        return true;
    }
}

/// <summary>
///     Single source of truth for every setting the NEXUS Control Panel manages. The panel, the generated <c>.env</c>
///     file and (from Slice 2) service startup validation are all driven from this list.
/// </summary>
public static class NexusSettingRegistry
{
    /// <summary>Panel section identifiers.</summary>
    public static class Sections
    {
        /// <summary>Master server &amp; gateway.</summary>
        public const string MasterServer = "MasterServer";

        /// <summary>Ports and bindings.</summary>
        public const string Network = "Network";

        /// <summary>Discord / Twitch OAuth.</summary>
        public const string OAuth = "OAuth";

        /// <summary>Database provider and passwords.</summary>
        public const string Database = "Database";

        /// <summary>Supabase project, storage and S3 keys.</summary>
        public const string Supabase = "Supabase";

        /// <summary>Signing keys, service keys and API keys.</summary>
        public const string Security = "Security";

        /// <summary>Local machine paths.</summary>
        public const string Paths = "Paths";

        /// <summary>COMPEL game server manager &amp; host account.</summary>
        public const string Compel = "Compel";

        /// <summary>Replay storage.</summary>
        public const string Replay = "Replay";

        /// <summary>Grafana / Pyroscope.</summary>
        public const string Observability = "Observability";

        /// <summary>In-game store.</summary>
        public const string Store = "Store";

        /// <summary>Operational alerting.</summary>
        public const string Alerts = "Alerts";
    }

    private static readonly string[] TriState = ["", "true", "false"];

    /// <summary>All registered settings.</summary>
    public static IReadOnlyList<NexusSettingDescriptor> All { get; } =
    [
        // ── Master Server ────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "MasterServerAddress", Key = "MasterServer:Address", Section = Sections.MasterServer,
            Label = "Master Server Address", Kind = NexusSettingKind.Url, DefaultValue = "http://localhost:55555",
            WriteAlso = ["master-server-address", "Parameters:master-server-address", "MASTER_SERVER_ADDRESS"],
            ReadAliases = ["MASTER_SERVER_URL", "MasterServerAddress"],
            UsedBy = "KONGOR, COMPEL, WILLOWMAKER", HelpText = "Base URL of the KONGOR master server."
        },
        new()
        {
            Id = "InfrastructureGateway", Key = "Infrastructure:Gateway", Section = Sections.MasterServer,
            Label = "Infrastructure Gateway", WriteAlso = ["INFRASTRUCTURE_GATEWAY"], ReadAliases = ["gateway"],
            UsedBy = "COMPEL, WILLOWMAKER",
            HelpText = "Public host (or host:port) clients and game servers use. Blank = derived from the master server address."
        },
        Port("MasterServerPort", "MasterServer:Port", "Master Server Port", "55555", "KONGOR",
            "MASTER_SERVER_PORT", "Port:MasterServer"),

        // ── Network ──────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "ChatServerHost", Key = "ChatServer:Host", Section = Sections.Network, Label = "Chat Server Bind Host",
            DefaultValue = "127.0.0.1", ReadAliases = ["CHAT_SERVER_HOST"], UsedBy = "TRANSMUTANSTEIN"
        },
        new()
        {
            Id = "ChatServerPublicHost", Key = "ChatServer:PublicHost", Section = Sections.Network,
            Label = "Chat Server Public Host", DefaultValue = "127.0.0.1", ReadAliases = ["CHAT_SERVER_PUBLIC_HOST"],
            UsedBy = "TRANSMUTANSTEIN"
        },
        Port("ChatServerClientPort", "ChatServer:ClientPort", "Chat Client Port", "11031", "TRANSMUTANSTEIN",
            "CHAT_SERVER_PORT_CLIENT", "CHAT_SERVER_CLIENT_PORT"),
        Port("ChatServerPublicClientPort", "ChatServer:PublicClientPort", "Chat Public Client Port", "11031",
            "TRANSMUTANSTEIN", "CHAT_SERVER_PUBLIC_CLIENT_PORT"),
        Port("ChatServerMatchServerPort", "ChatServer:MatchServerPort", "Chat Match Server Port", "11032",
            "TRANSMUTANSTEIN", "CHAT_SERVER_PORT_MATCH_SERVER"),
        Port("ChatServerMatchServerManagerPort", "ChatServer:MatchServerManagerPort", "Chat Match Server Manager Port",
            "11033", "TRANSMUTANSTEIN", "CHAT_SERVER_PORT_MATCH_SERVER_MANAGER"),
        Port("WebPortalHttpPort", "WebPortal:HttpPort", "Web Portal HTTP Port", "5266", "DAWNBRINGER",
            "DAWNBRINGER_HTTP_PORT"),
        Port("WebPortalHttpsPort", "WebPortal:HttpsPort", "Web Portal HTTPS Port", "5557", "DAWNBRINGER",
            "DAWNBRINGER_HTTPS_PORT"),
        Port("WebPortalApiHttpPort", "WebPortalApi:HttpPort", "Web API HTTP Port", "5200", "ZORGATH",
            "ZORGATH_HTTP_PORT"),
        Port("WebPortalApiHttpsPort", "WebPortalApi:HttpsPort", "Web API HTTPS Port", "5201", "ZORGATH",
            "ZORGATH_HTTPS_PORT"),
        Port("DatabaseContextPort", "DatabaseContext:Port", "Database Port", "5553", "MERRICK", "MERRICK_PORT"),
        Port("DistributedCachePort", "Resources:distributed-cache:tcp:port", "Redis Port", "28298", "ASPIRE",
            "DistributedCache:Port", "REDIS_PORT"),
        Port("DistributedCacheSecondaryPort", "Resources:distributed-cache:secondary:port", "Redis Secondary Port",
            "28299", "ASPIRE"),
        Port("DistributedCacheDashboardPort", "Resources:distributed-cache-dashboard:http:port",
            "Redis Dashboard Port", "28300", "ASPIRE", "DistributedCache:DashboardPort"),
        Port("MatchmakingPlayersPerTeam", "Matchmaking:PlayersPerTeam", "Players Per Team", "1", "KONGOR",
            "MATCHMAKING_PLAYERS_PER_TEAM"),
        Port("MatchmakingIntervalMs", "Matchmaking:IntervalInMilliseconds", "Matchmaking Interval (ms)", "5000",
            "KONGOR", "MATCHMAKING_INTERVAL_MS"),
        Port("CompelPingPort", "GameServer:CompelPingPort", "COMPEL Ping Port", "11234", "COMPEL",
            "COMPEL_PING_PORT"),
        new()
        {
            Id = "GameServerPublicIp", Key = "GameServer:PublicIP", Section = Sections.Network,
            Label = "Game Server Public IP", DefaultValue = "127.0.0.1", ReadAliases = ["GAME_SERVER_PUBLIC_IP"],
            UsedBy = "COMPEL"
        },
        new()
        {
            Id = "GameServerInstancePorts", Key = "GameServer:InstancePorts", Section = Sections.Network,
            Label = "Game Server Instance Ports", DefaultValue = "11235,11236,11237,11238",
            WriteAlso = ["GameServer:PublicPort"], ReadAliases = ["GAME_SERVER_PORTS"], UsedBy = "COMPEL"
        },
        Port("PyroscopePort", "Pyroscope:Port", "Pyroscope Port", "4040", "ASPIRE", "PYROSCOPE_PORT"),

        // ── OAuth ────────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "DiscordClientId", Key = "Authentication:Discord:ClientId", Section = Sections.OAuth,
            Label = "Discord Client ID", Kind = NexusSettingKind.Secret,
            WriteAlso = ["discord-client-id", "Parameters:discord-client-id"],
            ReadAliases = ["discord_client_id", "DiscordClientId"], PreservedAliases = ["DISCORD_CLIENT_ID"],
            UsedBy = "DAWNBRINGER"
        },
        new()
        {
            Id = "DiscordClientSecret", Key = "Authentication:Discord:ClientSecret", Section = Sections.OAuth,
            Label = "Discord Client Secret", Kind = NexusSettingKind.Secret,
            WriteAlso = ["discord-client-secret", "Parameters:discord-client-secret"],
            ReadAliases = ["discord_client_secret", "DiscordClientSecret"],
            PreservedAliases = ["DISCORD_CLIENT_SECRET"], UsedBy = "DAWNBRINGER"
        },
        new()
        {
            Id = "TwitchClientId", Key = "Authentication:Twitch:ClientId", Section = Sections.OAuth,
            Label = "Twitch Client ID", Kind = NexusSettingKind.Secret,
            WriteAlso = ["twitch-client-id", "Parameters:twitch-client-id"],
            ReadAliases = ["twitch_client_id", "TwitchClientId"], PreservedAliases = ["TWITCH_CLIENT_ID"],
            UsedBy = "DAWNBRINGER"
        },
        new()
        {
            Id = "TwitchClientSecret", Key = "Authentication:Twitch:ClientSecret", Section = Sections.OAuth,
            Label = "Twitch Client Secret", Kind = NexusSettingKind.Secret,
            WriteAlso = ["twitch-client-secret", "Parameters:twitch-client-secret"],
            ReadAliases = ["twitch_client_secret", "TwitchClientSecret"],
            PreservedAliases = ["TWITCH_CLIENT_SECRET"], UsedBy = "DAWNBRINGER"
        },

        // ── Database ─────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "DatabaseProvider", Key = "Database:Provider", Section = Sections.Database,
            Label = "Database Provider", Kind = NexusSettingKind.Choice, Choices = ["SqlServer", "PostgreSQL", "Supabase"],
            DefaultValue = "Supabase", WriteAlso = ["DATABASE_PROVIDER"], ReadAliases = ["DatabaseProvider"],
            UsedBy = "MERRICK"
        },
        new()
        {
            Id = "DatabasePassword", Key = "database-password", Section = Sections.Database,
            Label = "Local Database Password", Kind = NexusSettingKind.Secret,
            DefaultValue = "MerrickDatabasePassword123!", ReadAliases = ["Parameters:database-password"],
            UsedBy = "ASPIRE (local SQL Server container)"
        },
        new()
        {
            Id = "DistributedCachePassword", Key = "distributed-cache-password", Section = Sections.Database,
            Label = "Redis Password", Kind = NexusSettingKind.Secret, DefaultValue = "DistributedCachePassword123!",
            ReadAliases = ["Parameters:distributed-cache-password"], UsedBy = "ASPIRE (local Redis container)"
        },

        // ── Supabase ─────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "SupabaseUrl", Key = "Supabase:Url", Section = Sections.Supabase, Label = "Supabase Project URL",
            Kind = NexusSettingKind.Url, WriteAlso = ["SUPABASE_URL", "ReplayStorage:Supabase:ProjectUrl"],
            PreservedAliases = ["SupabaseUrl"], UsedBy = "KONGOR, ZORGATH, COMPEL"
        },
        new()
        {
            Id = "SupabaseAnonKey", Key = "Supabase:AnonKey", Section = Sections.Supabase,
            Label = "Supabase Anon Key", Kind = NexusSettingKind.Secret,
            WriteAlso = ["SUPABASE_ANON_KEY", "ReplayStorage:Supabase:AnonKey"],
            PreservedAliases = ["SupabaseAnonKey"], UsedBy = "KONGOR"
        },
        new()
        {
            Id = "SupabaseServiceRoleKey", Key = "Supabase:ServiceRoleKey", Section = Sections.Supabase,
            Label = "Supabase Service Role Key", Kind = NexusSettingKind.Secret,
            WriteAlso = ["SUPABASE_SERVICE_ROLE_KEY", "ReplayStorage:Supabase:ServiceRoleKey"],
            PreservedAliases = ["SupabaseServiceRoleKey"], UsedBy = "KONGOR"
        },
        new()
        {
            Id = "SupabaseDatabaseConnectionString", Key = "Supabase:DatabaseConnectionString",
            Section = Sections.Supabase, Label = "Supabase / PostgreSQL Connection String",
            Kind = NexusSettingKind.Secret, WriteAlso = ["SUPABASE_DB_CONNECTION_STRING"],
            PreservedAliases =
            [
                "Supabase:Database:ConnectionString", "ConnectionStrings:Supabase", "ConnectionStrings:PostgreSQL",
                "Database:PostgreSQL:ConnectionString", "DATABASE_POSTGRESQL_CONNECTION_STRING",
                "ConnectionStrings:MERRICK"
            ],
            UsedBy = "MERRICK"
        },
        new()
        {
            Id = "SupabaseStorageBucket", Key = "Supabase:StorageBucket", Section = Sections.Supabase,
            Label = "Replay Storage Bucket", DefaultValue = "replays",
            WriteAlso = ["SUPABASE_STORAGE_BUCKET", "ReplayStorage:Supabase:BucketName"], UsedBy = "KONGOR"
        },
        new()
        {
            Id = "SupabaseS3AccessKey", Key = "Supabase:S3AccessKey", Section = Sections.Supabase,
            Label = "Supabase S3 Access Key ID", Kind = NexusSettingKind.Secret,
            WriteAlso = ["SUPABASE_S3_ACCESS_KEY"],
            PreservedAliases = ["KONGOR_LAKEHOUSE_S3_ACCESS_KEY", "SUPABASE_STORAGE_KEY_ID"],
            UsedBy = "COMPEL (match lakehouse uploader)",
            HelpText = "Supabase Dashboard → Storage → S3 Connection → New access key."
        },
        new()
        {
            Id = "SupabaseS3SecretKey", Key = "Supabase:S3SecretKey", Section = Sections.Supabase,
            Label = "Supabase S3 Secret Access Key", Kind = NexusSettingKind.Secret,
            WriteAlso = ["SUPABASE_S3_SECRET_KEY"],
            PreservedAliases = ["KONGOR_LAKEHOUSE_S3_SECRET_KEY", "SUPABASE_STORAGE_SECRET"],
            UsedBy = "COMPEL (match lakehouse uploader)"
        },
        new()
        {
            Id = "SupabaseS3Region", Key = "Supabase:S3Region", Section = Sections.Supabase,
            Label = "Supabase S3 Region", DefaultValue = "us-east-1", WriteAlso = ["SUPABASE_S3_REGION"],
            PreservedAliases = ["KONGOR_LAKEHOUSE_S3_REGION", "SUPABASE_STORAGE_REGION"],
            UsedBy = "COMPEL (match lakehouse uploader)",
            HelpText = "Shown on the Supabase S3 Connection page (e.g. us-east-2)."
        },
        new()
        {
            Id = "SupabaseS3Endpoint", Key = "Supabase:S3Endpoint", Section = Sections.Supabase,
            Label = "Supabase S3 Endpoint (override)", Kind = NexusSettingKind.Url,
            WriteAlso = ["SUPABASE_S3_ENDPOINT"],
            PreservedAliases = ["KONGOR_LAKEHOUSE_S3_ENDPOINT", "SUPABASE_STORAGE_ENDPOINT"],
            UsedBy = "COMPEL (match lakehouse uploader)",
            HelpText = "Blank = <Supabase Project URL>/storage/v1/s3."
        },
        new()
        {
            Id = "LakehouseBucket", Key = "Supabase:LakehouseBucket", Section = Sections.Supabase,
            Label = "Match Analytics (Lakehouse) Bucket", DefaultValue = "match-analytics",
            WriteAlso = ["SUPABASE_LAKEHOUSE_BUCKET"], PreservedAliases = ["KONGOR_LAKEHOUSE_S3_BUCKET"],
            UsedBy = "COMPEL (match lakehouse uploader)"
        },

        // ── Security ─────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "JwtSigningKey", Key = "jwt-signing-key", Section = Sections.Security, Label = "JWT Signing Key",
            Kind = NexusSettingKind.Secret, Required = true,
            DefaultValue = "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!",
            WriteAlso = ["Parameters:jwt-signing-key", "Operational:JWT:SigningKey"], UsedBy = "ZORGATH, DAWNBRINGER",
            HelpText = "Generate a unique 32+ byte value. The default is public and only safe for local development."
        },
        new()
        {
            Id = "InternalServiceKey", Key = "InternalServiceKey", Section = Sections.Security,
            Label = "Internal Service Key", Kind = NexusSettingKind.Secret, WriteAlso = ["INTERNAL_SERVICE_KEY"],
            UsedBy = "ZORGATH, DAWNBRINGER, Control Panel",
            HelpText = "Shared secret for service-to-service calls. Blank = public development default."
        },
        new()
        {
            Id = "IpcAuthKey", Key = "KONGOR_IPC_AUTH_KEY", Section = Sections.Security,
            Label = "WILLOWMAKER IPC Auth Key", Kind = NexusSettingKind.Secret, UsedBy = "WILLOWMAKER",
            HelpText = "Blank = permissive IPC sandbox. Exported to .env; vault wiring for WILLOWMAKER lands in Slice 2."
        },
        new()
        {
            Id = "SendGridApiKey", Key = "sendgrid-api-key", Section = Sections.Security, Label = "SendGrid API Key",
            Kind = NexusSettingKind.Secret, ReadAliases = ["Parameters:sendgrid-api-key"], UsedBy = "ZORGATH"
        },

        // ── Paths ────────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "ProjectPath", Key = "project-path", Section = Sections.Paths, Label = "Project Path",
            Kind = NexusSettingKind.Path, ReadAliases = ["Parameters:project-path", "PROJECT_PATH"],
            UsedBy = "Control Panel"
        },
        new()
        {
            Id = "CompelPath", Key = "compel-path", Section = Sections.Paths, Label = "COMPEL Executable Path",
            Kind = NexusSettingKind.Path, WriteAlso = ["GameServer:CompelPath"],
            ReadAliases = ["Parameters:compel-path", "ReplayConverter:HonExecutablePath"],
            PreservedAliases = ["COMPEL_PATH"], UsedBy = "Control Panel"
        },
        new()
        {
            Id = "GameClientPath", Key = "GameClient:Path", Section = Sections.Paths, Label = "Game Client Path",
            Kind = NexusSettingKind.Path, WriteAlso = ["GameClient:GamePath"],
            ReadAliases = ["GameClient:ClientPath", "GameClientPath", "HON_GAME_PATH", "hon-game-path", "Parameters:game-path"],
            UsedBy = "Control Panel, WILLOWMAKER"
        },

        // ── COMPEL & Host Account ────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "CompelAuthenticationToken", Key = "Compel:AuthenticationToken", Section = Sections.Compel,
            Label = "COMPEL Control Plane Token", Kind = NexusSettingKind.Secret, Required = true,
            WriteAlso =
            [
                "COMPEL_AUTHENTICATION_TOKEN", "Compel:ControlPlaneAuthenticationToken",
                "COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN"
            ],
            ReadAliases = ["CompelAuthenticationToken", "AUTHENTICATION_TOKEN"],
            UsedBy = "COMPEL, Control Panel, kongor-cli",
            HelpText = "Bearer token protecting the COMPEL HTTP control plane. Use [Generate]."
        },
        new()
        {
            Id = "HostAccountUserName", Key = "HostAccount:UserName", Section = Sections.Compel,
            Label = "Host Account Username", DefaultValue = "HOST", Required = true,
            WriteAlso = ["HOST_USERNAME", "Compel:UserName", "MatchServer:UserName"], ReadAliases = ["HostUserName"],
            PreservedAliases = ["Host:UserName", "Parameters:host-username"], UsedBy = "COMPEL"
        },
        new()
        {
            Id = "HostAccountPassword", Key = "HostAccount:Password", Section = Sections.Compel,
            Label = "Host Account Password", Kind = NexusSettingKind.Secret, Required = true,
            WriteAlso = ["HOST_PASSWORD", "Compel:Password", "MatchServer:Password"], ReadAliases = ["HostPassword"],
            PreservedAliases = ["Host:Password", "Parameters:host-password"], UsedBy = "COMPEL"
        },
        new()
        {
            Id = "CompelGateway", Key = "Compel:Gateway", Section = Sections.Compel, Label = "COMPEL Gateway",
            DefaultValue = "localhost", PreservedAliases = ["COMPEL_GATEWAY"], UsedBy = "COMPEL"
        },
        new()
        {
            Id = "CompelInstances", Key = "Compel:Instances", Section = Sections.Compel, Label = "COMPEL Instances",
            Kind = NexusSettingKind.Integer, DefaultValue = "2", PreservedAliases = ["COMPEL_INSTANCES"],
            UsedBy = "COMPEL"
        },
        new()
        {
            Id = "CompelPriorityMode", Key = "Compel:PriorityMode", Section = Sections.Compel,
            Label = "COMPEL Priority Mode", Kind = NexusSettingKind.Choice,
            Choices = ["Background", "HighPriority", "Adaptive"], DefaultValue = "Background",
            WriteAlso = ["COMPEL_PRIORITY_MODE"], ReadAliases = ["HostPriorityMode"], UsedBy = "COMPEL"
        },
        new()
        {
            Id = "ContainerRuntimeTarget", Key = "Compel:ContainerRuntime", Section = Sections.Compel,
            Label = "Container Runtime", Kind = NexusSettingKind.Choice,
            Choices = ["Docker", "Minikube", "Kubernetes", "K3s (Agones)"], DefaultValue = "Docker",
            WriteAlso = ["COMPEL_CONTAINER_RUNTIME"], UsedBy = "Control Panel"
        },
        new()
        {
            Id = "CompelServerNamePrefix", Key = "Compel:ServerNamePrefix", Section = Sections.Compel,
            Label = "Server Name Prefix", DefaultValue = "KONGOR ARENA",
            WriteAlso = ["COMPEL_SERVER_NAME_PREFIX", "ServerNamePrefix"], UsedBy = "COMPEL"
        },
        new()
        {
            Id = "CompelLocation", Key = "Compel:Location", Section = Sections.Compel, Label = "Server Location",
            DefaultValue = "USE", WriteAlso = ["COMPEL_LOCATION", "Location"], UsedBy = "COMPEL"
        },
        new()
        {
            Id = "CompelInstallationDirectory", Key = "Compel:InstallationDirectory", Section = Sections.Compel,
            Label = "COMPEL Installation Directory", Kind = NexusSettingKind.Path,
            WriteAlso = ["COMPEL_INSTALLATION_DIRECTORY"], UsedBy = "COMPEL",
            HelpText = "Game server install COMPEL launches from. Blank = COMPEL.json / built-in default."
        },
        new()
        {
            Id = "CompelControlPlanePort", Key = "Compel:ControlPlanePort", Section = Sections.Compel,
            Label = "COMPEL Control Plane Port", Kind = NexusSettingKind.Integer, DefaultValue = "0", OmitValue = "0",
            WriteAlso = ["COMPEL_CONTROL_PLANE_PORT"], UsedBy = "COMPEL",
            HelpText = "0 = default (11234 + port range offset)."
        },
        new()
        {
            Id = "CompelPortRangeOffset", Key = "Compel:PortRangeOffset", Section = Sections.Compel,
            Label = "COMPEL Port Range Offset", Kind = NexusSettingKind.Integer, DefaultValue = "0", OmitValue = "0",
            WriteAlso = ["COMPEL_PORT_RANGE_OFFSET"], UsedBy = "COMPEL", HelpText = "0 = COMPEL.json / default."
        },
        new()
        {
            Id = "CompelUseProxy", Key = "Compel:UseProxy", Section = Sections.Compel, Label = "COMPEL Use UDP Proxy",
            Kind = NexusSettingKind.Choice, Choices = TriState, DefaultValue = "", OmitValue = "",
            WriteAlso = ["COMPEL_USE_PROXY"], UsedBy = "COMPEL", HelpText = "Blank = COMPEL.json / default (true)."
        },
        new()
        {
            Id = "CompelCdnSynchronisation", Key = "Compel:CDNSynchronisation", Section = Sections.Compel,
            Label = "COMPEL CDN Synchronisation", Kind = NexusSettingKind.Choice, Choices = TriState,
            DefaultValue = "", OmitValue = "", WriteAlso = ["COMPEL_CDN_SYNCHRONISATION"], UsedBy = "COMPEL",
            HelpText = "Blank = COMPEL.json / default (false)."
        },

        // ── Replay Storage ───────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "ReplayStorageProvider", Key = "ReplayStorage:Provider", Section = Sections.Replay,
            Label = "Replay Storage Provider", Kind = NexusSettingKind.Choice,
            Choices = ["LocalDisk", "Supabase", "Cloud"], DefaultValue = "LocalDisk", UsedBy = "KONGOR"
        },
        new()
        {
            Id = "LocalReplayDirectory", Key = "ReplayStorage:LocalDisk:UploadDirectory", Section = Sections.Replay,
            Label = "Local Replay Directory", Kind = NexusSettingKind.Path, UsedBy = "KONGOR"
        },
        new()
        {
            Id = "CloudBucketName", Key = "ReplayStorage:Cloud:BucketName", Section = Sections.Replay,
            Label = "Cloud Bucket", UsedBy = "KONGOR"
        },
        new()
        {
            Id = "CloudAccessKey", Key = "ReplayStorage:Cloud:AccessKey", Section = Sections.Replay,
            Label = "Cloud Access Key", Kind = NexusSettingKind.Secret, UsedBy = "KONGOR"
        },
        new()
        {
            Id = "CloudSecretKey", Key = "ReplayStorage:Cloud:SecretKey", Section = Sections.Replay,
            Label = "Cloud Secret Key", Kind = NexusSettingKind.Secret, UsedBy = "KONGOR"
        },
        new()
        {
            Id = "CloudServiceUrl", Key = "ReplayStorage:Cloud:ServiceUrl", Section = Sections.Replay,
            Label = "Cloud Service URL", Kind = NexusSettingKind.Url, UsedBy = "KONGOR"
        },
        new()
        {
            Id = "CloudRegion", Key = "ReplayStorage:Cloud:Region", Section = Sections.Replay, Label = "Cloud Region",
            DefaultValue = "us-east-1", UsedBy = "KONGOR"
        },

        // ── Observability ────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "GrafanaOtlpEndpoint", Key = "OTEL_EXPORTER_OTLP_ENDPOINT", Section = Sections.Observability,
            Label = "Grafana OTLP Endpoint", Kind = NexusSettingKind.Url,
            WriteAlso = ["GRAFANA_OTLP_ENDPOINT", "Grafana:OtlpEndpoint"], UsedBy = "All services, COMPEL"
        },
        new()
        {
            Id = "GrafanaOtlpHeaders", Key = "OTEL_EXPORTER_OTLP_HEADERS", Section = Sections.Observability,
            Label = "Grafana OTLP Headers", Kind = NexusSettingKind.Secret,
            WriteAlso = ["GRAFANA_OTLP_HEADERS", "Grafana:OtlpHeaders"], UsedBy = "All services, COMPEL"
        },
        new()
        {
            Id = "PyroscopeServerAddress", Key = "PYROSCOPE_SERVER_ADDRESS", Section = Sections.Observability,
            Label = "Pyroscope Server Address", Kind = NexusSettingKind.Url, WriteAlso = ["Pyroscope:ServerAddress"],
            ReadAliases =
                ["Pyroscope:Address", "PyroscopeServerAddress", "PYROSCOPE_ADDRESS", "Parameters:pyroscope-server-address"],
            UsedBy = "All services, COMPEL"
        },
        new()
        {
            Id = "PyroscopeBasicAuthUser", Key = "PYROSCOPE_BASIC_AUTH_USER", Section = Sections.Observability,
            Label = "Pyroscope User", Kind = NexusSettingKind.Secret, WriteAlso = ["Pyroscope:BasicAuthUser"],
            ReadAliases = ["Pyroscope:User", "PyroscopeUser", "PYROSCOPE_USER", "Parameters:pyroscope-user"],
            UsedBy = "All services, COMPEL"
        },
        new()
        {
            Id = "PyroscopeBasicAuthPassword", Key = "PYROSCOPE_BASIC_AUTH_PASSWORD",
            Section = Sections.Observability, Label = "Pyroscope Password", Kind = NexusSettingKind.Secret,
            WriteAlso = ["Pyroscope:BasicAuthPassword"],
            ReadAliases = ["Pyroscope:Password", "PyroscopePassword", "PYROSCOPE_PASSWORD", "Parameters:pyroscope-password"],
            UsedBy = "All services, COMPEL"
        },

        // ── Alerts ───────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "AlertWebhookUrl", Key = "Alerts:DiscordWebhookUrl", Section = Sections.Alerts,
            Label = "Discord Alert Webhook URL", Kind = NexusSettingKind.Secret,
            WriteAlso = ["DISCORD_ALERT_WEBHOOK_URL"], PreservedAliases = ["ALERT_WEBHOOK_URL"],
            UsedBy = "service-defaults circuit-breaker alerts",
            HelpText = "Discord channel → Integrations → Webhooks → Copy URL. Treat as a secret."
        },

        // ── Store ────────────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "UnlockAllItems", Key = "Store:UnlockAllItems", Section = Sections.Store, Label = "Unlock All Public Items",
            Kind = NexusSettingKind.Boolean, DefaultValue = "true",
            WriteAlso = ["UnlockAllItems", "unlock-all-items", "Parameters:unlock-all-items"],
            UsedBy = "KONGOR, ZORGATH, DAWNBRINGER, MERRICK, NEXUS Control Panel"
        },

        // ── Queue Arcade ("While You Wait" Mini-Game Servers) ────────────────────────────────────────────────────
        new()
        {
            Id = "QueueArcadeEnabled", Key = "QueueArcade:Enabled", Section = Sections.Compel,
            Label = "Queue Arcade Enabled", Kind = NexusSettingKind.Boolean, DefaultValue = "true",
            WriteAlso = ["QUEUE_ARCADE_ENABLED"], UsedBy = "TRANSMUTANSTEIN, NEXUS Control Panel",
            HelpText = "Enables auto-pushing queued players into hosted Queue Arcade mini-game servers while waiting."
        },
        new()
        {
            Id = "QueueArcadeInstances", Key = "QueueArcade:Instances", Section = Sections.Compel,
            Label = "Queue Arcade Worker Instances", Kind = NexusSettingKind.Integer, DefaultValue = "2",
            WriteAlso = ["QUEUE_ARCADE_INSTANCES"], UsedBy = "COMPEL (Tier Q), NEXUS Control Panel",
            HelpText = "Number of dedicated Queue Arcade worker slots to supervise (default 2: #Q1 warm + #Q2 overflow)."
        }
    ];

    /// <summary>Looks a descriptor up by <see cref="NexusSettingDescriptor.Id" />.</summary>
    public static NexusSettingDescriptor Get(string id)
    {
        return All.FirstOrDefault(descriptor => descriptor.Id == id)
               ?? throw new KeyNotFoundException($"No NEXUS setting is registered with Id '{id}'.");
    }

    private static NexusSettingDescriptor Port(string id, string key, string label, string defaultValue,
        string usedBy, params string[] readAliases)
    {
        return new NexusSettingDescriptor
        {
            Id = id, Key = key, Section = Sections.Network, Label = label, Kind = NexusSettingKind.Integer,
            DefaultValue = defaultValue, ReadAliases = readAliases, UsedBy = usedBy
        };
    }
}
