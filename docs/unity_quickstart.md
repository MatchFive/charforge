# 在你的 Unity 6 工程里跑通 CharForge（验收单）

目标只有一个：**从一句话角色描述出发，走完三步，得到一个能拖进场景、带循环帧动画的 NPC prefab。**

三步是一条直线：**角色立绘 → 帧动画 → 组装 prefab**。每一步的产物都落盘、可单独检查。

> 唯一前置是**本机 ComfyUI 在跑**（本地服务，不需要联网用云端模型）；
> 想让它把提示词写得更专业，再去高级设置里接一个外部 LLM —— **可选**。
> 这一遍跑通之前，不要去训 LoRA、不要去改流水线。

---

## 0. 前置检查（一次性，约 2 分钟）

| 检查项 | 要求 | 在哪里看 |
|---|---|---|
| Unity 版本 | 6000.0 及以上 | `Help > About Unity` |
| 工程类型 | 2D（模板任意，2D/2D URP 都行） | 有 Sprite 资产即可 |
| .NET 兼容级别 | **无要求**（.NET Standard 2.1 与 .NET Framework 都已支持） | `Edit > Project Settings > Player > Other Settings > Api Compatibility Level` |

> 兼容级别这一项**不需要改**。v0.2.2 起，`Process.Kill(entireProcessTree: true)` 这类
> 只在 .NET Standard 2.1 存在的重载已改为反射探测 + 平台回退（Windows 下走 `taskkill /T`
> 杀整棵进程树），因此两种档位都能编译、也都能真正清掉子进程——不为了一个插件去改工程设置。

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

- Package Manager 出现 **CharForge 2D Character Pipeline** `0.4.7`
- 顶部菜单多出 **`CharForge > 角色资产流水线`**
- **Console 没有红色报错**

自动装上的依赖：`com.unity.nuget.newtonsoft-json`、`com.unity.2d.sprite`。
`com.unity.2d.animation`（Sprite Library 用）是 **0.3.x 遗留的可选依赖**：当前的帧动画路线
自己写 `.anim` + `AnimatorController`，**不再产出 `*_SpriteLibrary.asset`**，
所以这个包装不装都一样，装不装都不会报错。

---

## 2. 三步跑通一次

0.4.0 起窗口是**一条直线**：第 1 步出立绘 → 第 2 步出帧 → 第 3 步出 prefab。
每一步的产物都落在磁盘上、可单独检查，所以失败时能立刻定位到是哪一步，
而不是像旧流程那样一次跑到黑再猜。

> **唯一的硬前置：本机 ComfyUI 得在跑。** 展开窗口最下面的**高级设置**，
> 把 **ComfyUI 地址**填对，点一下**探测连通性**。
> **ComfyUI Desktop 默认监听 `8000`**；通用教程里的 `8188` 在 Desktop 版上是空的。
> 探测自带 5 秒上限，连不上会直接给一句人话，不会一直转圈。

### 第 1 步 · 角色立绘

1. **角色名** —— 产物目录、prefab、控制器都靠它命名，**必填**。建议填拼音或英文（见下方「产物落在哪」）。
2. **角色描述** —— 一句话说清身份、服饰、道具、神态。默认值就是一条可用的示例。
3. 二选一拿到提示词：
   - 点 **锻造提示词（外部 LLM）** —— 用外部模型把描述写开。
     **高级设置里 BaseURL / API Key / 模型名三项都填了才走模型**；少填一个就自动退回本地模板拼，
     日志里会明说本次走的是哪条路（降级必须留痕）。
     **用了思考型模型**（Qwen3 思考版 / R1 一类）就顺手勾上**关闭模型思考模式**，
     否则思维链会被剥一遍、严重时正文全被剥空（日志会带 `⚠` 告诉你）。
   - 或者直接在 **文生图提示词** 框里手写 / 粘贴，跳过锻造。
4. 点 **生成角色立绘**；已有现成图就点 **载入已有立绘…** 指一张磁盘上的**透明底全身立绘**。
5. 状态栏出现 `完成 ✓` 且下方有缩略图 —— 这一步通过。

> **画风由插件固定，不用你写。** 立绘走**华丽精致的半写实厚涂**：光影层次细腻、材质区分明确
> （布料 / 皮革 / 金属 / 木料各自成色）、饰件细节丰富、轮廓带边缘光。
> 模型只负责写「角色本体」（身份、服饰、道具、神态），画风件与版式件由插件套在外面，
> 所以画风不会随模型措辞漂移。
> **为什么立绘能画得这么华丽**：v0.4.8 起第 2 步是 **keep_pose 动作模仿**，
> **立绘就是成片的画风来源**（身份槽直接决定输出风格），所以立绘必须画得结构清晰、材质分明。
> 唯一不能破的底线是**材质与轮廓可辨**：发色、服饰形制、主配色要一眼读得出来。

