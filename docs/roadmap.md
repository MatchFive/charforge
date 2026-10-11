# 路线图（v0.2 · Unity 6 插件形态）

每一周都有**可演示的产出**。没有产出的周就是失控的周。

> **本路线图已按 v0.2「宿主倒置」重写**：W1 的目标从"跑通 Python 水管"改成
> **"在编辑器内跑通完整链路，零外部依赖"**。理由是——只有先让"点一次按钮就得到 prefab"
> 成立，后面接什么模型才有意义；顺序反了会卡在环境配置上。

---

## W1 · 编辑器内跑通全链路（零外部依赖）

**目标**：**不装 Python、不下模型、不联网**，在 Unity 里点一次按钮得到 prefab。

| 任务 | 状态 |
|---|---|
| UPM 包结构 + 三个程序集 + CI 包校验 | ✅ 骨架已就绪 |
| UI Toolkit 窗口（4 个分区 + 进度 + 日志） | ✅ 已就绪 |
| `MockBackend` 程序化出图（方向/动作/帧序可视化编码） | ✅ 已就绪 |
| C# 侧归一化 `FrameNormalizer`（统一缩放 / 脚底锚点 / 像素网格） | ✅ 已就绪 |
| C# 侧打包 `SheetPacker`（sheet + 切片清单 + 角色包围盒实测） | ✅ 已就绪 |
| `PrefabAssembler` + `SpriteImportSetup` + `AnimatorWriter` | ✅ 已就绪 |
| **在 Unity 里实跑一次，修编译与联调问题** | ⬜ **本周唯一的验收动作** |

**验收**：窗口里选「占位后端」→ 点「生成并装配」→ `Assets/CharForge/<job_id>/` 出现 prefab，
拖进空场景能看到一个站立的小人，Animator 参数 `facing` / `isMoving` 能切换四向 idle/walk。

**本周不要做的事**：不要装 ComfyUI、不要下 SDXL、不要碰 LoRA。**先把管子跑通。**

---

## W2 · 接真实生成 + 画风锁定

**目标**：3–5 张参考图 → 四向 sprite，且**看起来是同一个项目的美术**。

| 任务 | 说明 |
|---|---|
| ~~配好 ComfyUI 地址与 workflow.json~~ | **已备好**：`packages/com.charforge/Configs/comfyui/` 下有可直接导入的工作流（含 ControlNet 版）+ 同名 `bindings.json`，并按显存给了参数建议。只需改模型文件名 |
| ~~写 `*.bindings.json` 指明正向/负向/种子节点~~ | **已备好**。不写会退化到启发式（取前两个 CLIPTextEncode），只保证"能跑" |
| ~~workflow 里接 ControlNet OpenPose~~ | **代码已接通**：`load_image` 绑定不再被忽略，插件会把 `<Unity 工程>/CharForge/pose_templates/{direction}_{action}_{index:00}.png` 自动上传并注入 LoadImage 节点。**骨架图要你自己准备**，做法见该目录 README |
| 接 IP-Adapter 做画风锁定 | **先别训 LoRA**：参考图 + IP-Adapter 起步，通了再考虑训练。这需要装 `ComfyUI_IPAdapter_plus` 自定义节点 |
| 准备四向姿势骨架图 | 一致性在这一步决定，不在提示词。可用现成素材，或用 Mixamo 自制（最可控） |
| 验证四向一致性 | 把四向 idle 拼成一张对比图，用眼睛看 |

**v0.3.0 补掉的三处「不报错但结果不对」**（这些是接入真实生成后必然踩到的）：

- **每帧一个随机种子** → 等于每帧生成一个不同的人。现在改为同一任务共用一个种子
  （由 jobId 派生，可复现），各帧差异只应来自姿势约束。
- **`load_image` 绑定解析了却没人用** → ControlNet 永远拿不到骨架图，
  症状表现为「四向姿势没锁住」，实为根本没接上。
