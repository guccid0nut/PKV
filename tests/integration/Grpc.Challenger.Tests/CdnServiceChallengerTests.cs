// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using KONGOR.MasterServer.Services.Grpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Cdn.V1;
using Xunit;

namespace Grpc.Challenger.Tests;

public sealed class CdnServiceChallengerTests
{
    private static IConfiguration CreateConfiguration(string? domain = null, string? version = null)
    {
        Dictionary<string, string?> settings = [];
        if (domain != null) settings["MASTER_SERVER_ADDRESS"] = domain;
        if (version != null) settings["Game:Version"] = version;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public async Task GetManifest_WithStandardAndEmptyRequest_GeneratesValidJsonAndSignature()
    {
        IConfiguration config = CreateConfiguration("http://master.kongor.net:55555", "4.10.1.5");
        GrpcCdnService service = new(NullLogger<GrpcCdnService>.Instance, config);
        ServerCallContext ctx = new AdversarialServerCallContext();

        // 1. Standard request
        GetManifestRequest req1 = new()
        {
            Variant = "client-linux64",
            Channel = "beta"
        };
        GetManifestResponse res1 = await service.GetManifest(req1, ctx);

        res1.Should().NotBeNull();
        res1.GameVersion.Should().Be("4.10.1.5");
        res1.ManifestJson.Should().NotBeNullOrWhiteSpace();
        res1.Ed25519Signature.Should().NotBeNullOrWhiteSpace();
        res1.SignerPublicKey.Should().NotBeNullOrWhiteSpace();

        // Verify JSON is well-formed
        using (JsonDocument doc = JsonDocument.Parse(res1.ManifestJson))
        {
            doc.RootElement.GetProperty("version").GetString().Should().Be("4.10.1.5");
            doc.RootElement.GetProperty("variant").GetString().Should().Be("client-linux64");
            doc.RootElement.GetProperty("channel").GetString().Should().Be("beta");
            doc.RootElement.GetProperty("files").GetArrayLength().Should().BeGreaterThan(0);
        }

        // 2. Empty / Malformed request (should default gracefully)
        GetManifestRequest req2 = new()
        {
            Variant = "",
            Channel = ""
        };
        GetManifestResponse res2 = await service.GetManifest(req2, ctx);

        res2.Should().NotBeNull();
        using (JsonDocument doc = JsonDocument.Parse(res2.ManifestJson))
        {
            doc.RootElement.GetProperty("variant").GetString().Should().Be("client-win64");
            doc.RootElement.GetProperty("channel").GetString().Should().Be("release");
        }
    }

    [Fact]
    public async Task InquireDelta_WithSpecialCharactersAndEmptyFields_SafelyEncodesUrl()
    {
        IConfiguration config = CreateConfiguration("master.kongor.net:55555"); // No protocol prefix
        GrpcCdnService service = new(NullLogger<GrpcCdnService>.Instance, config);
        ServerCallContext ctx = new AdversarialServerCallContext();

        InquireDeltaRequest req = new()
        {
            FilePath = "game/resources0.s2z?param=injected&exploit=true",
            SourceFileSha256 = "1111222233334444",
            TargetFileSha256 = "5555666677778888"
        };

        InquireDeltaResponse res = await service.InquireDelta(req, ctx);

        res.Should().NotBeNull();
        res.DeltaAvailable.Should().BeTrue();
        res.DeltaPatchUrl.Should().StartWith("http://master.kongor.net:55555/patch/delta?file=");
        // Ensure the injected query string in FilePath was properly escaped
        res.DeltaPatchUrl.Should().Contain("%3Fparam%3Dinjected%26exploit%3Dtrue");
        res.PatchSha256.Should().Be("5555666677778888");
        res.CompressionAlgorithm.Should().Be("zstd");
    }

    [Fact]
    public async Task CdnService_HighConcurrency_ReturnsConsistentManifests()
    {
        IConfiguration config = CreateConfiguration("http://localhost:55555", "4.10.1.0");
        GrpcCdnService service = new(NullLogger<GrpcCdnService>.Instance, config);
        ServerCallContext ctx = new AdversarialServerCallContext();

        List<Task> tasks = [];

        for (int i = 0; i < 50; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                GetManifestResponse manRes = await service.GetManifest(new GetManifestRequest
                {
                    Variant = index % 2 == 0 ? "client-win64" : "server-win64",
                    Channel = "release"
                }, ctx);

                manRes.Should().NotBeNull();
                manRes.GameVersion.Should().Be("4.10.1.0");

                InquireDeltaResponse deltaRes = await service.InquireDelta(new InquireDeltaRequest
                {
                    FilePath = $"test_file_{index}.s2z",
                    SourceFileSha256 = $"src_{index}",
                    TargetFileSha256 = $"tgt_{index}"
                }, ctx);

                deltaRes.Should().NotBeNull();
                deltaRes.DeltaAvailable.Should().BeTrue();
            }));
        }

        await Task.WhenAll(tasks);
    }
}
