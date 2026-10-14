"""立绘提示词「固定件」三处对齐校验（CI 用，不依赖 Unity）。

**为什么要专门写一个校验器**：立绘的最终提示词是

    alpha_hint + " " + style_prefix + 角色本体 + "。" + layout_suffix

这三段固定件在仓库里存在**三份**，而且各有各的用途，谁也替不了谁：

| 位置 | 谁读它 | 为什么不能删 |
|---|---|---|
| `PromptForgeService.cs` 的三个 const | 插件窗口 | C# 那边是编译期常量，跨进程拿不到 |
| `Configs/comfyui/*.bindings.json` 的 `prompt_parts` | npcforge Agent / 脚本 / CI | 外部下游不能引用 C# 常量，只能读数据文件 |
| 工作流节点 6 的默认提示词 | ComfyUI | 直接提交工作流时（不经插件）给模型看的样例 |

三份里任何一份被改而其余没跟上，症状都是**同一个**：输出一张实心底、没有 alpha 的图，
而且**不报错**。再往下 `FrameNormalizer` 按整张图算包围盒，一批帧的缩放基准全错——
要到质检那一步才显形，中间已经烧掉一次立绘 + 一组帧的算力。

这类"改一处、忘一处"的缺陷光靠注释是拦不住的（上一条注释就写在被漏掉的那处旁边）。
所以让代码去算。

校验：
1. C# 三个 const 的字符串值 与 bindings 的 `prompt_parts` 完全一致；
2. 工作流正节点的默认提示词以 `alpha_hint + " " + style_prefix` 开头；
3. 工作流正节点的默认提示词以 `layout_suffix` 结尾。

用法：`python scripts/check_prompt_parts.py`，通过返回 0，不通过返回 1。
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# C# 侧：`public const string Name = "a" + "b";`
CONST_RE = re.compile(r"public\s+const\s+string\s+(\w+)\s*=\s*(.*?);", re.DOTALL)
LITERAL_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')

# C# 常量名 → bindings 里的键名
CSHARP_MAP = {
    "QwenAlphaHint": "alpha_hint",
    "StylePrefix": "style_prefix",
    "LayoutSuffix": "layout_suffix",
}

PART_NAMES = ("alpha_hint", "style_prefix", "layout_suffix")

PORTRAIT_WORKFLOW = "charforge_qwen21_txt2img.workflow.json"

_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "'": "'"}


def _find_package_dir() -> Path:
    candidates = sorted(ROOT.glob("packages/*/package.json"))
    return candidates[0].parent if candidates else ROOT / "packages" / "com.charforge"


PKG = _find_package_dir()
CONFIG_DIR = PKG / "Configs" / "comfyui"
SERVICE = PKG / "Editor" / "Services" / "PromptForgeService.cs"

errors: list[str] = []
warnings: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


def _unescape(literal: str) -> str:
    out: list[str] = []
    i = 0
    while i < len(literal):
        ch = literal[i]
        if ch == "\\" and i + 1 < len(literal):
            out.append(_ESCAPES.get(literal[i + 1], literal[i + 1]))
            i += 2
        else:
            out.append(ch)
            i += 1
    return "".join(out)


def parse_csharp_consts(text: str) -> dict[str, str]:
    """把 `public const string X = "a" + "b";` 拼回完整字符串。

    只认 `public`：文件里还有一个 `private const string SystemPrompt`，
    它带 `;` 与 `\n`，混进来会把正则的边界搞乱。
    """
    found: dict[str, str] = {}
    for name, body in CONST_RE.findall(text):
        pieces = [_unescape(lit) for lit in LITERAL_RE.findall(body)]
        if pieces:
            found[name] = "".join(pieces)
    return found


def load_bindings() -> dict:
    path = CONFIG_DIR / PORTRAIT_WORKFLOW.replace(".workflow.json", ".bindings.json")
    if not path.is_file():
        fail(f"找不到 bindings：{path}")
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def load_workflow_positive(bindings: dict) -> str:
    path = CONFIG_DIR / PORTRAIT_WORKFLOW
    if not path.is_file():
        fail(f"找不到工作流：{path}")
        return ""
    workflow = json.loads(path.read_text(encoding="utf-8"))
    node_id = str(bindings.get("positive", ""))
    field = bindings.get("positive_input", "prompt")
    node = workflow.get(node_id)
    if not isinstance(node, dict):
        fail(f"工作流里没有正节点 {node_id}（bindings.positive 指的）")
        return ""
    value = (node.get("inputs") or {}).get(field)
    if not isinstance(value, str):
        fail(f"正节点 {node_id} 的 {field} 不是字符串，没法校验固定件")
        return ""
    return value


def check_csharp_matches_bindings(csharp: dict, parts: dict) -> None:
    for const_name, key in CSHARP_MAP.items():
        if const_name not in csharp:
            fail(f"PromptForgeService.cs 里没找到 public const string {const_name}")
            continue
        if key not in parts:
            continue  # 缺键的问题由 check_parts_complete 报，这里不重复刷屏
        if csharp[const_name] != parts[key]:
            fail(f"{const_name} 与 bindings.prompt_parts.{key} 不一致\n"
                 f"      C#      ：{csharp[const_name]}\n"
                 f"      bindings：{parts[key]}")


def check_parts_complete(parts) -> bool:
    if not isinstance(parts, dict):
        fail("bindings 里没有 prompt_parts——外部下游没法安全地拼立绘提示词")
        return False
    missing = [k for k in PART_NAMES if not parts.get(k)]
    if missing:
        fail(f"bindings.prompt_parts 缺字段：{missing}")
        return False
    return True


def check_workflow_wraps_parts(prompt: str, parts: dict) -> None:
    if not prompt:
        return
    head = f"{parts['alpha_hint']} {parts['style_prefix']}"
    if not prompt.startswith(head):
        fail("工作流正节点的默认提示词**没有**以 `alpha_hint + ' ' + style_prefix` 开头。\n"
             "      它是「固定件 + 一段样例角色本体」拼出来的；开头不对说明固定件被改过而这里没同步，\n"
             "      或者有人手工编辑了节点 6 的提示词。")
    if not prompt.rstrip().endswith(parts["layout_suffix"]):
        fail("工作流正节点的默认提示词**没有**以 layout_suffix 结尾。")
    if prompt == head + parts["layout_suffix"]:
        # 不是错误，但说明样例角色本体被删空了——节点 6 在 ComfyUI 里就没法直接试跑。
        warnings.append("工作流正节点的默认提示词只剩固定件，没有样例角色本体"
                        "（在 ComfyUI 里直接提交会得到一张空描述图）。")


def main() -> int:
    print("校验立绘提示词固定件（C# ←→ bindings ←→ 工作流）…")

    bindings = load_bindings()
    parts = bindings.get("prompt_parts")

    complete = check_parts_complete(parts)
    if complete:
        if not SERVICE.is_file():
            fail(f"找不到 {SERVICE}")
        else:
            check_csharp_matches_bindings(parse_csharp_consts(SERVICE.read_text(encoding="utf-8")), parts)
        check_workflow_wraps_parts(load_workflow_positive(bindings), parts)

    for w in warnings:
        print(f"  提醒：{w}")
    for e in errors:
        print(f"  错误：{e}")

    if errors:
        print(f"\n未通过：{len(errors)} 个错误，{len(warnings)} 条提醒")
        return 1
    print(f"\n通过：三处一致（{', '.join(PART_NAMES)}），{len(warnings)} 条提醒")
    return 0


if __name__ == "__main__":
    sys.exit(main())
