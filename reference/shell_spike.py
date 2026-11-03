"""Shell recogniser (reference spike for the C# core).

Reads a branch drawing (DWG converted to DXF) and extracts the existing building shell:
envelope, boundary walls, RCC columns, RCC cores, drop beams, stairs, levels, slab height.
Recognition is by geometry, hatch loops and layer aliases, never by block names.
Units: inches. Output: ShellModel JSON (the contract the C# port mirrors).

Usage: python3 shell_spike.py input.dxf shell.json
"""
import json
import math
import re
import sys
from collections import defaultdict

from ezdxf import recover
from ezdxf.entities.boundary_paths import PolylinePath

TOL = 0.25

# Layer alias table. Production: loaded from config per client, extended as new drawings arrive.
LAYERS = {
    "column": {"COL", "COLUMN", "COLUMNS", "S-COLS", "S-COLUMN"},
    "wall_hatch": {"WALL", "A-WALL", "A-WALL-PATT", "A-WALL-EXST"},
    "beam": {"BEAM", "BEAMS", "S-BEAM"},
    "stair": {"STAIR STEP", "STAIR", "STAIRS", "A-FLOR-STRS", "MATEL"},
    "wall_lines": {"WALLS", "L.H.W", "A-WALL", "BOUNDARY", "A-WALL-EXST"},
}


def on(layer, kind):
    return layer.upper() in LAYERS[kind]


