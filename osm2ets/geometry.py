# SPDX-License-Identifier: GPL-3.0-or-later
"""Geometrie: Projektion, Zuschnitt, Verdichtung, Vereinfachung, Segmentgrenzen."""
from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Optional

import numpy as np
from pyproj import Transformer

from .config import GAME_MIN_SEGMENT_M
from .network import Chain, is_bridge, is_tunnel, parse_lanes, parse_layer

NO_NODE = -(2 ** 63)  # Platzhalter für berechnete Punkte (kollidiert nicht mit OSM-IDs)


class Projector:
    """WGS84 (lon/lat) <-> projizierte Meter relativ zum Ursprung."""

    def __init__(self, crs: str, origin_lonlat: tuple[float, float]):
        self.crs = crs
        self._fwd = Transformer.from_crs("EPSG:4326", crs, always_xy=True)
        self._inv = Transformer.from_crs(crs, "EPSG:4326", always_xy=True)
        e0, n0 = self._fwd.transform(origin_lonlat[0], origin_lonlat[1])
        if not (math.isfinite(e0) and math.isfinite(n0)):
            raise ValueError(f"Ursprung {origin_lonlat} lässt sich nicht nach {crs} projizieren")
        self.origin = (float(e0), float(n0))

    def forward(self, lon: np.ndarray, lat: np.ndarray) -> np.ndarray:
        e, n = self._fwd.transform(np.asarray(lon, float), np.asarray(lat, float))
        return np.column_stack([np.asarray(e) - self.origin[0], np.asarray(n) - self.origin[1]])

    def inverse(self, xy: np.ndarray) -> np.ndarray:
        lon, lat = self._inv.transform(xy[:, 0] + self.origin[0], xy[:, 1] + self.origin[1])
        return np.column_stack([lon, lat])

    def absolute(self, xy: np.ndarray) -> np.ndarray:
        """Relative Meter -> absolute Koordinaten im Ziel-KBS (für DEM-Abfragen)."""
        return xy + np.asarray(self.origin)


# --------------------------------------------------------------------------- Läufe


def run_key(part, role: str) -> tuple:
    """Attribute, nach denen ein Straßenstück im Writer unterschieden wird."""
    tags = part.edge.way.tags
    return (
        part.edge.highway,
        role,
        part.direction != 0,
        parse_lanes(tags),
        is_bridge(tags),
        is_tunnel(tags),
        parse_layer(tags),
        tags.get("ref"),
        tags.get("maxspeed"),
        tags.get("service"),
    )


RUN_FIELDS = ("highway", "role", "oneway", "lanes", "bridge", "tunnel", "layer", "ref", "maxspeed", "service")


# --------------------------------------------------------------------------- Stücke


@dataclass
class Piece:
    """Zusammenhängendes, vollständig vorhandenes Stück einer Kette."""
    chain: Chain
    index: int
    lonlat: np.ndarray            # (N, 2)
    node_ids: np.ndarray          # (N,) int64, NO_NODE für berechnete Punkte
    seg_part: np.ndarray          # (N-1,) Index in chain.parts
    start_cut: bool = False
    end_cut: bool = False
    # später gesetzt
    xy: Optional[np.ndarray] = None
    dense: Optional["Dense"] = None
    result: Optional["PieceResult"] = None

    @property
    def key(self) -> str:
        return f"c{self.chain.id}p{self.index}"


def _segment_parts(chain: Chain) -> np.ndarray:
    out = []
    for i, part in enumerate(chain.parts):
        out.extend([i] * (part.edge.i1 - part.edge.i0))
    return np.asarray(out, dtype=np.int64)


def chain_pieces(chain: Chain, locations: dict) -> list[Piece]:
    """Zerlegt eine Kette an fehlenden Knotenkoordinaten in Stücke."""
    ids = chain.node_ids()
    seg_part = _segment_parts(chain)
    assert len(seg_part) == len(ids) - 1
    pieces: list[Piece] = []
    cur: list[int] = []

    def flush(end_cut: bool, start_cut: bool):
        if len(cur) >= 2:
            pts = np.array([locations[ids[k]] for k in cur], dtype=float)
            pieces.append(Piece(chain=chain, index=len(pieces), lonlat=pts,
                                node_ids=np.array([ids[k] for k in cur], dtype=np.int64),
                                seg_part=seg_part[cur[0]:cur[-1]],
                                start_cut=start_cut, end_cut=end_cut))

    start_cut = False
    for k, nid in enumerate(ids):
        if nid in locations:
            cur.append(k)
        else:
            flush(end_cut=True, start_cut=start_cut)
            cur = []
            start_cut = True
    flush(end_cut=False, start_cut=start_cut)
    return pieces


