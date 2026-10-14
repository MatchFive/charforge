#!/usr/bin/env python3
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
  8. 把静态 API 当实例方法调 —— 如 clip.GetEditorCurve(...)，CS1061。
     与第 1~4 条同源：记忆里的方法名对了，但**接收者/归属搞错**。
  9. 同族编号 helper 缺定义 —— 如 Set1/Set2/Set3 都在，独缺 Set4，CS0103 × N。
 10. 跨文件裸用嵌套类型 —— 如 `ForgeResult result`（实为 `PromptForgeService.ForgeResult`），CS0246。

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

第 8 条来自真实踩坑：`AnimationUtility.GetEditorCurve` / `SetEditorCurve` /
`GetCurveBindings` 都是**静态**方法，有人（包括本仓库）写成
`fresh.GetEditorCurve(binding)`——方法名记对了，接收者写错了，
于是 CS1061「AnimationClip 不含 GetEditorCurve」。这类「静态当实例」的错在
C# 里非常常见（String.Format、Mathf、AnimationUtility、AssetDatabase 都容易中）。
本规则维护一张「只以静态形式存在的方法名 → 正确所有者」的表，凡是
`其他标识符.该方法名(` 一律报出。

第 10 条与第 6 条是同一个大类的两面：都是**「简单名在这个位置查不到」**。
C# 里嵌套类型在其外层类型之外必须写全限定名（`Outer.Inner`），同命名空间下
裸写简单名会 CS0246「找不到类型或命名空间」。这条报错同样极具误导性——
看着像「文件没保存 / 没编译」。规则靠缩进区分「嵌套类型（8 空格）」与
「命名空间下的兄弟类型（4 空格）」：后者裸用合法，绝不能误报。

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

# ── 规则 8：静态方法被当作实例方法调 ──
# (方法名, 正确所有者, 补充说明)。这里只登记「在 Unity API 里**只**以静态形式存在」的
# 方法名——名字独特、几乎不会与用户自定义方法撞名，误报率低。
STATIC_ONLY_METHODS: list[tuple[str, str, str]] = [
    (
        "GetEditorCurve", "UnityEditor.AnimationUtility",
        "GetEditorCurve 是 AnimationUtility 的静态方法，AnimationClip 上没有这个成员。"
        "正确写法：AnimationUtility.GetEditorCurve(clip, binding)。",
    ),
    (
        "SetEditorCurve", "UnityEditor.AnimationUtility",
        "SetEditorCurve 是 AnimationUtility 的静态方法。"
        "正确写法：AnimationUtility.SetEditorCurve(clip, binding, curve)。",
    ),
    (
        "GetCurveBindings", "UnityEditor.AnimationUtility",
        "GetCurveBindings 是 AnimationUtility 的静态方法。"
        "正确写法：AnimationUtility.GetCurveBindings(clip)。",
    ),
    (
        "GetObjectReferenceCurve", "UnityEditor.AnimationUtility",
        "GetObjectReferenceCurve 是 AnimationUtility 的静态方法。",
    ),
    (
        "SetObjectReferenceCurve", "UnityEditor.AnimationUtility",
        "SetObjectReferenceCurve 是 AnimationUtility 的静态方法。",
    ),
    (
        "GetAnimationClipSettings", "UnityEditor.AnimationUtility",
        "GetAnimationClipSettings 是 AnimationUtility 的静态方法。",
    ),
    (
        "SetAnimationClipSettings", "UnityEditor.AnimationUtility",
        "SetAnimationClipSettings 是 AnimationUtility 的静态方法。",
    ),
]


def static_called_on_instance(text: str) -> list[tuple[int, str, str]]:
    """找出 `某个标识符.<静态方法名>(` 的写法（接收者不是正确所有者）。

    返回 [(行号, 正确所有者, 说明), ...]。
    """
    hits: list[tuple[int, str, str]] = []
    for method, owner, hint in STATIC_ONLY_METHODS:
        simple_owner = owner.rsplit(".", 1)[-1]
        pattern = re.compile(
            r"(?<![\w])(?!" + re.escape(simple_owner) + r"\b)"
            r"[A-Za-z_]\w*\s*\.\s*" + re.escape(method) + r"\s*(?:<[^>]*>)?\s*\("
        )
        for m in pattern.finditer(text):
            line_no = text.count("\n", 0, m.start()) + 1
            hits.append((line_no, owner, hint))
    return sorted(hits)

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


