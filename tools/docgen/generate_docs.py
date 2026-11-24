#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
ProjectKongor Automated Codebase Introspection & Full-Featured Documentation Hub Generator.
Scans all apps, services, libraries, and developer tools across the repository,
extracts live .csproj metadata, C# classes/controllers/endpoints, and full Markdown READMEs,
and generates an interactive, searchable documentation portal with sidebar navigation,
full article rendering, code highlighting, and API catalogs.
"""

from __future__ import annotations

import argparse
import ast
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from typing import Any, Dict, List, Optional, Set, Tuple

logger = logging.getLogger("tools.docgen.generate_docs")


def setup_logging(verbose: bool = False) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    logging.basicConfig(
        level=level,
        format="[%(asctime)s] [%(levelname)s] [DocGen] %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        handlers=[logging.StreamHandler(sys.stdout)],
        force=True
    )


def _find_repo_root() -> str:
    curr = os.path.abspath(os.path.dirname(__file__))
    while curr and os.path.dirname(curr) != curr:
        if os.path.exists(os.path.join(curr, "Directory.Build.props")):
            return curr
        curr = os.path.dirname(curr)
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


class CodebaseIntrospector:
    """Introspects C#, C++, Python, and Markdown files across ProjectKongor."""

    def __init__(self, repo_root: Optional[str] = None):
        self.repo_root = os.path.abspath(repo_root) if repo_root else _find_repo_root()
        self.apps: List[Dict[str, Any]] = []
        self.services: List[Dict[str, Any]] = []
        self.libs: List[Dict[str, Any]] = []
        self.extensions: List[Dict[str, Any]] = []
        self.tools: List[Dict[str, Any]] = []
        self.general_docs: List[Dict[str, Any]] = []

    def scan_all(self) -> None:
        logger.info("Scanning repository for all apps, microservices, libraries, extensions, and developer tools...")
        self.apps = self._scan_directory("src/apps", "Application")
        self.services = self._scan_directory("src/services", "Microservice")
        self.libs = self._scan_directory("src/libs", "Library")
        self.extensions = self._scan_directory("src/extensions", "Extension")
        self.tools = self._scan_directory("src/tools", "Developer Tool") + self._scan_directory("tools", "Developer Tool")
        self.general_docs = self._scan_general_docs()
        logger.info(
            "Scan complete: Found %d apps, %d microservices, %d libraries, %d extensions, %d developer tools, %d general guides.",
            len(self.apps), len(self.services), len(self.libs), len(self.extensions), len(self.tools), len(self.general_docs)
        )

    def _scan_general_docs(self) -> List[Dict[str, Any]]:
        docs = []
        candidates = [
            ("overview", "Overview & Getting Started", "docs/specs/replay-renderer-spec.md"),
            ("cli-guide", "Unified CLI Reference Guide", "docs/guides/cli-reference.md"),
            ("architecture", "Ecosystem Architecture Map", "docs/architecture/ecosystem.md"),
            ("general-readme", "ProjectKongor Master README", "README.md")
        ]
        for doc_id, title, rel_path in candidates:
            full_path = os.path.join(self.repo_root, rel_path)
            if os.path.isfile(full_path):
                try:
                    with open(full_path, "r", encoding="utf-8", errors="ignore") as f:
                        content = f.read()
                    docs.append({
                        "id": doc_id,
                        "title": title,
                        "rel_path": rel_path,
                        "content": content
                    })
                except Exception as e:
                    logger.debug("Error reading %s: %s", rel_path, e)
        return docs

    def _scan_directory(self, relative_dir: str, category_label: str) -> List[Dict[str, Any]]:
        target_path = os.path.join(self.repo_root, relative_dir)
        if not os.path.isdir(target_path):
            return []

        results = []
        for entry in os.listdir(target_path):
            if entry.startswith(".") or entry in ("__pycache__", "bin", "obj", "node_modules", "output", "dist", ".cache", ".venv", "venv"):
                continue
            entry_path = os.path.join(target_path, entry)
            if not os.path.isdir(entry_path):
                continue

            info = self._introspect_component(entry, entry_path, relative_dir, category_label)
            if info:
                results.append(info)

        results.sort(key=lambda x: x["name"].lower())
        return results

    def _introspect_component(self, name: str, path: str, parent_dir: str, category: str) -> Optional[Dict[str, Any]]:
        csprojs = []
        csharp_files = []
        python_files = []
        readme_path = None

        for root, dirs, files in os.walk(path):
            dirs[:] = [
                d for d in dirs
                if d not in (
                    "bin", "obj", ".git", ".vs", "node_modules", "output",
                    "dist", ".cache", ".venv", "venv", "__pycache__", "ComfyUI"
                ) and not d.startswith("blender-")
            ]
            for f in files:
                full_f = os.path.join(root, f)
                if f.lower().endswith(".csproj"):
                    csprojs.append(full_f)
                elif f.lower().endswith(".cs"):
                    csharp_files.append(full_f)
                elif f.lower().endswith(".py"):
                    python_files.append(full_f)
                elif f.lower() == "readme.md" and not readme_path:
                    readme_path = full_f

        package_refs = []
        project_refs = []
        target_framework = "net11.0"
        assembly_name = name
        description = f"{category} component in {parent_dir}/{name}"

        for csproj in csprojs:
            try:
                tree = ET.parse(csproj)
                root_elem = tree.getroot()
                tf = root_elem.find(".//TargetFramework")
                if tf is not None and tf.text:
                    target_framework = tf.text
                an = root_elem.find(".//AssemblyName")
                if an is not None and an.text:
                    assembly_name = an.text
                desc = root_elem.find(".//Description")
                if desc is not None and desc.text:
                    description = desc.text

                for pr in root_elem.findall(".//PackageReference"):
                    inc = pr.get("Include")
                    ver = pr.get("Version", "latest")
                    if inc:
                        package_refs.append(f"{inc} ({ver})")

                for proj_r in root_elem.findall(".//ProjectReference"):
                    inc = proj_r.get("Include")
                    if inc:
                        clean_proj = os.path.splitext(os.path.basename(inc))[0]
                        project_refs.append(clean_proj)
            except Exception as e:
                logger.debug("Error parsing %s: %s", csproj, e)

        parsed_classes = []
        for cs_file in csharp_files[:60]:
            classes = self._parse_csharp_file(cs_file)
            parsed_classes.extend(classes)

        parsed_python = []
        for py_file in python_files[:30]:
            py_info = self._parse_python_file(py_file)
            parsed_python.extend(py_info)

        readme_content = ""
        if readme_path and os.path.isfile(readme_path):
            try:
                with open(readme_path, "r", encoding="utf-8", errors="ignore") as rf:
                    readme_content = rf.read()
            except Exception:
                pass

        doc_id = f"{parent_dir}-{name}"

        return {
            "id": doc_id,
            "name": name,
            "category": category,
            "parent_dir": parent_dir,
            "rel_path": os.path.relpath(path, self.repo_root).replace("\\", "/"),
            "assembly_name": assembly_name,
            "target_framework": target_framework,
            "description": description,
            "package_refs": sorted(list(set(package_refs))),
            "project_refs": sorted(list(set(project_refs))),
            "classes": parsed_classes,
            "python_tools": parsed_python,
            "csharp_count": len(csharp_files),
            "python_count": len(python_files),
            "readme_content": readme_content,
            "has_readme": bool(readme_path)
        }

    def _parse_csharp_file(self, file_path: str) -> List[Dict[str, Any]]:
        classes = []
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                content = f.read()

            rel_file = os.path.relpath(file_path, self.repo_root).replace("\\", "/")
            pattern = re.compile(
                r'(?:public|internal|protected)\s+(?:static\s+|sealed\s+|abstract\s+)?(class|interface|record|struct)\s+([A-Za-z0-9_]+)(?:\s*:\s*([^{]+))?',
                re.MULTILINE
            )

            for match in pattern.finditer(content):
                kind = match.group(1)
                name = match.group(2)
                base_types = match.group(3).strip() if match.group(3) else ""

                start_pos = match.start()
                preceding = content[max(0, start_pos - 400):start_pos]
                summary = ""
                doc_match = re.findall(r'///\s*<summary>\s*([\s\S]*?)\s*///\s*</summary>', preceding)
                if doc_match:
                    summary = " ".join([line.strip().lstrip("///").strip() for line in doc_match[-1].split("\n")])

                classes.append({
                    "name": name,
                    "kind": kind,
                    "base_types": base_types,
                    "summary": summary,
                    "file": rel_file
                })
        except Exception:
            pass
        return classes

    def _parse_python_file(self, file_path: str) -> List[Dict[str, Any]]:
        tools = []
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                tree = ast.parse(f.read(), filename=file_path)

            rel_file = os.path.relpath(file_path, self.repo_root).replace("\\", "/")
            for node in ast.walk(tree):
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    doc = ast.get_docstring(node) or ""
                    tools.append({
                        "name": node.name,
                        "kind": "function",
                        "docstring": doc.split("\n")[0] if doc else "",
                        "file": rel_file
                    })
                elif isinstance(node, ast.ClassDef):
                    doc = ast.get_docstring(node) or ""
                    tools.append({
                        "name": node.name,
                        "kind": "class",
                        "docstring": doc.split("\n")[0] if doc else "",
                        "file": rel_file
                    })
        except Exception:
            pass
        return tools


