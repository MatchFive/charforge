# 四向 × 动作 ControlNet 姿势模板

## 为什么需要它

四向一致的**关键不是提示词，是姿势约束**。

同一句提示词分别生成「正面 / 背面 / 侧面」，模型会给出四个身材比例都不一样的人。
但只要给 ControlNet 喂上四个方向的**同一套骨架图**，身体比例与姿态就被锁住了，
剩下的差异只在视角——这正是我们要的。

## 文件命名约定

```
configs/pose_templates/
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

## 与 Python 侧的对接

`server/pipeline/generate.py` 的 `_generate_local` 需要在生成每一帧时把对应模板传进
`control_image` 参数：

```python
control = settings.pose_templates_path / f"{direction.value}_{action.value}_{i:02d}.png"
_generate_local(positive, negative, out_path, control_image=str(control), seed=seed, size=size)
```

骨架图缺失时不要静默跳过——要么显式报错，要么明确记录到 `warnings`。
静默跳过会导致「部分帧有一致性、部分帧没有」，这种问题在打包后极难排查。
