// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;

namespace Kongor.Modding.Repair;

/// <summary>
///     Fixes malformed legacy XML, normalizes quotes/entities, converts obsolete tags,
///     and upgrades legacy inline event handlers to modern Lua callbacks.
/// </summary>
public static class XmlSyntaxNormalizer
{
    private static readonly (string legacy, string modern)[] EventUpgrades =
    [
        ("onload", "onloadlua"),
        ("onshow", "onshowlua"),
        ("onclick", "onclicklua"),
        ("onhide", "onhidelua"),
        ("onframe", "onframelua"),
        ("onmousewheel", "onmousewheellua"),
        ("onenter", "onenterlua"),
        ("onesc", "onesclua"),
        ("onrightclick", "onrightclicklua"),
        ("ondoubleclick", "ondoubleclicklua"),
        ("onfocus", "onfocuslua"),
        ("onlosefocus", "onlosefocuslua"),
        ("onchange", "onchangelua"),
        ("onselect", "onselectlua"),
        ("onmouseover", "onmouseoverlua"),
        ("onmouseout", "onmouseoutlua"),
        ("onmouseldown", "onmouseldownlua"),
        ("onmouselup", "onmouseluplua"),
        ("onmouserdown", "onmouserdownlua"),
        ("onmouserup", "onmouseruplua"),
        ("onwake", "onwakelua"),
        ("onsleep", "onsleeplua"),
        ("onrefresh", "onrefreshlua"),
        ("onaction", "onactionlua"),
        ("onpan", "onpanlua"),
        ("onresize", "onresizelua")
    ];

    /// <summary>
    ///     Normalizes raw XML string, fixing legacy event handlers, ampersand entities,
    ///     unquoted attributes, and legacy tags.
    /// </summary>
    public static string NormalizeXml(string rawXml, out int fixesCount)
    {
        fixesCount = 0;
        if (string.IsNullOrWhiteSpace(rawXml)) return rawXml;

        string content = rawXml;

        // 1. Upgrade legacy inline event handlers (e.g. onclick="..." -> onclicklua="...")
        // Negative lookahead (?<!lua)(?!lua) ensures we don't convert onclicklua to onclicklualua
        foreach ((string legacy, string modern) in EventUpgrades)
        {
            string pattern = $@"\b{legacy}(?!lua)\s*=\s*([""'])";
            MatchCollection matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                fixesCount += matches.Count;
                content = Regex.Replace(content, pattern, $"{modern}=$1", RegexOptions.IgnoreCase);
            }
        }

        // 2. Fix unescaped ampersands outside of entities (e.g. & not followed by amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)
        // Skip text inside CDATA blocks
        content = FixAmpersandsOutsideCData(content, ref fixesCount);

        // 3. Fix unquoted attributes (e.g. <panel width=100 height=200>)
        string unquotedAttrPattern = @"(\s+[a-zA-Z_:][a-zA-Z0-9_\.\-:]*)=([a-zA-Z0-9_\.\-%#]+)(?=[\s>/])";
        MatchCollection unquotedMatches = Regex.Matches(content, unquotedAttrPattern);
        if (unquotedMatches.Count > 0)
        {
            fixesCount += unquotedMatches.Count;
            content = Regex.Replace(content, unquotedAttrPattern, "$1=\"$2\"");
        }

        // 4. Normalize legacy XML tags
        content = NormalizeLegacyTags(content, ref fixesCount);

        // 5. Fix malformed CDATA sections if present
        content = NormalizeCDataBlocks(content, ref fixesCount);

        return content;
    }

    private static string FixAmpersandsOutsideCData(string xml, ref int fixesCount)
    {
        // Matches CDATA sections or non-CDATA text
        string cdataPattern = @"(<!\[CDATA\[[\s\S]*?\]\]>)|([^<]+)";
        string ampPattern = @"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)";

        int localAmpFixes = 0;
        string result = Regex.Replace(xml, cdataPattern, match =>
        {
            if (match.Groups[1].Success)
                // Preserve CDATA content as-is
                return match.Groups[1].Value;

            string text = match.Groups[2].Value;
            MatchCollection amps = Regex.Matches(text, ampPattern);
            if (amps.Count > 0)
            {
                localAmpFixes += amps.Count;
                return Regex.Replace(text, ampPattern, "&amp;");
            }

            return match.Value;
        });

        fixesCount += localAmpFixes;
        return result;
    }

    private static string NormalizeLegacyTags(string xml, ref int fixesCount)
    {
        string content = xml;

        // A. Remove or normalize obsolete <interface_version>...</interface_version> tags
        string ivPattern = @"<interface_version\b[^>]*>[\s\S]*?</interface_version>";
        MatchCollection ivMatches = Regex.Matches(content, ivPattern, RegexOptions.IgnoreCase);
        if (ivMatches.Count > 0)
        {
            fixesCount += ivMatches.Count;
            content = Regex.Replace(content, ivPattern, string.Empty, RegexOptions.IgnoreCase);
        }

        // B. Convert legacy <floater ...>...</floater> or <floater ... /> to <panel float="true" ...>
        string floaterOpenPattern = @"<floater(\s+[^>]*)?>";
        MatchCollection floaterMatches = Regex.Matches(content, floaterOpenPattern, RegexOptions.IgnoreCase);
        if (floaterMatches.Count > 0)
        {
            fixesCount += floaterMatches.Count;
            content = Regex.Replace(content, floaterOpenPattern, "<panel float=\"true\"$1>", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"</floater>", "</panel>", RegexOptions.IgnoreCase);
        }

        // C. Convert standalone <align>center</align> or <valign>center</valign> into comment/cleaned
        string alignTagPattern = @"<(v?align)>([^<]+)</\1>";
        MatchCollection alignMatches = Regex.Matches(content, alignTagPattern, RegexOptions.IgnoreCase);
        if (alignMatches.Count > 0)
        {
            fixesCount += alignMatches.Count;
            content = Regex.Replace(content, alignTagPattern, "<!-- $1: $2 -->", RegexOptions.IgnoreCase);
        }

        // D. Convert legacy <anchor x="..." y="..." align="..." /> to <panel x="..." y="..." align="..." />
        string anchorPattern = @"<anchor(\s+[^>]*)?/>";
        MatchCollection anchorMatches = Regex.Matches(content, anchorPattern, RegexOptions.IgnoreCase);
        if (anchorMatches.Count > 0)
        {
            fixesCount += anchorMatches.Count;
            content = Regex.Replace(content, anchorPattern, "<panel$1/>", RegexOptions.IgnoreCase);
        }

        return content;
    }

    private static string NormalizeCDataBlocks(string xml, ref int fixesCount)
    {
        string content = xml;

        // Check for unclosed <![CDATA[ without closing ]]>
        int cdataIndex = content.IndexOf("<![CDATA[", StringComparison.Ordinal);
        while (cdataIndex >= 0)
        {
            int closeIndex = content.IndexOf("]]>", cdataIndex + 9, StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                // Missing CDATA closure: append closing ]]>
                content += "]]>";
                fixesCount++;
                break;
            }

            cdataIndex = content.IndexOf("<![CDATA[", closeIndex + 3, StringComparison.Ordinal);
        }

        return content;
    }
}
