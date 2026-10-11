"""[2] Style Lock：画风锁定。本项目的差异化能力之一。

为什么这一段重要？
「画风一致性」在 2D 侧不是提示词问题，而是**约束问题**。做法有三层，按可靠性递增：

    1. 风格提示词（弱，但有总比没有好）
    2. 参考图（IP-Adapter / 参考图引导）—— MVP 主力，无需训练
    3. LoRA（强，但要训练数据，放到 V1 之后再上）

MVP 阶段请**只做前两层**。先训 LoRA 会卡死整条链路。

当前状态：预设加载 + 接口已定，反推与打分待实现。
"""

from __future__ import annotations

from pathlib import Path

import yaml

from server.config import settings
from server.models.schemas import JobRequest, StyleProfile


def load_preset(preset: str) -> dict:
    """读取风格预设。预设只是「起点」，最终约束仍由参考图决定。"""
    path = settings.presets_path / f"{preset}.yaml"
    if not path.exists():
        return {}
    return yaml.safe_load(path.read_text(encoding="utf-8")) or {}


def describe_references(ref_paths: list[str]) -> list[str]:
    """从参考图反推风格标签。

    TODO（你来实现，二选一）：
    A. 视觉模型反推：本地跑 BLIP / Florence-2 / Qwen-VL，产出 caption 再抽风格词；
    B. CLIP 零样本匹配：预定义一组风格词表（如 "flat vector", "pixel art", "watercolor",
       "cel shaded", "ink wash painting"），算参考图与每个词的相似度，取 top-k。

    建议先做 B——不需要额外模型，CLIP 本来就为算相似度准备，一个函数就能出结果。
    """
    if not ref_paths:
        return []
    raise NotImplementedError("TODO: 用 BLIP/Qwen-VL 反推，或 CLIP 与风格词表做零样本匹配")


def extract_palette(ref_paths: list[str], k: int = 5) -> list[str]:
    """从参考图提取主色（hex）。

    TODO（你来实现）：k-means 聚类（sklearn 或手写）→ 取 k 个簇心 → 转 hex。
    这一步的产出会写进角色卡，用来约束配色的整体倾向（青绿 / 赭石 / 水墨等）。
    """
    if not ref_paths:
        return []
    raise NotImplementedError("TODO: 对参考图做 k-means 取主色，返回 ['#2E5C4E', ...]")


def score_similarity(image_path: str, master_path: str) -> float:
    """算产出与 master reference 的 CLIP 相似度（0-1）。验收指标用。"""
    raise NotImplementedError("TODO: CLIP 编码两张图，算余弦相似度")


def lock_style(req: JobRequest, master_reference: str | None = None) -> StyleProfile:
    """合成最终画风约束。

    master_reference 必须是**人工确认过**的那一张——这是人类在环的检查点，
    不要跳过。全自动定 master 的结果通常不可控。
    """
    preset = load_preset(req.style_preset)

    profile = StyleProfile(
        preset=req.style_preset,
        master_reference=master_reference,
        positive_tags=list(preset.get("positive", [])),
        negative_tags=list(preset.get("negative", [])),
        lora=preset.get("lora"),
        lora_scale=float(preset.get("lora_scale", 0.8)),
        ip_adapter_refs=list(req.reference_images),
        clip_similarity=None,
    )
    return profile


def save_profile(profile: StyleProfile, out_dir: Path) -> Path:
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / "style_profile.json"
    path.write_text(profile.model_dump_json(indent=2), encoding="utf-8")
    return path
