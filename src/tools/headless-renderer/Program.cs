// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;

namespace Kongor.HeadlessRenderer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Prevent headless mock ViewModels (e.g. DashboardViewModel) from spawning live Docker CLI
        // probes that can outlive this short-lived render process and keep stdout/stderr pipes open.
        Environment.SetEnvironmentVariable("KONGOR_HEADLESS_RENDER", "1");

        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            Console.WriteLine("Kongor Headless UI Renderer CLI");
            Console.WriteLine("Usage: kongor-render --view <ViewName> [--width <w>] [--height <h>] [--theme <dark|light>] [--output <path>]");
            return 0;
        }

        string viewName = string.Empty;
        int width = 1280;
        int height = 720;
        string theme = "dark";
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--view" && i + 1 < args.Length) viewName = args[++i];
            else if (args[i] == "--width" && i + 1 < args.Length && int.TryParse(args[++i], out int w)) width = w;
            else if (args[i] == "--height" && i + 1 < args.Length && int.TryParse(args[++i], out int h)) height = h;
            else if (args[i] == "--theme" && i + 1 < args.Length) theme = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
        }

        if (string.IsNullOrWhiteSpace(viewName))
        {
            Console.Error.WriteLine("Error: --view parameter is required.");
            return 1;
        }

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync(
            viewName,
            width: width,
            height: height,
            theme: theme,
            mockData: true,
            saveArtifact: outputPath != null,
            customOutputPath: outputPath);

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return result.Success ? 0 : 1;
    }
}
