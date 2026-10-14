"""C# 静态检查的单元测试。

`scripts/check_unity_api_misuse.py` 是 C# 侧唯一能在无 Unity 环境下跑的验证，
它自己也需要被测——否则一条写歪的规则要么漏报（等于没有），要么误报（没人再信它）。

重点测规则 6（跨顶级类型裸调 private/protected 成员）：它能命中，也必须能不误报。
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

from check_unity_api_misuse import (  # noqa: E402
    blank_strings,
    cross_type_private_calls,
    naked_nested_type_uses,
    nested_type_decls,
    nested_type_decls_in_text,
    strip_comments,
    unbalanced_delimiters,
)

# ── 真实踩过的坑：v0.2.3 的 WithWarnings ──
# PipelineResult 上的 private static 辅助方法，被 CharacterForgePipeline 裸调，
# Unity 报 CS0103「The name 'WithWarnings' does not exist in the current context」——
# 文案看起来像方法没写，实际是可见性问题（简单名查找不去兄弟类型里找）。
BUGGY = """
namespace N
{
    public class Dto
    {
        public static Dto Fail(string m) => new Dto();

        private static Dto WithWarnings(Dto r, List<string> w)
        {
            return r;
        }
    }

    public static class Pipeline
    {
        public static Dto Run()
        {
            return WithWarnings(new Dto(), null);
        }
    }
}
"""

FIXED = """
namespace N
{
    public class Dto
    {
        public static Dto Fail(string m) => new Dto();

        public static Dto WithWarnings(Dto r, List<string> w)
        {
            return r;
        }
    }

    public static class Pipeline
    {
        public static Dto Run()
        {
            return Dto.WithWarnings(new Dto(), null);
        }
    }
}
"""

# 同一类型内调用自己的 private 方法，必须不报。
SAME_TYPE_OK = """
namespace N
{
    public class OnlyOne
    {
        private static int Helper() => 1;

        public static int Use()
        {
            return Helper();
        }
    }
}
"""

# 名字相同但各自私有，互不干扰，必须不报。
SHADOWED_OK = """
namespace N
{
    public class A
    {
        private static int Helper() => 1;
        public static int UseA() => Helper();
    }

    public class B
    {
        private static int Helper() => 2;
        public static int UseB() => Helper();
    }
}
"""


def _lines(src: str) -> list[str]:
    return src.splitlines()


def test_catches_cross_type_private_call():
    """能用行号精确定位跨类型裸调。"""
    hits = cross_type_private_calls(_lines(BUGGY))
    assert len(hits) == 1, hits
    line_no, name, decl_type, decl_line = hits[0]
    assert name == "WithWarnings"
    assert decl_type == "Dto"
    assert decl_line < line_no, "声明行应早于调用行"


def test_no_false_positive_when_qualified():
    """加上类型限定并提升可见性后，不再命中。"""
    assert cross_type_private_calls(_lines(FIXED)) == []


def test_same_type_call_is_legal():
    """同类内调用自己的 private 方法完全合法。"""
    assert cross_type_private_calls(_lines(SAME_TYPE_OK)) == []


def test_same_named_private_members_do_not_cross_talk():
    """两个类型各有同名 private 方法，各自调用，不应互相误报。"""
    assert cross_type_private_calls(_lines(SHADOWED_OK)) == []


def test_repository_itself_is_clean():
    """真实仓库不允许存在这类问题——这是这条规则存在的意义。"""
    findings: list[str] = []
    for path in sorted((ROOT / "packages").rglob("*.cs")):
        if "obj" in path.parts:
            continue
        hits = cross_type_private_calls(
            path.read_text(encoding="utf-8", errors="replace").splitlines()
        )
        findings.extend(f"{path.name}:{ln} {name} ({decl_type})" for ln, name, decl_type, _ in hits)
    assert findings == [], "跨类型调用非 public 成员：\n" + "\n".join(findings)


# ── 规则 7：括号不配平 ──
# 手改代码时误删/误加括号，在 Unity 里报在文件末尾，定位成本很高。
# 这条检查是结构性的，不涉及语义，因此必须做到零误报。


def test_balanced_code_passes():
    assert unbalanced_delimiters("class A { void M() { int[] xs = new int[2]; } }") == []


def test_missing_brace_is_caught():
    problems = unbalanced_delimiters("class A { void M() { } ")
    assert len(problems) == 1
    assert "大括号" in problems[0]


def test_braces_inside_strings_are_ignored():
    """C# 的插值字符串与 JSON 字面量里全是花括号，不能算进去。"""
    assert unbalanced_delimiters('class A { string s = "{{ \\"x\\": 1 }}"; }') == []
    assert unbalanced_delimiters('class A { string s = $"{a}({b})"; }') == []


def test_repository_delimiters_balanced():
    findings: list[str] = []
    for path in sorted((ROOT / "packages").rglob("*.cs")):
        if "obj" in path.parts:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        findings.extend(f"{path.name}: {p}" for p in unbalanced_delimiters(text))
    assert findings == [], "括号不配平：\n" + "\n".join(findings)


