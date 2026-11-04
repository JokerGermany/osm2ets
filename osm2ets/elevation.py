# SPDX-License-Identifier: GPL-3.0-or-later
"""Höhen: Geländemodell abtasten, glätten, Brücken und Tunnel einarbeiten.

Alle Rechnungen laufen auf einem gemeinsamen Graphen aus den verdichteten
Punkten aller Stücke; Punkte an gemeinsamen OSM-Knoten sind ein Knoten. Damit
haben alle Straßen an einer Kreuzung dieselbe Höhe.

Ablauf:
  1. Gelände entlang jedes Stücks abtasten und glätten (ohne Geländemodell: 0).
     Stückenden werden auf den ungeglätteten Wert am OSM-Knoten festgehalten.
  2. Brücken (Bauwerke ab `min_bridge_length_m`), unter denen keine Straße des
     Netzes liegt, werden um `bridge_clearance_m × layer` angehoben. Die Anhebung
     fällt entlang des Netzes mit höchstens `max_ramp_grade` ab – so entstehen
     Rampen, auch über Kreuzungen hinweg. Brücken über Straßen des Netzes hebt
     erst Schritt 4 an, und nur so weit, wie der Lichtraum es verlangt.
  3. Bauwerke als Ganzes: Brückendecks hängen nicht ins Tal durch, Tunnel
     steigen nicht über den Berg. Dafür wird jedes zusammenhängende Bauwerk
     (auch über Kreuzungen im Bauwerk hinweg) harmonisch zwischen seinen
     Widerlagern bzw. Portalen interpoliert.
  4. Höhenfreie Kreuzungen mit zu wenig Abstand: Liegt unten ein Tunnel, wird
     er abgesenkt, sonst wird die obere Straße angehoben (Rangfolge: layer,
     Brücke, aktuelle Höhe, Rampe).
"""
from __future__ import annotations

import heapq
import logging
import math
from typing import Optional

import numpy as np

from .geometry import NO_NODE, Piece, Projector
from .network import is_bridge, is_tunnel, parse_layer

log = logging.getLogger(__name__)


class DemSampler:
    """Bilineares Abtasten eines Geländemodells (GeoTIFF, VRT, ... via rasterio)."""

    def __init__(self, path: str, projector: Projector, xy_bounds: tuple[float, float, float, float],
                 margin_m: float = 500.0):
        try:
            import rasterio
            from rasterio.windows import from_bounds
            from pyproj import Transformer
        except ImportError as exc:  # pragma: no cover
            raise RuntimeError("Für Geländemodelle wird rasterio benötigt: pip install 'osm2ets[dem]'") from exc

        self.path = path
        ds = rasterio.open(path)
        if ds.crs is None:
            raise RuntimeError(f"{path}: Geländemodell hat kein Koordinatenbezugssystem")
        self._to_dem = Transformer.from_crs(projector.crs, ds.crs, always_xy=True)
        self._projector = projector

        # nur den benötigten Ausschnitt lesen
        minx, miny, maxx, maxy = xy_bounds
        corners = projector.absolute(np.array([
            [minx - margin_m, miny - margin_m], [maxx + margin_m, miny - margin_m],
            [minx - margin_m, maxy + margin_m], [maxx + margin_m, maxy + margin_m]]))
        cx, cy = self._to_dem.transform(corners[:, 0], corners[:, 1])
        window = from_bounds(min(cx), min(cy), max(cx), max(cy), transform=ds.transform)
        window = window.round_offsets().round_lengths()
        col_off = max(int(window.col_off), 0)
        row_off = max(int(window.row_off), 0)
        col_end = min(int(window.col_off + window.width), ds.width)
        row_end = min(int(window.row_off + window.height), ds.height)
        if col_end <= col_off or row_end <= row_off:
            raise RuntimeError(f"{path}: Geländemodell deckt das Gebiet nicht ab")
        from rasterio.windows import Window
        win = Window(col_off, row_off, col_end - col_off, row_end - row_off)
        band = ds.read(1, window=win, masked=True).astype("float64")
        self.data = band.filled(np.nan)
        self.transform = ds.window_transform(win)
        self.inv = ~self.transform
        ds.close()
        self.missing = 0

    def sample(self, xy: np.ndarray) -> np.ndarray:
        absxy = self._projector.absolute(xy)
        dx, dy = self._to_dem.transform(absxy[:, 0], absxy[:, 1])
        dx, dy = np.asarray(dx), np.asarray(dy)
        inv = self.inv  # affine.Affine; Koeffizienten direkt, unabhängig von der affine-Version
        col = inv.a * dx + inv.b * dy + inv.c
        row = inv.d * dx + inv.e * dy + inv.f
        # Pixelmitten liegen bei +0.5
        c = col - 0.5
        r = row - 0.5
        h, w = self.data.shape
        c0 = np.clip(np.floor(c).astype(int), 0, w - 2)
        r0 = np.clip(np.floor(r).astype(int), 0, h - 2)
        fc = np.clip(c - c0, 0.0, 1.0)
        fr = np.clip(r - r0, 0.0, 1.0)
        d = self.data
        v = (d[r0, c0] * (1 - fc) * (1 - fr) + d[r0, c0 + 1] * fc * (1 - fr)
             + d[r0 + 1, c0] * (1 - fc) * fr + d[r0 + 1, c0 + 1] * fc * fr)
        bad = ~np.isfinite(v)
        if bad.any():
            # Lücken: nächster gültiger Nachbar, sonst 0
            near = d[np.clip(np.rint(r).astype(int), 0, h - 1), np.clip(np.rint(c).astype(int), 0, w - 1)]
            v = np.where(bad, near, v)
            still = ~np.isfinite(v)
            self.missing += int(still.sum())
            v = np.where(still, 0.0, v)
        return v


