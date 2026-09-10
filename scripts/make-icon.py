#!/usr/bin/env python3
"""生成 Conclave 的应用图标。

图案是**一只站在钥匙上的猫头鹰**，GitHub Octocat 那种做法：近黑的底板上一个白色剪影，
细节（眼睛、喙、翅膀刻线、钥匙环）全靠挖空。几何来自 <c>docs/logo/conclave-owl.svg</c>
（256 单位，含 translate(0,-8)），这里的坐标就是那份路径减 8 再除以 256 ——
**改图案请先改 SVG，再把数字搬过来**，别让两边各自漂移。

为什么是猫头鹰：一群猫头鹰在英文里就叫 parliament（议会），正是这套东西做的事；
大眼睛是审阅本身；夜行是它平时的样子。脚下那把钥匙是 conclave 的词源 cum clave。

六条来自实测的约束 —— 每一条都是某一版栽在上面之后才写下来的：

1. **App 图标不是 UI 图标。** 单线小图标放到圆角方块上永远像工具栏按钮：好看的 App
   图标是实心体块 + 层次/渐变。三横杠、互扣双环、三节点三角三版全是这个毛病。
2. **「设计感」来自材质，不是堆元素。** 试过把签名线断成三段（读成省略号）、字母上下
   双色（分界切在碗中间）、线尾加收笔点（像掉了个渣），都更吵。
3. **大面积金色会吵。** 整块金底、金色气泡占满画面，都比「墨蓝底 + 金色只做重点」难看。
   金色是强调色，不是主色。
4. **不要画「像什么」的轮廓，先定要表达什么。** 正形钥匙孔在小尺寸下读成用户头像 /
   奖杯 / 灯泡；两个互扣的斜环读成「CO」，一个没有含义的字母组合。
5. **每个候选都要在 16px 与深浅两底上看过才算数。** 链环穿块读成挂锁、区块加摘要读成
   证件卡、白烟读成火焰 —— 这些都是只有渲出来才发现的。
6. **描边比实心吃亏。** 线宽在 16px 上只剩一两个像素，再细就被抗锯齿抹成灰。

纯标准库（zlib + struct），不引任何图像库 —— 图标要能在 CI 上无依赖重现。
用 2x 超采样做抗锯齿；颜色可以是 (px, py) -> rgb/rgba 的函数，渐变与材质就是这么来的；
颜色为 None 表示把已画上的抹回透明（挖洞用，模板图不能拿底色去盖）。
贝塞尔轮廓先拍平成多边形，再按扫描行缓存交点做 even-odd 填充 —— render 是逐行扫的，
同一个 py 会被问 n 次，缓存之后每个子像素只剩一次二分查找。
"""
import bisect
import math
import struct
import sys
import zlib
from pathlib import Path

SIZE = 1024
SS = 2                      # 超采样倍数
# 36 = 18pt @2x，跟 MacStatusItem.IconPoints 对齐。给 44px 的话 NSImage 还要再缩一道，
# 白白多一次重采样把本来就细的线抹糊。
TRAY_SIZE = 36
TRAY_SS = 8                 # 小图必须高倍超采样，否则边缘全是锯齿
GROUND = (0x1F, 0x23, 0x28)     # 近黑，SVG 里是平涂的 #24292F；这里上下各偏一点做成极弱的渐变
GROUND_HI = (0x2C, 0x31, 0x39)  # 底板顶部
GLYPH = (0xFF, 0xFF, 0xFF)      # 剪影：纯白，跟 Octocat 一样不带暖色
TEMPLATE = (0x00, 0x00, 0x00)   # 菜单栏模板图：纯黑 + alpha，由 macOS 自己染色

def capsule(x0, y0, x1, y1, w):
    """带圆头的粗线段：点到线段的距离 <= 线宽/2。三行代码和那一折都是它。"""
    dx, dy = x1 - x0, y1 - y0
    length_sq = dx * dx + dy * dy
    half = w / 2.0

    def inside(px, py):
        t = 0.0 if length_sq == 0 else max(
            0.0, min(1.0, ((px - x0) * dx + (py - y0) * dy) / length_sq))
        return math.hypot(px - x0 - t * dx, py - y0 - t * dy) <= half

    return inside


