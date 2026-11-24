// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private GameStepHandler _gameStepHandler = null!;
    private AssertVisualHandler _assertVisualHandler = null!;

    public GameStepHandler GameStepHandler => _gameStepHandler;
    public AssertVisualHandler AssertVisualHandler => _assertVisualHandler;

    partial void InitializeControlFabricHandlers(TimeProvider timeProvider)
    {
        _gameStepHandler = new GameStepHandler(
            _gameClientManageHandler,
            _gameServerManageHandler,
            _gameIpcBridge,
            timeProvider);

        _assertVisualHandler = new AssertVisualHandler(
            _windowCapturer,
            _gameClientManageHandler,
            _gameServerManageHandler,
            timeProvider);
    }

    partial void RegisterControlFabricTools()
    {
        // 31. Deterministic Frame Stepping & Coordinated Timescale (Control Fabric)
        RegisterTool(CreateGameStepDefinition("kongor_game_step"), _gameStepHandler.HandleAsync);

        // 32. Perceptual Golden Visual Assertion (Control Fabric)
        RegisterTool(CreateAssertVisualDefinition("kongor_assert_visual"), _assertVisualHandler.HandleAsync);
    }

    public static ToolDefinition CreateGameStepDefinition(string name = "kongor_game_step") => new()
    {
        Name = name,
        Description = "Deterministic frame stepping and synchronized Host.TimeScale simulation acceleration across client IPC and dedicated server workers.",
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
                    Description = "Stepping action to perform: 'step' (advance N frames), 'timescale' (adjust speed), 'reset' (restore 1.0x), or 'status'.",
                    Default = "step",
                    Enum = ["step", "timescale", "speed", "reset", "status"]
                },
                ["frames"] = new()
                {
                    Type = "integer",
                    Title = "Frames to Step",
                    Description = "Number of engine simulation frames to advance synchronously.",
                    Default = 1
                },
                ["speed"] = new()
                {
                    Type = "number",
                    Title = "Simulation Speed (TimeScale)",
                    Description = "Engine simulation timescale multiplier (e.g. 1.0 = normal, 10.0 = 10x accelerated combat simulation).",
                    Default = 1.0
                },
                ["duration_ms"] = new()
                {
                    Type = "integer",
                    Title = "Acceleration Duration (ms)",
                    Description = "Optional duration in milliseconds to run accelerated timescale before automatically restoring 1.0x."
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Execution Plane",
                    Description = "Target to apply timescale/stepping to: 'both' (client + server), 'client', or 'server'.",
                    Default = "both",
                    Enum = ["both", "client", "server"]
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Dedicated Server Port",
                    Description = "Target dedicated server listen port (e.g. 11235) when target is server or both. Auto-resolves if omitted."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateAssertVisualDefinition(string name = "kongor_assert_visual") => new()
    {
        Name = name,
        Description = "Crops target UI widgets or viewport regions from GPU VRAM / window captures and asserts visual fidelity against golden baseline fixtures using perceptual SSIM and MSE.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Required = ["fixture"],
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["fixture"] = new()
                {
                    Type = "string",
                    Title = "Golden Fixture Name or Path",
                    Description = "Name or path of reference golden fixture image (e.g. 'hud_health_bar.png' in fixtures/visual/ or absolute path)."
                },
                ["threshold"] = new()
                {
                    Type = "number",
                    Title = "SSIM Threshold",
                    Description = "Minimum perceptual Structural Similarity Index (SSIM) required to pass assertion (0.0 to 1.0). Defaults to 0.99 for static UI and 0.92 for 3D viewports.",
                    Default = 0.95
                },
                ["mask_path"] = new()
                {
                    Type = "string",
                    Title = "Mask PNG Path",
                    Description = "Optional path to 8-bit grayscale PNG mask (255 = assert, 0 = ignore)."
                },
                ["ignore_viewport_rect"] = new()
                {
                    Type = "object",
                    Title = "Ignore Viewport Rectangle",
                    Description = "Optional dynamic 3D viewport bounding box to exclude from strict 2D matching: { x: int, y: int, width: int, height: int }.",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["x"] = new() { Type = "integer", Title = "X", Description = "Left X pixel coordinate." },
                        ["y"] = new() { Type = "integer", Title = "Y", Description = "Top Y pixel coordinate." },
                        ["width"] = new() { Type = "integer", Title = "Width", Description = "Rectangle width in pixels." },
                        ["height"] = new() { Type = "integer", Title = "Height", Description = "Rectangle height in pixels." }
                    }
                },
                ["widget"] = new()
                {
                    Type = "string",
                    Title = "Target UI Widget",
                    Description = "Optional K2 UI widget path to crop (e.g. 'main:btn_play')."
                },
                ["crop"] = new()
                {
                    Type = "object",
                    Title = "Bounding Box Crop",
                    Description = "Optional explicit bounding box rectangle to crop: { x: int, y: int, width: int, height: int }.",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["x"] = new() { Type = "integer", Title = "X", Description = "Left X pixel coordinate." },
                        ["y"] = new() { Type = "integer", Title = "Y", Description = "Top Y pixel coordinate." },
                        ["width"] = new() { Type = "integer", Title = "Width", Description = "Crop width in pixels." },
                        ["height"] = new() { Type = "integer", Title = "Height", Description = "Crop height in pixels." }
                    }
                },
                ["generate_fixture_if_missing"] = new()
                {
                    Type = "boolean",
                    Title = "Generate Baseline if Missing",
                    Description = "If true and golden fixture does not exist, saves current capture as the new golden baseline.",
                    Default = false
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Window Target",
                    Description = "Target window name or process (e.g. 'hon_x64').",
                    Enum = ["hon_x64", "foreground"],
                    Default = "hon_x64"
                }
            }
        }
    };
}
