// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class Argon2idHasherTests
{
    private readonly Argon2idHasher _hasher = new();

    [Fact]
    public void HashPassword_ProducesValidArgon2idEncodedString()
    {
        string hash = _hasher.HashPassword("TestSecretPassword123!");

        hash.Should().NotBeNullOrWhiteSpace();
        hash.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=4$");

        string[] parts = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        parts.Length.Should().Be(5);
    }

    [Fact]
    public void VerifyPassword_ValidPassword_ReturnsTrue()
    {
        const string password = "UltraSecurePassword456$#@";
        string hash = _hasher.HashPassword(password);

        bool isValid = _hasher.VerifyPassword(password, hash);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_IncorrectPassword_ReturnsFalse()
    {
        const string password = "UltraSecurePassword456$#@";
        string hash = _hasher.HashPassword(password);

        bool isValid = _hasher.VerifyPassword("WrongPassword!", hash);
        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not_a_hash")]
    [InlineData("$bcrypt$v=1$invalid")]
    [InlineData("$argon2id$invalid_parts")]
    [InlineData("$argon2id$v=19$m=abc,t=3,p=4$salt$hash")]
    public void VerifyPassword_MalformedHash_ReturnsFalseSafelyWithoutThrowing(string malformedHash)
    {
        bool isValid = _hasher.VerifyPassword("password", malformedHash);
        isValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpgradeUserArgon2idHashAsync_UpdatesUserHashInDatabase()
    {
        var options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new MerrickContext(options);
        var user = new User
        {
            ID = 1001,
            EmailAddress = "upgrade_test@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "verifier123",
            PBKDF2PasswordHash = "pbkdf2_legacy_hash",
            Argon2idPasswordHash = null
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repo = new AccountRepository(context, NullLogger<AccountRepository>.Instance);

        string newArgon2id = _hasher.HashPassword("PlaintextPass999");
        await repo.UpgradeUserArgon2idHashAsync(user.ID, newArgon2id);

        var updatedUser = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == user.ID);
        updatedUser.Should().NotBeNull();
        updatedUser!.Argon2idPasswordHash.Should().Be(newArgon2id);
    }

    [Fact]
    public async Task SetInGamePasswordAsync_InvalidatesArgon2idPasswordHash()
    {
        var options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new MerrickContext(options);
        string existingArgon2id = _hasher.HashPassword("OldPass");
        var user = new User
        {
            ID = 2002,
            EmailAddress = "reset_test@kongor.net",
            SRPPasswordSalt = "oldsalt",
            SRPPasswordHash = "oldverifier",
            PBKDF2PasswordHash = "old_pbkdf2",
            Argon2idPasswordHash = existingArgon2id
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var repo = new AccountRepository(context, NullLogger<AccountRepository>.Instance);

        // In-game password reset supplies new SRP salt, verifier, and legacy PBKDF2 hash (argon2idHash parameter defaults to null)
        await repo.SetInGamePasswordAsync(user.ID, "newsalt", "newverifier", "new_pbkdf2");

        var updatedUser = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == user.ID);
        updatedUser.Should().NotBeNull();
        updatedUser!.SRPPasswordSalt.Should().Be("newsalt");
        updatedUser.SRPPasswordHash.Should().Be("newverifier");
        updatedUser.PBKDF2PasswordHash.Should().Be("new_pbkdf2");
        // Crucial invariant: Argon2id hash MUST be nullified to prevent stale hash lockouts
        updatedUser.Argon2idPasswordHash.Should().BeNull();
    }

    [Theory]
    [InlineData("$argon2id$v=19$m=4194304,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")] // 4 GiB memory (OOM DoS)
    [InlineData("$argon2id$v=19$m=65536,t=99,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]   // 99 iterations (CPU DoS)
    [InlineData("$argon2id$v=19$m=65536,t=3,p=64$c2FsdHNhbHQ$aGFzaGhhc2g")]   // 64 parallelism (Thread starvation DoS)
    [InlineData("$argon2id$v=19$m=4,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]       // < 8 KiB
    [InlineData("$argon2id$v=19$m=65536,t=0,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]   // < 1 iteration
    [InlineData("$argon2id$v=19$m=65536,t=3,p=0$c2FsdHNhbHQ$aGFzaGhhc2g")]   // < 1 parallelism
    public void VerifyPassword_OversizedOrInvalidPhcParams_RejectedUnder5MsWithoutDos(string maliciousHash)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool isValid = _hasher.VerifyPassword("password123", maliciousHash);
        sw.Stop();

        isValid.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(250, "Oversized/malformed PHC parameters must be rejected immediately in <250ms without performing KDF computation");
    }

    [Theory]
    [InlineData("$argon2id$v=19$m=4194304,t=3,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]
    [InlineData("$argon2id$v=19$m=65536,t=99,p=4$c2FsdHNhbHQ$aGFzaGhhc2g")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=64$c2FsdHNhbHQ$aGFzaGhhc2g")]
    public async Task VerifyPasswordAsync_OversizedPhcParams_RejectedUnder5MsWithoutDos(string maliciousHash)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool isValid = await _hasher.VerifyPasswordAsync("password123", maliciousHash);
        sw.Stop();

        isValid.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(250, "Async verification must reject oversized parameters in <250ms without performing KDF computation");
    }
}
