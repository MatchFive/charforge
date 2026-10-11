"""生成后端适配器协议。

`pipeline/generate.py` 目前把 mock / local / cloud 三种分支内联在里面——骨架阶段这样最快。
但当你要接入第四种后端（ComfyUI、SD-WebUI、某家云 API、自建推理服务）时，
内联分支就会变成一坨 if-else。

扩展方式（推荐）：
1. 新建 `server/adapters/<你的后端>.py`，实现下面的 ImageBackend 协议；
2. 在 registry 里注册；
3. 把 generate.py 的分支换成 registry.get(settings.charforge_backend)。
"""

from __future__ import annotations

from pathlib import Path
from typing import Protocol, runtime_checkable


@runtime_checkable
class ImageBackend(Protocol):
    """图像生成后端。实现这一个方法即可。"""

    name: str

    def generate(
        self,
        positive: str,
        negative: str,
        out_path: Path,
        *,
        seed: int,
        size: tuple[int, int] = (512, 512),
        control_image: str | None = None,
        reference_images: list[str] | None = None,
        lora: str | None = None,
        lora_scale: float = 0.8,
    ) -> Path:
        """生成一张图并落盘，返回路径。

        参数里每一项都对应一个具体的约束能力，实现时**尽量都支持**：
        - seed            帧间一致性的基础
        - control_image   姿势锁定（四向一致性靠它）
        - reference_images 画风锁定（IP-Adapter）
        - lora / lora_scale 画风锁定（V1 之后）
        """
        ...


@runtime_checkable
class SimilarityScorer(Protocol):
    """画风一致性打分器，用于验收（CLIP 相似度）。"""

    def score(self, image_path: str, master_path: str) -> float: ...
