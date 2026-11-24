// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: S2zArchiveWriterTests.cs
// System: Kongor.Core.Tests - [L46-P1-03] K2 1.5 GiB Signed 32-Bit & Zip64 Guard
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security;
using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Packaging;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class S2zArchiveWriterTests
{
    [Fact]
    public async Task WriteArchiveAsync_ValidArchive_PassesK232BitAndNonZip64IntegrityVerification()
    {
        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>
        {
            ["ui/hud.interface"] = Encoding.UTF8.GetBytes("<interface name='hud' />"),
            ["heroes/axe/hero.entity"] = Encoding.UTF8.GetBytes("<hero name='Hero_Legionnaire' />")
        };

        using MemoryStream ms = new MemoryStream();
        await S2zArchiveWriter.WriteArchiveAsync(ms, files, CompressionLevel.Fastest, leaveOpen: true);

        byte[] archiveBytes = ms.ToArray();
        bool valid = S2zArchiveWriter.ValidateArchiveBufferIntegrity(archiveBytes, out int entryCount);
        valid.Should().BeTrue();
        entryCount.Should().Be(2);
        S2zArchiveWriter.ContainsZip64RecordsOrOverflowedEocd(archiveBytes).Should().BeFalse();
    }

    [Fact]
    public void ValidateArchiveBufferIntegrity_InjectedZip64LocatorOrSentinelOverflow_ReturnsFalse()
    {
        byte[] validZipBytes;
        using (MemoryStream ms = new MemoryStream())
        {
            using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = zip.CreateEntry("ui/test.lua", CompressionLevel.NoCompression);
                using Stream es = entry.Open();
                es.Write(Encoding.UTF8.GetBytes("return true"));
            }

            validZipBytes = ms.ToArray();
        }

        S2zArchiveWriter.ValidateArchiveBufferIntegrity(validZipBytes, out _).Should().BeTrue();

        // 1. Simulate Zip64 EOCD Locator (0x07064B50) inserted 20 bytes before standard 22-byte EOCD
        int eocdPos = validZipBytes.Length - 22;
        byte[] withZip64Locator = new byte[validZipBytes.Length + 20];
        Buffer.BlockCopy(validZipBytes, 0, withZip64Locator, 0, eocdPos);
        BinaryPrimitives.WriteUInt32LittleEndian(withZip64Locator.AsSpan(eocdPos, 4), 0x07064B50u);
        Buffer.BlockCopy(validZipBytes, eocdPos, withZip64Locator, eocdPos + 20, 22);

        S2zArchiveWriter.ContainsZip64RecordsOrOverflowedEocd(withZip64Locator).Should().BeTrue();
        S2zArchiveWriter.ValidateArchiveBufferIntegrity(withZip64Locator, out _).Should().BeFalse();

        // 2. Simulate 0xFFFFFFFF 32-bit Central Directory offset overflow sentinel in standard EOCD
        byte[] withCdOffsetOverflow = (byte[])validZipBytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(withCdOffsetOverflow.AsSpan(eocdPos + 16, 4), 0xFFFFFFFFu);

        S2zArchiveWriter.ContainsZip64RecordsOrOverflowedEocd(withCdOffsetOverflow).Should().BeTrue();
        S2zArchiveWriter.ValidateArchiveBufferIntegrity(withCdOffsetOverflow, out _).Should().BeFalse();

        // 3. Simulate Central Directory offset exceeding 1.5 GiB K2 signed 32-bit safety limit
        byte[] with15GbCdOffset = (byte[])validZipBytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            with15GbCdOffset.AsSpan(eocdPos + 16, 4),
            (uint)S2zArchiveWriter.MaxSafeS2zArchiveBytes + 16u);

        S2zArchiveWriter.ContainsZip64RecordsOrOverflowedEocd(with15GbCdOffset).Should().BeTrue();
        S2zArchiveWriter.ValidateArchiveBufferIntegrity(with15GbCdOffset, out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteArchiveAsync_ExceedingMaxEntryCountOr15GbStreamOffset_ThrowsInvalidOperationException()
    {
        // 1. Entry count > 65,534 (would trigger Zip64 0xFFFF EOCD sentinel)
        Dictionary<string, byte[]> tooManyEntries = new Dictionary<string, byte[]>(65_540);
        for (int i = 0; i <= S2zArchiveWriter.MaxSafeS2zEntryCount; i++)
        {
            tooManyEntries[$"e/{i}.txt"] = [];
        }

        using MemoryStream msEntries = new MemoryStream();
        Func<Task> entryLimitAct = async () => await S2zArchiveWriter.WriteArchiveAsync(msEntries, tooManyEntries);
        await entryLimitAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*65,534*");

        // 2. Stream positioned right at 1.5 GiB boundary -> writing an archive crosses MaxSafeS2zArchiveBytes without allocating 1.5 GB in RAM!
        using FakeOffsetWriteStream nearLimitStream = new FakeOffsetWriteStream(S2zArchiveWriter.MaxSafeS2zArchiveBytes - 16L);
        Dictionary<string, byte[]> smallFiles = new Dictionary<string, byte[]>
        {
            ["ui/overflow_probe.lua"] = Encoding.UTF8.GetBytes("local x = 1; return x;")
        };

        Func<Task> sizeLimitAct = async () => await S2zArchiveWriter.WriteArchiveAsync(
            nearLimitStream,
            smallFiles,
            CompressionLevel.NoCompression,
            leaveOpen: true);
        await sizeLimitAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*1.5 GiB*");

        // 3. Path traversal inside S2zArchiveWriter is also blocked via ModPackage.NormalizeZipPath
        using MemoryStream msTraversal = new MemoryStream();
        Dictionary<string, byte[]> traversalFiles = new Dictionary<string, byte[]>
        {
            ["../evil_escape.lua"] = Encoding.UTF8.GetBytes("-- bad")
        };

        Func<Task> traversalAct = async () => await S2zArchiveWriter.WriteArchiveAsync(msTraversal, traversalFiles);
        await traversalAct.Should().ThrowAsync<SecurityException>();
    }

    private sealed class FakeOffsetWriteStream(long virtualInitialPosition) : Stream
    {
        private long _virtualPosition = virtualInitialPosition;
        private long _virtualLength = virtualInitialPosition;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _virtualLength;

        public override long Position
        {
            get => _virtualPosition;
            set => _virtualPosition = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            _virtualPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _virtualPosition + offset,
                SeekOrigin.End => _virtualLength + offset,
                _ => _virtualPosition
            };
            return _virtualPosition;
        }

        public override void SetLength(long value) => _virtualLength = value;

        public override void Write(byte[] buffer, int offset, int count)
        {
            _virtualPosition += count;
            if (_virtualPosition > _virtualLength)
            {
                _virtualLength = _virtualPosition;
            }
        }
    }
}
