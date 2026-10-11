"""[3] Generate：出图。

三种后端，接口一致，靠 settings.charforge_backend 切换：

    mock  —— 画占位人形（无需 GPU / 无需 key）。用来验证水管，不是用来出货。
    local —— diffusers 或 ComfyUI API（需 GPU）。
    cloud —— 云端图像 API（需 key，注意：素材会离开本机）。

注意 cloud 后端的取舍：文博机构的馆藏素材通常不允许上传第三方，
所以**本地链路是这个项目的主线**，云端只是便利选项。
"""

from __future__ import annotations

import hashlib
import math
from collections.abc import Callable
from pathlib import Path

from PIL import Image, ImageDraw

from server.config import settings
from server.models.schemas import (
    ActionName,
    CharacterCard,
    Direction,
    FrameAsset,
    JobRequest,
    StyleProfile,
)
from server.pipeline.prompt_forge import build_prompts, plan_pose_grid

Progress = Callable[[float, str], None]

# 纯色背景便于抠图（洋红是行业惯例的键控色）
_MOCK_BG = (255, 0, 255)
_MOCK_DEFAULT_PALETTE = [(46, 92, 78), (196, 154, 92), (232, 226, 210)]


# ─────────────────────────────────────────────────────────────
# mock：占位人形
# ─────────────────────────────────────────────────────────────


def _mock_color(card: CharacterCard, idx: int) -> tuple[int, int, int]:
    if card.palette:
        hexv = card.palette[idx % len(card.palette)].lstrip("#")
        if len(hexv) == 6:
            return tuple(int(hexv[i : i + 2], 16) for i in (0, 2, 4))  # type: ignore[return-value]
    return _MOCK_DEFAULT_PALETTE[idx % len(_MOCK_DEFAULT_PALETTE)]


