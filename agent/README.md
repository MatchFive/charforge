# npcforge —— CharForge 的自主 NPC 创作 Agent

给它一句话角色描述（或一份角色列表），它自己跑完：

```
人设 → 提示词 → 立绘 → 逐帧动画 → 质检裁决 → 装配 prefab → 挂脚本
```

产物直接落在 Unity 工程的 `Assets/CharForge/<角色>/`，与手工在插件窗口里跑出来的**同一路径、同一规格**。

---

## 它和插件是什么关系

**它不是插件的一部分，而是一个外部调用者**——和"手点窗口"处于同一个位置：

```
                ┌─ 人手点窗口 ────────────┐
CharForge 插件 ←┤                        ├─ ComfyUI（HTTP, :8000）
（能力提供者）   └─ npcforge 驱动 CLI ─────┘
```

* 插件**单独也能用**：装上包、开窗口、点三步，一样出 NPC。
* Agent **单独也能用**：不装插件包也行，只要把 `--config-dir` 指到插件包的 `Configs/comfyui`。
* 两条路互不依赖，产物落在同一个地方。

**为什么这是对的**：CharForge 的既有设计是"宿主倒置"——Unity 是唯一控制面，推理后端是可替换的下游。
Agent 就是又一个下游。它不开第二个 Unity、不读界面内存，全部通过插件的无头 CLI
（`docs/cli.md`）与磁盘产物工作。

---

## 安装

```bash
cd agent
python -m venv .venv
.venv/Scripts/pip install -e .        # Windows；macOS/Linux 用 .venv/bin/pip
.venv/Scripts/npcforge doctor --init  # 生成配置模板 + 体检
```

`doctor --init` 会在 `.npcforge/config.toml` 写出一份带注释的模板。填三段：

```toml
[unity]
unity_exe     = "F:/unityhub/6000.6.5f1/Editor/Unity.exe"
unity_project = "F:/unity_project/plugin"

[comfyui]        # 出图必需；config_dir 不填则按同仓库自动推导
comfy_url = "http://localhost:8000"

[llm]            # 可选。不配也能跑，只是人设朴素、裁决按数字来
llm_base_url = "https://api.xxx.com/v1"
llm_model    = "deepseek-chat"
llm_api_key  = "sk-..."
```

环境变量同样可以：`UNITY_EXE` / `UNITY_PROJECT` / `COMFYUI_URL` /
`NPCFORGE_LLM_BASE_URL` / `NPCFORGE_LLM_API_KEY` / `NPCFORGE_LLM_MODEL`。
优先级：命令行 → 环境变量 → 配置文件 → 仓库内推导 → 默认值。

---

## 用法

```bash
# 0. 先体检。没有硬伤再开始
npcforge doctor

# 1. 一个角色：一句话描述 → 完整产物
npcforge create 铁匠老王 "驼背的矮人铁匠，皮围裙，右臂一道烫伤，手里拎着锤子"

#   先验后行：跑到提示词为止，人满意了再去掉 --until（省一次出图的钱）
npcforge create 卫兵 "披甲长枪兵" --until prompt

# 2. 一批角色
npcforge batch npcs.yaml
npcforge batch npcs.yaml --only 铁匠老王,卫兵

# 3. 看进度 / 接着跑
npcforge status
npcforge status 铁匠老王          # 详情：质检数字、裁决、诊断、日志路径
npcforge retry 铁匠老王 --from frames

# 4. 走外部 API 那条路：视频 → 姿势参考帧
npcforge refs walk.mp4 --action walk --direction right
```

角色列表 YAML（`defaults` 只填没写的键，角色自己写的永远优先）：

```yaml
defaults:
  actions: [walk]
  directions: [down, right]
  movement: none

characters:
  - name: 铁匠老王
    description: 驼背的矮人铁匠，皮围裙，右臂一道烫伤
    role: 铁匠
  - name: 导览员阿茹
    description: 二十出头的女导览员，藏蓝制服马甲，笑起来有梨涡
    role: 博物馆导览员
    script: [NpcCharacter]      # 要挂到 prefab 上的组件
```

`directions` 只能取 `down/up/right/left`——它们会变成 `Frames/<动作>_<朝向>/` 目录名，不能自创。

---

## 设计：骨架确定，判断点自主

**主循环不是 ReAct 式的自由发挥。** 阶段顺序固定（人设 → 提示词 → 立绘 → 帧 → 质检 → 装配 → 脚本），
自主性集中在**每个阶段内部的判断**：

