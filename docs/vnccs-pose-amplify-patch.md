# vnccs pose_amplify 服务端补丁（CharForge 侧）

**状态**：已应用（v0.5.13，2026-10-14）。**文件不在本仓库内** —— 它改的是 ComfyUI
安装目录下的第三方插件，所以把补丁原文备份在此，vnccs 升级覆盖后可按本文恢复。

## 打在哪

```
<ComfyUI>/custom_nodes/vnccs-utils/nodes/pose_studio.py
```

- 应用位置：`VNCCS_PoseStudio._apply_pose_image_via_frontend()` 里，
  `PromptServer.instance.send_sync("vnccs_apply_sam3d_pose", event_payload)` **之前**。
  即：对「服务端广播给前端的人偶驱动载荷」做放大，而不是对 pose_data 提交回节点的数据。
- 读的开关：`pose_data.export.pose_amplify`（由 CharForge 插件按方向写入，见
  `Configs/comfyui/charforge_keepose_pose.bindings.json` 的 `pose_amplify_by_direction`）。

桌面版实测路径：
`C:\Users\32113\AppData\Local\Comfy-Desktop\ComfyUI-Installs\ComfyUI (1)\ComfyUI\custom_nodes\vnccs-utils\nodes\pose_studio.py`

## 为什么必须放大 `joint_rotations`（两轮踩坑记录）

前端 `web/pose_studio/core.js` 的 `applySAM3DImport()` 有**两条通道**：

| # | 通道 | 数据 | 语义 |
|---|---|---|---|
| ① | `_applySAM3DRotationImport()` → `_applySAM3DHumanoidDeltaImport()` | `joint_rotations` + `rest_joint_rotations` | 世界旋转增量 → 直接写骨骼四元数。**真正决定四肢摆角** |
| ② | `applyWorldKeypointImport(worldKps, {normalizeLegs: true, normalizeArms: true})` | `joint_coords`（+ `canonical_keypoints_3d` / `keypoints_3d`） | 只取**方向**，长度归一化到人偶骨架 |

两次失效：

1. **v0.5.11**：按 `poses[].bones`（前端**回传**的欧拉角）放大 → 广播载荷里没有 `poses`
   → 命中 0 关节，静默无效（连日志都没有）。
2. **v0.5.12**：按 `joint_coords` 做「以骨盆为中心的**径向等比**放大」
   `base + (p - base) * factor` → 数值命中 36 点，但**等比缩放不改变向量方向**，
   在 `normalizeLegs` 眼下与没放大完全等价；唯一真实效果是把 SAM 骨架的长度拉长 3 倍，
   反而污染了 `autoFitSAM3DBoneLengths` 的体型拟合。
   实测：前后两批成图跨批差异 5.8 ≈ 重跑噪声，8 帧横向轮廓 span 仅 6–8 px / 角色高 920 px。

**结论**：位置通道放大方向，旋转通道放大角度，二者不能互换。

## 本版做法

对每个肢体关节 j（父 p，均为世界旋转矩阵）：

```
L_cur   = R_cur_p^T · R_cur_j          局部当前
L_rest  = R_rest_p^T · R_rest_j        局部 rest
Δ       = L_cur · L_rest^T             相对 rest 的额外摆动
Δ'      = Δ^(factor)                   轴角分解后绕原轴 ×factor（Rodrigues，钳 140°）
R_new_j = R_new_p · Δ' · L_rest        新世界旋转
pos_new = pos_new_p + R_new_p · (R_rest_p^T · (pos_rest_j - pos_rest_p))
```

- 位置用 **rest 局部骨骼向量**重建 → 骨骼长度恒等于 rest（单测 diff ≈ 0），不动体型。
- 放大环节：`thigh/calf/upperarm/lowerarm`（2/3/18/19、75/76/39/40）；
  `foot/hand`（4/20/78/42）**不放大自身旋转**，只随父链 FK 更新位置，避免踝腕翻折。
- 回退：载荷里没有旋转数据时，退回放大 `poses[].bones` 的摆动分量；再不行就打印
  `NO limb rotations matched` + payload 顶层键与各字段长度（**绝不静默假装成功**）。

## 应用步骤

1. 备份原文件（现状：同目录 `pose_studio.py.charforge-bak` / `.charforge-v0512-bak`）。
2. 把下方代码块整体替换文件中 `# === CharForge patch: pose amplification` 到
   `# === end CharForge patch` 之间的内容（含标记行）。
