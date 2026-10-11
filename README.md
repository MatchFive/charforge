# CharForge · Unity 6 原生 2D 角色资产插件

> 在编辑器里说一句话 + 拖几张项目画风参考图 → **得到可直接拖进场景、会走、会说话的 2D NPC prefab**。

**这是一个 Unity 6 UPM 包，不是一个独立工具。** 从探测后端到出图、切片、装配、打开产物，全程不离开 Editor。

---

## 为什么"插件"和"独立工具"不是一回事

现有同类工具（SpriteForge 是 CLI、GenForgeSprite 是 REST 服务、Animyth 是教程脚本）能力都不弱，但都有一个结构性问题：**它们是宿主，Unity 只是产物接收方**。于是用户必须离开编辑器、跑命令、再回 Unity 手工导入。

CharForge v0.2 做了**宿主倒置**：Unity 是唯一控制面，推理后端降级为可替换的下游。差别直接体现在产物上——

| 产物 | 独立工具 | CharForge（插件） | 靠什么 API |
|---|---|---|---|
| 切片正确的 sheet + `.meta` | 得手工在 Sprite Editor 点几十下 | ✅ 自动 | `SpriteDataProviderFactories` |
| **`SpriteLibraryAsset`（分类/标签）** | ❌ 做不到 | ✅ 自动 | `SpriteLibraryAsset` |
| **`AnimatorController` + `AnimationClip`** | ❌ 做不到 | ✅ 自动 | `UnityEditor.Animations` |
| **可拖进场景的 `.prefab`** | ❌ 做不到 | ✅ 自动 | `PrefabUtility` |
| 图层式 `.psb` → PSD Importer 自动绑骨 | ❌ 做不到 | ⬜ 路线图 W4 | `com.unity.2d.psdimporter` |

---

## 四层可替换推理后端

同一套 `IInferenceBackend` 接口收口，换后端 = 换窗口里一个下拉框，装配代码一行不改。

| 层 | 后端 | 外部进程 | 画风锁定 | 适用 |
|---|---|---|---|---|
| A | **占位后端** | 无 | — | 零依赖，第一次打开就能跑通全流程；也是 CI 的回归基线 |
| B | **引擎内推理（Sentis）** | 无（模型在工程内） | ❌（无法训 LoRA） | 不装任何环境出图；实验性，需 `com.unity.ai.inference` |
| C | **本地 ComfyUI** | 需（插件可代管起停） | ✅ 完整（LoRA + ControlNet + IP-Adapter） | **正式生产首选** |
| C | **自带 Python 服务**（`server/`） | 需（插件可代管起停） | ✅ | 复用 v0.1 已验证的流水线 |
| D | **远端 / 商业 API** | 网络 | 视供应商 | 团队已有付费画风模型（接口已留，未实现） |

> **引擎内推理不能替代 ComfyUI**：画风一致性本质是**训练问题**，不是提示词问题——Unity 官方内置的 LoRA 也来自外购。所以 B 层的价值是"删掉配置门槛"，C 层才是画质与一致性的正解。

---

## 快速开始（约 5 分钟，无需任何模型）

```
1. Unity 6 工程 → Window > Package Manager → + → Add package from disk
   → 选择 packages/com.charforge/package.json
2. Window > CharForge 角色资产生成
3. 后端选「占位后端（零依赖·立即可用）」
4. 填一句角色描述 → 点「生成并装配」
5. 在 Assets/CharForge/<job_id>/ 里得到 prefab —— 拖进场景即可
```

想接真实模型时（本地 ComfyUI / 自带 Python 服务），在窗口「④ 后端设置」里填地址与启动命令，之后点「启动后端」由插件代管进程——**你不需要开终端**。

---

## 仓库结构

```
unity2d-charforge/
├── packages/com.charforge/            # ★ Unity 6 UPM 包（控制面）
│   ├── Runtime/                       #   运行期组件（不依赖任何可选包）
│   │   ├── NpcCharacter.cs            #     身份 + 人设 + 朝向
│   │   ├── AigcDisclosure.cs          #     AIGC 显式标识数据（GB 45438-2025）
│   │   └── SimplePatrol2D.cs          #     巡逻行为
│   └── Editor/
│       ├── UI/                        #   UI Toolkit 窗口（uxml + uss）
│       ├── Backends/                  #   IInferenceBackend + 4 个实现
│       ├── Services/
│       │   ├── CharacterForgePipeline.cs   # C# 编排（取代 Python orchestrator）
│       │   ├── FrameNormalizer.cs          # [5] 归一化（引擎内，不需 Python）
│       │   ├── SheetPacker.cs              # [6] 打包（引擎内，不需 Python）
│       │   ├── AnimatorWriter.cs           # 生成 Controller + Clip
│       │   ├── BackendProcessManager.cs    # 后端进程托管（起停 + 日志转发）
│       │   └── ProjectAssetWriter.cs       # 工程内落盘
│       ├── PrefabAssembler.cs         # ★ 装配（本项目最有价值的代码）
│       ├── SpriteImportSetup.cs       # ★ 导入设置自动生成
│       ├── PostAssembleHooks.cs       # 可选依赖的扩展点
│       └── Integrations/              # Sprite Library（自动按包有无开关）
├── server/                            # Python 服务：**v0.2 后是可选后端之一**
├── schemas/                           # 三方共同语言（Python / C# / JSON Schema）
├── configs/                           # 风格预设 + 四向姿势模板
├── examples/                          # 游戏向 + 文博向示例
├── docs/architecture.md               # ★ 架构总纲（先读这个）
└── scripts/validate_unity_package.py  # CI：asmdef 归属 / 引用 / 资产路径校验
```

---

## 三方契约（唯一的强耦合点）

`assembly_spec.json` / `manifest.json` / `persona.json` 三份结构由
`schemas/*.schema.json` 定义，Python（`server/models/schemas.py`）与
C#（`Editor/Data/CharForgeContracts.cs`）两侧必须逐字段一致。

保留 JSON 契约（而不是让服务端直接产 prefab）的三个理由：装配指令**可重放**（改台词不用重新出图）、
CI **不依赖 Unity**、人设与美术资产**解耦**。

---

## 文档

| 文档 | 内容 |
|---|---|
| `docs/architecture.md` | **架构总纲**：宿主倒置、三层后端、进程托管、v0.1→v0.2 迁移对照 |
| `docs/pipeline.md` | 七段流水线的输入输出契约 |
| `docs/unity_readiness.md` | Unity 就绪度评分规则（法线/UV/PPU/锚点/切片） |
| `docs/roadmap.md` | 分周实施路线（含验收标准） |

---

## 许可

MIT（待定）。生成内容的使用须遵守素材来源授权与所在地法规；面向国内发布时须遵守
《人工智能生成合成内容标识办法》与强制性国标 GB 45438-2025 的标识要求——
插件已在 prefab 上写入 `AigcDisclosure` 组件承载界面侧显式标识，但**不替代**
图片/音频/视频自身的标识与元数据写入。
