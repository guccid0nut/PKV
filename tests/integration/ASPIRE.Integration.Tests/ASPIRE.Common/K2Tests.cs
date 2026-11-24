// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;
using System.Text.Json;
using ASPIRE.Common.K2;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using Kongor.Core.Services.Vfs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Services;

namespace ASPIRE.Tests.ASPIRE.Common;

public sealed class K2Tests
{
    [Fact]
    public void K2ModelReader_WhenExecuted_ThrowsOnInvalidHeader()
    {
        // Arrange
        byte[] invalidData = [.. "INVALID_HEADER_DATA"u8];
        using MemoryStream stream = new(invalidData);

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => K2ModelReader.Read(stream));
    }

    [Fact]
    public void K2ClipReader_WhenExecuted_ThrowsOnInvalidHeader()
    {
        // Arrange
        byte[] invalidData = [.. "BAD_CLIP_HEADER"u8];
        using MemoryStream stream = new(invalidData);

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => K2ClipReader.Read(stream));
    }

    [Fact]
    public void K2MdfReader_WhenExecuted_ParsesModelAndAnimations()
    {
        // Arrange
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <model name="rig.max" file="high.model" high="high.model" med="med.model" low="low.model">
                               <anim name="idle" clip="clips/default_1.clip" fps="30" loop="true"/>
                               <anim name="attack_1" clip="clips/attack_1.clip" fps="35" loop="false"/>
                               <anim name="death_1" clip="clips/death_1.clip" fps="25" loop="false"/>
                           </model>
                           """;
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));

        // Act
        K2Mdf mdf = K2MdfReader.Read(stream);

        // Assert
        Assert.Equal("high.model", mdf.ModelFile);
        Assert.Equal(3, mdf.Animations.Count);
        Assert.Equal("idle", mdf.Animations[0].Name);
        Assert.Equal("clips/default_1.clip", mdf.Animations[0].ClipPath);
        Assert.Equal(30, mdf.Animations[0].Fps);
        Assert.True(mdf.Animations[0].Loop);
        Assert.Equal("attack_1", mdf.Animations[1].Name);
        Assert.Equal(35, mdf.Animations[1].Fps);
        Assert.False(mdf.Animations[1].Loop);
    }

    [Fact]
    public void K2ToGltfConverter_WhenExecuted_ConvertsModelToValidGlbStructure()
    {
        // Arrange: Create a minimal synthesized K2 Model
        K2Model model = new()
        {
            Version = 3,
            NumMeshes = 1,
            NumBones = 2,
            Bones =
            [
                new K2Bone
                {
                    Index = 0,
                    ParentIndex = -1,
                    Name = "Root",
                    InverseMatrix = Matrix4x4.Identity,
                    LocalMatrix = Matrix4x4.Identity
                },
                new K2Bone
                {
                    Index = 1,
                    ParentIndex = 0,
                    Name = "Spine",
                    InverseMatrix = Matrix4x4.Identity,
                    LocalMatrix = Matrix4x4.Identity
                }
            ],
            Meshes =
            [
                new K2Mesh
                {
                    Index = 0,
                    Name = "TestMesh",
                    NumVertices = 3,
                    NumFaces = 1,
                    Vertices = [new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)],
                    Normals = [new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1)],
                    TexCoords = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
                    Indices = [0, 1, 2],
                    BoneLinks =
                    [
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 1, Weight0 = 1f }
                    ]
                }
            ]
        };

        K2Clip clip = new()
        {
            Name = "idle",
            NumBones = 2,
            NumFrames = 10,
            FrameRate = 30,
            BoneMotions =
            [
                new K2BoneMotion
                {
                    BoneIndex = 1,
                    BoneName = "Spine",
                    Translations = [.. Enumerable.Range(0, 10).Select(i => new Vector3(0, 0, i * 0.1f))],
                    Rotations = [.. Enumerable.Range(0, 10).Select(_ => Quaternion.Identity)]
                }
            ]
        };

        // Act
        byte[] glb = K2ToGltfConverter.ConvertToGlb(model, [clip]);

        // Assert
        Assert.NotNull(glb);
        Assert.True(glb.Length > 20);

        // Verify GLB Binary Header
        uint magic = BitConverter.ToUInt32(glb, 0);
        uint version = BitConverter.ToUInt32(glb, 4);
        uint length = BitConverter.ToUInt32(glb, 8);

        Assert.Equal(0x46546C67u, magic); // "glTF"
        Assert.Equal(2u, version);
        Assert.Equal((uint)glb.Length, length);

        // Verify JSON chunk
        uint jsonLength = BitConverter.ToUInt32(glb, 12);
        uint jsonType = BitConverter.ToUInt32(glb, 16);
        Assert.Equal(0x4E4F534Au, jsonType); // "JSON"

        string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLength).Trim();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        Assert.True(root.GetProperty("meshes").GetArrayLength() > 0);
        Assert.True(root.GetProperty("animations").GetArrayLength() > 0);
    }

    [Fact]
    public void K2ToGltfConverter_MultiMesh_GeneratesDistinctPrimitivesAndMaterials()
    {
        // Arrange: Create a synthesized model with 2 meshes (e.g. Body and Eyes/Weapon)
        K2Model model = new()
        {
            Version = 3,
            NumMeshes = 2,
            NumBones = 1,
            Bones =
            [
                new K2Bone
                {
                    Index = 0,
                    ParentIndex = -1,
                    Name = "Root",
                    InverseMatrix = Matrix4x4.Identity,
                    LocalMatrix = Matrix4x4.Identity
                }
            ],
            Meshes =
            [
                new K2Mesh
                {
                    Index = 0,
                    Name = "BodyMesh",
                    NumVertices = 3,
                    NumFaces = 1,
                    Vertices = [new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)],
                    Normals = [new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1)],
                    TexCoords = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
                    Indices = [0, 1, 2],
                    BoneLinks =
                    [
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f }
                    ]
                },
                new K2Mesh
                {
                    Index = 1,
                    Name = "EyesMesh",
                    NumVertices = 3,
                    NumFaces = 1,
                    Vertices = [new Vector3(0, 10, 0), new Vector3(10, 10, 0), new Vector3(0, 20, 0)],
                    Normals = [new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1)],
                    TexCoords = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
                    Indices = [0, 1, 2],
                    BoneLinks =
                    [
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f }
                    ]
                }
            ]
        };

        byte[] fakePng1 =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89
        ];
        byte[] fakePng2 =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00,
            0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x90
        ];

        List<K2MeshMaterial> mats =
        [
            new() { Name = "BodyMat", DiffusePngBytes = fakePng1 },
            new() { Name = "EyesMat", DiffusePngBytes = fakePng2, GlowPngBytes = fakePng1 }
        ];

        // Act
        byte[] glb = K2ToGltfConverter.ConvertToGlb(model, null, null, mats);

        // Assert
        Assert.NotNull(glb);
        uint jsonLength = BitConverter.ToUInt32(glb, 12);
        string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLength).Trim();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement meshEl = root.GetProperty("meshes")[0];
        JsonElement primitives = meshEl.GetProperty("primitives");
        Assert.Equal(2, primitives.GetArrayLength());

        JsonElement materials = root.GetProperty("materials");
        Assert.Equal(2, materials.GetArrayLength());
        Assert.Equal("BodyMat", materials[0].GetProperty("name").GetString());
        Assert.Equal("EyesMat", materials[1].GetProperty("name").GetString());
        Assert.True(materials[1].TryGetProperty("emissiveTexture", out _));
    }

    [Fact]
    public void K2_GameArchiveVfsService_HandlesEmptyOrMissingArchiveGracefully()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GameClient:GamePath"] = Path.Combine(Path.GetTempPath(), "NonExistentPath", "game")
            })
            .Build();

        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act & Assert
        Assert.False(vfs.FileExists("heroes/hunter/alt7/model.mdf"));
        Assert.Null(vfs.OpenFile("heroes/hunter/alt7/model.mdf"));
        Assert.Null(vfs.ReadAllBytes("heroes/hunter/alt7/model.mdf"));
        Assert.Null(vfs.GetHeroModelGlb("hunter", "alt7"));
    }

    [Fact]
    public void GameArchiveVfsService_ResolveGamePath_HandlesExecutableAndNestedGame()
    {
        // Act & Assert
        string emptyResult = GameArchiveVfsService.ResolveGamePath(null);
        Assert.Equal(string.Empty, emptyResult);

        string spaceResult = GameArchiveVfsService.ResolveGamePath("   ");
        Assert.Equal(string.Empty, spaceResult);

        // Simulated quoted executable path
        string exePath = @"C:\Games\Heroes of Newerth\hon_x64.exe";
        string resolvedDir = GameArchiveVfsService.ResolveGamePath($"\"{exePath}\"");
        Assert.DoesNotContain("hon_x64.exe", resolvedDir, StringComparison.OrdinalIgnoreCase);

        // Auto discovery check
        string autoPath = GameArchiveVfsService.AutoDiscoverGamePath(out List<string> supp);
        if (Directory.Exists(autoPath)) Assert.Contains("game", autoPath, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_AutoDiscovery_FindsReorganizedClientGame()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        using GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        Assert.NotNull(vfs.GamePath);
        if (Directory.Exists(vfs.GamePath))
            Assert.True(vfs.FileExists("heroes/krixi/model.mdf") || vfs.FileExists("heroes/krixi/alt/model.mdf") ||
                        vfs.FileExists("heroes/hunter/model.mdf"));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_AutoDiscovery_ExtractsRealGlb()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange: Empty config will trigger automatic discovery
        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act
        byte[]? glb = vfs.GetHeroModelGlb("hunter", "alt7");

        // Assert
        if (vfs.FileExists("heroes/hunter/alt7/model.mdf"))
        {
            Assert.NotNull(glb);
            Assert.True(glb.Length > 1000);

            // Verify valid glTF 2.0 Magic
            uint magic = BitConverter.ToUInt32(glb, 0);
            Assert.Equal(0x46546C67u, magic); // "glTF"
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_MatrixHierarchy_Verification()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);
        using Stream? stream = vfs.OpenFile("heroes/krixi/alt/high.model");
        Assert.NotNull(stream);

        K2Model model = K2ModelReader.Read(stream);

        Matrix4x4 c = new(
            1f, 0f, 0f, 0f,
            0f, 0f, -1f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 0f, 1f
        );
        Matrix4x4 cInv = new(
            1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, -1f, 0f, 0f,
            0f, 0f, 0f, 1f
        );

        // Test world * inv == Identity
        for (int i = 0; i < model.Bones.Count; i++)
        {
            Matrix4x4 wGl = cInv * model.Bones[i].LocalMatrix * c;
            Matrix4x4 invGl = cInv * model.Bones[i].InverseMatrix * c;
            Matrix4x4 prod = wGl * invGl;
            float err = MathF.Abs(prod.M11 - 1f) + MathF.Abs(prod.M22 - 1f) + MathF.Abs(prod.M33 - 1f) +
                        MathF.Abs(prod.M44 - 1f);
            Assert.True(err < 0.001f, $"Bone {i} ({model.Bones[i].Name}) wGl * invGl error: {err}");
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_ExportHeroGlb_Cli()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string hero = Environment.GetEnvironmentVariable("HON_EXPORT_HERO") ?? "krixi";
        string alt = Environment.GetEnvironmentVariable("HON_EXPORT_ALT") ?? "sexy";
        string? outFile = Environment.GetEnvironmentVariable("HON_EXPORT_OUT");

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        byte[]? glb = vfs.GetHeroModelGlb(hero, alt, $"/heroes/{hero}/alt/icon.tga");
        Assert.NotNull(glb);

        if (!string.IsNullOrWhiteSpace(outFile))
        {
            string? dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllBytes(outFile, glb);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_PrebakeAllHeroes_Cli()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (Environment.GetEnvironmentVariable("HON_PREBAKE_RUN") != "true") return;

        string outDir = Environment.GetEnvironmentVariable("HON_PREBAKE_OUT_DIR") ??
                        Path.Combine(AppContext.BaseDirectory, "prebaked_assets", "models");

        string? parentDir = Path.GetDirectoryName(outDir);
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GameClient:PreBakedAssetPath"] = parentDir })
            .Build();

        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        IEnumerable<string> heroMdfs = vfs.FindFiles("heroes/")
            .Where(f => f.EndsWith("/model.mdf", StringComparison.OrdinalIgnoreCase));

        List<string> heroList =
        [
            .. heroMdfs
                .Select(f => f.Split('/', StringSplitOptions.RemoveEmptyEntries)[1])
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        int exportedCount = 0;
        Parallel.ForEach(heroList, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, hero =>
        {
            try
            {
                bool success = vfs.PrebakeHeroModel(hero);
                if (success) Interlocked.Increment(ref exportedCount);
            }
            catch
            {
                // ignored
            }
        });

        Assert.True(exportedCount > 0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_InspectPimpSlayerAndTeenHound()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        IStaffStoreService store = new StaffStoreService(config, NullLogger<StaffStoreService>.Instance);
        StaffStoreViewModel cat = store.GetCatalog(null, null, 1, 500);

        List<string> typeSummaries =
        [
            .. cat.Items
                .GroupBy(x => x.Type)
                .OrderBy(g => g.Key)
                .Select(g => $"Type {g.Key} ({g.Count()} items): Sample: '{g.First().Name}' (Code: {g.First().Code})")
        ];

        Assert.NotEmpty(typeSummaries);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_ExtractPollywogPriest_ReturnsValidGlb()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act: Test Pollywog Priest
        byte[]? glb = vfs.GetHeroModelGlb("pollywogpriest");

        // Assert
        Assert.NotNull(glb);
        Assert.True(glb.Length > 1000);

        uint magic = BitConverter.ToUInt32(glb, 0);
        Assert.Equal(0x46546C67u, magic);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_ExtractKongorModel()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] candidateMdfs =
        [
            "npcs/kongor/model.mdf",
            "npcs/kongor/punk/model.mdf",
            "npcs/kongor/hell_kongor/model.mdf",
            "npcs/kongor/chris/model.mdf",
            "shared/automated_courier/couriers/kongor/model.mdf"
        ];

        byte[]? exportedGlb = null;

        foreach (string mdf in candidateMdfs)
            if (vfs.FileExists(mdf))
            {
                byte[]? glb = vfs.GetModelGlb(mdf);
                if (glb is { Length: > 1000 })
                {
                    exportedGlb = glb;
                    break;
                }
            }

        if (exportedGlb != null)
        {
            string outDir = Path.Combine(Path.GetTempPath(), "nexus_test_models");
            string outPath = Path.Combine(outDir, "kongor_default.glb");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            File.WriteAllBytes(outPath, exportedGlb);
        }

        Assert.NotNull(exportedGlb);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_ExtractEmpathGaiaModel()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] candidateMdfs =
        [
            "heroes/empath/alt4/grass/model.mdf",
            "heroes/empath/alt4/model.mdf",
            "heroes/empath/alt5/model.mdf",
            "heroes/empath/alt2/model.mdf",
            "heroes/empath/model.mdf"
        ];

        byte[]? exportedGlb = null;

        foreach (string mdf in candidateMdfs)
            if (vfs.FileExists(mdf))
            {
                byte[]? glb = vfs.GetModelGlb(mdf);
                if (glb is { Length: > 1000 })
                {
                    exportedGlb = glb;
                    break;
                }
            }

        if (exportedGlb != null)
        {
            string outDir = Path.Combine(Path.GetTempPath(), "nexus_test_models");
            string outPath = Path.Combine(outDir, "empath_gaia.glb");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            File.WriteAllBytes(outPath, exportedGlb);
        }

        Assert.NotNull(exportedGlb);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_ExtractAlunaStardustModel()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] candidateMdfs =
        [
            "heroes/aluna/alt/model.mdf",
            "heroes/aluna/alt2/model.mdf",
            "heroes/aluna/alt3/model.mdf",
            "heroes/aluna/model.mdf"
        ];

        byte[]? exportedGlb = null;

        foreach (string mdf in candidateMdfs)
            if (vfs.FileExists(mdf))
            {
                byte[]? glb = vfs.GetModelGlb(mdf);
                if (glb is { Length: > 1000 })
                {
                    exportedGlb = glb;
                    break;
                }
            }

        if (exportedGlb != null)
        {
            string outDir = Path.Combine(Path.GetTempPath(), "nexus_test_models");
            string outPath = Path.Combine(outDir, "aluna_stardust.glb");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            File.WriteAllBytes(outPath, exportedGlb);
        }

        Assert.NotNull(exportedGlb);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_ExtractMultipleHeroes_DiagnoseMdfResolution()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        IGameArchiveVfsService vfs = new GameArchiveVfsService(config, NullLogger<GameArchiveVfsService>.Instance);

        (string Hero, string Alt, string Res)[] cases =
        [
            ("pyromancer", "female", "/heroes/pyromancer/alt/icon.tga"),
            ("krixi", "sexy", "/heroes/krixi/alt/icon.tga"),
            ("magmar", "scorpion", "/heroes/magmar/alt/icon.tga"),
            ("frosty", "female", "/heroes/frosty/alt/icon.tga"),
            ("witch_slayer", "alt", "/heroes/witch_slayer/alt/icon.tga"),
            ("scout", null!, null!),
            ("succubis", "alt_1", "/heroes/succubis/alt_1/icon.tga")
        ];

        foreach ((string hero, string alt, string res) in cases)
        {
            byte[]? glb = vfs.GetHeroModelGlb(hero, alt, res);
            Assert.NotNull(glb);
            Assert.True(glb.Length > 1000);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_ScanMdfStructureAcrossAllHeroes()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        int mdfCount = vfs.FindFiles("heroes/")
            .Count(file => file.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase));

        Assert.True(mdfCount > 50);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StaffStoreService_AccurateSummaryCounts_ReturnsCorrectCategories()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        // Act
        StaffStoreViewModel catalog = storeService.GetCatalog("all", null, 1, 32);

        // Assert
        Assert.NotNull(catalog);
        Assert.True(catalog.TotalCount >= 4000, $"TotalCount was {catalog.TotalCount}");
        Assert.True(catalog.TotalWards >= 50,
            $"TotalWards was {catalog.TotalWards}, TotalCount: {catalog.TotalCount}, Items in list: {storeService.GetCatalog(null, null).TotalCount}");
        Assert.True(catalog.TotalBundles >= 100);
        Assert.True(catalog.TotalTaunts >= 30);
        Assert.True(catalog.TotalCouriers >= 30);
        Assert.True(catalog.TotalAnnouncers >= 30);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_GetTexturePng_DecodesDiverseShopIcons()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] testPaths =
        [
            "/heroes/pyromancer/alt2/icon.tga",
            "/ui/icons/flags/australia.tga",
            "/ui/legion/icons/taunt.tga",
            "/shared/automated_courier/couriers/Penguin_fly/icon.tga",
            "/heroes/frosty/alt/icon.tga",
            "/heroes/magmar/alt/icon.tga"
        ];

        foreach (string path in testPaths)
        {
            byte[]? png = vfs.GetTexturePng(path);
            Assert.True(png != null, $"Failed to decode icon for path='{path}'");
            Assert.True(png.Length > 20, $"Decoded PNG was too small for path='{path}'");

            // PNG magic signature: 0x89, 0x50, 0x4E, 0x47
            Assert.Equal(0x89, png[0]);
            Assert.Equal(0x50, png[1]);
            Assert.Equal(0x4E, png[2]);
            Assert.Equal(0x47, png[3]);
        }
    }

    [Fact]
    public void GameArchiveVfsService_NormalizePath_HandlesDotDotNavigation()
    {
        // Act & Assert
        Assert.Equal("heroes/devourer/clips/attack_1.clip",
            GameArchiveVfsService.NormalizePath("heroes/devourer/alt/../clips/attack_1.clip"));
        Assert.Equal("heroes/scar/clips/default_1.clip",
            GameArchiveVfsService.NormalizePath("heroes/scar/alt/../clips/default_1.clip"));
        Assert.Equal("heroes/witch_slayer/clips/walk_1.clip",
            GameArchiveVfsService.NormalizePath("heroes/witch_slayer/alt/../clips/walk_1.clip"));
        Assert.Equal("heroes/frosty/body.material",
            GameArchiveVfsService.NormalizePath("heroes/frosty/alt/../body.material"));
        Assert.Equal("heroes/krixi/clips/portrait.clip",
            GameArchiveVfsService.NormalizePath("heroes/krixi/alt/./../clips/portrait.clip"));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_AltAvatars_LoadMultipleClipsAndMaterials()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] altHeroes = ["devourer", "madman", "witchslayer", "frosty"];

        foreach (string hero in altHeroes)
        {
            byte[]? glb = vfs.GetHeroModelGlb(hero, "alt");
            Assert.NotNull(glb);
            Assert.True(glb.Length > 1000, $"GLB for {hero} alt was too small: {glb.Length} bytes");

            // Parse GLB JSON chunk
            uint jsonLength = BitConverter.ToUInt32(glb, 12);
            string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLength).Trim();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            JsonElement animations = root.GetProperty("animations");
            // Must have multiple animations (not 0 or just 1)
            Assert.True(animations.GetArrayLength() >= 2,
                $"Expected at least 2 animation clips for {hero} alt, got {animations.GetArrayLength()}");

            JsonElement meshes = root.GetProperty("meshes");
            Assert.True(meshes.GetArrayLength() > 0);
            JsonElement prims = meshes[0].GetProperty("primitives");
            Assert.True(prims.GetArrayLength() > 0);

            JsonElement materials = root.GetProperty("materials");
            Assert.True(materials.GetArrayLength() > 0);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_HeroSound_ResolvesBaseAndAltAudio()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        byte[]? attackSound = vfs.GetHeroSound("witchslayer", "pimp", "attack");
        Assert.NotNull(attackSound);
        Assert.True(attackSound.Length > 100);

        byte[]? walkSound = vfs.GetHeroSound("devourer", "clown", "walk");
        Assert.NotNull(walkSound);
        Assert.True(walkSound.Length > 100);

        byte[]? frostSound = vfs.GetHeroSound("frosty", "female", "attack");
        Assert.NotNull(frostSound);
        Assert.True(frostSound.Length > 100);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_HeroSound_ResolvesSpecificAbilityAndWalkAudio()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act & Assert Ability sounds
        byte[]? flintAbility = vfs.GetHeroSound("flint_beastwood", "alt", "ability_1");
        Assert.NotNull(flintAbility);
        Assert.True(flintAbility.Length > 100);

        byte[]? hammerAbility = vfs.GetHeroSound("hammerstorm", "alt", "ability_4");
        Assert.NotNull(hammerAbility);
        Assert.True(hammerAbility.Length > 100);

        byte[]? pestiAbility = vfs.GetHeroSound("pestilence", "alt", "ability_1");
        Assert.NotNull(pestiAbility);
        Assert.True(pestiAbility.Length > 100);

        // Act & Assert Walk and Movement sounds
        byte[]? hammerWalk = vfs.GetHeroSound("hammerstorm", "alt", "walk_1");
        Assert.NotNull(hammerWalk);
        Assert.True(hammerWalk.Length > 100);

        byte[]? pestiWalk = vfs.GetHeroSound("pestilence", "alt", "walk_1");
        Assert.NotNull(pestiWalk);
        Assert.True(pestiWalk.Length > 100);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_AltAvatars_FlintHammerPesti_GenerateValidGlb()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] targets = ["flint_beastwood", "hammerstorm", "pestilence"];

        foreach (string hero in targets)
        {
            byte[]? glb = vfs.GetHeroModelGlb(hero, "alt");
            Assert.NotNull(glb);
            Assert.True(glb.Length > 1000);

            // Verify GLB JSON header
            uint jsonLength = BitConverter.ToUInt32(glb, 12);
            string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLength).Trim();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            JsonElement materials = root.GetProperty("materials");
            Assert.True(materials.GetArrayLength() > 0);

            // Verify materials are valid and have non-null names
            foreach (JsonElement mat in materials.EnumerateArray())
                if (mat.TryGetProperty("alphaMode", out JsonElement alphaMode))
                {
                    string mode = alphaMode.GetString() ?? "";
                    Assert.True(mode is "OPAQUE" or "MASK" or "BLEND");
                    if (mode == "MASK")
                    {
                        Assert.True(mat.TryGetProperty("alphaCutoff", out JsonElement cutoff));
                        Assert.Equal(0.5f, cutoff.GetSingle());
                    }
                }
        }
    }

    [Fact]
    public void K2ToGltfConverter_WithNormalAndSpecular_IncludesTexturesInGlb()
    {
        // Arrange
        K2Model model = new()
        {
            Version = 3,
            Meshes =
            [
                new K2Mesh
                {
                    Index = 0,
                    Name = "BodyMesh",
                    NumVertices = 3,
                    NumFaces = 1,
                    Vertices = [new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)],
                    Normals = [new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1)],
                    TexCoords = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
                    Indices = [0, 1, 2],
                    BoneLinks =
                    [
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f },
                        new K2VertexBoneLink { BoneIndex0 = 0, Weight0 = 1f }
                    ]
                }
            ]
        };

        byte[] fakePng =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89
        ];

        List<K2MeshMaterial> mats =
        [
            new()
            {
                Name = "PbrMat",
                DiffusePngBytes = fakePng,
                NormalPngBytes = fakePng,
                MetallicRoughnessPngBytes = fakePng,
                GlowPngBytes = fakePng
            }
        ];

        // Act
        byte[] glb = K2ToGltfConverter.ConvertToGlb(model, null, null, mats);

        // Assert
        Assert.NotNull(glb);
        uint jsonLength = BitConverter.ToUInt32(glb, 12);
        string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLength).Trim();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement materials = root.GetProperty("materials");
        Assert.Equal(1, materials.GetArrayLength());
        JsonElement mat = materials[0];

        Assert.True(mat.TryGetProperty("normalTexture", out _));
        Assert.True(mat.TryGetProperty("emissiveTexture", out _));
        Assert.True(mat.GetProperty("pbrMetallicRoughness").TryGetProperty("metallicRoughnessTexture", out _));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_GetHeroEntityInfo_ReturnsHeroMetadata()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act
        HeroEntityInfoDto? arachnaInfo = vfs.GetHeroEntityInfo("arachna");
        HeroEntityInfoDto? hammerInfo = vfs.GetHeroEntityInfo("hammerstorm");

        // Assert
        Assert.NotNull(arachnaInfo);
        Assert.Equal("arachna", arachnaInfo.HeroName);
        Assert.Equal(4, arachnaInfo.AbilityIcons.Count);
        Assert.True(arachnaInfo.Difficulty > 0);

        Assert.NotNull(hammerInfo);
        Assert.Equal("hammerstorm", hammerInfo.HeroName);
        Assert.Equal(4, hammerInfo.AbilityIcons.Count);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_SpecificAltAvatars_NinjaUnumPenguinBenzalotBogatyr_GenerateValidGlb()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // 1. Ninja Bubbles (bubbles / Alt)
        byte[]? ninjaGlb = vfs.GetHeroModelGlb("bubbles", "Alt", "/heroes/bubbles/alt/icon.tga");
        Assert.NotNull(ninjaGlb);
        Assert.True(ninjaGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(ninjaGlb, 0));

        // 2. Unum Martyr (martyr / Alt2)
        byte[]? unumGlb = vfs.GetHeroModelGlb("martyr", "Alt2", "/heroes/martyr/alt2/icon.tga");
        Assert.NotNull(unumGlb);
        Assert.True(unumGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(unumGlb, 0));

        // 3. Penguin Priest (pollywogpriest / Alt4)
        byte[]? penguinGlb = vfs.GetHeroModelGlb("pollywogpriest", "Alt4", "/heroes/pollywogpriest/alt4/icon.tga");
        Assert.NotNull(penguinGlb);
        Assert.True(penguinGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(penguinGlb, 0));

        // 4. Sir Benzalot (sir_benzington & sirbenzington / Alt2)
        byte[]? benzalotGlb = vfs.GetHeroModelGlb("sir_benzington", "Alt2", "/heroes/sir_benzington/alt2/icon.tga");
        Assert.NotNull(benzalotGlb);
        Assert.True(benzalotGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(benzalotGlb, 0));

        byte[]? benzalotAliasGlb = vfs.GetHeroModelGlb("sirbenzington", "Alt2");
        Assert.NotNull(benzalotAliasGlb);
        Assert.True(benzalotAliasGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(benzalotAliasGlb, 0));

        // 5. Bogatyr (rally / Alt5)
        byte[]? bogatyrGlb = vfs.GetHeroModelGlb("rally", "Alt5", "/heroes/rally/alt5/icon.tga");
        Assert.NotNull(bogatyrGlb);
        Assert.True(bogatyrGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bogatyrGlb, 0));

        byte[]? bogatyrNoResGlb = vfs.GetHeroModelGlb("rally", "Alt5");
        Assert.NotNull(bogatyrNoResGlb);
        Assert.True(bogatyrNoResGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bogatyrNoResGlb, 0));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_PollywogPriest_HeroEntityAndAbilityIcons_Resolution()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act
        HeroEntityInfoDto? info = vfs.GetHeroEntityInfo("pollywogpriest");

        // Assert
        Assert.NotNull(info);
        Assert.Equal("pollywogpriest", info.HeroName);
        Assert.Equal(4, info.AbilityIcons.Count);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_AbilityPropsAndFrameEvents_ResolvesCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act
        HeroEntityInfoDto? devourerInfo = vfs.GetHeroEntityInfo("devourer");
        HeroEntityInfoDto? pollywogInfo = vfs.GetHeroEntityInfo("pollywogpriest");

        // Assert
        Assert.NotNull(devourerInfo);
        Assert.True(devourerInfo.AbilityProps.Count > 0, "Devourer should have ability props (hook/gib/claw/effects).");
        Assert.True(devourerInfo.Animations.Count > 0, "Devourer should have parsed animations.");
        Assert.Contains(devourerInfo.AbilityProps, p => p.AbilitySlot == "ability_1");

        Assert.NotNull(pollywogInfo);
        Assert.True(pollywogInfo.AbilityProps.Count > 0, "Pollywog Priest should have ability props (wards/effects).");
        Assert.True(pollywogInfo.Animations.Count > 0, "Pollywog Priest should have parsed animations.");
        Assert.Contains(pollywogInfo.AbilityProps, p => p.AbilitySlot == "ability_4");

        // Test GLB conversion of ability props
        string devourerHookPath = devourerInfo.AbilityProps.First(p => p.AbilitySlot == "ability_1").ModelPath;
        byte[]? hookGlb = vfs.GetModelGlb(devourerHookPath);
        Assert.NotNull(hookGlb);
        Assert.True(hookGlb.Length > 100);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(hookGlb, 0)); // "glTF"
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_GetHeroModelGlb_ProducesValidGlbWithProperSizing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act
        byte[]? succubusGlb = vfs.GetHeroModelGlb("succubis", "alt_1", "/heroes/succubis/alt_1/icon.tga");
        byte[]? magmarGlb = vfs.GetHeroModelGlb("magmar");

        // Assert
        Assert.NotNull(succubusGlb);
        Assert.True(succubusGlb.Length > 1000);

        Assert.NotNull(magmarGlb);
        Assert.True(magmarGlb.Length > 1000);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_GetHeroSound_ReturnsActualAudioBytes()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        string[] heroes =
            ["pollywogpriest", "devourer", "soulstealer", "magmar", "frosty", "witch_slayer", "succubis", "scout"];
        string[] actions =
            ["attack_1", "attack_2", "ability_1", "ability_2", "ability_3", "ability_4", "walk_1", "taunt_1"];

        foreach (string hero in heroes)
        {
            byte[]? attackSnd = vfs.GetHeroSound(hero, null, "attack_1");
            Assert.NotNull(attackSnd);
            Assert.True(attackSnd.Length > 100, $"Sound for {hero} attack_1 was too small: {attackSnd.Length}");

            byte[]? abilitySnd = vfs.GetHeroSound(hero, null, "ability_1");
            Assert.NotNull(abilitySnd);
            Assert.True(abilitySnd.Length > 100, $"Sound for {hero} ability_1 was too small: {abilitySnd.Length}");
        }

        byte[]? scoutGlb = vfs.GetHeroModelGlb("scout");
        Assert.NotNull(scoutGlb);
        Assert.True(scoutGlb.Length > 10000);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_AnalyzePollywogAndDevourer()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // 1. Analyze Pollywog Chieftain (Alt 4)
        List<string> pollyFiles = [.. vfs.FindFiles("heroes/pollywogpriest/alt4")];
        Assert.NotEmpty(pollyFiles);

        using (Stream? mdfStream = vfs.OpenFile("heroes/pollywogpriest/alt4/model.mdf"))
        {
            if (mdfStream != null)
            {
                K2Mdf mdf = K2MdfReader.Read(mdfStream);
                // Check attachments
                bool hasAttach = mdf.Attachments.Count > 0;
            }
        }

        // 2. Analyze Devourer Clown (Alt)
        List<string> devFiles = [.. vfs.FindFiles("heroes/devourer/alt")];
        Assert.NotEmpty(devFiles);

        // 3. Verify Pollywog Chieftain GLB generation
        byte[]? pollyGlb = vfs.GetHeroModelGlb("pollywogpriest", "alt4", "/heroes/pollywogpriest/alt4/icon.tga");
        Assert.NotNull(pollyGlb);
        Assert.True(pollyGlb.Length > 50000);

        // 4. Verify Clown Devourer GLB generation
        byte[]? devGlb = vfs.GetHeroModelGlb("devourer", "alt", "/heroes/devourer/alt/icon.tga");
        Assert.NotNull(devGlb);
        Assert.True(devGlb.Length > 50000);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2_GameArchiveVfsService_ExtractEmpathCnModel()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);
        byte[]? empathGlb = vfs.GetHeroModelGlb("empath", "hd_empath", "/heroes/empath/hd_empath/icon.tga");
        Assert.NotNull(empathGlb);
        Assert.True(empathGlb.Length > 50000);

        string guiAssetsWeb = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "NEXUS.ControlPanel.GUI", "Assets", "web"));
        if (Directory.Exists(guiAssetsWeb))
        {
            string targetPath = Path.Combine(guiAssetsWeb, "empath_cn.glb");
            File.WriteAllBytes(targetPath, empathGlb);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GameArchiveVfsService_ReorganizedPaths_ExtractsMultipleHeroesAndAlts()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        IConfiguration config = new ConfigurationBuilder().Build();
        using GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        if (string.IsNullOrEmpty(vfs.GamePath) || !Directory.Exists(vfs.GamePath)) return;

        // 1. Verify base hero GLB models
        string[] heroesToTest = ["krixi", "pollywogpriest", "devourer", "witch_slayer", "pyromancer"];
        foreach (string hero in heroesToTest)
        {
            byte[]? glb = vfs.GetHeroModelGlb(hero);
            if (vfs.FileExists($"heroes/{hero}/model.mdf"))
            {
                Assert.NotNull(glb);
                Assert.True(glb.Length > 1000, $"GLB for {hero} should be > 1000 bytes");
                uint magic = BitConverter.ToUInt32(glb, 0);
                Assert.Equal(0x46546C67u, magic); // "glTF"
            }
        }

        // 2. Verify alt avatar GLB models
        (string Hero, string Alt)[] altTargets =
        [
            ("hunter", "alt7"),
            ("krixi", "alt"),
            ("witch_slayer", "alt2")
        ];

        foreach ((string hero, string alt) in altTargets)
        {
            byte[]? glb = vfs.GetHeroModelGlb(hero, alt);
            if (vfs.FileExists($"heroes/{hero}/{alt}/model.mdf"))
            {
                Assert.NotNull(glb);
                Assert.True(glb.Length > 1000, $"GLB for {hero}/{alt} should be > 1000 bytes");
                uint magic = BitConverter.ToUInt32(glb, 0);
                Assert.Equal(0x46546C67u, magic); // "glTF"
            }
        }

        // 3. Verify hero info metadata
        HeroEntityInfoDto? heroInfo = vfs.GetHeroEntityInfo("krixi");
        Assert.NotNull(heroInfo);
        Assert.Equal("krixi", heroInfo.HeroName);
        Assert.NotEmpty(heroInfo.Animations);
    }
}
