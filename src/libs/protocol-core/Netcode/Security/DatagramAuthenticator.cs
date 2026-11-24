// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DatagramAuthenticator.cs
// System: Kongor Core Netcode - ChaCha20-Poly1305 AEAD Datagram Authenticator
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Direction of the datagram transport stream used to prevent cross-direction reflection attacks.
/// </summary>
public enum DatagramDirection : uint
{
    ClientToServer = 1,
    ServerToClient = 2
}

/// <summary>
///     High-performance zero-allocation AEAD datagram authenticator and encryptor.
///     Uses hardware-accelerated ChaCha20-Poly1305 (.NET 9) with deterministic 12-byte nonces
///     and RFC 4303 64-bit sliding bitmask anti-replay validation.
/// </summary>
public sealed class DatagramAuthenticator : IDisposable
{
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    private static readonly byte[] DefaultHkdfInfo = Encoding.UTF8.GetBytes("Kongor-UDP-Transport-Key");

    private readonly ChaCha20Poly1305 _cipher;
    private readonly uint _sessionId;
    private SlidingBitmaskAntiReplay _antiReplay;
    private bool _disposed;

    public uint SessionId => _sessionId;
    public SlidingBitmaskAntiReplay AntiReplay => _antiReplay;

    public DatagramAuthenticator(ReadOnlySpan<byte> key, uint sessionId)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Key must be exactly {KeySizeBytes} bytes.", nameof(key));
        }

        _cipher = new ChaCha20Poly1305(key);
        _sessionId = sessionId;
        _antiReplay = new SlidingBitmaskAntiReplay();
    }

    /// <summary>
    ///     Derives a 32-byte cryptographic key from a master secret (e.g. Master Server session cookie)
    ///     using HKDF-SHA256 expansion.
    /// </summary>
    public static byte[] DeriveSessionKey(ReadOnlySpan<byte> masterSecret, ReadOnlySpan<byte> context = default)
    {
        ReadOnlySpan<byte> info = context.IsEmpty ? DefaultHkdfInfo : context;
        byte[] derivedKey = new byte[KeySizeBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterSecret, derivedKey, salt: default, info);
        return derivedKey;
    }

    /// <summary>
    ///     Builds the deterministic 12-byte nonce for AEAD operations:
    ///     [0..3]  : SessionId (Little Endian uint32)
    ///     [4..7]  : Direction (Little Endian uint32)
    ///     [8..11] : PacketSequence (Little Endian uint32)
    /// </summary>
    public static void ConstructNonce(
        Span<byte> destination,
        uint sessionId,
        DatagramDirection direction,
        uint packetSequence)
    {
        if (destination.Length < NonceSizeBytes)
        {
            throw new ArgumentException($"Destination span must be at least {NonceSizeBytes} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], (uint)direction);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], packetSequence);
    }

    /// <summary>
    ///     Encrypts the payload and wraps it into a complete modern transport envelope.
    ///     The 21-byte header is authenticated as Associated Data (AAD).
    /// </summary>
    public bool TryEncryptAndWrap(
        Span<byte> destination,
        in KongorPacketHeader header,
        ReadOnlySpan<byte> plaintext,
        DatagramDirection direction,
        out int bytesWritten)
    {
        bytesWritten = 0;
        int totalRequired = KongorPacketEnvelope.HeaderSize + plaintext.Length + TagSizeBytes;

        if (destination.Length < totalRequired)
        {
            return false;
        }

        // 1. Write Header to destination
        if (!KongorPacketEnvelope.TryWriteHeader(destination[..KongorPacketEnvelope.HeaderSize], in header))
        {
            return false;
        }

        ReadOnlySpan<byte> headerAad = destination[..KongorPacketEnvelope.HeaderSize];
        Span<byte> ciphertextSlice = destination.Slice(KongorPacketEnvelope.HeaderSize, plaintext.Length);
        Span<byte> tagSlice = destination.Slice(KongorPacketEnvelope.HeaderSize + plaintext.Length, TagSizeBytes);

        // 2. Build 12-byte Nonce
        Span<byte> nonce = stackalloc byte[NonceSizeBytes];
        ConstructNonce(nonce, header.SessionId, direction, header.PacketSequence);

        // 3. Encrypt payload and compute Poly1305 MAC over (AAD + Ciphertext)
        _cipher.Encrypt(nonce, plaintext, ciphertextSlice, tagSlice, headerAad);

        bytesWritten = totalRequired;
        return true;
    }

    /// <summary>
    ///     Authenticates the MAC tag, verifies anti-replay sequence window, and decrypts the payload in-place.
    ///     Returns false immediately if tampering, replay, or corruption is detected.
    /// </summary>
    public bool TryUnwrapAndDecrypt(
        ReadOnlySpan<byte> datagram,
        DatagramDirection expectedDirection,
        Span<byte> destinationPlaintext,
        out KongorPacketHeader header,
        out int bytesWritten,
        bool enforceAntiReplay = true)
    {
        bytesWritten = 0;

        // 1. Structural envelope unwrapping
        if (!KongorPacketEnvelope.TryUnwrap(datagram, out header, out ReadOnlySpan<byte> ciphertext, out ReadOnlySpan<byte> tag))
        {
            return false;
        }

        // 2. Session ID verification (0 is wildcard accepting any matching key)
        if (_sessionId != 0 && header.SessionId != _sessionId)
        {
            return false;
        }

        // 3. RFC 4303 Anti-Replay Check (reject duplicates and stale sequence numbers)
        if (enforceAntiReplay)
        {
            AntiReplayStatus status = _antiReplay.CheckAndAdmit(header.PacketSequence);
            if (status is AntiReplayStatus.Duplicate or AntiReplayStatus.Stale)
            {
                return false;
            }
        }

        if (destinationPlaintext.Length < ciphertext.Length)
        {
            return false;
        }

        // 4. Build 12-byte Nonce
        Span<byte> nonce = stackalloc byte[NonceSizeBytes];
        ConstructNonce(nonce, header.SessionId, expectedDirection, header.PacketSequence);

        // 5. Authenticate and decrypt with ChaCha20-Poly1305
        ReadOnlySpan<byte> headerAad = datagram[..KongorPacketEnvelope.HeaderSize];
        Span<byte> targetPlaintext = destinationPlaintext[..ciphertext.Length];

        try
        {
            _cipher.Decrypt(nonce, ciphertext, tag, targetPlaintext, headerAad);
            bytesWritten = ciphertext.Length;
            return true;
        }
        catch (CryptographicException)
        {
            // MAC verification failed: corrupted or tampered datagram
            bytesWritten = 0;
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cipher.Dispose();
            _disposed = true;
        }
    }
}
