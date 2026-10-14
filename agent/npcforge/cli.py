"""npcforge 命令行：把这个 Agent 包成一个**可以独立运行**的程序。

这个文件只做三件事：把命令行参数变成 `NpcJob`、把 `Config` 装配好、
把 `NpcForgeRunner` 跑起来并把结果讲清楚。

**流程逻辑一律不在这里。** 在这里加一条 `if` 就等于把 Agent 的行为藏进了 CLI，
将来做看板、做常驻服务、包成 MCP server 时都得把这段抄一遍——然后它们各自漂移。
要改行为，去改 `loop.py`。

六个命令：

    doctor   先跑这个。看东西都在不在，并生成一份可编辑的配置模板
    create   一个角色：一句话描述 → 完整产物
    batch    一份角色列表（YAML）→ 一批产物，一个失败不拖累其余的
    status   看作业跑到哪一步了
    retry    接着跑 / 重跑，可以指定从哪一阶段起
    refs     视频 → 姿势参考帧（走「外部 API 出视频再抽帧」那条路时用）

退出码与插件 CLI 是同一套语义：**0 成功 / 1 失败 / 2 用法错**。
CI 里判 `== 0`，不要判 `!= 1`——脚本编译不过时那条命令压根没被执行。

    npcforge doctor --init
    npcforge create 铁匠老王 "驼背的矮人铁匠，皮围裙，右臂一道烫伤"
    npcforge create 卫兵 "披甲长枪兵" --until prompt     # 先看人设与提示词，满意再出图
    npcforge batch npcs.yaml
    npcforge status

配置开关（`--config` / `--comfy-url` / `--unity-exe` 等）写在各子命令**之后**。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import __version__
from .config import Config
from .loop import NpcForgeRunner, ref_roots
from .models import CharacterList, NpcJob, PromptDraft, Stage
from .state import JobStore, status_line

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_USAGE = 2

# `--until` 允许停在哪些阶段。终态（done/failed）与 pending 没有意义。
STOPPABLE = ("persona", "prompt", "portrait", "frames", "qa", "assemble", "scripting")

# 批量 YAML 里这两个键**不是** NpcJob 的字段，而是「预置件」——
# 给了就跳过对应阶段。省钱，也便于用已经出好的图复现问题。
PRESEED_KEYS = ("prompt", "portrait")


# ────────────────────────── 输出 ──────────────────────────


def _force_utf8() -> None:
    """Windows 控制台默认 cp936，打不出「✓」「·」时会在**打印阶段**抛异常。

    那种异常会把一次已经成功的作业变成非零退出码——最难查的一类故障。
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass


def _say(message: str = "") -> None:
    print(message)


def _head(title: str) -> None:
    _say()
    _say("─" * 62)
    _say(f"  {title}")
    _say("─" * 62)


# ────────────────────────── 配置装配 ──────────────────────────


def _add_config_opts(parser: argparse.ArgumentParser) -> None:
    """挂上「能覆盖配置文件与环境变量」的开关。

    全部 `default=argparse.SUPPRESS`：**没写就压根不产生这个属性**。
    这样主解析器与子解析器可以挂同一套开关而不会互相把对方的值冲成 None
    （argparse 的经典坑：子解析器的默认值会覆盖主解析器已经解析出来的值）。
    """
    group = parser.add_argument_group("配置（覆盖配置文件与环境变量）")

    def add(*names, **kwargs):
        group.add_argument(*names, default=argparse.SUPPRESS, **kwargs)

    add("--config", metavar="TOML", help="配置文件路径")
    add("--state-dir", dest="state_dir", metavar="DIR", help="运行状态目录（作业记录与日志）")
    add("--output-folder", dest="output_folder", metavar="REL",
        help="Unity 侧产物根，Assets 相对路径（默认 Assets/CharForge）")
    add("--comfy-url", dest="comfy_url", metavar="URL",
        help="ComfyUI 地址，默认 http://localhost:8000")
    add("--config-dir", dest="plugin_config_dir", metavar="DIR",
        help="插件包的 Configs/comfyui（工作流与 bindings 的所在地）")
    add("--unity-exe", dest="unity_exe", metavar="EXE", help="Unity 编辑器可执行文件")
    add("--unity-project", dest="unity_project", metavar="DIR", help="Unity 工程目录")
    add("--llm-base-url", dest="llm_base_url", metavar="URL")
    add("--llm-api-key", dest="llm_api_key", metavar="KEY")
    add("--llm-model", dest="llm_model", metavar="NAME")
    add("--max-attempts", dest="max_attempts", type=int, metavar="N",
        help="同一个作业最多重试几轮（默认 3）")


