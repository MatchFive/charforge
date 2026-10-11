"""[4] Matting：抠图，产出干净透明 PNG。

两个必须处理的现实问题：
1. 生成图的背景往往「不干净」（半透明渐变、伪影边缘）——直接键控会留下毛边；
2. 2D 游戏要求**干净 alpha**，边缘糊一点在放大后就很明显。

策略：优先 rembg（U2Net，本地运行）；不可用时降级到背景色键控——
mock 后端用纯色背景，降级版恰好能跑通，方便你零依赖验证链路。
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

_BG_KEY_TOLERANCE = 40  # 背景色键控的容差（0-255）


def _cutout_fallback(src: Path, dst: Path) -> Path:
    """背景色键控：取四角像素的中位数当背景色，按色彩距离生成 alpha。

    对纯色/近纯色背景有效（mock 后端就是纯色）；对真实生成图效果一般。
    真实生成图请务必用 rembg。
    """
    img = Image.open(src).convert("RGB")
    arr = np.asarray(img).astype(np.int16)

    h, w, _ = arr.shape
    corners = np.array(
        [
            arr[0, 0],
            arr[0, w - 1],
            arr[h - 1, 0],
            arr[h - 1, w - 1],
        ]
    )
    bg = np.median(corners, axis=0)

    dist = np.sqrt(((arr - bg) ** 2).sum(axis=2))
    alpha = np.clip((dist - _BG_KEY_TOLERANCE) / _BG_KEY_TOLERANCE * 255.0, 0, 255).astype(np.uint8)

    rgba = np.dstack([arr.astype(np.uint8), alpha])
    Image.fromarray(rgba, mode="RGBA").save(dst)
    return dst


def cutout(src: str | Path, dst: str | Path | None = None) -> tuple[Path, str]:
    """抠图。返回 (输出路径, 实际使用的方法)。"""
    src = Path(src)
    dst = Path(dst) if dst else src.with_name(src.stem + "_cutout.png")
    dst.parent.mkdir(parents=True, exist_ok=True)

    try:
        from rembg import remove  # type: ignore

        with Image.open(src) as im:
            result = remove(im.convert("RGBA"))
        result.save(dst)
        return dst, "rembg"
    except ImportError:
        return _cutout_fallback(src, dst), "fallback-color-key"
    except Exception as exc:  # rembg 装上了但模型下载失败等
        print(f"[matting] rembg 失败（{exc}），降级到色键")
        return _cutout_fallback(src, dst), "fallback-color-key"


def cutout_batch(paths: list[str]) -> tuple[list[str], str]:
    """批量抠图。返回 (新路径列表, 使用的方法)。"""
    out: list[str] = []
    method = "rembg"
    for p in paths:
        new_path, m = cutout(p)
        method = m
        out.append(str(new_path))
    return out, method
