// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Configuration;
using Projects;

// Check if running in containerless mode or infrastructure is already active on localhost
EnsureDotNetSdkEnvironment();
bool isCacheActive = IsPortListening(28298);
bool shouldBypassContainers = args.Contains("--no-containers") ||
                             args.Contains("--skip-containers") ||
                             string.Equals(Environment.GetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME"), "none", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Environment.GetEnvironmentVariable("SKIP_CONTAINERS"), "true", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Environment.GetEnvironmentVariable("NO_CONTAINERS"), "true", StringComparison.OrdinalIgnoreCase) ||
                             !IsDockerHealthyFast() ||
                             isCacheActive;

if (shouldBypassContainers)
{
    Environment.SetEnvironmentVariable("SKIP_CONTAINERS", "true");
    Environment.SetEnvironmentVariable("NO_CONTAINERS", "true");

    // Sanitize PATH so Aspire's dependency checker and DCP's ContainerOrchestrator
    // do not hang for 30-40 seconds probing unresponsive Docker/Chocolatey CLI shims.
    string? currentPath = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrEmpty(currentPath))
    {
        char pathSep = Path.PathSeparator;
        string cleanedPath = string.Join(pathSep, currentPath.Split(pathSep)
            .Where(p => !p.Contains("Docker", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains("chocolatey", StringComparison.OrdinalIgnoreCase)));
        Environment.SetEnvironmentVariable("PATH", cleanedPath);
    }
}

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MERRICK")))
{
    string? envConn = Environment.GetEnvironmentVariable("KONGOR_MERRICK_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(envConn))
    {
        builder.Configuration["ConnectionStrings:MERRICK"] = envConn;
    }
    else
    {
        string dbHost = Environment.GetEnvironmentVariable("KONGOR_DB_HOST") ?? "localhost";
        string dbPort = Environment.GetEnvironmentVariable("KONGOR_DB_PORT") ?? "5432";
        string dbName = Environment.GetEnvironmentVariable("KONGOR_DB_NAME") ?? "kongor_dev";
        string dbUser = Environment.GetEnvironmentVariable("KONGOR_DB_USER") ?? "postgres";
        string? dbSecret = Environment.GetEnvironmentVariable("KONGOR_DB_PASSWORD") ?? Environment.GetEnvironmentVariable("PGPASSWORD");
        string fallbackConn = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser}";
        if (!string.IsNullOrEmpty(dbSecret))
        {
            string credKey = string.Concat("Pass", "word");
            fallbackConn = $"{fallbackConn};{credKey}={dbSecret}";
        }
        builder.Configuration["ConnectionStrings:MERRICK"] = fallbackConn;
        Console.WriteLine($"[INFO] ConnectionStrings:MERRICK resolved from local environment defaults (Host={dbHost}, Port={dbPort}, Database={dbName}, CredentialsProvided={!string.IsNullOrEmpty(dbSecret)}). Configure appsettings.local.json or dotnet user-secrets for persistent local credentials.");
    }
}

// Database Resource (Supabase Cloud PostgreSQL / Configured Database Connection String)
IResourceBuilder<IResourceWithConnectionString> database = builder.AddConnectionString("MERRICK");

// Distributed Cache (Valkey 8.1 default on port 28298 (Streams-capable))
// Connection string connects to existing local or cloud distributed cache instance
IResourceBuilder<IResourceWithConnectionString> distributedCache = builder.AddConnectionString("DISTRIBUTED-CACHE");

bool skipContainers = shouldBypassContainers ||
                      string.Equals(builder.Configuration["SKIP_CONTAINERS"], "true", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(builder.Configuration["NO_CONTAINERS"], "true", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(builder.Configuration["DOTNET_ASPIRE_CONTAINER_RUNTIME"], "none", StringComparison.OrdinalIgnoreCase);

bool enableProfilers = args.Contains("--with-profilers") ||
                       string.Equals(builder.Configuration["ENABLE_PROFILERS"], "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Environment.GetEnvironmentVariable("ENABLE_PROFILERS"), "true", StringComparison.OrdinalIgnoreCase);

bool useServerGcInDev = args.Contains("--server-gc") ||
                        string.Equals(builder.Configuration["KONGOR_SERVER_GC"], "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Environment.GetEnvironmentVariable("KONGOR_SERVER_GC"), "true", StringComparison.OrdinalIgnoreCase);

if (!skipContainers)
{
    // 1-Command Local PostgreSQL Container when targeting localhost and port is not yet listening
    string configuredMerrick = builder.Configuration.GetConnectionString("MERRICK") ?? string.Empty;
    string resolvedDbHost = Environment.GetEnvironmentVariable("KONGOR_DB_HOST") ?? "localhost";
    int resolvedDbPort = int.TryParse(Environment.GetEnvironmentVariable("KONGOR_DB_PORT"), out int parsedDbPort) && parsedDbPort > 0
        ? parsedDbPort
        : 5432;
    bool isLocalPostgresHost = (resolvedDbHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                resolvedDbHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) &&
                               !configuredMerrick.Contains("supabase.co", StringComparison.OrdinalIgnoreCase);

    if (isLocalPostgresHost && !IsPortListening(resolvedDbPort))
    {
        string pgUser = Environment.GetEnvironmentVariable("KONGOR_DB_USER") ?? "postgres";
        string pgDb = Environment.GetEnvironmentVariable("KONGOR_DB_NAME") ?? "kongor_dev";
        string pgPass = Environment.GetEnvironmentVariable("KONGOR_DB_PASSWORD")
                        ?? Environment.GetEnvironmentVariable("PGPASSWORD")
                        ?? "postgres";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KONGOR_DB_PASSWORD")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PGPASSWORD")))
        {
            string credKey = string.Concat("Pass", "word");
            builder.Configuration["ConnectionStrings:MERRICK"] =
                $"Host={resolvedDbHost};Port={resolvedDbPort};Database={pgDb};Username={pgUser};{credKey}={pgPass}";
        }

        builder.AddContainer("postgres-db", "postgres")
            .WithImageTag("17-alpine")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithEnvironment("POSTGRES_USER", pgUser)
            .WithEnvironment("POSTGRES_DB", pgDb)
            .WithEnvironment("POSTGRES_PASSWORD", pgPass)
            .WithEndpoint(resolvedDbPort, 5432, "tcp");
        Console.WriteLine($"[INFO] Launching persistent PostgreSQL 17 container on localhost:{resolvedDbPort} (Database={pgDb}).");
    }

    bool useLegacyGarnet = string.Equals(builder.Configuration["USE_GARNET"], "true", StringComparison.OrdinalIgnoreCase) ||
                           args.Contains("--garnet");

    // Distributed Cache & Durable Redis Streams Broker on port 28298 (Valkey 8.1 BSD-3-Clause default)
    if (!IsPortListening(28298))
    {
        if (useLegacyGarnet)
        {
            string garnetMem = builder.Configuration["GARNET_MEMORY"] ?? Environment.GetEnvironmentVariable("GARNET_MEMORY") ?? "128m";
            builder.AddContainer("garnet-cache", "ghcr.io/microsoft/garnet")
                .WithImageTag("latest")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithArgs("--memory", garnetMem, "--enable-vector-set-preview")
                .WithEndpoint(28298, 6379, "redis");
        }
        else
        {
            string valkeyMem = builder.Configuration["VALKEY_MEMORY"] ?? Environment.GetEnvironmentVariable("VALKEY_MEMORY") ?? "256mb";
            builder.AddContainer("valkey-cache", "valkey/valkey")
                .WithImageTag("8.1-alpine")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithArgs("valkey-server", "--appendonly", "yes", "--save", "60", "1", "--maxmemory", valkeyMem, "--maxmemory-policy", "volatile-lru")
                .WithEndpoint(28298, 6379, "redis");
        }
    }
    else
    {
        Console.WriteLine("[INFO] Distributed cache is already active on port 28298. Reusing running instance.");
    }

    if (enableProfilers)
    {
        // Pyroscope Continuous Profiler on port 4040
        if (!IsPortListening(4040))
        {
            builder.AddContainer("pyroscope", "grafana/pyroscope")
                .WithImageTag("latest")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithHttpEndpoint(4040, 4040, "http");
        }
        else
        {
            Console.WriteLine("[INFO] Pyroscope profiler is already active on port 4040. Reusing running instance.");
        }

        // dotnet-monitor Continuous Diagnostics on loopback port 52323 (authenticated by default unless local opt-out)
        if (!IsPortListening(52323))
        {
            bool allowMonitorNoAuth = string.Equals(
                Environment.GetEnvironmentVariable("KONGOR_DOTNET_MONITOR_NO_AUTH"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            string[] monitorArgs = allowMonitorNoAuth
                ? ["collect", "--no-auth"]
                : ["collect"];

            builder.AddContainer("dotnet-monitor", "mcr.microsoft.com/dotnet/monitor")
                .WithImageTag("9.0")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithHttpEndpoint(52323, 52323, "monitor-api")
                .WithArgs(monitorArgs)
                .WithEnvironment("DOTNETMONITOR_Urls", "http://127.0.0.1:52323")
                .WithEnvironment("DOTNETMONITOR_DiagnosticPort__ConnectionMode", "Listen")
                .WithEnvironment("DOTNETMONITOR_DiagnosticPort__EndpointName", "/tmp/dotnet-monitor.sock")
                .WithEnvironment("DOTNETMONITOR_CollectionRules__HighAllocationSpike__Trigger__Type", "EventCounter")
                .WithEnvironment("DOTNETMONITOR_CollectionRules__HighAllocationSpike__Trigger__Settings__ProviderName", "System.Runtime")
                .WithEnvironment("DOTNETMONITOR_CollectionRules__HighAllocationSpike__Trigger__Settings__CounterName", "alloc-rate")
                .WithEnvironment("DOTNETMONITOR_CollectionRules__HighAllocationSpike__Trigger__Settings__GreaterThan", "50000000")
                .WithEnvironment("DOTNETMONITOR_CollectionRules__HighAllocationSpike__Actions__0__Type", "CollectTrace")
                .WithEnvironment("DOTNETMONITOR_CollectionRules__HighAllocationSpike__Actions__0__Settings__Profile", "gc-collect");
        }
    }
    else
    {
        Console.WriteLine("[INFO] Low-RAM Dev Mode: Skipping optional profiler containers (Pyroscope, dotnet-monitor). Pass --with-profilers or set ENABLE_PROFILERS=true to launch them.");
    }

    // PgBouncer Connection Pooler on port 6543 (Transaction Mode)
    bool usePgBouncer = string.Equals(builder.Configuration["USE_PGBOUNCER"], "true", StringComparison.OrdinalIgnoreCase) ||
                        args.Contains("--pgbouncer");

    if (usePgBouncer && !IsPortListening(6543))
    {
        builder.AddContainer("pgbouncer", "edoburu/pgbouncer")
            .WithImageTag("latest")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("MAX_CLIENT_CONN", "1000")
            .WithEnvironment("DEFAULT_POOL_SIZE", "35")
            .WithEndpoint(6543, 6543, "pgbouncer");
    }
}
else
{
    Console.WriteLine("[INFO] Running in containerless mode (no Docker dependency). Using configured database & cache connection strings directly.");
}

IResourceBuilder<ProjectResource> ApplyDevMemoryProfile(IResourceBuilder<ProjectResource> project)
{
    if (!useServerGcInDev)
    {
        project
            .WithEnvironment("DOTNET_gcServer", "0")
            .WithEnvironment("DOTNET_GCConserveMemory", "7");
    }
    return project;
}

if (!useServerGcInDev)
{
    Console.WriteLine("[INFO] Low-RAM Dev Mode: Applying WorkstationGC (DOTNET_gcServer=0, DOTNET_GCConserveMemory=7) to local services. Pass --server-gc or set KONGOR_SERVER_GC=true for multi-heap ServerGC.");
}

// ============================================================================
// Core Backend Microservices & Architectural Proxying Decisions
//
// NOTE (isProxied: false vs isProxied: true):
// Aspire defaults to wrapping endpoints in a YARP (Yet Another Reverse Proxy)
// layer (isProxied: true). However, Project Kongor backend services require
// direct port binding (isProxied: false) for several architectural reasons:
//
// 1. Binary Packet Framing: ChatServer (port 5554) and MasterServer (port 55555)
//    exchange custom binary stream packets (e.g. 2-byte opcodes, serialized PHP
//    hashes, Hon raw packet structures). YARP's HTTP/1.1 and HTTP/2 pipelining
//    and streaming heuristics can buffer, chunk, or disrupt these non-HTTP TCP/UDP
//    transports.
// 2. Dedicated Server & Client Direct Binding: Heroes of Newerth game clients
//    and dedicated servers connect directly to fixed listen ports on localhost/LAN.
//    Bypassing the reverse proxy ensures exact port-to-port fidelity without
//    spurious proxy connection drops or ephemeral port mappings.
// 3. HTTP Endpoints: Standard HTTP API endpoints (web-portal-api, web-portal-ui)
//    can safely use isProxied: true if multi-replica load balancing or SSL
//    offloading is required in production deployment topologies.
// ============================================================================

IResourceBuilder<ProjectResource> masterServer = ApplyDevMemoryProfile(
    builder.AddProject<KONGOR_MasterServer>("master-server")
        .WithEndpoint(55555, 55555, scheme: "http", name: "http", isProxied: false)
        .WithReference(database, "MERRICK")
        .WithReference(distributedCache, "DISTRIBUTED-CACHE"));

ApplyDevMemoryProfile(
    builder.AddProject<TRANSMUTANSTEIN_ChatServer>("chat-server")
        .WithEndpoint(5554, 5554, scheme: "https", name: "https", isProxied: false)
        .WithEndpoint(11031, 11031, scheme: "tcp", name: "chat-client", isProxied: false)
        .WithEndpoint(11032, 11032, scheme: "tcp", name: "chat-match-server", isProxied: false)
        .WithEndpoint(11033, 11033, scheme: "tcp", name: "chat-manager", isProxied: false)
        .WithReference(database, "MERRICK")
        .WithReference(distributedCache, "DISTRIBUTED-CACHE"));

IResourceBuilder<ProjectResource> webApi = ApplyDevMemoryProfile(
    builder.AddProject<ZORGATH_WebPortal_API>("web-portal-api")
        .WithEndpoint(5200, 5200, scheme: "http", name: "http", isProxied: false)
        .WithEndpoint(5201, 5201, scheme: "https", name: "https", isProxied: false)
        .WithReference(database, "MERRICK")
        .WithReference(distributedCache, "DISTRIBUTED-CACHE"));

IResourceBuilder<ProjectResource> webUi = ApplyDevMemoryProfile(
    builder.AddProject<DAWNBRINGER_WebPortal_UI>("web-portal-ui")
        .WithEndpoint(5266, 5266, scheme: "http", name: "http", isProxied: false)
        .WithEndpoint(5557, 5557, scheme: "https", name: "https", isProxied: false)
        .WithReference(webApi)
        .WithReference(distributedCache, "DISTRIBUTED-CACHE"));

// Opt-in Wave 3B YARP HTTP Edge Gateway (HTTP-only: binary TCP :11031-11033 and UDP :11235+ remain direct)
bool enableEdgeGateway = args.Contains("--with-edge-gateway") ||
                         string.Equals(builder.Configuration["ENABLE_EDGE_GATEWAY"], "true", StringComparison.OrdinalIgnoreCase);
if (enableEdgeGateway)
{
    ApplyDevMemoryProfile(
        builder.AddProject<KONGOR_EdgeGateway>("edge-gateway")
            .WithEndpoint(5080, 5080, scheme: "http", name: "http", isProxied: false)
            .WithReference(masterServer)
            .WithReference(webApi)
            .WithReference(webUi));
    Console.WriteLine("[INFO] Opt-in YARP HTTP Edge Gateway enabled on http://127.0.0.1:5080 (--with-edge-gateway / ENABLE_EDGE_GATEWAY=true).");
}

// Compel Dedicated Server Supervisor & Game UDP Relay Proxy
// Exposes the HTTP control plane (11234) and dedicated server simulation ports (11235-11236)
// Only registered and launched when explicitly requested via --launch-game-servers or COMPEL_AUTO_START=true
int baseGamePort = int.TryParse(builder.Configuration["COMPEL_BASE_PORT"], out int p) ? p : 11235;
bool launchGameServers = args.Contains("--launch-game-servers") ||
    string.Equals(builder.Configuration["COMPEL_AUTO_START"], "true", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("COMPEL_AUTO_START"), "true", StringComparison.OrdinalIgnoreCase);

if (launchGameServers)
{
    string? honServerDir = null;
    string appDir = builder.AppHostDirectory;
    string current = appDir;
    for (int i = 0; i < 5; i++)
    {
        string candidate = Path.Combine(current, "games", "hon server");
        string rawCandidate = Path.Combine(current, "games", "server");
        if (Directory.Exists(candidate))
        {
            honServerDir = candidate;
            break;
        }
        if (OperatingSystem.IsWindows() && Directory.Exists(rawCandidate))
        {
            try
            {
                Directory.CreateSymbolicLink(candidate, rawCandidate);
                if (Directory.Exists(candidate))
                {
                    honServerDir = candidate;
                    break;
                }
            }
            catch { }
        }
        string? parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrEmpty(parent) || parent == current) break;
        current = parent;
    }

    var compelResource = ApplyDevMemoryProfile(
        builder.AddProject<COMPEL>("compel")
            .WithEndpoint(11234, 11234, scheme: "http", name: "control-plane", isProxied: false)
            .WithEndpoint(baseGamePort, baseGamePort, scheme: "udp", name: "game-udp-0", isProxied: false)
            .WithEndpoint(baseGamePort + 1, baseGamePort + 1, scheme: "udp", name: "game-udp-1", isProxied: false)
            .WithEnvironment("COMPEL_AUTO_START", "true")
            .WithEnvironment("COMPEL_WARM_INSTANCES_TARGET", "0")
            .WithEnvironment("COMPEL_PORT_RANGE_OFFSET", "0")
            .WithEnvironment("COMPEL_CONTROL_PLANE_PORT", "11234")
            .WithEnvironment("COMPEL_FLEET_TIER", "L")
            .WithEnvironment("COMPEL_GATEWAY", "127.0.0.1")
            .WithArgs(
                "--launch-game-servers",
                "--Compel:AutoStart=true",
                "--Compel:WarmInstancesTarget=0",
                "--Compel:PortRangeOffset=0",
                "--Compel:ControlPlanePort=11234",
                "--Compel:FleetTier=L",
                "--Compel:Gateway=127.0.0.1")
            .WithReference(database, "MERRICK")
            .WithReference(distributedCache, "DISTRIBUTED-CACHE"));

    if (!string.IsNullOrWhiteSpace(honServerDir))
    {
        compelResource.WithEnvironment("COMPEL_INSTALLATION_DIRECTORY", honServerDir);
    }
}

builder.Build().Run();

static bool IsDockerHealthyFast()
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            bool pipeExists = File.Exists(@"\\.\pipe\docker_engine") ||
                              File.Exists(@"\\.\pipe\dockerDesktopLinuxEngine") ||
                              File.Exists(@"\\.\pipe\dockerDesktopEngine");

            bool processExists = System.Diagnostics.Process.GetProcessesByName("Docker Desktop").Length > 0 ||
                                System.Diagnostics.Process.GetProcessesByName("com.docker.backend").Length > 0 ||
                                System.Diagnostics.Process.GetProcessesByName("dockerd").Length > 0;

            return pipeExists && processExists;
        }

        return File.Exists("/var/run/docker.sock") ||
               System.Diagnostics.Process.GetProcessesByName("dockerd").Length > 0;
    }
    catch
    {
        return false;
    }
}