3. 语法自检：`python -m py_compile pose_studio.py`。
4. **重启 ComfyUI Desktop** —— 改的是服务端 Python，不重启不生效。
5. 生效自检：跑一帧 keep_pose 后看 ComfyUI 日志，应出现

   ```
   [VNCCS Pose Studio] CharForge pose_amplify x2.2: 12 limb joints re-solved
   (thigh_l 77.0deg, calf_l 0.0deg, thigh_r 77.0deg, ...)
   ```

   打 `NO limb rotations matched` = 载荷结构又变了，把那行连同 `payload keys=...` 发出来。

## 系数怎么调

`bindings.json` 的 `pose_amplify_by_direction`。本版是**真角度放大**（v0.5.12 的 ×3 是假的），
所以系数明显小于旧值：`up 2.2 / down 2.0 / left·right 1.0`（侧面本来就正常，保持 1.0）。
35° 的 thigh 摆幅 ×2.2 ≈ 77°。过大出畸形姿势，从 2.0 起观察成图。

## 算法单测

本版补丁的轴角放大与 FK 重建已在本地离线验证（合成骨架）：

| factor | thigh 摆角 | 骨骼长度 | 正交性 |
|---|---|---|---|
| 1.0 | 35°（**恒等**，安全） | 守恒 | ✓ |
| 2.0 | 70° | 守恒 | ✓ |
| 3.0 | 105° | 守恒 | ✓ |

另验证 140° 钳制（89°×3 → 140°）与 factor=1.0 时提前返回原载荷。

## 补丁原文

