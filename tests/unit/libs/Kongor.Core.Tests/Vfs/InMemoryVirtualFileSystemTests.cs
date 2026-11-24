// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Xunit;

namespace Kongor.Core.Tests.Vfs;

public sealed class InMemoryVirtualFileSystemTests
{
    [Fact]
    public async Task InMemoryVirtualFileSystem_WriteAndReadBytes_RoundTripsCorrectly()
    {
        using InMemoryVirtualFileSystem vfs = new();
        byte[] data = [1, 2, 3, 4, 5, 42, 255];

        await vfs.WriteAllBytesAsync("assets/textures/test.png", data);

        vfs.FileExists("assets/textures/test.png").Should().BeTrue();
        vfs.DirectoryExists("assets/textures").Should().BeTrue();
        vfs.DirectoryExists("assets").Should().BeTrue();

        byte[] read = await vfs.ReadAllBytesAsync("assets/textures/test.png");
        read.Should().Equal(data);

        byte[] syncRead = vfs.ReadAllBytes("assets/textures/test.png");
        syncRead.Should().Equal(data);
    }

    [Fact]
    public async Task InMemoryVirtualFileSystem_WriteAndReadText_RoundTripsCorrectly()
    {
        using InMemoryVirtualFileSystem vfs = new();
        string text = "Hello Virtual File System!\nTesting line 2.";

        await vfs.WriteAllTextAsync("configs/game.cfg", text);

        vfs.FileExists("configs/game.cfg").Should().BeTrue();
        string read = await vfs.ReadAllTextAsync("configs/game.cfg");
        read.Should().Be(text);

        string syncRead = vfs.ReadAllText("configs/game.cfg");
        syncRead.Should().Be(text);
    }

    [Fact]
    public async Task InMemoryVirtualFileSystem_OpenRead_ReturnsValidSeekableStream()
    {
        using InMemoryVirtualFileSystem vfs = new();
        byte[] expected = Encoding.UTF8.GetBytes("Stream Content Payload");
        vfs.WriteAllBytes("stream/test.dat", expected);

        await using Stream asyncStream = await vfs.OpenReadAsync("stream/test.dat");
        asyncStream.Should().NotBeNull();
        asyncStream.Length.Should().Be(expected.Length);

        using MemoryStream ms = new();
        await asyncStream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(expected);

        using Stream syncStream = vfs.OpenRead("stream/test.dat");
        syncStream.Length.Should().Be(expected.Length);
    }

    [Fact]
    public void InMemoryVirtualFileSystem_DeleteFile_RemovesFileAndLeavesOtherFilesIntact()
    {
        using InMemoryVirtualFileSystem vfs = new();
        vfs.WriteAllText("dir/file1.txt", "1");
        vfs.WriteAllText("dir/file2.txt", "2");

        vfs.FileExists("dir/file1.txt").Should().BeTrue();
        vfs.DeleteFile("dir/file1.txt");

        vfs.FileExists("dir/file1.txt").Should().BeFalse();
        vfs.FileExists("dir/file2.txt").Should().BeTrue();
        vfs.DirectoryExists("dir").Should().BeTrue();
    }

    [Fact]
    public void InMemoryVirtualFileSystem_EnumerateFiles_FiltersCorrectly()
    {
        using InMemoryVirtualFileSystem vfs = new();
        vfs.WriteAllText("heroes/astryon/hero.entity", "<hero />");
        vfs.WriteAllText("heroes/astryon/model.mdf", "<model />");
        vfs.WriteAllText("heroes/astryon/sounds/attack_1.wav", "RIFF");
        vfs.WriteAllText("heroes/pyromancer/hero.entity", "<hero />");
        vfs.WriteAllText("shared/config.xml", "<config />");

        // Top directory only in heroes/astryon
        List<string> topFiles = [.. vfs.EnumerateFiles("heroes/astryon")];
        topFiles.Should().HaveCount(2);
        topFiles.Should().Contain("heroes/astryon/hero.entity");
        topFiles.Should().Contain("heroes/astryon/model.mdf");

        // All directories in heroes/astryon
        List<string> allFiles = [.. vfs.EnumerateFiles("heroes/astryon", "*", SearchOption.AllDirectories)];
        allFiles.Should().HaveCount(3);
        allFiles.Should().Contain("heroes/astryon/sounds/attack_1.wav");

        // Pattern matching
        List<string> entities = [.. vfs.EnumerateFiles("heroes", "*.entity", SearchOption.AllDirectories)];
        entities.Should().HaveCount(2);
        entities.Should().Contain("heroes/astryon/hero.entity");
        entities.Should().Contain("heroes/pyromancer/hero.entity");
    }

    [Fact]
    public void InMemoryVirtualFileSystem_EnumerateDirectories_ReturnsUniqueChildDirectories()
    {
        using InMemoryVirtualFileSystem vfs = new();
        vfs.WriteAllText("game/heroes/astryon/hero.entity", "");
        vfs.WriteAllText("game/heroes/pyromancer/hero.entity", "");
        vfs.WriteAllText("game/ui/elements/minimap.package", "");

        List<string> dirs = [.. vfs.EnumerateDirectories("game")];
        dirs.Should().HaveCount(2);
        dirs.Should().Contain("game/heroes");
        dirs.Should().Contain("game/ui");
    }

    [Fact]
    public void InMemoryVirtualFileSystem_Mount_ExposesSubVfsUnderMountPrefix()
    {
        using InMemoryVirtualFileSystem rootVfs = new();
        using InMemoryVirtualFileSystem subVfs = new();

        subVfs.WriteAllText("sub_asset.txt", "Mounted Sub Content");
        rootVfs.Mount("mounted/sub", subVfs, 10);

        rootVfs.DirectoryExists("mounted").Should().BeTrue();
        rootVfs.DirectoryExists("mounted/sub").Should().BeTrue();
        rootVfs.FileExists("mounted/sub/sub_asset.txt").Should().BeTrue();
        rootVfs.ReadAllText("mounted/sub/sub_asset.txt").Should().Be("Mounted Sub Content");

        rootVfs.Unmount("mounted/sub");
        rootVfs.FileExists("mounted/sub/sub_asset.txt").Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryVirtualFileSystem_ConcurrentAccess_IsThreadSafe()
    {
        using InMemoryVirtualFileSystem vfs = new();

        Task[] tasks =
        [
            .. Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                string path = $"parallel/dir_{i % 5}/file_{i}.txt";
                string content = $"Content {i}";
                await vfs.WriteAllTextAsync(path, content);
                string read = await vfs.ReadAllTextAsync(path);
                read.Should().Be(content);
            }))
        ];

        await Task.WhenAll(tasks);

        List<string> allFiles = [.. vfs.EnumerateFiles("parallel", "*.txt", SearchOption.AllDirectories)];
        allFiles.Should().HaveCount(50);
    }
}
