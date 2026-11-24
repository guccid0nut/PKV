// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientFolderNormalizerTests.cs
// System: WILLOWMAKER Unit Tests - Client Directory Normalization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class ClientFolderNormalizerTests
{
    [Fact]
    public void NormalizeClientDirectory_WhenCanonicalFolderExists_ReturnsCanonicalPathWithoutRenaming()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClientNorm_Canonical_" + Guid.NewGuid().ToString("N"));
        string gamesDir = Path.Combine(tempRoot, "games");
        string clientDir = Path.Combine(gamesDir, "client");
        Directory.CreateDirectory(clientDir);

        try
        {
            string exePath = Path.Combine(clientDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string? result = ClientFolderNormalizer.NormalizeClientDirectory(tempRoot);

            Assert.NotNull(result);
            Assert.Equal(clientDir, result);
            Assert.True(Directory.Exists(clientDir));
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
    public void NormalizeClientDirectory_WhenNonStandardClientFolderExists_RenamesToGamesClient()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClientNorm_NonStd_" + Guid.NewGuid().ToString("N"));
        string gamesDir = Path.Combine(tempRoot, "games");
        string nonStandardDir = Path.Combine(gamesDir, "HoN Game Client v4.10.1 Windows Clean Installation");
        Directory.CreateDirectory(nonStandardDir);

        try
        {
            string exePath = Path.Combine(nonStandardDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string expectedCanonical = Path.Combine(gamesDir, "client");
            Assert.False(Directory.Exists(expectedCanonical));

            string? result = ClientFolderNormalizer.NormalizeClientDirectory(tempRoot);

            Assert.NotNull(result);
            Assert.Equal(expectedCanonical, result);
            Assert.True(Directory.Exists(expectedCanonical), "Canonical games/client must exist after rename.");
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
    public void NormalizeClientDirectory_WhenFolderContainsServer_DoesNotRename_StrictDomainSeparation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClientNorm_ServerIgnore_" + Guid.NewGuid().ToString("N"));
        string gamesDir = Path.Combine(tempRoot, "games");
        string serverDir = Path.Combine(gamesDir, "HoN Game Server v4.10.1 Windows Clean Installation");
        Directory.CreateDirectory(serverDir);

        try
        {
            string exePath = Path.Combine(serverDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string? result = ClientFolderNormalizer.NormalizeClientDirectory(tempRoot);

            Assert.Null(result);
            Assert.True(Directory.Exists(serverDir), "Server folder must NOT be modified or renamed by Willowmaker.");
            Assert.False(Directory.Exists(Path.Combine(gamesDir, "client")));
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
