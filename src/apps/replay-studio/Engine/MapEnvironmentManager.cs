// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MapEnvironmentManager.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayStudio.GUI.Engine;

public enum MapType
{
    ForestsOfCaldavar = 0,
    MidWars = 1,
    GrimmsCrossing = 2,
    Custom = 3
}

public enum GroundClassification
{
    Lane = 0,
    River = 1,
    Jungle = 2,
    BasePlateau = 3,
    Cliff = 4,
    Bridge = 5,
    Abyss = 6,
    OutOfBounds = 7
}

public sealed class WaterPlaneSettings
{
    public float WaterLevel { get; set; } = 0.0f;
    public Vector4 WaterColor { get; set; } = new(0.12f, 0.35f, 0.55f, 0.85f);
    public float FlowSpeed { get; set; } = 0.5f;
    public Vector2 FlowDirection { get; set; } = Vector2.Normalize(new Vector2(1.0f, -1.0f));
    public float RippleFrequency { get; set; } = 2.5f;
    public float RippleAmplitude { get; set; } = 0.08f;
    public float RefractionIndex { get; set; } = 1.333f;
    public float SpecularPower { get; set; } = 64.0f;
    public bool IsLava { get; set; }
}

public sealed class MapDefinition
{
    public MapType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Vector2 WorldMin { get; set; } = new(0.0f, 0.0f);
    public Vector2 WorldMax { get; set; } = new(16384.0f, 16384.0f);
    public Vector2 PlayableMin { get; set; } = new(1024.0f, 1024.0f);
    public Vector2 PlayableMax { get; set; } = new(15360.0f, 15360.0f);
    public Vector3 LegionBasePosition { get; set; }
    public Vector3 HellbourneBasePosition { get; set; }
    public WaterPlaneSettings WaterPlane { get; set; } = new();
    public float DefaultCameraHeight { get; set; } = 18.0f;
    public float DefaultCameraDistance { get; set; } = 14.0f;
}

public sealed class TerrainMeshData
{
    public Vector3[] Positions { get; set; } = [];
    public Vector3[] Normals { get; set; } = [];
    public Vector2[] TexCoords { get; set; } = [];
    public uint[] Indices { get; set; } = [];
}

/// <summary>
///     Manages map terrain heightmaps, coordinate transformations, environmental properties,
///     and water plane physics for Forests of Caldavar, Mid Wars, and custom arenas.
/// </summary>
public sealed class MapEnvironmentManager
{
    private readonly ILogger<MapEnvironmentManager> _logger;
    private readonly Dictionary<MapType, MapDefinition> _mapCatalog = new();
    private MapDefinition _currentMap;

    public MapDefinition CurrentMap => _currentMap;

    public MapEnvironmentManager(ILogger<MapEnvironmentManager>? logger = null)
    {
        _logger = logger ?? NullLogger<MapEnvironmentManager>.Instance;
        InitializeCatalog();
        _currentMap = _mapCatalog[MapType.ForestsOfCaldavar];
        _logger.LogInformation("[MapEnvironmentManager] Initialized with active map '{MapName}'", _currentMap.DisplayName);
    }

