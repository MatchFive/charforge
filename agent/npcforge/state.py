"""作业状态的落盘与恢复。

**为什么状态一定要落盘**：一轮批量可能跑几十个 NPC、花几个小时。中途
ComfyUI 崩了、Unity 被关掉、或者你按了 Ctrl+C —— 这些都不该让已经跑完的部分白费。
每个作业的阶段、种子、放大系数、质检结论都写进 `.npcforge/jobs/<name>.json`，
`npcforge retry` 或 `npcforge status` 直接接着跑。

格式刻意选**人类可读的 JSON**：出问题时要能直接打开看它卡在哪一步，
而不是去翻二进制或日志。
"""

from __future__ import annotations

from datetime import datetime
from pathlib import Path

from .models import JobRecord, NpcJob, Stage


class JobStore:
    def __init__(self, state_dir: Path):
        self.root = Path(state_dir) / "jobs"
        self.log_dir = Path(state_dir) / "logs"

    def ensure(self) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        self.log_dir.mkdir(parents=True, exist_ok=True)

    def path(self, name: str) -> Path:
        return self.root / f"{name}.json"

    def log_path(self, name: str) -> Path:
        """每个角色一份追加式日志——批量跑时控制台输出会互相穿插，翻它更清楚。"""
        self.log_dir.mkdir(parents=True, exist_ok=True)
        return self.log_dir / f"{name}.log"

    def append_log(self, name: str, message: str) -> None:
        stamp = datetime.now().strftime("%H:%M:%S")
        with self.log_path(name).open("a", encoding="utf-8") as fh:
            fh.write(f"[{stamp}] {message}\n")

    def load(self, name: str) -> JobRecord | None:
        path = self.path(name)
        if not path.is_file():
            return None
        try:
            return JobRecord.model_validate_json(path.read_text(encoding="utf-8"))
        except Exception as e:
            # 状态文件读不了不该让整个流程炸——把它当成"没有记录"，
            # 调用方会从阶段 0 重来（帧若已在磁盘上，装配那步仍能续上）。
            self.append_log(name, f"[警告] 状态文件读不了，将从头开始：{e}")
            return None

    def save(self, record: JobRecord) -> Path:
        self.ensure()
        path = self.path(record.job.name)
        path.write_text(record.model_dump_json(indent=2), encoding="utf-8")
        return path

    def all(self) -> list[JobRecord]:
        if not self.root.is_dir():
            return []
        out = []
        for f in sorted(self.root.glob("*.json")):
            rec = self.load(f.stem)
            if rec:
                out.append(rec)
        return out

    def create(self, job: NpcJob) -> JobRecord:
        rec = JobRecord(job=job, stage=Stage.pending)
        self.save(rec)
        return rec

    def forget(self, name: str) -> bool:
        path = self.path(name)
        if path.is_file():
            path.unlink()
            return True
        return False


def status_line(rec: JobRecord) -> str:
    """给 `npcforge status` 用的一行摘要。"""
    marks = {
        Stage.done: "✓ 完成",
        Stage.failed: "✗ 失败",
        Stage.pending: "· 待开始",
    }
    label = marks.get(rec.stage, f"… {rec.stage.value}")
    extra = []
    if rec.frame_groups:
        extra.append(f"{len(rec.frame_groups)} 组帧")
    if rec.prefab_path:
        extra.append("有 prefab")
    if rec.attempts:
        extra.append(f"尝试 {rec.attempts} 次")
    tail = f"（{'，'.join(extra)}）" if extra else ""
    detail = ""
    if rec.stage == Stage.failed and rec.diagnosis:
        detail = f"  ← {rec.diagnosis.explanation}"
    return f"{rec.job.name:<20} {label}{tail}{detail}"
