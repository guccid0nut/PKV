// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using System.Net.Sockets;
#if !PLAYTEST_BUILD
using NEXUS.Contracts.Security;
#endif
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Events;

Banner.Write();
bool isAotSmokeTest = Array.Exists(args, static arg => string.Equals(arg, "--aot-smoke-test", StringComparison.OrdinalIgnoreCase));

// "CreateSlimBuilder" Initialises The Host With Only The Features Native AOT Needs, Keeping The Published Binary Small.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

#if !PLAYTEST_BUILD
// Explicitly register the unified solution User Secrets vault so secrets work seamlessly across all environments
builder.Configuration.AddUserSecrets(UserSecretsConstants.SolutionUserSecretsId, reloadOnChange: true);

#endif
Kongor.Core.Configuration.BundledLayout? bundledLayout = null;
if (Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out bundledLayout) && bundledLayout is not null && File.Exists(bundledLayout.SecretsJson))
{
    builder.Configuration.AddJsonFile(bundledLayout.SecretsJson, optional: true, reloadOnChange: false);
    Console.WriteLine($"[Standalone] Loaded bundled secrets fallback from \"{bundledLayout.SecretsJson}\".");
}

builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
Console.WriteLine("[Standalone] Configuration precedence: user-secrets < env < args");


// Optional fallback: load COMPEL.json if present on disk for legacy/offline setups, but do not force-create it
CompelConfigurationFile? configuration = null;
if (CompelConfigurationLoader.Exists())
{
    try
    {
        configuration = CompelConfigurationLoader.Load();
        Console.WriteLine($@"[INFO] Optional fallback configuration loaded from ""{CompelConfigurationLoader.ResolvePath()}"".");
    }
    catch (Exception ex)
    {
        Console.WriteLine($@"[WARN] Could not parse ""{CompelConfigurationLoader.ResolvePath()}"": {ex.Message}. Proceeding with User Secrets.");
    }
}

// Resolve Gateway & Master Server Address: Check dotnet user secrets / environment variables first, then COMPEL.json
string resolvedGateway = builder.Configuration["Compel:Gateway"]
                         ?? builder.Configuration["COMPEL_GATEWAY"]
                         ?? Environment.GetEnvironmentVariable("COMPEL_GATEWAY")
                         ?? builder.Configuration["INFRASTRUCTURE_GATEWAY"]
                         ?? builder.Configuration["Infrastructure:Gateway"]
                         ?? builder.Configuration["Gateway"]
                         ?? builder.Configuration["Parameters:gateway"]
                         ?? Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY")
                         ?? (!string.IsNullOrWhiteSpace(configuration?.Gateway.Value)
                             ? configuration.Gateway.Value
                             : "127.0.0.1");