def _clip_segment(p0, p1, bbox):
    """Liang-Barsky. Gibt (t0, t1) des sichtbaren Teils zurück oder None."""
    minx, miny, maxx, maxy = bbox
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    t0, t1 = 0.0, 1.0
    for p, q in ((-dx, p0[0] - minx), (dx, maxx - p0[0]), (-dy, p0[1] - miny), (dy, maxy - p0[1])):
        if p == 0:
            if q < 0:
                return None
            continue
        r = q / p
        if p < 0:
            if r > t1:
                return None
            t0 = max(t0, r)
        else:
            if r < t0:
                return None
            t1 = min(t1, r)
    return (t0, t1)


def clip_piece(piece: Piece, bbox: tuple[float, float, float, float]) -> list[Piece]:
    """Schneidet ein Stück auf eine lon/lat-Box zu (Schnittpunkte werden berechnet)."""
    pts, ids, segp = piece.lonlat, piece.node_ids, piece.seg_part
    out: list[Piece] = []
    cur_pts: list = []
    cur_ids: list = []
    cur_seg: list = []
    cur_start_cut = piece.start_cut

    def flush(end_cut: bool):
        nonlocal cur_pts, cur_ids, cur_seg
        if len(cur_pts) >= 2:
            out.append(Piece(chain=piece.chain, index=-1, lonlat=np.array(cur_pts, float),
                             node_ids=np.array(cur_ids, np.int64), seg_part=np.array(cur_seg, np.int64),
                             start_cut=cur_start_cut, end_cut=end_cut))
        cur_pts, cur_ids, cur_seg = [], [], []

    n = len(pts)
    for k in range(n - 1):
        res = _clip_segment(pts[k], pts[k + 1], bbox)
        if res is None:
            if cur_pts:
                flush(end_cut=True)
                cur_start_cut = True
            continue
        t0, t1 = res
        a = pts[k] + t0 * (pts[k + 1] - pts[k])
        b = pts[k] + t1 * (pts[k + 1] - pts[k])
        if not cur_pts:
            if t0 > 0:
                cur_start_cut = True
            cur_pts.append(a)
            cur_ids.append(ids[k] if t0 == 0 else NO_NODE)
        cur_pts.append(b)
        cur_ids.append(ids[k + 1] if t1 == 1 else NO_NODE)
        cur_seg.append(segp[k])
        if t1 < 1:
            flush(end_cut=True)
            cur_start_cut = True
    if cur_pts:
        flush(end_cut=piece.end_cut)
    return out


# --------------------------------------------------------------------------- Verdichtung


@dataclass
class Dense:
    xy: np.ndarray          # (M, 2) relative Meter
    s: np.ndarray           # (M,) Bogenlänge
    seg_part: np.ndarray    # (M-1,)
    node_ids: np.ndarray    # (M,) OSM-ID an Originalscheiteln, sonst NO_NODE
    z: Optional[np.ndarray] = None        # Höhe in SPIELmetern
    xy_game: Optional[np.ndarray] = None  # xy / Maßstab
    s_game: Optional[np.ndarray] = None   # s / Maßstab

    def set_scale(self, scale_h: float) -> None:
        self.xy_game = self.xy / scale_h
        self.s_game = self.s / scale_h


def densify(xy: np.ndarray, node_ids: np.ndarray, seg_part: np.ndarray, spacing: float) -> Dense:
    out_xy = [xy[0]]
    out_ids = [node_ids[0]]
    out_seg = []
    for k in range(len(xy) - 1):
        a, b = xy[k], xy[k + 1]
        length = float(np.hypot(*(b - a)))
        n = max(1, int(math.ceil(length / spacing)))
        for j in range(1, n + 1):
            t = j / n
            out_xy.append(a + t * (b - a))
            out_ids.append(node_ids[k + 1] if j == n else NO_NODE)
            out_seg.append(seg_part[k])
    xy_d = np.array(out_xy, float)
    s = np.concatenate([[0.0], np.cumsum(np.hypot(*np.diff(xy_d, axis=0).T))])
    return Dense(xy=xy_d, s=s, seg_part=np.array(out_seg, np.int64), node_ids=np.array(out_ids, np.int64))


