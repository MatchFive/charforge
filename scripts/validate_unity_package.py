"""Unity 包结构校验（CI 用，不依赖 Unity）。

检查的都是"在 CI 里能查出来、但一旦漏掉就会在用户的工程里爆掉"的问题：

1. **每个 .cs 都必须落在某个 asmdef 的文件夹内** —— Unity 的经典陷阱：
   漏在 asmdef 之外的脚本会被编进 Assembly-CSharp，而包内的 Assembly-CSharp
   在用户工程里会与用户自己的脚本冲突。这类问题本地测不出来，装到别人工程才炸。
2. asmdef JSON 合法 + 引用名与预期的 Unity 程序集一致。
3. 类型全限定名不重复（同名类分处两个命名空间是允许的，同一命名空间内重名不行）。
4. 大括号配平 —— 便宜但有效的语法体检。
5. C# 里引用的 UXML / USS 路径真实存在。
6. package.json 合法，且声明的 dependencies 与 asmdef 的硬引用不矛盾。
7. **C# 里引用的 uxml/uss 路径真实存在**（UPM 虚拟路径与仓库目录名不是一回事，
   写错在 Unity 里是静默失败：窗口照常打开，只是样式丢失）。
"""

from __future__ import annotations

import json
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def _find_package_dir() -> Path:
    """按 package.json 定位包目录，避免把目录名写死在脚本里。"""
    candidates = sorted(ROOT.glob("packages/*/package.json"))
    if not candidates:
        return ROOT / "packages" / "com.charforge"
    return candidates[0].parent


PKG = _find_package_dir()


def _package_name() -> str:
    try:
        data = json.loads((PKG / "package.json").read_text(encoding="utf-8"))
        return data.get("name") or PKG.name
    except (OSError, json.JSONDecodeError):
        return PKG.name


# UPM 暴露给 AssetDatabase 的虚拟前缀，取 package.json 的 name，
# **不等于**仓库里的目录名。两者不一致正是最容易写错的地方。
VIRTUAL_PREFIX = f"Packages/{_package_name()}/"

# asmdef 里允许出现的 Unity 侧程序集引用（其余一律要能被包内 asmdef 名解析）
EXTERNAL_ASSEMBLIES = {
    "Newtonsoft.Json",
    "UnityEditor.Animations",
    "Unity.2D.Sprite.Editor",
    "Unity.2D.Animation.Runtime",
    "Unity.InferenceEngine",
    "Unity.Sentis",
}

NAMESPACE_RE = re.compile(r"^\s*namespace\s+([\w.]+)\s*$", re.MULTILINE)
TYPE_RE = re.compile(
    r"^\s*(?:public|internal|static|abstract|sealed|partial|\s)*"
    r"(class|interface|struct|enum)\s+([A-Za-z_]\w*)",
    re.MULTILINE,
)
USING_RE = re.compile(r"^\s*using\s+(?:static\s+)?([\w.]+)\s*;", re.MULTILINE)

# 任何以 .uxml / .uss 结尾的字符串字面量都算资产引用。
# 早期版本这里要求字符串前带 $，导致普通 const 里的路径从未被检查——
# 于是「Packages/com.charforge/...」这个错误路径一直没被发现。
ASSET_REF_RE = re.compile(r'"([^"\n]*\.(?:uxml|uss))"')

KNOWN_NAMESPACE_PREFIXES = (
    "System", "Unity", "UnityEngine", "UnityEditor", "Newtonsoft",
    "CharForge", "TMPro", "NUnit",
)

errors: list[str] = []
warnings: list[str] = []


def fail(msg: str) -> None:
    errors.append(msg)


def warn(msg: str) -> None:
    warnings.append(msg)