# ── 规则 10：跨文件裸用嵌套类型 ──
# 真实踩坑（v0.4.3）：ForgeArchive.cs 写了 `ForgeResult result`，而 ForgeResult 是
# PromptForgeService 的嵌套类。Unity 报 CS0246「找不到类型或命名空间」——
# 文案看着像「文件没保存 / 没编译」，极易白跑一轮「改 → 切回 Unity → 等编译」。
#
# 这条规则靠**缩进**区分「嵌套类型」与「命名空间下的兄弟类型」：本项目约定
# 顶级类型 4 空格、直接成员 8 空格；兄弟类型裸用是合法的，绝不能误报。

DECL_SRC = """
namespace N
{
    public static class Holder
    {
        public class Inner
        {
        }

        public enum Kind
        {
            A,
            B
        }

        private void M()
        {
        }
    }

    public class Sibling
    {
    }
}
"""

# 逐行写，行号可数：3 / 4 / 5 命中，6～9 是不该命中的反例。
USE_SRC = (
    "class C\n"                                             # 1
    "{\n"                                                   # 2
    "    void A(Inner x) { }\n"                             # 3  命中：类型 + 形参名
    "    void B() { var a = new Inner(); }\n"               # 4  命中：new
    "    List<Inner> C2() { return null; }\n"               # 5  命中：泛型实参
    "    void D(Holder.Inner x) { }\n"                      # 6  反例：已限定外层
    "    void E() { PortraitResult p = null; }\n"           # 7  反例：词内子串
    "    void F() { int Inner = 0; }\n"                     # 8  反例：撞名的局部变量
    "    void G() { var m = Kind.A; }\n"                    # 9  反例：枚举成员访问
    "}\n"
)


def test_nested_decls_keep_members_and_drop_siblings():
    """8 空格 = 嵌套成员（收）；4 空格 = 兄弟类型（不收）。"""
    decls = nested_type_decls_in_text(DECL_SRC)
    assert set(decls) == {"Inner", "Kind"}, decls
    assert decls["Inner"] == [(6, "Holder")], decls["Inner"]
    assert decls["Kind"] == [(10, "Holder")], decls["Kind"]


def test_method_body_types_are_ignored():
    """方法体内的局部类型缩进更深，不该被当成嵌套成员。"""
    src = (
        "namespace N\n"
        "{\n"
        "    public class Outer\n"
        "    {\n"
        "        public void M()\n"
        "        {\n"
        "            void Local() { }\n"
        "        }\n"
        "    }\n"
        "}\n"
    )
    assert nested_type_decls_in_text(src) == {}


def test_naked_nested_type_use_is_caught():
    """三种类型位置都要抓到，且行号准确。"""
    nested = {"Inner": [("decl.cs", 6, "Holder")]}
    hits = naked_nested_type_uses(blank_strings(USE_SRC), nested, "use.cs")
    assert sorted(ln for ln, _, _ in hits) == [3, 4, 5], hits
    assert {name for _, name, _ in hits} == {"Inner"}


def test_no_false_positive_variants():
    """限定写法 / 词内子串 / 撞名变量 / 枚举成员 / 字符串内容，一个都不能误报。"""
    nested = {
        "Inner": [("decl.cs", 6, "Holder")],
        "Kind": [("decl.cs", 10, "Holder")],
        "Result": [("decl.cs", 1, "Holder")],
    }
    lines = USE_SRC.splitlines()
    for line_no in (6, 7, 8, 9):
        probe = "\n" + lines[line_no - 1]
        assert naked_nested_type_uses(blank_strings(probe), nested, "use.cs") == [], lines[line_no - 1]
    assert naked_nested_type_uses(blank_strings('string s = "Inner Result Kind";'), nested, "use.cs") == []


def test_same_file_declaration_is_legal():
    """本文件自己声明的嵌套类型，在本文件内裸用完全合法——不能报。"""
    nested = {"Inner": [("use.cs", 6, "Holder")]}
    assert naked_nested_type_uses(blank_strings(USE_SRC), nested, "use.cs") == []


def test_repository_has_no_naked_nested_type_use():
    """真实仓库不允许存在这类问题——这是这条规则存在的意义。"""
    files = [p for p in sorted((ROOT / "packages").rglob("*.cs")) if "obj" not in p.parts]
    nested = nested_type_decls(files)
    findings: list[str] = []
    for path in files:
        rel = path.relative_to(ROOT).as_posix()
        text = blank_strings(strip_comments(path.read_text(encoding="utf-8", errors="replace")))
        findings.extend(f"{rel}:{ln} {name}" for ln, name, _ in naked_nested_type_uses(text, nested, rel))
    assert findings == [], "跨文件裸用嵌套类型：\n" + "\n".join(findings)
