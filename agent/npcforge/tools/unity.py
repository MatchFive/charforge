"""Unity 桥：把 CharForge 插件的命令行入口包成 Python 调用。

约定（与 `docs/cli.md` 一致，别改）：

* 入口是 `-executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run` 加 `-cmd <命令>`。
* **绝不加 `-quit`。** 加了之后 Unity 会在方法返回后立刻退出并把退出码固定成 0，
  于是"装配失败"和"装配成功"在调用方眼里一模一样。CLI 自己给退出码：
  0 成功 / 1 失败 / 2 用法错。
* 判据写 `code == 0 才算过`，**不要**写 `code != 1 就算过`——
  脚本编译不过时 Unity 压根不执行 executeMethod，退出码是别的值。

输出里机器可读的部分用 `CHARFORGE_*_BEGIN` / `_END` 包着，这里按块解析；
块外的内容原样进日志，出问题时能看到 Unity 自己说的话。
"""

from __future__ import annotations

import re
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path

ENTRY = "CharForge.EditorTools.Cli.CharForgeCli.Run"
BLOCK_RE = re.compile(r"CHARFORGE_(\w+)_BEGIN\n(.*?)\nCHARFORGE_\1_END", re.DOTALL)


class UnityError(RuntimeError):
    pass


@dataclass
class UnityResult:
    command: str
    code: int
    stdout: str
    stderr: str
    seconds: float = 0.0
    blocks: dict = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return self.code == 0

    @property
    def compiled_ok(self) -> bool:
        """编译失败时 Unity 根本不会执行 executeMethod——这个区分很重要。"""
        return "Script Compilation Error" not in self.stdout

    def failures(self) -> list[str]:
        """从输出里挑出真正该给人看的错。"""
        out = []
        for line in self.stdout.splitlines():
            s = line.strip()
            if s.startswith("error CS") or "Script Compilation Error" in s:
                out.append(s)
            elif s.startswith("[CharForge CLI]") and ("✗" in s or "失败" in s or "异常" in s):
                out.append(s)
        return out[:12]

    def summary(self) -> str:
        if self.ok:
            return "成功"
        if not self.compiled_ok:
            return "Unity 脚本编译失败——executeMethod 没被执行（先修编译，再看别的）"
        errs = self.failures()
        return "；".join(errs) if errs else f"退出码 {self.code}"


