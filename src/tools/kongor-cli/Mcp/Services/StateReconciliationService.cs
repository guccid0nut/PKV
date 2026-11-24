// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Authoritative state reconciliation service for Project Kongor.
/// Reconciles client-perceived local simulation state against dedicated server authoritative ground-truth.
/// Detects spatial position drift, gold prediction divergence, vitality desyncs, and clock drift.
/// </summary>
public sealed class StateReconciliationService : IStateReconciliationService
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public StateReconciliationService(TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    private void Log(string msg) => _logger?.Invoke($"[StateReconciliation] {msg}");

    public ReconciliationReport ReconcileMatchState(JsonElement clientState, JsonElement serverState, ReconciliationOptions? options = null)
    {
        options ??= new ReconciliationOptions();
        var report = new ReconciliationReport
        {
            Timestamp = _timeProvider.GetUtcNow().ToString("o"),
            ClientState = clientState,
            ServerState = serverState
        };

        // 1. Clock & Match Time Drift
        double clientTime = ExtractDouble(clientState, "matchTimeSeconds", "matchTime", "time");
        double serverTime = ExtractDouble(serverState, "matchTimeSeconds", "matchTime", "time");
        double clockDrift = Math.Abs(clientTime - serverTime);
        report.ClockDriftSeconds = clockDrift;

        if (clockDrift > options.MaxClockDriftSeconds)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "match.matchTimeSeconds",
                Category = DeltaCategory.Clock.ToString(),
                ClientValue = clientTime,
                ServerValue = serverTime,
                DriftMagnitude = clockDrift,
                Tolerance = options.MaxClockDriftSeconds,
                InTolerance = false,
                Severity = clockDrift > 5.0 ? "high" : "low",
                RemediationHint = $"Simulation clock drift ({clockDrift:F2}s) exceeds threshold ({options.MaxClockDriftSeconds:F2}s). Allow tick extrapolation to settle."
            });
        }

        // 2. Game Phase Verification
        string? clientPhase = ExtractString(clientState, "gamePhase", "phase");
        string? serverPhase = ExtractString(serverState, "gamePhase", "phase");
        if (!string.IsNullOrWhiteSpace(clientPhase) && !string.IsNullOrWhiteSpace(serverPhase))
        {
            int cPhaseNum = ExtractPhaseNumber(clientPhase);
            int sPhaseNum = ExtractPhaseNumber(serverPhase);
            if (cPhaseNum != sPhaseNum && cPhaseNum > 0 && sPhaseNum > 0)
            {
                report.Deltas.Add(new ReconciliationDelta
                {
                    Property = "match.gamePhase",
                    Category = DeltaCategory.MatchPhase.ToString(),
                    ClientValue = clientPhase,
                    ServerValue = serverPhase,
                    InTolerance = false,
                    Severity = "high",
                    RemediationHint = $"Client phase '{clientPhase}' (Phase {cPhaseNum}) diverges from authoritative server phase '{serverPhase}' (Phase {sPhaseNum})."
                });
            }
        }

        // 3. Match ID Discrepancy
        long cMatchId = ExtractLong(clientState, "matchId");
        long sMatchId = ExtractLong(serverState, "matchId");
        if (cMatchId > 0 && sMatchId > 0 && cMatchId != sMatchId)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "match.matchId",
                Category = DeltaCategory.MatchPhase.ToString(),
                ClientValue = cMatchId,
                ServerValue = sMatchId,
                InTolerance = false,
                Severity = "critical",
                RemediationHint = $"Client matchId ({cMatchId}) does not match server matchId ({sMatchId}). Reconnect client to target server."
            });
        }

        // 4. Hero Entity Reconciliation
        JsonElement cHero = ExtractSubObject(clientState, "hero");
        JsonElement sHero = ExtractSubObject(serverState, "hero");

        if (cHero.ValueKind == JsonValueKind.Object || sHero.ValueKind == JsonValueKind.Object)
        {
            ReconcileHeroEntities(cHero, sHero, report, options);
        }

        // 5. Position Drift Reconciliation (if not already checked via hero entity)
        if (!report.Deltas.Any(d => d.Property.EndsWith(".position", StringComparison.OrdinalIgnoreCase)))
        {
            (double cX, double cY, double cZ, bool cHasPos) = ExtractPosition(clientState, default);
            (double sX, double sY, double sZ, bool sHasPos) = ExtractPosition(serverState, default);

            if (cHasPos && sHasPos)
            {
                double dx = cX - sX;
                double dy = cY - sY;
                double dz = cZ - sZ;
                double posDist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                if (posDist > report.PositionDrift) report.PositionDrift = posDist;

                bool inTol = posDist <= options.ToleranceEpsilon;
                if (!inTol)
                {
                    string severity = posDist >= 1000.0 ? "critical" : (posDist >= 300.0 ? "high" : "medium");
                    report.Deltas.Add(new ReconciliationDelta
                    {
                        Property = "hero.position",
                        Category = DeltaCategory.SpatialPosition.ToString(),
                        ClientValue = new { x = cX, y = cY, z = cZ },
                        ServerValue = new { x = sX, y = sY, z = sZ },
                        DriftMagnitude = posDist,
                        Tolerance = options.ToleranceEpsilon,
                        InTolerance = false,
                        Severity = severity,
                        RemediationHint = $"Hero position drift of {posDist:F1} units exceeds tolerance ({options.ToleranceEpsilon:F1}). Dispatch 'kongor_game_action action:teleport' to resync."
                    });
                }
            }
        }

        FinalizeReport(report);
        return report;
    }

    public ReconciliationReport ReconcileSnapshots(GameSnapshotDto clientSnapshot, GameSnapshotDto serverSnapshot, ReconciliationOptions? options = null)
    {
        options ??= new ReconciliationOptions();
        var report = new ReconciliationReport
        {
            Timestamp = _timeProvider.GetUtcNow().ToString("o"),
            ClientState = clientSnapshot,
            ServerState = serverSnapshot
        };

        // Clock & Phase
        double clockDrift = Math.Abs(clientSnapshot.MatchTimeSeconds - serverSnapshot.MatchTimeSeconds);
        report.ClockDriftSeconds = clockDrift;
        if (clockDrift > options.MaxClockDriftSeconds)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "match.matchTimeSeconds",
                Category = DeltaCategory.Clock.ToString(),
                ClientValue = clientSnapshot.MatchTimeSeconds,
                ServerValue = serverSnapshot.MatchTimeSeconds,
                DriftMagnitude = clockDrift,
                Tolerance = options.MaxClockDriftSeconds,
                InTolerance = false,
                Severity = clockDrift > 5.0 ? "high" : "low",
                RemediationHint = $"Snapshot time divergence ({clockDrift:F2}s)."
            });
        }

        if (clientSnapshot.GamePhase != serverSnapshot.GamePhase)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "match.gamePhase",
                Category = DeltaCategory.MatchPhase.ToString(),
                ClientValue = clientSnapshot.GamePhase,
                ServerValue = serverSnapshot.GamePhase,
                InTolerance = false,
                Severity = "high",
                RemediationHint = $"Game phase mismatch: client={clientSnapshot.GamePhase}, server={serverSnapshot.GamePhase}."
            });
        }

        // Hero Name
        if (!string.Equals(clientSnapshot.Hero, serverSnapshot.Hero, StringComparison.OrdinalIgnoreCase))
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "hero.name",
                Category = DeltaCategory.Progression.ToString(),
                ClientValue = clientSnapshot.Hero,
                ServerValue = serverSnapshot.Hero,
                InTolerance = false,
                Severity = "critical",
                RemediationHint = $"Hero mismatch: '{clientSnapshot.Hero}' vs server authoritative '{serverSnapshot.Hero}'."
            });
        }

        // Level
        if (clientSnapshot.Level != serverSnapshot.Level)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "hero.level",
                Category = DeltaCategory.Progression.ToString(),
                ClientValue = clientSnapshot.Level,
                ServerValue = serverSnapshot.Level,
                DriftMagnitude = Math.Abs(clientSnapshot.Level - serverSnapshot.Level),
                InTolerance = false,
                Severity = "high",
                RemediationHint = $"Level divergence: client {clientSnapshot.Level} != server {serverSnapshot.Level}."
            });
        }

        // Gold
        int goldDiff = Math.Abs(clientSnapshot.Gold - serverSnapshot.Gold);
        if (goldDiff > options.GoldTolerance)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "hero.gold",
                Category = DeltaCategory.Economy.ToString(),
                ClientValue = clientSnapshot.Gold,
                ServerValue = serverSnapshot.Gold,
                DriftMagnitude = goldDiff,
                Tolerance = options.GoldTolerance,
                InTolerance = false,
                Severity = goldDiff > 500 ? "high" : "medium",
                RemediationHint = $"Gold discrepancy: client {clientSnapshot.Gold} vs server {serverSnapshot.Gold}."
            });
        }

        // Health
        double hpDiff = Math.Abs(clientSnapshot.Health - serverSnapshot.Health);
        if (hpDiff > options.HealthTolerance)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "hero.health",
                Category = DeltaCategory.HeroVitality.ToString(),
                ClientValue = clientSnapshot.Health,
                ServerValue = serverSnapshot.Health,
                DriftMagnitude = hpDiff,
                Tolerance = options.HealthTolerance,
                InTolerance = false,
                Severity = hpDiff > 100.0 ? "high" : "low",
                RemediationHint = $"Health discrepancy: client {clientSnapshot.Health:F1} vs server {serverSnapshot.Health:F1}."
            });
        }

        // Mana
        double mpDiff = Math.Abs(clientSnapshot.Mana - serverSnapshot.Mana);
        if (mpDiff > options.ManaTolerance)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "hero.mana",
                Category = DeltaCategory.HeroVitality.ToString(),
                ClientValue = clientSnapshot.Mana,
                ServerValue = serverSnapshot.Mana,
                DriftMagnitude = mpDiff,
                Tolerance = options.ManaTolerance,
                InTolerance = false,
                Severity = mpDiff > 50.0 ? "medium" : "low",
                RemediationHint = $"Mana discrepancy: client {clientSnapshot.Mana:F1} vs server {serverSnapshot.Mana:F1}."
            });
        }

        // Position
        double dx = clientSnapshot.X - serverSnapshot.X;
        double dy = clientSnapshot.Y - serverSnapshot.Y;
        double dz = clientSnapshot.Z - serverSnapshot.Z;
        double dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        report.PositionDrift = dist;

        if (dist > options.ToleranceEpsilon)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = "hero.position",
                Category = DeltaCategory.SpatialPosition.ToString(),
                ClientValue = new { x = clientSnapshot.X, y = clientSnapshot.Y, z = clientSnapshot.Z },
                ServerValue = new { x = serverSnapshot.X, y = serverSnapshot.Y, z = serverSnapshot.Z },
                DriftMagnitude = dist,
                Tolerance = options.ToleranceEpsilon,
                InTolerance = false,
                Severity = dist >= 1000.0 ? "critical" : (dist >= 300.0 ? "high" : "medium"),
                RemediationHint = $"Snapshot spatial position drift of {dist:F1} units exceeds {options.ToleranceEpsilon:F1}."
            });
        }

        FinalizeReport(report);
        return report;
    }

    public ReconciliationReport ReconcileWorldState(JsonElement clientWorldState, JsonElement serverWorldState, ReconciliationOptions? options = null)
    {
        options ??= new ReconciliationOptions();
        var report = new ReconciliationReport
        {
            Timestamp = _timeProvider.GetUtcNow().ToString("o"),
            ClientState = clientWorldState,
            ServerState = serverWorldState
        };

        // Match clock / time ms
        long cTimeMs = ExtractLong(clientWorldState, "gameTimeMs", "timeMs");
        long sTimeMs = ExtractLong(serverWorldState, "gameTimeMs", "timeMs");
        if (cTimeMs > 0 && sTimeMs > 0)
        {
            double driftSec = Math.Abs(cTimeMs - sTimeMs) / 1000.0;
            report.ClockDriftSeconds = driftSec;
            if (driftSec > options.MaxClockDriftSeconds)
            {
                report.Deltas.Add(new ReconciliationDelta
                {
                    Property = "world.gameTimeMs",
                    Category = DeltaCategory.Clock.ToString(),
                    ClientValue = cTimeMs,
                    ServerValue = sTimeMs,
                    DriftMagnitude = driftSec,
                    Tolerance = options.MaxClockDriftSeconds,
                    InTolerance = false,
                    Severity = driftSec > 5.0 ? "high" : "low",
                    RemediationHint = $"World simulation clock divergence ({driftSec:F2}s)."
                });
            }
        }

        // Reconcile Team 1 Heroes
        ReconcileHeroList(clientWorldState, serverWorldState, "team1Heroes", report, options);
        // Reconcile Team 2 Heroes
        ReconcileHeroList(clientWorldState, serverWorldState, "team2Heroes", report, options);

        FinalizeReport(report);
        return report;
    }

    private void ReconcileHeroList(JsonElement clientWorld, JsonElement serverWorld, string listProp, ReconciliationReport report, ReconciliationOptions options)
    {
        if (!clientWorld.TryGetProperty(listProp, out JsonElement cList) || cList.ValueKind != JsonValueKind.Array) return;
        if (!serverWorld.TryGetProperty(listProp, out JsonElement sList) || sList.ValueKind != JsonValueKind.Array) return;

        var sHeroes = sList.EnumerateArray().ToList();

        int index = 0;
        foreach (var cHero in cList.EnumerateArray())
        {
            int slot = cHero.TryGetProperty("slot", out JsonElement slotEl) && slotEl.TryGetInt32(out int sl) ? sl : index;
            var sHero = sHeroes.FirstOrDefault(h => h.TryGetProperty("slot", out JsonElement sSlEl) && sSlEl.TryGetInt32(out int ssl) && ssl == slot);

            if (sHero.ValueKind == JsonValueKind.Undefined && index < sHeroes.Count)
            {
                sHero = sHeroes[index];
            }

            if (sHero.ValueKind == JsonValueKind.Object)
            {
                ReconcileHeroEntities(cHero, sHero, report, options, prefix: $"{listProp}[{slot}]");
            }
            index++;
        }
    }

    private void ReconcileHeroEntities(JsonElement cHero, JsonElement sHero, ReconciliationReport report, ReconciliationOptions options, string prefix = "hero")
    {
        // 1. Hero Name
        string? cName = ExtractString(cHero, "heroName", "name", "hero");
        string? sName = ExtractString(sHero, "heroName", "name", "hero");
        if (!string.IsNullOrWhiteSpace(cName) && !string.IsNullOrWhiteSpace(sName) &&
            !string.Equals(cName, sName, StringComparison.OrdinalIgnoreCase))
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = $"{prefix}.name",
                Category = DeltaCategory.Progression.ToString(),
                ClientValue = cName,
                ServerValue = sName,
                InTolerance = false,
                Severity = "critical",
                RemediationHint = $"Hero selection mismatch: client '{cName}' vs server '{sName}'."
            });
        }

        // 2. Level
        int cLevel = ExtractInt(cHero, "level");
        int sLevel = ExtractInt(sHero, "level");
        if (cLevel > 0 && sLevel > 0 && cLevel != sLevel)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = $"{prefix}.level",
                Category = DeltaCategory.Progression.ToString(),
                ClientValue = cLevel,
                ServerValue = sLevel,
                DriftMagnitude = Math.Abs(cLevel - sLevel),
                InTolerance = false,
                Severity = "high",
                RemediationHint = $"Hero level mismatch: client {cLevel} vs server {sLevel}."
            });
        }

        // 3. Gold / Networth
        int cGold = ExtractInt(cHero, "gold", "networth");
        int sGold = ExtractInt(sHero, "gold", "networth");
        int goldDiff = Math.Abs(cGold - sGold);
        if (cGold > 0 && sGold > 0 && goldDiff > options.GoldTolerance)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = $"{prefix}.gold",
                Category = DeltaCategory.Economy.ToString(),
                ClientValue = cGold,
                ServerValue = sGold,
                DriftMagnitude = goldDiff,
                Tolerance = options.GoldTolerance,
                InTolerance = false,
                Severity = goldDiff > 500 ? "high" : "medium",
                RemediationHint = $"Gold desynchronization: client has {cGold}, server authoritative balance is {sGold}."
            });
        }

        // 4. Health
        double cHp = ExtractDouble(cHero, "health", "hp");
        double sHp = ExtractDouble(sHero, "health", "hp");
        double hpDiff = Math.Abs(cHp - sHp);
        if (cHp > 0 && sHp > 0 && hpDiff > options.HealthTolerance)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = $"{prefix}.health",
                Category = DeltaCategory.HeroVitality.ToString(),
                ClientValue = cHp,
                ServerValue = sHp,
                DriftMagnitude = hpDiff,
                Tolerance = options.HealthTolerance,
                InTolerance = false,
                Severity = hpDiff > 100.0 ? "high" : "low",
                RemediationHint = $"Health pool divergence: client={cHp:F1} hp, server={sHp:F1} hp."
            });
        }

        // 5. Mana
        double cMp = ExtractDouble(cHero, "mana", "mp");
        double sMp = ExtractDouble(sHero, "mana", "mp");
        double mpDiff = Math.Abs(cMp - sMp);
        if (cMp > 0 && sMp > 0 && mpDiff > options.ManaTolerance)
        {
            report.Deltas.Add(new ReconciliationDelta
            {
                Property = $"{prefix}.mana",
                Category = DeltaCategory.HeroVitality.ToString(),
                ClientValue = cMp,
                ServerValue = sMp,
                DriftMagnitude = mpDiff,
                Tolerance = options.ManaTolerance,
                InTolerance = false,
                Severity = mpDiff > 50.0 ? "medium" : "low",
                RemediationHint = $"Mana pool divergence: client={cMp:F1} mp, server={sMp:F1} mp."
            });
        }

        // 6. Sub-entity spatial position
        (double cX, double cY, double cZ, bool cHasPos) = ExtractPosition(cHero, default);
        (double sX, double sY, double sZ, bool sHasPos) = ExtractPosition(sHero, default);
        if (cHasPos && sHasPos)
        {
            double dx = cX - sX;
            double dy = cY - sY;
            double dz = cZ - sZ;
            double dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (dist > report.PositionDrift) report.PositionDrift = dist;

            if (dist > options.ToleranceEpsilon)
            {
                report.Deltas.Add(new ReconciliationDelta
                {
                    Property = $"{prefix}.position",
                    Category = DeltaCategory.SpatialPosition.ToString(),
                    ClientValue = new { x = cX, y = cY, z = cZ },
                    ServerValue = new { x = sX, y = sY, z = sZ },
                    DriftMagnitude = dist,
                    Tolerance = options.ToleranceEpsilon,
                    InTolerance = false,
                    Severity = dist >= 1000.0 ? "critical" : (dist >= 300.0 ? "high" : "medium"),
                    RemediationHint = $"Spatial position divergence of {dist:F1} units exceeds threshold {options.ToleranceEpsilon:F1}."
                });
            }
        }
    }

    private static void FinalizeReport(ReconciliationReport report)
    {
        var outOfTolerance = report.Deltas.Where(d => !d.InTolerance).ToList();
        if (outOfTolerance.Count == 0)
        {
            report.IsSynchronized = true;
            report.DesyncSeverity = "none";
            report.Summary = "Client-perceived simulation and dedicated server authoritative state are synchronized within tolerances.";
            report.RemediationAction = "none";
            report.RemediationHint = "No desynchronization detected.";
            return;
        }

        report.IsSynchronized = false;

        // Determine highest severity
        if (outOfTolerance.Any(d => d.Severity == "critical")) report.DesyncSeverity = "critical";
        else if (outOfTolerance.Any(d => d.Severity == "high")) report.DesyncSeverity = "high";
        else if (outOfTolerance.Any(d => d.Severity == "medium")) report.DesyncSeverity = "medium";
        else report.DesyncSeverity = "low";

        // Determine remediation action
        bool hasPositionDesync = outOfTolerance.Any(d => d.Category == DeltaCategory.SpatialPosition.ToString());
        bool hasVitalityOrEconomy = outOfTolerance.Any(d => d.Category is "Economy" or "HeroVitality" or "Progression");

        if (hasPositionDesync)
        {
            report.RemediationAction = "kongor_game_action action:teleport";
            report.RemediationHint = "Spatial position drift detected. Teleport client hero to authoritative server coordinates or allow K2 interpolation tick to correct.";
        }
        else if (hasVitalityOrEconomy)
        {
            report.RemediationAction = "kongor_game_snapshot action:restore";
            report.RemediationHint = "Hero stats or progression diverged. Reapply authoritative state snapshot or synchronize via dedicated server cvar.";
        }
        else
        {
            report.RemediationAction = "wait_for_server_tick";
            report.RemediationHint = "Minor clock or simulation frame drift. Allow network packets to catch up.";
        }

        report.Summary = $"Detected {outOfTolerance.Count} desynchronization(s) (Severity: {report.DesyncSeverity}): {string.Join("; ", outOfTolerance.Select(d => $"{d.Property}: client={d.ClientValue}, server={d.ServerValue}"))}";
    }

    #region JSON Extraction Helpers

    private static double ExtractDouble(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0.0;
        foreach (string name in propertyNames)
        {
            if (element.TryGetProperty(name, out JsonElement el) && el.TryGetDouble(out double d)) return d;
        }
        return 0.0;
    }

    private static long ExtractLong(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0L;
        foreach (string name in propertyNames)
        {
            if (element.TryGetProperty(name, out JsonElement el) && el.TryGetInt64(out long l)) return l;
        }
        return 0L;
    }

    private static int ExtractInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        foreach (string name in propertyNames)
        {
            if (element.TryGetProperty(name, out JsonElement el) && el.TryGetInt32(out int i)) return i;
        }
        return 0;
    }

    private static string? ExtractString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (string name in propertyNames)
        {
            if (element.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        return null;
    }

    private static JsonElement ExtractSubObject(JsonElement element, string propName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propName, out JsonElement sub) &&
            sub.ValueKind == JsonValueKind.Object)
        {
            return sub;
        }
        return default;
    }

    private static (double X, double Y, double Z, bool HasPos) ExtractPosition(JsonElement root, JsonElement sub)
    {
        JsonElement target = sub.ValueKind == JsonValueKind.Object ? sub : root;
        if (target.ValueKind != JsonValueKind.Object) return (0, 0, 0, false);

        if (target.TryGetProperty("position", out JsonElement posEl) && posEl.ValueKind == JsonValueKind.Object)
        {
            double x = posEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double xv) ? xv : 0;
            double y = posEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double yv) ? yv : 0;
            double z = posEl.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetDouble(out double zv) ? zv : 0;
            return (x, y, z, true);
        }

        if (target.TryGetProperty("pos", out posEl) && posEl.ValueKind == JsonValueKind.Object)
        {
            double x = posEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double xv) ? xv : 0;
            double y = posEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double yv) ? yv : 0;
            double z = posEl.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetDouble(out double zv) ? zv : 0;
            return (x, y, z, true);
        }

        if (target.TryGetProperty("x", out JsonElement rootX) && rootX.TryGetDouble(out double rx) &&
            target.TryGetProperty("y", out JsonElement rootY) && rootY.TryGetDouble(out double ry))
        {
            double rz = target.TryGetProperty("z", out JsonElement rootZ) && rootZ.TryGetDouble(out double rzv) ? rzv : 0;
            return (rx, ry, rz, true);
        }

        return (0, 0, 0, false);
    }

    private static int ExtractPhaseNumber(string phase)
    {
        if (phase.Contains("Phase 3", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("Phase3", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("Active Match", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("Combat", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (phase.Contains("Phase 2", StringComparison.OrdinalIgnoreCase) || phase.Contains("Phase2", StringComparison.OrdinalIgnoreCase) || phase.Contains("Strategy", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (phase.Contains("Phase 1", StringComparison.OrdinalIgnoreCase) || phase.Contains("Phase1", StringComparison.OrdinalIgnoreCase) || phase.Contains("Pick", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (phase.Contains("Phase 0", StringComparison.OrdinalIgnoreCase) || phase.Contains("Phase0", StringComparison.OrdinalIgnoreCase) || phase.Contains("Loading", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (phase.Contains("Phase 4", StringComparison.OrdinalIgnoreCase) || phase.Contains("Ended", StringComparison.OrdinalIgnoreCase))
            return 4;

        return 0;
    }

    /// <summary>
    /// #183 (Gem 1B): 5-state ('cochanged', 'static_only', 'runtime_only', 'both_unchanged', 'unknown')
    /// static-vs-runtime correlation matrix with side_alignment ('left_to_left' / 'left_to_right') and
    /// 3-valued epistemic classification ('hypothesis' / 'contradiction' / 'unresolved_branch') emitting
    /// deterministic 'cor_&lt;sha256&gt;' and 'contra_&lt;sha256&gt;' records.
    /// </summary>
    public StaticRuntimeCorrelationRecord CorrelateStaticVsRuntimeObservation(
        string subjectId,
        string? staticBeforeDigest,
        string? staticAfterDigest,
        string? runtimeBeforeDigest,
        string? runtimeAfterDigest,
        bool staticTruncated = false,
        bool runtimeTruncated = false)
    {
        Log($"Correlating static-vs-runtime observation '{subjectId}' across 5-state matrix (#183).");
        return ReaEpistemicForensicsEngine.CorrelateStaticAndRuntime(
            subjectId,
            staticBeforeDigest,
            staticAfterDigest,
            runtimeBeforeDigest,
            runtimeAfterDigest,
            staticTruncated,
            runtimeTruncated);
    }

    #endregion
}