```python
# === CharForge patch: pose amplification (2026-10-14, v0.5.13 重写) ==========
# 背景：背面/正面朝向的步伐在 2D 投影里是前后向位移，SAM 3D 估出的肢体角天然偏小
# （背面 thigh 摆幅只有侧面的 1/3~1/4），弱信号传到扩散端会被站姿先验吞掉（成图塌成
# 站姿）。此处按 pose_data.export.pose_amplify 把肢体关节**相对 rest 的旋转增量**绕
# 原轴放大，再用 rest 骨骼长度做一次 FK 重建关节位置——默认 1.0 = 完全原行为。
#
# ⚠ 为什么必须放大 joint_rotations，而不是 joint_coords：
#   前端 core.js 的 applySAM3DImport() 有两条通道：
#     ① _applySAM3DRotationImport() → _applySAM3DHumanoidDeltaImport()：
#        用 joint_rotations 与 rest_joint_rotations 的**世界旋转增量**直接写骨骼四元数
#        —— 这才是真正决定四肢摆角的通道；
#     ② applyWorldKeypointImport(worldKps, { normalizeLegs: true, ... })：
#        用 joint_coords 定**方向**、长度归一化到人偶自身骨架（autoFitSAM3DBoneLengths
#        还会按 SAM 骨架拟合骨骼长度）。
#   历史教训：
#     v0.5.11 按 poses[].bones 放大 → 命中 0 关节、静默无效；
#     v0.5.12 按 joint_coords 做「以骨盆为中心的径向等比放大」→ 位置命中 36 点，但
#       径向缩放**不改变方向**，在 normalizeLegs 眼下与没放大完全等价（实测两批成图
#       跨批差异 5.8 ≈ 重跑噪声；8 帧横向轮廓 span 仅 6~8px / 体高 920px），
#       而且把 SAM 骨架长度拉长 3 倍、污染了 autoFitSAM3DBoneLengths 的体型拟合。
#   本版只放大旋转增量，并用 rest 骨架 FK 重建位置，保证两条通道角度一致。
#
# 关节索引（vnccs_sam3d/pose_import.py 的 known_joint_names，与 core.js
# SAM3D_JOINT_COORD_NAMES / SAM3D_ROTATION_PARENTS 同名表一致）：
#   1=pelvis / 2=thigh_l 3=calf_l 4=foot_l / 18=thigh_r 19=calf_r 20=foot_r
#   38=clavicle_r 39=upperarm_r 40=lowerarm_r 42=hand_r
#   74=clavicle_l 75=upperarm_l 76=lowerarm_l 78=hand_l
_AMPLIFY_PARENT = {
    2: 1, 3: 2, 4: 3,
    18: 1, 19: 18, 20: 19,
    75: 74, 76: 75, 78: 76,
    39: 38, 40: 39, 42: 40,
}
# 从近根到末端（父先于子），保证 FK 时父的新状态已算好
_AMPLIFY_ORDER = (2, 3, 4, 18, 19, 20, 75, 76, 78, 39, 40, 42)
# 放大自身摆动角的环节（髋/膝、肩/肘）
_AMPLIFY_SPIN = (2, 3, 18, 19, 75, 76, 39, 40)
# 末端跟随环节（踝/腕）：自身旋转不放大，位置随父链 FK 更新，保持相对父的自然角
_AMPLIFY_FOLLOW = (4, 20, 78, 42)
_AMPLIFY_MAX_DEG = 140.0          # 单关节放大后角度上限，防止翻折
# 无旋转数据时的回退路径：Pose Studio 提交的 poses[].bones（欧拉角，放大摆动分量）
_AMPLIFY_FALLBACK_BONES = (
    "thigh_l", "thigh_r", "calf_l", "calf_r",
    "upperarm_l", "upperarm_r", "lowerarm_l", "lowerarm_r",
)
_AMPLIFY_BONE_CLAMP = 80.0


def _pose_amplify_factor(data):
    export = data.get("export", {}) if isinstance(data, dict) else {}
    try:
        factor = float(export.get("pose_amplify", 1.0))
    except (TypeError, ValueError):
        return 1.0
    if not math.isfinite(factor) or factor <= 0:
        return 1.0
    return factor


def _rotation_angle_deg(matrix):
    """旋转矩阵的旋转角（度）。"""
    import numpy as np

    trace = float(np.trace(np.asarray(matrix, dtype=np.float64)))
    cos_theta = max(-1.0, min(1.0, (trace - 1.0) * 0.5))
    return math.degrees(math.acos(cos_theta))


def _rotation_pow(matrix, factor):
    """把旋转矩阵的旋转角放大 factor 倍（绕同一轴，Rodrigues 轴角重建）。

    等比缩放矩阵元素是错的（会破坏正交性），必须走轴角分解。
    角度上限 _AMPLIFY_MAX_DEG 防止摆动放大到翻折。
    """
    import numpy as np

    rot = np.asarray(matrix, dtype=np.float64)
    cos_theta = max(-1.0, min(1.0, (float(np.trace(rot)) - 1.0) * 0.5))
    theta = math.acos(cos_theta)
    if theta < 1e-6:
        return np.eye(3)

    sin_theta = math.sin(theta)
    if sin_theta > 1e-8:
        axis = np.array([
            rot[2, 1] - rot[1, 2],
            rot[0, 2] - rot[2, 0],
            rot[1, 0] - rot[0, 1],
        ]) / (2.0 * sin_theta)
        norm = float(np.linalg.norm(axis))
        if norm < 1e-8:
            return rot.copy()
        axis = axis / norm
    else:
        # 接近 180°：轴不唯一，从 (R + I) 取范数最大的列
        candidate = rot + np.eye(3)
        norms = np.linalg.norm(candidate, axis=0)
        index = int(np.argmax(norms))
        if norms[index] < 1e-8:
            return np.eye(3)
        axis = candidate[:, index] / norms[index]

    new_theta = min(abs(theta * factor), math.radians(_AMPLIFY_MAX_DEG))
    x, y, z = float(axis[0]), float(axis[1]), float(axis[2])
    cos_new, sin_new = math.cos(new_theta), math.sin(new_theta)
    one_minus = 1.0 - cos_new
    return np.array([
        [cos_new + x * x * one_minus, x * y * one_minus - z * sin_new, x * z * one_minus + y * sin_new],
        [y * x * one_minus + z * sin_new, cos_new + y * y * one_minus, y * z * one_minus - x * sin_new],
        [z * x * one_minus - y * sin_new, z * y * one_minus + x * sin_new, cos_new + z * z * one_minus],
    ])


def _amplify_rotations_and_rebuild(pose_payload, factor):
    """放大肢体关节的旋转增量，并按 rest 骨架 FK 重建关节位置。

    对每个关节 j（父 p，均为「世界旋转矩阵」）：
      局部当前  L_cur  = R_cur_p^T · R_cur_j
      局部 rest L_rest = R_rest_p^T · R_rest_j
      增量      Δ      = L_cur · L_rest^T          （相对 rest 的额外摆动）
      放大      Δ'     = Δ^(factor)                （绕原轴，仅 _AMPLIFY_SPIN）
      新局部    L_new  = Δ' · L_rest
      新世界    R_new  = R_new_p · L_new
      新位置    pos_new = pos_new_p + R_new_p · (R_rest_p^T · (pos_rest_j - pos_rest_p))
    （位置用 rest 局部骨骼向量重建 → 骨骼长度恒等于 rest，不污染体型拟合。）

    返回成功重建的关节数（0 = 字段缺失/结构不符，调用方走回退）。
    """
    import numpy as np

    rots = pose_payload.get("joint_rotations")
    rest_rots = pose_payload.get("rest_joint_rotations")
    coords = pose_payload.get("joint_coords")
    rest_coords = pose_payload.get("rest_joint_coords")
    if not isinstance(rots, list) or not isinstance(rest_rots, list) or not isinstance(coords, list):
        return 0
    if not isinstance(rest_coords, list):
        return 0
    needed = max(_AMPLIFY_ORDER) + 1
    if len(rots) < needed or len(rest_rots) < needed or len(coords) < needed or len(rest_coords) < needed:
        return 0

    def _rot(seq, index):
        rows = seq[index] if 0 <= index < len(seq) else None
        if not isinstance(rows, (list, tuple)) or len(rows) != 3:
            return None
        try:
            matrix = np.array(
                [[float(rows[r][c]) for c in range(3)] for r in range(3)],
                dtype=np.float64,
            )
        except (TypeError, ValueError, IndexError):
            return None
        return matrix if np.all(np.isfinite(matrix)) else None

    def _pos(seq, index):
        point = seq[index] if 0 <= index < len(seq) else None
        if not isinstance(point, (list, tuple)) or len(point) < 3:
            return None
        try:
            vector = np.array([float(point[0]), float(point[1]), float(point[2])], dtype=np.float64)
        except (TypeError, ValueError):
            return None
        return vector if np.all(np.isfinite(vector)) else None

    new_rot = {}
    new_pos = {}
    angles = {}
    hit = 0

    for index in _AMPLIFY_ORDER:
        parent = _AMPLIFY_PARENT[index]
        r_cur = _rot(rots, index)
        r_rest = _rot(rest_rots, index)
        r_cur_p = _rot(rots, parent)
        r_rest_p = _rot(rest_rots, parent)
        p_cur_p = _pos(coords, parent)
        p_rest_j = _pos(rest_coords, index)
        p_rest_p = _pos(rest_coords, parent)
        if any(value is None for value in
               (r_cur, r_rest, r_cur_p, r_rest_p, p_cur_p, p_rest_j, p_rest_p)):
            continue

        # 父关节若不是本链一环（pelvis / clavicle），保持原始状态
        r_new_p = new_rot.get(parent, r_cur_p)
        p_new_p = new_pos.get(parent, p_cur_p)

        local_cur = r_cur_p.T @ r_cur
        local_rest = r_rest_p.T @ r_rest
        delta = local_cur @ local_rest.T
        delta_new = _rotation_pow(delta, factor) if index in _AMPLIFY_SPIN else delta
        local_new = delta_new @ local_rest
        rot_new = r_new_p @ local_new

        # 位置用 rest 局部骨骼向量重建：长度不变，只随父的新朝向摆动
        rest_local_vector = r_rest_p.T @ (p_rest_j - p_rest_p)
        pos_new = p_new_p + r_new_p @ rest_local_vector

        new_rot[index] = rot_new
        new_pos[index] = pos_new
        angles[index] = _rotation_angle_deg(delta_new)
        hit += 1

    if not hit:
        return 0

    for index, matrix in new_rot.items():
        rots[index] = [[float(matrix[r][c]) for c in range(3)] for r in range(3)]
    for index, vector in new_pos.items():
        coords[index] = [float(vector[0]), float(vector[1]), float(vector[2])]

    names = pose_payload.get("joint_names")

    def _name(index):
        if isinstance(names, list) and 0 <= index < len(names):
            return str(names[index])
        return "joint_%03d" % index

    detail = ", ".join(
        "%s %.1fdeg" % (_name(index), angles[index])
        for index in sorted(_AMPLIFY_SPIN) if index in angles
    )
    print("[VNCCS Pose Studio] CharForge pose_amplify x%g: %d limb joints re-solved (%s)."
          % (factor, hit, detail))
    return hit


def _amplify_poses_fallback(pose_payload, factor):
    """回退路径：放大 Pose Studio 提交的 poses[].bones 欧拉角摆动分量。"""
    poses = pose_payload.get("poses")
    if not isinstance(poses, list):
        return 0
    hit = 0
    for pose in poses:
        bones = pose.get("bones") if isinstance(pose, dict) else None
        if not isinstance(bones, dict):
            continue
        for name, rotation in list(bones.items()):
            if name not in _AMPLIFY_FALLBACK_BONES:
                continue
            if isinstance(rotation, (list, tuple)) and len(rotation) >= 1:
                try:
                    swing = float(rotation[0]) * factor
                except (TypeError, ValueError):
                    continue
                swing = max(-_AMPLIFY_BONE_CLAMP, min(_AMPLIFY_BONE_CLAMP, swing))
                bones[name] = [swing] + list(rotation[1:])
                hit += 1
    if hit:
        print("[VNCCS Pose Studio] CharForge pose_amplify x%g: amplified %d limb bones (poses fallback)."
              % (factor, hit))
    return hit


def _amplify_pose_payload(pose_payload, factor):
    """放大动作幅度：优先放大 dense MHR 关节旋转增量（真正驱动人偶的通道），
    其次回退到 poses[].bones 欧拉角。全都没命中就原样返回并 print 出 payload 实际
    形态——绝不静默假装成功。
    """
    if factor == 1.0 or not isinstance(pose_payload, dict):
        return pose_payload

    if _amplify_rotations_and_rebuild(pose_payload, factor):
        return pose_payload
    if _amplify_poses_fallback(pose_payload, factor):
        return pose_payload

    shapes = {}
    for key in ("joint_rotations", "rest_joint_rotations", "joint_coords",
                "rest_joint_coords", "canonical_keypoints_3d", "keypoints_3d"):
        value = pose_payload.get(key)
        if isinstance(value, list):
            shapes[key] = len(value)
    print("[VNCCS Pose Studio] CharForge pose_amplify x%g: NO limb rotations matched — "
          "payload keys=%s rotation/coord fields=%s. Amplification skipped (字段结构变了)."
          % (factor, list(pose_payload.keys())[:18], shapes))
    return pose_payload
# === end CharForge patch =====================================================
```

