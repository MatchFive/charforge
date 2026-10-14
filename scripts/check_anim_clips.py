#!/usr/bin/env python3
"""检查装配产物的**真相**：每个动画片段到底有没有帧。

为什么需要它：一次真实事故里（阿茹 walk_down / walk_left 空片段），控制器里状态一个不少、
Inspector 上 motion 也挂着，但片段文件里 **0 条精灵关键帧**——播不动，而且全程不报错。
写片段的那一层曾按内存对象自检，内存里 8 条键、磁盘上 0 条键，守卫照样放行。
**只有读磁盘数得出来。** 这个脚本就是那只眼睛。

用法：
    python scripts/check_anim_clips.py <角色产物目录>

    # 例（Unity 工程里的产物）：
    python scripts/check_anim_clips.py F:/unity_project/plugin/Assets/CharForge/阿茹

判定：
    片段的精灵关键帧数 == 同组帧 PNG 数   → ✓
    0 条关键帧                           → ✗ 空片段（装配失败，prefab 里这条动画播不出来）
    两者不等                             → ！帧数与 PNG 数不符，值得一查

退出码：全部 ✓ 为 0，否则 1（可以挂进 CI，或装配之后自检一遍）。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# 片段文件里一条关键帧长这样：`      - time: 0.125`（m_PPtrCurves 下）
KEYFRAME_RE = re.compile(r"^\s*-\s*time:\s*[\d.eE+-]+\s*$", re.MULTILINE)
LOOP_RE = re.compile(r"^\s*m_LoopTime:\s*(\d)\s*$", re.MULTILINE)


def count_keyframes(anim_text: str) -> int:
    """数精灵关键帧条数。生成的片段里 m_EditorCurves 恒空，所以数 time 条目即可。"""
    return len(KEYFRAME_RE.findall(anim_text))


def is_looping(anim_text: str) -> bool:
    m = LOOP_RE.search(anim_text)
    return bool(m and m.group(1) == "1")


def frame_png_counts(frames_dir: Path) -> dict[str, int]:
    """Frames/<动作>_<朝向>/ 下各有多少张 frame-N.png。"""
    counts: dict[str, int] = {}
    if not frames_dir.is_dir():
        return counts
    for group in sorted(p for p in frames_dir.iterdir() if p.is_dir()):
        # 只算帧图，别把 .meta 算进去
        counts[group.name] = len(
            [p for p in group.glob("frame-*.png") if p.suffix == ".png"]
        )
    return counts


def check(asset_dir: Path) -> int:
    frames_dir = asset_dir / "Frames"
    anim_dir = asset_dir / "Animations"

    if not asset_dir.is_dir():
        print(f"[错误] 目录不存在：{asset_dir}")
        return 2
    if not anim_dir.is_dir():
        print(f"[错误] 没有 Animations/ 目录：{asset_dir}")
        return 2

    png_counts = frame_png_counts(frames_dir)
    print(f"角色产物目录：{asset_dir}")
    if png_counts:
        print("帧 PNG：" + "  ".join(f"{k}={v}张" for k, v in png_counts.items()))
    else:
        print(f"帧 PNG：（{frames_dir} 下没有分组）")

    clips = sorted(anim_dir.glob("*.anim"))
    if not clips:
        print("[错误] Animations/ 下没有 .anim 片段")
        return 2

    bad: list[str] = []
    warn: list[str] = []
    print("片段：")
    for clip in clips:
        text = clip.read_text(encoding="utf-8", errors="replace")
        keys = count_keyframes(text)
        loop = is_looping(text)
        expected = png_counts.get(clip.stem)

        if keys == 0:
            mark, note = "✗ 空片段（prefab 里这条动画播不出来）", bad
        elif expected is not None and keys != expected:
            mark, note = f"！关键帧 {keys} ≠ PNG {expected}", warn
        else:
            mark, note = "✓", None
        if note is not None:
            note.append(f"{clip.name} 关键帧 {keys}")
        print(f"  {clip.name:<20} 关键帧 {keys:<4} {'循环' if loop else '一次性'}  {mark}")

    total = len(clips)
    if bad:
        print("")
        print(f"结论：{total} 个片段里有 {len(bad)} 个是空的 —— 装配失败（帧没写进片段）。")
        print("      先确认帧 PNG 与 .meta 都在（textureType: 8 / spriteMode: 1），")
        print("      然后重开第 3 步「组装 prefab」：帧还在磁盘上就能就地修好，不必重跑生成。")
        return 1
    if warn:
        print("")
        print(f"结论：{total} 个片段都有帧，但有 {len(warn)} 个帧数与 PNG 数不符 —— 值得一查。")
        return 1
    print("")
    print(f"结论：{total} 个片段全部有关键帧，且与帧 PNG 数一致 —— 装配通过。")
    return 0


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__)
        return 2
    return check(Path(argv[1]).expanduser())


if __name__ == "__main__":
    sys.exit(main(sys.argv))