# CLI 参数名 → Config 字段名。不在这里的字段只能用配置文件或环境变量。
_OVERRIDES = {
    "state_dir": "state_dir",
    "output_folder": "output_folder",
    "comfy_url": "comfy_url",
    "plugin_config_dir": "config_dir",
    "unity_exe": "unity_exe",
    "unity_project": "unity_project",
    "llm_base_url": "llm_base_url",
    "llm_api_key": "llm_api_key",
    "llm_model": "llm_model",
    "max_attempts": "max_attempts",
}


def _config_from(args) -> Config:
    cfg = Config.load(getattr(args, "config", None))
    for arg_name, field_name in _OVERRIDES.items():
        value = getattr(args, arg_name, None)
        if value not in (None, ""):
            setattr(cfg, field_name, value)
    return cfg


# ────────────────────────── 输入解析 ──────────────────────────


def _split_list(raw: str | None) -> list[str]:
    """`walk,attack` / `walk attack` / `walk、attack` → ['walk', 'attack']。"""
    if not raw:
        return []
    text = raw.replace(",", " ").replace("，", " ").replace("、", " ")
    return [item.strip() for item in text.split() if item.strip()]


def _multi(args, name: str) -> list[str]:
    """收重复给的开关（`--script A --script B`）以及逗号分隔的写法。"""
    raw = getattr(args, name, None)
    if not raw:
        return []
    if isinstance(raw, str):
        return _split_list(raw)
    out: list[str] = []
    for item in raw:
        out.extend(_split_list(item))
    return out


def _job_from_args(args) -> NpcJob:
    job = NpcJob(
        name=args.name,
        description=args.desc,
        role=args.role or "",
        persona_hint=args.persona_hint or "",
        actions=_split_list(args.actions) or ["walk"],
        directions=_split_list(args.directions) or ["down", "up", "right", "left"],
        movement=args.movement,
        collider=args.collider,
        scripts=_multi(args, "script"),
        seed=args.seed,
    )
    # 未知动作不拦（插件侧动作名由帧组目录决定），但**必须出声**——
    # 未知动作会被当成一次性动作，播完不回默认状态，是个安静的坑。
    unknown = [a for a in job.actions if a not in ("walk", "run", "attack", "jump", "death")]
    if unknown:
        _say(f"  [提醒] 动作 {', '.join(unknown)} 不在已知集里。"
             "插件只把 walk/run 当循环动作，其余播完停在末帧。")
    return job


def _load_character_entries(path: Path) -> list[dict]:
    """读角色列表 YAML，返回**原始映射**（预置件还留着，调用方自己挑）。

    顶层可以是 `characters:` 的映射，也可以直接是一个列表——两种写法都自然。
    """
    try:
        import yaml
    except ImportError:  # pragma: no cover
        raise SystemExit("批量模式要读 YAML，但 pyyaml 没装上：pip install pyyaml") from None

    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    if data is None:
        raise SystemExit(f"角色列表是空的：{path}")

    if isinstance(data, list):
        defaults, entries = {}, data
    elif isinstance(data, dict):
        defaults, entries = data.get("defaults") or {}, data.get("characters") or []
    else:
        raise SystemExit(f"角色列表的格式不认识（顶层应当是映射或列表）：{path}")

    if not entries:
        raise SystemExit(f"角色列表里没有 `characters`：{path}")

    out: list[dict] = []
    for entry in entries:
        if not isinstance(entry, dict):
            raise SystemExit(f"每个角色应当是一个映射，这里是：{entry!r}") from None
        merged = dict(entry)
        # defaults 只填**没写**的键——角色自己的写法永远优先。
        for key, value in defaults.items():
            merged.setdefault(key, value)
        out.append(merged)
    return out


