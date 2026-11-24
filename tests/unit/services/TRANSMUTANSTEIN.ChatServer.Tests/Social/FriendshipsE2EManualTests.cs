// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Social;

public sealed class FriendshipsE2EManualTests
{
    // protects: Full 15-stage Friendships, Presence, Invites, Monitor Game, Spectate/Mentor, and Redis PeerInvalidation wire contract
    // fails_when: Any stage in scripts/qa/test_friendships_e2e.ps1 regresses against live MasterServer + ChatServer + SQL Server + Garnet
    // seam: Real KONGOR.MasterServer + TRANSMUTANSTEIN.ChatServer OS processes over TCP/HTTP + MSSQLLocalDB + Garnet
    [Fact]
    [Trait("Category", "Manual")]
    public async Task FriendshipsAndSpectate_FifteenStageHermeticE2E_PassesAllStages()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string scriptPath = Path.Combine(repoRoot, "scripts", "qa", "test_friendships_e2e.ps1");
        File.Exists(scriptPath).Should().BeTrue($"Expected E2E script at {scriptPath} — run from repository checkout.");

        ProcessStartInfo psi = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell.exe for scripts/qa/test_friendships_e2e.ps1.");

        Task<string> stdOutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = proc.StandardError.ReadToEndAsync();

        await proc.WaitForExitAsync();
        string stdOut = await stdOutTask;
        string stdErr = await stdErrTask;

        proc.ExitCode.Should().Be(
            0,
            $"scripts/qa/test_friendships_e2e.ps1 failed (exit={proc.ExitCode}). Diagnose with '.\\scripts\\test_friendships_e2e.ps1':\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");
        stdOut.Should().Contain("[STAGE 15/15] [PASS]");
    }
}
