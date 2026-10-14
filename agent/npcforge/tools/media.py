"""素材：从视频抽姿势参考帧，以及去背景。

**为什么姿势参考要走视频**：keep_pose 的姿势来自 SAM 3D 对参考图的实时估计，
所以参考图必须是"照片级、人物占满画幅、背景干净"的实拍帧。骨架 stick figure
没经过 SAM 验证（实测不可用），手画的姿势图更不行。直接从一段动作视频里抽帧，
是拿到合格参考最省事的路。

流程（每一步都单独可跑，出问题能定位到是哪一步）：
    抽帧 → 测周期 → 等分取样 → 去背景 → k1..kN.png

**测周期必须用「直接帧对法」，不能用差分自相关**——自相关会出半周期谐波假值，
把 29 帧的周期测成 15 帧，于是取出来的 8 个相位里有一半是重复的，
而动画看起来"只是有点卡"，很难倒查到根源。
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from collections import deque
from pathlib import Path

from .png_io import Png, PngError, read_png, write_png


class MediaError(RuntimeError):
    pass


# ────────────────────────── ffmpeg 定位 ──────────────────────────

def find_ffmpeg() -> str:
    """先找 imageio_ffmpeg 自带的可执行文件，再退到系统 PATH。

    优先 imageio_ffmpeg 是因为它装了就有、版本可控；系统 ffmpeg 常常没装，
    或者装了但不在 PATH 里（Windows 尤其常见）。
    """
    try:
        import imageio_ffmpeg
        exe = imageio_ffmpeg.get_ffmpeg_exe()
        if exe and Path(exe).exists():
            return exe
    except Exception:
        pass

    exe = shutil.which("ffmpeg")
    if exe:
        return exe

    raise MediaError(
        "找不到 ffmpeg。二选一：\n"
        "  pip install imageio-ffmpeg      （推荐，自带可执行文件）\n"
        "  或把 ffmpeg 加进 PATH")


def extract_frames(video: Path, out_dir: Path, stride: int = 1, max_frames: int = 900,
                   log=print) -> list[Path]:
    """把视频抽成 PNG 序列。`stride=2` 表示隔一帧取一帧（长视频先降采样）。"""
    video = Path(video)
    if not video.is_file():
        raise MediaError(f"找不到视频：{video}")

    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("f*.png"):
        old.unlink()

    ffmpeg = find_ffmpeg()
    vf = f"select=not(mod(n\\,{stride})),setpts=N/FRAME_RATE/TB" if stride > 1 else "null"
    cmd = [ffmpeg, "-y", "-i", str(video), "-vf", vf, "-vsync", "vfr",
           "-frames:v", str(max_frames), str(out_dir / "f%04d.png")]

    log(f"[media] 抽帧：{' '.join(cmd[:6])} …")
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    frames = sorted(out_dir.glob("f*.png"))
    if not frames:
        tail = (proc.stderr or "")[-400:]
        raise MediaError(f"ffmpeg 没抽出任何帧。它的最后几行输出：\n{tail}")
    log(f"[media] 抽出 {len(frames)} 帧")
    return frames


# ────────────────────────── 周期测量 ──────────────────────────

def _luma(png: Png) -> bytes:
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


def measure_cycle(frames: list[Path], min_lag: int = 3, log=print) -> tuple[int, float]:
    """直接帧对法测周期：对每个候选 lag，算所有 i 与 i+lag 的平均绝对差，取最小者。

    返回 (周期帧数, 该周期的平均差)。**别改成自相关**——见模块开头。
    """
    if len(frames) < min_lag * 2:
        raise MediaError(f"帧太少（{len(frames)}），测不出周期。至少要有 {min_lag * 2} 帧。")

    lums = [_luma(read_png(p)) for p in frames]
    n = len(lums)
    max_lag = max(min_lag + 1, n // 2)

    best_lag, best_score = min_lag, float("inf")
    scores: list[tuple[int, float]] = []
    for lag in range(min_lag, max_lag + 1):
        total, count = 0, 0
        for i in range(n - lag):
            a, b = lums[i], lums[i + lag]
            # strict=False 是有意的：这里比较的是两帧亮度序列的重叠前缀。
            # 视频抽帧的尺寸天然一致，长度不同说明素材本身就不对——测周期这一步
            # 不该为此抛错（它只是"给个候选"，取样后还要人看图确认）。
            total += sum(abs(x - y) for x, y in zip(a, b, strict=False))
            count += 1
        score = total / (count * len(lums[0])) if count else float("inf")
        scores.append((lag, round(score, 3)))
        if score < best_score:
            best_lag, best_score = lag, score

    log(f"[media] 周期 = {best_lag} 帧（该 lag 的平均差 {best_score:.3f}）")
    log("[media] 低频候选：" + "，".join(f"{lag}:{s}" for lag, s in scores[:6]))
    return best_lag, best_score


def pick_phases(frames: list[Path], cycle: int, count: int = 8) -> list[Path]:
    """在一个周期内等分取 `count` 帧（相位采样）。"""
    if cycle < count:
        raise MediaError(f"周期只有 {cycle} 帧，取不出 {count} 个不同相位——"
                         "把视频截长一点，或减少相位数量。")
    step = cycle / count
    picks = [frames[int(round(i * step)) % len(frames)] for i in range(count)]
    if len(set(picks)) != count:
        raise MediaError("等分取样出现了重复帧——周期测量可能不准，先人工看一眼抽帧结果。")
    return picks


# ────────────────────────── 去背景 ──────────────────────────

def _bg_color(png: Png, sample: int = 6) -> tuple[int, int, int]:
    """从四角采样背景色（取中位数，避免某一角恰好压着角色）。"""
    corners = []
    w, h = png.width, png.height
    for cx, cy in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)):
        pixels = []
        for dy in range(-sample, sample + 1):
            for dx in range(-sample, sample + 1):
                x, y = min(max(cx + dx, 0), w - 1), min(max(cy + dy, 0), h - 1)
                px = png.px(x, y)
                pixels.append(px[:3] if len(px) >= 3 else (px[0],) * 3)
        pixels.sort()
        corners.append(pixels[len(pixels) // 2])
    return tuple(sorted(c[i] for c in corners)[1] for i in range(3))


def strip_background(png: Png, tolerance: int = 38, feather: int = 1) -> int:
    """把**与四边连通的背景**变透明，就地修改，返回变透明的像素数。

    只吃"从四边能走到的"同色区域——这样角色身上与背景同色的部分
    （白衣服配白幕）不会被误抠出洞来。这是本项目一贯的做法
    （`LocalMatting` 的四边连通域兜底），不是新发明。
    """
    if png.channels != 4:
        pixels = bytearray(png.width * png.height * 4)
        src = png.pixels
        for i in range(png.width * png.height):
            if png.channels == 3:
                pixels[i * 4:i * 4 + 3] = src[i * 3:i * 3 + 3]
            else:
                v = src[i] if png.channels == 1 else src[i * 2]
                pixels[i * 4:i * 4 + 3] = bytes([v, v, v])
            pixels[i * 4 + 3] = 255
        png.pixels = pixels
        png.channels = 4

    w, h = png.width, png.height
    br, bg, bb = _bg_color(png)

    def is_bg(x: int, y: int) -> bool:
        base = y * png.stride + x * 4
        r, g, b = png.pixels[base], png.pixels[base + 1], png.pixels[base + 2]
        return abs(r - br) <= tolerance and abs(g - bg) <= tolerance and abs(b - bb) <= tolerance

    seen = bytearray(w * h)
    queue = deque()
    for x in range(w):
        for y in (0, h - 1):
            if not seen[y * w + x] and is_bg(x, y):
                seen[y * w + x] = 1
                queue.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if not seen[y * w + x] and is_bg(x, y):
                seen[y * w + x] = 1
                queue.append((x, y))

    while queue:
        x, y = queue.popleft()
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not seen[ny * w + nx] and is_bg(nx, ny):
                seen[ny * w + nx] = 1
                queue.append((nx, ny))

    removed = 0
    for y in range(h):
        row = y * w
        for x in range(w):
            if seen[row + x]:
                png.pixels[y * png.stride + x * 4 + 3] = 0
                removed += 1

    if removed == 0:
        raise MediaError(
            f"一个背景像素都没去掉（背景色采样为 RGB{br, bg, bb}）。"
            "多半是背景不纯（花纹、渐变、绿幕不均匀）——换一段背景干净的视频，"
            "或调大 tolerance。")

    if feather:
        _feather_alpha(png, feather)
    return removed


def _feather_alpha(png: Png, radius: int = 1) -> None:
    """给边缘一像素的羽化，避免硬锯齿在缩小后闪烁。"""
    w, h = png.width, png.height
    src = bytes(png.pixels[3::4])
    out = bytearray(src)
    for y in range(h):
        for x in range(w):
            i = y * w + x
            a = src[i]
            if a == 0 or a == 255:
                continue
            neighbours = 0
            for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and src[ny * w + nx] == 0:
                    neighbours += 1
            if neighbours:
                out[i] = max(0, a - 64 * neighbours // 4)
    png.pixels[3::4] = out


# ────────────────────────── 一步到位 ──────────────────────────

def prepare_refs(video: Path, out_dir: Path, phases: int = 8, stride: int = 1,
                 tolerance: int = 38, log=print) -> dict:
    """视频 → `k1.png … kN.png`（去背景）。返回过程数据，便于写进日志。"""
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    work = out_dir / "_raw"
    frames = extract_frames(video, work, stride=stride, log=log)
    cycle, score = measure_cycle(frames, log=log)
    picks = pick_phases(frames, cycle, phases)

    log(f"[media] 取样 {len(picks)} 个相位，开始去背景…")
    report = []
    for i, src in enumerate(picks, 1):
        try:
            png = read_png(src)
        except PngError as e:
            raise MediaError(f"读不了 {src.name}：{e}") from None
        removed = strip_background(png, tolerance=tolerance)
        dst = out_dir / f"k{i}.png"
        write_png(dst, png.width, png.height, png.channels, bytes(png.pixels))
        report.append({"index": i, "source": src.name,
                       "removed_pct": round(100.0 * removed / (png.width * png.height), 1)})
        log(f"[media]   k{i}.png ← {src.name}（背景去掉 {report[-1]['removed_pct']}%）")

    return {"frames_extracted": len(frames), "cycle": cycle, "cycle_score": round(score, 3),
            "phases": phases, "out_dir": str(out_dir), "refs": report}


def main() -> int:
    import argparse
    ap = argparse.ArgumentParser(description="视频 → 姿势参考帧")
    ap.add_argument("video")
    ap.add_argument("out_dir")
    ap.add_argument("--phases", type=int, default=8)
    ap.add_argument("--stride", type=int, default=1)
    ap.add_argument("--tolerance", type=int, default=38)
    ap.add_argument("--measure-only", action="store_true", help="只抽帧测周期，不取样不去背景")
    args = ap.parse_args()

    try:
        if args.measure_only:
            work = Path(args.out_dir) / "_raw"
            frames = extract_frames(Path(args.video), work, stride=args.stride)
            cycle, score = measure_cycle(frames)
            print(f"周期 {cycle} 帧（平均差 {score:.3f}）")
            return 0
        report = prepare_refs(Path(args.video), Path(args.out_dir), args.phases,
                              args.stride, args.tolerance)
        print(f"完成：{report['frames_extracted']} 帧抽取，周期 {report['cycle']}，"
              f"取样 {report['phases']} 个相位 → {report['out_dir']}")
        return 0
    except MediaError as e:
        print(f"[失败] {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
