// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// 2D bounding box and paint metadata for UI layout defect analysis (#87 Option IIII).
/// </summary>
public sealed record UiLayoutBox(
    string Name,
    string Type,
    double X,
    double Y,
    double Width,
    double Height,
    string Ink = "#ffffff",
    int ZOrder = 0,
    bool IsOpaqueFill = false,
    double? LineEndX = null,
    double? LineEndY = null);

/// <summary>
/// Structured 2D UI layout defect detected by K2UiInspectHandler (#87 Option IIII).
/// Kinds: "cut", "overlap", "small", "crosses", "covers".
/// </summary>
public sealed record UiLayoutDefect(
    string Kind,
    string WidgetName,
    string? OtherWidgetName,
    string Message,
    double X,
    double Y,
    double Width,
    double Height,
    int DefectNumber);

/// <summary>
/// MCP Tool Handler for 'kongor_ui_inspect' (aliases: 'kongor_ui_tree', 'kongor_k2_ui_inspect').
/// Dispatches dual-mode K2 UI introspection queries + ManimGX 5-kind 2D layout defect detection
/// ('cut', 'overlap' with identical-ink drop-shadow exemption, 'small', Liang-Barsky 'crosses', 7x7 'covers')
/// and 2x3 contact-sheet generation.
/// Policy: Strict Explicit Typing (NO 'var').
/// </summary>
public sealed class K2UiInspectHandler
{
    private static readonly Regex SiblingIndexRegex = new(@"(\[\d+\]|_\d+|\.\d+)$", RegexOptions.Compiled);

