#!/usr/bin/env python3
"""把一个目录里的 PNG **水平镜像**后输出——2D 流水里"左右朝向"的标配操作。

为什么需要它：姿势参考图库与生成产物都可能出现「标 left、画的却是向右走」
（原始关键帧素材本身朝右，命名先入为主）。水平镜像不损画质（PNG 无损）、
不改变帧序（步态周期镜像后仍按 k1→k8 循环），是左右朝向互换的正解：
    * 参考图：mirror_frames.py <朝右参考目录> <朝左参考目录>   # A → B
    * 生成帧：mirror_frames.py <帧目录> --in-place --backup <备份目录>

alpha 原样保留；只处理 *.png（Unity 的 .meta 不会被碰，GUID 稳定，
动画片段里的关键帧引用在原地替换后依然有效）。
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

from PIL import Image, ImageOps


def mirror_png(src: Path, dst: Path) -> tuple[str, tuple[int, int]]:
    """水平镜像一张 PNG 并写到 dst。返回 (色彩模式, 尺寸) 供日志核对。"""
    with Image.open(src) as im:
        # ImageOps.mirror = 左右翻转（flip 上下翻，别用错）。
        flipped = ImageOps.mirror(im)
        dst.parent.mkdir(parents=True, exist_ok=True)
        flipped.save(dst)
        return im.mode, im.size


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="水平镜像目录下的全部 PNG")
    ap.add_argument("src", help="源目录")
    ap.add_argument("dst", nargs="?", help="目标目录（不镜像回原目录时给这个）")
    ap.add_argument("--in-place", action="store_true", help="原地覆盖源目录里的 PNG")
    ap.add_argument("--backup", help="原地覆盖时，先把原件拷进这个目录（留档/可回退）")
    args = ap.parse_args(argv[1:])

    src = Path(args.src).expanduser()
    if not src.is_dir():
        print(f"[错误] 源目录不存在：{src}")
        return 2
    if args.in_place and args.dst:
        print("[错误] --in-place 与目标目录二选一")
        return 2
    if not args.in_place and not args.dst:
        print("[错误] 要么给目标目录，要么加 --in-place")
        return 2

    files = sorted(p for p in src.glob("*.png") if p.is_file())
    if not files:
        print(f"[错误] {src} 下没有 PNG")
        return 2

    backup = Path(args.backup).expanduser() if args.backup else None
    if args.in_place and backup:
        backup.mkdir(parents=True, exist_ok=True)

    for f in files:
        if args.in_place:
            dst = f
            if backup:
                shutil.copy2(f, backup / f.name)   # 先留档再覆盖，可回退
        else:
            dst = Path(args.dst).expanduser() / f.name
        mode, size = mirror_png(f, dst)
        where = "原地" if args.in_place else str(dst)
        print(f"  {f.name}  {mode} {size[0]}x{size[1]}  → 镜像写入 {where}")

    n = len(files)
    print(f"完成：{n} 张 PNG 已水平镜像。")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
