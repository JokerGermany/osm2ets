# SPDX-License-Identifier: GPL-3.0-or-later
"""Bodenbedeckung aus OSM für das Geländematerial: Flächen mit Gras, Wiesen, Äckern, Hofflächen, Obst- und
Rebanlagen, Baumschulen, Gewächshausflächen, Heide, Gebüsch und offenem Boden.

Der Writer belegt das ganze Gelände mit Gras („grass ger main“) und malt die Quads innerhalb dieser Flächen
mit einem passenden Material an (``landcover[]``: ``osm``, ``kind``, ``outer``, ``inner`` in
Spielkoordinaten; Flächen mit Reihen zusätzlich ``angle`` – Richtung der Längsseite in Grad, x-z-Ebene –,
Äcker ``crop`` aus OSM, falls angegeben). Arten:

* ``lawn`` – gepflegtes Gras: landuse=grass/village_green/greenery/recreation_ground, landcover=grass,
  leisure=park/garden/golf_course, leisure=pitch (nur mit Gras oder ohne surface)
* ``meadow`` – Wiese/Weide: landuse=meadow, natural=grassland
* ``farmland`` – Acker: landuse=farmland
* ``farmyard`` – Hoffläche: landuse=farmyard
* ``orchard`` – Obstplantage: landuse=orchard; ``vineyard`` – landuse=vineyard
* ``nursery`` – Baumschule: landuse=plant_nursery; ``greenhouse`` – landuse=greenhouse_horticulture
* ``heath`` – natural=heath; ``scrub`` – natural=scrub
* ``bare`` – offener Boden: landuse=quarry/landfill/construction/brownfield, natural=bare_rock/scree/sand

Umrisse auf ``simplify_m`` vereinfacht, Flächen unter ``min_area_m2`` (Echt-m²) entfallen. Ganze Datei
(das Gelände deckt die ganze Karte ab).
"""
from __future__ import annotations

import logging
import math

import numpy as np
import osmium

from .buildings import min_rect
from .forests import _area, _simplify

log = logging.getLogger(__name__)

PITCH_GRASS = frozenset({None, "grass", "grass_paver", "artificial_turf"})
# Arten mit Reihen (Furchen, Pflanzreihen): der Writer richtet das Material an der Längsseite aus
ROWS = frozenset({"farmland", "orchard", "vineyard", "nursery"})
LANDUSE_KINDS = {"farmland": "farmland", "farmyard": "farmyard", "orchard": "orchard", "vineyard": "vineyard",
                 "plant_nursery": "nursery", "greenhouse_horticulture": "greenhouse"}


def kind_of(t) -> str | None:
    lu, le, na, lc = t.get("landuse"), t.get("leisure"), t.get("natural"), t.get("landcover")
    if lu in ("grass", "village_green", "greenery", "recreation_ground") or lc == "grass" \
            or le in ("park", "garden", "golf_course") or (le == "pitch" and t.get("surface") in PITCH_GRASS):
        return "lawn"
    if lu == "meadow" or na == "grassland":
        return "meadow"
    if lu in LANDUSE_KINDS:
        return LANDUSE_KINDS[lu]
    if na == "heath":
        return "heath"
    if na == "scrub":
        return "scrub"
    if lu in ("quarry", "landfill", "construction", "brownfield") or na in ("bare_rock", "scree", "sand"):
        return "bare"
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
        crop = a.tags.get("crop") if kind == "farmland" else None
        try:
            for outer in a.outer_rings():
                o = [(n.lon, n.lat) for n in outer]
                inner = [[(n.lon, n.lat) for n in r] for r in a.inner_rings(outer)]
                self.areas.append((oid, kind, crop, o, inner))
        except Exception:     # unvollständige Multipolygone
            return


def landcover_entries(input_path: str, profile, projector) -> tuple[list, dict]:
    cfg = getattr(profile, "landcover", None) or {}
    stats = {"read": 0, "kept": 0, "by_kind": {}, "area_km2": {}}
    if not cfg.get("enabled"):
        return [], stats
    rd = _Reader()
    try:
        rd.apply_file(input_path, locations=True)
    except RuntimeError as e:
        log.warning("Bodenbedeckung: %s – OSM-Datei nicht nach IDs sortiert (osmium sort), keine Flächen", e)
        return [], stats
    scale = profile.scale_h
    tol = float(cfg["simplify_m"]) / scale
    min_area = float(cfg["min_area_m2"])

    def to_game(ll):
        arr = np.array(ll, float)
        xy = projector.forward(arr[:, 0], arr[:, 1]) / scale
        return np.column_stack([xy[:, 0], -xy[:, 1]])

    def rnd(pts):
        return [[round(float(x), 1), round(float(z), 1)] for x, z in pts]

    out = []
    stats["read"] = len(rd.areas)
    for oid, kind, crop, o, inner in rd.areas:
        if len(o) < 4:
            continue
        g = to_game(o)
        a = _area(g) * scale * scale
        if a < min_area:
            continue
        holes = [rnd(_simplify(to_game(r), tol)) for r in inner if len(r) >= 4]
        s = _simplify(g, tol)
        e = {"osm": oid, "kind": kind, "outer": rnd(s), "inner": holes}
        if kind in ROWS:
            e["angle"] = round(math.degrees(min_rect(s)[4]), 1)
        if crop:
            e["crop"] = crop
        out.append(e)
        stats["by_kind"][kind] = stats["by_kind"].get(kind, 0) + 1
        stats["area_km2"][kind] = round(stats["area_km2"].get(kind, 0.0) + a / 1e6, 2)
    stats["kept"] = len(out)
    log.info("Bodenbedeckung: %d Flächen (%s)", len(out), ", ".join(f"{k} {v}" for k, v in stats["by_kind"].items()))
    return out, stats
