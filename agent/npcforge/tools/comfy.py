#!/usr/bin/env python3
"""CharForge ↔ ComfyUI 直驱客户端（零第三方依赖）。

它做的事和 Unity 插件窗口里那两个按钮**完全等价**——只是不经过界面：
把工作流的节点按 bindings 注入参数、POST /prompt、轮询 /history、收割输出图。
这条路径是插件的既有实现，本脚本是它的无头复刻，所以两边出的图应当一致。

四个命令：

    probe                                    探测连通性（先跑这个）
    portrait --prompt ... --out a.png        立绘（Qwen-Image-2.1 文生图）
    frames --character ... --refs ...        帧动画（keep_pose 逐帧动作模仿）
    keys                                     打印工作流里当前的节点与字段（排查用）

三个必须知道的坑（都是本项目的实测结论，不是通用经验）：

1. **地址用 `localhost:8000`，不要用 `127.0.0.1:8000`。**
   ComfyUI Desktop 监听 8000 且绑 `0.0.0.0`；而本机 8000 上常另有一个只绑
   `127.0.0.1` 的服务（比如你自己的 FastAPI 后端）。「更具体的绑定优先」，
   于是写 `127.0.0.1:8000` 打到的是它，症状是 `/upload/image` 回 405
   `{"detail":"Method Not Allowed"}`——那是 FastAPI 的报文形状，不是 ComfyUI 的。
   本脚本会自动把 `127.0.0.1` 换成 `localhost` 并提示。

2. **本机服务不走系统代理**，与「外部 LLM」那条链恰好相反。所以这里的 opener
   显式清空 proxy handler；否则会得到连不上本机的怪现象。

3. **keep_pose 需要 ComfyUI Desktop 的浏览器前端在线**（SAM 3D 估计要前端参与，
   同步超时 150 秒）。无头模式跑不了，报错形态是 `Pose Studio frontend sync failed`。
   这不是脚本的 bug，是这条路线的运行前提。
"""

from __future__ import annotations

import argparse
import json
import mimetypes
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

from .png_io import PngError, flatten_alpha_noise, read_png, write_png  # noqa: E402

DEFAULT_URL = "http://localhost:8000"
PORTRAIT_WORKFLOW = "charforge_qwen21_txt2img.workflow.json"
KEEPOSE_WORKFLOW = "charforge_keepose_pose.workflow.json"

_COMFY_ERROR_HINTS = (
    ("frontend sync failed", "ComfyUI Desktop 的前端窗口不在线，或没加载 keep_pose 工作流。"
                             "让 Desktop 窗口保持打开、把 charforge_keepose_pose.workflow.json 载入后重跑。"),
    ("sam3d", "SAM 3D 节点没起来。确认 ComfyUI 端装了 vnccs-utils，且 Desktop 前端在线。"),
    ("vnccs", "缺少 VNCCS 套件。姿势模仿依赖 custom_nodes/vnccs-utils 与 VNCCS_QI2_PoseStudioV1.1 LoRA。"),
    ("out of memory", "显存不够。立绘路线在 16 GB 上要用 INT8 DiT + W4A8 编码器；"
                      "先关掉别的占显存的程序。"),
    ("prompt_outputs_failed_validation", "提交参数没过服务端校验——多半是工作流文件与 bindings 对不上，"
                                         "先跑 `keys` 看节点结构。"),
)


class ComfyError(RuntimeError):
    """带人话解释的失败。调用方可以直接把 message 转述给用户。"""


# ────────────────────────── HTTP 基座 ──────────────────────────

def _opener():
    """没有代理的 opener（本机服务不走代理）。"""
    return urllib.request.build_opener(urllib.request.ProxyHandler({}))


def normalize_url(url: str) -> str:
    """把 127.0.0.1 换成 localhost —— 见模块开头第 1 条。"""
    url = (url or DEFAULT_URL).rstrip("/")
    if "127.0.0.1" in url:
        fixed = url.replace("127.0.0.1", "localhost")
        print(f"[提示] 已把 {url} 换成 {fixed}：本机 8000 常被另一个只绑 127.0.0.1 "
              f"的服务抢占，写 127.0.0.1 打不到 ComfyUI。", file=sys.stderr)
        return fixed
    return url


