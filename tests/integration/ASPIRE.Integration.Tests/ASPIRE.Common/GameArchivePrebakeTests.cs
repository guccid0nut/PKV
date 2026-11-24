// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Core.Services.Vfs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.ASPIRE.Common;

public class GameArchivePrebakeTests : IDisposable
{
    private readonly string _tempPrebakedDir;

    public GameArchivePrebakeTests()
    {
        _tempPrebakedDir = Path.Combine(Path.GetTempPath(), "nexus_prebaked_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempPrebakedDir, "models"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempPrebakedDir)) Directory.Delete(_tempPrebakedDir, true);
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public void GetHeroModelGlb_WhenPrebakedFileExists_ReturnsPrebakedBytesDirectly()
    {
        // Arrange
        byte[] expectedGlbBytes = [0x67, 0x6C, 0x54, 0x46, 0x02, 0x00, 0x00, 0x00]; // glTF binary header
        string targetFile = Path.Combine(_tempPrebakedDir, "models", "pyromancer_default.glb");
        File.WriteAllBytes(targetFile, expectedGlbBytes);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GameClient:GamePath"] = _tempPrebakedDir,
                ["GameClient:PreBakedAssetPath"] = _tempPrebakedDir
            })
            .Build();

        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        GameArchiveVfsService service = new(config, NullLogger<GameArchiveVfsService>.Instance, memoryCache);

        // Act
        byte[]? glbResult = service.GetHeroModelGlb("pyromancer");

        // Assert
        Assert.NotNull(glbResult);
        Assert.Equal(expectedGlbBytes, glbResult);
    }
}
