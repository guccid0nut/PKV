// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using Xunit;

namespace ASPIRE.Client.Integration.Tests.E2E.FeatureCoverage;

public sealed class SecretsManagerVaultCacheTests
{
    [Fact]
    public async Task SecretsManagerService_LoadSecretsAsync_ServesClonedCacheUntilVaultStampChanges()
    {
        // protects: Control Panel 1s poll loop against redundant JSON disk reads while guaranteeing instant reload on vault edits.
        // fails_when: Mutating a returned NexusSecrets instance corrupts cached state, or on-disk vault updates return stale cached values.
        // seam: Real SecretsManagerService + real filesystem JSON vault stamp (LastWriteTimeUtc + Length).
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_vault_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string vaultPath = Path.Combine(tempDir, "nexus_secrets.json");

        try
        {
            await File.WriteAllTextAsync(vaultPath, """
                                                    {
                                                      "HostingMode": "Local",
                                                      "WanHost": "play.initial.example"
                                                    }
                                                    """);

            SecretsManagerService service = new(customSecretsPath: vaultPath);
            NexusSecrets first = await service.LoadSecretsAsync();
            first.HostingMode.Should().Be("Local");
            first.WanHost.Should().Be("play.initial.example");

            // Mutate caller's returned copy; cached instance must remain isolated via Clone()
            first.WanHost = "mutated-in-memory";
            NexusSecrets second = await service.LoadSecretsAsync();
            second.WanHost.Should().Be("play.initial.example");

            // Update file on disk (changing length and timestamp); cache must invalidate immediately
            await File.WriteAllTextAsync(vaultPath, """
                                                    {
                                                      "HostingMode": "Wan",
                                                      "WanHost": "play.updated-evergreen.example.org"
                                                    }
                                                    """);
            File.SetLastWriteTimeUtc(vaultPath, File.GetLastWriteTimeUtc(vaultPath).AddSeconds(2));

            NexusSecrets third = await service.LoadSecretsAsync();
            third.HostingMode.Should().Be("Wan");
            third.WanHost.Should().Be("play.updated-evergreen.example.org");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