def smooth_along(s: np.ndarray, h: np.ndarray, window: float) -> np.ndarray:
    """Gleitender Mittelwert über die Bogenlänge (Fensterbreite in Metern)."""
    if len(s) < 3 or window <= 0 or s[-1] <= 0:
        return h.copy()
    step = max(min(window / 8.0, float(np.median(np.diff(s)))), 0.5)
    m = max(int(math.ceil(s[-1] / step)) + 1, 2)
    grid = np.linspace(0.0, s[-1], m)
    hg = np.interp(grid, s, h)
    k = max(1, int(round(window / (grid[1] - grid[0]))))
    if k % 2 == 0:
        k += 1
    pad = k // 2
    padded = np.pad(hg, pad, mode="edge")
    smoothed = np.convolve(padded, np.ones(k) / k, mode="valid")
    return np.interp(s, grid, smoothed)


def low_percentile_along(s: np.ndarray, h: np.ndarray, window: float, q: float) -> np.ndarray:
    """Gleitendes Perzentil q (0–100) über die Bogenlänge – unterdrückt Bäume/Gebäude im DSM."""
    if len(s) < 3 or window <= 0:
        return h.copy()
    out = np.empty_like(h)
    half = window / 2
    lo = 0
    hi = 0
    for i, si in enumerate(s):
        while s[lo] < si - half:
            lo += 1
        while hi < len(s) and s[hi] <= si + half:
            hi += 1
        out[i] = np.percentile(h[lo:hi], q)
    return out


def clamp_grade(z: np.ndarray, ea, eb, elen, grade: float, iterations: int = 200,
                fixed: Optional[np.ndarray] = None, only: Optional[np.ndarray] = None) -> int:
    """Begrenzt die Steigung jeder Kante auf grade, indem beide Enden aufeinander zu
    rücken (Relaxation über den gemeinsamen Graphen). `fixed` hält Punkte fest; `only`
    erlaubt je Punkt nur eine Richtung (+1 nur anheben, −1 nur absenken, 0 beide).
    Gibt die Zahl der Durchläufe zurück."""
    a = np.asarray(ea, np.int64)
    b = np.asarray(eb, np.int64)
    w = np.maximum(np.asarray(elen, float), 1e-6)
    grade = np.broadcast_to(np.asarray(grade, float), w.shape)
    if not (grade > 0).any():
        return 0
    fa = fixed[a] if fixed is not None else np.zeros(len(a), bool)
    fb = fixed[b] if fixed is not None else np.zeros(len(b), bool)
    oa = only[a] if only is not None else np.zeros(len(a), np.int8)
    ob = only[b] if only is not None else np.zeros(len(b), np.int8)
    for it in range(iterations):
        dz = z[b] - z[a]
        excess = np.abs(dz) - grade * w
        bad = excess > 1e-3
        if not bad.any():
            return it
        # a rückt in Richtung von dz, b entgegengesetzt – je Ende nur, wo das erlaubt ist
        s = np.sign(dz)
        ca = ~fa & ((oa == 0) | (oa == s))
        cb = ~fb & ((ob == 0) | (ob == -s))
        bad &= ca | cb
        if not bad.any():
            return it
        # Anteil je Ende: unbeweglich = 0, sonst halb bzw. ganz
        share_a = np.where(ca, np.where(cb, 0.5, 1.0), 0.0)
        share_b = np.where(cb, np.where(ca, 0.5, 1.0), 0.0)
        corr = np.where(bad, s * excess, 0.0)
        delta = np.zeros_like(z)
        cnt = np.zeros_like(z)
        np.add.at(delta, a, corr * share_a)
        np.add.at(delta, b, -corr * share_b)
        np.add.at(cnt, a, (bad & ca).astype(float))
        np.add.at(cnt, b, (bad & cb).astype(float))
        z += np.where(cnt > 0, delta / np.maximum(cnt, 1), 0.0)
    return iterations


# Höchste Längsneigung nach RAA (Entwurfsklassen): EKA 1A 4 %, EKA 1B/2 4,5 %, Rampen ~6 %
MAJOR = {"motorway", "trunk"}
GRADE_BY_HIGHWAY = {"motorway": 0.06, "trunk": 0.06}
# Glättungsfenster der Geländehöhe längs der Straße (m): Autobahnen weiträumiger
WINDOW_BY_HIGHWAY = {"motorway": 200.0, "trunk": 180.0, "motorway_link": 140.0, "trunk_link": 140.0}


DECK_THICKNESS_M = 1.2
SAG_WINDOW_M = 160.0      # Mulden in Autobahnprofilen auffüllen (Spielmeter)
SAG_MAX_RAISE_M = 3.0
# Reserve beim Anheben an Kreuzungen: mit Geländemodell verschieben Steigungsgrenzen und Hülle
# danach die Höhen um einige Zentimeter (ohne pauschales Anheben fehlte sonst bis 0,11 m Lichtraum)
CLEARANCE_MARGIN_M = 0.2


def parse_maxheight(value) -> Optional[float]:
    """OSM maxheight → Meter; None bei fehlend/ungültig/none/default.
    Erlaubt: "3.8", "3,8", "3.8 m", "12'6\"" (Fuß/Zoll)."""
    if not value:
        return None
    v = str(value).strip().lower().split(";")[0]
    if v in ("none", "default", "below_default", "no_sign", "no_indications", "unsigned"):
        return None
    import re
    m = re.fullmatch(r"(\d+)\s*'\s*(?:(\d+)\s*\"?)?", v)
    if m:
        return round(int(m.group(1)) * 0.3048 + int(m.group(2) or 0) * 0.0254, 2)
    m = re.fullmatch(r"(\d+(?:[.,]\d+)?)\s*(m)?", v)
    if m:
        h = float(m.group(1).replace(",", "."))
        return h if 1.5 <= h <= 10 else None
    return None


def _maxheight(piece: Piece, seg: int) -> Optional[float]:
    tags = piece.chain.parts[piece.dense.seg_part[seg]].edge.way.tags
    return parse_maxheight(tags.get("maxheight") or tags.get("maxheight:physical"))


def _highway(piece: Piece, seg: int) -> str:
    return piece.chain.parts[piece.dense.seg_part[seg]].edge.highway


def _piece_window(piece: Piece, default: float) -> float:
    n = max(len(piece.dense.xy) - 1, 1)
    best = default
    for hw, win in WINDOW_BY_HIGHWAY.items():
        share = sum(1 for j in range(n) if _highway(piece, j) == hw) / n
        if share >= 0.5:
            best = max(best, win)
    return best


