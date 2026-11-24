// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using COMPEL.Services.Supervision;

namespace COMPEL.Tests;

/// <summary>
/// Verifies that DedicatedWorkerArguments correctly constructs the argument vector
/// for direct dedicated server workers (hon_x64 in -dedicated mode), including sub-account
/// authentication credentials, dedicated Named Pipe IPC, and zero-lag 128-tick networking cvars.
/// </summary>
public sealed class DedicatedWorkerArgumentsTests
{
    private static MatchServerManagerOptions SampleOptions() => new()
    {
        UserName = "KONGOR",
        Password = "SecretPassword123!",
        Instances = 2,
        Gateway = "127.0.0.1",
        Location = "USE",
        ServerNamePrefix = "KONGOR BATTLEGROUNDS",
        UseProxy = true,
        PortRangeOffset = 0,
        RuntimeArtefactsPath = "DEFAULT"
    };

    [Fact]
    public void DedicatedWorkerArguments_Build_GeneratesCorrectDedicatedFlagsAndNamedPipe()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: plan.LocalGameStart,
            voicePort: plan.LocalVoiceStart,
            publicGamePort: plan.PublicGameStart,
            publicVoicePort: plan.PublicVoiceStart,
            serverName: $"{options.ServerNamePrefix} #1");

        string[] args = DedicatedWorkerArguments.Build(
            worker,
            options,
            plan,
            serverAddress: "127.0.0.1",
            masterServerHostAndPort: "127.0.0.1:55555");

        Assert.Equal("-dedicated", args[0]);
        Assert.Equal("-noconfig", args[1]);
        Assert.Contains("-port", args);
        Assert.Contains("11235", args);
        Assert.Contains("-voiceport", args);
        Assert.Contains("11735", args);
        Assert.Contains("-masterserver", args);
        Assert.Contains("127.0.0.1:55555", args);
        Assert.Contains("-pipe", args);
        Assert.Contains("hon-server-11235", args);
        Assert.Contains("-slave", args);
        Assert.Contains("-writeconsole", args);
        Assert.Contains("-logfile", args);
        Assert.Contains("server_11235.log", args);
        Assert.Contains("-execute", args);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_FormatsSubAccountLoginCorrectlyForMasterServer()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);

        // Worker 1: KONGOR:1
        DedicatedServerWorker worker1 = new(1, 11235, 11735, 21235, 21735, "Server #1");
        string[] args1 = DedicatedWorkerArguments.Build(worker1, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined1 = string.Join(' ', args1);
        Assert.Contains("+Set svr_login KONGOR:1", joined1);
        Assert.Contains("+Set svr_password SecretPassword123!", joined1);
        Assert.Contains("+Set svr_port 11235", joined1);
        Assert.Contains("+Set svr_voicePort 11735", joined1);

        // Worker 2: KONGOR:2
        DedicatedServerWorker worker2 = new(2, 11236, 11736, 21236, 21736, "Server #2");
        string[] args2 = DedicatedWorkerArguments.Build(worker2, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined2 = string.Join(' ', args2);
        Assert.Contains("+Set svr_login KONGOR:2", joined2);
        Assert.Contains("+Set svr_port 11236", joined2);
        Assert.Contains("+Set svr_voicePort 11736", joined2);
        Assert.Contains("hon-server-11236", joined2);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_IncludesZeroLag128TickNetworkingAndCleanLoggingCvars()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11735, 21235, 21735, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("+Set net_maxPacketRate 128", joined);
        Assert.Contains("+Set net_maxPacketSize 1350", joined);
        Assert.Contains("+Set cl_packetEpsilon 0.001", joined);
        Assert.Contains("+Set net_smoothInterpolation 1", joined);
        Assert.Contains("+Set sys_sleepTime 0", joined);
        Assert.Contains("+Set mem_maxHeapSize 4096", joined);
        Assert.Contains("+Set svr_maxFPS 1000", joined);
        Assert.Contains("+Set host_maxFPS 1000", joined);
        Assert.Contains("+Set con_flush true", joined);
        Assert.Contains("+Set log_flush true", joined);
        Assert.Contains("+Set sys_flushLogs true", joined);
        Assert.Contains("+Set con_buffered false", joined);
        Assert.Contains("+Set sys_showConsole false", joined);
        Assert.Contains("+Set con_log 1", joined);
        Assert.Contains("+Set host_writeConsole true", joined);
        Assert.Contains("+Set svr_slave 1", joined);
        Assert.Contains("+Set svr_gameFPS 20", joined);
        Assert.Contains("+Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("+Set svr_heartbeatInterval 15000", joined);
        Assert.Contains("+Set svr_matchIDRequestTotalTimeout 4000", joined);
        Assert.Contains("+Set svr_matchIDRequired false", joined);
        Assert.Contains("+Set svr_authRequestTotalTimeout 2000", joined);
        Assert.Contains("+Set svr_authRequestAttempts 2", joined);
        Assert.Contains("Set svr_gameFPS 20", joined);
        Assert.Contains("Set svr_maxFramesPerHostFrame 5", joined);
        Assert.Contains("Set svr_heartbeatInterval 15000", joined);
        Assert.Contains("Set svr_matchIDRequestTotalTimeout 4000", joined);
        Assert.Contains("Set svr_matchIDRequired false", joined);
        Assert.Contains("Set svr_authRequestTotalTimeout 2000", joined);
        Assert.Contains("Set svr_authRequestAttempts 2", joined);
    }

    [Fact]
    public void DedicatedWorkerArguments_ServerNameWithWhitespaceWorkaround_AppendsTokens()
    {
        string formatted = DedicatedWorkerArguments.ServerNameWithWhitespaceWorkaround("DONUT ARENA #1");
        Assert.Equal("DONUT ARENA #1 0 0", formatted);
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_SetsAlignedHostAffinityToPrimaryPCore()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11735, 21235, 21735, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        nint mask = CpuTopologyResolver.CalculateAffinityMask(0, options.Instances, CpuAffinityMode.PCoresOnly);
        int expectedPrimaryCore = CpuTopologyResolver.GetPrimaryCoreFromMask(mask);

        Assert.Contains($"+Set host_affinity {expectedPrimaryCore}", joined);
    }

    // protects: CHost::Init (0x1808b9990) -execute quote truncation on Linux & CConsole::Execute (0x180843880) $/[]/# evaluation
    // fails_when: -execute contains any inner '"' (0x22) or leaves '$', '[', '#', or spaces unescaped
    // seam: DedicatedWorkerArguments.Build + EscapeK2ExecuteToken real argument builder
    [Fact]
    public void DedicatedWorkerArguments_Build_EscapesExecuteTokensWithoutInnerQuotesForLinuxAndCvarSafety()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.UserName = "guccihost_dock";
        options.Password = "cIV$c3sGUkxR!OUM$tail";
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(2001, 11335, 11835, 11335, 11835, "^m[K3S] ^oDONUT ARENA ^g #D1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "host.docker.internal:55555");
        int execIdx = Array.IndexOf(args, "-execute");
        Assert.True(execIdx >= 0 && execIdx + 1 < args.Length);

        string executePayload = args[execIdx + 1];
        Assert.DoesNotContain("\"", executePayload);
        Assert.Contains(@"Set svr_name ^m\[K3S\]\ ^oDONUT\ ARENA\ ^g\ \#D1", executePayload);
        Assert.Contains("Set svr_login guccihost_dock:2001", executePayload);
        Assert.Contains(@"Set svr_password cIV\$c3sGUkxR!OUM\$tail", executePayload);
    }

    // protects: K2 CFileManager::SetModStack reverse evaluation order (override must be above game/KONGOR/* so archived resources*.s2z never shadow override/*.entity and cause CGameClient::ProcessSnapshot unread_bits disconnects)
    // fails_when: "override" is placed before "game" or "KONGOR/*" in -mod, or "server" is not preserved at back() for ~: userdata isolation
    // seam: DedicatedWorkerArguments.Build real filesystem & mod-stack argument builder
    [Fact]
    public void DedicatedWorkerArguments_Build_MountsOverrideAboveGameAndKongorTiersWithServerAtTop()
    {
        string tempInstallDir = Path.Combine(Path.GetTempPath(), "CompelModOrder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempInstallDir, "KONGOR", "extensions"));

        try
        {
            MatchServerManagerOptions options = SampleOptions();
            PortPlan plan = new(options);
            DedicatedServerWorker worker = new(1, 11235, 11735, 21235, 21735, "Server #1");

            string[] args = DedicatedWorkerArguments.Build(
                worker,
                options,
                plan,
                serverAddress: "127.0.0.1",
                masterServerHostAndPort: "127.0.0.1:55555",
                installationDirectory: tempInstallDir);

            int modIdx = Array.IndexOf(args, "-mod");
            Assert.True(modIdx >= 0 && modIdx + 1 < args.Length);

            string[] tiers = args[modIdx + 1].Split(';');
            int basePos = Array.IndexOf(tiers, "base");
            int gamePos = Array.IndexOf(tiers, "game");
            int extPos = Array.IndexOf(tiers, "KONGOR/extensions");
            int overridePos = Array.IndexOf(tiers, "override");
            int serverPos = Array.IndexOf(tiers, "server");

            Assert.True(basePos >= 0 && gamePos > basePos, $"Expected game after base in '{args[modIdx + 1]}'");
            Assert.True(extPos > gamePos, $"Expected KONGOR/extensions after game in '{args[modIdx + 1]}'");
            Assert.True(overridePos > extPos, $"Expected override after KONGOR/extensions in '{args[modIdx + 1]}'");
            Assert.Equal(tiers.Length - 1, serverPos);
        }
        finally
        {
            if (Directory.Exists(tempInstallDir))
            {
                Directory.Delete(tempInstallDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DedicatedWorkerArguments_Build_SetsNetMaxPacketSizeTo1350()
    {
        MatchServerManagerOptions options = SampleOptions();
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, 11735, 21235, 21735, "Server #1");

        string[] args = DedicatedWorkerArguments.Build(worker, options, plan, "127.0.0.1", "127.0.0.1:55555");
        string joined = string.Join(' ', args);

        Assert.Contains("+Set net_maxPacketSize 1350", joined);
    }
}