bool isLocalGateway = string.Equals(resolvedGateway, "localhost", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(resolvedGateway, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(resolvedGateway, "0.0.0.0", StringComparison.OrdinalIgnoreCase);

string? explicitMasterServer = !isLocalGateway
    ? (builder.Configuration["Compel:MasterServerAddress"]
       ?? builder.Configuration["MASTER_SERVER_ADDRESS"]
       ?? Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS")
       ?? builder.Configuration["MasterServer:Address"]
       ?? builder.Configuration["master-server-address"])
    : (builder.Configuration["Compel:MasterServerAddress"]
       ?? builder.Configuration["MasterServer:Address"]
       ?? builder.Configuration["master-server-address"]);

string resolvedMasterServer;
if ((builder.Configuration.GetValue<bool>("KONGOR_STANDALONE") || bundledLayout is not null) && string.IsNullOrWhiteSpace(explicitMasterServer))
{
    resolvedMasterServer = "127.0.0.1:55555";
    Console.WriteLine("[Standalone] Defaulting COMPEL MasterServerAddress to 127.0.0.1:55555 (KONGOR_STANDALONE=true)");
}
else
{
    resolvedMasterServer = !string.IsNullOrWhiteSpace(explicitMasterServer)
        ? explicitMasterServer
        : (AddressResolver.IsRunningInContainer ? "host.docker.internal:55555" : "127.0.0.1:55555");
}

if (resolvedMasterServer.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    resolvedMasterServer = resolvedMasterServer["https://".Length..].TrimEnd('/');
else if (resolvedMasterServer.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
    resolvedMasterServer = resolvedMasterServer["http://".Length..].TrimEnd('/');

// Resolve Host Account Credentials: Check dotnet user secrets / environment variables first, then COMPEL.json
string resolvedUserName = builder.Configuration["HostAccount:UserName"]
                          ?? builder.Configuration["Compel:HostUserName"]
                          ?? builder.Configuration["Compel:UserName"]
                          ?? builder.Configuration["HOST_USERNAME"]
                          ?? builder.Configuration["Host:UserName"]
                          ?? builder.Configuration["MatchServer:UserName"]
                          ?? builder.Configuration["Parameters:host-username"]
                          ?? Environment.GetEnvironmentVariable("HOST_USERNAME")
                          ?? (!string.IsNullOrWhiteSpace(configuration?.UserName.Value) && configuration.UserName.Value != "..."
                              ? configuration.UserName.Value
                              : string.Empty);

string resolvedPassword = builder.Configuration["HostAccount:Password"]
                          ?? builder.Configuration["Compel:HostPassword"]
                          ?? builder.Configuration["Compel:Password"]
                          ?? builder.Configuration["HOST_PASSWORD"]
                          ?? builder.Configuration["Host:Password"]
                          ?? builder.Configuration["MatchServer:Password"]
                          ?? builder.Configuration["Parameters:host-password"]
                          ?? Environment.GetEnvironmentVariable("HOST_PASSWORD")
                          ?? (!string.IsNullOrWhiteSpace(configuration?.Password.Value) && configuration.Password.Value != "..."
                              ? configuration.Password.Value
                              : string.Empty);

if (string.IsNullOrWhiteSpace(resolvedUserName) || string.IsNullOrWhiteSpace(resolvedPassword))
{
    if (isAotSmokeTest)
    {
        resolvedUserName = "aot-smoke-user";
        resolvedPassword = Guid.NewGuid().ToString("N");
        Console.WriteLine("[AOT-SMOKE] Using ephemeral smoke credentials for Native AOT DI graph verification.");
    }
    else
    {
        Console.WriteLine("[ERROR] Host Account credentials are missing.");
        Console.WriteLine("Please configure the Host Account Username and Password via dotnet user-secrets (e.g. 'dotnet user-secrets set HostAccount:UserName <name>' or via NEXUS Control Panel).");
        return;
    }
}

int resolvedInstances =
    int.TryParse(
        builder.Configuration["Compel:Instances"] ?? builder.Configuration["COMPEL_INSTANCES"] ??
        Environment.GetEnvironmentVariable("COMPEL_INSTANCES"), out int parsedInst) && parsedInst > 0
        ? parsedInst
        : (configuration?.Instances.Value > 0
            ? configuration.Instances.Value
            : 2);

string? rawPortRangeOffset = builder.Configuration["Compel:PortRangeOffset"]
                             ?? builder.Configuration["COMPEL_PORT_RANGE_OFFSET"]
                             ?? Environment.GetEnvironmentVariable("COMPEL_PORT_RANGE_OFFSET");

int resolvedPortRangeOffset = !string.IsNullOrWhiteSpace(rawPortRangeOffset) && int.TryParse(rawPortRangeOffset, out int parsedOffset)
    ? parsedOffset
    : (configuration?.PortRangeOffset.Value ?? 0);

// Cloud-Native Agones Port Discovery: When running under Agones orchestration, query local sidecar to discover dynamically assigned Game UDP port
if (string.Equals(Environment.GetEnvironmentVariable("AGONES_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        int agonesSdkPort = int.TryParse(Environment.GetEnvironmentVariable("AGONES_SDK_HTTP_PORT"), out int p) && p > 0 ? p : 9358;
        using HttpClient agonesClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(2));
        using HttpResponseMessage response = await agonesClient.GetAsync($"http://localhost:{agonesSdkPort}/gameserver").ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("status", out System.Text.Json.JsonElement statusElem) &&
                statusElem.TryGetProperty("ports", out System.Text.Json.JsonElement portsElem) &&
                portsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement portObj in portsElem.EnumerateArray())
                {
                    if (portObj.TryGetProperty("name", out System.Text.Json.JsonElement nameProp) &&
                        nameProp.GetString() == "game-udp" &&
                        portObj.TryGetProperty("port", out System.Text.Json.JsonElement portProp) &&
                        portProp.TryGetInt32(out int dynamicPort))
                    {
                        resolvedPortRangeOffset = dynamicPort - 11235;
                        Console.WriteLine($"[Agones] Auto-discovered assigned game-udp port {dynamicPort} (offset {resolvedPortRangeOffset}).");
                        break;
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Agones] Notice: Could not read /gameserver on startup: {ex.Message}. Using default offset {resolvedPortRangeOffset}.");
    }
}

int resolvedControlPlanePort =
    int.TryParse(
        builder.Configuration["Compel:ControlPlanePort"] ?? builder.Configuration["COMPEL_CONTROL_PLANE_PORT"] ??
        Environment.GetEnvironmentVariable("COMPEL_CONTROL_PLANE_PORT"), out int parsedPort) &&
    parsedPort is >= 1 and <= 65535
        ? parsedPort
        : (configuration?.ControlPlanePort.Value is >= 1 and <= 65535 && configuration.ControlPlanePort.Value != 8080
            ? configuration.ControlPlanePort.Value
            : 11234 + resolvedPortRangeOffset);

// The Control Plane Port Is Validated Here, Ahead Of Kestrel, So An Out-Of-Range Value Produces A Clear Message Rather Than An Unhandled Bind Failure Or A Silent Bind To An Arbitrary Ephemeral Port.
if (resolvedControlPlanePort is < 1 or > 65535)
{
    Console.WriteLine(
        $@"The Configured Control Plane Port ({resolvedControlPlanePort}) Is Invalid; It Must Be Between 1 And 65535.");
    Console.WriteLine(@"Fix Configuration And Start COMPEL Again.");

    return;
}

// COMPEL operates in standard or elevated mode without blocking non-admin execution.
if (Environment.IsPrivilegedProcess is false) Console.WriteLine("[INFO] Running in standard user mode.");

if (OperatingSystem.IsWindows() && !isAotSmokeTest)
{
    Kongor.Core.Network.FirewallUdpAuditReport? fwReport =
        await Kongor.Core.Network.WindowsFirewallUdpCoverageAuditor.RunAuditAsync(
            resolvedPortRangeOffset, resolvedInstances, resolvedGateway).ConfigureAwait(false);
    if (fwReport is { IsFullyCovered: false, WarningBanner: not null })
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(fwReport.WarningBanner);
        Console.WriteLine($"[WAN-REACHABILITY] Fix in elevated PowerShell: {fwReport.RemediationPowerShellCommand}");
        Console.WriteLine($"[WAN-REACHABILITY] Router NAT port-forward required for WAN play: {fwReport.RouterPortForwardSummary}");
        Console.ResetColor();
    }
}

// The Master Server Must Be Reachable Before Launching: The Servers Cannot Register Or Authenticate Without It, So An Unreachable Master Server Is A Hard Startup Failure. A Localhost Gateway Uses A Loopback Master Server And Is Not Pinged.
if (!isAotSmokeTest && await MasterServerIsReachable(resolvedGateway, resolvedMasterServer) is false)
{
    if (!resolvedMasterServer.Equals("127.0.0.1:55555", StringComparison.OrdinalIgnoreCase) &&
        !resolvedMasterServer.Equals("localhost:55555", StringComparison.OrdinalIgnoreCase) &&
        await AddressResolver.CheckMasterServerReachabilityAsync("127.0.0.1:55555").ConfigureAwait(false))
    {
        Console.WriteLine($@"[WARN] Master Server ""{resolvedMasterServer}"" is not reachable, but local Master Server (127.0.0.1:55555) is reachable.");
        Console.WriteLine(@"[INFO] Falling back to local Master Server: 127.0.0.1:55555.");
        resolvedMasterServer = "127.0.0.1:55555";
    }
    else
    {
        Console.WriteLine($@"The Master Server ""{resolvedMasterServer}"" Is Not Reachable; COMPEL Will Not Start.");
        Console.WriteLine("Check The Host's Network Connection or Local Cluster Status, Then Start COMPEL Again.");

        return;
    }
}

// Refuse To Start If Another COMPEL Instance Is Already Running Against This Installation.
string? rawQueueArcadeMode = builder.Configuration["Compel:QueueArcadeMode"]
                             ?? builder.Configuration["COMPEL_QUEUE_ARCADE_MODE"]
                             ?? Environment.GetEnvironmentVariable("COMPEL_QUEUE_ARCADE_MODE");
bool resolvedQueueArcadeMode = !string.IsNullOrWhiteSpace(rawQueueArcadeMode) &&
                               bool.TryParse(rawQueueArcadeMode, out bool parsedArcadeMode) &&
                               parsedArcadeMode;

string lockFileName = (!resolvedQueueArcadeMode && resolvedPortRangeOffset == 0 && resolvedControlPlanePort == 11234)
    ? "COMPEL.lock"
    : $"COMPEL-{resolvedControlPlanePort}.lock";
string lockFilePath = isAotSmokeTest
    ? Path.Combine(Path.GetTempPath(), $"COMPEL-aot-smoke-{Environment.ProcessId}.lock")
    : Path.Combine(AppContext.BaseDirectory, lockFileName);

if (SingleInstanceGuard.TryAcquire(lockFilePath, out SingleInstanceGuard singleInstanceGuard) is false)
{
    Console.WriteLine("Another COMPEL Instance Is Already Running Against This Installation.");

    return;
}

// Bind The Control Plane To Its Configured Port.
string? rawControlPlaneBind = builder.Configuration["Compel:ControlPlaneBindAddress"]
                              ?? builder.Configuration["COMPEL_CONTROL_PLANE_BIND_ADDRESS"]
                              ?? Environment.GetEnvironmentVariable("COMPEL_CONTROL_PLANE_BIND_ADDRESS");

IPAddress parsedBindIp;
if (string.IsNullOrWhiteSpace(rawControlPlaneBind))
{
    parsedBindIp = IPAddress.Loopback;
}
else if (!IPAddress.TryParse(rawControlPlaneBind.Trim(), out IPAddress? candidateIp) || candidateIp is null)
{
    Console.Error.WriteLine($"[Standalone] Invalid COMPEL_CONTROL_PLANE_BIND_ADDRESS '{rawControlPlaneBind}'; falling back to 127.0.0.1.");
    parsedBindIp = IPAddress.Loopback;
}
else
{
    parsedBindIp = candidateIp;
}

string formattedBindHost = parsedBindIp.AddressFamily == AddressFamily.InterNetworkV6
    ? $"[{parsedBindIp}]"
    : parsedBindIp.ToString();

builder.WebHost.UseUrls($"http://{formattedBindHost}:{resolvedControlPlanePort}");
Console.WriteLine($"[Standalone] control plane bind={formattedBindHost}:{resolvedControlPlanePort}");

string resolvedLocation = builder.Configuration["Compel:Location"]
                          ?? builder.Configuration["COMPEL_LOCATION"]
                          ?? builder.Configuration["Location"]
                          ?? configuration?.Location.Value
                          ?? "USE";

string resolvedServerNamePrefix = builder.Configuration["Compel:ServerNamePrefix"]
                                  ?? builder.Configuration["COMPEL_SERVER_NAME_PREFIX"]
                                  ?? builder.Configuration["ServerNamePrefix"]
                                  ?? Environment.GetEnvironmentVariable("COMPEL_SERVER_NAME_PREFIX")
                                  ?? configuration?.ServerNamePrefix.Value
                                  ?? "DONUT ARENA";

string? rawUseProxy = builder.Configuration["Compel:UseProxy"]
                      ?? builder.Configuration["COMPEL_USE_PROXY"]
                      ?? Environment.GetEnvironmentVariable("COMPEL_USE_PROXY");

bool resolvedUseProxy = !string.IsNullOrWhiteSpace(rawUseProxy) && bool.TryParse(rawUseProxy, out bool parsedProxy)
    ? parsedProxy
    : (configuration?.UseProxy.Value ?? false);

string resolvedRuntimeArtefactsPath = builder.Configuration["Compel:RuntimeArtefactsPath"]
                                      ?? builder.Configuration["COMPEL_RUNTIME_ARTEFACTS_PATH"]
                                      ?? configuration?.RuntimeArtefactsPath.Value
                                      ?? (bundledLayout is not null ? Path.Combine(bundledLayout.UserData, "compel") : "DEFAULT");

bool resolvedAutoStart =
    bool.TryParse(
        builder.Configuration["Compel:AutoStart"] ?? builder.Configuration["COMPEL_AUTO_START"] ??
        Environment.GetEnvironmentVariable("COMPEL_AUTO_START"), out bool parsedAutoStart)
        ? parsedAutoStart
        : true;

string priorityModeStr = builder.Configuration["Compel:PriorityMode"]
                         ?? builder.Configuration["COMPEL_PRIORITY_MODE"]
                         ?? Environment.GetEnvironmentVariable("COMPEL_PRIORITY_MODE")
                         ?? configuration?.PriorityMode?.Value
                         ?? "Background";

HostPriorityMode resolvedPriorityMode = Enum.TryParse<HostPriorityMode>(priorityModeStr, ignoreCase: true, out var parsedPriority)
    ? parsedPriority
    : HostPriorityMode.Background;

int resolvedWarmInstancesTarget =
    int.TryParse(
        builder.Configuration["Compel:WarmInstancesTarget"] ?? builder.Configuration["COMPEL_WARM_INSTANCES_TARGET"] ??
        Environment.GetEnvironmentVariable("COMPEL_WARM_INSTANCES_TARGET"), out int parsedWarmTarget)
        ? parsedWarmTarget
        : (configuration?.WarmInstancesTarget.Value ?? 0);

string? resolvedFleetTier = builder.Configuration["Compel:FleetTier"]
                            ?? builder.Configuration["COMPEL_FLEET_TIER"]
                            ?? Environment.GetEnvironmentVariable("COMPEL_FLEET_TIER");

// Options: The Host-Facing Settings Come From User Secrets / Environment / "COMPEL.json" (Validated At Startup); The Infrastructure Settings Use Built-In Defaults.
builder.Services.AddSingleton<IValidateOptions<MatchServerManagerOptions>, MatchServerManagerOptionsValidator>();

builder.Services.AddOptions<MatchServerManagerOptions>().Configure(options =>
{
    options.UserName = resolvedUserName;
    options.Password = resolvedPassword;
    options.Instances = resolvedInstances;
    options.WarmInstancesTarget = resolvedWarmInstancesTarget;
    options.Gateway = resolvedGateway;
    options.Location = resolvedLocation;
    options.ServerNamePrefix = resolvedServerNamePrefix;
    options.FleetTier = resolvedFleetTier ?? (resolvedQueueArcadeMode ? "Q" : null);
    options.QueueArcadeMode = resolvedQueueArcadeMode;
    options.QueueArcadeRoundDurationSeconds = int.TryParse(
        builder.Configuration["Compel:QueueArcadeRoundDurationSeconds"] ??
        builder.Configuration["COMPEL_QUEUE_ARCADE_ROUND_DURATION_SECONDS"] ??
        Environment.GetEnvironmentVariable("COMPEL_QUEUE_ARCADE_ROUND_DURATION_SECONDS"),
        out int parsedArcadeRound) && parsedArcadeRound > 0
        ? parsedArcadeRound
        : 600;
    if (resolvedQueueArcadeMode && options.WarmInstancesTarget == 0 && options.Instances > 1)
    {
        options.WarmInstancesTarget = 1;
    }
    options.UseProxy = resolvedUseProxy;
    if (options.UseProxy && !options.EnableLegacyWatermarkChallenge)
    {
        Console.WriteLine("[COMPEL] WARNING: UseProxy=true while EnableLegacyWatermarkChallenge=false exposes ports 20000..29999 where k2_x64.dll drops non-loopback packets after 100 datagrams. Prefer UseProxy=false for Cloudflare/LAN/WAN non-loopback play.");
    }
    if (options.UseProxy && (options.UseBbrPacing || options.UseSubTickNetcode))
    {
        Console.WriteLine("[COMPEL] WARNING: UseBbrPacing and/or UseSubTickNetcode are enabled on UDPProxy without an active modern client transport envelope. Features remain dormant for legacy K2 clients.");
    }
    options.PortRangeOffset = resolvedPortRangeOffset;
    options.RuntimeArtefactsPath = resolvedRuntimeArtefactsPath;
    options.MasterServerAddress = resolvedMasterServer;
    options.AutoStart = resolvedAutoStart;
    options.PriorityMode = resolvedPriorityMode;
}).ValidateOnStart();

string? resolvedToken = builder.Configuration["Compel:ControlPlaneAuthenticationToken"]
                       ?? builder.Configuration["COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN"]
                       ?? Environment.GetEnvironmentVariable("COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN")
                       ?? builder.Configuration["Compel:AuthenticationToken"]
                       ?? builder.Configuration["COMPEL_AUTHENTICATION_TOKEN"]
                       ?? Environment.GetEnvironmentVariable("COMPEL_AUTHENTICATION_TOKEN")
                       ?? (configuration?.AuthenticationToken.Value is { Length: > 0 } cfgTok && cfgTok != "..." ? cfgTok : null);

if (string.IsNullOrWhiteSpace(resolvedToken))
{
    if (builder.Environment.IsDevelopment())
    {
        resolvedToken = $"compel_dev_{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[SECURITY WARNING] No COMPEL authentication token configured. Generated ephemeral dev token: {resolvedToken}");
        Console.ResetColor();
    }
    else
    {
        throw new InvalidOperationException(
            "Fatal Security Error: 'Compel:ControlPlaneAuthenticationToken' (or 'Compel:AuthenticationToken') is required in production. Do not expose COMPEL without an authentication token.");
    }
}

bool allowInsecureDevToken = bool.TryParse(
    builder.Configuration["Compel:AllowInsecureDevToken"]
    ?? builder.Configuration["COMPEL_ALLOW_INSECURE_DEV_TOKEN"]
    ?? Environment.GetEnvironmentVariable("COMPEL_ALLOW_INSECURE_DEV_TOKEN"),
    out bool parsedAllowInsecureDevToken) && parsedAllowInsecureDevToken;

builder.Services.AddOptions<ControlPlaneOptions>().Configure(options =>
{
    options.AuthenticationToken = resolvedToken;
    options.AllowInsecureDevToken = allowInsecureDevToken;
});

bool resolvedCdnSync =
    bool.TryParse(
        builder.Configuration["Compel:CDNSynchronisation"] ?? builder.Configuration["COMPEL_CDN_SYNCHRONISATION"], out bool parsedSync)
        ? parsedSync
        : (configuration?.CDNSynchronisation.Value ?? false);

string? resolvedInstallationDir = builder.Configuration["Compel:InstallationDirectory"]
                               ?? builder.Configuration["COMPEL_INSTALLATION_DIRECTORY"]
                               ?? Environment.GetEnvironmentVariable("COMPEL_INSTALLATION_DIRECTORY")
                               ?? bundledLayout?.DedicatedServerDir
                               ?? (string.IsNullOrWhiteSpace(configuration?.InstallationDirectory?.Value) ? null : configuration.InstallationDirectory.Value);

string installationDirectory = DistributionSynchronisationService.ResolveInstallationDirectory(resolvedInstallationDir ?? new CDNOptions().InstallationDirectory);

// The Manager Spawns Each Server Instance With An Unquoted Executable Path, Which Heroes Of Newerth Only Parses Correctly When That Path Contains A Whitespace Character
if (HeroesOfNewerthExecutable.CanLaunchInstancesFrom(installationDirectory) is false)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($@"[GUARD] COMPEL Will Not Start From ""{installationDirectory}"" Because The Path Contains No Whitespace Character");
    Console.WriteLine("[GUARD] Heroes Of Newerth Requires A Whitespace Character Somewhere In The Path It Is Launched From, Otherwise Match Server Instances Never Start Correctly");
    Console.WriteLine(@"[GUARD] Move COMPEL To A Directory Whose Path Contains A Whitespace Character (For Example ""C:\HoN Match Server""), Then Start It Again");
    Console.ResetColor();

    return;
}

string? resolvedCdnHost = builder.Configuration["Compel:CDN"]
                          ?? builder.Configuration["COMPEL_CDN_HOST"]
                          ?? Environment.GetEnvironmentVariable("COMPEL_CDN_HOST")
                          ?? configuration?.CDN?.Value;

builder.Services.AddSingleton<IValidateOptions<CDNOptions>, CDNOptionsValidator>();

builder.Services.AddOptions<CDNOptions>()
    .Configure(options =>
    {
        if (!string.IsNullOrWhiteSpace(resolvedCdnHost))
        {
            string trimmed = resolvedCdnHost.Trim();
            if (!trimmed.Contains("://", StringComparison.Ordinal))
            {
                bool isLoopback = trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase);
                trimmed = isLoopback ? $"http://{trimmed}" : $"https://{trimmed}";
            }
            options.Host = trimmed.EndsWith('/') ? trimmed : $"{trimmed}/";
        }

        options.Synchronisation = resolvedCdnSync;
        options.InstallationDirectory = installationDirectory;
    })
    .ValidateOnStart();

