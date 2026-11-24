// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Xml.Linq;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Core.Parsing;

/// <summary>
///     Parser and serializer for HoN mod.xml manifest documents.
/// </summary>
public static class ModManifestParser
{
    public static ModManifest Parse(string xmlContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlContent);

        // Clean any BOM or leading whitespace before XML declaration
        string cleaned = xmlContent.Trim();
        XDocument doc = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return Parse(doc);
    }

    public static ModManifest Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        return Parse(reader.ReadToEnd());
    }

    public static ModManifest Parse(XDocument doc)
    {
        XElement? root = doc.Root;
        if (root == null) throw new FormatException("Invalid mod.xml: Missing root element.");

        string rootName = root.Name.LocalName;
        if (!rootName.Equals("modification", StringComparison.OrdinalIgnoreCase) &&
            !rootName.Equals("mod", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Invalid mod.xml root element '<{rootName}>'. Expected '<modification>'.");

        string name = GetAttrOrChild(root, "name") ?? "Unnamed Mod";
        string version = GetAttrOrChild(root, "version") ?? "1.0.0";
        string? author = GetAttrOrChild(root, "author");
        string? description = GetAttrOrChild(root, "description");
        string? date = GetAttrOrChild(root, "date");
        string? appVersion = GetAttrOrChild(root, "appversion");
        string? mmVersion = GetAttrOrChild(root, "mmversion");
        string? weblink = GetAttrOrChild(root, "weblink");
        string? updateCheckUrl = GetAttrOrChild(root, "updatecheckurl");
        string? updateDownloadUrl = GetAttrOrChild(root, "updatedownloadurl");
        string? icon = GetAttrOrChild(root, "icon");
        string? category = GetAttrOrChild(root, "category");

        List<ModRequirement> requirements = [];
        List<ModIncompatibility> incompatibilities = [];
        List<ModOrderingRule> applyBefore = [];
        List<ModOrderingRule> applyAfter = [];
        List<CopyFileOperation> fileCopies = [];
        List<EditFileOperation> fileEdits = [];
        List<EditStringtableOperation> stringtableEdits = [];
        Dictionary<string, string> customProps = new(StringComparer.OrdinalIgnoreCase);

        // Store extra root attributes in custom properties
        foreach (XAttribute attr in root.Attributes())
        {
            string aname = attr.Name.LocalName;
            if (!IsStandardRootAttribute(aname)) customProps[aname] = attr.Value;
        }

        foreach (XElement element in root.Elements())
        {
            string elName = element.Name.LocalName.ToLowerInvariant();
            switch (elName)
            {
                case "requirement":
                case "requires":
                case "depend":
                case "dependency":
                    ParseRequirement(element, requirements);
                    break;

                case "incompatibility":
                case "incompatible":
                case "conflict":
                case "conflicts":
                    ParseIncompatibility(element, incompatibilities);
                    break;

                case "applybefore":
                case "before":
                    ParseOrderingRule(element, applyBefore);
                    break;

                case "applyafter":
                case "after":
                    ParseOrderingRule(element, applyAfter);
                    break;

                case "copyfile":
                case "copy":
                    ParseCopyFile(element, fileCopies);
                    break;

                case "editfile":
                case "edit":
                    ParseEditFile(element, fileEdits);
                    break;

                case "editstringtable":
                case "stringtable":
                    ParseStringtableEdit(element, stringtableEdits);
                    break;

                default:
                    // Might be metadata child if not an attribute
                    if (IsStandardMetadataChild(elName))
                    {
                        // Already handled by GetAttrOrChild if attribute was missing
                    }
                    else
                    {
                        customProps[element.Name.LocalName] = element.Value;
                    }

                    break;
            }
        }

        return new ModManifest
        {
            Name = name,
            Version = version,
            Author = author,
            Description = description,
            Date = date,
            AppVersion = appVersion,
            MmVersion = mmVersion,
            WebLink = weblink,
            UpdateCheckUrl = updateCheckUrl,
            UpdateDownloadUrl = updateDownloadUrl,
            Icon = icon,
            Category = category,
            Requirements = requirements,
            Incompatibilities = incompatibilities,
            ApplyBefore = applyBefore,
            ApplyAfter = applyAfter,
            FileCopies = fileCopies,
            FileEdits = fileEdits,
            StringtableEdits = stringtableEdits,
            CustomProperties = customProps
        };
    }

    private static void ParseRequirement(XElement element, List<ModRequirement> list)
    {
        string? name = element.Attribute("name")?.Value;
        string? version = element.Attribute("version")?.Value;

        if (string.IsNullOrWhiteSpace(name))
        {
            string text = element.Value.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                (string n, string? v) = ExtractNameAndVersion(text);
                name = n;
                version ??= v;
            }
        }

        if (!string.IsNullOrWhiteSpace(name)) list.Add(new ModRequirement(name, version));
    }

    private static void ParseIncompatibility(XElement element, List<ModIncompatibility> list)
    {
        string? name = element.Attribute("name")?.Value;
        string? version = element.Attribute("version")?.Value;

        if (string.IsNullOrWhiteSpace(name))
        {
            string text = element.Value.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                (string n, string? v) = ExtractNameAndVersion(text);
                name = n;
                version ??= v;
            }
        }

        if (!string.IsNullOrWhiteSpace(name)) list.Add(new ModIncompatibility(name, version));
    }

    private static void ParseOrderingRule(XElement element, List<ModOrderingRule> list)
    {
        string? name = element.Attribute("name")?.Value ?? element.Value.Trim();
        string? version = element.Attribute("version")?.Value;

        if (!string.IsNullOrWhiteSpace(name))
        {
            (string n, string? v) = ExtractNameAndVersion(name);
            list.Add(new ModOrderingRule(n, version ?? v));
        }
    }

    private static void ParseCopyFile(XElement element, List<CopyFileOperation> list)
    {
        string? name = element.Attribute("name")?.Value;
        string? source = element.Attribute("source")?.Value ?? name;
        string? version = element.Attribute("version")?.Value;
        string? condition = element.Attribute("condition")?.Value;
        string overwriteStr = element.Attribute("overwrite")?.Value?.ToLowerInvariant() ?? "yes";

        FileOverwriteMode overwriteMode = overwriteStr switch
        {
            "no" => FileOverwriteMode.No,
            "newer" => FileOverwriteMode.Newer,
            _ => FileOverwriteMode.Yes
        };

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(source))
            list.Add(new CopyFileOperation
            {
                TargetName = name,
                SourcePath = source,
                Version = version,
                Overwrite = overwriteMode,
                Condition = condition
            });
    }

    private static void ParseEditFile(XElement element, List<EditFileOperation> list)
    {
        string? name = element.Attribute("name")?.Value;
        string? condition = element.Attribute("condition")?.Value;

        if (string.IsNullOrWhiteSpace(name)) return;

        List<PatchOperation> operations = [];

        foreach (XElement opElem in element.Elements())
        {
            string opName = opElem.Name.LocalName.ToLowerInvariant();
            switch (opName)
            {
                case "find":
                case "seek":
                case "search":
                {
                    FindPosition pos = ParseFindPosition(opElem.Attribute("position")?.Value, out int offset);
                    string target = GetElementContent(opElem);
                    bool caseSensitive = ParseBool(opElem.Attribute("casesensitive")?.Value, true);
                    operations.Add(new FindOperation(target, pos, offset, caseSensitive));
                }
                    break;

                case "findup":
                case "seekup":
                case "searchup":
                {
                    FindPosition pos = ParseFindPosition(opElem.Attribute("position")?.Value, out int offset);
                    string target = GetElementContent(opElem);
                    bool caseSensitive = ParseBool(opElem.Attribute("casesensitive")?.Value, true);
                    operations.Add(new FindUpOperation(target, pos, offset, caseSensitive));
                }
                    break;

                case "findall":
                {
                    string target = GetElementContent(opElem);
                    bool caseSensitive = ParseBool(opElem.Attribute("casesensitive")?.Value, true);
                    operations.Add(new FindAllOperation(target, caseSensitive));
                }
                    break;

                case "insert":
                case "add":
                {
                    string? posStr = opElem.Attribute("position")?.Value?.ToLowerInvariant();
                    InsertPosition pos = posStr == "before" ? InsertPosition.Before : InsertPosition.After;
                    string? source = opElem.Attribute("source")?.Value;
                    string content = GetElementContent(opElem);
                    operations.Add(new InsertOperation(content, source, pos));
                }
                    break;

                case "replace":
                {
                    string? source = opElem.Attribute("source")?.Value;
                    string content = GetElementContent(opElem);
                    operations.Add(new ReplaceOperation(content, source));
                }
                    break;

                case "delete":
                case "remove":
                    operations.Add(new DeleteOperation());
                    break;
            }
        }

        list.Add(new EditFileOperation
        {
            TargetName = name,
            Condition = condition,
            Operations = operations
        });
    }

    private static void ParseStringtableEdit(XElement element, List<EditStringtableOperation> list)
    {
        string? name = element.Attribute("name")?.Value;
        string? condition = element.Attribute("condition")?.Value;

        if (string.IsNullOrWhiteSpace(name)) return;

        List<StringtableEntryOperation> entries = [];
        foreach (XElement entryElem in element.Elements())
        {
            string ename = entryElem.Name.LocalName.ToLowerInvariant();
            string? key = entryElem.Attribute("key")?.Value ?? entryElem.Attribute("name")?.Value;
            string value = entryElem.Attribute("value")?.Value ?? entryElem.Value;

            if (ename is "delete" or "remove")
            {
                if (!string.IsNullOrEmpty(key))
                    entries.Add(new StringtableEntryOperation(key, string.Empty, StringtableAction.Delete));
            }
            else
            {
                if (!string.IsNullOrEmpty(key)) entries.Add(new StringtableEntryOperation(key, value));
            }
        }

        list.Add(new EditStringtableOperation
        {
            TargetName = name,
            Condition = condition,
            Entries = entries
        });
    }

    private static string GetElementContent(XElement element)
    {
        // Check if there are raw text nodes or CDATA
        StringBuilder sb = new();
        foreach (XNode node in element.Nodes())
            if (node is XCData cdata)
                sb.Append(cdata.Value);
            else if (node is XText text)
                sb.Append(text.Value);
            else
                sb.Append(node);

        return sb.ToString();
    }

    private static FindPosition ParseFindPosition(string? posStr, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(posStr)) return FindPosition.None;

        posStr = posStr.Trim().ToLowerInvariant();
        if (int.TryParse(posStr, out int num))
        {
            offset = num;
            return FindPosition.Offset;
        }

        return posStr switch
        {
            "before" => FindPosition.Before,
            "begin" or "start" or "head" => FindPosition.Begin,
            "after" => FindPosition.After,
            "end" or "tail" or "eof" => FindPosition.End,
            _ => FindPosition.None
        };
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (bool.TryParse(value, out bool result)) return result;
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
        if (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
        return defaultValue;
    }

    private static (string name, string? version) ExtractNameAndVersion(string input)
    {
        int bracketStart = input.IndexOf('[');
        if (bracketStart >= 0)
        {
            int bracketEnd = input.IndexOf(']', bracketStart);
            if (bracketEnd > bracketStart)
            {
                string name = input[..bracketStart].Trim();
                string ver = input.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim()
                    .TrimStart('v', 'V');
                return (name, ver);
            }
        }

        return (input.Trim(), null);
    }

    private static string? GetAttrOrChild(XElement element, string name)
    {
        XAttribute? attr = element.Attribute(name) ?? element.Attribute(name.ToLowerInvariant());
        if (attr != null) return attr.Value;

        XElement? child = element.Element(name) ?? element.Element(name.ToLowerInvariant());
        return child?.Value;
    }

    private static bool IsStandardRootAttribute(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "name" or "version" or "author" or "description" or "date" or
                "appversion" or "mmversion" or "weblink" or "updatecheckurl" or
                "updatedownloadurl" or "icon" or "category" or "application" => true,
            _ => false
        };
    }

    private static bool IsStandardMetadataChild(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "name" or "version" or "author" or "description" or "date" or
                "appversion" or "mmversion" or "weblink" or "updatecheckurl" or
                "updatedownloadurl" or "icon" or "category" or "application" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Serializes a ModManifest back into canonical mod.xml string.
    /// </summary>
    public static string Serialize(ModManifest manifest)
    {
        XDocument doc = new(new XDeclaration("1.0", "utf-8", "yes"));
        XElement root = new("modification",
            new XAttribute("application", "Heroes of Newerth"),
            new XAttribute("appversion", manifest.AppVersion ?? "*"),
            new XAttribute("mmversion", manifest.MmVersion ?? "1.3"),
            new XAttribute("name", manifest.Name),
            new XAttribute("version", manifest.Version)
        );

        if (!string.IsNullOrEmpty(manifest.Author)) root.Add(new XAttribute("author", manifest.Author));
        if (!string.IsNullOrEmpty(manifest.Date)) root.Add(new XAttribute("date", manifest.Date));
        if (!string.IsNullOrEmpty(manifest.Description)) root.Add(new XAttribute("description", manifest.Description));
        if (!string.IsNullOrEmpty(manifest.WebLink)) root.Add(new XAttribute("weblink", manifest.WebLink));
        if (!string.IsNullOrEmpty(manifest.UpdateCheckUrl))
            root.Add(new XAttribute("updatecheckurl", manifest.UpdateCheckUrl));
        if (!string.IsNullOrEmpty(manifest.UpdateDownloadUrl))
            root.Add(new XAttribute("updatedownloadurl", manifest.UpdateDownloadUrl));
        if (!string.IsNullOrEmpty(manifest.Icon)) root.Add(new XAttribute("icon", manifest.Icon));
        if (!string.IsNullOrEmpty(manifest.Category)) root.Add(new XAttribute("category", manifest.Category));

        foreach (KeyValuePair<string, string> kvp in manifest.CustomProperties)
            root.Add(new XAttribute(kvp.Key, kvp.Value));

        foreach (ModRequirement req in manifest.Requirements)
        {
            XElement el = new("requirement", new XAttribute("name", req.Name));
            if (!string.IsNullOrEmpty(req.VersionExpression)) el.Add(new XAttribute("version", req.VersionExpression));
            root.Add(el);
        }

        foreach (ModIncompatibility incomp in manifest.Incompatibilities)
        {
            XElement el = new("incompatibility", new XAttribute("name", incomp.Name));
            if (!string.IsNullOrEmpty(incomp.VersionExpression))
                el.Add(new XAttribute("version", incomp.VersionExpression));
            root.Add(el);
        }

        foreach (ModOrderingRule ab in manifest.ApplyBefore)
        {
            XElement el = new("applybefore", new XAttribute("name", ab.Name));
            if (!string.IsNullOrEmpty(ab.VersionExpression)) el.Add(new XAttribute("version", ab.VersionExpression));
            root.Add(el);
        }

        foreach (ModOrderingRule aa in manifest.ApplyAfter)
        {
            XElement el = new("applyafter", new XAttribute("name", aa.Name));
            if (!string.IsNullOrEmpty(aa.VersionExpression)) el.Add(new XAttribute("version", aa.VersionExpression));
            root.Add(el);
        }

        foreach (CopyFileOperation copy in manifest.FileCopies)
        {
            XElement el = new("copyfile",
                new XAttribute("name", copy.TargetName),
                new XAttribute("source", copy.SourcePath));

            if (!string.IsNullOrEmpty(copy.Version)) el.Add(new XAttribute("version", copy.Version));
            if (copy.Overwrite != FileOverwriteMode.Yes)
                el.Add(new XAttribute("overwrite", copy.Overwrite.ToString().ToLowerInvariant()));
            if (!string.IsNullOrEmpty(copy.Condition)) el.Add(new XAttribute("condition", copy.Condition));
            root.Add(el);
        }

        foreach (EditFileOperation edit in manifest.FileEdits)
        {
            XElement el = new("editfile", new XAttribute("name", edit.TargetName));
            if (!string.IsNullOrEmpty(edit.Condition)) el.Add(new XAttribute("condition", edit.Condition));

            foreach (PatchOperation op in edit.Operations)
                switch (op)
                {
                    case FindOperation find:
                    {
                        XElement findEl = new("find", new XCData(find.Target));
                        if (find.Position == FindPosition.Offset)
                            findEl.Add(new XAttribute("position", find.Offset.ToString()));
                        else if (find.Position != FindPosition.None)
                            findEl.Add(new XAttribute("position", find.Position.ToString().ToLowerInvariant()));
                        if (!find.CaseSensitive) findEl.Add(new XAttribute("casesensitive", "false"));
                        el.Add(findEl);
                    }
                        break;

                    case FindUpOperation findup:
                    {
                        XElement findupEl = new("findup", new XCData(findup.Target));
                        if (findup.Position == FindPosition.Offset)
                            findupEl.Add(new XAttribute("position", findup.Offset.ToString()));
                        else if (findup.Position != FindPosition.None)
                            findupEl.Add(new XAttribute("position", findup.Position.ToString().ToLowerInvariant()));
                        if (!findup.CaseSensitive) findupEl.Add(new XAttribute("casesensitive", "false"));
                        el.Add(findupEl);
                    }
                        break;

                    case FindAllOperation findall:
                    {
                        XElement findallEl = new("findall", new XCData(findall.Target));
                        if (!findall.CaseSensitive) findallEl.Add(new XAttribute("casesensitive", "false"));
                        el.Add(findallEl);
                    }
                        break;

                    case InsertOperation ins:
                    {
                        XElement insEl = new("insert");
                        if (ins.Position == InsertPosition.Before) insEl.Add(new XAttribute("position", "before"));
                        if (!string.IsNullOrEmpty(ins.SourceFile)) insEl.Add(new XAttribute("source", ins.SourceFile));
                        if (!string.IsNullOrEmpty(ins.Content)) insEl.Add(new XCData(ins.Content));
                        el.Add(insEl);
                    }
                        break;

                    case ReplaceOperation rep:
                    {
                        XElement repEl = new("replace");
                        if (!string.IsNullOrEmpty(rep.SourceFile)) repEl.Add(new XAttribute("source", rep.SourceFile));
                        if (!string.IsNullOrEmpty(rep.Content)) repEl.Add(new XCData(rep.Content));
                        el.Add(repEl);
                    }
                        break;

                    case DeleteOperation:
                        el.Add(new XElement("delete"));
                        break;
                }

            root.Add(el);
        }

        foreach (EditStringtableOperation strEdit in manifest.StringtableEdits)
        {
            XElement el = new("editstringtable", new XAttribute("name", strEdit.TargetName));
            if (!string.IsNullOrEmpty(strEdit.Condition)) el.Add(new XAttribute("condition", strEdit.Condition));

            foreach (StringtableEntryOperation entry in strEdit.Entries)
                if (entry.Action == StringtableAction.Delete)
                    el.Add(new XElement("delete", new XAttribute("key", entry.Key)));
                else
                    el.Add(new XElement("set", new XAttribute("key", entry.Key), new XAttribute("value", entry.Value)));

            root.Add(el);
        }

        doc.Add(root);
        return doc.ToString();
    }
}
