// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using System.Text;
using System.Text.Json;
using COMPEL.Serialisation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using Kongor.Core.Services.Voice;
using COMPEL.Services.Telemetry;

namespace COMPEL.Endpoints;

/// <summary>
///     Maps the HTTP control plane: an anonymous latency probe plus the bearer-token-protected management endpoints that
///     NEXUS and host operators use to query status and drive lifecycle actions.
/// </summary>
public static class ControlPlaneEndpoints
{
    public static WebApplication MapControlPlaneEndpoints(this WebApplication application)
    {
        long startTicks = Environment.TickCount64;

        application.Use(async (httpContext, next) =>
        {
            PathString path = httpContext.Request.Path;
            if (path.Equals("/ping", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/alive", StringComparison.OrdinalIgnoreCase))
            {
                await next(httpContext);
                return;
            }

            IResult? failure = ControlPlaneAuthentication.Validate(httpContext);
            if (failure is not null)
            {
                await failure.ExecuteAsync(httpContext);
                return;
            }

            await next(httpContext);
        });

        // Anonymous Latency Probe.
        application.MapGet("/ping",
            () => TypedResults.Ok(new PingResponse("COMPEL", GeneratedVersionInformation.VersionString,
                TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds())));

        application.MapGet("/api/ping",
            () => TypedResults.Ok(new PingResponse("COMPEL", GeneratedVersionInformation.VersionString,
                TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds())));

        // Minimal unauthenticated liveness probe (zero server tokens or IP addresses leaked).
        application.MapGet("/healthz", () => Results.Text("Healthy"));

        RouteGroupBuilder management = application.MapGroup(string.Empty);

        management.AddEndpointFilter(async (context, next) =>
        {
            IResult? failure = ControlPlaneAuthentication.Validate(context.HttpContext);

            return failure is not null ? failure : await next(context);
        });

        StatusResponse BuildStatusResponse(MatchServerManagerSupervisor supervisor, DistributionSynchronisationService distribution,
            UDPProxyService proxy, UDPPingResponder pingResponder, MatchServerManagerOptions options,
            COMPEL.Services.Telemetry.RlTrajectoryCollectorService? rlCollector = null)
        {
            PortPlan ports = supervisor.Ports;

            PortAllocationResponse portAllocation = new
            (
                ports.LocalGameStart,
                ports.LocalGameEnd,
                ports.LocalVoiceStart,
                ports.LocalVoiceEnd,
                ports.PublicGameStart,
                ports.PublicGameEnd,
                ports.PublicVoiceStart,
                ports.PublicVoiceEnd,
                ports.PingPort
            );

            IReadOnlyList<DedicatedWorkerStatus> workerStatuses = supervisor.Workers.Select(w =>
            {
                (bool ipcConnected, string? ipcPath, string? ipcTransport) = w.GetIpcStatusSnapshot();
                return new DedicatedWorkerStatus(
                    w.InstanceIndex,
                    w.Port,
                    w.VoicePort,
                    w.PublicGamePort,
                    w.ProcessId,
                    w.IsRunning,
                    w.WorkingSetBytes,
                    w.WorkingSetMb,
                    w.UptimeSeconds,
                    w.CrashCount,
                    w.CurrentState,
                    w.TickrateTracker.CurrentTickrateHz,
                    w.TickrateTracker.TickVarianceMs,
                    w.ServerName,
                    ipcConnected,
                    ipcPath,
                    ipcTransport);
            }).ToList();

            bool managerRunning = supervisor.DesiredRunning || supervisor.IsRunning;

            return new StatusResponse
            (
                "COMPEL",
                GeneratedVersionInformation.VersionString,
                options.ServerNamePrefix,
                options.Location,
                supervisor.ServerAddress,
                options.Instances,
                options.UseProxy,
                options.PortRangeOffset,
                portAllocation,
                distribution.DistributionVersion,
                distribution.SynchronisationState,
                managerRunning,
                proxy.IsRunning,
                pingResponder.IsBound,
                proxy.FailedForwarderCount,
                proxy.DroppedDatagramCount,
                proxy.IsUnderAttack,
                (Environment.TickCount64 - startTicks) / 1000.0,
                supervisor.ActiveWorkerCount,
                workerStatuses,
                OperatingSystem.IsLinux() ? "Container (Linux)" : "Local Native (Windows)",
                rlCollector?.GetStatusSnapshot()
            );
        }

        // Observable Node Health & Telemetry Status (Management Authenticated).
        management.MapGet("/status",
            ([FromServices] MatchServerManagerSupervisor supervisor, [FromServices] DistributionSynchronisationService distribution,
                [FromServices] UDPProxyService proxy, [FromServices] UDPPingResponder pingResponder,
                [FromServices] IOptions<MatchServerManagerOptions> optionsAccessor,
                [FromServices] COMPEL.Services.Telemetry.RlTrajectoryCollectorService? rlCollector) =>
            TypedResults.Ok(BuildStatusResponse(supervisor, distribution, proxy, pingResponder, optionsAccessor.Value, rlCollector)));

        // Route alias for /api/status
        management.MapGet("/api/status",
            ([FromServices] MatchServerManagerSupervisor supervisor, [FromServices] DistributionSynchronisationService distribution,
                [FromServices] UDPProxyService proxy, [FromServices] UDPPingResponder pingResponder,
                [FromServices] IOptions<MatchServerManagerOptions> optionsAccessor,
                [FromServices] COMPEL.Services.Telemetry.RlTrajectoryCollectorService? rlCollector) =>
            TypedResults.Ok(BuildStatusResponse(supervisor, distribution, proxy, pingResponder, optionsAccessor.Value, rlCollector)));

        // Prometheus Metrics Exposition Endpoint
        IResult GetMetricsHandler(
            [FromServices] MatchServerManagerSupervisor supervisor,
            [FromServices] UDPProxyService? proxy,
            [FromServices] IOptions<MatchServerManagerOptions> optionsAccessor)
        {
            string metrics = PrometheusMetricsExporter.ExportMetrics(supervisor, proxy, optionsAccessor.Value);
            return Results.Content(metrics, "text/plain; version=0.0.4; charset=utf-8");
        }

        management.MapGet("/metrics", GetMetricsHandler);
        management.MapGet("/api/metrics", GetMetricsHandler);

        // Dedicated Game Server Fleet Workers inspection
        IResult GetServersHandler([FromServices] MatchServerManagerSupervisor supervisor)
        {
            DedicatedWorkerStatus[] workerStatuses = supervisor.Workers.Select(w => new DedicatedWorkerStatus(
                w.InstanceIndex,
                w.Port,
                w.VoicePort,
                w.PublicGamePort,
                w.ProcessId,
                w.IsRunning,
                w.WorkingSetBytes,
                w.WorkingSetMb,
                w.UptimeSeconds,
                w.CrashCount,
                w.CurrentState,
                w.TickrateTracker.CurrentTickrateHz,
                w.TickrateTracker.TickVarianceMs)).ToArray();

            return TypedResults.Ok(workerStatuses);
        }

        management.MapGet("/servers", GetServersHandler);
        management.MapGet("/api/servers", GetServersHandler);

        // Dedicated Game Server Worker status by Port
        IResult GetServerByPortHandler(int port, [FromServices] MatchServerManagerSupervisor supervisor)
        {
            DedicatedServerWorker? worker = supervisor.FindWorker(port);
            if (worker is null)
                return Results.NotFound(new ActionResponse("query", false, $"No dedicated worker configured on port {port}"));

            DedicatedWorkerStatus status = new(
                worker.InstanceIndex,
                worker.Port,
                worker.VoicePort,
                worker.PublicGamePort,
                worker.ProcessId,
                worker.IsRunning,
                worker.WorkingSetBytes,
                worker.WorkingSetMb,
                worker.UptimeSeconds,
                worker.CrashCount,
                worker.CurrentState,
                worker.TickrateTracker.CurrentTickrateHz,
                worker.TickrateTracker.TickVarianceMs);

            return Results.Ok(status);
        }

        management.MapGet("/servers/{port:int}", GetServerByPortHandler);
        management.MapGet("/api/servers/{port:int}", GetServerByPortHandler);

        IResult HandleSync([FromServices] DistributionSynchronisationService distribution, [FromServices] MatchServerManagerSupervisor supervisor,
            [FromServices] IHostApplicationLifetime lifetime)
        {
            if (supervisor.IsRunning || supervisor.DesiredRunning)
                return TypedResults.Conflict(new ActionResponse("sync", false,
                    @"The Match Server Manager Must Be Stopped Via ""/instances/stop"" Before Synchronising"));

            _ = Task.Run(async () =>
            {
                try
                {
                    await distribution.SynchroniseNow(lifetime.ApplicationStopping).ConfigureAwait(false);
                }
                catch
                {
                    /* The Outcome Is Recorded On The Service's State And The Failure Is Logged Within. */
                }
            });

            return TypedResults.Ok(new ActionResponse("sync", true, "Synchronisation Started"));
        }

        management.MapPost("/sync", HandleSync);
        management.MapPost("/api/sync", HandleSync);

        IResult HandleStart([FromServices] MatchServerManagerSupervisor supervisor)
        {
            supervisor.RequestStart();
            return TypedResults.Ok(new ActionResponse("start", true, "Start Requested"));
        }

        management.MapPost("/instances/start", HandleStart);
        management.MapPost("/api/instances/start", HandleStart);

        IResult HandleStop([FromServices] MatchServerManagerSupervisor supervisor)
        {
            supervisor.RequestStop();
            return TypedResults.Ok(new ActionResponse("stop", true, "Stop Requested"));
        }

        management.MapPost("/instances/stop", HandleStop);
        management.MapPost("/api/instances/stop", HandleStop);

        async Task<IResult> HandleRestart([FromServices] MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken)
        {
            await supervisor.RequestRestart(cancellationToken);
            return TypedResults.Ok(new ActionResponse("restart", true, "Restart Requested"));
        }

        management.MapPost("/instances/restart", HandleRestart);
        management.MapPost("/api/instances/restart", HandleRestart);

        async Task<IResult> HandleWorkerStart(int port, [FromServices] MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken)
        {
            bool found = await supervisor.RequestWorkerStart(port, cancellationToken);
            if (!found)
                return Results.NotFound(new ActionResponse("start", false, $"No dedicated worker configured on port {port}"));

            return Results.Ok(new ActionResponse("start", true, $"Start requested for worker on port {port}"));
        }

        management.MapPost("/instances/{port:int}/start", HandleWorkerStart);
        management.MapPost("/api/instances/{port:int}/start", HandleWorkerStart);

        async Task<IResult> HandleWorkerStop(int port, [FromServices] MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken)
        {
            bool found = await supervisor.RequestWorkerStop(port, cancellationToken);
            if (!found)
                return Results.NotFound(new ActionResponse("stop", false, $"No dedicated worker configured on port {port}"));

            return Results.Ok(new ActionResponse("stop", true, $"Stop requested for worker on port {port}"));
        }

        management.MapPost("/instances/{port:int}/stop", HandleWorkerStop);
        management.MapPost("/api/instances/{port:int}/stop", HandleWorkerStop);

        async Task<IResult> HandleWorkerRestart(int port, [FromServices] MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken)
        {
            bool found = await supervisor.RequestWorkerRestart(port, cancellationToken);
            if (!found)
                return Results.NotFound(new ActionResponse("restart", false, $"No dedicated worker configured on port {port}"));

            return Results.Ok(new ActionResponse("restart", true, $"Restart requested for worker on port {port}"));
        }

        management.MapPost("/instances/{port:int}/restart", HandleWorkerRestart);
        management.MapPost("/api/instances/{port:int}/restart", HandleWorkerRestart);

        async Task<IResult> HandleWorkerDrain(int port, [FromQuery] string? reason, [FromServices] FleetDrainingService drainService, CancellationToken cancellationToken)
        {
            bool found = await drainService.MarkWorkerDrainingAsync(port, reason ?? "Operator requested drain", cancellationToken);
            if (!found)
                return Results.NotFound(new ActionResponse("drain", false, $"No dedicated worker configured on port {port} or worker is offline"));

            return Results.Ok(new ActionResponse("drain", true, $"Worker on port {port} marked for drain"));
        }

        management.MapPost("/instances/{port:int}/drain", HandleWorkerDrain);
        management.MapPost("/api/instances/{port:int}/drain", HandleWorkerDrain);

        IResult HandleWorkerCancelDrain(int port, [FromServices] FleetDrainingService drainService)
        {
            bool cancelled = drainService.CancelDraining(port);
            if (!cancelled)
                return Results.NotFound(new ActionResponse("cancel-drain", false, $"Worker on port {port} was not draining"));

            return Results.Ok(new ActionResponse("cancel-drain", true, $"Draining cancelled for worker on port {port}"));
        }

        management.MapPost("/instances/{port:int}/cancel-drain", HandleWorkerCancelDrain);
        management.MapPost("/api/instances/{port:int}/cancel-drain", HandleWorkerCancelDrain);

        IResult HandleWorkerLogs(int port, [FromQuery] int? tail, [FromServices] MatchServerManagerSupervisor supervisor)
        {
            DedicatedServerWorker? worker = supervisor.FindWorker(port);
            if (worker is null)
                return Results.NotFound(new ActionResponse("logs", false, $"No dedicated worker configured on port {port}"));

            int limit = tail.GetValueOrDefault(100);
            if (limit <= 0) limit = 100;
            if (limit > 500) limit = 500;

            IReadOnlyList<DedicatedServerLogLine> logs = worker.GetRecentLogs(limit);
            return Results.Ok(new DedicatedServerLogsResponse(worker.Port, worker.ServerName, logs));
        }

        management.MapGet("/instances/{port:int}/logs", HandleWorkerLogs);
        management.MapGet("/api/instances/{port:int}/logs", HandleWorkerLogs);

        async Task HandleWorkerLogsStream(int port, [FromQuery] int? tail, HttpContext context, [FromServices] MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken)
        {
            DedicatedServerWorker? worker = supervisor.FindWorker(port);
            if (worker is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json";
                string notFoundJson = JsonSerializer.Serialize(
                    new ActionResponse("logs-stream", false, $"No dedicated worker configured on port {port}"),
                    ControlPlaneJSONContext.Default.ActionResponse);
                await context.Response.WriteAsync(notFoundJson, cancellationToken).ConfigureAwait(false);
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            // 1. Initial backlog burst
            int limit = tail.GetValueOrDefault(50);
            if (limit > 0)
            {
                if (limit > 500) limit = 500;
                IReadOnlyList<DedicatedServerLogLine> initialLogs = worker.GetRecentLogs(limit);
                for (int i = 0; i < initialLogs.Count; i++)
                {
                    string json = JsonSerializer.Serialize(initialLogs[i], ControlPlaneJSONContext.Default.DedicatedServerLogLine);
                    await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
                }
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // 2. Active reactive subscription
            using DedicatedServerWorkerLogSubscription subscription = worker.SubscribeLogStream();
            try
            {
                using PeriodicTimer keepAliveTimer = new(TimeSpan.FromSeconds(15));
                Task<bool> readTask = subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();
                Task<bool> timerTask = keepAliveTimer.WaitForNextTickAsync(cancellationToken).AsTask();

                while (!cancellationToken.IsCancellationRequested)
                {
                    Task completed = await Task.WhenAny(readTask, timerTask).ConfigureAwait(false);
                    if (completed == readTask)
                    {
                        if (!await readTask.ConfigureAwait(false))
                        {
                            break;
                        }

                        while (subscription.Reader.TryRead(out DedicatedServerLogLine? line))
                        {
                            if (line is null) continue;
                            string json = JsonSerializer.Serialize(line, ControlPlaneJSONContext.Default.DedicatedServerLogLine);
                            await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
                        }
                        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                        readTask = subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    }
                    else if (completed == timerTask)
                    {
                        if (await timerTask.ConfigureAwait(false))
                        {
                            await context.Response.WriteAsync(": keepalive\n\n", cancellationToken).ConfigureAwait(false);
                            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                            timerTask = keepAliveTimer.WaitForNextTickAsync(cancellationToken).AsTask();
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal client disconnect
            }
        }

        management.MapGet("/instances/{port:int}/logs/stream", HandleWorkerLogsStream);
        management.MapGet("/api/instances/{port:int}/logs/stream", HandleWorkerLogsStream);

        async Task<IResult> HandleWorkerCommand(int port, [FromBody] ExecuteCommandRequest? request, [FromServices] MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken)
        {
            DedicatedServerWorker? worker = supervisor.FindWorker(port);
            if (worker is null)
                return Results.NotFound(new ExecuteCommandResponse(port, request?.Command ?? string.Empty, false, "none", $"No dedicated worker configured on port {port}"));

            if (request is null || string.IsNullOrWhiteSpace(request.Command))
                return Results.BadRequest(new ExecuteCommandResponse(port, string.Empty, false, "none", "Command must not be empty"));

            var (success, channel, message) = await supervisor.ExecuteCommandOnPortAsync(worker.Port, request.Command, cancellationToken);
            if (!success)
                return Results.BadRequest(new ExecuteCommandResponse(port, request.Command, false, channel, message));

            return Results.Ok(new ExecuteCommandResponse(port, request.Command, true, channel, message));
        }

        management.MapPost("/instances/{port:int}/command", HandleWorkerCommand);
        management.MapPost("/api/instances/{port:int}/command", HandleWorkerCommand);

        async Task<IResult> HandleServerRpc(
            int port,
            HttpContext context,
            [FromBody] JsonElement requestBody,
            [FromServices] MatchServerManagerSupervisor supervisor,
            [FromServices] IOptions<MatchServerManagerOptions> options,
            CancellationToken cancellationToken)
        {
            IPAddress? remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is not null && !IPAddress.IsLoopback(remoteIp))
            {
                bool isContainerE2E = false;
                if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("KONGOR_E2E_HARNESS") == "1")
                {
                    string masterServer = Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS")
                                           ?? Environment.GetEnvironmentVariable("KONGOR_MASTER_SERVER")
                                           ?? options.Value.MasterServerAddress;
                    bool nonProdMaster = !string.IsNullOrEmpty(masterServer) && !masterServer.Contains(":55555") && !masterServer.Contains(".newbs.io");
                    if (nonProdMaster && IsRfc1918Address(remoteIp))
                    {
                        isContainerE2E = true;
                    }
                }

                if (!isContainerE2E)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            string method = requestBody.TryGetProperty("method", out JsonElement mEl) ? mEl.GetString() ?? string.Empty : string.Empty;

            if (string.Equals(method, "trigger_match_victory", StringComparison.OrdinalIgnoreCase))
            {
                string? e2eHarness = Environment.GetEnvironmentVariable("KONGOR_E2E_HARNESS");
                if (e2eHarness != "1")
                {
                    return Results.Content(
                        "{\"error\":{\"code\":403,\"message\":\"403 E2E_ONLY: trigger_match_victory requires KONGOR_E2E_HARNESS=1\"}}",
                        "application/json",
                        Encoding.UTF8,
                        StatusCodes.Status403Forbidden);
                }

                string masterServer = Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS")
                                       ?? Environment.GetEnvironmentVariable("KONGOR_MASTER_SERVER")
                                       ?? options.Value.MasterServerAddress;
                if (!string.IsNullOrEmpty(masterServer) && (masterServer.Contains(":55555") || masterServer.Contains(".newbs.io")))
                {
                    return Results.Content(
                        "{\"error\":{\"code\":403,\"message\":\"403 FORBIDDEN: trigger_match_victory blocked on production master server target\"}}",
                        "application/json",
                        Encoding.UTF8,
                        StatusCodes.Status403Forbidden);
                }

                var (success, channel, message) = await supervisor.ExecuteCommandOnPortAsync(port, "ServerEndGame 1", cancellationToken);
                string respJson = $"{{\"success\":true,\"method\":\"trigger_match_victory\",\"port\":{port},\"channel\":\"{channel}\",\"dispatched\":{(success ? "true" : "false")},\"command\":\"ServerEndGame 1\"}}";
                return Results.Content(respJson, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
            }
            else if (string.Equals(method, "get_server_world_state", StringComparison.OrdinalIgnoreCase))
            {
                bool hasKeyframe = ServerKeyframeSharedMemory.TryReadKeyframe(port, out KongorServerKeyframe? keyframe);
                DedicatedServerWorker? worker = supervisor.FindWorker(port);
                string keyframeJson = keyframe != null
                    ? $"{{\"sequence\":{keyframe.Sequence},\"serverTick\":{keyframe.ServerTick},\"matchId\":{keyframe.MatchId},\"payloadSizeBytes\":{keyframe.StatePayload.Length}}}"
                    : "null";
                string respJson = $"{{\"success\":true,\"method\":\"get_server_world_state\",\"port\":{port},\"workerRunning\":{(worker?.IsRunning == true ? "true" : "false")},\"hasKeyframe\":{(hasKeyframe ? "true" : "false")},\"keyframe\":{keyframeJson}}}";
                return Results.Content(respJson, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
            }
            else if (string.Equals(method, "apply_server_fixture", StringComparison.OrdinalIgnoreCase))
            {
                string payload = requestBody.TryGetProperty("fixture", out JsonElement fEl) ? fEl.GetRawText() : "{}";
                string safePayload = payload.Replace("\\", "\\\\").Replace("'", "\\'");
                string fixtureCmd = $"Script if TAO and TAO.Dispatch then TAO.Dispatch('apply_fixture', '{safePayload}') elseif Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot() end";
                var (success, channel, message) = await supervisor.ExecuteCommandOnPortAsync(port, fixtureCmd, cancellationToken);
                string respJson = $"{{\"success\":{(success ? "true" : "false")},\"method\":\"apply_server_fixture\",\"port\":{port},\"channel\":\"{channel}\"}}";
                return Results.Content(respJson, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
            }
            else
            {
                return Results.Content(
                    $"{{\"error\":\"Unsupported method '{method}'\"}}",
                    "application/json",
                    Encoding.UTF8,
                    StatusCodes.Status400BadRequest);
            }
        }

        management.MapPost("/admin/server/{port:int}/rpc", HandleServerRpc);
        management.MapPost("/api/admin/server/{port:int}/rpc", HandleServerRpc);

        async Task<IResult> HandleProxyRestart([FromServices] UDPProxyService proxy, CancellationToken cancellationToken)
        {
            if (proxy.IsEnabled is false)
                return TypedResults.BadRequest(new ActionResponse("proxy-restart", false, "The Proxy Is Disabled In Configuration"));

            bool restarted = await proxy.RequestRestart(cancellationToken);
            if (restarted is false)
                return TypedResults.Problem("Failed To Rebind Proxy Forwarders", statusCode: StatusCodes.Status500InternalServerError);

            return TypedResults.Ok(new ActionResponse("proxy-restart", true, "Proxy Restarted"));
        }

        management.MapPost("/proxy/restart", HandleProxyRestart);
        management.MapPost("/api/proxy/restart", HandleProxyRestart);

        // Voice Moderation Sanction Endpoints
        IResult HandleGetActiveMutes([FromServices] IVoiceMuteRegistry muteRegistry)
        {
            IReadOnlyList<VoiceMuteEntry> activeMutes = muteRegistry.GetActiveMutes();
            return TypedResults.Ok(activeMutes);
        }

        management.MapGet("/api/moderation/mutes", HandleGetActiveMutes);

        IResult HandleSetMute([FromBody] SetVoiceMuteRequest? request, [FromServices] IVoiceMuteRegistry muteRegistry)
        {
            if (request is null || request.AccountId == 0)
            {
                return Results.BadRequest(new ActionResponse("set-mute", false, "AccountId must be greater than zero."));
            }

            DateTimeOffset? expiration = request.DurationHours.HasValue && request.DurationHours.Value > 0
                ? TimeProvider.System.GetUtcNow().AddHours(request.DurationHours.Value)
                : null;

            muteRegistry.SetMute(request.AccountId, expiration, request.Reason);
            return TypedResults.Ok(new ActionResponse("set-mute", true, $"Account {request.AccountId} voice muted."));
        }

        management.MapPost("/api/moderation/mutes", HandleSetMute);
        management.MapPost("/api/moderation/mutes/internal", HandleSetMute);

        IResult HandleRemoveMute(long accountId, [FromServices] IVoiceMuteRegistry muteRegistry)
        {
            if (accountId <= 0)
            {
                return Results.BadRequest(new ActionResponse("remove-mute", false, "AccountId must be greater than zero."));
            }

            muteRegistry.RemoveMute((uint)accountId);
            return TypedResults.Ok(new ActionResponse("remove-mute", true, $"Account {accountId} voice unmuted."));
        }

        management.MapDelete("/api/moderation/mutes/{accountId:long}", HandleRemoveMute);
        management.MapDelete("/api/moderation/mutes/internal/{accountId:long}", HandleRemoveMute);

        // Voice Ignore Endpoints
        IResult HandleSetIgnore([FromBody] SetVoiceIgnoreRequest? request, [FromServices] IVoiceMuteRegistry muteRegistry)
        {
            if (request is null || request.ListenerAccountId == 0 || request.SpeakerAccountId == 0)
            {
                return Results.BadRequest(new ActionResponse("set-voice-ignore", false, "ListenerAccountId and SpeakerAccountId must be greater than zero."));
            }

            muteRegistry.SetIgnore(request.ListenerAccountId, request.SpeakerAccountId, request.Ignored);
            return TypedResults.Ok(new ActionResponse("set-voice-ignore", true, $"Voice ignore relationship ({request.ListenerAccountId} -> {request.SpeakerAccountId}: {request.Ignored}) registered."));
        }

        management.MapPost("/api/voice/ignores", HandleSetIgnore);
        management.MapPost("/api/voice/ignores/internal", HandleSetIgnore);

        IResult HandleRemoveIgnore(long listenerAccountId, long speakerAccountId, [FromServices] IVoiceMuteRegistry muteRegistry)
        {
            if (listenerAccountId <= 0 || speakerAccountId <= 0)
            {
                return Results.BadRequest(new ActionResponse("remove-voice-ignore", false, "ListenerAccountId and SpeakerAccountId must be greater than zero."));
            }

            muteRegistry.SetIgnore((uint)listenerAccountId, (uint)speakerAccountId, false);
            return TypedResults.Ok(new ActionResponse("remove-voice-ignore", true, $"Voice ignore relationship ({listenerAccountId} -> {speakerAccountId}) removed."));
        }

        management.MapDelete("/api/voice/ignores/{listenerAccountId:long}/{speakerAccountId:long}", HandleRemoveIgnore);
        management.MapDelete("/api/voice/ignores/internal/{listenerAccountId:long}/{speakerAccountId:long}", HandleRemoveIgnore);

        IResult HandleGetRlStatus([FromServices] COMPEL.Services.Telemetry.RlTrajectoryCollectorService collector)
            => TypedResults.Ok(collector.GetStatusSnapshot());

        management.MapGet("/rl/status", HandleGetRlStatus);
        management.MapGet("/api/rl/status", HandleGetRlStatus);

        IResult HandleTrainRl(
            [FromBody] COMPEL.Services.Telemetry.RlTrainRequest? request,
            [FromServices] COMPEL.Services.Telemetry.RlTrajectoryCollectorService collector)
        {
            var summary = collector.ForceTrainFromRingBuffer(request?.Epochs ?? 6);
            return TypedResults.Ok(new COMPEL.Services.Telemetry.RlTrainResponse(true, summary));
        }

        management.MapPost("/rl/train", HandleTrainRl);
        management.MapPost("/api/rl/train", HandleTrainRl);

        IResult HandleRollbackRl(
            [FromBody] COMPEL.Services.Telemetry.RlRollbackRequest? request,
            [FromServices] ASPIRE.Common.MachineLearning.RlArenaCoordinator arena)
        {
            var restored = arena.RollbackChampion(request?.TargetGeneration);
            return TypedResults.Ok(new COMPEL.Services.Telemetry.RlRollbackResponse(true, restored.Generation));
        }

        management.MapPost("/rl/rollback", HandleRollbackRl);
        management.MapPost("/api/rl/rollback", HandleRollbackRl);

        IResult HandleExportRl(
            [FromServices] ASPIRE.Common.MachineLearning.RlArenaCoordinator arena,
            [FromServices] IConfiguration configuration)
        {
            string defaultBaseline = Path.Combine(
                Path.GetDirectoryName(COMPEL.Services.Telemetry.RlTrajectoryCollectorService.ResolveRuntimeDirectory(configuration)) ?? ".",
                "rl_weights_baseline.lua");
            string written = arena.ExportChampionToBaselineLua(defaultBaseline);
            var state = arena.GetArenaState();
            return TypedResults.Ok(new COMPEL.Services.Telemetry.RlExportResponse(true, written, state.ChampionGeneration));
        }

        management.MapPost("/rl/export", HandleExportRl);
        management.MapPost("/api/rl/export", HandleExportRl);

        return application;
    }

    private static bool IsRfc1918Address(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
        }
        return false;
    }
}

public sealed record SetVoiceMuteRequest(uint AccountId, int? DurationHours = null, string? Reason = null);
public sealed record SetVoiceIgnoreRequest(uint ListenerAccountId, uint SpeakerAccountId, bool Ignored = true);