def missing_numbered_helpers(text: str) -> list[tuple[int, str, str, list[str]]]:
    """检查「同族编号 helper 缺定义」—— 如声明了 Set1/Set2/Set3，却调用 Set4。

    来自真实踩坑（CharForge 0.3.9 → 0.3.10）：给向导加第 4 区时新增了 5 处
    `Set4(...)` 调用，却忘记补上 `private void Set4(string s) => _status4.text = s;`，
    于是 CS0103 × 5。这类错误的特点是**编译器能报、但报的是「名称不存在」**，
    极易被误认为「文件没保存 / 没编译」，于是白跑一轮「改 → 切回 Unity → 等编译」。

    识别方式（只认编号名，最大化精确度）：
      * 声明 = 一行里「修饰符序列 + 返回类型 + 名字 + (」，取名字；
      * 调用 = 名字以**数字结尾**且不是成员访问（`x.Foo1(`）、不是构造（`new Foo1(`）；
      * 若某调用名字本文件无声明，但存在**同前缀（数字前那段字母）且 ≥2 个**的已声明编号方法，
        则判定为「族内缺号」——这正是 Set1/2/3 齐、独缺 Set4 的形态。
    只认数字结尾的名字，因此不会把 Vector3 / Color32 / Texture2D 这类类型名误报进来。
    """
    decl_re = re.compile(
        r"^[ \t]*(?:\[[^\]]*\][ \t]*)*"
        r"(?:(?:public|private|protected|internal|static|sealed|override|virtual|abstract|partial|extern|unsafe|new|async|readonly)[ \t]+)+"
        r"(?:[\w<>\[\],\.\?]+[ \t]+)+"
        r"([A-Za-z_]\w*)[ \t]*(?:<[^<>()]*>)?[ \t]*\(",
        re.MULTILINE,
    )
    declared_all = {m.group(1) for m in decl_re.finditer(text)}

    numbered = re.compile(r"^([A-Za-z_]+)\d+$")
    families: dict[str, set[str]] = {}
    for name in declared_all:
        m = numbered.match(name)
        if m:
            families.setdefault(m.group(1), set()).add(name)

    call_re = re.compile(r"(?<![\w.])([A-Za-z_]\w*\d+)[ \t]*\(")
    findings: list[tuple[int, str, str, list[str]]] = []
    for m in call_re.finditer(text):
        name = m.group(1)
        if name in declared_all:
            continue
        head = text[: m.start()].rstrip()
        if head.endswith("new"):          # 构造调用
            continue
        fm = numbered.match(name)
        if not fm:
            continue
        siblings = families.get(fm.group(1))
        if not siblings or len(siblings) < 2:
            continue
        line_no = text.count("\n", 0, m.start()) + 1
        findings.append((line_no, name, fm.group(1), sorted(siblings)))
    return findings


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


def nested_type_decls_in_text(text: str) -> dict[str, list[tuple[int, str]]]:
    """从一份源码文本里抽出**嵌套类型**：{简单名: [(行号, 外层类型名), ...]}。

    判定靠缩进：缩进恰好 8 空格 = 外层的直接成员（本项目的缩进硬约定：
    顶级类型 4 空格、直接成员 8 空格）。缩进 4 空格是命名空间下的兄弟类型——
    那种裸用是合法的，不能混进来。缩进 ≥ 12 是方法体内的局部类型，也不收。
    """
    decl = re.compile(
        r"^(?:public|internal|private|protected)?[ \t]*"
        r"(?:static[ \t]+|abstract[ \t]+|sealed[ \t]+|partial[ \t]+|readonly[ \t]+|unsafe[ \t]+|new[ \t]+)*"
        r"(?:class|struct|enum|interface)[ \t]+([A-Za-z_]\w*)"
    )
    found: dict[str, list[tuple[int, str]]] = {}
    outer = None
    for i, line in enumerate(text.splitlines(), 1):
        body = line.lstrip(" ")
        m = decl.match(body)
        if not m:
            continue
        indent = len(line) - len(body)
        if indent == 4:
            outer = m.group(1)
        elif indent == 8:
            found.setdefault(m.group(1), []).append((i, outer))
    return found


