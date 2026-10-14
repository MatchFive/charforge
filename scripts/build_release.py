#!/usr/bin/env python3
"""构建 CharForge 发布包（dist/com.charforge.unity2d-<version>.zip）。

包内是完整的 UPM 包目录（含全部 .meta），用户解压后在 Unity Package Manager
里 Add package from disk 指向其中的 package.json 即可使用。

用法：
    python scripts/build_release.py            # 读 package.json 的版本号
    python scripts/build_release.py -o <dir>   # 指定输出目录（默认 dist/）

校验：构建前先跑 validate_unity_package.py 的同一套结构检查（包内资产
必须逐一带 .meta），有错即退出非零——宁可不出包，不出坏包。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PACKAGE_DIR = REPO_ROOT / "packages" / "com.charforge"


def fail(msg: str) -> None:
    print(f"[build_release] 错误：{msg}", file=sys.stderr)
    sys.exit(1)


def check_meta_coverage() -> list[str]:
    """包内每个资产文件都要有同名 .meta（Unity 包的铁律，缺一个导入就报警）。"""
    problems: list[str] = []
    all_files = sorted(p for p in PACKAGE_DIR.rglob("*") if p.is_file())
    metas = {p for p in all_files if p.suffix == ".meta"}
    for f in all_files:
        if f.suffix == ".meta":
            continue
        if f.name == ".gitignore":
            problems.append(f"意外文件：{f.relative_to(REPO_ROOT)}")
            continue
        if not (f.parent / (f.name + ".meta")).exists():
            problems.append(f"缺 .meta：{f.relative_to(REPO_ROOT)}")
    orphan_metas = [
        m for m in metas
        if not (m.parent / m.stem).exists() and not (m.parent / m.stem).is_dir()
    ]
    problems.extend(f"孤 .meta：{m.relative_to(REPO_ROOT)}" for m in orphan_metas)
    return problems


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("-o", "--out", default=str(REPO_ROOT / "dist"))
    args = parser.parse_args()

    if not PACKAGE_DIR.is_dir():
        fail(f"找不到包目录：{PACKAGE_DIR}")

    pkg_json = PACKAGE_DIR / "package.json"
    try:
        manifest = json.loads(pkg_json.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        fail(f"package.json 读不了：{e}")
        return
    name: str = manifest.get("name", "com.charforge")
    version: str = manifest.get("version", "0.0.0")

    problems = check_meta_coverage()
    if problems:
        print("\n".join(problems), file=sys.stderr)
        fail(f"{len(problems)} 个 meta 覆盖问题，先修再构建")
    print(f"[build_release] meta 覆盖检查通过（{pkg_json.parent}）")

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    zip_path = out_dir / f"{name}-{version}.zip"
    if zip_path.exists():
        zip_path.unlink()

    file_count = 0
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in sorted(PACKAGE_DIR.rglob("*")):
            if f.is_file():
                zf.write(f, f"{name}/{f.relative_to(PACKAGE_DIR).as_posix()}")
                file_count += 1

    digest = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    size_mb = zip_path.stat().st_size / (1024 * 1024)
    print(f"[build_release] 完成：{zip_path}")
    print(f"  文件数 {file_count}，大小 {size_mb:.1f} MB")
    print(f"  SHA256 {digest}")
    print("  安装：解压 → Unity Package Manager → + → Add package from disk → 包内 package.json")


if __name__ == "__main__":
    main()
