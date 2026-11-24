// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Fleet;

/// <summary>
///     A typed wrapper holding a configuration setting value and its human-readable description.
/// </summary>
/// <typeparam name="T">Setting value type.</typeparam>
public sealed class CompelConfigItem<T>
{
    [JsonPropertyName("Value")] public T Value { get; set; } = default!;

    [JsonPropertyName("Description")] public string Description { get; set; } = string.Empty;
}

/// <summary>
///     Configuration file DTO describing compel.config.json used by COMPEL server hosts.
/// </summary>
public sealed class CompelConfigFileDto
{
    [JsonPropertyName("UserName")]
    public CompelConfigItem<string> UserName { get; set; } = new()
    {
        Value = "HOST",
        Description =
            "The name of the user which will host the game servers. This needs to match the name of a registered Project KONGOR user."
    };

    [JsonPropertyName("Password")]
    public CompelConfigItem<string> Password { get; set; } = new()
    {
        Value = "KONGOR",
        Description =
            "The password of the user which will host the game servers. This needs to match the password of the registered Project KONGOR user set to host the game servers."
    };

    [JsonPropertyName("Instances")]
    public CompelConfigItem<int> Instances { get; set; } = new()
    {
        Value = 2,
        Description =
            "The number of server instances to spawn. This must be between one and the number of logical processors. The server manager spreads the instances across the available processors; running COMPEL with elevated privileges is required for the manager to assign their processor affinity."
    };

    [JsonPropertyName("Gateway")]
    public CompelConfigItem<string> Gateway { get; set; } = new()
    {
        Value = "localhost",
        Description =
            "The entry point for game servers and the server manager. Use 'kongor.net' for the official public gateway, 'localhost' for local development, a LAN or public IP address, or a local or public host name to resolve."
    };

    [JsonPropertyName("Location")]
    public CompelConfigItem<string> Location { get; set; } = new()
    {
        Value = "USE",
        Description =
            "Normally, the location can be set to any value, but, in order for the server to be TMM-compatible, only the following values are valid: 'USW', 'USE', 'EU', 'AU', 'BR', 'RU', and 'SEA'."
    };

    [JsonPropertyName("ServerNamePrefix")]
    public CompelConfigItem<string> ServerNamePrefix { get; set; } = new()
    {
        Value = "DONUT ARENA",
        Description =
            "The base name of the game server instances. The name of each server instance will be the concatenation of this base name and the 1-based index of the instance."
    };

    [JsonPropertyName("UseProxy")]
    public CompelConfigItem<bool> UseProxy { get; set; } = new()
    {
        Value = false,
        Description =
            "Whether to run COMPEL's built-in proxy in front of the game servers. When enabled, clients connect to public ports offset 10000 above the local server ports (e.g. 21235 instead of 11235); the proxy forwards them to the servers and authenticates each client with the challenge protocol required on that port range. Set to 'false' to make the local server ports public directly, without the proxy."
    };

    [JsonPropertyName("PortRangeOffset")]
    public CompelConfigItem<int> PortRangeOffset { get; set; } = new()
    {
        Value = 20,
        Description =
            "The offset from the start of the valid game/voice port ranges at which the game/voice ports to be used at runtime will start. The game/voice port ranges without the proxy are 11235-11335/11435-11535, and with the proxy they are 21235-21335/21435-21535."
    };

    [JsonPropertyName("RuntimeArtefactsPath")]
    public CompelConfigItem<string> RuntimeArtefactsPath { get; set; } = new()
    {
        Value = "DEFAULT",
        Description =
            "The base directory beneath which the match server writes its runtime artefacts (e.g. replays, logs). This value is either the 'DEFAULT' alias, which places artefacts beneath the host account's profile (its 'Documents/Heroes of Newerth x64' tree, where everything else is written), or a fully qualified path to use as the base profile directory instead. This setting applies on Windows only; the Linux server build writes to a fixed location ('/opt/hon/config') and ignores it."
    };

    [JsonPropertyName("CDNSynchronisation")]
    public CompelConfigItem<bool> CDNSynchronisation { get; set; } = new()
    {
        Value = false,
        Description =
            "Whether to synchronise the match server distribution from the CDN on startup. Set to 'false' to skip the initial synchronisation for development and testing, in which case the existing local distribution is used; the '/sync' management endpoint can still trigger a synchronisation on demand."
    };

    [JsonPropertyName("AuthenticationToken")]
    public CompelConfigItem<string> AuthenticationToken { get; set; } = new()
    {
        Value = "...",
        Description =
            "The bearer token that NEXUS and host operators must present to use the remote management endpoints (status, synchronisation, and instance lifecycle). Leave as '...' (or empty) to disable remote management. The control plane serves plain HTTP, so expose it only on a trusted network or behind a TLS-terminating reverse proxy; otherwise the token travels in cleartext."
    };

    [JsonPropertyName("ControlPlanePort")]
    public CompelConfigItem<int> ControlPlanePort { get; set; } = new()
    {
        Value = 8080,
        Description =
            "The TCP port on which the HTTP control plane (the latency ping, the health checks, and the management endpoints) listens."
    };

    [JsonPropertyName("GrafanaOtlpEndpoint")]
    public CompelConfigItem<string> GrafanaOtlpEndpoint { get; set; } = new()
    {
        Value = "https://otlp-gateway-prod-us-east-0.grafana.net/otlp",
        Description =
            "Optional Grafana Cloud OpenTelemetry (OTLP) HTTP/gRPC endpoint for live server metrics, tick profiling, and observability. If configured in dotnet User Secrets (GRAFANA_OTLP_ENDPOINT), secrets take precedence."
    };

    [JsonPropertyName("GrafanaOtlpHeaders")]
    public CompelConfigItem<string> GrafanaOtlpHeaders { get; set; } = new()
    {
        Value = "Basic MTIzNDU2OkZha2VFeGFtcGxlVG9rZW5Gb3JHcmFmYW5hQ2xvdWQ=",
        Description =
            "Optional Grafana Cloud OTLP authentication header (e.g. 'Basic <InstanceID:ApiKeyBase64>'). If configured in dotnet User Secrets (GRAFANA_OTLP_HEADERS), secrets take precedence."
    };

    [JsonPropertyName("PyroscopeServerAddress")]
    public CompelConfigItem<string> PyroscopeServerAddress { get; set; } = new()
    {
        Value = "https://pyroscope-prod-us-east-0.grafana.net",
        Description =
            "Optional Grafana Pyroscope continuous profiling server address. If configured in dotnet User Secrets (PYROSCOPE_SERVER_ADDRESS), secrets take precedence."
    };

    [JsonPropertyName("PyroscopeBasicAuthUser")]
    public CompelConfigItem<string> PyroscopeBasicAuthUser { get; set; } = new()
    {
        Value = "123456",
        Description =
            "Optional Grafana Pyroscope basic auth user ID. If configured in dotnet User Secrets (PYROSCOPE_BASIC_AUTH_USER), secrets take precedence."
    };

    [JsonPropertyName("PyroscopeBasicAuthPassword")]
    public CompelConfigItem<string> PyroscopeBasicAuthPassword { get; set; } = new()
    {
        Value = string.Empty,
        Description =
            "Optional Grafana Pyroscope basic auth password or API token. If configured in dotnet User Secrets (PYROSCOPE_BASIC_AUTH_PASSWORD), secrets take precedence."
    };
}
