# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。包版本以 `package.json` 为准。

---

## [0.2.4] — 2026-10-11

### 修复 · 0.2.3 编译不过：跨类型裸调 private 成员（CS0103）

0.2.3 的告警链路引入了 `PipelineResult.WithWarnings(...)`，但把它声明成了 `private static`，
而调用方在**另一个顶级类型** `CharacterForgePipeline` 里：

```
…/Services/CharacterForgePipeline.cs(321,24): error CS0103: The name 'WithWarnings' does not exist in the current context
…/Services/CharacterForgePipeline.cs(379,20): error CS0103: The name 'WithWarnings' does not exist in the current context
```

注意这条报错的**误导性**：它说的是"名称不存在"，而不是"private 不可访问"。原因是 C# 的
简单名查找根本不会去兄弟类型里找，所以压根没到"检查可见性"那一步。看上去像"方法写了
忘记保存"或"文件没编译"，实际是作用域问题——同类错误里最容易误判的一种。

- `WithWarnings` 提升为 `public static`（与同类型的 `Fail` 保持一致），两处调用点补上
  `PipelineResult.` 限定。
- **新增静态检查规则 6「跨类型调非 public 成员」**，防止复发。它用行扫描状态机判断
  「成员声明所属类型」与「调用点所属类型」，不依赖括号配对，因此对格式稳定、对字符串
  与注释免疫；并会跳过调用方自己声明同名成员的合法遮蔽情形。
  回归验证：对本条报错的修复前版本可**精确命中第 321、379 两行、零误报**，
  对当前全仓 22 个 `.cs` 文件零命中。
- 新增 `tests/test_csharp_lint.py`（5 个用例）：命中、限定后不再命中、同类内调用合法、
  同名遮蔽不误报、以及**真实仓库零命中**。C# 侧的静态检查本身现在也有测试了——
  此前它只是"没被测过的脚本"。

### 说明

0.2.3 的源码从未成功编译过，因此 0.2.4 才是本轮三处缺陷（碰撞体、warnings、文博输入）
的可运行版本。版本号必须前进，否则无法在 Package Manager 里区分"是否拿到了修复"。

---

## [0.2.3] — 2026-10-11

### 修复 · 首次真机跑通（W1）暴露的三处缺陷

占位后端首次在真实工程里跑完全链路（16 帧 → sheet → prefab），产出的碰撞体与提示链路
暴露出三个问题。这一轮**没有编译错误**，全部是"能跑但结果不对"，因此静态检查同样无能为力。

- **碰撞体尺寸取自缩放前的坐标空间，整体偏大 17.2%。** `FrameNormalizer.BoundsPixels` 是在
  **裁切阶段**量的（源图空间），而统一 scale 会改变角色在最终画布上的占比——拿它反算世界
  尺寸，等于把尺寸乘以 `1/scale`。实测 `scale = 0.8533`，胶囊体因此从应有的
  `2.68 × 4.26` 变成 `3.15 × 4.99`，明显盖住角色。
  现在改为在**贴图时**记录每帧实际落位矩形、求并集后回传——即真正的画布空间包围盒。
- **横向偏移用的是源图空间中心，与画布中心不是一回事。** 实测 `center_x = 0.87`（画布
  512px、角色居中，正确值应 ≈ 0）。两者只在"源图尺寸恰好等于画布尺寸"时相等，换用
  ComfyUI 出 1024 图就会整体错位。修复后 `centerX ≈ 0`。
  （注：0.87 里还混着占位后端自绘的右下角进度刻度——它说明**逐帧求并集**对任何单帧的
  杂散不透明像素都很敏感。真实素材有签名、水印、噪点时同样会被撑大，这条留待后续
  用更稳健的分位数取界替代。）
- **`warnings` 累积到最后没有任何消费点。** "后端不支持角色卡提炼，已用规则版"、"提供
  参考图但后端不支持风格剖析，产出不会体现画风"、"某帧未返回图像已跳过"这些**决定产出
  能否被采信**的提示，此前写进列表后就再无下文，等于没写。现在挂到 `PipelineResult.Warnings`
  并由窗口写入日志面板；同时在方向数不足 4 时主动提示"其余朝向不会有片段，四向一致性
  无法验收"。

### 修复 · 文博输入在窗口重建后静默丢失

