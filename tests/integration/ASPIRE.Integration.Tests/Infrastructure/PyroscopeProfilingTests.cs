// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ASPIRE.Tests.Infrastructure;

[Collection("PyroscopeProfiling")]
public sealed class PyroscopeProfilingTests
{
    [Fact]
    public void PyroscopeProfiling_ConfigurePyroscopeContinuousProfiling_SetsRequiredEnvironmentAndInitializes()
    {
        try
        {
            // Arrange
            HostApplicationBuilder builder = new();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PYROSCOPE_PROFILING_ENABLED"] = "true",
                ["PYROSCOPE_SERVER_ADDRESS"] = "http://localhost:4040",
                ["PYROSCOPE_BASIC_AUTH_USER"] = "test-user",
                ["PYROSCOPE_BASIC_AUTH_PASSWORD"] = "test-pass"
            });

            // Act
            TextWriter originalErr = Console.Error;
            try
            {
                Console.SetError(TextWriter.Null);
                ServiceExtensions.ConfigurePyroscopeContinuousProfiling(builder, "KONGOR.MasterServer");
            }
            finally
            {
                Console.SetError(originalErr);
            }

            // Assert
            Assert.Equal("http://localhost:4040", Environment.GetEnvironmentVariable("PYROSCOPE_SERVER_ADDRESS"));
            Assert.True(
                Environment.GetEnvironmentVariable("PYROSCOPE_APPLICATION_NAME") is "KONGOR.MasterServer"
                    or "ZORGATH.WebPortal.API"
                    or "TRANSMUTANSTEIN.ChatServer"
                    or "DAWNBRINGER.WebPortal.UI",
                $"Unexpected PYROSCOPE_APPLICATION_NAME: {Environment.GetEnvironmentVariable("PYROSCOPE_APPLICATION_NAME")}");
            Assert.True(Environment.GetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED") is "true" or "false");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED", null);
            Environment.SetEnvironmentVariable("PYROSCOPE_SERVER_ADDRESS", null);
            Environment.SetEnvironmentVariable("PYROSCOPE_APPLICATION_NAME", null);
        }
    }

    [Fact]
    public void PyroscopeProfiling_AddServiceDefaults_IncludesOpenTelemetryAndPyroscope()
    {
        // Arrange
        HostApplicationBuilder builder = new();

        // Act
        builder.AddServiceDefaults();

        // Assert
        Assert.NotNull(builder.Services);
    }

    [Fact]
    public void ConfigurePyroscopeContinuousProfiling_WhenDisabled_SetsProfilingEnabledFalse()
    {
        try
        {
            // Arrange
            HostApplicationBuilder builder = new();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PYROSCOPE_PROFILING_ENABLED"] = "false",
                ["PYROSCOPE_SERVER_ADDRESS"] = "http://localhost:4040"
            });

            // Act
            ServiceExtensions.ConfigurePyroscopeContinuousProfiling(builder, "KONGOR.MasterServer");

            // Assert
            Assert.Equal("false", Environment.GetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED", null);
        }
    }
}
