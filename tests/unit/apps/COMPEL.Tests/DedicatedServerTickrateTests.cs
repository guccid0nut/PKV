// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using COMPEL.Configuration;
using COMPEL.Services.Supervision;

namespace COMPEL.Tests;

/// <summary>
/// Verifies dedicated server simulation tickrate calibration (30 Hz / 60 Hz),
/// ensuring svr_gameFPS and svr_maxFramesPerHostFrame 5 are correctly configured,
/// clamped between 20 Hz and 60 Hz, and serialized into worker and manager argument vectors.
/// </summary>
public sealed class DedicatedServerTickrateTests
{
    private static MatchServerManagerOptions SampleOptions() => new()
    {
        UserName = "KONGOR",
        Password = "SecretPassword123!",
        Instances = 2,
        Gateway = "127.0.0.1",
        Location = "USE",
        ServerNamePrefix = "KONGOR CALIBRATION",
        UseProxy = true,
        PortRangeOffset = 0,
        RuntimeArtefactsPath = "DEFAULT"
    };

    [Fact]
    public void MatchServerManagerOptions_DefaultTickRate_Is20Hz()
    {
        MatchServerManagerOptions options = new();
        Assert.Equal(20, options.ServerTickRate);
    }

    [Theory]
    [InlineData(10, 20)]   // Below min: clamped to 20
    [InlineData(19, 20)]   // Below min: clamped to 20
    [InlineData(20, 20)]   // Min boundary: 20 Hz (legacy 50ms)
    [InlineData(30, 30)]   // Intermediate: 30 Hz (33.3ms)
    [InlineData(45, 45)]   // Valid mid-range
    [InlineData(60, 60)]   // Max boundary: 60 Hz (competitive 16.6ms)
    [InlineData(100, 60)]  // Above max: clamped to 60
    [InlineData(128, 60)]  // Above max: clamped to 60
    public void MatchServerManagerOptions_ClampsTickRate_Between20And60Hz(int inputTickrate, int expectedTickrate)
    {
        MatchServerManagerOptions options = new() { ServerTickRate = inputTickrate };
        Assert.Equal(expectedTickrate, options.ServerTickRate);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_EmitsCustomTickrate_30Hz()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.ServerTickRate = 30;
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("+Set svr_gameFPS 30", joined);
        Assert.Contains("+Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("Set svr_gameFPS 30", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_EmitsLegacyTickrate_20Hz()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.ServerTickRate = 20;
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("+Set svr_gameFPS 20", joined);
        Assert.Contains("+Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("Set svr_gameFPS 20", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_EmitsCanonicalTickrate_20HzByDefault()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("+Set svr_gameFPS 20", joined);
        Assert.Contains("+Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("Set svr_gameFPS 20", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_EmitsCompetitiveTickrate_60Hz()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.ServerTickRate = 60;
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("+Set svr_gameFPS 60", joined);
        Assert.Contains("+Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("Set svr_gameFPS 60", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Fact]
    public void ManagerArguments_Build_EmitsCustomTickrate_30Hz()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.ServerTickRate = 30;
        PortPlan plan = new(options);

        string[] args = ManagerArguments.Build(options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("Set svr_gameFPS 30", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Fact]
    public void ManagerArguments_Build_EmitsCanonicalTickrate_20HzByDefault()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);

        string[] args = ManagerArguments.Build(options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("Set svr_gameFPS 20", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Fact]
    public void ManagerArguments_Build_EmitsCompetitiveTickrate_60Hz()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.ServerTickRate = 60;
        PortPlan plan = new(options);

        string[] args = ManagerArguments.Build(options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("Set svr_gameFPS 60", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void MatchServerManagerOptionsValidator_AcceptsCalibratedTickrates(int tickrate)
    {
        MatchServerManagerOptions options = SampleOptions();
        options.ServerTickRate = tickrate;

        var result = new MatchServerManagerOptionsValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }
}
