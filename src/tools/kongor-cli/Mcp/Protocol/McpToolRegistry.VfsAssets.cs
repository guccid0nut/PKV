// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private VfsMountGraphHandler _vfsMountGraphHandler = null!;
    private TexturePreviewHandler _texturePreviewHandler = null!;
    private TextureInGameInspectHandler _textureInGameInspectHandler = null!;
    private TextureDiffHandler _textureDiffHandler = null!;
    private MaterialInspectHandler _materialInspectHandler = null!;
    private VfsLocateHandler _vfsLocateHandler = null!;
    private AssetBakePipelineHandler _assetBakePipelineHandler = null!;
    private AudioStudioHandler _audioStudioHandler = null!;
    private MirrorSyncHandler _mirrorSyncHandler = null!;
    private VfsCleanCacheHandler _vfsCleanCacheHandler = null!;
    private ValidatePreviewsHandler _validatePreviewsHandler = null!;
    private InspectModelMdfHandler _inspectModelMdfHandler = null!;
    private InspectHeroPoolHandler _inspectHeroPoolHandler = null!;
    private VfsCompletenessHandler _vfsCompletenessHandler = null!;

    public VfsMountGraphHandler VfsMountGraphHandler => _vfsMountGraphHandler;
    public TexturePreviewHandler TexturePreviewHandler => _texturePreviewHandler;
    public TextureInGameInspectHandler TextureInGameInspectHandler => _textureInGameInspectHandler;
    public TextureDiffHandler TextureDiffHandler => _textureDiffHandler;
    public MaterialInspectHandler MaterialInspectHandler => _materialInspectHandler;
    public VfsLocateHandler VfsLocateHandler => _vfsLocateHandler;
    public AssetBakePipelineHandler AssetBakePipelineHandler => _assetBakePipelineHandler;
    public AudioStudioHandler AudioStudioHandler => _audioStudioHandler;
    public MirrorSyncHandler MirrorSyncHandler => _mirrorSyncHandler;
    public VfsCleanCacheHandler VfsCleanCacheHandler => _vfsCleanCacheHandler;
    public ValidatePreviewsHandler ValidatePreviewsHandler => _validatePreviewsHandler;
    public InspectModelMdfHandler InspectModelMdfHandler => _inspectModelMdfHandler;
    public InspectHeroPoolHandler InspectHeroPoolHandler => _inspectHeroPoolHandler;
    public VfsCompletenessHandler VfsCompletenessHandler => _vfsCompletenessHandler;

    partial void InitializeVfsAssetsHandlers(TimeProvider timeProvider)
    {
        _vfsMountGraphHandler = new VfsMountGraphHandler(timeProvider);
        _texturePreviewHandler = new TexturePreviewHandler(_gameIpcBridge, timeProvider);
        _textureInGameInspectHandler = new TextureInGameInspectHandler(
            windowCapturer: _windowCapturer,
            gameIpcBridge: _gameIpcBridge,
            timeProvider: timeProvider);
        _textureDiffHandler = new TextureDiffHandler(timeProvider);
        _materialInspectHandler = new MaterialInspectHandler(timeProvider);
        _vfsLocateHandler = new VfsLocateHandler(timeProvider);
        _assetBakePipelineHandler = new AssetBakePipelineHandler(timeProvider);
        _audioStudioHandler = new AudioStudioHandler(timeProvider);
        _mirrorSyncHandler = new MirrorSyncHandler(timeProvider: timeProvider);
        _vfsCleanCacheHandler = new VfsCleanCacheHandler(timeProvider: timeProvider);
        _validatePreviewsHandler = new ValidatePreviewsHandler(timeProvider: timeProvider);
        _inspectModelMdfHandler = new InspectModelMdfHandler(timeProvider: timeProvider);
        _inspectHeroPoolHandler = new InspectHeroPoolHandler(timeProvider: timeProvider);
        _vfsCompletenessHandler = new VfsCompletenessHandler(timeProvider: timeProvider);
    }

    partial void RegisterVfsAssetsTools()
    {
        RegisterTool(CreateAssetInspectDefinition("kongor_asset_inspect"), _assetInspectHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateEngineCachePurgeDefinition("kongor_engine_cache_purge"), _engineCachePurgeHandler.HandleAsync, McpToolCategory.AssetPipeline);
        RegisterTool(CreateVfsResolveDefinition("kongor_vfs_resolve"), _vfsResolveHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateModBuildDefinition("kongor_mod_build"), _modBuildHandler.HandleAsync, McpToolCategory.AssetPipeline);
        RegisterTool(CreateAudioPreviewDefinition("kongor_audio_preview"), _audioPreviewHandler.HandleAsync, McpToolCategory.AssetPipeline);
        RegisterTool(CreateK2LintDefinition("kongor_k2_lint"), _k2LintHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateHotReloadDefinition("kongor_hot_reload"), _hotReloadHandler.HandleAsync, McpToolCategory.AssetPipeline);

        RegisterTool(CreateReloadAndVerifyDefinition("kongor_reload_and_verify"), _reloadAndVerifyHandler.HandleAsync, McpToolCategory.AssetPipeline);
        RegisterAlias("reload_and_verify", "kongor_reload_and_verify");
        RegisterAlias("kongor_verify_reload", "kongor_reload_and_verify");

        RegisterTool(CreateVfsMountGraphDefinition("kongor_vfs_mount_graph"), _vfsMountGraphHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateTexturePreviewDefinition("kongor_texture_preview"), _texturePreviewHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateTextureInGameInspectDefinition("kongor_texture_in_game_inspect"), _textureInGameInspectHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateTextureDiffDefinition("kongor_texture_diff"), _textureDiffHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateMaterialInspectDefinition("kongor_material_inspect"), _materialInspectHandler.HandleAsync, McpToolCategory.ReadOnly);

        RegisterTool(CreateVfsLocateDefinition("kongor_vfs_locate"), _vfsLocateHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("vfs_locate", "kongor_vfs_locate");

        RegisterTool(CreateAssetBakePipelineDefinition("kongor_asset_bake_pipeline"), _assetBakePipelineHandler.HandleAsync, McpToolCategory.AssetPipeline);
        RegisterAlias("asset_bake_pipeline", "kongor_asset_bake_pipeline");

        RegisterTool(CreateAudioStudioDefinition("kongor_audio_studio"), _audioStudioHandler.HandleAsync, McpToolCategory.AssetPipeline);
        RegisterAlias("audio_studio", "kongor_audio_studio");

        RegisterTool(CreateMirrorSyncDefinition("kongor_sync_mirrors"), _mirrorSyncHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("sync_mirrors", "kongor_sync_mirrors");

        RegisterTool(CreateVfsCleanCacheDefinition("kongor_vfs_clean_cache"), _vfsCleanCacheHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("vfs_clean_cache", "kongor_vfs_clean_cache");

        RegisterTool(CreateValidatePreviewsDefinition("kongor_validate_previews"), _validatePreviewsHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("validate_previews", "kongor_validate_previews");

        RegisterTool(CreateInspectModelMdfDefinition("kongor_inspect_model_mdf"), _inspectModelMdfHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("inspect_model_mdf", "kongor_inspect_model_mdf");

        RegisterTool(CreateInspectHeroPoolDefinition("kongor_inspect_hero_pool"), _inspectHeroPoolHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("inspect_hero_pool", "kongor_inspect_hero_pool");

        RegisterTool(CreateVfsCompletenessDefinition("kongor_vfs_completeness_audit"), _vfsCompletenessHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("vfs_completeness_audit", "kongor_vfs_completeness_audit");
    }

    public static ToolDefinition CreateAssetInspectDefinition(string name = "kongor_asset_inspect") => new()
    {
        Name = name,
        Description = "Binary sanity checker for game engine texture and asset files (.tga, .dds). Inspects dimensions, bit depth, alpha channels, K2 coordinate origin orientation (flags descriptor byte bit 5 0x20/0x28 top-left origin as inverted texture warning), and detects blank/solid textures ($white, $black, $invis). Resolves paths across VFS mount order and .s2z archives.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["path"] = new()
                {
                    Type = "string",
                    Title = "Asset Path",
                    Description = "Direct file path or K2 VFS path to the texture/asset (e.g. 'ui/fe2/mainmenu/logo_fb.tga' or 'core/brushes/standard/brush1.dds').",
                    Examples = ["ui/fe2/mainmenu/logo_fb.tga", "games/shared/00000000/ui/fe2/mainmenu/logo_fb.tga", "core/brushes/standard/brush1.dds"]
                }
            },
            Required = ["path"]
        }
    };

    public static ToolDefinition CreateReloadAndVerifyDefinition(string name = "kongor_reload_and_verify") => new()
    {
        Name = name,
        Description = "The single golden path for reloading client UI/scripts: synchronizes TAO build hash on disk, runs pre-flight Lua syntax verification, captures diagnostic checkpoint, triggers hot reload, verifies TAO is not stale, checks health, and performs a post-reload canary log observation scan.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["canaryDurationSeconds"] = new()
                {
                    Type = "integer",
                    Title = "Canary Duration (seconds)",
                    Description = "Duration in seconds to monitor engine logs after reload for delayed Lua errors (default: 5, min: 1, max: 60).",
                    Default = 5
                },
                ["skipLint"] = new()
                {
                    Type = "boolean",
                    Title = "Skip Lint",
                    Description = "Skip pre-flight verify_client_lua.ps1 syntax check (default: false).",
                    Default = false
                },
                ["skipBuildSync"] = new()
                {
                    Type = "boolean",
                    Title = "Skip Build Sync",
                    Description = "Skip running update_tao_build.ps1 (default: false).",
                    Default = false
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateEngineCachePurgeDefinition(string name = "kongor_engine_cache_purge") => new()
    {
        Name = name,
        Description = "Performs targeted atomic purging of engine cache directories (shadercache, texturecache, luadata, and dxvk Vulkan cache) across client and server. Cleans UTF-8 BOM headers from cache XML files, and coordinates in-game console hot-reloading (reloadshaders, reloadtextures, reloadmaterials, reloadui) via IPC if the game client is running.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["targets"] = new()
                {
                    Type = "array",
                    Title = "Targets",
                    Description = "Specific cache target areas to purge: 'all', 'shaders', 'textures', 'luadata', or 'dxvk'. Default: ['all'].",
                    Examples = ["all", "shaders", "textures", "luadata", "dxvk"],
                    Items = new ToolPropertySchema
                    {
                        Type = "string",
                        Title = "Cache Target",
                        Description = "Cache area identifier.",
                        Enum = ["all", "shaders", "textures", "luadata", "dxvk"]
                    }
                },
                ["hotReload"] = new()
                {
                    Type = "boolean",
                    Title = "Hot Reload",
                    Description = "Whether to dispatch in-game IPC console commands (reloadshaders, reloadtextures, reloadmaterials, reloadui) if hon_x64 is running.",
                    Default = true
                },
                ["cleanBoms"] = new()
                {
                    Type = "boolean",
                    Title = "Clean BOMs",
                    Description = "Whether to scan for and strip UTF-8 Byte Order Marks (0xEF, 0xBB, 0xBF) from XML cache files.",
                    Default = true
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateVfsResolveDefinition(string name = "kongor_vfs_resolve") => new()
    {
        Name = name,
        Description = "Resolve virtual file system paths across K2's active priority stack (override -> KONGOR/* -> ../shared -> ClientPackageFiles -> game/*.s2z -> base/*.s2z) with .s2z Central Directory inspection and shadow-collision auditing ('audit_shadows').",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "VFS operation: 'resolve' (default), 'find' (wildcard search), 'audit_shadows' (audit override/ collisions against ClientPackageFiles and .s2z archives), or 'verify_zlib'.",
                    Enum = ["resolve", "find", "audit_shadows", "verify_zlib"],
                    Default = "resolve"
                },
                ["virtualPath"] = new()
                {
                    Type = "string",
                    Title = "Virtual Path",
                    Description = "Virtual file system path to resolve (e.g. '/ui/scripts/store2.lua', 'heroes/pyromancer/hero.entity')."
                },
                ["pattern"] = new()
                {
                    Type = "string",
                    Title = "Search or Audit Pattern",
                    Description = "Optional wildcard or substring filter for 'find' or 'audit_shadows' (e.g. 'heroes/frieren/*', 'ui/scripts/*')."
                },
                ["includeArchives"] = new()
                {
                    Type = "boolean",
                    Title = "Include .s2z Archives",
                    Description = "Whether to inspect mounted .s2z Central Directories (resources99..0.s2z, textures.s2z).",
                    Default = true
                },
                ["checkMounts"] = new()
                {
                    Type = "boolean",
                    Title = "Check Mounts",
                    Description = "Whether to list all matching mount sources and detect layer collisions.",
                    Default = true
                },
                ["onlyWinners"] = new()
                {
                    Type = "boolean",
                    Title = "Only Winners",
                    Description = "For 'find' action, whether to omit shadowed lower-priority files from the output.",
                    Default = false
                },
                ["maxResults"] = new()
                {
                    Type = "integer",
                    Title = "Max Results",
                    Description = "Maximum number of results or collision entries to return (default: 100).",
                    Default = 100
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateModBuildDefinition(string name = "kongor_mod_build") => new()
    {
        Name = name,
        Description = "Package, validate, or build .honmod and .s2z modification archives from loose source files with checksums and manifest validation. Supports live hot-reloading (ReloadTextures & ReloadInterfaces) and shared server sync.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["sourceDirectory"] = new()
                {
                    Type = "string",
                    Title = "Source Directory",
                    Description = "Root directory containing mod files or unpacked assets."
                },
                ["outputFile"] = new()
                {
                    Type = "string",
                    Title = "Output File Path",
                    Description = "Target output file path for the .honmod or .s2z archive."
                },
                ["modType"] = new()
                {
                    Type = "string",
                    Title = "Mod Type",
                    Description = "Archive format to produce: 'honmod' or 's2z'.",
                    Enum = ["honmod", "s2z"],
                    Default = "honmod"
                },
                ["compress"] = new()
                {
                    Type = "boolean",
                    Title = "Compress",
                    Description = "Whether to enable Deflate compression.",
                    Default = true
                },
                ["hotReload"] = new()
                {
                    Type = "boolean",
                    Title = "Hot Reload Live Client",
                    Description = "Whether to dispatch ReloadTextures and ReloadInterfaces via IPC to live game client.",
                    Default = false
                },
                ["syncShared"] = new()
                {
                    Type = "boolean",
                    Title = "Sync To Shared",
                    Description = "Whether to mirror the compiled .s2z mod to games/shared/ for dedicated server compatibility.",
                    Default = false
                }
            },
            Required = ["sourceDirectory"]
        }
    };

    public static ToolDefinition CreateAudioPreviewDefinition(string name = "kongor_audio_preview") => new()
    {
        Name = name,
        Description = "Inspect, decode, and preview K2 audio assets (.wav, .ogg, .mp3, .s2z audio entries) including duration, sample rate, channels, format verification, and live PlaySound dispatch to client and dedicated server workers.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["soundPath"] = new()
                {
                    Type = "string",
                    Title = "Sound Path",
                    Description = "Virtual or physical path to audio file (e.g. '/shared/sounds/ui/button_click.wav')."
                },
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Audio operation to perform: 'info', 'preview', 'play', or 'export_wav'.",
                    Enum = ["info", "preview", "play", "export_wav"],
                    Default = "info"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Playback Target",
                    Description = "Target endpoint for 'play' action: 'client', 'server', or 'both'.",
                    Enum = ["client", "server", "both"],
                    Default = "client"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Listen Port",
                    Description = "Target dedicated server listen port (e.g. 11235) when target is 'server' or 'both'."
                },
                ["outputDirectory"] = new()
                {
                    Type = "string",
                    Title = "Output Directory",
                    Description = "Optional output directory to save exported .wav audio files."
                }
            },
            Required = ["soundPath"]
        }
    };

    public static ToolDefinition CreateK2LintDefinition(string name = "kongor_k2_lint") => new()
    {
        Name = name,
        Description = "Fast offline static linter for in-game K2 XML packages (.package, .interface) and Lua scripts (.lua). Validates UTF-8 BOM, XML well-formedness, unescaped entities in event attributes, template integrity, and K2 engine Lua rules without requiring a running game client.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["path"] = new()
                {
                    Type = "string",
                    Title = "Path",
                    Description = "File path, directory path, or glob pattern to lint (e.g. 'games/client/override/ui/watch_system_v2.package' or 'games/client/override/ui/'). Aliases: 'file_path', 'filePath', 'target_path', 'targetPath', 'file'."
                },
                ["recursive"] = new()
                {
                    Type = "boolean",
                    Title = "Recursive",
                    Description = "Whether to recursively scan child directories (default: true).",
                    Default = true
                },
                ["fix_bom"] = new()
                {
                    Type = "boolean",
                    Title = "Fix BOM",
                    Description = "Automatically strip UTF-8 Byte Order Marks (BOM) from scanned files (default: false).",
                    Default = false
                },
                ["severity_threshold"] = new()
                {
                    Type = "string",
                    Title = "Severity Threshold",
                    Description = "Minimum severity to report ('error' or 'warning', default: 'warning').",
                    Enum = ["error", "warning"],
                    Default = "warning"
                }
            },
            Required = ["path"]
        }
    };

    public static ToolDefinition CreateHotReloadDefinition(string name = "kongor_hot_reload") => new()
    {
        Name = name,
        Description = "Sub-second UI and Lua hot-reloading service monitoring 'games/client/override/ui/' and 'games/client/override/ui/scripts/'. Debounces filesystem change events (default: 150ms) and dispatches in-engine 'ReloadInterfaces' and 'Script dofile(\"/ui/scripts/nexus_bridge.lua\")' over Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) in under 200ms without restarting the game process.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Hot-reload action to perform: 'watch' (start watching paths with debounce), 'unwatch' (stop watching), 'trigger' (immediately force reload sequence over IPC), or 'status' (query current watcher state and reload metrics).",
                    Enum = ["watch", "unwatch", "trigger", "status"]
                },
                ["paths"] = new()
                {
                    Type = "array",
                    Title = "Paths",
                    Description = "List of directory or file paths to watch for changes (e.g. ['games/client/override/ui/', 'games/client/override/ui/scripts/']). If omitted during 'watch', defaults to games/client/override/ui/ and games/client/override/ui/scripts/.",
                    Examples = ["games/client/override/ui/", "games/client/override/ui/scripts/"],
                    Items = new ToolPropertySchema
                    {
                        Type = "string",
                        Title = "Watch Path",
                        Description = "Directory or file path to watch."
                    }
                },
                ["debounceMs"] = new()
                {
                    Type = "integer",
                    Title = "Debounce (ms)",
                    Description = "Sliding debounce window in milliseconds before dispatching reload over IPC (default: 150ms).",
                    Default = 150
                },
                ["files"] = new()
                {
                    Type = "array",
                    Title = "Files",
                    Description = "Optional list of files to associate with a manual 'trigger' action.",
                    Examples = ["games/client/override/ui/main.interface", "games/client/override/ui/scripts/nexus_bridge.lua"],
                    Items = new ToolPropertySchema
                    {
                        Type = "string",
                        Title = "File Path",
                        Description = "File path associated with trigger."
                    }
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateVfsMountGraphDefinition(string name = "kongor_vfs_mount_graph") => new()
    {
        Name = name,
        Description = "Visual inspection and resolution of virtual filesystem mount layers across .s2z archives and mod directories, reporting priority conflicts and shadowed assets.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "VFS inspection action: inspect_mounts, resolve_conflict, detect_shadowing.",
                    Enum = ["inspect_mounts", "resolve_conflict", "detect_shadowing"],
                    Default = "inspect_mounts"
                },
                ["game_dir"] = new() { Type = "string", Title = "Game Directory", Description = "Base HoN game installation path." },
                ["resource"] = new() { Type = "string", Title = "Resource Path", Description = "VFS relative resource path to resolve (e.g. 'ui/main.interface')." }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateTexturePreviewDefinition(string name = "kongor_texture_preview") => new()
    {
        Name = name,
        Description = "Extracts, decodes, and decomposes K2 game textures (.tga, .dds, .png). Reconstructs HoN RxGB normal maps into true tangent-space blue/purple maps, isolates alpha/specular, and generates 2x2 composite channel mosaics.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["path"] = new()
                {
                    Type = "string",
                    Title = "Texture Path",
                    Description = "Path to texture (e.g. 'heroes/pyromancer/color.tga', 'heroes/magmus/normal.dds', 'ui/elements/minimap.dds')."
                },
                ["channel"] = new()
                {
                    Type = "string",
                    Title = "Channel Mode",
                    Description = "Channel extraction: 'diffuse' (RGB), 'alpha' (grayscale mask), 'normal' (RxGB reconstructed tangent normal), 'specular' (glossiness), or 'mosaic' (2x2 grid of all channels).",
                    Enum = ["diffuse", "alpha", "normal", "specular", "mosaic"],
                    Default = "diffuse"
                },
                ["save_artifact"] = new()
                {
                    Type = "boolean",
                    Title = "Save PNG Artifact",
                    Description = "Whether to save the preview PNG file to the artifacts directory.",
                    Default = true
                },
                ["include_base64"] = new()
                {
                    Type = "boolean",
                    Title = "Include Base64 Preview",
                    Description = "Whether to include data:image/png;base64 URI in the tool response.",
                    Default = true
                },
                ["reload_textures"] = new()
                {
                    Type = "boolean",
                    Title = "Reload Textures via IPC",
                    Description = "Whether to immediately dispatch 'ReloadTextures' to the running game client via IPC.",
                    Default = false
                }
            },
            Required = ["path"]
        }
    };

    public static ToolDefinition CreateTextureInGameInspectDefinition(string name = "kongor_texture_in_game_inspect") => new()
    {
        Name = name,
        Description = "Examines textures live inside the running K2 engine (hon_x64.exe): frames camera on target hero/model, triggers ReloadTextures to re-upload VRAM assets from disk, and captures high-resolution rendered viewport screenshot.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero / Model",
                    Description = "Target hero entity name (e.g. 'Hero_Pyromancer', 'Hero_Magmus').",
                    Default = "Hero_Pyromancer"
                },
                ["texture"] = new()
                {
                    Type = "string",
                    Title = "Texture Path",
                    Description = "Optional texture path being inspected."
                },
                ["reload_textures"] = new()
                {
                    Type = "boolean",
                    Title = "Trigger ReloadTextures",
                    Description = "Whether to dispatch 'ReloadTextures' console command to force VRAM reload.",
                    Default = true
                },
                ["camera_zoom"] = new()
                {
                    Type = "string",
                    Title = "Camera Zoom",
                    Description = "Camera distance: 'close' (400 units), 'medium' (800 units), or 'wide' (1400 units).",
                    Enum = ["close", "medium", "wide"],
                    Default = "close"
                },
                ["camera_preset"] = new()
                {
                    Type = "string",
                    Title = "Camera Preset",
                    Description = "Camera preset angle: 'front', 'side', 'back', 'top', or 'portrait'.",
                    Enum = ["front", "side", "back", "top", "portrait"],
                    Default = "front"
                }
            },
            Required = ["hero"]
        }
    };

    public static ToolDefinition CreateTextureDiffDefinition(string name = "kongor_texture_diff") => new()
    {
        Name = name,
        Description = "Perceptual visual comparison between two textures: computes SSIM (Structural Similarity Index), PSNR, and MSE metrics, detects placeholder or white-out anomalies, and outputs a highlighted difference heatmap image.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["baseline"] = new()
                {
                    Type = "string",
                    Title = "Baseline Texture Path",
                    Description = "Path to original / baseline texture."
                },
                ["candidate"] = new()
                {
                    Type = "string",
                    Title = "Candidate Texture Path",
                    Description = "Path to modified / candidate texture."
                },
                ["tolerance"] = new()
                {
                    Type = "number",
                    Title = "Drift Tolerance",
                    Description = "Allowed perceptual SSIM drift tolerance (0.0001 - 1.0, default 0.05). If SSIM is less than (1.0 - tolerance), anomaly is flagged.",
                    Default = 0.05
                },
                ["generate_heatmap"] = new()
                {
                    Type = "boolean",
                    Title = "Generate Heatmap",
                    Description = "Whether to generate and save a pixel diff heatmap PNG artifact.",
                    Default = true
                }
            },
            Required = ["baseline", "candidate"]
        }
    };

    public static ToolDefinition CreateMaterialInspectDefinition(string name = "kongor_material_inspect") => new()
    {
        Name = name,
        Description = "Parses K2 .material XML definitions: extracts bound shaders and samplers (diffuse, normalmap, specular, glow), verifies VFS texture paths, audits dedicated server shared layer compatibility, and checks power-of-two (POT) resolution compatibility.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["path"] = new()
                {
                    Type = "string",
                    Title = "Material Path",
                    Description = "Path to .material XML file (e.g. 'heroes/pyromancer/hero.material')."
                }
            },
            Required = ["path"]
        }
    };

    public static ToolDefinition CreateVfsLocateDefinition(string name = "kongor_vfs_locate") => new()
    {
        Name = name,
        Description = "Fast K2 Virtual File System Disk Locator & Everything Indexer Bridge: maps virtual asset paths to physical files across mount layers (override -> extensions -> updates -> configuration -> game -> shared -> base) with sub-ms Voidtools Everything NTFS MFT discovery.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["virtual_path"] = new()
                {
                    Type = "string",
                    Title = "Virtual Asset Path",
                    Description = "Virtual asset path (e.g. '/heroes/pyromancer/ability_01/icon.tga') or filename (e.g. 'pyromancer.entity') to resolve across mount layers. Aliases: 'virtualPath', 'path', 'file_path', 'filePath', 'file', 'query', 'filename'."
                },
                ["refresh_cache"] = new()
                {
                    Type = "boolean",
                    Title = "Refresh Cache",
                    Description = "Force purge internal resolution cache to ensure newly baked or modified files are discovered immediately.",
                    Default = false
                }
            },
            Required = ["virtual_path"]
        }
    };

    public static ToolDefinition CreateAssetBakePipelineDefinition(string name = "kongor_asset_bake_pipeline") => new()
    {
        Name = name,
        Description = "Generative 2D/3D Asset Baking Pipeline: Suffix-aware texture compression (BC7 sRGB color, BC5 linear tangent normal with OpenGL Y-inversion, BC4 scalar specular/glow), POT dimension enforcement, missing texture pre-flight guard, K2 .material XML synthesis, and hot-reloading.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Asset baking operation: 'inspect' (analyzes texture and suggests texconv flags), 'bake_material' (synthesizes K2 .material XML and texture recipes), or 'hot_reload' (triggers live material refresh).",
                    Default = "inspect",
                    Enum = ["inspect", "bake_material", "hot_reload"]
                },
                ["material_name"] = new()
                {
                    Type = "string",
                    Title = "Material Name",
                    Description = "Identifier for the target material (e.g. 'pyromancer_alt1', 'hero_bubbles').",
                    Default = "custom_material"
                },
                ["shader_type"] = new()
                {
                    Type = "string",
                    Title = "Shader Type",
                    Description = "K2 shader template: 'hero' (with teamcolor mask), 'mesh' (static entities), 'effect' (translucent particles), or 'default'.",
                    Default = "hero",
                    Enum = ["hero", "mesh", "effect", "default"]
                },
                ["texture_path"] = new()
                {
                    Type = "string",
                    Title = "Texture File Path",
                    Description = "Input texture file path to inspect or compress (e.g. 'hero_color.png', 'hero_norm.png')."
                },
                ["invert_normal_y"] = new()
                {
                    Type = "boolean",
                    Title = "Invert Normal Y",
                    Description = "Invert green channel (Y-axis) for normal maps to translate OpenGL Y+ (Blender/ComfyUI) to DirectX Y- (K2 engine).",
                    Default = true
                },
                ["write_to_disk"] = new()
                {
                    Type = "boolean",
                    Title = "Write To Disk",
                    Description = "Whether to write the generated .material XML directly to disk.",
                    Default = false
                },
                ["output_directory"] = new()
                {
                    Type = "string",
                    Title = "Output Directory",
                    Description = "Target directory on disk when write_to_disk is true."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateAudioStudioDefinition(string name = "kongor_audio_studio") => new()
    {
        Name = name,
        Description = "Audio Voice & SFX Studio Pipeline: Broadcast audio loudness normalization (EBU R128 -16 LUFS, -1.5 dBFS peak), channel layout routing (mono WAV for 3D spatialization vs stereo OGG for music), multi-sample randomized K2 .sound XML definition synthesis, ElevenLabs prompt handoff, and in-engine preview.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Audio operation: 'normalize' (formulates FFmpeg EBU R128 loudness command), 'synthesize_voice' (creates ElevenLabs handoff), 'bake_sound' (generates multi-sample K2 .sound XML), or 'preview' (in-engine 3D audition).",
                    Default = "normalize",
                    Enum = ["normalize", "synthesize_voice", "bake_sound", "preview"]
                },
                ["input_file"] = new()
                {
                    Type = "string",
                    Title = "Input File Path",
                    Description = "Source audio file to normalize (e.g. 'voice_raw.wav', 'spell_cast.wav')."
                },
                ["audio_type"] = new()
                {
                    Type = "string",
                    Title = "Audio Type",
                    Description = "Audio classification: 'voice' (mono WAV), 'sfx' (mono WAV), 'music' (stereo OGG), 'announcer' (stereo OGG), or 'ambient' (stereo OGG).",
                    Default = "voice",
                    Enum = ["voice", "sfx", "music", "announcer", "ambient"]
                },
                ["sound_name"] = new()
                {
                    Type = "string",
                    Title = "Sound Identifier",
                    Description = "Name of the sound event in K2 engine (e.g. 'hero_attack', 'ability_cast').",
                    Default = "hero_sound"
                },
                ["sample_files"] = new()
                {
                    Type = "array",
                    Title = "Sample Files",
                    Description = "Array of audio filepaths for multi-sample randomized playback.",
                    Items = new ToolPropertySchema
                    {
                        Type = "string",
                        Title = "Sample File Path",
                        Description = "Audio file path."
                    }
                },
                ["lufs"] = new()
                {
                    Type = "number",
                    Title = "Integrated Loudness LUFS",
                    Description = "Target integrated loudness (EBU R128 broadcast standard: -16.0 LUFS).",
                    Default = -16.0
                },
                ["true_peak"] = new()
                {
                    Type = "number",
                    Title = "True Peak Ceiling dBFS",
                    Description = "Maximum true peak level (standard: -1.5 dBFS).",
                    Default = -1.5
                },
                ["prompt"] = new()
                {
                    Type = "string",
                    Title = "Speech Dialogue Prompt",
                    Description = "Spoken text line to generate via ElevenLabs."
                },
                ["voice_id"] = new()
                {
                    Type = "string",
                    Title = "ElevenLabs Voice ID",
                    Description = "Voice profile ID for synthesis."
                },
                ["write_to_disk"] = new()
                {
                    Type = "boolean",
                    Title = "Write To Disk",
                    Description = "Whether to write the generated .sound XML directly to disk.",
                    Default = false
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateMirrorSyncDefinition(string name = "kongor_sync_mirrors") => new()
    {
        Name = name,
        Description = "Multi-Directory Mirror Synchronization Supervisor: instantly mirrors client scripts, catalogs, and 141 modular hero definitions across the 5+ K2 VFS directory mounts (games/client/override, games/client/ClientPackageFiles, games/server/override, games/shared/override, games/client/game). Employs SHA-256 diffing to eliminate redundant disk writes and manages real-time FileSystemWatcher background supervision.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Mirror operation: 'sync' (performs synchronization sweep), 'diff' (detects directory drift and mismatches via SHA-256 without copying), 'watch' (starts background FileSystemWatcher), 'unwatch' (stops watcher), or 'status' (reports watcher status and metrics).",
                    Default = "sync",
                    Enum = ["sync", "diff", "watch", "unwatch", "status"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Filter",
                    Description = "Scope of files to synchronize: 'all' (all 12 core files + 141 modular heroes), 'heroes' (modular hero definition files only), 'catalogs' (items, cosmetics, avatar_index), or 'scripts' (UI and logger scripts).",
                    Default = "all",
                    Enum = ["all", "heroes", "hero_definitions", "catalogs", "scripts"]
                },
                ["dry_run"] = new()
                {
                    Type = "boolean",
                    Title = "Dry Run",
                    Description = "If true, simulates the sync operation without writing to disk. Default is false.",
                    Default = false
                },
                ["force"] = new()
                {
                    Type = "boolean",
                    Title = "Force Copy",
                    Description = "If true, bypasses SHA-256 hash checks and forces file copies across all destinations. Default is false.",
                    Default = false
                },
                ["debounce_ms"] = new()
                {
                    Type = "integer",
                    Title = "Debounce (ms)",
                    Description = "Debounce delay for FileSystemWatcher in milliseconds when action='watch'. Default is 200.",
                    Default = 200
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateVfsCleanCacheDefinition(string name = "kongor_vfs_clean_cache") => new()
    {
        Name = name,
        Description = "VFS Engine Cache Purge & File Lock Invalidation: clears cached .s2z indexes, temporary K2 engine cache folders (%USERPROFILE%/Documents/Heroes of Newerth/game/cache/), and shadow files to ensure newly synchronized models, textures, and scripts are loaded immediately without file lock conflicts.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Cache purge operation: 'clean' (purges cache files and returns freed space) or 'status' (inspects cache size without deleting).",
                    Default = "clean",
                    Enum = ["clean", "status"]
                },
                ["purge_user_cache"] = new()
                {
                    Type = "boolean",
                    Title = "Purge User Document Cache",
                    Description = "If true, also clears Documents/Heroes of Newerth/game/cache/. Default is true.",
                    Default = true
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateValidatePreviewsDefinition(string name = "kongor_validate_previews") => new()
    {
        Name = name,
        Description = "Static Asset & Skeletal Verification (Utah Teapot Detection): audits heroes_preview.xml and traverses the full VFS asset DAG (.mdf -> .model -> .clip) across all mounted folders to detect missing mesh/clip files and bone hierarchy mismatches before launching the game client. Eliminates Utah teapots at build time.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero Filter",
                    Description = "Hero entity key or filter to validate (e.g. 'Hero_Fayde', 'Hero_Pyromancer', 'Hero_Artillery', or 'all'). Default is 'all'.",
                    Default = "all"
                },
                ["strict_bone_check"] = new()
                {
                    Type = "boolean",
                    Title = "Strict Bone Check",
                    Description = "If true, parses binary .model and .clip files to verify bone count compatibility. Default is false.",
                    Default = false
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Issue Limit",
                    Description = "Maximum number of teapot issues to return before truncating. Default is 100.",
                    Default = 100
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateInspectModelMdfDefinition(string name = "kongor_inspect_model_mdf") => new()
    {
        Name = name,
        Description = "Deep Model & MDF Asset Forensics: parses the .mdf manifest, decodes K2 binary .model and .clip file headers, extracts vertex/face/bone counts, and performs bone-by-bone cross-validation to isolate broken animations and missing mesh components for a specific hero or avatar.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero Key",
                    Description = "Hero entity key (e.g. 'Hero_Fayde', 'Hero_Artillery', 'Hero_Accursed')."
                },
                ["alt"] = new()
                {
                    Type = "string",
                    Title = "Alt / Avatar Key",
                    Description = "Avatar or alt skin folder name (e.g. 'default', 'alt', 'alt2'). Default is 'default'.",
                    Default = "default"
                },
                ["path"] = new()
                {
                    Type = "string",
                    Title = "MDF Path",
                    Description = "Direct VFS path to .mdf manifest (e.g. 'heroes/fayde/alt/preview.mdf'). If omitted, inferred from hero and alt."
                }
            },
            Required = ["hero"]
        }
    };

    public static ToolDefinition CreateInspectHeroPoolDefinition(string name = "kongor_inspect_hero_pool") => new()
    {
        Name = name,
        Description = "Headless Hero Pool & Entity Reconciliation Inspector: performs 3-way reconciliation between server-side registered hero entities, client pick-screen grid (heroes_preview.xml), and modular Lua definition modules. Instantly diagnoses why heroes are missing from the pick screen or store.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["filter_missing_only"] = new()
                {
                    Type = "boolean",
                    Title = "Filter Missing Only",
                    Description = "If true, returns only discrepancy diffs (missing on server/client/lua). If false, includes full roster lists. Default is true.",
                    Default = true
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateVfsCompletenessDefinition(string name = "kongor_vfs_completeness_audit") => new()
    {
        Name = name,
        Description = "Static VFS Asset Completeness Auditor: cross-references 100% of UI widgets, MDF models, animation clips, and material sampler textures against K2 VFS mount layers (override, game, resources99..0.s2z, textures.s2z, and 00000000/ prefixes) to eliminate silent white dummy textures, pink checkerboards (#FF00FF), and Utah Teapot model fallbacks (null.mdf) before client boot.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["scope"] = new()
                {
                    Type = "string",
                    Title = "Asset Scope",
                    Description = "Asset domain to audit: 'all' (UI packages, MDF manifests, and materials), 'ui' (interfaces and packages only), 'models' (MDF and clips only), or 'materials' (material shaders and samplers only). Default is 'all'.",
                    Default = "all",
                    Enum = ["all", "ui", "models", "materials"]
                },
                ["target_path"] = new()
                {
                    Type = "string",
                    Title = "Target Path Filter",
                    Description = "Optional subpath or file to restrict the audit (e.g. 'ui/fe2/store' or 'heroes/kunas')."
                },
                ["strict"] = new()
                {
                    Type = "boolean",
                    Title = "Strict Mode",
                    Description = "If true, treats non-fatal warnings as validation errors. Default is false.",
                    Default = false
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Result Limit",
                    Description = "Maximum number of issues to report. Default is 100.",
                    Default = 100
                }
            },
            Required = []
        }
    };
}