def _preseed(store: JobStore, job: NpcJob, entry: dict) -> None:
    """把「预置件」写进作业记录。给了就跳过对应阶段。"""
    prompt = entry.get("prompt")
    portrait = entry.get("portrait")
    if not prompt and not portrait:
        return

    rec = store.load(job.name) or store.create(job)
    rec.job = job
    if prompt:
        rec.prompt = PromptDraft(prompt=str(prompt), reasoning="角色列表里直接给定，未走模型")
    if portrait:
        path = Path(str(portrait)).expanduser()
        if not path.is_file():
            raise SystemExit(f"[{job.name}] portrait 指向的文件不存在：{path}")
        rec.portrait_path = str(path)
    store.save(rec)


# ────────────────────────── 结果呈现 ──────────────────────────


def _report(rec, cfg: Config, as_json: bool = False) -> int:
    """打印一个作业的结局，并给出与插件 CLI 同语义的退出码。"""
    if as_json:
        _say(json.dumps(rec.model_dump(mode="json"), ensure_ascii=False, indent=2))
        return EXIT_OK if rec.stage != Stage.failed else EXIT_FAIL

    _head(f"{rec.job.name} → {rec.stage.value}")
    for entry in rec.log:
        _say(f"  {'✓' if entry.ok else '✗'} {entry.stage.value:<10} "
             f"{entry.seconds:>6.1f}s  {entry.message}")
    if rec.persona:
        _say(f"  人设      {rec.persona.display_name}（{rec.persona.role}）")
    if rec.portrait_path:
        _say(f"  立绘      {rec.portrait_path}")
    if rec.frame_groups:
        _say(f"  帧组      {', '.join(rec.frame_groups)}")
    if rec.prefab_path:
        _say(f"  prefab    {rec.prefab_path}")
    if rec.diagnosis:
        _say(f"  诊断      {rec.diagnosis.explanation}")
        _say(f"  下一步    {rec.diagnosis.action} —— {rec.diagnosis.detail}")
    if rec.stage not in (Stage.done, Stage.failed):
        _say(f"  ⏸ 未跑完（停在 {rec.stage.value}）。再跑一次同一条命令即可接着走。")

    return EXIT_OK if rec.stage != Stage.failed else EXIT_FAIL


def _summarize(records: list) -> int:
    done = [r for r in records if r.stage == Stage.done]
    failed = [r for r in records if r.stage == Stage.failed]
    stopped = [r for r in records if r.stage not in (Stage.done, Stage.failed)]

    _head(f"批量结束：完成 {len(done)} / 失败 {len(failed)} / 未跑完 {len(stopped)}")
    for rec in records:
        _say("  " + status_line(rec))
    if failed:
        _say()
        _say("  失败的作业都留了状态，修好环境后：npcforge retry <角色名>")
    return EXIT_FAIL if failed else EXIT_OK


def _detail(rec, cfg: Config) -> None:
    _head(rec.job.name)
    _say(f"  阶段      {rec.stage.value}")
    _say(f"  描述      {rec.job.description}")
    _say(f"  动作      {', '.join(f'{a}_{d}' for a in rec.job.actions for d in rec.job.directions)}")
    _say(f"  种子      {rec.seed}（已重试 {rec.attempts} 次）")
    _say(f"  放大系数  {rec.amplify or '（按方向用默认值）'}")
    if rec.portrait_path:
        _say(f"  立绘      {rec.portrait_path}")
    if rec.frame_groups:
        _say(f"  已落盘    {', '.join(rec.frame_groups)}")
    if rec.redo_groups:
        _say(f"  待重跑    {', '.join(rec.redo_groups)}")
    if rec.prefab_path:
        _say(f"  prefab    {rec.prefab_path}")

    if rec.stats:
        # 裁决与质检按 group 配对。两个列表是平行的，各取各的。
        by_group = {v.group: v for v in rec.verdicts if v.group}
        _say()
        _say("  质检：")
        for stats in rec.stats:
            line = (f"    {stats.group:<14} {stats.frames:>2} 帧  "
                    f"不透明 {stats.opaque_pct_min:.0f}~{stats.opaque_pct_max:.0f}%  "
                    f"帧差 {stats.mean_diff:.1f}  摆幅 {stats.span_ratio:.3f}")
            verdict = by_group.get(stats.group)
            if verdict:
                line += f"  → {verdict.verdict.value}"
            _say(line)
            for problem in stats.problems[:3]:
                _say(f"        · {problem}")

    if rec.diagnosis:
        _say()
        _say(f"  诊断      [{rec.diagnosis.category}] {rec.diagnosis.explanation}")
        _say(f"  下一步    {rec.diagnosis.action} —— {rec.diagnosis.detail}")

    if rec.log:
        _say()
        _say("  最近几笔：")
        for entry in rec.log[-8:]:
            _say(f"    {entry.at}  {'✓' if entry.ok else '✗'} "
                 f"{entry.stage.value:<10} {entry.message[:70]}")

    _say()
    _say(f"  状态文件  {cfg.state_path / 'jobs' / (rec.job.name + '.json')}")
    _say(f"  详细日志  {cfg.state_path / 'logs' / (rec.job.name + '.log')}")
    _say(f"  产物目录  {cfg.character_dir(rec.job.name)}")


