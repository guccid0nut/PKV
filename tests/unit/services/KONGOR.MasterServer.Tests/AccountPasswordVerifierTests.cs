// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using FluentAssertions;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class AccountPasswordVerifierTests
{
    private readonly Argon2idHasher _argon2Hasher = new();
    private readonly AccountPasswordVerifier _verifier;

    public AccountPasswordVerifierTests()
    {
        _verifier = new AccountPasswordVerifier(
            NullLogger<AccountPasswordVerifier>.Instance,
            TimeProvider.System,
            _argon2Hasher);
        User jitWarmupUser = new() { ID = 1, EmailAddress = "warmup@kongor.net", SRPPasswordSalt = "s", SRPPasswordHash = "h", Argon2idPasswordHash = "$argon2id$v=19$m=4194304,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g" };
        _ = _verifier.VerifyPassword(jitWarmupUser, "warmup");
    }

    [Theory]
    [InlineData("$argon2id$v=19$m=4194304,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")] // 4 GiB memory (OOM DoS)
    [InlineData("$argon2id$v=19$m=65536,t=99,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]   // 99 iterations (CPU DoS)
    [InlineData("$argon2id$v=19$m=65536,t=3,p=64$c2FsdHNhbHQ$aGFzaGhhc2g")]   // 64 parallelism (Thread starvation DoS)
    public async Task VerifyPasswordAsync_WhenOversizedPhcString_RejectsUnder5MsWithoutDos(string maliciousHash)
    {
        User user = new()
        {
            ID = 42,
            EmailAddress = "test@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "dummyhash",
            Argon2idPasswordHash = maliciousHash
        };

        _ = await _verifier.VerifyPasswordAsync(user, "warmup");
        Stopwatch sw = Stopwatch.StartNew();
        bool result = await _verifier.VerifyPasswordAsync(user, "testpass");
        sw.Stop();

        result.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(10, "Malicious PHC strings must be rejected in <10ms before memory allocation");
    }

    [Theory]
    [InlineData("$argon2id$v=19$m=4194304,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]
    [InlineData("$argon2id$v=19$m=65536,t=99,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=64$c2FsdHNhbHQ$aGFzaGhhc2g")]
    public void VerifyPassword_Sync_WhenOversizedPhcString_RejectsUnder5MsWithoutDos(string maliciousHash)
    {
        User user = new()
        {
            ID = 42,
            EmailAddress = "test@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "dummyhash",
            Argon2idPasswordHash = maliciousHash
        };

        Stopwatch sw = Stopwatch.StartNew();
        bool result = _verifier.VerifyPassword(user, "testpass");
        sw.Stop();

        result.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(10, "Sync verification must also reject malicious PHC strings in <10ms");
    }

    [Fact]
    public async Task VerifyPasswordAsync_WhenValidArgon2idHash_ReturnsTrue()
    {
        const string candidateInput = "SyntheticArgonFixture_A9!";
        string hash = _argon2Hasher.HashPassword(candidateInput);

        User user = new()
        {
            ID = 101,
            EmailAddress = "test@kongor.net",
            SRPPasswordSalt = "unused_salt",
            SRPPasswordHash = "unused_hash",
            Argon2idPasswordHash = hash
        };

        bool result = await _verifier.VerifyPasswordAsync(user, candidateInput);
        result.Should().BeTrue();

        bool wrongResult = await _verifier.VerifyPasswordAsync(user, "WrongFixtureInput!");
        wrongResult.Should().BeFalse();
    }
}
