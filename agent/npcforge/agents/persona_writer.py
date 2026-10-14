"""人设扩写：一句话描述 → 完整 persona.json。

**这一环补的是插件的空白。** `NpcCharacter` 早就有 `personaAsset` 字段和
`GetSystemPrompt()`，但三步流水线里没有任何一步生产 persona.json——
所以此前它一直是"字段在、没人填"。Agent 把它补上。

产物会被写成 `<产物目录>/persona.json`，其中 `persona_prompt` 是最终交给
对话模型的系统提示词。**键名固定**，Unity 侧是手写字符扫描找这个键的。
"""

from __future__ import annotations

from ..config import Config
from ..models import NpcJob, PersonaSpec
from .base import LlmUnavailable, brief, build_model, guard_structured

SYSTEM = """你是游戏 NPC 的人设撰写者。用户给你一句话角色描述，你补全成一份可直接驱动对话的人设。

产出要求：
- `display_name` 是给玩家看的中文名，可以带称谓（如「云叔」「阿茹姑娘」）。
- `personality` 写性格**倾向**，不要写外貌——外貌由立绘负责，写在这里没人看。
- `speech_style` 要具体到能模仿：句长、用词习惯、口头禅、对不同人的称呼差异。
- `dialogue_samples` 是示范，用来锁定语气，要写出这个角色**特有的**说法，
  不要写"你好，有什么可以帮您"这种放之四海皆准的句子。
- `persona_prompt` 是最终产物，一段完整的系统提示词。必须包含四件事：
  ① 身份与说话方式；② 知道什么（knowledge_scope）；③ **不知道什么**（boundaries）；
  ④ 一条硬约束：**遇到自己知识范围外的事，就说"这个我说不准"，绝不编造**。
- 这条硬约束对文博类角色尤其关键：NPC 在游戏里随口断言文物年代、出处、
  考古结论，是会把用户拖进合规风险的。宁可它答"我不太清楚，您问一下馆里的研究员"。

不要做的事：
- 不要写具体年代、朝代、窑口、藏品档案。这些属于世界观设定，不属于人设；
  写了也只会让人设变得又长又硬，模型照着念还容易错。
- 不要写能力数值、游戏机制（血量、攻击力）。
- 不要在 persona_prompt 里用 Markdown 标题和列表——它是喂给对话模型的字符串，
  自然段落比结构化排版更好用。

语言：全部用简体中文。
"""


def forge_persona(job: NpcJob, cfg: Config, log=print) -> PersonaSpec:
    """生成人设。LLM 不可用时退回模板（明确留痕）。"""
    try:
        handle = build_model(cfg)
    except LlmUnavailable as e:
        log(f"[降级] 人设走模板：{brief(e)}")
        return _fallback(job, log)

    from pydantic_ai import Agent

    agent = Agent(handle.model, output_type=PersonaSpec, instructions=SYSTEM)

    ask = [f"角色名（内部标识）：{job.name}",
           f"角色描述：{job.description}"]
    if job.role:
        ask.append(f"定位：{job.role}")
    if job.persona_hint:
        ask.append(f"补充要求：{job.persona_hint}")

    log(f"[llm] 人设扩写 → {handle.model_name}")
    result = agent.run_sync("\n".join(ask))
    spec = result.output
    guard_structured(spec, "人设", log)

    spec.character_id = job.name          # 以作业名为准，防止模型自己改写
    if not spec.persona_prompt.strip():
        raise RuntimeError("persona_prompt 是空的——它是 Unity 侧唯一会读的字段。")
    log(f"[llm] 人设完成：{spec.display_name}，{len(spec.persona_prompt)} 字的系统提示词")
    return spec


def _fallback(job: NpcJob, log) -> PersonaSpec:
    """没有 LLM 时的最小可用人设。

    刻意做得**诚实**：不去编造性格细节，只把用户给的信息整理成结构化的形式。
    一个编造的假人设比一个朴素的真人设危险得多——后者你能一眼看出是没配模型。
    """
    role = job.role or "NPC"
    hint = f"（补充要求：{job.persona_hint}）" if job.persona_hint else ""
    prompt = (
        f"你扮演「{job.description}」。\n"
        f"你的定位是{role}。\n"
        "说话自然、简洁，符合你的身份。\n"
        "遇到你不确定的事情，直接说不太清楚，不要编造。"
    )
    log("[降级] 人设由模板拼出（未调用模型）——配好 LLM 后重跑可得到完整人设")
    return PersonaSpec(
        character_id=job.name,
        display_name=job.name,
        role=role,
        personality=["沉静", "克制", "乐于解答"],
        speech_style="语速平缓，用词朴素，不夸张。",
        background=f"{job.description}{hint}",
        knowledge_scope=["自身身份相关的话题"],
        boundaries=["不确定的事一律说不清楚，不编造"],
        greeting="你好。",
        dialogue_samples=["你好。", "这个我说不太准。", "你可以再问我别的。"],
        persona_prompt=prompt,
    )