    private readonly IK2UiInspectService _inspectService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public K2UiInspectHandler(
        IK2UiInspectService? inspectService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [K2UiInspectHandler] {msg}"));
        _inspectService = inspectService ?? new K2UiInspectService(timeProvider: _timeProvider, logger: _logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? targetPath = null;
        if (arguments.TryGetProperty("path", out JsonElement pathElem) && !string.IsNullOrWhiteSpace(pathElem.GetString()))
        {
            targetPath = pathElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("interface_path", out JsonElement ipElem) && !string.IsNullOrWhiteSpace(ipElem.GetString()))
        {
            targetPath = ipElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("package", out JsonElement pkgElem) && !string.IsNullOrWhiteSpace(pkgElem.GetString()))
        {
            targetPath = pkgElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("target", out JsonElement tgtElem) && !string.IsNullOrWhiteSpace(tgtElem.GetString()))
        {
            string raw = tgtElem.GetString()!.Trim();
            if (raw.EndsWith(".interface", StringComparison.OrdinalIgnoreCase) ||
                raw.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains('/') || raw.Contains('\\'))
            {
                targetPath = raw;
            }
        }
        else if ((arguments.TryGetProperty("interface", out JsonElement ifaceElem) ||
                  arguments.TryGetProperty("interface_name", out ifaceElem) ||
                  arguments.TryGetProperty("interfaceName", out ifaceElem)) &&
                 !string.IsNullOrWhiteSpace(ifaceElem.GetString()))
        {
            string rawIface = ifaceElem.GetString()!.Trim();
            if (rawIface.EndsWith(".interface", StringComparison.OrdinalIgnoreCase) ||
                rawIface.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
                rawIface.Contains('/') || rawIface.Contains('\\'))
            {
                targetPath = rawIface;
            }
            else if (string.Equals(rawIface, "main", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = "ui/fe2/main.interface";
            }
            else
            {
                targetPath = $"ui/{rawIface}.interface";
            }
        }

        string? widgetName = null;
        if (arguments.TryGetProperty("widget_name", out JsonElement wElem) && !string.IsNullOrWhiteSpace(wElem.GetString()))
        {
            widgetName = wElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("widget", out JsonElement w2Elem) && !string.IsNullOrWhiteSpace(w2Elem.GetString()))
        {
            widgetName = w2Elem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("element", out JsonElement elElem) && !string.IsNullOrWhiteSpace(elElem.GetString()))
        {
            widgetName = elElem.GetString()!.Trim();
        }

        bool isFullDetail = false;
        if ((arguments.TryGetProperty("detail", out JsonElement detElem) &&
             string.Equals(detElem.GetString(), "full", StringComparison.OrdinalIgnoreCase)) ||
            (arguments.TryGetProperty("verbose", out JsonElement verbElem) && verbElem.ValueKind == JsonValueKind.True))
        {
            isFullDetail = true;
        }

        int depth = 10;
        int displayDepth = isFullDetail ? 4 : 2;
        if ((arguments.TryGetProperty("depth", out JsonElement depthElem) ||
             arguments.TryGetProperty("max_depth", out depthElem) ||
             arguments.TryGetProperty("maxDepth", out depthElem)) &&
            depthElem.TryGetInt32(out int parsedDepth) && parsedDepth >= 0)
        {
            depth = Math.Max(1, parsedDepth);
            displayDepth = Math.Clamp(parsedDepth, 0, 6);
        }

        string mode = "auto";
        if (arguments.TryGetProperty("mode", out JsonElement modeElem) && !string.IsNullOrWhiteSpace(modeElem.GetString()))
        {
            mode = modeElem.GetString()!.Trim().ToLowerInvariant();
        }

        double viewportWidth = 1920.0;
        if (arguments.TryGetProperty("viewportWidth", out JsonElement vwElem) && vwElem.TryGetDouble(out double parsedVw) && parsedVw > 0)
        {
            viewportWidth = parsedVw;
        }

        double viewportHeight = 1080.0;
        if (arguments.TryGetProperty("viewportHeight", out JsonElement vhElem) && vhElem.TryGetDouble(out double parsedVh) && parsedVh > 0)
        {
            viewportHeight = parsedVh;
        }

        string? contactSheetOutputPath = null;
        if ((arguments.TryGetProperty("contactSheetPath", out JsonElement csElem) || arguments.TryGetProperty("contact_sheet_path", out csElem)) &&
            csElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(csElem.GetString()))
        {
            contactSheetOutputPath = csElem.GetString()!.Trim();
        }

        // Direct layout_boxes evaluation mode (#87 Option IIII)
        List<UiLayoutBox> explicitBoxes = ParseExplicitLayoutBoxes(arguments);
        if (explicitBoxes.Count > 0 || string.Equals(mode, "layout", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<UiLayoutDefect> layoutDefects = DetectLayoutDefects(explicitBoxes, viewportWidth, viewportHeight, foldSiblings: true);
            string? savedContactSheet = null;
            if (!string.IsNullOrWhiteSpace(contactSheetOutputPath))
            {
                RenderContactSheetPng(explicitBoxes, layoutDefects, contactSheetOutputPath, (int)viewportWidth, (int)viewportHeight);
                savedContactSheet = contactSheetOutputPath;
            }

            _logger($"[LAYOUT-INSPECT] boxes={explicitBoxes.Count} defects={layoutDefects.Count} contact_sheet='{savedContactSheet ?? "none"}'");
            return ToolCallResult.Json(new
            {
                status = "success",
                mode = "layout",
                totalWidgets = explicitBoxes.Count,
                layoutDefectsCount = layoutDefects.Count,
                layoutDefects,
                contactSheetPath = savedContactSheet,
                timestamp = _timeProvider.GetUtcNow().ToString("O")
            });
        }

        _logger($"Received K2 UI inspect request: Mode='{mode}', TargetPath='{targetPath ?? "default"}', Widget='{widgetName ?? "all"}', Depth={depth}");

        try
        {
            K2UiInspectResult result = await _inspectService.InspectUiAsync(targetPath, widgetName, depth, mode, cancellationToken).ConfigureAwait(false);

            int maxListItems = isFullDetail ? 50 : 12;
            int maxChildren = isFullDetail ? 25 : 8;
            List<K2UiWarning> sampleWarnings = result.Warnings.Take(maxListItems).ToList();
            object? displayRoot = PruneNodeForDisplay(result.Root, 0, displayDepth, maxChildren);

            List<UiLayoutBox> extractedBoxes = ExtractLayoutBoxesFromWidgets(result.Widgets, viewportWidth, viewportHeight);
            IReadOnlyList<UiLayoutDefect> layoutDefects = DetectLayoutDefects(extractedBoxes, viewportWidth, viewportHeight, foldSiblings: true);
            string? savedContactSheet = null;
            if (!string.IsNullOrWhiteSpace(contactSheetOutputPath))
            {
                RenderContactSheetPng(extractedBoxes, layoutDefects, contactSheetOutputPath, (int)viewportWidth, (int)viewportHeight);
                savedContactSheet = contactSheetOutputPath;
            }

            object sampleWidgets;
            if (result.Widgets.Count > maxListItems)
            {
                sampleWidgets = result.Widgets.Take(maxListItems).Select(w => new
                {
                    name = w.Name,
                    type = w.Type,
                    visible = w.Visible,
                    texture = w.Texture,
                    width = w.Width,
                    height = w.Height,
                    isMissingTexture = w.IsMissingTexture,
                    isWhiteFallback = w.IsWhiteFallback
                }).ToList();
            }
            else
            {
                sampleWidgets = result.Widgets;
            }

            object response = new
            {
                status = result.Success ? "success" : "error",
                mode = result.Mode,
                sourcePath = result.SourcePath,
                depth = displayDepth,
                totalWidgets = result.TotalWidgets,
                visibleWidgets = result.VisibleWidgets,
                missingTexturesCount = result.MissingTexturesCount,
                whiteFallbacksCount = result.WhiteFallbacksCount,
                warningsCount = result.Warnings.Count,
                warnings = sampleWarnings,
                layoutDefectsCount = layoutDefects.Count,
                layoutDefects = layoutDefects.Take(maxListItems).ToList(),
                contactSheetPath = savedContactSheet,
                root = displayRoot,
                widgetsCount = result.Widgets.Count,
                sampleWidgets,
                truncated = result.Widgets.Count > maxListItems || result.Warnings.Count > maxListItems,
                errorMessage = result.ErrorMessage,
                timestamp = result.Timestamp
            };

            return ToolCallResult.Json(response);
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception handling K2 UI inspect request: {ex}");
            return ToolCallResult.Error($"Failed executing K2 UI inspection: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses explicit layout_boxes / widgets array from JSON arguments for #87 5-kind layout defect inspection.
    /// </summary>
    public static List<UiLayoutBox> ParseExplicitLayoutBoxes(JsonElement arguments)
    {
        List<UiLayoutBox> boxes = new List<UiLayoutBox>();
        JsonElement arrElem = default;
        if (!arguments.TryGetProperty("layout_boxes", out arrElem) &&
            !arguments.TryGetProperty("layoutBoxes", out arrElem) &&
            !arguments.TryGetProperty("boxes", out arrElem))
        {
            return boxes;
        }

        if (arrElem.ValueKind != JsonValueKind.Array)
        {
            return boxes;
        }

        int index = 0;
        foreach (JsonElement item in arrElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string name = (item.TryGetProperty("name", out JsonElement nEl) || item.TryGetProperty("Name", out nEl)) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? $"Widget_{index}"
                : $"Widget_{index}";
            string type = (item.TryGetProperty("type", out JsonElement tEl) || item.TryGetProperty("Type", out tEl)) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? "label"
                : "label";
            double x = (item.TryGetProperty("x", out JsonElement xEl) || item.TryGetProperty("X", out xEl)) && xEl.TryGetDouble(out double xv) ? xv : 0.0;
            double y = (item.TryGetProperty("y", out JsonElement yEl) || item.TryGetProperty("Y", out yEl)) && yEl.TryGetDouble(out double yv) ? yv : 0.0;
            double w = (item.TryGetProperty("width", out JsonElement wEl) || item.TryGetProperty("Width", out wEl)) && wEl.TryGetDouble(out double wv) ? wv : 0.0;
            double h = (item.TryGetProperty("height", out JsonElement hEl) || item.TryGetProperty("Height", out hEl)) && hEl.TryGetDouble(out double hv) ? hv : 0.0;
            string ink = (item.TryGetProperty("ink", out JsonElement inkEl) || item.TryGetProperty("Ink", out inkEl)) && inkEl.ValueKind == JsonValueKind.String
                ? inkEl.GetString() ?? "#ffffff"
                : ((item.TryGetProperty("color", out JsonElement colEl) || item.TryGetProperty("Color", out colEl)) && colEl.ValueKind == JsonValueKind.String ? colEl.GetString() ?? "#ffffff" : "#ffffff");
            int zOrder = (item.TryGetProperty("zOrder", out JsonElement zEl) || item.TryGetProperty("ZOrder", out zEl) || item.TryGetProperty("z_order", out zEl)) && zEl.TryGetInt32(out int zv)
                ? zv
                : index;
            bool isOpaque = (item.TryGetProperty("isOpaqueFill", out JsonElement opEl) || item.TryGetProperty("IsOpaqueFill", out opEl) || item.TryGetProperty("opaque", out opEl)) &&
                            opEl.ValueKind == JsonValueKind.True;
            double? lineEndX = (item.TryGetProperty("lineEndX", out JsonElement lexEl) || item.TryGetProperty("LineEndX", out lexEl)) &&
                               lexEl.ValueKind == JsonValueKind.Number && lexEl.TryGetDouble(out double lex) ? lex : null;
            double? lineEndY = (item.TryGetProperty("lineEndY", out JsonElement leyEl) || item.TryGetProperty("LineEndY", out leyEl)) &&
                               leyEl.ValueKind == JsonValueKind.Number && leyEl.TryGetDouble(out double ley) ? ley : null;

            boxes.Add(new UiLayoutBox(name, type, x, y, w, h, ink, zOrder, isOpaque, lineEndX, lineEndY));
            index++;
        }

        return boxes;
    }

    private static List<UiLayoutBox> ExtractLayoutBoxesFromWidgets(IReadOnlyList<K2WidgetNode> widgets, double viewportW, double viewportH)
    {
        List<UiLayoutBox> boxes = new List<UiLayoutBox>();
        for (int i = 0; i < widgets.Count; i++)
        {
            K2WidgetNode w = widgets[i];
            if (!w.Visible || string.IsNullOrWhiteSpace(w.Name))
            {
                continue;
            }

            if (TryParseK2Coord(w.X, viewportW, out double x) &&
                TryParseK2Coord(w.Y, viewportH, out double y) &&
                TryParseK2Coord(w.Width, viewportW, out double width) &&
                TryParseK2Coord(w.Height, viewportH, out double height) &&
                width > 0 && height > 0)
            {
                string ink = string.IsNullOrWhiteSpace(w.Color) ? "#ffffff" : w.Color!;
                bool isOpaque = string.Equals(w.Type, "panel", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(w.Texture);
                boxes.Add(new UiLayoutBox(w.Name, w.Type, x, y, width, height, ink, i, isOpaque));
            }
        }
        return boxes;
    }

    private static bool TryParseK2Coord(string? raw, double axisMax, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string s = raw.Trim();
        if (s.EndsWith('%'))
        {
            if (double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            {
                value = (pct / 100.0) * axisMax;
                return true;
            }
            return false;
        }

        if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase) || s.EndsWith("w", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 1);
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// ManimGX 5-Kind 2D UI Layout Defect Detector (#87 Option IIII):
    /// 1. 'cut': Clipped by viewport edge (OVER_EDGE = 2px), skipping full-bleed background panels.
    /// 2. 'overlap': Overlapping text/label/button boxes (PAD = 4px, >= 25% minor-axis overlap) with identical-ink exemption (a.Ink == b.Ink) for drop shadows.
    /// 3. 'small': Text/label/icon height &lt; 20px at 1080p.
    /// 4. 'crosses': Liang-Barsky segment-vs-AABB clipping for lines/connectors slicing through labels, exempting framing containers.
    /// 5. 'covers': 7x7 interior grid sampling inside labels covered >= 25% by later-drawn opaque foreground fills (zOrder > label.zOrder).
    /// Plus sibling folding (_fold_problems) for repetitive list/grid items.
    /// </summary>
    public static IReadOnlyList<UiLayoutDefect> DetectLayoutDefects(
        IReadOnlyList<UiLayoutBox> boxes,
        double viewportWidth = 1920.0,
        double viewportHeight = 1080.0,
        bool foldSiblings = true)
    {
        const double OverEdge = 2.0;
        const double SmallTextHeight = 20.0;
        const double OverlapMinorRatio = 0.25;
        const double CoverShareThreshold = 0.25;

        List<UiLayoutDefect> rawDefects = new List<UiLayoutDefect>();
        int nextNumber = 1;

        // 1. 'cut' & 3. 'small'
        for (int i = 0; i < boxes.Count; i++)
        {
            UiLayoutBox b = boxes[i];
            if (IsLineOrConnector(b))
            {
                continue;
            }

            bool spansHorizontal = b.X <= 0.0 && (b.X + b.Width) >= viewportWidth;
            bool spansVertical = b.Y <= 0.0 && (b.Y + b.Height) >= viewportHeight;
            bool isFullBleedPanel = spansHorizontal || spansVertical;

            if (!isFullBleedPanel && b.Width > 0.0 && b.Height > 0.0)
            {
                if (b.X < -OverEdge || b.Y < -OverEdge ||
                    (b.X + b.Width) > (viewportWidth + OverEdge) ||
                    (b.Y + b.Height) > (viewportHeight + OverEdge))
                {
                    rawDefects.Add(new UiLayoutDefect(
                        "cut",
                        b.Name,
                        null,
                        $"Widget '{b.Name}' is clipped by viewport edge ({b.X:F1},{b.Y:F1},{b.Width:F1}x{b.Height:F1} outside {viewportWidth:F0}x{viewportHeight:F0}).",
                        b.X, b.Y, b.Width, b.Height,
                        nextNumber++));
                }
            }

            if (IsTextOrLabel(b.Type) && b.Height > 0.0 && b.Height < SmallTextHeight)
            {
                rawDefects.Add(new UiLayoutDefect(
                    "small",
                    b.Name,
                    null,
                    $"Widget '{b.Name}' rendered height ({b.Height:F1}px) is below legible threshold ({SmallTextHeight:F0}px).",
                    b.X, b.Y, b.Width, b.Height,
                    nextNumber++));
            }
        }

        // 2. 'overlap' (with identical-ink drop-shadow exemption: a.Ink == b.Ink)
        for (int i = 0; i < boxes.Count; i++)
        {
            UiLayoutBox a = boxes[i];
            if (!IsTextOrInteractive(a.Type) || a.Width <= 0.0 || a.Height <= 0.0)
            {
                continue;
            }

            for (int j = i + 1; j < boxes.Count; j++)
            {
                UiLayoutBox b = boxes[j];
                if (!IsTextOrInteractive(b.Type) || b.Width <= 0.0 || b.Height <= 0.0)
                {
                    continue;
                }

                // Identical-ink exemption (a.ink == b.ink) so drop-shadows / layered glow labels never trigger false positives
                if (string.Equals(a.Ink.Trim(), b.Ink.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double ix0 = Math.Max(a.X, b.X);
                double iy0 = Math.Max(a.Y, b.Y);
                double ix1 = Math.Min(a.X + a.Width, b.X + b.Width);
                double iy1 = Math.Min(a.Y + a.Height, b.Y + b.Height);

                if (ix1 > ix0 && iy1 > iy0)
                {
                    double overlapW = ix1 - ix0;
                    double overlapH = iy1 - iy0;
                    double minorDim = Math.Max(1.0, Math.Min(Math.Min(a.Width, a.Height), Math.Min(b.Width, b.Height)));
                    if (Math.Min(overlapW, overlapH) / minorDim >= OverlapMinorRatio)
                    {
                        rawDefects.Add(new UiLayoutDefect(
                            "overlap",
                            a.Name,
                            b.Name,
                            $"Widget '{a.Name}' (ink={a.Ink}) overlaps '{b.Name}' (ink={b.Ink}) by {overlapW:F1}x{overlapH:F1}px.",
                            ix0, iy0, overlapW, overlapH,
                            nextNumber++));
                    }
                }
            }
        }

        // 4. 'crosses' (Liang-Barsky segment-vs-AABB clipping)
        for (int i = 0; i < boxes.Count; i++)
        {
            UiLayoutBox lineBox = boxes[i];
            if (!IsLineOrConnector(lineBox))
            {
                continue;
            }

            double x0 = lineBox.X;
            double y0 = lineBox.Y;
            double x1 = lineBox.LineEndX ?? (lineBox.X + lineBox.Width);
            double y1 = lineBox.LineEndY ?? (lineBox.Y + lineBox.Height);

            for (int j = 0; j < boxes.Count; j++)
            {
                if (i == j) continue;
                UiLayoutBox label = boxes[j];
                if (!IsTextOrLabel(label.Type) || label.Width <= 0.0 || label.Height <= 0.0)
                {
                    continue;
                }

                if (FramesLabel(lineBox, label))
                {
                    continue;
                }

                if (LiangBarskyIntersectsBox(x0, y0, x1, y1, label.X, label.Y, label.X + label.Width, label.Y + label.Height))
                {
                    rawDefects.Add(new UiLayoutDefect(
                        "crosses",
                        lineBox.Name,
                        label.Name,
                        $"Segment '{lineBox.Name}' crosses through label '{label.Name}'.",
                        label.X, label.Y, label.Width, label.Height,
                        nextNumber++));
                }
            }
        }

        // 5. 'covers' (7x7 interior sample grid covered >= 25% by later-drawn opaque foreground fill)
        for (int i = 0; i < boxes.Count; i++)
        {
            UiLayoutBox label = boxes[i];
            if (!IsTextOrLabel(label.Type) || label.Width <= 0.0 || label.Height <= 0.0)
            {
                continue;
            }

            for (int j = 0; j < boxes.Count; j++)
            {
                if (i == j) continue;
                UiLayoutBox panel = boxes[j];
                if (!panel.IsOpaqueFill || panel.ZOrder <= label.ZOrder || panel.Width <= 0.0 || panel.Height <= 0.0)
                {
                    continue;
                }

                int coveredSamples = 0;
                for (int r = 0; r < 7; r++)
                {
                    double sy = label.Y + (r + 0.5) * (label.Height / 7.0);
                    for (int c = 0; c < 7; c++)
                    {
                        double sx = label.X + (c + 0.5) * (label.Width / 7.0);
                        if (sx >= panel.X && sx <= (panel.X + panel.Width) &&
                            sy >= panel.Y && sy <= (panel.Y + panel.Height))
                        {
                            coveredSamples++;
                        }
                    }
                }

                double coverShare = coveredSamples / 49.0;
                if (coverShare >= CoverShareThreshold)
                {
                    rawDefects.Add(new UiLayoutDefect(
                        "covers",
                        panel.Name,
                        label.Name,
                        $"Foreground panel '{panel.Name}' (z={panel.ZOrder}) covers {coverShare * 100.0:F0}% ({coveredSamples}/49 samples) of label '{label.Name}' (z={label.ZOrder}).",
                        label.X, label.Y, label.Width, label.Height,
                        nextNumber++));
                }
            }
        }

        if (!foldSiblings || rawDefects.Count < 3)
        {
            return rawDefects;
        }

        return FoldSiblingDefects(rawDefects);
    }

    /// <summary>
    /// Collapses repetitive sibling defects (e.g. Item[0]..Item[11]) into a single summary defect (#87 _fold_problems).
    /// </summary>
    public static IReadOnlyList<UiLayoutDefect> FoldSiblingDefects(IReadOnlyList<UiLayoutDefect> defects)
    {
        List<UiLayoutDefect> result = new List<UiLayoutDefect>();
        IEnumerable<IGrouping<(string Kind, string Stem), UiLayoutDefect>> grouped =
            defects.GroupBy(d => (d.Kind, Stem: SiblingIndexRegex.Replace(d.WidgetName, "[*]")));
        int num = 1;
        foreach (IGrouping<(string Kind, string Stem), UiLayoutDefect> grp in grouped)
        {
            List<UiLayoutDefect> items = grp.ToList();
            if (items.Count >= 3 && grp.Key.Stem.Contains("[*]", StringComparison.Ordinal))
            {
                UiLayoutDefect first = items[0];
                result.Add(new UiLayoutDefect(
                    grp.Key.Kind,
                    grp.Key.Stem,
                    first.OtherWidgetName,
                    $"{items.Count} of {items.Count} {grp.Key.Stem} widgets have layout defect '{grp.Key.Kind}' (first: {first.Message})",
                    first.X, first.Y, first.Width, first.Height,
                    num++));
            }
            else
            {
                foreach (UiLayoutDefect item in items)
                {
                    result.Add(item with { DefectNumber = num++ });
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Liang-Barsky parametric line-clipping algorithm testing whether segment (x0,y0)-(x1,y1) intersects AABB [rx0,ry0,rx1,ry1].
    /// </summary>
    public static bool LiangBarskyIntersectsBox(
        double x0, double y0, double x1, double y1,
        double rx0, double ry0, double rx1, double ry1)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double t0 = 0.0;
        double t1 = 1.0;

        double[] p = [-dx, dx, -dy, dy];
        double[] q = [x0 - rx0, rx1 - x0, y0 - ry0, ry1 - y0];

        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-9)
            {
                if (q[i] < 0.0)
                {
                    return false;
                }
            }
            else
            {
                double t = q[i] / p[i];
                if (p[i] < 0.0)
                {
                    if (t > t1) return false;
                    if (t > t0) t0 = t;
                }
                else
                {
                    if (t < t0) return false;
                    if (t < t1) t1 = t;
                }
            }
        }

        return t0 <= t1;
    }

    /// <summary>
    /// Renders a 1920x1080 (2x3 storyboard contact sheet of 640x540 tiles) PNG with red (#e05555) defect bounding boxes (#87).
    /// </summary>
    public static void RenderContactSheetPng(
        IReadOnlyList<UiLayoutBox> boxes,
        IReadOnlyList<UiLayoutDefect> defects,
        string outputPath,
        int viewportWidth = 1920,
        int viewportHeight = 1080)
    {
        const int sheetWidth = 1920;
        const int sheetHeight = 1080;
        const int cols = 3;
        const int rows = 2;
        int tileW = sheetWidth / cols;
        int tileH = sheetHeight / rows;

        using Image<Rgba32> img = new Image<Rgba32>(sheetWidth, sheetHeight, new Rgba32(18, 20, 26, 255));
        Rgba32 tileBorderColor = new Rgba32(48, 54, 68, 255);
        Rgba32 boxColor = new Rgba32(90, 110, 140, 255);
        Rgba32 defectRed = new Rgba32(224, 85, 85, 255); // #e05555

        for (int tileIdx = 0; tileIdx < cols * rows; tileIdx++)
        {
            int col = tileIdx % cols;
            int row = tileIdx / cols;
            int originX = col * tileW;
            int originY = row * tileH;

            DrawRectOutline(img, originX, originY, tileW - 1, tileH - 1, tileBorderColor, 1);

            double scaleX = (double)tileW / Math.Max(1, viewportWidth);
            double scaleY = (double)tileH / Math.Max(1, viewportHeight);

            foreach (UiLayoutBox b in boxes)
            {
                int bx = originX + (int)Math.Round(b.X * scaleX);
                int by = originY + (int)Math.Round(b.Y * scaleY);
                int bw = Math.Max(2, (int)Math.Round(b.Width * scaleX));
                int bh = Math.Max(2, (int)Math.Round(b.Height * scaleY));
                DrawRectOutline(img, bx, by, bw, bh, boxColor, 1);
            }

            foreach (UiLayoutDefect d in defects)
            {
                int dx = originX + (int)Math.Round(d.X * scaleX);
                int dy = originY + (int)Math.Round(d.Y * scaleY);
                int dw = Math.Max(4, (int)Math.Round(d.Width * scaleX));
                int dh = Math.Max(4, (int)Math.Round(d.Height * scaleY));
                DrawRectOutline(img, dx, dy, dw, dh, defectRed, 2);
            }
        }

        string? parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        img.SaveAsPng(outputPath);
    }

    private static void DrawRectOutline(Image<Rgba32> img, int x, int y, int w, int h, Rgba32 color, int thickness)
    {
        int x0 = Math.Clamp(x, 0, img.Width - 1);
        int y0 = Math.Clamp(y, 0, img.Height - 1);
        int x1 = Math.Clamp(x + w, 0, img.Width - 1);
        int y1 = Math.Clamp(y + h, 0, img.Height - 1);

        for (int t = 0; t < thickness; t++)
        {
            int top = Math.Clamp(y0 + t, 0, img.Height - 1);
            int bot = Math.Clamp(y1 - t, 0, img.Height - 1);
            for (int px = x0; px <= x1; px++)
            {
                img[px, top] = color;
                img[px, bot] = color;
            }

            int left = Math.Clamp(x0 + t, 0, img.Width - 1);
            int right = Math.Clamp(x1 - t, 0, img.Width - 1);
            for (int py = y0; py <= y1; py++)
            {
                img[left, py] = color;
                img[right, py] = color;
            }
        }
    }

    private static bool IsTextOrLabel(string type) =>
        string.Equals(type, "label", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "icon", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextOrInteractive(string type) =>
        IsTextOrLabel(type) ||
        string.Equals(type, "button", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineOrConnector(UiLayoutBox b) =>
        b.LineEndX.HasValue || b.LineEndY.HasValue ||
        string.Equals(b.Type, "line", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(b.Type, "connector", StringComparison.OrdinalIgnoreCase);

    private static bool FramesLabel(UiLayoutBox container, UiLayoutBox label) =>
        !IsLineOrConnector(container) &&
        container.X <= label.X &&
        container.Y <= label.Y &&
        (container.X + container.Width) >= (label.X + label.Width) &&
        (container.Y + container.Height) >= (label.Y + label.Height);

    private static object? PruneNodeForDisplay(K2WidgetNode? node, int currentDepth, int maxDepth, int maxChildren = 8)
    {
        if (node == null) return null;

        if (currentDepth >= maxDepth)
        {
            return new
            {
                name = node.Name,
                type = node.Type,
                visible = node.Visible,
                childrenCount = node.Children.Count,
                hasMore = node.Children.Count > 0
            };
        }

        return new
        {
            name = node.Name,
            type = node.Type,
            visible = node.Visible,
            texture = node.Texture,
            model = node.Model,
            x = node.X,
            y = node.Y,
            width = node.Width,
            height = node.Height,
            align = node.Align,
            valign = node.Valign,
            color = node.Color,
            isWhiteFallback = node.IsWhiteFallback,
            isMissingTexture = node.IsMissingTexture,
            childrenCount = node.Children.Count,
            children = node.Children.Take(maxChildren).Select(c => PruneNodeForDisplay(c, currentDepth + 1, maxDepth, maxChildren)).ToList()
        };
    }
}

