# 示例

两个示例，一个证明**工程能力**，一个证明**领域理解**。简历与 README 里两个都放。

| 目录 | 场景 | 演示什么 |
|---|---|---|
| `town_shopkeeper/` | 游戏向 | 巡逻移动、对话人设、外观约束 |
| `museum_guide/` | 文博向 | 年代形制约束、藏品档案注入人设、四向全生成 |

## 怎么跑

```bash
# 校验示例配置是否符合 schema
python scripts/validate_examples.py

# 跑一个示例（mock 后端即可，不需要 GPU / API key）
python scripts/run_example.py examples/museum_guide
```

产物会落在 `out/<job_id>/`。

## 文博示例的关键设计（值得单独说明）

`museum_guide/character.yaml` 里的 `reference_notes` 字段不是装饰。它解决的是文博行业
最核心的顾虑：**AI 生成的导览内容会不会出现史实硬伤。**

处理方式是把它同时注入两个地方：

1. **生成阶段** —— 作为硬约束写进提示词，并进入 `forbidden` 列表（如「禁用立领与盘扣」）；
2. **对话阶段** —— 进入 `persona.json` 的「知识边界」段，并明确要求
   「只依据以下资料回答，资料没有的内容不要推测」。

也就是说：**先把藏品档案检索到位，再让模型说话。** 这个顺序不能反。
没有这一步，生成的角色只是一个穿古装的通用聊天机器人，文博机构不会买单。

（骨架阶段 `reference_notes` 由你在 yaml 里手写。V1 阶段应替换为真实的藏品档案 RAG 检索，
检索源可以是馆藏数据库、展陈大纲或已出版的图录。）