# --------------------------------------------------------------------------- Vereinfachung


def douglas_peucker(points: np.ndarray, tolerance: float, mandatory: np.ndarray) -> np.ndarray:
    """Iteratives Douglas-Peucker (beliebige Dimension). Pflichtpunkte bleiben."""
    n = len(points)
    keep = mandatory.copy()
    keep[0] = keep[-1] = True
    anchors = np.flatnonzero(keep)
    stack = list(zip(anchors[:-1].tolist(), anchors[1:].tolist()))
    while stack:
        a, b = stack.pop()
        if b - a < 2:
            continue
        seg = points[a + 1:b]
        A, B = points[a], points[b]
        AB = B - A
        L2 = float(AB @ AB)
        if L2 == 0.0:
            d = np.linalg.norm(seg - A, axis=1)
        else:
            t = np.clip(((seg - A) @ AB) / L2, 0.0, 1.0)
            d = np.linalg.norm(seg - (A + t[:, None] * AB), axis=1)
        k = int(np.argmax(d))
        if d[k] > tolerance:
            m = a + 1 + k
            keep[m] = True
            stack.append((a, m))
            stack.append((m, b))
    return keep


def enforce_min_spacing(pts: np.ndarray, candidates: list[int], prio: np.ndarray,
                        min_len: float, s: Optional[np.ndarray] = None) -> Optional[list[int]]:
    """Entfernt Punkte, die näher als min_len am Vorgänger liegen.

    Priorität: Endpunkt (2) > Laufgrenze (1) > normaler Punkt (0).
    Treffen zwei normale Punkte zu dicht aufeinander und ist die Bogenlänge `s`
    bekannt, ersetzt der verdichtete Punkt in ihrer Mitte beide – das schneidet
    Ecken weniger stark ab als das Weglassen eines der beiden.
    Gibt None zurück, wenn das ganze Stück kürzer als min_len ist.
    """
    last = candidates[-1]
    out = [candidates[0]]

    def dist(a, b):
        return float(np.linalg.norm(pts[a] - pts[b]))

    for i in candidates[1:]:
        while len(out) > 1 and dist(i, out[-1]) < min_len and (prio[out[-1]] < prio[i] or i == last):
            out.pop()
        if dist(i, out[-1]) >= min_len:
            out.append(i)
        elif i == last:
            return None
        elif s is not None and len(out) > 1 and prio[out[-1]] == 0 and prio[i] == 0:
            prev = out[-1]
            mid = int(np.argmin(np.abs(s[prev:i + 1] - (s[prev] + s[i]) / 2))) + prev
            if dist(mid, out[-2]) >= min_len:
                out[-1] = mid
    return out


def max_deviation(xy: np.ndarray, idx: list[int]) -> float:
    """Größte Lageabweichung der verdichteten Punkte von der vereinfachten Linie."""
    worst = 0.0
    for a, b in zip(idx, idx[1:]):
        if b - a < 2:
            continue
        A, B = xy[a], xy[b]
        AB = B - A
        L2 = float(AB @ AB)
        seg = xy[a + 1:b]
        if L2 == 0.0:
            d = np.linalg.norm(seg - A, axis=1)
        else:
            t = np.clip(((seg - A) @ AB) / L2, 0.0, 1.0)
            d = np.linalg.norm(seg - (A + t[:, None] * AB), axis=1)
        worst = max(worst, float(d.max()))
    return worst


def split_long_segments(pts: np.ndarray, s: np.ndarray, idx: list[int], max_len: float,
                        max_rounds: int = 20) -> list[int]:
    """Fügt Punkte der verdichteten Linie ein, bis kein Abschnitt länger als max_len ist.

    Die neuen Punkte liegen auf der verdichteten Linie (also auf der echten
    Geometrie). Weil sie dadurch nicht exakt gleichmäßig verteilt sind, wird
    wiederholt geprüft, bis alle Abschnitte passen.
    """
    out = list(idx)
    for _ in range(max_rounds):
        changed = False
        new = [out[0]]
        for a, b in zip(out, out[1:]):
            length = float(np.linalg.norm(pts[b] - pts[a]))
            if length > max_len and b - a >= 2:
                n = int(math.ceil(length / max_len))
                targets = s[a] + (s[b] - s[a]) * np.arange(1, n) / n
                prev = a
                for t in targets:
                    j = int(np.searchsorted(s, t))
                    if 0 < j < len(s) and abs(s[j - 1] - t) < abs(s[j] - t):
                        j -= 1  # nächstgelegener verdichteter Punkt
                    j = min(max(j, prev + 1), b - 1)
                    if j > prev:
                        new.append(j)
                        prev = j
                        changed = True
            new.append(b)
        out = new
        if not changed:
            break
    return out