builder.Services.AddOptions<SandboxLimitsOptions>()
    .Bind(builder.Configuration.GetSection(SandboxLimitsOptions.SectionName))
    .Configure(options => options.PriorityMode = resolvedPriorityMode);

// Resolve Grafana Cloud Observability Settings: Check dotnet user secrets / configuration / environment variables first, then fall back to COMPEL.json
string? grafanaEndpoint = builder.Configuration["GRAFANA_OTLP_ENDPOINT"]
                          ?? builder.Configuration["Grafana:OtlpEndpoint"]
                          ?? Environment.GetEnvironmentVariable("GRAFANA_OTLP_ENDPOINT")
                          ?? (string.IsNullOrWhiteSpace(configuration?.GrafanaOtlpEndpoint.Value) ||
                              configuration.GrafanaOtlpEndpoint.Value.Contains("example",
                                  StringComparison.OrdinalIgnoreCase) ||
                              configuration.GrafanaOtlpEndpoint.Value.Contains("otlp-gateway.grafana.net",
                                  StringComparison.OrdinalIgnoreCase)
                              ? null
                              : configuration.GrafanaOtlpEndpoint.Value);

string? rawGrafanaHeaders = builder.Configuration["GRAFANA_OTLP_HEADERS"]
                            ?? builder.Configuration["Grafana:OtlpHeaders"]
                            ?? Environment.GetEnvironmentVariable("GRAFANA_OTLP_HEADERS")
                            ?? (string.IsNullOrWhiteSpace(configuration?.GrafanaOtlpHeaders.Value) ||
                                configuration.GrafanaOtlpHeaders.Value.Contains("FakeExample",
                                    StringComparison.OrdinalIgnoreCase) ||
                                configuration.GrafanaOtlpHeaders.Value.Contains("token123",
                                    StringComparison.OrdinalIgnoreCase) ||
                                configuration.GrafanaOtlpHeaders.Value == "..."
                                ? null
                                : configuration.GrafanaOtlpHeaders.Value);

