# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。包版本以 `package.json` 为准。

## [0.6.2] 2026-10-15

### 修复 · 窗口多行输入框不自动换行（第四版，命中真病根）

「角色描述」「文生图提示词」两个 multiline 输入框，长文本不自动折行、
输入框直接溢出窗口右缘；手动回车能折，自动折不折。

**前三版为什么全无效**：multiline 的文本住在内层 ScrollView 里，而官方文档
（UI elements reference · ScrollView「Wrap text of elements inside ScrollView」节）
明说——ScrollView 的内容容器**没有有限宽度（infinite in size）**，
文本拿不到"可用宽度"就不会折。所以：

| 已做过的修复 | 为什么白搭 |
|---|---|
| `white-space: normal`（①②③版都设了） | 主题本来就有，不是缺它 |
| `TextElement.maxWidth: 100%` | 百分比对着**无限宽**的父容器解析，等于没约束 |
| 内层 ScrollView 钉 Vertical + 藏横滚条 | 模式不改变内容容器宽度无界这个事实 |

**本版补的一块**：给内容视口（`unity-scroll-view__content-viewport`）与
内容容器（`sv.contentContainer`）都钉 `width: 100%`——`100%` 对视口解析出
**有限宽度**，`maxWidth: 100%` 这才真正有界，文本随之折行。

配套加了 `[wrap]` 自诊断日志：窗口打开约 1 秒后自动量两个输入框内层
input / container / text 的实际宽度打到窗口日志，若仍不折，该行能直接
指出是哪一层没被约束住。

---

## [0.6.1] 2026-10-15

### 新增 · 立绘固定件的机器可读副本（`prompt_parts`）

`Configs/comfyui/charforge_qwen21_txt2img.bindings.json` 新增 `prompt_parts`
（`alpha_hint` / `style_prefix` / `layout_suffix`），把 `PromptForgeService`
持有的三段固定件以**数据**的形式暴露给插件之外的下游。

为什么必须有：立绘的最终提示词是 `透明底句 + 画风前缀 + 角色本体 + 版式后缀` 拼出来的。
外部程序如果往正节点**整体写入**一个字符串，前两段就被顶掉了——症状是输出 100% 实心、
没有 alpha 通道的图，**且不报错**；再往下 `FrameNormalizer` 按整张图算包围盒，
一批帧的缩放基准全错，要到质检那一步才显形。v0.5.15 已在 keep_pose 链上吃过同型亏。

配套：

| 项 | 说明 |
|---|---|
| `agent/`（新增） | **npcforge**——独立于 Unity 运行的 NPC 创作 Agent，读 `prompt_parts` 拼立绘提示词 |
| `scripts/check_prompt_parts.py`（新增） | 校验三处对齐：C# 三个 const ↔ bindings `prompt_parts` ↔ 工作流节点 6 默认提示词 |
| `tests/test_prompt_parts.py`（新增） | 上面的单元测试（含"故意改坏一处必须红"的反向用例） |

插件窗口自身的提示词链路**一字未动**——它仍走 `PromptForgeService.Compose()`，不读 bindings。
这是一份给外部下游用的副本，不是第二套真相：`check_prompt_parts.py` 保证它漂不了。

---

## [0.6.0] 2026-10-14

### 新增 · 无头命令行入口（`-batchmode -executeMethod`）

窗口只能人来点，而「勘察 / 装配 / 自检 / 挂脚本」四件事都是**确定性**的
——帧 PNG 在磁盘上，装配就是照着它算 pivot、写片段、出 prefab。确定性步骤不该由人守着。
新增 `Editor/Cli/CharForgeCli.cs`，把它交给外部程序（Agent / CI / 批处理脚本）驱动：

| 命令 | 作用 |
|---|---|
| `-cmd env` | 打印环境事实：工程根 / 包配置根 / ComfyUI 地址 / 立绘与帧动画工作流文件名 / 产物目录 / 姿势参考库的动作×朝向×帧数清单 |
| `-cmd scan` | 每个角色的就绪状态：帧组数、片段真实关键帧数、prefab / controller / `persona.json` 有无 |
| `-cmd assemble -character <名>` | **从磁盘帧重建**并装配 prefab。可选 `-movement patrol\|manual\|none`、`-collider`、`-fps`、`-dialogue`、`-speed`、`-folder`。**装配后自动跑一次自检** |
| `-cmd verify -character <名>` | 数片段文件里真实的关键帧条数，与帧 PNG 张数比对；空片段退出码 1 |
| `-cmd attach -character <名> -script <类型全名>` | 把已编译进工程的自定义组件挂到 prefab（幂等，重复执行安全） |

设计要点（每条都对应一次踩坑）：

1. **不要加 `-quit`。** 加了之后 Unity 会在 `executeMethod` 返回后立刻退出，
   并把进程退出码固定成 0——**失败会被读成成功**，这是最坏的一类错。
   退出码统一由 CLI 显式给出：`0` 成功 / `1` 失败 / `2` 用法错。
2. **只读磁盘产物，不读界面内存。** Unity 刚重启、窗口从没打开过，装配照样能跑
   ——复用第 2 步就有的 `LoadFrameSetsFromDisk`，`Frames/` 里有 PNG 就够。
3. **自检两边分别从磁盘数**：帧数数 PNG 张数，关键帧数读 `.anim` 的对象引用曲线。
   只要有一边来自内存，这道守卫就废了（v0.5.6 血证：内存 8 条键、磁盘 0 条键）。
4. **在打开着的编辑器里手动执行不会关编辑器**：非 `-batchmode` 时只打日志、不退出。
5. `attach` 只认**类型全名**不认脚本路径——找不到类型就一定是没编译通过，
   这个区分比路径更有诊断价值；本命令不做「编辑后自动重编译」。

新增 `docs/cli.md`（命令与退出码参考、CI 接线示例）。

---

## [0.5.16] 2026-10-14

### 修复 · 覆写提示词丢掉透明背景句（用户实测抓出的回归）

v0.5.15 的 `pose_copy_prompt` 文案只写了姿势强化句，把原生模板里的
**`This is an RGBA image with transparency.`（透明背景要求）一起顶掉了**——
用户跑的 8 帧（keepose_00213~00220）**100% 实心品红背景、零透明像素**
（对照：保留原生提示词的 00205/00206 有 77% 完全透明）。
根因链：v0.5.15 当时验证用的是 00153，**只看了跨步没查 alpha**——单维度验证
漏掉了回归。

- **修复**：文案重写为「原生模板三要素 + 强化句」：
  `Draw character from image2`（角色来源）+ `Replace the pose of <image 2>
  with the pose of <image 1>. This is an RGBA image with transparency.`
  （原生姿势句 + 透明背景句）+ `Copy the pose precisely: …`（姿势强化句）。
  丢的只有 `<lighting>` 行（前端光照描述，人偶图自带光照信息）。
- **双维度实测（keepose_00221，k3 参考、seed 20261018）**：跨步清晰 ✓ +
  完全透明 74.2% ✓（原生模板对照 77.1%）。
- `bindings` 的 `_note_pose_copy_prompt` 补「文案三要素」警示，防再删句。

---

## [0.5.15] 2026-10-14

### 变更 · 姿势复制提示词从 vnccs 服务端补丁迁回仓库（插件注入 484）

v0.5.14 把提示词常量写进了 vnccs `pose_studio.py`（服务端补丁 part 2），有三个代价：
改文案要重启 ComfyUI、vnccs 升级即丢、业务文案与仓库版本脱钩。本版全部消除：

- **文案落点**：`Configs/comfyui/charforge_keepose_pose.bindings.json` 新增
  `pose_copy_prompt`（正文）与 `pose_copy_prompt_node`（写入目标 `484`）。改文案
  = 改 JSON 保存，**下一次提交即生效，不用重启 ComfyUI**；
- **注入方式**：插件提交前 `SetNode(484, "prompt", 文案)`，顶掉 484.prompt 接自
  488 输出槽 1（lighting_prompt）的链接——预期行为（人偶图已带光照与相机角度）；
- **vnccs 补丁 part 2 已撤下**：`pose_studio.py` 恢复为仅含 part 1（pose_amplify，
  这个能力补丁保留，仍需重启加载）；`export.pose_copy_prompt` 开关不再写入；
- **负结果入库**（keepose_00204 站桩实证）：只改 `pose_data.poses[].prompt` /
  `export.user_prompt` 而保留 484 链接**无效**——vnccs 前端 sync 回传的
  `lighting_prompts` 由前端自身内存状态重算，不采纳提交侧的提示词字段。API 提交
  侧能确定性控制提示词的只有覆写 484（详见
  `docs/vnccs-pose-amplify-patch.md` 的「负结果入库」节）；
- `bindings` 的 `_note_auto_prompt` 同步更正：原「绝不要把 484 写成字面量」的结论
  已被取证实验推翻，注释保留更正痕迹防再踩。

---

## [0.5.14] 2026-10-14

### 修复 · 静态帧真根因：Qwen-Edit 不服从姿势图，提示词必须显式要求复制姿势

v0.5.13 的旋转放大**确认生效**（日志 8×`12 limb joints re-solved`，`calf_r` 触到 140°
钳制），但 walk/up 成图仍全部站桩（相邻帧差 2.8/255）。本轮用**取证节点**（临时
SaveImage 直连节点 488 图像输出）拿到决定性证据：

- **喂给扩散的人偶图是清晰大跨步**（`debug_poseimg_00001_`：右腿提起、左腿支撑、
  双腿分开）——SAM、放大、前端人偶、捕获**全部正常**；
- **扩散输出却是站桩**（`keepose_00148`）。故障锁定在最后一环：Qwen-Image-Edit 2.1
  对 `images.image_1`（人偶渲染）的姿势服从度不足，背面朝向尤其严重。

对照实验（同一人偶图、同一 seed，各 ~30s）：

| 方案 | 结果 |
|---|---|
| 原样（姿势只靠 image_1） | 站桩（00148） |
| A：交换 image_1/image_2 | 仍站桩（00149） |
| B：提示词逐句描述跨步 | **跨步出现**（00150） |
| C：通用「严格复制姿势」句 | **跨步出现，身份稳定**（00153，采纳） |

- **修复（服务端 vnccs patch part 2）**：`pose_studio.py` 在 CSR 路径把
  `_POSE_COPY_PROMPT_SENTENCE` 追加到 lighting_prompt 之后，由
  `pose_data.export.pose_copy_prompt` 门控（不影响原生 Pose Studio 用法）；
  **门控位置修正（21:34 实测踩坑）**：必须在前端 sync 覆盖 `data` **之前**取——
  `synced.export` 是前端 UI 状态，不含插件写入的开关，放行尾读取永远 False
  （`pose_amplify` 生效正是因为它在覆盖前读）。
- **插件侧**：`GenerateKeepPoseFramesAsync` 无条件写 `export.pose_copy_prompt = true`
  （与 pose_amplify 同一注入点，left/right ×1.0 也要追加）；
- **修复 · 无效重试**：静态帧重试原样重提 = ComfyUI 输入缓存直接回同一结果
  （用户截图实证：同一文件名 26.54s + 0.02s + 0.03s 三条记录，后两条根本没跑扩散）。
  重试改为**换 seed**（`seed + attempt`）绕开缓存，真正重跑；
- 参考帧取证附带结论：`pose_refs/*` 是 960×720 **带背景的视频帧**（alpha 全 255），
  SAM 输入不受影响；四个朝向的角色区邻差 6.4–9.3/255，参考素材本身动作信号充足。

---

## [0.5.13] 2026-10-14

### 修复 · pose_amplify 第二次失效：放大的是「长度」，不是「角度」

v0.5.12 的补丁**数值上命中了 36 个关节**（日志 `amplified 36 limb points` ×8），
但成图仍然静止。本轮定位到几何学上的错误，并改到真正的注入点。

- **证据（帧差量化）**：补丁前 `keepose_00124–00131` vs 补丁后 `00132–00139`——
  相邻相位帧差 2.72 / 2.79，全对帧差 3.29 / 3.55，跨批同相位差 5.8（≈重跑噪声）；
  **8 帧横向轮廓 span 仅 6–8 px，而角色高 920 px**。放大对成图完全无影响。
