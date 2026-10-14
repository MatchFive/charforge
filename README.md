# CharForge · Unity 6 原生 2D 角色资产流水线

![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black?logo=unity)
![版本](https://img.shields.io/badge/版本-0.6.3-blue)
![许可](https://img.shields.io/badge/代码许可-MIT-green)
![平台](https://img.shields.io/badge/平台-Windows-lightgrey)
![ComfyUI](https://img.shields.io/badge/后端-ComfyUI-important)

> 在 Unity 编辑器里写一句话角色描述 → 本机 ComfyUI 出立绘 → 逐帧动作模仿批量出帧动画 → **得到可直接拖进场景、会走、会巡逻的 2D NPC prefab**。

**这是一个 Unity 6 UPM 包，不是一个独立工具。** 从出图、算脚底锚点、装配、到打开产物，全程不离开 Editor——Unity 是唯一控制面，ComfyUI 只是可替换的推理下游。

---

## 目录

- [它能做什么](#它能做什么)
- [工作原理](#工作原理)
- [环境要求](#环境要求)
- [ComfyUI 侧依赖（装什么、放哪、从哪下）](#comfyui-侧依赖)
- [安装插件](#安装插件)
- [快速开始](#快速开始)
- [注意事项（血泪坑合集）](#注意事项)
- [仓库结构](#仓库结构)
- [从源码构建与开发](#从源码构建与开发)
- [配套 Agent：npcforge](#配套-agentnpcforge)
- [文档索引](#文档索引)
- [致谢](#致谢)
- [许可](#许可)

---

## 它能做什么

| 产物 | 独立工具的普遍做法 | CharForge |
|---|---|---|
| 成组的帧 PNG + 正确导入设置 | 手工在 Sprite Editor 点几十下 | ✅ 自动（`TextureImporter`） |
| **脚底对齐的 pivot** | 逐张手调，还调不准 | ✅ 自动（取全部帧**并集**包围盒） |
| **`AnimatorController` + `AnimationClip`** | ❌ | ✅ 自动（`facing` + `isMoving` + `Play_xxx`） |
| **可拖进场景的 `.prefab`** | ❌ | ✅ 自动（含贴合碰撞体，尺寸由像素包围盒反算） |
| **AIGC 合规** | 不管 | ✅ prefab 写入 `AigcDisclosure` 组件（GB 45438-2025 界面侧显式标识） |

流水线是一条直线，每一步产物都落盘、可单独检查；出问题能定位到具体哪一步，而不是一次跑到黑再猜。**第 ③ 步（装配）刻意不含任何模型调用**——纯 C#、主线程、可重放，同一批帧跑两次必须得到同一个 prefab。

## 工作原理

```
① 角色立绘   角色描述 →（可选）外部 LLM 锻造提示词 → ComfyUI 文生图（Qwen-Image-2.1，原生 RGBA、吃中文）
                └ 每次锻造留档：<工程根>/CharForge/forge_log/（提示词分段留痕 + 立绘）
② 帧动画     姿势参考库逐帧动作模仿（Qwen-Image-2.1 + VNCCS Pose Studio + SAM 3D 估姿势）
                └ 参考库 = <工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png，帧数随参考图走
③ 组装       落帧 + 算 pivot + 导入设置 → .anim + AnimatorController → NPC_<角色名>.prefab（纯 C#）
```

- **立绘**：Qwen-Image-2.1 原生透明通道（RGBA VAE），中文提示词直接可用，出图即带 alpha。
- **帧动画**：不改提示词、只换姿势参考图——每帧姿势由 SAM 3D Body 从照片级参考实时估计，经 3D 人偶渲染后与立绘一起进扩散（keep_pose 动作模仿），身份与画风天然跨帧一致。
- **画风解耦**：立绘就是成片的画风来源与身份锚点，唯一底线是**材质与轮廓可辨**（发色、服饰形制、主配色一眼读得出），否则第 ② 步会走形。

## 环境要求

| 项 | 要求 | 说明 |
|---|---|---|
| Unity | **6000.0+**（Unity 6） | UI Toolkit / `TypeCache` 等新 API |
| ComfyUI | **Desktop 版强烈推荐** | keep_pose 需要 Desktop 前端在线（见注意事项） |
| GPU | NVIDIA，**16 GB 显存为实测基准**（RTX 4070 Ti Super） | 16 GB 的可行权重组合见下表；更大显存可上 BF16，更小建议 GGUF Q4 档并自担质量损失 |
| 系统 | Windows 10/11 | 插件本身不挑平台，但 ComfyUI Desktop / 权重生态以 Windows 实测为准 |
| 磁盘 | 权重约 17 GB | Qwen 三件套 + 姿势 LoRA |

## ComfyUI 侧依赖

### 模型权重（一次性下载）

| 文件 | 放到 | 来源 |
|---|---|---|
| `qwen_image_2.1_int8_convrot.safetensors` | `ComfyUI/models/diffusion_models/` | [Comfy-Org/Qwen-Image-2.1](https://huggingface.co/Comfy-Org/Qwen-Image-2.1)（[ModelScope 镜像](https://modelscope.cn/models/Comfy-Org/Qwen-Image-2.1)） |
| `qwen3vl_8b_int8_convrot.safetensors` | `ComfyUI/models/text_encoders/` | 同上（16 GB 卡也可换 `qwen3vl_8b_w4a8.safetensors`，省 3.5 GB） |
| `qwen_image_2.1_vae_bf16.safetensors` | `ComfyUI/models/vae/` | 同上 |
| `VNCCS_QI2_PoseStudioV1.1.safetensors` | `ComfyUI/models/loras/` | [ModelScope · bozoyan/VNCCS_QI2_PoseStudio](https://www.modelscope.cn/bozoyan/VNCCS_QI2_PoseStudio.git)（keep_pose 姿势服从度的来源） |

> 不要整仓库下载（全仓 70 GB+）。ModelScope 命令行示例：
>
> ```bash
> pip install -U modelscope
> modelscope download Comfy-Org/Qwen-Image-2.1 \
>   diffusion_models/qwen_image_2.1_int8_convrot.safetensors \
>   text_encoders/qwen3vl_8b_int8_convrot.safetensors \
>   vae/qwen_image_2.1_vae_bf16.safetensors \
>   --local_dir ./ComfyUI/models
> ```

**16 GB 显存的推荐组合**：INT8 DiT + W4A8 文本编码器 + VAE ≈ **13.3 GB**，1024² / 20–25 步约 25–35 秒/帧。
BF16 全家桶是 30.8 GB——单文本编码器 BF16（16.3 GB）一张卡就装不下，别被「transformer 不到 15G」的说法带偏。

### ComfyUI 自定义节点

| 节点包 | 用途 | 安装 |
|---|---|---|
| [AHEKOT/ComfyUI_VNCCS_Utils](https://github.com/AHEKOT/ComfyUI_VNCCS_Utils) | `VNCCS_PoseStudio` 节点 + 内置 SAM 3D Body 图生姿势（keep_pose 路线必需） | `git clone` 进 `ComfyUI/custom_nodes/` 后 `pip install -r requirements.txt`，或 ComfyUI Manager 搜 `VNCCS Utils` |

立绘路线只用 ComfyUI 核心节点，无需任何自定义节点。

### 工作流

随包自带、**导入即可跑**（`packages/com.charforge/Configs/comfyui/`，配同名 `.bindings.json` 告诉插件提示词/种子在哪个节点）：

| 文件 | 用途 |
|---|---|
| `charforge_qwen21_txt2img.workflow.json` | 第 ① 步角色立绘（默认） |
| `charforge_keepose_pose.workflow.json` | 第 ② 步逐帧动作模仿（默认） |
| `charforge_qwen21_img2img / sd15_txt2img / sd15_controlnet / wan22_ti2v_video` | 备用/旧路线，窗口暂无入口 |

keep_pose 工作流改造自 [RunComfy · Qwen-Image-2.1 VNCCS Pose Control](https://www.runcomfy.com/comfyui-workflows/qwen-image-2-1-vnccs-pose-control) 页面提供的原始工作流，原稿留档于 [`docs/upstream/qwen-image-2.1-vnccs-pose-control_runcomfy.json`](docs/upstream/qwen-image-2.1-vnccs-pose-control_runcomfy.json) 以供比对。

## 安装插件

三选一：

1. **下载发布包（推荐）**：到 [Releases](../../releases) 下载 `com.charforge.unity2d-<版本>.zip` → 解压 → Unity Package Manager → `+` → **Add package from disk** → 选解压目录里的 `package.json`。
2. **Git URL**：Package Manager → `+` → **Add package from git URL** → `https://github.com/MatchFive/charforge.git?path=packages/com.charforge`。
3. **从源码**：clone 本仓库后 Add package from disk 指向 `packages/com.charforge/package.json`。

依赖 `com.unity.nuget.newtonsoft-json` 与 `com.unity.2d.sprite` 随包自动装；`com.unity.2d.animation` 与 `com.unity.ai.inference` 是可选遗留项，装不装都不报错。

## 快速开始

```
1. 启动 ComfyUI（Desktop 版直接打开），确认第 ② 步工作流在 ComfyUI 里能手工跑通
   （把 Configs/comfyui/ 里的 JSON 拖进画布点一次 Queue Prompt）
2. Unity → CharForge > 角色资产流水线 → 高级设置填 ComfyUI 地址 →「探测连通性」
3. 准备姿势参考库：<Unity 工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png
   （照片级姿势参考；一个目录一组帧，文件名排序即播放顺序）
4. 第 ① 步：填角色名 + 一句角色描述 →「锻造提示词」→「生成角色立绘」
   （画幅在「高级设置 → 出图尺寸」里调：1:1 / 2:3 / 3:4 + 目标长边，生成期按比例出图、交付端 Lanczos 等比缩放）
5. 第 ② 步：在「动作 × 朝向」矩阵勾格 →「生成勾选的帧动画」
6. 第 ③ 步：「组装 prefab」→ Assets/CharForge/<角色名>/ 里的 prefab 拖进场景即可
```

> 外部 LLM（提示词锻造）**不填也能跑**，会自动退回本地模板拼接并在日志里明说。
> 只走 OpenAI 兼容 `/chat/completions`；用思考型模型（Qwen3 思考版 / R1）建议勾「关闭模型思考模式」。
>
> 安装排错与报错对照表见 **[docs/unity_quickstart.md](docs/unity_quickstart.md)**，装完卡住先看它。

## 注意事项

来自实测的坑，每一条都交过学费：

1. **ComfyUI Desktop 监听 8000，不是 8188。** 通用教程里的 8188 在 Desktop 版上是空的。另外若本机 8000 上还有别的服务（如自己的 FastAPI），写 `127.0.0.1:8000` 会打到它——填 `localhost:8000` 或局域网 IP。
2. **keep_pose 必须 ComfyUI Desktop 前端在线。** SAM 3D 估出的姿势要经前端同步到 3D 人偶再渲染，无头后端跑不了（报 `Pose Studio frontend sync failed`）。保持 Desktop 窗口打开并加载该工作流。
3. **姿势参考图要照片级。** 视频抽帧实测可用；骨架 stick figure 未经 SAM 验证。参考图少一层目录都不收（严格 `<动作>/<朝向>/` 两层），放错位置不报错、只表现为矩阵全是 `—`。
4. **改过 vnccs 服务端文件要重启 ComfyUI Desktop**；对已完成的静态帧重试必须换 seed——ComfyUI 对输入相同的提交直接回缓存，原样重提等于没跑。
5. **帧背景半透明噪点**：keep_pose 输出背景约 44% 像素 alpha 在 1–63 之间，插件收割时已做 alpha<64 压平；若仍有紫雾残留看 alpha 体检日志。
6. **Qwen-Image-2.1 是非商用许可**（Qwen Research License）。代码 MIT，但**生成内容的商用边界由模型许可决定**，商用发布前请自行核对。
7. **AIGC 标识**：面向国内发布须遵守《人工智能生成合成内容标识办法》与 GB 45438-2025。插件在 prefab 上写入 `AigcDisclosure` 组件承载界面侧显式标识，但**不替代**图片/视频自身的标识与元数据写入。
8. **角色名建议拼音/英文**：它同时用作目录名、prefab 名、`characterId` 与 `displayName`，中文能跑但跨平台 git/CI 有编码风险。
9. **配置两级查找**：`<Unity 工程根>/CharForge/<路径>`（用户覆盖）→ 包内 `Configs/<路径>`（随包默认）。`<Unity 工程根>` 是 `Assets` 的**父目录**——插件包与工程是两棵目录树，覆盖配置放 `Assets/` 里永远解析不到且不报错。

## 仓库结构

```
charforge/
├── packages/com.charforge/            # ★ Unity 6 UPM 包（控制面，发布即打包这个目录）
│   ├── Runtime/                       #   NpcCharacter / AigcDisclosure / SimplePatrol2D（零依赖）
│   ├── Configs/
│   │   └── comfyui/                   #   随包工作流 JSON + .bindings.json + 模型文档
│   └── Editor/
│       ├── UI/CharacterAssetWindow.cs # ★ 唯一窗口（UI Toolkit，纯代码构建）
│       ├── Cli/CharForgeCli.cs        # ★ 无头入口（-batchmode：勘察/装配/自检/挂脚本）
│       ├── Services/                  #   出图 / 提示词锻造 / 留痕 / 装配 / 归一化 / 动画
│       └── Util/CharForgePaths.cs     #   配置两级查找
├── agent/                             # ★ npcforge：自主 NPC 创作 Agent（独立于 Unity 运行）
├── docs/
│   ├── architecture.md                # ★ 架构总纲（宿主倒置、三步数据流）
│   ├── unity_quickstart.md            # 安装 + 跑通 + 报错对照表
│   ├── cli.md                         # 无头 CLI 详解
│   └── upstream/                      #   上游原始工作流留档（RunComfy 原稿）
├── scripts/
│   ├── build_release.py               # ★ 出发布包（zip，含 meta 覆盖检查）
│   ├── check_unity_api_misuse.py      #   CI：10 条静态规则（防"能编但不对"）
│   ├── validate_unity_package.py      #   CI：asmdef / 引用 / 资产 meta 校验
│   ├── check_prompt_parts.py          #   CI：立绘固定件三处对齐
│   ├── check_anim_clips.py            #   产物自检：数 .anim 真实关键帧（空片段当场抓）
│   └── mirror_frames.py               #   PNG 整组水平镜像（左右朝向互换标配）
├── schemas/ · configs/ · examples/    # 旧流水线契约与预设（留档，当前流程不读）
└── dist/                              # 构建产物（gitignore）
```

## 从源码构建与开发

```bash
# 出发布包（先做 meta 覆盖检查，有错拒绝出包）
python scripts/build_release.py            # → dist/com.charforge.unity2d-<版本>.zip

# 提交前的 CI 门禁（本地全跑）
python scripts/check_unity_api_misuse.py   # C# 静态规则
python scripts/validate_unity_package.py   # 包结构
python scripts/check_prompt_parts.py       # 提示词固定件三处对齐
python -m pytest tests/                    # Python 侧测试
```

- 改 C# **必须由 Unity 编译验证**（本仓库 lint 拦不住「API 名不存在」这类错，CS0117 血证）。
- 配置与工作流改在 `<Unity 工程根>/CharForge/` 覆盖目录里做，别动包内默认值。
- 版本号三处同步：`package.json` / `CHANGELOG.md` / 相关 docs。

## 配套 Agent：npcforge

`agent/` 下还有一个独立于 Unity 的 NPC 创作自主 Agent：一句话描述或一份角色列表 YAML → 自动走「人设 → 提示词 → 立绘 → 帧动画 → 质检」并产出作业记录，四个裁决点（人设 / 提示词 / 质量 / 诊断）可接 LLM 也可规则降级。它通过插件的 `CharForgeCli` 无头入口驱动 Unity，适合批量造 NPC；日常手作用编辑器窗口即可。详见 [agent/README.md](agent/README.md)。

## 文档索引

| 文档 | 内容 |
|---|---|
| [docs/architecture.md](docs/architecture.md) | **架构总纲**：宿主倒置、三步数据流、配置两级查找 |
| [docs/unity_quickstart.md](docs/unity_quickstart.md) | 安装 + 三步跑通 + 报错对照表 |
| [docs/cli.md](docs/cli.md) | 无头命令行：装配 / 自检 / 挂脚本 |
| [Configs/comfyui/README.md](packages/com.charforge/Configs/comfyui/README.md) | 工作流导入、失败对照表 |
| [Configs/comfyui/qwen_image_21.md](packages/com.charforge/Configs/comfyui/qwen_image_21.md) | Qwen-Image-2.1 权重分档与 16 GB 可行组合的完整决策记录 |
| [agent/README.md](agent/README.md) | npcforge Agent 用法 |

## 致谢

- [RunComfy · Qwen-Image-2.1 VNCCS Pose Control](https://www.runcomfy.com/comfyui-workflows/qwen-image-2-1-vnccs-pose-control) —— keep_pose 工作流的原始出处（原稿留档于 `docs/upstream/`）
- [AHEKOT/ComfyUI_VNCCS_Utils](https://github.com/AHEKOT/ComfyUI_VNCCS_Utils) —— Pose Studio 节点与 SAM 3D 图生姿势
- [bozoyan/VNCCS_QI2_PoseStudio](https://www.modelscope.cn/bozoyan/VNCCS_QI2_PoseStudio) —— 姿势 LoRA
- [Comfy-Org/Qwen-Image-2.1](https://huggingface.co/Comfy-Org/Qwen-Image-2.1) —— 权重量化重打包
- [Qwen-Image-2.1](https://huggingface.co/Qwen) —— 阿里通义千问图像生成模型

## 许可

代码以 **MIT** 许可发布（见 [LICENSE](LICENSE)）。

但请注意**许可分层**：

- **模型权重不受本仓库 MIT 约束**。Qwen-Image-2.1 采用 Qwen Research License（**非商用**）；VNCCS LoRA 与其他第三方权重的许可以其发布页为准。生成内容的商用边界由所用权重许可决定。
- **生成内容合规**：面向国内发布须遵守《人工智能生成合成内容标识办法》与强制性国标 GB 45438-2025。插件已在 prefab 上写入 `AigcDisclosure` 组件承载界面侧显式标识，但不替代图片/音频/视频自身的隐式标识与元数据写入。
