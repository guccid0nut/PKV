// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.Metrics;
using ASPIRE.Common.Services.EntityCache;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using KONGOR.MasterServer.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Telemetry;

public sealed class HeroDefinitionServiceTelemetryIntegrationTests : IDisposable
{
    private readonly List<(string InstrumentName, long Value, Dictionary<string, object?> Tags)>
        _counterRecordings = [];

    private readonly MeterListener _listener;
    private readonly object _recordLock = new();

    public HeroDefinitionServiceTelemetryIntegrationTests()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NexusMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            lock (_recordLock)
            {
                Dictionary<string, object?> tagDict = [];
                foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
                _counterRecordings.Add((instrument.Name, measurement, tagDict));
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public async Task GetHeroIdentifier_And_GetBaseHeroId_EmitHitsAndMisses()
    {
        // Arrange
        EntityCacheContainer container = new()
        {
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 999,
                    Name = "Hero_PrebakedHero",
                    DisplayName = "Prebaked Hero"
                }
            ]
        };

        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        await registry.InitializeFromContainerAsync(container);

        HeroDefinitionService service = new(
            NullLogger<HeroDefinitionService>.Instance,
            new MockHostEnvironment(),
            registry);

        // Act 1: Query Prebaked Hero
        string idf1 = service.GetHeroIdentifier(999);
        uint baseId1 = service.GetBaseHeroId("Hero_PrebakedHero");

        // Act 2: Query Legacy Mapped Hero (e.g. Hero 153 -> Hero_Accursed)
        string idf2 = service.GetHeroIdentifier(153);
        uint baseId2 = service.GetBaseHeroId("Hero_Accursed");

        // Assert
        idf1.Should().Be("Hero_PrebakedHero");
        baseId1.Should().Be(999u);
        idf2.Should().Be("Hero_Accursed");
        baseId2.Should().Be(153u);

        lock (_recordLock)
        {
            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Hero" &&
                (string?)r.Tags["source_tier"] == "PreBakedMemory");

            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Hero" &&
                (string?)r.Tags["source_tier"] == "LegacyMemory");
        }
    }

    private sealed class MockHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "KONGOR.MasterServer";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
