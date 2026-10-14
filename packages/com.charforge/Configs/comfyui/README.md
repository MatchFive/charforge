# 接本地 ComfyUI

这份目录是**给 CharForge 用的 ComfyUI 工作流**，不是通用配置。导入即可跑，
不用自己在 ComfyUI 界面里从零搭图、也不用研究 API 格式怎么导出。

## 0. 先看这张表：哪个工作流现在真在用

| 文件 | 用途 | 当前三步流程用不用 |
|---|---|---|
| `charforge_qwen21_txt2img.workflow.json` | 第 1 步：**角色立绘**（Qwen-Image-2.1，原生 RGBA、吃中文） | ✅ **默认走这个** |
| `charforge_keepose_pose.workflow.json` | 第 2 步：**逐帧动作模仿**（Qwen-Image-2.1 + VNCCS Pose Studio + SAM 3D 估姿势） | ✅ **默认走这个** |
| `charforge_qwen21_img2img.workflow.json` | 图生图 / 风格母版 | ⬜ 已备好，窗口暂无入口 |
| `charforge_sd15_txt2img.workflow.json` | SD1.5 文生图 | ⬜ 旧路线，窗口暂无入口 |
| `charforge_sd15_controlnet.workflow.json` | SD1.5 + ControlNet OpenPose | ⬜ 旧路线，窗口暂无入口 |
| `charforge_wan22_ti2v_video.workflow.json` | Wan2.2 图生视频 | ⬜ 实验，见 `wan22_ti2v_5b.md` |
| `*.bindings.json` | 告诉插件「哪个节点是正向提示词、哪个是种子」 | **必须和工作流同名同目录**，插件按 `<工作流文件名>.bindings.json` 去找 |

> 本文件第 1～3 节、第 5～7 节写于 SD1.5 + ControlNet 路线时期，
> 讲的是**旧路线的显存估算与四向一致性思路**，仍然有参考价值（尤其「先跑通再升级」的节奏）；
> 但**当前窗口只走 Qwen 立绘 + keep_pose 逐帧动作模仿两条**，装什么模型请以下面这张表和
> `qwen_image_21.md` 为准。

> **改工作流时别踩这个坑**：`charforge_qwen21_txt2img.workflow.json` 的生成链与官方模板
> 「Qwen-Image-2.1 Text to Image」**逐字段一致**（已用脚本比对验证），只做了三处改动：
> 剔除提示词增强器分支（另一个 9B 模型）、节点重编号为 `1`–`10`、输出前缀改为 `charforge/qwen21`。
> 分辨率由节点 `4` 的 `ResolutionSelector`（1:1 / 1 MP → 1024×1024）决定，
> **不要在 bindings 里绑 `width` / `height`** —— 节点 `5` 的宽高是链接，
> 绑定会把链接替换成字面量，等于逼 Qwen 用 512 出图。

## 0.1 模型放哪（当前 Qwen / keep_pose 路线）

```
ComfyUI/
├── models/
│   ├── diffusion_models/   ← Qwen-Image-2.1（立绘与帧动画同底座，INT8 档）
│   ├── text_encoders/      ← Qwen3-VL 文本编码器（**16 GB 卡上必须用 W4A8 量化档**）
│   ├── vae/                ← Qwen 配套 VAE
│   ├── loras/              ← VNCCS_QI2_PoseStudioV1.1（keep_pose 姿势服从度的来源）
│   ├── checkpoints/        ← 仅旧 SD1.5 路线用
│   └── controlnet/         ← 仅旧 SD1.5 路线用
└── custom_nodes/           ← keep_pose 需要 vnccs-utils（含 SAM 3D Body 估姿势）
```

16 GB 显存下 Qwen-Image-2.1 的**唯一可行组合**（INT8 DiT + W4A8 文本编码器 + VAE ≈ 13.3 GB）
与官方 BF16 的 30.8 GB 差距、许可约束，全部记在 **`qwen_image_21.md`** —— 装模型前先读。
旧 2509 + Nunchaku + Spritely Sheet 路线的决策记录在 `../../../docs/qwen-edit-2509-sprite-lora.md`（路线已于 v0.4.8 删除，留作参考）。

