# Unity 就绪度评分规则（2D 版）

## 为什么要有这个

AI 生成的素材「看起来能用」和「真的能用」之间有一道坎。3D 侧表现为法线反转、无 UV、面数爆炸；
**2D 侧的表现是下面十条**。这套规则有两个用途：

1. **自动体检**：流水线跑完自动打分，不达标就打回重做，而不是等美术拖进 Unity 才发现；
2. **README 卖点**：一个能量化「就绪度」的工具，比一个只会出图的工具值钱得多。

（顺便：这套规则本身就是你可以写进简历的「质量评估体系」——它证明你不只会调模型。）

---

## 检查项

| # | 检查项 | 阈值 | 不达标的表现 | 权重 |
|---|---|---|---|---|
| 1 | **锚点在脚底** | `pivot_y = 0`，且所有帧实际脚底 y 一致（±2px） | 角色悬空 / 陷地 / 走路上下抖 | 15 |
| 2 | **统一缩放** | 所有帧角色高度方差 < 3% | 走路时忽大忽小 | 15 |
| 3 | **透明通道干净** | 背景残留像素 < 0.5%；无白边 / 黑边 | 边缘毛刺，深色场景尤其明显 | 12 |
| 4 | **切片完整** | 每个切片内角色 bbox 不触边（留 ≥2px 余量） | 缺胳膊少腿 | 12 |
| 5 | **统一画布** | 所有帧尺寸完全一致 | 播放时画面跳动 | 10 |
| 6 | **像素网格对齐** | 像素风：所有不透明像素坐标落在 grid 倍数上 | 移动时边缘抖动 | 10 |
| 7 | **命名规范** | `{direction}_{action}_{NN}`，帧号两位 | 切片排序错乱，动画倒放 | 8 |
| 8 | **帧数充足** | 每个 clip ≥ 2 帧（建议 4-8） | 动画不动或过于跳 | 6 |
| 9 | **PPU 一致** | 与项目其它素材相同 | 与场景中其他角色大小不搭 | 6 |
| 10 | **体积可控** | sheet < 4MB（移动端 < 2MB） | 包体超标 | 6 |

**评级**：≥ 90 优秀 / 75-89 可用 / 60-74 需人工修 / < 60 打回重做。

---

## 怎么实现（建议加到流水线第 6 步之后）

在 `pack.py` 之后加一个 `readiness.py`，输入归一化后的帧 + manifest，输出评分报告：

```python
def score_readiness(frames, manifest, canvas, pixel_grid, ppu) -> dict:
    checks = []

    # 1 锚点在脚底：所有帧的 alpha bbox 底边是否一致
    bottoms = [alpha_bbox_bottom(f) for f in frames]
    ok = max(bottoms) - min(bottoms) <= 2
    checks.append(("锚点在脚底", ok, 15))

    # 2 统一缩放：角色高度的标准差 / 均值
    heights = [alpha_bbox_height(f) for f in frames]
    cv = stdev(heights) / mean(heights)
    checks.append(("统一缩放", cv < 0.03, 15))

    # 3 透明通道干净：检查边缘一圈是否存在半透明带（>2px）
    ...

    # 6 像素网格对齐
    if pixel_grid > 1:
        ok = all_transparent_pixels_on_grid(frames, pixel_grid)
        checks.append(("像素网格对齐", ok, 10))

    ...  # 其余项

    total = sum(w for _, ok, w in checks if ok)
    return {"score": total, "checks": checks, "grade": grade(total)}
```

报告写进 `out/<job_id>/readiness.json`，同时通过 WebSocket 推给 Unity 显示。

---

## 两个实现细节

**1. 别把「画风一致性」放进这套规则里。**
它是主观项，用 CLIP 相似度单独算（`style_lock.score_similarity`），阈值 ≥ 0.85。
把它混进客观规则里会让评分失去意义。

**2. 检查 1 和检查 2 必须一起看。**
单独看锚点，逐帧自适应缩放能骗过检查；单独看缩放，位置飘移也能骗过检查。
两个一起才锁得住——这也是「为什么必须统一 scale + 统一锚点」的工程理由。