- **根因**：前端 `core.js` 的 `applySAM3DImport()` 有两条通道——
  1. `_applySAM3DRotationImport()` → `_applySAM3DHumanoidDeltaImport()`：
     用 `joint_rotations` 与 `rest_joint_rotations` 的**世界旋转增量**直接写骨骼
     四元数 —— **这才是决定四肢摆角的通道**；
  2. `applyWorldKeypointImport(worldKps, { normalizeLegs: true, normalizeArms: true })`：
     用 `joint_coords` 取**方向**，长度归一化到人偶骨架（`autoFitSAM3DBoneLengths`
     另按 SAM 骨架拟合骨骼长度）。
  v0.5.12 做的是 `base + (p - base) * factor` —— **以骨盆为中心的径向等比放大**。
  等比缩放向量**不改变方向**，在 `normalizeLegs` 眼下与没放大完全等价；它唯一改变的是
  「SAM 骨架的肢体长度」（3×），反而污染了体型拟合。
- **修复**：改为放大 `joint_rotations` 相对 `rest_joint_rotations` 的**旋转增量**——
  轴角分解后绕原轴 ×factor（Rodrigues 重建，钳 140°），再用 rest 骨骼长度做一次 FK
  重建 `joint_coords`，保证旋转通道与位置通道角度一致；`thigh/calf/upperarm/lowerarm`
  放大、`foot/hand` 只随父链 FK（不放大自身旋转，避免踝腕翻折）。
  骨骼长度恒等于 rest（单测 diff ≈ 0），**不再污染体型拟合**。
- **系数下调**：因为这次是真角度放大（35°→77°），bindings 改为 up ×2.2 / down ×2.0。
- **算法单测**：轴角放大精确（40°×3 → 120.0°）、140° 钳制、正交性保持、
  骨骼长度守恒、factor=1.0 时恒等（安全回退）。
- **自检日志换格式**：`CharForge pose_amplify x2.2: 12 limb joints re-solved
  (thigh_l 77.0deg, …)`，把放大后的真实摆角打出来；全不命中打 `NO limb rotations matched`。

## [0.5.12] 2026-10-14

### 修复 · pose_amplify 从未生效（注入点打在了错误的载荷结构上）

v0.5.11 给 vnccs 打的「姿势摆动放大」补丁**一次都没生效**，本轮实锤并修正。

- **证据**：ComfyUI 日志里找不到补丁的 `pose_amplify ... amplified N ...` 输出；
  重跑 walk/up 后相邻帧差仍是 4.6/255（远低于 ≥11 的正常线），且"只有一两帧有变化"
  的形状与未打补丁时完全一致。
- **根因**：同一份姿势数据在链路上有**两张不同的表**——
  - 服务端**广播给前端**的 SAM 原始载荷：`joint_coords`（dense 关节 3D 坐标）+
    `joint_names`（索引→名称）+ `canonical_keypoints_3d`（70 个图像关键点）；
  - 前端**提交回节点**的 `pose_data`：`poses[].bones`（欧拉角）。
  v0.5.11 的补丁按后者放大，而它作用在前者身上 → `poses` 取到 `None`，
  函数原样返回，**命中 0 个关节且一声不响**。
- **修复**：补丁改为逐个尝试真实坐标字段再回退——`joint_coords`（腿 2/3/4·18/19/20、
  臂 39/40/42·75/76/78，根 = pelvis 索引 1）、`canonical_keypoints_3d` 与 `keypoints_3d`
  （髋 9/10、膝 11/12、踝 13/14、肩 5/6、肘 7/8、腕 41/61，根 = 左右髋中点）；
  把肢体点相对"根"的位移等比放大。三种形态各写了单测。
- **不再静默**：全都没命中时打印 payload 顶层键与各坐标字段长度，直接指出"结构变了"。
- **静态帧警告文案**改为带序排查：① 先确认 ComfyUI 日志有 `amplified N limb points`
  （没有 = 补丁没加载，重启 Desktop）② 前端窗口是否在前台 ③ 才轮到换参考图。
- bindings 的 `_note_pose_amplify` 同步写明注入点与生效自检方法。

## [0.5.11] 2026-10-14

### 修复 · 背面/正面走路「塌成站姿」的根因：姿势摆动放大（pose_amplify）

用 vnccs 调试转储（temp/vnccs_debug_488_*.json 里的 captured_images）拿到决定性证据：
**SAM 估计与人偶渲染都在跨步**（背面帧后腿抬起清晰可见），但 SAM 从背面参考估出的
肢体摆幅只有侧面的 1/3~1/4（背面 thigh ±10~20°，侧面 ±40°+）——2D 投影里前后向
步伐的信号天然弱，弱信号到扩散端被站姿先验吞掉，成图静止（相邻帧差 0.9~3.5）。

- vnccs-utils 服务端补丁（`nodes/pose_studio.py`，原件备份 `.charforge-bak`）：
  新增 `pose_data.export.pose_amplify`——送前端人偶前，对肢体骨骼
  （thigh/calf/foot/upperarm/lowerarm × l/r）的摆动分量等比放大，钳制 ±80°；
  默认 1.0 = 完全原行为。**需重启 ComfyUI Desktop 生效**。
- bindings 新增 `pose_amplify_by_direction`（up ×3.0 / down ×2.5 / left/right ×1.0
  起步）与 `pose_data_node`；插件按方向把系数写入节点 488 的 pose_data 并留痕日志。

## [0.5.10] 2026-10-14

### 改进 · 静态帧体检给出方向性补救

阿茹 walk/up 实测：参考图本身有跨步（相邻差 4.6~9.4），成图却塌成站姿（1.6~2.6），
且**上半身摆动也没跟出来**（生成上半身差 2.7~6.5，参考 6~12）——逐帧自动重试救不动，
属系统性姿势跟随失败，不是捕获竞态。背面/正面朝向的步伐是前后向位移，
2D 投影信号天然弱，SAM 从背后难复原前后脚；裙装角色进一步遮挡。

静态帧体检警告现在会直接说明这一点，并给出补救：
换『跨步大、双脚左右明显分开』的该朝向参考图（侧向分腿的步伐信号在正/背面依然可见）。

## [0.5.9] 2026-10-14

### 修复 · 「上传视图失败（HTTP 405）」把「端口被别的服务抢答」说成人话

实跑报错：`上传视图失败（HTTP 405）：{"detail":"Method Not Allowed"}`，第一帧就断，
看不出跟 ComfyUI 有什么关系。排查结论——**不是 ComfyUI 的问题**：

- 本机 `python3.12 run.py`（自己的 FastAPI 后端）也监听 8000，但它绑的是 `127.0.0.1`，
  而 ComfyUI 绑 `0.0.0.0:8000`；**同一端口上更具体的绑定优先**，于是插件写
  `http://127.0.0.1:8000` 打到的是那个后端（它只有前端静态页的兜底 GET 路由，回 405），
  写 `http://localhost:8000`（解析到 `::1`）或本机局域网 IP 才打到 ComfyUI。
- 实测对照：`127.0.0.1:8000` → `/system_stats` 404、`/upload/image` **405**；
  `localhost:8000` 与 `192.168.x.x:8000` → 两者均 **200**（ComfyUI 0.40.0，上传返回 `{"name":…}`）。
- `{"detail":…}` 是 FastAPI/Starlette 的返回体形状，ComfyUI 的 aiohttp 不会这么回——
  这条线索足以在日志里一眼判出"对端不是 ComfyUI"。

修法（把下一次的排查时间省掉）：

- 新增 `PortCollisionHint`：`/upload/image` 与 `/system_stats` 返回 **404 / 405**
  或返回体含 `"detail"` 时，异常文案里直接带上判定与两条出路
  （临时：改 `localhost:<同端口>`；根治：`netstat -ano | findstr :<端口>` 找出占用者换端口）；
- 探测连通性新增两类识别：应答不是 JSON、`/system_stats` 里没有 `system.comfyui_version`
  → 报「这个地址上应答的不是 ComfyUI」，不再笼统地说"状态异常"；
- ComfyUI 地址输入框 tooltip 与 `docs/unity_quickstart.md` 报错对照表同步补上这一条。

## [0.5.8] 2026-10-14

### 修复 · 第 2 步只重跑部分格会把没勾的朝向从 prefab 里挤掉

第 2 步每次开跑都会清空内存里的帧序列、只装这次勾选的组——于是
「生成结果不对，只想单独重跑一格 left」之后直接点装配，控制器里就**只剩 walk_left**
一个状态，down / up 从 prefab 里消失，而且全程不报错。

修法：开跑前不丢历史——生成完勾选的组后，未勾选的组直接从产物目录的
`Frames/<动作>_<朝向>/` 读回来沿用（`LoadFrameSetsFromDisk`，帧在磁盘上就是信息），
沿用哪些组写进日志。想补一格就只勾一格，其余方向原样保留。

### 运维记录 · 姿势参考图库左右朝向归位（left ↔ right）

实测确认：`pose_refs/walk/left/` 的 8 张参考图画的是**向右走**（原始关键帧素材朝右、
命名先入为主），照它生成的 walk_left 帧自然也朝右——游戏里按左键人物「倒着走」。
处理（`scripts/mirror_frames.py`，两套 Unity 工程同步执行）：

- 原 8 张（朝右）归位到 **`pose_refs/walk/right/`**——顺带补齐了此前一直缺失的向右朝向；
- 水平镜像 8 张写回 **`pose_refs/walk/left/`**——现在是真正的向左走参考图。
  镜像无损、不改变帧序（步态周期镜像后仍按 k1→k8 循环）；
- 已生成产物 `Assets/CharForge/阿茹/Frames/walk_left/` 的 8 张同样就地镜像
  （原件备份在 `<工程>/CharForge/backup/20261014_walk_left_orig/`）——
  `.meta` 与 GUID 未动，prefab 里的 walk_left 动画**立即**变成向左走，不必重跑生成；
  此后重跑 left 会以镜像参考图重新生成、覆盖这批帧。

## [0.5.7] 2026-10-14

### 修复 · 一批片段里只有部分有帧（空片段：内存里有键、磁盘上一条也没有）

实跑现象：装配出的 prefab 里 `walk_down` / `walk_left` **一个精灵关键帧都没有**，
只有 `walk_up` 正常；而三向各 8 张帧 PNG 都好好地躺在 `Frames/` 下、控制器里三个状态
一个不少。看着像随机。

根因在 `AnimatorWriter.EnsureSpriteClip` 的**落盘时序**：新建片段走的是
「`AssetDatabase.CreateAsset`（此刻写出去的是一个**空**片段）→ 再在内存里补精灵曲线与
循环设置 → 把落盘留给调用方最后统一 `SaveAssets`」。中间只要再碰一次 AssetDatabase
（下一次循环的 `LoadAssetAtPath` / `CreateAsset` 就足够），Unity 会结清该资产的待导入，
**把刚落盘的 `.anim` 重新读进来顶掉内存实例**——脏标记连同未落盘的改动一起消失，
磁盘上留下一个 0 关键帧的空片段，全程一声不响。最后一个片段因为之后没有新的资产操作，
正好赶上那次统一 save，所以只有它活着。

产物时间戳与推断完全吻合（`walk_left` 17.666 / `walk_down` 17.715 = `CreateAsset` 写空文件的
时刻；`walk_up` 17.806 = 统一 save 的时刻）。v0.5.6 那道「空片段硬校验」查的是**内存对象**，
内存里 8 条键、磁盘上 0 条键，守卫必然放行——**校验了内存，没校验真相**。

修法：

- `EnsureSpriteClip` 改完就地 `AssetDatabase.SaveAssets()`，把「改动 → 落盘」窗口压到零；
  新建路径与复用路径都走这一条；
- `EnsureController` 之后也立刻落盘（控制器同样是新建的，参数/状态都在内存里）；
- 硬校验改为**读磁盘**：新增 `AnimatorWriter.CountSpriteKeysOnDisk()`，
  装配处据此校验；**关键帧数 ≠ 该组帧数**时按原参数自愈重写一次再验，
  仍不符才抛错（报错带上实测条数与应有帧数）；
- 「只凑到 N/M 帧」也不再静默——帧 PNG 没导入成 Sprite 时按剩余帧生成的旧行为，
  现在会在日志里点名；
