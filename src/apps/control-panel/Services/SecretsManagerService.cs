// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASPIRE.Common.Constants;
using Kongor.Core.Configuration;
using NEXUS.Contracts.Configuration;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

/// <summary>
///     Registry-driven persistence for the "Secrets &amp; Configs" page.
///     <list type="bullet">
///         <item>Load: canonical key → compatibility keys → legacy aliases, solution vault first, then legacy vaults.</item>
///         <item>
///             Save: timestamped backup → write ONLY the solution vault (canonical + compatibility keys) → drop dead
///             aliases → scrub panel-managed keys from legacy vaults (backed up) → regenerate the git-ignored repo-root
///             <c>.env</c> from the vault so both are always identical.
///         </item>
///         <item>COMPEL.json is never written any more (it is a read-only fallback consumed by COMPEL itself).</item>
///     </list>
///     Only key NAMES are ever logged (<c>logs/secrets-manager.log</c>), never values.
/// </summary>
public sealed class SecretsManagerService : ISecretsManagerService
{
    private const int MaxBackupsPerVault = 5;
    private const string MerrickConnectionStringKey = "ConnectionStrings:MERRICK";
    private static readonly object LogLock = new();
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly string? _customSecretsPath;
    private readonly string? _envFilePath;
    private readonly bool _manageMachineState;
    private readonly string? _bundleProbeDirectory;
    private readonly bool _isBundledLayout;
    private readonly string? _bundledLogDirectory;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private NexusSecrets? _cachedSecrets;
    private (string Path, bool Exists, long LastWriteTicks, long Length)[]? _cachedVaultStamp;
    private (string Path, bool Exists, long LastWriteTicks, long Length)[]? _lastLoggedVaultStamp;
    private long _lastLoadStopwatchTicks;
    private static readonly long VaultCacheMaxAgeTicks = Stopwatch.Frequency * 10;

    internal int DiskLoadCount { get; private set; }
    internal int LoadLogCount { get; private set; }


    public bool IsBundledLayout => _isBundledLayout;

    /// <param name="customSecretsPath">
    ///     Test hook: read/write this single file instead of the real vaults. When set, legacy-vault scrubbing,
    ///     launcher_settings.json sync and <c>.env</c> generation are disabled unless <paramref name="envFilePath" /> is
    ///     given explicitly.
    /// </param>
    /// <param name="envFilePath">Where to write the generated <c>.env</c>. Defaults to <c>&lt;repo root&gt;/.env</c>.</param>
    /// <param name="bundleProbeDirectory">Optional probe directory for bundled layout resolution.</param>
    public SecretsManagerService(string? customSecretsPath = null, string? envFilePath = null, string? bundleProbeDirectory = null)
    {
        _bundleProbeDirectory = bundleProbeDirectory;
        if (!string.IsNullOrEmpty(bundleProbeDirectory))
        {
            // Instance-scoped probe directory only (never mutate process-wide statics)
        }

        if (string.IsNullOrEmpty(customSecretsPath) &&
            BundledLayoutResolver.TryResolve(out BundledLayout? bundledLayout, bundleProbeDirectory))
        {
            _isBundledLayout = true;
            _bundledLogDirectory = bundledLayout.Logs;
            _customSecretsPath = bundledLayout.SecretsJson;
            _envFilePath = envFilePath ?? bundledLayout.DockerEnv;
            _manageMachineState = false;
            Console.Error.WriteLine("[BundledLayout] secrets isolated to userdata/secrets");
            Log("[BundledLayout] secrets isolated to userdata/secrets");
        }
        else
        {
            _isBundledLayout = false;
            _customSecretsPath = customSecretsPath;
            _manageMachineState = string.IsNullOrEmpty(customSecretsPath);
            _envFilePath = envFilePath ?? (_manageMachineState ? Path.Combine(FindSolutionRoot(), ".env") : null);
        }
    }

    public string? EnvFilePath => _envFilePath;

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // LOAD
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<NexusSecrets> LoadSecretsAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            string[] readPaths = GetReadVaultPaths().ToArray();
            string[] stampPaths = string.IsNullOrEmpty(_envFilePath) ? readPaths : [.. readPaths, _envFilePath];
            (string Path, bool Exists, long LastWriteTicks, long Length)[] currentStamp = CaptureVaultStamp(stampPaths);
            long nowTicks = Stopwatch.GetTimestamp();