**第 1 步的判据会留档。** 每次点「锻造提示词」，会在
`<工程根>/CharForge/forge_log/` 落一组**同名文件**：

| 文件 | 内容 |
|---|---|
| `<时间戳>_<角色名>.txt` | 人读：六段分开标注——你写的描述、模型扩写的本体、插件注入的画风与版式、最终提示词 |
| `<时间戳>_<角色名>.json` | 机器读：便于以后做提示词回归对比 |
| `<时间戳>_<角色名>_portrait.png` | 立绘本身（点「生成角色立绘」后出现在同前缀下） |

出图不对时**先翻这三份**：能直接看出是哪一环的问题——描述没说清、模型写偏了、
还是画风件在打架——不用猜。日志里会打印绝对路径，复制到资源管理器即可打开。
这个目录在**工程根**（与 `Assets` 同级），不会被 Unity 导入，也不该进 prefab。

> 立绘此前只活在窗口那个 200×200 的预览框里，**根本看不出画风对不对** —— 所以现在落盘。

### 第 2 步 · 帧动画（keep_pose 动作模仿）

1. 在 **动作 × 朝向** 矩阵里勾要生成的格，默认勾 `行走`。每格对应一个**姿势参考目录**
   （`pose_refs/<动作>/<朝向>/`），格子上会标帧数；缺目录的格子显示 `—`。
2. 点 **生成勾选的帧动画（每帧约 30-60 秒）**。每组 = 逐帧「立绘 + 该帧姿势图 → 一帧成图」。
   **提示词不用写**——由 Pose Studio 前端按当帧姿势实时生成。

> **姿势参考从哪来**：`<工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/k1…k8.png`，
> 每帧一张**照片级姿势参考**（视频抽帧图实测可用；骨架 stick figure 未经 SAM 验证）。
> 文件名排序即播放顺序。原理：姿势图经 **SAM 3D Body** 实时估计 → 应用到 3D 人偶渲染 →
> 与立绘一起进扩散模型做姿势模仿——所以 **ComfyUI Desktop 窗口必须在线**，
> 且 ComfyUI 端装了 `vnccs-utils` 与 `VNCCS_QI2_PoseStudioV1.1` LoRA。
> 报 `Pose Studio frontend sync failed` 就是这条没满足，重试即可。

> **一次点击只用一颗种子。** 日志开头会打印 `本次任务种子 N` —— 该角色的**全部动作/朝向共用**它。
> 这不是细节：逐帧各掷一次随机种子，结果是每个动作长一张脸，而症状看起来像「模型不行」。
> 种子由「角色名 + 本次点击时间戳」派生，所以同角色重跑会换新的一版，
> 而名字不变、一次跑完的这一批内部是互相一致的。

3. 跑完看日志里的**逐帧 alpha 体检**（全 ✓ 才算过），以及状态栏打印的**脚底 pivot Y**。
   keep_pose 的输出背景带半透明噪点，插件收割时已按 alpha<64 压平——体检出现 ⚠ 就重跑该组。
4. **可以只勾要重跑的格**：没勾的组会自动沿用产物目录里已有的帧（日志会点名沿用哪些），
   不会把别的朝向从 prefab 里挤掉。想补一格就只勾一格，不必整轮重跑。
5. **左右朝向自检**：生成的侧向帧必须与朝向名一致——`left` 朝左走、`right` 朝右走。
   参考图画反了可以用 `python scripts/mirror_frames.py <目录> --in-place --backup <备份目录>`
   整组水平镜像（无损、不改变帧序）。

### 第 3 步 · 组装 NPC prefab

1. 勾 **添加贴合碰撞体**（默认勾；胶囊体尺寸由帧的实测像素包围盒算出，不用手填）。
2. **移动方式** 选「巡逻」会在 prefab 上加 `Rigidbody2D` + `SimplePatrol2D`（巡逻点拖进场景后再设）；
   选「键盘试走（WASD）」则加 `Rigidbody2D` + `NpcKeyboardDriver`，Play 模式直接用 WASD / 方向键试走各朝向。
   键盘试走驱动**两种输入后端都支持**（经典 Input Manager 与 Input System 包）：插件核心不硬引用
   Input System，装了该包时由可选程序集 `CharForge.Runtime.InputSystem` 自动接管，没装也不报错。