# ---------- geometry helpers ----------
def bbox(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def poly_area(pts):
    n = len(pts)
    return abs(sum(pts[i][0] * pts[(i + 1) % n][1] - pts[(i + 1) % n][0] * pts[i][1] for i in range(n))) / 2


def in_box(x, y, b, pad=0.0):
    return b[0] - pad <= x <= b[2] + pad and b[1] - pad <= y <= b[3] + pad


def box_gap(a, b):
    dx = max(0, max(a[0], b[0]) - min(a[2], b[2]))
    dy = max(0, max(a[1], b[1]) - min(a[3], b[3]))
    return math.hypot(dx, dy)


def fmt_ftin(v):
    sign = "-" if v < 0 else ""
    v = abs(round(v * 2) / 2)
    ft = int(v // 12)
    inch = v - ft * 12
    whole = int(inch)
    s = f"{whole}" + (" 1/2" if inch - whole >= 0.5 else "")
    return f"{sign}{ft}'-{s}\""


def parse_ftin(s):
    s = s.replace(" ", "")
    m = re.fullmatch(r"([+-]?)(\d+)'-?(\d+(?:\.\d+)?)?(?:(\d)/(\d))?\"?", s)
    if m:
        v = int(m.group(2)) * 12 + float(m.group(3) or 0) + (int(m.group(4)) / int(m.group(5)) if m.group(4) else 0)
        return -v if m.group(1) == "-" else v
    m = re.fullmatch(r"([+-]?)(\d+(?:\.\d+)?)\"", s)
    if m:
        return (-1 if m.group(1) == "-" else 1) * float(m.group(2))
    return None


def hatch_loops(h):
    out = []
    for p in h.paths:
        if isinstance(p, PolylinePath):
            pts = [(v[0], v[1]) for v in p.vertices]
        else:
            pts = [(e.start[0], e.start[1]) for e in p.edges if e.type.name == "LINE"]
        if len(pts) > 1 and math.dist(pts[0], pts[-1]) < 1e-6:
            pts = pts[:-1]
        if len(pts) >= 3:
            out.append(pts)
    return out


def is_rect(pts):
    x0, y0, x1, y1 = bbox(pts)
    return abs(poly_area(pts) - (x1 - x0) * (y1 - y0)) < 1.0


def perimeter(pts):
    return sum(math.dist(pts[i], pts[(i + 1) % len(pts)]) for i in range(len(pts)))


def strip_thickness(pts):
    """Thickness of a wall strip from its area and perimeter (exact for rectangles, close for L and T shapes)."""
    q = perimeter(pts) / 4
    return q - math.sqrt(max(0.0, q * q - poly_area(pts)))


def face_thickness(pts, face, X0, X1, YR, YF):
    """Wall thickness measured off the envelope face: the length-weighted commonest distance of the
    edges that run parallel to the face. Handles notches, recesses and L-shaped loops."""
    weight = defaultdict(float)
    n = len(pts)
    for i in range(n):
        a, b = pts[i], pts[(i + 1) % n]
        if face in ("left", "right") and abs(a[0] - b[0]) < TOL:
            dist = a[0] - X0 if face == "left" else X1 - a[0]
        elif face in ("rear", "front") and abs(a[1] - b[1]) < TOL:
            dist = YR - a[1] if face == "rear" else a[1] - YF
        else:
            continue
        if dist > 1:
            weight[round(dist * 2) / 2] += math.dist(a, b)
    return max(weight, key=weight.get) if weight else 0.0


def r2(v):
    return round(v, 2)


# ---------- recogniser ----------
def recognise(path):
    doc, auditor = recover.readfile(path)
    msp = doc.modelspace()
    checks, unclassified = [], []

    texts = []
    for t in msp.query("MTEXT TEXT"):
        txt = t.plain_text() if t.dxftype() == "MTEXT" else t.dxf.text
        texts.append({"text": " ".join(txt.split()), "x": t.dxf.insert.x, "y": t.dxf.insert.y})
    # single letters inside blocks (a compass is usually a block); kept apart so they never act as labels
    block_letters = []
    for ins in msp.query("INSERT"):
        try:
            for t in ins.virtual_entities():
                if t.dxftype() in ("TEXT", "MTEXT"):
                    txt = (t.plain_text() if t.dxftype() == "MTEXT" else t.dxf.text).strip()
                    if len(txt) == 1:
                        block_letters.append((txt.upper(), t.dxf.insert.x, t.dxf.insert.y))
        except Exception:
            pass

    # 1. RCC hatches: small rectangles are columns, the rest are cores or proposed RCC
    col_rects, rcc_loops = [], []
    for h in msp.query("HATCH"):
        if not on(h.dxf.layer, "column"):
            continue
        for pts in hatch_loops(h):
            b = bbox(pts)
            if is_rect(pts) and max(b[2] - b[0], b[3] - b[1]) <= 36:
                col_rects.append(b)
            else:
                rcc_loops.append(pts)
    columns = []
    for b in col_rects:
        if not any(all(abs(b[i] - c["bbox"][i]) < TOL for i in range(4)) for c in columns):
            columns.append({"bbox": [r2(v) for v in b], "w": r2(b[2] - b[0]), "d": r2(b[3] - b[1])})
    if not columns:
        raise ValueError("No RCC columns found: check the column layer alias table")

    # 2. Envelope. Width from the outer faces of the columns; depth from the rear-most column face to the
    #    front-most column on the side walls (verandah columns stand forward of the shopfront).
    X0 = min(c["bbox"][0] for c in columns)
    X1 = max(c["bbox"][2] for c in columns)
    YR = max(c["bbox"][3] for c in columns)
    side = [c for c in columns if abs(c["bbox"][0] - X0) < 1 or abs(c["bbox"][2] - X1) < 1]
    # front of the covered area: walk each side's columns from the rear, stop at the first bay with no side wall
    wall_boxes = [bbox(pts) for h in msp.query("HATCH") if on(h.dxf.layer, "wall_hatch") for pts in hatch_loops(h)]

    def front_of(col_on_side, wall_on_side):
        chain = sorted([c for c in columns if col_on_side(c["bbox"])], key=lambda c: -c["bbox"][3])
        walls_side = [b for b in wall_boxes if wall_on_side(b)]
        front = chain[0]["bbox"][1]
        for a, b in zip(chain, chain[1:]):
            top, bottom = a["bbox"][1], b["bbox"][3]
            gap = top - bottom
            if gap > TOL:
                walled = sum(max(0, min(w[3], top) - max(w[1], bottom)) for w in walls_side)
                if walled < 0.5 * gap:
                    break
            front = b["bbox"][1]
        return front
    yf_l = front_of(lambda b: abs(b[0] - X0) < 1, lambda w: abs(w[0] - X0) < 1 and w[3] - w[1] >= w[2] - w[0])
    yf_r = front_of(lambda b: abs(b[2] - X1) < 1, lambda w: abs(w[2] - X1) < 1 and w[3] - w[1] >= w[2] - w[0])
    YF = min(yf_l, yf_r)
    if abs(yf_l - yf_r) > 1:
        checks.append({"name": "covered front", "status": "warn", "detail": f"left and right walls end at different depths; the deeper is used"})
    YF_all = min(c["bbox"][1] for c in columns)
    env = (X0, YF, X1, YR)

    # 3. Walls on the wall-hatch layer. Envelope walls have a long face on the envelope boundary.
    walls = []
    for h in msp.query("HATCH"):
        if not on(h.dxf.layer, "wall_hatch"):
            continue
        for pts in hatch_loops(h):
            b = bbox(pts)
            w, d = b[2] - b[0], b[3] - b[1]
            t = strip_thickness(pts)
            face = None
            if d >= w and abs(b[0] - X0) < 1:
                face = "left"
            elif d >= w and abs(b[2] - X1) < 1:
                face = "right"
            elif w > d and abs(b[3] - YR) < 1:
                face = "rear"
            elif w > d and abs(b[1] - YF) < 1:
                face = "front"
            if face in ("left", "right") and (b[1] < YF - 1 or b[3] > YR + 1):
                unclassified.append({"kind": f"wall hatch on the {face} line but outside the covered envelope (verandah pier?)", "bbox": [r2(v) for v in b]})
                continue
            if face:
                t = face_thickness(pts, face, X0, X1, YR, YF)
            rec = {"outline": [[r2(x), r2(y)] for x, y in pts], "thickness": r2(t), "bbox": [r2(v) for v in b]}
            if face and max(w, d) >= 12 and 4 <= t <= 14:
                rec["face"] = face
                walls.append(rec)
            elif face:
                unclassified.append({"kind": f"{t:.1f}in thick hatch on the {face} face (pier or planter?)", "bbox": rec["bbox"]})
            elif t >= 8.5:
                unclassified.append({"kind": "interior 9in wall", "bbox": rec["bbox"], "length": r2(max(w, d))})
    # envelope walls share one construction: drop pieces whose thickness is off the modal value
    if walls:
        counts = defaultdict(int)
        for w in walls:
            counts[round(w["thickness"] * 2) / 2] += 1
        modal = max(counts, key=counts.get)
        keep = []
        for w in walls:
            if abs(w["thickness"] - modal) <= 1.0:
                w["thickness"] = modal
                keep.append(w)
            else:
                unclassified.append({"kind": f"{w['thickness']:.1f}in piece on the {w['face']} face, off the {modal}in wall thickness (pier or planter?)", "bbox": w["bbox"]})
        walls = keep
    thick = sorted({w["thickness"] for w in walls})

    # 4. RCC cores: a loop whose bounding box holds a LIFT or STAIR label is an existing core
    cores = []
    for pts in rcc_loops:
        b = bbox(pts)
        labels = [t["text"] for t in texts if in_box(t["x"], t["y"], b)]
        lab = " ".join(labels).upper()
        if "LIFT" in lab or "STAIR" in lab:
            diag = []
            for e in msp.query("LINE"):
                a, c = (e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)
                if abs(a[0] - c[0]) > 12 and abs(a[1] - c[1]) > 12 and in_box(a[0], a[1], b) and in_box(c[0], c[1], b):
                    diag += [a, c]
            shaft = [r2(v) for v in bbox(diag)] if diag else None
            cores.append({"outline": [[r2(x), r2(y)] for x, y in pts], "bbox": [r2(v) for v in b], "shaft": shaft,
                          "label": "LIFT" if "LIFT" in lab else "STAIR"})
        else:
            unclassified.append({"kind": "RCC not labelled as lift/stair (taken as proposed)", "bbox": [r2(v) for v in b], "labels": labels})

    # 5. Beams: parallel line pairs on the beam layer, 6-30in apart, overlapping >= 60in
    lines = []
    for e in msp.query("LINE"):
        if not on(e.dxf.layer, "beam"):
            continue
        a, b = (e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)
        L = math.dist(a, b)
        if L < 6:
            continue
        d = ((b[0] - a[0]) / L, (b[1] - a[1]) / L)
        if d[0] < -1e-9 or (abs(d[0]) < 1e-9 and d[1] < 0):
            a, b, d = b, a, (-d[0], -d[1])
        lines.append((a, b, d, L))
    used, beams = set(), []
    for i, (a, b, d, L) in enumerate(lines):
        if i in used:
            continue
        n = (-d[1], d[0])
        best = None
        for j, (a2, b2, d2, L2) in enumerate(lines):
            if j <= i or j in used or abs(d[0] * d2[1] - d[1] * d2[0]) > 0.01:
                continue
            sep = (a2[0] - a[0]) * n[0] + (a2[1] - a[1]) * n[1]
            if not (6 <= abs(sep) <= 30):
                continue
            proj = lambda p: (p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1]
            lo, hi = max(0, min(proj(a2), proj(b2))), min(L, max(proj(a2), proj(b2)))
            if hi - lo >= 60 and (best is None or abs(sep) < abs(best[1])):
                best = (j, sep, lo, hi)
        if best:
            j, sep, lo, hi = best
            used.update({i, j})
            off = sep / 2
            p0 = (a[0] + d[0] * lo + n[0] * off, a[1] + d[1] * lo + n[1] * off)
            p1 = (a[0] + d[0] * hi + n[0] * off, a[1] + d[1] * hi + n[1] * off)
            beams.append({"p0": [r2(p0[0]), r2(p0[1])], "p1": [r2(p1[0]), r2(p1[1])], "width": r2(abs(sep))})
    if len(lines) - len(used):
        checks.append({"name": "beam lines", "status": "info", "detail": f"{len(lines) - len(used)} short or unpaired beam-layer lines ignored (lift cross, lintel)"})

    beam_depth = slab_height = None
    for t in texts:
        u = t["text"].upper()
        m = re.search(r"(?:DROP|DOWN)\s*BEAM\s*(\d+'\s*-?\s*\d+\"|\d+\")", u)
        if m and beam_depth is None:
            beam_depth = parse_ftin(m.group(1))
        m = re.search(r"SLAB\s*HEIGHT\s*(\d+'\s*-?\s*\d+\")", u)
        if m:
            slab_height = parse_ftin(m.group(1))
    for bm in beams:
        bm["depth"] = beam_depth

    # 6. Levels
    levels = []
    for t in texts:
        for m in re.finditer(r"LEVEL\s*=?\s*([+-]\s*\d+'\s*-?\s*\d+\")", t["text"].upper()):
            v = parse_ftin(m.group(1))
            inside = in_box(t["x"], t["y"], env)
            levels.append({"value": v, "text": m.group(1).replace(" ", ""), "x": r2(t["x"]), "y": r2(t["y"]), "where": "inside" if inside else "outside"})

    # 7. Stairs. Flights: >= 3 equally spaced parallel lines of equal length, spacing 8-14in.
    segs, circles = [], []
    for e in msp.query("LINE CIRCLE"):
        if not on(e.dxf.layer, "stair"):
            continue
        if e.dxftype() == "CIRCLE":
            circles.append((e.dxf.center.x, e.dxf.center.y, e.dxf.radius))
            continue
        segs.append(((e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)))
    groups = defaultdict(list)
    for a, b in segs:
        if abs(a[1] - b[1]) < TOL and abs(a[0] - b[0]) >= 24:
            groups[("h", round(min(a[0], b[0])), round(max(a[0], b[0])))].append(a[1])
        elif abs(a[0] - b[0]) < TOL and abs(a[1] - b[1]) >= 24:
            groups[("v", round(min(a[1], b[1])), round(max(a[1], b[1])))].append(a[0])
    stairs = []
    for (o, s0, s1), pos in groups.items():
        pos = sorted(set(round(p, 2) for p in pos))
        runs, run = [], [pos[0]]
        for p in pos[1:]:
            g = p - run[-1]
            if 8 <= g <= 14 and (len(run) < 2 or abs(g - (run[1] - run[0])) < 0.5):
                run.append(p)
            else:
                runs.append(run)
                run = [p]
        runs.append(run)
        for run in runs:
            if len(run) < 3:
                continue
            b = (s0, run[0], s1, run[-1]) if o == "h" else (run[0], s0, run[-1], s1)
            stairs.append({"kind": "flight", "bbox": [r2(v) for v in b], "treads_run": "y" if o == "h" else "x",
                           "width": r2(s1 - s0), "going": r2(run[1] - run[0]), "treads": len(run) - 1})
    done = set()
    for cx, cy, r in sorted(circles, key=lambda c: -c[2]):
        key = (round(cx), round(cy))
        if key in done or r < 18:
            continue
        radial = sum(1 for a, b in segs if math.dist(b, (cx, cy)) < 0.25 * r or math.dist(a, (cx, cy)) < 0.25 * r)
        if radial >= 8:
            done.add(key)
            stairs.append({"kind": "spiral", "center": [r2(cx), r2(cy)], "radius": r2(r), "treads": radial,
                           "bbox": [r2(cx - r), r2(cy - r), r2(cx + r), r2(cy + r)]})
    for st in stairs:
        if st["kind"] != "flight":
            continue
        diag = sum(1 for a, b in segs if abs(a[0] - b[0]) > TOL and abs(a[1] - b[1]) > TOL
                   and in_box(a[0], a[1], st["bbox"], 1) and in_box(b[0], b[1], st["bbox"], 1))
        if diag >= 2:
            st["kind"] = "ramp"
    for st in stairs:
        best = None
        for t in texts:
            if not re.search(r"STEP|STAIR|\bUP\b|\bDN\b", t["text"].upper()):
                continue
            g = box_gap(st["bbox"], (t["x"], t["y"], t["x"], t["y"]))
            if g <= 60 and (best is None or g < best[0]):
                best = (g, t["text"])
        st["label"] = best[1] if best else None
        st["direction"] = ("down" if re.search(r"\bDN\b|DOWN", best[1].upper()) else "up") if best else None
        st["label_xy"] = None
        if best:
            for t in texts:
                if t["text"] == best[1] and box_gap(st["bbox"], (t["x"], t["y"], t["x"], t["y"])) == best[0]:
                    st["label_xy"] = [r2(t["x"]), r2(t["y"])]
    # an unlabelled flight next to a labelled one is the other flight of the same stair (dog-leg or L)
    for i, st in enumerate(stairs):
        if st["kind"] == "flight" and st["label"] is None:
            near = [(box_gap(st["bbox"], o["bbox"]), j) for j, o in enumerate(stairs) if j != i and o["kind"] == "flight" and o["label"]]
            near = [n for n in near if n[0] <= 72]
            if near:
                st["same_stair_as"] = min(near)[1]

    # 8. Checks against what the architect wrote
    width, depth = X1 - X0, YR - YF
    covered = width * depth / 144
    stated = None
    for t in texts:
        m = re.search(r"COVERED\s*AREA.*?(\d[\d,]*)\s*S", t["text"].upper())
        if m:
            stated = float(m.group(1).replace(",", ""))
    if stated:
        status = "pass" if abs(covered - stated) / stated <= 0.005 else "warn"
        detail = f"computed {covered:,.0f} sft ({fmt_ftin(width)} x {fmt_ftin(depth)} to outer faces) vs {stated:,.0f} sft written on the drawing"
        if status == "warn":
            alt = stated * 144 / width
            detail += f"; the written figure equals a depth of {fmt_ftin(alt)}, {fmt_ftin(depth - alt)} less than measured"
        checks.append({"name": "covered area", "status": status, "detail": detail})

    arch = []
    for dm in msp.query("DIMENSION"):
        try:
            arch.append(dm.get_measurement())
        except Exception:
            pass
    left = sorted([c for c in side if abs(c["bbox"][0] - X0) < 1], key=lambda c: -c["bbox"][3])
    chain, prev = [], None
    for c in left:
        if prev is not None:
            chain.append(r2(prev - c["bbox"][3]))
        chain.append(c["d"])
        prev = c["bbox"][1]
    matched = 0
    for v in chain:
        ok = any(abs(v - a) < TOL for a in arch)
        if not ok:  # the architect may split a bay at an opening: accept consecutive dims that sum to it
            for i in range(len(arch)):
                s = 0
                for k in range(i, min(i + 4, len(arch))):
                    s += arch[k]
                    if k > i and abs(s - v) < TOL:
                        ok = True
                        break
                if ok:
                    break
        matched += ok
    checks.append({"name": "column bay chain", "status": "pass" if matched == len(chain) else "warn",
                   "detail": f"{matched}/{len(chain)} column sizes and bay lengths along the left wall agree with the architect's dimensions"})
    checks.append({"name": "overall width", "status": "pass" if any(abs(width - a) < TOL for a in arch) else "warn",
                   "detail": f"overall width {fmt_ftin(width)}"})
    checks.append({"name": "covered depth", "status": "pass" if any(abs(depth - a) < TOL for a in arch) else "warn",
                   "detail": f"covered depth {fmt_ftin(depth)}"})

    plot_rear = None
    for e in msp.query("LINE"):
        a, b = e.dxf.start, e.dxf.end
        if abs(a.y - b.y) < TOL and a.y > YR + 24 and min(a.x, b.x) <= X0 + 1 and max(a.x, b.x) >= X1 - 1:
            plot_rear = a.y if plot_rear is None else max(plot_rear, a.y)
    if plot_rear is not None:
        pd = plot_rear - YF_all
        checks.append({"name": "plot depth", "status": "pass" if any(abs(pd - a) < TOL for a in arch) else "info",
                       "detail": f"plot depth, rear boundary to front columns, {fmt_ftin(pd)}"})

    # boundary walls of the open yard behind the building: parallel line pairs 4-14in apart beyond the rear face
    yard_walls = []
    ylines = []
    for e in msp.query("LINE"):
        if not on(e.dxf.layer, "wall_lines"):
            continue
        a, b = (e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)
        if min(a[1], b[1]) < YR - 0.5 or math.dist(a, b) < 6:
            continue
        if abs(a[1] - b[1]) < TOL:
            ylines.append(("h", a[1], min(a[0], b[0]), max(a[0], b[0])))
        elif abs(a[0] - b[0]) < TOL:
            ylines.append(("v", a[0], min(a[1], b[1]), max(a[1], b[1])))
    for i, (o, c, s0, s1) in enumerate(ylines):
        for o2, c2, t0, t1 in ylines[i + 1:]:
            if o2 != o or not (4 <= abs(c2 - c) <= 14):
                continue
            lo, hi = max(s0, t0), min(s1, t1)
            if hi - lo < 6:
                continue
            th = abs(c2 - c)
            lo2, hi2 = min(s0, t0), max(s1, t1)   # fill the corner where the outer line runs on
            lo = lo2 if lo - lo2 <= th + 1 else lo
            hi = hi2 if hi2 - hi <= th + 1 else hi
            a0, a1 = min(c, c2), max(c, c2)
            rect = (lo, a0, hi, a1) if o == "h" else (a0, lo, a1, hi)
            if plot_rear is not None and rect[3] <= plot_rear + 0.5 and rect[2] - rect[0] > 0 and rect[3] - rect[1] > 0:
                yard_walls.append({"bbox": [r2(v) for v in rect], "thickness": r2(th)})
    def contains(A, B):
        return A[0] <= B[0] + TOL and A[1] <= B[1] + TOL and A[2] >= B[2] - TOL and A[3] >= B[3] - TOL
    yw = []
    for i, w in enumerate(yard_walls):
        dup = any((contains(o["bbox"], w["bbox"]) and (not contains(w["bbox"], o["bbox"]) or j < i)) for j, o in enumerate(yard_walls) if j != i)
        if not dup:
            yw.append(w)
    yard_walls = yw

    # north from a compass: N and S letters within 300in, E to the right of north when E and W exist
    north = None
    letters = [(t["text"].upper(), t["x"], t["y"]) for t in texts if len(t["text"]) == 1] + block_letters
    for c, nx, ny in letters:
        if c != "N":
            continue
        near = [l for l in letters if math.dist((l[1], l[2]), (nx, ny)) <= 300]
        south = sorted([l for l in near if l[0] == "S"], key=lambda l: math.dist((l[1], l[2]), (nx, ny)))
        if not south:
            continue
        vx, vy = nx - south[0][1], ny - south[0][2]
        e = [l for l in near if l[0] == "E"]
        w = [l for l in near if l[0] == "W"]
        if e and w and vx * (e[0][2] - w[0][2]) - vy * (e[0][1] - w[0][1]) > 0:
            continue
        north = round(math.degrees(math.atan2(-vx, vy)), 1)
        break
    if north is not None:
        checks.append({"name": "north", "status": "info", "detail": f"compass found in the drawing: north is {abs(north):.1f} degrees {'clockwise' if north < 0 else 'anticlockwise'} from up"})

    missing = []
    if not any(re.search(r"\bROAD\b", t["text"].upper()) for t in texts):
        missing.append("road name")
    if north is None:
        missing.append("north direction (no compass found in the drawing)")
    missing += ["neighbour labels (left, right, rear)", "branch name and address for the title block"]

    return {
        "units": "inch",
        "source": {"file": path, "entities": len(msp), "audit_errors": len(auditor.errors)},
        "envelope": {"x0": r2(X0), "x1": r2(X1), "y_rear": r2(YR), "y_front": r2(YF), "y_front_all": r2(YF_all),
                      "y_plot_rear": r2(plot_rear) if plot_rear is not None else None,
                      "width": r2(width), "depth": r2(depth), "covered_area_sft": round(covered, 1), "stated_area_sft": stated},
        "columns": columns,
        "walls": walls,
        "wall_thicknesses": thick,
        "yard_walls": yard_walls,
        "cores": cores,
        "beams": beams,
        "beam_depth": beam_depth,
        "slab_height": slab_height,
        "levels": levels,
        "north_degrees": north,
        "stairs": stairs,
        "left_bay_chain": chain,
        "checks": checks,
        "unclassified": unclassified,
        "missing_inputs": missing,
        "texts": texts,
    }


if __name__ == "__main__":
    s = recognise(sys.argv[1])
    with open(sys.argv[2], "w") as f:
        json.dump(s, f, indent=1)
    e = s["envelope"]
    print(f"source: {s['source']['entities']} entities, {s['source']['audit_errors']} audit errors")
    print(f"envelope {fmt_ftin(e['width'])} x {fmt_ftin(e['depth'])}; covered {e['covered_area_sft']:,.0f} sft; plot rear y {e['y_plot_rear']}")
    sizes = defaultdict(int)
    for c in s["columns"]:
        sizes[f"{c['w']:.0f}x{c['d']:.0f}"] += 1
    print("columns:", dict(sizes))
    print("envelope walls:", len(s["walls"]), "faces", sorted({w['face'] for w in s['walls']}), "thickness", s["wall_thicknesses"])
    print("yard walls:", [(w["bbox"], w["thickness"]) for w in s["yard_walls"]])
    print("cores:", [c["label"] for c in s["cores"]])
    print("beams:", len(s["beams"]), "widths", sorted({b['width'] for b in s['beams']}), "depth", s["beam_depth"], "| slab height", s["slab_height"])
    print("levels:", [(l['text'], l['where']) for l in s["levels"]])
    for st in s["stairs"]:
        print("stair:", st["kind"], st.get("treads"), "treads,", st.get("going"), "going,", st.get("width"), "wide |", st["label"], st["direction"], st.get("same_stair_as", ""))
    print("checks:")
    for c in s["checks"]:
        print(f"  [{c['status']}] {c['name']}: {c['detail']}")
    print("unclassified:")
    for u in s["unclassified"]:
        print("  -", u["kind"], u["bbox"], u.get("labels", ""))
    print("missing inputs:", s["missing_inputs"])