def circle(cx, cy, r):
    def inside(px, py):
        return (px - cx) ** 2 + (py - cy) ** 2 <= r * r

    return inside


def cubic(p0, p1, p2, p3, n=24):
    """把一段三次贝塞尔拍平成 n 段折线（不含起点）。24 段在 1024px 上看不出棱。"""
    pts = []
    for i in range(1, n + 1):
        t = i / n
        u = 1 - t
        pts.append((
            u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0],
            u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1],
        ))
    return pts


def polygon(points):
    """点在多边形内（even-odd）。

    交点按扫描行缓存：render 逐行扫描，同一个 py 会被问 n 次，第一次算出这一行
    所有边的交点并排序，之后每个子像素只剩一次二分查找。判定用半开区间
    (y0 <= py) != (y1 <= py)，顶点正好落在扫描线上时不会被数两次。
    """
    edges = [(points[i], points[(i + 1) % len(points)]) for i in range(len(points))]
    cache = {}

    def inside(px, py):
        xs = cache.get(py)
        if xs is None:
            xs = []
            for (x0, y0), (x1, y1) in edges:
                if (y0 <= py) != (y1 <= py):
                    xs.append(x0 + (py - y0) * (x1 - x0) / (y1 - y0))
            xs.sort()
            cache[py] = xs
        return bisect.bisect_right(xs, px) % 2 == 1

    return inside


def stroke(points, w):
    """一条折线描边：相邻点之间各放一个 capsule，返回形状列表。翅膀刻线用它。"""
    return [capsule(*a, *b, w) for a, b in zip(points, points[1:])]


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


def linear_gradient(c0, c1, angle):
    """线性渐变，返回 (px, py) -> rgb。angle: 90 = 自上而下。"""
    ux, uy = math.cos(math.radians(angle)), math.sin(math.radians(angle))

    def colour(px, py):
        t = min(1.0, max(0.0, (px - 0.5) * ux + (py - 0.5) * uy + 0.5))
        return tuple(int(round(c0[i] + (c1[i] - c0[i]) * t)) for i in range(3))

    return colour


def sheen(cx, cy, r, peak):
    """柔和高光：alpha 随距离二次衰减。硬边高光看起来像贴了张纸。"""
    def paint(px, py):
        t = max(0.0, 1.0 - math.hypot(px - cx, py - cy) / r)
        return (0xFF, 0xFF, 0xFF, int(255 * peak * t * t))

    return paint


def gloss(angle, offset, width, peak):
    """斜向光带：一条软边亮带，模拟弧面反光。"""
    sin_a, cos_a = math.sin(math.radians(angle)), math.cos(math.radians(angle))

    def paint(px, py):
        d = abs((px - 0.5) * sin_a + (py - 0.5) * cos_a - offset)
        t = max(0.0, 1.0 - d / width)
        return (0xFF, 0xFF, 0xFF, int(255 * peak * t * t))

    return paint


def vignette(inner, outer, peak):
    """暗角：把四角压下去，中间的字母才浮起来。"""
    def paint(px, py):
        t = min(1.0, max(0.0, (math.hypot(px - 0.5, py - 0.5) - inner) / (outer - inner)))
        return (0, 0, 0, int(255 * peak * t * t))

    return paint


def rim_light(thickness, peak, radius):
    """
    沿顶边的一圈轮廓高光，越往下越淡。

    缺了这一道，圆角方块看起来就是一块贴纸而不是一个有厚度的物件 ——
    加上之后立刻像 App 图标。返回 (形状, 画笔)。
    """
    outer = rounded_rect(0.0, 0.0, 1.0, 1.0, radius)
    inner = rounded_rect(thickness, thickness, 1 - 2 * thickness, 1 - 2 * thickness,
                         radius - thickness)

    def paint(px, py):
        t = max(0.0, 1.0 - py / 0.55)
        return (0xFF, 0xFF, 0xFF, int(255 * peak * t * t))

    return (lambda px, py: outer(px, py) and not inner(px, py)), paint