# ────────────────────────── 命令 ──────────────────────────


def cmd_create(args) -> int:
    cfg = _config_from(args)
    job = _job_from_args(args)
    store = JobStore(cfg.state_path)
    store.ensure()

    rec = store.load(job.name)
    if rec is not None and rec.stage in (Stage.done, Stage.failed):
        _head(f"{job.name} 之前已经跑过（{rec.stage.value}）")
        _say(f"  接着跑：npcforge retry {job.name}")
        _say(f"  清掉重来：npcforge retry {job.name} --forget")
        return EXIT_OK if rec.stage == Stage.done else EXIT_FAIL

    if args.portrait:
        path = Path(args.portrait).expanduser()
        if not path.is_file():
            _say(f"[用法错] --portrait 指向的文件不存在：{path}")
            return EXIT_USAGE
        rec = rec or store.create(job)
        rec.portrait_path = str(path)
        store.save(rec)

    if args.prompt:
        rec = rec or store.create(job)
        rec.prompt = PromptDraft(prompt=args.prompt, reasoning="命令行 --prompt 直接给定，未走模型")
        store.save(rec)

    runner = NpcForgeRunner(cfg, store, stop_after=_stage_or_none(args.until))
    records = runner.run([job])
    return _report(records[-1], cfg, as_json=args.json)


def cmd_batch(args) -> int:
    cfg = _config_from(args)
    path = Path(args.file).expanduser()
    if not path.is_file():
        _say(f"[用法错] 找不到角色列表：{path}")
        return EXIT_USAGE

    entries = _load_character_entries(path)
    clean = [{k: v for k, v in e.items() if k not in PRESEED_KEYS} for e in entries]
    listing = CharacterList.model_validate({"characters": clean})
    jobs = listing.characters

    only = set(_split_list(args.only))
    if only:
        missing = only - {j.name for j in jobs}
        if missing:
            _say(f"[用法错] --only 里有列表中不存在的角色：{', '.join(sorted(missing))}")
            return EXIT_USAGE
        keep = {j.name for j in jobs if j.name in only}
        jobs = [j for j in jobs if j.name in keep]
        entries = [e for e in entries if e.get("name") in keep]

    store = JobStore(cfg.state_path)
    store.ensure()
    # 预置件在跑之前写进记录，`run()` 会按记录里的阶段接着走。
    for job, entry in zip(jobs, entries, strict=True):
        _preseed(store, job, entry)

    _head(f"批量 {len(jobs)} 个角色 ← {path.name}")
    runner = NpcForgeRunner(cfg, store, stop_after=_stage_or_none(args.until))
    return _summarize(runner.run(jobs))


def cmd_status(args) -> int:
    cfg = _config_from(args)
    store = JobStore(cfg.state_path)

    if args.name:
        rec = store.load(args.name)
        if rec is None:
            _say(f"没有这个作业：{args.name}")
            _say(f"  在 {cfg.state_path / 'jobs'} 里找过了。`npcforge status` 列出全部。")
            return EXIT_FAIL
        if args.json:
            _say(json.dumps(rec.model_dump(mode="json"), ensure_ascii=False, indent=2))
        else:
            _detail(rec, cfg)
        return EXIT_OK

    records = store.all()
    if args.json:
        _say(json.dumps([r.model_dump(mode="json") for r in records], ensure_ascii=False, indent=2))
        return EXIT_OK

    if not records:
        _say(f"还没有任何作业。状态目录：{cfg.state_path}")
        _say('起一个：npcforge create <角色名> "一句话描述"')
        return EXIT_OK

    _head(f"{len(records)} 个作业（{cfg.state_path}）")
    for rec in records:
        _say("  " + status_line(rec))
    return EXIT_OK


