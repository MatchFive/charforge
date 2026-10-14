# 换装 Qwen-Image-Edit-2509 + Spritely 帧动画 LoRA

> 决策记录。配套阅读：`packages/com.charforge/Configs/comfyui/qwen_image_21.md`（现役 Qwen-2.1 路线的完整记录）。
>
> **一句话**：用 2509 换掉 2.1，一次拿到「现成的 8 方向 sprite sheet LoRA」+「可商用许可」两件东西，代价是多下一个 20B 的量化底座。

---

## 一、为什么换

| 理由 | 说明 |
|---|---|
| **有现成的帧动画 LoRA** | Spritely Pose/Sheet v5 就是训练来干这个的：骨架图 + 角色 → sprite sheet，**8 方向**，side-scroller / isometric / top-down 三种相机。它只认 Qwen-Image-Edit-2509。 |
| **许可从「仅研究」变「可商用」** | Qwen-Image-2.1 是 **Qwen Research License**（FOR NON-COMMERCIAL PURPOSES ONLY，商用需单独授权）；Qwen 早期那一支（Qwen-Image / 2512 / **Edit-2511**）是 **Apache 2.0**。文博机构部署会审这一项。2509 的 license 字段**下单前请在 HF 模型页再确认一次**。 |
| **绕开本机空的 controlnet 目录** | 2509 **原生识别 OpenPose 骨架图**（把骨架图当输入图之一即可，模型自带姿态检测），不需要 ControlNet 模型。本机 `F:\model\controlnet` 是空的——这条正好把坑填了。 |
| **一次出 6 帧** | Sheet v5 输出 1536×1024 的 3×2 网格，**一次生成 = 6 帧**。当时并行的「逐件拆分」要 16 次生成——那条路线已于 v0.4.0 移除。 |

最终分工（v0.4.0 定型）：**2.1 出立绘**（第 1 步，原生 64 通道 RGBA、中文直入），
**2509 + Spritely 出帧动画**（第 2 步，一次 6 帧、骨架图锁姿势）。两条底座并存，各管一段。

---

## 二、本机现状（已实测）

| 项 | 状态 |
|---|---|
| ComfyUI | Desktop 版（`F:\ComfyUI`），模型根 **`F:\model`**（93 GB），base_path `C:\Users\32113\Documents\ComfyUI` |
| 图像底座 | **只有** Qwen-Image-2.1（`qwen_image_2.1_int8_convrot` + `Qwen-Image-2.1-Turbo-INT8-ConvRot`） |
| 视频底座 | Wan2.2 TI2V 5B |
| LoRA | 仅 `qwen_image_2.1_turbo_lora_avg_rank_178_bf16`（加速用，**未接进任何工作流**） |
| **checkpoints / unet / controlnet / clip_vision** | **全为空** → 现役 SD1.5 + ControlNet 路线无权重可跑 |
| 自定义节点 | 仅 `ComfyUI-QwenVL`（+ 内置 Easy-Use / KJNodes / RMBG）；**没有 ComfyUI-GGUF**；Manager 已装 |
| 磁盘 | F 盘剩 **621 GB** |
| 网络 | Bash 无外网（代理 CONNECT 502）→ **下载需在浏览器里手动完成** |

---

## 三、下载清单