_PLATE = object()   # hole 的默认值哨兵：None 是「挖透」，不能拿它当「没传」


def build_shapes(*, plate=True, scale=1.0, glyph=None, hole=_PLATE, tray=False, eyes="open"):
    """
    近黑底板 + 白色猫头鹰 + 脚下一把钥匙。

    参数化是为了让菜单栏那张图跟 App 图标共用同一份几何 —— 两处各画一遍，
    迟早漂移成「同一个标志两个样子」。默认值就是 App 图标。

    plate=False 时不画底板也不叠材质（菜单栏是模板图，底下透出来的是菜单栏本身）。
    glyph / hole 是剪影色与挖空色；App 图标的洞里露出底板的渐变，模板图的洞是真透明（None）。
    tray=True 走 docs/logo/conclave-owl-tray-*.svg 那套简化：眼洞放大、去掉瞳孔、翅膀刻线
    和钥匙的环与齿 —— 18pt 上它们只会变成一圈灰毛边，脚下只留一根横杆。
    eyes="closed" 是空闲那张：眼睛不是两个洞，而是两道向下弯的弧缝（闭着的眼睑）。
    菜单栏里状态一变，最先动的就是这一对眼；缝宽 12，缩到 18px 约 2px，再细就抹成灰了。
    """
    # 坐标 = SVG 路径的 (x, y - 8) / 256，再绕画布中心按 scale 缩放
    def at(x, y):
        return 0.5 + (x / 256.0 - 0.5) * scale, 0.5 + ((y - 8) / 256.0 - 0.5) * scale

    def d(v):
        return v / 256.0 * scale

    def rrect(x, y, w, h, r):
        x0, y0 = at(x, y)
        return rounded_rect(x0, y0, d(w), d(h), d(r))

    def circ(cx, cy, r):
        return circle(*at(cx, cy), d(r))

    def path(*cmds):
        """M 起点，随后每项是 3 个点（两个控制点 + 终点，三次贝塞尔）或 1 个点（直线），闭合成多边形。"""
        pts = [at(*cmds[0])]
        for seg in cmds[1:]:
            if len(seg) == 3:
                pts += cubic(pts[-1], at(*seg[0]), at(*seg[1]), at(*seg[2]))
            else:
                pts.append(at(*seg[0]))
        return pts

    radius = 0.225
    square = rounded_rect(0.0, 0.0, 1.0, 1.0, radius)
    plate_paint = linear_gradient(GROUND_HI, GROUND, 90)
    glyph = glyph if glyph is not None else GLYPH
    hole = plate_paint if hole is _PLATE else hole

    shapes = []
    if plate:
        # 圆角半径 0.225：对齐 macOS Big Sur 起的 squircle 比例，装进 Dock 不会显得方
        shapes.append((square, plate_paint))

    # 身体 + 头 + 两撮耳羽：docs/logo/conclave-owl.svg 那条 M62 52 … Z
    body = path(
        (62, 52),
        ((56, 72), (52, 96), (52, 120)),
        ((52, 160), (66, 196), (100, 206)),
        ((156, 206),),
        ((190, 196), (204, 160), (204, 120)),
        ((204, 96), (200, 72), (194, 52)),
        ((186, 66), (174, 78), (158, 76)),
        ((146, 70), (110, 70), (98, 76)),
        ((82, 78), (70, 66), (62, 52)),
    )
    shapes.append((polygon(body), glyph))

    if eyes == "closed":
        for lid in (((78, 116), (86, 130), (114, 130), (122, 116)),
                    ((134, 116), (142, 130), (170, 130), (178, 116))):
            pts = [at(*lid[0])] + cubic(at(*lid[0]), at(*lid[1]), at(*lid[2]), at(*lid[3]), 12)
            shapes += [(seg, hole) for seg in stroke(pts, d(12))]
    else:
        eye_r = 25 if tray else 24
        shapes += [(circ(100, 118, eye_r), hole), (circ(156, 118, eye_r), hole)]

    if not tray:
        shapes += [(circ(102, 120, 9), glyph), (circ(154, 120, 9), glyph)]
        shapes.append((polygon(path((119, 138), ((137, 138),), ((128, 154),))), hole))
        # 收拢的翅膀：两道刻线
        for wing in (
            ((70, 150), (64, 172), (70, 190), (84, 200)),
            ((186, 150), (192, 172), (186, 190), (172, 200)),
        ):
            pts = [at(*wing[0])] + cubic(at(*wing[0]), at(*wing[1]), at(*wing[2]), at(*wing[3]), 12)
            shapes += [(seg, hole) for seg in stroke(pts, d(5))]

    # 脚：跨过身体与钥匙之间那道缝
    if tray:
        shapes += [(rrect(94, 202, 22, 20, 6), glyph), (rrect(140, 202, 22, 20, 6), glyph)]
        shapes.append((rrect(50, 216, 156, 18, 9), glyph))          # 只留一根横杆
    else:
        shapes += [(rrect(98, 200, 18, 18, 6), glyph), (rrect(140, 200, 18, 18, 6), glyph)]
        # 钥匙：左环、横杆、右端两个齿
        shapes += [
            (circ(68, 220, 15), glyph), (circ(68, 220, 6), hole),
            (rrect(80, 214, 108, 12, 6), glyph),
            (rrect(164, 224, 8, 14, 2), glyph), (rrect(176, 224, 8, 10, 2), glyph),
        ]

    if plate:
        # 材质叠在最上面：整个物件一起受光
        shapes += [
            (square, sheen(0.30, 0.16, 0.72, 0.17)),
            (square, gloss(-28, -0.10, 0.14, 0.05)),
            (square, vignette(0.34, 0.80, 0.32)),
            rim_light(0.012, 0.42, radius),
        ]

    return shapes


