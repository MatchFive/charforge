"""[1] Prompt Forge：自由描述 → 结构化角色卡 → 分方向、分动作的提示词组。

为什么需要这一段？
直接拿用户的一句话去生成，会得到「每次都不一样的角色」。这一段把描述**固化成结构化字段**
（外观 / 剪影 / 配色 / 禁忌 / 年代），后续每一帧都从同一张卡派生，一致性才有保障。

当前状态：
- 规则版：可用，无需 LLM 也能跑通全链路（默认走这条）。
- LLM 版：接口已定，待实现（见 forge_with_llm）。
"""

from __future__ import annotations

import re
import unicodedata
from pathlib import Path

import yaml

from server.config import settings
from server.models.schemas import (
    ActionName,
    CharacterCard,
    Direction,
    JobRequest,
    StyleProfile,
)

# 从描述里捞「年代 / 形制」的简易规则（文博场景常用）
_ERA_PATTERNS = [
    r"(商|周|秦|汉|三国|晋|南北朝|隋|唐|五代|宋|辽|金|元|明|清|民国)代?",
    r"(新石器|仰韶|龙山|良渚|二里头|殷墟|三星堆)",
    r"(公元前\d+年|\d{3,4}年)",
]

# 画风 → 基础提示词（预设没覆盖时兜底）
_FALLBACK_STYLE_TAGS = [
    "clean lineart",
    "consistent character design",
    "game asset",
    "transparent background",
    "full body",
]

# 通用负向词。这些是 2D 角色生成最常见的崩坏点，别删。
_COMMON_NEGATIVE = [
    "extra limbs",
    "extra fingers",
    "fused fingers",
    "missing fingers",
    "bad anatomy",
    "deformed hands",
    "blurry",
    "low quality",
    "jpeg artifacts",
    "text",
    "watermark",
    "signature",
    "multiple views in one image",
    "cropped",
    "off-center",
    "shadow on background",
]

_DIRECTION_PHRASE = {
    Direction.SOUTH: "front view, facing the camera, looking at viewer",
    Direction.NORTH: "back view, seen from behind",
    Direction.EAST: "side view, facing right, profile",
    Direction.WEST: "side view, facing left, profile",
}

_ACTION_PHRASE = {
    ActionName.IDLE: "standing idle, relaxed pose, subtle breathing",
    ActionName.WALK: "walking cycle, mid-stride, one leg forward",
}


def _slugify(text: str) -> str:
    """把中文描述压成一个可用的标识片段（用于文件名/切片名）。"""
    text = unicodedata.normalize("NFKC", text).strip()
    text = re.sub(r"[\s,，。、；;：:！!？?【】\[\]（）()「」\"'`~!@#$%^&*+=|\\/<>]+", "_", text)
    return text.strip("_")[:24] or "char"


def _guess_era(text: str) -> str | None:
    for pat in _ERA_PATTERNS:
        m = re.search(pat, text)
        if m:
            return m.group(0)
    return None


def load_preset_tags(preset: str) -> tuple[list[str], list[str]]:
    """读取 configs/style_presets/<preset>.yaml 里的正/负向风格词。"""
    path = settings.presets_path / f"{preset}.yaml"
    if not path.exists():
        return [], []
    data = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    return list(data.get("positive", [])), list(data.get("negative", []))


def _short_name(role: str, job_id: str) -> str:
    """从身份描述推断短名。

    规则版只做最保守的事：身份短语 ≤6 字就拿来当名（如「杂货店主」）；
    太长说明描述是整句（如「一位四十余岁的博物馆讲解员」），规则版取不出好名字，
    退化用任务号——此时应由调用方显式传 character_name，或等 LLM 版来推断。
    """
    compact = re.sub(r"_+", "", _slugify(role))
    if 0 < len(compact) <= 6:
        return compact
    return job_id[:6]


