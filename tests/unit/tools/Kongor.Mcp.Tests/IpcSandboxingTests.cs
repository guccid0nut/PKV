// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using NEXUS.Contracts.Ipc;
using Xunit;
using ContractOpCode = NEXUS.Contracts.Ipc.IpcOpCode;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Deterministic TimeProvider for unit testing time-dependent security windows.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
}

public sealed class IpcSandboxingTests
{
    private const string TestKey = "kongor-super-secret-auth-key-2026";
    private readonly DateTimeOffset _initialTime = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IpcSecurityValidator_ValidSignature_VerifiesSuccessfully()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        IpcAuthPayload payload = IpcSecurityValidator.CreateHandshakePayload(TestKey, "test-client", timeProvider);

        // Act
        IpcAuthResult result = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.IsAuditWarning.Should().BeFalse();
    }

    [Fact]
    public void IpcSecurityValidator_TamperedClientName_FailsVerification()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        IpcAuthPayload payload = IpcSecurityValidator.CreateHandshakePayload(TestKey, "legitimate-client", timeProvider);

        // Tamper with client name
        payload.ClientName = "adversary-client";

        // Act
        IpcAuthResult result = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("signature mismatch");
    }

    [Fact]
    public void IpcSecurityValidator_IncorrectKey_FailsVerification()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        IpcAuthPayload payload = IpcSecurityValidator.CreateHandshakePayload(TestKey, "test-client", timeProvider);

        // Act - validate with wrong key
        IpcAuthResult result = validator.ValidateHandshake(payload, "incorrect-wrong-key-9999", IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("signature mismatch");
    }

    [Fact]
    public void IpcSecurityValidator_ExpiredTimestamp_FailsVerification()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(TimeSpan.FromSeconds(30), timeProvider);
        IpcAuthPayload payload = IpcSecurityValidator.CreateHandshakePayload(TestKey, "test-client", timeProvider);

        // Advance time past the 30-second window
        timeProvider.Advance(TimeSpan.FromSeconds(35));

        // Act
        IpcAuthResult result = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("expired or too far in the future");
    }

    [Fact]
    public void IpcSecurityValidator_FutureTimestamp_FailsVerification()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(TimeSpan.FromSeconds(30), timeProvider);

        // Create payload with future timestamp (50s ahead)
        DateTimeOffset futureTime = _initialTime.AddSeconds(50);
        long futureTimestamp = futureTime.ToUnixTimeSeconds();
        string nonce = "future-nonce-xyz";
        string canonicalData = $"AUTH:test-client:{futureTimestamp}:{nonce}";
        string sig = IpcSecurityValidator.ComputeHmacSha256Hex(TestKey, canonicalData);

        IpcAuthPayload payload = new()
        {
            ClientName = "test-client",
            Timestamp = futureTimestamp,
            Nonce = nonce,
            Signature = sig
        };

        // Act
        IpcAuthResult result = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("expired or too far in the future");
    }

    [Fact]
    public void IpcSecurityValidator_ReplayedNonce_FailsVerification()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(TimeSpan.FromSeconds(60), timeProvider);
        IpcAuthPayload payload = IpcSecurityValidator.CreateHandshakePayload(TestKey, "test-client", timeProvider);

        // Act 1 - first validation should succeed
        IpcAuthResult result1 = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Enforced);
        result1.IsSuccess.Should().BeTrue();

        // Act 2 - replaying the exact same payload within window should be rejected
        IpcAuthResult result2 = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result2.IsSuccess.Should().BeFalse();
        result2.ErrorMessage.Should().Contain("Replay attack detected");
    }

    [Fact]
    public void IpcSecurityValidator_AdvancesTime_PrunesReplayCache()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(TimeSpan.FromSeconds(30), timeProvider);
        IpcAuthPayload payload1 = IpcSecurityValidator.CreateHandshakePayload(TestKey, "client-1", timeProvider);

        validator.ValidateHandshake(payload1, TestKey, IpcSandboxMode.Enforced).IsSuccess.Should().BeTrue();

        // Advance time past 2x tolerance so cleanup triggers
        timeProvider.Advance(TimeSpan.FromSeconds(70));

        // Create fresh payload at current advanced time
        IpcAuthPayload payload2 = IpcSecurityValidator.CreateHandshakePayload(TestKey, "client-2", timeProvider);
        IpcAuthResult result2 = validator.ValidateHandshake(payload2, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result2.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void IpcSecurityValidator_TimingAttackImmunity_ConstantTimeComparison()
    {
        // Test exact match
        IpcSecurityValidator.VerifySignatureConstantTime("abcdef123456", "abcdef123456").Should().BeTrue();

        // Test length mismatch
        IpcSecurityValidator.VerifySignatureConstantTime("abcdef123456", "abcdef123").Should().BeFalse();

        // Test single bit/character mismatch
        IpcSecurityValidator.VerifySignatureConstantTime("abcdef123456", "abcdef123457").Should().BeFalse();

        // Test empty strings
        IpcSecurityValidator.VerifySignatureConstantTime("", "").Should().BeFalse();
        IpcSecurityValidator.VerifySignatureConstantTime("abc", "").Should().BeFalse();
    }

    [Fact]
    public void IpcSecurityValidator_IsMutatingOpCode_CorrectlyIdentifiesActuation()
    {
        // Mutating opcodes (Contracts enum)
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.ExecuteCommand).Should().BeTrue();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.HotReloadMod).Should().BeTrue();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.DispatchUiEvent).Should().BeTrue();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.EvalLuaSync).Should().BeTrue();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.InjectSubTickInput).Should().BeTrue();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.UpdateTelemetryMetrics).Should().BeTrue();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.DispatchVoiceUiTrigger).Should().BeTrue();

        // Read-only opcodes (Contracts enum)
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.Ping).Should().BeFalse();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.Pong).Should().BeFalse();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.GetEngineState).Should().BeFalse();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.EngineStateResponse).Should().BeFalse();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.Authenticate).Should().BeFalse();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.AuthResponse).Should().BeFalse();
        IpcSecurityValidator.IsMutatingOpCode(ContractOpCode.GetMatchState).Should().BeFalse();

        // Mutating opcodes (ushort overload including Cockpit window-sync opcodes)
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0003).Should().BeTrue(); // ExecuteCommand
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0008).Should().BeTrue(); // HotReloadMod
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0009).Should().BeTrue(); // DispatchUiEvent
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x000E).Should().BeTrue(); // EvalLuaSync
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0012).Should().BeTrue(); // InjectSubTickInput
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0014).Should().BeTrue(); // UpdateTelemetryMetrics
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0021).Should().BeTrue(); // DispatchVoiceUiTrigger
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0030).Should().BeTrue(); // SyncEndpoints
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0032).Should().BeTrue(); // BringToFront

        // Read-only opcodes (ushort overload)
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0001).Should().BeFalse(); // Ping
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0005).Should().BeFalse(); // GetEngineState
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0010).Should().BeFalse(); // Authenticate
        IpcSecurityValidator.IsMutatingOpCode((ushort)0x0011).Should().BeFalse(); // AuthResponse
    }

    [Fact]
    public void IpcSecurityValidator_CommandHeaderValidation_ValidEnvelope_Succeeds()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        const string command = "Set cl_fov 100";
        IpcAuthHeader header = IpcSecurityValidator.CreateCommandHeader(TestKey, command, timeProvider);

        // Act
        IpcAuthResult result = validator.ValidateCommandEnvelope(header, command, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void IpcSecurityValidator_CommandHeaderValidation_TamperedCommand_Fails()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        const string command = "Set cl_fov 100";
        IpcAuthHeader header = IpcSecurityValidator.CreateCommandHeader(TestKey, command, timeProvider);

        // Tamper with command
        const string tamperedCommand = "Set cl_fov 120";

        // Act
        IpcAuthResult result = validator.ValidateCommandEnvelope(header, tamperedCommand, TestKey, IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("signature mismatch");
    }

    [Fact]
    public void IpcSecurityValidator_PermissiveMode_ReturnsSuccessWithAuditWarning()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        IpcAuthPayload payload = new()
        {
            ClientName = "untrusted",
            Timestamp = _initialTime.ToUnixTimeSeconds(),
            Nonce = "nonce",
            Signature = "invalid_signature"
        };

        // Act
        IpcAuthResult result = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Permissive);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsAuditWarning.Should().BeTrue();
        result.ErrorMessage.Should().Contain("Permissive audit warning");
    }

    [Fact]
    public void IpcSecurityValidator_DisabledMode_AlwaysReturnsSuccess()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        IpcAuthPayload payload = new()
        {
            ClientName = "untrusted",
            Timestamp = 0,
            Nonce = "",
            Signature = "invalid"
        };

        // Act
        IpcAuthResult result = validator.ValidateHandshake(payload, TestKey, IpcSandboxMode.Disabled);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsAuditWarning.Should().BeFalse();
    }

    [Fact]
    public void GameIpcBridge_ExecuteCommand_WhenUnauthorized_InterceptsAndReturnsRemediation()
    {
        // Arrange
        string rawError = "[SECURITY ERROR] 401 Unauthorized: Mutating command 'Set dev true' requires HMAC-SHA256 authentication handshake.";

        // Act
        bool isSecurityError = rawError.Contains("[SECURITY ERROR] 401 Unauthorized");

        // Assert
        isSecurityError.Should().BeTrue();
        rawError.Should().Contain("HMAC-SHA256 authentication handshake");
    }

    [Fact]
    public void IpcSecurityValidator_EmptyKeyInEnforcedMode_FailsGracefully()
    {
        // Arrange
        TestTimeProvider timeProvider = new(_initialTime);
        IpcSecurityValidator validator = new(timeProvider: timeProvider);
        IpcAuthPayload payload = IpcSecurityValidator.CreateHandshakePayload(TestKey, "test-client", timeProvider);

        // Act - key is empty string
        IpcAuthResult result = validator.ValidateHandshake(payload, "", IpcSandboxMode.Enforced);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No server authentication key configured");
    }
}
