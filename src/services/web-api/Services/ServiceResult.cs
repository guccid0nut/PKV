// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ZORGATH.WebPortal.API.Services;

public enum ServiceErrorType
{
    None,
    BadRequest,
    NotFound,
    Conflict,
    Unauthorized,
    UnprocessableEntity,
    InternalError
}

public static class ServiceResult
{
    public static ServiceResult<T> Success<T>(T data)
    {
        return new ServiceResult<T> { IsSuccess = true, Data = data };
    }

    public static ServiceResult<T> Failure<T>(string message, ServiceErrorType type)
    {
        return new ServiceResult<T> { IsSuccess = false, ErrorMessage = message, ErrorType = type };
    }

    public static ServiceResult<T> ValidationFailure<T>(IEnumerable<string> errors)
    {
        return new ServiceResult<T>
        {
            IsSuccess = false,
            ValidationErrors = errors,
            ErrorType = ServiceErrorType.BadRequest,
            ErrorMessage = "Validation Failed"
        };
    }
}

public class ServiceResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public ServiceErrorType ErrorType { get; init; }
    public IEnumerable<string>? ValidationErrors { get; init; }
}
