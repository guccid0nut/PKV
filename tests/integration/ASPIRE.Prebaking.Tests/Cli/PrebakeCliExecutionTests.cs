// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using Kongor.PrebakeTool;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Cli;

[Collection("CliExecution")]
public sealed class PrebakeCliExecutionTests
{
    [Fact]
    public async Task Program_Main_WithHelp_ReturnsSuccess()
    {
        // Act
        using (new ConsoleSilencer())
        {
            int exitCode = await Program.Main(["--help"]);
            exitCode.Should().Be(0);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Program_Main_WithDryRun_ExecutesWithoutError()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempLog = Path.Combine(tempDir, "prebake_log.ndjson");

        try
        {
            // Act
            using (new ConsoleSilencer())
            {
                int exitCode = await Program.Main([
                    "all",
                    "--dry-run",
                    "--game-dir", tempDir,
                    "--output-dir", Path.Combine(tempDir, "override"),
                    "--log-file", tempLog
                ]);

                // Assert
                exitCode.Should().Be(0);
                File.Exists(tempLog).Should().BeTrue();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