---

## 1. 你的 4070 Ti Super（16 GB）能做到什么（**旧 SD1.5 路线**）

> 当前路线的实测数据是另一组：**Spritely 六帧 sheet 约 15–25 秒/组**（4 步采样、CFG 1），
> Qwen 立绘按 `ResolutionSelector` 出 1024。下面这一节保留的是**旧 SD1.5 路线的显存估算**，
> 理由（「先跑通再升级」「不要跳步」）仍然成立，但**数字不要照搬到新路线**。
> 16 GB 跑 Qwen 的可行组合与显存明细见 `qwen_image_21.md`。

先说结论：**16 GB 跑 2D 角色立绘绰绰有余，而且这个项目的画布尺寸恰好落在最省的那个点上。**

| 路线 | 单帧耗时（估） | 显存峰值 | 16 帧总耗时 |
|---|---|---|---|
| **SD1.5 @ 512×512**（推荐起步） | 1.5–3 秒 | ~4–5 GB | **30–60 秒** |
| SDXL @ 1024×1024（质量升级） | 6–10 秒 | ~9–11 GB | 2–3 分钟 |
| Flux dev fp8 @ 1024 | 20–40 秒 | ~14–16 GB（需 offload） | 6–11 分钟 |

**为什么建议从 SD1.5 起步，而不是直接上 Flux：**

1. 插件的生成画布就是 **512×512**（`FrameRequest.Width/Height` 默认 512，
   `FrameNormalizer.CanvasWidth` 也是 512）。SD1.5 的原生分辨率正是 512；
   而 SDXL 的原生分辨率是 1024，**把它压到 512 反而比 SD1.5 差**（训练分辨率不匹配会糊）。
2. Flux 的 ControlNet 生态明显弱于 SD1.5/SDXL，而四向一致性**必须**靠 ControlNet。
   先上 Flux 等于把最需要的那件工具丢了。
3. 16 帧要跑 16 次推理，SD1.5 的迭代速度让你能在几分钟内试 5 版提示词，
   而 Flux 一版就是十分钟——**这个阶段速度比单张质量重要**。

> 也就是说：SD1.5 跑通 → 确认四向一致性 → 再考虑升 SDXL 提质量。
> 中途不要跳步，跳了以后分不清「是模型不行还是姿势没约束住」。

---

## 2. 模型放哪（**旧 SD1.5 路线**，一次性）

ComfyUI 根目录下：

```
ComfyUI/
├── models/
│   ├── checkpoints/     ← 放 SD1.5 主模型（.safetensors）
│   └── controlnet/      ← 放 control_v11p_sd15_openpose.pth
└── custom_nodes/        ← 本方案不需要任何自定义节点
```

**checkpoint（必需，二选一）**：一个 2D/插画向的 SD1.5 模型。选型时注意两点——
**授权是否允许商用**（Civitai 上很多模型标注了 `No selling images`），
以及是否能稳定画出**全身全身立绘**。古风/国风向的 SD1.5 微调模型最适合本项目。

**ControlNet（第二步才需要）**：
`control_v11p_sd15_openpose.pth`，来自 `lllyasviel/ControlNet-v1-1`。
放到 `models/controlnet/` 下，文件名保持一致，否则要改 workflow 里的 `control_net_name`。

> **不要**现在下载 LoRA，也**不要**现在训练。参考图 + IP-Adapter 起步，通了再考虑训。
> 这一步顺序错了，你会花一周训出一个「自己都不确定哪版更好」的 LoRA。

---

## 3. 导入工作流

**所有工作流都是同一个套路**：打开 ComfyUI 界面 → 把 JSON **拖进画布** → 点一次 **Queue Prompt**，
确认能出图。**这一步必须在 ComfyUI 界面里手工验证通过**，否则插件那边只会给你一个含糊的超时错误。

当前默认路线要手工改的地方：

- `charforge_qwen21_txt2img.workflow.json` —— 检查 UNet / 文本编码器 / VAE 三个加载节点的
  文件名与你 `models/` 下的实际文件名一致（**16 GB 卡上文本编码器必须用 W4A8 量化档**，见 `qwen_image_21.md`）。