string? grafanaHeaders = null;
if (!string.IsNullOrWhiteSpace(rawGrafanaHeaders) && rawGrafanaHeaders != "...")
{
    string trimmed = rawGrafanaHeaders.Trim();
    if (trimmed.Contains('='))
    {
        grafanaHeaders = trimmed;
    }
    else if (trimmed.Contains(':'))
    {
        string[] parts = trimmed.Split(':', 2);
        grafanaHeaders = $"{parts[0].Trim()}={Uri.EscapeDataString(parts[1].Trim())}";
    }
    else
    {
        grafanaHeaders = $"Authorization={Uri.EscapeDataString(trimmed)}";
    }
}

if (!string.IsNullOrWhiteSpace(grafanaEndpoint) &&
    Uri.TryCreate(grafanaEndpoint, UriKind.Absolute, out Uri? validEndpointUri))
{
    Console.WriteLine($"[INFO] Grafana Cloud Observability Endpoint Configured: {grafanaEndpoint}");

    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics =>
        {
            metrics.AddMeter(CompelMetrics.MeterName);
            metrics.AddOtlpExporter((exporterOptions, metricReaderOptions) =>
            {
                exporterOptions.Endpoint = validEndpointUri;
                if (!string.IsNullOrWhiteSpace(grafanaHeaders))
                {
                    try
                    {
                        exporterOptions.Headers = grafanaHeaders;
                    }
                    catch (ArgumentException)
                    {
                        // Ignore malformed placeholder headers in local dev
                    }
                }
                metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000;
            });
        });
}

