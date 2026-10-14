"""质量裁决：读质检报告，决定「收下 / 重跑 / 换参数」。

**这是 Agent 自主性的主要来源。** 三种坏法在数据上分得很开，但只有看报告才分得清：

| 报告里的形态 | 真正的问题 | 该做的动作 |
|---|---|---|
| `semi_pct_max` 高 | 收割时漏了 alpha 压平 | 原地重跑（`retry_same`） |
| `mean_diff` 低 + `span_ratio` 低 | 姿势被站姿先验吞掉 | 加大放大系数（`retry_amplify`） |
| 一切正常但就是不像 | 采样运气 | 换种子（`retry_new_seed`） |
| 尺寸不一致 / 空帧 | 帧本身坏了 | 原地重跑，且**不要**碰参数 |

LLM 介入时给它看的就是这些数，并要求它引用具体数字，不接受"看起来还行"。
没有 LLM 时走同一套规则（`_rule_verdict`），结果应当一致。
"""

from __future__ import annotations

from ..config import Config
from ..models import FrameStats, FrameVerdict, Verdict
from .base import LlmUnavailable, brief, build_model, guard_structured

AMPLIFY_STEP = 1.3      # 每次追加的放大倍率
AMPLIFY_CEILING = 3.5   # 超过这个值姿势会畸形，到顶就换别的策略

SYSTEM = """你是 2D 角色帧动画的质检裁决者。给你一组帧的量化报告，你要决定怎么处理。

先看 `problems`：里面是被程序判定为确定性的缺陷（半透明噪点、空帧、尺寸不一致）。
有任何一条，就是"这一组不能用"，不要被其他数字说服。

再看动作幅度：
- `mean_diff`（相邻帧平均亮度差）：低于 8 说明帧与帧几乎没差别。
- `span_ratio`（轮廓变化 / 体高）：低于 0.02 说明各帧姿态高度雷同。

**关键判断**：如果 `mean_diff` 和 `span_ratio` 都低，而 `problems` 里没有 alpha 类问题，
那这不是"画坏了"，而是**姿势约束太弱**——人偶在跨步，但扩散端被站姿先验拉回去了。
正确的动作是 `retry_amplify` 并给出更大的系数，而不是重跑同一套参数。

裁决选项：
- `accept`     —— 可以收下。轻微问题（比如个别帧边缘略糙）不值得重跑。
- `retry_same` —— 原参数重跑。用于 alpha 类或帧损坏类问题（参数没错，是执行问题）。
- `retry_new_seed` —— 换个种子。用于一切都正常但外观不理想时。
- `retry_amplify` —— 加大姿势放大系数。用于"姿势太弱"这一类，必须给出 `next_amplify`。
- `reject`     —— 这一组放弃（比如参数已经到顶还是不行）。

`reason` 必须引用报告里的**具体数字**，不要写"看起来还行"或"质量不错"。
"""


def judge_frames(stats: FrameStats, cfg: Config, attempt: int = 1,
                 current_amplify: float = 1.0, log=print) -> FrameVerdict:
    """对一组帧出裁决。"""
    try:
        handle = build_model(cfg)
    except LlmUnavailable as e:
        log(f"[降级] 质量裁决走规则：{brief(e)}")
        return _rule_verdict(stats, cfg, attempt, current_amplify, log)

    from pydantic_ai import Agent

    agent = Agent(handle.model, output_type=FrameVerdict, instructions=SYSTEM)

    ask = [
        f"帧组：{stats.group}",
        f"帧数：{stats.frames}，尺寸：{stats.size}（一致：{stats.size_uniform}）",
        f"不透明像素占比：{stats.opaque_pct_min}% ~ {stats.opaque_pct_max}%",
        f"半透明像素峰值：{stats.semi_pct_max}%",
        f"帧间平均差：{stats.mean_diff}",
        f"轮廓变化/体高：{stats.width_range_ratio}",
        f"中心横移/体高：{stats.center_range_ratio}",
        f"已检出问题：{stats.problems if stats.problems else '（无）'}",
        f"这是第 {attempt} 次尝试，当前放大系数 {current_amplify}",
        f"判据：帧差 ≥{cfg.min_frame_diff}，轮廓变化 ≥{cfg.min_span_ratio}",
    ]

    log(f"[llm] 质量裁决 {stats.group} → {handle.model_name}")
    result = agent.run_sync("\n".join(ask))
    verdict = result.output
    guard_structured(verdict, f"裁决({stats.group})", log)

    verdict = _clamp(verdict, stats, cfg, attempt, current_amplify, log)
    verdict.group = stats.group
    log(f"[llm] 裁决 {stats.group}：{verdict.verdict.value} — {verdict.reason}")
    return verdict


