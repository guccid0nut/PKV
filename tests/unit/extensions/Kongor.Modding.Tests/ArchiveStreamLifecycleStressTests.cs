// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Empirical stress tests challenging stream disposal lifecycle, exception safety,
///     cancellation forwarding, and resource leak prevention in Modding archive readers.
/// </summary>
public sealed class ArchiveStreamLifecycleStressTests
{
    private static byte[] CreateValidHonModZip(string modName = "TestMod", string version = "1.0",
        byte[]? iconBytes = null)
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, true))
        {
            ZipArchiveEntry xmlEntry = archive.CreateEntry("mod.xml");
            using (Stream xmlStream = xmlEntry.Open())
            using (StreamWriter sw = new(xmlStream, Encoding.UTF8))
            {
                sw.Write(
                    $"<mod name='{modName}' version='{version}' author='Tester'><description>Test</description></mod>");
            }

            if (iconBytes != null)
            {
                ZipArchiveEntry iconEntry = archive.CreateEntry("icon.png");
                using Stream iconStream = iconEntry.Open();
                iconStream.Write(iconBytes, 0, iconBytes.Length);
            }
        }

        return ms.ToArray();
    }

    private static byte[] CreateZipWithoutModXml()
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("other_file.txt");
            using Stream s = entry.Open();
            using StreamWriter sw = new(s, Encoding.UTF8);
            sw.Write("No manifest here");
        }

        return ms.ToArray();
    }

    private static byte[] CreateValidS2zZip(params (string Name, string Content)[] entries)
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream s = entry.Open();
                using StreamWriter sw = new(s, Encoding.UTF8);
                sw.Write(content);
            }
        }

        return ms.ToArray();
    }

    // ==========================================
    // HonModArchiveReader Stream Lifecycle Tests
    // ==========================================

    [Fact]
    public async Task HonMod_CorruptedZip_DisposesUnderlyingStream_WhenOwnsArchive()
    {
        byte[] corruptedData = [0x50, 0x4B, 0x00, 0x00, 0xFF, 0xFF, 0xAA];
        TrackingStream stream = new(corruptedData);

        Func<Task> act = async () => await HonModArchiveReader.ReadPackageAsync(stream, "corrupt.honmod", false);

        await act.Should().ThrowAsync<Exception>();
        stream.IsDisposed.Should()
            .BeTrue("Underlying stream must be disposed on corrupted archive parsing when leaveOpen=false");
    }

    [Fact]
    public async Task HonMod_CorruptedZip_LeavesOpen_WhenLeaveOpenIsTrue()
    {
        byte[] corruptedData = [0x50, 0x4B, 0x00, 0x00, 0xFF, 0xFF, 0xAA];
        TrackingStream stream = new(corruptedData);

        Func<Task> act = async () => await HonModArchiveReader.ReadPackageAsync(stream, "corrupt.honmod", true);

        await act.Should().ThrowAsync<Exception>();
        stream.IsDisposed.Should().BeFalse("Underlying stream must NOT be disposed when leaveOpen=true");
    }

    [Fact]
    public async Task HonMod_MissingModXml_DisposesUnderlyingStream_WhenOwnsArchive()
    {
        byte[] zipBytes = CreateZipWithoutModXml();
        TrackingStream stream = new(zipBytes);

        Func<Task> act = async () => await HonModArchiveReader.ReadPackageAsync(stream, "no_manifest.honmod", false);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*mod.xml*");
        stream.IsDisposed.Should()
            .BeTrue("Underlying stream must be disposed when mod.xml is missing and leaveOpen=false");
    }

    [Fact]
    public async Task HonMod_ValidPackage_DisposeModPackage_DisposesUnderlyingStream()
    {
        byte[] zipBytes = CreateValidHonModZip("TestMod", "1.0.0", [1, 2, 3, 4]);
        TrackingStream stream = new(zipBytes);

        ModPackage package = await HonModArchiveReader.ReadPackageAsync(stream, "valid.honmod", false);

        package.Should().NotBeNull();
        package.Manifest.Name.Should().Be("TestMod");
        package.IconData.Should().Equal(1, 2, 3, 4);
        stream.IsDisposed.Should().BeFalse("Stream should remain active while ModPackage is alive");

        package.Dispose();
        stream.IsDisposed.Should().BeTrue("Disposing ModPackage must dispose underlying stream when leaveOpen=false");
    }

    [Fact]
    public async Task HonMod_ValidPackage_LeaveOpen_PreservesStreamOnPackageDispose()
    {
        byte[] zipBytes = CreateValidHonModZip("TestMod2", "2.0.0");
        TrackingStream stream = new(zipBytes);

        ModPackage package = await HonModArchiveReader.ReadPackageAsync(stream, "valid2.honmod", true);

        package.Should().NotBeNull();
        stream.IsDisposed.Should().BeFalse();

        package.Dispose();
        stream.IsDisposed.Should()
            .BeFalse("Disposing ModPackage must NOT dispose underlying stream when leaveOpen=true");
    }

    [Fact]
    public async Task HonMod_CancellationToken_PropagatesCancellationImmediately()
    {
        byte[] zipBytes = CreateValidHonModZip();
        TrackingStream stream = new(zipBytes);

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-canceled token

        Func<Task> act = async () =>
            await HonModArchiveReader.ReadPackageAsync(stream, "cancel.honmod", false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ==========================================
    // S2zArchiveReader Stream Lifecycle Tests
    // ==========================================

    [Fact]
    public void S2z_Open_TrackingStream_DisposesUnderlyingStreamOnReaderDispose()
    {
        byte[] zipBytes = CreateValidS2zZip(("heroes/test.entity", "<entity />"));
        TrackingStream stream = new(zipBytes);

        S2zArchiveReader reader = S2zArchiveReader.Open(stream, "resources.s2z", false);
        reader.HasEntry("heroes/test.entity").Should().BeTrue();
        stream.IsDisposed.Should().BeFalse();

        reader.Dispose();
        stream.IsDisposed.Should()
            .BeTrue("Disposing S2zArchiveReader must dispose underlying stream when leaveOpen=false");
    }

    [Fact]
    public void S2z_Open_TrackingStream_LeavesOpenWhenRequested()
    {
        byte[] zipBytes = CreateValidS2zZip(("heroes/test.entity", "<entity />"));
        TrackingStream stream = new(zipBytes);

        S2zArchiveReader reader = S2zArchiveReader.Open(stream, "resources.s2z", true);
        reader.Dispose();

        stream.IsDisposed.Should()
            .BeFalse("Disposing S2zArchiveReader must NOT dispose underlying stream when leaveOpen=true");
    }

    [Fact]
    public async Task S2z_OpenEntryStreamAsync_MissingEntry_ThrowsFileNotFoundException()
    {
        byte[] zipBytes = CreateValidS2zZip(("heroes/test.entity", "<entity />"));
        using S2zArchiveReader reader = S2zArchiveReader.Open(new MemoryStream(zipBytes), "test.s2z", false);

        Func<Task> act = async () => await reader.OpenEntryStreamAsync("non_existent.entity");

        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage("*non_existent.entity*");
    }

    [Fact]
    public async Task S2z_OpenEntryStreamAsync_ReturnsIndependentSeekableMemoryStream()
    {
        byte[] zipBytes = CreateValidS2zZip(("scripts/logic.lua", "return 42"));
        using S2zArchiveReader reader = S2zArchiveReader.Open(new MemoryStream(zipBytes), "test.s2z", false);

        Stream entryStream = await reader.OpenEntryStreamAsync("scripts/logic.lua");

        entryStream.Should().NotBeNull();
        entryStream.CanSeek.Should().BeTrue();
        entryStream.Position.Should().Be(0);

        using StreamReader sr = new(entryStream, Encoding.UTF8);
        string text = await sr.ReadToEndAsync();
        text.Should().Be("return 42");

        // Disposing entry stream should not invalidate reader
        entryStream.Dispose();
        reader.HasEntry("scripts/logic.lua").Should().BeTrue();
        (await reader.ReadEntryTextAsync("scripts/logic.lua")).Should().Be("return 42");
    }

    [Fact]
    public async Task S2z_DisposedReader_ThrowsObjectDisposedExceptionAcrossAllOperations()
    {
        byte[] zipBytes = CreateValidS2zZip(("file.txt", "hello"));
        S2zArchiveReader reader = S2zArchiveReader.Open(new MemoryStream(zipBytes), "test.s2z", false);
        reader.Dispose();

        Func<Task> actStream = async () => await reader.OpenEntryStreamAsync("file.txt");
        Func<Task> actBytes = async () => await reader.ReadEntryBytesAsync("file.txt");
        Func<Task> actText = async () => await reader.ReadEntryTextAsync("file.txt");

        await actStream.Should().ThrowAsync<ObjectDisposedException>();
        await actBytes.Should().ThrowAsync<ObjectDisposedException>();
        await actText.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task S2z_CancellationToken_AbortsReadEntryBytesAsync()
    {
        byte[] zipBytes = CreateValidS2zZip(("huge.bin", new string('A', 10000)));
        using S2zArchiveReader reader = S2zArchiveReader.Open(new MemoryStream(zipBytes), "test.s2z", false);

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-canceled token

        Func<Task> act = async () => await reader.ReadEntryBytesAsync("huge.bin", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class TrackingStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }

        public override bool CanRead => !IsDisposed && _inner.CanRead;
        public override bool CanSeek => !IsDisposed && _inner.CanSeek;
        public override bool CanWrite => !IsDisposed && _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                DisposeCount++;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
