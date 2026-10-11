"""[6] Pack：打包成 sprite sheet + 切片元数据。

Unity 侧需要的不只是图，还有「怎么切」。
这里产出的 SpriteManifest 会被 Editor/SpriteImportSetup.cs 读走，
自动写进 .meta 的 Sprite Mode = Multiple 切片列表——
**这就是「导入设置自动生成」这件事的落地方式**。
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image

from server.config import settings
from server.models.schemas import (
    ActionName,
    Direction,
    FrameAsset,
    SpriteManifest,
    SpriteRect,
    SpriteSheetInfo,
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
) -> SpriteManifest:
    """组装清单。pixel_art=True 时用 Point 过滤，避免像素风被插值糊掉。"""
    return SpriteManifest(
        job_id=job_id,
        sheet=sheet,
        sprites=rects,
        pixels_per_unit=pixels_per_unit,
        filter_mode="Point" if pixel_art else "Bilinear",
    )


def save_manifest(manifest: SpriteManifest, out_dir: Path) -> Path:
    path = out_dir / "manifest.json"
    path.write_text(manifest.model_dump_json(indent=2), encoding="utf-8")
    return path


def default_pack_dir(job_id: str) -> Path:
    return settings.output_path / job_id
