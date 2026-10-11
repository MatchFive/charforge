# 在你的 Unity 6 工程里跑通 CharForge（W1 验收单）

目标只有一个：**在编辑器内零依赖跑通一次，得到一个能拖进场景、四向 idle/walk 能切的 NPC prefab。**
这一步完成之前不要碰 ComfyUI、不要训 LoRA、不要改流水线。

---

## 0. 前置检查（一次性，约 2 分钟）

| 检查项 | 要求 | 在哪里看 |
|---|---|---|
| Unity 版本 | 6000.0 及以上 | `Help > About Unity` |
| 工程类型 | 2D（模板任意，2D/2D URP 都行） | 有 Sprite 资产即可 |
| .NET 兼容级别 | **.NET Standard 2.1**（Unity 6 默认值） | `Edit > Project Settings > Player > Other Settings > Api Compatibility Level` |

> 兼容级别这一项要确认：本插件用 `Process.Kill(entireProcessTree: true)` 关闭代管的本地服务，
> 该重载在 .NET Framework 老档位下不存在。若报 `CS1501` 之类的方法签名错误，改回 .NET Standard 2.1。

---

## 1. 安装（两种方式，任选一）

### 方式一：从磁盘添加（推荐，仓库即唯一来源）

1. `Window > Package Manager`
2. 左上角 `+` → **Add package from disk…**
3. 选中本仓库的 `packages/com.charforge/package.json`

> 注意选的是 **package.json**，不是文件夹。Unity 会按路径引用，不复制文件——
> 之后在仓库里改代码，切回编辑器自动重编译。

### 方式二：写进 manifest（适合固定团队路径）

编辑工程的 `Packages/manifest.json`，在 `dependencies` 里加一行（相对路径以该文件的 `Packages/` 目录为基准）：

```json
"com.charforge.unity2d": "file:../../unity2d-charforge/packages/com.charforge"
```

### 装完应该看到什么

- Package Manager 出现 **CharForge 2D Character Pipeline** `0.2.0`
- 顶部菜单多出 **`Window > CharForge 角色资产生成`**
- **Console 没有红色报错**

自动装上的依赖：`com.unity.nuget.newtonsoft-json`、`com.unity.2d.sprite`。
`com.unity.2d.animation`（Sprite Library 用）**不是**硬依赖——你的 2D 工程若已装，它自动生效；
没装也不会报错，只是少生成一份 `*_SpriteLibrary.asset`。

---

## 2. 首次运行：用占位后端跑通「管子」

占位后端不调用任何模型，程序化画占位帧——它的作用是**证明装配链路是通的**，
而不是产出美术资产。第一次务必用它。

1. `Window > CharForge 角色资产生成`
2. **推理后端** → 选 `占位后端（零依赖·立即可用）`
3. **角色描述** 保持默认（`博物馆导览员，身着素雅青灰色长衫…`）
4. 方向默认勾了 `南（正面）`、`北（背面）`；动作勾了 `待机 idle`、`行走 walk`
   → 即 **2 方向 × 2 动作 × 4 帧 = 16 帧，4 个动画片段**
5. 点 **生成并装配**

### 验收标准（三项都满足才算 W1 通过）

- [ ] 状态栏出现 `完成 → Assets/CharForge/<job_id>/NPC_xxxxxx.prefab`
- [ ] Project 窗口里 prefab 被自动选中（插件会 Ping 它）
- [ ] 把 prefab 拖进场景，选中它：
  - `Animator` 的 Controller 有 **4 个状态**（south_idle / south_walk / north_idle / north_walk）
  - `CapsuleCollider2D` 尺寸**贴合角色**（不是一个小点）
  - 勾 `isMoving`、改 `facing`（0/1/2/3）能正确切到对应动画

### 产物落在哪

```
Assets/CharForge/<job_id>/
├── sheet.png                       # 2048×2048，4 行 × 4 列
├── manifest.json                   # 切片清单（y 已是 Unity 左下原点，勿再翻转）
├── assembly_spec.json              # 装配指令（可重放）
├── persona.json                    # 人设（知识边界在此）
├── Animations/
│   ├── south_idle.anim …           # 4 个 clip
│   └── <prefabName>.controller
├── NPC_xxxxxx.prefab               # ★ 要的就是它
└── NPC_xxxxxx_SpriteLibrary.asset  # 仅装了 2D Animation 时出现
```