def _request(url: str, data: bytes | None = None, headers: dict | None = None,
             timeout: float = 30.0) -> bytes:
    req = urllib.request.Request(url, data=data, headers=headers or {})
    try:
        with _opener().open(req, timeout=timeout) as resp:
            return resp.read()
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")[:400]
        raise ComfyError(f"HTTP {e.code} {e.reason} ← {url}\n  {body}") from None
    except urllib.error.URLError as e:
        raise ComfyError(
            f"连不上 ComfyUI：{url}（{e.reason}）\n"
            "  排查顺序：① ComfyUI Desktop 是否在跑；② 端口是不是 8000"
            "（Desktop 版默认 8000，教程里的 8188 在 Desktop 上是空的）；"
            "③ 地址写成 localhost 而不是 127.0.0.1。") from None
    except TimeoutError:
        raise ComfyError(f"请求超时：{url}（已等 {timeout:.0f}s）") from None


def get_json(url: str, timeout: float = 30.0):
    raw = _request(url, timeout=timeout)
    try:
        return json.loads(raw.decode("utf-8"))
    except json.JSONDecodeError:
        raise ComfyError(f"应答不是 JSON ← {url}\n  {raw[:300]!r}\n"
                         "  若报文长得像 {\"detail\": …}，说明这个端口上是别的服务"
                         "（多半是你自己的后端），不是 ComfyUI。") from None


def post_json(url: str, obj) -> dict:
    body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    raw = _request(url, data=body, headers={"Content-Type": "application/json"}, timeout=120.0)
    if not raw:
        return {}
    return json.loads(raw.decode("utf-8"))


def upload_image(base: str, path: Path) -> str:
    """上传一张图到 ComfyUI 的 input 目录，返回可喂给 LoadImage.image 的名字。"""
    data = path.read_bytes()
    mime = mimetypes.guess_type(path.name)[0] or "image/png"
    boundary = "----CharForge" + uuid.uuid4().hex
    body = bytearray()

    for key, value in (("overwrite", "true"), ("type", "input")):
        body += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{key}\"\r\n\r\n"
                 f"{value}\r\n").encode()
    body += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; "
             f"filename=\"{path.name}\"\r\nContent-Type: {mime}\r\n\r\n").encode()
    body += data
    body += b"\r\n"
    body += f"--{boundary}--\r\n".encode()

    raw = _request(f"{base}/upload/image", data=bytes(body),
                   headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
                   timeout=120.0)
    info = json.loads(raw.decode("utf-8"))
    name = info.get("name") or path.name
    sub = info.get("subfolder") or ""
    return f"{sub}/{name}" if sub else name


# ────────────────────────── 工作流装配 ──────────────────────────

