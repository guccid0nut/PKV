// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Cli.Tests;

public sealed class TaoMcpRuntimeAndFastPathTests : IDisposable
{
    private readonly string _tempRoot;

    public TaoMcpRuntimeAndFastPathTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "kongor_cli_tao_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TaoMcpRuntimeAndFastPathTests] Cleanup notice: {ex.Message}");
        }
    }

    private static string CreateFakeDotNetRoot(string path, bool isWindows, params string[] netcoreVersions)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, isWindows ? "dotnet.exe" : "dotnet"), "fake-dotnet");
        Directory.CreateDirectory(Path.Combine(path, "host", "fxr", "11.0.0"));
        foreach (string ver in netcoreVersions)
        {
            Directory.CreateDirectory(Path.Combine(path, "shared", "Microsoft.NETCore.App", ver));
        }
        return path;
    }

    [Fact]
    public void ResolveDotNetRoot_AcrossWindowsLinuxAndMacOs_PrefersGa11OverPreview11()
    {
        // protects: Cross-OS .NET 11.0 runtime auto-discovery and GA-over-preview selection in LifecycleService
        // fails_when: Preview-only ProgramFiles dotnet beats user-local GA 11.0.0 or Linux/macOS ~/.dotnet roots are ignored
        // seam: Real LifecycleService.ResolveDotNetRoot + EnsureDotNetSdkEnvironment against filesystem layouts
        string winProgFiles = Path.Combine(_tempRoot, "win_progfiles");
        string winLocalApp = Path.Combine(_tempRoot, "win_localapp");
        string previewRoot = CreateFakeDotNetRoot(Path.Combine(winProgFiles, "dotnet"), isWindows: true, "11.0.0-preview.7.26381.103");
        string gaRoot = CreateFakeDotNetRoot(Path.Combine(winLocalApp, "Microsoft", "dotnet"), isWindows: true, "11.0.0");

        Dictionary<string, string?> winEnv = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = previewRoot,
            ["LOCALAPPDATA"] = winLocalApp,
            ["ProgramFiles"] = winProgFiles
        };

        string? resolvedWin = LifecycleService.ResolveDotNetRoot(
            platformOverride: "win32",
            getEnv: key => winEnv.TryGetValue(key, out string? v) ? v : null,
            homeOverride: Path.Combine(_tempRoot, "win_home"));

        resolvedWin.Should().Be(gaRoot);

        string linuxHome = Path.Combine(_tempRoot, "linux_home");
        string linuxDotNet = CreateFakeDotNetRoot(Path.Combine(linuxHome, ".dotnet"), isWindows: false, "10.0.4", "11.0.1");
        string? resolvedLinux = LifecycleService.ResolveDotNetRoot(
            platformOverride: "linux",
            getEnv: _ => null,
            homeOverride: linuxHome);
        resolvedLinux.Should().Be(linuxDotNet);

        string macHome = Path.Combine(_tempRoot, "mac_home");
        string macDotNet = CreateFakeDotNetRoot(Path.Combine(macHome, ".dotnet"), isWindows: false, "11.0.0");
        string? resolvedMac = LifecycleService.ResolveDotNetRoot(
            platformOverride: "darwin",
            getEnv: _ => null,
            homeOverride: macHome);
        resolvedMac.Should().Be(macDotNet);

        ProcessStartInfo psi = new("dotnet");
        psi.Environment["DOTNET_ROOT"] = gaRoot;
        psi.EnvironmentVariables["PATH"] = $"{gaRoot}{Path.PathSeparator}/usr/bin";
        LifecycleService.EnsureDotNetSdkEnvironment(psi);
        psi.Environment["DOTNET_ROOT"].Should().NotBeNullOrWhiteSpace();
        psi.Environment["DOTNET_ROLL_FORWARD"].Should().Be("LatestMinor");
    }

    [Fact]
    public void AntigravityRegistrar_ResolveCanonicalDeveloperMcpCommand_NeverPersistsTempShadowPath()
    {
        // protects: mcp_config.json stability against ephemeral %TEMP%/kongor_mcp_runtime/pid_XXXX/kongor.exe registration (MCP-267)
        // fails_when: --register-only writes a shadow-copy executable path into mcp_config.json or clobbers sibling MCP servers
        // seam: AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand + UpsertAgentMcpServerConfigs
        string fakeRepo = Path.Combine(_tempRoot, "repo");
        string toolsDir = Path.Combine(fakeRepo, "src", "tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(fakeRepo, "ProjectKongor.slnx"), "<Solution />");
        string taoPy = Path.Combine(toolsDir, "tao_mcp.py");
        File.WriteAllText(taoPy, "# tao launcher\n");

        string ephemeralShadowExe = Path.Combine(_tempRoot, "kongor_mcp_runtime", "pid_7800", "kongor.exe");
        var (cmd, args, env) = AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand(
            processPath: ephemeralShadowExe,
            repoRootOverride: fakeRepo);

        cmd.Should().Be("python");
        args.Should().Equal("-u", taoPy);
        env.Should().NotBeNull();
        env!["PYTHONUNBUFFERED"].Should().Be("1");

        string fakeHome = Path.Combine(_tempRoot, "user_home");
        string mcpConfigDir = Path.Combine(fakeHome, ".gemini", "antigravity");
        Directory.CreateDirectory(mcpConfigDir);
        string mcpConfigPath = Path.Combine(mcpConfigDir, "mcp_config.json");
        File.WriteAllText(mcpConfigPath, """
        {
          "mcpServers": {
            "everything": {
              "command": "npx",
              "args": ["everything-mcp"]
            },
            "kongor": {
              "command": "C:\\Temp\\kongor_mcp_runtime\\pid_7800\\kongor.exe",
              "args": ["mcp"]
            }
          }
        }
        """);

        AntigravityRegistrar.UpsertAgentMcpServerConfigs(cmd, args, bundleRoot: null, userHomeOverride: fakeHome, extraEnv: env);

        using JsonDocument updated = JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
        JsonElement servers = updated.RootElement.GetProperty("mcpServers");
        servers.GetProperty("everything").GetProperty("command").GetString().Should().Be("npx");

        JsonElement kongor = servers.GetProperty("kongor");
        kongor.GetProperty("command").GetString().Should().Be("python");
        kongor.GetProperty("args")[0].GetString().Should().Be("-u");
        kongor.GetProperty("args")[1].GetString().Should().Be(taoPy);
        kongor.GetProperty("env").GetProperty("PYTHONUNBUFFERED").GetString().Should().Be("1");
    }

    [Fact]
    public async Task NexusMailboxAndHealthCheck_WhenOffline_FastReturnUnder150Ms()
    {
        // protects: Sub-500ms kongor_health_check latency when hon_x64 is closed (zero 300ms-3000ms pipe/mailbox polling stalls)
        // fails_when: NexusMailboxIpcBridge.ConnectAsync polls Task.Delay(50) while offline or HealthCheckHandler reports non-zero pingLatencyMs when disconnected
        // seam: Real NexusMailboxIpcBridge + CompositeGameIpcBridge + HealthCheckHandler
        string mailboxDir = Path.Combine(_tempRoot, "offline_mailbox");
        await using NexusMailboxIpcBridge mailbox = new(mailboxDir)
        {
            ForceConnected = false
        };

        // Warm up NexusMailboxIpcBridge.ConnectAsync JIT before timed offline latency assertion
        await mailbox.ConnectAsync(timeoutMs: 3000);
        Stopwatch swConnect = Stopwatch.StartNew();
        await mailbox.ConnectAsync(timeoutMs: 3000);
        swConnect.Stop();
        swConnect.ElapsedMilliseconds.Should().BeLessThan(100);

        await using NativeClientIpcBridge nativeBridge = new("kongor-nonexistent-test-pipe-" + Guid.NewGuid().ToString("N"));
        await using McpServerIpcBridge serverBridge = new(autoStart: false);
        await using CompositeGameIpcBridge composite = new(nativeBridge, serverBridge, mailbox)
        {
            NativePipePresenceProbe = () => false
        };

        TaoClient taoClient = new(mailboxBridge: mailbox, fallbackBridge: composite);
        TaoCapabilitiesHandler capsHandler = new(taoClient);
        ErrorsSinceHandler errorsHandler = new(repoRoot: _tempRoot);
        Mock<IClientProcessRegistry> emptyRegistry = new();

        HealthCheckHandler healthHandler = new(
            taoClient,
            capsHandler,
            errorsHandler,
            processRegistry: emptyRegistry.Object,
            gameIpcBridge: composite,
            allowAmbientProcessFallback: false);

        using JsonDocument reqDoc = JsonDocument.Parse("{}");
        // Warm up HealthCheckHandler / System.Text.Json JIT before timed offline latency assertion
        _ = await healthHandler.HandleAsync(reqDoc.RootElement);
        Stopwatch swHealth = Stopwatch.StartNew();
        ToolCallResult res = await healthHandler.HandleAsync(reqDoc.RootElement);
        swHealth.Stop();

        res.IsError.Should().BeFalse();
        swHealth.ElapsedMilliseconds.Should().BeLessThan(150);

        using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
        outDoc.RootElement.GetProperty("status").GetString().Should().Be("offline");
        outDoc.RootElement.GetProperty("elapsedMs").GetDouble().Should().BeLessThan(150.0);
        outDoc.RootElement.GetProperty("checks").GetProperty("mailbox").GetProperty("connected").GetBoolean().Should().BeFalse();
        outDoc.RootElement.GetProperty("checks").GetProperty("mailbox").GetProperty("pingLatencyMs").GetDouble().Should().Be(0);
    }

    [Fact]
    public async Task ClusterDiagnosticsService_UnboundLoopbackAndLiveRedis_CompletesUnder500Ms()
    {
        // protects: Sub-500ms kongor_diagnostics execution on Windows even when loopback cluster ports are unbound or live services (:55555/:5200) respond concurrently
        // fails_when: Winsock loopback SYN retransmission stalls unbound TCP/HTTP probes to 500ms-1556ms or live loopback listeners are falsely skipped
        // seam: Real ClusterDiagnosticsService + IPGlobalProperties listener table + real loopback TcpListener Redis mock
        ClusterDiagnosticsService svc = new();
        // Warm up SocketsHttpHandler / IPGlobalProperties JIT before timed assertion
        _ = await svc.ProbeCompelPoolAsync();

        using TcpListener fakeRedis = new(IPAddress.Loopback, 0);
        fakeRedis.Start();
        int livePort = ((IPEndPoint)fakeRedis.LocalEndpoint).Port;

        Task acceptTask = Task.Run(async () =>
        {
            using TcpClient accepted = await fakeRedis.AcceptTcpClientAsync();
            await using NetworkStream ns = accepted.GetStream();
            byte[] buf = new byte[256];
            _ = await ns.ReadAsync(buf);
            byte[] pong = Encoding.UTF8.GetBytes("+PONG\r\n");
            await ns.WriteAsync(pong);
            _ = await ns.ReadAsync(buf);
            byte[] info = Encoding.UTF8.GetBytes("redis_version:7.2.0\r\nconnected_clients:2\r\ndb0:keys=42,expires=0\r\n");
            await ns.WriteAsync(info);
        });

        // Reserve and immediately close an ephemeral port to guarantee an unbound port number
        int closedPort;
        using (TcpListener temp = new(IPAddress.Loopback, 0))
        {
            temp.Start();
            closedPort = ((IPEndPoint)temp.LocalEndpoint).Port;
            temp.Stop();
        }

        Stopwatch sw = Stopwatch.StartNew();
        ClusterDiagnosticsReport report = await svc.RunDiagnosticsAsync(
            redisHost: "127.0.0.1",
            redisPort: livePort,
            dbConnectionString: $"Host=127.0.0.1;Port={closedPort};Database=merrick;Username=postgres",
            maxLogErrors: 5,
            repoRoot: _tempRoot);
        sw.Stop();

        await acceptTask;

        report.Redis.Connected.Should().BeTrue();
        report.Redis.TotalKeys.Should().Be(42);
        report.Database.Connected.Should().BeFalse();
        report.TotalProbeDurationMs.Should().BeLessThan(500.0);
        sw.ElapsedMilliseconds.Should().BeLessThan(550);
    }

    [Fact]
    public void VectorSearchHandler_DefaultConstructor_IsLazyAndNonBlocking()
    {
        // protects: Fast McpToolRegistry cold start by deferring StackExchange.Redis ConnectionMultiplexer.Connect until kongor_vector_search is called
        // fails_when: VectorSearchHandler constructor synchronously opens TCP/Redis sockets during McpServer startup
        // seam: VectorSearchHandler default constructor timing
        _ = new VectorSearchHandler(logger: _ => { });
        Stopwatch sw = Stopwatch.StartNew();
        VectorSearchHandler handler = new(logger: _ => { });
        sw.Stop();

        handler.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }
}