**落地根目录：`F:\model\`**

| # | 文件 | 大小 | 放到 | 来源 |
|---|---|---|---|---|
| 1 | `Qwen-Image-Edit-2509-Q5_K_M.gguf` | 14.9 GB | `diffusion_models\` | [QuantStack/Qwen-Image-Edit-2509-GGUF](https://huggingface.co/QuantStack/Qwen-Image-Edit-2509-GGUF) |
| 2 | `qwen_2.5_vl_7b_fp8_scaled.safetensors` | ≈9 GB | `text_encoders\` | [Comfy-Org/Qwen-Image_ComfyUI](https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors) |
| 3 | `qwen_image_vae.safetensors` | ≈250 MB | `vae\` | [同仓库](https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/vae/qwen_image_vae.safetensors) |
| 4 | `Qwen-Image-Lightning-4steps-V2.0.safetensors` | ≈1.5 GB | `loras\` | [lightx2v/Qwen-Image-Lightning](https://huggingface.co/lightx2v/Qwen-Image-Lightning/resolve/main/Qwen-Image-Lightning-4steps-V2.0.safetensors) |
| 5 | `spritely_pose_v5.safetensors` | 562.81 MB | `loras\` | **Civitai 2982439 · Pose v5 版**（见 §三·补） |
| 6 | `spritely_sheet_v5.safetensors` | ≈562 MB | `loras\` | **Civitai 2982439 · Sheet v5 版**（同上，**须切版本再下**） |
| 7 | `spritely-free-workflows.zip` | 7.95 MB | 解压留用 | Civitai 2982439 · **Optional Files** |
| 8 | `spritely-starter-charts.zip` | 24.55 MB | 解压留用（**846 张现成骨架图 + 对应 prompt**） | Civitai 2982439 · **Optional Files** |

**节点**：Manager 搜 `GGUF` 装 [city96/ComfyUI-GGUF](https://github.com/city96/ComfyUI-GGUF)（**仅第 1 项选 GGUF 档时需要**；选下方 int8_convrot 方案则不需要）。

### 三·补2、底座改选：2509 的官方 int8_convrot（2026-10-12 二次核实，✅ 已采纳为首选）

ModelScope 上 Comfy-Org 官方仓库的 `split_files/diffusion_models/` 里有 **`qwen_image_edit_2509_int8_convrot.safetensors`（20.50 GB）**——与现役 2.1 底座（`qwen_image_2.1_int8_convrot`）**同一量化家族、同一加载路径**。已核实的事实：

| 项 | 结论 |
|---|---|
| 格式支持 | Comfy-Org 官方 `.comfy_quant` 格式（int8_tensorwise + convrot group 256，**逐行 row-wise scale**）。要求 ComfyUI 支持 `int8_tensorwise`——本机已在跑 2.1 的 convrot 底座，**支持性已实测具备**，无需装任何新节点 |
| LoRA 兼容 | 逐行 scale 正是社区记载的「**不破坏 LoRA 兼容**」的量化配方（tensor-wise scale 才会废掉 LoRA 且画面发软）；Comfy-Org 自家 2511 的 convrot 构建同为逐行 layout。**但本机尚未实际挂过 LoRA**，第一次跑 Sheet v5 时即为验证点 |
| 画质 | convrot（块 Hadamard 旋转）比普通 int8 损失小，社区实测接近原版，不劣于 GGUF Q5_K_M |
| 显存 | 文件 20.5 GB ≈ bf16（40.9 GB）的一半，但 **> 16 GB 显存** → 采样时部分层自动驻留系统内存（ComfyUI offload），速度比 Q5_K_M 全驻慢；配 Lightning 4 步可接受。**系统内存建议 ≥ 32 GB** |
| 网络 | ModelScope **国内直连**，不需要代理——比 HF/Civitai 省一整层折腾。同仓库 `text_encoders/`、`vae/` 目录可一并取配套文件 |

**决策**：底座首选 **2509 int8_convrot（20.5 GB，本表第 1 项替换）**；若 OOM 或速度不可接受，再退 GGUF Q4_K_M/Q5_K_M（此时才装 ComfyUI-GGUF）。同页另有 `qwen_image_edit_2511_int8_convrot`（2511 更新、Apache 2.0 已确认），但 **Spritely 是按 2509 训的**——先按 2509 走，2511 留作后续实验。

Lightning 加速 LoRA 同样有 ModelScope 镜像（lightx2v/Qwen-Image-Lightning），不用去 HF。

### 三·补3、本机能不能跑 20.5 GB 的底座（2026-10-12 实测，结论：能）

数据来源：`C:\Users\32113\Documents\ComfyUI\user\comfyui_8000.log`（2026-10-12 14:26 启动）。

**机器规格（实测）**

| 项 | 数值 | 判读 |
|---|---|---|
| 显存 | **16376 MB**（RTX 4070 Ti SUPER） | 不足以全驻 20.5 GB DiT |
| 系统内存 | **65322 MB（约 64 GB）** | **这是能跑的关键**——权重可分页到 RAM |
| 显存管理状态 | `NORMAL_VRAM` + `Using async weight offloading with 2 streams` | 异步权重卸载已启用 |
| 锁页内存 | `Enabled pinned memory 26128 MB` | 26 GB 锁页池，流式搬运的通道 |
| ComfyUI | Desktop · Python 3.12.13 · PyTorch **2.10.0+cu130** | 版本足够新 |
| 原生量化算子 | `Native ops: …, int8_tensorwise, …`；cuda 后端含 `dequantize_int8_convrot_weight` | **int8_convrot 原生支持，无需任何补丁/自定义节点** |
| 同族格式实跑记录 | 日志中已加载 `qwen_image_2.1_int8_convrot` + `qwen3vl_8b_int8_convrot` | 该量化格式本机**已跑通**，不是纸面推断 |
| 磁盘 | F 盘剩 **621 GB** | 充裕 |

**内存账**：2509 int8 DiT 20.5 GB + Qwen2.5-VL 编码器 ≈9 GB + VAE 0.25 GB ≈ **30 GB 权重**；16 GB 显存 + 26 GB 锁页内存 = 42 GB 快速存储，**放得下**，代价是每步从 RAM 流一部分权重进显存。

**速度基线（同机实测，现役 Qwen-2.1）**：单次任务 `Prompt executed in` = 17.9 s / 18.3 s / 35.3 s / 38.6 s / 115 s（最久那次是三视图 + 提示词增强 + 25 步）。
**换 2509 的推算**：DiT 体积是 2.1 的 3 倍（20.5 vs 6.8）且需部分流式 → 同步数约慢 2–3 倍；但 Lightning 4 步替掉 25 步省约 6 倍。**净结果：单张 Sheet v5（1536×1024、4 步）预计 40–90 秒量级；首张另加权重首次读盘加载，约 1–3 分钟。** 属估算，第一张跑完日志里的真实值回来即修正。

**顺带挖出的一处旧账**：日志 `15:02:13` 有
`ERROR UNSUPPORTED DIFFUSION MODEL F:\model\diffusion_models\Qwen-Image-2.1-Turbo-INT8-ConvRot.safetensors` → `Could not detect model type of`。
即本机那个 **Turbo 权重当前 ComfyUI 根本加载不了**（缺少可识别的模型类型元数据）。此前「接上 turbo 把 25 步降到 4-8 步」的建议**作废**——加速只走 **Lightning LoRA** 这一条路。它躺在盘上不是没接，是接不上。

---

### 三·补4、Nunchaku 路线评估（svdq-int4-r128 + Lightning 融合版，2026-10-12 核实）

用户在 ModelScope 看中 `nunchaku-tech/nunchaku-qwen-image-edit-2509` 的 **`svdq-int4-r128-qwen-image-edit-2509-lightningv2.0-8steps.safetensors`（12.65 GB）**。

**它是什么**：MIT-HAN-Lab 的 SVDQuant——INT4 量化 + 秩 128 低秩分支；**Lightning-8steps-V2.0 蒸馏 LoRA 已按强度 1.0 融合进权重**。INT4 版适用于 50 系之前的卡（4070 Ti SUPER ✓）；NVFP4 版才是 50 系专属。

**「压力更小」——成立**：

| 对比项 | int8_convrot（现首选） | Nunchaku INT4 r128 融合版 |
|---|---|---|
| 文件/运行驻留 | 20.5 GB，**超显存，需部分流式** | **12.65 GB，全驻 16 GB 显存** |
| 速度 | 流式搬运打折 | **最快**（INT4 融合核，无流式） |
| Lightning | 另挂 LoRA | **已融合，8 步即用** |
| 加载配套 | 零补丁、零节点 | 见下方「三道关」 |

**「能不能配 Spritely」——技术上可以，但要过三道关，且有一类静默失效是它的高发区**：

1. **关一：安装链路全是社区件**。需要 ① nunchaku 后端 wheel（必须精确匹配本机 Python 3.12 + torch 2.10 + cu130；社区确认存在 `nunchaku-1.3.0.dev…cu13.0torch2.10-cp312-win_amd64.whl`，但只能从 GitHub Releases 下载）② ComfyUI-nunchaku 节点 ③ **Qwen 的 LoRA Loader 官方长期没做**，需第三方 `ComfyUI-QwenImageLoraLoader`（ussoewwin/GuardSkill 系），部分版本还要**手改官方插件的 `__init__.py`**。
2. **关二：LoRA 静默无效的高发区**。该 LoRA 机制是把外部 LoRA 并进 r128 低秩分支（Lightning 已占掉一部分秩预算）；文档明确记载 **LoKr / LoHa / IA³ 格式会被静默跳过**——挂上去毫无效果且不报错。Spritely 是标准 LoRA（562 MB，疑似 rank ~32）大概率没事，但「大概率」在本项目不算数。
3. **关三：采样档位没对齐**。融合版锁定 **8 步蒸馏**；Spritely 官方只验证过「Lightning 4 步 / CFG 1」与「25 步 / CFG 4」两档。8 步档上 Spritely 的姿势遵循度是**三重未验证组合**（Spritely × SVDQuant-INT4 × 8 步蒸馏）。
4. 附带：加载时吃系统内存大（16 GB 内存机器会崩；本机 64 GB 无碍）；个别环境需 `--disable-dynamic-vram` 启动参数。

**决策**：**不作为首选，列为第二阶段提速实验**。理由：验证 Spritely 有效性应走**变量最少**的路径——int8_convrot 原生加载的 LoRA 语义最标准。等 Spritely 在标准路径上确认生效后，再用**同一张骨架图 + 同一 prompt + 同一 seed** 在 Nunchaku 上复跑对比；三样都对得上（按骨架变形、六帧同角色、画质不塌）才考虑切换。一次只改一个变量——这也是排查「LoRA 静默失效」唯一可靠的办法。

若坚持现在就上 Nunchaku，验收点不变：骨架换了姿势而画面没跟着变 = LoRA 静默失效，当场抓。

---

### 三·补5、Nunchaku 为什么装不上：根因已定位（2026-10-12 20:19 实测）

**一句话：装的是一支 CUDA 12 的 wheel，本机是 CUDA 13。它永远加载不了，不是配置问题、不是缺节点。**

日志现象：`ComfyUI-nunchaku` 全部节点 import 失败，`nunchaku 1.0.0+torch2.9` 已装、`_C.cp312-win_amd64.pyd`（290 MB）在位：

```
from ..._C.ops import gemm_awq, gemv_awq
ImportError: DLL load failed while importing _C: 找不到指定的模块。
```

直接解析 `_C.cp312-win_amd64.pyd` 的 PE 导入表，它索要的 CUDA 运行库是：

| `_C.pyd` 依赖 | 本机情况 | 判定 |
|---|---|---|
| **`cudart64_12.dll`** | 只有 `cudart64_13.dll`（torch 2.9.1+**cu130** 自带） | **缺失 → 就是这一条** |
| `c10.dll` / `c10_cuda.dll` / `torch_cpu.dll` / `torch_python.dll` | `torch/lib` 下齐全 | OK |
| `MSVCP140.dll` / `VCRUNTIME140*.dll` | System32 齐全 | OK |

全盘（`C:\Windows\System32`、`torch/lib`、CUDA Toolkit 目录）**只有 cudart64_13.dll，没有 12**；本机也没装 CUDA Toolkit。所以这支 wheel 缺的那个 DLL **无处可寻**，补 DLL 也补不上——只能换 wheel。

**两个必须同时满足的版本轴**（补4 只看到了第一条）：

| 轴 | 要求 | 现状 |
|---|---|---|
| CUDA 大版本 | wheel 的 cu 版本须 = torch 的 cu 版本 | 装的是 **cu12**，环境是 **cu130** ✗ |
| torch 小版本 | 须一致 | 装的 `+torch2.9` 与本机 2.9.1 一致 ✓ |

补4 记下的那支 `nunchaku-1.3.0.dev…**cu13.0torch2.10**-cp312-win_amd64.whl` 是 **cu13 ✓ 但 torch2.10 ✗**（本机 2.9.1）。
→ 要么找 **cu13.0 + torch2.9** 的构建，要么把 torch 升到 2.10 再上那一支。**两支必须成套，单改一头无效。**

**顺带排掉两处误报**

- `[WARNING] [ROCm/AMD] nunchaku is not available on this system` —— 这台是 NVIDIA 卡。该句来自 `ComfyUI-QwenImageLoraLoader`，它的检测只等于"nunchaku 导不进来"，与 AMD 无关，**分支标签是错的，别看这句**。
- 节点面板里能搜到 `NunchakuWheelInstaller` / `NunchakuDepthPreprocessor` / `NunchakuQwenImageDiffsynthControlnet`，是因为 `nunchaku_versions.json` 缺失使插件退回 **minimal mode**，只注册了少数不依赖 `_C` 的节点；DiT / LoRA / 编码器加载节点全部缺失。**"面板里看得到 Nunchaku" 不等于 nunchaku 可用。**

**对现役路线零影响（已用运行中实例的 `/object_info` 核对）**：本机 989 个节点类型中，7 份 CharForge 工作流引用到的 20 种 `class_type` **全部在位**，含 Qwen-Image-2.1 那一路的 `TextEncodeQwenImage21` / `ResolutionSelector` / `SaveImageAdvanced` / `QwenImage21Cache`。**没有任何一份工作流用到 nunchaku 节点**——它不是断在链路上，是装备躺在旁边还没装上。

**今天（10-12）新下的两个 svdq 权重当前都不可用**

| 文件 | 判定 |
|---|---|
| `svdq-int4_r32-qwen-image-edit-2509-lightningv2.0-4steps.safetensors`（11.5 GB） | ~~依赖 nunchaku，当前加载不了~~ **已可用**（见补6），实测 15.28 秒/张 |
| `svdq-fp4_r128-qwen-image-edit-2509-lightningv2.0-4steps.safetensors`（13.08 GB） | **fp4 = NVFP4，5 系（Blackwell）专属，Ada 确定用不了——已实锤**：nunchaku 修好后仍被 `check_hardware_compatibility` 当场拒绝（`ValueError: Please use "int4" quantization for Turing, Ampere and Ada GPUs`）。这文件在本机永远加载不了 |

另：补4 提到的目标件是 `svdq-int4-**r128**-…-**8steps**`，实际落盘的是 **r32 / 4steps**——秩与蒸馏步数都不是同一支。走 Nunchaku 之前请先把支号对齐（这属于"变量没对齐就先跑对比"的禁忌区）。

---

### 三·补6、Nunchaku 已装好并实测出图（2026-10-12 21:10，补5 的根因已解决）

**方案**：卸掉 cu12 的 `nunchaku 1.0.0+torch2.9`，换装 **`nunchaku-1.2.1+cu13.0torch2.9-cp312-cp312-win_amd64.whl`**（107 MB，sha256 `81cb888f…04eeb` 与官方一致，已存 `F:\model\wheels\`）。torch 保持 2.9.1+cu130 不动，路线 B（2.1 int8_convrot）不受影响。

| 项 | 结果 |
|---|---|
| wheel 来源 | GitHub Releases v1.2.1，经 **`gh-proxy.com`** 代理下载（本机直连 GitHub 不通）。**ModelScope 官方 wheel 仓 `nunchaku-tech/nunchaku` 最新只到 1.0.2，无 cu13 构建**——网上说的"去 ModelScope 下 nunchaku 轮子"对 cu130 环境不成立 |
| 插件 | ComfyUI-nunchaku 1.0.1 → **1.2.1**（与后端同版本配套；旧版备份在实例根 `backup-custom-nodes/ComfyUI-nunchaku-v1.0.1-bak`，未删） |
| 启动日志 | **0 ERROR / 0 Traceback**（此前 nunchaku 五组 import 全挂）；23 个 Nunchaku 节点注册（此前仅 minimal mode 的 3 个），含 `NunchakuQwenImageDiTLoader` / `NunchakuTextEncoderLoaderV2` / Qwen LoRA 栈 |
| 冒烟实测 | svdq-int4_r32-…-lightningv2.0-**4steps** + `qwen_2.5_vl_7b_fp8_scaled` + `qwen_image_vae`，1024×1024，**4 步 / cfg 1 / shift 3 / cpu_offload enable / num_blocks_on_gpu 20**（照抄官方 lightning 示例）→ **成功出图，`Prompt executed in 15.28 s`（含首次读盘）** |
| 中文考据 | 提示词「南宋龙泉窑青釉梅瓶，粉青釉，开片自然」直接进 Qwen2.5-VL，出图器形/釉色/开片语义全部正确（见 `output/charforge/nunchaku_smoke_00001_.png`） |

**速度结论更新**：Nunchaku INT4 融合版单张 **~15 s**，比 int8_convrot 估算（40–90 s）快一个量级——补4 说的"第二阶段提速实验"技术上已就绪。但 **Spritely × SVDQuant-INT4 × 4 步蒸馏的姿势遵循度仍未验证**，切换前仍按补4 的单变量纪律：先在 int8_convrot 原生路径确认 Spritely 生效，再同骨架图+同 prompt+同 seed 复跑对比。

冒烟工作流（API 格式，`/prompt` 可直接提交）：`docs/qwen-edit-2509-nunchaku-smoke.api.json`。

**遗留清单**

- `svdq-fp4_r128-…-4steps.safetensors`（13.08 GB）是 **5 系专属，本机（Ada）已被 nunchaku 硬性拒绝**（见上表实锤），留着只会被误选后报错。处置方式待定：删除 / 移出模型目录 / 保留。
- 插件示例工作流是 **UI 格式，`/prompt` 不收**（与 subgraph 同坑）；接插件时必须转扁平 API 格式——转换器思路已验证（见冒烟工作流）。
- `AILab_QwenVL_GGUF_PromptEnhancer` 因缺 `llama_cpp` 仍不可用——本仓库工作流不含 PE，不需要它，留着不管。
- 实例 "ComfyUI"（无 venv 的空壳）建议在 Desktop 设置里移除；模型路径两实例都指向 `F:\model`，无分叉。
- 服务当前**由 Comfy Desktop 自己管理**（进程链 `Comfy Desktop.exe` → venv python → `uv` python 监听 8000）。
  期间我手动拉起过一次，Desktop 于 21:09 自行接管——**无需人工交还**。（原文写的"Desktop 不会自动拉起"是错的。）

---

### 三·补、Spritely 到底去哪下（2026-10-12 核实）

**唯一官方源：Civitai 模型页**

- 页面：**https://civitai.com/models/2982439** —《Posable Game Sprites - Spritely Pose v5 for Qwen》，作者 **SpritelyStudio**（2026-10-04 发布，10-08 更新）。
- **页码结构上的坑**：Pose v5 与 Sheet v5 是**同一个模型页的两个版本（version）**，不是两个文件。**必须先在下拉里切到对应版本，再点 Download**，否则只会拿到其中一个。两个都下。
- 附件（**不随版本走**，在 Optional Files 分组里）：`spritely-free-workflows.zip`、`spritely-starter-charts.zip`。**工作流免费、必须拿**——`spritely-pose.json` / `spritely-sheet.json` 是我们后续转插件工作流的唯一依据。
- **需登录**（免费账号即可）。下载按钮 → 右键复制链接，形如 `https://civitai.com/api/download/models/<版本ID>`；附件走 `?type=Other`。
- 文件校验值（CivArchive 页给出）：`spritely_pose_v5.safetensors` SHA256 `2d1b1ef0…73ba`（完整值 `2d1b1ef0f00993293baec5f5692cf95626b1c16d43a33edd8c480025007e73ba`）。

