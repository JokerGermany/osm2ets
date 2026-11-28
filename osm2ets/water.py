# SPDX-License-Identifier: GPL-3.0-or-later
"""Wasserflächen aus OSM fürs Gelände: ``water[]`` mit ``osm``, ``kind``, ``outer``, ``inner`` (Spielkoordinaten).

Der Writer senkt das Gelände darin zu einem Becken ab und legt eine ebene, durchsichtige Wasserfläche auf
Höhe des Ufers darüber. Arten (``kind``):

* ``pool`` – leisure=swimming_pool im Freien (nicht indoor/covered/location=indoor|roof|underground)
* ``kneipp`` – amenity=kneipp_water_cure
* ``pond`` – water=pond; ``basin`` – water=basin, landuse=basin; ``reservoir`` – water=reservoir,
  landuse=reservoir; ``lake`` – water=lake; ``lock`` – water=lock; ``wastewater`` – water=wastewater
* ``river`` – fließendes Wasser: water=river/stream/canal/ditch/drain, waterway=riverbank
* ``water`` – natural=water ohne (bekanntes) water=*

Ganze Datei; Umrisse auf ``simplify_m`` vereinfacht, Flächen unter ``min_area_m2`` (Echt-m²) entfallen.
"""
from __future__ import annotations

import logging

import numpy as np
import osmium

from .forests import _area, _simplify

log = logging.getLogger(__name__)

WATER_KINDS = {"pond": "pond", "basin": "basin", "reservoir": "reservoir", "lake": "lake", "lock": "lock",
               "wastewater": "wastewater", "river": "river", "stream": "river", "canal": "river",
               "ditch": "river", "drain": "river", "oxbow": "lake"}
INDOOR = {"indoor", "roof", "underground", "rooftop"}


def kind_of(t) -> str | None:
    if t.get("leisure") == "swimming_pool":
        if t.get("indoor") == "yes" or t.get("covered") == "yes" or t.get("location") in INDOOR:
            return None
        return "pool"
    if t.get("amenity") == "kneipp_water_cure":
        return "kneipp"
    if t.get("landuse") == "basin":
        return "basin"
    if t.get("landuse") == "reservoir":
        return "reservoir"
    if t.get("waterway") == "riverbank":
        return "river"
    if t.get("natural") == "water":
        return WATER_KINDS.get(t.get("water") or "", "water")
    return None


class _Reader(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.areas = []

    def area(self, a):
        kind = kind_of(a.tags)
        if kind is None:
            return
        oid = f"{'w' if a.from_way() else 'r'}{a.orig_id()}"
        try:
            for outer in a.outer_rings():
                o = [(n.lon, n.lat) for n in outer]
                inner = [[(n.lon, n.lat) for n in r] for r in a.inner_rings(outer)]
                self.areas.append((oid, kind, o, inner))
        except Exception:     # unvollständige Multipolygone
            return


def water_entries(input_path: str, profile, projector) -> tuple[list, dict]:
    cfg = getattr(profile, "water", None) or {}
    stats = {"read": 0, "kept": 0, "by_kind": {}, "area_km2": 0.0}
    if not cfg.get("enabled"):
        return [], stats
    rd = _Reader()
    try:
        rd.apply_file(input_path, locations=True)
    except RuntimeError as e:
        log.warning("Wasser: %s – OSM-Datei nicht nach IDs sortiert (osmium sort), keine Wasserflächen", e)
        return [], stats
    scale = profile.scale_h
    tol = float(cfg["simplify_m"]) / scale
    min_area = float(cfg["min_area_m2"])

    def to_game(ll):
        arr = np.array(ll, float)
        xy = projector.forward(arr[:, 0], arr[:, 1]) / scale
        return np.column_stack([xy[:, 0], -xy[:, 1]])

    def rnd(pts):
        return [[round(float(x), 2), round(float(z), 2)] for x, z in pts]

    out = []
    stats["read"] = len(rd.areas)
    for oid, kind, o, inner in rd.areas:
        if len(o) < 4:
            continue
        g = to_game(o)
        a = _area(g) * scale * scale
        if a < min_area:
            continue
        holes = [rnd(_simplify(to_game(r), tol)) for r in inner if len(r) >= 4]
        out.append({"osm": oid, "kind": kind, "outer": rnd(_simplify(g, tol)), "inner": holes})
        stats["by_kind"][kind] = stats["by_kind"].get(kind, 0) + 1
        stats["area_km2"] += a / 1e6
    stats["kept"] = len(out)
    stats["area_km2"] = round(stats["area_km2"], 3)
    log.info("Wasser: %d Flächen (%s)", len(out), ", ".join(f"{k} {v}" for k, v in stats["by_kind"].items()))
    return out, stats