def _road_width(piece: Piece, seg: int) -> float:
    """Geschätzte Fahrbahnbreite (Spielmeter) – passend zu mapping.jsonc des Writers."""
    edge = piece.chain.parts[piece.dense.seg_part[seg]].edge
    hw = edge.highway
    tags = edge.way.tags
    oneway = tags.get("oneway") in ("yes", "1", "true") or hw in ("motorway", "motorway_link")
    if hw in ("motorway", "trunk") and oneway:
        try:
            return 14.5 if int(str(tags.get("lanes", "2")).split(";")[0]) >= 3 else 11.0
        except ValueError:
            return 11.0
    if hw.endswith("_link") or oneway:
        return 6.5
    return 8.0


TWIN_HIGHWAYS = ("motorway", "trunk")


def align_partner_points(pieces: list[Piece], scale_h: float, max_gap: float = 25.0,
                         snap: float = 2.0, margin: float = 1.0) -> int:
    """Setzt auf nebeneinanderliegende Straßen Punkte genau gegenüber.

    Die Höhenbindung zwischen Richtungsfahrbahnen (`twin_ties`) und sich überdeckenden
    Fahrbahnen (`overlap_ties`) verbindet je Punkt den nächsten Punkt der anderen
    Straße. Liegen die verdichteten Punkte beider Straßen versetzt (z. B. alle 19 m
    gegen alle 16 m), wird ein Punkt an einen bis zu halben Punktabstand entfernten
    Partner gebunden und übernimmt dessen Höhe: Bei Gefälle entsteht eine Treppe
    (abwechselnd ebene und steile Abschnitte, bis ~0,5 m daneben). Das Spiel zieht
    die Fahrbahn als Hermite-Kurve durch diese Treppe; in den Mulden liegen Gelände
    und Füllgelände dann über dem Asphalt.

    Hier wird für jeden Punkt der Lotfußpunkt auf der Partnerstraße eingefügt (wenn
    dort nicht schon ein Punkt liegt), so dass die Bindung quer zur Fahrbahn wirkt.
    Gibt die Zahl der eingefügten Punkte zurück.
    """
    from scipy.spatial import cKDTree

    pos, owner, local = [], [], []
    for pi, p in enumerate(pieces):
        d = p.dense
        if len(d.xy) < 2:
            continue
        for j in range(len(d.xy)):
            pos.append(d.xy_game[j]); owner.append(pi); local.append(j)
    if not pos:
        return 0
    pos = np.asarray(pos, float)
    owner = np.asarray(owner)
    local = np.asarray(local)

    def tangent(p: Piece, j: int) -> np.ndarray:
        g = p.dense.xy_game
        t = g[min(j + 1, len(g) - 1)] - g[max(j - 1, 0)]
        n = float(np.hypot(*t))
        return t / n if n > 1e-9 else np.array([1.0, 0.0])

    tree = cKDTree(pos)
    # je (Punkt, andere Straße) der nächste Lotfußpunkt: (Abstand, Segment, t)
    best: dict[tuple[int, int], tuple[float, int, float]] = {}
    for i, k in tree.query_pairs(r=max_gap):
        if owner[i] == owner[k]:
            continue
        for a, b in ((i, k), (k, i)):
            pa, pb = pieces[owner[a]], pieces[owner[b]]
            g = pb.dense.xy_game
            m = len(g)
            P = pos[a]
            for s in (local[b] - 1, local[b]):
                if s < 0 or s >= m - 1:
                    continue
                A, B = g[s], g[s + 1]
                AB = B - A
                L2 = float(AB @ AB)
                if L2 < 1e-12:
                    continue
                t = min(max(float((P - A) @ AB) / L2, 0.0), 1.0)
                dist = float(np.hypot(*(P - (A + t * AB))))
                key = (int(a), int(owner[b]))
                if dist < best.get(key, (1e18, 0, 0.0))[0]:
                    best[key] = (dist, int(s), t)

    inserts: dict[int, list[tuple[int, float]]] = {}
    for (a, pb_i), (dist, s, t) in best.items():
        pa, pb = pieces[owner[a]], pieces[pb_i]
        ja = int(local[a])
        na = len(pa.dense.xy)
        g = pb.dense.xy_game
        seg_vec = g[s + 1] - g[s]
        seg_len = float(np.hypot(*seg_vec))
        if seg_len < 1e-9:
            continue
        cos = abs(float(tangent(pa, ja) @ (seg_vec / seg_len)))
        sa = min(ja, na - 2)
        twin = (_highway(pa, sa) in TWIN_HIGHWAYS and _highway(pb, s) in TWIN_HIGHWAYS
                and cos >= 0.95 and dist <= max_gap)
        wa = max(_road_width(pa, sa), _road_width(pa, max(ja - 1, 0)))
        overlap = cos >= 0.9 and dist < (wa + _road_width(pb, s)) / 2 + margin
        if not (twin or overlap):
            continue
        if t * seg_len < snap or (1 - t) * seg_len < snap:
            continue   # dort liegt schon ein Punkt
        inserts.setdefault(pb_i, []).append((s, t))

    added = 0
    for pi, lst in inserts.items():
        added += _insert_dense(pieces[pi].dense, lst, scale_h, snap)
    return added


def _insert_dense(d, inserts: list[tuple[int, float]], scale_h: float, snap: float) -> int:
    """Fügt Punkte (Segment, t) in eine verdichtete Linie ein (Abstand ≥ snap Spielmeter)."""
    by_seg: dict[int, list[float]] = {}
    for s, t in inserts:
        by_seg.setdefault(s, []).append(t)
    xy, ids, parts = [d.xy[0]], [d.node_ids[0]], []
    added = 0
    for s in range(len(d.xy) - 1):
        a, b = d.xy[s], d.xy[s + 1]
        seg_len = float(np.hypot(*(d.xy_game[s + 1] - d.xy_game[s])))
        last = 0.0
        for t in sorted(by_seg.get(s, ())):
            if (t - last) * seg_len < snap or (1 - t) * seg_len < snap:
                continue
            xy.append(a + t * (b - a)); ids.append(NO_NODE); parts.append(d.seg_part[s])
            last = t
            added += 1
        xy.append(b); ids.append(d.node_ids[s + 1]); parts.append(d.seg_part[s])
    if added:
        d.xy = np.asarray(xy, float)
        d.node_ids = np.asarray(ids, np.int64)
        d.seg_part = np.asarray(parts, np.int64)
        d.s = np.concatenate([[0.0], np.cumsum(np.hypot(*np.diff(d.xy, axis=0).T))])
        d.set_scale(scale_h)
    return added