**镜像（Civitai 打不开时）**

- **https://civarchive.com/models/2982439** — CivArchive 的同一页快照，附文件条目与 SHA256，并提供镜像下载。**下载后务必核 SHA256**（第三方镜像一致性无保证，混错版本会静默失效）。

**作者的网页版（不是本地权重）**

- **https://spritely.studio** — 这两个模型可以直接在网页上跑（免费额度每天 5 次生成），也有 3D 绑定转 sprite、Unity/Godot/Aseprite 导出与 MCP。**它不能替代本地 LoRA**：本地跑要的是 `.safetensors`，网页版只出图。
- 作者另有 YouTube 教程（walk/run/attack 八方向演示）。

**没找到的（先排除，省得你们再查一遍）**

- **HuggingFace / ModelScope 上没有官方搬运**。LoRA 许可（Spritely Model License v1）明确禁止转载、托管、转售权重文件——所以 HF 上出现同名文件基本是违规搬运，别用。
- LiblibAI 有 Qwen-Edit-2509 底座与大量 LoRA 的搬运，但**未搜到 Spritely 搬运**；要用请自行在站内搜确认。

**国内网络的实操路径**

1. Civitai 直连一般要代理；下不通就走上面的 CivArchive 镜像。
2. 更省事的一条：ComfyUI Desktop → **Manager → Install Models → Custom URL**，粘贴 `https://civitai.com/api/download/models/<版本ID>?token=<你的API Key>`（Civitai 设置页可建 Key），落盘目标直接选 `F:\model\loras`。附件（zip）走同法，但目标目录选非模型目录。
3. 下完核对：`pose ↔ 562.81 MB`、`sheet ↔ 同量级`。**两个文件大小若有量级差异，说明版本切错了。**

