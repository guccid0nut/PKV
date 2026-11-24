// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet.Models;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public enum ContainerPlatform
{
    Docker,
    Minikube,
    Kubernetes,
    K3sAgones
}

public static class ContainerLaunchCommandBuilder
{
    public const string DefaultDockerImage = "projectkongor/compel-dedicated:latest";
    public const string RankedContainerName = "ranked-compel-server";
    public const string QueueArcadeContainerName = "arcade-compel-server";
    public const string LegacyRankedContainerName = "compel-dedicated-server";
    public const string LegacyQueueArcadeContainerName = "compel-queue-arcade-server";
    public const string RankedRuntimeVolumeName = "kongor-ranked-runtime";
    public const string QueueArcadeRuntimeVolumeName = "kongor-arcade-runtime";

    /// <summary>
    /// Isolates default local host accounts ("guccihost", "guccihost_local") to their dedicated Docker/K3s
    /// ServerHost accounts ("guccihost_dock", "guccihost_k3s") so all 3 fleets can run concurrently without
    /// invalidating each other's MasterServer auth session, while preserving custom operator usernames.
    /// </summary>
    public static string ResolveFleetHostUsername(string? rawUsername, ContainerPlatform platform, string fallback = "YOUR_KONGOR_USERNAME")
    {
        if (string.IsNullOrWhiteSpace(rawUsername))
        {
            return fallback;
        }

        string trimmed = rawUsername.Trim();
        bool isDefaultLocalHost = trimmed.Equals("guccihost", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.Equals("guccihost_local", StringComparison.OrdinalIgnoreCase);
        if (!isDefaultLocalHost)
        {
            return trimmed;
        }

        return platform switch
        {
            ContainerPlatform.Docker => "guccihost_dock",
            ContainerPlatform.Kubernetes or ContainerPlatform.K3sAgones => "guccihost_k3s",
            _ => trimmed
        };
    }

    /// <summary>
    /// Resolves the isolated Queue Arcade ServerHost account ("guccihost_arc", "kongor_host_arc", or "&lt;stem&gt;_arc")
    /// clamped to HoN/PostgreSQL's 15-character Account.Name VARCHAR(15) limit so Queue Arcade COMPEL never purges
    /// live Ranked server registrations on startup or disconnect.
    /// </summary>
    public static string ResolveQueueArcadeHostUsername(string? rawUsername, string fallback = "kongor_host_arc")
    {
        if (string.IsNullOrWhiteSpace(rawUsername))
        {
            return fallback;
        }

        string trimmed = rawUsername.Trim();
        if (trimmed.Equals("YOUR_KONGOR_USERNAME", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("YOUR_KONGOR_ARCADE_USERNAME", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        string baseName = trimmed;
        string[] knownSuffixes = ["_arc", "_arcade", "_local", "_dock", "_docker", "_k3s"];
        foreach (string suffix in knownSuffixes)
        {
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && baseName.Length > suffix.Length)
            {
                baseName = baseName[..^suffix.Length];
                break;
            }
        }

        if (baseName.Equals("guccihost", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("guccidonut", StringComparison.OrdinalIgnoreCase))
        {
            return "guccihost_arc";
        }

        if (baseName.Equals("kongor_host", StringComparison.OrdinalIgnoreCase))
        {
            return "kongor_host_arc";
        }

        string stem = baseName.Length > 11
            ? baseName[..11].TrimEnd('_')
            : baseName.TrimEnd('_');

        return string.IsNullOrWhiteSpace(stem) ? fallback : $"{stem}_arc";
    }

    /// <summary>
    /// Ensures Ranked and Queue Arcade supervisors never share the exact same ServerHost login
    /// (which would cause MasterServer replay_auth and ChatServer manager disconnect to purge each other's sessions).
    /// </summary>
    public static void EnsureDistinctHostAccounts(string? rankedUsername, string? arcadeUsername)
    {
        if (!string.IsNullOrWhiteSpace(rankedUsername) &&
            !string.IsNullOrWhiteSpace(arcadeUsername) &&
            string.Equals(rankedUsername.Trim(), arcadeUsername.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Queue Arcade cannot share the same ServerHost account ('{arcadeUsername.Trim()}') as Ranked game servers because manager auth/disconnect purges Redis sessions per host account. Use '{ResolveQueueArcadeHostUsername(rankedUsername)}' for Queue Arcade or set a base host name in Secrets & Config.");
        }
    }

    /// <summary>
    /// Builds an executable PowerShell container launch command with environment variables populated from vault secrets.
    /// </summary>
    public static string BuildCommand(
        NexusSecrets secrets,
        ContainerPlatform platform,
        bool maskSecrets = false,
        bool isWindowsHost = true,
        bool queueArcadeMode = false)
    {
        string username = queueArcadeMode
            ? ResolveQueueArcadeHostUsername(secrets.HostAccountUserName, "YOUR_KONGOR_ARCADE_USERNAME")
            : ResolveFleetHostUsername(secrets.HostAccountUserName, platform);

        string password = !string.IsNullOrWhiteSpace(secrets.HostAccountPassword)
            ? (maskSecrets ? "••••••••••••" : secrets.HostAccountPassword.Trim())
            : (maskSecrets ? "••••••••••••" : "YOUR_KONGOR_PASSWORD");

        string token = !string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken)
            ? (maskSecrets ? "••••••••••••" : secrets.CompelAuthenticationToken.Trim())
            : (maskSecrets ? "••••••••••••" : "YOUR_COMPEL_AUTH_TOKEN");

        string priorityMode = !string.IsNullOrWhiteSpace(secrets.CompelPriorityMode)
            ? secrets.CompelPriorityMode.Trim()
            : "Background";

        int instances = queueArcadeMode
            ? (secrets.QueueArcadeInstances > 0 ? secrets.QueueArcadeInstances : 2)
            : (secrets.CompelInstances > 0 ? secrets.CompelInstances : 2);

        string location = !string.IsNullOrWhiteSpace(secrets.CompelLocation) ? secrets.CompelLocation.Trim() : "USE";

        return platform switch
        {
            ContainerPlatform.Docker => BuildDockerRunCommand(username, password, token, priorityMode, instances, secrets, isWindowsHost, maskSecrets, queueArcadeMode),
            ContainerPlatform.Minikube => BuildMinikubeCommand(username, password, token, priorityMode, instances, location, queueArcadeMode),
            ContainerPlatform.Kubernetes => BuildKubernetesCommand(username, password, token, priorityMode, location, queueArcadeMode),
            ContainerPlatform.K3sAgones => BuildK3sAgonesCommand(username, password, token, priorityMode, secrets.CompelServerNamePrefix, location, queueArcadeMode),
            _ => BuildDockerRunCommand(username, password, token, priorityMode, instances, secrets, isWindowsHost, maskSecrets, queueArcadeMode)
        };
    }

    private static string BuildDockerRunCommand(
        string username,
        string password,
        string token,
        string priorityMode,
        int instances,
        NexusSecrets secrets,
        bool isWindowsHost,
        bool maskSecrets = false,
        bool queueArcadeMode = false)
    {
        string serverVolumeMount;
        if (!string.IsNullOrWhiteSpace(secrets.CompelPath) && Directory.Exists(secrets.CompelPath))
        {
            string normPath = secrets.CompelPath.Replace('\\', '/');
            serverVolumeMount = isWindowsHost
                ? $"\"{normPath}:/app/server:ro\""
                : $"{normPath}:/app/server:ro";
        }
        else if (isWindowsHost)
        {
            serverVolumeMount = "\"${PWD}/games/server:/app/server:ro\"";
        }
        else
        {
            serverVolumeMount = "/opt/hon/server:/app/server:ro";
        }

        string artefactsVolumeMount = isWindowsHost
            ? "\"${PWD}/artefacts:/app/artefacts:rw\""
            : "./artefacts:/app/artefacts:rw";

        // Multi-Fleet Offset: Isolate Docker Tier D to offset 100-199 (default 100 -> 11334) or Queue Arcade Tier Q to offset 250 (-> 11484)
        int portOffset = queueArcadeMode
            ? 250
            : (secrets.CompelPortRangeOffset is >= 100 and < 200 ? secrets.CompelPortRangeOffset : 100);
        int controlPort = queueArcadeMode
            ? 11484
            : (secrets.CompelControlPlanePort is >= 11300 and < 11400
                ? secrets.CompelControlPlanePort
                : (11234 + portOffset));
        int firstGamePort = 11235 + portOffset;
        int lastGamePort = firstGamePort + instances - 1;
        int firstVoicePort = 11735 + portOffset;
        int lastVoicePort = firstVoicePort + instances - 1;
        int firstProxyPort = 21234 + portOffset;
        int lastProxyPort = firstProxyPort + instances;
        int firstProxyVoicePort = 21735 + portOffset;
        int lastProxyVoicePort = firstProxyVoicePort + instances - 1;

        string location = !string.IsNullOrWhiteSpace(secrets.CompelLocation) ? secrets.CompelLocation.Trim() : "USE";
        string prefix = !string.IsNullOrWhiteSpace(secrets.CompelServerNamePrefix) ? secrets.CompelServerNamePrefix.Trim() : "KONGOR ARENA";
        string containerName = queueArcadeMode ? QueueArcadeContainerName : RankedContainerName;
        string runtimeVolumeName = queueArcadeMode ? QueueArcadeRuntimeVolumeName : RankedRuntimeVolumeName;
        string fleetTier = queueArcadeMode ? "Q" : "D";

        StringBuilder sb = new();
        sb.AppendLine("docker run -d `");
        sb.AppendLine($"  --name {containerName} `");
        sb.AppendLine("  --restart unless-stopped `");
        if (isWindowsHost)
        {
            sb.AppendLine("  --add-host=host.docker.internal:host-gateway `");
            sb.AppendLine($"  -p {controlPort}:{controlPort} `");
            sb.AppendLine($"  -p {controlPort}:{controlPort}/udp `");
            sb.AppendLine($"  -p {firstGamePort}-{lastGamePort}:{firstGamePort}-{lastGamePort}/udp `");
            sb.AppendLine($"  -p {firstVoicePort}-{lastVoicePort}:{firstVoicePort}-{lastVoicePort}/udp `");
            sb.AppendLine($"  -p {firstProxyPort}-{lastProxyPort}:{firstProxyPort}-{lastProxyPort}/udp `");
            sb.AppendLine($"  -p {firstProxyVoicePort}-{lastProxyVoicePort}:{firstProxyVoicePort}-{lastProxyVoicePort}/udp `");
        }
        else
        {
            sb.AppendLine("  --net=host `");
        }

        string escapedPassword = isWindowsHost && !maskSecrets
            ? password.Replace("`", "``").Replace("$", "`$")
            : password;

        sb.AppendLine("  --cap-add=SYS_NICE `");
        if (isWindowsHost)
        {
            sb.AppendLine("  -e MASTER_SERVER_ADDRESS=\"http://host.docker.internal:55555\" `");
            sb.AppendLine("  -e Compel__MasterServerAddress=\"host.docker.internal:55555\" `");
            sb.AppendLine("  -e COMPEL_GATEWAY=\"127.0.0.1\" `");
            sb.AppendLine("  -e Compel__Gateway=\"127.0.0.1\" `");
        }
        sb.AppendLine($"  -e COMPEL_FLEET_TIER={fleetTier} `");
        sb.AppendLine($"  -e Compel__FleetTier={fleetTier} `");
        if (queueArcadeMode)
        {
            sb.AppendLine("  -e COMPEL_QUEUE_ARCADE_MODE=true `");
            sb.AppendLine("  -e Compel__QueueArcadeMode=true `");
            sb.AppendLine("  -e COMPEL_WARM_INSTANCES_TARGET=1 `");
            sb.AppendLine("  -e Compel__WarmInstancesTarget=1 `");
        }
        sb.AppendLine($"  -e COMPEL_PRIORITY_MODE={priorityMode} `");
        sb.AppendLine($"  -e Compel__PriorityMode={priorityMode} `");
        sb.AppendLine($"  -e COMPEL_INSTANCES={instances} `");
        sb.AppendLine($"  -e Compel__Instances={instances} `");
        sb.AppendLine($"  -e COMPEL_LOCATION=\"{location}\" `");
        sb.AppendLine($"  -e Compel__Location=\"{location}\" `");
        sb.AppendLine($"  -e COMPEL_SERVER_NAME_PREFIX=\"{prefix}\" `");
        sb.AppendLine($"  -e Compel__ServerNamePrefix=\"{prefix}\" `");
        sb.AppendLine($"  -e COMPEL_PORT_RANGE_OFFSET={portOffset} `");
        sb.AppendLine($"  -e Compel__PortRangeOffset={portOffset} `");
        sb.AppendLine($"  -e COMPEL_CONTROL_PLANE_PORT={controlPort} `");
        sb.AppendLine($"  -e Compel__ControlPlanePort={controlPort} `");
        sb.AppendLine("  -e COMPEL_USE_PROXY=false `");
        sb.AppendLine("  -e Compel__UseProxy=false `");
        sb.AppendLine($"  -e HOST_USERNAME=\"{username}\" `");
        sb.AppendLine($"  -e HostAccount__UserName=\"{username}\" `");
        sb.AppendLine($"  -e HOST_PASSWORD=\"{escapedPassword}\" `");
        sb.AppendLine($"  -e HostAccount__Password=\"{escapedPassword}\" `");
        sb.AppendLine($"  -e COMPEL_AUTHENTICATION_TOKEN=\"{token}\" `");
        sb.AppendLine($"  -e Compel__AuthenticationToken=\"{token}\" `");
        sb.AppendLine($"  -v {serverVolumeMount} `");
        sb.AppendLine($"  -v {artefactsVolumeMount} `");
        sb.AppendLine($"  -v {runtimeVolumeName}:/app/run-server:rw `");
        sb.Append($"  {DefaultDockerImage}");

        return sb.ToString();
    }

    /// <summary>
    /// Builds raw docker run arguments (starting with "run -d ...") for direct ProcessStartInfo execution without shell interpolation.
    /// </summary>
    public static string BuildDockerRunArguments(
        NexusSecrets secrets,
        string priorityMode,
        int instances,
        string serverVol,
        string artefactsVol,
        bool isWindowsHost = true,
        bool queueArcadeMode = false)
    {
        string username = queueArcadeMode
            ? ResolveQueueArcadeHostUsername(secrets.HostAccountUserName)
            : ResolveFleetHostUsername(secrets.HostAccountUserName, ContainerPlatform.Docker);

        string password = !string.IsNullOrWhiteSpace(secrets.HostAccountPassword)
            ? secrets.HostAccountPassword.Trim()
            : "YOUR_KONGOR_PASSWORD";

        string token = !string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken)
            ? secrets.CompelAuthenticationToken.Trim()
            : "YOUR_COMPEL_AUTH_TOKEN";

        string location = !string.IsNullOrWhiteSpace(secrets.CompelLocation) ? secrets.CompelLocation.Trim() : "USE";
        string prefix = !string.IsNullOrWhiteSpace(secrets.CompelServerNamePrefix) ? secrets.CompelServerNamePrefix.Trim() : "KONGOR ARENA";

        int portOffset = queueArcadeMode
            ? 250
            : (secrets.CompelPortRangeOffset is >= 100 and < 200 ? secrets.CompelPortRangeOffset : 100);
        int controlPort = queueArcadeMode
            ? 11484
            : (secrets.CompelControlPlanePort is >= 11300 and < 11400
                ? secrets.CompelControlPlanePort
                : (11234 + portOffset));
        int firstGamePort = 11235 + portOffset;
        int lastGamePort = firstGamePort + instances - 1;
        int firstVoicePort = 11735 + portOffset;
        int lastVoicePort = firstVoicePort + instances - 1;
        int firstProxyPort = 21234 + portOffset;
        int lastProxyPort = firstProxyPort + instances;
        int firstProxyVoicePort = 21735 + portOffset;
        int lastProxyVoicePort = firstProxyVoicePort + instances - 1;

        string netArgs = isWindowsHost
            ? $"--add-host=host.docker.internal:host-gateway -p {controlPort}:{controlPort} -p {controlPort}:{controlPort}/udp -p {firstGamePort}-{lastGamePort}:{firstGamePort}-{lastGamePort}/udp -p {firstVoicePort}-{lastVoicePort}:{firstVoicePort}-{lastVoicePort}/udp -p {firstProxyPort}-{lastProxyPort}:{firstProxyPort}-{lastProxyPort}/udp -p {firstProxyVoicePort}-{lastProxyVoicePort}:{firstProxyVoicePort}-{lastProxyVoicePort}/udp"
            : "--net=host";

        string masterArgs = isWindowsHost
            ? "-e MASTER_SERVER_ADDRESS=\"http://host.docker.internal:55555\" -e Compel__MasterServerAddress=\"host.docker.internal:55555\" -e COMPEL_GATEWAY=\"127.0.0.1\" -e Compel__Gateway=\"127.0.0.1\" "
            : "";

        string containerName = queueArcadeMode ? QueueArcadeContainerName : RankedContainerName;
        string runtimeVolumeName = queueArcadeMode ? QueueArcadeRuntimeVolumeName : RankedRuntimeVolumeName;
        string fleetTier = queueArcadeMode ? "Q" : "D";
        string arcadeArgs = queueArcadeMode
            ? "-e COMPEL_QUEUE_ARCADE_MODE=true -e Compel__QueueArcadeMode=true -e COMPEL_WARM_INSTANCES_TARGET=1 -e Compel__WarmInstancesTarget=1 "
            : "";

        return $"run -d --name {containerName} --restart unless-stopped {netArgs} --cap-add=SYS_NICE {masterArgs}-e COMPEL_FLEET_TIER={fleetTier} -e Compel__FleetTier={fleetTier} {arcadeArgs}-e COMPEL_PRIORITY_MODE={priorityMode} -e Compel__PriorityMode={priorityMode} -e COMPEL_INSTANCES={instances} -e Compel__Instances={instances} -e COMPEL_LOCATION=\"{location}\" -e Compel__Location=\"{location}\" -e COMPEL_SERVER_NAME_PREFIX=\"{prefix}\" -e Compel__ServerNamePrefix=\"{prefix}\" -e COMPEL_PORT_RANGE_OFFSET={portOffset} -e Compel__PortRangeOffset={portOffset} -e COMPEL_CONTROL_PLANE_PORT={controlPort} -e Compel__ControlPlanePort={controlPort} -e COMPEL_USE_PROXY=false -e Compel__UseProxy=false -e HOST_USERNAME=\"{username}\" -e HostAccount__UserName=\"{username}\" -e HOST_PASSWORD=\"{password}\" -e HostAccount__Password=\"{password}\" -e COMPEL_AUTHENTICATION_TOKEN=\"{token}\" -e Compel__AuthenticationToken=\"{token}\" -v {serverVol} -v {artefactsVol} -v {runtimeVolumeName}:/app/run-server:rw {DefaultDockerImage}";
    }

    /// <summary>
    /// Builds strongly-typed CreateContainerParameters for Docker.DotNet execution.
    /// </summary>
    public static CreateContainerParameters BuildCreateContainerParameters(
        NexusSecrets secrets,
        string priorityMode,
        int instances,
        string serverPath,
        string artefactsPath,
        bool isWindowsHost = true,
        bool queueArcadeMode = false)
    {
        string username = queueArcadeMode
            ? ResolveQueueArcadeHostUsername(secrets.HostAccountUserName)
            : ResolveFleetHostUsername(secrets.HostAccountUserName, ContainerPlatform.Docker);

        string password = !string.IsNullOrWhiteSpace(secrets.HostAccountPassword)
            ? secrets.HostAccountPassword.Trim()
            : "YOUR_KONGOR_PASSWORD";

        string token = !string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken)
            ? secrets.CompelAuthenticationToken.Trim()
            : "YOUR_COMPEL_AUTH_TOKEN";

        string location = !string.IsNullOrWhiteSpace(secrets.CompelLocation) ? secrets.CompelLocation.Trim() : "USE";
        string prefix = !string.IsNullOrWhiteSpace(secrets.CompelServerNamePrefix) ? secrets.CompelServerNamePrefix.Trim() : "KONGOR ARENA";

        int portOffset = queueArcadeMode
            ? 250
            : (secrets.CompelPortRangeOffset is >= 100 and < 200 ? secrets.CompelPortRangeOffset : 100);
        int controlPort = queueArcadeMode
            ? 11484
            : (secrets.CompelControlPlanePort is >= 11300 and < 11400
                ? secrets.CompelControlPlanePort
                : (11234 + portOffset));
        int firstGamePort = 11235 + portOffset;
        int lastGamePort = firstGamePort + instances - 1;
        int firstVoicePort = 11735 + portOffset;
        int lastVoicePort = firstVoicePort + instances - 1;
        int firstProxyPort = 21234 + portOffset;
        int lastProxyPort = firstProxyPort + instances;
        int firstProxyVoicePort = 21735 + portOffset;
        int lastProxyVoicePort = firstProxyVoicePort + instances - 1;

        // Clean volume paths - remove any quotes or leading/trailing whitespace
        string cleanServer = serverPath.Trim().Trim('\"').Replace('\\', '/');
        string cleanArtefacts = artefactsPath.Trim().Trim('\"').Replace('\\', '/');

        string runtimeVolumeName = queueArcadeMode ? QueueArcadeRuntimeVolumeName : RankedRuntimeVolumeName;

        // Binds format: "/host/path:/container/path:mode"
        List<string> binds = new()
        {
            $"{cleanServer}:/app/server:ro",
            $"{cleanArtefacts}:/app/artefacts:rw",
            $"{runtimeVolumeName}:/app/run-server:rw"
        };

        string fleetTier = queueArcadeMode ? "Q" : "D";
        List<string> env = new()
        {
            $"COMPEL_FLEET_TIER={fleetTier}",
            $"Compel__FleetTier={fleetTier}",
            $"COMPEL_PRIORITY_MODE={priorityMode}",
            $"Compel__PriorityMode={priorityMode}",
            $"COMPEL_INSTANCES={instances}",
            $"Compel__Instances={instances}",
            $"COMPEL_LOCATION={location}",
            $"Compel__Location={location}",
            $"COMPEL_SERVER_NAME_PREFIX={prefix}",
            $"Compel__ServerNamePrefix={prefix}",
            $"COMPEL_PORT_RANGE_OFFSET={portOffset}",
            $"Compel__PortRangeOffset={portOffset}",
            $"COMPEL_CONTROL_PLANE_PORT={controlPort}",
            $"Compel__ControlPlanePort={controlPort}",
            "COMPEL_USE_PROXY=false",
            "Compel__UseProxy=false",
            $"HOST_USERNAME={username}",
            $"HostAccount__UserName={username}",
            $"HOST_PASSWORD={password}",
            $"HostAccount__Password={password}",
            $"COMPEL_AUTHENTICATION_TOKEN={token}",
            $"Compel__AuthenticationToken={token}"
        };

        if (queueArcadeMode)
        {
            env.Add("COMPEL_QUEUE_ARCADE_MODE=true");
            env.Add("Compel__QueueArcadeMode=true");
            env.Add("COMPEL_WARM_INSTANCES_TARGET=1");
            env.Add("Compel__WarmInstancesTarget=1");
        }

        HostConfig hostConfig = new()
        {
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
            CapAdd = new List<string> { "SYS_NICE" },
            Binds = binds
        };

        Dictionary<string, EmptyStruct> exposedPorts = new();

        if (isWindowsHost)
        {
            env.Add("MASTER_SERVER_ADDRESS=http://host.docker.internal:55555");
            env.Add("Compel__MasterServerAddress=host.docker.internal:55555");
            env.Add("COMPEL_GATEWAY=127.0.0.1");
            env.Add("Compel__Gateway=127.0.0.1");

            hostConfig.ExtraHosts = new List<string> { "host.docker.internal:host-gateway" };

            var portBindings = new Dictionary<string, IList<PortBinding>>
            {
                [$"{controlPort}/tcp"] = new List<PortBinding> { new() { HostPort = controlPort.ToString() } }
            };
            exposedPorts[$"{controlPort}/tcp"] = default;

            void AddPortRange(int start, int end, string proto)
            {
                for (int p = start; p <= end; p++)
                {
                    string key = $"{p}/{proto}";
                    portBindings[key] = new List<PortBinding> { new() { HostPort = p.ToString() } };
                    exposedPorts[key] = default;
                }
            }

            AddPortRange(firstGamePort, lastGamePort, "udp");
            AddPortRange(firstVoicePort, lastVoicePort, "udp");
            AddPortRange(firstProxyPort, lastProxyPort, "udp");
            AddPortRange(firstProxyVoicePort, lastProxyVoicePort, "udp");

            hostConfig.PortBindings = portBindings;
        }
        else
        {
            hostConfig.NetworkMode = "host";
        }

        return new CreateContainerParameters
        {
            Name = queueArcadeMode ? QueueArcadeContainerName : RankedContainerName,
            Image = DefaultDockerImage,
            Env = env,
            HostConfig = hostConfig,
            ExposedPorts = exposedPorts
        };
    }

    private static string BuildMinikubeCommand(
        string username,
        string password,
        string token,
        string priorityMode,
        int instances,
        string location = "USE",
        bool queueArcadeMode = false)
    {
        StringBuilder sb = new();
        sb.AppendLine($"minikube image load {DefaultDockerImage}");
        sb.AppendLine();
        sb.AppendLine(queueArcadeMode ? "kubectl run compel-queue-arcade-minikube `" : "kubectl run compel-minikube `");
        sb.AppendLine($"  --image={DefaultDockerImage} `");
        sb.AppendLine("  --image-pull-policy=Never `");
        if (queueArcadeMode)
        {
            sb.AppendLine("  --env=\"COMPEL_FLEET_TIER=Q\" `");
            sb.AppendLine("  --env=\"COMPEL_QUEUE_ARCADE_MODE=true\" `");
            sb.AppendLine("  --env=\"COMPEL_PORT_RANGE_OFFSET=250\" `");
            sb.AppendLine("  --env=\"COMPEL_CONTROL_PLANE_PORT=11484\" `");
        }
        sb.AppendLine($"  --env=\"COMPEL_LOCATION={location}\" `");
        sb.AppendLine($"  --env=\"COMPEL_PRIORITY_MODE={priorityMode}\" `");
        sb.AppendLine($"  --env=\"COMPEL_INSTANCES={instances}\" `");
        sb.AppendLine($"  --env=\"HOST_USERNAME={username}\" `");
        sb.AppendLine($"  --env=\"HOST_PASSWORD={password}\" `");
        sb.Append($"  --env=\"COMPEL_AUTHENTICATION_TOKEN={token}\"");

        return sb.ToString();
    }

    private static string BuildKubernetesCommand(
        string username,
        string password,
        string token,
        string priorityMode,
        string location = "USE",
        bool queueArcadeMode = false)
    {
        string secretName = queueArcadeMode ? "kongor-arcade-host-credentials" : "kongor-host-credentials";
        StringBuilder sb = new();
        sb.AppendLine("# 1. Create or update secret with host credentials");
        sb.AppendLine($"kubectl create secret generic {secretName} `");
        sb.AppendLine($"  --from-literal=username=\"{username}\" `");
        sb.AppendLine($"  --from-literal=password=\"{password}\" `");
        sb.AppendLine($"  --from-literal=token=\"{token}\" `");
        sb.AppendLine("  --dry-run=client -o yaml | kubectl apply -f -");
        sb.AppendLine();
        sb.AppendLine("# 2. Apply Kubernetes Deployment or Agones Fleet");
        sb.AppendLine("kubectl apply -f infra/k8s/compel-k3s.yaml");
        if (queueArcadeMode)
        {
            sb.AppendLine($"kubectl set env deployment/compel-k3s COMPEL_FLEET_TIER=Q COMPEL_QUEUE_ARCADE_MODE=true COMPEL_PORT_RANGE_OFFSET=250 COMPEL_CONTROL_PLANE_PORT=11484 COMPEL_LOCATION=\"{location}\"");
        }
        else
        {
            sb.AppendLine($"kubectl set env deployment/compel-k3s COMPEL_LOCATION=\"{location}\"");
        }
        sb.AppendLine("# Or Agones Fleet: kubectl apply -f infra/k8s/agones/fleet.yaml");

        return sb.ToString();
    }

    private static string BuildK3sAgonesCommand(
        string username,
        string password,
        string token,
        string priorityMode,
        string? prefix = null,
        string location = "USE",
        bool queueArcadeMode = false)
    {
        string secretName = queueArcadeMode ? "kongor-arcade-host-credentials" : "kongor-host-credentials";
        StringBuilder sb = new();
        sb.AppendLine("# 1. Provision host credentials secret in k3s cluster");
        sb.AppendLine($"kubectl create secret generic {secretName} `");
        sb.AppendLine($"  --from-literal=username=\"{username}\" `");
        sb.AppendLine($"  --from-literal=password=\"{password}\" `");
        sb.AppendLine($"  --from-literal=token=\"{token}\" `");
        sb.AppendLine("  --dry-run=client -o yaml | kubectl apply -f -");
        sb.AppendLine();
        sb.AppendLine(queueArcadeMode
            ? "# 2. Apply Agones Queue Arcade Fleet (Tier Q: Port Offset 250 -> Game 11485+, Control Plane 11484)"
            : "# 2. Apply Agones GameServer Fleet (Port Offset 200 -> Game 11435+, Control Plane 11434)");
        sb.AppendLine("kubectl apply -f infra/k8s/agones/fleet.yaml");
        if (queueArcadeMode)
        {
            sb.AppendLine($"kubectl set env fleet/kongor-dedicated-fleet COMPEL_FLEET_TIER=Q COMPEL_QUEUE_ARCADE_MODE=true COMPEL_PORT_RANGE_OFFSET=250 COMPEL_CONTROL_PLANE_PORT=11484 COMPEL_WARM_INSTANCES_TARGET=1 COMPEL_LOCATION=\"{location}\"");
        }
        else
        {
            sb.AppendLine($"kubectl set env fleet/kongor-dedicated-fleet COMPEL_LOCATION=\"{location}\"");
        }
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            sb.AppendLine($"kubectl set env fleet/kongor-dedicated-fleet COMPEL_SERVER_NAME_PREFIX=\"{prefix.Trim()}\"");
        }
        sb.AppendLine();
        sb.AppendLine("# 3. Verify fleet status and GameServer pod allocation");
        sb.Append("kubectl get gameservers");

        return sb.ToString();
    }
}