---

## 姿势复制提示词（v0.5.14 服务端补丁 → **v0.5.15 起改插件注入，补丁已撤**）

### 为什么放大了还不够（2026-10-14 取证实锤）

v0.5.13 的旋转放大确认生效（日志 8×`12 limb joints re-solved`），但 walk/up 成图
仍全部站桩。用临时 SaveImage 直连节点 488 图像输出取证：

- 488 输出（=喂给扩散的 `images.image_1`）**是清晰大跨步人偶**；
- 扩散成图却是站桩（keepose_00148）。

即：SAM / 放大 / 前端人偶 / 捕获全部正常，**最后一环 Qwen-Image-Edit 2.1 对
image_1 的姿势服从度不足**（背面朝向尤甚）。对照实验：交换 image_1/image_2 无效
（00149）；提示词显式要求「严格复制第一张图的姿势」立刻出现跨步（00150 逐句 /
00153 通用，采纳通用句）。

### v0.5.15 现行方案：插件覆写 484.prompt（文案在 bindings，零第三方改动）

- 文案与目标节点都在 `Configs/comfyui/charforge_keepose_pose.bindings.json`：
  `pose_copy_prompt`（正文）+ `pose_copy_prompt_node`（写入目标，当前 `484`）。
- 插件提交前 `SetNode(484, "prompt", 文案)`，**顶掉** 484.prompt 原本接自
  488 输出槽 1（lighting_prompt）的链接——这是预期行为：lighting/相机描述丢了
  无碍，人偶图本身已带光照与相机角度；若日后确需保留，改走文本拼接节点
  （把 488 输出槽 1 与固定文本拼起来），不要撤 bindings 键。