### VRAM 档位（16 GB 卡）

社区给的档位表（16G→Q6_K、12G→Q5_K_M、8G→Q4_K_M）是**纯 DiT 驻留口径**；实际还要塞 fp8 编码器 + LoRA，所以 16 GB 更稳的选择是：

- **首选 Q5_K_M（14.9 GB）**，跑不动或 OOM 就退 **Q4_K_M（13.1 GB）**；
- 编码器会在编码完成后被卸载（ComfyUI 的 model storage policy），这是正常行为，不是故障；
- 配 Lightning 4 步能把速度拉回来一大截（见第四节）。

---

## 四、Spritely 用法要点

- **双槽位顺序与现役相反**：`image 1 = 骨架图`，`image 2 = 角色（白底）`。
  → 恰好是现役 i2i 工作流里记过的那条「对调两槽接线」实验，现在它是**必需项**而不是可选实验。
- **触发词固定且很长**，照抄：
  `the same character as the reference, redrawn as a game character from a side-scroller camera at character height, facing right, in exactly the pose given by the skeleton chart, clean white background`
- 要看正面就在 `facing right` 后面加 `turned toward the camera`。
- 强度设 **1**。
- 采样：配 Lightning 用 **4 步 / CFG 1**；不配则 **25 步 / CFG 4**。
- 两个版本各管一段：
  - **Sheet v5** = 1536×1024，3×2 六帧（走 / 跑 / 攻击…），走动画主力；
  - **Pose v5** = 单帧 1024×1024，质量更高，适合单帧姿势或补帧。
