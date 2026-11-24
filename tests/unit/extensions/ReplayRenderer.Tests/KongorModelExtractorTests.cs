// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: KongorModelExtractorTests.cs
// System: ReplayRenderer.Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using WILLOWMAKER.Core.Services;
using Xunit;

namespace ReplayRenderer.Tests;

public sealed class KongorModelExtractorTests
{
    [Fact]
    public void ExtractKongorModelToCacheFile_WhenInvoked_ShouldLocateAndDecodeClientTexture()
    {
        string currentDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(currentDir) && !File.Exists(Path.Combine(currentDir, "ProjectKongor.slnx")))
        {
            string? parent = Directory.GetParent(currentDir)?.FullName;
            if (parent == null || parent == currentDir) break;
            currentDir = parent;
        }

        string clientRoot = Path.Combine(currentDir, "games", "shared");
        if (!Directory.Exists(clientRoot)) clientRoot = Path.Combine(currentDir, "games", "client");
        string? cacheFile = KongorModelExtractor.ExtractKongorModelToCacheFile(clientRoot);
        Assert.NotNull(cacheFile);
        Assert.True(File.Exists(cacheFile), $"Cache file not found at: {cacheFile}");
        Assert.True(new FileInfo(cacheFile).Length > 54, "Cache file size too small");
    }
}