- 精灵曲线的绑定收敛到 `AnimatorWriter.SpriteCurveBinding` **一处**，
  `FrameNpcAssembler.SpriteBinding` 只做转发——两处各写一份迟早漂移，
  那时校验的就不是同一条曲线了。

### 新增 · 第 3 步可从磁盘已有帧重建（修复入口）

帧序列只活在界面内存里，Unity 一重启就没了，而 `Frames/<动作>_<朝向>/frame-N.png` 还在。
现在点「组装 prefab」时若内存里没有帧序列，会自动照着产物目录重建
（`FrameNpcAssembler.LoadFrameSetsFromDisk`，帧号按数值排序，`frame-10` 排在 `frame-9` 之后），
**不必重跑几分钟的生成**。已有产物损坏时，一次装配即可修复。

### 新增 · `scripts/check_anim_clips.py`：产物自检（数出"到底有没有帧"）

`python scripts/check_anim_clips.py <角色产物目录>` 数的是**片段文件里**的精灵关键帧条数——
不是内存对象、也不是 Inspector 上看着像不像。输出每个片段的关键帧数与循环位，
与帧 PNG 张数比对；有 0 关键帧的片段即退出 1（可串进脚本）。
配套单测 `tests/test_anim_clip_check.py` 用事故里的真实片段形状做夹具，坏法必须被抓、好产物必须放行。

> 这次事故里它一眼看出关键：两个坏片段的 `m_LoopTime` 还是默认 0，只有正常的那个是 1——
> 独立印证了"空片段就是停在 `CreateAsset` 那一刻、之后再没被写过"。

## [0.5.6] 2026-10-14

### 修复 · NpcKeyboardDriver 在 Input System 后端下每帧抛异常

实机 Play 报错刷屏：`InvalidOperationException: You are trying to read Input using the
UnityEngine.Input class, but you have switched active Input handling to Input System
package in Player Settings`——工程把 Active Input Handling 设成「Input System 包」
后，经典 `UnityEngine.Input` 被禁用，驱动件每帧抛一次。

修法沿用仓库既有的「可选依赖用门控程序集」范式，**核心程序集不硬引用 Input System**
（没装该包的用户照样能编译）：

- `NpcKeyboardDriver` 新增静态钩子 `InputProvider`：有钩子用钩子，否则在
  `#if ENABLE_LEGACY_INPUT_MANAGER` 下读经典 `Input.GetKey`；两者都不可用时静默返回 0
  而不是每帧抛异常；
- 新增可选程序集 **`CharForge.Runtime.InputSystem`**（`Runtime/InputSystem/`）：
  `versionDefines` + `defineConstraints` 门控，装了 `com.unity.inputsystem` 才编译，
  通过 `[RuntimeInitializeOnLoadMethod]` 把 `Keyboard.current` 读取注册进钩子。
  与 Sprite Library 集成同一范式；
- 门禁白名单 `validate_unity_package.py` 增加 `Unity.InputSystem`。

工程用哪种后端都不需要再改设置，Play 直接 WASD / 方向键试走。

## [0.5.5] 2026-10-14

### 修复 · 超时自动重试此前从未生效（异常文案截断丢关键词）

0.5.4 实跑第一帧即超时且直接穿透重试：`RunWorkflowAsync` 抛异常时把整段 JSON
`Trunc` 截断，`RunKeepPoseFrameAsync` 靠 `frontend sync timed out` 识别重试，
关键词被截掉 → 过滤器永远匹配不上。修复：超时错误改抛**简洁人话文案**
（关键词保留在文案开头），其余错误照旧截断透传。

另核实 vnccs 同步机制：服务端广播 `vnccs_apply_sam3d_pose`，**任意**打开该
ComfyUI 的前端页面（含局域网内其他电脑的浏览器）都会接收并渲染/捕获/回传——
Desktop 窗口被遮挡时，让局域网另一台电脑打开同一工作流即可充当渲染前端
（Desktop 需在服务器设置里把 Host 改 0.0.0.0 开放局域网）。

## [0.5.4] 2026-10-14

### 修复 · alpha 体检假警报 + 前端同步超时单帧自动重试

实机复跑（walk/left 8/8，补丁后相邻帧差 12.7 = 正常走路水平，**viewer.js 捕获补丁生效**）：

1. **alpha 体检假警报**：`FlattenAlphaNoise` 的阈值写错——`ratio` 已是百分数，
   判定却用 `ratio > 0.92`（本意 92%），导致任何超过 0.92% 的正常帧都报
   「背景疑似没抠开」。实拆新旧批次 alpha 构成几乎一致（不透明 17% / 21.9%），
   帧本身没问题。改为 `ratio > 92`；
2. **偶发 `frontend sync timed out` 不再掀翻整组**：walk/down 第 4 帧超时直接
   中止了整组生成——超时是随机的（前端 ws 短暂失联），新增 `RunKeepPoseFrameAsync`
   对该帧自动重试（最多 2 次、间隔 10 秒），其余异常照旧上抛。

## [0.5.3] 2026-10-14

### 修复 · keep_pose 静态帧改为「逐帧检测 + 自动重试」（根因已定位）

追问「是不是必须一直盯着 Desktop」引出的深挖：up 朝向重生成 8/8 成功但 5 帧仍是站姿，
而**同一颗种子+同一张参考图的两次运行全图差 80.6**（一次跨步一次站立）——非确定性竞态实锤，
读 vnccs-utils 源码定位：前端捕获走 `spark.update()`（3DGS 高斯排布，异步跨帧）后立刻
`drawImage` 抓画布，排布没落完就抓到**上一次渲染的旧内容（默认站姿）**；Desktop 窗口
最小化/被遮挡时浏览器节流渲染循环，触发概率大增。第三方代码内部竞态，插件侧无法根治，改自愈：

- `GenerateKeepPoseFramesAsync` 每帧完成后**立刻与前一帧比差**：参考图差异明显（>4/255）
  而成图几乎没变（<5/255）→ 判定抓到旧帧，**自动重提该帧（最多 2 次）**；
- 参考图本身就静止时不重试（那是姿势参考的问题，重试无解）；尺寸不匹配（NaN）不误重试；
- 出组静态告警保留（兜住首帧与残余漏网）。

**生成时的操作建议**（降低触发概率，非必须）：生成期间别最小化 ComfyUI Desktop 窗口。

## [0.5.2] 2026-10-14

### 修复 · keep_pose 批量竞态出「整组站姿帧」——帧间间隔 + 出组静态自检

实机判别实验（`NPC_阿茹` up 朝向 8 帧全站立、腿部差仅 2.9/255，而 down/left 为 16）：

- 同一颗种子、同一张姿势参考、同一份工作流，**单独提交重跑即正常**（腿部差 11.3）；
- 排除种子与背面 SAM 估计两个嫌疑——问题在**批量排队时 VNCCS 前端同步的时序**：
  姿势还没渲染到 3D 人偶就被捕获，输出默认站姿，且不报任何错。

- `GenerateKeepPoseFramesAsync`：帧与帧之间加 2 秒间隔（给前端渲染收尾）；
- 新增**出组静态帧自检**：相邻帧平均像素差（RGBA，/255 尺度）实测正常走路 ≥11、
  站姿残骸 ≤4——min<5 或均值<8 即打 ⚠（提示查前台焦点/逐帧单独重跑），只告警不抛
  （个别双脚触地相位本来就动得少，全组静态才致命，交给人工判断）。

## [0.5.1] 2026-10-14

### 修复 · 空片段残骸不再静默存活

实机排查 `NPC_阿茹`：三个 .anim 里 `walk_up` 有 8 个精灵关键帧，`walk_down` / `walk_left`
是**空壳**（`m_PPtrCurves: []`、LoopTime 0）——磁盘上 24 张帧是全的，控制器引用的却是
上一轮装配留下的空片段。空片段的来源：某轮装配时该片段有效帧数为 0（帧未导入完 /
Sprite 加载失败被跳过），装配器只打一条 ⚠ 就跳过，同名旧片段原样留在磁盘——之后
帧补齐了，只要控制器还引用它，就永远播空动画且无人报错。

- 装配器：某片段无有效帧时**删除同名残留 .anim**（让缺片段变成看得见的状态），
  而不是把空壳留给下一轮；
- 写完片段后**硬校验精灵关键帧数**，为 0 直接抛错中止（附「帧 PNG 导入异常」文案）；
  日志从「N 帧」改为「N 关键帧」，写的是实测值不是期望值。

已有空片段的工程：用 v0.5.1 重跑第 3 步即可——EnsureSpriteClip 会就地把曲线写满。

## [0.5.0] 2026-10-14

### 修复 · 朝向契约三处统一（「大部分帧播不到」的根因）

v0.4.8 换 keep_pose 路线后，facing 索引有三套互相对不上的编码，且默认状态没有回转边：

1. **装配器按勾选顺序编号** facing（同一控制器两次装配编号都不同），外部脚本无法预知；
2. `NpcCharacter.SetFacing` 写死 `FacingDirection` 枚举值（South=0/North=1/East=2/West=3），与 1 永远错位；
3. **默认状态被整条跳过**不连 AnyState——从其他朝向走回默认朝向时永远切不回来。

修复：装配器 `BuildFacingIndex` 改为**规范索引**（down=0/up=1/right=2/left=3，与枚举一致，
未收录朝向接 4 往后排）；循环动作的默认状态也连回转边（自转移由 `canTransitionToSelf=false`
拦截）；`SimplePatrol2D` 删掉 moveX/moveY 死写入（控制器里从无这两个参数）。
**已有 prefab 需重跑第 3 步重新装配**（控制器会就地重建）。

### 新增 · 键盘试走驱动（NpcKeyboardDriver）

- 第 3 步「移动方式」下拉新增**「键盘试走（WASD）」**：prefab 挂
  `Rigidbody2D + NpcKeyboardDriver`（Runtime/NpcKeyboardDriver.cs），Play 模式
  WASD/方向键移动 + 切朝向，验证各朝向帧序列的转换。
- 停下时 `Animator.speed=0` 定格当前帧当待机（可关）。
- 朝向经 `NpcCharacter.SetFacing` 走同一套规范索引——装配器与运行期从此共用一份契约。

## [0.4.9] 2026-10-14

### 修复 · keep_pose 姿势同步超时的报错可读性

- 插件第 2 步实跑第一帧即遇到 `VNCCS_PoseStudio: Pose image frontend sync timed out`
  （此前 up k8 的"确定性失败"大概率也是同一根因：批量中途 Desktop 前端掉线）。
  原文是一整坨执行历史 JSON，用户看不出动作。
- `RunWorkflowAsync` 现在识别这条报错并在异常文案后**直接拼接修复指引**：
  打开 ComfyUI Desktop 应用窗口（不是只有后台服务在跑），保持前端页面在线再重跑。
  其他报错维持原样透传。

## [0.4.8] — 2026-10-14

第 2 步帧动画**整体换路线**：Spritely 骨架整表（2509 + Nunchaku + Sheet LoRA）删除，
换成 **keep_pose 逐帧动作模仿**（VNCCS Pose Studio + SAM 3D Body）。

### 换路线的依据（实测）

双参考姿势路线在 2.1 img2img 上被系统性验证为**不可用**：照片级姿势参考无论放 image_1 还是
image_2（含槽位对调），身份图的构图引力都完全压过姿势约束，三连试点全部输出站立。
而 VNCCS Pose Studio 的「姿势图 → SAM 3D 估计 → 3D 人偶渲染 → 姿势模仿」链路实测
姿势服从度极高（left/up/down 三方向 24 帧试点 23 成功，唯一失败是前端同步超时，重试即过）。

### 变更

- **Configs**：新增 `charforge_keepose_pose.workflow.json`（用户桌面版实测件的整理版：
  去 rgthree 对比器与 PreviewAny 两个纯显示节点、filename_prefix 改 `charforge/keepose`）
  与配套 bindings——`seed=481`、`load_image_character=470`、`load_image_pose=507`、
  **`auto_prompt: true`**（提示词由 Pose Studio 前端按当帧姿势实时生成，插件不写，
  bindings 里**没有 positive 键——这是有意的**）。删除 `charforge_2509_spritely_sheet.*`
  与 `spritely_charts~` 骨架图库（160 文件）。
