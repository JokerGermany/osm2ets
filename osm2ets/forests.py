# SPDX-License-Identifier: GPL-3.0-or-later
"""Wälder, Baumreihen und Einzelbäume nahe der Strecke fürs Zwischenformat.

* Wälder: Flächen mit ``landuse=forest``, ``natural=wood`` oder ``landcover=trees`` (auch Multipolygone mit
  Lichtungen) als Ringe in Spielkoordinaten [x, z], vereinfacht (Douglas-Peucker ``simplify_m``); Flächen
  unter ``min_area_m2`` (Echt-m²) entfallen. Der Writer setzt an ihre Ränder Waldwände aus Bäumen – nur bis
  ``near_m`` neben den Straßen der Karte; das Innere bleibt leer, außer an Straßen, die hindurchführen.
* Baumreihen: Wege mit ``natural=tree_row``.
* Einzelbäume: Knoten mit ``natural=tree`` (mit ``height``, ``genus``).

Blattwerk aus ``leaf_type`` (broadleaved, needleleaved, mixed, leafless); fehlt es, bei Bäumen aus
``leaf_cycle=evergreen`` bzw. bekannten Gattungen, sonst ``mixed`` (Wald) bzw. ``broadleaved`` (Baum).
Ins Zwischenformat kommt nur, was höchstens ``near_m`` (Spielmeter) neben einer Straße der Karte liegt
(ohne Gleise) – Wälder auch, wenn eine Straße der Karte durch sie hindurchführt.
"""
from __future__ import annotations

import logging
import math
import re

import numpy as np
import osmium

log = logging.getLogger(__name__)

LEAF_TYPES = ("broadleaved", "needleleaved", "mixed", "leafless")
# Gattungen, deren Blattwerk feststeht (genus=*, lateinisch)
NEEDLE_GENERA = frozenset({"Picea", "Pinus", "Abies", "Larix", "Pseudotsuga", "Taxus", "Thuja", "Juniperus",
                           "Cedrus", "Tsuga", "Chamaecyparis", "Sequoiadendron", "Metasequoia"})


def leaf_of(tags, default: str) -> str:
    lt = tags.get("leaf_type")
    if lt in LEAF_TYPES:
        return lt
    genus = (tags.get("genus") or "").strip()
    if genus:
        return "needleleaved" if genus in NEEDLE_GENERA else "broadleaved"
    if tags.get("leaf_cycle") == "evergreen" and default == "broadleaved":
        return "needleleaved"
    return default


def _simplify(pts: np.ndarray, tol: float) -> np.ndarray:
    if len(pts) <= 4:
        return pts
    keep = np.zeros(len(pts), bool)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        a, b = stack.pop()
        if b <= a + 1:
            continue
        ab = pts[b] - pts[a]
        n = np.hypot(*ab)
        seg = pts[a + 1:b] - pts[a]
        d = np.abs(ab[0] * seg[:, 1] - ab[1] * seg[:, 0]) / n if n > 1e-9 else np.hypot(seg[:, 0], seg[:, 1])
        i = int(np.argmax(d))
        if d[i] > tol:
            keep[a + 1 + i] = True
            stack += [(a, a + 1 + i), (a + 1 + i, b)]
    return pts[keep]


def _area(p: np.ndarray) -> float:
    x, y = p[:, 0], p[:, 1]
    return 0.5 * abs(float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1))))


def _in_ring(r: np.ndarray, x: float, z: float) -> bool:
    inside = False
    n = len(r)
    for i in range(n):
        x1, z1 = r[i]
        x2, z2 = r[i - 1]
        if (z1 > z) != (z2 > z) and x < (x2 - x1) * (z - z1) / (z2 - z1) + x1:
            inside = not inside
    return inside


def _height(tags) -> float | None:
    m = re.match(r"^\s*([0-9]+(?:[.,][0-9]+)?)\s*(m)?\s*$", tags.get("height") or "")
    if not m:
        return None
    h = float(m.group(1).replace(",", "."))
    return h if 1 <= h <= 80 else None


def _is_forest(t) -> bool:
    return t.get("landuse") == "forest" or t.get("natural") == "wood" or t.get("landcover") == "trees"


