// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using ASPIRE.Common.Services.EntityCache;
using Kongor.Core.Services.Vfs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Runtime;

public sealed class PrebakedVfsResolutionTests : IDisposable
{
    private readonly string _archivePath;
    private readonly string _overrideDir;
    private readonly string _tempDir;

    public PrebakedVfsResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus_vfs_res_test_" + Guid.NewGuid().ToString("N"));
        _overrideDir = Path.Combine(_tempDir, "override");
        Directory.CreateDirectory(_overrideDir);

        _archivePath = Path.Combine(_tempDir, "resources0_prebaked.s2z");
        using (FileStream fs = new(_archivePath, FileMode.Create, FileAccess.Write))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("heroes/pyromancer/archive_texture.tga");
            using (Stream s = entry.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes("archive_texture_bytes");
                s.Write(bytes, 0, bytes.Length);
            }

            ZipArchiveEntry sharedEntry = zip.CreateEntry("heroes/common/shared.tga");
            using Stream s2 = sharedEntry.Open();
            byte[] bytes2 = Encoding.UTF8.GetBytes("archive_shared_version");
            s2.Write(bytes2, 0, bytes2.Length);
        }

        // Add loose override file to override directory (to test Tier 2 priority over Tier 3 archive)
        string looseFile = Path.Combine(_overrideDir, "heroes", "common", "shared.tga");
        Directory.CreateDirectory(Path.GetDirectoryName(looseFile)!);
        File.WriteAllText(looseFile, "loose_override_version");

        string looseOnlyFile = Path.Combine(_overrideDir, "heroes", "accursed", "loose_icon.tga");
        Directory.CreateDirectory(Path.GetDirectoryName(looseOnlyFile)!);
        File.WriteAllText(looseOnlyFile, "loose_icon_bytes");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            for (int attempt = 0; attempt < 5; attempt++)
                try
                {
                    Directory.Delete(_tempDir, true);
                    break;
                }
                catch
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Yield();
                }
    }

    [Fact]
    public async Task ResolveAsset_PreBakedMemory_ResolvesInstantly()
    {
        EntityCacheContainer container = new()
        {
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 153,
                    Name = "Hero_Accursed",
                    DisplayName = "Accursed",
                    ModelPath = "/heroes/accursed/hd_model.mdf"
                }
            ]
        };

        using PrebakedEntityRegistry registry = new(
            NullLogger<PrebakedEntityRegistry>.Instance,
            overrideDirectory: _overrideDir,
            s2zArchivePath: _archivePath);

        await registry.InitializeFromContainerAsync(container);

        EntityLookupResult result = registry.ResolveAsset("Hero_Accursed");
        Assert.True(result.Found);
        Assert.Equal("PreBakedMemory", result.SourceTier);
        Assert.Equal("/heroes/accursed/hd_model.mdf", result.ResolvedPath);
        Assert.True(result.LatencyMicroseconds >= 0);
    }

    [Fact]
    public async Task ResolveAsset_PreBakedDisk_ResolvesLooseOverrideFiles()
    {
        using PrebakedEntityRegistry registry = new(
            NullLogger<PrebakedEntityRegistry>.Instance,
            overrideDirectory: _overrideDir,
            s2zArchivePath: _archivePath);

        await registry.InitializeFromContainerAsync(new EntityCacheContainer());

        EntityLookupResult result = registry.ResolveAsset("heroes/accursed/loose_icon.tga");
        Assert.True(result.Found);
        Assert.Equal("PreBakedDisk", result.SourceTier);
        Assert.Contains("loose_icon.tga", result.ResolvedPath);
    }

    [Fact]
    public async Task ResolveAsset_VfsArchive_ResolvesArchiveEntries()
    {
        using PrebakedEntityRegistry registry = new(
            NullLogger<PrebakedEntityRegistry>.Instance,
            overrideDirectory: _overrideDir,
            s2zArchivePath: _archivePath);

        await registry.InitializeFromContainerAsync(new EntityCacheContainer());

        EntityLookupResult result = registry.ResolveAsset("heroes/pyromancer/archive_texture.tga");
        Assert.True(result.Found);
        Assert.Equal("VfsArchive", result.SourceTier);
        Assert.Contains("resources0_prebaked.s2z", result.ResolvedPath);
    }

    [Fact]
    public async Task ResolveAsset_NonExistentAsset_ReturnsFalse()
    {
        using PrebakedEntityRegistry registry = new(
            NullLogger<PrebakedEntityRegistry>.Instance,
            overrideDirectory: _overrideDir,
            s2zArchivePath: _archivePath);

        await registry.InitializeFromContainerAsync(new EntityCacheContainer());

        EntityLookupResult result = registry.ResolveAsset("completely/non_existent/resource.dds");
        Assert.False(result.Found);
        Assert.Equal("NotFound", result.SourceTier);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public async Task PrebakedVfsResolution_ResolveAsset_LooseOverrideTakesPrecedenceOverArchive()
    {
        using PrebakedEntityRegistry registry = new(
            NullLogger<PrebakedEntityRegistry>.Instance,
            overrideDirectory: _overrideDir,
            s2zArchivePath: _archivePath);

        await registry.InitializeFromContainerAsync(new EntityCacheContainer());

        // "heroes/common/shared.tga" exists in both override dir and archive
        EntityLookupResult result = registry.ResolveAsset("heroes/common/shared.tga");
        Assert.True(result.Found);
        Assert.Equal("PreBakedDisk", result.SourceTier);
    }

    [Fact]
    public void PrebakedVfsResolution_GameArchiveVfsService_IntegratesLooseOverrideFilesPriority()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GameClient:GamePath"] = _tempDir,
                ["GameClient:OverridePath"] = _overrideDir,
                ["GameClient:PreBakedAssetPath"] = _tempDir
            })
            .Build();

        using IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        using GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance, memoryCache);

        // Loose override file should exist and read loose override contents
        Assert.True(vfs.FileExists("heroes/common/shared.tga"));
        string? text = vfs.ReadAllText("heroes/common/shared.tga");
        Assert.Equal("loose_override_version", text);

        // Archive-only file should still resolve from archive
        Assert.True(vfs.FileExists("heroes/pyromancer/archive_texture.tga"));
        string? archiveText = vfs.ReadAllText("heroes/pyromancer/archive_texture.tga");
        Assert.Equal("archive_texture_bytes", archiveText);
    }
}
