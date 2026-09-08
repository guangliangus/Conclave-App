#!/usr/bin/env python3
"""生成 Conclave 的应用图标。

图案就是这个系统的本体：一条自下而上生长的哈希链，最上面一块是链尾（最新区块），
用近白色标出。三块 + 两个连接件，在 16px 下也还能读成「一叠区块」。

纯标准库（zlib + struct），不引任何图像库 —— 图标要能在 CI 上无依赖重现。
用 2x 超采样做抗锯齿。
"""
import struct
import sys
import zlib
from pathlib import Path

SIZE = 1024
SS = 2                      # 超采样倍数
GROUND = (0x14, 0x14, 0x2B)
BLOCK = (0xE9, 0xB4, 0x4C)
HEAD = (0xF5, 0xF0, 0xE6)


def rounded_rect(x, y, w, h, r):
    """返回一个判定函数：点是否落在圆角矩形内（坐标为 0..1 归一化）。

    做法是把矩形向内收缩 r 得到「芯」，再判到芯的距离是否 <= r。
    不能用「到原矩形的外部距离」那种写法 —— 那样圆角是向外长出来的，
    矩形内部恒为真，圆角根本不会生效。
    """
    r = min(r, w / 2.0, h / 2.0)
    cx0, cx1 = x + r, x + w - r
    cy0, cy1 = y + r, y + h - r

    def inside(px, py):
        if px < x or px > x + w or py < y or py > y + h:
            return False
        dx = max(cx0 - px, px - cx1, 0.0)
        dy = max(cy0 - py, py - cy1, 0.0)
        return dx * dx + dy * dy <= r * r

    return inside


def build_shapes():
    """自下而上的三块 + 两个连接件。最上面一块是链尾（最新区块），用近白色标出。"""
    bar_w, bar_h, gap = 0.50, 0.115, 0.105
    link_w = 0.065

    stack_h = 3 * bar_h + 2 * gap
    top = (1.0 - stack_h) / 2.0
    x = (1.0 - bar_w) / 2.0
    link_x = (1.0 - link_w) / 2.0

    shapes = [(rounded_rect(0.0, 0.0, 1.0, 1.0, 0.225), GROUND)]

    # 连接件先画，随后的区块会盖住它的两端，看上去就是块与块被串起来
    for i in range(2):
        link_y = top + bar_h + i * (bar_h + gap) - 0.004
        shapes.append((rounded_rect(link_x, link_y, link_w, gap + 0.008, 0.018), BLOCK))

    for i in range(3):
        y = top + i * (bar_h + gap)
        shapes.append((rounded_rect(x, y, bar_w, bar_h, 0.032), HEAD if i == 0 else BLOCK))

    return shapes


def render(size, ss):
    shapes = build_shapes()
    n = size * ss
    step = 1.0 / n
    half = step / 2.0
    samples = ss * ss

    # 先按超采样分辨率求每个子像素的颜色，再盒式下采样
    rows = []
    for sy in range(n):
        py = sy * step + half
        row = []
        for sx in range(n):
            px = sx * step + half
            hit = None
            for inside, colour in reversed(shapes):
                if inside(px, py):
                    hit = colour
                    break
            row.append(hit)
        rows.append(row)

    out = bytearray()
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0
            for dy in range(ss):
                for dx in range(ss):
                    c = rows[y * ss + dy][x * ss + dx]
                    if c is not None:
                        r += c[0]
                        g += c[1]
                        b += c[2]
                        a += 255
            if a == 0:
                out += b"\x00\x00\x00\x00"
            else:
                covered = a // 255
                out += bytes((r // covered, g // covered, b // covered, a // samples))
    return bytes(out)


def write_png(path, size, pixels):
    def chunk(kind, data):
        head = struct.pack(">I", len(data)) + kind
        return head + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    raw = b"".join(
        b"\x00" + pixels[y * size * 4:(y + 1) * size * 4] for y in range(size))
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    Path(path).write_bytes(png)
    return len(png)


def write_ico(path, png_bytes, size):
    """把 PNG 直接塞进 ICO 容器。ICO 从 Vista 起支持 PNG 负载（边长 <= 256）。"""
    if size > 256:
        raise ValueError("ICO 的 PNG 负载边长不能超过 256")
    dim = 0 if size == 256 else size          # 256 在目录项里记作 0
    header = struct.pack("<HHH", 0, 1, 1)
    entry = struct.pack(
        "<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png_bytes), 6 + 16)
    Path(path).write_bytes(header + entry + png_bytes)


if __name__ == "__main__":
    out_dir = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"渲染 {SIZE}x{SIZE}（{SS}x 超采样）...", flush=True)
    big = render(SIZE, SS)
    n = write_png(out_dir / "conclave.png", SIZE, big)
    print(f"  conclave.png  {n:,} 字节")

    small = render(256, SS)
    tmp = out_dir / "conclave-256.png"
    write_png(tmp, 256, small)
    write_ico(out_dir / "conclave.ico", tmp.read_bytes(), 256)
    tmp.unlink()
    print(f"  conclave.ico  {(out_dir / 'conclave.ico').stat().st_size:,} 字节")