class Workflow:
    """一个工作流模板 + 它的 bindings。每次提交都在副本上注入，模板本身不动。"""

    def __init__(self, workflow_path: Path, bindings_path: Path):
        if not workflow_path.exists():
            raise ComfyError(f"找不到工作流文件：{workflow_path}\n"
                             f"  用 --config-dir 指向 <插件包>/Configs/comfyui"
                             "（可以先用 `Unity -executeMethod … -cmd env` 问出来）。")
        self.path = workflow_path
        self.bindings_path = bindings_path
        self.template = json.loads(workflow_path.read_text(encoding="utf-8"))
        self.bindings = (json.loads(bindings_path.read_text(encoding="utf-8"))
                         if bindings_path.exists() else {})
        if not isinstance(self.template, dict) or not self.template:
            raise ComfyError(f"工作流结构不对（应为节点 id → 节点体的字典）：{workflow_path}")

    def node_id(self, key: str) -> str | None:
        value = self.bindings.get(key)
        return str(value) if value not in (None, "") else None

    def fresh(self) -> dict:
        return json.loads(json.dumps(self.template))

    def set_input(self, wf: dict, node_id: str, field: str, value) -> None:
        """给节点的输入字段赋值。**字段不存在时直接抛错**，不静默丢弃。

        这条守卫来自真实事故：`CLIPTextEncode` 的字段叫 `text`，而 Qwen 的
        `TextEncodeQwenImage21` 叫 `prompt`/`negative_prompt`。往不存在的键赋值
        ComfyUI 不报错，只是那张图跟你的提示词毫无关系——看起来像"模型不行"。
        """
        node = wf.get(str(node_id))
        if node is None:
            raise ComfyError(f"工作流里没有节点 {node_id}——bindings 与 workflow 对不上。")
        inputs = node.setdefault("inputs", {})
        if field not in inputs:
            raise ComfyError(
                f"节点 {node_id}（{node.get('class_type')}）没有输入字段 '{field}'。"
                f"现有字段：{', '.join(inputs)}。\n"
                "  往不存在的键赋值不会报错、只会静默失效——所以这里直接拦下。")
        inputs[field] = value

    def set_pose_data_field(self, wf: dict, node_id: str, path: list[str], value) -> None:
        """往节点 `pose_data`（一个 JSON 字符串）里的嵌套字段写值。

        `export.pose_amplify` 就走这条路——它是 vnccs 服务端补丁读取的注入点。
        注意**不是** `poses[].bones`：那张表与 SAM 上行载荷结构不同，
        按它放大等于命中 0 个关节、静默无效（v0.5.11 的教训）。
        """
        node = wf.get(str(node_id))
        if node is None:
            raise ComfyError(f"工作流里没有节点 {node_id}。")
        raw = node.get("inputs", {}).get("pose_data")
        if not isinstance(raw, str):
            raise ComfyError(f"节点 {node_id} 的 pose_data 不是字符串（实际 {type(raw).__name__}）。")

        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            raise ComfyError(f"节点 {node_id} 的 pose_data 不是合法 JSON，无法注入。") from None

        cursor = data
        for key in path[:-1]:
            if not isinstance(cursor, dict) or key not in cursor:
                raise ComfyError(f"pose_data 里没有路径 {'/'.join(path)}"
                                 f"（在 '{key}' 处断了）——结构变了，先跑 `keys` 看。")
            cursor = cursor[key]
        if not isinstance(cursor, dict):
            raise ComfyError(f"pose_data 的 {'/'.join(path[:-1])} 不是对象。")
        cursor[path[-1]] = value

        node["inputs"]["pose_data"] = json.dumps(data, ensure_ascii=False, separators=(",", ":"))


def resolve_workflow(config_dir: Path, name: str) -> tuple[Path, Path]:
    return config_dir / name, config_dir / name.replace(".workflow.json", ".bindings.json")


def compose_portrait_prompt(wf: Workflow, body: str) -> str:
    """把「角色本体」包成最终立绘提示词。

    **不许整体覆盖正节点的提示词。** 工作流节点 6 里那串默认提示词是三段拼起来的：
    透明底句 + 画风前缀 + 角色本体 + 版式后缀。直接往正节点写一个字符串，
    前两段就没了——症状是输出 100% 实心、没有 alpha 通道的图，而**没有任何报错**。
    再往下，`FrameNormalizer` 靠 alpha 算包围盒，会在整张图上算出一个"包围盒"，
    于是一批帧的缩放基准全错。这类失败要跑很久才显形，所以在这里挡住。

    固定件由**插件**持有（`PromptForgeService.StylePrefix` / `LayoutSuffix` / `QwenAlphaHint`），
    bindings 里的是它们的机器可读副本——下游照抄，不自己发明。
    校验：`python scripts/check_prompt_parts.py`（bindings ↔ C# 常量 ↔ 工作流节点 6 三处对齐）。
    """
    parts = wf.bindings.get("prompt_parts")
    if not isinstance(parts, dict):
        raise ComfyError(
            f"bindings 里没有 prompt_parts，无法安全地拼立绘提示词：{wf.bindings_path}\n"
            "  硬写一个字符串进去会把透明底句和画风前缀顶掉——出的图会是实心底，且不报错。\n"
            "  修复：从插件包更新这个 bindings 文件（它随包发布，不该手改）。")
    missing = [k for k in ("alpha_hint", "style_prefix", "layout_suffix") if not parts.get(k)]
    if missing:
        raise ComfyError(f"prompt_parts 缺字段：{missing}（在 {wf.bindings_path}）")

    body = (body or "").strip().rstrip("。. ")
    if not body:
        raise ComfyError("角色本体是空的——提示词里只剩固定件，等于让模型自由发挥。")
    return f"{parts['alpha_hint']} {parts['style_prefix']}{body}。{parts['layout_suffix']}"


