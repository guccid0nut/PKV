// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.EntityCache;
using KONGOR.MasterServer.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Runtime;

public sealed class HeroDefinitionServicePrebakedIntegrationTests
{
    [Fact]
    public async Task HeroDefinitionService_WithPrebakedRegistry_QueriesPrebakedHeroesDirectly()
    {
        EntityCacheContainer container = new()
        {
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 999,
                    Name = "Hero_CustomPrebakedHero",
                    DisplayName = "Custom Prebaked Hero",
                    PrimaryAttribute = "Strength",
                    AltAvatars =
                    [
                        new AltAvatarDto
                        {
                            Key = "Hero_CustomPrebakedHero.Alt1",
                            HeroName = "Hero_CustomPrebakedHero",
                            DisplayName = "Custom Alt Avatar"
                        }
                    ]
                }
            ]
        };

        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        await registry.InitializeFromContainerAsync(container);

        HeroDefinitionService service = new(
            NullLogger<HeroDefinitionService>.Instance,
            new MockHostEnvironment(),
            registry);

        // 1. GetHeroIdentifier
        string ident = service.GetHeroIdentifier(999);
        Assert.Equal("Hero_CustomPrebakedHero", ident);

        // 2. GetBaseHeroId
        uint baseId = service.GetBaseHeroId("Hero_CustomPrebakedHero");
        Assert.Equal(999u, baseId);

        // 3. IsHero
        Assert.True(service.IsHero(999));

        // 4. GetAllHeroIds contains prebaked ID
        IEnumerable<uint> allIds = service.GetAllHeroIds();
        Assert.Contains(999u, allIds);

        // 5. Rich queries
        HeroDefinitionDto? def = service.GetHeroDefinition(999);
        Assert.NotNull(def);
        Assert.Equal("Custom Prebaked Hero", def.DisplayName);

        HeroDefinitionDto? defByName = service.GetHeroDefinition("Hero_CustomPrebakedHero");
        Assert.NotNull(defByName);
        Assert.Equal(999u, defByName.Id);

        AltAvatarDto? alt = service.GetAltAvatar("Hero_CustomPrebakedHero", "Hero_CustomPrebakedHero.Alt1");
        Assert.NotNull(alt);
        Assert.Equal("Custom Alt Avatar", alt.DisplayName);

        IReadOnlyCollection<HeroDefinitionDto> allDefs = service.GetAllHeroDefinitions();
        Assert.NotEmpty(allDefs);
        Assert.Contains(allDefs, h => h.Id == 999);
    }

    [Fact]
    public void HeroDefinitionService_WithoutPrebakedRegistry_FallsBackToLegacyMappings()
    {
        HeroDefinitionService service = new(
            NullLogger<HeroDefinitionService>.Instance,
            new MockHostEnvironment());

        // Base hero lookups should continue working via legacy mappings
        string accursedIdent = service.GetHeroIdentifier(153);
        Assert.Equal("Hero_Accursed", accursedIdent);

        uint accursedBaseId = service.GetBaseHeroId("Hero_Accursed");
        Assert.Equal(153u, accursedBaseId);

        Assert.True(service.IsHero(153));
    }

    private sealed class MockHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "KONGOR.MasterServer";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
