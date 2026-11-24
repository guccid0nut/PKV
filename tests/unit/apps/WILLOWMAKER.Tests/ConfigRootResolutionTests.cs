// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

/// <summary>
///     Regression tests for the "config files written into the current directory" bug: parameterless
///     WriteCustomConfiguration() (called by the resolution / refresh-rate / display-mode / network-profile change
///     handlers) used to fall back to Environment.CurrentDirectory, which scattered autoexec.cfg, game/, KONGOR/ and
///     userdataKONGOR/ into the repository root.
/// </summary>
public sealed class ConfigRootResolutionTests
{
    [Fact]
    public void ResolveClientConfigRoot_ExplicitDirectory_IsReturnedVerbatim()
    {
        MainViewModel viewModel = new();
        string explicitDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_ExplicitRoot_" + Guid.NewGuid().ToString("N"));

        Assert.Equal(explicitDir, viewModel.ResolveClientConfigRoot(explicitDir, "test"));
    }

    [Fact]
    public void ResolveClientConfigRoot_NoDirectory_ResolvesGameClientNotCurrentDirectory()
    {
        MainViewModel viewModel = new();

        string? resolved = viewModel.ResolveClientConfigRoot(null, "test");

        // Module initializer points HON_GAME_PATH at the sandbox client, which outranks every other resolver input.
        Assert.Equal(WillowmakerTestSandboxInitializer.SandboxClientDirectory, resolved);
        Assert.NotEqual(Environment.CurrentDirectory, resolved);
    }

    [Fact]
    public void WriteCustomConfiguration_NoDirectory_WritesIntoResolvedClient_AndNothingIntoCurrentDirectory()
    {
        string cwd = Environment.CurrentDirectory;
        string[] cwdArtefacts =
        [
            Path.Combine(cwd, "autoexec.cfg"),
            Path.Combine(cwd, "game", "autoexec.cfg"),
            Path.Combine(cwd, "KONGOR", "configuration", "autoexec.cfg"),
            Path.Combine(cwd, "userdataKONGOR", "configuration", "autoexec.cfg")
        ];
        Dictionary<string, DateTime?> before = cwdArtefacts.ToDictionary(p => p, p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : (DateTime?)null);

        MainViewModel viewModel = new();
        viewModel.WriteCustomConfiguration();

        string sandboxAutoexec = Path.Combine(WillowmakerTestSandboxInitializer.SandboxClientDirectory, "userdataKONGOR", "configuration", "autoexec.cfg");
        Assert.True(File.Exists(sandboxAutoexec), $"Expected autoexec.cfg in resolved client userdataKONGOR dir: {sandboxAutoexec}");

        foreach (string artefact in cwdArtefacts)
        {
            DateTime? after = File.Exists(artefact) ? File.GetLastWriteTimeUtc(artefact) : null;
            Assert.True(before[artefact] == after, $"WriteCustomConfiguration() touched the current directory: {artefact}");
        }
    }
}
