#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""针对 Unity C# 代码的「语法合法、语义用错」静态检查。

为什么需要它：本项目的编译错误集中在同一类问题——**调用了不存在的重载/枚举成员**。
这类错误在任何 C# 语法检查里都是"合法代码"，只有真正在 Unity 里编译才会暴露，
而每暴露一次就得多跑一轮"改 → 切回编辑器 → 等编译 → 看 Console"。

目前覆盖的规则全部来自真实踩过的坑（见 packages/com.charforge/CHANGELOG.md）：

  1. FilterMode.Box            —— UnityEngine.FilterMode 只有 Point / Bilinear / Trilinear
  2. new Color32(单个表达式)    —— Color32 没有单参数构造函数（是把数组写成了单个 struct）
  3. SetPixels32(... GetPixels(...)) —— GetPixels 返回 Color[]，SetPixels32 需要 Color32[]
  4. Process.Kill(entireProcessTree: ...) —— 该重载在 .NET Framework 兼容档位下不存在
  5. 裸写 Object.xxx / <Object>  —— 同时 using UnityEngine 与 using System 时会 CS0104 二义

用法：python scripts/check_unity_api_misuse.py
退出码：0 = 通过；1 = 有命中。

注意：本脚本会先剥掉注释再匹配，但不解析字符串字面量——
若某条规则在字符串里出现，会有误报，按需在该行加 `// lint:allow` 即可豁免。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PKG_ROOTS = [ROOT / "packages", ROOT / "Assets"]

# ────────────────────────── 规则表 ──────────────────────────
# (规则名, 正则, 说明, 该行豁免标记)
RULES: list[tuple[str, re.Pattern[str], str]] = [
    (
        "FilterMode.Box",
        re.compile(r"\bFilterMode\s*\.\s*Box\b"),
        "UnityEngine.FilterMode 没有 Box 成员（只有 Point / Bilinear / Trilinear）。"
        "降采样请自行计算盒式平均，不要指望引擎的 filter 模式。",
    ),
    (
        "Color32 单参数构造",
        re.compile(r"\bnew\s+Color32\s*\(\s*[A-Za-z_][\w\.\s\*]*\)"),
        "Color32 没有单参数构造函数（是 (byte r, byte g, byte b, byte a)）。"
        "出现这种写法通常是想写数组：`new Color32[N]`。",
    ),
    (
        "SetPixels32 收到 Color[]",
        re.compile(r"\bSetPixels32\s*\([^;]*?\bGetPixels\s*\(", re.DOTALL),
        "GetPixels 返回 UnityEngine.Color[]，而 SetPixels32 需要 Color32[]。"
        "改用 GetPixels32()，或显式在 Color / Color32 之间转换。",
    ),
    (
        "Process.Kill(entireProcessTree:)",
        re.compile(r"\.\s*Kill\s*\(\s*entireProcessTree"),
        "Process.Kill(bool) 只在 .NET Standard 2.1 / .NET Core 3.0+ 存在，"
        ".NET Framework 档位下会 CS1739。请走 BackendProcessManager.KillProcessTree（反射 + taskkill 回退）。",
    ),
]

# 单独一条：Object 二义性。只在「同时 using UnityEngine 与 using System」的文件里报。
# 匹配一个独立的标识符 Object（排除 ObjectField 这类更长标识符，也排除 System.Object /
# UnityEngine.Object 这种带限定名或别名的情况）。
OBJECT_RE = re.compile(r"(?<![\w\.])Object(?![\w])")
USING_SYSTEM_RE = re.compile(r"^\s*using\s+System\s*;", re.MULTILINE)
USING_UNITY_RE = re.compile(r"^\s*using\s+UnityEngine\s*;", re.MULTILINE)
OBJECT_ALIAS_RE = re.compile(r"^\s*using\s+\w+\s*=\s*(?:UnityEngine|System)\s*\.\s*Object\s*;", re.MULTILINE)
ALLOW_MARK = "lint:allow"


def strip_comments(text: str) -> str:
    """去掉 // 与 /* */ 注释，保留行号（注释内容替换为等长空白）。"""
    out = []
    i, n = 0, len(text)
    in_line = in_block = in_str = False
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""

        if in_line:
            if c == "\n":
                in_line = False
                out.append(c)
            else:
                out.append(" ")
            i += 1
            continue

        if in_block:
            if c == "*" and nxt == "/":
                in_block = False
                out.append("  ")
                i += 2
            else:
                out.append("\n" if c == "\n" else " ")
                i += 1
            continue

        if in_str:
            out.append(c)
            if c == "\\":
                if i + 1 < n:
                    out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue

        if c == "/" and nxt == "/":
            in_line = True
            out.append("  ")
            i += 2
            continue
        if c == "/" and nxt == "*":
            in_block = True
            out.append("  ")
            i += 2
            continue
        if c == '"':
            in_str = True
            out.append(c)
            i += 1
            continue

        out.append(c)
        i += 1
    return "".join(out)


def csharp_files() -> list[Path]:
    files: list[Path] = []
    for base in PKG_ROOTS:
        if base.exists():
            files.extend(sorted(p for p in base.rglob("*.cs") if "obj" not in p.parts))
    return files


def main() -> int:
    findings: list[str] = []
    checked = 0

    files = csharp_files()
    if not files:
        print("未找到 .cs 文件，跳过。")
        return 0

    for path in files:
        checked += 1
        rel = path.relative_to(ROOT).as_posix()
        raw = path.read_text(encoding="utf-8", errors="replace")
        text = strip_comments(raw)
        lines = text.splitlines()

        for name, pattern, hint in RULES:
            for m in pattern.finditer(text):
                line_no = text.count("\n", 0, m.start()) + 1
                source_line = lines[line_no - 1] if line_no - 1 < len(lines) else ""
                if ALLOW_MARK in source_line:
                    continue
                findings.append(f"{rel}:{line_no}  [{name}]  {hint}")

        if USING_SYSTEM_RE.search(text) and USING_UNITY_RE.search(text) and not OBJECT_ALIAS_RE.search(text):
            for m in OBJECT_RE.finditer(text):
                line_no = text.count("\n", 0, m.start()) + 1
                source_line = lines[line_no - 1] if line_no - 1 < len(lines) else ""
                if ALLOW_MARK in source_line:
                    continue
                findings.append(
                    f"{rel}:{line_no}  [Object 二义]  本文件同时 using 了 UnityEngine 与 System，"
                    "裸写 Object 会 CS0104（UnityEngine.Object vs System.Object）。"
                    "请显式写 UnityEngine.Object，或加 using 别名。"
                )

    print(f"检查 {checked} 个 .cs 文件，共发现 {len(findings)} 处问题。")
    for f in findings:
        print("  " + f)

    if findings:
        print("\n提示：确认是误报时，在该行加 '// lint:allow' 豁免。")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