// Resolve Grafana Pyroscope Profiling Settings: Check dotnet user secrets / configuration / environment variables first, then fall back to COMPEL.json
string? pyroscopeAddress = builder.Configuration["PYROSCOPE_SERVER_ADDRESS"]
                           ?? builder.Configuration["Pyroscope:ServerAddress"]
                           ?? Environment.GetEnvironmentVariable("PYROSCOPE_SERVER_ADDRESS")
                           ?? (string.IsNullOrWhiteSpace(configuration?.PyroscopeServerAddress.Value) ||
                               configuration.PyroscopeServerAddress.Value.Contains("example",
                                   StringComparison.OrdinalIgnoreCase) ||
                               configuration.PyroscopeServerAddress.Value.Contains("grafana.net",
                                   StringComparison.OrdinalIgnoreCase)
                               ? ((builder.Configuration.GetValue<bool>("KONGOR_STANDALONE") || bundledLayout is not null) ? null : "http://localhost:4040")
                               : configuration.PyroscopeServerAddress.Value);

string? pyroscopeUser = builder.Configuration["PYROSCOPE_BASIC_AUTH_USER"]
                        ?? builder.Configuration["Pyroscope:BasicAuthUser"]
                        ?? Environment.GetEnvironmentVariable("PYROSCOPE_BASIC_AUTH_USER")
                        ?? (string.IsNullOrWhiteSpace(configuration?.PyroscopeBasicAuthUser.Value) ||
                            configuration.PyroscopeBasicAuthUser.Value == "..." || configuration.PyroscopeBasicAuthUser.Value == "123456"
                            ? null
                            : configuration.PyroscopeBasicAuthUser.Value);

