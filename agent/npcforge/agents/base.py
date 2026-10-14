"""LLM 接线：一个 OpenAI 兼容端点，四个裁决点共用。

三件事在这里定死：

1. **延迟导入**。`pydantic_ai` 只在真的要调模型时才 import。这样质检、装配、
   素材这些纯本地能力在没有模型的情况下照样能用——少一个可选依赖就少一道坎。

2. **没配齐就降级，不报错**。与插件里 `PromptForgeService` 同一套判定：
   BaseURL / APIKey / 模型名三项齐了才走模型，否则退回确定性规则，
   **并在日志里明说走的是哪条路**。降级不可怕，降级了不吭声才可怕。

3. **非 OpenAI 模型要显式声明能力**。`OpenAIProvider` 默认按 OpenAI 的模型画像
   来假设（严格工具定义、结构化输出都能用），而 DeepSeek / Qwen / 本地网关
   这些兼容端点往往不支持 `strict`。不关掉的话，`output_type` 会被静默忽略——
   模型照样返回一段文本，而你拿到的是一个字段全空的 Pydantic 对象。
"""

from __future__ import annotations

import os
from dataclasses import dataclass

from ..config import Config


class LlmUnavailable(RuntimeError):
    """LLM 没配齐。调用方应退回确定性规则，而不是把作业判失败。"""


@dataclass
class LlmHandle:
    model: object
    model_name: str
    base_url: str
    is_openai: bool


_PROFILE_KEYS = (
    # 这些字段名在不同版本里改过，逐个试，命中即用。
    "openai_supports_strict_tool_definition",
    "openai_chat_supports_strict_tool_definition",
    "openai_supports_strict_mode",
)


def build_model(cfg: Config) -> LlmHandle:
    """按配置造一个 pydantic-ai 模型。没配齐就抛 `LlmUnavailable`。"""
    if not cfg.llm_ready:
        raise LlmUnavailable(
            "LLM 没配齐（要 base_url + model 两项，key 视网关而定）。\n"
            "  配法：在 .npcforge/config.toml 的 [llm] 段填 llm_base_url / llm_api_key / llm_model，\n"
            "  或设环境变量 NPCFORGE_LLM_BASE_URL / NPCFORGE_LLM_API_KEY / NPCFORGE_LLM_MODEL。\n"
            "  走 OpenAI 官方端点时 key 从 OPENAI_API_KEY 读。")

    from pydantic_ai.models.openai import OpenAIChatModel
    from pydantic_ai.providers.openai import OpenAIProvider

    provider_kwargs: dict = {"base_url": cfg.llm_base_url}
    if cfg.llm_api_key:
        provider_kwargs["api_key"] = cfg.llm_api_key
    elif os.environ.get("OPENAI_API_KEY"):
        provider_kwargs["api_key"] = os.environ["OPENAI_API_KEY"]

    model = OpenAIChatModel(cfg.llm_model, provider=OpenAIProvider(**provider_kwargs))

    is_openai = "api.openai.com" in cfg.llm_base_url or cfg.llm_model.startswith(("gpt-", "o1", "o3", "o4"))
    if not is_openai:
        _relax_profile(model)

    return LlmHandle(model=model, model_name=cfg.llm_model,
                     base_url=cfg.llm_base_url, is_openai=is_openai)


def _relax_profile(model) -> None:
    """对非 OpenAI 端点关掉 `strict` 假设。

    第三方兼容端点对 OpenAI 的 `strict` 结构化输出支持参差：
    严格的会直接回 400，宽松的会**静默忽略**（更糟——你以为拿到了结构，
    其实字段全是默认值）。这里主动关掉，走"提示模型按 schema 输出 + 本地校验失败重试"的路子。

    **不 import `pydantic_ai.profiles.ModelProfile`**：我们只改已有 profile 对象的字段，
    从不构造它。为"检查能不能 import"而 import 一个用不上的类，只会让这层多一个
    pydantic-ai 的版本耦合点——而这层存在的意义恰恰是"版本之间来回兼容"。
    """
    for attr in _PROFILE_KEYS:
        try:
            profile = getattr(model, "profile", None)
            if profile is None:
                return
            if hasattr(profile, attr):
                setattr(profile, attr, False)
                return
        except Exception:
            continue
    # 找不到可调字段不是错误：说明这个版本的默认行为已经合适，
    # 或者端点本来就支持 strict。真正的判定在下面的"空输出守卫"里兜底。


def guard_structured(output, what: str, log=None) -> None:
    """结构化输出的空值守卫。

    模型/网关不认 schema 时不会报错，只会返回一个"全默认值"的对象。
    这个守卫把它变成一句明确的失败——**不报错的失败优先于报错的失败**。
    """
    if output is None:
        raise RuntimeError(f"{what} 拿不到结构化结果（模型返回了 None）。")
    if hasattr(output, "model_dump"):
        data = output.model_dump()
        meaningful = [k for k, v in data.items() if v not in ("", None, [], {}, 0, False)]
        if not meaningful:
            raise RuntimeError(
                f"{what} 的结构化结果全是空值——多半是端点不支持结构化输出"
                "（`output_type` 被静默忽略了）。换个支持 function calling 的模型，"
                "或改用纯文本输出再自己解析。")
    if log:
        log(f"[llm] {what} 结构化输出校验通过")


def describe(cfg: Config) -> str:
    if cfg.llm_ready:
        return f"{cfg.llm_model} @ {cfg.llm_base_url}"
    return "（未配齐 → 人设与裁决走确定性规则，日志会标 [降级]）"


def brief(exc: Exception) -> str:
    """只取异常信息的**第一行**。

    `LlmUnavailable` 带一段多行配法说明，那是对着人说的、说一次就够。
    四个裁决点各打一遍全文，日志里就会出现四份同样的四行——把真正有用的
    那一行（"谁在降级"）淹掉。想细看配法的跑 `npcforge doctor`。
    """
    text = str(exc).strip()
    if not text:
        return type(exc).__name__
    head = text.splitlines()[0].strip()
    return head + ("（完整配法见 npcforge doctor）" if len(text.splitlines()) > 1 else "")
