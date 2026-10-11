# 四周路线图

每一周都有**可演示的产出**。没有产出的周就是失控的周。

---

## W1 · 管道跑通（先跑通水管，再接真实模型）

**目标**：一句描述 → 单张图 → 抠图 → 归一化 → 透明 PNG。

| 任务 | 状态 |
|---|---|
| 建仓、装依赖、CI 跑绿 | 骨架已就绪 |
| `mock` 后端跑通全链路（`python -m server.cli smoke`） | 骨架已可跑 |
| 确认 `out/<job_id>/` 七类产物齐全 | 待你验证 |
| 起服务端、确认 `/docs` 与 WebSocket 可用 | 待你验证 |

**验收**：`charforge smoke` 输出 `成功`，且 `sheet.png` 能打开看到人形。

**本周不要做的事**：不要装 torch、不要下模型、不要碰 LoRA。**先把水管跑通。**

---

## W2 · 画风锁定 + 四向 + 打包

**目标**：3-5 张参考图 → 四向 sprite sheet + 切片元数据。

| 任务 | 说明 |
|---|---|
| 实现 `style_lock.describe_references` | 建议先用 CLIP 零样本匹配风格词表，成本最低 |
| 实现 `style_lock.extract_palette` | k-means 取主色 |
| 实现 `generate._generate_local` | 接 ComfyUI API（推荐）或 diffusers |
| 准备四向 ControlNet 模板 | 见 `configs/pose_templates/README.md` |
| 验证四向一致性 | 把四向 idle 拼成一张对比图，用眼睛看 |

**验收**：`out/<job_id>/sheet.png` 里四向角色**比例一致、脚底对齐**。

**关键**：master reference 由**你人工确认**。这一步不要自动化。

---

## W3 · Unity 装配（本项目最核心的一周）

**目标**：Editor 窗口 → prefab，拖进场景能走。

| 任务 | 说明 |
|---|---|
| 联调 `PrefabAssembler.Assemble` | 骨架已写好，需要真实数据打磨 |
| 联调 `SpriteImportSetup.Apply` | 重点验证切片位置与锚点 |
| 验证 Animator 状态机 | 4 个参数（facing/isMoving/moveX/moveY） |
| 验证 SimplePatrol2D 巡逻 | 用 Gizmos 看巡逻路径 |
| 修「脚底对齐」问题 | 十有八九会在这里卡一次，属正常 |

**验收**：把 prefab 拖进一个空场景，加个地板，它能在巡逻点之间来回走，且**不悬空、不抖动**。

---

## W4 · 对话 + 示例 + 文档

**目标**：可发布的 v0.1.0。

| 任务 | 说明 |
|---|---|
| 接对话后端（LLMUnity 或你自己的 HTTP 服务） | `NpcCharacter.GetSystemPrompt()` 已提供人设 |
| 跑两个示例（小镇店主 / 博物馆导览员） | 见 `examples/` |
| 实现就绪度评分 | 见 `docs/unity_readiness.md` |
| 录 GIF（3 个：生成过程 / Editor 装配 / 场景中走动+对话） | README 的第一印象全靠它 |
| 写 README（含「标准→实现」映射表） | 让人 30 秒看懂你在做什么 |
| 打 tag v0.1.0 | |

**验收**：别人 clone 下来，**按 README 能在 30 分钟内跑出第一个 prefab**。

---

## v0.2+ 待办（按优先级）

1. 接 LLM 版 `forge_with_llm`（角色卡质量直接影响一切）
2. 就绪度评分落地
3. 画风参考图自动反推（BLIP / Qwen-VL）
4. LoRA 训练脚本（Kohya）+ 自动挂载
5. 文博场景的藏品档案 RAG 接真实数据源
6. Unity Package Manager 发布 / OpenUPM 上架
7. 行为扩展（接 Unity Behavior 包，把「文本 → 行为树」做出来——这是另一个空白点）

---

## 一个提醒

**别在 W1 就想着接入真实模型。** 这条链路上有 7 个环节，每个环节都有自己的坑。
一次性全接上，出问题时你无法定位是哪个环节坏了——这是这类项目最典型的死法。

一周一个环节，出问题立刻能定位。慢就是快。
