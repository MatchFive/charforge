"""服务端配置。所有可选值都能通过 .env 覆盖（见 .env.example）。"""

from __future__ import annotations

from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

ROOT = Path(__file__).resolve().parent.parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=str(ROOT / ".env"),
        env_file_encoding="utf-8",
        extra="ignore",
    )

    # ── 生成后端 ──────────────────────────────────────────
    charforge_backend: str = Field(default="mock", description="mock | local | cloud")

    sd_model_id: str = "stabilityai/stable-diffusion-xl-base-1.0"
    sd_controlnet_openpose: str = "lllyasviel/control_v11p_sd15_openpose"
    sd_device: str = "cuda"
    sd_dtype: str = "float16"
    comfyui_url: str = ""

    openai_api_key: str = ""
    openai_image_model: str = "gpt-image-1"

    # ── 风格锁定 ──────────────────────────────────────────
    style_master_dir: str = "./configs/style_masters"
    clip_model_id: str = "openai/clip-vit-large-patch14"
    style_similarity_threshold: float = 0.85

    # ── 输出与配额 ────────────────────────────────────────
    output_dir: str = "./out"
    max_frames_per_job: int = 32
    max_jobs_per_hour: int = 20

    # ── 服务端 ────────────────────────────────────────────
    host: str = "127.0.0.1"
    port: int = 8000
    cors_origins: str = "http://localhost:3000,http://localhost:5173"

    @property
    def output_path(self) -> Path:
        p = Path(self.output_dir)
        if not p.is_absolute():
            p = ROOT / p
        p.mkdir(parents=True, exist_ok=True)
        return p

    @property
    def cors_origin_list(self) -> list[str]:
        return [o.strip() for o in self.cors_origins.split(",") if o.strip()]

    @property
    def presets_path(self) -> Path:
        return ROOT / "configs" / "style_presets"

    @property
    def pose_templates_path(self) -> Path:
        return ROOT / "configs" / "pose_templates"


settings = Settings()
