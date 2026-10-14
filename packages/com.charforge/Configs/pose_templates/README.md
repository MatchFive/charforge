# 四向 × 动作 ControlNet 姿势模板

## 为什么需要它

四向一致的**关键不是提示词，是姿势约束**。

同一句提示词分别生成「正面 / 背面 / 侧面」，模型会给出四个身材比例都不一样的人。
但只要给 ControlNet 喂上四个方向的**同一套骨架图**，身体比例与姿态就被锁住了，
剩下的差异只在视角——这正是我们要的。

## 文件放在哪

插件按**两级顺序**查找，先命中者胜：

```
1) <你的 Unity 工程根>/CharForge/pose_templates/     ← 优先，你放这里
2) <插件包>/Configs/pose_templates/                  ← 随包默认（本目录）
```

> 注意是**工程根**（`Assets` 的同级目录），不是 `Assets/` 里面，也不是本仓库。
> 插件包和 Unity 工程是两棵不同的目录树，把骨架图放在仓库的 `configs/` 下
> 编辑器**永远解析不到**，而且不会报错——只会表现为「姿势没锁住」。

## 文件命名约定

```
<Unity 工程根>/CharForge/pose_templates/
├── south_idle_00.png ~ south_idle_03.png     # 正面待机 4 帧
├── south_walk_00.png ~ south_walk_03.png     # 正面行走 4 帧
├── north_idle_00.png ...
├── north_walk_00.png ...
├── east_*.png
└── west_*.png
```

- 骨架图必须是 **OpenPose 风格**（彩色关节点 + 连线），或纯剪影（配合 Depth/Canny 模型）。
- 尺寸建议与生成画布一致（骨架 512×512 → 输出 512×512）。
- 每一帧的角色**脚底位置必须一致**——否则归一化的统一缩放会被误导，出现「走路时角色忽大忽小」。

## 怎么准备（推荐顺序）

### 1. 直接用现成的（最快）

到 Civitai 或 Hugging Face 搜 `openpose sprite sheet` / `openpose walk cycle`，
能找到大量现成的四向行走骨架图。选一套分辨率与你的画布匹配的。

### 2. 用 Mixamo 自制（最可控）

这是社区里最通行的做法，也最值得你做一遍：

1. 在 Mixamo 上传一个简易人形（或用自带角色），选 Idle / Walking 动作；
2. 导出 FBX（记得勾选 with skin）；
3. 在 Blender 里导入，配置正/背/左/右四个相机，按帧渲染成 PNG；
4. 用 ControlNet 的 `openpose` 预处理器（或 `dw_openpose`）把渲染图转成骨架图；
5. 按上面的命名约定放进本目录。

> 好处：你能精确控制「每帧的脚底位置」和「四个相机是否严格正交」，一致性最好。

### 3. 画剪影（像素风首选）

像素风不适合 OpenPose 骨架（骨架太细，映射到低分辨率会糊）。
改用**纯色剪影 + Canny/Depth 模型**效果更好：
自己画 64×64 的黑白剪影，然后按同样的命名放进来。

## 与生成端的对接

> ⚠️ 本节原名「与 Python 侧的对接」。它描述的那个调用方——`server/pipeline/generate.py`——
> 已于 **v0.4.5** 随服务端整体删除。**当前读取本目录的只有 Unity 侧代码。**

读取入口是 `Editor/Services/CharacterForgePipeline.cs`：它按 `{direction}_{action}_{NN}`
拼出文件名，再交给 `CharForgePaths.ResolveFile("pose_templates/<名字>.png")` 做**两级查找**：

```
1. <Unity 工程根>/CharForge/pose_templates/<名字>.png   ← 你自己的骨架图放这里（优先）
2. <插件包>/Configs/pose_templates/<名字>.png            ← 随包默认
```

命中后由 `ComfyUiBackend` 上传给本机 ComfyUI，作为 ControlNet 的 `LoadImage` 输入。

> 本目录属 **SD1.5 + ControlNet** 路线的姿势骨架；当前三步流程的帧动画走的是
> keep_pose 动作模仿，姿势参考库在 `Configs/comfyui/` 旁的 `pose_refs/<动作>/<朝向>/`，两者不要混。

骨架图缺失时**不要静默跳过**——`ComfyUiBackend` 会显式告警（同系列多帧只报一次）。
但要注意：**结果对不对不能靠「没报错」推断**——缺图时各帧会沿用工作流里写死的那张，
四向姿势根本没被锁住，画面照样出得来，只有告警里那一行字能告诉你。
