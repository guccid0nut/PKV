// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Kongor.Core.Vfs;
using Xunit;

namespace Kongor.Core.Tests.Vfs;

public sealed class SharedClientAssetLinkerTests : IDisposable
{
    private readonly string _tempRoot;

    public SharedClientAssetLinkerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "KongorAssetLinkerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Best-effort cleanup of temp directories skipped: {ex.Message}");
            }
        }
    }

    [Fact]
    public void EnsureLinked_ExposesClientS2zInServerGameDir_AndSkipsExcludedFiles()
    {
        string clientDir = Path.Combine(_tempRoot, "client");
        string serverDir = Path.Combine(_tempRoot, "server");
        string clientGameDir = Path.Combine(clientDir, "game");
        string clientBaseDir = Path.Combine(clientDir, "base");
        string serverGameDir = Path.Combine(serverDir, "game");
        string serverBaseDir = Path.Combine(serverDir, "base");

        Directory.CreateDirectory(clientGameDir);
        Directory.CreateDirectory(clientBaseDir);
        Directory.CreateDirectory(serverDir);

        File.WriteAllText(Path.Combine(clientGameDir, "resources0.s2z"), "shared_resource_data");
        File.WriteAllText(Path.Combine(clientGameDir, "textures.s2z"), "client_texture_data");
        File.WriteAllText(Path.Combine(clientGameDir, "game_x64.dll"), "client_game_binary");
        File.WriteAllText(Path.Combine(clientGameDir, "resources0.s2z.bak"), "backup_data");
        File.WriteAllText(Path.Combine(clientBaseDir, "core.s2z"), "base_core_data");

        AssetLinkSummary summary = SharedClientAssetLinker.EnsureLinked(serverDir, clientDir);

        summary.LinkedDirs.Should().BeGreaterThan(0);
        summary.LinkedFiles.Should().BeGreaterThan(0);

        File.Exists(Path.Combine(serverGameDir, "resources0.s2z")).Should().BeTrue();
        File.Exists(Path.Combine(serverBaseDir, "core.s2z")).Should().BeTrue();

        File.Exists(Path.Combine(serverGameDir, "textures.s2z")).Should().BeFalse();
        File.Exists(Path.Combine(serverGameDir, "game_x64.dll")).Should().BeFalse();
        File.Exists(Path.Combine(serverGameDir, "resources0.s2z.bak")).Should().BeFalse();
    }

    [Fact]
    public void EnsureLinked_PreservesExactS2zLengthAndBytes()
    {
        string clientDir = Path.Combine(_tempRoot, "client");
        string serverDir = Path.Combine(_tempRoot, "server");
        string clientGameDir = Path.Combine(clientDir, "game");
        string serverGameDir = Path.Combine(serverDir, "game");

        Directory.CreateDirectory(clientGameDir);
        Directory.CreateDirectory(serverGameDir);

        byte[] s2zPayload = new byte[2048];
        Random.Shared.NextBytes(s2zPayload);
        string clientS2z = Path.Combine(clientGameDir, "resources0.s2z");
        File.WriteAllBytes(clientS2z, s2zPayload);

        // Pre-create server-owned game_settings.cfg
        string serverSettings = Path.Combine(serverGameDir, "game_settings.cfg");
        File.WriteAllText(serverSettings, "server_setting_value=123");

        // Seed client game_settings.cfg which must NOT overwrite server's
        File.WriteAllText(Path.Combine(clientGameDir, "game_settings.cfg"), "client_setting_value=999");

        AssetLinkSummary summary = SharedClientAssetLinker.EnsureLinked(serverDir, clientDir);

        string linkedS2z = Path.Combine(serverGameDir, "resources0.s2z");
        File.Exists(linkedS2z).Should().BeTrue();
        new FileInfo(linkedS2z).Length.Should().Be(s2zPayload.Length);
        File.ReadAllBytes(linkedS2z).Should().Equal(s2zPayload);

        File.ReadAllText(serverSettings).Should().Be("server_setting_value=123");
    }

    [Fact]
    public void TryCreateDirectoryJunction_CreatesWorkingDirectoryLinkUnprivileged()
    {
        string targetDir = Path.Combine(_tempRoot, "target");
        string linkDir = Path.Combine(_tempRoot, "junction_link");
        Directory.CreateDirectory(targetDir);

        string markerFile = Path.Combine(targetDir, "marker.txt");
        File.WriteAllText(markerFile, "unprivileged_junction_test_marker");

        bool result = SharedClientAssetLinker.TryCreateDirectoryJunction(linkDir, targetDir);

        result.Should().BeTrue();
        Directory.Exists(linkDir).Should().BeTrue();
        (new DirectoryInfo(linkDir).Attributes & FileAttributes.ReparsePoint).Should().NotBe(0);

        string readMarker = File.ReadAllText(Path.Combine(linkDir, "marker.txt"));
        readMarker.Should().Be("unprivileged_junction_test_marker");
    }

    [Fact]
    public void EnsureLinked_WhenRunTwice_IsIdempotent()
    {
        string clientDir = Path.Combine(_tempRoot, "client");
        string serverDir = Path.Combine(_tempRoot, "server");
        Directory.CreateDirectory(Path.Combine(clientDir, "base"));
        Directory.CreateDirectory(Path.Combine(clientDir, "game"));
        Directory.CreateDirectory(serverDir);

        File.WriteAllText(Path.Combine(clientDir, "game", "resources0.s2z"), "idempotent_archive_content");

        List<string> logs = new();
        AssetLinkSummary first = SharedClientAssetLinker.EnsureLinked(serverDir, clientDir, logs.Add);
        first.LinkedDirs.Should().BeGreaterThan(0);
        first.LinkedFiles.Should().BeGreaterThan(0);

        logs.Clear();
        AssetLinkSummary second = SharedClientAssetLinker.EnsureLinked(serverDir, clientDir, logs.Add);
        second.LinkedDirs.Should().Be(0);
        second.LinkedFiles.Should().Be(0);
        second.CopiedSmallFiles.Should().Be(0);

        logs.Should().Contain(msg => msg.Contains("[AssetLinker] linked 0 dirs, 0 files, copied 0 small files"));
    }

    [Fact]
    public void EnsureLinked_UsesHardLinkOrSymlinkWithoutDiskDuplication()
    {
        string clientDir = Path.Combine(_tempRoot, "client");
        string serverDir = Path.Combine(_tempRoot, "server");
        string clientGameDir = Path.Combine(clientDir, "game");
        string serverGameDir = Path.Combine(serverDir, "game");

        Directory.CreateDirectory(clientGameDir);
        Directory.CreateDirectory(serverDir);

        string clientFile = Path.Combine(clientGameDir, "resources0.s2z");
        File.WriteAllText(clientFile, "initial_data");

        AssetLinkSummary summary = SharedClientAssetLinker.EnsureLinked(serverDir, clientDir);
        summary.LinkedFiles.Should().Be(1);

        string serverFile = Path.Combine(serverGameDir, "resources0.s2z");
        File.Exists(serverFile).Should().BeTrue();

        // Mutate client file by appending bytes
        using (FileStream fs = new(clientFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (StreamWriter writer = new(fs))
        {
            writer.Write("_appended_bytes");
        }

        string serverContent = File.ReadAllText(serverFile);
        serverContent.Should().Be("initial_data_appended_bytes");
    }
}
