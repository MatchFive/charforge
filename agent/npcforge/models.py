"""npcforge 的数据契约。

**这个文件是整套系统的宪法。** Agent 的阶段推进、工具层的输入输出、
以及落盘的状态文件，全部只认这里的类型。改这里的字段名等于改契约，
要同步改三处：`loop.py` 的阶段实现、`tools/` 的返回构造、以及已有作业的状态文件。

两个来自宿主的硬约束（不是设计选择，是既定事实）：

1. **`PersonaSpec.persona_prompt` 必须存在。** Unity 侧 `NpcCharacter.GetSystemPrompt()`
   是从 persona.json 里按字符串找 `"persona_prompt"` 这个键的——它不用 JSON 库，
   是手写的字符扫描。键名少一个字母，运行期拿到的就是空字符串，而且不报错。
2. **朝向名是 `down / up / right / left`**，与 `NpcCharacter.FacingDirection`
   （South=0 / North=1 / East=2 / West=3）同序。这些字符串会变成目录名
   （`Frames/walk_down/`），改名就接不上插件。
"""

from __future__ import annotations

from enum import StrEnum
from typing import Literal

from pydantic import BaseModel, Field, field_validator

# 与 NpcCharacter.FacingDirection 同序：down=0 / up=1 / right=2 / left=3
DIRECTIONS = ("down", "up", "right", "left")

# 这套朝向的默认放大系数来自 keep_pose 的实测：背面/正面走路的肢体摆幅在
# 2D 投影里天然只有侧面的 1/3~1/4，不放大就会被扩散端的"站姿先验"吞掉。
DEFAULT_DIRECTIONS = ("down", "up", "right", "left")

# 插件窗口里现有的动作。walk 是循环的，其余按一次性动作处理
# （`FrameSet.IsLoop` 只认 walk/run；death 播完停在末帧）。
KNOWN_ACTIONS = ("walk", "run", "attack", "jump", "death")


class Stage(StrEnum):
    """作业的阶段。顺序即推进方向，`done` / `failed` 是终态。

    用 `StrEnum` 而不是 `(str, Enum)`：两者序列化结果一样，但后者的 `str()` 会给出
    `"Stage.pending"` 这种带类名的形式，于是日志里 `f"{stage}"` 与 `stage.value` 长得不一样，
    迟早有人在某处写了前者而看起来"也像对的"。字符串枚举就该只有一个字符串形态。
    """

    pending = "pending"
    persona = "persona"
    prompt = "prompt"
    portrait = "portrait"
    frames = "frames"
    qa = "qa"
    assemble = "assemble"
    scripting = "scripting"
    done = "done"
    failed = "failed"


# ────────────────────────── 输入 ──────────────────────────


class NpcJob(BaseModel):
    """一个 NPC 的创作需求。既能从一句话生成，也能从角色列表里读出来。"""

    name: str = Field(description="角色名。会变成目录名 / prefab 名 / characterId —— 建议拼音或英文")
    description: str = Field(description="一句话说清身份、服饰、道具、神态。**只回答「看起来是什么样」**")
    role: str = Field(default="", description="定位，如「导览员」「铁匠」。进人设不出图")
    persona_hint: str = Field(default="", description="人设补充线索：世界观、口癖、禁忌")
    actions: list[str] = Field(default_factory=lambda: ["walk"])
    directions: list[str] = Field(default_factory=lambda: list(DEFAULT_DIRECTIONS))
    movement: Literal["none", "patrol", "manual"] = "none"
    collider: bool = True
    scripts: list[str] = Field(default_factory=list, description="要挂到 prefab 上的组件类型全名")
    seed: int | None = Field(default=None, description="不给则按角色名派生，保证同角色可复现")

    @field_validator("name")
    @classmethod
    def _name_is_path_safe(cls, v: str) -> str:
        v = v.strip()
        if not v:
            raise ValueError("角色名不能为空——产物目录靠它命名。")
        bad = set('\\/:*?"<>|')
        if bad & set(v):
            raise ValueError(f"角色名不能含路径字符 {''.join(sorted(bad))}：{v}")
        return v

    @field_validator("actions")
    @classmethod
    def _actions_known(cls, v: list[str]) -> list[str]:
        unknown = [a for a in v if a not in KNOWN_ACTIONS]
        if unknown:
            # 不拦，只提醒：插件的动作名由帧组目录决定，理论上可以自定义。
            # 但未知动作会被当成"一次性动作"，播完不回默认状态，容易踩坑。
            pass
        return v or ["walk"]

    @field_validator("directions")
    @classmethod
    def _directions_valid(cls, v: list[str]) -> list[str]:
        bad = [d for d in v if d not in DIRECTIONS]
        if bad:
            raise ValueError(f"未知朝向 {bad}；可选 {list(DIRECTIONS)}"
                             "（这几个名字会变成 Frames/<动作>_<朝向>/ 目录名，不能自创）")
        return v or list(DEFAULT_DIRECTIONS)


class CharacterList(BaseModel):
    """批量输入的角色列表。`batch` 命令读的就是它。"""

    defaults: dict = Field(default_factory=dict, description="整份列表共用的默认值")
    characters: list[NpcJob] = Field(default_factory=list)

    @field_validator("characters")
    @classmethod
    def _not_empty(cls, v: list[NpcJob]) -> list[NpcJob]:
        if not v:
            raise ValueError("角色列表是空的。")
        names = [c.name for c in v]
        dup = {n for n in names if names.count(n) > 1}
        if dup:
            raise ValueError(f"角色名重复：{sorted(dup)}——它们会互相覆盖产物目录。")
        return v