def cmd_retry(args) -> int:
    cfg = _config_from(args)
    store = JobStore(cfg.state_path)

    rec = store.load(args.name)
    if rec is None:
        _head(f"没有这个作业：{args.name}")
        _say(f"  在 {cfg.state_path / 'jobs'} 里找过了。文件名就是角色名。")
        _say("  `npcforge status` 可以看到全部。")
        return EXIT_FAIL

    if args.forget:
        store.forget(args.name)
        _say(f"已丢掉 {args.name} 的记录，从头跑。")
        return _resume_like_create(cfg, rec.job, args)

    runner = NpcForgeRunner(cfg, store, stop_after=_stage_or_none(args.until))
    if args.clean:
        runner.clean(args.name)
        _say(f"已清掉 {args.name} 的中间产物（状态记录保留）。")

    target = _stage_or_none(args.from_stage)
    if target is not None:
        rec.stage = target
    elif not args.force and rec.stage in (Stage.done, Stage.failed):
        # 终态的记录默认不动：重跑是**要花钱**的（出图/出帧）。
        _say(f"{args.name} 已经是终态（{rec.stage.value}）。")
        _say("  真要重跑：加 --force；或 --from <阶段> 指定从哪一步起。")
        return EXIT_OK if rec.stage == Stage.done else EXIT_FAIL
    if args.force and target is None:
        rec.stage = Stage.persona

    rec.diagnosis = None
    runner.store.save(rec)
    _head(f"接着跑 {args.name}")
    return _report(runner.run([rec.job])[-1], cfg, as_json=args.json)


def _resume_like_create(cfg: Config, job: NpcJob, args) -> int:
    """`--forget` 之后按原定义从头跑一遍（复用 create 的路，不复制一遍它）。"""
    inner = argparse.Namespace(
        config=getattr(args, "config", None), state_dir=getattr(args, "state_dir", None),
        output_folder=getattr(args, "output_folder", None), comfy_url=getattr(args, "comfy_url", None),
        plugin_config_dir=getattr(args, "plugin_config_dir", None),
        unity_exe=getattr(args, "unity_exe", None), unity_project=getattr(args, "unity_project", None),
        llm_base_url=getattr(args, "llm_base_url", None), llm_api_key=getattr(args, "llm_api_key", None),
        llm_model=getattr(args, "llm_model", None), max_attempts=getattr(args, "max_attempts", None),
        name=job.name, desc=job.description, role=job.role, persona_hint=job.persona_hint,
        actions=" ".join(job.actions), directions=" ".join(job.directions),
        movement=job.movement, collider=job.collider,
        script=list(job.scripts), seed=job.seed,
        prompt=None, portrait=None, until=getattr(args, "until", None), json=args.json,
    )
    return cmd_create(inner)


