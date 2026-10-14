# com.charforge.unity2d

Unity 6 原生 2D 角色资产流水线插件。在编辑器内完成「一句话角色描述 → 立绘 → 帧动画 → 可运行的 NPC prefab」。

## 安装

**方式一（本地）**：Package Manager → `+` → Add package from disk → 选择本目录的 `package.json`。

**方式二（Git URL）**：Package Manager → `+` → Add package from git URL。

**依赖**：
- `com.unity.nuget.newtonsoft-json`（必需，随包自动安装）
- `com.unity.2d.sprite`（必需，随包自动安装——切片导入设置要用它的 Sprite Data Provider）
- `com.unity.2d.animation`（**可选·遗留**）：只有已下线的旧流水线才会生成 Sprite Library。
  由 `versionDefines` + `defineConstraints` 自动开关，**装不装都不报错**——当前三步流程不产出 `*_SpriteLibrary.asset`。
- `com.unity.ai.inference` ≥ 2.0（**可选·遗留**）：曾经用于「引擎内推理」后端，当前无 UI 入口。

## 用法

`CharForge > 角色资产流水线`（唯一窗口，三步一条链）

1. **① 角色立绘**：写一句角色描述 → 「锻造提示词」（外部 LLM，未配置则本地模板）→
   可手工改提示词 → 「生成角色立绘」。也可以直接「载入已有立绘…」跳过生成。
   画幅在「高级设置 → 出图尺寸」控制：比例（1:1 / 2:3 / 3:4）生成期就调好，
   交付端 Lanczos3 预乘 alpha 等比缩到目标长边（2:3 / 3:4 是 2.5D 立绘主流画幅）。
   画风由插件固定（**华丽精致的半写实厚涂**，见 `PromptForgeService.StylePrefix`），
   模型只写「角色本体」——身份、服饰、道具、神态。每次锻造会把判据留档（见下）。
2. **② 帧动画**：在「动作 × 朝向」矩阵里勾需要的格 →
   「生成勾选的帧动画」。每组按姿势参考库逐帧动作模仿出图，
   **帧数随参考图张数走**（参考库里几张就出几帧），边跑边出预览，逐格体检。
3. **③ 组装**：勾碰撞体 / 移动方式 / 对话开关 → 「组装 prefab」。

`ComfyUI 地址 / 工作流文件 / 外部 LLM / 关闭思考模式 / 输出目录 / 帧率` 收在「高级设置」折叠区——
设一次就不动的东西，不该摆在流程中间。

产物落在 `Assets/CharForge/<角色名>/`：`Frames/` 帧 PNG、`Animations/` 片段与状态机、`NPC_<角色名>.prefab`。

**第 1 步另有一份留痕**，落在**工程根** `<工程根>/CharForge/forge_log/`：
`<时间戳>_<角色名>.txt`（分段标出哪句是你写的、哪句是模型写的、哪句是插件套的画风）、
`.json`（机器读）、`_portrait.png`（立绘本身）—— 同前缀一组，配对查看不用翻目录。
放工程根而不是 `Assets/`：它是**排查用的中间物、不是游戏资产**，
进 `Assets/` 会被 Unity 导入（1024×1024 的 PNG 还要生成 `.meta`），而它永远不该进 prefab。

## 目录职责

| 目录 | 内容 | 备注 |
|---|---|---|
| `Runtime/` | `NpcCharacter` / `AigcDisclosure` / `SimplePatrol2D` | 不引用任何可选包，运行期可安全打包 |
| `Editor/UI/` | 唯一窗口 `CharacterAssetWindow`（UI Toolkit，纯代码构建） | 三步纵向排布 + 折叠的「高级设置」 |
| `Editor/Backends/` | `IInferenceBackend` + 4 个后端实现 | ⚠ **已下线**：仍能编译，但窗口已无入口 |
| `Editor/Services/` | 生成服务 / 提示词锻造 / 提示词留痕 / 帧装配 / 归一化 / 打包 / 动画 / 进程托管 | 服务层只算不碰 UI，资产写入统一走装配器（主线程） |
| `Editor/Integrations/` | Sprite Library 集成 | 独立程序集，由 2D Animation 包有无自动开关 |

