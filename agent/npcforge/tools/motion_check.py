#!/usr/bin/env python3
"""帧序列的「动没动」量化报告。

存在的理由：走路动画最阴的失败不是"图坏了"，而是**图看着没坏、但它是站着的**。
背面/正面朝向尤其容易——SAM 从背/正面参考估出的肢体摆幅天然偏小
（只有侧面的 1/3~1/4，2D 投影里前后向位移本来就弱），弱信号到了扩散端
会被"站姿先验"吞掉。人眼盯着 8 张相似的图，很难说清"到底动没动"。

这里给出三个可以对比的数字：

* `mean_diff` —— 相邻帧的逐像素平均绝对差。**帧差掉了就是真的没动**，
  实测：正常走路的帧间差 ≥ 10，被站姿先验吞掉时只有 1.6~3.8。
* `width_range` / `center_range` —— 各帧角色包围盒的宽度 / 中心横向变化范围。
  原地走路的横向轮廓会明显起伏（实测有效时 ≥ 体高的 2%，被吞掉时只有 6~8 px / 体高 920 px ≈ 0.8%）。

用法：
    python stride_report.py <目录> [--json] [--min-diff 8] [--min-span 0.02]
退出码：0 判定为"有动作"；1 判定为"基本静止"。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .png_io import Png, PngError, read_png  # noqa: E402


def frame_files(target: Path) -> list[Path]:
    if target.is_file():
        return [target]
    if target.is_dir():
        files = sorted(target.glob("frame-*.png"))
        if files:
            return files
        files = sorted(target.glob("*.png"))
        if files:
            return files
        return sorted(target.glob("Frames/*/frame-*.png"))
    return []


def opaque_mask(png: Png, cutoff: int = 64) -> bytes:
    """1 字节/像素的不透明掩码（非 0 = 不透明）。"""
    if png.channels == 4:
        return bytes(1 if a >= cutoff else 0 for a in png.pixels[3::4])
    if png.channels == 2:
        return bytes(1 if a >= cutoff else 0 for a in png.pixels[1::2])
    return b"\x01" * (png.width * png.height)


def luminance(png: Png) -> bytes:
    """把像素压成 1 字节/像素的亮度，用于算帧间差（无 alpha 的图也适用）。"""
    px = png.pixels
    if png.channels == 4:
        return bytes((px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) // 1000
                     for i in range(0, len(px), 4))
    if png.channels == 3:
        return bytes((px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) // 1000
                     for i in range(0, len(px), 3))
    if png.channels == 2:
        return bytes(px[0::2])
    return bytes(px)


def bbox_of(mask: bytes, w: int, h: int):
    min_x, min_y, max_x, max_y = w, h, -1, -1
    for y in range(h):
        row = mask[y * w:(y + 1) * w]
        if not any(row):
            continue
        if y < min_y:
            min_y = y
        max_y = y
        for x in range(w):
            if row[x]:
                if x < min_x:
                    min_x = x
                if x > max_x:
                    max_x = x
    return None if max_x < 0 else (min_x, min_y, max_x, max_y)


def analyze(files: list[Path], cutoff: int = 64) -> dict:
    pngs = []
    for path in files:
        try:
            pngs.append((path, read_png(path)))
        except PngError as e:
            return {"error": f"读不了 {path.name}：{e}"}

    if len(pngs) < 2:
        return {"error": "至少要两帧才能算帧间差。"}

    w, h = pngs[0][1].width, pngs[0][1].height
    for path, png in pngs:
        if (png.width, png.height) != (w, h):
            return {"error": f"{path.name} 的尺寸 {png.width}x{png.height} 与首帧 {w}x{h} 不一致。"}

    lums = [luminance(png) for _, png in pngs]
    masks = [opaque_mask(png, cutoff) for _, png in pngs]

    diffs = []
    for i in range(len(lums) - 1):
        a, b = lums[i], lums[i + 1]
        ma, mb = masks[i], masks[i + 1]
        total = 0
        count = 0
        for j in range(len(a)):
            if ma[j] or mb[j]:
                total += abs(a[j] - b[j])
                count += 1
        diffs.append(round(total / count, 2) if count else 0.0)

    boxes = [bbox_of(m, w, h) for m in masks]
    valid = [b for b in boxes if b]
    if not valid:
        return {"error": "所有帧都是空的（无非透明像素）。"}

    widths = [b[2] - b[0] + 1 for b in valid]
    centers = [(b[0] + b[2]) / 2.0 for b in valid]
    heights = [b[3] - b[1] + 1 for b in valid]
    body_h = max(heights) or 1

    return {
        "frames": len(pngs),
        "size": f"{w}x{h}",
        "body_height": body_h,
        "mean_diff": round(sum(diffs) / len(diffs), 2) if diffs else 0.0,
        "min_diff": min(diffs) if diffs else 0.0,
        "max_diff": max(diffs) if diffs else 0.0,
        "width_range": max(widths) - min(widths),
        "width_range_ratio": round((max(widths) - min(widths)) / body_h, 4),
        "center_range": round(max(centers) - min(centers), 1),
        "center_range_ratio": round((max(centers) - min(centers)) / body_h, 4),
        # strict=True：boxes 是逐张 pngs 算出来的，两者长度不等就是上游的 bug，
        # 静默截短会得到一份"少了几帧"的报告——而报告正是裁决的依据。
        "boxes": [{"file": p.name, "bbox": b} for (p, _), b in zip(pngs, boxes, strict=True)],
    }


def judge(report: dict, min_diff: float, min_span: float) -> tuple[bool, list[str]]:
    if "error" in report:
        return False, [report["error"]]

    notes = []
    ok = True

    if report["mean_diff"] < min_diff:
        ok = False
        notes.append(f"帧间平均差只有 {report['mean_diff']}（判据 ≥{min_diff}）"
                     "——这就是「人偶在跨步、成图站桩」的数值形态。"
                     "先查姿势复制提示词是否生效，再考虑加大姿势放大系数。"
                     "（顺带排除一个常见误判：换 seed 会让帧差自然变化，"
                     "但不会从 3 变到 12。）")

    span = max(report["width_range_ratio"], report["center_range_ratio"])
    if span < min_span:
        ok = False
        notes.append(f"横向轮廓变化 {span:.3f}×体高（判据 ≥{min_span}）"
                     "——轮廓几乎不变，说明各帧姿态高度雷同。")

    if not ok:
        notes.append("排查顺序：① 提示词三要素是否齐（尤其姿势强化句）；"
                     "② 该朝向的 pose_amplify 是否 >1；③ 姿势参考图是不是本身就没在跨步。")
    return ok, notes


def main() -> int:
    ap = argparse.ArgumentParser(description="帧序列动作幅度报告")
    ap.add_argument("target")
    ap.add_argument("--cutoff", type=int, default=64)
    ap.add_argument("--min-diff", type=float, default=8.0)
    ap.add_argument("--min-span", type=float, default=0.02)
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    files = frame_files(Path(args.target))
    if not files:
        print(f"[失败] {args.target} 里没找到帧 PNG。", file=sys.stderr)
        return 1

    report = analyze(files, args.cutoff)
    ok, notes = judge(report, args.min_diff, args.min_span)

    if args.json:
        print(json.dumps({**report, "ok": ok, "notes": notes}, ensure_ascii=False, indent=2))
        return 0 if ok else 1

    if "error" in report:
        print(f"[失败] {report['error']}", file=sys.stderr)
        return 1

    print(f"动作幅度：{report['frames']} 帧，{report['size']}，体高 {report['body_height']} px")
    print(f"  帧间平均差   {report['mean_diff']}（区间 {report['min_diff']}~{report['max_diff']}）")
    print(f"  轮廓宽变化   {report['width_range']} px = {report['width_range_ratio']:.3f}×体高")
    print(f"  中心横移     {report['center_range']} px = {report['center_range_ratio']:.3f}×体高")
    for note in notes:
        print(f"  {'✓' if ok else '✗'} {note}")
    print("[通过] 判定为有动作。" if ok else "[失败] 判定为基本静止。")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