- `charforge_keepose_pose.workflow.json` —— 检查节点 470/507 两张 LoadImage 的文件名存在于
  `ComfyUI/input/`（插件运行时会自动上传覆写，手工验证时可随便放）；确认
  `VNCCS_QI2_PoseStudioV1.1` LoRA 文件名与 `models/loras/` 一致，且 `custom_nodes/vnccs-utils` 已装。

<details>
<summary>旧 SD1.5 路线（已无窗口入口，留作参考）</summary>

1. 把 `charforge_sd15_txt2img.workflow.json` 里的
   `"PUT_YOUR_SD15_CHECKPOINT_HERE.safetensors"` 改成你的实际文件名
   （或导入后在界面上点一下节点改）。
2. `ControlNet` 版本还需额外把 `control_net_name` 改成你实际的文件名，
   并确认 `LoadImage` 节点里那张 `south_idle_00.png` 已经存在于 `ComfyUI/input/`
   （插件会在生成时自动上传姿势图，见第 5 节；手工验证时可以先随便放一张）。

</details>

---

## 4. 在插件里配

`CharForge > 角色资产流水线` → 展开最下面的**「高级设置」**折叠区，只需两个字段：

| 项 | 值 |
|---|---|
| **ComfyUI 地址** | `http://127.0.0.1:8188`（手装版默认）。**Desktop 版监听 8000**，本机须填 `http://127.0.0.1:8000` |
| **立绘工作流文件** | `charforge_qwen21_txt2img.workflow.json` —— **填文件名即可** |
| **帧动画工作流文件** | `charforge_keepose_pose.workflow.json` |

点 **「探测连通性」**，应显示连通。**工作流只填文件名**，插件自动在
「工程覆盖 → 包内 `Configs/comfyui/`」两级里找；同名的 `.bindings.json` 会被自动发现，不需要单独指定。

> 改地址的输入框**立刻生效**（会重建服务实例），不用重开窗口。

---

## 5. 姿势参考库（决定一致性的那一步）

**当前 keep_pose 逐帧路线的姿势参考库**放在：

```
<你的 Unity 工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png
例：pose_refs/walk/left/k1.png … k8.png
```

- 一个目录 = 一组帧序列，**文件名排序即播放顺序**（k1…k8 这类命名天然正确）。
- 每帧一张「照片级姿势参考」：视频抽帧图实测可用；骨架 stick figure 未经 SAM 验证，不建议。
- 枚举规则是**严格两层** `<动作>/<朝向>/`——少一层不收，防止杂目录混进来静默出错。

> 是**工程根**（`Assets` 的同级目录），不是这个仓库。插件包和 Unity 工程是两棵目录树，
> 放错地方不会报错，只会表现为「第 2 步矩阵全是 `—`」。

**keep_pose 的姿势从哪来**：节点 507 收到姿势图后，VNCCS 走 **SAM 3D Body 实时估计**，
经前端同步把姿势应用到 3D 人偶再渲染捕获——所以 **ComfyUI Desktop 窗口必须在线**，
无头后端跑不了（报 `Pose Studio frontend sync failed`）。提示词由前端按当帧姿势实时生成，
插件**不写提示词**（bindings 里 `auto_prompt: true`，没有 positive 键——这是有意的）。

**旧 SD1.5 + ControlNet 路线**用的是逐帧姿势骨架：

```
<你的 Unity 工程根>/CharForge/pose_templates/{direction}_{action}_{index:00}.png
例：south_idle_00.png  south_walk_02.png  north_idle_01.png
```

命名约定、怎么用 Mixamo 自制、像素风该改用剪影——见同级的
`Configs/pose_templates/README.md`，那里写得很细，先读它。

**为什么必须固定 seed**：旧路线的每一帧都是**一次独立的 `/prompt` 调用**。
固定 seed 后，同一句提示词 + 同一张骨架图 = 同一套身体比例与配色，
差异只剩姿势——这正是我们要的。反之如果 seed 每帧乱跳，
你会得到一堆不同的人，这跟「一致性」是相反的。
（Spritely 路线一次出六帧、共用一个 seed，这个问题被结构性消掉了。）

---

