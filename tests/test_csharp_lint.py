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
    cross_type_private_calls,
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