@dataclass
class PieceResult:
    points: np.ndarray        # (K, 3) Spielkoordinaten x, y(Höhe), z
    runs: list[dict] = field(default_factory=list)
    xy_real: Optional[np.ndarray] = None   # (K, 2) relative Echtmeter, für GeoJSON
    split_count: int = 0
    deviation: float = 0.0                 # größte Lageabweichung (Spielmeter)


def finalize_piece(piece: Piece, profile, part_run: list[int], runs: list[dict]) -> Optional[PieceResult]:
    """Vereinfacht ein verdichtetes, mit Höhen versehenes Stück (Höhen bereits
    in Spielmetern) und gibt Spielkoordinaten zurück."""
    d = piece.dense
    m = len(d.xy)
    z = d.z if d.z is not None else np.zeros(m)
    run_of_seg = np.array([part_run[p] for p in d.seg_part], np.int64)

    prio = np.zeros(m, np.int64)
    prio[0] = prio[-1] = 2
    boundary = np.flatnonzero(run_of_seg[1:] != run_of_seg[:-1]) + 1
    prio[boundary] = np.maximum(prio[boundary], 1)

    # Alles ab hier in Spielmetern
    tol = max(profile.simplify_tolerance_game_m, 1e-6)
    zfac = tol / max(profile.vertical_tolerance_game_m, 1e-6)
    pts3 = np.column_stack([d.xy_game, z])
    scaled = np.column_stack([d.xy_game, z * zfac])
    keep = douglas_peucker(scaled, tol, prio > 0)

    idx = enforce_min_spacing(pts3, np.flatnonzero(keep).tolist(), prio, profile.min_segment_game_m,
                              d.s_game)
    if idx is None:
        # Ganzes Stück kürzer als die Wunsch-Mindestlänge: als ein einziges
        # Straßenstück behalten, solange das Spielminimum erreicht ist.
        if np.linalg.norm(pts3[-1] - pts3[0]) >= GAME_MIN_SEGMENT_M and m >= 2:
            idx = [0, m - 1]
        else:
            return None
    before = len(idx)
    idx = split_long_segments(pts3, d.s_game, idx, profile.max_segment_game_m)
    split_count = len(idx) - before

    # Lauf je Ergebnis-Abschnitt: Lauf des mittleren verdichteten Abschnitts
    seg_runs = [int(run_of_seg[(a + b - 1) // 2]) for a, b in zip(idx, idx[1:])]
    out_runs = []
    start = 0
    for k in range(1, len(seg_runs) + 1):
        if k == len(seg_runs) or seg_runs[k] != seg_runs[start]:
            r = dict(runs[seg_runs[start]])
            r["from"], r["to"] = start, k
            out_runs.append(r)
            start = k

    sel = pts3[idx]
    # Spielachsen: x = Osten, y = Höhe, z = Süden
    game = np.column_stack([sel[:, 0], sel[:, 2], -sel[:, 1]])
    return PieceResult(points=game, runs=out_runs, xy_real=sel[:, :2] * profile.scale_h,
                       split_count=split_count, deviation=max_deviation(d.xy_game, idx))


def build_runs(chain: Chain) -> tuple[list[int], list[dict]]:
    """Fasst Teile gleicher Attribute zu Läufen zusammen.

    Rückgabe: Lauf-Index je Kettenteil und die Lauf-Attribute.
    """
    part_run: list[int] = []
    runs: list[dict] = []
    keys: list[tuple] = []
    for part in chain.parts:
        key = run_key(part, part.edge.role)
        if keys and keys[-1] == key:
            runs[-1]["osm_ways"].append(part.edge.way.id)
        else:
            keys.append(key)
            r = dict(zip(RUN_FIELDS, key))
            r["osm_ways"] = [part.edge.way.id]
            runs.append(r)
        part_run.append(len(runs) - 1)
    for r in runs:
        r["osm_ways"] = sorted(set(r["osm_ways"]))
    return part_run, runs