3. 点 **组装 prefab**。装配会**读磁盘校验**每个片段真有 N 条精灵关键帧
   （N = 该组帧数），条数不符会先自动重写一次，仍不符才报错中止——不会交出「播不动的动画」。
4. 界面重启过 / 内存里没有帧序列时，直接点 **组装 prefab** 也能修：帧 PNG 还在磁盘上，
   插件会照着 `Frames/<动作>_<朝向>/` 自动重建帧序列（**不必重跑几分钟的生成**）。

### 验收标准（都满足才算通过）

- [ ] 第 3 步状态栏出现 `完成 ✓ <prefab 路径>`，Project 窗口里 prefab 被自动选中（插件会 Ping 它）
- [ ] 收尾自检一遍产物（**推荐**，一条命令看清"到底有没有帧"）：
  `python scripts/check_anim_clips.py <你的Unity工程>/Assets/CharForge/<角色名>`
  —— 每个片段的关键帧数都等于对应帧 PNG 数才退出 0；报"空片段"就是装配失败
- [ ] 第 2 步状态栏打印了脚底 pivot Y，日志逐帧 alpha 体检全 ✓
- [ ] 把 prefab 拖进场景，选中它：
  - Animator Controller 的**状态数 = 有效帧组数**，状态名形如 `walk_left` / `idle_down`
  - **每个状态的 motion 都有精灵关键帧**（点开 Animation 窗口看得到 N 个帧块，N = 该组帧数）——
    有状态但动画里一条帧都没有，就是空片段，属于失败
  - `CapsuleCollider2D` 尺寸**贴合角色**（不是一个小点）
  - 勾 `isMoving`、改 `facing` 能在循环动作间切换。**朝向索引是固定契约**
    （`down=0 / up=1 / right=2 / left=3`，与 `NpcCharacter.FacingDirection` 的
    南/北/东/西 一致），日志里那行「朝向索引」只是回显，不随勾选顺序漂移；
    触发 `Play_attack` 这类 trigger 能切到一次性动作，播完自动回默认状态（`倒地` 例外：停在末帧）
  - 角色**站在地面线上**，循环动作播起来**不上下抖动**（pivot 取自全部帧的并集包围盒，不是逐帧各算）

### 产物落在哪

```
Assets/CharForge/<角色名>/
├── Frames/
│   ├── walk_left/frame-1.png … frame-N.png      # 帧数 = 该组姿势参考目录里的 PNG 数，已压平 alpha
│   └── attack_down/frame-1.png …
├── Animations/
│   ├── walk_left.anim …                         # 每组一个片段，默认 8 fps（高级设置可调）
│   └── <角色名>.controller
└── NPC_<角色名>.prefab                          # ★ 要的就是它
```

> **一个预期内的现象**：目录名、prefab 名、控制器名，以及 `NpcCharacter` 的
> `characterId` / `displayName` **全都是同一个「角色名」**——本版没有单独的「中文显示名」字段。
> 填中文能跑，但中文进文件名会在跨平台 git / CI / 不同文件系统编码下出问题；
> 想省事就填拼音或英文（**代价是显示名也会变成拼音**，见下方「已知待办」）。
>
> 另外：窗口里的**角色描述 / 角色名 / 文生图提示词 / 高级设置**都会持久化到 `EditorPrefs`
> （属于「这台机器上的这个编辑器」，不进版本库、也不会因切分支漂移），
> 重开窗口不会把你填的东西清空。

---

## 3. 报错对照表

