// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml.Linq;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Patching;

/// <summary>
///     Resilient AST-level XML matcher and transformer for HoN .interface, .package, and entity documents.
/// </summary>
internal static class XmlAstPatcher
{
    public static XDocument Parse(string xmlContent)
    {
        return XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }

    public static string Serialize(XDocument doc)
    {
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    ///     Finds a single element matching a tag name, name attribute, or id attribute.
    ///     Selector format: 'tag', 'tag#id', 'tag[name=foo]', or just 'name' / '#id'.
    /// </summary>
    public static XElement? FindElement(XContainer container, string selector)
    {
        return FindElements(container, selector).FirstOrDefault();
    }

    /// <summary>
    ///     Finds all elements matching a selector.
    /// </summary>
    public static IEnumerable<XElement> FindElements(XContainer container, string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        (string? tagName, string? id, string? attrName, string? attrVal) parsed = ParseSelector(selector);

        return container.Descendants().Where(el =>
        {
            if (parsed.tagName != null &&
                !el.Name.LocalName.Equals(parsed.tagName, StringComparison.OrdinalIgnoreCase)) return false;

            if (parsed.id != null)
            {
                string? idAttr = el.Attribute("id")?.Value ?? el.Attribute("name")?.Value;
                if (!string.Equals(idAttr, parsed.id, StringComparison.OrdinalIgnoreCase)) return false;
            }

            if (parsed.attrName != null)
            {
                string? attrVal = el.Attribute(parsed.attrName)?.Value;
                if (attrVal == null || (parsed.attrVal != null &&
                                        !string.Equals(attrVal, parsed.attrVal, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        });
    }

    public static void InsertChild(XElement parent, XElement newElement, InsertPosition position = InsertPosition.After)
    {
        if (position == InsertPosition.Before)
            parent.AddFirst(newElement);
        else
            parent.Add(newElement);
    }

    public static void InsertSibling(XElement target, XElement newElement,
        InsertPosition position = InsertPosition.After)
    {
        if (position == InsertPosition.Before)
            target.AddBeforeSelf(newElement);
        else
            target.AddAfterSelf(newElement);
    }

    public static void ReplaceElement(XElement target, XElement newElement)
    {
        target.ReplaceWith(newElement);
    }

    public static void RemoveElement(XElement target)
    {
        target.Remove();
    }

    public static void SetAttribute(XElement target, string name, string value)
    {
        target.SetAttributeValue(name, value);
    }

    private static (string? tagName, string? id, string? attrName, string? attrVal) ParseSelector(string selector)
    {
        selector = selector.Trim();
        string? tag = null;
        string? id = null;
        string? attrName = null;
        string? attrVal = null;

        // Check for attribute bracket: panel[@name='chat_box'] or panel[name=chat_box]
        int bracketStart = selector.IndexOf('[');
        if (bracketStart >= 0)
        {
            int bracketEnd = selector.IndexOf(']', bracketStart);
            if (bracketEnd > bracketStart)
            {
                string attrExpr = selector.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim()
                    .TrimStart('@');
                int eqIdx = attrExpr.IndexOf('=');
                if (eqIdx >= 0)
                {
                    attrName = attrExpr[..eqIdx].Trim();
                    attrVal = attrExpr[(eqIdx + 1)..].Trim().Trim('\'', '"');
                }
                else
                {
                    attrName = attrExpr;
                }

                selector = selector[..bracketStart].Trim();
            }
        }

        // Check for hash id: panel#chat_box or #chat_box
        int hashIdx = selector.IndexOf('#');
        if (hashIdx >= 0)
        {
            id = selector[(hashIdx + 1)..].Trim();
            selector = selector[..hashIdx].Trim();
        }

        if (!string.IsNullOrWhiteSpace(selector)) tag = selector;

        return (tag, id, attrName, attrVal);
    }
}
