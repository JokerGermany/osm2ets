# SPDX-License-Identifier: GPL-3.0-or-later
"""Wälder: Flächen mit ``landuse=forest`` oder ``natural=wood`` als Polygone fürs Zwischenformat.

Der Writer bepflanzt damit das Gelände (Bewuchs der Geländestreifen, SCS-Vegetationssätze nach
``leaf_type``). Ringe in Spielkoordinaten [x, z], vereinfacht (Douglas-Peucker ``simplify_m``),
Flächen unter ``min_area_m2`` (Echt-m²) entfallen. Außen- und Innenringe (Lichtungen) getrennt.
"""
from __future__ import annotations

import logging

import numpy as np
import osmium

log = logging.getLogger(__name__)


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


class _Reader(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.forests = []

    def area(self, a):
        t = a.tags
        if t.get("landuse") != "forest" and t.get("natural") != "wood":
            return
        leaf = t.get("leaf_type", "")
        oid = f"{'w' if a.from_way() else 'r'}{a.orig_id()}"
        try:
            for outer in a.outer_rings():
                o = [(n.lon, n.lat) for n in outer]
                inner = [[(n.lon, n.lat) for n in r] for r in a.inner_rings(outer)]
                self.forests.append((oid, leaf, o, inner))
        except Exception:     # unvollständige Multipolygone
            return


def forest_entries(input_path: str, profile, projector) -> tuple[list, dict]:
    cfg = getattr(profile, "forests", None) or {}
    stats = {"read": 0, "kept": 0, "area_km2": 0.0, "by_leaf": {}}
    if not cfg.get("enabled"):
        return [], stats
    rd = _Reader()
    try:
        rd.apply_file(input_path, locations=True)
    except RuntimeError as e:
        log.warning("Wälder: %s – OSM-Datei nicht nach IDs sortiert (osmium sort), keine Wälder", e)
        return [], stats
    scale = profile.scale_h
    tol = float(cfg["simplify_m"]) / scale
    min_area = float(cfg["min_area_m2"])

    def to_game(ll):
        arr = np.array(ll, float)
        xy = projector.forward(arr[:, 0], arr[:, 1]) / scale
        return np.column_stack([xy[:, 0], -xy[:, 1]])

    out = []
    stats["read"] = len(rd.forests)
    for oid, leaf, o, inner in rd.forests:
        if len(o) < 4:
            continue
        g = to_game(o)
        if _area(g) * scale * scale < min_area:
            continue
        g = _simplify(g, tol)
        holes = []
        for r in inner:
            if len(r) >= 4:
                h = to_game(r)
                if _area(h) * scale * scale >= min_area:
                    holes.append([[round(float(x), 1), round(float(z), 1)] for x, z in _simplify(h, tol)])
        leaf = leaf if leaf in ("broadleaved", "needleleaved", "mixed") else "mixed"
        out.append({"osm": oid, "leaf": leaf,
                    "outer": [[round(float(x), 1), round(float(z), 1)] for x, z in g], "inner": holes})
        stats["by_leaf"][leaf] = stats["by_leaf"].get(leaf, 0) + 1
        stats["area_km2"] += _area(g) * scale * scale / 1e6
    stats["kept"] = len(out)
    stats["area_km2"] = round(stats["area_km2"], 2)
    log.info("Wälder: %d Flächen (%.1f km²)", len(out), stats["area_km2"])
    return out, stats
