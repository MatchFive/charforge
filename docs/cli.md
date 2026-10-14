# CharForge 命令行入口（无头驱动）

> 什么时候读这一篇：你要**用程序**（Agent / CI / 批处理脚本）驱动 CharForge，
> 而不是用手点窗口。窗口那三步的操作说明在 [`unity_quickstart.md`](unity_quickstart.md)。

---

## 1. 为什么会有这个入口

插件是 Unity 编辑器扩展，"唯一控制面"是 `CharForge > 角色资产流水线` 那个窗口。
但三步里有一步半是**确定性的**：

| 步骤 | 确定性 | 能不能无人值守 |
|---|---|---|
| ① 立绘 | 依赖模型采样，同一提示词换 seed 就换一张 | 要看图才知道对不对，需要人 |
| ② 帧动画 | 依赖模型采样，但姿势由参考图锁死 | 出图后要过 alpha 体检，**机器能判** |
| ③ 装配 prefab | **纯计算，无任何模型调用** | 完全可以 |

第 ③ 步"同一批帧跑两次必须得到同一个 prefab"——这是它被刻意设计成不含模型调用的原因，
也是它最该被脚本化的理由。CLI 就是这个出口。

**边界说清楚**：CLI **不生成图片**。出图仍然走 ComfyUI（本机 HTTP 服务，
`localhost:8000`），那一段由调用方自己发请求。CLI 管的是「Unity 侧」那半截：
勘察环境、把帧装配成 prefab、验证片段里真有帧、把脚本挂上去。

---

## 2. 调用形态

```bash
Unity.exe -batchmode -projectPath <Unity 工程> \
  -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run \
  -cmd <命令> [参数...]
```

Windows 上的真实路径形如：

```bash
"C:/Program Files/Unity/Hub/Editor/6000.0.58f1/Editor/Unity.exe"
```

### 三条硬规矩

1. **不要加 `-quit`。**
   `-quit` 会让 Unity 在 `executeMethod` 返回后立刻退出，并把进程退出码**固定成 0**。
   于是「装配失败」和「装配成功」在调用方眼里一模一样 —— 这是最坏的一类错。
   退出码由 CLI 自己给。
2. **退出码是唯一可信的成败信号**：

   | 退出码 | 含义 |
   |---|---|
   | `0` | 成功 |
   | `1` | 失败（帧没找到 / 装配没出 prefab / 自检发现空片段 / 脚本类型找不到…） |
   | `2` | 用法错（命令名拼错、缺 `-character` 这类必填参数） |

   注意：**脚本编译不过时，Unity 根本不会执行 `executeMethod`**，
   此时退出码是非 0 但不一定是 1 —— 所以判据是「`== 0` 才算过」，不要写「`!= 1` 就算过」。
3. **第一次跑会慢。** Unity 要启动、导入资产、编译脚本，几十秒是正常的。
   不要在 10 秒就判超时。

### 在打开着的编辑器里手动跑

不带 `-batchmode` 时 CLI 只打日志、**绝不关闭编辑器**（没人希望点一下菜单就丢未保存的场景）。
日志前缀统一是 `[CharForge CLI]`，机器可读的块用 `CHARFORGE_*_BEGIN` / `_END` 包起来。

---

## 3. 命令参考

### `-cmd env` —— 先跑这个

打印本次运行的**环境事实**。任何一端都不该猜路径，让机器把事实说出来。

```
CHARFORGE_ENV_BEGIN
project_root=F:\unity_project\plugin
user_root=F:\unity_project\plugin\CharForge
package_config_root=C:\...\unity2d-charforge\packages\com.charforge\Configs
comfyui_url=http://localhost:8000
portrait_workflow=charforge_qwen21_txt2img.workflow.json
keepose_workflow=charforge_keepose_pose.workflow.json
output_folder=Assets/CharForge
sprite_frame_rate=8
pose_refs_lookup=F:\...\CharForge\comfyui\pose_refs（用户覆盖）或 <插件包>/Configs/comfyui/pose_refs（随包默认）
pose_refs_total=4
pose_ref=walk/up frames=8
pose_ref=walk/down frames=8
pose_ref=walk/left frames=8
pose_ref=walk/right frames=8
CHARFORGE_ENV_END
```

`pose_refs_total=0` 表示姿势参考库没找到 —— 第 2 步整片会是 `—`，
**不要去调提示词**，先把姿势图放对位置（严格两层：`<动作>/<朝向>/*.png`）。

### `-cmd scan` —— 看每个角色走到哪了

```
CHARFORGE_SCAN_BEGIN
character=yunshu dir=Assets/CharForge/yunshu groups=4 frames=32 prefab=yes controller=yes persona=yes
  group=walk_down frames=8 anim_keys=8 ok=yes
  group=walk_left frames=8 anim_keys=0 ok=no
characters=1
CHARFORGE_SCAN_END
```

`ok=no` 的那一行就是问题所在：帧 PNG 有 8 张，但片段里 0 条关键帧 ——
**动画播不出来且不报错**，正是这个形态。修法是重跑一次 `assemble`。

### `-cmd assemble` —— 装配 prefab

