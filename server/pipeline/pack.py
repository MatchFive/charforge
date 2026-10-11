"""[6] Pack：打包成 sprite sheet + 切片元数据。

Unity 侧需要的不只是图，还有「怎么切」。
这里产出的 SpriteManifest 会被 Editor/SpriteImportSetup.cs 读走，
自动写进 .meta 的 Sprite Mode = Multiple 切片列表——
**这就是「导入设置自动生成」这件事的落地方式**。
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

from server.config import settings
from server.models.schemas import (
    ActionName,
    CharacterBounds,
    Direction,
    FrameAsset,
    SpriteManifest,
    SpriteRect,
    SpriteSheetInfo,
)

ALPHA_THRESHOLD = 8  # alpha 低于此值视为透明，与 normalize 段保持一致


def measure_character_bounds(
    paths: list[str],
    cell: tuple[int, int],
    pixels_per_unit: int = 100,
) -> CharacterBounds | None:
    """实测角色可视范围（世界单位，相对脚底锚点）。

    为什么要实测而不是写死：碰撞体一旦不贴合角色，自动装配出来的 prefab 就是废的——
    要么拦不住玩家（穿模），要么挡住半条街（卡住）。而归一化之后的帧已经统一了比例与锚点，
    此时量一次就是准的。

    坐标为「所有帧 alpha 包围盒的并集」，取并集而非单帧，是为了让待机/行走的
    碰撞体保持一致（逐帧变化的碰撞体会让物理表现抖动）。
    """
    if not paths:
        return None

    cw, ch = cell
    left, top, right, bottom = cw, ch, 0, 0
    measured = False

    for p in paths:
        try:
            with Image.open(p) as im:
                alpha = np.asarray(im.convert("RGBA").getchannel("A"))
        except Exception:  # noqa: BLE001 —— 单帧损坏不该让整单失败
            continue

        mask = alpha > ALPHA_THRESHOLD
        if not mask.any():
            continue
        rows = np.any(mask, axis=1)
        cols = np.any(mask, axis=0)
        t, b = int(np.argmax(rows)), int(len(rows) - np.argmax(rows[::-1]))
        l, r = int(np.argmax(cols)), int(len(cols) - np.argmax(cols[::-1]))

        left, top = min(left, l), min(top, t)
        right, bottom = max(right, r), max(bottom, b)
        measured = True

    if not measured:
        return None

    # 图像坐标是「左上原点、y 向下」，而锚点在画布底部中线（pivot_y = 0）。
    # 换算到 Unity 的「以锚点为原点、y 向上、单位 = 世界单位」：
    px_to_unit = 1.0 / max(1, pixels_per_unit)
    width = (right - left) * px_to_unit
    height = (bottom - top) * px_to_unit
    center_x = ((left + right) / 2.0 - cw / 2.0) * px_to_unit
    center_y = (ch - (top + bottom) / 2.0) * px_to_unit

    return CharacterBounds(
        width=round(width, 4),
        height=round(height, 4),
        center_x=round(center_x, 4),
        center_y=round(center_y, 4),
    )


def _group_key(f: FrameAsset) -> str:
    return f"{f.direction.value}_{f.action.value}"


def pack_sheet(
    frames: list[FrameAsset],
    out_path: Path,
    cell: tuple[int, int] = (512, 512),
    columns: int = 4,
) -> tuple[SpriteSheetInfo, list[SpriteRect]]:
    """按网格拼 sheet，同时算出每个切片的矩形与锚点。

    布局：一行一个 clip（如 south_walk），跨行排列。
    这样切片名可读，Unity 里找帧也直观。
    """
    # 按 clip 分组，保持稳定顺序（方向 -> 动作），避免每次任务顺序抖动
    groups: dict[str, list[FrameAsset]] = {}
    for f in frames:
        groups.setdefault(_group_key(f), []).append(f)
    for v in groups.values():
        v.sort(key=lambda x: x.index)

    keys = sorted(groups.keys(), key=lambda k: (list(Direction).index(Direction(k.split("_")[0])), k))
    rows = len(keys)
    cell_w, cell_h = cell

    sheet = Image.new("RGBA", (cell_w * columns, cell_h * rows), (0, 0, 0, 0))
    rects: list[SpriteRect] = []

    for r, key in enumerate(keys):
        items = groups[key]
        for c, f in enumerate(items):
            if c >= columns:
                break  # 超过列数的帧不放进 sheet（请调大 columns 或减小 frames）
            x, y = c * cell_w, r * cell_h
            with Image.open(f.path) as im:
                im = im.convert("RGBA")
                if im.size != cell:
                    im = im.resize(cell, Image.Resampling.LANCZOS)
                sheet.paste(im, (x, y), im)

            direction, action = key.split("_", 1)
            rects.append(
                SpriteRect(
                    name=f"{key}_{f.index:02d}",
                    x=x,
                    # Unity 的纹理坐标原点在左下，需从左上翻转换算
                    y=(rows - r - 1) * cell_h,
                    w=cell_w,
                    h=cell_h,
                    pivot_x=0.5,
                    pivot_y=0.0,  # 脚底
                )
            )

    out_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out_path)

    info = SpriteSheetInfo(
        path=str(out_path),
        cell_width=cell_w,
        cell_height=cell_h,
        columns=columns,
        rows=rows,
        frame_count=len(frames),
    )
    return info, rects


def build_manifest(
    job_id: str,
    sheet: SpriteSheetInfo,
    rects: list[SpriteRect],
    pixels_per_unit: int = 100,
    pixel_art: bool = False,
    frame_paths: list[str] | None = None,
) -> SpriteManifest:
    """组装清单。pixel_art=True 时用 Point 过滤，避免像素风被插值糊掉。

    传入 frame_paths 时会实测角色包围盒并写进 manifest——
    这是「碰撞体贴合角色」的前提，缺了它 Unity 侧只能拍脑袋填尺寸。
    """
    bounds = None
    if frame_paths:
        bounds = measure_character_bounds(
            frame_paths, (sheet.cell_width, sheet.cell_height), pixels_per_unit
        )

    return SpriteManifest(
        job_id=job_id,
        sheet=sheet,
        sprites=rects,
        pixels_per_unit=pixels_per_unit,
        filter_mode="Point" if pixel_art else "Bilinear",
        character_bounds=bounds,
    )


def save_manifest(manifest: SpriteManifest, out_dir: Path) -> Path:
    path = out_dir / "manifest.json"
    path.write_text(manifest.model_dump_json(indent=2), encoding="utf-8")
    return path


def default_pack_dir(job_id: str) -> Path:
    return settings.output_path / job_id