def _rule_verdict(stats: FrameStats, cfg: Config, attempt: int,
                  current_amplify: float, log) -> FrameVerdict:
    """没有 LLM 时的确定性裁决。**与上面那套判据完全一致**，只是不用模型。"""
    problems = stats.problems

    alpha_like = [p for p in problems if any(k in p for k in ("半透明", "alpha", "空帧", "不一致"))]
    weak_motion = (stats.mean_diff < cfg.min_frame_diff
                   or stats.span_ratio < cfg.min_span_ratio)

    if alpha_like:
        v = FrameVerdict(verdict=Verdict.retry_same, confidence=0.9,
                         reason=f"存在执行层缺陷（{'；'.join(alpha_like[:2])}）——"
                                "参数没问题，原样重跑并确认收割时做了 alpha 压平。")
    elif weak_motion and current_amplify < AMPLIFY_CEILING:
        nxt = min(round(current_amplify * AMPLIFY_STEP, 2), AMPLIFY_CEILING)
        v = FrameVerdict(verdict=Verdict.retry_amplify, next_amplify=nxt, confidence=0.75,
                         reason=f"帧差 {stats.mean_diff}、轮廓变化 {stats.span_ratio:.3f} 都低于判据，"
                                f"而 alpha 正常 —— 典型的姿势约束被站姿先验吞掉。"
                                f"把放大系数从 {current_amplify} 提到 {nxt}。")
    elif weak_motion:
        if attempt < cfg.max_attempts:
            v = FrameVerdict(verdict=Verdict.retry_new_seed, confidence=0.5,
                             reason=f"放大系数已到上限 {current_amplify} 但动作仍然很弱"
                                    f"（帧差 {stats.mean_diff}）——换种子再试一次，"
                                    "同时应当考虑换一张姿势参考。")
        else:
            v = FrameVerdict(verdict=Verdict.reject, confidence=0.6,
                             reason=f"放大 {current_amplify} + {attempt} 次尝试后帧差仍只有 "
                                    f"{stats.mean_diff}。这一组的问题不在采样，"
                                    "去查姿势参考图本身是不是就没在跨步。")
    elif attempt == 1 and stats.frames <= 2:
        v = FrameVerdict(verdict=Verdict.retry_new_seed, confidence=0.4,
                         reason=f"帧数只有 {stats.frames}，太少无法判定动作——先补帧。")
    else:
        v = FrameVerdict(verdict=Verdict.accept, confidence=0.8,
                         reason=f"帧差 {stats.mean_diff}、轮廓变化 {stats.span_ratio:.3f} "
                                f"均达标，无 alpha 类缺陷。")

    # 打上组名：`status` 要靠它把裁决与质检报告配对（两个列表是平行的，不是同一个对象）。
    v.group = stats.group
    log(f"[规则] 裁决 {stats.group}：{v.verdict.value} — {v.reason}")
    return v


def _clamp(verdict: FrameVerdict, stats: FrameStats, cfg: Config, attempt: int,
           current_amplify: float, log) -> FrameVerdict:
    """把模型给的裁决约束到安全范围内。

    模型偶尔会给出做不到的指令（放大 10 倍、或者明明是 alpha 缺陷却让它 accept）。
    这里做最后一道闸——**模型可以建议，但边界由代码守**。
    """
    alpha_like = [p for p in stats.problems if any(k in p for k in ("半透明", "alpha", "空帧", "不一致"))]

    if alpha_like and verdict.verdict == Verdict.accept:
        log("[llm] 模型的裁决被覆盖：存在执行层缺陷，不能收下")
        return _rule_verdict(stats, cfg, attempt, current_amplify, log)

    if verdict.verdict == Verdict.retry_amplify:
        target = verdict.next_amplify or round(current_amplify * AMPLIFY_STEP, 2)
        capped = min(max(target, current_amplify + 0.1), AMPLIFY_CEILING)
        if abs(capped - target) > 1e-6:
            log(f"[llm] 放大系数被钳制：{target} → {capped}（上限 {AMPLIFY_CEILING}，"
                "超过会出畸形姿势）")
        verdict.next_amplify = round(capped, 2)

    if verdict.verdict == Verdict.retry_amplify and current_amplify >= AMPLIFY_CEILING:
        log(f"[llm] 放大系数已到上限 {AMPLIFY_CEILING}，改判为换种子")
        verdict.verdict = Verdict.retry_new_seed
        verdict.next_amplify = None

    return verdict
