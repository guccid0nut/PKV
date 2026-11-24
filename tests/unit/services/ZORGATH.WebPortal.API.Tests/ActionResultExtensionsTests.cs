// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Email;
using NEXUS.Contracts.CQRS.Registration;
using Xunit;
using ZORGATH.WebPortal.API.Extensions;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class ActionResultExtensionsTests
{
    private sealed class TestController : ControllerBase { }

    private readonly TestController _controller = new();

    [Fact]
    public void AdminActionResultDto_Success_ReturnsOk()
    {
        var dto = new AdminActionResultDto(true, "All good");
        var result = dto.ToActionResult(_controller);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(dto);
    }

    [Fact]
    public void AdminActionResultDto_Failure_ReturnsBadRequest()
    {
        var dto = new AdminActionResultDto(false, "Something failed");
        var result = dto.ToActionResult(_controller);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be("Something failed");
    }

    [Fact]
    public void ToActionResultOrNotFound_WithValue_ReturnsOk()
    {
        var obj = new { Name = "Test" };
        var result = obj.ToActionResultOrNotFound(_controller);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(obj);
    }

    [Fact]
    public void ToActionResultOrNotFound_NullWithoutMessage_ReturnsNotFoundResult()
    {
        string? obj = null;
        var result = obj.ToActionResultOrNotFound(_controller);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void ToActionResultOrNotFound_NullWithMessage_ReturnsNotFoundObjectResult()
    {
        string? obj = null;
        var result = obj.ToActionResultOrNotFound(_controller, "Item not found");

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("Item not found");
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, typeof(OkObjectResult))]
    [InlineData(StatusCodes.Status400BadRequest, typeof(BadRequestObjectResult))]
    [InlineData(StatusCodes.Status409Conflict, typeof(ConflictObjectResult))]
    [InlineData(StatusCodes.Status500InternalServerError, typeof(ObjectResult))]
    public void InitiateRegistrationResult_MapsStatusCodesCorrectly(int statusCode, Type expectedType)
    {
        var dto = new InitiateRegistrationResult(statusCode == StatusCodes.Status200OK, statusCode, "Message");
        var result = dto.ToActionResult(_controller);

        result.Should().BeOfType(expectedType);
    }

    [Fact]
    public void RegistrationResult_Created_ReturnsCreatedAtAction()
    {
        var dto = RegistrationResult.Created(42, new { Id = 42 });
        var result = dto.ToActionResult(_controller);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be("GetUser");
        created.ControllerName.Should().Be("User");
    }

    [Fact]
    public void RegistrationResult_CustomAction_ReturnsCustomCreatedAtAction()
    {
        var dto = RegistrationResult.Created(42, new { Id = 42 });
        var result = dto.ToActionResult(_controller, "CustomAction", "CustomController");

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be("CustomAction");
        created.ControllerName.Should().Be("CustomController");
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, typeof(BadRequestObjectResult))]
    [InlineData(StatusCodes.Status404NotFound, typeof(NotFoundObjectResult))]
    [InlineData(StatusCodes.Status409Conflict, typeof(ConflictObjectResult))]
    [InlineData(StatusCodes.Status500InternalServerError, typeof(ObjectResult))]
    public void RegistrationResult_ErrorStatuses_MappedCorrectly(int statusCode, Type expectedType)
    {
        var dto = new RegistrationResult(false, statusCode, "Error message");
        var result = dto.ToActionResult(_controller);

        result.Should().BeOfType(expectedType);
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, typeof(OkObjectResult))]
    [InlineData(StatusCodes.Status400BadRequest, typeof(BadRequestObjectResult))]
    [InlineData(StatusCodes.Status422UnprocessableEntity, typeof(UnprocessableEntityObjectResult))]
    [InlineData(StatusCodes.Status503ServiceUnavailable, typeof(ObjectResult))]
    [InlineData(StatusCodes.Status500InternalServerError, typeof(ObjectResult))]
    public void RegisterEmailAddressResult_MapsStatusCodesCorrectly(int statusCode, Type expectedType)
    {
        var dto = new RegisterEmailAddressResult(statusCode == StatusCodes.Status200OK, statusCode, "Email response");
        var result = dto.ToActionResult(_controller);

        result.Should().BeOfType(expectedType);
    }
}
