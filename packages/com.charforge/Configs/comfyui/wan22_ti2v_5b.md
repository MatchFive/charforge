# Wan2.2 TI2V-5B 视频抽帧工作流（charforge_wan22_ti2v_video）

> 用途：母版立绘 → i2v 行走视频 → 抽帧 → 抠图 → sprite sheet。
> 定位：帧动画的"快速出片"路线，与插件主链（Spritely 骨架图 → 六帧）并行，二者互不替代；
> 侧面行走视频的中间帧可回收当立绘姿态参考。（骨骼路线已于 v0.4.0 移除，见 CHANGELOG。）

## 一、模型三件套（官方 Repackaged 包）

| 文件 | 大小 | 目录 |
|---|---|---|
| `wan2.2_ti2v_5B_fp16.safetensors` | 9.31 GB | `models/diffusion_models/` |
| `umt5_xxl_fp8_e4m3fn_scaled.safetensors` | 6.27 GB | `models/text_encoders/` |
| `wan2.2_vae.safetensors` | 1.31 GB | `models/vae/` |

下载源：HuggingFace / ModelScope `Comfy-Org/Wan_2.2_ComfyUI_Repackaged`（见官方件 MarkdownNote）。

**VAE 注意**：官方模板用的就是 `wan2.2_vae`（不要再找 2.1 的 VAE，
社区文章的「2.2 必须配 2.1 VAE」说法与官方模板矛盾，以官方为准）。

## 二、与官方件的对应关系（已逐节点核实）

官方 `video_wan2_2_5B_ti2v.json`（UI 格式）→ 本扁平 API 版（13 节点）：

| 官方节点 | 作用 | 本版节点 |
|---|---|---|
| 37 UNETLoader | 主模型 | 1 |
| 38 CLIPLoader（type=wan） | 文本编码器 | 2 |
| 39 VAELoader | VAE | 3 |
| 48 ModelSamplingSD3（shift=8） | 采样偏移 | 4 |
| 6/7 CLIPTextEncode | 正/负提示词 | 5 / 6 |
| 56 LoadImage（官方 bypass，i2v 需启用） | 首帧图 | 7（常驻启用） |
| 55 Wan22ImageToVideoLatent（1280×704×121） | 首帧→视频 latent | 8（704×704×121，正方形适配 sprite） |
| 3 KSampler（20步 / CFG5 / uni_pc / simple） | 采样 | 9 |
| 8 VAEDecode | 解码 | 10 |
| 57 CreateVideo + 58 SaveVideo | mp4 预览 | 12 / 13 |
| —（官方无） | **PNG 帧序列落盘** | 11 SaveImage（新增） |

节点 11 是给流水线加的：SaveImage 对批量 IMAGE 会逐帧存
`output/CharForge/video_frames_00001_.png …`，插件直接取帧做抠图，
**不需要 ffmpeg 解 mp4**；节点 12/13 的 mp4 仅作人工预览。

## 三、关键参数与坑

- **分辨率**：宽高必须是 16 的倍数。704×704（44×16）是 sprite 友好的正方形；
  显存吃紧降到 640×640 / 512×512，4070 Ti Super 16GB 跑 704 富余。
- **时长**：121 帧 @24fps = 5s，保证至少两个完整步态周期；
  后续抽帧脚本按自相关截取一个周期做无缝循环，不依赖首尾帧模型。
- **首帧图**：节点 7 的 `image` 指向 ComfyUI `input/` 里的母版文件名
  （默认 `master.png`，手动上传/改名对齐；与 Qwen i2i 的母版约定一致）。
- **提示词**：正向模板已锁「纯色背景、镜头固定、背景静止、人物居中」——
  这是抽帧抠图成败的关键；负向在官方中文负向表基础上追加了
  阴影/镜头移动/背景移动/人物移出画面。
- **种子**：bindings 已绑 seed（固定值），批量出片可复现。
- bindings 不含 `load_image`：本工作流母版按文件名引用（插件不参与上传），
  姿势骨架注入对视频路线不适用。

## 四、使用方式

1. **手动试水（推荐先做）**：ComfyUI 里直接加载官方件（或拖入本 API 格式件），
   启用 LoadImage 分组（Ctrl+B），把 start_image 换成母版，跑一条侧面行走看效果；
2. **流水线集成**：插件按本文件 + bindings 提交，取 `CharForge/video_frames_*.png`；
   抽帧选周期 → rembg 抠图 → 归一拼图集——这部分属新后端逻辑，待实现。
