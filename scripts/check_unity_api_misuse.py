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
  6. 跨顶级类型裸调 private/protected 成员 —— CS0103，且报错文案会误导
  7. {} / () / [] 不配平 —— 手改代码误删括号，Unity 报在文件末尾，定位成本高

第 6 条与前面几条不同：它不是「API 用错」，而是**作用域用错**。
C# 的访问控制在*类型*一级生效，private 成员只对其声明类型（及其嵌套类型）可见。
若顶级类型 B 里裸写 `Helper(...)`，而 Helper 是顶级类型 A 的 private 方法，
编译器报的是 CS0103「名称不存在」，而不是「private 不可访问」——
因为简单名查找压根不会去兄弟类型里找。这个文案极具误导性：
看起来像「方法写了忘记保存 / 文件没编译」，实际是**可见性**问题。
本规则用行扫描状态机实现（不依赖括号配对），依赖本仓库的缩进约定：
顶级类型声明 4 空格、直接成员 8 空格。

第 7 条是结构检查，与语义无关：只要有一次手改代码时误删或多加一个括号，
在 Unity 里的报错位置往往离真正出错的地方很远。在本地拦下来成本极低。

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

# ── 规则 6：跨顶级类型裸调非 public 成员 ──
# 顶级类型声明：恰好 4 空格缩进。
TOP_TYPE_RE = re.compile(
    r"^    (?:(?:public|internal|private|protected|static|sealed|abstract|partial)\s+)*"
    r"(?:class|struct|interface|enum)\s+([A-Za-z_]\w*)"
)
# 直接成员：恰好 8 空格缩进，访问修饰符为 private / protected，且形态是方法。
PRIV_MEMBER_RE = re.compile(r"^        (private|protected)\b[^\n=;]*?\b([A-Za-z_]\w*)\s*\(")
# 任意直接成员声明（8 空格缩进），用于判断「调用方自己有没有声明这个名字」。
# 同名遮蔽是合法的：A 和 B 各有 private Helper()，各自调用自己的，互不相干。
MEMBER_DECL_RE = re.compile(
    r"^        (?:(?:public|private|protected|internal|static|readonly|const|sealed|override"
    r"|virtual|abstract|async|partial|extern|new|unsafe|event|volatile)\s+)*"
    r"[\w<>\[\],\.\?]+\s+([A-Za-z_]\w*)\s*[=;({]"
)


def cross_type_private_calls(lines: list[str]) -> list[tuple[int, str, str, int]]:
    """找出「在顶级类型 B 里裸调用顶级类型 A 的 private/protected 方法」。

    返回 [(行号, 被调方法名, 声明类型, 声明行号), ...]，按位置排序。
    跳过同类型内调用（含其嵌套类型），也跳过调用方自己声明了同名成员的情况。
    """
    # 第一遍：登记每个类型「私有成员」与「已声明的成员名」
    priv_of: dict[str, dict[str, int]] = {}   # 类型 -> {私有成员名: 声明行}
    declared: dict[str, set[str]] = {}        # 类型 -> 该类型声明过的全部成员名
    current: str | None = None
    for i, line in enumerate(lines, 1):
        m = TOP_TYPE_RE.match(line)
        if m:
            current = m.group(1)
            priv_of.setdefault(current, {})
            declared.setdefault(current, set())
            continue
        if current is None:
            continue
        m = MEMBER_DECL_RE.match(line)
        if m:
            declared[current].add(m.group(1))
        m = PRIV_MEMBER_RE.match(line)
        if m:
            priv_of[current].setdefault(m.group(2), i)

    if not any(priv_of.values()):
        return []

    # 第二遍：找出现在别的类型里的裸调用
    hits: dict[tuple[int, str], tuple[int, str, str, int]] = {}
    current = None
    for i, line in enumerate(lines, 1):
        m = TOP_TYPE_RE.match(line)
        if m:
            current = m.group(1)
            continue
        if current is None or PRIV_MEMBER_RE.match(line) or ALLOW_MARK in line:
            continue
        mine = declared.get(current, set())
        for decl_type, members in priv_of.items():
            if decl_type == current:          # 同类型内调用，合法
                continue
            for name, decl_line in members.items():
                if name in mine:              # 自己声明了同名成员，遮蔽，合法
                    continue
                if re.search(r"(?<![\w.])" + re.escape(name) + r"\s*\(", line):
                    hits.setdefault((i, name), (i, name, decl_type, decl_line))
    return sorted(hits.values())


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


def blank_strings(text: str) -> str:
    """把字符串字面量的内容替换成空白（保留长度与换行）。

    strip_comments 会保留字符串内容（对正则匹配是必要的），但做**结构**分析时
    字符串里的引号、括号、花括号会干扰判断。两者用途不同，别混用。
    """
    out = []
    i, n = 0, len(text)
    quote = None
    while i < n:
        c = text[i]
        if quote:
            if c == "\\" and i + 1 < n:
                out.append("  ")
                i += 2
                continue
            if c == quote:
                quote = None
                out.append(c)
            else:
                out.append("\n" if c == "\n" else " ")
            i += 1
            continue
        if c in "\"'":
            quote = c
            out.append(c)
            i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def unbalanced_delimiters(text: str) -> list[str]:
    """检查 {} / () / [] 是否配平。

    纯手写 C# 时最容易犯的错就是手滑删/多一个括号，而这类错误在 Unity 里的报错
    位置往往离真正出错的地方很远（常见的是「文件末尾缺少 }」）。在本地拦下来便宜得多。
    """
    blanked = blank_strings(text)
    problems: list[str] = []
    for label, opener, closer in (
        ("大括号", "{", "}"),
        ("圆括号", "(", ")"),
        ("方括号", "[", "]"),
    ):
        a, b = blanked.count(opener), blanked.count(closer)
        if a != b:
            problems.append(f"{label}不配平（{opener} × {a}，{closer} × {b}）")
    return problems


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

        for problem in unbalanced_delimiters(text):
            findings.append(
                f"{rel}  [括号不配平]  {problem}。"
                "多为手改代码时误删/误加；Unity 里通常报在文件末尾（「缺少 }」），离真正出错处很远。"
            )

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

        for line_no, name, decl_type, decl_line in cross_type_private_calls(lines):
            findings.append(
                f"{rel}:{line_no}  [跨类型调非 public 成员]  {name}() 是 {decl_type} 的 private/protected 成员"
                f"（声明于本文件第 {decl_line} 行），却被另一个顶级类型裸调。"
                f"C# 会报 CS0103「名称不存在」（而非「private 不可访问」），因为简单名查找不去兄弟类型里找。"
                f"改为 {decl_type}.{name}(...) 并把它提升为 public，或把该方法挪到调用方类型内。"
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
