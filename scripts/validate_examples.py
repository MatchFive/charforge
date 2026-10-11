#!/usr/bin/env python
"""校验 examples/*/character.yaml 是否符合 schemas/character.schema.json。

CI 里会跑，本地改完示例也可以先跑一遍。
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
SCHEMA_PATH = ROOT / "schemas" / "character.schema.json"
EXAMPLES_DIR = ROOT / "examples"


def main() -> int:
    if not SCHEMA_PATH.exists():
        print(f"找不到 schema：{SCHEMA_PATH}", file=sys.stderr)
        return 1

    try:
        import jsonschema
    except ImportError:
        print("请先安装 jsonschema：pip install jsonschema", file=sys.stderr)
        return 1

    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    targets = sorted(EXAMPLES_DIR.glob("*/character.yaml"))
    if not targets:
        print("没有找到任何示例配置", file=sys.stderr)
        return 1

    failed = 0
    for path in targets:
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
        try:
            jsonschema.validate(instance=data, schema=schema)
        except jsonschema.ValidationError as exc:
            failed += 1
            rel = path.relative_to(ROOT)
            print(f"[失败] {rel}")
            print(f"       路径：{'/'.join(str(p) for p in exc.absolute_path) or '<root>'}")
            print(f"       原因：{exc.message}")
        else:
            name = data.get("name", "?")
            dirs = len(data.get("directions", []))
            acts = len(data.get("actions", []))
            frames = data.get("frames_per_clip", 4)
            total = dirs * acts * frames
            print(
                f"[通过] {path.relative_to(ROOT)}  角色={name}  "
                f"方向={dirs} 动作={acts} 每片段={frames}  预计生成 {total} 帧"
            )

    if failed:
        print(f"\n{failed} 个示例未通过校验")
        return 1

    print(f"\n全部 {len(targets)} 个示例通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
