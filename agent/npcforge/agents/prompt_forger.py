"""提示词锻造：角色描述 → 文生图提示词。

与插件里 `PromptForgeService` 是**同一套约束**（那不是巧合——两边产出的提示词
会喂给同一个工作流，措辞漂移会直接改变出图）：

* **模型只写「角色本体」**：身份、服饰、道具、神态。画风件与版式件由插件套在外面，
  所以这里不许写画风、不许写"厚涂""半写实"这类词——写了会跟固定画风件打架。
* **禁止年代、朝代、藏品档案、考古考据**。它们不改变任何一个像素，只会把提示词带偏。
* **目标是英文提示词**，但角色的中文专名可以保留（Qwen 的文本编码器是多语言 Qwen3-VL，
  中文直入没问题；反倒是机翻的英文专名会丢掉服饰与配色的精度）。
"""

from __future__ import annotations

from ..config import Config
from ..models import NpcJob, PromptDraft
from .base import LlmUnavailable, brief, build_model, guard_structured

SYSTEM = """你把一句中文角色描述锻造成文生图提示词。

只写「角色本体」这五类信息，其余一律不写：
  身份感、服饰（形制 / 材质 / 配色）、随身道具、发型发色、神态与姿态。

**绝对不要写**：
  - 画风词：thick impasto / semi-realistic / pixel art / concept art / 8k / masterpiece / best quality
    （画风由下游固定注入，写了会打架）
  - 镜头词：close-up / full body / from side（构图也由下游固定）
  - 年代朝代、窑口、藏品编号、考古结论（不改变像素，只会带偏）
  - 背景描述（下游要的是可抠图的干净立绘）

写作要求：
  - 输出英文，用逗号分隔的短语，不用完整句子。
  - 中文专名可直译或音译后保留，如 褙子 → beizi，不要机翻成 "vest"。
  - 材质要具体：不是 "clothes"，而是 "matte linen jacket with visible weave"。
  - 配色要具体：不是 "warm colors"，而是 "celadon green with off-white trim"。
  - 控制在 45~80 个词之间。太短会丢信息，太长会稀释每一条的权重。

`reasoning` 用一句中文说明你做了哪些取舍。
"""


def forge_prompt(job: NpcJob, cfg: Config, log=print) -> PromptDraft:
    """锻造绘图提示词。没有 LLM 时直接用描述原文——Qwen 支持中文直入。"""
    try:
        handle = build_model(cfg)
    except LlmUnavailable as e:
        log(f"[降级] 提示词用描述原文：{brief(e)}")
        return PromptDraft(prompt=job.description, reasoning="未调用模型，直接使用角色描述原文")

    from pydantic_ai import Agent

    agent = Agent(handle.model, output_type=PromptDraft, instructions=SYSTEM)

    ask = [f"角色描述：{job.description}"]
    if job.role:
        ask.append(f"定位（只作语气参考，不要写进提示词）：{job.role}")

    log(f"[llm] 提示词锻造 → {handle.model_name}")
    result = agent.run_sync("\n".join(ask))
    draft = result.output
    guard_structured(draft, "提示词", log)

    if len(draft.prompt.split()) < 8:
        raise RuntimeError(f"锻造出的提示词太短（{draft.prompt!r}）——"
                           "多半是模型没按格式输出，建议换模型或直接用描述原文。")

    log(f"[llm] 提示词完成：{len(draft.prompt.split())} 词")
    return draft
