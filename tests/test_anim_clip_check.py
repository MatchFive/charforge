"""装配产物检查脚本（`scripts/check_anim_clips.py`）的单元测试。

它存在的理由就是"那个坏法曾经没人看得见"，所以它必须先能**看见**坏法，
又不能在好产物上误报——否则这条防线等于没有，还会让正常产物背锅。

用真实踩过的两种片段文件形状做夹具：
  * 空片段：`m_PPtrCurves: []`、`m_LoopTime: 0`（事故里的 walk_down / walk_left）
  * 正常片段：`m_PPtrCurves` 下 N 条 time、`m_LoopTime: 1`
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

from check_anim_clips import (  # noqa: E402
    check,
    count_keyframes,
    frame_png_counts,
    is_looping,
)

HEAD = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!74 &7400000
AnimationClip:
  m_Name: {name}
  serializedVersion: 8
  m_PPtrCurves:{curves}
  m_SampleRate: 8
{m_editor}
  m_LoopTime: {loop}
  m_EditorCurves: []
"""


def empty_clip(name: str) -> str:
    """CreateAsset 刚写完那一刻的形状：没有曲线、循环位还是默认 0。"""
    return HEAD.format(name=name, curves=" []", m_editor="", loop=0)


def full_clip(name: str, count: int) -> str:
    keys = "\n".join(
        f"    - time: {i / 8:g}\n"
        f"      value: {{fileID: 21300000, guid: {'0' * 31}{i}, type: 3}}"
        for i in range(count)
    )
    return HEAD.format(
        name=name,
        curves="\n" + "  - serializedVersion: 2\n    curve:\n" + keys,
        m_editor="",
        loop=1,
    )


def make_asset_dir(root: Path, clip_keys: dict[str, int], frames: dict[str, int]) -> Path:
    """按「片段名 → 关键帧数」「组名 → PNG 张数」铺一个产物目录。"""
    anim = root / "Animations"
    anim.mkdir(parents=True, exist_ok=True)
    for name, keys in clip_keys.items():
        text = empty_clip(name) if keys == 0 else full_clip(name, keys)
        (anim / f"{name}.anim").write_text(text, encoding="utf-8")

    for group, n in frames.items():
        gdir = root / "Frames" / group
        gdir.mkdir(parents=True, exist_ok=True)
        for i in range(1, n + 1):
            (gdir / f"frame-{i}.png").write_bytes(b"\x89PNG")
            (gdir / f"frame-{i}.png.meta").write_text("guid: x\n", encoding="utf-8")
    return root


# ── 解析层 ──


def test_count_keyframes_sees_empty_and_full():
    assert count_keyframes(empty_clip("walk_up")) == 0
    assert count_keyframes(full_clip("walk_up", 8)) == 8
    assert count_keyframes(full_clip("walk_up", 3)) == 3


def test_is_looping_reads_the_loop_bit():
    assert is_looping(full_clip("walk_up", 8)) is True
    assert is_looping(empty_clip("walk_down")) is False


def test_frame_png_counts_ignores_meta(tmp_path: Path):
    make_asset_dir(tmp_path, {}, {"walk_up": 8, "walk_left": 3})
    assert frame_png_counts(tmp_path / "Frames") == {"walk_left": 3, "walk_up": 8}


# ── 判定层：坏法必须被抓住 ──


def test_empty_clip_is_caught(tmp_path: Path, capsys):
    """事故原样：帧 PNG 都在，两条片段是空的 —— 必须以非 0 退出。"""
    make_asset_dir(
        tmp_path,
        {"walk_down": 0, "walk_left": 0, "walk_up": 8},
        {"walk_down": 8, "walk_left": 8, "walk_up": 8},
    )
    assert check(tmp_path) == 1
    out = capsys.readouterr().out
    assert "空片段" in out
    assert "walk_down.anim" in out and "walk_left.anim" in out


def test_frame_count_mismatch_warns(tmp_path: Path, capsys):
    """片段有帧但张数与 PNG 对不上 —— 也算问题，不能放过。"""
    make_asset_dir(tmp_path, {"walk_up": 3}, {"walk_up": 8})
    assert check(tmp_path) == 1
    assert "3" in capsys.readouterr().out


# ── 判定层：好产物必须放行（不能误报） ──


def test_healthy_output_passes(tmp_path: Path, capsys):
    make_asset_dir(
        tmp_path,
        {"walk_down": 8, "walk_left": 8, "walk_up": 8},
        {"walk_down": 8, "walk_left": 8, "walk_up": 8},
    )
    assert check(tmp_path) == 0
    out = capsys.readouterr().out
    assert "装配通过" in out
    assert "空片段" not in out


def test_missing_animations_dir_is_a_hard_error(tmp_path: Path):
    (tmp_path / "Frames" / "walk_up").mkdir(parents=True)
    assert check(tmp_path) == 2