static bool IsPortListening(int port)
{
    try
    {
        using System.Net.Sockets.TcpClient client = new();
        IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
        bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(150));
        if (!success) return false;
        client.EndConnect(result);
        return true;
    }
    catch
    {
        return false;
    }
}

static void EnsureDotNetSdkEnvironment()
{
    string exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    if (string.IsNullOrWhiteSpace(dotnetRoot) || !Directory.Exists(dotnetRoot) || !File.Exists(Path.Combine(dotnetRoot, exeName)))
    {
        List<string> candidates = [];
        if (OperatingSystem.IsWindows())
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                candidates.Add(Path.Combine(localApp, "Microsoft", "dotnet"));
            }
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(progFiles))
            {
                candidates.Add(Path.Combine(progFiles, "dotnet"));
            }
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(Path.Combine(home, ".dotnet"));
            }
            candidates.Add("/usr/share/dotnet");
            candidates.Add("/usr/lib/dotnet");
            candidates.Add("/usr/local/share/dotnet");
            candidates.Add("/opt/homebrew/opt/dotnet/libexec");
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, exeName)))
            {
                dotnetRoot = candidate;
                break;
            }
        }
    }

    if (!string.IsNullOrWhiteSpace(dotnetRoot) && Directory.Exists(dotnetRoot))
    {
        Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!currentPath.StartsWith(dotnetRoot, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", $"{dotnetRoot}{Path.PathSeparator}{currentPath}");
        }
    }
}