    private void InitializeCatalog()
    {
        // 1. Forests of Caldavar (3-Lane Standard MOBA)
        MapDefinition caldavar = new()
        {
            Type = MapType.ForestsOfCaldavar,
            Name = "caldavar",
            DisplayName = "Forests of Caldavar",
            WorldMin = new Vector2(0.0f, 0.0f),
            WorldMax = new Vector2(16384.0f, 16384.0f),
            PlayableMin = new Vector2(1024.0f, 1024.0f),
            PlayableMax = new Vector2(15360.0f, 15360.0f),
            LegionBasePosition = new Vector3(-11.70f, -11.70f, 2.2f),
            HellbourneBasePosition = new Vector3(11.70f, 11.70f, 2.2f),
            WaterPlane = new WaterPlaneSettings
            {
                WaterLevel = 0.0f,
                WaterColor = new Vector4(0.12f, 0.35f, 0.55f, 0.85f),
                FlowSpeed = 0.5f,
                FlowDirection = Vector2.Normalize(new Vector2(1.0f, -1.0f)),
                RippleFrequency = 2.5f,
                RippleAmplitude = 0.08f,
                RefractionIndex = 1.333f,
                SpecularPower = 64.0f,
                IsLava = false
            }
        };
        _mapCatalog[MapType.ForestsOfCaldavar] = caldavar;

        // 2. Mid Wars (Single Bridge Arena)
        MapDefinition midwars = new()
        {
            Type = MapType.MidWars,
            Name = "midwars",
            DisplayName = "Mid Wars",
            WorldMin = new Vector2(0.0f, 0.0f),
            WorldMax = new Vector2(16384.0f, 16384.0f),
            PlayableMin = new Vector2(1500.0f, 6144.0f),
            PlayableMax = new Vector2(14884.0f, 10240.0f),
            LegionBasePosition = new Vector3(-12.0f, 0.0f, 1.2f),
            HellbourneBasePosition = new Vector3(12.0f, 0.0f, 1.2f),
            WaterPlane = new WaterPlaneSettings
            {
                WaterLevel = -2.5f,
                WaterColor = new Vector4(0.20f, 0.15f, 0.10f, 0.90f),
                FlowSpeed = 0.2f,
                FlowDirection = new Vector2(0.0f, 1.0f),
                RippleFrequency = 1.5f,
                RippleAmplitude = 0.05f,
                RefractionIndex = 1.333f,
                SpecularPower = 32.0f,
                IsLava = false
            }
        };
        _mapCatalog[MapType.MidWars] = midwars;

        // 3. Grimm's Crossing
        MapDefinition grimms = new()
        {
            Type = MapType.GrimmsCrossing,
            Name = "grimms_crossing",
            DisplayName = "Grimm's Crossing",
            WorldMin = new Vector2(0.0f, 0.0f),
            WorldMax = new Vector2(16384.0f, 16384.0f),
            PlayableMin = new Vector2(2048.0f, 2048.0f),
            PlayableMax = new Vector2(14336.0f, 14336.0f),
            LegionBasePosition = new Vector3(-10.0f, -10.0f, 1.5f),
            HellbourneBasePosition = new Vector3(10.0f, 10.0f, 1.5f),
            WaterPlane = new WaterPlaneSettings
            {
                WaterLevel = -0.5f,
                WaterColor = new Vector4(0.10f, 0.28f, 0.45f, 0.80f),
                FlowSpeed = 0.4f,
                FlowDirection = Vector2.Normalize(new Vector2(1.0f, 0.0f)),
                RippleFrequency = 2.0f,
                RippleAmplitude = 0.06f,
                RefractionIndex = 1.333f,
                SpecularPower = 48.0f,
                IsLava = false
            }
        };
        _mapCatalog[MapType.GrimmsCrossing] = grimms;
    }

    public MapDefinition GetMapDefinition(MapType type)
    {
        return _mapCatalog.TryGetValue(type, out MapDefinition? def) ? def : _mapCatalog[MapType.ForestsOfCaldavar];
    }

    public void LoadMap(string? rawMapName)
    {
        string normalized = (rawMapName ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("midwars") || normalized.Contains("mid_wars") || normalized.Equals("mw", StringComparison.Ordinal))
        {
            _currentMap = _mapCatalog[MapType.MidWars];
        }
        else if (normalized.Contains("grimm"))
        {
            _currentMap = _mapCatalog[MapType.GrimmsCrossing];
        }
        else
        {
            _currentMap = _mapCatalog[MapType.ForestsOfCaldavar];
        }

        _logger.LogInformation("[MapEnvironmentManager] Loaded map definition '{MapName}' (Type={MapType})", _currentMap.DisplayName, _currentMap.Type);
    }

    public static Vector3 HonToWorld3D(Vector2 honPos, float height = 0.0f)
    {
        float worldX = (honPos.X - 8192.0f) / 512.0f;
        float worldY = (honPos.Y - 8192.0f) / 512.0f;
        return new Vector3(worldX, worldY, height);
    }

    public static Vector2 World3DToHon(Vector3 worldPos)
    {
        float honX = worldPos.X * 512.0f + 8192.0f;
        float honY = worldPos.Y * 512.0f + 8192.0f;
        return new Vector2(honX, honY);
    }

    public static Vector2 NormalizedToHon(Vector2 normalizedPos)
    {
        return new Vector2(normalizedPos.X * 16384.0f, normalizedPos.Y * 16384.0f);
    }

    public static Vector2 HonToNormalized(Vector2 honPos)
    {
        return new Vector2(honPos.X / 16384.0f, honPos.Y / 16384.0f);
    }

    public float SampleHeight(float worldX, float worldY)
    {
        if (_currentMap.Type == MapType.MidWars)
        {
            return SampleMidWarsHeight(worldX, worldY);
        }

        return SampleCaldavarHeight(worldX, worldY);
    }

    private static float SampleCaldavarHeight(float x, float y)
    {
        // 1. Base ground elevation
        float h = 0.8f;

        // 2. River depression along diagonal line x + y = 0
        float dRiver = MathF.Abs(x + y) / 1.41421356f;
        float riverFactor = 1.0f - Math.Clamp(dRiver / 2.34f, 0.0f, 1.0f);
        float riverSmooth = riverFactor * riverFactor * (3.0f - 2.0f * riverFactor);
        h -= 1.5f * riverSmooth;

        // 3. High ground base plateaus
        float dLegion = Vector2.Distance(new Vector2(x, y), new Vector2(-11.7f, -11.7f));
        float dHell = Vector2.Distance(new Vector2(x, y), new Vector2(11.7f, 11.7f));

        float legionFactor = 1.0f - Math.Clamp(dLegion / 4.0f, 0.0f, 1.0f);
        float hellFactor = 1.0f - Math.Clamp(dHell / 4.0f, 0.0f, 1.0f);

        h += 1.4f * (legionFactor * legionFactor * (3.0f - 2.0f * legionFactor));
        h += 1.4f * (hellFactor * hellFactor * (3.0f - 2.0f * hellFactor));

        // 4. Subtle terrain roughness
        h += 0.15f * MathF.Sin(0.5f * x) * MathF.Cos(0.5f * y);

        return h;
    }