## 程序集设计

三个程序集，边界刻意划清：

```
CharForge.Runtime                      ← 无外部依赖，运行期可打包
CharForge.Editor                       ← 核心：编排 / 装配 / 导入 / 后端
CharForge.Editor.SpriteLibrary         ← 可选：依赖 com.unity.2d.animation
```

`CharForge.Editor.SpriteLibrary` 用 `defineConstraints: ["CHARFORGE_2D_ANIMATION"]` 实现
"装了才编译"。它通过 `[InitializeOnLoad]` 把钩子注册进 `PostAssembleHooks.AfterAssembleRoot`，
因此**核心程序集不需要引用它**，也就不会产生"依赖可选包"的连锁问题。

## 常见问题

**Q：第 1 步点「生成角色立绘」一直连不上？**
ComfyUI 地址填错了。**ComfyUI Desktop 默认监听 `8000`**，通用教程里的 `8188` 在 Desktop 版上是空的。
在「高级设置」里改地址后点「探测连通性」——探测自带 5 秒上限，连不上会直接给一句人话，不会一直转圈。

**Q：「锻造提示词」出来的结果像模板拼的？**
外部 LLM 的三个字段（BaseURL / API Key / 模型名）**三项齐了才走模型**。
少填一个就自动退回本地模板拼接，日志里会写明本次走的是哪条路。

**Q：锻造出来的提示词里混进了「让我分析一下需求…」「检查一下是否符合要求」？**
用的是**思考型模型**（Qwen3 思考版 / DeepSeek-R1 一类），它把思维链写进了 `content`。
v0.4.2 起会先剥 `<think>` 块再拼装；若日志出现 `⚠ 已从中摘出 xx 字`，
说明正文被剥空了、提示词是从思考块里摘的——**在高级设置勾上「关闭模型思考模式」重锻一次**。
那个开关默认不勾，因为严格的网关会因未知参数回 400。

**Q：第 2 步提示「未找到姿势参考库」、勾选矩阵整片是 `—`？**
姿势参考库放在 `<工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png`（用户覆盖）
或包内 `Configs/comfyui/pose_refs/`（随包默认）。一个目录 = 一组帧序列，文件名排序即帧序。

**Q：第 2 步报 `Pose Studio frontend sync failed`？**
keep_pose 的姿势来自 SAM 3D Body 实时估计，估计结果要经 **ComfyUI Desktop 前端**同步到
3D 人偶再渲染——保持 Desktop 窗口打开并加载 Pose Studio 工作流，重跑即可。
ComfyUI 端还需 `vnccs-utils` 套件与 `VNCCS_QI2_PoseStudioV1.1` LoRA。

**Q：画风为什么是华丽厚涂？想换怎么办？**
v0.4.8 起第 2 步是 keep_pose 动作模仿，**立绘就是成片的画风来源**（身份槽直接决定输出风格）——
立绘画风糊，帧动画跟着糊。唯一不能破的底线是**材质与轮廓可辨**：
发色、服饰形制、主配色要一眼读得出来，否则第 2 步会走形。

换画风改 `PromptForgeService.StylePrefix` 一处，**并同步改**
`Configs/comfyui/charforge_qwen21_txt2img.workflow.json` 节点 6 的默认提示词——
两处不一致不会报错，只会让「在 ComfyUI 里手工排队」与「插件出的图」悄悄变成两种画风。

**Q：prefab 名字为什么直接用我填的角色名？**
当前版本没有做 ASCII 兜底，**「角色名」同时用作目录名、prefab 名、`characterId` 与 `displayName`**，
也没有单独的「中文显示名」字段。中文能跑，但中文进文件名会在跨平台 git、CI、
不同文件系统编码下出问题——想省事就填拼音或英文（代价是显示名也是拼音）。
