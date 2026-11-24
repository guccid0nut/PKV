// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Extensions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using Npgsql;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class SecretsConfigViewModel : ViewModelBase
{
    private static readonly HttpClient Client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(10));
    private readonly HttpClient _httpClient;
    private readonly IGameClientAssetService? _assetService;
    private readonly ISecretsManagerService _secretsManager;
    private readonly IDockerHealthService _dockerHealth;
    private readonly Func<string, CancellationToken, Task<(bool Success, long ElapsedMs, string Message)>> _postgresProbe;
    private bool _isSyncingSupabasePassword;

    [ObservableProperty] private bool _isTestingAuthentication;
    [ObservableProperty] private string _authenticationTestFeedback = string.Empty;
    [ObservableProperty] private string _authenticationTestStatusColor = "#94A3B8";
    [ObservableProperty] private string _authenticationTestStatusBorderColor = "#1E293B";
    [ObservableProperty] private string _authenticationTestStatusIcon = "🔑";

    [ObservableProperty] private string _compelPriorityMode = "Background";
    [ObservableProperty] private string _selectedContainerPlatform = "Docker";
    [ObservableProperty] private bool _revealContainerSecretsInCommand;
    [ObservableProperty] private string _generatedContainerCommand = string.Empty;
    [ObservableProperty] private bool _isLaunchingContainer;
    [ObservableProperty] private string _containerLaunchFeedback = string.Empty;
    [ObservableProperty] private bool _isDockerImageBuilt = true;

    public ObservableCollection<string> CompelPriorityModes { get; } = ["Background", "HighPriority", "Adaptive"];
    public ObservableCollection<string> ContainerPlatforms { get; } = ["Docker", "Minikube", "Kubernetes", "K3s (Agones)"];

    [ObservableProperty] private int _chatServerClientPort = 11031;
    [ObservableProperty] private string _chatServerHost = "127.0.0.1";
    [ObservableProperty] private int _chatServerMatchServerManagerPort = 11033;
    [ObservableProperty] private int _chatServerMatchServerPort = 11032;
    [ObservableProperty] private int _chatServerPublicClientPort = 11031;
    [ObservableProperty] private string _chatServerPublicHost = "127.0.0.1";

    [ObservableProperty] private string _cloudAccessKey = string.Empty;
    [ObservableProperty] private string _cloudBucketName = string.Empty;
    [ObservableProperty] private string _cloudRegion = "us-east-1";
    [ObservableProperty] private string _cloudSecretKey = string.Empty;
    [ObservableProperty] private string _cloudServiceUrl = string.Empty;
    [ObservableProperty] private string _compelAuthenticationToken = string.Empty;
    [ObservableProperty] private string _compelGateway = "localhost";
    [ObservableProperty] private int _compelInstances = 2;
    [ObservableProperty] private string _compelPath = string.Empty;
    [ObservableProperty] private int _compelPingPort = 11234;
    [ObservableProperty] private string _databasePassword = "MerrickDatabasePassword123!";
    [ObservableProperty] private string _databaseProvider = "Supabase";
    [ObservableProperty] private int _dbContextPort = 5553;
    [ObservableProperty] private string _discordClientId = string.Empty;
    [ObservableProperty] private string _discordClientSecret = string.Empty;
    [ObservableProperty] private int _distributedCacheDashboardPort = 28300;
    [ObservableProperty] private string _distributedCachePassword = "DistributedCachePassword123!";
    [ObservableProperty] private int _distributedCachePort = 28298;
    [ObservableProperty] private int _distributedCacheSecondaryPort = 28299;
    [ObservableProperty] private string _docModalBadge = "GUIDE";
    [ObservableProperty] private string _docModalContent = string.Empty;
    [ObservableProperty] private string _docModalCopyLabel = string.Empty;
    [ObservableProperty] private string _docModalCopySuccessText = string.Empty;
    [ObservableProperty] private string _docModalCopyText = string.Empty;
    [ObservableProperty] private string _docModalSecondaryUrl = string.Empty;
    [ObservableProperty] private string _docModalSecondaryUrlLabel = string.Empty;
    [ObservableProperty] private string _docModalTitle = string.Empty;
    [ObservableProperty] private string _docModalUrl = string.Empty;
    [ObservableProperty] private string _docModalUrlLabel = string.Empty;
    [ObservableProperty] private string _gameClientPath = string.Empty;
    [ObservableProperty] private string _gameServerInstancePorts = "11235,11236,11237,11238";
    [ObservableProperty] private string _gameServerPublicIp = "127.0.0.1";
    [ObservableProperty] private string _grafanaOtlpEndpoint = string.Empty;
    [ObservableProperty] private string _grafanaOtlpHeaders = string.Empty;
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private bool _hasDatabaseChanges;
    [ObservableProperty] private bool _hasHostAccountChanges;
    [ObservableProperty] private bool _hasMasterServerChanges;
    [ObservableProperty] private bool _hasNetworkChanges;
    [ObservableProperty] private bool _hasOAuthChanges;
    [ObservableProperty] private bool _hasObservabilityChanges;
    [ObservableProperty] private bool _hasPathChanges;
    [ObservableProperty] private bool _hasReplayChanges;
    [ObservableProperty] private bool _hasSecurityChanges;
    [ObservableProperty] private bool _hasStoreChanges;
    [ObservableProperty] private bool _hasSupabaseChanges;
    [ObservableProperty] private string _hostAccountPassword = string.Empty;
    [ObservableProperty] private string _hostAccountUserName = "HOST";

    [ObservableProperty] private string _infrastructureGateway = "masterserver.newbs.io";

    // ── Settings added in Slice 1 (registry-driven; previously hand-edited in vault/env/COMPEL.json) ──
    [ObservableProperty] private string _supabaseS3Endpoint = string.Empty;
    [ObservableProperty] private string _lakehouseBucket = "match-analytics";
    [ObservableProperty] private string _internalServiceKey = string.Empty;
    [ObservableProperty] private string _ipcAuthKey = string.Empty;
    [ObservableProperty] private string _alertWebhookUrl = string.Empty;
    [ObservableProperty] private string _compelInstallationDirectory = string.Empty;
    [ObservableProperty] private int _compelControlPlanePort;
    [ObservableProperty] private int _compelPortRangeOffset;
    [ObservableProperty] private string _compelUseProxy = string.Empty;
    [ObservableProperty] private string _compelCdnSynchronisation = string.Empty;
    [ObservableProperty] private string _compelServerNamePrefix = "KONGOR ARENA";
    [ObservableProperty] private string _compelLocation = "USE";
    [ObservableProperty] private bool _queueArcadeEnabled = true;
    [ObservableProperty] private int _queueArcadeInstances = 2;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(InternalServiceKeyMask))]
    private bool _revealInternalServiceKey;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IpcAuthKeyMask))]
    private bool _revealIpcAuthKey;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AlertWebhookUrlMask))]
    private bool _revealAlertWebhookUrl;

    // Vault ⇄ .env ⇄ OS environment drift banner
    [ObservableProperty] private bool _isDriftBannerVisible;
    [ObservableProperty] private string _driftBannerText = string.Empty;
    [ObservableProperty] private bool _canReconcileDrift;

    // First-run setup checklist (derived from registry "Required" flags)
    [ObservableProperty] private bool _hasSetupChecklist;
    [ObservableProperty] private string _setupChecklistText = string.Empty;

    // Connection test feedback
    [ObservableProperty] private bool _isTestingS3Connection;
    [ObservableProperty] private string _s3ConnectionFeedback = string.Empty;
    [ObservableProperty] private bool _isSendingTestAlert;
    [ObservableProperty] private string _alertTestFeedback = string.Empty;

    public ObservableCollection<string> TriStateOptions { get; } = ["", "true", "false"];

    public char InternalServiceKeyMask => RevealInternalServiceKey ? '\0' : '•';
    public char IpcAuthKeyMask => RevealIpcAuthKey ? '\0' : '•';
    public char AlertWebhookUrlMask => RevealAlertWebhookUrl ? '\0' : '•';

    /// <summary>Effective S3 endpoint: explicit override, else derived from the Supabase project URL.</summary>
    public string EffectiveSupabaseS3Endpoint =>
        !string.IsNullOrWhiteSpace(SupabaseS3Endpoint)
            ? SupabaseS3Endpoint.Trim()
            : (string.IsNullOrWhiteSpace(SupabaseUrl) ? string.Empty : SupabaseUrl.Trim().TrimEnd('/') + "/storage/v1/s3");

    /// <summary>
    ///     Watermark for the optional S3 Endpoint Override field. Masks any derived project ref while
    ///     <see cref="RevealSupabaseUrl" /> is false so screenshots of Secrets &amp; Config never leak hidden project IDs.
    /// </summary>
    public string SupabaseS3EndpointWatermark =>
        RevealSupabaseUrl && !string.IsNullOrWhiteSpace(EffectiveSupabaseS3Endpoint)
            ? EffectiveSupabaseS3Endpoint
            : "https://<project-ref>.supabase.co/storage/v1/s3";

    private NexusSecrets _loadedSecrets = new();

    // Documentation Modal State
    [ObservableProperty] private bool _isDocModalOpen;

    partial void OnIsDocModalOpenChanged(bool value)
    {
        Controls.Kongor3DViewportControl.SetModalOverlayActive(value);
    }

    [ObservableProperty] private bool _isStatusBannerVisible;
    [ObservableProperty] private bool _isStatusSuccess = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupabaseStatusBackground))]
    [NotifyPropertyChangedFor(nameof(SupabaseStatusBorderBrush))]
    [NotifyPropertyChangedFor(nameof(SupabaseStatusForeground))]
    private bool _isSupabaseStatusSuccess = true;

    [ObservableProperty] private bool _isSupabaseStatusVisible;
    [ObservableProperty] private string _jwtSigningKey = "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!";
    [ObservableProperty] private string _localReplayDirectory = string.Empty;
    [ObservableProperty] private string _masterServerAddress = "http://localhost:55555";
    [ObservableProperty] private int _masterServerPort = 55555;
    [ObservableProperty] private int _matchmakingIntervalMs = 5000;
    [ObservableProperty] private int _matchmakingPlayersPerTeam = 1;

    private NexusSecrets _originalSecrets = new();
    [ObservableProperty] private string _projectPath = string.Empty;
    [ObservableProperty] private string _pyroscopeBasicAuthPassword = string.Empty;
    [ObservableProperty] private string _pyroscopeBasicAuthUser = string.Empty;
    [ObservableProperty] private int _pyroscopePort = 4040;
    [ObservableProperty] private string _pyroscopeServerAddress = "http://localhost:4040";

    [ObservableProperty] private string _replayStorageProvider = "LocalDisk";

    [ObservableProperty] private bool _revealCloudAccessKey;

    [ObservableProperty] private bool _revealCloudBucketName;

    [ObservableProperty] private bool _revealCloudRegion;

    [ObservableProperty] private bool _revealCloudSecretKey;

    [ObservableProperty] private bool _revealCloudServiceUrl;

    [ObservableProperty] private bool _revealCompelAuthenticationToken;

    [ObservableProperty] private bool _revealCompelPath;

    [ObservableProperty] private bool _revealDatabasePassword;

    [ObservableProperty] private bool _revealDatabaseProvider;

    [ObservableProperty] private bool _revealDiscordClientId;

    [ObservableProperty] private bool _revealDiscordClientSecret;

    [ObservableProperty] private bool _revealDistributedCachePassword;

    [ObservableProperty] private bool _revealGameClientPath;

    [ObservableProperty] private bool _revealGrafanaOtlpEndpoint;

    [ObservableProperty] private bool _revealGrafanaOtlpHeaders;
    [ObservableProperty] private bool _revealHostAccountPassword;

    [ObservableProperty] private bool _revealJwtSigningKey;

    [ObservableProperty] private bool _revealLocalReplayDirectory;

    // Visibility toggles for masked fields
    [ObservableProperty] private bool _revealMasterServerAddress;

    [ObservableProperty] private bool _revealPyroscopeBasicAuthUser;

    [ObservableProperty] private bool _revealPyroscopePassword;

    [ObservableProperty] private bool _revealPyroscopeServerAddress;

    [ObservableProperty] private bool _revealSendGridApiKey;

    [ObservableProperty] private bool _revealSupabaseAnonKey;

    [ObservableProperty] private bool _revealSupabaseDatabaseConnectionString;

    [ObservableProperty] private bool _revealSupabaseDatabasePassword;

    [ObservableProperty] private bool _revealSupabaseS3AccessKey;

    [ObservableProperty] private bool _revealSupabaseS3Region;

    [ObservableProperty] private bool _revealSupabaseS3SecretKey;

    [ObservableProperty] private bool _revealSupabaseServiceRoleKey;

    [ObservableProperty] private bool _revealSupabaseStorageBucket;

    [ObservableProperty] private bool _revealSupabaseUrl;

    [ObservableProperty] private bool _revealTwitchClientId;

    [ObservableProperty] private bool _revealTwitchClientSecret;

    [ObservableProperty] private string _sendGridApiKey = string.Empty;

    [ObservableProperty] private string _statusBannerText = string.Empty;

    [ObservableProperty] private string _supabaseAnonKey = string.Empty;

    [ObservableProperty] private string _supabaseDatabaseConnectionString = string.Empty;

    [ObservableProperty] private string _supabaseDatabasePassword = string.Empty;

    [ObservableProperty] private string _supabaseS3AccessKey = string.Empty;

    [ObservableProperty] private string _supabaseS3Region = "us-east-1";

    [ObservableProperty] private string _supabaseS3SecretKey = string.Empty;

    [ObservableProperty] private string _supabaseServiceRoleKey = string.Empty;

    [ObservableProperty] private string _supabaseStatusText = string.Empty;

    [ObservableProperty] private string _supabaseStorageBucket = "replays";

    [ObservableProperty] private string _supabaseUrl = string.Empty;

    [ObservableProperty] private string _twitchClientId = string.Empty;

    [ObservableProperty] private string _twitchClientSecret = string.Empty;
    [ObservableProperty] private bool _unlockAllItems = true;
    [ObservableProperty] private int _webPortalApiHttpPort = 5200;
    [ObservableProperty] private int _webPortalApiHttpsPort = 5201;
    [ObservableProperty] private int _webPortalHttpPort = 5266;
    [ObservableProperty] private int _webPortalHttpsPort = 5557;

    public SecretsConfigViewModel(
        ISecretsManagerService secretsManager,
        IGameClientAssetService? assetService = null,
        IDockerHealthService? dockerHealth = null,
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<(bool Success, long ElapsedMs, string Message)>>? postgresProbe = null)
    {
        _secretsManager = secretsManager;
        _assetService = assetService;
        _dockerHealth = dockerHealth ?? new DockerHealthService();
        _httpClient = httpClient ?? Client;
        _postgresProbe = postgresProbe ?? ProbePostgresConnectionAsync;
        _ = LoadSecretsAsync();
    }

    public string SupabaseStatusBackground => IsSupabaseStatusSuccess ? "#064E3B" : "#450A0A";
    public string SupabaseStatusBorderBrush => IsSupabaseStatusSuccess ? "#10B981" : "#EF4444";
    public string SupabaseStatusForeground => IsSupabaseStatusSuccess ? "#A7F3D0" : "#FCA5A5";

    public ObservableCollection<string> DatabaseProviders { get; } = ["SqlServer", "PostgreSQL", "Supabase"];

    public ObservableCollection<string> ReplayStorageProviders { get; } = ["LocalDisk", "Supabase", "Cloud"];

    public bool HasDocModalUrl => !string.IsNullOrEmpty(DocModalUrl);
    public bool HasDocModalSecondaryUrl => !string.IsNullOrEmpty(DocModalSecondaryUrl);
    public bool HasDocModalCopyText => !string.IsNullOrEmpty(DocModalCopyText);
    public bool HasDocModalCopySuccess => !string.IsNullOrEmpty(DocModalCopySuccessText);

    public bool IsCloudStorage => string.Equals(ReplayStorageProvider, "Cloud", StringComparison.OrdinalIgnoreCase);

    public bool IsSupabaseStorage =>
        string.Equals(ReplayStorageProvider, "Supabase", StringComparison.OrdinalIgnoreCase);

    public bool IsLocalDiskStorage =>
        string.Equals(ReplayStorageProvider, "LocalDisk", StringComparison.OrdinalIgnoreCase);

    public bool IsSupabaseDatabase => string.Equals(DatabaseProvider, "Supabase", StringComparison.OrdinalIgnoreCase);

    public bool IsPostgreSqlDatabase =>
        string.Equals(DatabaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase) || IsSupabaseDatabase;

    public bool IsSupabaseIntegrated =>
        IsSupabaseDatabase || IsSupabaseStorage || !string.IsNullOrWhiteSpace(SupabaseUrl);

    public char MasterServerAddressMask => RevealMasterServerAddress ? '\0' : '•';
    public char DiscordClientIdMask => RevealDiscordClientId ? '\0' : '•';
    public char DiscordClientSecretMask => RevealDiscordClientSecret ? '\0' : '•';
    public char TwitchClientIdMask => RevealTwitchClientId ? '\0' : '•';
    public char TwitchClientSecretMask => RevealTwitchClientSecret ? '\0' : '•';
    public char DatabasePasswordMask => RevealDatabasePassword ? '\0' : '•';
    public char DistributedCachePasswordMask => RevealDistributedCachePassword ? '\0' : '•';
    public char JwtSigningKeyMask => RevealJwtSigningKey ? '\0' : '•';
    public char SendGridApiKeyMask => RevealSendGridApiKey ? '\0' : '•';
    public char CompelPathMask => RevealCompelPath ? '\0' : '•';
    public char CompelAuthenticationTokenMask => RevealCompelAuthenticationToken ? '\0' : '•';
    public char HostAccountPasswordMask => RevealHostAccountPassword ? '\0' : '•';
    public char GameClientPathMask => RevealGameClientPath ? '\0' : '•';
    public char LocalReplayDirectoryMask => RevealLocalReplayDirectory ? '\0' : '•';
    public char CloudBucketNameMask => RevealCloudBucketName ? '\0' : '•';
    public char CloudAccessKeyMask => RevealCloudAccessKey ? '\0' : '•';
    public char CloudSecretKeyMask => RevealCloudSecretKey ? '\0' : '•';
    public char CloudServiceUrlMask => RevealCloudServiceUrl ? '\0' : '•';
    public char CloudRegionMask => RevealCloudRegion ? '\0' : '•';
    public char GrafanaOtlpEndpointMask => RevealGrafanaOtlpEndpoint ? '\0' : '•';
    public char GrafanaOtlpHeadersMask => RevealGrafanaOtlpHeaders ? '\0' : '•';
    public char PyroscopeServerAddressMask => RevealPyroscopeServerAddress ? '\0' : '•';
    public char PyroscopeBasicAuthUserMask => RevealPyroscopeBasicAuthUser ? '\0' : '•';
    public char PyroscopePasswordMask => RevealPyroscopePassword ? '\0' : '•';

    public char SupabaseUrlMask => RevealSupabaseUrl ? '\0' : '•';
    public char SupabaseAnonKeyMask => RevealSupabaseAnonKey ? '\0' : '•';
    public char SupabaseServiceRoleKeyMask => RevealSupabaseServiceRoleKey ? '\0' : '•';
    public char SupabaseDatabaseConnectionStringMask => RevealSupabaseDatabaseConnectionString ? '\0' : '•';
    public char SupabaseDatabasePasswordMask => RevealSupabaseDatabasePassword ? '\0' : '•';
    public char SupabaseStorageBucketMask => RevealSupabaseStorageBucket ? '\0' : '•';
    public char SupabaseS3AccessKeyMask => RevealSupabaseS3AccessKey ? '\0' : '•';
    public char SupabaseS3SecretKeyMask => RevealSupabaseS3SecretKey ? '\0' : '•';
    public char SupabaseS3RegionMask => RevealSupabaseS3Region ? '\0' : '•';

    public bool IsDiscordConfigured =>
        !string.IsNullOrWhiteSpace(DiscordClientId) && !string.IsNullOrWhiteSpace(DiscordClientSecret);

    public bool IsTwitchConfigured =>
        !string.IsNullOrWhiteSpace(TwitchClientId) && !string.IsNullOrWhiteSpace(TwitchClientSecret);

    public void CheckForChanges()
    {
        HasMasterServerChanges =
            MasterServerAddress != _originalSecrets.MasterServerAddress ||
            InfrastructureGateway != _originalSecrets.InfrastructureGateway ||
            MasterServerPort != _originalSecrets.MasterServerPort;

        HasDatabaseChanges =
            DatabaseProvider != _originalSecrets.DatabaseProvider ||
            DatabasePassword != _originalSecrets.DatabasePassword ||
            DistributedCachePassword != _originalSecrets.DistributedCachePassword ||
            SupabaseDatabaseConnectionString != _originalSecrets.SupabaseDatabaseConnectionString;

        HasSupabaseChanges =
            SupabaseUrl != _originalSecrets.SupabaseUrl ||
            SupabaseAnonKey != _originalSecrets.SupabaseAnonKey ||
            SupabaseServiceRoleKey != _originalSecrets.SupabaseServiceRoleKey ||
            SupabaseDatabaseConnectionString != _originalSecrets.SupabaseDatabaseConnectionString ||
            SupabaseStorageBucket != _originalSecrets.SupabaseStorageBucket ||
            SupabaseS3AccessKey != _originalSecrets.SupabaseS3AccessKey ||
            SupabaseS3SecretKey != _originalSecrets.SupabaseS3SecretKey ||
            SupabaseS3Region != _originalSecrets.SupabaseS3Region ||
            SupabaseS3Endpoint != _originalSecrets.SupabaseS3Endpoint ||
            LakehouseBucket != _originalSecrets.LakehouseBucket;

        HasSecurityChanges =
            JwtSigningKey != _originalSecrets.JwtSigningKey ||
            SendGridApiKey != _originalSecrets.SendGridApiKey ||
            InternalServiceKey != _originalSecrets.InternalServiceKey ||
            IpcAuthKey != _originalSecrets.IpcAuthKey ||
            AlertWebhookUrl != _originalSecrets.AlertWebhookUrl;

        HasHostAccountChanges =
            HostAccountUserName != _originalSecrets.HostAccountUserName ||
            HostAccountPassword != _originalSecrets.HostAccountPassword ||
            CompelGateway != _originalSecrets.CompelGateway ||
            CompelInstances != _originalSecrets.CompelInstances ||
            CompelPriorityMode != _originalSecrets.CompelPriorityMode ||
            CompelInstallationDirectory != _originalSecrets.CompelInstallationDirectory ||
            CompelControlPlanePort != _originalSecrets.CompelControlPlanePort ||
            CompelPortRangeOffset != _originalSecrets.CompelPortRangeOffset ||
            CompelUseProxy != _originalSecrets.CompelUseProxy ||
            CompelCdnSynchronisation != _originalSecrets.CompelCdnSynchronisation ||
            CompelServerNamePrefix != _originalSecrets.CompelServerNamePrefix ||
            CompelLocation != _originalSecrets.CompelLocation ||
            QueueArcadeEnabled != _originalSecrets.QueueArcadeEnabled ||
            QueueArcadeInstances != _originalSecrets.QueueArcadeInstances;

        HasPathChanges =
            ProjectPath != _originalSecrets.ProjectPath ||
            CompelPath != _originalSecrets.CompelPath ||
            CompelAuthenticationToken != _originalSecrets.CompelAuthenticationToken ||
            GameClientPath != _originalSecrets.GameClientPath ||
            HasHostAccountChanges;

        HasReplayChanges =
            ReplayStorageProvider != _originalSecrets.ReplayStorageProvider ||
            LocalReplayDirectory != _originalSecrets.LocalReplayDirectory ||
            CloudBucketName != _originalSecrets.CloudBucketName ||
            CloudAccessKey != _originalSecrets.CloudAccessKey ||
            CloudSecretKey != _originalSecrets.CloudSecretKey ||
            CloudServiceUrl != _originalSecrets.CloudServiceUrl ||
            CloudRegion != _originalSecrets.CloudRegion;

        HasObservabilityChanges =
            GrafanaOtlpEndpoint != _originalSecrets.GrafanaOtlpEndpoint ||
            GrafanaOtlpHeaders != _originalSecrets.GrafanaOtlpHeaders ||
            PyroscopeServerAddress != _originalSecrets.PyroscopeServerAddress ||
            PyroscopeBasicAuthUser != _originalSecrets.PyroscopeBasicAuthUser ||
            PyroscopeBasicAuthPassword != _originalSecrets.PyroscopeBasicAuthPassword ||
            PyroscopePort != _originalSecrets.PyroscopePort;

        HasOAuthChanges =
            DiscordClientId != _originalSecrets.DiscordClientId ||
            DiscordClientSecret != _originalSecrets.DiscordClientSecret ||
            TwitchClientId != _originalSecrets.TwitchClientId ||
            TwitchClientSecret != _originalSecrets.TwitchClientSecret;

        HasNetworkChanges =
            ChatServerHost != _originalSecrets.ChatServerHost ||
            ChatServerPublicHost != _originalSecrets.ChatServerPublicHost ||
            ChatServerClientPort != _originalSecrets.ChatServerClientPort ||
            ChatServerPublicClientPort != _originalSecrets.ChatServerPublicClientPort ||
            ChatServerMatchServerPort != _originalSecrets.ChatServerMatchServerPort ||
            ChatServerMatchServerManagerPort != _originalSecrets.ChatServerMatchServerManagerPort ||
            WebPortalHttpPort != _originalSecrets.WebPortalHttpPort ||
            WebPortalHttpsPort != _originalSecrets.WebPortalHttpsPort ||
            WebPortalApiHttpPort != _originalSecrets.WebPortalApiHttpPort ||
            WebPortalApiHttpsPort != _originalSecrets.WebPortalApiHttpsPort ||
            DbContextPort != _originalSecrets.DatabaseContextPort ||
            DistributedCachePort != _originalSecrets.DistributedCachePort ||
            DistributedCacheSecondaryPort != _originalSecrets.DistributedCacheSecondaryPort ||
            DistributedCacheDashboardPort != _originalSecrets.DistributedCacheDashboardPort ||
            MatchmakingPlayersPerTeam != _originalSecrets.MatchmakingPlayersPerTeam ||
            MatchmakingIntervalMs != _originalSecrets.MatchmakingIntervalMs ||
            CompelPingPort != _originalSecrets.CompelPingPort ||
            GameServerPublicIp != _originalSecrets.GameServerPublicIp ||
            GameServerInstancePorts != _originalSecrets.GameServerInstancePorts;

        HasStoreChanges = UnlockAllItems != _originalSecrets.UnlockAllItems;

        HasChanges =
            HasMasterServerChanges ||
            HasDatabaseChanges ||
            HasSupabaseChanges ||
            HasSecurityChanges ||
            HasPathChanges ||
            HasReplayChanges ||
            HasObservabilityChanges ||
            HasOAuthChanges ||
            HasNetworkChanges ||
            HasStoreChanges;
    }

    [RelayCommand]
    public void DiscardChanges()
    {
        ApplySecretsToFields(_originalSecrets);
        UpdateGeneratedContainerCommand();

        CheckForChanges();
        StatusBannerText = "Changes discarded. Restored original configuration.";
        IsStatusSuccess = true;
        IsStatusBannerVisible = true;
    }

    partial void OnUnlockAllItemsChanged(bool value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnMasterServerAddressChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnInfrastructureGatewayChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnMasterServerPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnChatServerHostChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnChatServerPublicHostChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnChatServerClientPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnChatServerPublicClientPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnChatServerMatchServerPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnChatServerMatchServerManagerPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnWebPortalHttpPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnWebPortalHttpsPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnWebPortalApiHttpPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnWebPortalApiHttpsPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDbContextPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDistributedCachePortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDistributedCacheSecondaryPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDistributedCacheDashboardPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnMatchmakingPlayersPerTeamChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnMatchmakingIntervalMsChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCompelPingPortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnGameServerPublicIpChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnGameServerInstancePortsChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnPyroscopePortChanged(int value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDatabaseProviderChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsSupabaseDatabase));
        OnPropertyChanged(nameof(IsPostgreSqlDatabase));
        OnPropertyChanged(nameof(IsSupabaseIntegrated));
        CheckForChanges();
    }

    partial void OnSupabaseUrlChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsSupabaseIntegrated));
        CheckForChanges();
    }

    partial void OnSupabaseAnonKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnSupabaseServiceRoleKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnSupabaseDatabaseConnectionStringChanged(string value)
    {
        if (!_isSyncingSupabasePassword)
        {
            _isSyncingSupabasePassword = true;
            try
            {
                string extracted = TryExtractPasswordFromConnectionString(value);
                if (extracted is "[YOUR-PASSWORD]" or "[password]" or "<password>" &&
                    !string.IsNullOrEmpty(SupabaseDatabasePassword))
                {
                    SupabaseDatabaseConnectionString =
                        SplicePasswordIntoConnectionString(value, SupabaseDatabasePassword);
                }
                else
                {
                    SupabaseDatabasePassword = extracted;
                }
            }
            finally
            {
                _isSyncingSupabasePassword = false;
            }
        }

        CheckForChanges();
    }

    partial void OnSupabaseDatabasePasswordChanged(string value)
    {
        if (_isSyncingSupabasePassword) return;

        if (!string.IsNullOrWhiteSpace(SupabaseDatabaseConnectionString))
        {
            _isSyncingSupabasePassword = true;
            try
            {
                SupabaseDatabaseConnectionString =
                    SplicePasswordIntoConnectionString(SupabaseDatabaseConnectionString, value ?? string.Empty);
            }
            finally
            {
                _isSyncingSupabasePassword = false;
            }
        }

        CheckForChanges();
    }

    public static string TryExtractPasswordFromConnectionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            int schemeSep = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeSep < 0) return string.Empty;

            int restStart = schemeSep + 3;
            int authorityEnd = trimmed.Length;
            for (int i = restStart; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch is '/' or '?' or '#')
                {
                    authorityEnd = i;
                    break;
                }
            }

            string authority = trimmed[restStart..authorityEnd];
            int lastAt = authority.LastIndexOf('@');
            if (lastAt < 0) return string.Empty;

            string userInfo = authority[..lastAt];
            int firstColon = userInfo.IndexOf(':');
            if (firstColon < 0) return string.Empty;

            string rawPassword = userInfo[(firstColon + 1)..];
            try
            {
                return Uri.UnescapeDataString(rawPassword);
            }
            catch (UriFormatException)
            {
                return rawPassword;
            }
        }

        try
        {
            string normalized = ConfigurationExtensions.NormalizePostgreSqlConnectionString(trimmed);
            NpgsqlConnectionStringBuilder csb = new(normalized);
            return csb.Password ?? string.Empty;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SecretsConfig] TryExtractPasswordFromConnectionString non-URI parse skipped: {ex.GetType().Name}");
            return string.Empty;
        }
    }

    public static string SplicePasswordIntoConnectionString(string? existing, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(existing)) return string.Empty;

        string trimmed = existing.Trim();
        if (trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            int schemeSep = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeSep < 0) return trimmed;

            int restStart = schemeSep + 3;
            int authorityEnd = trimmed.Length;
            for (int i = restStart; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch is '/' or '?' or '#')
                {
                    authorityEnd = i;
                    break;
                }
            }

            string prefix = trimmed[..restStart];
            string authority = trimmed[restStart..authorityEnd];
            string suffix = trimmed[authorityEnd..];
            string encodedPassword = Uri.EscapeDataString(newPassword ?? string.Empty);

            int lastAt = authority.LastIndexOf('@');
            if (lastAt < 0)
            {
                return $"{prefix}postgres:{encodedPassword}@{authority}{suffix}";
            }

            string userInfo = authority[..lastAt];
            string hostPort = authority[(lastAt + 1)..];
            int firstColon = userInfo.IndexOf(':');
            string username = firstColon >= 0 ? userInfo[..firstColon] : userInfo;
            return $"{prefix}{username}:{encodedPassword}@{hostPort}{suffix}";
        }

        try
        {
            string normalized = ConfigurationExtensions.NormalizePostgreSqlConnectionString(trimmed);
            NpgsqlConnectionStringBuilder csb = new(normalized) { Password = newPassword ?? string.Empty };
            return csb.ConnectionString;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SecretsConfig] SplicePasswordIntoConnectionString non-URI parse skipped: {ex.GetType().Name}");
            return trimmed;
        }
    }

    partial void OnSupabaseStorageBucketChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnSupabaseS3AccessKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnSupabaseS3SecretKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnSupabaseS3RegionChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDiscordClientIdChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsDiscordConfigured));
        CheckForChanges();
    }

    partial void OnDiscordClientSecretChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsDiscordConfigured));
        CheckForChanges();
    }

    partial void OnTwitchClientIdChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsTwitchConfigured));
        CheckForChanges();
    }

    partial void OnTwitchClientSecretChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsTwitchConfigured));
        CheckForChanges();
    }

    partial void OnDatabasePasswordChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDistributedCachePasswordChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnJwtSigningKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnSendGridApiKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnProjectPathChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCompelPathChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCompelAuthenticationTokenChanged(string value)
    {
        _ = value;
        CheckForChanges();
        UpdateGeneratedContainerCommand();
    }

    partial void OnHostAccountUserNameChanged(string value)
    {
        _ = value;
        CheckForChanges();
        UpdateGeneratedContainerCommand();
    }

    partial void OnHostAccountPasswordChanged(string value)
    {
        _ = value;
        CheckForChanges();
        UpdateGeneratedContainerCommand();
    }

    partial void OnCompelGatewayChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCompelInstancesChanged(int value)
    {
        _ = value;
        CheckForChanges();
        UpdateGeneratedContainerCommand();
    }

    partial void OnCompelPriorityModeChanged(string value)
    {
        _ = value;
        CheckForChanges();
        UpdateGeneratedContainerCommand();
    }

    partial void OnSelectedContainerPlatformChanged(string value)
    {
        _ = value;
        UpdateGeneratedContainerCommand();
    }

    partial void OnRevealContainerSecretsInCommandChanged(bool value)
    {
        _ = value;
        UpdateGeneratedContainerCommand();
    }

    partial void OnRevealHostAccountPasswordChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(HostAccountPasswordMask));
    }

    partial void OnGameClientPathChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnReplayStorageProviderChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsCloudStorage));
        OnPropertyChanged(nameof(IsLocalDiskStorage));
        CheckForChanges();
    }

    partial void OnLocalReplayDirectoryChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCloudBucketNameChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCloudAccessKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCloudSecretKeyChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCloudServiceUrlChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnCloudRegionChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnGrafanaOtlpEndpointChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnGrafanaOtlpHeadersChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnPyroscopeServerAddressChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnPyroscopeBasicAuthUserChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnPyroscopeBasicAuthPasswordChanged(string value)
    {
        _ = value;
        CheckForChanges();
    }

    partial void OnDocModalUrlChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasDocModalUrl));
    }

    partial void OnDocModalSecondaryUrlChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasDocModalSecondaryUrl));
    }

    partial void OnDocModalCopyTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasDocModalCopyText));
    }

    partial void OnDocModalCopySuccessTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasDocModalCopySuccess));
    }

    partial void OnRevealMasterServerAddressChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(MasterServerAddressMask));
    }

    partial void OnRevealDiscordClientIdChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(DiscordClientIdMask));
    }

    partial void OnRevealDiscordClientSecretChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(DiscordClientSecretMask));
    }

    partial void OnRevealTwitchClientIdChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TwitchClientIdMask));
    }

    partial void OnRevealTwitchClientSecretChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TwitchClientSecretMask));
    }

    partial void OnRevealDatabasePasswordChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(DatabasePasswordMask));
    }

    partial void OnRevealDistributedCachePasswordChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(DistributedCachePasswordMask));
    }

    partial void OnRevealJwtSigningKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(JwtSigningKeyMask));
    }

    partial void OnRevealSendGridApiKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SendGridApiKeyMask));
    }

    partial void OnRevealCompelPathChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CompelPathMask));
    }

    partial void OnRevealGameClientPathChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(GameClientPathMask));
    }

    partial void OnRevealLocalReplayDirectoryChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(LocalReplayDirectoryMask));
    }

    partial void OnRevealCloudBucketNameChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CloudBucketNameMask));
    }

    partial void OnRevealCloudAccessKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CloudAccessKeyMask));
    }

    partial void OnRevealCloudSecretKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CloudSecretKeyMask));
    }

    partial void OnRevealCloudServiceUrlChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CloudServiceUrlMask));
    }

    partial void OnRevealCloudRegionChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CloudRegionMask));
    }

    partial void OnRevealGrafanaOtlpEndpointChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(GrafanaOtlpEndpointMask));
    }

    partial void OnRevealGrafanaOtlpHeadersChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(GrafanaOtlpHeadersMask));
    }

    partial void OnRevealPyroscopeServerAddressChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(PyroscopeServerAddressMask));
    }

    partial void OnRevealPyroscopeBasicAuthUserChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(PyroscopeBasicAuthUserMask));
    }

    partial void OnRevealDatabaseProviderChanged(bool value)
    {
        _ = value;
    }

    partial void OnRevealSupabaseUrlChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseUrlMask));
        OnPropertyChanged(nameof(SupabaseS3EndpointWatermark));
    }

    partial void OnRevealSupabaseAnonKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseAnonKeyMask));
    }

    partial void OnRevealSupabaseServiceRoleKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseServiceRoleKeyMask));
    }

    partial void OnRevealSupabaseDatabaseConnectionStringChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseDatabaseConnectionStringMask));
    }

    partial void OnRevealSupabaseDatabasePasswordChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseDatabasePasswordMask));
    }

    partial void OnRevealSupabaseStorageBucketChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseStorageBucketMask));
    }

    partial void OnRevealSupabaseS3AccessKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseS3AccessKeyMask));
    }

    partial void OnRevealSupabaseS3SecretKeyChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseS3SecretKeyMask));
    }

    partial void OnRevealSupabaseS3RegionChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SupabaseS3RegionMask));
    }

    partial void OnRevealPyroscopePasswordChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(PyroscopePasswordMask));
    }

    [RelayCommand]
    public void ToggleReveal(string field)
    {
        switch (field.ToLowerInvariant())
        {
            case "master-server-address":
            case "masterserver":
                RevealMasterServerAddress = !RevealMasterServerAddress;
                break;
            case "discord-client-id":
                RevealDiscordClientId = !RevealDiscordClientId;
                break;
            case "discord-client-secret":
                RevealDiscordClientSecret = !RevealDiscordClientSecret;
                break;
            case "twitch-client-id":
                RevealTwitchClientId = !RevealTwitchClientId;
                break;
            case "twitch-client-secret":
                RevealTwitchClientSecret = !RevealTwitchClientSecret;
                break;
            case "database-password":
                RevealDatabasePassword = !RevealDatabasePassword;
                break;
            case "distributed-cache-password":
                RevealDistributedCachePassword = !RevealDistributedCachePassword;
                break;
            case "jwt-signing-key":
                RevealJwtSigningKey = !RevealJwtSigningKey;
                break;
            case "sendgrid-api-key":
                RevealSendGridApiKey = !RevealSendGridApiKey;
                break;
            case "compel-path":
                RevealCompelPath = !RevealCompelPath;
                break;
            case "compel-token":
                RevealCompelAuthenticationToken = !RevealCompelAuthenticationToken;
                break;
            case "host-password":
            case "hostaccount-password":
            case "compel-password":
                RevealHostAccountPassword = !RevealHostAccountPassword;
                break;
            case "game-client-path":
                RevealGameClientPath = !RevealGameClientPath;
                break;
            case "local-replay-directory":
                RevealLocalReplayDirectory = !RevealLocalReplayDirectory;
                break;
            case "cloud-bucket-name":
                RevealCloudBucketName = !RevealCloudBucketName;
                break;
            case "cloud-access-key":
                RevealCloudAccessKey = !RevealCloudAccessKey;
                break;
            case "cloud-secret-key":
                RevealCloudSecretKey = !RevealCloudSecretKey;
                break;
            case "cloud-service-url":
                RevealCloudServiceUrl = !RevealCloudServiceUrl;
                break;
            case "cloud-region":
                RevealCloudRegion = !RevealCloudRegion;
                break;
            case "grafana-otlp-endpoint":
                RevealGrafanaOtlpEndpoint = !RevealGrafanaOtlpEndpoint;
                break;
            case "grafana-otlp-headers":
                RevealGrafanaOtlpHeaders = !RevealGrafanaOtlpHeaders;
                break;
            case "pyroscope-server-address":
                RevealPyroscopeServerAddress = !RevealPyroscopeServerAddress;
                break;
            case "pyroscope-basic-auth-user":
                RevealPyroscopeBasicAuthUser = !RevealPyroscopeBasicAuthUser;
                break;
            case "pyroscope-password":
            case "pyroscope-basic-auth-password":
                RevealPyroscopePassword = !RevealPyroscopePassword;
                break;
            case "database-provider":
                RevealDatabaseProvider = !RevealDatabaseProvider;
                break;
            case "supabase-url":
                RevealSupabaseUrl = !RevealSupabaseUrl;
                break;
            case "supabase-anon-key":
                RevealSupabaseAnonKey = !RevealSupabaseAnonKey;
                break;
            case "supabase-service-role-key":
                RevealSupabaseServiceRoleKey = !RevealSupabaseServiceRoleKey;
                break;
            case "supabase-db-connection-string":
            case "supabase-database-connection-string":
                RevealSupabaseDatabaseConnectionString = !RevealSupabaseDatabaseConnectionString;
                break;
            case "supabase-db-password":
            case "supabase-database-password":
                RevealSupabaseDatabasePassword = !RevealSupabaseDatabasePassword;
                break;
            case "supabase-storage-bucket":
                RevealSupabaseStorageBucket = !RevealSupabaseStorageBucket;
                break;
            case "supabase-s3-access-key":
                RevealSupabaseS3AccessKey = !RevealSupabaseS3AccessKey;
                break;
            case "supabase-s3-secret-key":
                RevealSupabaseS3SecretKey = !RevealSupabaseS3SecretKey;
                break;
            case "supabase-s3-region":
                RevealSupabaseS3Region = !RevealSupabaseS3Region;
                break;
            case "internal-service-key":
                RevealInternalServiceKey = !RevealInternalServiceKey;
                break;
            case "ipc-auth-key":
                RevealIpcAuthKey = !RevealIpcAuthKey;
                break;
            case "alert-webhook-url":
                RevealAlertWebhookUrl = !RevealAlertWebhookUrl;
                break;
        }
    }

    public string ResolveSupabaseDashboardUrl()
    {
        string url = "https://supabase.com/dashboard";

        if (!string.IsNullOrWhiteSpace(SupabaseUrl))
            try
            {
                string trimmed = SupabaseUrl.Trim();
                if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    trimmed = "https://" + trimmed;

                Uri uri = new(trimmed);
                string host = uri.Host;
                string[] parts = host.Split('.');
                if (parts.Length >= 3 && parts[1].Equals("supabase", StringComparison.OrdinalIgnoreCase))
                {
                    string projectRef = parts[0];
                    url = $"https://supabase.com/dashboard/project/{projectRef}";
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SecretsConfig] ResolveSupabaseDashboardUrl fallback: {ex.GetType().Name}");
            }

        return url;
    }

    [RelayCommand]
    public void OpenSupabaseDashboard()
    {
        OpenExternalUrl(ResolveSupabaseDashboardUrl());
    }

    [RelayCommand]
    public void OpenSupabaseDocs()
    {
        ShowDoc("supabase");
    }

    [RelayCommand]
    public void DismissSupabaseStatus()
    {
        IsSupabaseStatusVisible = false;
    }

    [RelayCommand]
    public async Task TestSupabaseConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(SupabaseUrl) && string.IsNullOrWhiteSpace(SupabaseDatabaseConnectionString))
        {
            SupabaseStatusText = "⚠️ Please enter a Supabase URL or Database Connection String first!";
            IsSupabaseStatusSuccess = false;
            IsSupabaseStatusVisible = true;
            return;
        }

        try
        {
            SupabaseStatusText = "⚡ Testing Supabase REST, Storage & PostgreSQL authentication...";
            IsSupabaseStatusSuccess = true;
            IsSupabaseStatusVisible = true;

            List<string> results = [];

            if (!string.IsNullOrWhiteSpace(SupabaseUrl))
            {
                string cleanUrl = SupabaseUrl.TrimEnd('/');
                string apiKey = !string.IsNullOrWhiteSpace(SupabaseAnonKey)
                    ? SupabaseAnonKey.Trim()
                    : !string.IsNullOrWhiteSpace(SupabaseServiceRoleKey)
                        ? SupabaseServiceRoleKey.Trim()
                        : string.Empty;

                // 1. Test REST API
                Stopwatch restSw = Stopwatch.StartNew();
                string healthUrl = $"{cleanUrl}/rest/v1/";
                using HttpRequestMessage restReq = new(HttpMethod.Get, healthUrl);
                if (!string.IsNullOrWhiteSpace(apiKey)) restReq.Headers.TryAddWithoutValidation("apikey", apiKey);

                using HttpResponseMessage restResp = await _httpClient.SendAsync(restReq);
                restSw.Stop();
                results.Add($"REST API: {restSw.ElapsedMilliseconds}ms (HTTP {(int)restResp.StatusCode})");

                // 2. Test Storage API & Auto-Provision Standard Buckets
                Stopwatch storageSw = Stopwatch.StartNew();
                string storageUrl = $"{cleanUrl}/storage/v1/bucket";
                string authKey = !string.IsNullOrWhiteSpace(SupabaseServiceRoleKey)
                    ? SupabaseServiceRoleKey.Trim()
                    : apiKey;

                using HttpRequestMessage storageReq = new(HttpMethod.Get, storageUrl);
                if (!string.IsNullOrWhiteSpace(authKey))
                {
                    storageReq.Headers.TryAddWithoutValidation("apikey", authKey);
                    storageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authKey);
                }

                using HttpResponseMessage storageResp = await _httpClient.SendAsync(storageReq);
                string storageBody = await storageResp.Content.ReadAsStringAsync();

                string[] standardBuckets = ["replays", "avatars", "clans", "mods"];
                List<string> readyBuckets = [];

                foreach (string bucket in standardBuckets)
                    if (storageBody.Contains($"\"{bucket}\"", StringComparison.OrdinalIgnoreCase))
                    {
                        readyBuckets.Add(bucket);
                    }
                    else if (!string.IsNullOrWhiteSpace(authKey))
                    {
                        // Auto-create missing bucket
                        using HttpRequestMessage createReq = new(HttpMethod.Post, storageUrl);
                        createReq.Headers.TryAddWithoutValidation("apikey", authKey);
                        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authKey);
                        string payload = $"{{\"id\":\"{bucket}\",\"name\":\"{bucket}\",\"public\":true}}";
                        createReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using HttpResponseMessage createResp = await _httpClient.SendAsync(createReq);
                        if (createResp.IsSuccessStatusCode) readyBuckets.Add(bucket);
                    }

                storageSw.Stop();
                results.Add($"Storage: {storageSw.ElapsedMilliseconds}ms ({readyBuckets.Count}/4 buckets active)");
            }

            if (!string.IsNullOrWhiteSpace(SupabaseDatabaseConnectionString))
            {
                (bool pgOk, long pgMs, string pgMessage) =
                    await _postgresProbe(SupabaseDatabaseConnectionString, CancellationToken.None);
                if (!pgOk)
                {
                    results.Add(pgMessage);
                    SupabaseStatusText =
                        $"⚠️ Supabase Database Authentication Failed: {string.Join(" | ", results)}. Verify 'Supabase / PostgreSQL Database Password' and click Save.";
                    IsSupabaseStatusSuccess = false;
                    IsSupabaseStatusVisible = true;
                    return;
                }

                results.Add(string.IsNullOrWhiteSpace(pgMessage) ? $"Postgres Auth OK ({pgMs}ms)" : pgMessage);
            }

            SupabaseStatusText = $"✓ Supabase Connected! ({string.Join(", ", results)})";
            IsSupabaseStatusSuccess = true;
            IsSupabaseStatusVisible = true;
        }
        catch (Exception ex)
        {
            SupabaseStatusText = $"⚠️ Supabase Connection Test Failed: {ex.Message}";
            IsSupabaseStatusSuccess = false;
            IsSupabaseStatusVisible = true;
        }
    }

    private static async Task<(bool Success, long ElapsedMs, string Message)> ProbePostgresConnectionAsync(
        string rawConnectionString,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string normalized = ConfigurationExtensions.NormalizePostgreSqlConnectionString(rawConnectionString);
            NpgsqlConnectionStringBuilder csb = new(normalized)
            {
                Timeout = 6,
                CommandTimeout = 6,
                Pooling = false
            };

            string extractedPassword = TryExtractPasswordFromConnectionString(rawConnectionString);
            if (!string.IsNullOrEmpty(extractedPassword))
            {
                csb.Password = extractedPassword;
            }

            await using NpgsqlConnection conn = new(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            await using NpgsqlCommand cmd = new("SELECT 1", conn);
            _ = await cmd.ExecuteScalarAsync(cancellationToken);
            sw.Stop();
            Trace.WriteLine(
                $"[SecretsConfig] Postgres probe succeeded host={csb.Host} port={csb.Port} elapsedMs={sw.ElapsedMilliseconds}");
            return (true, sw.ElapsedMilliseconds, $"Postgres Auth OK ({sw.ElapsedMilliseconds}ms)");
        }
        catch (PostgresException pgEx)
        {
            sw.Stop();
            Trace.WriteLine(
                $"[SecretsConfig] Postgres probe failed sqlState={pgEx.SqlState} elapsedMs={sw.ElapsedMilliseconds} message={pgEx.MessageText}");
            return (false, sw.ElapsedMilliseconds, $"Postgres auth failed (SQLSTATE {pgEx.SqlState}): {pgEx.MessageText}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Trace.WriteLine(
                $"[SecretsConfig] Postgres probe error type={ex.GetType().Name} elapsedMs={sw.ElapsedMilliseconds} message={ex.Message}");
            return (false, sw.ElapsedMilliseconds, $"Postgres connection failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static async Task<IStorageFolder?> ResolveStartFolderAsync(IStorageProvider storage, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            string trimmed = path.Trim();
            if (File.Exists(trimmed))
            {
                string? parent = Path.GetDirectoryName(trimmed);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    return await storage.TryGetFolderFromPathAsync(parent);
            }
            else if (Directory.Exists(trimmed))
            {
                return await storage.TryGetFolderFromPathAsync(trimmed);
            }
        }
        catch
        {
            // Fallback to default start location
        }

        return null;
    }

    [RelayCommand]
    public async Task BrowseCompelPathAsync()
    {
        if (Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.StorageProvider: { } storage
                }
            })
        {
            IStorageFolder? startFolder = await ResolveStartFolderAsync(storage, CompelPath);
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select COMPEL Game Server Directory",
                SuggestedStartLocation = startFolder,
                AllowMultiple = false
            });

            if (folders.Count > 0) CompelPath = folders[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    public async Task BrowseGameClientPathAsync()
    {
        if (Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.StorageProvider: { } storage
                }
            })
        {
            IStorageFolder? startFolder = await ResolveStartFolderAsync(storage, GameClientPath);
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select HoN / WILLOWMAKER Game Client Directory",
                SuggestedStartLocation = startFolder,
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                GameClientPath = folders[0].Path.LocalPath;
                if (_assetService != null) _ = _assetService.EnsureAssetsExtractedAsync(GameClientPath);
            }
        }
    }

    [RelayCommand]
    public async Task BrowseReplayDirectoryAsync()
    {
        if (Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.StorageProvider: { } storage
                }
            })
        {
            IStorageFolder? startFolder = await ResolveStartFolderAsync(storage, LocalReplayDirectory);
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Replay Storage Directory",
                SuggestedStartLocation = startFolder,
                AllowMultiple = false
            });

            if (folders.Count > 0) LocalReplayDirectory = folders[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    public void LaunchGameClient()
    {
        if (string.IsNullOrWhiteSpace(GameClientPath)) return;

        string solutionRoot = SecretsManagerService.FindSolutionRoot();
        string clientDir = Directory.Exists(GameClientPath) ? GameClientPath : (Path.GetDirectoryName(GameClientPath) ?? Path.Combine(solutionRoot, "games", "client"));

        string willowRelease = Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Release", "net11.0", "WILLOWMAKER.exe");
        string willowDebug = Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Debug", "net11.0", "WILLOWMAKER.exe");

        string exe = string.Empty;
        if (File.Exists(willowRelease) && (!File.Exists(willowDebug) || File.GetLastWriteTimeUtc(willowRelease) >= File.GetLastWriteTimeUtc(willowDebug)))
            exe = willowRelease;
        else if (File.Exists(willowDebug))
            exe = willowDebug;
        else if (Directory.Exists(clientDir))
        {
            string hon = Path.Combine(clientDir, "hon_x64.exe");
            if (File.Exists(hon)) exe = hon;
        }
        else if (File.Exists(GameClientPath))
        {
            exe = GameClientPath;
        }

        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = clientDir,
                    UseShellExecute = true
                });
            }
            catch
            {
                Trace.WriteLine($"[SecretsConfig] Failed to launch game client ({exe}).");
            }
    }

    [RelayCommand]
    public void OpenDocUrl()
    {
        if (!string.IsNullOrEmpty(DocModalUrl)) OpenExternalUrl(DocModalUrl);
    }

    [RelayCommand]
    public void OpenDocSecondaryUrl()
    {
        if (!string.IsNullOrEmpty(DocModalSecondaryUrl)) OpenExternalUrl(DocModalSecondaryUrl);
    }

    [RelayCommand]
    public void OpenExternalUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", url);
        }
        catch
        {
            // Ignore if default browser cannot be spawned directly
        }
    }

    [RelayCommand]
    public void SetPresetMasterServerAddress(string? preset)
    {
        if (!string.IsNullOrWhiteSpace(preset)) MasterServerAddress = preset.Trim();
    }

    [RelayCommand]
    public async Task CopyMasterServerAddressAsync()
    {
        if (Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: not null
                } desktop
            })
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(MasterServerAddress);
            StatusBannerText = $"✓ Copied Master Server Address ({MasterServerAddress}) to clipboard!";
            IsStatusSuccess = true;
            IsStatusBannerVisible = true;
            await Task.Delay(3000);
            IsStatusBannerVisible = false;
        }
    }

    [RelayCommand]
    public void OpenMasterServerInBrowser()
    {
        string url = string.IsNullOrWhiteSpace(MasterServerAddress)
            ? "http://localhost:55555"
            : MasterServerAddress.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = $"http://{url}";

        string swaggerUrl = $"{url.TrimEnd('/')}/swagger";
        OpenExternalUrl(swaggerUrl);
    }

    [RelayCommand]
    public void OpenDiscordDeveloperPortal()
    {
        OpenExternalUrl("https://discord.com/developers/applications");
    }

    [RelayCommand]
    public async Task CopyDiscordRedirectUriAsync()
    {
        if (Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: not null
                } desktop
            })
        {
            await desktop.MainWindow.Clipboard.SetTextAsync("https://localhost:5557/signin-discord");
            StatusBannerText = "✓ Copied Discord Redirect URI (https://localhost:5557/signin-discord) to clipboard!";
            IsStatusSuccess = true;
            IsStatusBannerVisible = true;
            await Task.Delay(3000);
            IsStatusBannerVisible = false;
        }
    }

    [RelayCommand]
    public void OpenTwitchDeveloperConsole()
    {
        OpenExternalUrl("https://dev.twitch.tv/console/apps");
    }

    [RelayCommand]
    public async Task CopyTwitchRedirectUriAsync()
    {
        if (Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: not null
                } desktop
            })
        {
            await desktop.MainWindow.Clipboard.SetTextAsync("https://localhost:5557/signin-twitch");
            StatusBannerText = "✓ Copied Twitch Redirect URI (https://localhost:5557/signin-twitch) to clipboard!";
            IsStatusSuccess = true;
            IsStatusBannerVisible = true;
            await Task.Delay(3000);
            IsStatusBannerVisible = false;
        }
    }

    [RelayCommand]
    public async Task CopyDocTextAsync()
    {
        if (!string.IsNullOrEmpty(DocModalCopyText) &&
            Application.Current is
            {
                ApplicationLifetime: IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: not null
                } desktop
            })
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(DocModalCopyText);
            DocModalCopySuccessText = "✓ Copied to clipboard!";
            await Task.Delay(2500);
            DocModalCopySuccessText = string.Empty;
        }
    }

    [RelayCommand]
    public void ShowDoc(string section)
    {
        DocModalCopySuccessText = string.Empty;
        DocModalUrl = string.Empty;
        DocModalUrlLabel = string.Empty;
        DocModalSecondaryUrl = string.Empty;
        DocModalSecondaryUrlLabel = string.Empty;
        DocModalCopyText = string.Empty;
        DocModalCopyLabel = string.Empty;

        switch (section.ToLowerInvariant())
        {
            case "masterserver":
            case "master-server":
            case "gateway":
                DocModalTitle = "Master Server & Gateway Setup (ELI5 Guide)";
                DocModalBadge = "GATEWAY";
                DocModalUrl = "http://localhost:55555/swagger";
                DocModalUrlLabel = "🌐 Open Local Master Server Swagger";
                DocModalContent =
                    "• What is the Master Server Address?\n" +
                    "  The Master Server handles account login authentication (SRP), stats tracking, match tokens, CDN updates, and patcher manifests.\n\n" +
                    "• Common Presets:\n" +
                    "  👉 Localhost: http://localhost:55555 (default for local development with ASPIRE)\n" +
                    "  👉 Loopback: http://127.0.0.1:55555\n" +
                    "  👉 KONGOR: https://api.kongor.net\n" +
                    "  👉 NEWBS: https://masterserver.newbs.io\n\n" +
                    "• ☁️ Cloudflare & Public Domain Forwarding Guide:\n" +
                    "  - Cloudflare's orange-cloud proxy only supports standard web ports (80/443). Custom port 55555 cannot be proxied directly through Cloudflare DNS proxy.\n" +
                    "  - Best Option (Cloudflare Tunnel): Run a free Cloudflare Tunnel (cloudflared tunnel --url http://localhost:55555) to route https://api.kongor.net securely to your local machine with zero router port forwarding required!\n" +
                    "  - Local Development: For testing Swagger locally, use http://localhost:55555/swagger (or select the 'Localhost' preset button above).\n\n" +
                    "🔒 Persistence: This address is saved into the unified .NET User Secrets store ('MasterServer:Address' and 'Infrastructure:Gateway') and shared across all NEXUS microservices.";
                break;

            case "discord":
                DocModalTitle = "Discord OAuth2 Setup (ELI5 Guide)";
                DocModalBadge = "STEP-BY-STEP";
                DocModalUrl = "https://discord.com/developers/applications";
                DocModalUrlLabel = "🌐 1. Open Discord Developer Portal";
                DocModalCopyText = "https://localhost:5557/signin-discord";
                DocModalCopyLabel = "📋 2. Copy Redirect URI (https://localhost:5557/signin-discord)";
                DocModalContent =
                    "1. Click '🌐 1. Open Discord Developer Portal' below.\n\n" +
                    "2. Click the 'New Application' button in the top right, name it (e.g. 'NEXUS' or 'Project KONGOR'), and accept terms.\n\n" +
                    "3. In the left sidebar, click 'OAuth2' -> 'General'.\n\n" +
                    "4. Under 'Redirects', click 'Add Redirect' and paste the Redirect URI (click '📋 2. Copy Redirect URI' below to copy it).\n\n" +
                    "5. Click 'Save Changes' at the bottom of the Discord portal.\n\n" +
                    "6. Under 'Client Information' on the same page, copy your 'Client ID' and click 'Reset Secret' to get your 'Client Secret'. Paste both into the vault!\n\n" +
                    "💡 Local Dev Mode: If you leave these fields empty, the web portal automatically enables mock registration so you can test without Discord credentials.";
                break;

            case "twitch":
                DocModalTitle = "Twitch OAuth2 & Streamer Integration Setup (ELI5 Guide)";
                DocModalBadge = "STEP-BY-STEP";
                DocModalUrl = "https://dev.twitch.tv/console/apps";
                DocModalUrlLabel = "🌐 1. Open Twitch Developer Console";
                DocModalCopyText = "https://localhost:5557/signin-twitch";
                DocModalCopyLabel = "📋 2. Copy Redirect URI (https://localhost:5557/signin-twitch)";
                DocModalContent =
                    "1. Click '🌐 1. Open Twitch Developer Console' below.\n\n" +
                    "2. Click '+ Register Your Application'.\n\n" +
                    "3. Enter a Name (e.g. 'Project KONGOR' or 'NEXUS Streamer Sync').\n\n" +
                    "4. Under 'OAuth Redirect URLs', paste the Redirect URI (click '📋 2. Copy Redirect URI' below to copy it).\n\n" +
                    "5. Set Category to 'Game Integration' or 'Website Integration' and click 'Create'.\n\n" +
                    "6. Click 'Manage' on your new application, copy the 'Client ID', and click 'New Secret' to generate the 'Client Secret'.\n\n" +
                    "7. Paste both values into the Twitch card in this control panel and click 'Save & Apply Secrets'.";
                break;

            case "database":
                DocModalTitle = "Database & Cache Passwords (ELI5 Guide)";
                DocModalBadge = "INFRASTRUCTURE";
                DocModalContent =
                    "• SQL Server Database Password:\n" +
                    "  NEXUS stores all user accounts, match histories, clan rosters, player statistics, and tribunal reports in Microsoft SQL Server.\n\n" +
                    "• Redis Distributed Cache Password:\n" +
                    "  Redis is an ultra-fast in-memory cache used for live matchmaking queues, chat rooms, and SRP auth handshakes.\n\n" +
                    "👉 Quick Setup: For local development, click the '⚡ Reset to Recommended' button in the top right of this page to instantly fill in standard dev passwords with 100% compatibility!";
                break;

            case "security":
                DocModalTitle = "JWT Secret & SendGrid (ELI5 Guide)";
                DocModalBadge = "SECURITY";
                DocModalUrl = "https://app.sendgrid.com";
                DocModalUrlLabel = "🌐 Open SendGrid Dashboard (Optional)";
                DocModalContent =
                    "• JWT Signing Key:\n" +
                    "  This is a private secret passphrase used by the ZORGATH API to create and verify login tokens for the DAWNBRINGER Web Portal.\n" +
                    "  👉 Requirement: Must be at least 32 characters long for cryptographic security.\n\n" +
                    "• SendGrid API Key (Optional):\n" +
                    "  Used in production to send real email verification codes.\n" +
                    "  👉 For local development: Leave this blank! NEXUS automatically detects dev mode and prints mock email links directly into the terminal with zero errors.";
                break;

            case "compel":
            case "paths":
                DocModalTitle = "COMPEL & Game Client (ELI5 Guide)";
                DocModalBadge = "LOCAL PATHS";
                DocModalUrl = "https://github.com/mdelemen/COMPEL";
                DocModalUrlLabel = "🌐 Open COMPEL Project Repository";
                DocModalContent =
                    "• COMPEL Game Server Manager:\n" +
                    "  COMPEL is the game server supervisor. When a match starts, COMPEL automatically launches and supervises hon_x64 game server processes.\n" +
                    "  👉 How to set: Click '📁 Browse...' next to COMPEL Path and select your COMPEL folder.\n\n" +
                    "• Heroes of Newerth Client Directory:\n" +
                    "  This is your clean Heroes of Newerth game installation containing game resources (.s2z archives).\n" +
                    "  👉 How to set: Click '📁 Browse...' next to Game Client Directory and select your HoN folder (e.g. C:\\Program Files\\Heroes of Newerth).";
                break;

            case "replay":
                DocModalTitle = "Match Replay Storage (ELI5 Guide)";
                DocModalBadge = "STORAGE";
                DocModalUrl = "https://dash.cloudflare.com";
                DocModalUrlLabel = "🌐 1. Open Cloudflare R2 Console (Free Egress)";
                DocModalSecondaryUrl = "https://aws.amazon.com/s3";
                DocModalSecondaryUrlLabel = "🌐 2. Open AWS S3 Console";
                DocModalContent =
                    "• LocalDisk (Recommended for Local Dev):\n" +
                    "  Match replays (.honreplay) are saved directly on your hard drive.\n" +
                    "  👉 Leave the path blank to use the default 'replays/' folder, or click '📁 Browse...' to choose any folder on your machine.\n\n" +
                    "• Cloud (Recommended for Production):\n" +
                    "  Automatically uploads replays to cloud object storage (Cloudflare R2, AWS S3, or Google Cloud Storage).\n" +
                    "  👉 Setup: Select 'Cloud' in the dropdown and enter your bucket name, access key, secret key, and endpoint URL.";
                break;

            case "grafana":
                DocModalTitle = "Grafana Cloud & Pyroscope (ELI5 Guide)";
                DocModalBadge = "TELEMETRY";
                DocModalUrl = "https://grafana.com";
                DocModalUrlLabel = "🌐 1. Open Grafana Cloud Console";
                DocModalSecondaryUrl = "https://grafana.com/docs/pyroscope/latest/";
                DocModalSecondaryUrlLabel = "🌐 2. Open Pyroscope Documentation";
                DocModalContent =
                    "• What is this?\n" +
                    "  Grafana gives you real-time live performance dashboards, error tracking, and continuous CPU profiling for all NEXUS microservices.\n\n" +
                    "• 2-Minute Free Setup:\n" +
                    "  1. Sign up for a free account at grafana.com (click '🌐 1. Open Grafana Cloud Console' below).\n" +
                    "  2. In your Grafana Cloud stack, find 'OpenTelemetry (OTLP)' and click 'Configure' -> 'Generate Token'.\n" +
                    "  3. Copy your OTLP Endpoint (e.g. https://otlp-gateway-prod-us-east-3.grafana.net/otlp) and Basic Auth Header into the fields below.\n" +
                    "  4. 🪄 NEXUS automatically extracts your Pyroscope Address, User ID, and Token from your Grafana OTLP Header!";
                break;

            case "supabase":
            case "postgresql":
            case "postgres":
                DocModalTitle = "⚡ Supabase Cloud & PostgreSQL Integration (ELI5 Guide)";
                DocModalBadge = "NEXT-LEVEL TECH";
                DocModalUrl = "https://supabase.com/dashboard";
                DocModalUrlLabel = "🌐 1. Open Supabase Dashboard (Free Cloud)";
                DocModalSecondaryUrl = "https://supabase.com/docs/guides/storage";
                DocModalSecondaryUrlLabel = "🌐 2. Supabase Storage Guide";
                DocModalContent =
                    "• Why Supabase & PostgreSQL for NEXUS?\n" +
                    "  PostgreSQL / Supabase provides a 100% free, high-performance, cross-platform relational database and global CDN replay storage that seamlessly replaces or coexists with SQL Server.\n\n" +
                    "• 🌐 IPv4 vs IPv6 & Connection Pooling Guide:\n" +
                    "  - Direct database URLs (db.<project-ref>.supabase.co:5432) are IPv6-only on Supabase's free tier.\n" +
                    "  - To connect seamlessly over standard IPv4 without paying for a dedicated IPv4 add-on, Supabase provides free AWS Pooler endpoints (aws-0-<region>.pooler.supabase.com)!\n" +
                    "  - Port 5432 (Session Mode) vs Port 6543 (Transaction Mode): EF Core ORM requires Session Mode (Port 5432). NEXUS automatically normalizes your connection string to use Port 5432 with IPv4 SSL session pooling!\n\n" +
                    "• 4-Step Setup (Takes < 2 minutes):\n\n" +
                    "  1️⃣ Open Supabase Dashboard:\n" +
                    "     Click '🌐 1. Open Supabase Dashboard' below (opens your org dashboard) and create or select your project.\n\n" +
                    "  2️⃣ Copy Project URL & API Keys:\n" +
                    "     In Project Settings -> API, copy your Project URL (e.g. https://xyz.supabase.co), anon public key, and service_role secret key into the fields below.\n\n" +
                    "  3️⃣ Copy Connection String (IPv4 Pooler):\n" +
                    "     In Project Settings -> Database -> Connection string -> URI, select 'Session mode' (URI: postgresql://postgres.<ref>:<password>@aws-0-<region>.pooler.supabase.com:5432/postgres). In the Database Provider dropdown, select 'Supabase'!\n\n" +
                    "  4️⃣ Create Replays Bucket:\n" +
                    "     In Storage, create a bucket named 'replays' and toggle 'Public bucket' to ON.\n\n" +
                    "🪄 Automatic Handling: MERRICK automatically initializes all schemas, tables, relations, and indexes in PostgreSQL / Supabase on launch!";
                break;

            default:
                DocModalTitle = "NEXUS Configuration Guide";
                DocModalBadge = "CONFIG";
                DocModalContent = "Configuration parameters are synchronized across .NET User Secrets stores.";
                break;
        }

        IsDocModalOpen = true;
    }

    [RelayCommand]
    public void CloseDocModal()
    {
        IsDocModalOpen = false;
    }

    [RelayCommand]
    public async Task LoadSecretsAsync()
    {
        NexusSecrets secrets = await _secretsManager.LoadSecretsAsync() ?? new NexusSecrets();
        _loadedSecrets = secrets.Clone();

        ApplySecretsToFields(secrets);

        // Snapshot loaded baseline to track delta changes
        _originalSecrets = BuildSecretsFromFields();

        CheckForChanges();
        UpdateGeneratedContainerCommand();
        RefreshSetupChecklist();
        await RefreshDriftAsync();
    }

    [RelayCommand]
    public async Task SaveSecretsAsync()
    {
        try
        {
            NexusSecrets secrets = BuildSecretsFromFields();

            await _secretsManager.SaveSecretsAsync(secrets, _originalSecrets);

            // Update snapshot to the newly saved state
            _originalSecrets = secrets.Clone();
            _loadedSecrets = secrets.Clone();

            CheckForChanges();
            RefreshSetupChecklist();

            if (_assetService != null && !string.IsNullOrWhiteSpace(GameClientPath))
                _ = _assetService.EnsureAssetsExtractedAsync(GameClientPath);

            StatusBannerText = "✓ Saved to the user-secrets vault and regenerated .env. Restart services to apply.";
            IsStatusSuccess = true;
            IsStatusBannerVisible = true;

            await RefreshDriftAsync();
        }
        catch (Exception ex)
        {
            StatusBannerText = $"Error saving configuration: {ex.Message}";
            IsStatusSuccess = false;
            IsStatusBannerVisible = true;
        }
    }

    /// <summary>
    ///     Single mapping NexusSecrets → panel fields (with the same empty-value fallbacks the panel always used).
    ///     Used by Load and Discard so the two can never diverge.
    /// </summary>
    private void ApplySecretsToFields(NexusSecrets secrets)
    {
        MasterServerAddress = !string.IsNullOrEmpty(secrets.MasterServerAddress)
            ? secrets.MasterServerAddress
            : "http://localhost:55555";
        InfrastructureGateway = !string.IsNullOrEmpty(secrets.InfrastructureGateway)
            ? secrets.InfrastructureGateway
            : "masterserver.newbs.io";
        MasterServerPort = secrets.MasterServerPort > 0 ? secrets.MasterServerPort : 55555;
        ChatServerHost = !string.IsNullOrEmpty(secrets.ChatServerHost) ? secrets.ChatServerHost : "127.0.0.1";
        ChatServerPublicHost = !string.IsNullOrEmpty(secrets.ChatServerPublicHost)
            ? secrets.ChatServerPublicHost
            : "127.0.0.1";
        ChatServerClientPort = secrets.ChatServerClientPort > 0 ? secrets.ChatServerClientPort : 11031;
        ChatServerPublicClientPort =
            secrets.ChatServerPublicClientPort > 0 ? secrets.ChatServerPublicClientPort : 11031;
        ChatServerMatchServerPort = secrets.ChatServerMatchServerPort > 0 ? secrets.ChatServerMatchServerPort : 11032;
        ChatServerMatchServerManagerPort = secrets.ChatServerMatchServerManagerPort > 0
            ? secrets.ChatServerMatchServerManagerPort
            : 11033;
        WebPortalHttpPort = secrets.WebPortalHttpPort > 0 ? secrets.WebPortalHttpPort : 5266;
        WebPortalHttpsPort = secrets.WebPortalHttpsPort > 0 ? secrets.WebPortalHttpsPort : 5557;
        WebPortalApiHttpPort = secrets.WebPortalApiHttpPort > 0 ? secrets.WebPortalApiHttpPort : 5200;
        WebPortalApiHttpsPort = secrets.WebPortalApiHttpsPort > 0 ? secrets.WebPortalApiHttpsPort : 5201;
        DbContextPort = secrets.DatabaseContextPort > 0 ? secrets.DatabaseContextPort : 5553;
        DistributedCachePort = secrets.DistributedCachePort > 0 ? secrets.DistributedCachePort : 28298;
        DistributedCacheSecondaryPort =
            secrets.DistributedCacheSecondaryPort > 0 ? secrets.DistributedCacheSecondaryPort : 28299;
        DistributedCacheDashboardPort =
            secrets.DistributedCacheDashboardPort > 0 ? secrets.DistributedCacheDashboardPort : 28300;
        MatchmakingPlayersPerTeam = secrets.MatchmakingPlayersPerTeam > 0 ? secrets.MatchmakingPlayersPerTeam : 1;
        MatchmakingIntervalMs = secrets.MatchmakingIntervalMs > 0 ? secrets.MatchmakingIntervalMs : 5000;
        CompelPingPort = secrets.CompelPingPort > 0 ? secrets.CompelPingPort : 11234;
        GameServerPublicIp =
            !string.IsNullOrEmpty(secrets.GameServerPublicIp) ? secrets.GameServerPublicIp : "127.0.0.1";
        GameServerInstancePorts = !string.IsNullOrEmpty(secrets.GameServerInstancePorts)
            ? secrets.GameServerInstancePorts
            : "11235,11236,11237,11238";
        PyroscopePort = secrets.PyroscopePort > 0 ? secrets.PyroscopePort : 4040;

        DiscordClientId = secrets.DiscordClientId;
        DiscordClientSecret = secrets.DiscordClientSecret;
        TwitchClientId = secrets.TwitchClientId;
        TwitchClientSecret = secrets.TwitchClientSecret;
        DatabaseProvider = !string.IsNullOrEmpty(secrets.DatabaseProvider) ? secrets.DatabaseProvider : "Supabase";
        DatabasePassword = secrets.DatabasePassword;
        DistributedCachePassword = secrets.DistributedCachePassword;
        SupabaseUrl = secrets.SupabaseUrl;
        SupabaseAnonKey = secrets.SupabaseAnonKey;
        SupabaseServiceRoleKey = secrets.SupabaseServiceRoleKey;
        SupabaseDatabaseConnectionString = secrets.SupabaseDatabaseConnectionString;
        _isSyncingSupabasePassword = true;
        try
        {
            SupabaseDatabasePassword = TryExtractPasswordFromConnectionString(SupabaseDatabaseConnectionString);
        }
        finally
        {
            _isSyncingSupabasePassword = false;
        }
        SupabaseStorageBucket = !string.IsNullOrEmpty(secrets.SupabaseStorageBucket)
            ? secrets.SupabaseStorageBucket
            : "replays";
        SupabaseS3AccessKey = secrets.SupabaseS3AccessKey;
        SupabaseS3SecretKey = secrets.SupabaseS3SecretKey;
        SupabaseS3Region = !string.IsNullOrEmpty(secrets.SupabaseS3Region) ? secrets.SupabaseS3Region : "us-east-1";
        SupabaseS3Endpoint = secrets.SupabaseS3Endpoint;
        LakehouseBucket = !string.IsNullOrEmpty(secrets.LakehouseBucket) ? secrets.LakehouseBucket : "match-analytics";
        JwtSigningKey = secrets.JwtSigningKey;
        InternalServiceKey = secrets.InternalServiceKey;
        IpcAuthKey = secrets.IpcAuthKey;
        SendGridApiKey = secrets.SendGridApiKey;
        AlertWebhookUrl = secrets.AlertWebhookUrl;
        ProjectPath = secrets.ProjectPath;
        CompelPath = secrets.CompelPath;
        CompelAuthenticationToken = secrets.CompelAuthenticationToken;
        HostAccountUserName = !string.IsNullOrEmpty(secrets.HostAccountUserName) ? secrets.HostAccountUserName : "HOST";
        HostAccountPassword = secrets.HostAccountPassword;
        CompelGateway = !string.IsNullOrEmpty(secrets.CompelGateway) ? secrets.CompelGateway : "localhost";
        CompelInstances = secrets.CompelInstances > 0 ? secrets.CompelInstances : 2;
        CompelPriorityMode = !string.IsNullOrEmpty(secrets.CompelPriorityMode) ? secrets.CompelPriorityMode : "Background";
        CompelInstallationDirectory = secrets.CompelInstallationDirectory;
        CompelControlPlanePort = Math.Max(0, secrets.CompelControlPlanePort);
        CompelPortRangeOffset = Math.Max(0, secrets.CompelPortRangeOffset);
        CompelUseProxy = secrets.CompelUseProxy ?? string.Empty;
        CompelCdnSynchronisation = secrets.CompelCdnSynchronisation ?? string.Empty;
        CompelServerNamePrefix = !string.IsNullOrEmpty(secrets.CompelServerNamePrefix)
            ? secrets.CompelServerNamePrefix
            : "KONGOR ARENA";
        CompelLocation = !string.IsNullOrEmpty(secrets.CompelLocation) ? secrets.CompelLocation : "USE";
        QueueArcadeEnabled = secrets.QueueArcadeEnabled;
        QueueArcadeInstances = secrets.QueueArcadeInstances > 0 ? secrets.QueueArcadeInstances : 2;
        GameClientPath = secrets.GameClientPath;
        ReplayStorageProvider = !string.IsNullOrEmpty(secrets.ReplayStorageProvider)
            ? secrets.ReplayStorageProvider
            : "Supabase";
        LocalReplayDirectory = secrets.LocalReplayDirectory;
        CloudBucketName = secrets.CloudBucketName;
        CloudAccessKey = secrets.CloudAccessKey;
        CloudSecretKey = secrets.CloudSecretKey;
        CloudServiceUrl = secrets.CloudServiceUrl;
        CloudRegion = !string.IsNullOrEmpty(secrets.CloudRegion) ? secrets.CloudRegion : "us-east-1";
        GrafanaOtlpEndpoint = secrets.GrafanaOtlpEndpoint;
        GrafanaOtlpHeaders = secrets.GrafanaOtlpHeaders;
        PyroscopeServerAddress = !string.IsNullOrEmpty(secrets.PyroscopeServerAddress)
            ? secrets.PyroscopeServerAddress
            : "http://localhost:4040";
        PyroscopeBasicAuthUser = secrets.PyroscopeBasicAuthUser;
        PyroscopeBasicAuthPassword = secrets.PyroscopeBasicAuthPassword;
        UnlockAllItems = secrets.UnlockAllItems;
    }

    /// <summary>
    ///     Single mapping panel fields → NexusSecrets. Starts from the last loaded model so settings that have no panel
    ///     field (e.g. ContainerRuntimeTarget, owned by the Game Servers page) are carried through unchanged.
    /// </summary>
    private NexusSecrets BuildSecretsFromFields()
    {
        NexusSecrets secrets = _loadedSecrets.Clone();

        secrets.MasterServerAddress = MasterServerAddress;
        secrets.InfrastructureGateway = InfrastructureGateway;
        secrets.MasterServerPort = MasterServerPort;
        secrets.ChatServerHost = ChatServerHost;
        secrets.ChatServerPublicHost = ChatServerPublicHost;
        secrets.ChatServerClientPort = ChatServerClientPort;
        secrets.ChatServerPublicClientPort = ChatServerPublicClientPort;
        secrets.ChatServerMatchServerPort = ChatServerMatchServerPort;
        secrets.ChatServerMatchServerManagerPort = ChatServerMatchServerManagerPort;
        secrets.WebPortalHttpPort = WebPortalHttpPort;
        secrets.WebPortalHttpsPort = WebPortalHttpsPort;
        secrets.WebPortalApiHttpPort = WebPortalApiHttpPort;
        secrets.WebPortalApiHttpsPort = WebPortalApiHttpsPort;
        secrets.DatabaseContextPort = DbContextPort;
        secrets.DistributedCachePort = DistributedCachePort;
        secrets.DistributedCacheSecondaryPort = DistributedCacheSecondaryPort;
        secrets.DistributedCacheDashboardPort = DistributedCacheDashboardPort;
        secrets.MatchmakingPlayersPerTeam = MatchmakingPlayersPerTeam;
        secrets.MatchmakingIntervalMs = MatchmakingIntervalMs;
        secrets.CompelPingPort = CompelPingPort;
        secrets.GameServerPublicIp = GameServerPublicIp;
        secrets.GameServerInstancePorts = GameServerInstancePorts;
        secrets.PyroscopePort = PyroscopePort;
        secrets.DiscordClientId = DiscordClientId;
        secrets.DiscordClientSecret = DiscordClientSecret;
        secrets.TwitchClientId = TwitchClientId;
        secrets.TwitchClientSecret = TwitchClientSecret;
        secrets.DatabaseProvider = DatabaseProvider;
        secrets.DatabasePassword = DatabasePassword;
        secrets.DistributedCachePassword = DistributedCachePassword;
        secrets.SupabaseUrl = SupabaseUrl;
        secrets.SupabaseAnonKey = SupabaseAnonKey;
        secrets.SupabaseServiceRoleKey = SupabaseServiceRoleKey;
        secrets.SupabaseDatabaseConnectionString = SupabaseDatabaseConnectionString;
        secrets.SupabaseStorageBucket = SupabaseStorageBucket;
        secrets.SupabaseS3AccessKey = SupabaseS3AccessKey;
        secrets.SupabaseS3SecretKey = SupabaseS3SecretKey;
        secrets.SupabaseS3Region = SupabaseS3Region;
        secrets.SupabaseS3Endpoint = SupabaseS3Endpoint;
        secrets.LakehouseBucket = LakehouseBucket;
        secrets.JwtSigningKey = JwtSigningKey;
        secrets.InternalServiceKey = InternalServiceKey;
        secrets.IpcAuthKey = IpcAuthKey;
        secrets.SendGridApiKey = SendGridApiKey;
        secrets.AlertWebhookUrl = AlertWebhookUrl;
        secrets.ProjectPath = ProjectPath;
        secrets.CompelPath = CompelPath;
        secrets.CompelAuthenticationToken = CompelAuthenticationToken;
        secrets.HostAccountUserName = HostAccountUserName;
        secrets.HostAccountPassword = HostAccountPassword;
        secrets.CompelGateway = CompelGateway;
        secrets.CompelInstances = CompelInstances;
        secrets.CompelPriorityMode = CompelPriorityMode;
        secrets.CompelInstallationDirectory = CompelInstallationDirectory;
        secrets.CompelControlPlanePort = CompelControlPlanePort;
        secrets.CompelPortRangeOffset = CompelPortRangeOffset;
        secrets.CompelUseProxy = CompelUseProxy ?? string.Empty;
        secrets.CompelCdnSynchronisation = CompelCdnSynchronisation ?? string.Empty;
        secrets.CompelServerNamePrefix = CompelServerNamePrefix;
        secrets.CompelLocation = CompelLocation;
        secrets.QueueArcadeEnabled = QueueArcadeEnabled;
        secrets.QueueArcadeInstances = QueueArcadeInstances > 0 ? QueueArcadeInstances : 2;
        secrets.GameClientPath = GameClientPath;
        secrets.ReplayStorageProvider = ReplayStorageProvider;
        secrets.LocalReplayDirectory = LocalReplayDirectory;
        secrets.CloudBucketName = CloudBucketName;
        secrets.CloudAccessKey = CloudAccessKey;
        secrets.CloudSecretKey = CloudSecretKey;
        secrets.CloudServiceUrl = CloudServiceUrl;
        secrets.CloudRegion = CloudRegion;
        secrets.GrafanaOtlpEndpoint = GrafanaOtlpEndpoint;
        secrets.GrafanaOtlpHeaders = GrafanaOtlpHeaders;
        secrets.PyroscopeServerAddress = PyroscopeServerAddress;
        secrets.PyroscopeBasicAuthUser = PyroscopeBasicAuthUser;
        secrets.PyroscopeBasicAuthPassword = PyroscopeBasicAuthPassword;
        secrets.UnlockAllItems = UnlockAllItems;

        return secrets;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Slice 1: generators, connection tests, drift banner, setup checklist
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> Slice1TrackedProperties =
    [
        nameof(SupabaseS3Endpoint), nameof(LakehouseBucket), nameof(InternalServiceKey), nameof(IpcAuthKey),
        nameof(AlertWebhookUrl), nameof(CompelInstallationDirectory), nameof(CompelControlPlanePort),
        nameof(CompelPortRangeOffset), nameof(CompelUseProxy), nameof(CompelCdnSynchronisation),
        nameof(CompelServerNamePrefix), nameof(CompelLocation), nameof(QueueArcadeEnabled), nameof(QueueArcadeInstances)
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is null) return;

        if (Slice1TrackedProperties.Contains(e.PropertyName)) CheckForChanges();

        if (e.PropertyName is nameof(SupabaseUrl) or nameof(SupabaseS3Endpoint))
        {
            OnPropertyChanged(nameof(EffectiveSupabaseS3Endpoint));
            OnPropertyChanged(nameof(SupabaseS3EndpointWatermark));
        }

        if (e.PropertyName is nameof(CompelAuthenticationToken) or nameof(HostAccountPassword)
            or nameof(JwtSigningKey) or nameof(InternalServiceKey) or nameof(SupabaseUrl)
            or nameof(SupabaseS3AccessKey) or nameof(SupabaseS3SecretKey))
            RefreshSetupChecklist();
    }

    /// <summary>32 cryptographically random bytes, base64url (no padding) – safe in JSON, .env and URLs.</summary>
    public static string GenerateSecretValue(int byteCount = 32)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [RelayCommand]
    public void GenerateInternalServiceKey()
    {
        InternalServiceKey = GenerateSecretValue();
        RevealInternalServiceKey = false;
        ShowGeneratedBanner("Internal Service Key");
    }

    [RelayCommand]
    public void GenerateIpcAuthKey()
    {
        IpcAuthKey = GenerateSecretValue();
        RevealIpcAuthKey = false;
        ShowGeneratedBanner("WILLOWMAKER IPC Auth Key");
    }

    [RelayCommand]
    public void GenerateJwtSigningKey()
    {
        JwtSigningKey = GenerateSecretValue(48);
        RevealJwtSigningKey = false;
        ShowGeneratedBanner("JWT Signing Key (existing logins will need to sign in again)");
    }

    [RelayCommand]
    public void GenerateCompelToken()
    {
        CompelAuthenticationToken = "compel_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        RevealCompelAuthenticationToken = false;
        UpdateGeneratedContainerCommand();
        ShowGeneratedBanner("COMPEL Control Plane Token");
    }

    [RelayCommand]
    public void GenerateHostPassword()
    {
        HostAccountPassword = GenerateSecretValue(18);
        RevealHostAccountPassword = false;
        UpdateGeneratedContainerCommand();
        ShowGeneratedBanner("Host Account Password (also update the HOST account password in the database)");
    }

    private void ShowGeneratedBanner(string what)
    {
        StatusBannerText = $"🔑 Generated a new {what}. Click Save to store it in the vault and .env.";
        IsStatusSuccess = true;
        IsStatusBannerVisible = true;
        CheckForChanges();
        RefreshSetupChecklist();
    }

    [RelayCommand]
    public async Task TestS3ConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (IsTestingS3Connection) return;

        string endpoint = EffectiveSupabaseS3Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(SupabaseS3AccessKey) ||
            string.IsNullOrWhiteSpace(SupabaseS3SecretKey))
        {
            S3ConnectionFeedback = "⚠️ Fill in the Supabase Project URL (or S3 endpoint), S3 Access Key ID and S3 Secret first.";
            return;
        }

        IsTestingS3Connection = true;
        S3ConnectionFeedback = "Testing S3 connection…";
        try
        {
            string bucket = string.IsNullOrWhiteSpace(LakehouseBucket) ? "match-analytics" : LakehouseBucket.Trim();
            Uri uri = NEXUS.Contracts.Security.AwsSigV4Signer.BuildObjectUri(endpoint, bucket);
            using HttpRequestMessage request = new(HttpMethod.Head, uri);
            NEXUS.Contracts.Security.AwsSigV4Signer.Sign(request, NEXUS.Contracts.Security.AwsSigV4Signer.EmptyPayloadHash,
                string.IsNullOrWhiteSpace(SupabaseS3Region) ? "us-east-1" : SupabaseS3Region.Trim(),
                SupabaseS3AccessKey.Trim(), SupabaseS3SecretKey.Trim(), TimeProvider.System.GetUtcNow());

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            int status = (int)response.StatusCode;
            S3ConnectionFeedback = status switch
            {
                >= 200 and < 300 => $"✓ Connected. Bucket '{bucket}' is reachable (HTTP {status}).",
                403 => $"⛔ HTTP 403: credentials rejected. Check the access key/secret and region ({SupabaseS3Region}).",
                404 => $"⚠️ HTTP 404: keys accepted but bucket '{bucket}' was not found. Create it in Supabase Storage.",
                _ => $"⚠️ Unexpected HTTP {status} from {uri.Host}."
            };
            Trace.WriteLine($"[SecretsConfig] S3 test host={uri.Host} bucket={bucket} status={status}");
        }
        catch (Exception exception)
        {
            S3ConnectionFeedback = $"⛔ S3 test failed: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            IsTestingS3Connection = false;
        }
    }

    [RelayCommand]
    public async Task SendTestAlertAsync(CancellationToken cancellationToken = default)
    {
        if (IsSendingTestAlert) return;

        if (!Uri.TryCreate(AlertWebhookUrl?.Trim(), UriKind.Absolute, out Uri? webhook) || webhook.Scheme != Uri.UriSchemeHttps)
        {
            AlertTestFeedback = "⚠️ Enter a valid https:// Discord webhook URL first.";
            return;
        }

        IsSendingTestAlert = true;
        AlertTestFeedback = "Sending test alert…";
        try
        {
            using StringContent body = new(
                "{\"content\":\"✅ NEXUS Control Panel test alert – circuit-breaker notifications are wired up.\"}",
                Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(webhook, body, cancellationToken);
            AlertTestFeedback = response.IsSuccessStatusCode
                ? $"✓ Test alert delivered (HTTP {(int)response.StatusCode})."
                : $"⚠️ Webhook returned HTTP {(int)response.StatusCode}.";
        }
        catch (Exception exception)
        {
            AlertTestFeedback = $"⛔ Test alert failed: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            IsSendingTestAlert = false;
        }
    }

    [RelayCommand]
    public async Task ReconcileDriftAsync()
    {
        try
        {
            await _secretsManager.ReconcileAsync();
            StatusBannerText = "✓ .env regenerated from the vault.";
            IsStatusSuccess = true;
            IsStatusBannerVisible = true;
        }
        catch (Exception exception)
        {
            StatusBannerText = $"Reconcile failed: {exception.Message}";
            IsStatusSuccess = false;
            IsStatusBannerVisible = true;
        }

        await RefreshDriftAsync();
    }

    [RelayCommand]
    public void DismissDriftBanner()
    {
        IsDriftBannerVisible = false;
    }

    public async Task RefreshDriftAsync()
    {
        try
        {
            SecretsDriftReport? report = await _secretsManager.GetDriftReportAsync();
            report ??= SecretsDriftReport.None;

            List<string> lines = [];
            if (report.EnvFileMissing) lines.Add(".env is missing – click Reconcile to generate it from the vault.");
            else if (report.EnvFileMismatchedNames.Count > 0)
                lines.Add($".env differs from the vault ({report.EnvFileMismatchedNames.Count} variable(s): " +
                          $"{string.Join(", ", report.EnvFileMismatchedNames.Take(6))}{(report.EnvFileMismatchedNames.Count > 6 ? ", …" : string.Empty)}).");

            if (report.ShadowingEnvironmentVariables.Count > 0)
                lines.Add("Windows environment variables override vault values for: " +
                          $"{string.Join(", ", report.ShadowingEnvironmentVariables.Take(6))}{(report.ShadowingEnvironmentVariables.Count > 6 ? ", …" : string.Empty)}. " +
                          "Remove them (System Properties → Environment Variables) so the panel is the single source of truth.");

            DriftBannerText = string.Join(Environment.NewLine, lines);
            CanReconcileDrift = report.HasEnvFileDrift;
            IsDriftBannerVisible = report.HasDrift;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[SecretsConfig] Drift check failed: {exception.GetType().Name}");
            IsDriftBannerVisible = false;
        }
    }

    public void RefreshSetupChecklist()
    {
        List<string> items = [];

        if (string.IsNullOrWhiteSpace(CompelAuthenticationToken))
            items.Add("COMPEL Control Plane Token is empty → Game Servers section → [Generate].");
        if (string.IsNullOrWhiteSpace(HostAccountPassword))
            items.Add("Host Account Password is empty → Game Servers section.");
        if (string.IsNullOrWhiteSpace(JwtSigningKey) ||
            JwtSigningKey == "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!")
            items.Add("JWT Signing Key is the public development default → Security → [Generate].");
        if (string.IsNullOrWhiteSpace(InternalServiceKey))
            items.Add("Internal Service Key uses the public development default → Security → [Generate].");
        if (!string.IsNullOrWhiteSpace(SupabaseUrl) &&
            (string.IsNullOrWhiteSpace(SupabaseS3AccessKey) || string.IsNullOrWhiteSpace(SupabaseS3SecretKey)))
            items.Add("Supabase S3 keys are empty → match analytics uploads are disabled (Supabase section).");

        SetupChecklistText = string.Join(Environment.NewLine, items.Select(item => "• " + item));
        HasSetupChecklist = items.Count > 0;
    }

    [RelayCommand]
    public void DismissStatusBanner()
    {
        IsStatusBannerVisible = false;
    }

    [RelayCommand]
    public void SetStandardHoNPorts()
    {
        ChatServerClientPort = 11031;
        ChatServerPublicClientPort = 11031;
        ChatServerMatchServerPort = 11032;
        ChatServerMatchServerManagerPort = 11033;
        MasterServerPort = 55555;
        WebPortalHttpPort = 5266;
        WebPortalHttpsPort = 5557;
        WebPortalApiHttpPort = 5200;
        WebPortalApiHttpsPort = 5201;
        DbContextPort = 5553;
        DistributedCachePort = 28298;
        DistributedCacheSecondaryPort = 28299;
        DistributedCacheDashboardPort = 28300;
        PyroscopePort = 4040;
        CompelPingPort = 11234;
        CheckForChanges();
    }

    [RelayCommand]
    public void SetSoloDevMatchmaking()
    {
        MatchmakingPlayersPerTeam = 1;
        MatchmakingIntervalMs = 5000;
        CheckForChanges();
    }

    [RelayCommand]
    public void SetStandard5v5Matchmaking()
    {
        MatchmakingPlayersPerTeam = 5;
        MatchmakingIntervalMs = 5000;
        CheckForChanges();
    }

    [RelayCommand]
    public async Task ApplyRecommendedBaselineAsync()
    {
        MasterServerAddress = "http://localhost:55555";
        InfrastructureGateway = "masterserver.newbs.io";
        MasterServerPort = 55555;
        ChatServerHost = "127.0.0.1";
        ChatServerPublicHost = "127.0.0.1";
        ChatServerClientPort = 11031;
        ChatServerPublicClientPort = 11031;
        ChatServerMatchServerPort = 11032;
        ChatServerMatchServerManagerPort = 11033;
        WebPortalHttpPort = 5266;
        WebPortalHttpsPort = 5557;
        WebPortalApiHttpPort = 5200;
        WebPortalApiHttpsPort = 5201;
        DbContextPort = 5553;
        DistributedCachePort = 28298;
        DistributedCacheSecondaryPort = 28299;
        DistributedCacheDashboardPort = 28300;
        MatchmakingPlayersPerTeam = 1;
        MatchmakingIntervalMs = 5000;
        CompelPingPort = 11234;
        GameServerPublicIp = "127.0.0.1";
        GameServerInstancePorts = "11235,11236,11237,11238";
        PyroscopePort = 4040;
        DatabaseProvider = "Supabase";
        DatabasePassword = "MerrickDatabasePassword123!";
        DistributedCachePassword = "DistributedCachePassword123!";
        JwtSigningKey = "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!";
        ReplayStorageProvider = "Supabase";
        LocalReplayDirectory = string.Empty;

        await SaveSecretsAsync();
    }

    public void UpdateGeneratedContainerCommand()
    {
        ContainerPlatform platform = SelectedContainerPlatform switch
        {
            "Minikube" => ContainerPlatform.Minikube,
            "Kubernetes" => ContainerPlatform.Kubernetes,
            _ => ContainerPlatform.Docker
        };

        NexusSecrets currentConfig = new()
        {
            HostAccountUserName = HostAccountUserName,
            HostAccountPassword = HostAccountPassword,
            CompelAuthenticationToken = CompelAuthenticationToken,
            CompelPriorityMode = CompelPriorityMode,
            CompelInstances = CompelInstances,
            CompelPath = CompelPath
        };

        GeneratedContainerCommand = ContainerLaunchCommandBuilder.BuildCommand(
            currentConfig,
            platform,
            maskSecrets: !RevealContainerSecretsInCommand,
            isWindowsHost: RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
    }

    [RelayCommand]
    public async Task CopyContainerCommandAsync()
    {
        ContainerPlatform platform = SelectedContainerPlatform switch
        {
            "Minikube" => ContainerPlatform.Minikube,
            "Kubernetes" => ContainerPlatform.Kubernetes,
            _ => ContainerPlatform.Docker
        };

        NexusSecrets currentConfig = new()
        {
            HostAccountUserName = HostAccountUserName,
            HostAccountPassword = HostAccountPassword,
            CompelAuthenticationToken = CompelAuthenticationToken,
            CompelPriorityMode = CompelPriorityMode,
            CompelInstances = CompelInstances,
            CompelPath = CompelPath
        };

        string fullCommand = ContainerLaunchCommandBuilder.BuildCommand(
            currentConfig,
            platform,
            maskSecrets: false,
            isWindowsHost: RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.Clipboard: not null
            } desktop)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(fullCommand);
            ContainerLaunchFeedback = $"✓ Copied {SelectedContainerPlatform} command to clipboard!";
        }
        else
        {
            ContainerLaunchFeedback = "Clipboard unavailable.";
        }
    }

    [RelayCommand]
    public void GenerateTestToken()
    {
        string rawToken = "tok_" + Guid.NewGuid().ToString("N")[..20];
        CompelAuthenticationToken = rawToken;
        ContainerLaunchFeedback = "✓ Generated test token for local control plane testing (note: does not register on public Master Server).";
        UpdateGeneratedContainerCommand();
    }

    [RelayCommand]
    public async Task TestAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        if (Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => TestAuthenticationAsync(cancellationToken));
            return;
        }

        if (IsTestingAuthentication) return;

        if (string.IsNullOrWhiteSpace(HostAccountUserName))
        {
            AuthenticationTestStatusIcon = "⚠️";
            AuthenticationTestFeedback = "Please enter a Host Account Username before testing authentication.";
            AuthenticationTestStatusColor = "#F59E0B";
            AuthenticationTestStatusBorderColor = "#D97706";
            return;
        }

        if (string.IsNullOrWhiteSpace(HostAccountPassword))
        {
            AuthenticationTestStatusIcon = "⚠️";
            AuthenticationTestFeedback = "Please enter a Host Account Password before testing authentication.";
            AuthenticationTestStatusColor = "#F59E0B";
            AuthenticationTestStatusBorderColor = "#D97706";
            return;
        }

        IsTestingAuthentication = true;
        AuthenticationTestStatusIcon = "⏳";
        AuthenticationTestFeedback = "Connecting to Master Server and verifying host credentials...";
        AuthenticationTestStatusColor = "#38BDF8";
        AuthenticationTestStatusBorderColor = "#0284C7";

        try
        {
            string host = MasterServerAddress?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "https://masterserver.newbs.io";
            }
            else if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = $"http://{host}";
            }

            string endpoint = $"{host.TrimEnd('/')}/server_requester.php";

            string pwd = HostAccountPassword.Trim();
            string passHash = (pwd.Length == 32 && pwd.All(char.IsAsciiHexDigit))
                ? pwd.ToLowerInvariant()
                : Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(pwd)));

            using FormUrlEncodedContent content = new([
                new KeyValuePair<string, string>("f", "replay_auth"),
                new KeyValuePair<string, string>("login", HostAccountUserName.Trim()),
                new KeyValuePair<string, string>("pass", passHash)
            ]);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(7));

            using HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cts.Token);
                AuthenticationTestStatusIcon = "✅";
                AuthenticationTestFeedback = $"✓ Master Server authentication verified! Host '{HostAccountUserName.Trim()}' is authorized as an official ServerHost on {host}. When you host, dedicated servers will connect and register successfully.";
                AuthenticationTestStatusColor = "#10B981";
                AuthenticationTestStatusBorderColor = "#059669";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                AuthenticationTestStatusIcon = "❌";
                if (errorBody.Contains("Not A Server Host", StringComparison.OrdinalIgnoreCase))
                {
                    AuthenticationTestFeedback = $"Authentication Failed (401): Account '{HostAccountUserName.Trim()}' exists, but does not have ServerHost permissions (AccountType != 2). Please grant ServerHost role in database or admin portal.";
                }
                else if (errorBody.Contains("Incorrect Password", StringComparison.OrdinalIgnoreCase))
                {
                    AuthenticationTestFeedback = $"Authentication Failed (401): Incorrect password for host account '{HostAccountUserName.Trim()}' on {host}.";
                }
                else
                {
                    AuthenticationTestFeedback = $"Authentication Failed (401 Unauthorized): {errorBody}";
                }
                AuthenticationTestStatusColor = "#EF4444";
                AuthenticationTestStatusBorderColor = "#DC2626";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AuthenticationTestStatusIcon = "❌";
                AuthenticationTestFeedback = $"Account Not Found (404): Account '{HostAccountUserName.Trim()}' does not exist on Master Server ({host}).";
                AuthenticationTestStatusColor = "#EF4444";
                AuthenticationTestStatusBorderColor = "#DC2626";
            }
            else
            {
                string errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                AuthenticationTestStatusIcon = "⚠️";
                AuthenticationTestFeedback = $"Master Server responded with HTTP {(int)response.StatusCode} ({response.StatusCode}): {errorBody}";
                AuthenticationTestStatusColor = "#F59E0B";
                AuthenticationTestStatusBorderColor = "#D97706";
            }
        }
        catch (OperationCanceledException)
        {
            AuthenticationTestStatusIcon = "⏱️";
            AuthenticationTestFeedback = "Authentication test timed out after 7 seconds. Master Server did not respond.";
            AuthenticationTestStatusColor = "#F59E0B";
            AuthenticationTestStatusBorderColor = "#D97706";
        }
        catch (Exception ex)
        {
            AuthenticationTestStatusIcon = "❌";
            AuthenticationTestFeedback = $"Connection Failed: Unable to reach Master Server. {ex.Message}";
            AuthenticationTestStatusColor = "#EF4444";
            AuthenticationTestStatusBorderColor = "#DC2626";
        }
        finally
        {
            IsTestingAuthentication = false;
        }
    }

    [RelayCommand]
    public async Task LaunchContainerInDockerAsync()
    {
        if (IsLaunchingContainer) return;
        IsLaunchingContainer = true;
        ContainerLaunchFeedback = "Launching container in Docker...";

        try
        {
            DockerStatus status = await _dockerHealth.CheckDockerStatusAsync();
            if (!status.IsRunning)
            {
                ContainerLaunchFeedback = $"Cannot launch: {status.StatusMessage}";
                return;
            }

            bool imageBuilt = await _dockerHealth.IsImageBuiltAsync();
            if (!imageBuilt)
            {
                IsDockerImageBuilt = false;
                ContainerLaunchFeedback = "⚠️ Docker image 'projectkongor/compel-dedicated:latest' not built yet. Click 'Build Image First' below.";
                return;
            }

            IsDockerImageBuilt = true;

            string repoRoot = DockerHealthService.ResolveRepositoryRoot();
            string gamesServerDir = Path.Combine(repoRoot, "games", "server");
            string serverDir = !string.IsNullOrWhiteSpace(CompelPath) && Directory.Exists(CompelPath)
                ? CompelPath
                : (Directory.Exists(gamesServerDir) ? gamesServerDir : Path.Combine(repoRoot, "server"));
            string artefactsDir = Path.Combine(repoRoot, "artefacts");
            try { Directory.CreateDirectory(artefactsDir); } catch { }

            string serverVol = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"\"{Path.GetFullPath(serverDir)}:/app/server:ro\""
                : $"{serverDir}:/app/server:ro";
            string artefactsVol = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"\"{Path.GetFullPath(artefactsDir)}:/app/artefacts:rw\""
                : $"{artefactsDir}:/app/artefacts:rw";

            int instances = CompelInstances > 0 ? CompelInstances : 2;
            NexusSecrets secrets = new()
            {
                HostAccountUserName = HostAccountUserName,
                HostAccountPassword = HostAccountPassword,
                CompelAuthenticationToken = CompelAuthenticationToken,
                CompelLocation = string.IsNullOrWhiteSpace(CompelLocation) ? "USE" : CompelLocation,
                CompelServerNamePrefix = string.IsNullOrWhiteSpace(CompelServerNamePrefix) ? "KONGOR ARENA" : CompelServerNamePrefix,
                CompelPath = CompelPath,
                CompelInstances = CompelInstances,
                CompelPriorityMode = CompelPriorityMode
            };

            var createParams = ContainerLaunchCommandBuilder.BuildCreateContainerParameters(
                secrets,
                CompelPriorityMode,
                instances,
                Path.GetFullPath(serverDir),
                Path.GetFullPath(artefactsDir),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

            var (success, output) = await _dockerHealth.LaunchContainerAsync(createParams);
            ContainerLaunchFeedback = success ? $"✓ {output}" : $"⚠️ {output}";
        }
        catch (Exception ex)
        {
            ContainerLaunchFeedback = $"Error launching container: {ex.Message}";
        }
        finally
        {
            IsLaunchingContainer = false;
        }
    }

    [RelayCommand]
    public async Task StopDockerContainerAsync()
    {
        ContainerLaunchFeedback = "Stopping container...";
        var (success, output) = await _dockerHealth.StopContainerAsync();
        ContainerLaunchFeedback = success ? $"✓ {output}" : $"⚠️ {output}";
    }

    [RelayCommand]
    public async Task BuildDockerImageAsync()
    {
        if (IsLaunchingContainer) return;
        IsLaunchingContainer = true;
        ContainerLaunchFeedback = "Building dedicated server Docker image (this may take 1-2 minutes)...";

        try
        {
            string repoRoot = DockerHealthService.ResolveRepositoryRoot();
            string dockerfilePath = Path.Combine(repoRoot, "infra", "docker", "Dockerfile.dedicated");
            string contextPath = repoRoot;
            var (success, output) = await _dockerHealth.BuildImageAsync(dockerfilePath, contextPath);
            if (success)
            {
                IsDockerImageBuilt = true;
                ContainerLaunchFeedback = "✓ Image built successfully! You can now launch your container.";
            }
            else
            {
                ContainerLaunchFeedback = $"⚠️ {output}";
            }
        }
        catch (Exception ex)
        {
            ContainerLaunchFeedback = $"Error building image: {ex.Message}";
        }
        finally
        {
            IsLaunchingContainer = false;
        }
    }
}
