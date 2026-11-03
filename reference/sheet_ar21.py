"""AR-21 As-Built Layout Plan sheet generator (reference spike for the C# sheet module).

Input: ShellModel JSON (from shell_spike.py) + project/title-block JSON.
Output: A3 portrait PDF + PNG in the Manawala AR-21 format, and a DXF of the plan on clean layers.
Paper units are millimetres; model units are inches. The scale is picked automatically: the largest
standard architectural scale at which the plan, its dimension tiers and the site labels fit.

Usage: python3 sheet_ar21.py shell.json project.json out_basename
"""
import json
import math
import re
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon, Rectangle, Circle, Wedge
from matplotlib.font_manager import FontProperties

PT = 72 / 25.4            # points per mm
RED = "#c62828"
INK = "black"

SCALES = [(48, '1/4" = 1\'-0"'), (64, '3/16" = 1\'-0"'), (96, '1/8" = 1\'-0"'), (128, '3/32" = 1\'-0"'), (192, '1/16" = 1\'-0"')]

F_SANS = FontProperties(family="DejaVu Sans", stretch="condensed")
F_TB = FontProperties(family="DejaVu Sans")
F_SERIF = FontProperties(family="FreeSerif")


def fmt_ftin(v):
    v = abs(round(v * 2) / 2)
    ft = int(v // 12)
    inch = v - ft * 12
    whole = int(inch)
    return f"{ft}'-{whole}" + (" 1/2" if inch - whole >= 0.5 else "") + '"'


# ---------------------------------------------------------------- paper primitives
class Paper:
    def __init__(self, w=297, h=420):
        self.w, self.h = w, h
        self.fig = plt.figure(figsize=(w / 25.4, h / 25.4))
        self.ax = self.fig.add_axes([0, 0, 1, 1])
        self.ax.set_xlim(0, w)
        self.ax.set_ylim(0, h)
        self.ax.set_aspect("equal")
        self.ax.axis("off")

    def line(self, p, q, lw=0.18, color=INK, dash=None, z=3):
        ln, = self.ax.plot([p[0], q[0]], [p[1], q[1]], color=color, lw=lw * PT, solid_capstyle="butt", zorder=z)
        if dash:
            ln.set_dashes([d * PT / (lw * PT) for d in dash])  # dash lengths in mm, scaled by line width
        return ln

    def polyline(self, pts, lw=0.18, color=INK, closed=False, z=3):
        pts = list(pts) + ([pts[0]] if closed else [])
        self.ax.plot([p[0] for p in pts], [p[1] for p in pts], color=color, lw=lw * PT, zorder=z, solid_joinstyle="miter")

    def poly(self, pts, fc="none", ec=INK, lw=0.25, z=4):
        self.ax.add_patch(Polygon(pts, closed=True, fc=fc, ec=ec, lw=lw * PT, zorder=z, joinstyle="miter"))

    def rect(self, x0, y0, x1, y1, **kw):
        self.poly([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], **kw)

    def hatch(self, pts, spacing=0.75, angle=45, lw=0.1, z=3):
        """ANSI31-style hatch: parallel lines clipped to the polygon (scanline in the rotated frame)."""
        a = math.radians(angle)
        ca, sa = math.cos(a), math.sin(a)
        rot = [(x * ca + y * sa, -x * sa + y * ca) for x, y in pts]   # rotate by -angle
        ys = [p[1] for p in rot]
        k0, k1 = math.floor(min(ys) / spacing), math.ceil(max(ys) / spacing)
        n = len(rot)
        for k in range(k0, k1 + 1):
            yk = k * spacing
            xs = []
            for i in range(n):
                (x1, y1), (x2, y2) = rot[i], rot[(i + 1) % n]
                if (y1 > yk) != (y2 > yk):
                    xs.append(x1 + (yk - y1) * (x2 - x1) / (y2 - y1))
            xs.sort()
            for j in range(0, len(xs) - 1, 2):
                p = (xs[j] * ca - yk * sa, xs[j] * sa + yk * ca)
                q = (xs[j + 1] * ca - yk * sa, xs[j + 1] * sa + yk * ca)
                self.line(p, q, lw=lw, z=z)

    def text(self, x, y, s, h=2.0, ha="left", va="baseline", rot=0, font=None, color=INK, z=6, bg=None, weight="normal", style="normal"):
        """h is the cap height in mm."""
        size = h / 0.72 * PT
        t = self.ax.text(x, y, s, fontsize=size, ha=ha, va=va, rotation=rot, fontproperties=font or F_SANS,
                         color=color, zorder=z, rotation_mode="anchor", fontweight=weight, fontstyle=style)
        if bg:
            t.set_bbox(dict(fc=bg, ec="none", pad=0.6))
        return t

    def text_width(self, s, h, font=None):
        t = self.text(0, 0, s, h=h, font=font)
        self.fig.canvas.draw() if not hasattr(self, "_r") else None
        self._r = self.fig.canvas.get_renderer()
        bb = t.get_window_extent(renderer=self._r)
        t.remove()
        return bb.width / self.fig.dpi * 25.4

    def save(self, base):
        self.fig.savefig(base + ".pdf")
        self.fig.savefig(base + ".png", dpi=150, facecolor="white")


# ---------------------------------------------------------------- drafting conventions
DIM_H = 1.5        # dimension text cap height
TAG_H = 1.35       # tag text
LABEL_H = 2.6      # site labels


def dim_chain(P, pts, axis, base, side, tier_gap=0.0, obj_gap=1.0, label=None):
    """Linear dimension chain with architectural ticks.
    pts: positions along the axis (paper mm). axis 'x' (horizontal string) or 'y' (vertical string).
    base: perpendicular coordinate of the measured objects; side: +1 or -1 (which side the string sits);
    the string sits at base + side*(offset). label(v_model) -> text from model length."""
    off = base + side * tier_gap
    for i, p in enumerate(pts):
        # extension line from the object to just past the dimension line
        a0 = base + side * obj_gap
        a1 = off + side * 1.0
        if axis == "x":
            P.line((p, a0), (p, a1), lw=0.1)
        else:
            P.line((a0, p), (a1, p), lw=0.1)
    if axis == "x":
        P.line((min(pts) - 1.0, off), (max(pts) + 1.0, off), lw=0.13)
    else:
        P.line((off, min(pts) - 1.0), (off, max(pts) + 1.0), lw=0.13)
    for p in pts:  # 45-degree ticks
        d = 0.75
        if axis == "x":
            P.line((p - d, off - d), (p + d, off + d), lw=0.3)
        else:
            P.line((off - d, p - d), (off + d, p + d), lw=0.3)
    stagger = 0
    for a, b in zip(pts[:-1], pts[1:]):
        s = label(abs(b - a))
        mid = (a + b) / 2
        need = P.text_width(s, DIM_H) + 0.8
        fits = abs(b - a) >= need
        lift = 0.7 if fits else 0.7 + (2.2 if stagger % 2 == 0 else 0.0)
        if not fits:
            stagger += 1
        if axis == "x":
            y = off + 0.7 if side > 0 else off + 0.7
            if not fits and side < 0:
                y = off - DIM_H - 1.3
            P.text(mid, y if fits else (off + lift if side > 0 else y), s, h=DIM_H, ha="center")
        else:
            x = off - 0.7
            if not fits:
                x = off - 0.7 - 2.2 if side < 0 else off + 0.7 + 2.2 + DIM_H
            P.text(x, mid, s, h=DIM_H, ha="center", rot=90)


def overlap(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def level_marker(P, x, y, text, h=1.8):
    """Circle-cross level symbol with two quadrants filled, leader and text (Manawala convention)."""
    r = 1.5
    P.ax.add_patch(Circle((x, y), r, fc="white", ec=INK, lw=0.2 * PT, zorder=6))
    P.ax.add_patch(Wedge((x, y), r, 0, 90, fc=INK, ec="none", zorder=7))
    P.ax.add_patch(Wedge((x, y), r, 180, 270, fc=INK, ec="none", zorder=7))
    P.line((x - r - 0.8, y), (x + r + 0.8, y), lw=0.13, z=7)
    P.line((x, y - r - 0.8), (x, y + r + 0.8), lw=0.13, z=7)
    P.line((x + r, y), (x + r + 14, y + 3.5), lw=0.13)
    P.text(x + r + 2.0, y + 1.6, text, h=h, rot=math.degrees(math.atan2(3.5, 14)))


# ---------------------------------------------------------------- the sheet
def build(shell, proj, base):
    env = shell["envelope"]
    X0, X1, YR, YF, YFA = env["x0"], env["x1"], env["y_rear"], env["y_front"], env["y_front_all"]
    YP = env["y_plot_rear"] if env["y_plot_rear"] is not None else YR
    tb = proj["title_block"]

    P = Paper(297, 420)
    # frames (from the Manawala sheet)
    P.rect(7, 10, 290, 410, lw=0.25)
    INNER = (14, 18.5, 282.5, 399)
    P.rect(*INNER, lw=0.7)
    TB_TOP = 48.5
    DRAW = (INNER[0], TB_TOP, INNER[2], INNER[3])

    # ---- automatic scale: plan + margins must fit the drawing area
    plan_w_in, plan_h_in = X1 - X0, YP - YFA
    side_margin = 2 * (14 + 26)          # dimension tiers + neighbour label each side (mm)
    top_margin, bottom_margin = 16, 16 + 48   # rear label; front dims + parking/road band + area note
    chosen = None
    for den, name in SCALES:
        k = 25.4 / den
        if plan_w_in * k + side_margin <= DRAW[2] - DRAW[0] and plan_h_in * k + top_margin + bottom_margin <= DRAW[3] - DRAW[1]:
            chosen = (den, name)
            break
    if chosen is None:
        raise ValueError("Plan does not fit A3 at any standard scale: move to A2 or split the plan")
    den, scale_name = chosen
    k = 25.4 / den
    cx = (DRAW[0] + DRAW[2]) / 2
    oy_top = DRAW[3] - top_margin
    ox = cx - plan_w_in * k / 2

    def T(x, y):
        return (ox + (x - X0) * k, oy_top - (YP - y) * k)

    def TP(pts):
        return [T(x, y) for x, y in pts]

    # ---- boundary walls of the covered envelope (cut, hatched)
    for w in shell["walls"]:
        pts = TP(w["outline"])
        P.poly(pts, fc="white", lw=0.35, z=4)
        P.hatch(pts, spacing=0.7, z=5)
    for w in shell["yard_walls"]:
        b = w["bbox"]
        pts = TP([(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])])
        P.poly(pts, fc="white", lw=0.25, z=4)
        P.hatch(pts, spacing=0.7, z=5)

    # ---- RCC: columns and the lift core
    RCC = "#3d3d3d"
    for c in shell["columns"]:
        b = c["bbox"]
        P.poly(TP([(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])]), fc=RCC, lw=0.35, z=6)
    for core in shell["cores"]:
        P.poly(TP(core["outline"]), fc=RCC, lw=0.35, z=6)
        if core.get("shaft"):
            s = core["shaft"]
            P.line(T(s[0], s[1]), T(s[2], s[3]), lw=0.13)
            P.line(T(s[0], s[3]), T(s[2], s[1]), lw=0.13)
            c0 = T((s[0] + s[2]) / 2, (s[1] + s[3]) / 2)
            P.text(c0[0], c0[1] - 2.6, core["label"], h=1.8, ha="center", bg="white")

    # ---- obstacles for annotation placement (paper boxes)
    def pbox(b):
        (ax0, ay0), (ax1, ay1) = T(b[0], b[1]), T(b[2], b[3])
        return (min(ax0, ax1), min(ay0, ay1), max(ax0, ax1), max(ay0, ay1))
    obstacles = [pbox(st["bbox"]) for st in shell["stairs"] if st["kind"] != "ramp"]
    obstacles += [pbox(c["bbox"]) for c in shell["cores"]] + [pbox(c["bbox"]) for c in shell["columns"]]

    # ---- drop beams: dashed edges + tag
    beam_label = proj.get("beam_label", "DROP BEAM {depth}")
    for bm in shell["beams"]:
        p0, p1 = bm["p0"], bm["p1"]
        L = math.dist(p0, p1)
        d = ((p1[0] - p0[0]) / L, (p1[1] - p0[1]) / L)
        n = (-d[1], d[0])
        hw = bm["width"] / 2
        for sgn in (1, -1):
            a = (p0[0] + n[0] * hw * sgn, p0[1] + n[1] * hw * sgn)
            b = (p1[0] + n[0] * hw * sgn, p1[1] + n[1] * hw * sgn)
            P.line(T(*a), T(*b), lw=0.18, dash=(1.6, 0.8), z=3)
        depth = bm.get("depth")
        txt = beam_label.format(depth=f'{depth:.0f}"' if depth and depth < 12 else fmt_ftin(depth or 0))
        vertical = abs(d[0]) < 0.5
        tw = P.text_width(txt, TAG_H) + 1.0
        th = TAG_H + 1.0
        chosen = None
        for f in (0.5, 0.35, 0.65, 0.25, 0.75, 0.15, 0.85):
            m = T(p0[0] + (p1[0] - p0[0]) * f, p0[1] + (p1[1] - p0[1]) * f)
            box = (m[0] - th / 2, m[1] - tw / 2, m[0] + th / 2, m[1] + tw / 2) if vertical else (m[0] - tw / 2, m[1] - th / 2, m[0] + tw / 2, m[1] + th / 2)
            if not any(overlap(box, o) for o in obstacles):
                chosen = m
                obstacles.append(box)
                break
        if chosen is None:
            chosen = T((p0[0] + p1[0]) / 2, (p0[1] + p1[1]) / 2)
        P.text(chosen[0], chosen[1], txt, h=TAG_H, ha="center", va="center", rot=90 if vertical else 0, bg="white", z=5)

    # ---- stairs
    for st in shell["stairs"]:
        if st["kind"] == "ramp":
            continue  # MS portable ramp is a proposed UBL item, not part of the as-built shell
        if st["kind"] == "spiral":
            c = T(*st["center"])
            r = st["radius"] * k
            P.ax.add_patch(Circle(c, r, fc="white", ec=INK, lw=0.25 * PT, zorder=4))
            P.ax.add_patch(Circle(c, 0.35, fc=INK, ec=INK, lw=0.1 * PT, zorder=5))
            for i in range(st["treads"]):
                a = 2 * math.pi * i / st["treads"]
                P.line((c[0] + 0.35 * math.cos(a), c[1] + 0.35 * math.sin(a)), (c[0] + r * math.cos(a), c[1] + r * math.sin(a)), lw=0.1, z=5)
            P.text(c[0] + r + 1.2, c[1] - 0.6, "MS ROUND STAIR\n(TO BASEMENT)", h=1.3)
            continue
        b = st["bbox"]
        x0, y0 = T(b[0], b[1])
        x1, y1 = T(b[2], b[3])
        P.rect(x0, y0, x1, y1, lw=0.25, z=4, fc="white")
        n = st["treads"]
        for i in range(1, n):
            if st["treads_run"] == "y":
                yy = y0 + (y1 - y0) * i / n
                P.line((x0, yy), (x1, yy), lw=0.13, z=5)
            else:
                xx = x0 + (x1 - x0) * i / n
                P.line((xx, y0), (xx, y1), lw=0.13, z=5)
        if st.get("label") and st.get("label_xy"):
            lx, ly = T(*st["label_xy"])
            # walking line starts at the end nearest the label
            if st["treads_run"] == "y":
                xm = (x0 + x1) / 2
                start, end = ((xm, y0 + 0.8), (xm, y1 - 0.8)) if abs(ly - y0) < abs(ly - y1) else ((xm, y1 - 0.8), (xm, y0 + 0.8))
            else:
                ym = (y0 + y1) / 2
                start, end = ((x0 + 0.8, ym), (x1 - 0.8, ym)) if abs(lx - x0) < abs(lx - x1) else ((x1 - 0.8, ym), (x0 + 0.8, ym))
            P.ax.annotate("", xy=end, xytext=start, arrowprops=dict(arrowstyle="-|>", lw=0.18 * PT, color=INK, mutation_scale=4), zorder=6)
            P.ax.add_patch(Circle(start, 0.45, fc="none", ec=INK, lw=0.15 * PT, zorder=6))
            word = st["label"].upper()
            P.text(lx, ly, word, h=1.4, ha="center", va="top")

    # ---- levels (outside levels move into the parking band so they never sit on the dimension strings)
    front_y = T(X0, YFA)[1]
    band_top = front_y - 17          # below the two front dimension tiers
    band_bot = band_top - 26
    for lv in shell["levels"]:
        x, y = T(lv["x"], lv["y"])
        if lv["where"] == "outside":
            y = band_top - 9
            x = min(max(x, DRAW[0] + 40), DRAW[2] - 60)
        level_marker(P, x - 8, y, f"LEVEL = {lv['text']}")

    # ---- site: parking band and road (symbolic, the label carries the width as on Manawala)
    xl, xr = T(X0, 0)[0], T(X1, 0)[0]
    P.line((DRAW[0] + 4, band_bot), (DRAW[2] - 4, band_bot), lw=0.25)
    P.line((DRAW[0] + 4, band_bot - 14), (DRAW[2] - 4, band_bot - 14), lw=0.25)
    parking = next((t["text"] for t in shell["texts"] if "PARKING" in t["text"].upper()), None)
    if parking:
        parking = re.sub(r"(\d+)'(?!-)", lambda m: m.group(1) + "'-0\"", parking).upper()
        P.text(cx, band_top - 19, parking, h=LABEL_H, ha="center")
    road = proj.get("road_name")
    P.text(cx, band_bot - 9, road or "[ROAD NAME: INPUT REQUIRED]", h=LABEL_H + 0.4, ha="center", color=INK if road else RED)

    # ---- neighbours
    nb = proj.get("neighbours", {})
    mid_y = (T(0, YR)[1] + T(0, YF)[1]) / 2
    for side, x, ha in (("left", xl - 30, "center"), ("right", xr + 30, "center")):
        v = nb.get(side)
        P.text(x, mid_y, v or "[NEIGHBOUR:\nINPUT]", h=LABEL_H, ha=ha, color=INK if v else RED)
    v = nb.get("rear")
    P.text(cx, T(0, YP)[1] + 9, v or "[REAR NEIGHBOUR: INPUT]", h=LABEL_H, ha="center", color=INK if v else RED)

    # ---- dimensions
    lab = lambda mm: fmt_ftin(mm / k)
    left_cols = sorted([c for c in shell["columns"] if abs(c["bbox"][0] - X0) < 1], key=lambda c: -c["bbox"][3])
    ys = [T(0, YP)[1]]
    for c in left_cols:
        ys += [T(0, c["bbox"][3])[1], T(0, c["bbox"][1])[1]]
    dim_chain(P, ys, "y", xl, -1, tier_gap=7, label=lab)
    right_pts = [T(0, v)[1] for v in (YP, YR, YF, YFA)]
    dim_chain(P, right_pts, "y", xr, +1, tier_gap=7, label=lab)
    dim_chain(P, [right_pts[0], right_pts[-1]], "y", xr, +1, tier_gap=14, label=lab)
    front_cols = sorted([c for c in shell["columns"] if abs(c["bbox"][1] - YFA) < 1], key=lambda c: c["bbox"][0])
    xs = [xl] + [T(c["bbox"][2], 0)[0] if i == 0 else T(c["bbox"][0], 0)[0] for i, c in enumerate(front_cols)] + [xr]
    xs = sorted(set(round(v, 3) for v in xs))
    dim_chain(P, xs, "x", front_y, -1, tier_gap=7, label=lab)
    dim_chain(P, [xl, xr], "x", front_y, -1, tier_gap=13, label=lab)
    dim_chain(P, [xl, xr], "x", T(0, YP)[1], +1, tier_gap=5, label=lab)

    # ---- column tags: one per column size, leader into the building
    tagged = set()
    for c in sorted(shell["columns"], key=lambda c: -c["bbox"][3]):
        size = f'{c["w"]:.0f}"X{c["d"]:.0f}"'
        if size in tagged:
            continue
        b = c["bbox"]
        on_left = abs(b[0] - X0) < 1
        if not on_left and abs(b[2] - X1) >= 1:
            continue
        tagged.add(size)
        y_mid = (b[1] + b[3]) / 2
        p = T(b[2] if on_left else b[0], y_mid)
        sgn = 1 if on_left else -1
        vy = 1 if y_mid < (YR + YF) / 2 else -1
        q = (p[0] + sgn * 5, p[1] + vy * 4)
        P.line(p, q, lw=0.13)
        P.line(q, (q[0] + sgn * 1.5, q[1]), lw=0.13)
        P.text(q[0] + sgn * 2.0, q[1] - 0.6, f"{size} RCC\nCOLUMN", h=TAG_H, ha="left" if on_left else "right", va="center")

    # ---- area statement and slab note (bottom left, like Manawala)
    ax_ = DRAW[0] + 6
    ay = band_top - 2
    area = env["covered_area_sft"]
    P.text(ax_, ay, "GROUND FLOOR", h=2.0)
    t = P.text(ax_, ay - 3.6, f"COVERED AREA = {area:,.0f} SFT.", h=2.0)
    wpx = P.text_width(f"COVERED AREA = {area:,.0f} SFT.", 2.0)
    P.line((ax_, ay - 4.3), (ax_ + wpx, ay - 4.3), lw=0.18)
    if env.get("stated_area_sft") and abs(area - env["stated_area_sft"]) / env["stated_area_sft"] > 0.005:
        P.text(ax_, ay - 7.0, f"CHECK: source drawing states {env['stated_area_sft']:,.0f} SFT", h=1.3, color=RED)
    notes = []
    if shell.get("slab_height"):
        notes.append(f"SLAB HEIGHT {fmt_ftin(shell['slab_height'])} (FROM F.F.L.)")
    if shell.get("beam_depth"):
        notes.append(f'DROP BEAMS {shell["beam_depth"]:.0f}" DEEP, CLEAR UNDER BEAM {fmt_ftin(shell["slab_height"] - shell["beam_depth"])}' if shell.get("slab_height") else "")
    for i, s in enumerate(notes):
        P.text(DRAW[2] - 6, ay - 3.6 * i, s, h=1.5, ha="right")

    # ---- north arrow (an input: never guessed)
    north = proj.get("north_deg")
    if north is None:
        north = shell.get("north_degrees")
    nx, ny = DRAW[2] - 22, band_bot - 27
    if north is not None:
        a = math.radians(north)
        dx, dy = -math.sin(a), math.cos(a)
        P.ax.add_patch(Circle((nx, ny), 5, fc="white", ec=INK, lw=0.25 * PT, zorder=6))
        tip, tail = (nx + dx * 4.5, ny + dy * 4.5), (nx - dx * 4.5, ny - dy * 4.5)
        P.ax.add_patch(Polygon([tip, (nx - dy * 1.6, ny + dx * 1.6), (nx, ny), (nx + dy * 1.6, ny - dx * 1.6)], closed=True, fc=INK, ec=INK, lw=0.2 * PT, zorder=7))
        P.line(tail, (nx, ny), lw=0.25, z=7)
        P.text(nx + dx * 7.5, ny + dy * 7.5 - 1.1, "N", h=2.4, ha="center")
    else:
        P.ax.add_patch(Circle((nx, ny), 5, fc="none", ec=RED, lw=0.25 * PT, ls=(0, (2, 1)), zorder=6))
        P.text(nx, ny - 0.9, "N ?", h=2.2, ha="center", color=RED)
        P.text(nx, ny - 8.5, "NORTH: INPUT REQUIRED", h=1.3, ha="center", color=RED)

    # ---- graphic scale bar (feet)
    sb_x, sb_y = DRAW[0] + 6, DRAW[1] + 6
    step_ft = 5
    for i in range(4):
        x_a = sb_x + i * step_ft * 12 * k
        x_b = sb_x + (i + 1) * step_ft * 12 * k
        P.rect(x_a, sb_y, x_b, sb_y + 1.2, fc=INK if i % 2 == 0 else "white", lw=0.13, z=6)
        P.text(x_a, sb_y + 2.0, f"{i * step_ft}'", h=1.2, ha="center")
    P.text(sb_x + 4 * step_ft * 12 * k, sb_y + 2.0, f"{4 * step_ft}'", h=1.2, ha="center")
    P.text(sb_x + 4 * step_ft * 12 * k + 4, sb_y + 0.1, f"SCALE {scale_name}", h=1.3)

    # ---- title block (template geometry measured from the Manawala sheet)
    L, R, B, Tt = INNER[0], INNER[2], INNER[1], TB_TOP
    P.line((L, Tt), (R, Tt), lw=0.7)
    c1, c2, c3 = 156.0, 181.5, 227.0
    for x in (c1, c2, c3):
        P.line((x, B), (x, Tt), lw=0.35)
    rh = (Tt - B) / 3
    P.line((L, B + rh), (c1, B + rh), lw=0.25)
    P.line((L, B + 2 * rh), (c1, B + 2 * rh), lw=0.25)
    P.text(L + 2, Tt - 3.3, "Title:", h=2.0, font=F_TB, color="#444")
    P.text(L + 16, Tt - 6.0, tb["title"], h=2.6, font=F_TB)
    P.text(L + 2, B + 2 * rh - 3.3, "Client:", h=1.8, font=F_TB, color="#444")
    if tb.get("client_logo"):
        pass  # production: the client's own logo file is placed here
    P.rect(L + 17, B + rh + 1.8, L + 35, B + 2 * rh - 1.8, lw=0.13, ec="#888")
    P.text(L + 26, B + 1.5 * rh - 0.5, "CLIENT LOGO", h=1.0, ha="center", color="#888", font=F_TB)
    P.text(L + 39, B + 1.5 * rh - 1.2, tb["client"], h=2.6, font=F_TB)
    P.text(L + 2, B + rh - 3.3, "Project:", h=1.8, font=F_TB, color="#444")
    proj_name = tb.get("project")
    P.text(L + 17, B + 2.6, proj_name or "[BRANCH NAME AND ADDRESS: INPUT REQUIRED]", h=2.5, font=F_TB, color=INK if proj_name else RED)
    P.line((c1, B + rh), (c2, B + rh), lw=0.25)
    P.text(c1 + 1.5, Tt - 3.6, "DRG NO.", h=1.5, font=F_TB)
    P.text((c1 + c2) / 2, B + rh + 5.0, tb["drawing_no"], h=3.6, ha="center", font=F_TB)
    P.text(c1 + 1.5, B + rh - 3.6, "PRJ. NO.", h=1.5, font=F_TB)
    if tb.get("project_no"):
        P.text((c1 + c2) / 2, B + 2.5, tb["project_no"], h=2.0, ha="center", font=F_TB)
    # people / date table
    r4 = (Tt - (B + rh * 1.16)) / 4
    ytab = [Tt - i * r4 for i in range(5)]
    for y in ytab[1:]:
        P.line((c2, y), (c3 if y == ytab[4] else 212.5 if y in (ytab[1], ytab[3]) else c3, y), lw=0.2)
    P.line((c2 + 13, ytab[4]), (c2 + 13, Tt), lw=0.2)
    P.line((212.5, ytab[4]), (212.5, Tt), lw=0.2)
    people = tb.get("people", {})
    for i, role in enumerate(("DRAWN", "DESIGN", "CHECKED", "APPROVED")):
        yb = ytab[i + 1] + 1.3
        P.text(c2 + 0.8, yb, role, h=1.15, font=F_TB)
        if people.get(role.lower()):
            P.text(c2 + 13.8, yb, people[role.lower()], h=1.15, font=F_TB)
    P.text(213.3, ytab[2] + 2.6, "DATE", h=1.2, font=F_TB)
    P.text((212.5 + c3) / 2, ytab[4] + 4.2, tb["date"], h=1.2, ha="center", font=F_TB)
    P.text((c2 + c3) / 2, B + (ytab[4] - B) / 2 - 1.2, tb["status"], h=2.2, ha="center", font=F_TB, color=RED if "PREVIEW" in tb["status"] else INK)
    # firm block
    P.line((c3, Tt - 5.4), (R, Tt - 5.4), lw=0.2)
    P.text(c3 + 1.5, Tt - 4.0, "SCALE:", h=1.5, font=F_TB)
    P.text(R - 1.5, Tt - 4.0, f"{scale_name} (A3)", h=1.5, ha="right", font=F_TB)
    f = tb["firm"]
    P.text(c3 + 1.2, Tt - 10.8, f["name"], h=3.0, font=F_SERIF)
    P.text(c3 + 1.2, Tt - 15.2, f["tagline"], h=2.0, font=F_SERIF, weight="bold")
    for i, ln in enumerate(f["address"]):
        P.text(c3 + 1.2, Tt - 19.0 - i * 3.4, ln, h=1.75, font=F_TB)
    P.text(INNER[0], INNER[1] - 2.6, tb.get("copyright", ""), h=0.9, font=F_TB)

    P.save(base)
    return {"scale": scale_name, "scale_den": den}


if __name__ == "__main__":
    shell = json.load(open(sys.argv[1]))
    proj = json.load(open(sys.argv[2]))
    print(build(shell, proj, sys.argv[3]))
