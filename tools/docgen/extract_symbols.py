#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
ProjectKongor Deep Codebase Symbol & XML Documentation Comment Extractor.
Scans all C# (.cs) and Python (.py) source files across apps, services, libs, and tools,
extracting classes, records, interfaces, structs, enums, methods, properties, parameters,
return types, line numbers, and full XML triple-slash (///) documentation comments.
Outputs: docs/_site/symbols.json
"""

from __future__ import annotations

import ast
import datetime
import json
import logging
import os
import re
import sys
import xml.etree.ElementTree as ET
from typing import Any, Dict, List, Optional, Tuple

logger = logging.getLogger("tools.docgen.extract_symbols")


def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


class DeepSymbolExtractor:
    """Extracts deep symbol hierarchies and XML doc comments from source files in pure Python."""

    def __init__(self, repo_root: Optional[str] = None):
        self.repo_root = os.path.abspath(repo_root) if repo_root else _find_repo_root()

    def extract_all(self) -> Dict[str, Any]:
        """Extracts symbols from apps, services, libs, extensions, and tools."""
        results: Dict[str, Any] = {
            "version": "2.0",
            "extracted_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "symbols": [],
            "all_symbols": [],
            "components": {},
            "symbol_count": 0,
            "method_count": 0,
            "property_count": 0,
            "doc_comment_count": 0,
        }

        for category_dir in ["apps", "services", "libs", "extensions", "tools"]:
            cat_path = os.path.join(self.repo_root, "src", category_dir)
            if not os.path.isdir(cat_path):
                cat_path = os.path.join(self.repo_root, category_dir)
            if not os.path.isdir(cat_path):
                continue

            for entry in sorted(os.listdir(cat_path)):
                entry_path = os.path.join(cat_path, entry)
                if not os.path.isdir(entry_path):
                    continue

                component_id = f"{category_dir}-{entry}"
                component_data = self._extract_component_symbols(entry, entry_path, category_dir)
                results["components"][component_id] = component_data

                for cls in component_data.get("classes", []):
                    results["symbol_count"] += 1
                    kind = cls.get("kind", "class")
                    full_name = cls.get("full_name") or cls.get("name", "")

                    symbol_entry = {
                        "name": cls["name"],
                        "full_name": full_name,
                        "kind": kind,
                        "namespace": cls.get("namespace", ""),
                        "file_path": cls["file"],
                        "line_number": cls["line"],
                        "summary": cls.get("summary", ""),
                        "parameters": cls.get("constructor_parameters", []),
                        "returns": "",
                        "signature": cls.get("signature") or f"{kind} {cls['name']}",
                        "modifiers": cls.get("modifiers", []),
                        "base_types": cls.get("base_types", ""),
                    }
                    results["symbols"].append(symbol_entry)

                    # Backward-compatible all_symbols list
                    results["all_symbols"].append({
                        "component_id": component_id,
                        "component_name": entry,
                        "category": category_dir,
                        "type": kind,
                        "name": cls["name"],
                        "full_name": full_name,
                        "namespace": cls.get("namespace", ""),
                        "summary": cls["summary"],
                        "file": cls["file"],
                        "line": cls["line"]
                    })
                    if cls["summary"]:
                        results["doc_comment_count"] += 1

                    for m in cls.get("methods", []):
                        results["method_count"] += 1
                        m_full_name = f"{full_name}.{m['name']}"
                        m_symbol_entry = {
                            "name": m["name"],
                            "full_name": m_full_name,
                            "kind": "method",
                            "namespace": cls.get("namespace", ""),
                            "file_path": cls["file"],
                            "line_number": m["line"],
                            "summary": m.get("summary", ""),
                            "parameters": m.get("parameters", []),
                            "returns": m.get("returns_doc") or m.get("return_type", ""),
                            "signature": m.get("signature", ""),
                            "return_type": m.get("return_type", ""),
                            "modifiers": m.get("modifiers", []),
                            "class_name": cls["name"]
                        }
                        results["symbols"].append(m_symbol_entry)

                        results["all_symbols"].append({
                            "component_id": component_id,
                            "component_name": entry,
                            "category": category_dir,
                            "type": "method",
                            "class_name": cls["name"],
                            "name": m["name"],
                            "full_name": m_full_name,
                            "signature": m["signature"],
                            "return_type": m["return_type"],
                            "summary": m["summary"],
                            "file": cls["file"],
                            "line": m["line"]
                        })
                        if m["summary"]:
                            results["doc_comment_count"] += 1

                    for p in cls.get("properties", []):
                        results["property_count"] += 1
                        p_full_name = f"{full_name}.{p['name']}"
                        p_symbol_entry = {
                            "name": p["name"],
                            "full_name": p_full_name,
                            "kind": "property",
                            "namespace": cls.get("namespace", ""),
                            "file_path": cls["file"],
                            "line_number": p["line"],
                            "summary": p.get("summary", ""),
                            "parameters": [],
                            "returns": p.get("type", ""),
                            "signature": f"{p.get('type', '')} {p['name']}",
                            "return_type": p.get("type", ""),
                            "modifiers": p.get("modifiers", []),
                            "class_name": cls["name"]
                        }
                        results["symbols"].append(p_symbol_entry)

                for py_sym in component_data.get("python_symbols", []):
                    results["symbol_count"] += 1
                    kind = py_sym.get("kind", "function")
                    py_entry = {
                        "name": py_sym["name"],
                        "full_name": py_sym["name"],
                        "kind": kind,
                        "namespace": "",
                        "file_path": py_sym["file"],
                        "line_number": py_sym["line"],
                        "summary": py_sym.get("summary", ""),
                        "parameters": py_sym.get("parameters", []),
                        "returns": "",
                        "signature": py_sym.get("signature", ""),
                        "modifiers": [],
                        "base_types": ""
                    }
                    results["symbols"].append(py_entry)
                    results["all_symbols"].append({
                        "component_id": component_id,
                        "component_name": entry,
                        "category": category_dir,
                        "type": kind,
                        "name": py_sym["name"],
                        "full_name": py_sym["name"],
                        "summary": py_sym.get("summary", ""),
                        "file": py_sym["file"],
                        "line": py_sym["line"]
                    })
                    if py_sym.get("summary"):
                        results["doc_comment_count"] += 1

        return results

    def _extract_component_symbols(self, name: str, dir_path: str, category: str) -> Dict[str, Any]:
        csharp_classes: List[Dict[str, Any]] = []
        python_symbols: List[Dict[str, Any]] = []

        for root, dirs, files in os.walk(dir_path):
            dirs[:] = [
                d for d in dirs
                if d not in (
                    "bin", "obj", ".git", ".vs", "node_modules", "output",
                    "dist", ".cache", ".venv", "venv", "__pycache__", "ComfyUI"
                ) and not d.startswith("blender-")
            ]
            for file in sorted(files):
                full_path = os.path.join(root, file)
                if file.lower().endswith(".cs"):
                    classes = self._parse_csharp_file(full_path)
                    csharp_classes.extend(classes)
                elif file.lower().endswith(".py"):
                    py_syms = self._parse_python_file(full_path)
                    python_symbols.extend(py_syms)

        return {
            "name": name,
            "category": category,
            "rel_path": os.path.relpath(dir_path, self.repo_root).replace("\\", "/"),
            "classes": csharp_classes,
            "python_symbols": python_symbols
        }

    def _parse_csharp_file(self, file_path: str) -> List[Dict[str, Any]]:
        classes: List[Dict[str, Any]] = []
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                content = f.read()
        except Exception:
            return classes

        rel_file = os.path.relpath(file_path, self.repo_root).replace("\\", "/")

        # 1. Detect Namespace (file-scoped `namespace Foo.Bar;` or block `namespace Foo.Bar {`)
        file_namespace = ""
        ns_file_match = re.search(r'^\s*namespace\s+([A-Za-z0-9_.]+)\s*;', content, re.MULTILINE)
        if ns_file_match:
            file_namespace = ns_file_match.group(1).strip()

        # 2. Match Type Definitions: class, interface, struct, record, record class, record struct, enum
        # Handles attributes, access modifiers, generic parameters, positional record parameters, base types
        type_pattern = re.compile(
            r'((?:\s*///[^\n]*\n)*)'  # XML doc block
            r'\s*(?:\[[^\]\n]+\]\s*)*'  # Attributes
            r'((?:public|internal|protected|private|static|sealed|abstract|partial|readonly|unsafe|ref)\s+)*'  # Modifiers
            r'(class|interface|record\s+class|record\s+struct|record|struct|enum)\s+'  # Kind
            r'([A-Za-z0-9_]+)'  # Name
            r'(?:<([^>\n]+)>)?'  # Generics
            r'(?:\s*\(([^)]*)\))?'  # Positional record constructor parameters (if any)
            r'(?:\s*:\s*([^{\n;]+))?',  # Base types
            re.MULTILINE
        )

        for match in type_pattern.finditer(content):
            doc_block = match.group(1) or ""
            modifiers_str = match.group(2) or ""
            kind_raw = match.group(3).strip()
            kind = "record" if "record" in kind_raw else kind_raw
            name = match.group(4).strip()
            generics = match.group(5) or ""
            record_params = match.group(6) or ""
            base_types = match.group(7).strip() if match.group(7) else ""

            if name in ("if", "for", "while", "switch", "catch", "using", "lock", "return", "where"):
                continue

            modifiers = [m.strip() for m in modifiers_str.split() if m.strip()]
            line_no = content[:match.start()].count("\n") + 1

            # Determine enclosing block namespace if file_namespace is empty
            current_namespace = file_namespace
            if not current_namespace:
                # Find preceding block namespace
                prefix = content[:match.start()]
                block_ns_matches = list(re.finditer(r'namespace\s+([A-Za-z0-9_.]+)\s*\{', prefix))
                if block_ns_matches:
                    current_namespace = block_ns_matches[-1].group(1).strip()

            full_name = f"{current_namespace}.{name}" if current_namespace else name
            generic_suffix = f"<{generics}>" if generics else ""
            type_sig = f"{' '.join(modifiers + [kind_raw])} {name}{generic_suffix}".strip()

            summary, params_doc, returns_doc, remarks = self._parse_xml_doc(doc_block)

            # Positional record parameters
            constructor_params = []
            if record_params:
                constructor_params = self._parse_param_list(record_params, params_doc)

            # Extract type body
            type_start = match.end()
            type_body, is_block = self._extract_matching_bracket_block(content, type_start)

            methods: List[Dict[str, Any]] = []
            properties: List[Dict[str, Any]] = []
            enum_members: List[str] = []

            if is_block and type_body:
                if kind == "enum":
                    enum_members = self._extract_enum_members(type_body)
                else:
                    methods = self._extract_csharp_methods(
                        type_body, line_no, rel_file, is_interface=(kind == "interface"), class_name=name
                    )
                    properties = self._extract_csharp_properties(type_body, line_no)

            classes.append({
                "name": name,
                "full_name": full_name,
                "kind": kind,
                "namespace": current_namespace,
                "modifiers": modifiers,
                "generics": generics,
                "base_types": base_types,
                "signature": type_sig,
                "summary": summary,
                "remarks": remarks,
                "constructor_parameters": constructor_params,
                "file": rel_file,
                "line": line_no,
                "methods": methods,
                "properties": properties,
                "enum_members": enum_members
            })

        return classes

    def _extract_matching_bracket_block(self, content: str, start_index: int) -> Tuple[str, bool]:
        """Finds the balanced { ... } block for a type definition or detects semicolon-terminated record."""
        limit = min(len(content), start_index + 300)
        chunk_ahead = content[start_index:limit]

        # Check if type is a semicolon-terminated record or interface declaration
        semicolon_pos = chunk_ahead.find(";")
        brace_pos = chunk_ahead.find("{")

        if semicolon_pos != -1 and (brace_pos == -1 or semicolon_pos < brace_pos):
            return "", False

        if brace_pos == -1:
            return "", False

        actual_brace_pos = start_index + brace_pos
        depth = 0
        end_pos = actual_brace_pos
        in_string = False
        in_char = False
        in_line_comment = False
        in_block_comment = False
        content_len = len(content)

        i = actual_brace_pos
        while i < content_len:
            ch = content[i]
            nxt = content[i + 1] if i + 1 < content_len else ""

            if in_line_comment:
                if ch == "\n":
                    in_line_comment = False
                i += 1
                continue

            if in_block_comment:
                if ch == "*" and nxt == "/":
                    in_block_comment = False
                    i += 2
                    continue
                i += 1
                continue

            if in_string:
                if ch == '\\':
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
                i += 1
                continue

            if in_char:
                if ch == '\\':
                    i += 2
                    continue
                if ch == "'":
                    in_char = False
                i += 1
                continue

            # Check comments
            if ch == "/" and nxt == "/":
                in_line_comment = True
                i += 2
                continue
            if ch == "/" and nxt == "*":
                in_block_comment = True
                i += 2
                continue

            if ch == '"':
                in_string = True
                i += 1
                continue
            if ch == "'":
                in_char = True
                i += 1
                continue

            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    end_pos = i
                    break
            i += 1

        if depth == 0:
            return content[actual_brace_pos:end_pos + 1], True
        return content[actual_brace_pos:min(content_len, actual_brace_pos + 10000)], True

    def _extract_csharp_methods(
        self, class_body: str, class_line_offset: int, file_path: str, is_interface: bool = False, class_name: str = ""
    ) -> List[Dict[str, Any]]:
        methods: List[Dict[str, Any]] = []
        if not class_body:
            return methods

        # Regex for C# methods, including regular methods, interface methods, constructors, async/unsafe/static
        if is_interface:
            # Interface methods: [Modifiers] ReturnType MethodName<T>(Params);
            method_pattern = re.compile(
                r'((?:\s*///[^\n]*\n)*)'  # XML doc
                r'\s*(?:\[[^\]\n]+\]\s*)*'  # Attributes
                r'((?:public|internal|protected|private|static|virtual|abstract|new)\s+)*'  # Optional modifiers
                r'([A-Za-z0-9_<>,\[\]\?\.\s]+?)\s+'  # Return type
                r'([A-Za-z0-9_]+)'  # Method Name
                r'(?:<([^>\n]+)>)?\s*'  # Generic params
                r'\(([^)]*)\)\s*;',  # Parameter list ending in ;
                re.MULTILINE
            )
        else:
            # Class/Struct/Record methods and constructors:
            method_pattern = re.compile(
                r'((?:\s*///[^\n]*\n)*)'  # XML doc
                r'\s*(?:\[[^\]\n]+\]\s*)*'  # Attributes
                r'((?:public|internal|protected|private|static|virtual|override|async|sealed|unsafe|new)\s+)+'  # Modifiers (required for regular classes)
                r'([A-Za-z0-9_<>,\[\]\?\.\s]+?)\s+'  # Return type
                r'([A-Za-z0-9_]+)'  # Method Name
                r'(?:<([^>\n]+)>)?\s*'  # Generic params
                r'\(([^)]*)\)'  # Parameter list
                r'(?:\s*where\s+[^{;=]+)?'  # Where constraints
                r'(?:\s*=>|\s*\{)',  # Method body start
                re.MULTILINE
            )

        for match in method_pattern.finditer(class_body):
            doc_block = match.group(1) or ""
            modifiers_str = match.group(2) or ""
            return_type = match.group(3).strip()
            method_name = match.group(4).strip()
            generics = match.group(5) or ""
            raw_params = match.group(6) or ""

            # Filter out C# control structures that can look like methods
            if method_name in ("if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "sizeof", "typeof"):
                continue

            # Clean modifiers
            modifiers = [m.strip() for m in modifiers_str.split() if m.strip()]

            # Handle constructors where return_type is empty or matched modifier
            if method_name == class_name and not return_type:
                return_type = "void"

            summary, params_doc, returns_doc, remarks = self._parse_xml_doc(doc_block)
            line_no = class_line_offset + class_body[:match.start()].count("\n")

            # Parse parameters
            params_list = self._parse_param_list(raw_params, params_doc)

            generic_suffix = f"<{generics}>" if generics else ""
            clean_params = re.sub(r'\s+', ' ', raw_params).strip()
            sig = f"{' '.join(modifiers)} {return_type} {method_name}{generic_suffix}({clean_params})".strip()

            methods.append({
                "name": method_name,
                "return_type": return_type,
                "modifiers": modifiers,
                "generics": generics,
                "parameters": params_list,
                "signature": sig,
                "summary": summary,
                "returns_doc": returns_doc,
                "remarks": remarks,
                "line": line_no
            })

        return methods

    def _extract_csharp_properties(self, class_body: str, class_line_offset: int) -> List[Dict[str, Any]]:
        properties: List[Dict[str, Any]] = []
        if not class_body:
            return properties

        # Properties with { get; set; } or => expr;
        prop_pattern = re.compile(
            r'((?:\s*///[^\n]*\n)*)'  # Doc block
            r'\s*(?:\[[^\]\n]+\]\s*)*'  # Attributes
            r'((?:public|internal|protected|private|static|virtual|override|readonly|required|init)\s+)+'  # Modifiers
            r'([A-Za-z0-9_<>,\[\]\?\.\s]+?)\s+'  # Property Type
            r'([A-Za-z0-9_]+)\s*'  # Property Name
            r'(?:\{\s*(?:get|set|init)|=>)',  # Property body
            re.MULTILINE
        )

        for match in prop_pattern.finditer(class_body):
            doc_block = match.group(1) or ""
            modifiers_str = match.group(2) or ""
            prop_type = match.group(3).strip()
            prop_name = match.group(4).strip()

            if prop_name in ("get", "set", "init", "value", "return", "throw", "class", "record"):
                continue

            modifiers = [m.strip() for m in modifiers_str.split() if m.strip()]
            summary, _, _, _ = self._parse_xml_doc(doc_block)
            line_no = class_line_offset + class_body[:match.start()].count("\n")

            properties.append({
                "name": prop_name,
                "type": prop_type,
                "modifiers": modifiers,
                "summary": summary,
                "line": line_no
            })

        return properties

    def _extract_enum_members(self, enum_body: str) -> List[str]:
        members: List[str] = []
        clean = re.sub(r'//[^\n]*', '', enum_body)
        clean = re.sub(r'/\*.*?\*/', '', clean, flags=re.DOTALL)
        clean = clean.strip("{} \t\r\n")

        for item in clean.split(","):
            item_s = item.strip()
            if not item_s:
                continue
            name = item_s.split("=")[0].strip()
            name = re.sub(r'\[[^\]]+\]', '', name).strip()
            if name and re.match(r'^[A-Za-z0-9_]+$', name):
                members.append(name)
        return members

    def _parse_param_list(self, raw_params: str, params_doc: Dict[str, str]) -> List[Dict[str, Any]]:
        params_list: List[Dict[str, Any]] = []
        if not raw_params or not raw_params.strip():
            return params_list

        # Split on comma not within angle brackets <...>
        tokens = []
        depth = 0
        current = []
        for ch in raw_params:
            if ch in ('<', '('):
                depth += 1
                current.append(ch)
            elif ch in ('>', ')'):
                depth -= 1
                current.append(ch)
            elif ch == ',' and depth == 0:
                tokens.append("".join(current).strip())
                current = []
            else:
                current.append(ch)
        if current:
            tokens.append("".join(current).strip())

        for param_str in tokens:
            p_clean = param_str.strip()
            if not p_clean:
                continue
            # Remove default value
            param_def = p_clean.split("=")[0].strip()
            parts = param_def.split()
            if parts:
                p_name = parts[-1].lstrip("@")
                p_type = " ".join(parts[:-1]) if len(parts) > 1 else ""
                p_doc = params_doc.get(p_name, "")
                params_list.append({
                    "name": p_name,
                    "type": p_type,
                    "doc": p_doc
                })
        return params_list

    def _parse_xml_doc(self, doc_block: str) -> Tuple[str, Dict[str, str], str, str]:
        """Parses triple-slash /// XML comments."""
        if not doc_block:
            return "", {}, "", ""

        clean_xml_lines = []
        for line in doc_block.split("\n"):
            line_s = line.strip()
            if line_s.startswith("///"):
                clean_xml_lines.append(line_s[3:].strip())

        xml_content = " ".join(clean_xml_lines)
        xml_str = f"<doc>{xml_content}</doc>"
        summary = ""
        params_doc: Dict[str, str] = {}
        returns_doc = ""
        remarks = ""

        try:
            root = ET.fromstring(xml_str)
            sum_elem = root.find("summary")
            if sum_elem is not None:
                summary = self._clean_xml_element_text(sum_elem)

            for p_elem in root.findall("param"):
                p_name = p_elem.get("name", "")
                if p_name:
                    params_doc[p_name] = self._clean_xml_element_text(p_elem)

            ret_elem = root.find("returns")
            if ret_elem is not None:
                returns_doc = self._clean_xml_element_text(ret_elem)

            rem_elem = root.find("remarks")
            if rem_elem is not None:
                remarks = self._clean_xml_element_text(rem_elem)
        except Exception:
            # Fallback regex extraction
            sum_match = re.search(r'<summary>(.*?)</summary>', xml_content, re.DOTALL)
            if sum_match:
                summary = re.sub(r'<[^>]+>', '', sum_match.group(1)).strip()
                summary = " ".join(summary.split())

            for p_match in re.finditer(r'<param\s+name="([^"]+)">(.*?)</param>', xml_content, re.DOTALL):
                p_name = p_match.group(1)
                p_text = re.sub(r'<[^>]+>', '', p_match.group(2)).strip()
                params_doc[p_name] = " ".join(p_text.split())

            ret_match = re.search(r'<returns>(.*?)</returns>', xml_content, re.DOTALL)
            if ret_match:
                returns_doc = re.sub(r'<[^>]+>', '', ret_match.group(1)).strip()
                returns_doc = " ".join(returns_doc.split())

        return summary, params_doc, returns_doc, remarks

    def _clean_xml_element_text(self, elem: ET.Element) -> str:
        """Extracts text and normalizes nested tags like <see cref='X'/> -> X."""
        text_parts = []
        if elem.text:
            text_parts.append(elem.text)
        for child in elem:
            # Handle see cref or c tags
            cref = child.get("cref") or child.get("name")
            if cref:
                text_parts.append(cref.split(".")[-1])
            elif child.text:
                text_parts.append(child.text)
            if child.tail:
                text_parts.append(child.tail)
        return " ".join("".join(text_parts).split())

    def _parse_python_file(self, file_path: str) -> List[Dict[str, Any]]:
        symbols: List[Dict[str, Any]] = []
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                tree = ast.parse(f.read(), filename=file_path)
        except Exception:
            return symbols

        rel_file = os.path.relpath(file_path, self.repo_root).replace("\\", "/")

        for node in ast.walk(tree):
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                doc = ast.get_docstring(node) or ""
                args = [a.arg for a in node.args.args]
                param_objs = [{"name": a, "type": "", "doc": ""} for a in args]
                symbols.append({
                    "kind": "function",
                    "name": node.name,
                    "signature": f"def {node.name}({', '.join(args)})",
                    "docstring": doc,
                    "summary": doc.split("\n")[0].strip() if doc else "",
                    "parameters": param_objs,
                    "file": rel_file,
                    "line": getattr(node, "lineno", 1)
                })
            elif isinstance(node, ast.ClassDef):
                doc = ast.get_docstring(node) or ""
                symbols.append({
                    "kind": "class",
                    "name": node.name,
                    "docstring": doc,
                    "summary": doc.split("\n")[0].strip() if doc else "",
                    "parameters": [],
                    "file": rel_file,
                    "line": getattr(node, "lineno", 1)
                })

        return symbols


def extract_and_save_symbols(repo_root: str, output_json_path: Optional[str] = None) -> Dict[str, Any]:
    """Extracts symbols and saves to the designated JSON output path."""
    extractor = DeepSymbolExtractor(repo_root)
    data = extractor.extract_all()

    if output_json_path:
        os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
        with open(output_json_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        logger.info("Deep symbol index saved to %s (%d symbols extracted)", output_json_path, data["symbol_count"])

    return data


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="[%(levelname)s] %(message)s")
    current_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(current_dir, "..", ".."))
    out_file = os.path.join(repo_root, "docs", "_site", "symbols.json")
    codebase_out_file = os.path.join(repo_root, "docs", "_site", "codebase_symbols.json")

    res = extract_and_save_symbols(repo_root, out_file)
    # Also save codebase_symbols.json for legacy tools
    with open(codebase_out_file, "w", encoding="utf-8") as f:
        json.dump(res, f, indent=2)

    print(
        f"Extracted {res['symbol_count']} types/symbols, {res['method_count']} methods, "
        f"{res['property_count']} properties, {res['doc_comment_count']} XML doc comments."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
