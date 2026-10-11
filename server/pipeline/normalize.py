"""[5] Normalize：归一化。这一步直接决定「能不能用」。

不归一化会发生什么：
- 角色在画布里位置飘忽 → 播放动画时抽搐；
- 每帧角色大小不一致 → 走路时忽大忽小；
- 锚点不在脚底 → 角色悬空或陷进地面；
- 像素风没对齐像素网格 → 移动时边缘抖动。

归一化要做四件事：
    1. 按 alpha 裁切（trim）
    2. **统一缩放**（关键：所有帧用同一个 scale，不能逐帧自适应）
    3. 放到统一画布、锚点对齐（脚底居中）
    4. 可选：量化到像素网格（像素风必做）
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

ALPHA_THRESHOLD = 8  # alpha 低于此值视为透明


def _trim(img: Image.Image) -> Image.Image:
    """按 alpha 通道裁掉四周全透明区域。"""
    alpha = np.asarray(img.getchannel("A"))
    mask = alpha > ALPHA_THRESHOLD
    if not mask.any():
        return img
    rows = np.any(mask, axis=1)
    cols = np.any(mask, axis=0)
    top, bottom = int(np.argmax(rows)), int(len(rows) - np.argmax(rows[::-1]))
    left, right = int(np.argmax(cols)), int(len(cols) - np.argmax(cols[::-1]))
    return img.crop((left, top, right, bottom))


def _quantize_to_grid(img: Image.Image, grid: int) -> Image.Image:
    """量化到像素网格：缩小到 1/grid 分辨率再最近邻放大回来。

    grid 是「一个像素占多少画面像素」。
    - 像素风（如 64x64 素材放大到 512）：grid = 8
    - 高清手绘：不要调用这个函数
    """
    if grid <= 1:
        return img
    w, h = img.size
    small = img.resize((max(1, w // grid), max(1, h // grid)), Image.Resampling.BOX)
    return small.resize((w, h), Image.Resampling.NEAREST)


def normalize_frames(
    paths: list[str],
    out_dir: Path,
    canvas: tuple[int, int] = (512, 512),
    margin_ratio: float = 0.08,
    pixel_grid: int = 1,
) -> list[str]:
    """批量归一化。所有帧共用同一 scale（这是关键，别逐帧自适应）。"""
    out_dir.mkdir(parents=True, exist_ok=True)

    trimmed: list[Image.Image] = []
    for p in paths:
        with Image.open(p) as im:
            trimmed.append(_trim(im.convert("RGBA")))

    if not trimmed:
        return []

    cw, ch = canvas
    usable_w = cw * (1 - margin_ratio * 2)
    usable_h = ch * (1 - margin_ratio * 2)

    # 统一 scale：以「最大的那一帧」为基准，保证谁都不会超出画布
    max_w = max(im.width for im in trimmed)
    max_h = max(im.height for im in trimmed)
    scale = min(usable_w / max_w, usable_h / max_h)

    out_paths: list[str] = []
    for src_path, im in zip(paths, trimmed, strict=True):
        new_w = max(1, int(round(im.width * scale)))
        new_h = max(1, int(round(im.height * scale)))
        resized = im.resize((new_w, new_h), Image.Resampling.LANCZOS)

        canvas_img = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
        # 水平居中；垂直「脚底对齐」——脚底贴到画布底部留出下边距
        x = (cw - new_w) // 2
        y = ch - int(ch * margin_ratio) - new_h
        canvas_img.paste(resized, (x, y), resized)

        if pixel_grid > 1:
            canvas_img = _quantize_to_grid(canvas_img, pixel_grid)

        out_path = out_dir / Path(src_path).name
        canvas_img.save(out_path)
        out_paths.append(str(out_path))

    return out_paths


def suggest_pixel_grid(canvas: tuple[int, int], target_resolution: int = 64) -> int:
    """根据画布与目标像素分辨率，推荐 pixel_grid。

    例：512 画布、目标 64x64 像素风 → grid = 8。
    """
    return max(1, round(canvas[0] / target_resolution))
