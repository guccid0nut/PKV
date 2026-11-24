// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DatagramAuthenticatorTests.cs
// System: COMPEL Tests - ChaCha20-Poly1305 AEAD Datagram Authenticator Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class DatagramAuthenticatorTests
{
    private static readonly byte[] TestMasterSecret = Encoding.UTF8.GetBytes("SuperSecretMatchSessionCookie12345");

    [Fact]
    public void DeriveSessionKey_ProducesDeterministic32ByteKey()
    {
        byte[] key1 = DatagramAuthenticator.DeriveSessionKey(TestMasterSecret);
        byte[] key2 = DatagramAuthenticator.DeriveSessionKey(TestMasterSecret);

        key1.Length.Should().Be(DatagramAuthenticator.KeySizeBytes);
        key1.Should().Equal(key2);

        byte[] diffSecret = Encoding.UTF8.GetBytes("DifferentSecretKey67890");
        byte[] key3 = DatagramAuthenticator.DeriveSessionKey(diffSecret);
        key3.Should().NotEqual(key1);
    }

    [Fact]
    public void ConstructNonce_FormatsDeterministic12Bytes()
    {
        Span<byte> nonce = stackalloc byte[DatagramAuthenticator.NonceSizeBytes];
        DatagramAuthenticator.ConstructNonce(nonce, sessionId: 0x11223344, DatagramDirection.ClientToServer, packetSequence: 100);

        nonce.Length.Should().Be(12);
        // [0..3]: 0x11223344 (LE)
        nonce[0].Should().Be(0x44);
        nonce[1].Should().Be(0x33);
        nonce[2].Should().Be(0x22);
        nonce[3].Should().Be(0x11);

        // [4..7]: 0x00000001 (Direction: ClientToServer)
        nonce[4].Should().Be(0x01);
        nonce[5].Should().Be(0x00);
        nonce[6].Should().Be(0x00);
        nonce[7].Should().Be(0x00);

        // [8..11]: 100 = 0x00000064 (LE)
        nonce[8].Should().Be(0x64);
        nonce[9].Should().Be(0x00);
        nonce[10].Should().Be(0x00);
        nonce[11].Should().Be(0x00);
    }

    [Fact]
    public void EncryptAndDecrypt_Roundtrip_RestoresPlaintext()
    {
        byte[] key = DatagramAuthenticator.DeriveSessionKey(TestMasterSecret);
        uint sessionId = 555;

        using DatagramAuthenticator sender = new(key, sessionId);
        using DatagramAuthenticator receiver = new(key, sessionId);

        byte[] plaintext = Encoding.UTF8.GetBytes("Pyromancer CastAbility Hero_Legionnaire");
        KongorPacketHeader header = new(sessionId, packetSequence: 1, subTickDeltaUs: 14500, ackSequence: 0, ackTimestampEchoUs: 0);

        byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + plaintext.Length];

        bool encryptOk = sender.TryEncryptAndWrap(
            envelope.AsSpan(),
            in header,
            plaintext.AsSpan(),
            DatagramDirection.ClientToServer,
            out int bytesWritten);

        encryptOk.Should().BeTrue();
        bytesWritten.Should().Be(envelope.Length);

        // Decrypt on receiver
        byte[] decryptedBuffer = new byte[plaintext.Length + 64];

        bool decryptOk = receiver.TryUnwrapAndDecrypt(
            envelope.AsSpan(),
            DatagramDirection.ClientToServer,
            decryptedBuffer.AsSpan(),
            out KongorPacketHeader readHeader,
            out int decryptedBytes);

        decryptOk.Should().BeTrue();
        decryptedBytes.Should().Be(plaintext.Length);
        readHeader.Should().Be(header);
        decryptedBuffer.AsSpan(0, decryptedBytes).SequenceEqual(plaintext).Should().BeTrue();
    }

    [Fact]
    public void Decrypt_WithSingleBitFlipInPayload_FailsAuthenticationWithoutThrowing()
    {
        byte[] key = DatagramAuthenticator.DeriveSessionKey(TestMasterSecret);
        uint sessionId = 777;

        using DatagramAuthenticator sender = new(key, sessionId);
        using DatagramAuthenticator receiver = new(key, sessionId);

        byte[] plaintext = Encoding.UTF8.GetBytes("MoveToPosition 1500 2400 100");
        KongorPacketHeader header = new(sessionId, packetSequence: 1, subTickDeltaUs: 1000);

        byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + plaintext.Length];
        sender.TryEncryptAndWrap(envelope.AsSpan(), in header, plaintext.AsSpan(), DatagramDirection.ClientToServer, out _);

        // Tamper with payload byte (offset 25)
        envelope[25] ^= 0x01;

        byte[] dest = new byte[128];
        bool decryptOk = receiver.TryUnwrapAndDecrypt(
            envelope.AsSpan(),
            DatagramDirection.ClientToServer,
            dest.AsSpan(),
            out _,
            out int written);

        decryptOk.Should().BeFalse();
        written.Should().Be(0);
    }

    [Fact]
    public void Decrypt_WithSingleBitFlipInHeader_FailsAuthenticationBecauseHeaderIsAad()
    {
        byte[] key = DatagramAuthenticator.DeriveSessionKey(TestMasterSecret);
        uint sessionId = 888;

        using DatagramAuthenticator sender = new(key, sessionId);
        using DatagramAuthenticator receiver = new(key, sessionId);

        byte[] plaintext = Encoding.UTF8.GetBytes("ChatMessage All AttackMid!");
        KongorPacketHeader header = new(sessionId, packetSequence: 1, subTickDeltaUs: 1000);

        byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + plaintext.Length];
        sender.TryEncryptAndWrap(envelope.AsSpan(), in header, plaintext.AsSpan(), DatagramDirection.ClientToServer, out _);

        // Tamper with header byte (offset 9: SubTickDeltaUs)
        envelope[9] ^= 0xFF;

        byte[] dest = new byte[128];
        bool decryptOk = receiver.TryUnwrapAndDecrypt(
            envelope.AsSpan(),
            DatagramDirection.ClientToServer,
            dest.AsSpan(),
            out _,
            out int written);

        decryptOk.Should().BeFalse();
        written.Should().Be(0);
    }

    [Fact]
    public void Decrypt_WithReplayedSequence_IsRejectedByAntiReplay()
    {
        byte[] key = DatagramAuthenticator.DeriveSessionKey(TestMasterSecret);
        uint sessionId = 999;

        using DatagramAuthenticator sender = new(key, sessionId);
        using DatagramAuthenticator receiver = new(key, sessionId);

        byte[] plaintext = Encoding.UTF8.GetBytes("GameCommand_1");
        KongorPacketHeader header = new(sessionId, packetSequence: 10, subTickDeltaUs: 0);

        byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + plaintext.Length];
        sender.TryEncryptAndWrap(envelope.AsSpan(), in header, plaintext.AsSpan(), DatagramDirection.ClientToServer, out _);

        byte[] dest = new byte[128];

        // First delivery: Admitted
        bool firstOk = receiver.TryUnwrapAndDecrypt(
            envelope.AsSpan(),
            DatagramDirection.ClientToServer,
            dest.AsSpan(),
            out _,
            out _);
        firstOk.Should().BeTrue();

        // Replay of same sequence 10: Dropped by Anti-Replay
        bool replayOk = receiver.TryUnwrapAndDecrypt(
            envelope.AsSpan(),
            DatagramDirection.ClientToServer,
            dest.AsSpan(),
            out _,
            out _);
        replayOk.Should().BeFalse();
    }

    [Fact]
    public void Decrypt_WithWrongKey_FailsAuthentication()
    {
        byte[] keyA = DatagramAuthenticator.DeriveSessionKey(Encoding.UTF8.GetBytes("ClientSecretA"));
        byte[] keyB = DatagramAuthenticator.DeriveSessionKey(Encoding.UTF8.GetBytes("ClientSecretB"));
        uint sessionId = 1234;

        using DatagramAuthenticator sender = new(keyA, sessionId);
        using DatagramAuthenticator receiver = new(keyB, sessionId);

        byte[] plaintext = Encoding.UTF8.GetBytes("TopSecretPacketData");
        KongorPacketHeader header = new(sessionId, packetSequence: 1, subTickDeltaUs: 0);

        byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + plaintext.Length];
        sender.TryEncryptAndWrap(envelope.AsSpan(), in header, plaintext.AsSpan(), DatagramDirection.ClientToServer, out _);

        byte[] dest = new byte[128];
        bool decryptOk = receiver.TryUnwrapAndDecrypt(
            envelope.AsSpan(),
            DatagramDirection.ClientToServer,
            dest.AsSpan(),
            out _,
            out _);

        decryptOk.Should().BeFalse();
    }
}
