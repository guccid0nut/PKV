// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.Ipc;

/// <summary>
/// Sandboxing enforcement modes for Named Pipe IPC communication.
/// </summary>
public enum IpcSandboxMode
{
    /// <summary>
    /// Sandboxing is disabled; all commands execute without cryptographic authentication checks.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Audit mode: Unauthenticated mutating commands are logged with a security warning but permitted.
    /// Ideal for development environments and backward compatibility.
    /// </summary>
    Permissive = 1,

    /// <summary>
    /// Strict enforcement: Any mutating/actuation command without valid HMAC-SHA256 authentication is rejected.
    /// Read-only telemetry commands remain accessible.
    /// </summary>
    Enforced = 2
}

/// <summary>
/// Data payload exchanged during the IpcOpCode.Authenticate challenge-response handshake.
/// </summary>
[MemoryPackable]
public sealed partial class IpcAuthPayload
{
    public string ClientName { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// Embedded authentication envelope header for ad-hoc signed mutating commands.
/// </summary>
[MemoryPackable]
public sealed partial class IpcAuthHeader
{
    public long Timestamp { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// Structured response returned for IpcOpCode.AuthResponse.
/// </summary>
[MemoryPackable]
public sealed partial class IpcAuthResponseDto
{
    public bool Authenticated { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SessionId { get; set; }
}

/// <summary>
/// Outcome of validating an IPC message or authentication handshake.
/// </summary>
public sealed record IpcAuthResult(bool IsSuccess, string? ErrorMessage = null, bool IsAuditWarning = false)
{
    public static IpcAuthResult Success() => new(true);
    public static IpcAuthResult Fail(string error) => new(false, error);
    public static IpcAuthResult Audit(string warning) => new(true, warning, IsAuditWarning: true);
}
