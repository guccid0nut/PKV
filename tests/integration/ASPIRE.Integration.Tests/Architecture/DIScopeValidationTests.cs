// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using KONGOR.MasterServer.Services;
using MERRICK.DatabaseContext.Services;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.Architecture;

public class DIScopeValidationTests
{
    [Fact]
    public void MerrickContext_IsRegisteredAsScoped_NotSingleton()
    {
        ServiceCollection services = [];
        services.AddDbContext<MerrickContext>();

        ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(MerrickContext));

        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void Singletons_DoNotDirectlyDependOn_MerrickContext()
    {
        Type dbType = typeof(MerrickContext);
        List<Type> singletonTypesWithDirectDb = [];

        Type[] targetTypes =
        [
            typeof(TelemetryPublisherService),
            typeof(MatchmakingService),
            typeof(WebPortalTelemetrySubscriberService),
            typeof(DiscordPresenceRedisSubscriber),
            typeof(DatabaseInitializer)
        ];

        foreach (Type singletonType in targetTypes)
            singletonTypesWithDirectDb.AddRange(from constructor in singletonType.GetConstructors()
                where constructor.GetParameters().Any(p => p.ParameterType == dbType)
                select singletonType);

        Assert.Empty(singletonTypesWithDirectDb);
    }
}
