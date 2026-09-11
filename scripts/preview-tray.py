#!/usr/bin/env python3
"""把菜单栏模板图按 macOS 的做法染色，拼成一张「浅色栏 / 深色栏」对照预览。

`scripts/make-icon.py` 顶上第 5 条写着「每个候选都要在 16px 与深浅两底上看过才算数」，
但那句话一直没有对应的工具 —— 要看就得打包、装上、盯着自己的菜单栏，而模板图在编辑器里
看到的是一团黑（颜色会被 macOS 丢掉，只有 alpha 有意义），跟它上栏之后的样子没关系。

这里做的就是 macOS 那一步：拿标签色（浅色栏近黑、深色栏白）透过 alpha 染色，
放大若干倍，上下两行分别压在两种菜单栏底色上。

    python3 scripts/preview-tray.py /tmp/tray.png

不传文件名就是 `src/Conclave.App/Assets` 下全部 `conclave-tray-*.png`（空闲、评审中、
7 张呼吸帧），按文件名排序。

跟 make-icon.py 一样不依赖任何图像库：PNG 编码器那边是手写的，这边就手写个解码器 ——
只解自己生成的那一种（8-bit RGBA、无交错、每行 filter 0），够用且不会悄悄支持错。

⚠️ 这是模拟，不是截图。它验的是「alpha 分布对不对」；真机上还有壁纸透上来、
「降低透明度」这类设置，最终还得肉眼在真菜单栏上看一眼。
"""
import struct
import sys
import zlib
from pathlib import Path

ASSETS = Path(__file__).resolve().parent.parent / "src/Conclave.App/Assets"

# 菜单栏底色与标签色。取的是实测的近似值，差几个灰阶不影响判断 alpha 分布。
LIGHT_BAR, LIGHT_INK = (0xEC, 0xEC, 0xEC), (0x00, 0x00, 0x00)
DARK_BAR, DARK_INK = (0x2A, 0x2C, 0x2E), (0xFF, 0xFF, 0xFF)

ZOOM = 6        # 最近邻放大：要看的是每个像素的 alpha，不能让插值把它抹匀
GAP = 8


def read_png(path):
    """解一张 8-bit RGBA、无交错、每行 filter 0 的方形 PNG，返回 (边长, 每行字节)。"""
    data = Path(path).read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} 不是 PNG")

    pos, idat, size = 8, b"", None
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if (depth, ctype, interlace) != (8, 6, 0):
                raise ValueError(f"{path} 不是 8-bit RGBA 无交错（{depth}/{ctype}/{interlace}）")
            if width != height:
                raise ValueError(f"{path} 不是方的（{width}x{height}）")
            size = width
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length

    raw = zlib.decompress(idat)
    stride = size * 4
    rows = []
    for y in range(size):
        if raw[y * (stride + 1)] != 0:
            raise ValueError(f"{path} 第 {y} 行用了 filter，这个解码器只认 0")
        rows.append(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
    return size, rows


def write_png(path, width, height, pixels):
    def chunk(kind, body):
        return (struct.pack(">I", len(body)) + kind + body
                + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF))

    raw = b"".join(
        b"\x00" + bytes(pixels[y * width * 4:(y + 1) * width * 4]) for y in range(height))
    Path(path).write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b""))


def compose(tiles):
    """两行（浅色栏 / 深色栏）× N 列，返回 (宽, 高, 像素)。"""
    cell = tiles[0][0] * ZOOM
    width = len(tiles) * cell + (len(tiles) + 1) * GAP
    height = 2 * cell + 3 * GAP
    out = bytearray()

    for y in range(height):
        dark = y >= GAP + cell
        bar, ink = (DARK_BAR, DARK_INK) if dark else (LIGHT_BAR, LIGHT_INK)
        oy = y - (GAP + (cell + GAP) * (1 if dark else 0))

        for x in range(width):
            col = (x - GAP) // (cell + GAP)
            ox = x - (GAP + col * (cell + GAP))

            rgb = bar
            if 0 <= col < len(tiles) and 0 <= ox < cell and 0 <= oy < cell:
                size, rows = tiles[col]
                alpha = rows[oy // ZOOM][(ox // ZOOM) * 4 + 3] / 255.0
                rgb = tuple(int(round(ink[i] * alpha + bar[i] * (1 - alpha))) for i in range(3))
            out += bytes((*rgb, 255))

    return width, height, out


if __name__ == "__main__":
    out_path = Path(sys.argv[1] if len(sys.argv) > 1 else "tray-preview.png")
    names = sys.argv[2:] or sorted(p.name for p in ASSETS.glob("conclave-tray-*.png"))
    if not names:
        raise SystemExit(f"{ASSETS} 下没有 conclave-tray-*.png")

    tiles = [read_png(ASSETS / name) for name in names]
    width, height, pixels = compose(tiles)
    write_png(out_path, width, height, pixels)
    print(f"{out_path}  {width}x{height}  上=浅色栏 下=深色栏")
    for name in names:
        print(f"  · {name}")