            if (_cachedSecrets is not null &&
                _cachedVaultStamp is not null &&
                nowTicks - _lastLoadStopwatchTicks <= VaultCacheMaxAgeTicks &&
                VaultStampsEqual(_cachedVaultStamp, currentStamp))
            {
                return _cachedSecrets.Clone();
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            DiskLoadCount++;
            NexusSecrets secrets = new();

            List<(string Path, JsonObject Document)> vaults = [];
            foreach (string path in readPaths)
            {
                JsonObject? document = await ReadVaultAsync(path, cancellationToken);
                if (document is not null) vaults.Add((path, document));
            }

            int canonicalHits = 0, compatibilityHits = 0, aliasHits = 0, legacyVaultHits = 0;
            List<string> aliasSourced = [];

            foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
            {
                bool found = false;
                for (int v = 0; v < vaults.Count && !found; v++)
                {
                    foreach (string key in descriptor.ReadKeys)
                    {
                        if (!TryReadString(vaults[v].Document, key, out string value) || string.IsNullOrWhiteSpace(value))
                            continue;

                        NexusSecretsBinding.SetValue(secrets, descriptor, value);
                        found = true;

                        if (key == descriptor.Key) canonicalHits++;
                        else if (descriptor.WriteAlso.Contains(key)) compatibilityHits++;
                        else
                        {
                            aliasHits++;
                            aliasSourced.Add($"{descriptor.Id}<-{key}");
                        }

                        if (v > 0) legacyVaultHits++;
                        break;
                    }
                }
            }

            if (vaults.Count > 0)
            {
                for (int v = 0; v < vaults.Count; v++)
                {
                    JsonObject vDoc = vaults[v].Document;
                    if (TryReadString(vDoc, "HostingMode", out string hostingMode) && !string.IsNullOrWhiteSpace(hostingMode))
                    {
                        secrets.HostingMode = hostingMode;
                    }
                    if (TryReadString(vDoc, "SelectedLanIp", out string selectedLanIp))
                    {
                        secrets.SelectedLanIp = selectedLanIp;
                    }
                    if (TryReadString(vDoc, "WanHost", out string wanHost))
                    {
                        secrets.WanHost = wanHost;
                    }
                    if (vDoc.TryGetPropertyValue("WanWarningAcknowledged", out JsonNode? wanNode) && wanNode is not null)
                    {
                        if (wanNode is JsonValue jv && jv.TryGetValue(out bool wanAck))
                        {
                            secrets.WanWarningAcknowledged = wanAck;
                        }
                        else if (TryReadString(vDoc, "WanWarningAcknowledged", out string wanAckStr) && bool.TryParse(wanAckStr, out bool parsedAck))
                        {
                            secrets.WanWarningAcknowledged = parsedAck;
                        }
                    }
                }
            }

            ApplyAutoDiscovery(secrets);
            DerivePyroscopeFromGrafana(secrets);

            _cachedVaultStamp = currentStamp;
            _cachedSecrets = secrets.Clone();
            _lastLoadStopwatchTicks = Stopwatch.GetTimestamp();

            if (_lastLoggedVaultStamp is null || !VaultStampsEqual(_lastLoggedVaultStamp, currentStamp))
            {
                _lastLoggedVaultStamp = currentStamp;
                LoadLogCount++;
                Log($"LOAD vaults={vaults.Count} canonical={canonicalHits} compat={compatibilityHits} alias={aliasHits} " +
                    $"fromLegacyVault={legacyVaultHits} elapsedMs={stopwatch.ElapsedMilliseconds}" +
                    (aliasSourced.Count > 0 ? $" aliasSourced=[{string.Join(", ", aliasSourced)}]" : string.Empty));
            }

            return secrets;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static (string Path, bool Exists, long LastWriteTicks, long Length)[] CaptureVaultStamp(string[] paths)
    {
        (string Path, bool Exists, long LastWriteTicks, long Length)[] stamp =
            new (string Path, bool Exists, long LastWriteTicks, long Length)[paths.Length];

        for (int i = 0; i < paths.Length; i++)
        {
            string p = paths[i];
            try
            {
                FileInfo fi = new(p);
                stamp[i] = fi.Exists ? (p, true, fi.LastWriteTimeUtc.Ticks, fi.Length) : (p, false, 0L, 0L);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SecretsManagerService] Vault stamp probe failed for '{p}': {ex.Message}");
                stamp[i] = (p, false, -1L, -1L);
            }
        }

        return stamp;
    }

