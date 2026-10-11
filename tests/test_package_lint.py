"""Unity 包结构校验的单元测试。

重点测 v0.3.1 新增的第 8 条（`check_meta_files`）：每个资产必须有 `.meta`，
且 `.meta` 的 importer 段必须**真的换行**。

这条规则拦的是一类"肉眼看不出来"的缺陷：批量生成 `.meta` 时把 `\\n` 写成了字面量 `/n`，
文件看着"有内容"，Unity 却解析不出导入设置段——不报错，只是导入参数悄悄丢掉，
或在别人的工程里换一个 GUID。本项目上一版就真写了 10 个这样的文件。
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

from validate_unity_package import (  # noqa: E402
    META_BROKEN_IMPORTER,
    META_GUID,
    _meta_assets,
)

GOOD = (
    "fileFormatVersion: 2\n"
    "guid: 5b1f9ccb64b47e09f04bb21a114ba09c\n"
    "TextScriptImporter:\n"
    "  externalObjects: {}\n"
    "  userData: \n"
    "  assetBundleName: \n"
    "  assetBundleVariant: \n"
)

# 真实踩过的坑：换行写成了字面量 /n
BROKEN_TEXT = GOOD.replace("TextScriptImporter:\n", "TextScriptImporter:/n")

# 目录用的是 folderAsset + DefaultImporter，同样会被写坏
BROKEN_FOLDER = (
    "fileFormatVersion: 2\n"
    "guid: 0123456789abcdef0123456789abcdef\n"
    "folderAsset: yes\n"
    "DefaultImporter:/n"
    "  externalObjects: {}\n"
)


def test_broken_importer_newline_is_detected():
    assert META_BROKEN_IMPORTER.search(BROKEN_TEXT)


def test_folder_importer_variant_is_covered():
    """目录的 .meta 走 DefaultImporter，不能只查 TextScriptImporter。"""
    assert META_BROKEN_IMPORTER.search(BROKEN_FOLDER)


def test_healthy_meta_is_not_flagged():
    assert META_BROKEN_IMPORTER.search(GOOD) is None


def test_guid_must_be_32_lowercase_hex():
    assert META_GUID.search(GOOD)
    bad = GOOD.replace("5b1f9ccb64b47e09f04bb21a114ba09c", "not-a-guid")
    assert META_GUID.search(bad) is None
    upper = GOOD.replace(
        "5b1f9ccb64b47e09f04bb21a114ba09c", "5B1F9CCB64B47E09F04BB21A114BA09C"
    )
    assert META_GUID.search(upper) is None, "Unity 写的是小写 GUID"


def test_repository_metas_are_clean():
    """真实仓库不允许存在坏 .meta —— 这是这条规则存在的全部意义。"""
    problems: list[str] = []
    total = 0
    for asset in _meta_assets():
        total += 1
        meta = asset.with_name(asset.name + ".meta")
        if not meta.exists():
            problems.append(f"{meta.as_posix()} 缺失")
            continue
        text = meta.read_text(encoding="utf-8", errors="replace")
        if META_BROKEN_IMPORTER.search(text) or not META_GUID.search(text):
            problems.append(meta.as_posix())
    assert total > 0, "没扫到任何资产，检查脚本可能坏了"
    assert problems == [], "有问题的 .meta：\n" + "\n".join(problems)
