# SPDX-License-Identifier: GPL-3.0-or-later
"""Gebäude nahe der Strecke (building=*).

Wie die Gleise nur Gebäude, die man von den Straßen der Stufe (Kern und Rampen) aus sieht: höchstens
``near_m`` daneben (Spielmeter). Je Gebäude kommen ins Zwischenformat:

* das kleinste umschließende Rechteck (Mitte, Länge, Breite, Richtung der Längsseite) – ETS2 kennt
  keine frei geformten Gebäude, der Writer setzt fertige SCS-Modelle darauf;
* die Art (``kind``) aus ``building=*``, eigenen ``shop``/``amenity``-Tags, der umgebenden
  ``landuse``-Fläche und der Grundfläche (``building=yes`` sind in Deutschland die meisten);
* Höhe aus ``height`` oder ``building:levels`` (sonst nichts – dann gilt die Modellhöhe);
* die Richtung zur nächsten Straße (``street``, Einheitsvektor in x/z) – aus allen OSM-Straßen bis
  ``STREET_SEARCH_M``, nicht nur denen der Karte; der Writer dreht die Vorderseite des Modells dorthin.
"""
from __future__ import annotations

import logging
import math
import re

import numpy as np
import osmium

log = logging.getLogger(__name__)

# Arten, auf die der Writer Modelle verteilt
KIND_BY_BUILDING = {
    **dict.fromkeys(("house", "detached", "semidetached_house", "bungalow", "farm", "presbytery",
                     "annexe", "terrace", "static_caravan", "cabin"), "house"),
    **dict.fromkeys(("apartments", "dormitory", "hotel", "barracks"), "apartments"),
    **dict.fromkeys(("garage", "garages"), "garage"),
    **dict.fromkeys(("shed", "hut", "allotment_house", "kiosk", "toilets", "service", "transformer_tower",
                     "tech_cab", "guardhouse", "bakehouse", "boathouse", "outbuilding", "beach_hut"), "shed"),
    **dict.fromkeys(("industrial", "warehouse", "manufacture", "factory", "hangar", "storage",
                     "quonset_hut", "parking"), "industrial"),
    **dict.fromkeys(("commercial", "office", "government", "civic", "public", "school", "college",
                     "university", "hospital", "kindergarten", "fire_station", "train_station",
                     "transportation", "museum", "sports_hall", "sports_centre", "riding_hall",
                     "pavilion", "military", "gatehouse"), "commercial"),
    **dict.fromkeys(("retail", "supermarket"), "retail"),
    **dict.fromkeys(("barn", "farm_auxiliary", "cowshed", "stable", "sty", "livestock", "greenhouse",
                     "conservatory"), "farm"),
    **dict.fromkeys(("church", "chapel", "cathedral", "religious", "monastery", "mosque", "synagogue",
                     "temple", "shrine", "kingdom_hall"), "church"),
    **dict.fromkeys(("silo", "storage_tank", "slurry_tank", "water_tower", "digester"), "silo"),
}
# Straßen, zu denen ein Gebäude seine Vorderseite dreht – auch solche, die nicht in der Karte sind.
# Nicht: Autobahnen und Kraftfahrstraßen (Häuser stehen nicht zur Autobahn hin), Einfahrten, Gassen und
# Parkplatzgassen (führen seitlich ans Haus).
STREET_KEYS = frozenset({"residential", "living_street", "unclassified", "tertiary", "secondary", "primary",
                         "service", "pedestrian", "road", "tertiary_link", "secondary_link", "primary_link"})
STREET_SKIP_SERVICE = frozenset({"driveway", "parking_aisle", "alley", "drive-through"})
STREET_SEARCH_M = 60.0     # Spielmeter um die Gebäudemitte
LANDUSE_KEYS = frozenset({"residential", "industrial", "commercial", "retail", "farmyard", "garages",
                          "religious", "military", "railway"})