def _draw_mock_character(
    card: CharacterCard,
    direction: Direction,
    action: ActionName,
    frame_index: int,
    frame_count: int,
    size: tuple[int, int],
) -> Image.Image:
    """画一个极简人形：头 + 身体 + 双腿 + 一个方向标记。

    腿的摆幅随帧变化，所以 walk 序列在打包后能看到明显的帧间差异——
    这让你能在没有 GPU 的情况下验证「打包 / 切片 / 动画」整条链路。
    """
    w, h = size
    img = Image.new("RGB", (w, h), _MOCK_BG)
    d = ImageDraw.Draw(img)

    body_color = _mock_color(card, 0)
    accent = _mock_color(card, 1)

    cx = w // 2
    head_r = int(min(w, h) * 0.10)
    head_cy = int(h * 0.26)
    torso_top = head_cy + head_r + int(h * 0.02)
    torso_bottom = int(h * 0.66)

    # 相位：让 walk 有一个完整的迈步周期
    phase = (frame_index / max(frame_count, 1)) * 2 * math.pi
    swing = int(math.sin(phase) * min(w, h) * 0.05) if action is ActionName.WALK else 0

    # 双腿
    leg_w = int(w * 0.07)
    d.rounded_rectangle(
        [cx - leg_w * 2 + swing, torso_bottom - 4, cx - leg_w + swing, int(h * 0.88)],
        radius=leg_w // 2,
        fill=accent,
    )
    d.rounded_rectangle(
        [cx + leg_w - swing, torso_bottom - 4, cx + leg_w * 2 - swing, int(h * 0.88)],
        radius=leg_w // 2,
        fill=accent,
    )

    # 身体
    d.rounded_rectangle(
        [cx - int(w * 0.16), torso_top, cx + int(w * 0.16), torso_bottom],
        radius=int(w * 0.05),
        fill=body_color,
    )

    # 头
    d.ellipse(
        [cx - head_r, head_cy - head_r, cx + head_r, head_cy + head_r],
        fill=body_color,
    )

    # 方向标记：正面画两只眼睛，背面不画，侧面画一只
    eye_r = max(2, head_r // 6)
    eye_y = head_cy
    if direction is Direction.SOUTH:
        d.ellipse([cx - head_r // 2 - eye_r, eye_y - eye_r, cx - head_r // 2 + eye_r, eye_y + eye_r], fill=(255, 255, 255))
        d.ellipse([cx + head_r // 2 - eye_r, eye_y - eye_r, cx + head_r // 2 + eye_r, eye_y + eye_r], fill=(255, 255, 255))
    elif direction is Direction.NORTH:
        pass
    elif direction is Direction.EAST:
        d.ellipse([cx + head_r // 3 - eye_r, eye_y - eye_r, cx + head_r // 3 + eye_r, eye_y + eye_r], fill=(255, 255, 255))
    else:
        d.ellipse([cx - head_r // 3 - eye_r, eye_y - eye_r, cx - head_r // 3 + eye_r, eye_y + eye_r], fill=(255, 255, 255))

    return img


# ─────────────────────────────────────────────────────────────
# local：diffusers / ComfyUI
# ─────────────────────────────────────────────────────────────


def _generate_local(
    positive: str,
    negative: str,
    out_path: Path,
    control_image: str | None,
    seed: int,
    size: tuple[int, int],
) -> Path:
    """本地生成。

    TODO（你来实现，二选一）：

    A. diffusers 直调
       ```python
       from diffusers import StableDiffusionXLPipeline, ControlNetModel
       pipe = StableDiffusionXLPipeline.from_pretrained(settings.sd_model_id, torch_dtype=...)
       pipe.load_ip_adapter(...)          # 画风参考图
       img = pipe(prompt=positive, negative_prompt=negative,
                  ip_adapter_image=ref, generator=torch.Generator().manual_seed(seed),
                  num_inference_steps=28, guidance_scale=6.0).images[0]
       ```

    B. ComfyUI API（推荐先用这条）
       你已经跑通过 ComfyUI 链路，把 workflow 存成 API 格式 JSON，
       这里只做「填参数 + POST /prompt + 轮询 history + 下载图」。

    三个参数务必暴露出来：seed（一致性关键）、control_image（姿势锁定）、
    以及 IP-Adapter 的参考图（画风关键）。
    """
    raise NotImplementedError("TODO: 接 diffusers 或 ComfyUI API")


def _generate_cloud(
    positive: str,
    negative: str,
    out_path: Path,
    size: tuple[int, int],
) -> Path:
    """云端生成（OpenAI Images 等）。

    TODO（你来实现）。注意：多数云 API 不支持 negative prompt 与 ControlNet，
    姿势锁定与画风锁定会退化——这也是「本地优先」的原因之一。
    """
    raise NotImplementedError("TODO: 接云端图像 API（注意素材出本机的合规问题）")


# ─────────────────────────────────────────────────────────────
# 对外入口
# ─────────────────────────────────────────────────────────────


def _seed_for(clip_name: str, frame_index: int) -> int:
    """同一 clip 共用 seed 族，帧间只做微调——这是帧一致性的第一道保险。"""
    base = int(hashlib.sha256(clip_name.encode("utf-8")).hexdigest()[:8], 16)
    return (base + frame_index) % (2**31)


def generate_frames(
    card: CharacterCard,
    style: StyleProfile,
    req: JobRequest,
    out_dir: Path,
    frames_per_clip: int = 4,
    progress: Progress | None = None,
) -> list[FrameAsset]:
    """生成所有方向的帧。返回落盘清单。"""
    backend = settings.charforge_backend
    out_dir.mkdir(parents=True, exist_ok=True)

    grid = plan_pose_grid(card, frames_per_clip=frames_per_clip)
    total = sum(g["frame_count"] for g in grid)
    if total > settings.max_frames_per_job:
        raise ValueError(
            f"本任务需要 {total} 帧，超过上限 {settings.max_frames_per_job}。"
            "请在 .env 调整 MAX_FRAMES_PER_JOB，或减少方向/动作数量。"
        )

    size = (512, 512)
    frames: list[FrameAsset] = []
    done = 0

    for cell in grid:
        direction: Direction = cell["direction"]
        action: ActionName = cell["action"]
        clip_name: str = cell["clip_name"]
        count: int = cell["frame_count"]

        positive, negative = build_prompts(card, style, direction, action)
        clip_dir = out_dir / clip_name
        clip_dir.mkdir(parents=True, exist_ok=True)

        for i in range(count):
            path = clip_dir / f"{clip_name}_{i:02d}.png"
            seed = _seed_for(clip_name, i)

            if backend == "mock":
                img = _draw_mock_character(card, direction, action, i, count, size)
                img.save(path)
            elif backend == "local":
                _generate_local(positive, negative, path, control_image=None, seed=seed, size=size)
            elif backend == "cloud":
                _generate_cloud(positive, negative, path, size=size)
            else:
                raise ValueError(f"未知后端: {backend}")

            frames.append(
                FrameAsset(
                    direction=direction,
                    action=action,
                    index=i,
                    path=str(path),
                    width=size[0],
                    height=size[1],
                )
            )

            done += 1
            if progress:
                progress(100.0 * done / total, f"生成 {clip_name} 第 {i + 1}/{count} 帧")

    return frames
