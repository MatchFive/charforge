"""npcforge —— CharForge 的自主 NPC 创作 Agent。

输入一句话角色描述或一份角色列表，自己跑完：
人设 → 提示词 → 立绘 → 逐帧动画 → 质检 → 装配 prefab → 挂脚本，
产物直接落在 Unity 工程的 `Assets/CharForge/<角色>/`。

它不是 Unity 插件的一部分，而是一个**外部调用者**：通过命令行驱动
CharForge 插件的 CLI、通过 HTTP 驱动本机 ComfyUI。所以插件单独也能用，
两条路互不依赖。
"""

__version__ = "0.1.0"

from .config import Config
from .models import (
    DIRECTIONS,
    CharacterList,
    Diagnosis,
    FrameStats,
    FrameVerdict,
    JobRecord,
    NpcJob,
    PersonaSpec,
    PromptDraft,
    Stage,
    Verdict,
)

__all__ = [
    "__version__",
    "Config",
    "DIRECTIONS",
    "CharacterList",
    "Diagnosis",
    "FrameStats",
    "FrameVerdict",
    "JobRecord",
    "NpcJob",
    "PersonaSpec",
    "PromptDraft",
    "Stage",
    "Verdict",
]