「年代 / 形制约束」与「藏品档案 / 考据要点」是 `persona.json` 里【知识边界】的**唯一来源**，
但此前只活在窗口实例内，重开窗口即被重置为空——症状是画面看不出异常，只有翻开
`persona.json` 才发现知识边界是空的。这两个字段（连同角色描述、角色名）现走 `EditorPrefs`
持久化，并在点击生成时落盘。

### 修复 · 表单读取异常会被静默吞掉

`RunGenerateAsync` 在 `try` 之外调 `BuildRequest()`，而点击绑定是 `() => _ = RunGenerateAsync()`：
此处一旦抛异常会被丢弃的任务吞掉，表现为"点了没反应、日志零输出"，与"按钮坏了"无法区分。
现已就地接住并把原因写入日志面板；「请先填写角色描述」的校验也补进了日志面板
（此前只进状态栏，易漏看）。后端徽标同理：此前只在构建时写一次「未探测」，探测完成也不更新。

---

## [0.2.2] — 2026-10-11

### 修复 · 编辑器内图像处理与进程托管的编译错误

第二轮真机编译暴露的 12 条错误，全部集中在 `FrameNormalizer`（引擎内归一化）与
`BackendProcessManager`。它们有一个共同特征：**语法合法、API 用错**，因此语法检查无从发现。

- **`FrameNormalizer` 六处 Unity API 用法错误：**
  - 包围盒累加用 `int` 承接 `Rect.xMin/xMax`（float），触发 4 条 `CS0266`。改为 `float` 成员。
  - `new Color32(CanvasWidth * CanvasHeight)` 误把数组写成单个结构体——`Color32` 只有
    `(byte, byte, byte, byte)` 构造。改为 `new Color32[N]`（数组默认值即全透明）。
  - `FilterMode.Box` 不存在（`UnityEngine.FilterMode` 只有 Point / Bilinear / Trilinear）。
  - `SetPixels32(src.GetPixels(...))` 把 `Color[]` 喂给了需要 `Color32[]` 的 API。
    改为直接从已读出的 `Color32[]` 逐行 `Array.Copy`（顺带省掉一次类型转换）。
- **`BackendProcessManager` 不再依赖 .NET Standard 2.1 专有重载。** `Process.Kill(bool)` 只在
  .NET Standard 2.1 / .NET Core 3.0+ 存在，工程的 Api Compatibility Level 为 .NET Framework
  时报 `CS1739`。改为反射探测 + 平台回退（Windows 走 `taskkill /PID x /T /F`，最后兜底
  `Kill()`）——**三种档位下都能编译，也都真正清掉子进程**，不再要求用户改工程设置。
- **`CharacterForgeWindow` 的 `Object` 二义（`CS0104`）。** 该文件同时 `using System` 与
  `using UnityEngine`，裸写 `Object` 会在 `System.Object` 与 `UnityEngine.Object` 间二义。
  改为显式 `UnityEngine.Object`。

### 修复 · 引擎内图像处理的两处质量问题（编译错误的同源缺陷）

- **`Graphics.Blit` 的缩放过滤参数此前完全没有生效。** Blit 采样时用的是**源纹理**的
  `filterMode`，而不是目标 RenderTexture 的；原实现只设了 RT，导致 `FilterMode.Point`
  放大被静默忽略——像素风会被双线性插值糊掉。现在改为设置源纹理并在用完后还原。
- **像素网格量化改用按 alpha 加权的盒式降采样**（新增 `DownscaleBox`）。原先意图用
  `FilterMode.Box` 做缩小，但该枚举成员不存在；换成双线性又会在大倍率缩小时只采 4 个纹素，
  既漏细节、又因透明像素 RGB 为 0 而在轮廓外圈留下黑边（halo）。按 alpha 加权平均可同时
  解决这两个问题。

### 新增 · 针对此类错误的静态检查

- `scripts/check_unity_api_misuse.py`：静态捕获"语法合法、Unity API 用错"的错误。已接入 CI。
  当前规则全部来自真实踩过的坑：不存在的枚举成员、错误的参数类型、.NET Framework 档位下
  不存在的重载、`Object` 二义。脚本会先剥离注释再匹配（不解析字符串字面量，误报可用
  行内 `// lint:allow` 豁免）。已用历史上真实报错的那几行原文验证可被捕获。

### 文档

- `docs/unity_quickstart.md`：第 0 节不再要求必须把 Api Compatibility Level 改为 .NET Standard 2.1
  （插件已两种档位通吃）；报错对照表新增本轮四条；修正重复的章节编号。

---

## [0.2.1] — 2026-10-11

### 修复 · 首次导入真实 Unity 工程时的编译阻塞