- 改文案 = 改 bindings 保存 → **不用重启 ComfyUI**，下一次提交即生效。
- 自检：Unity 日志出现 `姿势复制提示词 → 节点 484.prompt（顶掉 PoseStudio 的
  lighting_prompt 链接）`。
- **文案三要素（v0.5.16 教训，缺一不可）**：
  1. `Draw character from image2` —— 角色来源指令；
  2. `Replace the pose of <image 2> with the pose of <image 1>. This is an
     RGBA image with transparency.` —— 原生姿势句 + **透明背景句**。v0.5.15
     把透明句顶掉后输出 100% 实心品红背景（00213~00220 实证；保留原生
     提示词的 00205/00206 有 77% 完全透明）。修正版 00221 双验证：跨步 ✓
     + 完全透明 74.2% ✓；
  3. `Copy the pose precisely: …` —— 姿势强化句（Qwen 服从的关键）。
  丢的只有 `<lighting>` 行（前端光照描述，人偶图自带光照信息，可接受）。
- **槽位约定**：句中 image 1 = 人偶（`484.images.image_1 ← 488`，姿势来源），
  image 2 = 立绘（`image_2 ← 470`，姿势被替换到它身上）——与 vnccs 原生
  模板同一套占位语义；调整槽位顺序必须同步改写句子。

