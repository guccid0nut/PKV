// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Patching;
using Grpc.Core;
using NEXUS.Contracts.Cdn.V1;

namespace KONGOR.MasterServer.Services.Grpc;

public sealed class GrpcCdnService(ILogger<GrpcCdnService> logger, IConfiguration configuration)
    : CdnService.CdnServiceBase
{
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<GrpcCdnService> _logger = logger;

    public override Task<GetManifestResponse> GetManifest(GetManifestRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[GrpcCdnService] GetManifest requested for Variant: '{Variant}', Channel: '{Channel}'",
            request.Variant, request.Channel);

        string gameVersion = _configuration["Game:Version"] ?? "5.0.0.0";
        string variant = string.IsNullOrWhiteSpace(request.Variant) ? "client-win64" : request.Variant;
        string channel = string.IsNullOrWhiteSpace(request.Channel) ? "release" : request.Channel;

        PatchManifest manifest = new(
            GameVersion: gameVersion,
            Variant: variant,
            Channel: channel,
            Timestamp: TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(),
            Files: new List<PatchFileEntry>
            {
                new("hon_x64.exe", 25165824L, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
                new("k2_x64.dll", 18874368L, "d41d8cd98f00b204e9800998ecf8427e"),
                new("game/resources0.s2z", 157286400L, "ca978112ca1bbdcafac231b39a23dc4da786081442f0c9707ea16e49e31dff57")
            },
            Deltas: new List<PatchDeltaEntry>()
        );

        string manifestJson =
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false });

        GetManifestResponse response = new()
        {
            GameVersion = gameVersion,
            ManifestJson = manifestJson,
            Ed25519Signature =
                "3045022100e4b8b6a382cf89e02c6114a8e32d4b8f52f8216c52a36b9c9f4d1e2f8a12345602202c6114a8e32d4b8f52f8216c52a36b9c9f4d1e2f8a123456e4b8b6a382cf89e0",
            SignerPublicKey = "ed25519:pk:nexus-masterserver-cdn-root-key-2026"
        };

        return Task.FromResult(response);
    }

    public override Task<InquireDeltaResponse> InquireDelta(InquireDeltaRequest request, ServerCallContext context)
    {
        _logger.LogInformation(
            "[GrpcCdnService] InquireDelta requested for File: '{FilePath}', SrcSHA: '{Src}', TgtSHA: '{Tgt}'",
            request.FilePath, request.SourceFileSha256, request.TargetFileSha256);

        string domain = _configuration["MASTER_SERVER_ADDRESS"] ?? "http://localhost:55555";
        if (!domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            domain = "http://" + domain;

        string deltaUrl =
            $"{domain.TrimEnd('/')}/patch/delta?file={Uri.EscapeDataString(request.FilePath)}&src={request.SourceFileSha256}&tgt={request.TargetFileSha256}";

        InquireDeltaResponse response = new()
        {
            DeltaAvailable = true,
            DeltaPatchUrl = deltaUrl,
            PatchSizeBytes = 1048576,
            PatchSha256 = !string.IsNullOrWhiteSpace(request.TargetFileSha256)
                ? request.TargetFileSha256
                : "delta_patch_sha256_mock",
            CompressionAlgorithm = "zstd"
        };

        return Task.FromResult(response);
    }
}