class DocumentationBuilder:
    """Generates Markdown pages, DocFX metadata, and the full interactive documentation portal."""

    def __init__(self, introspector: CodebaseIntrospector):
        self.intro = introspector
        self.repo_root = introspector.repo_root
        self.docs_dir = os.path.join(self.repo_root, "docs")
        self.site_dir = os.path.join(self.docs_dir, "_site")

    def build_all(self) -> None:
        logger.info("Building full markdown pages and interactive documentation portal...")
        os.makedirs(os.path.join(self.docs_dir, "apps"), exist_ok=True)
        os.makedirs(os.path.join(self.docs_dir, "services"), exist_ok=True)
        os.makedirs(os.path.join(self.docs_dir, "libs"), exist_ok=True)
        os.makedirs(os.path.join(self.docs_dir, "extensions"), exist_ok=True)
        os.makedirs(os.path.join(self.docs_dir, "tools"), exist_ok=True)
        os.makedirs(os.path.join(self.docs_dir, "api"), exist_ok=True)

        self._generate_component_markdown(self.intro.apps, "apps", "Application")
        self._generate_component_markdown(self.intro.services, "services", "Microservice")
        self._generate_component_markdown(self.intro.libs, "libs", "Library")
        self._generate_component_markdown(self.intro.extensions, "extensions", "Extension")
        self._generate_component_markdown(self.intro.tools, "tools", "Developer Tool")

        self._generate_api_catalog()
        self._generate_toc()
        self._generate_full_portal_html()
        logger.info("Interactive Documentation Portal built successfully at docs/_site/index.html")

    def _generate_component_markdown(self, items: List[Dict[str, Any]], folder: str, category_name: str) -> None:
        for item in items:
            name = item["name"]
            out_file = os.path.join(self.docs_dir, folder, f"{name}.md")

            md = [
                f"# {name} ({category_name})",
                "",
                f"> **Category:** `{category_name}` | **Location:** `{item['rel_path']}`",
                "",
                "## 📋 Overview",
                f"{item['description']}",
                "",
                "## ⚙️ Technical Specifications",
                f"* **Assembly / Target:** `{item['assembly_name']}` (`{item['target_framework']}`)",
                f"* **Source Code Metrics:** {item['csharp_count']} C# files, {item['python_count']} Python files",
                ""
            ]

            if item["package_refs"]:
                md.append("## 📦 NuGet Package Dependencies")
                for p in item["package_refs"]:
                    md.append(f"* `{p}`")
                md.append("")

            if item["project_refs"]:
                md.append("## 🔗 Solution References")
                for pr in item["project_refs"]:
                    md.append(f"* `{pr}`")
                md.append("")

            if item["classes"]:
                md.append("## 🏛️ Auto-Introspected Classes & APIs")
                md.append("| Type | Name | Base / Interface | Summary |")
                md.append("|---|---|---|---|")
                for c in item["classes"]:
                    summary = c["summary"] if c["summary"] else "—"
                    base_t = f"`{c['base_types']}`" if c["base_types"] else "—"
                    md.append(f"| `{c['kind']}` | **`{c['name']}`** | {base_t} | {summary} |")
                md.append("")

            if item["python_tools"]:
                md.append("## 🐍 Python Functions & Modules")
                md.append("| Type | Name | Docstring | Source File |")
                md.append("|---|---|---|---|")
                for pt in item["python_tools"]:
                    doc = pt["docstring"] if pt["docstring"] else "—"
                    md.append(f"| `{pt['kind']}` | **`{pt['name']}`** | {doc} | `{pt['file']}` |")
                md.append("")

            if item["readme_content"]:
                md.append("---")
                md.append("## 📖 Component README & Usage Guide")
                md.append(item["readme_content"])
                md.append("")

            with open(out_file, "w", encoding="utf-8") as f:
                f.write("\n".join(md))

    def _generate_api_catalog(self) -> None:
        out_file = os.path.join(self.docs_dir, "api", "index.md")
        md = [
            "# 📚 ProjectKongor Auto-Generated API Reference",
            "",
            "Complete catalog of all classes, interfaces, and controllers extracted directly from .NET 11 solution assemblies.",
            "",
            "## ☁️ Microservices",
            "| Microservice | Target Framework | Extracted Types | Documentation |",
            "|---|---|---|---|"
        ]

        for s in self.intro.services:
            class_count = len(s["classes"])
            md.append(f"| **`{s['name']}`** | `{s['target_framework']}` | {class_count} types | [View Service Docs](../services/{s['name']}.md) |")

        md.extend([
            "",
            "## 🖥️ Applications",
            "| Application | Framework | Extracted Types | Documentation |",
            "|---|---|---|---|"
        ])
        for a in self.intro.apps:
            class_count = len(a["classes"])
            md.append(f"| **`{a['name']}`** | `{a['target_framework']}` | {class_count} types | [View App Docs](../apps/{a['name']}.md) |")

        md.extend([
            "",
            "## 🧩 Extensions",
            "| Extension | Framework | Extracted Types | Documentation |",
            "|---|---|---|---|"
        ])
        for ext in self.intro.extensions:
            class_count = len(ext["classes"])
            md.append(f"| **`{ext['name']}`** | `{ext['target_framework']}` | {class_count} types | [View Extension Docs](../extensions/{ext['name']}.md) |")

        md.extend([
            "",
            "## 🛠️ Developer Tools",
            "| Tool | Language | Extracted APIs | Documentation |",
            "|---|---|---|---|"
        ])
        for t in self.intro.tools:
            lang = "C# (.NET)" if t["csharp_count"] > 0 else "Python"
            count = len(t["classes"]) + len(t["python_tools"])
            md.append(f"| **`{t['name']}`** | {lang} | {count} APIs | [View Tool Docs](../tools/{t['name']}.md) |")

        with open(out_file, "w", encoding="utf-8") as f:
            f.write("\n".join(md))

    def _generate_toc(self) -> None:
        toc_file = os.path.join(self.docs_dir, "toc.yml")
        if os.path.isfile(toc_file):
            return
        toc = [
            "- name: Overview",
            "  href: index.md",
            "- name: Architecture",
            "  href: architecture/ecosystem.md",
            "- name: CLI Reference",
            "  href: guides/cli-reference.md",
            "- name: Applications",
            "  items:"
        ]
        for a in self.intro.apps:
            toc.append(f"    - name: {a['name']}")
            toc.append(f"      href: apps/{a['name']}.md")

        toc.append("- name: Microservices")
        toc.append("  items:")
        for s in self.intro.services:
            toc.append(f"    - name: {s['name']}")
            toc.append(f"      href: services/{s['name']}.md")

        toc.append("- name: Libraries")
        toc.append("  items:")
        for lib in self.intro.libs:
            toc.append(f"    - name: {lib['name']}")
            toc.append(f"      href: libs/{lib['name']}.md")

        toc.append("- name: Extensions")
        toc.append("  items:")
        for ext in self.intro.extensions:
            toc.append(f"    - name: {ext['name']}")
            toc.append(f"      href: extensions/{ext['name']}.md")

        toc.append("- name: Developer Tools")
        toc.append("  items:")
        for t in self.intro.tools:
            toc.append(f"    - name: {t['name']}")
            toc.append(f"      href: tools/{t['name']}.md")

        toc.append("- name: API Catalog")
        toc.append("  href: api/index.md")

        with open(toc_file, "w", encoding="utf-8") as f:
            f.write("\n".join(toc))

    def _generate_full_portal_html(self) -> None:
        """Generates the interactive documentation portal with sidebar navigation, full article reader, and search."""
        os.makedirs(self.site_dir, exist_ok=True)

        src_img = os.path.join(self.docs_dir, "images")
        dst_img = os.path.join(self.site_dir, "images")
        if os.path.isdir(src_img):
            shutil.copytree(src_img, dst_img, dirs_exist_ok=True)

        # Collect all documentation articles
        articles = {}

        # General articles
        for g in self.intro.general_docs:
            articles[g["id"]] = {
                "id": g["id"],
                "title": g["title"],
                "category": "Guide",
                "rel_path": g["rel_path"],
                "content": g["content"],
                "classes": [],
                "framework": "Docs",
                "packages": []
            }

        # Component articles
        for cat, items in [
            ("Applications", self.intro.apps),
            ("Microservices", self.intro.services),
            ("Libraries", self.intro.libs),
            ("Developer Tools", self.intro.tools)
        ]:
            for item in items:
                # Generate full markdown body
                full_md = [
                    f"# {item['name']}",
                    "",
                    f"> **Category:** `{item['category']}` | **Path:** `{item['rel_path']}` | **Target:** `{item['target_framework']}`",
                    "",
                    "## 📋 Description & Purpose",
                    f"{item['description']}",
                    ""
                ]

                if item["readme_content"]:
                    full_md.append("---")
                    full_md.append(item["readme_content"])
                    full_md.append("")

                if item["classes"]:
                    full_md.append("---")
                    full_md.append("## 🏛️ Auto-Introspected Classes & APIs")
                    full_md.append("| Type | Name | Base / Interface | Summary |")
                    full_md.append("|---|---|---|---|")
                    for c in item["classes"]:
                        summary = c["summary"] if c["summary"] else "—"
                        base_t = f"`{c['base_types']}`" if c["base_types"] else "—"
                        md_row = f"| `{c['kind']}` | **`{c['name']}`** | {base_t} | {summary} |"
                        full_md.append(md_row)
                    full_md.append("")

                if item["package_refs"]:
                    full_md.append("---")
                    full_md.append("## 📦 NuGet Package Dependencies")
                    for p in item["package_refs"]:
                        full_md.append(f"* `{p}`")
                    full_md.append("")

                articles[item["id"]] = {
                    "id": item["id"],
                    "title": item["name"],
                    "category": cat,
                    "rel_path": item["rel_path"],
                    "content": "\n".join(full_md),
                    "classes": [c["name"] for c in item["classes"]],
                    "framework": item["target_framework"],
                    "packages": item["package_refs"]
                }

        articles_json = json.dumps(articles)

        # Build portal HTML
        html = f"""<!DOCTYPE html>
<html lang="en" class="dark">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>ProjectKongor Developer Documentation Hub</title>
    <!-- Tailwind CSS CDN -->
    <script src="https://cdn.tailwindcss.com"></script>
    <!-- Marked.js Markdown Parser CDN -->
    <script src="https://cdn.jsdelivr.net/npm/marked/marked.min.js"></script>
    <script>
        tailwind.config = {{
            darkMode: 'class',
            theme: {{
                extend: {{
                    colors: {{
                        brand: {{
                            400: '#c084fc',
                            500: '#a855f7',
                            600: '#9333ea',
                            900: '#581c87',
                            950: '#2e1065'
                        }}
                    }}
                }}
            }}
        }}
    </script>
    <style>
        body {{ background-color: #0b0f19; color: #e2e8f0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }}
        .glass-panel {{ background: rgba(17, 24, 39, 0.85); backdrop-filter: blur(16px); border: 1px solid rgba(255, 255, 255, 0.08); }}
        .glow-purple {{ box-shadow: 0 0 35px -5px rgba(168, 85, 247, 0.35); }}
        
        /* Markdown Article Styling */
        .markdown-body {{ line-height: 1.7; font-size: 0.95rem; color: #cbd5e1; }}
        .markdown-body h1 {{ font-size: 1.85rem; font-weight: 800; color: #f8fafc; margin-top: 1.5rem; margin-bottom: 1rem; border-bottom: 1px solid #1e293b; padding-bottom: 0.5rem; }}
        .markdown-body h2 {{ font-size: 1.4rem; font-weight: 700; color: #f1f5f9; margin-top: 1.75rem; margin-bottom: 0.75rem; border-bottom: 1px solid #1e293b/60; padding-bottom: 0.35rem; }}
        .markdown-body h3 {{ font-size: 1.15rem; font-weight: 700; color: #e2e8f0; margin-top: 1.25rem; margin-bottom: 0.5rem; }}
        .markdown-body p {{ margin-bottom: 1rem; }}
        .markdown-body ul, .markdown-body ol {{ margin-bottom: 1rem; padding-left: 1.5rem; list-style-type: disc; }}
        .markdown-body li {{ margin-bottom: 0.35rem; }}
        .markdown-body code {{ background-color: #1e293b; color: #c084fc; padding: 0.15rem 0.4rem; border-radius: 0.375rem; font-size: 0.85em; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; }}
        .markdown-body pre {{ background-color: #0f172a; border: 1px solid #1e293b; border-radius: 0.75rem; padding: 1rem; margin-bottom: 1.25rem; overflow-x: auto; }}
        .markdown-body pre code {{ background-color: transparent; color: #e2e8f0; padding: 0; font-size: 0.85rem; }}
        .markdown-body table {{ width: 100%; border-collapse: collapse; margin-bottom: 1.5rem; font-size: 0.85rem; }}
        .markdown-body th {{ background-color: #1e293b; color: #f8fafc; font-weight: 700; text-align: left; padding: 0.6rem 0.75rem; border: 1px solid #334155; }}
        .markdown-body td {{ padding: 0.5rem 0.75rem; border: 1px solid #334155; }}
        .markdown-body tr:nth-child(even) {{ background-color: rgba(30, 41, 59, 0.4); }}
        .markdown-body blockquote {{ border-left: 4px solid #a855f7; padding-left: 1rem; margin-left: 0; margin-bottom: 1rem; color: #94a3b8; background-color: rgba(168, 85, 247, 0.05); padding-top: 0.5rem; padding-bottom: 0.5rem; border-radius: 0 0.5rem 0.5rem 0; }}
        .markdown-body a {{ color: #a855f7; text-decoration: underline; }}
        .markdown-body hr {{ border-color: #1e293b; margin: 2rem 0; }}
        .markdown-body img {{ max-width: 100%; border-radius: 0.75rem; border: 1px solid #1e293b; margin: 1rem 0; }}
    </style>
</head>
<body class="min-h-screen flex flex-col antialiased">
    <!-- Header -->
    <header class="glass-panel sticky top-0 z-50 border-b border-slate-800">
        <div class="w-full px-4 sm:px-6 lg:px-8 py-3 flex items-center justify-between">
            <div class="flex items-center gap-3">
                <span class="text-2xl">🦍</span>
                <a href="#/overview" onclick="loadArticle('overview')" class="text-xl font-black bg-clip-text text-transparent bg-gradient-to-r from-purple-400 via-pink-400 to-amber-400">
                    PROJECT KONGOR
                </a>
                <span class="text-xs font-bold px-2.5 py-0.5 rounded-full bg-purple-500/20 text-purple-300 border border-purple-500/30 ml-2 hidden sm:inline">
                    DEVELOPER DOCS HUB
                </span>
            </div>
            <div class="flex items-center gap-4">
                <div class="relative w-72 sm:w-96">
                    <input type="text" id="docSearchInput" placeholder="Search guides, classes, APIs, CLI flags... (Ctrl+K)"
                           class="w-full px-3.5 py-1.5 pl-9 rounded-xl bg-slate-900/90 border border-slate-700 text-xs text-white placeholder-slate-400 focus:outline-none focus:border-purple-500 focus:ring-1 focus:ring-purple-500">
                    <span class="absolute left-3 top-2 text-slate-400 text-xs">🔍</span>
                </div>
                <a href="http://localhost:5200" class="text-xs font-bold text-slate-300 hover:text-white px-3 py-1.5 rounded-lg bg-slate-800 border border-slate-700 hidden md:inline">
                    Web Portal ↗
                </a>
            </div>
        </div>
    </header>

    <!-- App Body: Sidebar + Article Content -->
    <div class="flex-grow flex w-full max-w-[1920px] mx-auto overflow-hidden">
        <!-- Sidebar Navigation -->
        <aside id="sidebar" class="w-72 sm:w-80 flex-shrink-0 bg-slate-950/90 border-r border-slate-800/80 flex flex-col h-[calc(100vh-57px)] sticky top-[57px] overflow-y-auto p-4 space-y-6">
            <!-- Guides Section -->
            <div>
                <h4 class="text-[11px] font-black tracking-wider text-slate-400 uppercase mb-2 px-2">🚀 Getting Started</h4>
                <div class="space-y-0.5">
                    <button onclick="loadArticle('overview')" class="nav-btn w-full text-left px-3 py-2 rounded-xl text-xs font-bold text-slate-300 hover:bg-slate-900 hover:text-white transition-all flex items-center gap-2" data-id="overview">
                        <span>📖</span>
                        <span>Project Overview</span>
                    </button>
                    <button onclick="loadArticle('cli-guide')" class="nav-btn w-full text-left px-3 py-2 rounded-xl text-xs font-bold text-slate-300 hover:bg-slate-900 hover:text-white transition-all flex items-center gap-2" data-id="cli-guide">
                        <span>💻</span>
                        <span>Unified CLI Guide</span>
                    </button>
                    <button onclick="loadArticle('architecture')" class="nav-btn w-full text-left px-3 py-2 rounded-xl text-xs font-bold text-slate-300 hover:bg-slate-900 hover:text-white transition-all flex items-center gap-2" data-id="architecture">
                        <span>🗺️</span>
                        <span>Ecosystem Architecture</span>
                    </button>
                </div>
            </div>

            <!-- Applications Section -->
            <div>
                <h4 class="text-[11px] font-black tracking-wider text-blue-400 uppercase mb-2 px-2">📱 Applications ({len(self.intro.apps)})</h4>
                <div class="space-y-0.5" id="nav-apps"></div>
            </div>

            <!-- Microservices Section -->
            <div>
                <h4 class="text-[11px] font-black tracking-wider text-emerald-400 uppercase mb-2 px-2">☁️ Microservices ({len(self.intro.services)})</h4>
                <div class="space-y-0.5" id="nav-services"></div>
            </div>

            <!-- Developer Tools Section -->
            <div>
                <h4 class="text-[11px] font-black tracking-wider text-amber-400 uppercase mb-2 px-2">🛠️ Developer Tools ({len(self.intro.tools)})</h4>
                <div class="space-y-0.5" id="nav-tools"></div>
            </div>

            <!-- Libraries Section -->
            <div>
                <h4 class="text-[11px] font-black tracking-wider text-purple-400 uppercase mb-2 px-2">💾 Libraries ({len(self.intro.libs)})</h4>
                <div class="space-y-0.5" id="nav-libs"></div>
            </div>
        </aside>

        <!-- Main Article Reader Viewport -->
        <main class="flex-grow p-6 sm:p-10 lg:p-12 overflow-y-auto max-w-5xl">
            <!-- Article Header Badge Bar -->
            <div id="articleMeta" class="mb-6 flex flex-wrap items-center gap-3">
            </div>

            <!-- Rendered Markdown Body -->
            <article id="articleContent" class="markdown-body glass-panel rounded-3xl p-6 sm:p-10 border border-slate-800/80 shadow-2xl">
            </article>
        </main>
    </div>

    <script>
        const articles = {articles_json};
        let currentArticleId = 'overview';

        function buildSidebar() {{
            const appsContainer = document.getElementById('nav-apps');
            const servicesContainer = document.getElementById('nav-services');
            const toolsContainer = document.getElementById('nav-tools');
            const libsContainer = document.getElementById('nav-libs');

            Object.values(articles).forEach(art => {{
                if (art.category === 'Guide') return;

                const btn = document.createElement('button');
                btn.className = 'nav-btn w-full text-left px-3 py-1.5 rounded-xl text-xs text-slate-300 hover:bg-slate-900 hover:text-white transition-all flex items-center justify-between';
                btn.setAttribute('data-id', art.id);
                btn.onclick = () => loadArticle(art.id);

                const icon = art.category === 'Applications' ? '🚀' :
                    art.category === 'Microservices' ? '⚡' :
                    art.category === 'Developer Tools' ? '🔧' : '📦';

                btn.innerHTML = `<span class="truncate">${{icon}} ${{art.title}}</span><span class="text-[10px] text-slate-500 font-mono">${{art.framework}}</span>`;

                if (art.category === 'Applications') appsContainer.appendChild(btn);
                else if (art.category === 'Microservices') servicesContainer.appendChild(btn);
                else if (art.category === 'Developer Tools') toolsContainer.appendChild(btn);
                else if (art.category === 'Libraries') libsContainer.appendChild(btn);
            }});
        }}

        function loadArticle(id) {{
            const art = articles[id];
            if (!art) return;

            currentArticleId = id;
            window.location.hash = '/' + id;

            // Highlight Active Sidebar Button
            document.querySelectorAll('.nav-btn').forEach(btn => {{
                if (btn.getAttribute('data-id') === id) {{
                    btn.classList.add('bg-purple-600', 'text-white', 'font-bold');
                    btn.classList.remove('text-slate-300', 'hover:bg-slate-900');
                    btn.scrollIntoView({{ block: 'nearest' }});
                }} else {{
                    btn.classList.remove('bg-purple-600', 'text-white', 'font-bold');
                    btn.classList.add('text-slate-300', 'hover:bg-slate-900');
                }}
            }});

            // Meta Bar
            const metaContainer = document.getElementById('articleMeta');
            const badgeColor = art.category === 'Microservices' ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/30' :
                art.category === 'Applications' ? 'bg-blue-500/20 text-blue-300 border-blue-500/30' :
                art.category === 'Developer Tools' ? 'bg-amber-500/20 text-amber-300 border-amber-500/30' :
                'bg-purple-500/20 text-purple-300 border-purple-500/30';

            metaContainer.innerHTML = `
                <span class="text-xs font-black px-3 py-1 rounded-full border ${{badgeColor}} uppercase tracking-wider">${{art.category}}</span>
                <span class="text-xs font-mono text-slate-400 bg-slate-900 px-3 py-1 rounded-xl border border-slate-800">📁 ${{art.rel_path}}</span>
                <span class="text-xs font-mono text-purple-300 bg-purple-950/60 px-3 py-1 rounded-xl border border-purple-800/60">${{art.framework}}</span>
            `;

            // Parse Markdown and Render
            const contentContainer = document.getElementById('articleContent');
            contentContainer.innerHTML = marked.parse(art.content);
            window.scrollTo({{ top: 0, behavior: 'smooth' }});
        }}

        // Universal Search Filter (Search content, headings, classes)
        const searchInput = document.getElementById('docSearchInput');
        if (searchInput) {{
            searchInput.addEventListener('input', function(e) {{
                const q = e.target.value.toLowerCase().trim();
                document.querySelectorAll('.nav-btn').forEach(btn => {{
                    const id = btn.getAttribute('data-id');
                    const art = articles[id];
                    if (!art) return;

                    const match = !q || 
                        art.title.toLowerCase().includes(q) || 
                        art.content.toLowerCase().includes(q) ||
                        art.classes.some(c => c.toLowerCase().includes(q)) ||
                        art.rel_path.toLowerCase().includes(q);

                    btn.style.display = match ? 'flex' : 'none';
                }});
            }});

            searchInput.addEventListener('keydown', function(e) {{
                if (e.key === 'Enter') {{
                    const visibleBtn = document.querySelector('.nav-btn:not([style*="display: none"])');
                    if (visibleBtn) {{
                        const id = visibleBtn.getAttribute('data-id');
                        loadArticle(id);
                    }}
                }}
            }});

            document.addEventListener('keydown', function(e) {{
                if ((e.ctrlKey || e.metaKey) && e.key === 'k') {{
                    e.preventDefault();
                    searchInput.focus();
                }}
            }});
        }}

        // Initialize
        buildSidebar();

        // Check URL Hash on Load
        const hash = window.location.hash.replace('#/', '');
        if (hash && articles[hash]) {{
            loadArticle(hash);
        }} else {{
            loadArticle('overview');
        }}
    </script>
</body>
</html>
"""
        with open(os.path.join(self.site_dir, "index.html"), "w", encoding="utf-8") as f:
            f.write(html)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Auto-generate live documentation across all apps, services, and tools")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable verbose logging")
    parser.add_argument("--watch", "-w", action="store_true", help="Watch codebase for changes and re-generate automatically")
    args = parser.parse_args(argv)

    setup_logging(verbose=args.verbose)

    repo_root = _find_repo_root()

    intro = CodebaseIntrospector(repo_root)
    intro.scan_all()

    builder = DocumentationBuilder(intro)
    builder.build_all()

    if args.watch:
        logger.info("Watch mode activated. Monitoring codebase for changes (poll interval: 3s)...")
        try:
            while True:
                time.sleep(3)
                intro.scan_all()
                builder.build_all()
        except KeyboardInterrupt:
            logger.info("Watch mode stopped.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
