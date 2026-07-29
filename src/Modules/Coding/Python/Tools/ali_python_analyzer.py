"""Bounded, dependency-free Python project analysis for Ali."""

from __future__ import annotations

import ast
import json
import os
import sys
from pathlib import Path

MAX_FILES = 5_000
MAX_BYTES = 64 * 1024 * 1024
MAX_FILE_BYTES = 2 * 1024 * 1024
IGNORED = {".git", ".hg", ".svn", ".venv", "venv", "__pycache__", ".mypy_cache", ".pytest_cache", ".ruff_cache", "build", "dist", ".ali"}


def iter_sources(root: Path):
    seen_bytes = 0
    seen_files = 0
    for directory, names, files in os.walk(root, followlinks=False):
        names[:] = [name for name in names if name not in IGNORED and not (Path(directory) / name).is_symlink()]
        for name in sorted(files):
            if not name.endswith((".py", ".pyi")):
                continue
            path = Path(directory) / name
            try:
                size = path.stat().st_size
            except OSError:
                continue
            if size > MAX_FILE_BYTES or seen_files >= MAX_FILES or seen_bytes + size > MAX_BYTES:
                continue
            seen_files += 1
            seen_bytes += size
            yield path


def analyze(root: Path):
    diagnostics = []
    symbols = []
    imports = []
    files = 0
    for path in iter_sources(root):
        files += 1
        relative = path.relative_to(root).as_posix()
        try:
            source = path.read_text(encoding="utf-8-sig")
            tree = ast.parse(source, filename=relative, type_comments=True)
        except (OSError, UnicodeError, SyntaxError) as error:
            diagnostics.append({
                "file": relative,
                "line": getattr(error, "lineno", 0) or 0,
                "column": getattr(error, "offset", 0) or 0,
                "severity": "error",
                "message": str(error),
            })
            continue

        for node in ast.walk(tree):
            if isinstance(node, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
                symbols.append({
                    "file": relative,
                    "line": node.lineno,
                    "kind": type(node).__name__,
                    "name": node.name,
                })
            elif isinstance(node, ast.Import):
                imports.extend({"file": relative, "line": node.lineno, "module": alias.name} for alias in node.names)
            elif isinstance(node, ast.ImportFrom):
                imports.append({"file": relative, "line": node.lineno, "module": node.module or ""})

    return {
        "success": True,
        "filesAnalyzed": files,
        "diagnostics": diagnostics,
        "symbols": symbols,
        "imports": imports,
        "errorCount": sum(item["severity"] == "error" for item in diagnostics),
    }


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    if not root.is_dir():
        print(json.dumps({"success": False, "error": "Project root is not a directory."}))
        return 2
    print(json.dumps(analyze(root), separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