def load_config_dir(args) -> Path:
    raw = args.config_dir or os.environ.get("CHARFORGE_CONFIG_DIR") or ""
    if not raw:
        raise ComfyError(
            "没给出配置目录。二选一：\n"
            "  --config-dir <插件包>/Configs/comfyui\n"
            "  export CHARFORGE_CONFIG_DIR=<插件包>/Configs/comfyui\n"
            "  不知道插件包在哪就问 Unity：\n"
            "  Unity.exe -batchmode -projectPath <工程> "
            "-executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd env\n"
            "    看输出里的 package_config_root。")
    path = Path(raw).expanduser().resolve()
    if not path.is_dir():
        raise ComfyError(f"配置目录不存在：{path}")
    return path


# ────────────────────────── 提交与收割 ──────────────────────────

def submit(base: str, wf: dict, client_id: str) -> str:
    payload = {"prompt": wf, "client_id": client_id}
    try:
        resp = post_json(f"{base}/prompt", payload)
    except ComfyError as e:
        low = str(e).lower()
        for needle, hint in _COMFY_ERROR_HINTS:
            if needle in low:
                raise ComfyError(f"{e}\n  → {hint}") from None
        raise
    pid = resp.get("prompt_id")
    if not pid:
        raise ComfyError(f"提交后没拿到 prompt_id：{json.dumps(resp, ensure_ascii=False)[:300]}")
    return pid


def wait_for(base: str, prompt_id: str, timeout: float, poll: float = 1.0) -> dict:
    deadline = time.time() + timeout
    last_note = 0.0
    while time.time() < deadline:
        hist = get_json(f"{base}/history/{prompt_id}", timeout=30.0)
        if prompt_id in hist:
            entry = hist[prompt_id]
            status = entry.get("status", {})
            if status.get("status_str") == "error":
                messages = json.dumps(status.get("messages", []), ensure_ascii=False)[:500]
                for needle, hint in _COMFY_ERROR_HINTS:
                    if needle in messages.lower():
                        raise ComfyError(f"ComfyUI 报错：{messages}\n  → {hint}")
                raise ComfyError(f"ComfyUI 报错：{messages}")
            return entry

        now = time.time()
        if now - last_note > 15:
            print(f"  …等待中（{int(now - (deadline - timeout))}s / 上限 {int(timeout)}s）", flush=True)
            last_note = now
        time.sleep(poll)

    raise ComfyError(f"等超时（{timeout:.0f}s）还没出结果。\n"
                     "  帧动画每帧 30–60 秒属正常，但**卡住不动**多半是 ComfyUI Desktop "
                     "前端不在线导致 Pose Studio 同步挂起。看一眼 ComfyUI 控制台窗口。")


def download_outputs(base: str, entry: dict, out_dir: Path, stem: str) -> list[Path]:
    """把这次执行产出的图下载到 out_dir。"""
    out_dir.mkdir(parents=True, exist_ok=True)
    outputs = entry.get("outputs") or {}
    saved: list[Path] = []
    index = 0

    for node_id in sorted(outputs, key=lambda x: (len(x), x)):
        for img in outputs[node_id].get("images", []) or []:
            query = urllib.parse.urlencode({
                "filename": img.get("filename", ""),
                "subfolder": img.get("subfolder", ""),
                "type": img.get("type", "output"),
            })
            blob = _request(f"{base}/view?{query}", timeout=120.0)
            index += 1
            target = out_dir / (f"{stem}.png" if index == 1 and stem else f"{stem}-{index}.png")
            target.write_bytes(blob)
            saved.append(target)

    if not saved:
        raise ComfyError("这次执行没有产出任何图片——工作流的输出节点可能被改过。")
    return saved


