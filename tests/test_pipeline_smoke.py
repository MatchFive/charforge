"""全链路冒烟测试。

用 mock 后端跑完整条流水线——不需要 GPU、不需要 API key、几秒内跑完。
这是 CI 的主力测试：任何一段改坏了，这里立刻红。
"""

from __future__ import annotations

import json

import pytest

from server.config import settings
from server.models.schemas import ActionName, Direction, JobRequest, MovementMode, MovementSpec
from server.pipeline.orchestrator import new_job_id, run_job


@pytest.fixture(autouse=True)
def _mock_env(tmp_path, monkeypatch):
    """把输出目录指到临时目录，并强制 mock 后端。"""
    monkeypatch.setattr(settings, "output_dir", str(tmp_path))
    monkeypatch.setattr(settings, "charforge_backend", "mock")
    yield


def _make_request(**overrides) -> JobRequest:
    base = dict(
        prompt="博物馆导览员，身着青灰色长衫，手持讲解册",
        directions=[Direction.SOUTH, Direction.NORTH],
        actions=[ActionName.IDLE, ActionName.WALK],
        movement=MovementSpec(mode=MovementMode.ASTAR, speed=1.5),
        dialogue_enabled=True,
    )
    base.update(overrides)
    return JobRequest(**base)


async def test_full_pipeline_produces_all_artifacts(tmp_path):
    job_id = new_job_id()
    result = await run_job(_make_request(), job_id)

    assert result.ok, f"流水线失败：{result.error}"
    out = tmp_path / job_id

    # 七类产物
    for name in (
        "character_card.json",
        "style_profile.json",
        "sheet.png",
        "manifest.json",
        "assembly_spec.json",
        "persona.json",
    ):
        assert (out / name).exists(), f"缺少产物 {name}"

    # 帧目录非空
    frames = list((out / "frames").rglob("*.png"))
    assert len(frames) > 0


async def test_manifest_sprite_count_matches_grid(tmp_path):
    job_id = new_job_id()
    result = await run_job(_make_request(), job_id)
    assert result.ok

    manifest = result.manifest
    assert manifest is not None

    # 2 方向 × 2 动作 × 4 帧 = 16
    assert len(manifest.sprites) == 16
    assert manifest.sheet.columns == 4
    assert manifest.sheet.rows == 4


async def test_sprite_names_follow_convention(tmp_path):
    """切片名必须是 {direction}_{action}_{NN}——装配时靠它排序。"""
    job_id = new_job_id()
    result = await run_job(_make_request(), job_id)
    assert result.ok and result.manifest

    import re

    pattern = re.compile(r"^(south|north|east|west)_(idle|walk)_\d{2}$")
    for rect in result.manifest.sprites:
        assert pattern.match(rect.name), f"切片名不合规：{rect.name}"


async def test_animations_built_from_manifest(tmp_path):
    job_id = new_job_id()
    result = await run_job(_make_request(), job_id)
    assert result.ok and result.spec

    names = {a.name for a in result.spec.animations}
    assert names == {"south_idle", "south_walk", "north_idle", "north_walk"}

    for clip in result.spec.animations:
        assert len(clip.sprites) == 4
        # 帧顺序必须是 00 < 01 < 02 < 03
        assert clip.sprites == sorted(clip.sprites)


async def test_persona_contains_knowledge_boundary(tmp_path):
    """文博场景的关键：有 reference_notes 时，人设必须带知识边界约束。"""
    job_id = new_job_id()
    req = _make_request(era_or_period="南宋")
    result = await run_job(req, job_id)
    assert result.ok

    persona_path = tmp_path / job_id / "persona.json"
    persona = json.loads(persona_path.read_text(encoding="utf-8"))

    assert persona["role"]
    assert "南宋" in persona["persona_prompt"] or "南宋" in str(persona)


async def test_missing_reference_images_produces_warning(tmp_path):
    """给了参考图但反推未实现，必须显式告警——不能静默假装画风已锁定。"""
    job_id = new_job_id()
    req = _make_request(reference_images=["/tmp/fake_ref_1.png"])
    result = await run_job(req, job_id)

    assert result.ok
    assert any("画风" in w for w in result.warnings)


async def test_frame_quota_guard(tmp_path, monkeypatch):
    """超配额必须在生成前拦住，不能跑一半才失败。"""
    monkeypatch.setattr(settings, "max_frames_per_job", 4)
    job_id = new_job_id()
    result = await run_job(_make_request(), job_id)

    assert not result.ok
    assert "上限" in (result.error or "")


def test_suggest_pixel_grid():
    from server.pipeline.normalize import suggest_pixel_grid

    assert suggest_pixel_grid((512, 512), 64) == 8
    assert suggest_pixel_grid((256, 256), 64) == 4
    assert suggest_pixel_grid((512, 512), 512) == 1


def test_load_preset_tags():
    from server.pipeline.prompt_forge import load_preset_tags

    positive, negative = load_preset_tags("pixel_art")
    assert any("pixel" in p for p in positive)
    assert any("anti-aliasing" in n for n in negative)

    # 不存在的预设不应抛异常
    assert load_preset_tags("does_not_exist") == ([], [])
