# 适配器层

这一层的作用是**把「生成后端」变成可插拔的**。

骨架阶段，`server/pipeline/generate.py` 里内联了三个分支：

| 分支 | 用途 | 需要什么 |
|---|---|---|
| `mock` | 画占位人形，验证水管 | 什么都不需要 |
| `local` | diffusers 或 ComfyUI | GPU（8-12GB） |
| `cloud` | 云端图像 API | API key（注意素材外传的合规问题） |

当你要接第四个后端时，别继续往 `if/elif` 里塞。按 `base.py` 的 `ImageBackend` 协议
新建一个模块，然后让 `generate.py` 从注册表取。

## 建议扩展顺序

1. **ComfyUI API**（最推荐先做）—— 你已经跑通过 ComfyUI 链路。把 workflow 导出成 API 格式 JSON，
   这里只做「填参数 → `POST /prompt` → 轮询 `/history` → 下载图」。好处：节点级控制全在手，
   社区工作流可直接复用，ControlNet / IP-Adapter / LoRA 都是现成节点。
2. **diffusers 直调** —— 纯 Python，无外部服务依赖，但 ControlNet + IP-Adapter 的组装比较啰嗦。
3. **云端 API** —— 最快出图，但姿势与画风锁定能力通常最弱，且素材离开本机。

## 一个容易踩的坑

不同后端对 `size` 的处理不同：有的要求是 8 的倍数，有的要求是 64，
有的（SDXL 原生）在 1024 才出最佳质量而在 512 会崩。

**别在适配器里静默改尺寸**——尺寸不一致会让归一化那一步的统一缩放失准，
表现就是「有的帧角色偏大」。如果后端有尺寸约束，在适配器里显式报错，让上层决定。
