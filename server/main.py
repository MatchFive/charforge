"""FastAPI 服务端：HTTP 建任务 + WebSocket 流式进度。

Unity 侧的用法（三行）：
    1. POST /api/jobs        -> 拿到 job_id
    2. 连 WS /ws/jobs/{id}   -> 收进度，画进度条
    3. 收到 stage=done 后，GET /api/jobs/{id} 拿 assembly_spec.json 与 persona.json

注意：任务状态存在进程内存里（骨架阶段够用）。
要长期运行或多人共用，把 _QUEUES / _RESULTS 换成 Redis 或 数据库。
"""

from __future__ import annotations

import asyncio
import json

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from server import __version__
from server.config import settings
from server.models.schemas import JobProgress, JobRequest, JobResult, JobStage
from server.pipeline.orchestrator import list_jobs, new_job_id, run_job

DESCRIPTION = """
CharForge —— Unity 2D 角色资产流水线。

一句话 + 项目画风参考图 → 可直接拖进场景的 2D NPC prefab。
七段流水线：提示词编排 / 画风锁定 / 生成 / 抠图 / 归一化 / 打包 / 装配。
"""

app = FastAPI(title="CharForge", version=__version__, description=DESCRIPTION)

app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origin_list,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# 内存态：job_id -> 进度队列（None 为结束哨兵）
_QUEUES: dict[str, asyncio.Queue[JobProgress | None]] = {}
_RESULTS: dict[str, JobResult] = {}


def _make_emitter(job_id: str):
    async def emit(progress: JobProgress) -> None:
        queue = _QUEUES.get(job_id)
        if queue is not None:
            await queue.put(progress)

    return emit


async def _run_and_close(req: JobRequest, job_id: str) -> None:
    try:
        result = await run_job(req, job_id, _make_emitter(job_id))
    except Exception as exc:  # noqa: BLE001
        result = JobResult(job_id=job_id, ok=False, error=f"{type(exc).__name__}: {exc}")
    finally:
        _RESULTS[job_id] = result
        queue = _QUEUES.get(job_id)
        if queue is not None:
            await queue.put(None)  # 哨兵：告诉 WS 可以关闭了


# ─────────────────────────────────────────────────────────────
# HTTP
# ─────────────────────────────────────────────────────────────


@app.get("/api/health", tags=["系统"])
async def health() -> dict:
    return {
        "ok": True,
        "version": __version__,
        "backend": settings.charforge_backend,
        "output_dir": str(settings.output_path),
        "jobs": len(list_jobs()),
    }


@app.post("/api/jobs", tags=["任务"], summary="创建任务（异步）")
async def create_job(req: JobRequest) -> dict:
    job_id = new_job_id()
    _QUEUES[job_id] = asyncio.Queue()
    asyncio.create_task(_run_and_close(req, job_id))
    return {"job_id": job_id, "ws": f"/ws/jobs/{job_id}"}


@app.post("/api/jobs/sync", response_model=JobResult, tags=["任务"], summary="同步跑完（调试/测试）")
async def run_sync(req: JobRequest) -> JobResult:
    job_id = new_job_id()
    return await run_job(req, job_id, emit=None)


@app.get("/api/jobs", tags=["任务"])
async def get_jobs() -> dict:
    return {"jobs": list_jobs()}


@app.get("/api/jobs/{job_id}", response_model=JobResult, tags=["任务"])
async def get_job(job_id: str) -> JobResult:
    if job_id in _RESULTS:
        return _RESULTS[job_id]
    if job_id in _QUEUES:
        raise HTTPException(status_code=202, detail="任务仍在进行中")
    raise HTTPException(status_code=404, detail=f"未找到任务 {job_id}")


# ─────────────────────────────────────────────────────────────
# WebSocket
# ─────────────────────────────────────────────────────────────


@app.websocket("/ws/jobs/{job_id}")
async def ws_job(ws: WebSocket, job_id: str) -> None:
    await ws.accept()

    queue = _QUEUES.get(job_id)
    if queue is None:
        result = _RESULTS.get(job_id)
        stage = "done" if (result and result.ok) else "failed"
        await ws.send_json({"stage": stage, "percent": 100, "message": "任务已结束"})
        await ws.close()
        return

    try:
        while True:
            item = await queue.get()
            if item is None:
                break
            await ws.send_json(json.loads(item.model_dump_json()))
    except WebSocketDisconnect:
        pass
    finally:
        try:
            result = _RESULTS.get(job_id)
            await ws.send_json(
                {
                    "stage": JobStage.DONE.value if (result and result.ok) else JobStage.FAILED.value,
                    "percent": 100,
                    "message": "流已结束",
                }
            )
        except Exception:  # noqa: BLE001 —— 客户端可能已断开
            pass
        try:
            await ws.close()
        except Exception:  # noqa: BLE001
            pass


# 产物直接可访问：/files/<job_id>/sheet.png
app.mount("/files", StaticFiles(directory=str(settings.output_path)), name="files")


def main() -> None:
    import uvicorn

    uvicorn.run(
        "server.main:app",
        host=settings.host,
        port=settings.port,
        reload=False,
    )


if __name__ == "__main__":
    main()