def harvest(src: Path, dst: Path, alpha_cutoff: int = 64) -> dict:
    """收割一帧：压平半透明噪点 → 存盘 → 返回体检数据。

    **alpha 压平是必做步骤**：keep_pose 的输出背景带 alpha 1–63 的半透明噪点
    （实测约占画面 44% 的像素），预览里看不出来，进 Unity 就是一层灰雾。
    """
    try:
        png = read_png(src)
    except PngError as e:
        raise ComfyError(f"读不了输出图 {src}：{e}") from None

    flattened = flatten_alpha_noise(png, alpha_cutoff)
    dst.parent.mkdir(parents=True, exist_ok=True)
    write_png(dst, png.width, png.height, png.channels, bytes(png.pixels))

    opaque = sum(1 for i in range(3, len(png.pixels), 4) if png.pixels[i] >= alpha_cutoff) \
        if png.channels == 4 else png.width * png.height
    total = png.width * png.height
    return {
        "file": dst.name,
        "size": f"{png.width}x{png.height}",
        "channels": png.channels,
        "flattened_pixels": flattened,
        "opaque_pct": round(100.0 * opaque / total, 1) if total else 0.0,
    }


# ────────────────────────── 命令 ──────────────────────────

def cmd_probe(args) -> int:
    base = normalize_url(args.url)
    stats = get_json(f"{base}/system_stats", timeout=8.0)
    sysinfo = stats.get("system", {}) if isinstance(stats, dict) else {}
    print(f"在线：ComfyUI {sysinfo.get('comfyui_version', '?')}  ← {base}")
    for dev in stats.get("devices", []) or []:
        total = dev.get("vram_total", 0)
        free = dev.get("vram_free", 0)
        print(f"  设备 {dev.get('name', '?')}：显存 {(total - free) / 2**30:.1f} / "
              f"{total / 2**30:.1f} GB 已用")
    return 0


def cmd_keys(args) -> int:
    cfg = load_config_dir(args)
    name = args.workflow or KEEPOSE_WORKFLOW
    wf = Workflow(*resolve_workflow(cfg, name))
    print(f"工作流 {name}（{len(wf.template)} 个节点）")
    print(f"bindings：{json.dumps({k: v for k, v in wf.bindings.items() if not k.startswith('_')}, ensure_ascii=False)}")
    for node_id in sorted(wf.template, key=lambda x: (len(x), x)):
        node = wf.template[node_id]
        fields = []
        for field, value in (node.get("inputs") or {}).items():
            mark = "←链接" if isinstance(value, list) and len(value) == 2 and isinstance(value[1], int) else ""
            shown = json.dumps(value, ensure_ascii=False)
            if len(shown) > 60:
                shown = shown[:57] + "…"
            fields.append(f"{field}={shown}{mark}")
        print(f"  {node_id:>4} {node.get('class_type', '?'):<28} {' | '.join(fields)}")
    return 0


