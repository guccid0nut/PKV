// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Core.Configuration;
using Spectre.Console.Cli;

namespace Kongor.Cli.Infrastructure;

internal enum KongorLayoutSupport
{
    AllLayouts = 0,
    SourceWorkspaceOnly = 1
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class KongorLayoutRequirementAttribute(KongorLayoutSupport support) : Attribute
{
    public KongorLayoutSupport Support { get; } = support;
}

/// <summary>
/// Spectre.Console.Cli <see cref="ICommandInterceptor"/> that enforces developer-source vs. standalone-bundle
/// layout capability boundaries before command execution.
/// </summary>
internal sealed class KongorLayoutGateInterceptor : ICommandInterceptor
{
    /// <summary>
    /// Canonical primary command names (and branch subcommands) that require a verified developer
    /// <see cref="KongorWorkspaceKind.SourceWorkspace"/> checkout and must be cleanly rejected with
    /// actionable guidance when running against a standalone <see cref="KongorWorkspaceKind.BundledInstall"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> SourceWorkspaceOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "build",
        "test",
        "package",
        "gate",
        "oracle",
        "practice",
        "inject",
        "decompile",
        "sanitize",
        "sync-lua",
        "sbom",
        "scan",
        "soak",
        "migrate",
        "tailwind",
        "install-hooks",
        "audit-all",
        "test-e2e",
        "docgen",
        "clean",
        "sync-version",
        "master-server",
        "chat-server",
        "web-api",
        "web-ui",
        "secret",
        "audit-endpoints",
        "lua-verify",
        "k2-test",
        "prebake",
        "all",
        "entities",
        "lua",
        "bundle"
    };

    public static bool RequiresSourceWorkspace(string? commandName, object? commandData = null)
    {
        if (commandData is KongorLayoutSupport support)
        {
            return support == KongorLayoutSupport.SourceWorkspaceOnly;
        }

        return !string.IsNullOrWhiteSpace(commandName) && SourceWorkspaceOnlyCommands.Contains(commandName);
    }

    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (context == null)
        {
            return;
        }

        if (!RequiresSourceWorkspace(context.Name, context.Data))
        {
            return;
        }

        // Verify active workspace is a SourceWorkspace (throws actionable KongorWorkspaceNotFoundException if BundledInstall or missing)
        if (CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws) && ws is not null)
        {
            if (ws.IsBundledInstall)
            {
                string message =
                    $"Command 'kongor {context.Name}' requires a Project Kongor developer source checkout, " +
                    $"but the active workspace '{ws.Root}' is a standalone BundledInstall (via {ws.Source}).\n" +
                    "Fix: Switch to a developer checkout with 'kongor workspace use \"<path-to-ProjectKongor>\"' " +
                    "or pass '--root \"<path-to-ProjectKongor>\"'.";
                Console.Error.WriteLine($"[kongor] [LayoutGate] Blocked source-only command '{context.Name}' in BundledInstall '{ws.Root}'.");
                throw new KongorWorkspaceNotFoundException(message, [$"LayoutGate({context.Name}): BundledInstall at {ws.Root}"]);
            }

            return;
        }

        // Neither source nor bundle resolved: trigger canonical 6-tier failure message immediately
        _ = CliRuntimeContext.RequireSourceWorkspace();
    }
}