class _Reader(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.forests = []
        self.trees = []
        self.rows = []

    def node(self, n):
        if n.tags.get("natural") == "tree":
            t = n.tags
            self.trees.append((f"n{n.id}", leaf_of(t, "broadleaved"), t.get("genus"), _height(t),
                               (n.location.lon, n.location.lat)))

    def way(self, w):
        if w.tags.get("natural") == "tree_row":
            try:
                ll = [(n.lon, n.lat) for n in w.nodes]
            except osmium.InvalidLocationError:
                return
            if len(ll) >= 2:
                self.rows.append((f"w{w.id}", leaf_of(w.tags, "broadleaved"), ll))

    def area(self, a):
        t = a.tags
        if not _is_forest(t):
            return
        oid = f"{'w' if a.from_way() else 'r'}{a.orig_id()}"
        try:
            for outer in a.outer_rings():
                o = [(n.lon, n.lat) for n in outer]
                inner = [[(n.lon, n.lat) for n in r] for r in a.inner_rings(outer)]
                self.forests.append((oid, leaf_of(t, "mixed"), o, inner))
        except Exception:     # unvollständige Multipolygone
            return


class _Near:
    """Raster der Zellen bis ``near`` neben den Straßen der Karte (ohne Gleise)."""

    def __init__(self, roads: list[dict], near: float):
        self.cell = cell = 50.0
        self.near = near
        self.cells: set[tuple[int, int]] = set()
        self.pts: list[tuple[float, float]] = []
        r = int(math.ceil((near + cell) / cell))
        for road in roads:
            if road.get("role") == "rail":
                continue
            pts = [(p[0], p[2]) for p in road["points"]]
            for (ax, az), (bx, bz) in zip(pts, pts[1:]):
                n = max(1, int(math.hypot(bx - ax, bz - az) // 20))
                for k in range(n + 1):
                    x, z = ax + (bx - ax) * k / n, az + (bz - az) * k / n
                    self.pts.append((x, z))
                    gx, gz = int(x // cell), int(z // cell)
                    for i in range(-r, r + 1):
                        for j in range(-r, r + 1):
                            if (i * i + j * j) * cell * cell <= (near + cell) ** 2:
                                self.cells.add((gx + i, gz + j))

    def __call__(self, x: float, z: float) -> bool:
        return (int(x // self.cell), int(z // self.cell)) in self.cells


def forest_entries(input_path: str, profile, projector, roads: list[dict] | None = None) -> tuple[dict, dict]:
    """Rückgabe: ({"forests": [...], "trees": [...], "tree_rows": [...]}, Kennzahlen)."""
    cfg = getattr(profile, "forests", None) or {}
    stats = {"read": 0, "kept": 0, "area_km2": 0.0, "by_leaf": {}, "trees": 0, "tree_rows": 0}
    empty = {"forests": [], "trees": [], "tree_rows": []}
    if not cfg.get("enabled"):
        return empty, stats
    rd = _Reader()
    try:
        rd.apply_file(input_path, locations=True)
    except RuntimeError as e:
        log.warning("Wälder: %s – OSM-Datei nicht nach IDs sortiert (osmium sort), keine Wälder", e)
        return empty, stats
    scale = profile.scale_h
    tol = float(cfg["simplify_m"]) / scale
    min_area = float(cfg["min_area_m2"])
    near = _Near(roads or [], float(cfg["near_m"])) if roads is not None else None

    def to_game(ll):
        arr = np.array(ll, float)
        xy = projector.forward(arr[:, 0], arr[:, 1]) / scale
        return np.column_stack([xy[:, 0], -xy[:, 1]])

    def rnd(pts):
        return [[round(float(x), 1), round(float(z), 1)] for x, z in pts]

    def ring_near(g: np.ndarray) -> bool:
        if near is None:
            return True
        closed = np.vstack([g, g[:1]])
        for a, b in zip(closed, closed[1:]):
            n = max(1, int(np.hypot(*(b - a)) // 40))
            for k in range(n + 1):
                q = a + (b - a) * k / n
                if near(q[0], q[1]):
                    return True
        return False

    def road_inside(g: np.ndarray) -> bool:
        if near is None:
            return False
        lo, hi = g.min(axis=0), g.max(axis=0)
        return any(lo[0] <= x <= hi[0] and lo[1] <= z <= hi[1] and _in_ring(g, x, z) for x, z in near.pts)

    out = []
    stats["read"] = len(rd.forests)
    for oid, leaf, o, inner in rd.forests:
        if len(o) < 4:
            continue
        g = to_game(o)
        if _area(g) * scale * scale < min_area:
            continue
        if not ring_near(g) and not road_inside(g):
            continue
        g = _simplify(g, tol)
        holes = []
        for r in inner:
            if len(r) >= 4:
                h = to_game(r)
                if _area(h) * scale * scale >= min_area:
                    holes.append(rnd(_simplify(h, tol)))
        out.append({"osm": oid, "leaf": leaf, "outer": rnd(g), "inner": holes})
        stats["by_leaf"][leaf] = stats["by_leaf"].get(leaf, 0) + 1
        stats["area_km2"] += _area(g) * scale * scale / 1e6
    trees = []
    for oid, leaf, genus, h, ll in rd.trees:
        x, z = to_game([ll])[0]
        if near is None or near(x, z):
            trees.append({"osm": oid, "leaf": leaf, "genus": genus, "pos": [round(float(x), 2), round(float(z), 2)],
                          "height": round(h / profile.scale_v, 1) if h else None})
    rows = []
    for oid, leaf, ll in rd.rows:
        g = to_game(ll)
        if near is None or any(near(x, z) for x, z in g):
            rows.append({"osm": oid, "leaf": leaf, "points": rnd(g)})
    stats.update(kept=len(out), area_km2=round(stats["area_km2"], 2), trees=len(trees), tree_rows=len(rows))
    log.info("Wälder: %d Flächen nahe der Strecke (%.1f km²), %d Baumreihen, %d Einzelbäume",
             len(out), stats["area_km2"], len(rows), len(trees))
    return {"forests": out, "trees": trees, "tree_rows": rows}, stats
