// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kongor.Modding.Repair;

/// <summary>
///     Rescales legacy fixed 4:3 pixel coordinates into modern responsive height percentage ('h') units
///     or responsive percentage units, preventing UI distortion on modern 16:9, 21:9, and 4K displays.
/// </summary>
public static class UiAnchorRescaler
{
    public const double BaselineHeightPx = 768.0;
    public const double BaselineWidthPx = 1024.0;

    private static readonly string[] TargetAttributes =
    [
        "x", "y", "width", "height", "xpos", "ypos",
        "borderthickness", "growsize", "padding", "margin",
        "indent", "offset", "font", "size", "gap",
        "headspace", "tailspace", "itemwidth", "itemheight",
        "minwidth", "minheight", "maxwidth", "maxheight"
    ];

    /// <summary>
    ///     Scans XML content for fixed pixel dimensions and converts them into responsive 'h' percentage units.
    /// </summary>
    public static string RescaleUiCoordinates(string xmlContent, out int rescaledCount)
    {
        int localRescaledCount = 0;
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            rescaledCount = 0;
            return xmlContent;
        }

        string content = xmlContent;

        foreach (string attr in TargetAttributes)
        {
            // Matches attr="123" or attr="123px" or attr='123' or attr="-120px" or attr="76.8px"
            string pattern = $@"\b({attr})\s*=\s*([""'])([-+]?\d+(?:\.\d+)?)(px)?([""'])";
            MatchCollection matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);

            if (matches.Count > 0)
                content = Regex.Replace(content, pattern, m =>
                {
                    string attrName = m.Groups[1].Value;
                    string quote = m.Groups[2].Value;
                    string valStr = m.Groups[3].Value;

                    if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double pxVal))
                        // Rescale only if absolute value is significant (>= 10px) to preserve 1px/2px borders
                        if (Math.Abs(pxVal) >= 10.0)
                        {
                            localRescaledCount++;
                            double hVal = Math.Round(pxVal / BaselineHeightPx * 100.0, 2,
                                MidpointRounding.AwayFromZero);
                            return $"{attrName}={quote}{hVal.ToString("0.##", CultureInfo.InvariantCulture)}h{quote}";
                        }

                    return m.Value;
                }, RegexOptions.IgnoreCase);
        }

        rescaledCount = localRescaledCount;
        return content;
    }
}