def nested_type_decls(files: list[Path]) -> dict[str, list[tuple[str, int, str]]]:
    """扫全部文件，汇总嵌套类型声明：{简单名: [(相对路径, 行号, 外层类型名), ...]}。

    为什么要这个：嵌套类型在其**外层类型之外**必须写全限定名（`Outer.Inner`）。
    同一命名空间下裸写简单名会 CS0246「找不到类型或命名空间」——而这条报错
    看起来特别像「文件没保存 / 没编译」，极易白跑一轮「改 → 切回 Unity → 等编译」。
    CharForge 0.4.3 就栽在这上面：`ForgeArchive.WriteForge(..., ForgeResult result, ...)`
    里的 `ForgeResult` 其实是 `PromptForgeService` 的嵌套类。
    """
    found: dict[str, list[tuple[str, int, str]]] = {}
    for path in files:
        rel = path.relative_to(ROOT).as_posix()
        text = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
        for name, entries in nested_type_decls_in_text(text).items():
            for line_no, outer in entries:
                found.setdefault(name, []).append((rel, line_no, outer))
    return found


def naked_nested_type_uses(
    text: str, nested: dict[str, list[tuple[str, int, str]]], self_rel: str
) -> list[tuple[int, str, list[tuple[str, int, str]]]]:
    """本文件裸用**别的文件里**声明的嵌套类型简单名（正确写法是 `Outer.Inner`）。

    只认「类型位置」，避免把同名的字段 / 属性 / 枚举成员误判为类型引用：
      * `new Inner(`            —— 构造
      * `Inner xxx`             —— 类型 + 变量名 / 返回类型 + 方法名
      * `List<Inner>`           —— 泛型实参

    三条都用 `[ \\t]` 而非 `\\s`：注释已被剥成空白，允许跨行空白会把
    「上一行以名字结尾、下一行以标识符开头」也算进来。
    前置 `(?<![\\w.])` 排除 `Outer.Inner`（限定写法，合法）与词内子串。
    """
    hit: list[tuple[int, str, list[tuple[str, int, str]]]] = []
    for name, decls in nested.items():
        if any(rel == self_rel for rel, _, _ in decls):
            continue                        # 本文件声明的：在本文件内裸用合法
        patterns = (
            re.compile(r"(?<![\w.])new[ \t]+" + re.escape(name) + r"\b"),
            re.compile(r"(?<![\w.])" + re.escape(name) + r"[ \t]+[A-Za-z_]\w*"),
            re.compile(r"<[ \t]*(?:[A-Za-z_][\w.\[\]][ \t]*,[ \t]*)*" + re.escape(name) + r"[ \t]*(?:,|>)"),
        )
        for pattern in patterns:
            for m in pattern.finditer(text):
                hit.append((text.count("\n", 0, m.start()) + 1, name, decls))

    dedup: dict[tuple[int, str], tuple[int, str, list[tuple[str, int, str]]]] = {}
    for line_no, name, decls in hit:
        dedup.setdefault((line_no, name), (line_no, name, decls))
    return sorted(dedup.values())


def main() -> int:
    findings: list[str] = []
    checked = 0

    files = csharp_files()
    if not files:
        print("未找到 .cs 文件，跳过。")
        return 0

    nested = nested_type_decls(files)

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

        for line_no, name, prefix, siblings in missing_numbered_helpers(text):
            source_line = lines[line_no - 1] if line_no - 1 < len(lines) else ""
            if ALLOW_MARK in source_line:
                continue
            findings.append(
                f"{rel}:{line_no}  [编号 helper 缺定义]  调用了 {name}()，但本文件没有它的声明；"
                f"同族已声明的是 {', '.join(siblings)}（前缀 {prefix}）。"
                f"C# 报 CS0103「名称不存在」——多为新增调用时忘了补方法体。"
                f"补齐 `private void {name}(...)` 即可。"
            )

        for line_no, name, decls in naked_nested_type_uses(blank_strings(text), nested, rel):
            source_line = lines[line_no - 1] if line_no - 1 < len(lines) else ""
            if ALLOW_MARK in source_line:
                continue
            owner = decls[0][2] or "（未识别外层）"
            findings.append(
                f"{rel}:{line_no}  [跨文件裸用嵌套类型]  {name} 是 {owner} 的嵌套类型"
                f"（声明于 {decls[0][0]}:{decls[0][1]}），在 {owner} 之外必须写 {owner}.{name}。"
                f"C# 报 CS0246「找不到类型或命名空间」——看着像「文件没保存」，容易白跑一轮编译。"
            )

        for line_no, owner, hint in static_called_on_instance(text):
            source_line = lines[line_no - 1] if line_no - 1 < len(lines) else ""
            if ALLOW_MARK in source_line:
                continue
            findings.append(
                f"{rel}:{line_no}  [{owner} 的静态方法被当实例方法调]  {hint}"
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
