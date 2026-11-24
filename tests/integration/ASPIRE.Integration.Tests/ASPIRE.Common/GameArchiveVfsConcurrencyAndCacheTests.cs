// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using Kongor.Core.Services.Vfs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.ASPIRE.Common;

public sealed class GameArchiveVfsConcurrencyAndCacheTests : IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly string _tempGameDir;

    public GameArchiveVfsConcurrencyAndCacheTests()
    {
        _tempGameDir = Path.Combine(Path.GetTempPath(), "VfsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempGameDir);

        CreateTestZipArchive(Path.Combine(_tempGameDir, "resources0.s2z"));
        CreateTestZipArchive(Path.Combine(_tempGameDir, "textures.s2z"));

        _memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 10 * 1024 * 1024 // 10 MB test limit
        });
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        if (Directory.Exists(_tempGameDir))
            try
            {
                Directory.Delete(_tempGameDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
    }

    private static void CreateTestZipArchive(string archivePath)
    {
        using FileStream fs = new(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create, false);
        ZipArchiveEntry textEntry = zip.CreateEntry("heroes/test_hero/hero.entity");
        using (Stream writer = textEntry.Open())
        {
            using StreamWriter sw = new(writer);
            sw.Write("<hero displayname=\"TestHero\" primaryattribute=\"Strength\" difficulty=\"1.5\" />");
            sw.Flush();
        }

        ZipArchiveEntry ddsEntry = zip.CreateEntry("heroes/test_hero/icon.tga");
        using (Stream writer = ddsEntry.Open())
        {
            byte[] dummyData = new byte[128];
            Array.Fill(dummyData, (byte)0xFF);
            writer.Write(dummyData, 0, dummyData.Length);
            writer.Flush();
        }

        ZipArchiveEntry wavEntry = zip.CreateEntry("heroes/test_hero/sounds/attack_1.wav");
        using (Stream writer = wavEntry.Open())
        {
            byte[] dummySound = new byte[256];
            Array.Fill(dummySound, (byte)0xAA);
            writer.Write(dummySound, 0, dummySound.Length);
            writer.Flush();
        }
    }

    [Fact]
    public async Task OpenFile_ConcurrentAccess_ExtractsEntriesWithoutContention()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GameClient:GamePath"] = _tempGameDir })
            .Build();

        using GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance, _memoryCache);

        List<Task> tasks = [];
        for (int i = 0; i < 20; i++)
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    using Stream? stream = vfs.OpenFile("heroes/test_hero/sounds/attack_1.wav");
                    Assert.NotNull(stream);
                    Assert.Equal(256, stream.Length);

                    string? text = vfs.ReadAllText("heroes/test_hero/hero.entity");
                    Assert.NotNull(text);
                    Assert.Contains("TestHero", text);
                }
            }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void GameArchiveVfsConcurrencyAndCache_MemoryCache_StoresAndRetrievesCachedEntries()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GameClient:GamePath"] = _tempGameDir })
            .Build();

        using GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance, _memoryCache);

        HeroEntityInfoDto? heroInfo = vfs.GetHeroEntityInfo("test_hero");
        Assert.NotNull(heroInfo);
        Assert.Equal("TestHero", heroInfo.DisplayName);
        Assert.Equal("Strength", heroInfo.PrimaryAttribute);

        // Verify it was placed into IMemoryCache
        bool foundInCache = _memoryCache.TryGetValue("vfs:hero_info:test_hero", out HeroEntityInfoDto? cachedHero);
        Assert.True(foundInCache);
        Assert.NotNull(cachedHero);
        Assert.Equal("TestHero", cachedHero.DisplayName);

        // Subsequent call returns cached version
        HeroEntityInfoDto? secondCall = vfs.GetHeroEntityInfo("test_hero");
        Assert.Same(cachedHero, secondCall);
    }

    [Fact]
    public void GameArchiveVfsConcurrencyAndCache_TextureBytes_UsesMemoryCache()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GameClient:GamePath"] = _tempGameDir })
            .Build();

        using GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance, _memoryCache);

        byte[]? textureBytes = vfs.GetTextureBytes("heroes/test_hero/icon.tga");
        Assert.NotNull(textureBytes);
        Assert.Equal(128, textureBytes.Length);

        bool foundInCache = _memoryCache.TryGetValue("vfs:tex:heroes/test_hero/icon.tga", out byte[]? cachedBytes);
        Assert.True(foundInCache);
        Assert.NotNull(cachedBytes);
        Assert.Equal(128, cachedBytes.Length);
    }
}
