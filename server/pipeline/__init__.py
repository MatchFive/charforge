"""七段流水线。

每段只依赖上游的结构化产物（见 server/models/schemas.py），不读自由文本。
这样任何一段都可以单独替换、单独测试。

    [1] prompt_forge  -> CharacterCard
    [2] style_lock    -> StyleProfile
    [3] generate      -> list[FrameAsset]
    [4] matting       -> 透明 PNG
    [5] normalize     -> 统一画布 PNG
    [6] pack          -> SpriteSheetInfo + list[SpriteRect]
    [7] assemble      -> AssemblySpec（下发 Unity）
"""

from .orchestrator import run_job

__all__ = ["run_job"]
