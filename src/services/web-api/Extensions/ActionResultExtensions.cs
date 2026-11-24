// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Email;
using NEXUS.Contracts.CQRS.Registration;

namespace ZORGATH.WebPortal.API.Extensions;

/// <summary>
/// Provides extension methods for converting domain and CQRS results into ASP.NET Core <see cref="IActionResult"/> responses.
/// </summary>
public static class ActionResultExtensions
{
    /// <summary>
    /// Converts an <see cref="AdminActionResultDto"/> into an appropriate <see cref="IActionResult"/>.
    /// </summary>
    public static IActionResult ToActionResult(this AdminActionResultDto result, ControllerBase controller)
    {
        if (result.Success)
        {
            return controller.Ok(result);
        }

        return controller.BadRequest(result.Message);
    }

    /// <summary>
    /// Converts a nullable model into an <see cref="IActionResult"/>, returning 200 OK with the model or 404 NotFound.
    /// </summary>
    public static IActionResult ToActionResultOrNotFound<T>(this T? model, ControllerBase controller, string? notFoundMessage = null)
        where T : class
    {
        if (model is null)
        {
            return notFoundMessage != null ? controller.NotFound(notFoundMessage) : controller.NotFound();
        }

        return controller.Ok(model);
    }

    /// <summary>
    /// Converts an <see cref="InitiateRegistrationResult"/> into an <see cref="IActionResult"/>.
    /// </summary>
    public static IActionResult ToActionResult(this InitiateRegistrationResult result, ControllerBase controller)
    {
        return result.StatusCode switch
        {
            StatusCodes.Status200OK => controller.Ok(result.Message),
            StatusCodes.Status400BadRequest => controller.BadRequest(result.Message),
            StatusCodes.Status409Conflict => controller.Conflict(result.Message),
            _ => controller.StatusCode(result.StatusCode, result.Message)
        };
    }

    /// <summary>
    /// Converts a <see cref="RegistrationResult"/> into an <see cref="IActionResult"/>.
    /// </summary>
    public static IActionResult ToActionResult(this RegistrationResult result, ControllerBase controller)
    {
        return result.StatusCode switch
        {
            StatusCodes.Status201Created => controller.CreatedAtAction(
                "GetUser",
                "User",
                new { id = result.UserId },
                result.User),
            StatusCodes.Status400BadRequest => controller.BadRequest(result.ErrorMessage),
            StatusCodes.Status404NotFound => controller.NotFound(result.ErrorMessage),
            StatusCodes.Status409Conflict => controller.Conflict(result.ErrorMessage),
            _ => controller.StatusCode(result.StatusCode, result.ErrorMessage)
        };
    }

    /// <summary>
    /// Converts a <see cref="RegistrationResult"/> into an <see cref="IActionResult"/> with custom route targets.
    /// </summary>
    public static IActionResult ToActionResult(this RegistrationResult result, ControllerBase controller, string actionName, string controllerName)
    {
        return result.StatusCode switch
        {
            StatusCodes.Status201Created => controller.CreatedAtAction(
                actionName,
                controllerName,
                new { id = result.UserId },
                result.User),
            StatusCodes.Status400BadRequest => controller.BadRequest(result.ErrorMessage),
            StatusCodes.Status404NotFound => controller.NotFound(result.ErrorMessage),
            StatusCodes.Status409Conflict => controller.Conflict(result.ErrorMessage),
            _ => controller.StatusCode(result.StatusCode, result.ErrorMessage)
        };
    }

    /// <summary>
    /// Converts a <see cref="RegisterEmailAddressResult"/> into an <see cref="IActionResult"/>.
    /// </summary>
    public static IActionResult ToActionResult(this RegisterEmailAddressResult result, ControllerBase controller)
    {
        return result.StatusCode switch
        {
            StatusCodes.Status200OK => controller.Ok(result.Message),
            StatusCodes.Status400BadRequest => controller.BadRequest(result.Message),
            StatusCodes.Status422UnprocessableEntity => controller.UnprocessableEntity(result.Message),
            StatusCodes.Status503ServiceUnavailable => controller.StatusCode(StatusCodes.Status503ServiceUnavailable, result.Message),
            _ => controller.StatusCode(result.StatusCode, result.Message)
        };
    }
}
