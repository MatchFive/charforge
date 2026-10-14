# 流水线契约

> ⚠️ **历史存档：本文件描述的七段流水线，连同它两侧的实现，都已不在仓库里。**
> C# 侧 `CharacterForgePipeline` 于 v0.4.0 下线，Python 侧 `server/` 于 **v0.4.5 整体删除**——
> 下文出现的 `server/...` 路径、`python -m server.cli` 命令与 `SidecarBackend` 整单委托，
> 都只是**设计记录**，照着敲会找不到文件。
> 现在实际走的是**三步直线**：角色立绘 → 帧动画 → 组装 prefab，
> 三步之间**没有 JSON 契约**，只有磁盘上的产物 —— 见
> [`README.md`](../README.md) 的「当前路线的数据流」与 [`docs/unity_quickstart.md`](unity_quickstart.md)。
> 保留本文件的原因是「装配指令可重放」这条设计思路本身仍有价值，**不代表当前流程依赖它**。

七段，每段只依赖上游的**结构化产物**。任何一段都可以单独替换、单独测试。

改契约之前先想清楚：**Python 与 C# 两侧都要跟着改**（`server/models/schemas.py` ↔
`packages/com.charforge/Editor/Data/CharForgeContracts.cs`，外加 `schemas/*.schema.json`）。

> **v0.2 起每一段有两个实现，任选其一：**
> - **C# 侧（Unity 引擎内）**：`Editor/Services/CharacterForgePipeline.cs` 编排，
>   第 5 段 `FrameNormalizer`、第 6 段 `SheetPacker` 都在引擎内完成 —— **不需要 Python**。
> - **Python 侧**：`server/pipeline/`，通过 `SidecarBackend` 以「整单委托」方式调用。
>
> 两条路径产出**同样的产物目录**（sheet.png + manifest.json + assembly_spec.json + persona.json），
> 因此第 7 段装配代码只有一份。

---

## [1] Prompt Forge

| 项 | 内容 |
|---|---|
| 输入 | `JobRequest`（自由描述 + 约束） |
| 输出 | `CharacterCard` |
| 实现 | `server/pipeline/prompt_forge.py` |
| 状态 | 规则版可用；LLM 版待实现 |

**为什么需要**：直接拿一句话去生成，每次得到的都不是同一个角色。这一段把描述固化成结构化字段，后续每一帧从同一张卡派生。

**要做的事**：接 LLM，要求只输出 JSON，用 pydantic 校验，失败降级到规则版。

**文博场景的增强点**：先用 RAG 检索藏品档案的形制要点，写进 `reference_notes`，再让 LLM 生成外观描述。

**失败模式**：LLM 输出的 JSON 里出现「合理但不可约束」的词（如「漂亮的衣服」）。对策是要求它输出**可视觉化**的描述。

---

## [2] Style Lock

| 项 | 内容 |
|---|---|
| 输入 | `JobRequest` + 参考图 + 预设 |
| 输出 | `StyleProfile` |
| 实现 | `server/pipeline/style_lock.py` |
| 状态 | 预设加载可用；反推与打分待实现 |

**三层约束，按可靠性递增**：

1. 风格提示词（弱）
2. 参考图 / IP-Adapter（MVP 主力，无需训练）
3. LoRA（强，需训练数据，放到 V1 之后）

**MVP 只做前两层。先训 LoRA 会卡死整条链路——这是最容易踩的坑。**

**人审检查点**：`master_reference` 必须人工确认。全自动选 master 的结果不可控。

---

## [3] Generate

| 项 | 内容 |
|---|---|
| 输入 | `CharacterCard` + `StyleProfile` + 方向×动作网格 |
| 输出 | `list[FrameAsset]` |
| 实现 | `server/pipeline/generate.py` |
| 状态 | mock 可用；local / cloud 待实现 |

**三个关键参数，缺一个都不行**：

- `seed`：帧间一致性（同 clip 共用 seed 族）
- `control_image`：姿势锁定（四向一致性靠它）
- `reference_images`：画风锁定（IP-Adapter）

**配额保护**：`max_frames_per_job` 必须在生成前检查（四向 × 两动作 × 4 帧 = 32 帧，很容易超）。

**失败模式**：逐帧自适应分辨率/尺寸。这会让归一化的统一缩放失准，表现为「有的帧角色偏大」。

---

## [4] Matting

| 项 | 内容 |
|---|---|
| 输入 | 帧 PNG |
| 输出 | 透明 PNG |
| 实现 | `server/pipeline/matting.py` |
| 状态 | rembg 与色键降级均可用 |

**真实生成图必须用 rembg**。色键降级只对纯色背景有效，是给你零依赖验证链路用的。

**失败模式**：细长结构（头发、杖、飘带）被抠断。对策是控制背景干净度，必要时后处理补回。

---

## [5] Normalize

| 项 | 内容 |
|---|---|
| 输入 | 透明 PNG 列表 |
| 输出 | 统一画布 PNG 列表 |
| 实现 | `server/pipeline/normalize.py` |
| 状态 | 可用 |

四件事：裁切 → **统一缩放**（关键）→ 统一画布 + 脚底锚点 → 可选像素网格量化。

**最容易错的一点**：逐帧自适应缩放。必须用**所有帧共用的一个 scale**，否则走路时角色忽大忽小。

---

## [6] Pack

| 项 | 内容 |
|---|---|
| 输入 | 归一化帧 |
| 输出 | `SpriteSheetInfo` + `list[SpriteRect]` |
| 实现 | `server/pipeline/pack.py` |
| 状态 | 可用 |

一行一个 clip，便于切片名可读。

**坐标系**：`SpriteRect.y` 已换算为 Unity 的**左下原点**坐标，与 `TextureImporter.spritesheet[].rect` 一致。Unity 侧不要再翻转。

**切片名格式**：`{direction}_{action}_{NN}`。末段两位帧号，装配时靠它排序——**顺序错了动画会倒放**。

---

## [7] Assemble

| 项 | 内容 |
|---|---|
| 输入 | `CharacterCard` + `SpriteManifest` + `JobRequest` |
| 输出 | `AssemblySpec` + `persona.json` |
| 实现 | 服务端 `assemble.py` + Unity `PrefabAssembler.cs` |
| 状态 | 服务端可用；Unity 侧待联调 |

**服务端不生成 prefab**，只产出指令。好处：CI 能跑；改了指令重跑即可，无需重新出图；人设与资产解耦。

**本项目最有价值的一段代码就在这里**（`PrefabAssembler.cs`）——现有开源项目全都停在「出图 + 打包」，没有一个把它变成可用的 prefab。

---

## 端到端验收（W4 之前必须跑通）

```bash
python -m server.cli smoke --prompt "博物馆导览员" --directions south north --dialogue
# 期望：out/<job_id>/ 下 7 类产物齐全，spec.animations 数量 = 方向数 × 动作数
```

Unity 侧：把窗口里的产物目录指向该 job 目录，点「仅重新装配」，应得到一个带 4 个动画片段的 prefab。