### 负结果入库（别再试这三条路）

1. **改 `pose_data.poses[].prompt` / `export.user_prompt`（保留 484 链接）无效**
   （keepose_00204 站桩实证）。vnccs 前端 UI 的 Prompt 输入框确实最终进模板
   （`prompt_template` 的 `<user_prompt>` 槽），但 sync 回传的 `lighting_prompts`
   是**前端用自己内存状态重算**的，不采纳提交侧 pose_data 里的提示词字段——
   API 提交侧能确定性控制提示词的只有覆写 484。
2. **改顶层 `pose_prompts` 数组同样无效**（keepose_00205 追加 / 00206 完整替换，
   双双站桩）。`pose_prompts` 确实是前端 `this.posePrompts` 的恢复入口
   （core L15799），但那段 restore 逻辑挂在 **widget 值 setter**（工作流加载 /
   节点初始化）上；执行时的前端 sync 只回传捕获图与自己内存里的
   `lighting_prompts`，不从提交侧恢复任何提示词。**最强对照**：同一句
   FULL 提示词，00206（走 pose_prompts）站桩 vs 00153（覆写 484）跨步——
   注入位置决定一切。
3. **服务端补丁门控不能放在 `generate()` 尾部**：那时 `data` 已被前端 sync 回传
   的 `synced` 整体覆盖，`synced.export` 是前端 UI 状态，永远读不到插件写入的
   开关（pose_amplify 生效正是因为它在覆盖前读）。v0.5.14 实测踩坑。

**为什么 pose_amplify 能生效而提示词不能**：pose_amplify 由**服务端**消费
（广播姿势数据前先放大），不依赖前端采纳；提示词最终由**前端**生成回传，
前端是提示词的唯一主人——提交侧任何字段都改不到它。若日后确需「保留
lighting_prompt 同时追加句子」，唯一理论可行路径是复活 v0.5.14 的服务端
part 2（门控修正版，见下方存档），在服务端拿到 synced 后追加——需重启
ComfyUI 验证，未实测。

### 历史参考：v0.5.14 服务端补丁（已从 vnccs 撤下，仅存档）

<details>
<summary>当时的三处修改（若需「保留 lighting_prompt 同时追加句子」可参考复活）</summary>

修改点 1：模块级常量（追加在 `# === end CharForge patch ===` 行之前）

```python
_POSE_COPY_PROMPT_SENTENCE = (
    "Convert the mannequin in the first image into the same character shown in the "
    "second image, strictly preserving the mannequin's exact body pose, stride and "
    "camera angle. Copy the pose precisely: keep each limb where the mannequin has it, "
    "including which leg is lifted or planted and how the arms swing."
)
```

修改点 2a：`generate()` 解析 pose_data 处（紧跟 `_hydrate_cached_pose_animation(data)` 之后），
门控必须在数据被前端 sync 覆盖**前**取好：

```python
            _cf_pose_copy_prompt = (
                isinstance(data, dict)
                and isinstance(data.get("export"), dict)
                and data["export"].get("pose_copy_prompt") is True
            )
```

修改点 2b：CSR 路径（紧跟 lighting_prompts 的 `_merge_camera_prompt` 列表推导之后）：

```python
            if _cf_pose_copy_prompt:
                lighting_prompts = [
                    " ".join(part for part in (prompt, _POSE_COPY_PROMPT_SENTENCE) if part)
                    for prompt in lighting_prompts
                ]
                print("[VNCCS Pose Studio] CharForge pose_copy_prompt: appended pose-copy "
                      "instruction to %d prompt(s)." % len(lighting_prompts))
```

</details>
