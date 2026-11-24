// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Xunit;

namespace Kongor.Core.Tests.Vfs;

public sealed class PhysicalFileSystemProviderTests : IDisposable
{
    private readonly string _tempRoot;

    public PhysicalFileSystemProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "KongorPhysicalVfsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            try
            {
                Directory.Delete(_tempRoot, true);
            }
            catch
            {
                /* ignore */
            }
    }

    [Fact]
    public async Task PhysicalProvider_WriteAndRead_InteractsWithDiskCorrectly()
    {
        using PhysicalFileSystemProvider vfs = new(_tempRoot);
        byte[] payload = Encoding.UTF8.GetBytes("Testing physical provider");

        await vfs.WriteAllBytesAsync("sub/dir/test.txt", payload);

        string physicalPath = Path.Combine(_tempRoot, "sub", "dir", "test.txt");
        File.Exists(physicalPath).Should().BeTrue();
        vfs.FileExists("sub/dir/test.txt").Should().BeTrue();

        byte[] read = await vfs.ReadAllBytesAsync("sub/dir/test.txt");
        read.Should().Equal(payload);

        string text = await vfs.ReadAllTextAsync("sub/dir/test.txt");
        text.Should().Be("Testing physical provider");
    }

    [Fact]
    public void PhysicalProvider_PathTraversal_ThrowsUnauthorizedAccessException()
    {
        using PhysicalFileSystemProvider vfs = new(_tempRoot);

        Action act = () => vfs.FileExists("../../escaped.txt");
        act.Should().Throw<UnauthorizedAccessException>();

        Action writeAct = () => vfs.WriteAllText("../escaped.txt", "evil");
        writeAct.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void PhysicalProvider_EnumerateFiles_ReturnsRelativePaths()
    {
        using PhysicalFileSystemProvider vfs = new(_tempRoot);
        vfs.WriteAllText("heroes/astryon/hero.entity", "<hero />");
        vfs.WriteAllText("heroes/astryon/model.mdf", "<mdf />");
        vfs.WriteAllText("ui/main.interface", "<interface />");

        List<string> heroFiles = [.. vfs.EnumerateFiles("heroes/astryon")];
        heroFiles.Should().HaveCount(2);
        heroFiles.Should().Contain("heroes/astryon/hero.entity");
        heroFiles.Should().Contain("heroes/astryon/model.mdf");

        List<string> allFiles = [.. vfs.EnumerateFiles("", "*", SearchOption.AllDirectories)];
        allFiles.Should().HaveCount(3);
    }
}
