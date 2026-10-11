# CharForge 架构总纲（v0.2 · Unity 6 原生插件）

> 本文回答一个问题：**为什么 CharForge 是 Unity 插件，而不是独立工具；以及"插件化"在工程上具体意味着什么。**

---

## 一、先纠正一个常见误解

"独立工具效果差"这个判断是对的，但**原因不是"用了 Python"**。

v0.1 版本被设计成「Python 是宿主，Unity 只负责最后一步装配」——用户必须离开编辑器去跑 `python -m server.cli`，生成完再回 Unity 导入。这才是体验差的真正来源。业界同类方案（SpriteForge 是 CLI、GenForgeSprite 是 REST 服务、Animyth 是教程脚本）全都有这个问题。

**真正的分野是"谁是宿主"，而不是"有没有外部进程"。**

| 形态 | 宿主 | 用户体验 | 产物质量 |
|---|---|---|---|
| 独立 CLI 工具 | 终端 | 离开编辑器，手工导入 | 只能给"素材" |
| REST 服务 | 服务端 | 需自己写客户端 | 只能给"素材" |
| **编辑器插件 + 托管后端** | **Unity** | **全程不出编辑器** | **可给"可运行 prefab"** |

参照系：`Amin-HP/Unity-ComfyUI-Bridge` 就是一个纯 UPM 包，用户在 Inspector 里配置、在 Unity 里排队执行，ComfyUI 只是它托管的后端——没人认为它是"独立工具"。

**所以 v0.2 做的是"宿主倒置"：Unity 成为唯一的控制面，Python 降级为可替换的推理后端之一。**

---

## 二、三层后端：插件能不能"不装 Python"

2026 年这个问题的答案已经和 2024 年不同了。**可以完全不装 Python。**

Unity 官方的 **Sentis / Unity Inference Engine**（`com.unity.ai.inference`，v2.6.x，Unity 6 全线支持）能在引擎内直接跑 ONNX 模型；Asset Store 上已经出现基于它的「零 Python 进程」本地出图资产，可跑 SDXL / FLUX.2 / SDXS Mobile（Windows 桌面 + 独显已验证）。

因此 CharForge v0.2 定义**三层可替换后端**，用同一个 `IInferenceBackend` 接口收口：

| 层 | 后端 | 进程外依赖 | 质量 | 适用 |
|---|---|---|---|---|
| **A** | `SentisBackend` | **无**（模型在工程内） | 中（受显存与模型档位限制，**无法训 LoRA**） | 想零配置试用、小体量项目、离线环境 |
| **B** | `ComfyUiBackend` ／ `SidecarBackend` | 本地 ComfyUI 或本仓库 Python 服务 | **高**（SDXL/Flux + LoRA + ControlNet + IP-Adapter 全量） | 正式生产；画风锁定必须靠它 |
| **C** | `HttpBackend` | 远端/商业 API（Scenario、Layer AI、自建） | 高 | 团队已有付费画风模型 |
| — | `MockBackend` | 无 | 占位图 | CI 与流程自测 |

**关键设计**：`SentisBackend` 不是"降级方案"，而是把"装不上环境"这个劝退门槛直接删掉。用户第一次点开窗口就能出东西，之后再按需升级到 B 层——**首次成功率决定开源项目有没有第二个用户**。

> 判断依据：三层后端全部走同一套 `IInferenceBackend`，所以「换后端」= 换一个下拉框，不改任何调用代码。这也是简历上可以讲的一段架构设计。

---

## 三、宿主倒置带来的一件真正重要的事：产物是 Unity 原生资产

独立工具无论多强，交付物只能是"一堆 PNG + 一个说明文档"，因为**它拿不到 Unity 的资产管线**。成为插件之后，CharForge 可以直接产出只有 Unity 侧才能生成的东西：

| 产物 | 由谁生成 | 独立工具能做到吗 |
|---|---|---|
| 切片正确的 `sheet.png` + `.meta` | `UnityAssetWriter`（现代 `SpriteDataProvider` API） | ⚠️ 要用户手工切 |
| **`SpriteLibraryAsset`（分类/标签）** | `SpriteLibraryWriter` | ❌ 做不到 |
| **`AnimatorController` + `AnimationClip`** | `AnimatorWriter` | ❌ 做不到 |
| **可直接拖进场景的 `.prefab`** | `PrefabAssembler` | ❌ 做不到 |
| 图层式 `.psd` → **PSD Importer 自动绑骨** | 生成 + `com.unity.2d.psdimporter` | ❌ 做不到 |

第 2、3、5 行是**只有插件形态才存在的产品能力**，也是"效果差"这个直觉的真正落点：

- **Sprite Library + SpriteResolver**：四向 idle/walk 被组织成「分类=朝向/动作、标签=帧或变体」，运行期 `SetCategoryAndLabel` 换装、换表情**不需要重新导入、不丢蒙皮**。独立工具给的 PNG 做不到这件事。
- **PSD Importer 路径**：把角色按「底身 / 发型 / 外袍 / 面部」分层输出成 `.psb`，Unity 的 PSD Importer 打开 Mosaic + Character Rig 就能自动生成**带骨骼的 prefab**。这是 2D 角色从"帧动画"升级到"骨骼动画"的入口，而它**必须由插件在工程内生成**。

---

## 四、进程之外的依赖怎么处理