def overlap_ties(pieces, offsets, canon, movable: np.ndarray, margin: float = 1.0):
    """Paare von Punkten verschiedener Straßen, deren Fahrbahnen sich überdecken.
    Ergebnis: (a, b, Gewicht) mit kleinem Gewicht – die Steigungsgrenze hält sie
    dann praktisch auf gleicher Höhe."""
    from scipy.spatial import cKDTree

    pos, width, owner, vid, kind = [], [], [], [], []
    for pi, (p, off) in enumerate(zip(pieces, offsets)):
        d = p.dense
        n = len(d.xy)
        for j in range(n):
            c = int(canon[off + j])
            if not movable[c]:
                continue
            w = max(_road_width(p, min(j, n - 2)), _road_width(p, max(j - 1, 0)))
            pos.append(d.xy_game[j]); width.append(w); owner.append(pi); vid.append(c)
            seg = min(j, n - 2)
            kind.append(2 if p.chain.parts[d.seg_part[seg]].edge.role == "visible"
                        else 1 if _highway(p, seg) in ("motorway", "trunk") else 0)
    if not pos:
        return []
    pos = np.asarray(pos); width = np.asarray(width)
    tree = cKDTree(pos)
    ties = {}
    for i, j in tree.query_pairs(r=float(width.max()) + margin):
        if owner[i] == owner[j] or vid[i] == vid[j]:
            continue
        # Sichtstück unter/neben einer Autobahn zieht deren Fahrbahn nicht herunter (A 70 vor der
        # Brücke über die St 2183: Weg schräg unter dem Brückenende, Fahrbahn sank 2 m ab)
        if {kind[i], kind[j]} == {1, 2}:
            continue
        dist = float(np.linalg.norm(pos[i] - pos[j]))
        need = (width[i] + width[j]) / 2 + margin
        if dist >= need:
            continue
        key = (min(vid[i], vid[j]), max(vid[i], vid[j]))
        ties[key] = min(ties.get(key, 1e9), 0.5)
    return [(a, b, w) for (a, b), w in ties.items()]


def twin_ties(pieces, offsets, canon, movable, max_gap: float = 25.0):
    """Richtungsfahrbahnen (Autobahn/Kraftfahrstraße, Einbahn) eng nebeneinander und etwa
    parallel: auf gleiche Höhe binden – sonst entstehen im Mittelstreifen Stufen."""
    from scipy.spatial import cKDTree

    pos, tang, owner, vid = [], [], [], []
    for pi, (p, off) in enumerate(zip(pieces, offsets)):
        d = p.dense
        n = len(d.xy)
        if n < 2:
            continue
        for j in range(n):
            seg = min(j, n - 2)
            if _highway(p, seg) not in ("motorway", "trunk"):
                continue
            c = int(canon[off + j])
            if not movable[c]:
                continue
            a, b = d.xy_game[max(j - 1, 0)], d.xy_game[min(j + 1, n - 1)]
            t = np.asarray(b, float) - np.asarray(a, float)
            nt = np.linalg.norm(t)
            if nt < 1e-6:
                continue
            pos.append(d.xy_game[j]); tang.append(t / nt); owner.append(pi); vid.append(c)
    if not pos:
        return []
    pos = np.asarray(pos, float); tang = np.asarray(tang)
    tree = cKDTree(pos)
    best: dict = {}
    for i, j in tree.query_pairs(r=max_gap):
        if owner[i] == owner[j] or vid[i] == vid[j]:
            continue
        if abs(float(tang[i] @ tang[j])) < 0.95:
            continue
        d = float(np.linalg.norm(pos[i] - pos[j]))
        # je Punkt nur der nächste Partner
        for x, y in ((i, j), (j, i)):
            if d < best.get(x, (1e9, -1))[0]:
                best[x] = (d, y)
    ties = {}
    for x, (d, y) in best.items():
        key = (min(vid[x], vid[y]), max(vid[x], vid[y]))
        ties[key] = 0.5
    return [(a, b, w) for (a, b), w in ties.items()]


def junction_plateaus(pieces, offsets, canon, adj, degree, movable, radius: float = 25.0):
    """Ebenerdige Kreuzungen normaler Straßen bekommen eine waagerechte Fläche
    (Radius ≈ Kreuzungs-Prefab), weil die Prefabs waagerecht eingesetzt werden."""
    motorway = set()
    for p, off in zip(pieces, offsets):
        for j, part_idx in enumerate(p.dense.seg_part):
            edge = p.chain.parts[part_idx].edge
            if edge.highway in ("motorway", "trunk") or edge.highway.endswith("motorway_link"):
                motorway.add(int(canon[off + j])); motorway.add(int(canon[off + j + 1]))
    ties = []
    for v, deg in degree.items():
        if deg < 3 or v in motorway or not movable[v]:
            continue
        dist = {v: 0.0}
        stack = [v]
        while stack:
            a = stack.pop()
            for b, w in adj.get(a, ()):
                nd = dist[a] + w
                if nd <= radius and nd < dist.get(b, 1e9):
                    dist[b] = nd
                    stack.append(b)
        ties += [(v, u, 0.5) for u in dist if u != v and movable[u]]
    return ties


