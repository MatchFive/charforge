#!/usr/bin/env python3
"""零依赖 PNG 读写（只覆盖 8-bit 的灰度 / 灰度+alpha / RGB / RGBA）。

为什么不用 Pillow：这个技能包要在**用户的机器上直接跑**，而"先装个 Pillow"
在离线或受限环境里是一道没必要的坎。PNG 的 8-bit 非隔行子集足够覆盖
全部需要处理的图：ComfyUI 的输出、视频抽帧、以及本流水线的帧产物。
遇到超出子集的文件会明确报错，不会悄悄读错。
"""

from __future__ import annotations

import struct
import zlib

PNG_SIG = b"\x89PNG\r\n\x1a\n"
_CHANNELS_OF = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


class PngError(RuntimeError):
    """读不了 / 不在支持子集内。带人话说明，便于直接转述给用户。"""


class Png:
    """一张 8-bit PNG。`pixels` 是逐行紧凑排列的字节，通道数见 `channels`。"""

    def __init__(self, width: int, height: int, channels: int, pixels: bytearray):
        self.width = width
        self.height = height
        self.channels = channels
        self.pixels = pixels

    @property
    def stride(self) -> int:
        return self.width * self.channels

    def px(self, x: int, y: int) -> tuple[int, ...]:
        base = y * self.stride + x * self.channels
        return tuple(self.pixels[base:base + self.channels])

    def alpha_at(self, x: int, y: int) -> int:
        """取 alpha；没有 alpha 通道时按 255（完全不透明）算。"""
        if self.channels == 4:
            return self.pixels[y * self.stride + x * 4 + 3]
        if self.channels == 2:
            return self.pixels[y * self.stride + x * 2 + 1]
        return 255


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def _unfilter(raw: bytes, width: int, height: int, channels: int) -> bytearray:
    """把 PNG 的逐行 filter 还原成紧凑像素。"""
    stride = width * channels
    out = bytearray(stride * height)
    pos = 0
    prev = bytearray(stride)

    for y in range(height):
        if pos >= len(raw):
            raise PngError(f"数据提前结束（第 {y} 行），文件可能损坏。")
        ftype = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        if len(line) != stride:
            raise PngError(f"第 {y} 行数据长度不足，文件可能损坏。")
        pos += stride

        if ftype == 1:      # Sub
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ftype == 2:    # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:    # Average
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:    # Paeth
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                upleft = prev[i - channels] if i >= channels else 0
                line[i] = (line[i] + _paeth(left, prev[i], upleft)) & 0xFF
        elif ftype != 0:
            raise PngError(f"未知的 filter 类型 {ftype}（第 {y} 行）。")

        out[y * stride:(y + 1) * stride] = line
        prev = line

    return out


def read_png(path) -> Png:
    """读一张 PNG。超出「8-bit 非隔行」子集时抛 PngError。"""
    with open(path, "rb") as fh:
        data = fh.read()

    if not data.startswith(PNG_SIG):
        raise PngError(f"{path} 不是 PNG 文件（签名不对）。")

    pos = len(PNG_SIG)
    width = height = depth = ctype = None
    idat = bytearray()

    while pos + 8 <= len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length

        if ctag == b"IHDR":
            width, height, depth, ctype, comp, filt, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8:
                raise PngError(f"只支持 8-bit 深度，这张是 {depth}-bit。")
            if interlace != 0:
                raise PngError("不支持隔行（Adam7）PNG。")
            if ctype not in _CHANNELS_OF:
                raise PngError(f"不支持的颜色类型 {ctype}。")
        elif ctag == b"IDAT":
            idat.extend(body)
        elif ctag == b"IEND":
            break

    if width is None:
        raise PngError("没有找到 IHDR，文件可能损坏。")
    if ctype == 3:
        raise PngError("不支持索引色（palette）PNG——先转成 RGBA 再处理。")

    channels = _CHANNELS_OF[ctype]
    raw = zlib.decompress(bytes(idat))
    return Png(width, height, channels, _unfilter(raw, width, height, channels))


def write_png(path, width: int, height: int, channels: int, pixels: bytes) -> None:
    """写一张 8-bit PNG（RGBA / RGB / 灰度）。"""
    if channels not in (1, 2, 3, 4):
        raise PngError(f"不支持的通道数 {channels}。")
    ctype = {1: 0, 2: 4, 3: 2, 4: 6}[channels]

    stride = width * channels
    if len(pixels) != stride * height:
        raise PngError(f"像素数据长度 {len(pixels)} != 期望 {stride * height}。")

    raw = bytearray()
    for y in range(height):
        raw.append(0)                                     # filter = None
        raw.extend(pixels[y * stride:(y + 1) * stride])

    def chunk(tag: bytes, body: bytes) -> bytes:
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF))

    out = bytearray(PNG_SIG)
    out.extend(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, ctype, 0, 0, 0)))
    out.extend(chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
    out.extend(chunk(b"IEND", b""))

    with open(path, "wb") as fh:
        fh.write(out)


def flatten_alpha_noise(png: Png, cutoff: int = 64) -> int:
    """把 `alpha < cutoff` 的像素压成**全透明**，就地修改。返回被压平的像素数。

    keep_pose 的输出背景带半透明噪点（实测约占画面 44% 的像素、alpha 落在 1~63），
    它们在预览里看不出来，但进 Unity 会变成一层灰雾，还会让 FrameNormalizer 的
    alpha 裁切算错包围盒。**这是收割时必做的一步，不是可选优化。**
    """
    if png.channels != 4:
        return 0

    touched = 0
    px = png.pixels
    for i in range(3, len(px), 4):
        a = px[i]
        if a < cutoff:
            if a != 0:
                touched += 1
            px[i - 3] = 0
            px[i - 2] = 0
            px[i - 1] = 0
            px[i] = 0
    return touched


def alpha_stats(png: Png) -> dict:
    """统计透明 / 半透明 / 不透明三类像素的占比。

    用切片一次取出全部 alpha（而不是逐像素双循环）——1024² 的图有 100 万个像素，
    双重 Python 循环会慢到让整轮质检卡住，而 `bytes` 切片是 C 层做的。
    """
    total = png.width * png.height

    if png.channels == 4:
        alpha = bytes(png.pixels[3::4])
    elif png.channels == 2:
        alpha = bytes(png.pixels[1::2])
    else:
        return {
            "pixels": total,
            "clear": 0, "clear_pct": 0.0,
            "semi": 0, "semi_pct": 0.0,
            "opaque": total, "opaque_pct": 100.0,
            "has_alpha": False,
        }

    clear = alpha.count(0)
    semi = sum(1 for a in alpha if 0 < a < 64)
    opaque = total - clear - semi

    def pct(n: int) -> float:
        return round(100.0 * n / total, 1) if total else 0.0

    return {
        "pixels": total,
        "clear": clear, "clear_pct": pct(clear),
        "semi": semi, "semi_pct": pct(semi),
        "opaque": opaque, "opaque_pct": pct(opaque),
        "has_alpha": True,
    }