`SentisBackend` 以外的后端需要本地推理环境。处理原则：**用户可以不知道它的存在，但必须能看见它、能关掉它。**

`BackendProcessManager` 负责：

1. **自动发现**：探测 127.0.0.1:8188（ComfyUI）/ 127.0.0.1:8000（本仓库 Python 服务），命中就复用，不重复拉起
2. **按需拉起**：未发现时，若配置了可执行路径则后台拉起，并把 stdout 转发到 Unity Console
3. **健康检查**：轮询 `/system_stats` 或 `/health`，就绪才允许提交任务
4. **自动收起**：编辑器退出、空闲超时、或用户点"停止后端"时终止**整棵进程树**。实现上不写
   `Kill(entireProcessTree: true)`——该重载只在 .NET Standard 2.1+ 存在，工程的
   Api Compatibility Level 为 .NET Framework 时会编译不过。改用反射探测该重载，
   探测不到则在 Windows 上走 `taskkill /PID x /T /F`，最后兜底 `Kill()`：
   **两种档位都能编译，也都真的清得干净**
5. **绝不静默失败**：任何一步失败都在窗口里给出**可执行的下一步**（"未检测到 ComfyUI，点此查看安装指引 / 或切换到本地 Sentis 后端"）

---

## 五、数据流（v0.2）

```
Unity Editor（宿主 · 控制面）
│
├─ CharacterForgeWindow（UI Toolkit）
│    描述 / 参考图 / 方向动作 / 后端选择
│
├─ CharacterForgePipeline（C# 编排，替代 Python orchestrator 的主控地位）
│    ① 角色卡提炼      → 可走 Backend.DescribeAsync 或本地规则
│    ② 风格约束        → Backend.ExtractStyleAsync（参考图 → 风格约束）
│    ③ 逐帧生成        → Backend.GenerateAsync（返回图片字节）
│    ④ 抠图            → Backend.MattingAsync（rembg / 色键）
│    ⑤ 归一化          → 服务端或本地（统一缩放 + 脚底锚点 + 像素网格）
│    ⑥ 打包 sheet      → UnityAssetWriter
│    ⑦ 装配            → SpriteImportSetup + SpriteLibraryWriter + AnimatorWriter + PrefabAssembler
│
└─ 后端（可替换 · 数据面）
     SentisBackend（进程内）／ ComfyUiBackend ／ SidecarBackend ／ HttpBackend ／ MockBackend
```

**职责边界**：Unity 侧负责**一切与资产、编辑器、用户交互相关的逻辑**；后端只负责**它不可替代的那一件事——扩散推理与风格分析**。这条边界划清楚之后，后端换成什么都不会影响插件的形态。

---

## 六、v0.1 → v0.2 迁移对照

| 模块 | v0.1 | v0.2 |
|---|---|---|
| 控制面 | Python `orchestrator.py` | **C# `CharacterForgePipeline`** |
| 编辑器 UI | IMGUI `OnGUI` | **UI Toolkit（UXML + USS）** |
| 最低 Unity | 2021.3 | **Unity 6（6000.0+）** |
| 推理调用 | 硬编码 HTTP | **`IInferenceBackend` 可替换四后端** |
| 切片导入 | `TextureImporter.spritesheet` | **`SpriteDataProviderFactories`（Unity 6 推荐 API）** |
| 动画 | 只有 AnimatorController | AnimatorController + **SpriteLibraryAsset** |
| 后端进程 | 用户手工启动 | **`BackendProcessManager` 托管** |
| Python 服务 | 唯一的生成套件 | **可选后端之一（`server/` 保留，语义降级）** |
| 装配契约 | JSON spec 回放 | JSON spec 仍保留（**可重放、CI 可跑**），但生成主控在 C# |

**刻意保留的**：`assembly_spec.json` / `manifest.json` / `persona.json` 三份契约不变。理由：装配指令可重放（改台词不用重新出图）、CI 不依赖 Unity、人设与资产解耦。这是 v0.1 做对的地方，不该丢。

---

## 七、落地顺序（对应 `docs/roadmap.md` 的调整）

| 周 | 目标 | 验收 |
|---|---|---|
| W1 | `SentisBackend` 或 `MockBackend` 打通「窗口 → 后端 → 落盘 → 装配」，**全程不出编辑器** | 点一次按钮得到 prefab |
| W2 | 接 `ComfyUiBackend`，实现风格锁定（参考图 + IP-Adapter） | 两张角色图风格一致 |
| W3 | `SpriteLibraryWriter` + `AnimatorWriter` 打磨，`BackendProcessManager` 生命周期 | 换装不重导、后端起停无残留 |
| W4 | 分层 PSD 输出 → PSD Importer 绑骨（实验性）；文博场景端到端 | 博物馆导览员完整交付 |

---

## 八、复用与延伸

本仓库的 `server/`（Python）在 v0.2 后不再承担编排职责，但**代码没有浪费**：它的 prompt_forge / style_lock / normalize 逻辑是 `SidecarBackend` 的服务端实现，第三、五、六段仍然可用。这与《开源立项书-Unity游戏AI-NPC运行时》的关系也保持不变：

- **本项目**管 NPC 的「出生」（生产装配）
- **Game Agent Runtime**管 NPC 的「活着」（运行时记忆、打断、降级、成本、合规）
- 合起来是完整叙事：**从一个想法到能上线运行的 AI NPC**
