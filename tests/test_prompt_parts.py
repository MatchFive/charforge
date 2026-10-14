"""立绘提示词固定件校验器的单元测试。

**为什么这个校验需要测试**：它守的是一个"三份常量必须一致"的约束，而三份里有两份
不在 Python 里（C# 源码、工作流 JSON）。所以校验器本身要能读对 C# 的多行字符串拼接
（`"a" + "b"`）——读错了会得到一个"永远通过"的绿灯，比没有校验更糟。
下面第二个用例就是专门盯这件事的：**故意把一处改坏，校验必须红**。

第三个用例守的是仓库当前状态——三处真的对齐。它失败时不要改测试，去改被改坏的那一处。
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import check_prompt_parts as cpp  # noqa: E402


@pytest.fixture
def captured(monkeypatch):
    """把校验器的模块级 `errors` 换成一次性列表，避免用例之间互相污染。"""
    sink: list[str] = []
    monkeypatch.setattr(cpp, "errors", sink)
    return sink


def test_parse_csharp_consts_joins_literals_and_unescapes():
    source = (
        '        public const string QwenAlphaHint =\n'
        '            "This is an RGBA image with transparency. " +\n'
        '            "The image has alpha channel and the background is transparent.";\n'
    )
    consts = cpp.parse_csharp_consts(source)
    assert consts["QwenAlphaHint"] == (
        "This is an RGBA image with transparency. "
        "The image has alpha channel and the background is transparent."
    )


def test_parse_ignores_private_consts():
    """`private const string SystemPrompt` 里带 `;` 与 `\\n`，混进来会把边界搞乱。

    它必须以同名的 private 出现才被排除；`public` 才算契约的一部分。
    """
    source = (
        '        private const string SystemPrompt =\n'
        '            "只输出提示词；不要解释。\\n" +\n'
        '            "不要分点。";\n'
        '        public const string LayoutSuffix = "全身立绘。";\n'
    )
    consts = cpp.parse_csharp_consts(source)
    assert consts == {"LayoutSuffix": "全身立绘。"}


def test_parse_unescapes_quotes_and_newlines():
    consts = cpp.parse_csharp_consts('public const string X = "a\\"b\\nc";')
    assert consts["X"] == 'a"b\nc'


def test_csharp_drift_is_caught(captured):
    """C# 常量与 bindings 不一致时必须报错——这是本校验器存在的理由。"""
    parts = {"alpha_hint": "A", "style_prefix": "B", "layout_suffix": "C"}
    csharp = {"QwenAlphaHint": "A", "StylePrefix": "B 改坏了", "LayoutSuffix": "C"}

    cpp.check_csharp_matches_bindings(csharp, parts)

    assert len(captured) == 1
    assert "StylePrefix" in captured[0]
    assert "style_prefix" in captured[0]


def test_missing_binding_key_is_reported_once(captured):
    """缺键由 check_parts_complete 报，逐字段比较不该再刷一遍同样的错。"""
    parts = {"alpha_hint": "A", "layout_suffix": "C"}
    csharp = {"QwenAlphaHint": "A", "StylePrefix": "B", "LayoutSuffix": "C"}

    cpp.check_csharp_matches_bindings(csharp, parts)

    assert captured == []


def test_workflow_must_be_wrapped_by_fixed_parts(captured):
    parts = {"alpha_hint": "ALPHA.", "style_prefix": "STYLE。", "layout_suffix": "全身立绘。"}

    # 正确形态：固定件 + 一段样例角色本体 + 版式后缀
    cpp.check_workflow_wraps_parts("ALPHA. STYLE。一位讲解员，手持讲解册。全身立绘。", parts)
    assert captured == []

    # 被整体覆盖过：只剩角色本体
    cpp.check_workflow_wraps_parts("一位讲解员，手持讲解册。", parts)
    assert len(captured) == 2, captured


def test_parts_complete_rejects_empty(captured):
    assert cpp.check_parts_complete({"alpha_hint": "A"}) is False
    assert "缺字段" in captured[0]


def test_repo_is_aligned():
    """仓库当前状态：C# ←→ bindings ←→ 工作流 三处一致。

    这条失败时不要改测试——去改被改坏的那一处（`python scripts/check_prompt_parts.py`
    会打印具体是哪一处、差在哪个字）。
    """
    bindings = cpp.load_bindings()
    parts = bindings["prompt_parts"]
    csharp = cpp.parse_csharp_consts(cpp.SERVICE.read_text(encoding="utf-8"))

    errors: list[str] = []
    saved = cpp.errors
    cpp.errors = errors
    try:
        cpp.check_csharp_matches_bindings(csharp, parts)
        cpp.check_workflow_wraps_parts(cpp.load_workflow_positive(bindings), parts)
    finally:
        cpp.errors = saved

    assert errors == []


def test_repo_parts_are_actually_used_by_composition():
    """三份常量都非空，且拼出来的提示词确实以透明底句开头。

    只校验"三处一致"是不够的：三处可以一起空着。这里顺手守住"内容本身是对的"。
    """
    parts = cpp.load_bindings()["prompt_parts"]
    assert parts["alpha_hint"].startswith("This is an RGBA image with transparency.")
    assert parts["layout_suffix"].startswith("全身立绘")
    assert "厚涂" in parts["style_prefix"], "画风前缀应当说明画风，否则立绘没有风格锚点"

    composed = f"{parts['alpha_hint']} {parts['style_prefix']}某角色。{parts['layout_suffix']}"
    assert composed.startswith(parts["alpha_hint"])
    assert composed.endswith(parts["layout_suffix"])
