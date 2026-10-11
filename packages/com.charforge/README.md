# CharForge · Unity 2D 角色资产流水线（UPM 包）

## 安装

两种方式，选一种：

**方式一：本地路径（开发期推荐）**

把本仓库 `packages/com.charforge` 拷到你的 Unity 工程，或用 UPM 的本地路径引用：

`Window > Package Manager > + > Add package from disk...` → 选 `packages/com.charforge/package.json`

**方式二：Git URL（发布后）**

`Window > Package Manager > + > Add package from git URL...` → 填仓库地址

依赖：`com.unity.nuget.newtonsoft-json`、`com.unity.editorcoroutines`（UPM 会自动拉取）。

## 使用

1. 启动服务端：
   ```bash
   python -m server.cli serve
   ```
2. Unity 菜单 `Window > CharForge 角色资产生成`
3. 填描述、拖入 3-5 张画风参考图、勾方向与动作
4. 点「生成并装配」
5. 完成后会自动选中生成的 prefab，拖进场景即可

## 装配出的层级

```
NPC_云叔 (根)
  ├── SpriteRenderer      精灵
  ├── Animator            四向 idle / walk 状态机
  ├── NpcCharacter        身份 + 人设 + 朝向
  ├── Rigidbody2D         按 movement.mode 决定是否添加
  ├── CapsuleCollider2D   按 collider.shape 决定形状
  └── SimplePatrol2D      巡逻 / 跟随
```

## Animator 参数

| 参数 | 类型 | 说明 |
|---|---|---|
| `facing` | int | 0=南 1=北 2=东 3=西 |
| `isMoving` | bool | 是否在移动 |
| `moveX` / `moveY` | float | 移动向量（留给更细的过渡用） |

`SimplePatrol2D` 会自动驱动这些参数；自定义行为脚本照同样的约定设置即可。

## 对话接入

`NpcCharacter` 刻意保持"薄"：它只持有身份与人设，不主动调用任何模型。

- 人设原文：`NpcCharacter.PersonaJson`
- 系统提示词：`NpcCharacter.GetSystemPrompt()`

接 LLMUnity 或你自己的对话服务时，把 `GetSystemPrompt()` 作为 system message 传进去即可。
**换对话后端不需要重新生成 prefab**——这是把「资产」和「人设」分开的直接好处。

## 两个容易踩的坑

**1. 切片位置全错。**
`manifest.json` 里的 `y` 已经是 Unity 左下原点坐标。如果你在 `SpriteImportSetup` 里又翻转了一次，
切片会上下错位。**不要再翻转。**

**2. 角色悬空或陷进地面。**
锚点在脚底（`pivot_y = 0`），所以 prefab 的原点就是角色的脚底。
摆放时把这个原点对齐地面即可，不要再去调 Sprite 的 offset。

## 已知限制

- SpriteRenderer 挂在根物体上（动画绑定的 `path` 为空）。要改成子物体需同步改 `PrefabAssembler.BuildSpriteClip` 的绑定路径。
- 行为只有巡逻与跟随两个模板。复杂行为请接 Unity Behavior 包。
- NavMesh 是 3D 组件，2D 请用 A*（`movement.mode = astar`）或自己接 2D 寻路方案。
