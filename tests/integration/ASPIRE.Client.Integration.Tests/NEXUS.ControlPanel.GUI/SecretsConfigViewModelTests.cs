// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class SecretsConfigViewModelTests
{
    [Fact]
    public void SecretsConfigViewModel_WhenExecuted_InitializesWithMaskedPasswords()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets
        {
            DiscordClientSecret = "secret123",
            DatabasePassword = "password123",
            DistributedCachePassword = "cache123",
            JwtSigningKey = "jwtkey123",
            CompelPath = @"C:\Games\COMPEL",
            GameClientPath = @"C:\Games\HoN",
            GrafanaOtlpEndpoint = "https://otlp.grafana.net",
            PyroscopeServerAddress = "https://profiles.grafana.net",
            PyroscopeBasicAuthPassword = "pyropassword123"
        });

        // Act
        SecretsConfigViewModel vm = new(mockSecrets.Object);

        Assert.False(vm.RevealMasterServerAddress);
        Assert.False(vm.RevealDiscordClientSecret);
        Assert.False(vm.RevealDatabasePassword);
        Assert.False(vm.RevealDistributedCachePassword);
        Assert.False(vm.RevealJwtSigningKey);
        Assert.False(vm.RevealCompelPath);
        Assert.False(vm.RevealGameClientPath);
        Assert.False(vm.RevealGrafanaOtlpEndpoint);
        Assert.False(vm.RevealPyroscopeServerAddress);
        Assert.False(vm.RevealPyroscopePassword);
        Assert.False(vm.RevealCloudSecretKey);
        Assert.Equal('•', vm.MasterServerAddressMask);
        Assert.Equal('•', vm.DiscordClientSecretMask);
        Assert.Equal('•', vm.DatabasePasswordMask);
        Assert.Equal('•', vm.CompelPathMask);
        Assert.Equal('•', vm.GameClientPathMask);
        Assert.Equal('•', vm.GrafanaOtlpEndpointMask);
        Assert.Equal('•', vm.PyroscopeServerAddressMask);
        Assert.Equal('•', vm.PyroscopePasswordMask);
    }

    [Fact]
    public void SecretsConfigViewModel_PasswordVisibilityToggles_ToggleCorrectly()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object);

        // Act & Assert Master Server Address
        Assert.False(vm.RevealMasterServerAddress);
        vm.ToggleRevealCommand.Execute("master-server-address");
        Assert.True(vm.RevealMasterServerAddress);
        Assert.Equal('\0', vm.MasterServerAddressMask);

        // Act & Assert Discord
        Assert.False(vm.RevealDiscordClientSecret);
        vm.ToggleRevealCommand.Execute("discord-client-secret");
        Assert.True(vm.RevealDiscordClientSecret);
        Assert.Equal('\0', vm.DiscordClientSecretMask);

        // Act & Assert Game Client Path
        Assert.False(vm.RevealGameClientPath);
        vm.ToggleRevealCommand.Execute("game-client-path");
        Assert.True(vm.RevealGameClientPath);
        Assert.Equal('\0', vm.GameClientPathMask);

        // Act & Assert Grafana Endpoint
        Assert.False(vm.RevealGrafanaOtlpEndpoint);
        vm.ToggleRevealCommand.Execute("grafana-otlp-endpoint");
        Assert.True(vm.RevealGrafanaOtlpEndpoint);
        Assert.Equal('\0', vm.GrafanaOtlpEndpointMask);

        // Act & Assert Pyroscope Address
        Assert.False(vm.RevealPyroscopeServerAddress);
        vm.ToggleRevealCommand.Execute("pyroscope-server-address");
        Assert.True(vm.RevealPyroscopeServerAddress);
        Assert.Equal('\0', vm.PyroscopeServerAddressMask);

        // Act & Assert Pyroscope Password
        Assert.False(vm.RevealPyroscopePassword);
        vm.ToggleRevealCommand.Execute("pyroscope-password");
        Assert.True(vm.RevealPyroscopePassword);
        Assert.Equal('\0', vm.PyroscopePasswordMask);
    }

    [Fact]
    public void SecretsConfigViewModel_ReplayStorageProvider_ControlsStorageVisibilityFlags()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object) { ReplayStorageProvider = "LocalDisk" };

        // Act 1: LocalDisk
        Assert.True(vm.IsLocalDiskStorage);
        Assert.False(vm.IsCloudStorage);

        // Act 2: Cloud
        vm.ReplayStorageProvider = "Cloud";
        Assert.False(vm.IsLocalDiskStorage);
        Assert.True(vm.IsCloudStorage);
    }

    [Fact]
    public void SecretsConfigViewModel_DocumentationModal_OpensAndClosesCorrectly()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object);

        Assert.False(vm.IsDocModalOpen);

        // Act 1: Open Discord Guide
        vm.ShowDocCommand.Execute("discord");
        Assert.True(vm.IsDocModalOpen);
        Assert.Contains("Discord", vm.DocModalTitle);
        Assert.False(string.IsNullOrEmpty(vm.DocModalUrl));

        // Act 2: Open COMPEL Guide
        vm.ShowDocCommand.Execute("compel");
        Assert.True(vm.IsDocModalOpen);
        Assert.Contains("COMPEL", vm.DocModalTitle);

        // Act 3: Open Grafana Guide
        vm.ShowDocCommand.Execute("grafana");
        Assert.True(vm.IsDocModalOpen);
        Assert.Contains("Grafana", vm.DocModalTitle);

        // Act 4: Close Modal
        vm.CloseDocModalCommand.Execute(null);
        Assert.False(vm.IsDocModalOpen);
    }

    [Fact]
    public async Task HasChanges_TracksPropertyEdits_AndResetsOnSave()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        NexusSecrets initial = new()
        {
            DiscordClientId = "111222333",
            DiscordClientSecret = "secret123",
            CompelPath = @"C:\Games\COMPEL"
        };
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(initial);
        mockSecrets.Setup(s =>
                s.SaveSecretsAsync(It.IsAny<NexusSecrets>(), It.IsAny<NexusSecrets?>(), CancellationToken.None))
            .Returns(Task.CompletedTask);

        SecretsConfigViewModel vm = new(mockSecrets.Object);
        await vm.LoadSecretsAsync();

        // Assert initially no changes
        Assert.False(vm.HasChanges);

        // Act 1: Edit CompelPath
        vm.CompelPath = @"C:\NewPath\COMPEL";
        Assert.True(vm.HasChanges);

        // Act 2: Revert CompelPath
        vm.CompelPath = @"C:\Games\COMPEL";
        Assert.False(vm.HasChanges);

        // Act 3: Edit Discord Client Secret & Save
        vm.DiscordClientSecret = "NewSecret456!";
        Assert.True(vm.HasChanges);

        await vm.SaveSecretsAsync();

        // Assert changes reset after successful save
        Assert.False(vm.HasChanges);
        mockSecrets.Verify(
            s => s.SaveSecretsAsync(It.IsAny<NexusSecrets>(), It.IsAny<NexusSecrets?>(), CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public void SecretsConfigViewModel_SetPresetMasterServerAddressCommand_UpdatesMasterServerAddress()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object);

        // Act & Assert Presets
        vm.SetPresetMasterServerAddressCommand.Execute("http://localhost:55555");
        Assert.Equal("http://localhost:55555", vm.MasterServerAddress);

        vm.SetPresetMasterServerAddressCommand.Execute("https://api.kongor.net");
        Assert.Equal("https://api.kongor.net", vm.MasterServerAddress);

        vm.SetPresetMasterServerAddressCommand.Execute("https://masterserver.newbs.io");
        Assert.Equal("https://masterserver.newbs.io", vm.MasterServerAddress);
    }

    [Fact]
    public void ShowDoc_MasterServer_OpensMasterServerGuide()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object);

        // Act
        vm.ShowDocCommand.Execute("masterserver");

        // Assert
        Assert.True(vm.IsDocModalOpen);
        Assert.Contains("Master Server", vm.DocModalTitle);
        Assert.Equal("GATEWAY", vm.DocModalBadge);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenUsernameMissing_DisplaysWarning()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object)
        {
            HostAccountUserName = "",
            HostAccountPassword = "validpassword"
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("⚠️", vm.AuthenticationTestStatusIcon);
        Assert.Contains("Username", vm.AuthenticationTestFeedback);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenPasswordMissing_DisplaysWarning()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());
        SecretsConfigViewModel vm = new(mockSecrets.Object)
        {
            HostAccountUserName = "SuperHost",
            HostAccountPassword = ""
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("⚠️", vm.AuthenticationTestStatusIcon);
        Assert.Contains("Password", vm.AuthenticationTestFeedback);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenCredentialsValidAndServerHost_DisplaysSuccess()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        string requestedUrl = string.Empty;
        string requestBody = string.Empty;

        using HttpClient client = new(new MockHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString() ?? string.Empty;
            if (req.Content != null)
            {
                requestBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("a:5:{s:9:\"server_id\";i:1;s:7:\"session\";s:32:\"abc12345\";s:8:\"official\";i:1;s:12:\"chat_address\";s:9:\"127.0.0.1\";s:9:\"chat_port\";i:11031;}")
            };
        }));

        SecretsConfigViewModel vm = new(mockSecrets.Object, httpClient: client)
        {
            MasterServerAddress = "https://masterserver.newbs.io",
            HostAccountUserName = "SuperHost",
            HostAccountPassword = "MySecretPassword123"
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("✅", vm.AuthenticationTestStatusIcon);
        Assert.Contains("verified", vm.AuthenticationTestFeedback);
        Assert.Contains("SuperHost", vm.AuthenticationTestFeedback);
        Assert.Contains("https://masterserver.newbs.io/server_requester.php", requestedUrl);
        Assert.Contains("f=replay_auth", requestBody);
        Assert.Contains("login=SuperHost", requestBody);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenNotServerHost_DisplaysNotServerHostWarning()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        using HttpClient client = new(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Account SuperHost Is Not A Server Host")
        }));

        SecretsConfigViewModel vm = new(mockSecrets.Object, httpClient: client)
        {
            MasterServerAddress = "https://masterserver.newbs.io",
            HostAccountUserName = "SuperHost",
            HostAccountPassword = "password123"
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("❌", vm.AuthenticationTestStatusIcon);
        Assert.Contains("ServerHost permissions", vm.AuthenticationTestFeedback);
        Assert.Contains("AccountType != 2", vm.AuthenticationTestFeedback);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenIncorrectPassword_DisplaysIncorrectPasswordError()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        using HttpClient client = new(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Incorrect Password")
        }));

        SecretsConfigViewModel vm = new(mockSecrets.Object, httpClient: client)
        {
            MasterServerAddress = "https://masterserver.newbs.io",
            HostAccountUserName = "SuperHost",
            HostAccountPassword = "wrongpassword"
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("❌", vm.AuthenticationTestStatusIcon);
        Assert.Contains("Incorrect password", vm.AuthenticationTestFeedback);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenAccountNotFound_DisplaysNotFoundError()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        using HttpClient client = new(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent("Account Not Found")
        }));

        SecretsConfigViewModel vm = new(mockSecrets.Object, httpClient: client)
        {
            MasterServerAddress = "https://masterserver.newbs.io",
            HostAccountUserName = "NonExistentUser",
            HostAccountPassword = "password123"
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("❌", vm.AuthenticationTestStatusIcon);
        Assert.Contains("404", vm.AuthenticationTestFeedback);
        Assert.Contains("does not exist", vm.AuthenticationTestFeedback);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public async Task TestAuthenticationAsync_WhenServerConnectionFails_DisplaysConnectionError()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        using HttpClient client = new(new MockHttpMessageHandler(_ => throw new HttpRequestException("Connection refused")));

        SecretsConfigViewModel vm = new(mockSecrets.Object, httpClient: client)
        {
            MasterServerAddress = "http://127.0.0.1:9999",
            HostAccountUserName = "SuperHost",
            HostAccountPassword = "password123"
        };

        // Act
        await vm.TestAuthenticationAsync();

        // Assert
        Assert.Equal("❌", vm.AuthenticationTestStatusIcon);
        Assert.Contains("Connection Failed", vm.AuthenticationTestFeedback);
        Assert.False(vm.IsTestingAuthentication);
    }

    [Fact]
    public void SecretsConfigViewModel_UnlockAllItemsToggle_TracksChangesAndDiscardsCorrectly()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets
        {
            UnlockAllItems = true
        });

        SecretsConfigViewModel vm = new(mockSecrets.Object);

        // Assert Initial State
        Assert.True(vm.UnlockAllItems);
        Assert.False(vm.HasStoreChanges);
        Assert.False(vm.HasChanges);

        // Act - Toggle to Progression Mode
        vm.UnlockAllItems = false;

        // Assert - Change Tracking Triggered
        Assert.True(vm.HasStoreChanges);
        Assert.True(vm.HasChanges);

        // Act - Discard Changes
        vm.DiscardChanges();

        // Assert - Restored
        Assert.True(vm.UnlockAllItems);
        Assert.False(vm.HasStoreChanges);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task SupabaseDatabasePassword_TwoWaySyncsAndUrlEncodesSpecialCharacters_AndTracksHasSupabaseChanges()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets
        {
            DatabaseProvider = "Supabase",
            SupabaseDatabaseConnectionString =
                "postgresql://postgres.xyzref:InitialPass99@aws-0-us-east-1.pooler.supabase.com:5432/postgres"
        });

        SecretsConfigViewModel vm = new(mockSecrets.Object);
        await vm.LoadSecretsAsync();

        Assert.Equal("InitialPass99", vm.SupabaseDatabasePassword);
        Assert.False(vm.RevealSupabaseDatabasePassword);
        Assert.Equal('•', vm.SupabaseDatabasePasswordMask);
        Assert.False(vm.HasSupabaseChanges);

        // Act 1: Edit password with URI-reserved characters (@, :, #, %, /)
        vm.SupabaseDatabasePassword = "p@ss:w#rd%1/2";

        // Assert 1: Connection string URI is automatically updated + URL-encoded and Save Section flag turns true
        Assert.Equal(
            "postgresql://postgres.xyzref:p%40ss%3Aw%23rd%251%2F2@aws-0-us-east-1.pooler.supabase.com:5432/postgres",
            vm.SupabaseDatabaseConnectionString);
        Assert.True(vm.HasSupabaseChanges);
        Assert.True(vm.HasDatabaseChanges);
        Assert.True(vm.HasChanges);

        // Act 2: Edit connection string directly -> unescapes back into password box
        vm.SupabaseDatabaseConnectionString =
            "postgresql://postgres.xyzref:Rotated%21Pass%402@aws-0-us-east-1.pooler.supabase.com:5432/postgres";
        Assert.Equal("Rotated!Pass@2", vm.SupabaseDatabasePassword);

        // Act 3: Toggle password reveal
        vm.ToggleRevealCommand.Execute("supabase-db-password");
        Assert.True(vm.RevealSupabaseDatabasePassword);
        Assert.Equal('\0', vm.SupabaseDatabasePasswordMask);

        // Act 4: Discard restores both fields cleanly
        vm.DiscardChanges();
        Assert.Equal("InitialPass99", vm.SupabaseDatabasePassword);
        Assert.False(vm.HasSupabaseChanges);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task SupabaseS3EndpointWatermark_MasksDerivedProjectRef_UntilSupabaseUrlRevealed()
    {
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets
        {
            SupabaseUrl = "https://hiddenprojref.supabase.co"
        });

        SecretsConfigViewModel vm = new(mockSecrets.Object);
        await vm.LoadSecretsAsync();

        // Effective endpoint remains available for SigV4 S3 test, while UI watermark masks the hidden project ref
        Assert.False(vm.RevealSupabaseUrl);
        Assert.Equal("https://hiddenprojref.supabase.co/storage/v1/s3", vm.EffectiveSupabaseS3Endpoint);
        Assert.Equal("https://<project-ref>.supabase.co/storage/v1/s3", vm.SupabaseS3EndpointWatermark);
        Assert.DoesNotContain("hiddenprojref", vm.SupabaseS3EndpointWatermark, StringComparison.OrdinalIgnoreCase);

        vm.ToggleRevealCommand.Execute("supabase-url");
        Assert.True(vm.RevealSupabaseUrl);
        Assert.Equal("https://hiddenprojref.supabase.co/storage/v1/s3", vm.SupabaseS3EndpointWatermark);
    }

    [Fact]
    public async Task SupabaseDashboardAndDocs_ShipGenericUrls_WithoutPersonalOrgSlug()
    {
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        SecretsConfigViewModel vm = new(mockSecrets.Object);
        await vm.LoadSecretsAsync();

        Assert.Equal("https://supabase.com/dashboard", vm.ResolveSupabaseDashboardUrl());
        Assert.DoesNotContain("/org/", vm.ResolveSupabaseDashboardUrl(), StringComparison.OrdinalIgnoreCase);

        vm.SupabaseUrl = "https://xyzcompany.supabase.co";
        Assert.Equal("https://supabase.com/dashboard/project/xyzcompany", vm.ResolveSupabaseDashboardUrl());

        vm.ShowDocCommand.Execute("supabase");
        Assert.Equal("https://supabase.com/dashboard", vm.DocModalUrl);
        Assert.DoesNotContain("/org/", vm.DocModalUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestSupabaseConnectionAsync_VerifiesPostgresProbe_FailsOnAuthErrorAndSucceedsWhenValid()
    {
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets());

        using HttpClient client = new(new MockHttpMessageHandler(req =>
        {
            string path = req.RequestUri?.AbsolutePath ?? string.Empty;
            string body = path.Contains("/storage/v1/bucket", StringComparison.OrdinalIgnoreCase)
                ? "[\"replays\",\"avatars\",\"clans\",\"mods\"]"
                : "[]";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }));

        bool shouldSucceed = false;
        string capturedConnStr = string.Empty;

        SecretsConfigViewModel vm = new(
            mockSecrets.Object,
            httpClient: client,
            postgresProbe: (connStr, _) =>
            {
                capturedConnStr = connStr;
                return Task.FromResult(shouldSucceed
                    ? (true, 8L, "Postgres Auth OK (8ms)")
                    : (false, 11L, "Postgres auth failed (SQLSTATE 28P01): password authentication failed"));
            });
        await vm.LoadSecretsAsync();

        vm.SupabaseUrl = "https://xyzcompany.supabase.co";
        vm.SupabaseAnonKey = "anon-key";
        vm.SupabaseDatabaseConnectionString =
            "postgresql://postgres.xyzcompany:WrongPass@aws-0-us-east-1.pooler.supabase.com:5432/postgres";

        // Act 1: Bad password fails the connection test instead of reporting a false green
        await vm.TestSupabaseConnectionAsync();
        Assert.False(vm.IsSupabaseStatusSuccess);
        Assert.True(vm.IsSupabaseStatusVisible);
        Assert.Contains("28P01", vm.SupabaseStatusText);

        // Act 2: Updating password via dedicated box splices into connection string and passes probe
        shouldSucceed = true;
        vm.SupabaseDatabasePassword = "CorrectRotatedPassword!";
        await vm.TestSupabaseConnectionAsync();

        Assert.True(vm.IsSupabaseStatusSuccess);
        Assert.Contains("Postgres Auth OK", vm.SupabaseStatusText);
        Assert.Contains("CorrectRotatedPassword%21", capturedConnStr);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}