中间产物另存在 `<工程根>/CharForgeCache/<job_id>/`——在 `Assets/` 之外，不会被 Unity 导入。

> **一个预期内的现象**：prefab 文件名是 ASCII（如 `NPC_a1b2c3`），因为中文名进文件名会在
> 跨平台 git / CI / 不同文件系统编码下出问题。**显示名仍是中文**（在 `NpcCharacter` 的
> Identity 里）。想让文件名可读，在「角色名（可选）」里填拼音或英文。

---

## 3. 报错对照表

| 现象 | 原因 | 处理 |
|---|---|---|
| `CS0246 找不到 Newtonsoft` | 依赖未解析（多为无网络） | Package Manager 搜索 `com.unity.nuget.newtonsoft-json` 手动安装 |
| `CS0246 找不到 UnityEditor.U2D.Sprites` | `com.unity.2d.sprite` 缺失 | 手动安装该包；它本应随依赖自动装上 |
| 程序集引用缺失 `Unity.2D.Animation.Runtime` | 不该发生（有 `defineConstraints` 保护） | 报出来请贴给我，说明可选依赖开关失效 |
| 方法签名错误（`Kill`、`TaskCompletionSource` 等） | API 兼容级别不是 .NET Standard 2.1 | 按第 0 节改回 |
| 窗口能开但**样式朴素、没有折叠面板** | UXML 未加载 | 看 Console 的 `[CharForge]` 警告，把路径贴给我（已改为动态解析，理论上不会出现） |
| 提示「没有可用的推理后端」 | 选了「自动选择」但没配任何后端 | 切到「占位后端」 |
| `没有解析到任何切片` | manifest 坐标与贴图不匹配 | 用占位后端复现一次并把 `manifest.json` 发我 |
| prefab 进场景**悬空 / 陷进地面** | 脚底锚点对齐问题 | **W3 范围**，属已知待打磨项 |

---

## 4. 第二次运行：换成文博场景（体现学术严谨）

跑通占位后端后，再填一次真实场景参数——这一段是给甄别简历的人看的：

| 字段 | 填什么 | 作用 |
|---|---|---|
| 角色描述 | `博物馆导览员，身着素雅青灰色长衫，手持讲解册，神态温和` | 进提示词 |
| 角色名 | `云叔` | 显示名（prefab 文件名会走 ASCII 兜底） |
| **年代 / 形制约束** | `南宋` | 进提示词 + 人设知识边界，防止把宋代器型画成明代样式 |
| **藏品档案 / 考据要点** | 例如 `龙泉窑青瓷：釉色青碧、开片自然；展陈口径参照南宋地层出土器物，不得出现明清纹样` | 进 `persona.json` 的【知识边界】 |

生成后打开 `persona.json`，确认 `persona_prompt` 里出现这段考据——**这是「AI 生成」与
「文博学术严谨」接上的那一环**，也是通用游戏工具给不了的能力。

---

## 5. 工程侧两件小事

在**你的 Unity 工程**根目录的 `.gitignore` 里加一行（避免把中间产物提交进版本库）：

```gitignore
/CharForgeCache/
```

生成的资产目录 `Assets/CharForge/` 是否入库由你决定——建议入库，它是产物、会进 prefab 引用。

---

## 6. 下一步

W1 验收通过后，按 `docs/roadmap.md` 一周一环推进。**铁律：任何一环没有单独验证过，不要往上叠第二环。**

顺序建议：

1. **W2** `ComfyUiBackend` 联调 —— 从 ComfyUI 用 `Save (API Format)` 导出 workflow，
   在同目录写 `xxx.bindings.json` 指定提示词/种子节点。这一步开始产出真实美术资产。
2. **W3** 脚底对齐与碰撞体打磨 + `PrefabAssembler` 手感 —— 让 prefab"看起来对"。
3. **W4** `style_lock`：先用**参考图 + IP-Adapter**做风格约束，**不要一上来训 LoRA**。
   顺序错了会卡死在训练环节，前功尽弃。
4. **W5** Sprite Library 实机验证 + 打包文档 + 一个能贴进 README 的 GIF。

跑完第一次把 Console 里 `[CharForge]` 开头的完整输出发我——按输出修，比按猜测修快得多。