def kind_of(tags: dict, landuse: str | None, area: float, length: float = 0.0, width: float = 0.0) -> str:
    """Art eines Gebäudes. ``building=yes`` (und unbekannte Werte) nach eigenen Tags, der umgebenden
    landuse-Fläche, der Grundfläche (m²) und dem Rechteck (Länge, Breite in m: schmal und lang im
    Wohngebiet = Garagenzeile)."""
    b = tags.get("building", "yes")
    if b in KIND_BY_BUILDING:
        k = KIND_BY_BUILDING[b]
        if k == "house" and b != "terrace" and area > 400:
            return "apartments"
        return k
    if tags.get("shop") or tags.get("amenity") in ("marketplace", "fuel"):
        return "retail" if area >= 40 else "shed"
    if tags.get("amenity") == "place_of_worship":
        return "church"
    if tags.get("amenity") or tags.get("office"):
        return "commercial" if area >= 40 else "shed"
    if b == "residential" or landuse in ("residential", "garages", None):
        if area < 15:
            return "shed"
        if landuse == "garages" or area < 40 or (0 < width < 7 and length >= 2.5 * width):
            return "garage"
        if landuse is None and area > 400:
            return "industrial"      # große Halle im Freien (Scheune, Lager)
        return "house" if area < 300 else "apartments"
    if landuse == "farmyard":
        return "shed" if area < 30 else "house" if area < 180 else "farm"
    if landuse in ("industrial", "railway", "military"):
        return "shed" if area < 50 else "industrial"
    if landuse == "commercial":
        return "shed" if area < 50 else "retail" if area > 2000 else "commercial"
    if landuse == "retail":
        return "shed" if area < 50 else "retail"
    if landuse == "religious":
        return "church" if area > 150 else "house"
    return "house"


def parse_height(tags: dict) -> tuple[float | None, float | None]:
    """(Höhe in m aus height, Stockwerke aus building:levels) – je None, wenn nicht lesbar."""
    def num(v):
        if v is None:
            return None
        m = re.match(r"^\s*([0-9]+(?:[.,][0-9]+)?)\s*(m)?\s*$", str(v))
        return float(m.group(1).replace(",", ".")) if m else None
    h = num(tags.get("height"))
    lv = num(tags.get("building:levels"))
    return (h if h and 1.5 <= h <= 300 else None), (lv if lv and 0 < lv <= 80 else None)


def min_rect(xy: np.ndarray) -> tuple[float, float, float, float, float]:
    """Kleinstes umschließendes Rechteck: (Mitte x, Mitte y, Länge, Breite, Winkel der Längsseite)."""
    pts = np.unique(np.round(xy, 3), axis=0)
    if len(pts) < 3:
        c = pts.mean(axis=0)
        return float(c[0]), float(c[1]), 0.0, 0.0, 0.0
    hull = _hull(pts)
    best = None
    for i in range(len(hull)):
        e = hull[(i + 1) % len(hull)] - hull[i]
        ang = math.atan2(e[1], e[0])
        ca, sa = math.cos(ang), math.sin(ang)
        rot = hull @ np.array([[ca, -sa], [sa, ca]])       # in Kantenrichtung drehen
        lo, hi = rot.min(axis=0), rot.max(axis=0)
        area = float(np.prod(hi - lo))
        if best is None or area < best[0] - 1e-9:
            best = (area, ang, lo, hi)
    _, ang, lo, hi = best
    ca, sa = math.cos(ang), math.sin(ang)
    mid = (lo + hi) / 2
    cx, cy = mid[0] * ca - mid[1] * sa, mid[0] * sa + mid[1] * ca
    a, b = float(hi[0] - lo[0]), float(hi[1] - lo[1])
    if b > a:                       # Länge = längere Seite
        a, b, ang = b, a, ang + math.pi / 2
    ang = (ang + math.pi / 2) % math.pi - math.pi / 2   # Richtung ohne Vorzeichen: −90°…90°
    return float(cx), float(cy), a, b, ang


def _hull(p: np.ndarray) -> np.ndarray:
    p = p[np.lexsort((p[:, 1], p[:, 0]))]

    def half(pts):
        out = []
        for q in pts:
            while len(out) >= 2:
                a, b = out[-1] - out[-2], q - out[-2]
                if a[0] * b[1] - a[1] * b[0] > 0:
                    break
                out.pop()
            out.append(q)
        return out
    lower, upper = half(p), half(p[::-1])
    return np.array(lower[:-1] + upper[:-1])


def _polygon_area(xy: np.ndarray) -> float:
    x, y = xy[:, 0], xy[:, 1]
    return float(abs(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1))) / 2)


def _inside(px: float, py: float, poly: np.ndarray) -> bool:
    x, y = poly[:, 0], poly[:, 1]
    x2, y2 = np.roll(x, -1), np.roll(y, -1)
    cross = (y > py) != (y2 > py)
    with np.errstate(divide="ignore", invalid="ignore"):
        xi = x + (py - y) * (x2 - x) / (y2 - y)
    return bool(np.count_nonzero(cross & (px < xi)) % 2)


