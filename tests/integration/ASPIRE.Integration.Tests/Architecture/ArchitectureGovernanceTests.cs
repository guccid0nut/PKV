// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ArchitectureGovernanceTests.cs
// System: ASPIRE.Integration.Tests - Architectural Boundary Governance
// Policy: NetArchTest Layer Guards, Sub-Second Execution, Zero Circular Dependencies
// ============================================================================

using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace ASPIRE.Tests.Architecture;

/// <summary>
/// Architectural layer governance enforcing separation of concerns, strict layer boundaries,
/// directed acyclic layer dependencies (zero circular references), and decoupling between
/// core domains, services, and native desktop applications.
/// </summary>
public sealed class ArchitectureGovernanceTests
{
    private static readonly Assembly ContractsAssembly = typeof(global::NEXUS.Contracts.Domain.Identifiers.AccountId).Assembly;
    private static readonly Assembly CoreAssembly = typeof(global::ASPIRE.Common.Constants.ConfigurationConstants).Assembly;
    private static readonly Assembly DatabaseContextAssembly = typeof(global::MERRICK.DatabaseContext.Persistence.MerrickContext).Assembly;
    private static readonly Assembly MasterServerAssembly = typeof(global::KONGOR.MasterServer.Services.HeroDefinitionService).Assembly;
    private static readonly Assembly ChatServerAssembly = typeof(global::TRANSMUTANSTEIN.ChatServer.Services.TelemetryPublisherService).Assembly;
    private static readonly Assembly WebPortalUIAssembly = typeof(global::DAWNBRINGER.WebPortal.UI.Services.WebPortalTelemetrySubscriberService).Assembly;
    private static readonly Assembly WebPortalAPIAssembly = typeof(global::ZORGATH.WebPortal.API.Services.UserService).Assembly;

    [Fact]
    public void NexusContracts_HasZeroDependencies_OnHigherSolutionAssemblies()
    {
        // Assert: NEXUS.Contracts is Layer 0 - must be completely decoupled from all higher layers
        TestResult result = Types.InAssembly(ContractsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MERRICK.DatabaseContext",
                "KONGOR.MasterServer",
                "TRANSMUTANSTEIN.ChatServer",
                "ZORGATH.WebPortal.API",
                "DAWNBRINGER.WebPortal.UI",
                "WILLOWMAKER",
                "COMPEL",
                "Kongor.Cli",
                "ReplayStudio",
                "NEXUS.CdnBroker",
                "NEXUS.VulkanInterop",
                "NEXUS.SpatialAudio"
            )
            .GetResult();

        AssertTrueWithDetails(result, "NEXUS.Contracts violated layer purity by referencing higher-layer assemblies.");
    }

    [Fact]
    public void KongorCore_HasZeroDependencies_OnApplicationsOrHigherServices()
    {
        // Assert: Kongor.Core (ASPIRE.Common) is Layer 1 - must never depend on applications or backend services
        TestResult result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MERRICK.DatabaseContext",
                "WILLOWMAKER",
                "COMPEL",
                "Kongor.Cli",
                "ReplayStudio",
                "NEXUS.ControlPanel",
                "KONGOR.MasterServer",
                "TRANSMUTANSTEIN.ChatServer",
                "ZORGATH.WebPortal.API",
                "DAWNBRINGER.WebPortal.UI"
            )
            .GetResult();

