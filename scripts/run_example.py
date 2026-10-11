#!/usr/bin/env python
"""跑一个示例配置。

    python scripts/run_example.py examples/museum_guide
    python scripts/run_example.py examples/town_shopkeeper --backend mock

默认用 mock 后端（无需 GPU / API key），几秒就能看到七类产物。
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from server.config import settings  # noqa: E402
from server.models.schemas import (  # noqa: E402
    ActionName,
    Direction,
    JobRequest,
    MovementMode,
    MovementSpec,
)
from server.pipeline.orchestrator import new_job_id, run_job  # noqa: E402


def build_request(cfg: dict) -> JobRequest:
    mv = cfg.get("movement") or {}
    movement = MovementSpec(
        mode=MovementMode(mv.get("mode", "none")),
        speed=float(mv.get("speed", 1.5)),
        patrol_points=[tuple(p) for p in (mv.get("patrol_points") or [])],
    )
    return JobRequest(
        prompt=cfg["prompt"],
        character_name=cfg.get("name"),
        reference_images=[],
        style_preset=cfg.get("style_preset", "custom"),
        actions=[ActionName(a) for a in cfg.get("actions", ["idle", "walk"])],
        directions=[Direction(d) for d in cfg.get("directions", ["south"])],
        movement=movement,
        dialogue_enabled=bool(cfg.get("dialogue", False)),
        era_or_period=cfg.get("era_or_period"),
        reference_notes=cfg.get("reference_notes"),
        forbidden=list(cfg.get("forbidden") or []),
    )


async def run(example_dir: Path, backend: str) -> int:
    cfg_path = example_dir / "character.yaml"
    if not cfg_path.exists():
        print(f"找不到配置：{cfg_path}", file=sys.stderr)
        return 1

    settings.charforge_backend = backend
    cfg = yaml.safe_load(cfg_path.read_text(encoding="utf-8"))

    print(f"示例：{example_dir.name}")
    print(f"角色：{cfg.get('name')} / {cfg.get('role')}")
    print(f"后端：{backend}")
    print("-" * 56)

    async def emit(p) -> None:
        print(f"[{p.percent:5.1f}%] {p.stage.value:<14} {p.message}")

    job_id = new_job_id()
    result = await run_job(build_request(cfg), job_id, emit)

    print("-" * 56)
    if not result.ok:
        print(f"失败：{result.error}")
        return 1

    print(f"成功。产物目录：{result.output_dir}")
    for w in result.warnings:
        print(f"  提醒：{w}")
    if result.spec and result.manifest:
        print(f"  prefab：{result.spec.prefab_name}")
        print(f"  动画：{[a.name for a in result.spec.animations]}")
        print(f"  切片：{len(result.manifest.sprites)} 个")
        print(f"  sheet：{result.manifest.sheet.path}")
    print("\n接下来：在 Unity 打开 Window > CharForge，把上面的产物目录填进去，点「仅重新装配」。")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="跑一个示例配置")
    parser.add_argument("example_dir", type=Path, help="如 examples/museum_guide")
    parser.add_argument(
        "--backend",
        default="mock",
        choices=["mock", "local", "cloud"],
        help="生成后端（默认 mock）",
    )
    args = parser.parse_args()

    example_dir = args.example_dir
    if not example_dir.is_absolute():
        example_dir = ROOT / example_dir

    return asyncio.run(run(example_dir, args.backend))


if __name__ == "__main__":
    raise SystemExit(main())
