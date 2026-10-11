# com.charforge.unity2d

Unity 6 原生 2D 角色资产流水线插件。在编辑器内完成「一句话描述 + 画风参考图 → 可运行的 NPC prefab」。

## 安装

**方式一（本地）**：Package Manager → `+` → Add package from disk → 选择本目录的 `package.json`。

**方式二（Git URL）**：Package Manager → `+` → Add package from git URL。

**依赖**：
- `com.unity.nuget.newtonsoft-json`（必需，随包自动安装）
- `com.unity.2d.sprite`（必需，随包自动安装——切片导入设置要用它的 Sprite Data Provider）
- `com.unity.2d.animation`（**可选**）：装了才会启用 Sprite Library 生成。由 `versionDefines` + `defineConstraints` 自动开关，**没装不会报错**。
- `com.unity.ai.inference` ≥ 2.0（**可选**）：装了才会启用「引擎内推理」后端。

## 用法

`Window > CharForge 角色资产生成`

1. **① 推理后端**：先点「探测后端」。没有环境时选「占位后端」也能立刻跑通全流程。
2. **② 角色描述**：一句话。文博场景填「年代/形制约束」与「藏品档案考据要点」，会进入 NPC 的知识边界。
3. **③ 方向/动作/移动**：勾选需要的四向与动作。移动方式先选「不移动」——先拿到能站着的角色，再谈走路。
4. 点「生成并装配」。产物落在 `Assets/CharForge/<job_id>/`。

## 目录职责

| 目录 | 内容 | 备注 |
|---|---|---|
| `Runtime/` | `NpcCharacter` / `AigcDisclosure` / `SimplePatrol2D` | 不引用任何可选包，运行期可安全打包 |
| `Editor/UI/` | UI Toolkit 窗口（`.uxml` + `.uss`） | Unity 6 官方推荐复杂编辑器工具用 UI Toolkit |
| `Editor/Backends/` | `IInferenceBackend` + 4 个后端实现 | 换后端 = 换一个下拉框 |
| `Editor/Services/` | 编排 / 归一化 / 打包 / 动画 / 进程托管 | 归一化与打包在**引擎内**完成，不需要 Python |
| `Editor/Integrations/` | Sprite Library 集成 | 独立程序集，由 2D Animation 包有无自动开关 |

## 程序集设计

三个程序集，边界刻意划清：

```
CharForge.Runtime                      ← 无外部依赖，运行期可打包
CharForge.Editor                       ← 核心：编排 / 装配 / 导入 / 后端
CharForge.Editor.SpriteLibrary         ← 可选：依赖 com.unity.2d.animation
```

`CharForge.Editor.SpriteLibrary` 用 `defineConstraints: ["CHARFORGE_2D_ANIMATION"]` 实现
"装了才编译"。它通过 `[InitializeOnLoad]` 把钩子注册进 `PostAssembleHooks.AfterAssembleRoot`，
因此**核心程序集不需要引用它**，也就不会产生"依赖可选包"的连锁问题。

## 常见问题

**Q：为什么窗口里没有「引擎内推理」后端？**
`com.unity.ai.inference` 未安装。装上并重启编辑器后会自动出现。

**Q：生成出来的角色穿模 / 碰撞体不对？**
碰撞体尺寸来自后端实测的角色轮廓（`manifest.character_bounds`）。若该字段缺失，
插件会按贴图包围盒估算并**刻意偏大**。先确认 back 端产物里带了这个字段。

**Q：prefab 名字为什么是 `NPC_yun_shu_a1b2c3` 而不是中文？**
中文文件名在跨平台 git、CI、不同文件系统编码下会出问题。显示名保留中文（`NpcCharacter.displayName`），
文件名用 ASCII + 短后缀防重名。