def cmd_portrait(args) -> int:
    base = normalize_url(args.url)
    cfg = load_config_dir(args)
    wf = Workflow(*resolve_workflow(cfg, args.workflow or PORTRAIT_WORKFLOW))

    body = args.prompt or ""
    if body.startswith("@"):
        body = Path(body[1:]).read_text(encoding="utf-8").strip()
    if not body:
        raise ComfyError("没有提示词。用 --prompt '...' 或 --prompt @提示词.txt。")

    # `--prompt` 给的是**角色本体**，不是最终提示词。固定件（透明底句 / 画风前缀 /
    # 版式后缀）由 bindings 提供并在这里包上去。`--raw` 才是"我就是要原样写进去"。
    prompt = body if args.raw else compose_portrait_prompt(wf, body)

    work = wf.fresh()
    positive = wf.node_id("positive")
    if not positive:
        raise ComfyError("bindings 里没有 'positive' 节点，无法注入提示词。")

    wf.set_input(work, positive, wf.bindings.get("positive_input", "prompt"), prompt)
    neg = wf.node_id("negative")
    if neg:
        wf.set_input(work, neg, wf.bindings.get("negative_input", "negative_prompt"), args.negative or "")

    seed_node = wf.node_id("seed")
    seed = args.seed if args.seed is not None else stable_seed(args.name or body)
    if seed_node:
        wf.set_input(work, seed_node, "seed", seed)

    print(f"提交立绘：种子 {seed}，提示词 {len(prompt)} 字"
          f"（角色本体 {len(body)} 字{'' if args.raw else '+ 固定件'}）")
    pid = submit(base, work, args.client_id)
    entry = wait_for(base, pid, args.timeout)
    files = download_outputs(base, entry, Path(args.out).parent, Path(args.out).stem)

    report = harvest(files[0], Path(args.out), args.alpha_cutoff)
    print(f"立绘已存：{args.out}（{report['size']}，不透明 {report['opaque_pct']}%，"
          f"压平噪点 {report['flattened_pixels']} px）")

    # 透明底体检。立绘要的是「无背景、无地面」的干净抠图素材，
    # 底子实心意味着下游 `FrameNormalizer` 会在整张图上算包围盒——不报错，只是全错。
    report["alpha_ok"] = report["channels"] == 4 and report["opaque_pct"] < 95.0
    if report["channels"] == 3:
        print("  [注意] 输出没有 alpha 通道——立绘要透明底，检查工作流是不是被改过。")
    elif not report["alpha_ok"]:
        print(f"  [注意] 不透明 {report['opaque_pct']}% 且不透明——几乎肯定没有透明背景。"
              "透明底句可能在拼装时被顶掉了（见 bindings 的 prompt_parts）。")

    report["out"] = str(args.out)
    report["seed"] = seed
    report["prompt_words"] = len(prompt.split())
    report["prompt_chars"] = len(prompt)
    return report


def cmd_frames(args) -> int:
    base = normalize_url(args.url)
    cfg = load_config_dir(args)
    wf = Workflow(*resolve_workflow(cfg, args.workflow or KEEPOSE_WORKFLOW))

    character = Path(args.character)
    if not character.exists():
        raise ComfyError(f"找不到立绘：{character}")

    refs_dir = Path(args.refs)
    refs = sorted((p for p in refs_dir.glob("*.png")),
                  key=lambda p: _natural(p.stem)) if refs_dir.is_dir() else []
    if not refs:
        raise ComfyError(f"姿势参考目录里没有 PNG：{refs_dir}\n"
                         "  目录约定：<动作>/<朝向>/*.png，通常是 k1.png … k8.png。")
    if args.limit and args.limit > 0:
        refs = refs[:args.limit]

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    char_slot = wf.node_id("load_image_character")
    pose_slot = wf.node_id("load_image_pose")
    seed_node = wf.node_id("seed")
    if not char_slot or not pose_slot or not seed_node:
        raise ComfyError("bindings 缺少 load_image_character / load_image_pose / seed 之一。")

    amplify_by_dir = wf.bindings.get("pose_amplify_by_direction") or {}
    amplify = args.amplify
    if amplify is None:
        amplify = float(amplify_by_dir.get(args.direction, 1.0)) if amplify_by_dir else 1.0

    seed = args.seed if args.seed is not None else stable_seed(f"{args.name or character.stem}")

    print(f"上传立绘：{character.name}")
    char_name = upload_image(base, character)

    print(f"本组 {len(refs)} 帧；种子 {seed}（逐帧共用）；放大系数 ×{amplify}"
          f"（方向 {args.direction or '未指定'}）")
    print("  ⚠ 姿势参考由 SAM 3D 实时估计，**ComfyUI Desktop 前端必须在线**，否则会同步超时。")

    results = []
    for i, ref in enumerate(refs, 1):
        work = wf.fresh()
        wf.set_input(work, char_slot, "image", char_name)
        wf.set_input(work, seed_node, "seed", seed)

        pose_name = upload_image(base, ref)
        wf.set_input(work, pose_slot, "image", pose_name)

        # 姿势复制提示词：覆写 484.prompt，顶掉 PoseStudio 的 lighting_prompt 链接。
        # **这是有意的**——Qwen-Image-Edit 2.1 对 image_1 的姿势服从度不足，
        # 不在提示词里显式要求"严格复制"就会退回站姿。文案三要素缺一不可（含透明背景句）。
        copy_node = wf.node_id("pose_copy_prompt_node")
        copy_text = wf.bindings.get("pose_copy_prompt")
        if copy_node and copy_text:
            wf.set_input(work, copy_node, "prompt", copy_text)

        # 背面/正面走路的摆幅在 2D 投影里天然弱（只有侧面的 1/3~1/4），
        # 弱信号到扩散端会被站姿先验吞掉——所以按方向放大。
        if amplify and abs(amplify - 1.0) > 1e-6:
            pose_node = wf.node_id("pose_data_node")
            if pose_node:
                wf.set_pose_data_field(work, pose_node, ["export", "pose_amplify"], float(amplify))

        print(f"[{i}/{len(refs)}] {ref.name} →", end=" ", flush=True)
        started = time.time()
        pid = submit(base, work, args.client_id)
        entry = wait_for(base, pid, args.timeout)
        files = download_outputs(base, entry, out_dir / "_raw", f"raw-{i}")

        target = out_dir / f"frame-{i}.png"
        report = harvest(files[0], target, args.alpha_cutoff)
        report["source"] = ref.name
        report["seconds"] = round(time.time() - started, 1)
        report["out"] = str(target)
        results.append(report)
        print(f"{report['seconds']}s，不透明 {report['opaque_pct']}%"
              + (f"，压平噪点 {report['flattened_pixels']} px" if report["flattened_pixels"] else ""))

    report = {"ok": True, "group": out_dir.name, "out_dir": str(out_dir), "seed": seed,
              "amplify": amplify, "frames": results}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return report


