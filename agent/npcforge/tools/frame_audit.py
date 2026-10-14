#!/usr/bin/env python3
"""帧序列的 alpha 体检：透明率、半透明噪点、空帧、包围盒一致性。

为什么单独做一件事：keep_pose 的输出有**两种互不相同的坏法**，
症状都只是"图看着怪"，而它们要用的修法完全相反——

| 症状 | 数据形态 | 修法 |
|---|---|---|
| 背景没抠开 | 不透明 ≈ 100%，无 alpha 通道 | 查工作流是否丢了透明背景提示词句 |
| 背景有灰雾 | 半透明像素占 5%~50% | 收割时没做 alpha<64 压平 |

只看预览图，这两种都是"背景有点脏"，分不出来。所以这里把它们分别量出来。

用法：
    python alpha_report.py <目录或单张 PNG> [--json] [--cutoff 64]
退出码：0 全部通过；1 有帧不合格。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .png_io import Png, PngError, alpha_stats, read_png  # noqa: E402


def _natural(p: Path) -> list:
    """自然序键：frame-2 必须排在 frame-10 前面。纯字典序会倒着播动画。"""
    name = p.name.lower()
    parts = []
    num = ""
    for ch in name:
        if ch.isdigit():
            num += ch
        elif num:
            parts.append((1, int(num), ""))
            num = ""
            parts.append((0, 0, ch))
        else:
            parts.append((0, 0, ch))
    if num:
        parts.append((1, int(num), ""))
    return parts


def frame_files(target: Path) -> list[Path]:
    if target.is_file():
        return [target]
    if target.is_dir():
        files = sorted(target.glob("*.png"), key=_natural)
        if files:
            return files
        # 也接受「角色产物目录」：Frames/<动作>_<朝向>/*.png
        return sorted(target.glob("Frames/*/frame-*.png"))
    return []


def bbox(png: Png, cutoff: int = 64) -> tuple[int, int, int, int] | None:
    """不透明区域（alpha ≥ cutoff）的包围盒。全透明返回 None。

    走切片 + 整行快路径：整行最大值都不到 cutoff 就直接跳过，
    于是绝大多数"角色不在的那几行"是 C 层一次 `max()` 解决的，
    只有角色真正占的行才逐像素找边界。1024² 的图从十几秒降到零点几秒。
    """
    if png.channels == 4:
        alpha = bytes(png.pixels[3::4])
    elif png.channels == 2:
        alpha = bytes(png.pixels[1::2])
    else:
        return (0, 0, png.width - 1, png.height - 1)

    w, h = png.width, png.height
    min_x, min_y, max_x, max_y = w, h, -1, -1

    for y in range(h):
        row = alpha[y * w:(y + 1) * w]
        if max(row, default=0) < cutoff:
            continue
        if y < min_y:
            min_y = y
        max_y = y
        for x in range(w):
            a = row[x]
            if a >= cutoff:
                if x < min_x:
                    min_x = x
                if x > max_x:
                    max_x = x

    return None if max_x < 0 else (min_x, min_y, max_x, max_y)


def inspect(path: Path, cutoff: int) -> dict:
    try:
        png = read_png(path)
    except PngError as e:
        return {"file": path.name, "error": str(e), "hard_fail": True}

    stats = alpha_stats(png)
    box = bbox(png, cutoff)
    notes: list[str] = []

    if not stats["has_alpha"]:
        notes.append("没有 alpha 通道——背景压根没透明（查工作流是否丢了透明背景提示词句）")
    elif stats["clear_pct"] < 5:
        notes.append(f"完全透明只有 {stats['clear_pct']}%——背景基本没抠开")
    if stats["semi_pct"] > 2:
        notes.append(f"半透明像素 {stats['semi_pct']}%——收割时漏了 alpha<{cutoff} 压平")
    if stats["opaque_pct"] < 0.5:
        notes.append("几乎没有不透明像素——空帧或去背景去过了头")

    return {
        "file": path.name,
        "size": f"{png.width}x{png.height}",
        "clear_pct": stats["clear_pct"],
        "semi_pct": stats["semi_pct"],
        "opaque_pct": stats["opaque_pct"],
        "bbox": box,
        "notes": notes,
    }


def main() -> int:
    ap = argparse.ArgumentParser(description="帧序列 alpha 体检")
    ap.add_argument("target", help="目录（含 PNG）或单张 PNG")
    ap.add_argument("--cutoff", type=int, default=64)
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    files = frame_files(Path(args.target))
    if not files:
        print(f"[失败] {args.target} 里没找到 PNG。", file=sys.stderr)
        return 1

    rows = [inspect(p, args.cutoff) for p in files]

    sizes = {r.get("size") for r in rows if "size" in r}
    bad = [r for r in rows if r.get("hard_fail") or r.get("notes")]

    if args.json:
        print(json.dumps({"frames": rows, "size_uniform": len(sizes) <= 1,
                          "bad": len(bad)}, ensure_ascii=False, indent=2))
        return 1 if bad else 0

    print(f"alpha 体检：{len(rows)} 帧，阈值 alpha<{args.cutoff} 视为透明")
    print(f"{'帧':<16}{'尺寸':<12}{'全透明%':>9}{'半透明%':>9}{'不透明%':>9}  备注")
    for r in rows:
        if r.get("hard_fail"):
            print(f"{r['file']:<16}{'—':<12}{'—':>9}{'—':>9}{'—':>9}  {r.get('error')}")
            continue
        print(f"{r['file']:<16}{r['size']:<12}{r['clear_pct']:>9}{r['semi_pct']:>9}"
              f"{r['opaque_pct']:>9}  {'；'.join(r['notes']) if r['notes'] else '✓'}")

    if len(sizes) > 1:
        print(f"\n[警告] 帧尺寸不一致：{sorted(sizes)}——尺寸统一是 pivot 对齐的前提。")
    if bad:
        print(f"\n[失败] {len(bad)} 帧有问题。半透明噪点那一类，用收割流程（comfy_client frames）"
              "重跑即可自动压平；背景全实心那一类要去查工作流的提示词。", file=sys.stderr)
        return 1
    print("\n[通过] 全部帧的透明背景与噪点都在正常范围。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
