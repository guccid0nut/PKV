// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ServerFolderNormalizerTests.cs
// System: COMPEL Unit Tests - Dedicated Server Directory Normalization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using COMPEL.Services.Supervision;
using Xunit;

namespace COMPEL.Tests;

public sealed class ServerFolderNormalizerTests
{
    [Fact]
    public void NormalizeServerDirectory_WhenCanonicalFolderExists_ReturnsCanonicalPathWithoutRenaming()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ServerNorm_Canonical_" + Guid.NewGuid().ToString("N"));
        string gamesDir = Path.Combine(tempRoot, "games");
        string serverDir = Path.Combine(gamesDir, "server");
        Directory.CreateDirectory(serverDir);

        try
        {
            string exePath = Path.Combine(serverDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string? result = ServerFolderNormalizer.NormalizeServerDirectory(tempRoot);

            Assert.NotNull(result);
            Assert.Equal(serverDir, result);
            Assert.True(Directory.Exists(serverDir));
            Assert.True(File.Exists(exePath));
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
    public void NormalizeServerDirectory_WhenNonStandardServerFolderExists_RenamesToGamesServer()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ServerNorm_NonStd_" + Guid.NewGuid().ToString("N"));
        string gamesDir = Path.Combine(tempRoot, "games");
        string nonStandardDir = Path.Combine(gamesDir, "HoN Game Server v4.10.1 Windows Clean Installation");
        Directory.CreateDirectory(nonStandardDir);

        try
        {
            string exePath = Path.Combine(nonStandardDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string expectedCanonical = Path.Combine(gamesDir, "server");
            Assert.False(Directory.Exists(expectedCanonical));

            string? result = ServerFolderNormalizer.NormalizeServerDirectory(tempRoot);

            Assert.NotNull(result);
            Assert.Equal(expectedCanonical, result);
            Assert.True(Directory.Exists(expectedCanonical), "Canonical games/server must exist after rename.");
            Assert.False(Directory.Exists(nonStandardDir), "Old non-standard directory must no longer exist.");
            Assert.True(File.Exists(Path.Combine(expectedCanonical, "hon_x64.exe")));
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
    public void NormalizeServerDirectory_WhenFolderContainsClient_DoesNotRename_StrictDomainSeparation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ServerNorm_ClientIgnore_" + Guid.NewGuid().ToString("N"));
        string gamesDir = Path.Combine(tempRoot, "games");
        string clientDir = Path.Combine(gamesDir, "HoN Game Client v4.10.1 Windows Clean Installation");
        Directory.CreateDirectory(clientDir);

        try
        {
            string exePath = Path.Combine(clientDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string? result = ServerFolderNormalizer.NormalizeServerDirectory(tempRoot);

            Assert.Null(result);
            Assert.True(Directory.Exists(clientDir), "Client folder must NOT be modified or renamed by COMPEL.");
            Assert.False(Directory.Exists(Path.Combine(gamesDir, "server")));
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
    public void NormalizeServerDirectory_WhenDeepBuildDirectory_TraversesUpToFindCanonicalGamesServer()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ServerNorm_Deep_" + Guid.NewGuid().ToString("N"));
        string deepSubdir = Path.Combine(tempRoot, "src", "apps", "compel", "bin", "Release", "net11.0");
        string gamesDir = Path.Combine(tempRoot, "games");
        string serverDir = Path.Combine(gamesDir, "server");
        Directory.CreateDirectory(deepSubdir);
        Directory.CreateDirectory(serverDir);

        try
        {
            string exePath = Path.Combine(serverDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string? result = ServerFolderNormalizer.NormalizeServerDirectory(deepSubdir);

            Assert.NotNull(result);
            Assert.Equal(serverDir, result);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