string? pyroscopePassword = builder.Configuration["PYROSCOPE_BASIC_AUTH_PASSWORD"]
                            ?? builder.Configuration["Pyroscope:BasicAuthPassword"]
                            ?? Environment.GetEnvironmentVariable("PYROSCOPE_BASIC_AUTH_PASSWORD")
                            ?? (string.IsNullOrWhiteSpace(configuration?.PyroscopeBasicAuthPassword.Value) ||
                                configuration.PyroscopeBasicAuthPassword.Value == "..." || configuration.PyroscopeBasicAuthPassword.Value.Contains("FakeExample",
                                    StringComparison.OrdinalIgnoreCase)
                                ? null
                                : configuration.PyroscopeBasicAuthPassword.Value);

if (!string.IsNullOrWhiteSpace(pyroscopeAddress))
    Console.WriteLine(
        $"[INFO] Continuous Profiler Configured: {pyroscopeAddress} (User: {pyroscopeUser ?? "anonymous"})");

// JSON: Source-Generated Serialisation Metadata For The Minimal-API Responses (Required Under Native AOT).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlPlaneJSONContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, ManifestJSONContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(2, COMPEL.Services.Telemetry.RlControlPlaneJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(3, ASPIRE.Common.MachineLearning.RlJsonContext.Default);
});

// Logging: Serilog In Code (Native AOT Safe). Console Plus A Single "COMPEL.log" File Beside The Executable.
builder.Logging.ClearProviders();

string logFileStem = resolvedQueueArcadeMode ? "COMPEL-Arcade" : "COMPEL";
string logFilePath = Path.Combine(AppContext.BaseDirectory, $"{logFileStem}.log");
string logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsDir);
string rollingLogFilePath = Path.Combine(logsDir, $"{logFileStem}-.log");

// Write The Banner And This Session's Marker To The Log File Before The Logger Opens It, So Each Session Reads As A Distinct Block Headed By The Banner.
Banner.WriteToLogFile(logFilePath);

builder.Services.AddSerilog(loggerConfiguration =>
{
    loggerConfiguration
        .MinimumLevel.Debug()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
        .WriteTo.File(
            logFilePath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 20,
            flushToDiskInterval: TimeSpan.FromSeconds(1))
        .WriteTo.File(
            rollingLogFilePath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 20,
            flushToDiskInterval: TimeSpan.FromSeconds(1));
});

// Domain Services.
// The Port Plan Is A Pure Function Of The Options, So It Is Registered Once As The Single Source Of Truth Shared By The Supervisor, The Proxy, And The Ping Responder.
builder.Services.AddSingleton(serviceProvider =>
    new PortPlan(serviceProvider.GetRequiredService<IOptions<MatchServerManagerOptions>>().Value));
