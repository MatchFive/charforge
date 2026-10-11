"""[7] Assemble：产出 Unity 的「施工图」。

服务端不直接生成 prefab（那是 Editor 侧的活），而是产出一份**装配指令 JSON** +
一份**人设 JSON**。Unity 的 PrefabAssembler.cs 读这两份文件施工。

这样分层的三个好处：
1. 服务端不需要装 Unity，CI 能跑；
2. prefab 可以随时按指令重新生成（改了指令重跑即可，不用重新出图）；
3. 人设与资产解耦——改台词不用重新生成美术资产。
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from server.models.schemas import (
    ActionName,
    AesLabelSpec,
    AnimationClipSpec,
    AssemblySpec,
    CharacterCard,
    ColliderSpec,
    DialogueSpec,
    Direction,
    JobRequest,
    SpriteManifest,
)

_PERSONA_TEMPLATE = """你是{role}，名字叫{name}。

【身份】{role}{era}
【性格】{personality}
【说话要求】
- 用词符合身份与年代，不要出现现代词汇或网络用语。
- 每次回答不超过 3 句，避免长篇大论。
- 不知道的事情就说不知道，不要编造。
{knowledge}
{forbidden}
【开场白】{greeting}
"""

_KNOWLEDGE_BLOCK = """
【知识边界】
- 只依据以下资料回答，资料没有的内容不要推测：
{notes}
"""

_FORBIDDEN_BLOCK = """
【禁止】
- 不要提及：{items}
"""


def _ascii_slug(text: str, max_len: int = 20) -> str:
    """生成 ASCII 安全的标识片段。

    中文角色名（如「云叔」）不能直接用进 prefab 文件名——跨平台 git、CI、
    不同文件系统编码都会出问题。显示名保留中文，文件名用 ASCII。
    """
    slug = re.sub(r"[^A-Za-z0-9]+", "_", text).strip("_").lower()
    return slug[:max_len]


def make_prefab_name(card: CharacterCard, job_id: str) -> str:
    """prefab 名：ASCII 安全 + 短后缀避免重名。

    形如 NPC_yun_shu_a1b2c3；角色名若全为中文则退化为 NPC_a1b2c3。
    """
    suffix = job_id[:6]
    slug = _ascii_slug(card.name)
    return f"NPC_{slug}_{suffix}" if slug else f"NPC_{suffix}"


def build_persona(card: CharacterCard) -> dict:
    """构造人设 JSON。文博场景下 knowledge 段会带上藏品档案的形制要点。"""
    personality = "、".join(card.personality) if card.personality else "沉稳、亲切"

    knowledge = ""
    if card.reference_notes:
        knowledge = _KNOWLEDGE_BLOCK.format(notes=card.reference_notes)

    forbidden = ""
    if card.forbidden:
        forbidden = _FORBIDDEN_BLOCK.format(items="、".join(card.forbidden))

    prompt = _PERSONA_TEMPLATE.format(
        role=card.role,
        name=card.name,
        era=f"（{card.era_or_period}）" if card.era_or_period else "",
        personality=personality,
        knowledge=knowledge,
        forbidden=forbidden,
        greeting=f"你好，我是{card.name}，{card.role}。",
    )

    return {
        "id": card.name,
        "display_name": card.name,
        "role": card.role,
        "personality": card.personality,
        "era_or_period": card.era_or_period,
        "knowledge_domains": [card.reference_notes] if card.reference_notes else [],
        "forbidden": card.forbidden,
        "persona_prompt": prompt.strip(),
        "greeting": f"你好，我是{card.name}，{card.role}。",
        "max_tokens": 160,
        "source_prompt": card.source_prompt,
    }


def build_animations(manifest: SpriteManifest, frame_rate: int = 8) -> list[AnimationClipSpec]:
    """把切片清单还原成动画片段列表（一行一个 clip）。"""
    grouped: dict[str, list[tuple[int, str]]] = {}
    for rect in manifest.sprites:
        clip_key, _, idx = rect.name.rpartition("_")
        try:
            grouped.setdefault(clip_key, []).append((int(idx), rect.name))
        except ValueError:
            continue

    clips: list[AnimationClipSpec] = []
    for clip_key, items in grouped.items():
        items.sort(key=lambda x: x[0])
        if "_" not in clip_key:
            continue
        direction_raw, action_raw = clip_key.split("_", 1)
        try:
            direction = Direction(direction_raw)
            action = ActionName(action_raw)
        except ValueError:
            continue
        clips.append(
            AnimationClipSpec(
                name=clip_key,
                direction=direction,
                action=action,
                sprites=[name for _, name in items],
                frame_rate=frame_rate,
                loop=True,
            )
        )

    order = {d: i for i, d in enumerate(Direction)}
    clips.sort(key=lambda c: (order.get(c.direction, 99), c.action.value))
    return clips


def build_assembly_spec(
    card: CharacterCard,
    manifest: SpriteManifest,
    req: JobRequest,
    job_id: str,
    frame_rate: int = 8,
) -> AssemblySpec:
    """组装最终指令。"""
    animations = build_animations(manifest, frame_rate=frame_rate)

    return AssemblySpec(
        job_id=job_id,
        prefab_name=make_prefab_name(card, job_id),
        character_card=card,
        animations=animations,
        collider=ColliderSpec(shape="capsule", radius=0.3, size=(0.6, 1.0), offset=(0.5, 0.0)),
        movement=req.movement,
        dialogue=DialogueSpec(
            enabled=req.dialogue_enabled,
            provider="llm-unity" if req.dialogue_enabled else "none",
            persona_path="persona.json" if req.dialogue_enabled else None,
        ),
        aes_label=AesLabelSpec(
            enabled=True,
            text="本内容由人工智能生成",
            placement="corner",
        ),
    )


def write_outputs(
    spec: AssemblySpec,
    persona: dict,
    manifest: SpriteManifest,
    out_dir: Path,
) -> dict[str, str]:
    """把三份产物落到输出目录。"""
    out_dir.mkdir(parents=True, exist_ok=True)

    spec_path = out_dir / "assembly_spec.json"
    persona_path = out_dir / "persona.json"
    manifest_path = out_dir / "manifest.json"

    spec_path.write_text(spec.model_dump_json(indent=2), encoding="utf-8")
    persona_path.write_text(json.dumps(persona, ensure_ascii=False, indent=2), encoding="utf-8")
    manifest_path.write_text(manifest.model_dump_json(indent=2), encoding="utf-8")

    return {
        "assembly_spec": str(spec_path),
        "persona": str(persona_path),
        "manifest": str(manifest_path),
    }