class _Reader(osmium.SimpleHandler):
    """Straßen (für die Ausrichtung); mit ``closed_ways`` auch Gebäude und landuse-Flächen aus geschlossenen
    Wegen – Rückfall für unsortierte Dateien, ohne Multipolygone."""

    def __init__(self, exclude: frozenset, closed_ways: bool = False):
        super().__init__()
        self.exclude = exclude
        self.closed_ways = closed_ways
        self.buildings: list[tuple[str, dict, list]] = []
        self.landuse: list[tuple[str, list]] = []
        self.streets: list[list] = []

    def way(self, w):
        t = w.tags
        street = t.get("highway") in STREET_KEYS and t.get("service") not in STREET_SKIP_SERVICE
        if not street and not (self.closed_ways and w.is_closed() and ("building" in t or "landuse" in t)):
            return
        try:
            ll = [(n.lon, n.lat) for n in w.nodes]
        except osmium.InvalidLocationError:
            return
        if street:
            self.streets.append(ll)
        else:
            self._take(f"w{w.id}", t, ll)

    def _take(self, oid: str, t, ll: list) -> None:
        b = t.get("building")
        lu = t.get("landuse")
        if b is None and lu not in LANDUSE_KEYS:
            return
        if b is not None and (b in self.exclude or t.get("building:part")):
            return
        if len(ll) < 4:
            return
        if b is not None:
            self.buildings.append((oid, {k: t.get(k) for k in ("building", "height", "building:levels", "shop",
                                                                "amenity", "office", "name")}, ll))
        else:
            self.landuse.append((lu, ll))


class _AreaReader(_Reader):
    """Gebäude und landuse-Flächen als Flächen (geschlossene Wege und Multipolygone, äußerer Ring);
    braucht nach IDs sortierte Dateien."""

    def area(self, a):
        t = a.tags
        if t.get("building") is None and t.get("landuse") not in LANDUSE_KEYS:
            return
        try:
            ring = next(iter(a.outer_rings()))
            ll = [(n.lon, n.lat) for n in ring]
        except Exception:      # unvollständige Multipolygone
            return
        self._take(f"{'w' if a.from_way() else 'r'}{a.orig_id()}", t, ll)