def forge_character_card(req: JobRequest, job_id: str) -> CharacterCard:
    """规则版角色卡构造。不依赖任何模型，先跑通链路用。

    它做的事很朴素：
    - 描述第一句当身份，整句当外观；
    - 抓年代/形制关键词（文博场景）；
    - 用预设的风格词补 style_tags；
    - 把用户显式给的 forbidden 合并进来。

    升级到 LLM 版之后，这个函数仍然作为**降级路径**保留（LLM 不可用时兜底）。
    """
    raw = req.prompt.strip()
    role = re.split(r"[，,。.；;]", raw)[0][:24] or "角色"
    era = req.era_or_period or _guess_era(raw)

    positive, _ = load_preset_tags(req.style_preset)

    card = CharacterCard(
        job_id=job_id,
        source_prompt=raw,
        name=req.character_name or _short_name(role, job_id),
        role=role,
        personality=[],
        appearance=raw,
        palette=[],
        silhouette_notes="轮廓清晰、剪影可辨；头身比与项目既有角色保持一致",
        forbidden=list(req.forbidden),
        actions=list(req.actions),
        directions=list(req.directions),
        style_tags=positive or list(_FALLBACK_STYLE_TAGS),
        era_or_period=era,
        reference_notes=req.reference_notes,
    )
    return card


async def forge_with_llm(req: JobRequest, job_id: str, llm=None) -> CharacterCard:
    """LLM 版角色卡构造。

    TODO（你来实现）：
    1. 设计一个 system prompt，要求模型**只输出 JSON**（配合 JSON Schema 约束）；
    2. 把 req.prompt / req.era_or_period / req.forbidden 一起给它；
    3. 对输出做 pydantic 校验（CharacterCard.model_validate_json），校验失败重试 1 次；
    4. 失败则降级到 forge_character_card。

    文博场景建议追加一步：先用 RAG 从藏品档案检索「形制要点」，写进 reference_notes，
    再让 LLM 生成外观描述——这一步能避免「宋代器型画成明代样式」这类硬伤。
    """
    raise NotImplementedError("TODO: 接你的 LLM（本地 Ollama / 云端皆可），失败时降级到规则版")


def build_prompts(
    card: CharacterCard,
    style: StyleProfile,
    direction: Direction,
    action: ActionName,
) -> tuple[str, str]:
    """给某一帧组装 (positive, negative) 提示词。

    分方向、分动作是刻意的：四向一致性靠「同一张卡 + 不同的视角短语」，
    而不是靠让模型自己想象四个视角。
    """
    parts: list[str] = []

    parts.append(card.appearance)
    if card.era_or_period:
        parts.append(f"{card.era_or_period} era, historically accurate")
    if card.silhouette_notes:
        parts.append(card.silhouette_notes)

    parts.append(_DIRECTION_PHRASE[direction])
    parts.append(_ACTION_PHRASE[action])

    parts.extend(card.style_tags)
    parts.extend(style.positive_tags)
    if card.palette:
        parts.append("limited palette " + " ".join(card.palette))

    negative = list(_COMMON_NEGATIVE)
    negative.extend(style.negative_tags)
    negative.extend(f"no {f}" for f in card.forbidden)

    positive_text = ", ".join(p for p in parts if p)
    negative_text = ", ".join(dict.fromkeys(n for n in negative if n))
    return positive_text, negative_text


def plan_pose_grid(card: CharacterCard, frames_per_clip: int = 4) -> list[dict]:
    """规划要生成哪些帧：方向 × 动作。返回纯数据，便于打印和测试。"""
    grid: list[dict] = []
    for direction in card.directions:
        for action in card.actions:
            grid.append(
                {
                    "direction": direction,
                    "action": action,
                    "frame_count": frames_per_clip,
                    "clip_name": f"{card.name}_{direction.value}_{action.value}",
                }
            )
    return grid


def save_card(card: CharacterCard, out_dir: Path) -> Path:
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "character_card.json"
    path.write_text(card.model_dump_json(indent=2), encoding="utf-8")
    return path
