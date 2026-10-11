"""命令行入口。

最常用的一条（W1 验证用，不需要 GPU、不需要 API key）：

    python -m server.cli smoke --prompt "博物馆导览员，青绿山水色调"

它会用 mock 后端跑完整条七段流水线，产出：
    out/<job_id>/character_card.json
    out/<job_id>/frames/...
    out/<job_id>/normalized/...
    out/<job_id>/sheet.png
    out/<job_id>/manifest.json
    out/<job_id>/assembly_spec.json
    out/<job_id>/persona.json
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys

from server.config import settings
from server.models.schemas import (
    ActionName,
    Direction,
    JobRequest,
    MovementMode,
    MovementSpec,
)
from server.pipeline.orchestrator import new_job_id, run_job


def _build_request(args: argparse.Namespace) -> JobRequest:
    actions = [ActionName(a) for a in args.actions]
    directions = [Direction(d) for d in args.directions]
    movement = MovementSpec(
        mode=MovementMode(args.movement),
        speed=args.speed,
        patrol_points=[],
    )
    return JobRequest(
        prompt=args.prompt,
        character_name=args.name,
        reference_images=list(args.ref or []),
        style_preset=args.preset,
        actions=actions,
        directions=directions,
        movement=movement,
        dialogue_enabled=args.dialogue,
        era_or_period=args.era,
        reference_notes=args.notes,
        forbidden=list(args.forbidden or []),
    )


async def _cmd_smoke(args: argparse.Namespace) -> int:
    job_id = new_job_id()
    req = _build_request(args)

    print(f"任务 {job_id}")
    print(f"后端 {settings.charforge_backend}")
    print(f"描述 {req.prompt}")
    print("-" * 56)

    async def emit(p) -> None:
        bar = "#" * int(p.percent / 4)
        print(f"[{p.percent:5.1f}%] {bar:<25} {p.stage.value:<14} {p.message}")

    result = await run_job(req, job_id, emit)

    print("-" * 56)
    if not result.ok:
        print(f"失败：{result.error}")
        return 1

    print(f"成功。产物目录：{result.output_dir}")
    for w in result.warnings:
        print(f"  提醒：{w}")
    if result.spec:
        print(f"  prefab 名：{result.spec.prefab_name}")
        print(f"  动画片段：{len(result.spec.animations)} 个 -> {[a.name for a in result.spec.animations]}")
        print(f"  切片数量：{len(result.manifest.sprites) if result.manifest else 0}")
    return 0


def _cmd_serve(args: argparse.Namespace) -> int:
    import uvicorn

    uvicorn.run("server.main:app", host=args.host, port=args.port, reload=args.reload)
    return 0


def _cmd_inspect(args: argparse.Namespace) -> int:
    """查看某个任务的装配指令（调试用）。"""
    from server.pipeline.orchestrator import load_output

    path = load_output(args.job_id) / "assembly_spec.json"
    if not path.exists():
        print(f"找不到 {path}", file=sys.stderr)
        return 1
    print(json.dumps(json.loads(path.read_text(encoding="utf-8")), ensure_ascii=False, indent=2))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="charforge", description="Unity 2D 角色资产流水线")
    sub = parser.add_subparsers(dest="command", required=True)

    p_smoke = sub.add_parser("smoke", help="跑通整条流水线（mock 后端即可）")
    p_smoke.add_argument("--prompt", required=True, help="一句角色描述")
    p_smoke.add_argument("--name", default=None, help="角色名（不填则自动推断）")
    p_smoke.add_argument("--ref", nargs="*", help="项目画风参考图路径（3-5 张）")
    p_smoke.add_argument("--preset", default="custom", help="风格预设名（configs/style_presets/）")
    p_smoke.add_argument("--actions", nargs="*", default=["idle", "walk"])
    p_smoke.add_argument("--directions", nargs="*", default=["south", "north"])
    p_smoke.add_argument("--movement", default="astar", choices=[m.value for m in MovementMode])
    p_smoke.add_argument("--speed", type=float, default=1.5)
    p_smoke.add_argument("--dialogue", action="store_true", help="启用对话人设")
    p_smoke.add_argument("--era", default=None, help="年代/形制约束（文博场景）")
    p_smoke.add_argument("--notes", default=None, help="形制/考据要点（进入人设的知识边界）")
    p_smoke.add_argument("--forbidden", nargs="*", help="禁止元素")
    p_smoke.set_defaults(func=lambda a: asyncio.run(_cmd_smoke(a)))

    p_serve = sub.add_parser("serve", help="启动服务端")
    p_serve.add_argument("--host", default=settings.host)
    p_serve.add_argument("--port", type=int, default=settings.port)
    p_serve.add_argument("--reload", action="store_true")
    p_serve.set_defaults(func=_cmd_serve)

    p_inspect = sub.add_parser("inspect", help="查看某任务的装配指令")
    p_inspect.add_argument("job_id")
    p_inspect.set_defaults(func=_cmd_inspect)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
