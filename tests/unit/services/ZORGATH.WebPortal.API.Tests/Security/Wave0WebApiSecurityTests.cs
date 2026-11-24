// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Threading.Tasks;
using ASPIRE.Common.DTOs;
using FluentAssertions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using ZORGATH.WebPortal.API.Services;
using ZORGATH.WebPortal.API.Services.Email;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Tests.Security;

public sealed class Wave0WebApiSecurityTests
{
    // protects: Registration error responses against echoing user plaintext passwords into HTTP bodies, browser caches, or error logs.
    // fails_when: UserService.RegisterUserAsync includes Password or ConfirmPassword values in ServiceResult.ErrorMessage.
    // seam: Real UserService.RegisterUserAsync with mismatched passwords.
    [Fact]
    public async Task RegisterUserAsync_PasswordMismatch_NeverEchoesPlaintextPasswords()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEmailService> emailService = new();
        PasswordValidator validator = new(Options.Create(new IdentityOptions()));
        UserService userService = new(db, emailService.Object, validator, NullLogger<UserService>.Instance);

        const string secretPwA = "SuperSecretCandidateAlpha!99"; // kongor-security-ignore: synthetic unit-test dummy secret
        const string secretPwB = "SuperSecretCandidateBravo!88"; // kongor-security-ignore: synthetic unit-test dummy secret

        RegisterUserAndMainAccountDTO dto = new(
            "00000000-0000-0000-0000-000000000001",
            "TestPlayer",
            secretPwA,
            secretPwB);

        var result = await userService.RegisterUserAsync(dto);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Password and confirmation password do not match.");
        result.ErrorMessage.Should().NotContain(secretPwA);
        result.ErrorMessage.Should().NotContain(secretPwB);
    }
}
