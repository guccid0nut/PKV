// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Services;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ScrimmageDeterminismTests
{
    [Fact]
    public void ComputeSimulationHash_WithIdenticalParameters_YieldsIdenticalHash()
    {
        var service = new ScrimmageDeterminismService();

        string hash1 = service.ComputeSimulationHash(seed: 1337, map: "caldavar", bots: 10, durationSeconds: 15);
        string hash2 = service.ComputeSimulationHash(seed: 1337, map: "caldavar", bots: 10, durationSeconds: 15);

        hash1.Should().NotBeNullOrWhiteSpace();
        hash1.Length.Should().Be(64, "SHA-256 hex string should be 64 characters");
        hash1.Should().Be(hash2, "Identical simulation parameters must produce identical replay hashes");
    }

    [Fact]
    public void ComputeSimulationHash_WithDifferentSeeds_YieldsDivergentHashes()
    {
        var service = new ScrimmageDeterminismService();

        string hash1 = service.ComputeSimulationHash(seed: 1337, map: "caldavar", bots: 10, durationSeconds: 15);
        string hash2 = service.ComputeSimulationHash(seed: 9999, map: "caldavar", bots: 10, durationSeconds: 15);

        hash1.Should().NotBe(hash2, "Different seeds must produce different replay hashes");
    }

    [Fact]
    public void ComputeSimulationHash_WithDifferentMapsOrBots_YieldsDivergentHashes()
    {
        var service = new ScrimmageDeterminismService();

        string baseHash = service.ComputeSimulationHash(seed: 1337, map: "caldavar", bots: 10, durationSeconds: 15);
        string diffMapHash = service.ComputeSimulationHash(seed: 1337, map: "midwars", bots: 10, durationSeconds: 15);
        string diffBotsHash = service.ComputeSimulationHash(seed: 1337, map: "caldavar", bots: 6, durationSeconds: 15);

        baseHash.Should().NotBe(diffMapHash);
        baseHash.Should().NotBe(diffBotsHash);
    }

    [Fact]
    public void VerifyDeterminism_DualPassValidation_SucceedsAndConfirmsBitForBitInvariance()
    {
        List<string> logs = [];
        var service = new ScrimmageDeterminismService(msg => logs.Add(msg));

        var (isDeterministic, pass1, pass2, divergent) =
            service.VerifyDeterminism(seed: 4242, map: "caldavar", bots: 10, durationSeconds: 10);

        isDeterministic.Should().BeTrue("Dual-pass simulation with the same seed must be deterministic");
        pass1.Should().Be(pass2);
        pass1.Should().NotBe(divergent);
        logs.Should().Contain(l => l.Contains("Identical Passes: True"));
    }

    [Fact]
    public void ScrimmageDeterminismReport_JsonSerialization_RoundtripsSuccessfully()
    {
        var report = new ScrimmageDeterminismReport
        {
            MatchId = "SCRIMMAGE_1337_TEST",
            Seed = 1337,
            Map = "caldavar",
            Bots = 10,
            DurationSeconds = 15,
            TotalTicks = 300,
            TickRate = 20,
            ReplayWalChecksum = "2340e8893d1660b678b03161e7e41a5bf0f8cff82a4fcbdc9c07af990fd4d1c9",
            Pass2Checksum = "2340e8893d1660b678b03161e7e41a5bf0f8cff82a4fcbdc9c07af990fd4d1c9",
            IsDeterministic = true,
            LatencyMs = 1.2,
            JitterMs = 0.4,
            PacketLossPercentage = 0.0,
            HealthLevel = "Optimal",
            Timestamp = DateTimeOffset.Parse("2026-10-05T20:00:00Z")
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        json.Should().Contain("\"isDeterministic\": true");
        json.Should().Contain("\"seed\": 1337");

        var deserialized = JsonSerializer.Deserialize<ScrimmageDeterminismReport>(json);
        deserialized.Should().NotBeNull();
        deserialized!.MatchId.Should().Be(report.MatchId);
        deserialized.ReplayWalChecksum.Should().Be(report.ReplayWalChecksum);
        deserialized.IsDeterministic.Should().BeTrue();
    }
}