- 许可：**Spritely Model License v1** —— 产出**可商用**，但不可转载 / 托管 / 转售 LoRA 文件本身。
  → **给法务的口径**：Civitai 页面上显示的 `Apache 2.0` 指的是**底座（Qwen）**的许可，不是 LoRA 的；两层要分开写。出图可商用，权重文件不可再分发。

---

## 五、⚠️ 与 2.1 的不通用清单（最容易静默出错的地方）

2509 与 2.1 是**两套架构**（2509 = 20B 双流 MMDiT + Qwen2.5-VL + 16 通道 VAE；2.1 = 7B 单流 DiT + Qwen3-VL 8B + **64 通道 RGBA** VAE）。配件**一律不能混用**：

| 配件 | 2509 | 2.1（现役，勿混） |
|---|---|---|
| VAE | `qwen_image_vae.safetensors`（16 通道） | `qwen_image_2.1_vae_bf16.safetensors`（64 通道 RGBA） |
| 文本编码器 | `qwen_2.5_vl_7b_fp8_scaled` | `qwen3vl_8b_*` / `qwen3.5_9b_*` |
| LoRA | 2509 系列 | `qwen_image_2.1_turbo_lora_*` |

**LoRA 不可跨代**：社区说的「Qwen-Image 与 Edit-2509 的 LoRA 互通」只在 **20B 那一支内部**成立；装到 2.1 上（或反之）不会生效——张量形状不同，且多数情况下**不报错**，只是效果不动。

