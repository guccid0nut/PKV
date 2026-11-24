// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Packaging;
using NEXUS.Contracts.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

public sealed class S2zArchiveProviderTests
{
    [Fact]
    public void SupportsExtension_WhenInvoked_IdentifiesS2zAndZip()
    {
        using S2zArchiveProvider provider = new();
        provider.SupportsExtension(".s2z").Should().BeTrue();
        provider.SupportsExtension("s2z").Should().BeTrue();
        provider.SupportsExtension(".zip").Should().BeTrue();
        provider.SupportsExtension("zip").Should().BeTrue();
        provider.SupportsExtension(".honmod").Should().BeFalse();
        provider.SupportsExtension(".txt").Should().BeFalse();
    }

    [Fact]
    public async Task Provider_CreateWriterAndOpenReader_RoundTripsCorrectly()
    {
        using S2zArchiveProvider provider = new();
        using MemoryStream ms = new();

        // 1. Write archive entries using IArchiveWriter
        using (IArchiveWriter writer = await provider.CreateWriterAsync(ms))
        {
            await writer.WriteEntryAsync("heroes/astryon/hero.entity",
                Encoding.UTF8.GetBytes("<hero name='Astryon' />"));
            await writer.WriteEntryAsync("ui/menu.package", Encoding.UTF8.GetBytes("<package />"));
            await writer.FlushAsync();
        }

        ms.Position = 0;

        // 2. Read archive entries using IArchiveReader
        using IArchiveReader reader = await provider.OpenReaderAsync(ms);
        reader.EntryNames.Should().HaveCount(2);
        reader.HasEntry("heroes/astryon/hero.entity").Should().BeTrue();
        reader.HasEntry("ui/menu.package").Should().BeTrue();
        reader.HasEntry("heroes/astryon/missing.txt").Should().BeFalse();

        string heroEntity = await reader.ReadEntryTextAsync("heroes/astryon/hero.entity");
        heroEntity.Should().Be("<hero name='Astryon' />");

        byte[] menuBytes = await reader.ReadEntryBytesAsync("ui/menu.package");
        Encoding.UTF8.GetString(menuBytes).Should().Be("<package />");

        await using Stream stream = await reader.OpenEntryStreamAsync("heroes/astryon/hero.entity");
        using StreamReader sr = new(stream, Encoding.UTF8);
        string streamText = await sr.ReadToEndAsync();
        streamText.Should().Be("<hero name='Astryon' />");
    }
}
