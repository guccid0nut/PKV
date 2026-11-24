// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using ZORGATH.WebPortal.API.Services.Grpc;

namespace ZORGATH.WebPortal.API.Serialization;

/// <summary>
///     Source-generated JSON serialization context for ZORGATH.WebPortal.API,
///     providing reflection-free, Native AOT compatible serialization for gRPC SRP session state.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GrpcAuthService.GrpcSrpSessionState))]
public sealed partial class WebApiJsonContext : JsonSerializerContext
{
}
