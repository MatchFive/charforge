# server/ —— 可选的推理后端（v0.2 起不再是宿主）

> **v0.1 → v0.2 的定位变化**：这部分代码原本是整套工具的**宿主**（用户必须先跑它）。
> v0.2 之后 Unity 成为唯一控制面，它降级为 `IInferenceBackend` 的一个实现
> （`SidecarBackend`）——**「整单委托」型后端**。
>
> 代码没有浪费：它的提示词编排 / 画风锁定 / 归一化逻辑仍然可用，
> 只是不再决定用户的使用形态。

## 它在架构里的位置

```
Unity（宿主 · 控制面）
  └─ SidecarBackend  ──HTTP──►  server/（数据面 · 七段流水线）
```

**什么时候用它**：你已经有一套调好的 diffusers / ComfyUI 流程，想直接复用；
或者想拿它做流水线的独立调试（不必开 Unity）。

**什么时候不用它**：想完全零外部依赖时，用 `MockBackend`（占位）或
`SentisBackend`（引擎内推理）——Unity 侧自己就能跑完第 1–6 段，装配也全在 C# 里。

## 接口（C# 侧 `SidecarBackend` 对齐这些端点）

| 方法 | 端点 | 说明 |
|---|---|---|
| 探测 | `GET /api/health` | 返回 ok / version / backend / output_dir |
| 建单 | `POST /api/jobs` | 异步，返回 `{job_id, ws}` |
| 查询 | `GET /api/jobs/{job_id}` | `202` 进行中，`200` 返回 `JobResult` |
| 同步 | `POST /api/jobs/sync` | 调试/测试用，一次跑完 |
| 进度 | `WS /ws/jobs/{job_id}` | 流式进度（当前 C# 侧走轮询，WS 为后续增量） |

产物目录必须包含：`sheet.png`、`manifest.json`、`assembly_spec.json`、`persona.json`。
**这是「整单委托」型后端的契约**——Unity 侧的 `PrefabAssembler` 只认这四份文件。

## 跑起来

```bash
pip install -e ".[dev,local]"

# 只验证水管（不需要 GPU 与模型）
python -m server.cli smoke --prompt "博物馆导览员，青灰色长衫" --name 云叔 --dialogue

# 起服务（也可以让 Unity 侧「启动后端」按钮代管）
python -m server.cli serve
```

## 目录

| 路径 | 内容 |
|---|---|
| `pipeline/` | 七段：prompt_forge / style_lock / generate / matting / normalize / pack / assemble |
| `adapters/` | 生成后端适配器（本地 SD / 云端可切换） |
| `models/schemas.py` | pydantic 契约 —— **与 `schemas/*.json` 和 C# `CharForgeContracts.cs` 三方必须一致** |

## 尚未实现（与 C# 侧对等的 TODO）

1. `pipeline/generate.py` 的 `_generate_local` —— 接 diffusers 或 ComfyUI API
2. `pipeline/style_lock.py` 的 `describe_references` —— CLIP 反推风格标签（**先别训 LoRA**，用工参考图 + IP-Adapter 起步）
3. `matting.py` 的色键降级路径
