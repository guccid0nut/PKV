// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using COMPEL.Configuration;
using COMPEL.Services.ContentBroker;
using COMPEL.Services.Supervision;
using Xunit;

namespace COMPEL.Tests;

public sealed class CompelStandaloneTests
{
    [Fact]
    public void DedicatedWorkerArguments_Build_IncludesClientPackageFilesAndSharedInCanonicalOrder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"compel-mod-test-{Guid.NewGuid():N}");
        string installDir = Path.Combine(tempRoot, "server");
        string sharedDir = Path.Combine(tempRoot, "shared");

        try
        {
            Directory.CreateDirectory(Path.Combine(installDir, "base"));
            Directory.CreateDirectory(Path.Combine(installDir, "game"));
            Directory.CreateDirectory(Path.Combine(installDir, "ClientPackageFiles"));
            Directory.CreateDirectory(sharedDir);
            Directory.CreateDirectory(Path.Combine(installDir, "KONGOR", "extensions"));
            Directory.CreateDirectory(Path.Combine(installDir, "override"));
            Directory.CreateDirectory(Path.Combine(installDir, "server"));

            MatchServerManagerOptions options = new()
            {
                UserName = "TEST",
                Password = "pwd",
                Instances = 1,
                Gateway = "127.0.0.1"
            };
            PortPlan plan = new(options);
            DedicatedServerWorker worker = new(1, 11235, 11735, 21235, 21735, "Server #1");

            string[] args = DedicatedWorkerArguments.Build(
                worker,
                options,
                plan,
                serverAddress: "127.0.0.1",
                masterServerHostAndPort: "127.0.0.1:55555",
                installationDirectory: installDir);

            int modIndex = Array.IndexOf(args, "-mod");
            Assert.True(modIndex >= 0);
            string modStr = args[modIndex + 1];

            // Canonical order: base;game;ClientPackageFiles;../shared;KONGOR/extensions;override;server
            Assert.Equal("base;game;ClientPackageFiles;../shared;KONGOR/extensions;override;server", modStr);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DistributionSynchronisationService_EnsureWhitespacePathOnWindows_CreatesJunctionForSpaceFreeDirectoryWithTrailingSlash()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string tempRoot = Path.Combine(Path.GetTempPath(), $"compel-whitespace-{Guid.NewGuid():N}");
        string serverDirNoSpace = Path.Combine(tempRoot, "games", "server");

        try
        {
            Directory.CreateDirectory(serverDirNoSpace);
            File.WriteAllText(Path.Combine(serverDirNoSpace, HeroesOfNewerthExecutable.FileName), "dummy");
            Directory.CreateDirectory(Path.Combine(serverDirNoSpace, "base"));

            // Pass with a trailing separator to verify normalization
            string serverDirWithSlash = serverDirNoSpace + Path.DirectorySeparatorChar;

            string resolved = DistributionSynchronisationService.EnsureWhitespacePathOnWindows(serverDirWithSlash);

            Assert.Contains("hon server", resolved, StringComparison.OrdinalIgnoreCase);
            Assert.True(HeroesOfNewerthExecutable.CanLaunchInstancesFrom(resolved));
            Assert.True(Directory.Exists(resolved));
            Assert.True(DistributionSynchronisationService.IsValidGameDirectory(resolved));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void MatchServerManagerSupervisor_FormatRelativePreloadPath_NormalizesSpaceFreeRelativePaths()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "My Games", "ProjectKongor", "games", "server");
        string libPath = Path.Combine(tempRoot, "compatibility", "libk2_native_guard.so");

        // 1. Working directory = games/server
        string workingDir1 = tempRoot;
        string rel1 = MatchServerManagerSupervisor.FormatRelativePreloadPath(workingDir1, libPath);
        Assert.Equal("./compatibility/libk2_native_guard.so", rel1);
        Assert.DoesNotContain(" ", rel1);

        // 2. Working directory = games/server/linux-x64
        string workingDir2 = Path.Combine(tempRoot, "linux-x64");
        string rel2 = MatchServerManagerSupervisor.FormatRelativePreloadPath(workingDir2, libPath);
        Assert.Equal("./../compatibility/libk2_native_guard.so", rel2);
        Assert.DoesNotContain(" ", rel2);
    }

    [Fact]
    public void MatchServerManagerSupervisor_IsDirectoryWritable_ValidatesDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"compel-writable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Assert.True(MatchServerManagerSupervisor.IsDirectoryWritable(tempDir));
            Assert.False(MatchServerManagerSupervisor.IsDirectoryWritable(Path.Combine(tempDir, "missing_subdir")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