> 额外提醒：如果改用 **GGUF 版文本编码器**，必须把 `Qwen2.5-VL-7B-Instruct-mmproj-BF16` 放在**同一目录**，否则视觉分支起不来、图生图会静默失效。用上面第 2 项的 fp8 safetensors 编码器则**不需要** mmproj。

---

## 六、待办

- [ ] **在 HF 模型页确认 Qwen-Image-Edit-2509 的 license 字段**（Qwen 早期线为 Apache 2.0，但 2509 未被点名确认过）——文博机构部署前必须落实。
- [ ] 按第三节下载 1–7 项，装 ComfyUI-GGUF。
- [ ] 用 Spritely 官方工作流跑通一张 Sheet v5，确认：①六帧是同一角色 ②真的按骨架姿势走。
- [ ] 跑通后把结论回填本文件，再由插件侧接手（见下）。

## 七、插件侧后续（文件到位后执行）

> **✅ 2026-10-12 已完成（v0.3.9）**，且**实跑结论推翻了两条此前的评估**：

1. ~~「Nunchaku 列为第二阶段实验」~~ → **本机实际走的就是 Nunchaku**：用户已装 `svdq-int4_r32-…-2509-lightningv2.0-4steps`（同时误下了 fp4_r128，Ada 卡用不了，留在盘上勿选），且 Spritely LoRA 挂在**标准 LoraLoaderModelOnly** 上跑通了——新版 nunchaku 兼容标准 LoRA 节点，第三方 Qwen LoRA Loader 那道关**不存在**。实跑出图：六帧同角色、按骨架走、像素风（见 CHANGELOG 0.3.9）。
2. ~~「必须按官方槽位：骨架=image1」~~ → 实跑排布是**角色=image1（槽 10）、骨架=image2（槽 11）**，两路同时进 VL 编码器与 ReferenceLatent，结果正常。插件工作流照实跑排布落盘。