- **SD 出的是全不透明图** → 下游靠 alpha 裁切，拿到不透明图时包围盒等于整幅画布，
  碰撞体会盖住整个屏幕且不报任何错。现在加了本地色键抠图兜底（凑近背景色的像素还会被
  四边连通域保护，避免把青灰长衫击穿成空洞），质量仍不如 rembg，已在日志里说明。

**验收**：`sheet.png` 里四向角色**比例一致、脚底对齐、风格看起来来自同一个项目**。

**关键**：master reference 由**你人工确认**。这一步不要自动化。

---

## W3 · Unity 原生资产与进程体验打磨

**目标**：让"插件"这件事的用户体验真的成立。

| 任务 | 说明 |
|---|---|
| 装 `com.unity.2d.animation`，验证 `SpriteLibraryWriter` 自动生效 | 四个分类（动作）× 四个标签（朝向），运行期可 `SetCategoryAndLabel` 换套 |
| 打磨 `BackendProcessManager` 生命周期 | 重点：编辑器退出后**不留孤儿进程**（这是同类工具最常见的投诉） |
| 把进度从轮询升级为 WebSocket 流式 | `server/` 已有 `WS /ws/jobs/{id}`；C# 侧目前走轮询 |
| 联调 `PrefabAssembler` 的碰撞体贴合 | 用 Gizmos 看胶囊是否包住角色：**不穿模、不悬空** |
| 修「脚底对齐」问题 | 十有八九会在这里卡一次，属正常 |
| 加就绪度体检面板 | 按 `docs/unity_readiness.md` 的规则给生成结果打分 |

**验收**：把 prefab 拖进空场景 → 加个地板 → 它能在巡逻点之间来回走，**不悬空、不抖动、不卡门**；
换装时不需要重新导入贴图。

---

## W4 · 分层资产 + 文博端到端 + 发布

**目标**：可发布的 v0.3.0（当前包版本已到 0.3.0，本阶段是把它推到"可发布"状态）。

| 任务 | 说明 |
|---|---|
| 分层 `.psb` 输出 → PSD Importer 自动绑骨（实验性） | 按「底身 / 发型 / 外袍 / 面部」分层输出，Unity 侧开 Mosaic + Character Rig |
| 跑通文博示例 | `examples/museum_guide`：馆藏形制约束 → 人设知识边界 → 导览员 prefab |
| 录 GIF（3 个） | 生成过程 / Editor 装配 / 场景中走动+对话——README 的第一印象全靠它 |
| 写清「标准→实现」映射表 | 尤其 AIGC 标识那一段（GB 45438-2025） |
| 打 tag v0.3.0 | |
| 发 UPM / OpenUPM | 让别人一条 URL 就能装 |

**验收**：别人 clone 下来，**按 README 在 10 分钟内跑出第一个 prefab**（因为不需要任何环境）。

---

## v0.3+ 待办（按优先级）

1. **接 LLM 版角色卡**（`IInferenceBackend.DescribeAsync` 已在接口里）——角色卡质量直接影响一切
2. **`SentisBackend` 的扩散流程实现**（`GenerateFrameAsync` 内的 TODO）——把"零环境出图"从占位图升级为真图
3. 就绪度评分落地为独立 CLI（脱离 Unity 也能体检资产）
4. 画风参考图自动反推（BLIP / Qwen-VL），替代纯 CLIP 零样本
5. LoRA 训练脚本（Kohya）+ 自动挂载
6. 文博场景的藏品档案 RAG 接真实数据源
7. **行为扩展**：接 Unity Behavior 包，把「文本 → 行为树」做出来（另一个空白点）

---

## 一个提醒

**别在 W1 就想着接入真实模型。** 这条链路上有七个环节，每个环节都有自己的坑。
一次性全接上，出问题时你无法定位是哪个环节坏了——这是这类项目最典型的死法。

一周一个环节，出问题立刻能定位。慢就是快。
