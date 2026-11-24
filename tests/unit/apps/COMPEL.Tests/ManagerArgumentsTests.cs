// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     Verifies the command line built for the Heroes Of Newerth manager: the flag set the legacy COMPEL relied on, the
///     new re-authentication frequency, and the quoting the ""-execute"" payload requires to survive on both platforms.
/// </summary>
public sealed class ManagerArgumentsTests
{
    private static MatchServerManagerOptions SampleOptions()
    {
        return new MatchServerManagerOptions
        {
            UserName = "KONGOR",
            Password = "secret",
            Instances = 2,
            Gateway = "kongor.net",
            Location = "EU",
            ServerNamePrefix = "KONGOR ARENA",
            UseProxy = true,
            PortRangeOffset = 0,
            RuntimeArtefactsPath = "DEFAULT"
        };
    }

    private static string[] Build()
    {
        MatchServerManagerOptions options = SampleOptions();

        return ManagerArguments.Build(options, new PortPlan(options), "1.2.3.4", "masterserver.newbs.io");
    }

    [Fact]
    public void The_Argument_Vector_Starts_With_The_Manager_And_Master_Server_Flags()
    {
        string[] arguments = Build();

        Assert.Equal("-manager", arguments[0]);
        Assert.Contains("-noconfig", arguments);
        Assert.Contains("-masterserver", arguments);
        Assert.Contains("masterserver.newbs.io", arguments);
    }

    [Fact]
    public void The_Execute_Payload_Carries_The_Legacy_Flags_And_The_New_Reauthentication_Frequency()
    {
        string joined = string.Join(' ', Build());

        // The Master Login Carries A Trailing Colon So The Manager Can Append A Per-Instance Index.
        Assert.Contains("Set man_masterLogin KONGOR:", joined);
        Assert.Contains("Set man_startServerPort 11235", joined);
        Assert.Contains("Set man_endServerPort 11236", joined);
        Assert.Contains("Set man_enableProxy true", joined);
        Assert.Contains("Set man_reauthFrequency 30000", joined);
        Assert.Contains("Set host_affinity -1", joined);
        Assert.Contains("Set svr_location EU", joined);
        Assert.Contains("Set svr_ip 1.2.3.4", joined);
        Assert.Contains("Set con_flush false", joined);
        Assert.Contains("Set log_flush false", joined);
        Assert.Contains("Set sys_flushLogs false", joined);
        Assert.Contains("Set con_buffered true", joined);
        Assert.Contains("Set con_showDev false", joined);
        Assert.Contains("Set con_showNet false", joined);
        Assert.Contains("Set con_verbose false", joined);
        Assert.Contains("Set http_printdebuginfo false", joined);
        Assert.Contains("Set php_printdebuginfo false", joined);
        Assert.Contains("Set net_maxPacketRate 128", joined);
        Assert.Contains("Set cl_packetEpsilon 0.001", joined);
        Assert.Contains("Set net_smoothInterpolation 1", joined);
        Assert.Contains("Set sys_sleepTime 0", joined);
        Assert.Contains("Set mem_maxHeapSize 4096", joined);
        Assert.Contains("Set svr_maxFPS 1000", joined);
        Assert.Contains("Set host_maxFPS 1000", joined);
        Assert.Contains("Set host_maxFPS 1000", joined);
        Assert.Contains("Set svr_chatRateLimit 0", joined);
        Assert.Contains("Set svr_gameFPS 20", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("Set svr_seed ", joined);
        Assert.Contains("Set rand_seed ", joined);
    }

    [Fact]
    public void The_Execute_Payload_Is_A_Single_Argument_Wrapped_In_Literal_Quotes()
    {
        string[] arguments = Build();

        int executeIndex = Array.IndexOf(arguments, "-execute");
        string payload = arguments[executeIndex + 1];

        Assert.True(executeIndex >= 0);
        Assert.StartsWith("\"", payload);
        Assert.EndsWith("\"", payload);
    }

    [Fact]
    public void The_Server_Name_Gets_Two_Trailing_Workaround_Tokens()
    {
        string result = ManagerArguments.ServerNameWithWhitespaceWorkaround("KONGOR ARENA");

        Assert.Equal("KONGOR ARENA 0 0", result);
    }
}