**插件侧已落地（详见 CHANGELOG 0.3.9）**：

- `Configs/comfyui/charforge_2509_spritely_sheet.workflow.json` + bindings —— 逐节点照抄 history 实跑图；
- `Configs/comfyui/spritely_charts~/sheets/` —— starter 骨架图库全量入库（160 文件，~ 目录免 meta）；
- 向导第 4 区：选骨架图 → 角色图（默认第 1 步正面视图，自动做 sheet_identity 白底版式）→ 生成 → 3×2 切片（阅读顺序，防 GetPixels32 左下原点导致相位颠倒）→ 逐格体检 → 存 Assets 出 8 fps 循环 .anim。

**遗留待办**：

- [ ] 在 Unity 里跑一次向导第 4 区，确认「生成 → 切片 → .anim」闭环（首次编译即验）；
- [ ] 文博角色（讲解员）的像素风版本 vs 华丽厚涂版本出图对比——Sheet LoRA 训练分布偏像素风，非像素 prompt 的服从度待验（v0.4.3 把立绘画风从扁平赛璐璐换成华丽厚涂后，这一项更值得先跑）；
- [ ] 侧视左右镜像（right ↔ left）是贴图翻转还是重新生成，视左右非对称部件（如单侧挎包）决定；
- [ ] 2511 底座 + Spritely 兼容性实验（留观）。

---

### 三·补7、方向覆盖的补全：官方没有的方向只能自制（2026-10-13）

**官方 starter 包核实**（`Downloads/spritely-starter-charts`）：isometric 只有 `down-right`，
poses/ 里根本没有 topdown 相机，sheets/ 里 topdown 只有 `down`——**缺口是官方如此，不是漏装**。

自制补全（骨架图是地面平面俯视图，几何变换合法），落在**用户覆盖目录**
`<工程根>/CharForge/comfyui/spritely_charts~/sheets/`（两级查找下优先生效，不动包内默认）：

| 新方向 | 方法 | 数量 |
|---|---|---|
| topdown `right` / `left` / `up` | 源 `down` 逐格**平面内旋转**（ROT_90 / ROT_270 / ROT_180）；白箭头、地面圈随转，仍是合法俯视骨架 | 3×20 |
| isometric `down-left` | 源 `down-right` 逐格**水平镜像**（东南↔西南自洽，白箭头已放大核实指向语义） | 20 |
| isometric `up-left` / `up-right` | **推导不了**：背面视角无法从正面图用保持站立的刚性变换得到（转 180° 头朝下） | — |