- **bindings 契约扩展**：`auto_prompt: true` 时 positive 允许缺失、提示词写入整体跳过。
  这是一道防静默失效的闸——auto 工作流的 positive 字段是**前端链接**，
  写字面量进去会顶掉链接，不报错、只是姿势全废。
- **Service**：新增 `ListPoseRefs()`（`pose_refs/<动作>/<朝向>/*.png` 两级查找，
  文件名排序即帧序）与 `GenerateKeepPoseFramesAsync()`（逐帧上传姿势图、整组共用 seed、
  收割后主线程做 **alpha<64 压平**——keep_pose 背景残留约 44% 半透明噪点，
  不清会污染装配侧的 alpha 包围盒）；删除 `ListSpritelyCharts` / `GenerateSpritelySheetAsync` /
  `SliceSpritelySheetAsync` / `PrepareCharacterOnWhite` / `BilinearResize` /
  `ClearWhiteEdgeFlood` 整条旧链。装配侧不受影响——`FrameNpcAssembler` 本就按
  `Frames.Length` 迭代，帧数从六变成任意值零改动。
- **Window**：第 2 步矩阵换成姿势参考库（持械/相机两级下拉删除，帧数标在格子上）；
  运行前提写进界面（vnccs-utils + LoRA + **ComfyUI Desktop 前端必须在线**——
  姿势估计要经前端同步渲染，无头后端跑不动）。
- **设置**：`SpritelyWorkflowFile` → `KeepPoseWorkflowFile`（旧键不再读取，残留值无害）。

### 迁移

- 姿势参考改放 `<工程根>/CharForge/comfyui/pose_refs/<动作>/<朝向>/k1…k8.png`
  （视频抽帧图直接可用；骨架 stick figure 未经 SAM 验证）。
- ComfyUI 端需装 `vnccs-utils` 套件与 `VNCCS_QI2_PoseStudioV1.1` LoRA；
  生成时保持 Desktop 窗口在线。报 `Pose Studio frontend sync failed` 就是这条没满足，重试即可。

---

## [0.4.7] — 2026-10-13

两个「图长出来不对」的问题，根子都在**骨架图资产本身**，不在代码：

### 修复 · sidescroller 帧里多出一个漂浮的「禁止」图标

骨架图（`spritely_charts`）的 `unarmed/*` 系列在**每格右上角画了一个黄色 🚫 图标**
（`(255,255,64)`，格内 `(424–472, 38–86)`，80 张图位置完全统一）——这是 Spritely
原素材表达「explicit no-weapon control」的方式。生成模型把图标**当画面内容照抄**，
于是每帧右上角多了个漂浮的禁止符。

**修法**：按色域精准擦除（黄环 + 2px 膨胀去抗锯齿边），20 张 `unarmed` 图标全净，
骨架肢体零误伤（`sidescroller/attack/left`、`isometric/jump` 的手臂伸进擦除窗口，
靠 `|R−G|≤40 且 B<0.45R` 的颜色规则避开——橙色 `255,165,0` 的 G 不满足条件）。
逐像素复验全库 0 残留。`.txt` 提示词里本来就有文字版约束
（`explicit no-weapon control; keep every frame completely unarmed`），语义不受影响。

### 修复 · topdown walk 生成的角色没有头

topdown 骨架图**本来就没画头节点**——俯视模板只有肩线、灰圆（俯视的头部轮廓）
和朝向三角，模型照抄骨架后头部被整体省略。给全部 20 个 topdown 配套 `.txt`
在相机短语后插入显式约束：`head clearly rendered above the shoulders with hair
and face outline, never omit or hide the head`。

### 提醒（不是 bug）

**持械角色的形象一致性差，一半原因选错了图表族**：参考图里持巨剑、却用 `unarmed`
图表，提示词 `keep every frame completely unarmed` 会跟参考图打架——模型两边都不听。
持剑/持杖/持枪角色请分别选 `sword` / `staff` / `gun` 图表族。

---

## [0.4.6] — 2026-10-13

修一处**主线程越界**崩溃（第 2 步「生成帧动画」一按就炸），并给这类错误装上会说话的守卫。

### 修复 · `SupportsTextureFormatNative can only be called from the main thread`

复现链（用户贴的堆栈）：

```
CharacterAssetService.Decode                    → new Texture2D(...)   ← 主线程 API
CharacterAssetService.PrepareCharacterOnWhite   :355
CharacterAssetService.GenerateSpritelySheetAsync :232
CharacterAssetWindow.RunFramesAsync             :582
```

根因不是「构造器写错了」——Unity 那句报错的后半截在讲**另一个场景**——而是**线程**：

```csharp
var wf = await LoadWorkflowAsync(file, ct).ConfigureAwait(false);  // ← 续体已被甩到线程池
var bind = LoadBindings(file);
byte[] prepared = PrepareCharacterOnWhite(characterPng, log);      // ← 在线程池上碰 Texture2D
```

本类第 53 行写着「内部全程 `ConfigureAwait(false)`」——这条纪律本身没错（服务层不该占主线程），
错在**忘了把纹理活送回主线程**。而项目里早就有干这件事的工具
（`EditorMainThread.RunAsync`，`SheetPacker` / `FrameNormalizer` / `MockBackend` 都在用），
只有这两处漏了。

**改法**——照既有 idiom，不发明新机制：

| 位置 | 改法 |
|---|---|
| `GenerateSpritelySheetAsync` | `PrepareCharacterOnWhite` 经 `EditorMainThread.RunAsync(...)` 封送。**调用方封送**，与旧流水线调 `SheetPacker.Pack` 同款 |
| `SliceSpritelySheetAsync` | **自封送**：公开方法只剩 `EditorMainThread.RunAsync(() => …OnMainThread(...))`，同步体重命名为 `SliceSpritelySheetOnMainThread`。与 `FrameNormalizer.NormalizeAllAsync` 同款，不给调用方留「必须记得在主线程调」的隐性契约 |

顺带一个好处：`SliceSpritelySheetAsync` 原先在已取消时会**同步抛**
`OperationCanceledException`（而不是返回 faulted Task），封送后行为与签名一致了。

### 新增 · 会说话的守卫 `EditorMainThread.EnsureMainThread(what)`

Unity 那句报错的后半截——「Constructors and field initializers will be executed from the
loading thread when loading a scene」——读起来像「构造器 / 字段初始化写错了」，
与真实原因（被 `ConfigureAwait(false)` 甩到线程池）毫无关系。为了让下一次同类越界**自己说清楚**，
在 `CharacterAssetService.Decode` 入口加了断言，不在主线程就抛：

> `CharacterAssetService.Decode（Texture2D 解码） 只能在主线程调用，当前在线程池线程上。…请把它包进 EditorMainThread.RunAsync(...)`

主线程 id 在 `EditorMainThread` 的 `[InitializeOnLoad]` 静态构造里捕获（那一刻必在主线程）。

**为什么不做成 lint 规则**：「这个调用点在不在主线程」需要全程序分析，lambda / 局部函数 /
间接调用都能绕过去，硬做必然误报——而**误报的规则没人再信**。故只在运行时拦，
并写进 `docs/unity_quickstart.md` §3 报错对照表与 §8 待办。

### 文档

`docs/unity_quickstart.md`：报错对照表新增本行；§8 新增「主线程越界只能运行时兜底」一条
（说明为什么这里没有静态规则）。`CharacterAssetService` 类文档原本自称「只做纯计算与 HTTP」，
与实际不符（PNG 编解码要借 `Texture2D`），已改成如实描述并把这条坑写进去。

一次**仓库清理**：把已下线旧流水线的 Python 侧整体退役。插件行为不变（`server/` 本就不参与当前三步流程），
但顺手照出一个「文件在本地好好的、clone 下来却没有」的静默失效。

### 删除 · `server/`（Python 服务端）

`server/` 是 v0.1「Python 是宿主」时代的编排实现，v0.2 后已不承担编排职责、也无任何 UI 入口。
本次连同它的依赖与入口一并移除：

| 删除 | 说明 |
|---|---|
| `server/`（16 个文件） | FastAPI + pydantic 的装配指令生成器 |
| `tests/test_pipeline_smoke.py` | 依赖 pydantic，本地 `pytest` 直接 collect 失败 |
| `tests/conftest.py` | 唯一作用是让 `import server` 成立，随之失去意义 |
| `scripts/run_example.py` | 依赖 `server.pipeline.orchestrator` 跑示例 |
| `pyproject.toml` 的打包段 | `charforge` CLI 入口、`include = ["server*"]`，以及 fastapi / uvicorn / pydantic / pillow / numpy 等**仅服务端用到**的依赖 |

`pyproject.toml` 退化为纯工具配置（ruff / pytest）；CI 的「服务端」job 改为
「静态检查工具（lint + 单测）」，不再 `pip install -e .`、不再 `mypy server`。

**代价要说清楚**：`server/` 曾是 `SidecarBackend`（四后端里的「整单委托型」）的服务端实现。
删除后该后端仍能编译，但已没有服务端可连。要复活这条路线，从 git 历史取：
`git log --oneline -- server/`。

### 修复 · `.gitignore` 的 `models/` 吃掉了 `server/models/`

一条本意只针对**仓库根**模型权重缓存的规则 `models/`，因为**没有前导 `/`**，
把任意层级下的同名目录一并忽略了——`server/models/` 中招：

```
.gitignore:18:models/	server/models/schemas.py
.gitignore:18:models/	server/models/__init__.py
```

后果是这两个文件**从未入库**，而 `server/config.py`、`server/pipeline/*` 全都
`from server.models.schemas import ...` —— **全新 clone 下的 `server/` 从一开始就是坏的**，
本地却因为文件一直在而毫无察觉。这是本仓库最怕的那类缺陷：能跑、但结果不对，且没人会说出来。

修法是把所有「根目录缓存」类规则锚定到仓库根（`/models/`、`/checkpoints/`、`/out/`、`/outputs/`），
并把这段教训写进 `.gitignore` 注释里。

> 这条洞在 0.4.5 之前一直在，**与本次删除无关**——只是删 `server/` 时被
> `git check-ignore -v` 顺手照了出来。

### 文档与提示文案同步

README、`docs/architecture.md`、`docs/pipeline.md`、`docs/roadmap.md`、`docs/unity_quickstart.md`、
`Configs/pose_templates/README.md` 里指向 `server/` 的活引用已全部改写。
`docs/pipeline.md` 保留为**设计记录**，但开头明说「照着敲会找不到文件」。

保留的旧流水线 C# 代码里，凡**会显示给用户的文案**也一并改了：它们原先在教用户
「执行 `python -m server.cli serve`」，而那个命令已经不存在了。留着这种提示比报错更坏——
用户会照着做、失败、然后怀疑是自己的环境问题。涉及 `SidecarBackend`
（`DisplayName` + 3 处失败说明）、`BackendProcessManager`、`CharForgeContracts`、
`CharacterForgePipeline` 的注释。

### 修复 · CI 的 lint 步骤此前是红的

`ruff check .` 在未提交的 v0.4.x 改动里积了 6 处问题，而这条 lint 一直都在 CI 里——
换句话说，CI 只要跑到这批改动就是红的（**一个红的门禁等于没有门禁**）。一并修掉：

| 文件 | 问题 |
|---|---|
| `check_unity_api_misuse.py` | `# -*- coding: utf-8 -*-` 在 Python 3 下多余（UP009） |
| `validate_unity_package.py` | 未使用的 `defaultdict` 导入（F401）、未使用的 `package_assemblies` 局部变量（F841）、两个未使用的循环变量（B007） |
| `test_csharp_lint.py` | import 块后多了一个空行（I001） |

均为纯清理：改完门禁输出与单测结果**逐字不变**（29 文件 0 问题 / 0 错 0 提醒 / 20 passed），
`ruff check .` 从 6 error 变为 All checks passed。

### 未动

C# 侧的旧流水线代码（`CharacterForgePipeline` / `PrefabAssembler` / `SheetPacker` /
`SpriteLibraryWriter` / `ProjectAssetWriter` / `Editor/Backends/`）**仍然保留**——
它们能编译、无 UI 入口，删还是接回留待后续决定。

修一处编译错误，并把它的**成因**加进静态门禁——因为这条错的报错文案会骗人。