| 现象 | 原因 | 处理 |
|---|---|---|
| `CS0246 找不到 Newtonsoft` | 依赖未解析（多为无网络） | Package Manager 搜索 `com.unity.nuget.newtonsoft-json` 手动安装 |
| `CS0246 找不到 UnityEditor.U2D.Sprites` | `com.unity.2d.sprite` 缺失 | 手动安装该包；它本应随依赖自动装上 |
| 程序集引用缺失 `Unity.2D.Animation.Runtime` | 不该发生（有 `defineConstraints` 保护） | 报出来请贴给我，说明可选依赖开关失效 |
| `CS0234 Unity.InferenceEngine 不存在` | 装了 `com.unity.ai.inference`，但插件程序集没有引用它 | v0.2.1 起改为**零引用**（不触碰该包任何类型），不该再出现；若仍报见第 6 节 |
| `CS0246 找不到 ObjectField` | `ObjectField` 是编辑器侧控件，不在 `UnityEngine.UIElements` | v0.2.1 起改用 `UnityEditor.UIElements` 别名，不该再出现 |
| `CS0104 'Object' 二义`（`UnityEngine.Object` vs `object`） | 文件同时 `using System` 与 `using UnityEngine`，裸写 `Object` | v0.2.2 起已显式写 `UnityEngine.Object`，不该再出现 |
| `CS0266 无法将 float 隐式转换为 int`（`FrameNormalizer`） | 用 `int` 承接 `Rect.xMin/xMax` | v0.2.2 起已改为 `float`，不该再出现 |
| `CS0117 FilterMode 没有 Box 成员` | `UnityEngine.FilterMode` 只有 Point / Bilinear / Trilinear | v0.2.2 起改用自实现的按 alpha 加权盒式降采样，不该再出现 |
| `CS1739 Kill 没有名为 entireProcessTree 的参数` | 工程 Api Compatibility Level 是 .NET Framework | v0.2.2 起改为反射 + `taskkill` 回退，**无需改工程设置**，不该再出现 |
| `CS0103 名称 'Xxx' 不存在于当前上下文中`，而 Xxx 明明写了 | **可见性**问题，不是"忘保存"。Xxx 是**另一个类型**的 `private` 成员，被跨类型裸调 | v0.2.4 起已加静态规则拦截（`check_unity_api_misuse.py` 第 6 条）。改法是加类型限定 `A.Xxx(...)` 并把 Xxx 提为 `public` |
| `CS0246 找不到类型或命名空间名 'Xxx'`，而 Xxx 明明是本仓库的类型 | **作用域**问题，同上一行同源。Xxx 是**另一个类型里的嵌套类**，在它的外层类型之外必须写 `外层.Xxx` | v0.4.4 起已加静态规则拦截（同一脚本第 10 条）。改法是把裸名改成 `外层.Xxx` |
| `SupportsTextureFormatNative can only be called from the main thread`（后半句在讲「构造器 / 字段初始化」） | 纹理 API 被 `ConfigureAwait(false)` 甩到线程池上了。**后半句是 Unity 的通用样板，与本问题无关——别去改构造器** | v0.4.6 起 `CharacterAssetService` 内部自封送、并在 `Decode` 处加了主线程断言，不该再出现。自己加新路径时，把纹理 / AssetDatabase 调用包进 `EditorMainThread.RunAsync(...)` |
| 警告 `CS0414 dialogueEndpoint 已赋值但未使用` | 序列化字段缺读取方 | v0.2.1 起已暴露 `DialogueEndpoint` 只读属性，警告消失 |
| 点「生成角色立绘」**连不上 / 超时** | ComfyUI 地址不对 | **ComfyUI Desktop 默认 `8000`**，不是教程里的 `8188`。高级设置里改完点「探测连通性」 |
| `上传视图失败（HTTP 405）：{"detail":"Method Not Allowed"}` | **端口被别的本地服务抢答**：同一端口上「更具体的绑定优先」——某个只绑 `127.0.0.1` 的本地服务（如自己的 FastAPI 后端）会拦住发往 `127.0.0.1:<端口>` 的连接，ComfyUI 绑的是 `0.0.0.0`，于是插件根本没打到 ComfyUI。`{"detail":…}` 是 FastAPI 的形状，ComfyUI 的 aiohttp 不这么回 | 临时绕过：地址改 `http://localhost:<端口>`（`localhost` 解析到 `::1`，会命中 ComfyUI 的 IPv6 绑定）或本机局域网 IP，再点「探测连通性」应显示 `在线：ComfyUI x.y.z`。根治：`netstat -ano \| findstr :8000` 看是哪个进程，给其中一个换端口（自己的后端换掉最省事；ComfyUI Desktop 端口在其设置里改，重启生效） |
| `有响应但状态异常：HTTP 404` / `应答不像 ComfyUI 的 /system_stats` | 同上，端口被别的服务占用 | 同上 |
| 第 2 步报「**还没有角色立绘**」 | 第 1 步没跑 | 第 2 步的角色参考图就是第 1 步的立绘；先生成或点「载入已有立绘…」 |
| 第 2 步提示**未找到姿势参考库**，或矩阵整片是 `—` | `Configs/comfyui/pose_refs/` 不在，或姿势库没放对 | 确认包目录完整；自备姿势图放 `<工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png`（严格两层） |
| 第 2 步报 `Pose Studio frontend sync failed` | ComfyUI Desktop 前端不在线，或当前没加载 Pose Studio 图 | 保持 Desktop 窗口打开并加载 `charforge_keepose_pose.workflow.json`，重跑 |
| 「锻造提示词」的结果**像模板拼的** | 外部 LLM 三个字段没填满 | 高级设置里 BaseURL / API Key / 模型名**三项齐了才走模型**；日志会写明本次走的是哪条路 |
| 提示词里混进大段「让我分析一下需求…」「检查一下是否符合要求」 | 思考型模型把**思维链**写进了 `content` | v0.4.2 起会先剥 `<think>` 块再拼装。若结果仍是摘出来的（日志有 `⚠ 已从中摘出`），勾高级设置的**关闭模型思考模式**后重锻 |
| 帧 alpha 体检出现**空帧 / 背景没抠开** | 那一帧的姿势没被 SAM 吃进去 | 该组重跑；连续失败换一张姿势参考（骨架图不行，要照片级） |
| 出图**姿势不对**（不报错、只是动作不对） | 姿势图分辨率过低或人物太小 | 裁剪放大姿势参考，让人物占满画幅（与立绘构图对齐） |
| prefab 进场景**浮空 / 陷进地面** | 某一帧的脚底被去白底一起清掉了 | 看日志六格体检；pivot 取自**全部帧的并集**包围盒，单帧被清空会把整体带偏 |
| 重跑后 Animator 窗口抛 `UnityEditor.Graphs` 空引用 | 控制器被删档重建，窗口持有失效图节点 | v0.4.0 起改为**复用 + 就地更新**，不该再出现；碰到就重开 Animator 窗口 |
| prefab 进场景**整体偏移、横向偏到一侧**（旧版本） | v0.2.3 之前包围盒取自缩放前的源图空间（偏大 `1/scale`≈17%，横向中心错位） | 历史问题，v0.2.3 起按画布落位求并集；重新生成即可 |
| 出图与角色描述**毫不相干**（像模型不行） | 提示词没写进节点。`CLIPTextEncode` 的字段叫 `text`，而 Qwen 的 `TextEncodeQwenImage21` 叫 `prompt`/`negative_prompt`，且正负在**同一节点**上——往不存在的键赋值不会报错，只会静默丢弃 | v0.3.1 起字段名改为候选探测并会在日志里明确告警。若仍出现，在 `.bindings.json` 里加 `positive_input` / `negative_input` |
| ComfyUI 工作流加载后**整片节点缺失** | 用了官方模板，而它把生成链包在 **subgraph** 里，subgraph 不能被 `/prompt` 直接提交 | 用 `Configs/comfyui/` 下已展开成扁平 API 格式的工作流；或自行从官方模板展开 |
| 换了后端后 `.meta` 的导入设置丢失 / GUID 变了 | `.meta` 里 importer 段落没换行（写成了字面量 `/n`） | v0.3.1 起已修并加入门禁第 8 条；若自行手写 `.meta`，注意换行必须是真换行 |

