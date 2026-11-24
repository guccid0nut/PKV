// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Security;
using Xunit;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class ZorgathStandaloneConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(SecurityConstants.DefaultInternalServiceKey)]
    public void ValidateInternalServiceKey_InDevelopmentMode_DoesNotThrow(string? key)
    {
        Action act = () => ZORGATH.ValidateInternalServiceKey(key, isDevelopment: true, isStandalone: false);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(SecurityConstants.DefaultInternalServiceKey)]
    public void ValidateInternalServiceKey_InNonDevelopmentMode_ThrowsSecurityException(string? key)
    {
        Action act = () => ZORGATH.ValidateInternalServiceKey(key, isDevelopment: false, isStandalone: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FATAL SECURITY MISCONFIGURATION*");
    }

    [Fact]
    public void ValidateInternalServiceKey_InNonDevelopmentMode_WithCustomKey_Passes()
    {
        Action act = () => ZORGATH.ValidateInternalServiceKey("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", isDevelopment: false, isStandalone: false);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(SecurityConstants.DefaultInternalServiceKey)]
    public void ValidateInternalServiceKey_InStandaloneMode_ThrowsActionableStandaloneException(string? key)
    {
        Action act = () => ZORGATH.ValidateInternalServiceKey(key, isDevelopment: true, isStandalone: true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*[Standalone]*InternalServiceKey*");
    }

    [Fact]
    public void ValidateInternalServiceKey_InStandaloneMode_WithCustomKey_Passes()
    {
        Action act = () => ZORGATH.ValidateInternalServiceKey("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", isDevelopment: true, isStandalone: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveJwtSigningKey_WithOperationalConfigurationSection_ResolvesCorrectly()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperationalConfiguration:JWT:SigningKey"] = "my-custom-operational-jwt-signing-key-32bytes!!"
            })
            .Build();

        string resolved = ZORGATH.ResolveJwtSigningKey(config);
        resolved.Should().Be("my-custom-operational-jwt-signing-key-32bytes!!");
    }

    [Fact]
    public void ResolveJwtSigningKey_WithOperationalSection_ResolvesCorrectly()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operational:JWT:SigningKey"] = "my-custom-operational-jwt-signing-key-32bytes!!"
            })
            .Build();

        string resolved = ZORGATH.ResolveJwtSigningKey(config);
        resolved.Should().Be("my-custom-operational-jwt-signing-key-32bytes!!");
    }

    [Fact]
    public void ResolveJwtSigningKey_WhenManagedByAspire_FallsBackToJwtSigningKey()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operational:JWT:SigningKey"] = "MANAGED_BY_ASPIRE",
                ["jwt-signing-key"] = "my-fallback-jwt-signing-key-32byteslong!!"
            })
            .Build();

        string resolved = ZORGATH.ResolveJwtSigningKey(config);
        resolved.Should().Be("my-fallback-jwt-signing-key-32byteslong!!");
    }

    [Fact]
    public void ResolveJwtSigningKey_WhenPlaceholderDots_FallsBackToParametersJwtSigningKey()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operational:JWT:SigningKey"] = "...",
                ["Parameters:jwt-signing-key"] = "my-parameters-jwt-signing-key-32byteslong!"
            })
            .Build();

        string resolved = ZORGATH.ResolveJwtSigningKey(config);
        resolved.Should().Be("my-parameters-jwt-signing-key-32byteslong!");
    }

    [Fact]
    public void ValidateJwtSigningKey_WhenNotStandalone_DoesNotThrowForDevPhrase()
    {
        Action act = () => ZORGATH.ValidateJwtSigningKey("NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!", isStandalone: false);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MANAGED_BY_ASPIRE")]
    [InlineData("...")]
    [InlineData("NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!")]
    [InlineData("short-key-under-32-bytes!")]
    public void ValidateJwtSigningKey_WhenNonDevelopmentAndNotStandalone_ThrowsFatalSecurityMisconfiguration(string? invalidKey)
    {
        // protects: Public non-standalone Production deployment against hardcoded dev JWT signing key fallback
        // fails_when: ValidateJwtSigningKey only checks isStandalone and ignores !isDevelopment
        // seam: ZORGATH.ValidateJwtSigningKey
        Action act = () => ZORGATH.ValidateJwtSigningKey(invalidKey, isStandalone: false, isDevelopment: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FATAL SECURITY MISCONFIGURATION*JWT signing key*");
    }

    [Fact]
    public void ValidateJwtSigningKey_WhenNonDevelopmentAndNotStandalone_WithValidCustomKey_Passes()
    {
        Action act = () => ZORGATH.ValidateJwtSigningKey(
            "super-secret-production-public-cluster-jwt-key-32bytes!",
            isStandalone: false,
            isDevelopment: false);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MANAGED_BY_ASPIRE")]
    [InlineData("...")]
    [InlineData("NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!")]
    [InlineData("short-key-under-32-bytes!")]
    public void ValidateJwtSigningKey_WhenStandalone_ThrowsActionableStandaloneException(string? invalidKey)
    {
        Action act = () => ZORGATH.ValidateJwtSigningKey(invalidKey, isStandalone: true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*[Standalone]*JWT*signing key*");
    }

    [Fact]
    public void ValidateJwtSigningKey_WhenStandalone_WithValidSecret_Passes()
    {
        Action act = () => ZORGATH.ValidateJwtSigningKey("super-secret-production-standalone-jwt-key-32bytes!", isStandalone: true);
        act.Should().NotThrow();
    }
}
