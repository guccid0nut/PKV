// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CatmullRomSpline.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Director;

/// <summary>
///     Evaluator for cubic Hermite / Catmull-Rom spline curves with C1 continuity,
///     phantom endpoint extrapolation, spherical quaternion slerp, and timeline node management.
/// </summary>
public sealed class CatmullRomSpline
{
    private readonly ILogger<CatmullRomSpline> _logger;
    private readonly List<DirectorNode> _nodes = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<DirectorNode> Nodes
    {
        get
        {
            lock (_lock) return _nodes.ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _nodes.Count;
        }
    }

    public CatmullRomSpline(ILogger<CatmullRomSpline>? logger = null)
    {
        _logger = logger ?? NullLogger<CatmullRomSpline>.Instance;
    }

    public void AddNode(DirectorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_lock)
        {
            _nodes.Add(node.Clone());
            _nodes.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            _logger.LogInformation("[CatmullRomSpline] Added node at {TimestampMs}ms (Total nodes: {Count})", node.TimestampMs, _nodes.Count);
        }
    }

    public bool RemoveNode(Guid id)
    {
        lock (_lock)
        {
            int index = _nodes.FindIndex(n => n.Id == id);
            if (index >= 0)
            {
                _nodes.RemoveAt(index);
                _logger.LogInformation("[CatmullRomSpline] Removed node {Id} (Remaining: {Count})", id, _nodes.Count);
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _nodes.Clear();
            _logger.LogInformation("[CatmullRomSpline] Cleared all nodes");
        }
    }

    /// <summary>
    ///     Evaluates the camera spline state (Position, Target, Orientation, FOV) at a given timeline millisecond timestamp.
    /// </summary>
    public (Vector3 Position, Vector3 Target, Quaternion Orientation, float Fov) Evaluate(int timestampMs)
    {
        lock (_lock)
        {
            if (_nodes.Count == 0)
            {
                return (new Vector3(0, -14, 18), Vector3.Zero, Quaternion.Identity, 55.0f);
            }

            if (_nodes.Count == 1)
            {
                DirectorNode single = _nodes[0];
                return (single.Position, single.LookAtTarget, single.Orientation, single.FovDegrees);
            }

            // Timestamp before first node
            if (timestampMs <= _nodes[0].TimestampMs)
            {
                DirectorNode first = _nodes[0];
                return (first.Position, first.LookAtTarget, first.Orientation, first.FovDegrees);
            }

            // Timestamp after last node
            if (timestampMs >= _nodes[^1].TimestampMs)
            {
                DirectorNode last = _nodes[^1];
                return (last.Position, last.LookAtTarget, last.Orientation, last.FovDegrees);
            }

            // Locate segment [i, i + 1] containing timestamp
            int segmentIdx = 0;
            for (int i = 0; i < _nodes.Count - 1; i++)
            {
                if (timestampMs >= _nodes[i].TimestampMs && timestampMs <= _nodes[i + 1].TimestampMs)
                {
                    segmentIdx = i;
                    break;
                }
            }

            DirectorNode p1Node = _nodes[segmentIdx];
            DirectorNode p2Node = _nodes[segmentIdx + 1];

            int deltaT = p2Node.TimestampMs - p1Node.TimestampMs;
            float u = deltaT > 0 ? (float)(timestampMs - p1Node.TimestampMs) / deltaT : 0.0f;
            float clampedU = Math.Clamp(u, 0.0f, 1.0f);

            // 4 Control points for Catmull-Rom Position
            Vector3 p0 = segmentIdx > 0 ? _nodes[segmentIdx - 1].Position : (2.0f * p1Node.Position - p2Node.Position);
            Vector3 p1 = p1Node.Position;
            Vector3 p2 = p2Node.Position;
            Vector3 p3 = segmentIdx + 2 < _nodes.Count ? _nodes[segmentIdx + 2].Position : (2.0f * p2Node.Position - p1Node.Position);

            Vector3 interpolatedPos = EvaluateCubic(p0, p1, p2, p3, clampedU);

            // 4 Control points for Target
            Vector3 t0 = segmentIdx > 0 ? _nodes[segmentIdx - 1].LookAtTarget : (2.0f * p1Node.LookAtTarget - p2Node.LookAtTarget);
            Vector3 t1 = p1Node.LookAtTarget;
            Vector3 t2 = p2Node.LookAtTarget;
            Vector3 t3 = segmentIdx + 2 < _nodes.Count ? _nodes[segmentIdx + 2].LookAtTarget : (2.0f * p2Node.LookAtTarget - p1Node.LookAtTarget);

            Vector3 interpolatedTarget = EvaluateCubic(t0, t1, t2, t3, clampedU);

            // Quaternion Slerp with shortest arc
            Quaternion interpolatedRot = SlerpShortestArc(p1Node.Orientation, p2Node.Orientation, clampedU);

            // FOV linear interpolation
            float interpolatedFov = p1Node.FovDegrees + (p2Node.FovDegrees - p1Node.FovDegrees) * clampedU;

            return (interpolatedPos, interpolatedTarget, interpolatedRot, interpolatedFov);
        }
    }

    /// <summary>
    ///     Evaluates standard Catmull-Rom cubic interpolation:
    ///     P(u) = 0.5 * [ 2*P1 + (-P0 + P2)*u + (2*P0 - 5*P1 + 4*P2 - P3)*u^2 + (-P0 + 3*P1 - 3*P2 + P3)*u^3 ]
    /// </summary>
    public static Vector3 EvaluateCubic(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        float u2 = u * u;
        float u3 = u2 * u;

        Vector3 term0 = 2.0f * p1;
        Vector3 term1 = (-p0 + p2) * u;
        Vector3 term2 = (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * u2;
        Vector3 term3 = (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * u3;

        return 0.5f * (term0 + term1 + term2 + term3);
    }

    /// <summary>
    ///     Calculates the analytical first derivative (tangent velocity) along the Catmull-Rom spline curve.
    /// </summary>
    public static Vector3 EvaluateTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        float u2 = u * u;

        Vector3 term1 = -p0 + p2;
        Vector3 term2 = 2.0f * (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * u;
        Vector3 term3 = 3.0f * (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * u2;

        return 0.5f * (term1 + term2 + term3);
    }

    /// <summary>
    ///     Spherical linear interpolation between two unit quaternions with shortest arc antipodal check.
    /// </summary>
    public static Quaternion SlerpShortestArc(Quaternion q1, Quaternion q2, float t)
    {
        float tClamped = Math.Clamp(t, 0.0f, 1.0f);
        float dot = Quaternion.Dot(q1, q2);
        Quaternion targetQ2 = q2;

        if (dot < 0.0f)
        {
            targetQ2 = -q2;
            dot = -dot;
        }

        if (dot > 0.9995f)
        {
            Quaternion nlerp = q1 * (1.0f - tClamped) + targetQ2 * tClamped;
            return Quaternion.Normalize(nlerp);
        }

        float theta = MathF.Acos(Math.Clamp(dot, -1.0f, 1.0f));
        float sinTheta = MathF.Sin(theta);

        if (sinTheta < 1e-6f) return q1;

        float scale1 = MathF.Sin((1.0f - tClamped) * theta) / sinTheta;
        float scale2 = MathF.Sin(tClamped * theta) / sinTheta;

        return Quaternion.Normalize(q1 * scale1 + targetQ2 * scale2);
    }
}
