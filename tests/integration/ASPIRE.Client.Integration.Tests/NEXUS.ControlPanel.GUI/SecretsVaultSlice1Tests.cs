// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NEXUS.Contracts.Configuration;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

/// <summary>
///     Slice 1: the Secrets &amp; Configs panel is the single configuration surface.
///     Covers the registry ⇄ model binding, canonical vault writes, alias clean-up without data loss, backups,
///     .env generation and drift detection. Every test uses an isolated temp directory (vault + .env + backups).
/// </summary>
public sealed class SecretsVaultSlice1Tests : IDisposable
{
    private readonly string _directory;
    private readonly string _vaultPath;
    private readonly string _envPath;

    public SecretsVaultSlice1Tests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"nexus_slice1_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _vaultPath = Path.Combine(_directory, "secrets.json");
        _envPath = Path.Combine(_directory, ".env");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private SecretsManagerService CreateService() => new(_vaultPath, _envPath);

    private async Task<JsonObject> ReadVaultAsync() =>
        JsonNode.Parse(await File.ReadAllTextAsync(_vaultPath))!.AsObject();

    // ── Registry ⇄ NexusSecrets binding ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Binding_CoversEveryRegistrySetting_AndNothingElse()
    {
        HashSet<string> registryIds = NexusSettingRegistry.All.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> boundIds = NexusSecretsBinding.Accessors.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(registryIds.Except(boundIds));
        Assert.Empty(boundIds.Except(registryIds));
    }