> 上表里标「不该再出现」的几条，是已经修掉的历史问题。若仍然报出来，多半是
> Package Manager 缓存了旧版本——`Window > Package Manager > CharForge > 右键 > Reimport`，
> 或确认 Package Manager 里的版本号是 `0.4.7`（或更高）。

### 怎么判断是「旧错误残留」还是「真的还在报」

Unity 的 Console **不会自己清掉上一次失败的编译结果**：脚本改了、但编辑器还没有重新编译时，
面板上仍是上一轮的报错。同一段错误来回报两次，多半就是这种情况。

三个判据，从上往下依次做：

1. **看行号对不对得上。** 报错行号若指向的内容和当前源码不符（例如报 `FrameNormalizer.cs(161)`
   是 `FilterMode.Box`，而文件里这一行早已不是），那这条错误就来自旧版本文件——属于残留。
2. **看 Console 里错误条目的时间戳**，和源码最后修改时间比。错误早于改动 = 残留。
3. **查 Unity 自己的编译日志**（最硬的证据）：

   ```
   <你的工程>/Logs/Editor.log
   ```

   搜 `Script Compilation Error`，看**最后一条**是哪一轮；再看它之后有没有新的
   `Requested script compilation` + `CompileScripts:` 且**不带** Error 块。
   有没有编译成功，还可以看产物时间戳：

   ```
   <你的工程>/Library/ScriptAssemblies/CharForge.Editor.dll
   ```

   编译失败时 Unity 不会写出这个 DLL；它的时间戳晚于源码改动，即说明这一轮是成功的。

**处理动作**：Console 右键 `Clear`，然后 `Ctrl+R`（或切到别的窗口再切回来）触发重编译。
不要因为面板上留着旧条目就去改已经正确的代码。

### 与 CharForge 无关的编辑器噪音：`NoSubscription`

Console 里若反复刷这一条：

```
Error reason is 'NoSubscription' and no additional error information was provided
(https://generators.ai.unity.com).
```