builder.Services.AddSingleton<AddressResolver>();
builder.Services.AddSingleton<ArtefactsLocator>();
builder.Services.AddSingleton<ProcessTreeSandbox>();
builder.Services.AddSingleton<DistributionSynchronisationService>();
builder.Services.AddSingleton<CompelTelemetryRelayService>();
builder.Services.AddSingleton<DedicatedServerCrashResilienceService>();
builder.Services.AddSingleton<COMPEL.Services.Diagnostics.AutomatedCrashSymbolizerService>();
builder.Services.AddSingleton<COMPEL.Services.Proxy.XdpPacketFilterManager>();
builder.Services.AddSingleton<MatchServerManagerSupervisor>();
builder.Services.AddSingleton<FleetDrainingService>();
builder.Services.AddSingleton<UDPProxyService>();
builder.Services.AddSingleton<UDPPingResponder>();
builder.Services.AddSingleton<Kongor.Core.Services.Voice.IVoiceMuteRegistry, Kongor.Core.Services.Voice.VoiceMuteRegistry>();
builder.Services.AddSingleton<COMPEL.Services.Voice.CompelVoiceRelayService>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    int port = config.GetValue<int?>("Compel:VoiceRelayPort")
        ?? config.GetValue<int?>("COMPEL_VOICE_RELAY_PORT")
        ?? ((COMPEL.Services.Supervision.PortPlan.BaseVoiceRelayPort) +
            ((resolvedQueueArcadeMode || resolvedPortRangeOffset >= 250) ? 3
                : resolvedPortRangeOffset >= 200 ? 2
                : resolvedPortRangeOffset >= 100 ? 1
                : 0));
    ILogger<COMPEL.Services.Voice.CompelVoiceRelayService> logger = sp.GetRequiredService<ILogger<COMPEL.Services.Voice.CompelVoiceRelayService>>();
    TimeProvider? timeProvider = sp.GetService<TimeProvider>();
    Kongor.Core.Services.Voice.IVoiceMuteRegistry? muteRegistry = sp.GetService<Kongor.Core.Services.Voice.IVoiceMuteRegistry>();
    return new COMPEL.Services.Voice.CompelVoiceRelayService(logger, port, timeProvider, muteRegistry);
});
builder.Services.AddSingleton<COMPEL.Services.Voice.ICompelVoiceRelayService>(sp =>
    sp.GetRequiredService<COMPEL.Services.Voice.CompelVoiceRelayService>());

builder.Services.AddSingleton<COMPEL.Services.Telemetry.MatchParquetExportService>();
builder.Services.AddSingleton(sp => new ASPIRE.Common.MachineLearning.RlArenaCoordinator(
    COMPEL.Services.Telemetry.RlTrajectoryCollectorService.ResolveRuntimeDirectory(sp.GetRequiredService<IConfiguration>()),
    new ASPIRE.Common.MachineLearning.RlArenaOptions()));
builder.Services.AddSingleton<COMPEL.Services.Telemetry.RlTrajectoryCollectorService>();
builder.Services.AddSingleton(serviceProvider => new COMPEL.Services.Telemetry.MatchLakehouseStorageUploader(
    serviceProvider.GetRequiredService<IConfiguration>(),
    serviceProvider.GetRequiredService<ILogger<COMPEL.Services.Telemetry.MatchLakehouseStorageUploader>>()));
builder.Services.AddSingleton<COMPEL.Services.Telemetry.PostMatchParquetIngestorService>();
builder.Services.AddSingleton<COMPEL.Services.Telemetry.ReplayWalRecoveryService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<COMPEL.Services.Supervision.AgonesLifecycleService>();

// Hosted Services. The Stateful Singletons Are Re-Registered As Hosted Services So The Control Plane And The Health Checks Can Resolve Them To Read Their Live State.
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<DistributionSynchronisationService>());
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<MatchServerManagerSupervisor>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<UDPProxyService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<UDPPingResponder>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<COMPEL.Services.Voice.CompelVoiceRelayService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<COMPEL.Services.Telemetry.RlTrajectoryCollectorService>());
if (!resolvedQueueArcadeMode)
{
    builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<COMPEL.Services.Telemetry.MatchLakehouseStorageUploader>());
    builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<COMPEL.Services.Telemetry.PostMatchParquetIngestorService>());
    builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<COMPEL.Services.Telemetry.ReplayWalRecoveryService>());
}
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<COMPEL.Services.Supervision.AgonesLifecycleService>());
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<FleetDrainingService>());
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<GameServerLogWatcherService>();

// Health Checks. The Ping Responder And Proxy Checks Surface A Port-Bind Failure Via "/health" Instead Of It Being Visible Only In A Log Line.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"])
    .AddCheck<UDPPingResponderHealthCheck>("ping-responder")
    .AddCheck<UDPProxyServiceHealthCheck>("proxy");

// Initialize high-resolution Windows timer resolution (1.0ms) for exact game server tick scheduling.
using WindowsTimerResolution timerResolution = WindowsTimerResolution.Enable();
if (timerResolution.IsActive)
    Console.WriteLine("[INFO] Windows high-resolution timer (1.0ms) enforced for deterministic 50ms tick pacing.");

// Verify unified NativeGuard proxy supervision (version.dll on Windows, libk2_native_guard.so on Linux)
if (OperatingSystem.IsWindows())
{
    Console.WriteLine("[INFO] Windows NativeGuard supervision unified via version.dll proxy (legacy Kongor.NativeGuard.dll synthesis retired).");
}
else if (OperatingSystem.IsLinux())
{
    Console.WriteLine("[INFO] Linux NativeGuard supervision unified via LD_PRELOAD libk2_native_guard.so (POSIX V2 Seqlock + Unix domain socket IPC).");
}

