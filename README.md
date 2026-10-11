# CharForge · Unity 2D 角色资产流水线

> 一句话 + 项目画风参考图 → **可直接拖进场景、会走、会说话的 2D NPC prefab**。

LLM 编排 + 画风锁定 + Unity 装配。面向独立游戏团队与文博数字化场景（数字展陈角色、导览员立绘、展项插图、社教动画帧）。

---

## 这个项目解决什么

现有工具分成两半，中间是空的：

| 已有工具 | 做到哪一步 | 缺什么 |
|---|---|---|
| SpriteForge | master ref 锁风格 + 抠图 + 归一化 + 元数据清洗 | 只出**静态 sprite**；无动画；无 Unity 装配；依赖云 API |
| GenForgeSprite | REST API + 六类动画帧 + 引擎元数据 | 画风靠**预设**不是自动锁定；依赖云 API；无 prefab 装配 |
| Animyth / abutbul-imgen | 脚本级 demo | 无产品化 |
| Ludo AI / Unity AI Generators | 商业 / 官方云端 | 只出资产，**不装配**；云端，素材出本机 |

**CharForge 补的是「装配层」**：把 sprite 集变成带 Animator、2D 碰撞、寻路、人设与对话组件的 prefab。同时提供**项目画风自动锁定**与**Unity 导入设置自动生成**（切片 / 锚点 / PPU / 像素网格对齐）——这两项在开源侧目前无等价实现。

三条设计原则：

1. **本地优先**：全链路可自托管，美术资产与馆藏素材**不出本机**。
2. **人类在环**：master reference 与关键帧需人工确认，不假装全自动——这也符合企业真实做法。
3. **不重造模型**：生成、抠图、ControlNet 全部复用成熟开源件，本项目只做编排与装配。

---

## 架构

```
                    ┌─────────────────────── Unity Editor ───────────────────────┐
                    │  CharForgeWindow  →  描述 / 参考图 / 动作勾选 / 人审确认       │
                    │  PrefabAssembler  →  生成 prefab（Animator / Collider / 寻路）│
                    │  SpriteImportSetup→  自动写 .meta（切片 / 锚点 / PPU / Point）│
                    └───────────────┬───────────────────────────────────────────┘
                                    │  WebSocket（流式进度）/ HTTP
                    ┌───────────────▼───────────── Python 服务端 ────────────────┐
                    │                                                            │
  角色描述 ──LLM──►  │ [1] Prompt Forge  →  结构化角色卡 → 分方向 / 分动作提示词组   │
  参考图(3-5张) ───► │ [2] Style Lock    →  master reference + LoRA / IP-Adapter   │
                    │ [3] Generate      →  SDXL / SD1.5 + ControlNet（四向 + 动作） │
                    │ [4] Matting       →  rembg → 透明 PNG                        │
                    │ [5] Normalize     →  裁切 / 统一画布 / 锚点 / 像素网格对齐     │
                    │ [6] Pack          →  sprite sheet + 切片元数据                │
                    │ [7] Assemble      →  装配指令 JSON（下发给 Unity）            │
                    └────────────────────────────────────────────────────────────┘
```

---

## 快速开始（W1：先跑通最细的管子，约 30 分钟）

目标：**一句描述 → 一张图 → 抠图 → 归一化 → 透明 PNG**。先不管风格与四向。

```bash
# 1. 环境
python -m venv .venv
.venv\Scripts\activate          # Windows
pip install -e ".[dev,local]"

# 2. 无 GPU 也能验证全链路（用占位图跑通水管）
python -m server.cli smoke --prompt "博物馆导览员，青绿山水色调" --out ./out

# 3. 起服务
uvicorn server.main:app --reload --port 8000
# 打开 http://localhost:8000/docs 看 API
```

产出在 `./out/<job_id>/`：`raw.png` → `cutout.png` → `normalized/` → `sheet.png` + `manifest.json`。

> **重要**：先跑通水管，再接真实模型。任何一环没有单独验证过，就不要往上叠第二环。

---

## 流水线七段（契约见 `docs/pipeline.md`）

