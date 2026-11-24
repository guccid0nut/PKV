// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.Services.Standalone;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.Standalone;

public sealed class SecretsManagerBundledIsolationTests : IDisposable
{
    private readonly string _tempRoot;

    public SecretsManagerBundledIsolationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_test_bundle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestCleanup] Exception deleting tempRoot: {ex.Message}");
        }
    }

    private static string GetHostUserSecretsPath()
    {
        const string secretsId = "00005072-6f6a-6563-7420-4b4f4e474f52";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Microsoft", "UserSecrets", secretsId, "secrets.json");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".microsoft", "usersecrets", secretsId, "secrets.json");
    }

    [Fact]
    public async Task SecretsManager_InBundledLayout_IsolatesToUserdataSecretsAndLeavesHostSecretsUntouched()
    {
        // 1. Arrange bundle root with playtest_policy.json and games/client
        File.WriteAllText(Path.Combine(_tempRoot, "playtest_policy.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "games", "client"));

        string probeDir = Path.Combine(_tempRoot, "control-panel-win-x64");
        Directory.CreateDirectory(probeDir);

        // Record host user secrets state
        string hostSecretsPath = GetHostUserSecretsPath();
        bool hostSecretsExistedBefore = File.Exists(hostSecretsPath);
        DateTime? hostMtimeBefore = hostSecretsExistedBefore ? File.GetLastWriteTimeUtc(hostSecretsPath) : null;

        // 2. Act: instantiate SecretsManagerService pointing at probeDir
        var secretsManager = new SecretsManagerService(bundleProbeDirectory: probeDir);
        Assert.True(secretsManager.IsBundledLayout);

        NexusSecrets secrets = new()
        {
            DatabasePassword = "TestPgPass123",
            HostingMode = "Lan",
            SelectedLanIp = "192.168.1.100",
            WanHost = "kongor.example.com",
            WanWarningAcknowledged = true
        };

        await secretsManager.SaveSecretsAsync(secrets);

        // 3. Assert: bundle files created
        string secretsJsonPath = Path.Combine(_tempRoot, "userdata", "secrets", "nexus_secrets.json");
        string envDockerPath = Path.Combine(_tempRoot, "userdata", "secrets", ".env.docker");

        Assert.True(File.Exists(secretsJsonPath), $"Expected {secretsJsonPath} to exist.");
        Assert.True(File.Exists(envDockerPath), $"Expected {envDockerPath} to exist.");

        // Assert .env.docker contains POSTGRES_PASSWORD
        string envContent = await File.ReadAllTextAsync(envDockerPath);
        Assert.Contains("POSTGRES_PASSWORD", envContent);

        var parsedEnv = DotEnvFile.Parse(envContent);
        Assert.True(parsedEnv.TryGetValue("POSTGRES_PASSWORD", out string? actualPgPass));
        Assert.Equal("TestPgPass123", actualPgPass);

        // Assert Reload
        NexusSecrets reloaded = await secretsManager.LoadSecretsAsync();
        Assert.Equal("Lan", reloaded.HostingMode);
        Assert.Equal("192.168.1.100", reloaded.SelectedLanIp);
        Assert.Equal("kongor.example.com", reloaded.WanHost);
        Assert.True(reloaded.WanWarningAcknowledged);
        Assert.Equal("TestPgPass123", reloaded.DatabasePassword);

        // Assert host secrets untouched
        bool hostSecretsExistedAfter = File.Exists(hostSecretsPath);
        if (!hostSecretsExistedBefore)
        {
            Assert.False(hostSecretsExistedAfter, "Host user secrets file should not have been created by bundled SecretsManagerService!");
        }
        else
        {
            Assert.True(hostSecretsExistedAfter, "Host user secrets file should still exist.");
            DateTime hostMtimeAfter = File.GetLastWriteTimeUtc(hostSecretsPath);
            Assert.Equal(hostMtimeBefore!.Value, hostMtimeAfter);
        }
    }

    [Fact]
    public void StandaloneSecretsBootstrapper_PasswordComplexityRequirements_AreStrictlySatisfied()
    {
        for (int i = 0; i < 50; i++)
        {
            string pass = StandaloneSecretsBootstrapper.GenerateComplexPassword(24);
            Assert.True(pass.Length >= 20, $"Password length {pass.Length} must be >= 20");

            int upper = pass.Count(char.IsUpper);
            int lower = pass.Count(char.IsLower);
            int digit = pass.Count(char.IsDigit);
            int symbol = pass.Count(c => "!@#$%^&*-_=+".Contains(c));
            int unique = pass.Distinct().Count();

            Assert.True(upper >= 2, $"Password '{pass}' has only {upper} uppercase characters");
            Assert.True(lower >= 2, $"Password '{pass}' has only {lower} lowercase characters");
            Assert.True(digit >= 2, $"Password '{pass}' has only {digit} digit characters");
            Assert.True(symbol >= 2, $"Password '{pass}' has only {symbol} symbol characters");
            Assert.True(unique >= 4, $"Password '{pass}' has only {unique} unique characters");
        }
    }

    [Fact]
    public async Task StandaloneSecretsBootstrapper_BootstrapsIdempotently_WithoutOverwritingExistingSecrets()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "playtest_policy.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "games", "client"));

        Assert.True(BundledLayoutResolver.TryResolve(out BundledLayout? layout, _tempRoot));

        List<string> logs1 = [];
        await StandaloneSecretsBootstrapper.EnsureBootstrappedAsync(layout, null, logs1.Add);

        Assert.True(File.Exists(layout.SecretsJson));
        Assert.True(File.Exists(layout.DockerEnv));
        Assert.True(File.Exists(layout.InitialCredentials));

        string credsText1 = await File.ReadAllTextAsync(layout.InitialCredentials);
        Assert.Contains("KONGOR STANDALONE INITIAL ADMIN CREDENTIALS", credsText1);
        Assert.Contains("Admin Username: Admin", credsText1);
        Assert.Contains("Host Username: kongor_host", credsText1);

        string secretsJson1 = await File.ReadAllTextAsync(layout.SecretsJson);
        var json1 = JsonNode.Parse(secretsJson1) as JsonObject;
        Assert.NotNull(json1);

        Assert.True(SecretsManagerService.TryReadString(json1, "database-password", out string dbPass1));
        Assert.True(SecretsManagerService.TryReadString(json1, "Standalone:AdminPassword", out string adminPass1));
        Assert.True(SecretsManagerService.TryReadString(json1, "Compel:HostPassword", out string hostPass1));
        Assert.True(SecretsManagerService.TryReadString(json1, "InternalServiceKey", out string isk1));
        Assert.True(SecretsManagerService.TryReadString(json1, "jwt-signing-key", out string jwt1));

        Assert.False(string.IsNullOrWhiteSpace(dbPass1));
        Assert.False(string.IsNullOrWhiteSpace(adminPass1));
        Assert.False(string.IsNullOrWhiteSpace(hostPass1));
        Assert.False(string.IsNullOrWhiteSpace(isk1));
        Assert.False(string.IsNullOrWhiteSpace(jwt1));

        // Second boot run: must be completely idempotent and not alter anything
        List<string> logs2 = [];
        await StandaloneSecretsBootstrapper.EnsureBootstrappedAsync(layout, null, logs2.Add);

        string secretsJson2 = await File.ReadAllTextAsync(layout.SecretsJson);
        var json2 = JsonNode.Parse(secretsJson2) as JsonObject;
        Assert.NotNull(json2);

        Assert.True(SecretsManagerService.TryReadString(json2, "database-password", out string dbPass2));
        Assert.True(SecretsManagerService.TryReadString(json2, "Standalone:AdminPassword", out string adminPass2));
        Assert.True(SecretsManagerService.TryReadString(json2, "Compel:HostPassword", out string hostPass2));
        Assert.True(SecretsManagerService.TryReadString(json2, "InternalServiceKey", out string isk2));
        Assert.True(SecretsManagerService.TryReadString(json2, "jwt-signing-key", out string jwt2));

        Assert.Equal(dbPass1, dbPass2);
        Assert.Equal(adminPass1, adminPass2);
        Assert.Equal(hostPass1, hostPass2);
        Assert.Equal(isk1, isk2);
        Assert.Equal(jwt1, jwt2);

        string credsText2 = await File.ReadAllTextAsync(layout.InitialCredentials);
        Assert.Equal(credsText1, credsText2);
    }
}
