"""主循环：作业队列 + 阶段推进 + 自主裁决。

**它不是 ReAct 式的自由循环。** 阶段顺序是固定的（人设 → 提示词 → 立绘 → 帧 → 质检 →
装配 → 脚本），自主性体现在**每个阶段内部的判断**：

* 帧不合格时，它自己决定"原样重跑"还是"加大姿势放大系数"还是"换种子"；
* 重试仍失败时，它自己诊断出是"ComfyUI 前端不在线"还是"参考图不对"；
* 一个 NPC 失败**不阻塞**同批的其它 NPC。

这么设计的原因是：**这条流水线 90% 的步骤是确定性的**。让模型自由驱动每一步会带来
三个具体代价——每步一次模型往返（慢）、同一角色两次跑结果不同（不可复现）、
出错时分不清"模型想歪了"还是"工具坏了"。骨架确定、判断点自主，两头的好处都拿到。
"""

from __future__ import annotations

import shutil
import time
from pathlib import Path

from .agents import diagnose, forge_persona, forge_prompt, judge_frames
from .config import Config
from .models import JobRecord, NpcJob, Stage, Verdict
from .state import JobStore
from .tools import comfy, qa
from .tools.unity import UnityBridge, UnityError

# 各朝向的默认姿势放大系数。背面/正面走路的肢体摆幅在 2D 投影里天然只有侧面的 1/3~1/4，
# 不放大就会被扩散端的"站姿先验"吞掉（人偶在跨步、成图站桩）。
DEFAULT_AMPLIFY = {"up": 2.2, "down": 2.0, "left": 1.0, "right": 1.0}

# 阶段的推进顺序。`stop_after` 靠它比较"跑过头了没有"，
# 不能拿 `Stage` 的声明位置当序号——`failed` 排在最后，但它不是"最远"的阶段。
_STAGE_ORDER = (Stage.pending, Stage.persona, Stage.prompt, Stage.portrait,
                Stage.frames, Stage.qa, Stage.assemble, Stage.scripting, Stage.done)


def _rank(stage: Stage) -> int:
    try:
        return _STAGE_ORDER.index(stage)
    except ValueError:
        return 99


def ref_roots(cfg: Config) -> list[Path]:
    """姿势参考库的查找根，按优先级排列（与插件窗口的两级查找同序）：

    1. 用户覆盖：`<Unity 工程>/CharForge/comfyui/pose_refs/`
    2. 随包默认：`<插件包>/Configs/comfyui/pose_refs/`

    `doctor` 与 `refs` 也要用它——**三处各写一遍路径拼接就是三份会漂移的真相**。
    """
    roots: list[Path] = []
    if cfg.unity_project:
        roots.append(Path(cfg.unity_project).expanduser() / "CharForge" / "comfyui" / "pose_refs")
    roots.append(Path(cfg.plugin_config_dir) / "pose_refs")
    return roots


