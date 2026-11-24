// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using Xunit;

namespace NEXUS.ControlPanel.Tests;

public sealed class SecretsManagerStampCacheTests : IDisposable
{
    private readonly string _tempDir;

    public SecretsManagerStampCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KongorSecretsCacheTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup
        }
    }

    // protects: SecretsManagerService 10s in-memory vault cache when _envFilePath is active (used by control panel pollers).
    // fails_when: _cachedVaultStamp is captured from readPaths (length n) instead of stampPaths (length n+1), causing VaultStampsEqual to return false on every call.
    // seam: Real SecretsManagerService(customSecretsPath, envFilePath) reading real vault JSON and .env files on disk.
    [Fact]
    public async Task LoadSecretsAsync_WithActiveEnvFilePath_HitsInMemoryCacheOnSecondCall()
    {
        string vaultPath = Path.Combine(_tempDir, "secrets.json");
        string envPath = Path.Combine(_tempDir, ".env");

        await File.WriteAllTextAsync(vaultPath, """
            {
              "MasterServer:Address": "http://127.0.0.1:55555",
              "HostingMode": "LAN"
            }
            """);
        await File.WriteAllTextAsync(envPath, "MASTER_SERVER_ADDRESS=http://127.0.0.1:55555\n");

        SecretsManagerService sut = new(customSecretsPath: vaultPath, envFilePath: envPath);
        sut.EnvFilePath.Should().Be(envPath);

        NexusSecrets first = await sut.LoadSecretsAsync();
        first.MasterServerAddress.Should().Be("http://127.0.0.1:55555");
        first.HostingMode.Should().Be("LAN");
        sut.DiskLoadCount.Should().Be(1);
        sut.LoadLogCount.Should().Be(1);

        // Second call within 10s cache window with unchanged vault & .env must hit cache (0 extra disk loads/logs).
        NexusSecrets second = await sut.LoadSecretsAsync();
        second.MasterServerAddress.Should().Be("http://127.0.0.1:55555");
        second.HostingMode.Should().Be("LAN");
        sut.DiskLoadCount.Should().Be(1, "active _envFilePath must match cached stampPaths length and hit the in-memory cache");
        sut.LoadLogCount.Should().Be(1);

        // Modifying .env on disk must invalidate the stamp cache and trigger a fresh load + log.
        DateTime prevWrite = File.GetLastWriteTimeUtc(envPath);
        await File.WriteAllTextAsync(envPath, "MASTER_SERVER_ADDRESS=http://127.0.0.1:55556\n");
        File.SetLastWriteTimeUtc(envPath, prevWrite.AddSeconds(2));

        NexusSecrets third = await sut.LoadSecretsAsync();
        third.MasterServerAddress.Should().Be("http://127.0.0.1:55555");
        sut.DiskLoadCount.Should().Be(2);
        sut.LoadLogCount.Should().Be(2);
    }

    // protects: secrets-manager.log deduplication across 10s cache TTL expirations when neither vault nor .env changed on disk.
    // fails_when: LoadSecretsAsync emits LOAD vaults=... on every TTL expiry even when _lastLoggedVaultStamp equals currentStamp.
    // seam: Real SecretsManagerService + InvalidateSecretsCache() simulating TTL expiration without file edits.
    [Fact]
    public async Task LoadSecretsAsync_WhenCacheExpiresWithoutDiskChange_DoesNotEmitDuplicateLoadLog()
    {
        string vaultPath = Path.Combine(_tempDir, "secrets.json");
        string envPath = Path.Combine(_tempDir, ".env");

        await File.WriteAllTextAsync(vaultPath, """
            {
              "MasterServer:Address": "http://127.0.0.1:55555"
            }
            """);
        await File.WriteAllTextAsync(envPath, "MASTER_SERVER_ADDRESS=http://127.0.0.1:55555\n");

        SecretsManagerService sut = new(customSecretsPath: vaultPath, envFilePath: envPath);

        await sut.LoadSecretsAsync();
        sut.DiskLoadCount.Should().Be(1);
        sut.LoadLogCount.Should().Be(1);

        // Simulate cache TTL expiry without changing any file on disk.
        sut.InvalidateSecretsCache();
        await sut.LoadSecretsAsync();

        sut.DiskLoadCount.Should().Be(2);
        sut.LoadLogCount.Should().Be(1, "unchanged vault and .env stamps must not emit redundant LOAD log lines");

        // Changing the vault file on disk must emit a new LOAD log line.
        DateTime prevWrite = File.GetLastWriteTimeUtc(vaultPath);
        await File.WriteAllTextAsync(vaultPath, """
            {
              "MasterServer:Address": "http://127.0.0.1:55557"
            }
            """);
        File.SetLastWriteTimeUtc(vaultPath, prevWrite.AddSeconds(2));

        NexusSecrets reloaded = await sut.LoadSecretsAsync();
        reloaded.MasterServerAddress.Should().Be("http://127.0.0.1:55557");
        sut.DiskLoadCount.Should().Be(3);
        sut.LoadLogCount.Should().Be(2);
    }
}