def lipschitz_envelope(z: np.ndarray, adj: dict, grade: float) -> np.ndarray:
    """Mittel aus oberer und unterer Steigungs-Hülle (beide höchstens grade steil,
    also auch ihr Mittel). Dijkstra auf dem Punktgraphen."""
    import heapq

    def envelope(sign: float) -> np.ndarray:
        # sign=+1: obere Hülle min_j(z_j + g·d), sign=-1: untere max_j(z_j - g·d)
        val = sign * z.copy()  # minimieren in transformierter Form
        heap = [(v, i) for i, v in enumerate(val)]
        heapq.heapify(heap)
        done = np.zeros(len(z), bool)
        while heap:
            v, i = heapq.heappop(heap)
            if done[i] or v > val[i]:
                continue
            done[i] = True
            for j, w in adj.get(i, ()):
                nv = v + grade * w
                if nv < val[j]:
                    val[j] = nv
                    heapq.heappush(heap, (nv, j))
        return sign * val

    return (envelope(1.0) + envelope(-1.0)) / 2


def pin_ends(s: np.ndarray, h: np.ndarray, start: float, end: float) -> np.ndarray:
    """Verschiebt das Profil linear so, dass es an den Enden die Pin-Werte trifft."""
    if s[-1] <= 0:
        return h.copy()
    t = s / s[-1]
    return h + (start - h[0]) * (1 - t) + (end - h[-1]) * t


def _endpoint_key(piece: Piece, at_start: bool):
    nid = piece.dense.node_ids[0 if at_start else -1]
    cut = piece.start_cut if at_start else piece.end_cut
    if nid != NO_NODE and not cut:
        return ("n", int(nid))
    return ("c", piece.key, at_start)


def _propagate(values: np.ndarray, adj: dict, seeds, grade: float) -> None:
    """Max-Ausbreitung: values(u) >= values(v) - grade·Abstand. Arbeitet in
    place und startet nur von `seeds` (Indizes mit erhöhtem Wert)."""
    heap = [(-float(values[v]), int(v)) for v in seeds if values[v] > 0]
    heapq.heapify(heap)
    while heap:
        negv, v = heapq.heappop(heap)
        val = -negv
        if val < values[v]:
            continue
        for u, w in adj.get(v, ()):
            nv = val - grade * w
            if nv > 0 and nv > values[u]:
                values[u] = nv
                heapq.heappush(heap, (-nv, u))


class _Structure:
    """Zusammenhängendes Bauwerk (Brücke oder Tunnel) im Punktgraphen."""

    MAX_INTERIOR = 4000  # darüber wird nicht interpoliert (Speicher)

    def __init__(self, vertices: np.ndarray, edges: list[int], length: float):
        self.vertices = vertices
        self.edges = edges
        self.length = length
        self.abutments = np.empty(0, np.int64)
        self.interior = np.empty(0, np.int64)
        self.operator: Optional[np.ndarray] = None

    def prepare(self, ea, eb, elen, degree, in_structure_edges: set[int], incident) -> None:
        verts = set(self.vertices.tolist())
        # Widerlager/Portal: Punkt mit einer Kante außerhalb des Bauwerks.
        # Enden mitten im Bauwerk (Zuschnitt, abgeschnittene Daten) sind keine
        # Widerlager – sie übernehmen die Höhe ihres Nachbarn (Neumann-Rand).
        # Nur wenn es gar kein echtes Widerlager gibt, dienen die Enden als Rand.
        abut = [v for v in self.vertices if any(e not in in_structure_edges for e in incident[v])]
        if not abut:
            abut = [v for v in self.vertices if degree[v] <= 1]
        self.abutments = np.array(sorted(abut), np.int64)
        self.interior = np.array(sorted(verts - set(abut)), np.int64)
        if not len(self.abutments) or not len(self.interior) or len(self.interior) > self.MAX_INTERIOR:
            self.operator = None
            return
        ii = {int(v): k for k, v in enumerate(self.interior)}
        ai = {int(v): k for k, v in enumerate(self.abutments)}
        n, m = len(self.interior), len(self.abutments)
        A = np.zeros((n, n))
        B = np.zeros((n, m))
        for e in self.edges:
            a, b = int(ea[e]), int(eb[e])
            w = 1.0 / max(float(elen[e]), 1e-6)
            for x, y in ((a, b), (b, a)):
                if x in ii:
                    A[ii[x], ii[x]] += w
                    if y in ii:
                        A[ii[x], ii[y]] -= w
                    else:
                        B[ii[x], ai[y]] -= w
        try:
            self.operator = np.linalg.solve(A, -B)  # H_innen = operator @ H_widerlager
        except np.linalg.LinAlgError:
            self.operator = None

    def interpolate(self, z: np.ndarray) -> Optional[np.ndarray]:
        if self.operator is None:
            return None
        return self.operator @ z[self.abutments]


def _components(edge_ids: list[int], ea, eb, elen) -> list[_Structure]:
    parent: dict[int, int] = {}

    def find(x):
        while parent.setdefault(x, x) != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for e in edge_ids:
        ra, rb = find(int(ea[e])), find(int(eb[e]))
        if ra != rb:
            parent[ra] = rb
    groups: dict[int, list[int]] = {}
    for e in edge_ids:
        groups.setdefault(find(int(ea[e])), []).append(e)
    out = []
    for edges in groups.values():
        verts = np.unique(np.concatenate([ea[edges], eb[edges]]))
        out.append(_Structure(verts, edges, float(elen[edges].sum())))
    return out