| # | 阶段 | 模块 | 状态 |
|---|------|------|------|
| 1 | Prompt Forge（角色卡 → 提示词组） | `server/pipeline/prompt_forge.py` | ✅ 模板版可用 / ⬜ LLM 版待接 |
| 2 | Style Lock（画风锁定） | `server/pipeline/style_lock.py` | ⬜ 待实现（接口已定） |
| 3 | Generate（生成） | `server/pipeline/generate.py` | ⬜ 待实现（占位图可跑通） |
| 4 | Matting（抠图） | `server/pipeline/matting.py` | ✅ rembg 版可用 / ⬜ 降级版待补 |
| 5 | Normalize（归一化） | `server/pipeline/normalize.py` | ✅ 可用（裁切/画布/锚点/网格） |
| 6 | Pack（打包） | `server/pipeline/pack.py` | ✅ 可用（sheet + manifest） |
| 7 | Assemble（装配） | `server/pipeline/assemble.py` + `Editor/PrefabAssembler.cs` | ⬜ 待实现（指令格式已定） |

---

## 仓库结构

```
unity2d-charforge/
├── packages/com.charforge/        # Unity UPM 包（C#）
│   ├── Editor/                    #   窗口 / 装配 / 导入设置
│   └── Runtime/                   #   运行期组件（NPC 组件 / 巡逻行为）
├── server/                        # Python 服务端（FastAPI + WebSocket）
│   ├── pipeline/                  #   七段流水线
│   ├── adapters/                  #   生成后端适配器（本地 / 云端可切换）
│   └── models/                    #   pydantic 数据模型
├── configs/
│   ├── style_presets/             #   风格预设（像素 / 手绘 / 文博…）
│   └── pose_templates/            #   四向 + 动作 ControlNet 模板
├── schemas/                       # JSON Schema（角色卡 / 切片清单 / 人设）
├── examples/
│   ├── town_shopkeeper/           #   游戏向示例
│   └── museum_guide/              #   文博向示例（含形制约束）
├── docs/                          # 流水线契约 / 就绪度规则 / 模板清单 / 路线图
└── tests/
```

---

## 四周路线图

| 周 | 目标 | 验收 |
|---|---|---|
| **W1** | 管道跑通 | 描述 → 单图 → 抠图 → 归一化 → 透明 PNG |
| **W2** | 风格锁定 + 四向 + 打包 | 3-5 张参考图 → 四向 sprite sheet + 切片元数据 |
| **W3** | Unity 装配 ★ | Editor 窗口 → prefab，拖入场景能走 |
| **W4** | 对话 + 示例 + 文档 | 接 LLMUnity 通话；两个示例；README + GIF；v0.1.0 |

详见 `docs/roadmap.md`。

---

## 验收指标

| 指标 | 目标 |
|---|---|
| 单角色（四向 idle + walk，8-12 帧）总耗时 | ≤ 8 分钟（本地 12GB 显存） |
| 画风一致性（与 master ref 的 CLIP 相似度） | ≥ 0.85 |
| 帧间一致性（相邻帧姿势关键点偏差） | ≤ 5 px |
| Unity 就绪度（无手工修正即可入场景） | 100% |
| 生成成本 | 本地全托管 → 单角色 API 成本 ≈ 0 |

---

## TODO（你自己实现的核心）

按优先级：

1. `pipeline/generate.py` —— 接 diffusers（SDXL / SD1.5）+ ControlNet，输出四向与动作帧
2. `pipeline/style_lock.py` —— CLIP 反推风格标签 + IP-Adapter / LoRA 挂载
3. `Editor/PrefabAssembler.cs` —— 读装配指令生成 prefab（本项目最有价值的 30 行）
4. `Editor/SpriteImportSetup.cs` —— 自动写 `.meta` 切片设置
5. `adapters/local_sd.py` —— ComfyUI API 或 diffusers 直调

---

## 许可

MIT（待定）。生成内容的使用须遵守素材来源的授权与所在地法规；面向国内发布时请遵守 AIGC 内容标识相关要求（见 `docs/` 后续补充）。