**三条硬纪律**：
1. **必须逐格变换再按原 3×2 阅读顺序重排**——整张旋转/镜像会把六帧顺序打乱（帧序错 = 动画倒放，静默不报错）。
2. 格子是 512×512 方格（1536×1024 / 3×2），旋转不越格；批量脚本里已断言源尺寸。
3. **.txt 的 facing 措辞必须同步改**（`facing south` → east/west/north；iso → `facing south-west`），
   且 topdown 侧面/背面的 `face outline` 分别改为 `the face in profile` / `the back of the head`——
   朝向与描写自相矛盾是本项目的大忌（同版式后缀与手势打架那类坑）。

**首次生成踩坑**：PIL `ROTATE_90` 是**逆时针**，南箭头转 90° 逆时针指向**东**——第一版左/右装反，
已按白箭头实测对调重生成。判断依据是箭头不是名字。

补全后覆盖：sidescroller left/right（包内）｜isometric down-right（包内）+ down-left（自制）｜
topdown down（包内）+ left/right/up（自制）。全部 160 张 png 均有配对 .txt。
背面视角（iso up-left/up-right）如确需，出路：官方在线摆姿势工具（spritely.studio，可能付费）或等实拍素材。

### 三·补8：姿势源实验——「高清化成画当姿势参考」可行，但必须去身份 + 头部抽象化（2026-10-13）

用户观察到骨架图出图动态偏弱，提议：拿真实成画精灵表（4×4）高清化后当姿势参考，
生成时让模型换角色保动作。同 seed（20261013）A/B 实测（工作流 = 现役六帧全套，单变量切换 image2）：

| 方案 | 结果 |
|---|---|
| A 骨架图（现役） | 身份 ✓，姿势松散、帧间相位乱（用户抱怨复现） |
| B1 成画条带直接当 image2 | **身份劫持**：输出参考图里的角色，不是立绘角色。cfg=1 下提示词压不住 |
| B3 摘 LoRA | **排版崩**：模型把立绘原样贴进画面中央。LoRA 是 3×2 排版纪律来源，不能摘 |
| B5 灰剪影 + LoRA | 姿势跟住 ✓ 服装身份 ✓，**剪影头部被画成巨大黑发团** |
| **B6 混合人偶 + LoRA（推荐）** | 身体=真实成画剪影、头部替换为骨架式圆灰圆：身份 ✓ 头正常 ✓ 六格步幅/倾身完全跟住 ✓ |

**机制解释**：模型从「含完整角色的那张图」取身份——骨架图之所以不劫持身份，正因为它不含身份；
成画参考含身份就必然劫持。所以姿势参考必须**去身份**（二值剪影），且**头部必须抽象化**
（保留头发形状会被当成发型复刻）。LoRA 承担排版纪律，摘掉即崩。

**可行管线**（Python 即可，无需训练）：成画帧 → nearest ×4 → 暗度转 alpha 的灰色剪影 (90,90,96)
→ 头部区抹除 + 画圆头（混合人偶）→ 4 帧循环补 6 格（f1 f2 f3 f4 f2 f3）→ 作为 image2 提交现役工作流，
提示词用 B6 版（"match the six gray mannequin poses… round head is only a placeholder"）。

边界与待验证：单动作（侧行走）单角色验证；attack/jump/death 与 topdown/iso 角度泛化未测；
剪影头部区域阈值（y<72、r=20 圆）按 32×48 源帧手调，换规格需重调。
产物：`output/charforge/ab_{A_skeleton,B_sprite,B3_nolora,B5_silhouette,B6_mannequin}_00001_.png`。

---

*本文件由调研整理，数据来自 2026-10-12 的实地扫描（`F:\model`、ComfyUI 日志、自定义节点目录）与公开模型页。*

**四向验证（同日追记）**：混合人偶管线批量跑参考图四向行走（同 seed），**left / right / up 三向成功**
（身份✓ 朝向✓ 步幅相位✓，up 连背面头发都正确）；**down（正面）6 次尝试全败**，失败模式固定：
中间格被立绘大幅占据 + 其余格子站立不迈步。根因：正面控制图与立绘同为正面人形，模型把两张图
当「两个角色」同框渲染而非「姿势→替换」；侧面/背面能成正因控制图与立绘形态差异大。这是
lightning 4/8 步 cfg=1 指令遵循的硬边界，提示词与步数都救不回。down 向出路：等 base-edit INT8
权重 / spritely.studio 摆正面姿势 / row1 直接调色换装。