def apply_elevation(pieces: list[Piece], profile, dem: Optional[DemSampler],
                    max_iterations: int = 8) -> dict:
    """Setzt piece.dense.z (Spielmeter) für alle Stücke. Gibt Kennzahlen zurück.

    Brückenhöhe und Rampensteigung gelten im Spiel (Fahrzeuge sind 1:1),
    das Gelände wird mit dem Höhenmaßstab umgerechnet.
    """
    from .qa import find_crossings

    # --- Punktgraph: gemeinsame Knoten zusammenlegen
    offsets: list[int] = []
    total = 0
    for p in pieces:
        offsets.append(total)
        total += len(p.dense.xy)
    canon = np.arange(total)
    first_by_key: dict = {}
    for p, off in zip(pieces, offsets):
        for at_start, local in ((True, 0), (False, len(p.dense.xy) - 1)):
            key = _endpoint_key(p, at_start)
            vid = off + local
            if key in first_by_key:
                canon[vid] = first_by_key[key]
            else:
                first_by_key[key] = vid

    # --- 1. Gelände (Spielmeter)
    # Copernicus & Co. sind Oberflächenmodelle (Bäume, Gebäude): erst ein
    # bodennahes Perzentil, dann glätten. Enden auf den Mittelwert aller an
    # einem Knoten treffenden Straßen setzen (nicht auf Rohwerte – sonst Spitzen).
    base = np.zeros(total)
    smoothed = []
    end_sum: dict[int, float] = {}
    end_cnt: dict[int, int] = {}
    for p, off in zip(pieces, offsets):
        d = p.dense
        if dem is None:
            smoothed.append(None)
            continue
        raw = dem.sample(d.xy)
        win = _piece_window(p, profile.smoothing_window_m)
        ground = low_percentile_along(d.s, raw, profile.smoothing_window_m / 2, 25.0)
        # unter Brücken liegt das Tal: dort für die Glättung linear zwischen den
        # Brückenenden überbrücken, sonst zieht das breite Fenster die Enden ins Tal
        fl = _segment_flags(p)
        under = np.zeros(len(ground), bool)
        for j, f in enumerate(fl):
            if f[0]:
                under[j] = under[j + 1] = True
        if under.any() and (~under).sum() >= 2:
            inner = under.copy()
            for j, f in enumerate(fl):   # Brückenenden selbst behalten
                if f[0] and (j == 0 or not fl[j - 1][0]):
                    inner[j] = False
                if f[0] and (j + 1 == len(fl) or not fl[j + 1][0]):
                    inner[j + 1] = False
            if inner.any():
                # Perzentil nur über Punkte außerhalb der Brücken: sonst zog das Fenster (± 30 m)
                # das Tal unter der Brücke in die Zufahrten – die A 70 sank vor ihrer Brücke über
                # die St 2183 um gut 2 m ab und stieg auf der Brücke wieder an
                keep = ~inner
                g2 = low_percentile_along(d.s[keep], raw[keep], profile.smoothing_window_m / 2, 25.0)
                ground = np.interp(d.s, d.s[keep], g2)
        sm = smooth_along(d.s, ground, win)

        smoothed.append(sm)
        for idx in (0, len(d.xy) - 1):
            c = int(canon[off + idx])
            end_sum[c] = end_sum.get(c, 0.0) + float(sm[idx])
            end_cnt[c] = end_cnt.get(c, 0) + 1
    for p, off, sm in zip(pieces, offsets, smoothed):
        if sm is None:
            continue
        d = p.dense
        c0, c1 = int(canon[off]), int(canon[off + len(d.xy) - 1])
        pinned = pin_ends(d.s, sm, end_sum[c0] / end_cnt[c0], end_sum[c1] / end_cnt[c1])
        base[canon[off:off + len(d.xy)]] = pinned / profile.scale_v

    # --- Kanten
    ea_l, eb_l, elen_l, flags_l, egrade_l = [], [], [], [], []
    flags_by_piece = []
    for p, off in zip(pieces, offsets):
        flags = _segment_flags(p)
        flags_by_piece.append(flags)
        seg_len = np.diff(p.dense.s_game)
        for j in range(len(p.dense.xy) - 1):
            ea_l.append(int(canon[off + j]))
            eb_l.append(int(canon[off + j + 1]))
            elen_l.append(float(seg_len[j]))
            flags_l.append(flags[j])
            egrade_l.append(min(GRADE_BY_HIGHWAY.get(_highway(p, j), profile.terrain_max_grade),
                                profile.terrain_max_grade))
    ea = np.array(ea_l, np.int64)
    eb = np.array(eb_l, np.int64)
    elen = np.array(elen_l, float)
    adj: dict[int, list[tuple[int, float]]] = {}
    incident: dict[int, list[int]] = {}
    for e, (a, b, w) in enumerate(zip(ea_l, eb_l, elen_l)):
        adj.setdefault(a, []).append((b, w))
        adj.setdefault(b, []).append((a, w))
        incident.setdefault(a, []).append(e)
        incident.setdefault(b, []).append(e)
    degree = {v: len(es) for v, es in incident.items()}
    if dem is not None:
        # Kanten in Bauwerken nicht begrenzen – dort liegt das Tal unter der Brücke
        keep = [e for e, f in enumerate(flags_l) if not (f[0] or f[1])]
        clamp_grade(base, [ea_l[e] for e in keep], [eb_l[e] for e in keep], [elen_l[e] for e in keep],
                    np.array([egrade_l[e] for e in keep]), iterations=800)

    # --- Bauwerke
    bridge_edges = [e for e, f in enumerate(flags_l) if f[0]]
    tunnel_edges = [e for e, f in enumerate(flags_l) if f[1]]
    min_bridge_game = profile.min_bridge_length_m / profile.scale_h
    bridges_all = _components(bridge_edges, ea, eb, elen)
    bridges = [b for b in bridges_all if b.length >= min_bridge_game]
    tunnels = _components(tunnel_edges, ea, eb, elen)
    for group in (bridges, tunnels):
        for st in group:
            st.prepare(ea, eb, elen, degree, set(st.edges), incident)

    # --- 2. Brücken anheben
    clearance = profile.bridge_clearance_m
    grade = profile.max_ramp_grade
    max_offset = clearance * profile.max_bridge_levels
    lift = np.zeros(total)
    sink = np.zeros(total)
    # Autobahn-/Kraftfahrstraßen-Brücken nicht pauschal anheben: dort wird stattdessen
    # die untere Straße abgesenkt (siehe höhenfreie Kreuzungen)
    major_edge = []
    for p in pieces:
        for j in range(len(p.dense.xy) - 1):
            major_edge.append(_highway(p, j) in MAJOR)
    # Kreuzt eine Brücke eine Straße des Netzes, ergibt sich ihre Höhe aus deren Höhe plus
    # Lichtraum (Schritt 4) – kein pauschales Anheben. Pauschal (+Lichtraum über die Linie
    # zwischen den Widerlagern) lag z. B. die BT 46 über der A 9 gut 4 m höher als nötig,
    # samt 120 m langen Dammrampen und Erdplateau neben der Anschlussrampe.
    crossings = find_crossings([p.dense.xy_game for p in pieces])
    eoff = np.cumsum([0] + [len(p.dense.xy) - 1 for p in pieces])
    crossed = {int(eoff[la] + sa) for la, sa, _, _, _, _ in crossings} | \
              {int(eoff[lb] + sb) for _, _, _, lb, sb, _ in crossings}
    blanket_skipped = 0
    for st in bridges:
        if all(major_edge[e] for e in st.edges):
            continue
        if any(e in crossed for e in st.edges):
            blanket_skipped += 1
            continue
        for e in st.edges:
            value = min(clearance * max(flags_l[e][2], 1), max_offset)
            lift[ea[e]] = max(lift[ea[e]], value)
            lift[eb[e]] = max(lift[eb[e]], value)
    _propagate(lift, adj, np.flatnonzero(lift > 0), grade)

    def heights() -> np.ndarray:
        z = base + lift - sink
        for st in bridges:
            h = st.interpolate(z)
            if h is not None:
                z[st.interior] = np.maximum(z[st.interior], h)
        for st in tunnels:
            h = st.interpolate(z)
            if h is not None:
                z[st.interior] = np.minimum(z[st.interior], h)
        return z

    # --- 4. höhenfreie Kreuzungen
    margin = CLEARANCE_MARGIN_M if dem is not None else 0.0   # nur mit Hülle/Steigungsgrenze danach
    raised = sunk = 0
    sag_filled = 0
    maxheight_used = 0
    iterations = 0
    mh_crossings: set[int] = set()

    def shortfalls(z: np.ndarray, extra: float, sides: Optional[list] = None):
        """Je Kreuzung mit zu wenig Lichtraum: (Punkte, Fehlbetrag, absenken?).
        `sides` sammelt für jede Kreuzung (Punkte oben, Punkte unten)."""
        out = []
        for k, (la, sa, t, lb, sb, u) in enumerate(crossings):
            a0, a1 = int(canon[offsets[la] + sa]), int(canon[offsets[la] + sa + 1])
            b0, b1 = int(canon[offsets[lb] + sb]), int(canon[offsets[lb] + sb + 1])
            ha = z[a0] + t * (z[a1] - z[a0])
            hb = z[b0] + u * (z[b1] - z[b0])
            fa, fb = flags_by_piece[la][sa], flags_by_piece[lb][sb]
            ra = (_layer_eff(fa), fa[0], round(float(ha), 2), _is_link(pieces[la], sa))
            rb = (_layer_eff(fb), fb[0], round(float(hb), 2), _is_link(pieces[lb], sb))
            if ra >= rb:
                up, low, h_up, h_low, f_low = (a0, a1), (b0, b1), ha, hb, fb
            else:
                up, low, h_up, h_low, f_low = (b0, b1), (a0, a1), hb, ha, fa
            if sides is not None:
                sides.append((up, low))
            # eingetragene Durchfahrtshöhe (untere Straße, sonst Brücke) + Bauwerksdicke
            mh = _maxheight(pieces[la], sa) if ra < rb else _maxheight(pieces[lb], sb)
            if mh is None:
                mh = _maxheight(pieces[lb], sb) if ra < rb else _maxheight(pieces[la], sa)
            need = (mh + DECK_THICKNESS_M if mh is not None else clearance) + extra
            if mh is not None:
                mh_crossings.add(k)
            deficit = h_low + need - h_up
            if deficit <= 1e-6:
                continue
            hw_up = _highway(pieces[la if ra >= rb else lb], sa if ra >= rb else sb)
            hw_low = _highway(pieces[lb if ra >= rb else la], sb if ra >= rb else sa)
            # Tunnel oder untergeordnete Straße unter Autobahn: unten absenken, sonst oben anheben
            lower = f_low[1] or (hw_up in MAJOR and hw_low not in MAJOR)
            out.append((low if lower else up, deficit, lower))
        return out

    for iterations in range(1, max_iterations + 1):
        z = heights()
        lift_seeds, sink_seeds = [], []
        # Stand zu Beginn des Durchgangs: mehrere Kreuzungen an denselben Punkten (Brücke über
        # beide Richtungsfahrbahnen) heben auf das Maximum ihrer Fehlbeträge, nicht auf die Summe
        lift0, sink0 = lift.copy(), sink.copy()
        for target, deficit, lower in shortfalls(z, margin):
            values, before, seeds = (sink, sink0, sink_seeds) if lower else (lift, lift0, lift_seeds)
            for vid in target:
                new = min(before[vid] + deficit, max_offset)
                if new > values[vid] + 1e-6:
                    values[vid] = new
                    seeds.append(vid)
        if not lift_seeds and not sink_seeds:
            break
        raised += len(set(lift_seeds))
        sunk += len(set(sink_seeds))
        _propagate(lift, adj, lift_seeds, grade)
        _propagate(sink, adj, sink_seeds, grade)
    maxheight_used = len(mh_crossings)

    z = heights()
    # Autobahnen und Kraftfahrstraßen: keine Mulden. Das Geländemodell sinkt vor Brücken über ein
    # Tal oft ab; die Fahrbahn folgte (A 70 vor der Brücke über die St 2183: 1,5 m runter, auf der
    # Brücke 5 m hoch). Höhe je Stück höchstens auf die obere Hülle eines langen Mittels anheben.
    if dem is not None:
        for p, off in zip(pieces, offsets):
            n = len(p.dense.xy)
            if n < 3 or sum(_highway(p, j) in ("motorway", "trunk") for j in range(n - 1)) < 0.5 * (n - 1):
                continue
            idx = canon[off:off + n]
            zz = z[idx]
            sm = smooth_along(p.dense.s_game, zz, SAG_WINDOW_M)
            up = np.clip(sm - zz, 0.0, SAG_MAX_RAISE_M)
            if up.max() > 0.2:
                z[idx] = zz + up
                sag_filled += int((up > 0.2).sum())
    clearance_repaired = 0
    if dem is not None:
        # Brückenrampen und Gelände zusammen dürfen nicht steiler werden als erlaubt;
        # Bauwerke selbst bleiben, wie sie sind.
        fixed = np.zeros(total, bool)
        for e, f in enumerate(flags_l):
            if f[0] or f[1]:
                fixed[ea[e]] = fixed[eb[e]] = True
        g = max(profile.terrain_max_grade, grade)
        # Überlagerungsregel: Fahrbahnen, die sich in der Draufsicht überdecken, müssen
        # gleich hoch liegen – außer eine davon ist Brücke/Tunnel (oder wurde an einer
        # höhenfreien Kreuzung angehoben). Dafür kurze Hilfskanten zwischen den Punkten.
        movable = ~fixed & (lift < 0.1) & (sink < 0.1)
        ties = overlap_ties(pieces, offsets, canon, movable)
        twins = twin_ties(pieces, offsets, canon, movable)
        ties += twins
        ties += junction_plateaus(pieces, offsets, canon, adj, degree, movable)
        # Kantenlänge so skalieren, dass die Hülle je Kante deren eigene Grenze einhält
        adj2: dict = {}
        for e, (a, b, w) in enumerate(zip(ea_l, eb_l, elen_l)):
            ge = g if (flags_l[e][0] or flags_l[e][1]) else egrade_l[e]
            w2 = w * ge / g
            adj2.setdefault(a, []).append((b, w2))
            adj2.setdefault(b, []).append((a, w2))
        for a, b, w in ties:
            adj2.setdefault(a, []).append((b, w))
            adj2.setdefault(b, []).append((a, w))
        before = sum(1 for a, b, _ in ties if abs(z[a] - z[b]) > 0.3)
        env = lipschitz_envelope(z, adj2, g)
        # Bauwerke nur festhalten, wo die Hülle sie absenken würde (Lichtraum bleibt);
        # anheben darf sie sie, damit kurze Brücken nicht steiler sind als ihre Rampen.
        # Ebenso an Kreuzungen angehobene Strecken (nicht absenken) und abgesenkte (nicht
        # wieder anheben) – sonst glättete die Hülle den Lichtraum zum Teil wieder weg.
        up_only = fixed | (lift > 0.05)
        down_only = ~up_only & (sink > 0.05)
        z = np.where(up_only, np.maximum(z, env), np.where(down_only, np.minimum(z, env), env))
        eg = [g if (f[0] or f[1]) else ge for f, ge in zip(flags_l, egrade_l)]
        clamp_grade(z, ea_l + [t[0] for t in ties], eb_l + [t[1] for t in ties],
                    elen_l + [t[2] for t in ties], np.array(eg + [g] * len(ties)), iterations=800)
        overlap_stats = (len(ties), before, sum(1 for a, b, _ in ties if abs(z[a] - z[b]) > 0.3))
        # Hülle und Steigungsgrenze verschieben auch Punkte an Kreuzungen (eine abgesenkte
        # Straße unter einer Autobahnbrücke hob die Hülle bis 0,75 m wieder an): Lichtraum
        # nachprüfen und Fehlbetrag an den Kreuzungspunkten ausgleichen, während die
        # Steigungsgrenze den Rest der Straßen nachzieht. Kreuzungspunkte bewegen sich dabei
        # nur vom anderen Weg weg – die untere Straße nur nach unten, die obere nur nach oben.
        # (Ganz festgehalten blieben Kanten zwischen zwei Kreuzungspunkten zu steil: 6,95 %
        # unter der A 70.)
        sides: list = []
        shortfalls(z, 0.0, sides)
        rise = np.zeros(total, bool)
        fall = np.zeros(total, bool)
        for up, low in sides:
            rise[list(up)] = True
            fall[list(low)] = True
        crit_only = rise.astype(np.int8) - fall.astype(np.int8)
        crit_fixed = rise & fall
        for _ in range(4):
            fix = np.zeros(total)
            for target, deficit, lower in shortfalls(z, 0.0):
                for vid in target:
                    d = -deficit if lower else deficit
                    if abs(d) > abs(fix[vid]):
                        fix[vid] = d
            if not fix.any():
                break
            clearance_repaired += int(np.count_nonzero(fix))
            z = z + fix
            clamp_grade(z, ea_l + [t[0] for t in ties], eb_l + [t[1] for t in ties],
                        elen_l + [t[2] for t in ties], np.array(eg + [g] * len(ties)),
                        iterations=800, fixed=crit_fixed, only=crit_only)
    else:
        overlap_stats = (0, 0, 0)
    for p, off in zip(pieces, offsets):
        p.dense.z = z[canon[off:off + len(p.dense.xy)]]

    return {
        "dem": dem.path if dem else None,
        "dem_missing_samples": dem.missing if dem else 0,
        "bridges": len(bridges),
        "bridges_ignored_short": len(bridges_all) - len(bridges),
        "bridges_over_network": blanket_skipped,
        "tunnels": len(tunnels),
        "structures_not_interpolated": sum(1 for st in bridges + tunnels
                                           if st.operator is None and len(st.interior)),
        "crossings_dense": len(crossings),
        "crossing_raised_points": raised,
        "crossing_maxheight_used": maxheight_used,
        "crossing_sunk_points": sunk,
        "crossing_repaired_points": clearance_repaired,
        "crossing_iterations": iterations,
        "sag_filled_points": sag_filled,
        "overlap_ties": overlap_stats[0],
        "twin_ties": len(twins) if dem is not None else 0,
        "overlap_steps_before": overlap_stats[1],
        "overlap_steps_after": overlap_stats[2],
        "max_lift_m": float(lift.max()) if total else 0.0,
        "max_sink_m": float(sink.max()) if total else 0.0,
    }


def _layer_eff(flags: tuple[bool, bool, int]) -> int:
    bridge, tunnel, layer = flags
    if layer != 0:
        return layer
    return 1 if bridge else (-1 if tunnel else 0)


def _is_link(piece: Piece, seg: int) -> bool:
    part = piece.chain.parts[piece.dense.seg_part[seg]]
    return part.edge.highway.endswith("_link")


def _segment_flags(p: Piece) -> list[tuple[bool, bool, int]]:
    cache = {}
    out = []
    for part_idx in p.dense.seg_part:
        if part_idx not in cache:
            tags = p.chain.parts[part_idx].edge.way.tags
            cache[part_idx] = (is_bridge(tags), is_tunnel(tags), parse_layer(tags))
        out.append(cache[part_idx])
    return out
