// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class UserClaimsExtensionsTests

{
    [Fact]
    public void GetAccountID_ValidClaim_ReturnsGuid()

    {
        Guid guid = Guid.NewGuid();

        List<Claim> claims = [new(Claims.AccountID, guid.ToString())];

        Assert.Equal(guid, claims.GetAccountID());
    }


    [Fact]
    public void GetAccountIsMain_ValidClaim_ReturnsBool()

    {
        List<Claim> claims = [new(Claims.AccountIsMain, "true")];

        Assert.True(claims.GetAccountIsMain());


        claims = [new Claim(Claims.AccountIsMain, "false")];

        Assert.False(claims.GetAccountIsMain());
    }


    [Fact]
    public void GetAccountName_WithSubjectClaim_ReturnsName()

    {
        List<Claim> claims = [new(Claims.Subject, "TestName")];

        Assert.Equal("TestName", claims.GetAccountName());
    }


    [Fact]
    public void GetAccountName_WithNameIdentifierClaim_ReturnsName()

    {
        List<Claim> claims = [new(Claims.NameIdentifier, "TestName")];

        Assert.Equal("TestName", claims.GetAccountName());
    }


    [Fact]
    public void GetAudience_ValidClaim_ReturnsString()

    {
        List<Claim> claims = [new(Claims.Audience, "TestAudience")];

        Assert.Equal("TestAudience", claims.GetAudience());
    }


    [Fact]
    public void GetAuthenticatedAtTime_ValidClaim_ReturnsDateTimeOffset()

    {
        const long epoch = 1672531200; // 2023-01-01

        DateTimeOffset expected = DateTimeOffset.FromUnixTimeSeconds(epoch);

        List<Claim> claims = [new(Claims.AuthenticatedAtTime, epoch.ToString())];

        Assert.Equal(expected, claims.GetAuthenticatedAtTime());
    }


    [Fact]
    public void GetClanName_ValidClaim_ReturnsString()

    {
        List<Claim> claims = [new(Claims.ClanName, "TestClan")];

        Assert.Equal("TestClan", claims.GetClanName());
    }


    [Fact]
    public void GetClanTag_ValidClaim_ReturnsString()

    {
        List<Claim> claims = [new(Claims.ClanTag, "TC")];

        Assert.Equal("TC", claims.GetClanTag());
    }


    [Fact]
    public void GetExpiresAtTime_ValidClaim_ReturnsDateTimeOffset()

    {
        const long epoch = 1672531200;

        DateTimeOffset expected = DateTimeOffset.FromUnixTimeSeconds(epoch);

        List<Claim> claims = [new(Claims.ExpiresAtTime, epoch.ToString())];

        Assert.Equal(expected, claims.GetExpiresAtTime());
    }


    [Fact]
    public void GetIssuedAtTime_ValidClaim_ReturnsDateTimeOffset()

    {
        const long epoch = 1672531200;

        DateTimeOffset expected = DateTimeOffset.FromUnixTimeSeconds(epoch);

        List<Claim> claims = [new(Claims.IssuedAtTime, epoch.ToString())];

        Assert.Equal(expected, claims.GetIssuedAtTime());
    }


    [Fact]
    public void GetIssuer_ValidClaim_ReturnsString()

    {
        List<Claim> claims = [new(Claims.Issuer, "TestIssuer")];

        Assert.Equal("TestIssuer", claims.GetIssuer());
    }


    [Fact]
    public void GetJWTIdentifier_ValidClaim_ReturnsGuid()

    {
        Guid guid = Guid.NewGuid();

        List<Claim> claims = [new(Claims.JWTIdentifier, guid.ToString())];

        Assert.Equal(guid, claims.GetJWTIdentifier());
    }


    [Fact]
    public void GetNonce_ValidClaim_ReturnsGuid()

    {
        Guid guid = Guid.NewGuid();

        List<Claim> claims = [new(Claims.Nonce, guid.ToString())];

        Assert.Equal(guid, claims.GetNonce());
    }


    [Fact]
    public void GetUserID_ValidClaim_ReturnsGuid()

    {
        Guid guid = Guid.NewGuid();

        List<Claim> claims = [new(Claims.UserID, guid.ToString())];

        Assert.Equal(guid, claims.GetUserID());
    }


    [Fact]
    public void GetUserEmailAddress_WithEmailClaim_ReturnsEmail()

    {
        List<Claim> claims = [new(Claims.Email, "test@kongor.com")];

        Assert.Equal("test@kongor.com", claims.GetUserEmailAddress());
    }


    [Fact]
    public void GetUserEmailAddress_WithEmailAddressClaim_ReturnsEmail()

    {
        List<Claim> claims = [new(Claims.EmailAddress, "test@kongor.com")];

        Assert.Equal("test@kongor.com", claims.GetUserEmailAddress());
    }


    [Fact]
    public void GetUserRole_ValidClaim_ReturnsString()

    {
        List<Claim> claims = [new(Claims.UserRole, "Admin")];

        Assert.Equal("Admin", claims.GetUserRole());
    }
}