    private static float SampleMidWarsHeight(float x, float y)
    {
        float bridgeWidth = 3.51f;
        float halfWidth = bridgeWidth * 0.5f;
        float distFromCenterY = MathF.Abs(y);

        if (distFromCenterY <= halfWidth)
        {
            return 1.2f; // Flat bridge combat deck
        }

        // Deep chasm / abyss drop-off
        float rampDist = distFromCenterY - halfWidth;
        float rampFactor = Math.Clamp(rampDist / 1.2f, 0.0f, 1.0f);
        float smoothRamp = rampFactor * rampFactor * (3.0f - 2.0f * rampFactor);

        return 1.2f - 4.5f * smoothRamp;
    }

    public Vector3 SampleNormal(float worldX, float worldY)
    {
        const float eps = 0.05f;
        float hL = SampleHeight(worldX - eps, worldY);
        float hR = SampleHeight(worldX + eps, worldY);
        float hD = SampleHeight(worldX, worldY - eps);
        float hU = SampleHeight(worldX, worldY + eps);

        float dX = (hR - hL) / (2.0f * eps);
        float dY = (hU - hD) / (2.0f * eps);

        return Vector3.Normalize(new Vector3(-dX, -dY, 1.0f));
    }

    public bool IsInWater(float worldX, float worldY)
    {
        float terrainZ = SampleHeight(worldX, worldY);
        return terrainZ <= _currentMap.WaterPlane.WaterLevel;
    }

    public GroundClassification ClassifyTerrain(float worldX, float worldY)
    {
        if (MathF.Abs(worldX) > 16.0f || MathF.Abs(worldY) > 16.0f)
        {
            return GroundClassification.OutOfBounds;
        }

        if (_currentMap.Type == MapType.MidWars)
        {
            if (MathF.Abs(worldY) <= 1.755f) return GroundClassification.Bridge;
            return GroundClassification.Abyss;
        }

        if (IsInWater(worldX, worldY))
        {
            return GroundClassification.River;
        }

        float dLegion = Vector2.Distance(new Vector2(worldX, worldY), new Vector2(-11.7f, -11.7f));
        float dHell = Vector2.Distance(new Vector2(worldX, worldY), new Vector2(11.7f, 11.7f));
        if (dLegion < 4.0f || dHell < 4.0f)
        {
            return GroundClassification.BasePlateau;
        }

        return GroundClassification.Jungle;
    }

    public TerrainMeshData GenerateTerrainMesh(int gridResX = 64, int gridResY = 64)
    {
        int vertexCount = gridResX * gridResY;
        Vector3[] positions = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] texCoords = new Vector2[vertexCount];

        float minX = -16.0f;
        float maxX = 16.0f;
        float minY = -16.0f;
        float maxY = 16.0f;

        float stepX = (maxX - minX) / (gridResX - 1);
        float stepY = (maxY - minY) / (gridResY - 1);

        for (int y = 0; y < gridResY; y++)
        {
            for (int x = 0; x < gridResX; x++)
            {
                int index = y * gridResX + x;
                float wx = minX + x * stepX;
                float wy = minY + y * stepY;
                float wz = SampleHeight(wx, wy);

                positions[index] = new Vector3(wx, wy, wz);
                normals[index] = SampleNormal(wx, wy);
                texCoords[index] = new Vector2((float)x / (gridResX - 1), (float)y / (gridResY - 1));
            }
        }

        int quadCount = (gridResX - 1) * (gridResY - 1);
        uint[] indices = new uint[quadCount * 6];
        int idx = 0;

        for (int y = 0; y < gridResY - 1; y++)
        {
            for (int x = 0; x < gridResX - 1; x++)
            {
                uint i0 = (uint)(y * gridResX + x);
                uint i1 = (uint)(y * gridResX + (x + 1));
                uint i2 = (uint)((y + 1) * gridResX + x);
                uint i3 = (uint)((y + 1) * gridResX + (x + 1));

                // Triangle 1
                indices[idx++] = i0;
                indices[idx++] = i1;
                indices[idx++] = i2;

                // Triangle 2
                indices[idx++] = i1;
                indices[idx++] = i3;
                indices[idx++] = i2;
            }
        }

        _logger.LogInformation(
            "[MapEnvironmentManager] Generated terrain mesh ({Vertices} vertices, {Triangles} triangles, Res={ResX}x{ResY})",
            vertexCount,
            indices.Length / 3,
            gridResX,
            gridResY);

        return new TerrainMeshData
        {
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Indices = indices
        };
    }
}