def cmd_doctor(args) -> int:
    cfg = _config_from(args)

    if args.init:
        target = cfg.state_path / "config.toml"
        if target.is_file() and not args.force:
            _say(f"配置已经在了，没动它：{target}")
            _say("  要重新生成：加 --force（会覆盖）")
        else:
            _say(f"已写出配置模板：{cfg.save(target)}")
            _say("  填好 [unity] / [comfyui] / [llm] 三段后再跑一次 doctor。")

    _head("配置")
    _say(cfg.describe())

    hard = 0

    # ── 插件侧：工作流与 bindings ──
    _head("插件配置（工作流与 bindings）")
    config_dir = cfg.plugin_config_dir
    from .tools import comfy
    if not config_dir.is_dir():
        _say(f"  ✗ 目录不存在：{config_dir}")
        _say("      Agent 与插件包同仓库时这里是自动算出来的；装在别处请配 config_dir。")
        _say("      不知道插件包在哪就问 Unity：")
        _say("        Unity.exe -batchmode -projectPath <工程> "
             "-executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd env")
        hard += 1
    else:
        _say(f"  ✓ {config_dir}")
        for name in (comfy.PORTRAIT_WORKFLOW, comfy.KEEPOSE_WORKFLOW):
            workflow, bindings = comfy.resolve_workflow(config_dir, name)
            for path in (workflow, bindings):
                if path.is_file():
                    _say(f"      ✓ {path.name}")
                else:
                    _say(f"      ✗ 缺 {path.name}")
                    hard += 1

    # ── 姿势参考库 ──
    _head("姿势参考库（严格两层：<动作>/<朝向>/*.png）")
    roots = ref_roots(cfg)
    found = False
    for root in roots:
        if not root.is_dir():
            _say(f"  · 不存在：{root}")
            continue
        found = True
        pairs = sorted(f"{d.parent.name}/{d.name}"
                       for d in root.glob("*/*") if d.is_dir() and any(d.glob("*.png")))
        _say(f"  ✓ {root}")
        _say(f"      {len(pairs)} 组：{', '.join(pairs) if pairs else '（空）'}")
    if not found:
        _say("  ✗ 两处都没有。逐帧生成会直接失败，而且会失败得很晚（跑完立绘才轮到它）。")
        _say("      从视频造一套：npcforge refs <视频> --action walk --direction right")
        hard += 1

    # ── ComfyUI ──
    _head("ComfyUI")
    try:
        # 探活时把客户端自己的输出吞掉：`cmd_probe` 会打一遍，我们下面还要按 doctor 的
        # 格式再打一遍——两份输出叠在一起就是这个「在线：…　✓ …」的重复。
        info = comfy.ComfyClient(config_dir, url=cfg.comfy_url, alpha_cutoff=cfg.alpha_cutoff,
                                 log=lambda _m: None).probe()
        _say(f"  ✓ ComfyUI {info['version']} @ {info['url']}")
        for device in info.get("devices", []):
            total, free = device.get("vram_total", 0), device.get("vram_free", 0)
            _say(f"      {device.get('name', '?')}：显存 "
                 f"{(total - free) / 2**30:.1f} / {total / 2**30:.1f} GB 已用")
        _say("  ⚠ 逐帧生成还要 ComfyUI Desktop 的窗口开着——SAM 3D 估计要前端参与，"
             "无头跑不了。")
    except Exception as e:
        _say(f"  ✗ 连不上 {cfg.comfy_url}：{e}")
        _say("      地址用 localhost 而不是 127.0.0.1：本机 8000 上常另有一个只绑 "
             "127.0.0.1 的服务抢答，症状是 /upload/image 回 405。")
        hard += 1

    # ── Unity ──
    _head("Unity（装配 prefab 与自检靠它）")
    if not cfg.unity_ready:
        _say("  · 没配齐（unity_exe / unity_project 缺一个）")
        _say("      人设与出图不需要 Unity；装配那一步必须有。")
    else:
        from .tools.unity import UnityBridge
        try:
            bridge = UnityBridge(cfg.unity_exe, cfg.unity_project, cfg.unity_timeout)
            if bridge.probe(timeout=180, log=_say):
                _say("  ✓ 可以驱动（首次启动 Unity 批处理要一两分钟，属正常）")
            else:
                _say("  ✗ 能启动但命令没成功——多半是脚本编译没过，开一次编辑器看控制台")
                hard += 1
        except Exception as e:
            _say(f"  ✗ {e}")
            hard += 1

    # ── LLM ──
    _head("外部 LLM（四个裁决点）")
    if cfg.llm_ready:
        from .agents import build_model
        try:
            handle = build_model(cfg)
            _say(f"  ✓ {handle.model_name} @ {handle.base_url}"
                 f"{'' if handle.is_openai else '（非 OpenAI 端点，已关掉 strict 假设）'}")
        except Exception as e:
            _say(f"  ✗ 配了但搭不起来：{e}")
            hard += 1
    else:
        _say("  · 未配齐 → 人设与裁决走确定性规则，日志里会标 [降级]。")
        _say("      这不是错误：没有模型整条流水线照样能跑，只是人设朴素、裁决按数字来。")

    _head("结论")
    if hard:
        _say(f"  {hard} 处硬伤，先修这些，别的都往后放。")
        return EXIT_FAIL
    _say('  没有硬伤。可以开始：npcforge create <角色名> "一句话描述"')
    return EXIT_OK