        AssertTrueWithDetails(result, "Kongor.Core (ASPIRE.Common) leaked dependencies into applications or services.");
    }

    [Fact]
    public void WebPortalUI_HasZeroDependencies_OnNativeLibrariesOrLaunchers()
    {
        // Assert: DAWNBRINGER.WebPortal.UI is a web frontend - must never reference native binary graphics/audio or desktop launchers
        TestResult result = Types.InAssembly(WebPortalUIAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Silk.NET",
                "NEXUS.VulkanInterop",
                "NEXUS.SpatialAudio",
                "WILLOWMAKER",
                "COMPEL"
            )
            .GetResult();

        AssertTrueWithDetails(result, "DAWNBRINGER.WebPortal.UI leaked dependencies into native binary layers or desktop launchers.");
    }

    [Fact]
    public void WebPortalAPI_HasZeroDependencies_OnNativeLibrariesOrLaunchers()
    {
        // Assert: ZORGATH.WebPortal.API is a REST/gRPC backend - must never reference native desktop layers or launchers
        TestResult result = Types.InAssembly(WebPortalAPIAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Silk.NET",
                "NEXUS.VulkanInterop",
                "NEXUS.SpatialAudio",
                "WILLOWMAKER",
                "COMPEL"
            )
            .GetResult();

        AssertTrueWithDetails(result, "ZORGATH.WebPortal.API leaked dependencies into native binary layers or desktop launchers.");
    }

    [Fact]
    public void DatabaseContext_HasZeroDependencies_OnWebOrApps()
    {
        // Assert: MERRICK.DatabaseContext is Layer 2 - must never depend on Web UI, Web API, or desktop launcher applications
        TestResult result = Types.InAssembly(DatabaseContextAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "DAWNBRINGER.WebPortal.UI",
                "ZORGATH.WebPortal.API",
                "WILLOWMAKER",
                "COMPEL",
                "Kongor.Cli"
            )
            .GetResult();

        AssertTrueWithDetails(result, "MERRICK.DatabaseContext leaked dependencies into Web UI or applications.");
    }

    [Fact]
    public void MasterServer_HasZeroDependencies_OnWebUIOrApps()
    {
        // Assert: KONGOR.MasterServer must never depend on the Web UI or desktop launchers
        TestResult result = Types.InAssembly(MasterServerAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "DAWNBRINGER.WebPortal.UI",
                "WILLOWMAKER",
                "COMPEL"
            )
            .GetResult();

        AssertTrueWithDetails(result, "KONGOR.MasterServer leaked dependencies into Web UI or applications.");
    }

    [Fact]
    public void ChatServer_HasZeroDependencies_OnWebUIOrApps()
    {
        // Assert: TRANSMUTANSTEIN.ChatServer must never depend on Web UI or desktop launchers
        TestResult result = Types.InAssembly(ChatServerAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "DAWNBRINGER.WebPortal.UI",
                "WILLOWMAKER",
                "COMPEL"
            )
            .GetResult();

        AssertTrueWithDetails(result, "TRANSMUTANSTEIN.ChatServer leaked dependencies into Web UI or applications.");
    }

    [Fact]
    public void LayerArchitecture_IsStrictlyAcyclicAndDirectional()
    {
        // Assert: Layer directionality forms a strict Directed Acyclic Graph (DAG)
        // Layer 0: NEXUS.Contracts
        // Layer 1: Kongor.Core (ASPIRE.Common) -> references Layer 0
        // Layer 2: MERRICK.DatabaseContext -> references Layer 1, Layer 0
        // Layer 4: Services -> reference Layer 2, Layer 1, Layer 0
        // Upward references are prohibited, eliminating circular dependencies by construction.
        
        TestResult contractsNoCore = Types.InAssembly(ContractsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("MERRICK.DatabaseContext")
            .GetResult();
        AssertTrueWithDetails(contractsNoCore, "Layer 0 (Contracts) depends on Layer 2 (DatabaseContext).");

        TestResult coreNoDb = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("MERRICK.DatabaseContext")
            .GetResult();
        AssertTrueWithDetails(coreNoDb, "Layer 1 (Core) depends on Layer 2 (DatabaseContext).");

        TestResult dbNoServices = Types.InAssembly(DatabaseContextAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("KONGOR.MasterServer", "TRANSMUTANSTEIN.ChatServer", "ZORGATH.WebPortal.API")
            .GetResult();
        AssertTrueWithDetails(dbNoServices, "Layer 2 (DatabaseContext) depends on Layer 4 (Services).");
    }

    [Fact]
    public void MediatR_RequestContracts_MustOnlyExistInNexusContracts()
    {
        // Assert: MediatR request contracts (IBaseRequest: IRequest, IRequest<T>) must live in NEXUS.Contracts (Layer 0)
        // so that MCP tools, Web API, Master Server, and external consumers can reference them
        // without taking direct dependencies on concrete service implementation assemblies.
        Assembly[] serviceAssemblies =
        [
            MasterServerAssembly,
            WebPortalAPIAssembly,
            ChatServerAssembly,
            WebPortalUIAssembly
        ];

        foreach (Assembly assembly in serviceAssemblies)
        {
            TestResult result = Types.InAssembly(assembly)
                .ShouldNot()
                .ImplementInterface<MediatR.IBaseRequest>()
                .GetResult();

            AssertTrueWithDetails(result,
                $"MediatR request contracts leaked into {assembly.GetName().Name}. " +
                $"Move all IRequest<T> and IRequest definitions to NEXUS.Contracts.");
        }
    }

    [Fact]
    public void Controllers_MustNot_DependOn_MerrickContext()
    {
        // Assert: Controllers must depend on IMediator/ILogger and never directly on MerrickContext
        Assembly[] controllerAssemblies = [WebPortalAPIAssembly, MasterServerAssembly];

        foreach (Assembly assembly in controllerAssemblies)
        {
            TestResult result = Types.InAssembly(assembly)
                .That()
                .Inherit<Microsoft.AspNetCore.Mvc.ControllerBase>()
                .ShouldNot()
                .HaveDependencyOnAny(typeof(global::MERRICK.DatabaseContext.Persistence.MerrickContext).FullName!)
                .GetResult();

            AssertTrueWithDetails(result,
                $"Controller in {assembly.GetName().Name} violated architectural boundary by referencing MerrickContext.");
        }
    }

    private static void AssertTrueWithDetails(TestResult result, string failureMessage)
    {
        if (!result.IsSuccessful)
        {
            string offending = result.FailingTypes != null && result.FailingTypes.Count > 0
                ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
                : "Unknown offending types";

            Assert.Fail($"{failureMessage} Offending types: [{offending}]");
        }
    }
}
