// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NEXUS.Contracts.Ipc;

/// <summary>
/// Cryptographic HMAC-SHA256 validator, replay attack shield, and permission gate
/// for Named Pipe IPC communication in ProjectKongor.
/// </summary>
public sealed class IpcSecurityValidator
{
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();
    private readonly TimeSpan _toleranceWindow;
    private readonly TimeProvider _timeProvider;

    public TimeSpan ToleranceWindow => _toleranceWindow;
    public TimeProvider TimeProvider => _timeProvider;

    public IpcSecurityValidator(TimeSpan? toleranceWindow = null, TimeProvider? timeProvider = null)
    {
        _toleranceWindow = toleranceWindow ?? TimeSpan.FromSeconds(60);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Computes the HMAC-SHA256 hex digest for a given message using a secret key.
    /// </summary>
    public static string ComputeSignature(string secret, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(message);

        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the HMAC-SHA256 hex digest for a given message using a secret key (alias for ComputeSignature).
    /// </summary>
    public static string ComputeHmacSha256Hex(string secret, string message) => ComputeSignature(secret, message);

    /// <summary>
    /// Verifies two hex strings in constant time using CryptographicOperations.FixedTimeEquals to prevent timing attacks.
    /// </summary>
    public static bool VerifySignatureConstantTime(string expectedHex, string actualHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || string.IsNullOrWhiteSpace(actualHex))
        {
            return false;
        }

        try
        {
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedHex.Trim().ToLowerInvariant());
            byte[] actualBytes = Encoding.UTF8.GetBytes(actualHex.Trim().ToLowerInvariant());
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies an HMAC-SHA256 signature in constant time to prevent timing attacks.
    /// </summary>
    public static bool VerifySignature(string secret, string message, string signature)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        try
        {
            string expectedHex = ComputeSignature(secret, message);
            return VerifySignatureConstantTime(expectedHex, signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Categorizes an IPC opcode as mutating/actuation (high security tier) vs read-only (telemetry tier).
    /// </summary>
    public static bool IsMutatingOpCode(ushort opCode) => opCode switch
    {
        0x0003 => true, // ExecuteCommand
        0x0008 => true, // HotReloadMod
        0x0009 => true, // DispatchUiEvent
        0x000E => true, // EvalLuaSync
        0x0012 => true, // InjectSubTickInput
        0x0014 => true, // UpdateTelemetryMetrics
        0x0021 => true, // DispatchVoiceUiTrigger
        0x0030 => true, // SyncEndpoints
        0x0032 => true, // BringToFront
        _ => false
    };

    public static bool IsMutatingOpCode(IpcOpCode opCode) => IsMutatingOpCode((ushort)opCode);

    /// <summary>
    /// Categorizes an IPC opcode as safe read-only inspection.
    /// </summary>
    public static bool IsReadOnlyOpCode(ushort opCode) => !IsMutatingOpCode(opCode);
    public static bool IsReadOnlyOpCode(IpcOpCode opCode) => !IsMutatingOpCode((ushort)opCode);

    /// <summary>
    /// Creates a signed handshake payload for authenticating a client session.
    /// </summary>
    public static IpcAuthPayload CreateHandshakePayload(string secret, string clientName, TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        long timestamp = tp.GetUtcNow().ToUnixTimeSeconds();
        string nonce = Guid.NewGuid().ToString("N");
        string message = $"AUTH:{clientName}:{timestamp}:{nonce}";
        string signature = ComputeSignature(secret, message);

        return new IpcAuthPayload
        {
            ClientName = clientName,
            Timestamp = timestamp,
            Nonce = nonce,
            Signature = signature
        };
    }

    /// <summary>
    /// Creates a signed authentication envelope header for ad-hoc command signing.
    /// </summary>
    public static IpcAuthHeader CreateCommandHeader(string secret, string command, TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        long timestamp = tp.GetUtcNow().ToUnixTimeSeconds();
        string nonce = Guid.NewGuid().ToString("N");
        string message = $"CMD:{timestamp}:{nonce}:{command}";
        string signature = ComputeSignature(secret, message);

        return new IpcAuthHeader
        {
            Timestamp = timestamp,
            Nonce = nonce,
            Signature = signature
        };
    }

    /// <summary>
    /// Validates an incoming handshake payload: verifies signature freshness, replay prevention, and signature match.
    /// </summary>
    public IpcAuthResult ValidateHandshake(string secret, IpcAuthPayload? payload, IpcSandboxMode mode = IpcSandboxMode.Enforced)
    {
        if (mode == IpcSandboxMode.Disabled)
        {
            return IpcAuthResult.Success();
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return IpcAuthResult.Fail("No server authentication key configured.");
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Signature) || string.IsNullOrWhiteSpace(payload.Nonce))
        {
            return IpcAuthResult.Fail("Missing required handshake signature or nonce.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset msgTime = DateTimeOffset.FromUnixTimeSeconds(payload.Timestamp);
        TimeSpan skew = (now - msgTime).Duration();

        if (skew > _toleranceWindow)
        {
            var fail = IpcAuthResult.Fail($"Handshake timestamp {payload.Timestamp} is expired or too far in the future (skew: {skew.TotalSeconds:F1}s, limit: {_toleranceWindow.TotalSeconds:F1}s).");
            return mode == IpcSandboxMode.Permissive ? IpcAuthResult.Audit($"Permissive audit warning: {fail.ErrorMessage}") : fail;
        }

        CleanupExpiredNonces(now.ToUnixTimeSeconds());

        if (!_seenNonces.TryAdd(payload.Nonce, payload.Timestamp))
        {
            var fail = IpcAuthResult.Fail($"Replay attack detected: Nonce '{payload.Nonce}' was already processed.");
            return mode == IpcSandboxMode.Permissive ? IpcAuthResult.Audit($"Permissive audit warning: {fail.ErrorMessage}") : fail;
        }

        string expectedMessage = $"AUTH:{payload.ClientName}:{payload.Timestamp}:{payload.Nonce}";
        if (!VerifySignature(secret, expectedMessage, payload.Signature))
        {
            var fail = IpcAuthResult.Fail("HMAC-SHA256 signature mismatch.");
            return mode == IpcSandboxMode.Permissive ? IpcAuthResult.Audit($"Permissive audit warning: {fail.ErrorMessage}") : fail;
        }

        return IpcAuthResult.Success();
    }

    public IpcAuthResult ValidateHandshake(IpcAuthPayload? payload, string secret, IpcSandboxMode mode = IpcSandboxMode.Enforced)
        => ValidateHandshake(secret, payload, mode);

    /// <summary>
    /// Validates a per-command signed envelope: verifies signature freshness, replay prevention, and signature match.
    /// </summary>
    public IpcAuthResult ValidateCommand(string secret, string command, IpcAuthHeader? header, IpcSandboxMode mode = IpcSandboxMode.Enforced)
    {
        if (mode == IpcSandboxMode.Disabled)
        {
            return IpcAuthResult.Success();
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return IpcAuthResult.Fail("No server authentication key configured.");
        }

        if (header == null || string.IsNullOrWhiteSpace(header.Signature) || string.IsNullOrWhiteSpace(header.Nonce))
        {
            return IpcAuthResult.Fail("Missing required command authentication header.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset msgTime = DateTimeOffset.FromUnixTimeSeconds(header.Timestamp);
        TimeSpan skew = (now - msgTime).Duration();

        if (skew > _toleranceWindow)
        {
            var fail = IpcAuthResult.Fail($"Command timestamp {header.Timestamp} is expired or too far in the future (skew: {skew.TotalSeconds:F1}s, limit: {_toleranceWindow.TotalSeconds:F1}s).");
            return mode == IpcSandboxMode.Permissive ? IpcAuthResult.Audit($"Permissive audit warning: {fail.ErrorMessage}") : fail;
        }

        CleanupExpiredNonces(now.ToUnixTimeSeconds());

        if (!_seenNonces.TryAdd(header.Nonce, header.Timestamp))
        {
            var fail = IpcAuthResult.Fail($"Replay attack detected: Command nonce '{header.Nonce}' was already processed.");
            return mode == IpcSandboxMode.Permissive ? IpcAuthResult.Audit($"Permissive audit warning: {fail.ErrorMessage}") : fail;
        }

        string expectedMessage = $"CMD:{header.Timestamp}:{header.Nonce}:{command}";
        if (!VerifySignature(secret, expectedMessage, header.Signature))
        {
            var fail = IpcAuthResult.Fail("Command HMAC-SHA256 signature mismatch.");
            return mode == IpcSandboxMode.Permissive ? IpcAuthResult.Audit($"Permissive audit warning: {fail.ErrorMessage}") : fail;
        }

        return IpcAuthResult.Success();
    }

    public IpcAuthResult ValidateCommandEnvelope(IpcAuthHeader? header, string command, string secret, IpcSandboxMode mode = IpcSandboxMode.Enforced)
        => ValidateCommand(secret, command, header, mode);

    private void CleanupExpiredNonces(long currentUnixSeconds)
    {
        long cutoff = currentUnixSeconds - (long)_toleranceWindow.TotalSeconds;
        foreach (var kv in _seenNonces)
        {
            if (kv.Value < cutoff)
            {
                _seenNonces.TryRemove(kv.Key, out _);
            }
        }
    }
}
