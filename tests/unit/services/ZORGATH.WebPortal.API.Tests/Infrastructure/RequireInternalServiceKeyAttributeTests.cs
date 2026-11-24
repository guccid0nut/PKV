// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Security;
using Xunit;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Tests.Infrastructure;

public sealed class RequireInternalServiceKeyAttributeTests
{
    private static ActionExecutingContext CreateActionExecutingContext(
        IHeaderDictionary headers,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(sp => sp.GetService(typeof(IHostEnvironment))).Returns(environment);
        serviceProvider.Setup(sp => sp.GetService(typeof(IConfiguration))).Returns(configuration);

        DefaultHttpContext httpContext = new()
        {
            RequestServices = serviceProvider.Object
        };

        foreach (KeyValuePair<string, StringValues> header in headers)
            httpContext.Request.Headers[header.Key] = header.Value;

        ActionContext actionContext = new(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            new object());
    }

    [Fact]
    public async Task OnActionExecutionAsync_ProductionMissingKey_ReturnsUnauthorized401()
    {
        // Arrange
        Mock<IHostEnvironment> envMock = new();
        envMock.Setup(e => e.EnvironmentName).Returns(Environments.Production);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "InternalServiceKey", "RealProductionSecretKey123456789" }
            })
            .Build();

        HeaderDictionary headers = [];

        ActionExecutingContext context = CreateActionExecutingContext(headers, envMock.Object, config);
        bool nextCalled = false;

        Task<ActionExecutedContext> next()
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        }

        RequireInternalServiceKeyAttribute attribute = new();

        // Act
        await attribute.OnActionExecutionAsync(context, next);

        // Assert
        nextCalled.Should().BeFalse("Pipeline execution should be blocked on missing key in Production");
        context.Result.Should().BeOfType<UnauthorizedObjectResult>();
        UnauthorizedObjectResult result = (UnauthorizedObjectResult)context.Result!;
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ProductionInvalidKey_ReturnsUnauthorized401()
    {
        // Arrange
        Mock<IHostEnvironment> envMock = new();
        envMock.Setup(e => e.EnvironmentName).Returns(Environments.Production);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "InternalServiceKey", "CorrectProductionKey999" }
            })
            .Build();

        HeaderDictionary headers = new()
        {
            { SecurityConstants.InternalServiceKeyHeader, "AttackerForgedKey123" }
        };

        ActionExecutingContext context = CreateActionExecutingContext(headers, envMock.Object, config);
        bool nextCalled = false;

        Task<ActionExecutedContext> next()
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        }

        RequireInternalServiceKeyAttribute attribute = new();

        // Act
        await attribute.OnActionExecutionAsync(context, next);

        // Assert
        nextCalled.Should().BeFalse();
        context.Result.Should().BeOfType<UnauthorizedObjectResult>();
        UnauthorizedObjectResult result = (UnauthorizedObjectResult)context.Result!;
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ProductionValidKey_PassesExecutionToNext()
    {
        // Arrange
        Mock<IHostEnvironment> envMock = new();
        envMock.Setup(e => e.EnvironmentName).Returns(Environments.Production);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "InternalServiceKey", "ExactProductionKeySecret123" }
            })
            .Build();

        HeaderDictionary headers = new()
        {
            { SecurityConstants.InternalServiceKeyHeader, "ExactProductionKeySecret123" }
        };

        ActionExecutingContext context = CreateActionExecutingContext(headers, envMock.Object, config);
        bool nextCalled = false;

        Task<ActionExecutedContext> next()
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        }

        RequireInternalServiceKeyAttribute attribute = new();

        // Act
        await attribute.OnActionExecutionAsync(context, next);

        // Assert
        nextCalled.Should().BeTrue("Pipeline execution should proceed when valid key is provided");
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task OnActionExecutionAsync_DevelopmentMissingConfig_AllowsDefaultDevKey()
    {
        // Arrange
        Mock<IHostEnvironment> envMock = new();
        envMock.Setup(e => e.EnvironmentName).Returns(Environments.Development);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        HeaderDictionary headers = new()
        {
            { SecurityConstants.InternalServiceKeyHeader, SecurityConstants.DefaultInternalServiceKey }
        };

        ActionExecutingContext context = CreateActionExecutingContext(headers, envMock.Object, config);
        bool nextCalled = false;

        Task<ActionExecutedContext> next()
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        }

        RequireInternalServiceKeyAttribute attribute = new();

        // Act
        await attribute.OnActionExecutionAsync(context, next);

        // Assert
        nextCalled.Should().BeTrue("Development environment allows DefaultInternalServiceKey when unconfigured");
        context.Result.Should().BeNull();
    }
}
