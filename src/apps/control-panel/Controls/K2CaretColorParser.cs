// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace NEXUS.ControlPanel.GUI.Controls;

public readonly record struct K2ColorSegment(string Text, string HexColor);

/// <summary>
/// Parses K2 engine caret color tokens (^p, ^o, ^y, ^c, ^g, ^r, ^m, ^w, etc.) into clean text and colored segments.
/// </summary>
public static class K2CaretColorParser
{
    public const string DefaultColor = "#E2E8F0"; // Slate-200
    public const string GoldColor = "#FFD700";    // Gold for ALL / ^y

    private static readonly Dictionary<char, string> ColorMap = new()
    {
        ['y'] = GoldColor,      // Gold / Yellow
        ['Y'] = GoldColor,
        ['c'] = "#00F0FF",      // Cyan / USE
        ['C'] = "#00F0FF",
        ['g'] = "#10B981",      // Green / USW
        ['G'] = "#10B981",
        ['p'] = "#A855F7",      // Purple / EU
        ['P'] = "#A855F7",
        ['r'] = "#EF4444",      // Red / AU
        ['R'] = "#EF4444",
        ['o'] = "#F59E0B",      // Orange / SEA
        ['O'] = "#F59E0B",
        ['m'] = "#EC4899",      // Magenta / Pink / BR
        ['M'] = "#EC4899",
        ['w'] = "#FFFFFF",      // White / RU
        ['W'] = "#FFFFFF",
        ['b'] = "#3B82F6",      // Blue
        ['B'] = "#3B82F6",
        ['k'] = "#1E293B",      // Black / Dark Slate
        ['K'] = "#1E293B",
        ['t'] = "#14B8A6",      // Teal
        ['T'] = "#14B8A6",
        ['v'] = "#8B5CF6",      // Violet
        ['V'] = "#8B5CF6",
        ['d'] = "#64748B",      // Dark Grey
        ['D'] = "#64748B",
        ['0'] = "#FFFFFF",
        ['1'] = "#EF4444",
        ['2'] = "#10B981",
        ['3'] = "#3B82F6",
        ['4'] = GoldColor,
        ['5'] = "#00F0FF",
        ['6'] = "#EC4899",
        ['7'] = "#F59E0B",
        ['8'] = "#A855F7",
        ['9'] = "#64748B"
    };

    /// <summary>
    /// Strips all ^&lt;char&gt; color codes from text, returning pure plain text.
    /// </summary>
    public static string StripCarets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length)
            {
                if (text[i + 1] == '^')
                {
                    sb.Append('^');
                    i++;
                }
                else
                {
                    // Skip caret code char
                    i++;
                }
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a string with caret color codes into individual colored text segments.
    /// </summary>
    public static List<K2ColorSegment> Parse(string? text, string fallbackColor = DefaultColor)
    {
        List<K2ColorSegment> segments = [];
        if (string.IsNullOrEmpty(text)) return segments;

        string currentColor = fallbackColor;
        StringBuilder currentText = new();

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next == '^')
                {
                    currentText.Append('^');
                    i++;
                }
                else if (ColorMap.TryGetValue(next, out string? mappedColor))
                {
                    if (currentText.Length > 0)
                    {
                        segments.Add(new K2ColorSegment(currentText.ToString(), currentColor));
                        currentText.Clear();
                    }
                    currentColor = mappedColor;
                    i++;
                }
                else
                {
                    // Unknown caret code, skip code
                    i++;
                }
            }
            else
            {
                currentText.Append(text[i]);
            }
        }

        if (currentText.Length > 0)
        {
            segments.Add(new K2ColorSegment(currentText.ToString(), currentColor));
        }

        return segments;
    }
}

/// <summary>
/// Attached property for standard Avalonia TextBlock controls to dynamically render K2 caret colors.
/// Usage: &lt;TextBlock controls:K2CaretText.Text="{Binding ServerName}" /&gt;
/// </summary>
public static class K2CaretText
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>(
            "Text",
            typeof(K2CaretText));

    static K2CaretText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
        {
            string? text = e.NewValue as string;
            ApplyCaretText(tb, text);
        });
    }

    public static string? GetText(TextBlock textBlock) => textBlock.GetValue(TextProperty);
    public static void SetText(TextBlock textBlock, string? value) => textBlock.SetValue(TextProperty, value);

    public static void ApplyCaretText(TextBlock tb, string? text)
    {
        tb.Inlines ??= new InlineCollection();
        tb.Inlines.Clear();

        if (string.IsNullOrEmpty(text))
        {
            tb.Text = string.Empty;
            return;
        }

        // Fast path: no carets
        if (!text.Contains('^'))
        {
            tb.Text = text;
            return;
        }

        tb.Text = null; // Clear raw text so Inlines render
        var segments = K2CaretColorParser.Parse(text);
        foreach (var seg in segments)
        {
            tb.Inlines.Add(new Run
            {
                Text = seg.Text,
                Foreground = new SolidColorBrush(Color.Parse(seg.HexColor))
            });
        }
    }
}