**它不是 CharForge 报的，也不影响编译与运行。** 出处是 Unity 官方的
`com.unity.ai.assistant` 包（`Unity.AI.ModelSelector`）在轮询生成式模型列表，
而当前 Unity 账号是 **Personal 版、且没有 Unity AI 订阅**，服务端就返回
`NoSubscription`。判据两条：

- 堆栈里只有 `Unity.AI.*` / `AiEditorToolsSdk.*`，没有一行 `CharForge`；
- 收尾是 `UnityEngine.Debug:Log`（普通日志），不是 `LogError` / `LogException`。

想让它消失，三选一：

| 做法 | 适合谁 |
|---|---|
| **移除包**：`Window > Package Manager > Unity AI Assistant > Remove`（Unity 官方说明该包「默认不包含、需手动安装」，可安全移除） | 不需要 Unity AI 的，推荐 |
| **只静音**：Console 搜索框输入 `-NoSubscription` 排除该条；右上角 `⋮ > Stack Trace > None` 收掉堆栈 | 想留着 AI 面板的 |
| **开通免费试用**：Personal 版可注册 Unity AI 试用，每月 1,000 点额度 | 想用 Unity 官方 Sprite Generator 出 2D 素材的 |

> 第三条与本插件相关：Unity 自带的 **Sprite Generator** 是同类的 2D 角色素材来源，
> 若开通试用，可以拿它和本插件的「本机 ComfyUI + keep_pose 动作模仿」路线做对比 ——
> 差别在于本插件产出的是**成组的帧动画**而不是单张立绘，且姿势由姿势参考图经 SAM 3D 估计锁定。

---

## 4. 第二次运行：接上外部模型，换成真实场景

### 接外部 LLM（可选，但推荐）

高级设置里填三项，只走 **OpenAI 兼容 `/chat/completions`** 一种协议：

| 字段 | 例子 | 注意 |
|---|---|---|
| LLM BaseURL | `https://api.deepseek.com/v1` | **不要带 `/chat/completions` 后缀**，插件自己拼 |
| LLM API Key | `sk-…` | 输入框是密码样式，不显示明文 |
| LLM 模型名 | `deepseek-chat` / `qwen-plus` / `gpt-4o-mini` | 服务商文档里叫什么就填什么 |
| 关闭模型思考模式 | 勾选框（默认不勾） | 用**思考型**模型（Qwen3 思考版 / DeepSeek-R1 一类）时才勾。见下方「思考链」一段 |

三项**任一为空 → 不报错，自动退回本地模板拼接**，日志里会写明「本次走模板」。
这是刻意的：提示词锻造是**增强**而不是前置，缺配置不该让整条流水线停摆 ——
但降级必须留痕，不许让人以为「模型给的就是这样」。

> 与走本机的 ComfyUI 相反，外部 LLM 这条链路**会走系统代理**：
> 多数云端端点不通代理出不去。两处的代理策略是分别选的，不是漏配。

#### 用思考型模型时：开「关闭模型思考模式」

Qwen3 思考版、DeepSeek-R1 这类模型会把**思维链混在正文里**返回
（形如 `<think>让我分析一下需求…</think>`）。插件会**先剥一遍**再拼提示词，
剥完正文为空时还会从思考块里**摘**出最像提示词的一句——但摘录会**带警告**，
因为摘的不是模型的最终答案。

想彻底避开这一步：在高级设置里勾 **关闭模型思考模式**
（向网关发 `chat_template_kwargs.enable_thinking=false`）。
**默认不勾**是刻意的——不是所有网关都认这个字段，严格的会因未知参数直接回 400；
遇到 400 就把它取消勾选。

日志里怎么看这件事：

| 日志 | 含义 |
|---|---|
| `提示词来源：<模型>（xx 字）` | 正常，正文就是模型给的 |
| `⚠ 模型把提示词写在了思考块里，已从中摘出 xx 字` | 摘录命中，**结果不保证与模型本意一致**，建议开上面那个开关重锻 |
| `⚠ 外部 LLM 结果不可用（…只有思考过程、没有提示词），已退回本地模板` | 摘不出来，已降级为本地模板拼的描述原文 |

### 换成文博场景

| 字段 | 填什么 | 作用 |
|---|---|---|
| 角色名 | `yunshu`（拼音） | 产物目录 / prefab / 控制器命名 |
| 角色描述 | `博物馆导览员，身着素雅青灰色长衫，手持讲解册，神态温和` | 提示词锻造的输入 |