```bash
-cmd assemble -character yunshu \
  [-movement patrol|manual|none] \
  [-collider true|false] \
  [-fps 8] \
  [-dialogue true|false] \
  [-speed 1.5] \
  [-folder Assets/CharForge] \
  [-prompt "<文生图提示词原文>"]
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `-character` | **必填** | 产物目录名、prefab 名、控制器名、`characterId` 共用它。**建议拼音或英文**（中文进文件名在跨平台 git / CI 下会出问题） |
| `-movement` | `none` | `patrol` = 加 `Rigidbody2D` + `SimplePatrol2D`；`manual` = 加 `Rigidbody2D` + `NpcKeyboardDriver`（Play 模式 WASD 试走）；`none` = 纯静态 |
| `-collider` | `true` | 胶囊体尺寸由帧的实测像素包围盒反算，不用手填 |
| `-fps` | 取设置里的值 | 片段的采样帧率 |
| `-dialogue` | `true` | 写进 `NpcCharacter.dialogueEnabled` |
| `-speed` | `1.5` | 移动速度（仅 `patrol` / `manual` 有意义） |
| `-folder` | `Assets/CharForge` | 产物根目录 |
| `-prompt` | 空 | 留痕用，写进 prefab 旁的记录 |

**它会先 `LoadFrameSetsFromDisk` 从 `Frames/` 重建帧序列**——
所以不需要先开窗口跑第 2 步，也不需要内存里有任何东西。
帧还在磁盘上，装配就永远能重来。

**装配成功后自动跑一次 `verify`**：有空片段就退出码 1，不会把坏产物当成功交付。

成功时输出：

```
CHARFORGE_RESULT ok=1 prefab=Assets/CharForge/yunshu/NPC_yunshu.prefab dir=Assets/CharForge/yunshu
```

### `-cmd verify` —— 数片段里到底有没有帧

```bash
-cmd verify -character yunshu [-folder Assets/CharForge]
```

判据是**两次独立计数**：帧数数 `Frames/<动作>_<朝向>/frame-*.png` 的张数，
关键帧数读 `<动作>_<朝向>.anim` 里的对象引用曲线点。

```
CHARFORGE_VERIFY_BEGIN
character=yunshu groups=4 frames=32
  group=walk_down frames=8 anim_keys=8 ok=yes
bad_groups=0
CHARFORGE_VERIFY_END
```

另一种更"离线"的等价检查在仓库脚本里：
`python scripts/check_anim_clips.py <Unity 工程>/Assets/CharForge/<角色名>`
（直接读 `.anim` 的 YAML，不需要 Unity 进程）。两者数的是同一件事。

### `-cmd attach` —— 挂自定义组件

```bash
-cmd attach -character yunshu -script MyGame.YunshuGreeter -script MyGame.NpcInteractable
```

- `-script` 传**类型全名**（含命名空间），不是脚本路径。
  找不到类型就一定是**没编译通过** —— 这个区分比路径更有诊断价值。
- 已经挂过的不重复挂，重复执行同一命令安全。
- 本命令**不会替你触发重编译**：先让 Unity 把脚本编译过（开一次编辑器，或跑一次
  `-cmd scan` 让它启动导入），再挂。
- 组件必须继承 `Component`（`MonoBehaviour` 自然满足）。

---

## 4. 接线示例

### 一条链跑完（帧已经在磁盘上的情况）

```bash
UNITY="C:/Program Files/Unity/Hub/Editor/6000.0.58f1/Editor/Unity.exe"
PROJ="F:/unity_project/plugin"

"$UNITY" -batchmode -projectPath "$PROJ" \
  -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd env
echo "退出码 $?"     # 0 才继续

"$UNITY" -batchmode -projectPath "$PROJ" \
  -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd assemble \
  -character yunshu -movement manual -collider true
echo "退出码 $?"     # 非 0 就停，别往下走

"$UNITY" -batchmode -projectPath "$PROJ" \
  -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd verify -character yunshu
```

### 批量：一个循环装 N 个角色

```bash
for name in yunshu jianke daoyou; do
  "$UNITY" -batchmode -projectPath "$PROJ" \
    -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd assemble \
    -character "$name" -movement patrol
  if [ $? -ne 0 ]; then echo "!! $name 装配失败，停"; exit 1; fi
done
```

（每个角色一次进程启动确实笨，但换来的好处是**失败边界干净**：
上一次的编辑器状态不会污染下一次。真嫌慢再说批内循环。）

### CI：产物自检

```bash
"$UNITY" -batchmode -projectPath "$PROJ" \
  -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd verify -character yunshu
# 非 0 → 构建失败（片段空 = 动画播不出来，这种产物不该进版本库）
```

---

## 5. 与窗口的关系

**两者不冲突，读写的是同一批磁盘产物。**

- 窗口里的"生成勾选的帧动画"把 PNG 写进 `Frames/`；CLI 的 `assemble` 读的就是它。
- 反过来也一样：CLI 装配完，打开窗口能看到 prefab 和片段（窗口重启时会从磁盘重建帧序列）。
- 唯一要注意的是 **`.anim` / `.controller` / `.prefab` 的写入是就地覆盖**，
  同一时刻别让窗口和 CLI 同时写同一个角色。

窗口里那些**只能人判断**的部分（提示词写得好不好、出图姿势对不对）
不会因为有了 CLI 就自动化 —— CLI 只是把"必须有人在"的地方缩到最小。