### 修复 · CS0246：跨文件裸用嵌套类型

`ForgeArchive.WriteForge(..., ForgeResult result, ...)` 里的 `ForgeResult` 是
`PromptForgeService` 的**嵌套类**。C# 里嵌套类型在其外层类型之外必须写全限定名，
同命名空间下裸写简单名一律不认：

```csharp
public static string WriteForge(..., PromptForgeService.ForgeResult result, Action<string> log)
```

**为什么要为一次笔误写一整节**：它报的是 CS0246「找不到类型或命名空间」，
读起来像「文件没保存 / 没编译」——上一版（0.3.10）的 CS0103 就是这个形态，
当时白跑了一轮「改 → 切回 Unity → 等编译」。少一次误判就值回票价。

### 新增 · 门禁规则 10「跨文件裸用嵌套类型」

一条可静态判定的规则：收集全部 `.cs` 里**缩进恰好 8 空格**（= 外层的直接成员）
的 `class/struct/enum/interface` 声明，再检查其他文件里是否裸用了这些简单名。

只认**类型位置**，避免把撞名的字段 / 属性 / 枚举成员误判成类型引用：

| 判据 | 例子 |
|---|---|
| `new Inner(` | 构造 |
| `Inner xxx` | 类型 + 变量名 / 返回类型 + 方法名 |
| `List<Inner>` | 泛型实参 |

前置 `(?<![\w.])` 排除 `Outer.Inner`（限定写法合法）与词内子串。实测反例全部静默：
`PortraitResult`（词内子串）、`PromptForgeService.ForgeResult`（已限定）、
`int Mode = 0`（撞名局部变量）、`LocalMatting.Outcome.None`（枚举成员访问）。

配套：`tests/test_csharp_lint.py` 增 6 条单测（能命中三种类型位置 + 5 类反例不误报 +
本文件内裸用合法 + 全仓清洁）。门禁自己的规则也得有人看守。

顺带把 `nested_type_decls` 拆成「解析文本」与「读盘汇总」两层，前者才可单测。

---

## [0.4.3] — 2026-10-12

换立绘画风，并把第 1 步的判据留存下来。两件事同一动机：**让「出图不对」这句话可查**。

### 变更 · 画风：扁平赛璐璐 → 华丽精致的半写实厚涂

`PromptForgeService.StylePrefix` 改写为：

> 华丽精致的半写实厚涂画风的2D游戏角色立绘：光影层次细腻，体积感与材质感明确
> （布料、皮革、金属、木料各自成色），服饰纹样与随身饰件细节丰富，轮廓带边缘光，
> 整体华丽而有层次；不要扁平色块，不要写实照片质感。

**为什么现在敢换**：立绘与成片的画风是**解耦**的。第 2 步的 Spritely Sheet LoRA 会把角色
统一转成 `32-bit pixel art`（骨架图配套 `.txt` 里那句是固定措辞），立绘只作槽 10 的
身份参考图，它的画风根本不进成片。既然不背像素风的包袱，就没理由为了「统一」
把立绘压成扁平色块——那只会白白丢掉光影与材质的信息量。

**不能破的底线**：材质与轮廓必须可辨。发色、服饰形制、主配色要一眼读得出来——
华丽到把结构糊掉，第 2 步的 identity 参考就会走形。

同步改动：

| 位置 | 改动 |
|---|---|
| `SystemPrompt` 第 3 条 | 由「画风固定为扁平赛璐璐…」改为「画风与版式由插件注入，你只写角色本体，不要自己写画风词」——避免模型写的画风与注入的画风打架 |
| `StyleLeadin` 正则 | **删掉写死的「扁平(化)?赛璐璐(画风)?的?」，改为从 `StylePrefix` 自动派生前导短语**。写死正则换画风必漏，症状是提示词里悄悄留下两段画风描述——不报错、只是画面更糊 |
| `charforge_qwen21_txt2img.workflow.json` 节点 6 | 占位提示词同步新画风（与 `StylePrefix` + `LayoutSuffix` **逐字一致**，已用脚本比对）。它是别人打开 ComfyUI 看到的第一条范例，教错比没有更坏 |
| `charforge_qwen21_img2img.*` | 槽位指令里的具体画风名去掉，改写为中性的「与画风完全不变」——母版换过画风，写死画风名的指令就成了假话 |

### 新增 · 第 1 步留痕（`ForgeArchive`）

最终提示词是三段拼的：**你写的描述 + 模型扩写的角色本体 + 插件注入的画风件与版式件**。
混成一段之后，事后无从分辨哪句是谁写的——出图不对时只能靠猜。
所以每次「锻造提示词」落一组记录到 `<Unity 工程>/CharForge/forge_log/`：

```
20261012-235130_云叔.txt              人读：六段分开标注，一眼看出哪句是谁写的
20261012-235130_云叔.json             机器读：便于以后做提示词回归对比
20261012-235130_云叔_portrait.png     立绘（与上面同前缀，配对查看不用翻目录）
```

- **立绘也落盘了**。此前它只活在窗口那个 200×200 的预览框里——**根本看不出画风对不对**。
- **放工程根，不放 `Assets/`**：它是排查用的中间物，不是游戏资产。放 `Assets` 会被 Unity 导入
  （1024×1024 的 PNG 还要生成 `.meta`），而它永远不该进 prefab。
- **写失败不抛异常**，但一定给 ⚠ 告警：磁盘满 / 目录只读不该把已经出好的立绘弄丢，
  可静默写不进去等于让人以为「已经留了」——那比不留更坏。
- 手写提示词直接出图**不产生**提示词留痕：那种情况下提示词就在输入框里，
  没有「模型写的」与「插件套的」的分野，无需求证。

### 新增 · `PortraitResult`（立绘种子回交调用方）

`GeneratePortraitAsync` 由返回 `byte[]` 改为返回 `PortraitResult{ Png, Seed, Prompt }`。
种子此前留在服务层内部，出图不满意时无法回答「上次用的哪颗种子」——只能盲试。
现在它进日志、进留痕、进状态栏。

### 其它

- `PromptForgeService.Compose(body)` 收口三处拼接。此前 `$"{QwenAlphaHint} {StylePrefix}…"`
  在三个出口各写了一遍，改画风时差点漏掉一处。
- `ForgeResult.Body` 新增：模型（或模板）写的那截角色本体，留痕时用来分辨来源。

---

## [0.4.2] — 2026-10-12

修「锻造提示词」的两处硬伤。**触发场景**：接了一个思考型模型（Qwen3 思考版一类），
锻出来的提示词里塞进了整段思维链。

### 修复 · 思考链被当成正文拼进提示词

不少模型会把思维链**混在 `choices[0].message.content` 里**返回，形如 `<think>…</think>` 包一大段。
0.4.1 及之前是直接取 `content` 拼进提示词的，于是画风前缀后面跟着五百字的
「让我分析一下需求…让我检查是否符合要求…」，正文夹在中间——**出图自然一塌糊涂，
而日志只显示「完成」**。这正是本项目最忌讳的那类失败：看起来成功、其实没做成。

新增 `CleanBody` 清洗链，依次做：