# ────────────────────────── LLM 裁决点的输出 ──────────────────────────


class PersonaSpec(BaseModel):
    """人设。会被写成 `<产物目录>/persona.json`，供 prefab 上的 NpcCharacter 消费。"""

    character_id: str = Field(description="与 NpcJob.name 一致")
    display_name: str = Field(description="给玩家看的中文显示名，可以带称谓")
    role: str = Field(description="一句话定位")
    personality: list[str] = Field(min_length=3, max_length=6, description="性格特质，每条一个短句")
    speech_style: str = Field(description="说话方式：语速、用词、口癖")
    background: str = Field(description="背景故事，2~4 句")
    knowledge_scope: list[str] = Field(description="它懂的事（3~6 条）")
    boundaries: list[str] = Field(description="它不懂 / 不谈的事，避免对话时乱编（2~4 条）")
    greeting: str = Field(description="第一次见面说的一句话")
    dialogue_samples: list[str] = Field(min_length=3, max_length=6, description="示例对话，展示语气")
    persona_prompt: str = Field(
        description="最终喂给对话模型的系统提示词。**必须包含角色身份、语气、知识边界、"
                    "以及「不知道就说不确定、不要编造」这条硬约束**。字段名固定为 persona_prompt")


class PromptDraft(BaseModel):
    """文生图提示词的锻造结果。"""

    prompt: str = Field(description="英文提示词，只写角色本体（身份、服饰、道具、神态），不写画风")
    reasoning: str = Field(default="", description="一句话说明做了哪些取舍")


class FrameStats(BaseModel):
    """一组帧的质检数据。由工具层算出来，喂给裁决者。"""

    group: str = Field(description="<动作>_<朝向>")
    frames: int
    size: str = Field(default="", description="如 1024x1024")
    size_uniform: bool = True
    opaque_pct_min: float = 0.0
    opaque_pct_max: float = 0.0
    semi_pct_max: float = 0.0
    mean_diff: float = 0.0
    width_range_ratio: float = 0.0
    center_range_ratio: float = 0.0
    problems: list[str] = Field(default_factory=list)

    @property
    def span_ratio(self) -> float:
        return max(self.width_range_ratio, self.center_range_ratio)


class Verdict(StrEnum):
    accept = "accept"
    retry_same = "retry_same"
    retry_new_seed = "retry_new_seed"
    retry_amplify = "retry_amplify"
    reject = "reject"


class FrameVerdict(BaseModel):
    """对一组帧的裁决。**这是 Agent 自主性的主要来源**——
    "重跑一次"和"换姿势参考"和"加大放大系数"在数据上长得不一样，但只有看报告才能分辨。
    """

    group: str = Field(default="", description="<动作>_<朝向>，与 FrameStats.group 对应")
    verdict: Verdict
    reason: str = Field(description="说清依据的是哪个数字，别写「看起来还行」")
    next_amplify: float | None = Field(default=None, description="verdict=retry_amplify 时给出新系数")
    confidence: float = Field(default=0.0, ge=0.0, le=1.0)


class Diagnosis(BaseModel):
    """重试仍失败时的诊断。它决定"下一步换什么策略"，而不是"再试一次"。"""

    category: Literal[
        "service_down",     # ComfyUI / Unity 没在跑或连不上
        "frontend_offline", # keep_pose 需要 Desktop 前端在线
        "missing_asset",    # 姿势参考库 / 立绘 / 工作流文件缺失
        "vram",             # 显存不足
        "bad_reference",    # 姿势参考本身有问题（背景脏、人物太小、非实拍）
        "prompt_regression",# 提示词把透明背景句顶掉了之类的回归
        "config_mismatch",  # bindings 与 workflow 对不上
        "unknown",
    ]
    explanation: str
    action: Literal["retry", "fix_and_retry", "skip_group", "abort_job", "ask_user"]
    detail: str = Field(default="", description="给用户看的具体建议，含要改的文件路径或命令")


# ────────────────────────── 落盘状态 ──────────────────────────


class StageLog(BaseModel):
    stage: Stage
    ok: bool
    seconds: float = 0.0
    message: str = ""
    at: str = Field(default="", description="ISO 时间戳")


class JobRecord(BaseModel):
    """一个作业的完整状态。写进 `.npcforge/jobs/<name>.json`，用来断点续跑。"""

    job: NpcJob
    stage: Stage = Stage.pending
    attempts: int = 0
    seed: int = 0
    amplify: dict[str, float] = Field(default_factory=dict, description="按朝向记录的放大系数")
    persona: PersonaSpec | None = None
    prompt: PromptDraft | None = None
    portrait_path: str = ""
    frame_groups: list[str] = Field(default_factory=list, description="已完成落盘的帧组名")
    redo_groups: list[str] = Field(default_factory=list,
                                   description="质检判定需要重跑的组——下一轮只跑这些，不重跑全部")
    stats: list[FrameStats] = Field(default_factory=list)
    verdicts: list[FrameVerdict] = Field(default_factory=list)
    diagnosis: Diagnosis | None = None
    prefab_path: str = ""
    log: list[StageLog] = Field(default_factory=list)

    def note(self, stage: Stage, ok: bool, message: str = "", seconds: float = 0.0) -> None:
        from datetime import datetime
        self.log.append(StageLog(stage=stage, ok=ok, message=message, seconds=round(seconds, 1),
                                 at=datetime.now().isoformat(timespec="seconds")))
