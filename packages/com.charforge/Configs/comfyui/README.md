# 接本地 ComfyUI（W2）

这份目录是**给 CharForge 用的 ComfyUI 工作流**，不是通用配置。导入即可跑，
不用自己在 ComfyUI 界面里从零搭图、也不用研究 API 格式怎么导出。

- `charforge_sd15_txt2img.workflow.json` —— **第一步先跑这个**。只有核心节点，零额外依赖。
- `charforge_sd15_controlnet.workflow.json` —— **第二步**。加了 ControlNet OpenPose，
  姿势由骨架图锁定，这是四向一致性的真正来源。核心节点，同样不需要装自定义节点。
- `charforge_qwen21_txt2img.workflow.json` —— **另一条路线**：Qwen-Image-2.1，原生 RGBA 透明通道、
  直接吃中文提示词。**但它现在补不上"四向一致性"**（ControlNet 尚未并入稳定版），
  所以定位是并行路线而非替代。上下车之前请先读 **`qwen_image_21.md`**。
- `*.bindings.json` —— 告诉插件「哪个节点是正向提示词、哪个是种子」。**必须和工作流同名同目录**，
  插件按 `<工作流文件名>.bindings.json` 去找。

---

## 1. 你的 4070 Ti Super（16 GB）能做到什么

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

## 2. 模型放哪（一次性）

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

1. 把 `charforge_sd15_txt2img.workflow.json` 里的
   `"PUT_YOUR_SD15_CHECKPOINT_HERE.safetensors"` 改成你的实际文件名
   （或导入后在界面上点一下节点改）。
2. 打开 ComfyUI 界面 → 把 JSON **拖进画布**即可加载。
3. 先点一次 **Queue Prompt**，确认能出图。**这一步必须在 ComfyUI 界面里手工验证通过**，
   否则插件那边只会给你一个含糊的超时错误。

`ControlNet` 版本还需额外把 `control_net_name` 改成你实际的文件名，
并确认 `LoadImage` 节点里那张 `south_idle_00.png` 已经存在于 `ComfyUI/input/`
（插件会在生成时自动上传姿势图，见第 5 节；手工验证时可以先随便放一张）。

---

## 4. 在插件里配

`Window > CharForge 角色资产生成` → 「④ 后端设置」：

| 项 | 值 |
|---|---|
| 后端 | 本地 ComfyUI |
| 地址 | `http://127.0.0.1:8188` |
| 工作流 JSON | 指向本目录下的 `.workflow.json` 绝对路径 |

点「探测后端」，应显示 `ComfyUI 就绪`。绑定文件会被自动发现，不需要单独指定。

---

## 5. 四向姿势模板（决定一致性的那一步）

工作流里 `LoadImage` 节点读的是**每一帧的姿势骨架图**，插件会按下面这个约定去找并自动上传：

```
<你的 Unity 工程根>/CharForge/pose_templates/{direction}_{action}_{index:00}.png
例：south_idle_00.png  south_walk_02.png  north_idle_01.png
```

> 是**工程根**（`Assets` 的同级目录），不是这个仓库。插件包和 Unity 工程是两棵目录树，
> 放错地方不会报错，只会表现为「姿势没锁住」。也支持放在插件包的
> `Configs/pose_templates/` 下作为全局默认。

命名约定、怎么用 Mixamo 自制、像素风该改用剪影——见同级的
`Configs/pose_templates/README.md`，那里写得很细，先读它。

**为什么必须固定 seed**：插件的每帧都是**一次独立的 `/prompt` 调用**。
固定 seed 后，同一句提示词 + 同一张骨架图 = 同一套身体比例与配色，
差异只剩姿势——这正是我们要的。反之如果 seed 每帧乱跳，
你会得到 16 个不同的人，这跟「四向一致性」是相反的。

---

## 6. 失败了看这里

| 现象 | 原因 | 处理 |
|---|---|---|
| 探测报「3 秒内连不上」 | ComfyUI 没启动，或端口不是 8188 | 先 `python main.py`；改过端口就在插件里同步改地址 |
| 提交时 HTTP 400 / `node type not found` | 工作流里有未安装的自定义节点 | 本方案的两个工作流只用核心节点；若你改过图，删掉或装上对应节点 |
| 提交时报 `Value not in list: ckpt_name` | 模型文件名与 JSON 里不一致 | 改成 `ComfyUI/models/checkpoints/` 下的实际文件名 |
| `control_net_name` 报同样的错 | ControlNet 模型没放对位置/没下载 | 放到 `ComfyUI/models/controlnet/`，核对文件名 |
| 报 `prompt_id` 后一直超时 | 显存不足在换页，或队列里排了别的任务 | 看 ComfyUI 终端的进度；单帧超时默认 240 秒，SDXL 可以适当加大 |
| 出图是**四个不像的人** | 没接 ControlNet，或 seed 每帧在变 | 用带 ControlNet 的工作流；确认插件里 seed 是固定的 |
| 出图**全身都在但背景是白/灰**，不是透明 | SD 不产 alpha，`transparent background` 只是提示词 | 让提示词写 `solid magenta background`，再由插件本地抠图；或用 ComfyUI 的 rembg 节点 |

---

## 7. 往后（W2 后半段再做）

- **IP-Adapter**：把 3–5 张参考图的画风真正锁住。需要在工作流里加
  `IPAdapterUnifiedLoader` + `IPAdapterAdvanced`，这两个来自
  `ComfyUI_IPAdapter_plus`（**这次要装自定义节点了**）。
- **SDXL 升级**：`FrameRequest` 尺寸改成 1024，或工作流里用 1024 出图
  再让插件降到 512——后者质量更好，`FrameNormalizer` 的归一化会处理尺寸差异。
- **LoRA**：只有在前两条都稳定、且你能明确说出「现在差在哪」之后才值得训。
