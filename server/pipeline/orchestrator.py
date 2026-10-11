"""任务编排：把七段串起来，并把进度流式推给 Unity。

这一段刻意做得很薄——所有业务逻辑在各段里，这里只负责顺序、进度与错误处理。
这样任何一段都能单独测试，也方便以后把某段换成异步/远程执行。
"""

from __future__ import annotations

import uuid
from collections.abc import Awaitable, Callable
from pathlib import Path

from server.config import settings
from server.models.schemas import (
    JobProgress,
    JobRequest,
    JobResult,
    JobStage,
    SpriteManifest,
)
from server.pipeline import assemble, generate, matting, normalize, pack, prompt_forge, style_lock

Emit = Callable[[JobProgress], Awaitable[None]] | None


async def _emit(emit: Emit, job_id: str, stage: JobStage, percent: float, message: str, **extra) -> None:
    if emit is None:
        return
    await emit(
        JobProgress(job_id=job_id, stage=stage, percent=percent, message=message, **extra)
    )


def new_job_id() -> str:
    return uuid.uuid4().hex[:12]


async def run_job(req: JobRequest, job_id: str | None = None, emit: Emit = None) -> JobResult:
    """跑完整条流水线。"""
    job_id = job_id or new_job_id()
    out_dir = settings.output_path / job_id
    frames_dir = out_dir / "frames"
    norm_dir = out_dir / "normalized"
    warnings: list[str] = []

    try:
        await _emit(emit, job_id, JobStage.QUEUED, 0, "任务已入队")

        # ── [1] Prompt Forge ────────────────────────────────
        await _emit(emit, job_id, JobStage.PROMPT_FORGE, 5, "解析角色描述")
        card = prompt_forge.forge_character_card(req, job_id)
        prompt_forge.save_card(card, out_dir)
        await _emit(
            emit,
            job_id,
            JobStage.PROMPT_FORGE,
            10,
            f"角色卡已生成：{card.role}",
            artifact=str(out_dir / "character_card.json"),
        )

        # ── [2] Style Lock ──────────────────────────────────
        await _emit(emit, job_id, JobStage.STYLE_LOCK, 12, "锁定画风约束")
        style = style_lock.lock_style(req, master_reference=None)
        style_lock.save_profile(style, out_dir)
        if req.reference_images and not style.positive_tags:
            warnings.append(
                "画风锁定未生效：仅提供了参考图但 style_lock.describe_references 尚未实现，"
                "当前产出不会体现项目画风。请先实现该函数（见 docs/pipeline.md 第 2 段）。"
            )
        if style.master_reference is None:
            warnings.append(
                "未指定 master reference。人类在环检查点：请人工挑选一张定稿图作为风格锚点，"
                "否则四向一致性无法验收。"
            )

        # ── [3] Generate ────────────────────────────────────
        await _emit(emit, job_id, JobStage.GENERATE, 15, "开始生成")
        loop = _make_loop()
        frames = generate.generate_frames(
            card, style, req, frames_dir, frames_per_clip=4, progress=loop
        )
        await _emit(emit, job_id, JobStage.GENERATE, 55, f"已生成 {len(frames)} 帧")

        # ── [4] Matting ─────────────────────────────────────
        await _emit(emit, job_id, JobStage.MATTING, 58, "抠图")
        cut_paths, method = matting.cutout_batch([f.path for f in frames])
        if method != "rembg":
            warnings.append(
                f"抠图使用了降级方案（{method}），边缘质量有限。"
                "真实生成图请安装 rembg：pip install 'charforge[local]'"
            )
        await _emit(emit, job_id, JobStage.MATTING, 70, f"抠图完成（{method}）")

        # ── [5] Normalize ───────────────────────────────────
        await _emit(emit, job_id, JobStage.NORMALIZE, 72, "归一化")
        canvas = (512, 512)
        pixel_grid = normalize.suggest_pixel_grid(canvas, target_resolution=64) if _is_pixel_style(style) else 1
        norm_paths = normalize.normalize_frames(cut_paths, norm_dir, canvas=canvas, pixel_grid=pixel_grid)
        if not norm_paths:
            raise RuntimeError("归一化没有产出任何帧，请检查上游抠图结果")
        for f, p in zip(frames, norm_paths, strict=True):
            f.path = p
        await _emit(emit, job_id, JobStage.NORMALIZE, 80, "归一化完成")

        # ── [6] Pack ────────────────────────────────────────
        await _emit(emit, job_id, JobStage.PACK, 82, "打包 sprite sheet")
        sheet_info, rects = pack.pack_sheet(frames, out_dir / "sheet.png", cell=canvas, columns=4)
        manifest = pack.build_manifest(
            job_id,
            sheet_info,
            rects,
            pixel_art=_is_pixel_style(style),
            frame_paths=norm_paths,
        )
        pack.save_manifest(manifest, out_dir)
        await _emit(
            emit,
            job_id,
            JobStage.PACK,
            90,
            f"打包完成：{len(rects)} 个切片",
            artifact=str(out_dir / "sheet.png"),
        )

        # ── [7] Assemble ────────────────────────────────────
        await _emit(emit, job_id, JobStage.ASSEMBLE, 92, "生成 Unity 装配指令")
        spec = assemble.build_assembly_spec(card, manifest, req, job_id)
        persona = assemble.build_persona(card)
        paths = assemble.write_outputs(spec, persona, manifest, out_dir)

        await _emit(emit, job_id, JobStage.DONE, 100, "完成", artifact=str(out_dir))

        return JobResult(
            job_id=job_id,
            ok=True,
            spec=spec,
            manifest=manifest,
            output_dir=str(out_dir),
            warnings=warnings + [f"产物：{k} -> {v}" for k, v in paths.items()],
        )

    except Exception as exc:  # noqa: BLE001 —— 顶层兜底，保证 Unity 侧一定收到失败事件
        await _emit(emit, job_id, JobStage.FAILED, 0, f"失败：{exc}")
        return JobResult(
            job_id=job_id,
            ok=False,
            output_dir=str(out_dir),
            warnings=warnings,
            error=f"{type(exc).__name__}: {exc}",
        )


def _is_pixel_style(style) -> bool:
    tags = " ".join(style.positive_tags).lower()
    return any(t in tags for t in ("pixel", "8-bit", "16-bit", "retro"))


def _make_loop():
    """把同步的进度回调转成打印（服务端日志）。WebSocket 的进度走 _emit。"""

    def cb(_pct: float, msg: str) -> None:
        print(f"[generate] {msg}")

    return cb


def load_output(job_id: str) -> Path:
    return settings.output_path / job_id


def list_jobs() -> list[str]:
    root = settings.output_path
    if not root.exists():
        return []
    return sorted([p.name for p in root.iterdir() if p.is_dir()], reverse=True)


__all__ = ["run_job", "new_job_id", "load_output", "list_jobs", "SpriteManifest"]