- **`SentisBackend` 不再 `using` 推理引擎命名空间。** 原先按 `CHARFORGE_SENTIS` 宏切换
  `using Unity.InferenceEngine;` / `using Unity.Sentis;`，但宏只控制**代码**，不控制**程序集引用**。
  装了 `com.unity.ai.inference` 的工程点亮了宏，却因 `CharForge.Editor` 未引用其程序集而报
  `CS0234`。现在改为零引用：本后端目前只用字符串与 `Task` 描述能力，不触碰任何引擎类型。
  好处是**装不装该包都能编译**——把"可选依赖"真正留在可选状态。
  需要接实现时的三步引用流程，见 `docs/unity_quickstart.md` 第 6 节。
- **`ObjectField` 改用 `UnityEditor.UIElements` 别名。** 它是编辑器侧控件，
  `UnityEngine.UIElements` 里没有，原先报 `CS0246`。用别名而非再 `using` 一个命名空间，
  可同时规避不同 Unity 版本下潜在的 `CS0104` 二义性。
- **`NpcCharacter.dialogueEndpoint` 暴露为只读属性 `DialogueEndpoint`。** 消除
  `CS0414`（已赋值但从未使用）警告，同时让外部对话后端有正当途径读取该地址。

### 加固 · Sprite Library 持久化

- `SpriteLibraryWriter` 落盘后补 `EditorUtility.SetDirty`。此前只 `CreateAsset` + `SaveAssets`，
  存在"当时可见、重启 Unity 后分类标签消失"的假成功风险（Unity 官方论坛有实例记录）。

### 文档

- `docs/unity_quickstart.md` 报错对照表新增本轮三条编译报错；新增第 6 节说明**可选依赖的两种
  隔离手法**（零引用 vs 独立程序集 + `defineConstraints`）；补充 `.meta` 必须入库的原因。
- 新增本 CHANGELOG 与 `changelogUrl` 指向。

---

## [0.2.0] — 2026-10-11

### 变更 · 宿主倒置：Unity 成为唯一控制面

- 推理后端降级为可替换的下游，统一收口在 `IInferenceBackend` / `IWholeJobBackend`。
  四种后端：占位（零依赖）／引擎内（Sentis）／本地 ComfyUI／自带 Python 服务。
- 包最低版本提升至 **Unity 6000.0**；编辑器窗口由 IMGUI 改写为 **UI Toolkit**（uxml + uss）。
- **C# 侧流水线取代 Python 编排**：`FrameNormalizer`（缩放、脚底锚点、像素网格量化）与
  `SheetPacker` 在引擎内完成，因此逐帧路径**完全不需要 Python**。
- Unity 原生产物：切片导入设置（`SpriteDataProvider`）、`AnimatorController`（AnyState 驱动，
  8 条 transition 而非 56 条）、可选 `SpriteLibraryAsset`（`versionDefines` + `defineConstraints` 门控）、
  装配后钩子 `PostAssembleHooks`。
- `BackendProcessManager`：先探测再拉起、stdout 转发、`Kill(entireProcessTree)`、失败必给下一步。

### 修复

- **碰撞体尺寸原本是错的。** 原先写死 `size=(0.6, 1.0)`、`offset=(0.5, 0.0)`，而 512 画布 / PPU=100
  下角色实测高约 4.30 单位、宽约 2.23 单位——碰撞体会缩成一个点，生成的 prefab 一进场景就穿模。
  改为在打包阶段用 alpha 包围盒实测角色尺寸，装配时生成贴合胶囊（验证值 2.007 × 3.87）。

### 新增

- `scripts/validate_unity_package.py`：校验脚本是否都落在 asmdef 内（漏在外的会被编进
  `Assembly-CSharp`，与用户脚本冲突）、程序集引用名、类型重名、资产路径存在性、
  UPM 虚拟前缀与仓库目录名一致性。已接入 CI。

---

## [0.1.0] — 2026-10-11

### 首个骨架版本

- 七段流水线契约（角色卡 → 画风锁定 → 生成 → 抠图 → 归一化 → 打包 → 装配），
  服务端 FastAPI + WebSocket，可替换推理后端。
- 2D 角色资产：四向 idle / walk 帧、sprite sheet、切片元数据、装配指令。
- 两个示例场景：小镇店主（游戏向）、博物馆导览员（文博向，含年代与形制考据约束）。
- 三个 JSON Schema；占位后端可零依赖跑通全链路；pytest 冒烟测试。
