// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AssertUiHandler.cs
// System: Kongor MCP Server - Programmatic UI DOM Assertion Tool
// Purpose: Implements 'kongor_assert_ui' (alias 'assert_ui') to perform
//          declarative, high-fidelity UI widget state assertions.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_assert_ui' (aliases: 'assert_ui', 'kongor_ui_assert').
/// Performs one-shot, zero-flake declarative assertions against in-engine UI widget state
/// (visibility, text, textures, dimensions, layout bounds) over Mailbox IPC.
/// </summary>
public sealed class AssertUiHandler
{
    private readonly ITaoClient? _taoClient;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public AssertUiHandler(
        ITaoClient? taoClient = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _taoClient = taoClient;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch sw = Stopwatch.StartNew();

        string widget = "";
        if (arguments.TryGetProperty("widget", out JsonElement wEl) && !string.IsNullOrWhiteSpace(wEl.GetString()))
        {
            widget = wEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            widget = tEl.GetString()!.Trim();
        }

        string ifaceName = "main";
        if (arguments.TryGetProperty("interface", out JsonElement iEl) && !string.IsNullOrWhiteSpace(iEl.GetString()))
        {
            ifaceName = iEl.GetString()!.Trim();
        }

        bool? expectedVisible = null;
        if (arguments.TryGetProperty("visible", out JsonElement vEl))
        {
            expectedVisible = vEl.GetBoolean();
        }

        bool? expectedEnabled = null;
        if (arguments.TryGetProperty("enabled", out JsonElement enEl))
        {
            expectedEnabled = enEl.GetBoolean();
        }

        string? expectedText = null;
        if (arguments.TryGetProperty("text", out JsonElement txtEl) && !string.IsNullOrWhiteSpace(txtEl.GetString()))
        {
            expectedText = txtEl.GetString()!.Trim();
        }

        string? expectedTextContains = null;
        if (arguments.TryGetProperty("textContains", out JsonElement tcEl) && !string.IsNullOrWhiteSpace(tcEl.GetString()))
        {
            expectedTextContains = tcEl.GetString()!.Trim();
        }

        string? expectedTexture = null;
        if (arguments.TryGetProperty("texture", out JsonElement texEl) && !string.IsNullOrWhiteSpace(texEl.GetString()))
        {
            expectedTexture = texEl.GetString()!.Trim();
        }

        string? expectedValue = null;
        if (arguments.TryGetProperty("value", out JsonElement valEl))
        {
            expectedValue = valEl.ToString()?.Trim();
        }

        double? minWidth = null;
        if (arguments.TryGetProperty("minWidth", out JsonElement mwEl) && mwEl.TryGetDouble(out double mwVal))
        {
            minWidth = mwVal;
        }

        double? minHeight = null;
        if (arguments.TryGetProperty("minHeight", out JsonElement mhEl) && mhEl.TryGetDouble(out double mhVal))
        {
            minHeight = mhVal;
        }

        List<UiLayoutBox> explicitBoxes = K2UiInspectHandler.ParseExplicitLayoutBoxes(arguments);
        if (explicitBoxes.Count > 0)
        {
            double viewportW = arguments.TryGetProperty("viewportWidth", out JsonElement vwEl) && vwEl.TryGetDouble(out double vw) && vw > 0 ? vw : 1920.0;
            double viewportH = arguments.TryGetProperty("viewportHeight", out JsonElement vhEl) && vhEl.TryGetDouble(out double vh) && vh > 0 ? vh : 1080.0;
            IReadOnlyList<UiLayoutDefect> layoutDefects = K2UiInspectHandler.DetectLayoutDefects(explicitBoxes, viewportW, viewportH, foldSiblings: true);

            string? contactSheetPath = null;
            if ((arguments.TryGetProperty("contactSheetPath", out JsonElement csEl) || arguments.TryGetProperty("contact_sheet_path", out csEl)) &&
                csEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(csEl.GetString()))
            {
                contactSheetPath = csEl.GetString()!.Trim();
                K2UiInspectHandler.RenderContactSheetPng(explicitBoxes, layoutDefects, contactSheetPath, (int)viewportW, (int)viewportH);
            }

            sw.Stop();
            double layoutElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            bool layoutPassed = layoutDefects.Count == 0;
            List<string> layoutDiscrepancies = layoutDefects.Select(d => $"[{d.Kind}] {d.Message}").ToList();

            return ToolCallResult.Json(new
            {
                status = layoutPassed ? "success" : "failed",
                passed = layoutPassed,
                widget = string.IsNullOrEmpty(widget) ? "layout_boxes" : widget,
                interfaceName = ifaceName,
                layoutDefectsCount = layoutDefects.Count,
                layoutDefects,
                discrepancies = layoutDiscrepancies,
                contactSheetPath,
                elapsedMs = layoutElapsedMs,
                actionableRemediation = layoutPassed ? null : "Fix overlapping/clipped/small/covered widgets reported in layoutDefects."
            });
        }

        if (_taoClient == null)
        {
            return ToolCallResult.Error("TaoClient is not configured for UI assertion.");
        }

        object assertPayload = new
        {
            widget,
            target = widget,
            @interface = ifaceName,
            visible = expectedVisible,
            enabled = expectedEnabled,
            text = expectedText,
            textContains = expectedTextContains,
            texture = expectedTexture,
            minWidth,
            minHeight
        };

        TaoResponse<JsonElement>? resp = await _taoClient.DispatchAsync("assert_ui", assertPayload, 2000, cancellationToken).ConfigureAwait(false);
        if (resp == null || !resp.Success || string.Equals(resp.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            object inspectPayload = new
            {
                widget,
                @interface = ifaceName,
                maxDepth = 3
            };
            resp = await _taoClient.DispatchAsync("inspect_ui", inspectPayload, 2000, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        List<string> discrepancies = new List<string>();

        if (resp == null || !resp.Success || string.Equals(resp.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            string errMsg = resp?.Error ?? "Failed to query assert_ui/inspect_ui via TaoClient";
            discrepancies.Add($"Engine dispatch error: {errMsg}");
            return ToolCallResult.Json(new
            {
                status = "error",
                passed = false,
                error = $"UI assertion failed: {errMsg}",
                widget,
                interfaceName = ifaceName,
                discrepancies,
                elapsedMs
            }, isError: true);
        }

        if (resp.Data.TryGetProperty("discrepancies", out JsonElement discEl) && discEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in discEl.EnumerateArray())
            {
                string? s = item.GetString();
                if (!string.IsNullOrEmpty(s)) discrepancies.Add(s);
            }
        }

        if (!resp.Data.TryGetProperty("found", out JsonElement foundEl) || !foundEl.GetBoolean())
        {
            discrepancies.Add($"Widget '{widget}' was not found in interface '{ifaceName}'.");
            return ToolCallResult.Json(new
            {
                passed = false,
                widget,
                interfaceName = ifaceName,
                discrepancies,
                elapsedMs,
                actionableRemediation = $"Inspect available widgets using 'kongor_k2_ui_inspect' with interface '{ifaceName}'."
            });
        }

        // Extract actual widget node properties
        bool actualVisible = false;
        bool actualEnabled = true;
        string actualText = "";
        object? actualValue = null;
        string actualTexture = "";
        double actualWidth = 0;
        double actualHeight = 0;

        JsonElement targetNode = resp.Data.TryGetProperty("root", out JsonElement rn) ? rn : resp.Data;

        if (targetNode.TryGetProperty("visible", out JsonElement rV)) actualVisible = rV.GetBoolean();
        if (targetNode.TryGetProperty("enabled", out JsonElement rEn)) actualEnabled = rEn.GetBoolean();
        if (targetNode.TryGetProperty("text", out JsonElement rT)) actualText = rT.GetString() ?? "";
        if (targetNode.TryGetProperty("value", out JsonElement rVal))
        {
            if (rVal.ValueKind == JsonValueKind.Number && rVal.TryGetDouble(out double dVal)) actualValue = dVal;
            else if (rVal.ValueKind == JsonValueKind.String) actualValue = rVal.GetString();
        }
        if (targetNode.TryGetProperty("texture", out JsonElement rTex)) actualTexture = rTex.GetString() ?? "";

        if (targetNode.TryGetProperty("bounds", out JsonElement bNode))
        {
            if (bNode.TryGetProperty("width", out JsonElement bw)) bw.TryGetDouble(out actualWidth);
            if (bNode.TryGetProperty("height", out JsonElement bh)) bh.TryGetDouble(out actualHeight);
        }

        void AddDiscrepancy(string msg)
        {
            if (!discrepancies.Contains(msg))
            {
                discrepancies.Add(msg);
            }
        }

        // Perform evaluations
        if (expectedVisible.HasValue && actualVisible != expectedVisible.Value)
        {
            AddDiscrepancy($"Expected visible: {expectedVisible.Value}, but actual visible: {actualVisible}");
        }

        if (expectedEnabled.HasValue && actualEnabled != expectedEnabled.Value)
        {
            AddDiscrepancy($"Expected enabled: {expectedEnabled.Value}, but actual enabled: {actualEnabled}");
        }

        if (!string.IsNullOrEmpty(expectedText) && !string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase))
        {
            AddDiscrepancy($"Expected text: '{expectedText}', but actual text: '{actualText}'");
        }

        if (!string.IsNullOrEmpty(expectedValue) && (actualValue == null || !string.Equals(actualValue.ToString(), expectedValue, StringComparison.OrdinalIgnoreCase)))
        {
            AddDiscrepancy($"Expected value: '{expectedValue}', but actual value: '{actualValue}'");
        }

        if (!string.IsNullOrEmpty(expectedTextContains) && !actualText.Contains(expectedTextContains, StringComparison.OrdinalIgnoreCase))
        {
            AddDiscrepancy($"Expected text to contain: '{expectedTextContains}', but actual text: '{actualText}'");
        }

        if (!string.IsNullOrEmpty(expectedTexture) && !actualTexture.Contains(expectedTexture, StringComparison.OrdinalIgnoreCase))
        {
            AddDiscrepancy($"Expected texture: '{expectedTexture}', but actual texture: '{actualTexture}'");
        }

        if (minWidth.HasValue && actualWidth < minWidth.Value)
        {
            AddDiscrepancy($"Expected width >= {minWidth.Value}, but actual width: {actualWidth}");
        }

        if (minHeight.HasValue && actualHeight < minHeight.Value)
        {
            AddDiscrepancy($"Expected height >= {minHeight.Value}, but actual height: {actualHeight}");
        }

        bool passed = discrepancies.Count == 0;
        _logger?.Invoke($"[AssertUiHandler] Widget '{widget}' in '{ifaceName}' => Passed: {passed} ({discrepancies.Count} discrepancies)");

        return ToolCallResult.Json(new
        {
            status = passed ? "success" : "failed",
            passed,
            widget,
            interfaceName = ifaceName,
            discrepancies,
            actual = new
            {
                visible = actualVisible,
                enabled = actualEnabled,
                text = actualText,
                value = actualValue,
                texture = actualTexture,
                width = actualWidth,
                height = actualHeight
            },
            elapsedMs,
            actionableRemediation = passed ? null : "Adjust widget state or inspect UI hierarchy using kongor_k2_ui_inspect."
        });
    }
}