| 判断点 | LLM 在判断什么 | 没有 LLM 时 |
|---|---|---|
| `persona_writer` | 一句话描述 → 完整人设（含"不知道就说不准"的硬约束） | 模板拼一个朴素人设，**不编造** |
| `prompt_forger` | 角色描述 → 提示词的**角色本体**段（禁画风词、禁年代考据） | 直接用描述原文（Qwen 中文直入） |
| `qa_judge` | 质检报告 → 收下 / 原样重跑 / 加大姿势放大系数 / 换种子 | 同一套阈值的规则判定 |
| `diagnostician` | 失败日志 → 下一步动作（等前端 / 换参考 / 修配置 / 交给人） | 13 条已知故障特征匹配，匹配不到**不猜** |

这么分是权衡过的：这条流水线 90% 的步骤是确定性的，让模型自由驱动每一步会带来
三个具体代价——每步一次模型往返（慢）、同角色两次跑结果不同（不可复现）、
出错时分不清"模型想歪了"还是"工具坏了"。

三条纪律：

1. **降级必须留痕。** LLM 没配齐就退回确定性规则，日志打 `[降级]`，绝不假装"模型给的就是这样"。
2. **模型可以建议，边界由代码守。** 裁决要求放大 10 倍？钳到上限。存在确定性缺陷却判收下？覆盖掉。
3. **不报错的失败优先于报错的失败。** 立绘没有透明底、片段里 0 条关键帧——这类问题全部在这里显式挡下，不让它拖到更晚、更贵的地方才显形。

---

## 常见的坑（都是实测结论）

* **ComfyUI 地址用 `localhost:8000`，不要 `127.0.0.1:8000`。**
  本机 8000 上常另有一个只绑 `127.0.0.1` 的服务（比如你自己的 FastAPI 后端），
  "更具体的绑定优先"，于是 `127.0.0.1` 打到的是它——症状是 `/upload/image` 回 405。
* **逐帧生成需要 ComfyUI Desktop 的窗口开着。** SAM 3D 估计要浏览器前端参与，无头跑不了，
  报错形态是 `Pose Studio frontend sync failed`。这不是 bug，是这条路线的运行前提。
* **立绘提示词必须带固定件。** `--prompt` 给的是**角色本体**，透明底句 / 画风前缀 / 版式后缀
  由 bindings 的 `prompt_parts` 包在外面。自己硬写整串，出来的是实心底，
  而 `FrameNormalizer` 会按整张图算包围盒——不报错，只是全错。
  `python scripts/check_prompt_parts.py` 校验三处是否一致。
* **姿势参考库是严格两层**：`pose_refs/<动作>/<朝向>/*.png`。缺参考时**不要去调提示词**，先补参考。
* **归一化不在 Python 里复刻。** alpha 裁切 → 全批统一 scale → 统一画布 → 脚底锚定，
  这套规则只由插件的 `FrameNormalizer` 执行。外部复刻就是造第二份真相，迟早漂移。

---

## 目录

```
npcforge/
├── cli.py            # 命令行：参数 → NpcJob → 主循环。只做搬运，不做流程
├── loop.py           # ★ 主循环：阶段推进、批内隔离、断点续跑、失败诊断
├── models.py         # ★ 契约（改这里 = 改三处：loop / tools / 已有状态文件）
├── config.py         # 配置装配与优先级
├── state.py          # 作业落盘（.npcforge/jobs/*.json，人类可读，断点续跑靠它）
├── agents/           # 四个 LLM 裁决点（各自带确定性降级）
└── tools/            # 工具层：Agent 只能通过它碰外部世界
    ├── comfy.py      #   ComfyUI 直驱（与插件窗口同一段代码路径）
    ├── unity.py      #   插件 CLI 桥（退出码语义见 docs/cli.md）
    ├── qa.py         #   质检：只输出事实，不下结论
    ├── frame_audit.py / motion_check.py   # alpha 体检 / 动作幅度
    ├── media.py      #   视频 → 姿势参考帧（ffmpeg + 抠图 + 直接帧对法测周期）
    └── png_io.py     #   零依赖 PNG 读写（含 alpha 噪点压平）
```

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功（含 `--until` 提前停下的情况） |
| 1 | 失败（作业失败 / 体检有硬伤 / 批量里有角色失败） |
| 2 | 用法错（参数不对、文件不存在） |
| 130 | Ctrl+C（进度已落盘，重跑同一命令接着走） |