| 步骤 | 处理 |
|---|---|
| 剥配对思考块 | `<think>` / `<thinking>` / `<reasoning>` / `<reflection>` / `<analysis>`，大小写不敏感、跨行 |
| 剥**未闭合**思考块 | 被 `max_tokens` 截断时最常见：从开标签一路吃到结尾 |
| 丢 `reasoning_content` | 有的网关把思考链单独放这个字段——不动正文，只在日志记一笔 |
| 去代码围栏 / 自贴标签 | ` ```lang `、「提示词：」「最终提示词如下：」 |
| 去重复的固定件 | 模型爱把画风前缀/版式后缀抄一遍；连「扁平赛璐璐画风的…」这种半截开头也剥 |
| 压成一行 | 换行会破坏版式，一律压成单空格 |

### 新增 · 思考块兜底摘录（**带警告**）

实测样本里，模型把最终答案**也写在思考块内部**，剥完正文就空了。此时从思考块里
挑「最长、不是小标题、长度在 20–300 字、且不含推演指纹词」的一行作为正文。
推演指纹词是一张小黑名单（用户 / 需求 / 字数 / 需要 / 让我 / 检查 / 分析 / 格式…）——
**没有它，纯推演的自述会被当成提示词发去出图**（离线样本验证时真踩到了这一步，
只按「最长的一行」挑就会命中）。

摘录命中时 `Source` 标为 `llm:<模型>·思考块摘录` 并**带上 Warning**——
摘的不是模型最终答案，必须让人看见。摘不出来就降级本地模板。

### 修复 · 版式后缀与角色描述互相打架

`LayoutSuffix` 原文是「全身立绘，正面站立，**双手自然垂放，神情平静**」。
这跟角色描述直接冲突：描述写「手持讲解册」「眼神凶，看贵族更凶」，
后缀却固定「垂放」「平静」，拼出来的提示词自相矛盾，模型只能猜。

**手势与神态属于角色内容，归描述；版式后缀只管版式。**
现改为「全身立绘，正面站立。无背景，无地面，无投影，脚下是看不见的地面。」
系统提示词同步加了一条：描述里说了就照写，没说就留白，不要替角色决定。

### 新增 · 高级设置「关闭模型思考模式」（默认关）

勾选后向网关发 `chat_template_kwargs.enable_thinking=false`，
**从源头**省掉思考链（剥洗那一步就不会被触发）。

默认关是刻意的：不是所有网关都认这个字段，严格的会因未知参数直接回 **400**，
所以不能默认发。只有确认模型把思维链混在正文里时才打开。

### 其它

- `max_tokens` 512 → **1024**。512 是给「纯提示词」留的额度，思考型模型把思维链也算进这个上限，
  常常推理到一半就被截断，正文一个字都轮不到。
- 正文超过 **400 字**判为异常（疑似把说明文字也写进来）→ 降级模板，
  **不做截断**——截哪儿都可能截断语义。日志附前 80 字供判断。
- 系统提示词第 2 条明确要求「不要输出思考过程」。
- 规则用 Python 复刻流水线做过离线验证（5 个样本：本机真实失败样本、干净正文、
  答案在前+未闭合块、纯推演、超长正文），验证脚本本身**不入库**——它是 C# 逻辑的第二份拷贝，
  留着必然腐化。

---

## [0.4.1] — 2026-10-12

修 0.4.0 引入的**编译错误**，并补回 0.4.0 重构时丢掉的**跨帧共用种子**。

### 修复 · `CS1503`：`SetNode` 收到 `int`

`CharacterAssetService.cs` 第 113 / 194 行把 `rng.Next(1, int.MaxValue)`（`int`）
直接传给了形参为 `string` 的 `SetNode`，两处均无法编译。

修法不是补一个 `.ToString()`，而是**给 `SetNode` 加 int 重载**，理由：

- `KSampler.seed` 在工作流 JSON 里是**数字**（`"seed": 12345`）。写成 `"12345"`
  就把类型改了。ComfyUI 现版本会隐式 `int()` 兜住所以照样能跑 —— 但这属于依赖
  未文档化的宽容，一旦上游校验收紧，症状是一个难查的 400，而不是一句人话。
- 再加一道**类型守门**：目标字段原本是数值、却收到字符串（或反之）时直接抛错并
  打印新旧值。以后谁再图省事写 `.ToString()`，会被当场拦下而不是悄悄改回去。

### 修复 · 帧动画**逐格掷骰子**（0.4.0 的回归）

0.4.0 的 `GenerateSpritelySheetAsync` 在**每次调用内部**取一颗随机种子，于是同角色的
行走 / 奔跑 / 攻击 / 跳跃各用一颗 —— 正是本项目反复踩的那条：**同一次任务的种子必须是同一个**。
症状不是报错，是「每个动作像不同的人」，看起来像模型不行。

- `GenerateSpritelySheetAsync` 改为**由调用方传入 seed**，方法内不再掷骰子。
- 新增 `CharacterAssetService.StableSeed(key)`：由任务钥匙派生稳定种子（可复现）。
- 窗口侧一次点击算一颗（钥匙 = `角色名@时间戳`），**该角色全部动作/朝向共用**，
  并写进日志；同角色重跑会换新的一版（与旧 `SeedFromJobId` 语义一致）。
- 立绘仍是每次一颗新种子（只出一张，不需要共用），但也**记进日志**——出图不满意时，
  可复现是排查的第一前提。

### 修复 · bindings 注释与实际不符

`charforge_2509_spritely_sheet.bindings.json`：

- `_note_seed` 声称「插件已按任务共用 SeedFromJobId」—— 0.4.0 实际上没有，属**注释说谎**；
  现改为陈述 `StableSeed` 的实际行为。
- `_note_backend` 仍在说「向导（CharForgeWizardService）不经本 bindings」——
  该类已随 0.4.0 删除，而这个 bindings 恰恰是当前唯一在用的读取方。已改正。

---

## [0.4.0] — 2026-10-12

**破坏性重构**：插件定位收敛为**三段式角色资产流水线**，旧的「三视图 → 逐件拆分 → 骨骼装配」路线整体删除。
从 0.3.x 升上来需重开窗口（菜单位置变了）。

### 新增 · 单一窗口三步流程（`Editor/UI/CharacterAssetWindow.cs`）

旧的两个窗口（`CharacterForgeWindow`、`CharForgeWizardWindow`）与它们的 UXML/USS 已删除，合并为一个：

- 菜单：**`CharForge > 角色资产流水线`**（旧路径 `Window > CharForge 角色资产生成` 不再存在）
- **第 1 步 · 角色立绘** —— 角色名 / 角色描述 → 「锻造提示词（外部 LLM）」→「生成角色立绘」，
  或「载入已有立绘…」直接指定磁盘上的透明底全身图。文生图提示词框可手工修改。
- **第 2 步 · 帧动画（Spritely 骨架图）** —— 持械 × 相机两个下拉 + 「动作 × 朝向」勾选矩阵
  （矩阵由**实际存在的骨架图文件**驱动，缺哪格显示 `—`，默认勾 `walk`）→ 一次批量生成 + 切片。
- **第 3 步 · 组装 NPC prefab** —— 贴合碰撞体 / 移动方式（无 · 巡逻）/ 巡逻速度 / 对话人设 → 组装。
- 折叠区**高级设置**：ComfyUI 地址（带「探测连通性」）、立绘与帧动画工作流文件名、输出目录、
  动画帧率、外部 LLM 三个字段。

### 新增 · 外部 LLM 提示词锻造（`Editor/Services/PromptForgeService.cs`）

- 只做 **OpenAI 兼容 `/chat/completions`** 一种协议：DeepSeek / 通义 / 智谱 / OpenAI / 本地 vLLM 都能接，
  填 BaseURL + Key + 模型名三个字段即可。
- **三项任一为空 → 自动退回本地模板拼接**，并在日志里明说本次走了「模型」还是「模板」——
  降级必须留痕，不许静默。
- 系统提示词里硬性禁止输出**年代 / 朝代 / 藏品档案 / 考古考据**：角色描述只回答「看起来是什么样」，
  知识边界不属于立绘生成，写进去只会污染提示词。
- 该服务**刻意不禁用系统代理**（与走本机的 ComfyUI 相反）——多数云端 LLM 端点需要代理才能出去。

### 新增 · 帧动画 NPC 装配器（`Editor/Services/FrameNpcAssembler.cs`）

- 两段式入口：`WriteFrames`（落帧 PNG + 算 pivot + 写导入设置）与 `Assemble`（建控制器、片段、prefab）。
  第 2 步跑完即落盘，第 3 步直接复用第 2 步的 report，不重复切图。
- **脚底 pivot 取自全部帧的并集包围盒**，一次算出、所有帧共用。逐帧各算会让走路时人物上下抖动。
- `SetNode` 硬校验目标字段**必须已存在**，否则抛出并列出该节点现有字段名——
  把「提示词写进没人读的键、静默失效」变成显式报错。
- 导入设置：Single Sprite / `SpriteAlignment.Custom` + `spritePivot = (0.5, pivotY)` / FullRect / Point / 不压缩 / 关 mipmap。
- 状态机：`facing`(int) + `isMoving`(bool) + `Play_xxx`(trigger)（trigger 名用 `SortedSet` 去重——
  同一动作多个朝向会来两条 set，撞名 `AddParameter` 会直接抛异常）。
  用 `AnyState` 而非两两互联，N 个状态只需 N 条转移；`death` 用 `IsHold` 停在末帧。
- **不删档重建**：控制器与 prefab 一律**复用 + 就地更新**（仅在控制器已损坏时才重建）。
  删了再建会换 GUID、打断场景里已拖好的实例引用与 prefab 对控制器的引用，
  并让正打开该控制器的 Animator 窗口抛 `UnityEditor.Graphs` NRE。

### 变更 · 设置项（`Editor/CharForgeSettings.cs`）

- **移除** `EraOrPeriod`、`ReferenceNotes`（年代/藏品档案——不属于立绘生成的输入）。
- **新增** `PortraitPrompt`（可复用资产：同一条提示词反复换 seed 试稿，不该每次重跑 LLM）、
  `LlmBaseUrl` / `LlmApiKey` / `LlmModel`、
  `PortraitWorkflowFile`（默认 `charforge_qwen21_txt2img.workflow.json`）、
  `SpritelyWorkflowFile`（默认 `charforge_2509_spritely_sheet.workflow.json`）、
  `SpriteFrameRate`（默认 8，与 Spritely 官方 GIF 一致）。
- `CharacterAssetService.ProbeAsync` 自带 5 秒超时：本类 HttpClient 是 `Timeout.InfiniteTimeSpan`
  （生成任务动辄几分钟，不能有超时），探测必须单独设限，否则点一下「探测连通性」会永久转圈。

### 删除 · 旧骨骼 / 三视图路线

删除前已归档到 `.workbuddy/backup/charforge-0.4.0-removed.zip`（25 个文件，60.5 KB）——
其中多数是本会话新建、尚未被 git 跟踪，直接删不可恢复，故先打包再删。

| 删除项 | 说明 |
|---|---|
| `Editor/Wizard/`（整目录） | `CharForgeWizardWindow.cs`（并入新窗口）、`CharForgeWizardService.cs`（迁为 `Services/CharacterAssetService.cs`） |
| `Editor/Wizard/CharForgePrefabBuilder.cs` | 骨骼装配器 |
| `Editor/Rigging/`（整目录） | `CharForgeGenericClips.cs` 等 |
| `Editor/UI/CharacterForgeWindow.cs` / `.uxml` / `.uss` | 旧主窗口 |
| `Configs/comfyui/charforge_qwen21_threerview.workflow.json` | 三视图生成 |
| `Configs/comfyui/charforge_qwen21_partextract.workflow.json` | 逐件拆分 |
| `Docs/psd-rigging-spec.md`、`Tools/krita/` | PSD / Krita 骨骼切分规范与脚本 |

**为什么不删不行**：三视图 + 逐件拆分意味着单角色 16 次生成，成本线性上涨而一致性收益有限；
Spritely 骨架图路线一次出一组六帧、且姿势由骨架图而非提示词锁定，
在同样的显存下把「能用」的产出效率拉高了一个数量级。留两套并行只会让人每次都要选一遍。

### 迁移 · 文件搬家保 GUID

`Editor/Wizard/CharForgeWizardService.cs` → `Editor/Services/CharacterAssetService.cs`：
**连同 `.meta` 一起移动**（不是删旧建新），GUID 不变、不触发资产重导入。
类名 `CharForgeWizardService` → `CharacterAssetService`，命名空间 → `CharForge.EditorTools.Services`。

### 修复 · Spritely 工作流双槽位值写错

`charforge_2509_spritely_sheet.bindings.json` 里 `load_image` 原为 `"11"`。
实测**角色在槽 10（image1）、骨架在槽 11（image2）**，与官方 README 相反。
改为显式 `load_image_character: "10"` + `load_image_pose: "11"`，
并同步 `_note_dual_slot` 说明。写错的表现是「角色和骨架被对调」，出图不报错但姿势全废。

### 工具 · 门禁

- `check_unity_api_misuse.py` **规则 9**（同族编号 helper 缺定义）已在 0.3.10 落地，本版沿用。
- `FrameNpcAssembler` 的嵌套类型改名为 `AssembleOptions` / `AssembleResult` / `FrameWriteReport`：
  原 `Result` / `Options` 与 `FrameNormalizer.Result` 撞名，被门禁的「命名空间.类型名」扁平比对拦下
  （嵌套类型也参与比对）。

### 已知遗留（下一步）

- `Editor/Services/CharacterForgePipeline.cs`、`Editor/PrefabAssembler.cs`、`Editor/Services/SheetPacker.cs`、
  `Editor/Integrations/SpriteLibraryWriter.cs`、`Editor/Services/ProjectAssetWriter.cs` 以及 `Editor/Backends/` 下的
  Mock / Sentis / Sidecar 后端**已无 UI 入口**（新窗口只走 `CharacterAssetService` → ComfyUI）。
  它们仍能编译、不报错，但属于死代码——留待下一版决定是删还是重新接回。
- 新路线**不生成 `*_SpriteLibrary.asset`**（`com.unity.2d.animation` 因此成为纯历史依赖）。

---

## [0.3.10] — 2026-10-12

### 修复 · 向导第 4 区编译错误 CS0103 × 5

- 报错：`CharForgeWizardWindow.cs(271,284,291,293,294) CS0103: The name 'Set4' does not exist in the current context`。
- 根因：0.3.9 给向导加 Spritely 第 4 区时，新写了 5 处 `Set4(...)` 状态栏调用，却漏掉配套的
  `private void Set4(string s) => _status4.text = s;`（`Set1/Set2/Set3` 齐、独缺 `Set4`）。字段 `_status4` 已声明，属**只缺方法体**。
- 修复：补上 `Set4`。
- 顺带修一处资源累积：六帧预览纹理原先挂在三视图共用的 `_keepAlive` 上、且 `_sprPreviewRow` 只增不清——
  重跑会残留旧帧、纹理越攒越多。改为独立 `_sprKeepAlive` + `ClearSprPreview()`（重跑前与 `OnDisable` 均清），
  与三视图预览互不干扰。

### 工具 · 门禁新增规则 9「同族编号 helper 缺定义」

- `scripts/check_unity_api_misuse.py` 新增第 9 条：声明了 Set1/Set2/Set3 却调用 Set4 这类「族内缺号」，
  本地直接拦下，不必再切回 Unity 等一轮编译看 CS0103。
- 只认**以数字结尾**的名字，因此不会把 `Texture2D` / `Vector3` / `Color32` 这类类型名误报进来；
  已自测「缺号命中 + 补齐后不命中 + 全仓 29 文件零误报」。

---

## [0.3.9] — 2026-10-12

### 新增 · Spritely 六帧动画路线进插件（2509 + Nunchaku）

- **工作流**：`Configs/comfyui/charforge_2509_spritely_sheet.workflow.json` + `.bindings.json` —— 逐节点照抄本机 ComfyUI history 里**实测跑通**的一次生成（非官方 JSON 转换）：`NunchakuQwenImageDiTLoader` + `svdq-int4_r32-…-2509-lightningv2.0-4steps`（Lightning 已融合）+ `spritely_sheet_v5` 走标准 LoRA 节点（nunchaku 新版兼容，实测可用）。槽位排布与官方 README 相反（角色=image1/槽10、骨架=image2/槽11），以实跑为准，bindings `_note_dual_slot` 有记录。
- **骨架图库**：`Configs/comfyui/spritely_charts~/sheets/` —— starter-charts 的 sheets 全量入库（160 个 png+txt，7.5 MB，全部持械/相机/动作/朝向）。目录名带 `~`：Unity 不导入、不生成 meta，纯文件系统访问；`CharForgePaths` 两级查找支持 `<工程>/CharForge/comfyui/spritely_charts~/` 用户覆盖。
- **向导第 4 区**：骨架图下拉（unarmed/sidescroller·walk·left 默认，prompt 取配套 .txt）、角色图路径（留空用第 1 步正面视图）→ 一次生成 → 3×2 切片 → 逐格体检（空格 / 背景非白专属文案）→ 6 帧存 `Assets/CharForge/<角色名>/Spritely/<动作>_<朝向>/`（Single Sprite / Point 过滤 / 关 mipmap）→ 复用 `EnsureSpriteClip`（改 internal）出 8 fps 循环 .anim 并 ping。
- **角色预处理**：`PrepareCharacterOnWhite` 对齐官方 `prepare_character.py` 的 sheet_identity（alpha 包围盒 + 长边 0.72×1024 + 白底 1536×1024，双线性）。
- **去白底**：官方 `slice_sheet.py` 的 clear_white 同款四边连通域（只清与边缘连通的近白像素，防角色身上浅色被击穿）。
- **静默失败防御**：切片按**阅读顺序**（上行 1-3、下行 4-6）——`GetPixels32` 原点在左下，直接按数组行序切会得到相位颠倒的行走循环且不报错。
- **工具修复**：`validate_unity_package.py` 的 `~` 豁免从「只查目录自身名」扩为「路径每一段」，修掉 239 条误报（目录本身被豁免、子文件全被报缺 meta）。

---

## [0.3.8] — 2026-10-12

### 新增 · 骨骼动画绑定体检（菜单 5）与场景内预览（菜单 6）

用户反馈「prefab 有了，但没绑定骨骼动画」。装配器里
`AddComponent<Animator>()` + `runtimeAnimatorController = GetOrCreateController(simple:true)`
是在 `SaveAsPrefabAsset` 之前写的，绑定路径本身存在——**误解来自两件事**：

1. **Edit 模式下 Animator 不播放**（prefab 预览窗口更不会动），看着就是静止；
2. 默认状态机播的是 **idle**，而那套 idle 是 ±1.5°~2.5° 的呼吸幅度，肉眼看不出来。

「有没有绑上」不该靠肉眼判，于是补两个菜单：

- **菜单 5 校验动画绑定**：报骨架档位、控制器档位、两者是否匹配（错档会让整档曲线
  失联，例如完整版曲线找 `torso/arm_l_upper` 而简化骨架只有 `torso/arm_l`）；
  逐状态列出 clip 的曲线数与**失联曲线**（`Transform.Find(path) == null` 的绑定），
  并同时统计对象引用曲线（避免帧动画路线被误判成「无曲线」）。控制器为空、
  控制器无状态、档位不匹配三种失败各有专属文案与处置指引。
- **菜单 6 场景内预览**：用 `AnimationMode.SampleAnimationClip` 在 **不进 Play 模式**
  的情况下循环采样 idle/walk，逐次点击切换、切完自动关闭；退出 Play 模式前自动收摊
  （AnimationMode 与 Play 模式同时活动会让层级状态打架）。

---

## [0.3.7] — 2026-10-12

### 修正 · 向导提交工作流漏了 `prompt` 封装（HTTP 400 no_prompt）

实跑第 1 步报
`ComfyUI 拒绝工作流（HTTP 400）：{"type":"no_prompt","message":"No prompt provided"}`。

`/prompt` 的 body 必须是 `{"prompt": <工作流图>, "client_id": <id>}` 这层封装，
而向导服务把工作流图**原样**当 body 发了出去，ComfyUI 在顶层找不到 `prompt` 键。
`ComfyUiBackend.SubmitAsync` 一直是带封装的，向导服务是新写的、漏了。
现已对齐同一封装格式（`prompt` + `client_id`）。

### 新增 · 切片体检（让失败自己说话）

三视图切片后逐格体检并打印一行结论，不再靠肉眼看三个小预览判断：

- `✓` 非透明像素占比与横向跨度正常；
- `✗ 空格` —— 视图没落在这 1/3 格里（模型没按等分排布）；
- `✗ 整格不透明` —— 模型没输出 alpha 通道，拆分阶段会失去遮罩；
- `⚠ 内容贴边` —— 立绘压在格线上，拆出的部件会缺一块。

第 1 步的完成提示改为「三格都 ✓ 才继续」。

### 修正 · 三视图画布 1664 → 1680（宽可被 3 整除）

1664 ÷ 3 余 2，三等分切片只能取 554/554/556，三格不等宽；
1680 ÷ 3 = 560 为整数，且 1680 与 768 均为 16 的倍数。

### 说明 · 向导「五件拆分」用词纠正为「逐件拆分」

「简化五件套」是**骨架**档位名（torso 之外挂 head/双臂/双腿 共 5 个挂件），
而第 2 步拆出来的是 **6 个部件**（含躯干），16 次生成也按 6+6+4 计。
界面原写「五件拆分」与步内文案的「正面 6 件」自相矛盾，已统一为「逐件拆分」。

---

## [0.3.6] — 2026-10-12

### 修正 · 向导第 1 步炸 UnityException：后台线程写 UIElements

实跑向导第 1 步即抛
`UnityException: get_timeSinceStartup can only be called from the main thread`，
栈顶是 `CharForgeWizardWindow.Log` 里的 `_logLabel.text = ...`。

根因是**线程归属**：`CharForgeWizardService` 内部全程 `ConfigureAwait(false)`
（HTTP 轮询留在后台线程，这本身是对的），所以传给它的 `log` 回调几乎必然
在线程池线程上触发；而 UIElements 全家只能在主线程碰。此前没炸只是因为
生成完成前的第一句日志恰好排在服务侧第一个 `await` 之后。

修法遵循「谁拥有 UI，谁负责封送」：

- `CreateGUI` 记录主线程 ID；`Log()` 发现自己不在主线程时，日志进
  `ConcurrentQueue<string>` 并挂 `EditorApplication.update` 泵回主线程再写 UI
  （`Debug.Log` 本身线程安全，仍在原线程立即输出）；
- `OnDisable` 摘掉 update 泵，避免窗口销毁后悬空回调；
- 服务层**不改**：保持后台执行，进度日志顺序不乱。

线程模型此后固定为：服务层后台（HTTP/JSON/轮询）→ 窗口层封送（UI/Texture2D/AssetDatabase）。
`SliceThreeViewsAsync` 的 Texture2D 切片因 `RunStep1` 的 await 未脱离
SynchronizationContext，天然留在主线程，无需处理。

---

## [0.3.5] — 2026-10-12

### 修正 · 编译错误 CS1061（GetEditorCurve 被当成 AnimationClip 实例方法）

`Editor/Rigging/CharForgeGenericClips.cs` 的 `EnsureClip` 里写成了
`fresh.GetEditorCurve(binding)`。`GetEditorCurve` / `SetEditorCurve` /
`GetCurveBindings` 都是 `AnimationUtility` 的**静态方法**，`AnimationClip` 上
没有这个成员，整包因此编译不过。已改为 `AnimationUtility.GetEditorCurve(fresh, b)`。

同一处顺带去掉 `existing.name = fresh.name;`：对已落盘资产改 `Object.name`
会触发资产重命名（且不报错），属于本项目最忌讳的"静默破坏"，改为不写。

### 修正 · idle 动画挂了整条臂的无效曲线

`BuildIdleClip` 原先无条件给 `torso/arm_l_upper` / `torso/arm_l` 都加曲线，
两档骨架各有一半曲线指向不存在的骨骼（不报错但白写）。改为 if/else 按档绑。

### 说明

`EnsureClip(overwrite: true)` 的就地刷新（`ClearCurves` + 逐条 `SetEditorCurve`）
本身是对的——保住 GUID 才能让控制器里的 motion 引用不悬空，本次只是修 API 调用形式。

---

## [0.3.4] — 2026-10-12

### 新增 · Qwen i2i 双参考槽位（母版 + 骨架）

`TextEncodeQwenImage21` 的参考图本就是多槽位（image_1…image_16）。
i2i 工作流新增节点 10（第二个 LoadImage）接入 `images.image_2` 作姿势骨架槽：

- 节点 1（image_1）= 角色母版，**插件不再改写**，文件须在 ComfyUI input 目录；
- 节点 10（image_2）= 姿势骨架，`load_image` 绑定改指它，逐帧注入
  `pose_templates/<方向>_<动作>_<帧号>.png`；
- 新增 bindings 可选键 `prompt_suffix`：插件覆写 prompt 后不含槽位指令，
  由它固定追加「保持 image_1 / 按 image_2」两句（`WorkflowBindings.PromptSuffix`）。

若姿势服从度不足，可对调两槽接线让初始潜空间跟随骨架几何（一行 JSON 实验）。

### 修正 · 插件覆写提示词丢失 alpha 句式

插件逐帧用 `BuildPositivePrompt` 的产物**覆写**工作流节点 6 的默认提示词，
而官方 alpha 句式（"This is an RGBA image with transparency…"）只写在
工作流默认值里——覆写后 Qwen 收到的指令不含 alpha 要求，输出全不透明
（实测四角 alpha=255），只能依赖 `LocalMatting` 兜底抠图。现已把
alpha 句式并入 `BuildPositivePrompt` 尾部；对 SD1.5 路线无副作用。

### 修正 · 配置定位崩溃 + 包路径解析兜底

实测运行中流水线在起步阶段中断：`ArgumentException: Illegal characters in path`，
栈指向 `CharForgePaths.DescribeLookup`。根因有两层：

- **崩溃（表层）**：包定位失败时，占位文本 `<插件包>/Configs` 里的 `<` `>` 是
  Windows 非法路径字符，被直接喂给 `Path.Combine` → 抛异常 →
  一条本该温和的「术语表未找到」告警升级成整条流水线中断。
  现已改为 `SafeJoin`（路径拼接失败时退化为纯字符串拼接），告警文案永远能生成。
- **术语表静默找不到（里层）**：`PackageInfo.FindForAssembly` 在该环境未解析出
  包路径，`Configs/` 整条定位失效（61 条词条的术语表就在包里，却报未找到）。
  新增**编译期兜底**：`[CallerFilePath]` 把本文件源码绝对路径嵌进程序集，
  往上三级即包根，并用 `Directory.Exists` 验证后才采用——不依赖 PackageInfo，
  file: / 嵌入 / PackageCache 三种形态均覆盖。
- **主线程违规（换真后端后暴露）**：流水线全程 `ConfigureAwait(false)`，
  占位后端的任务是同步完成的，整条链内联在主线程上跑，Unity API 调用从不出事；
  ComfyUI 后端是真异步，续体被甩到线程池，`LocalMatting` 解码 PNG 时建
  `Texture2D` 直接踩 `SupportsTextureFormatNative can only be called from the
  main thread`。该调用改经 `EditorMainThread.RunAsync` 封送
  （`FrameNormalizer` / 打包 / 装配此前已在用同一机制，仅此处遗漏）。

---

## [0.3.3] — 2026-10-12

### 新增 · Qwen-Image-2.1 图生图（Image Edit）工作流，锁定角色一致性

官方模板「Qwen-Image-2.1 Image Edit」导出的 API 格式（用户提供）整理为
`charforge_qwen21_img2img.workflow.json` + 同名 bindings。生成与编辑**共用同一套权重**，
不需要下载任何新模型。

- **参考图通道**：`LoadImage` → `TextEncodeQwenImage21` 的 `images.image_1` 槽位；
  插件把「姿势模板」字段里的图经 `/upload/image` 逐帧重写节点 1（复用 v0.3.0 的
  `load_image` 绑定消费点，C# 侧零改动）。
- **构图锁定原理**：编码节点输出 2 是跟随 `image_1` 几何的初始潜空间，
  KSampler 的 `latent_image` 直连该输出（官方经 `ComfySwitchNode` 切换，本工作流
  恒取「跟随参考图」一档）；`resolution=0` 表示参考图按原尺寸参与编码。
- **剔除 9 个节点**：PE 分支（`TextGenerate` + PE 9B `CLIPLoader` +
  `PrimitiveStringMultiline` + `PreviewAny` + `BatchImagesNode` +
  prompt 切换节点）+ latent 开关 + `EmptyLatentImage` + `ImageCompare`，
  18 → 9 节点。与 t2i 版同口径：PE 会改写中文考据词项，16 GB 上不可常驻。
- **校验**：与官方导出件逐节点逐字段比对通过（白名单差异仅 prompt / seed /
  filename_prefix / 参考图默认名四处）；无悬空引用、全部可达、bindings 自洽。

---

## [0.3.2] — 2026-10-12

### 修正 · Qwen 工作流改用官方模板实测版为准，并补回漏掉的缓存节点

0.3.1 的 `charforge_qwen21_txt2img.workflow.json` 是从官方模板的 subgraph
**手工展开**的，事后比对发现两处与官方不一致：**漏掉了 `QwenImage21Cache` 缓存节点**，
且宽高是写死在 `EmptyLatentImage` 上的字面量。这次以 ComfyUI 官方模板导出的
API 格式（已在本机 RTX 4070 Ti SUPER 16 GB 上实测出图）为准重做。

- **补回 `QwenImage21Cache`**：官方链路是 `UNETLoader → QwenImage21Cache → KSampler`，
  此前那份是 `UNETLoader → KSampler` 直连。
- **尺寸改由 `ResolutionSelector` 提供**（`1:1` / `1 MP` → 1024×1024），
  `EmptyLatentImage` 的宽高改为**链接**。这让「不要绑 `width` / `height`」
  从一条建议变成硬约束：绑定会替换链接，把尺寸压回插件默认的 512。
- **剔净提示词增强器（PE）分支**：官方模板带 `TextGenerate` + 9B `CLIPLoader` +
  `PrimitiveStringMultiline` + `ComfySwitchNode` 一整段。实测通过时其开关为 `false`，
  PE 从未参与出图；保留只带来两个负担——`/prompt` 会校验全图节点
  （删掉 PE 模型文件即报 `value not in list`），以及开关被误触时直接爆显存。
- **节点重编号为 1–10**（官方导出件的 id 形如 `459:452`，是 subgraph 展开残留），
  绑定文件因此可读；输出前缀改为 `charforge/qwen21`。

改动经脚本**逐节点逐字段**比对验证：生成链（`UNETLoader` / `CLIPLoader` / `VAELoader` /
`TextEncodeQwenImage21` / `EmptyLatentImage` / `KSampler` / `VAEDecode` / `SaveImageAdvanced`
的 `class_type` 与全部 `inputs`，含节点间连线）与官方导出件**完全一致**，
差异仅为上述三处刻意改动；无悬空引用、无不可达节点、`bindings` 与工作流自洽。

### 说明 · 为什么不用官方的提示词增强器

官方模板里的 PE 是**另一个 9B 模型**（`qwen3.5_9b_qwen_image_2.1_pe_t2i`），
Comfy-Org 官方只发布了 `int8_convrot` 一档。本工作流不使用它，理由有两条：

1. 它的 system prompt 明确要求**把用户请求改写成一段英文描述**。而文博场景的核心信息
   恰恰是「南宋」「龙泉窑青釉」「开片」这类中文考据词项——交给一个「改写并英译」的模型，
   丢掉的是术语精度。Qwen3-VL 文本编码器本身支持中文直入，没有绕这一圈的必要。
2. 16 GB 显存不允许主链路（13.3 GiB）与一支 9B 编码器同时常驻。

---

## [0.3.1] — 2026-10-11

### 承载第二条生成路线（Qwen-Image-2.1），并修掉两处只在换路线时才会暴露的缺陷

**1. 换上 Qwen 工作流后，提示词会「静默丢失」。**

`WorkflowBindings.SetText` 把字段名**写死**成了 `inputs["text"]`。
SD1.5/SDXL 的 `CLIPTextEncode` 确实叫 `text`；但 Qwen-Image 2.1 的
`TextEncodeQwenImage21` 用的是 `prompt` / `negative_prompt`，**而且正向与负向
挂在同一个节点上**。往一个没有 `text` 键的 `inputs` 里赋值，Json.NET 不会报错——
它会安静地**加一个新字段**，ComfyUI 读不到、直接忽略。于是提示词一个字都没进模型，
日志里只有一行「生成完成」。症状是「出图跟角色描述毫不相干」，
看起来像模型不行或提示词写得不好，实际是接线问题。

修法三处：

- 字段名改为**候选探测**：正向 `text → prompt → prompt_text`，
  负向 `text → negative_prompt → prompt`，命中即写；绑定文件可用
  `positive_input` / `negative_input` 显式指定，优先级最高。
- 启发式不再只认 `CLIPTextEncode`，改为认所有 `class_type` 含 `TextEncode` 的节点，
  并额外识别「单节点同时持有正负提示词」这一形态（Qwen 就是这样，
  此时正向取 `prompt`、负向取 `negative_prompt`）。
- `SetText` 改为返回 bool，`Apply` 改为返回告警列表：**写不进去会被明确报出来**，
  而不是像以前那样无声无息。一个把提示词丢掉却保持沉默的后端，
  比一个报错的后端难查得多。

**2. 10 个 `.meta` 的 importer 段换行是坏的。**

上一版批量生成 `Configs/` 下的 `.meta` 时把 `\n` 写成了**字面量** `/n`，
于是文件里成了 `TextScriptImporter:/n  externalObjects: {}`。
文件看着"有内容"，Unity 却解析不出导入设置段——这类问题不会让任何东西立刻报错，
只会让资产的导入参数悄悄丢掉、或在别人的工程里拿到另一个 GUID。

已全部修复，并新增门禁第 8 条（`check_meta_files`）：每个资产都必须有 `.meta`、
importer 段必须真的换行、`guid` 必须是合法的 32 位十六进制。

**3. 新增 Qwen-Image-2.1 路线（并行，非替代）**

- `Configs/comfyui/charforge_qwen21_txt2img.workflow.json`
  —— 从**官方模板的 subgraph 展开**成的扁平 API 格式。官方模板把整条生成链包在
  一个 subgraph 节点里，而 **subgraph 无法直接提交给 `/prompt`**，硬提交只会得到
  一个含糊的报错；这是它和 SD1.5 工作流最大的形态差异。
  其中的提示词增强器已剔除——那是**另一个 9B 模型**，官方默认开关是关的。
- `charforge_qwen21_txt2img.bindings.json` —— **刻意不绑 `width` / `height`**：
  节点保持 1024×1024，缩放交给 `FrameNormalizer`。因为 512×512 只有 26 万像素，
  低于 Qwen 建议的 100 万像素下限一个数量级，逼它出 512 会明显掉质量。
- `Configs/comfyui/qwen_image_21.md` —— 16 GB 档位的权重组合（关键：**7B 主干不是全部，
  文本编码器是另一个 8B 模型**，BF16 下两者相加 30.8 GB 根本进不去 16 GB）、
  许可（Qwen Research License，**仅限非商用**）、采样参数（`cfg=1.0`，
  此档位下负向提示词不生效属正常）、以及**它现在还做不了四向一致性**
  （Fun-ControlNet-Union 的 ComfyUI 支持仍在 PR review 中）这一硬限制。

原生 RGBA 与现有链路是干净对接的：`LocalMatting` 检测到图里已有 alpha 会**原样放行**，
不会去覆盖掉模型自己产出的透明度。

门禁：pytest 18/18、API 误用 0（25 个文件）、结构 0、示例 2/2。

---

## [0.3.0] — 2026-10-11

### W2 · 接通真实生成链路，并补掉四处「不报错但结果不对」的断点

准备接本机 ComfyUI 时把整条链路重新走了一遍，发现四处**此前从未被触发、因此从未暴露**
的问题。它们的共同特征是：不抛异常、不报编译错误，只是产出一个「看起来不对」的结果。

**1. 每帧一个随机种子——四向一致性的头号杀手。**
`FrameRequest.Seed` 从未被赋值（默认 -1），后端于是每帧现取一个随机种子。
16 帧 = 16 个不同的人，而症状（「四个人」）看起来像模型不行。
现在同一任务共用一个种子（`SeedFromJobId`，由 jobId 派生，可复现）；
各帧的差异**只应来自姿势约束**——这正是把 ControlNet 放在首位的原因。

**2. `load_image` 绑定解析了却没有任何消费点——姿势注入是死的。**
`WorkflowBindings.Load` 读了 `load_image`，`Apply` 却没碰它，`UploadImageAsync`
写好了也从未被调用。后果：无论用户准备多少骨架图，ControlNet 拿到的永远是工作流里
写死的那一张，症状表现为「四向姿势锁不住」，实为**根本没接上**。
现在 `ApplyPoseTemplateAsync` 会把 `<Unity 工程>/CharForge/pose_templates/{direction}_{action}_{index:00}.png`
经 `/upload/image` 传过去并改写 LoadImage 节点的文件名；缺图时明确告警并给出期望路径。

**3. 扩散模型出的是全不透明图——抠图缺失会让碰撞体盖住整个屏幕。**
提示词里的 `transparent background` 只是一句话，SD 不会真的产生 alpha。
而下游 `FrameNormalizer` 恰恰靠 alpha 裁切并反算角色包围盒：拿到不透明图时
包围盒 = 整幅画布，碰撞体覆盖全屏，**且不报任何错**。
新增 `LocalMatting`：四角中位数取背景色 → **从四边做连通域**判定背景
（只把连到画外的部分判为背景，因此青灰长衫对灰底、青碧釉色对绿底这类
「角色身上的相近色」不会被击穿成空洞）→ 输出带 alpha 的 PNG。
质量低于 rembg，已在告警里说明；背景占比不足 2% 时判定为「无纯色背景」并拒绝键控，避免毁图。

**4. 中文提示词整段被丢弃——文博场景的核心信息进不了模型。**
SD/SDXL 的文本编码器用英文 CLIP 词表，**汉字不入模型**（不是翻错，是压根没有）。
而决定画面内容的恰恰是「南宋」「龙泉窑」「青灰色长衫」这些中文词；不翻译的话，
真正进模型的只剩画风预设里那几句英文标签，产出会是个与文博无关的随机角色。
新增 `packages/com.charforge/Configs/prompt_glossary.json`（术语表，数据而非代码，按词条长度倒序应用）
+ `PromptGlossary.TranslateCard`；翻译后仍残留的中文会被明确列为告警，
提示补词条或改用英文描述。

### 接口变更

- `IInferenceBackend` 新增 `IReadOnlyList<string> DrainWarnings()`。
  后端自己知道、流水线不知道的问题（姿势模板缺失、工作流里找不到 load_image 节点）
  必须能浮到窗口日志里——丢掉它，用户只会看到「一致性很差」却查不到原因。
  四个后端均已实现，不产生告警的返回空集合。
- `FrameRequest` 新增 `PoseTemplatePath`。

### 新增配置（开箱可用）

全部位于**插件包内**的 `Configs/`：

- `Configs/comfyui/` —— 两份可直接导入的 API 格式工作流（SD1.5 txt2img 起步版、
  SD1.5 + ControlNet OpenPose 版），**只用核心节点，不需要装任何自定义节点**；
  同名 `bindings.json`；README 含显存分档参数建议与失败对照表。
- `Configs/prompt_glossary.json` —— 文博术语中英对照（年代 / 器物 / 服饰 / 配色 / 身份）。
- `Configs/pose_templates/README.md` —— 骨架图命名约定与制作方法。

### 配置路径：为什么不能放在仓库的 `configs/` 下

本轮一开始把配置放在仓库根的 `configs/`，随后发现**这在编辑器里永远解析不到**：
Unity 的 `Application.dataPath` 指向 `<Unity 工程>/Assets`，而插件包是另一棵树
（`<仓库>/packages/com.charforge`，或 PackageCache 里的展开副本），两者没有共同祖先目录。
放错位置不会报错，只会表现为「配置静默不生效」——正是本项目已经反复栽过的那类问题。

因此新增 `Editor/Util/CharForgePaths.cs`，统一按两级查找，先命中者胜：

1. `<Unity 工程>/CharForge/<相对路径>` —— 用户覆盖（自己的姿势模板、改过的术语表）；
2. `<插件包>/Configs/<相对路径>` —— 随包默认。

包内路径用 `PackageInfo.resolvedPath` 反查，因此 Add from disk / 嵌入 / 注册表三种安装
形态都能定位（写死 `Packages/com.charforge` 会在后两种形态下失效）。
查不到文件时**返回 null 而非抛异常**，由调用方转成一句可读告警并给出两处期望位置。

### 静态检查

- 新增**规则 7「括号不配平」**。纯手写 C# 时手滑删/多一个括号，Unity 往往报在文件末尾，
  离真正出错处很远。配合 `blank_strings`（剥离字符串字面量）做结构检查，零误报。
- `tests/test_csharp_lint.py` 扩到 9 个用例，覆盖两条新规则的正例与反例，
  并断言真实仓库始终为零命中。

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