def stable_seed(key: str) -> int:
    """由字符串派生一个稳定种子（同一角色重跑得到同一颗，便于复现）。

    与 C# 侧 `CharacterAssetService.StableSeed` 是同一套意图，但**不要求数值一致**——
    它只服务于"这一批内部一致、跨批可复现"。
    """
    h = 2166136261
    for ch in key.encode("utf-8"):
        h = ((h ^ ch) * 16777619) & 0xFFFFFFFF
    return h % (2**31)


def _natural(text: str):
    import re
    return [int(t) if t.isdigit() else t.lower() for t in re.split(r"(\d+)", text)]


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="CharForge ↔ ComfyUI 直驱客户端")
    p.add_argument("--config-dir", help="<插件包>/Configs/comfyui（或用 CHARFORGE_CONFIG_DIR）")
    p.add_argument("--url", default=os.environ.get("COMFYUI_URL", DEFAULT_URL))
    p.add_argument("--client-id", default="charforge-agent")
    p.add_argument("--alpha-cutoff", type=int, default=64,
                   help="alpha 压平阈值（默认 64；keep_pose 的输出噪点落在 1~63）")
    sub = p.add_subparsers(dest="cmd", required=True)

    probe = sub.add_parser("probe", help="探测连通性")
    probe.set_defaults(func=cmd_probe)

    keys = sub.add_parser("keys", help="打印工作流节点与字段")
    keys.add_argument("--workflow", default=None)
    keys.set_defaults(func=cmd_keys)

    portrait = sub.add_parser("portrait", help="生成立绘")
    portrait.add_argument("--prompt", required=True,
                          help="**角色本体**（不是完整提示词），或 @文件名 读文件")
    portrait.add_argument("--raw", action="store_true",
                          help="跳过固定件拼装，把 --prompt 原样写进正节点（排查用，"
                               "会丢掉透明底句与画风前缀）")
    portrait.add_argument("--negative", default="")
    portrait.add_argument("--out", required=True)
    portrait.add_argument("--name", default="", help="用于派生稳定种子")
    portrait.add_argument("--seed", type=int, default=None)
    portrait.add_argument("--timeout", type=float, default=300.0)
    portrait.add_argument("--workflow", default=None)
    portrait.set_defaults(func=cmd_portrait)

    frames = sub.add_parser("frames", help="逐帧生成帧动画")
    frames.add_argument("--character", required=True, help="立绘 PNG")
    frames.add_argument("--refs", required=True, help="姿势参考目录（严格两层里的最内层）")
    frames.add_argument("--out-dir", required=True)
    frames.add_argument("--direction", default="", help="up/down/left/right，决定默认放大系数")
    frames.add_argument("--amplify", type=float, default=None,
                        help="覆盖姿势放大系数（不给则按方向查 bindings）")
    frames.add_argument("--name", default="", help="用于派生稳定种子")
    frames.add_argument("--seed", type=int, default=None)
    frames.add_argument("--limit", type=int, default=0)
    frames.add_argument("--timeout", type=float, default=900.0)
    frames.add_argument("--workflow", default=None)
    frames.set_defaults(func=cmd_frames)

    return p


