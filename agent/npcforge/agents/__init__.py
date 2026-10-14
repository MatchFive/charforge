"""四个 LLM 裁决点。

它们**不负责跑流程**——流程在 `loop.py` 里，是确定性的阶段推进。
这四个模块只在需要判断的地方被调用，且每一个在 LLM 不可用时都有确定性降级：

| 模块 | 判断什么 | 降级成什么 |
|---|---|---|
| `persona_writer` | 一句话描述 → 完整人设 | 模板拼一个朴素人设（诚实，不编造） |
| `prompt_forger` | 角色描述 → 绘图提示词 | 直接用描述原文（Qwen 支持中文直入） |
| `qa_judge` | 质检报告 → 收下 / 重跑 / 换参数 | 同一套阈值的规则判定 |
| `diagnostician` | 失败日志 → 下一步动作 | 已知故障特征匹配，否则交给人 |

降级一定要在日志里留痕（`[降级]` 前缀）——不许让人以为"模型给的就是这样"。
"""

from .base import LlmUnavailable, brief, build_model, describe, guard_structured
from .diagnostician import diagnose
from .persona_writer import forge_persona
from .prompt_forger import forge_prompt
from .qa_judge import judge_frames

__all__ = [
    "LlmUnavailable",
    "brief",
    "build_model",
    "describe",
    "guard_structured",
    "diagnose",
    "forge_persona",
    "forge_prompt",
    "judge_frames",
]