def cmd_refs(args) -> int:
    cfg = _config_from(args)
    from .tools import media

    video = Path(args.video).expanduser()
    if not video.is_file():
        _say(f"[用法错] 找不到视频：{video}")
        return EXIT_USAGE

    if args.action and args.direction:
        root = Path(args.pose_root).expanduser() if args.pose_root else ref_roots(cfg)[0]
        out_dir = root / args.action / args.direction
        _say(f"目标：{out_dir}  （严格两层，插件按这个结构找）")
    else:
        out_dir = Path(args.out).expanduser()
        _say(f"目标：{out_dir}")
        _say("  插件的姿势参考库要求 <动作>/<朝向>/*.png 两层。"
             "想直接进库：--action walk --direction right")

    _head(f"视频 → 姿势参考帧（{args.phases} 个相位）")
    try:
        report = media.prepare_refs(video, out_dir, phases=args.phases, stride=args.stride,
                                    tolerance=args.tolerance, log=_say)
    except Exception as e:
        _say(f"  ✗ {type(e).__name__}: {e}")
        return EXIT_FAIL

    _head("结果")
    _say(f"  抽帧 {report['frames_extracted']} 张，测出周期 {report['cycle']} 帧"
         f"（平均差 {report['cycle_score']}）")
    _say(f"  取样 {len(report['refs'])} 个相位 → {report['out_dir']}")
    _say()
    _say("  周期是**直接帧对法**测的，不是差分自相关——后者会出半周期谐波，"
         "29 帧的走路会测成 15 帧。")
    _say("  抽出来的原片留在 _raw/ 里，确认相位选对了再删。")
    return EXIT_OK


# ────────────────────────── 参数表 ──────────────────────────


def _stage_or_none(value: str | None) -> Stage | None:
    return Stage(value) if value else None


def _common() -> argparse.ArgumentParser:
    """给子命令挂配置开关的「母解析器」。"""
    parent = argparse.ArgumentParser(add_help=False)
    _add_config_opts(parent)
    return parent