class NpcForgeRunner:
    def __init__(self, cfg: Config, store: JobStore | None = None, log=print,
                 stop_after: Stage | None = None):
        self.cfg = cfg
        self.store = store or JobStore(cfg.state_path)
        self.store.ensure()
        self._log = log
        # `--until`：跑完这一阶段就停下，**不标记完成**。用于"先看人设和提示词再决定要不要出图"
        # 这类先验后行的场景——状态留在磁盘上，之后 `npcforge retry` 或直接再跑一次就接着走。
        self.stop_after = stop_after
        self._unity: UnityBridge | None = None
        self._comfy: comfy.ComfyClient | None = None

    # ── 日志 ──────────────────────────────────────────

    def log(self, message: str, job: str | None = None) -> None:
        self._log(message)
        if job:
            self.store.append_log(job, message)

    # ── 惰性构造桥（配置错时只在真要用的那一刻才炸） ──

    @property
    def unity(self) -> UnityBridge:
        if self._unity is None:
            self._unity = UnityBridge(self.cfg.unity_exe, self.cfg.unity_project,
                                      self.cfg.unity_timeout)
        return self._unity

    @property
    def comfy(self) -> comfy.ComfyClient:
        if self._comfy is None:
            self._comfy = comfy.ComfyClient(
                self.cfg.plugin_config_dir, url=self.cfg.comfy_url,
                alpha_cutoff=self.cfg.alpha_cutoff,
                portrait_timeout=self.cfg.portrait_timeout,
                frames_timeout=self.cfg.frames_timeout,
                log=lambda m: self.log(m))
        return self._comfy

    def work_dir(self, name: str) -> Path:
        path = self.cfg.state_path / "work" / name
        path.mkdir(parents=True, exist_ok=True)
        return path

    # ── 批量入口 ──────────────────────────────────────

    def run(self, jobs: list[NpcJob], resume: bool = True) -> list[JobRecord]:
        records = []
        total = len(jobs)
        for index, job in enumerate(jobs, 1):
            self.log(f"\n{'=' * 64}\n[{index}/{total}] {job.name} — {job.description[:40]}")
            rec = self.store.load(job.name) if resume else None
            if rec is None:
                rec = self.store.create(job)
            else:
                rec.job = job          # 以本次定义为准（描述可能改过）
                if rec.stage in (Stage.done, Stage.failed):
                    self.log(f"  已完成过（{rec.stage.value}），跳过。"
                             "要重跑请先 `npcforge retry`。", job.name)
                    records.append(rec)
                    continue

            try:
                rec = self.run_one(rec)
            except KeyboardInterrupt:
                self.store.save(rec)
                self.log(f"  已中断，进度已存。用 `npcforge retry {job.name}` 接着跑。", job.name)
                records.append(rec)
                break
            except Exception as e:
                # 一个 NPC 崩了不能带走整批——这是批量的基本要求。
                self._fail(rec, rec.stage, f"{type(e).__name__}: {e}")
                self.store.save(rec)
            records.append(rec)

        return records

    def run_one(self, rec: JobRecord) -> JobRecord:
        """把一个作业推到终态。阶段顺序固定，守卫防止状态机打转。"""
        guard = 0
        while rec.stage not in (Stage.done, Stage.failed):
            guard += 1
            if guard > 40:
                self._fail(rec, rec.stage, "阶段推进次数异常（疑似状态机打转），已中止")
                break

            handler = getattr(self, f"_do_{rec.stage.value}", None)
            if handler is None:
                self._fail(rec, rec.stage, f"没有 {rec.stage.value} 阶段的实现")
                break

            try:
                rec = handler(rec)
            except Exception as e:
                self._fail(rec, rec.stage, f"{type(e).__name__}: {e}")
                break

            self.store.save(rec)

            # 判据是"刚跑完的这一阶段"而不是"下一阶段"：`_do_persona` 返回时 stage 已经
            # 是 prompt 了，若拿它比较，`--until persona` 会误判成还没到。
            if self.stop_after is not None and _rank(rec.stage) > _rank(self.stop_after):
                self.log(f"  ⏸ 已按 --until 停在 {self.stop_after.value} 之后"
                         f"（当前进度 {rec.stage.value}）。要继续就去掉 --until 或跑 retry。",
                         rec.job.name)
                break
        return rec

    def _fail(self, rec: JobRecord, stage: Stage, context: str) -> None:
        """失败 → 诊断 → 落盘。诊断决定"下一步该做什么"，而不是"再试一次"。"""
        self.log(f"  ✗ {stage.value} 阶段失败：{context}", rec.job.name)
        helper = _helper(self.cfg)
        diag = diagnose(stage.value, context, self.cfg,
                        extra={"角色": rec.job.name, "尝试次数": rec.attempts},
                        log=lambda m: self.log("  " + m, rec.job.name))
        rec.diagnosis = diag
        rec.stage = Stage.failed
        rec.note(stage, False, context)
        self.log(f"  → 诊断：{diag.explanation}", rec.job.name)
        self.log(f"  → 建议：{diag.detail}", rec.job.name)
        if helper:
            self.log(f"  → 环境提示：{helper}", rec.job.name)

    # ── 阶段：人设 ────────────────────────────────────

    def _do_pending(self, rec: JobRecord) -> JobRecord:
        # 种子在这里定死，且**只定一次**：之后的重试才谈得上"换种子"（`_next_seed`）。
        # 不在这里派生的话，`JobRecord.seed` 一直是 0，而 0 是"合法值"不是"未设置"——
        # 于是每个角色都用同一个种子出图，谁也不会报错。这正是最难发现的那类 bug。
        if not rec.seed:
            rec.seed = rec.job.seed if rec.job.seed is not None else comfy.stable_seed(rec.job.name)
            self.log(f"  种子定为 {rec.seed}"
                     f"{'（角色指定）' if rec.job.seed is not None else '（按角色名派生）'}"
                     f"。同角色重跑结果一致；要变就得走换种子那条路。", rec.job.name)
        rec.stage = Stage.persona
        return rec

    def _do_persona(self, rec: JobRecord) -> JobRecord:
        if rec.persona is not None:
            rec.stage = Stage.prompt
            return rec

        started = time.time()
        self.log("  · 补人设…", rec.job.name)
        spec = forge_persona(rec.job, self.cfg, log=lambda m: self.log("    " + m, rec.job.name))
        rec.persona = spec

        path = self.cfg.character_dir(rec.job.name) / "persona.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(spec.model_dump_json(indent=2), encoding="utf-8")
        self.log(f"    persona.json → {path}", rec.job.name)

        rec.note(Stage.persona, True, spec.display_name, time.time() - started)
        rec.stage = Stage.prompt
        return rec

    # ── 阶段：提示词 ──────────────────────────────────

    def _do_prompt(self, rec: JobRecord) -> JobRecord:
        if rec.prompt is not None:
            rec.stage = Stage.portrait
            return rec

        started = time.time()
        self.log("  · 锻造提示词…", rec.job.name)
        draft = forge_prompt(rec.job, self.cfg, log=lambda m: self.log("    " + m, rec.job.name))
        rec.prompt = draft
        self.log(f"    {draft.prompt[:110]}{'…' if len(draft.prompt) > 110 else ''}", rec.job.name)

        rec.note(Stage.prompt, True, draft.reasoning, time.time() - started)
        rec.stage = Stage.portrait
        return rec

    # ── 阶段：立绘 ────────────────────────────────────

    def _do_portrait(self, rec: JobRecord) -> JobRecord:
        existing = Path(rec.portrait_path) if rec.portrait_path else None
        if existing and existing.is_file():
            self.log(f"  · 已有立绘，沿用：{existing}", rec.job.name)
            rec.stage = Stage.frames
            return rec

        started = time.time()
        out = self.work_dir(rec.job.name) / "portrait.png"
        self.log(f"  · 出立绘（种子 {rec.seed}）…", rec.job.name)
        report = self.comfy.portrait(rec.prompt.prompt, out, seed=rec.seed, name=rec.job.name)

        # **透明底是硬门禁，不是提醒。** 立绘的下一站是 FrameNormalizer 的 alpha 裁切，
        # 一张实心底的立绘会让它按整张图算包围盒——不报错，只是后面每一帧都错。
        # 在这里挡住，比让它拖到质检那一步再回头查便宜得多。
        if not report.get("alpha_ok", True):
            raise RuntimeError(
                f"立绘没有透明背景（{report['size']}，不透明 {report['opaque_pct']}%，"
                f"通道数 {report['channels']}）。\n"
                "  这几乎总是提示词被整体覆盖了——透明底句与画风前缀在 bindings 的 "
                "prompt_parts 里，必须包在角色本体外面。\n"
                f"  图还在，可以自己看一眼：{out}")

        rec.portrait_path = str(out)
        self.log(f"    立绘 {report['size']}，不透明 {report['opaque_pct']}%，"
                 f"提示词 {report.get('prompt_chars', 0)} 字（含固定件）", rec.job.name)

        rec.note(Stage.portrait, True, report["size"], time.time() - started)
        rec.stage = Stage.frames
        return rec

    # ── 阶段：帧动画 ──────────────────────────────────

    def _do_frames(self, rec: JobRecord) -> JobRecord:
        wanted = [f"{a}_{d}" for a in rec.job.actions for d in rec.job.directions]
        todo = rec.redo_groups or wanted
        started = time.time()

        for group in todo:
            action, direction = _split_group(group)
            refs = self._resolve_refs(action, direction)
            amplify = rec.amplify.get(direction, DEFAULT_AMPLIFY.get(direction, 1.0))

            self.log(f"  · {group}：{len(list(refs.glob('*.png')))} 张参考，"
                     f"放大 ×{amplify}（种子 {rec.seed}）", rec.job.name)
            self.log("    ⚠ 姿势由 SAM 3D 实时估计，ComfyUI Desktop 前端必须在线", rec.job.name)

            raw_dir = self.work_dir(rec.job.name) / group
            self.comfy.frames(rec.portrait_path, refs, raw_dir, direction=direction,
                              amplify=amplify, seed=rec.seed, name=rec.job.name)

            # 归一化（alpha 裁切 / 全批统一缩放 / 脚底锚定）**交给插件的 import 命令**——
            # 外部复刻这套规则会造出第二份真相，迟早漂移。
            res = self.unity.run("import", {
                "character": rec.job.name, "group": group, "src": str(raw_dir),
                "folder": self.cfg.output_folder,
            }, log=lambda m: self.log("    " + m, rec.job.name))
            if not res.ok:
                raise UnityError(f"{group} 导入失败：{res.summary()}")

            if group not in rec.frame_groups:
                rec.frame_groups.append(group)

        rec.redo_groups = []
        rec.note(Stage.frames, True, f"{len(todo)} 组", time.time() - started)
        rec.stage = Stage.qa
        return rec

    def _resolve_refs(self, action: str, direction: str) -> Path:
        """姿势参考库：用户覆盖优先，其次随包默认（与插件两级查找同序）。"""
        searched = []
        for root in ref_roots(self.cfg):
            candidate = root / action / direction
            searched.append(candidate)
            if candidate.is_dir() and any(candidate.glob("*.png")):
                return candidate

        raise FileNotFoundError(
            f"找不到姿势参考：<动作>/<朝向> = {action}/{direction}\n"
            + "\n".join(f"  查过：{p}" for p in searched)
            + "\n  目录约定是严格两层：<动作>/<朝向>/*.png。"
            + f"\n  用 `npcforge refs <视频> --action {action} --direction {direction}` 从视频造一套。"
            + "\n  **这时候不要去改提示词**——缺参考就先补参考。")

    # ── 阶段：质检与裁决 ──────────────────────────────

    def _do_qa(self, rec: JobRecord) -> JobRecord:
        started = time.time()
        char_dir = self.cfg.character_dir(rec.job.name)
        stats = qa.audit_character(char_dir, self.cfg.alpha_cutoff,
                                   self.cfg.min_frame_diff, self.cfg.min_span_ratio)
        if not stats:
            raise RuntimeError("没有可体检的帧组——先跑帧阶段。")

        rec.stats = stats
        verdicts, redo = [], []

        for s in stats:
            direction = _split_group(s.group)[1]
            current = rec.amplify.get(direction, DEFAULT_AMPLIFY.get(direction, 1.0))
            v = judge_frames(s, self.cfg, attempt=rec.attempts + 1,
                             current_amplify=current,
                             log=lambda m: self.log("    " + m, rec.job.name))
            verdicts.append(v)

            if v.verdict == Verdict.accept:
                continue
            if v.verdict == Verdict.reject:
                self.log(f"    {s.group} 被判定放弃：{v.reason}", rec.job.name)
                continue
            if v.verdict == Verdict.retry_amplify and v.next_amplify:
                rec.amplify[direction] = v.next_amplify
            if v.verdict == Verdict.retry_new_seed:
                rec.seed = _next_seed(rec.seed)
                self.log(f"    换种子 → {rec.seed}", rec.job.name)
            redo.append(s.group)

        rec.verdicts = verdicts
        rec.note(Stage.qa, not redo,
                 f"{len(stats) - len(redo)}/{len(stats)} 组通过", time.time() - started)

        if redo and rec.attempts < self.cfg.max_attempts:
            rec.redo_groups = redo
            rec.attempts += 1
            self.log(f"  ↻ 第 {rec.attempts} 次重跑，涉及 {len(redo)} 组：{', '.join(redo)}",
                     rec.job.name)
            rec.stage = Stage.frames
            return rec

        if redo:
            raise RuntimeError(
                f"重试 {rec.attempts} 次后仍有 {len(redo)} 组不达标：{', '.join(redo)}。\n"
                "  帧还在磁盘上，但按判据它们不合格。先看 <state_dir>/logs/"
                f"{rec.job.name}.log 里的质检数字，再决定是换姿势参考还是放宽阈值。")

        rec.stage = Stage.assemble
        return rec

    # ── 阶段：装配 ────────────────────────────────────

    def _do_assemble(self, rec: JobRecord) -> JobRecord:
        started = time.time()
        self.log("  · 装配 prefab…", rec.job.name)
        res = self.unity.assemble(
            rec.job.name, folder=self.cfg.output_folder,
            movement=rec.job.movement, collider=rec.job.collider,
            fps=None, dialogue=True,
            log=lambda m: self.log("    " + m, rec.job.name))
        if not res.ok:
            raise UnityError(f"装配失败：{res.summary()}")

        ok, groups = self.unity.verify(rec.job.name, self.cfg.output_folder)
        bad = [g["group"] for g in groups if not g["ok"]]
        if not ok:
            raise UnityError(f"装配自检未通过，这些片段里没有帧：{', '.join(bad)}")

        rec.prefab_path = f"{self.cfg.output_folder}/{rec.job.name}/NPC_{rec.job.name}.prefab"
        self.log(f"    {rec.prefab_path}", rec.job.name)
        rec.note(Stage.assemble, True, f"{len(groups)} 个片段", time.time() - started)
        rec.stage = Stage.scripting
        return rec

    # ── 阶段：脚本挂载 ────────────────────────────────

    def _do_scripting(self, rec: JobRecord) -> JobRecord:
        char_dir = self.cfg.character_dir(rec.job.name)
        persona = char_dir / "persona.json"
        persona_rel = self.cfg.asset_rel(persona) if persona.is_file() else None

        display = rec.persona.display_name if rec.persona else None
        if not rec.job.scripts and not persona_rel and not display:
            rec.stage = Stage.done
            return rec

        started = time.time()
        self.log("  · 绑定人设与组件…", rec.job.name)
        res = self.unity.attach(rec.job.name, folder=self.cfg.output_folder,
                                scripts=rec.job.scripts or None,
                                persona=persona_rel, display_name=display,
                                log=lambda m: self.log("    " + m, rec.job.name))
        if not res.ok:
            raise UnityError(f"绑定失败：{res.summary()}")

        rec.note(Stage.scripting, True, f"{len(rec.job.scripts)} 个组件", time.time() - started)
        rec.stage = Stage.done
        self.log(f"  ✓ 完成：{rec.job.name}", rec.job.name)
        return rec

    # ── 重跑 ──────────────────────────────────────────

    def retry(self, name: str, from_stage: Stage | None = None) -> JobRecord | None:
        rec = self.store.load(name)
        if rec is None:
            return None
        rec.stage = from_stage or Stage.persona
        rec.attempts = 0
        rec.diagnosis = None
        rec.redo_groups = []
        return self.run_one(rec)

    def clean(self, name: str) -> None:
        """清掉一个角色的中间产物（保留状态记录）。"""
        work = self.cfg.state_path / "work" / name
        if work.is_dir():
            shutil.rmtree(work, ignore_errors=True)


def _split_group(group: str) -> tuple[str, str]:
    if "_" not in group:
        raise ValueError(f"帧组名必须是 <动作>_<朝向>：{group}")
    action, _, direction = group.rpartition("_")
    return action, direction


def _next_seed(seed: int) -> int:
    """换种子。用固定步长而不是随机——**同一批重跑应当可复现**。"""
    return (seed * 1103515245 + 12345) & 0x7FFFFFFF


def _helper(cfg: Config) -> str:
    """失败时顺手给一句环境提示（多数失败其实是环境没就绪）。"""
    tips = []
    if not cfg.llm_ready:
        tips.append("LLM 未配齐（人设与裁决走确定性规则）")
    if not cfg.unity_ready:
        tips.append("Unity 路径未配齐")
    if not cfg.plugin_config_dir.is_dir():
        tips.append(f"插件配置目录不存在：{cfg.plugin_config_dir}")
    return "；".join(tips)