def render(size, ss, shapes=None):
    shapes = shapes or build_shapes()
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
            # 从后往前叠加：带 alpha 的材质层要跟下面混合，不能「最后一个赢」
            for inside, colour in shapes:
                if not inside(px, py):
                    continue

                # 颜色可以是函数：渐变与材质就是靠这一步，逐子像素求值
                if colour is None:
                    # 打洞：把已经画上的颜色抹掉，恢复透明（见模块头第 7 条）
                    hit = None
                    continue

                c = colour(px, py) if callable(colour) else colour
                if len(c) == 3:
                    hit = c
                elif hit is not None and c[3]:
                    a = c[3] / 255.0
                    hit = tuple(int(round(c[i] * a + hit[i] * (1 - a))) for i in range(3))
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

    # 菜单栏（NSStatusItem）用的模板图。三处跟 App 图标不同：
    #   1. 没有底板 —— 菜单栏图标一律只有字形，底透出菜单栏本身
    #   2. 纯黑 + alpha —— 模板图由 macOS 按浅色/深色菜单栏自己染色，自带颜色反而会错；
    #      眼洞传 None 是真的挖透（render 里 None = 抹回透明），不能拿底色去盖
    #   3. 简化 + 放大 —— tray=True 去掉瞳孔、翅膀刻线、钥匙的环与齿，眼洞放大一档；
    #      没有底板就没有 squircle 的视觉收边，scale 1.2 让它占满又不顶到栏的上下沿
    #
    # 两张：空闲闭眼、评审中睁眼。App 按 NodeState.Reviewing 在菜单栏里换图。
    for name, eyes in (("conclave-tray-idle.png", "closed"), ("conclave-tray-busy.png", "open")):
        tray = render(TRAY_SIZE, TRAY_SS, build_shapes(
            plate=False, scale=1.2, glyph=TEMPLATE, hole=None, tray=True, eyes=eyes))
        n = write_png(out_dir / name, TRAY_SIZE, tray)
        print(f"  {name}  {n:,} 字节（{TRAY_SIZE}px 模板图，{eyes}）")