    [Fact]
    public void Binding_TextLikeSettings_RoundTripThroughNexusSecrets()
    {
        NexusSecrets secrets = new();
        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All.Where(d =>
                     d.Kind is NexusSettingKind.Text or NexusSettingKind.Secret or NexusSettingKind.Path
                         or NexusSettingKind.Url))
        {
            string probe = $"probe-{descriptor.Id}";
            NexusSecretsBinding.SetValue(secrets, descriptor, probe);
            Assert.Equal(probe, NexusSecretsBinding.GetValue(secrets, descriptor));
        }
    }

    [Fact]
    public void Clone_ProducesIndependentCopyWithEqualValues()
    {
        NexusSecrets original = new() { SupabaseS3AccessKey = "AK", CompelControlPlanePort = 12000, UnlockAllItems = true };

        NexusSecrets clone = original.Clone();
        clone.SupabaseS3AccessKey = "changed";

        Assert.Equal("AK", original.SupabaseS3AccessKey);
        Assert.Equal(12000, clone.CompelControlPlanePort);
        Assert.True(clone.UnlockAllItems);
        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All.Where(d => d.Id != "SupabaseS3AccessKey"))
            Assert.Equal(NexusSecretsBinding.GetValue(original, descriptor), NexusSecretsBinding.GetValue(clone, descriptor));
    }

    [Fact]
    public void NewNexusSecrets_ShipsNoPersonalOrSecretDefaults()
    {
        NexusSecrets defaults = new();
        List<string> forbidden = GetForbiddenTokens();

        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
        {
            string value = NexusSecretsBinding.GetValue(defaults, descriptor);
            foreach (string token in forbidden)
                Assert.DoesNotContain(token, value, StringComparison.OrdinalIgnoreCase);
            if (descriptor.IsSecret && descriptor.Id is not ("DatabasePassword" or "DistributedCachePassword" or "JwtSigningKey"))
                Assert.True(string.IsNullOrEmpty(value), $"{descriptor.Id} must default to empty");
        }
    }

    private static List<string> GetForbiddenTokens()
    {
        List<string> forbidden = [@"C:\Users", "compel_", "canary_dev_user"];
        string? envDenylist = Environment.GetEnvironmentVariable("KONGOR_PII_DENYLIST");
        if (!string.IsNullOrWhiteSpace(envDenylist))
        {
            foreach (string part in envDenylist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length >= 4 && !forbidden.Contains(part, StringComparer.OrdinalIgnoreCase))
                    forbidden.Add(part);
            }
        }

        string piiConfigPath = Path.Combine(SecretsManagerService.FindSolutionRoot(), "config", "local_pii_guard.local.json");
        if (File.Exists(piiConfigPath))
        {
            try
            {
                JsonNode? root = JsonNode.Parse(File.ReadAllText(piiConfigPath));
                if (root?["terms"] is JsonArray terms)
                {
                    foreach (JsonNode? item in terms)
                    {
                        string? val = item?["value"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(val) && val.Length >= 4 && !forbidden.Contains(val, StringComparer.OrdinalIgnoreCase))
                        {
                            forbidden.Add(val);
                        }
                    }
                }
            }
            catch
            {
                // gitignored local config fallback
            }
        }

        return forbidden;
    }

    // ── Save: canonical keys, aliases, backups ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_WritesCanonicalAndCompatibilityKeys_ForNewSlice1Settings()
    {
        NexusSecrets secrets = new()
        {
            SupabaseUrl = "https://abc.supabase.co",
            SupabaseS3AccessKey = "AK-TEST",
            SupabaseS3SecretKey = "SK-TEST",
            SupabaseS3Region = "us-east-2",
            CompelAuthenticationToken = "compel-test-token",
            IpcAuthKey = "ipc-test",
            AlertWebhookUrl = "https://discord.example/api/webhooks/1/x"
        };

        await CreateService().SaveSecretsAsync(secrets);
        JsonObject vault = await ReadVaultAsync();

        foreach (string id in new[] { "SupabaseS3AccessKey", "SupabaseS3SecretKey", "SupabaseS3Region", "CompelAuthenticationToken", "IpcAuthKey", "AlertWebhookUrl" })
        {
            NexusSettingDescriptor descriptor = NexusSettingRegistry.Get(id);
            string expected = NexusSecretsBinding.GetValue(secrets, descriptor);
            foreach (string key in descriptor.WriteKeys)
                Assert.True(SecretsManagerService.TryReadString(vault, key, out string actual) && actual == expected,
                    $"{id}: key '{key}' should hold the saved value");
        }

        Assert.True(SecretsManagerService.TryReadString(vault, "Compel:ControlPlaneAuthenticationToken", out string compelKey));
        Assert.Equal("compel-test-token", compelKey);
    }

    [Fact]
    public async Task Save_OmitsDefaultSentinels_ForTriStateAndZeroPortSettings()
    {
        await CreateService().SaveSecretsAsync(new NexusSecrets { CompelControlPlanePort = 0, CompelUseProxy = "" });
        JsonObject vault = await ReadVaultAsync();

        foreach (string key in NexusSettingRegistry.Get("CompelControlPlanePort").WriteKeys) Assert.False(vault.ContainsKey(key), key);
        foreach (string key in NexusSettingRegistry.Get("CompelUseProxy").WriteKeys) Assert.False(vault.ContainsKey(key), key);
    }

    [Fact]
    public async Task Save_MigratesLegacyAliasToCanonicalKey_WithoutLosingTheValue()
    {
        NexusSettingDescriptor descriptor = NexusSettingRegistry.All.First(d =>
            d.ReadAliases.Length > 0 && d.Kind is NexusSettingKind.Text or NexusSettingKind.Secret or NexusSettingKind.Path);
        string alias = descriptor.ReadAliases[0];

        await File.WriteAllTextAsync(_vaultPath, new JsonObject
        {
            [alias] = "legacy-value",
            ["unmanaged-custom-secret"] = "LeaveMeAlone"
        }.ToJsonString());

        SecretsManagerService service = CreateService();
        NexusSecrets loaded = await service.LoadSecretsAsync();
        Assert.Equal("legacy-value", NexusSecretsBinding.GetValue(loaded, descriptor));

        // Unchanged save (original == current): the value must still be migrated, not dropped.
        await service.SaveSecretsAsync(loaded, loaded.Clone());
        JsonObject vault = await ReadVaultAsync();

        Assert.True(SecretsManagerService.TryReadString(vault, descriptor.Key, out string canonical));
        Assert.Equal("legacy-value", canonical);
        Assert.False(vault.ContainsKey(alias), $"dead alias '{alias}' should be removed once canonical holds the value");
        Assert.Equal("LeaveMeAlone", vault["unmanaged-custom-secret"]?.GetValue<string>());
    }

    [Fact]
    public async Task Save_ClearingASecret_RemovesAllItsWriteKeys()
    {
        SecretsManagerService service = CreateService();
        NexusSecrets first = new() { IpcAuthKey = "to-be-removed" };
        await service.SaveSecretsAsync(first);

        NexusSecrets second = first.Clone();
        second.IpcAuthKey = "";
        await service.SaveSecretsAsync(second, first);

        JsonObject vault = await ReadVaultAsync();
        foreach (string key in NexusSettingRegistry.Get("IpcAuthKey").WriteKeys) Assert.False(vault.ContainsKey(key), key);
    }

    [Fact]
    public async Task Save_CreatesTimestampedBackups_AndKeepsAtMostFive()
    {
        SecretsManagerService service = CreateService();
        for (int i = 0; i < 8; i++)
        {
            await service.SaveSecretsAsync(new NexusSecrets { IpcAuthKey = $"value-{i}" });
            await Task.Delay(5); // distinct millisecond stamps
        }

        string[] backups = Directory.GetFiles(_directory, "secrets.json.bak-*");
        Assert.InRange(backups.Length, 1, 5);
        Assert.All(backups, backup => Assert.Matches(@"secrets\.json\.bak-\d{8}-\d{6}-\d{3}$", backup));
    }

    // ── .env generation & drift ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_GeneratesDotEnvFromVault_WithHeaderAndDoubleUnderscoreNames()
    {
        await CreateService().SaveSecretsAsync(new NexusSecrets { SupabaseS3AccessKey = "AK-ENV", IpcAuthKey = "ipc-env" });

        Assert.True(File.Exists(_envPath));
        string content = await File.ReadAllTextAsync(_envPath);
        Assert.StartsWith(DotEnvFile.HeaderMarker, content);
        Assert.DoesNotContain(Environment.UserName, content.Split('\n').First(), StringComparison.OrdinalIgnoreCase);

        Dictionary<string, string> parsed = DotEnvFile.Parse(content);
        Assert.Equal("AK-ENV", parsed["Supabase__S3AccessKey"]);
        Assert.Equal("ipc-env", parsed["KONGOR_IPC_AUTH_KEY"]);
        Assert.All(parsed.Keys, name => Assert.True(NexusSettingDescriptor.IsEnvironmentSafe(name), name));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("has spaces and # hash")]
    [InlineData("it's quoted")]
    [InlineData("dollar $HOME and ${VAR}")]
    [InlineData("back\\slash \"double\"")]
    [InlineData("multi\nline")]
    [InlineData("Authorization=Basic abc==")]
    public void DotEnvFile_QuoteAndParse_RoundTrip(string value)
    {
        string rendered = DotEnvFile.Render([new KeyValuePair<string, string>("TEST_VALUE", value)], "vault.json");

        Dictionary<string, string> parsed = DotEnvFile.Parse(rendered);

        Assert.Equal(value, parsed["TEST_VALUE"]);
    }

    [Fact]
    public async Task Drift_DetectsHandEditedDotEnv_AndReconcileRestoresIt()
    {
        SecretsManagerService service = CreateService();
        await service.SaveSecretsAsync(new NexusSecrets { IpcAuthKey = "vault-value" });

        SecretsDriftReport clean = await service.GetDriftReportAsync();
        Assert.True(clean.EnvFileTracked);
        Assert.False(clean.HasEnvFileDrift);

        string edited = (await File.ReadAllTextAsync(_envPath)).Replace("vault-value", "hand-edited") +
                        "\nROGUE_VARIABLE='x'\n";
        await File.WriteAllTextAsync(_envPath, edited);

        SecretsDriftReport drifted = await service.GetDriftReportAsync();
        Assert.True(drifted.HasEnvFileDrift);
        Assert.Contains("KONGOR_IPC_AUTH_KEY", drifted.EnvFileMismatchedNames);
        Assert.Contains("ROGUE_VARIABLE", drifted.EnvFileMismatchedNames);

        await service.ReconcileAsync();

        Assert.False((await service.GetDriftReportAsync()).HasEnvFileDrift);
        Assert.Equal("vault-value", DotEnvFile.Parse(await File.ReadAllTextAsync(_envPath))["KONGOR_IPC_AUTH_KEY"]);
    }

    [Fact]
    public async Task Drift_ReportsMissingDotEnv()
    {
        SecretsManagerService service = CreateService();
        await service.SaveSecretsAsync(new NexusSecrets { IpcAuthKey = "v" });
        File.Delete(_envPath);

        SecretsDriftReport report = await service.GetDriftReportAsync();

        Assert.True(report.EnvFileMissing);
        Assert.True(report.HasEnvFileDrift);
    }

    // ── Panel coverage: every registry setting has an input on the Secrets & Configs page ─────────────────────

    // Registry ids whose panel field has a different view-model name, or that live on another page.
    private static readonly Dictionary<string, string?> PanelExceptions = new(StringComparer.Ordinal)
    {
        ["DatabaseContextPort"] = "DbContextPort",
        ["ContainerRuntimeTarget"] = null // owned by the Game Servers page
    };

    [Fact]
    public void Panel_RendersAnInputForEveryRegistrySetting()
    {
        string root = SecretsManagerService.FindSolutionRoot();
        string axamlPath = Path.Combine(root, "src", "apps", "control-panel", "Views", "SecretsConfigView.axaml");
        Assert.True(File.Exists(axamlPath), $"expected view at {axamlPath}");

        string axaml = File.ReadAllText(axamlPath);
        HashSet<string> bound = Regex.Matches(axaml, @"\{Binding\s+([A-Za-z0-9_]+)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        List<string> missing = [];
        foreach (NexusSettingDescriptor descriptor in NexusSettingRegistry.All)
        {
            string? property = PanelExceptions.TryGetValue(descriptor.Id, out string? mapped) ? mapped : descriptor.Id;
            if (property is null) continue;
            if (!bound.Contains(property)) missing.Add(descriptor.Id);
        }

        Assert.True(missing.Count == 0, "Settings with no panel input: " + string.Join(", ", missing));
    }
}