def building_entries(input_path: str, profile, roads: list[dict], projector, bbox=None) -> tuple[list, dict]:
    """Gebäude bis ``buildings.near_m`` neben Kern und Rampen der Stufe (``roads`` in Spielkoordinaten).
    Rückgabe: (Einträge fürs Zwischenformat, Kennzahlen)."""
    cfg = getattr(profile, "buildings", None) or {}
    stats = {"read": 0, "near": 0, "by_kind": {}}
    if not cfg.get("enabled"):
        return [], stats
    anchors = [np.array([[p[0], p[2]] for p in r["points"]]) for r in roads if r["role"] in ("core", "link")]
    if not anchors:
        return [], stats
    rd = _AreaReader(frozenset(cfg["exclude"]))
    try:
        rd.apply_file(input_path, locations=True)
    except RuntimeError as e:          # „Way IDs out of order“: Multipolygone brauchen sortierte Dateien
        log.warning("Gebäude: %s – OSM-Datei nicht nach IDs sortiert (osmium sort), lese nur geschlossene "
                    "Wege, Gebäude und Flächen aus Multipolygonen fehlen", e)
        rd = _Reader(frozenset(cfg["exclude"]), closed_ways=True)
        rd.apply_file(input_path, locations=True)
    stats["read"] = len(rd.buildings)
    scale = profile.scale_h

    def to_game(ll):
        lon = np.array([p[0] for p in ll])
        lat = np.array([p[1] for p in ll])
        xy = projector.forward(lon, lat) / scale
        return np.column_stack([xy[:, 0], -xy[:, 1]])

    # Straßen der Stufe in ein Raster (Zellen near_m)
    near = float(cfg["near_m"])
    cell = max(near, 50.0)
    grid: dict[tuple[int, int], list] = {}
    for pts in anchors:
        for a, b in zip(pts[:-1], pts[1:]):
            lo, hi = np.minimum(a, b) - near, np.maximum(a, b) + near
            for gx in range(int(lo[0] // cell), int(hi[0] // cell) + 1):
                for gz in range(int(lo[1] // cell), int(hi[1] // cell) + 1):
                    grid.setdefault((gx, gz), []).append((a, b))

    def dist(px, pz):
        best = math.inf
        for a, b in grid.get((int(px // cell), int(pz // cell)), ()):
            ab = b - a
            t = max(0.0, min(1.0, ((px - a[0]) * ab[0] + (pz - a[1]) * ab[1]) / max(ab @ ab, 1e-9)))
            best = min(best, math.hypot(px - a[0] - t * ab[0], pz - a[1] - t * ab[1]))
        return best

    # Straßen (aus ganz OSM) in ein Raster, für die Ausrichtung der Vorderseite
    scell = STREET_SEARCH_M
    sgrid: dict[tuple[int, int], list] = {}
    for ll in rd.streets:
        g = to_game(ll)
        for a, b in zip(g[:-1], g[1:]):
            lo, hi = np.minimum(a, b) - scell, np.maximum(a, b) + scell
            for gx in range(int(lo[0] // scell), int(hi[0] // scell) + 1):
                for gz in range(int(lo[1] // scell), int(hi[1] // scell) + 1):
                    sgrid.setdefault((gx, gz), []).append((a, b))

    def street_dir(px, pz):
        best, bp = STREET_SEARCH_M, None
        for a, b in sgrid.get((int(px // scell), int(pz // scell)), ()):
            ab = b - a
            t = max(0.0, min(1.0, ((px - a[0]) * ab[0] + (pz - a[1]) * ab[1]) / max(ab @ ab, 1e-9)))
            qx, qz = a[0] + t * ab[0], a[1] + t * ab[1]
            dd = math.hypot(qx - px, qz - pz)
            if 1e-6 < dd < best:
                best, bp = dd, (qx, qz)
        if bp is None:
            return None
        return [round((bp[0] - px) / best, 4), round((bp[1] - pz) / best, 4)]

    landuse = []
    for lu, ll in rd.landuse:
        g = to_game(ll)
        landuse.append((lu, g, g.min(axis=0), g.max(axis=0), _polygon_area(g)))
    out = []
    by_kind: dict[str, int] = {}
    for oid, tags, ll in rd.buildings:
        if bbox is not None and not any(bbox[0] <= lon <= bbox[2] and bbox[1] <= lat <= bbox[3] for lon, lat in ll):
            continue
        g = to_game(ll)
        cx, cz, length, width, ang = min_rect(g)
        if length < 2.0 or width < 1.5:
            continue
        if dist(cx, cz) - length / 2 > near:
            continue
        area = _polygon_area(g)
        # kleinste umgebende landuse-Fläche
        lu = None
        lu_area = math.inf
        for name, poly, lo, hi, pa in landuse:
            if lo[0] <= cx <= hi[0] and lo[1] <= cz <= hi[1] and pa < lu_area and _inside(cx, cz, poly):
                lu, lu_area = name, pa
        kind = kind_of(tags, lu, area * scale * scale, length * scale, width * scale)
        h, levels = parse_height(tags)
        out.append({"osm": oid, "building": tags.get("building"), "kind": kind, "landuse": lu,
                    "center": [round(cx, 2), round(cz, 2)], "length": round(length, 2),
                    "width": round(width, 2), "angle": round(math.degrees(ang), 2),
                    "area": round(area, 1), "fill": round(area / max(length * width, 1e-6), 3),
                    "height": h / profile.scale_v if h else None, "levels": levels,
                    "street": street_dir(cx, cz)})
        by_kind[kind] = by_kind.get(kind, 0) + 1
    # Gebäude, deren Mitte in einem größeren liegt (überlappend erfasst), weglassen – sonst stünden zwei
    # Modelle ineinander
    out.sort(key=lambda e: -e["length"] * e["width"])
    kept: list[dict] = []
    nested = 0
    for e in out:
        cx, cz = e["center"]
        inside = False
        for o in kept:
            if abs(o["center"][0] - cx) > o["length"] or abs(o["center"][1] - cz) > o["length"]:
                continue
            a = math.radians(o["angle"])
            dx, dz = cx - o["center"][0], cz - o["center"][1]
            if abs(dx * math.cos(a) + dz * math.sin(a)) <= o["length"] / 2 and \
                    abs(-dx * math.sin(a) + dz * math.cos(a)) <= o["width"] / 2:
                inside = True
                break
        if inside:
            nested += 1
            by_kind[e["kind"]] -= 1
            continue
        kept.append(e)
    out = sorted(kept, key=lambda e: e["osm"])
    stats.update(near=len(out), nested=nested,
                 by_kind=dict(sorted(((k, v) for k, v in by_kind.items() if v), key=lambda kv: -kv[1])))
    log.info("Gebäude: %d gelesen, %d bis %.0f m neben der Strecke (%s)", stats["read"], len(out), near,
             ", ".join(f"{k} {v}" for k, v in stats["by_kind"].items()))
    return out, stats