## 6. 失败了看这里

### 当前路线（Qwen 立绘 / keep_pose 逐帧）

| 现象 | 原因 | 处理 |
|---|---|---|
| 探测报**连不上** | ComfyUI 没启动，或地址填错 | **Desktop 版监听 8000**，通用教程里的 8188 在 Desktop 上是空的 |
| 提交时 HTTP 400 / `node type not found` | 缺自定义节点 | keep_pose 需要 **vnccs-utils**（VNCCS_PoseStudio）；立绘路线只用核心节点 |
| `Value not in list: unet_name / clip_name / vae_name` | 模型文件名与 JSON 里不一致 | 改成你 `models/` 下的实际文件名 |
| `Pose Studio frontend sync failed` / 同步超时 | **ComfyUI Desktop 前端不在线**，或当前打开的不是这张 Pose Studio 图 | keep_pose 的姿势估计要经前端同步渲染：保持 Desktop 窗口打开并加载该工作流，再跑 |
| 姿势全废、每帧都是站立 | 姿势图没被 SAM 吃进去，或节点 484 的 prompt 被写成了字面量 | 确认姿势图是照片级参考；**不要覆写节点 484 的 prompt**（那是前端链接，bindings 已用 `auto_prompt: true` 断开插件写入） |
| 帧背景带紫雾 / alpha 包围盒撑满全画布 | keep_pose 输出背景有半透明噪点（实测约 44% 像素 alpha 1-63） | 插件收割时已做 alpha<64 压平；若仍有残留，检查帧的 alpha 体检日志 |
| `Value not in list: lora_name` | VNCCS_QI2_PoseStudioV1.1 未下载 | 放到 `models/loras/`，核对文件名 |
| 提交后得到一个**含糊的报错**、像什么都没发生 | 用了官方 Qwen 模板，它把整条生成链包在一个 **subgraph** 里，而 subgraph 不能直接提交给 `/prompt` | 用本目录已展开成扁平 API 格式的 JSON |
| 出图与提示词**毫不相干** | 提示词没写进节点 | `TextEncodeQwenImage21` 的字段叫 `prompt`/`negative_prompt`（**正负在同一节点上**）。写进不存在的键不会报错、只会静默丢弃。在 `.bindings.json` 里用 `positive_input` / `negative_input` 显式指定 |
| 出图**带白底、不透明** | 该路线不产 alpha | keep_pose 原生透明；立绘路线靠插件本地**四边连通域**去白底兜底 |
| 提交后一直超时 | 显存不足在换页，或队列里排了别的任务 | 看 ComfyUI 终端的进度条；30-60 秒出一帧是正常值 |

<details>
<summary>旧 SD1.5 路线（已无窗口入口，留作参考）</summary>

| 现象 | 原因 | 处理 |
|---|---|---|
| 探测报「3 秒内连不上」 | ComfyUI 没启动，或端口不是 8188 | 先 `python main.py`；改过端口就在插件里同步改地址 |
| `control_net_name` 报 `Value not in list` | ControlNet 模型没放对位置/没下载 | 放到 `ComfyUI/models/controlnet/`，核对文件名 |
| 出图是**四个不像的人** | 没接 ControlNet，或 seed 每帧在变 | 用带 ControlNet 的工作流；确认插件里 seed 是固定的 |
| 出图**全身都在但背景是白/灰**，不是透明 | SD 不产 alpha，`transparent background` 只是提示词 | 让提示词写 `solid magenta background`，再由插件本地抠图；或用 ComfyUI 的 rembg 节点 |

</details>

---

## 7. 往后

- **风格锁定**：先用**参考图 + IP-Adapter**（画风母版走 `charforge_qwen21_img2img.workflow.json` 的图生图路更省事），
  **不要一上来训 LoRA**。顺序错了会花一周训出一个「自己都不确定哪版更好」的 LoRA。
- **帧动画扩展**：骨架图库是纯文件系统的四层目录（见第 5 节），
  自备 `持械/相机/动作/朝向` 就往里丢，窗口里会自动多出对应格子 —— 不用改代码。
- **LoRA**：只有在你能明确说出「现在差在哪」之后才值得训。
