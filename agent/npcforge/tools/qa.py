"""质检门禁：把一组帧判成「能用 / 不能用」，并留下可裁决的事实。

**只输出事实，不下"要不要收下"的结论**——结论由 `agents/qa_judge.py` 里那个
带 LLM 的裁决者给。这样分开有两个好处：

1. 这些数字可以单独跑、单独看（`python -m npcforge.tools.frame_audit <目录>`），
   排查"到底哪一帧坏了"时不必惊动模型；
2. 裁决的依据是可复现的数字，不是模型的印象。同一个报告喂两次，裁决应当一致。
"""

from __future__ import annotations

from pathlib import Path

from ..models import FrameStats
from .frame_audit import frame_files, inspect
from .motion_check import analyze, judge


def group_dirs(character_dir: Path) -> list[Path]:
    """列出 `<产物目录>/Frames/` 下的帧组目录（`<动作>_<朝向>`）。"""
    frames = Path(character_dir) / "Frames"
    if not frames.is_dir():
        return []
    return sorted((p for p in frames.iterdir() if p.is_dir()), key=lambda p: p.name)


def audit_group(group_dir: Path, cutoff: int = 64,
                min_diff: float = 8.0, min_span: float = 0.02) -> FrameStats:
    """体检一个帧组，返回结构化结果。`problems` 为空即这一组没有已知缺陷。"""
    files = frame_files(Path(group_dir))
    stats = FrameStats(group=Path(group_dir).name, frames=len(files))

    if not files:
        stats.problems.append("这个目录下没有帧 PNG")
        return stats

    rows = [inspect(p, cutoff) for p in files]

    sizes = {r["size"] for r in rows if "size" in r}
    stats.size = next(iter(sizes)) if sizes else ""
    stats.size_uniform = len(sizes) <= 1
    if not stats.size_uniform:
        stats.problems.append(f"帧尺寸不一致（{sorted(sizes)}）——尺寸统一是 pivot 对齐的前提")

    opac = [r["opaque_pct"] for r in rows if "opaque_pct" in r]
    if opac:
        stats.opaque_pct_min = min(opac)
        stats.opaque_pct_max = max(opac)
    semis = [r.get("semi_pct", 0.0) for r in rows]
    stats.semi_pct_max = max(semis) if semis else 0.0

    for row in rows:
        for note in row.get("notes", []):
            stats.problems.append(f"{row['file']}：{note}")

    report = analyze(files, cutoff)
    if "error" in report:
        stats.problems.append(report["error"])
    else:
        stats.mean_diff = report["mean_diff"]
        stats.width_range_ratio = report["width_range_ratio"]
        stats.center_range_ratio = report["center_range_ratio"]
        ok, notes = judge(report, min_diff, min_span)
        if not ok:
            stats.problems.extend(n for n in notes if not n.startswith("排查顺序"))

    return stats


def audit_character(character_dir: Path, cutoff: int = 64,
                    min_diff: float = 8.0, min_span: float = 0.02) -> list[FrameStats]:
    return [audit_group(g, cutoff, min_diff, min_span) for g in group_dirs(character_dir)]


def blocking_problems(stats_list: list[FrameStats]) -> list[str]:
    """挑出"这一组不该被收下"的问题（区别于"可以容忍的瑕疵"）。

    半透明噪点是**确定性的坏**：它在 Unity 里会变成一层灰雾，
    而且会让 FrameNormalizer 的包围盒算偏。这类必须重跑。
    """
    out = []
    for s in stats_list:
        for p in s.problems:
            if "半透明" in p or "没有 alpha" in p or "空帧" in p or "几乎" in p or "不一致" in p:
                out.append(f"[{s.group}] {p}")
    return out