    private static bool VaultStampsEqual(
        (string Path, bool Exists, long LastWriteTicks, long Length)[] left,
        (string Path, bool Exists, long LastWriteTicks, long Length)[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].Path, right[i].Path, StringComparison.OrdinalIgnoreCase) ||
                left[i].Exists != right[i].Exists ||
                left[i].LastWriteTicks != right[i].LastWriteTicks ||
                left[i].Length != right[i].Length ||
                left[i].LastWriteTicks < 0)
            {
                return false;
            }
        }

        return true;
    }

    public void InvalidateSecretsCache()
    {
        _cachedSecrets = null;
        _cachedVaultStamp = null;
        _lastLoadStopwatchTicks = 0;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // SAVE
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task SaveSecretsAsync(NexusSecrets secrets, NexusSecrets? originalSecrets = null,
        CancellationToken cancellationToken = default)
    {
        InvalidateSecretsCache();
        Stopwatch stopwatch = Stopwatch.StartNew();
        NexusSecrets normalized = Normalize(secrets);
        string vaultPath = GetSecretsFilePath();

        string? directory = Path.GetDirectoryName(vaultPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            EnforcePosixDirectoryPermissions(directory);
        }

        JsonObject vault = await ReadVaultAsync(vaultPath, cancellationToken) ?? [];
        string? backup = BackupVault(vaultPath);

        List<string> written = [], removed = [], aliasesDropped = [];

        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
        {
            string value = NexusSecretsBinding.GetValue(normalized, descriptor).Trim();
            string? originalValue = originalSecrets is null
                ? null
                : NexusSecretsBinding.GetValue(Normalize(originalSecrets), descriptor).Trim();

            bool omit = string.IsNullOrWhiteSpace(value) ||
                        (descriptor.OmitValue is not null && value == descriptor.OmitValue);
            bool changed = originalSecrets is null || !string.Equals(value, originalValue, StringComparison.Ordinal);

            if (changed && omit)
            {
                foreach (string key in descriptor.WriteKeys)
                    if (vault.Remove(key)) removed.Add(key);
            }
            else if (!omit && (changed || !AllKeysHold(vault, descriptor, value)))
            {
                foreach (string key in descriptor.WriteKeys) vault[key] = CreateNode(descriptor, value);
                written.Add(descriptor.Key);
            }

            // Dead aliases: drop them, but never destroy a non-empty value the canonical key does not hold.
            bool canonicalHoldsValue = TryReadString(vault, descriptor.Key, out string canonical) &&
                                       !string.IsNullOrWhiteSpace(canonical);
            foreach (string alias in descriptor.ReadAliases)
            {
                if (!vault.ContainsKey(alias)) continue;
                bool aliasEmpty = !TryReadString(vault, alias, out string aliasValue) || string.IsNullOrWhiteSpace(aliasValue);
                if (canonicalHoldsValue || aliasEmpty)
                {
                    vault.Remove(alias);
                    aliasesDropped.Add(alias);
                }
            }
        }

        vault["HostingMode"] = normalized.HostingMode;
        vault["SelectedLanIp"] = normalized.SelectedLanIp;
        vault["WanHost"] = normalized.WanHost;
        vault["WanWarningAcknowledged"] = normalized.WanWarningAcknowledged;
        written.Add("HostingMode");
        written.Add("SelectedLanIp");
        written.Add("WanHost");
        written.Add("WanWarningAcknowledged");

        ApplyMerrickConnectionString(vault, normalized, originalSecrets, written);

        await WriteJsonAtomicallyAsync(vaultPath, vault, cancellationToken);

        Log($"SAVE vault={Redact(vaultPath)} backup={(backup is null ? "none" : Path.GetFileName(backup))} " +
            $"written={written.Count} removed={removed.Count} aliasesDropped={aliasesDropped.Count} " +
            $"writtenKeys=[{string.Join(", ", written)}]" +
            (removed.Count > 0 ? $" removedKeys=[{string.Join(", ", removed)}]" : string.Empty) +
            (aliasesDropped.Count > 0 ? $" droppedAliases=[{string.Join(", ", aliasesDropped)}]" : string.Empty));

        if (_manageMachineState)
        {
            await ScrubLegacyVaultsAsync(vault, cancellationToken);
            await SyncLauncherSettingsAsync(normalized, cancellationToken);
        }

        if (_envFilePath is not null) await WriteEnvFileAsync(vault, vaultPath, cancellationToken);

        InvalidateSecretsCache();
        _lastLoggedVaultStamp = null;
        Log($"SAVE complete elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // DRIFT
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<SecretsDriftReport> GetDriftReportAsync(CancellationToken cancellationToken = default)
    {
        JsonObject vault = await ReadVaultAsync(GetSecretsFilePath(), cancellationToken) ?? [];
        List<KeyValuePair<string, string>> expected = BuildEnvEntries(vault);

        bool tracked = _envFilePath is not null;
        bool missing = false;
        List<string> mismatched = [];

        if (tracked)
        {
            if (!File.Exists(_envFilePath))
            {
                missing = expected.Count > 0;
            }
            else
            {
                Dictionary<string, string> actual =
                    DotEnvFile.Parse(await File.ReadAllTextAsync(_envFilePath!, cancellationToken));

                foreach (KeyValuePair<string, string> entry in expected)
                    if (!actual.TryGetValue(entry.Key, out string? actualValue) ||
                        !string.Equals(actualValue, entry.Value, StringComparison.Ordinal))
                        mismatched.Add(entry.Key);

                HashSet<string> expectedNames = expected.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
                mismatched.AddRange(actual.Keys.Where(name => !expectedNames.Contains(name)));
            }
        }

        List<string> shadowing = FindShadowingEnvironmentVariables(vault);

        SecretsDriftReport report = new(tracked, missing, mismatched, shadowing);
        Log($"DRIFT envTracked={tracked} envMissing={missing} envMismatched=[{string.Join(", ", mismatched)}] " +
            $"shadowing=[{string.Join(", ", shadowing)}]");
        return report;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        Log("RECONCILE requested (vault wins; .env regenerated)");
        JsonObject vault = await ReadVaultAsync(GetSecretsFilePath(), cancellationToken) ?? [];
        if (_envFilePath is not null) await WriteEnvFileAsync(vault, GetSecretsFilePath(), cancellationToken);
        InvalidateSecretsCache();
        _lastLoggedVaultStamp = null;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // RECOMMENDED DEFAULTS
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task ApplyRecommendedDefaultsAsync(NexusSecrets secrets, CancellationToken cancellationToken = default)
    {
        secrets.MasterServerAddress = "http://localhost:55555";
        secrets.InfrastructureGateway = "masterserver.newbs.io";
        secrets.MasterServerPort = 55555;
        secrets.ChatServerHost = "127.0.0.1";
        secrets.ChatServerPublicHost = "127.0.0.1";
        secrets.ChatServerClientPort = 11031;
        secrets.ChatServerPublicClientPort = 11031;
        secrets.ChatServerMatchServerPort = 11032;
        secrets.ChatServerMatchServerManagerPort = 11033;
        secrets.WebPortalHttpPort = 5266;
        secrets.WebPortalHttpsPort = 5557;
        secrets.WebPortalApiHttpPort = 5200;
        secrets.WebPortalApiHttpsPort = 5201;
        secrets.DatabaseContextPort = 5553;
        secrets.DistributedCachePort = 28298;
        secrets.DistributedCacheSecondaryPort = 28299;
        secrets.DistributedCacheDashboardPort = 28300;
        secrets.MatchmakingPlayersPerTeam = 1;
        secrets.MatchmakingIntervalMs = 5000;
        secrets.CompelPingPort = 11234;
        secrets.GameServerPublicIp = "127.0.0.1";
        secrets.GameServerInstancePorts = "11235,11236,11237,11238";
        secrets.PyroscopePort = 4040;
        secrets.DatabasePassword = NexusSettingRegistry.Get(nameof(NexusSecrets.DatabasePassword)).DefaultValue ?? string.Empty;
        secrets.DistributedCachePassword = NexusSettingRegistry.Get(nameof(NexusSecrets.DistributedCachePassword)).DefaultValue ?? string.Empty;
        secrets.JwtSigningKey = NexusSettingRegistry.Get(nameof(NexusSecrets.JwtSigningKey)).DefaultValue ?? string.Empty;
        secrets.ReplayStorageProvider = "LocalDisk";
        secrets.CloudRegion = "us-east-1";

        await SaveSecretsAsync(secrets, null, cancellationToken);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // PATHS
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public string GetSecretsFilePath()
    {
        return _customSecretsPath ?? GetSecretsFilePathForId(UserSecretsConstants.SolutionUserSecretsId);
    }

    public static string FindSolutionRoot() => FindSolutionRoot(null);

    public static string FindSolutionRoot(string? probeDir)
    {
        if (BundledLayoutResolver.TryResolve(out BundledLayout? layout, probeDir))
        {
            return layout.Root;
        }

        if (string.IsNullOrWhiteSpace(probeDir) &&
            KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws) && ws is not null)
        {
            return ws.Root;
        }

        string current = string.IsNullOrWhiteSpace(probeDir) ? AppContext.BaseDirectory : probeDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.sln")))
                return current;

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;

            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>The <c>.env</c> lines the vault currently implies (name, value) in registry order.</summary>
    public static List<KeyValuePair<string, string>> BuildEnvEntries(JsonObject vault)
    {
        List<KeyValuePair<string, string>> entries = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
        {
            if (!TryReadString(vault, descriptor.Key, out string value) || string.IsNullOrWhiteSpace(value)) continue;

            foreach (string name in descriptor.DotEnvNames)
                if (seen.Add(name))
                    entries.Add(new KeyValuePair<string, string>(name, value));
        }

        if (TryReadString(vault, MerrickConnectionStringKey, out string merrick) && !string.IsNullOrWhiteSpace(merrick) &&
            seen.Add("ConnectionStrings__MERRICK"))
            entries.Add(new KeyValuePair<string, string>("ConnectionStrings__MERRICK", merrick));

        return entries;
    }

    private IEnumerable<string> GetReadVaultPaths()
    {
        if (!string.IsNullOrEmpty(_customSecretsPath)) return [_customSecretsPath];

        return new[] { UserSecretsConstants.SolutionUserSecretsId }
            .Concat(UserSecretsConstants.LegacyProjectSecretsIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(GetSecretsFilePathForId);
    }

    private static string GetSecretsFilePathForId(string secretsId)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string winPath = Path.Combine(appData, "Microsoft", "UserSecrets", secretsId, "secrets.json");
        if (File.Exists(winPath) || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return winPath;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".microsoft", "usersecrets", secretsId, "secrets.json");
    }

    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // INTERNALS
    // ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static NexusSecrets Normalize(NexusSecrets source)
    {
        NexusSecrets copy = source.Clone();

        if (string.IsNullOrWhiteSpace(copy.DatabasePassword))
            copy.DatabasePassword = NexusSettingRegistry.Get(nameof(NexusSecrets.DatabasePassword)).DefaultValue ?? string.Empty;

        // Gateway blank → derive the authority (host[:port]) from the master server address
        if (string.IsNullOrWhiteSpace(copy.InfrastructureGateway) &&
            Uri.TryCreate(copy.MasterServerAddress?.Trim(), UriKind.Absolute, out Uri? uri))
            copy.InfrastructureGateway = uri.Authority;

        return copy;
    }

    private static bool AllKeysHold(JsonObject vault, NexusSettingDescriptor descriptor, string value)
    {
        foreach (string key in descriptor.WriteKeys)
            if (!TryReadString(vault, key, out string existing) || !string.Equals(existing, value, StringComparison.Ordinal))
                return false;

        return true;
    }

    private static JsonNode CreateNode(NexusSettingDescriptor descriptor, string value)
    {
        if (descriptor.Kind == NexusSettingKind.Boolean && bool.TryParse(value, out bool flag))
            return JsonValue.Create(flag);

        return JsonValue.Create(value)!;
    }

    private static void ApplyMerrickConnectionString(JsonObject vault, NexusSecrets secrets, NexusSecrets? original,
        List<string> written)
    {
        bool relevantChange = original is null ||
                              secrets.DatabaseProvider != original.DatabaseProvider ||
                              secrets.SupabaseDatabaseConnectionString != original.SupabaseDatabaseConnectionString ||
                              secrets.DatabasePassword != original.DatabasePassword ||
                              secrets.DatabaseContextPort != original.DatabaseContextPort ||
                              !vault.ContainsKey(MerrickConnectionStringKey);
        if (!relevantChange) return;

        string? connectionString = null;
        if (secrets.DatabaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
            secrets.DatabaseProvider.Equals("Supabase", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(secrets.SupabaseDatabaseConnectionString))
                connectionString = secrets.SupabaseDatabaseConnectionString.Trim();
        }
        else if (secrets.DatabaseProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) ||
                 secrets.DatabaseProvider.Equals("MSSQL", StringComparison.OrdinalIgnoreCase))
        {
            connectionString =
                $"Server=127.0.0.1,{secrets.DatabaseContextPort};Database=MerrickDb;User Id=sa;Password={secrets.DatabasePassword};TrustServerCertificate=True;MultipleActiveResultSets=true;";
        }

        if (connectionString is null) return;

        vault[MerrickConnectionStringKey] = connectionString;
        written.Add(MerrickConnectionStringKey);
    }

    private async Task ScrubLegacyVaultsAsync(JsonObject solutionVault, CancellationToken cancellationToken)
    {
        string solutionPath = GetSecretsFilePath();

        foreach (string legacyPath in GetReadVaultPaths().Skip(1))
        {
            if (string.Equals(legacyPath, solutionPath, StringComparison.OrdinalIgnoreCase)) continue;

            JsonObject? legacy = await ReadVaultAsync(legacyPath, cancellationToken);
            if (legacy is null) continue;

            List<string> scrubbed = [];
            foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
            {
                bool solutionHoldsValue = TryReadString(solutionVault, descriptor.Key, out string solutionValue) &&
                                          !string.IsNullOrWhiteSpace(solutionValue);

                foreach (string key in descriptor.ManagedKeys)
                {
                    if (!legacy.ContainsKey(key)) continue;
                    bool legacyEmpty = !TryReadString(legacy, key, out string legacyValue) ||
                                       string.IsNullOrWhiteSpace(legacyValue);
                    if (!solutionHoldsValue && !legacyEmpty) continue; // never lose data

                    legacy.Remove(key);
                    scrubbed.Add(key);
                }
            }

            if (scrubbed.Count == 0) continue;

            string? backup = BackupVault(legacyPath);
            await WriteJsonAtomicallyAsync(legacyPath, legacy, cancellationToken);
            Log($"LEGACY_SCRUB vault={Redact(legacyPath)} backup={(backup is null ? "none" : Path.GetFileName(backup))} " +
                $"removed={scrubbed.Count} keys=[{string.Join(", ", scrubbed.Distinct())}]");
        }
    }

    private async Task WriteEnvFileAsync(JsonObject vault, string vaultPath, CancellationToken cancellationToken)
    {
        try
        {
            List<KeyValuePair<string, string>> entries = BuildEnvEntries(vault);
            if (_isBundledLayout)
            {
                int existingIdx = entries.FindIndex(e => string.Equals(e.Key, "POSTGRES_PASSWORD", StringComparison.Ordinal));
                string pgPass = string.Empty;
                if (TryReadString(vault, "POSTGRES_PASSWORD", out string p1) && !string.IsNullOrWhiteSpace(p1))
                {
                    pgPass = p1;
                }
                else if (TryReadString(vault, "database-password", out string p2) && !string.IsNullOrWhiteSpace(p2))
                {
                    pgPass = p2;
                }
                else if (TryReadString(vault, "DatabasePassword", out string p3) && !string.IsNullOrWhiteSpace(p3))
                {
                    pgPass = p3;
                }

                if (string.IsNullOrWhiteSpace(pgPass) && !string.IsNullOrEmpty(_envFilePath) && File.Exists(_envFilePath))
                {
                    try
                    {
                        Dictionary<string, string> existingEnv = DotEnvFile.Parse(await File.ReadAllTextAsync(_envFilePath, cancellationToken));
                        if (existingEnv.TryGetValue("POSTGRES_PASSWORD", out string? filePass) && !string.IsNullOrWhiteSpace(filePass))
                        {
                            pgPass = filePass;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ENV_PARSE_FALLBACK_FAILED path={Redact(_envFilePath)} error={ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(pgPass))
                {
                    if (existingIdx >= 0)
                    {
                        entries[existingIdx] = new KeyValuePair<string, string>("POSTGRES_PASSWORD", pgPass);
                    }
                    else
                    {
                        entries.Add(new KeyValuePair<string, string>("POSTGRES_PASSWORD", pgPass));
                    }
                }
            }

            string content = DotEnvFile.Render(entries, vaultPath);

            string? directory = Path.GetDirectoryName(_envFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                EnforcePosixDirectoryPermissions(directory);
            }

            string temp = _envFilePath + ".tmp";
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken);
            EnforcePosixSecretFilePermissions(temp);
            File.Move(temp, _envFilePath!, true);
            EnforcePosixSecretFilePermissions(_envFilePath!);

            Log($"ENV_WRITE path={Redact(_envFilePath!)} variables={entries.Count} names=[{string.Join(", ", entries.Select(e => e.Key))}]");
        }
        catch (Exception exception)
        {
            Log($"ENV_WRITE_FAILED path={Redact(_envFilePath!)} error={exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private static List<string> FindShadowingEnvironmentVariables(JsonObject vault)
    {
        List<string> shadowing = [];
        EnvironmentVariableTarget[] targets =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? [EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine]
                : [EnvironmentVariableTarget.Process];

        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
        {
            if (!TryReadString(vault, descriptor.Key, out string vaultValue) || string.IsNullOrWhiteSpace(vaultValue))
                continue;

            foreach (string name in descriptor.DotEnvNames)
            foreach (EnvironmentVariableTarget target in targets)
            {
                string? osValue;
                try
                {
                    osValue = Environment.GetEnvironmentVariable(name, target);
                }
                catch
                {
                    continue;
                }

                if (osValue is null || string.Equals(osValue, vaultValue, StringComparison.Ordinal)) continue;

                string label = $"{name} ({target})";
                if (!shadowing.Contains(label)) shadowing.Add(label);
            }
        }

        return shadowing;
    }

    private async Task SyncLauncherSettingsAsync(NexusSecrets secrets, CancellationToken cancellationToken)
    {
        string solutionRoot = FindSolutionRoot(_bundleProbeDirectory);
        string[] launcherPaths =
        [
            Path.Combine(solutionRoot, "games", "client", "userdataKONGOR", "launcher_settings.json"),
            Path.Combine(solutionRoot, "games", "client", "launcher_settings.json"),
            Path.Combine(solutionRoot, "apps", "willowmaker", "src", "launcher_settings.json"),
            Path.Combine(solutionRoot, "apps", "willowmaker", "launcher_settings.json")
        ];

        foreach (string launcherPath in launcherPaths)
        {
            if (!File.Exists(launcherPath)) continue;

            try
            {
                string text = await File.ReadAllTextAsync(launcherPath, cancellationToken);
                if (JsonNode.Parse(text) is not JsonObject root) continue;

                string targetAddress = !string.IsNullOrWhiteSpace(secrets.InfrastructureGateway)
                    ? secrets.InfrastructureGateway.Trim()
                    : secrets.MasterServerAddress.Trim();

                root["MasterServerAddress"] = targetAddress;
                await File.WriteAllTextAsync(launcherPath, JsonSerializer.Serialize(root, IndentedJson), cancellationToken);
                EnforcePosixSecretFilePermissions(launcherPath);
                Log($"LAUNCHER_SYNC path={Redact(launcherPath)} key=MasterServerAddress");
            }
            catch (Exception exception)
            {
                Log($"LAUNCHER_SYNC_FAILED path={Redact(launcherPath)} error={exception.GetType().Name}");
            }
        }
    }

    private void ApplyAutoDiscovery(NexusSecrets secrets)
    {
        string solutionRoot = FindSolutionRoot(_bundleProbeDirectory);

        if (string.IsNullOrWhiteSpace(secrets.CompelPath))
        {
            string[] compelCandidates =
            [
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net11.0", "COMPEL.exe"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net11.0", "COMPEL.exe"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net10.0", "COMPEL.exe"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net10.0", "COMPEL.exe"),
                Path.Combine(solutionRoot, "games", "server", "COMPEL.exe"),
                Path.Combine(solutionRoot, "games", "server"),
                Path.Combine(solutionRoot, "apps", "compel", "src", "bin", "Debug", "net11.0", "COMPEL.exe"),
                Path.Combine(solutionRoot, "apps", "compel", "src", "bin", "Release", "net11.0", "COMPEL.exe"),
                Path.Combine(solutionRoot, "apps", "compel", "src")
            ];

            string? bestCompel = compelCandidates
                                     .Where(File.Exists)
                                     .OrderByDescending(File.GetLastWriteTimeUtc)
                                     .FirstOrDefault()
                                 ?? compelCandidates.FirstOrDefault(Directory.Exists);

            if (!string.IsNullOrEmpty(bestCompel)) secrets.CompelPath = bestCompel;
        }

        if (string.IsNullOrWhiteSpace(secrets.GameClientPath) ||
            secrets.GameClientPath.EndsWith("WILLOWMAKER.exe", StringComparison.OrdinalIgnoreCase))
        {
            string[] clientCandidates =
            [
                Path.Combine(solutionRoot, "games", "client"),
                Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Release", "net11.0"),
                Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Debug", "net11.0")
            ];

            string? bestClient = clientCandidates.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(bestClient)) secrets.GameClientPath = bestClient;
        }
    }

    private static void DerivePyroscopeFromGrafana(NexusSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(secrets.PyroscopeServerAddress) &&
            !string.IsNullOrWhiteSpace(secrets.GrafanaOtlpEndpoint) &&
            Uri.TryCreate(secrets.GrafanaOtlpEndpoint.Trim(), UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host;
            if (host.StartsWith("otlp-gateway-", StringComparison.OrdinalIgnoreCase))
                host = "profiles-" + host["otlp-gateway-".Length..];

            secrets.PyroscopeServerAddress = $"{uri.Scheme}://{host}";
        }

        if ((!string.IsNullOrWhiteSpace(secrets.PyroscopeBasicAuthUser) &&
             !string.IsNullOrWhiteSpace(secrets.PyroscopeBasicAuthPassword)) ||
            string.IsNullOrWhiteSpace(secrets.GrafanaOtlpHeaders))
            return;

        string rawHeader = secrets.GrafanaOtlpHeaders.Trim();
        string base64Token = rawHeader;
        int basicIdx = rawHeader.IndexOf("Basic ", StringComparison.OrdinalIgnoreCase);
        if (basicIdx >= 0)
            base64Token = rawHeader[(basicIdx + 6)..].Trim();
        else if (rawHeader.StartsWith("Authorization=", StringComparison.OrdinalIgnoreCase))
            base64Token = rawHeader[14..].Trim();

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64Token));
            int colonIdx = decoded.IndexOf(':');
            if (colonIdx <= 0) return;

            if (string.IsNullOrWhiteSpace(secrets.PyroscopeBasicAuthUser))
                secrets.PyroscopeBasicAuthUser = decoded[..colonIdx];

            if (string.IsNullOrWhiteSpace(secrets.PyroscopeBasicAuthPassword))
                secrets.PyroscopeBasicAuthPassword = decoded[(colonIdx + 1)..];
        }
        catch
        {
            // Not standard basic auth – nothing to derive
        }
    }

    private async Task<JsonObject?> ReadVaultAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (Exception exception)
        {
            Log($"VAULT_READ_FAILED vault={Redact(path)} error={exception.GetType().Name}");
            return null;
        }
    }

    public static bool TryReadString(JsonObject document, string key, out string value)
    {
        value = string.Empty;
        if (!document.TryGetPropertyValue(key, out JsonNode? node) || node is null) return false;

        if (node is JsonValue jsonValue)
        {
            switch (jsonValue.GetValueKind())
            {
                case JsonValueKind.String:
                    value = jsonValue.GetValue<string>();
                    return true;
                case JsonValueKind.True:
                    value = "true";
                    return true;
                case JsonValueKind.False:
                    value = "false";
                    return true;
                case JsonValueKind.Number:
                    value = jsonValue.ToJsonString();
                    return true;
            }
        }

        return false;
    }

    private static async Task WriteJsonAtomicallyAsync(string path, JsonObject document, CancellationToken cancellationToken)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, IndentedJson), cancellationToken);
        EnforcePosixSecretFilePermissions(temp);
        File.Move(temp, path, true);
        EnforcePosixSecretFilePermissions(path);
    }

    private static void EnforcePosixSecretFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecretsManager] SetUnixFileMode(0600) warning for '{Redact(path)}': {ex.Message}");
        }
    }

    private static void EnforcePosixDirectoryPermissions(string directoryPath)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecretsManager] SetUnixFileMode(0700) warning for '{Redact(directoryPath)}': {ex.Message}");
        }
    }

    private string? BackupVault(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string stamp = TimeProvider.System.GetLocalNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string backup = $"{path}.bak-{stamp}";
            File.Copy(path, backup, true);
            EnforcePosixSecretFilePermissions(backup);

            string directory = Path.GetDirectoryName(path)!;
            string prefix = Path.GetFileName(path) + ".bak-";
            foreach (string stale in Directory.GetFiles(directory, prefix + "*")
                         .OrderByDescending(file => file, StringComparer.Ordinal)
                         .Skip(MaxBackupsPerVault))
                File.Delete(stale);

            return backup;
        }
        catch (Exception exception)
        {
            Log($"BACKUP_FAILED vault={Redact(path)} error={exception.GetType().Name}");
            return null;
        }
    }

    private static string Redact(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[profile.Length..]
            : path;
    }

    private void Log(string message)
    {
        string line = $"{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} [SecretsManager] {message}";
        Debug.WriteLine(line);

        try
        {
            string logDirectory;
            if (!string.IsNullOrEmpty(_bundledLogDirectory))
            {
                logDirectory = _bundledLogDirectory;
            }
            else if (BundledLayoutResolver.TryResolve(out BundledLayout? logLayout, _bundleProbeDirectory))
            {
                logDirectory = logLayout.Logs;
            }
            else
            {
                logDirectory = Path.Combine(FindSolutionRoot(), "logs");
            }

            Directory.CreateDirectory(logDirectory);
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(logDirectory, "secrets-manager.log"), line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SecretsManager] Failed to write log: {ex.Message}");
        }
    }
}