> **角色描述只回答「看起来是什么样」。** 年代、朝代、藏品档案、考古考据**不进这个字段** ——
> 它们不改变任何一个像素，只会把提示词带偏；锻造提示词的系统提示词里已硬性禁止这类内容。
> 学术严谨该落在**知识问答与展陈口径**上，不在立绘的像素里。
> 这条边界是刻意划的，不是省事 —— 见 `docs/architecture.md`。

---

## 5. 工程侧两件小事

**第一件：`.meta` 文件必须入库。** 你是以「Add package from disk」装的本地包，Unity 会在包目录里
为每个文件生成 `.meta`（本仓库现有 **64 个**）。它们存的是资产 GUID——不提交，别人拉下来时 Unity 会
重新生成，GUID 一变，prefab 里的引用就断。所以**别把 `packages/**/*.meta` 加进 `.gitignore`**。

**第二件：搞清两个目录的分工**，别混：

| 目录 | 是什么 | 建议 |
|---|---|---|
| `<工程根>/CharForge/` | **你的覆盖配置**：姿势参考库、姿势模板 | 自备素材就入库（团队共享） |
| `<工程根>/CharForge/forge_log/` | **第 1 步的留痕**：提示词与立绘（`.txt` / `.json` / `.png` 同前缀一组） | 排查用，可随时删；嫌吵就加进 `.gitignore` |
| `Assets/CharForge/` | **产物**：帧 PNG、`.anim`、`.controller`、prefab | 建议入库，它会被 prefab 引用 |
| `<工程根>/CharForgeCache/` | 只有**已下线的旧流水线**还用；当前三步流程**不写这里** | 不用旧流水线就不用加 |

> 顺带一个容易栽的坑：**`<工程根>/CharForge/` 与 `Assets/` 是两棵目录树，没有共同祖先。**
> 所以覆盖配置不能放 `Assets/` 下面——插件的 `Application.dataPath` 指向 `Assets`，
> 包目录又是另一棵树，放错位置会**永远解析不到而且不报错**。两级查找是专门为这个坑写的。
>
> `forge_log/` 放工程根也是同一个理由：它是**排查用的中间物、不是游戏资产**。
> 放 `Assets` 会被 Unity 导入（1024×1024 的 PNG 还要生成 `.meta`），而它永远不该进 prefab。

---

## 6. 可选依赖的装配引用（要接引擎内推理时才需要）

> **先说清现状**：0.4.0 的窗口只走 `CharacterAssetService` → ComfyUI 这一条路。
> `Editor/Backends/` 下的 Mock / Sentis / Sidecar 与 `CharacterForgePipeline` / `PrefabAssembler`
> 仍然能编译，但**已无 UI 入口**（属死代码，待下一版决定去留）。
> 下面这段是给「要把它重新接回来」的人看的。

本插件对 AI 推理包与 2D Animation 包都是**可选依赖**，但处理方式刻意不同：

| 可选包 | 处理方式 | 原因 |
|---|---|---|
| `com.unity.ai.inference` | versionDefine 点亮宏，代码**完全不引用**其程序集 | 目前只用字符串描述能力，不需要它的类型；零引用 = 装不装都能编译 |
| `com.unity.2d.animation` | 独立的 `CharForge.Editor.SpriteLibrary` 程序集 + `defineConstraints` | 必须用它的类型（`SpriteLibraryAsset`），所以把这类代码整体隔离 |

所以当你开始实现 `SentisBackend.GenerateFrameAsync` 里的扩散流程时，需要**三步**：

1. 查装配名（别照抄博客，版本间改过名）：

   `Library/PackageCache/com.unity.ai.inference@<版本>/Runtime/*.asmdef` → 看 `"name"` 字段。
   2.2 起通常是 `Unity.InferenceEngine`；旧的 `com.unity.sentis`（≤2.1）是 `Unity.Sentis`。
2. 把它加进 `packages/com.charforge/Editor/com.charforge.Editor.asmdef` 的 `"references"`。
3. 那时才在 `SentisBackend.cs` 顶部 `using` 对应命名空间。

> **顺序别反**：先加引用、再写 using。反过来会得到一个"装包前能编、装包后编不过"的错位状态。
> 另外，**命名空间与包名不是一回事**：2.4 起包的显示名又改回 "Sentis"，但包 id 仍是
> `com.unity.ai.inference`、命名空间仍是 `Unity.InferenceEngine`。

---

## 7. 下一步

跑通一次后按 `docs/roadmap.md` 推进。**铁律：任何一环没有单独验证过，不要往上叠第二环。**

顺序建议：