class ComfyClient:
    """给 Agent 用的面向对象入口。

    内部**直接复用上面那套命令实现**——CLI 与 Agent 走的是同一段代码，
    所以两边出的图必然一致，不会出现"手动跑得通、Agent 跑出来不一样"这种最难查的分歧。
    """

    def __init__(self, config_dir, url: str = DEFAULT_URL, alpha_cutoff: int = 64,
                 portrait_timeout: float = 300.0, frames_timeout: float = 900.0,
                 client_id: str = "npcforge", log=print):
        self.config_dir = str(config_dir)
        self.url = url
        self.alpha_cutoff = alpha_cutoff
        self.portrait_timeout = portrait_timeout
        self.frames_timeout = frames_timeout
        self.client_id = client_id
        self.log = log

    def _ns(self, **overrides):
        from types import SimpleNamespace
        base = dict(config_dir=self.config_dir, url=self.url, alpha_cutoff=self.alpha_cutoff,
                    client_id=self.client_id, workflow=None, raw=False)
        base.update(overrides)
        return SimpleNamespace(**base)

    def _emit(self, buffer) -> None:
        for line in buffer.getvalue().splitlines():
            if line.strip():
                self.log(line)

    def probe(self) -> dict:
        """探活。返回 system_stats 的关键字段；连不上会抛 ComfyError。"""
        import contextlib
        import io
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            cmd_probe(self._ns())
        self._emit(buf)

        base = normalize_url(self.url)
        stats = get_json(f"{base}/system_stats", timeout=8.0)
        info = stats.get("system", {}) if isinstance(stats, dict) else {}
        return {"url": base, "version": info.get("comfyui_version", "?"),
                "devices": stats.get("devices", []) or []}

    def portrait(self, body: str, out_path, seed: int | None = None,
                 name: str = "", negative: str = "", raw: bool = False) -> dict:
        """出立绘。

        `body` 是**角色本体**（`PromptDraft.prompt`），不是最终提示词——
        固定件（透明底句 / 画风前缀 / 版式后缀）在 `cmd_portrait` 里按 bindings 包上去。
        `raw=True` 才原样注入（排查用）。
        """
        import contextlib
        import io
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            report = cmd_portrait(self._ns(prompt=body, out=str(out_path), seed=seed,
                                           name=name, negative=negative, raw=raw,
                                           timeout=self.portrait_timeout))
        self._emit(buf)
        return report

    def frames(self, character, refs, out_dir, direction: str = "",
               amplify: float | None = None, seed: int | None = None,
               name: str = "", limit: int = 0) -> dict:
        import contextlib
        import io
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            report = cmd_frames(self._ns(character=str(character), refs=str(refs),
                                         out_dir=str(out_dir), direction=direction,
                                         amplify=amplify, seed=seed, name=name, limit=limit,
                                         timeout=self.frames_timeout))
        self._emit(buf)
        return report


def main() -> int:
    args = build_parser().parse_args()
    try:
        result = args.func(args)
    except ComfyError as e:
        print(f"\n[失败] {e}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\n[中断] 用户取消。", file=sys.stderr)
        return 130
    # 命令返回 dict 表示成功（结构化结果），返回 int 就是退出码本身。
    return result if isinstance(result, int) else 0


if __name__ == "__main__":
    sys.exit(main())
