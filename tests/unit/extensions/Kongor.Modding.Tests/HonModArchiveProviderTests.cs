// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Packaging;
using NEXUS.Contracts.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

public sealed class HonModArchiveProviderTests
{
    [Fact]
    public void SupportsExtension_WhenInvoked_IdentifiesHonmodAndZip()
    {
        using HonModArchiveProvider provider = new();
        provider.SupportsExtension(".honmod").Should().BeTrue();
        provider.SupportsExtension("honmod").Should().BeTrue();
        provider.SupportsExtension(".zip").Should().BeTrue();
        provider.SupportsExtension("zip").Should().BeTrue();
        provider.SupportsExtension(".s2z").Should().BeFalse();
        provider.SupportsExtension(".dll").Should().BeFalse();
    }

    [Fact]
    public async Task Provider_CreateWriterAndOpenReader_RoundTripsCorrectly()
    {
        using HonModArchiveProvider provider = new();
        using MemoryStream ms = new();

        // 1. Write archive entries using IArchiveWriter
        using (IArchiveWriter writer = await provider.CreateWriterAsync(ms))
        {
            await writer.WriteEntryAsync("mod.xml", Encoding.UTF8.GetBytes("<mod name='TestMod' version='1.0' />"));
            await writer.WriteEntryAsync("icon.png", [137, 80, 78, 71]);
            await writer.WriteEntryAsync("scripts/init.lua", Encoding.UTF8.GetBytes("print('init')"));
            await writer.FlushAsync();
        }

        ms.Position = 0;

        // 2. Read archive entries using IArchiveReader
        using IArchiveReader reader = await provider.OpenReaderAsync(ms);
        reader.EntryNames.Should().HaveCount(3);
        reader.HasEntry("mod.xml").Should().BeTrue();
        reader.HasEntry("icon.png").Should().BeTrue();
        reader.HasEntry("scripts/init.lua").Should().BeTrue();

        string modXml = await reader.ReadEntryTextAsync("mod.xml");
        modXml.Should().Be("<mod name='TestMod' version='1.0' />");

        byte[] iconBytes = await reader.ReadEntryBytesAsync("icon.png");
        iconBytes.Should().Equal(137, 80, 78, 71);

        string scriptText = await reader.ReadEntryTextAsync("scripts/init.lua");
        scriptText.Should().Be("print('init')");
    }
}