1. **接本地 ComfyUI 出真实美术资产** —— 从 `packages/com.charforge/Configs/comfyui/README.md` 开始，
   那里有开箱可导入的工作流（keep_pose 逐帧 / Qwen 立绘 / SD1.5 ControlNet）与显存参数建议，
   不需要自己在 ComfyUI 里从零搭图或手工导出 API 格式。
2. **一致性打磨** —— 当前的姿势一致性来自**姿势参考图 + SAM 3D 估计**（VNCCS Pose Studio），不是提示词。
   要换姿势就换姿势参考图，**不要试图覆写提示词**（节点 484 的 prompt 是前端链接，写了就姿势全废）。
3. **风格锁定** —— 先用**参考图 + IP-Adapter**，**不要一上来就训 LoRA**。顺序错了会卡死在训练环节，前功尽弃。
4. **打包与展示** —— README 里贴一个能跑的 GIF + 打包文档。

> **最容易走错的一步**：把 ComfyUI 接上、出图成功，就以为一致性会自动成立。
> 不会。角色形态与姿势的一致来自**姿势约束**，而姿势约束来自姿势参考库 ——
> 库在 `<工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/`（用户覆盖）
> 或包内 `Configs/comfyui/pose_refs/`（随包默认）。
> 缺图时第 2 步会直接提示「未找到姿势参考库」、矩阵整片是 `—`，
> 看到这个**不要去调提示词**，先把姿势图补上。

跑完第一次把 Console 里 `[CharForge]` 开头的完整输出发我——按输出修，比按猜测修快得多。

---

## 8. 已知待办（v0.4.7 的取舍）

| 项 | 现状 | 影响 |
|---|---|---|
| **没有「中文显示名」字段** | 「角色名」同时用作目录名、prefab 名、`characterId`、`displayName` | 填拼音 → 文件名安全但显示名也是拼音；填中文 → 显示名好看但文件名带中文，跨平台出问题的风险自负 |
| **seed 不可手工指定** | 帧动画的种子由「角色名 + 本次点击时间戳」派生，写进日志 | 能复现、能重跑，但不能锁定到某一颗。要「同角色恒定外观」目前只能靠不改名字 + 一次跑完 |
| **思考型模型要手动关思考模式** | `关闭模型思考模式` 默认不勾（怕严格网关因未知字段报 400） | 忘了勾就走「剥 + 摘录」路径：能出结果但可能带 `⚠`，且摘录内容不保证是模型的最终答案 |
| **画风不能在界面里改** | 画风前缀写在 `PromptForgeService.StylePrefix` 常量里 | 想换画风要改代码 + **同步改** `charforge_qwen21_txt2img.workflow.json` 节点 6 的默认提示词（两处逐字一致，不一致会让人在 ComfyUI 里手工排队时看到另一种画风） |
| **手写提示词不留痕** | 提示词留痕只在点「锻造提示词」时产生 | 绕开锻造直接生成时，只有立绘 PNG 落盘，没有 `.txt` / `.json`——因为那种情况下提示词就在输入框里，没有「谁写的」可分辨 |
| **旧流水线代码仍在仓库里** | `CharacterForgePipeline` / `PrefabAssembler` / `SheetPacker` / `SpriteLibraryWriter` / `ProjectAssetWriter` / `Editor/Backends/`（Mock·Sentis·Sidecar）**已无 UI 入口** | 仍能编译、不影响使用，但会让人误以为还有第二条路。**待定：删还是接回**（v0.4.5 只清了 Python 侧的 `server/`，这批 C# 还在） |
| **`com.unity.2d.animation` 已是纯历史依赖** | 当前路线自己写 `.anim` + `Controller` | 装了不生效、卸载也不报错；可以卸 |
| **`Configs/comfyui/` 里 4 个旧工作流** | SD1.5 ×2、Qwen 图生图、Wan2.2 视频 | 窗口无入口；想用需先在高级设置里把「立绘/帧动画工作流文件」改成对应文件名 |
| **`schemas/` + `examples/`** | 旧流水线的 JSON 契约与示例配置 | 新流程不读不写；其 Python 实现 `server/`、示例运行脚本与单测已于 v0.4.5 删除，契约留档 |
| **主线程越界只能运行时兜底** | 「这个调用点在不在主线程」需要全程序分析，静态规则做不干净（lambda / 局部函数 / 间接调用都能绕过去） | 故**不做 lint 规则**，只在 `EditorMainThread.EnsureMainThread` 与 `CharacterAssetService.Decode` 处运行时抛**指名道姓**的异常。新增碰 Unity API 的路径时，先问一句「它一定在主线程吗」 |