def load_asmdefs() -> dict[Path, dict]:
    """返回 {asmdef 所在目录: 定义}。"""
    result: dict[Path, dict] = {}
    for path in PKG.rglob("*.asmdef"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            fail(f"{path.relative_to(ROOT)} 不是合法 JSON：{exc}")
            continue

        for field in ("name", "rootNamespace", "references"):
            if field not in data:
                fail(f"{path.relative_to(ROOT)} 缺少字段 {field}")

        if data.get("versionDefines") and not data.get("defineConstraints") and "SpriteLibrary" in data.get("name", ""):
            warn(f"{path.name} 有 versionDefines 但没配 defineConstraints，可选依赖不会自动跳过编译")

        result[path.parent] = data
    return result


def owning_asmdef(cs_path: Path, asmdefs: dict[Path, dict]) -> Path | None:
    """找出包含该 .cs 的最近 asmdef 目录。"""
    candidates = [d for d in asmdefs if cs_path.is_relative_to(d)]
    if not candidates:
        return None
    return max(candidates, key=lambda d: len(d.parts))


def check_cs_files(asmdefs: dict[Path, dict]) -> None:
    package_assemblies = {d.get("name") for d in asmdefs.values() if d.get("name")}
    seen_types: dict[str, Path] = {}

    for cs in sorted(PKG.rglob("*.cs")):
        rel = cs.relative_to(ROOT)
        text = cs.read_text(encoding="utf-8")

        if text.count("{") != text.count("}"):
            fail(f"{rel} 大括号不配平（{{={text.count('{')} }}={text.count('}')}）")

        if owning_asmdef(cs, asmdefs) is None:
            fail(f"{rel} 不在任何 asmdef 目录内 —— 会被编进 Assembly-CSharp，"
                 "在用户工程里与用户脚本冲突")

        for ns in NAMESPACE_RE.findall(text):
            for kind, name in TYPE_RE.findall(text):
                fqn = f"{ns}.{name}"
                if fqn in seen_types:
                    fail(f"类型重名：{fqn} 同时出现在 {seen_types[fqn].relative_to(ROOT)} 与 {rel}")
                seen_types[fqn] = cs

        for used in USING_RE.findall(text):
            if not used.startswith(KNOWN_NAMESPACE_PREFIXES):
                warn(f"{rel} 引用了不常见的命名空间 {used}")

        for ref in ASSET_REF_RE.findall(text):
            if not ref.startswith("Packages/"):
                continue

            warn(f"{rel} 硬编码了包内路径 {ref}：UPM 虚拟路径随安装形态变化"
                 "（Add package from disk / 嵌入 Packages/ / 注册表），"
                 "加载失败是静默的（界面回落而不报错），建议改用 "
                 "PackageInfo.FindForAssembly 动态解析")

            if not ref.startswith(VIRTUAL_PREFIX):
                fail(f"{rel} 引用了 {ref}，但本包的 UPM 虚拟路径是 {VIRTUAL_PREFIX}"
                     f"（取自 package.json 的 name；仓库目录名 packages/{PKG.name}/ 与它不同）")
                continue

            if not (PKG / ref[len(VIRTUAL_PREFIX):]).exists():
                fail(f"{rel} 引用的资产不存在：{ref}")

    print(f"  .cs 文件：{len(list(PKG.rglob('*.cs')))} 个，类型：{len(seen_types)} 个")


def check_asmdef_references(asmdefs: dict[Path, dict]) -> None:
    package_assemblies = {d.get("name") for d in asmdefs.values() if d.get("name")}

    for directory, data in asmdefs.items():
        name = data.get("name", "?")
        for ref in data.get("references", []):
            if ref in package_assemblies or ref in EXTERNAL_ASSEMBLIES:
                continue
            fail(f"{name}.asmdef 引用了未知程序集「{ref}」——拼错程序集名在 Unity 里"
                 f"会静默变成'程序集引用缺失'的编译错误")

        if data.get("overrideReferences") and not data.get("precompiledReferences"):
            warn(f"{name}.asmdef 开了 overrideReferences 但 precompiledReferences 为空")

    print(f"  asmdef：{len(asmdefs)} 个（{', '.join(sorted(d.get('name', '?') for d in asmdefs.values()))}）")


def check_package_json(asmdefs: dict[Path, dict]) -> None:
    path = PKG / "package.json"
    if not path.exists():
        fail(f"{path.relative_to(ROOT)} 不存在")
        return

    data = json.loads(path.read_text(encoding="utf-8"))
    for field in ("name", "version", "displayName", "unity", "dependencies"):
        if field not in data:
            fail(f"package.json 缺少字段 {field}")

    if data.get("unity") != "6000.0":
        warn(f"package.json 最低 Unity 版本为 {data.get('unity')}，本项目按 Unity 6.0 设计")

    deps = data.get("dependencies", {})

    # 硬引用必须能在 dependencies 里找到对应包，否则用户装上就是一堆编译错误
    hard_ref_to_package = {
        "Newtonsoft.Json": "com.unity.nuget.newtonsoft-json",
        "Unity.2D.Sprite.Editor": "com.unity.2d.sprite",
    }
    all_refs = {r for d in asmdefs.values() for r in d.get("references", [])}
    for ref, package in hard_ref_to_package.items():
        if ref in all_refs and package not in deps:
            fail(f"asmdef 硬引用 {ref}，但 package.json 未声明依赖 {package}")

    # 2D Animation 是可选依赖：应该通过 versionDefines 自动开关，不该写进 dependencies
    if "com.unity.2d.animation" in deps:
        warn("com.unity.2d.animation 被声明为硬依赖；它的版本号随 Unity 版本变化，"
             "改为 versionDefines + defineConstraints 更稳")

    print(f"  package.json：{data['name']} v{data['version']}，Unity {data['unity']}+，"
          f"依赖 {len(deps)} 项")


def main() -> int:
    if not PKG.exists():
        print(f"找不到包目录：{PKG}")
        return 1

    print("校验 Unity 包结构…")
    asmdefs = load_asmdefs()
    if not asmdefs:
        fail("没有找到任何 .asmdef")

    check_asmdef_references(asmdefs)
    check_cs_files(asmdefs)
    check_package_json(asmdefs)

    for w in warnings:
        print(f"  提醒：{w}")
    for e in errors:
        print(f"  错误：{e}")

    if errors:
        print(f"\n未通过：{len(errors)} 个错误，{len(warnings)} 条提醒")
        return 1

    print(f"\n通过：0 个错误，{len(warnings)} 条提醒")
    return 0


if __name__ == "__main__":
    sys.exit(main())