class UnityBridge:
    """调用插件 CLI。所有方法都同步阻塞（Unity 批处理本身就是个前台进程）。"""

    def __init__(self, unity_exe: str, project: str, timeout: int = 900):
        if not unity_exe:
            raise UnityError("没配 Unity 编辑器路径（配置文件里的 unity_exe，或 UNITY_EXE 环境变量）。")
        if not project:
            raise UnityError("没配 Unity 工程路径（配置文件里的 unity_project，或 UNITY_PROJECT 环境变量）。")
        self.exe = unity_exe
        self.project = str(Path(project).expanduser())
        self.timeout = timeout

    # ── 底座 ──────────────────────────────────────────

    def run(self, cmd: str, params: dict | None = None, timeout: int | None = None,
            log=None) -> UnityResult:
        argv = [self.exe, "-batchmode", "-projectPath", self.project, "-executeMethod", ENTRY,
                "-cmd", cmd]
        for key, value in (params or {}).items():
            if value is None:
                continue
            if isinstance(value, bool):
                argv += [f"-{key}", "true" if value else "false"]
            elif isinstance(value, (list, tuple)):
                for item in value:
                    argv += [f"-{key}", str(item)]
            else:
                argv += [f"-{key}", str(value)]

        started = time.time()
        try:
            proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                                  errors="replace", timeout=timeout or self.timeout)
            code, out, err = proc.returncode, proc.stdout or "", proc.stderr or ""
        except subprocess.TimeoutExpired as e:
            code = -1
            out = (e.stdout or "") if isinstance(e.stdout, str) else ""
            err = f"超时（{timeout or self.timeout}s）"

        result = UnityResult(command=f"{cmd} {' '.join(f'-{k} {v}' for k, v in (params or {}).items())}",
                             code=code, stdout=out, stderr=err,
                             seconds=round(time.time() - started, 1))

        for name, body in BLOCK_RE.findall(out):
            result.blocks[name] = body

        if log:
            log(f"[unity] {result.command} → 退出码 {code}（{result.seconds}s）")
            if not result.ok:
                log(f"[unity] {result.summary()}")
        return result

    # ── 命令 ──────────────────────────────────────────

    def env(self, log=None) -> dict:
        """读插件报出来的环境事实。**先跑它再配别的东西**，别靠猜路径。"""
        res = self.run("env", log=log)
        body = res.blocks.get("ENV")
        if not res.ok or not body:
            raise UnityError(f"读不到环境信息：{res.summary()}")
        env = {}
        for line in body.splitlines():
            if "=" in line:
                key, _, value = line.partition("=")
                env[key.strip()] = value.strip()
        return env

    def scan(self, folder: str | None = None, log=None) -> list[dict]:
        """列出产物目录里每个角色的就绪状态。"""
        res = self.run("scan", {"folder": folder}, log=log)
        return _parse_characters(res.blocks.get("SCAN", ""))

    def assemble(self, character: str, folder: str | None = None,
                 movement: str = "none", collider: bool = True, fps: int | None = None,
                 dialogue: bool = True, speed: float | None = None, log=None) -> UnityResult:
        """从磁盘帧重建并装配 prefab。CLI 内部装配完会自动自检一次。"""
        return self.run("assemble", {
            "character": character, "folder": folder, "movement": movement,
            "collider": collider, "fps": fps, "dialogue": dialogue, "speed": speed,
        }, log=log)

    def verify(self, character: str, folder: str | None = None, log=None) -> tuple[bool, list[dict]]:
        """数片段里真实的关键帧 vs 帧 PNG 张数。返回 (是否全通过, 每组明细)。"""
        res = self.run("verify", {"character": character, "folder": folder}, log=log)
        return res.ok, _parse_groups(res.blocks.get("VERIFY", ""))

    def attach(self, character: str, folder: str | None = None, scripts: list[str] | None = None,
               persona: str | None = None, display_name: str | None = None, log=None) -> UnityResult:
        """挂自定义组件，并把 persona.json / 中文显示名绑到 prefab 上。"""
        return self.run("attach", {
            "character": character, "folder": folder,
            "script": scripts or None,          # CLI 侧收重复 key
            "persona": persona, "displayName": display_name,
        }, log=log)

    def probe(self, timeout: int = 60, log=None) -> bool:
        """只想知道"这个 Unity 能不能被我驱动"时用（比如 doctor）。"""
        res = self.run("scan", timeout=timeout, log=log)
        return res.ok


def _parse_characters(body: str) -> list[dict]:
    out = []
    current = None
    for line in body.splitlines():
        stripped = line.strip()
        if line.startswith("character="):
            parts = dict(kv.split("=", 1) for kv in stripped.split() if "=" in kv)
            current = {
                "name": parts.get("character", ""),
                "dir": parts.get("dir", ""),
                "groups": int(parts.get("groups", 0) or 0),
                "frames": int(parts.get("frames", 0) or 0),
                "prefab": parts.get("prefab") == "yes",
                "controller": parts.get("controller") == "yes",
                "persona": parts.get("persona") == "yes",
                "detail": [],
            }
            out.append(current)
        elif stripped.startswith("group=") and current is not None:
            parts = dict(kv.split("=", 1) for kv in stripped.split() if "=" in kv)
            current["detail"].append({
                "group": parts.get("group", ""),
                "frames": int(parts.get("frames", 0) or 0),
                "anim_keys": int(parts.get("anim_keys", 0) or 0),
                "ok": parts.get("ok") == "yes",
            })
    return out


def _parse_groups(body: str) -> list[dict]:
    out = []
    for line in body.splitlines():
        stripped = line.strip()
        if not stripped.startswith("group="):
            continue
        parts = dict(kv.split("=", 1) for kv in stripped.split() if "=" in kv)
        out.append({
            "group": parts.get("group", ""),
            "frames": int(parts.get("frames", 0) or 0),
            "anim_keys": int(parts.get("anim_keys", 0) or 0),
            "ok": parts.get("ok") == "yes",
        })
    return out
