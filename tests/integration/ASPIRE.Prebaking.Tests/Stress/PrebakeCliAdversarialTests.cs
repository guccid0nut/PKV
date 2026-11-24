// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using Kongor.PrebakeTool;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Stress;

[Collection("CliExecution")]
public sealed class PrebakeCliAdversarialTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task Program_Main_WithVersionFlag_ReturnsSuccess()
    {
        // Act
        using (new ConsoleSilencer())
        {
            int exitCode = await Program.Main(["--version"]);
            exitCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task Program_Main_Validate_OnNonExistentDirectory_ExecutesGracefully()
    {
        // Arrange
        string nonExistentDir = Path.Combine(Path.GetTempPath(), $"missing_dir_{Guid.NewGuid():N}");

        // Act
        using (new ConsoleSilencer())
        {
            int exitCode = await Program.Main(["validate", "--game-dir", nonExistentDir]);
            exitCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task Program_Main_Clean_OnEmptyDirectory_ExecutesGracefully()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"clean_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            using (new ConsoleSilencer())
            {
                int exitCode = await Program.Main(["clean", "--game-dir", tempDir]);
                exitCode.Should().Be(0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task Program_Main_All_WithTelemetryLogFile_EmitsValidNdjson()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"cli_telemetry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logFile = Path.Combine(tempDir, "telemetry.ndjson");

        try
        {
            // Act
            using (new ConsoleSilencer())
            {
                int exitCode = await Program.Main([
                    "all",
                    "--game-dir", tempDir,
                    "--shared-dir", tempDir,
                    "--output-dir", Path.Combine(tempDir, "override"),
                    "--dry-run",
                    "--log-file", logFile
                ]);

                // Assert
                exitCode.Should().Be(0);
                File.Exists(logFile).Should().BeTrue("Telemetry log file must be created");

                string[] lines = await File.ReadAllLinesAsync(logFile);
                lines.Should().NotBeEmpty();

                using JsonDocument doc = JsonDocument.Parse(lines[0]);
                JsonElement root = doc.RootElement;
                root.GetProperty("event_name").GetString().Should().Be("PrebakePipelineExecuted");
                root.GetProperty("dry_run").GetBoolean().Should().BeTrue();
                root.GetProperty("elapsed_ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }
}
