"""故障诊断：重试仍失败时，判断"该换什么策略"。

它的价值不在"解释错误"（日志里已经有了），而在**把错误映射到一个可执行的动作**：
是等 ComfyUI 起来、是换参考图、是改配置、还是该找人。
没有这一层，Agent 的自主循环只会变成"重试到次数用尽然后放弃"。
"""

from __future__ import annotations

from ..config import Config
from ..models import Diagnosis
from .base import LlmUnavailable, brief, build_model, guard_structured

# 关键词 → 诊断。这套规则**先于** LLM 跑：命中就直接用，
# 因为这些都是历史上真实踩过的、形态确定的错，没必要问模型。
KNOWN = (
    ("frontend sync failed", "frontend_offline", "fix_and_retry",
     "ComfyUI Desktop 的前端窗口不在线（或没加载 keep_pose 工作流）。"
     "SAM 3D 估计要前端参与，无头跑不了——保持 Desktop 窗口打开、把 "
     "charforge_keepose_pose.workflow.json 载入后重跑。"),
    ("no limb rotations matched", "frontend_offline", "ask_user",
     "vnccs 的姿势放大补丁没命中关节——上行载荷结构变了，补丁的注入点需要跟着改。"
     "这一条要人看一眼，见 docs/vnccs-pose-amplify-patch.md。"),
    ("out of memory", "vram", "fix_and_retry",
     "显存不够。立绘路线在 16 GB 上要用 INT8 DiT + W4A8 编码器；"
     "关掉别的占显存的程序，或把并发生成降到 1。"),
    ("pose studio frontend sync", "frontend_offline", "fix_and_retry",
     "同上：ComfyUI Desktop 前端必须在线。"),
    ("方法 not allowed", "config_mismatch", "fix_and_retry",
     "端口被别的本地服务抢答了。用 localhost 而不是 127.0.0.1，"
     "或给其中一个服务换端口。"),
    ("method not allowed", "config_mismatch", "fix_and_retry",
     "端口被别的本地服务抢答了（报文形状是 FastAPI 的，不是 ComfyUI 的）。"
     "地址改 localhost，或给其中一个服务换端口。"),
    ("connection refused", "service_down", "retry",
     "服务没在跑。ComfyUI Desktop 默认监听 8000；Unity 那边确认工程路径对不对。"),
    ("script compilation error", "config_mismatch", "fix_and_retry",
     "Unity 脚本编译不过，executeMethod 根本没被执行。先在编辑器里把编译错误清掉。"),
    ("cannot connect to comfyui", "service_down", "retry", "本机 ComfyUI 没起来。"),
    ("未找到姿势参考库", "missing_asset", "fix_and_retry",
     "姿势参考库不在。放 `<工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png`，"
     "注意严格两层目录。**这时候不要去调提示词**。"),
    ("找不到工作流文件", "missing_asset", "fix_and_retry",
     "工作流 JSON 没找到。确认 config_dir 指向的是 <插件包>/Configs/comfyui。"),
    ("一个背景像素都没去掉", "bad_reference", "fix_and_retry",
     "参考视频的背景不纯（花纹 / 渐变 / 绿幕不均）。换一段背景干净的视频，"
     "或调大 --tolerance。"),
    ("没有可用的帧组", "missing_asset", "skip_group",
     "Frames/ 下没有帧组目录。目录名必须是 <动作>_<朝向>，里面放 frame-N.png。"),
    ("帧尺寸不一致", "bad_reference", "skip_group",
     "这一组帧的尺寸不统一，pivot 对不齐。整组重跑，别只补单帧。"),
    ("立绘没有透明背景", "prompt_regression", "fix_and_retry",
     "立绘被画成了实心底——提示词里的透明底句被顶掉了。固定件（透明底句 / 画风前缀 / "
     "版式后缀）由插件持有、放在工作流 bindings 的 prompt_parts 里，必须包在角色本体外面。"
     "确认 config_dir 指向的是当前版本的插件包，跑 python scripts/check_prompt_parts.py 校验三处是否一致。"),
)

SYSTEM = """你诊断 CharForge 流水线的失败。给你失败发生的阶段和日志片段。

你要判断出：属于哪一类问题、下一步该做什么动作、以及一句给用户看的具体建议。

严格的取向：**宁可说"我不确定、需要人看一眼"，也不要猜一个看似合理的错。**
错误的诊断会把用户引到错误的方向，比直接报错更浪费时间。

给用户的建议里要包含可操作的信息：具体哪个文件、哪条命令、在哪看。
"""


def diagnose(stage: str, context: str, cfg: Config,
             extra: dict | None = None, log=print) -> Diagnosis:
    """先查已知错（快且准），再问模型（处理没见过的形态）。"""
    hit = _match_known(context)
    if hit:
        log(f"[诊断] 命中已知错：{hit.category} → {hit.action}")
        return hit

    try:
        handle = build_model(cfg)
    except LlmUnavailable as e:
        log(f"[降级] 诊断走兜底：{brief(e)}")
        return _unknown(stage, context, cfg)

    from pydantic_ai import Agent

    agent = Agent(handle.model, output_type=Diagnosis, instructions=SYSTEM)

    ask = [f"失败阶段：{stage}", "", "失败上下文：", context[-3000:]]
    for key, value in (extra or {}).items():
        ask.append(f"{key}: {value}")

    log(f"[llm] 故障诊断 → {handle.model_name}")
    result = agent.run_sync("\n".join(ask))
    diag = result.output
    guard_structured(diag, "诊断", log)
    log(f"[llm] 诊断：{diag.category} → {diag.action}：{diag.explanation}")
    return diag


def _match_known(context: str) -> Diagnosis | None:
    low = context.lower()
    for needle, category, action, detail in KNOWN:
        if needle.lower() in low:
            return Diagnosis(category=category, action=action,
                             explanation=f"命中已知故障特征「{needle}」。", detail=detail)
    return None


def _unknown(stage: str, context: str, cfg: Config) -> Diagnosis:
    """兜底：不猜，把原始信息整理好交给人。

    这里刻意**不返回一个"看似合理"的分类**。一个错的 category 会让
    Agent 去做错误的动作（比如把"参考图不对"当成"服务没起"去重试），
    而 `ask_user` 至少不会把用户带偏。
    """
    tail = "\n".join(context.strip().splitlines()[-12:])
    return Diagnosis(
        category="unknown",
        action="ask_user",
        explanation=f"{stage} 阶段失败，且不在已知故障特征里。",
        detail=("把下面这段交给能看日志的人：\n" + tail
                + "\n\n完整日志在 <state_dir>/logs/<角色名>.log。"),
    )


def describe_ladder() -> str:
    """给用户看的一句话：已知故障的处理顺序。"""
    return "已知故障先按特征匹配（快且准），匹配不到才问模型。"