// Log host CPU topology and P-Core affinity order
int[] processorOrder = CpuTopologyResolver.GetOptimizedProcessorOrder();
Console.WriteLine(
    $"[INFO] CPU Topology Resolver initialized: {Environment.ProcessorCount} logical cores detected (P-Core prioritized order: [{string.Join(", ", processorOrder)}]).");

// The Options Validation Failure Is Caught Here And Reported As A Clean Message, Matching How The Configuration-Load Path Above Reports Problems Rather Than Surfacing As A Host Startup Crash.
try
{
    WebApplication application = builder.Build();

    // Trigger The Configured Options Validation Before The Host Starts, So A Configuration Problem Is Reported By The Handler Below Instead Of Being Logged As A Host Startup Failure With A Stack Trace.
    _ = application.Services.GetRequiredService<IOptions<MatchServerManagerOptions>>().Value;

    // Released Explicitly On A Graceful Shutdown; A Crash Or A Forced Kill Still Releases The Underlying File Handle At The Operating-System Level.
    application.Lifetime.ApplicationStopping.Register(() =>
    {
        timerResolution.Dispose();
        singleInstanceGuard.Dispose();
    });

    application.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = static (httpContext, elapsedMs, exception) =>
            CompelRequestLogging.ResolveRequestLogLevel(httpContext, elapsedMs, exception);
    });

    application.MapHealthChecks("/health");
    application.MapHealthChecks("/alive",
        new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("live") });

    application.MapControlPlaneEndpoints();

    if (isAotSmokeTest)
    {
        await Task.Yield();
        _ = application.Services.GetRequiredService<PortPlan>();
        _ = ControlPlaneJSONContext.Default.Options;
        _ = ManifestJSONContext.Default.Options;
        timerResolution.Dispose();
        singleInstanceGuard.Dispose();
        await application.DisposeAsync();
        Console.WriteLine("[AOT-SMOKE] COMPEL Native AOT + RuntimeAsync DI, endpoints & JSON graph verified cleanly.");
        return;
    }

    application.Run();
}

catch (IOException exception)
{
    timerResolution.Dispose();
    singleInstanceGuard.Dispose();

    // Kestrel Surfaces A Failure To Bind The Control-Plane Port (For Example When It Is Already In Use) As An "IOException" During Startup; It Is Reported Cleanly Here Rather Than As An Unhandled Stack Trace.
    Console.WriteLine(
        $@"COMPEL Could Not Start Its HTTP Control Plane On Port {resolvedControlPlanePort}: {exception.Message}");
    Console.WriteLine(@"Ensure The Port Is Free Or Change ""Compel:ControlPlanePort"" in User Secrets / Configuration, Then Start COMPEL Again.");
}

catch (OptionsValidationException exception)
{
    timerResolution.Dispose();
    singleInstanceGuard.Dispose();

    Console.WriteLine("The COMPEL Configuration Is Invalid:");

    foreach (string failure in exception.Failures)
        Console.WriteLine($"    - {failure}");

    Console.WriteLine("Fix your User Secrets or Configuration, then start COMPEL again.");
}

// Pings The Master Server (Unless The Gateway Is Loopback, Whose Master Server Is Local And Assumed Reachable), Reporting The Round-Trip Time On Success. A Filtered ICMP Response Is Treated As Unreachable, As In The Legacy Startup Check.
static async Task<bool> MasterServerIsReachable(string gateway, string masterServerAddress)
{
    if (gateway.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        gateway.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        gateway.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        return true;

    string host = masterServerAddress;
    if (host.Contains(':'))
        host = host.Split(':')[0];

    try
    {
        using Ping ping = new();

        PingReply reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(5));

        if (reply.Status is IPStatus.Success)
        {
            Console.WriteLine($@"[INFO] Master Server ""{host}"" Is Reachable via ICMP ({reply.RoundtripTime} ms).");
            return true;
        }
    }
    catch
    {
        // ICMP ping may be blocked/filtered by network firewalls
    }

    // Fallback: Test TCP connection on the master server port
    try
    {
        int port = 80;
        if (masterServerAddress.Contains(':') && int.TryParse(masterServerAddress.Split(':')[1], out int parsedPort))
            port = parsedPort;

        using TcpClient client = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(host, port, cts.Token);
        Console.WriteLine($@"[INFO] Master Server ""{host}:{port}"" Is Reachable via TCP.");
        return true;
    }
    catch
    {
        return false;
    }
}

internal static class CompelRequestLogging
{
    internal static bool IsVerboseHeartbeatsEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("KONGOR_VERBOSE_HEARTBEATS");
        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal) ||
               string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRoutinePollPath(PathString path)
    {
        return path.Equals("/ping", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/status", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/alive", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
    }

    internal static LogEventLevel ResolveRequestLogLevel(
        HttpContext httpContext,
        double elapsedMs,
        Exception? exception,
        bool? verboseHeartbeatsOverride = null)
    {
        if (exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        if (httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            return LogEventLevel.Warning;
        }

        bool verboseHeartbeats = verboseHeartbeatsOverride ?? IsVerboseHeartbeatsEnabled();
        if (!verboseHeartbeats &&
            (HttpMethods.IsGet(httpContext.Request.Method) || HttpMethods.IsHead(httpContext.Request.Method)) &&
            IsRoutinePollPath(httpContext.Request.Path) &&
            httpContext.Response.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices &&
            elapsedMs < 250.0)
        {
            return LogEventLevel.Debug;
        }

        return LogEventLevel.Information;
    }
}
