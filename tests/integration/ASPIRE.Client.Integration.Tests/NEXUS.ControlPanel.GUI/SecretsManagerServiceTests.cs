// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Nodes;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class SecretsManagerServiceTests
{
    [Fact]
    public void SecretsManagerService_GetSecretsFilePath_ReturnsValidNonEmptyPath()
    {
        // Arrange
        SecretsManagerService service = new();

        // Act
        string path = service.GetSecretsFilePath();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.EndsWith("secrets.json", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("00005072-6f6a-6563-7420-4b4f4e474f52", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecretsManagerService_SaveAndLoadSecrets_PreservesValuesExact()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_test_{Guid.NewGuid():N}.json");
        try
        {
            SecretsManagerService service = new(tempFile);
            NexusSecrets secrets = new()
            {
                DiscordClientId = "999888777666555",
                DiscordClientSecret = "SuperSecretDiscordKey123!",
                MasterServerAddress = "http://127.0.0.1:55555",
                DatabasePassword = "CustomDatabasePassword123!",
                DistributedCachePassword = "CustomCachePassword123!",
                JwtSigningKey = "Custom-JWT-SigningKey-Phrase-32BytesLong!",
                CompelPath = @"C:\Games\COMPEL\COMPEL.exe",
                GameClientPath = @"C:\Games\Heroes of Newerth",
                DatabaseProvider = "Supabase",
                SupabaseUrl = "https://xyzcompany.supabase.co",
                SupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.anon",
                SupabaseServiceRoleKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.service_role",
                SupabaseDatabaseConnectionString =
                    "postgresql://postgres:password123@db.xyz.supabase.co:6543/postgres",
                SupabaseStorageBucket = "replays",
                SupabaseS3AccessKey = "s3access123",
                SupabaseS3SecretKey = "s3secret123",
                SupabaseS3Region = "us-east-1",
                ReplayStorageProvider = "Supabase",
                CloudBucketName = "my-test-replays",
                CloudAccessKey = "access123",
                CloudSecretKey = "secret123",
                CloudServiceUrl = "https://r2.cloudflarestorage.com",
                CloudRegion = "auto",
                GrafanaOtlpEndpoint = "https://otlp-gateway.grafana.net",
                GrafanaOtlpHeaders = "Basic token123",
                PyroscopeServerAddress = "https://profiles.grafana.net",
                PyroscopeBasicAuthUser = "user123",
                PyroscopeBasicAuthPassword = "pass123",
                CompelPriorityMode = "HighPriority",
                ContainerRuntimeTarget = "Kubernetes",
                CompelServerNamePrefix = "^pTEST ^oARENA ^r",
                CompelLocation = "USE",
                UnlockAllItems = false
            };

            // Act
            await service.SaveSecretsAsync(secrets);
            NexusSecrets loaded = await service.LoadSecretsAsync();

            // Assert
            Assert.False(loaded.UnlockAllItems);
            Assert.Equal("999888777666555", loaded.DiscordClientId);
            Assert.Equal("SuperSecretDiscordKey123!", loaded.DiscordClientSecret);
            Assert.Equal("http://127.0.0.1:55555", loaded.MasterServerAddress);
            Assert.Equal("CustomDatabasePassword123!", loaded.DatabasePassword);
            Assert.Equal("CustomCachePassword123!", loaded.DistributedCachePassword);
            Assert.Equal("Custom-JWT-SigningKey-Phrase-32BytesLong!", loaded.JwtSigningKey);
            Assert.Equal(@"C:\Games\COMPEL\COMPEL.exe", loaded.CompelPath);
            Assert.Equal(@"C:\Games\Heroes of Newerth", loaded.GameClientPath);
            Assert.Equal("Supabase", loaded.DatabaseProvider);
            Assert.Equal("https://xyzcompany.supabase.co", loaded.SupabaseUrl);
            Assert.Equal("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.anon", loaded.SupabaseAnonKey);
            Assert.Equal("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.service_role", loaded.SupabaseServiceRoleKey);
            Assert.Equal("postgresql://postgres:password123@db.xyz.supabase.co:6543/postgres",
                loaded.SupabaseDatabaseConnectionString);
            Assert.Equal("replays", loaded.SupabaseStorageBucket);
            Assert.Equal("s3access123", loaded.SupabaseS3AccessKey);
            Assert.Equal("s3secret123", loaded.SupabaseS3SecretKey);
            Assert.Equal("us-east-1", loaded.SupabaseS3Region);
            Assert.Equal("Supabase", loaded.ReplayStorageProvider);
            Assert.Equal("my-test-replays", loaded.CloudBucketName);
            Assert.Equal("access123", loaded.CloudAccessKey);
            Assert.Equal("secret123", loaded.CloudSecretKey);
            Assert.Equal("https://r2.cloudflarestorage.com", loaded.CloudServiceUrl);
            Assert.Equal("auto", loaded.CloudRegion);
            Assert.Equal("https://otlp-gateway.grafana.net", loaded.GrafanaOtlpEndpoint);
            Assert.Equal("Basic token123", loaded.GrafanaOtlpHeaders);
            Assert.Equal("https://profiles.grafana.net", loaded.PyroscopeServerAddress);
            Assert.Equal("user123", loaded.PyroscopeBasicAuthUser);
            Assert.Equal("pass123", loaded.PyroscopeBasicAuthPassword);
            Assert.Equal("HighPriority", loaded.CompelPriorityMode);
            Assert.Equal("Kubernetes", loaded.ContainerRuntimeTarget);
            Assert.Equal("^pTEST ^oARENA ^r", loaded.CompelServerNamePrefix);
            Assert.Equal("USE", loaded.CompelLocation);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadSecretsAsync_PullsGameClientPathAndPyroscope_FromStandardKeys()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_load_test_{Guid.NewGuid():N}.json");
        try
        {
            JsonObject seeded = new()
            {
                ["GameClient:Path"] = @"C:\HoN_Install\game",
                ["PYROSCOPE_SERVER_ADDRESS"] = "https://profiles-prod-us-east-0.grafana.net",
                ["PYROSCOPE_BASIC_AUTH_USER"] = "1775601",
                ["PYROSCOPE_BASIC_AUTH_PASSWORD"] = "glc_secret_token"
            };
            await File.WriteAllTextAsync(tempFile, seeded.ToJsonString());

            SecretsManagerService service = new(tempFile);

            // Act
            NexusSecrets loaded = await service.LoadSecretsAsync();

            // Assert
            Assert.Equal(@"C:\HoN_Install\game", loaded.GameClientPath);
            Assert.Equal("https://profiles-prod-us-east-0.grafana.net", loaded.PyroscopeServerAddress);
            Assert.Equal("1775601", loaded.PyroscopeBasicAuthUser);
            Assert.Equal("glc_secret_token", loaded.PyroscopeBasicAuthPassword);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadSecretsAsync_AutoDerivesPyroscope_FromGrafanaOtlpCredentials()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_pyro_derive_test_{Guid.NewGuid():N}.json");
        try
        {
            // Seed with Grafana OTLP credentials (Base64 of "1775601:glc_token_12345")
            string base64Header = Convert.ToBase64String("1775601:glc_token_12345"u8);
            JsonObject seeded = new()
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://otlp-gateway-prod-us-east-3.grafana.net/otlp",
                ["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization=Basic {base64Header}"
            };
            await File.WriteAllTextAsync(tempFile, seeded.ToJsonString());

            SecretsManagerService service = new(tempFile);

            // Act
            NexusSecrets loaded = await service.LoadSecretsAsync();

            // Assert: Automatically derived Pyroscope credentials
            Assert.Equal("https://profiles-prod-us-east-3.grafana.net", loaded.PyroscopeServerAddress);
            Assert.Equal("1775601", loaded.PyroscopeBasicAuthUser);
            Assert.Equal("glc_token_12345", loaded.PyroscopeBasicAuthPassword);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SaveSecretsAsync_WithOriginalSecrets_OnlyUpdatesModifiedKeys()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_delta_test_{Guid.NewGuid():N}.json");
        try
        {
            // Seed file with unmanaged key and initial secrets
            JsonObject initialJson = new()
            {
                ["unmanaged-custom-secret"] = "LeaveMeAlone123",
                ["compel-path"] = @"C:\OldCompel",
                ["database-password"] = "OriginalDbPass123!"
            };
            await File.WriteAllTextAsync(tempFile, initialJson.ToJsonString());

            SecretsManagerService service = new(tempFile);
            NexusSecrets original = new() { CompelPath = @"C:\OldCompel", DatabasePassword = "OriginalDbPass123!" };

            // Only modify CompelPath
            NexusSecrets updated = new() { CompelPath = @"C:\NewCompel", DatabasePassword = "OriginalDbPass123!" };

            // Act: Delta save
            await service.SaveSecretsAsync(updated, original);

            // Assert: Read JSON directly
            string rawSaved = await File.ReadAllTextAsync(tempFile);
            JsonObject? savedObj = JsonNode.Parse(rawSaved)?.AsObject();

            Assert.NotNull(savedObj);
            Assert.Equal("LeaveMeAlone123", savedObj["unmanaged-custom-secret"]?.GetValue<string>());
            Assert.Equal(@"C:\NewCompel", savedObj["compel-path"]?.GetValue<string>());
            Assert.Equal("OriginalDbPass123!", savedObj["database-password"]?.GetValue<string>());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task SecretsManagerService_ApplyRecommendedDefaultsAsync_SetsProductionSafeBaselines()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_test_{Guid.NewGuid():N}.json");
        try
        {
            SecretsManagerService service = new(tempFile);
            NexusSecrets secrets = new() { DatabasePassword = "", DistributedCachePassword = "", JwtSigningKey = "" };

            // Act
            await service.ApplyRecommendedDefaultsAsync(secrets);
            NexusSecrets loaded = await service.LoadSecretsAsync();

            // Assert
            Assert.Equal("http://localhost:55555", loaded.MasterServerAddress);
            Assert.Equal("MerrickDatabasePassword123!", loaded.DatabasePassword);
            Assert.Equal("DistributedCachePassword123!", loaded.DistributedCachePassword);
            Assert.Equal("NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!", loaded.JwtSigningKey);
            Assert.Equal("LocalDisk", loaded.ReplayStorageProvider);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadSecretsAsync_ResolvesMasterServerAddress_FromAliasKeys()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_ms_test_{Guid.NewGuid():N}.json");
        try
        {
            JsonObject seeded = new() { ["MasterServer:Address"] = "https://api.kongor.net" };
            await File.WriteAllTextAsync(tempFile, seeded.ToJsonString());

            SecretsManagerService service = new(tempFile);

            // Act
            NexusSecrets loaded = await service.LoadSecretsAsync();

            // Assert
            Assert.Equal("https://api.kongor.net", loaded.MasterServerAddress);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SecretsManagerService_SaveSecretsAsync_SynchronizesMasterServerAddressAndGateway()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_save_ms_test_{Guid.NewGuid():N}.json");
        try
        {
            SecretsManagerService service = new(tempFile);
            NexusSecrets secrets = new() { MasterServerAddress = "http://192.168.1.100:55555" };

            // Act
            await service.SaveSecretsAsync(secrets);

            // Assert
            string rawSaved = await File.ReadAllTextAsync(tempFile);
            JsonObject? savedObj = JsonNode.Parse(rawSaved)?.AsObject();

            Assert.NotNull(savedObj);
            Assert.Equal("http://192.168.1.100:55555", savedObj["MasterServer:Address"]?.GetValue<string>());
            Assert.Equal("http://192.168.1.100:55555", savedObj["master-server-address"]?.GetValue<string>());
            Assert.Equal("http://192.168.1.100:55555", savedObj["MASTER_SERVER_ADDRESS"]?.GetValue<string>());
            Assert.Equal("192.168.1.100:55555", savedObj["Infrastructure:Gateway"]?.GetValue<string>());
            Assert.Equal("192.168.1.100:55555", savedObj["INFRASTRUCTURE_GATEWAY"]?.GetValue<string>());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SecretsManagerService_CompelPriorityModeAndContainerTarget_LoadsFromAliases()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"nexus_secrets_compel_aliases_{Guid.NewGuid():N}.json");
        try
        {
            JsonObject seeded = new()
            {
                ["COMPEL_PRIORITY_MODE"] = "Adaptive",
                ["COMPEL_CONTAINER_RUNTIME"] = "Minikube"
            };
            await File.WriteAllTextAsync(tempFile, seeded.ToJsonString());

            SecretsManagerService service = new(tempFile);

            // Act
            NexusSecrets loaded = await service.LoadSecretsAsync();

            // Assert
            Assert.Equal("Adaptive", loaded.CompelPriorityMode);
            Assert.Equal("Minikube", loaded.ContainerRuntimeTarget);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
