// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.IconsController;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class IconsControllerTests
{
    [Fact]
    public void DefaultAvatarPng_ShouldBeAuthenticImage_NotDummyWhiteSquare()
    {
        string? dir = AppContext.BaseDirectory;
        string? foundPath = null;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "Resources", "Icons", "default.png");
            if (File.Exists(candidate))
            {
                foundPath = candidate;
                break;
            }
            string srcCandidate = Path.Combine(dir, "src", "services", "master-server", "Resources", "Icons", "default.png");
            if (File.Exists(srcCandidate))
            {
                foundPath = srcCandidate;
                break;
            }
            DirectoryInfo? parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        foundPath.Should().NotBeNull("Resources/Icons/default.png should exist");
        byte[] bytes = File.ReadAllBytes(foundPath!);

        // PNG Header verification: 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A
        bytes.Length.Should().BeGreaterThan(500, "Authentic HoN shield icon must have full texture data, not a dummy 67-byte quad");
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50); // 'P'
        bytes[2].Should().Be(0x4E); // 'N'
        bytes[3].Should().Be(0x47); // 'G'

        // Check PNG dimensions from IHDR chunk (bytes 16-23: width and height big-endian)
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        width.Should().Be(64);
        height.Should().Be(64);
    }

    [Fact]
    public void FallbackIconBytes_InIconsController_ShouldMatchAuthenticPngHeaderAndSize()
    {
        Type controllerType = typeof(IconsController);
        controllerType.Should().NotBeNull();

        FieldInfo? field = controllerType.GetField("FallbackIconBytes", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("FallbackIconBytes static field must be present in IconsController");

        byte[]? fallbackBytes = field!.GetValue(null) as byte[];
        fallbackBytes.Should().NotBeNull();
        fallbackBytes!.Length.Should().BeGreaterThan(500, "Fallback icon bytes should be authentic HoN shield icon, not a 1x1 68-byte white placeholder");

        // Verify valid PNG header
        fallbackBytes[0].Should().Be(0x89);
        fallbackBytes[1].Should().Be(0x50);
        fallbackBytes[2].Should().Be(0x4E);
        fallbackBytes[3].Should().Be(0x47);

        // Verify 64x64 dimensions in IHDR
        int width = (fallbackBytes[16] << 24) | (fallbackBytes[17] << 16) | (fallbackBytes[18] << 8) | fallbackBytes[19];
        int height = (fallbackBytes[20] << 24) | (fallbackBytes[21] << 16) | (fallbackBytes[22] << 8) | fallbackBytes[23];
        width.Should().Be(64);
        height.Should().Be(64);
    }
}