def _add_job_opts(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("name", nargs="?", help="角色名（会变成目录名 / prefab 名，用拼音或英文）")
    parser.add_argument("desc", nargs="?", metavar="描述",
                        help="一句话说清身份、服饰、道具、神态。只回答「看起来是什么样」")
    parser.add_argument("--name", dest="name_flag", metavar="名", help="角色名（与位置参数二选一）")
    parser.add_argument("--desc", dest="desc_flag", metavar="描述", help="同上")
    parser.add_argument("--role", help="定位，如「导览员」「铁匠」。进人设，不出图")
    parser.add_argument("--persona-hint", dest="persona_hint", metavar="线索",
                        help="人设补充：世界观、口癖、禁忌")
    parser.add_argument("--actions", default="walk", metavar="列表",
                        help="动作，逗号或空格分隔（默认 walk；walk/run 是循环动作）")
    parser.add_argument("--directions", default="down,up,right,left", metavar="列表",
                        help="朝向，只能是 down/up/right/left——它们会变成目录名")
    parser.add_argument("--movement", choices=("none", "patrol", "manual"), default="none",
                        help="prefab 上的移动方式")
    parser.add_argument("--collider", action=argparse.BooleanOptionalAction, default=True,
                        help="是否加碰撞体（默认加）")
    parser.add_argument("--script", action="append", metavar="类型全名",
                        help="要挂到 prefab 上的组件，可重复给")
    parser.add_argument("--seed", type=int, help="不给则按角色名派生，保证同角色可复现")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="npcforge",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description="CharForge 的自主 NPC 创作 Agent：一句话描述或一份角色列表，"
                    "跑出一批可直接拖进 Unity 场景的 2D NPC。",
        epilog="例：\n"
               "  npcforge doctor --init\n"
               '  npcforge create 铁匠老王 "驼背的矮人铁匠，皮围裙，右臂一道烫伤"\n'
               '  npcforge create 卫兵 "披甲长枪兵" --until prompt      # 先看人设与提示词\n'
               "  npcforge batch npcs.yaml --only 铁匠老王,卫兵\n"
               "  npcforge status && npcforge retry 卫兵 --from frames\n"
               "  npcforge refs walk.mp4 --action walk --direction right\n"
               "配置开关（--config / --comfy-url / --unity-exe …）写在各子命令之后。\n"
               "退出码：0 成功 / 1 失败 / 2 用法错。",
    )
    parser.add_argument("--version", action="version", version=f"npcforge {__version__}")
    _add_config_opts(parser)

    sub = parser.add_subparsers(dest="cmd", required=True)

    create = sub.add_parser("create", help="一个角色：一句话描述 → 完整产物", parents=[_common()])
    _add_job_opts(create)
    create.add_argument("--prompt", help="直接给定图提示词，跳过提示词锻造")
    create.add_argument("--portrait", metavar="PNG", help="直接用现成立绘，跳过出图")
    create.add_argument("--until", choices=STOPPABLE, help="跑到这一阶段就停（不标记完成）")
    create.add_argument("--json", action="store_true", help="以 JSON 输出结果")
    create.set_defaults(func=cmd_create)

    batch = sub.add_parser("batch", help="一份角色列表（YAML）→ 一批产物", parents=[_common()])
    batch.add_argument("file", metavar="角色列表.yaml")
    batch.add_argument("--only", help="只跑这几个角色（逗号分隔）")
    batch.add_argument("--until", choices=STOPPABLE, help="跑到这一阶段就停")
    batch.set_defaults(func=cmd_batch)

    status = sub.add_parser("status", help="看作业跑到哪一步了", parents=[_common()])
    status.add_argument("name", nargs="?", help="给角色名看详情，不给就列全部")
    status.add_argument("--json", action="store_true")
    status.set_defaults(func=cmd_status)

    retry = sub.add_parser("retry", help="接着跑 / 重跑一个作业", parents=[_common()])
    retry.add_argument("name", help="角色名")
    retry.add_argument("--from", dest="from_stage", choices=STOPPABLE,
                       help="从哪一阶段起重跑（默认接着当前进度）")
    retry.add_argument("--force", action="store_true", help="作业已到终态时也要它再跑一遍")
    retry.add_argument("--clean", action="store_true", help="先清掉中间产物（记录保留）")
    retry.add_argument("--forget", action="store_true", help="丢掉记录，完全从头来")
    retry.add_argument("--until", choices=STOPPABLE, help="跑到这一阶段就停")
    retry.add_argument("--json", action="store_true")
    retry.set_defaults(func=cmd_retry)

    doctor = sub.add_parser("doctor", help="体检 + 生成配置模板", parents=[_common()])
    doctor.add_argument("--init", action="store_true", help="写出一份配置模板")
    doctor.add_argument("--force", action="store_true", help="配合 --init：覆盖已有配置")
    doctor.set_defaults(func=cmd_doctor)

    refs = sub.add_parser("refs", help="视频 → 姿势参考帧", parents=[_common()])
    refs.add_argument("video", metavar="视频")
    refs.add_argument("--out", default="./refs", metavar="DIR", help="输出目录")
    refs.add_argument("--action", help="放进姿势参考库时指定动作名，如 walk")
    refs.add_argument("--direction", choices=("down", "up", "right", "left"),
                      help="放进姿势参考库时指定朝向")
    refs.add_argument("--pose-root", dest="pose_root", metavar="DIR",
                      help="姿势参考库的根（默认取 Unity 工程里那一处）")
    refs.add_argument("--phases", type=int, default=8, help="取几个相位（默认 8，与插件一致）")
    refs.add_argument("--stride", type=int, default=1, help="抽帧步长")
    refs.add_argument("--tolerance", type=int, default=38, help="去背景的颜色容差")
    refs.set_defaults(func=cmd_refs)

    return parser


# ────────────────────────── 入口 ──────────────────────────


def main(argv: list[str] | None = None) -> int:
    """**永远返回一个整数。** 不往外抛 `SystemExit`——被别的程序调用时那会很难受。"""
    _force_utf8()
    parser = build_parser()

    try:
        args = parser.parse_args(argv)

        # 位置参数与开关二选一；脚本里更常用开关，所以开关优先。
        if args.cmd == "create":
            args.name = getattr(args, "name_flag", None) or args.name
            args.desc = getattr(args, "desc_flag", None) or args.desc
            if not args.name or not args.desc:
                _say("[用法错] create 需要角色名与描述：")
                _say('  npcforge create <角色名> "一句话描述"')
                return EXIT_USAGE
            if not getattr(args, "actions", None):
                args.actions = "walk"

        return args.func(args)

    except KeyboardInterrupt:
        _say()
        _say("[中断] 用户取消。已完成的阶段都落盘了，再跑同一条命令就接着走。")
        return 130
    except SystemExit as e:
        # argparse 的用法错（2）与 --version（0）都从这里出去。
        code = e.code
        if code is None:
            return EXIT_OK
        return code if isinstance(code, int) else EXIT_USAGE


if __name__ == "__main__":
    sys.exit(main())
