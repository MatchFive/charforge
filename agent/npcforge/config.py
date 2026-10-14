"""配置：Agent 要知道"东西都在哪"。

取值优先级（先命中者胜）：命令行参数 → 环境变量 → 配置文件 → 仓库内相对推导 → 默认值。

**为什么要有"仓库内相对推导"这一档**：Agent 与 Unity 插件包在同一个仓库里
（`agent/` 与 `packages/com.charforge/` 平级），而它硬依赖插件包的
`Configs/comfyui/`（工作流 JSON 与 bindings）。这条链不该让用户手填——
同仓库就直接算出来。装在别处时才需要配 `config_dir`。
"""

from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field, fields
from pathlib import Path

# agent/npcforge/config.py → 上溯三级到仓库根
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
PLUGIN_CONFIG_DEFAULT = REPO_ROOT / "packages" / "com.charforge" / "Configs" / "comfyui"

TRUE_WORDS = {"1", "true", "yes", "on"}


def _env(name: str, fallback: str = "") -> str:
    return os.environ.get(name, fallback).strip()


def _env_bool(name: str, fallback: bool) -> bool:
    raw = os.environ.get(name)
    return fallback if raw is None else raw.strip().lower() in TRUE_WORDS


@dataclass
class Config:
    """运行时配置。所有路径都存成字符串，便于写回 TOML。"""

    # —— Unity 侧 ——
    unity_exe: str = ""
    unity_project: str = ""

    # —— ComfyUI 侧 ——
    # ⚠ 用 localhost 而不是 127.0.0.1：本机 8000 常被另一个只绑 127.0.0.1 的服务抢答
    comfy_url: str = "http://localhost:8000"
    config_dir: str = ""

    # —— 产物与运行状态 ——
    output_folder: str = "Assets/CharForge"
    state_dir: str = ""

    # —— 外部 LLM（OpenAI 兼容 /chat/completions）——
    llm_base_url: str = ""
    llm_api_key: str = ""
    llm_model: str = ""
    llm_no_think: bool = False

    # —— 质量门禁 ——
    alpha_cutoff: int = 64
    min_frame_diff: float = 8.0
    min_span_ratio: float = 0.02
    max_attempts: int = 3

    # —— 超时（秒）——
    portrait_timeout: int = 300
    frames_timeout: int = 900
    unity_timeout: int = 900

    # —— 派生字段（不写进配置文件）——
    _source: str = field(default="", repr=False)

    # ── 派生路径 ──────────────────────────────────────────

    @property
    def plugin_config_dir(self) -> Path:
        """插件包里的 `Configs/comfyui`（工作流 + bindings 的所在地）。"""
        if self.config_dir:
            return Path(self.config_dir).expanduser()
        return PLUGIN_CONFIG_DEFAULT

    @property
    def state_path(self) -> Path:
        """运行状态目录：作业记录、日志、缓存。"""
        if self.state_dir:
            return Path(self.state_dir).expanduser()
        return REPO_ROOT / "agent" / ".npcforge"

    @property
    def assets_root(self) -> Path:
        """Unity 侧的产物根（绝对路径）。`output_folder` 是 Assets 相对路径。"""
        project = Path(self.unity_project).expanduser() if self.unity_project else Path(".")
        return project / self.output_folder

    def character_dir(self, name: str) -> Path:
        return self.assets_root / name

    def asset_rel(self, path: Path) -> str:
        """把绝对路径转成 Unity 认识的 Assets 相对路径（正斜杠）。"""
        try:
            return path.resolve().relative_to(
                (Path(self.unity_project).expanduser() / "Assets").resolve()
            ).as_posix()
        except ValueError:
            return path.as_posix()

    @property
    def llm_ready(self) -> bool:
        """三项齐了才走模型（与插件 PromptForgeService 同一套判定）。"""
        return bool(self.llm_base_url and self.llm_model)

    @property
    def unity_ready(self) -> bool:
        return bool(self.unity_exe and self.unity_project)

    # ── 载入与落盘 ────────────────────────────────────────

    @classmethod
    def load(cls, explicit: str | None = None) -> Config:
        """按优先级装配配置。"""
        path = Path(explicit).expanduser() if explicit else cls._default_config_file()
        data: dict = {}
        if path and path.is_file():
            with path.open("rb") as fh:
                raw = tomllib.load(fh)
            # save() 写的是分节 TOML（[unity] / [llm]…），load() 就也得认分节——
            # **写和读必须走同一套形状**，否则"自己生成的模板自己读不回来"
            # （0.6.1 实证：doctor --init 出的文件，下一轮 doctor 全显示未配）。
            # 平铺键也照收，两种写法混用都行（平铺键后合并，同键平铺胜出）。
            for section in raw.values():
                if isinstance(section, dict):
                    data.update(section)
            data.update({k: v for k, v in raw.items() if not isinstance(v, dict)})

        cfg = cls()
        for f in fields(cls):
            if f.name.startswith("_") or f.name not in data:
                continue
            setattr(cfg, f.name, data[f.name])

        # 环境变量覆盖面：够用就好，不做全字段映射
        cfg.unity_exe = _env("UNITY_EXE", cfg.unity_exe)
        cfg.unity_project = _env("UNITY_PROJECT", cfg.unity_project)
        cfg.comfy_url = _env("COMFYUI_URL", cfg.comfy_url)
        cfg.config_dir = _env("CHARFORGE_CONFIG_DIR", cfg.config_dir)
        cfg.llm_base_url = _env("NPCFORGE_LLM_BASE_URL", _env("LLM_BASE_URL", cfg.llm_base_url))
        cfg.llm_api_key = _env("NPCFORGE_LLM_API_KEY", _env("LLM_API_KEY", cfg.llm_api_key))
        cfg.llm_model = _env("NPCFORGE_LLM_MODEL", _env("LLM_MODEL", cfg.llm_model))
        cfg.llm_no_think = _env_bool("NPCFORGE_LLM_NO_THINK", cfg.llm_no_think)
        cfg.state_dir = _env("NPCFORGE_STATE_DIR", cfg.state_dir)

        cfg._source = str(path) if path else "(默认值)"
        if not cfg.state_dir:
            base = Path(path).parent if path else REPO_ROOT / "agent"
            cfg.state_dir = str(base / ".npcforge")
        return cfg

    @staticmethod
    def _default_config_file() -> Path | None:
        for candidate in (
            Path.cwd() / ".npcforge" / "config.toml",
            Path.cwd() / "npcforge.toml",
            REPO_ROOT / "agent" / ".npcforge" / "config.toml",
        ):
            if candidate.is_file():
                return candidate
        return None

    def save(self, path: Path | None = None) -> Path:
        """写回 TOML。`doctor --init` 用它生成一份可编辑的模板。"""
        target = path or (self.state_path / "config.toml")
        target.parent.mkdir(parents=True, exist_ok=True)

        lines = ["# npcforge 配置。改完直接生效，不用重启。", ""]
        section = ""
        for f in fields(self):
            if f.name.startswith("_"):
                continue
            head = _SECTION_OF.get(f.name, "")
            if head != section:
                section = head
                lines.append(f"\n[{section}]" if section else "")
            value = getattr(self, f.name)
            lines.append(f"{f.name} = {_toml_value(value)}")
        target.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return target

    def describe(self) -> str:
        lines = [f"配置来源：{self._source}"]
        lines.append(f"  Unity 编辑器   {self.unity_exe or '（未配）'}")
        lines.append(f"  Unity 工程     {self.unity_project or '（未配）'}")
        lines.append(f"  产物根         {self.assets_root}")
        lines.append(f"  ComfyUI        {self.comfy_url}")
        cfg_dir = self.plugin_config_dir
        lines.append(f"  插件配置目录   {cfg_dir}{'' if cfg_dir.is_dir() else '  ← 不存在！'}")
        lines.append(f"  运行状态       {self.state_path}")
        if self.llm_ready:
            lines.append(f"  LLM            {self.llm_model} @ {self.llm_base_url}")
        else:
            lines.append("  LLM            （未配齐，人设与裁决会退回确定性规则）")
        return "\n".join(lines)


_SECTION_OF = {
    "unity_exe": "unity",
    "unity_project": "unity",
    "comfy_url": "comfyui",
    "config_dir": "comfyui",
    "output_folder": "paths",
    "state_dir": "paths",
    "llm_base_url": "llm",
    "llm_api_key": "llm",
    "llm_model": "llm",
    "llm_no_think": "llm",
    "alpha_cutoff": "quality",
    "min_frame_diff": "quality",
    "min_span_ratio": "quality",
    "max_attempts": "quality",
    "portrait_timeout": "timeouts",
    "frames_timeout": "timeouts",
    "unity_timeout": "timeouts",
}


def _toml_value(value) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    text = str(value).replace("\\", "/")
    return '"' + text.replace('"', '\\"') + '"'
