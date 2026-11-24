// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Severity level for detected state desynchronizations between client and server.
/// </summary>
public enum DesyncSeverity
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>
/// Functional category of the desynchronization delta.
/// </summary>
public enum DeltaCategory
{
    SpatialPosition,
    HeroVitality,
    Economy,
    Progression,
    Inventory,
    Cooldowns,
    MatchPhase,
    Clock,
    Score,
    WorldEntity
}

/// <summary>
/// Individual property discrepancy between client-perceived state and server authoritative state.
/// </summary>
public sealed record ReconciliationDelta
{
    [JsonPropertyName("property")]
    public string Property { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("clientValue")]
    public object? ClientValue { get; init; }

    [JsonPropertyName("serverValue")]
    public object? ServerValue { get; init; }

    [JsonPropertyName("driftMagnitude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DriftMagnitude { get; init; }

    [JsonPropertyName("tolerance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Tolerance { get; init; }

    [JsonPropertyName("inTolerance")]
    public bool InTolerance { get; init; } = true;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "none";

    [JsonPropertyName("remediationHint")]
    public string RemediationHint { get; init; } = string.Empty;
}

/// <summary>
/// Tolerance parameters for state reconciliation.
/// </summary>
public sealed record ReconciliationOptions
{
    public double ToleranceEpsilon { get; init; } = 50.0;
    public double MaxClockDriftSeconds { get; init; } = 1.0;
    public double HealthTolerance { get; init; } = 2.0;
    public double ManaTolerance { get; init; } = 2.0;
    public int GoldTolerance { get; init; } = 0;
    public double CooldownToleranceSeconds { get; init; } = 0.5;
}

/// <summary>
/// Comprehensive reconciliation report comparing client state with server authoritative state.
/// </summary>
public sealed class ReconciliationReport
{
    [JsonPropertyName("isSynchronized")]
    public bool IsSynchronized { get; set; } = true;

    [JsonPropertyName("desyncSeverity")]
    public string DesyncSeverity { get; set; } = "none";

    [JsonPropertyName("clockDriftSeconds")]
    public double ClockDriftSeconds { get; set; } = 0.0;

    [JsonPropertyName("positionDrift")]
    public double PositionDrift { get; set; } = 0.0;

    [JsonPropertyName("totalDeltas")]
    public int TotalDeltas => Deltas.Count;

    [JsonPropertyName("outOfToleranceCount")]
    public int OutOfToleranceCount => Deltas.Count(d => !d.InTolerance);

    [JsonPropertyName("deltas")]
    public List<ReconciliationDelta> Deltas { get; set; } = [];

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "States are synchronized.";

    [JsonPropertyName("remediationAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemediationAction { get; set; }

    [JsonPropertyName("remediationHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemediationHint { get; set; }

    [JsonPropertyName("authoritativeSource")]
    public string AuthoritativeSource { get; set; } = "server";

    [JsonPropertyName("clientState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ClientState { get; set; }

    [JsonPropertyName("serverState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ServerState { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
/// Service interface for comparing and reconciling client-perceived local match/world state
/// against dedicated server ground-truth authoritative simulation state.
/// </summary>
public interface IStateReconciliationService
{
    ReconciliationReport ReconcileMatchState(JsonElement clientState, JsonElement serverState, ReconciliationOptions? options = null);
    ReconciliationReport ReconcileSnapshots(GameSnapshotDto clientSnapshot, GameSnapshotDto serverSnapshot, ReconciliationOptions? options = null);
    ReconciliationReport ReconcileWorldState(JsonElement clientWorldState, JsonElement serverWorldState, ReconciliationOptions? options = null);
}
